"""One explicit provider action. No process runs during passive monitoring."""
import hashlib
import json
import os
import subprocess
import threading
import time
import uuid
from pathlib import Path


class ActionError(ValueError):
    def __init__(self, code):
        self.code = code
        super().__init__(code)


# Verified native signed OpenAI installation and queue --help on 2026-09-12.
# An update disables this capability until the new binary is explicitly verified.
VERIFIED_SHA256 = "081e4de4be8e38fac6ed4d95e3b1a0b9f6d31c090ddc36e1696b349fe406f575"


class NativeBridge:
    def __init__(self, executable=None, expected_hash=VERIFIED_SHA256, run=subprocess.run):
        self.executable = Path(executable) if executable else Path(os.environ.get("LOCALAPPDATA", "")) / "OpenAI/Codex/bin/bffc5354119c8421/codex.exe"
        self.expected_hash = expected_hash
        self.run = run
        self.lock = threading.Lock()
        self.requests = {}
        self.inflight = set()
        self.accepted = []

    def capability(self):
        try:
            path = self.executable
            if not path.is_absolute() or path.suffix.lower() != ".exe" or path.is_symlink():
                raise ValueError()
            with path.open("rb") as stream:
                if stream.read(2) != b"MZ":
                    raise ValueError()
                stream.seek(0)
                actual = hashlib.file_digest(stream, "sha256").hexdigest()
            available = actual == self.expected_hash
        except (OSError, ValueError):
            available = False
        return {"available": available, "reason": "Verified native Codex queue" if available else
                "A verified native Codex executable is unavailable or has changed."}

    def send(self, payload, store):
        if not isinstance(payload, dict) or set(payload) != {"action", "thread_id", "message", "request_id", "confirmed"}:
            raise ActionError("invalid_request")
        if payload["action"] != "send_followup" or payload["confirmed"] is not True:
            raise ActionError("confirmation_required")
        for key in ("thread_id", "request_id"):
            try:
                if str(uuid.UUID(payload[key])) != payload[key]:
                    raise ValueError()
            except (ValueError, TypeError, AttributeError):
                raise ActionError("invalid_request") from None
        message = payload["message"]
        if not isinstance(message, str) or not message.strip() or len(message) > 4000 or "\0" in message:
            raise ActionError("invalid_request")
        try:
            message.encode("utf-8")
        except UnicodeEncodeError:
            raise ActionError("invalid_request") from None
        request_id, thread_id = payload["request_id"], payload["thread_id"]
        digest = hashlib.sha256(json.dumps(payload, sort_keys=True).encode()).hexdigest()
        now = time.monotonic()
        with self.lock:
            self.requests = {key: value for key, value in self.requests.items()
                             if now - value[0] < 600 or value[2].get("status") == "pending"}
            previous = self.requests.get(request_id)
            if previous:
                if previous[1] != digest:
                    raise ActionError("request_conflict")
                return dict(previous[2])
        state = store.snapshot()
        if state.get("adapter") != "codex":
            raise ActionError("unsupported")
        if not any(row.get("id") == thread_id for row in state.get("sessions", [])):
            raise ActionError("stale_session")
        if not self.capability()["available"]:
            raise ActionError("unsupported")
        with self.lock:
            # Recheck after the snapshot/hash work: reservation must be atomic.
            previous = self.requests.get(request_id)
            if previous:
                if previous[1] != digest:
                    raise ActionError("request_conflict")
                return dict(previous[2])
            self.accepted = [stamp for stamp in self.accepted if now - stamp < 60]
            if thread_id in self.inflight:
                raise ActionError("thread_busy")
            if len(self.accepted) >= 5:
                raise ActionError("rate_limited")
            result = {"ok": False, "status": "pending", "request_id": request_id, "thread_id": thread_id}
            self.requests[request_id] = (now, digest, result)
            self.accepted.append(now)
            self.inflight.add(thread_id)
        try:
            environment = os.environ.copy()
            if state.get("source"):
                environment["CODEX_HOME"] = state["source"]
            command = self.run([str(self.executable), "queue", "--thread=" + thread_id, "--message=" + message],
                               shell=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                               stdin=subprocess.DEVNULL, timeout=15, check=False,
                               env=environment,
                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            result = {**result, "ok": command.returncode == 0, "status": "finished",
                      "code": "queued" if command.returncode == 0 else "provider_failed"}
        except subprocess.TimeoutExpired:
            result = {**result, "status": "finished", "code": "provider_timeout", "uncertain": True}
        except OSError:
            result = {**result, "status": "finished", "code": "provider_failed"}
        finally:
            with self.lock:
                self.requests[request_id] = (now, digest, result)
                self.inflight.discard(thread_id)
        return dict(result)

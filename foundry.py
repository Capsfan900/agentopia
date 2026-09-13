"""Explicit local configuration/library actions; the live adapter stays separate."""
import base64
import hashlib
import json
import os
import tempfile
import threading
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from actions import ActionError, NativeBridge
from library import Library


CONFIG_KEYS = {"adapter", "codex_home", "state_file", "host", "port", "open", "verbose"}


def default_data_dir():
    return Path(os.environ.get("LOCALAPPDATA") or Path.home() / ".local/share") / "AgentFoundry"


def load_config(data_dir):
    path = Path(data_dir) / "settings.json"
    if not path.exists():
        return {}
    if path.stat().st_size > 16384 or path.is_symlink():
        raise ValueError("Invalid settings file")
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict) or value.get("schema_version") != 1 or set(value) != {"schema_version", "config"}:
        raise ValueError("Unsupported settings format")
    config = value["config"]
    if not isinstance(config, dict) or set(config) != CONFIG_KEYS:
        raise ValueError("Invalid settings fields")
    return config


def config_revision(config):
    return hashlib.sha256(json.dumps(config, sort_keys=True).encode()).hexdigest()


class FoundryApp:
    def __init__(self, store, config, data_dir):
        self.store = store
        self.config = dict(config)
        self.data_dir = Path(data_dir).resolve()
        self.library = Library(self.data_dir / "library")
        self.bridge = NativeBridge()
        self.lock = threading.RLock()
        self.runtime_port = config["port"]

    def snapshot(self):
        with self.lock:
            return self.store.snapshot()

    def signature(self):
        with self.lock:
            return (config_revision(self.config), self.store.signature())

    def bootstrap(self):
        bridge = self.bridge.capability() if self.config["adapter"] == "codex" else {"available": False, "reason": "JSON sources are read-only."}
        return {"configuration": dict(self.config), "settings_revision": config_revision(self.config),
                "data_dir": str(self.data_dir), "runtime_port": self.runtime_port,
                "capabilities": [
                    {"adapter": "codex", "label": "Codex", "supported": True,
                     "detected": bool(self.config["codex_home"]) and Path(self.config["codex_home"]).is_dir(),
                     "selected": self.config["adapter"] == "codex", "session_discovery": True,
                     "semantic_activity": True, "metrics": True,
                     "send_followup": self.config["adapter"] == "codex" and bridge["available"],
                     "reason": bridge["reason"] if self.config["adapter"] == "codex" else "Select the Codex data source to use this adapter.",
                     "future_controls": False},
                    {"adapter": "json", "label": "Generic JSON", "supported": True,
                     "detected": bool(self.config.get("state_file")) and Path(self.config["state_file"]).is_file(),
                     "selected": self.config["adapter"] == "json", "session_discovery": False,
                     "semantic_activity": "supplied or labeled fallback", "metrics": "if supplied",
                     "send_followup": False, "future_controls": False,
                     "reason": "Any runner can supply the documented JSON contract. No native provider discovery is installed."},
                    *[{"adapter": key, "label": label, "supported": False, "detected": "Not checked",
                       "selected": False, "session_discovery": False, "semantic_activity": False,
                       "metrics": False, "send_followup": False, "future_controls": False,
                       "reason": f"{label} is unavailable because no adapter is installed. Detection and sign-in are not attempted."}
                      for key, label in (("claude_code", "Claude Code"), ("pi", "Pi Agent"), ("hermes", "Hermes"))]]}

    @staticmethod
    def fields(payload, required, optional=()):
        if not isinstance(payload, dict) or not set(required) <= set(payload) or set(payload) - set(required) - set(optional):
            raise ActionError("invalid_request")

    def settings(self, payload):
        self.fields(payload, {"config", "expected_revision"})
        config = payload["config"]
        if not isinstance(config, dict) or set(config) != CONFIG_KEYS:
            raise ActionError("invalid_request")
        if (config["adapter"] not in ("codex", "json") or config["host"] != "127.0.0.1" or
                type(config["port"]) is not int or not 1024 <= config["port"] <= 65535 or
                type(config["open"]) is not bool or type(config["verbose"]) is not bool or
                not all(isinstance(config[key], str) and len(config[key]) < 2048 for key in ("codex_home", "state_file"))):
            raise ActionError("invalid_request")
        from monitor import CodexStore, JsonStore
        if config["adapter"] == "codex":
            path = Path(config["codex_home"])
            if not path.is_absolute() or not path.is_dir():
                raise ActionError("source_unavailable")
            store = CodexStore(path.resolve())
        else:
            path = Path(config["state_file"])
            if not path.is_absolute() or not path.is_file():
                raise ActionError("source_unavailable")
            store = JsonStore(path.resolve())
            store.snapshot()
        with self.lock:
            if payload["expected_revision"] != config_revision(self.config):
                raise ActionError("stale_revision")
            self.data_dir.mkdir(parents=True, exist_ok=True)
            target = self.data_dir / "settings.json"
            if target.is_symlink():
                raise ActionError("unsafe_path")
            temp_name = None
            try:
                with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=self.data_dir, suffix=".tmp", delete=False) as temp:
                    temp_name = temp.name
                    json.dump({"schema_version": 1, "config": config}, temp, indent=2)
                    temp.flush(); os.fsync(temp.fileno())
                os.replace(temp_name, target)
            finally:
                if temp_name and Path(temp_name).exists():
                    Path(temp_name).unlink()
            self.config, self.store = dict(config), store
        return self.bootstrap()

    def read_library(self, url):
        parsed = urlparse(url)
        if parsed.path == "/api/library":
            query = parse_qs(parsed.query)
            archived = query.get("archived", [None])[0]
            return self.library.list_items(kind=query.get("kind", [None])[0],
                                           archived=None if archived is None else archived == "true",
                                           query=query.get("q", [""])[0])
        item_id = parsed.path.removeprefix("/api/library/")
        return self.library.get_item(item_id)

    def dispatch(self, route, payload):
        if route == "/api/settings":
            return self.settings(payload)
        if route == "/api/actions":
            return self.bridge.send(payload, self)
        if route == "/api/library/refresh":
            self.fields(payload, set())
            self.library.refresh()
            return {"ok": True}
        if route == "/api/library/work":
            self.fields(payload, {"thread_id"}, {"title", "context", "include_details"})
            snapshot = self.snapshot()
            rows = snapshot.get("sessions", [])
            by_id = {row["id"]: row for row in rows}
            root = by_id.get(payload["thread_id"])
            if root is None:
                raise ActionError("stale_session")
            seen = set()
            while root.get("parent") in by_id and root["id"] not in seen:
                seen.add(root["id"]); root = by_id[root["parent"]]
            selected = {root["id"]}
            for _ in range(len(rows)):
                additions = {row["id"] for row in rows if row.get("parent") in selected} - selected
                if not additions:
                    break
                selected.update(additions)
            snapshot["sessions"] = [row for row in rows if row["id"] in selected]
            snapshot["selected_session_id"] = payload["thread_id"]
            return self.library.save_work(snapshot, payload.get("title") or Path(root.get("cwd") or "Session").name,
                                          payload.get("context", ""), payload.get("include_details", False))
        if route == "/api/library/artifacts":
            self.fields(payload, {"kind", "manifest"}, {"id", "expected_revision"})
            return self.library.save_artifact(**payload)
        if route == "/api/library/import":
            self.fields(payload, {"bundle"})
            if not isinstance(payload["bundle"], str):
                raise ActionError("invalid_request")
            return self.library.import_bundle(base64.b64decode(payload["bundle"], validate=True))
        parts = route.strip("/").split("/")
        if len(parts) != 4 or parts[:2] != ["api", "library"]:
            raise ActionError("unsupported")
        item_id, action = parts[2:]
        if action == "state":
            self.fields(payload, {"expected_revision"}, {"archived", "verdict", "note", "completed"})
            return self.library.set_work_state(item_id, **payload)
        if action == "validate":
            self.fields(payload, {"expected_revision"})
            return self.library.validate_candidate(item_id, **payload)
        if action == "classification":
            self.fields(payload, {"expected_revision", "classification"})
            return self.library.set_classification(item_id, **payload)
        if action == "promote":
            self.fields(payload, {"expected_revision", "validation_id", "confirmed"})
            if payload["confirmed"] is not True:
                raise ActionError("confirmation_required")
            payload = {key: value for key, value in payload.items() if key != "confirmed"}
            return self.library.promote_candidate(item_id, **payload)
        if action in ("preflight", "export"):
            self.fields(payload, {"expected_revision", "mode"}, {"plan_hash", "acknowledge_warnings", "confirm_vendor"})
            if action == "preflight":
                return self.library.preflight_bundle(item_id, payload["expected_revision"], payload["mode"])
            return self.library.export_bundle(item_id, **payload)
        raise ActionError("unsupported")

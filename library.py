"""Durable, local-only Agentopia work and artifact library."""

from __future__ import annotations

import base64
import binascii
import copy
import datetime as dt
import hashlib
import io
import json
import math
import os
import platform
import re
import stat
import threading
import uuid
import zipfile
from pathlib import Path, PurePosixPath


SCHEMA_VERSION = 1
KINDS = {"work", "template", "component"}
HASH = re.compile(r"^[0-9a-f]{64}$")
MAX_UPLOAD = 8 * 1024 * 1024
MAX_MEMBER = 2 * 1024 * 1024
MAX_TOTAL = 16 * 1024 * 1024
MAX_ENTRIES = 64
MAX_STRING = 20_000
MAX_ASSET = 1024 * 1024


class DomainError(ValueError):
    """A safe, stable error intended for an API boundary."""

    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code
        self.message = message


def _error(code: str, message: str):
    raise DomainError(code, message)


def _now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def _json_bytes(value) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _uuid(value, field="id") -> str:
    if not isinstance(value, str):
        _error("invalid_input", f"{field} must be a UUID string")
    try:
        parsed = uuid.UUID(value)
    except (ValueError, AttributeError):
        _error("invalid_input", f"{field} must be a UUID string")
    if str(parsed) != value.lower():
        _error("invalid_input", f"{field} must use canonical UUID form")
    return str(parsed)


def _hash(value, field="revision") -> str:
    if not isinstance(value, str) or not HASH.fullmatch(value):
        _error("invalid_input", f"{field} must be a SHA-256 digest")
    return value


def _string(value, field, *, limit=MAX_STRING, empty=False) -> str:
    if not isinstance(value, str) or (not empty and not value.strip()) or len(value) > limit:
        _error("invalid_input", f"{field} must be a bounded string")
    return value


def _exact(mapping, required, optional=(), field="value"):
    if not isinstance(mapping, dict):
        _error("invalid_input", f"{field} must be an object")
    keys, required, allowed = set(mapping), set(required), set(required) | set(optional)
    if keys - allowed or required - keys:
        _error("invalid_input", f"{field} has missing or unsupported fields")


def _strings(value, field, *, limit=256):
    if not isinstance(value, list) or len(value) > limit:
        _error("invalid_input", f"{field} must be a bounded list")
    for index, item in enumerate(value):
        _string(item, f"{field}[{index}]", limit=2000)


def _bounded(value, *, depth=0):
    if depth > 12:
        _error("invalid_input", "input is nested too deeply")
    if isinstance(value, str):
        if len(value) > MAX_STRING:
            _error("invalid_input", "input contains an oversized string")
    elif isinstance(value, list):
        if len(value) > 256:
            _error("invalid_input", "input contains an oversized list")
        for item in value:
            _bounded(item, depth=depth + 1)
    elif isinstance(value, dict):
        if len(value) > 256:
            _error("invalid_input", "input contains an oversized object")
        for key, item in value.items():
            if not isinstance(key, str) or len(key) > 200:
                _error("invalid_input", "input contains an invalid field name")
            if key == "content_base64" and isinstance(item, str) and len(item) <= 2 * MAX_ASSET:
                continue
            _bounded(item, depth=depth + 1)
    elif isinstance(value, float) and not math.isfinite(value):
        _error("invalid_input", "input contains a non-finite number")
    elif value is not None and not isinstance(value, (bool, int, float)):
        _error("invalid_input", "input contains an unsupported value")


def _strict_json(raw, code):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("duplicate key")
            result[key] = value
        return result

    def number(value):
        parsed = float(value)
        if not math.isfinite(parsed):
            raise ValueError("non-finite number")
        return parsed

    try:
        value = json.loads(raw, object_pairs_hook=pairs, parse_float=number,
                           parse_constant=lambda _: (_ for _ in ()).throw(ValueError("non-finite number")))
        _bounded(value)
        return value
    except (UnicodeError, json.JSONDecodeError, ValueError, DomainError, RecursionError):
        _error(code, "JSON is malformed or exceeds safe limits")


_ASSIGNMENT_SECRET = re.compile(
    r"(?i)\b(password|passwd|api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|client[-_ ]?secret|secret|authorization)\b(\s*[:=]\s*)(['\"]?)[^\s,'\"}]+\3"
)
_BEARER = re.compile(r"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}")
_OPENAI_KEY = re.compile(r"\bsk-[A-Za-z0-9_-]{12,}\b")
_AWS_KEY = re.compile(r"\bAKIA[0-9A-Z]{16}\b")
_PRIVATE_KEY = re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----")


def _redact(value: str) -> str:
    value = _ASSIGNMENT_SECRET.sub(lambda m: f"{m.group(1)}{m.group(2)}[redacted]", value)
    value = _BEARER.sub("Bearer [redacted]", value)
    value = _OPENAI_KEY.sub("[redacted]", value)
    value = _AWS_KEY.sub("[redacted]", value)
    return _PRIVATE_KEY.sub("[redacted private key]", value)


def _secret_present(value) -> bool:
    if isinstance(value, str):
        return _redact(value) != value
    if isinstance(value, list):
        return any(_secret_present(item) for item in value)
    if isinstance(value, dict):
        for key, item in value.items():
            normalized = key.lower().replace("-", "_")
            if normalized in {"password", "passwd", "secret", "api_key", "access_token", "refresh_token",
                              "client_secret", "private_key", "credential", "authorization"}:
                if item not in (None, "", [], {}):
                    return True
            if _secret_present(item):
                return True
    return False


def _safe_scalar(value):
    if value is None or isinstance(value, (bool, int, float)):
        return value
    return _redact(_string(value, "snapshot value", limit=MAX_STRING, empty=True))


_SNAPSHOT_SCALARS = {
    "id", "parent", "depth", "kind", "agent_path", "nickname", "name", "title", "task", "cwd",
    "model", "effort", "harness", "provider", "role", "status", "turn_status", "progress", "eta_seconds", "estimate_basis",
    "updated", "age_seconds", "stale", "approval_pending", "turn_elapsed_seconds", "current_action",
    "context_window", "last_event", "last_tool_status", "collaboration", "adapter", "generated", "source",
    "selected_session_id", "root_id",
}
_SEMANTIC_FIELDS = {
    "id", "parent_id", "session_id", "kind", "summary", "source", "timestamp", "status", "observation_id",
    "turn_id", "tool", "task_path", "version", "root_id", "end_timestamp", "detail", "text", "time",
    "observations", "truncated", "input_tokens", "cached_input_tokens", "cache_write_input_tokens",
    "output_tokens", "reasoning_output_tokens", "total_tokens", "model_context_window", "plan_type",
    "primary", "secondary", "used_percent", "window_minutes", "resets_at", "limit_name",
    "files", "exit_code", "call_id", "ordinal", "scope", "observations", "sessions",
}


def _sanitize_semantic(value, include_details, depth=0):
    if depth > 8:
        return None
    if isinstance(value, list):
        return [_sanitize_semantic(item, include_details, depth + 1) for item in value[:256]]
    if not isinstance(value, dict):
        return _safe_scalar(value)
    result = {}
    for key, item in value.items():
        if key not in _SEMANTIC_FIELDS or (key in {"detail", "text"} and not include_details):
            continue
        if isinstance(item, (dict, list)):
            result[key] = _sanitize_semantic(item, include_details, depth + 1)
        else:
            result[key] = _safe_scalar(item)
    return result


def _sanitize_snapshot(snapshot, include_details):
    if not isinstance(snapshot, dict):
        _error("invalid_input", "snapshot must be an object")
    _bounded(snapshot)
    result = {key: _safe_scalar(snapshot[key]) for key in _SNAPSHOT_SCALARS if key in snapshot}
    if "sessions" in snapshot:
        sessions = snapshot["sessions"]
        if not isinstance(sessions, list) or len(sessions) > 128:
            _error("invalid_input", "snapshot sessions must be a bounded list")
        result["sessions"] = [_sanitize_snapshot(session, include_details) for session in sessions]
    for key in ("prompt_context", "working_on", "work_breakdown", "trace"):
        if key in snapshot:
            result[key] = _sanitize_semantic(snapshot[key], include_details)
    for key in ("token_usage", "last_token_usage", "rate_limits", "collaboration"):
        if key in snapshot and isinstance(snapshot[key], (dict, list)):
            result[key] = _sanitize_semantic(snapshot[key], False)
    if include_details and "recent_activity" in snapshot:
        result["recent_activity"] = _sanitize_semantic(snapshot["recent_activity"], True)
    return result


def _completion(snapshot):
    sessions = snapshot.get("sessions") if isinstance(snapshot, dict) else None
    statuses = [item.get("status", "") for item in sessions] if sessions is not None else [snapshot.get("status", "")]
    terminal = {"completed", "failed", "interrupted"}
    return {"completed": bool(statuses) and all(status in terminal for status in statuses),
            "source": "runtime", "statuses": statuses}


class Library:
    """One-process, explicitly refreshed library backed by ordinary JSON files."""

    def __init__(self, root):
        if not isinstance(root, (str, os.PathLike)):
            _error("invalid_input", "root must be a filesystem path")
        self.root = Path(root).absolute()
        self.catalog = self.root / "catalog"
        self.objects = self.root / "objects"
        self._lock = threading.RLock()
        self._index = {}
        self._warnings = []
        self._generation = 0
        self._saved_handoffs = None
        self._load_index()

    @staticmethod
    def _is_reparse(path: Path) -> bool:
        try:
            info = path.lstat()
        except FileNotFoundError:
            return False
        return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0) & 0x400)

    def _guard(self, path: Path):
        try:
            path.absolute().relative_to(self.root)
        except ValueError:
            _error("unsafe_path", "library path escaped its root")
        current = self.root
        if self._is_reparse(current):
            _error("unsafe_path", "library root cannot be a link or reparse point")
        try:
            relative = path.absolute().relative_to(self.root)
        except ValueError:
            _error("unsafe_path", "library path escaped its root")
        for part in relative.parts:
            current /= part
            if current.exists() and self._is_reparse(current):
                _error("unsafe_path", "library paths cannot contain links or reparse points")

    def _prepare_storage(self):
        if self.root.exists() and self._is_reparse(self.root):
            _error("unsafe_path", "library root cannot be a link or reparse point")
        self.root.mkdir(parents=True, exist_ok=True)
        for folder in (self.catalog, self.objects):
            self._guard(folder)
            folder.mkdir(exist_ok=True)
            self._guard(folder)

    def _atomic_write(self, path: Path, content: bytes, *, replace: bool):
        self._guard(path.parent)
        self._guard(path)
        if not replace and path.exists():
            if path.read_bytes() != content:
                _error("corrupt_data", "immutable object hash collision")
            return
        temporary = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
        self._guard(temporary)
        try:
            with temporary.open("xb") as stream:
                stream.write(content)
                stream.flush()
                os.fsync(stream.fileno())
            self._guard(path)
            os.replace(temporary, path)
            try:
                descriptor = os.open(path.parent, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
                try:
                    os.fsync(descriptor)
                finally:
                    os.close(descriptor)
            except OSError:
                pass  # ponytail: Windows lacks portable directory fsync; add native flush only if durability tests require it.
        finally:
            try:
                temporary.unlink()
            except FileNotFoundError:
                pass

    def _head_path(self, item_id):
        return self.catalog / f"{_uuid(item_id)}.json"

    def _object_path(self, revision):
        return self.objects / f"{_hash(revision)}.json"

    def _validate_head(self, value):
        _exact(value, {"schema_version", "id", "kind", "title", "revision", "archived", "updated_at"},
               {"approved_revision", "classification"}, "catalog head")
        if value["schema_version"] != SCHEMA_VERSION:
            _error("unsupported_schema", "unsupported catalog schema")
        _uuid(value["id"])
        if value["kind"] not in KINDS:
            _error("corrupt_data", "catalog contains an unsupported kind")
        _string(value["title"], "title", limit=200)
        _hash(value["revision"])
        if "approved_revision" in value:
            _hash(value["approved_revision"], "approved_revision")
        if "classification" in value:
            self._validate_classification(value["classification"])
            if value["classification"]["revision"] != value["revision"]:
                _error("corrupt_data", "catalog classification revision is stale")
        if not isinstance(value["archived"], bool):
            _error("corrupt_data", "catalog archived state is invalid")
        _string(value["updated_at"], "updated_at", limit=80)
        return value

    def _load_index(self):
        index, warnings = {}, []
        if not self.root.exists():
            self._index, self._warnings = index, warnings
            self._invalidate_projection()
            return
        self._guard(self.root)
        if not self.catalog.exists():
            self._index, self._warnings = index, warnings
            self._invalidate_projection()
            return
        self._guard(self.catalog)
        for path in self.catalog.iterdir():
            safe_name = re.sub(r"[^A-Za-z0-9_.-]", "?", path.name)[:100]
            try:
                self._guard(path)
                if not path.is_file() or path.suffix != ".json" or path.stat().st_size > MAX_MEMBER:
                    raise ValueError
                value = self._validate_head(_strict_json(path.read_bytes(), "corrupt_data"))
                if path.name != f'{value["id"]}.json':
                    raise ValueError
                self._check_classification_validation(value, value.get("classification"))
                index[value["id"]] = value
            except (OSError, UnicodeError, json.JSONDecodeError, DomainError, ValueError):
                warnings.append(f"Ignored malformed catalog entry {safe_name}.")
        self._index, self._warnings = index, warnings
        self._invalidate_projection()

    def _invalidate_projection(self):
        self._saved_handoffs = None
        self._generation += 1

    @property
    def generation(self):
        with self._lock:
            return self._generation

    def refresh(self):
        with self._lock:
            self._load_index()
            return self.list_items()

    def list_items(self, kind=None, archived=None, query=""):
        if kind is not None and kind not in KINDS:
            _error("invalid_input", "kind is unsupported")
        if archived is not None and not isinstance(archived, bool):
            _error("invalid_input", "archived must be true, false, or omitted")
        query = _string(query, "query", limit=200, empty=True).casefold()
        with self._lock:
            items = [copy.deepcopy(item) for item in self._index.values()
                     if (kind is None or item["kind"] == kind)
                     and (archived is None or item["archived"] == archived)
                     and (not query or query in f'{item["title"]} {item["id"]} {item["kind"]}'.casefold())]
            items.sort(key=lambda item: (item["updated_at"], item["title"], item["id"]), reverse=True)
            return {"items": items, "warnings": list(self._warnings)}

    def _head(self, item_id):
        item_id = _uuid(item_id)
        try:
            return copy.deepcopy(self._index[item_id])
        except KeyError:
            _error("not_found", "library item was not found")

    def _validate_object(self, value):
        _exact(value, {"schema_version", "id", "kind", "parent_revision", "created_at", "action", "payload"},
               field="library object")
        if value["schema_version"] != SCHEMA_VERSION:
            _error("unsupported_schema", "unsupported object schema")
        _uuid(value["id"])
        if value["kind"] not in KINDS | {"validation"}:
            _error("corrupt_data", "object contains an unsupported kind")
        if value["parent_revision"] is not None:
            _hash(value["parent_revision"], "parent_revision")
        _string(value["created_at"], "created_at", limit=80)
        _string(value["action"], "action", limit=80)
        _bounded(value["payload"])
        return value

    def _read_object(self, revision):
        path = self._object_path(revision)
        self._guard(path)
        try:
            if path.stat().st_size > MAX_MEMBER:
                _error("corrupt_data", "library object is oversized")
            raw = path.read_bytes()
        except FileNotFoundError:
            _error("corrupt_data", "library object is missing")
        if hashlib.sha256(raw).hexdigest() != revision:
            _error("corrupt_data", "library object checksum failed")
        try:
            value = _strict_json(raw, "corrupt_data")
        except DomainError:
            raise
        value = self._validate_object(value)
        try:
            if value["kind"] in {"template", "component"}:
                self._validate_manifest(value["kind"], value["payload"])
            elif value["kind"] == "work":
                self._validate_work_payload(value["payload"])
            else:
                report = value["payload"]
                _exact(report, {"subject_id", "subject_revision", "scope", "checks", "errors", "warnings", "result"},
                       field="validation report")
                _uuid(report["subject_id"], "validation.subject_id")
                _hash(report["subject_revision"], "validation.subject_revision")
                if report["scope"] != "structural" or report["result"] not in {"valid", "invalid"}:
                    _error("invalid_input", "validation report is invalid")
                _strings(report["errors"], "validation.errors", limit=128)
                _strings(report["warnings"], "validation.warnings", limit=128)
                if not isinstance(report["checks"], list) or len(report["checks"]) > 128:
                    _error("invalid_input", "validation checks are invalid")
                for check in report["checks"]:
                    _exact(check, {"name", "status"}, field="validation check")
                    _string(check["name"], "validation check name", limit=1000)
                    if check["status"] not in {"passed", "failed", "unverified", "not_applicable"}:
                        _error("invalid_input", "validation check status is invalid")
        except DomainError:
            _error("corrupt_data", "library object payload is invalid")
        return value

    def _public(self, head, obj):
        if obj["id"] != head["id"] or obj["kind"] != head["kind"]:
            _error("corrupt_data", "catalog and object identity disagree")
        return {**copy.deepcopy(head), "parent_revision": obj["parent_revision"],
                "created_at": obj["created_at"], "action": obj["action"], "data": copy.deepcopy(obj["payload"])}

    def get_item(self, id):
        with self._lock:
            head = self._head(id)
            return self._public(head, self._read_object(head["revision"]))

    def saved_handoff(self, adapter, source, session_id):
        with self._lock:
            if not all(isinstance(value, str) and value for value in (adapter, source, session_id)):
                return {"state": "unavailable"}
            if self._saved_handoffs is None:
                entries, complete = {}, not self._warnings
                for head in self._index.values():
                    if head["kind"] != "work":
                        continue
                    try:
                        obj = self._read_object(head["revision"])
                        if obj["id"] != head["id"] or obj["kind"] != "work":
                            raise DomainError("corrupt_data", "catalog and object identity disagree")
                        snapshot = obj["payload"]["snapshot"]
                        identity = (snapshot.get("adapter"), snapshot.get("source"))
                        sessions = snapshot.get("sessions", [snapshot])
                        if (not all(isinstance(value, str) and value for value in identity)
                                or not isinstance(sessions, list) or not sessions):
                            raise DomainError("corrupt_data", "saved work identity is incomplete")
                        completion = obj["payload"]["completion"]
                        captured_at = snapshot.get("generated")
                        try:
                            valid_capture = (isinstance(captured_at, str) and bool(captured_at)
                                             and dt.datetime.fromisoformat(captured_at.replace("Z", "+00:00")).tzinfo is not None)
                        except ValueError:
                            valid_capture = False
                        if not valid_capture:
                            cursor, seen, captured_at = obj, set(), ""
                            for _ in range(MAX_ENTRIES):
                                parent = cursor["parent_revision"]
                                if parent is None:
                                    captured_at = cursor["created_at"]
                                    break
                                if parent in seen:
                                    break
                                seen.add(parent)
                                try:
                                    previous = self._read_object(parent)
                                except (DomainError, OSError):
                                    break
                                if previous["id"] != head["id"] or previous["kind"] != "work":
                                    break
                                cursor = previous
                        summary = {
                            "state": "saved", "item_id": head["id"], "revision": head["revision"],
                            "captured_at": captured_at, "archived": head["archived"],
                            "completion": {"completed": completion["completed"], "source": completion["source"],
                                           "trusted": completion["source"] != "imported"},
                        }
                        order = (captured_at or obj["created_at"], head["updated_at"], head["id"])
                        session_ids = []
                        for session in sessions:
                            if not isinstance(session, dict) or not isinstance(session.get("id"), str) or not session["id"]:
                                raise DomainError("corrupt_data", "saved work session identity is incomplete")
                            session_ids.append(session["id"])
                        for captured_id in session_ids:
                            key = (*identity, captured_id)
                            if key not in entries or order > entries[key][0]:
                                entries[key] = (order, summary)
                    except (DomainError, OSError):
                        complete = False
                self._saved_handoffs = ({key: value for key, (_, value) in entries.items()}, complete)
            entries, complete = self._saved_handoffs
            return copy.deepcopy(entries.get((adapter, source, session_id),
                                               {"state": "none" if complete else "unavailable"}))

    def _publish_object(self, item_id, kind, parent_revision, action, payload):
        self._prepare_storage()
        value = {"schema_version": SCHEMA_VERSION, "id": item_id, "kind": kind,
                 "parent_revision": parent_revision, "created_at": _now(), "action": action,
                 "payload": copy.deepcopy(payload)}
        raw = _json_bytes(value)
        if len(raw) > MAX_MEMBER:
            _error("too_large", "library object is too large")
        revision = hashlib.sha256(raw).hexdigest()
        self._atomic_write(self._object_path(revision), raw, replace=False)
        return revision, value

    def _publish_head(self, head):
        self._validate_head(head)
        self._atomic_write(self._head_path(head["id"]), _json_bytes(head), replace=True)
        self._index[head["id"]] = copy.deepcopy(head)
        self._invalidate_projection()

    def _check_revision(self, head, expected_revision):
        if _hash(expected_revision, "expected_revision") != head["revision"]:
            _error("conflict", "library item changed; refresh and retry")

    @staticmethod
    def _validate_classification(value):
        _bounded(value)
        _exact(value, {"revision", "display_title", "kind", "domain", "provider", "harness", "projects",
                       "sources", "maturity", "portability", "relevance", "reason", "evidence", "validation_id"},
               field="classification")
        _hash(value["revision"], "classification.revision")
        _string(value["display_title"], "classification.display_title", limit=200)
        for key in ("kind", "domain", "provider", "harness"):
            _string(value[key], "classification." + key, limit=120)
        _string(value["reason"], "classification.reason", limit=2000)
        for key in ("projects", "sources", "maturity", "evidence"):
            _strings(value[key], "classification." + key, limit=128)
            if not value[key]:
                _error("invalid_input", "classification." + key + " must not be empty")
        if value["portability"] not in {"bytes-retained", "reference-only", "composition"}:
            _error("invalid_input", "classification.portability is unsupported")
        if value["relevance"] not in {"direct", "support", "generic", "uncertain"}:
            _error("invalid_input", "classification.relevance is unsupported")
        if value["validation_id"] is not None:
            _hash(value["validation_id"], "classification.validation_id")
        if _secret_present(value):
            _error("invalid_input", "classification contains a possible secret value")

    def _check_classification_validation(self, head, classification):
        validation_id = classification and classification["validation_id"]
        if validation_id is None:
            return
        validation = self._read_object(validation_id)
        report = validation["payload"]
        if (validation["kind"] != "validation" or report.get("subject_id") != head["id"]
                or report.get("subject_revision") != head["revision"] or report.get("scope") != "structural"):
            _error("validation_failed", "classification validation does not match this artifact revision")

    def set_classification(self, id, expected_revision, classification):
        self._validate_classification(classification)
        with self._lock:
            head = self._head(id)
            if head["kind"] not in {"template", "component"}:
                _error("invalid_state", "only artifacts can be classified")
            self._check_revision(head, expected_revision)
            if classification["revision"] != head["revision"]:
                _error("conflict", "classification does not describe the current revision")
            self._check_classification_validation(head, classification)
            obj = self._read_object(head["revision"])
            if head.get("classification") == classification:
                return self._public(head, obj)
            head["classification"] = copy.deepcopy(classification)
            head["updated_at"] = _now()
            self._publish_head(head)
            return self._public(head, obj)

    def save_work(self, snapshot, title, context="", include_details=False):
        title = _string(title, "title", limit=200)
        context = _redact(_string(context, "context", limit=4000, empty=True))
        if not isinstance(include_details, bool):
            _error("invalid_input", "include_details must be boolean")
        sanitized = _sanitize_snapshot(snapshot, include_details)
        payload = {"context": context, "snapshot": sanitized, "completion": _completion(sanitized),
                   "capture": {"sanitizer_version": 1, "include_details": include_details,
                               "method": "Allowlisted fields and common secret-pattern redaction; not a secrecy guarantee",
                               "capture_environment": {"python": platform.python_version(), "platform": platform.system()},
                               "configuration_limitations": "Only source-recorded model/effort and telemetry captured; instruction/component versions and project dependencies are unrecorded"}}
        with self._lock:
            item_id = str(uuid.uuid4())
            revision, obj = self._publish_object(item_id, "work", None, "save_work", payload)
            head = {"schema_version": SCHEMA_VERSION, "id": item_id, "kind": "work", "title": title,
                    "revision": revision, "archived": False, "updated_at": _now()}
            self._publish_head(head)
            return self._public(head, obj)

    def set_work_state(self, id, expected_revision, archived=None, verdict=None, note="", completed=None):
        if archived is not None and not isinstance(archived, bool):
            _error("invalid_input", "archived must be boolean or omitted")
        if verdict is not None and verdict not in {"successful", "mixed", "failed"}:
            _error("invalid_input", "verdict is unsupported")
        if completed is not None and not isinstance(completed, bool):
            _error("invalid_input", "completed must be boolean or omitted")
        note = _redact(_string(note, "note", limit=4000, empty=True))
        if verdict is None and note:
            _error("invalid_input", "note requires a verdict")
        with self._lock:
            head = self._head(id)
            if head["kind"] != "work":
                _error("invalid_state", "only saved work has work state")
            self._check_revision(head, expected_revision)
            obj = self._read_object(head["revision"])
            payload = copy.deepcopy(obj["payload"])
            changed_payload = False
            if completed is not None:
                payload["completion"] = {"completed": completed, "source": "user",
                                         "statuses": payload.get("completion", {}).get("statuses", [])}
                if not completed:
                    payload.pop("review", None)
                changed_payload = True
            if verdict is not None:
                if not payload.get("completion", {}).get("completed"):
                    _error("invalid_state", "a verdict requires a completed work record")
                payload["review"] = {"verdict": verdict, "note": note, "recorded_at": _now()}
                changed_payload = True
            if archived is not None:
                changed_payload = True
            if changed_payload:
                action = ("archive" if archived else "unarchive") if completed is None and verdict is None else "set_work_state"
                revision, obj = self._publish_object(head["id"], "work", head["revision"], action, payload)
                head["revision"] = revision
            if archived is not None:
                head["archived"] = archived
            head["updated_at"] = _now()
            self._publish_head(head)
            return self._public(head, obj)

    @staticmethod
    def _validate_review(review):
        _exact(review, {"candidate_revision", "baseline", "verdict", "no_worse", "evidence", "note",
                        "unverified", "user_confirmed"}, field="review")
        _hash(review["candidate_revision"], "review.candidate_revision")
        _exact(review["baseline"], {"revision_or_evidence_hash", "scope"}, {"artifact_id"}, "review.baseline")
        _hash(review["baseline"]["revision_or_evidence_hash"], "review.baseline.revision_or_evidence_hash")
        if "artifact_id" in review["baseline"]:
            _uuid(review["baseline"]["artifact_id"], "review.baseline.artifact_id")
        _string(review["baseline"]["scope"], "review.baseline.scope", limit=1000)
        if review["verdict"] not in {"successful", "mixed", "failed"}:
            _error("invalid_input", "review verdict is unsupported")
        if not isinstance(review["no_worse"], bool) or not isinstance(review["user_confirmed"], bool):
            _error("invalid_input", "review decisions must be boolean")
        if not isinstance(review["evidence"], list) or not review["evidence"] or len(review["evidence"]) > 64:
            _error("invalid_input", "review evidence must be a non-empty bounded list")
        for index, evidence in enumerate(review["evidence"]):
            _exact(evidence, {"kind", "scope", "summary"}, {"ref"}, f"review.evidence[{index}]")
            if evidence["kind"] not in {"user-attested", "measured"}:
                _error("invalid_input", "review evidence kind is unsupported")
            for key in ("scope", "summary"):
                _string(evidence[key], f"review.evidence[{index}].{key}", limit=2000)
            if "ref" in evidence:
                _string(evidence["ref"], f"review.evidence[{index}].ref", limit=2000)
        _string(review["note"], "review.note", limit=4000)
        _strings(review["unverified"], "review.unverified", limit=64)

    @classmethod
    def _validate_template(cls, manifest):
        required = {"title", "purpose", "harness", "model", "instruction_layers", "components", "capabilities",
                    "tools", "permissions_policy", "quality_policy", "environment", "validation", "provenance", "resume",
                    "compatibility", "limitations"}
        _exact(manifest, required, {"review", "vendor_assets"}, "template manifest")
        _string(manifest["title"], "manifest.title", limit=200)
        _string(manifest["purpose"], "manifest.purpose", limit=4000)
        _exact(manifest["harness"], {"id", "version", "config_ref"}, field="manifest.harness")
        _exact(manifest["model"], {"name", "effort"}, field="manifest.model")
        for field, limit in (("id", 200), ("version", 200), ("config_ref", 2000)):
            _string(manifest["harness"][field], f"manifest.harness.{field}", limit=limit)
        for field in ("name", "effort"):
            _string(manifest["model"][field], f"manifest.model.{field}", limit=200)
        if not isinstance(manifest["instruction_layers"], list) or len(manifest["instruction_layers"]) > 64:
            _error("invalid_input", "instruction_layers must be a bounded list")
        for index, layer in enumerate(manifest["instruction_layers"]):
            _exact(layer, {"role", "ref", "rationale"}, field=f"instruction_layers[{index}]")
            for key in layer:
                _string(layer[key], f"instruction_layers[{index}].{key}", limit=2000)
        if not isinstance(manifest["components"], list) or len(manifest["components"]) > 128:
            _error("invalid_input", "components must be a bounded list")
        for index, component in enumerate(manifest["components"]):
            _exact(component, {"kind", "ref", "version", "sha256", "rationale"}, field=f"components[{index}]")
            for key in ("kind", "ref", "version", "rationale"):
                _string(component[key], f"components[{index}].{key}", limit=2000)
            if component["kind"] not in {"skill", "workflow", "runbook", "script", "instructions"}:
                _error("invalid_input", "component kind is unsupported")
            _hash(component["sha256"], f"components[{index}].sha256")
        _strings(manifest["capabilities"], "manifest.capabilities")
        _strings(manifest["tools"], "manifest.tools")
        _exact(manifest["permissions_policy"], {"ref", "description"}, field="manifest.permissions_policy")
        _string(manifest["permissions_policy"]["ref"], "permissions_policy.ref", limit=2000)
        _string(manifest["permissions_policy"]["description"], "permissions_policy.description", limit=4000)
        _exact(manifest["quality_policy"], {"procedures", "skill_mappings"}, field="manifest.quality_policy")
        _strings(manifest["quality_policy"]["procedures"], "quality_policy.procedures", limit=64)
        if not manifest["quality_policy"]["procedures"]:
            _error("invalid_input", "quality policy requires at least one tool-neutral procedure")
        mappings = manifest["quality_policy"]["skill_mappings"]
        if not isinstance(mappings, list) or len(mappings) > 64:
            _error("invalid_input", "quality skill mappings must be a bounded list")
        for index, mapping in enumerate(mappings):
            _exact(mapping, {"procedure", "skill_ref"}, field=f"quality_policy.skill_mappings[{index}]")
            _string(mapping["procedure"], f"quality_policy.skill_mappings[{index}].procedure", limit=200)
            _string(mapping["skill_ref"], f"quality_policy.skill_mappings[{index}].skill_ref", limit=2000)
            if mapping["procedure"] not in manifest["quality_policy"]["procedures"]:
                _error("invalid_input", "quality skill mapping must name a declared procedure")
        _exact(manifest["environment"], {"dependencies", "required_env_names"}, field="manifest.environment")
        dependencies = manifest["environment"]["dependencies"]
        if not isinstance(dependencies, list) or len(dependencies) > 128:
            _error("invalid_input", "dependencies must be a bounded list")
        for index, dependency in enumerate(dependencies):
            _exact(dependency, {"name", "version", "rationale"}, field=f"dependencies[{index}]")
            for key in dependency:
                _string(dependency[key], f"dependencies[{index}].{key}", limit=2000)
        _strings(manifest["environment"]["required_env_names"], "environment.required_env_names")
        for name in manifest["environment"]["required_env_names"]:
            if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,127}", name):
                _error("invalid_input", "environment names must not contain values")
        declarations = manifest["validation"]
        if not isinstance(declarations, list) or len(declarations) > 64:
            _error("invalid_input", "validation must be a bounded list")
        for index, declaration in enumerate(declarations):
            _exact(declaration, {"name"}, {"argv", "cwd_ref", "last_result_ref"}, f"validation[{index}]")
            _string(declaration["name"], f"validation[{index}].name", limit=1000)
            if "argv" in declaration:
                _strings(declaration["argv"], f"validation[{index}].argv", limit=64)
            for key in ("cwd_ref", "last_result_ref"):
                if key in declaration:
                    _string(declaration[key], f"validation[{index}].{key}", limit=2000)
        _exact(manifest["provenance"], {"source_work_id", "provider", "session_id", "captured_at"},
               field="manifest.provenance")
        for key in manifest["provenance"]:
            _string(manifest["provenance"][key], f"provenance.{key}", limit=2000)
        _exact(manifest["resume"], {"handoff", "next_steps", "open_questions"}, field="manifest.resume")
        _string(manifest["resume"]["handoff"], "resume.handoff", limit=4000)
        _strings(manifest["resume"]["next_steps"], "resume.next_steps", limit=64)
        _strings(manifest["resume"]["open_questions"], "resume.open_questions", limit=64)
        _strings(manifest["compatibility"], "manifest.compatibility", limit=128)
        _strings(manifest["limitations"], "manifest.limitations", limit=128)
        if "review" in manifest:
            cls._validate_review(manifest["review"])
        if "vendor_assets" in manifest:
            if not isinstance(manifest["vendor_assets"], list) or len(manifest["vendor_assets"]) > 32:
                _error("invalid_input", "vendor_assets must be a bounded list")
            for index, asset in enumerate(manifest["vendor_assets"]):
                _exact(asset, {"id", "revision"}, field=f"vendor_assets[{index}]")
                _uuid(asset["id"], f"vendor_assets[{index}].id")
                _hash(asset["revision"], f"vendor_assets[{index}].revision")

    @classmethod
    def _validate_component(cls, manifest):
        _exact(manifest, {"title", "purpose", "component_type", "source_ref", "version", "sha256", "rationale",
                          "compatibility", "limitations"}, {"asset", "review"}, "component manifest")
        for key in ("title", "purpose", "component_type", "source_ref", "version", "rationale"):
            _string(manifest[key], f"manifest.{key}", limit=4000 if key in {"purpose", "rationale"} else 2000)
        if manifest["component_type"] not in {"skill", "workflow", "runbook", "script", "instructions"}:
            _error("invalid_input", "component_type is unsupported")
        _hash(manifest["sha256"], "manifest.sha256")
        _strings(manifest["compatibility"], "manifest.compatibility", limit=128)
        _strings(manifest["limitations"], "manifest.limitations", limit=128)
        if "review" in manifest:
            cls._validate_review(manifest["review"])
        if "asset" in manifest:
            asset = manifest["asset"]
            _exact(asset, {"content_base64", "media_type", "license", "selected"}, field="manifest.asset")
            _string(asset["content_base64"], "asset.content_base64", limit=2 * MAX_ASSET)
            _string(asset["media_type"], "asset.media_type", limit=200)
            _string(asset["license"], "asset.license", limit=2000)
            if asset["selected"] is not True:
                _error("invalid_input", "stored vendor asset requires explicit selection")
            try:
                content = base64.b64decode(asset["content_base64"], validate=True)
            except (ValueError, binascii.Error):
                _error("invalid_input", "asset content is not valid base64")
            if len(content) > MAX_ASSET:
                _error("too_large", "vendor asset is too large")
            if hashlib.sha256(content).hexdigest() != manifest["sha256"]:
                _error("invalid_input", "vendor asset checksum does not match component")
            try:
                text = content.decode("utf-8")
            except UnicodeError:
                text = ""
            if text and _secret_present(text):
                _error("invalid_input", "vendor asset contains a possible secret value")

    @classmethod
    def _validate_manifest(cls, kind, manifest):
        if not isinstance(manifest, dict):
            _error("invalid_input", "manifest must be an object")
        _bounded(manifest)
        (cls._validate_template if kind == "template" else cls._validate_component)(manifest)
        if _secret_present(manifest):
            _error("invalid_input", "manifest contains a possible secret value")

    @staticmethod
    def _validate_work_payload(payload):
        _exact(payload, {"context", "snapshot", "completion"}, {"review", "capture"}, "work payload")
        _string(payload["context"], "work.context", limit=4000, empty=True)
        if not isinstance(payload["snapshot"], dict) or _sanitize_snapshot(payload["snapshot"], True) != payload["snapshot"]:
            _error("invalid_input", "work snapshot contains unsupported or unsanitized fields")
        _exact(payload["completion"], {"completed", "source", "statuses"}, field="work.completion")
        if not isinstance(payload["completion"]["completed"], bool) or payload["completion"]["source"] not in {"runtime", "user", "imported"}:
            _error("invalid_input", "work completion is invalid")
        _strings(payload["completion"]["statuses"], "work.completion.statuses", limit=128)
        if "capture" in payload:
            capture = payload["capture"]
            _exact(capture, {"sanitizer_version", "include_details", "method", "capture_environment", "configuration_limitations"}, field="work.capture")
            if type(capture["sanitizer_version"]) is not int or capture["sanitizer_version"] != 1 or type(capture["include_details"]) is not bool:
                _error("invalid_input", "work capture metadata is invalid")
            _exact(capture["capture_environment"], {"python", "platform"}, field="work.capture_environment")
            for value in capture["capture_environment"].values():
                _string(value, "capture environment", limit=200)
            for key in ("method", "configuration_limitations"):
                _string(capture[key], "work.capture." + key, limit=2000)
        if "review" in payload:
            _exact(payload["review"], {"verdict", "note", "recorded_at"}, field="work.review")
            if payload["review"]["verdict"] not in {"successful", "mixed", "failed"}:
                _error("invalid_input", "work review verdict is invalid")
            _string(payload["review"]["note"], "work.review.note", limit=4000, empty=True)
            _string(payload["review"]["recorded_at"], "work.review.recorded_at", limit=80)

    def save_artifact(self, kind, manifest, id=None, expected_revision=None):
        if kind not in {"template", "component"}:
            _error("invalid_input", "artifact kind must be template or component")
        self._validate_manifest(kind, manifest)
        with self._lock:
            if id is None:
                if expected_revision is not None:
                    _error("invalid_input", "a new artifact cannot have an expected revision")
                item_id, parent, approved = str(uuid.uuid4()), None, None
            else:
                head = self._head(id)
                if head["kind"] != kind:
                    _error("invalid_state", "artifact kind cannot change")
                if expected_revision is None:
                    _error("invalid_input", "updating an artifact requires expected_revision")
                self._check_revision(head, expected_revision)
                item_id, parent, approved = head["id"], head["revision"], head.get("approved_revision")
            revision, obj = self._publish_object(item_id, kind, parent, "save_artifact", manifest)
            head = {"schema_version": SCHEMA_VERSION, "id": item_id, "kind": kind,
                    "title": manifest["title"], "revision": revision, "archived": False, "updated_at": _now()}
            if approved:
                head["approved_revision"] = approved
            self._publish_head(head)
            return self._public(head, obj)

    def validate_candidate(self, id, expected_revision):
        with self._lock:
            head = self._head(id)
            if head["kind"] not in {"template", "component"}:
                _error("invalid_state", "only artifacts can be validated")
            self._check_revision(head, expected_revision)
            obj = self._read_object(head["revision"])
            errors, warnings = [], []
            try:
                self._validate_manifest(head["kind"], obj["payload"])
            except DomainError as error:
                errors.append(error.message)
            if head["kind"] == "template":
                external = False
                for component in obj["payload"].get("components", []):
                    ref = component["ref"].removeprefix("library:")
                    try:
                        component_id = _uuid(ref, "component.ref")
                    except DomainError:
                        external = True
                        continue
                    try:
                        referenced = self._read_object(component["sha256"])
                        if referenced["id"] != component_id or referenced["kind"] != "component":
                            errors.append(f"component reference {component_id} does not match its pinned revision")
                    except DomainError:
                        errors.append(f"component reference {component_id} is unavailable")
                if external or obj["payload"].get("harness", {}).get("config_ref"):
                    warnings.append("External references and declared environment compatibility were not evaluated.")
            checks = [{"name": "manifest schema", "status": "passed" if not errors else "failed"},
                      {"name": "pinned library references",
                       "status": "failed" if errors else ("unverified" if warnings else "passed")},
                      {"name": "functional behavior", "status": "unverified"}]
            report = {"subject_id": head["id"], "subject_revision": head["revision"], "scope": "structural",
                      "checks": checks, "errors": errors, "warnings": warnings,
                      "result": "valid" if not errors else "invalid"}
            validation_id, _ = self._publish_object(head["id"], "validation", head["revision"], "validate", report)
            return {"validation_id": validation_id, **copy.deepcopy(report)}

    def promote_candidate(self, id, expected_revision, validation_id):
        with self._lock:
            head = self._head(id)
            if head["kind"] not in {"template", "component"}:
                _error("invalid_state", "only artifacts can be promoted")
            self._check_revision(head, expected_revision)
            current = self._read_object(head["revision"])
            review = current["payload"].get("review")
            if not review:
                _error("promotion_blocked", "promotion requires explicit review evidence")
            self._validate_review(review)
            candidate_revision = review["candidate_revision"]
            candidate = self._read_object(candidate_revision)
            if candidate["id"] != head["id"] or candidate["kind"] != head["kind"]:
                _error("promotion_blocked", "review does not identify this artifact candidate")
            reviewed_payload = copy.deepcopy(current["payload"])
            reviewed_payload.pop("review", None)
            if reviewed_payload != candidate["payload"]:
                _error("promotion_blocked", "review evidence is stale for the current candidate content")
            validation = self._read_object(_hash(validation_id, "validation_id"))
            report = validation["payload"]
            if (validation["kind"] != "validation" or report.get("subject_id") != head["id"]
                    or report.get("subject_revision") != candidate_revision or report.get("scope") != "structural"
                    or report.get("result") != "valid"):
                _error("validation_failed", "validation does not approve the reviewed candidate structure")
            baseline = review["baseline"]
            if "artifact_id" in baseline:
                baseline_object = self._read_object(baseline["revision_or_evidence_hash"])
                if baseline_object["id"] != baseline["artifact_id"]:
                    _error("promotion_blocked", "baseline identity and revision disagree")
            if (review["verdict"] == "failed" or review["no_worse"] is not True
                    or review["user_confirmed"] is not True or not review["evidence"]):
                _error("promotion_blocked", "promotion requires explicit successful or scoped mixed no-worse acceptance")
            audit_revision, audit = self._publish_object(head["id"], head["kind"], head["revision"],
                                                         "promote", current["payload"])
            head["revision"] = audit_revision
            head.pop("classification", None)
            head["approved_revision"] = candidate_revision
            head["updated_at"] = _now()
            self._publish_head(head)
            return self._public(head, audit)

    def _bundle_members(self, head, revision):
        root_obj = self._read_object(revision)
        if root_obj["id"] != head["id"] or root_obj["kind"] != head["kind"]:
            _error("corrupt_data", "catalog and object identity disagree")
        objects = {revision: root_obj}
        heads = [{**head, "revision": revision}]
        if head["kind"] == "template":
            for component in root_obj["payload"].get("components", []):
                ref = component["ref"].removeprefix("library:")
                try:
                    component_id = _uuid(ref, "component.ref")
                except DomainError:
                    continue
                item = self._read_object(component["sha256"])
                if item["id"] != component_id or item["kind"] != "component":
                    _error("corrupt_data", "pinned component identity disagrees")
                objects[component["sha256"]] = item
                component_head = self._head(component_id)
                heads.append({**component_head, "revision": component["sha256"]})
        return heads, objects

    @staticmethod
    def _derived_export_object(original, payload):
        value = {"schema_version": SCHEMA_VERSION, "id": original["id"], "kind": original["kind"],
                 "parent_revision": hashlib.sha256(_json_bytes(original)).hexdigest(),
                 "created_at": original["created_at"],
                 "action": "export_reference", "payload": payload}
        raw = _json_bytes(value)
        return hashlib.sha256(raw).hexdigest(), value

    def _asset_projection(self, heads, objects, selected_revisions=()):
        mapped, projected = {}, {}
        for revision, obj in objects.items():
            if obj["kind"] == "component" and "asset" in obj["payload"] and revision not in selected_revisions:
                payload = copy.deepcopy(obj["payload"])
                payload.pop("asset")
                new_revision, new_obj = self._derived_export_object(obj, payload)
                mapped[revision], projected[new_revision] = new_revision, new_obj
            else:
                mapped[revision], projected[revision] = revision, obj
        final = {}
        for revision, obj in projected.items():
            if obj["kind"] != "template":
                final[revision] = obj
                continue
            payload = copy.deepcopy(obj["payload"])
            payload.pop("review", None)
            if not selected_revisions:
                payload.pop("vendor_assets", None)
            for component in payload.get("components", []):
                component["sha256"] = mapped.get(component["sha256"], component["sha256"])
            if payload != obj["payload"]:
                new_revision, new_obj = self._derived_export_object(obj, payload)
                mapped[revision], final[new_revision] = new_revision, new_obj
            else:
                final[revision] = obj
        projected_heads = []
        for head in heads:
            value = {key: copy.deepcopy(item) for key, item in head.items()
                     if key not in {"approved_revision", "classification"}}
            value["revision"] = mapped.get(head["revision"], head["revision"])
            projected_heads.append(value)
        return projected_heads, final

    def _export_inventory(self, head, mode):
        heads, objects = self._bundle_members(head, head["revision"])
        assets = {}
        if mode == "reference":
            heads, objects = self._asset_projection(heads, objects)
        else:
            root = objects[head["revision"]]
            if head["kind"] != "template" or not root["payload"].get("vendor_assets"):
                _error("invalid_state", "vendor export requires explicitly selected stored assets")
            for selected in root["payload"]["vendor_assets"]:
                component = self._read_object(selected["revision"])
                if component["id"] != selected["id"] or component["kind"] != "component":
                    _error("invalid_state", "selected vendor asset is not the pinned component")
                asset = component["payload"].get("asset")
                if not asset or asset.get("selected") is not True or not asset.get("license"):
                    _error("invalid_state", "vendor asset lacks explicit selection or license")
                content = base64.b64decode(asset["content_base64"], validate=True)
                checksum = hashlib.sha256(content).hexdigest()
                assets[checksum] = {"content": content, "component_id": component["id"],
                                    "license": asset["license"], "media_type": asset["media_type"]}
                if selected["revision"] not in objects:
                    objects[selected["revision"]] = component
                    component_head = self._head(selected["id"])
                    heads.append({**component_head, "revision": selected["revision"]})
            heads, objects = self._asset_projection(heads, objects, {selected["revision"] for selected in root["payload"]["vendor_assets"]})
        return heads, objects, assets

    @staticmethod
    def _export_warnings(mode):
        warnings = ["Resume and validation instructions remain inert and are never executed on import.",
                    "Destination compatibility has not been evaluated.",
                    "References and other saved text may reveal private paths; review the actual values before sharing.",
                    "Referenced files are not opened: availability, local changes and repository dirty state are unverified.",
                    "Credential scanning only checks stored content for known patterns; unrecognized secrets and all external files remain unverified."]
        warnings.append("External references are not copied and must remain available to the recipient."
                        if mode == "reference" else
                        "Selected stored assets and their license declarations will be copied into the bundle.")
        return warnings

    @staticmethod
    def _plan(head, mode, heads, objects, assets, warnings):
        inventory = {"items": sorted({item["id"]: item["revision"] for item in heads}.items()),
                     "objects": sorted(objects),
                     "assets": sorted((checksum, item["component_id"], item["license"], item["media_type"])
                                      for checksum, item in assets.items())}
        references, descriptions = [], []
        for revision, obj in sorted(objects.items()):
            payload = obj["payload"]
            title = payload.get("title") or next((item["title"] for item in heads if item["id"] == obj["id"]), obj["id"])
            descriptions.append({"title": title, "kind": obj["kind"], "revision": revision,
                                 "version": payload.get("version", "unrecorded")})
            values = {"source_ref": payload.get("source_ref"),
                      "harness.config_ref": payload.get("harness", {}).get("config_ref"),
                      "permissions_policy.ref": payload.get("permissions_policy", {}).get("ref")}
            for group, field in (("instruction_layers", "ref"), ("components", "ref"), ("validation", "cwd_ref"), ("validation", "last_result_ref")):
                for index, entry in enumerate(payload.get(group, [])):
                    values[f"{group}[{index}].{field}"] = entry.get(field)
            snapshot = payload.get("snapshot", {})
            values["snapshot.source"] = snapshot.get("source")
            for index, session in enumerate(snapshot.get("sessions", [snapshot])):
                values[f"snapshot.sessions[{index}].cwd"] = session.get("cwd")
            references.extend({"title": title, "field": field, "value": value, "verification": "unverified"}
                              for field, value in values.items() if value)
        inventory["descriptions"], inventory["references"] = descriptions, references
        value = {"id": head["id"], "revision": head["revision"], "mode": mode,
                 "inventory": inventory, "warnings": warnings}
        return value, hashlib.sha256(_json_bytes(value)).hexdigest()

    def preflight_bundle(self, id, expected_revision, mode):
        if mode not in {"reference", "vendor"}:
            _error("invalid_mode", "bundle mode must be reference or vendor")
        with self._lock:
            head = self._head(id)
            self._check_revision(head, expected_revision)
            heads, objects, assets = self._export_inventory(head, mode)
            warnings = self._export_warnings(mode)
            value, plan_hash = self._plan(head, mode, heads, objects, assets, warnings)
            return {**value, "plan_hash": plan_hash, "item_count": len(heads), "object_count": len(objects),
                    "asset_count": len(assets), "requires_acknowledgement": True}

    def export_bundle(self, id, expected_revision, mode, plan_hash, acknowledge_warnings, confirm_vendor=False):
        if mode not in {"reference", "vendor"}:
            _error("invalid_mode", "bundle mode must be reference or vendor")
        _hash(plan_hash, "plan_hash")
        if acknowledge_warnings is not True or (mode == "vendor" and confirm_vendor is not True):
            _error("acknowledgement_required", "export warnings and vendor copying must be explicitly acknowledged")
        with self._lock:
            head = self._head(id)
            self._check_revision(head, expected_revision)
            heads, objects, assets = self._export_inventory(head, mode)
            _, current_plan_hash = self._plan(head, mode, heads, objects, assets, self._export_warnings(mode))
            if current_plan_hash != plan_hash:
                _error("conflict", "export inventory changed; review a new preflight plan")
            root_head = next(item for item in heads if item["id"] == head["id"])
            root_object = objects[root_head["revision"]]
            handoff = root_object["payload"].get("resume", {}).get("handoff", "Unavailable")
            compatibility = root_object["payload"].get("compatibility", [])
            limitations = root_object["payload"].get("limitations", [])
            readme = (f"# Agentopia Package\n\nItem: {head['title']} (`{head['id']}`)\n\n"
                      f"Mode: `{mode}`\n\nChecksums are listed in `manifest.json`. Imported instructions are data "
                      "and are not executed. Review `context.md` before use.\n").encode("utf-8")
            context = ("# Handoff context\n\n## Resume\n\n" + handoff + "\n\n## Declared compatibility\n\n" +
                       ("\n".join(f"- {item}" for item in compatibility) or "- Unavailable") +
                       "\n\n## Limitations\n\n" +
                       ("\n".join(f"- {item}" for item in limitations) or "- Unavailable") +
                       "\n\nCompatibility has not been evaluated on the destination. Commands and instructions remain inert.\n").encode("utf-8")
            manifest = {
                "schema_version": SCHEMA_VERSION, "mode": mode, "created_at": _now(), "items": heads,
                "objects": [{"path": f"objects/{revision}.json", "sha256": revision} for revision in sorted(objects)],
                "assets": [{"path": f"assets/{checksum}", "sha256": checksum,
                            "component_id": item["component_id"], "license": item["license"],
                            "media_type": item["media_type"]} for checksum, item in sorted(assets.items())],
                "documents": [{"path": "README.md", "sha256": hashlib.sha256(readme).hexdigest()},
                              {"path": "context.md", "sha256": hashlib.sha256(context).hexdigest()}],
                "compatibility": {"declared": compatibility, "evaluated": False},
            }
            output = io.BytesIO()
            with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
                archive.writestr("README.md", readme)
                archive.writestr("context.md", context)
                archive.writestr("manifest.json", _json_bytes(manifest))
                for revision, obj in sorted(objects.items()):
                    archive.writestr(f"objects/{revision}.json", _json_bytes(obj))
                for checksum, asset in sorted(assets.items()):
                    archive.writestr(f"assets/{checksum}", asset["content"])
            result = output.getvalue()
            if len(result) > MAX_UPLOAD:
                _error("too_large", "bundle is too large")
            return result

    @staticmethod
    def _zip_name(info):
        name = info.filename
        path = PurePosixPath(name)
        if (not name or "\\" in name or path.is_absolute() or ".." in path.parts or ":" in path.parts[0]
                or name.endswith("/") or len(name) > 240):
            _error("bundle_invalid", "bundle contains an unsafe path")
        mode = (info.external_attr >> 16) & 0xFFFF
        if mode and stat.S_ISLNK(mode):
            _error("bundle_invalid", "bundle links are not allowed")
        allowed = name in {"README.md", "context.md", "manifest.json"} or bool(
            re.fullmatch(r"(?:objects/[0-9a-f]{64}\.json|assets/[0-9a-f]{64})", name))
        if not allowed:
            _error("bundle_invalid", "bundle contains an unsupported file")
        return name

    @staticmethod
    def _validate_bundle_manifest(manifest):
        _exact(manifest, {"schema_version", "mode", "created_at", "items", "objects", "assets", "documents",
                          "compatibility"},
               field="bundle manifest")
        if manifest["schema_version"] != SCHEMA_VERSION:
            _error("unsupported_schema", "unsupported bundle schema")
        if manifest["mode"] not in {"reference", "vendor"}:
            _error("bundle_invalid", "bundle mode is invalid")
        _string(manifest["created_at"], "bundle.created_at", limit=80)
        if not isinstance(manifest["items"], list) or not manifest["items"] or len(manifest["items"]) > MAX_ENTRIES:
            _error("bundle_invalid", "bundle items are invalid")
        if not isinstance(manifest["objects"], list) or len(manifest["objects"]) > MAX_ENTRIES:
            _error("bundle_invalid", "bundle objects are invalid")
        if not isinstance(manifest["assets"], list) or len(manifest["assets"]) > MAX_ENTRIES:
            _error("bundle_invalid", "bundle assets are invalid")
        if not isinstance(manifest["documents"], list) or len(manifest["documents"]) != 2:
            _error("bundle_invalid", "bundle documents are invalid")
        _exact(manifest["compatibility"], {"declared", "evaluated"}, field="bundle.compatibility")
        _strings(manifest["compatibility"]["declared"], "bundle.compatibility.declared", limit=128)
        if manifest["compatibility"]["evaluated"] is not False:
            _error("bundle_invalid", "bundle cannot claim destination compatibility")

    def import_bundle(self, upload_bytes):
        if not isinstance(upload_bytes, bytes):
            _error("invalid_input", "bundle upload must be bytes")
        if not upload_bytes or len(upload_bytes) > MAX_UPLOAD:
            _error("too_large", "bundle upload is empty or too large")
        try:
            archive = zipfile.ZipFile(io.BytesIO(upload_bytes))
        except (zipfile.BadZipFile, OSError):
            _error("bundle_invalid", "upload is not a valid ZIP bundle")
        with archive:
            infos = archive.infolist()
            if len(infos) > MAX_ENTRIES:
                _error("too_large", "bundle has too many entries")
            names, total = {}, 0
            for info in infos:
                name = self._zip_name(info)
                if name in names:
                    _error("bundle_invalid", "bundle contains duplicate paths")
                names[name] = info
                total += info.file_size
                if info.file_size > MAX_MEMBER or total > MAX_TOTAL:
                    _error("too_large", "bundle expands beyond its limit")
                if info.file_size and (not info.compress_size or info.file_size / info.compress_size > 200):
                    _error("bundle_invalid", "bundle compression ratio is unsafe")
            if "manifest.json" not in names or "README.md" not in names or "context.md" not in names:
                _error("bundle_invalid", "bundle manifest or README is missing")
            try:
                raw_manifest = archive.read("manifest.json")
                manifest = _strict_json(raw_manifest, "bundle_invalid")
            except (OSError, DomainError):
                _error("bundle_invalid", "bundle manifest is not valid JSON")
            if _secret_present(manifest):
                _error("secret_detected", "bundle contains a possible secret value")
            self._validate_bundle_manifest(manifest)
            declared_documents = set()
            for entry in manifest["documents"]:
                _exact(entry, {"path", "sha256"}, field="bundle document entry")
                if entry["path"] not in {"README.md", "context.md"} or entry["path"] in declared_documents:
                    _error("bundle_invalid", "bundle document declaration is inconsistent")
                content = archive.read(entry["path"])
                if hashlib.sha256(content).hexdigest() != _hash(entry["sha256"], "bundle document checksum"):
                    _error("bundle_invalid", "bundle document checksum failed")
                try:
                    text = content.decode("utf-8")
                except UnicodeError:
                    _error("bundle_invalid", "bundle document is not UTF-8")
                if _secret_present(text):
                    _error("secret_detected", "bundle document contains a possible secret value")
                declared_documents.add(entry["path"])
            object_entries = {}
            declared_paths = set()
            for entry in manifest["objects"]:
                _exact(entry, {"path", "sha256"}, field="bundle object entry")
                path, checksum = entry["path"], _hash(entry["sha256"], "bundle object checksum")
                if path != f"objects/{checksum}.json" or path not in names or path in declared_paths:
                    _error("bundle_invalid", "bundle object declaration is inconsistent")
                declared_paths.add(path)
                raw = archive.read(path)
                if hashlib.sha256(raw).hexdigest() != checksum:
                    _error("bundle_invalid", "bundle object checksum failed")
                try:
                    obj = _strict_json(raw, "bundle_invalid")
                except DomainError:
                    _error("bundle_invalid", "bundle object is not valid JSON")
                if _secret_present(obj):
                    _error("secret_detected", "bundle contains a possible secret value")
                self._validate_object(obj)
                if obj["kind"] == "validation":
                    _error("bundle_invalid", "validation records are not portable items")
                try:
                    if obj["kind"] in {"template", "component"}:
                        self._validate_manifest(obj["kind"], obj["payload"])
                    else:
                        self._validate_work_payload(obj["payload"])
                except DomainError:
                    _error("bundle_invalid", "bundle object payload is invalid")
                object_entries[checksum] = (raw, obj)
            expected_files = {"README.md", "context.md", "manifest.json"} | declared_paths
            declared_assets = set()
            for entry in manifest["assets"]:
                _exact(entry, {"path", "sha256", "component_id", "license", "media_type"}, field="bundle asset entry")
                checksum = _hash(entry["sha256"], "bundle asset checksum")
                if (entry["path"] != f"assets/{checksum}" or entry["path"] not in names
                        or entry["path"] in declared_assets):
                    _error("bundle_invalid", "bundle asset declaration is inconsistent")
                _uuid(entry["component_id"], "bundle asset component_id")
                _string(entry["license"], "bundle asset license", limit=2000)
                _string(entry["media_type"], "bundle asset media_type", limit=200)
                content = archive.read(entry["path"])
                if hashlib.sha256(content).hexdigest() != checksum:
                    _error("bundle_invalid", "bundle asset checksum failed")
                try:
                    text = content.decode("utf-8")
                except UnicodeError:
                    text = ""
                if text and _secret_present(text):
                    _error("secret_detected", "bundle asset contains a possible secret value")
                matching = [obj for _, obj in object_entries.values()
                            if obj["kind"] == "component" and obj["id"] == entry["component_id"]]
                if len(matching) != 1:
                    _error("bundle_invalid", "bundle asset lacks its component manifest")
                asset = matching[0]["payload"].get("asset")
                if (not asset or matching[0]["payload"].get("sha256") != checksum
                        or asset.get("license") != entry["license"] or asset.get("media_type") != entry["media_type"]):
                    _error("bundle_invalid", "bundle asset and component manifest disagree")
                declared_assets.add(entry["path"])
                expected_files.add(entry["path"])
            if manifest["mode"] == "reference" and manifest["assets"]:
                _error("bundle_invalid", "reference bundles cannot contain vendored assets")
            if set(names) != expected_files:
                _error("bundle_invalid", "bundle contains undeclared files")
            heads = []
            item_ids, item_revisions = set(), set()
            for item in manifest["items"]:
                head = self._validate_head(item)
                if head["id"] in item_ids or head["revision"] in item_revisions:
                    _error("bundle_invalid", "bundle contains duplicate catalog identities")
                obj = object_entries.get(head["revision"], (None, None))[1]
                if not obj or obj["id"] != head["id"] or obj["kind"] != head["kind"]:
                    _error("bundle_invalid", "bundle catalog and object identity disagree")
                item_ids.add(head["id"])
                item_revisions.add(head["revision"])
                heads.append(copy.deepcopy(head))
            if item_revisions != set(object_entries):
                _error("bundle_invalid", "bundle contains orphan or undeclared item objects")
            mapping, local_objects = {}, {}
            ordered = sorted(heads, key=lambda item: item["kind"] == "template")
            with self._lock:
                for head in ordered:
                    foreign_revision = head["revision"]
                    original = object_entries[foreign_revision][1]
                    payload = copy.deepcopy(original["payload"])
                    payload.pop("review", None)
                    if original["kind"] == "work":
                        payload["completion"] = {"completed": False, "source": "imported",
                                                 "statuses": payload["completion"]["statuses"]}
                    if original["kind"] == "template":
                        for component in payload.get("components", []):
                            component["sha256"] = mapping.get(component["sha256"], component["sha256"])
                        for selected in payload.get("vendor_assets", []):
                            selected["revision"] = mapping.get(selected["revision"], selected["revision"])
                    local = {"schema_version": SCHEMA_VERSION, "id": original["id"], "kind": original["kind"],
                             "parent_revision": foreign_revision, "created_at": original["created_at"],
                             "action": "import_candidate", "payload": payload}
                    raw = _json_bytes(local)
                    local_revision = hashlib.sha256(raw).hexdigest()
                    existing = self._index.get(head["id"])
                    if existing and existing["revision"] != local_revision:
                        previous = self._read_object(existing["revision"])
                        if previous == {**local, "created_at": previous["created_at"]}:
                            local_revision = existing["revision"]
                            raw = None
                    mapping[foreign_revision] = local_revision
                    if raw is not None:
                        local_objects[local_revision] = (raw, local)
                    head.pop("approved_revision", None)
                    head.pop("classification", None)
                    head["revision"] = local_revision
                    head["updated_at"] = manifest["created_at"]
                for head in heads:
                    existing = self._index.get(head["id"])
                    if existing and existing["revision"] != head["revision"]:
                        _error("conflict", "bundle item already exists at another revision")
                self._prepare_storage()
                for checksum, (raw, _) in {**object_entries, **local_objects}.items():
                    self._atomic_write(self._object_path(checksum), raw, replace=False)
                imported, unchanged = [], []
                for head in heads:
                    if head["id"] in self._index:
                        unchanged.append(head["id"])
                    else:
                        self._publish_head(head)
                        imported.append(head["id"])
            return {"status": "imported", "imported_ids": imported, "unchanged_ids": unchanged,
                    "warnings": ["Imported validation declarations and resume instructions remain inert."],
                    "compatibility": {"declared": manifest["compatibility"]["declared"], "evaluated": False,
                                      "summary": "Destination compatibility was not evaluated; review declarations and limitations."}}

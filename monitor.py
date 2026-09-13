"""Agent Foundry: local agent-session monitoring and explicit work-library actions.

Supports a Codex local-store adapter and an agent-agnostic JSON adapter. Passive monitoring never invokes
providers or reads auth.json or hidden reasoning. Visible prompts and statements supply bounded semantics.
"""
import argparse
import copy
import datetime as dt
import json
import math
import os
import re
import secrets
import sqlite3
import statistics
import time
from threading import Lock
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse


HERE = Path(__file__).resolve().parent
THREAD_ID = re.compile(r"([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})")
FINAL_EVENTS = {"task_complete", "task_completed", "turn_completed", "turn_aborted", "task_failed"}
WORK_EVENTS = {"task_started", "item_started", "item_completed", "agent_message_delta", "reasoning_delta"}
STALE_ACTIVITY_SECONDS = 30 * 60
TRACE_HEAD_BYTES = 128 * 1024
TRACE_TAIL_BYTES = 256 * 1024


def iso(timestamp):
    return dt.datetime.fromtimestamp(timestamp, dt.timezone.utc).astimezone().isoformat(timespec="seconds")


def compact_text(value, limit=260):
    value = value or ""
    delegation = re.search(r"^<(?:codex|realtime)_delegation>.*?<input>(.*?)</input>", value, re.DOTALL)
    value = delegation.group(1) if delegation else value
    value = re.sub(r"\s+", " ", value).strip()
    return value if len(value) <= limit else value[:limit - 1].rstrip() + "…"


def read_json_lines(path, tail_bytes=None, head_bytes=0):
    try:
        with path.open("rb") as stream:
            size = path.stat().st_size
            head = b""
            if head_bytes and not tail_bytes:
                head = stream.read(head_bytes)
                if size > head_bytes:
                    head = head[:head.rfind(b"\n") + 1]
                stream.seek(0, os.SEEK_END)
            elif head_bytes and tail_bytes and size > head_bytes + tail_bytes:
                head = stream.read(head_bytes)
                head = head[:head.rfind(b"\n") + 1]
                stream.seek(-tail_bytes, os.SEEK_END)
                stream.readline()
            elif tail_bytes and not head_bytes and size > tail_bytes:
                stream.seek(-tail_bytes, os.SEEK_END)
                stream.readline()
            offset = stream.tell()
            # Bound concurrent appends too, not just the size observed before seeking.
            tail = (stream.read(max(0, head_bytes + (tail_bytes or 0) - len(head))).splitlines(keepends=True)
                    if head_bytes or tail_bytes is not None else stream)
            for offset, lines in ((0, head.splitlines(keepends=True)), (offset, tail)):
                for raw in lines:
                    try:
                        row = json.loads(raw.decode("utf-8", errors="replace"))
                        if head_bytes and isinstance(row, dict):
                            row.setdefault("ordinal", offset)
                        yield row
                    except (ValueError, UnicodeDecodeError):
                        pass
                    offset += len(raw)
    except OSError:
        return


class CodexStore:
    def __init__(self, codex_home):
        self.home = codex_home
        self.sessions_dir = codex_home / "sessions"
        self.locks_dir = codex_home / "thread-writer-locks"
        self.index_path = codex_home / "session_index.jsonl"
        self.state_db = codex_home / "state_5.sqlite"
        self.history_db = codex_home / "thread_history_1.sqlite"
        self._files = {}
        self._files_scanned = -float("inf")
        self._spawn_tasks = {}
        self._semantic_cache = {}
        self._recent_cache = {}

    def session_files(self):
        now = time.monotonic()
        if now - self._files_scanned > 2:
            found = {}
            try:
                for path in self.sessions_dir.rglob("*.jsonl"):
                    match = THREAD_ID.search(path.name)
                    if match:
                        found[match.group(1)] = path
            except OSError:
                pass
            self._files = found
            self._files_scanned = now
        return self._files

    def titles(self):
        values = {}
        for row in read_json_lines(self.index_path):
            thread_id = row.get("id")
            if thread_id:
                values[thread_id] = compact_text(row.get("thread_name", ""), 160)
        return values

    def active_ids(self):
        result = set()
        try:
            for path in self.locks_dir.glob("*.lock"):
                match = THREAD_ID.fullmatch(path.stem)
                if match:
                    result.add(match.group(1))
        except OSError:
            pass
        return result

    @staticmethod
    def database(path):
        connection = sqlite3.connect("file:" + path.as_posix() + "?mode=ro", uri=True, timeout=1)
        connection.row_factory = sqlite3.Row
        return connection

    def database_rows(self, ids):
        if not ids or not self.state_db.exists() or not self.history_db.exists():
            return {}, {}, {}, {}
        marks = ",".join("?" for _ in ids)
        threads, parents, turns, durations = {}, {}, {}, {}
        try:
            with self.database(self.state_db) as db:
                sql = ("SELECT id,title,cwd,updated_at_ms,agent_nickname,agent_path,model,reasoning_effort,"
                       "thread_source,first_user_message FROM threads WHERE id IN (" + marks + ")")
                threads = {row["id"]: dict(row) for row in db.execute(sql, tuple(ids))}
                edge_sql = "SELECT parent_thread_id,child_thread_id,status FROM thread_spawn_edges WHERE child_thread_id IN (" + marks + ")"
                parents = {row["child_thread_id"]: dict(row) for row in db.execute(edge_sql, tuple(ids))}
            with self.database(self.history_db) as db:
                turn_sql = ("SELECT thread_id,turn_id,status,started_at,completed_at,duration_ms FROM "
                            "(SELECT *,ROW_NUMBER() OVER(PARTITION BY thread_id ORDER BY started_at DESC) n "
                            "FROM thread_turns WHERE thread_id IN (" + marks + ")) WHERE n=1")
                turns = {row["thread_id"]: dict(row) for row in db.execute(turn_sql, tuple(ids))}
                duration_sql = ("SELECT thread_id,duration_ms FROM thread_turns WHERE status='completed' "
                                "AND duration_ms IS NOT NULL AND thread_id IN (" + marks + ") ORDER BY started_at DESC")
                for row in db.execute(duration_sql, tuple(ids)):
                    bucket = durations.setdefault(row["thread_id"], [])
                    if len(bucket) < 12:
                        bucket.append(row["duration_ms"] / 1000)
        except sqlite3.Error:
            pass
        return threads, parents, turns, durations

    @staticmethod
    def estimate(status, turn, samples, now):
        if status == "completed":
            return 100, 0, "completed"
        if status not in ("working", "tool", "thinking"):
            return None, None, "not running"
        started = turn.get("started_at") or now
        elapsed = max(0.0, now - started)
        if samples:
            baseline = max(15.0, statistics.median(samples))
            basis = f"median of {len(samples)} completed turn{'s' if len(samples) != 1 else ''}"
        else:
            baseline = 180.0
            basis = "default baseline; no completed turns yet"
        estimated_total = max(baseline, elapsed * 1.2, 1)
        progress = min(95, max(2, round(100 * elapsed / estimated_total)))
        eta = max(1, round(estimated_total - elapsed))
        return progress, eta, basis

    @staticmethod
    def session_status(turn_status, source_kind, last_tool_status, age_seconds):
        """Treat abandoned in-progress records as idle once their rollout stops changing."""
        if turn_status == "inProgress":
            if age_seconds >= STALE_ACTIVITY_SECONDS:
                return "idle"
            return "tool" if last_tool_status == "running" else "working"
        if turn_status in ("completed", "interrupted", "failed"):
            return turn_status
        return "idle"

    def spawn_task(self, parent_id, agent_path):
        path = self.session_files().get(parent_id)
        if not path:
            return ""
        try:
            key = (parent_id, path.stat().st_size, path.stat().st_mtime_ns)
        except OSError:
            return ""
        cached = self._spawn_tasks.get(key)
        if cached is None:
            cached = {}
            for row in read_json_lines(path, TRACE_TAIL_BYTES, TRACE_HEAD_BYTES):
                payload = row.get("payload", {})
                if row.get("type") != "response_item" or payload.get("type") not in ("custom_tool_call", "function_call"):
                    continue
                if not str(payload.get("name", "")).endswith("spawn_agent"):
                    continue
                try:
                    args = json.loads(payload.get("input") or payload.get("arguments") or "{}")
                except (TypeError, ValueError):
                    continue
                task_name = args.get("task_name", "")
                if task_name:
                    message = args.get("message", "")
                    if not message or message.startswith("gAAAA") or len(message) > 2000:
                        message = task_name.replace("_", " ")
                    cached[task_name] = compact_text(message)
            for old_key in [item for item in self._spawn_tasks if item[0] == parent_id]:
                self._spawn_tasks.pop(old_key, None)
            self._spawn_tasks[key] = cached
        return cached.get(agent_path.rsplit("/", 1)[-1], "")

    @staticmethod
    def metadata(path):
        first = next(read_json_lines(path), {})
        return first.get("payload", {}) if first.get("type") == "session_meta" else {}

    @staticmethod
    def source_fields(meta):
        source = meta.get("source")
        if (meta.get("thread_source") == "guardian_review" or
                isinstance(source, dict) and isinstance(source.get("subagent"), dict) and
                source["subagent"].get("other") == "guardian"):
            return {"kind": "guardian_review"}
        if not isinstance(source, dict):
            return {"kind": str(meta.get("thread_source") or source or "user")}
        spawn = ((source.get("subagent") or {}).get("thread_spawn") or {})
        if spawn:
            return {
                "kind": "subagent", "parent": spawn.get("parent_thread_id", ""),
                "path": spawn.get("agent_path", ""), "nickname": spawn.get("agent_nickname", ""),
                "depth": spawn.get("depth", 0),
            }
        return {"kind": next(iter(source), "unknown")}

    @staticmethod
    def task_from_head(path, is_subagent):
        if not is_subagent:
            return ""
        for index, row in enumerate(read_json_lines(path, head_bytes=TRACE_HEAD_BYTES)):
            if index > 30:
                break
            payload = row.get("payload", {})
            if row.get("type") == "event_msg" and payload.get("type") == "user_message":
                value = payload.get("message") or payload.get("text") or ""
                if value and not value.startswith(("# AGENTS.md", "<environment_context>")):
                    return compact_text(value)
            if row.get("type") == "response_item" and payload.get("type") == "message" and payload.get("role") == "user":
                parts = payload.get("content") or []
                value = " ".join(part.get("text", "") for part in parts if isinstance(part, dict))
                if value and not value.startswith(("# AGENTS.md", "<environment_context>")):
                    return compact_text(value)
        return ""

    @staticmethod
    def _tool_args(payload):
        raw = payload.get("input") or payload.get("arguments") or ""
        args = raw if isinstance(raw, dict) else {}
        if not args and isinstance(raw, str) and raw.lstrip().startswith("{"):
            try:
                value = json.loads(raw)
                args = value if isinstance(value, dict) else {}
            except ValueError:
                pass
        return raw, args

    @staticmethod
    def _tool_command(raw, args):
        command = args.get("cmd", "")
        if not command and isinstance(raw, str):
            match = re.search(r'\bcmd["\']?\s*:\s*("(?:\\.|[^"\\])*")', raw)
            if match:
                try:
                    command = json.loads(match.group(1))
                except ValueError:
                    pass
        return command

    @staticmethod
    def tool_detail(name, payload):
        """Return a short, non-secret description without exposing full tool inputs."""
        raw, args = CodexStore._tool_args(payload)

        tool = name.rsplit(".", 1)[-1]
        if tool == "exec" and isinstance(raw, str):
            nested = re.search(r"tools\.([A-Za-z0-9_]+)", raw)
            tool = nested.group(1) if nested else tool
        label = tool.replace("__", " ").replace("_", " ")

        if tool == "exec_command":
            command = CodexStore._tool_command(raw, args)
            if command and str(command).strip():
                category = CodexStore.action_detail(str(command))
                if category != str(command):
                    return category
            return "Running a local command"
        if tool == "apply_patch":
            names = CodexStore.tool_files(name, payload)[:3]
            return "Editing " + ", ".join(names) if names else "Editing project files"
        if tool in ("web__run", "web run") or tool.endswith("web__run"):
            return "Researching current information"
        if tool == "view_image":
            path = args.get("path", "")
            return "Inspecting " + Path(path).name if path else "Inspecting an image"
        if tool == "write_stdin":
            return "Interacting with a running process"
        if tool.endswith(("wait_threads", "wait_agent")):
            return "Waiting for delegated agents"
        if tool == "wait":
            return "Waiting for a running tool to finish"
        if tool.endswith("request_user_input"):
            return "Waiting for user input"
        if tool.endswith(("spawn_agent", "followup_task", "send_message", "send_message_to_thread")):
            task = args.get("task_name") or args.get("target")
            return f"Coordinating {task}" if task else "Coordinating another agent"
        return compact_text(label, 150) or "Using a tool"

    @staticmethod
    def tool_input_detail(name, payload):
        return CodexStore.tool_detail(name, payload)

    @staticmethod
    def tool_files(name, payload):
        raw, _ = CodexStore._tool_args(payload)
        if not isinstance(raw, str):
            return []
        normalized = raw.replace("\\r\\n", "\n").replace("\\n", "\n")
        paths = re.findall(r"^\*\*\* (?:Update|Add|Delete) File: (.+)$", normalized, re.MULTILINE)
        return list(dict.fromkeys(Path(path.strip()).name for path in paths[:8] if path.strip()))

    @staticmethod
    def safe_result(payload):
        values = [payload]
        raw = payload.get("output") or payload.get("result")
        if isinstance(raw, dict):
            values.append(raw)
        elif isinstance(raw, str) and raw.lstrip().startswith("{"):
            try:
                parsed = json.loads(raw)
                if isinstance(parsed, dict):
                    values.append(parsed)
            except ValueError:
                pass
        exit_code = next((value.get("exit_code", value.get("exitCode")) for value in values
                          if value.get("exit_code", value.get("exitCode")) is not None), None)
        is_error = any(value.get("isError") is True or value.get("is_error") is True for value in values)
        exit_code = exit_code if type(exit_code) is int else None
        if exit_code not in (None, 0):
            is_error = True
        return exit_code, is_error

    @staticmethod
    def action_detail(detail):
        """Turn recognizable command syntax into an activity label; keep the raw trail separate."""
        rules = (
            (r"(?i)\b(pytest|unittest|run_tests|test_[\w.]+)\b", "Running automated tests"),
            (r"(?i)\b(py_compile|dotnet build|npm run build)\b", "Checking project compilation"),
            (r"(?i)\bgit\b.*\b(commit|add)\b", "Saving project changes in Git"),
            (r"(?i)\bgit\b.*\b(status|diff|log|show)\b", "Reviewing project changes"),
            (r"(?i)\b(Get-Content|Select-String|rg)\b", "Reading and searching project files"),
            (r"(?i)\b(Get-Process|Get-CimInstance|netstat)\b", "Checking running processes"),
            (r"(?i)\b(Invoke-RestMethod|Invoke-WebRequest|curl)\b", "Checking a service response"),
        )
        for pattern, label in rules:
            if re.search(pattern, detail):
                return label
        return detail

    @staticmethod
    def _content_text(payload, allowed_types):
        content = payload.get("content") or []
        if isinstance(content, str):
            return content
        if not isinstance(content, list):
            return ""
        return " ".join(
            part.get("text", "") for part in content
            if isinstance(part, dict) and part.get("type") in allowed_types
            and isinstance(part.get("text"), str)
        )

    @staticmethod
    def semantic_trace(path, session_id, fallback=""):
        """Build a bounded public trace from explicit user/assistant and safe runtime events."""
        observations = []
        by_id = {}
        tool_calls = {}
        delegation_calls = {}
        evidence_calls = {}
        approval_calls = {}
        seen_messages = set()
        seen_message_ids = set()
        prompt_context = None
        prompt_observation = None
        latest_step = None
        turn_id = ""
        turn_open = False
        terminal_status = ""
        pending_approvals = set()

        def identity(row, payload, index):
            return str(payload.get("call_id") or payload.get("id") or row.get("ordinal", index))

        def add(kind, source, text, row, payload, parent_id=None, status="working", detail="", **extra):
            summary = compact_text(text)
            if not summary:
                return None
            observation_id = f"{session_id}:{kind}:{identity(row, payload, len(observations))}"
            if observation_id in by_id:
                return by_id[observation_id]
            observation = {
                "id": observation_id, "parent_id": parent_id, "session_id": session_id,
                "kind": kind, "summary": summary, "source": source,
                "timestamp": row.get("timestamp", ""), "status": status,
            }
            if detail:
                observation["detail"] = compact_text(detail, 12000)
            observation.update({key: value for key, value in extra.items() if value not in (None, "", [])})
            observations.append(observation)
            by_id[observation_id] = observation
            return observation

        def visible_user(payload):
            text = payload.get("message") or payload.get("text") or CodexStore._content_text(
                payload, {"input_text", "text", "Text"})
            if not isinstance(text, str):
                return ""
            stripped = text.lstrip()
            hidden_prefixes = (
                "# AGENTS.md", "<environment_context>", "<permissions instructions>",
                "<recommended_plugins>", "<codex_delegation>", "<realtime_delegation>",
            )
            return "" if stripped.startswith(hidden_prefixes) else text

        def visible_assistant(payload, default_source="agent_message", require_phase=False):
            phases = [payload.get(key) for key in ("phase", "channel") if payload.get(key)]
            if any(phase not in ("commentary", "final_answer") for phase in phases) or (require_phase and not phases):
                return "", ""
            phase = "commentary" if "commentary" in phases else (phases[0] if phases else "")
            text = payload.get("message") or payload.get("text") or CodexStore._content_text(
                payload, {"output_text", "text", "Text"})
            source = "agent_commentary" if phase == "commentary" else default_source
            return (text, source) if isinstance(text, str) else ("", "")

        def ensure_prompt(row):
            nonlocal prompt_context, prompt_observation
            if prompt_context is not None:
                return
            text = compact_text(fallback or "Waiting for another turn", 12000)
            synthetic = {"ordinal": "fallback", "timestamp": row.get("timestamp", "")}
            prompt_observation = add("prompt", "prompt_fallback", text, synthetic, synthetic,
                                     detail=text, status="idle")
            prompt_context = {
                "id": prompt_observation["id"], "summary": prompt_observation["summary"],
                "text": text, "source": "prompt_fallback", "session_id": session_id,
                "turn_id": turn_id if turn_open else "", "timestamp": row.get("timestamp", ""),
            }

        def record_prompt(text, row, payload, source="user_prompt"):
            nonlocal prompt_context, prompt_observation, latest_step, terminal_status
            text = compact_text(text, 12000)
            signature = ("user", text, row.get("timestamp", ""))
            message_id = payload.get("id")
            if not text or signature in seen_messages or (message_id and message_id in seen_message_ids):
                return
            seen_messages.add(signature)
            if message_id:
                seen_message_ids.add(message_id)
            prompt_observation = add("prompt", source, text, row, payload, detail=text)
            prompt_context = {
                "id": prompt_observation["id"], "summary": prompt_observation["summary"],
                "text": text, "source": source, "session_id": session_id,
                "turn_id": turn_id if turn_open else "", "timestamp": row.get("timestamp", ""),
            }
            latest_step = None
            terminal_status = ""
            pending_approvals.clear()

        def record_assistant(text, source, row, payload, phase=""):
            nonlocal latest_step
            text = compact_text(text, 12000)
            signature = ("assistant", text, row.get("timestamp", ""))
            if not text or signature in seen_messages:
                return
            seen_messages.add(signature)
            ensure_prompt(row)
            kind = "outcome" if phase == "final_answer" else "step"
            latest_step = add(kind, source, text, row, payload,
                              prompt_observation["id"] if prompt_observation else None,
                              "completed" if kind == "outcome" else "working", detail=text)

        def record_tool(name, call_id, row, payload, status):
            nonlocal latest_step
            call_id = str(call_id or identity(row, payload, len(observations)))
            detail = CodexStore.tool_detail(name, payload)
            _, args = CodexStore._tool_args(payload)
            ensure_prompt(row)
            status = status if status in ("running", "completed", "failed", "approval") else "completed"
            tool_name = name.rsplit(".", 1)[-1]
            parent_id = latest_step["id"] if latest_step else (prompt_observation["id"] if prompt_observation else None)
            if tool_name.endswith("spawn_agent") and call_id not in delegation_calls:
                task_name = compact_text(args.get("task_name", ""), 160)
                task = args.get("message") or task_name.replace("_", " ")
                if not isinstance(task, str) or task.startswith("gAAAA"):
                    task = task_name.replace("_", " ")
                delegation = add("delegation", "delegated_task", task, row, {"id": call_id},
                                 parent_id, "working", detail=task)
                if delegation:
                    parent_id = delegation["id"]
                    ancestor_ids = []
                    cursor = delegation
                    while cursor:
                        ancestor_ids.append(cursor["id"])
                        cursor = by_id.get(cursor.get("parent_id"))
                    delegation_calls[call_id] = {
                        "task_name": task_name, "summary": delegation["summary"],
                        "observation": dict(delegation),
                        "prompt_context": dict(prompt_context) if prompt_context else None,
                        "ancestors": [dict(by_id[item_id]) for item_id in reversed(ancestor_ids)],
                    }
            observation = tool_calls.get(call_id)
            if observation is None:
                observation = add(
                    "tool", "runtime_event", CodexStore.action_detail(detail), row,
                    {"id": call_id}, parent_id,
                    status, detail=CodexStore.tool_input_detail(name, payload), tool=name, call_id=call_id,
                )
                tool_calls[call_id] = observation
                evidence_calls[call_id] = []
                files = CodexStore.tool_files(name, payload)
                if files:
                    evidence_calls[call_id].append(add(
                        "file", "runtime_event", "Editing " + ", ".join(files), row,
                        {"id": f"{call_id}:file"}, observation["id"], status,
                        files=files, call_id=call_id,
                    ))
                if CodexStore.action_detail(detail) == "Running automated tests":
                    evidence_calls[call_id].append(add(
                        "test", "runtime_event", "Running automated tests", row,
                        {"id": f"{call_id}:test"}, observation["id"], status,
                        tool=name, call_id=call_id,
                    ))
            elif observation:
                observation["status"] = status
                if status != "running" and not observation.get("end_timestamp"):
                    observation["end_timestamp"] = row.get("timestamp", "")
                for evidence in evidence_calls.get(call_id, []):
                    if evidence:
                        evidence["status"] = status
                        evidence["end_timestamp"] = row.get("timestamp", "")
            return observation

        def finish_tool(call_id, row, payload, default_status="completed"):
            call_id = str(call_id or "")
            tool = tool_calls.get(call_id)
            if not tool:
                return
            exit_code, is_error = CodexStore.safe_result(payload)
            status = "failed" if is_error else default_status
            if status not in ("completed", "failed", "interrupted"):
                status = "completed"
            tool["status"] = status
            tool["end_timestamp"] = row.get("timestamp", "")
            for evidence in evidence_calls.get(call_id, []):
                if evidence:
                    evidence["status"] = status
                    evidence["end_timestamp"] = row.get("timestamp", "")
            kind = "error" if status == "failed" else "result"
            add(kind, "runtime_event", f"{tool['summary']} {status}", row,
                {"id": call_id}, tool["id"], status, tool=tool.get("tool"),
                call_id=call_id, exit_code=exit_code)
            approval = approval_calls.get(call_id)
            if approval:
                approval["status"] = "completed"
                approval["end_timestamp"] = row.get("timestamp", "")

        # ponytail: retain bounded opening context and recent evidence, not an ever-growing
        # full transcript. An incremental parser is warranted only if omitted history is needed.
        try:
            input_truncated = path.stat().st_size > TRACE_HEAD_BYTES + TRACE_TAIL_BYTES
        except OSError:
            input_truncated = False
        for index, row in enumerate(read_json_lines(path, TRACE_TAIL_BYTES, TRACE_HEAD_BYTES)):
            payload = row.get("payload", {})
            if not isinstance(payload, dict):
                continue
            row_type = row.get("type")
            if row_type == "compacted":
                history = payload.get("replacement_history") or []
                for history_index, message in enumerate(history if isinstance(history, list) else []):
                    if not isinstance(message, dict) or message.get("type") != "message" or message.get("role") != "user":
                        continue
                    retained_row = dict(row)
                    retained_row["ordinal"] = message.get("id") or f"{row.get('ordinal', index)}:{history_index}"
                    record_prompt(visible_user(message), retained_row, message, "retained_user_prompt")
            elif row_type == "event_msg":
                kind = payload.get("type", "")
                if kind == "user_message":
                    record_prompt(visible_user(payload), row, payload)
                elif kind == "agent_message":
                    text, source = visible_assistant(payload)
                    record_assistant(text, source, row, payload, payload.get("phase") or payload.get("channel") or "")
                elif kind == "task_started":
                    turn_id, turn_open, terminal_status = payload.get("turn_id", ""), True, ""
                    if prompt_context and not prompt_context["turn_id"]:
                        prompt_context["turn_id"] = turn_id
                elif kind in FINAL_EVENTS:
                    turn_open = False
                    terminal_status = {"turn_aborted": "interrupted", "task_failed": "failed"}.get(kind, "completed")
                elif kind in ("exec_approval_request", "apply_patch_approval_request", "approval_request"):
                    approval_id = str(payload.get("call_id") or payload.get("id") or "unidentified")
                    pending_approvals.add(approval_id)
                    ensure_prompt(row)
                    parent_id = tool_calls.get(approval_id, {}).get("id") or (
                        latest_step["id"] if latest_step else prompt_observation["id"])
                    approval_calls[approval_id] = add(
                        "approval", "runtime_event", "Approval required", row,
                        {"id": approval_id}, parent_id, "approval", call_id=approval_id,
                    )
                elif kind in ("approval_response", "exec_approval_response", "apply_patch_approval_response"):
                    approval_id = payload.get("call_id") or payload.get("id")
                    pending_approvals.discard(approval_id) if approval_id else pending_approvals.clear()
                    if approval_id in approval_calls:
                        approval_calls[approval_id]["status"] = "completed"
                        approval_calls[approval_id]["end_timestamp"] = row.get("timestamp", "")
                elif kind == "token_count":
                    ensure_prompt(row)
                    info = payload.get("info") or {}
                    context_window = info.get("model_context_window")
                    used = (info.get("last_token_usage") or {}).get("total_tokens")
                    numbers = [value if isinstance(value, (int, float)) and not isinstance(value, bool) else None
                               for value in (used, context_window)]
                    summary = (f"Context {numbers[0]:g}/{numbers[1]:g} tokens" if None not in numbers
                               else f"Context {numbers[0]:g} tokens" if numbers[0] is not None
                               else "Updated context usage")
                    add("metric", "runtime_event", summary, row, payload,
                        latest_step["id"] if latest_step else prompt_observation["id"],
                        "working" if turn_open else "idle",
                        detail=f"Context window: {context_window}" if isinstance(context_window, (int, float)) else "")

                item = payload.get("item") or {}
                if kind in ("item_started", "item_completed") and isinstance(item, dict):
                    item_kind = item.get("type")
                    if item_kind == "AgentMessage":
                        text, source = visible_assistant(item, require_phase=True)
                        record_assistant(text, source, row, item, item.get("phase") or item.get("channel") or "")
                    elif item_kind == "McpToolCall":
                        name = ".".join(filter(None, (item.get("server"), item.get("tool")))) or "tool"
                        status = "running" if kind == "item_started" else item.get("status", "completed")
                        record_tool(name, item.get("id"), row, {"arguments": item.get("arguments") or {}}, status)
                        if kind == "item_completed":
                            finish_tool(item.get("id"), row, item, status)
                    elif item_kind == "CommandExecution":
                        command = item.get("command")
                        if isinstance(command, list):
                            command = " ".join(str(part) for part in command)
                        command = command if isinstance(command, str) else ""
                        status = "running" if kind == "item_started" else item.get("status", "completed")
                        record_tool("exec_command", item.get("id"), row, {"arguments": {"cmd": command}}, status)
                        if kind == "item_completed":
                            finish_tool(item.get("id"), row, item, status)
                    elif item_kind == "FileChange":
                        changes = item.get("changes") or {}
                        paths = list(changes) if isinstance(changes, dict) else []
                        headers = "\n".join(f"*** Update File: {path}" for path in paths[:8])
                        status = "running" if kind == "item_started" else item.get("status", "completed")
                        record_tool("apply_patch", item.get("id"), row, {"arguments": headers}, status)
                        if kind == "item_completed":
                            finish_tool(item.get("id"), row, item, status)
                    if item.get("status") in ("awaitingApproval", "pendingApproval", "waiting_for_approval"):
                        pending_approvals.add(item.get("id") or "unidentified")
                    elif kind == "item_completed":
                        pending_approvals.discard(item.get("id"))
            elif row_type == "response_item":
                item_type = payload.get("type")
                if item_type == "message" and payload.get("role") == "user":
                    record_prompt(visible_user(payload), row, payload)
                elif item_type == "message" and payload.get("role") == "assistant":
                    text, source = visible_assistant(payload, require_phase=True)
                    record_assistant(text, source, row, payload, payload.get("phase") or payload.get("channel") or "")
                elif item_type in ("custom_tool_call", "function_call"):
                    record_tool(payload.get("name", "tool"), payload.get("call_id") or payload.get("id"),
                                row, payload, "running")
                elif item_type in ("custom_tool_call_output", "function_call_output"):
                    call_id = str(payload.get("call_id") or "")
                    pending_approvals.discard(call_id)
                    finish_tool(call_id, row, payload, payload.get("status") or "completed")

        if prompt_context is None:
            text = compact_text(fallback or "Waiting for another turn", 12000)
            synthetic = {"ordinal": "fallback", "timestamp": ""}
            prompt_observation = add("prompt", "prompt_fallback", text, synthetic, synthetic, detail=text,
                                     status="idle")
            prompt_context = {
                "id": prompt_observation["id"], "summary": prompt_observation["summary"],
                "text": text, "source": "prompt_fallback", "session_id": session_id,
                "turn_id": turn_id, "timestamp": "",
            }

        current_tools = [item for item in tool_calls.values() if item and item.get("parent_id") == (
            latest_step["id"] if latest_step else prompt_observation["id"])]
        status = "approval" if pending_approvals else terminal_status or (
            "tool" if any(item.get("status") == "running" for item in current_tools)
            else "working" if turn_open else "idle")
        working = latest_step or (current_tools[-1] if current_tools and current_tools[-1].get("status") == "running"
                                  else prompt_observation)
        working_source = working["source"] if working is not prompt_observation else prompt_context["source"]
        if working.get("kind") == "tool":
            working_source = "tool_inferred"
        working_on = {
            "summary": working["summary"], "source": working_source,
            "observation_id": working["id"], "status": status,
            "task_path": list(dict.fromkeys((prompt_context["summary"], working["summary"]))),
        }

        required = {prompt_context["id"], working["id"]}
        cursor = working
        while cursor and cursor.get("parent_id"):
            required.add(cursor["parent_id"])
            cursor = by_id.get(cursor["parent_id"])
        def belongs_to_root(item):
            cursor = item
            seen = set()
            while cursor and cursor["id"] not in seen:
                if cursor["id"] == prompt_context["id"]:
                    return True
                seen.add(cursor["id"])
                cursor = by_id.get(cursor.get("parent_id"))
            return False

        branch = [item for item in observations if belongs_to_root(item)]
        truncated = input_truncated or len(branch) > 120
        selected = branch[-120:]
        for item in selected:
            cursor = item
            seen = set()
            while cursor and cursor["id"] not in seen:
                seen.add(cursor["id"])
                required.add(cursor["id"])
                cursor = by_id.get(cursor.get("parent_id"))
        selected_ids = {item["id"] for item in selected}
        selected = [item for item in branch if item["id"] in required and item["id"] not in selected_ids] + selected
        semantic_kinds = {"prompt", "agent", "step", "delegation", "outcome"}
        return {
            "prompt_context": prompt_context, "working_on": working_on,
            "work_breakdown": [item for item in selected if item["kind"] in semantic_kinds],
            "trace": {"version": 1, "root_id": prompt_context["id"],
                      "observations": selected, "truncated": truncated},
            "_delegations": list(delegation_calls.values()),
        }

    def semantic_cached(self, path, session_id, fallback=""):
        try:
            stat = path.stat()
            key = (stat.st_size, stat.st_mtime_ns, fallback)
        except OSError:
            key = (0, 0, fallback)
        cached = self._semantic_cache.get(session_id)
        if cached and cached[0] == key:
            return cached[1]
        value = self.semantic_trace(path, session_id, fallback)
        self._semantic_cache[session_id] = (key, value)
        return value

    @staticmethod
    def delegated_task(semantic, agent_path):
        leaf = (agent_path or "").rsplit("/", 1)[-1]
        for item in reversed((semantic or {}).get("_delegations", [])):
            if item.get("task_name") == leaf:
                return item.get("summary", "")
        return ""

    @staticmethod
    def _public_semantic(value, status):
        observations = [dict(item) for item in value["trace"]["observations"]]
        working = dict(value["working_on"])
        working["status"] = status
        return {
            "prompt_context": dict(value["prompt_context"]),
            "working_on": working,
            "work_breakdown": [dict(item) for item in observations
                               if item["kind"] in {"prompt", "agent", "step", "delegation", "outcome"}],
            "trace": {"version": 1, "root_id": value["trace"]["root_id"],
                      "observations": observations, "truncated": value["trace"]["truncated"]},
        }

    @staticmethod
    def attach_semantics(rows):
        """Attach child traces to recorded delegations without mutating cached parser data."""
        by_session = {row["id"]: row for row in rows}
        resolved = {}

        def resolve(session_id, visiting):
            if session_id in resolved:
                return resolved[session_id]
            row = by_session[session_id]
            base = row["_semantic_source"]
            public = CodexStore._public_semantic(base, row.get("status", "idle"))
            parent_id = row.get("parent")
            if not parent_id or parent_id not in by_session or session_id in visiting:
                row.update(public)
                resolved[session_id] = public
                return public

            parent = by_session[parent_id]
            parent_public = resolve(parent_id, visiting | {session_id})
            leaf = (row.get("agent_path") or "").rsplit("/", 1)[-1]
            matches = [item for item in parent.get("_semantic_source", {}).get("_delegations", [])
                       if item.get("task_name") == leaf]
            delegation = matches[-1] if matches else None

            if delegation:
                inherited_prompt = delegation.get("prompt_context") or parent_public["prompt_context"]
                if inherited_prompt.get("source") == "prompt_fallback" and parent.get("parent"):
                    inherited_prompt = parent_public["prompt_context"]
                prompt_context = dict(inherited_prompt)
                source = "delegated_task"
                assignment = delegation["summary"]
            else:
                prompt_context = dict(parent_public["prompt_context"])
                prompt_context["source"] = "prompt_fallback"
                source = "metadata_fallback"
                assignment = compact_text(row.get("task") or row.get("name") or session_id)

            parent_observations = {item["id"]: item for item in parent_public["trace"]["observations"]}
            chain = []
            if delegation:
                candidate = parent_observations.get(delegation["observation"]["id"])
                ancestors = {item["id"]: item for item in delegation.get("ancestors", [])}
                cursor = dict(candidate or delegation["observation"])
                seen = set()
                while cursor and cursor["id"] not in seen:
                    seen.add(cursor["id"])
                    chain.append(dict(cursor))
                    cursor = parent_observations.get(cursor.get("parent_id")) or ancestors.get(cursor.get("parent_id"))
                chain.reverse()
                if any(item["kind"] == "prompt" and item["id"] != prompt_context["id"] for item in chain):
                    chain = []
                    cursor = parent_observations.get(parent_public["working_on"]["observation_id"])
                    seen = set()
                    while cursor and cursor["id"] not in seen:
                        seen.add(cursor["id"])
                        chain.append(dict(cursor))
                        cursor = parent_observations.get(cursor.get("parent_id"))
                    chain.reverse()
                    delegation_node = dict(candidate or delegation["observation"])
                    delegation_node["parent_id"] = chain[-1]["id"] if chain else prompt_context["id"]
                    chain.append(delegation_node)
            if not chain or chain[0]["id"] != prompt_context["id"]:
                prompt_node = next((item for item in parent_public["trace"]["observations"]
                                    if item["id"] == prompt_context["id"]), None)
                if prompt_node:
                    chain.insert(0, dict(prompt_node))
            if not chain:
                chain.append({
                    "id": prompt_context["id"], "parent_id": None, "session_id": prompt_context["session_id"],
                    "kind": "prompt", "summary": prompt_context["summary"], "source": prompt_context["source"],
                    "timestamp": prompt_context["timestamp"], "status": "working", "detail": prompt_context["text"],
                })
            chain = [item for item in chain
                     if item["kind"] != "prompt" or item["id"] == prompt_context["id"]]
            if not chain or chain[0]["id"] != prompt_context["id"]:
                chain = [item for item in chain if item["id"] != prompt_context["id"]]
                chain.insert(0, {
                    "id": prompt_context["id"], "parent_id": None, "session_id": prompt_context["session_id"],
                    "kind": "prompt", "summary": prompt_context["summary"], "source": prompt_context["source"],
                    "timestamp": prompt_context["timestamp"], "status": "working", "detail": prompt_context["text"],
                })
            chain[0]["parent_id"] = None
            chain[0]["source"] = prompt_context["source"]
            chain_ids = {chain[0]["id"]}
            for index in range(1, len(chain)):
                if chain[index].get("parent_id") not in chain_ids:
                    chain[index]["parent_id"] = chain[index - 1]["id"]
                chain_ids.add(chain[index]["id"])

            parent_node_id = chain[-1]["id"]
            agent_id = f"{session_id}:agent:session"
            agent = {
                "id": agent_id, "parent_id": parent_node_id, "session_id": session_id,
                "kind": "agent", "summary": assignment, "source": source,
                "timestamp": row.get("_session_timestamp") or chain[-1].get("timestamp", ""),
                "status": row.get("status", "idle"), "detail": assignment,
            }
            local_root = base["trace"]["root_id"]
            local = []
            for cached in base["trace"]["observations"]:
                if cached["id"] == local_root:
                    continue
                item = dict(cached)
                if item.get("parent_id") in (None, local_root):
                    item["parent_id"] = agent_id
                local.append(item)

            observations = []
            for item in [*chain, agent, *local]:
                if item["id"] not in {current["id"] for current in observations}:
                    observations.append(item)
            required = {item["id"] for item in chain} | {agent_id}
            truncated = base["trace"]["truncated"] or len(observations) > 120
            tail = observations[-120:]
            observation_map = {item["id"]: item for item in observations}
            for item in tail:
                cursor = item
                seen = set()
                while cursor and cursor["id"] not in seen:
                    seen.add(cursor["id"])
                    required.add(cursor["id"])
                    cursor = observation_map.get(cursor.get("parent_id"))
            tail_ids = {item["id"] for item in tail}
            observations = [item for item in observations
                            if item["id"] in required and item["id"] not in tail_ids] + tail

            task_path = [prompt_context["summary"]]
            for item in [*chain, agent]:
                if item["kind"] in ("delegation", "agent") and item["summary"] not in task_path:
                    task_path.append(item["summary"])
            working = {"summary": assignment, "source": source, "observation_id": agent_id,
                       "status": row.get("status", "idle"), "task_path": task_path}
            semantic_kinds = {"prompt", "agent", "step", "delegation", "outcome"}
            public = {
                "prompt_context": prompt_context, "working_on": working,
                "work_breakdown": [item for item in observations if item["kind"] in semantic_kinds],
                "trace": {"version": 1, "root_id": prompt_context["id"],
                          "observations": observations, "truncated": truncated},
            }
            row.update(public)
            resolved[session_id] = public
            return public

        for row in rows:
            row["_semantic_source"] = row["_semantic"]
        for row in rows:
            resolve(row["id"], set())
        for row in rows:
            row.pop("_semantic_source", None)
            row.pop("_semantic", None)

    @staticmethod
    def recent(path):
        model = ""
        collaboration = ""
        last_event = ""
        token_usage = {}
        last_token_usage = {}
        context_window = None
        rate_limits = {}
        last_tool = ""
        last_tool_status = ""
        turn_open = False
        turn_id = ""
        tool_calls = {}
        activities = []
        phase = ""
        pending_approvals = set()
        event_time = ""
        for row in read_json_lines(path, 768 * 1024):
            payload = row.get("payload", {})
            stamp = row.get("timestamp", "")
            if row.get("type") == "turn_context":
                model = payload.get("model") or model
                mode = payload.get("collaboration_mode")
                if isinstance(mode, dict):
                    mode = mode.get("mode") or mode.get("kind") or ""
                collaboration = mode or collaboration
            elif row.get("type") == "event_msg":
                kind = payload.get("type", "")
                if kind:
                    last_event, event_time = kind, stamp
                item = payload.get("item") or {}
                item = item if isinstance(item, dict) else {}
                item_kind = item.get("type", "")
                phase_names = {
                    "task_started": "Starting the turn", "reasoning_delta": "Thinking through the task",
                    "agent_message_delta": "Writing a response", "agent_message": "Writing a response",
                    "task_complete": "Turn complete", "task_completed": "Turn complete",
                    "turn_completed": "Turn complete", "turn_aborted": "Turn interrupted",
                    "task_failed": "Turn failed", "user_message": "Reviewing instructions",
                }
                if kind in phase_names:
                    phase = phase_names[kind]
                # Only explicit runtime signals imply approval; requesting escalation is not proof.
                if kind in ("exec_approval_request", "apply_patch_approval_request", "approval_request"):
                    pending_approvals.add(payload.get("call_id") or payload.get("id") or "unidentified")
                elif kind in ("approval_response", "exec_approval_response", "apply_patch_approval_response"):
                    approval_id = payload.get("call_id") or payload.get("id")
                    if approval_id:
                        pending_approvals.discard(approval_id)
                    else:
                        pending_approvals.clear()
                elif kind == "task_started" or kind in FINAL_EVENTS:
                    pending_approvals.clear()
                if kind in ("item_started", "item_completed"):
                    if item_kind == "Reasoning":
                        phase = "Thinking through the task"
                    elif item_kind == "AgentMessage":
                        phase = "Writing a response"
                    elif item_kind == "McpToolCall":
                        name = ".".join(filter(None, (item.get("server"), item.get("tool")))) or "tool"
                        detail = CodexStore.tool_detail(name, {"arguments": item.get("arguments") or {}})
                        last_tool = name
                        last_tool_status = "running" if kind == "item_started" else item.get("status", "completed")
                        phase = CodexStore.action_detail(detail) if kind == "item_started" else "Latest tool finished: " + CodexStore.action_detail(detail)
                        call_id = item.get("id")
                        activity = tool_calls.get(call_id)
                        if activity is None:
                            activity = {"time": stamp, "tool": name, "detail": detail}
                            tool_calls[call_id] = activity
                            activities.append(activity)
                        activity["status"] = last_tool_status
                    if item.get("status") in ("awaitingApproval", "pendingApproval", "waiting_for_approval"):
                        pending_approvals.add(item.get("id") or "unidentified")
                    elif kind == "item_completed":
                        pending_approvals.discard(item.get("id"))
                if kind == "token_count":
                    info = payload.get("info") or {}
                    token_usage = info.get("total_token_usage") or token_usage
                    last_token_usage = info.get("last_token_usage") or last_token_usage
                    context_window = info.get("model_context_window") or context_window
                    rate_limits = payload.get("rate_limits") or rate_limits
                if kind == "task_started":
                    turn_open, turn_id = True, payload.get("turn_id", "")
                elif kind in FINAL_EVENTS:
                    turn_open = False
            elif row.get("type") == "response_item" and payload.get("type") in ("custom_tool_call", "function_call"):
                call_id = payload.get("call_id") or payload.get("id")
                name = payload.get("name", "tool")
                detail = CodexStore.tool_detail(name, payload)
                activity = {"time": stamp, "tool": name, "detail": detail, "status": "running"}
                tool_calls[call_id] = activity
                activities.append(activity)
                last_tool, last_tool_status = name, "running"
                phase = CodexStore.action_detail(detail)
                event_time = stamp
            elif row.get("type") == "response_item" and payload.get("type") in ("custom_tool_call_output", "function_call_output"):
                call_id = payload.get("call_id")
                pending_approvals.discard(call_id)
                if call_id in tool_calls:
                    activity = tool_calls[call_id]
                    activity["status"] = "completed"
                    last_tool, last_tool_status = activity["tool"], "completed"
                    phase = "Latest tool finished: " + CodexStore.action_detail(activity["detail"])
                event_time = stamp
        return {
            "model": model, "collaboration": collaboration, "last_event": last_event,
            "event_time": event_time, "turn_open": turn_open, "turn_id": turn_id,
            "last_tool": last_tool, "last_tool_status": last_tool_status,
            "current_action": "Approval required — review this task in Codex" if pending_approvals else phase or ("Working on the task" if turn_open else "Waiting for another turn"),
            "approval_pending": bool(pending_approvals),
            "recent_activity": activities[-4:],
            "token_usage": token_usage, "last_token_usage": last_token_usage,
            "context_window": context_window, "rate_limits": rate_limits,
        }

    @staticmethod
    def account_summary(rows):
        """Combine non-secret provider telemetry from active rollout logs."""
        providers = sorted({row.get("provider", "") for row in rows if row.get("provider")})
        totals = {
            "input_tokens": 0, "cached_input_tokens": 0, "cache_write_input_tokens": 0,
            "output_tokens": 0, "reasoning_output_tokens": 0, "total_tokens": 0,
        }
        for row in rows:
            usage = row.get("token_usage") or {}
            for key in totals:
                value = usage.get(key, 0)
                if isinstance(value, (int, float)) and not isinstance(value, bool):
                    totals[key] += value

        newest_limits, limits_provider = {}, ""
        for row in sorted(rows, key=lambda item: item.get("age_seconds", float("inf"))):
            if row.get("rate_limits"):
                newest_limits = row["rate_limits"]
                limits_provider = row.get("provider") or ""
                break

        return {
            "provider": limits_provider,
            "tracked_providers": providers,
            "plan": newest_limits.get("plan_type") or "",
            "token_usage": totals,
            "rate_limits": newest_limits,
            "scope": "tracked sessions",
            "token_usage_scope": "tracked sessions",
            "rate_limits_scope": "provider account",
        }

    def snapshot(self):
        files = self.session_files()
        active = self.active_ids()
        active_paths = {files.get(item) for item in active}
        self._semantic_cache = {key: value for key, value in self._semantic_cache.items() if key in active}
        self._spawn_tasks = {key: value for key, value in self._spawn_tasks.items() if key[0] in active}
        self._recent_cache = {key: value for key, value in self._recent_cache.items() if key in active_paths}
        titles = self.titles()
        db_threads, db_parents, db_turns, db_durations = self.database_rows(active)
        active = {thread_id for thread_id in active
                  if self.source_fields(db_threads.get(thread_id, {}))["kind"] != "guardian_review"
                  and (not files.get(thread_id) or
                       self.source_fields(self.metadata(files[thread_id]))["kind"] != "guardian_review")}
        now = time.time()
        rows = []
        semantics = {}
        for thread_id in active:
            path = files.get(thread_id)
            if path:
                record = db_threads.get(thread_id, {})
                fallback = record.get("first_user_message") or record.get("title") or titles.get(thread_id, "")
                semantics[thread_id] = self.semantic_cached(path, thread_id, fallback)
        for thread_id in sorted(active):
            path = files.get(thread_id)
            record = db_threads.get(thread_id, {})
            edge = db_parents.get(thread_id, {})
            if not path:
                parent = edge.get("parent_thread_id", "")
                title = record.get("title") or titles.get(thread_id, "")
                task = record.get("first_user_message") or title
                rows.append({
                    "id": thread_id, "parent": parent, "harness": "codex",
                    "kind": "subagent" if parent or record.get("agent_path") else "user",
                    "name": record.get("agent_path") or compact_text(title, 72) or thread_id[:8],
                    "title": compact_text(title, 160), "task": task,
                    "cwd": record.get("cwd", ""), "model": record.get("model", ""),
                    "effort": record.get("reasoning_effort", ""), "status": "idle",
                    "current_action": "Session starting; activity log is not available yet",
                    "recent_activity": [], "age_seconds": 0, "missing_log": True,
                    "_semantic": self.semantic_trace(path or self.sessions_dir / "missing", thread_id, task),
                    "_session_timestamp": "",
                })
                continue
            try:
                stat = path.stat()
            except OSError:
                continue
            meta = self.metadata(path)
            source = self.source_fields(meta)
            if edge.get("parent_thread_id"):
                source["parent"] = edge["parent_thread_id"]
            recent_key = (stat.st_size, stat.st_mtime_ns)
            cached_recent = self._recent_cache.get(path)
            if cached_recent is None or cached_recent[0] != recent_key:
                cached_recent = (recent_key, self.recent(path))
                self._recent_cache[path] = cached_recent
            recent = copy.deepcopy(cached_recent[1])
            updated = (record.get("updated_at_ms") or 0) / 1000 or stat.st_mtime
            age = max(0.0, now - updated)
            turn = db_turns.get(thread_id, {})
            turn_status = turn.get("status", "")
            status = self.session_status(turn_status, source.get("kind"), recent["last_tool_status"], age)
            stale = turn_status == "inProgress" and status == "idle"
            if recent["approval_pending"] and not stale:
                status = "approval"
            if stale:
                recent["current_action"] = "No recent activity; thread is still tracked by Codex"
                if recent["last_tool_status"] == "running":
                    recent["last_tool_status"] = "stale"
                for activity in recent["recent_activity"]:
                    if activity.get("status") == "running":
                        activity["status"] = "stale"
            title = record.get("title") or titles.get(thread_id, "")
            agent_path = record.get("agent_path") or source.get("path", "")
            parent = source.get("parent", "")
            task = self.delegated_task(semantics.get(parent), agent_path) if parent and agent_path else ""
            if parent and agent_path and not task:
                task = self.spawn_task(parent, agent_path)
            if source.get("kind") == "subagent":
                task = task or self.task_from_head(path, True) or agent_path.rsplit("/", 1)[-1].replace("_", " ")
            else:
                task = record.get("first_user_message") or title
            progress, eta_seconds, estimate_basis = self.estimate(status, turn, db_durations.get(thread_id, []), now)
            rows.append({
                "id": thread_id, "parent": parent, "depth": source.get("depth", 0),
                "kind": source.get("kind", "user"), "agent_path": agent_path,
                "nickname": record.get("agent_nickname") or source.get("nickname", ""),
                "name": agent_path or compact_text(title, 72) or thread_id[:8],
                "title": compact_text(title, 160), "task": compact_text(task), "cwd": record.get("cwd") or meta.get("cwd", ""),
                "model": record.get("model") or recent["model"], "effort": record.get("reasoning_effort") or "",
                "harness": "codex", "provider": meta.get("model_provider", ""),
                "collaboration": recent["collaboration"], "turn_status": turn_status,
                "progress": progress, "eta_seconds": eta_seconds, "estimate_basis": estimate_basis,
                "status": status, "last_event": recent["last_event"], "last_tool": recent["last_tool"],
                "last_tool_status": recent["last_tool_status"], "updated": iso(updated),
                "age_seconds": round(age, 1), "bytes": stat.st_size, "stale": stale,
                "current_action": recent["current_action"], "recent_activity": recent["recent_activity"],
                "approval_pending": recent["approval_pending"] and not stale,
                "turn_elapsed_seconds": (max(0, round(now - turn["started_at"]))
                                         if turn.get("started_at") and status in ("working", "tool", "thinking") else None),
                "token_usage": recent["token_usage"], "last_token_usage": recent["last_token_usage"],
                "context_window": recent["context_window"], "rate_limits": recent["rate_limits"],
                "_semantic": semantics[thread_id], "_session_timestamp": iso(stat.st_mtime),
            })
        self.attach_semantics(rows)
        roots = sum(1 for row in rows if not row.get("parent"))
        return {
            "generated": dt.datetime.now().astimezone().isoformat(timespec="milliseconds"),
            "adapter": "codex", "source": str(self.home), "sessions": rows,
            "account": self.account_summary(rows),
            "counts": {"sessions": roots, "agents": len(rows) - roots,
                       "working": sum(r["status"] in ("working", "tool", "thinking") for r in rows)},
        }

    def signature(self):
        parts = []
        files = self.session_files()
        for thread_id in sorted(self.active_ids()):
            path = files.get(thread_id)
            try:
                stat = path.stat()
                parts.append((thread_id, stat.st_size, stat.st_mtime_ns))
            except (AttributeError, OSError):
                parts.append((thread_id, 0, 0))
        for path in (self.index_path, self.state_db, self.state_db.with_name(self.state_db.name + "-wal"),
                     self.history_db, self.history_db.with_name(self.history_db.name + "-wal")):
            try:
                stat = path.stat()
                parts.append((path.name, stat.st_size, stat.st_mtime_ns))
            except OSError:
                pass
        return tuple(parts)


class JsonStore:
    """Adapter for any runner that can atomically write the documented JSON state schema."""
    def __init__(self, path):
        self.path = path

    def snapshot(self):
        try:
            state_value = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            state_value = {"sessions": []}
        rows = state_value.get("sessions", []) if isinstance(state_value, dict) else []
        rows = [row for row in rows if isinstance(row, dict)]
        provided = {}
        for index, row in enumerate(rows):
            supplied = set(row)
            row.setdefault("id", f"agent-{index}")
            row.setdefault("name", row["id"])
            row.setdefault("kind", "subagent" if row.get("parent") else "user")
            row.setdefault("status", "idle")
            row.setdefault("task", "")
            row.setdefault("progress", None)
            row.setdefault("eta_seconds", None)
            row.setdefault("estimate_basis", "provided by runner" if row.get("progress") is not None else "not provided")
            row.setdefault("age_seconds", 0)
            row.setdefault("bytes", 0)
            provided[row["id"]] = supplied
            fallback = CodexStore.semantic_trace(
                self.path.with_name(self.path.name + ".semantic-fallback"), row["id"],
                row.get("task") or row.get("name") or row["id"],
            )
            semantic = CodexStore._public_semantic(fallback, row["status"])
            if row.get("current_action") and "working_on" not in supplied:
                prompt = semantic["prompt_context"]
                step = {
                    "id": f"{row['id']}:step:metadata", "parent_id": prompt["id"],
                    "session_id": row["id"], "kind": "step",
                    "summary": compact_text(row["current_action"]), "source": "metadata_fallback",
                    "timestamp": "", "status": row["status"],
                    "detail": compact_text(row["current_action"], 12000),
                }
                semantic["trace"]["observations"].append(step)
                semantic["work_breakdown"].append(step)
                semantic["working_on"] = {
                    "summary": step["summary"], "source": "metadata_fallback",
                    "observation_id": step["id"], "status": row["status"],
                    "task_path": [prompt["summary"], step["summary"]],
                }
                if "trace" in supplied:
                    semantic["working_on"]["observation_id"] = row["trace"].get("root_id") or prompt["id"]
            for key, semantic_value in semantic.items():
                row.setdefault(key, semantic_value)

        by_session = {row["id"]: row for row in rows}
        resolved = set()

        def resolve(row, visiting):
            if row["id"] in resolved or row["id"] in visiting:
                return
            parent = by_session.get(row.get("parent"))
            if parent:
                resolve(parent, visiting | {row["id"]})
            supplied = provided[row["id"]]
            if parent and "prompt_context" not in supplied:
                prompt = dict(parent["prompt_context"])
                prompt["source"] = "prompt_fallback"
                row["prompt_context"] = prompt
            prompt = row["prompt_context"]
            assignment = compact_text(row.get("task") or row.get("name") or row["id"])
            agent = None
            if parent and "working_on" not in supplied:
                agent = {
                    "id": f"{row['id']}:agent:metadata", "parent_id": parent["working_on"]["observation_id"],
                    "session_id": row["id"], "kind": "agent", "summary": assignment,
                    "source": "metadata_fallback", "timestamp": "", "status": row["status"],
                    "detail": assignment,
                }
                prefix = (parent["working_on"].get("task_path") if parent.get("parent")
                          else [prompt["summary"]])
                row["working_on"] = {
                    "summary": assignment, "source": "metadata_fallback",
                    "observation_id": agent["id"], "status": row["status"],
                    "task_path": list(dict.fromkeys([*prefix, assignment])),
                }

            prompt_node = {
                "id": prompt["id"], "parent_id": None, "session_id": prompt["session_id"],
                "kind": "prompt", "summary": prompt["summary"], "source": prompt["source"],
                "timestamp": prompt["timestamp"], "status": "working", "detail": prompt["text"],
            }
            if "work_breakdown" not in supplied:
                if parent:
                    nodes = [dict(item) for item in parent.get("work_breakdown", [])]
                    if not any(item.get("id") == prompt["id"] for item in nodes):
                        nodes.insert(0, prompt_node)
                    if agent:
                        if agent["parent_id"] not in {item.get("id") for item in nodes}:
                            agent["parent_id"] = prompt["id"]
                        nodes.append(agent)
                    row["work_breakdown"] = nodes
                elif "trace" in supplied:
                    row["work_breakdown"] = [dict(item) for item in row["trace"].get("observations", [])
                                             if item.get("kind") in {"prompt", "agent", "step", "delegation", "outcome"}]
            if "trace" not in supplied:
                observations = [dict(item) for item in row["work_breakdown"]]
                row["trace"] = {"version": 1, "root_id": prompt["id"],
                                "observations": observations, "truncated": False}
            resolved.add(row["id"])

        for row in rows:
            resolve(row, set())
        roots = sum(1 for row in rows if not row.get("parent"))
        account = state_value.get("account", {}) if isinstance(state_value, dict) else {}
        return {"generated": dt.datetime.now().astimezone().isoformat(timespec="milliseconds"),
                "adapter": "json", "source": str(self.path), "sessions": rows,
                "account": account if isinstance(account, dict) else {},
                "counts": {"sessions": roots, "agents": len(rows) - roots,
                           "working": sum(row.get("status") in ("working", "tool", "thinking") for row in rows)}}

    def signature(self):
        try:
            stat = self.path.stat()
            return ((str(self.path), stat.st_size, stat.st_mtime_ns),)
        except OSError:
            return ((str(self.path), 0, 0),)


def signature(store):
    return store.signature()


def handler_for(store, app=None, desktop_key=None):
    token = secrets.token_urlsafe(32)
    feed_lock = Lock()
    feed_signature, feed_payload, feed_checked = None, b"", 0.0

    def event_snapshot():
        nonlocal feed_signature, feed_payload, feed_checked
        # One immutable payload per source change, shared by every existing SSE consumer.
        # No new poller: callers retain the existing quarter-second observation cadence.
        with feed_lock:
            now = time.monotonic()
            if not feed_payload or now - feed_checked >= 0.25:
                current = (signature(store), int(time.time() // 30))
                if current != feed_signature:
                    feed_payload = json.dumps(store.snapshot(), ensure_ascii=False, separators=(",", ":")).encode("utf-8")
                    feed_signature = current
                feed_checked = time.monotonic()
            return feed_signature, feed_payload

    class Handler(BaseHTTPRequestHandler):
        def private_desktop_request(self):
            if desktop_key is None:
                return True
            keys = self.headers.get_all("X-Foundry-Desktop", [])
            return len(keys) == 1 and secrets.compare_digest(keys[0].encode(), desktop_key.encode())

        def setup(self):
            self.nonce = secrets.token_urlsafe(24)
            super().setup()

        def trusted_request(self):
            if not self.private_desktop_request():
                return False
            hosts = self.headers.get_all("Host", [])
            address, port = self.server.server_address[:2]
            local = self.connection.getsockname()[0]
            allowed = {f"{host}:{port}" for host in (address, local) if host not in ("0.0.0.0", "::")}
            if local == "127.0.0.1":
                allowed.add(f"localhost:{port}")
            if len(hosts) != 1 or hosts[0].lower() not in allowed:
                return False
            origins = self.headers.get_all("Origin", [])
            return not origins or origins == ["http://" + hosts[0]]

        def end_headers(self):
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Referrer-Policy", "no-referrer")
            self.send_header("Content-Security-Policy", "default-src 'self'; script-src 'nonce-" + self.nonce +
                             "'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; "
                             "object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
            self.send_header("X-Frame-Options", "DENY")
            self.send_header("Cross-Origin-Resource-Policy", "same-origin")
            super().end_headers()

        def send_bytes(self, content, content_type, status=200):
            if content_type.startswith("text/html"):
                content = re.sub(rb"<script(?=[\s>])", ('<script nonce="' + self.nonce + '"').encode(), content)
                if app is not None:
                    shared = ('<script nonce="' + self.nonce + '" src="/foundry.js"></script>').encode()
                    # Load finite dependencies before an inline page reserves a long-lived SSE connection.
                    anchor = b"<script" if b"<script" in content else b"</body>"
                    content = content.replace(anchor, shared + anchor, 1)
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(content)))
            self.end_headers()
            self.wfile.write(content)

        def send_json(self, value, status=200):
            self.send_bytes(json.dumps(value, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8", status)

        def do_POST(self):
            authority = f"127.0.0.1:{self.server.server_port}"
            credentials = self.headers.get_all("X-Foundry-Token", [])
            if (not self.private_desktop_request() or app is None or self.server.server_address[0] != "127.0.0.1" or
                    self.headers.get_all("Host", []) != [authority] or
                    self.headers.get_all("Origin", []) != ["http://" + authority] or
                    len(credentials) != 1 or not secrets.compare_digest(credentials[0].encode(), token.encode())):
                self.send_json({"error": "forbidden"}, 403)
                return
            route = urlparse(self.path).path
            lengths = self.headers.get_all("Content-Length", [])
            content_types = self.headers.get_all("Content-Type", [])
            if (self.headers.get("Transfer-Encoding") is not None or len(lengths) != 1 or
                    len(lengths[0]) > 8 or not lengths[0].isascii() or not lengths[0].isdigit() or len(content_types) != 1 or
                    content_types[0].lower() not in ("application/json", "application/json; charset=utf-8")):
                self.send_json({"error": "invalid_request"}, 400)
                return
            limit = 12 * 1024 * 1024 if route == "/api/library/import" else 8192 if route == "/api/actions" else 1024 * 1024
            size = int(lengths[0])
            if not 0 < size <= limit:
                self.send_json({"error": "body_too_large"}, 413)
                return
            def unique_object(pairs):
                result = {}
                for key, value in pairs:
                    if key in result:
                        raise ValueError("duplicate_key")
                    result[key] = value
                return result
            def finite_float(value):
                number = float(value)
                if not math.isfinite(number):
                    raise ValueError("nonfinite_number")
                return number
            try:
                self.connection.settimeout(5)
                raw = self.rfile.read(size)
                if len(raw) != size:
                    raise ValueError("incomplete_body")
                payload = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object,
                                     parse_float=finite_float,
                                     parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
                if not isinstance(payload, dict):
                    raise ValueError("invalid_request")
                pending = [(payload, 0)]
                while pending:
                    value, depth = pending.pop()
                    if depth > 32:
                        raise ValueError("excessive_nesting")
                    if isinstance(value, (dict, list)):
                        pending.extend((child, depth + 1) for child in (value.values() if isinstance(value, dict) else value))
            except (ValueError, UnicodeError, TimeoutError, OSError, RecursionError):
                self.send_json({"error": "invalid_request"}, 400)
                return
            finally:
                self.connection.settimeout(None)
            try:
                result = app.dispatch(route, payload)
                if route == "/api/settings" and isinstance(result, dict):
                    self.server.verbose = result.get("configuration", {}).get("verbose", False)
                if isinstance(result, bytes):
                    self.send_bytes(result, "application/zip")
                else:
                    self.send_json(result)
            except Exception as error:
                code = getattr(error, "code", "invalid_request" if isinstance(error, (TypeError, ValueError)) else "operation_failed")
                status = 409 if code in ("request_conflict", "stale_revision", "thread_busy") else 429 if code == "rate_limited" else 400
                self.send_json({"error": code}, status)

        def do_GET(self):
            if not self.trusted_request():
                self.send_bytes(b"Forbidden request origin", "text/plain; charset=utf-8", 403)
                return
            route = urlparse(self.path).path
            if route in ("/", "/index.html"):
                self.send_bytes((HERE / "index.html").read_bytes(), "text/html; charset=utf-8")
            elif route in ("/observatory", "/observatory.html"):
                self.send_bytes((HERE / "observatory.html").read_bytes(), "text/html; charset=utf-8")
            elif route in ("/settings", "/library") and app is not None:
                self.send_bytes((HERE / "manage.html").read_bytes(), "text/html; charset=utf-8")
            elif route == "/foundry.js" and app is not None:
                self.send_bytes((HERE / "foundry.js").read_bytes(), "text/javascript; charset=utf-8")
            elif route == "/foundry.css":
                self.send_bytes((HERE / "foundry.css").read_bytes(), "text/css; charset=utf-8")
            elif route == "/api/bootstrap" and app is not None:
                self.send_json({**app.bootstrap(), "token": token})
            elif route.startswith("/api/library") and app is not None:
                try:
                    self.send_json(app.read_library(self.path))
                except Exception as error:
                    self.send_json({"error": getattr(error, "code", "not_found")}, 404)
            elif route == "/api/state":
                body = json.dumps(store.snapshot(), ensure_ascii=False).encode("utf-8")
                self.send_bytes(body, "application/json; charset=utf-8")
            elif route == "/api/events":
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Cache-Control", "no-cache")
                self.send_header("Connection", "keep-alive")
                self.end_headers()
                previous = None
                heartbeat = 0.0
                try:
                    while True:
                        current, data = event_snapshot()
                        now = time.monotonic()
                        if current != previous:
                            self.wfile.write(b"event: state\ndata: " + data + b"\n\n")
                            self.wfile.flush()
                            previous, heartbeat = current, now
                        elif now - heartbeat >= 5:
                            self.wfile.write(b": heartbeat\n\n")
                            self.wfile.flush()
                            heartbeat = now
                        time.sleep(0.25)
                except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                    pass
            elif route == "/api/health":
                self.send_bytes(b'{"ok":true}', "application/json")
            else:
                self.send_bytes(b"not found", "text/plain; charset=utf-8", 404)

        def log_message(self, fmt, *args):
            if getattr(self.server, "verbose", False):
                super().log_message(fmt, *args)

    return Handler


def main():
    from foundry import FoundryApp, default_data_dir, load_config
    from desktop_runtime import DataDirectoryLease, read_handshake, require_normal_windows_token
    default_home = Path(os.environ.get("CODEX_HOME") or Path.home() / ".codex")
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--data-dir", type=Path, default=default_data_dir(), help="Agent Foundry settings and private library folder")
    parser.add_argument("--codex-home", type=Path)
    parser.add_argument("--adapter", choices=("codex", "json"))
    parser.add_argument("--state-file", type=Path, help="JSON state file when --adapter json is selected")
    parser.add_argument("--host")
    parser.add_argument("--port", type=int)
    parser.add_argument("--open", action="store_true", default=None, help="open the monitor in the default browser")
    parser.add_argument("--verbose", action="store_true", default=None)
    parser.add_argument("--desktop", action="store_true", help="private owned native-host mode; requires inherited stdin handshake")
    args = parser.parse_args()
    config = {"adapter": "codex", "codex_home": str(default_home), "state_file": "", "host": "127.0.0.1",
              "port": 8777, "open": False, "verbose": False}
    try:
        config.update(load_config(args.data_dir))
    except (ValueError, OSError):
        parser.error("Settings file is invalid; restore it or use a different --data-dir.")
    for key in config:
        value = getattr(args, key, None)
        if value is not None:
            config[key] = str(value) if isinstance(value, Path) else value
    if config["host"] != "127.0.0.1":
        parser.error("Agent Foundry interactive features require exact --host 127.0.0.1.")
    if type(config["port"]) is not int or not 1024 <= config["port"] <= 65535:
        parser.error("Port must be between 1024 and 65535.")
    if config["adapter"] == "json":
        if not config["state_file"]:
            parser.error("--state-file is required with --adapter json")
        store = JsonStore(Path(config["state_file"]).resolve())
    else:
        if not Path(config["codex_home"]).is_dir():
            parser.error("Codex data folder does not exist.")
        store = CodexStore(Path(config["codex_home"]).resolve())
    desktop_key = None
    if args.desktop:
        import sys
        require_normal_windows_token()
        desktop_key = read_handshake(sys.stdin.buffer)
    with DataDirectoryLease(args.data_dir):
        app = FoundryApp(store, config, args.data_dir)
        server = ThreadingHTTPServer((config["host"], 0 if args.desktop else config["port"]),
                                     handler_for(app, app, desktop_key))
        app.runtime_port = server.server_port
        server.daemon_threads = True
        server.verbose = config["verbose"] and not args.desktop
        if args.desktop:
            import sys
            from threading import Thread
            print(json.dumps({"port": server.server_port, "pid": os.getpid()}), flush=True)
            def parent_lifetime():
                # No control channel after the handshake. EOF means the owner is gone.
                sys.stdin.buffer.read(1)
                server.shutdown()
            Thread(target=parent_lifetime, daemon=True).start()
        else:
            print(f"Agent Foundry — Operations: http://127.0.0.1:{server.server_port}/")
            print(f"Adapter: {config['adapter']} (Ctrl+C to stop)")
            if config["open"]:
                webbrowser.open(f"http://127.0.0.1:{server.server_port}/")
        try:
            server.serve_forever(poll_interval=0.1)
        except KeyboardInterrupt:
            pass
        finally:
            server.server_close()


if __name__ == "__main__":
    main()

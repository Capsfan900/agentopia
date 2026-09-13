import json
import tempfile
import unittest
from http.client import HTTPConnection
from http.server import ThreadingHTTPServer
from pathlib import Path
from threading import Thread

from monitor import CodexStore, JsonStore, handler_for


class ActivitySummaryTests(unittest.TestCase):
    ROOT_ID = "11111111-1111-1111-1111-111111111111"
    CHILD_ID = "22222222-2222-2222-2222-222222222222"
    GRANDCHILD_ID = "33333333-3333-3333-3333-333333333333"

    def test_large_history_reads_bounded_context_and_latest_work(self):
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "large.jsonl"
            old = {"type": "event_msg", "payload": {"type": "user_message", "message": "Recorded purpose"}}
            filler = {"type": "ignored", "payload": {"padding": "x" * 1200}}
            latest = {"type": "event_msg", "payload": {"type": "agent_message", "message": "Latest work"}}
            path.write_text(json.dumps(old) + "\n" + (json.dumps(filler) + "\n") * 2000 + json.dumps(latest), encoding="utf-8")
            with patch("monitor.json.loads", wraps=json.loads) as decode:
                trace = CodexStore.semantic_trace(path, self.ROOT_ID)
            self.assertLess(decode.call_count, 500, "A changed log must not reparse its entire history")
            self.assertTrue(trace["trace"]["truncated"])
            self.assertEqual(trace["prompt_context"]["text"], "Recorded purpose")
            self.assertEqual(trace["working_on"]["summary"], "Latest work")

    def test_trace_budget_still_holds_if_log_grows_after_stat(self):
        import io
        from unittest.mock import Mock
        from monitor import read_json_lines
        path = Mock()
        path.stat.return_value.st_size = 3
        path.open.return_value = io.BytesIO(b'{}\n' * 1000)
        self.assertEqual(len(list(read_json_lines(path, tail_bytes=18, head_bytes=12))), 10)

    def test_head_only_budget_ignores_appends_after_seeking_end(self):
        import io
        from unittest.mock import Mock
        from monitor import read_json_lines
        class GrowingStream(io.BytesIO):
            def seek(self, offset, whence=0):
                position = super().seek(offset, whence)
                if whence == 2:
                    super().write(b'{}\n' * 1000)
                    super().seek(position)
                return position
        path = Mock()
        path.stat.return_value.st_size = 30
        path.open.return_value = GrowingStream(b'{}\n' * 10)
        self.assertEqual(len(list(read_json_lines(path, head_bytes=30))), 10)

    def test_tail_read_stays_bounded_if_log_grows_after_stat(self):
        import io
        from unittest.mock import Mock
        from monitor import read_json_lines
        path = Mock()
        path.stat.return_value.st_size = 3
        path.open.return_value = io.BytesIO(b'{}\n' * 1000)
        self.assertEqual(len(list(read_json_lines(path, tail_bytes=18))), 6)

    def test_unchanged_logs_reuse_recent_activity_without_aliasing(self):
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as folder:
            home = Path(folder); (home / "sessions").mkdir(); (home / "thread-writer-locks").mkdir()
            path = home / "sessions" / (self.ROOT_ID + ".jsonl")
            path.write_text("\n".join(json.dumps(row) for row in self.rows(
                {"type": "response_item", "payload": {"type": "function_call", "name": "exec_command", "call_id": "one", "arguments": '{"cmd":"echo first"}'}})), encoding="utf-8")
            (home / "thread-writer-locks" / (self.ROOT_ID + ".lock")).touch()
            store = CodexStore(home)
            with patch.object(CodexStore, "recent", wraps=CodexStore.recent) as recent:
                first = store.snapshot(); first["sessions"][0]["recent_activity"][0]["detail"] = "changed by consumer"
                second = store.snapshot()
                self.assertEqual(recent.call_count, 1)
                self.assertNotEqual(second["sessions"][0]["recent_activity"][0]["detail"], "changed by consumer")
                with path.open("a", encoding="utf-8") as stream:
                    stream.write('\n{"type":"event_msg","payload":{"type":"task_started","turn_id":"two"}}\n')
                store.snapshot()
                self.assertEqual(recent.call_count, 2)

    def test_empty_session_discovery_obeys_existing_scan_interval(self):
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as folder:
            store = CodexStore(Path(folder))
            with patch("monitor.time.monotonic", return_value=100), patch.object(Path, "rglob", return_value=iter(())) as scan:
                store.session_files(); store.session_files(); store.signature()
            self.assertEqual(scan.call_count, 1)

    def test_harness_provider_model_and_account_scopes_are_independent(self):
        rows = self.rows({"type": "turn_context", "payload": {"model": "unrelated-model-name"}})
        rows[0]["payload"]["model_provider"] = "openrouter"
        session = self.snapshot({self.ROOT_ID: rows})["sessions"][0]
        self.assertEqual(session["harness"], "codex")
        self.assertEqual(session["provider"], "openrouter")
        self.assertEqual(session["model"], "unrelated-model-name")
        del rows[0]["payload"]["model_provider"]
        self.assertFalse(self.snapshot({self.ROOT_ID: rows})["sessions"][0].get("provider"))
        accounts = [{"provider": "openai", "age_seconds": 1, "token_usage": {"total_tokens": 2}},
                    {"provider": "anthropic", "age_seconds": 2, "token_usage": {"total_tokens": 3},
                     "rate_limits": {"plan_type": "recorded-plan"}}]
        summary = CodexStore.account_summary(accounts)
        self.assertEqual(summary["provider"], "anthropic")
        self.assertEqual(summary["tracked_providers"], ["anthropic", "openai"])
        self.assertEqual(summary["token_usage"]["total_tokens"], 5)
        self.assertEqual(summary["rate_limits_scope"], "provider account")
        del accounts[1]["provider"]
        self.assertFalse(CodexStore.account_summary(accounts)["provider"])

    def parse_events(self, payloads):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'events.jsonl'
            path.write_text('\n'.join(json.dumps({'type': 'event_msg', 'payload': p}) for p in payloads), encoding='utf-8')
            return CodexStore.recent(path)

    def test_nested_activity_and_approval_lifecycle(self):
        start = {'type': 'task_started', 'turn_id': 'turn'}
        request = {'type': 'exec_approval_request', 'call_id': 'call'}
        result = self.parse_events([start, request, {'type': 'token_count'},
                                   {'type': 'item_completed', 'item': {'id': 'other', 'type': 'Reasoning'}}])
        self.assertTrue(result['approval_pending'])
        self.assertIn('Approval required', result['current_action'])
        result = self.parse_events([start, request, {'type': 'approval_response'}])
        self.assertFalse(result['approval_pending'])
        result = self.parse_events([{'type': 'item_completed', 'item': {
            'type': 'McpToolCall', 'id': 'call', 'server': 'codex_app', 'tool': 'wait_threads',
            'status': 'completed', 'arguments': {}, 'result': 'private output'}}])
        self.assertIn('Waiting for delegated agents', result['current_action'])
        self.assertEqual(result['recent_activity'][0]['status'], 'completed')
        self.assertNotIn('private output', json.dumps(result))
        result = self.parse_events([{'type': 'item_completed', 'item': {
            'type': 'Reasoning', 'summary_text': 'private reasoning'}}])
        self.assertEqual(result['current_action'], 'Thinking through the task')
        self.assertNotIn('private reasoning', json.dumps(result))

    def test_command_summary_uses_category_or_safe_fallback(self):
        payload = {"arguments": json.dumps({"cmd": "deploy --api-key=abc123\nsecond line"})}
        summary = CodexStore.tool_detail("exec_command", payload)
        self.assertEqual(summary, "Running a local command")

    def test_passive_tool_telemetry_never_exposes_command_or_patch_bodies(self):
        command_secret, patch_secret = "synthetic-bearer-credential", "synthetic-opaque-credential"
        command = f"curl -H 'Authorization: Bearer {command_secret}' --token {patch_secret}"
        patch = ("*** Begin Patch\n*** Update File: C:/private/monitor.py\n"
                 f"+Authorization: Bearer {command_secret}\n+token={patch_secret}\n*** End Patch")
        command_payload = {"arguments": json.dumps({"cmd": command})}
        patch_payload = {"arguments": patch}
        self.assertEqual(CodexStore.tool_detail("exec_command", command_payload), "Checking a service response")
        self.assertEqual(CodexStore.tool_input_detail("exec_command", command_payload), "Checking a service response")
        self.assertEqual(CodexStore.tool_detail("apply_patch", patch_payload), "Editing monitor.py")
        self.assertEqual(CodexStore.tool_input_detail("apply_patch", patch_payload), "Editing monitor.py")
        recent = self.parse_events([{"type": "response_item", "payload": {
            "type": "function_call", "call_id": "command", "name": "exec_command",
            "arguments": json.dumps({"cmd": command})}}])
        snapshot = self.snapshot({self.ROOT_ID: self.rows(
            {"ordinal": 1, "timestamp": "2026-09-12T12:00:01Z", "type": "event_msg",
             "payload": {"type": "user_message", "message": "Use safe tool evidence."}},
            {"ordinal": 2, "timestamp": "2026-09-12T12:00:02Z", "type": "response_item",
             "payload": {"type": "function_call", "call_id": "command", "name": "exec_command",
                         "arguments": json.dumps({"cmd": command})}},
            {"ordinal": 3, "timestamp": "2026-09-12T12:00:03Z", "type": "response_item",
             "payload": {"type": "function_call", "call_id": "patch", "name": "apply_patch",
                         "arguments": patch}},
        )})
        trace = snapshot["sessions"][0]["trace"]
        for value in (json.dumps(recent), json.dumps(snapshot), json.dumps(trace)):
            self.assertNotIn(command_secret, value)
            self.assertNotIn(patch_secret, value)
            self.assertNotIn("Bearer", value)

    def test_delegation_wrapper_is_reduced_to_its_task(self):
        from monitor import compact_text
        wrapped = "<realtime_delegation><input>Inspect the dashboard.</input><transcript_delta>noise</transcript_delta></realtime_delegation>"
        self.assertEqual(compact_text(wrapped), "Inspect the dashboard.")

    def test_recent_tracks_tool_status_without_reading_message_text(self):
        rows = [
            {"timestamp": "2026-09-08T20:00:00Z", "type": "event_msg",
             "payload": {"type": "task_started", "turn_id": "turn-1"}},
            {"timestamp": "2026-09-08T20:00:01Z", "type": "response_item",
             "payload": {"type": "function_call", "call_id": "call-1", "name": "exec_command",
                         "arguments": json.dumps({"cmd": "python -m unittest"})}},
            {"timestamp": "2026-09-08T20:00:02Z", "type": "response_item",
             "payload": {"type": "function_call_output", "call_id": "call-1", "output": "secret output"}},
            {"timestamp": "2026-09-08T20:00:03Z", "type": "event_msg",
             "payload": {"type": "token_count", "info": {"last_token_usage": {"total_tokens": 42},
                         "model_context_window": 1000}}},
        ]
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "rollout.jsonl"
            path.write_text("\n".join(json.dumps(row) for row in rows), encoding="utf-8")
            result = CodexStore.recent(path)

        self.assertEqual(result["last_tool_status"], "completed")
        self.assertEqual(result["recent_activity"][0]["detail"], "Running automated tests")
        self.assertEqual(result["recent_activity"][0]["status"], "completed")
        self.assertNotIn("secret output", json.dumps(result))
        self.assertEqual(result["last_token_usage"]["total_tokens"], 42)

    def test_stale_in_progress_turn_is_idle(self):
        self.assertEqual(CodexStore.session_status("inProgress", "user", "running", 1800), "idle")

    def test_recent_in_progress_turn_keeps_working_status(self):
        self.assertEqual(CodexStore.session_status("inProgress", "user", "running", 1799), "tool")
        self.assertEqual(CodexStore.session_status("inProgress", "user", "completed", 1799), "working")

    def test_terminal_root_turns_stay_terminal_for_done_and_folding(self):
        for source in ("user", "subagent"):
            for status in ("completed", "failed", "interrupted"):
                self.assertEqual(CodexStore.session_status(status, source, "completed", 4000), status)

    def test_current_item_events_have_human_activity(self):
        rows = [
            {"timestamp": "2026-09-12T12:00:00Z", "type": "event_msg",
             "payload": {"type": "task_started", "turn_id": "turn-1"}},
            {"timestamp": "2026-09-12T12:00:01Z", "type": "event_msg",
             "payload": {"type": "item_started", "item": {"type": "Reasoning"}}},
            {"timestamp": "2026-09-12T12:00:02Z", "type": "event_msg",
             "payload": {"type": "item_started", "item": {"type": "McpToolCall",
                         "server": "mcp__codex_app", "tool": "wait_threads", "arguments": {}}}},
        ]
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "rollout.jsonl"
            path.write_text("\n".join(json.dumps(row) for row in rows), encoding="utf-8")
            result = CodexStore.recent(path)
        self.assertEqual(result["current_action"], "Waiting for delegated agents")
        self.assertEqual(result["last_tool_status"], "running")

    def test_subagent_task_fallback_reads_initial_assignment(self):
        rows = [
            {"ordinal": 0, "type": "session_meta", "payload": {}},
            {"ordinal": 1, "type": "event_msg",
             "payload": {"type": "user_message", "message": "Audit the parser and report the gap."}},
        ]
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "rollout.jsonl"
            path.write_text("\n".join(json.dumps(row) for row in rows), encoding="utf-8")
            self.assertEqual(CodexStore.task_from_head(path, True), "Audit the parser and report the gap.")

    def test_guardian_infrastructure_is_excluded_but_orphan_workers_remain(self):
        state = self.snapshot({
            self.ROOT_ID: self.rows(source={"subagent": {"other": "guardian"}}),
            self.CHILD_ID: self.rows(source={"subagent": {"thread_spawn": {
                "parent_thread_id": "missing", "agent_path": "/root/worker"}}}),
            self.GRANDCHILD_ID: self.rows(source="user"),
        })
        self.assertEqual({row["id"] for row in state["sessions"]}, {self.CHILD_ID, self.GRANDCHILD_ID})
        self.assertEqual(CodexStore.source_fields({"thread_source": "guardian_review"})["kind"], "guardian_review")

    def snapshot(self, sessions):
        with tempfile.TemporaryDirectory() as folder:
            home = Path(folder)
            session_dir = home / "sessions" / "2026" / "09" / "12"
            lock_dir = home / "thread-writer-locks"
            session_dir.mkdir(parents=True)
            lock_dir.mkdir()
            for session_id, rows in sessions.items():
                (session_dir / f"rollout-{session_id}.jsonl").write_text(
                    "\n".join(json.dumps(row) for row in rows), encoding="utf-8")
                (lock_dir / f"{session_id}.lock").touch()
            return CodexStore(home).snapshot()

    @staticmethod
    def rows(*rows, source="user"):
        meta = {"timestamp": "2026-09-12T12:00:00Z", "type": "session_meta",
                "payload": {"source": source, "cwd": "C:/work/project"}}
        return [meta, *rows]

    def test_new_user_prompt_reanchors_turn_and_survives_long_tail(self):
        filler = {"timestamp": "2026-09-12T12:01:01Z", "type": "event_msg",
                  "payload": {"type": "token_count", "padding": "x" * (800 * 1024)}}
        state = self.snapshot({self.ROOT_ID: self.rows(
            {"ordinal": 1, "timestamp": "2026-09-12T12:00:00Z", "type": "event_msg",
             "payload": {"type": "user_message", "message": "Build the semantic trace."}},
            {"ordinal": 2, "timestamp": "2026-09-12T12:01:00Z", "type": "event_msg",
             "payload": {"type": "user_message", "message": "Now document the trace."}},
            filler,
            {"ordinal": 4, "timestamp": "2026-09-12T12:01:02Z", "type": "event_msg",
             "payload": {"type": "task_started", "turn_id": "turn-2"}},
        )})
        session = state["sessions"][0]
        self.assertEqual(session["prompt_context"]["text"], "Now document the trace.")
        self.assertEqual(session["prompt_context"]["turn_id"], "turn-2")
        self.assertEqual(session["working_on"]["summary"], "Now document the trace.")
        self.assertEqual(session["trace"]["root_id"], session["prompt_context"]["id"])

    def test_compacted_replacement_history_restores_visible_user_prompt_only(self):
        filler = {"ordinal": 5, "timestamp": "2026-09-12T12:00:05Z", "type": "event_msg",
                  "payload": {"type": "token_count", "padding": "x" * (800 * 1024)}}
        state = self.snapshot({self.ROOT_ID: self.rows(
            {"ordinal": 1, "timestamp": "2026-09-12T12:00:01Z", "type": "compacted",
             "payload": {"message": "hidden compaction summary", "replacement_history": [
                 {"type": "message", "role": "developer", "id": "dev",
                  "content": [{"type": "input_text", "text": "hidden developer text"}]},
                 {"type": "message", "role": "user", "id": "user-1",
                  "content": [{"type": "input_text", "text": "Restore the real root prompt."}]},
             ]}},
            {"ordinal": 2, "timestamp": "2026-09-12T12:00:02Z", "type": "response_item",
             "payload": {"type": "message", "role": "user",
                         "content": [{"type": "input_text", "text": "<environment_context>hidden</environment_context>"}]}},
            {"ordinal": 3, "timestamp": "2026-09-12T12:00:03Z", "type": "response_item",
             "payload": {"type": "message", "role": "assistant", "phase": "commentary",
                         "content": [{"type": "output_text", "text": "Continuing retained work."}]}},
            {"ordinal": 4, "timestamp": "2026-09-12T12:00:04Z", "type": "compacted",
             "payload": {"replacement_history": [{
                 "type": "message", "role": "user", "id": "user-1",
                 "content": [{"type": "input_text", "text": "Restore the real root prompt."}],
             }]}},
            filler,
        )})
        session = state["sessions"][0]
        self.assertEqual(session["prompt_context"]["text"], "Restore the real root prompt.")
        self.assertEqual(session["prompt_context"]["source"], "retained_user_prompt")
        self.assertEqual(session["working_on"]["summary"], "Continuing retained work.")
        serialized = json.dumps(session["trace"])
        self.assertNotIn("hidden compaction summary", serialized)
        self.assertNotIn("hidden developer text", serialized)

    def test_nested_subagent_keeps_originating_prompt_after_parent_advances(self):
        parent_rows = self.rows(
            {"ordinal": 1, "timestamp": "2026-09-12T12:00:01Z", "type": "event_msg",
             "payload": {"type": "user_message", "message": "Build the semantic monitor."}},
            {"ordinal": 2, "timestamp": "2026-09-12T12:00:02Z", "type": "response_item",
             "payload": {"type": "function_call", "call_id": "spawn-1", "name": "spawn_agent",
                         "arguments": json.dumps({"task_name": "semantic_parser",
                                                  "message": "Implement the semantic parser."})}},
            {"ordinal": 3, "timestamp": "2026-09-12T12:00:03Z", "type": "event_msg",
             "payload": {"type": "user_message", "message": "Polish the dashboard UI."}},
        )
        source = {"subagent": {"thread_spawn": {
            "parent_thread_id": self.ROOT_ID, "agent_path": "/root/semantic_parser",
            "agent_nickname": "Parser", "depth": 1}}}
        child_rows = self.rows(
            {"ordinal": 1, "timestamp": "2026-09-12T12:00:02Z", "type": "event_msg",
             "payload": {"type": "user_message",
                         "message": "<codex_delegation><input>Implement the semantic parser.</input></codex_delegation>"}},
            {"ordinal": 2, "timestamp": "2026-09-12T12:00:04Z", "type": "response_item",
             "payload": {"type": "function_call", "call_id": "spawn-2", "name": "spawn_agent",
                         "arguments": json.dumps({"task_name": "evidence_reader",
                                                  "message": "Inspect live event shapes."})}},
            {"ordinal": 3, "timestamp": "2026-09-12T12:00:05Z", "type": "response_item",
             "payload": {"type": "message", "role": "assistant", "phase": "commentary",
                         "content": [{"type": "output_text", "text": "Checking the child event stream."}]}},
            {"ordinal": 4, "timestamp": "2026-09-12T12:00:06Z", "type": "response_item",
             "payload": {"type": "function_call", "call_id": "child-tool", "name": "exec_command",
                         "arguments": json.dumps({"cmd": "rg AgentMessage monitor.py"})}},
            source=source,
        )
        grandchild_source = {"subagent": {"thread_spawn": {
            "parent_thread_id": self.CHILD_ID, "agent_path": "/root/semantic_parser/evidence_reader",
            "agent_nickname": "Evidence", "depth": 2}}}
        grandchild_rows = self.rows(
            {"ordinal": 1, "timestamp": "2026-09-12T12:00:04Z", "type": "event_msg",
             "payload": {"type": "user_message",
                         "message": "<codex_delegation><input>Inspect live event shapes.</input></codex_delegation>"}},
            source=grandchild_source,
        )
        sessions = {row["id"]: row for row in self.snapshot({
            self.ROOT_ID: parent_rows, self.CHILD_ID: child_rows,
            self.GRANDCHILD_ID: grandchild_rows})["sessions"]}
        child = sessions[self.CHILD_ID]
        self.assertEqual(child["prompt_context"]["text"], "Build the semantic monitor.")
        self.assertEqual(child["prompt_context"]["source"], "user_prompt")
        self.assertEqual(child["working_on"]["summary"], "Implement the semantic parser.")
        self.assertEqual(child["working_on"]["source"], "delegated_task")
        self.assertEqual(child["working_on"]["task_path"],
                         ["Build the semantic monitor.", "Implement the semantic parser."])
        self.assertEqual(child["trace"]["root_id"], child["prompt_context"]["id"])
        self.assertEqual(sum(item["kind"] == "prompt" for item in child["trace"]["observations"]), 1)
        child_step = next(item for item in child["trace"]["observations"]
                          if item["summary"] == "Checking the child event stream.")
        child_tool = next(item for item in child["trace"]["observations"]
                          if item.get("call_id") == "child-tool")
        self.assertEqual(child_tool["parent_id"], child_step["id"])
        grandchild = sessions[self.GRANDCHILD_ID]
        self.assertEqual(grandchild["prompt_context"]["text"], "Build the semantic monitor.")
        self.assertEqual(grandchild["working_on"]["task_path"], [
            "Build the semantic monitor.", "Implement the semantic parser.", "Inspect live event shapes."])
        observation_ids = {item["id"] for item in grandchild["trace"]["observations"]}
        for item in grandchild["trace"]["observations"]:
            self.assertTrue(item["parent_id"] is None or item["parent_id"] in observation_ids)
        self.assertEqual(sum(item["kind"] == "prompt" for item in grandchild["trace"]["observations"]), 1)

    def test_spawn_does_not_replace_parent_step_for_follow_on_tool(self):
        state = self.snapshot({self.ROOT_ID: self.rows(
            {"ordinal": 1, "timestamp": "2026-09-12T12:00:01Z", "type": "event_msg",
             "payload": {"type": "user_message", "message": "Coordinate the parser work."}},
            {"ordinal": 2, "timestamp": "2026-09-12T12:00:02Z", "type": "response_item",
             "payload": {"type": "message", "role": "assistant", "phase": "commentary",
                         "content": [{"type": "output_text", "text": "Integrating the parser contract."}]}},
            {"ordinal": 3, "timestamp": "2026-09-12T12:00:03Z", "type": "response_item",
             "payload": {"type": "function_call", "call_id": "spawn-1", "name": "spawn_agent",
                         "arguments": json.dumps({"task_name": "parser", "message": "Implement parser."})}},
            {"ordinal": 4, "timestamp": "2026-09-12T12:00:04Z", "type": "response_item",
             "payload": {"type": "function_call", "call_id": "follow-on", "name": "exec_command",
                         "arguments": json.dumps({"cmd": "python -m unittest"})}},
        )})
        session = state["sessions"][0]
        step = next(item for item in session["trace"]["observations"]
                    if item["summary"] == "Integrating the parser contract.")
        follow_on = next(item for item in session["trace"]["observations"]
                         if item.get("call_id") == "follow-on")
        self.assertEqual(session["working_on"]["observation_id"], step["id"])
        self.assertEqual(follow_on["parent_id"], step["id"])

    def test_child_trace_tail_keeps_every_evidence_parent(self):
        parent_rows = self.rows(
            {"ordinal": 1, "timestamp": "2026-09-12T12:00:01Z", "type": "event_msg",
             "payload": {"type": "user_message", "message": "Build bounded traces."}},
            {"ordinal": 2, "timestamp": "2026-09-12T12:00:02Z", "type": "response_item",
             "payload": {"type": "function_call", "call_id": "spawn", "name": "spawn_agent",
                         "arguments": json.dumps({"task_name": "semantic_parser",
                                                  "message": "Implement bounded traces."})}},
        )
        source = {"subagent": {"thread_spawn": {
            "parent_thread_id": self.ROOT_ID, "agent_path": "/root/semantic_parser", "depth": 1}}}
        child_rows = self.rows(
            {"ordinal": 1, "timestamp": "2026-09-12T12:00:03Z", "type": "response_item",
             "payload": {"type": "message", "role": "assistant", "phase": "commentary",
                         "content": [{"type": "output_text", "text": "Collecting bounded evidence."}]}},
            *({"ordinal": index + 2, "timestamp": f"2026-09-12T12:01:{index % 60:02d}Z",
               "type": "event_msg", "payload": {"type": "token_count",
               "info": {"last_token_usage": {"total_tokens": index}}}}
              for index in range(130)),
            source=source,
        )
        sessions = {row["id"]: row for row in self.snapshot({
            self.ROOT_ID: parent_rows, self.CHILD_ID: child_rows})["sessions"]}
        observations = sessions[self.CHILD_ID]["trace"]["observations"]
        ids = {item["id"] for item in observations}
        self.assertTrue(sessions[self.CHILD_ID]["trace"]["truncated"])
        self.assertTrue(all(item["parent_id"] is None or item["parent_id"] in ids for item in observations))

    def test_tool_evidence_attaches_to_visible_semantic_step(self):
        state = self.snapshot({self.ROOT_ID: self.rows(
            {"ordinal": 1, "timestamp": "2026-09-12T12:00:01Z", "type": "event_msg",
             "payload": {"type": "user_message", "message": "Repair the parser."}},
            {"ordinal": 2, "timestamp": "2026-09-12T12:00:02Z", "type": "response_item",
             "payload": {"type": "message", "role": "assistant", "phase": "commentary",
                         "content": [{"type": "output_text", "text": "Adding prompt anchoring tests."}]}},
            {"ordinal": 3, "timestamp": "2026-09-12T12:00:03Z", "type": "response_item",
             "payload": {"type": "function_call", "call_id": "call-1", "name": "exec_command",
                         "arguments": json.dumps({"cmd": "python -m unittest test_monitor.py"})}},
            {"ordinal": 4, "timestamp": "2026-09-12T12:00:04Z", "type": "response_item",
             "payload": {"type": "function_call_output", "call_id": "call-1", "output": "private"}},
        )})
        session = state["sessions"][0]
        step = next(item for item in session["trace"]["observations"] if item["kind"] == "step")
        tool = next(item for item in session["trace"]["observations"] if item["kind"] == "tool")
        self.assertEqual(tool["parent_id"], step["id"])
        self.assertEqual(session["working_on"]["observation_id"], step["id"])
        self.assertEqual(session["working_on"]["source"], "agent_commentary")
        self.assertNotIn("private", json.dumps(session["trace"]))

    def test_safe_evidence_categories_keep_parents_and_drop_output_bodies(self):
        patch_call = ('const result = await tools.apply_patch("*** Begin Patch\\n'
                      '*** Update File: C:\\Users\\example\\project\\monitor.py\\n*** End Patch");')
        test_call = ('const result = await tools.exec_command({"cmd":'
                     '"python -m unittest test_monitor.py"});')
        state = self.snapshot({self.ROOT_ID: self.rows(
            {"ordinal": 1, "timestamp": "2026-09-12T12:00:01Z", "type": "event_msg",
             "payload": {"type": "user_message", "message": "Verify trace evidence."}},
            {"ordinal": 2, "timestamp": "2026-09-12T12:00:02Z", "type": "response_item",
             "payload": {"type": "function_call", "call_id": "patch", "name": "functions.exec",
                         "arguments": patch_call}},
            {"ordinal": 3, "timestamp": "2026-09-12T12:00:03Z", "type": "response_item",
             "payload": {"type": "function_call_output", "call_id": "patch",
                         "output": json.dumps({"exit_code": 0, "output": "SECRET PATCH BODY"})}},
            {"ordinal": 4, "timestamp": "2026-09-12T12:00:04Z", "type": "response_item",
             "payload": {"type": "function_call", "call_id": "tests", "name": "functions.exec",
                         "arguments": test_call}},
            {"ordinal": 5, "timestamp": "2026-09-12T12:00:05Z", "type": "event_msg",
             "payload": {"type": "exec_approval_request", "call_id": "tests"}},
            {"ordinal": 6, "timestamp": "2026-09-12T12:00:06Z", "type": "response_item",
             "payload": {"type": "function_call_output", "call_id": "tests",
                         "output": json.dumps({"exit_code": 2, "isError": True,
                                               "output": "SECRET TEST OUTPUT"})}},
            {"ordinal": 7, "timestamp": "2026-09-12T12:00:07Z", "type": "event_msg",
             "payload": {"type": "token_count", "info": {"model_context_window": 1000,
                         "last_token_usage": {"total_tokens": 42}}}},
        )})
        trace = state["sessions"][0]["trace"]
        kinds = {item["kind"] for item in trace["observations"]}
        self.assertTrue({"tool", "result", "file", "test", "approval", "error", "metric"} <= kinds)
        file_observation = next(item for item in trace["observations"] if item["kind"] == "file")
        self.assertEqual(file_observation["files"], ["monitor.py"])
        test_tool = next(item for item in trace["observations"]
                         if item["kind"] == "tool" and item.get("call_id") == "tests")
        self.assertEqual(test_tool["detail"], "Running automated tests")
        error = next(item for item in trace["observations"] if item["kind"] == "error")
        self.assertEqual(error["exit_code"], 2)
        ids = {item["id"] for item in trace["observations"]}
        self.assertTrue(all(item["parent_id"] is None or item["parent_id"] in ids
                            for item in trace["observations"]))
        serialized = json.dumps(trace)
        self.assertNotIn("SECRET PATCH BODY", serialized)
        self.assertNotIn("SECRET TEST OUTPUT", serialized)
        self.assertNotIn("do-not-expose", serialized)

    def test_structured_command_and_file_items_use_safe_fields_only(self):
        state = self.snapshot({self.ROOT_ID: self.rows(
            {"ordinal": 1, "timestamp": "2026-09-12T12:00:01Z", "type": "event_msg",
             "payload": {"type": "user_message", "message": "Read structured evidence."}},
            {"ordinal": 2, "timestamp": "2026-09-12T12:00:02Z", "type": "event_msg",
             "payload": {"type": "item_completed", "item": {
                 "type": "CommandExecution", "id": "command", "command": "python -m unittest",
                 "status": "failed", "exit_code": 1, "stdout": "SECRET STDOUT",
                 "stderr": "SECRET STDERR", "aggregated_output": "SECRET AGGREGATE"}}},
            {"ordinal": 3, "timestamp": "2026-09-12T12:00:03Z", "type": "event_msg",
             "payload": {"type": "item_completed", "item": {
                 "type": "FileChange", "id": "files", "status": "completed",
                 "changes": {"C:\\Users\\example\\project\\monitor.py": {"diff": "SECRET DIFF"}},
                 "formatted_output": "SECRET FORMATTED"}}},
        )})
        observations = state["sessions"][0]["trace"]["observations"]
        self.assertTrue({"tool", "test", "error", "file", "result"} <=
                        {item["kind"] for item in observations})
        self.assertEqual(next(item for item in observations if item["kind"] == "error")["exit_code"], 1)
        self.assertEqual(next(item for item in observations if item["kind"] == "file")["files"], ["monitor.py"])
        serialized = json.dumps(observations)
        for secret in ("SECRET STDOUT", "SECRET STDERR", "SECRET AGGREGATE", "SECRET DIFF", "SECRET FORMATTED"):
            self.assertNotIn(secret, serialized)

    def test_visible_sources_beat_tool_and_metadata_fallbacks(self):
        state = self.snapshot({self.ROOT_ID: self.rows(
            {"ordinal": 1, "timestamp": "2026-09-12T12:00:01Z", "type": "event_msg",
             "payload": {"type": "user_message", "message": "Inspect the live state."}},
            {"ordinal": 2, "timestamp": "2026-09-12T12:00:02Z", "type": "event_msg",
             "payload": {"type": "agent_message", "message": "Tracing the parent-child association."}},
            {"ordinal": 3, "timestamp": "2026-09-12T12:00:03Z", "type": "response_item",
             "payload": {"type": "function_call", "call_id": "call-1", "name": "exec_command",
                         "arguments": json.dumps({"cmd": "rg parent monitor.py"})}},
        )})
        session = state["sessions"][0]
        self.assertEqual(session["prompt_context"]["source"], "user_prompt")
        self.assertEqual(session["working_on"]["summary"], "Tracing the parent-child association.")
        self.assertEqual(session["working_on"]["source"], "agent_message")

    def test_hidden_reasoning_and_paired_messages_never_surface(self):
        visible = "Testing the public semantic trace."
        state = self.snapshot({self.ROOT_ID: self.rows(
            {"ordinal": 1, "timestamp": "2026-09-12T12:00:01Z", "type": "event_msg",
             "payload": {"type": "user_message", "message": "Show safe progress."}},
            {"ordinal": 2, "timestamp": "2026-09-12T12:00:02Z", "type": "event_msg",
             "payload": {"type": "item_completed", "item": {"type": "AgentMessage",
                         "phase": "commentary", "content": [{"type": "Text", "text": visible}],
                         "summary_text": "hidden summary"}}},
            {"ordinal": 3, "timestamp": "2026-09-12T12:00:02Z", "type": "response_item",
             "payload": {"type": "message", "role": "assistant", "phase": "commentary",
                         "content": [{"type": "output_text", "text": visible}]}},
            {"ordinal": 4, "timestamp": "2026-09-12T12:00:03Z", "type": "response_item",
             "payload": {"type": "message", "role": "assistant", "phase": "analysis",
                         "content": [{"type": "output_text", "text": "hidden analysis message"}]}},
            {"ordinal": 5, "timestamp": "2026-09-12T12:00:03Z", "type": "event_msg",
             "payload": {"type": "item_completed", "item": {"type": "AgentMessage",
                         "phase": "analysis", "content": [{"type": "Text", "text": "hidden analysis item"}],
                         "summary_text": "hidden item summary"}}},
            {"ordinal": 51, "timestamp": "2026-09-12T12:00:03Z", "type": "event_msg",
             "payload": {"type": "item_completed", "item": {"type": "AgentMessage",
                         "phase": "commentary", "channel": "analysis",
                         "content": [{"type": "Text", "text": "hidden mixed channel"}]}}},
            {"ordinal": 6, "timestamp": "2026-09-12T12:00:04Z", "type": "response_item",
             "payload": {"type": "reasoning", "summary_text": "hidden reasoning",
                         "encrypted_content": "hidden ciphertext"}},
            {"ordinal": 7, "timestamp": "2026-09-12T12:00:05Z", "type": "event_msg",
             "payload": {"type": "item_completed", "item": {"type": "Reasoning",
                         "raw_content": "hidden raw chain"}}},
        )})
        trace = state["sessions"][0]["trace"]
        serialized = json.dumps(trace)
        self.assertEqual([item["summary"] for item in trace["observations"]].count(visible), 1)
        for secret in ("hidden summary", "hidden analysis message", "hidden analysis item",
                       "hidden item summary", "hidden mixed channel", "hidden reasoning",
                       "hidden ciphertext", "hidden raw chain"):
            self.assertNotIn(secret, serialized)


class HttpBoundaryTests(unittest.TestCase):
    def setUp(self):
        class Store:
            reads = 0

            def snapshot(self):
                self.reads += 1
                return {"sessions": []}

            def signature(self):
                return ()

        self.store = Store()
        class App:
            calls = []

            def bootstrap(self):
                return {"capabilities": []}

            def dispatch(self, route, payload):
                self.calls.append((route, payload))
                return {"ok": True}

        self.app = App()
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(self.store, self.app))
        self.server.daemon_threads = True
        self.thread = Thread(target=self.server.serve_forever, kwargs={"poll_interval": .01})
        self.thread.start()
        self.authority = "127.0.0.1:" + str(self.server.server_port)

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()

    def request(self, path, hosts=None, origin=None):
        connection = HTTPConnection("127.0.0.1", self.server.server_port, timeout=2)
        connection.putrequest("GET", path, skip_host=True)
        for host in hosts if hosts is not None else [self.authority]:
            connection.putheader("Host", host)
        if origin is not None:
            connection.putheader("Origin", origin)
        connection.endheaders()
        response = connection.getresponse()
        body = response.readline() if response.getheader("Content-Type") == "text/event-stream" else response.read()
        result = response.status, dict(response.getheaders()), body
        connection.close()
        return result

    def test_foreign_missing_duplicate_or_wrong_port_hosts_cannot_read_local_data(self):
        for hosts in [["attacker.example:" + str(self.server.server_port)], [],
                      [self.authority, self.authority], ["127.0.0.1:1"],
                      [self.authority + "@attacker.example"]]:
            for path in ["/api/state", "/api/events", "/", "/observatory.html"]:
                with self.subTest(hosts=hosts, path=path):
                    self.assertEqual(self.request(path, hosts)[0], 403)
        self.assertEqual(self.store.reads, 0)

    def test_multiple_event_consumers_share_one_unchanged_snapshot(self):
        from concurrent.futures import ThreadPoolExecutor
        with ThreadPoolExecutor(max_workers=8) as readers:
            replies = list(readers.map(lambda _: self.request('/api/events'), range(8)))
        self.assertTrue(all(reply[0] == 200 for reply in replies))
        self.assertEqual(self.store.reads, 1, "Views must share the same immutable feed payload")

    def test_local_aliases_work_and_foreign_origin_is_rejected_before_source_read(self):
        self.assertEqual(self.request("/api/state", origin="https://attacker.example")[0], 403)
        self.assertEqual(self.store.reads, 0)
        for path in ["/", "/index.html", "/observatory", "/observatory.html", "/api/health", "/api/state"]:
            with self.subTest(path=path):
                status, headers, body = self.request(path, origin="http://" + self.authority)
                self.assertEqual(status, 200)
                self.assertEqual(headers["X-Content-Type-Options"], "nosniff")
                self.assertNotIn("Access-Control-Allow-Origin", headers)
                self.assertTrue(body)
        self.assertEqual(self.request("/api/state", ["localhost:" + str(self.server.server_port)])[0], 200)
        self.assertEqual(self.store.reads, 2)

    def test_mutations_require_exact_origin_secret_json_and_bounded_body(self):
        status, _, body = self.request("/api/bootstrap")
        self.assertEqual(status, 200)
        token = json.loads(body)["token"]
        def post(overrides=None, body=b'{}'):
            headers = {"Host": self.authority, "Origin": "http://" + self.authority,
                       "Content-Type": "application/json", "X-Foundry-Token": token}
            headers.update(overrides or {})
            headers = {key: value for key, value in headers.items() if value is not None}
            connection = HTTPConnection("127.0.0.1", self.server.server_port, timeout=2)
            connection.request("POST", "/api/settings", body, headers)
            response = connection.getresponse()
            result = response.status
            response.read(); connection.close()
            return result
        for headers in [{"Origin": None}, {"Origin": "null"}, {"Origin": "https://attacker.example"},
                        {"X-Foundry-Token": "wrong"}, {"Content-Type": "text/plain"},
                        {"Content-Length": "9" * 5000},
                        {"Content-Length": str(1024 * 1024 + 1)},
                        {"Host": "localhost:" + str(self.server.server_port)}, {"Transfer-Encoding": "chunked"}]:
            with self.subTest(headers=tuple(headers)):
                # Reject headers before reading a body; unread TCP bytes can reset Windows sockets.
                self.assertGreaterEqual(post(headers, body=b''), 400)
        for body in [b'[]', b'bad json', b'\xff', b'{"a":1,"a":2}', b'{"x":1e400}',
                     b'{"x":' + b'[' * 40 + b'0' + b']' * 40 + b'}',
                     b'{"x":' + b'[' * 2000 + b'0' + b']' * 2000 + b'}']:
            with self.subTest(body_length=len(body)):
                self.assertGreaterEqual(post(body=body), 400)
        self.assertEqual(self.app.calls, [])
        self.assertEqual(post(), 200)
        self.assertEqual(self.app.calls, [("/api/settings", {})])

    def test_every_html_alias_has_matching_script_nonce(self):
        import re
        for path in ["/", "/index.html", "/observatory", "/observatory.html", "/settings", "/library"]:
            status, headers, body = self.request(path)
            nonce = re.search(r"script-src 'nonce-([^']+)'", headers["Content-Security-Policy"])
            self.assertIsNotNone(nonce)
            scripts = re.findall(r"<script([^>]*)>", body.decode())
            self.assertTrue(scripts)
            for attrs in scripts:
                self.assertIn('nonce="' + nonce.group(1) + '"', attrs)

    def test_shared_script_loads_before_the_persistent_live_stream(self):
        for path in ["/", "/observatory"]:
            with self.subTest(path=path):
                _, _, body = self.request(path)
                self.assertEqual(body.count(b'src="/foundry.js"'), 1)
                self.assertLess(body.index(b'src="/foundry.js"'),
                                body.index(b"new EventSource('/api/events')"))


class JsonAdapterTests(unittest.TestCase):
    def test_json_preserves_reported_identity_without_inference(self):
        supplied = [{"id": "a", "harness": "codex", "provider": "openrouter", "model": "independent"},
                    {"id": "b", "harness": "claude-code", "model": "claude-model"},
                    {"id": "c", "harness": "pi-agent", "provider": "local"},
                    {"id": "d", "harness": "hermes", "role": "agent-runtime"},
                    {"id": "e", "model": "hermes"}]
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "state.json"
            path.write_text(json.dumps({"sessions": supplied}), encoding="utf-8")
            actual = JsonStore(path).snapshot()["sessions"]
        for before, after in zip(supplied, actual):
            for key in ("harness", "provider", "model", "role"):
                self.assertEqual(after.get(key), before.get(key))

    def test_missing_semantic_fields_are_normalized_as_fallbacks(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "state.json"
            path.write_text(json.dumps({"sessions": [{
                "id": "lead", "task": "Integrate the release", "status": "working"}]}), encoding="utf-8")
            session = JsonStore(path).snapshot()["sessions"][0]
        self.assertEqual(session["prompt_context"]["source"], "prompt_fallback")
        self.assertEqual(session["working_on"]["summary"], "Integrate the release")
        self.assertEqual(session["trace"]["version"], 1)
        self.assertEqual(session["work_breakdown"], session["trace"]["observations"])

    def test_account_and_parent_context_survive_json_fallback_normalization(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "state.json"
            path.write_text(json.dumps({
                "account": {"provider": "runner", "token_usage": {"total_tokens": 7}},
                "sessions": [
                    {"id": "lead", "task": "Ship the monitor", "current_action": "Reviewing integration",
                     "status": "working"},
                    {"id": "child", "parent": "lead", "task": "Run semantic tests",
                     "current_action": "python -m unittest", "status": "tool"},
                ],
            }), encoding="utf-8")
            state = JsonStore(path).snapshot()
        sessions = {row["id"]: row for row in state["sessions"]}
        self.assertEqual(state["account"]["provider"], "runner")
        self.assertEqual(sessions["lead"]["working_on"]["summary"], "Reviewing integration")
        self.assertEqual(sessions["lead"]["working_on"]["source"], "metadata_fallback")
        self.assertEqual(sessions["child"]["prompt_context"]["text"], "Ship the monitor")
        self.assertEqual(sessions["child"]["working_on"]["summary"], "Run semantic tests")
        self.assertEqual(sessions["child"]["working_on"]["task_path"],
                         ["Ship the monitor", "Run semantic tests"])

    def test_reordered_nested_json_inherits_recursively_and_preserves_supplied_trace(self):
        supplied_trace = {"version": 1, "root_id": "runner-root", "observations": [], "truncated": False}
        supplied_breakdown = [{"id": "runner-step", "kind": "step", "summary": "Runner step"}]
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "state.json"
            path.write_text(json.dumps({"sessions": [
                {"id": "grand", "parent": "child", "task": "Inspect evidence"},
                {"id": "child", "parent": "lead", "task": "Implement parser",
                 "trace": supplied_trace, "work_breakdown": supplied_breakdown},
                {"id": "lead", "task": "Build monitor"},
            ]}), encoding="utf-8")
            sessions = {row["id"]: row for row in JsonStore(path).snapshot()["sessions"]}
        self.assertEqual(sessions["grand"]["prompt_context"]["text"], "Build monitor")
        self.assertEqual(sessions["grand"]["working_on"]["task_path"],
                         ["Build monitor", "Implement parser", "Inspect evidence"])
        self.assertEqual(sessions["child"]["trace"], supplied_trace)
        self.assertEqual(sessions["child"]["work_breakdown"], supplied_breakdown)


if __name__ == "__main__":
    unittest.main()

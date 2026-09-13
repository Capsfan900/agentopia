import hashlib
import subprocess
import tempfile
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace

from actions import NativeBridge, ActionError


class NativeBridgeTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.exe = Path(self.folder.name) / "codex.exe"
        self.exe.write_bytes(b"MZtest native fixture")
        self.calls = []
        self.bridge = NativeBridge(self.exe, hashlib.sha256(self.exe.read_bytes()).hexdigest(),
                                   run=self.run_command)
        self.store = SimpleNamespace(snapshot=lambda: {"adapter": "codex", "sessions": [{"id": "11111111-1111-4111-8111-111111111111"}]})
        self.payload = {"action": "send_followup", "thread_id": "11111111-1111-4111-8111-111111111111",
                        "request_id": "22222222-2222-4222-8222-222222222222", "message": "Continue", "confirmed": True}

    def tearDown(self):
        self.folder.cleanup()

    def run_command(self, argv, **kwargs):
        self.calls.append((argv, kwargs))
        return SimpleNamespace(returncode=0)

    def test_reads_do_not_execute_and_message_metacharacters_remain_one_argument(self):
        self.assertTrue(self.bridge.capability()["available"])
        self.assertEqual(self.calls, [])
        self.payload["message"] = '--remote=evil ; $(write-file) " | &\nnext'
        self.assertTrue(self.bridge.send(self.payload, self.store)["ok"])
        argv, options = self.calls[0]
        self.assertEqual(argv, [str(self.exe), "queue", "--thread=" + self.payload["thread_id"], "--message=" + self.payload["message"]])
        self.assertIs(options["shell"], False)
        self.assertEqual(options["stdout"], subprocess.DEVNULL)
        self.assertEqual(options["stderr"], subprocess.DEVNULL)
        self.assertEqual(options["timeout"], 15)

    def test_invalid_confirmation_fields_sessions_and_changed_executable_fail_closed(self):
        for change in [{"confirmed": False}, {"message": " "}, {"message": "x" * 4001},
                       {"thread_id": "unknown"}, {"action": "shell"}, {"extra": "field"}]:
            with self.subTest(change=change), self.assertRaises(ActionError):
                self.bridge.send({**self.payload, **change}, self.store)
        self.store.snapshot = lambda: {"adapter": "json", "sessions": [{"id": self.payload["thread_id"]}]}
        with self.assertRaises(ActionError):
            self.bridge.send(self.payload, self.store)
        self.exe.write_bytes(b"MZchanged")
        self.assertFalse(self.bridge.capability()["available"])
        self.assertEqual(self.calls, [])

    def test_duplicate_ids_are_bound_to_payload_and_concurrent_send_is_single(self):
        entered, release = threading.Event(), threading.Event()
        def delayed(argv, **kwargs):
            self.calls.append((argv, kwargs)); entered.set(); release.wait(2)
            return SimpleNamespace(returncode=0)
        self.bridge.run = delayed
        worker = threading.Thread(target=lambda: self.bridge.send(self.payload, self.store))
        worker.start()
        try:
            self.assertTrue(entered.wait(1))
            self.assertEqual(self.bridge.send(self.payload, self.store)["status"], "pending")
            with self.assertRaises(ActionError) as conflict:
                self.bridge.send({**self.payload, "message": "different"}, self.store)
            self.assertEqual(conflict.exception.code, "request_conflict")
        finally:
            release.set(); worker.join()
        self.assertTrue(self.bridge.send(self.payload, self.store)["ok"])
        self.assertEqual(len(self.calls), 1)

    def test_timeout_is_cached_uncertain_and_rate_limit_prevents_sixth_call(self):
        def timeout(*args, **kwargs):
            raise subprocess.TimeoutExpired("private command", 15, output="private output")
        self.bridge.run = timeout
        result = self.bridge.send(self.payload, self.store)
        self.assertEqual(result["code"], "provider_timeout")
        self.assertTrue(result["uncertain"])
        self.assertNotIn("private", str(result))
        self.assertEqual(self.bridge.send(self.payload, self.store), result)
        self.bridge.run = self.run_command
        for i in range(3, 7):
            self.bridge.send({**self.payload, "request_id": f"22222222-2222-4222-8222-{i:012d}"}, self.store)
        with self.assertRaises(ActionError) as limited:
            self.bridge.send({**self.payload, "request_id": "22222222-2222-4222-8222-999999999999"}, self.store)
        self.assertEqual(limited.exception.code, "rate_limited")
        self.assertEqual(len(self.calls), 4)


if __name__ == "__main__":
    unittest.main()

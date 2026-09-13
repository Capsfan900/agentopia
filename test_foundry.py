import json
import tempfile
import unittest
from pathlib import Path

from foundry import FoundryApp, load_config
from monitor import JsonStore


class FoundryAppTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.root = Path(self.folder.name)
        self.source = self.root / "state.json"
        self.source.write_text(json.dumps({"sessions": [{"id": "root", "cwd": "C:/Work/Project", "status": "completed"},
                                                        {"id": "child", "parent": "root", "status": "completed"},
                                                        {"id": "other", "status": "working"}]}))
        self.config = {"adapter": "json", "state_file": str(self.source), "codex_home": str(self.root),
                       "host": "127.0.0.1", "port": 8777, "open": False, "verbose": False}
        self.app = FoundryApp(JsonStore(self.source), self.config, self.root / "foundry")

    def tearDown(self):
        self.folder.cleanup()

    def test_settings_are_explicit_atomic_and_credentials_are_not_configurable(self):
        self.assertFalse((self.root / "foundry/settings.json").exists())
        initial = self.app.bootstrap()
        update = {"config": {**self.config, "verbose": True}, "expected_revision": initial["settings_revision"]}
        result = self.app.dispatch("/api/settings", update)
        self.assertTrue(result["configuration"]["verbose"])
        self.assertTrue(load_config(self.root / "foundry")["verbose"])
        with self.assertRaises(ValueError):
            self.app.dispatch("/api/settings", update)
        with self.assertRaises(ValueError):
            self.app.dispatch("/api/settings", {"config": {**self.config, "api_key": "secret"},
                                               "expected_revision": result["settings_revision"]})
        self.assertNotIn("secret", (self.root / "foundry/settings.json").read_text())

    def test_feed_signature_changes_when_configuration_changes(self):
        before = self.app.signature()
        self.app.dispatch('/api/settings', {'config': {**self.config, 'verbose': True},
                          'expected_revision': self.app.bootstrap()['settings_revision']})
        self.assertNotEqual(self.app.signature(), before)

    def test_snapshot_projects_saved_handoffs_and_library_changes_feed_signature(self):
        initial = self.app.snapshot()
        self.assertTrue(all(row["saved_handoff"] == {"state": "none"} for row in initial["sessions"]))
        before = self.app.signature()

        saved = self.app.dispatch("/api/library/work", {"thread_id": "child", "context": "private note"})
        self.assertNotEqual(self.app.signature(), before)
        rows = {row["id"]: row["saved_handoff"] for row in self.app.snapshot()["sessions"]}
        self.assertEqual(rows["root"]["item_id"], saved["id"])
        self.assertEqual(rows["child"]["item_id"], saved["id"])
        self.assertEqual(rows["other"], {"state": "none"})
        self.assertEqual(set(rows["child"]),
                         {"state", "item_id", "revision", "captured_at", "archived", "completion"})

        before = self.app.signature()
        archived = self.app.dispatch(f'/api/library/{saved["id"]}/state',
                                     {"expected_revision": saved["revision"], "archived": True})
        self.assertNotEqual(self.app.signature(), before)
        projected = {row["id"]: row["saved_handoff"] for row in self.app.snapshot()["sessions"]}["child"]
        self.assertTrue(projected["archived"])
        self.assertEqual(projected["revision"], archived["revision"])

    def test_snapshot_resolves_source_family_and_cannot_accept_client_supplied_logs(self):
        item = self.app.dispatch("/api/library/work", {"thread_id": "child", "title": "Release review"})
        saved = self.app.library.get_item(item["id"])["data"]["snapshot"]
        self.assertEqual({row["id"] for row in saved["sessions"]}, {"root", "child"})
        self.assertEqual(saved["selected_session_id"], "child")
        self.assertEqual(saved["sessions"][0]["cwd"], "C:/Work/Project")
        with self.assertRaises(ValueError):
            self.app.dispatch("/api/library/work", {"thread_id": "root", "snapshot": {"sessions": [{"secret": "x"}]}})
        with self.assertRaises(ValueError):
            self.app.dispatch("/api/library/work", {"thread_id": "missing"})
        self.assertEqual(len(self.app.library.list_items()["items"]), 1)

    def test_capabilities_do_not_claim_json_discovery_or_native_controls(self):
        value = self.app.bootstrap()
        json_adapter = next(row for row in value["capabilities"] if row["adapter"] == "json")
        self.assertFalse(json_adapter["send_followup"])
        self.assertFalse(json_adapter["session_discovery"])
        self.assertTrue(json_adapter["detected"])

    def test_unimplemented_harnesses_are_explicit_unselectable_placeholders(self):
        rows = {row["adapter"]: row for row in self.app.bootstrap()["capabilities"]}
        for key, label in (("claude_code", "Claude Code"), ("pi", "Pi Agent"), ("hermes", "Hermes")):
            self.assertIn(key, rows)
            self.assertEqual(rows[key]["label"], label)
            self.assertEqual(rows[key]["detected"], "Not checked")
            self.assertFalse(rows[key]["supported"])
            self.assertFalse(rows[key]["selected"])
            self.assertFalse(rows[key]["send_followup"])
            with self.assertRaisesRegex(ValueError, "invalid_request"):
                self.app.settings({"config": {**self.config, "adapter": key},
                                   "expected_revision": self.app.bootstrap()["settings_revision"]})
        self.assertFalse((self.root / "foundry/settings.json").exists())

    def test_empty_codex_root_is_not_detected_as_the_current_directory(self):
        self.app.config["codex_home"] = ""
        row = next(row for row in self.app.bootstrap()["capabilities"] if row["adapter"] == "codex")
        self.assertFalse(row["detected"])

    def test_live_example_preserves_approval_and_nested_session_identity(self):
        snapshot = JsonStore(Path(__file__).parent / "example-state.json").snapshot()
        rows = {row["id"]: row for row in snapshot["sessions"]}
        self.assertEqual(rows["tests"]["status"], "approval")
        self.assertEqual(rows["review"]["status"], "completed")
        self.assertEqual(rows["nested"]["parent"], "tests")
        self.assertTrue(all(row["trace"]["version"] == 1 for row in rows.values()))

    def test_checked_examples_are_valid_and_export_requires_exact_preflight(self):
        for kind in ("component", "template"):
            manifest = json.loads((Path(__file__).parent / "examples" / (kind + ".json")).read_text(encoding="utf-8"))
            item = self.app.dispatch("/api/library/artifacts", {"kind": kind, "manifest": manifest})
            self.assertEqual(item["data"]["title"], manifest["title"])
        route = "/api/library/" + item["id"]
        payload = {"expected_revision": item["revision"], "mode": "reference"}
        plan = self.app.dispatch(route + "/preflight", payload)
        self.assertTrue(any(ref["field"] == "harness.config_ref" and ref["verification"] == "unverified"
                            for ref in plan["inventory"]["references"]))
        self.assertTrue(any("dirty" in warning for warning in plan["warnings"]))
        with self.assertRaises(ValueError):
            self.app.dispatch(route + "/export", {**payload, "plan_hash": plan["plan_hash"], "acknowledge_warnings": False})
        bundle = self.app.dispatch(route + "/export", {**payload, "plan_hash": plan["plan_hash"], "acknowledge_warnings": True})
        self.assertTrue(bundle.startswith(b"PK"))
        import base64
        destination = FoundryApp(JsonStore(self.source), self.config, self.root / "destination")
        result = destination.dispatch("/api/library/import", {"bundle": base64.b64encode(bundle).decode()})
        self.assertEqual(result["status"], "imported")
        self.assertFalse(result["compatibility"]["evaluated"])

    def test_saved_review_can_restart_after_reload_without_stale_promotion(self):
        manifest = json.loads((Path(__file__).parent / "examples/template.json").read_text(encoding="utf-8"))
        candidate = self.app.dispatch("/api/library/artifacts", {"kind": "template", "manifest": manifest})
        baseline = self.app.dispatch("/api/library/work", {"thread_id": "root"})
        review = {"candidate_revision": candidate["revision"],
                  "baseline": {"artifact_id": baseline["id"], "revision_or_evidence_hash": baseline["revision"], "scope": "synthetic recovery test"},
                  "verdict": "mixed", "no_worse": True, "user_confirmed": True,
                  "evidence": [{"kind": "measured", "scope": "synthetic test", "summary": "Fixture only, not a real user verdict"}],
                  "note": "Test fixture", "unverified": ["real work quality"]}
        reviewed = self.app.dispatch("/api/library/artifacts", {"kind": "template", "id": candidate["id"],
                                    "expected_revision": candidate["revision"], "manifest": {**manifest, "review": review}})
        reopened = FoundryApp(JsonStore(self.source), self.config, self.root / "foundry")
        loaded = reopened.read_library("/api/library/" + reviewed["id"])
        fresh_manifest = {key: value for key, value in loaded["data"].items() if key != "review"}
        fresh = reopened.dispatch("/api/library/artifacts", {"kind": "template", "id": loaded["id"],
                                  "expected_revision": loaded["revision"], "manifest": fresh_manifest})
        route = "/api/library/" + fresh["id"]
        validation = reopened.dispatch(route + "/validate", {"expected_revision": fresh["revision"]})
        accepted = reopened.dispatch("/api/library/artifacts", {"kind": "template", "id": fresh["id"],
                                     "expected_revision": fresh["revision"], "manifest": {**fresh_manifest, "review": {**review, "candidate_revision": fresh["revision"]}}})
        promoted = reopened.dispatch(route + "/promote", {"expected_revision": accepted["revision"],
                                      "validation_id": validation["validation_id"], "confirmed": True})
        self.assertEqual(promoted["approved_revision"], fresh["revision"])
        self.assertTrue((self.root / "foundry/library/objects" / (reviewed["revision"] + ".json")).exists())


if __name__ == "__main__":
    unittest.main()

import copy
import hashlib
import io
import json
import tempfile
import unittest
import uuid
import zipfile
from pathlib import Path

from library import DomainError, Library


def template_manifest(**changes):
    value = {
        "title": "Release helper",
        "purpose": "Resume a reviewed release workflow",
        "harness": {"id": "codex", "version": "1", "config_ref": "config:local"},
        "model": {"name": "gpt", "effort": "high"},
        "instruction_layers": [{"role": "system", "ref": "policy:base", "rationale": "Safety baseline"}],
        "components": [],
        "capabilities": ["filesystem-read"],
        "tools": ["shell"],
        "permissions_policy": {"ref": "policy:reviewed", "description": "Ask before external writes"},
        "quality_policy": {"procedures": ["test-first", "explicit-user-acceptance"],
                           "skill_mappings": [{"procedure": "test-first",
                                               "skill_ref": "superpowers:test-driven-development"}]},
        "environment": {"dependencies": [{"name": "python", "version": "3.12", "rationale": "Runtime"}],
                        "required_env_names": ["SERVICE_TOKEN"]},
        "validation": [{"name": "unit tests", "argv": ["python", "-m", "unittest"],
                        "cwd_ref": "project:root", "last_result_ref": "work:result"}],
        "provenance": {"source_work_id": "unavailable", "provider": "codex", "session_id": "session-1",
                       "captured_at": "2026-09-12T12:00:00Z"},
        "resume": {"handoff": "Read the report", "next_steps": ["Inspect failures"], "open_questions": []},
        "compatibility": ["Python 3.12"],
        "limitations": ["No provider mutation"],
    }
    value.update(changes)
    return value


def component_manifest(content=b"safe component"):
    return {
        "title": "Stored runbook", "purpose": "Pinned reusable instructions", "component_type": "runbook",
        "source_ref": "library:stored", "version": "1", "sha256": hashlib.sha256(content).hexdigest(),
        "rationale": "Known reviewed input", "compatibility": ["text/plain"], "limitations": [],
        "asset": {"content_base64": __import__("base64").b64encode(content).decode(), "media_type": "text/plain",
                  "license": "MIT", "selected": True},
    }


def rewrite_first_object(bundle, transform):
    with zipfile.ZipFile(io.BytesIO(bundle)) as source:
        files = {name: source.read(name) for name in source.namelist()}
    manifest = json.loads(files["manifest.json"])
    old_path = manifest["objects"][0]["path"]
    new_raw = transform(files.pop(old_path))
    checksum = hashlib.sha256(new_raw).hexdigest()
    new_path = f"objects/{checksum}.json"
    files[new_path] = new_raw
    manifest["objects"][0] = {"path": new_path, "sha256": checksum}
    manifest["items"][0]["revision"] = checksum
    files["manifest.json"] = json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode()
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as target:
        for name, content in files.items():
            target.writestr(name, content)
    return output.getvalue()


def export_acked(library, item, mode="reference"):
    plan = library.preflight_bundle(item["id"], item["revision"], mode)
    return library.export_bundle(item["id"], item["revision"], mode, plan["plan_hash"], True, mode == "vendor")


def bundle_created_at(bundle, created_at):
    with zipfile.ZipFile(io.BytesIO(bundle)) as source:
        files = {name: source.read(name) for name in source.namelist()}
    manifest = json.loads(files["manifest.json"])
    manifest["created_at"] = created_at
    files["manifest.json"] = json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode()
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as target:
        for name, content in files.items():
            target.writestr(name, content)
    return output.getvalue()


def seed_legacy_import(root, bundle, created_at):
    with zipfile.ZipFile(io.BytesIO(bundle)) as archive:
        manifest = json.loads(archive.read("manifest.json"))
        heads = copy.deepcopy(manifest["items"])
        originals = {head["revision"]: (archive.read(f'objects/{head["revision"]}.json'),
                                               json.loads(archive.read(f'objects/{head["revision"]}.json')))
                     for head in heads}
    catalog, objects = root / "catalog", root / "objects"
    catalog.mkdir(parents=True)
    objects.mkdir()
    mapping, revisions = {}, {}
    for head in sorted(heads, key=lambda item: item["kind"] == "template"):
        foreign_revision = head["revision"]
        foreign_raw, original = originals[foreign_revision]
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
        local = {"schema_version": 1, "id": original["id"], "kind": original["kind"],
                 "parent_revision": foreign_revision, "created_at": created_at,
                 "action": "import_candidate", "payload": payload}
        local_raw = json.dumps(local, sort_keys=True, separators=(",", ":")).encode()
        local_revision = hashlib.sha256(local_raw).hexdigest()
        (objects / f"{foreign_revision}.json").write_bytes(foreign_raw)
        (objects / f"{local_revision}.json").write_bytes(local_raw)
        mapping[foreign_revision] = local_revision
        revisions[head["id"]] = local_revision
        head.pop("approved_revision", None)
        head.pop("classification", None)
        head["revision"] = local_revision
        head["updated_at"] = created_at
        (catalog / f'{head["id"]}.json').write_text(
            json.dumps(head, sort_keys=True, separators=(",", ":")), encoding="utf-8")
    return revisions


class LibraryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "library"
        self.library = Library(self.root)
        self.assertFalse(self.root.exists(), "opening a missing library must be read-only")

    def tearDown(self):
        self.temp.cleanup()

    def test_work_is_immutable_indexed_sanitized_and_concurrency_checked(self):
        snapshot = {
            "id": "session-1", "task": "Ship it", "status": "completed", "cwd": "C:/project",
            "harness": "hermes", "role": "agent-runtime", "provider": "openrouter", "model": "independent-model",
            "prompt_context": {"summary": "Deploy", "text": "token=top-secret"},
            "working_on": {"summary": "Done", "status": "completed", "detail": "password=hunter2"},
            "trace": {"version": 1, "root_id": "p1", "observations": [
                {"id": "p1", "kind": "prompt", "summary": "Deploy", "detail": "api_key=secret"}
            ]},
            "recent_activity": [{"tool": "exec", "detail": "--token abc", "status": "completed"}],
            "unexpected": "must not persist",
        }
        saved = self.library.save_work(snapshot, "Release work", context="customer A")
        uuid.UUID(saved["id"])
        self.assertRegex(saved["revision"], r"^[0-9a-f]{64}$")
        self.assertEqual(saved["data"]["snapshot"]["task"], "Ship it")
        for key in ("harness", "role", "provider", "model"):
            self.assertEqual(saved["data"]["snapshot"][key], snapshot[key])
        self.assertEqual(saved["data"]["capture"]["sanitizer_version"], 1)
        self.assertFalse(saved["data"]["capture"]["include_details"])
        self.assertTrue(saved["data"]["capture"]["capture_environment"]["python"])
        self.assertIn("unrecorded", saved["data"]["capture"]["configuration_limitations"])
        serialized = json.dumps(saved)
        self.assertNotIn("unexpected", serialized)
        self.assertNotIn("top-secret", serialized)
        self.assertNotIn("hunter2", serialized)
        self.assertNotIn("api_key", serialized)
        self.assertNotIn("recent_activity", serialized)

        head = json.loads((self.root / "catalog" / f'{saved["id"]}.json').read_text(encoding="utf-8"))
        object_path = self.root / "objects" / f'{saved["revision"]}.json'
        self.assertEqual(hashlib.sha256(object_path.read_bytes()).hexdigest(), saved["revision"])
        self.assertEqual(head["revision"], saved["revision"])
        self.assertEqual(Library(self.root).get_item(saved["id"])["revision"], saved["revision"])

        with self.assertRaises(DomainError) as stale:
            self.library.set_work_state(saved["id"], "0" * 64, archived=True)
        self.assertEqual(stale.exception.code, "conflict")

        archived = self.library.set_work_state(saved["id"], saved["revision"], archived=True)
        self.assertNotEqual(archived["revision"], saved["revision"])
        with self.assertRaises(DomainError) as replay:
            self.library.set_work_state(saved["id"], saved["revision"], archived=False)
        self.assertEqual(replay.exception.code, "conflict")

    def test_work_details_are_opt_in_redacted_and_verdict_requires_completed_work(self):
        active = self.library.save_work({"id": "s1", "status": "idle", "task": "Wait"}, "Waiting")
        with self.assertRaises(DomainError) as incomplete:
            self.library.set_work_state(active["id"], active["revision"], verdict="successful", note="Looks good")
        self.assertEqual(incomplete.exception.code, "invalid_state")

        completed = self.library.set_work_state(active["id"], active["revision"], completed=True)
        self.assertEqual(completed["data"]["completion"],
                         {"completed": True, "source": "user", "statuses": ["idle"]})
        accepted = self.library.set_work_state(completed["id"], completed["revision"],
                                               verdict="successful", note="Manually accepted")
        self.assertEqual(accepted["data"]["review"]["verdict"], "successful")

        done = self.library.save_work({
            "id": "s2", "status": "completed", "task": "Test",
            "prompt_context": {"summary": "Test", "text": "Use password=secret-value"},
            "recent_activity": [{"tool": "exec", "detail": "Bearer abcdefghijklmnop", "status": "completed"}],
        }, "Completed", include_details=True)
        self.assertIn("[redacted]", json.dumps(done))
        reviewed = self.library.set_work_state(done["id"], done["revision"], archived=True,
                                               verdict="mixed", note="Useful with one limitation")
        self.assertTrue(reviewed["archived"])
        self.assertEqual(reviewed["data"]["review"]["verdict"], "mixed")
        self.assertNotEqual(reviewed["revision"], done["revision"])
        self.assertEqual(self.library.list_items(kind="work", archived=True)["items"][0]["id"], done["id"])

    def test_work_keeps_allowlisted_semantic_evidence_and_metrics(self):
        session = {"id": "s", "status": "failed", "collaboration": "multi-agent",
                   "token_usage": {"input_tokens": 10, "output_tokens": 4, "password": "drop-me"},
                   "trace": {"version": 1, "root_id": "root", "truncated": False,
                             "observations": [{"id": "root", "kind": "prompt", "summary": "Task",
                                               "files": ["library.py"], "exit_code": 1, "call_id": "c1"}]}}
        saved = self.library.save_work({"adapter": "codex", "generated": "2026-09-12T12:00:00Z",
                                        "source": "C:/private/codex", "selected_session_id": "s",
                                        "sessions": [session]}, "Evidence")
        snapshot = saved["data"]["snapshot"]
        self.assertEqual(snapshot["selected_session_id"], "s")
        self.assertEqual(snapshot["sessions"][0]["collaboration"], "multi-agent")
        self.assertEqual(snapshot["sessions"][0]["token_usage"], {"input_tokens": 10, "output_tokens": 4})
        observation = snapshot["sessions"][0]["trace"]["observations"][0]
        self.assertEqual((observation["summary"], observation["files"], observation["exit_code"]),
                         ("Task", ["library.py"], 1))
        self.assertEqual(saved["data"]["completion"]["statuses"], ["failed"])
        accepted = self.library.set_work_state(saved["id"], saved["revision"], verdict="failed",
                                               note="The run ended but did not meet the goal")
        self.assertEqual(accepted["data"]["review"]["verdict"], "failed")

    def test_startup_index_needs_explicit_refresh_and_reports_bad_heads(self):
        first = self.library.save_work({"id": "s", "status": "completed"}, "One")
        other = Library(self.root)
        second = self.library.save_work({"id": "s2", "status": "completed"}, "Two")
        self.assertEqual([item["id"] for item in other.list_items()["items"]], [first["id"]])
        (self.root / "catalog" / "bad.json").write_text("not json", encoding="utf-8")
        refreshed = other.refresh()
        self.assertEqual({item["id"] for item in refreshed["items"]}, {first["id"], second["id"]})
        self.assertEqual(len(refreshed["warnings"]), 1)
        self.assertNotIn("not json", refreshed["warnings"][0])

    def test_valid_hash_cannot_smuggle_unknown_work_fields_from_disk(self):
        saved = self.library.save_work({"id": "s", "status": "completed"}, "Safe")
        original = json.loads((self.root / "objects" / f'{saved["revision"]}.json').read_text(encoding="utf-8"))
        original["payload"]["snapshot"]["hidden_reasoning"] = "private"
        raw = json.dumps(original, sort_keys=True, separators=(",", ":")).encode()
        revision = hashlib.sha256(raw).hexdigest()
        (self.root / "objects" / f"{revision}.json").write_bytes(raw)
        head_path = self.root / "catalog" / f'{saved["id"]}.json'
        head = json.loads(head_path.read_text(encoding="utf-8"))
        head["revision"] = revision
        head_path.write_text(json.dumps(head), encoding="utf-8")
        reopened = Library(self.root)
        with self.assertRaises(DomainError) as corrupt:
            reopened.get_item(saved["id"])
        self.assertEqual(corrupt.exception.code, "corrupt_data")

    def test_artifact_validation_review_and_promotion_are_distinct(self):
        candidate = self.library.save_artifact("template", template_manifest())
        self.assertEqual(candidate["data"]["purpose"], "Resume a reviewed release workflow")
        validation = self.library.validate_candidate(candidate["id"], candidate["revision"])
        self.assertEqual(validation["scope"], "structural")
        self.assertEqual(validation["subject_revision"], candidate["revision"])
        self.assertEqual(validation["result"], "valid")
        self.assertIn("unverified", {check["status"] for check in validation["checks"]})
        with self.assertRaises(DomainError) as no_review:
            self.library.promote_candidate(candidate["id"], candidate["revision"], validation["validation_id"])
        self.assertEqual(no_review.exception.code, "promotion_blocked")

        baseline = self.library.save_work({"id": "base", "status": "completed"}, "Pinned baseline")
        reviewed_manifest = template_manifest(review={
            "candidate_revision": candidate["revision"],
            "baseline": {"artifact_id": baseline["id"], "revision_or_evidence_hash": baseline["revision"],
                         "scope": "release workflow"},
            "verdict": "mixed", "no_worse": True,
            "evidence": [{"kind": "user-attested", "scope": "manual smoke test",
                          "summary": "Core workflow was no worse"}],
            "note": "Accepted with documented limitation", "unverified": ["Windows runner"],
            "user_confirmed": True,
        })
        reviewed = self.library.save_artifact("template", reviewed_manifest, candidate["id"], candidate["revision"])
        promoted = self.library.promote_candidate(reviewed["id"], reviewed["revision"], validation["validation_id"])
        self.assertEqual(promoted["approved_revision"], candidate["revision"])
        self.assertNotEqual(promoted["revision"], reviewed["revision"])

        next_candidate = self.library.save_artifact("template", template_manifest(purpose="Next draft"),
                                                    promoted["id"], promoted["revision"])
        self.assertEqual(next_candidate["approved_revision"], candidate["revision"])

        stale_review = template_manifest(purpose="Changed after review", review=reviewed_manifest["review"])
        stale = self.library.save_artifact("template", stale_review, next_candidate["id"], next_candidate["revision"])
        with self.assertRaises(DomainError) as stale_error:
            self.library.promote_candidate(stale["id"], stale["revision"], validation["validation_id"])
        self.assertEqual(stale_error.exception.code, "promotion_blocked")

    def test_artifact_schema_rejects_unknown_fields_and_secret_values(self):
        with self.assertRaises(DomainError) as unknown:
            self.library.save_artifact("template", template_manifest(surprise=True))
        self.assertEqual(unknown.exception.code, "invalid_input")
        secret = template_manifest(environment={"dependencies": [], "required_env_names": [],
                                                "SERVICE_TOKEN": "actual-secret"})
        with self.assertRaises(DomainError) as leaked:
            self.library.save_artifact("template", secret)
        self.assertEqual(leaked.exception.code, "invalid_input")

        with self.assertRaises(DomainError) as encoded:
            self.library.save_artifact("component", component_manifest(
                b"-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----"))
        self.assertEqual(encoded.exception.code, "invalid_input")

        with self.assertRaises(DomainError):
            self.library.save_artifact("component", {**component_manifest(), "component_type": "unknown"})

    def test_reference_export_does_not_carry_stored_vendor_bytes(self):
        large_component = self.library.save_artifact("component", component_manifest(b"x" * 30_000))
        self.assertEqual(large_component["kind"], "component")
        component = self.library.save_artifact("component", component_manifest())
        bundle = export_acked(self.library, component)
        with zipfile.ZipFile(io.BytesIO(bundle)) as archive:
            manifest = json.loads(archive.read("manifest.json"))
            exported = json.loads(archive.read(manifest["objects"][0]["path"]))
            self.assertNotIn("asset", exported["payload"])
            self.assertFalse(any(name.startswith("assets/") for name in archive.namelist()))

    def test_vendor_export_requires_specific_confirmation_and_includes_selected_manifest(self):
        component = self.library.save_artifact("component", component_manifest())
        unselected = self.library.save_artifact("component", component_manifest(b"Unselected private component body"))
        component_ref = {"kind": "runbook", "ref": component["id"], "version": "1",
                         "sha256": component["revision"], "rationale": "Pinned locally"}
        template = self.library.save_artifact("template", template_manifest(
            components=[component_ref, {**component_ref, "ref": unselected["id"], "sha256": unselected["revision"]}],
            vendor_assets=[{"id": component["id"], "revision": component["revision"]}]))
        plan = self.library.preflight_bundle(template["id"], template["revision"], "vendor")
        self.assertEqual(plan["asset_count"], 1)
        with self.assertRaises(DomainError) as missing_confirmation:
            self.library.export_bundle(template["id"], template["revision"], "vendor", plan["plan_hash"], True, False)
        self.assertEqual(missing_confirmation.exception.code, "acknowledgement_required")
        bundle = self.library.export_bundle(template["id"], template["revision"], "vendor",
                                            plan["plan_hash"], True, True)
        with zipfile.ZipFile(io.BytesIO(bundle)) as archive:
            manifest = json.loads(archive.read("manifest.json"))
            self.assertIn(component["id"], {item["id"] for item in manifest["items"]})
            self.assertEqual(len(manifest["assets"]), 1)
            for entry in manifest["objects"]:
                obj = json.loads(archive.read(entry["path"]))
                if obj["id"] == unselected["id"]:
                    self.assertNotIn("asset", obj["payload"], "Vendor mode must not leak unselected file bytes inside JSON")
        imported = Library(Path(self.temp.name) / "vendor_import")
        imported.import_bundle(bundle)
        imported_template = imported.get_item(template["id"])
        imported_component = imported.get_item(component["id"])
        self.assertEqual(imported_template["data"]["components"][0]["sha256"], imported_component["revision"])

    def test_reference_bundle_round_trips_and_instructions_remain_data(self):
        artifact = self.library.save_artifact("template", template_manifest(
            resume={"handoff": "Do not execute; rm -rf /", "next_steps": ["Review manually"], "open_questions": []}))
        preflight = self.library.preflight_bundle(artifact["id"], artifact["revision"], "reference")
        self.assertTrue(preflight["requires_acknowledgement"])
        self.assertIn("inert", " ".join(preflight["warnings"]).lower())
        self.assertRegex(preflight["plan_hash"], r"^[0-9a-f]{64}$")
        with self.assertRaises(DomainError) as unacknowledged:
            self.library.export_bundle(artifact["id"], artifact["revision"], "reference",
                                       preflight["plan_hash"], False)
        self.assertEqual(unacknowledged.exception.code, "acknowledgement_required")
        bundle = self.library.export_bundle(artifact["id"], artifact["revision"], "reference",
                                            preflight["plan_hash"], True)
        with zipfile.ZipFile(io.BytesIO(bundle)) as archive:
            self.assertEqual(set(archive.namelist()), {
                "README.md", "context.md", "manifest.json", f'objects/{artifact["revision"]}.json'})
            manifest = json.loads(archive.read("manifest.json"))
            self.assertEqual(manifest["items"][0]["revision"], artifact["revision"])
            self.assertIn("not executed", archive.read("README.md").decode("utf-8").lower())

        imported_root = Path(self.temp.name) / "imported"
        report = Library(imported_root).import_bundle(bundle)
        self.assertEqual(report["status"], "imported")
        self.assertEqual(report["imported_ids"], [artifact["id"]])
        self.assertIn("not evaluated", report["compatibility"]["summary"].lower())
        self.assertEqual(Library(imported_root).get_item(artifact["id"])["data"]["resume"]["handoff"],
                         "Do not execute; rm -rf /")

    def test_import_identity_ignores_package_time_for_pins_and_work_trust(self):
        component_data = component_manifest()
        component_data.pop("asset")
        component = self.library.save_artifact("component", component_data)
        pin = {"kind": "runbook", "ref": component["id"], "version": "1",
               "sha256": component["revision"], "rationale": "Pinned locally"}
        template = self.library.save_artifact("template", template_manifest(components=[pin]))
        template_bundle = export_acked(self.library, template)
        early = bundle_created_at(template_bundle, "2026-01-01T00:00:00Z")
        late = bundle_created_at(template_bundle, "2026-12-31T23:59:59Z")
        destination = Library(Path(self.temp.name) / "stable-template")
        first = destination.import_bundle(early)
        before = {item["id"]: item["revision"] for item in destination.list_items()["items"]}

        second = destination.import_bundle(late)

        after = {item["id"]: item["revision"] for item in destination.list_items()["items"]}
        imported_template = destination.get_item(template["id"])
        imported_component = destination.get_item(component["id"])
        self.assertEqual(before, after)
        self.assertEqual(set(first["imported_ids"]), {component["id"], template["id"]})
        self.assertEqual(set(second["unchanged_ids"]), {component["id"], template["id"]})
        self.assertEqual(imported_template["data"]["components"][0]["sha256"], imported_component["revision"])

        work = self.library.save_work({"id": "reviewed", "status": "completed"}, "Reviewed work")
        work = self.library.set_work_state(work["id"], work["revision"], verdict="successful", note="accepted")
        work_bundle = export_acked(self.library, work)
        work_destination = Library(Path(self.temp.name) / "stable-work")
        work_destination.import_bundle(bundle_created_at(work_bundle, "2026-02-01T00:00:00Z"))
        repeated = work_destination.import_bundle(bundle_created_at(work_bundle, "2026-11-01T00:00:00Z"))
        imported_work = work_destination.get_item(work["id"])
        self.assertEqual(repeated["unchanged_ids"], [work["id"]])
        self.assertNotIn("review", imported_work["data"])
        self.assertEqual(imported_work["data"]["completion"],
                         {"completed": False, "source": "imported", "statuses": ["completed"]})

    def test_import_reuses_exact_legacy_pinned_graph_without_rewriting_it(self):
        component_data = component_manifest()
        component_data.pop("asset")
        component = self.library.save_artifact("component", component_data)
        pin = {"kind": "runbook", "ref": component["id"], "version": "1",
               "sha256": component["revision"], "rationale": "Pinned locally"}
        template = self.library.save_artifact("template", template_manifest(components=[pin]))
        bundle = export_acked(self.library, template)
        legacy_root = Path(self.temp.name) / "legacy"
        legacy_revisions = seed_legacy_import(legacy_root, bundle, "2025-01-01T00:00:00Z")
        before = {path.relative_to(legacy_root): path.read_bytes()
                  for path in legacy_root.rglob("*") if path.is_file()}
        destination = Library(legacy_root)

        result = destination.import_bundle(bundle_created_at(bundle, "2026-12-31T23:59:59Z"))

        after = {path.relative_to(legacy_root): path.read_bytes()
                 for path in legacy_root.rglob("*") if path.is_file()}
        self.assertEqual(set(result["unchanged_ids"]), {component["id"], template["id"]})
        imported_component = destination.get_item(component["id"])
        imported_template = destination.get_item(template["id"])
        self.assertEqual(imported_component["revision"], legacy_revisions[component["id"]])
        self.assertEqual(imported_template["revision"], legacy_revisions[template["id"]])
        self.assertEqual(imported_template["data"]["components"][0]["sha256"], imported_component["revision"])
        self.assertEqual(after, before)

    def test_import_conflicts_after_meaningful_local_change(self):
        component_data = component_manifest()
        component_data.pop("asset")
        component = self.library.save_artifact("component", component_data)
        bundle = export_acked(self.library, component)
        destination = Library(Path(self.temp.name) / "changed")
        destination.import_bundle(bundle)
        imported = destination.get_item(component["id"])
        changed = {**imported["data"], "purpose": "Locally changed purpose"}
        destination.save_artifact("component", changed, imported["id"], imported["revision"])

        with self.assertRaises(DomainError) as conflict:
            destination.import_bundle(bundle_created_at(bundle, "2027-01-01T00:00:00Z"))

        self.assertEqual(conflict.exception.code, "conflict")

    def test_import_strips_foreign_approval_and_review_trust(self):
        candidate = self.library.save_artifact("template", template_manifest())
        validation = self.library.validate_candidate(candidate["id"], candidate["revision"])
        baseline = self.library.save_work({"id": "b", "status": "completed"}, "Baseline")
        manifest = template_manifest(review={
            "candidate_revision": candidate["revision"],
            "baseline": {"artifact_id": baseline["id"], "revision_or_evidence_hash": baseline["revision"], "scope": "all"},
            "verdict": "successful", "no_worse": True,
            "evidence": [{"kind": "user-attested", "scope": "all", "summary": "accepted"}],
            "note": "accepted", "unverified": [], "user_confirmed": True})
        reviewed = self.library.save_artifact("template", manifest, candidate["id"], candidate["revision"])
        promoted = self.library.promote_candidate(candidate["id"], reviewed["revision"], validation["validation_id"])
        bundle = export_acked(self.library, promoted)
        imported = Library(Path(self.temp.name) / "foreign")
        imported.import_bundle(bundle)
        item = imported.get_item(candidate["id"])
        self.assertNotIn("approved_revision", item)
        self.assertNotIn("review", item["data"])
        self.assertEqual(item["action"], "import_candidate")

    def test_import_rejects_unsanitized_work_and_duplicate_json_keys(self):
        work = self.library.save_work({"id": "s", "status": "completed"}, "Safe")
        bundle = export_acked(self.library, work)
        imported = Library(Path(self.temp.name) / "valid_work")
        imported.import_bundle(bundle)
        self.assertEqual(imported.get_item(work["id"])["data"]["completion"],
                         {"completed": False, "source": "imported", "statuses": ["completed"]})

        def add_private(raw):
            value = json.loads(raw)
            value["payload"]["snapshot"]["hidden_reasoning"] = "private"
            return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()

        def duplicate_key(raw):
            return raw.replace(b'"payload":{', b'"payload":{"hidden_reasoning":"private","hidden_reasoning":"benign",', 1)

        for transform in (add_private, duplicate_key):
            with self.subTest(transform=transform.__name__):
                with self.assertRaises(DomainError) as rejected:
                    Library(Path(self.temp.name) / transform.__name__).import_bundle(
                        rewrite_first_object(bundle, transform))
                self.assertEqual(rejected.exception.code, "bundle_invalid")

    def test_bundle_import_rejects_traversal_symlink_bomb_and_secrets(self):
        cases = []
        for name, content, attrs in [
            ("../outside", b"x", 0),
            ("objects/link.json", b"{}", 0o120777 << 16),
            ("manifest.json", b'{"password":"should-not-import"}', 0),
        ]:
            stream = io.BytesIO()
            with zipfile.ZipFile(stream, "w", zipfile.ZIP_DEFLATED) as archive:
                info = zipfile.ZipInfo(name)
                info.external_attr = attrs
                archive.writestr(info, content)
            cases.append(stream.getvalue())
        huge = io.BytesIO()
        with zipfile.ZipFile(huge, "w", zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("manifest.json", b"0" * (2 * 1024 * 1024 + 1))
        cases.append(huge.getvalue())

        for payload in cases:
            with self.subTest(size=len(payload)):
                with self.assertRaises(DomainError) as rejected:
                    self.library.import_bundle(payload)
                self.assertIn(rejected.exception.code, {"bundle_invalid", "secret_detected", "too_large"})


if __name__ == "__main__":
    unittest.main()

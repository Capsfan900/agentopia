import hashlib
import io
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from actions import ActionError
from foundry import FoundryApp
from library import DomainError, Library


def component_manifest(title="Fixture"):
    content = (title + "\n").encode()
    return {
        "title": title,
        "purpose": "Inert fixture",
        "component_type": "instructions",
        "source_ref": "fixture:" + title,
        "version": "1",
        "sha256": hashlib.sha256(content).hexdigest(),
        "rationale": "Test fixture",
        "compatibility": ["text/plain"],
        "limitations": ["Data only"],
    }


def classification(item, validation_id=None, **changes):
    value = {
        "revision": item["revision"],
        "display_title": "AGENTS.md",
        "kind": "instructions",
        "domain": "agent-workflow",
        "provider": "unknown",
        "harness": "codex",
        "projects": ["fixture"],
        "sources": ["fixture/AGENTS.md"],
        "maturity": ["project-specific"],
        "portability": "reference-only",
        "relevance": "direct",
        "reason": "Own agent instructions",
        "evidence": ["self:component_type=instructions"],
        "validation_id": validation_id,
    }
    value.update(changes)
    return value


class ClassificationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "library"
        self.library = Library(self.root)

    def tearDown(self):
        self.temp.cleanup()

    def test_classification_is_revision_bound_durable_and_idempotent(self):
        item = self.library.save_artifact("component", component_manifest())
        validation = self.library.validate_candidate(item["id"], item["revision"])
        object_path = self.root / "objects" / (item["revision"] + ".json")
        before_bytes = object_path.read_bytes()
        before_objects = sorted(path.name for path in (self.root / "objects").iterdir())
        before_approval = item.get("approved_revision")

        saved = self.library.set_classification(
            item["id"], item["revision"], classification(item, validation["validation_id"])
        )

        self.assertEqual(saved["revision"], item["revision"])
        self.assertEqual(saved.get("approved_revision"), before_approval)
        self.assertEqual(object_path.read_bytes(), before_bytes)
        self.assertEqual(sorted(path.name for path in (self.root / "objects").iterdir()), before_objects)
        reopened = Library(self.root).get_item(item["id"])
        self.assertEqual(reopened["classification"], classification(item, validation["validation_id"]))
        listed = next(row for row in Library(self.root).list_items()["items"] if row["id"] == item["id"])
        self.assertEqual(listed["classification"], classification(item, validation["validation_id"]))
        head_path = self.root / "catalog" / (item["id"] + ".json")
        first_head = head_path.read_bytes()
        repeated = self.library.set_classification(
            item["id"], item["revision"], classification(item, validation["validation_id"])
        )
        self.assertEqual(repeated["updated_at"], saved["updated_at"])
        self.assertEqual(head_path.read_bytes(), first_head)

    def test_invalid_classifications_reject_without_mutation(self):
        item = self.library.save_artifact("component", component_manifest())
        other = self.library.save_artifact("component", component_manifest("Other"))
        wrong_validation = self.library.validate_candidate(other["id"], other["revision"])
        work = self.library.save_work({}, "Saved work")
        cases = [
            (item["id"], "0" * 64, classification(item)),
            (item["id"], item["revision"], classification(item, revision="0" * 64)),
            (item["id"], item["revision"], {**classification(item), "surprise": True}),
            (item["id"], item["revision"], classification(item, display_title="x" * 201)),
            (item["id"], item["revision"], classification(item, kind="x" * 121)),
            (item["id"], item["revision"], classification(item, projects=[])),
            (item["id"], item["revision"], classification(item, projects=["x"] * 129)),
            (item["id"], item["revision"], classification(item, relevance="approved")),
            (item["id"], item["revision"], classification(item, wrong_validation["validation_id"])),
            (item["id"], item["revision"], classification(item, reason="api_key=sk-" + "x" * 24)),
            (work["id"], work["revision"], classification(work)),
        ]
        for item_id, revision, metadata in cases:
            with self.subTest(item_id=item_id, revision=revision, keys=sorted(metadata)):
                head_path = self.root / "catalog" / (item_id + ".json")
                before = head_path.read_bytes()
                with self.assertRaises(DomainError):
                    self.library.set_classification(item_id, revision, metadata)
                self.assertEqual(head_path.read_bytes(), before)

    def test_reload_ignores_catalog_classification_with_missing_validation(self):
        item = self.library.save_artifact("component", component_manifest())
        validation = self.library.validate_candidate(item["id"], item["revision"])
        self.library.set_classification(
            item["id"], item["revision"], classification(item, validation["validation_id"])
        )
        head_path = self.root / "catalog" / (item["id"] + ".json")
        head = json.loads(head_path.read_text(encoding="utf-8"))
        head["classification"]["validation_id"] = "f" * 64
        head_path.write_text(json.dumps(head), encoding="utf-8")

        reloaded = Library(self.root).list_items()

        self.assertEqual(reloaded["items"], [])
        self.assertEqual(len(reloaded["warnings"]), 1)

    def test_dispatch_accepts_only_expected_revision_and_classification(self):
        item = self.library.save_artifact("component", component_manifest())
        app = object.__new__(FoundryApp)
        app.library = self.library
        route = f"/api/library/{item['id']}/classification"
        saved = app.dispatch(
            route,
            {"expected_revision": item["revision"], "classification": classification(item)},
        )
        self.assertEqual(saved["classification"]["relevance"], "direct")
        with self.assertRaises(ActionError):
            app.dispatch(
                route,
                {
                    "expected_revision": item["revision"],
                    "classification": classification(item),
                    "extra": True,
                },
            )

    def test_revision_changes_and_bundles_do_not_carry_local_classification(self):
        item = self.library.save_artifact("component", component_manifest())
        item = self.library.set_classification(item["id"], item["revision"], classification(item))
        plan = self.library.preflight_bundle(item["id"], item["revision"], "reference")
        bundle = self.library.export_bundle(item["id"], item["revision"], "reference", plan["plan_hash"], True)
        with zipfile.ZipFile(io.BytesIO(bundle)) as archive:
            manifest = json.loads(archive.read("manifest.json"))
            files = {name: archive.read(name) for name in archive.namelist()}
        self.assertNotIn("classification", manifest["items"][0])
        manifest["items"][0]["classification"] = classification(item)
        files["manifest.json"] = json.dumps(manifest, separators=(",", ":")).encode()
        foreign_output = io.BytesIO()
        with zipfile.ZipFile(foreign_output, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, content in files.items():
                archive.writestr(name, content)
        imported = Library(Path(self.temp.name) / "imported")
        imported.import_bundle(foreign_output.getvalue())
        self.assertNotIn("classification", imported.get_item(item["id"]))

        revised = self.library.save_artifact(
            "component", component_manifest("Revised"), item["id"], item["revision"]
        )
        self.assertNotIn("classification", revised)

    def test_promotion_drops_classification_bound_to_the_review_revision(self):
        candidate = self.library.save_artifact("component", component_manifest())
        validation = self.library.validate_candidate(candidate["id"], candidate["revision"])
        review = {
            "candidate_revision": candidate["revision"],
            "baseline": {"revision_or_evidence_hash": "1" * 64, "scope": "fixture"},
            "verdict": "successful",
            "no_worse": True,
            "evidence": [{"kind": "user-attested", "scope": "fixture", "summary": "accepted"}],
            "note": "accepted",
            "unverified": [],
            "user_confirmed": True,
        }
        reviewed = self.library.save_artifact(
            "component", {**component_manifest(), "review": review}, candidate["id"], candidate["revision"]
        )
        reviewed = self.library.set_classification(
            reviewed["id"], reviewed["revision"], classification(reviewed)
        )
        promoted = self.library.promote_candidate(
            reviewed["id"], reviewed["revision"], validation["validation_id"]
        )
        self.assertNotIn("classification", promoted)
        classified = self.library.set_classification(
            promoted["id"], promoted["revision"], classification(promoted)
        )
        self.assertEqual(classified["approved_revision"], promoted["approved_revision"])


if __name__ == "__main__":
    unittest.main()

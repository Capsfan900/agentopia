import hashlib
import io
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from library import DomainError, Library


FIXTURES = Path(__file__).parent / "examples" / "open-format-v1"


def fixture_bytes(name):
    return (FIXTURES / name).read_bytes()


def fixture_json(name):
    return json.loads(fixture_bytes(name))


def fixture_bundle():
    index = fixture_json("fixture-index.json")
    files = {
        "README.md": fixture_bytes("README.md"),
        "context.md": fixture_bytes("context.md"),
    }
    objects = []
    items = []
    for entry in index["records"]:
        raw = fixture_bytes(entry["file"])
        revision = hashlib.sha256(raw).hexdigest()
        assert revision == entry["revision"]
        path = f"objects/{revision}.json"
        files[path] = raw
        obj = json.loads(raw)
        objects.append({"path": path, "sha256": revision})
        items.append({"schema_version": 1, "id": obj["id"], "kind": obj["kind"],
                      "title": entry["title"], "revision": revision,
                      "archived": False, "updated_at": index["created_at"]})
    asset = fixture_bytes("component-asset.txt")
    asset_hash = hashlib.sha256(asset).hexdigest()
    files[f"assets/{asset_hash}"] = asset
    manifest = {
        "schema_version": 1,
        "mode": "vendor",
        "created_at": index["created_at"],
        "items": items,
        "objects": objects,
        "assets": [{"path": f"assets/{asset_hash}", "sha256": asset_hash,
                    "component_id": index["component_id"], "license": "CC0-1.0",
                    "media_type": "text/plain"}],
        "documents": [{"path": name, "sha256": hashlib.sha256(files[name]).hexdigest()}
                      for name in ("README.md", "context.md")],
        "compatibility": {"declared": ["unrecorded"], "evaluated": False},
    }
    files["manifest.json"] = json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode("utf-8")
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, content in files.items():
            archive.writestr(name, content)
    return output.getvalue()


def rewrite_manifest(bundle, change):
    with zipfile.ZipFile(io.BytesIO(bundle)) as source:
        files = {name: source.read(name) for name in source.namelist()}
    manifest = json.loads(files["manifest.json"])
    change(manifest)
    files["manifest.json"] = json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode("utf-8")
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as target:
        for name, content in files.items():
            target.writestr(name, content)
    return output.getvalue()


class OpenFormatV1Tests(unittest.TestCase):
    def test_public_fixture_bytes_match_declared_revisions(self):
        index = fixture_json("fixture-index.json")
        component = fixture_json("component.object.json")
        self.assertEqual(component["payload"]["sha256"],
                         hashlib.sha256(fixture_bytes("component-asset.txt")).hexdigest())
        for entry in index["records"]:
            raw_object = fixture_bytes(entry["file"])
            self.assertEqual(hashlib.sha256(raw_object).hexdigest(), entry["revision"])

    def test_existing_importer_round_trips_reference_and_vendor_as_inert_data(self):
        index = fixture_json("fixture-index.json")
        with tempfile.TemporaryDirectory() as temporary:
            source = Library(Path(temporary) / "source")
            source.import_bundle(fixture_bundle())
            template = source.get_item(index["template_id"])
            bundles = {}
            for mode in ("reference", "vendor"):
                plan = source.preflight_bundle(template["id"], template["revision"], mode)
                bundles[mode] = source.export_bundle(template["id"], template["revision"], mode,
                                                     plan["plan_hash"], True, mode == "vendor")
            with zipfile.ZipFile(io.BytesIO(bundles["reference"])) as archive:
                self.assertFalse(any(name.startswith("assets/") for name in archive.namelist()))
            with zipfile.ZipFile(io.BytesIO(bundles["vendor"])) as archive:
                self.assertTrue(any(name.startswith("assets/") for name in archive.namelist()))
            for mode, bundle in bundles.items():
                with self.subTest(mode=mode):
                    destination = Library(Path(temporary) / mode)
                    report = destination.import_bundle(bundle)
                    imported = destination.get_item(index["template_id"])
                    self.assertEqual(report["compatibility"]["evaluated"], False)
                    self.assertEqual(imported["data"]["resume"]["handoff"],
                                     "DO NOT EXECUTE: review this handoff manually.")

    def test_existing_importer_rejects_unknown_version_key_and_hash(self):
        bundle = fixture_bundle()
        cases = {
            "version": rewrite_manifest(bundle, lambda manifest: manifest.update(schema_version=2)),
            "key": rewrite_manifest(bundle, lambda manifest: manifest.update(unexpected=True)),
        }
        with zipfile.ZipFile(io.BytesIO(bundle)) as source:
            files = {name: source.read(name) for name in source.namelist()}
        first_object = json.loads(files["manifest.json"])["objects"][0]["path"]
        files[first_object] += b" "
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, content in files.items():
                archive.writestr(name, content)
        cases["hash"] = output.getvalue()
        with tempfile.TemporaryDirectory() as temporary:
            for name, mutated_package in cases.items():
                with self.subTest(name=name), self.assertRaises(DomainError):
                    Library(Path(temporary) / name).import_bundle(mutated_package)


if __name__ == "__main__":
    unittest.main()

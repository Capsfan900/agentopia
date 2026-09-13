import hashlib
import importlib.util
import io
import tempfile
import unittest
import warnings
import zipfile
from pathlib import Path


SPEC = importlib.util.spec_from_file_location("package", Path(__file__).with_name("package.py"))
package = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(package)


class PackageTests(unittest.TestCase):
    def test_metadata_must_match_the_pinned_embed_archive(self):
        metadata = {"versions": [{"id": "pythonembed-3.13-64", "url": package.ARCHIVE_URL, "hash": {"sha256": package.ARCHIVE_SHA256}}]}
        self.assertEqual(package.verify_metadata(metadata), package.ARCHIVE_URL)
        metadata["versions"][0]["hash"]["sha256"] = "0" * 64
        with self.assertRaises(ValueError):
            package.verify_metadata(metadata)

    def test_archive_member_guard_rejects_escape_link_and_collision(self):
        with tempfile.TemporaryDirectory() as temporary:
            archive = Path(temporary) / "runtime.zip"
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", UserWarning)
                with zipfile.ZipFile(archive, "w") as bundle:
                    bundle.writestr("python.exe", b"one")
                    bundle.writestr("python.exe", b"two")
                    bundle.writestr("../escape.txt", b"bad")
            with self.assertRaises(ValueError):
                package.extract_runtime(archive, Path(temporary) / "runtime")
        for unsafe in ("../escape", "/absolute", "C:drive", "dir:stream", ""):
            with self.assertRaises(ValueError):
                package.safe_archive_name(unsafe)

    def test_output_queue_is_bounded_by_manifest_hashes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            file = root / "app" / "monitor.py"
            file.parent.mkdir()
            file.write_bytes(b"safe")
            self.assertEqual(package.hash_tree(root), {"app/monitor.py": hashlib.sha256(b"safe").hexdigest()})

    def test_existing_stage_requires_its_previous_manifest(self):
        files = {"app/monitor.py": "a" * 64}
        self.assertTrue(package.is_owned_stage(files, {"schema_version": 1, "files": files}))
        self.assertFalse(package.is_owned_stage(files, {"schema_version": 1, "files": {"app/monitor.py": "b" * 64}}))


if __name__ == "__main__":
    unittest.main()

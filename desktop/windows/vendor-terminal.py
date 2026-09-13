"""Fetch only pinned renderer assets from official npm tarballs; never run scripts."""
import base64
import hashlib
from io import BytesIO
import json
from pathlib import Path
import tarfile
from urllib.request import urlopen

PACKAGES = [
    ("@xterm/xterm", "6.0.0", "https://registry.npmjs.org/@xterm/xterm/-/xterm-6.0.0.tgz",
     "TQwDdQGtwwDt+2cgKDLn0IRaSxYu1tSUjgKarSDkUM0ZNiSRXFpjxEsvc/Zgc5kq5omJ+V0a8/kIM2WD3sMOYg==",
     {"package/lib/xterm.js": "xterm.js", "package/css/xterm.css": "xterm.css", "package/LICENSE": "xterm.LICENSE"}),
    ("@xterm/addon-fit", "0.11.0", "https://registry.npmjs.org/@xterm/addon-fit/-/addon-fit-0.11.0.tgz",
     "jYcgT6xtVYhnhgxh3QgYDnnNMYTcf8ElbxxFzX0IZo+vabQqSPAjC3c1wJrKB5E19VwQei89QCiZZP86DCPF7g==",
     {"package/lib/addon-fit.js": "addon-fit.js", "package/LICENSE": "addon-fit.LICENSE"}),
]


def main():
    target = Path(__file__).resolve().parent / "terminal" / "vendor"
    target.mkdir(parents=True, exist_ok=True)
    manifest = []
    for name, version, url, integrity, files in PACKAGES:
        with urlopen(url, timeout=30) as response:
            raw = response.read(8 * 1024 * 1024 + 1)
        if len(raw) > 8 * 1024 * 1024 or base64.b64encode(hashlib.sha512(raw).digest()).decode() != integrity:
            raise ValueError(f"Package integrity mismatch: {name}")
        entry = {"name": name, "version": version, "source": url, "license": "MIT",
                 "integrity": "sha512-" + integrity, "files": {}}
        with tarfile.open(fileobj=BytesIO(raw), mode="r:gz") as archive:
            for member, filename in files.items():
                item = archive.getmember(member)
                if not item.isfile() or item.size > 4 * 1024 * 1024:
                    raise ValueError("Invalid asset member")
                content = archive.extractfile(item).read()
                (target / filename).write_bytes(content)
                entry["files"][filename] = hashlib.sha256(content).hexdigest()
        manifest.append(entry)
    (target / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print("Verified pinned local terminal assets:", ", ".join(item["name"] + "@" + item["version"] for item in manifest))


if __name__ == "__main__":
    main()

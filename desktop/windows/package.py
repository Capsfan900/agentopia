"""Build the private, integrity-checked Agent Foundry staging tree."""
import hashlib
import json
import os
import shutil
import stat
import tempfile
import urllib.request
import zipfile
from pathlib import Path, PurePosixPath

WINDOWS = Path(__file__).resolve().parent
ROOT = WINDOWS.parents[1]
STAGE = WINDOWS / "stage"
LOCK_PATH = WINDOWS / "runtime-lock.json"
MANIFEST_PATH = WINDOWS / "bundle-manifest.json"
REPORT_PATH = WINDOWS / "package-report.md"
METADATA_URL = "https://www.python.org/ftp/python/3.13.15/windows-3.13.15.json"
ARCHIVE_URL = "https://www.python.org/ftp/python/3.13.15/python-3.13.15-embeddable-amd64.zip"
ARCHIVE_SHA256 = "791ada5e20aba24524f8d939cdeb069976d632a699fe5cb65274b23f4545e68a"
MAX_DOWNLOAD = 64 * 1024 * 1024
MAX_EXTRACTED = 160 * 1024 * 1024
APP_FILES = ("monitor.py", "foundry.py", "library.py", "actions.py", "desktop_runtime.py", "index.html", "observatory.html", "manage.html", "foundry.js", "foundry.css")
TERMINAL_FILES = ("index.html", "terminal.js", "terminal.css")


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def is_reparse(path):
    try:
        info = os.lstat(path)
    except FileNotFoundError:
        return False
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0) & 0x400)


def require_regular(path):
    path = Path(path)
    if is_reparse(path) or not path.is_file():
        raise ValueError("expected regular file: " + str(path))
    return path


def safe_archive_name(name):
    if not isinstance(name, str) or not name or "\\" in name or "\x00" in name or ":" in name:
        raise ValueError("unsafe archive member")
    path = PurePosixPath(name)
    if path.is_absolute() or any(part in ("", ".", "..") for part in path.parts):
        raise ValueError("unsafe archive member")
    return path


def extract_runtime(archive, destination):
    destination = Path(destination)
    if destination.exists():
        raise ValueError("runtime destination already exists")
    seen, total = set(), 0
    with zipfile.ZipFile(archive) as bundle:
        entries = []
        for info in bundle.infolist():
            name = safe_archive_name(info.filename.rstrip("/"))
            if info.is_dir():
                continue
            mode = info.external_attr >> 16
            if stat.S_IFMT(mode) not in (0, stat.S_IFREG):
                raise ValueError("archive links are not allowed")
            key = name.as_posix().casefold()
            if key in seen:
                raise ValueError("archive member collision")
            seen.add(key); total += info.file_size
            if info.file_size < 0 or total > MAX_EXTRACTED:
                raise ValueError("archive is too large")
            entries.append((info, name))
        for info, name in entries:
            target = destination.joinpath(*name.parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            if is_reparse(target.parent):
                raise ValueError("archive destination reparse point")
            with bundle.open(info) as source, target.open("xb") as output:
                shutil.copyfileobj(source, output, 1024 * 1024)


def verify_metadata(metadata):
    if not isinstance(metadata, dict) or not isinstance(metadata.get("versions"), list):
        raise ValueError("official metadata is malformed")
    matches = [entry for entry in metadata["versions"] if isinstance(entry, dict) and entry.get("id") == "pythonembed-3.13-64"]
    if len(matches) != 1 or matches[0].get("url") != ARCHIVE_URL or matches[0].get("hash", {}).get("sha256") != ARCHIVE_SHA256:
        raise ValueError("official metadata does not match the runtime lock")
    return ARCHIVE_URL


def read_url(url, maximum):
    request = urllib.request.Request(url, headers={"User-Agent": "AgentFoundryPackage/1"})
    with urllib.request.urlopen(request, timeout=30) as response:
        size = response.headers.get("Content-Length")
        if size and (not size.isdigit() or int(size) > maximum):
            raise ValueError("download is too large")
        data = response.read(maximum + 1)
    if len(data) > maximum:
        raise ValueError("download is too large")
    return data


def download_runtime(url):
    request = urllib.request.Request(url, headers={"User-Agent": "AgentFoundryPackage/1"})
    handle = tempfile.NamedTemporaryFile(prefix="agent-foundry-python-", suffix=".zip", delete=False)
    path = Path(handle.name)
    try:
        with handle, urllib.request.urlopen(request, timeout=60) as response:
            size = response.headers.get("Content-Length")
            if size and (not size.isdigit() or int(size) > MAX_DOWNLOAD):
                raise ValueError("download is too large")
            total = 0
            while chunk := response.read(1024 * 1024):
                total += len(chunk)
                if total > MAX_DOWNLOAD:
                    raise ValueError("download is too large")
                handle.write(chunk)
        if sha256(path) != ARCHIVE_SHA256:
            raise ValueError("runtime archive hash mismatch")
        return path
    except BaseException:
        path.unlink(missing_ok=True)
        raise


def hash_tree(root):
    root = Path(root)
    files = {}
    for path in sorted(root.rglob("*")):
        if path.is_dir():
            if is_reparse(path):
                raise ValueError("reparse point in staged tree")
            continue
        require_regular(path)
        files[path.relative_to(root).as_posix()] = sha256(path)
    return files


def is_owned_stage(files, manifest):
    return isinstance(manifest, dict) and manifest.get("schema_version") == 1 and manifest.get("files") == files


def copy_atomic(source, destination):
    source, destination = require_regular(source), Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    if is_reparse(destination.parent) or (destination.exists() and (is_reparse(destination) or not destination.is_file())):
        raise ValueError("unsafe staging target")
    descriptor, temporary = tempfile.mkstemp(prefix=".package-", dir=destination.parent)
    try:
        with os.fdopen(descriptor, "wb") as output, source.open("rb") as input:
            shutil.copyfileobj(input, output, 1024 * 1024)
        os.replace(temporary, destination)
    except BaseException:
        Path(temporary).unlink(missing_ok=True)
        raise


def write_atomic(destination, data):
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    if is_reparse(destination.parent) or (destination.exists() and (is_reparse(destination) or not destination.is_file())):
        raise ValueError("unsafe staging target")
    descriptor, temporary = tempfile.mkstemp(prefix=".package-", dir=destination.parent)
    try:
        with os.fdopen(descriptor, "wb") as output:
            output.write(data)
        os.replace(temporary, destination)
    except BaseException:
        Path(temporary).unlink(missing_ok=True)
        raise


def copy_sources(tree):
    app = tree / "app"; terminal = tree / "terminal"
    for name in APP_FILES:
        copy_atomic(ROOT / name, app / name)
    for name in TERMINAL_FILES:
        copy_atomic(WINDOWS / "terminal" / name, terminal / name)
    for source in sorted((WINDOWS / "terminal" / "vendor").iterdir()):
        copy_atomic(source, terminal / "vendor" / source.name)


def stage_tree(archive):
    with tempfile.TemporaryDirectory(prefix="agent-foundry-stage-", dir=WINDOWS) as temporary:
        tree = Path(temporary) / "stage"
        extract_runtime(archive, tree / "runtime" / "python")
        write_atomic(tree / "runtime" / "python" / "python313._pth", b"python313.zip\n.\n../../app\n")
        copy_sources(tree)
        files = hash_tree(tree)
        install_tree(tree, files)
        return files


def install_tree(tree, files):
    if STAGE.exists() and (is_reparse(STAGE) or not STAGE.is_dir()):
        raise ValueError("unsafe existing stage")
    if STAGE.exists():
        existing = hash_tree(STAGE)
        if not MANIFEST_PATH.exists() or is_reparse(MANIFEST_PATH):
            raise ValueError("existing stage has no trusted manifest")
        previous = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
        if not is_owned_stage(existing, previous):
            raise ValueError("existing stage does not match its manifest")
        if not set(existing).issubset(files):
            raise ValueError("existing stage contains unowned files")
    for relative in sorted(files):
        copy_atomic(tree / relative, STAGE / relative)
    manifest = {"schema_version": 1, "files": files}
    write_atomic(MANIFEST_PATH, (json.dumps(manifest, indent=2, sort_keys=True) + "\n").encode("utf-8"))


def load_lock():
    value = json.loads(LOCK_PATH.read_text(encoding="utf-8"))
    if value != {"schema_version": 1, "python": {"id": "pythonembed-3.13-64", "metadata_url": METADATA_URL, "url": ARCHIVE_URL, "sha256": ARCHIVE_SHA256}}:
        raise ValueError("runtime lock is not the pinned Python runtime")


def write_report(files):
    size = sum((STAGE / relative).stat().st_size for relative in files)
    report = "# Private runtime staging\n\n- Python: `pythonembed-3.13-64` / 3.13.15\n- Metadata: " + METADATA_URL + "\n- Archive: " + ARCHIVE_URL + "\n- SHA-256: `" + ARCHIVE_SHA256 + "`\n- Staged files: " + str(len(files)) + "\n- Staged bytes: " + str(size) + "\n- Python path: `python313.zip`, `.`, `../../app`; `import site` remains disabled.\n\nThe host must verify `bundle-manifest.json` before startup. This is staging only; it does not sign, install, run Agent Foundry, or provide process containment.\n"
    write_atomic(REPORT_PATH, report.encode("utf-8"))


def main():
    load_lock()
    metadata = json.loads(read_url(METADATA_URL, 1024 * 1024).decode("utf-8"))
    archive = download_runtime(verify_metadata(metadata))
    try:
        files = stage_tree(archive)
    finally:
        archive.unlink(missing_ok=True)
    write_report(files)
    print("staged", len(files), "files")


if __name__ == "__main__":
    main()

"""Private native-host startup and one-writer ownership; no terminal operations."""
import json
import os
from pathlib import Path
import re


class DataDirectoryLease:
    """OS-held file lock, released on normal exit or process death; file stays inert."""
    def __init__(self, directory):
        self.directory = Path(directory).resolve()
        self.stream = None

    def __enter__(self):
        self.directory.mkdir(parents=True, exist_ok=True)
        path = self.directory / ".foundry-service.lock"
        if path.is_symlink():
            raise OSError("Unsafe service lock path")
        self.stream = path.open("a+b")
        try:
            self.stream.seek(0, 2)
            if self.stream.tell() == 0:
                self.stream.write(b"\0")
                self.stream.flush()
            self.stream.seek(0)
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self.stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BaseException:
            self.stream.close()
            self.stream = None
            raise
        return self

    def __exit__(self, *_):
        if self.stream:
            self.stream.close()
            self.stream = None


def read_handshake(stream):
    raw = stream.readline(4097)
    if not raw.endswith(b"\n") or len(raw) > 4096:
        raise ValueError("Invalid private desktop handshake")
    def unique(pairs):
        value = {}
        for key, item in pairs:
            if key in value:
                raise ValueError("Duplicate handshake field")
            value[key] = item
        return value
    value = json.loads(raw.decode("utf-8"), object_pairs_hook=unique)
    if (not isinstance(value, dict) or set(value) != {"key"} or
            not isinstance(value["key"], str) or not re.fullmatch(r"[0-9a-f]{64}", value["key"])):
        raise ValueError("Invalid private desktop handshake")
    return value["key"]


def require_normal_windows_token():
    if os.name != "nt":
        raise OSError("The desktop service requires Windows")
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    advapi = ctypes.WinDLL("advapi32", use_last_error=True)
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    advapi.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
    advapi.GetTokenInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p,
                                          wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
    token = wintypes.HANDLE()
    if not advapi.OpenProcessToken(kernel.GetCurrentProcess(), 8, ctypes.byref(token)):
        raise OSError("Cannot verify desktop privilege")
    try:
        elevated, size = wintypes.DWORD(), wintypes.DWORD()
        if (not advapi.GetTokenInformation(token, 20, ctypes.byref(elevated),
                                          ctypes.sizeof(elevated), ctypes.byref(size)) or elevated.value):
            raise OSError("Run Agent Foundry without administrator privileges")
    finally:
        kernel.CloseHandle(token)

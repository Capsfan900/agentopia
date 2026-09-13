"""Reproducible sidecar-only measurements; no agents, provider calls, or live Library writes."""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import http.client
import json
import os
from pathlib import Path
import platform
import queue
import secrets
import socket
import statistics
import subprocess
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def process_sample(pid):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    class Memory(ctypes.Structure):
        _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
            (name, ctypes.c_size_t) for name in ("PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
            "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage")]
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Memory), wintypes.DWORD]
    handle = kernel.OpenProcess(0x410, False, pid)
    if not handle:
        raise OSError(ctypes.get_last_error(), "Cannot read owned process counters")
    try:
        stamps = [wintypes.FILETIME() for _ in range(4)]
        memory = Memory(); memory.cb = ctypes.sizeof(memory)
        if not kernel.GetProcessTimes(handle, *[ctypes.byref(x) for x in stamps]) or not psapi.GetProcessMemoryInfo(handle, ctypes.byref(memory), memory.cb):
            raise OSError(ctypes.get_last_error(), "Cannot read process counters")
        cpu = sum((stamp.dwHighDateTime << 32) | stamp.dwLowDateTime for stamp in stamps[2:]) / 10_000_000
        return {"cpu_seconds": cpu, "working_set_bytes": memory.WorkingSetSize, "peak_working_set_bytes": memory.PeakWorkingSetSize}
    finally:
        kernel.CloseHandle(handle)


def private_service(executable, app, directory, codex_home=None):
    started = time.perf_counter()
    command = [str(executable), "-I", "-S", "-B", "-u", str(app / "monitor.py"), "--desktop", "--data-dir", str(directory)]
    if codex_home is None:
        command += ["--adapter", "json", "--state-file", str(ROOT / "example-state.json")]
    else:
        command += ["--adapter", "codex", "--codex-home", str(codex_home)]
    child = subprocess.Popen(command,
        cwd=app, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        creationflags=subprocess.CREATE_NO_WINDOW)
    key = secrets.token_hex(32)
    try:
        child.stdin.write((json.dumps({"key": key}) + "\n").encode()); child.stdin.flush()
        result = queue.Queue(maxsize=1)
        reader = threading.Thread(target=lambda: result.put(child.stdout.readline(4097)), daemon=True)
        reader.start()
        ready = json.loads(result.get(timeout=10))
        if set(ready) != {"port", "pid"} or ready["pid"] != child.pid:
            raise RuntimeError("Owned private identity mismatch")
        connection = http.client.HTTPConnection("127.0.0.1", ready["port"], timeout=5)
        connection.request("GET", "/api/health", headers={"X-Foundry-Desktop": key})
        response = connection.getresponse()
        if response.status != 200 or len(response.read(4097)) > 4096:
            raise RuntimeError("Health check failed")
        connection.close()
        return child, ready["port"], key, (time.perf_counter() - started) * 1000
    except BaseException:
        child.terminate(); child.wait(timeout=5)
        child.stdin.close(); child.stdout.close(); child.stderr.close()
        raise


def stop(child):
    started = time.perf_counter()
    child.stdin.close()
    forced = False
    try:
        child.wait(timeout=3)
    except subprocess.TimeoutExpired:
        forced = True; child.terminate(); child.wait(timeout=3)
    child.stdout.close(); child.stderr.close()
    return {"milliseconds": round((time.perf_counter() - started) * 1000, 2), "forced": forced, "exit_code": child.returncode}


class SseDrain:
    """Discard SSE bytes continuously; retain only bounded counters."""
    def __init__(self, connection, response, transport):
        self.connection, self.response, self.transport = connection, response, transport
        self.stop_requested = threading.Event()
        self.lock = threading.Lock()
        self.bytes = self.events = self.errors = 0
        self.previous_lf = False
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()

    def run(self):
        while not self.stop_requested.is_set():
            try:
                block = self.response.read1(4096)
            except (OSError, http.client.HTTPException, socket.timeout):
                if not self.stop_requested.is_set():
                    with self.lock: self.errors += 1
                return
            if not block:
                if not self.stop_requested.is_set():
                    with self.lock: self.errors += 1
                return
            with self.lock:
                self.bytes += len(block)
                for value in block:
                    if self.previous_lf and value == 10: self.events += 1
                    self.previous_lf = value == 10

    def snapshot(self):
        with self.lock: return self.bytes, self.events, self.errors

    def close(self):
        self.stop_requested.set()
        try: self.transport.shutdown(socket.SHUT_RDWR)
        except OSError: pass
        self.thread.join(2)
        if self.thread.is_alive(): raise RuntimeError("SSE reader did not stop before deadline")
        self.response.close(); self.connection.close()
        _, _, errors = self.snapshot()
        if errors: raise RuntimeError("SSE reader failed before cleanup")


def check_reader():
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"
        def do_GET(self):
            self.send_response(200); self.send_header("Content-Type", "text/event-stream"); self.end_headers()
            self.wfile.write(b"data: a\n\n"); self.wfile.flush()
            time.sleep(1.25)
            self.wfile.write(b"data: b\n\n"); self.wfile.flush()
        def log_message(self, *_): pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    drain = None
    try:
        connection = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=8)
        connection.request("GET", "/")
        transport = connection.sock
        if transport is None: raise RuntimeError("SSE transport was unavailable")
        response = connection.getresponse()
        drain = SseDrain(connection, response, transport)
        time.sleep(1.5)
        bytes_read, events_read, errors = drain.snapshot()
        close_started = time.perf_counter(); drain.close()
        if bytes_read <= 0 or events_read < 2 or errors or time.perf_counter() - close_started >= 2:
            raise RuntimeError("SSE reader self-check failed")
        print(json.dumps({"reader_check": "passed", "bytes_drained": bytes_read, "events_drained": events_read}))
    finally:
        if drain is not None and drain.thread.is_alive():
            try: drain.close()
            except RuntimeError: pass
        server.shutdown(); server.server_close()


def measure(executable, app, label, codex_home=None):
    launches = []
    load = []
    trials = 1 if codex_home is not None else 3
    with tempfile.TemporaryDirectory(prefix="foundry-backend-measure-") as temporary:
        for trial in range(trials):
            child, port, key, elapsed = private_service(executable, app, Path(temporary) / str(trial), codex_home)
            drains = []
            try:
                launches.append({"pid": child.pid, "health_ready_ms": round(elapsed, 2)})
                if trial == trials - 1:
                    time.sleep(2)
                    for streams in (0, 1, 8):
                        while len(drains) < streams:
                            connection = http.client.HTTPConnection("127.0.0.1", port, timeout=8)
                            connection.request("GET", "/api/events", headers={"X-Foundry-Desktop": key})
                            transport = connection.sock
                            if transport is None: raise RuntimeError("SSE transport was unavailable")
                            response = connection.getresponse()
                            if response.status != 200:
                                raise RuntimeError("SSE failed")
                            drains.append(SseDrain(connection, response, transport))
                        before_bytes = sum(item.snapshot()[0] for item in drains)
                        before_events = sum(item.snapshot()[1] for item in drains)
                        before_errors = sum(item.snapshot()[2] for item in drains)
                        before = process_sample(child.pid); start = time.perf_counter(); time.sleep(5)
                        after = process_sample(child.pid); seconds = time.perf_counter() - start
                        bytes_read = sum(item.snapshot()[0] for item in drains) - before_bytes
                        events_read = sum(item.snapshot()[1] for item in drains) - before_events
                        errors = sum(item.snapshot()[2] for item in drains) - before_errors
                        if errors: raise RuntimeError("SSE reader failed during sample")
                        load.append({"sse_streams": streams, "sample_seconds": round(seconds, 3),
                            "cpu_percent_one_core": round((after["cpu_seconds"] - before["cpu_seconds"]) / seconds * 100, 3),
                            "cpu_percent_machine": round((after["cpu_seconds"] - before["cpu_seconds"]) / seconds * 100 / os.cpu_count(), 3),
                            "working_set_bytes": after["working_set_bytes"], "peak_working_set_bytes": after["peak_working_set_bytes"],
                            "sse_bytes_drained": bytes_read, "sse_events_drained": events_read})
            finally:
                close_errors = []
                for drain in drains:
                    try: drain.close()
                    except RuntimeError as error: close_errors.append(str(error))
                launches[-1]["shutdown"] = stop(child)
                if close_errors: raise RuntimeError("; ".join(close_errors))
    return {"label": label, "executable": str(executable), "app": str(app), "launches": launches,
        "first_process_health_ready_ms": launches[0]["health_ready_ms"],
        "warm_health_ready_median_ms": (statistics.median(item["health_ready_ms"] for item in launches[1:]) if len(launches) > 1 else None), "load": load}


def main():
    if os.name != "nt":
        raise SystemExit("Windows counters are required")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--real-feed", action="store_true", help="read an explicit Codex data folder through one private sidecar")
    parser.add_argument("--codex-home", type=Path, help="required with --real-feed; never inferred")
    parser.add_argument("--check-reader", action="store_true", help="run the bounded local SSE drain self-check")
    args = parser.parse_args()
    if args.check_reader:
        if args.real_feed or args.codex_home:
            parser.error("--check-reader cannot be combined with real-feed options")
        check_reader()
        return
    if args.real_feed != (args.codex_home is not None):
        parser.error("--real-feed and --codex-home must be supplied together")
    codex_home = args.codex_home.resolve() if args.codex_home else None
    if codex_home is not None and not codex_home.is_dir():
        parser.error("--codex-home must be an existing directory")
    manifest = json.loads((HERE / "bundle-manifest.json").read_text())
    for relative, digest in manifest["files"].items():
        if hashlib.sha256((HERE / "stage" / relative).read_bytes()).hexdigest() != digest:
            raise RuntimeError("Staged bundle changed; rebuild it before measuring")
    # Root source uses isolated -I mode too: its modules need an explicit trusted path entry.
    # The staged private interpreter has the reviewed ._pth; compare the same app through two runs.
    runtime = HERE / "stage/runtime/python/python.exe"
    real_feed = codex_home is not None
    result = {"schema_version": 1, "measured_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "os": platform.platform(),
        "logical_processors": os.cpu_count(), "fixture": ("read-only explicit Codex data folder" if real_feed else "example-state.json (4 supplied sessions, no provider activity)"),
        "staged_bytes": sum((HERE / "stage" / name).stat().st_size for name in manifest["files"]),
        "scope": ("One private authenticated port-0 sidecar; SSE bytes are discarded after bounded counting. "
                  "No WebView/Terminal/provider/LLM/Codex launch/Library or source writes."
                  if real_feed else "Python sidecar only. First launch is not a cold-disk-cache test. No WebView/Terminal/real-feed/GPU claims."),
        "results": [measure(runtime, HERE / "stage/app", "private staged real-feed" if real_feed else "private staged sidecar baseline", codex_home)]}
    output = HERE / ("real-feed-performance.json" if real_feed else "backend-performance.json")
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()

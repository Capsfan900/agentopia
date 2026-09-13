"""Offline ownership/authentication checks; never opens the live Library."""
from http.client import HTTPConnection
from http.server import ThreadingHTTPServer
from io import BytesIO
from pathlib import Path
from tempfile import TemporaryDirectory
from threading import Thread
import json
import unittest

from desktop_runtime import DataDirectoryLease, read_handshake
from monitor import handler_for


class DesktopRuntimeTests(unittest.TestCase):
    def test_only_one_service_can_own_a_data_directory(self):
        with TemporaryDirectory() as folder:
            with DataDirectoryLease(Path(folder)):
                with self.assertRaises(OSError):
                    with DataDirectoryLease(Path(folder)):
                        self.fail("Second writer acquired the same directory")
            with DataDirectoryLease(Path(folder)):
                pass

    def test_private_handshake_is_bounded_strict_and_never_a_url_secret(self):
        secret = "a" * 64
        self.assertEqual(read_handshake(BytesIO(json.dumps({"key": secret}).encode() + b"\n")), secret)
        for raw in [b"", b"{}\n", b'{"key":"short"}\n', b"x" * 4097,
                    b'{"key":"' + secret.encode() + b'","extra":1}\n',
                    b'{"key":"' + secret.encode() + b'","key":"' + secret.encode() + b'"}\n']:
            with self.subTest(length=len(raw)), self.assertRaises(ValueError):
                read_handshake(BytesIO(raw))

    def test_desktop_secret_required_before_any_source_read_or_bootstrap(self):
        class Store:
            reads = 0
            def snapshot(self):
                self.reads += 1
                return {"sessions": []}
            def bootstrap(self):
                self.reads += 1
                return {}
        store = Store()
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(store, store, desktop_key="a" * 64))
        server.daemon_threads = True
        thread = Thread(target=server.serve_forever, kwargs={"poll_interval": .01})
        thread.start()
        try:
            def request(path, keys=(), method="GET"):
                conn = HTTPConnection("127.0.0.1", server.server_port, timeout=2)
                conn.putrequest(method, path)
                for key in keys:
                    conn.putheader("X-Foundry-Desktop", key)
                conn.endheaders()
                response = conn.getresponse()
                status = response.status
                response.read()
                conn.close()
                return status
            for path in ["/", "/observatory", "/foundry.css", "/api/state", "/api/bootstrap", "/api/events"]:
                for keys in [(), ("wrong",), ("a" * 64, "a" * 64)]:
                    self.assertEqual(request(path, keys), 403)
            self.assertEqual(request("/api/settings", (), "POST"), 403)
            self.assertEqual(store.reads, 0)
            self.assertEqual(request("/api/state", ("a" * 64,)), 200)
            self.assertEqual(store.reads, 1)
        finally:
            server.shutdown()
            server.server_close()
            thread.join()


if __name__ == "__main__":
    unittest.main()

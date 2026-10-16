#!/usr/bin/env python3
"""Protocol tests for a stdio bridge binary against the stub server (no Rhino needed).

BRIDGE_CMD selects the binary, e.g.
    BRIDGE_CMD=dist/rhinomcp-bridge                  python3 tests/test_bridge_protocol.py   (Swift, macOS)
    BRIDGE_CMD="mono dist/rhinomcp-bridge.exe"       python3 tests/test_bridge_protocol.py   (C#, macOS via Mono)
    set BRIDGE_CMD=dist\\rhinomcp-bridge.exe         python -m unittest tests.test_bridge_protocol   (Windows)
Defaults: dist/rhinomcp-bridge(.exe) next to the repo root.
"""
import json
import os
import queue
import shlex
import subprocess
import sys
import tempfile
import threading
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
from stub_server import StubServer  # noqa: E402


def bridge_cmd():
    cmd = os.environ.get("BRIDGE_CMD")
    if not cmd:
        exe = os.path.join(ROOT, "dist", "rhinomcp-bridge.exe" if os.name == "nt" else "rhinomcp-bridge")
        return [exe]
    parts = shlex.split(cmd, posix=(os.name != "nt"))
    if os.name == "nt":  # non-POSIX split keeps the quotes around "C:\\path with spaces\\x.exe"
        parts = [x[1:-1] if len(x) > 1 and x[0] == x[-1] == '"' else x for x in parts]
    return parts


class Bridge:
    """A running bridge process with line-based I/O (stdout read on a thread so tests can time out)."""

    def __init__(self, data_dir):
        env = dict(os.environ, RHINOMCP_DATA_DIR=data_dir)
        self.stderr_file = tempfile.TemporaryFile()
        self.p = subprocess.Popen(bridge_cmd(), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=self.stderr_file, env=env, cwd=ROOT)
        self.lines = queue.Queue()
        self.raw = []
        threading.Thread(target=self._pump, daemon=True).start()
        self.n = 0

    def _pump(self):
        for raw in iter(self.p.stdout.readline, b""):
            self.raw.append(raw)
            self.lines.put(raw)
        self.lines.put(None)

    def send_raw(self, text):
        self.p.stdin.write(text.encode("utf-8") + b"\n")
        self.p.stdin.flush()

    def read(self, timeout=30):
        raw = self.lines.get(timeout=timeout)
        assert raw is not None, "bridge closed stdout; stderr: " + self.stderr()
        assert raw.endswith(b"\n") and raw.count(b"\n") == 1, "not exactly one line: %r" % raw
        assert not raw.startswith(b"\xef\xbb\xbf"), "BOM on stdout"
        return json.loads(raw.decode("utf-8"))

    def rpc(self, method, params=None):
        self.n += 1
        msg = {"jsonrpc": "2.0", "id": self.n, "method": method}
        if params is not None:
            msg["params"] = params
        self.send_raw(json.dumps(msg, ensure_ascii=False))
        r = self.read()
        while "id" not in r:  # skip notifications such as list_changed; tests that care read them explicitly
            r = self.read()
        assert r["id"] == self.n, r
        return r

    def notify(self, method):
        self.send_raw(json.dumps({"jsonrpc": "2.0", "method": method}))

    def handshake(self):
        r = self.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                    "clientInfo": {"name": "t", "version": "0"}})
        self.notify("notifications/initialized")
        return r

    def stderr(self):
        self.stderr_file.seek(0)
        return self.stderr_file.read().decode("utf-8", "replace")

    def close(self):
        try:
            self.p.stdin.close()
            self.p.wait(timeout=10)
        except Exception:
            self.p.kill()
        return self.p.returncode


class BridgeTestCase(unittest.TestCase):
    def setUp(self):
        self.stub = None
        self.bridge = None
        self.tmp = tempfile.mkdtemp(prefix="rhinomcp-bt-")
        self.addCleanup(self._cleanup)

    def _cleanup(self):
        if self.bridge:
            self.bridge.close()
        if self.stub:
            self.stub.stop()

    def start(self, with_stub=True, **stub_kw):
        if with_stub:
            self.stub = StubServer(self.tmp, **stub_kw).start()
        self.bridge = Bridge(self.tmp)
        return self.bridge


class LiveTests(BridgeTestCase):
    def test_initialize_tools_call_roundtrip(self):
        b = self.start()
        r = b.handshake()
        self.assertEqual(r["result"]["serverInfo"]["name"], "rhino-mcp-stub")
        self.assertEqual(r["result"]["protocolVersion"], "2025-06-18")
        names = [t["name"] for t in b.rpc("tools/list")["result"]["tools"]]
        self.assertEqual(names, ["echo", "context_get"])
        r = b.rpc("tools/call", {"name": "context_get", "arguments": {}})
        self.assertFalse(r["result"]["isError"])
        self.assertEqual(b.rpc("ping")["result"], {})
        self.assertEqual(b.close(), 0)

    def test_requests_carry_loopback_host_and_token(self):
        b = self.start()
        b.handshake()
        b.rpc("tools/list")
        hosts = {h for (_, h, ok) in self.stub.requests if ok}
        self.assertTrue(hosts <= {"127.0.0.1:%d" % self.stub.port, "localhost:%d" % self.stub.port}, hosts)
        self.assertTrue(all(ok for (_, _, ok) in self.stub.requests))

    def test_notification_produces_no_output(self):
        b = self.start()
        b.handshake()
        b.notify("notifications/cancelled")
        r = b.rpc("ping")  # the very next line must be the ping response, not something for the notification
        self.assertEqual(r["result"], {})
        self.assertEqual(len(b.raw), 2, [x[:80] for x in b.raw])  # initialize + ping responses only

    def test_unicode_roundtrip(self):
        b = self.start()
        b.handshake()
        text = "Grüße éè 中文 ☃ \U0001F600 µm ⌀ ok"
        r = b.rpc("tools/call", {"name": "echo", "arguments": {"text": text}})
        self.assertEqual(r["result"]["content"][0]["text"], text)

    def test_malformed_input_gets_parse_error_and_bridge_survives(self):
        b = self.start()
        b.send_raw("{not json")
        r = b.read()
        self.assertEqual(r["error"]["code"], -32700)
        self.assertEqual(b.rpc("ping")["result"], {})

    def test_blank_lines_and_crlf_are_ignored(self):
        b = self.start()
        b.send_raw("")
        b.send_raw(json.dumps({"jsonrpc": "2.0", "id": 99, "method": "ping"}) + "\r")
        self.assertEqual(b.read()["id"], 99)

    def test_jsonrpc_error_from_server_is_forwarded(self):
        b = self.start()
        b.handshake()
        r = b.rpc("no/such/method")  # stub answers a JSON-RPC error body with 200; forwarded verbatim
        self.assertEqual(r["error"]["code"], -32601)


class PlaceholderTests(BridgeTestCase):
    def test_placeholder_when_no_instance(self):
        b = self.start(with_stub=False)
        r = b.handshake()
        self.assertIn("Rhino is not reachable", r["result"]["instructions"])
        self.assertTrue(r["result"]["capabilities"]["tools"]["listChanged"])
        tools = b.rpc("tools/list")["result"]["tools"]
        self.assertEqual([t["name"] for t in tools], ["rhino_status"])
        r = b.rpc("tools/call", {"name": "rhino_status", "arguments": {}})
        self.assertIn("Open Rhino 7", r["result"]["content"][0]["text"])
        self.assertFalse(r["result"]["isError"])
        r = b.rpc("tools/call", {"name": "geometry_create", "arguments": {}})
        self.assertTrue(r["result"]["isError"])
        self.assertEqual(b.rpc("no/such")["error"]["code"], -32601)
        self.assertEqual(b.close(), 0)

    def test_stale_instance_file_with_dead_pid_is_ignored(self):
        os.makedirs(os.path.join(self.tmp, "instances"))
        with open(os.path.join(self.tmp, "config.json"), "w") as f:
            json.dump({"token": "x"}, f)
        with open(os.path.join(self.tmp, "instances", "999999.json"), "w") as f:
            json.dump({"pid": 999999, "port": 7340}, f)
        b = self.start(with_stub=False)
        b.handshake()
        self.assertEqual([t["name"] for t in b.rpc("tools/list")["result"]["tools"]], ["rhino_status"])

    def test_no_token_means_placeholder(self):
        s = StubServer(self.tmp).start()
        self.stub = s
        os.remove(os.path.join(self.tmp, "config.json"))
        b = self.start(with_stub=False)
        b.handshake()
        self.assertEqual([t["name"] for t in b.rpc("tools/list")["result"]["tools"]], ["rhino_status"])

    def test_list_changed_when_rhino_goes_away_and_returns(self):
        b = self.start()
        b.handshake()
        self.assertEqual(len(b.rpc("tools/list")["result"]["tools"]), 2)
        self.stub.stop()  # Rhino quits: server gone, instance file removed
        b.n += 1
        b.send_raw(json.dumps({"jsonrpc": "2.0", "id": b.n, "method": "tools/list"}))
        first = b.read()
        self.assertEqual(first.get("method"), "notifications/tools/list_changed", first)
        second = b.read()
        self.assertEqual([t["name"] for t in second["result"]["tools"]], ["rhino_status"])
        self.stub = StubServer(self.tmp).start()  # Rhino back (new port)
        b.n += 1
        b.send_raw(json.dumps({"jsonrpc": "2.0", "id": b.n, "method": "tools/list"}))
        self.assertEqual(b.read().get("method"), "notifications/tools/list_changed")
        self.assertEqual(len(b.read()["result"]["tools"]), 2)


class RediscoveryTests(BridgeTestCase):
    def test_rediscovery_after_stub_restart_on_new_port(self):
        b = self.start()
        b.handshake()
        old_port = self.stub.port
        self.stub.stop()
        self.stub = StubServer(self.tmp, port=0).start()
        if self.stub.port == old_port:  # astronomically unlikely, but make the test meaningful
            self.stub.stop()
            self.stub = StubServer(self.tmp, port=0).start()
        names = [t["name"] for t in b.rpc("tools/list")["result"]["tools"]]
        self.assertEqual(names, ["echo", "context_get"])
        self.assertTrue(any(ok and m == "tools/list" for (m, _, ok) in self.stub.requests))

    def test_401_triggers_rediscovery_with_fresh_token(self):
        b = self.start()
        b.handshake()
        self.stub.rotate_token(publish=False)  # stale config.json: first attempt 401s, the bridge re-reads the config
        r = b.rpc("tools/list")
        self.assertEqual(len(r["result"]["tools"]), 2)
        self.assertIn(False, [ok for (_, _, ok) in self.stub.requests])  # a 401 really happened
        self.assertIn("401", b.stderr())

    def test_persistent_401_falls_back_to_placeholder(self):
        b = self.start()
        b.handshake()
        with open(os.path.join(self.tmp, "config.json"), "w") as f:
            json.dump({"token": "wrong"}, f)  # stub keeps its own token
        r = b.rpc("tools/list")
        self.assertEqual([t["name"] for t in r["result"]["tools"]], ["rhino_status"])

    def test_newest_instance_wins(self):
        old = StubServer(os.path.join(self.tmp, "a")).start()
        self.addCleanup(old.stop)
        self.stub = StubServer(self.tmp).start()
        # Put both instance files in the same data dir: old one first, then the stub (newer mtime).
        os.makedirs(os.path.join(self.tmp, "instances"), exist_ok=True)
        with open(os.path.join(self.tmp, "instances", "1.json"), "w") as f:
            json.dump({"pid": os.getpid(), "port": old.port}, f)
        past = 1_000_000_000
        os.utime(os.path.join(self.tmp, "instances", "1.json"), (past, past))
        b = self.start(with_stub=False)
        b.handshake()
        b.rpc("tools/list")
        self.assertTrue(self.stub.requests)
        self.assertFalse(old.requests)

    def test_localhost_fallback_when_only_ipv6_loopback_listens(self):
        try:
            self.stub = StubServer(self.tmp, ipv6_only=True).start()
        except OSError:
            self.skipTest("IPv6 loopback not available")
        b = self.start(with_stub=False)
        r = b.handshake()
        if "rhino-mcp-stub" not in json.dumps(r):
            self.skipTest("'localhost' does not resolve to ::1 on this machine")
        self.assertTrue(any(h.startswith("localhost:") for (_, h, ok) in self.stub.requests if ok))


if __name__ == "__main__":
    unittest.main(verbosity=2)

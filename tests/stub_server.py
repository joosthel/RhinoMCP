#!/usr/bin/env python3
"""Stdlib MCP stub standing in for the RhinoMCP plugin (HTTP POST /mcp) for bridge protocol tests.

Mimics the plugin's rules: Bearer token (401 otherwise), Host must be 127.0.0.1:<port> or localhost:<port> (403
otherwise), no Origin header (403 otherwise). Writes <data_dir>/config.json {token} and
<data_dir>/instances/<pid>.json {pid,port,version,startedAt} like the real plugin does.

Library use:  s = StubServer(data_dir); s.start(); ...; s.stop()
CLI use:      python stub_server.py [--data-dir DIR] [--port N]   (prints the data dir; Ctrl-C to stop)
"""
import json
import os
import secrets
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

TOOLS = [
    {
        "name": "echo",
        "description": "Returns its text argument.",
        "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}}},
    },
    {
        "name": "context_get",
        "description": "Stub context.",
        "inputSchema": {"type": "object", "properties": {}},
    },
]


class _V6Server(ThreadingHTTPServer):
    import socket as _s
    address_family = _s.AF_INET6
    daemon_threads = True


class _V4Server(ThreadingHTTPServer):
    daemon_threads = True


class StubServer:
    def __init__(self, data_dir=None, port=0, token=None, ipv6_only=False):
        self.data_dir = data_dir or tempfile.mkdtemp(prefix="rhinomcp-stub-")
        self.requested_port = port
        self.token = token or secrets.token_hex(16)
        self.ipv6_only = ipv6_only
        self.requests = []  # (method, host header, auth ok) of every POST seen
        self.httpd = None
        self.thread = None
        self.port = None
        self.publish_on_401 = False
        self.pid = os.getpid()  # a pid that is certainly alive

    # ---- files ----
    @property
    def instance_file(self):
        return os.path.join(self.data_dir, "instances", "%d.json" % self.pid)

    def write_config(self):
        os.makedirs(self.data_dir, exist_ok=True)
        with open(os.path.join(self.data_dir, "config.json"), "w", encoding="utf-8") as f:
            json.dump({"token": self.token, "port": self.port}, f)

    def write_instance(self):
        os.makedirs(os.path.join(self.data_dir, "instances"), exist_ok=True)
        with open(self.instance_file, "w", encoding="utf-8") as f:
            json.dump({"pid": self.pid, "port": self.port, "version": "stub",
                       "startedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}, f)

    def remove_instance(self):
        try:
            os.remove(self.instance_file)
        except OSError:
            pass

    def rotate_token(self, publish=True):
        """Change the accepted token. publish=False leaves config.json stale until the first 401 is served
        (simulates the plugin restarting with a new token while a client still holds the old one)."""
        self.token = secrets.token_hex(16)
        self.publish_on_401 = not publish
        if publish:
            self.write_config()

    # ---- lifecycle ----
    def start(self):
        stub = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *a):
                pass

            def _send(self, status, body=b"", ctype="application/json"):
                self.send_response(status)
                self.send_header("Content-Type", ctype)
                self.send_header("Content-Length", str(len(body)))
                self.send_header("Connection", "close")  # so stop() really severs clients
                self.close_connection = True
                self.end_headers()
                if body:
                    self.wfile.write(body)

            def do_GET(self):
                host = self.headers.get("Host", "")
                if self.path == "/health" and host in ("127.0.0.1:%d" % stub.port, "localhost:%d" % stub.port):
                    return self._send(200, json.dumps({"ok": True, "version": "stub", "port": stub.port}).encode())
                self._send(404, b"{}")

            def do_POST(self):
                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length)
                host = self.headers.get("Host", "")
                if self.headers.get("Origin") is not None:
                    return self._send(403, b'{"error":"origin"}')
                if host not in ("127.0.0.1:%d" % stub.port, "localhost:%d" % stub.port):
                    return self._send(403, b'{"error":"host"}')
                if self.headers.get("Authorization") != "Bearer " + stub.token:
                    stub.requests.append(("(unauthorized)", host, False))
                    if stub.publish_on_401:
                        stub.publish_on_401 = False
                        stub.write_config()
                    return self._send(401, b'{"error":"unauthorized"}')
                if self.path != "/mcp":
                    return self._send(404, b"{}")
                try:
                    msg = json.loads(raw.decode("utf-8"))
                except Exception:
                    return self._send(400, b'{"error":"bad json"}')
                method = msg.get("method", "")
                stub.requests.append((method, host, True))
                if "id" not in msg:
                    return self._send(202)
                self._send(200, json.dumps(stub.dispatch(msg), ensure_ascii=False).encode("utf-8"))

        cls = _V6Server if self.ipv6_only else _V4Server
        bind = "::1" if self.ipv6_only else "127.0.0.1"
        self.httpd = cls((bind, self.requested_port), Handler)
        self.port = self.httpd.server_address[1]
        self.write_config()
        self.write_instance()
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        self.thread.start()
        return self

    def stop(self, remove_instance=True):
        if self.httpd:
            self.httpd.shutdown()
            self.httpd.server_close()
            self.httpd = None
        if remove_instance:
            self.remove_instance()

    # ---- protocol ----
    def dispatch(self, msg):
        method, mid, params = msg.get("method"), msg.get("id"), msg.get("params") or {}

        def ok(result):
            return {"jsonrpc": "2.0", "id": mid, "result": result}

        if method == "initialize":
            return ok({"protocolVersion": params.get("protocolVersion", "2025-06-18"),
                       "capabilities": {"tools": {"listChanged": True}},
                       "serverInfo": {"name": "rhino-mcp-stub", "version": "0"}})
        if method == "ping":
            return ok({})
        if method == "tools/list":
            return ok({"tools": TOOLS})
        if method == "tools/call":
            name, args = params.get("name"), params.get("arguments") or {}
            if name == "echo":
                return ok({"content": [{"type": "text", "text": args.get("text", "")}], "isError": False})
            if name == "context_get":
                return ok({"content": [{"type": "text", "text": json.dumps({"ok": True, "data": {"stub": True}})}],
                           "isError": False})
            return ok({"content": [{"type": "text", "text": "unknown tool"}], "isError": True})
        return {"jsonrpc": "2.0", "id": mid, "error": {"code": -32601, "message": "Method not found: %s" % method}}


def main():
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--data-dir")
    ap.add_argument("--port", type=int, default=0)
    a = ap.parse_args()
    s = StubServer(a.data_dir, a.port).start()
    print("stub on 127.0.0.1:%d  RHINOMCP_DATA_DIR=%s" % (s.port, s.data_dir), flush=True)
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        s.stop()


if __name__ == "__main__":
    main()

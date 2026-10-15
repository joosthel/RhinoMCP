"""Minimal stdlib MCP client for the integration tests (talks to the live RhinoMCP server)."""
import glob
import json
import os
import urllib.error
import urllib.request

BASE = os.path.expanduser("~/Library/Application Support/RhinoMCP")


class ProtocolError(Exception):
    """JSON-RPC level error (-32xxx) or unexpected HTTP failure."""

    def __init__(self, message, code=None):
        super().__init__(message)
        self.code = code


def instance():
    """Newest live instance from the discovery files: dict(pid, port, ...)."""
    for f in sorted(glob.glob(os.path.join(BASE, "instances", "*.json")), key=os.path.getmtime, reverse=True):
        try:
            info = json.load(open(f))
            os.kill(int(info["pid"]), 0)
            return info
        except (OSError, ValueError, KeyError):
            continue
    raise RuntimeError("no live RhinoMCP instance found")


def token():
    return json.load(open(os.path.join(BASE, "config.json")))["token"]


def base_url():
    return "http://127.0.0.1:%d" % instance()["port"]


def http(method, path, body=None, headers=None, auth=True):
    """Raw HTTP call. Returns (status, headers, text). Never raises on HTTP error codes."""
    h = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    if auth:
        h["Authorization"] = "Bearer " + token()
    h.update(headers or {})
    data = body.encode() if isinstance(body, str) else body
    req = urllib.request.Request(base_url() + path, data=data, method=method, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=90) as r:
            return r.status, dict(r.headers), r.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read().decode()


def rpc(method, params=None, id=1):
    """Send a JSON-RPC request; returns the 'result'. Raises ProtocolError on JSON-RPC errors."""
    req = {"jsonrpc": "2.0", "id": id, "method": method}
    if params is not None:
        req["params"] = params
    status, _, text = http("POST", "/mcp", json.dumps(req))
    if status != 200:
        raise ProtocolError("HTTP %d: %s" % (status, text))
    doc = json.loads(text)
    if "error" in doc:
        raise ProtocolError(doc["error"].get("message", "error"), doc["error"].get("code"))
    return doc["result"]


def call(tool, args=None):
    """Call a tool. Returns the parsed payload dict ({ok, data|error, ...}); image blocks under '_images'.

    Tool-level failures (ok=false) are returned, not raised; protocol faults raise ProtocolError.
    """
    res = rpc("tools/call", {"name": tool, "arguments": args or {}})
    texts = [c["text"] for c in res.get("content", []) if c.get("type") == "text"]
    payload = json.loads(texts[0])
    payload["_isError"] = bool(res.get("isError"))
    imgs = [c for c in res.get("content", []) if c.get("type") == "image"]
    if imgs:
        payload["_images"] = imgs
    return payload

"""Minimal stdlib MCP client for the integration tests (talks to the live RhinoMCP server)."""
import glob
import json
import os
import sys
import urllib.error
import time
import unittest
import urllib.request

WINDOWS = sys.platform == "win32"


def data_dir(platform=None, env=None, home=None):
    """RhinoMCP data folder: Mac ~/Library/Application Support/RhinoMCP, Windows %APPDATA%\\RhinoMCP (RHINOMCP_DATA_DIR overrides).

    Pure given its arguments (platform: "win32" | other, env: mapping, home: user home), so it is unit-testable on any OS.
    """
    platform = sys.platform if platform is None else platform
    env = os.environ if env is None else env
    if env.get("RHINOMCP_DATA_DIR"):
        return env["RHINOMCP_DATA_DIR"]
    if platform == "win32":
        appdata = env.get("APPDATA") or os.path.join(home or os.path.expanduser("~"), "AppData", "Roaming")
        return os.path.join(appdata, "RhinoMCP")
    return os.path.join(home or os.path.expanduser("~"), "Library", "Application Support", "RhinoMCP")


BASE = data_dir()


def pid_alive(pid):
    """True if a process with this pid exists. (os.kill(pid, 0) is NOT safe on Windows: signal 0 is CTRL_C_EVENT.)"""
    pid = int(pid)
    if WINDOWS:
        import ctypes
        k32 = ctypes.windll.kernel32
        h = k32.OpenProcess(0x1000, False, pid)   # PROCESS_QUERY_LIMITED_INFORMATION
        if not h:
            return False
        code = ctypes.c_ulong()
        ok = k32.GetExitCodeProcess(h, ctypes.byref(code))
        k32.CloseHandle(h)
        return bool(ok) and code.value == 259     # STILL_ACTIVE
    try:
        os.kill(pid, 0)
        return True
    except PermissionError:
        return True
    except OSError:
        return False


class ProtocolError(Exception):
    """JSON-RPC level error (-32xxx) or unexpected HTTP failure."""

    def __init__(self, message, code=None):
        super().__init__(message)
        self.code = code


def instance():
    """Newest live instance from the discovery files: dict(pid, port, ...)."""
    for f in sorted(glob.glob(os.path.join(BASE, "instances", "*.json")), key=os.path.getmtime, reverse=True):
        try:
            with open(f) as fh:
                info = json.load(fh)
            if pid_alive(info["pid"]):
                return info
        except (OSError, ValueError, KeyError):
            continue
    raise RuntimeError("no live RhinoMCP instance found")


def token():
    with open(os.path.join(BASE, "config.json")) as f:
        return json.load(f)["token"]


def base_url():
    info = instance()
    # "host" is 127.0.0.1, or localhost after the server's Windows URL-ACL fallback (older discovery files lack it).
    return "http://%s:%d" % (info.get("host") or "127.0.0.1", info["port"])


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


def ensure_idle(attempts=10):
    """Cancel any command left waiting for input so one stuck command cannot cascade into the whole run.

    Calls the command_cancel tool (Escape into the Rhino document window, restoring it if minimized) until Rhino
    reports it is idle. Returns the last tool payload. Never raises; unknown tool (old DLL) just returns None.
    """
    last = None
    for _ in range(attempts):
        try:
            last = call("command_cancel")
        except Exception:
            return last
        if not last.get("ok") or not last["data"].get("was_in_command"):
            return last
        time.sleep(1.0)
    raise RuntimeError("Rhino is still stuck in a command after %d cancel attempts: %s" % (attempts, last))


def skip_unless_mac(reason="Mac-only behaviour"):
    return unittest.skipUnless(not WINDOWS, reason)


class IdleTestCase(unittest.TestCase):
    """TestCase whose setUpClass first makes sure Rhino is not stuck in an interactive command."""

    @classmethod
    def setUpClass(cls):
        ensure_idle()

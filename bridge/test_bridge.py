#!/usr/bin/env python3
"""Spawns dist/rhinomcp-bridge and exercises it. Read-only calls only.
Usage: bridge/test_bridge.py [--no-rhino]   (--no-rhino points the bridge at an empty data dir to test the placeholder)"""
import json, os, subprocess, sys, tempfile

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
exe = os.path.join(root, "dist", "rhinomcp-bridge")
env = dict(os.environ)
no_rhino = "--no-rhino" in sys.argv
if no_rhino:
    env["RHINOMCP_DATA_DIR"] = tempfile.mkdtemp()

p = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env, text=True)
n = 0
def rpc(method, params=None):
    global n
    n += 1
    p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": n, "method": method, "params": params or {}}) + "\n"); p.stdin.flush()
    line = p.stdout.readline()
    assert line.endswith("\n") and "\n" not in line.strip(), "not a single line"
    r = json.loads(line); assert r["id"] == n, r
    return r
def note(method):
    p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": method}) + "\n"); p.stdin.flush()

fails = 0
def check(name, cond, extra=""):
    global fails
    print(("PASS " if cond else "FAIL ") + name, extra if not cond else "")
    fails += 0 if cond else 1

r = rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "t", "version": "0"}})
check("initialize", "result" in r and r["result"]["protocolVersion"] == "2025-06-18", r)
note("notifications/initialized")
r = rpc("tools/list")
names = [t["name"] for t in r["result"]["tools"]]
if no_rhino:
    check("placeholder tool", names == ["rhino_status"], names)
    r = rpc("tools/call", {"name": "rhino_status", "arguments": {}})
    check("rhino_status text", "Open Rhino 7" in r["result"]["content"][0]["text"], r)
else:
    check("tools/list has context_get", "context_get" in names, names[:5])
    r = rpc("tools/call", {"name": "context_get", "arguments": {}})
    check("context_get ok", "result" in r and not r["result"].get("isError"), str(r)[:300])
    print("context_get ->", json.dumps(r["result"])[:200])
    r = rpc("ping")
    check("ping", "result" in r, r)
p.stdin.close(); p.wait(timeout=5)
check("clean exit", p.returncode == 0, p.returncode)
print("stderr:", p.stderr.read().strip()[:300])
sys.exit(1 if fails else 0)

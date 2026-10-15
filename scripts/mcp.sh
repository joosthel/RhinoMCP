#!/bin/bash
# Talk to the running RhinoMCP server.
#   mcp.sh tools/list
#   mcp.sh call <tool> '<json args>'
#   mcp.sh raw '<jsonrpc request>'
# Port comes from the newest live ~/Library/Application Support/RhinoMCP/instances/*.json, token from config.json.
set -euo pipefail

usage() { echo "usage: mcp.sh tools/list | call <tool> '<json>' | raw '<jsonrpc>'" >&2; exit 2; }
[ $# -ge 1 ] || usage
MODE="$1"
case "$MODE" in
  tools/list) REQ='{"jsonrpc":"2.0","id":1,"method":"tools/list"}' ;;
  call)
    [ $# -ge 2 ] || usage
    ARGS="${3:-}"
    [ -n "$ARGS" ] || ARGS='{}'
    REQ=$(python3 -c '
import json, sys
print(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": sys.argv[1], "arguments": json.loads(sys.argv[2])}}))
' "$2" "$ARGS") ;;
  raw) [ $# -ge 2 ] || usage; REQ="$2" ;;
  *) usage ;;
esac

python3 - "$REQ" <<'PY'
import glob, json, os, sys, urllib.request, urllib.error

base = os.path.expanduser("~/Library/Application Support/RhinoMCP")
best = None
for f in sorted(glob.glob(os.path.join(base, "instances", "*.json")), key=os.path.getmtime, reverse=True):
    try:
        info = json.load(open(f))
        os.kill(int(info["pid"]), 0)
        best = info
        break
    except (OSError, ValueError, KeyError):
        continue
if not best:
    sys.exit("no live RhinoMCP instance found (is the plugin installed and Rhino running? try MCPStatus)")
token = json.load(open(os.path.join(base, "config.json")))["token"]

req = urllib.request.Request(
    "http://127.0.0.1:%d/mcp" % best["port"], data=sys.argv[1].encode(), method="POST",
    headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream",
             "Authorization": "Bearer " + token})
try:
    with urllib.request.urlopen(req, timeout=90) as r:
        status, body = r.status, r.read().decode()
except urllib.error.HTTPError as e:
    status, body = e.code, e.read().decode()

if not body.strip():
    print("HTTP %d (empty body)" % status)
    sys.exit(0)
try:
    doc = json.loads(body)
except ValueError:
    print("HTTP %d\n%s" % (status, body)); sys.exit(1)
# Unwrap tools/call text blocks so the payload is readable.
res = doc.get("result") if isinstance(doc, dict) else None
if isinstance(res, dict) and isinstance(res.get("content"), list):
    for c in res["content"]:
        if c.get("type") == "text":
            try:
                c["text"] = json.loads(c["text"])
            except ValueError:
                pass
        elif c.get("type") == "image":
            c["data"] = "<%d base64 chars>" % len(c.get("data", ""))
print(json.dumps(doc, indent=2, ensure_ascii=False))
PY

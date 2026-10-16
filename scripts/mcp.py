#!/usr/bin/env python3
"""Talk to the running RhinoMCP server (Mac and Windows). Used by mcp.sh and dev-test.ps1.

  mcp.py tools/list
  mcp.py call <tool> '<json args>'
  mcp.py raw '<jsonrpc request>'
Port/host come from the newest live <data dir>/instances/*.json, token from <data dir>/config.json
(data dir: ~/Library/Application Support/RhinoMCP on Mac, %APPDATA%\\RhinoMCP on Windows; override with RHINOMCP_DATA_DIR).
"""
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tests"))
import mcp_client  # noqa: E402


def usage():
    sys.exit("usage: mcp.py tools/list | call <tool> '<json>' | raw '<jsonrpc>'")


def main(argv):
    if len(argv) < 1:
        usage()
    mode = argv[0]
    if mode == "tools/list":
        req = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/list"})
    elif mode == "call":
        if len(argv) < 2:
            usage()
        args = argv[2] if len(argv) > 2 and argv[2] else "{}"
        req = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                          "params": {"name": argv[1], "arguments": json.loads(args)}})
    elif mode == "raw":
        if len(argv) < 2:
            usage()
        req = argv[1]
    else:
        usage()

    try:
        status, _, body = mcp_client.http("POST", "/mcp", req)
    except RuntimeError as e:
        sys.exit("%s (is the plugin installed and Rhino running? try MCPStatus)" % e)
    if not body.strip():
        print("HTTP %d (empty body)" % status)
        return 0
    try:
        doc = json.loads(body)
    except ValueError:
        print("HTTP %d\n%s" % (status, body))
        return 1
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
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

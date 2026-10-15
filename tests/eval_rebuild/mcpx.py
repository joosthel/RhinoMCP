"""Logged MCP calls for the rebuild evaluation. Every call is appended to calls.jsonl."""
import base64, json, os, sys, time

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import mcp_client as c  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.path.join(HERE, "calls.jsonl")


def call(tool, args=None, purpose="", image_to=None, quiet=False):
    t0 = time.time()
    try:
        r = c.call(tool, args or {})
    except Exception as e:  # protocol fault
        r = {"ok": False, "error": {"code": "PROTOCOL", "message": str(e)}}
    ok = bool(r.get("ok"))
    rec = {"t": round(t0, 1), "tool": tool, "purpose": purpose, "ok": ok,
           "ms": int((time.time() - t0) * 1000)}
    if not ok:
        rec["error"] = r.get("error")
    with open(LOG, "a") as f:
        f.write(json.dumps(rec) + "\n")
    if image_to and r.get("_images"):
        with open(image_to, "wb") as f:
            f.write(base64.b64decode(r["_images"][0]["data"]))
    r.pop("_images", None)
    if not quiet:
        s = json.dumps(r)
        print("[%s] %s %s %s" % ("ok" if ok else "FAIL", tool, purpose, s[:600]))
    return r

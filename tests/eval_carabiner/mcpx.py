"""Logged MCP calls for the carabiner evaluation. Every call is appended to calls.jsonl with a phase tag."""
import base64, json, os, sys, time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import mcp_client as c  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.path.join(HERE, "calls.jsonl")
PHASE = os.environ.get("PHASE", "explore")


def call(tool, args=None, purpose="", image_to=None, quiet=False, maxlen=700):
    t0 = time.time()
    try:
        r = c.call(tool, args or {})
    except Exception as e:  # protocol fault / client timeout
        r = {"ok": False, "error": {"code": "PROTOCOL", "message": str(e)}}
    ok = bool(r.get("ok"))
    rec = {"t": round(t0, 1), "phase": PHASE, "tool": tool, "purpose": purpose, "ok": ok,
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
        print("[%s %dms] %s %s %s" % ("ok" if ok else "FAIL", rec["ms"], tool, purpose, json.dumps(r)[:maxlen]))
    return r


if __name__ == "__main__":  # python3 mcpx.py <tool> '<json>' [purpose] [image_to]
    a = sys.argv
    call(a[1], json.loads(a[2]) if len(a) > 2 else {}, a[3] if len(a) > 3 else "",
         a[4] if len(a) > 4 else None, maxlen=4000)

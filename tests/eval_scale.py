"""Scale evaluation: 20k simple objects, timings and response sizes of the model_* tools.

Run inside the shared-Rhino lock:   scripts/dev-test.sh tests/eval_scale.py
(A unittest module only so dev-test.sh can run it; not part of the regular suite.)

Creates N small boxes on _mcp_test::scale with run_python (batches of 2000), times model_overview, objects_query,
objects_near, verify_audit, model_changes, context_get, writes docs/eval/scale/{results.json,REPORT.md}, then deletes
everything and verifies that nothing is left (the test fails if it is).
"""
import json
import os
import time
import unittest

import mcp_client as c

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "docs", "eval", "scale")
LAYER = "_mcp_test::scale"
N = int(os.environ.get("SCALE_N", "20000"))
BATCH = 2000
COLS = 150

CREATE = """
import Rhino, scriptcontext as sc, System
import Rhino.Geometry as g
import Rhino.DocObjects as d
doc = sc.doc
idx = doc.Layers.FindByFullPath(args['layer'], -1)
if idx < 0:
    lay = d.Layer(); lay.Name = args['layer'].split('::')[-1]
    parent = '::'.join(args['layer'].split('::')[:-1])
    if parent:
        pi = doc.Layers.FindByFullPath(parent, -1)
        if pi < 0:
            pl = d.Layer(); pl.Name = parent; pi = doc.Layers.Add(pl)
        lay.ParentLayerId = doc.Layers[pi].Id
    idx = doc.Layers.Add(lay)
attr = d.ObjectAttributes(); attr.LayerIndex = idx
n = 0
for i in range(args['start'], args['start'] + args['count']):
    x = (i % args['cols']) * 12.0
    y = (i // args['cols']) * 12.0
    b = g.Brep.CreateFromBox(g.BoundingBox(g.Point3d(x, y, 0), g.Point3d(x + 5, y + 5, 5)))
    if b is not None and doc.Objects.AddBrep(b, attr) != System.Guid.Empty:
        n += 1
result = n
"""


def ok(r):
    assert r["ok"], r
    return r["data"]


class Timer:
    def __init__(self):
        self.rows = []

    def call(self, label, tool, args):
        t0 = time.time()
        r = c.call(tool, args)
        dt = time.time() - t0
        size = len(json.dumps({k: v for k, v in r.items() if not k.startswith("_")}, separators=(",", ":")))
        self.rows.append({"label": label, "tool": tool, "ok": r["ok"], "secs": round(dt, 3), "bytes": size,
                          "approx_tokens": int(size / 3.5)})
        return r


class ScaleEval(unittest.TestCase):
    def test_scale(self):
        T = Timer()
        res = {"n": N, "layer": LAYER}
        t_before = ok(c.call("model_changes", {}))["token"]
        base = T.call("context_get (baseline, before)", "context_get", {})
        base_total = ok(base)["objects"]["total"]
        try:
            t0 = time.time()
            made = 0
            for start in range(0, N, BATCH):
                r = c.call("run_python", {"code": CREATE, "args": {"layer": LAYER, "start": start, "count": min(BATCH, N - start), "cols": COLS}})
                made += ok(r)["result"]
            res["create_secs"] = round(time.time() - t0, 2)
            res["created"] = made
            self.assertEqual(made, N)

            # context_get for comparison
            T.call("context_get", "context_get", {})
            # journal
            T.call("model_changes since creation", "model_changes", {"since": t_before})
            # overview
            T.call("model_overview (all sections, whole doc)", "model_overview", {})
            T.call("model_overview (scope = scale layer)", "model_overview", {"scope": {"layer": LAYER}})
            T.call("model_overview (layers+totals only)", "model_overview", {"include": ["totals", "layers"]})
            # query
            T.call("objects_query group_by layer, limit 0", "objects_query", {"group_by": "layer", "limit": 0})
            T.call("objects_query group_by type, scale layer, limit 0", "objects_query", {"filters": {"layer": LAYER}, "group_by": "type", "limit": 0})
            T.call("objects_query rows limit 50", "objects_query", {"filters": {"layer": LAYER}, "limit": 50})
            # near
            mid = [COLS * 12.0 / 2, (N // COLS) * 12.0 / 2, 2.5]
            T.call("objects_near cold (builds RTree)", "objects_near", {"point": mid, "limit": 20})
            T.call("objects_near warm, nearest 20", "objects_near", {"point": mid, "limit": 20})
            T.call("objects_near warm, radius 50", "objects_near", {"point": mid, "radius": 50, "limit": 50})
            T.call("objects_near warm, bbox", "objects_near", {"bbox": {"min": [mid[0] - 30, mid[1] - 30, 0], "max": [mid[0] + 30, mid[1] + 30, 5]}, "limit": 50})
            # audit
            T.call("verify_audit whole model", "verify_audit", {})
            T.call("verify_audit since creation", "verify_audit", {"since": t_before, "checks": ["invalid", "tiny", "far", "duplicates"]})
            T.call("verify_audit target = scale layer, fast checks", "verify_audit", {"target": {"query": {"layer": LAYER}}, "checks": ["tiny", "far"]})
            # incremental change: one add, then near must rebuild
            t1 = ok(c.call("model_changes", {}))["token"]
            c.call("run_python", {"code": CREATE, "args": {"layer": LAYER, "start": N, "count": 1, "cols": COLS}})
            T.call("model_changes since 1 add", "model_changes", {"since": t1})
            T.call("objects_near after 1 add (rebuild)", "objects_near", {"point": mid, "limit": 20})
            T.call("verify_audit since 1 add", "verify_audit", {"since": t1})
            res["rows"] = T.rows
        finally:
            # delete everything we made and verify
            t0 = time.time()
            q = c.call("objects_query", {"filters": {"layer": LAYER}, "limit": 0})
            total = ok(q)["total"] if q["ok"] else 0
            if total:
                ok(c.call("objects_delete", {"target": {"query": {"layer": LAYER}}, "confirm_count": total}))
            res["delete_secs"] = round(time.time() - t0, 2)
            res["deleted"] = total
            left = ok(c.call("objects_query", {"filters": {"layer": LAYER}, "limit": 0}))["total"]
            c.call("run_python", {"code": "import rhinoscriptsyntax as rs\nrs.CurrentLayer('Default')\n"
                                          "for n in sorted(list(rs.LayerNames() or []), key=len, reverse=True):\n"
                                          "    if n == '_mcp_test::scale':\n"
                                          "        try: rs.DeleteLayer(n)\n"
                                          "        except Exception: pass\n"})
            after = ok(c.call("context_get", {}))["objects"]["total"]
            res["left_on_layer"] = left
            res["doc_total_before"] = base_total
            res["doc_total_after"] = after
            self._write(res)
        self.assertEqual(left, 0)
        self.assertEqual(after, base_total)

    def _write(self, res):
        os.makedirs(OUT, exist_ok=True)
        json.dump(res, open(os.path.join(OUT, "results.json"), "w"), indent=1)
        lines = ["# Scale evaluation: %d objects" % res["n"], "",
                 "Generated by `tests/eval_scale.py` on the live Rhino 7.39 Mac (objects: small boxes on `%s`, created with `run_python` in batches of %d)." % (res["layer"], BATCH), "",
                 "- create: %s s for %s objects (includes the change-journal handlers firing once per object)" % (res.get("create_secs"), res.get("created")),
                 "- delete: %s s for %s objects; left on layer afterwards: %s; document total before/after: %s / %s" % (
                     res.get("delete_secs"), res.get("deleted"), res.get("left_on_layer"), res.get("doc_total_before"), res.get("doc_total_after")),
                 "", "| call | time (s) | response bytes | ~tokens |", "|---|---:|---:|---:|"]
        for r in res.get("rows", []):
            lines.append("| %s | %.3f | %d | %d |" % (r["label"], r["secs"], r["bytes"], r["approx_tokens"]))
        lines += ["", "Tokens are estimated as bytes / 3.5 (compact JSON).", ""]
        open(os.path.join(OUT, "REPORT.md"), "w").write("\n".join(lines))


if __name__ == "__main__":
    unittest.main()

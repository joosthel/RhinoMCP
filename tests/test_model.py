import glob
import json
import os
import shutil
import tempfile
import unittest

import mcp_client as c

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LAYER = "_mcp_test::model"


def ok(r):
    assert r["ok"], r
    return r["data"]


def box(x, y=0, z=0, size=10, layer=LAYER, **extra):
    it = {"kind": "box", "corner": [x, y, z], "size": [size, size, size]}
    it.update(extra)
    d = ok(c.call("geometry_create", {"items": [it], "layer": layer}))
    assert d["items"][0]["ok"], d
    return d["items"][0]["handle"]


def py(code, args=None):
    r = c.call("run_python", {"code": code, "args": args or {}})
    assert r["ok"], r
    return r["data"].get("result")


def token():
    return ok(c.call("model_changes", {}))["token"]


def since(t, **kw):
    a = {"since": t}
    a.update(kw)
    return ok(c.call("model_changes", a))


def find(nodes, name):
    for n in nodes["layers"]:
        if n["name"] == name:
            return n
    raise AssertionError("no node %s in %s" % (name, nodes))


def delete_layer_content():
    r = c.call("objects_query", {"filters": {"layer": "_mcp_test", "include_sublayers": True}, "limit": 0})
    if r["ok"] and r["data"]["total"]:
        ok(c.call("objects_delete", {"target": {"query": {"layer": "_mcp_test", "include_sublayers": True}},
                                     "confirm_count": r["data"]["total"]}))


DEL_LAYERS = ("import rhinoscriptsyntax as rs\n"
              "rs.CurrentLayer('Default')\n"
              "for n in sorted(list(rs.LayerNames() or []), key=len, reverse=True):\n"
              "    if n.startswith(args['P']):\n"
              "        try: rs.DeleteLayer(n)\n"
              "        except Exception: pass\n")


class ModelTests(c.IdleTestCase):
    @classmethod
    def setUpClass(cls):
        super().setUpClass()
        delete_layer_content()

    def tearDown(self):
        delete_layer_content()
        py(DEL_LAYERS, {"P": LAYER + "::"})

    @classmethod
    def tearDownClass(cls):
        delete_layer_content()
        py(DEL_LAYERS, {"P": "_mcp_test"})

    # ------------------------------------------------------------ overview
    def test_overview_totals_match_context(self):
        box(0)
        box(50)
        ctx = ok(c.call("context_get", {}))
        ov = ok(c.call("model_overview", {}))
        t = ov["totals"]
        self.assertEqual(t["objects"], ctx["objects"]["total"])
        self.assertEqual(t["layers"], ctx["layers"]["total"])
        self.assertEqual(t["units"], ctx["units"])
        self.assertAlmostEqual(t["abs_tol"], ctx["abs_tol"])
        self.assertEqual(t["named_views"], ctx["named_views"]["total"])
        for k in ("anomalies", "layer_tree", "blocks", "largest", "grid_top_view"):
            self.assertIn(k, ov)
        self.assertEqual(len(ov["grid_top_view"]["rows"]), 8)
        self.assertLess(len(json.dumps(ov)), 12000)

    def test_overview_scope_include_and_largest(self):
        small = box(0, size=1)
        big = box(100, size=40)
        ov = ok(c.call("model_overview", {"scope": {"layer": LAYER}, "include": ["totals", "largest"]}))
        self.assertEqual(ov["totals"]["objects"], 2)
        self.assertNotIn("anomalies", ov)
        self.assertEqual(ov["largest"][0]["handle"], big)
        self.assertEqual(ov["largest"][1]["handle"], small)

    def test_layer_tree_bounded_with_others(self):
        py("import rhinoscriptsyntax as rs\n"
           "for i in range(30):\n"
           "    n = '%s::s%02d' % (args['L'], i)\n"
           "    rs.AddLayer(n)\n"
           "    rs.CurrentLayer(n)\n"
           "    for k in range(i + 1):\n"
           "        rs.AddPoint((i, k, 0))\n"
           "rs.CurrentLayer('Default')\n", {"L": LAYER})
        scope = {"layer": "_mcp_test", "include_sublayers": True}
        ov = ok(c.call("model_overview", {"scope": scope, "include": ["layers"], "depth": 3, "top_k": 5}))
        root = find(ov["layer_tree"], "_mcp_test")
        model = find(root["children"], "model")
        kids = model["children"]
        self.assertEqual(len(kids["layers"]), 5)
        self.assertEqual(kids["others"]["layers"], 25)
        self.assertEqual(model["n"], sum(range(1, 31)))
        self.assertEqual(kids["layers"][0]["name"], "s29")
        self.assertLess(len(json.dumps(ov)), 4000)
        # depth=2 collapses the 30 sublayers into a count
        ov2 = ok(c.call("model_overview", {"scope": scope, "include": ["layers"], "depth": 2}))
        model2 = find(find(ov2["layer_tree"], "_mcp_test")["children"], "model")
        self.assertEqual(model2["sublayers"], 30)

    # ------------------------------------------------------------ journal
    def test_journal_add_delete_counts_and_tokens(self):
        t0 = token()
        hs = [box(0), box(20), box(40)]
        t1 = token()
        self.assertGreater(t1, t0)
        d = since(t0)
        self.assertEqual(d["by_kind"].get("add"), 3, d)
        self.assertEqual(d["by_actor"]["user"], 0, d)
        self.assertGreaterEqual(d["by_actor"]["agent"], 3, d)
        self.assertEqual(d["net"]["added"], 3)
        self.assertEqual(sorted(h["handle"] for h in d["handles"] if h["kind"] == "add"), sorted(hs))
        self.assertEqual(d["top_layers"][0], {"layer": LAYER, "n": 3})
        self.assertEqual(since(t1)["events"], 0)   # nothing newer than t1
        ok(c.call("objects_delete", {"target": hs[:2]}))
        d2 = since(t1)
        self.assertEqual(d2["by_kind"].get("delete"), 2, d2)
        self.assertEqual(d2["net"]["deleted"], 2)
        self.assertGreater(d2["token"], t1)
        d3 = since(t0)   # whole window: created-and-deleted objects cancel out
        self.assertEqual(d3["by_kind"].get("add"), 3)
        self.assertEqual(d3["by_kind"].get("delete"), 2)
        self.assertEqual(d3["net"]["added"], 1)
        self.assertEqual(d3["net"]["deleted"], 0)
        only = since(t0, kind="delete")
        self.assertEqual(only["handles_matched"], 2)
        self.assertEqual(sorted(h["handle"] for h in only["handles"]), sorted(hs[:2]))

    def test_changes_after_undo(self):
        h = box(0)
        t = token()
        ok(c.call("history_undo", {}))
        d = since(t)
        self.assertEqual(d["by_kind"].get("delete"), 1, d)
        self.assertEqual(d["net"]["deleted"], 1)
        self.assertEqual(d["handles"][0]["handle"], h)
        t2 = token()
        ok(c.call("history_redo", {}))
        d2 = since(t2)
        self.assertGreaterEqual(d2["events"], 1)
        self.assertEqual(d2["handles"][0]["handle"], h)
        self.assertIn(d2["handles"][0]["kind"], ("undelete", "add"))

    def test_changes_modify_and_reset(self):
        h = box(0)
        t = token()
        ok(c.call("objects_set_props", {"target": h, "name": "renamed"}))
        d = since(t)
        self.assertGreaterEqual(d["events"], 1, d)
        self.assertEqual(d["handles"][0]["handle"], h)
        r = ok(c.call("model_changes", {"since": t, "reset": True}))
        self.assertIn("reset_at", r)
        self.assertEqual(since(0)["events"], 0)
        self.assertGreaterEqual(token(), r["reset_at"])   # token keeps counting

    def test_layer_events(self):
        t = token()
        box(0, layer=LAYER + "::fresh")
        d = since(t)
        names = [e["ev"] for e in d.get("layer_events", [])]
        self.assertIn("layer_added", names, d)

    def test_changes_arg_errors(self):
        r = c.call("model_changes", {"kind": "nope", "actor": "robot"})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")

    # ------------------------------------------------------------ audit
    def test_audit_flags_tiny_far_dup_role(self):
        tol = ok(c.call("context_get", {}))["abs_tol"]
        t = token()
        tiny = box(0, size=tol * 2)
        far = box(3e5)
        box(30)
        b = box(30)
        wall = box(60, user_text={"ai.role": "wall"})
        d = ok(c.call("verify_audit", {"since": t}))
        checks = {i["check"]: i for i in d["issues"]}
        self.assertEqual(d["health"], "warn", d)
        self.assertEqual(d["scope"]["kind"], "since")
        self.assertIn(tiny, [e["handle"] for e in checks["tiny"]["examples"]])
        self.assertIn(far, [e["handle"] for e in checks["far"]["examples"]])
        self.assertEqual(checks["duplicates"]["count"], 1)
        self.assertIn(b, [e["handle"] for e in checks["duplicates"]["examples"]])
        self.assertEqual(checks["role_layer"]["examples"][0]["handle"], wall)
        self.assertNotIn("invalid", checks)
        d2 = ok(c.call("verify_audit", {"target": {"query": {"layer": LAYER}}, "checks": ["tiny", "far"]}))
        self.assertEqual(sorted(i["check"] for i in d2["issues"]), ["far", "tiny"])
        self.assertIn("global_checks", d2)

    def test_audit_whole_model_and_empty_layers(self):
        py("import rhinoscriptsyntax as rs\nrs.AddLayer(args['L'] + '::emptyone')\n", {"L": LAYER})
        d = ok(c.call("verify_audit", {"checks": ["empty_layers", "orphan_blocks", "invalid"]}))
        self.assertEqual(d["scope"]["kind"], "all")
        empties = [i for i in d["issues"] if i["check"] == "empty_layers"]
        self.assertTrue(empties and LAYER + "::emptyone" in empties[0]["examples"], d)

    def test_audit_arg_errors(self):
        r = c.call("verify_audit", {"since": 1, "target": "o1", "checks": ["bogus"]})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")

    # ------------------------------------------------------------ near
    def test_objects_near_nearest_of_three(self):
        h0, h1, h2 = box(0), box(100), box(200)
        flt = {"layer": LAYER}
        r = ok(c.call("objects_near", {"point": [95, 5, 5], "limit": 3, "filters": flt}))
        self.assertEqual([x["handle"] for x in r["rows"]], [h1, h0, h2])
        self.assertAlmostEqual(r["rows"][0]["distance"], 5.0, places=3)
        self.assertAlmostEqual(r["rows"][1]["distance"], 85.0, places=3)
        r2 = ok(c.call("objects_near", {"point": [95, 5, 5], "radius": 90, "filters": flt}))
        self.assertEqual([x["handle"] for x in r2["rows"]], [h1, h0])
        r3 = ok(c.call("objects_near", {"bbox": {"min": [-1, -1, -1], "max": [5, 5, 5]}, "filters": flt}))
        self.assertEqual([x["handle"] for x in r3["rows"]], [h0])
        # the cache is invalidated by changes: a new box must be found
        h3 = box(96)
        r4 = ok(c.call("objects_near", {"point": [95, 5, 5], "limit": 1, "filters": flt}))
        self.assertEqual(r4["rows"][0]["handle"], h3)
        self.assertAlmostEqual(r4["rows"][0]["distance"], 1.0, places=3)
        r5 = ok(c.call("objects_near", {"point": [95, 5, 5], "radius": 20, "exact": True, "filters": flt}))
        self.assertEqual(r5["distance_basis"], "geometry")
        self.assertEqual(r5["rows"][0]["handle"], h3)
        ok(c.call("objects_delete", {"target": h3}))
        r6 = ok(c.call("objects_near", {"point": [95, 5, 5], "limit": 1, "filters": flt}))
        self.assertEqual(r6["rows"][0]["handle"], h1)

    def test_objects_near_filter_with_many_unrelated_neighbours(self):
        # Regression: the nearest-k radius used to stop growing once `limit` unfiltered objects were inside it,
        # then the filter removed them and fewer rows than requested came back.
        h0, h1, h2 = box(0), box(100), box(200)
        junk = [box(90 + i, 20, 0, size=1, layer="_mcp_test::model_other") for i in range(8)]
        try:
            r = ok(c.call("objects_near", {"point": [95, 5, 5], "limit": 3, "filters": {"layer": LAYER}}))
            self.assertEqual([x["handle"] for x in r["rows"]], [h1, h0, h2])
        finally:
            ok(c.call("objects_delete", {"target": junk, "confirm_count": len(junk)}))

    def test_objects_near_arg_errors(self):
        self.assertFalse(c.call("objects_near", {})["ok"])
        r = c.call("objects_near", {"point": [0, 0, 0], "bbox": {"min": [0, 0, 0], "max": [1, 1, 1]}})
        self.assertFalse(r["ok"])

    # ------------------------------------------------------------ hot reload
    def test_reload_keeps_single_journal(self):
        dlls = sorted(glob.glob(os.path.join(ROOT, "dist", "RhinoMCP.Tools.*.dll")), key=os.path.getmtime)
        self.assertTrue(dlls)
        orig = dlls[-1]
        tmp = tempfile.mkdtemp(prefix="rhinomcp-reload-")
        gens = [since(0)["journal_generation"]]
        try:
            for i in (1, 2):
                copy = os.path.join(tmp, "RhinoMCP.Tools.zz%d.dll" % i)
                shutil.copy(orig, copy)
                ok(c.call("dev_reload", {"dll": copy}))
                gens.append(since(0)["journal_generation"])
                t = token()
                box(i * 20)
                d = since(t)
                self.assertEqual(d["by_kind"].get("add"), 1, "double counting after reload %d: %s" % (i, d))
                self.assertGreater(d["token"], t)
        finally:
            ok(c.call("dev_reload", {"dll": orig}))
            shutil.rmtree(tmp, ignore_errors=True)
        gens.append(since(0)["journal_generation"])
        self.assertEqual(sorted(gens), gens)
        self.assertGreater(gens[-1], gens[0], gens)
        t = token()
        box(300)
        self.assertEqual(since(t)["by_kind"].get("add"), 1)


if __name__ == "__main__":
    unittest.main()

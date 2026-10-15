import math
import unittest

import mcp_client as c

LAYER = "_mcp_test::geometry"


def ok(r):
    assert r["ok"], r
    return r["data"]


class GeometryTests(unittest.TestCase):
    def tearDown(self):
        # Everything created by these tests lives on LAYER: select it and delete it.
        c.call("run_command", {"command": '-_SelLayer "%s" _Enter' % LAYER})
        c.call("run_command", {"command": "_Delete"})

    # ---- helpers ----
    def make(self, *items, **kw):
        args = {"items": list(items), "layer": LAYER}
        args.update(kw)
        d = ok(c.call("geometry_create", args))
        return d["items"]

    def one(self, item):
        r = self.make(item)[0]
        self.assertTrue(r["ok"], r)
        return r

    def box(self, corner=(0, 0, 0), size=(10, 10, 10)):
        return self.one({"kind": "box", "corner": list(corner), "size": list(size)})["handle"]

    def validate(self, h):
        d = ok(c.call("geometry_validate", {"target": h}))
        return d["objects"][0]

    # ---- create ----
    def test_box_volume_bbox_and_layer(self):
        r = self.one({"kind": "box", "corner": [0, 0, 0], "size": [10, 10, 5], "name": "t_box"})
        self.assertAlmostEqual(r["volume"], 500.0, places=3)
        self.assertEqual(r["bbox"]["min"], [0, 0, 0])
        self.assertEqual(r["bbox"]["max"], [10, 10, 5])
        self.assertEqual(self.validate(r["handle"])["layer"], LAYER)

    def test_frames(self):
        r = self.one({"kind": "box", "corner": [0, 0, 0], "size": [1, 1, 1],
                      "plane": {"origin": [100, 0, 0], "x_axis": [1, 0, 0], "y_axis": [0, 1, 0]}})
        self.assertEqual(r["bbox"]["min"], [100, 0, 0])
        # world_yz: local x = world Y, local y = world Z, local z = world X
        r = self.one({"kind": "box", "corner": [0, 0, 0], "size": [1, 2, 3], "plane": "world_yz"})
        self.assertEqual(r["bbox"]["max"], [3, 1, 2])

    def test_batch_partial_failure(self):
        items = self.make({"kind": "sphere", "center": [0, 0, 0], "radius": 5},
                          {"kind": "sphere", "center": [0, 0, 0], "radius": -1},
                          {"kind": "nope"})
        self.assertTrue(items[0]["ok"])
        self.assertAlmostEqual(items[0]["volume"], 4.0 / 3 * math.pi * 125, delta=0.5)
        self.assertFalse(items[1]["ok"])
        self.assertIn("radius", items[1]["error"])
        self.assertFalse(items[2]["ok"])

    def test_all_fail_is_error(self):
        r = c.call("geometry_create", {"items": [{"kind": "line", "from": [0, 0, 0], "to": [0, 0, 0]}], "layer": LAYER})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "GEOMETRY_FAILED")

    def test_curves(self):
        items = self.make(
            {"kind": "line", "from": [0, 0, 0], "to": [3, 4, 0]},
            {"kind": "polyline", "points": [[0, 0, 0], [10, 0, 0], [10, 10, 0]]},
            {"kind": "circle", "center": [0, 0, 0], "radius": 2},
            {"kind": "arc", "start": [1, 0, 0], "through": [0, 1, 0], "end": [-1, 0, 0]},
            {"kind": "arc", "center": [0, 0, 0], "radius": 1, "start_angle_deg": 0, "end_angle_deg": 90},
            {"kind": "rectangle", "corner": [0, 0, 0], "width": 4, "height": 3},
            {"kind": "polygon", "center": [0, 0, 0], "radius": 1, "sides": 6},
            {"kind": "curve", "points": [[0, 0, 0], [5, 5, 0], [10, 0, 0], [15, 5, 0]], "degree": 3},
            {"kind": "curve", "points": [[0, 0, 0], [5, 5, 0], [10, 0, 0], [15, 5, 0]], "degree": 3, "mode": "control_points"},
        )
        for it in items:
            self.assertTrue(it["ok"], it)
        self.assertAlmostEqual(items[0]["length"], 5.0, places=4)
        self.assertAlmostEqual(items[1]["length"], 20.0, places=4)
        self.assertAlmostEqual(items[2]["length"], 4 * math.pi, places=3)
        self.assertAlmostEqual(items[3]["length"], math.pi, places=3)
        self.assertAlmostEqual(items[4]["length"], math.pi / 2, places=3)
        self.assertAlmostEqual(items[5]["length"], 14.0, places=4)
        self.assertAlmostEqual(items[6]["length"], 6.0, places=4)

    def test_primitives(self):
        items = self.make(
            {"kind": "cylinder", "base": [0, 0, 0], "radius": 2, "height": 10},
            {"kind": "cone", "base": [0, 0, 0], "radius": 3, "height": 6},
            {"kind": "extrusion", "points": [[0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0]], "height": 5},
            {"kind": "extrusion", "points": [[0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0]], "height": 5, "plane": "world_xy",
             "name": "ext2"},
        )
        for it in items:
            self.assertTrue(it["ok"], it)
        self.assertAlmostEqual(items[0]["volume"], math.pi * 4 * 10, delta=0.05)
        self.assertAlmostEqual(items[1]["volume"], math.pi * 9 * 6 / 3, delta=0.05)
        self.assertAlmostEqual(items[2]["volume"], 60.0, places=3)
        self.assertEqual(items[2]["bbox"]["max"][2], 5)
        # cone apex points up
        self.assertAlmostEqual(items[1]["bbox"]["max"][2], 6.0, places=3)

    def test_loft_pipe_revolve_sweep(self):
        a = self.one({"kind": "rectangle", "corner": [0, 0, 0], "width": 10, "height": 10})["handle"]
        b = self.one({"kind": "rectangle", "corner": [0, 0, 10], "width": 10, "height": 10})["handle"]
        loft = self.one({"kind": "loft", "curves": [a, b], "cap": True})
        self.assertAlmostEqual(loft["volume"], 1000.0, delta=0.01)
        rail = self.one({"kind": "line", "from": [50, 0, 0], "to": [50, 0, 10]})["handle"]
        pipe = self.one({"kind": "pipe", "curve": rail, "radius": 1, "cap": "flat"})
        self.assertAlmostEqual(pipe["volume"], math.pi * 10, delta=0.05)
        rev = self.one({"kind": "revolve", "points": [[2, 0, 0], [4, 0, 0], [4, 0, 5], [2, 0, 5]], "closed": True,
                        "axis": "z", "angle_deg": 360})
        self.assertAlmostEqual(rev["bbox"]["max"][0], 4.0, places=3)
        prof = self.one({"kind": "circle", "center": [100, 0, 0], "radius": 1})["handle"]
        rail2 = self.one({"kind": "line", "from": [100, 0, 0], "to": [100, 0, 8]})["handle"]
        sw = self.one({"kind": "sweep1", "rail": rail2, "profiles": [prof], "cap": True})
        self.assertAlmostEqual(sw["volume"], math.pi * 8, delta=0.05)

    def test_text_point_plane_surface(self):
        items = self.make({"kind": "text", "text": "hello", "at": [0, 0, 0], "height": 2},
                          {"kind": "text", "text": "dot", "at": [1, 1, 1], "as": "dot"},
                          {"kind": "point", "point": [1, 2, 3]},
                          {"kind": "plane_surface", "corner": [0, 0, 0], "width": 4, "height": 5})
        for it in items:
            self.assertTrue(it["ok"], it)
        self.assertAlmostEqual(items[3]["area"], 20.0, places=3)

    # ---- transform ----
    def test_transform_ops(self):
        h = self.box()
        d = ok(c.call("geometry_transform", {"target": h, "op": "move", "vector": [5, 0, 0]}))
        self.assertEqual(d["modified"], [h])
        self.assertEqual(d["bbox"]["min"], [5, 0, 0])
        d = ok(c.call("geometry_transform", {"target": h, "op": "move", "vector": [0, 20, 0], "copy": True}))
        self.assertEqual(len(d["created"]), 1)
        self.assertNotEqual(d["created"][0], h)
        self.assertEqual(d["bbox"]["min"], [5, 20, 0])
        d = ok(c.call("geometry_transform", {"target": h, "op": "scale", "factor": 2, "origin": [5, 0, 0]}))
        self.assertEqual(d["bbox"]["max"], [25, 20, 20])
        self.assertAlmostEqual(self.validate(h)["volume"], 8000.0, delta=0.01)
        b2 = self.box((100, 0, 0), (10, 10, 10))
        d = ok(c.call("geometry_transform", {"target": b2, "op": "rotate", "angle_deg": 90, "center": [100, 0, 0]}))
        self.assertEqual(d["bbox"]["min"][0], 90)
        d = ok(c.call("geometry_transform", {"target": b2, "op": "mirror", "plane": {
            "origin": [0, 0, 0], "x_axis": [0, 1, 0], "y_axis": [0, 0, 1]}, "copy": True}))
        self.assertAlmostEqual(d["bbox"]["max"][0], -90, places=4)
        b3 = self.box((200, 0, 0), (1, 1, 1))
        d = ok(c.call("geometry_transform", {"target": b3, "op": "orient", "from": "world_xy",
                                             "to": {"origin": [0, 0, 50], "x_axis": [1, 0, 0], "y_axis": [0, 1, 0]}}))
        self.assertEqual(d["bbox"]["min"][2], 50)

    def test_arrays(self):
        h = self.box((0, 0, 0), (1, 1, 1))
        d = ok(c.call("geometry_transform", {"target": h, "op": "array_linear", "count": 4, "vector": [3, 0, 0]}))
        self.assertEqual(len(d["created"]), 3)
        self.assertEqual(d["bbox"]["max"][0], 10)
        d = ok(c.call("geometry_transform", {"target": h, "op": "array_grid", "nx": 2, "ny": 2, "nz": 2, "spacing": 5}))
        self.assertEqual(len(d["created"]), 7)
        d = ok(c.call("geometry_transform", {"target": h, "op": "array_polar", "count": 4, "center": [0, 0, 100]}))
        self.assertEqual(len(d["created"]), 3)
        r = c.call("geometry_transform", {"target": h, "op": "array_linear", "count": 1, "vector": [1, 0, 0]})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")

    # ---- boolean ----
    def test_boolean_union_difference_intersection(self):
        a = self.box((0, 0, 0), (10, 10, 10))
        b = self.box((5, 0, 0), (10, 10, 10))
        d = ok(c.call("geometry_boolean", {"op": "union", "a": [a, b]}))
        self.assertAlmostEqual(self.validate(d["created"][0])["volume"], 1500.0, delta=0.01)
        self.assertEqual(sorted(d["deleted"]), sorted([a, b]))
        self.assertEqual(d["step_used"], "document_tolerance")

        a = self.box((0, 0, 0), (10, 10, 10))
        b = self.box((5, 0, 0), (10, 10, 10))
        d = ok(c.call("geometry_boolean", {"op": "difference", "a": a, "b": b, "keep_inputs": True}))
        self.assertAlmostEqual(d["volume_result"], 500.0, delta=0.01)
        self.assertEqual(d["deleted"], [])
        d = ok(c.call("geometry_boolean", {"op": "intersection", "a": a, "b": b}))
        self.assertAlmostEqual(d["volume_result"], 500.0, delta=0.01)
        self.assertEqual(d["bbox"]["min"][0], 5)
        self.assertEqual(d["bbox"]["max"][0], 10)

    def test_boolean_split(self):
        a = self.box((0, 0, 0), (10, 10, 10))
        b = self.box((5, 5, 5), (10, 10, 10))
        d = ok(c.call("geometry_boolean", {"op": "split", "a": a, "b": b}))
        self.assertEqual(len(d["created"]), 2)

    def test_boolean_failure_not_solid(self):
        solid = self.box()
        srf = self.one({"kind": "plane_surface", "corner": [-5, -5, 5], "width": 30, "height": 30})["handle"]
        r = c.call("geometry_boolean", {"op": "difference", "a": solid, "b": srf})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "BOOLEAN_FAILED")
        det = r["error"]["details"]
        self.assertIn("not a closed solid", " ".join(det["notes"]))
        self.assertTrue(r["error"]["guidance"])
        self.assertFalse(det["inputs_changed"])
        # inputs untouched
        self.assertAlmostEqual(self.validate(solid)["volume"], 1000.0, delta=0.01)

    def test_boolean_failure_no_overlap_and_args(self):
        a = self.box((0, 0, 0), (1, 1, 1))
        b = self.box((50, 0, 0), (1, 1, 1))
        r = c.call("geometry_boolean", {"op": "intersection", "a": a, "b": b})
        self.assertEqual(r["error"]["code"], "BOOLEAN_FAILED")
        self.assertIn("do not overlap", " ".join(r["error"]["details"]["notes"]))
        r = c.call("geometry_boolean", {"op": "difference", "a": a})
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")

    # ---- edit ----
    def test_join_explode(self):
        items = self.make({"kind": "line", "from": [0, 0, 0], "to": [10, 0, 0]},
                          {"kind": "line", "from": [10, 0, 0], "to": [10, 10, 0]},
                          {"kind": "line", "from": [10, 10, 0], "to": [0, 0, 0]})
        hs = [i["handle"] for i in items]
        d = ok(c.call("geometry_edit", {"target": hs, "op": "join"}))
        self.assertEqual(len(d["created"]), 1)
        self.assertTrue(d["items"][0]["closed"])
        d2 = ok(c.call("geometry_edit", {"target": d["created"][0], "op": "explode"}))
        self.assertEqual(len(d2["created"]), 3)
        h = self.box()
        d3 = ok(c.call("geometry_edit", {"target": h, "op": "explode"}))
        self.assertEqual(len(d3["created"]), 6)

    def test_offset_curve(self):
        h = self.one({"kind": "rectangle", "corner": [0, 0, 0], "width": 10, "height": 10})["handle"]
        out = ok(c.call("geometry_edit", {"target": h, "op": "offset_curve", "distance": 1, "side": "outside"}))
        self.assertAlmostEqual(out["items"][0]["length"], 48.0, places=3)
        inn = ok(c.call("geometry_edit", {"target": h, "op": "offset_curve", "distance": 1, "side": "inside"}))
        self.assertAlmostEqual(inn["items"][0]["length"], 32.0, places=3)

    def test_fillet_shell_cap(self):
        h = self.box()
        d = ok(c.call("geometry_edit", {"target": h, "op": "fillet_edges", "radius": 1, "edges": "all"}))
        self.assertEqual(d["modified"], [h])
        r, a = 1.0, 10.0
        s = a - 2 * r
        expect = s ** 3 + 6 * s * s * r + 12 * s * math.pi * r * r / 4 + 4.0 / 3 * math.pi * r ** 3
        self.assertAlmostEqual(self.validate(h)["volume"], expect, delta=0.5)
        h2 = self.box((50, 0, 0))
        d = ok(c.call("geometry_edit", {"target": h2, "op": "shell", "thickness": 1, "remove_faces": [0]}))
        # open shell (one face removed): not a solid, bbox must stay the original cube
        self.assertEqual(d["bbox"]["max"][0], 60)
        self.assertEqual(d["bbox"]["min"][0], 50)
        r = c.call("geometry_edit", {"target": h2, "op": "shell", "thickness": 1, "remove_faces": []})
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")
        # failing fillet reports GEOMETRY_FAILED
        h3 = self.box((100, 0, 0), (2, 2, 2))
        r = c.call("geometry_edit", {"target": h3, "op": "fillet_edges", "radius": 5})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "GEOMETRY_FAILED")
        # cap: open extrusion without caps
        e = self.one({"kind": "extrusion", "points": [[0, 0, 0], [4, 0, 0], [4, 4, 0], [0, 4, 0]], "vector": [0, 0, 4], "cap": False})
        self.assertFalse(e["solid"])
        d = ok(c.call("geometry_edit", {"target": e["handle"], "op": "cap"}))
        self.assertTrue(d["items"][0]["solid"])
        self.assertAlmostEqual(d["items"][0]["volume"], 64.0, delta=0.01)

    def test_split_rebuild_offset_surface(self):
        line = self.one({"kind": "line", "from": [0, 0, 0], "to": [10, 0, 0]})["handle"]
        cut = self.one({"kind": "line", "from": [5, -1, 0], "to": [5, 1, 0]})["handle"]
        d = ok(c.call("geometry_edit", {"target": line, "op": "split", "cutter": cut, "keep_inputs": True}))
        self.assertEqual(len(d["created"]), 2)
        crv = self.one({"kind": "curve", "points": [[0, 0, 0], [3, 2, 0], [6, -2, 0], [9, 0, 0], [12, 3, 0]]})["handle"]
        d = ok(c.call("geometry_edit", {"target": crv, "op": "rebuild_curve", "points": 8, "degree": 3}))
        self.assertLess(d["items"][0]["max_deviation"], 1.0)
        self.assertEqual(self.validate(crv)["control_points"], 8)
        srf = self.one({"kind": "plane_surface", "corner": [0, 50, 0], "width": 4, "height": 4})["handle"]
        d = ok(c.call("geometry_edit", {"target": srf, "op": "offset_surface", "distance": 2, "solid": True}))
        self.assertAlmostEqual(d["items"][0]["volume"], 32.0, delta=0.05)

    # ---- validate ----
    def test_validate_reports_problems(self):
        solid = self.box()
        srf = self.one({"kind": "plane_surface", "corner": [0, 0, 0], "width": 4, "height": 4})["handle"]
        d = ok(c.call("geometry_validate", {"target": [solid, srf]}))
        s = d["summary"]
        self.assertEqual(s["checked"], 2)
        self.assertEqual(s["invalid"], 0)
        self.assertEqual(s["with_naked_edges"], 1)
        self.assertEqual(d["objects"][0]["naked_edges"], 0)
        self.assertTrue(d["objects"][0]["solid"])
        self.assertEqual(d["objects"][1]["naked_edges"], 4)
        d = ok(c.call("geometry_validate", {"target": [solid, srf], "only_problems": True}))
        self.assertEqual(d["returned"], 1)
        crv = self.one({"kind": "curve", "points": [[0, 0, 0], [5, 5, 0], [10, 0, 0], [15, 5, 0]]})["handle"]
        o = self.validate(crv)
        self.assertEqual(o["degree"], 3)
        self.assertEqual(o["control_points"], 6)


if __name__ == "__main__":
    unittest.main()

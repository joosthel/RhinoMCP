"""Designer primitives: periodic/tangent curves, line_blend, fillet_corners, text as curves, engrave/emboss,
explicit extrusion direction, split/trim keep, project/pull curve."""
import math
import unittest

import mcp_client as c

LAYER = "_mcp_test::curves"

PY_HEADER = """
import System, math
import Rhino.Geometry as g
def geo(i):
    return doc.Objects.FindId(System.Guid(i)).Geometry
"""


def ok(r):
    assert r["ok"], r
    return r["data"]


def ang(a, b):
    na = math.sqrt(sum(x * x for x in a)); nb = math.sqrt(sum(x * x for x in b))
    d = sum(x * y for x, y in zip(a, b)) / (na * nb)
    return math.degrees(math.acos(max(-1.0, min(1.0, d))))


class DesignerCurveTests(c.IdleTestCase):
    def tearDown(self):
        c.call("run_command", {"command": '-_SelLayer "%s" _Enter' % LAYER})
        c.call("run_command", {"command": "_Delete"})

    # ---- helpers ----
    def make(self, *items):
        d = ok(c.call("geometry_create", {"items": list(items), "layer": LAYER, "include_guids": True}))
        return d["items"]

    def one(self, item):
        r = self.make(item)[0]
        self.assertTrue(r["ok"], r)
        return r

    def py(self, code, args=None):
        r = c.call("run_python", {"code": PY_HEADER + code, "args": args or {}})
        self.assertTrue(r["ok"], r)
        return r["data"]["result"]

    def edit(self, **kw):
        return c.call("geometry_edit", kw)

    # ---- curves ----
    def test_closed_control_point_curve_is_periodic(self):
        r = self.one({"kind": "curve", "mode": "control_points", "closed": True, "degree": 3,
                      "points": [[0, 0, 0], [10, 0, 0], [10, 10, 0], [0, 10, 0], [-5, 5, 0]]})
        self.assertTrue(r["closed"])
        self.assertTrue(r["periodic"])
        self.assertEqual(r["degree"], 3)
        self.assertEqual(r["kinks"], 0)
        res = self.py("result = [geo(args['i']).IsClosed, geo(args['i']).IsPeriodic]", {"i": r["guids"][0]})
        self.assertEqual(res, [True, True])

    def test_interpolated_tangents_are_honoured(self):
        st, et = [0, 1, 0], [1, -1, 0]
        r = self.one({"kind": "curve", "points": [[0, 0, 0], [5, 8, 0], [12, 10, 0], [20, 4, 0]],
                      "start_tangent": st, "end_tangent": et})
        res = self.py("""
cv = geo(args['i'])
ts = cv.TangentAtStart; te = cv.TangentAtEnd
result = [[ts.X, ts.Y, ts.Z], [te.X, te.Y, te.Z]]
""", {"i": r["guids"][0]})
        self.assertLess(ang(res[0], st), 0.5)
        self.assertLess(ang(res[1], et), 0.5)
        # only one end given: the other is left free but the call must still succeed
        r2 = self.one({"kind": "curve", "points": [[0, 0, 0], [5, 8, 0], [12, 10, 0], [20, 4, 0]], "start_tangent": [1, 0, 0]})
        self.assertFalse(r2["closed"])

    def test_line_blend_has_straight_span_and_g1(self):
        for cont in ("G1", "G2"):
            r = self.one({"kind": "line_blend", "from": [0, 0, 0], "to": [10, 0, 0], "continuity": cont,
                          "points": [[20, 6, 0], [30, 6, 0], [40, 12, 0]]})
            self.assertEqual(r["segments"], 3)
            self.assertLess(max(r["g_gaps_deg"]), 0.5, r)
            self.assertAlmostEqual(r["line_length"], 10.0, places=4)
            res = self.py("""
pc = geo(args['i'])
s0 = pc.SegmentCurve(0)
gaps = []
for k in range(pc.SegmentCount - 1):
    a = pc.SegmentCurve(k).TangentAtEnd; b = pc.SegmentCurve(k + 1).TangentAtStart
    gaps.append(math.degrees(g.Vector3d.VectorAngle(a, b)))
result = [s0.IsLinear(), s0.GetLength(), gaps, pc.IsClosed]
""", {"i": r["guids"][0]})
            self.assertTrue(res[0], "first span must be exactly linear")
            self.assertAlmostEqual(res[1], 10.0, places=4)
            self.assertLess(max(res[2]), 0.5, cont)

    def test_fillet_corners_radius(self):
        rect = self.one({"kind": "polyline", "closed": True, "points": [[0, 0, 0], [20, 0, 0], [20, 10, 0], [0, 10, 0]]})
        self.assertEqual(rect["kinks"], 4)
        d = ok(self.edit(target=rect["handle"], op="fillet_corners", radius=2))
        it = d["items"][0]
        self.assertEqual(it["corners_filleted"], 4)
        self.assertAlmostEqual(it["arc_radius_min"], 2.0, places=3)
        self.assertAlmostEqual(it["arc_radius_max"], 2.0, places=3)
        self.assertEqual(it["kinks_after"], 0)
        self.assertTrue(it["closed"])
        self.assertAlmostEqual(it["length_after"], 2 * (20 + 10) - 8 * 2 + 2 * math.pi * 2, places=2)

    # ---- text ----
    def text_curves(self, text="OAR", h=12):
        r = self.one({"kind": "text", "text": text, "as": "curves", "height": h, "at": [0, 0, 0], "center": True})
        return r

    def test_text_as_curves(self):
        r = self.text_curves()
        self.assertGreaterEqual(r["curves"], 3)
        self.assertEqual(len(r["glyphs"]), 3)
        self.assertTrue(all(len(gl["holes"]) >= 1 for gl in r["glyphs"]), r["glyphs"])  # O, A and R all have counters
        self.assertGreater(r["area_total"], 0)
        res = self.py("result = [geo(i).IsClosed for i in args['gs']]", {"gs": r["guids"]})
        self.assertTrue(all(res))
        self.assertEqual(len(res), r["curves"])

    def test_engrave_removes_letter_volume_and_keeps_counters(self):
        box = self.one({"kind": "box", "corner": [-40, -20, 0], "size": [80, 40, 20]})
        v0 = box["volume"]
        txt = self.text_curves("OAR", 14)
        area = txt["area_total"]
        # remove the reference curves, engrave straight from text
        depth = 2.0
        d = ok(self.edit(target=box["handle"], op="engrave", text="OAR", text_height=14, depth=depth))
        it = d["items"][0]
        self.assertEqual(it["letters"], 3)
        self.assertGreaterEqual(it["counters"], 3)
        expected = area * depth
        self.assertAlmostEqual(it["removed_volume"], expected, delta=expected * 0.10)
        self.assertAlmostEqual(it["volume_before"] - it["volume_after"], it["removed_volume"], places=3)
        self.assertAlmostEqual(it["volume_before"], v0, places=3)
        self.assertTrue(it["solid"])
        self.assertGreater(it["outward_normal"][2], 0.99)  # engraved into the top face
        # the island inside the O counter is kept: removing the filled disc area would be clearly larger
        self.assertLess(it["removed_volume"], expected * 1.1)

    def test_engrave_curves_with_counter_island(self):
        box = self.one({"kind": "box", "corner": [-30, -30, 0], "size": [60, 60, 10]})
        ring_o = self.one({"kind": "circle", "center": [0, 0, 10], "radius": 10})
        ring_i = self.one({"kind": "circle", "center": [0, 0, 10], "radius": 5})
        d = ok(self.edit(target=box["handle"], op="engrave", curves=[ring_o["handle"], ring_i["handle"]], depth=3))
        it = d["items"][0]
        self.assertEqual(it["letters"], 1)
        self.assertEqual(it["counters"], 1)
        self.assertAlmostEqual(it["removed_volume"], math.pi * (100 - 25) * 3, delta=math.pi * 75 * 3 * 0.02)
        self.assertAlmostEqual(it["volume_after"], 60 * 60 * 10 - it["removed_volume"], places=2)

    def test_emboss_adds_volume_on_side_face(self):
        box = self.one({"kind": "box", "corner": [0, 0, 0], "size": [80, 40, 40]})
        # the face on +Y (front is -Y); pick it via a point
        d = ok(self.edit(target=box["handle"], op="emboss", text="HI", text_height=10, depth=1.5, at=[40, 40, 20]))
        it = d["items"][0]
        self.assertGreater(it["outward_normal"][1], 0.99)
        self.assertAlmostEqual(it["added_volume"], it["expected_volume"], delta=it["expected_volume"] * 0.10)
        self.assertGreater(it["volume_after"], it["volume_before"])

    # ---- extrusion ----
    def test_extrusion_direction_ignores_curve_orientation(self):
        ccw = [[0, 0, 0], [10, 0, 0], [10, 10, 0], [0, 10, 0]]
        cw = list(reversed(ccw))
        for pts in (ccw, cw):
            r = self.one({"kind": "extrusion", "points": pts, "height": 5})
            self.assertEqual(r["bbox"]["min"][2], 0)
            self.assertEqual(r["bbox"]["max"][2], 5)
            self.assertEqual(r["direction"], [0, 0, 1])
            # same through a curve handle
            pr = self.one({"kind": "polyline", "closed": True, "points": pts})
            r2 = self.one({"kind": "extrusion", "profile": pr["handle"], "height": 5})
            self.assertEqual((r2["bbox"]["min"][2], r2["bbox"]["max"][2]), (0, 5), pts)
            self.assertEqual(r2["direction"], [0, 0, 1])
            self.assertAlmostEqual(r2["volume"], 500.0, places=3)

    def test_extrusion_direction_both_sides_and_offset(self):
        pr = self.one({"kind": "polyline", "closed": True, "points": [[0, 0, 0], [10, 0, 0], [10, 10, 0], [0, 10, 0]]})
        r = self.one({"kind": "extrusion", "profile": pr["handle"], "height": 5, "direction": [0, 0, -1]})
        self.assertEqual((r["bbox"]["min"][2], r["bbox"]["max"][2]), (-5, 0))
        self.assertEqual(r["direction"], [0, 0, -1])
        r = self.one({"kind": "extrusion", "profile": pr["handle"], "height": 5, "both_sides": True})
        self.assertEqual((r["bbox"]["min"][2], r["bbox"]["max"][2]), (-5, 5))
        r = self.one({"kind": "extrusion", "profile": pr["handle"], "height": 5, "start_offset": 2})
        self.assertEqual((r["bbox"]["min"][2], r["bbox"]["max"][2]), (2, 7))
        # slanted direction: height is measured along the (unit) direction, tight bbox shifts in x
        r = self.one({"kind": "extrusion", "profile": pr["handle"], "height": 10, "direction": [1, 0, 1]})
        self.assertAlmostEqual(r["bbox"]["max"][2], 10 / math.sqrt(2), places=3)
        self.assertAlmostEqual(r["bbox"]["max"][0], 10 + 10 / math.sqrt(2), places=3)

    # ---- split / trim keep ----
    def test_split_keep_containing_point(self):
        srf = self.one({"kind": "plane_surface", "corner": [0, 0, 0], "width": 20, "height": 10})
        cut = self.one({"kind": "plane_surface", "corner": [-5, -5, 0], "width": 20, "height": 10,
                        "plane": {"origin": [10, 0, 0], "x_axis": [0, 1, 0], "y_axis": [0, 0, 1]}})
        d = ok(self.edit(target=srf["handle"], op="split", cutter=cut["handle"], keep="containing_point", point=[3, 5, 0], keep_inputs=True))
        it = d["items"][0]
        self.assertEqual(it["pieces"], 1)
        self.assertEqual(it["discarded"], 1)
        self.assertAlmostEqual(d["bbox"]["max"][0], 10, places=3)
        self.assertAlmostEqual(d["bbox"]["min"][0], 0, places=3)

    def test_split_curve_keep_inside_and_largest(self):
        line = self.one({"kind": "line", "from": [0, 0, 0], "to": [30, 0, 0]})
        box = self.one({"kind": "box", "corner": [10, -5, -5], "size": [10, 10, 10]})
        d = ok(self.edit(target=line["handle"], op="split", cutter=box["handle"], keep="inside", keep_inputs=True))
        self.assertEqual(d["items"][0]["pieces"], 1)
        self.assertAlmostEqual(d["bbox"]["min"][0], 10, places=3)
        self.assertAlmostEqual(d["bbox"]["max"][0], 20, places=3)
        line2 = self.one({"kind": "line", "from": [0, 0, 0], "to": [30, 0, 0]})
        d = ok(self.edit(target=line2["handle"], op="split", cutter=box["handle"], keep="largest", keep_inputs=True))
        self.assertEqual(d["items"][0]["pieces"], 1)
        self.assertAlmostEqual(d["bbox"]["max"][0] - d["bbox"]["min"][0], 10, places=3)

    # ---- project / pull ----
    def test_project_and_pull_land_on_surface(self):
        sph = self.one({"kind": "sphere", "center": [0, 0, 0], "radius": 10})
        cir = self.one({"kind": "circle", "center": [0, 0, 20], "radius": 3})
        d = ok(self.edit(target=cir["handle"], op="project_curve", onto=sph["handle"], direction=[0, 0, -1]))
        it = d["items"][0]
        self.assertLess(it["max_deviation"], 0.001)
        gs = d["created"]
        self.assertGreaterEqual(len(gs), 1)
        # z of the projected curve = sqrt(100 - 9) on the sphere
        self.assertAlmostEqual(d["bbox"]["max"][2], math.sqrt(91), delta=0.01)
        # pull
        d2 = ok(self.edit(target=cir["handle"], op="pull_curve", onto=sph["handle"]))
        it2 = d2["items"][0]
        self.assertLess(it2["max_deviation"], 0.001)
        self.assertGreaterEqual(it2["pieces"], 1)

    # ---- fillet budget ----
    def test_fillet_edges_reports_budget_and_steps(self):
        box = self.one({"kind": "box", "corner": [0, 0, 0], "size": [20, 20, 20]})
        d = ok(self.edit(target=box["handle"], op="fillet_edges", radius=2, budget_ms=20000))
        it = d["items"][0]
        self.assertTrue(it["solid"])
        self.assertTrue(any("ok" in t for t in it["tried"]))
        # a failing fillet with a 1 ms budget: the retry ladder is cut short with a structured TIMEOUT
        small = self.one({"kind": "box", "corner": [100, 0, 0], "size": [2, 2, 2]})
        bad = c.call("geometry_edit", {"target": small["handle"], "op": "fillet_edges", "radius": 5, "budget_ms": 1})
        self.assertFalse(bad["ok"])
        self.assertIn(bad["error"]["code"], ("TIMEOUT", "GEOMETRY_FAILED"))
        if bad["error"]["code"] == "TIMEOUT":
            self.assertEqual(bad["error"]["details"]["budget_ms"], 1)
            self.assertTrue(bad["error"]["details"]["tried"])
        # the next call must not inherit the timeout
        ok2 = c.call("geometry_edit", {"target": small["handle"], "op": "fillet_edges", "radius": 5})
        self.assertEqual(ok2["error"]["code"], "GEOMETRY_FAILED")


if __name__ == "__main__":
    unittest.main()

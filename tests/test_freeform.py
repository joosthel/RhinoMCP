import math
import unittest

import mcp_client as c

LAYER = "_mcp_test::freeform"
CLASS_RANK = {"none": -1, "G0": 0, "G1": 1, "G2": 2}


def ok(r):
    assert r["ok"], r
    return r["data"]


def grid(nx, ny, f, x0=0.0, x1=10.0, y0=0.0, y1=10.0):
    """rows (u) along x, cols (v) along y: points [x, y, f(x, y)]."""
    xs = [x0 + (x1 - x0) * i / (nx - 1.0) for i in range(nx)]
    ys = [y0 + (y1 - y0) * j / (ny - 1.0) for j in range(ny)]
    return [[[x, y, f(x, y)] for y in ys] for x in xs]


def circle(cx, cy, z, r):
    return {"circle": {"center": [cx, cy, z], "radius": r}}


class FreeformTests(unittest.TestCase):
    def tearDown(self):
        c.call("run_command", {"command": '-_SelLayer "%s" _Enter' % LAYER})
        c.call("run_command", {"command": "_Delete"})

    # ---- helpers ----
    def ff(self, *items):
        d = ok(c.call("freeform_surface", {"items": list(items), "layer": LAYER}))
        return d["items"]

    def one(self, item):
        r = self.ff(item)[0]
        self.assertTrue(r["ok"], r)
        return r

    def valid(self, h):
        d = ok(c.call("geometry_validate", {"target": h}))
        self.assertTrue(d["objects"][0]["valid"], d)
        return d["objects"][0]

    def cont(self, a, b, **kw):
        args = {"a": a, "b": b}
        args.update(kw)
        return ok(c.call("analyze_continuity", args))

    def edge_where(self, h, pred):
        d = ok(c.call("analyze_surface", {"target": h}))
        for e in d["edge_list"]:
            if pred(e):
                return e["i"]
        self.fail("no edge matched in %s" % d["edge_list"])

    def flat(self, x0, x1, y0=0, y1=10, z=0.0):
        return self.one({"kind": "nurbs_points", "points": grid(4, 4, lambda x, y: z, x0, x1, y0, y1)})["handle"]

    # ---- surface constructors ----
    def test_nurbs_points_counts(self):
        r = self.one({"kind": "nurbs_points", "points": grid(5, 6, lambda x, y: math.sin(x / 3.0) * 2),
                      "degree_u": 3, "degree_v": 2})
        self.assertEqual((r["degree_u"], r["degree_v"]), (3, 2))
        self.assertEqual((r["cp_u"], r["cp_v"]), (5, 6))
        self.assertFalse(r["closed_u"])
        self.assertEqual(r["faces"], 1)
        self.assertGreater(r["area"], 90)

    def test_nurbs_points_degree_clamped_and_weights(self):
        r = self.one({"kind": "nurbs_points", "points": grid(3, 3, lambda x, y: 0), "degree_u": 3, "degree_v": 3,
                      "weights": [[1, 1, 1], [1, 2, 1], [1, 1, 1]]})
        self.assertEqual(r["degree_u"], 2)
        self.assertTrue(r["rational"])

    def test_nurbs_points_closed(self):
        pts = []
        for i in range(6):  # ring of 6 points x 3 rows
            a = 2 * math.pi * i / 6
            pts.append([[5 * math.cos(a), 5 * math.sin(a), z] for z in (0, 4, 8)])
        r = self.one({"kind": "nurbs_points", "points": pts, "degree_u": 3, "degree_v": 2, "closed_u": True})
        self.assertTrue(r["closed_u"])
        self.assertEqual(r["cp_u"], 9)  # 6 distinct + degree wrap
        self.assertEqual(r["naked_edges"], 2)

    def test_through_points_interpolates(self):
        r = self.one({"kind": "through_points", "points": grid(5, 5, lambda x, y: 3 * math.sin(x / 2.0) * math.cos(y / 3.0))})
        self.assertLess(r["max_point_deviation"], 0.001)
        self.valid(r["handle"])

    def test_loft_three_circles_open_and_closed(self):
        cs = [circle(0, 0, 0, 5), circle(0, 0, 5, 8), circle(0, 0, 10, 4)]
        r = self.one({"kind": "loft", "curves": cs})
        self.assertEqual(r["faces"], 1)
        self.assertFalse(r["solid"])
        self.assertEqual(r["naked_edges"], 2)
        self.assertTrue(r["closed_u"] or r["closed_v"])
        self.valid(r["handle"])
        cl = self.one({"kind": "loft", "curves": cs, "closed": True})
        self.assertTrue(cl["closed_u"] and cl["closed_v"], cl)
        self.assertEqual(cl["naked_edges"], 0)

    def test_loft_cap_gives_solid(self):
        cs = [circle(0, 0, 0, 5), circle(0, 0, 5, 8), circle(0, 0, 10, 4)]
        r = self.one({"kind": "loft", "curves": cs, "cap": True, "type": "tight"})
        self.assertTrue(r["solid"])
        self.assertGreater(r["volume"], 500)

    def test_loft_inline_curves_and_options(self):
        cs = [[[0, 0, 0], [5, 2, 0], [10, 0, 0]], [[0, 0, 5], [5, 4, 5], [10, 0, 5]], [[0, 0, 10], [5, 1, 10], [10, 0, 10]]]
        a = self.one({"kind": "loft", "curves": cs, "rebuild": 6})
        self.assertEqual(a["cp_u"] * a["cp_v"] > 0, True)
        b = self.one({"kind": "loft", "curves": cs, "refit": 0.05, "type": "loose"})
        self.assertTrue(b["ok"])

    def test_network_valid(self):
        f = lambda x, y: 0.05 * (x - 5) ** 2 - 0.03 * (y - 5) ** 2 + 2
        xs = [0, 2.5, 5, 7.5, 10]
        u_curves = [[[x, y0, f(x, y0)] for x in xs] for y0 in (0, 5, 10)]
        v_curves = [[[x0, y, f(x0, y)] for y in xs] for x0 in (0, 5, 10)]
        r = self.one({"kind": "network", "u_curves": u_curves, "v_curves": v_curves, "interior_tol": 0.05, "edge_tol": 0.05})
        self.assertEqual(r["faces"], 1)
        self.assertGreater(r["area"], 95)
        self.valid(r["handle"])

    def test_network_failure_teaches(self):
        r = c.call("freeform_surface", {"items": [{"kind": "network", "curves": [
            [[0, 0, 0], [1, 0, 0]], [[0, 5, 0], [1, 5, 0]], [[0, 0, 3], [1, 0, 3]], [[0, 5, 3], [1, 5, 3]]]}], "layer": LAYER})
        self.assertFalse(r["ok"])
        self.assertIn("interior_tol", r["error"]["details"]["items"][0]["error"])

    def test_patch_valid(self):
        boundary = {"points": [[0, 0, 0], [10, 0, 0], [10, 10, 0], [0, 10, 0]], "mode": "polyline", "closed": True}
        r = self.one({"kind": "patch", "inputs": [boundary, [5, 5, 3]], "u_spans": 6, "v_spans": 6})
        self.assertEqual(r["faces"], 1)
        self.assertGreater(r["bbox"]["max"][2], 1.0)
        self.valid(r["handle"])

    def test_edge_surface_planar(self):
        ls = [{"points": p, "mode": "polyline"} for p in
              ([[0, 0, 0], [10, 0, 0]], [[10, 0, 0], [10, 10, 0]], [[10, 10, 0], [0, 10, 0]], [[0, 10, 0], [0, 0, 0]])]
        r = self.one({"kind": "edge_surface", "curves": ls})
        self.assertAlmostEqual(r["area"], 100.0, places=2)
        self.assertAlmostEqual(r["bbox"]["max"][2], 0.0, places=4)
        self.assertTrue(r["planar"])

    def test_sweep2_valid(self):
        r = self.one({"kind": "sweep2",
                      "rails": [[[0, 0, 0], [0, 3, 5], [0, 0, 10]], [[10, 0, 0], [10, 3, 5], [10, 0, 10]]],
                      "profiles": [[[0, 0, 0], [5, 0, 2], [10, 0, 0]], [[0, 0, 10], [5, 0, 5], [10, 0, 10]]]})
        self.assertEqual(r["faces"], 1)
        self.assertGreater(r["area"], 70)
        self.valid(r["handle"])

    def test_rebuild_surface(self):
        t = self.one({"kind": "through_points", "points": grid(6, 6, lambda x, y: math.sin(x / 3.0) * 2)})
        r = self.one({"kind": "rebuild_surface", "target": t["handle"], "points_u": 5, "points_v": 7, "degree_u": 3, "degree_v": 3})
        self.assertEqual((r["cp_u"], r["cp_v"]), (5, 7))
        self.assertAlmostEqual(r["area"], t["area"], delta=t["area"] * 0.03)

    def test_batch_partial_failure_and_errors(self):
        items = self.ff({"kind": "nurbs_points", "points": grid(3, 3, lambda x, y: 0)},
                        {"kind": "loft", "curves": ["o99999", "o99998"]},
                        {"kind": "nope"},
                        {"kind": "edge_surface", "curves": [[[0, 0, 0], [1, 1, 0]]]})
        self.assertTrue(items[0]["ok"])
        for i in (1, 2, 3):
            self.assertFalse(items[i]["ok"], items[i])
        r = c.call("freeform_surface", {"items": [{"kind": "nope"}], "layer": LAYER})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "GEOMETRY_FAILED")

    # ---- continuity ----
    def test_coplanar_adjoining_is_g1(self):
        a = self.flat(0, 10)
        b = self.flat(10, 20)
        d = self.cont(a, b)
        self.assertGreaterEqual(CLASS_RANK[d["class"]], 1, d)
        self.assertLess(d["max_gap"], 1e-3)
        self.assertLess(d["max_angle_deg"], 0.01)

    def test_sharp_crease_is_g0_only(self):
        a = self.flat(0, 10)
        b = self.one({"kind": "nurbs_points", "points": [[[10, y, z] for y in (0, 3.33, 6.67, 10)] for z in (0, 3.33, 6.67, 10)]})["handle"]
        d = self.cont(a, b)
        self.assertEqual(d["class"], "G0", d)
        self.assertAlmostEqual(d["max_angle_deg"], 90.0, delta=0.5)

    def _parabola_pair(self, k_left, k_right):
        """z = k*y^2 on y in [-20,0] (left) and [0,20] (right); exact for a cubic through_points grid."""
        def make(y0, y1, k):
            return self.one({"kind": "through_points",
                             "points": grid(4, 4, lambda x, y: k * y * y, 0, 30, y0, y1)})["handle"]
        return make(-20, 0, k_left), make(0, 20, k_right)

    def test_analytic_g2_parabola(self):
        a, b = self._parabola_pair(-0.01, -0.01)   # same parabola on both sides of y=0
        d = self.cont(a, b, samples=15)
        self.assertEqual(d["class"], "G2", d)
        self.assertLess(d["max_curv_diff_pct"], 1.0)

    def test_analytic_curvature_flip_is_g1(self):
        a, b = self._parabola_pair(-0.01, 0.01)    # slope 0 at y=0 on both sides, curvature changes sign
        d = self.cont(a, b, samples=15)
        self.assertEqual(d["class"], "G1", d)
        self.assertGreater(d["max_curv_diff_pct"], 150.0)
        self.assertLess(d["max_angle_deg"], 0.05)

    def test_network_between_edges_is_tangent(self):
        a = self.one({"kind": "nurbs_points", "points": grid(4, 4, lambda x, y: 0.05 * (x - 15) ** 2 / 10.0 + 0.0 * y, 0, 30, 0, 20)})["handle"]
        b = self.flat(0, 30, 40, 60)
        ea = self.edge_where(a, lambda e: abs(e["from"][1] - 20) < 1e-6 and abs(e["to"][1] - 20) < 1e-6)
        eb = self.edge_where(b, lambda e: abs(e["from"][1] - 40) < 1e-6 and abs(e["to"][1] - 40) < 1e-6)
        az = grid(4, 4, lambda x, y: 0.05 * (x - 15) ** 2 / 10.0, 0, 30, 0, 20)
        def net(cont):
            return self.one({"kind": "network", "u_curves": [{"target": a, "edge": ea}, {"target": b, "edge": eb}],
                             "v_curves": [[[0, 20, az[0][3][2]], [0, 40, 0]], [[30, 20, az[3][3][2]], [30, 40, 0]]],
                             "continuity": cont, "interior_tol": 0.5, "edge_tol": 0.5})["handle"]
        angles = {}
        for cont in ("position", "tangent"):
            n = net(cont)
            angles[cont] = max(self.cont(a, n)["max_angle_deg"], self.cont(n, b)["max_angle_deg"])
        # brep-edge inputs carry the adjacent surface: tangent continuity must beat plain position continuity
        self.assertLess(angles["tangent"], angles["position"] * 0.5, angles)

    def test_gap_is_none(self):
        a = self.flat(0, 10)
        b = self.flat(10.5, 20)
        d = self.cont(a, b)
        self.assertEqual(d["class"], "none")
        self.assertAlmostEqual(d["max_gap"], 0.5, places=3)

    def _blend_pair(self):
        f_a = lambda x, y: 0.03 * x * x
        a = self.one({"kind": "nurbs_points", "points": grid(5, 5, f_a, 0, 10)})["handle"]
        f_b = lambda x, y: 0.03 * (x - 10) ** 2 + 3 + 0.9 * (x - 10) * 0
        b = self.one({"kind": "nurbs_points", "points": grid(5, 5, lambda x, y: 6 + 0.03 * (x - 18) ** 2, 18, 28)})["handle"]
        ea = self.edge_where(a, lambda e: abs(e["from"][0] - 10) < 1e-6 and abs(e["to"][0] - 10) < 1e-6)
        eb = self.edge_where(b, lambda e: abs(e["from"][0] - 18) < 1e-6 and abs(e["to"][0] - 18) < 1e-6)
        return a, b, ea, eb

    def test_blend_g1(self):
        a, b, ea, eb = self._blend_pair()
        r = self.one({"kind": "blend_surface", "a": {"target": a, "edge": ea}, "b": {"target": b, "edge": eb},
                      "continuity_a": "G1", "continuity_b": "G1"})
        self.valid(r["handle"])
        d1 = self.cont(a, r["handle"])
        d2 = self.cont(b, r["handle"])
        for d in (d1, d2):
            self.assertGreaterEqual(CLASS_RANK[d["class"]], 1, d)
            self.assertLess(d["max_gap"], 0.01)

    def test_blend_g2(self):
        a, b, ea, eb = self._blend_pair()
        r = self.one({"kind": "blend_surface", "a": {"target": a, "edge": ea}, "b": {"target": b, "edge": eb},
                      "continuity_a": "G2", "continuity_b": "G2"})
        d1 = self.cont(a, r["handle"])
        d2 = self.cont(b, r["handle"])
        for d in (d1, d2):
            self.assertEqual(d["class"], "G2", d)

    def test_curve_blend_continuity(self):
        a = ok(c.call("geometry_create", {"items": [{"kind": "curve", "points": [[0, 0, 0], [3, 2, 0], [6, 2.5, 0]], "name": "ca"}], "layer": LAYER}))["items"][0]["handle"]
        b = ok(c.call("geometry_create", {"items": [{"kind": "curve", "points": [[12, 1, 0], [15, 3, 0], [18, 0, 0]], "name": "cb"}], "layer": LAYER}))["items"][0]["handle"]
        for cont, rank in (("G0", 0), ("G1", 1), ("G2", 2)):
            r = self.one({"kind": "curve_blend", "a": a, "b": b, "continuity": cont})
            da = self.cont(a, r["handle"])
            db = self.cont(r["handle"], b)
            for d in (da, db):
                self.assertGreaterEqual(CLASS_RANK[d["class"]], rank, (cont, d))

    # ---- analysis ----
    def test_analyze_surface_sphere(self):
        s = ok(c.call("geometry_create", {"items": [{"kind": "sphere", "center": [0, 0, 0], "radius": 10}], "layer": LAYER}))["items"][0]["handle"]
        d = ok(c.call("analyze_surface", {"target": s}))
        self.assertAlmostEqual(d["area"], 4 * math.pi * 100, delta=2.0)
        self.assertAlmostEqual(abs(d["gaussian"]["max"]), 0.01, delta=0.0005)
        self.assertAlmostEqual(d["max_abs_curvature"]["value"], 0.1, delta=0.005)
        self.assertAlmostEqual(d["max_abs_curvature"]["min_radius"], 10.0, delta=0.5)

    def test_analyze_surface_nurbs_info(self):
        r = self.one({"kind": "nurbs_points", "points": grid(4, 5, lambda x, y: 0.1 * x * y)})
        d = ok(c.call("analyze_surface", {"target": r["handle"], "u_samples": 8, "v_samples": 8}))
        self.assertEqual((d["degree_u"], d["degree_v"]), (3, 3))
        self.assertEqual((d["cp_u"], d["cp_v"]), (4, 5))
        self.assertEqual(d["naked_edges"], 4)
        self.assertEqual(len(d["edge_list"]), 4)
        self.assertLess(d["gaussian"]["max"], 0)  # saddle

    # ---- subd ----
    def subd(self, op, **kw):
        args = {"op": op, "layer": LAYER}
        args.update(kw)
        return ok(c.call("subd", args))

    def test_subd_box_to_brep_solid(self):
        s = self.subd("create_from_mesh", primitive={"type": "box", "corner": [0, 0, 0], "size": [10, 10, 10], "nx": 2, "ny": 2, "nz": 2})
        self.assertEqual(s["faces"], 24)
        self.assertTrue(s["is_solid"])
        b = self.subd("to_brep", target=s["handle"])
        self.assertTrue(b["solid"], b)
        self.assertGreater(b["volume"], 300)
        self.assertLess(b["volume"], 1000)  # rounded blob is smaller than the box

    def test_subd_offset_solid(self):
        s = self.subd("create_from_mesh", primitive={"type": "box", "corner": [0, 0, 0], "size": [10, 10, 10]})
        o = self.subd("offset", target=s["handle"], distance=1.0, solid=True)
        self.assertTrue(o["is_solid"])
        self.assertGreater(o["faces"], 0)

    def test_subd_subdivide_and_crease(self):
        s = self.subd("create_from_mesh", primitive={"type": "box", "corner": [0, 0, 0], "size": [10, 10, 10]})
        sub = self.subd("subdivide", target=s["handle"], levels=1)
        self.assertEqual(sub["faces"], s["faces"] * 4)
        cr = self.subd("crease_edges", target=s["handle"], all_sharp_by_angle=30)
        self.assertEqual(cr["tagged"], 12)
        info = self.subd("info", target=cr["handle"])
        self.assertEqual(info["creased_edges"], 12)
        top = self.subd("crease_edges", target=s["handle"], in_box={"min": [-1, -1, 9], "max": [11, 11, 11]})
        self.assertEqual(top["tagged"], 4)
        # a creased box is boxier: bigger volume than the smooth one
        b_smooth = self.subd("to_brep", target=s["handle"])
        b_crease = self.subd("to_brep", target=cr["handle"])
        self.assertGreater(b_crease["volume"], b_smooth["volume"] * 1.1)

    def test_subd_cylinder_loft_surface_interpolate_join(self):
        cyl = self.subd("create_from_mesh", primitive={"type": "cylinder", "base": [0, 0, 0], "radius": 5, "height": 10, "around": 8, "vertical": 3})
        self.assertTrue(cyl["is_solid"])
        lofted = self.subd("from_loft", curves=[circle(30, 0, 0, 5), circle(30, 0, 5, 7), circle(30, 0, 10, 4), circle(30, 0, 15, 5)],
                           closed=False)
        self.assertGreater(lofted["faces"], 8)
        srf = self.one({"kind": "nurbs_points", "points": grid(4, 4, lambda x, y: math.sin(x / 3.0) * 2, 60, 70)})["handle"]
        fs = self.subd("from_surface", target=srf)
        self.assertGreater(fs["faces"], 0)
        ip = self.subd("interpolate_points", points=grid(4, 4, lambda x, y: math.sin(x / 3.0) * 2, 90, 100))
        self.assertLess(ip["max_interpolation_deviation"], 0.01)
        s1 = self.subd("from_surface", target=self.one({"kind": "nurbs_points", "points": grid(4, 4, lambda x, y: 0, 100, 110)})["handle"], method="control_net")
        s2 = self.subd("from_surface", target=self.one({"kind": "nurbs_points", "points": grid(4, 4, lambda x, y: 0, 110, 120)})["handle"], method="control_net")
        j = self.subd("join", targets=[s1["handle"], s2["handle"]], tol=0.01)
        self.assertEqual(j["faces"], s1["faces"] + s2["faces"])

    def test_subd_errors_teach(self):
        box = ok(c.call("geometry_create", {"items": [{"kind": "box", "corner": [0, 0, 0], "size": [1, 1, 1]}], "layer": LAYER}))["items"][0]["handle"]
        r = c.call("subd", {"op": "subdivide", "target": box})
        self.assertFalse(r["ok"])
        self.assertIn("not a SubD", r["error"]["message"])
        r = c.call("subd", {"op": "bogus"})
        self.assertFalse(r["ok"])


if __name__ == "__main__":
    unittest.main()

import unittest

import mcp_client as c

LAYER = "_mcp_test::measure"


def run(cmd):
    r = c.call("run_command", {"command": cmd, "input_timeout_s": 5})
    assert r["ok"], r
    return r["data"]


def guid_of(handle):
    r = c.call("measure", {"target": handle, "per_object": 1, "include_guids": True})
    assert r["ok"], r
    return r["data"]["objects"][0]["guid"]


class MeasureTests(unittest.TestCase):
    created = []

    def box(self, x0, y0, z0, x1, y1, z1, h):
        """Creates a box from base corners and height, moves it to the test layer, returns its handle."""
        return self.place("-_Box %s,%s,%s %s,%s,%s %s" % (x0, y0, z0, x1, y1, z1, h))

    def place(self, cmd):
        d = run(cmd)
        self.assertEqual(len(d["added"]), 1, d)
        handle = d["added"][0]
        self.created.append(handle)
        run("_SelNone")
        run("-_SelId %s" % guid_of(handle))
        run("-_ChangeLayer %s" % LAYER)
        run("_SelNone")
        return handle

    @classmethod
    def setUpClass(cls):
        run("-_Layer _New %s _Enter" % LAYER)

    def setUp(self):
        self.created = []

    def tearDown(self):
        # delete only what this test created
        run("_SelNone")
        for h in self.created:
            try:
                run("-_SelId %s" % guid_of(h))
            except AssertionError:
                pass
        if self.created:
            run("_Delete")
        run("_SelNone")

    @classmethod
    def tearDownClass(cls):
        run("-_Layer _Delete %s _Enter" % LAYER)

    # ---------------------------------------------------------------- measure
    def test_box_metrics(self):
        h = self.box(0, 0, 0, 10, 10, 0, 5)
        r = c.call("measure", {"target": h, "group_by": "layer", "per_object": 5})
        self.assertTrue(r["ok"], r)
        d = r["data"]
        self.assertAlmostEqual(d["totals"]["volume"], 500.0, places=3)
        self.assertAlmostEqual(d["totals"]["area"], 400.0, places=3)
        for got, want in zip(d["totals"]["centroid"], [5, 5, 2.5]):
            self.assertAlmostEqual(got, want, places=3)
        self.assertEqual(d["totals"]["centroid_basis"], "volume")
        self.assertEqual(d["groups"][0]["key"], LAYER)
        self.assertEqual(d["objects"][0]["handle"], h)
        self.assertIn("volume", d["units"])
        self.assertEqual(d["units"]["area"], r["units"] + "^2")
        self.assertEqual(d["totals"]["bbox"]["max"], [10.0, 10.0, 5.0])

    def test_set_totals_and_group_by_type(self):
        a = self.box(0, 0, 0, 10, 10, 0, 5)
        b = self.box(100, 0, 0, 102, 2, 0, 3)
        r = c.call("measure", {"target": [a, b], "metrics": ["volume"], "group_by": "type"})
        self.assertTrue(r["ok"], r)
        self.assertAlmostEqual(r["data"]["totals"]["volume"], 512.0, places=3)
        self.assertNotIn("area", r["data"]["totals"])
        self.assertEqual(r["data"]["groups"][0]["count"], 2)

    def test_open_surface_skips_volume(self):
        h = self.place("-_Plane 300,0,0 310,10,0")
        r = c.call("measure", {"target": h})
        self.assertTrue(r["ok"], r)
        self.assertAlmostEqual(r["data"]["totals"]["area"], 100.0, places=3)
        self.assertEqual(r["data"]["totals"]["volume"], 0)
        self.assertEqual(r["data"]["skipped"]["open_brep_no_volume"]["count"], 1)
        self.assertTrue(r["warnings"])

    def test_curve_length(self):
        h = self.place("-_Line 400,0,0 403,4,0")
        r = c.call("measure", {"target": h})
        self.assertAlmostEqual(r["data"]["totals"]["length"], 5.0, places=3)

    def test_errors_teach(self):
        r = c.call("measure", {"target": "o99999999"})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")
        r = c.call("measure", {"target": "o1", "group_by": "colour"})
        self.assertFalse(r["ok"])

    # --------------------------------------------------------------- distance
    def test_distance_between_boxes(self):
        a = self.box(0, 0, 0, 10, 10, 0, 5)
        b = self.box(20, 0, 0, 30, 10, 0, 5)
        r = c.call("measure_distance", {"a": a, "b": b})
        self.assertTrue(r["ok"], r)
        self.assertAlmostEqual(r["data"]["distance"], 10.0, places=3)
        self.assertEqual(sorted([r["data"]["a"], r["data"]["b"]]), sorted([a, b]))
        self.assertAlmostEqual(abs(r["data"]["point_a"][0] - r["data"]["point_b"][0]), 10.0, places=3)

    def test_distance_point_to_box(self):
        a = self.box(0, 0, 0, 10, 10, 0, 5)
        r = c.call("measure_distance", {"point": [15, 5, 2], "b": a})
        self.assertTrue(r["ok"], r)
        self.assertAlmostEqual(r["data"]["distance"], 5.0, places=3)

    def test_distance_overlapping_is_zero(self):
        a = self.box(0, 0, 0, 10, 10, 0, 5)
        b = self.box(5, 5, 0, 15, 15, 0, 5)
        r = c.call("measure_distance", {"a": a, "b": b})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["distance"], 0.0)
        self.assertTrue(r["data"]["intersecting"])

    # ------------------------------------------------------------------ clash
    def test_clash_overlapping_and_separated(self):
        a = self.box(0, 0, 0, 10, 10, 0, 5)
        b = self.box(5, 5, 0, 15, 15, 0, 5)    # overlaps a by 5x5x5 = 125
        far = self.box(100, 0, 0, 110, 10, 0, 5)
        r = c.call("clash_check", {"a": [a, b, far], "compute_volume": True})
        self.assertTrue(r["ok"], r)
        d = r["data"]
        self.assertEqual(d["clash_count"], 1, d)
        self.assertEqual(sorted([d["top"][0]["a"], d["top"][0]["b"]]), sorted([a, b]))
        self.assertAlmostEqual(d["top"][0]["overlap_volume"], 125.0, places=2)
        self.assertGreater(d["top"][0]["intersection_length"], 0)
        self.assertEqual(d["matrix"][0]["layer_a"], LAYER)
        self.assertFalse(d["partial"])

    def test_no_clash_for_separated(self):
        a = self.box(0, 0, 0, 10, 10, 0, 5)
        b = self.box(20, 0, 0, 30, 10, 0, 5)
        r = c.call("clash_check", {"a": a, "b": b})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["clash_count"], 0)
        self.assertEqual(r["data"]["candidate_pairs"], 0)

    def test_clearance_and_containment_and_limits(self):
        a = self.box(0, 0, 0, 10, 10, 0, 5)
        b = self.box(12, 0, 0, 20, 10, 0, 5)       # 2 units apart
        inner = self.box(3, 3, 1, 6, 6, 1, 2)      # fully inside a
        r = c.call("clash_check", {"a": [a, b, inner], "clearance": 3})
        self.assertTrue(r["ok"], r)
        d = r["data"]
        kinds = sorted(t["kind"] for t in d["top"])
        self.assertEqual(kinds, ["clearance", "containment"], d)
        cl = [t for t in d["top"] if t["kind"] == "clearance"][0]
        self.assertAlmostEqual(cl["distance"], 2.0, places=3)
        # safety limit
        r = c.call("clash_check", {"a": [a, b, inner], "clearance": 3, "max_pairs": 1})
        self.assertTrue(r["ok"], r)
        self.assertTrue(r["data"]["partial"])
        self.assertTrue(any("max_pairs" in w for w in r["warnings"]))


if __name__ == "__main__":
    unittest.main()

import unittest

import mcp_client as c

LAYER = "_mcp_test::resources"


def ok(r):
    assert r["ok"], r
    return r["data"]


def grid(n, m, step=1.0):
    return [[[i * step, j * step, 0.3 * ((i + j) % 3)] for j in range(m)] for i in range(n)]


def count_layer():
    r = c.call("run_python", {"code": (
        "import scriptcontext as sc\n"
        "doc = sc.doc\n"
        "result = len([o for o in doc.Objects if doc.Layers[o.Attributes.LayerIndex].FullPath.startswith('%s')])\n" % LAYER)})
    return ok(r)["result"]


class ResourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        names = [t["name"] for t in c.rpc("tools/list")["tools"]]
        for n in ("system_status", "performance_mode", "freeform_surface", "subd", "view_capture"):
            assert n in names, n

    def tearDown(self):
        c.call("run_python", {"code": (
            "import Rhino, scriptcontext as sc\n"
            "doc = sc.doc\n"
            "ids = [o.Id for o in doc.Objects if doc.Layers[o.Attributes.LayerIndex].FullPath.startswith('%s')]\n"
            "for i in ids: doc.Objects.Delete(i, True)\n"
            "result = len(ids)\n" % LAYER)})
        c.call("performance_mode", {"action": "off"})

    def test_system_status(self):
        d = ok(c.call("system_status", {"refresh": True}))
        self.assertGreater(d["process"]["rss_mb"], 0)
        self.assertIn("rosetta", d["process"])
        self.assertIsInstance(d["process"]["rosetta"], bool)
        self.assertGreater(d["system"]["cores_logical"], 0)
        self.assertEqual(d["limits"]["max_surface_control_points"] >= 100, True)
        self.assertIn("objects", d["document"])
        self.assertIsInstance(d["recommendations"], list)

    def test_dense_grid_is_refused(self):
        before = count_layer()
        r = c.call("freeform_surface", {"layer": LAYER, "items": [{"kind": "nurbs_points", "points": grid(80, 80)}]})
        self.assertFalse(r["ok"], r)
        self.assertEqual(r["error"]["code"], "RESOURCE_LIMIT")
        self.assertIn("rebuild_surface", r["error"]["guidance"])
        self.assertEqual(r["error"]["details"]["estimated"], 6400)
        self.assertEqual(count_layer(), before)

    def test_small_grid_ok(self):
        r = c.call("freeform_surface", {"layer": LAYER, "items": [{"kind": "nurbs_points", "points": grid(10, 10)}]})
        d = ok(r)
        self.assertEqual(d["created"], 1)

    def test_subd_levels_limit(self):
        box = ok(c.call("subd", {"op": "create_from_mesh", "layer": LAYER,
                                  "primitive": {"type": "box", "corner": [0, 0, 0], "size": [10, 10, 10]}}))
        r = c.call("subd", {"op": "subdivide", "target": box["handle"], "levels": 4})
        self.assertFalse(r["ok"], r)
        self.assertEqual(r["error"]["code"], "RESOURCE_LIMIT")
        # within the limit it still works
        d = ok(c.call("subd", {"op": "subdivide", "target": box["handle"], "levels": 1, "layer": LAYER}))
        self.assertGreater(d["faces"], 6)

    def test_big_capture_is_refused(self):
        r = c.call("view_capture", {"width": 4000, "height": 3000})
        self.assertFalse(r["ok"], r)
        self.assertEqual(r["error"]["code"], "RESOURCE_LIMIT")
        self.assertIn("max_capture_pixels", r["error"]["details"]["limit"])
        self.assertNotIn("_images", r)

    def test_performance_mode_round_trip(self):
        before = ok(c.call("performance_mode", {"action": "status"}))["meshing_style"]
        on = ok(c.call("performance_mode", {"action": "on"}))
        self.assertTrue(on["on"])
        st = ok(c.call("performance_mode", {"action": "status"}))
        self.assertTrue(st["on"])
        self.assertEqual(st["meshing_style"], "Fast")
        off = ok(c.call("performance_mode", {"action": "off"}))
        self.assertFalse(off["on"])
        after = ok(c.call("performance_mode", {"action": "status"}))
        self.assertFalse(after["on"])
        self.assertEqual(after["meshing_style"], before)


if __name__ == "__main__":
    unittest.main()

import unittest

import mcp_client as c

SRC = "_mcp_test::drawing"
OUT = "_mcp_test::drawing_out"
SET_NAMES = ["Top", "Front", "Right", "Back", "Left", "Bottom", "Plane", "Dir", "Perspective"]


def ok(r):
    assert r["ok"], r
    return r["data"]


def wipe(root):
    c.call("objects_delete", {"target": {"query": {"layer": root, "include_sublayers": True}}, "confirm_count": 100000})
    c.call("layers_manage", {"ops": [{"op": "delete", "path": root, "recursive": True, "confirm_objects": 0}]})


def box(x0, y0, z0, x1, y1, z1):
    return {"kind": "box", "min": [x0, y0, z0], "max": [x1, y1, z1]}


class DrawingTests(c.IdleTestCase):
    def setUp(self):
        self.clips = []
        self.cleanup()

    def tearDown(self):
        for h in self.clips:
            c.call("drawing_clipping_plane", {"action": "delete", "target": h})
        self.cleanup()

    def cleanup(self):
        for r in (SRC, OUT):
            wipe(r)
        for n in SET_NAMES:
            c.call("sets_delete", {"name": "make2d_" + n})

    # ---- helpers
    def mk(self, *items):
        d = ok(c.call("geometry_create", {"items": list(items), "layer": SRC}))
        return [i["handle"] for i in d["items"]]

    def m2d(self, target, **kw):
        args = {"target": target, "layer_root": OUT + "::t"}
        args.update(kw)
        r = c.call("drawing_make2d", args)
        assert r["ok"], r
        return r["data"], r

    def objs(self, sub="", **kw):
        f = {"layer": OUT + "::t" + sub, "include_sublayers": True}
        f.update(kw)
        d = ok(c.call("objects_query", {"filters": f, "limit": 500, "fields": ["handle", "type", "layer", "bbox"]}))
        return d["rows"]

    def get(self, handles):
        return ok(c.call("objects_get", {"target": handles}))["objects"]

    def clip_z(self, z, **kw):
        args = {"action": "create", "z": z}
        args.update(kw)
        d = ok(c.call("drawing_clipping_plane", args))
        self.clips.append(d["handle"])
        return d

    # ---- tests
    def test_box_cylinder_top_is_clean(self):
        hs = self.mk(box(0, 0, 0, 4000, 3000, 2500),
                     {"kind": "cylinder", "base": [7000, 1500, 0], "radius": 800, "height": 3000})
        d, _ = self.m2d(hs, view="Top")
        rep = d["report"]
        self.assertGreater(rep["segments_in"], rep["segments_out"])
        self.assertGreater(rep["dropped_duplicate"], 0)          # Rhino flags the coincident bottom/top edges
        self.assertGreater(rep["dropped_projecting"], 0)          # vertical edges project to points
        co = rep["curves_out"]
        self.assertEqual(co["nurbs"], 0)
        self.assertEqual(co["total"], co["line"] + co["arc"] + co["polyline"] + co["polycurve"])
        self.assertEqual((co["arc"], co["polyline"]), (1, 1))     # circle + rectangle, nothing else
        self.assertEqual(rep["open_ends"], 0)
        self.assertTrue(rep["scale_check"]["ok"])
        rows = self.objs()
        self.assertEqual(len(rows), 2)
        objs = self.get([r["handle"] for r in rows])
        self.assertTrue(all(o["closed"] for o in objs), objs)
        self.assertTrue(all(o["valid"] for o in objs))
        # the rectangle is exactly 4000 x 3000 and sits left of the circle (x order preserved, not mirrored)
        by_w = sorted(objs, key=lambda o: o["bbox"]["max"][0] - o["bbox"]["min"][0])
        circ, rect = by_w[0], by_w[1]
        self.assertAlmostEqual(circ["bbox"]["max"][0] - circ["bbox"]["min"][0], 1600, 2)
        self.assertAlmostEqual(rect["bbox"]["max"][0] - rect["bbox"]["min"][0], 4000, 2)
        self.assertAlmostEqual(rect["bbox"]["max"][1] - rect["bbox"]["min"][1], 3000, 2)
        self.assertLess(rect["bbox"]["max"][0], circ["bbox"]["min"][0])
        self.assertAlmostEqual(circ["bbox"]["min"][0] - rect["bbox"]["min"][0], 6200, 2)   # 6200 in the model
        self.assertAlmostEqual(circ["bbox"]["min"][1] - rect["bbox"]["min"][1], 700, 2)

    def test_front_view_orientation(self):
        hs = self.mk(box(0, 0, 0, 1000, 500, 2000), box(3000, 0, 0, 4500, 500, 500))
        d, _ = self.m2d(hs, view="Front")
        objs = self.get([r["handle"] for r in self.objs()])
        self.assertEqual(len(objs), 2)
        objs.sort(key=lambda o: o["bbox"]["min"][0])
        tall, low = objs
        self.assertAlmostEqual(tall["bbox"]["max"][1] - tall["bbox"]["min"][1], 2000, 2)   # z became drawing Y
        self.assertAlmostEqual(low["bbox"]["max"][1] - low["bbox"]["min"][1], 500, 2)
        self.assertAlmostEqual(low["bbox"]["min"][0] - tall["bbox"]["min"][0], 3000, 2)     # +X is to the right
        self.assertAlmostEqual(low["bbox"]["min"][1], tall["bbox"]["min"][1], 2)            # both on the ground line

    def test_overlap_pass_removes_engine_duplicates(self):
        hs = self.mk(box(0, 0, 0, 1000, 1000, 500), box(0, 0, 0, 1000, 1000, 500),
                     {"kind": "cylinder", "base": [3000, 500, 0], "radius": 400, "height": 800})
        clean, _ = self.m2d(hs, view="Top", layer_root=OUT + "::t")
        self.assertEqual(clean["report"]["overlaps_removed"], 0)            # Rhino's duplicates are dropped up front
        self.assertGreater(clean["report"]["dropped_duplicate"], 0)
        kept, _ = self.m2d(hs, view="Top", layer_root=OUT + "::k", keep_engine_duplicates=True)
        rep = kept["report"]
        self.assertGreater(rep["overlaps_removed"], 0)                      # the overlap pass has to do the work now
        self.assertEqual(rep["curves_out"], clean["report"]["curves_out"])  # ... and ends up with the same drawing
        self.assertEqual(rep["segments_out"], clean["report"]["segments_out"])
        self.assertEqual(rep["open_ends"], 0)

    def test_overlapping_line_sources_are_cut_to_one_copy(self):
        hs = self.mk({"kind": "line", "from": [0, 0, 0], "to": [1000, 0, 0]},
                     {"kind": "line", "from": [500, 0, 0], "to": [1500, 0, 0]},
                     {"kind": "line", "from": [0, 0, 0], "to": [1000, 0, 0]},
                     {"kind": "circle", "center": [500, 800, 0], "radius": 300},
                     {"kind": "circle", "center": [500, 800, 0], "radius": 300})
        d, _ = self.m2d(hs, view="Top", keep_engine_duplicates=True)
        rep = d["report"]
        self.assertGreater(rep["overlaps_removed"], 0)
        objs = self.get([r["handle"] for r in self.objs()])
        total_len = sum(o["length"] for o in objs if not o["closed"])
        self.assertAlmostEqual(total_len, 1500, 2)                  # union of the three lines, each part once
        circles = [o for o in objs if o["closed"]]
        self.assertEqual(len(circles), 1)

    def test_section_cut_loop_and_hatch(self):
        hs = self.mk(box(0, 0, 0, 4000, 3000, 2500))
        clip = self.clip_z(1200)
        self.assertEqual(clip["view"]["direction"], [0.0, 0.0, -1.0])
        d, r = self.m2d(hs, view="Top", clipping_planes=[clip["handle"]])
        rep = d["report"]
        self.assertEqual(rep["closed_section_loops"], 1)
        self.assertEqual(rep["open_section_curves"], 0)
        self.assertEqual(rep["hatches_created"], 1)
        self.assertEqual(rep["open_ends"], 0)
        rows = self.objs()
        types = sorted(set(x["type"] for x in rows))
        self.assertIn("hatch", types)
        sec = [x for x in rows if x["layer"].endswith("::Section")]
        hat = [x for x in rows if x["layer"].endswith("::Hatch")]
        self.assertEqual((len(sec), len(hat)), (1, 1))
        ly = ok(c.call("layers_list", {"under": OUT + "::t"}))["layers"]
        section_layer = [l for l in ly if l["path"].endswith("::Section")][0]
        self.assertAlmostEqual(section_layer["print_width_mm"], 0.5)

    def test_section_of_hollow_solid_hatches_with_hole(self):
        hs = self.mk({"kind": "cylinder", "base": [0, 0, 0], "radius": 1000, "height": 2000},
                     {"kind": "cylinder", "base": [0, 0, -10], "radius": 700, "height": 2100})
        tube = ok(c.call("geometry_boolean", {"op": "difference", "a": hs[0], "b": hs[1]}))["created"][0]
        clip = self.clip_z(1000)
        d, _ = self.m2d(tube, view="Top", clipping_planes=[clip["handle"]])
        rep = d["report"]
        self.assertEqual(rep["closed_section_loops"], 2)         # outer + inner circle
        self.assertEqual(rep["hatches_created"], 1)              # one hatch with a hole, not two
        self.assertEqual(rep["curves_out"]["arc"], 2)

    def test_hidden_lines_on_dashed_layer(self):
        # a small box behind a big one (seen from the front, +Y is away from the viewer)
        hs = self.mk(box(0, 0, 0, 4000, 500, 3000), box(1000, 1500, 500, 2000, 2500, 1500))
        d, _ = self.m2d(hs, view="Front", include_hidden=True)
        rep = d["report"]
        self.assertGreater(rep["by_category"].get("hidden", 0), 0)
        ly = {l["path"]: l for l in ok(c.call("layers_list", {"under": OUT + "::t"}))["layers"]}
        hidden = ly[OUT + "::t::Hidden"]
        self.assertEqual(hidden["linetype"], "Hidden")
        self.assertAlmostEqual(hidden["print_width_mm"], 0.18)
        hidden_objs = [x for x in self.objs() if x["layer"].endswith("::Hidden")]
        self.assertGreater(len(hidden_objs), 0)
        # without the flag there is no hidden line at all
        d2, _ = self.m2d(hs, view="Front", layer_root=OUT + "::n")
        self.assertNotIn("hidden", d2["report"]["by_category"])
        self.assertEqual(d2["report"]["open_ends"], 0)

    def test_scale_1_to_100(self):
        hs = self.mk(box(0, 0, 0, 12000, 8000, 3000))
        d, _ = self.m2d(hs, view="Top", scale="1:100", origin=[0, -20000, 0])
        bb = d["report"]["bbox"]
        self.assertAlmostEqual(bb["size"][0], 120, 3)
        self.assertAlmostEqual(bb["size"][1], 80, 3)
        self.assertEqual(bb["min"], [0.0, -20000.0, 0.0])        # origin = lower-left corner of the drawing
        sc = d["report"]["scale_check"]
        self.assertTrue(sc["ok"])
        self.assertEqual(sc["scale"], "1:100")
        self.assertAlmostEqual(sc["model_ratio"][0], 1.0, 3)
        self.assertAlmostEqual(sc["factor"], 0.01)
        o = self.get([self.objs()[0]["handle"]])[0]
        self.assertAlmostEqual(o["bbox"]["max"][0] - o["bbox"]["min"][0], 120, 3)

    def test_compare_native(self):
        hs = self.mk(box(0, 0, 0, 4000, 3000, 2500), box(4000, 1000, 0, 6000, 2000, 1500),
                     {"kind": "cylinder", "base": [9000, 1500, 0], "radius": 800, "height": 3000})
        before = ok(c.call("objects_query", {"limit": 1}))["total"]
        d, _ = self.m2d(hs, view="Top", compare_native=True)
        nat = d["native"]
        ours = d["report"]
        self.assertTrue(nat["ok"], nat)
        self.assertGreater(nat["curves"], 0)
        self.assertLessEqual(ours["curves_out"]["total"], nat["curves"])
        self.assertLess(ours["curves_out"]["total"], nat["curves"])        # joined outlines vs one curve per edge
        self.assertEqual(ours["curves_out"]["nurbs"], 0)
        self.assertGreater(nat["curves_by_kind"]["nurbs"], 0)               # Rhino writes NURBS where lines/arcs do
        self.assertLessEqual(ours["open_ends"], nat["open_ends"])
        # the native output was removed again: only our own curves are new
        after = ok(c.call("objects_query", {"limit": 1}))["total"]
        self.assertEqual(after - before, d["output_objects"])
        names = [l["path"] for l in ok(c.call("layers_list", {"limit": 500}))["layers"]]
        self.assertFalse([n for n in names if n.startswith("Make2D")], names)   # native Make2D layers were removed again

    def test_clipping_convention_matches_rhino(self):
        # our tools and Rhino's own Make2D keep the same side of a clipping plane
        hs = self.mk(box(0, 0, 0, 4000, 3000, 2500))
        for looking, kept in (("down", 1200.0), ("up", 1300.0)):
            clip = self.clip_z(1200, looking=looking)
            d, _ = self.m2d(hs, view="Front", clipping_planes=[clip["handle"]], compare_native=True, layer_root=OUT + "::" + looking)
            self.assertAlmostEqual(d["report"]["bbox"]["size"][1], kept, 2)
            self.assertAlmostEqual(d["native"]["extent"][1], kept, 2)

    def test_group_by_source_and_sublayers(self):
        hs = self.mk(box(0, 0, 0, 1000, 1000, 500), {"kind": "cylinder", "base": [3000, 500, 0], "radius": 400, "height": 800})
        clip = self.clip_z(300)
        d, _ = self.m2d(hs, view="Top", clipping_planes=[clip["handle"]], keep_source_layer_sublayers=True)
        self.assertEqual(d["groups_created"], 2)                 # outline + hatch per source object
        self.assertTrue(all(l.startswith(OUT + "::t::") and l.endswith("::drawing") for l in d["layers"]), d["layers"])
        d2, _ = self.m2d(hs, view="Top", layer_root=OUT + "::g", group_by="none")
        self.assertEqual(d2["groups_created"], 0)

    def test_custom_scheme_and_hatch_pattern(self):
        hs = self.mk(box(0, 0, 0, 1000, 1000, 500))
        clip = self.clip_z(300)
        d, _ = self.m2d(hs, view="Top", clipping_planes=[clip["handle"]], hatch={"pattern": "Hatch1", "scale": 5, "rotation_deg": 45},
                        layer_scheme={"section": {"layer": "Cut", "print_width_mm": 0.7, "color": "#ff0000"}})
        self.assertEqual(d["report"]["hatches_created"], 1)
        ly = {l["path"]: l for l in ok(c.call("layers_list", {"under": OUT + "::t"}))["layers"]}
        cut = ly[OUT + "::t::Cut"]
        self.assertAlmostEqual(cut["print_width_mm"], 0.7)
        self.assertEqual(cut["color"], "#ff0000")
        d2, r2 = self.m2d(hs, view="Top", layer_root=OUT + "::u", clipping_planes=[clip["handle"]], hatch="NoSuchPattern")
        self.assertTrue(any("NoSuchPattern" in w for w in r2.get("warnings", [])))
        self.assertEqual(d2["report"]["hatches_created"], 1)
        d3, _ = self.m2d(hs, view="Top", layer_root=OUT + "::v", clipping_planes=[clip["handle"]], hatch="none")
        self.assertEqual(d3["report"]["hatches_created"], 0)

    def test_views_plane_direction_and_perspective_flag(self):
        hs = self.mk(box(0, 0, 0, 4000, 3000, 2500), {"kind": "sphere", "center": [7000, 1500, 1000], "radius": 900})
        d, _ = self.m2d(hs, view={"plane": {"origin": [0, 0, 0], "x_axis": [1, 0, 0], "y_axis": [0, 0, 1]}})
        self.assertEqual(d["view"]["direction"], [0.0, 1.0, 0.0])    # same as Front
        self.assertTrue(d["report"]["scale_check"]["ok"])
        d, _ = self.m2d(hs, view={"direction": [-1, -1, -1], "name": "iso"}, layer_root=OUT + "::iso")
        self.assertTrue(d["report"]["scale_check"]["ok"])
        self.assertIsNone(d["report"]["scale_check"]["model_ratio"])  # only axis-aligned views get the exact model check
        d, r = self.m2d(hs, view="Perspective", layer_root=OUT + "::p")
        self.assertTrue(d["view"]["perspective"])
        self.assertTrue(any("perspective" in w for w in r["warnings"]))

    def test_mesh_sources_are_flagged(self):
        h = self.mk(box(0, 0, 0, 1000, 1000, 500))[0]
        code = ("import scriptcontext as sc, Rhino\nimport Rhino.Geometry as rg\n"
                "m = rg.Mesh.CreateFromBrep(sc.doc.Objects.FindId(System.Guid(args['g'])).Geometry)[0]\n"
                "at = Rhino.DocObjects.ObjectAttributes(); at.LayerIndex = sc.doc.Layers.FindByFullPath('%s', -1)\n"
                "result = str(sc.doc.Objects.AddMesh(m, at))" % SRC)
        r = c.call("run_python", {"code": "import System\n" + code, "args": {"g": ok(c.call("measure", {"target": h, "per_object": 1, "include_guids": True}))["objects"][0]["guid"]}})
        mesh_guid = ok(r)["result"]
        d, rr = self.m2d(mesh_guid, view="Top")
        self.assertEqual(d["report"]["mesh_sources"], 1)
        self.assertTrue(any("mesh" in w and "jagged" in w for w in rr["warnings"]))

    def test_clipping_plane_tool(self):
        hs = self.mk(box(0, 0, 0, 4000, 3000, 2500))
        sec = ok(c.call("drawing_clipping_plane", {"action": "create", "section_line": {"from": [-1000, 1500, 0], "to": [5000, 1500, 0]}, "looking": "left", "name": "mcp_sec"}))
        self.clips.append(sec["handle"])
        # travelling +X, left is +Y: the viewer stands at +Y and looks toward -Y
        self.assertEqual(sec["view"]["direction"], [0.0, -1.0, 0.0])
        lst = ok(c.call("drawing_clipping_plane", {"action": "list"}))
        self.assertIn(sec["handle"], [p["handle"] for p in lst["clipping_planes"]])
        d, _ = self.m2d(hs, view=sec["view"], clipping_planes=[sec["handle"]])
        rep = d["report"]
        self.assertEqual(rep["closed_section_loops"], 1)         # the section through the box is a closed rectangle
        self.assertEqual(rep["hatches_created"], 1)
        self.assertAlmostEqual(rep["bbox"]["size"][0], 4000, 2)
        self.assertAlmostEqual(rep["bbox"]["size"][1], 2500, 2)
        # looking right keeps the other half but gives the same section outline
        sec2 = ok(c.call("drawing_clipping_plane", {"action": "create", "section_line": {"from": [-1000, 1500, 0], "to": [5000, 1500, 0]}, "looking": "right"}))
        self.clips.append(sec2["handle"])
        self.assertEqual(sec2["view"]["direction"], [0.0, 1.0, 0.0])
        gone = ok(c.call("drawing_clipping_plane", {"action": "delete", "target": sec2["handle"]}))
        self.clips.remove(sec2["handle"])
        self.assertEqual(gone["deleted"], [sec2["handle"]])
        bad = c.call("drawing_clipping_plane", {"action": "create", "z": 5, "section_line": {"from": [0, 0, 0], "to": [1, 0, 0]}})
        self.assertFalse(bad["ok"])
        self.assertEqual(bad["error"]["code"], "INVALID_ARGS")

    def test_argument_errors_teach(self):
        hs = self.mk(box(0, 0, 0, 1000, 1000, 500))
        r = c.call("drawing_make2d", {"target": hs, "view": "Nope", "scale": "x", "layer_scheme": {"bogus": {}}})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")
        msg = r["error"]["message"]
        self.assertIn("unknown view", msg)
        self.assertIn("scale", msg)
        self.assertIn("bogus", msg)
        r = c.call("drawing_make2d", {"view": "Top"})
        self.assertFalse(r["ok"])
        # annotation-only / non-drawable target
        t = ok(c.call("geometry_create", {"items": [{"kind": "point", "point": [0, 0, 0]}], "layer": SRC}))["items"][0]["handle"]
        r = c.call("drawing_make2d", {"target": t, "layer_root": OUT + "::t"})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "NO_GEOMETRY")
        r = c.call("drawing_make2d", {"target": hs, "clipping_planes": [hs[0]]})
        self.assertFalse(r["ok"])
        self.assertIn("clipping plane", r["error"]["message"])

    def test_undo_removes_the_drawing(self):
        hs = self.mk(box(0, 0, 0, 1000, 1000, 500))
        before = ok(c.call("objects_query", {"limit": 1}))["total"]
        d, _ = self.m2d(hs, view="Top")
        self.assertEqual(ok(c.call("objects_query", {"limit": 1}))["total"], before + d["output_objects"])
        ok(c.call("history_undo", {}))
        self.assertEqual(ok(c.call("objects_query", {"limit": 1}))["total"], before)


if __name__ == "__main__":
    unittest.main()

import collections
import os
import re
import shutil
import unittest

import mcp_client as c

LAYOUT = "_mcp_test_layout"
LAYER = "_mcp_test::layout"
ANNOT = "_mcp_test::annot"
OUT = "/tmp/rhinomcp_test_exports/"


def run(cmd, **kw):
    args = {"command": cmd, "input_timeout_s": 5}
    args.update(kw)
    r = c.call("run_command", args)
    assert r["ok"], r
    return r["data"]["added"]


def on_layer(handles, layer=LAYER):
    r = c.call("layers_move_objects", {"target": handles, "layer": layer, "create": True})
    assert r["ok"], r


def dxf_entities(path):
    lines = open(path, errors="replace").read().split("\n")
    cnt = collections.Counter()
    ent = False
    for i in range(0, len(lines) - 1, 2):
        code, val = lines[i].strip(), lines[i + 1].strip()
        if code == "2" and val == "ENTITIES":
            ent = True
        elif code == "0" and val == "ENDSEC":
            ent = False
        elif ent and code == "0" and val not in ("VERTEX", "SEQEND"):
            cnt[val] += 1
    return cnt


def layer_paths():
    r = c.call("layers_list", {"limit": 500})
    assert r["ok"], r
    return {l["path"]: l for l in r["data"]["layers"]}


class LayoutTests(c.IdleTestCase):
    @classmethod
    def setUpClass(cls):
        super().setUpClass()
        names = [t["name"] for t in c.rpc("tools/list")["tools"]]
        for n in ("layout_create", "layouts_list", "layout_delete", "annotate", "export"):
            assert n in names, n
        cls.preexisting = set(layer_paths())
        cls.styles_before = cls.dim_styles()
        shutil.rmtree(OUT + "layout", ignore_errors=True)
        os.makedirs(OUT + "layout", exist_ok=True)

    def setUp(self):
        self.cleanup()
        # 30 m x 20 m plan outline in mm
        self.h = run("_Polyline 0,0,0 30000,0,0 30000,20000,0 0,20000,0 0,0,0 _Enter")
        self.lines = run("_Line 0,0,0 30000,20000,0") + run("_Line 0,20000,0 30000,0,0")
        on_layer(self.h + self.lines)

    def tearDown(self):
        self.cleanup()

    @classmethod
    def dim_styles(cls):
        r = c.call("run_csharp", {"code": 'var l = new System.Collections.Generic.List<string>(); for (int i = 0; i < doc.DimStyles.Count; i++) '
                                          'if (!doc.DimStyles[i].IsDeleted && doc.DimStyles[i].Name.StartsWith("MCP ")) l.Add(doc.DimStyles[i].Name); return string.Join("|", l);'})
        assert r["ok"], r
        return set(x for x in r["data"]["result"].split("|") if x)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(OUT + "layout", ignore_errors=True)
        new = cls.dim_styles() - cls.styles_before
        if new:  # dimension styles created by the tools during these tests
            c.call("run_csharp", {"code": 'var n = new System.Collections.Generic.HashSet<string>(args["n"].ToObject<string[]>()); '
                                          'for (int i = doc.DimStyles.Count - 1; i >= 0; i--) if (n.Contains(doc.DimStyles[i].Name)) doc.DimStyles.Delete(i, true); return 0;',
                                  "args": {"n": sorted(new)}})
        for p in ("Sheet::TitleBlock", "Sheet::Details", "Sheet", ANNOT, "_mcp_test::layout", "_mcp_test"):
            lp = layer_paths()
            if p in lp and p not in cls.preexisting and lp[p]["objects_total"] == 0:
                c.call("layers_manage", {"ops": [{"op": "delete", "path": p, "recursive": True, "confirm_objects": 0}]})

    def cleanup(self):
        if any(l["name"] == LAYOUT for l in (c.call("layouts_list")["data"]["layouts"])):
            r = c.call("layout_delete", {"name": LAYOUT, "confirm": LAYOUT})
            self.assertTrue(r["ok"], r)
        d = c.call("objects_query", {"filters": {"layer": "_mcp_test", "include_sublayers": True}, "limit": 500})
        if d["ok"] and d["data"].get("total", 0) > 0:
            c.call("objects_delete", {"target": {"query": {"layer": "_mcp_test", "include_sublayers": True}}})

    def make_layout(self, **kw):
        args = {"name": LAYOUT, "paper": "A3", "orientation": "landscape",
                "details": [
                    {"view": "Top", "scale": "1:100", "rect": [10, 55, 205, 287]},
                    {"view": "Top", "scale": "1:50", "rect": [215, 55, 410, 287], "zoom_to": {"min": [0, 0, 0], "max": [8000, 6000, 0]}},
                ],
                "title_block": {"project": "Test Villa", "sheet_title": "Ground floor plan", "sheet_number": "A-101",
                                "date": "2026-10-15", "author": "MCP", "revision": "A"}}
        args.update(kw)
        r = c.call("layout_create", args)
        self.assertTrue(r["ok"], r)
        return r

    # ---- layout_create / layouts_list ------------------------------------
    def test_layout_a3_two_details_scales(self):
        r = self.make_layout()
        d = r["data"]
        self.assertEqual(d["layout"]["name"], LAYOUT)
        self.assertEqual(d["layout"]["width_mm"], 420)
        self.assertEqual(d["layout"]["height_mm"], 297)
        self.assertEqual(len(d["details"]), 2)
        for det, want in zip(d["details"], (100.0, 50.0)):
            self.assertTrue(det["within_tolerance"], det)
            self.assertAlmostEqual(det["achieved_denominator"], want, delta=want * 0.001)
            self.assertAlmostEqual(det["achieved_denominator_from_transform"], want, delta=want * 0.001)
            self.assertTrue(det["locked"])
        self.assertTrue(d["all_scales_ok"])
        # independent read-back through layouts_list
        l = c.call("layouts_list", {"name": LAYOUT})
        self.assertTrue(l["ok"], l)
        lay = l["data"]["layouts"][0]
        self.assertEqual(len(lay["details"]), 2)
        got = sorted(x["achieved_denominator"] for x in lay["details"])
        self.assertAlmostEqual(got[0], 50.0, delta=0.05)
        self.assertAlmostEqual(got[1], 100.0, delta=0.1)
        self.assertEqual(lay["details"][0]["achieved_scale"] in ("1:100", "1:50"), True)

    def test_title_block_text_on_layer(self):
        self.make_layout()
        lay = c.call("layouts_list", {"name": LAYOUT})["data"]["layouts"][0]
        self.assertGreaterEqual(lay["page_objects_by_layer"].get("Sheet::TitleBlock", 0), 12, lay)
        q = c.call("objects_query", {"filters": {"layer": "Sheet::TitleBlock", "name": "tb:*"}, "limit": 1})
        self.assertTrue(q["ok"], q)
        self.assertGreaterEqual(q["data"]["total"], 7, q)  # one value text per filled field
        for key, want in (("project", "Test Villa"), ("sheet_title", "Ground floor plan"), ("sheet_number", "A-101")):
            r = c.call("objects_query", {"filters": {"layer": "Sheet::TitleBlock", "name": "tb:" + key}, "limit": 1})
            self.assertGreaterEqual(r["data"]["total"], 1, key)
        code = ('var m = new System.Collections.Generic.Dictionary<string,string>();'
                'var vid = System.Linq.Enumerable.First(doc.Views.GetPageViews(), x => x.PageName == "%s").MainViewport.Id;'
                'foreach (var o in doc.Objects) { if (o.Attributes.ViewportId != vid) continue;  var t = o.Geometry as TextEntity; if (t != null && o.Attributes.Name != null && o.Attributes.Name.StartsWith("tb:") '
                '&& doc.Layers[o.Attributes.LayerIndex].FullPath == "Sheet::TitleBlock") m[o.Attributes.Name] = t.PlainText; }'
                'return string.Join("|", System.Linq.Enumerable.Select(m, kv => kv.Key + "=" + kv.Value));' % LAYOUT)
        rr = c.call("run_csharp", {"code": code})
        self.assertTrue(rr["ok"], rr)
        txt = rr["data"]["result"]
        for want in ("tb:project=Test Villa", "tb:sheet_title=Ground floor plan", "tb:sheet_number=A-101",
                     "tb:scale=1:100, 1:50", "tb:date=2026-10-15", "tb:author=MCP", "tb:revision=A"):
            self.assertIn(want, txt)
        scale_txt = c.call("objects_query", {"filters": {"layer": "Sheet::TitleBlock", "name": "tb:scale"}})
        self.assertGreaterEqual(scale_txt["data"]["total"], 1)

    def test_detail_layers_filter(self):
        r = self.make_layout(details=[{"view": "Top", "scale": "1:100", "layers": [LAYER], "rect": [10, 55, 205, 287]}])
        det = r["data"]["details"][0]
        self.assertTrue(det["within_tolerance"], det)
        self.assertGreaterEqual(det["layers_visible"], 2)
        code = ('var pv = System.Linq.Enumerable.First(doc.Views.GetPageViews(), x => x.PageName == "%s"); var d = System.Linq.Enumerable.First(pv.GetDetailViews()); var v = d.Viewport.Id;'
                'int a = doc.Layers.FindByFullPath("Default", -1); int b = doc.Layers.FindByFullPath("%s", -1);'
                'return doc.Layers[a].PerViewportIsVisible(v) + "|" + doc.Layers[b].PerViewportIsVisible(v);' % (LAYOUT, LAYER))
        rr = c.call("run_csharp", {"code": code})
        self.assertTrue(rr["ok"], rr)
        self.assertEqual(rr["data"]["result"], "False|True")

    def test_perspective_and_unknown_view(self):
        r = c.call("layout_create", {"name": LAYOUT, "details": [{"view": "Sideways", "scale": "1:100"}]})
        self.assertFalse(r["ok"])
        self.assertIn("unknown view", r["error"]["message"])
        r = self.make_layout(details=[{"view": "Perspective", "rect": [10, 55, 205, 287]},
                                      {"view": "Front", "scale": "1:200", "rect": [215, 55, 410, 287]}], title_block=None)
        d = r["data"]["details"]
        self.assertFalse(d[0]["parallel"])
        self.assertNotIn("requested_scale", d[0])
        self.assertTrue(d[1]["within_tolerance"], d[1])
        self.assertEqual(r["data"]["title_block"]["objects"], 4)  # frame only

    def test_layout_validation_and_duplicates(self):
        r = c.call("layout_create", {"name": LAYOUT, "paper": "A9"})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")
        self.assertIn("A9", r["error"]["message"])
        r = c.call("layout_create", {"name": LAYOUT, "details": [{"view": "Top"}]})
        self.assertFalse(r["ok"])
        self.assertIn("scale", r["error"]["message"])
        self.make_layout()
        r = c.call("layout_create", {"name": LAYOUT})
        self.assertFalse(r["ok"])
        self.assertIn("already exists", r["error"]["message"])

    def test_layout_delete_requires_confirm(self):
        self.make_layout()
        r = c.call("layout_delete", {"name": LAYOUT, "confirm": "nope"})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "CONFIRM_REQUIRED")
        r = c.call("layout_delete", {"name": LAYOUT, "confirm": LAYOUT})
        self.assertTrue(r["ok"], r)
        self.assertGreaterEqual(r["data"]["details_deleted"], 2)
        self.assertEqual(c.call("layouts_list", {"name": LAYOUT})["error"]["code"], "NOT_FOUND")
        # model geometry untouched
        self.assertEqual(c.call("objects_query", {"filters": {"layer": LAYER}})["data"]["total"], 3)

    # ---- annotate -----------------------------------------------------------
    def test_annotate_linear_dimension_measures(self):
        r = c.call("annotate", {"scale": "1:100", "layer": ANNOT, "items": [
            {"type": "linear", "from": [0, 0], "to": [12345, 0], "offset_mm": 8},
            {"type": "aligned", "from": [0, 0], "to": [30000, 20000], "offset_mm": 10},
            {"type": "linear", "from": [0, 0], "to": [0, 7000], "axis": "y"},
            {"type": "text", "text": "Living room", "point": [1000, 1000], "height_mm": 3.5},
            {"type": "angular", "center": [0, 0], "from": [3000, 0], "to": [0, 3000]},
            {"type": "leader", "points": [[5000, 5000], [6500, 7000]], "text": "Wall"},
        ]})
        self.assertTrue(r["ok"], r)
        res = r["data"]["results"]
        self.assertEqual(r["data"]["failed"], 0, res)
        self.assertAlmostEqual(res[0]["measured"], 12345, delta=0.5)
        self.assertAlmostEqual(res[1]["measured"], (30000 ** 2 + 20000 ** 2) ** 0.5, delta=0.5)
        self.assertAlmostEqual(res[2]["measured"], 7000, delta=0.5)
        self.assertIn("12345", res[0]["display_text"].replace(",", "").replace(" ", ""))
        # 3.5 mm printed at 1:100 in a mm model is 350
        self.assertAlmostEqual(res[3]["text_height_units"], 350, delta=0.01)  # printed 3.5 x 100
        self.assertAlmostEqual(res[4]["measured_deg"], 90, delta=0.01)
        self.assertEqual(res[0]["dim_style"], "MCP 1:100 t3.5 arrow")
        q = c.call("objects_query", {"filters": {"layer": ANNOT}, "limit": 50})
        self.assertEqual(q["data"]["total"], 6, q)

    def test_annotate_radial_and_errors(self):
        circ = run("_Circle 5000,5000,0 1500")
        on_layer(circ)
        r = c.call("annotate", {"scale": "1:50", "layer": ANNOT, "items": [
            {"type": "radial", "curve": circ[0], "kind": "radius"},
            {"type": "radial", "curve": circ[0], "kind": "diameter", "angle_deg": 200},
            {"type": "hatch", "curves": "nope"},
        ]})
        self.assertTrue(r["ok"], r)
        res = r["data"]["results"]
        self.assertTrue(res[0]["ok"], res[0])
        self.assertAlmostEqual(res[0]["measured"], 1500, delta=0.5)
        self.assertAlmostEqual(res[1]["measured"], 3000, delta=0.5)
        self.assertFalse(res[2]["ok"])
        r = c.call("annotate", {"items": [{"type": "text", "text": "x", "point": [0, 0]}]})
        self.assertFalse(r["ok"])
        self.assertIn("scale", r["error"]["message"])

    def test_annotate_hatch_from_closed_curve(self):
        r = c.call("annotate", {"scale": "1:100", "layer": ANNOT, "items": [
            {"type": "hatch", "curves": self.h[0], "pattern": "Hatch1", "spacing_mm": 2, "rotation_deg": 45}]})
        self.assertTrue(r["ok"], r)
        item = r["data"]["results"][0]
        self.assertTrue(item["ok"], item)
        self.assertGreaterEqual(item["hatches"], 1)
        self.assertAlmostEqual(item["base_line_spacing"], 0.125, delta=0.002)
        self.assertAlmostEqual(item["pattern_scale"], 16, delta=0.3)
        self.assertAlmostEqual(item["printed_spacing_mm"], 2, delta=0.05)
        g = c.call("objects_get", {"target": item["handle"]})
        self.assertTrue(g["ok"], g)
        self.assertIn("hatch", str(g["data"]).lower())
        bad = c.call("annotate", {"scale": "1:100", "items": [{"type": "hatch", "curves": self.lines[0]}]})
        self.assertFalse(bad["ok"])

    def test_annotate_in_layout(self):
        self.make_layout()
        before = c.call("layouts_list", {"name": LAYOUT})["data"]["layouts"][0]["page_objects"]
        r = c.call("annotate", {"layout": LAYOUT, "layer": ANNOT, "items": [
            {"type": "text", "text": "Note: all dimensions in mm", "point": [15, 40], "height_mm": 2.5},
            {"type": "linear", "from": [10, 30], "to": [110, 30], "offset_mm": 5},
        ]})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["failed"], 0, r)
        self.assertAlmostEqual(r["data"]["results"][1]["measured"], 100, delta=0.01)
        after = c.call("layouts_list", {"name": LAYOUT})["data"]["layouts"][0]["page_objects"]
        self.assertEqual(after, before + 2)

    # ---- export ---------------------------------------------------------------
    def test_export_dxf_lines_not_splines(self):
        path = OUT + "layout/box.dxf"
        r = c.call("export", {"format": "dxf", "path": path, "target": {"query": {"layer": LAYER}}})
        self.assertTrue(r["ok"], r)
        d = r["data"]
        self.assertTrue(os.path.exists(path))
        self.assertGreater(d["bytes"], 0)
        ents = d["check"]["entities"]
        self.assertGreaterEqual(ents.get("LINE", 0), 2, ents)
        self.assertEqual(ents.get("SPLINE", 0), 0, ents)
        mine = dxf_entities(path)
        self.assertEqual(dict(mine), ents)
        self.assertGreaterEqual(mine["LINE"], 2)
        self.assertEqual(mine["SPLINE"], 0)
        # refuses to overwrite, then overwrites on request
        r2 = c.call("export", {"format": "dxf", "path": path, "target": {"query": {"layer": LAYER}}})
        self.assertFalse(r2["ok"])
        self.assertEqual(r2["error"]["code"], "FILE_EXISTS")
        r3 = c.call("export", {"format": "dxf", "path": path, "overwrite": True, "target": {"query": {"layer": LAYER}}})
        self.assertTrue(r3["ok"], r3)

    def test_export_dxf_curves_polylines_option(self):
        sp = run("_Curve 0,0,0 1000,2000,0 2500,-500,0 4000,1500,0 _Enter")
        on_layer(sp)
        t = {"query": {"layer": LAYER}}
        p1 = OUT + "layout/spline_asis.dxf"
        r = c.call("export", {"format": "dxf", "path": p1, "target": sp})
        self.assertTrue(r["ok"], r)
        self.assertGreaterEqual(r["data"]["check"]["entities"].get("SPLINE", 0), 1)
        self.assertTrue(r["data"]["check"]["exploded_nurbs_risk"])
        p2 = OUT + "layout/spline_poly.dxf"
        r = c.call("export", {"format": "dxf", "path": p2, "target": sp, "curves": "polylines", "polyline_tolerance": 0.05})
        self.assertTrue(r["ok"], r)
        ents = r["data"]["check"]["entities"]
        self.assertEqual(ents.get("SPLINE", 0), 0, ents)
        self.assertGreaterEqual(ents.get("POLYLINE", 0) + ents.get("LWPOLYLINE", 0), 1, ents)
        # the model itself is unchanged: still one curve, still a nurbs curve
        q = c.call("objects_query", {"filters": {"layer": LAYER, "type": "curve"}})
        self.assertEqual(q["data"]["total"], 4)

    def test_export_relative_path_and_extension_rejected(self):
        r = c.call("export", {"format": "dxf", "path": "rel.dxf", "target": self.h[0]})
        self.assertFalse(r["ok"])
        self.assertIn("absolute", r["error"]["message"])
        r = c.call("export", {"format": "dxf", "path": OUT + "layout/x.dwg", "target": self.h[0]})
        self.assertFalse(r["ok"])
        self.assertIn(".dxf", r["error"]["message"])

    def test_export_pdf_of_layout(self):
        self.make_layout()
        path = OUT + "layout/sheet.pdf"
        r = c.call("export", {"format": "pdf", "path": path, "layout": LAYOUT})
        self.assertTrue(r["ok"], r)
        d = r["data"]
        self.assertTrue(os.path.exists(path))
        self.assertGreaterEqual(d["check"]["pages"], 1)
        self.assertTrue(d["check"]["valid_header"])
        w, h = d["check"]["first_page_mm"]
        self.assertAlmostEqual(w, 420, delta=1.5)
        self.assertAlmostEqual(h, 297, delta=1.5)
        raw = open(path, "rb").read()
        self.assertEqual(len(re.findall(rb"/Type\s*/Page(?![A-Za-z])", raw)), d["check"]["pages"])
        r2 = c.call("export", {"format": "pdf", "path": path, "layout": LAYOUT})
        self.assertEqual(r2["error"]["code"], "FILE_EXISTS")
        r3 = c.call("export", {"format": "pdf", "path": OUT + "layout/none.pdf", "layout": "no such layout"})
        self.assertEqual(r3["error"]["code"], "NOT_FOUND")
        self.assertFalse(os.path.exists(OUT + "layout/none.pdf"))

    def test_export_other_formats(self):
        box = run("-_Box 0,0,0 1000,500,0 300")
        on_layer(box)
        for fmt, ext, key in (("3dm", ".3dm", "valid_header"), ("step", ".step", "valid_header"), ("stl", ".stl", "triangles"),
                              ("obj", ".obj", "faces"), ("dwg", ".dwg", "valid_header"), ("ai", ".ai", "valid_header"), ("svg", ".svg", "valid_header")):
            p = OUT + "layout/box" + ext
            r = c.call("export", {"format": fmt, "path": p, "target": box})
            self.assertTrue(r["ok"], (fmt, r))
            self.assertGreater(r["data"]["bytes"], 0, fmt)
            self.assertTrue(r["data"]["check"][key], (fmt, r["data"]["check"]))
        # selection is restored (nothing was selected before)
        sel = c.call("objects_query", {"filters": {"selected": True, "layer": LAYER}})
        self.assertEqual(sel["data"]["total"], 0)


if __name__ == "__main__":
    unittest.main()

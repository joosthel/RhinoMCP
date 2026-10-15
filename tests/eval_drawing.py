"""Evaluation of drawing_make2d on an architectural massing and a product, driven ONLY through MCP tools.

Run inside the shared-Rhino lock:   scripts/dev-test.sh tests/eval_drawing.py
(It is a unittest module only so dev-test.sh can run it; it is not part of the regular test suite.)

Builds a 2-storey L-shaped building (slabs, walls with openings, stair) on _mcp_eval::drawing::model, makes plan (cut at 1.2 m),
section and south elevation at 1:100 with compare_native, repeats for the freeform vase on _mcp_eval::freeform (read only),
captures Top-view images of each drawing and writes docs/eval/drawing/{results.json,*.png}.
The drawings stay in the document on _mcp_eval::drawing::* (they are cleared at the START of the next run).
"""
import base64
import json
import os
import unittest

import mcp_client as c

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "docs", "eval", "drawing")
BASE = "_mcp_eval::drawing"
MODEL = BASE + "::model"
PRODUCT_SRC = "_mcp_eval::freeform"      # read only, used when present
PRODUCT = BASE + "::product"             # otherwise the same vase is rebuilt here


def ok(r):
    assert r["ok"], r
    return r["data"]


def box(x0, y0, z0, x1, y1, z1):
    return {"kind": "box", "min": [x0, y0, z0], "max": [x1, y1, z1]}


L_OUT = [[0, 0], [12000, 0], [12000, 6000], [6000, 6000], [6000, 12000], [0, 12000]]
L_IN = [[300, 300], [11700, 300], [11700, 5700], [5700, 5700], [5700, 11700], [300, 11700]]


def ring(z0, z1):
    """L-shaped wall ring as two extrusions to subtract."""
    return [{"kind": "extrusion", "points": [[x, y, z0] for x, y in L_OUT], "height": z1 - z0, "name": "outer"},
            {"kind": "extrusion", "points": [[x, y, z0 - 10] for x, y in L_IN], "height": z1 - z0 + 20, "name": "inner"}]


class DrawingEval(unittest.TestCase):
    def test_eval(self):
        os.makedirs(OUT, exist_ok=True)
        results = {"building": {}, "product": {}, "notes": {}}
        # ---- clear the previous run (only our own layers; never touch _mcp_eval::freeform / layout)
        c.call("objects_delete", {"target": {"query": {"layer": BASE, "include_sublayers": True}}, "confirm_count": 100000})
        for cp in ok(c.call("drawing_clipping_plane", {"action": "list"}))["clipping_planes"]:
            if (cp.get("name") or "").startswith("mcp_eval_"):
                c.call("drawing_clipping_plane", {"action": "delete", "target": cp["handle"]})
        c.call("layers_manage", {"ops": [{"op": "delete", "path": BASE, "recursive": True, "confirm_objects": 0}]})

        try:
            self.build_building(results)
            self.draw_building(results)
            self.draw_product(results)
        finally:
            with open(os.path.join(OUT, "results.json"), "w") as f:
                json.dump(results, f, indent=1, sort_keys=True)

    # ------------------------------------------------------------------ building
    def build_building(self, results):
        def create(items, layer):
            d = ok(c.call("geometry_create", {"items": items, "layer": layer}))
            bad = [i for i in d["items"] if not i["ok"]]
            assert not bad, bad
            return [i["handle"] for i in d["items"]]

        def cut(a, cutters):
            d = ok(c.call("geometry_boolean", {"op": "difference", "a": a, "b": cutters}))
            return d["created"][0]

        walls_l = MODEL + "::Walls"
        for storey, (z0, z1) in enumerate([(0, 3000), (3300, 6300)]):
            outer, inner = create(ring(z0, z1), walls_l)
            wall = cut(outer, [inner])
            if storey == 0:
                door = create([box(2000, -50, 0, 3200, 350, 2200)], walls_l)[0]
                win = create([box(11650, 2000, 900, 12050, 4000, 2100)], walls_l)[0]
            else:
                door = create([box(2000, -50, 3300 + 900, 3600, 350, 3300 + 2100)], walls_l)[0]
                win = create([box(11650, 2000, 3300 + 900, 12050, 4000, 3300 + 2100)], walls_l)[0]
            wall = cut(wall, [door, win])
        # slabs
        create([{"kind": "extrusion", "points": [[x, y, -300] for x, y in L_OUT], "height": 300, "name": "ground slab"}], MODEL + "::Slabs")
        slab2 = create([{"kind": "extrusion", "points": [[x, y, 3000] for x, y in L_OUT], "height": 300, "name": "first floor slab"}], MODEL + "::Slabs")[0]
        void = create([box(7000, 2300, 2990, 11000, 3700, 3310)], MODEL + "::Slabs")[0]
        cut(slab2, [void])
        create([{"kind": "extrusion", "points": [[x, y, 6300] for x, y in L_OUT], "height": 300, "name": "roof slab"}], MODEL + "::Slabs")
        # stair: 16 steps, riser 187.5, tread 250, in the slab void, solid steps (unioned)
        steps = [box(7000 + 250 * i, 2300, 0, 11000, 3700, 187.5 * (i + 1)) for i in range(16)]
        hs = create(steps, MODEL + "::Stair")
        d = ok(c.call("geometry_boolean", {"op": "union", "a": hs}))
        results["notes"]["stair_union"] = d.get("step_used")
        q = ok(c.call("objects_query", {"filters": {"layer": MODEL, "include_sublayers": True}, "group_by": "layer", "limit": 100}))
        results["building"]["model_objects"] = q["total"]
        results["building"]["model_groups"] = q["groups"]
        ok(c.call("sets_save", {"name": "eval_drawing_model", "target": {"query": {"layer": MODEL, "include_sublayers": True}}}))
        self.capture("model.png", {"view": "Perspective", "zoom_to": {"set": "eval_drawing_model"}, "display_mode": "Shaded", "width": 1200, "height": 900})

    # ------------------------------------------------------------------ drawings
    def capture(self, fname, args):
        r = c.call("view_capture", args)
        if r["ok"] and r.get("_images"):
            with open(os.path.join(OUT, fname), "wb") as f:
                f.write(base64.b64decode(r["_images"][0]["data"]))
            return True
        print("capture failed", fname, r.get("error"))
        return False

    def draw(self, results_bucket, key, target, capture_name, **kw):
        args = {"target": target, "compare_native": True}
        args.update(kw)
        r = c.call("drawing_make2d", args)
        assert r["ok"], r
        d = r["data"]
        results_bucket[key] = {"report": d["report"], "native": d.get("native"), "layers": d["layers"], "warnings": r.get("warnings", []), "origin": d["origin"]}
        self.capture(capture_name, {"view": "Top", "zoom_to": {"set": d["output_set"]}, "display_mode": "Pen", "width": 1400, "height": 1000})
        return d

    def draw_building(self, results):
        tgt = {"query": {"layer": MODEL, "include_sublayers": True}}
        b = results["building"]
        plan_clip = ok(c.call("drawing_clipping_plane", {"action": "create", "z": 1200, "looking": "down", "name": "mcp_eval_plan_1200", "layer": BASE + "::clipping"}))
        sec_clip = ok(c.call("drawing_clipping_plane", {"action": "create", "section_line": {"from": [-1000, 3000, 0], "to": [13000, 3000, 0]}, "looking": "left",
                                                          "name": "mcp_eval_section_y3000", "layer": BASE + "::clipping"}))
        b["plan_clip"] = plan_clip
        b["section_clip"] = sec_clip
        self.draw(b, "plan", tgt, "plan.png", view="Top", clipping_planes=[plan_clip["handle"]], scale="1:100",
                  origin=[0, -30000, 0], layer_root=BASE + "::plan_1200")
        self.draw(b, "section", tgt, "section.png", view=sec_clip["view"], clipping_planes=[sec_clip["handle"]], scale="1:100",
                  origin=[300, -30000, 0], layer_root=BASE + "::section_y3000")
        self.draw(b, "elevation", tgt, "elevation.png", view="Front", scale="1:100",
                  origin=[600, -30000, 0], layer_root=BASE + "::elevation_south")
        self.draw(b, "elevation_east", tgt, "elevation_east.png", view="Right", scale="1:100",
                  origin=[750, -30000, 0], layer_root=BASE + "::elevation_east")
        self.draw(b, "elevation_hidden", tgt, "elevation_hidden.png", view="Front", scale="1:100", include_hidden=True, include_tangent_edges=True,
                  origin=[900, -30000, 0], layer_root=BASE + "::elevation_south_hidden")

    # ------------------------------------------------------------------ product
    def build_product(self, p):
        """The vase of the freeform evaluation (7 circles lofted twice, outer minus inner), rebuilt because _mcp_eval::freeform is absent."""
        def circle(cx, cy, z, r):
            return {"circle": {"center": [cx, cy, z], "radius": r}}
        cx, cy = 20000, 0
        h_out = [0, 30, 70, 110, 150, 185, 200]
        r_out = [32, 48, 52, 40, 26, 30, 38]
        h_in = [3, 30, 70, 110, 150, 185, 205]
        r_in = [29, 45, 49, 37, 23, 27, 36]
        d = ok(c.call("freeform_surface", {"layer": PRODUCT, "items": [
            {"kind": "loft", "curves": [circle(cx, cy, z, r) for z, r in zip(h_out, r_out)], "cap": True, "name": "vase_outer"},
            {"kind": "loft", "curves": [circle(cx, cy, z, r) for z, r in zip(h_in, r_in)], "cap": True, "name": "vase_inner"}]}))
        b = ok(c.call("geometry_boolean", {"op": "difference", "a": d["items"][0]["handle"], "b": d["items"][1]["handle"]}))
        p["built"] = {"layer": PRODUCT, "boolean_step": b.get("step_used"), "solid": b.get("solid")}
        return PRODUCT

    def draw_product(self, results):
        p = results["product"]
        src = PRODUCT_SRC
        if ok(c.call("layers_list", {"limit": 500}))["layers"] and PRODUCT_SRC in [l["path"] for l in ok(c.call("layers_list", {"limit": 500}))["layers"]]:
            p["source"] = PRODUCT_SRC + " (existing, read only)"
        else:
            src = self.build_product(p)
            p["source"] = src + " (rebuilt: _mcp_eval::freeform not present)"
        q = ok(c.call("objects_query", {"filters": {"layer": src, "include_sublayers": True}, "fields": ["handle", "type", "name", "bbox"], "limit": 100}))
        p["source_layer_objects"] = q["total"]
        rows = [r for r in q["rows"] if r["type"] in ("brep", "extrusion", "surface", "subd", "mesh")]
        vase = [r for r in rows if "vase" in (r.get("name") or "").lower()]
        if not vase:
            p["note"] = "no object named vase on %s; using every solid/surface there" % PRODUCT_SRC
            vase = rows
        if not vase:
            p["note"] = "nothing to draw on %s" % PRODUCT_SRC
            return
        hs = [r["handle"] for r in vase]
        bb = vase[0]["bbox"]
        for r in vase[1:]:
            bb = {"min": [min(a, b) for a, b in zip(bb["min"], r["bbox"]["min"])], "max": [max(a, b) for a, b in zip(bb["max"], r["bbox"]["max"])]}
        p["bbox"] = bb
        p["targets"] = [{"handle": r["handle"], "name": r.get("name")} for r in vase]
        cx, cy = (bb["min"][0] + bb["max"][0]) / 2.0, (bb["min"][1] + bb["max"][1]) / 2.0
        height = bb["max"][2] - bb["min"][2]
        size = max(bb["max"][0] - bb["min"][0], bb["max"][1] - bb["min"][1], height)
        scale = "1:2" if size > 100 else "1:1"
        zmid = (bb["min"][2] + bb["max"][2]) / 2.0
        import math
        ang = math.radians(15.0)   # off the surface seam at +X: a section exactly on the seam gives incomplete loops (see REPORT.md)
        dx, dy = math.cos(ang) * size, math.sin(ang) * size
        sec = ok(c.call("drawing_clipping_plane", {"action": "create", "section_line": {"from": [cx - dx, cy - dy, 0], "to": [cx + dx, cy + dy, 0]}, "looking": "left",
                                                     "name": "mcp_eval_vase_section", "layer": BASE + "::clipping"}))
        seam = ok(c.call("drawing_clipping_plane", {"action": "create", "section_line": {"from": [cx - size, cy, 0], "to": [cx + size, cy, 0]}, "looking": "left",
                                                      "name": "mcp_eval_vase_section_on_seam", "layer": BASE + "::clipping"}))
        oy = bb["min"][1] - 400
        self.draw(p, "elevation", hs, "vase_elevation.png", view="Front", scale=scale, origin=[bb["min"][0] - 800, oy, 0],
                  layer_root=BASE + "::vase_elevation", include_tangent_edges=True)
        self.draw(p, "section", hs, "vase_section.png", view=sec["view"], clipping_planes=[sec["handle"]], scale=scale,
                  origin=[bb["min"][0] - 600, oy, 0], layer_root=BASE + "::vase_section")
        self.draw(p, "section_on_seam", hs, "vase_section_on_seam.png", view=seam["view"], clipping_planes=[seam["handle"]], scale=scale,
                  origin=[bb["min"][0] - 700, oy, 0], layer_root=BASE + "::vase_section_on_seam")
        self.draw(p, "plan", hs, "vase_plan.png", view="Top", scale=scale, origin=[bb["min"][0] - 400, oy, 0], layer_root=BASE + "::vase_plan")
        p["scale"] = scale

"""End-to-end evaluation of layouts, annotation and export (feat/drawing-layout).

Builds an A2 sheet for the geometry on `_mcp_eval::drawing` (or simple plan geometry on `_mcp_eval::layout` when that layer is
absent), exports PDF + DXF to docs/eval/layout/ and leaves the layout in the document.

    scripts/dev-test.sh tests/eval_layout.py
"""
import collections
import json
import os
import shutil
import subprocess
import unittest

import mcp_client as c

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.normpath(os.path.join(HERE, "..", "docs", "eval", "layout"))
SHEET = "EVAL-A2-Plan"
SRC_LAYER = "_mcp_eval::drawing"
FALLBACK_LAYER = "_mcp_eval::layout"
ANNOT = "_mcp_eval::annot"
TMP = "/tmp/rhinomcp_test_exports/eval/"


def run(cmd):
    r = c.call("run_command", {"command": cmd, "input_timeout_s": 5})
    assert r["ok"], (cmd, r)
    return r["data"]["added"]


def layer_exists(path):
    r = c.call("layers_list", {"limit": 500})
    return path in {l["path"]: l for l in r["data"]["layers"]}


def build_fallback():
    """A 30 m x 20 m single-storey plan, 300 mm walls, columns, door swings (mm)."""
    c.call("layers_manage", {"ops": [{"op": "create", "path": FALLBACK_LAYER, "color": "#000000", "print_width_mm": 0.25}]})
    h = []
    h += run("_Polyline 0,0,0 30000,0,0 30000,20000,0 0,20000,0 0,0,0 _Enter")
    h += run("_Polyline 300,300,0 29700,300,0 29700,19700,0 300,19700,0 300,300,0 _Enter")
    h += run("_Line 12000,300,0 12000,19700,0")
    h += run("_Line 12000,10000,0 29700,10000,0")
    h += run("_Line 21000,10000,0 21000,19700,0")
    h += run("_Circle 6000,10000,0 250")
    h += run("_Circle 6000,5000,0 250")
    h += run("_Arc 12000,3000,0 12900,3900,0 12000,4800,0")
    h += run("_Arc 21000,12000,0 21900,12900,0 21000,13800,0")
    core = run("_Polyline 21300,10300,0 29400,10300,0 29400,19400,0 21300,19400,0 21300,10300,0 _Enter")
    h += core
    c.call("layers_move_objects", {"target": h, "layer": FALLBACK_LAYER})
    return FALLBACK_LAYER, core[0]


class EvalLayout(unittest.TestCase):
    def test_eval(self):
        os.makedirs(OUT_DIR, exist_ok=True)
        shutil.rmtree(TMP, ignore_errors=True)
        os.makedirs(TMP, exist_ok=True)
        notes = {}
        # tidy up a previous run
        if any(l["name"] == SHEET for l in c.call("layouts_list")["data"]["layouts"]):
            self.assertTrue(c.call("layout_delete", {"name": SHEET, "confirm": SHEET})["ok"])
        c.call("objects_delete", {"target": {"query": {"layer": ANNOT}}}) if layer_exists(ANNOT) else None

        core = None
        if layer_exists(FALLBACK_LAYER):  # our own fallback geometry from an earlier run
            c.call("objects_delete", {"target": {"query": {"layer": FALLBACK_LAYER}}})
        if layer_exists(SRC_LAYER):
            # The Make2D output is pre-scaled (the 12 m plan is 120 mm wide at 1:100) and sits next to the full-size model.
            # A detail at 1:100 needs the full-size geometry, so take the "model" sublayers when present, else a *plan* sublayer.
            ll = c.call("layers_list", {"limit": 500})["data"]["layers"]
            paths = {l["path"]: l for l in ll}
            if paths.get(SRC_LAYER + "::model", {}).get("objects_total", 0) > 0:
                src = SRC_LAYER + "::model"
                notes["source"] = "full-size model sublayers of existing " + SRC_LAYER + " (plan view, Top)"
            else:
                plans = [l for l in ll if l["path"].startswith(SRC_LAYER + "::") and "::" not in l["path"][len(SRC_LAYER) + 2:]
                         and "plan" in l["path"].lower() and "vase" not in l["path"].lower() and l["objects_total"] > 0]
                src = max(plans, key=lambda l: l["objects_total"])["path"] if plans else SRC_LAYER
                notes["source"] = "sublayer %s of existing %s" % (src, SRC_LAYER)
        else:
            src, core = build_fallback()
            notes["source"] = "built simple plan on " + FALLBACK_LAYER
        q = c.call("objects_query", {"filters": {"layer": src, "include_sublayers": True}, "fields": ["handle", "type", "bbox"], "limit": 500})
        self.assertTrue(q["ok"] and q["data"]["total"] > 0, q)
        rows = q["data"]["rows"]
        mins = [min(r["bbox"]["min"][i] for r in rows) for i in range(3)]
        maxs = [max(r["bbox"]["max"][i] for r in rows) for i in range(3)]
        w, d = maxs[0] - mins[0], maxs[1] - mins[1]
        notes["source_objects"] = q["data"]["total"]
        notes["source_bbox"] = [mins, maxs]
        # A2 landscape 594 x 420: usable detail area about 574 x 337 above the title block
        scale = 100
        for cand in (100, 200, 500):
            if w / cand <= 540 and d / cand <= 300:
                scale = cand
                break
        notes["scale"] = "1:%d" % scale

        lay = c.call("layout_create", {
            "name": SHEET, "paper": "A2", "orientation": "landscape", "margins": {"left": 20, "right": 10, "top": 10, "bottom": 10},
            "details": [{"view": "Top", "scale": "1:%d" % scale, "zoom_to": {"query": {"layer": src, "include_sublayers": True}},
                         "layers": [src, ANNOT], "title": "Plan"}],
            "title_block": {"project": "Evaluation house", "sheet_title": "Ground floor plan", "sheet_number": "A-101",
                            "author": "RhinoMCP", "revision": "A"}})
        self.assertTrue(lay["ok"], lay)
        notes["layout"] = lay["data"]
        notes["layout_warnings"] = lay.get("warnings", [])
        self.assertTrue(lay["data"]["all_scales_ok"], lay)

        cx = (mins[0] + maxs[0]) / 2
        items = [
            {"type": "linear", "from": [mins[0], mins[1]], "to": [maxs[0], mins[1]], "offset_mm": -12},
            {"type": "linear", "from": [mins[0], mins[1]], "to": [mins[0], maxs[1]], "axis": "y", "offset_mm": -12},
            {"type": "text", "text": "GROUND FLOOR PLAN", "point": [cx, maxs[1] + 12 * scale], "height_mm": 5, "justify": "center"},
            {"type": "text", "text": "Living room", "point": [mins[0] + 0.2 * w, mins[1] + 0.7 * d], "height_mm": 3.5, "justify": "center"},
            {"type": "text", "text": "Bedroom", "point": [mins[0] + 0.55 * w, mins[1] + 0.25 * d], "height_mm": 3.5, "justify": "center"},
            {"type": "leader", "points": [[mins[0] + 0.4 * w, mins[1] + 0.8 * d], [mins[0] + 0.5 * w, mins[1] + 0.9 * d]], "text": "Load-bearing wall"},
        ]
        if core is not None:
            items.append({"type": "hatch", "curves": core, "pattern": "Hatch1", "spacing_mm": 2, "rotation_deg": 45})
            items.append({"type": "linear", "from": [21300, 19400], "to": [29400, 19400], "offset_mm": 8})
            items.append({"type": "aligned", "from": [12000, 300], "to": [12000, 10000], "offset_mm": -8})
        ann = c.call("annotate", {"scale": "1:%d" % scale, "layer": ANNOT, "arrows": "tick", "items": items})
        self.assertTrue(ann["ok"], ann)
        notes["annotate"] = {k: v for k, v in ann["data"].items() if k != "results"}
        notes["annotate_results"] = ann["data"]["results"]
        # sheet note in paper space
        ann2 = c.call("annotate", {"layout": SHEET, "layer": ANNOT, "items": [
            {"type": "text", "text": "All dimensions in mm. Levels in m.", "point": [25, 30], "height_mm": 2.5},
            {"type": "text", "text": "Scale 1:%d at A2" % scale, "point": [25, 22], "height_mm": 3.5}]})
        self.assertTrue(ann2["ok"], ann2)

        # exports
        pdf = c.call("export", {"format": "pdf", "path": TMP + "sheet.pdf", "layout": SHEET})
        self.assertTrue(pdf["ok"], pdf)
        notes["pdf"] = pdf["data"]
        dxf_all = c.call("export", {"format": "dxf", "path": TMP + "plan.dxf", "target": {"query": {"layer": src, "include_sublayers": True}}})
        self.assertTrue(dxf_all["ok"], dxf_all)
        notes["dxf_default"] = dxf_all["data"]
        dxf_ann = c.call("export", {"format": "dxf", "path": TMP + "plan_annot.dxf", "target": {"query": {"layer": ANNOT}}})
        notes["dxf_annotation"] = dxf_ann.get("data") or dxf_ann.get("error")
        dxf_poly = c.call("export", {"format": "dxf", "path": TMP + "plan_poly.dxf", "curves": "polylines",
                                     "target": {"query": {"layer": src, "include_sublayers": True}}})
        notes["dxf_polylines"] = dxf_poly.get("data") or dxf_poly.get("error")
        svg = c.call("export", {"format": "svg", "path": TMP + "plan.svg", "target": {"query": {"layer": src, "include_sublayers": True}}})
        notes["svg"] = svg.get("data") or svg.get("error")
        # DXF of the clean 2D Make2D layers made by the drawing agent (what a DXF deliverable really contains)
        clean = {}
        ll = c.call("layers_list", {"limit": 500})["data"]["layers"]
        for l in ll:
            leaf = l["path"][len(SRC_LAYER) + 2:]
            if l["path"].startswith(SRC_LAYER + "::") and "::" not in leaf and l["objects_total"] > 0 and leaf.split("_")[0] in ("plan", "section", "elevation"):
                for mode in ("as_is", "polylines"):
                    rr = c.call("export", {"format": "dxf", "path": TMP + "clean_%s_%s.dxf" % (leaf, mode), "curves": mode,
                                           "target": {"query": {"layer": l["path"], "include_sublayers": True}}})
                    clean["%s/%s" % (leaf, mode)] = (rr["data"]["check"]["entities"] if rr["ok"] else rr["error"]["message"])
        notes["dxf_clean_layers"] = clean
        ai = c.call("export", {"format": "ai", "path": TMP + "plan.ai", "target": {"query": {"layer": src, "include_sublayers": True}}})
        notes["ai"] = ai.get("data") or ai.get("error")

        # deliverables
        pdf_size = os.path.getsize(TMP + "sheet.pdf")
        notes["pdf_bytes"] = pdf_size
        if pdf_size < 2 * 1024 * 1024:
            shutil.copy(TMP + "sheet.pdf", os.path.join(OUT_DIR, "sheet.pdf"))
        # render the PDF with the OS (outside Rhino) for a visual check
        png = os.path.join(OUT_DIR, "sheet.png")
        try:
            subprocess.check_call(["sips", "-s", "format", "png", "--resampleWidth", "1600", TMP + "sheet.pdf", "--out", png],
                                  stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            notes["png_bytes"] = os.path.getsize(png)
        except Exception as e:  # sips only exists on macOS
            notes["png_error"] = str(e)
        summary = {
            "dxf_default": dxf_all["data"]["check"], "dxf_polylines": (dxf_poly.get("data") or {}).get("check"),
            "dxf_annotation_layer": (dxf_ann.get("data") or {}).get("check"),
            "clean_make2d_layers": clean,
            "bytes": {"plan.dxf": dxf_all["data"]["bytes"], "plan_poly.dxf": (dxf_poly.get("data") or {}).get("bytes")},
        }
        json.dump(summary, open(os.path.join(OUT_DIR, "dxf_entity_summary.json"), "w"), indent=2, sort_keys=True)
        json.dump(notes, open(os.path.join(OUT_DIR, "run_notes.json"), "w"), indent=2, sort_keys=True, default=str)
        print(json.dumps({"scale": notes["scale"], "pdf": notes["pdf"]["check"], "dxf": summary["dxf_default"]}, indent=1))


if __name__ == "__main__":
    unittest.main()

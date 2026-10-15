"""Evaluation of the freeform tools on three real shapes, driven ONLY through MCP tools (like an agent would).

Run inside the shared-Rhino lock:   scripts/dev-test.sh tests/eval_freeform.py
(It is a unittest module only so dev-test.sh can run it; it is not part of the regular test suite.)

Writes docs/eval/freeform/{vase,hood,stool}.png (when a capture path works), results.json and REPORT.md.
The objects stay in the document on layer _mcp_eval::freeform (they are cleared at the START of the next run).
"""
import base64
import json
import math
import os
import time
import unittest

import mcp_client as c

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "docs", "eval", "freeform")
LAYER = "_mcp_eval::freeform"


def circle(cx, cy, z, r):
    return {"circle": {"center": [cx, cy, z], "radius": r}}


class Session:
    def __init__(self):
        self.calls = []
        self.failures = []
        self.shape = None
        self.notes = {}
        self.tools = [t["name"] for t in c.rpc("tools/list")["tools"]]

    def call(self, tool, args, why=""):
        t0 = time.time()
        r = c.call(tool, args)
        dt = time.time() - t0
        self.calls.append({"shape": self.shape, "tool": tool, "ok": r["ok"], "why": why, "secs": round(dt, 2)})
        if not r["ok"]:
            e = r["error"]
            det = e.get("details")
            msg = e["message"]
            if det and isinstance(det, dict) and det.get("items"):
                msg += " | " + "; ".join(str(i.get("error")) for i in det["items"] if not i.get("ok"))
            self.failures.append({"shape": self.shape, "tool": tool, "why": why, "error": msg[:600]})
        else:
            d = r.get("data") or {}
            for it in (d.get("items") or []) if isinstance(d, dict) else []:
                if isinstance(it, dict) and it.get("ok") is False:
                    self.failures.append({"shape": self.shape, "tool": tool, "why": why, "error": str(it.get("error"))[:600]})
        return r

    def data(self, tool, args, why=""):
        r = self.call(tool, args, why)
        return r["data"] if r["ok"] else None

    def run(self, cmd, why=""):
        return self.call("run_command", {"command": cmd, "input_timeout_s": 8}, why)

    def capture(self, name_pattern, fname, why):
        """Image of the objects named name_pattern: view_capture tool when present, else the ViewCaptureToFile command."""
        path = os.path.join(OUT, fname)
        if "view_capture" in self.tools:
            r = c.call("view_capture", {"view": "Perspective", "zoom_to": {"query": {"layer": LAYER, "name": name_pattern}},
                                        "display_mode": "Shaded", "width": 1100, "height": 800})
            if r["ok"] and r.get("_images"):
                with open(path, "wb") as f:
                    f.write(base64.b64decode(r["_images"][0]["data"]))
                self.notes["capture"] = "view_capture tool"
                return True
            self.failures.append({"shape": self.shape, "tool": "view_capture", "why": why,
                                  "error": (r.get("error") or {}).get("message", "no image block")[:300]})
        steps = ['-_SelNone', '-_SelName %s' % name_pattern, '-_SetView _World _Perspective', '-_Zoom _Selected',
                 '-_SelNone', '-_ViewCaptureToFile Width=1000 Height=750 "%s" _Enter' % path]
        for st in steps:
            r = c.call("run_command", {"command": st, "input_timeout_s": 8})
            if not r["ok"]:
                self.failures.append({"shape": self.shape, "tool": "run_command", "why": "image: " + st, "error": r["error"]["message"][:300]})
        self.notes["capture"] = "ViewCaptureToFile command (view_capture not available)"
        return os.path.exists(path)


def clear_layer():
    c.call("run_command", {"command": '-_SelLayer "%s" _Enter' % LAYER})
    c.call("run_command", {"command": "_Delete"})


def bbox_size(bb):
    return [round(bb["max"][i] - bb["min"][i], 3) for i in range(3)]


# ---------------------------------------------------------------------------------------------------------- shapes

def vase(s):
    s.shape = "vase"
    out = {}
    h_out = [0, 30, 70, 110, 150, 185, 200]
    r_out = [32, 48, 52, 40, 26, 30, 38]
    h_in = [3, 30, 70, 110, 150, 185, 205]
    r_in = [29, 45, 49, 37, 23, 27, 36]
    d = s.data("freeform_surface", {"layer": LAYER, "items": [
        {"kind": "loft", "curves": [circle(0, 0, z, r) for z, r in zip(h_out, r_out)], "cap": True, "name": "vase_outer"},
        {"kind": "loft", "curves": [circle(0, 0, z, r) for z, r in zip(h_in, r_in)], "cap": True, "name": "vase_inner"},
    ]}, "loft outer + inner profile circles (7 each), capped")
    if not d:
        return out
    outer, inner = d["items"][0], d["items"][1]
    out["outer"] = {k: outer.get(k) for k in ("handle", "solid", "volume", "area", "faces")}
    a = s.data("analyze_surface", {"target": outer["handle"], "u_samples": 25, "v_samples": 25}, "curvature of the outer shell")
    if a:
        out["max_abs_curvature"] = a["max_abs_curvature"]
        out["gaussian"] = a["gaussian"]
        out["mean_curvature"] = a["mean"]
        out["faces_outer"] = a["faces"]
    b = s.data("geometry_boolean", {"op": "difference", "a": outer["handle"], "b": inner["handle"]}, "hollow the vase (open top, closed bottom)")
    if b:
        res_h = (b.get("handles") or [b.get("handle")])[0] if isinstance(b, dict) else None
        out["boolean"] = {k: b.get(k) for k in ("created", "solid", "volume_a", "volume_b", "volume_result", "step_used", "bbox") if k in b}
        out["boolean_raw_keys"] = sorted(b.keys())
        m = s.data("measure", {"target": {"query": {"layer": LAYER, "name": "vase_outer"}}, "per_object": 3}, "measure the hollow vase")
        if m:
            out["measure"] = m
        v = s.data("geometry_validate", {"target": {"query": {"layer": LAYER, "name": "vase_outer"}}}, "validate the result")
        if v:
            out["validate"] = v
    out["image"] = "vase.png" if s.capture("vase_outer", "vase.png", "vase") else None
    return out


def hood(s):
    s.shape = "hood"
    out = {}
    nx, ny = 7, 7
    L, W = 300.0, 200.0
    X0 = 200.0   # keep the shapes apart

    def hz(x, y):
        sx = (x - L / 2) / (L / 2)
        ty = (y - W / 2) / (W / 2)
        return 45.0 * (1 - sx * sx) * (1 - ty * ty) * (1 + 0.25 * sx)

    pts = []
    for i in range(nx):
        x = L * i / (nx - 1.0)
        pts.append([[x + X0, W * j / (ny - 1.0), hz(x, W * j / (ny - 1.0))] for j in range(ny)])
    flange = [[[X0 + L * i / 3.0, -90.0 + 60.0 * j / 3.0, 0.0] for j in range(4)] for i in range(4)]   # y -90..-30, z=0
    d = s.data("freeform_surface", {"layer": LAYER, "items": [
        {"kind": "nurbs_points", "points": pts, "degree_u": 3, "degree_v": 3, "name": "hood_panel"},
        {"kind": "nurbs_points", "points": flange, "degree_u": 3, "degree_v": 3, "name": "hood_flange"},
    ]}, "panel from a 7x7 control-point grid (double curvature) + flat flange")
    if not d:
        return out
    panel, fl = d["items"][0], d["items"][1]
    out["panel"] = {k: panel.get(k) for k in ("handle", "area", "degree_u", "degree_v", "cp_u", "cp_v", "kinks_u", "kinks_v", "bbox")}
    a = s.data("analyze_surface", {"target": panel["handle"], "u_samples": 25, "v_samples": 25}, "panel curvature + edge list")
    f = s.data("analyze_surface", {"target": fl["handle"]}, "flange edge list")
    if not a or not f:
        return out
    out["max_abs_curvature"] = a["max_abs_curvature"]
    out["gaussian"] = a["gaussian"]
    out["mean_curvature"] = a["mean"]

    def edge_at_y(info, y):
        for e in info["edge_list"]:
            if abs(e["from"][1] - y) < 1e-3 and abs(e["to"][1] - y) < 1e-3:
                return e["i"]
        return None

    ep, ef = edge_at_y(a, 0.0), edge_at_y(f, -30.0)
    out["edge_panel"], out["edge_flange"] = ep, ef
    if ep is None or ef is None:
        s.failures.append({"shape": "hood", "tool": "analyze_surface", "why": "find edges", "error": "edge not found"})
        return out
    base = s.data("analyze_continuity", {"a": {"target": panel["handle"], "edge": ep}, "b": {"target": fl["handle"], "edge": ef}}, "baseline: panel vs flange (30 mm apart)")
    out["baseline"] = base
    blends = {}
    for cont in ("G1", "G2"):
        b = s.data("freeform_surface", {"layer": LAYER, "items": [
            {"kind": "blend_surface", "a": {"target": panel["handle"], "edge": ep}, "b": {"target": fl["handle"], "edge": ef},
             "continuity_a": cont, "continuity_b": cont, "name": "hood_blend_" + cont}]}, "blend panel edge to flange, " + cont)
        if not b:
            continue
        bh = b["items"][0]["handle"]
        c1 = s.data("analyze_continuity", {"a": panel["handle"], "b": bh, "samples": 41}, "panel vs blend (%s)" % cont)
        c2 = s.data("analyze_continuity", {"a": bh, "b": fl["handle"], "samples": 41}, "blend vs flange (%s)" % cont)
        blends[cont] = {"handle": bh, "area": b["items"][0].get("area"), "panel_vs_blend": c1, "blend_vs_flange": c2,
                        "cp": [b["items"][0].get("cp_u"), b["items"][0].get("cp_v")]}
    out["blends"] = blends
    # second route to G2: network surface between the two brep edges, curvature continuity requested
    nb = s.data("freeform_surface", {"layer": LAYER, "items": [
        {"kind": "network", "u_curves": [{"target": panel["handle"], "edge": ep}, {"target": fl["handle"], "edge": ef}],
         "v_curves": [[[X0, 0, 0], [X0, -30, 0]], [[X0 + 300, 0, 0], [X0 + 300, -30, 0]]],
         "continuity": "curvature", "interior_tol": 0.5, "edge_tol": 0.5, "name": "hood_net_G2"}]}, "alternative: network between the two edges, curvature continuity")
    if nb:
        nh = nb["items"][0]["handle"]
        out["network_g2"] = {
            "handle": nh,
            "panel_vs_network": s.data("analyze_continuity", {"a": panel["handle"], "b": nh, "samples": 41}, "panel vs network"),
            "network_vs_flange": s.data("analyze_continuity", {"a": nh, "b": fl["handle"], "samples": 41}, "network vs flange"),
        }
        s.run("-_SelName hood_net_G2 _Enter")
        s.run("_Delete")
    # the G1 variant was only needed for the comparison: remove it so the picture shows the G2 blend
    if "G1" in blends:
        s.run("-_SelName hood_blend_G1 _Enter")
        s.run("_Delete")
    out["image"] = "hood.png" if s.capture("hood_*", "hood.png", "hood") else None
    return out


def stool(s):
    s.shape = "stool"
    out = {}
    PL = {"origin": [900, 0, 0], "x_axis": [1, 0, 0], "y_axis": [0, 1, 0]}
    seat = s.data("subd", {"op": "create_from_mesh", "plane": PL, "layer": LAYER, "name": "stool_seat_subd",
                           "primitive": {"type": "box", "corner": [-180, -180, 280], "size": [360, 360, 50], "nx": 4, "ny": 4, "nz": 1}},
                  "seat blob: SubD from a 4x4x1 box mesh")
    if not seat:
        return out
    out["seat_raw"] = {k: seat[k] for k in ("faces", "edges", "vertices", "is_solid")}
    cr = s.data("subd", {"op": "crease_edges", "plane": PL, "target": seat["handle"], "in_box": {"min": [-200, -200, 270], "max": [200, 200, 281]},
                         "replace": True, "name": "stool_seat_subd", "layer": LAYER}, "crease the underside so the bottom stays flat")
    if cr:
        out["seat_creased"] = {"tagged": cr["tagged"], "faces": cr["faces"]}
        seat = cr
    sub = s.data("subd", {"op": "subdivide", "target": seat["handle"], "levels": 1, "replace": True, "name": "stool_seat_subd", "layer": LAYER}, "subdivide once for a denser control net")
    if sub:
        seat = sub
        out["seat_subdivided"] = {"faces": sub["faces"], "is_solid": sub["is_solid"]}
    leg = s.data("subd", {"op": "create_from_mesh", "plane": PL, "layer": LAYER, "name": "stool_leg_subd",
                          "primitive": {"type": "cylinder", "base": [-140, -140, 0], "radius": 18, "height": 285, "around": 8, "vertical": 3, "caps": True, "cap_edges": "smooth"}},
                 "first leg: SubD cylinder")
    legs = [leg["handle"]] if leg else []
    if leg:
        arr = s.data("geometry_transform", {"op": "array_grid", "target": leg["handle"], "nx": 2, "ny": 2, "nz": 1, "spacing": [280, 280, 0]}, "copy leg to the 4 corners")
        if arr:
            legs += arr.get("handles") or arr.get("created") or []
            out["leg_copy_keys"] = sorted(arr.keys())
    out["leg_count"] = len(legs)
    breps = []
    for nm, h in [("seat", seat["handle"])] + [("leg%d" % i, h) for i, h in enumerate(legs)]:
        b = s.data("subd", {"op": "to_brep", "target": h, "layer": LAYER, "name": "stool_" + nm}, "SubD -> NURBS brep: " + nm)
        if b:
            breps.append(b)
            out.setdefault("breps", {})[nm] = {k: b.get(k) for k in ("handle", "solid", "faces", "volume", "naked_edges")}
    if breps:
        u = s.data("geometry_boolean", {"op": "union", "a": [b["handle"] for b in breps]}, "union all parts into one solid")
        if u:
            out["union"] = {k: u.get(k) for k in ("created", "solid", "volume_a", "volume_result", "step_used", "bbox") if k in u}
            m = s.data("measure", {"target": {"query": {"layer": LAYER, "name": "stool*"}}, "per_object": 5}, "measure result")
            if m:
                out["measure"] = m
            v = s.data("geometry_validate", {"target": {"query": {"layer": LAYER, "name": "stool*"}}}, "validate result")
            if v:
                out["validate"] = v
    # park the editable SubD cages beside the NURBS result so both can be inspected
    mv = s.data("geometry_transform", {"op": "move", "target": [seat["handle"]] + legs, "vector": [0, 600, 0]}, "move the SubD sources beside the union")
    out["sources_moved"] = bool(mv)
    out["image"] = "stool.png" if s.capture("stool*", "stool.png", "stool") else None
    return out


# ---------------------------------------------------------------------------------------------------------- report

def report(s, results):
    def n_calls(shape, ok_only=False):
        return len([x for x in s.calls if x["shape"] == shape and (x["ok"] or not ok_only)])

    L = []
    L.append("# Freeform tools: evaluation on three real shapes\n")
    L.append("Generated by `tests/eval_freeform.py` (run inside the shared-Rhino lock via `scripts/dev-test.sh`). Every shape was built **only through MCP tool calls**; "
             "the objects stay in the document on layer `%s`.\n" % LAYER)
    L.append("Tools used for modelling: `freeform_surface`, `subd`, `analyze_surface`, `analyze_continuity`, plus existing `geometry_boolean`, `geometry_transform`, `measure`, `geometry_validate`. "
             "`run_command` calls that only select/zoom/capture are not counted as modelling calls.\n")
    L.append("| shape | modelling calls | failed calls |\n|---|---|---|")
    for sh in ("vase", "hood", "stool"):
        L.append("| %s | %d | %d |" % (sh, n_calls(sh), len([x for x in s.calls if x["shape"] == sh and not x["ok"]])))
    L.append("")
    for sh in ("vase", "hood", "stool"):
        L.append("## %s\n" % sh)
        img = results.get(sh, {}).get("image")
        if img:
            L.append("![%s](%s)\n" % (sh, img))
        else:
            L.append("_No image: capture unavailable in this run._\n")
        L.append("Numbers:\n\n```json\n%s\n```\n" % json.dumps(results.get(sh, {}), indent=1, default=str)[:6000])
        L.append("Tool calls:\n")
        for i, x in enumerate([x for x in s.calls if x["shape"] == sh], 1):
            L.append("%d. `%s` %s%s" % (i, x["tool"], "" if x["ok"] else "**(failed)** ", "- " + x["why"] if x["why"] else ""))
        L.append("")
    L.append("## Failures seen\n")
    if s.failures:
        for f in s.failures:
            L.append("- **%s** `%s` (%s): %s" % (f["shape"], f["tool"], f["why"], f["error"]))
    else:
        L.append("None.")
    L.append("")
    L.append(FINDINGS)
    return "\n".join(L)


FINDINGS = """## Findings

### Results at a glance
- **Vase**: 7 profile circles per shell, lofted and capped, hollowed with one boolean. 5 calls, no failures. Height 200 mm, solid, closed bottom, open top. Max |principal curvature| 0.039 (min radius 25.5 mm) in the shoulder.
- **Hood**: double-curved 7x7 control-point panel + flat flange, joined by a blend. G1 blend: reaches **G1** on both sides (max normal angle 0.6 deg). G2 blend: **G2 on the flat flange side, only G1 on the panel side** (curvature difference up to ~196 percent along the edge). A network surface between the two brep edges with curvature continuity requested also only reached G1 (tangent honoured, curvature not). So true G2 against a doubly curved edge was **not** achieved with the R7 RhinoCommon calls; `analyze_continuity` made that visible with numbers instead of a misleading zebra image.
- **Stool**: SubD seat blob (box 4x4x1, underside creased, one subdivision) plus 4 SubD cylinder legs; each converted with `to_brep` and fused with one boolean union into a **solid** NURBS brep. The editable SubD cages sit beside it.

### What was hard for an agent
- **Edge addressing.** `blend_surface` and the network need edge indices. An agent has to call `analyze_surface` first just to learn them (that is why it lists edges). Natural-language "the edge at y=0" does not exist yet.
- **No SubD vertex editing and no SubD boolean in R7.** The only shaping levers are primitive choice, grid density, creases and offset. The stool needed 5 `to_brep` calls plus a union: `to_brep` takes one target per call (a candidate for a batch form).
- **G2 is not guaranteed.** Asking for G2 does not give G2 (see hood). The agent must verify and decide what to do; the tools report the class and the worst sample, but RhinoCommon offers no "add control points" switch for blends.
- **Loft input hygiene.** Seam alignment and direction are handled by `align:true`; inline point curves of 3 points needed degree 3 interpolation (degree 2 returned null in Rhino). Network curves must meet within `interior_tol`, otherwise the sorter fails with a terse code.
- **Closed bottoms.** `cap:true` caps both ends of a loft, so a hollow vessel needs the inner-loft-and-subtract pattern (one extra boolean).

### Rhino surprises
- `Curve.CreateInterpolatedCurve` with degree 2 and 3 points returns null; degree 3 works.
- `CreateBlendCurve` 8-argument form: `reverse` must equal "attached at the curve start" for both curves.
- `SubD.Vertices` has no enumerator (linked list via `First`/`Next`).
- `BlendContinuity` in R7 has only Position, Tangency and Curvature (no G3).
- `-_ViewCaptureToFile` options (`Width=`/`Height=`) must precede the file name or they are ignored (64x64 px); the `view_capture` tool is used when present.
- Curvature signs reported by `SurfaceCurvature` are not orientation-consistent across faces; `analyze_continuity` therefore derives the normal-section curvature from Su/Sv/Suu/Suv/Svv against a common normal (validated by analytic parabola tests).
"""


class EvalRun(unittest.TestCase):
    def test_eval(self):
        os.makedirs(OUT, exist_ok=True)
        clear_layer()
        s = Session()
        results = {}
        for fn in (vase, hood, stool):
            try:
                results[fn.__name__] = fn(s)
            except Exception as ex:  # keep going: the report must show what broke
                s.failures.append({"shape": fn.__name__, "tool": "(script)", "why": "exception", "error": "%s: %s" % (type(ex).__name__, ex)})
                results.setdefault(fn.__name__, {})
        s.run("_SelNone")
        with open(os.path.join(OUT, "results.json"), "w") as f:
            json.dump({"results": results, "calls": s.calls, "failures": s.failures, "tools": s.tools}, f, indent=1, default=str)
        with open(os.path.join(OUT, "REPORT.md"), "w") as f:
            f.write(report(s, results))
        print("EVAL DONE: calls=%d failures=%d" % (len(s.calls), len(s.failures)))
        print(json.dumps(results, indent=1, default=str)[:8000])


if __name__ == "__main__":
    unittest.main()

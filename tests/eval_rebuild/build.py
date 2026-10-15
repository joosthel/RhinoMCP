"""Rebuild driver: python3 build.py <phase> [...]. Handles are kept in state.json between phases.

Phases
  layers   create the _mcp_eval::rebuild layer tree
  curves   reference curves: 3D outlines, plan outlines, mat grid
  fields   height-field surfaces (through_points) for both elements
  laminate offsets, cutters, shifted copies, split, keep inner pieces, thicken, union
  check    validate / measure / distance / continuity
  cam      photo-matched capture (+ named view)
  clean    delete everything this driver made (reference curves included)
"""
import json
import os
import sys

import numpy as np

import mcpx
import shape as s

HERE = os.path.dirname(os.path.abspath(__file__))
STATE = os.path.join(HERE, "state.json")
OUT = os.path.join(HERE, "..", "..", "docs", "eval", "rebuild")
ROOT = "_mcp_eval::rebuild"
L_REF, L_LOW, L_UP, L_FIN, L_CON = (ROOT + "::reference_curves", ROOT + "::lower_element", ROOT + "::upper_element",
                                    ROOT + "::final", ROOT + "::construction")
LAYER_T = 2.0     # step between laminate layers (mm)
SHEET_T = 2.3     # sheet thickness; 0.3 mm overlap so the union never meets coincident faces
INSETS = [0.0, 3.0, 6.0]
GRID_N = (18, 13)


def load():
    return json.load(open(STATE)) if os.path.exists(STATE) else {}


def save(st):
    json.dump(st, open(STATE, "w"), indent=1)


def pts(P):
    return [[round(float(c), 4) for c in p] for p in P]


def phase_layers(st):
    ops = [{"op": "create", "path": ROOT, "color": "#808080"},
           {"op": "create", "path": L_REF, "color": "#e04040"},
           {"op": "create", "path": L_LOW, "color": "#4080e0"},
           {"op": "create", "path": L_UP, "color": "#e0a040"},
           {"op": "create", "path": L_FIN, "color": "#f0f0f0"},
           {"op": "create", "path": L_CON, "color": "#909090"}]
    mcpx.call("layers_manage", {"ops": ops}, "create rebuild layer tree")


def phase_curves(st):
    items = []
    for n, P in (("A", s.A3), ("B", s.B3)):
        items.append({"kind": "curve", "points": pts(P), "closed": True, "degree": 3, "layer": L_REF,
                      "name": "%s_outline_3d" % n})
    for n, P in (("A", s.A3), ("B", s.B3)):
        Q = P.copy()
        Q[:, 2] = -40.0
        items.append({"kind": "curve", "points": pts(Q), "closed": True, "degree": 3, "layer": L_REF,
                      "name": "%s_outline_plan" % n})
    # cutting-mat grid (12.7 mm) for photo comparison
    for i in range(-20, 21):
        x = i * s.CELL + s.SITE[0]
        items.append({"kind": "line", "from": [x, -120, 0], "to": [x, 140, 0], "layer": L_REF, "name": "mat_grid"})
    for j in range(-9, 12):
        y = j * s.CELL
        items.append({"kind": "line", "from": [s.SITE[0] - 255, y, 0], "to": [s.SITE[0] + 255, y, 0], "layer": L_REF,
                      "name": "mat_grid"})
    r = mcpx.call("geometry_create", {"items": items}, "reference curves: 3D outlines, plan outlines, mat grid", quiet=True)
    res = r["data"]["results"] if "results" in r.get("data", {}) else r["data"].get("items")
    hs = [x.get("handle") for x in res]
    print("created", len(hs), "fails", [x for x in res if not x.get("ok", True)][:3])
    st["A_out3d"], st["B_out3d"], st["A_plan"], st["B_plan"] = hs[:4]
    st["grid"] = hs[4:]
    save(st)


def phase_fields(st):
    items = []
    for n, F, P in (("A", s.FA, s.A3), ("B", s.FB, s.B3)):
        g = s.grid(F, P, *GRID_N)
        items.append({"kind": "through_points", "points": g, "degree_u": 3, "degree_v": 3, "layer": L_CON,
                      "name": "%s_field_0" % n})
    r = mcpx.call("freeform_surface", {"items": items}, "height-field surfaces (through_points 18x13)")
    res = _items(r)
    st["A_field0"], st["B_field0"] = res[0]["handle"], res[1]["handle"]
    save(st)


def _items(r):
    d = r.get("data", {})
    return d.get("results") or d.get("items") or []


def phase_laminate(st, only=None):
    for n in (only or ("A", "B")):
        lay = L_LOW if n == "A" else L_UP
        plans = [st[n + "_plan"]]
        for d in INSETS[1:]:
            r = mcpx.call("geometry_edit", {"target": st[n + "_plan"], "op": "offset_curve", "distance": d,
                                            "side": "inside", "plane": "world_xy", "keep_inputs": True},
                          "%s plan outline inset %.0f mm" % (n, d))
            plans.append(r["data"]["created"][0])
        r = mcpx.call("geometry_create", {"items": [{"kind": "extrusion", "profile": h, "vector": [0, 0, 200], "cap": False,
                                                     "layer": L_CON, "name": "%s_cutter_%d" % (n, i)}
                                                    for i, h in enumerate(plans)]},
                      "%s vertical cutters from plan outlines" % n)
        cutters = [x["handle"] for x in _items(r)]
        fields = [st[n + "_field0"]]
        for k in range(1, len(INSETS)):
            r = mcpx.call("geometry_transform", {"target": st[n + "_field0"], "op": "move", "copy": True,
                                                 "vector": [0, 0, LAYER_T * k]}, "%s field copy +%.0f mm" % (n, LAYER_T * k))
            fields.append(r["data"]["created"][0] if "created" in r["data"] else r["data"]["handles"][0])
        solids = []
        for k, (f, cu) in enumerate(zip(fields, cutters)):
            r = mcpx.call("geometry_edit", {"target": f, "op": "split", "cutter": cu}, "%s split layer %d by outline" % (n, k))
            pieces = r["data"]["created"]
            q = mcpx.call("objects_get", {"target": pieces}, "%s layer %d: bboxes of split pieces" % (n, k), quiet=True)
            rows = q["data"]["objects"]
            # inner piece = smallest bbox footprint
            def foot(o):
                b = o["bbox"]
                return (b["max"][0] - b["min"][0]) * (b["max"][1] - b["min"][1])
            rows.sort(key=foot)
            inner = rows[0]["handle"]
            outer = [o["handle"] for o in rows[1:]]
            if outer:
                mcpx.call("objects_delete", {"target": outer}, "%s layer %d: delete outer pieces" % (n, k))
            r = mcpx.call("geometry_edit", {"target": inner, "op": "offset_surface", "distance": SHEET_T, "solid": True},
                          "%s layer %d: thicken %.1f mm to a solid" % (n, k, SHEET_T))
            sol = r["data"]["created"][0]
            mcpx.call("objects_set_props", {"target": sol, "layer": lay, "name": "%s_layer_%d" % (n, k)},
                      "%s layer %d to element layer" % (n, k))
            solids.append(sol)
        st[n + "_layers"] = solids
        st[n + "_cutters"] = cutters
        st[n + "_insets"] = plans[1:]
        save(st)


def phase_clean(st):
    mcpx.call("objects_delete", {"target": {"query": {"layer": ROOT, "include_sublayers": True}}},
              "delete everything on the rebuild layer tree (restart)")
    save({})


def phase_ribs(st):
    items = []
    names = list(s.RIBS)
    for n in names:
        base, top = s.rib_curves(n, inset=4.0 if n == "B_rib_inner_C" else 2.0)
        items.append({"kind": "curve", "points": pts(base), "layer": L_CON, "name": n + "_base"})
        items.append({"kind": "curve", "points": pts(top), "layer": L_CON, "name": n + "_top"})
    r = mcpx.call("geometry_create", {"items": items}, "rib band rails (base + top) for 3 ribs")
    hs = [x["handle"] for x in _items(r)]
    lofts = [{"kind": "loft", "curves": [hs[2 * k], hs[2 * k + 1]], "type": "normal", "layer": L_CON, "name": n + "_ribbon"}
             for k, n in enumerate(names)]
    r = mcpx.call("freeform_surface", {"items": lofts}, "rib ribbons: loft base->top")
    ribbons = [x["handle"] for x in _items(r)]
    st["rib_rails"] = hs
    st["ribs"] = {}
    for n, h in zip(names, ribbons):
        r = mcpx.call("geometry_edit", {"target": h, "op": "offset_surface", "distance": 1.5, "solid": True},
                      "%s: thicken ribbon 1.5 mm to a solid" % n)
        sol = r["data"]["created"][0]
        lay = L_LOW if n.startswith("A") else L_UP
        mcpx.call("objects_set_props", {"target": sol, "layer": lay, "name": n}, "%s to element layer" % n)
        st["ribs"][n] = sol
    save(st)


def phase_union(st):
    for n in ("A", "B"):
        parts = st[n + "_layers"] + [h for k, h in st["ribs"].items() if k.startswith(n)]
        r = mcpx.call("geometry_boolean", {"op": "union", "a": parts}, "%s: union laminate layers + ribs into one solid" % n)
        st[n + "_solid"] = r["data"]["created"][0] if r.get("ok") else None
        if r.get("ok"):
            mcpx.call("objects_set_props", {"target": st[n + "_solid"], "layer": L_FIN,
                                            "name": "lower_element" if n == "A" else "upper_element"},
                      "%s solid to final layer" % n)
    save(st)


def phase_notch(st):
    """Seat B's tip on A's rim: A minus B (B kept)."""
    r = mcpx.call("geometry_boolean", {"op": "difference", "a": st["A_solid"], "b": st["B_solid"], "keep_inputs": True},
                  "notch A where B's tip rests (A minus B, keep B)")
    if r.get("ok"):
        old = st["A_solid"]
        st["A_solid"] = r["data"]["created"][0]
        mcpx.call("objects_delete", {"target": old}, "remove un-notched A")
        mcpx.call("objects_set_props", {"target": st["A_solid"], "layer": L_FIN, "name": "lower_element"}, "name notched A")
    save(st)


def phase_tidy(st):
    mcpx.call("objects_delete", {"target": {"query": {"layer": L_CON}}}, "delete construction surfaces/cutters/rails")


def photo_camera(lens=None):
    import cam
    loc = s.to_world(cam.C)
    tgt = loc + cam.fwd * 600.0
    if lens is None:
        lens = json.load(open(os.path.join(HERE, "camera.json")))["rhino_lens_mm"]  # Rhino: lens vs 24 mm frame height
    return {"location": [round(float(c), 3) for c in loc], "target": [round(float(c), 3) for c in tgt], "lens": round(float(lens), 3)}


def capture_cam(path, mode="Shaded", lens=None, purpose="photo-matched capture"):
    return mcpx.call("view_capture", {"view": photo_camera(lens), "display_mode": mode, "width": 600, "height": 600},
                     purpose, image_to=path)


if __name__ == "__main__" and len(sys.argv) > 2 and sys.argv[1] == "capcam":
    capture_cam(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else "Wireframe",
                float(sys.argv[4]) if len(sys.argv) > 4 else None)


if __name__ == "__main__" and sys.argv[1] != "capcam":
    st = load()
    for ph in sys.argv[1:]:
        globals()["phase_" + ph](st)

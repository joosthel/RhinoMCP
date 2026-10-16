"""Record the raw Rhino captures for the RhinoMCP demo video (talks to the live Rhino 7 through the MCP client).

    python3 tests/demo/record_demo.py [--work DIR] [build|details|turntable|drawings|all]

Everything modelled here is an original design written for this demo: a modular sideboard (white steel tubes,
spherical connectors, flush navy panels, oak top). No third-party products, logos or text are modelled.

Captures are written as PNG into DIR/<scene>/ (default /tmp/rhinomcp_demo) together with DIR/meta.json (the tool results
used for the captions by compose.py). Nothing is composited here: see compose.py.

Housekeeping: everything lives on the layer tree `_demo`, the user's viewport is saved as a named view first and
restored at the end, and `_demo` objects, layers, clipping planes, named sets, render materials and the temporary named
view are deleted. Rhino's window must be visible (captures draw the real viewport).
"""
import argparse
import base64
import json
import math
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import mcp_client as c  # noqa: E402

ROOT = "_demo"
SAVED_VIEW = "_demo_user_view"
MODE = "Rendered"               # one display mode for every model shot: plain white ground, soft shadow, layer materials, no grain
# NEVER use "Raytraced" here: it renders progressively on all cores and blocks the capture / the MCP server
CAP_W, CAP_H = 1840, 1035       # largest 16:9 size inside limits.max_capture_pixels (1,920,000); composed down to 1280x720
DRAW_MODE = "Rendered"          # curves + solid hatches on white (Pen adds a paper texture that reads as noise)

CAM_TARGET = [780, 190, 320]
CAM_DIST = 2900
CAM_ELEV = 22.0
LENS = 50

# modular sideboard (mm): our own proportions. Nodes on a grid, tubes between nodes, panels flush against the tubes.
NX = 2                          # bays wide
XS = [0, 780, 1560]             # node x
DEPTH = 384                     # node y: 0 .. DEPTH
ZB, ZM, ZT = 60, 437, 605       # node z: bottom rail (feet raise it by 60), mid rail, top rail
R_TUBE, R_BALL = 9, 13          # tubes O18, connectors O26
T_PANEL = 6                     # panel thickness, centred on the tube axis plane
# panel edge distance from a tube axis: ends 0.15 mm inside the tube surface at the panel face, so there is no air gap
EDGE = (R_TUBE ** 2 - (T_PANEL / 2.0) ** 2) ** 0.5 - 0.15
TOP_T, TOP_OVER = 22, 10        # oak slab thickness and overhang beyond the tube faces

MATERIALS = """
import scriptcontext as sc, Rhino, System.Drawing as sd
def setmat(path, col, refl=0.0, shine=0.3):
    idx = sc.doc.Layers.FindByFullPath(path, -1)
    m = Rhino.DocObjects.Material()
    m.Name = '_demo'
    m.DiffuseColor = sd.Color.FromArgb(*col); m.Reflectivity = refl; m.Shine = shine
    rm = Rhino.Render.RenderMaterial.CreateBasicMaterial(m)
    sc.doc.RenderMaterials.Add(rm)
    lay = sc.doc.Layers[idx]; lay.RenderMaterial = rm; lay.CommitChanges()
S = '_demo::sideboard::'
setmat(S + 'Tubes', (236, 237, 234), 0.12, 0.5)       # white powder-coated steel
setmat(S + 'Connectors', (240, 241, 238), 0.12, 0.6)
setmat(S + 'Panels', (28, 44, 86), 0.0, 0.2)          # deep navy
setmat(S + 'Fronts', (28, 44, 86), 0.0, 0.2)
setmat(S + 'Knobs', (246, 246, 243), 0.1, 0.7)
setmat(S + 'Top', (196, 148, 96), 0.0, 0.25)          # oak
setmat(S + 'Feet', (190, 192, 195), 0.1, 0.4)
print('ok')
"""

CLEAN_MATERIALS = """
import scriptcontext as sc
n = 0
for rm in list(sc.doc.RenderMaterials):
    if rm.Name == '_demo':
        try:
            n += 1 if sc.doc.RenderMaterials.Remove(rm) else 0
        except Exception:
            pass
print(n)
"""


def ok(r):
    if not r.get("ok"):
        raise RuntimeError(json.dumps(r)[:900])
    return r["data"]


def png_to(path, r):
    imgs = r.get("_images")
    if not r.get("ok") or not imgs:
        raise RuntimeError("capture failed: %s" % json.dumps(r)[:600])
    with open(path, "wb") as f:
        f.write(base64.b64decode(imgs[0]["data"]))


def cam_at(az_deg, elev=CAM_ELEV, dist=CAM_DIST, target=CAM_TARGET, lens=LENS):
    a, e = math.radians(az_deg), math.radians(elev)
    loc = [target[0] + dist * math.cos(e) * math.cos(a), target[1] + dist * math.cos(e) * math.sin(a), target[2] + dist * math.sin(e)]
    return {"location": [round(v, 3) for v in loc], "target": list(target), "lens": lens, "up": [0, 0, 1]}


def ease(t):
    return t * t * (3 - 2 * t)


class Recorder(object):
    def __init__(self, work):
        self.work = work
        os.makedirs(work, exist_ok=True)
        self.meta_path = os.path.join(work, "meta.json")
        self.meta = json.load(open(self.meta_path)) if os.path.exists(self.meta_path) else {}

    def save_meta(self):
        json.dump(self.meta, open(self.meta_path, "w"), indent=1)

    def cap(self, scene, name, view, mode=MODE, **kw):
        d = os.path.join(self.work, scene)
        os.makedirs(d, exist_ok=True)
        args = {"view": view, "display_mode": mode, "width": CAP_W, "height": CAP_H, "show_grid": False, "show_axes": False}
        if scene in ("build", "turntable", "detail"):
            args["only_layers"] = [ROOT + "::sideboard"]       # hides clipping-plane widgets and construction lines
        args.update(kw)
        png_to(os.path.join(d, name + ".png"), c.call("view_capture", args))

    # ------------------------------------------------------------------ sideboard
    def sideboard(self):
        P = ROOT + "::sideboard::"
        ok(c.call("layers_manage", {"ops": [{"op": "create", "path": P + n, "color": col} for n, col in (
            ("Tubes", "#ececea"), ("Connectors", "#f0f1ee"), ("Panels", "#1c2c56"), ("Fronts", "#1c2c56"),
            ("Knobs", "#f6f6f3"), ("Top", "#c49460"), ("Feet", "#bec0c3"))]}))
        ok(c.call("run_python", {"code": MATERIALS}))
        ys = [0, DEPTH]
        zs = [ZB, ZM, ZT]
        view = cam_at(-58)
        steps = []
        e = EDGE
        t2 = T_PANEL / 2.0

        def batch(items, layer):
            d = ok(c.call("geometry_create", {"items": items, "layer": layer}))
            bad = [i for i in d["items"] if not i["ok"]]
            if bad:
                raise RuntimeError(json.dumps(bad[:2]))
            return [i["handle"] for i in d["items"]]

        def step(key, caption):
            self.cap("build", key, view)
            steps.append({"key": key, "caption": caption})

        # 1 connectors: one O26 sphere on every grid node
        balls = batch([{"kind": "sphere", "center": [x, y, z], "radius": R_BALL} for x in XS for y in ys for z in zs], P + "Connectors")
        step("s1", 'geometry_create  kind:"sphere"  radius:13  × %d   (connectors on the grid)' % len(balls))
        # 2 tubes: guide lines between neighbouring nodes, then one pipe batch (O18)
        lines = []
        for y in ys:
            for z in zs:
                for i in range(NX):
                    lines.append({"kind": "line", "from": [XS[i], y, z], "to": [XS[i + 1], y, z]})
        for x in XS:
            for z in zs:
                lines.append({"kind": "line", "from": [x, ys[0], z], "to": [x, ys[1], z]})
        for x in XS:
            for y in ys:
                for k in range(2):
                    lines.append({"kind": "line", "from": [x, y, zs[k]], "to": [x, y, zs[k + 1]]})
        lh = batch(lines, ROOT + "::guides")
        tubes = batch([{"kind": "pipe", "curve": h, "radius": R_TUBE, "cap": "flat"} for h in lh], P + "Tubes")
        ok(c.call("objects_delete", {"target": lh, "confirm_count": len(lh)}))
        step("s2", 'geometry_create  kind:"pipe"  radius:9  × %d   (tubes node to node)' % len(tubes))
        # 3 navy panels, flush against the tubes: each ends EDGE from the tube axis (0.15 mm inside the tube surface)
        pan = []
        for i in range(NX):
            for k in range(2):                                           # back: below / above the mid rail
                pan.append({"kind": "box", "min": [XS[i] + e, DEPTH - t2, zs[k] + e], "max": [XS[i + 1] - e, DEPTH + t2, zs[k + 1] - e]})
            pan.append({"kind": "box", "min": [XS[i] + e, e, ZB - t2], "max": [XS[i + 1] - e, DEPTH - e, ZB + t2]})   # bottom
        for x in XS:                                                     # sides and the divider
            for k in range(2):
                pan.append({"kind": "box", "min": [x - t2, e, zs[k] + e], "max": [x + t2, DEPTH - e, zs[k + 1] - e]})
        ph = batch(pan, P + "Panels")
        step("s3", 'geometry_create  kind:"box"  × %d   (back, bottom, sides, divider: flush with the tubes)' % len(ph))
        # 4 fronts (door below, drawer above the mid rail) and one domed knob each
        fr = []
        for i in range(NX):
            for k in range(2):
                fr.append({"kind": "box", "min": [XS[i] + e, -t2, zs[k] + e], "max": [XS[i + 1] - e, t2, zs[k + 1] - e], "name": "drawer" if k else "door"})
        batch(fr, P + "Fronts")
        for i in range(NX):
            xc = (XS[i] + XS[i + 1]) / 2.0
            for zc in (ZM - e - 45, (ZM + ZT) / 2.0):
                kh = batch([{"kind": "sphere", "center": [xc, -t2, zc], "radius": 11}], P + "Knobs")[0]
                ok(c.call("geometry_transform", {"target": kh, "op": "scale", "xyz": [1, 0.45, 1], "origin": [xc, -t2, zc]}))
        step("s4", 'geometry_create  × 4 fronts (drawer 150, door 359) + 4 domed knobs \u00d822')
        # 5 oak top (22 mm, 10 mm overhang, fillet 2) and short feet with glider discs
        top = batch([{"kind": "box", "min": [-R_TUBE - TOP_OVER, -R_TUBE - TOP_OVER, ZT + R_BALL],
                      "max": [XS[-1] + R_TUBE + TOP_OVER, DEPTH + R_TUBE + TOP_OVER, ZT + R_BALL + TOP_T], "name": "oak_top"}], P + "Top")[0]
        ok(c.call("geometry_edit", {"target": top, "op": "fillet_edges", "radius": 2, "edges": "all"}))
        feet = []
        for x in XS:
            for y in ys:
                feet.append({"kind": "cylinder", "base": [x, y, 5], "radius": 7, "height": ZB - 5})
                feet.append({"kind": "cylinder", "base": [x, y, 0], "radius": 17, "height": 6})
        batch(feet, P + "Feet")
        step("s5", 'geometry_edit  op:"fillet_edges"  radius:2   (oak top 22 mm)  +  feet with glider discs')
        ok(c.call("objects_delete", {"target": {"query": {"layer": ROOT + "::guides"}}, "confirm_count": 1000}))
        # 6 numbers instead of pictures
        allq = {"query": {"layer": ROOT + "::sideboard", "include_sublayers": True}}
        m = ok(c.call("measure", {"target": allq, "metrics": ["bbox", "volume"]}))
        v = ok(c.call("geometry_validate", {"target": allq, "only_problems": True, "limit": 5}))
        self.meta["numbers"] = {"measure": m, "validate_summary": v}
        steps.append({"key": "s6", "caption": "measure + geometry_validate"})
        self.cap("build", "s6", cam_at(-38, dist=CAM_DIST * 1.2))
        self.meta["build"] = {"steps": steps}
        ok(c.call("sets_save", {"name": "_demo_sideboard", "target": allq}))

    # ------------------------------------------------------------------ close-ups used to check the details (not in the video)
    def details(self):
        shots = {"node_corner": ([0, 0, ZT], 260), "knob": ([(XS[0] + XS[1]) / 2.0, 0, (ZM + ZT) / 2.0], 320), "mid_rail": ([XS[1], 0, ZM], 300)}
        for name, (tgt, dist) in shots.items():
            self.cap("detail", name, cam_at(-40, 12, dist, tgt, 50))

    # ------------------------------------------------------------------ turntable
    def turntable(self, frames=120):
        os.makedirs(os.path.join(self.work, "turntable"), exist_ok=True)
        for i in range(frames):
            az = -58 + 360.0 * ease(i / float(frames))   # fixed distance, lens and elevation: only the azimuth changes
            self.cap("turntable", "f%03d" % i, cam_at(az, dist=CAM_DIST * 1.15))
        self.meta["turntable"] = {"frames": frames}

    # ------------------------------------------------------------------ drawings
    def drawings(self):
        S = ROOT + "::sideboard"
        tgt = {"query": {"layer": S, "include_sublayers": True}}
        plan_clip = ok(c.call("drawing_clipping_plane", {"action": "create", "z": 520, "looking": "down", "name": "_demo_plan", "layer": ROOT + "::clip"}))
        sec_clip = ok(c.call("drawing_clipping_plane", {"action": "create", "section_line": {"from": [XS[1] / 2.0, -1500, 0], "to": [XS[1] / 2.0, 1500, 0]},
                                                          "looking": "left", "name": "_demo_section", "layer": ROOT + "::clip"}))
        res = {}
        # (a Front elevation is not used: Rhino's hidden-line drops the tube silhouettes that sit tangent to the flush panels)
        for key, kw in (("plan", {"view": "Top", "clipping_planes": [plan_clip["handle"]], "origin": [0, -4000, 0]}),
                        ("section", {"view": sec_clip["view"], "clipping_planes": [sec_clip["handle"]], "origin": [0, -2000, 0]})):
            d = ok(c.call("drawing_make2d", dict(target=tgt, scale="1:10", layer_root=ROOT + "::" + key, hatch={"pattern": "Solid"}, **kw)))
            res[key] = {"report": d["report"], "set": d["output_set"], "layers": d["layers"]}
        self.meta["drawings"] = res
        for key in res:
            self.cap("drawing", key, "Top", mode=DRAW_MODE, zoom_to={"set": res[key]["set"]}, margin=0.08, only_layers=[ROOT + "::" + key])

    # ------------------------------------------------------------------ cleanup
    def cleanup(self):
        for cp in ok(c.call("drawing_clipping_plane", {"action": "list"}))["clipping_planes"]:
            if (cp.get("name") or "").startswith("_demo"):
                c.call("drawing_clipping_plane", {"action": "delete", "target": cp["handle"]})
        c.call("objects_delete", {"target": {"query": {"layer": ROOT, "include_sublayers": True}}, "confirm_count": 100000})
        c.call("sets_delete", {"name": "_demo_sideboard"})
        c.call("run_python", {"code": CLEAN_MATERIALS})
        c.call("layers_manage", {"ops": [{"op": "delete", "path": ROOT, "recursive": True, "confirm_objects": 0}]})


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("scene", nargs="?", default="all", choices=["build", "details", "turntable", "drawings", "all", "cleanup"])
    ap.add_argument("--work", default="/tmp/rhinomcp_demo")
    ap.add_argument("--keep", action="store_true", help="do not clean up / restore the viewport at the end")
    a = ap.parse_args()
    c.ensure_idle()
    rec = Recorder(a.work)
    if a.scene == "cleanup":
        rec.cleanup()
        return
    ok(c.call("views_named", {"action": "save", "name": SAVED_VIEW}))
    try:
        if a.scene in ("build", "all"):
            rec.sideboard()
        if a.scene in ("details", "all"):
            rec.details()
        if a.scene in ("turntable", "all"):
            rec.turntable()
        if a.scene in ("drawings", "all"):
            rec.drawings()
        rec.save_meta()
    finally:
        if not a.keep:
            rec.cleanup()
            c.call("views_named", {"action": "restore", "name": SAVED_VIEW})
            c.call("views_named", {"action": "delete", "name": SAVED_VIEW})


if __name__ == "__main__":
    main()

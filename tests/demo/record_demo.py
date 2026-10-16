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
import subprocess
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import mcp_client as c  # noqa: E402

ROOT = "_demo"
SAVED_VIEW = "_demo_user_view"
SHEET = "_demo_A3"
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

    # ------------------------------------------------------------------ drawings: Make2D + dimensions + labels + A3 sheet
    def purge_drawings(self):
        """Remove everything the drawing scene makes (so that it can be re-run on a kept model)."""
        for cp in ok(c.call("drawing_clipping_plane", {"action": "list"}))["clipping_planes"]:
            if (cp.get("name") or "").startswith("_demo"):
                c.call("drawing_clipping_plane", {"action": "delete", "target": cp["handle"]})
        if any(l["name"] == SHEET for l in ok(c.call("layouts_list"))["layouts"]):
            c.call("layout_delete", {"name": SHEET, "confirm": SHEET})
        for k in ("elev", "plan", "section"):
            for suffix in ("", "_dims", "_lbl"):
                path = "%s::%s%s" % (ROOT, k, suffix)
                c.call("objects_delete", {"target": {"query": {"layer": path, "include_sublayers": True}}, "confirm_count": 100000})
                c.call("layers_manage", {"ops": [{"op": "delete", "path": path, "recursive": True, "confirm_objects": 0}]})

    def drawings(self):
        self.purge_drawings()
        S = ROOT + "::sideboard::"
        P = lambda x, y: [x, y, 0]
        e = EDGE

        def handles(layer, pred=lambda b: True):
            r = ok(c.call("objects_query", {"filters": {"layer": S + layer}, "fields": ["handle", "bbox"], "limit": 500}))
            return [x["handle"] for x in r["rows"] if pred(x["bbox"])]

        front = lambda b: b["max"][1] < 20                                  # objects in the front plane y = 0 (the back ones hide behind them)
        # layers: black linework (Make2D), terracotta dimensions, navy labels
        ok(c.call("layers_manage", {"ops": [{"op": "create", "path": "%s::%s_dims" % (ROOT, k), "color": "#b3472b", "print_width_mm": 0.18} for k in ("elev", "plan", "section")]
                                           + [{"op": "create", "path": "%s::%s_lbl" % (ROOT, k), "color": "#1c2c56", "print_width_mm": 0.18} for k in ("elev", "plan", "section")]
                                           + [{"op": "create", "path": ROOT + "::plan_lbl::cut", "color": "#1c2c56", "print_width_mm": 0.5},
                                              {"op": "create", "path": ROOT + "::plan_lbl::top", "color": "#7a7f87", "linetype": "Dashed", "print_width_mm": 0.13}]}))
        res = {}
        # ---------------------------------------------------------------- Make2D at full size (the sheet details bring them to 1:10)
        # Front elevation: Rhino's hidden-line drops the tube silhouettes that sit tangent to the flush panels (also with
        # include_tangent_edges), so the elevation is made from the objects of the FRONT plane only: the back tubes, connectors and
        # feet are hidden behind them anyway. Panels, tubes and connectors then do not compete, and every silhouette survives.
        elev_t = handles("Tubes", front) + handles("Connectors", front) + handles("Knobs") + handles("Top") + handles("Feet", front)
        d = ok(c.call("drawing_make2d", {"target": elev_t, "view": "Front", "scale": "1:1", "layer_root": ROOT + "::elev", "origin": [-19, -10000, 0], "hatch": "none"}))
        res["elev"] = {"report": d["report"], "set": d["output_set"], "layers": d["layers"]}
        # the four front panels in a run of their own: together with the tubes, their edges (0.15 mm inside the tube surface) cancel the silhouettes
        c.call("drawing_make2d", {"target": handles("Fronts"), "view": "Front", "scale": "1:1", "layer_root": ROOT + "::elev", "origin": [XS[0] + e, -10000 + ZB + e, 0], "hatch": "none"})
        plan_clip = ok(c.call("drawing_clipping_plane", {"action": "create", "z": 520, "looking": "down", "name": "_demo_plan", "layer": ROOT + "::clip"}))
        sec_clip = ok(c.call("drawing_clipping_plane", {"action": "create", "section_line": {"from": [XS[1] / 2.0, -1500, 0], "to": [XS[1] / 2.0, 1500, 0]},
                                                          "looking": "left", "name": "_demo_section", "layer": ROOT + "::clip"}))
        tgt = {"query": {"layer": ROOT + "::sideboard", "include_sublayers": True}}
        for key, kw in (("plan", {"view": "Top", "clipping_planes": [plan_clip["handle"]], "origin": [-17, -20017, 0]}),
                        ("section", {"view": sec_clip["view"], "clipping_planes": [sec_clip["handle"]], "origin": [11597, -10000, 0]})):
            d = ok(c.call("drawing_make2d", dict(target=tgt, scale="1:1", layer_root=ROOT + "::" + key, hatch={"pattern": "Solid"}, **kw)))
            res[key] = {"report": d["report"], "set": d["output_set"], "layers": d["layers"]}
        self.meta["drawings"] = res
        # drawing coordinates: elevation (X, Z-10000) · plan (X, Y-20000) · section (12000-Y, Z-10000), all in mm at full size
        ex, ey = (lambda X: X), (lambda Z: Z - 10000)
        px, py = (lambda X: X), (lambda Y: Y - 20000)
        sx, sy = (lambda Y: 12000 - Y), (lambda Z: Z - 10000)
        DOOR = (ZB + e, ZM - e)
        DRAWER = (ZM + e, ZT - e)
        xl, xr = -R_TUBE - TOP_OVER, XS[-1] + R_TUBE + TOP_OVER        # top slab x extent
        yf, yb = -R_TUBE - TOP_OVER, DEPTH + R_TUBE + TOP_OVER         # top slab y extent
        zt0, zt1 = ZT + R_BALL, ZT + R_BALL + TOP_T                    # top slab z extent

        def lin(a, b, off, axis=None):
            it = {"type": "linear", "from": a, "to": b, "offset_mm": off}
            if axis:
                it["axis"] = axis
            return it

        def run_ann(layer, items, height, arrows):
            r = ok(c.call("annotate", {"scale": "1:10", "layer": layer, "arrows": arrows, "text_height_mm": height, "items": items}))
            bad = [x for x in r["results"] if not x.get("ok", True)]
            if bad:
                raise RuntimeError(json.dumps(bad[:3]))
            return r

        # ---------------------------------------------------------------- dimensions (3.5 mm text, ticks, chained, one dim style for 1:10)
        z0, zT = ey(0), ey(zt1)
        elev_dims = [lin(P(XS[0], z0), P(XS[1], z0), -8), lin(P(XS[1], z0), P(XS[2], z0), -8), lin(P(xl, z0), P(xr, z0), -18),            # bays, overall width
                     lin(P(xl, zT), P(-R_TUBE, zT), 14), lin(P(-R_TUBE, zT), P(XS[-1] + R_TUBE, zT), 14), lin(P(XS[-1] + R_TUBE, zT), P(xr, zT), 14)]   # top overhang
        for a, b in ((0, ZB), (ZB, ZM), (ZM, ZT)):                                  # foot, lower and upper zone (chain on the right)
            elev_dims.append(lin(P(xr, ey(a)), P(xr, ey(b)), 8, "y"))
        elev_dims.append(lin(P(xr, ey(0)), P(xr, ey(zt1)), 20, "y"))                           # overall height
        for a, b in (DOOR, DRAWER):                                                            # door and drawer fronts (chain on the left)
            elev_dims.append(lin(P(xl, ey(a)), P(xl, ey(b)), -8, "y"))
        run_ann(ROOT + "::elev_dims", elev_dims, 3.5, "tick")
        pz = py(0)
        plan_dims = [lin(P(XS[0], pz), P(XS[1], pz), -12), lin(P(XS[1], pz), P(XS[2], pz), -12), lin(P(xl, pz), P(xr, pz), -22),
                     lin(P(xr, py(0)), P(xr, py(DEPTH)), 8, "y"), lin(P(xr, py(yf)), P(xr, py(yb)), 18, "y")]
        run_ann(ROOT + "::plan_dims", plan_dims, 3.5, "tick")
        sec_dims = [lin(P(sx(yb), sy(0)), P(sx(yb), sy(ZB)), -8, "y"), lin(P(sx(yb), sy(ZB)), P(sx(yb), sy(ZT)), -8, "y"), lin(P(sx(yb), sy(ZT)), P(sx(yb), sy(zt1)), -8, "y"),
                    lin(P(sx(yb), sy(0)), P(sx(yb), sy(zt1)), -18, "y"),
                    lin(P(sx(yf), sy(DOOR[0])), P(sx(yf), sy(DOOR[1])), 8, "y"), lin(P(sx(yf), sy(DRAWER[0])), P(sx(yf), sy(DRAWER[1])), 8, "y"),
                    lin(P(sx(yf), sy(zt0)), P(sx(yf), sy(zt1)), 18, "y"),
                    lin(P(sx(DEPTH), sy(0)), P(sx(0), sy(0)), -8), lin(P(sx(yb), sy(0)), P(sx(yf), sy(0)), -18)]
        run_ann(ROOT + "::section_dims", sec_dims, 3.5, "tick")
        # a diameter dimension on one knob (radial dimensions need a true circle: Make2D joins it from two arcs, so a circle is added on the dimension layer)
        kc = [XS[1] / 2.0, ey((ZM + ZT) / 2.0), 0]
        kh = ok(c.call("geometry_create", {"items": [{"kind": "circle", "center": kc, "radius": 11}], "layer": ROOT + "::elev_dims"}))["items"][0]["handle"]
        run_ann(ROOT + "::elev_dims", [{"type": "radial", "curve": kh, "kind": "diameter", "angle_deg": 40}], 3.5, "tick")

        # ---------------------------------------------------------------- labels: leaders (2.5 mm), view titles (5 mm), section marker
        def lead(tip, end, text):
            return {"type": "leader", "points": [tip, end], "text": text}

        ztn = ey(zt1 - 8)
        elev_lbl = [lead(P(1100, ztn), P(1200, ey(700)), "oak top 22 mm"),
                    lead(P(XS[1] - R_BALL, ey(ZM)), P(700, ey(345)), "spherical connector \u00d826"),
                    lead(P(XS[2] - R_TUBE, ey(250)), P(1450, ey(200)), "steel tube \u00d818"),
                    lead(P(1000, ey(480)), P(1060, ey(560)), "drawer front"),
                    lead(P(1000, ey(250)), P(1060, ey(300)), "door"),
                    lead(P(XS[2] - 17, ey(3)), P(1400, ey(28)), "glider foot"),
                    {"type": "text", "text": "Front elevation 1:10", "point": P(XS[1], ey(-300)), "height_mm": 5, "justify": "center"}]
        run_ann(ROOT + "::elev_lbl", elev_lbl, 2.5, "arrow")
        plan_lbl = [lead(P(300, py(DEPTH)), P(150, py(470)), "back panel 6 mm"),
                    lead(P(XS[1] + 6, py(DEPTH + R_TUBE)), P(900, py(530)), "steel tube \u00d818"),
                    lead(P(XS[2] + 9, py(DEPTH + 9)), P(1450, py(590)), "spherical connector \u00d826"),
                    lead(P(1300, py(yb)), P(1250, py(470)), "oak top outline (above the cut)"),
                    lead(P(1000, py(0)), P(1100, py(100)), "drawer front"),
                    lead(P(-12, py(-12)), P(-130, py(-90)), "glider foot"),
                    {"type": "text", "text": "Plan 1:10", "point": P(XS[1], py(-330)), "height_mm": 5, "justify": "center"}]
        run_ann(ROOT + "::plan_lbl", plan_lbl, 2.5, "arrow")
        sec_lbl = [lead(P(sx(100), sy(zt1 - 11)), P(sx(190), sy(740)), "oak top 22 mm"),
                   lead(P(sx(0) - 9, sy(ZM)), P(11960, sy(480)), "steel tube \u00d818"),
                   lead(P(11960, sy(ZB + 3)), P(11940, sy(140)), "panel 6 mm"),
                   lead(P(sx(0), sy(250)), P(11960, sy(300)), "door"),
                   lead(P(sx(0), sy(520)), P(11960, sy(560)), "drawer front"),
                   lead(P(sx(0) + 17, sy(3)), P(12070, sy(-40)), "glider foot"),
                   {"type": "text", "text": "Section A\u2013A 1:10", "point": P(sx(DEPTH / 2.0), sy(-300)), "height_mm": 5, "justify": "center"}]
        run_ann(ROOT + "::section_lbl", sec_lbl, 2.5, "arrow")
        # section marker A-A on the plan: thin cut line, thick ends, arrows towards +x (the viewing direction), letters
        xc = XS[1] / 2.0
        y0, y1 = py(-36), py(DEPTH + 130)
        mk = [{"kind": "line", "from": P(xc, y0), "to": P(xc, y1)},
              {"kind": "polyline", "points": [P(xc, y0), P(xc, y0 + 60)]}, {"kind": "polyline", "points": [P(xc, y1 - 60), P(xc, y1)]},
              {"kind": "polyline", "points": [P(xc, y0), P(xc + 100, y0)]}, {"kind": "polyline", "points": [P(xc, y1), P(xc + 100, y1)]},
              {"kind": "polyline", "points": [P(xc + 100, y0), P(xc + 60, y0 - 16), P(xc + 60, y0 + 16)], "closed": True},
              {"kind": "polyline", "points": [P(xc + 100, y1), P(xc + 60, y1 - 16), P(xc + 60, y1 + 16)], "closed": True}]
        items = ok(c.call("geometry_create", {"items": mk, "layer": ROOT + "::plan_lbl::cut"}))["items"]
        hs = [i["handle"] for i in items]
        c.call("annotate", {"scale": "1:10", "layer": ROOT + "::plan_lbl::cut", "items": [{"type": "hatch", "curves": hs[5], "pattern": "Solid"}, {"type": "hatch", "curves": hs[6], "pattern": "Solid"}]})
        run_ann(ROOT + "::plan_lbl::cut", [{"type": "text", "text": "A", "point": P(xc + 130, y0), "height_mm": 5, "justify": "left", "valign": "middle"},
                                          {"type": "text", "text": "A", "point": P(xc + 130, y1), "height_mm": 5, "justify": "left", "valign": "middle"}], 2.5, "arrow")
        # the oak top lies above the plan cut: its outline as a thin dashed line (with the overhang)
        ok(c.call("geometry_create", {"items": [{"kind": "polyline", "points": [P(xl, py(yf)), P(xr, py(yf)), P(xr, py(yb)), P(xl, py(yb))], "closed": True}], "layer": ROOT + "::plan_lbl::top"}))
        self.meta["annot"] = {"elev": {"dims": len(elev_dims) + 1, "labels": len(elev_lbl)}, "plan": {"dims": len(plan_dims), "labels": len(plan_lbl) + 1},
                              "section": {"dims": len(sec_dims), "labels": len(sec_lbl)}}
        # framing boxes for the captures (drawing + dimensions + labels)
        self.meta["frames"] = {"elev": {"min": [-330, ey(-420), 0], "max": [1900, ey(850), 0]},
                               "plan": {"min": [-450, py(-360), 0], "max": [1900, py(620), 0]},
                               "section": {"min": [sx(yb) - 330, sy(-420), 0], "max": [sx(yf) + 330, sy(850), 0]}}
        # the captures: lines only, + dimensions, + labels
        for key in ("elev", "plan", "section"):
            fr = self.meta["frames"][key]
            for beat, layers in (("0", [ROOT + "::" + key]), ("1", [ROOT + "::" + key, ROOT + "::" + key + "_dims"]),
                                 ("2", [ROOT + "::" + key, ROOT + "::" + key + "_dims", ROOT + "::" + key + "_lbl"])):
                self.cap("drawing", "%s_%s" % (key, beat), "Top", mode=DRAW_MODE, zoom_to={"min": fr["min"], "max": fr["max"]}, margin=0.02, only_layers=layers)
        self.sheet()

    def sheet(self):
        """A3 landscape sheet: three details at 1:10 with the annotation layers, title block, export to PDF."""
        fr = self.meta["frames"]
        cen = lambda f: [(f["min"][0] + f["max"][0]) / 2.0, (f["min"][1] + f["max"][1]) / 2.0, 0]
        det = lambda key, rect: {"view": "Top", "scale": "1:10", "rect": rect, "center": cen(fr[key]), "locked": True,
                                 "layers": [ROOT + "::" + key, ROOT + "::" + key + "_dims", ROOT + "::" + key + "_lbl"]}
        lay = ok(c.call("layout_create", {"name": SHEET, "paper": "A3", "orientation": "landscape", "margins": 10,
                                           "details": [det("elev", [12, 150, 262, 287]), det("section", [265, 150, 408, 287]), det("plan", [12, 52, 262, 148])],
                                           "title_block": {"project": "Modular sideboard", "sheet_title": "General arrangement", "sheet_number": "A-01",
                                                           "scale": "1:10", "date": "2026-10-16", "author": "RhinoMCP agent"}}))
        self.meta["layout"] = lay
        ok(c.call("annotate", {"layout": SHEET, "layer": ROOT + "::sheet_notes", "items": [
            {"type": "text", "text": "Notes", "point": [275, 128], "height_mm": 3.5},
            {"type": "text", "text": "All dimensions in mm.", "point": [275, 120], "height_mm": 2.5},
            {"type": "text", "text": "Section A–A cuts through the middle of bay 1.", "point": [275, 114], "height_mm": 2.5},
            {"type": "text", "text": "Overall 1598 × 422 × 640 mm, two bays of 780 mm.", "point": [275, 108], "height_mm": 2.5}]}))
        os.makedirs(os.path.join(self.work, "sheet"), exist_ok=True)
        pdf = os.path.join(self.work, "sheet", "sheet.pdf")
        r = ok(c.call("export", {"format": "pdf", "path": pdf, "layout": SHEET, "overwrite": True}))
        # the PDF as a vector-crisp raster (view_capture refuses page views): macOS sips renders the first page, A3 at 300 dpi
        png = os.path.join(self.work, "sheet", "sheet.png")
        subprocess.check_call(["sips", "-s", "format", "png", "--resampleHeightWidthMax", "4961", pdf, "--out", png], stdout=subprocess.DEVNULL)
        dxf = ok(c.call("export", {"format": "dxf", "path": os.path.join(self.work, "sheet", "drawings.dxf"), "overwrite": True,
                                   "target": [{"query": {"layer": "%s::%s%s" % (ROOT, k, sfx), "include_sublayers": True}} for k in ("elev", "plan", "section") for sfx in ("", "_dims", "_lbl")]}))
        self.meta["sheet"] = {"pdf": pdf, "png": png, "export": r, "dxf": dxf}

    # ------------------------------------------------------------------ cleanup
    def cleanup(self):
        if any(l["name"] == SHEET for l in ok(c.call("layouts_list"))["layouts"]):
            c.call("layout_delete", {"name": SHEET, "confirm": SHEET})
        for cp in ok(c.call("drawing_clipping_plane", {"action": "list"}))["clipping_planes"]:
            if (cp.get("name") or "").startswith("_demo"):
                c.call("drawing_clipping_plane", {"action": "delete", "target": cp["handle"]})
        c.call("objects_delete", {"target": {"query": {"layer": ROOT, "include_sublayers": True}}, "confirm_count": 100000})
        c.call("sets_delete", {"name": "_demo_sideboard"})
        for st in ok(c.call("sets_list"))["sets"]:                    # the output sets of drawing_make2d (empty after the delete above)
            if st["name"].startswith("make2d_"):
                c.call("sets_delete", {"name": st["name"]})
        c.call("run_python", {"code": CLEAN_MATERIALS})
        c.call("layers_manage", {"ops": [{"op": "delete", "path": ROOT, "recursive": True, "confirm_objects": 0}]})
        c.call("layers_manage", {"ops": [{"op": "delete", "path": "Sheet", "recursive": True, "confirm_objects": 0}]})   # made by layout_create (frame, title block, details)


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

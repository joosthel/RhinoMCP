"""Record the RhinoMCP demo media by driving a live Rhino 7 through the MCP server (no OS screen recording).

  python3 tests/demo/record_demo.py capture   # build the carabiner on _demo::*, capture frames, clean up the document
  python3 tests/demo/record_demo.py compose   # overlay titles/captions with Pillow, assemble mp4 / gif / hero / stills
  python3 tests/demo/record_demo.py all

Needs: Rhino 7 open with an (empty) document and a visible window, numpy + scipy (outline data of tests/eval_carabiner),
Pillow, ffmpeg. Raw frames go to $DEMO_FRAMES (default /tmp/rhinomcp_demo_frames) and are deleted after `compose`.
"""
import base64, json, math, os, shutil, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(ROOT, "tests"))
sys.path.insert(0, os.path.join(ROOT, "tests", "eval_carabiner"))
import mcp_client as c  # noqa: E402

FRAMES = os.environ.get("DEMO_FRAMES", "/tmp/rhinomcp_demo_frames")
MEDIA = os.path.join(ROOT, "docs", "media")
W, H = 1280, 720
MODE = os.environ.get("DEMO_MODE", "Arctic")
L = "_demo::part"

CALLS = []


def call(tool, args=None):
    t0 = time.time()
    r = c.call(tool, args or {})
    CALLS.append((tool, round(time.time() - t0, 2)))
    if not r.get("ok"):
        raise RuntimeError("%s failed: %s" % (tool, json.dumps(r.get("error"))[:600]))
    return r


def cap(name, view, **kw):
    """view_capture -> FRAMES/name.png"""
    args = {"view": view, "width": W, "height": H, "display_mode": MODE, "only_layers": kw.pop("only_layers", "_demo::part")}
    args.update(kw)
    r = call("view_capture", args)
    with open(os.path.join(FRAMES, name + ".png"), "wb") as f:
        f.write(base64.b64decode(r["_images"][0]["data"]))
    return r


BOX = {"min": [-106, -62, -2], "max": [106, 62, 9]}      # whole part: every build capture frames the same box
CENTER = [0.0, 0.0, -6.0]     # looking slightly below the part keeps it in the upper frame (room for captions)


def cam(az_deg, el_deg=38.0, dist=330.0, lens=50):
    """Camera on a sphere around CENTER. az 0 = looking from the front (-Y)."""
    a, e = math.radians(az_deg), math.radians(el_deg)
    loc = [CENTER[0] + dist * math.cos(e) * math.sin(a), CENTER[1] - dist * math.cos(e) * math.cos(a), CENTER[2] + dist * math.sin(e)]
    return {"location": loc, "target": CENTER, "lens": lens}


AZ0 = -22.0
BUILD_CAM = cam(AZ0)


def build_cap(name, **kw):
    return cap(name, BUILD_CAM, **kw)


def build():
    import numpy as np
    from scipy.interpolate import CubicSpline
    import plan, trace as T
    import straight_fit as sf
    import build_base_v2 as v2

    call("layers_manage", {"ops": [{"op": "create", "path": L}]})
    # 1. outline curves (straight front edge + control-point curve, joined)
    (Co, _, do), (Ci, Di, di) = v2.curves()
    p3 = lambda C: [[round(float(x), 4), round(float(y), 4), 0.0] for x, y in C]
    l3 = lambda P: [float(P[0]), float(P[1]), 0.0]
    r = call("geometry_create", {"layer": L, "items": [
        {"kind": "line", "from": l3(sf.OUTER_LINE[1]), "to": l3(sf.OUTER_LINE[0])},
        {"kind": "curve", "points": p3(Co), "mode": "control_points", "degree": 3},
        {"kind": "line", "from": l3(sf.INNER_LINE[1]), "to": l3(sf.INNER_LINE[0])},
        {"kind": "curve", "points": p3(Ci), "mode": "control_points", "degree": 3}]})
    h = [i["handle"] for i in r["data"]["items"]]
    outer = call("geometry_edit", {"target": h[:2], "op": "join"})["data"]["created"][0]
    inner = call("geometry_edit", {"target": h[2:], "op": "join"})["data"]["created"][0]
    build_cap("s1_curves")
    # 2. extrusion + boolean difference
    t = plan.THICK_MM
    r = call("geometry_create", {"layer": L, "items": [
        {"kind": "extrusion", "profile": outer, "height": t, "cap": True},
        {"kind": "extrusion", "profile": inner, "height": t + 2, "cap": True}]})
    (plate, pb), (cutter, cb) = [(i["handle"], i["bbox"]) for i in r["data"]["items"]]
    call("geometry_transform", {"target": [plate], "op": "move", "vector": [0, 0, -pb["min"][2]]})
    call("geometry_transform", {"target": [cutter], "op": "move", "vector": [0, 0, -1 - cb["min"][2]]})
    body = call("geometry_boolean", {"op": "difference", "a": plate, "b": cutter})["data"]["created"][0]
    call("objects_delete", {"target": [outer, inner]})
    build_cap("s2_solid")
    # 3. rounded edges
    call("geometry_edit", {"target": body, "op": "fillet_edges", "radius": 2.0, "edges": [0, 2, 4, 5, 6, 7, 8, 9]})
    build_cap("s3_fillet")
    # 4. gate split + latch notches (one boolean)
    GAP = 0.4
    inner_dense = np.vstack([Di, np.linspace(Di[-1], Di[0], 200)[1:-1]])

    def slot(P, ext=3.0, n=12):
        P = np.asarray(P, float)
        if len(P) == 2:
            P = np.array([P[0], (P[0] + P[1]) / 2, P[1]])
        s = np.r_[0, np.cumsum(np.linalg.norm(np.diff(P, axis=0), axis=1))]
        cs = CubicSpline(s, P, bc_type="natural")
        q = np.linspace(-ext, s[-1] + ext, n)
        C = cs(q); d = cs(q, 1); d /= np.linalg.norm(d, axis=1)[:, None]
        nrm = np.c_[-d[:, 1], d[:, 0]]
        ring = np.vstack([C + nrm * GAP / 2, (C - nrm * GAP / 2)[::-1]])
        return [[round(float(x), 3), round(float(y), 3), 0.0] for x, y in ring]

    def notch(px, w=1.2, depth=1.5, z0=1.5, z1=5.5):
        p = plan.to_plan([px])[0]
        k = np.argmin(((inner_dense - p) ** 2).sum(1)); o = inner_dense[k]
        tg = inner_dense[(k + 1) % len(inner_dense)] - inner_dense[k - 1]; tg /= np.linalg.norm(tg)
        nrm = np.array([-tg[1], tg[0]])
        if np.dot(nrm, o - inner_dense.mean(0)) < 0:
            nrm = -nrm
        pl = {"origin": [float(o[0]), float(o[1]), 0.0], "x_axis": [float(tg[0]), float(tg[1]), 0.0], "y_axis": [float(nrm[0]), float(nrm[1]), 0.0]}
        return {"kind": "box", "corner": [-w / 2, -0.6, z0], "size": [w, depth + 0.6, z1 - z0], "plane": pl}

    pl0 = {"origin": [0, 0, -1], "x_axis": [1, 0, 0], "y_axis": [0, 1, 0]}
    items = [{"kind": "extrusion", "points": slot(plan.to_plan(T.SPLIT_L)), "height": t + 2, "plane": pl0},
             {"kind": "extrusion", "points": slot(plan.to_plan(T.SPLIT_R)), "height": t + 2, "plane": pl0},
             notch(T.NOTCHES[0]), notch(T.NOTCHES[1], w=1.0)]
    r = call("geometry_create", {"items": items, "layer": L})
    cut = [i["handle"] for i in r["data"]["items"] if i.get("ok")]
    assert len(cut) == 4, r
    r = call("geometry_boolean", {"op": "difference", "a": body, "b": cut})
    parts = r["data"]["created"]
    print("boolean:", json.dumps(r["data"])[:500])
    # the gate is the small solid: compare bbox diagonals
    objs = call("objects_get", {"target": parts})["data"]
    objs = objs.get("objects") or objs.get("items") or objs
    def diag(o):
        b = o.get("bbox") or {}
        return sum((b["max"][i] - b["min"][i]) ** 2 for i in range(3)) if b else 0
    ranked = sorted(zip(parts, [diag(o) for o in objs]), key=lambda x: -x[1])
    body, gate = ranked[0][0], ranked[-1][0]
    build_cap("s4_gate")
    gb = [o for o in objs if o.get("handle") == gate][0]["bbox"]
    cap("s4_gate_zoom", BUILD_CAM, zoom_to={"min": gb["min"], "max": gb["max"]}, margin=0.18)
    # 5. engraving (geometry_edit engrave)
    TXT = plan.to_plan([(542, 1325), (685, 1357)]).mean(0)
    r = call("geometry_edit", {"target": body, "op": "engrave", "text": "HELIOT EMIL", "text_height": 4.2,
                               "depth": 0.3, "at": [float(TXT[0]), float(TXT[1]), 7.0], "center": True})
    print("engrave:", json.dumps(r["data"])[:400])
    return {"body": body, "gate": gate, "text_at": [float(TXT[0]), float(TXT[1])]}


def captures(st):
    tx, ty = st["text_at"]
    build_cap("s5_engraved")
    n = 14                                  # push-in towards the engraved band
    for i in range(n):
        u = i / (n - 1.0); u = u * u * (3 - 2 * u)
        hx, hy = 106 + (30 - 106) * u, 62 + (11 - 62) * u
        cap("push_%02d" % i, BUILD_CAM, zoom_to={"min": [tx - hx, ty - hy, -2], "max": [tx + hx, ty + hy, 9]}, margin=0.06)
    N, SWEEP = 96, 170.0                    # turntable
    for i in range(N):
        cap("orbit_%03d" % i, cam(AZ0 + SWEEP * i / (N - 1.0)))


def drawings(st):
    """Top plan + plan section at z=3.5 (hatched), each in its own layer root."""
    both = [st["body"], st["gate"]]
    r = call("drawing_make2d", {"target": both, "view": "Top", "scale": "1:1", "origin": [140, -60, 0],
                                 "layer_root": "_demo::dr_plan", "hatch": "none"})
    st["plan_report"] = r["data"]
    cp = call("drawing_clipping_plane", {"action": "create", "z": 3.5, "looking": "down", "name": "_demo_cut", "layer": "_demo::cp"})
    print("cp:", json.dumps(cp["data"])[:400])
    d = cp["data"]
    cph = d.get("handle") or (d.get("clipping_plane") or {}).get("handle")
    r = call("drawing_make2d", {"target": both, "view": d.get("view", "Top"), "clipping_planes": [cph], "scale": "1:1", "origin": [380, -60, 0],
                                 "layer_root": "_demo::dr_cut", "hatch": {"pattern": "Hatch1", "scale": 3, "rotation_deg": 45},
                                 "layer_scheme": {"hatch": {"color": "#2f6f9f"}, "section": {"color": "#16324a"}}})
    st["cut_report"] = r["data"]
    print("plan", json.dumps(st["plan_report"])[:600]); print("cut", json.dumps(st["cut_report"])[:600])


def cleanup(saved_view):
    steps = [
        ("objects", lambda: call("objects_delete", {"target": {"query": {"layer": "_demo", "include_sublayers": True}}, "confirm_count": 100000})),
        ("clipping planes", lambda: [call("drawing_clipping_plane", {"action": "delete", "target": cp["handle"]})
                                     for cp in call("drawing_clipping_plane", {"action": "list"})["data"]["clipping_planes"]
                                     if (cp.get("name") or "").startswith("_demo")]),
        ("layers", lambda: call("layers_manage", {"ops": [{"op": "delete", "path": "_demo", "recursive": True, "confirm_objects": 0}]})),
        ("view", lambda: (call("views_named", {"action": "restore", "name": saved_view}), call("views_named", {"action": "delete", "name": saved_view}))),
    ]
    for nm, fn in steps:
        try:
            fn()
        except Exception as e:
            print("cleanup %s: %s" % (nm, e))


def capture_stage():
    shutil.rmtree(FRAMES, ignore_errors=True); os.makedirs(FRAMES)
    c.ensure_idle()
    n0 = call("system_status")["data"]["document"]["objects"]
    assert n0 == 0, "document must be empty (%d objects)" % n0
    call("views_named", {"action": "save", "name": "_demo_restore"})
    st = {}
    try:
        st = build()
        captures(st)
        drawings(st)
        for nm, root in (("dr_plan", "_demo::dr_plan"), ("dr_cut", "_demo::dr_cut")):
            cap(nm, "Top", only_layers=root, zoom_to="all", margin=0.08)
        cap("dr_both", "Top", only_layers=["_demo::dr_plan", "_demo::dr_cut"], zoom_to="all", margin=0.05)
    finally:
        cleanup("_demo_restore")
    print("calls:", len(CALLS), "total tool s:", round(sum(t for _, t in CALLS), 1))


if __name__ == "__main__":
    what = sys.argv[1] if len(sys.argv) > 1 else "all"
    if what in ("capture", "all"):
        capture_stage()
    if what in ("compose", "all"):
        import compose
        compose.main(FRAMES, MEDIA)

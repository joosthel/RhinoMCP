"""Detail 2+3: gate split (two 0.4 mm slots through the top band) and two latch notches in the inner wall,
cut in ONE boolean difference. The band between the slots becomes a separate solid (the gate)."""
import json, os, sys
import numpy as np
from scipy.interpolate import CubicSpline
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import plan, trace as T, select_pts as sp
from mcpx import call

STATE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "state.json")
st = json.load(open(STATE))
L = "_mcp_eval::carabiner::detail"
t = plan.THICK_MM
GAP = float(os.environ.get("GAP", 0.4))

O, I = plan.to_plan(T.OUTER), plan.to_plan(T.INNER)
import build_base_v2 as v2   # v2 inner outline (straight front + control-point curve)
_, (_, _Di, _) = v2.curves()
inner_dense = np.vstack([_Di, np.linspace(_Di[-1], _Di[0], 200)[1:-1]])


def slot(P, ext=3.0, n=12):
    P = np.asarray(P, float)
    if len(P) == 2:
        P = np.array([P[0], (P[0] + P[1]) / 2, P[1]])
    s = np.r_[0, np.cumsum(np.linalg.norm(np.diff(P, axis=0), axis=1))]
    cs = CubicSpline(s, P, bc_type="natural")
    q = np.linspace(-ext, s[-1] + ext, n)
    C = cs(q)
    d = cs(q, 1); d /= np.linalg.norm(d, axis=1)[:, None]
    nrm = np.c_[-d[:, 1], d[:, 0]]
    A, B = C + nrm * GAP / 2, C - nrm * GAP / 2
    ring = np.vstack([A, B[::-1]])
    return [[round(float(x), 3), round(float(y), 3), 0.0] for x, y in ring]


def notch(px, w=1.2, depth=1.5, z0=1.5, z1=5.5):
    p = plan.to_plan([px])[0]
    k = np.argmin(((inner_dense - p) ** 2).sum(1))
    o = inner_dense[k]
    tng = inner_dense[(k + 1) % len(inner_dense)] - inner_dense[k - 1]; tng /= np.linalg.norm(tng)
    nrm = np.array([-tng[1], tng[0]])
    # outward = away from the opening centroid
    if np.dot(nrm, o - inner_dense.mean(0)) < 0:
        nrm = -nrm
    plane = {"origin": [float(o[0]), float(o[1]), 0.0], "x_axis": [float(tng[0]), float(tng[1]), 0.0],
             "y_axis": [float(nrm[0]), float(nrm[1]), 0.0]}
    return {"kind": "box", "corner": [-w / 2, -0.6, z0], "size": [w, depth + 0.6, z1 - z0], "plane": plane}


sl, sr = plan.to_plan(T.SPLIT_L), plan.to_plan(T.SPLIT_R)
items = [
    {"kind": "extrusion", "points": slot(sl), "height": t + 2, "plane": {"origin": [0, 0, -1], "x_axis": [1, 0, 0], "y_axis": [0, 1, 0]}, "name": "cut_split_l"},
    {"kind": "extrusion", "points": slot(sr), "height": t + 2, "plane": {"origin": [0, 0, -1], "x_axis": [1, 0, 0], "y_axis": [0, 1, 0]}, "name": "cut_split_r"},
    dict(notch(T.NOTCHES[0]), name="cut_notch_1"),
    dict(notch(T.NOTCHES[1], w=1.0), name="cut_notch_2"),
]
r = call("geometry_create", {"items": items, "layer": L}, "gate slots + notch cutters")
cut = [i["handle"] for i in r["data"]["items"] if i.get("ok")]
for i in r["data"]["items"]:
    print(i.get("kind"), i.get("ok"), i.get("volume"), i.get("bbox"), i.get("error"))
if len(cut) == 4 and os.environ.get("DRY") != "1":
    r = call("geometry_boolean", {"op": "difference", "a": st["detail"], "b": cut}, "cut gate split + notches")
    if r["ok"]:
        st["detail_parts"] = r["data"]["created"]
        json.dump(st, open(STATE, "w"), indent=1)

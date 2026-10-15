"""Owner correction: the long front edge (with the HELIOT EMIL engraving) is exactly straight.
Outer and inner outlines = LineCurve front segment + open clamped cubic control-point curve, joined (G1)."""
import json, os, sys
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import plan, trace as T, straight_fit as sf
from mcpx import call

L = "_mcp_eval::carabiner::base"
STATE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "state.json")
N_OUT, N_IN = 18, 12
RO = list(range(35, 46)) + list(range(0, 28))
RI = list(range(21, 27)) + list(range(0, 16))


def curves():
    O, I = plan.to_plan(T.OUTER), plan.to_plan(T.INNER)
    Co, Do, do, _ = sf.fit_open(O, RO, *sf.OUTER_LINE, N_OUT)
    Ci, Di, di, _ = sf.fit_open(I, RI, *sf.INNER_LINE, N_IN)
    return (Co, Do, do), (Ci, Di, di)


if __name__ == "__main__":
    st = json.load(open(STATE))
    (Co, _, do), (Ci, _, di) = curves()
    print("outer %d cps max %.2f mean %.2f | inner %d cps max %.2f mean %.2f mm"
          % (N_OUT, do.max(), do.mean(), N_IN, di.max(), di.mean()))
    p3 = lambda C: [[round(float(x), 4), round(float(y), 4), 0.0] for x, y in C]
    l3 = lambda P: [float(P[0]), float(P[1]), 0.0]
    r = call("geometry_create", {"layer": L, "items": [
        {"kind": "line", "from": l3(sf.OUTER_LINE[1]), "to": l3(sf.OUTER_LINE[0]), "name": "outer_front_line"},
        {"kind": "curve", "points": p3(Co), "mode": "control_points", "degree": 3, "name": "outer_rest"},
        {"kind": "line", "from": l3(sf.INNER_LINE[1]), "to": l3(sf.INNER_LINE[0]), "name": "inner_front_line"},
        {"kind": "curve", "points": p3(Ci), "mode": "control_points", "degree": 3, "name": "inner_rest"},
    ]}, "v2 plan: straight front lines + control-point curves")
    h = [i["handle"] for i in r["data"]["items"]]
    ro = call("geometry_edit", {"target": h[:2], "op": "join"}, "join outer line + curve")
    ri = call("geometry_edit", {"target": h[2:], "op": "join"}, "join inner line + curve")
    st["outer"], st["inner"] = ro["data"]["created"][0], ri["data"]["created"][0]
    json.dump(st, open(STATE, "w"), indent=1)
    print(st)

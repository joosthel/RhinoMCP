"""Basic shape: plan curves (few control points) -> outer extrusion minus inner cutter = one closed solid."""
import json, os, sys
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import plan, trace as T
from mcpx import call

N_OUT = int(os.environ.get("N_OUT", 14))
N_IN = int(os.environ.get("N_IN", 10))
L = "_mcp_eval::carabiner::"
STATE = os.path.join(plan.os.path.dirname(os.path.abspath(__file__)), "state.json")


def cps(P, n):
    """LSQ periodic fit with n control points, then n points ON that curve (at the knots) for a
    closed interpolated curve (the tool has no periodic control-point mode)."""
    C, dev = plan.fit_periodic(P, n)
    Q = plan.bspline_basis_periodic(n, 3, np.arange(n) / n) @ C
    return [[round(float(x), 3), round(float(y), 3), 0.0] for x, y in Q], dev


def main(with_ref=True):
    st = json.load(open(STATE)) if os.path.exists(STATE) else {}
    O, I = plan.to_plan(T.OUTER), plan.to_plan(T.INNER)
    O = O + np.array(json.loads(os.environ.get("OUT_SHIFT", "[0,0]")))
    so = json.loads(os.environ.get("SEL_OUT", "[0, 1, 5, 7, 10, 13, 16, 19, 22, 23, 26, 28, 32, 36, 39, 42, 44]"))
    si = json.loads(os.environ.get("SEL_IN", "[0, 3, 6, 7, 11, 12, 14, 16, 20, 21, 24, 25]"))
    adj = json.loads(os.environ.get("ADJ", "{}"))     # manual nudges {"o5": [dx,dy], "i3": [dx,dy]} in mm
    O, I = O.copy(), I.copy()
    for k, v in adj.items():
        (O if k[0] == "o" else I)[int(k[1:])] += v
    co = [[round(float(x), 3), round(float(y), 3), 0.0] for x, y in O[so]]
    ci = [[round(float(x), 3), round(float(y), 3), 0.0] for x, y in I[si]]
    import select_pts as sp
    do, di = sp.deviation(so, O), sp.deviation(si, I)
    print("interp outer %d pts max %.2f mean %.2f | inner %d pts max %.2f mean %.2f mm"
          % (len(so), do.max(), do.mean(), len(si), di.max(), di.mean()))
    items = [
        {"kind": "curve", "points": co, "mode": "interpolate", "degree": 3, "closed": True,
         "layer": L + "base", "name": "outer_plan"},
        {"kind": "curve", "points": ci, "mode": "interpolate", "degree": 3, "closed": True,
         "layer": L + "base", "name": "inner_plan"},
    ]
    if with_ref:
        for nm, P in (("trace_outer", O), ("trace_inner", I),
                      ("trace_split_l", plan.to_plan(T.SPLIT_L)), ("trace_split_r", plan.to_plan(T.SPLIT_R))):
            pts = [[round(float(x), 2), round(float(y), 2), plan.THICK_MM] for x, y in P]
            items.append({"kind": "polyline", "points": pts, "closed": nm in ("trace_outer", "trace_inner"),
                          "layer": L + "reference", "name": nm})
    r = call("geometry_create", {"items": items}, "plan curves + reference traces")
    res = r["data"]["items"] if "items" in r["data"] else r["data"]["results"]
    st["outer"], st["inner"] = res[0]["handle"], res[1]["handle"]
    json.dump(st, open(STATE, "w"), indent=1)
    return st


if __name__ == "__main__":
    main(with_ref=os.environ.get("REF", "1") == "1")

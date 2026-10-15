"""Pick a few trace points for a closed interpolated cubic (greedy removal by max deviation)."""
import numpy as np
from scipy.interpolate import CubicSpline


def closed_spline(P, m=1500):
    Q = np.vstack([P, P[:1]])
    s = np.r_[0, np.cumsum(np.linalg.norm(np.diff(Q, axis=0), axis=1))]
    cs = CubicSpline(s, Q, bc_type="periodic")
    return cs(np.linspace(0, s[-1], m))


def deviation(sel, P):
    D = closed_spline(P[sel])
    return np.sqrt(((P[:, None] - D[None]) ** 2).sum(-1)).min(1)


def greedy(P, n, keep=()):
    sel = list(range(len(P)))
    while len(sel) > n:
        best = None
        for k in sel:
            if k in keep:
                continue
            s2 = [j for j in sel if j != k]
            d = deviation(s2, P)
            score = d.max() + 0.5 * d.mean()
            if best is None or score < best[0]:
                best = (score, k)
        sel.remove(best[1])
    return sel, deviation(sel, P)

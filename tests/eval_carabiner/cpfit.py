"""Designer-style closed control-point curve: clamped uniform cubic whose first and last control point is
the same seam point S, with both neighbours on the tangent line through S (G1 seam on a straight run).
Least-squares fit to the trace with parameter correction. Rhino: geometry_create curve mode=control_points
with the seam point repeated (the tool has no periodic control-point mode)."""
import numpy as np
from scipy.interpolate import BSpline


def clamped_basis(ncp, t, k=3):
    inner = np.linspace(0, 1, ncp - k + 1)
    knots = np.r_[[0] * k, inner, [1] * k]
    B = np.zeros((len(t), ncp))
    for j in range(ncp):
        c = np.zeros(ncp); c[j] = 1
        B[:, j] = BSpline(knots, c, k, extrapolate=False)(np.clip(t, 0, 1 - 1e-12))
    return np.nan_to_num(B)


def resample_from(P, start_xy, m=500):
    """Dense closed polyline resampled by arc length, starting at the point nearest to start_xy
    (inserted exactly)."""
    Q = np.vstack([P, P[:1]])
    # find segment closest to start
    best = None
    for i in range(len(Q) - 1):
        a, b = Q[i], Q[i + 1]
        u = np.clip(np.dot(start_xy - a, b - a) / np.dot(b - a, b - a), 0, 1)
        d = np.linalg.norm(a + u * (b - a) - start_xy)
        if best is None or d < best[0]:
            best = (d, i, a + u * (b - a))
    _, i, S = best
    ring = np.vstack([S, Q[i + 1:-1], Q[:i + 1], S])
    s = np.r_[0, np.cumsum(np.linalg.norm(np.diff(ring, axis=0), axis=1))]
    q = np.linspace(0, s[-1], m)
    R = np.c_[np.interp(q, s, ring[:, 0]), np.interp(q, s, ring[:, 1])]
    tang = ring[1] - ring[-2]; tang /= np.linalg.norm(tang)
    return R, S, tang


def fit(P, start_xy, n_unique, iters=10, tangent=None):
    R, S, T = resample_from(P, np.asarray(start_xy, float))
    if tangent is not None:
        T = np.asarray(tangent, float) / np.linalg.norm(tangent)
    ncp = n_unique + 1
    t = np.linspace(0, 1, len(R))
    for it in range(iters):
        B = clamped_basis(ncp, t)
        # C0 = Cn = S ; C1 = S + a T ; C_{n-1} = S - b T ; free C2..C_{n-2}
        nf = ncp - 4
        rows = len(R)
        A = np.zeros((2 * rows, 2 + 2 * nf)); y = np.zeros(2 * rows)
        base = (B[:, 0] + B[:, 1] + B[:, -2] + B[:, -1])[:, None] * S[None]
        for d in range(2):
            A[d * rows:(d + 1) * rows, 0] = B[:, 1] * T[d]
            A[d * rows:(d + 1) * rows, 1] = -B[:, -2] * T[d]
            A[d * rows:(d + 1) * rows, 2 + d * nf:2 + (d + 1) * nf] = B[:, 2:-2]
            y[d * rows:(d + 1) * rows] = R[:, d] - base[:, d]
        x, *_ = np.linalg.lstsq(A, y, rcond=None)
        a, b = x[0], x[1]
        C = np.vstack([S, S + a * T, np.c_[x[2:2 + nf], x[2 + nf:]], S - b * T, S])
        td = np.linspace(0, 1, 5000)
        D = clamped_basis(ncp, td) @ C
        idx = np.argmin(((R[:, None] - D[None]) ** 2).sum(-1), axis=1)
        t = td[idx]; t[0], t[-1] = 0, 1
        t = np.maximum.accumulate(t)
    dev = np.sqrt(((np.asarray(P)[:, None] - D[None]) ** 2).sum(-1)).min(1)
    return C, D, dev

"""Outline = exact straight front segment A-B + one open clamped cubic control-point curve from A around to B.
The curve's 2nd and 2nd-to-last control points lie on the line through A-B (G1 blend into the straight)."""
import numpy as np
import cpfit

# straight front edges in plan (mm), fitted to the rectified traces (residuals <= 0.3 mm outer, <= 0.5 mm inner)
OUTER_LINE = ((-28.0, -55.2), (62.0, -55.2))
INNER_LINE = ((-22.0, -43.15), (46.0, -43.15))


def path(P, idx_rest, A, B, m=400):
    R = np.vstack([A, P[idx_rest], B])
    s = np.r_[0, np.cumsum(np.linalg.norm(np.diff(R, axis=0), axis=1))]
    q = np.linspace(0, s[-1], m)
    return np.c_[np.interp(q, s, R[:, 0]), np.interp(q, s, R[:, 1])]


def fit_open(P, idx_rest, A, B, ncp, iters=10):
    A, B = np.asarray(A, float), np.asarray(B, float)
    T = (A - B) / np.linalg.norm(A - B)   # the trace runs along the front from B to A, i.e. direction T
    R = path(P, idx_rest, A, B)
    t = np.linspace(0, 1, len(R))
    for _ in range(iters):
        Bm = cpfit.clamped_basis(ncp, t)
        nf = ncp - 4; rows = len(R)
        M = np.zeros((2 * rows, 2 + 2 * nf)); y = np.zeros(2 * rows)
        base = Bm[:, :2].sum(1)[:, None] * A[None] + Bm[:, -2:].sum(1)[:, None] * B[None]
        for d in range(2):
            M[d*rows:(d+1)*rows, 0] = Bm[:, 1] * T[d]       # C1 = A + a T   (leaves A in the travel direction)
            M[d*rows:(d+1)*rows, 1] = -Bm[:, -2] * T[d]     # C_{n-1} = B - b T (arrives at B in direction T)
            M[d*rows:(d+1)*rows, 2+d*nf:2+(d+1)*nf] = Bm[:, 2:-2]
            y[d*rows:(d+1)*rows] = R[:, d] - base[:, d]
        x, *_ = np.linalg.lstsq(M, y, rcond=None)
        C = np.vstack([A, A + x[0] * T, np.c_[x[2:2+nf], x[2+nf:]], B - x[1] * T, B])
        td = np.linspace(0, 1, 5000)
        D = cpfit.clamped_basis(ncp, td) @ C
        idx = np.argmin(((R[:, None] - D[None]) ** 2).sum(-1), axis=1)
        t = np.maximum.accumulate(td[idx]); t[0], t[-1] = 0, 1
    full = np.vstack([D, np.linspace(B, A, 50)])
    dev = np.sqrt(((np.asarray(P)[:, None] - full[None]) ** 2).sum(-1)).min(1)
    return C, D, dev, x[:2]

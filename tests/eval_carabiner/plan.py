"""Rectify the traces to the plan (mm) and fit light periodic cubic B-splines (few control points)."""
import json, sys, os
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cam, trace as T

LENGTH_MM = 200.0          # size assumption: overall plan length of the large hoop
THICK_MM = 7.0             # from the ~20 px front edge face through the camera
ROT = np.radians(11.6357)  # rotate so the front edge runs along +X
DIST = 1.0
Rz = np.array([[np.cos(ROT), -np.sin(ROT)], [np.sin(ROT), np.cos(ROT)]])


def _raw(pts):
    return cam.backproject(pts, DIST)[:, :2] @ Rz.T


_o = _raw(T.OUTER)
SCALE = LENGTH_MM / (_o[:, 0].max() - _o[:, 0].min())
CENTER = (_o.max(0) + _o.min(0)) / 2


def to_plan(pts):
    return (_raw(pts) - CENTER) * SCALE


def plan_to_cam_world(xy, z=0.0):
    """Inverse: plan mm -> camera-frame world (units) for projecting model points into the photo."""
    xy = np.atleast_2d(xy) / SCALE + CENTER
    raw = xy @ Rz            # inverse rotation
    return np.c_[raw, np.full(len(raw), z / SCALE)]


def photo_camera_rhino():
    """Camera of the photo expressed in the plan frame (mm): location, target, up."""
    C = cam.cam_center(DIST)
    tgt = np.zeros(3)
    def f(p):
        p = np.asarray(p, float)
        xy = (Rz @ p[:2] - CENTER) * SCALE
        return [float(xy[0]), float(xy[1]), float(p[2] * SCALE)]
    up = cam.UP
    up_xy = Rz @ up[:2]
    return f(C), f(tgt), [float(up_xy[0]), float(up_xy[1]), float(up[2])]


def bspline_basis_periodic(n, deg, t):
    """Uniform periodic B-spline basis matrix (len(t) x n) for parameters t in [0,1)."""
    B = np.zeros((len(t), n))
    for k, tk in enumerate(t):
        x = tk * n
        i = int(np.floor(x)) % n
        u = x - np.floor(x)
        b = [(1 - u) ** 3 / 6, (3 * u**3 - 6 * u**2 + 4) / 6, (-3 * u**3 + 3 * u**2 + 3 * u + 1) / 6, u**3 / 6]
        for j in range(4):
            B[k, (i + j) % n] += b[j]
    return B


def resample(P, m=400):
    P = np.vstack([P, P[:1]])
    seg = np.linalg.norm(np.diff(P, axis=0), axis=1)
    s = np.r_[0, np.cumsum(seg)]
    q = np.linspace(0, s[-1], m, endpoint=False)
    return np.c_[np.interp(q, s, P[:, 0]), np.interp(q, s, P[:, 1])]


def fit_periodic(P, n, iters=8):
    """Least-squares periodic cubic fit with n control points + parameter correction."""
    Q = resample(P)
    t = np.linspace(0, 1, len(Q), endpoint=False)
    for _ in range(iters):
        B = bspline_basis_periodic(n, 3, t)
        C, *_ = np.linalg.lstsq(B, Q, rcond=None)
        # parameter correction: project each sample to the dense curve
        td = np.linspace(0, 1, 4000, endpoint=False)
        D = bspline_basis_periodic(n, 3, td) @ C
        idx = np.argmin(((Q[:, None, :] - D[None]) ** 2).sum(-1), axis=1)
        t = td[idx]
    D = bspline_basis_periodic(n, 3, np.linspace(0, 1, 4000, endpoint=False)) @ C
    dev = np.sqrt(((P[:, None, :] - D[None]) ** 2).sum(-1)).min(1)
    return C, dev


if __name__ == "__main__":
    O, I = to_plan(T.OUTER), to_plan(T.INNER)
    print("outer bbox", O.min(0).round(1), O.max(0).round(1))
    for n in (10, 12, 14):
        _, d = fit_periodic(O, n); print("outer n=%d max %.2f mean %.2f mm" % (n, d.max(), d.mean()))
    for n in (8, 10, 12):
        _, d = fit_periodic(I, n); print("inner n=%d max %.2f mean %.2f mm" % (n, d.max(), d.mean()))
    print("camera", photo_camera_rhino())

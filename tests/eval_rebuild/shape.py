"""Shape data for the two-element sculpture, derived from reference.png.

Outlines are traced in photo pixels (u, v). Each outline point gets a designed height z (mm) from
a smooth height field; the pixel is back-projected through the fitted photo camera onto the plane
Z = z, so the model reproduces the photo silhouette exactly from the photo viewpoint while the
heights (which a 70-degree top-down photo barely shows) are design estimates.

World frame: X = photo right, Y = away from the camera (into the photo), Z = up, millimetres.
Grid cell = 12.7 mm (half-inch mat: ruler numbers are two cells apart).
"""
import numpy as np
from scipy.interpolate import RBFInterpolator

import cam

CELL = 12.7
X0, Y0 = 11.0, 12.0  # cell offset -> world origin near the middle of the object
SITE = np.array([-3000.0, 0.0, 0.0])  # model placed away from the other eval layers (free space)


def to_world(p_cells):
    p = np.asarray(p_cells, float)
    return np.array([(p[0] - X0) * CELL, (p[1] - Y0) * CELL, p[2] * CELL]) + SITE


def to_cells(p_mm):
    q = np.asarray(p_mm, float) - SITE
    return np.array([q[0] / CELL + X0, q[1] / CELL + Y0, q[2] / CELL])


def bp(u, v, z_mm):
    """photo pixel + height (mm) -> world point (mm)."""
    return to_world(cam.backproject(u, v, z_mm / CELL))


def photo_of(p_mm):
    return cam.project(to_cells(p_mm))[0]


# ---------------------------------------------------------------- traces (photo px)
# Lower element A: hairpin band. Lower leg = blade (tip left) under the upper element, bends up on the
# right into the tri-cornered fin and returns left as the raised top band (free pointed tip).
A_OUT = [
    (173, 329), (220, 333), (270, 336), (320, 339), (370, 346), (420, 362), (460, 378), (500, 398),
    (525, 414), (540, 423),  # bottom tip on the mat
    (560, 410), (578, 398), (592, 388),  # right tip on the mat
    (585, 375), (574, 356), (560, 338), (545, 322), (525, 298), (510, 272), (502, 245), (500, 210),
    (503, 178),  # top-right corner
    (480, 172), (440, 172), (405, 175), (395, 184), (388, 194),  # top edge + notch
    (360, 203), (325, 215), (290, 228),  # top band free tip
    (330, 232), (370, 226), (410, 217), (440, 209), (457, 210),  # under side of top band / hole top
    (465, 225), (462, 246), (451, 263), (432, 276), (405, 285), (370, 290), (330, 292), (290, 292),
    (255, 294), (228, 300), (200, 313),  # lower leg inner edge back to the blade tip
]
# Upper element B: crescent fork. Two arms reach down to the mat on the left (concave C opening
# facing left), the body sweeps right over the lower element and tapers into a tip that lands in
# the opening of A.
B_OUT = [
    (10, 271),  # upper arm tip (on the mat)
    (30, 258), (55, 243), (80, 227), (105, 215), (130, 210), (160, 211), (200, 217), (250, 226),
    (300, 236), (350, 245), (400, 256), (445, 272),  # right tip (lands on the rim of A's opening)
    (395, 265), (350, 265), (300, 261), (260, 263), (225, 269), (195, 281), (172, 298), (158, 320),
    (150, 345), (146, 365), (143, 379),  # lower-right corner of the lower arm
    (120, 385), (80, 387), (40, 386), (12, 384),  # lower arm tip (on the mat)
    (40, 376), (75, 369), (100, 361), (115, 350), (124, 330), (128, 300), (126, 272), (118, 251),
    (102, 238), (85, 240), (60, 250), (35, 262),
]

# ---------------------------------------------------------------- designed heights (photo px -> mm)
A_H = [  # round 2: taller, front edge curls down to the mat, fin rises to ~68 mm
    ((173, 329), 10), ((220, 333), 8), ((320, 339), 6), ((420, 362), 4), ((500, 398), 2), ((540, 423), 0),
    ((592, 388), 0), ((330, 292), 34), ((255, 294), 30),
    ((432, 276), 40), ((545, 322), 20), ((510, 272), 45), ((500, 210), 60), ((503, 178), 66), ((440, 172), 68),
    ((392, 190), 66), ((290, 228), 58), ((457, 210), 62), ((463, 245), 52), ((500, 330), 30), ((480, 260), 50),
    ((560, 395), 4),
]
B_H = [
    ((10, 271), 0), ((12, 384), 0), ((55, 243), 25), ((105, 215), 52), ((160, 211), 64), ((250, 226), 70),
    ((350, 245), 66), ((445, 272), 47.2), ((300, 261), 60), ((195, 281), 52), ((158, 320), 36), ((146, 365), 16),
    ((143, 379), 10), ((80, 387), 3), ((100, 361), 8), ((124, 330), 24), ((128, 300), 34), ((118, 251), 38),
    ((100, 236), 36), ((200, 245), 70), ((160, 260), 60), ((40, 380), 1), ((30, 260), 7),
]


class Field:
    """Smooth height field z(x, y) (mm) built from photo-pixel height controls."""

    def __init__(self, controls, smoothing=8.0):
        pts, zs = [], []
        for (u, v), z in controls:
            p = bp(u, v, z)
            pts.append(p[:2])
            zs.append(z)
        self.rbf = RBFInterpolator(np.array(pts), np.array(zs, float), kernel="thin_plate_spline", smoothing=smoothing)

    def __call__(self, xy):
        return self.rbf(np.atleast_2d(xy))


def lift(trace, field):
    """Back-project each traced pixel onto the field: solve g(z) = field(bp(u, v, z)) - z = 0 (brentq;
    a plain fixed-point iteration oscillates where the field is steep)."""
    from scipy.optimize import brentq
    out = []
    for (u, v) in trace:
        g = lambda z: float(field(bp(u, v, z)[:2])[0]) - z
        zs = np.linspace(-20, 120, 141)
        gs = [g(z) for z in zs]
        roots = [brentq(g, zs[i], zs[i + 1]) for i in range(len(zs) - 1) if gs[i] * gs[i + 1] <= 0]
        z = min(roots, key=lambda r: abs(r - 30)) if roots else 0.0
        out.append(bp(u, v, z))
    return np.array(out)


def grid(field, outline, nx, ny, margin=12.0):
    lo = outline[:, :2].min(0) - margin
    hi = outline[:, :2].max(0) + margin
    xs = np.linspace(lo[0], hi[0], nx)
    ys = np.linspace(lo[1], hi[1], ny)
    rows = []
    for y in ys:
        row = []
        for x in xs:
            row.append([float(x), float(y), float(field([x, y])[0])])
        rows.append(row)
    return rows


FA = Field(A_H)
FB = Field(B_H)
A3 = lift(A_OUT, FA)
B3 = lift(B_OUT, FB)

if __name__ == "__main__":
    for n, P in (("A", A3), ("B", B3)):
        lo, hi = P.min(0), P.max(0)
        print(n, "bbox", lo.round(1), hi.round(1), "size", (hi - lo).round(1))
    allp = np.vstack([A3, B3])
    print("overall", allp.min(0).round(1), allp.max(0).round(1), (allp.max(0) - allp.min(0)).round(1))
    ga = np.array(grid(FA, A3, 16, 12))
    gb = np.array(grid(FB, B3, 16, 12))
    print("A grid z", ga[..., 2].min().round(1), ga[..., 2].max().round(1))
    print("B grid z", gb[..., 2].min().round(1), gb[..., 2].max().round(1))
    # reprojection self-check
    err = max(np.hypot(*(photo_of(p) - np.array(t))) for p, t in zip(A3, A_OUT))
    print("max reprojection err px", round(err, 3))


# ---------------------------------------------------------------- rib bands (round 4)
# Raised lips seen in the photo along B's concave lower-right edge, B's inner C edge and around
# A's opening. (outline, field, first index, last index)
RIBS = {
    "B_rib_lower_edge": ("B", 14, 23),
    "B_rib_inner_C": ("B", 30, 36),
    "A_rib_opening": ("A", 31, 43),
}


def rib_curves(name, inset=1.0, base_dz=-1.2, top_dz=8.0, n=16):
    """Base and top polylines (world mm) of a rib ribbon standing on the element's top sheet."""
    from scipy.interpolate import splprep, splev
    from matplotlib.path import Path
    el, i0, i1 = RIBS[name]
    trace = np.array(A_OUT if el == "A" else B_OUT, float)
    P3 = A3 if el == "A" else B3
    F = FA if el == "A" else FB
    seg = trace[i0:i1 + 1]
    tck, _ = splprep([seg[:, 0], seg[:, 1]], s=0, k=3)
    uv = np.array(splev(np.linspace(0, 1, n), tck)).T
    P = lift([tuple(p) for p in uv], F)
    poly = Path(P3[:, :2])
    # one consistent side per rib: per-point left normals, side chosen by majority vote (a per-point
    # inside test flips where the spline bulges past the coarse trace polygon -> zig-zag ribs)
    nrms = []
    for i in range(n):
        a, b = P[max(i - 1, 0), :2], P[min(i + 1, n - 1), :2]
        t = (b - a) / np.linalg.norm(b - a)
        nrms.append(np.array([-t[1], t[0]]))
    votes = sum(1 if poly.contains_point(P[i, :2] + nrms[i] * 1.0) else -1 for i in range(n))
    side = 1.0 if votes >= 0 else -1.0
    base, top = [], []
    for i in range(n):
        xy = P[i, :2] + side * nrms[i] * inset
        z = float(F(xy)[0])
        base.append([xy[0], xy[1], z + base_dz])
        top.append([xy[0], xy[1], z + top_dz])
    return np.array(base), np.array(top)

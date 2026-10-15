"""Vertical clearance between the two laminates where their plans overlap (python-side pre-check)."""
import numpy as np
from matplotlib.path import Path
import shape as s

LO, HI = -2.3, 4.0  # laminate extent relative to the field (bottom sheet down, top sheet up)
pa, pb = Path(s.A3[:, :2]), Path(s.B3[:, :2])
lo = np.minimum(s.A3.min(0), s.B3.min(0)); hi = np.maximum(s.A3.max(0), s.B3.max(0))
X, Y = np.meshgrid(np.linspace(lo[0], hi[0], 400), np.linspace(lo[1], hi[1], 200))
P = np.c_[X.ravel(), Y.ravel()]
both = pa.contains_points(P) & pb.contains_points(P)
print("overlap samples", int(both.sum()))
if both.sum():
    za, zb = s.FA(P[both]), s.FB(P[both])
    gap_b_over_a = (zb + LO) - (za + HI)
    gap_a_over_b = (za + LO) - (zb + HI)
    i = np.argmin(gap_b_over_a)
    print("B over A: min gap %.2f at %s; A over B: min gap %.2f" % (gap_b_over_a.min(), P[both][i].round(1), gap_a_over_b.min()))
    print("samples with B above A:", int((zb > za).sum()), "A above B:", int((za > zb).sum()))
print("B tip", s.B3[12].round(1), "inside A", pa.contains_point(s.B3[12][:2]), "A top there", round(float(s.FA(s.B3[12][:2])[0]) + HI, 1))
print("A z", s.A3[:, 2].min().round(1), s.A3[:, 2].max().round(1), "B z", s.B3[:, 2].min().round(1), s.B3[:, 2].max().round(1))

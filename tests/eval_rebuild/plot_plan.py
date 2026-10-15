"""Plot plan (XY) and front (XZ) of the traced 3D outlines -> PNG (argv[1])."""
import sys
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
import shape as s

fig, ax = plt.subplots(2, 1, figsize=(10, 8))
for P, c, n in ((s.A3, "tab:blue", "A lower"), (s.B3, "tab:red", "B upper")):
    Q = np.vstack([P, P[:1]])
    ax[0].plot(Q[:, 0], Q[:, 1], c, label=n)
    for i, p in enumerate(P):
        ax[0].text(p[0], p[1], "%d:%.0f" % (i, p[2]), fontsize=6, color=c)
    ax[1].plot(Q[:, 0], Q[:, 2], c)
ax[0].set_aspect("equal"); ax[0].grid(True); ax[0].legend(); ax[0].set_title("plan XY (labels idx:z)")
ax[1].set_aspect("equal"); ax[1].grid(True); ax[1].set_title("front XZ")
plt.tight_layout(); plt.savefig(sys.argv[1], dpi=110)

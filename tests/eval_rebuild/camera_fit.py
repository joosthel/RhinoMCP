"""Fit a pinhole camera to the cutting-mat grid in reference.png.

Grid lines were located from brightness peaks of the photo (rows/columns outside the object).
Grid pitch is unknown up front (1 cm or 1/2 inch); the fit is done in 'cell' units and the
result is reported for both pitches. Prints camera parameters and a JSON with the camera.
"""
import json
import numpy as np
from scipy.optimize import least_squares

W = H_IMG = 600.0
CX = CY = 300.0

# horizontal grid lines: (u where measured, v observed), index j counted from the bottom
H_LINES = {
    20: [575, 551, 528, 505, 483, 461, 440, 419, 398, None, None, None, None, None, None, None, None, None, 212, 195, 178, 162, 145],
    595: [574, 550, None, 504, 482, 460, 439, 418, 397, None, None, 337, 318, 299, 281, 263, 245, 227, 210, 193, 176],
}
# vertical grid lines: row v -> (u0, pitch) regular sequences
V_ROWS = {540: (45.0, 22.03, range(0, 25)), 40: (13.0, 17.53, range(0, 34))}


def rot(p):
    f, e, psi, roll = p[0], p[1], p[2], p[3]
    fwd = np.array([np.sin(psi) * np.cos(e), np.cos(psi) * np.cos(e), -np.sin(e)])
    r = np.array([np.cos(psi), -np.sin(psi), 0.0])
    up = np.cross(r, fwd)
    cr, sr = np.cos(roll), np.sin(roll)
    r2 = cr * r + sr * up
    up2 = -sr * r + cr * up
    return f, fwd, r2, up2


def project(p, P):
    f, fwd, r, up = rot(p)
    C = np.array([p[4], p[5], p[6]])
    d = P - C
    z = d @ fwd
    return np.stack([CX + f * (d @ r) / z, CY - f * (d @ up) / z], -1)


def line_v_at_u(p, Y, u):
    a, b = project(p, np.array([[-200.0, Y, 0], [200.0, Y, 0]]))
    t = (u - a[0]) / (b[0] - a[0])
    return a[1] + t * (b[1] - a[1])


def line_u_at_v(p, X, v):
    a, b = project(p, np.array([[X, -200.0, 0], [X, 200.0, 0]]))
    t = (v - a[1]) / (b[1] - a[1])
    return a[0] + t * (b[0] - a[0])


def residuals(p, koff):
    res = []
    for u, vs in H_LINES.items():
        for j, v in enumerate(vs):
            if v is not None:
                res.append(line_v_at_u(p, float(j), u) - v)
    for v, (u0, pitch, rng) in V_ROWS.items():
        off = 0 if v == 540 else koff
        for i in rng:
            uo = u0 + pitch * i
            if uo > 600:
                break
            res.append(line_u_at_v(p, float(i - off), v) - uo)
    return np.array(res)


best = None
for koff in range(-10, 11):
    p0 = [900.0, np.radians(65), 0.0, 0.0, 12.0, -10.0, 30.0]
    r = least_squares(residuals, p0, args=(koff,))
    if best is None or r.cost < best[1].cost:
        best = (koff, r)
koff, r = best
p = r.x
rms = float(np.sqrt(np.mean(r.fun ** 2)))
f, e, psi, roll, Xc, Yc, Hc = p
fov_h = 2 * np.degrees(np.arctan(300.0 / f))
print("koff", koff, "rms px", round(rms, 2))
print("f_px %.1f  elevation %.1f deg  yaw %.2f deg  roll %.2f deg  fov_h %.1f deg" % (f, np.degrees(e), np.degrees(psi), np.degrees(roll), fov_h))
print("camera (cells) X %.2f Y %.2f H %.2f" % (Xc, Yc, Hc))
out = {"f_px": f, "elev_deg": float(np.degrees(e)), "yaw_deg": float(np.degrees(psi)), "roll_deg": float(np.degrees(roll)),
       "cam_cells": [Xc, Yc, Hc], "rms_px": rms, "fov_h_deg": fov_h,
       "lens_35mm_equiv": 36.0 / 2 / np.tan(np.radians(fov_h / 2))}
json.dump(out, open(__file__.replace("camera_fit.py", "camera.json"), "w"), indent=1)
print(json.dumps(out, indent=1))

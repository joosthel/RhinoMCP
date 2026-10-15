"""Assumed photo camera and plane back-projection for the lower hoop.

The 'paper' is the white border of a print (its edges are parallel in the image), so it gives no
homography. The camera is therefore a pinhole with assumed parameters, chosen from cues on the hoop:
 - perspective is weak: the edge face at the front (~20 px) and the inner wall at the far gate (~23 px)
   have nearly the same apparent height -> long focal length (F_PX);
 - elevation ELEV: band width / edge-face ratio and the aspect of the engraved text (see report).
Image coords: u right, v down. Principal point = centre of the lower hoop (PP), roll 0.
Model frame (before the final plan rotation): Z up, image-up ~ +Y, image-right ~ +X.
"""
import numpy as np

W, H = 1200, 1500
PP = np.array([590.0, 1165.0])
F_PX = 2400.0
ELEV = np.radians(55.0)
DIST = 1.0   # camera distance in 'plan units' is set by the scale; projection is scale-free if DIST scales
fwd = np.array([0.0, np.cos(ELEV), -np.sin(ELEV)])
R = np.array([1.0, 0.0, 0.0])
UP = np.cross(R, fwd)


def cam_center(dist):
    return -dist * fwd       # target is the origin


def project(P, dist, z0=0.0):
    C = cam_center(dist)
    d = np.atleast_2d(P) - C
    z = d @ fwd
    return np.stack([PP[0] + F_PX * (d @ R) / z, PP[1] - F_PX * (d @ UP) / z], -1)


def backproject(uv, dist, z=0.0):
    C = cam_center(dist)
    uv = np.atleast_2d(np.asarray(uv, float))
    d = fwd[None] + ((uv[:, 0] - PP[0]) / F_PX)[:, None] * R[None] - ((uv[:, 1] - PP[1]) / F_PX)[:, None] * UP[None]
    t = (z - C[2]) / d[:, 2]
    return C[None] + t[:, None] * d

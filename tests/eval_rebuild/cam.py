"""Photo camera model (fitted by camera_fit.py) in grid-cell units: project / back-project."""
import json, os
import numpy as np

_c = json.load(open(os.path.join(os.path.dirname(__file__), "camera.json")))
F = _c["f_px"]
E, PSI, ROLL = np.radians(_c["elev_deg"]), np.radians(_c["yaw_deg"]), np.radians(_c["roll_deg"])
C = np.array(_c["cam_cells"])
fwd = np.array([np.sin(PSI) * np.cos(E), np.cos(PSI) * np.cos(E), -np.sin(E)])
_r = np.array([np.cos(PSI), -np.sin(PSI), 0.0])
_up = np.cross(_r, fwd)
R = np.cos(ROLL) * _r + np.sin(ROLL) * _up
UP = -np.sin(ROLL) * _r + np.cos(ROLL) * _up


def project(P):
    d = np.atleast_2d(P) - C
    z = d @ fwd
    return np.stack([300 + F * (d @ R) / z, 300 - F * (d @ UP) / z], -1)


def backproject(u, v, z=0.0):
    """Ray through pixel (u,v) intersected with plane Z=z (cells). Returns [X,Y,z]."""
    d = fwd + (u - 300) / F * R - (v - 300) / F * UP
    t = (z - C[2]) / d[2]
    return C + t * d

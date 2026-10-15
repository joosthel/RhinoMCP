"""Capture the model from the estimated photo camera and compare with the photo crop around the lower hoop.

usage: photo_cam.py <out_prefix> [lens_mm] [display_mode]
writes <out_prefix>_cap.png (model), <out_prefix>_blend.png (50/50 overlay) and <out_prefix>_sbs.png (photo | model).
"""
import os, sys
import numpy as np
from PIL import Image, ImageChops
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cam, plan
from mcpx import call

HERE = os.path.dirname(os.path.abspath(__file__))
REF = os.path.join(HERE, "..", "..", "docs", "eval", "carabiner", "reference.png")
CW, CH = 1100, 600                       # capture = photo crop size (1 px = 1 photo px)
LENS_DEFAULT = cam.F_PX / CH * 24.0      # Rhino: lens relative to a 24 mm frame on the short side (checked)


def photo_crop():
    x0, y0 = int(cam.PP[0] - CW / 2), int(cam.PP[1] - CH / 2)
    return Image.open(REF).convert("RGB").crop((x0, y0, x0 + CW, y0 + CH))


def capture(out_prefix, lens=LENS_DEFAULT, mode="Shaded", purpose="photo camera capture"):
    loc, tgt, _ = plan.photo_camera_rhino()
    loc[2] += plan.THICK_MM; tgt[2] += plan.THICK_MM          # traces were on the TOP face
    cap = out_prefix + "_cap.png"
    r = call("view_capture", {"view": {"location": loc, "target": tgt, "lens": lens}, "display_mode": mode,
                              "width": CW, "height": CH}, purpose, image_to=cap, maxlen=300)
    if not r["ok"]:
        return r
    m = Image.open(cap).convert("RGB")
    p = photo_crop()
    Image.blend(p, m, 0.5).save(out_prefix + "_blend.png")
    sbs = Image.new("RGB", (CW, CH * 2 + 10), "white"); sbs.paste(p, (0, 0)); sbs.paste(m, (0, CH + 10))
    sbs.save(out_prefix + "_sbs.png")
    return r


if __name__ == "__main__":
    capture(sys.argv[1], float(sys.argv[2]) if len(sys.argv) > 2 else LENS_DEFAULT,
            sys.argv[3] if len(sys.argv) > 3 else "Shaded")

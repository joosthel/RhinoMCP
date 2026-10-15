"""Project candidate plan curves (top face, z = THICK) back into the photo and draw them (local check)."""
import sys, os, json
import numpy as np
from PIL import Image, ImageDraw
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cam, plan, trace as T, select_pts as sp


def to_px(D):
    W = plan.plan_to_cam_world(D, 0.0)
    return cam.project(W, plan.DIST)


def draw(curves, out):
    im = Image.open(os.path.join(os.path.dirname(__file__), "../../docs/eval/carabiner/reference.png")).convert("RGB")
    d = ImageDraw.Draw(im)
    for D, col in curves:
        d.line([tuple(p) for p in to_px(D)], fill=col, width=2)
    im = im.crop((30, 870, 1150, 1470)); im = im.resize((im.width * 2 // 2, im.height * 2 // 2))
    im.save(out)


if __name__ == "__main__":
    O, I = plan.to_plan(T.OUTER), plan.to_plan(T.INNER)
    so, si = json.loads(sys.argv[2]), json.loads(sys.argv[3])
    draw([(sp.closed_spline(O[so]), (255, 0, 0)), (sp.closed_spline(I[si]), (0, 0, 255))], sys.argv[1])

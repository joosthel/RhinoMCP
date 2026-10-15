"""Plot plan traces (black) vs candidate curves (red) at 4 px/mm for checking."""
import sys, os, json
import numpy as np
from PIL import Image, ImageDraw
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import plan, trace as T, select_pts as sp


def draw(curves, out, k=4.0):
    im = Image.new("RGB", (int(210 * k), int(125 * k)), "white"); d = ImageDraw.Draw(im)
    f = lambda P: [(float((x + 105) * k), float((62 - y) * k)) for x, y in P]
    for P in (plan.to_plan(T.OUTER), plan.to_plan(T.INNER)):
        d.line(f(np.vstack([P, P[:1]])), fill="black", width=1)
        for p in f(P): d.ellipse([p[0]-2, p[1]-2, p[0]+2, p[1]+2], outline="black")
    for C, col in curves:
        d.line(f(C), fill=col, width=2)
    im.save(out)


if __name__ == "__main__":
    O, I = plan.to_plan(T.OUTER), plan.to_plan(T.INNER)
    so = json.loads(sys.argv[2]); si = json.loads(sys.argv[3])
    draw([(sp.closed_spline(O[so]), "red"), (sp.closed_spline(I[si]), "red")], sys.argv[1])

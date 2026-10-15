"""Overlay the pixel traces on the reference photo (check of the trace)."""
import sys
from PIL import Image, ImageDraw
import trace as T
im = Image.open("../../docs/eval/carabiner/reference.png").convert("RGB")
d = ImageDraw.Draw(im)
for pts, col in ((T.OUTER, (255, 0, 0)), (T.INNER, (0, 0, 255))):
    d.line(pts + [pts[0]], fill=col, width=2)
    for p in pts: d.ellipse([p[0]-3, p[1]-3, p[0]+3, p[1]+3], outline=col)
d.line(T.SPLIT_L, fill=(0, 160, 0), width=2); d.line(T.SPLIT_R, fill=(0, 160, 0), width=2)
im.crop((30, 870, 1150, 1470)).save(sys.argv[1])

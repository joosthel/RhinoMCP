"""Overlay a labelled pixel grid on regions of the reference photo (for tracing outlines)."""
import sys
from PIL import Image, ImageDraw

im = Image.open(sys.argv[1]).convert("RGB")
out = sys.argv[2]
x0, y0, x1, y1 = map(int, sys.argv[3:7])
k = 3
c = im.crop((x0, y0, x1, y1)).resize(((x1 - x0) * k, (y1 - y0) * k), Image.LANCZOS)
d = ImageDraw.Draw(c)
for x in range((x0 // 20 + 1) * 20, x1, 20):
    d.line([((x - x0) * k, 0), ((x - x0) * k, c.size[1])], fill=(255, 0, 0) if x % 100 == 0 else (255, 140, 0), width=1)
    d.text(((x - x0) * k + 2, 2), str(x), fill=(255, 255, 0))
for y in range((y0 // 20 + 1) * 20, y1, 20):
    d.line([(0, (y - y0) * k), (c.size[0], (y - y0) * k)], fill=(255, 0, 0) if y % 100 == 0 else (0, 160, 255), width=1)
    d.text((2, (y - y0) * k + 2), str(y), fill=(0, 255, 255))
c.save(out)

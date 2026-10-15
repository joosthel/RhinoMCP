"""Compare where Rhino drew the red reference traces (capture) with where they are in the photo crop."""
import sys, os
import numpy as np
from PIL import Image
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cam, trace as T
import photo_cam as pc
im = np.asarray(Image.open(sys.argv[1]).convert("RGB")).astype(int)
red = (im[:, :, 0] - im[:, :, 1] > 40) & (im[:, :, 0] - im[:, :, 2] > 40)
ys, xs = np.nonzero(red)
x0, y0 = cam.PP[0] - pc.CW / 2, cam.PP[1] - pc.CH / 2
P = np.array(T.OUTER + T.INNER, float) - [x0, y0]
print("red px", len(xs), "bbox", xs.min(), xs.max(), ys.min(), ys.max())
print("trace bbox", P[:, 0].min(), P[:, 0].max(), P[:, 1].min(), P[:, 1].max())

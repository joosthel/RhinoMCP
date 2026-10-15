"""Overlay a 600x600 Rhino capture on the reference photo: python3 overlay.py capture.png out.png [mode]
mode 'lines': capture's non-background pixels drawn in red over the photo; 'side': side by side; 'blend': 50/50."""
import sys
import numpy as np
from PIL import Image

ref = Image.open(sys.argv[3] if len(sys.argv) > 4 else __file__.replace("tests/eval_rebuild/overlay.py", "docs/eval/rebuild/reference.png")).convert("RGB")
cap = Image.open(sys.argv[1]).convert("RGB").resize(ref.size)
mode = sys.argv[3] if len(sys.argv) > 3 else "lines"
if mode == "side":
    out = Image.new("RGB", (ref.width * 2 + 10, ref.height), "white")
    out.paste(ref, (0, 0)); out.paste(cap, (ref.width + 10, 0))
elif mode == "blend":
    out = Image.blend(ref, cap, 0.5)
else:
    a = np.asarray(cap).astype(int)
    bg = np.median(a.reshape(-1, 3), 0)
    mask = np.abs(a - bg).sum(-1) > 60
    o = np.asarray(ref).copy()
    o[mask] = [255, 40, 40]
    out = Image.fromarray(o)
out.save(sys.argv[2])

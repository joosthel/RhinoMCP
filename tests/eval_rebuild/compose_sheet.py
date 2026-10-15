"""Compose a labelled 2x2 sheet (Top, Front, Right, photo cam) from individual captures.
view_capture_sheet always zooms to ALL objects, which is useless in a shared document."""
import sys
from PIL import Image, ImageDraw

d = sys.argv[1]
names = [("final_top.png", "Top"), ("final_front.png", "Front"), ("final_right.png", "Right"), ("final_photo_cam.png", "Photo camera")]
W, H = 640, 400
out = Image.new("RGB", (2 * W + 6, 2 * H + 6), (90, 90, 90))
for k, (f, label) in enumerate(names):
    im = Image.open(d + "/" + f).convert("RGB")
    im.thumbnail((W, H))
    tile = Image.new("RGB", (W, H), (255, 255, 255))
    tile.paste(im, ((W - im.width) // 2, (H - im.height) // 2))
    ImageDraw.Draw(tile).text((8, 6), label, fill=(200, 0, 0))
    out.paste(tile, ((k % 2) * (W + 6), (k // 2) * (H + 6)))
out.save(d + "/final_sheet_composed.png")

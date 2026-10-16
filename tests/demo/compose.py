"""Compose the demo video, GIF, hero image and stills from the raw captures made by record_demo.py.

    python3 tests/demo/compose.py [--work /tmp/rhinomcp_demo] [--out docs/media]

Quality rules implemented here:
  * model shots were captured at 1840 x 1035 (the largest 16:9 size inside the capture guard); every frame is scaled to
    1280 x 720 with Lanczos (the push-in uses a sub-pixel source box, so there is no stepping);
  * all text is drawn at 2x (2560 x 1440) and downscaled with Lanczos, so it is crisp;
  * drawings: the thin pen lines are dilated by ~2 px at capture size before the downscale, so they cannot vanish;
  * scene changes are 0.4 s dips through white (0.2 s out, 0.2 s in); captions only change at cuts, never crossfade;
  * turntable frames are played back 1:1 (fixed camera, no per-frame zoom).
"""
import argparse
import json
import math
import os
import shutil
import subprocess
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

FPS = 20
W, H = 1280, 720
SS = 2                                   # text supersampling
FFMPEG = "/opt/homebrew/bin/ffmpeg"
BG = (255, 255, 255)
INK = (28, 32, 38)
MUTED = (110, 117, 126)
ACCENT = (201, 113, 79)
HELV = "/System/Library/Fonts/HelveticaNeue.ttc"
MENLO = "/System/Library/Fonts/Menlo.ttc"


def font(path, size, index=0):
    return ImageFont.truetype(path, int(size * SS), index=index)


def wrap(draw, text, fnt, width):
    lines, cur = [], ""
    for word in text.split():
        t = (cur + " " + word).strip()
        if draw.textlength(t, font=fnt) <= width or not cur:
            cur = t
        else:
            lines.append(cur)
            cur = word
    if cur:
        lines.append(cur)
    return lines


def down(rgba):
    return rgba.resize((W, H), Image.LANCZOS)


# ---------------------------------------------------------------------------------------------------- overlays (RGBA, 1280 x 720)
def overlay_caption(tool_text, step=None, sub=None):
    """Bottom caption bar: monospace tool call (and an optional second line in the UI font)."""
    im = Image.new("RGBA", (W * SS, H * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    f = font(MENLO, 19)
    fs = font(HELV, 19)
    pad = 22 * SS
    lines = [(tool_text, f, INK)]
    if sub:
        lines.append((sub, fs, MUTED))
    lh = 30 * SS
    bar_h = lh * len(lines) + 2 * 14 * SS
    x0, x1 = 40 * SS, (W - 40) * SS
    y1 = (H - 34) * SS
    y0 = y1 - bar_h
    d.rounded_rectangle((x0, y0, x1, y1), radius=14 * SS, fill=(255, 255, 255, 236), outline=(222, 224, 228, 255), width=2)
    x = x0 + pad
    if step:
        d.rounded_rectangle((x0 + 12 * SS, y0 + 14 * SS, x0 + 12 * SS + 52 * SS, y0 + 14 * SS + 28 * SS), radius=8 * SS, fill=ACCENT + (255,))
        d.text((x0 + 12 * SS + 26 * SS, y0 + 14 * SS + 14 * SS), step, font=font(HELV, 17, 1), fill=(255, 255, 255, 255), anchor="mm")
        x = x0 + 12 * SS + 52 * SS + 16 * SS
    y = y0 + 14 * SS
    for t, ff, col in lines:
        d.text((x, y + lh // 2), t, font=ff, fill=col + (255,), anchor="lm")
        y += lh
    return down(im)


def overlay_title():
    im = Image.new("RGBA", (W * SS, H * SS), BG + (255,))
    d = ImageDraw.Draw(im)
    d.text((W * SS // 2, 175 * SS), "RhinoMCP", font=font(HELV, 84, 1), fill=INK + (255,), anchor="mm")
    d.text((W * SS // 2, 250 * SS), "AI agents inside Rhino 7", font=font(HELV, 44, 7), fill=MUTED + (255,), anchor="mm")
    prompt = ("Design a modular sideboard: 2 bays, white steel tubes with spherical connectors, flush navy panels, "
              "drawers and doors, oak top — structure first, then panels and details.")
    f = font(HELV, 31)
    maxw = 900 * SS
    lines = wrap(d, prompt, f, maxw)
    lh = 46 * SS
    bw = max(d.textlength(t, font=f) for t in lines) + 64 * SS
    bh = lh * len(lines) + 48 * SS
    x1 = (W - 120) * SS
    x0 = x1 - bw
    y0 = 330 * SS
    d.rounded_rectangle((x0, y0, x1, y0 + bh), radius=28 * SS, fill=(236, 240, 244, 255))
    d.polygon([(x1 - 40 * SS, y0 + bh - 2 * SS), (x1 - 4 * SS, y0 + bh + 26 * SS), (x1 - 12 * SS, y0 + bh - 2 * SS)], fill=(236, 240, 244, 255))
    d.text((x0, y0 - 24 * SS), "You", font=font(HELV, 19, 1), fill=MUTED + (255,), anchor="ls")
    y = y0 + 24 * SS + lh // 2
    for t in lines:
        d.text((x0 + 32 * SS, y), t, font=f, fill=INK + (255,), anchor="lm")
        y += lh
    return down(im)


def overlay_end():
    im = Image.new("RGBA", (W * SS, H * SS), BG + (255,))
    d = ImageDraw.Draw(im)
    d.text((W * SS // 2, 215 * SS), "RhinoMCP", font=font(HELV, 76, 1), fill=INK + (255,), anchor="mm")
    d.text((W * SS // 2, 325 * SS), "52 tools  ·  Rhino 7 for Mac  ·  Windows: untested — feedback welcome",
           font=font(HELV, 31), fill=INK + (255,), anchor="mm")
    d.rounded_rectangle((W * SS // 2 - 330 * SS, 390 * SS, W * SS // 2 + 330 * SS, 460 * SS), radius=14 * SS, fill=(236, 240, 244, 255))
    d.text((W * SS // 2, 425 * SS), "github.com/joosthel/RhinoMCP", font=font(MENLO, 29, 1), fill=ACCENT + (255,), anchor="mm")
    return down(im)


def overlay_label(text):
    """Small label in the top-left corner."""
    im = Image.new("RGBA", (W * SS, H * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    f = font(HELV, 22, 1)
    w = d.textlength(text, font=f) + 36 * SS
    d.rounded_rectangle((40 * SS, 34 * SS, 40 * SS + w, 34 * SS + 44 * SS), radius=22 * SS, fill=(255, 255, 255, 236), outline=(222, 224, 228, 255), width=2)
    d.text((40 * SS + 18 * SS, 34 * SS + 22 * SS), text, font=f, fill=INK + (255,), anchor="lm")
    return down(im)


# ---------------------------------------------------------------------------------------------------- image helpers
def ease(t):
    return t * t * (3 - 2 * t)


def load(path):
    return Image.open(path).convert("RGB")


def push_in(img, t, zoom=0.05, cx=0.5, cy=0.5):
    """Frame at time t in [0,1]: eased slow push-in using a float source box (Lanczos, sub-pixel)."""
    z = 1.0 + zoom * ease(t)
    w, h = img.width / z, img.height / z
    x0, y0 = (img.width - w) * cx, (img.height - h) * cy
    return img.resize((W, H), Image.LANCZOS, box=(x0, y0, x0 + w, y0 + h))


def thicken_drawing(img, size=5):
    """Pen lines are ~1 px at capture size: dilate the ink (and boost it) so that they survive the downscale."""
    g = img.convert("L")
    ink = ImageChops.invert(g).filter(ImageFilter.MaxFilter(size))
    ink = ink.point(lambda v: min(255, int(v * 1.5)))
    return ImageChops.invert(ink).convert("RGB")


def fit_model(frame, k=0.9, top=14):
    """Shrink a model frame and anchor it to the top, so nothing runs under the caption bar. The edges are feathered into the
    white background (the soft shadow does not reach the capture edge, so no seam is visible)."""
    sm = frame.resize((int(W * k), int(H * k)), Image.LANCZOS)
    mask = Image.new("L", sm.size, 0)
    ImageDraw.Draw(mask).rectangle((36, 36, sm.width - 37, sm.height - 37), fill=255)
    mask = mask.filter(ImageFilter.GaussianBlur(18))
    canvas = Image.new("RGB", (W, H), BG)
    canvas.paste(sm, ((W - sm.width) // 2, top), mask)
    return canvas


def ink_bbox(img, pad=24):
    g = ImageChops.invert(img.convert("L")).point(lambda v: 255 if v > 40 else 0)
    bb = g.getbbox()
    return (max(0, bb[0] - pad), max(0, bb[1] - pad), min(img.width, bb[2] + pad), min(img.height, bb[3] + pad))


def fit_drawing(img, box_w=1120, box_h=500, top=80, bb=None):
    """Crop a (thickened) drawing capture to its ink (bb: shared by all beats of a view) and fit it into the free area above the caption bar."""
    crop = img.crop(bb or ink_bbox(img))
    k = min(box_w / float(crop.width), box_h / float(crop.height))
    sm = crop.resize((int(round(crop.width * k)), int(round(crop.height * k))), Image.LANCZOS)
    canvas = Image.new("RGB", (W, H), BG)
    canvas.paste(sm, ((W - sm.width) // 2, top + (box_h - sm.height) // 2))
    return canvas


# ---- the A3 sheet: camera path over the 300 dpi render of the exported PDF (paper mm: x right, y up from the bottom-left corner)
PAD_MM = 100
SHEET_KEYS = [(210, 148.5, 560, 1.2),      # whole sheet
              (140, 224.5, 275, 1.4),      # front elevation, all of it
              (95, 222, 150, 1.7),         # elevation: left half, closer
              (185, 222, 150, 1.7),        # elevation: right half
              (345, 224, 258, 1.7),        # section A-A
              (118, 101, 262, 1.7),        # plan
              (320, 75, 270, 1.6)]         # notes and title block
SHEET_MOVE = 0.9


def sheet_seconds():
    return sum(k[3] for k in SHEET_KEYS) + SHEET_MOVE * (len(SHEET_KEYS) - 1)


def sheet_path():
    segs, t = [], 0.0
    for i, k in enumerate(SHEET_KEYS):
        segs.append(("hold", t, t + k[3], i))
        t += k[3]
        if i < len(SHEET_KEYS) - 1:
            segs.append(("move", t, t + SHEET_MOVE, i))
            t += SHEET_MOVE

    def state(i, s):                      # a hold drifts in by 4 % while it lasts
        cx, cy, w, _ = SHEET_KEYS[i]
        return cx, cy, w * (1 - 0.04 * s)

    def f(u):
        tt = min(max(u, 0.0), 1.0) * t
        for kind, a, b, i in segs:
            if tt <= b:
                s = (tt - a) / (b - a)
                if kind == "hold":
                    return state(i, s)
                e = ease(s)
                c0, c1 = state(i, 1.0), state(i + 1, 0.0)
                lw = math.log(c0[2]) + (math.log(c1[2]) - math.log(c0[2])) * e
                return c0[0] + (c1[0] - c0[0]) * e, c0[1] + (c1[1] - c0[1]) * e, math.exp(lw)
        return state(len(SHEET_KEYS) - 1, 1.0)
    return f


def sheet_canvas(sheet):
    kk = sheet.width / 420.0
    pad = int(PAD_MM * kk)
    cv = Image.new("RGB", (sheet.width + 2 * pad, sheet.height + 2 * pad), BG)
    cv.paste(sheet, (pad, pad))
    return cv


def sheet_frame(cv, st):
    cx, cy, w = st
    kk = (cv.width / (420.0 + 2 * PAD_MM))
    h = w * 9.0 / 16.0
    cyv = cy - 0.07 * h                                   # the content sits a little high, clear of the caption bar
    x0 = (cx - w / 2.0 + PAD_MM) * kk
    y0 = (297 + PAD_MM - (cyv + h / 2.0)) * kk
    return cv.resize((W, H), Image.LANCZOS, box=(x0, y0, x0 + w * kk, y0 + h * kk))


def fade(img, k):
    """k=1 -> img, k=0 -> white."""
    if k >= 1:
        return img
    return Image.blend(Image.new("RGB", img.size, BG), img, max(0.0, k))


def with_overlay(base, ov):
    out = base.convert("RGBA")
    out.alpha_composite(ov)
    return out.convert("RGB")


# ---------------------------------------------------------------------------------------------------- scenes
class Out(object):
    def __init__(self, d):
        self.dir = d
        shutil.rmtree(d, ignore_errors=True)
        os.makedirs(d)
        self.n = 0
        self.seg = {}

    def add(self, img):
        img.save(os.path.join(self.dir, "%05d.png" % self.n), compress_level=1)
        self.n += 1


def scene(out, seconds, make, fade_in=True, fade_out=True, edge=4, label=None):
    n = int(round(seconds * FPS))
    start = out.n
    for i in range(n):
        img = make(i, n)
        k = 1.0
        if fade_in and i < edge:
            k = (i + 1) / float(edge + 1)
        if fade_out and i >= n - edge:
            k = min(k, (n - i) / float(edge + 1))
        out.add(fade(img, k))
    if label:
        out.seg[label] = (start, out.n)


def build_all(work, outdir, frames_dir):
    meta = json.load(open(os.path.join(work, "meta.json")))
    out = Out(frames_dir)
    stills = {}

    # 1 title
    title = overlay_title().convert("RGB")
    scene(out, 2.5, lambda i, n: title, fade_in=False, label="title")

    # 2 build: tool-call captions, one capture per step, eased push-in
    steps = meta["build"]["steps"]
    holds = [1.8, 2.0, 2.2, 2.4, 2.4, 2.8]
    nums = meta["numbers"]
    bb = nums["measure"]["totals"]["bbox"]
    size = [int(round(bb["max"][i] - bb["min"][i])) for i in range(3)]
    val = nums["validate_summary"]["summary"]
    cap5 = "geometry_validate  →  %d objects valid, %d problems   ·   measure  →  %d × %d × %d mm" % (val["valid"], val["with_problems"], size[0], size[1], size[2])
    for st, hold in zip(steps, holds):
        src = load(os.path.join(work, "build", st["key"] + ".png"))
        txt = cap5 if st["key"] == "s6" else st["caption"].replace("O22", "\u00d822")
        ov = overlay_caption(txt, step="%s/%d" % (st["key"][1:], len(steps)))
        lab = overlay_label("Build the sideboard")
        scene(out, hold, lambda i, n, src=src, ov=ov, lab=lab, kk=(0.78 if st["key"] == "s6" else 0.9): with_overlay(with_overlay(fit_model(push_in(src, i / float(n - 1), 0.04, 0.5, 0.45), kk), lab), ov),
              fade_in=(st["key"] == "s1"), fade_out=(st["key"] == "s6"), label=st["key"])
        if st["key"] == "s5":
            stills["build"] = with_overlay(with_overlay(fit_model(push_in(src, 0.5, 0.04, 0.5, 0.45)), lab), ov)

    # 3 turntable (fixed camera and lens, frames played 1:1, eased in the recorder)
    nt = meta["turntable"]["frames"]
    ovt = overlay_caption("view_capture  display_mode:\"Rendered\"  ·  fixed camera & lens, %d frames orbit" % nt)
    labt = overlay_label("Turntable")
    tt = [fit_model(load(os.path.join(work, "turntable", "f%03d.png" % i)).resize((W, H), Image.LANCZOS), 0.84, 10) for i in range(nt)]

    def mk_t(i, n):
        return with_overlay(with_overlay(tt[i], labt), ovt)

    scene(out, nt / float(FPS), mk_t, label="turntable")
    stills["turntable"] = mk_t(int(nt * 0.55), nt)

    # 4 drawings: model, then every view appears in three beats (Make2D linework, dimensions, leaders and labels), then the A3 sheet
    lab = overlay_label("Dimensioned drawings")
    sub = "front elevation, plan and section A–A at true scale 1:10, dimensioned and annotated"
    src = load(os.path.join(work, "build", "s6.png"))
    ov = overlay_caption("drawing_clipping_plane  +  drawing_make2d  +  annotate  +  layout_create  +  export", sub=sub)
    scene(out, 1.6, lambda i, n: with_overlay(with_overlay(fit_model(push_in(src, i / float(n - 1), 0.03, 0.5, 0.45), 0.78), lab), ov), fade_out=True, label="model")
    d = meta["drawings"]
    an = meta["annot"]

    def cv(k):
        co = d[k]["report"]["curves_out"]
        return "%d curves, %d arcs" % (co["total"], co["arc"])

    beats = {
        "elev": [('drawing_make2d  view:"Front"  →  ' + cv("elev"), "every tube silhouette kept: elevation from the front-plane objects"),
                 ("annotate  linear dimensions, ticks, text 3.5 mm  →  %d" % an["elev"]["dims"], "overall, bays, chained heights, door and drawer, overhang, Ø22"),
                 ("annotate  leaders + text 2.5 mm  →  %d labels" % an["elev"]["labels"], "oak top 22 mm · steel tube Ø18 · spherical connector Ø26 · glider foot")],
        "plan": [('drawing_clipping_plane z:520  +  drawing_make2d  hatch:"Solid"  →  ' + cv("plan"), "plan cut just under the oak top"),
                 ("annotate  linear dimensions  →  %d   (bays 780, overall 1598 × 422)" % an["plan"]["dims"], "same dimension style, chained"),
                 ("annotate  leaders + section marker A–A  →  %d items" % an["plan"]["labels"], "cut line, arrows and letters; oak top shown dashed above the cut")],
        "section": [('drawing_make2d  section_line through bay 1  hatch:"Solid"  →  ' + cv("section"), "solid poché where tubes, panels and top are cut"),
                    ("annotate  linear dimensions  →  %d   (heights, depth, top 22 mm)" % an["section"]["dims"], "foot, frame, door, drawer front, top thickness"),
                    ("annotate  leaders  →  %d labels" % an["section"]["labels"], "panel 6 mm · steel tube Ø18 · door · drawer front · glider foot")],
    }
    holds = {"elev": [1.5, 1.8, 2.0], "plan": [1.4, 1.5, 1.8], "section": [1.4, 1.5, 1.8]}
    stills_key = {"elev": "elevation", "plan": "plan", "section": "section"}
    for key in ("elev", "plan", "section"):
        raw = [load(os.path.join(work, "drawing", "%s_%d.png" % (key, b))) for b in range(3)]
        lines = thicken_drawing(raw[0], 3)                                     # black linework: dilated so that it survives the downscale
        imgs = [ImageChops.darker(lines, r.filter(ImageFilter.MinFilter(3))) for r in raw]   # annotations keep their colours (dilated by 1 px)
        bb = ink_bbox(imgs[2])
        for b in range(3):
            frame = with_overlay(with_overlay(fit_drawing(imgs[b], bb=bb), lab), overlay_caption(beats[key][b][0], sub=beats[key][b][1]))
            scene(out, holds[key][b], lambda i, n, frame=frame: frame, fade_in=(b == 0), fade_out=(b == 2), label="%s%d" % (key, b))
            if b == 2:
                stills[stills_key[key]] = frame

    # the A3 sheet: the PDF rendered at 300 dpi (sips), a slow push-in and pan over the three views, the notes and the title block
    sheet = Image.open(meta["sheet"]["png"]).convert("RGB")
    ov = overlay_caption("layout_create + export  \u2192  dimensioned A3 sheet, exported to PDF & DXF")
    seq = sheet_path()
    pan = sheet_canvas(sheet)

    def mk_sheet(i, n):
        return with_overlay(sheet_frame(pan, seq(i / float(n - 1))), ov)

    scene(out, sheet_seconds(), mk_sheet, fade_in=True, fade_out=False, label="sheet")
    stills["sheet"] = mk_sheet(int(2.9 * FPS), int(sheet_seconds() * FPS))   # the whole elevation, seen from the first hold

    # 5 end card
    end = overlay_end().convert("RGB")
    scene(out, 2.5, lambda i, n: end, fade_out=False, label="end")

    # hero 1600 x 800: rendered sideboard (left) + the crisp sheet (elevation and plan with their dimensions, right)
    hero = Image.new("RGB", (1600, 800), BG)
    m = load(os.path.join(work, "build", "s6.png"))
    bw = m.width * 0.62
    left = m.resize((690, int(690 * m.height / bw)), Image.LANCZOS, box=(m.width * 0.15, 0, m.width * 0.15 + bw, m.height))
    hero.paste(left, (0, (800 - left.height) // 2))
    kk = sheet.width / 420.0
    mm = lambda x0, y0, x1, y1: sheet.crop((int(x0 * kk), int((297 - y1) * kk), int(x1 * kk), int((297 - y0) * kk)))
    top = mm(42, 162, 238, 282)                      # front elevation incl. all dimensions and labels
    bot = mm(230, 25, 410, 53)                       # the title block
    top = top.resize((890, int(top.height * 890 / float(top.width))), Image.LANCZOS)
    bot = bot.resize((890, int(bot.height * 890 / float(bot.width))), Image.LANCZOS)
    hero.paste(top, (700, 8))
    hero.paste(bot, (700, 8 + top.height + 14))
    dh = ImageDraw.Draw(hero)
    dh.line((694, 40, 694, 760), fill=(222, 224, 228), width=2)
    yb = 8 + top.height + 14 + bot.height
    dh.line((700, yb, 1590, yb), fill=(0, 0, 0), width=3)
    stills["hero"] = hero
    return out, stills, sheet


def encode(frames_dir, outdir, nframes):
    mp4 = os.path.join(outdir, "demo.mp4")
    subprocess.check_call([FFMPEG, "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", os.path.join(frames_dir, "%05d.png"),
                           "-c:v", "libx264", "-preset", "slow", "-crf", "19", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-r", str(FPS), mp4])
    return mp4


def gif_selection(out, gdir):
    """A shorter cut for the GIF (the full story is in the MP4): short excerpts of every scene, turntable at 2x speed."""
    shutil.rmtree(gdir, ignore_errors=True)
    os.makedirs(gdir)
    pick = []
    seg = out.seg
    def take(lbl, a, b, step=1):
        s0, e0 = seg[lbl]
        pick.extend(range(s0 + a, min(e0, s0 + b), step))
    take("title", 0, 24)
    for k in ("s1", "s2", "s3", "s4", "s5"):
        take(k, 0, 18)
    take("s6", 6, 40)
    take("turntable", 0, 120, 3)
    take("elev0", 0, 14)
    take("elev1", 0, 12)
    take("elev2", 0, 22)
    take("plan2", 0, 20)
    take("section2", 0, 20)
    take("sheet", 0, 210, 3)
    take("end", 0, 34)
    for i, f in enumerate(pick):
        os.symlink(os.path.join(out.dir, "%05d.png" % f), os.path.join(gdir, "%05d.png" % i))
    return len(pick)


def encode_gif(frames_dir, outdir, fps=12, width=960, start=0, end=None, name="demo.gif", dither="sierra2_4a"):
    pal = os.path.join(frames_dir, "_palette.png")
    sel = "fps=%d,scale=%d:-1:flags=lanczos" % (fps, width)
    inp = ["-framerate", str(FPS), "-i", os.path.join(frames_dir, "%05d.png")]
    subprocess.check_call([FFMPEG, "-y", "-loglevel", "error"] + inp + ["-vf", sel + ",palettegen=stats_mode=diff", pal])
    gif = os.path.join(outdir, name)
    subprocess.check_call([FFMPEG, "-y", "-loglevel", "error"] + inp + ["-i", pal, "-lavfi", sel + " [x]; [x][1:v] paletteuse=dither=%s:diff_mode=rectangle" % dither,
                                                                         "-loop", "0", gif])
    return gif


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--work", default="/tmp/rhinomcp_demo")
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "docs", "media"))
    ap.add_argument("--frames", default="/tmp/rhinomcp_demo/frames")
    ap.add_argument("--gif-fps", type=int, default=15)
    ap.add_argument("--gif-width", type=int, default=960)
    ap.add_argument("--dither", default="sierra2_4a")
    ap.add_argument("--skip-frames", action="store_true", help="re-encode from the existing frames dir")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    if not a.skip_frames:
        out, stills, sheet = build_all(a.work, a.out, a.frames)
        stills["hero"].save(os.path.join(a.out, "hero.png"), optimize=True)
        for k in ("build", "turntable", "elevation", "plan", "section", "sheet"):
            stills[k].save(os.path.join(a.out, "still-%s.png" % k), optimize=True)
        sheet.save(os.path.join(a.out, "sheet.png"), optimize=True)
        pdf = json.load(open(os.path.join(a.work, "meta.json")))["sheet"]["pdf"]
        if os.path.getsize(pdf) < 2 * 1024 * 1024:
            shutil.copy(pdf, os.path.join(a.out, "sheet.pdf"))
        print("seg", {k: v for k, v in out.seg.items() if k in ("sheet", "elev0", "end")})
        print("frames:", out.n, "= %.1f s" % (out.n / float(FPS)))
    print(encode(a.frames, a.out, 0))
    if not a.skip_frames:
        n = gif_selection(out, a.frames + "_gif")
        print("gif frames:", n, "= %.1f s at %d fps source" % (n / float(FPS), FPS))
    print(encode_gif(a.frames + "_gif", a.out, a.gif_fps, a.gif_width, dither=a.dither))
    for f in sorted(os.listdir(a.out)):
        print(f, os.path.getsize(os.path.join(a.out, f)))


if __name__ == "__main__":
    main()

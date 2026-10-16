"""Compose the demo: Pillow overlays on the captured frames, ffmpeg assembly (mp4, gif), hero and step stills."""
import glob, os, shutil, subprocess, sys

from PIL import Image, ImageChops, ImageDraw, ImageFont

FFMPEG = shutil.which("ffmpeg") or "/opt/homebrew/bin/ffmpeg"
FPS = 30
W, H = 1280, 720
INK, MUTED, ACCENT, BG = (24, 32, 44), (104, 116, 132), (31, 111, 178), (255, 255, 255)
SF = "/System/Library/Fonts/SFNS.ttf"
MONO = "/System/Library/Fonts/Menlo.ttc"
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REF = os.path.join(REPO, "docs", "eval", "carabiner", "reference.png")
END_LINE = "52 tools  ·  233 live tests  ·  Rhino 7 for Mac (Windows: untested — feedback welcome)"
PROMPT = "Model the carabiner hoop from this photo — basic shapes first, details after."

_fonts = {}


def font(size, weight="Regular", mono=False):
    key = (size, weight, mono)
    if key not in _fonts:
        if mono:
            f = ImageFont.truetype(MONO, size)
        else:
            f = ImageFont.truetype(SF, size)
            try:
                f.set_variation_by_name(weight)
            except Exception:
                pass
        _fonts[key] = f
    return _fonts[key]


class Frames:
    def __init__(self, d):
        self.d = d
        self.cache = {}

    def get(self, name):
        if name not in self.cache:
            self.cache = {k: v for k, v in list(self.cache.items())[-40:]}
            self.cache[name] = Image.open(os.path.join(self.d, name + ".png")).convert("RGB")
        return self.cache[name]


def rounded(d, box, r, fill, outline=None, width=1):
    d.rounded_rectangle(box, r, fill=fill, outline=outline, width=width)


def caption(img, tool, text, step=None):
    """Bottom caption: mono tool-call pill + one sentence."""
    img = img.copy()
    d = ImageDraw.Draw(img)
    size = 26
    while True:
        ft, fx = font(size, mono=True), font(size, "Medium")
        tw = d.textlength(tool, font=ft)
        if 48 + tw + 64 + d.textlength(text, font=fx) <= W - 40 or size <= 18:
            break
        size -= 1
    tw = d.textlength(tool, font=ft)
    x0, y0, y1 = 48, H - 104, H - 44
    rounded(d, (x0, y0, x0 + tw + 40, y1), 14, (22, 34, 50))
    d.text((x0 + 20, (y0 + y1) / 2), tool, font=ft, fill=(236, 244, 255), anchor="lm")
    d.text((x0 + tw + 64, (y0 + y1) / 2), text, font=fx, fill=INK, anchor="lm")
    if step:
        d.text((W - 48, 44), step, font=font(22, "Medium"), fill=MUTED, anchor="rm")
    return img


def header(img, text="RhinoMCP"):
    d = ImageDraw.Draw(img)
    d.text((48, 44), text, font=font(24, "Semibold"), fill=MUTED, anchor="lm")
    return img


def blend(a, b, t):
    return Image.blend(a, b, max(0.0, min(1.0, t)))


# ------------------------------------------------------------------------------------------------------ scenes
def title_scene(n):
    ref = Image.open(REF).convert("RGB").crop((30, 880, 1170, 1470))
    ref = ref.resize((420, int(420 * ref.height / ref.width)))
    ref_card = Image.new("RGB", (ref.width + 16, ref.height + 16), (255, 255, 255))
    ref_card.paste(ref, (8, 8))

    def fn(i):
        img = Image.new("RGB", (W, H), (247, 249, 252))
        d = ImageDraw.Draw(img)
        a = min(1.0, i / 12.0)
        c = lambda col: tuple(int(255 - (255 - v) * a) if False else int(247 + (v - 247) * a) for v in col)
        d.text((W / 2, 150), "RhinoMCP", font=font(92, "Bold"), fill=c(INK), anchor="mm")
        d.text((W / 2, 235), "AI agents inside Rhino 7", font=font(44, "Medium"), fill=c(ACCENT), anchor="mm")
        # chat bubble appears at 0.5 s, text types over ~1.6 s
        t0 = 15
        if i >= t0:
            k = min(1.0, (i - t0) / 6.0)
            bx0, by0, bx1, by1 = 120, 330, 1160, 520
            ox = int((1 - k) * 24)
            rounded(d, (bx0, by0 + ox, bx1, by1 + ox), 30, (226, 236, 249), (196, 214, 238), 2)
            d.ellipse((bx0 + 28, by0 + 28 + ox, bx0 + 84, by0 + 84 + ox), fill=ACCENT)
            d.text((bx0 + 56, by0 + 56 + ox), "J", font=font(30, "Bold"), fill=(255, 255, 255), anchor="mm")
            nchar = int(len(PROMPT) * min(1.0, max(0.0, (i - t0 - 6) / 48.0)))
            txt = PROMPT[:nchar]
            f = font(34, "Medium")
            lines, cur = [], ""
            for wd in PROMPT.split(" "):
                if d.textlength((cur + " " + wd).strip(), font=f) > 700:
                    lines.append(cur); cur = wd
                else:
                    cur = (cur + " " + wd).strip()
            lines.append(cur)
            shown, pos = [], 0
            for ln in lines:
                shown.append(txt[pos:pos + len(ln)]); pos += len(ln) + 1
            for j, ln in enumerate(shown):
                d.text((bx0 + 110, by0 + 50 + j * 50 + ox), ln, font=f, fill=INK)
            if nchar < len(PROMPT) and (i // 6) % 2 == 0:
                last = shown[-1] if shown[-1] else ""
                jj = max(j for j, s in enumerate(shown) if s or j == 0)
                d.text((bx0 + 110 + d.textlength(shown[jj], font=f) + 4, by0 + 50 + jj * 50 + ox), "|", font=f, fill=ACCENT)
            # reference photo crop
            if i >= t0 + 24:
                k2 = min(1.0, (i - t0 - 24) / 10.0)
                rx, ry = bx1 - ref_card.width - 20, by0 + 18 + ox
                small = ref_card.resize((int(ref_card.width * 0.5), int(ref_card.height * 0.5)))
                sx, sy = bx1 - small.width - 28, by0 + (by1 - by0 - small.height) // 2 + ox
                if k2 > 0:
                    img.paste(Image.blend(Image.new("RGB", small.size, (226, 236, 249)), small, k2), (sx, sy))
        d.text((W / 2, 640), "an MCP server for Rhino 7  ·  52 tools  ·  driven by an agent, filmed with view_capture", font=font(24), fill=MUTED, anchor="mm")
        return img, None
    return n, fn


def still_scene(n, name, tool, text, step, fr, scale=1.0, dy=0):
    def fn(i):
        base = fr.get(name)
        if scale != 1.0:
            s = base.resize((int(W * scale), int(H * scale)), Image.LANCZOS)
            base = Image.new("RGB", (W, H), BG)
            base.paste(s, ((W - s.width) // 2, 20 + dy))
        return base, (tool, text, step)
    return n, fn


def seq_scene(n, names, tool, text, step, fr, smooth=False):
    def fn(i):
        p = min(len(names) - 1.0, i * len(names) / float(n))
        j = int(p)
        base = fr.get(names[j])
        if smooth and j + 1 < len(names):
            base = blend(base, fr.get(names[j + 1]), p - j)
        return base, (tool, text, step)
    return n, fn


def orbit_scene(n, N, tool, text, step, fr):
    def fn(i):
        p = min(N - 1.0, i * (N - 1) / float(max(1, n - 1)))
        a = int(p)
        b = min(N - 1, a + 1)
        img = blend(fr.get("orbit_%03d" % a), fr.get("orbit_%03d" % b), p - a)
        return img, (tool, text, step)
    return n, fn


def end_scene(n):
    def fn(i):
        img = Image.new("RGB", (W, H), (247, 249, 252))
        d = ImageDraw.Draw(img)
        d.text((W / 2, 250), "RhinoMCP", font=font(96, "Bold"), fill=INK, anchor="mm")
        d.text((W / 2, 340), "AI agents inside Rhino 7", font=font(44, "Medium"), fill=ACCENT, anchor="mm")
        d.text((W / 2, 450), END_LINE, font=font(27, "Medium"), fill=INK, anchor="mm")
        f = font(30, mono=True)
        tw = d.textlength("github.com/joosthel/RhinoMCP", font=f)
        rounded(d, (W / 2 - tw / 2 - 28, 520, W / 2 + tw / 2 + 28, 584), 16, (22, 34, 50))
        d.text((W / 2, 552), "github.com/joosthel/RhinoMCP", font=f, fill=(236, 244, 255), anchor="mm")
        return img, None
    return n, fn


def timeline(fr):
    s = int(0.4 * FPS)   # crossfade length
    sc = []
    sc.append(title_scene(int(3.4 * FPS)))
    sc.append(still_scene(int(2.3 * FPS), "s1_curves", "geometry_create  curve", "Basic shape first: outline curves, straight front edge", "1 / 5", fr))
    sc.append(still_scene(int(2.3 * FPS), "s2_solid", "geometry_boolean  difference", "Extrude 7 mm, subtract the opening: one solid", "2 / 5", fr))
    sc.append(still_scene(int(2.0 * FPS), "s3_fillet", "geometry_edit  fillet_edges", "Details: round the edges, r = 2 mm", "3 / 5", fr))
    sc.append(seq_scene(int(2.6 * FPS), ["s4_gate"] * 4 + ["s4_gate_zoom"] * 8, "geometry_boolean  difference", "Gate split (0.4 mm slots) and latch notches", "4 / 5", fr))
    push = ["s5_engraved"] * 5 + ["push_%02d" % i for i in range(14)] + ["push_13"] * 5
    sc.append(seq_scene(int(3.6 * FPS), push, "geometry_edit  engrave", "Engrave HELIOT EMIL, 0.3 mm deep", "5 / 5", fr, smooth=True))
    sc.append(orbit_scene(int(8 * FPS), 96, "view_capture", "Turntable: 96 captures, camera stepped around a fixed target", "", fr))
    sc.append(still_scene(int(2.6 * FPS), "dr_plan", "drawing_make2d  Top", "Plan view as clean linework on ISO layers, true scale", "", fr, 0.8, 0))
    sc.append(still_scene(int(3.4 * FPS), "dr_cut", "drawing_make2d  clipping plane z=3.5", "clean Make2D: lines & arcs, hatched sections, true scale", "", fr, 0.8, 0))
    sc.append(end_scene(int(3.2 * FPS)))
    out, t = [], 0
    for k, (n, fn) in enumerate(sc):
        out.append({"start": t, "n": n + (s if k < len(sc) - 1 else 0), "fn": fn, "xf": s if k > 0 else 0})
        t += n
    return out, t


def render_video(fr, outdir):
    scenes, total = timeline(fr)
    os.makedirs(outdir, exist_ok=True)
    for g in range(total):
        act = [sc for sc in scenes if sc["start"] <= g < sc["start"] + sc["n"]]
        imgs = [(sc, sc["fn"](max(0, min(sc["n"] - 1, g - sc["start"])))) for sc in act]
        if len(imgs) == 2:
            nxt = imgs[1][0]
            a = (g - nxt["start"]) / float(max(1, nxt["xf"]))
            (b0, c0), (b1, c1) = imgs[0][1], imgs[1][1]
            img, cp = blend(b0, b1, a), (c0 if a < 0.5 else c1)
        else:
            img, cp = imgs[-1][1]
        if cp:
            img = caption(header(img.copy()), *cp)
        img.save(os.path.join(outdir, "%05d.png" % g))
    return total


def autocrop(img, pad=0, thr=10):
    bg = Image.new("RGB", img.size, img.getpixel((2, 2)))
    bb = ImageChops.difference(img, bg).convert("L").point(lambda v: 255 if v > thr else 0).getbbox()
    bb = (max(0, bb[0] - pad), max(0, bb[1] - pad), min(img.width, bb[2] + pad), min(img.height, bb[3] + pad))
    return img.crop(bb)


def hero(fr, path):
    HW, HH = 1600, 800
    img = Image.new("RGB", (HW, HH), (244, 247, 251))
    d = ImageDraw.Draw(img)
    d.text((HW / 2, 78), "RhinoMCP — AI agents inside Rhino 7", font=font(54, "Bold"), fill=INK, anchor="mm")
    cards = [("Photo → model: 137 MCP calls, valid solids", fr.get("orbit_000")), ("Clean section at true scale (drawing_make2d)", fr.get("dr_cut"))]
    cw, ch, y = 740, 560, 140
    for k, (label, src) in enumerate(cards):
        x = 50 + k * (cw + 20)
        rounded(d, (x, y, x + cw, y + ch), 26, (255, 255, 255), (222, 229, 238), 2)
        im = autocrop(src, 10 if k == 0 else 4, 5 if k == 0 else 10)
        s = min((cw - 70) / im.width, (ch - 120) / im.height)
        im = im.resize((int(im.width * s), int(im.height * s)), Image.LANCZOS)
        pos = (x + (cw - im.width) // 2, y + 28 + (ch - 110 - im.height) // 2)
        img.paste(ImageChops.multiply(img.crop((pos[0], pos[1], pos[0] + im.width, pos[1] + im.height)), im), pos)
        d.text((x + cw / 2, y + ch - 42), label, font=font(26, "Medium"), fill=MUTED, anchor="mm")
    d.text((HW / 2, 740), END_LINE, font=font(27, "Medium"), fill=INK, anchor="mm")
    img.save(path, optimize=True)


def stills(fr, media):
    sets = [("step_1_outline", "s1_curves", "geometry_create  curve", "Basic shape first: outline curves, straight front edge", "1 / 5", 1.0),
            ("step_2_rounded", "s3_fillet", "geometry_edit  fillet_edges", "Details: round the edges, r = 2 mm", "3 / 5", 1.0),
            ("step_3_engrave", "push_13", "geometry_edit  engrave", "Engrave HELIOT EMIL, 0.3 mm deep", "5 / 5", 1.0),
            ("step_4_section", "dr_cut", "drawing_make2d  +  clipping plane", "Hatched section, true scale", "", 0.8)]
    for out, name, tool, text, step, sc in sets:
        base = fr.get(name)
        if sc != 1.0:
            s = base.resize((int(W * sc), int(H * sc)), Image.LANCZOS)
            base = Image.new("RGB", (W, H), BG)
            base.paste(s, ((W - s.width) // 2, 20))
        caption(header(base.copy()), tool, text, step).save(os.path.join(media, out + ".png"), optimize=True)


def sh(cmd):
    print(" ".join(cmd))
    subprocess.check_call(cmd)


def main(frames_dir, media):
    os.makedirs(media, exist_ok=True)
    fr = Frames(frames_dir)
    out = os.path.join(frames_dir, "out")
    shutil.rmtree(out, ignore_errors=True)
    total = render_video(fr, out)
    print("frames:", total, "= %.1f s" % (total / float(FPS)))
    mp4 = os.path.join(media, "demo.mp4")
    sh([FFMPEG, "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", out + "/%05d.png", "-c:v", "libx264", "-preset", "slow",
        "-crf", "23", "-pix_fmt", "yuv420p", "-movflags", "+faststart", mp4])
    gif = os.path.join(media, "demo.gif")
    pal = os.path.join(frames_dir, "pal.png")
    gfps, gw = int(os.environ.get("GIF_FPS", 12)), int(os.environ.get("GIF_W", 800))
    vf = "fps=%d,scale=%d:-1:flags=lanczos" % (gfps, gw)
    sh([FFMPEG, "-y", "-loglevel", "error", "-i", mp4, "-vf", vf + ",palettegen=max_colors=96:stats_mode=diff", pal])
    sh([FFMPEG, "-y", "-loglevel", "error", "-i", mp4, "-i", pal, "-lavfi", vf + "[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle",
        "-loop", "0", gif])
    hero(fr, os.path.join(media, "hero.png"))
    stills(fr, media)
    for f in ("demo.mp4", "demo.gif", "hero.png"):
        print(f, round(os.path.getsize(os.path.join(media, f)) / 1e6, 2), "MB")
    shutil.rmtree(out, ignore_errors=True)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])

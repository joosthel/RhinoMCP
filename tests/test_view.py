import base64
import hashlib
import math
import struct
import unittest
import zlib

import mcp_client as c

TEST_VIEW = "_mcp_test_view"
ROOT = "_mcp_test::viewfix"
LA = ROOT + "::a"
LB = ROOT + "::b::deep"


def png_ink_bbox(img):
    """(width, height, (x0, y0, x1, y1)) of the pixels that differ from the corner (background) colour."""
    raw = base64.b64decode(img["data"])
    pos, idat = 8, b""
    while pos < len(raw):
        n, t = struct.unpack(">I4s", raw[pos:pos + 8])
        d = raw[pos + 8:pos + 8 + n]
        if t == b"IHDR":
            w, h, _, ct = struct.unpack(">IIBB", d[:10])
        elif t == b"IDAT":
            idat += d
        pos += 12 + n
    bpp = {0: 1, 2: 3, 4: 2, 6: 4}[ct]
    data, stride, prev, rows, p = zlib.decompress(idat), w * bpp, bytearray(w * bpp), [], 0
    for _ in range(h):
        f, line = data[p], bytearray(data[p + 1:p + 1 + stride])
        p += 1 + stride
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0
            b = prev[i]
            cc = prev[i - bpp] if i >= bpp else 0
            if f == 1:
                line[i] = (line[i] + a) & 255
            elif f == 2:
                line[i] = (line[i] + b) & 255
            elif f == 3:
                line[i] = (line[i] + (a + b) // 2) & 255
            elif f == 4:
                pa, pb, pc = abs(b - cc), abs(a - cc), abs(a + b - 2 * cc)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else (b if pb <= pc else cc))) & 255
        rows.append(line)
        prev = line
    bg = rows[0][0:3]
    x0, y0, x1, y1 = w, h, -1, -1
    for y in range(h):
        for x in range(w):
            px = rows[y][x * bpp:x * bpp + 3]
            if abs(px[0] - bg[0]) + abs(px[1] - bg[1]) + abs(px[2] - bg[2]) > 40:
                x0, x1, y0, y1 = min(x0, x), max(x1, x), min(y0, y), max(y1, y)
    return w, h, (x0, y0, x1, y1)


def md5(img):
    return hashlib.md5(base64.b64decode(img["data"])).hexdigest()


def image_info(img):
    """(mime, width, height) from the magic bytes of an MCP image block."""
    raw = base64.b64decode(img["data"])
    if raw[:8] == b"\x89PNG\r\n\x1a\n":
        w, h = struct.unpack(">II", raw[16:24])
        return "image/png", w, h
    if raw[:3] == b"\xff\xd8\xff":
        i = 2
        while i + 9 < len(raw):
            if raw[i] != 0xFF:
                i += 1
                continue
            m = raw[i + 1]
            if 0xC0 <= m <= 0xCF and m not in (0xC4, 0xC8, 0xCC):
                h, w = struct.unpack(">HH", raw[i + 5:i + 9])
                return "image/jpeg", w, h
            i += 2 + struct.unpack(">H", raw[i + 2:i + 4])[0]
    raise AssertionError("not a PNG/JPEG: %r" % raw[:8])


class ViewTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.names = [t["name"] for t in c.rpc("tools/list")["tools"]]
        for n in ("view_capture", "view_capture_sheet", "view_set", "views_named"):
            assert n in cls.names, n

    def tearDown(self):
        c.call("views_named", {"action": "delete", "name": TEST_VIEW})

    def active(self):
        r = c.call("view_capture", {"width": 64, "height": 64})
        self.assertTrue(r["ok"], r)
        d = r["data"]
        return d["camera"], d["display_mode"], d["view"]

    def test_capture_default_size(self):
        r = c.call("view_capture", {})
        self.assertTrue(r["ok"], r)
        self.assertEqual(len(r["_images"]), 1)
        mime, w, h = image_info(r["_images"][0])
        self.assertEqual(mime, "image/png")
        self.assertEqual((w, h), (768, 512))
        self.assertIn("camera", r["data"])
        self.assertIn("scene", r["data"])

    def test_capture_png_custom_size_and_mode(self):
        r = c.call("view_capture", {"format": "png", "width": 320, "height": 200, "display_mode": "Wireframe",
                                    "view": "Top", "zoom_to": "all"})
        self.assertTrue(r["ok"], r)
        mime, w, h = image_info(r["_images"][0])
        self.assertEqual((mime, w, h), ("image/png", 320, 200))
        self.assertEqual(r["data"]["display_mode"], "Wireframe")
        self.assertEqual(r["data"]["view"], "Top")

    def test_capture_restores_view(self):
        before = self.active()
        r = c.call("view_capture", {"view": "Front", "display_mode": "Wireframe", "zoom_to": "all",
                                    "width": 200, "height": 120, "show_grid": True})
        self.assertTrue(r["ok"], r)
        r = c.call("view_capture", {"view": {"location": [30, -30, 20], "target": [0, 0, 0], "lens": 35},
                                    "width": 200, "height": 120})
        self.assertTrue(r["ok"], r)
        after = self.active()
        self.assertEqual(before, after)

    def test_capture_restores_selection(self):
        sel_before = c.call("context_get")["data"]["selected"]
        r = c.call("view_capture", {"highlight": {"query": {"type": "Curve"}}, "width": 100, "height": 80})
        self.assertTrue(r["ok"], r)
        self.assertEqual(c.call("context_get")["data"]["selected"], sel_before)

    def test_capture_bad_args(self):
        r = c.call("view_capture", {"display_mode": "Nope", "width": 5000, "format": "gif"})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")
        r = c.call("view_capture", {"view": "no_such_view_xyz"})
        self.assertFalse(r["ok"])

    def test_sheet_single_image(self):
        before = self.active()
        r = c.call("view_capture_sheet", {"width": 640, "height": 480})
        self.assertTrue(r["ok"], r)
        self.assertEqual(len(r["_images"]), 1)
        self.assertEqual(image_info(r["_images"][0]), ("image/png", 640, 480))
        self.assertEqual([p["panel"] for p in r["data"]["panels"]], ["Top", "Front", "Right", "Perspective"])
        self.assertEqual(before, self.active())

    def test_named_view_round_trip(self):
        r = c.call("views_named", {"action": "save", "name": TEST_VIEW})
        self.assertTrue(r["ok"], r)
        self.assertIn(TEST_VIEW, r["data"]["named_views"])
        saved = self.active()
        c.call("view_set", {"projection": "Top", "zoom_to": "all"})
        self.assertNotEqual(self.active()[0], saved[0])
        name_before = c.call("context_get")["data"]["active_view"]
        r = c.call("views_named", {"action": "restore", "name": TEST_VIEW})
        self.assertTrue(r["ok"], r)
        self.assertEqual(c.call("context_get")["data"]["active_view"], name_before)
        self.assertEqual(self.active()[0]["location"], saved[0]["location"])
        self.assertEqual(self.active()[0]["projection"], saved[0]["projection"])
        # capture by named view name
        r = c.call("view_capture", {"view": TEST_VIEW, "width": 100, "height": 80})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["view"], TEST_VIEW)
        r = c.call("views_named", {"action": "list"})
        self.assertIn(TEST_VIEW, r["data"]["named_views"])
        r = c.call("views_named", {"action": "delete", "name": TEST_VIEW})
        self.assertTrue(r["ok"], r)
        self.assertNotIn(TEST_VIEW, c.call("views_named", {"action": "list"})["data"]["named_views"])
        r = c.call("views_named", {"action": "restore", "name": TEST_VIEW})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "NOT_FOUND")

    def test_view_set_validation(self):
        r = c.call("view_set", {})
        self.assertFalse(r["ok"])
        r = c.call("view_set", {"projection": "Sideways"})
        self.assertFalse(r["ok"])


class ViewFixTests(unittest.TestCase):
    """Display modes, camera, tight zoom, isolate and restore. Scene: box A (layer a) and sphere B (layer b::deep)."""

    @classmethod
    def setUpClass(cls):
        cls.wipe()
        r = c.call("geometry_create", {"items": [{"kind": "box", "min": [0, 0, 0], "max": [100, 40, 10]}], "layer": LA})
        assert r["ok"], r
        cls.a = r["data"]["items"][0]["handle"]
        r = c.call("geometry_create", {"items": [{"kind": "sphere", "center": [150, 20, 20], "radius": 25}], "layer": LB})
        assert r["ok"], r
        cls.b = r["data"]["items"][0]["handle"]

    @classmethod
    def tearDownClass(cls):
        cls.wipe()

    @staticmethod
    def wipe():
        c.call("objects_delete", {"target": {"query": {"layer": ROOT, "include_sublayers": True}}, "confirm_count": 1000})
        c.call("layers_manage", {"ops": [{"op": "delete", "path": ROOT, "recursive": True, "confirm_objects": 0}]})

    def cap(self, **kw):
        args = {"width": 400, "height": 300, "isolate": [self.a, self.b]}
        args.update(kw)
        args = {k: v for k, v in args.items() if v is not None}
        r = c.call("view_capture", args)
        self.assertTrue(r["ok"], r)
        return r

    def test_display_modes_render_differently(self):
        hashes = {}
        for m in ("Shaded", "Wireframe", "Ghosted"):
            r = self.cap(display_mode=m, view="Perspective", zoom_to="all")
            self.assertEqual(r["data"]["display_mode"], m)
            hashes[m] = md5(r["_images"][0])
        self.assertEqual(len(set(hashes.values())), 3, hashes)
        # same request again, in another order: same bytes (no stale frame from the previous mode)
        for m in ("Ghosted", "Shaded", "Wireframe"):
            r = self.cap(display_mode=m, view="Perspective", zoom_to="all")
            self.assertEqual(md5(r["_images"][0]), hashes[m], m)

    def test_display_mode_names_and_aliases(self):
        for name, expect in (("Arctic", "Arctic"), ("artic", "Arctic"), ("wireFRAME", "Wireframe"), ("xray", "X-Ray"),
                             ("X-Ray", "X-Ray"), ("rendered", "Rendered"), ("Technical", "Technical"), ("Pen", "Pen"),
                             ("ghosted", "Ghosted")):
            r = self.cap(display_mode=name, width=100, height=80)
            self.assertEqual(r["data"]["display_mode"], expect, name)
        r = c.call("view_capture", {"display_mode": "Nonsense"})
        self.assertFalse(r["ok"])
        msg = " ".join(r["error"]["details"]["errors"])
        for n in ("Wireframe", "Shaded", "Arctic", "Ghosted"):
            self.assertIn(n, msg)

    def test_fov_up_and_lens_convention(self):
        cam = {"location": [200, -300, 150], "target": [75, 20, 10]}
        r = self.cap(view=dict(cam, fov_deg=40, up=[0.3, 0, 1]), zoom_to="all")
        k = r["data"]["camera"]
        self.assertAlmostEqual(k["fov_deg"], 40, delta=0.1)
        self.assertAlmostEqual(k["lens_mm"], 12 / math.tan(math.radians(20)), delta=0.05)   # 24 mm frame on the short side
        n = math.sqrt(sum(x * x for x in k["direction"]))
        d = [x / n for x in k["direction"]]
        dot = 0.3 * d[0] + d[2]
        want = [0.3 - dot * d[0], 0 - dot * d[1], 1 - dot * d[2]]
        wn = math.sqrt(sum(x * x for x in want))
        got = sum(g * w / wn for g, w in zip(k["up"], want))
        self.assertGreater(got, 0.999, (k["up"], want))                 # roll applied
        r2 = c.call("view_capture", {"view": dict(cam, fov_deg=40, lens=50)})
        self.assertFalse(r2["ok"])
        r2 = c.call("view_capture", {"view": dict(cam, up=[0.0, 0.0, 0.0])})
        self.assertFalse(r2["ok"])

    def assert_fill(self, r, min_fill=0.8):
        f = r["data"]["framing"]
        self.assertGreaterEqual(max(f["fill_x"], f["fill_y"]), min_fill, f)
        self.assertLessEqual(max(f["fill_x"], f["fill_y"]), 1.0, f)
        w, h, bb = png_ink_bbox(r["_images"][0])      # the metadata matches the real pixels
        px = max((bb[2] - bb[0] + 1) / float(w), (bb[3] - bb[1] + 1) / float(h))
        self.assertGreaterEqual(px, min_fill - 0.03, (bb, w, h))
        self.assertAlmostEqual(px, max(f["fill_x"], f["fill_y"]), delta=0.04)

    def test_tight_zoom_parallel_and_perspective(self):
        for view in ("Top", "Front", "Perspective"):
            r = self.cap(view=view, zoom_to=self.a, isolate=self.a, display_mode="Wireframe")
            self.assert_fill(r)
        r = self.cap(view="Top", zoom_to=self.a, isolate=self.a, display_mode="Wireframe", width=200, height=500)
        self.assert_fill(r)
        r = self.cap(view="Top", zoom_to=self.a, isolate=self.a, display_mode="Wireframe", margin=0.2, width=300, height=300)
        f = r["data"]["framing"]
        self.assertLess(max(f["fill_x"], f["fill_y"]), 0.65)
        self.assertGreater(max(f["fill_x"], f["fill_y"]), 0.55)

    def test_zoom_to_nested_layer_query(self):
        q = {"query": {"layer": ROOT, "include_sublayers": True}}
        r = self.cap(view="Top", zoom_to=q, display_mode="Wireframe")
        self.assert_fill(r)
        w, h, bb = png_ink_bbox(r["_images"][0])
        self.assertGreater(bb[2] - bb[0], 0.7 * w)            # both objects (box and sphere) are in the frame
        r = self.cap(view="Top", zoom_to={"query": {"layer": ROOT + "::b", "include_sublayers": True}}, isolate=self.b, display_mode="Wireframe")
        self.assert_fill(r)

    def test_isolate_and_only_layers_hide_others(self):
        box = {"min": [-5, -5, 0], "max": [180, 45, 50]}
        kw = dict(view="Top", zoom_to=box, display_mode="Wireframe", width=600, height=300)
        r = self.cap(isolate=self.a, **kw)
        w, h, bb = png_ink_bbox(r["_images"][0])
        self.assertLess(bb[2], 0.7 * w)                        # only the box (left part)
        self.assertEqual(r["data"]["scene"]["visible_objects"], 1)
        r = self.cap(only_layers=ROOT + "::b", isolate=None, **kw)    # parent of the nested layer: sublayers are included
        w, h, bb = png_ink_bbox(r["_images"][0])
        self.assertGreater(bb[0], 0.55 * w)                    # only the sphere (right part)
        r = self.cap(only_layers=[LA, "deep"], isolate=None, **kw)
        w, h, bb = png_ink_bbox(r["_images"][0])
        self.assertLess(bb[0], 0.2 * w)
        self.assertGreater(bb[2], 0.8 * w)                     # both layers (full path and leaf name)
        r = c.call("view_capture", {"only_layers": "no_such_layer_xyz"})
        self.assertFalse(r["ok"])

    def test_capture_leaves_everything_as_it_was(self):
        def snap():
            ctx = c.call("context_get")["data"]
            k = c.call("view_capture", {"width": 32, "height": 32})["data"]
            lay = c.call("layers_list", {})["data"]
            vis = c.call("objects_query", {"filters": {"layer": ROOT, "include_sublayers": True, "visible": True}, "limit": 10})["data"]
            return (ctx.get("active_view"), k["camera"], k["display_mode"], lay, vis["total"])
        before = snap()
        self.cap(view={"location": [200, -300, 150], "target": [75, 20, 10], "fov_deg": 30, "up": [0.5, 0, 1]}, display_mode="Arctic",
                 zoom_to=self.a, isolate=None, only_layers=LA, show_grid=True)
        self.cap(view="Top", display_mode="Wireframe", isolate=self.b, zoom_to="all")
        r = c.call("view_capture_sheet", {"display_mode": "Ghosted", "isolate": self.a, "width": 320, "height": 240})
        self.assertTrue(r["ok"], r)
        self.assertEqual(snap(), before)
        r = c.call("view_capture", {"only_layers": LA, "view": {"location": [0, 0, 0], "target": [0, 0, 0]}})   # error path
        self.assertFalse(r["ok"])
        self.assertEqual(snap(), before)

    def test_sheet_zoom_isolate_margin_labels(self):
        args = {"width": 800, "height": 600, "isolate": self.a, "zoom_to": self.a, "margin": 0.02, "display_mode": "Shaded"}
        r = c.call("view_capture_sheet", args)
        self.assertTrue(r["ok"], r)
        d = r["data"]
        self.assertTrue(d["labels_drawn"])
        self.assertEqual(d["scene"]["visible_objects"], 1)
        self.assertEqual([p["panel"] for p in d["panels"]], ["Top", "Front", "Right", "Perspective"])
        for p in d["panels"]:
            self.assertGreaterEqual(max(p["framing"]["fill_x"], p["framing"]["fill_y"]), 0.9, p)
        r2 = c.call("view_capture_sheet", dict(args, labels=False))
        self.assertNotEqual(md5(r["_images"][0]), md5(r2["_images"][0]))      # labels really change the image
        r3 = c.call("view_capture_sheet", {"width": 800, "height": 600, "only_layers": LB, "display_mode": "Wireframe"})
        self.assertTrue(r3["ok"], r3)
        self.assertEqual(r3["data"]["scene"]["visible_objects"], 1)


if __name__ == "__main__":
    unittest.main()

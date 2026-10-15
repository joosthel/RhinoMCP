import base64
import struct
import unittest

import mcp_client as c

TEST_VIEW = "_mcp_test_view"


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


if __name__ == "__main__":
    unittest.main()

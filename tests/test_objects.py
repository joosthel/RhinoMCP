import unittest

import mcp_client as c

LAYER = "_mcp_test::objects"
SUB = "_mcp_test::objects::sub"


def cmd(text):
    r = c.call("run_command", {"command": text})
    assert r["ok"] and r["data"]["success"], r
    return r["data"]["added"]


class ObjectsTests(unittest.TestCase):
    def setUp(self):
        self.created = []
        self.sets = []
        cmd("-_Layer _New %s _Enter" % LAYER)
        cmd("-_Layer _New %s _Enter" % SUB)
        self.box1 = self.make("-_Box 1000,0,0 1010,10,0 5")      # 10x10x5 -> volume 500, area 400
        self.box2 = self.make("-_Box 1100,0,0 1110,10,0 5")
        self.line1 = self.make("-_Line 1000,50,0 1010,50,0")
        self.line2 = self.make("-_Line 1000,60,0 1010,60,0")
        self.point = self.make("-_Point 1005,5,2")
        self.sub_line = self.make("-_Line 1200,0,0 1210,0,0", layer=SUB)

    def tearDown(self):
        for s in self.sets:
            c.call("sets_delete", {"name": s})
        if self.created:
            c.call("objects_delete", {"target": [h for h in self.created if self._exists(h)]})
        for layer in (SUB, LAYER):
            try:
                cmd("-_Layer _Delete %s _Enter" % layer)
            except AssertionError:
                pass

    def _exists(self, h):
        return c.call("objects_get", {"target": h})["ok"]

    def make(self, command, layer=LAYER):
        added = cmd(command)
        self.assertEqual(len(added), 1, added)
        h = added[0] if isinstance(added[0], str) else added[0]["handle"]
        self.created.append(h)
        r = c.call("objects_set_props", {"target": h, "layer": layer})
        self.assertTrue(r["ok"], r)
        return h

    def q(self, **kw):
        r = c.call("objects_query", kw)
        self.assertTrue(r["ok"], r)
        return r

    # --- objects_query ---

    def test_counts_groups_and_rows(self):
        r = self.q(filters={"layer": LAYER})
        d = r["data"]
        self.assertEqual(d["total"], 5)
        groups = {g["key"]: g["count"] for g in d["groups"]}
        self.assertEqual(groups.get("line", groups.get("curve")), 2)
        self.assertEqual(groups["point"], 1)
        self.assertEqual(len(d["rows"]), 5)
        self.assertTrue(r["units"])
        self.assertIsNone(r["next_cursor"])

    def test_limit_zero_and_paging(self):
        d = self.q(filters={"layer": LAYER}, limit=0)["data"]
        self.assertNotIn("rows", d)
        self.assertEqual(d["total"], 5)
        r1 = self.q(filters={"layer": LAYER}, limit=2)
        self.assertEqual(len(r1["data"]["rows"]), 2)
        self.assertIsNotNone(r1["next_cursor"])
        seen = [x["handle"] for x in r1["data"]["rows"]]
        cur = r1["next_cursor"]
        while cur:
            r = self.q(filters={"layer": LAYER}, limit=2, cursor=cur)
            seen += [x["handle"] for x in r["data"]["rows"]]
            cur = r["next_cursor"]
        self.assertEqual(len(seen), 5)
        self.assertEqual(len(set(seen)), 5)

    def test_sublayers_flag(self):
        self.assertEqual(self.q(filters={"layer": LAYER}, limit=0)["data"]["total"], 5)
        d = self.q(filters={"layer": LAYER, "include_sublayers": True}, limit=0)["data"]
        self.assertEqual(d["total"], 6)
        d = self.q(filters={"layer": "_mcp_test", "include_sublayers": True, "type": "point"}, group_by="layer", limit=0)["data"]
        self.assertEqual(d["total"], 1)

    def test_type_filter(self):
        d = self.q(filters={"layer": LAYER, "type": "point"}, limit=0)["data"]
        self.assertEqual(d["total"], 1)
        d = self.q(filters={"layer": LAYER, "type": ["curve", "point"]}, limit=0)["data"]
        self.assertEqual(d["total"], 3)
        r = c.call("objects_query", {"filters": {"type": "banana"}})
        self.assertFalse(r["ok"])
        self.assertIn("Valid:", r["error"]["message"])

    def test_name_and_user_text(self):
        c.call("objects_set_props", {"target": self.box1, "name": "wall_A", "user_text": {"set": {"role": "wall", "level": "L1"}}})
        c.call("objects_set_props", {"target": self.box2, "name": "wall_B", "user_text": {"set": {"role": "slab"}}})
        self.assertEqual(self.q(filters={"layer": LAYER, "name": "wall_*"}, limit=0)["data"]["total"], 2)
        self.assertEqual(self.q(filters={"layer": LAYER, "name": "wall_?"}, limit=0)["data"]["total"], 2)
        self.assertEqual(self.q(filters={"layer": LAYER, "name": "nothing"}, limit=0)["data"]["total"], 0)
        self.assertEqual(self.q(filters={"layer": LAYER, "user_text": {"key": "role"}}, limit=0)["data"]["total"], 2)
        self.assertEqual(self.q(filters={"layer": LAYER, "user_text": {"key": "role", "value": "wall"}}, limit=0)["data"]["total"], 1)
        d = self.q(filters={"layer": LAYER}, group_by="user_text:role", limit=0)["data"]
        groups = {g["key"]: g["count"] for g in d["groups"]}
        self.assertEqual(groups["wall"], 1)
        self.assertEqual(groups["slab"], 1)
        self.assertEqual(groups["(none)"], 3)
        rows = self.q(filters={"layer": LAYER, "name": "wall_A"}, fields=["handle", "user_text", "bbox"])["data"]["rows"]
        self.assertEqual(rows[0]["user_text"]["level"], "L1")
        self.assertEqual(rows[0]["bbox"]["min"][0], 1000)

    def test_bbox_modes(self):
        f = {"layer": LAYER, "bbox": {"min": [995, -5, -1], "max": [1015, 15, 10], "mode": "inside"}}
        self.assertEqual(self.q(filters=f, limit=0)["data"]["total"], 2)   # box1 + point (lines are at y=50/60)
        f = {"layer": LAYER, "bbox": {"min": [1005, 55, -1], "max": [1006, 65, 1]}}
        self.assertEqual(self.q(filters=f, limit=0)["data"]["total"], 1)   # intersects line2 only
        f = {"layer": LAYER, "bbox": {"min": [1005, 5, 1], "max": [1006, 6, 3]}}
        self.assertEqual(self.q(filters=f, limit=0)["data"]["total"], 2)   # box1 + point

    def test_selected_and_visible(self):
        c.call("objects_select", {"target": self.box1, "clear_first": True})
        d = self.q(filters={"layer": LAYER, "selected": True}, limit=0)["data"]
        self.assertEqual(d["total"], 1)
        c.call("objects_set_props", {"target": self.line1, "visible": False})
        self.assertEqual(self.q(filters={"layer": LAYER, "visible": False}, limit=0)["data"]["total"], 1)
        self.assertEqual(self.q(filters={"layer": LAYER, "visible": True}, limit=0)["data"]["total"], 4)
        c.call("objects_select", {"target": self.box1, "action": "deselect"})

    def test_measure_sums(self):
        d = self.q(filters={"layer": LAYER, "type": ["brep", "extrusion", "surface"]}, group_by="layer", measure_sums=True, limit=0)["data"]
        g = d["groups"][0]
        self.assertEqual(g["count"], 2)
        self.assertAlmostEqual(g["volume_sum"], 1000, places=3)
        self.assertAlmostEqual(g["area_sum"], 800, places=3)
        d = self.q(filters={"layer": LAYER, "type": "curve"}, measure_sums=True, limit=0)["data"]
        self.assertAlmostEqual(d["groups"][0]["length_sum"], 20, places=3)
        # not computed unless asked
        d = self.q(filters={"layer": LAYER}, limit=0)["data"]
        self.assertNotIn("area_sum", d["groups"][0])

    def test_save_as_set_and_use(self):
        self.sets.append("t_curves")
        d = self.q(filters={"layer": LAYER, "type": "curve"}, save_as_set="t_curves", limit=0)["data"]
        self.assertEqual(d["saved_set"]["count"], 2)
        r = c.call("objects_get", {"target": {"set": "t_curves"}})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["count"], 2)

    def test_bad_args_collected(self):
        r = c.call("objects_query", {"limit": 9999, "group_by": "colour", "filters": {"layer": "no_such_layer", "bogus": 1}})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")
        msg = r["error"]["message"]
        self.assertIn("limit", msg)
        self.assertIn("group_by", msg)
        self.assertIn("bogus", msg)

    def test_unknown_layer_lists_layers(self):
        r = c.call("objects_query", {"filters": {"layer": "no_such_layer"}})
        self.assertFalse(r["ok"])
        self.assertIn(LAYER, r["error"]["message"])

    # --- objects_get ---

    def test_get_details(self):
        r = c.call("objects_get", {"target": [self.box1, self.line1, self.point], "include_guids": True})
        self.assertTrue(r["ok"], r)
        objs = {o["handle"]: o for o in r["data"]["objects"]}
        box = objs[self.box1]
        self.assertTrue(box["solid"])
        self.assertAlmostEqual(box["volume"], 500, places=3)
        self.assertAlmostEqual(box["area"], 400, places=3)
        self.assertTrue(box["valid"])
        self.assertEqual(box["layer"], LAYER)
        self.assertEqual(len(box["guid"]), 36)
        self.assertAlmostEqual(objs[self.line1]["length"], 10, places=3)
        self.assertIn("color", box)
        self.assertEqual(r["units"], c.call("context_get")["data"]["units"])

    def test_get_errors(self):
        r = c.call("objects_get", {"target": "o999999"})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")
        r = c.call("objects_get", {})
        self.assertFalse(r["ok"])

    # --- objects_set_props ---

    def test_set_props_all(self):
        r = c.call("objects_set_props", {"target": {"query": {"layer": LAYER, "type": "curve"}}, "name": "ln", "color": "#ff0000",
                                         "user_text": {"set": {"k": "v"}}, "locked": True})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["modified"], 2)
        o = c.call("objects_get", {"target": self.line1})["data"]["objects"][0]
        self.assertEqual(o["name"], "ln")
        self.assertEqual(o["color"], "#ff0000")
        self.assertEqual(o["color_source"], "ColorFromObject")
        self.assertEqual(o["user_text"], {"k": "v"})
        self.assertTrue(o["locked"])
        r = c.call("objects_set_props", {"target": self.line1, "color": "bylayer", "name": "", "locked": False,
                                         "user_text": {"remove": ["k"]}})
        self.assertTrue(r["ok"], r)
        o = c.call("objects_get", {"target": self.line1})["data"]["objects"][0]
        self.assertEqual(o["color_source"], "ColorFromLayer")
        self.assertEqual(o["name"], "")
        self.assertNotIn("user_text", o)
        self.assertFalse(o["locked"])
        r = c.call("objects_set_props", {"target": self.line1, "color": [0, 128, 255]})
        self.assertEqual(c.call("objects_get", {"target": self.line1})["data"]["objects"][0]["color"], "#0080ff")

    def test_set_props_layer_and_errors(self):
        r = c.call("objects_set_props", {"target": self.point, "layer": SUB})
        self.assertTrue(r["ok"], r)
        self.assertEqual(c.call("objects_get", {"target": self.point})["data"]["objects"][0]["layer"], SUB)
        r = c.call("objects_set_props", {"target": self.point, "layer": "no_such_layer", "color": "red"})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")
        self.assertIn("Existing layers", r["error"]["message"])
        self.assertIn("color", r["error"]["message"])      # all errors collected
        r = c.call("objects_set_props", {"target": self.point})
        self.assertFalse(r["ok"])
        self.assertIn("nothing to change", r["error"]["message"])

    def test_set_props_unchanged_count_and_undo(self):
        r = c.call("objects_set_props", {"target": self.line1, "layer": LAYER})
        self.assertTrue(r["ok"], r)
        r = c.call("objects_set_props", {"target": self.line1, "name": "undo_me"})
        self.assertEqual(r["data"]["modified"], 1)
        c.call("history_undo", {"steps": 1})
        self.assertEqual(c.call("objects_get", {"target": self.line1})["data"]["objects"][0]["name"], "")

    # --- objects_select ---

    def test_select(self):
        r = c.call("objects_select", {"target": [self.box1, self.box2], "clear_first": True})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["selected"], 2)
        self.assertEqual(r["data"]["selection_total"], 2)
        r = c.call("objects_select", {"target": self.box1, "action": "deselect"})
        self.assertEqual(r["data"]["selection_total"], 1)
        c.call("objects_set_props", {"target": self.line1, "visible": False})
        r = c.call("objects_select", {"target": self.line1})
        self.assertEqual(r["data"]["not_selectable"], 1)
        c.call("objects_select", {"target": {"query": {"layer": LAYER}}, "action": "deselect"})

    # --- objects_delete ---

    def test_delete_and_undo(self):
        r = c.call("objects_delete", {"target": {"query": {"layer": SUB}}})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["deleted"], 1)
        self.assertEqual(r["data"]["handles"], [self.sub_line])
        self.assertFalse(self._exists(self.sub_line))
        c.call("history_undo", {"steps": 1})
        self.assertTrue(self._exists(self.sub_line))

    def test_delete_locked_and_invalid(self):
        c.call("objects_set_props", {"target": self.line2, "locked": True})
        r = c.call("objects_delete", {"target": self.line2})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["deleted"], 1)
        r = c.call("objects_delete", {"target": self.line2})
        self.assertFalse(r["ok"])

    # --- sets ---

    def test_sets_lifecycle(self):
        self.sets += ["t_set", "t_set2"]
        r = c.call("sets_save", {"name": "t_set", "target": [self.box1, self.box2]})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["count"], 2)
        r = c.call("sets_save", {"name": "t_set", "target": self.line1, "mode": "add"})
        self.assertEqual(r["data"]["count"], 3)
        r = c.call("sets_save", {"name": "t_set", "target": self.box1, "mode": "remove"})
        self.assertEqual(r["data"]["count"], 2)
        r = c.call("sets_save", {"name": "nope_set", "target": self.box1, "mode": "add"})
        self.assertFalse(r["ok"])
        c.call("sets_save", {"name": "t_set2", "target": {"query": {"layer": LAYER, "type": "point"}}})
        r = c.call("sets_list")
        self.assertTrue(r["ok"], r)
        counts = {s["name"]: s["count"] for s in r["data"]["sets"]}
        self.assertEqual(counts["t_set"], 2)
        self.assertEqual(counts["t_set2"], 1)
        # use as target in other tools; stale members are dropped silently and reported by sets_list
        r = c.call("objects_set_props", {"target": {"set": "t_set"}, "name": "in_set"})
        self.assertEqual(r["data"]["modified"], 2)
        c.call("objects_delete", {"target": self.box2})
        self.created.remove(self.box2)
        counts = {s["name"]: s for s in c.call("sets_list")["data"]["sets"]}
        self.assertEqual(counts["t_set"]["count"], 1)
        self.assertEqual(counts["t_set"]["stale"], 1)
        r = c.call("sets_delete", {"name": "t_set"})
        self.assertTrue(r["ok"], r)
        r = c.call("sets_delete", {"name": "t_set"})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "NOT_FOUND")
        r = c.call("objects_get", {"target": {"set": "t_set"}})
        self.assertFalse(r["ok"])
        self.assertIn("t_set2", r["error"]["message"])


if __name__ == "__main__":
    unittest.main()

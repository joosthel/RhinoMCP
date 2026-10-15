"""Correctness/strictness fixes (fix/core-strictness).

Tools-DLL parts (objects_query filter composition, system_status live counts, hidden-object consistency, objects_query
strict arguments) run on any core. Core parts (schema validation in tools/call, server_info rss_mb/rosetta, the
ToolRegistry.InToolCall hook) are skipped unless server_info.version >= 0.2.0 (needs a Rhino restart after the core build).
"""
import unittest

import mcp_client as c

ROOT = "_mcp_test::strict"
LA = ROOT + "::A"
LSUB = ROOT + "::A::Sub"
LB = ROOT + "::B"
PFX = "zzs_"


def core_version():
    r = c.call("server_info", {})
    v = r["data"]["version"] if r.get("ok") else "0.0.0"
    return tuple(int(x) for x in v.split(".")[:3])


NEW_CORE = core_version() >= (0, 2, 0)
needs_core = unittest.skipUnless(NEW_CORE, "needs Core >= 0.2.0 (restart Rhino after building)")


def cmd(text):
    r = c.call("run_command", {"command": text})
    assert r["ok"] and r["data"]["success"], r
    return r["data"]["added"]


def hid(x):
    return x if isinstance(x, str) else x["handle"]


class Base(unittest.TestCase):
    def setUp(self):
        self.created = []
        for l in (LA, LSUB, LB):
            cmd("-_Layer _New %s _Enter" % l)
        self.t1 = self.make("-_Line 0,0,0 10,0,0", LA, PFX + "txt_1")
        self.t2 = self.make("-_Line 0,5,0 10,5,0", LA, PFX + "txt_2")
        self.o1 = self.make("-_Line 0,10,0 10,10,0", LA, PFX + "other")
        self.box = self.make("-_Box 20,0,0 30,10,0 5", LA, PFX + "txt_box")
        self.s1 = self.make("-_Line 0,20,0 10,20,0", LSUB, PFX + "txt_sub")
        self.b1 = self.make("-_Line 0,30,0 10,30,0", LB, PFX + "txt_3")

    def tearDown(self):
        c.call("objects_delete", {"target": {"query": {"layer": ROOT, "include_sublayers": True}}})
        for l in (LSUB, LA, LB, ROOT):
            try:
                cmd("-_Layer _Delete %s _Enter" % l)
            except AssertionError:
                pass

    def make(self, command, layer, name):
        added = cmd(command)
        self.assertEqual(len(added), 1, added)
        h = hid(added[0])
        r = c.call("objects_set_props", {"target": h, "layer": layer, "name": name})
        self.assertTrue(r["ok"], r)
        return h

    def q(self, **filters):
        r = c.call("objects_query", {"filters": filters, "limit": 100, "group_by": "layer"})
        self.assertTrue(r["ok"], r)
        return r["data"]

    def names(self, data):
        return sorted(row["name"] for row in data["rows"])


class QueryComposition(Base):
    def test_layer_and_name(self):
        d = self.q(layer=LA, name=PFX + "txt_*")
        self.assertEqual(self.names(d), [PFX + "txt_1", PFX + "txt_2", PFX + "txt_box"], d)
        self.assertTrue(all(r["layer"] == LA for r in d["rows"]), d)

    def test_layer_and_name_exact_and_question_mark(self):
        d = self.q(layer=LA, name=PFX + "txt_?")
        self.assertEqual(self.names(d), [PFX + "txt_1", PFX + "txt_2"], d)
        d = self.q(layer=LB, name=PFX + "other")
        self.assertEqual(d["total"], 0, d)

    def test_name_glob_alone_spans_layers(self):
        d = self.q(name=PFX + "txt_*")
        self.assertEqual(d["total"], 5, d)
        self.assertEqual(len({r["layer"] for r in d["rows"]}), 3, d)

    def test_layer_and_type(self):
        d = self.q(layer=LA, type="curve")
        self.assertEqual(sorted(r["name"] for r in d["rows"]), [PFX + "other", PFX + "txt_1", PFX + "txt_2"], d)
        d = self.q(layer=LA, type=["brep", "extrusion"])
        self.assertEqual(self.names(d), [PFX + "txt_box"], d)
        d = self.q(layer=LA, type=["curve", "brep", "extrusion"], name=PFX + "txt_*")
        self.assertEqual(d["total"], 3, d)

    def test_include_sublayers(self):
        d = self.q(layer=LA)
        self.assertEqual(d["total"], 4, d)
        self.assertNotIn(LSUB, {r["layer"] for r in d["rows"]})
        d = self.q(layer=LA, include_sublayers=True)
        self.assertEqual(d["total"], 5, d)
        d = self.q(layer=LA, include_sublayers=True, name=PFX + "txt_*")
        self.assertEqual(d["total"], 4, d)
        self.assertIn(LSUB, {r["layer"] for r in d["rows"]})

    def test_top_level_filter_keys_are_rejected(self):
        """The reported bug: layer/name passed beside (not inside) filters were ignored and everything matched."""
        r = c.call("objects_query", {"layer": LA, "name": PFX + "txt_*"})
        self.assertFalse(r["ok"], r)
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")
        self.assertIn("filters", r["error"]["message"])
        self.assertIn("'layer'", r["error"]["message"])
        self.assertIn("'name'", r["error"]["message"])      # all errors collected

    def test_unknown_filter_key(self):
        r = c.call("objects_query", {"filters": {"layr": LA}})
        self.assertFalse(r["ok"], r)
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")


class HiddenConsistency(Base):
    def hide(self, h):
        r = c.call("objects_set_props", {"target": h, "visible": False})
        self.assertTrue(r["ok"], r)

    def test_query_targets_include_hidden_by_default_everywhere(self):
        self.hide(self.t1)
        q = {"layer": LA, "name": PFX + "txt_*"}
        # measure (read) and layers_move_objects (mutate) must see the same 3 objects
        r = c.call("measure", {"target": {"query": q}, "group_by": "layer"})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"].get("count", r["data"].get("objects")), 3, r)
        r = c.call("layers_move_objects", {"target": {"query": q}, "layer": LB})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["moved"], 3, r)
        self.assertEqual(self.q(layer=LB, name=PFX + "txt_*")["total"], 4)

    def test_include_hidden_false_excludes_everywhere(self):
        self.hide(self.t1)
        q = {"layer": LA, "name": PFX + "txt_*", "include_hidden": False}
        r = c.call("layers_move_objects", {"target": {"query": q}, "layer": LB})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["moved"], 2, r)
        self.assertEqual(self.q(layer=LA, name=PFX + "txt_*", include_hidden=False)["total"], 0)
        self.assertEqual(self.q(layer=LA, name=PFX + "txt_*")["total"], 1)     # the hidden one stayed

    def test_objects_delete_and_move_agree(self):
        self.hide(self.o1)
        a = c.call("objects_query", {"filters": {"layer": LA}, "limit": 0})["data"]["total"]
        r = c.call("layers_move_objects", {"target": {"query": {"layer": LA}}, "layer": LB})
        self.assertEqual(r["data"]["moved"], a, r)


class SystemStatus(Base):
    def test_live_object_count_and_undo_held_deleted(self):
        c.call("objects_delete", {"target": self.b1})
        r = c.call("system_status", {"refresh": True})
        self.assertTrue(r["ok"], r)
        doc = r["data"]["document"]
        total = c.call("objects_query", {"limit": 0})["data"]["total"]
        if not doc["partial"]:
            self.assertEqual(doc["objects"], total, doc)
        self.assertIn("undo_held_deleted", doc)
        self.assertGreaterEqual(doc["undo_held_deleted"], 0)
        self.assertGreaterEqual(doc["undo_held_deleted"], 1, "the object deleted just now is held by undo")


@needs_core
class CoreStrictness(unittest.TestCase):
    def test_unknown_argument_rejected_and_nothing_executed(self):
        r = c.call("history_undo", {"count": 2})
        self.assertFalse(r["ok"], r)
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")
        self.assertIn("count", r["error"]["message"])
        self.assertIn("steps", r["error"]["guidance"])
        self.assertEqual(r["error"]["details"]["unknown"], ["count"])
        self.assertIn("steps", r["error"]["details"]["valid"])

    def test_did_you_mean(self):
        r = c.call("history_undo", {"stepz": 1})
        self.assertFalse(r["ok"])
        self.assertIn("did you mean 'steps'", r["error"]["message"])

    def test_wrong_type_and_all_errors_collected(self):
        r = c.call("history_undo", {"steps": "2", "bogus": True})
        self.assertFalse(r["ok"])
        errs = r["error"]["details"]["errors"]
        self.assertEqual(len(errs), 2, errs)
        self.assertTrue(any("steps" in e and "integer" in e for e in errs), errs)
        self.assertTrue(any("bogus" in e for e in errs), errs)

    def test_required_key(self):
        r = c.call("objects_set_props", {"layer": "x", "nope": 1})
        self.assertFalse(r["ok"])
        errs = r["error"]["details"]["errors"]
        self.assertTrue(any("'target' is required" in e for e in errs), errs)
        self.assertTrue(any("nope" in e for e in errs), errs)

    def test_valid_call_unaffected(self):
        r = c.call("objects_query", {"limit": 0})
        self.assertTrue(r["ok"], r)
        r = c.call("objects_query", {"limit": 0, "include_guids": None})   # null = absent
        self.assertTrue(r["ok"], r)

    def test_server_info_fields(self):
        d = c.call("server_info", {})["data"]
        self.assertGreaterEqual(d["version"].split("."), ["0", "2", "0"])
        self.assertGreater(d["rss_mb"], 100)
        self.assertIsInstance(d["rosetta"], bool)

    def test_agent_actor_via_hook(self):
        t = c.call("model_changes", {})["data"]["token"]
        cmd("-_Layer _New %s _Enter" % LB)
        h = hid(cmd("-_Point 1,2,3")[0])
        try:
            d = c.call("model_changes", {"since": t})["data"]
            self.assertEqual(d["by_actor"]["user"], 0, d)
            self.assertGreaterEqual(d["by_actor"]["agent"], 1, d)
        finally:
            c.call("objects_delete", {"target": h})
            try:
                cmd("-_Layer _Delete %s _Enter" % LB)
            except AssertionError:
                pass


if __name__ == "__main__":
    unittest.main()

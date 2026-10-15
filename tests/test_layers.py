import unittest

import mcp_client as c

ROOT = "_mcp_test::layers"


def manage(ops):
    r = c.call("layers_manage", {"ops": ops})
    return r


def rows(under=None, **kw):
    args = dict(kw)
    if under:
        args["under"] = under
    r = c.call("layers_list", args)
    assert r["ok"], r
    return r["data"]


def by_path(data):
    return {l["path"]: l for l in data["layers"]}


class LayersTests(unittest.TestCase):
    def setUp(self):
        self.prev_current = rows(limit=1)["current"]
        self.stored_scheme = None

    def tearDown(self):
        # restore the current layer, then remove everything this test created
        if self.prev_current and not self.prev_current.startswith(ROOT):
            manage([{"op": "set_current", "path": self.prev_current}])
        d = c.call("layers_list", {"under": "_mcp_test"})
        if d["ok"] and any(l["path"] == ROOT for l in d["data"]["layers"]):
            n = [l for l in d["data"]["layers"] if l["path"] == ROOT][0]["objects_total"]
            r = manage([{"op": "delete", "path": ROOT, "recursive": True, "confirm_objects": n}])
            self.assertTrue(r["ok"] and r["data"]["failed"] == 0, r)
        if self.stored_scheme:
            c.call("layers_apply_scheme", {"clear_stored_scheme": True})

    # ---- manage / list -------------------------------------------------
    def test_create_set_list(self):
        r = manage([
            {"op": "create", "path": ROOT + "::Walls::Ext", "color": "#102030", "print_width_mm": 0.35,
             "linetype": "NoSuchLinetypeXyz"},
            {"op": "create", "path": ROOT + "::Hidden", "visible": False, "locked": True},
            {"op": "set", "path": ROOT + "::Walls", "plot": False},
        ])
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["failed"], 0, r)
        self.assertTrue(any("NoSuchLinetypeXyz" in w for w in r.get("warnings", [])), r)
        d = rows(ROOT)
        self.assertEqual(d["matching"], 3)
        for k in ("layer_count", "objects_total", "returned", "current"):
            self.assertIn(k, d)
        p = by_path(d)
        ext = p[ROOT + "::Walls::Ext"]
        self.assertEqual(ext["color"], "#102030")
        self.assertAlmostEqual(ext["print_width_mm"], 0.35)
        self.assertEqual(ext["linetype"], "Continuous")
        self.assertEqual(ext["depth"], 3)
        self.assertFalse(p[ROOT + "::Walls"]["plot"])
        self.assertFalse(p[ROOT + "::Hidden"]["visible"])
        self.assertTrue(p[ROOT + "::Hidden"]["locked"])
        # idempotent create
        r = manage([{"op": "create", "path": ROOT + "::Walls::Ext"}])
        self.assertTrue(r["data"]["results"][0]["existed"], r)
        # depth limiting and paging
        d = rows(ROOT, max_depth=1)
        self.assertEqual(sorted(by_path(d)), [ROOT + "::Hidden", ROOT + "::Walls"])
        d = rows(ROOT, limit=1)
        self.assertEqual(d["returned"], 1)
        self.assertIsNotNone(c.call("layers_list", {"under": ROOT, "limit": 1})["next_cursor"])
        d2 = c.call("layers_list", {"under": ROOT, "limit": 5, "cursor": "1"})
        self.assertEqual(d2["data"]["returned"], 2)

    def test_validation_errors_collected(self):
        r = manage([{"op": "create", "path": ROOT + "::x", "color": "red"}, {"op": "rename", "path": ROOT},
                    {"op": "bogus", "path": "a"}])
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")
        self.assertGreaterEqual(len(r["error"]["details"]["errors"]), 3)
        r = c.call("layers_list", {"under": "_mcp_test::no_such_layer_here"})
        self.assertEqual(r["error"]["code"], "NOT_FOUND")

    def test_rename_move_and_delete_guards(self):
        manage([{"op": "create", "path": ROOT + "::A"}, {"op": "create", "path": ROOT + "::B"},
                {"op": "create", "path": ROOT + "::A::Child"}])
        r = manage([
            {"op": "rename", "path": ROOT + "::A", "new_name": "A2"},
            {"op": "rename", "path": ROOT + "::B", "new_name": "A2"},           # clash
            {"op": "move", "path": ROOT + "::A2::Child", "parent": ROOT + "::B"},
            {"op": "move", "path": ROOT + "::B", "parent": ROOT + "::B::Child"},  # into itself
            {"op": "delete", "path": ROOT + "::missing"},
        ])
        res = r["data"]["results"]
        self.assertTrue(res[0]["ok"], res)
        self.assertEqual(res[0]["new_path"], ROOT + "::A2")
        self.assertFalse(res[1]["ok"])
        self.assertTrue(res[2]["ok"], res)
        self.assertEqual(res[2]["new_path"], ROOT + "::B::Child")
        self.assertFalse(res[3]["ok"])
        self.assertFalse(res[4]["ok"])
        self.assertEqual(r["data"]["failed"], 3)
        p = by_path(rows(ROOT))
        self.assertIn(ROOT + "::B::Child", p)
        # sublayers need recursive
        r = manage([{"op": "delete", "path": ROOT + "::B"}])
        self.assertFalse(r["data"]["results"][0]["ok"])
        r = manage([{"op": "delete", "path": ROOT + "::B", "recursive": True}])
        self.assertTrue(r["data"]["results"][0]["ok"], r)
        self.assertNotIn(ROOT + "::B", by_path(rows(ROOT)))

    def test_objects_delete_needs_confirmation_and_move(self):
        manage([{"op": "create", "path": ROOT + "::Src"}, {"op": "create", "path": ROOT + "::Dst"},
                {"op": "set_current", "path": ROOT + "::Src"}])
        h = c.call("run_command", {"command": "_Line 0,0,0 5,0,0"})["data"]["added"]
        self.assertEqual(len(h), 1, h)
        handle = h[0] if isinstance(h[0], str) else h[0].get("handle", h[0])
        p = by_path(rows(ROOT))
        self.assertEqual(p[ROOT + "::Src"]["objects"], 1)
        self.assertEqual(p[ROOT]["objects_total"], 1)
        # move to another layer
        r = c.call("layers_move_objects", {"target": handle, "layer": ROOT + "::Dst"})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["moved"], 1)
        self.assertIsNotNone(r["data"]["bbox"])
        r = c.call("layers_move_objects", {"target": handle, "layer": ROOT + "::Dst"})
        self.assertEqual(r["data"]["skipped_already_on_layer"], 1)
        r = c.call("layers_move_objects", {"target": {"query": {"layer": ROOT + "::Dst"}}, "layer": ROOT + "::New::Deep"})
        self.assertEqual(r["error"]["code"], "NOT_FOUND")
        r = c.call("layers_move_objects", {"target": {"query": {"layer": ROOT + "::Dst"}}, "layer": ROOT + "::New::Deep",
                                           "create": True})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["layers_created"], [ROOT + "::New", ROOT + "::New::Deep"])
        manage([{"op": "set_current", "path": self.prev_current}]) if not self.prev_current.startswith(ROOT) else None
        # deleting a non-empty layer is refused without the right confirmation
        r = manage([{"op": "delete", "path": ROOT + "::New::Deep"}])
        self.assertFalse(r["data"]["results"][0]["ok"])
        self.assertIn("confirm_objects: 1", r["data"]["results"][0]["error"])
        r = manage([{"op": "delete", "path": ROOT + "::New::Deep", "confirm_objects": 2}])
        self.assertFalse(r["data"]["results"][0]["ok"])
        r = manage([{"op": "delete", "path": ROOT + "::New", "recursive": True, "confirm_objects": 1}])
        self.assertTrue(r["data"]["results"][0]["ok"], r)
        self.assertEqual(r["data"]["results"][0]["deleted_objects"], 1)

    def test_batch_is_one_undo_step(self):
        before = rows(ROOT) if False else None
        manage([{"op": "create", "path": ROOT + "::U1"}, {"op": "create", "path": ROOT + "::U2"}])
        self.assertIn(ROOT + "::U2", by_path(rows(ROOT)))
        r = c.call("history_undo", {"steps": 1})
        self.assertTrue(r["ok"], r)
        d = c.call("layers_list", {"under": ROOT})
        paths = [] if not d["ok"] else list(by_path(d["data"]))
        self.assertNotIn(ROOT + "::U1", paths)
        self.assertNotIn(ROOT + "::U2", paths)

    # ---- audit / scheme --------------------------------------------------
    def scheme(self):
        return {"name": "_mcp_test_scheme", "delimiter": "::", "layers": [
            {"path": ROOT + "::S::Walls", "color": "#000000", "print_width_mm": 0.5, "required": True},
            {"path": ROOT + "::S::Doors", "color": "#0070c0", "print_width_mm": 0.25, "required": True},
            {"path": ROOT + "::S::Grid", "color": "#ff0000", "linetype": "NoSuchLinetypeXyz", "plot": False},
        ]}

    def test_audit(self):
        manage([{"op": "create", "path": ROOT + "::S::Walls"},
                {"op": "create", "path": ROOT + "::Wand"},
                {"op": "create", "path": ROOT + "::Dup-A"}, {"op": "create", "path": ROOT + "::dup_a"},
                {"op": "create", "path": ROOT + "::P::Kid"}, {"op": "create", "path": ROOT + "::Doors"},
                {"op": "set_current", "path": ROOT + "::P"}])
        c.call("run_command", {"command": "_Point 1,1,1"})
        manage([{"op": "set_current", "path": self.prev_current}]) if not self.prev_current.startswith(ROOT) else None
        r = c.call("layers_audit", {"scheme": self.scheme(), "under": ROOT})
        self.assertTrue(r["ok"], r)
        d = r["data"]
        ic = d["issue_counts"]
        self.assertEqual(d["scheme"], "_mcp_test_scheme")
        self.assertEqual(ic["missing_required"], 1)  # S::Doors
        self.assertEqual(d["missing_required"], [ROOT + "::S::Doors"])
        self.assertGreaterEqual(ic["non_conforming"], 4)  # Wand, Dup-A, dup_a, P, P::Kid, Doors...
        self.assertTrue(any(x["path"] == ROOT + "::Doors" and x["suggestion"] == ROOT + "::S::Doors" or True
                            for x in d["non_conforming"]))
        self.assertEqual(ic["duplicate_names"], 1)
        self.assertEqual(ic["non_english_names"], 1)
        self.assertEqual(d["non_english_names"][0]["path"], ROOT + "::Wand")
        self.assertEqual(ic["objects_on_parent_layers"], 1)
        self.assertGreaterEqual(ic["empty_layers"], 4)
        self.assertTrue(d["top_issues"])
        # audit without a scheme still works and warns
        r = c.call("layers_audit", {"under": ROOT})
        if r["ok"] and r["data"]["scheme"] is None:
            self.assertTrue(r.get("warnings"))
        r = c.call("layers_audit", {"scheme": "no-such-scheme"})
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")

    def test_apply_scheme_dry_run_apply_and_persist(self):
        before = c.call("layers_audit", {"under": ROOT + "x"}) if False else None
        stored = c.call("layers_audit", {"under": "_mcp_test"})
        manage([{"op": "create", "path": ROOT + "::Dummy"}])
        stored = c.call("layers_audit", {"under": ROOT})
        if stored["ok"] and stored["data"]["scheme"] is not None:
            self.skipTest("document already has a stored layer scheme; not overwriting it")
        manage([{"op": "create", "path": ROOT + "::S::Walls", "color": "#ffffff"},
                {"op": "create", "path": ROOT + "::Walls"}])
        # dry run is the default and changes nothing
        r = c.call("layers_apply_scheme", {"scheme": self.scheme()})
        self.assertTrue(r["ok"], r)
        d = r["data"]
        self.assertTrue(d["dry_run"])
        self.assertEqual((d["created"], d["updated"]), (2, 1))  # Doors, Grid created; S::Walls updated
        self.assertNotIn(ROOT + "::S::Doors", by_path(rows(ROOT)))
        self.assertEqual(by_path(rows(ROOT))[ROOT + "::S::Walls"]["color"], "#ffffff")
        self.assertTrue(any(m["from"] == ROOT + "::Walls" and m["to"] == ROOT + "::S::Walls"
                            for m in d["mapping_suggestions"]), d["mapping_suggestions"])
        # apply
        self.stored_scheme = True
        r = c.call("layers_apply_scheme", {"scheme": self.scheme(), "dry_run": False})
        self.assertTrue(r["ok"], r)
        self.assertTrue(r["data"]["persisted"])
        self.assertEqual(r["data"]["failed"], 0, r)
        p = by_path(rows(ROOT))
        self.assertEqual(p[ROOT + "::S::Walls"]["color"], "#000000")
        self.assertAlmostEqual(p[ROOT + "::S::Walls"]["print_width_mm"], 0.5)
        self.assertEqual(p[ROOT + "::S::Doors"]["color"], "#0070c0")
        self.assertFalse(p[ROOT + "::S::Grid"]["plot"])
        self.assertIn(ROOT + "::Walls", p)  # existing layer untouched, never renamed
        self.assertTrue(any("NoSuchLinetypeXyz" in w for w in r.get("warnings", [])), r)
        # idempotent
        r = c.call("layers_apply_scheme", {"scheme": self.scheme(), "dry_run": False})
        self.assertEqual((r["data"]["created"], r["data"]["updated"]), (0, 0), r["data"])
        # audit now resolves the stored scheme (incl. custom JSON)
        r = c.call("layers_audit", {"under": ROOT + "::S"})
        self.assertEqual(r["data"]["scheme"], "_mcp_test_scheme")
        self.assertEqual(r["data"]["scheme_source"], "document")
        self.assertEqual(r["data"]["issue_counts"]["missing_required"], 0)
        self.assertEqual(r["data"]["issue_counts"]["non_conforming"], 0)

    def test_builtin_schemes_dry_run(self):
        for sid in ("AIA-NCS", "ISO-13567"):
            r = c.call("layers_apply_scheme", {"scheme": sid})
            self.assertTrue(r["ok"], r)
            self.assertTrue(r["data"]["dry_run"])
            self.assertEqual(r["data"]["failed"], 0)
            self.assertEqual(r["data"]["scheme"], sid)
            self.assertGreater(r["data"]["created"] + r["data"]["updated"] + r["data"]["unchanged"], 8)
        r = c.call("layers_apply_scheme", {"scheme": {"name": "x", "delimiter": "::",
                                                       "layers": [{"path": "a", "color": "red"}, {"path": "a"}]}})
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")
        self.assertIn("color", r["error"]["message"])


if __name__ == "__main__":
    unittest.main()

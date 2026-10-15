import unittest

import mcp_client as c

LAYER = "_mcp_test::script"


def cleanup():
    """Delete everything on the test layer (only objects this suite created live there) and the layer itself."""
    c.call("run_python", {"code": (
        "import scriptcontext as sc\n"
        "n = 0\n"
        "if rs.IsLayer(LAYER):\n"
        "    ids = rs.ObjectsByLayer(LAYER) or []\n"
        "    n = len(ids)\n"
        "    if ids: rs.DeleteObjects(ids)\n"
        "    rs.DeleteLayer(LAYER)\n"
        "    if rs.IsLayer('_mcp_test') and not rs.LayerChildCount('_mcp_test'): rs.DeleteLayer('_mcp_test')\n"
        "result = n\n"
    ).replace("LAYER", repr(LAYER))})


class ScriptTests(unittest.TestCase):
    def setUp(self):
        cleanup()

    def tearDown(self):
        cleanup()

    def test_run_python_creates_line(self):
        r = c.call("run_python", {"code": (
            "print 'hello %s' % args['who']\n"
            "if not rs.IsLayer('_mcp_test::script'): rs.AddLayer('_mcp_test::script')\n"
            "id = rs.AddLine([0, 0, 0], [10, 0, 0])\n"
            "rs.ObjectLayer(id, '_mcp_test::script')\n"
            "result = {'length': rs.CurveLength(id), 'who': args['who']}\n"
        ), "args": {"who": "world"}})
        self.assertTrue(r["ok"], r)
        d = r["data"]
        self.assertAlmostEqual(d["result"]["length"], 10.0)
        self.assertEqual(d["result"]["who"], "world")
        self.assertIn("hello world", d["stdout"])
        self.assertEqual(len(d["objects_added"]), 1)
        self.assertIsNone(d["error"])
        self.assertIn("state_token", r)

    def test_run_python_syntax_error(self):
        r = c.call("run_python", {"code": "x = 1\nif x ==:\n    pass\n"})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "SCRIPT_ERROR")
        err = r["error"]["details"]["error"]
        self.assertEqual(err["type"], "SyntaxError")
        self.assertEqual(err["line"], 2)

    def test_run_python_runtime_error_line(self):
        r = c.call("run_python", {"code": "def f(a):\n    return a / 0\nprint 'before'\nf(1)\n"})
        self.assertFalse(r["ok"])
        err = r["error"]["details"]["error"]
        self.assertEqual(err["type"], "ZeroDivisionError")
        self.assertEqual(err["line"], 2)
        self.assertEqual(r["error"]["details"]["stdout"].strip(), "before")

    def test_history_undo_reverts_run_python(self):
        # Compare by object id, not document counts: the live document may change for other reasons.
        r = c.call("run_python", {"code": (
            "if not rs.IsLayer('_mcp_test::script'): rs.AddLayer('_mcp_test::script')\n"
            "id = rs.AddPoint([1, 2, 3])\n"
            "rs.ObjectLayer(id, '_mcp_test::script')\n"
            "result = str(id)\n"
        )})
        self.assertTrue(r["ok"], r)
        pid = r["data"]["result"]
        exists = {"code": "result = rs.IsObject(args['i'])", "args": {"i": pid}}
        self.assertTrue(c.call("run_python", exists)["data"]["result"])
        u = c.call("history_undo", {})
        self.assertTrue(u["ok"], u)
        self.assertFalse(c.call("run_python", exists)["data"]["result"])

    def test_run_csharp_value_and_point(self):
        r = c.call("run_csharp", {"code": "Log(\"hi\"); return 1 + 1;"})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["result"], 2)
        self.assertEqual(r["data"]["log"], ["hi"])
        r = c.call("run_csharp", {"code": (
            "int li = doc.Layers.FindByFullPath(\"_mcp_test::script\", -1);\n"
            "if (li < 0) {\n"
            "  var parent = new Rhino.DocObjects.Layer { Name = \"_mcp_test\" };\n"
            "  int pi = doc.Layers.FindByFullPath(\"_mcp_test\", -1); if (pi < 0) pi = doc.Layers.Add(parent);\n"
            "  li = doc.Layers.Add(new Rhino.DocObjects.Layer { Name = \"script\", ParentLayerId = doc.Layers[pi].Id });\n"
            "}\n"
            "var attr = new Rhino.DocObjects.ObjectAttributes { LayerIndex = li };\n"
            "var id = doc.Objects.AddPoint(new Point3d(args[\"x\"].Value<double>(), 0, 0), attr);\n"
            "return new { id = id.ToString(), count = doc.Objects.Count };"
        ), "args": {"x": 5}})
        self.assertTrue(r["ok"], r)
        self.assertEqual(len(r["data"]["objects_added"]), 1)
        self.assertEqual(r["data"]["engine"], "roslyn")

    def test_no_duplicate_framework_assemblies(self):
        c.call("run_csharp", {"code": "return 1;"})
        r = c.call("run_csharp", {"code": (
            "return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name)"
            ".GroupBy(n => n).Where(g => g.Count() > 1 && (g.Key.StartsWith(\"System\") || g.Key == \"mscorlib\"))"
            ".Select(g => g.Key + \"x\" + g.Count()).ToArray();"
        )})
        self.assertTrue(r["ok"], r)
        self.assertEqual(r["data"]["result"], [])
        r = c.call("run_python", {"code": "result = len([a for a in System.AppDomain.CurrentDomain.GetAssemblies() if a.GetName().Name == 'System.Drawing'])"})
        self.assertEqual(r["data"]["result"], 1)

    def test_run_csharp_compile_error(self):
        r = c.call("run_csharp", {"code": "int x = 1;\nint y = x +;\nreturn y;"})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "SCRIPT_ERROR")
        diags = r["error"]["details"]["diagnostics"]
        self.assertTrue(diags)
        self.assertEqual(diags[0]["line"], 2)
        self.assertEqual(diags[0]["severity"], "Error")

    def test_run_csharp_runtime_error(self):
        r = c.call("run_csharp", {"code": "throw new System.InvalidOperationException(\"boom\");"})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "SCRIPT_ERROR")
        self.assertIn("boom", r["error"]["message"])

    def test_commands_search_box(self):
        r = c.call("commands_search", {"query": "box"})
        self.assertTrue(r["ok"], r)
        names = [m["name"].lower() for m in r["data"]["matches"]]
        self.assertIn("box", names)
        self.assertEqual(names[0], "box")

    def test_commands_search_flags_modal(self):
        r = c.call("commands_search", {"query": "what", "limit": 50})
        self.assertTrue(r["ok"], r)
        m = {x["name"].lower(): x for x in r["data"]["matches"]}
        self.assertTrue(m["what"]["blocked_in_run_command"])
        r = c.call("commands_search", {"query": "export", "limit": 50})
        m = {x["name"].lower(): x for x in r["data"]["matches"]}
        self.assertTrue(m["export"]["dash_advised"])


if __name__ == "__main__":
    unittest.main()

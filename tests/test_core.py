import json
import unittest

import mcp_client as c


class CoreTests(unittest.TestCase):
    def test_health_needs_no_auth(self):
        status, _, text = c.http("GET", "/health", auth=False)
        self.assertEqual(status, 200)
        d = json.loads(text)
        self.assertTrue(d["ok"])
        self.assertIn("version", d)
        self.assertEqual(d["port"], c.instance()["port"])

    def test_auth_required(self):
        body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "ping"})
        status, _, _ = c.http("POST", "/mcp", body, auth=False)
        self.assertEqual(status, 401)
        status, _, _ = c.http("POST", "/mcp", body, headers={"Authorization": "Bearer wrong"}, auth=False)
        self.assertEqual(status, 401)

    def test_origin_rejected(self):
        body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "ping"})
        status, _, _ = c.http("POST", "/mcp", body, headers={"Origin": "http://evil.example"})
        self.assertEqual(status, 403)

    def test_bad_host_rejected(self):
        body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "ping"})
        status, _, _ = c.http("POST", "/mcp", body, headers={"Host": "evil.example"})
        # 403 from our check; HttpListener itself may answer 400 before we see the request.
        self.assertIn(status, (400, 403))

    def test_get_mcp_is_405(self):
        status, _, _ = c.http("GET", "/mcp")
        self.assertEqual(status, 405)

    def test_initialize(self):
        r = c.rpc("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                 "clientInfo": {"name": "t", "version": "0"}})
        self.assertEqual(r["protocolVersion"], "2025-11-25")
        self.assertIn("tools", r["capabilities"])
        self.assertTrue(r["instructions"])
        r = c.rpc("initialize", {"protocolVersion": "1999-01-01"})
        self.assertEqual(r["protocolVersion"], "2025-06-18")

    def test_notification_is_202(self):
        status, _, text = c.http("POST", "/mcp", json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}))
        self.assertEqual(status, 202)
        self.assertEqual(text, "")

    def test_protocol_errors(self):
        with self.assertRaises(c.ProtocolError) as e:
            c.rpc("no/such/method")
        self.assertEqual(e.exception.code, -32601)
        status, _, text = c.http("POST", "/mcp", "{bad json")
        self.assertEqual(json.loads(text)["error"]["code"], -32700)

    def test_tools_list(self):
        tools = {t["name"]: t for t in c.rpc("tools/list")["tools"]}
        for name in ("context_get", "run_command", "server_info", "dev_reload"):
            self.assertIn(name, tools)
        self.assertTrue(tools["context_get"]["annotations"]["readOnlyHint"])
        self.assertEqual(tools["context_get"]["inputSchema"]["type"], "object")

    def test_context_get(self):
        r = c.call("context_get")
        self.assertTrue(r["ok"], r)
        self.assertTrue(r["data"]["units"])
        self.assertIn("abs_tol", r["data"])
        self.assertEqual(r["units"], r["data"]["units"])

    def test_run_command_line_then_undo(self):
        before = c.call("context_get")["data"]["objects"]["total"]
        r = c.call("run_command", {"command": "_Line 0,0,0 10,0,0"})
        self.assertTrue(r["ok"], r)
        self.assertTrue(r["data"]["success"])
        self.assertEqual(len(r["data"]["added"]), 1)
        self.assertEqual(c.call("context_get")["data"]["objects"]["total"], before + 1)
        r = c.call("history_undo", {"steps": 1})
        self.assertTrue(r["ok"], r)
        self.assertEqual(c.call("context_get")["data"]["objects"]["total"], before)

    def test_mutating_tool_is_one_undo_step(self):
        before = c.call("context_get")["data"]["objects"]["total"]
        r = c.call("geometry_create", {"items": [{"kind": "box", "corner": [0, 0, 0], "size": [1, 1, 1], "layer": "_mcp_test::core"}]})
        self.assertTrue(r["ok"], r)
        self.assertEqual(c.call("context_get")["data"]["objects"]["total"], before + 1)
        r = c.call("history_undo", {"steps": 1})
        self.assertTrue(r["ok"], r)
        self.assertEqual(c.call("context_get")["data"]["objects"]["total"], before)

    def test_command_waiting_for_input_is_cancelled(self):
        r = c.call("run_command", {"command": "_Line", "input_timeout_s": 2})
        self.assertFalse(r["ok"])
        self.assertEqual(r["error"]["code"], "COMMAND_NEEDS_INPUT")
        self.assertIn("Start of line", r["error"]["details"]["last_prompt"])
        # Rhino is responsive again afterwards
        self.assertTrue(c.call("context_get")["ok"])

    def test_modal_commands_blocked(self):
        for cmd in ("_What", "-_What", "_Options", "_Export"):
            r = c.call("run_command", {"command": cmd})
            self.assertEqual(r["error"]["code"], "COMMAND_BLOCKED", cmd)

    def test_invalid_args_collected(self):
        r = c.call("run_command", {})
        self.assertFalse(r["ok"])
        self.assertTrue(r["_isError"])
        self.assertEqual(r["error"]["code"], "INVALID_ARGS")

    def test_unknown_tool_is_protocol_error(self):
        with self.assertRaises(c.ProtocolError) as e:
            c.call("nope_tool")
        self.assertEqual(e.exception.code, -32602)


if __name__ == "__main__":
    unittest.main()

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RhinoMCP
{
    /// <summary>Hand-written MCP (JSON-RPC 2.0) handler. Stateless; no SDK dependency.</summary>
    public static class McpProtocol
    {
        static readonly string[] SupportedVersions = { "2025-11-25", "2025-06-18", "2025-03-26" };
        const string DefaultVersion = "2025-06-18";

        public const string Instructions =
            "Rhino 7 (Mac) MCP server. Start with context_get to learn units, tolerance, layers and object counts. " +
            "Objects are addressed by short handles (o12); tools return handles, not GUIDs. Lengths are in document units. " +
            "Lists are bounded and paged. Mutating tools are undoable (one undo step per call). " +
            "Use run_command only when no dedicated tool exists. [placeholder text; replaced in a later milestone]";

        /// <summary>Returns the JSON response text, or null when the HTTP reply should be 202 with no body.</summary>
        public static string Handle(string body)
        {
            JToken root;
            try { root = JToken.Parse(body); }
            catch (JsonException) { return Error(null, -32700, "Parse error"); }

            var req = root as JObject;
            if (req == null) return Error(null, -32600, "Invalid Request: expected a single JSON-RPC object (batches are not supported)");

            JToken idTok;
            bool hasId = req.TryGetValue("id", out idTok);
            string method = req.Value<string>("method");
            if (method == null)
            {
                if (req["result"] != null || req["error"] != null) return null; // a response from the client; ignore
                return Error(hasId ? idTok : null, -32600, "Invalid Request: 'method' missing");
            }
            if (!hasId) return null; // notification (e.g. notifications/initialized)

            var prms = req["params"] as JObject ?? new JObject();
            try
            {
                switch (method)
                {
                    case "initialize": return Result(idTok, Initialize(prms));
                    case "ping": return Result(idTok, new JObject());
                    case "tools/list": return Result(idTok, new JObject { { "tools", ToolList() } });
                    case "tools/call": return CallTool(idTok, prms);
                    case "resources/list": return Result(idTok, new JObject { { "resources", new JArray() } });
                    case "resources/templates/list": return Result(idTok, new JObject { { "resourceTemplates", new JArray() } });
                    case "resources/read": return Error(idTok, -32602, "Unknown resource: " + prms.Value<string>("uri"));
                    case "prompts/list": return Result(idTok, new JObject { { "prompts", new JArray() } });
                    case "prompts/get": return Error(idTok, -32602, "Unknown prompt: " + prms.Value<string>("name"));
                    default: return Error(idTok, -32601, "Method not found: " + method);
                }
            }
            catch (Exception ex)
            {
                return Error(idTok, -32603, "Internal error: " + ex.Message);
            }
        }

        static JObject Initialize(JObject p)
        {
            string wanted = p.Value<string>("protocolVersion");
            string version = wanted != null && SupportedVersions.Contains(wanted) ? wanted : DefaultVersion;
            return new JObject
            {
                { "protocolVersion", version },
                { "capabilities", new JObject {
                    { "tools", new JObject { { "listChanged", false } } },
                    { "resources", new JObject { { "listChanged", false } } },
                    { "prompts", new JObject { { "listChanged", false } } } } },
                { "serverInfo", new JObject { { "name", "rhino-mcp" }, { "title", "RhinoMCP" }, { "version", RhinoMCPPlugin.VersionString } } },
                { "instructions", Instructions }
            };
        }

        static JArray ToolList()
        {
            var arr = new JArray();
            foreach (var t in ToolRegistry.All)
            {
                var o = new JObject
                {
                    { "name", t.Name },
                    { "description", t.Description },
                    { "inputSchema", ToolRegistry.SchemaFor(t) },
                    { "annotations", new JObject {
                        { "readOnlyHint", t.Kind == ToolKind.Read },
                        { "destructiveHint", t.Kind == ToolKind.Destructive || t.Kind == ToolKind.Script } } }
                };
                arr.Add(o);
            }
            return arr;
        }

        static string CallTool(JToken id, JObject p)
        {
            string name = p.Value<string>("name");
            if (string.IsNullOrEmpty(name)) return Error(id, -32602, "Invalid params: 'name' is required");
            ITool tool;
            if (!ToolRegistry.TryGet(name, out tool))
                return Error(id, -32602, "Unknown tool: " + name + ". Call tools/list for available tools.");
            JToken argTok = p["arguments"];
            if (argTok != null && argTok.Type != JTokenType.Null && !(argTok is JObject))
                return Error(id, -32602, "Invalid params: 'arguments' must be an object");
            var args = argTok as JObject ?? new JObject();

            ToolResult r;
            try { r = UiDispatcher.Invoke(() => ToolRegistry.Execute(tool, args)); }
            catch (TimeoutException ex)
            {
                r = ToolResult.Fail("TIMEOUT", ex.Message,
                    "Rhino may be busy or showing a dialog. The call may still complete later; check with context_get before retrying.");
            }
            catch (Exception ex) { r = ToolRegistry.InternalError(ex); }
            return Result(id, r.ToMcp());
        }

        static string Result(JToken id, JObject result)
        {
            return new JObject { { "jsonrpc", "2.0" }, { "id", id }, { "result", result } }.ToString(Formatting.None);
        }

        public static string Error(JToken id, int code, string message)
        {
            return new JObject
            {
                { "jsonrpc", "2.0" }, { "id", id == null ? JValue.CreateNull() : id },
                { "error", new JObject { { "code", code }, { "message", message } } }
            }.ToString(Formatting.None);
        }
    }
}

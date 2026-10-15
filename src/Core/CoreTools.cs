using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCP
{
    /// <summary>Built-in tools that live in the core assembly.</summary>
    public static class CoreTools
    {
        public static IEnumerable<ITool> Create(bool devMode)
        {
            yield return new ServerInfoTool();
            if (devMode) yield return new DevReloadTool();
        }
    }

    sealed class ServerInfoTool : ITool
    {
        public string Name { get { return "server_info"; } }
        public string Description { get { return "Server status: version, port, uptime, tool count, tools dll, devMode, Rhino version, state_token. Use to check the connection."; } }
        public string InputSchema { get { return "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"; } }
        public ToolKind Kind { get { return ToolKind.Read; } }
        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var s = RhinoMCPPlugin.Server;
            return ToolResult.Ok(new
            {
                version = RhinoMCPPlugin.VersionString,
                rhino = RhinoApp.Version.ToString(),
                port = s == null ? 0 : s.Port,
                uptime_s = s == null ? 0 : (long)(DateTime.UtcNow - s.StartedAt).TotalSeconds,
                tools = ToolRegistry.Count,
                tools_dll = ToolRegistry.ToolsDll == null ? null : System.IO.Path.GetFileName(ToolRegistry.ToolsDll),
                dev_mode = RhinoMCPPlugin.Config.DevMode,
                allow_scripts = RhinoMCPPlugin.Config.AllowScripts
            });
        }
    }

    sealed class DevReloadTool : ITool
    {
        public string Name { get { return "dev_reload"; } }
        public string Description { get { return "Development only: reload the newest tools DLL from ~/Library/Application Support/RhinoMCP/tools without restarting Rhino. Returns tool count and names. On failure the previous tool set stays active."; } }
        public string InputSchema { get { return "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"; } }
        public ToolKind Kind { get { return ToolKind.Read; } }
        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var r = ToolRegistry.Reload();
            if (!r.Ok)
                return ToolResult.Fail("INTERNAL", "Reload failed: " + r.Error,
                    "Previous tools stay active. Fix the problem (duplicate names, load errors) and rebuild.", new { active_tools = r.Names });
            return ToolResult.Ok(new { count = r.Names.Length, dll = r.Dll, changed = r.Changed, names = r.Names });
        }
    }
}

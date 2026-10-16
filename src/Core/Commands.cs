using System;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;

namespace RhinoMCP
{
    [Guid("B5781EFF-6842-482E-AB90-3657DEACF3E8")]
    public class MCPStart : Command
    {
        public override string EnglishName { get { return "MCPStart"; } }
        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            try
            {
                var s = RhinoMCPPlugin.StartServer();
                RhinoApp.WriteLine("RhinoMCP running at " + s.BaseUrl + "/mcp");
                return Result.Success;
            }
            catch (Exception ex) { RhinoApp.WriteLine("RhinoMCP: could not start: " + ex.Message); return Result.Failure; }
        }
    }

    [Guid("7E2FC465-09C3-45C4-83FD-780BEB1C872F")]
    public class MCPStop : Command
    {
        public override string EnglishName { get { return "MCPStop"; } }
        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            RhinoMCPPlugin.StopServer();
            RhinoApp.WriteLine("RhinoMCP stopped.");
            return Result.Success;
        }
    }

    [Guid("226D7E3D-3053-4CBA-9078-0822EADD9FD4")]
    public class MCPStatus : Command
    {
        public override string EnglishName { get { return "MCPStatus"; } }
        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var s = RhinoMCPPlugin.Server;
            if (s == null || !s.Running) { RhinoApp.WriteLine("RhinoMCP: not running. Use MCPStart."); return Result.Success; }
            var up = DateTime.UtcNow - s.StartedAt;
            RhinoApp.WriteLine("RhinoMCP " + RhinoMCPPlugin.VersionString + ": " + s.BaseUrl + "/mcp, " +
                               ToolRegistry.Count + " tools, uptime " + (long)up.TotalSeconds + "s");
            return Result.Success;
        }
    }

    [Guid("2E51A9D6-0CBB-45CF-B3FA-4CF5FF883591")]
    public class MCPConnect : Command
    {
        public override string EnglishName { get { return "MCPConnect"; } }
        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var s = RhinoMCPPlugin.Server;
            if (s == null || !s.Running) { RhinoApp.WriteLine("RhinoMCP: not running. Use MCPStart first."); return Result.Failure; }
            string line = "claude mcp add --transport http rhino " + s.BaseUrl + "/mcp --header \"Authorization: Bearer " + RhinoMCPPlugin.Config.Token + "\"";
            RhinoApp.WriteLine(line);
            RhinoApp.WriteLine(Platform.CopyToClipboard(line) ? "(copied to clipboard)" : "(could not copy to clipboard)");
            return Result.Success;
        }
    }
}

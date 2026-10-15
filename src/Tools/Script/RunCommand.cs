using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;

namespace RhinoMCP.Tools.Script
{
    /// <summary>
    /// Runs a Rhino command string. Rhino commands manage their own undo records, so the registry does not
    /// open one (IManagesOwnUndo); this also lets "_Undo" work as expected.
    /// </summary>
    public sealed class RunCommand : ITool, IManagesOwnUndo
    {
        public string Name { get { return "run_command"; } }
        public string Description
        {
            get
            {
                return "Run a Rhino command string, e.g. \"_Line 0,0,0 10,0,0\" or \"-_Layer _New Walls\". Returns success, " +
                       "the command-window lines produced, and handles of objects added. Always pass every argument inline and prefix " +
                       "commands with _ (and - for scripted dialogs); a command waiting for interactive input blocks Rhino until it times out. " +
                       "Prefer dedicated tools when they exist.";
            }
        }
        public string InputSchema
        {
            get { return "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"description\":\"Rhino command line, e.g. _Line 0,0,0 10,0,0\"}},\"required\":[\"command\"],\"additionalProperties\":false}"; }
        }
        public ToolKind Kind { get { return ToolKind.Script; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var a = new ArgReader(args);
            string cmd = a.String("command", true);
            if (a.HasErrors) return a.ErrorResult();
            if (string.IsNullOrWhiteSpace(cmd))
                return ToolResult.Fail("INVALID_ARGS", "'command' is empty", "Pass a Rhino command such as \"_Line 0,0,0 10,0,0\".");

            var added = new List<Guid>();
            EventHandler<RhinoObjectEventArgs> onAdd = delegate (object s, RhinoObjectEventArgs e) { added.Add(e.ObjectId); };
            bool prevCapture = RhinoApp.CommandWindowCaptureEnabled;
            bool success;
            string[] lines;
            RhinoDoc.AddRhinoObject += onAdd;
            try
            {
                RhinoApp.CommandWindowCaptureEnabled = true;
                RhinoApp.CapturedCommandWindowStrings(true); // clear
                success = RhinoApp.RunScript(cmd, false);
                lines = RhinoApp.CapturedCommandWindowStrings(true) ?? new string[0];
            }
            finally
            {
                RhinoDoc.AddRhinoObject -= onAdd;
                RhinoApp.CommandWindowCaptureEnabled = prevCapture;
            }

            var output = lines.SelectMany(l => (l ?? "").Split('\n'))
                              .Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).Take(100).ToList();
            // Objects added then removed again inside the same command (temp geometry) are not reported.
            var live = added.Distinct().Where(id => ctx.Doc.Objects.FindId(id) != null).ToList();
            var handles = live.Take(200).Select(id => ctx.Handles.For(id)).ToList();

            var data = new { success = success, output = output, added = handles, added_total = live.Count };
            if (!success)
                return ToolResult.Fail("SCRIPT_ERROR", "Rhino reported the command failed or was cancelled",
                    "Check the output lines for the reason; make sure the command name and all arguments are valid.",
                    data);
            return ToolResult.Ok(data);
        }
    }
}

using System;
using Newtonsoft.Json.Linq;
using Rhino;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Script
{
    /// <summary>
    /// Cancels a Rhino command that is stuck waiting for input (e.g. "Start of line"). Posts Escape into the Rhino document window
    /// (deminiaturizing it if it is minimized) and reports how. Safe to call when idle (does nothing). Because it runs while the
    /// stuck command pumps UI callbacks, call it repeatedly until was_in_command is false.
    /// </summary>
    public sealed class CommandCancel : ITool
    {
        public string Name { get { return "command_cancel"; } }
        public ToolKind Kind { get { return ToolKind.Read; } }
        public string Description
        {
            get
            {
                return "Cancel a Rhino command that is stuck waiting for input. Does nothing when Rhino is idle. Sends Escape to the document window " +
                       "(a minimized window is restored first). Returns was_in_command, cancel_method and the prompt it was waiting at; repeat until was_in_command is false.";
            }
        }
        public string InputSchema { get { return Schemas.Object(""); } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            bool inCmd = Rhino.Commands.Command.InCommand();
            if (!inCmd) return ToolResult.Ok(new { was_in_command = false, cancel_method = (string)null, prompt = (string)null });
            string prompt = null;
            try { prompt = RhinoApp.CommandPrompt; } catch (Exception) { }
            string m;
            bool posted = MacEscape.Post(out m);
            if (!posted) { RhinoApp.SendKeystrokes("!", true); m = "sendkeystrokes"; }
            try { RhinoApp.Wait(); } catch (Exception) { }
            return ToolResult.Ok(new { was_in_command = true, cancel_method = m, prompt = prompt });
        }
    }
}

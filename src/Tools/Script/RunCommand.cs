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
    /// <summary>
    /// Cancels a command that waits for interactive input. Rhino keeps pumping UI callbacks while a
    /// command waits at a GetPoint/GetOption prompt, so after the timeout we inject Escape ("!") on the
    /// UI thread. Modal dialogs do not pump these callbacks and cannot be cancelled this way.
    /// </summary>
    sealed class InputWatchdog
    {
        System.Threading.Timer _timer;
        public volatile bool Fired;
        public string Prompt;
        public void Start(int seconds)
        {
            if (seconds <= 0) return;
            _timer = new System.Threading.Timer(_ =>
            {
                RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    try { Prompt = RhinoApp.CommandPrompt; } catch (Exception) { }
                    Fired = true;
                    RhinoApp.SendKeystrokes("!", true);
                }));
            }, null, seconds * 1000, System.Threading.Timeout.Infinite);
        }
        public void Stop() { if (_timer != null) _timer.Dispose(); }
    }

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
            get { return "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"description\":\"Rhino command line, e.g. _Line 0,0,0 10,0,0\"},\"input_timeout_s\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":120,\"default\":10,\"description\":\"Cancel the command if it is still waiting for input after this many seconds (0 = never).\"}},\"required\":[\"command\"],\"additionalProperties\":false}"; }
        }
        public ToolKind Kind { get { return ToolKind.Script; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var a = new ArgReader(args);
            string cmd = a.String("command", true);
            if (a.HasErrors) return a.ErrorResult();
            if (string.IsNullOrWhiteSpace(cmd))
                return ToolResult.Fail("INVALID_ARGS", "'command' is empty", "Pass a Rhino command such as \"_Line 0,0,0 10,0,0\".");

            var block = CheckModal(cmd);
            if (block != null) return block;

            var added = new List<Guid>();
            EventHandler<RhinoObjectEventArgs> onAdd = delegate (object s, RhinoObjectEventArgs e) { added.Add(e.ObjectId); };
            int inputTimeout = a.Int("input_timeout_s", false, 10);
            bool prevCapture = RhinoApp.CommandWindowCaptureEnabled;
            bool success;
            string[] lines;
            string histBefore = SafeHistory();
            var watchdog = new InputWatchdog();
            RhinoDoc.AddRhinoObject += onAdd;
            try
            {
                RhinoApp.CommandWindowCaptureEnabled = true;
                RhinoApp.CapturedCommandWindowStrings(true); // clear
                watchdog.Start(inputTimeout);
                success = RhinoApp.RunScript(cmd, true);
                lines = RhinoApp.CapturedCommandWindowStrings(true) ?? new string[0];
            }
            finally
            {
                watchdog.Stop();
                if (watchdog.Fired) RhinoMCP.Tools.History.HistoryUndo.CloseStrayRecord(ctx.Doc);
                RhinoDoc.AddRhinoObject -= onAdd;
                RhinoApp.CommandWindowCaptureEnabled = prevCapture;
            }
            if (lines.Length == 0)
            {
                // Capture is empty on Mac; fall back to the command-history diff.
                string histAfter = SafeHistory();
                if (histAfter.Length > histBefore.Length && histAfter.StartsWith(histBefore, StringComparison.Ordinal))
                    lines = new[] { histAfter.Substring(histBefore.Length) };
                else if (histAfter.Length > 0 && histAfter != histBefore)
                    lines = new[] { histAfter.Substring(Math.Max(0, histAfter.Length - 4000)) };
            }

            var output = lines.SelectMany(l => (l ?? "").Split('\n'))
                              .Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).Take(100).ToList();
            // Objects added then removed again inside the same command (temp geometry) are not reported.
            var live = added.Distinct().Where(id => ctx.Doc.Objects.FindId(id) != null).ToList();
            var handles = live.Take(200).Select(id => ctx.Handles.For(id)).ToList();

            var data = new { success = success, output = output, added = handles, added_total = live.Count,
                             cancelled_waiting_for_input = watchdog.Fired, last_prompt = watchdog.Prompt };
            if (watchdog.Fired)
                return ToolResult.Fail("COMMAND_NEEDS_INPUT",
                    "The command stopped to wait for input and was cancelled after " + inputTimeout + " s" +
                    (string.IsNullOrEmpty(watchdog.Prompt) ? "" : " at prompt: " + watchdog.Prompt),
                    "Pass every input inline in one string and finish with _Enter, e.g. \"-_Box 0,0,0 10,10,0 5\" or " +
                    "\"-_Layer _New Walls _Enter\". Use the dash form (-_Command) to avoid dialogs. Use commands_search to check options.",
                    data);
            if (!success)
                return ToolResult.Fail("SCRIPT_ERROR", "Rhino reported the command failed or was cancelled",
                    "Check the output lines for the reason; make sure the command name and all arguments are valid.",
                    data);
            return ToolResult.Ok(data);
        }
        // Modal windows do not pump UI callbacks, so the input watchdog cannot cancel them: refuse up front.
        internal static readonly HashSet<string> AlwaysModal = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "What", "Options", "DocumentProperties", "PlugInManager", "About", "Help", "Notes", "SystemInfo" };
        internal static readonly HashSet<string> ModalUnlessDash = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Open", "Save", "SaveAs", "SaveSmall", "Import", "Export", "ExportWithOrigin", "Insert", "Print", "Hatch", "Text",
          "Make2D", "ViewCaptureToFile", "Purge", "Audit3dmFile", "BlockManager", "Block", "Mesh", "Rebuild", "FitCrv",
          "Unroll", "Smash", "Dim", "TextObject", "Leader" };

        static ToolResult CheckModal(string cmd)
        {
            string first = cmd.TrimStart().Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            bool dash = first.StartsWith("-");
            string name = first.TrimStart('-', '_', '\'', '!');
            if (AlwaysModal.Contains(name))
                return ToolResult.Fail("COMMAND_BLOCKED", "'" + name + "' opens a modal window that would freeze Rhino until a person closes it",
                    "Use a dedicated tool instead (context_get for document info, objects_get for object details).");
            if (!dash && ModalUnlessDash.Contains(name))
                return ToolResult.Fail("COMMAND_BLOCKED", "'" + name + "' opens a dialog unless run in its scripted form",
                    "Use the dash form and pass all options inline, e.g. \"-_" + name + " ... _Enter\".");
            return null;
        }

        static string SafeHistory()
        {
            try { return RhinoApp.CommandHistoryWindowText ?? ""; } catch (Exception) { return ""; }
        }
    }
}

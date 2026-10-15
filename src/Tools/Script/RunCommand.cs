using System;
using System.Runtime.InteropServices;
using System.Threading;
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
        int _attempts;
        public void Start(int seconds)
        {
            if (seconds <= 0) return;
            // First attempt after the timeout, then retry every 2 s (up to 4 tries): SendKeystrokes only
            // reaches the command line while Rhino's main window has keyboard focus.
            _timer = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (Interlocked.Increment(ref _attempts) > 4) { _timer.Change(Timeout.Infinite, Timeout.Infinite); return; }
                    RhinoApp.InvokeOnUiThread(new Action(() =>
                    {
                        try
                        {
                            if (!Rhino.Commands.Command.InCommand()) return;
                            if (Prompt == null) { try { Prompt = RhinoApp.CommandPrompt; } catch (Exception) { } }
                            Fired = true;
                            // SendKeystrokes only works while Rhino has keyboard focus; posting a native
                            // Escape key event into Rhino's own AppKit queue works in the background too.
                            if (!MacEscape.Post()) RhinoApp.SendKeystrokes("!", true);
                        }
                        catch (Exception) { }
                    }));
                }
                catch (Exception) { }
            }, null, seconds * 1000, 2000);
        }
        public void Stop() { if (_timer != null) _timer.Dispose(); }
    }

    /// <summary>
    /// Posts an Escape key-down/up pair to Rhino's main window via the Objective-C runtime
    /// ([NSApp postEvent:atStart:]). Must run on the UI thread. Returns false if anything is unavailable.
    /// </summary>
    static class MacEscape
    {
        const string ObjC = "/usr/lib/libobjc.dylib";
        [StructLayout(LayoutKind.Sequential)] struct NSPoint { public double X, Y; }

        [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendPtr(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendIdx(IntPtr r, IntPtr s, ulong i);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern long SendLong(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern ulong SendULong(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void SendPost(IntPtr r, IntPtr s, IntPtr e, [MarshalAs(UnmanagedType.I1)] bool atStart);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr SendKeyEvent(IntPtr r, IntPtr s, ulong type, NSPoint loc, ulong flags, double time, long windowNumber,
            IntPtr context, IntPtr chars, IntPtr charsIgnoring, [MarshalAs(UnmanagedType.I1)] bool isRepeat, ushort keyCode);

        public static bool Post()
        {
            try
            {
                if (!Rhino.Runtime.HostUtils.RunningOnOSX) return false;
                IntPtr app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                if (app == IntPtr.Zero) return false;
                IntPtr win = Send(app, sel_registerName("mainWindow"));
                if (win == IntPtr.Zero)
                {
                    IntPtr wins = Send(app, sel_registerName("orderedWindows"));
                    if (wins != IntPtr.Zero && SendULong(wins, sel_registerName("count")) > 0)
                        win = SendIdx(wins, sel_registerName("objectAtIndex:"), 0);
                }
                long winNum = win == IntPtr.Zero ? 0 : SendLong(win, sel_registerName("windowNumber"));
                IntPtr esc = Marshal.StringToHGlobalAnsi("\u001b");
                IntPtr str;
                try { str = SendPtr(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), esc); }
                finally { Marshal.FreeHGlobal(esc); }
                IntPtr keySel = sel_registerName("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:");
                IntPtr postSel = sel_registerName("postEvent:atStart:");
                IntPtr nsEvent = objc_getClass("NSEvent");
                foreach (ulong type in new ulong[] { 10, 11 }) // NSEventTypeKeyDown, NSEventTypeKeyUp
                {
                    IntPtr ev = SendKeyEvent(nsEvent, keySel, type, new NSPoint(), 0, 0, winNum, IntPtr.Zero, str, str, false, 53);
                    if (ev == IntPtr.Zero) return false;
                    SendPost(app, postSel, ev, false);
                }
                return true;
            }
            catch (Exception) { return false; }
        }
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

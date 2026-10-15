using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCP.Tools.History
{
    /// <summary>Document-level undo. Rhino's _Undo command does nothing when run through RunScript, so use RhinoDoc.Undo().</summary>
    public sealed class HistoryUndo : ITool, IManagesOwnUndo
    {
        public string Name { get { return "history_undo"; } }
        public string Description { get { return "Undo the last N undo records in the active document (each MCP mutating call is one record named 'MCP: <tool>'). Returns how many steps were undone. Use instead of running the Undo command."; } }
        public string InputSchema { get { return "{\"type\":\"object\",\"properties\":{\"steps\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50,\"default\":1}},\"additionalProperties\":false}"; } }
        public ToolKind Kind { get { return ToolKind.Mutate; } }
        public ToolResult Execute(ToolContext ctx, JObject args) { return Run(ctx, args, true); }

        internal static ToolResult Run(ToolContext ctx, JObject args, bool undo)
        {
            var a = new ArgReader(args);
            int steps = a.Int("steps", false, 1, 1, 50);
            if (a.HasErrors) return a.ErrorResult();
            bool healed = CloseStrayRecord(ctx.Doc);
            int done = 0;
            for (int i = 0; i < steps; i++)
            {
                bool ok = undo ? ctx.Doc.Undo() : ctx.Doc.Redo();
                if (!ok) break;
                done++;
            }
            ctx.Doc.Views.Redraw();
            if (done == 0)
                return ToolResult.Fail("NOT_FOUND", undo ? "Nothing to undo" : "Nothing to redo", null);
            return ToolResult.Ok(new { requested = steps, done = done, closed_stray_record = healed });
        }

        /// <summary>
        /// A cancelled scripted command can leave an undo record open; while it is open Undo() fails and later
        /// edits pile into it. Close it when no command is running.
        /// </summary>
        internal static bool CloseStrayRecord(RhinoDoc doc) { return UndoScope.CloseStray(doc); }
    }

    public sealed class HistoryRedo : ITool, IManagesOwnUndo
    {
        public string Name { get { return "history_redo"; } }
        public string Description { get { return "Redo the last N undone records in the active document. Returns how many steps were redone."; } }
        public string InputSchema { get { return "{\"type\":\"object\",\"properties\":{\"steps\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50,\"default\":1}},\"additionalProperties\":false}"; } }
        public ToolKind Kind { get { return ToolKind.Mutate; } }
        public ToolResult Execute(ToolContext ctx, JObject args) { return HistoryUndo.Run(ctx, args, false); }
    }
}

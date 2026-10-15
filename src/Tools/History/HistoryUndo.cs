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
            int skippedEmpty = 0;
            for (int i = 0; i < steps; i++)
            {
                // Rhino may hold a near-empty record (selection/redraw) on top of the real one; an undo that
                // leaves the document unchanged does not count, so take one more step (bounded).
                for (int tries = 0; tries < 3; tries++)
                {
                    long before = Fingerprint(ctx.Doc);
                    bool ok = undo ? ctx.Doc.Undo() : ctx.Doc.Redo();
                    if (!ok) { tries = 99; break; }
                    if (Fingerprint(ctx.Doc) != before) { done++; break; }
                    skippedEmpty++;
                }
                if (done <= i) break;
            }
            ctx.Doc.Views.Redraw();
            if (done == 0)
                return ToolResult.Fail("NOT_FOUND", undo ? "Nothing to undo" : "Nothing to redo", null);
            return ToolResult.Ok(new { requested = steps, done = done, closed_stray_record = healed, skipped_empty_records = skippedEmpty });
        }

        /// <summary>
        /// A cancelled scripted command can leave an undo record open; while it is open Undo() fails and later
        /// edits pile into it. Close it when no command is running.
        /// </summary>
        /// <summary>Cheap document-state hash: object count, layer count and the newest object runtime serial.</summary>
        static long Fingerprint(RhinoDoc doc)
        {
            long h = doc.Objects.Count * 1000003L + doc.Layers.Count * 7919L;
            uint maxSerial = 0;
            foreach (var o in doc.Objects.GetObjectList(new Rhino.DocObjects.ObjectEnumeratorSettings { IncludeLights = true, HiddenObjects = true, LockedObjects = true }))
                if (o.RuntimeSerialNumber > maxSerial) maxSerial = o.RuntimeSerialNumber;
            return h ^ ((long)maxSerial << 20);
        }

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

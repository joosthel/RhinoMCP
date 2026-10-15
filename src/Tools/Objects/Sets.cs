using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino.DocObjects;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Objects
{
    public sealed class SetsSave : ITool
    {
        public string Name { get { return "sets_save"; } }
        public string Description
        {
            get
            {
                return "Save target objects as a named set, usable later as target {set:name} in any tool. Sets persist in the .3dm document (doc.Strings, key rhinomcp.set.<name>) " +
                       "and survive restarts. mode: replace (default) | add | remove (modifies an existing set). Shortcut: objects_query with save_as_set.";
            }
        }
        public string InputSchema
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"target\":" + ObjectQuery.TargetSchema +
                       ",\"mode\":{\"enum\":[\"replace\",\"add\",\"remove\"],\"default\":\"replace\"}},\"required\":[\"name\",\"target\"],\"additionalProperties\":false}";
            }
        }
        public ToolKind Kind { get { return ToolKind.Mutate; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            string name = a.String("name", true);
            string mode = a.String("mode", false, "replace");
            var objs = ObjectQuery.Resolve(ctx, args == null ? null : args["target"], a);
            if (name != null) { var e = ObjectQuery.ValidateSetName(name); if (e != null) a.Error("'name': " + e); }
            if (mode != "replace" && mode != "add" && mode != "remove") a.Error("'mode' must be replace, add or remove");
            List<Guid> existing = null;
            if (name != null && !a.HasErrors && mode != "replace")
            {
                existing = ObjectQuery.LoadSet(doc, name);
                if (existing == null) a.Error("set '" + name + "' does not exist; use mode 'replace' to create it. Existing sets: " + string.Join(", ", ObjectQuery.SetNames(doc)));
            }
            if (a.HasErrors) return a.ErrorResult();
            IEnumerable<Guid> ids;
            if (mode == "add") ids = existing.Concat(objs.Select(o => o.Id)).Distinct();
            else if (mode == "remove") { var rm = new HashSet<Guid>(objs.Select(o => o.Id)); ids = existing.Where(g => !rm.Contains(g)); }
            else ids = objs.Select(o => o.Id).Distinct();
            var list = ids.ToList();
            ObjectQuery.StoreSet(doc, name, list);
            return ToolResult.Ok(new { name = name, count = list.Count, mode = mode, persisted = "doc.Strings" });
        }
    }

    public sealed class SetsList : ITool
    {
        public string Name { get { return "sets_list"; } }
        public string Description { get { return "List named object sets in the document with live member counts (members deleted since saving are reported as stale)."; } }
        public string InputSchema { get { return "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"; } }
        public ToolKind Kind { get { return ToolKind.Read; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var rows = new List<object>();
            var names = ObjectQuery.SetNames(doc);
            foreach (var n in names.Take(200))
            {
                var ids = ObjectQuery.LoadSet(doc, n) ?? new List<Guid>();
                int live = ids.Count(g => doc.Objects.FindId(g) != null);
                rows.Add(new { name = n, count = live, stale = ids.Count - live });
            }
            return ToolResult.Ok(new { total = names.Count, sets = rows });
        }
    }

    public sealed class SetsDelete : ITool
    {
        public string Name { get { return "sets_delete"; } }
        public string Description { get { return "Delete a named set (the objects themselves are untouched). Errors with the list of existing sets if the name is unknown."; } }
        public string InputSchema { get { return "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"],\"additionalProperties\":false}"; } }
        public ToolKind Kind { get { return ToolKind.Mutate; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            string name = a.String("name", true);
            if (a.HasErrors) return a.ErrorResult();
            if (!ObjectQuery.RemoveSet(doc, name))
                return ToolResult.Fail("NOT_FOUND", "Set '" + name + "' not found.", "Existing sets: " + string.Join(", ", ObjectQuery.SetNames(doc)) + ". See sets_list.");
            return ToolResult.Ok(new { deleted = name });
        }
    }
}

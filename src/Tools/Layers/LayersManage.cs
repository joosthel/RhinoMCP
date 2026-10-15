using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;

namespace RhinoMCP.Tools.Layers
{
    public sealed class LayersManage : ITool
    {
        public string Name { get { return "layers_manage"; } }
        public string Description
        {
            get
            {
                return "Batch layer operations. NOTE: Rhino 7 Mac does not record layer-table edits in undo (history_undo cannot revert them); layers_audit/layers_list first, and use confirm_objects for deletes. ops: [{op: create|rename|move|set|delete|set_current, path, ...}]. " +
                       "create: builds the parent chain, idempotent (updates settings if the layer exists). rename: new_name (leaf only). " +
                       "move: parent (layer path, '' = top level; created if missing), sublayers follow. set / create accept color '#rrggbb', linetype (name; unknown -> Continuous + warning), " +
                       "print_width_mm, plot (false = do not print), visible, locked. " +
                       "delete: refuses layers with objects unless confirm_objects equals the object count of the layer (and sublayers); refuses sublayers unless recursive:true; the current layer cannot be deleted. " +
                       "Ops run in order; each gets its own result (a failed op does not stop the batch). All arguments are validated up front.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"ops\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":200,\"items\":{\"type\":\"object\",\"properties\":{" +
                    "\"op\":{\"type\":\"string\",\"enum\":[\"create\",\"rename\",\"move\",\"set\",\"delete\",\"set_current\"]}," +
                    "\"path\":{\"type\":\"string\",\"description\":\"Full layer path with '::'\"}," +
                    "\"new_name\":{\"type\":\"string\"},\"parent\":{\"type\":\"string\"}," +
                    "\"color\":{\"type\":\"string\",\"description\":\"#rrggbb\"},\"linetype\":{\"type\":\"string\"}," +
                    "\"print_width_mm\":{\"type\":\"number\",\"minimum\":0},\"plot\":{\"type\":\"boolean\"}," +
                    "\"visible\":{\"type\":\"boolean\"},\"locked\":{\"type\":\"boolean\"}," +
                    "\"recursive\":{\"type\":\"boolean\"},\"confirm_objects\":{\"type\":\"integer\",\"minimum\":0}" +
                    "},\"required\":[\"op\",\"path\"]}}",
                    "\"ops\"");
            }
        }
        public ToolKind Kind { get { return ToolKind.Mutate; } }

        sealed class Op
        {
            public int I; public string Kind, Path, NewName, Parent; public LayerProps Props; public bool Recursive; public int? Confirm;
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var ops = ParseOps(args);
            if (ops.Errors.Count > 0)
                return ToolResult.Fail("INVALID_ARGS", string.Join("; ", ops.Errors), "Fix all listed operations and call again.", new { errors = ops.Errors });

            var warnings = new List<string>();
            var results = new List<object>();
            int okCount = 0;
            foreach (var op in ops.List)
            {
                var errors = new List<string>();
                var info = new Dictionary<string, object>();
                try { Run(doc, op, info, warnings, errors); }
                catch (Exception e) { errors.Add("unexpected error: " + e.Message); }
                var r = new Dictionary<string, object>();
                r["i"] = op.I; r["op"] = op.Kind; r["path"] = op.Path;
                r["ok"] = errors.Count == 0;
                if (errors.Count == 0) okCount++;
                else r["error"] = string.Join("; ", errors);
                foreach (var kv in info) r[kv.Key] = kv.Value;
                results.Add(r);
            }
            var res = ToolResult.Ok(new { ops = ops.List.Count, succeeded = okCount, failed = ops.List.Count - okCount, results = results });
            foreach (var w in warnings) res.Warn(w);
            if (okCount < ops.List.Count) res.Warn(string.Format("{0} of {1} operations failed; successful ones were applied (layer edits cannot be reverted with history_undo).", ops.List.Count - okCount, ops.List.Count));
            return res;
        }

        sealed class Parsed { public List<Op> List = new List<Op>(); public List<string> Errors = new List<string>(); }

        static Parsed ParseOps(JObject args)
        {
            var p = new Parsed();
            var arr = args == null ? null : args["ops"] as JArray;
            if (arr == null || arr.Count == 0) { p.Errors.Add("'ops' is required (non-empty array of operations)"); return p; }
            if (arr.Count > 200) { p.Errors.Add("at most 200 operations per call"); return p; }
            for (int i = 0; i < arr.Count; i++)
            {
                string w = "ops[" + i + "]: ";
                var o = arr[i] as JObject;
                if (o == null) { p.Errors.Add(w + "must be an object"); continue; }
                var op = new Op { I = i };
                op.Kind = ((string)o["op"] ?? "").ToLowerInvariant();
                op.Path = ((string)o["path"] ?? "").Trim();
                if (Array.IndexOf(new[] { "create", "rename", "move", "set", "delete", "set_current" }, op.Kind) < 0)
                { p.Errors.Add(w + "'op' must be one of create, rename, move, set, delete, set_current"); continue; }
                string pe = LayerUtil.ValidatePath(op.Path);
                if (pe != null) p.Errors.Add(w + pe);
                if (op.Kind == "rename")
                {
                    op.NewName = (string)o["new_name"];
                    if (string.IsNullOrWhiteSpace(op.NewName) || op.NewName != op.NewName.Trim() || op.NewName.Contains(LayerUtil.Sep) || !Layer.IsValidName(op.NewName))
                        p.Errors.Add(w + "rename needs 'new_name': a single valid layer name without '::' or surrounding spaces");
                }
                if (op.Kind == "move")
                {
                    JToken pt;
                    if (!o.TryGetValue("parent", out pt) || pt.Type != JTokenType.String) p.Errors.Add(w + "move needs 'parent' (layer path, or \"\" for top level)");
                    else
                    {
                        op.Parent = ((string)pt).Trim();
                        if (op.Parent.Length > 0) { var e = LayerUtil.ValidatePath(op.Parent); if (e != null) p.Errors.Add(w + "parent: " + e); }
                    }
                }
                var errs = new List<string>();
                op.Props = LayerProps.Read(o, errs, w);
                p.Errors.AddRange(errs);
                if (op.Kind == "set" && op.Props.Color == null && op.Props.Linetype == null && op.Props.Width == null && op.Props.Plot == null && op.Props.Visible == null && op.Props.Locked == null)
                    p.Errors.Add(w + "set needs at least one of color, linetype, print_width_mm, plot, visible, locked");
                JToken rt, ct;
                op.Recursive = o.TryGetValue("recursive", out rt) && rt.Type == JTokenType.Boolean && (bool)rt;
                if (o.TryGetValue("confirm_objects", out ct) && ct.Type != JTokenType.Null)
                {
                    if (ct.Type == JTokenType.Integer && (long)ct >= 0) op.Confirm = (int)(long)ct;
                    else p.Errors.Add(w + "confirm_objects must be a non-negative integer");
                }
                p.List.Add(op);
            }
            return p;
        }

        static void Run(RhinoDoc doc, Op op, Dictionary<string, object> info, List<string> warnings, List<string> errors)
        {
            var tree = LayerTree.Build(doc, op.Kind == "delete");
            Layer l = tree.Find(op.Path);
            switch (op.Kind)
            {
                case "create":
                    {
                        var created = new List<string>();
                        string err;
                        if (l == null)
                        {
                            l = LayerUtil.Ensure(doc, op.Path, created, out err);
                            if (l == null) { errors.Add(err); return; }
                        }
                        var ch = op.Props.Apply(doc, l, warnings, errors);
                        info["created"] = created;
                        info["existed"] = created.Count == 0;
                        if (ch.Count > 0) info["changed"] = ch;
                        return;
                    }
                case "set":
                    {
                        if (l == null) { errors.Add(NotFound(op.Path)); return; }
                        info["changed"] = op.Props.Apply(doc, l, warnings, errors);
                        return;
                    }
                case "set_current":
                    {
                        if (l == null) { errors.Add(NotFound(op.Path)); return; }
                        if (!doc.Layers.SetCurrentLayerIndex(l.Index, true)) errors.Add("Rhino refused to make '" + op.Path + "' current");
                        return;
                    }
                case "rename":
                    {
                        if (l == null) { errors.Add(NotFound(op.Path)); return; }
                        if (string.Equals(l.Name, op.NewName, StringComparison.Ordinal)) { info["unchanged"] = true; return; }
                        string np = LayerUtil.Parent(l.FullPath);
                        np = np.Length == 0 ? op.NewName : np + LayerUtil.Sep + op.NewName;
                        var clash = tree.Find(np);
                        if (clash != null && clash.Id != l.Id) { errors.Add("a layer named '" + np + "' already exists"); return; }
                        l.Name = op.NewName;
                        if (l.Name != op.NewName) { errors.Add("Rhino refused the new name '" + op.NewName + "'"); return; }
                        info["new_path"] = l.FullPath;
                        return;
                    }
                case "move":
                    {
                        if (l == null) { errors.Add(NotFound(op.Path)); return; }
                        Layer parent = null;
                        var created = new List<string>();
                        if (op.Parent.Length > 0)
                        {
                            parent = tree.Find(op.Parent);
                            if (parent == null)
                            {
                                string err;
                                parent = LayerUtil.Ensure(doc, op.Parent, created, out err);
                                if (parent == null) { errors.Add(err); return; }
                            }
                            if (parent.Id == l.Id || l.IsParentOf(parent)) { errors.Add("cannot move a layer into itself or one of its sublayers"); return; }
                        }
                        string target = parent == null ? l.Name : parent.FullPath + LayerUtil.Sep + l.Name;
                        var clash = tree.Find(target);
                        if (clash != null && clash.Id != l.Id) { errors.Add("a layer named '" + target + "' already exists; rename first"); return; }
                        l.ParentLayerId = parent == null ? Guid.Empty : parent.Id;
                        if (l.ParentLayerId != (parent == null ? Guid.Empty : parent.Id)) { errors.Add("Rhino refused the move"); return; }
                        info["new_path"] = l.FullPath;
                        if (created.Count > 0) info["created"] = created;
                        return;
                    }
                case "delete":
                    {
                        if (l == null) { errors.Add(NotFound(op.Path)); return; }
                        var sub = tree.Subtree(l);
                        if (sub.Count > 1 && !op.Recursive) { errors.Add("layer has " + (sub.Count - 1) + " sublayer(s); pass recursive:true to delete them too"); return; }
                        int cur = doc.Layers.CurrentLayerIndex;
                        if (sub.Any(x => x.Index == cur)) { errors.Add("the current layer is inside this subtree and cannot be deleted; set_current on another layer first"); return; }
                        if (sub.Any(x => x.IsReference)) { errors.Add("subtree contains a reference layer, which cannot be deleted"); return; }
                        if (doc.Layers.ActiveCount - sub.Count < 1) { errors.Add("a document needs at least one layer"); return; }
                        int count = tree.Total[l.Id];
                        if (count > 0 && op.Confirm != count)
                        {
                            errors.Add("layer holds " + count + " object(s) (incl. sublayers); to delete them with the layer pass confirm_objects: " + count +
                                       ". To keep them, move them first with layers_move_objects.");
                            info["objects"] = count;
                            return;
                        }
                        var ids = new HashSet<int>(sub.Select(x => x.Index));
                        int deletedObjects = 0;
                        if (count > 0)
                        {
                            var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true };
                            var del = doc.Objects.GetObjectList(settings).Where(o => ids.Contains(o.Attributes.LayerIndex)).Select(o => o.Id).ToList();
                            foreach (var id in del) if (doc.Objects.Delete(id, true)) deletedObjects++;
                        }
                        // deepest first
                        foreach (var x in sub.OrderByDescending(s => LayerTree.Depth(s)).ToList())
                            if (!doc.Layers.Delete(x.Index, true)) { errors.Add("Rhino could not delete '" + x.FullPath + "' (locked or still holding objects?)"); return; }
                        info["deleted_layers"] = sub.Count;
                        info["deleted_objects"] = deletedObjects;
                        return;
                    }
            }
        }

        static string NotFound(string path) { return "layer '" + path + "' not found. Call layers_list to see the paths."; }
    }
}

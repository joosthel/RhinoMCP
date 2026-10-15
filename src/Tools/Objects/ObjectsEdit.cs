using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Objects
{
    public sealed class ObjectsGet : ITool
    {
        public string Name { get { return "objects_get"; } }
        public string Description
        {
            get
            {
                return "Detailed info for up to 50 objects: type, layer, name, color (effective + source), bbox, validity (short reason if invalid), " +
                       "closed/solid, length/area/volume where defined, locked/visible, user text, block definition name for instances. " +
                       "Use objects_query first to find handles; this tool is for inspection, not discovery.";
            }
        }
        public string InputSchema
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{\"target\":" + ObjectQuery.TargetSchema +
                       ",\"include_guids\":{\"type\":\"boolean\",\"default\":false}},\"required\":[\"target\"],\"additionalProperties\":false}";
            }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            if (ctx.Doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            bool guids = a.Bool("include_guids");
            var objs = ObjectQuery.Resolve(ctx, args == null ? null : args["target"], a);
            if (a.HasErrors) return a.ErrorResult();
            if (objs.Count > 50)
                return ToolResult.Fail("INVALID_ARGS", "Target resolved to " + objs.Count + " objects; objects_get accepts at most 50.",
                    "Narrow the target, or use objects_query (counts, group_by, paged rows) for bulk overviews.");
            var doc = ctx.Doc;
            var list = new List<object>();
            foreach (var o in objs)
            {
                var d = new Dictionary<string, object>();
                d["handle"] = ctx.Handles.For(o.Id);
                if (guids) d["guid"] = o.Id.ToString();
                d["type"] = ObjectQuery.TypeName(o);
                d["layer"] = ObjectQuery.LayerPath(doc, o);
                d["name"] = o.Attributes.Name ?? "";
                d["color_source"] = o.Attributes.ColorSource.ToString();
                try { d["color"] = ColorHex(o, doc); }
                catch (MissingMethodException) { d["color"] = null; }
                d["visible"] = o.Visible;
                d["locked"] = o.IsLocked;
                d["selected"] = o.IsSelected(false) != 0;
                d["bbox"] = ObjectQuery.BoxJson(o.Geometry.GetBoundingBox(true));
                string log;
                bool valid = o.Geometry.IsValidWithLog(out log);
                d["valid"] = valid;
                if (!valid) d["invalid_reason"] = Short(log);
                var g = o.Geometry;
                var crv = g as Curve; var brep = g as Brep; var ext = g as Extrusion; var mesh = g as Mesh;
                if (crv != null) d["closed"] = crv.IsClosed;
                if (brep != null) d["solid"] = brep.IsSolid;
                if (ext != null) d["solid"] = ext.IsSolid;
                if (mesh != null) d["closed"] = mesh.IsClosed;
                double? len, area, vol;
                ObjectQuery.Measure(o, out len, out area, out vol);
                if (len.HasValue) d["length"] = Math.Round(len.Value, 6);
                if (area.HasValue) d["area"] = Math.Round(area.Value, 6);
                if (vol.HasValue) d["volume"] = Math.Round(vol.Value, 6);
                var inst = o as InstanceObject;
                if (inst != null && inst.InstanceDefinition != null) d["block"] = inst.InstanceDefinition.Name;
                var ut = ObjectQuery.UserText(o);
                if (ut.Count > 0) d["user_text"] = ut;
                list.Add(d);
            }
            var res = ToolResult.Ok(new { count = list.Count, objects = list });
            res.Units = ctx.Units;
            return res;
        }

        // System.Drawing members live in separate non-inlined methods so a binding problem with them cannot break the whole tool.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string ColorHex(RhinoObject o, RhinoDoc doc)
        {
            return ObjectQuery.Hex(o.Attributes.DrawColor(doc));
        }

        static string Short(string log)
        {
            if (string.IsNullOrEmpty(log)) return "invalid (no reason given)";
            string one = log.Replace("\r", " ").Replace("\n", " ").Trim();
            return one.Length > 160 ? one.Substring(0, 160) + "..." : one;
        }
    }

    public sealed class ObjectsSetProps : ITool
    {
        public string Name { get { return "objects_set_props"; } }
        public string Description
        {
            get
            {
                return "Set properties on target objects: layer (must already exist, full path; error lists valid layers), name (\"\" clears), " +
                       "color (\"#rrggbb\", [r,g,b] or \"bylayer\"), user_text {set:{k:v}, remove:[k]}, visible, locked. " +
                       "Everything is validated before anything changes. Returns counts of modified objects (one undo step).";
            }
        }
        public string InputSchema
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{\"target\":" + ObjectQuery.TargetSchema + "," +
                       "\"layer\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"}," +
                       "\"color\":{\"description\":\"#rrggbb | [r,g,b] | bylayer\"}," +
                       "\"user_text\":{\"type\":\"object\",\"properties\":{\"set\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}},\"remove\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},\"additionalProperties\":false}," +
                       "\"visible\":{\"type\":\"boolean\"},\"locked\":{\"type\":\"boolean\"}," +
                       "\"include_guids\":{\"type\":\"boolean\",\"default\":false}},\"required\":[\"target\"],\"additionalProperties\":false}";
            }
        }
        public ToolKind Kind { get { return ToolKind.Mutate; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            var objs = ObjectQuery.Resolve(ctx, args == null ? null : args["target"], a);
            string layerName = a.String("layer");
            bool hasName = a.Has("name"); string name = a.String("name");
            bool? visible = a.Has("visible") ? (bool?)a.Bool("visible") : null;
            bool? locked = a.Has("locked") ? (bool?)a.Bool("locked") : null;
            bool guids = a.Bool("include_guids");

            int layerIndex = -1;
            if (layerName != null)
            {
                layerIndex = doc.Layers.FindByFullPath(layerName, -1);
                if (layerIndex < 0)
                {
                    var leaf = new List<int>();
                    for (int i = 0; i < doc.Layers.Count; i++)
                        if (!doc.Layers[i].IsDeleted && string.Equals(doc.Layers[i].Name, layerName, StringComparison.OrdinalIgnoreCase)) leaf.Add(i);
                    if (leaf.Count == 1) layerIndex = leaf[0];
                    else if (leaf.Count > 1) a.Error("'layer': '" + layerName + "' is ambiguous; use the full path. Existing layers: " + ObjectsQuery.LayerList(doc));
                }
                if (layerIndex < 0 && !a.HasErrors)
                    a.Error("'layer': no layer '" + layerName + "'. Layers are not created implicitly. Existing layers: " + ObjectsQuery.LayerList(doc));
            }

            int rgb = -1; bool byLayer = false;
            JToken ct;
            if (args != null && args.TryGetValue("color", out ct) && ct.Type != JTokenType.Null)
            {
                if (ct.Type == JTokenType.String && string.Equals((string)ct, "bylayer", StringComparison.OrdinalIgnoreCase)) byLayer = true;
                else
                {
                    if (!TryRgb(ct, out rgb)) rgb = -1;
                    if (rgb < 0) a.Error("'color' must be \"#rrggbb\", [r,g,b] (0-255) or \"bylayer\"");
                }
            }

            var utSet = new Dictionary<string, string>(); var utRemove = new List<string>();
            JToken ut;
            if (args != null && args.TryGetValue("user_text", out ut) && ut.Type != JTokenType.Null)
            {
                var uo = ut as JObject;
                if (uo == null) a.Error("'user_text' must be {set:{key:value}, remove:[key]}");
                else
                {
                    var st = uo["set"] as JObject;
                    if (uo["set"] != null && st == null) a.Error("'user_text.set' must be an object of string values");
                    if (st != null) foreach (var p in st.Properties()) utSet[p.Name] = p.Value.Type == JTokenType.String ? (string)p.Value : p.Value.ToString(Newtonsoft.Json.Formatting.None);
                    var rm = uo["remove"] as JArray;
                    if (uo["remove"] != null && rm == null) a.Error("'user_text.remove' must be an array of keys");
                    if (rm != null) utRemove.AddRange(rm.Select(x => (string)x));
                }
            }
            bool any = layerName != null || hasName || (rgb >= 0) || byLayer || utSet.Count > 0 || utRemove.Count > 0 || visible.HasValue || locked.HasValue;
            if (!any && !a.HasErrors) a.Error("nothing to change; give at least one of layer, name, color, user_text, visible, locked");
            if (a.HasErrors) return a.ErrorResult();

            var modified = new List<RhinoObject>();
            int failed = 0;
            foreach (var o in objs)
            {
                bool changed = false, ok = true;
                bool attrChange = layerIndex >= 0 || hasName || (rgb >= 0) || byLayer || utSet.Count > 0 || utRemove.Count > 0;
                if (attrChange)
                {
                    var at = o.Attributes.Duplicate();
                    if (layerIndex >= 0) at.LayerIndex = layerIndex;
                    if (hasName) at.Name = name ?? "";
                    if (rgb >= 0)
                    {
                        try { SetObjectColor(at, rgb); }
                        catch (MissingMethodException) { return ToolResult.Fail("UNSUPPORTED", "Setting an explicit object color is not available in this Rhino session (System.Drawing binding failed).", "Use color:\"bylayer\" and change the layer color instead."); }
                    }
                    if (byLayer) at.ColorSource = ObjectColorSource.ColorFromLayer;
                    foreach (var kv in utSet) at.SetUserString(kv.Key, kv.Value);
                    foreach (var k in utRemove) at.DeleteUserString(k);
                    if (doc.Objects.ModifyAttributes(o, at, true)) changed = true; else ok = false;
                }
                var cur = doc.Objects.FindId(o.Id);
                if (cur != null && visible.HasValue)
                {
                    if (visible.Value) { if (cur.IsHidden) { if (doc.Objects.Show(cur, true)) changed = true; else ok = false; } }
                    else if (!cur.IsHidden) { if (doc.Objects.Hide(cur, true)) changed = true; else ok = false; }
                }
                cur = doc.Objects.FindId(o.Id);
                if (cur != null && locked.HasValue)
                {
                    if (locked.Value) { if (!cur.IsLocked) { if (doc.Objects.Lock(cur, true)) changed = true; else ok = false; } }
                    else if (cur.IsLocked) { if (doc.Objects.Unlock(cur, true)) changed = true; else ok = false; }
                }
                if (!ok) failed++;
                if (changed) modified.Add(o);
            }
            doc.Views.Redraw();
            var fresh = modified.Select(o => doc.Objects.FindId(o.Id)).Where(o => o != null).ToList();
            var data = new Dictionary<string, object>();
            data["targeted"] = objs.Count;
            data["modified"] = modified.Count;
            data["unchanged"] = objs.Count - modified.Count - failed;
            if (failed > 0) data["failed"] = failed;
            data["objects"] = ObjectQuery.Handles(fresh, 100, guids);
            data["bbox"] = ObjectQuery.BoxJson(ObjectQuery.BoxOf(fresh));
            var res = ToolResult.Ok(data);
            res.Units = ctx.Units;
            if (visible == true && fresh.Any(o => !o.Visible)) res.Warn("Some objects are still not visible because their layer is off or locked; change the layer state with the layer tools.");
            return res;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SetObjectColor(ObjectAttributes at, int rgb)
        {
            at.ObjectColor = System.Drawing.Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            at.ColorSource = ObjectColorSource.ColorFromObject;
        }

        static bool TryRgb(JToken t, out int rgb)
        {
            rgb = -1;
            if (t.Type == JTokenType.String)
            {
                string s = ((string)t).Trim().TrimStart('#');
                int v;
                if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) { rgb = v; return true; }
                return false;
            }
            var arr = t as JArray;
            if (arr != null && arr.Count == 3 && arr.All(x => x.Type == JTokenType.Integer && (long)x >= 0 && (long)x <= 255))
            { rgb = ((int)arr[0] << 16) | ((int)arr[1] << 8) | (int)arr[2]; return true; }
            return false;
        }
    }

    public sealed class ObjectsDelete : ITool
    {
        public string Name { get { return "objects_delete"; } }
        public string Description
        {
            get
            {
                return "Delete target objects (undoable with history_undo). Refuses more than 1000 objects unless confirm_count equals the exact number " +
                       "(the error tells you the count). Locked objects are deleted too.";
            }
        }
        public string InputSchema
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{\"target\":" + ObjectQuery.TargetSchema +
                       ",\"confirm_count\":{\"type\":\"integer\",\"description\":\"Required when the target has more than 1000 objects; must equal the count\"}},\"required\":[\"target\"],\"additionalProperties\":false}";
            }
        }
        public ToolKind Kind { get { return ToolKind.Destructive; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            var objs = ObjectQuery.Resolve(ctx, args == null ? null : args["target"], a);
            int confirm = a.Int("confirm_count", false, -1);
            if (a.HasErrors) return a.ErrorResult();
            if (objs.Count > 1000 && confirm != objs.Count)
                return ToolResult.Fail("CONFIRMATION_REQUIRED", "Target resolves to " + objs.Count + " objects (limit without confirmation is 1000).",
                    "Call again with confirm_count=" + objs.Count + " if you really mean to delete all of them, or narrow the target.",
                    new { count = objs.Count });
            var bbox = ObjectQuery.BoxJson(ObjectQuery.BoxOf(objs));
            var deleted = new List<string>();
            int failed = 0;
            foreach (var o in objs)
            {
                string h = ctx.Handles.For(o.Id);
                if (doc.Objects.Delete(new ObjRef(o), true, true)) deleted.Add(h); else failed++;
            }
            doc.Views.Redraw();
            var data = new Dictionary<string, object>();
            data["requested"] = objs.Count;
            data["deleted"] = deleted.Count;
            if (failed > 0) data["failed"] = failed;
            data["handles"] = deleted.Take(100).ToList();
            if (deleted.Count > 100) data["truncated"] = true;
            data["bbox"] = bbox;
            var res = ToolResult.Ok(data);
            res.Units = ctx.Units;
            return res;
        }
    }

    public sealed class ObjectsSelect : ITool
    {
        public string Name { get { return "objects_select"; } }
        public string Description
        {
            get
            {
                return "Select or deselect target objects in the Rhino viewports (action select|deselect, default select). clear_first:true deselects everything first. " +
                       "Hidden or locked objects cannot be selected and are reported in not_selectable. Selection is not part of undo.";
            }
        }
        public string InputSchema
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{\"target\":" + ObjectQuery.TargetSchema +
                       ",\"action\":{\"enum\":[\"select\",\"deselect\"],\"default\":\"select\"},\"clear_first\":{\"type\":\"boolean\",\"default\":false}},\"required\":[\"target\"],\"additionalProperties\":false}";
            }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            var objs = ObjectQuery.Resolve(ctx, args == null ? null : args["target"], a);
            string action = a.String("action", false, "select");
            bool clear = a.Bool("clear_first");
            if (action != "select" && action != "deselect") a.Error("'action' must be 'select' or 'deselect'");
            if (a.HasErrors) return a.ErrorResult();
            if (clear) doc.Objects.UnselectAll();
            bool sel = action == "select";
            int changed = 0, notSelectable = 0;
            foreach (var o in objs)
            {
                var cur = doc.Objects.FindId(o.Id);
                if (cur == null) continue;
                if (sel)
                {
                    cur.Select(true);
                    if (cur.IsSelected(false) != 0) changed++; else notSelectable++;
                }
                else { cur.Select(false); changed++; }
            }
            doc.Views.Redraw();
            int total = doc.Objects.GetSelectedObjects(false, false).Count();
            var data = new Dictionary<string, object>();
            data["action"] = action;
            data["targeted"] = objs.Count;
            data[sel ? "selected" : "deselected"] = changed;
            if (notSelectable > 0) data["not_selectable"] = notSelectable;
            data["selection_total"] = total;
            return ToolResult.Ok(data);
        }
    }
}

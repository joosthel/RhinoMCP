using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Objects
{
    public sealed class ObjectsQuery : ITool
    {
        static readonly string[] FieldNames = { "handle", "type", "layer", "name", "bbox", "user_text" };

        public string Name { get { return "objects_query"; } }
        public string Description
        {
            get
            {
                return "Find objects by filters:{layer, include_sublayers, type, name glob, user_text, bbox, selected, visible, include_hidden}, combined with AND; hidden objects are included by default; unknown arguments are rejected.Always returns total and group counts " +
                       "(group_by layer|type|user_text:<key>, default type); rows only when limit>0 (default 50, max 500, use cursor to page). " +
                       "measure_sums:true adds length/area/volume sums per group (slower on big models). save_as_set stores ALL matches as a named set. " +
                       "Types: curve|surface|brep|extrusion|mesh|subd|point|annotation|instance|hatch. Returns handles (o12); pass include_guids for GUIDs.";
            }
        }
        public string InputSchema
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{" +
                       "\"filters\":" + ObjectQuery.FilterSchema + "," +
                       "\"group_by\":{\"type\":\"string\",\"description\":\"layer | type | user_text:<key>\",\"default\":\"type\"}," +
                       "\"measure_sums\":{\"type\":\"boolean\",\"default\":false}," +
                       "\"fields\":{\"type\":\"array\",\"items\":{\"enum\":[\"handle\",\"type\",\"layer\",\"name\",\"bbox\",\"user_text\"]},\"default\":[\"handle\",\"type\",\"layer\",\"name\"]}," +
                       "\"limit\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500,\"default\":50}," +
                       "\"cursor\":{\"type\":\"string\"}," +
                       "\"save_as_set\":{\"type\":\"string\",\"description\":\"Name of a set to create/overwrite with all matches\"}," +
                       "\"include_guids\":{\"type\":\"boolean\",\"default\":false}" +
                       "},\"additionalProperties\":false}";
            }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            // Filter keys belong inside "filters"; a top-level layer/name/type used to be silently ignored (so everything matched).
            ArgCheck.RejectUnknown(args, new[] { "filters", "group_by", "measure_sums", "fields", "limit", "cursor", "save_as_set", "include_guids" }, a,
                k => Array.IndexOf(new[] { "layer", "include_sublayers", "type", "name", "user_text", "bbox", "selected", "visible", "include_hidden" }, k) >= 0
                    ? "filter keys go inside \"filters\": {\"filters\":{\"" + k + "\": ...}}" : null);
            int limit = a.Int("limit", false, 50, 0, 500);
            string cursor = a.String("cursor");
            string groupBy = a.String("group_by", false, "type");
            bool measure = a.Bool("measure_sums");
            bool guids = a.Bool("include_guids");
            string saveAs = a.String("save_as_set");
            int offset = 0;
            if (cursor != null && (!int.TryParse(cursor, out offset) || offset < 0)) a.Error("'cursor' is invalid; pass next_cursor from a previous response");
            if (saveAs != null) { var e = ObjectQuery.ValidateSetName(saveAs); if (e != null) a.Error("'save_as_set': " + e); }

            string gb = (groupBy ?? "type").Trim();
            string userKey = null;
            if (gb.StartsWith("user_text:", StringComparison.Ordinal) && gb.Length > 10) userKey = gb.Substring(10);
            else if (gb != "layer" && gb != "type") a.Error("'group_by' must be layer, type or user_text:<key>");

            var fields = new List<string> { "handle", "type", "layer", "name" };
            JToken ft;
            if (args != null && args.TryGetValue("fields", out ft) && ft.Type != JTokenType.Null)
            {
                var fa = ft as JArray;
                if (fa == null) a.Error("'fields' must be an array of: " + string.Join(", ", FieldNames));
                else
                {
                    fields = fa.Select(x => (string)x).ToList();
                    foreach (var f in fields) if (!FieldNames.Contains(f)) a.Error("'fields': unknown field '" + f + "'. Valid: " + string.Join(", ", FieldNames));
                }
            }

            ObjectFilter filter = new ObjectFilter();
            JToken fl;
            if (args != null && args.TryGetValue("filters", out fl) && fl.Type != JTokenType.Null)
            {
                if (!(fl is JObject)) a.Error("'filters' must be an object");
                else filter = ObjectFilter.Parse((JObject)fl, e => a.Error(e), "'filters': ");
            }
            if (filter.Layer != null && !a.HasErrors && FindLayers(doc, filter.Layer) == 0)
                a.Error("'filters.layer': no layer '" + filter.Layer + "'. Closest existing layers: " + LayerList(doc, filter.Layer));
            if (a.HasErrors) return a.ErrorResult();

            var matches = ObjectQuery.Run(doc, filter);

            // Group counts (and optional sums).
            var groups = new Dictionary<string, Group>();
            foreach (var o in matches)
            {
                string key = userKey != null ? (o.Attributes.GetUserString(userKey) ?? "(none)")
                           : gb == "layer" ? ObjectQuery.LayerPath(doc, o)
                           : ObjectQuery.TypeName(o);
                Group g;
                if (!groups.TryGetValue(key, out g)) { g = new Group { Key = key }; groups[key] = g; }
                g.Count++;
                if (measure)
                {
                    double? len, ar, vol;
                    ObjectQuery.Measure(o, out len, out ar, out vol);
                    if (len.HasValue) g.Length += len.Value;
                    if (ar.HasValue) g.Area += ar.Value;
                    if (vol.HasValue) g.Volume += vol.Value;
                }
            }
            var ordered = groups.Values.OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            var groupRows = new List<object>();
            foreach (var g in ordered.Take(50))
            {
                var row = new Dictionary<string, object> { { "key", g.Key }, { "count", g.Count } };
                if (measure) { row["length_sum"] = Math.Round(g.Length, 6); row["area_sum"] = Math.Round(g.Area, 6); row["volume_sum"] = Math.Round(g.Volume, 6); }
                groupRows.Add(row);
            }

            var data = new Dictionary<string, object>();
            data["total"] = matches.Count;
            data["group_by"] = gb;
            data["groups"] = groupRows;
            if (ordered.Count > 50) data["groups_truncated"] = ordered.Count - 50;
            if (measure) data["sums_note"] = "length: curves; area: closed planar curves, surfaces, breps, meshes, hatches; volume: closed solids only. Units are document units (squared/cubed).";

            string next = null;
            if (limit > 0)
            {
                var page = matches.Skip(offset).Take(limit).ToList();
                var rows = new List<object>();
                foreach (var o in page)
                {
                    var r = new Dictionary<string, object>();
                    foreach (var f in fields)
                    {
                        switch (f)
                        {
                            case "handle": r["handle"] = ctx.Handles.For(o.Id); if (guids) r["guid"] = o.Id.ToString(); break;
                            case "type": r["type"] = ObjectQuery.TypeName(o); break;
                            case "layer": r["layer"] = ObjectQuery.LayerPath(doc, o); break;
                            case "name": r["name"] = o.Attributes.Name ?? ""; break;
                            case "bbox": r["bbox"] = ObjectQuery.BoxJson(o.Geometry.GetBoundingBox(true)); break;
                            case "user_text": r["user_text"] = ObjectQuery.UserText(o); break;
                        }
                    }
                    if (!fields.Contains("handle") && guids) r["guid"] = o.Id.ToString();
                    rows.Add(r);
                }
                data["rows"] = rows;
                data["returned"] = rows.Count;
                if (offset + rows.Count < matches.Count) next = (offset + rows.Count).ToString();
            }
            else data["returned"] = 0;

            if (saveAs != null)
            {
                ObjectQuery.StoreSet(doc, saveAs, matches.Select(o => o.Id));
                data["saved_set"] = new { name = saveAs, count = matches.Count };
            }
            var res = ToolResult.Ok(data);
            res.Units = ctx.Units;
            res.NextCursor = next;
            return res;
        }

        sealed class Group { public string Key; public int Count; public double Length, Area, Volume; }

        internal static int FindLayers(RhinoDoc doc, string layer)
        {
            int n = 0;
            for (int i = 0; i < doc.Layers.Count; i++)
            {
                var l = doc.Layers[i];
                if (l.IsDeleted) continue;
                if (string.Equals(l.FullPath, layer, StringComparison.OrdinalIgnoreCase) || string.Equals(l.Name, layer, StringComparison.OrdinalIgnoreCase)) n++;
            }
            return n;
        }

        /// <summary>Existing layer paths, closest to <paramref name="wanted"/> first (so typos surface the right layer).</summary>
        internal static string LayerList(RhinoDoc doc, string wanted = null)
        {
            var names = new List<string>();
            for (int i = 0; i < doc.Layers.Count; i++) { var l = doc.Layers[i]; if (!l.IsDeleted) names.Add(l.FullPath); }
            const int Max = 25;
            if (string.IsNullOrEmpty(wanted)) names.Sort(StringComparer.Ordinal);
            else
            {
                string w = wanted.ToLowerInvariant();
                string wLeaf = Leaf(w);
                names = names.OrderBy(n =>
                {
                    string ln = n.ToLowerInvariant();
                    string leaf = Leaf(ln);
                    int d = Math.Min(Lev(w, ln), Lev(wLeaf, leaf));
                    if (ln.Contains(wLeaf) || wLeaf.Contains(leaf)) d -= 3;
                    return d;
                }).ThenBy(n => n, StringComparer.Ordinal).ToList();
            }
            return string.Join(", ", names.Take(Max)) + (names.Count > Max ? ", ... (" + names.Count + " total; use layers_list)" : "");
        }

        static string Leaf(string path)
        {
            int i = path.LastIndexOf("::", StringComparison.Ordinal);
            return i < 0 ? path : path.Substring(i + 2);
        }

        static int Lev(string a, string b)
        {
            if (a.Length > 64) a = a.Substring(0, 64);
            if (b.Length > 64) b = b.Substring(0, 64);
            var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }
}

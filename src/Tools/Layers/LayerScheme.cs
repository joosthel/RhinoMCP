using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCP.Tools.Layers
{
    internal sealed class SchemeLayer
    {
        public string Path;          // as written in the scheme (uses the scheme delimiter)
        public string RhinoPath;     // full Rhino layer path ('::' hierarchy)
        public LayerProps Props;
        public bool Required;
    }

    internal sealed class Scheme
    {
        public string Name, Delimiter;
        public List<SchemeLayer> Layers = new List<SchemeLayer>();
        public JObject Json;
        public bool IsBuiltIn;

        public static readonly string[] BuiltInIds = { "AIA-NCS", "ISO-13567" };

        /// <summary>Layer paths plus implicit parents (hierarchical schemes only).</summary>
        public HashSet<string> AllPaths()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in Layers)
            {
                var segs = LayerUtil.Split(l.RhinoPath);
                string cum = "";
                foreach (var s in segs) { cum = cum.Length == 0 ? s : cum + LayerUtil.Sep + s; set.Add(cum); }
            }
            return set;
        }

        public static Scheme Parse(JObject o, List<string> errs)
        {
            var s = new Scheme { Json = o };
            s.Name = ((string)o["name"] ?? "").Trim();
            s.Delimiter = (string)o["delimiter"];
            if (s.Name.Length == 0) errs.Add("scheme.name is required");
            if (string.IsNullOrEmpty(s.Delimiter)) errs.Add("scheme.delimiter is required (e.g. '::' or '-')");
            var arr = o["layers"] as JArray;
            if (arr == null || arr.Count == 0) { errs.Add("scheme.layers must be a non-empty array"); return s; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < arr.Count; i++)
            {
                string w = "scheme.layers[" + i + "]: ";
                var lo = arr[i] as JObject;
                if (lo == null) { errs.Add(w + "must be an object"); continue; }
                var sl = new SchemeLayer();
                sl.Path = ((string)lo["path"] ?? "").Trim();
                if (sl.Path.Length == 0) { errs.Add(w + "path is empty"); continue; }
                sl.RhinoPath = sl.Path;
                if (s.Delimiter != null && s.Delimiter != LayerUtil.Sep)
                {
                    if (sl.Path.Contains(LayerUtil.Sep)) { errs.Add(w + "path '" + sl.Path + "' contains '::' but the scheme delimiter is '" + s.Delimiter + "' (flat layer names)"); continue; }
                }
                else if (s.Delimiter == LayerUtil.Sep) { }
                string pe = LayerUtil.ValidatePath(sl.RhinoPath);
                if (pe != null) { errs.Add(w + pe); continue; }
                if (!seen.Add(sl.RhinoPath)) { errs.Add(w + "duplicate path '" + sl.Path + "'"); continue; }
                sl.Props = LayerProps.Read(lo, errs, w);
                JToken rq;
                sl.Required = lo.TryGetValue("required", out rq) && rq.Type == JTokenType.Boolean && (bool)rq;
                s.Layers.Add(sl);
            }
            return s;
        }

        /// <summary>Resolves a scheme argument (built-in id or object) or the one stored in the document.</summary>
        public static Scheme Resolve(JToken tok, RhinoDoc doc, List<string> errs, List<string> warnings, out string source)
        {
            source = null;
            if (tok != null && tok.Type == JTokenType.Object)
            {
                source = "inline";
                return Parse((JObject)tok, errs);
            }
            string id = tok != null && tok.Type == JTokenType.String ? (string)tok : null;
            if (tok != null && tok.Type != JTokenType.String && tok.Type != JTokenType.Null) { errs.Add("'scheme' must be a built-in id (" + string.Join(", ", BuiltInIds) + ") or a scheme object"); return null; }
            if (id == null)
            {
                id = doc.Strings.GetValue(LayerUtil.SchemeKey);
                if (string.IsNullOrEmpty(id)) return null;
                source = "document";
                string json = doc.Strings.GetValue(LayerUtil.SchemeJsonKey);
                if (!string.IsNullOrEmpty(json) && !BuiltInIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var s = Parse(JObject.Parse(json), errs);
                        return s;
                    }
                    catch (Exception e) { warnings.Add("Stored scheme JSON is unreadable: " + e.Message); }
                }
            }
            else source = "built-in";
            var b = BuiltIn(id);
            if (b == null)
            {
                if (source == "document") warnings.Add("Document scheme id '" + id + "' is not a built-in and no scheme JSON is stored; pass 'scheme' explicitly.");
                else errs.Add("Unknown scheme id '" + id + "'. Built-ins: " + string.Join(", ", BuiltInIds) + ". Or pass a scheme object (see skills/rhino-layer-standards/references/scheme-format.md).");
            }
            return b;
        }

        public static Scheme BuiltIn(string id)
        {
            if (id == null) return null;
            JObject o = null;
            if (string.Equals(id, "AIA-NCS", StringComparison.OrdinalIgnoreCase)) o = Build("AIA-NCS", "-", Aia);
            else if (string.Equals(id, "ISO-13567", StringComparison.OrdinalIgnoreCase)) o = Build("ISO-13567", "_", Iso);
            if (o == null) return null;
            var errs = new List<string>();
            var s = Parse(o, errs);
            s.IsBuiltIn = true;
            return s;
        }

        // path, color, linetype, print width mm, required, plot
        static readonly object[][] Aia =
        {
            new object[] { "A-WALL", "#000000", "Continuous", 0.50, true, true },
            new object[] { "A-WALL-FULL", "#000000", "Continuous", 0.50, false, true },
            new object[] { "A-WALL-PRHT", "#404040", "Continuous", 0.25, false, true },
            new object[] { "A-DOOR", "#0070c0", "Continuous", 0.25, true, true },
            new object[] { "A-GLAZ", "#00a0a0", "Continuous", 0.25, true, true },
            new object[] { "A-FLOR", "#808080", "Continuous", 0.25, true, true },
            new object[] { "A-FLOR-STRS", "#808080", "Continuous", 0.25, false, true },
            new object[] { "A-CLNG", "#c0c0c0", "Continuous", 0.13, false, true },
            new object[] { "A-ROOF", "#804000", "Continuous", 0.35, false, true },
            new object[] { "A-COLS", "#000000", "Continuous", 0.50, false, true },
            new object[] { "A-AREA", "#00a000", "Continuous", 0.13, false, true },
            new object[] { "A-GRID", "#ff0000", "Dashdot", 0.13, false, true },
            new object[] { "A-ANNO-DIMS", "#ff0000", "Continuous", 0.13, true, true },
            new object[] { "A-ANNO-TEXT", "#ff0000", "Continuous", 0.13, true, true },
            new object[] { "A-ANNO-SYMB", "#ff00ff", "Continuous", 0.13, false, true },
            new object[] { "A-ANNO-TTLB", "#000000", "Continuous", 0.25, false, true },
            new object[] { "A-ANNO-NPLT", "#c0c0c0", "Continuous", 0.0, false, false }
        };
        // Illustrative only: agent_element_presentation. Verify against the national profile before delivering.
        static readonly object[][] Iso =
        {
            new object[] { "A_WALL_OUTLINE", "#000000", "Continuous", 0.50, true, true },
            new object[] { "A_WALL_HATCH", "#808080", "Continuous", 0.13, false, true },
            new object[] { "A_DOOR_OUTLINE", "#0070c0", "Continuous", 0.25, true, true },
            new object[] { "A_WINDOW_OUTLINE", "#00a0a0", "Continuous", 0.25, true, true },
            new object[] { "A_SLAB_OUTLINE", "#804000", "Continuous", 0.35, false, true },
            new object[] { "A_STAIR_OUTLINE", "#808080", "Continuous", 0.25, false, true },
            new object[] { "A_GRID_LINE", "#ff0000", "Dashdot", 0.13, false, true },
            new object[] { "A_ROOM_TEXT", "#ff0000", "Continuous", 0.13, true, true },
            new object[] { "A_DIM_DIMENSION", "#ff0000", "Continuous", 0.13, true, true },
            new object[] { "A_HELP_NOPLOT", "#c0c0c0", "Continuous", 0.0, false, false }
        };

        static JObject Build(string name, string delim, object[][] rows)
        {
            var arr = new JArray();
            foreach (var r in rows)
                arr.Add(new JObject
                {
                    { "path", (string)r[0] }, { "color", (string)r[1] }, { "linetype", (string)r[2] },
                    { "print_width_mm", (double)r[3] }, { "required", (bool)r[4] }, { "plot", (bool)r[5] }
                });
            return new JObject { { "name", name }, { "delimiter", delim }, { "layers", arr } };
        }


        static readonly Dictionary<string, string> Synonyms = new Dictionary<string, string>
        {
            { "walls", "awall" }, { "wall", "awall" }, { "doors", "adoor" }, { "door", "adoor" },
            { "windows", "aglaz" }, { "window", "aglaz" }, { "glazing", "aglaz" }, { "glass", "aglaz" },
            { "floor", "aflor" }, { "floors", "aflor" }, { "slab", "aflor" }, { "stairs", "aflorstrs" }, { "stair", "aflorstrs" },
            { "roof", "aroof" }, { "ceiling", "aclng" }, { "ceilings", "aclng" }, { "columns", "acols" }, { "column", "acols" },
            { "grid", "agrid" }, { "grids", "agrid" }, { "dimensions", "aannodims" }, { "dims", "aannodims" }, { "dimension", "aannodims" },
            { "text", "aannotext" }, { "annotation", "aannotext" }, { "areas", "aarea" }, { "rooms", "aarea" }
        };

        /// <summary>Suggested scheme layer (Rhino path) for an existing layer path, or null. Matches on normalised names and common English synonyms.</summary>
        public string Suggest(string existingPath)
        {
            string leaf = LayerUtil.Leaf(existingPath);
            var keys = new List<string> { Norm(existingPath), Norm(leaf) };
            string nl = Norm(leaf);
            if (nl.EndsWith("s") && nl.Length > 2) keys.Add(nl.Substring(0, nl.Length - 1));
            string syn;
            if (Synonyms.TryGetValue(nl, out syn)) keys.Add(syn);
            foreach (var k in keys)
            {
                foreach (var l in Layers)
                {
                    if (Norm(l.RhinoPath) == k) return l.RhinoPath;
                }
            }
            foreach (var k in keys)
                foreach (var l in Layers)
                    if (Norm(LayerUtil.Leaf(l.RhinoPath)) == k) return l.RhinoPath;
            return null;
        }

        public static string Norm(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
    }
}

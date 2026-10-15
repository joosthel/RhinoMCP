using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;

namespace RhinoMCP.Tools.Layers
{
    /// <summary>Snapshot of the layer table with object counts. Built per tool call (cheap).</summary>
    internal sealed class LayerTree
    {
        public RhinoDoc Doc;
        public List<Layer> All = new List<Layer>();
        public Dictionary<Guid, Layer> ById = new Dictionary<Guid, Layer>();
        public Dictionary<Guid, List<Layer>> Kids = new Dictionary<Guid, List<Layer>>();
        public Dictionary<string, Layer> ByPath = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Guid, int> Direct = new Dictionary<Guid, int>();
        public Dictionary<Guid, int> Total = new Dictionary<Guid, int>();

        public static LayerTree Build(RhinoDoc doc, bool counts = true)
        {
            var t = new LayerTree { Doc = doc };
            for (int i = 0; i < doc.Layers.Count; i++)
            {
                var l = doc.Layers[i];
                if (l.IsDeleted) continue;
                t.All.Add(l);
                t.ById[l.Id] = l;
                t.ByPath[l.FullPath] = l;
                t.Direct[l.Id] = 0; t.Total[l.Id] = 0;
            }
            foreach (var l in t.All)
            {
                List<Layer> k;
                if (!t.Kids.TryGetValue(l.ParentLayerId, out k)) { k = new List<Layer>(); t.Kids[l.ParentLayerId] = k; }
                k.Add(l);
            }
            foreach (var k in t.Kids.Values) k.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            if (counts)
            {
                var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true };
                var byIndex = new Dictionary<int, int>();
                foreach (var o in doc.Objects.GetObjectList(settings))
                {
                    int c; int li = o.Attributes.LayerIndex;
                    byIndex.TryGetValue(li, out c); byIndex[li] = c + 1;
                }
                foreach (var l in t.All)
                {
                    int c;
                    if (!byIndex.TryGetValue(l.Index, out c) || c == 0) continue;
                    t.Direct[l.Id] = c;
                    var cur = l; int guard = 0;
                    while (cur != null && guard++ < 64)
                    {
                        t.Total[cur.Id] += c;
                        Layer p;
                        cur = t.ById.TryGetValue(cur.ParentLayerId, out p) ? p : null;
                    }
                }
            }
            return t;
        }

        public Layer Find(string path)
        {
            Layer l;
            return path != null && ByPath.TryGetValue(path.Trim(), out l) ? l : null;
        }
        public List<Layer> Children(Layer l)
        {
            List<Layer> k;
            return Kids.TryGetValue(l.Id, out k) ? k : new List<Layer>();
        }
        public List<Layer> Roots()
        {
            List<Layer> k;
            return Kids.TryGetValue(Guid.Empty, out k) ? k : new List<Layer>();
        }
        /// <summary>Depth-first, siblings sorted by name. Includes the start layers themselves.</summary>
        public IEnumerable<Layer> Walk(IEnumerable<Layer> start)
        {
            foreach (var l in start)
            {
                yield return l;
                foreach (var d in Walk(Children(l))) yield return d;
            }
        }
        public List<Layer> Subtree(Layer l) { return Walk(new[] { l }).ToList(); }
        public static int Depth(Layer l) { return LayerUtil.Split(l.FullPath).Length - 1; }
    }

    /// <summary>Settings that can be applied to a layer. Null members are left untouched.</summary>
    internal sealed class LayerProps
    {
        public Color? Color;
        public string Linetype;
        public double? Width;
        public bool? Plot, Visible, Locked;

        public static LayerProps Read(JObject o, List<string> errs, string where)
        {
            var p = new LayerProps();
            JToken t;
            if (o.TryGetValue("color", out t) && t.Type != JTokenType.Null)
            {
                Color c;
                if (t.Type == JTokenType.String && LayerUtil.TryHex((string)t, out c)) p.Color = c;
                else errs.Add(where + "color must be \"#rrggbb\"");
            }
            if (o.TryGetValue("linetype", out t) && t.Type != JTokenType.Null)
            {
                if (t.Type == JTokenType.String && ((string)t).Trim().Length > 0) p.Linetype = ((string)t).Trim();
                else errs.Add(where + "linetype must be a linetype name");
            }
            if (o.TryGetValue("print_width_mm", out t) && t.Type != JTokenType.Null)
            {
                if ((t.Type == JTokenType.Float || t.Type == JTokenType.Integer) && (double)t >= 0 && (double)t <= 20) p.Width = (double)t;
                else errs.Add(where + "print_width_mm must be a number in [0..20] (0 = default width)");
            }
            p.Plot = Bool(o, "plot", errs, where);
            p.Visible = Bool(o, "visible", errs, where);
            p.Locked = Bool(o, "locked", errs, where);
            return p;
        }
        static bool? Bool(JObject o, string k, List<string> errs, string where)
        {
            JToken t;
            if (!o.TryGetValue(k, out t) || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.Boolean) { errs.Add(where + k + " must be a boolean"); return null; }
            return (bool)t;
        }

        /// <summary>Applies the settings; returns the names of the properties that actually changed.</summary>
        public List<string> Apply(RhinoDoc doc, Layer l, List<string> warnings, List<string> errors, bool commit = true)
        {
            var changes = new List<string>();
            if (Color.HasValue && l.Color.ToArgb() != Color.Value.ToArgb()) { if (commit) l.Color = Color.Value; changes.Add("color"); }
            if (Linetype != null)
            {
                bool found;
                int idx = LayerUtil.ResolveLinetype(doc, Linetype, out found);
                if (!found)
                {
                    warnings.Add("Linetype '" + Linetype + "' not found in document (layer '" + l.FullPath + "'); using Continuous. Available: " + string.Join(", ", LayerUtil.LinetypeNames(doc)));
                    idx = -1;
                }
                string want = idx < 0 ? "Continuous" : doc.Linetypes[idx].Name;
                if (!string.Equals(LayerUtil.LinetypeName(doc, l), want, StringComparison.OrdinalIgnoreCase)) { if (commit) l.LinetypeIndex = idx; changes.Add("linetype"); }
            }
            double cur = l.PlotWeight, target = cur;
            if (Plot == false) target = -1;
            else if (Width.HasValue) target = Width.Value;
            else if (Plot == true && cur < 0) target = 0;
            if (Math.Abs(target - cur) > 1e-9) { if (commit) l.PlotWeight = target; changes.Add(target < 0 ? "plot" : "print_width_mm"); }
            if (Visible.HasValue && l.IsVisible != Visible.Value)
            {
                if (commit) l.IsVisible = Visible.Value;
                if (commit && l.IsVisible != Visible.Value) errors.Add("could not set visible=" + Visible.Value.ToString().ToLowerInvariant() + " (the current layer cannot be hidden; use set_current on another layer first)");
                else changes.Add("visible");
            }
            if (Locked.HasValue && l.IsLocked != Locked.Value)
            {
                if (commit) l.IsLocked = Locked.Value;
                if (commit && l.IsLocked != Locked.Value) errors.Add("could not set locked=" + Locked.Value.ToString().ToLowerInvariant() + " (the current layer cannot be locked; use set_current on another layer first)");
                else changes.Add("locked");
            }
            return changes;
        }
    }

    internal static class LayerUtil
    {
        public const string Sep = "::";
        public const string SchemeKey = "rhino-mcp.layer-scheme";
        public const string SchemeJsonKey = "rhino-mcp.layer-scheme-json";
        static readonly Regex HexRx = new Regex("^#[0-9a-fA-F]{6}$");

        public static string Hex(Color c) { return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"); }
        public static bool TryHex(string s, out Color c)
        {
            c = Color.Black;
            if (s == null || !HexRx.IsMatch(s)) return false;
            c = Color.FromArgb(Convert.ToInt32(s.Substring(1, 2), 16), Convert.ToInt32(s.Substring(3, 2), 16), Convert.ToInt32(s.Substring(5, 2), 16));
            return true;
        }

        public static string[] Split(string path) { return path.Split(new[] { Sep }, StringSplitOptions.None); }

        /// <summary>Returns an error message for an invalid layer path, or null.</summary>
        public static string ValidatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "layer path is empty";
            foreach (var seg in Split(path))
            {
                if (seg.Length == 0) return "layer path '" + path + "' has an empty segment";
                if (seg != seg.Trim()) return "layer path '" + path + "' has leading/trailing spaces in a segment";
                if (!Layer.IsValidName(seg)) return "'" + seg + "' is not a valid layer name (avoid : ; \\ and similar characters)";
            }
            return null;
        }

        public static int ResolveLinetype(RhinoDoc doc, string name, out bool found)
        {
            found = true;
            for (int i = 0; i < doc.Linetypes.Count; i++)
            {
                var lt = doc.Linetypes[i];
                if (!lt.IsDeleted && string.Equals(lt.Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            if (string.Equals(name, "Continuous", StringComparison.OrdinalIgnoreCase)) return -1;
            found = false;
            return -1;
        }
        public static string LinetypeName(RhinoDoc doc, Layer l)
        {
            int i = l.LinetypeIndex;
            return i < 0 || i >= doc.Linetypes.Count ? "Continuous" : doc.Linetypes[i].Name;
        }
        public static List<string> LinetypeNames(RhinoDoc doc)
        {
            var r = new List<string> { "Continuous" };
            for (int i = 0; i < doc.Linetypes.Count; i++)
                if (!doc.Linetypes[i].IsDeleted && !string.Equals(doc.Linetypes[i].Name, "Continuous", StringComparison.OrdinalIgnoreCase)) r.Add(doc.Linetypes[i].Name);
            return r;
        }

        /// <summary>Finds or creates a layer path, creating the whole parent chain. Names of created layers go to 'created'.</summary>
        public static Layer Ensure(RhinoDoc doc, string path, List<string> created, out string error)
        {
            error = ValidatePath(path);
            if (error != null) return null;
            var segs = Split(path);
            Layer parent = null;
            string cum = "";
            foreach (var seg in segs)
            {
                cum = cum.Length == 0 ? seg : cum + Sep + seg;
                var tree = LayerTree.Build(doc, false);
                var existing = tree.Find(cum);
                if (existing == null)
                {
                    int idx;
                    using (var nl = new Layer { Name = seg })
                    {
                        if (parent != null) nl.ParentLayerId = parent.Id;
                        idx = doc.Layers.Add(nl);
                    }
                    if (idx < 0) { error = "Rhino refused to create layer '" + cum + "'"; return null; }
                    existing = doc.Layers[idx];
                    if (created != null) created.Add(cum);
                }
                parent = existing;
            }
            return parent;
        }

        public static string Parent(string path)
        {
            int i = path.LastIndexOf(Sep, StringComparison.Ordinal);
            return i < 0 ? "" : path.Substring(0, i);
        }
        public static string Leaf(string path)
        {
            int i = path.LastIndexOf(Sep, StringComparison.Ordinal);
            return i < 0 ? path : path.Substring(i + Sep.Length);
        }

        public static object BBox(IEnumerable<RhinoObject> objs)
        {
            var bb = Rhino.Geometry.BoundingBox.Empty;
            foreach (var o in objs) bb.Union(o.Geometry.GetBoundingBox(true));
            if (!bb.IsValid) return null;
            return new { min = new[] { bb.Min.X, bb.Min.Y, bb.Min.Z }, max = new[] { bb.Max.X, bb.Max.Y, bb.Max.Z } };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCP.Tools.Shared
{
    /// <summary>Filter for objects_query / {query:{...}} targets. Built once, then applied per object.</summary>
    public sealed class ObjectFilter
    {
        public string Layer; public bool IncludeSublayers;
        public HashSet<string> Types;               // null = any
        public Regex Name;
        public string UserKey; public Regex UserValue;
        public BoundingBox Box; public bool HasBox; public bool BoxInside;
        public bool? Selected, Visible;
        readonly Dictionary<int, bool> _layerOk = new Dictionary<int, bool>();

        public static readonly string[] TypeNames = { "curve", "surface", "brep", "extrusion", "mesh", "subd", "point", "annotation", "instance", "hatch", "any" };

        /// <summary>Parses a filter object; every problem is reported through <paramref name="err"/>.</summary>
        public static ObjectFilter Parse(JObject q, Action<string> err, string prefix)
        {
            var f = new ObjectFilter();
            if (q == null) return f;
            var known = new HashSet<string> { "layer", "include_sublayers", "type", "name", "user_text", "bbox", "selected", "visible" };
            foreach (var p in q.Properties())
                if (!known.Contains(p.Name)) err(prefix + "unknown filter '" + p.Name + "'. Valid: " + string.Join(", ", known));
            JToken t;
            if (q.TryGetValue("layer", out t) && t.Type != JTokenType.Null)
            {
                if (t.Type != JTokenType.String) err(prefix + "layer must be a string (full path like 'Parent::Child' or leaf name)");
                else f.Layer = (string)t;
            }
            if (q.TryGetValue("include_sublayers", out t) && t.Type == JTokenType.Boolean) f.IncludeSublayers = (bool)t;
            if (q.TryGetValue("type", out t) && t.Type != JTokenType.Null)
            {
                var names = new List<string>();
                if (t.Type == JTokenType.String) names.Add((string)t);
                else if (t is JArray) { foreach (var x in (JArray)t) names.Add((string)x); }
                else err(prefix + "type must be a string or array of strings");
                var set = new HashSet<string>();
                foreach (var n in names)
                {
                    string l = (n ?? "").ToLowerInvariant();
                    if (!TypeNames.Contains(l)) { err(prefix + "type '" + n + "' unknown. Valid: " + string.Join(", ", TypeNames)); continue; }
                    if (l == "any") { set = null; break; }
                    set.Add(l);
                }
                f.Types = set;
            }
            if (q.TryGetValue("name", out t) && t.Type != JTokenType.Null) f.Name = Glob((string)t);
            if (q.TryGetValue("user_text", out t) && t.Type != JTokenType.Null)
            {
                var u = t as JObject;
                if (u == null || u["key"] == null || u["key"].Type != JTokenType.String) err(prefix + "user_text must be {key: string, value?: string (wildcards * ? allowed)}");
                else
                {
                    f.UserKey = (string)u["key"];
                    if (u["value"] != null && u["value"].Type != JTokenType.Null) f.UserValue = Glob((string)u["value"]);
                }
            }
            if (q.TryGetValue("bbox", out t) && t.Type != JTokenType.Null)
            {
                var b = t as JObject;
                Point3d mn = Point3d.Unset, mx = Point3d.Unset;
                if (b == null || !TryPt(b["min"], out mn) || !TryPt(b["max"], out mx))
                    err(prefix + "bbox must be {min:[x,y,z], max:[x,y,z], mode?: intersects|inside}");
                else
                {
                    string mode = b["mode"] == null ? "intersects" : ((string)b["mode"] ?? "").ToLowerInvariant();
                    if (mode != "intersects" && mode != "inside") err(prefix + "bbox.mode must be 'intersects' or 'inside'");
                    f.Box = new BoundingBox(mn, mx); f.HasBox = true; f.BoxInside = mode == "inside";
                }
            }
            if (q.TryGetValue("selected", out t) && t.Type == JTokenType.Boolean) f.Selected = (bool)t;
            if (q.TryGetValue("visible", out t) && t.Type == JTokenType.Boolean) f.Visible = (bool)t;
            return f;
        }

        static bool TryPt(JToken t, out Point3d p)
        {
            p = Point3d.Unset;
            var a = t as JArray;
            if (a == null || a.Count != 3 || a.Any(x => x.Type != JTokenType.Integer && x.Type != JTokenType.Float)) return false;
            p = new Point3d((double)a[0], (double)a[1], (double)a[2]);
            return true;
        }

        public static Regex Glob(string g)
        {
            return new Regex("^" + Regex.Escape(g ?? "").Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        bool LayerMatches(RhinoDoc doc, int layerIndex)
        {
            bool ok;
            if (_layerOk.TryGetValue(layerIndex, out ok)) return ok;
            var l = doc.Layers[layerIndex];
            ok = NameIs(l);
            if (!ok && IncludeSublayers)
            {
                var cur = l; int guard = 0;
                while (!ok && cur.ParentLayerId != Guid.Empty && guard++ < 64)
                {
                    cur = doc.Layers.FindId(cur.ParentLayerId);
                    if (cur == null) break;
                    ok = NameIs(cur);
                }
            }
            _layerOk[layerIndex] = ok;
            return ok;
        }

        bool NameIs(Layer l)
        {
            return string.Equals(l.FullPath, Layer, StringComparison.OrdinalIgnoreCase) || string.Equals(l.Name, Layer, StringComparison.OrdinalIgnoreCase);
        }

        public bool Matches(RhinoDoc doc, RhinoObject o)
        {
            if (Layer != null && !LayerMatches(doc, o.Attributes.LayerIndex)) return false;
            if (Types != null && !Types.Contains(ObjectQuery.TypeName(o))) return false;
            if (Name != null && !Name.IsMatch(o.Attributes.Name ?? "")) return false;
            if (UserKey != null)
            {
                string v = o.Attributes.GetUserString(UserKey);
                if (string.IsNullOrEmpty(v)) return false;
                if (UserValue != null && !UserValue.IsMatch(v)) return false;
            }
            if (Selected.HasValue && (o.IsSelected(false) != 0) != Selected.Value) return false;
            if (Visible.HasValue && o.Visible != Visible.Value) return false;
            if (HasBox)
            {
                var bb = o.Geometry.GetBoundingBox(true);
                if (!bb.IsValid) return false;
                if (BoxInside) { if (!Box.Contains(bb)) return false; }
                else if (bb.Min.X > Box.Max.X || bb.Max.X < Box.Min.X || bb.Min.Y > Box.Max.Y || bb.Max.Y < Box.Min.Y || bb.Min.Z > Box.Max.Z || bb.Max.Z < Box.Min.Z) return false;
            }
            return true;
        }
    }

    public static class ObjectQuery
    {
        public const string SetPrefix = "rhinomcp.set.";

        /// <summary>JSON schema for the filter object (used by objects_query and by {query} targets).</summary>
        public const string FilterSchema =
            "{\"type\":\"object\",\"properties\":{" +
            "\"layer\":{\"type\":\"string\",\"description\":\"Full path (Parent::Child) or leaf name\"}," +
            "\"include_sublayers\":{\"type\":\"boolean\",\"default\":false}," +
            "\"type\":{\"description\":\"curve|surface|brep|extrusion|mesh|subd|point|annotation|instance|hatch|any (string or array)\",\"oneOf\":[{\"type\":\"string\"},{\"type\":\"array\",\"items\":{\"type\":\"string\"}}]}," +
            "\"name\":{\"type\":\"string\",\"description\":\"Glob with * ?\"}," +
            "\"user_text\":{\"type\":\"object\",\"properties\":{\"key\":{\"type\":\"string\"},\"value\":{\"type\":\"string\",\"description\":\"glob; omit = key present\"}},\"required\":[\"key\"]}," +
            "\"bbox\":{\"type\":\"object\",\"properties\":{\"min\":{\"$ref\":\"#/$defs/point\"},\"max\":{\"$ref\":\"#/$defs/point\"},\"mode\":{\"enum\":[\"intersects\",\"inside\"],\"default\":\"intersects\"}},\"required\":[\"min\",\"max\"]}," +
            "\"selected\":{\"type\":\"boolean\"},\"visible\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";

        /// <summary>Target schema: handle | GUID | {set} | {query: filter} | array of those.</summary>
        public const string TargetSchema =
            "{\"description\":\"Object handle (o12), GUID, {set:name}, {query:{layer,include_sublayers,type,name,user_text,bbox,selected,visible}} or an array of those\"," +
            "\"oneOf\":[{\"type\":\"string\"},{\"type\":\"object\",\"properties\":{\"set\":{\"type\":\"string\"},\"query\":" + FilterSchema + "}},{\"type\":\"array\",\"items\":{}}]}";

        public static string TypeName(RhinoObject o)
        {
            switch (o.ObjectType)
            {
                case ObjectType.Curve: return "curve";
                case ObjectType.Surface: return "surface";
                case ObjectType.Brep: return "brep";
                case ObjectType.Extrusion: return "extrusion";
                case ObjectType.Mesh: return "mesh";
                case ObjectType.SubD: return "subd";
                case ObjectType.Point:
                case ObjectType.PointSet: return "point";
                case ObjectType.Annotation:
                case ObjectType.TextDot: return "annotation";
                case ObjectType.InstanceReference: return "instance";
                case ObjectType.Hatch: return "hatch";
                default: return o.ObjectType.ToString().ToLowerInvariant();
            }
        }

        public static IEnumerable<RhinoObject> All(RhinoDoc doc)
        {
            var s = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true };
            return doc.Objects.GetObjectList(s);
        }

        public static List<RhinoObject> Run(RhinoDoc doc, ObjectFilter f)
        {
            var res = new List<RhinoObject>();
            foreach (var o in All(doc)) if (f.Matches(doc, o)) res.Add(o);
            return res;
        }

        /// <summary>
        /// Resolves a target token (handle | GUID | {set} | {query} | array). Errors go to <paramref name="a"/>.
        /// Sets are read from doc.Strings (persisted), falling back to the in-memory Core SetStore.
        /// </summary>
        public static List<RhinoObject> Resolve(ToolContext ctx, JToken t, ArgReader a, string name = "target", bool required = true)
        {
            var res = new List<RhinoObject>();
            if (t == null || t.Type == JTokenType.Null)
            {
                if (required) a.Error("'" + name + "' is required (handle, GUID, {set} or {query})");
                return res;
            }
            if (ctx.Doc == null) { a.Error("No active document"); return res; }
            var seen = new HashSet<Guid>();
            var arr = t as JArray;
            if (arr != null) { foreach (var it in arr) ResolveOne(ctx, it, a, name, res, seen); }
            else ResolveOne(ctx, t, a, name, res, seen);
            return res;
        }

        static void ResolveOne(ToolContext ctx, JToken t, ArgReader a, string name, List<RhinoObject> res, HashSet<Guid> seen)
        {
            var doc = ctx.Doc;
            if (t.Type == JTokenType.String)
            {
                string s = (string)t;
                Guid id;
                if (!HandleMap.Instance.TryResolve(s, out id) && !Guid.TryParse(s, out id))
                { a.Error("'" + name + "': '" + s + "' is neither a known handle (e.g. o12) nor a GUID. Get handles from objects_query."); return; }
                var obj = doc.Objects.FindId(id);
                if (obj == null) { a.Error("'" + name + "': object '" + s + "' no longer exists"); return; }
                if (seen.Add(id)) res.Add(obj);
                return;
            }
            var o = t as JObject;
            if (o == null) { a.Error("'" + name + "' has an unsupported form; use a handle, GUID, {set} or {query}"); return; }
            JToken setTok, qTok;
            if (o.TryGetValue("set", out setTok))
            {
                string sn = (string)setTok;
                List<Guid> ids = LoadSet(doc, sn);
                if (ids == null)
                {
                    var names = SetNames(doc);
                    a.Error("'" + name + "': set '" + sn + "' not found. Existing sets: " + (names.Count == 0 ? "(none)" : string.Join(", ", names)) + ". Create one with sets_save.");
                    return;
                }
                foreach (var id in ids) { var obj = doc.Objects.FindId(id); if (obj != null && seen.Add(id)) res.Add(obj); }
                return;
            }
            if (o.TryGetValue("query", out qTok) && qTok is JObject)
            {
                int errs = 0;
                var f = ObjectFilter.Parse((JObject)qTok, e => { errs++; a.Error(e); }, "'" + name + ".query': ");
                if (errs > 0) return;
                foreach (var obj in Run(doc, f)) if (seen.Add(obj.Id)) res.Add(obj);
                return;
            }
            a.Error("'" + name + "' object must contain 'set' or 'query'");
        }

        // ---- named sets: persisted in doc.Strings under "rhinomcp.set.<name>" as "v1:" + ';'-joined GUIDs ----

        public static List<Guid> LoadSet(RhinoDoc doc, string name)
        {
            string v = doc.Strings.GetValue(SetPrefix + name);
            if (v != null && v.StartsWith("v1:", StringComparison.Ordinal))
            {
                var ids = new List<Guid>();
                foreach (var p in v.Substring(3).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                { Guid g; if (Guid.TryParse(p, out g)) ids.Add(g); }
                return ids;
            }
            List<Guid> mem;
            if (SetStore.Instance.TryGet(name, out mem)) return mem;
            return null;
        }

        public static void StoreSet(RhinoDoc doc, string name, IEnumerable<Guid> ids)
        {
            var list = ids.ToList();
            var sb = new StringBuilder("v1:");
            for (int i = 0; i < list.Count; i++) { if (i > 0) sb.Append(';'); sb.Append(list[i].ToString("D")); }
            doc.Strings.SetString(SetPrefix + name, sb.ToString());
            SetStore.Instance.Save(name, list);   // keep Core's in-memory store in sync for tools that use ArgReader.Target
        }

        public static bool RemoveSet(RhinoDoc doc, string name)
        {
            string cur = doc.Strings.GetValue(SetPrefix + name);
            bool had = cur != null && cur.StartsWith("v1:", StringComparison.Ordinal);
            if (had) doc.Strings.Delete(SetPrefix + name);
            bool mem = SetStore.Instance.Delete(name);
            return had || mem;
        }

        public static List<string> SetNames(RhinoDoc doc)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            int n = doc.Strings.Count;
            for (int i = 0; i < n; i++)
            {
                string k = doc.Strings.GetKey(i);
                if (k != null && k.StartsWith(SetPrefix, StringComparison.Ordinal)) names.Add(k.Substring(SetPrefix.Length));
            }
            foreach (var m in SetStore.Instance.Names()) names.Add(m);
            return names.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        public static string ValidateSetName(string n)
        {
            if (string.IsNullOrWhiteSpace(n)) return "set name must not be empty";
            if (n.Length > 64) return "set name too long (max 64 chars)";
            foreach (char c in n) if (char.IsControl(c)) return "set name must not contain control characters";
            return null;
        }

        // ---- shared output helpers ----

        public static string LayerPath(RhinoDoc doc, RhinoObject o) { return doc.Layers[o.Attributes.LayerIndex].FullPath; }

        public static double[] Pt(Point3d p) { return new[] { Math.Round(p.X, 6), Math.Round(p.Y, 6), Math.Round(p.Z, 6) }; }

        public static object BoxJson(BoundingBox b)
        {
            if (!b.IsValid) return null;
            return new { min = Pt(b.Min), max = Pt(b.Max) };
        }

        public static BoundingBox BoxOf(IEnumerable<RhinoObject> objs)
        {
            var b = BoundingBox.Empty;
            foreach (var o in objs) { var ob = o.Geometry.GetBoundingBox(true); if (ob.IsValid) b.Union(ob); }
            return b;
        }

        public static Dictionary<string, string> UserText(RhinoObject o)
        {
            var d = new Dictionary<string, string>();
            var nv = o.Attributes.GetUserStrings();
            if (nv != null) foreach (string k in nv.AllKeys) d[k] = nv[k];
            return d;
        }

        /// <summary>Length / area / volume of one object, computed only on request. Null entries mean "not applicable".</summary>
        public static void Measure(RhinoObject o, out double? length, out double? area, out double? volume)
        {
            length = null; area = null; volume = null;
            try
            {
                var g = o.Geometry;
                var crv = g as Curve;
                if (crv != null)
                {
                    length = crv.GetLength();
                    if (crv.IsClosed && crv.IsPlanar())
                    { using (var amp = AreaMassProperties.Compute(crv)) { if (amp != null) area = amp.Area; } }
                    return;
                }
                Brep brep = g as Brep;
                Brep temp = null;   // temporary conversion, disposed below
                try
                {
                    var ext = g as Extrusion;
                    if (brep == null && ext != null) { temp = ext.ToBrep(); brep = temp; }
                    var srf = g as Surface;
                    if (brep == null && srf != null) { temp = srf.ToBrep(); brep = temp; }
                    if (brep != null)
                    {
                        using (var amp = AreaMassProperties.Compute(brep)) { if (amp != null) area = amp.Area; }
                        if (brep.IsSolid)
                            using (var vmp = VolumeMassProperties.Compute(brep)) { if (vmp != null) volume = vmp.Volume; }
                        return;
                    }
                }
                finally { if (temp != null) temp.Dispose(); }
                var mesh = g as Mesh;
                if (mesh != null)
                {
                    using (var amp = AreaMassProperties.Compute(mesh)) { if (amp != null) area = amp.Area; }
                    if (mesh.IsClosed)
                        using (var vmp = VolumeMassProperties.Compute(mesh)) { if (vmp != null) volume = vmp.Volume; }
                    return;
                }
                var hatch = g as Hatch;
                if (hatch != null)
                    using (var amp = AreaMassProperties.Compute(hatch)) { if (amp != null) area = amp.Area; }
            }
            catch (Exception) { /* measurement is best-effort */ }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static string Hex(System.Drawing.Color c) { return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"); }

        /// <summary>Up to <paramref name="max"/> handles plus a truncation note.</summary>
        public static object Handles(IEnumerable<RhinoObject> objs, int max, bool includeGuids)
        {
            var list = objs.ToList();
            var r = new Dictionary<string, object>();
            r["count"] = list.Count;
            r["handles"] = list.Take(max).Select(o => HandleMap.Instance.For(o.Id)).ToList();
            if (list.Count > max) r["truncated"] = true;
            if (includeGuids) r["guids"] = list.Take(max).Select(o => o.Id.ToString()).ToList();
            return r;
        }
    }
}

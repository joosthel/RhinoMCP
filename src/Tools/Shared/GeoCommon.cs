using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCP.Tools.Shared
{
    /// <summary>A coordinate frame (plane). Tool arguments are given in this frame; the server converts to world.</summary>
    public sealed class Frame
    {
        public Plane Plane;
        public Transform ToWorld;
        public string Name;

        public Frame(Plane plane, string name)
        {
            Plane = plane; Name = name;
            ToWorld = Transform.PlaneToPlane(Plane.WorldXY, plane);
        }
        public Point3d Pt(Point3d local) { var p = local; p.Transform(ToWorld); return p; }
        public Vector3d Vec(Vector3d local) { var v = local; v.Transform(ToWorld); return v; }
        public bool IsWorld { get { return Name == "world_xy"; } }
    }

    /// <summary>Argument reader for one JSON object. Collects errors; points/vectors/planes are interpreted in <see cref="Frame"/>.</summary>
    public sealed class P
    {
        public readonly JObject O;
        public readonly ToolContext Ctx;
        public Frame Frame;
        public readonly List<string> Errors = new List<string>();

        public P(JObject o, ToolContext ctx, Frame frame) { O = o ?? new JObject(); Ctx = ctx; Frame = frame; }

        public void Err(string m) { Errors.Add(m); }
        public bool Has(string n) { JToken t; return O.TryGetValue(n, out t) && t.Type != JTokenType.Null; }
        public JToken Tok(string n) { JToken t; return O.TryGetValue(n, out t) && t.Type != JTokenType.Null ? t : null; }

        public string Str(string n, bool req = false, string def = null)
        {
            var t = Tok(n);
            if (t == null) { if (req) Err("'" + n + "' is required (string)"); return def; }
            if (t.Type != JTokenType.String) { Err("'" + n + "' must be a string"); return def; }
            return (string)t;
        }
        public double Num(string n, bool req = false, double def = double.NaN)
        {
            var t = Tok(n);
            if (t == null) { if (req) Err("'" + n + "' is required (number)"); return def; }
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) { Err("'" + n + "' must be a number"); return def; }
            return (double)t;
        }
        public int Int(string n, bool req = false, int def = 0)
        {
            var t = Tok(n);
            if (t == null) { if (req) Err("'" + n + "' is required (integer)"); return def; }
            if (t.Type != JTokenType.Integer) { Err("'" + n + "' must be an integer"); return def; }
            return (int)(long)t;
        }
        public bool Bool(string n, bool def = false)
        {
            var t = Tok(n);
            if (t == null) return def;
            if (t.Type != JTokenType.Boolean) { Err("'" + n + "' must be a boolean"); return def; }
            return (bool)t;
        }

        public static bool TryNums(JToken t, int min, int max, out double[] v)
        {
            v = null;
            var a = t as JArray;
            if (a == null || a.Count < min || a.Count > max) return false;
            v = new double[a.Count];
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Type != JTokenType.Integer && a[i].Type != JTokenType.Float) return false;
                v[i] = (double)a[i];
            }
            return true;
        }

        /// <summary>Local point [x,y,z] (z optional) as a raw, untransformed value.</summary>
        public Point3d LocalPt(string n, bool req = false)
        {
            var t = Tok(n);
            if (t == null) { if (req) Err("'" + n + "' is required ([x,y,z])"); return Point3d.Unset; }
            double[] v;
            if (!TryNums(t, 2, 3, out v)) { Err("'" + n + "' must be [x,y,z] numbers"); return Point3d.Unset; }
            return new Point3d(v[0], v[1], v.Length > 2 ? v[2] : 0);
        }
        /// <summary>Point in the frame, converted to world.</summary>
        public Point3d Pt(string n, bool req = false)
        {
            var l = LocalPt(n, req);
            return l.IsValid ? Frame.Pt(l) : Point3d.Unset;
        }
        public Vector3d Vec(string n, bool req = false)
        {
            var l = LocalPt(n, req);
            return l.IsValid ? Frame.Vec(new Vector3d(l)) : Vector3d.Unset;
        }
        public List<Point3d> Pts(string n, int min, bool req = true)
        {
            var res = new List<Point3d>();
            var t = Tok(n) as JArray;
            if (t == null) { if (req || Has(n)) Err("'" + n + "' must be an array of [x,y,z] points"); return res; }
            foreach (var it in t)
            {
                double[] v;
                if (!TryNums(it, 2, 3, out v)) { Err("'" + n + "' contains an item that is not [x,y,z]"); return new List<Point3d>(); }
                res.Add(Frame.Pt(new Point3d(v[0], v[1], v.Length > 2 ? v[2] : 0)));
            }
            if (res.Count < min) { Err("'" + n + "' needs at least " + min + " points"); }
            return res;
        }

        /// <summary>Resolves a target-ish field to Rhino objects. Errors are collected.</summary>
        public List<RhinoObject> Objs(string n, bool req = true)
        {
            var t = Tok(n);
            if (t == null) { if (req) Err("'" + n + "' is required (handle, GUID, {set} or {query})"); return new List<RhinoObject>(); }
            var ar = new ArgReader(new JObject { { "t", t.DeepClone() } });
            var l = ar.Target("t", Ctx, req);
            if (ar.HasErrors) Err(ar.ErrorResult().Message.Replace("'t'", "'" + n + "'"));
            return l;
        }
        public List<Curve> Curves(string n, bool req = true)
        {
            var res = new List<Curve>();
            foreach (var o in Objs(n, req))
            {
                var c = o.Geometry as Curve;
                if (c == null) { Err("'" + n + "': object " + Ctx.Handles.For(o.Id) + " is " + o.ObjectType + ", not a curve"); continue; }
                res.Add(c.DuplicateCurve());
            }
            return res;
        }
        public bool TryPlane(string n, out Plane pl, bool req = false)
        {
            pl = Frame.Plane;
            var t = Tok(n);
            if (t == null) { if (req) Err("'" + n + "' is required (plane)"); return !req; }
            string perr;
            Frame f = GeoUtil.ParsePlane(t, Ctx, Frame, out perr);
            if (f == null) { Err("'" + n + "': " + perr); return false; }
            pl = f.Plane;
            return true;
        }
    }

    public static class GeoUtil
    {
        public const string PlaneHelp = "world_xy | world_yz | world_zx | named CPlane | {origin, x_axis, y_axis} (x_axis/y_axis are direction vectors)";

        /// <summary>Parses a plane. Plane object coordinates are interpreted in <paramref name="inFrame"/> (may be null = world).</summary>
        public static Frame ParsePlane(JToken t, ToolContext ctx, Frame inFrame, out string err)
        {
            err = null;
            if (t == null || t.Type == JTokenType.Null) return new Frame(Plane.WorldXY, "world_xy");
            if (t.Type == JTokenType.String)
            {
                string s = (string)t;
                switch (s.ToLowerInvariant())
                {
                    case "world_xy": case "top": return new Frame(Plane.WorldXY, "world_xy");
                    case "world_yz": return new Frame(Plane.WorldYZ, "world_yz");
                    case "world_zx": return new Frame(Plane.WorldZX, "world_zx");
                }
                if (ctx.Doc != null)
                {
                    int i = ctx.Doc.NamedConstructionPlanes.Find(s);
                    if (i >= 0) return new Frame(ctx.Doc.NamedConstructionPlanes[i].Plane, s);
                }
                err = "unknown plane '" + s + "'. Use " + PlaneHelp;
                return null;
            }
            var o = t as JObject;
            if (o != null)
            {
                double[] ov, xv, yv;
                if (!P.TryNums(o["origin"], 2, 3, out ov) || !P.TryNums(o["x_axis"], 2, 3, out xv) || !P.TryNums(o["y_axis"], 2, 3, out yv))
                { err = "plane object needs origin, x_axis, y_axis as [x,y,z]"; return null; }
                var origin = new Point3d(ov[0], ov[1], ov.Length > 2 ? ov[2] : 0);
                var xa = new Vector3d(xv[0], xv[1], xv.Length > 2 ? xv[2] : 0);
                var ya = new Vector3d(yv[0], yv[1], yv.Length > 2 ? yv[2] : 0);
                if (inFrame != null) { origin = inFrame.Pt(origin); xa = inFrame.Vec(xa); ya = inFrame.Vec(ya); }
                var pl = new Plane(origin, xa, ya);
                if (!pl.IsValid) { err = "plane axes are parallel or zero length"; return null; }
                return new Frame(pl, "custom");
            }
            err = "plane must be a name or object. Use " + PlaneHelp;
            return null;
        }

        public static int EnsureLayer(RhinoDoc doc, string fullPath)
        {
            int idx = doc.Layers.FindByFullPath(fullPath, -1);
            if (idx >= 0) return idx;
            string[] parts = fullPath.Split(new[] { "::" }, StringSplitOptions.None).Select(x => x.Trim()).ToArray();
            string path = ""; Guid parent = Guid.Empty; int cur = -1;
            foreach (var part in parts)
            {
                if (part.Length == 0) throw new ArgumentException("layer path '" + fullPath + "' has an empty segment");
                path = path.Length == 0 ? part : path + "::" + part;
                cur = doc.Layers.FindByFullPath(path, -1);
                if (cur < 0)
                {
                    var l = new Layer { Name = part, ParentLayerId = parent };
                    cur = doc.Layers.Add(l);
                    if (cur < 0) throw new InvalidOperationException("could not create layer '" + path + "'");
                }
                parent = doc.Layers[cur].Id;
            }
            return cur;
        }

        /// <summary>Builds attributes from layer / name / user_text of an item (null fields keep defaults).</summary>
        public static ObjectAttributes Attributes(P p, RhinoDoc doc, string defaultLayer)
        {
            var a = new ObjectAttributes();
            string layer = p.Str("layer", false, defaultLayer);
            if (!string.IsNullOrEmpty(layer))
            {
                try { a.LayerIndex = EnsureLayer(doc, layer); }
                catch (Exception ex) { p.Err("layer: " + ex.Message); }
            }
            string name = p.Str("name");
            if (name != null) a.Name = name;
            var ut = p.Tok("user_text") as JObject;
            if (p.Has("user_text") && ut == null) p.Err("'user_text' must be an object of string values");
            if (ut != null) foreach (var kv in ut) a.SetUserString(kv.Key, kv.Value.Type == JTokenType.String ? (string)kv.Value : kv.Value.ToString(Newtonsoft.Json.Formatting.None));
            return a;
        }

        public static Guid AddGeometry(RhinoDoc doc, GeometryBase g, ObjectAttributes a)
        {
            if (g is Brep) return doc.Objects.AddBrep((Brep)g, a);
            if (g is Extrusion) return doc.Objects.AddExtrusion((Extrusion)g, a);
            if (g is Curve) return doc.Objects.AddCurve((Curve)g, a);
            if (g is Surface) return doc.Objects.AddSurface((Surface)g, a);
            if (g is Point) return doc.Objects.AddPoint(((Point)g).Location, a);
            if (g is TextDot) return doc.Objects.AddTextDot((TextDot)g, a);
            if (g is Mesh) return doc.Objects.AddMesh((Mesh)g, a);
            return doc.Objects.Add(g, a);
        }

        public static double R(double v) { return Math.Round(v, 6); }
        public static double[] Arr(Point3d p) { return new[] { R(p.X), R(p.Y), R(p.Z) }; }
        public static double[] Arr(Vector3d p) { return new[] { R(p.X), R(p.Y), R(p.Z) }; }
        public static object BBox(BoundingBox b)
        {
            if (!b.IsValid) return null;
            return new { min = Arr(b.Min), max = Arr(b.Max) };
        }
        public static string TypeName(GeometryBase g)
        {
            if (g is Extrusion) return "extrusion";
            if (g is Brep)
            {
                var b = (Brep)g;
                return b.Faces.Count == 1 && !b.IsSolid ? "surface" : (b.IsSolid ? "solid" : "polysurface");
            }
            if (g is Curve) return "curve";
            if (g is Point) return "point";
            if (g is TextDot) return "text_dot";
            if (g is Mesh) return "mesh";
            if (g is Surface) return "surface";
            return g.GetType().Name.ToLowerInvariant();
        }

        public static Brep AsBrep(GeometryBase g)
        {
            var b = g as Brep;
            if (b != null) return b;
            var e = g as Extrusion;
            if (e != null) return e.ToBrep(true);
            var s = g as Surface;
            if (s != null) return Brep.CreateFromSurface(s);
            return null;
        }

        public static double Volume(Brep b)
        {
            if (b == null || !b.IsSolid) return 0;
            try
            {
                var m = VolumeMassProperties.Compute(b);
                return m == null ? 0 : Math.Abs(m.Volume);
            }
            catch (Exception) { return 0; }
        }

        /// <summary>Short numeric facts about a geometry (length / area / volume).</summary>
        public static Dictionary<string, object> Measure(GeometryBase g)
        {
            var d = new Dictionary<string, object>();
            try
            {
                var c = g as Curve;
                if (c != null) { d["length"] = R(c.GetLength()); d["closed"] = c.IsClosed; return d; }
                var b = AsBrep(g);
                if (b != null)
                {
                    bool solid = b.IsSolid;
                    d["solid"] = solid;
                    var am = AreaMassProperties.Compute(b);
                    if (am != null) d["area"] = R(am.Area);
                    if (solid) d["volume"] = R(Volume(b));
                }
            }
            catch (Exception) { }
            return d;
        }

        public static string FirstLines(string log, int maxLines, int maxChars)
        {
            if (string.IsNullOrEmpty(log)) return null;
            var lines = log.Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Take(maxLines);
            string s = string.Join(" | ", lines);
            return s.Length > maxChars ? s.Substring(0, maxChars) + "..." : s;
        }

        public static BoundingBox Union(IEnumerable<BoundingBox> boxes)
        {
            var u = BoundingBox.Empty;
            foreach (var b in boxes) if (b.IsValid) u.Union(b);
            return u;
        }

        public static string Handle(ToolContext ctx, Guid id) { return ctx.Handles.For(id); }

        /// <summary>Copies layer/name/color etc. from a source object's attributes (fresh object id).</summary>
        public static ObjectAttributes CopyAttributes(RhinoObject src)
        {
            var a = src.Attributes.Duplicate(); a.ObjectId = Guid.Empty;
            return a;
        }
    }
}

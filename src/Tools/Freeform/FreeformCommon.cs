using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Freeform
{
    /// <summary>Collects temporary geometry and disposes it deterministically (never throws).</summary>
    public sealed class Bag : IDisposable
    {
        readonly List<IDisposable> _l = new List<IDisposable>();
        public T Add<T>(T d) where T : class, IDisposable { if (d != null) _l.Add(d); return d; }
        public void Dispose()
        {
            foreach (var d in _l) { try { d.Dispose(); } catch (Exception) { } }
            _l.Clear();
        }
    }

    /// <summary>A resolved reference to a curve, surface face and/or edge of a document object.</summary>
    public sealed class Ref
    {
        public RhinoObject Obj;
        public string Handle;
        public Brep Brep;          // for surface-ish objects
        public BrepFace Face;
        public BrepEdge Edge;
        public Curve Curve;        // for curve objects
        public int FaceIndex = -1, EdgeIndex = -1;
    }

    public static class FF
    {
        /// <summary>Short " @ Class.Method" suffix naming the first stack frame (for unexpected exceptions).</summary>
        public static string Where(Exception ex)
        {
            try
            {
                var st = ex.StackTrace;
                if (string.IsNullOrEmpty(st)) return "";
                var line = st.Split('\n')[0].Trim();
                return " @ " + (line.Length > 160 ? line.Substring(0, 160) : line);
            }
            catch (Exception) { return ""; }
        }

        public static double R(double v) { return Math.Round(v, 6); }
        public static double R4(double v) { return Math.Round(v, 4); }

        public static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        // ---------------------------------------------------------------- object resolution

        public static RhinoObject OneObject(P p, JToken t, string field)
        {
            var ar = new ArgReader(new JObject { { "t", t.DeepClone() } });
            var l = ar.Target("t", p.Ctx);
            if (ar.HasErrors) { p.Err("'" + field + "': " + ar.ErrorResult().Message.Replace("'t'", "value")); return null; }
            if (l.Count != 1) { p.Err("'" + field + "' must resolve to exactly one object (got " + l.Count + ")"); return null; }
            return l[0];
        }

        /// <summary>Resolves "o12" or {target:"o12", face?:n, edge?:n} to a Ref. Surfaces/extrusions/SubDs are converted to Brep.</summary>
        public static Ref ResolveRef(P p, JToken t, string field, bool wantFace, bool wantEdge, Bag bag)
        {
            if (t == null) { p.Err("'" + field + "' is required"); return null; }
            JToken target = t;
            int face = -1, edge = -1;
            var o = t as JObject;
            if (o != null)
            {
                target = o["target"];
                if (target == null) { p.Err("'" + field + "' object needs 'target' (handle) and optional 'face'/'edge' indices"); return null; }
                if (o["face"] != null) face = (int)o["face"];
                if (o["edge"] != null) edge = (int)o["edge"];
            }
            var obj = OneObject(p, target, field);
            if (obj == null) return null;
            var r = new Ref { Obj = obj, Handle = p.Ctx.Handles.For(obj.Id) };
            var g = obj.Geometry;
            if (g is Curve)
            {
                if (wantFace && !wantEdge) { p.Err("'" + field + "': " + r.Handle + " is a curve, a surface/face is required"); return null; }
                r.Curve = (Curve)g;
                return r;
            }
            Brep b = g as Brep;
            if (b == null)
            {
                var ex = g as Extrusion;
                var sd = g as SubD;
                var sf = g as Surface;
                if (ex != null) b = bag.Add(ex.ToBrep(true));
                else if (sd != null) b = bag.Add(sd.ToBrep());
                else if (sf != null) b = bag.Add(Brep.CreateFromSurface(sf));
            }
            if (b == null) { p.Err("'" + field + "': " + r.Handle + " (" + obj.ObjectType + ") is not a surface, brep, extrusion or SubD"); return null; }
            r.Brep = b;
            if (wantFace)
            {
                if (face < 0)
                {
                    if (b.Faces.Count == 1) face = 0;
                    else { p.Err("'" + field + "': " + r.Handle + " has " + b.Faces.Count + " faces; pass {target, face: 0.." + (b.Faces.Count - 1) + "}"); return null; }
                }
                if (face >= b.Faces.Count) { p.Err("'" + field + "': face " + face + " out of range 0.." + (b.Faces.Count - 1)); return null; }
                r.Face = b.Faces[face]; r.FaceIndex = face;
            }
            if (wantEdge && edge >= 0)
            {
                if (edge >= b.Edges.Count) { p.Err("'" + field + "': edge " + edge + " out of range 0.." + (b.Edges.Count - 1)); return null; }
                r.Edge = b.Edges[edge]; r.EdgeIndex = edge;
            }
            return r;
        }

        // ---------------------------------------------------------------- curve inputs

        public static bool IsPointArr(JToken t)
        {
            double[] v;
            return t is JArray && P.TryNums(t, 2, 3, out v);
        }

        public static List<Point3d> PointList(P p, JToken t, string field, int min)
        {
            var res = new List<Point3d>();
            var a = t as JArray;
            if (a == null) { p.Err("'" + field + "' must be an array of [x,y,z]"); return res; }
            foreach (var it in a)
            {
                double[] v;
                if (!P.TryNums(it, 2, 3, out v)) { p.Err("'" + field + "' contains an item that is not [x,y,z]"); return new List<Point3d>(); }
                res.Add(p.Frame.Pt(new Point3d(v[0], v[1], v.Length > 2 ? v[2] : 0)));
            }
            if (res.Count < min) p.Err("'" + field + "' needs at least " + min + " points");
            return res;
        }

        public const string CurveHelp = "curve spec: handle 'o5' | {target:'o5', edge:n} | [[x,y,z],...] (interpolated, degree 3) | " +
            "{points:[...], mode?:interpolate|control_points|polyline, degree?:3, closed?:false} | {circle:{center,radius}} | {ellipse:{center,rx,ry}} (in the item plane)";

        /// <summary>Builds a curve from a curve spec (see <see cref="CurveHelp"/>). Result is owned by the bag.</summary>
        public static Curve CurveSpec(P p, JToken t, string field, Bag bag, bool keepEdge = false)
        {
            if (t == null) { p.Err("'" + field + "' is required (" + CurveHelp + ")"); return null; }
            try
            {
                if (t.Type == JTokenType.String || (t is JObject && ((JObject)t)["target"] != null))
                {
                    var r = ResolveRef(p, t, field, false, true, bag);
                    if (r == null) return null;
                    if (keepEdge && r.Edge != null) return r.Edge;   // live brep edge: keeps adjacent-surface continuity info (network surface)
                    Curve c = null;
                    if (r.Curve != null) c = r.Curve.DuplicateCurve();
                    else if (r.Edge != null) c = r.Edge.DuplicateCurve();
                    else if (r.Brep != null && r.Brep.Edges.Count == 1) c = r.Brep.Edges[0].DuplicateCurve();
                    if (c == null) { p.Err("'" + field + "': " + r.Handle + " is not a curve (pass {target, edge:n} for a surface edge)"); return null; }
                    return bag.Add(c);
                }
                if (t is JArray && ((JArray)t).Count > 0 && IsPointArr(((JArray)t).First))
                {
                    var pts = PointList(p, t, field, 2);
                    if (pts.Count < 2) return null;
                    { var ic = InterpCurve(pts, 3, false); if (ic == null) { p.Err("'" + field + "': could not interpolate a curve through these points (duplicates?)"); return null; } return bag.Add(ic); }
                }
                var o = t as JObject;
                if (o != null)
                {
                    var sub = new P(o, p.Ctx, p.Frame);
                    if (o["circle"] != null)
                    {
                        var co = o["circle"] as JObject;
                        var cp = new P(co ?? new JObject(), p.Ctx, p.Frame);
                        var ctr = cp.Pt("center", true); double rad = cp.Num("radius", true);
                        if (cp.Errors.Count > 0) { foreach (var e in cp.Errors) p.Err("'" + field + ".circle': " + e); return null; }
                        if (!(rad > 0)) { p.Err("'" + field + ".circle.radius' must be > 0"); return null; }
                        var pl = p.Frame.Plane; pl.Origin = ctr;
                        return bag.Add(NurbsCurve.CreateFromCircle(new Circle(pl, rad)));
                    }
                    if (o["ellipse"] != null)
                    {
                        var co = o["ellipse"] as JObject;
                        var cp = new P(co ?? new JObject(), p.Ctx, p.Frame);
                        var ctr = cp.Pt("center", true); double rx = cp.Num("rx", true), ry = cp.Num("ry", true);
                        if (cp.Errors.Count > 0) { foreach (var e in cp.Errors) p.Err("'" + field + ".ellipse': " + e); return null; }
                        if (!(rx > 0) || !(ry > 0)) { p.Err("'" + field + ".ellipse' rx/ry must be > 0"); return null; }
                        var pl = p.Frame.Plane; pl.Origin = ctr;
                        return bag.Add(new Ellipse(pl, rx, ry).ToNurbsCurve());
                    }
                    if (o["points"] != null)
                    {
                        var pts = PointList(p, o["points"], field + ".points", 2);
                        string mode = sub.Str("mode", false, "interpolate");
                        int deg = sub.Int("degree", false, 3);
                        bool closed = sub.Bool("closed");
                        if (sub.Errors.Count > 0) { foreach (var e in sub.Errors) p.Err("'" + field + "': " + e); return null; }
                        if (pts.Count < 2) return null;
                        if (mode == "polyline")
                        {
                            if (closed && pts[0].DistanceTo(pts[pts.Count - 1]) > 1e-9) pts.Add(pts[0]);
                            return bag.Add(new PolylineCurve(pts));
                        }
                        if (mode == "control_points")
                        {
                            if (deg < 1 || pts.Count < deg + 1) { p.Err("'" + field + "': degree " + deg + " needs at least " + (deg + 1) + " control points"); return null; }
                            return bag.Add(Curve.CreateControlPointCurve(pts, deg));
                        }
                        if (mode == "interpolate") { var ic = InterpCurve(pts, deg, closed); if (ic == null) { p.Err("'" + field + "': could not interpolate a curve through these points (duplicates?)"); return null; } return bag.Add(ic); }
                        p.Err("'" + field + ".mode' must be interpolate | control_points | polyline"); return null;
                    }
                }
            }
            catch (Exception ex) { p.Err("'" + field + "': " + ex.GetType().Name + ": " + ex.Message); return null; }
            p.Err("'" + field + "' is not a valid curve spec. Use " + CurveHelp);
            return null;
        }

        static Curve InterpCurve(List<Point3d> pts, int degree, bool closed)
        {
            if (pts.Count == 2) return new LineCurve(pts[0], pts[1]);
            if (closed && pts[0].DistanceTo(pts[pts.Count - 1]) > 1e-9) { pts = new List<Point3d>(pts); pts.Add(pts[0]); }
            int d = Math.Max(1, degree);
            if (closed)
            {
                // periodic interpolation: drop the duplicated last point
                var open = pts.Take(pts.Count - 1).ToList();
                var c = Curve.CreateInterpolatedCurve(open, d == 1 ? 3 : d, CurveKnotStyle.ChordPeriodic);
                if (c != null) return c;
            }
            if (d == 1) return new PolylineCurve(pts);
            var r = Curve.CreateInterpolatedCurve(pts, d);
            if (r == null && d != 3) r = Curve.CreateInterpolatedCurve(pts, 3);
            if (r == null) r = Curve.CreateInterpolatedCurve(pts, 2);
            return r;
        }

        public static List<Curve> CurveList(P p, string field, int min, int max, Bag bag, bool keepEdge = false)
        {
            var res = new List<Curve>();
            var a = p.Tok(field) as JArray;
            if (a == null) { p.Err("'" + field + "' is required: array of curve specs (" + CurveHelp + ")"); return res; }
            if (a.Count < min || a.Count > max) { p.Err("'" + field + "' needs " + min + ".." + max + " curves (got " + a.Count + ")"); return res; }
            for (int i = 0; i < a.Count; i++)
            {
                var c = CurveSpec(p, a[i], field + "[" + i + "]", bag, keepEdge);
                if (c != null) res.Add(c);
            }
            return res;
        }

        /// <summary>Makes loft inputs compatible: consistent direction and (for closed curves) nearby seams.</summary>
        public static void AlignCurves(List<Curve> cs)
        {
            for (int i = 1; i < cs.Count; i++)
            {
                var prev = cs[i - 1]; var c = cs[i];
                try
                {
                    if (c.IsClosed && prev.IsClosed)
                    {
                        double t;
                        if (c.ClosestPoint(prev.PointAtStart, out t)) c.ChangeClosedCurveSeam(t);
                        var tp = prev.TangentAtStart; var tc = c.TangentAtStart;
                        if (tp.IsValid && tc.IsValid && tp * tc < 0) c.Reverse();
                    }
                    else if (!c.IsClosed && !prev.IsClosed)
                    {
                        double same = prev.PointAtStart.DistanceTo(c.PointAtStart) + prev.PointAtEnd.DistanceTo(c.PointAtEnd);
                        double flip = prev.PointAtStart.DistanceTo(c.PointAtEnd) + prev.PointAtEnd.DistanceTo(c.PointAtStart);
                        if (flip < same) c.Reverse();
                    }
                }
                catch (Exception) { }
            }
        }

        public static BlendContinuity ParseBlend(P p, string field, string def)
        {
            string s = (p.Str(field, false, def) ?? def).ToLowerInvariant();
            switch (s)
            {
                case "g0": case "position": return BlendContinuity.Position;
                case "g1": case "tangent": case "tangency": return BlendContinuity.Tangency;
                case "g2": case "curvature": return BlendContinuity.Curvature;
            }
            p.Err("'" + field + "' must be G0 | G1 | G2");
            return BlendContinuity.Tangency;
        }

        // ---------------------------------------------------------------- reporting

        public static int Kinks(NurbsSurface ns, int dir)
        {
            try
            {
                var k = dir == 0 ? ns.KnotsU : ns.KnotsV;
                int deg = ns.Degree(dir);
                if (deg < 2) return 0;
                int n = k.Count, count = 0;
                for (int i = deg; i <= n - deg - 1;)
                {
                    int m = k.KnotMultiplicity(i);
                    if (m <= 0) m = 1;
                    if (m >= deg) count++;
                    i += m;
                }
                return count;
            }
            catch (Exception) { return 0; }
        }

        public static Dictionary<string, object> FaceInfo(BrepFace f, double tol)
        {
            var d = new Dictionary<string, object>();
            NurbsSurface tmp = null;
            try
            {
                var ns = f.UnderlyingSurface() as NurbsSurface;
                if (ns == null) { tmp = f.ToNurbsSurface(); ns = tmp; d["surface_type"] = f.UnderlyingSurface().GetType().Name; }
                else d["surface_type"] = "nurbs";
                if (ns != null)
                {
                    d["degree_u"] = ns.Degree(0); d["degree_v"] = ns.Degree(1);
                    d["cp_u"] = ns.Points.CountU; d["cp_v"] = ns.Points.CountV;
                    d["rational"] = ns.IsRational;
                    d["kinks_u"] = Kinks(ns, 0); d["kinks_v"] = Kinks(ns, 1);
                    d["periodic_u"] = ns.IsPeriodic(0); d["periodic_v"] = ns.IsPeriodic(1);
                }
                d["closed_u"] = f.IsClosed(0); d["closed_v"] = f.IsClosed(1);
                d["planar"] = f.IsPlanar(tol);
                d["trimmed"] = f.OuterLoop != null && f.Loops.Count > 0 && !IsFullDomain(f);
            }
            catch (Exception ex) { d["info_error"] = ex.Message; }
            finally { if (tmp != null) { try { tmp.Dispose(); } catch (Exception) { } } }
            return d;
        }

        static bool IsFullDomain(BrepFace f)
        {
            try
            {
                var s = f.UnderlyingSurface();
                var bb = f.GetBoundingBox(true);
                var sb = s.GetBoundingBox(true);
                return bb.Min.DistanceTo(sb.Min) < 1e-6 && bb.Max.DistanceTo(sb.Max) < 1e-6 && f.Loops.Count == 1;
            }
            catch (Exception) { return false; }
        }

        public static Dictionary<string, object> BrepInfo(Brep b, double tol)
        {
            var d = new Dictionary<string, object>();
            d["faces"] = b.Faces.Count;
            d["edges"] = b.Edges.Count;
            d["naked_edges"] = b.Edges.Count(e => e.Valence == EdgeAdjacency.Naked);
            d["solid"] = b.IsSolid;
            try { var am = AreaMassProperties.Compute(b); if (am != null) d["area"] = R(am.Area); } catch (Exception) { }
            if (b.IsSolid) d["volume"] = R(GeoUtil.Volume(b));
            if (b.Faces.Count == 1)
            {
                foreach (var kv in FaceInfo(b.Faces[0], tol)) d[kv.Key] = kv.Value;
            }
            else
            {
                d["face_info"] = Enumerable.Range(0, Math.Min(b.Faces.Count, 12)).Select(i =>
                {
                    var fi = FaceInfo(b.Faces[i], tol); fi["face"] = i; return (object)fi;
                }).ToList();
            }
            return d;
        }

        /// <summary>Validates, adds the geometry (Brep/Curve) to the document and fills the result dictionary. Returns an error or null.</summary>
        public static string AddAndDescribe(ToolContext ctx, GeometryBase g, ObjectAttributes attr, Dictionary<string, object> r, List<Guid> ids)
        {
            var doc = ctx.Doc;
            if (g == null) return "operation returned no geometry";
            string log;
            if (!g.IsValidWithLog(out log)) return "result is invalid: " + (GeoUtil.FirstLines(log, 2, 240) ?? "no log");
            var b = g as Brep;
            if (b != null && b.Faces.Count == 0) return "result has no faces";
            Guid id = GeoUtil.AddGeometry(doc, g, attr);
            if (id == Guid.Empty) return "Rhino rejected the geometry";
            ids.Add(id);
            r["handle"] = ctx.Handles.For(id);
            r["bbox"] = GeoUtil.BBox(g.GetBoundingBox(true));
            if (b != null) foreach (var kv in BrepInfo(b, ctx.Tol)) r[kv.Key] = kv.Value;
            else { var c = g as Curve; if (c != null) { r["length"] = R(c.GetLength()); r["closed"] = c.IsClosed; r["degree"] = c.Degree; } }
            return null;
        }

        public static ToolResult ArgFail(IEnumerable<string> errs)
        {
            var l = errs.ToList();
            return ToolResult.Fail("INVALID_ARGS", string.Join("; ", l), "Fix the arguments and call again.", new { errors = l });
        }
    }
}

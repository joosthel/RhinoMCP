using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCP.Tools.Measure
{
    /// <summary>Normalised geometry kinds used by the measure tools.</summary>
    public enum GKind { None, Point, Curve, Brep, Mesh }

    /// <summary>Geometry wrapper for one document object: Extrusion/Surface/SubD are converted to Brep once.</summary>
    public sealed class Geo
    {
        public RhinoObject Obj;
        public GKind Kind;
        public GeometryBase G;      // Point | Curve | Brep | Mesh
        public BoundingBox Box;
        public string Handle;
        public string Layer;
        public string Reason;       // set when Kind == None
        Mesh _mesh;

        public static Geo From(RhinoObject o, ToolContext ctx)
        {
            var r = new Geo { Obj = o, Handle = ctx.Handles.For(o.Id), Layer = LayerOf(o, ctx.Doc) };
            GeometryBase g = o.Geometry;
            try
            {
                if (g is Rhino.Geometry.Point) { r.Kind = GKind.Point; r.G = g; }
                else if (g is Curve) { r.Kind = GKind.Curve; r.G = g; }
                else if (g is Brep) { r.Kind = GKind.Brep; r.G = g; }
                else if (g is Mesh) { r.Kind = GKind.Mesh; r.G = g; }
                else if (g is Extrusion) { var b = ((Extrusion)g).ToBrep(true); if (b != null) { r.Kind = GKind.Brep; r.G = b; } }
                else if (g is Surface) { var b = ((Surface)g).ToBrep(); if (b != null) { r.Kind = GKind.Brep; r.G = b; } }
                else if (g is SubD) { var b = ((SubD)g).ToBrep(); if (b != null) { r.Kind = GKind.Brep; r.G = b; } }
            }
            catch (Exception) { r.Kind = GKind.None; r.G = null; }
            if (r.Kind == GKind.None) r.Reason = "unsupported_type:" + o.ObjectType;
            else r.Box = r.G.GetBoundingBox(true);
            return r;
        }

        public static string LayerOf(RhinoObject o, RhinoDoc doc)
        {
            var l = doc.Layers[o.Attributes.LayerIndex];
            return l == null ? "?" : l.FullPath;
        }

        /// <summary>Mesh used for mesh-based clash tests (cached; for Brep it is a fast render mesh).</summary>
        public Mesh AsMesh()
        {
            if (_mesh != null) return _mesh;
            if (Kind == GKind.Mesh) _mesh = (Mesh)G;
            else if (Kind == GKind.Brep)
            {
                var ms = Mesh.CreateFromBrep((Brep)G, MeshingParameters.FastRenderMesh);
                if (ms != null && ms.Length > 0)
                {
                    var m = new Mesh();
                    foreach (var p in ms) if (p != null) m.Append(p);
                    _mesh = m;
                }
            }
            return _mesh;
        }

        public bool IsSolid
        {
            get
            {
                if (Kind == GKind.Brep) return ((Brep)G).IsSolid;
                if (Kind == GKind.Mesh) return ((Mesh)G).IsClosed;
                return false;
            }
        }
    }

    public static class MU
    {
        public static double R(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? 0.0 : Math.Round(v, 6); }
        public static double[] P(Point3d p) { return new[] { R(p.X), R(p.Y), R(p.Z) }; }
        public static object Box(BoundingBox b)
        {
            return b.IsValid ? new { min = P(b.Min), max = P(b.Max) } : null;
        }

        /// <summary>Distance between two boxes (0 if overlapping).</summary>
        public static double BoxDistance(BoundingBox a, BoundingBox b)
        {
            double dx = Math.Max(0, Math.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X));
            double dy = Math.Max(0, Math.Max(a.Min.Y - b.Max.Y, b.Min.Y - a.Max.Y));
            double dz = Math.Max(0, Math.Max(a.Min.Z - b.Max.Z, b.Min.Z - a.Max.Z));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static List<Geo> Wrap(IEnumerable<RhinoObject> objs, ToolContext ctx, Dictionary<string, int> skipped, List<string> skippedExamples)
        {
            var res = new List<Geo>();
            foreach (var o in objs)
            {
                var g = Geo.From(o, ctx);
                if (g.Kind == GKind.None)
                {
                    int c; skipped.TryGetValue(g.Reason, out c); skipped[g.Reason] = c + 1;
                    if (skippedExamples != null && skippedExamples.Count < 10) skippedExamples.Add(g.Handle);
                }
                else res.Add(g);
            }
            return res;
        }

        // ---------- closest point machinery ----------

        /// <summary>Closest point on a geometry to a test point (null if it could not be computed).</summary>
        public static Point3d? Closest(Geo g, Point3d p)
        {
            switch (g.Kind)
            {
                case GKind.Point: return ((Rhino.Geometry.Point)g.G).Location;
                case GKind.Curve:
                    {
                        double t;
                        var c = (Curve)g.G;
                        return c.ClosestPoint(p, out t) ? (Point3d?)c.PointAt(t) : null;
                    }
                case GKind.Brep:
                    {
                        var q = ((Brep)g.G).ClosestPoint(p);
                        return q.IsValid ? (Point3d?)q : null;
                    }
                case GKind.Mesh:
                    {
                        var q = ((Mesh)g.G).ClosestPoint(p);
                        return q.IsValid ? (Point3d?)q : null;
                    }
            }
            return null;
        }

        /// <summary>Representative sample points of a geometry (seeds for the alternating projection).</summary>
        public static List<Point3d> Samples(Geo g)
        {
            var pts = new List<Point3d>();
            switch (g.Kind)
            {
                case GKind.Point: pts.Add(((Rhino.Geometry.Point)g.G).Location); break;
                case GKind.Curve:
                    {
                        var c = (Curve)g.G;
                        int n = 48;
                        for (int i = 0; i <= n; i++) pts.Add(c.PointAtNormalizedLength((double)i / n));
                        break;
                    }
                case GKind.Brep:
                    {
                        var b = (Brep)g.G;
                        foreach (var v in b.Vertices) pts.Add(v.Location);
                        foreach (var e in b.Edges)
                            for (int i = 1; i < 8; i++) pts.Add(e.PointAtNormalizedLength(i / 8.0));
                        foreach (var f in b.Faces)
                        {
                            var du = f.Domain(0); var dv = f.Domain(1);
                            const int n = 6;
                            for (int i = 0; i <= n; i++)
                                for (int j = 0; j <= n; j++)
                                {
                                    double u = du.ParameterAt((i + 0.5) / (n + 1)), v = dv.ParameterAt((j + 0.5) / (n + 1));
                                    if (f.IsPointOnFace(u, v) != PointFaceRelation.Interior) continue;
                                    pts.Add(f.PointAt(u, v));
                                }
                        }
                        break;
                    }
                case GKind.Mesh:
                    {
                        var m = (Mesh)g.G;
                        int step = Math.Max(1, m.Vertices.Count / 1500);
                        for (int i = 0; i < m.Vertices.Count; i += step) pts.Add(m.Vertices[i]);
                        break;
                    }
            }
            return pts;
        }

        public sealed class DistResult
        {
            public double Distance = double.MaxValue;
            public Point3d PA, PB;
            public string Method;
            public bool Valid { get { return Distance < double.MaxValue; } }
        }

        /// <summary>
        /// Minimum distance between two geometries.
        /// Point-vs-anything and Curve-vs-Curve are exact (ClosestPoint / Curve.ClosestPoints).
        /// Everything else: intersection test (distance 0 if they cross), then seeded alternating projection:
        /// take the K best sample points on each side and repeatedly project A-&gt;B-&gt;A until converged. This converges
        /// to a local minimum; for ordinary and convex parts that is the global one and the value is accurate to
        /// ~1e-9 relative. Seeds are coarse, so thin, strongly non-convex shapes can report a slightly larger value.
        /// Containment (one solid inside another without touching) is NOT reported as 0; distance is boundary to boundary.
        /// </summary>
        public static DistResult Distance(Geo a, Geo b, double tol)
        {
            var r = new DistResult();
            if (a.Kind == GKind.Point && b.Kind == GKind.Point)
            {
                r.PA = ((Rhino.Geometry.Point)a.G).Location; r.PB = ((Rhino.Geometry.Point)b.G).Location;
                r.Distance = r.PA.DistanceTo(r.PB); r.Method = "exact"; return r;
            }
            if (a.Kind == GKind.Point || b.Kind == GKind.Point)
            {
                bool aPt = a.Kind == GKind.Point;
                var pt = ((Rhino.Geometry.Point)(aPt ? a.G : b.G)).Location;
                var other = aPt ? b : a;
                var q = Closest(other, pt);
                if (q.HasValue)
                {
                    r.PA = aPt ? pt : q.Value; r.PB = aPt ? q.Value : pt;
                    r.Distance = pt.DistanceTo(q.Value); r.Method = "exact";
                }
                return r;
            }
            if (a.Kind == GKind.Curve && b.Kind == GKind.Curve)
            {
                Point3d pa, pb;
                if (((Curve)a.G).ClosestPoints((Curve)b.G, out pa, out pb))
                { r.PA = pa; r.PB = pb; r.Distance = pa.DistanceTo(pb); r.Method = "exact (Curve.ClosestPoints)"; return r; }
            }
            Point3d hit;
            if (TryIntersect(a, b, tol, out hit))
            { r.PA = hit; r.PB = hit; r.Distance = 0; r.Method = "intersecting"; return r; }

            var sa = Samples(a); var sb = Samples(b);
            const int K = 4;
            for (int side = 0; side < 2; side++)
            {
                var from = side == 0 ? a : b; var to = side == 0 ? b : a;
                var seeds = (side == 0 ? sa : sb)
                    .Select(p => new { p = p, q = Closest(to, p) })
                    .Where(x => x.q.HasValue)
                    .OrderBy(x => x.p.DistanceTo(x.q.Value)).Take(K).ToList();
                foreach (var s in seeds)
                {
                    Point3d pf = s.p, pt2 = s.q.Value;
                    double prev = pf.DistanceTo(pt2);
                    for (int it = 0; it < 40; it++)
                    {
                        var nf = Closest(from, pt2); if (!nf.HasValue) break;
                        var nt = Closest(to, nf.Value); if (!nt.HasValue) break;
                        pf = nf.Value; pt2 = nt.Value;
                        double d = pf.DistanceTo(pt2);
                        if (prev - d < 1e-10 * Math.Max(1, prev)) break;
                        prev = d;
                    }
                    double dist = pf.DistanceTo(pt2);
                    if (dist < r.Distance)
                    {
                        r.Distance = dist;
                        r.PA = side == 0 ? pf : pt2; r.PB = side == 0 ? pt2 : pf;
                        r.Method = "seeded alternating projection";
                    }
                }
            }
            return r;
        }

        /// <summary>True if the two geometries intersect (touching counts); hit = a representative intersection point.</summary>
        public static bool TryIntersect(Geo a, Geo b, double tol, out Point3d hit)
        {
            hit = Point3d.Unset;
            try
            {
                Curve[] cs = null; Point3d[] ps = null;
                if (a.Kind == GKind.Brep && b.Kind == GKind.Brep)
                {
                    if (!Rhino.Geometry.Intersect.Intersection.BrepBrep((Brep)a.G, (Brep)b.G, tol, out cs, out ps)) return false;
                }
                else if (a.Kind == GKind.Curve && b.Kind == GKind.Brep)
                {
                    if (!Rhino.Geometry.Intersect.Intersection.CurveBrep((Curve)a.G, (Brep)b.G, tol, out cs, out ps)) return false;
                }
                else if (a.Kind == GKind.Brep && b.Kind == GKind.Curve)
                {
                    if (!Rhino.Geometry.Intersect.Intersection.CurveBrep((Curve)b.G, (Brep)a.G, tol, out cs, out ps)) return false;
                }
                else if ((a.Kind == GKind.Mesh || b.Kind == GKind.Mesh) && a.Kind != GKind.Curve && b.Kind != GKind.Curve)
                {
                    var ma = a.AsMesh(); var mb = b.AsMesh();
                    if (ma == null || mb == null) return false;
                    var lines = Rhino.Geometry.Intersect.Intersection.MeshMeshFast(ma, mb);
                    if (lines == null || lines.Length == 0) return false;
                    hit = lines[0].From; return true;
                }
                else return false;
                if (cs != null && cs.Length > 0) { hit = cs[0].PointAtStart; return true; }
                if (ps != null && ps.Length > 0) { hit = ps[0]; return true; }
            }
            catch (Exception) { }
            return false;
        }
    }
}

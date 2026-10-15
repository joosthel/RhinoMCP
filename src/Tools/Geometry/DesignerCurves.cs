using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Geometry
{
    /// <summary>One glyph region: an outer loop with its counters (holes).</summary>
    public sealed class Glyph
    {
        public Curve Outer;
        public List<Curve> Holes = new List<Curve>();
        public double Area;   // net area (outer minus holes)
    }

    /// <summary>Modelling helpers for product design: text outlines, glyph grouping, line + blend curves, curve facts.</summary>
    public static class DesignerCurves
    {
        public const int MaxTextChars = 200;

        /// <summary>Outline curves of a string on a plane (closed loops). Anchored at the plane origin (left baseline) or centered.</summary>
        public static List<Curve> TextOutlines(string text, string font, double height, Plane plane, double tol, bool bold, bool italic, bool center)
        {
            int style = (bold ? 1 : 0) | (italic ? 2 : 0);
            var arr = Curve.CreateTextOutlines(text, font, height, style, true, plane, 1.0, tol);
            var list = new List<Curve>();
            if (arr != null) foreach (var c in arr) if (c != null && c.IsValid) list.Add(c);
            if (center && list.Count > 0)
            {
                var bb = BoundingBox.Empty;
                foreach (var c in list) bb.Union(c.GetBoundingBox(plane));
                // GetBoundingBox(plane) is expressed in plane coordinates: move its center onto the plane origin.
                double cx = (bb.Min.X + bb.Max.X) / 2, cy = (bb.Min.Y + bb.Max.Y) / 2;
                var shift = -(plane.XAxis * cx + plane.YAxis * cy);
                var xf = Transform.Translation(shift);
                foreach (var c in list) c.Transform(xf);
            }
            return list;
        }

        static double AreaOf(Curve c)
        {
            try { var am = AreaMassProperties.Compute(c); return am == null ? 0 : Math.Abs(am.Area); } catch (Exception) { return 0; }
        }

        /// <summary>Groups closed coplanar curves into glyphs by nesting depth (even depth = outer loop, odd = counter of its smallest container).</summary>
        public static List<Glyph> GroupGlyphs(List<Curve> curves, Plane plane, double tol)
        {
            int n = curves.Count;
            var area = curves.Select(AreaOf).ToArray();
            var parent = new int[n];
            var depth = new int[n];
            for (int j = 0; j < n; j++)
            {
                parent[j] = -1;
                var pt = curves[j].PointAt(curves[j].Domain.ParameterAt(0.37));
                for (int i = 0; i < n; i++)
                {
                    if (i == j || area[i] <= area[j]) continue;
                    if (curves[i].Contains(pt, plane, tol) == PointContainment.Inside)
                    {
                        depth[j]++;
                        if (parent[j] < 0 || area[i] < area[parent[j]]) parent[j] = i;
                    }
                }
            }
            var glyphs = new List<Glyph>();
            var byOuter = new Dictionary<int, Glyph>();
            for (int j = 0; j < n; j++)
                if (depth[j] % 2 == 0) { var g = new Glyph { Outer = curves[j], Area = area[j] }; glyphs.Add(g); byOuter[j] = g; }
            for (int j = 0; j < n; j++)
                if (depth[j] % 2 == 1 && parent[j] >= 0 && byOuter.ContainsKey(parent[j]))
                { byOuter[parent[j]].Holes.Add(curves[j]); byOuter[parent[j]].Area -= area[j]; }
            // reading order: left to right
            return glyphs.OrderBy(g => g.Outer.GetBoundingBox(true).Min.X).ToList();
        }

        /// <summary>Planar brep (with holes) for a glyph.</summary>
        public static Brep GlyphBrep(Glyph g, double tol)
        {
            var loops = new List<Curve> { g.Outer }; loops.AddRange(g.Holes);
            var bs = Brep.CreatePlanarBreps(loops, tol);
            if (bs == null || bs.Length == 0) return null;
            return bs[0];
        }

        /// <summary>Straight span plus a smooth (G1/G2) blend into a curve through the given points. Returns [line, blend, tail].</summary>
        public static List<Curve> LineBlend(Point3d a, Point3d b, List<Point3d> pts, int degree, bool interpolate, string continuity, double bulge, double tol, out string error)
        {
            error = null;
            var line = new LineCurve(a, b);
            Curve tail;
            if (interpolate) tail = Curve.CreateInterpolatedCurve(pts, degree);
            else
            {
                if (pts.Count < degree + 1) { error = "degree " + degree + " needs at least " + (degree + 1) + " 'points' for the curve after the line"; return null; }
                tail = Curve.CreateControlPointCurve(pts, degree);
            }
            if (tail == null || !tail.IsValid) { error = "could not build the curve through 'points'"; return null; }
            var cont = continuity == "G2" ? BlendContinuity.Curvature : BlendContinuity.Tangency;
            var blend = Curve.CreateBlendCurve(line, tail, cont, bulge, bulge);
            if (blend == null || !blend.IsValid)
            {
                error = "blend between the line end " + Fmt(b) + " and the curve start " + Fmt(tail.PointAtStart) + " failed; move the first 'points' entry so the blend can turn (not behind the line end), or lower 'bulge'";
                return null;
            }
            return new List<Curve> { line, blend, tail };
        }

        public static string Fmt(Point3d p) { return "[" + GeoUtil.R(p.X) + "," + GeoUtil.R(p.Y) + "," + GeoUtil.R(p.Z) + "]"; }

        /// <summary>Angle in degrees between the end tangent of a and the start tangent of b.</summary>
        public static double TangentGap(Curve a, Curve b)
        {
            var ta = a.TangentAtEnd; var tb = b.TangentAtStart;
            ta.Unitize(); tb.Unitize();
            return RhinoMath.ToDegrees(Vector3d.VectorAngle(ta, tb));
        }

        /// <summary>Degree, control points, periodic, closed, kinks (G1 breaks) for a curve.</summary>
        public static void CurveFacts(Curve c, Dictionary<string, object> d)
        {
            try
            {
                var nc = c as NurbsCurve;
                if (nc == null && !(c is PolyCurve)) { try { nc = c.ToNurbsCurve(); } catch (Exception) { } }
                d["degree"] = c.Degree;
                if (nc != null) d["cp"] = nc.Points.Count;
                if (c is PolyCurve) d["segments"] = ((PolyCurve)c).SegmentCount;
                d["periodic"] = c.IsPeriodic;
                int kinks = 0; double t = c.Domain.T0;
                double t1 = c.Domain.T1;
                int guard = 0;
                while (guard++ < 500)
                {
                    double nt;
                    if (!c.GetNextDiscontinuity(Continuity.G1_continuous, t, t1, out nt)) break;
                    kinks++; t = nt;
                }
                if (c.IsClosed && !c.IsPeriodic && c.Domain.Length > 0)
                {
                    var tE = c.TangentAtEnd; var tS = c.TangentAtStart; tE.Unitize(); tS.Unitize();
                    if (Vector3d.VectorAngle(tE, tS) > RhinoMath.ToRadians(1.0)) kinks++;
                }
                d["kinks"] = kinks;
            }
            catch (Exception) { }
        }
    }
}

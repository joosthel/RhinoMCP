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
    public sealed class AnalyzeContinuity : ITool
    {
        public string Name { get { return "analyze_continuity"; } }
        public ToolKind Kind { get { return ToolKind.Read; } }
        public string Description
        {
            get
            {
                return "Numeric G0/G1/G2 continuity between two surfaces (or brep faces) along their shared or nearest edge, or between two curves at their touching ends. " +
                       "a, b: handle | {target, face?, edge?} (face needed for multi-face breps; the edge is auto-picked as the closest edge pair when omitted; list edge indices with analyze_surface). " +
                       "Samples N points (default 21, 3..201) along edge a, pairs each with the closest point on edge b. Reports max_gap (G0), max_angle_deg between surface normals (G1, folded to 0..90), " +
                       "max_curv_diff_pct (G2: difference of normal curvature across the edge, relative to the larger; plus absolute). " +
                       "Class: none if gap > g0_tol (default document tolerance); G0 if angle > g1_tol_deg (default document angle tolerance); G1 if curvature diff > g2_tol_pct (default 10); else G2. " +
                       "Curves: one comparison at the nearest end pair (angle between the continuing tangents, curvature vector difference). Includes the worst sample location. " +
                       "Flat-to-flat counts as G2 (zero curvature on both sides).";
            }
        }
        public string InputSchema
        {
            get
            {
                string refS = "{\"description\":\"handle or {target, face?, edge?}\"}";
                return Schemas.Object(
                    "\"a\":" + refS + ",\"b\":" + refS + ",\"samples\":{\"type\":\"integer\",\"minimum\":3,\"maximum\":201}," +
                    "\"g0_tol\":{\"type\":\"number\"},\"g1_tol_deg\":{\"type\":\"number\"},\"g2_tol_pct\":{\"type\":\"number\"},\"include_samples\":{\"type\":\"boolean\"}",
                    "\"a\",\"b\"");
            }
        }

        sealed class Smp { public double T, Gap, Angle, Ka, Kb, CurvAbs, CurvPct, Dot; public Point3d Pa, Pb; }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            if (ctx.Doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active document");
            var p = new P(args, ctx, new Frame(Plane.WorldXY, "world_xy"));
            int n = p.Int("samples", false, 21);
            double g0 = p.Num("g0_tol", false, ctx.Tol), g1 = p.Num("g1_tol_deg", false, ctx.AngleTolDeg), g2 = p.Num("g2_tol_pct", false, 10.0);
            bool inc = p.Bool("include_samples");
            if (n < 3 || n > 201) p.Err("'samples' must be 3..201");
            using (var bag = new Bag())
            {
                var a = FF.ResolveRef(p, p.Tok("a"), "a", true, true, bag);
                var b = FF.ResolveRef(p, p.Tok("b"), "b", true, true, bag);
                if (a == null || b == null || p.Errors.Count > 0) return FF.ArgFail(p.Errors);
                try
                {
                    if (a.Curve != null || b.Curve != null)
                    {
                        if (a.Curve == null || b.Curve == null) return ToolResult.Fail("INVALID_ARGS", "a and b must both be curves or both be surfaces/faces", "To compare a curve with a surface edge pass {target, edge} for a curve spec, or use analyze_surface.");
                        return CurvePair(ctx, a, b, g0, g1, g2);
                    }
                    return SurfacePair(ctx, a, b, n, g0, g1, g2, inc);
                }
                catch (Exception ex) { return ToolResult.Fail("INTERNAL", ex.GetType().Name + ": " + ex.Message); }
            }
        }

        static string Classify(double gap, double angle, double pct, double g0, double g1, double g2)
        {
            if (gap > g0) return "none";
            if (angle > g1) return "G0";
            if (pct > g2) return "G1";
            return "G2";
        }

        static object Thresholds(double g0, double g1, double g2)
        {
            return new { g0_gap_max = g0, g1_angle_deg_max = g1, g2_curv_diff_pct_max = g2, note = "G0: gap <= g0; G1: also angle <= g1; G2: also curvature difference <= g2 percent" };
        }

        // mean distance from sample points on ca to cb
        static double EdgeScore(Curve ca, Curve cb)
        {
            double sum = 0; int m = 5;
            for (int i = 0; i < m; i++)
            {
                var pa = ca.PointAt(ca.Domain.ParameterAt(i / (double)(m - 1)));
                double t;
                if (!cb.ClosestPoint(pa, out t)) return double.MaxValue;
                sum += pa.DistanceTo(cb.PointAt(t));
            }
            return sum / m;
        }

        /// <summary>Normal-section curvature along the 3D tangent direction dir, signed against nref. Orientation-free (uses Su, Sv, Suu, Suv, Svv).</summary>
        static double NormalCurvature(Vector3d[] d, Vector3d dir, Vector3d nref)
        {
            Vector3d su = d[0], sv = d[1];
            double g11 = su * su, g12 = su * sv, g22 = sv * sv;
            double det = g11 * g22 - g12 * g12;
            if (det < 1e-18) return double.NaN;
            double r1 = dir * su, r2 = dir * sv;
            double a = (r1 * g22 - r2 * g12) / det, b = (r2 * g11 - r1 * g12) / det;
            var t = su * a + sv * b;
            double tt = t * t;
            if (tt < 1e-18) return double.NaN;
            var acc = d[2] * (a * a) + d[3] * (2 * a * b) + d[4] * (b * b);
            return (acc * nref) / tt;
        }

        ToolResult SurfacePair(ToolContext ctx, Ref a, Ref b, int n, double g0, double g1, double g2, bool inc)
        {
            Curve ca, cb;
            int ea = a.EdgeIndex, eb = b.EdgeIndex;
            if (a.Edge == null || b.Edge == null)
            {
                // choose the closest edge pair (restricting to the user-given edge on one side when present)
                var ia = a.Edge != null ? new[] { a.EdgeIndex } : a.Face.AdjacentEdges();
                var ib = b.Edge != null ? new[] { b.EdgeIndex } : b.Face.AdjacentEdges();
                double best = double.MaxValue; int bi = -1, bj = -1;
                foreach (int i in ia)
                    foreach (int j in ib)
                    {
                        double s1 = EdgeScore(a.Brep.Edges[i], b.Brep.Edges[j]);
                        double s2 = EdgeScore(b.Brep.Edges[j], a.Brep.Edges[i]);
                        double s = (s1 + s2) * 0.5;
                        if (s < best - 1e-12) { best = s; bi = i; bj = j; }
                    }
                if (bi < 0) return ToolResult.Fail("GEOMETRY_FAILED", "could not find an edge pair", "Pass explicit edge indices (see analyze_surface edges).");
                ea = bi; eb = bj;
            }
            ca = a.Brep.Edges[ea]; cb = b.Brep.Edges[eb];
            var smp = new List<Smp>();
            double lenA = ca.GetLength();
            for (int i = 0; i < n; i++)
            {
                double t = ca.Domain.ParameterAt(i / (double)(n - 1));
                var pa = ca.PointAt(t);
                double tb;
                if (!cb.ClosestPoint(pa, out tb)) continue;
                var pb = cb.PointAt(tb);
                double ua, va, ub, vb;
                if (!a.Face.ClosestPoint(pa, out ua, out va) || !b.Face.ClosestPoint(pb, out ub, out vb)) continue;
                Point3d p0; Vector3d[] dA, dB;
                if (!a.Face.Evaluate(ua, va, 2, out p0, out dA) || !b.Face.Evaluate(ub, vb, 2, out p0, out dB) || dA.Length < 5 || dB.Length < 5) continue;
                var na = Vector3d.CrossProduct(dA[0], dA[1]); var nb = Vector3d.CrossProduct(dB[0], dB[1]);
                var tan = ca.TangentAt(t);
                if (!na.Unitize() || !nb.Unitize() || !tan.IsValid) continue;
                double dot = Math.Max(-1, Math.Min(1, na * nb));
                double ang = Math.Acos(Math.Abs(dot)) * 180.0 / Math.PI;
                if (dot < 0) nb = -nb;   // align B's normal with A's for the curvature sign
                // curvature across the edge: direction in the tangent plane perpendicular to the edge
                var da = Vector3d.CrossProduct(na, tan); var tanB = cb.TangentAt(tb);
                var db = Vector3d.CrossProduct(nb, tanB.IsValid ? tanB : tan);
                double ka = 0, kb = 0;
                if (da.Length > 1e-9 && db.Length > 1e-9)
                {
                    ka = NormalCurvature(dA, da, na);
                    kb = NormalCurvature(dB, db, nb);
                    if (!FF.Finite(ka) || !FF.Finite(kb)) continue;
                }
                double cabs = Math.Abs(ka - kb);
                double mx = Math.Max(Math.Abs(ka), Math.Abs(kb));
                double pct = mx < 1e-12 ? 0 : cabs / mx * 100.0;
                smp.Add(new Smp { T = i / (double)(n - 1), Gap = pa.DistanceTo(pb), Angle = ang, Ka = ka, Kb = kb, Dot = dot, CurvAbs = cabs, CurvPct = pct, Pa = pa, Pb = pb });
            }
            if (smp.Count == 0) return ToolResult.Fail("GEOMETRY_FAILED", "no valid samples (closest-point or curvature evaluation failed)", "Check the faces are valid; pass explicit edges.");
            double maxGap = smp.Max(s => s.Gap), maxAng = smp.Max(s => s.Angle), maxPct = smp.Max(s => s.CurvPct), maxAbs = smp.Max(s => s.CurvAbs);
            var worst = smp.OrderByDescending(s => (s.Gap > g0 ? 3e6 : (s.Angle > g1 ? 2e6 : (s.CurvPct > g2 ? 1e6 : 0.0))) + s.Gap + s.Angle + s.CurvPct).First();
            var data = new Dictionary<string, object>();
            data["class"] = Classify(maxGap, maxAng, maxPct, g0, g1, g2);
            data["max_gap"] = FF.R(maxGap);
            data["max_angle_deg"] = FF.R4(maxAng);
            data["max_curv_diff_pct"] = FF.R4(maxPct);
            data["max_curv_diff_abs"] = Math.Round(maxAbs, 9);
            data["samples"] = smp.Count;
            data["edge_a"] = new { target = a.Handle, face = a.FaceIndex, edge = ea, length = FF.R(lenA) };
            data["edge_b"] = new { target = b.Handle, face = b.FaceIndex, edge = eb, length = FF.R(cb.GetLength()) };
            data["same_object"] = a.Obj.Id == b.Obj.Id;
            data["worst"] = new { t = FF.R4(worst.T), at = GeoUtil.Arr(worst.Pa), gap = FF.R(worst.Gap), angle_deg = FF.R4(worst.Angle), curv_diff_pct = FF.R4(worst.CurvPct), curvature_a = Math.Round(worst.Ka, 9), curvature_b = Math.Round(worst.Kb, 9) };
            data["thresholds"] = Thresholds(g0, g1, g2);
            if (inc) data["sample_list"] = smp.Select(s => new { t = FF.R4(s.T), gap = FF.R(s.Gap), angle_deg = FF.R4(s.Angle), curv_pct = FF.R4(s.CurvPct), curvature_a = Math.Round(s.Ka, 9), curvature_b = Math.Round(s.Kb, 9), normals_dot = FF.R4(s.Dot) }).ToList();
            return ToolResult.Ok(data);
        }

        ToolResult CurvePair(ToolContext ctx, Ref a, Ref b, double g0, double g1, double g2)
        {
            var ca = a.Curve; var cb = b.Curve;
            bool aStart = false, bStart = true; double best = double.MaxValue;
            foreach (var sa in new[] { false, true })
                foreach (var sb in new[] { false, true })
                {
                    double d = (sa ? ca.PointAtStart : ca.PointAtEnd).DistanceTo(sb ? cb.PointAtStart : cb.PointAtEnd);
                    if (d < best) { best = d; aStart = sa; bStart = sb; }
                }
            var pa = aStart ? ca.PointAtStart : ca.PointAtEnd; var pb = bStart ? cb.PointAtStart : cb.PointAtEnd;
            var ta = ca.TangentAt(aStart ? ca.Domain.Min : ca.Domain.Max); if (aStart) ta = -ta;     // leaving a at the joint
            var tb = cb.TangentAt(bStart ? cb.Domain.Min : cb.Domain.Max); if (!bStart) tb = -tb;    // leaving the joint along b
            // for a smooth join, the direction arriving along a equals the direction leaving along b
            var arrive = ta;
            double ang = Vector3d.VectorAngle(arrive, tb) * 180.0 / Math.PI;
            var ka = ca.CurvatureAt(aStart ? ca.Domain.Min : ca.Domain.Max); var kb = cb.CurvatureAt(bStart ? cb.Domain.Min : cb.Domain.Max);
            double la = ka.IsValid ? ka.Length : 0, lb = kb.IsValid ? kb.Length : 0;
            double dk = (ka.IsValid && kb.IsValid) ? (ka - kb).Length : Math.Abs(la - lb);
            double mx = Math.Max(la, lb);
            double pct = mx < 1e-12 ? 0 : dk / mx * 100.0;
            var data = new Dictionary<string, object>();
            data["class"] = Classify(best, ang, pct, g0, g1, g2);
            data["max_gap"] = FF.R(best);
            data["max_angle_deg"] = FF.R4(ang);
            data["max_curv_diff_pct"] = FF.R4(pct);
            data["max_curv_diff_abs"] = Math.Round(dk, 9);
            data["samples"] = 1;
            data["joint"] = new { a_end = aStart ? "start" : "end", b_end = bStart ? "start" : "end", at = GeoUtil.Arr(pa) };
            data["thresholds"] = Thresholds(g0, g1, g2);
            return ToolResult.Ok(data);
        }
    }

    public sealed class AnalyzeSurface : ITool
    {
        public string Name { get { return "analyze_surface"; } }
        public ToolKind Kind { get { return ToolKind.Read; } }
        public string Description
        {
            get
            {
                return "Numbers about a surface or brep (all faces unless face is given): area, degrees, control point counts, closed/periodic, kinks (C0 knots), planarity, naked edges, " +
                       "and curvature statistics sampled on a u x v grid (default 15 x 15, max 60 x 60) inside the trimmed face: gaussian and mean curvature min/max/avg with the point and (u,v) of each extreme, " +
                       "max |principal curvature| with its minimum radius, and the count of flat samples. Also lists edges (index, length, naked/shared, endpoints) so you can address them in blend_surface / analyze_continuity. " +
                       "Curvature signs depend on face orientation; use absolute values for comparisons. Numbers only, no image.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"target\":{\"description\":\"handle or {target, face?}\"},\"u_samples\":{\"type\":\"integer\",\"minimum\":2,\"maximum\":60},\"v_samples\":{\"type\":\"integer\",\"minimum\":2,\"maximum\":60}," +
                    "\"edges\":{\"type\":\"boolean\",\"description\":\"list edges (default true, max 100)\"}",
                    "\"target\"");
            }
        }

        sealed class Ext
        {
            public double Min = double.MaxValue, Max = double.MinValue, Sum; public int N;
            public Point3d MinPt, MaxPt; public double[] MinUv, MaxUv; public int MinFace, MaxFace;
            public void Add(double v, Point3d pt, double u, double w, int face)
            {
                Sum += v; N++;
                if (v < Min) { Min = v; MinPt = pt; MinUv = new[] { u, w }; MinFace = face; }
                if (v > Max) { Max = v; MaxPt = pt; MaxUv = new[] { u, w }; MaxFace = face; }
            }
            public object ToObj()
            {
                if (N == 0) return null;
                return new
                {
                    min = Math.Round(Min, 9), max = Math.Round(Max, 9), avg = Math.Round(Sum / N, 9),
                    min_at = new { point = GeoUtil.Arr(MinPt), uv = new[] { FF.R4(MinUv[0]), FF.R4(MinUv[1]) }, face = MinFace },
                    max_at = new { point = GeoUtil.Arr(MaxPt), uv = new[] { FF.R4(MaxUv[0]), FF.R4(MaxUv[1]) }, face = MaxFace }
                };
            }
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            if (ctx.Doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active document");
            var p = new P(args, ctx, new Frame(Plane.WorldXY, "world_xy"));
            int nu = p.Int("u_samples", false, 15), nv = p.Int("v_samples", false, 15);
            bool listEdges = p.Bool("edges", true);
            if (nu < 2 || nu > 60 || nv < 2 || nv > 60) p.Err("'u_samples'/'v_samples' must be 2..60");
            using (var bag = new Bag())
            {
                var t = p.Tok("target");
                var rf = FF.ResolveRef(p, t, "target", false, false, bag);
                if (rf == null || p.Errors.Count > 0) return FF.ArgFail(p.Errors);
                if (rf.Brep == null) return ToolResult.Fail("INVALID_ARGS", "target must be a surface/brep/extrusion/SubD; for curves use measure", "analyze_surface is for surfaces.");
                var brep = rf.Brep;
                var faceTok = (t as JObject) != null ? ((JObject)t)["face"] : null;
                var faces = new List<int>();
                if (faceTok != null)
                {
                    int fi = (int)faceTok;
                    if (fi < 0 || fi >= brep.Faces.Count) return ToolResult.Fail("INVALID_ARGS", "face " + fi + " out of range 0.." + (brep.Faces.Count - 1));
                    faces.Add(fi);
                }
                else for (int i = 0; i < brep.Faces.Count; i++) faces.Add(i);
                try
                {
                    var gaus = new Ext(); var mean = new Ext(); var kmax = new Ext();
                    int flat = 0, total = 0, skipped = 0;
                    var faceInfos = new List<object>();
                    double area = 0;
                    foreach (int fi in faces)
                    {
                        var f = brep.Faces[fi];
                        var du = f.Domain(0); var dv = f.Domain(1);
                        for (int i = 0; i < nu; i++)
                            for (int j = 0; j < nv; j++)
                            {
                                double u = du.ParameterAt(i / (double)(nu - 1)), v = dv.ParameterAt(j / (double)(nv - 1));
                                try
                                {
                                    if (f.IsPointOnFace(u, v) == PointFaceRelation.Exterior) { skipped++; continue; }
                                    var sc = f.CurvatureAt(u, v);
                                    if (sc == null) { skipped++; continue; }
                                    double g = sc.Gaussian, m = sc.Mean, k0 = sc.Kappa(0);
                                    if (!FF.Finite(g) || !FF.Finite(m) || !FF.Finite(k0)) { skipped++; continue; }
                                    var pt = f.PointAt(u, v);
                                    gaus.Add(g, pt, u, v, fi); mean.Add(m, pt, u, v, fi); kmax.Add(Math.Abs(k0), pt, u, v, fi);
                                    total++;
                                    if (Math.Abs(k0) < 1e-9) flat++;
                                }
                                catch (Exception) { skipped++; }
                            }
                        try { using (var ft = f.DuplicateFace(false)) { var am = AreaMassProperties.Compute(ft); if (am != null) area += am.Area; } } catch (Exception) { }
                    }
                    var data = new Dictionary<string, object>();
                    data["target"] = rf.Handle;
                    data["faces"] = brep.Faces.Count;
                    data["analysed_faces"] = faces.Count;
                    data["area"] = FF.R(area);
                    data["solid"] = brep.IsSolid;
                    data["naked_edges"] = brep.Edges.Count(e => e.Valence == EdgeAdjacency.Naked);
                    data["bbox"] = GeoUtil.BBox(brep.GetBoundingBox(true));
                    data["grid"] = new { u = nu, v = nv, valid_samples = total, skipped = skipped, flat_samples = flat };
                    if (faces.Count == 1)
                        foreach (var kv in FF.FaceInfo(brep.Faces[faces[0]], ctx.Tol)) data[kv.Key] = kv.Value;
                    else
                        data["face_info"] = faces.Take(12).Select(i => { var d = FF.FaceInfo(brep.Faces[i], ctx.Tol); d["face"] = i; return (object)d; }).ToList();
                    data["gaussian"] = gaus.ToObj();
                    data["mean"] = mean.ToObj();
                    if (kmax.N > 0)
                        data["max_abs_curvature"] = new
                        {
                            value = Math.Round(kmax.Max, 9),
                            min_radius = kmax.Max > 1e-12 ? FF.R(1.0 / kmax.Max) : (double?)null,
                            at = GeoUtil.Arr(kmax.MaxPt)
                        };
                    if (listEdges)
                    {
                        var el = new List<object>();
                        var faceSet = new HashSet<int>(faces);
                        for (int i = 0; i < brep.Edges.Count && el.Count < 100; i++)
                        {
                            var e = brep.Edges[i];
                            if (faces.Count < brep.Faces.Count && !e.AdjacentFaces().Any(x => faceSet.Contains(x))) continue;
                            el.Add(new { i = i, length = FF.R(e.GetLength()), naked = e.Valence == EdgeAdjacency.Naked, faces = e.AdjacentFaces(), from = GeoUtil.Arr(e.PointAtStart), to = GeoUtil.Arr(e.PointAtEnd) });
                        }
                        data["edge_list"] = el;
                    }
                    return ToolResult.Ok(data);
                }
                catch (Exception ex) { return ToolResult.Fail("INTERNAL", ex.GetType().Name + ": " + ex.Message); }
            }
        }
    }
}

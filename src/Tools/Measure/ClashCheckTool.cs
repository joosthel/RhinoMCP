using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Measure
{
    public sealed class ClashCheckTool : ITool
    {
        public string Name { get { return "clash_check"; } }
        public string Description
        {
            get
            {
                return "Finds clashes between set A and set B (or within A when b is omitted). Broad phase: RTree over bboxes (inflated by clearance). " +
                       "Narrow phase: Intersection.BrepBrep (breps/extrusions), MeshMeshAccurate (meshes, breps vs meshes), CurveBrep/CurveCurve; solids fully inside another solid count as 'containment'. " +
                       "Optional clearance reports pairs closer than that distance (kind 'clearance'). compute_volume:true adds overlap volume for solid brep pairs via boolean intersection on duplicates (slow). " +
                       "Output: totals, clash matrix by layer pair, top_k worst pairs with handles and intersection curve length. " +
                       "Safety: max_pairs caps candidate pairs and budget_ms (default 20000) time-boxes the run; both return partial results with a warning.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"a\":{\"$ref\":\"#/$defs/target\"}," +
                    "\"b\":{\"$ref\":\"#/$defs/target\",\"description\":\"Omit to check within a\"}," +
                    "\"clearance\":{\"type\":\"number\",\"minimum\":0,\"default\":0,\"description\":\"Also report non-clashing pairs closer than this (document units)\"}," +
                    "\"compute_volume\":{\"type\":\"boolean\",\"default\":false}," +
                    "\"top_k\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200,\"default\":20}," +
                    "\"max_pairs\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000000,\"default\":5000,\"description\":\"Max candidate pairs after broad phase\"}," +
                    "\"budget_ms\":{\"type\":\"integer\",\"minimum\":100,\"maximum\":600000,\"default\":20000}," +
                    "\"force\":{\"type\":\"boolean\",\"description\":\"allow max_pairs above limits.clash_max_pairs\"}",
                    "\"a\"");
            }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        sealed class Hit
        {
            public Geo A, B;
            public string Kind;            // clash | containment | clearance
            public double Length;          // intersection curve length
            public double Distance;        // for clearance
            public double? Volume;
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var a = new ArgReader(args);
            var aObjs = a.Target("a", ctx);
            bool within = !a.Has("b");
            var bObjs = within ? null : a.Target("b", ctx);
            double clearance = a.Double("clearance", false, 0);
            bool wantVol = a.Bool("compute_volume", false);
            int topK = a.Int("top_k", false, 20, 1, 200);
            int maxPairs = a.Int("max_pairs", false, 5000, 1, 1000000);
            int budget = a.Int("budget_ms", false, 20000, 100, 600000);
            if (clearance < 0) a.Error("'clearance' must be >= 0");
            bool force = a.Bool("force", false);
            if (a.HasErrors) return a.ErrorResult();

            var rssBlock = Resources.CheckRss("clash_check");
            if (rssBlock != null) return rssBlock;
            var resGuard = Resources.Begin("clash_check");
            if (maxPairs > Resources.Limits.ClashMaxPairs && !force)
                return Resources.LimitFail("clash_max_pairs", maxPairs, Resources.Limits.ClashMaxPairs,
                    "max_pairs " + maxPairs + " exceeds clash_max_pairs (" + Resources.Limits.ClashMaxPairs + "). Nothing was computed.",
                    "Split the check by set or layer (a = one discipline, b = another) instead of raising max_pairs; results are partial-but-useful at the default.", true);

            var skipped = new Dictionary<string, int>();
            var A = MU.Wrap(aObjs, ctx, skipped, null);
            var B = within ? A : MU.Wrap(bObjs, ctx, skipped, null);
            if (A.Count == 0 || B.Count == 0)
                return ToolResult.Fail("NOT_FOUND", "Nothing checkable in " + (A.Count == 0 ? "a" : "b"), "Supported: points, curves, breps/extrusions/surfaces/SubD, meshes.", new { skipped = skipped });

            var sw = Stopwatch.StartNew();
            double tol = ctx.Tol;
            var warnings = new List<string>();

            // ----- broad phase -----
            var tree = new RTree();
            var bIdx = new Dictionary<Guid, int>();
            for (int j = 0; j < B.Count; j++) tree.Insert(B[j].Box, j);
            var pairs = new List<int[]>();
            var seen = new HashSet<string>();
            bool truncated = false;
            double inflate = clearance + tol;
            for (int i = 0; i < A.Count && !truncated; i++)
            {
                var box = A[i].Box; box.Inflate(inflate);
                var hits = new List<int>();
                tree.Search(box, (s, e) => hits.Add(e.Id));
                hits.Sort();
                foreach (int j in hits)
                {
                    var ga = A[i]; var gb = B[j];
                    if (ga.Obj.Id == gb.Obj.Id) continue;
                    string key = string.CompareOrdinal(ga.Handle, gb.Handle) < 0 ? ga.Handle + "|" + gb.Handle : gb.Handle + "|" + ga.Handle;
                    if (!seen.Add(key)) continue;
                    if (pairs.Count >= maxPairs) { truncated = true; break; }
                    pairs.Add(new[] { i, j });
                }
                if (sw.ElapsedMilliseconds > budget) break;
            }
            if (truncated) warnings.Add("max_pairs (" + maxPairs + ") reached in the broad phase; results are PARTIAL. Raise max_pairs or narrow a/b (e.g. by layer).");

            // ----- narrow phase -----
            var hitsOut = new List<Hit>();
            int tested = 0; bool timedOut = false; int errors = 0;
            foreach (var p in pairs)
            {
                if (sw.ElapsedMilliseconds > budget) { timedOut = true; break; }
                var ga = A[p[0]]; var gb = B[p[1]];
                tested++;
                try
                {
                    Hit h = Narrow(ga, gb, tol, clearance);
                    if (h == null) continue;
                    if (wantVol && h.Kind != "clearance" && ga.Kind == GKind.Brep && gb.Kind == GKind.Brep && ga.IsSolid && gb.IsSolid)
                    {
                        if (sw.ElapsedMilliseconds > budget) { timedOut = true; }
                        else h.Volume = OverlapVolume((Brep)ga.G, (Brep)gb.G, tol);
                    }
                    hitsOut.Add(h);
                }
                catch (Exception) { errors++; }
            }
            if (timedOut) warnings.Add("budget_ms (" + budget + ") exceeded after " + tested + " of " + pairs.Count + " candidate pairs; results are PARTIAL. Raise budget_ms or narrow the sets.");
            if (errors > 0) warnings.Add(errors + " pair(s) failed in the intersection kernel and were skipped.");
            if (wantVol && hitsOut.Any(h => h.Kind != "clearance" && !h.Volume.HasValue))
                warnings.Add("overlap volume is only computed for pairs of closed breps; others (or failed booleans) have no 'overlap_volume'.");

            // ----- report -----
            int clashes = hitsOut.Count(h => h.Kind != "clearance");
            int clear = hitsOut.Count(h => h.Kind == "clearance");
            var matrix = new Dictionary<string, int[]>();
            var matrixKeys = new Dictionary<string, string[]>();
            foreach (var h in hitsOut)
            {
                string la = h.A.Layer, lb = h.B.Layer;
                if (string.CompareOrdinal(la, lb) > 0) { var t = la; la = lb; lb = t; }
                string k = la + "\u0001" + lb;
                int[] c; if (!matrix.TryGetValue(k, out c)) { c = new int[2]; matrix[k] = c; matrixKeys[k] = new[] { la, lb }; }
                if (h.Kind == "clearance") c[1]++; else c[0]++;
            }
            var mx = new JArray();
            foreach (var kv in matrix.OrderByDescending(k => k.Value[0] + k.Value[1]).Take(100))
                mx.Add(new JObject { { "layer_a", matrixKeys[kv.Key][0] }, { "layer_b", matrixKeys[kv.Key][1] }, { "clashes", kv.Value[0] }, { "clearance", kv.Value[1] } });
            if (matrix.Count > 100) warnings.Add("Matrix truncated to the 100 busiest layer pairs of " + matrix.Count + ".");

            var ordered = hitsOut
                .OrderBy(h => h.Kind == "clearance" ? 1 : 0)
                .ThenByDescending(h => h.Volume ?? 0).ThenByDescending(h => h.Length).ThenBy(h => h.Distance)
                .Take(topK);
            var top = new JArray();
            foreach (var h in ordered)
            {
                var o = new JObject();
                o["a"] = h.A.Handle; o["b"] = h.B.Handle; o["layer_a"] = h.A.Layer; o["layer_b"] = h.B.Layer; o["kind"] = h.Kind;
                if (h.Kind == "clearance") o["distance"] = MU.R(h.Distance);
                else o["intersection_length"] = MU.R(h.Length);
                if (h.Volume.HasValue) o["overlap_volume"] = MU.R(h.Volume.Value);
                top.Add(o);
            }

            var data = new JObject();
            data["mode"] = within ? "within_a" : "a_vs_b";
            data["a_count"] = A.Count; data["b_count"] = B.Count;
            data["candidate_pairs"] = pairs.Count;
            data["pairs_tested"] = tested;
            data["clash_count"] = clashes;
            data["clearance_count"] = clear;
            data["partial"] = truncated || timedOut;
            data["matrix"] = mx;
            data["top"] = top;
            if (hitsOut.Count > topK) data["top_truncated"] = true;
            data["elapsed_ms"] = sw.ElapsedMilliseconds;
            if (skipped.Count > 0) data["skipped"] = JToken.FromObject(skipped);
            var res = ToolResult.Ok(data);
            res.Units = ctx.Units;
            foreach (var w in warnings) res.Warn(w);
            return resGuard.Finish(res);
        }

        static Hit Narrow(Geo ga, Geo gb, double tol, double clearance)
        {
            double len = 0; bool clash = false; string kind = "clash";
            if (ga.Kind == GKind.Brep && gb.Kind == GKind.Brep)
            {
                Curve[] cs; Point3d[] ps;
                if (Rhino.Geometry.Intersect.Intersection.BrepBrep((Brep)ga.G, (Brep)gb.G, tol, out cs, out ps))
                {
                    if (cs != null) foreach (var c in cs) { len += c.GetLength(); c.Dispose(); }
                    clash = (cs != null && cs.Length > 0) || (ps != null && ps.Length > 0);
                }
                if (!clash && (Contains(ga, gb, tol) || Contains(gb, ga, tol))) { clash = true; kind = "containment"; }
            }
            else if ((ga.Kind == GKind.Mesh || gb.Kind == GKind.Mesh) && ga.Kind != GKind.Curve && gb.Kind != GKind.Curve && ga.Kind != GKind.Point && gb.Kind != GKind.Point)
            {
                var ma = ga.AsMesh(); var mb = gb.AsMesh();
                if (ma != null && mb != null)
                {
                    var lines = Rhino.Geometry.Intersect.Intersection.MeshMeshAccurate(ma, mb, tol);
                    if (lines != null && lines.Length > 0) { clash = true; foreach (var l in lines) len += l.Length; }
                }
            }
            else if (ga.Kind == GKind.Curve && gb.Kind == GKind.Curve)
            {
                var ev = Rhino.Geometry.Intersect.Intersection.CurveCurve((Curve)ga.G, (Curve)gb.G, tol, tol);
                if (ev != null && ev.Count > 0)
                {
                    clash = true;
                    foreach (var e in ev) if (e.IsOverlap) len += e.OverlapA.Length * ((Curve)ga.G).GetLength() / Math.Max(1e-12, ((Curve)ga.G).Domain.Length);
                }
            }
            else if ((ga.Kind == GKind.Curve && gb.Kind == GKind.Brep) || (ga.Kind == GKind.Brep && gb.Kind == GKind.Curve))
            {
                var cg = ga.Kind == GKind.Curve ? ga : gb; var bg = ga.Kind == GKind.Curve ? gb : ga;
                Curve[] cs; Point3d[] ps;
                if (Rhino.Geometry.Intersect.Intersection.CurveBrep((Curve)cg.G, (Brep)bg.G, tol, out cs, out ps))
                {
                    if (cs != null) foreach (var c in cs) { len += c.GetLength(); c.Dispose(); }
                    clash = (cs != null && cs.Length > 0) || (ps != null && ps.Length > 0);
                }
            }
            else
            {
                var d0 = MU.Distance(ga, gb, tol);
                if (d0.Valid && d0.Distance <= tol) clash = true;
            }
            if (clash) return new Hit { A = ga, B = gb, Kind = kind, Length = len };
            if (clearance > 0)
            {
                var d = MU.Distance(ga, gb, tol);
                if (d.Valid && d.Distance < clearance) return new Hit { A = ga, B = gb, Kind = "clearance", Distance = d.Distance };
            }
            return null;
        }

        /// <summary>True if solid 'outer' fully contains 'inner' (which does not intersect its boundary).</summary>
        static bool Contains(Geo outer, Geo inner, double tol)
        {
            if (!outer.IsSolid || !outer.Box.Contains(inner.Box)) return false;
            var ob = (Brep)outer.G; var ib = (Brep)inner.G;
            Point3d p = ib.Vertices.Count > 0 ? ib.Vertices[0].Location : ib.GetBoundingBox(true).Center;
            return ob.IsPointInside(p, tol, false);
        }

        static double? OverlapVolume(Brep a, Brep b, double tol)
        {
            Brep da = null, db = null; Brep[] r = null;
            try
            {
                da = a.DuplicateBrep(); db = b.DuplicateBrep();
                r = Brep.CreateBooleanIntersection(da, db, tol);
                if (r == null) return null;
                double v = 0;
                foreach (var x in r)
                {
                    var vp = VolumeMassProperties.Compute(x);
                    if (vp != null) { v += Math.Abs(vp.Volume); vp.Dispose(); }
                }
                return r.Length == 0 ? 0.0 : (double?)v;
            }
            catch (Exception) { return null; }
            finally
            {
                try
                {
                    if (r != null) foreach (var x in r) if (x != null) x.Dispose();
                    if (da != null) da.Dispose();
                    if (db != null) db.Dispose();
                }
                catch (Exception) { }
            }
        }
    }
}

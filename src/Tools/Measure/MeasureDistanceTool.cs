using System;
using RhinoMCP.Tools.Shared;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino.Geometry;

namespace RhinoMCP.Tools.Measure
{
    public sealed class MeasureDistanceTool : ITool
    {
        public string Name { get { return "measure_distance"; } }
        public string Description
        {
            get
            {
                return "Minimum distance between two targets (a, b: handle|set|query|array) or between a point and a target. " +
                       "Returns distance, the closest point on each side and the handles involved. An RTree over bboxes prunes candidate pairs, then exact tests run: " +
                       "point-vs-anything and curve-vs-curve are exact; brep/mesh pairs are 0 if they intersect, otherwise found by seeded alternating closest-point projection " +
                       "(accurate to ~1e-6 for normal parts; very thin or strongly non-convex shapes may read slightly high). A solid fully inside another solid is NOT reported as 0. " +
                       "budget_ms (default 20000) caps the work; on overrun the best result so far is returned with a warning.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"a\":{\"$ref\":\"#/$defs/target\"}," +
                    "\"point\":{\"$ref\":\"#/$defs/point\",\"description\":\"Use instead of 'a' to measure from a point\"}," +
                    "\"b\":{\"$ref\":\"#/$defs/target\"}," +
                    "\"budget_ms\":{\"type\":\"integer\",\"minimum\":100,\"maximum\":600000,\"default\":20000}",
                    "\"b\"");
            }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var a = new ArgReader(args);
            bool hasPoint = a.Has("point"), hasA = a.Has("a");
            if (hasPoint == hasA) a.Error("Provide exactly one of 'a' (target) or 'point' ([x,y,z])");
            Point3d pt = hasPoint ? a.Point3d("point") : Point3d.Unset;
            var aObjs = hasA ? ObjectQuery.TargetOf(ctx, args, a, "a") : new List<Rhino.DocObjects.RhinoObject>();
            var bObjs = ObjectQuery.TargetOf(ctx, args, a, "b");
            int budget = a.Int("budget_ms", false, 20000, 100, 600000);
            if (a.HasErrors) return a.ErrorResult();

            var skipped = new Dictionary<string, int>();
            var A = new List<Geo>();
            if (hasPoint) A.Add(new Geo { Kind = GKind.Point, G = new Rhino.Geometry.Point(pt), Box = new BoundingBox(pt, pt), Handle = "point", Layer = "" });
            else A = MU.Wrap(aObjs, ctx, skipped, null);
            var B = MU.Wrap(bObjs, ctx, skipped, null);
            if (A.Count == 0 || B.Count == 0)
                return ToolResult.Fail("NOT_FOUND", "Nothing measurable in " + (A.Count == 0 ? "a" : "b") + " (empty or unsupported object types)",
                    "Supported: points, curves, breps/extrusions/surfaces/SubD, meshes.", new { skipped = skipped });

            var sw = Stopwatch.StartNew();
            double tol = ctx.Tol;
            var tree = new RTree();
            for (int i = 0; i < B.Count; i++) tree.Insert(B[i].Box, i);

            MU.DistResult best = null; Geo bestA = null, bestB = null;
            int exact = 0, candidates = 0; bool partial = false;
            Action<Geo, Geo> test = (ga, gb) =>
            {
                if (ga.Obj != null && gb.Obj != null && ga.Obj.Id == gb.Obj.Id) return;
                exact++;
                var d = MU.Distance(ga, gb, tol);
                if (d.Valid && (best == null || d.Distance < best.Distance)) { best = d; bestA = ga; bestB = gb; }
            };

            for (int ai = 0; ai < A.Count && !partial; ai++)
            {
                var ga = A[ai];
                if (best == null)
                {
                    // seed with the nearest few B boxes so the search radius is finite
                    foreach (var j in Enumerable.Range(0, B.Count).OrderBy(j => MU.BoxDistance(ga.Box, B[j].Box)).Take(8)) { candidates++; test(ga, B[j]); }
                }
                if (best == null) continue;
                double radius = best.Distance;
                var box = ga.Box; box.Inflate(radius + tol);
                var hits = new List<int>();
                tree.Search(box, (s, e) => hits.Add(e.Id));
                foreach (int j in hits)
                {
                    if (best.Distance <= tol * 0.01) break;
                    if (MU.BoxDistance(ga.Box, B[j].Box) >= best.Distance) continue;   // cannot beat current best
                    if (sw.ElapsedMilliseconds > budget) { partial = true; break; }
                    candidates++; test(ga, B[j]);
                }
                if (best.Distance <= tol * 0.01) break;
                if (sw.ElapsedMilliseconds > budget) partial = true;
            }
            if (best == null) return ToolResult.Fail("GEOMETRY_FAILED", "Could not compute a distance for these objects", "Check validity with geometry_validate.");

            var data = new JObject();
            data["distance"] = MU.R(best.Distance);
            data["point_a"] = JToken.FromObject(MU.P(best.PA));
            data["point_b"] = JToken.FromObject(MU.P(best.PB));
            data["a"] = bestA.Handle;
            data["b"] = bestB.Handle;
            data["method"] = best.Method;
            data["intersecting"] = best.Distance <= tol;
            data["pairs_evaluated"] = exact;
            data["a_count"] = A.Count; data["b_count"] = B.Count;
            data["elapsed_ms"] = sw.ElapsedMilliseconds;
            if (skipped.Count > 0) data["skipped"] = JToken.FromObject(skipped);
            var res = ToolResult.Ok(data);
            res.Units = ctx.Units;
            if (partial) res.Warn("budget_ms (" + budget + ") exceeded; this is the best distance found so far and may not be the minimum. Raise budget_ms or narrow the targets.");
            if (skipped.Count > 0) res.Warn("Some objects were skipped (unsupported types); see 'skipped'.");
            if (best.Method != null && best.Method.StartsWith("seeded")) res.Warn("Distance from alternating projection (not an exact solver); typical error < 1e-6 units on ordinary parts.");
            return res;
        }
    }
}

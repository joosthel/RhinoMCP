using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Geometry
{
    public sealed class GeometryBoolean : ITool
    {
        public string Name { get { return "geometry_boolean"; } }
        public ToolKind Kind { get { return ToolKind.Mutate; } }
        public string Description
        {
            get
            {
                return "Boolean on solids: op union (all of 'a', plus 'b' if given) | difference (a minus b) | intersection (a and b) | split (cut a with b). " +
                       "Pre-flight (valid, closed solid, bbox overlap), attempt at document tolerance, result validation (validity + volume sanity), then a retry ladder: " +
                       "tol x0.1, tol x10, one cutter at a time, cutter nudged by 2 x tol (breaks coplanar faces), repaired inputs. " +
                       "Inputs are deleted only on success (keep_inputs:true keeps them); on failure nothing changes and BOOLEAN_FAILED returns cause, tried[] and a suggestion. " +
                       "Result takes layer/attributes of the first 'a' object. Returns new handles, volumes (before/after), the ladder step used and the bbox.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"op\":{\"type\":\"string\",\"enum\":[\"union\",\"difference\",\"intersection\",\"split\"]}," +
                    "\"a\":{\"$ref\":\"#/$defs/target\"}," +
                    "\"b\":{\"$ref\":\"#/$defs/target\",\"description\":\"cutter(s); required for difference/intersection/split\"}," +
                    "\"keep_inputs\":{\"type\":\"boolean\"}," +
                    "\"include_guids\":{\"type\":\"boolean\"}",
                    "\"op\",\"a\"");
            }
        }

        sealed class Attempt { public string Step; public double Tol; public string Result; }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var p = new P(args, ctx, new Frame(Plane.WorldXY, "world_xy"));
            string op = p.Str("op", true);
            var aObjs = p.Objs("a");
            var bObjs = p.Objs("b", false);
            bool keep = p.Bool("keep_inputs");
            bool guids = p.Bool("include_guids");
            if (op != null && op != "union" && op != "difference" && op != "intersection" && op != "split") p.Err("'op' must be union, difference, intersection or split");
            if (op != null && op != "union" && bObjs.Count == 0 && !p.Errors.Any(e => e.StartsWith("'b'"))) p.Err("'b' is required for " + op);
            if (op == "union" && aObjs.Count + bObjs.Count < 2) p.Err("union needs at least two objects (give more in 'a' or add 'b')");
            if (p.Errors.Count > 0) return ToolResult.Fail("INVALID_ARGS", string.Join("; ", p.Errors), "Fix the arguments and call again.", new { errors = p.Errors });

            double tol = ctx.Tol;
            var doc = ctx.Doc;
            var aList = aObjs.Select(o => new { o, b = GeoUtil.AsBrep(o.Geometry) }).ToList();
            var bList = bObjs.Select(o => new { o, b = GeoUtil.AsBrep(o.Geometry) }).ToList();
            foreach (var x in aList.Concat(bList))
                if (x.b == null) return ToolResult.Fail("INVALID_ARGS", "object " + ctx.Handles.For(x.o.Id) + " is " + x.o.ObjectType + "; booleans need breps/solids/extrusions",
                    "Convert curves to solids first (geometry_create extrusion/loft...) or pick other objects.");
            var A = aList.Select(x => x.b).ToList();
            var B = bList.Select(x => x.b).ToList();
            if (op == "union") { A = A.Concat(B).ToList(); B = new List<Brep>(); }

            // ---- pre-flight ----
            var pre = new List<string>();
            var diag = new Dictionary<string, object>();
            bool needSolidA = op != "split";
            Action<string, List<Brep>, List<RhinoObject>, bool> check = (label, list, objs, needSolid) =>
            {
                for (int i = 0; i < list.Count; i++)
                {
                    string h = ctx.Handles.For(objs[i].Id);
                    string log;
                    if (!list[i].IsValidWithLog(out log)) pre.Add(label + " " + h + " is invalid: " + (GeoUtil.FirstLines(log, 2, 160) ?? "no log"));
                    if (needSolid && !list[i].IsSolid)
                    {
                        int naked = list[i].Edges.Count(e => e.Valence == EdgeAdjacency.Naked);
                        pre.Add(label + " " + h + " is not a closed solid (" + naked + " naked edges). Cap/join it first (geometry_edit cap|join) or use geometry_validate");
                    }
                    if (list[i].Edges.Any(e => e.Valence == EdgeAdjacency.NonManifold)) pre.Add(label + " " + h + " has non-manifold edges");
                }
            };
            var allA = op == "union" ? aObjs.Concat(bObjs).ToList() : aObjs;
            check("a", A, allA, needSolidA);
            if (B.Count > 0) check("b", B, bObjs, true);

            var bbA = GeoUtil.Union(A.Select(x => x.GetBoundingBox(true)));
            var bbB = B.Count > 0 ? GeoUtil.Union(B.Select(x => x.GetBoundingBox(true))) : bbA;
            double far = Math.Max(Math.Max(Math.Abs(bbA.Min.X), Math.Abs(bbA.Max.X)), Math.Max(Math.Abs(bbA.Min.Y), Math.Abs(bbA.Max.Y)));
            diag["max_distance_from_origin"] = GeoUtil.R(far);
            if (far > 1e5 * Math.Max(tol, 1e-9)) diag["far_from_origin"] = "geometry is very far from the origin relative to tolerance; move it near 0,0,0 for robust booleans";
            if (B.Count > 0)
            {
                var inter = BoundingBox.Intersection(bbA, bbB);
                // Slightly grown boxes to tolerate touching.
                var ga = bbA; ga.Inflate(tol); var gb = bbB; gb.Inflate(tol);
                if (!BoundingBox.Intersection(ga, gb).IsValid)
                    pre.Add("bounding boxes of a and b do not overlap; the result would be empty/unchanged");
            }
            if (B.Count > 0)
            {
                int coplanar = CountCoplanar(A, B, tol);
                if (coplanar > 0) diag["coplanar_face_pairs"] = coplanar;
            }
            if (pre.Count > 0) return Failure(ctx, op, "preflight", pre, new List<Attempt>(), diag,
                "Fix the listed inputs (use geometry_validate on them), then retry.");

            // ---- reference volumes ----
            double vA = A.Sum(x => GeoUtil.Volume(x));
            double vB = B.Sum(x => GeoUtil.Volume(x));
            double vMax = A.Count == 0 ? 0 : A.Max(x => GeoUtil.Volume(x));

            var tried = new List<Attempt>();
            Brep[] result = null;
            string stepUsed = null;

            Func<string, double, Func<double, Brep[]>, Brep[]> run = (name, t, fn) =>
            {
                var at = new Attempt { Step = name, Tol = t };
                tried.Add(at);
                Brep[] r;
                try { r = fn(t); }
                catch (Exception ex) { at.Result = "exception: " + ex.Message; return null; }
                string why = Validate(op, r, vA, vB, vMax);
                at.Result = why ?? "ok";
                return why == null ? r : null;
            };

            Func<List<Brep>, List<Brep>, double, Brep[]> basic = (a, b, t) =>
            {
                switch (op)
                {
                    case "union": return Brep.CreateBooleanUnion(a, t);
                    case "difference": return Brep.CreateBooleanDifference(a, b, t);
                    case "intersection": return Brep.CreateBooleanIntersection(a, b, t);
                    default: return Brep.CreateBooleanSplit(a, b, t);
                }
            };

            // 1-3: tolerance ladder
            double[] tols = new[] { tol, tol * 0.1, tol * 10 };
            string[] names = new[] { "document_tolerance", "tolerance_x0.1", "tolerance_x10" };
            for (int i = 0; i < tols.Length && result == null; i++)
            {
                result = run(names[i], tols[i], t => basic(A, B, t));
                if (result != null) stepUsed = names[i];
            }

            // 4: one cutter / one operand at a time
            if (result == null && (B.Count > 1 || (op == "union" && A.Count > 2)))
            {
                result = run("one_at_a_time", tol, t => Sequential(op, A, B, t));
                if (result != null) stepUsed = "one_at_a_time";
            }

            // 5: nudge cutter by 2 x tol (breaks coplanar faces)
            if (result == null)
            {
                var dirs = new[] { new Vector3d(1, 1, 1), new Vector3d(-1, -1, -1) };
                foreach (var d in dirs)
                {
                    if (result != null) break;
                    var v = d; v.Unitize(); v *= 2 * tol;
                    string nm = "nudge_cutter_" + (d.X > 0 ? "+" : "-");
                    result = run(nm, tol, t =>
                    {
                        if (op == "union")
                        {
                            var moved = A.Select((x, k) => { var c = x.DuplicateBrep(); if (k > 0) c.Translate(v); return c; }).ToList();
                            return Brep.CreateBooleanUnion(moved, t);
                        }
                        var cb = B.Select(x => { var c = x.DuplicateBrep(); c.Translate(v); return c; }).ToList();
                        return basic(A, cb, t);
                    });
                    if (result != null) stepUsed = nm;
                }
            }

            // 6: repaired copies of the inputs
            if (result == null)
            {
                Func<List<Brep>, List<Brep>> fix = list => list.Select(x =>
                {
                    var c = x.DuplicateBrep();
                    c.Repair(tol); c.JoinNakedEdges(tol * 2);
                    c.MergeCoplanarFaces(tol);
                    return c;
                }).ToList();
                result = run("repair_inputs", tol, t => basic(fix(A), fix(B), t));
                if (result != null) stepUsed = "repair_inputs";
            }

            if (result == null)
            {
                var causes = tried.Select(t => t.Step + " (tol " + GeoUtil.R(t.Tol) + "): " + t.Result).ToList();
                string cause = DominantCause(tried);
                if (diag.ContainsKey("coplanar_face_pairs")) cause += "; inputs share coplanar faces, a classic boolean failure";
                if (diag.ContainsKey("far_from_origin")) cause += "; geometry is far from the origin";
                return Failure(ctx, op, cause, causes, tried, diag,
                    "Try: (1) geometry_validate on a and b, (2) offset the cutter slightly so faces are not coplanar (geometry_transform move by a few x tolerance), " +
                    "(3) make cutters overlap clearly past the target's faces, (4) split the job into one cutter at a time, (5) rebuild/simplify the cutter shape.");
            }

            // ---- success: add results, delete inputs ----
            var attrSrc = aObjs[0];
            var ids = new List<Guid>();
            var bbs = new List<BoundingBox>();
            foreach (var r in result)
            {
                var attr = GeoUtil.CopyAttributes(attrSrc);
                var id = doc.Objects.AddBrep(r, attr);
                if (id == Guid.Empty) continue;
                ids.Add(id); bbs.Add(r.GetBoundingBox(true));
            }
            if (ids.Count == 0) return Failure(ctx, op, "result could not be added to the document", new List<string>(), tried, diag, "Retry with different inputs.");
            var deleted = new List<string>();
            if (!keep)
            {
                foreach (var o in allA.Concat(op == "union" ? new List<RhinoObject>() : bObjs).GroupBy(x => x.Id).Select(g => g.First()))
                {
                    deleted.Add(ctx.Handles.For(o.Id));
                    doc.Objects.Delete(o.Id, true);
                }
            }
            double vR = result.Sum(x => GeoUtil.Volume(x));
            var data = new Dictionary<string, object>();
            data["op"] = op;
            data["created"] = ids.Select(x => ctx.Handles.For(x)).ToList();
            if (guids) data["guids"] = ids.Select(x => x.ToString()).ToList();
            data["deleted"] = deleted;
            data["bbox"] = GeoUtil.BBox(GeoUtil.Union(bbs));
            data["volume_a"] = GeoUtil.R(vA);
            if (B.Count > 0) data["volume_b"] = GeoUtil.R(vB);
            data["volume_result"] = GeoUtil.R(vR);
            data["step_used"] = stepUsed;
            data["solid"] = result.All(x => x.IsSolid);
            var tr = ToolResult.Ok(data);
            if (stepUsed != "document_tolerance")
                tr.Warn("Succeeded only at ladder step '" + stepUsed + "' (" + tried.Count + " attempts); verify the result with geometry_validate");
            return tr;
        }

        static string DominantCause(List<Attempt> tried)
        {
            if (tried.All(t => t.Result.StartsWith("no result"))) return "Rhino returned no result at any tolerance (typically coplanar/tangent faces or inputs that barely touch)";
            var last = tried.Last(t => true).Result;
            return "all " + tried.Count + " attempts rejected; last: " + last;
        }

        static Brep[] Sequential(string op, List<Brep> A, List<Brep> B, double t)
        {
            if (op == "union")
            {
                var acc = new List<Brep> { A[0] };
                for (int i = 1; i < A.Count; i++)
                {
                    var r = Brep.CreateBooleanUnion(new[] { acc[0], A[i] }, t);
                    if (r == null || r.Length != 1) return null;
                    acc = new List<Brep> { r[0] };
                }
                return acc.ToArray();
            }
            var cur = A;
            foreach (var cutter in B)
            {
                Brep[] r;
                if (op == "difference") r = Brep.CreateBooleanDifference(cur, new List<Brep> { cutter }, t);
                else if (op == "intersection") r = Brep.CreateBooleanIntersection(cur, new List<Brep> { cutter }, t);
                else r = Brep.CreateBooleanSplit(cur, new List<Brep> { cutter }, t);
                if (r == null || r.Length == 0) return null;
                cur = r.ToList();
            }
            return cur.ToArray();
        }

        static string Validate(string op, Brep[] r, double vA, double vB, double vMax)
        {
            if (r == null || r.Length == 0) return "no result";
            foreach (var b in r) { if (b == null) return "null brep in result"; if (!b.IsValid) return "invalid result brep"; }
            double v = r.Sum(x => GeoUtil.Volume(x));
            double eps = 1e-6 * Math.Max(1.0, vA + vB) + 1e-9;
            switch (op)
            {
                case "union":
                    {
                        if (!r.All(x => x.IsSolid)) return "result is not a closed solid";
                        if (v > vA + eps) return "volume " + GeoUtil.R(v) + " exceeds the sum of operands " + GeoUtil.R(vA);
                        if (v < vMax - eps) return "volume " + GeoUtil.R(v) + " is smaller than the largest operand " + GeoUtil.R(vMax);
                        return null;
                    }
                case "difference":
                    if (!r.All(x => x.IsSolid)) return "result is not a closed solid";
                    if (v > vA + eps) return "volume " + GeoUtil.R(v) + " is larger than the original " + GeoUtil.R(vA);
                    if (v < vA - vB - eps) return "volume " + GeoUtil.R(v) + " is smaller than a-b allows (" + GeoUtil.R(vA - vB) + ")";
                    return null;
                case "intersection":
                    if (!r.All(x => x.IsSolid)) return "result is not a closed solid";
                    if (v <= eps) return "empty intersection (zero volume)";
                    if (v > Math.Min(vA, vB) + eps) return "volume " + GeoUtil.R(v) + " exceeds the smaller input " + GeoUtil.R(Math.Min(vA, vB));
                    return null;
                default:
                    {
                        // split: pieces together keep the volume (when a is solid); a surface split has no volume.
                        if (vA > 0 && r.All(x => x.IsSolid) && Math.Abs(v - vA) > 1e-4 * vA + eps) return "pieces' volume " + GeoUtil.R(v) + " differs from the original " + GeoUtil.R(vA);
                        return null;
                    }
            }
        }

        /// <summary>Counts planar face pairs (one from each side) that lie in the same plane within tolerance.</summary>
        static int CountCoplanar(List<Brep> A, List<Brep> B, double tol)
        {
            int n = 0;
            Func<Brep, List<Plane>> planes = b =>
            {
                var l = new List<Plane>();
                foreach (var f in b.Faces) { Plane pl; if (f.TryGetPlane(out pl, tol)) { if (f.OrientationIsReversed) pl.Flip(); l.Add(pl); } }
                return l;
            };
            var pa = A.SelectMany(planes).Take(400).ToList();
            var pb = B.SelectMany(planes).Take(400).ToList();
            foreach (var x in pa)
                foreach (var y in pb)
                {
                    if (Math.Abs(Math.Abs(x.ZAxis * y.ZAxis) - 1.0) > 1e-9) continue;
                    if (Math.Abs(x.DistanceTo(y.Origin)) <= tol * 2) n++;
                }
            return n;
        }

        static ToolResult Failure(ToolContext ctx, string op, string cause, List<string> detail, List<Attempt> tried, Dictionary<string, object> diag, string suggestion)
        {
            var det = new Dictionary<string, object>();
            det["op"] = op;
            det["cause"] = cause;
            det["tried"] = tried.Select(t => new { step = t.Step, tol = GeoUtil.R(t.Tol), result = t.Result }).ToList();
            det["notes"] = detail;
            det["diagnosis"] = diag;
            det["inputs_changed"] = false;
            return ToolResult.Fail("BOOLEAN_FAILED", "Boolean " + op + " failed: " + cause, suggestion, det);
        }
    }
}

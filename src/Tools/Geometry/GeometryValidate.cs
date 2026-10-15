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
    public sealed class GeometryValidate : ITool
    {
        public string Name { get { return "geometry_validate"; } }
        public ToolKind Kind { get { return ToolKind.Read; } }
        public string Description
        {
            get
            {
                return "Health check of objects. Summary counts first, then per-object rows: valid + short log reason, closed/solid, naked edge count, non-manifold edges, " +
                       "tiny edges/segments (< 2 x tolerance), faces/edges, and for curves degree/control-point count/length/domain/planar. " +
                       "Use before booleans or when an operation fails. only_problems:true lists just objects with issues. limit (default 50) + cursor page the rows.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"target\":{\"$ref\":\"#/$defs/target\"}," +
                    "\"only_problems\":{\"type\":\"boolean\"}," +
                    "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500}," +
                    "\"cursor\":{\"type\":\"string\"}",
                    "\"target\"");
            }
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var p = new P(args, ctx, new Frame(Plane.WorldXY, "world_xy"));
            var objs = p.Objs("target");
            bool onlyProblems = p.Bool("only_problems");
            int limit = p.Int("limit", false, 50);
            string cursor = p.Str("cursor");
            int start = 0;
            if (cursor != null && !int.TryParse(cursor, out start)) p.Err("'cursor' is invalid; use next_cursor from a previous call");
            if (limit < 1 || limit > 500) p.Err("'limit' must be 1..500");
            if (p.Errors.Count > 0) return ToolResult.Fail("INVALID_ARGS", string.Join("; ", p.Errors), "Fix the arguments and call again.", new { errors = p.Errors });
            if (objs.Count == 0) return ToolResult.Fail("NOT_FOUND", "target resolved to no objects", "Check the handles.");

            double tiny = 2 * ctx.Tol;
            var rows = new List<Dictionary<string, object>>();
            int invalid = 0, naked = 0, nonMan = 0, tinyN = 0, open = 0, problems = 0;
            foreach (var o in objs)
            {
                var row = Check(o, ctx, tiny);
                bool bad = !(bool)row["valid"];
                if (bad) invalid++;
                if (row.ContainsKey("naked_edges") && (int)row["naked_edges"] > 0) naked++;
                if (row.ContainsKey("non_manifold_edges") && (int)row["non_manifold_edges"] > 0) nonMan++;
                if (row.ContainsKey("tiny_edges") && (int)row["tiny_edges"] > 0) tinyN++;
                if (row.ContainsKey("solid") && !(bool)row["solid"]) open++;
                bool prob = bad || (row.ContainsKey("issues") && ((List<string>)row["issues"]).Count > 0);
                if (prob) problems++;
                row["_prob"] = prob;
                rows.Add(row);
            }
            var shown = (onlyProblems ? rows.Where(r => (bool)r["_prob"]) : rows).ToList();
            foreach (var r in rows) r.Remove("_prob");
            var page = shown.Skip(start).Take(limit).ToList();
            var summary = new
            {
                checked_ = rows.Count,
                valid = rows.Count - invalid,
                invalid = invalid,
                with_problems = problems,
                with_naked_edges = naked,
                with_non_manifold_edges = nonMan,
                with_tiny_edges = tinyN,
                open_breps_not_solid = open,
                tiny_threshold = GeoUtil.R(tiny)
            };
            var sj = JObject.FromObject(summary);
            sj["checked"] = sj["checked_"]; sj.Remove("checked_");
            var res = ToolResult.Ok(new { summary = sj, total = shown.Count, returned = page.Count, objects = page });
            if (start + page.Count < shown.Count) res.NextCursor = (start + page.Count).ToString();
            return res;
        }

        static Dictionary<string, object> Check(RhinoObject o, ToolContext ctx, double tiny)
        {
            var d = new Dictionary<string, object>();
            var issues = new List<string>();
            d["handle"] = ctx.Handles.For(o.Id);
            var g = o.Geometry;
            d["type"] = o.ObjectType.ToString();
            string log = null;
            bool valid = g.IsValidWithLog(out log);
            d["valid"] = valid;
            if (!valid) { d["log"] = GeoUtil.FirstLines(log, 2, 220) ?? "invalid"; issues.Add("invalid"); }
            try
            {
                var ext = g as Extrusion;
                var brep = g as Brep ?? (ext != null ? ext.ToBrep(true) : null);
                var curve = g as Curve;
                var mesh = g as Mesh;
                if (brep != null)
                {
                    int naked = 0, nm = 0, tn = 0;
                    foreach (var e in brep.Edges)
                    {
                        if (e.Valence == EdgeAdjacency.Naked) naked++;
                        else if (e.Valence == EdgeAdjacency.NonManifold) nm++;
                        if (e.GetLength() < tiny) tn++;
                    }
                    d["faces"] = brep.Faces.Count; d["edges"] = brep.Edges.Count;
                    d["naked_edges"] = naked; d["non_manifold_edges"] = nm; d["tiny_edges"] = tn;
                    bool solid = brep.IsSolid;
                    d["solid"] = solid; d["closed"] = solid;
                    if (solid) d["volume"] = GeoUtil.R(GeoUtil.Volume(brep));
                    var am = AreaMassProperties.Compute(brep);
                    if (am != null) d["area"] = GeoUtil.R(am.Area);
                    if (ext != null) d["extrusion"] = true;
                    if (naked > 0) issues.Add(naked + " naked edges");
                    if (nm > 0) issues.Add(nm + " non-manifold edges");
                    if (tn > 0) issues.Add(tn + " tiny edges (< " + GeoUtil.R(tiny) + ")");
                    if (!brep.IsManifold) issues.Add("non-manifold");
                    if (solid && brep.SolidOrientation == BrepSolidOrientation.Inward) issues.Add("solid is inside-out (normals point inward); use Flip");
                    if (!solid && naked == 0 && brep.Faces.Count > 0) issues.Add("not closed");
                }
                else if (curve != null)
                {
                    d["closed"] = curve.IsClosed;
                    d["length"] = GeoUtil.R(curve.GetLength());
                    d["domain"] = new[] { GeoUtil.R(curve.Domain.T0), GeoUtil.R(curve.Domain.T1) };
                    d["degree"] = curve.Degree;
                    d["planar"] = curve.IsPlanar(ctx.Tol);
                    var nc = curve.ToNurbsCurve();
                    if (nc != null) d["control_points"] = nc.Points.Count;
                    var pc = curve as PolyCurve;
                    int tn = 0;
                    if (pc != null) { d["segments"] = pc.SegmentCount; for (int i = 0; i < pc.SegmentCount; i++) if (pc.SegmentCurve(i).GetLength() < tiny) tn++; }
                    var pl = curve as PolylineCurve;
                    if (pl != null)
                    {
                        d["segments"] = pl.PointCount - 1;
                        for (int i = 0; i < pl.PointCount - 1; i++) if (pl.Point(i).DistanceTo(pl.Point(i + 1)) < tiny) tn++;
                    }
                    d["tiny_edges"] = tn;
                    if (tn > 0) issues.Add(tn + " tiny segments (< " + GeoUtil.R(tiny) + ")");
                    if (curve.GetLength() < tiny) issues.Add("curve is shorter than 2 x tolerance");
                }
                else if (mesh != null)
                {
                    d["vertices"] = mesh.Vertices.Count; d["faces"] = mesh.Faces.Count;
                    d["closed"] = mesh.IsClosed;
                    var ne = mesh.GetNakedEdges();
                    int naked = ne == null ? 0 : ne.Sum(x => x.Count - 1);
                    d["naked_edges"] = naked;
                    if (naked > 0) issues.Add(naked + " naked edge segments");
                    if (mesh.IsClosed && !mesh.IsManifold(true, out bool oriented, out bool hasBoundary)) issues.Add("mesh is non-manifold");
                }
                else if (g is Surface)
                {
                    var s = (Surface)g;
                    d["degree_u"] = s.Degree(0); d["degree_v"] = s.Degree(1);
                }
            }
            catch (Exception ex) { issues.Add("check error: " + ex.Message); }
            d["issues"] = issues;
            d["layer"] = ctx.Doc.Layers[o.Attributes.LayerIndex].FullPath;
            return d;
        }
    }
}

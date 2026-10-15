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
    public sealed class GeometryEdit : ITool
    {
        public string Name { get { return "geometry_edit"; } }
        public ToolKind Kind { get { return ToolKind.Mutate; } }
        public string Description
        {
            get
            {
                return "Edit existing geometry. op: join (curves -> curves, surfaces -> polysurface) | explode (polysurface -> faces, polyline/polycurve -> segments) | " +
                       "offset_curve{distance, plane?, corner?=sharp|round|smooth|chamfer, side?=inside|outside (closed curves), both_sides?} | " +
                       "fillet_edges{radius, edges?='all'|[edge indices], budget_ms?} (TIMEOUT when the retry ladder exceeds the budget) | fillet_corners{radius} (polyline/polycurve kinks -> arcs, reports arc radii) | " +
                       " shell{thickness, remove_faces:[face indices]} (thickness>0 = inward) | cap (close planar holes) | " +
                       "split{cutter, keep?} | trim{cutter | plane, flip?, keep?  or, for curves, domain[t0,t1]} (keep = all|inside|outside|largest|smallest|containing_point|nearest_point, with point; the other pieces are discarded; inside/outside need a closed cutter: solid or closed planar curve) | " +
                       "project_curve{onto, direction?=[0,0,-1], join?=true} | pull_curve{onto} (closest-point pull; sources are kept unless keep_inputs:false; reports max_deviation from the surface) | " +
                       "engrave|emboss{text, text_height, font?, bold?, italic? | curves(closed, nested = counters), depth, face?|at?|plane?, x_axis?, rotation_deg?, center?=true} (solid target; text is placed on the face (default: the most upward planar face) with its up direction toward world Y or Z, cut/raised along the outward normal, counters of O/A/R stay; reports removed/added volume) | rebuild_curve{points, degree?=3} | offset_surface{distance, solid?}. " +
                       "In-place ops (fillet_edges, shell, cap, rebuild_curve) keep the handle; others delete the source unless keep_inputs:true. " +
                       "Results are validated (IsValid, solid stays solid); failures return GEOMETRY_FAILED with a reason and a suggestion. Rhino 7 has no chamfer-edges API; use fillet_edges.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"target\":{\"$ref\":\"#/$defs/target\"}," +
                    "\"op\":{\"type\":\"string\",\"enum\":[\"join\",\"explode\",\"offset_curve\",\"fillet_edges\",\"shell\",\"cap\",\"split\",\"trim\",\"rebuild_curve\",\"offset_surface\",\"fillet_corners\",\"engrave\",\"emboss\",\"project_curve\",\"pull_curve\"]}," +
                    "\"keep_inputs\":{\"type\":\"boolean\"}," +
                    "\"distance\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"thickness\":{\"type\":\"number\"}," +
                    "\"plane\":{\"$ref\":\"#/$defs/plane\"}," +
                    "\"corner\":{\"type\":\"string\",\"enum\":[\"sharp\",\"round\",\"smooth\",\"chamfer\"]}," +
                    "\"side\":{\"type\":\"string\",\"enum\":[\"inside\",\"outside\"]},\"both_sides\":{\"type\":\"boolean\"}," +
                    "\"edges\":{\"oneOf\":[{\"type\":\"string\",\"enum\":[\"all\"]},{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}]}," +
                    "\"remove_faces\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}," +
                    "\"cutter\":{\"$ref\":\"#/$defs/target\"}," +
                    "\"flip\":{\"type\":\"boolean\"}," +
                    "\"domain\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2}," +
                    "\"points\":{\"type\":\"integer\",\"description\":\"rebuild_curve: control point count\"},\"degree\":{\"type\":\"integer\"}," +
                    "\"solid\":{\"type\":\"boolean\"}," +
                    "\"max_cp\":{\"type\":\"integer\",\"minimum\":16,\"description\":\"offset_surface/shell: faces of the result above this control-point count are rebuilt when possible (single untrimmed face); default limits.max_surface_control_points\"}," +
                    "\"keep\":{\"type\":\"string\",\"enum\":[\"all\",\"inside\",\"outside\",\"largest\",\"smallest\",\"containing_point\",\"nearest_point\"]}," +
                    "\"point\":{\"$ref\":\"#/$defs/point\"},\"at\":{\"$ref\":\"#/$defs/point\"},\"direction\":{\"$ref\":\"#/$defs/point\"},\"x_axis\":{\"$ref\":\"#/$defs/point\"}," +
                    "\"onto\":{\"$ref\":\"#/$defs/target\"},\"curves\":{\"$ref\":\"#/$defs/target\"}," +
                    "\"text\":{\"type\":\"string\"},\"text_height\":{\"type\":\"number\"},\"font\":{\"type\":\"string\"},\"bold\":{\"type\":\"boolean\"},\"italic\":{\"type\":\"boolean\"},\"center\":{\"type\":\"boolean\"}," +
                    "\"depth\":{\"type\":\"number\",\"description\":\"engrave: cut depth; emboss: raised height\"},\"face\":{\"type\":\"integer\"},\"rotation_deg\":{\"type\":\"number\"},\"join\":{\"type\":\"boolean\"}," +
                    "\"budget_ms\":{\"type\":\"integer\",\"minimum\":1000,\"maximum\":600000,\"description\":\"fillet_edges: time budget checked between retry steps (default limits.fillet_budget_ms = 30000); a single kernel call cannot be interrupted\"}," +
                    "\"force\":{\"type\":\"boolean\"}," +
                    "\"include_guids\":{\"type\":\"boolean\"}",
                    "\"target\",\"op\"");
            }
        }

        // Result accumulator shared by the ops.
        sealed class Out
        {
            public List<string> Created = new List<string>(), Modified = new List<string>(), Deleted = new List<string>();
            public List<BoundingBox> Boxes = new List<BoundingBox>();
            public List<object> Items = new List<object>();
            public List<string> Warnings = new List<string>();
            public List<Guid> NewIds = new List<Guid>();
            public int Failed;
            public ToolResult Resource;
        }

        ToolContext _ctx; P _p; Out _o; bool _keep;
        object _timeout; string _timeoutMsg;

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            _ctx = ctx; _timeout = null; _timeoutMsg = null;
            var p = new P(args, ctx, new Frame(Plane.WorldXY, "world_xy"));
            _p = p;
            var objs = p.Objs("target");
            string op = p.Str("op", true);
            _keep = p.Bool("keep_inputs");
            bool guids = p.Bool("include_guids");
            if (p.Errors.Count > 0) return Bad(p);
            if (objs.Count == 0) return ToolResult.Fail("NOT_FOUND", "target resolved to no objects", "Check the handles.");
            _o = new Out();
            double tol = ctx.Tol;
            var doc = ctx.Doc;

            switch (op)
            {
                case "join": Join(objs); break;
                case "explode": foreach (var o in objs) Explode(o); break;
                case "offset_curve": foreach (var o in objs) OffsetCurve(o); break;
                case "fillet_edges": foreach (var o in objs) Fillet(o); break;
                case "shell": foreach (var o in objs) Shell(o); break;
                case "cap": foreach (var o in objs) CapHoles(o); break;
                case "split": foreach (var o in objs) Split(o); break;
                case "trim": foreach (var o in objs) Trim(o); break;
                case "rebuild_curve": foreach (var o in objs) Rebuild(o); break;
                case "offset_surface": foreach (var o in objs) OffsetSurface(o); break;
                case "fillet_corners": foreach (var o in objs) FilletCorners(o); break;
                case "project_curve": foreach (var o in objs) ProjectOrPull(o, false); break;
                case "pull_curve": foreach (var o in objs) ProjectOrPull(o, true); break;
                case "engrave": foreach (var o in objs) Engrave(o, false); break;
                case "emboss": foreach (var o in objs) Engrave(o, true); break;
                default:
                    p.Err("unknown op '" + op + "'. Valid: join, explode, offset_curve, fillet_edges, fillet_corners, shell, cap, split, trim, rebuild_curve, offset_surface, project_curve, pull_curve, engrave, emboss");
                    break;
            }
            if (p.Errors.Count > 0) return Bad(p);

            int done = _o.Created.Count + _o.Modified.Count;
            if (done == 0 && _timeout != null)
                return ToolResult.Fail("TIMEOUT", _timeoutMsg, "Raise budget_ms, use a smaller radius or fewer edges, or fillet in several calls. Nothing was changed.", _timeout);
            if (_o.Resource != null && done == 0) return _o.Resource;
            if (done == 0)
            {
                var first = _o.Items.FirstOrDefault();
                string msg = _o.Warnings.Count > 0 ? _o.Warnings[0] : "operation produced no result";
                return ToolResult.Fail("GEOMETRY_FAILED", op + " failed: " + msg, Suggest(op), new { items = _o.Items });
            }
            var data = new Dictionary<string, object>();
            data["op"] = op;
            data["created"] = _o.Created;
            data["modified"] = _o.Modified;
            data["deleted"] = _o.Deleted;
            data["failed"] = _o.Failed;
            data["bbox"] = GeoUtil.BBox(GeoUtil.Union(_o.Boxes));
            data["items"] = _o.Items;
            if (guids) data["guids"] = _o.NewIds.Select(x => x.ToString()).ToList();
            var tr = ToolResult.Ok(data);
            foreach (var w in _o.Warnings) tr.Warn(w);
            return tr;
        }

        static string Suggest(string op)
        {
            switch (op)
            {
                case "fillet_corners": return "Needs a curve with kinks (polyline/polycurve); the radius must be smaller than half of the shortest adjacent segment.";
                case "engrave": case "emboss": return "Target must be a closed solid. Check that the text sits on the face (face index / at point), use a smaller depth, and run geometry_validate on the target.";
                case "project_curve": case "pull_curve": return "Check that 'onto' is a surface/polysurface under the curve along 'direction' (project_curve) or near it (pull_curve).";
                case "fillet_edges": return "Use a smaller radius (must be well below the shortest adjacent edge/face width), select fewer edges, or run geometry_validate on the target.";
                case "shell": return "Use a smaller thickness, pick other faces to remove, or check the solid with geometry_validate.";
                case "cap": return "Only planar holes bounded by naked edges can be capped. Check naked edges with geometry_validate.";
                case "join": return "Objects must touch within tolerance. Check gaps with geometry_validate or increase overlap.";
                case "offset_curve": return "Offset needs a planar curve (give 'plane'), and a distance smaller than local radii for inward offsets.";
                default: return "See details.items for the reason per object.";
            }
        }

        static ToolResult Bad(P p)
        {
            return ToolResult.Fail("INVALID_ARGS", string.Join("; ", p.Errors), "Fix the arguments and call again. See the tool description for each op's parameters.", new { errors = p.Errors });
        }

        // ---------- helpers ----------
        string H(RhinoObject o) { return _ctx.Handles.For(o.Id); }

        bool Delete(RhinoObject src)
        {
            if (_keep) return false;
            string h = H(src);
            if (_ctx.Doc.Objects.Delete(src.Id, true)) { _o.Deleted.Add(h); return true; }
            return false;
        }

        Guid AddLike(RhinoObject src, GeometryBase g)
        {
            var id = GeoUtil.AddGeometry(_ctx.Doc, g, GeoUtil.CopyAttributes(src));
            if (id != Guid.Empty)
            {
                _o.Created.Add(_ctx.Handles.For(id)); _o.NewIds.Add(id);
                _o.Boxes.Add(g.GetBoundingBox(true));
            }
            return id;
        }

        /// <summary>Replaces the geometry of src with g keeping the handle when possible; otherwise adds a new object and deletes src.</summary>
        string ReplaceKeep(RhinoObject src, GeometryBase g)
        {
            var doc = _ctx.Doc;
            Guid id = src.Id;
            bool ok = false;
            try { ok = doc.Objects.Replace(src.Id, g, false); } catch (Exception) { ok = false; }
            if (!ok)
            {
                id = GeoUtil.AddGeometry(doc, g, GeoUtil.CopyAttributes(src));
                if (id == Guid.Empty) return null;
                doc.Objects.Delete(src.Id, true);
                _o.Deleted.Add(H(src));
                _o.Created.Add(_ctx.Handles.For(id)); _o.NewIds.Add(id);
            }
            else _o.Modified.Add(H(src));
            _o.Boxes.Add(g.GetBoundingBox(true));
            return _ctx.Handles.For(id);
        }

        void Fail(RhinoObject o, string reason)
        {
            _o.Failed++;
            _o.Warnings.Add(H(o) + ": " + reason);
            _o.Items.Add(new { source = H(o), ok = false, error = reason });
        }

        // ---------- ops ----------
        void Join(List<RhinoObject> objs)
        {
            double tol = _ctx.Tol;
            if (objs.Count < 2) { _p.Err("join needs at least two objects"); return; }
            var curves = objs.Where(o => o.Geometry is Curve).ToList();
            var breps = objs.Where(o => o.Geometry is Brep || o.Geometry is Extrusion || o.Geometry is Surface).ToList();
            if (curves.Count > 0 && breps.Count > 0) { _p.Err("cannot join curves together with surfaces; split the target"); return; }
            if (curves.Count + breps.Count != objs.Count) { _p.Err("join supports curves and surfaces/polysurfaces only"); return; }
            var made = new List<GeometryBase>();
            if (curves.Count > 0)
            {
                var j = Curve.JoinCurves(curves.Select(o => ((Curve)o.Geometry).DuplicateCurve()).ToList(), tol * 2);
                if (j != null) made.AddRange(j);
            }
            else
            {
                var j = Brep.JoinBreps(breps.Select(o => GeoUtil.AsBrep(o.Geometry)).ToList(), tol);
                if (j != null) made.AddRange(j);
            }
            int before = objs.Count;
            if (made.Count == 0 || made.Count >= before)
            {
                _o.Failed++;
                _o.Warnings.Add("nothing joined: the objects do not touch within tolerance " + tol + " (" + made.Count + " pieces from " + before + " inputs)");
                _o.Items.Add(new { ok = false, error = "no join", inputs = before, pieces = made.Count });
                return;
            }
            foreach (var g in made)
            {
                Guid id = AddLike(objs[0], g);
                var gm = GeoUtil.Measure(g);
                _o.Items.Add(new { handle = _ctx.Handles.For(id), type = GeoUtil.TypeName(g), closed = gm.ContainsKey("closed") ? gm["closed"] : gm.ContainsKey("solid") ? gm["solid"] : null });
            }
            foreach (var o in objs) Delete(o);
        }

        void Explode(RhinoObject o)
        {
            var pieces = new List<GeometryBase>();
            var c = o.Geometry as Curve;
            if (c != null)
            {
                var segs = c.DuplicateSegments();
                if (segs != null) pieces.AddRange(segs);
                if (pieces.Count <= 1) { Fail(o, "curve is a single segment; nothing to explode"); return; }
            }
            else
            {
                var b = GeoUtil.AsBrep(o.Geometry);
                if (b == null) { Fail(o, o.ObjectType + " cannot be exploded"); return; }
                if (b.Faces.Count <= 1) { Fail(o, "object has a single face; nothing to explode"); return; }
                for (int i = 0; i < b.Faces.Count; i++) { var f = b.Faces[i].DuplicateFace(false); if (f != null) pieces.Add(f); }
            }
            var hs = new List<string>();
            foreach (var g in pieces) { var id = AddLike(o, g); if (id != Guid.Empty) hs.Add(_ctx.Handles.For(id)); }
            Delete(o);
            _o.Items.Add(new { source = H(o), pieces = hs.Count, handles = hs });
        }

        void OffsetCurve(RhinoObject o)
        {
            double tol = _ctx.Tol;
            var c = o.Geometry as Curve;
            if (c == null) { Fail(o, "not a curve"); return; }
            double d = _p.Num("distance", true);
            string cs = _p.Str("corner", false, "sharp");
            string side = _p.Str("side");
            bool both = _p.Bool("both_sides");
            Plane pl;
            if (_p.Has("plane")) { if (!_p.TryPlane("plane", out pl, true)) return; }
            else if (!c.TryGetPlane(out pl, tol)) pl = Plane.WorldXY;
            CurveOffsetCornerStyle style;
            if (!Enum.TryParse<CurveOffsetCornerStyle>(cs, true, out style)) { _p.Err("'corner' must be sharp, round, smooth or chamfer"); return; }
            if (_p.Errors.Count > 0) return;
            if (Math.Abs(d) <= tol) { _p.Err("'distance' must be larger than tolerance"); return; }
            if (side != null && side != "inside" && side != "outside") { _p.Err("'side' must be inside or outside"); return; }
            if (side != null && !c.IsClosed) { _p.Err("'side' only applies to closed curves; use a signed 'distance' for open curves"); return; }

            Func<double, Curve[]> off = dist => c.Offset(pl, dist, tol, style);
            double mag = Math.Abs(d);
            var made = new List<Curve>();
            if (both)
            {
                var r1 = off(mag); var r2 = off(-mag);
                if (r1 != null) made.AddRange(r1);
                if (r2 != null) made.AddRange(r2);
            }
            else
            {
                double sd = d;
                if (side != null)
                {
                    double L0 = c.GetLength();
                    var t = off(mag);
                    double Lt = t == null || t.Length == 0 ? double.NaN : t.Sum(x => x.GetLength());
                    bool plusIsOutside = !double.IsNaN(Lt) && Lt > L0;
                    sd = (side == "outside") == plusIsOutside ? mag : -mag;
                }
                var r = off(sd);
                if (r != null) made.AddRange(r);
            }
            if (made.Count == 0) { Fail(o, "Rhino could not offset this curve by " + d + " (non-planar curve, or distance larger than local curvature radius)"); return; }
            var hs = new List<string>();
            foreach (var g in made)
            {
                string log;
                if (!g.IsValidWithLog(out log)) continue;
                var id = AddLike(o, g); if (id != Guid.Empty) hs.Add(_ctx.Handles.For(id));
            }
            if (hs.Count == 0) { Fail(o, "offset result was invalid"); return; }
            _o.Items.Add(new { source = H(o), handles = hs, length = GeoUtil.R(made.Sum(x => x.GetLength())) });
        }

        void Fillet(RhinoObject o)
        {
            double tol = _ctx.Tol;
            double r = _p.Num("radius", true);
            var et = _p.Tok("edges");
            if (_p.Errors.Count > 0) return;
            if (r <= 0) { _p.Err("'radius' must be > 0"); return; }
            var b = GeoUtil.AsBrep(o.Geometry);
            if (b == null) { Fail(o, "not a brep/solid"); return; }
            var idx = new List<int>();
            if (et == null || (et.Type == JTokenType.String && (string)et == "all"))
            {
                for (int i = 0; i < b.Edges.Count; i++) if (b.Edges[i].Valence == EdgeAdjacency.Interior) idx.Add(i);
            }
            else if (et is JArray)
            {
                foreach (var x in (JArray)et)
                {
                    if (x.Type != JTokenType.Integer || (int)x < 0 || (int)x >= b.Edges.Count) { _p.Err("'edges' index out of range 0.." + (b.Edges.Count - 1)); return; }
                    idx.Add((int)x);
                }
            }
            else { _p.Err("'edges' must be 'all' or an array of edge indices"); return; }
            if (idx.Count == 0) { Fail(o, "no (interior) edges selected"); return; }
            double v0 = GeoUtil.Volume(b);
            var radii = idx.Select(_ => r).ToList();
            Brep[] res = null;
            int budgetMs = _p.Has("budget_ms") ? _p.Int("budget_ms", false, Resources.Limits.FilletBudgetMs) : Resources.Limits.FilletBudgetMs;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ladder = new List<string>();
            var steps = new[] { new { name = "distance_from_edge", rail = RailType.DistanceFromEdge }, new { name = "rolling_ball", rail = RailType.RollingBall } };
            bool timedOut = false;
            foreach (var st in steps)
            {
                if (sw.ElapsedMilliseconds > budgetMs) { timedOut = true; ladder.Add(st.name + ": skipped, budget used up"); break; }
                try { res = Brep.CreateFilletEdges(b, idx, radii, radii, BlendType.Fillet, st.rail, tol); } catch (Exception) { }
                ladder.Add(st.name + ": " + ((res == null || res.Length == 0) ? "failed" : "ok") + " (" + sw.ElapsedMilliseconds + " ms)");
                if (res != null && res.Length > 0) break;
            }
            if (res != null && res.Length > 0 && sw.ElapsedMilliseconds > budgetMs) _o.Warnings.Add("fillet took " + sw.ElapsedMilliseconds + " ms (budget " + budgetMs + " ms); the result was kept");
            if ((res == null || res.Length == 0) && timedOut)
            {
                _timeoutMsg = "fillet_edges exceeded its time budget of " + budgetMs + " ms after " + sw.ElapsedMilliseconds + " ms (" + string.Join("; ", ladder) + ")";
                _timeout = new { budget_ms = budgetMs, elapsed_ms = sw.ElapsedMilliseconds, edges = idx.Count, radius = r, tried = ladder, faces = b.Faces.Count, hints = new[] { "radius larger than the adjacent faces allow", "edges meeting at tiny angles or near-tangent faces", "try fewer edges per call, or a smaller radius" } };
                Fail(o, "time budget exceeded"); return;
            }
            if (res == null || res.Length == 0)
            {
                double minLen = idx.Min(i => b.Edges[i].GetLength());
                Fail(o, "fillet with radius " + r + " failed on " + idx.Count + " edges (shortest selected edge " + GeoUtil.R(minLen) + "); try a smaller radius or fewer edges");
                return;
            }
            Brep result = res[0];
            if (res.Length > 1)
            {
                var j = Brep.JoinBreps(res, tol);
                if (j != null && j.Length == 1) result = j[0];
            }
            string log;
            if (!result.IsValidWithLog(out log)) { Fail(o, "fillet produced an invalid brep: " + GeoUtil.FirstLines(log, 2, 160)); return; }
            if (b.IsSolid && !result.IsSolid) { Fail(o, "fillet result is no longer a closed solid; try a smaller radius"); return; }
            double v1 = GeoUtil.Volume(result);
            if (v0 > 0 && (v1 < v0 * 0.5 || v1 > v0 * 1.5)) { Fail(o, "fillet volume changed implausibly (" + GeoUtil.R(v0) + " -> " + GeoUtil.R(v1) + "); try a smaller radius"); return; }
            string h = ReplaceKeep(o, result);
            if (h == null) { Fail(o, "could not store the filleted brep"); return; }
            _o.Items.Add(new { handle = h, edges_filleted = idx.Count, radius = r, elapsed_ms = sw.ElapsedMilliseconds, tried = ladder, faces = result.Faces.Count, volume_before = GeoUtil.R(v0), volume_after = GeoUtil.R(v1), solid = result.IsSolid });
        }

        void Shell(RhinoObject o)
        {
            double tol = _ctx.Tol;
            double t = _p.Num("thickness", true);
            var rf = _p.Tok("remove_faces") as JArray;
            if (_p.Has("remove_faces") && rf == null) _p.Err("'remove_faces' must be an array of face indices");
            if (_p.Errors.Count > 0) return;
            if (t <= tol) { _p.Err("'thickness' must be > tolerance (positive = inward)"); return; }
            var b = GeoUtil.AsBrep(o.Geometry);
            if (b == null) { Fail(o, "not a brep/solid"); return; }
            var faces = new List<int>();
            if (rf != null) foreach (var x in rf)
                {
                    if (x.Type != JTokenType.Integer || (int)x < 0 || (int)x >= b.Faces.Count) { _p.Err("'remove_faces' index out of range 0.." + (b.Faces.Count - 1)); return; }
                    faces.Add((int)x);
                }
            if (faces.Count == 0) { _p.Err("'remove_faces' needs at least one face index (Rhino cannot shell without an opening); see face indices with geometry_validate / explode"); return; }
            double v0 = GeoUtil.Volume(b);
            // Verified live: positive distance offsets inward on a solid.
            Brep[] res = Brep.CreateShell(b, faces, t, tol);
            if (res == null || res.Length == 0) { Fail(o, "shell failed (thickness " + t + ")"); return; }
            var result = res[0];
            if (res.Length > 1) { var j = Brep.JoinBreps(res, tol); if (j != null && j.Length == 1) result = j[0]; }
            string log;
            if (!result.IsValidWithLog(out log)) { Fail(o, "shell produced an invalid brep: " + GeoUtil.FirstLines(log, 2, 160)); return; }
            double v1 = GeoUtil.Volume(result);
            var bo = b.GetBoundingBox(true); bo.Inflate(tol * 2);
            if (!bo.Contains(result.GetBoundingBox(true).Min) || !bo.Contains(result.GetBoundingBox(true).Max)) { Fail(o, "shell grew outside the original bounding box (offset went outward)"); return; }
            if (v0 > 0 && v1 >= v0) { Fail(o, "shell result is not thinner than the original (" + GeoUtil.R(v1) + " >= " + GeoUtil.R(v0) + ")"); return; }
            var cpInfo = new Dictionary<string, object>();
            result = Lighten(result, _p.Int("max_cp", false, Resources.Limits.MaxSurfaceControlPoints), cpInfo);
            string h = ReplaceKeep(o, result);
            if (h == null) { Fail(o, "could not store the shell"); return; }
            _o.Items.Add(new { cp = cpInfo, handle = h, thickness = t, removed_faces = faces, volume_before = GeoUtil.R(v0), volume_after = GeoUtil.R(v1), solid = result.IsSolid });
        }

        void CapHoles(RhinoObject o)
        {
            var b = GeoUtil.AsBrep(o.Geometry);
            if (b == null) { Fail(o, "not a brep"); return; }
            int nakedBefore = b.Edges.Count(e => e.Valence == EdgeAdjacency.Naked);
            if (nakedBefore == 0) { Fail(o, "object is already closed (no naked edges)"); return; }
            var d = b.DuplicateBrep();
            var res = d.CapPlanarHoles(_ctx.Tol);
            if (res == null) { Fail(o, "no planar hole could be capped (" + nakedBefore + " naked edges remain)"); return; }
            string h = ReplaceKeep(o, res);
            _o.Items.Add(new { handle = h, naked_edges_before = nakedBefore, naked_edges_after = res.Edges.Count(e => e.Valence == EdgeAdjacency.Naked), solid = res.IsSolid, volume = GeoUtil.R(GeoUtil.Volume(res)) });
        }

        void Split(RhinoObject o)
        {
            double tol = _ctx.Tol;
            var cutters = _p.Objs("cutter");
            if (_p.Errors.Count > 0) return;
            var pieces = new List<GeometryBase>();
            var c = o.Geometry as Curve;
            if (c != null)
            {
                var ts = new List<double>();
                foreach (var cu in cutters)
                {
                    var cc = cu.Geometry as Curve;
                    var cb = GeoUtil.AsBrep(cu.Geometry);
                    if (cc != null)
                    {
                        var ev = Rhino.Geometry.Intersect.Intersection.CurveCurve(c, cc, tol, tol);
                        if (ev != null) foreach (var e in ev) ts.Add(e.ParameterA);
                    }
                    else if (cb != null)
                    {
                        Curve[] outC; Point3d[] pts;
                        Rhino.Geometry.Intersect.Intersection.CurveBrep(c, cb, tol, out outC, out pts);
                        if (pts != null) foreach (var pt in pts) { double t; if (c.ClosestPoint(pt, out t)) ts.Add(t); }
                    }
                }
                ts = ts.Distinct().OrderBy(x => x).ToList();
                if (ts.Count == 0) { Fail(o, "cutter does not intersect the curve"); return; }
                var sp = c.Split(ts);
                if (sp != null) pieces.AddRange(sp);
            }
            else
            {
                var b = GeoUtil.AsBrep(o.Geometry);
                if (b == null) { Fail(o, o.ObjectType + " cannot be split"); return; }
                var cb = cutters.Select(x => GeoUtil.AsBrep(x.Geometry)).Where(x => x != null).ToList();
                var cc = cutters.Where(x => x.Geometry is Curve).Select(x => (Curve)x.Geometry).ToList();
                Brep[] r = null;
                if (cb.Count > 0) r = b.Split(cb, tol);
                if ((r == null || r.Length == 0) && cc.Count > 0) r = b.Split(cc, tol);
                if (r != null) pieces.AddRange(r);
            }
            if (pieces.Count < 2) { Fail(o, "split produced " + pieces.Count + " piece(s): the cutter does not cross the object (or only touches it)"); return; }
            int total = pieces.Count;
            pieces = ApplyKeep(pieces, cutters);
            if (pieces == null) return;
            var hs = new List<string>();
            foreach (var g in pieces)
            {
                if (!g.IsValid) continue;
                var id = AddLike(o, g); if (id != Guid.Empty) hs.Add(_ctx.Handles.For(id));
            }
            Delete(o);
            _o.Items.Add(new { source = H(o), pieces = hs.Count, handles = hs, kept = _p.Str("keep", false, "all"), discarded = total - pieces.Count });
        }

        void Trim(RhinoObject o)
        {
            double tol = _ctx.Tol;
            bool flip = _p.Bool("flip");
            var c = o.Geometry as Curve;
            if (c != null)
            {
                var dom = _p.Tok("domain");
                double[] dv;
                if (dom != null)
                {
                    if (!P.TryNums(dom, 2, 2, out dv)) { _p.Err("'domain' must be [t0,t1]"); return; }
                    var t = c.Trim(dv[0], dv[1]);
                    if (t == null) { Fail(o, "domain [" + dv[0] + "," + dv[1] + "] is outside the curve domain " + c.Domain.T0 + ".." + c.Domain.T1); return; }
                    ReplaceKeep(o, t);
                    _o.Items.Add(new { handle = H(o), length = GeoUtil.R(t.GetLength()) });
                    return;
                }
                _p.Err("trim on curves needs 'domain' [t0,t1] (curve parameter range to keep; see curve domain via geometry_validate)");
                return;
            }
            var b = GeoUtil.AsBrep(o.Geometry);
            if (b == null) { Fail(o, o.ObjectType + " cannot be trimmed"); return; }
            Brep[] r = null;
            List<RhinoObject> trimCutters = null;
            if (_p.Has("plane"))
            {
                Plane pl;
                if (!_p.TryPlane("plane", out pl, true)) return;
                if (flip) pl.Flip();
                r = b.Trim(pl, tol);
            }
            else
            {
                var cut = _p.Objs("cutter");
                if (_p.Errors.Count > 0) return;
                trimCutters = cut;
                var cb = GeoUtil.AsBrep(cut[0].Geometry);
                if (cb == null) { _p.Err("'cutter' must be a surface/polysurface (or give 'plane')"); return; }
                cb = cb.DuplicateBrep();
                if (flip) cb.Flip();
                r = b.Trim(cb, tol);
            }
            if (r == null || r.Length == 0) { Fail(o, "trim removed nothing or everything (does the cutter cross the object? try flip:true)"); return; }
            var kept = ApplyKeep(r.Select(x => (GeometryBase)x).ToList(), trimCutters);
            if (kept == null) return;
            var hs = new List<string>();
            foreach (var g in kept) { if (!g.IsValid) continue; var id = AddLike(o, g); if (id != Guid.Empty) hs.Add(_ctx.Handles.For(id)); }
            Delete(o);
            _o.Items.Add(new { source = H(o), handles = hs, kept = _p.Str("keep", false, "all"), discarded = r.Length - kept.Count });
        }

        void Rebuild(RhinoObject o)
        {
            var c = o.Geometry as Curve;
            if (c == null) { Fail(o, "not a curve"); return; }
            int n = _p.Int("points", true), deg = _p.Int("degree", false, 3);
            bool tang = _p.Bool("preserve_tangents");
            if (_p.Errors.Count > 0) return;
            if (deg < 1 || deg > 11 || n < deg + 1) { _p.Err("need 1 <= degree <= 11 and points >= degree+1"); return; }
            var nc = c.Rebuild(n, deg, tang);
            if (nc == null || !nc.IsValid) { Fail(o, "rebuild failed with " + n + " points, degree " + deg); return; }
            double maxDev = 0;
            var dom = c.Domain;
            for (int i = 0; i <= 50; i++)
            {
                var pt = c.PointAt(dom.ParameterAt(i / 50.0));
                double t;
                if (nc.ClosestPoint(pt, out t)) maxDev = Math.Max(maxDev, pt.DistanceTo(nc.PointAt(t)));
            }
            string h = ReplaceKeep(o, nc);
            _o.Items.Add(new { handle = h, point_count = n, degree = deg, max_deviation = GeoUtil.R(maxDev) });
        }

        static long SurfCp(Surface s) { var ns = s as NurbsSurface; return ns == null ? 4 : (long)ns.Points.CountU * ns.Points.CountV; }

        /// <summary>Reports face control-point counts of a result and rebuilds it when it is one untrimmed face above maxCp.</summary>
        Brep Lighten(Brep g, int maxCp, Dictionary<string, object> info)
        {
            long total = 0, mx = 0;
            foreach (var s in g.Surfaces) { long k = SurfCp(s); total += k; if (k > mx) mx = k; }
            info["cp_total"] = total; info["cp_max_face"] = mx;
            if (mx <= maxCp) return g;
            var ns = g.Surfaces.Count == 1 && g.Faces.Count == 1 && g.Faces[0].IsSurface ? g.Surfaces[0] as NurbsSurface : null;
            if (ns == null)
            {
                _o.Warnings.Add("result has a face with " + mx + " control points (max_cp " + maxCp + ") and cannot be rebuilt automatically (joined or trimmed). Explode it and use freeform_surface rebuild_surface on the dense faces.");
                return g;
            }
            double ratio = (double)ns.Points.CountU / Math.Max(1, ns.Points.CountV);
            int pv = Math.Max(4, (int)Math.Floor(Math.Sqrt(maxCp / ratio))), pu = Math.Max(4, (int)Math.Floor(pv * ratio));
            while ((long)pu * pv > maxCp && pu > 4 && pv > 4) { if (pu >= pv) pu--; else pv--; }
            var rb = ns.Rebuild(Math.Min(3, pu - 1), Math.Min(3, pv - 1), pu, pv);
            if (rb == null) { _o.Warnings.Add("rebuild to " + pu + "x" + pv + " failed; result kept with " + mx + " control points"); return g; }
            double dev = 0;
            for (int i = 0; i <= 8; i++) for (int j = 0; j <= 8; j++)
                {
                    var q = rb.PointAt(rb.Domain(0).ParameterAt(i / 8.0), rb.Domain(1).ParameterAt(j / 8.0));
                    double u, v;
                    if (ns.ClosestPoint(q, out u, out v)) dev = Math.Max(dev, q.DistanceTo(ns.PointAt(u, v)));
                }
            var nb = Brep.CreateFromSurface(rb);
            if (nb == null) return g;
            info["rebuilt_from_cp"] = mx; info["rebuilt_to"] = pu + "x" + pv; info["rebuild_max_deviation"] = GeoUtil.R(dev);
            info["cp_total"] = (long)pu * pv; info["cp_max_face"] = (long)pu * pv;
            _o.Warnings.Add("result had " + mx + " control points; rebuilt to " + pu + "x" + pv + " (max deviation " + GeoUtil.R(dev) + ")");
            return nb;
        }

        void OffsetSurface(RhinoObject o)
        {
            double tol = _ctx.Tol;
            double d = _p.Num("distance", true);
            bool solid = _p.Bool("solid");
            if (_p.Errors.Count > 0) return;
            if (Math.Abs(d) <= tol) { _p.Err("'distance' must be larger than tolerance"); return; }
            var b = GeoUtil.AsBrep(o.Geometry);
            if (b == null) { Fail(o, "not a surface/brep"); return; }
            Brep[] blends, walls;
            var res = Brep.CreateOffsetBrep(b, d, solid, false, tol, out blends, out walls);
            if (res == null || res.Length == 0) { Fail(o, "offset by " + d + " failed (distance too large for the surface curvature?)"); return; }
            var hs = new List<string>();
            var cpInfo = new Dictionary<string, object>();
            int maxCp = _p.Int("max_cp", false, Resources.Limits.MaxSurfaceControlPoints);
            foreach (var g0 in res)
            {
                string log;
                if (!g0.IsValidWithLog(out log)) continue;
                var g = Lighten(g0, maxCp, cpInfo);
                var id = AddLike(o, g); if (id != Guid.Empty) hs.Add(_ctx.Handles.For(id));
            }
            if (hs.Count == 0) { Fail(o, "offset result was invalid"); return; }
            _o.Items.Add(new { source = H(o), cp = cpInfo, handles = hs, distance = d, solid = res.All(x => x.IsSolid), volume = GeoUtil.R(res.Sum(x => GeoUtil.Volume(x))), area = GeoUtil.R(AreaMassProperties.Compute(res[0]) == null ? 0 : AreaMassProperties.Compute(res[0]).Area) });
        }

        // ---------- keep (split / trim) ----------
        static double Metric(GeometryBase g)
        {
            var c = g as Curve;
            if (c != null) return c.GetLength();
            var b = GeoUtil.AsBrep(g);
            if (b == null) return 0;
            if (b.IsSolid) return GeoUtil.Volume(b);
            try { var am = AreaMassProperties.Compute(b); return am == null ? 0 : am.Area; } catch (Exception) { return 0; }
        }

        static Point3d RepPoint(GeometryBase g)
        {
            var c = g as Curve;
            if (c != null) return c.PointAt(c.Domain.ParameterAt(0.5));
            var b = GeoUtil.AsBrep(g);
            if (b == null) return Point3d.Unset;
            try
            {
                if (b.IsSolid) { var vm = VolumeMassProperties.Compute(b); if (vm != null) return vm.Centroid; }
                var am = AreaMassProperties.Compute(b);
                if (am != null)
                {
                    var cp = b.ClosestPoint(am.Centroid);
                    if (cp.IsValid) return cp;
                }
            }
            catch (Exception) { }
            return b.GetBoundingBox(true).Center;
        }

        static double DistTo(GeometryBase g, Point3d pt)
        {
            var c = g as Curve;
            if (c != null) { double t; return c.ClosestPoint(pt, out t) ? c.PointAt(t).DistanceTo(pt) : double.MaxValue; }
            var b = GeoUtil.AsBrep(g);
            if (b == null) return double.MaxValue;
            var cp = b.ClosestPoint(pt);
            return cp.IsValid ? cp.DistanceTo(pt) : double.MaxValue;
        }

        bool Holds(GeometryBase g, Point3d pt)
        {
            double tol = _ctx.Tol;
            var c = g as Curve;
            Plane pl;
            if (c != null) return c.IsClosed && c.TryGetPlane(out pl, tol) && c.Contains(pt, pl, tol) == PointContainment.Inside;
            var b = GeoUtil.AsBrep(g);
            return b != null && b.IsSolid && b.IsPointInside(pt, tol, false);
        }

        /// <summary>Is pt inside any closed region cutter (solid or closed planar curve)? hasRegion tells whether any cutter can define a region.</summary>
        bool InCutter(Point3d pt, List<RhinoObject> cutters, out bool hasRegion)
        {
            hasRegion = false;
            double tol = _ctx.Tol;
            foreach (var cu in cutters)
            {
                var cc = cu.Geometry as Curve;
                Plane pl;
                if (cc != null)
                {
                    if (!cc.IsClosed || !cc.TryGetPlane(out pl, tol)) continue;
                    hasRegion = true;
                    if (cc.Contains(pt, pl, tol) != PointContainment.Outside) return true;
                    continue;
                }
                var cb = GeoUtil.AsBrep(cu.Geometry);
                if (cb != null && cb.IsSolid) { hasRegion = true; if (cb.IsPointInside(pt, tol, false)) return true; }
            }
            return false;
        }

        /// <summary>Filters split/trim pieces by the 'keep' option. Returns null (after reporting) when the option cannot be applied.</summary>
        List<GeometryBase> ApplyKeep(List<GeometryBase> pieces, List<RhinoObject> cutters)
        {
            string keep = _p.Str("keep", false, "all");
            if (_p.Errors.Count > 0) return null;
            if (keep == "all") return pieces;
            var valid = new[] { "all", "inside", "outside", "largest", "smallest", "containing_point", "nearest_point" };
            if (!valid.Contains(keep)) { _p.Err("'keep' must be one of " + string.Join(", ", valid)); return null; }
            if (keep == "largest" || keep == "smallest")
            {
                var m = pieces.Select(Metric).ToList();
                double target = keep == "largest" ? m.Max() : m.Min();
                int i = m.IndexOf(target);
                return new List<GeometryBase> { pieces[i] };
            }
            if (keep == "containing_point" || keep == "nearest_point")
            {
                if (!_p.Has("point")) { _p.Err("keep '" + keep + "' needs 'point' [x,y,z]"); return null; }
                var pt = _p.Pt("point", true);
                if (_p.Errors.Count > 0) return null;
                if (keep == "containing_point")
                {
                    var inside = pieces.Where(g => Holds(g, pt)).ToList();
                    if (inside.Count >= 1) return new List<GeometryBase> { inside.OrderBy(g => DistTo(g, pt)).First() };
                }
                var best = pieces.OrderBy(g => DistTo(g, pt)).First();
                if (keep == "containing_point") _o.Warnings.Add("no piece contains the point strictly; kept the nearest piece (distance " + GeoUtil.R(DistTo(best, pt)) + ")");
                return new List<GeometryBase> { best };
            }
            // inside / outside a closed cutter
            if (cutters == null || cutters.Count == 0) { _p.Err("keep '" + keep + "' needs a 'cutter' (a plane cannot define inside/outside; use largest, smallest or a point option)"); return null; }
            var kept = new List<GeometryBase>();
            bool anyRegion = false;
            foreach (var g in pieces)
            {
                bool region;
                bool inside = InCutter(RepPoint(g), cutters, out region);
                anyRegion |= region;
                if (inside == (keep == "inside")) kept.Add(g);
            }
            if (!anyRegion) { _p.Err("keep '" + keep + "' needs a closed cutter: a solid, or a closed planar curve"); return null; }
            if (kept.Count == 0) _o.Warnings.Add("no piece lies " + keep + " the cutter; everything was discarded");
            return kept;
        }

        // ---------- fillet_corners ----------
        void FilletCorners(RhinoObject o)
        {
            double tol = _ctx.Tol;
            var c = o.Geometry as Curve;
            if (c == null) { Fail(o, "not a curve"); return; }
            double r = _p.Num("radius", true);
            if (_p.Errors.Count > 0) return;
            if (r <= tol) { _p.Err("'radius' must be larger than tolerance"); return; }
            var before = new Dictionary<string, object>(); DesignerCurves.CurveFacts(c, before);
            Curve res = null;
            try { res = Curve.CreateFilletCornersCurve(c, r, tol, RhinoMath.ToRadians(_ctx.AngleTolDeg)); } catch (Exception) { }
            if (res == null || !res.IsValid) { Fail(o, "could not fillet the corners with radius " + r + " (curve has no kinks, or the radius is larger than the adjacent segments allow)"); return; }
            var radii = new List<double>();
            var segs = res.DuplicateSegments();
            if (segs != null) foreach (var sg in segs)
                {
                    Arc arc;
                    if (!(sg is LineCurve) && sg.TryGetArc(out arc, tol * 0.5)) radii.Add(GeoUtil.R(arc.Radius));
                }
            var after = new Dictionary<string, object>(); DesignerCurves.CurveFacts(res, after);
            double len0 = c.GetLength();
            string h = ReplaceKeep(o, res);
            if (h == null) { Fail(o, "could not store the filleted curve"); return; }
            _o.Items.Add(new
            {
                handle = h, radius = r, corners_filleted = radii.Count,
                arc_radius_min = radii.Count > 0 ? radii.Min() : 0, arc_radius_max = radii.Count > 0 ? radii.Max() : 0,
                kinks_before = before.ContainsKey("kinks") ? before["kinks"] : null, kinks_after = after.ContainsKey("kinks") ? after["kinks"] : null,
                closed = res.IsClosed, length_before = GeoUtil.R(len0), length_after = GeoUtil.R(res.GetLength()),
                degree = res.Degree, segments = segs == null ? 1 : segs.Length
            });
        }

        // ---------- project_curve / pull_curve ----------
        void ProjectOrPull(RhinoObject o, bool pull)
        {
            double tol = _ctx.Tol;
            var c = o.Geometry as Curve;
            if (c == null) { Fail(o, "not a curve"); return; }
            var ontoObjs = _p.Objs("onto");
            bool hasDir = _p.Has("direction");
            var dir = hasDir ? _p.Vec("direction") : new Vector3d(0, 0, -1);
            bool join = _p.Bool("join", true);
            if (_p.Errors.Count > 0) return;
            var breps = new List<Brep>();
            foreach (var u in ontoObjs)
            {
                var bb = GeoUtil.AsBrep(u.Geometry);
                if (bb == null) { _p.Err("'onto': object " + H(u) + " is " + u.ObjectType + ", not a surface/polysurface"); return; }
                breps.Add(bb);
            }
            var made = new List<Curve>();
            if (!pull)
            {
                if (dir.Length <= 1e-12) { _p.Err("'direction' has zero length"); return; }
                var r = Curve.ProjectToBrep(c, breps, dir, tol);
                if (r != null) made.AddRange(r);
            }
            else
            {
                foreach (var bb in breps)
                    foreach (var face in bb.Faces)
                    {
                        Curve[] r = null;
                        try { r = c.PullToBrepFace(face, tol); } catch (Exception) { }
                        if (r != null) made.AddRange(r);
                    }
            }
            if (made.Count == 0)
            {
                Fail(o, pull ? "nothing could be pulled onto the surface (curve too far from it?)" : "projection along " + string.Format("[{0},{1},{2}]", GeoUtil.R(dir.X), GeoUtil.R(dir.Y), GeoUtil.R(dir.Z)) + " does not hit the surface; check direction (try the opposite sign) and that the curve is over/under the surface");
                return;
            }
            if (join && made.Count > 1)
            {
                var j = Curve.JoinCurves(made, tol * 2);
                if (j != null && j.Length > 0) made = j.ToList();
            }
            double maxDev = 0;
            foreach (var m in made)
                for (int i = 0; i <= 20; i++)
                {
                    var pt = m.PointAt(m.Domain.ParameterAt(i / 20.0));
                    double best = double.MaxValue;
                    foreach (var bb in breps) { var cp = bb.ClosestPoint(pt); if (cp.IsValid) best = Math.Min(best, cp.DistanceTo(pt)); }
                    if (best < double.MaxValue) maxDev = Math.Max(maxDev, best);
                }
            var hs = new List<string>();
            foreach (var m in made) { var id = AddLike(o, m); if (id != Guid.Empty) hs.Add(_ctx.Handles.For(id)); }
            // sources are kept by default (the usual workflow keeps the original curve)
            if (_p.Has("keep_inputs") && !_keep) Delete(o);
            _o.Items.Add(new { source = H(o), handles = hs, pieces = hs.Count, length = GeoUtil.R(made.Sum(x => x.GetLength())), max_deviation = GeoUtil.R(maxDev), direction = pull ? null : GeoUtil.Arr(dir) });
        }

        // ---------- engrave / emboss ----------
        void Engrave(RhinoObject o, bool emboss)
        {
            double tol = _ctx.Tol;
            string opName = emboss ? "emboss" : "engrave";
            var b = GeoUtil.AsBrep(o.Geometry);
            if (b == null || !b.IsSolid) { Fail(o, "target must be a closed solid (" + o.ObjectType + (b != null ? ", not closed" : "") + ")"); return; }
            double depth = _p.Num("depth", true);
            string text = _p.Str("text");
            bool hasCurves = _p.Has("curves");
            double th = _p.Num("text_height", false, double.NaN);
            string font = _p.Str("font", false, "Arial");
            bool bold = _p.Bool("bold"), italic = _p.Bool("italic"), center = _p.Bool("center", true);
            int faceIdx = _p.Has("face") ? _p.Int("face") : -1;
            double rot = _p.Num("rotation_deg", false, 0);
            var curveObjs = hasCurves ? _p.Objs("curves") : new List<RhinoObject>();
            Point3d at = _p.Has("at") ? _p.Pt("at") : Point3d.Unset;
            Vector3d xAxisIn = _p.Has("x_axis") ? _p.Vec("x_axis") : Vector3d.Unset;
            Plane userPlane = Plane.Unset;
            bool hasPlane = _p.Has("plane");
            if (hasPlane && !_p.TryPlane("plane", out userPlane, true)) return;
            if (_p.Errors.Count > 0) return;
            if (depth <= tol) { _p.Err("'depth' must be > tolerance"); return; }
            if ((text == null) == (!hasCurves)) { _p.Err("give exactly one of 'text' (with 'text_height') or 'curves' (closed curve handles)"); return; }
            if (text != null && (double.IsNaN(th) || th <= 0)) { _p.Err("'text_height' is required (> 0) with 'text'"); return; }
            if (text != null && text.Length > DesignerCurves.MaxTextChars) { _p.Err("text longer than " + DesignerCurves.MaxTextChars + " characters"); return; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rssBlock = Resources.CheckRss("geometry_edit");
            if (rssBlock != null) { _o.Resource = rssBlock; Fail(o, "memory limit"); return; }

            // ---- source curves (curves mode): placed where they are, projected onto the surface plane later
            List<Curve> srcCurves = null;
            if (hasCurves)
            {
                srcCurves = new List<Curve>();
                foreach (var co in curveObjs)
                {
                    var cc = co.Geometry as Curve;
                    if (cc == null || !cc.IsClosed) { Fail(o, "'curves' object " + H(co) + " is not a closed curve"); return; }
                    srcCurves.Add(cc.DuplicateCurve());
                }
                if (srcCurves.Count == 0) { Fail(o, "'curves' resolved to nothing"); return; }
                if (!at.IsValid) { var bbx = BoundingBox.Empty; foreach (var cc in srcCurves) bbx.Union(cc.GetBoundingBox(true)); at = bbx.Center; }
            }

            // ---- where on the solid: origin, outward normal, face
            Point3d origin; Vector3d n; int usedFace = -1;
            if (hasPlane)
            {
                var cp0 = b.ClosestPoint(userPlane.Origin);
                origin = cp0.IsValid ? cp0 : userPlane.Origin; n = userPlane.ZAxis; n.Unitize();
            }
            else
            {
                BrepFace face = null;
                if (faceIdx >= 0)
                {
                    if (faceIdx >= b.Faces.Count) { _p.Err("'face' index out of range 0.." + (b.Faces.Count - 1)); return; }
                    face = b.Faces[faceIdx];
                }
                else if (at.IsValid)
                {
                    Point3d cp; ComponentIndex ci; double s, t; Vector3d nv;
                    if (b.ClosestPoint(at, out cp, out ci, out s, out t, double.MaxValue, out nv) && ci.ComponentIndexType == ComponentIndexType.BrepFace)
                        face = b.Faces[ci.Index];
                }
                if (face == null)
                {
                    // most upward planar face, ties -> larger area
                    double bestScore = double.MinValue;
                    foreach (var f in b.Faces)
                    {
                        var fn = FaceNormal(f, f.Domain(0).Mid, f.Domain(1).Mid);
                        double score = fn.Z;
                        if (!f.IsPlanar(tol * 10)) score -= 10;
                        if (score > bestScore + 1e-9) { bestScore = score; face = f; }
                        else if (Math.Abs(score - bestScore) <= 1e-9 && face != null && FaceArea(f) > FaceArea(face)) face = f;
                    }
                }
                usedFace = face.FaceIndex;
                double u, v;
                Point3d probe;
                if (at.IsValid) probe = at;
                else
                {
                    var fb = face.DuplicateFace(false);
                    var am = AreaMassProperties.Compute(fb);
                    probe = am != null ? am.Centroid : fb.GetBoundingBox(true).Center;
                }
                if (!face.ClosestPoint(probe, out u, out v)) { u = face.Domain(0).Mid; v = face.Domain(1).Mid; }
                origin = face.PointAt(u, v);
                n = FaceNormal(face, u, v);
            }
            // make sure n points out of the solid
            double eps = Math.Max(depth * 0.25, tol * 5);
            bool inIn = b.IsPointInside(origin - n * eps, tol, false), outIn = b.IsPointInside(origin + n * eps, tol, false);
            if (!inIn && outIn) n = -n;
            else if (!inIn && !outIn) _o.Warnings.Add("could not confirm the outward direction (solid thinner than depth near the text); check the result");

            // ---- text plane: x = reading direction, y = up
            Vector3d xdir;
            if (xAxisIn.IsValid && xAxisIn.Length > 1e-12) { xdir = xAxisIn - n * (xAxisIn * n); }
            else if (hasPlane) { xdir = userPlane.XAxis - n * (userPlane.XAxis * n); }
            else
            {
                Vector3d up = Vector3d.ZAxis;
                if (Math.Abs(up * n) > 0.99) up = Vector3d.YAxis;
                up = up - n * (up * n); up.Unitize();
                xdir = Vector3d.CrossProduct(up, n);
            }
            if (xdir.Length <= 1e-9) xdir = Vector3d.XAxis - n * (Vector3d.XAxis * n);
            xdir.Unitize();
            if (Math.Abs(rot) > 0) xdir.Rotate(RhinoMath.ToRadians(rot), n);
            var ydir = Vector3d.CrossProduct(n, xdir); ydir.Unitize();
            var tp = new Plane(origin, xdir, ydir);

            // ---- outline curves on tp
            List<Curve> curves;
            if (text != null)
            {
                curves = DesignerCurves.TextOutlines(text, font, th, tp, tol, bold, italic, center);
                if (curves.Count == 0) { Fail(o, "no outline curves for this text/font (empty, only spaces, or font '" + font + "' missing)"); return; }
            }
            else
            {
                var xf = Transform.PlanarProjection(tp);
                curves = new List<Curve>();
                foreach (var cc in srcCurves) { var d = cc.DuplicateCurve(); d.Transform(xf); curves.Add(d); }
            }
            var glyphs = DesignerCurves.GroupGlyphs(curves, tp, tol);
            if (glyphs.Count == 0) { Fail(o, "no closed regions found in the outlines"); return; }
            double netArea = glyphs.Sum(g => g.Area);

            long faceTotal = b.Faces.Count + glyphs.Sum(g => 2L + g.Holes.Count + g.Outer.SpanCount);
            if (faceTotal > Resources.Limits.BooleanMaxFaceCount && !_p.Bool("force"))
            { _o.Resource = Resources.LimitFail("boolean_max_face_count", faceTotal, Resources.Limits.BooleanMaxFaceCount, opName + " would create about " + faceTotal + " faces; the limit is " + Resources.Limits.BooleanMaxFaceCount + ". Nothing was changed.", "Use fewer or shorter letters, a simpler font, or split the work over several calls.", true); Fail(o, "face limit"); return; }

            // ---- cutter solids, one per glyph
            double m = Math.Max(depth * 0.25, tol * 10);
            var dirExt = emboss ? n : -n;
            var cutters = new List<Brep>();
            foreach (var g in glyphs)
            {
                var pb = DesignerCurves.GlyphBrep(g, tol);
                if (pb == null) { Fail(o, "could not make a planar region from a letter outline"); return; }
                pb.Translate(emboss ? -n * m : n * m);
                var p0 = pb.GetBoundingBox(true).Center;
                var path = new LineCurve(p0, p0 + dirExt * (depth + m));
                var solid = pb.Faces[0].CreateExtrusion(path, true);
                if (solid == null || !solid.IsSolid)
                {
                    // fallback: outer prism minus counter prisms
                    solid = PrismWithHoles(g, n, dirExt, emboss ? -m : m, depth + m, tol);
                    if (solid == null) { Fail(o, "could not extrude a letter outline into a solid (self-intersecting outline?)"); return; }
                }
                if (solid.SolidOrientation == BrepSolidOrientation.Inward) solid.Flip();
                cutters.Add(solid);
            }

            double v0 = GeoUtil.Volume(b);
            Brep[] res = emboss ? Brep.CreateBooleanUnion(new List<Brep>(new[] { b }.Concat(cutters)), tol) : Brep.CreateBooleanDifference(new[] { b }, cutters, tol);
            if (res == null || res.Length == 0)
            {
                Fail(o, opName + " boolean failed (" + cutters.Count + " letter solids, " + sw.ElapsedMilliseconds + " ms). Check that the text lies on the face, try a different depth, or split the text into fewer letters");
                return;
            }
            var ordered = res.OrderByDescending(x => GeoUtil.Volume(x)).ToList();
            var result = ordered[0];
            string log;
            if (!result.IsValidWithLog(out log)) { Fail(o, opName + " produced an invalid brep: " + GeoUtil.FirstLines(log, 2, 160)); return; }
            if (!result.IsSolid) { Fail(o, opName + " result is not a closed solid; the text probably extends past the face edge (use a smaller text_height or move 'at')"); return; }
            double v1 = GeoUtil.Volume(result);
            double change = emboss ? v1 - v0 : v0 - v1;
            if (change <= 0) { Fail(o, opName + " changed nothing (volume " + GeoUtil.R(v0) + " -> " + GeoUtil.R(v1) + "); the text is probably not on the solid: set 'face' or 'at'"); return; }
            double expected = netArea * depth;
            if (expected > 0 && (change < expected * 0.8 || change > expected * 1.2))
                _o.Warnings.Add(opName + " volume " + GeoUtil.R(change) + " differs from letter area x depth " + GeoUtil.R(expected) + " (curved face, letters partly off the face, or overlapping outlines)");
            string h = ReplaceKeep(o, result);
            if (h == null) { Fail(o, "could not store the result"); return; }
            for (int i = 1; i < ordered.Count; i++)
            {
                var id = AddLike(o, ordered[i]);
                if (id != Guid.Empty) _o.Warnings.Add(opName + " left a loose piece " + _ctx.Handles.For(id) + " (the text cut the solid apart)");
            }
            _o.Items.Add(new
            {
                handle = h, op = opName, letters = glyphs.Count, counters = glyphs.Sum(g => g.Holes.Count),
                area_net = GeoUtil.R(netArea), depth = depth,
                volume_before = GeoUtil.R(v0), volume_after = GeoUtil.R(v1),
                removed_volume = emboss ? (double?)null : GeoUtil.R(change), added_volume = emboss ? (double?)GeoUtil.R(change) : null,
                expected_volume = GeoUtil.R(expected), solid = result.IsSolid, face = usedFace >= 0 ? (int?)usedFace : null,
                outward_normal = GeoUtil.Arr(n), text_origin = GeoUtil.Arr(origin), reading_direction = GeoUtil.Arr(xdir), elapsed_ms = sw.ElapsedMilliseconds
            });
        }

        static Vector3d FaceNormal(BrepFace f, double u, double v)
        {
            var nv = f.NormalAt(u, v);
            if (f.OrientationIsReversed) { /* NormalAt already accounts for orientation in RhinoCommon; the solid check in Engrave settles the sign */ }
            nv.Unitize();
            return nv;
        }

        static double FaceArea(BrepFace f)
        {
            try { var am = AreaMassProperties.Compute(f.DuplicateFace(false)); return am == null ? 0 : am.Area; } catch (Exception) { return 0; }
        }

        /// <summary>Fallback cutter: prism of the outer loop minus prisms of the counters.</summary>
        static Brep PrismWithHoles(Glyph g, Vector3d n, Vector3d dir, double shiftAlongN, double length, double tol)
        {
            Func<Curve, Brep> prism = c =>
            {
                var d = c.DuplicateCurve(); d.Translate(n * shiftAlongN);
                var srf = Surface.CreateExtrusion(d, dir * length);
                if (srf == null) return null;
                var bb = Brep.CreateFromSurface(srf);
                return bb == null ? null : bb.CapPlanarHoles(tol);
            };
            var outer = prism(g.Outer);
            if (outer == null || !outer.IsSolid) return null;
            if (g.Holes.Count == 0) return outer;
            var holes = g.Holes.Select(prism).Where(x => x != null && x.IsSolid).ToList();
            var diff = Brep.CreateBooleanDifference(new[] { outer }, holes, tol);
            return diff != null && diff.Length == 1 ? diff[0] : null;
        }
    }
}

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
                       "fillet_edges{radius, edges?='all'|[edge indices]} | shell{thickness, remove_faces:[face indices]} (thickness>0 = inward) | cap (close planar holes) | " +
                       "split{cutter} | trim{cutter | plane, flip?  or, for curves, domain[t0,t1]} | rebuild_curve{points, degree?=3} | offset_surface{distance, solid?}. " +
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
                    "\"op\":{\"type\":\"string\",\"enum\":[\"join\",\"explode\",\"offset_curve\",\"fillet_edges\",\"shell\",\"cap\",\"split\",\"trim\",\"rebuild_curve\",\"offset_surface\"]}," +
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
        }

        ToolContext _ctx; P _p; Out _o; bool _keep;

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            _ctx = ctx;
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
                default:
                    p.Err("unknown op '" + op + "'. Valid: join, explode, offset_curve, fillet_edges, shell, cap, split, trim, rebuild_curve, offset_surface");
                    break;
            }
            if (p.Errors.Count > 0) return Bad(p);

            int done = _o.Created.Count + _o.Modified.Count;
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
            try { res = Brep.CreateFilletEdges(b, idx, radii, radii, BlendType.Fillet, RailType.DistanceFromEdge, tol); } catch (Exception) { }
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
            _o.Items.Add(new { handle = h, edges_filleted = idx.Count, radius = r, faces = result.Faces.Count, volume_before = GeoUtil.R(v0), volume_after = GeoUtil.R(v1), solid = result.IsSolid });
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
            double v0 = GeoUtil.Volume(b);
            // Rhino's CreateShell offsets outward for positive distance on a properly oriented solid; we want positive = inward.
            Brep[] res = Brep.CreateShell(b, faces, -t, tol);
            if (res == null || res.Length == 0) { Fail(o, "shell failed (thickness " + t + ")"); return; }
            var result = res[0];
            if (res.Length > 1) { var j = Brep.JoinBreps(res, tol); if (j != null && j.Length == 1) result = j[0]; }
            string log;
            if (!result.IsValidWithLog(out log)) { Fail(o, "shell produced an invalid brep: " + GeoUtil.FirstLines(log, 2, 160)); return; }
            double v1 = GeoUtil.Volume(result);
            if (v0 > 0 && v1 >= v0) { Fail(o, "shell result is not thinner than the original (" + GeoUtil.R(v1) + " >= " + GeoUtil.R(v0) + ")"); return; }
            string h = ReplaceKeep(o, result);
            if (h == null) { Fail(o, "could not store the shell"); return; }
            _o.Items.Add(new { handle = h, thickness = t, removed_faces = faces, volume_before = GeoUtil.R(v0), volume_after = GeoUtil.R(v1), solid = result.IsSolid });
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
            var hs = new List<string>();
            foreach (var g in pieces)
            {
                if (!g.IsValid) continue;
                var id = AddLike(o, g); if (id != Guid.Empty) hs.Add(_ctx.Handles.For(id));
            }
            Delete(o);
            _o.Items.Add(new { source = H(o), pieces = hs.Count, handles = hs });
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
                var cb = GeoUtil.AsBrep(cut[0].Geometry);
                if (cb == null) { _p.Err("'cutter' must be a surface/polysurface (or give 'plane')"); return; }
                cb = cb.DuplicateBrep();
                if (flip) cb.Flip();
                r = b.Trim(cb, tol);
            }
            if (r == null || r.Length == 0) { Fail(o, "trim removed nothing or everything (does the cutter cross the object? try flip:true)"); return; }
            var hs = new List<string>();
            foreach (var g in r) { if (!g.IsValid) continue; var id = AddLike(o, g); if (id != Guid.Empty) hs.Add(_ctx.Handles.For(id)); }
            Delete(o);
            _o.Items.Add(new { source = H(o), handles = hs });
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
            foreach (var g in res)
            {
                string log;
                if (!g.IsValidWithLog(out log)) continue;
                var id = AddLike(o, g); if (id != Guid.Empty) hs.Add(_ctx.Handles.For(id));
            }
            if (hs.Count == 0) { Fail(o, "offset result was invalid"); return; }
            _o.Items.Add(new { source = H(o), handles = hs, distance = d, solid = res.All(x => x.IsSolid), volume = GeoUtil.R(res.Sum(x => GeoUtil.Volume(x))), area = GeoUtil.R(AreaMassProperties.Compute(res[0]) == null ? 0 : AreaMassProperties.Compute(res[0]).Area) });
        }
    }
}

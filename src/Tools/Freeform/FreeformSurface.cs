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
    public sealed class FreeformSurface : ITool
    {
        public string Name { get { return "freeform_surface"; } }
        public ToolKind Kind { get { return ToolKind.Mutate; } }
        public string Description
        {
            get
            {
                return "Freeform NURBS surface modelling in batch (items <= 50). Each item: {kind, ...params, plane?, layer?, name?, user_text?}. " +
                       "Points are in the 'plane' frame (default world_xy). Every result is validated; one bad item does not stop the others. " +
                       "Result per item: handle, bbox, area, faces, degree_u/v, cp_u/cp_v, closed_u/v, kinks_u/v (interior knots with multiplicity >= degree = C0 kink), naked_edges, solid. " +
                       "Curve inputs ('curve spec'): " + FF.CurveHelp + ". " +
                       "Kinds: nurbs_points{points:[[[x,y,z]..cols]..rows], weights?:[[w..]..], degree_u?=3, degree_v?=3, closed_u?, closed_v?} (control-point grid; closed = periodic, give each distinct point once) | " +
                       "through_points{points grid, degree_u?, degree_v?, closed_u?, closed_v?} (surface interpolates the grid) | " +
                       "loft{curves[>=2], type?=normal|loose|tight|straight|uniform, closed?, rebuild?=pointcount, refit?=tolerance, align?=true, cap?} | " +
                       "sweep2{rails[2], profiles[], closed?} | " +
                       "network{curves[] (auto-sorted) | u_curves[], v_curves[]; continuity?=position|loose|tangent|curvature (applies to the first/last u curve; with 'curves' to all edges), continuity_v?=position, edge_tol?, interior_tol?, angle_tol_deg?} (curves must meet within interior_tol). Curve specs {target, edge:n} are used as live brep edges, so tangent/curvature continuity matches the adjacent surface: the way to get true G2 between two surfaces is u_curves=[edge of A, edge of B] + v_curves=[two straight connectors] with continuity curvature | " +
                       "patch{inputs[curve specs or [x,y,z] points], u_spans?=10, v_spans?=10, stiffness?=1 (higher=stiffer), sample_spacing?, trim?=true, surface_pull?=1} | " +
                       "edge_surface{curves[2..4]} | " +
                       "blend_surface{a:{target,face?,edge}, b:{target,face?,edge}, continuity_a?=G1, continuity_b?=G1, flip_b?(auto)} (face/edge indices: see analyze_surface edges list; G2 is only approximate on strongly varying edges: verify with analyze_continuity, or use network) | " +
                       "rebuild_surface{target|{target,face}, points_u, points_v, degree_u?=3, degree_v?=3, replace?=false} | " +
                       "curve_blend{a,b (curve specs), continuity?=G1, end_a?=auto|start|end, end_b?} (returns a curve) | " +
                       "revolve_rail{profile, rail, axis:{from,to}, scale_height?}. " +
                       "Check G0/G1/G2 between results with analyze_continuity.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"items\":{\"type\":\"array\",\"maxItems\":50,\"minItems\":1,\"items\":{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"nurbs_points\",\"through_points\",\"loft\",\"sweep2\",\"network\",\"patch\",\"edge_surface\",\"blend_surface\",\"rebuild_surface\",\"curve_blend\",\"revolve_rail\"]},\"plane\":{\"$ref\":\"#/$defs/plane\"},\"layer\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"user_text\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}}},\"required\":[\"kind\"]}}," +
                    "\"plane\":{\"$ref\":\"#/$defs/plane\"}," +
                    "\"force\":{\"type\":\"boolean\",\"default\":false,\"description\":\"override the control-point limits (max_surface_control_points, max_batch_control_points); never overrides block_rss_mb\"}," +
                    "\"layer\":{\"type\":\"string\",\"description\":\"default layer for items without one, e.g. A::B\"}",
                    "\"items\"");
            }
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var top = new P(args, ctx, new Frame(Plane.WorldXY, "world_xy"));
            var itemsTok = top.Tok("items") as JArray;
            if (itemsTok == null) top.Err("'items' is required (array of items)");
            else if (itemsTok.Count == 0 || itemsTok.Count > 50) top.Err("'items' must contain 1..50 entries (got " + itemsTok.Count + ")");
            string defLayer = top.Str("layer");
            Frame defFrame = new Frame(Plane.WorldXY, "world_xy");
            if (top.Has("plane"))
            {
                string perr;
                defFrame = GeoUtil.ParsePlane(top.Tok("plane"), ctx, null, out perr);
                if (defFrame == null) top.Err("'plane': " + perr);
            }
            bool force = top.Bool("force");
            if (top.Errors.Count > 0) return FF.ArgFail(top.Errors);

            var blocked = Resources.CheckRss("freeform_surface");
            if (blocked != null) return blocked;
            var guard = Resources.Begin("freeform_surface");
            var pre = Preflight(itemsTok, force);
            if (pre != null) return pre;

            var doc = ctx.Doc;
            var results = new List<object>();
            var boxes = new List<BoundingBox>();
            int created = 0, failed = 0;
            for (int i = 0; i < itemsTok.Count; i++)
            {
                var o = itemsTok[i] as JObject;
                if (o == null) { failed++; results.Add(new { i = i, ok = false, error = "item must be an object" }); continue; }
                var p = new P(o, ctx, defFrame);
                string kind = p.Str("kind", true);
                string err = null;
                var ids = new List<Guid>();
                var r = new Dictionary<string, object>();
                using (var bag = new Bag())
                {
                    try
                    {
                        if (p.Has("plane"))
                        {
                            string perr;
                            var f = GeoUtil.ParsePlane(p.Tok("plane"), ctx, null, out perr);
                            if (f == null) p.Err("'plane': " + perr); else p.Frame = f;
                        }
                        var attr = GeoUtil.Attributes(p, doc, defLayer);
                        GeometryBase g = null;
                        Guid deleteId = Guid.Empty;
                        if (p.Errors.Count == 0 && kind != null) g = Build(kind, p, ctx, bag, r, out deleteId);
                        if (p.Errors.Count > 0) err = string.Join("; ", p.Errors);
                        else
                        {
                            err = FF.AddAndDescribe(ctx, g, attr, r, ids);
                            if (err == null && deleteId != Guid.Empty) doc.Objects.Delete(deleteId, true);
                        }
                    }
                    catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message + FF.Where(ex); }
                    if (err != null) foreach (var id in ids) { try { doc.Objects.Delete(id, true); } catch (Exception) { } }
                }
                if (err == null)
                {
                    created++;
                    r["i"] = i; r["ok"] = true; r["kind"] = kind;
                    var ro = doc.Objects.FindId(ids[0]);
                    if (ro != null) boxes.Add(ro.Geometry.GetBoundingBox(true));
                    results.Add(r);
                }
                else { failed++; results.Add(new { i = i, ok = false, kind = kind, error = err }); }
            }
            var data = new { created = created, failed = failed, bbox = GeoUtil.BBox(GeoUtil.Union(boxes)), plane = defFrame.Name, items = results };
            if (created == 0)
                return ToolResult.Fail("GEOMETRY_FAILED", "No item could be created (" + failed + " failed)",
                    "See details.items for the reason per item. Check kind names and parameters in the tool description.", new { items = results });
            var res = ToolResult.Ok(data);
            if (failed > 0) res.Warn(failed + " of " + itemsTok.Count + " items failed; see items[].error");
            foreach (var ri in results)
            {
                var rd = ri as Dictionary<string, object>;
                object cu, cv;
                if (rd != null && rd.TryGetValue("cp_u", out cu) && rd.TryGetValue("cp_v", out cv) && cu is int && cv is int && (int)cu * (int)cv > Resources.Limits.MaxSurfaceControlPoints)
                    res.Warn("item " + rd["i"] + " has " + (int)cu + "x" + (int)cv + " = " + ((int)cu * (int)cv) + " control points (limit " + Resources.Limits.MaxSurfaceControlPoints + "). Rebuild it with rebuild_surface to about 20x20 before booleans, offsets or Make2D.");
            }
            return guard.Finish(res);
        }

        // ------------------------------------------------------------------ resource pre-flight

        /// <summary>Estimated control points of one item from its inputs (0 = unknown).</summary>
        static long EstimateCp(string kind, JObject o, out string how)
        {
            how = null;
            Func<string, int> cnt = f => { var g = o[f] as JArray; return g == null ? 0 : g.Count; };
            switch (kind)
            {
                case "nurbs_points":
                case "through_points":
                    {
                        var g = o["points"] as JArray;
                        if (g == null || g.Count < 2) return 0;
                        var first = g[0] as JArray;
                        if (first == null) return 0;
                        long rows = g.Count, cols = first.Count;
                        JToken t;
                        if (kind == "nurbs_points")
                        {
                            int du = 3, dv = 3;
                            if (o.TryGetValue("degree_u", out t) && t.Type == JTokenType.Integer) du = (int)t;
                            if (o.TryGetValue("degree_v", out t) && t.Type == JTokenType.Integer) dv = (int)t;
                            if (o.TryGetValue("closed_u", out t) && t.Type == JTokenType.Boolean && (bool)t) rows += du;
                            if (o.TryGetValue("closed_v", out t) && t.Type == JTokenType.Boolean && (bool)t) cols += dv;
                        }
                        how = rows + "x" + cols + " point grid";
                        return rows * cols;
                    }
                case "rebuild_surface":
                    {
                        JToken u, v;
                        if (o.TryGetValue("points_u", out u) && o.TryGetValue("points_v", out v) && u.Type == JTokenType.Integer && v.Type == JTokenType.Integer)
                        { how = (long)u + "x" + (long)v + " rebuild"; return (long)u * (long)v; }
                        return 0;
                    }
                case "patch":
                    {
                        long us = 10, vs = 10; JToken t;
                        if (o.TryGetValue("u_spans", out t) && t.Type == JTokenType.Integer) us = (long)t;
                        if (o.TryGetValue("v_spans", out t) && t.Type == JTokenType.Integer) vs = (long)t;
                        how = us + "x" + vs + " spans";
                        return (us + 3) * (vs + 3);
                    }
                case "loft":
                    {
                        JToken t; long rb = 0;
                        if (o.TryGetValue("rebuild", out t) && t.Type == JTokenType.Integer) rb = (long)t;
                        if (rb >= 2) { how = cnt("curves") + " curves x rebuild " + rb; return cnt("curves") * rb; }
                        return 0;
                    }
            }
            return 0;
        }

        /// <summary>Fails the whole call (nothing is created) when an item or the batch exceeds the control-point limits.</summary>
        static ToolResult Preflight(JArray items, bool force)
        {
            var lim = Resources.Limits;
            long total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var o = items[i] as JObject;
                if (o == null) continue;
                string how;
                long cp = EstimateCp((string)o["kind"] ?? "", o, out how);
                total += cp;
                if (cp > lim.MaxSurfaceControlPoints && !force)
                {
                    int side = (int)Math.Round(Math.Sqrt(lim.MaxSurfaceControlPoints));
                    return Resources.LimitFail("max_surface_control_points", cp, lim.MaxSurfaceControlPoints,
                        "items[" + i + "] (" + o["kind"] + ", " + how + ") would have ~" + cp + " control points; the limit is " + lim.MaxSurfaceControlPoints + " (" + side + "x" + side + "). Nothing was created.",
                        "Use fewer points: a smooth surface rarely needs more than 20x20 (degree 3). Build with a coarse grid, then refine locally, or split into several patches. For an existing dense surface call rebuild_surface to 20x20.", true);
                }
            }
            if (total > lim.MaxBatchControlPoints && !force)
                return Resources.LimitFail("max_batch_control_points", total, lim.MaxBatchControlPoints,
                    "the batch would create ~" + total + " control points in total; the limit is " + lim.MaxBatchControlPoints + ". Nothing was created.",
                    "Split the batch over several calls with fewer points per surface (about 20x20 each), or lower the point counts.", true);
            return null;
        }

        // ------------------------------------------------------------------ builders

        static Point3d[,] Grid(P p, string field, out int rows, out int cols)
        {
            rows = cols = 0;
            var g = p.Tok(field) as JArray;
            if (g == null || g.Count < 2) { p.Err("'" + field + "' must be a grid: array of rows, each an array of [x,y,z] (>= 2 rows)"); return null; }
            rows = g.Count;
            var first = g[0] as JArray;
            if (first == null || first.Count < 2) { p.Err("'" + field + "' rows must be arrays of >= 2 [x,y,z] points"); return null; }
            cols = first.Count;
            var res = new Point3d[rows, cols];
            for (int i = 0; i < rows; i++)
            {
                var row = g[i] as JArray;
                if (row == null || row.Count != cols) { p.Err("'" + field + "' row " + i + " must have " + cols + " points (all rows equal length)"); return null; }
                for (int j = 0; j < cols; j++)
                {
                    double[] v;
                    if (!P.TryNums(row[j], 2, 3, out v)) { p.Err("'" + field + "'[" + i + "][" + j + "] is not [x,y,z]"); return null; }
                    res[i, j] = p.Frame.Pt(new Point3d(v[0], v[1], v.Length > 2 ? v[2] : 0));
                }
            }
            return res;
        }

        static GeometryBase BrepOf(Surface s, Bag bag)
        {
            if (s == null) return null;
            bag.Add(s);
            var b = Brep.CreateFromSurface(s);
            return bag.Add(b);
        }

        static GeometryBase First(Brep[] bs, Bag bag, Dictionary<string, object> extra)
        {
            if (bs == null || bs.Length == 0) return null;
            foreach (var b in bs) bag.Add(b);
            if (bs.Length > 1) extra["note"] = "operation produced " + bs.Length + " breps; the first was kept";
            return bs[0];
        }

        static GeometryBase Build(string kind, P p, ToolContext ctx, Bag bag, Dictionary<string, object> extra, out Guid deleteId)
        {
            deleteId = Guid.Empty;
            double tol = ctx.Tol;
            switch (kind)
            {
                case "nurbs_points": return NurbsPoints(p, bag, extra);
                case "through_points":
                    {
                        int rows, cols;
                        var g = Grid(p, "points", out rows, out cols);
                        int du = p.Int("degree_u", false, 3), dv = p.Int("degree_v", false, 3);
                        bool cu = p.Bool("closed_u"), cv = p.Bool("closed_v");
                        if (p.Errors.Count > 0) return null;
                        du = Math.Min(du, rows - 1); dv = Math.Min(dv, cols - 1);
                        if (du < 1 || dv < 1) { p.Err("degrees must be >= 1 and < the point count per direction"); return null; }
                        var list = new List<Point3d>();
                        for (int i = 0; i < rows; i++) for (int j = 0; j < cols; j++) list.Add(g[i, j]);
                        var s = NurbsSurface.CreateThroughPoints(list, rows, cols, du, dv, cu, cv);
                        if (s == null) { p.Err("interpolation failed: check that points are distinct and the grid is not degenerate"); return null; }
                        double dev = 0;
                        foreach (var gp in list) { double su, sv; if (s.ClosestPoint(gp, out su, out sv)) dev = Math.Max(dev, gp.DistanceTo(s.PointAt(su, sv))); }
                        extra["max_point_deviation"] = FF.R(dev);
                        return BrepOf(s, bag);
                    }
                case "loft":
                    {
                        var cs = FF.CurveList(p, "curves", 2, 200, bag);
                        string type = p.Str("type", false, "normal");
                        bool closed = p.Bool("closed"), align = p.Bool("align", true), cap = p.Bool("cap");
                        int rebuild = p.Int("rebuild", false, 0); double refit = p.Num("refit", false, 0);
                        LoftType lt = LoftType.Normal;
                        switch (type)
                        {
                            case "normal": lt = LoftType.Normal; break;
                            case "loose": lt = LoftType.Loose; break;
                            case "tight": lt = LoftType.Tight; break;
                            case "straight": lt = LoftType.Straight; break;
                            case "uniform": lt = LoftType.Uniform; break;
                            default: p.Err("'type' must be normal | loose | tight | straight | uniform"); break;
                        }
                        if (p.Errors.Count > 0) return null;
                        if (closed && cs.Count < 3) { p.Err("a closed loft needs >= 3 curves"); return null; }
                        if (align) FF.AlignCurves(cs);
                        Brep[] bs;
                        if (rebuild >= 2) bs = Brep.CreateFromLoftRebuild(cs, Point3d.Unset, Point3d.Unset, lt, closed, rebuild);
                        else if (refit > 0) bs = Brep.CreateFromLoftRefit(cs, Point3d.Unset, Point3d.Unset, lt, closed, refit);
                        else bs = Brep.CreateFromLoft(cs, Point3d.Unset, Point3d.Unset, lt, closed);
                        var b = First(bs, bag, extra) as Brep;
                        if (b == null) { p.Err("loft failed. Curves must be compatible: same open/closed state, consistent direction, no crossings"); return null; }
                        if (cap)
                        {
                            var c = bag.Add(b.CapPlanarHoles(tol));
                            if (c == null) extra["cap_warning"] = "no planar holes could be capped"; else b = c;
                        }
                        return b;
                    }
                case "sweep2":
                    {
                        var rails = FF.CurveList(p, "rails", 2, 2, bag);
                        var profs = FF.CurveList(p, "profiles", 1, 100, bag);
                        bool closed = p.Bool("closed");
                        if (p.Errors.Count > 0) return null;
                        var bs = Brep.CreateFromSweep(rails[0], rails[1], profs, closed, tol);
                        var b = First(bs, bag, extra);
                        if (b == null) p.Err("sweep2 failed. Profiles must touch both rails (within tolerance " + tol + ")");
                        return b;
                    }
                case "network": return Network(p, ctx, bag);
                case "patch": return Patch(p, ctx, bag);
                case "edge_surface":
                    {
                        var cs = FF.CurveList(p, "curves", 2, 4, bag);
                        if (p.Errors.Count > 0) return null;
                        var b = bag.Add(Brep.CreateEdgeSurface(cs));
                        if (b == null) p.Err("edge_surface failed: the curve ends must meet at corners (within tolerance)");
                        return b;
                    }
                case "blend_surface": return BlendSurface(p, ctx, bag);
                case "rebuild_surface":
                    {
                        var rf = FF.ResolveRef(p, p.Tok("target"), "target", true, false, bag);
                        int pu = p.Int("points_u", true), pv = p.Int("points_v", true);
                        int du = p.Int("degree_u", false, 3), dv = p.Int("degree_v", false, 3);
                        bool replace = p.Bool("replace");
                        if (rf == null || p.Errors.Count > 0) return null;
                        if (du < 1 || dv < 1 || pu < du + 1 || pv < dv + 1) { p.Err("need points_u >= degree_u+1 and points_v >= degree_v+1 (degree >= 1)"); return null; }
                        var ns = rf.Face.Rebuild(du, dv, pu, pv);
                        if (ns == null) { p.Err("rebuild failed for this surface"); return null; }
                        if (!rf.Face.IsPlanar(tol) && rf.Brep.Faces.Count == 1 && rf.Brep.Loops.Count > 1) extra["warning"] = "source face was trimmed; the rebuilt surface is untrimmed";
                        if (replace) deleteId = rf.Obj.Id;
                        return BrepOf(ns, bag);
                    }
                case "curve_blend": return CurveBlend(p, bag);
                case "revolve_rail":
                    {
                        var prof = FF.CurveSpec(p, p.Tok("profile"), "profile", bag);
                        var rail = FF.CurveSpec(p, p.Tok("rail"), "rail", bag);
                        var ax = p.Tok("axis") as JObject;
                        Point3d a = Point3d.Unset, b2 = Point3d.Unset;
                        if (ax == null) p.Err("'axis' is required: {from:[x,y,z], to:[x,y,z]}");
                        else { var ap = new P(ax, ctx, p.Frame); a = ap.Pt("from", true); b2 = ap.Pt("to", true); foreach (var e in ap.Errors) p.Err("axis: " + e); }
                        bool sc = p.Bool("scale_height");
                        if (p.Errors.Count > 0) return null;
                        var ns = NurbsSurface.CreateRailRevolvedSurface(prof, rail, new Line(a, b2), sc);
                        if (ns == null) { p.Err("rail revolve failed: the profile must touch the rail"); return null; }
                        return BrepOf(ns, bag);
                    }
            }
            p.Err("unknown kind '" + kind + "'. Use nurbs_points | through_points | loft | sweep2 | network | patch | edge_surface | blend_surface | rebuild_surface | curve_blend | revolve_rail");
            return null;
        }

        static GeometryBase NurbsPoints(P p, Bag bag, Dictionary<string, object> extra)
        {
            int rows, cols;
            var g = Grid(p, "points", out rows, out cols);
            int du = p.Int("degree_u", false, 3), dv = p.Int("degree_v", false, 3);
            bool cu = p.Bool("closed_u"), cv = p.Bool("closed_v");
            double[,] w = null;
            var wt = p.Tok("weights") as JArray;
            if (p.Has("weights") && wt == null) p.Err("'weights' must be a grid of numbers (rows x cols)");
            if (g == null || p.Errors.Count > 0) return null;
            if (wt != null)
            {
                w = new double[rows, cols];
                if (wt.Count != rows) p.Err("'weights' needs " + rows + " rows");
                else for (int i = 0; i < rows; i++)
                    {
                        var row = wt[i] as JArray;
                        if (row == null || row.Count != cols) { p.Err("'weights' row " + i + " needs " + cols + " numbers"); break; }
                        for (int j = 0; j < cols; j++)
                        {
                            if (row[j].Type != JTokenType.Integer && row[j].Type != JTokenType.Float || (double)row[j] <= 0) { p.Err("weights must be numbers > 0"); break; }
                            w[i, j] = (double)row[j];
                        }
                    }
                if (p.Errors.Count > 0) return null;
            }
            int maxU = cu ? rows - 1 : rows - 1, maxV = cv ? cols - 1 : cols - 1;
            if (du > maxU) { extra["degree_u_clamped"] = maxU; du = maxU; }
            if (dv > maxV) { extra["degree_v_clamped"] = maxV; dv = maxV; }
            if (du < 1 || dv < 1) { p.Err("'degree_u'/'degree_v' must be >= 1"); return null; }
            if (cu && rows < 3) { p.Err("closed_u needs >= 3 distinct point rows"); return null; }
            if (cv && cols < 3) { p.Err("closed_v needs >= 3 distinct point columns"); return null; }
            int ncu = cu ? rows + du : rows, ncv = cv ? cols + dv : cols;
            var ns = NurbsSurface.Create(3, w != null, du + 1, dv + 1, ncu, ncv);
            if (ns == null) { p.Err("could not allocate a NURBS surface with these degrees/counts"); return null; }
            bag.Add(ns);
            for (int i = 0; i < ncu; i++)
                for (int j = 0; j < ncv; j++)
                {
                    int gi = i % rows, gj = j % cols;
                    bool ok = w != null ? ns.Points.SetPoint(i, j, g[gi, gj], w[gi, gj]) : ns.Points.SetPoint(i, j, g[gi, gj]);
                    if (!ok) { p.Err("could not set control point [" + i + "," + j + "]"); return null; }
                }
            bool k1 = cu ? ns.KnotsU.CreatePeriodicKnots(1.0) : ns.KnotsU.CreateUniformKnots(1.0);
            bool k2 = cv ? ns.KnotsV.CreatePeriodicKnots(1.0) : ns.KnotsV.CreateUniformKnots(1.0);
            if (!k1 || !k2) { p.Err("could not create knot vectors"); return null; }
            var b = Brep.CreateFromSurface(ns);
            bag.Add(b);
            return b;
        }

        static int ContCode(P p, string field, string def)
        {
            string cont = p.Str(field, false, def);
            switch (cont) { case "loose": return 0; case "position": return 1; case "tangent": return 2; case "curvature": return 3; }
            p.Err("'" + field + "' must be loose | position | tangent | curvature");
            return 1;
        }

        static GeometryBase Network(P p, ToolContext ctx, Bag bag)
        {
            int cu = ContCode(p, "continuity", "position"), cv = ContCode(p, "continuity_v", "position");
            double edgeTol = p.Num("edge_tol", false, ctx.Tol), intTol = p.Num("interior_tol", false, ctx.Tol);
            double angTol = p.Num("angle_tol_deg", false, ctx.AngleTolDeg) * Math.PI / 180.0;
            List<Curve> all = null, us = null, vs = null;
            if (p.Has("u_curves") || p.Has("v_curves"))
            {
                us = FF.CurveList(p, "u_curves", 2, 100, bag, true);
                vs = FF.CurveList(p, "v_curves", 2, 100, bag, true);
            }
            else all = FF.CurveList(p, "curves", 4, 200, bag, true);
            if (p.Errors.Count > 0) return null;
            int err;
            NurbsSurface ns = all != null
                ? NurbsSurface.CreateNetworkSurface(all, cu, edgeTol, intTol, angTol, out err)
                : NurbsSurface.CreateNetworkSurface(us, cu, cu, vs, cv, cv, edgeTol, intTol, angTol, out err);
            if (ns == null)
            {
                string[] msg = { "success", "curve sorter failed (curves do not form a u/v grid, or do not meet)", "network initialisation failed", "surface build failed", "result network surface invalid" };
                p.Err("network surface failed (code " + err + ": " + (err >= 0 && err < msg.Length ? msg[err] : "?") + "). Curves must intersect each other within interior_tol (" + intTol + ") and form a grid; raise edge_tol/interior_tol or build the curves from points on one common surface");
                return null;
            }
            return BrepOf(ns, bag);
        }

        static GeometryBase Patch(P p, ToolContext ctx, Bag bag)
        {
            var arr = p.Tok("inputs") as JArray;
            if (arr == null || arr.Count == 0) { p.Err("'inputs' is required: array of curve specs and/or [x,y,z] points"); return null; }
            var geo = new List<GeometryBase>();
            for (int i = 0; i < arr.Count; i++)
            {
                if (FF.IsPointArr(arr[i]))
                {
                    var pts = FF.PointList(p, new JArray(arr[i]), "inputs[" + i + "]", 1);
                    if (pts.Count == 1) geo.Add(bag.Add(new Rhino.Geometry.Point(pts[0])));
                }
                else
                {
                    var c = FF.CurveSpec(p, arr[i], "inputs[" + i + "]", bag);
                    if (c != null) geo.Add(c);
                }
            }
            int us = p.Int("u_spans", false, 10), vs = p.Int("v_spans", false, 10);
            double stiff = p.Num("stiffness", false, 1.0);
            double pull = p.Num("surface_pull", false, 1.0);
            bool trim = p.Bool("trim", true);
            double spacing = p.Num("sample_spacing", false, double.NaN);
            if (p.Errors.Count > 0) return null;
            if (us < 1 || vs < 1 || !(stiff > 0)) { p.Err("u_spans/v_spans must be >= 1 and stiffness > 0"); return null; }
            var bb = BoundingBox.Empty;
            foreach (var g in geo) bb.Union(g.GetBoundingBox(true));
            if (!FF.Finite(spacing)) spacing = Math.Max(bb.Diagonal.Length / 40.0, ctx.Tol * 10);
            var b = Brep.CreatePatch(geo, null, us, vs, trim, false, spacing, 1.0 / stiff, pull, new[] { false, false, false, false }, ctx.Tol);
            bag.Add(b);
            if (b == null) p.Err("patch failed. Provide closed boundary curves (for trim) and/or more points; try more spans or a smaller sample_spacing");
            return b;
        }

        static GeometryBase BlendSurface(P p, ToolContext ctx, Bag bag)
        {
            var a = FF.ResolveRef(p, p.Tok("a"), "a", true, true, bag);
            var b = FF.ResolveRef(p, p.Tok("b"), "b", true, true, bag);
            var ca = FF.ParseBlend(p, "continuity_a", "G1");
            var cb = FF.ParseBlend(p, "continuity_b", "G1");
            bool hasFlip = p.Has("flip_b"); bool flipB = p.Bool("flip_b"); bool flipA = p.Bool("flip_a");
            if (a != null && a.Edge == null) p.Err("'a' needs an edge index: {target, face?, edge:n}");
            if (b != null && b.Edge == null) p.Err("'b' needs an edge index: {target, face?, edge:n}");
            if (p.Errors.Count > 0) return null;
            if (!hasFlip)
            {
                var sa = flipA ? a.Edge.PointAtEnd : a.Edge.PointAtStart;
                flipB = sa.DistanceTo(b.Edge.PointAtStart) > sa.DistanceTo(b.Edge.PointAtEnd);
            }
            var bs = Brep.CreateBlendSurface(a.Face, a.Edge, a.Edge.Domain, flipA, ca, b.Face, b.Edge, b.Edge.Domain, flipB, cb);
            var res = First(bs, bag, new Dictionary<string, object>());
            if (res == null) p.Err("blend failed. Check the edge indices (analyze_surface lists edges), try flip_b, or lower the continuity");
            return res;
        }

        static GeometryBase CurveBlend(P p, Bag bag)
        {
            var a = FF.CurveSpec(p, p.Tok("a"), "a", bag);
            var b = FF.CurveSpec(p, p.Tok("b"), "b", bag);
            var cont = FF.ParseBlend(p, "continuity", "G1");
            string ea = p.Str("end_a", false, "auto"), eb = p.Str("end_b", false, "auto");
            if (p.Errors.Count > 0) return null;
            // choose which ends meet: nearest pair unless forced
            bool aStart = false, bStart = true;
            double best = double.MaxValue;
            foreach (var sa in new[] { false, true })
                foreach (var sb in new[] { false, true })
                {
                    if (ea == "start" && !sa || ea == "end" && sa) continue;
                    if (eb == "start" && !sb || eb == "end" && sb) continue;
                    double d = (sa ? a.PointAtStart : a.PointAtEnd).DistanceTo(sb ? b.PointAtStart : b.PointAtEnd);
                    if (d < best) { best = d; aStart = sa; bStart = sb; }
                }
            if (best == double.MaxValue) { p.Err("end_a/end_b must be auto | start | end"); return null; }
            var c = Curve.CreateBlendCurve(a, aStart ? a.Domain.Min : a.Domain.Max, aStart, cont, b, bStart ? b.Domain.Min : b.Domain.Max, bStart, cont);
            if (c == null) { p.Err("curve blend failed (curves too close, overlapping or incompatible ends)"); return null; }
            return bag.Add(c);
        }
    }
}

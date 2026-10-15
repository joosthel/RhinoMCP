using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Geometry
{
    public sealed class GeometryCreate : ITool
    {
        public string Name { get { return "geometry_create"; } }
        public ToolKind Kind { get { return ToolKind.Mutate; } }
        public string Description
        {
            get
            {
                return "Create geometry in batch (items <= 200). Each item: {kind, ...params, layer?, name?, user_text?{k:v}}. " +
                       "All points/vectors are in the 'plane' frame (default world_xy; item.plane overrides); the server converts to world and echoes the world bbox. " +
                       "Missing layers (incl. parent chain 'A::B::C') are created. One bad item does not stop the others; each result has ok/handle(s)/bbox and length/area/volume. " +
                       "Kinds and params: point{point} | line{from,to} | polyline{points,closed?} | curve{points,degree?=3,mode?=interpolate|control_points,closed?} | " +
                       "arc{start,through,end} or {center,radius,start_angle_deg?=0,end_angle_deg?=90} | circle{center,radius|diameter} | rectangle{corner,width,height | corner,corner2} | " +
                       "polygon{center,radius,sides,circumscribed?,rotation_deg?} | box{corner,size[dx,dy,dz] | min,max} | sphere{center,radius} | cylinder{base,radius,height,cap?=true} | " +
                       "cone{base,radius,height,cap?=true} (base circle at 'base', apex above) | plane_surface{corner,width,height | corner,corner2} | " +
                       "extrusion{profile(handle) | points, height | vector, cap?=true} | loft{curves[handles],loft_type?=normal|loose|tight|straight|uniform,closed?,cap?} | " +
                       "sweep1{rail,profiles[],closed?,cap?} | revolve{profile(handle)|points, axis:'x'|'y'|'z'|{from,to}, angle_deg?=360, cap?} | " +
                       "pipe{curve,radius,cap?=flat|round|none} | text{text,at?,height?=1,as?=text|dot,font?}. " +
                       "Extrusion with 'points' extrudes along the frame Z by 'height' (negative = down); closed profiles are capped. " +
                       "Fails per item with a reason (e.g. non-planar profile, invalid result).";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"items\":{\"type\":\"array\",\"maxItems\":200,\"minItems\":1,\"items\":{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"point\",\"line\",\"polyline\",\"curve\",\"arc\",\"circle\",\"rectangle\",\"polygon\",\"box\",\"sphere\",\"cylinder\",\"cone\",\"plane_surface\",\"extrusion\",\"loft\",\"sweep1\",\"revolve\",\"pipe\",\"text\"]},\"plane\":{\"$ref\":\"#/$defs/plane\"},\"layer\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"user_text\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}}},\"required\":[\"kind\"]}}," +
                    "\"plane\":{\"$ref\":\"#/$defs/plane\"}," +
                    "\"layer\":{\"type\":\"string\",\"description\":\"default layer for items without one, e.g. A::B\"}," +
                    "\"include_guids\":{\"type\":\"boolean\"}",
                    "\"items\"");
            }
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var top = new P(args, ctx, new Frame(Plane.WorldXY, "world_xy"));
            var itemsTok = top.Tok("items") as JArray;
            if (itemsTok == null) top.Err("'items' is required (array of items)");
            else if (itemsTok.Count == 0 || itemsTok.Count > 200) top.Err("'items' must contain 1..200 entries (got " + itemsTok.Count + ")");
            string defLayer = top.Str("layer");
            bool guids = top.Bool("include_guids");
            Frame defFrame = null;
            if (top.Has("plane"))
            {
                string perr;
                defFrame = GeoUtil.ParsePlane(top.Tok("plane"), ctx, null, out perr);
                if (defFrame == null) top.Err("'plane': " + perr);
            }
            else defFrame = new Frame(Plane.WorldXY, "world_xy");
            if (top.Errors.Count > 0) return ToolResult.Fail("INVALID_ARGS", string.Join("; ", top.Errors), "Fix the arguments and call again.", new { errors = top.Errors });

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
                try
                {
                    if (p.Has("plane"))
                    {
                        string perr;
                        var f = GeoUtil.ParsePlane(p.Tok("plane"), ctx, null, out perr);
                        if (f == null) p.Err("'plane': " + perr); else p.Frame = f;
                    }
                    var attr = GeoUtil.Attributes(p, doc, defLayer);
                    List<GeometryBase> geos = null;
                    var directIds = new List<Guid>();
                    if (p.Errors.Count == 0 && kind != null)
                    {
                        if (kind == "text") TextItem(p, doc, ctx, attr, directIds);
                        else geos = Build(kind, p, ctx);
                    }
                    if (p.Errors.Count > 0) err = string.Join("; ", p.Errors);
                    else
                    {
                        var ids = new List<Guid>(directIds);
                        var bb = BoundingBox.Empty;
                        var extra = new Dictionary<string, object>();
                        if (geos != null)
                        {
                            if (geos.Count == 0 || geos.All(g => g == null)) err = "'" + kind + "' produced no geometry. Check the inputs (planarity, closed profiles, distinct points).";
                            foreach (var g in geos)
                            {
                                if (err != null) break;
                                if (g == null) continue;
                                string log;
                                if (!g.IsValidWithLog(out log)) { err = "'" + kind + "' produced invalid geometry: " + (GeoUtil.FirstLines(log, 2, 200) ?? "no log"); break; }
                                Guid id = GeoUtil.AddGeometry(doc, g, attr);
                                if (id == Guid.Empty) { err = "Rhino rejected the " + kind + " geometry"; break; }
                                ids.Add(id);
                                var gb = g.GetBoundingBox(true);
                                bb.Union(gb);
                                if (geos.Count == 1) foreach (var kv in GeoUtil.Measure(g)) extra[kv.Key] = kv.Value;
                            }
                            if (err != null) foreach (var id in ids) doc.Objects.Delete(id, true);
                        }
                        else
                        {
                            foreach (var id in ids) { var ro = doc.Objects.FindId(id); if (ro != null) bb.Union(ro.Geometry.GetBoundingBox(true)); }
                            if (ids.Count == 1) { var ro = doc.Objects.FindId(ids[0]); if (ro != null) foreach (var kv in GeoUtil.Measure(ro.Geometry)) extra[kv.Key] = kv.Value; }
                        }
                        if (err == null)
                        {
                            created += ids.Count;
                            boxes.Add(bb);
                            var r = new Dictionary<string, object>();
                            r["i"] = i; r["ok"] = true; r["kind"] = kind;
                            if (ids.Count == 1) r["handle"] = ctx.Handles.For(ids[0]); else r["handles"] = ids.Select(x => ctx.Handles.For(x)).ToList();
                            if (guids) r["guids"] = ids.Select(x => x.ToString()).ToList();
                            r["bbox"] = GeoUtil.BBox(bb);
                            foreach (var kv in extra) r[kv.Key] = kv.Value;
                            results.Add(r);
                            continue;
                        }
                    }
                }
                catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
                failed++;
                results.Add(new { i = i, ok = false, kind = kind, error = err });
            }

            var all = GeoUtil.Union(boxes);
            var data = new { created = created, failed = failed, bbox = GeoUtil.BBox(all), plane = defFrame.Name, items = results };
            if (created == 0)
                return ToolResult.Fail("GEOMETRY_FAILED", "No item could be created (" + failed + " failed)",
                    "See details.items for the reason per item. Check kind names and parameters in the tool description.", new { items = results });
            var res = ToolResult.Ok(data);
            if (failed > 0) res.Warn(failed + " of " + itemsTok.Count + " items failed; see items[].error");
            return res;
        }

        static void TextItem(P p, RhinoDoc doc, ToolContext ctx, Rhino.DocObjects.ObjectAttributes attr, List<Guid> ids)
        {
            string text = p.Str("text", true);
            Point3d at = p.Has("at") ? p.Pt("at") : p.Frame.Pt(Point3d.Origin);
            double h = p.Num("height", false, 1.0);
            string how = p.Str("as", false, "text");
            string font = p.Str("font", false, "Arial");
            if (p.Errors.Count > 0) return;
            if (h <= 0) { p.Err("'height' must be > 0"); return; }
            if (how == "dot")
            {
                var id = doc.Objects.AddTextDot(new TextDot(text, at), attr);
                if (id == Guid.Empty) p.Err("could not add text dot"); else ids.Add(id);
            }
            else if (how == "text")
            {
                var pl = p.Frame.Plane; pl.Origin = at;
                var id = doc.Objects.AddText(text, pl, h, font, false, false, attr);
                if (id == Guid.Empty) p.Err("could not add text (empty string?)"); else ids.Add(id);
            }
            else p.Err("'as' must be 'text' or 'dot'");
        }

        static List<GeometryBase> One(GeometryBase g) { return new List<GeometryBase> { g }; }
        static List<GeometryBase> Many(IEnumerable<GeometryBase> g) { return g == null ? new List<GeometryBase>() : g.ToList(); }

        static Plane At(P p, Point3d origin) { var pl = p.Frame.Plane; pl.Origin = origin; return pl; }

        static Brep Cap(Brep b, double tol)
        {
            if (b == null) return null;
            var c = b.CapPlanarHoles(tol);
            return c ?? b;
        }

        static List<GeometryBase> Build(string kind, P p, ToolContext ctx)
        {
            double tol = ctx.Tol;
            switch (kind)
            {
                case "point":
                    {
                        var pt = p.Pt("point", true);
                        return p.Errors.Count > 0 ? null : One(new Point(pt));
                    }
                case "line":
                    {
                        var a = p.Pt("from", true); var b = p.Pt("to", true);
                        if (p.Errors.Count > 0) return null;
                        if (a.DistanceTo(b) <= tol) { p.Err("'from' and 'to' coincide (distance <= tolerance " + tol + ")"); return null; }
                        return One(new LineCurve(a, b));
                    }
                case "polyline":
                    {
                        var pts = p.Pts("points", 2);
                        bool closed = p.Bool("closed");
                        if (p.Errors.Count > 0) return null;
                        if (closed && pts[0].DistanceTo(pts[pts.Count - 1]) > tol) pts.Add(pts[0]);
                        if (closed && pts.Count < 4) { p.Err("closed polyline needs at least 3 distinct points"); return null; }
                        return One(new PolylineCurve(pts));
                    }
                case "curve":
                    {
                        var pts = p.Pts("points", 2);
                        int degree = p.Int("degree", false, 3);
                        string mode = p.Str("mode", false, "interpolate");
                        bool closed = p.Bool("closed");
                        if (p.Errors.Count > 0) return null;
                        if (degree < 1 || degree > 11) { p.Err("'degree' must be 1..11"); return null; }
                        Curve c;
                        if (mode == "interpolate")
                        {
                            if (closed)
                            {
                                if (pts[0].DistanceTo(pts[pts.Count - 1]) > tol) pts.Add(pts[0]);
                                c = Curve.CreateInterpolatedCurve(pts, degree, CurveKnotStyle.ChordPeriodic);
                            }
                            else c = Curve.CreateInterpolatedCurve(pts, degree);
                        }
                        else if (mode == "control_points")
                        {
                            if (closed) { p.Err("'closed' is only supported with mode 'interpolate'; repeat the first control point instead"); return null; }
                            if (pts.Count < degree + 1) { p.Err("degree " + degree + " needs at least " + (degree + 1) + " control points"); return null; }
                            c = Curve.CreateControlPointCurve(pts, degree);
                        }
                        else { p.Err("'mode' must be 'interpolate' or 'control_points'"); return null; }
                        return One(c);
                    }
                case "arc":
                    {
                        if (p.Has("through"))
                        {
                            var s = p.Pt("start", true); var t = p.Pt("through", true); var e = p.Pt("end", true);
                            if (p.Errors.Count > 0) return null;
                            var arc = new Arc(s, t, e);
                            if (!arc.IsValid) { p.Err("start/through/end are collinear or coincident"); return null; }
                            return One(new ArcCurve(arc));
                        }
                        var c = p.Pt("center", true); double r = p.Num("radius", true);
                        double a0 = p.Num("start_angle_deg", false, 0), a1 = p.Num("end_angle_deg", false, 90);
                        if (p.Errors.Count > 0) return null;
                        if (r <= 0) { p.Err("'radius' must be > 0"); return null; }
                        double sweep = (a1 - a0) % 360.0;
                        if (sweep <= 0) sweep += 360.0;
                        var pl = At(p, c); pl.Rotate(RhinoMath.ToRadians(a0), pl.ZAxis);
                        return One(new ArcCurve(new Arc(pl, r, RhinoMath.ToRadians(sweep))));
                    }
                case "circle":
                    {
                        var c = p.Pt("center", true);
                        double r = p.Has("diameter") ? p.Num("diameter") / 2.0 : p.Num("radius", true);
                        if (p.Errors.Count > 0) return null;
                        if (r <= 0) { p.Err("radius/diameter must be > 0"); return null; }
                        return One(new ArcCurve(new Circle(At(p, c), r)));
                    }
                case "rectangle":
                    {
                        var c = p.LocalPt("corner", true);
                        var f = p.Frame;
                        double w, h;
                        if (p.Has("corner2"))
                        {
                            var c2 = p.LocalPt("corner2", true);
                            if (p.Errors.Count > 0) return null;
                            w = c2.X - c.X; h = c2.Y - c.Y;
                        }
                        else { w = p.Num("width", true); h = p.Num("height", true); }
                        if (p.Errors.Count > 0) return null;
                        if (Math.Abs(w) <= tol || Math.Abs(h) <= tol) { p.Err("width/height must be larger than tolerance " + tol); return null; }
                        var pts = new List<Point3d> {
                            f.Pt(c), f.Pt(new Point3d(c.X + w, c.Y, c.Z)), f.Pt(new Point3d(c.X + w, c.Y + h, c.Z)), f.Pt(new Point3d(c.X, c.Y + h, c.Z)), f.Pt(c) };
                        return One(new PolylineCurve(pts));
                    }
                case "polygon":
                    {
                        var c = p.Pt("center", true); double r = p.Num("radius", true); int n = p.Int("sides", true);
                        bool circ = p.Bool("circumscribed"); double rot = p.Num("rotation_deg", false, 0);
                        if (p.Errors.Count > 0) return null;
                        if (n < 3 || n > 1000) { p.Err("'sides' must be 3..1000"); return null; }
                        if (r <= 0) { p.Err("'radius' must be > 0"); return null; }
                        var pl = At(p, c); pl.Rotate(RhinoMath.ToRadians(rot), pl.ZAxis);
                        var circle = new Circle(pl, r);
                        var pl2 = circ ? Polyline.CreateCircumscribedPolygon(circle, n) : Polyline.CreateInscribedPolygon(circle, n);
                        return One(new PolylineCurve(pl2));
                    }
                case "box":
                    {
                        Point3d lo, hi;
                        var f = p.Frame;
                        if (p.Has("size"))
                        {
                            lo = p.LocalPt("corner", true);
                            var sz = p.LocalPt("size", true);
                            if (p.Errors.Count > 0) return null;
                            hi = new Point3d(lo.X + sz.X, lo.Y + sz.Y, lo.Z + sz.Z);
                        }
                        else
                        {
                            lo = p.LocalPt("min", true); hi = p.LocalPt("max", true);
                            if (p.Errors.Count > 0) return null;
                        }
                        var min = new Point3d(Math.Min(lo.X, hi.X), Math.Min(lo.Y, hi.Y), Math.Min(lo.Z, hi.Z));
                        var max = new Point3d(Math.Max(lo.X, hi.X), Math.Max(lo.Y, hi.Y), Math.Max(lo.Z, hi.Z));
                        if (max.X - min.X <= tol || max.Y - min.Y <= tol || max.Z - min.Z <= tol)
                        { p.Err("box size must be larger than tolerance " + tol + " in all three directions"); return null; }
                        var plane = f.Plane; plane.Origin = f.Pt(Point3d.Origin);
                        var box = new Box(plane, new Interval(min.X, max.X), new Interval(min.Y, max.Y), new Interval(min.Z, max.Z));
                        return One(Brep.CreateFromBox(box));
                    }
                case "sphere":
                    {
                        var c = p.Pt("center", true); double r = p.Num("radius", true);
                        if (p.Errors.Count > 0) return null;
                        if (r <= 0) { p.Err("'radius' must be > 0"); return null; }
                        return One(Brep.CreateFromSphere(new Sphere(At(p, c), r)));
                    }
                case "cylinder":
                    {
                        var b = p.Pt("base", true); double r = p.Num("radius", true), h = p.Num("height", true);
                        bool cap = p.Bool("cap", true);
                        if (p.Errors.Count > 0) return null;
                        if (r <= 0 || Math.Abs(h) <= tol) { p.Err("'radius' must be > 0 and 'height' non-zero"); return null; }
                        return One(Brep.CreateFromCylinder(new Cylinder(new Circle(At(p, b), r), h), cap, cap));
                    }
                case "cone":
                    {
                        var b = p.Pt("base", true); double r = p.Num("radius", true), h = p.Num("height", true);
                        bool cap = p.Bool("cap", true);
                        if (p.Errors.Count > 0) return null;
                        if (r <= 0 || h <= tol) { p.Err("'radius' must be > 0 and 'height' > 0"); return null; }
                        var cone = new Cone(At(p, b), h, r);
                        return One(Brep.CreateFromCone(cone, cap));
                    }
                case "plane_surface":
                    {
                        var c = p.LocalPt("corner", true);
                        double w, h;
                        if (p.Has("corner2")) { var c2 = p.LocalPt("corner2", true); if (p.Errors.Count > 0) return null; w = c2.X - c.X; h = c2.Y - c.Y; }
                        else { w = p.Num("width", true); h = p.Num("height", true); }
                        if (p.Errors.Count > 0) return null;
                        if (Math.Abs(w) <= tol || Math.Abs(h) <= tol) { p.Err("width/height must be larger than tolerance " + tol); return null; }
                        var plane = At(p, p.Frame.Pt(c));
                        var ps = new PlaneSurface(plane, new Interval(Math.Min(0, w), Math.Max(0, w)), new Interval(Math.Min(0, h), Math.Max(0, h)));
                        return One(Brep.CreateFromSurface(ps));
                    }
                case "extrusion": return BuildExtrusion(p, ctx);
                case "loft":
                    {
                        var curves = p.Curves("curves");
                        string lt = p.Str("loft_type", false, "normal");
                        bool closed = p.Bool("closed"), cap = p.Bool("cap");
                        if (p.Errors.Count > 0) return null;
                        if (curves.Count < 2) { p.Err("'curves' needs at least 2 curve handles"); return null; }
                        LoftType t;
                        if (!Enum.TryParse<LoftType>(lt, true, out t)) { p.Err("'loft_type' must be one of normal, loose, tight, straight, uniform, developable"); return null; }
                        var res = Brep.CreateFromLoft(curves, Point3d.Unset, Point3d.Unset, t, closed);
                        if (res == null || res.Length == 0) return null;
                        return Many(res.Select(b => cap ? (GeometryBase)Cap(b, tol) : b));
                    }
                case "sweep1":
                    {
                        var rails = p.Curves("rail"); var profiles = p.Curves("profiles");
                        bool closed = p.Bool("closed"), cap = p.Bool("cap");
                        if (p.Errors.Count > 0) return null;
                        if (rails.Count != 1) { p.Err("'rail' must be exactly one curve"); return null; }
                        if (profiles.Count < 1) { p.Err("'profiles' needs at least one curve"); return null; }
                        var res = Brep.CreateFromSweep(rails[0], profiles, closed, tol);
                        if (res == null || res.Length == 0) return null;
                        return Many(res.Select(b => cap ? (GeometryBase)Cap(b, tol) : b));
                    }
                case "revolve": return BuildRevolve(p, ctx);
                case "pipe":
                    {
                        var cs = p.Curves("curve"); double r = p.Num("radius", true);
                        string cap = p.Str("cap", false, "flat");
                        if (p.Errors.Count > 0) return null;
                        if (cs.Count != 1) { p.Err("'curve' must be exactly one curve"); return null; }
                        if (r <= 0) { p.Err("'radius' must be > 0"); return null; }
                        PipeCapMode m;
                        if (!Enum.TryParse<PipeCapMode>(cap, true, out m)) { p.Err("'cap' must be none, flat or round"); return null; }
                        var res = Brep.CreatePipe(cs[0], r, false, m, true, tol, RhinoMath.ToRadians(ctx.AngleTolDeg));
                        return res == null ? null : Many(res);
                    }
                default:
                    p.Err("unknown kind '" + kind + "'. Valid: point, line, polyline, curve, arc, circle, rectangle, polygon, box, sphere, cylinder, cone, plane_surface, extrusion, loft, sweep1, revolve, pipe, text");
                    return null;
            }
        }

        /// <summary>Profile curve from a handle or from 'points' (closed by default if closeDefault).</summary>
        static Curve Profile(P p, ToolContext ctx, bool closeDefault, out bool fromPoints)
        {
            fromPoints = false;
            if (p.Has("profile"))
            {
                var cs = p.Curves("profile");
                if (p.Errors.Count > 0) return null;
                if (cs.Count == 1) return cs[0];
                var j = Curve.JoinCurves(cs, ctx.Tol);
                if (j == null || j.Length != 1) { p.Err("'profile' curves do not join into one curve"); return null; }
                return j[0];
            }
            if (p.Has("points"))
            {
                var pts = p.Pts("points", 2);
                bool closed = p.Bool("closed", closeDefault);
                if (p.Errors.Count > 0) return null;
                if (closed && pts[0].DistanceTo(pts[pts.Count - 1]) > ctx.Tol) pts.Add(pts[0]);
                fromPoints = true;
                return new PolylineCurve(pts);
            }
            p.Err("give either 'profile' (curve handle) or 'points'");
            return null;
        }

        static List<GeometryBase> BuildExtrusion(P p, ToolContext ctx)
        {
            double tol = ctx.Tol;
            bool fromPts;
            var curve = Profile(p, ctx, true, out fromPts);
            bool cap = p.Bool("cap", true);
            double height = p.Num("height");
            bool hasVec = p.Has("vector");
            var vec = p.Vec("vector");
            if (p.Errors.Count > 0) return null;
            if (curve == null) return null;
            if (!hasVec && double.IsNaN(height)) { p.Err("give 'height' or 'vector'"); return null; }
            bool capOk = cap && curve.IsClosed;
            if (cap && !curve.IsClosed) { /* open profile: surface only */ }
            if (hasVec)
            {
                if (vec.Length <= tol) { p.Err("'vector' has zero length"); return null; }
                var srf = Surface.CreateExtrusion(curve, vec);
                if (srf == null) return null;
                var b = Brep.CreateFromSurface(srf);
                return One(capOk ? Cap(b, tol) : b);
            }
            if (Math.Abs(height) <= tol) { p.Err("'height' must be non-zero"); return null; }
            Plane pl;
            if (!curve.TryGetPlane(out pl, tol)) { p.Err("profile is not planar; give a 'vector' to extrude a non-planar curve"); return null; }
            double h = height;
            if (fromPts && pl.ZAxis * p.Frame.Plane.ZAxis < 0) h = -h;
            var ext = Extrusion.Create(curve, h, capOk);
            if (ext == null) return null;
            if (fromPts)
            {
                // Make sure it went the intended way (frame Z * sign(height)).
                var dir = p.Frame.Plane.ZAxis * Math.Sign(height);
                var c0 = curve.GetBoundingBox(true).Center;
                var c1 = ext.GetBoundingBox(true).Center;
                if ((c1 - c0) * dir < 0)
                {
                    var ext2 = Extrusion.Create(curve, -h, capOk);
                    if (ext2 != null) ext = ext2;
                }
            }
            return One(ext);
        }

        static List<GeometryBase> BuildRevolve(P p, ToolContext ctx)
        {
            double tol = ctx.Tol;
            bool fromPts;
            var curve = Profile(p, ctx, false, out fromPts);
            double ang = p.Num("angle_deg", false, 360);
            bool cap = p.Bool("cap");
            Line axis = Line.Unset;
            var at = p.Tok("axis");
            if (at == null) p.Err("'axis' is required: 'x' | 'y' | 'z' (through the frame origin) or {from:[..], to:[..]}");
            else if (at.Type == JTokenType.String)
            {
                string s = ((string)at).ToLowerInvariant();
                var o = p.Frame.Pt(Point3d.Origin);
                Vector3d d = s == "x" ? p.Frame.Plane.XAxis : s == "y" ? p.Frame.Plane.YAxis : s == "z" ? p.Frame.Plane.ZAxis : Vector3d.Zero;
                if (d.IsZero) p.Err("'axis' string must be x, y or z"); else axis = new Line(o, o + d * 1.0);
            }
            else if (at is JObject)
            {
                var ap = new P((JObject)at, ctx, p.Frame);
                var a = ap.Pt("from", true); var b = ap.Pt("to", true);
                foreach (var e in ap.Errors) p.Err("axis." + e);
                if (ap.Errors.Count == 0) axis = new Line(a, b);
            }
            else p.Err("'axis' must be a string or {from,to}");
            if (p.Errors.Count > 0 || curve == null) return null;
            if (!axis.IsValid || axis.Length <= tol) { p.Err("axis is degenerate (from == to)"); return null; }
            if (ang <= 0 || ang > 360) { p.Err("'angle_deg' must be in (0, 360]"); return null; }
            var rev = RevSurface.Create(curve, axis, 0, RhinoMath.ToRadians(ang));
            if (rev == null) return null;
            var brep = Brep.CreateFromRevSurface(rev, false, false);
            if (brep == null) return null;
            if (cap) brep = Cap(brep, tol);
            return One(brep);
        }
    }
}

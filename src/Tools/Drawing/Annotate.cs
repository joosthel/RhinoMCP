using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Drawing
{
    public sealed class Annotate : ITool
    {
        public string Name { get { return "annotate"; } }
        public string Description
        {
            get
            {
                return "Batch annotation: text, linear/aligned/angular/radial dimensions, leaders and hatches, sized for the drawing scale. " +
                       "scale '1:100' (required for model space): printed sizes are multiplied by the denominator (text-dims.md: 3.5 mm printed = 350 model mm at 1:100; the style stores 3.5 with DimensionScale 100, so it also prints at 3.5 mm inside a 1:100 detail); a dimension style 'MCP 1:100 t3.5 <arrows>' is found or created. " +
                       "layout: place in that layout's page space instead (coordinates are paper mm, sizes are printed, scale ignored). text_height_mm default 3.5, arrows 'arrow' (default) | 'tick'. " +
                       "plane: world_xy (default) | world_yz | world_zx | named | {origin,x_axis,y_axis}; points in items are [x,y] on that plane. layer default 'Annotation'. English text only. " +
                       "items[].type: text {text, point, height_mm | height, justify left|center|right, valign top|middle|bottom, rotation_deg}; linear {from, to, offset_mm | offset, axis x|y}; aligned {from, to, offset_mm | offset}; " +
                       "angular {center, from, to, radius_mm}; radial {curve: arc/circle handle, kind radius|diameter, angle_deg}; leader {points:[tip,...], text}; " +
                       "hatch {curves: closed curve target, pattern Solid|Hatch1.., spacing_mm = printed line spacing (default 2; converted to Rhino's pattern scale, measured for Hatch1-3 in mm documents), scale = raw pattern scale (overrides spacing_mm), rotation_deg}. " +
                       "Each item gets its own result (handle, measured value for dimensions); a failed item does not stop the batch.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"scale\":{\"description\":\"'1:100' or denominator; required for model space\",\"oneOf\":[{\"type\":\"string\"},{\"type\":\"number\"}]}," +
                    "\"layout\":{\"type\":\"string\",\"description\":\"Layout name: annotate in paper space\"}," +
                    "\"text_height_mm\":{\"type\":\"number\",\"minimum\":0.5,\"default\":3.5}," +
                    "\"arrows\":{\"enum\":[\"arrow\",\"tick\"],\"default\":\"arrow\"}," +
                    "\"layer\":{\"type\":\"string\",\"default\":\"Annotation\"}," +
                    "\"plane\":{\"$ref\":\"#/$defs/plane\"}," +
                    "\"items\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":200,\"items\":{\"type\":\"object\",\"properties\":{" +
                    "\"type\":{\"enum\":[\"text\",\"linear\",\"aligned\",\"angular\",\"radial\",\"leader\",\"hatch\"]}," +
                    "\"text\":{\"type\":\"string\"},\"point\":{\"$ref\":\"#/$defs/point\"},\"from\":{\"$ref\":\"#/$defs/point\"},\"to\":{\"$ref\":\"#/$defs/point\"},\"center\":{\"$ref\":\"#/$defs/point\"}," +
                    "\"points\":{\"type\":\"array\",\"items\":{\"$ref\":\"#/$defs/point\"}}," +
                    "\"height_mm\":{\"type\":\"number\"},\"height\":{\"type\":\"number\",\"description\":\"text height in model units (overrides height_mm)\"}," +
                    "\"justify\":{\"enum\":[\"left\",\"center\",\"right\"]},\"valign\":{\"enum\":[\"top\",\"middle\",\"bottom\"]},\"rotation_deg\":{\"type\":\"number\"}," +
                    "\"offset_mm\":{\"type\":\"number\",\"description\":\"dimension line offset in printed mm (default 8)\"},\"offset\":{\"type\":\"number\",\"description\":\"offset in model units (overrides offset_mm)\"},\"axis\":{\"enum\":[\"x\",\"y\"]}," +
                    "\"radius_mm\":{\"type\":\"number\"},\"curve\":{\"$ref\":\"#/$defs/target\"},\"curves\":{\"$ref\":\"#/$defs/target\"},\"kind\":{\"enum\":[\"radius\",\"diameter\"]},\"angle_deg\":{\"type\":\"number\"}," +
                    "\"pattern\":{\"type\":\"string\"},\"spacing_mm\":{\"type\":\"number\",\"minimum\":0.1,\"maximum\":200},\"scale\":{\"type\":\"number\",\"exclusiveMinimum\":0,\"description\":\"raw Rhino pattern scale (overrides spacing_mm)\"}," +
                    "\"layer\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"plane\":{\"$ref\":\"#/$defs/plane\"}" +
                    "},\"required\":[\"type\"]}}",
                    "\"items\"");
            }
        }
        public ToolKind Kind { get { return ToolKind.Mutate; } }

        sealed class Env
        {
            public RhinoDoc Doc; public ToolContext Ctx; public double Denom = 1, UnitsPerMm = 1, TextMm = 3.5; public string Arrows = "arrow";
            public RhinoPageView Page; public string DefaultLayer = DrawingUtil.AnnotationLayer; public JToken PlaneTok; public Unit Units;
        }
        enum Unit { Model, Page }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            var env = new Env { Doc = doc, Ctx = ctx };
            string layoutName = a.String("layout");
            env.TextMm = a.Double("text_height_mm", false, 3.5);
            if (env.TextMm < 0.5 || env.TextMm > 50) a.Error("'text_height_mm' must be between 0.5 and 50");
            env.Arrows = a.String("arrows", false, "arrow");
            if (env.Arrows != "arrow" && env.Arrows != "tick") a.Error("'arrows' must be arrow or tick");
            env.DefaultLayer = a.String("layer", false, DrawingUtil.AnnotationLayer);
            string lerr = LayerCheck(env.DefaultLayer);
            if (lerr != null) a.Error("layer: " + lerr);
            JToken pt; if (args.TryGetValue("plane", out pt)) env.PlaneTok = pt;

            if (layoutName != null)
            {
                env.Page = DrawingUtil.FindPage(doc, layoutName);
                if (env.Page == null) a.Error("no layout named '" + layoutName + "'. Existing layouts: " + DrawingUtil.PageNames(doc));
                env.Units = Unit.Page; env.Denom = 1; env.UnitsPerMm = DrawingUtil.PageUnitsPerMm(doc);
            }
            else
            {
                env.Units = Unit.Model; env.UnitsPerMm = DrawingUtil.ModelUnitsPerMm(doc);
                JToken sc;
                if (!args.TryGetValue("scale", out sc) || sc.Type == JTokenType.Null) a.Error("'scale' is required for model-space annotation (e.g. '1:100'): text and dimension sizes depend on it");
                else { string err; if (!DrawingUtil.TryScale(sc, out env.Denom, out err)) a.Error("scale: " + err); }
            }
            var items = args["items"] as JArray;
            if (items == null) a.Error("'items' is required (array)");
            else if (items.Count == 0 || items.Count > 200) a.Error("'items' must hold 1 to 200 entries");
            if (a.HasErrors) return a.ErrorResult();

            var results = new List<object>();
            var created = new List<Guid>();
            int ok = 0, i = 0;
            foreach (var it in items)
            {
                var r = new Dictionary<string, object>();
                r["i"] = i;
                var o = it as JObject;
                string type = o == null ? null : (string)o["type"];
                r["type"] = type;
                try
                {
                    if (o == null) throw new ArgumentException("item must be an object");
                    var id = RunItem(env, o, type, r);
                    created.Add(id);
                    r["handle"] = ctx.Handles.For(id);
                    r["ok"] = true; ok++;
                }
                catch (ArgumentException e) { r["ok"] = false; r["error"] = e.Message; }
                catch (Exception e) { r["ok"] = false; r["error"] = e.GetType().Name + ": " + e.Message; }
                results.Add(r);
                i++;
            }
            doc.Views.Redraw();
            var data = new Dictionary<string, object>
            {
                { "created", ok }, { "failed", results.Count - ok }, { "space", env.Page == null ? "model" : "layout:" + env.Page.PageName },
                { "scale", env.Page == null ? DrawingUtil.ScaleLabel(env.Denom) : "paper (printed size)" }, { "results", results }
            };
            if (ok == 0) return ToolResult.Fail("INVALID_ARGS", "no annotation was created: " + string.Join("; ", results.Select(x => (string)((Dictionary<string, object>)x)["error"]).Distinct()),
                "Fix the items and call again.", data);
            return ToolResult.Ok(data);
        }

        static string LayerCheck(string path) { return Layers.LayerUtil.ValidatePath(path); }

        // ---------- per item ----------

        Guid RunItem(Env env, JObject o, string type, Dictionary<string, object> r)
        {
            string perr = null;
            Frame frame;
            JToken planeTok = o["plane"] != null && o["plane"].Type != JTokenType.Null ? o["plane"] : env.PlaneTok;
            frame = GeoUtil.ParsePlane(planeTok, env.Ctx, null, out perr);
            if (frame == null) throw new ArgumentException("plane: " + perr);
            // page space: point coordinates are paper mm
            var p = new P(o, env.Ctx, frame);
            double toUnits = env.Units == Unit.Page ? env.UnitsPerMm : 1.0;
            string layer = p.Str("layer", false, env.DefaultLayer);
            string lerr = LayerCheck(layer);
            if (lerr != null) throw new ArgumentException("layer: " + lerr);
            string name = p.Str("name");

            Guid id = Guid.Empty;
            switch (type)
            {
                case "text": id = Text(env, p, frame, toUnits, layer, name, r); break;
                case "linear": case "aligned": id = Linear(env, p, frame, toUnits, layer, name, type, r); break;
                case "angular": id = Angular(env, p, frame, toUnits, layer, name, r); break;
                case "radial": id = Radial(env, p, frame, layer, name, r); break;
                case "leader": id = LeaderItem(env, p, frame, toUnits, layer, name, r); break;
                case "hatch": id = HatchItem(env, p, layer, name, r); break;
                default: throw new ArgumentException("unknown type '" + type + "'. Use text, linear, aligned, angular, radial, leader or hatch");
            }
            if (p.Errors.Count > 0) { if (id != Guid.Empty) env.Doc.Objects.Delete(id, true); throw new ArgumentException(string.Join("; ", p.Errors)); }
            if (id == Guid.Empty) throw new InvalidOperationException("Rhino did not add the object");
            return id;
        }

        ObjectAttributes Attr(Env env, string layer, string name)
        {
            string err;
            int li = DrawingUtil.EnsureLayer(env.Doc, layer, null, null, out err);
            if (li < 0) throw new ArgumentException("layer: " + err);
            var a = env.Page != null ? DrawingUtil.PageAttributes(env.Page, li) : new ObjectAttributes { LayerIndex = li };
            if (name != null) a.Name = name;
            return a;
        }

        Point3d ToSpace(Env env, Point3d worldPt, double toUnits) { return toUnits == 1.0 ? worldPt : new Point3d(worldPt.X * toUnits, worldPt.Y * toUnits, worldPt.Z * toUnits); }

        // The frame transforms local points to world. In page space the user gives paper mm, so scale to page units afterwards.
        Point3d Pt(Env env, P p, string key, double toUnits, bool req = true)
        {
            var w = p.Pt(key, req);
            return w.IsValid ? ToSpace(env, w, toUnits) : w;
        }
        Plane PlaneAt(Frame f, Point3d origin) { var pl = f.Plane; pl.Origin = origin; return pl; }

        double PrintedText(Env env, P p)
        {
            if (p.Has("height"))
            {
                double h = p.Num("height");
                if (!(h > 0)) throw new ArgumentException("'height' must be > 0");
                return h / (env.UnitsPerMm * env.Denom);
            }
            double mm = p.Num("height_mm", false, env.TextMm);
            if (!(mm >= 0.5 && mm <= 50)) throw new ArgumentException("'height_mm' must be between 0.5 and 50");
            return mm;
        }

        DimensionStyle StyleFor(Env env, double textMm, Dictionary<string, object> r)
        {
            string name, err;
            var ds = DrawingUtil.GetStyle(env.Doc, textMm, env.Denom, env.UnitsPerMm, env.Arrows, out name, out err);
            if (ds == null) throw new InvalidOperationException(err);
            r["dim_style"] = name;
            r["text_height_printed_mm"] = DrawingUtil.R(textMm);
            r["text_height_units"] = DrawingUtil.R(ds.TextHeight * ds.DimensionScale);
            return ds;
        }

        Guid Text(Env env, P p, Frame f, double toUnits, string layer, string name, Dictionary<string, object> r)
        {
            string text = p.Str("text", true);
            var origin = Pt(env, p, "point", toUnits);
            string just = p.Str("justify", false, "left"), va = p.Str("valign", false, "bottom");
            double rot = p.Num("rotation_deg", false, 0);
            double mm = PrintedText(env, p);
            if (p.Errors.Count > 0) throw new ArgumentException(string.Join("; ", p.Errors));
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("'text' is empty");
            var ds = StyleFor(env, mm, r);
            var te = TextEntity.Create(text, PlaneAt(f, origin), ds, false, 0, rot * Math.PI / 180.0);
            if (te == null) throw new InvalidOperationException("TextEntity.Create returned null");
            switch (just) { case "left": te.TextHorizontalAlignment = TextHorizontalAlignment.Left; break; case "center": te.TextHorizontalAlignment = TextHorizontalAlignment.Center; break; case "right": te.TextHorizontalAlignment = TextHorizontalAlignment.Right; break; default: throw new ArgumentException("'justify' must be left, center or right"); }
            switch (va) { case "top": te.TextVerticalAlignment = TextVerticalAlignment.Top; break; case "middle": te.TextVerticalAlignment = TextVerticalAlignment.Middle; break; case "bottom": te.TextVerticalAlignment = TextVerticalAlignment.Bottom; break; default: throw new ArgumentException("'valign' must be top, middle or bottom"); }
            var id = env.Doc.Objects.AddText(te, Attr(env, layer, name));
            r["text"] = text;
            return id;
        }

        double Offset(Env env, P p, double toUnits)
        {
            if (p.Has("offset")) return p.Num("offset") * toUnits;
            return p.Num("offset_mm", false, 8.0) * env.UnitsPerMm * env.Denom;
        }

        Guid Linear(Env env, P p, Frame f, double toUnits, string layer, string name, string type, Dictionary<string, object> r)
        {
            var p1 = Pt(env, p, "from", toUnits); var p2 = Pt(env, p, "to", toUnits);
            double off = Offset(env, p, toUnits);
            string axis = p.Str("axis", false, "x");
            double mm = PrintedText(env, p);
            if (p.Errors.Count > 0) throw new ArgumentException(string.Join("; ", p.Errors));
            if (p1.DistanceTo(p2) < 1e-9) throw new ArgumentException("'from' and 'to' coincide");
            var ds = StyleFor(env, mm, r);
            var pl = PlaneAt(f, p1);
            LinearDimension dim;
            if (type == "aligned")
            {
                var dir = p2 - p1; dir.Unitize();
                var perp = Vector3d.CrossProduct(pl.ZAxis, dir); perp.Unitize();
                // an aligned dimension is only measured correctly when the plane's x axis runs along the dimension
                var apl = new Plane(p1, dir, perp);
                dim = LinearDimension.Create(AnnotationType.Aligned, ds, apl, dir, p1, p2, p1 + perp * off, 0.0);
            }
            else
            {
                if (axis != "x" && axis != "y") throw new ArgumentException("'axis' must be x or y");
                var linePt = axis == "x" ? p1 + pl.YAxis * off : p1 + pl.XAxis * off;
                dim = LinearDimension.Create(AnnotationType.Rotated, ds, pl, pl.XAxis, p1, p2, linePt, axis == "x" ? 0.0 : Math.PI / 2);
            }
            if (dim == null) throw new InvalidOperationException("LinearDimension.Create returned null");
            try { dim.DimensionStyleId = ds.Id; } catch (Exception) { }
            var id = env.Doc.Objects.Add(dim, Attr(env, layer, name));
            Measured(env, dim, ds, r);
            r["expected_length"] = DrawingUtil.R(type == "aligned" ? p1.DistanceTo(p2) : (axis == "x" ? Math.Abs((p2 - p1) * pl.XAxis) : Math.Abs((p2 - p1) * pl.YAxis)));
            return id;
        }

        void Measured(Env env, Dimension dim, DimensionStyle ds, Dictionary<string, object> r)
        {
            try { r["measured"] = DrawingUtil.R(dim.NumericValue); } catch (Exception) { }
            try
            {
                var us = env.Page == null ? env.Doc.ModelUnitSystem : env.Doc.PageUnitSystem;
                var ld = dim as LinearDimension; var rd = dim as RadialDimension;
                if (ld != null) r["display_text"] = ld.GetDistanceDisplayText(us, ds);
                else if (rd != null) r["display_text"] = rd.GetDistanceDisplayText(us, ds);
            }
            catch (Exception) { }
        }

        Guid Angular(Env env, P p, Frame f, double toUnits, string layer, string name, Dictionary<string, object> r)
        {
            var c = Pt(env, p, "center", toUnits); var a1 = Pt(env, p, "from", toUnits); var a2 = Pt(env, p, "to", toUnits);
            double mm = PrintedText(env, p);
            if (p.Errors.Count > 0) throw new ArgumentException(string.Join("; ", p.Errors));
            double d1 = c.DistanceTo(a1), d2 = c.DistanceTo(a2);
            if (d1 < 1e-9 || d2 < 1e-9) throw new ArgumentException("'from'/'to' must differ from 'center'");
            var ds = StyleFor(env, mm, r);
            double rad = p.Has("radius_mm") ? p.Num("radius_mm") * env.UnitsPerMm * env.Denom : 0.6 * Math.Min(d1, d2);
            var u1 = a1 - c; u1.Unitize(); var u2 = a2 - c; u2.Unitize();
            var bis = u1 + u2; if (bis.Length < 1e-9) bis = Vector3d.CrossProduct(f.Plane.ZAxis, u1);
            bis.Unitize();
            var pl = PlaneAt(f, c);
            var dim = AngularDimension.Create(ds, pl, pl.XAxis, c, a1, a2, c + bis * rad);
            if (dim == null) throw new InvalidOperationException("AngularDimension.Create returned null");
            try { dim.DimensionStyleId = ds.Id; } catch (Exception) { }
            var id = env.Doc.Objects.Add(dim, Attr(env, layer, name));
            try { r["measured_deg"] = DrawingUtil.R(RhinoMath.ToDegrees(dim.NumericValue)); } catch (Exception) { }
            try { r["display_text"] = dim.GetAngleDisplayText(ds); } catch (Exception) { }
            r["expected_deg"] = DrawingUtil.R(RhinoMath.ToDegrees(Vector3d.VectorAngle(u1, u2)));
            return id;
        }

        Guid Radial(Env env, P p, Frame f, string layer, string name, Dictionary<string, object> r)
        {
            string kind = p.Str("kind", false, "radius");
            double ang = p.Num("angle_deg", false, 45.0);
            double mm = PrintedText(env, p);
            var objs = p.Objs("curve");
            if (p.Errors.Count > 0) throw new ArgumentException(string.Join("; ", p.Errors));
            if (kind != "radius" && kind != "diameter") throw new ArgumentException("'kind' must be radius or diameter");
            if (objs.Count != 1) throw new ArgumentException("'curve' must be exactly one arc or circle object");
            var crv = objs[0].Geometry as Curve;
            if (crv == null) throw new ArgumentException("'curve' must be an arc or circle");
            Plane apl; Point3d center; double radius;
            Arc arc; Circle circ;
            if (crv.TryGetCircle(out circ, env.Doc.ModelAbsoluteTolerance)) { apl = circ.Plane; center = circ.Center; radius = circ.Radius; }
            else if (crv.TryGetArc(out arc, env.Doc.ModelAbsoluteTolerance)) { apl = arc.Plane; center = arc.Center; radius = arc.Radius; }
            else throw new ArgumentException("'curve' is not an arc or circle (radial dimensions need true arcs/circles)");
            if (env.Page != null) throw new ArgumentException("radial dimensions measure model geometry; leave 'layout' out");
            var ds = StyleFor(env, mm, r);
            double t = ang * Math.PI / 180.0;
            var dir = apl.XAxis * Math.Cos(t) + apl.YAxis * Math.Sin(t);
            var radPt = center + dir * radius;
            var linePt = center + dir * (radius * 1.5);
            var pl = apl; pl.Origin = center;
            var dim = RadialDimension.Create(ds, kind == "radius" ? AnnotationType.Radius : AnnotationType.Diameter, pl, center, radPt, linePt);
            if (dim == null) throw new InvalidOperationException("RadialDimension.Create returned null");
            try { dim.DimensionStyleId = ds.Id; } catch (Exception) { }
            var id = env.Doc.Objects.Add(dim, Attr(env, layer, name));
            Measured(env, dim, ds, r);
            r["expected_length"] = DrawingUtil.R(kind == "radius" ? radius : 2 * radius);
            return id;
        }

        Guid LeaderItem(Env env, P p, Frame f, double toUnits, string layer, string name, Dictionary<string, object> r)
        {
            string text = p.Str("text", true);
            var raw = p.Pts("points", 2);
            double mm = PrintedText(env, p);
            if (p.Errors.Count > 0) throw new ArgumentException(string.Join("; ", p.Errors));
            var pts = raw.Select(w => ToSpace(env, w, toUnits)).ToArray();
            var ds = StyleFor(env, mm, r);
            var pl = PlaneAt(f, pts[0]);
            var ld = Leader.Create(text, pl, ds, pts);
            if (ld == null) throw new InvalidOperationException("Leader.Create returned null");
            try { ld.DimensionStyleId = ds.Id; } catch (Exception) { }
            var id = env.Doc.Objects.Add(ld, Attr(env, layer, name));
            r["text"] = text;
            return id;
        }

        /// <summary>Line spacing of a pattern at scale 1, estimated from the number of display lines over a 100-unit square (0 when unknown).</summary>
        static double BaseSpacing(RhinoDoc doc, int patternIndex)
        {
            try
            {
                var pl = new Polyline(new[] { new Point3d(0, 0, 0), new Point3d(100, 0, 0), new Point3d(100, 100, 0), new Point3d(0, 100, 0), new Point3d(0, 0, 0) });
                var hs = Hatch.Create(pl.ToNurbsCurve(), patternIndex, 0.0, 1.0, doc.ModelAbsoluteTolerance);
                if (hs == null || hs.Length == 0) return 0;
                Curve[] cs; Line[] ls; Brep br;
                hs[0].CreateDisplayGeometry(doc.HatchPatterns[patternIndex], 1.0, out cs, out ls, out br);
                if (ls == null || ls.Length < 2 || ls.Length > 50000) return 0;
                return Math.Round(100.0 / ls.Length, 4);
            }
            catch (Exception) { return 0; }
        }

        Guid HatchItem(Env env, P p, string layer, string name, Dictionary<string, object> r)
        {
            string pattern = p.Str("pattern", false, "Hatch1");
            double spacing = p.Num("spacing_mm", false, 2.0);
            double rawScale = p.Has("scale") ? p.Num("scale") : double.NaN;
            double rot = p.Num("rotation_deg", false, 0.0);
            var curves = p.Curves("curves");
            if (p.Errors.Count > 0) throw new ArgumentException(string.Join("; ", p.Errors));
            if (!(spacing >= 0.1 && spacing <= 200)) throw new ArgumentException("'spacing_mm' must be between 0.1 and 200");
            if (!double.IsNaN(rawScale) && !(rawScale > 0)) throw new ArgumentException("'scale' must be > 0");
            var hp = env.Doc.HatchPatterns.FindName(pattern); int pidx = hp == null ? -1 : hp.Index;
            if (pidx < 0)
            {
                var names = new List<string>();
                for (int k = 0; k < env.Doc.HatchPatterns.Count; k++) names.Add(env.Doc.HatchPatterns[k].Name);
                throw new ArgumentException("unknown hatch pattern '" + pattern + "'. Available: " + string.Join(", ", names));
            }
            foreach (var c in curves)
                if (!c.IsClosed || !c.IsPlanar()) throw new ArgumentException("hatch curves must be closed and planar");
            // Measured on Rhino 7.39 (layout PDF): a pattern scale s prints with line spacing base*s paper-mm in a detail, whatever the detail scale
            // (hatches behave like annotation). So the printed spacing is requested directly and converted to the pattern scale.
            double baseSpacing = BaseSpacing(env.Doc, pidx);
            double eff = double.IsNaN(rawScale) ? (baseSpacing > 0 ? spacing / baseSpacing : spacing) : rawScale;
            r["base_line_spacing"] = DrawingUtil.R(baseSpacing);
            r["printed_spacing_mm"] = baseSpacing > 0 ? (double?)DrawingUtil.R(baseSpacing * eff) : null;
            var hatches = Hatch.Create(curves, pidx, rot * Math.PI / 180.0, eff, env.Doc.ModelAbsoluteTolerance);
            if (hatches == null || hatches.Length == 0) throw new InvalidOperationException("Hatch.Create produced no hatch (curves may overlap, be open, or not coplanar)");
            Guid first = Guid.Empty;
            var ids = new List<string>();
            foreach (var h in hatches)
            {
                var id = env.Doc.Objects.AddHatch(h, Attr(env, layer, name));
                if (id == Guid.Empty) continue;
                if (first == Guid.Empty) first = id; else ids.Add(env.Ctx.Handles.For(id));
            }
            r["pattern"] = pattern; r["pattern_scale"] = DrawingUtil.R(eff); r["hatches"] = hatches.Length;
            if (ids.Count > 0) r["more_handles"] = ids;
            return first;
        }
    }
}

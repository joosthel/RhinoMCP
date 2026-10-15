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
using RhinoMCP.Tools.View;

namespace RhinoMCP.Tools.Drawing
{
    public sealed class LayoutCreate : ITool
    {
        public string Name { get { return "layout_create"; } }
        public string Description
        {
            get
            {
                return "Create a layout (sheet) with detail viewports at exact scales and an optional title block. " +
                       "paper: A0-A4 | ANSI_A-E | ARCH_A-E | {width_mm,height_mm}; orientation landscape (default) | portrait; margins mm (number or {left,right,top,bottom}, default 10). " +
                       "details: [{view: Top|Bottom|Left|Right|Front|Back|Perspective | named view, scale '1:100', rect [x0,y0,x1,y1] in paper mm from the bottom-left (omit = auto grid above the title block), " +
                       "zoom_to: 'all' (default) | target | {min,max} model bbox | center [x,y,z] to centre the detail on, layers [paths] = show only these layers in the detail, display_mode, locked (default true), title}]. " +
                       "Scale is set with DetailView.SetScale (1 paper mm = N model mm) and read back two ways (PageToModelRatio and the world-to-page transform); each detail reports achieved_scale and within_tolerance (0.1%). " +
                       "title_block {project, sheet_title, sheet_number, scale, date, author, revision}: English labels, text and frame lines on layer Sheet::TitleBlock, bottom-right strip 180 mm wide. Unknown fields stay blank (never invented). " +
                       "Fails if the layout name exists; delete it first (layout_delete). Page views do not show in view_capture; use export format pdf to see the sheet. Layer edits are not undoable on Rhino 7 Mac.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"name\":{\"type\":\"string\",\"description\":\"Layout name, unique\"}," +
                    "\"paper\":{\"description\":\"A0-A4 | ANSI_A-E | ARCH_A-E | {width_mm,height_mm}; default A3\",\"oneOf\":[{\"type\":\"string\"},{\"type\":\"object\",\"properties\":{\"width_mm\":{\"type\":\"number\"},\"height_mm\":{\"type\":\"number\"}},\"required\":[\"width_mm\",\"height_mm\"]}]}," +
                    "\"orientation\":{\"enum\":[\"landscape\",\"portrait\"],\"default\":\"landscape\"}," +
                    "\"margins\":{\"description\":\"mm: number or {left,right,top,bottom}; default 10\",\"oneOf\":[{\"type\":\"number\",\"minimum\":0},{\"type\":\"object\",\"properties\":{\"left\":{\"type\":\"number\"},\"right\":{\"type\":\"number\"},\"top\":{\"type\":\"number\"},\"bottom\":{\"type\":\"number\"}}}]}," +
                    "\"frame\":{\"type\":\"boolean\",\"default\":true,\"description\":\"Draw the sheet border at the margins\"}," +
                    "\"details\":{\"type\":\"array\",\"maxItems\":24,\"items\":{\"type\":\"object\",\"properties\":{" +
                    "\"view\":{\"type\":\"string\",\"description\":\"Top|Bottom|Left|Right|Front|Back|Perspective or a named view; default Top\"}," +
                    "\"scale\":{\"description\":\"'1:100' or the denominator 100\",\"oneOf\":[{\"type\":\"string\"},{\"type\":\"number\"}]}," +
                    "\"rect\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":4,\"maxItems\":4,\"description\":\"[x0,y0,x1,y1] paper mm, origin bottom-left\"}," +
                    "\"zoom_to\":{\"description\":\"'all' (default), a target, or {min:[x,y,z],max:[x,y,z]}\"}," +
                    "\"center\":{\"$ref\":\"#/$defs/point\",\"description\":\"model point to centre the detail on (overrides zoom_to)\"}," +
                    "\"layers\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Show only these layers (and their sublayers) in this detail; all other layers are hidden per viewport\"}," +
                    "\"display_mode\":{\"type\":\"string\"},\"locked\":{\"type\":\"boolean\",\"default\":true},\"title\":{\"type\":\"string\"}}}}," +
                    "\"title_block\":{\"type\":\"object\",\"properties\":{\"project\":{\"type\":\"string\"},\"sheet_title\":{\"type\":\"string\"},\"sheet_number\":{\"type\":\"string\"},\"scale\":{\"type\":\"string\",\"description\":\"default: derived from the details\"},\"date\":{\"type\":\"string\"},\"author\":{\"type\":\"string\"},\"revision\":{\"type\":\"string\"}},\"additionalProperties\":false}",
                    "\"name\"");
            }
        }
        public ToolKind Kind { get { return ToolKind.Mutate; } }

        sealed class DetailSpec
        {
            public int I; public string View = "Top", Title, DisplayMode; public double Denom; public bool HasScale;
            public double[] Rect; public JToken ZoomTo; public Point3d Center = Point3d.Unset; public bool Locked = true;
            public DefinedViewportProjection Proj; public string NamedView; public BoundingBox Zoom = BoundingBox.Empty;
            public bool Perspective; public List<string> Layers;
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            string name = a.String("name", true);
            string orient = a.String("orientation", false, "landscape");
            bool frame = a.Bool("frame", true);
            double pw = 0, ph = 0; string paperLabel = null;
            JToken pt; args.TryGetValue("paper", out pt);
            string perr;
            if (!a.HasErrors || name != null)
            {
                if (!DrawingUtil.TryPaper(pt, orient, out pw, out ph, out paperLabel, out perr)) a.Error(perr);
            }
            if (name != null && DrawingUtil.FindPage(doc, name) != null)
                a.Error("a layout named '" + name + "' already exists. Existing layouts: " + DrawingUtil.PageNames(doc) + ". Delete it with layout_delete or pick another name");
            if (name != null && name.Trim().Length == 0) a.Error("'name' is empty");

            double ml = 10, mr = 10, mt = 10, mb = 10;
            JToken mt0;
            if (args.TryGetValue("margins", out mt0) && mt0.Type != JTokenType.Null)
            {
                if (mt0.Type == JTokenType.Integer || mt0.Type == JTokenType.Float) { ml = mr = mt = mb = (double)mt0; }
                else if (mt0 is JObject)
                {
                    var mo = (JObject)mt0;
                    ml = Mm(mo, "left", 10, a); mr = Mm(mo, "right", 10, a); mt = Mm(mo, "top", 10, a); mb = Mm(mo, "bottom", 10, a);
                }
                else a.Error("'margins' must be a number or {left,right,top,bottom}");
            }
            if (pw > 0 && (ml + mr >= pw - 20 || mt + mb >= ph - 20)) a.Error("margins leave no usable area on a " + DrawingUtil.R(pw) + " x " + DrawingUtil.R(ph) + " mm sheet");

            // ---- details ----
            var specs = new List<DetailSpec>();
            var darr = args["details"] as JArray;
            if (args["details"] != null && args["details"].Type != JTokenType.Null && darr == null) a.Error("'details' must be an array");
            if (darr != null)
            {
                if (darr.Count > 24) a.Error("at most 24 details per layout");
                int i = 0;
                foreach (var it in darr.Take(24))
                {
                    var o = it as JObject;
                    if (o == null) { a.Error("details[" + i + "] must be an object"); i++; continue; }
                    specs.Add(ParseDetail(ctx, o, i, a, pw, ph));
                    i++;
                }
            }
            JObject tb = null;
            JToken tbt;
            if (args.TryGetValue("title_block", out tbt) && tbt.Type != JTokenType.Null)
            {
                tb = tbt as JObject;
                if (tb == null) a.Error("'title_block' must be an object");
                else foreach (var p in tb.Properties())
                        if (p.Value.Type != JTokenType.String) a.Error("title_block." + p.Name + " must be a string");
            }
            if (a.HasErrors) return a.ErrorResult();

            double ps = DrawingUtil.PageUnitsPerMm(doc);
            var prevActive = doc.Views.ActiveView;
            RhinoPageView pv = null;
            var warnings = new List<string>();
            var detailResults = new List<object>();
            try
            {
                pv = doc.Views.AddPageView(name, pw * ps, ph * ps);
                if (pv == null) return ToolResult.Fail("INTERNAL", "Rhino did not create the page view", "Check that the document is open and the name is valid.");

                string lerr;
                int detailLayer = DrawingUtil.EnsureLayer(doc, DrawingUtil.DetailLayer, 0x000000, 0.18, out lerr);
                if (detailLayer < 0) throw new InvalidOperationException(lerr);

                // title block geometry first so the auto grid knows how much room is left
                double tbW = Math.Min(180.0, pw - ml - mr), tbH = 42.0;
                bool hasTb = tb != null;
                double gridBottom = mb + (hasTb ? tbH + 5 : 0);
                AutoRects(specs, ml, gridBottom, pw - mr, ph - mt, warnings);

                foreach (var s in specs)
                    detailResults.Add(AddDetail(doc, ctx, pv, s, detailLayer, ps, warnings));

                var tbInfo = new Dictionary<string, object>();
                if (hasTb || frame)
                {
                    int tbLayer = DrawingUtil.EnsureLayer(doc, DrawingUtil.TitleBlockLayer, 0x000000, 0.25, out lerr);
                    if (tbLayer < 0) throw new InvalidOperationException(lerr);
                    var tba = new TitleBlockBuilder(doc, pv, tbLayer, ps, warnings);
                    int n = 0;
                    if (frame) n += tba.Frame(ml, mb, pw - mr, ph - mt);
                    if (hasTb)
                    {
                        string scaleText = TbString(tb, "scale");
                        if (scaleText == null)
                        {
                            var labels = specs.Where(s => s.HasScale && !s.Perspective).Select(s => DrawingUtil.ScaleLabel(s.Denom)).Distinct().ToList();
                            scaleText = string.Join(", ", labels);
                        }
                        n += tba.Block(pw - mr - tbW, mb, tbW, tbH,
                            TbString(tb, "project"), TbString(tb, "sheet_title"), TbString(tb, "sheet_number"), scaleText,
                            TbString(tb, "date"), TbString(tb, "author"), TbString(tb, "revision"));
                    }
                    tbInfo["layer"] = DrawingUtil.TitleBlockLayer;
                    tbInfo["objects"] = n;
                }

                doc.Views.Redraw();
                var info = new Dictionary<string, object>();
                info["layout"] = new
                {
                    name = pv.PageName, id = pv.MainViewport.Id.ToString(), paper = paperLabel,
                    width_mm = DrawingUtil.R(pw), height_mm = DrawingUtil.R(ph), orientation = pw >= ph ? "landscape" : "portrait",
                    margins_mm = new { left = ml, right = mr, top = mt, bottom = mb }
                };
                info["details"] = detailResults;
                if (tbInfo.Count > 0) info["title_block"] = tbInfo;
                info["all_scales_ok"] = detailResults.All(d => !((Dictionary<string, object>)d).ContainsKey("within_tolerance") || (bool)((Dictionary<string, object>)d)["within_tolerance"]);
                var res = ToolResult.Ok(info);
                foreach (var w in warnings) res.Warn(w);
                return res;
            }
            catch (Exception e)
            {
                try { if (pv != null) { foreach (var o in DrawingUtil.PageObjects(doc, pv).ToList()) doc.Objects.Delete(o, true); pv.Close(); } } catch (Exception) { }
                return ToolResult.Fail("INTERNAL", "layout_create failed: " + e.Message, "The partial layout was removed. " + e.GetType().Name, new { warnings });
            }
            finally
            {
                try { if (prevActive != null && !(prevActive is RhinoPageView)) doc.Views.ActiveView = prevActive; } catch (Exception) { }
            }
        }

        static string TbString(JObject tb, string key)
        {
            if (tb == null) return null;
            JToken t;
            return tb.TryGetValue(key, out t) && t.Type == JTokenType.String ? (string)t : null;
        }

        static double Mm(JObject o, string key, double def, ArgReader a)
        {
            JToken t;
            if (!o.TryGetValue(key, out t) || t.Type == JTokenType.Null) return def;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float || (double)t < 0) { a.Error("margins." + key + " must be a number >= 0"); return def; }
            return (double)t;
        }

        DetailSpec ParseDetail(ToolContext ctx, JObject o, int i, ArgReader a, double pw, double ph)
        {
            var s = new DetailSpec { I = i };
            var p = new P(o, ctx, new Frame(Plane.WorldXY, "world_xy"));
            string pre = "details[" + i + "]: ";
            s.View = p.Str("view", false, "Top");
            s.Title = p.Str("title");
            s.DisplayMode = p.Str("display_mode");
            s.Locked = p.Bool("locked", true);
            string canon;
            if (ViewCommon.TryStdProjection(s.View, out s.Proj, out canon)) { s.View = canon; s.Perspective = canon == "Perspective"; }
            else
            {
                int idx = ctx.Doc.NamedViews.FindByName(s.View);
                if (idx < 0)
                {
                    var names = new List<string>();
                    for (int k = 0; k < ctx.Doc.NamedViews.Count; k++) names.Add(ctx.Doc.NamedViews[k].Name);
                    a.Error(pre + "unknown view '" + s.View + "'. Use " + string.Join("/", ViewCommon.StdNames) + " or a named view (" + (names.Count == 0 ? "none saved" : string.Join(", ", names)) + ")");
                }
                else { s.NamedView = s.View; s.Proj = DefinedViewportProjection.Top; }
            }
            JToken lyt;
            if (o.TryGetValue("layers", out lyt) && lyt.Type != JTokenType.Null)
            {
                var la = lyt as JArray;
                if (la == null || la.Any(x => x.Type != JTokenType.String)) a.Error(pre + "'layers' must be an array of layer paths");
                else
                {
                    s.Layers = la.Select(x => (string)x).ToList();
                    foreach (var lp in s.Layers)
                    {
                        string lv = Layers.LayerUtil.ValidatePath(lp);
                        if (lv != null) a.Error(pre + "'layers': " + lv);
                    }
                }
            }
            JToken sc;
            if (o.TryGetValue("scale", out sc) && sc.Type != JTokenType.Null)
            {
                string err;
                if (!DrawingUtil.TryScale(sc, out s.Denom, out err)) a.Error(pre + err); else s.HasScale = true;
            }
            else if (!s.Perspective) a.Error(pre + "'scale' is required (e.g. '1:100')");
            JToken rt;
            if (o.TryGetValue("rect", out rt) && rt.Type != JTokenType.Null)
            {
                double[] r;
                if (!P.TryNums(rt, 4, 4, out r)) a.Error(pre + "'rect' must be [x0,y0,x1,y1] numbers (paper mm)");
                else
                {
                    s.Rect = new[] { Math.Min(r[0], r[2]), Math.Min(r[1], r[3]), Math.Max(r[0], r[2]), Math.Max(r[1], r[3]) };
                    if (s.Rect[2] - s.Rect[0] < 5 || s.Rect[3] - s.Rect[1] < 5) a.Error(pre + "'rect' must be at least 5 x 5 mm");
                    if (pw > 0 && (s.Rect[0] < 0 || s.Rect[1] < 0 || s.Rect[2] > pw || s.Rect[3] > ph))
                        a.Error(pre + "'rect' lies outside the " + DrawingUtil.R(pw) + " x " + DrawingUtil.R(ph) + " mm sheet");
                }
            }
            if (o["center"] != null && o["center"].Type != JTokenType.Null)
            {
                s.Center = p.LocalPt("center");
            }
            else if (o.TryGetValue("zoom_to", out s.ZoomTo) && s.ZoomTo.Type != JTokenType.Null)
            {
                var z = s.ZoomTo;
                if (z.Type == JTokenType.String && string.Equals((string)z, "all", StringComparison.OrdinalIgnoreCase)) s.ZoomTo = null;
                else if (z is JObject && ((JObject)z)["min"] != null) s.Zoom = ViewCommon.ReadBBox(z, a);
                else
                {
                    var objs = ObjectQuery.Resolve(ctx, z, a, pre + "zoom_to");
                    foreach (var ob in objs) { var b = ob.Geometry.GetBoundingBox(true); if (b.IsValid) s.Zoom.Union(b); }
                }
            }
            else s.ZoomTo = null;
            foreach (var e in p.Errors) a.Error(pre + e);
            return s;
        }

        static void AutoRects(List<DetailSpec> specs, double x0, double y0, double x1, double y1, List<string> warnings)
        {
            var auto = specs.Where(s => s.Rect == null).ToList();
            if (auto.Count == 0) return;
            double gap = 5;
            double w = x1 - x0, h = y1 - y0;
            int n = auto.Count;
            int cols = n == 1 ? 1 : (w >= h ? (n == 2 ? 2 : (int)Math.Ceiling(Math.Sqrt(n))) : (n == 2 ? 1 : (int)Math.Ceiling(Math.Sqrt(n))));
            int rows = (int)Math.Ceiling(n / (double)cols);
            double cw = (w - gap * (cols - 1)) / cols, chh = (h - gap * (rows - 1)) / rows;
            if (cw < 20 || chh < 20) { warnings.Add("auto grid cells are very small (" + DrawingUtil.R(cw) + " x " + DrawingUtil.R(chh) + " mm); pass explicit rects or use a larger sheet"); }
            for (int k = 0; k < n; k++)
            {
                int c = k % cols, r = k / cols;
                double cx0 = x0 + c * (cw + gap);
                double cy1 = y1 - r * (chh + gap);
                auto[k].Rect = new[] { cx0, cy1 - chh, cx0 + cw, cy1 };
            }
        }

        Dictionary<string, object> AddDetail(RhinoDoc doc, ToolContext ctx, RhinoPageView pv, DetailSpec s, int layerIndex, double ps, List<string> warnings)
        {
            var res = new Dictionary<string, object>();
            res["i"] = s.I; res["view"] = s.View;
            var title = s.Title ?? (s.View + (s.HasScale && !s.Perspective ? " " + DrawingUtil.ScaleLabel(s.Denom) : ""));
            var d = pv.AddDetailView(title, new Point2d(s.Rect[0] * ps, s.Rect[1] * ps), new Point2d(s.Rect[2] * ps, s.Rect[3] * ps), s.Proj);
            if (d == null) throw new InvalidOperationException("detail " + s.I + ": Rhino did not create the detail view");
            Guid id = d.Id;
            res["handle"] = ctx.Handles.For(id);
            res["title"] = title;
            res["rect_mm"] = s.Rect.Select(DrawingUtil.R).ToArray();

            var attr = d.Attributes.Duplicate();
            attr.LayerIndex = layerIndex;
            doc.Objects.ModifyAttributes(d, attr, true);
            d = (DetailViewObject)doc.Objects.FindId(id);

            var vp = d.Viewport;
            if (s.NamedView != null)
            {
                int idx = doc.NamedViews.FindByName(s.NamedView);
                string keep = vp.Name;
                doc.NamedViews.Restore(idx, vp);
                if (vp.Name != keep) vp.Name = keep;
            }
            if (s.DisplayMode != null)
            {
                string derr;
                var dm = ViewCommon.FindDisplayMode(s.DisplayMode, out derr);
                if (dm == null) warnings.Add("detail " + s.I + ": " + derr);
                else { vp.DisplayMode = dm; res["display_mode"] = dm.EnglishName; }
            }
            if (vp.DisplayMode != null && !res.ContainsKey("display_mode")) res["display_mode"] = vp.DisplayMode.EnglishName;

            if (s.Layers != null)
            {
                Guid vid = vp.Id; int shown = 0;
                for (int li = 0; li < doc.Layers.Count; li++)
                {
                    var l = doc.Layers[li];
                    if (l.IsDeleted) continue;
                    string lp = l.FullPath;
                    bool keep = s.Layers.Any(x => lp == x || lp.StartsWith(x + "::") || x.StartsWith(lp + "::"));
                    l.SetPerViewportVisible(vid, keep);
                    if (keep) shown++;
                }
                res["layers_visible"] = shown;
                foreach (var x in s.Layers)
                    if (doc.Layers.FindByFullPath(x, -1) < 0)
                        warnings.Add("detail " + s.I + ": layer '" + x + "' does not exist yet; layers created later are visible in every detail by default");
            }

            // centre of the content
            Point3d center = Point3d.Unset;
            if (s.Center.IsValid) center = s.Center;
            else if (s.Zoom.IsValid) center = s.Zoom.Center;
            else
            {
                int cnt;
                var bb = DrawingUtil.ModelBBox(doc, out cnt, s.Layers);
                if (bb.IsValid) center = bb.Center;
                else warnings.Add("detail " + s.I + ": the model has no visible objects, so the detail is not centred");
            }

            if (s.Perspective)
            {
                if (s.HasScale) warnings.Add("detail " + s.I + ": a perspective view has no scale; 'scale' was ignored");
                int c2;
                var zb = s.Zoom.IsValid ? s.Zoom : DrawingUtil.ModelBBox(doc, out c2, s.Layers);
                if (zb.IsValid) vp.ZoomBoundingBox(zb);
                d.CommitViewportChanges();
            }
            else
            {
                // scale first, then shift the camera in the view plane so the content is centred
                var dg = d.DetailGeometry;
                if (!dg.SetScale(s.Denom, UnitSystem.Millimeters, 1.0, UnitSystem.Millimeters))
                    warnings.Add("detail " + s.I + ": DetailView.SetScale returned false");
                d.CommitChanges();
                d = (DetailViewObject)doc.Objects.FindId(id);
                vp = d.Viewport;
                if (center.IsValid)
                {
                    Point3d t = vp.CameraTarget; Vector3d dir = vp.CameraDirection;
                    Vector3d off = center - t;
                    off -= dir * (off * dir);
                    vp.SetCameraLocations(t + off, vp.CameraLocation + off);
                }
                d.CommitViewportChanges();
                d = (DetailViewObject)doc.Objects.FindId(id);
                // CommitViewportChanges may reset the scale in some cases: set it again if it drifted
                double r1, r2;
                DrawingUtil.ReadScale(doc, d, out r1, out r2);
                if (!(Math.Abs(r1 - s.Denom) / s.Denom < 0.0005))
                {
                    d.DetailGeometry.SetScale(s.Denom, UnitSystem.Millimeters, 1.0, UnitSystem.Millimeters);
                    d.CommitChanges();
                    d = (DetailViewObject)doc.Objects.FindId(id);
                }
            }
            if (s.Locked)
            {
                d.DetailGeometry.IsProjectionLocked = true;
                d.CommitChanges();
                d = (DetailViewObject)doc.Objects.FindId(id);
            }

            if (!s.Perspective)
            {
                double r1, r2;
                DrawingUtil.ReadScale(doc, d, out r1, out r2);
                res["requested_scale"] = DrawingUtil.ScaleLabel(s.Denom);
                res["requested_denominator"] = s.Denom;
                res["achieved_denominator"] = double.IsNaN(r1) ? (double?)null : Math.Round(r1, 4);
                res["achieved_denominator_from_transform"] = double.IsNaN(r2) ? (double?)null : Math.Round(r2, 4);
                string fmt; d.GetFormattedScale(DetailViewObject.ScaleFormat.OneToModelLength, out fmt);
                res["achieved_scale"] = fmt;
                double rel = double.IsNaN(r1) ? 1 : Math.Abs(r1 - s.Denom) / s.Denom;
                bool ok = rel <= 0.001;
                if (!double.IsNaN(r2) && Math.Abs(r2 - s.Denom) / s.Denom > 0.001) { ok = false; }
                res["within_tolerance"] = ok;
                if (!ok) warnings.Add("detail " + s.I + ": achieved scale " + r1 + " (transform " + r2 + ") differs from requested 1:" + s.Denom + " by more than 0.1%");
            }
            res["locked"] = d.DetailGeometry.IsProjectionLocked;
            res["parallel"] = d.DetailGeometry.IsParallelProjection;
            return res;
        }
    }

    /// <summary>Draws a frame and an ISO 7200-style title block as lines and text on page space.</summary>
    internal sealed class TitleBlockBuilder
    {
        readonly RhinoDoc _doc; readonly RhinoPageView _pv; readonly int _layer; readonly double _ps; readonly List<string> _warn;
        readonly Dictionary<double, DimensionStyle> _styles = new Dictionary<double, DimensionStyle>();

        public TitleBlockBuilder(RhinoDoc doc, RhinoPageView pv, int layer, double ps, List<string> warn)
        { _doc = doc; _pv = pv; _layer = layer; _ps = ps; _warn = warn; }

        ObjectAttributes Attr(string name, double? weightMm)
        {
            var a = DrawingUtil.PageAttributes(_pv, _layer);
            if (name != null) a.Name = name;
            if (weightMm.HasValue) { a.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromObject; a.PlotWeight = weightMm.Value; }
            return a;
        }

        int Line(double x0, double y0, double x1, double y1, string name, double weight)
        {
            return _doc.Objects.AddLine(new Point3d(x0 * _ps, y0 * _ps, 0), new Point3d(x1 * _ps, y1 * _ps, 0), Attr(name, weight)) != Guid.Empty ? 1 : 0;
        }

        public int Frame(double x0, double y0, double x1, double y1)
        {
            int n = 0;
            n += Line(x0, y0, x1, y0, "frame", 0.5); n += Line(x1, y0, x1, y1, "frame", 0.5);
            n += Line(x1, y1, x0, y1, "frame", 0.5); n += Line(x0, y1, x0, y0, "frame", 0.5);
            return n;
        }

        DimensionStyle Style(double heightMm)
        {
            DimensionStyle ds;
            if (_styles.TryGetValue(heightMm, out ds)) return ds;
            string name, err;
            ds = DrawingUtil.GetStyle(_doc, heightMm, 1.0, _ps, "arrow", out name, out err);
            if (ds == null) throw new InvalidOperationException(err);
            _styles[heightMm] = ds;
            return ds;
        }

        int Text(string text, double x, double y, double heightMm, string name, TextVerticalAlignment va, double maxWidthMm)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var ds = Style(heightMm);
            var plane = new Plane(new Point3d(x * _ps, y * _ps, 0), Vector3d.XAxis, Vector3d.YAxis);
            var te = TextEntity.Create(text, plane, ds, false, 0, 0);
            if (te == null) { _warn.Add("could not create title block text '" + text + "'"); return 0; }
            te.TextHorizontalAlignment = TextHorizontalAlignment.Left;
            te.TextVerticalAlignment = va;
            double est = text.Length * heightMm * 0.58;
            if (maxWidthMm > 0 && est > maxWidthMm) _warn.Add("title block '" + text + "' is probably wider than its cell (" + DrawingUtil.R(est) + " mm > " + DrawingUtil.R(maxWidthMm) + " mm); shorten it");
            return _doc.Objects.AddText(te, Attr(name, null)) != Guid.Empty ? 1 : 0;
        }

        /// <summary>Three rows: project (14 mm), sheet title (14 mm), five small cells (14 mm).</summary>
        public int Block(double x0, double y0, double w, double h, string project, string sheetTitle, string number, string scale, string date, string author, string rev)
        {
            int n = 0;
            double row = h / 3.0, x1 = x0 + w, y1 = y0 + h;
            n += Frame(x0, y0, x1, y1);
            n += Line(x0, y0 + row, x1, y0 + row, "tb_rule", 0.25);
            n += Line(x0, y0 + 2 * row, x1, y0 + 2 * row, "tb_rule", 0.25);
            double cw = w / 5.0;
            for (int i = 1; i < 5; i++) n += Line(x0 + i * cw, y0, x0 + i * cw, y0 + row, "tb_rule", 0.25);

            // labels sit at the top-left of each cell, values at the bottom-left
            n += Cell(x0, y0 + 2 * row, w, row, "PROJECT", project, 5.0, "project");
            n += Cell(x0, y0 + row, w, row, "SHEET TITLE", sheetTitle, 5.0, "sheet_title");
            string[] labels = { "SHEET NO.", "SCALE", "DATE", "DRAWN BY", "REV." };
            string[] keys = { "sheet_number", "scale", "date", "author", "revision" };
            string[] vals = { number, scale, date, author, rev };
            for (int i = 0; i < 5; i++) n += Cell(x0 + i * cw, y0, cw, row, labels[i], vals[i], 3.5, keys[i]);
            return n;
        }

        int Cell(double x, double y, double w, double h, string label, string value, double valueH, string key)
        {
            int n = Text(label, x + 2, y + h - 1.5, 2.5, "tb_label:" + key, TextVerticalAlignment.Top, w - 4);
            n += Text(value, x + 2, y + 2.5, valueH, "tb:" + key, TextVerticalAlignment.Bottom, w - 4);
            return n;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Layers;

namespace RhinoMCP.Tools.Drawing
{
    /// <summary>Shared helpers for layout_*, annotate and export: paper sizes, scales, page views, dimension styles, layers.</summary>
    internal static class DrawingUtil
    {
        public const string TitleBlockLayer = "Sheet::TitleBlock";
        public const string DetailLayer = "Sheet::Details";
        public const string AnnotationLayer = "Annotation";

        static double In(double inches) { return inches * 25.4; }

        // portrait dimensions in mm (short side first)
        static readonly Dictionary<string, double[]> Papers = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "A0", new[] { 841.0, 1189.0 } }, { "A1", new[] { 594.0, 841.0 } }, { "A2", new[] { 420.0, 594.0 } },
            { "A3", new[] { 297.0, 420.0 } }, { "A4", new[] { 210.0, 297.0 } },
            { "ANSI_A", new[] { In(8.5), In(11) } }, { "ANSI_B", new[] { In(11), In(17) } }, { "ANSI_C", new[] { In(17), In(22) } },
            { "ANSI_D", new[] { In(22), In(34) } }, { "ANSI_E", new[] { In(34), In(44) } },
            { "ARCH_A", new[] { In(9), In(12) } }, { "ARCH_B", new[] { In(12), In(18) } }, { "ARCH_C", new[] { In(18), In(24) } },
            { "ARCH_D", new[] { In(24), In(36) } }, { "ARCH_E", new[] { In(36), In(48) } },
        };
        public static string PaperHelp { get { return string.Join(", ", Papers.Keys) + " or {width_mm, height_mm}"; } }

        /// <summary>Paper token ("A3" | {width_mm,height_mm}) + orientation to final width/height in mm.</summary>
        public static bool TryPaper(JToken t, string orientation, out double w, out double h, out string label, out string err)
        {
            w = h = 0; label = null; err = null;
            if (orientation != null && orientation != "landscape" && orientation != "portrait") { err = "'orientation' must be landscape or portrait"; return false; }
            if (t == null || t.Type == JTokenType.Null) { t = "A3"; }
            if (t.Type == JTokenType.String)
            {
                double[] d;
                string key = ((string)t).Trim().Replace(' ', '_').Replace('-', '_');
                if (!Papers.TryGetValue(key, out d)) { err = "unknown paper '" + (string)t + "'. Use " + PaperHelp; return false; }
                label = Papers.Keys.First(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                bool land = orientation != "portrait";
                w = land ? d[1] : d[0]; h = land ? d[0] : d[1];
                return true;
            }
            var o = t as JObject;
            if (o != null && o["width_mm"] != null && o["height_mm"] != null
                && (o["width_mm"].Type == JTokenType.Integer || o["width_mm"].Type == JTokenType.Float)
                && (o["height_mm"].Type == JTokenType.Integer || o["height_mm"].Type == JTokenType.Float))
            {
                w = (double)o["width_mm"]; h = (double)o["height_mm"];
                if (w < 20 || h < 20 || w > 20000 || h > 20000) { err = "custom paper must be between 20 and 20000 mm per side"; return false; }
                if (orientation == "landscape" && w < h) { double x = w; w = h; h = x; }
                if (orientation == "portrait" && w > h) { double x = w; w = h; h = x; }
                label = "custom " + R(w) + "x" + R(h) + " mm";
                return true;
            }
            err = "'paper' must be a name or {width_mm, height_mm}. Use " + PaperHelp;
            return false;
        }

        /// <summary>"1:100" | "100" | 100 | "1/100" | "2:1" to the scale denominator (model per paper). 0.5 means 2:1.</summary>
        public static bool TryScale(JToken t, out double denom, out string err)
        {
            denom = 0; err = null;
            if (t == null || t.Type == JTokenType.Null) { err = "scale is missing"; return false; }
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) denom = (double)t;
            else if (t.Type == JTokenType.String)
            {
                string s = ((string)t).Trim();
                string[] parts = s.Split(new[] { ':', '/' });
                double a, b;
                var ci = CultureInfo.InvariantCulture;
                if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, ci, out a)) denom = a;
                else if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, ci, out a) && double.TryParse(parts[1], NumberStyles.Float, ci, out b) && a > 0 && b > 0)
                    denom = b / a;
                else { err = "cannot parse scale '" + s + "'. Use '1:100', '1:50' or a number (the denominator)"; return false; }
            }
            else { err = "scale must be a string like '1:100' or a number"; return false; }
            if (!(denom > 0) || double.IsInfinity(denom) || denom > 100000 || denom < 0.001) { err = "scale denominator must be between 0.001 and 100000"; return false; }
            return true;
        }

        public static string ScaleLabel(double denom)
        {
            if (denom >= 1) return "1:" + denom.ToString("0.###", CultureInfo.InvariantCulture);
            return (1.0 / denom).ToString("0.###", CultureInfo.InvariantCulture) + ":1";
        }

        public static double R(double v) { return Math.Round(v, 4); }

        /// <summary>Page units per mm.</summary>
        public static double PageUnitsPerMm(RhinoDoc doc) { return RhinoMath.UnitScale(UnitSystem.Millimeters, doc.PageUnitSystem); }
        /// <summary>Model units per mm.</summary>
        public static double ModelUnitsPerMm(RhinoDoc doc) { return RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem); }

        public static RhinoPageView FindPage(RhinoDoc doc, string name)
        {
            var pages = doc.Views.GetPageViews();
            if (pages == null) return null;
            var hit = pages.FirstOrDefault(p => string.Equals(p.PageName, name, StringComparison.Ordinal));
            return hit ?? pages.FirstOrDefault(p => string.Equals(p.PageName, name, StringComparison.OrdinalIgnoreCase));
        }
        public static string PageNames(RhinoDoc doc)
        {
            var pages = doc.Views.GetPageViews() ?? new RhinoPageView[0];
            return pages.Length == 0 ? "(none)" : string.Join(", ", pages.Select(p => p.PageName));
        }

        /// <summary>Ensures a layer path exists (parent chain included). Optionally sets colour/plot width on creation.</summary>
        public static int EnsureLayer(RhinoDoc doc, string path, int? rgb, double? plotWidthMm, out string error)
        {
            var created = new List<string>();
            var l = LayerUtil.Ensure(doc, path, created, out error);
            if (l == null) return -1;
            if (created.Count > 0 && created.Contains(path))
            {
                if (rgb.HasValue) LayerUtil.SetRgb(l, rgb.Value);
                if (plotWidthMm.HasValue) l.PlotWeight = plotWidthMm.Value;
            }
            return l.Index;
        }

        public static ObjectAttributes PageAttributes(RhinoPageView pv, int layerIndex)
        {
            var a = new ObjectAttributes();
            a.LayerIndex = layerIndex;
            a.Space = ActiveSpace.PageSpace;
            a.ViewportId = pv.MainViewport.Id;
            return a;
        }

        public static IEnumerable<RhinoObject> PageObjects(RhinoDoc doc, RhinoPageView pv)
        {
            Guid vid = pv.MainViewport.Id;
            var s = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true, ActiveObjects = true, ReferenceObjects = true, IncludeLights = false, IncludeGrips = false };
            foreach (var o in doc.Objects.GetObjectList(s))
                if (o.Attributes.Space == ActiveSpace.PageSpace && o.Attributes.ViewportId == vid) yield return o;
        }

        /// <summary>Axis-aligned bounding box of visible model-space objects.</summary>
        public static BoundingBox ModelBBox(RhinoDoc doc, out int count, List<string> onlyLayers = null)
        {
            var bb = BoundingBox.Empty; count = 0;
            var s = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = false, VisibleFilter = true };
            foreach (var o in doc.Objects.GetObjectList(s))
            {
                if (o.Attributes.Space != ActiveSpace.ModelSpace) continue;
                if (onlyLayers != null) { string lp = doc.Layers[o.Attributes.LayerIndex].FullPath; if (!onlyLayers.Any(x => lp == x || lp.StartsWith(x + "::"))) continue; }
                var b = o.Geometry.GetBoundingBox(true);
                if (b.IsValid) { bb.Union(b); count++; }
            }
            return bb;
        }

        /// <summary>
        /// Scale denominator achieved by a detail, measured two ways: from PageToModelRatio and from the world-to-page transform
        /// (1 m of model length along the view's x axis, converted to mm on paper).
        /// </summary>
        public static void ReadScale(RhinoDoc doc, DetailViewObject d, out double fromRatio, out double fromTransform)
        {
            double ratio = d.DetailGeometry.PageToModelRatio;
            double pageMm = 1.0 / PageUnitsPerMm(doc);          // mm per page unit
            double modelMm = 1.0 / ModelUnitsPerMm(doc);        // mm per model unit
            // ratio = page length / model length, each in its own unit
            double paperPerModel = ratio * pageMm / modelMm;
            fromRatio = paperPerModel > 0 ? 1.0 / paperPerModel : double.NaN;
            fromTransform = double.NaN;
            try
            {
                var xf = d.WorldToPageTransform;
                var vp = d.Viewport;
                Vector3d ax = vp.CameraX; // world direction of the detail's horizontal axis
                double len = 1000.0 * ModelUnitsPerMm(doc); // 1000 mm of model length in model units
                Point3d p0 = vp.CameraTarget, p1 = p0 + ax * len;
                p0.Transform(xf); p1.Transform(xf);
                double paperMm = p0.DistanceTo(p1) * pageMm;
                if (paperMm > 0) fromTransform = 1000.0 / paperMm;
            }
            catch (Exception) { }
        }

        // ---------- dimension styles ----------

        /// <summary>
        /// Finds or creates a dimension style whose sizes are printed-mm values and whose DimensionScale is the drawing scale denominator.
        /// Model-space size = printed height x denominator (text-dims.md), and Rhino 7 annotation scaling makes the same style print at
        /// exactly the printed height inside a detail of that scale. unitsPerMm converts paper mm to the unit of the space the annotation lives in.
        /// </summary>
        public static DimensionStyle GetStyle(RhinoDoc doc, double textMm, double denom, double unitsPerMm, string arrows, out string name, out string error)
        {
            error = null;
            var ci = CultureInfo.InvariantCulture;
            name = "MCP " + ScaleLabel(denom) + " t" + textMm.ToString("0.##", ci) + " " + arrows;
            var existing = doc.DimStyles.FindName(name);
            if (existing != null && existing.IsDeleted) existing = null;
            double k = unitsPerMm;                 // printed mm in the units of the space; the style's DimensionScale (= denominator) enlarges it in model space
            var ds = new DimensionStyle();
            ds.Name = name;
            ds.TextHeight = textMm * k;
            ds.DimensionScale = denom;
            ds.ArrowLength = 2.5 * k;
            ds.LeaderArrowLength = 2.5 * k;
            ds.ExtensionLineExtension = 1.5 * k;
            ds.ExtensionLineOffset = 1.0 * k;
            ds.TextGap = 0.8 * k;
            ds.CentermarkSize = 2.5 * k;
            ds.BaselineSpacing = 7.0 * k;
            ds.LeaderLandingLength = 4.0 * k;
            ds.DimTextLocation = DimensionStyle.TextLocation.AboveDimLine;
            // decimals: mm -> 0, cm -> 1, m -> 3, inch -> 2, ft -> 3 (about 1 mm of resolution)
            double mmPerUnit = 1.0 / unitsPerMm;
            ds.LengthResolution = mmPerUnit <= 1.0 ? 0 : Math.Min(4, (int)Math.Ceiling(Math.Log10(mmPerUnit)));
            if (arrows == "tick")
            {
                ds.ArrowType1 = DimensionStyle.ArrowType.Tick; ds.ArrowType2 = DimensionStyle.ArrowType.Tick;
                ds.DimensionLineExtension = 1.5 * k;
                ds.ArrowLength = 2.0 * k;
            }
            else if (arrows == "arrow")
            {
                ds.ArrowType1 = DimensionStyle.ArrowType.SolidTriangle; ds.ArrowType2 = DimensionStyle.ArrowType.SolidTriangle;
            }
            if (existing != null)
            {
                bool same = Math.Abs(existing.TextHeight - ds.TextHeight) < 1e-9 && Math.Abs(existing.DimensionScale - ds.DimensionScale) < 1e-9
                            && Math.Abs(existing.ArrowLength - ds.ArrowLength) < 1e-9 && existing.LengthResolution == ds.LengthResolution;
                if (same) return existing;
                if (!doc.DimStyles.Modify(ds, existing.Index, false)) { error = "could not update dimension style '" + name + "'"; return null; }
                return doc.DimStyles[existing.Index];
            }
            int idx = doc.DimStyles.Add(ds, false);
            if (idx < 0) { error = "Rhino refused to create dimension style '" + name + "'"; return null; }
            return doc.DimStyles[idx];
        }
    }
}

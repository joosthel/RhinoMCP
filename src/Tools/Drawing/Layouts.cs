using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCP.Tools.Drawing
{
    public sealed class LayoutsList : ITool
    {
        public string Name { get { return "layouts_list"; } }
        public string Description
        {
            get
            {
                return "List layouts (page views): paper size, detail viewports with their scale read back from Rhino (achieved_scale, locked, view, display mode, rect in paper mm) " +
                       "and a count of annotation objects by layer. Optional name filters to one layout. Use before layout_create (names must be unique) and to verify scale after edits.";
            }
        }
        public string InputSchema { get { return Schemas.Object("\"name\":{\"type\":\"string\",\"description\":\"Only this layout\"}"); } }
        public ToolKind Kind { get { return ToolKind.Read; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            string filter = a.String("name");
            if (a.HasErrors) return a.ErrorResult();
            var pages = (doc.Views.GetPageViews() ?? new RhinoPageView[0]).ToList();
            if (filter != null)
            {
                pages = pages.Where(p => string.Equals(p.PageName, filter, StringComparison.OrdinalIgnoreCase)).ToList();
                if (pages.Count == 0)
                    return ToolResult.Fail("NOT_FOUND", "no layout named '" + filter + "'", "Existing layouts: " + DrawingUtil.PageNames(doc) + ".");
            }
            double ps = DrawingUtil.PageUnitsPerMm(doc);
            var list = new List<object>();
            foreach (var pv in pages)
            {
                var details = new List<object>();
                foreach (var d in (pv.GetDetailViews() ?? new DetailViewObject[0]))
                {
                    double r1, r2;
                    DrawingUtil.ReadScale(doc, d, out r1, out r2);
                    string fmt = null; try { d.GetFormattedScale(DetailViewObject.ScaleFormat.OneToModelLength, out fmt); } catch (Exception) { }
                    var bb = d.Geometry.GetBoundingBox(true);
                    var vp = d.Viewport;
                    details.Add(new
                    {
                        handle = ctx.Handles.For(d.Id),
                        title = d.DescriptiveTitle,
                        view = vp.Name,
                        rect_mm = new[] { DrawingUtil.R(bb.Min.X / ps), DrawingUtil.R(bb.Min.Y / ps), DrawingUtil.R(bb.Max.X / ps), DrawingUtil.R(bb.Max.Y / ps) },
                        parallel = d.DetailGeometry.IsParallelProjection,
                        achieved_scale = d.DetailGeometry.IsParallelProjection ? fmt : null,
                        achieved_denominator = d.DetailGeometry.IsParallelProjection && !double.IsNaN(r1) ? (double?)Math.Round(r1, 4) : null,
                        locked = d.DetailGeometry.IsProjectionLocked,
                        display_mode = vp.DisplayMode == null ? null : vp.DisplayMode.EnglishName,
                        layer = doc.Layers[d.Attributes.LayerIndex].FullPath
                    });
                }
                var byLayer = new Dictionary<string, int>();
                int total = 0;
                foreach (var o in DrawingUtil.PageObjects(doc, pv))
                {
                    if (o is DetailViewObject) continue;
                    string lp = doc.Layers[o.Attributes.LayerIndex].FullPath;
                    int c; byLayer.TryGetValue(lp, out c); byLayer[lp] = c + 1; total++;
                }
                list.Add(new
                {
                    name = pv.PageName,
                    id = pv.MainViewport.Id.ToString(),
                    paper = pv.PaperName,
                    width_mm = DrawingUtil.R(pv.PageWidth / ps),
                    height_mm = DrawingUtil.R(pv.PageHeight / ps),
                    page_number = pv.PageNumber,
                    details,
                    page_objects = total,
                    page_objects_by_layer = byLayer
                });
            }
            return ToolResult.Ok(new { total = list.Count, layouts = list });
        }
    }

    public sealed class LayoutDelete : ITool
    {
        public string Name { get { return "layout_delete"; } }
        public string Description
        {
            get
            {
                return "Delete a layout and everything on it (detail viewports, title block and annotation placed in the layout). Model geometry is not touched. " +
                       "Requires confirm equal to the layout name, so a typo cannot delete the wrong sheet. Layout deletion is not undoable on Rhino 7 Mac.";
            }
        }
        public string InputSchema
        {
            get { return Schemas.Object("\"name\":{\"type\":\"string\"},\"confirm\":{\"type\":\"string\",\"description\":\"Must equal name\"}", "\"name\",\"confirm\""); }
        }
        public ToolKind Kind { get { return ToolKind.Destructive; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            string name = a.String("name", true), confirm = a.String("confirm", true);
            if (a.HasErrors) return a.ErrorResult();
            var pv = DrawingUtil.FindPage(doc, name);
            if (pv == null)
                return ToolResult.Fail("NOT_FOUND", "no layout named '" + name + "'", "Existing layouts: " + DrawingUtil.PageNames(doc) + ".");
            if (confirm != pv.PageName)
                return ToolResult.Fail("CONFIRM_REQUIRED", "confirm must equal the layout name '" + pv.PageName + "'", "Repeat the call with confirm set to the exact layout name to delete it.");
            var prevActive = doc.Views.ActiveView;
            var objs = DrawingUtil.PageObjects(doc, pv).ToList();
            foreach (var dv in objs.OfType<DetailViewObject>().ToList())
                for (int li = 0; li < doc.Layers.Count; li++) { try { doc.Layers[li].DeletePerViewportSettings(dv.Viewport.Id); } catch (Exception) { } }
            int details = objs.Count(o => o is DetailViewObject);
            int deleted = 0;
            foreach (var o in objs) if (doc.Objects.Delete(o, true)) deleted++;
            bool closed = pv.Close();
            doc.Views.Redraw();
            bool gone = DrawingUtil.FindPage(doc, name) == null;
            try { if (prevActive != null && !(prevActive is RhinoPageView)) doc.Views.ActiveView = prevActive; } catch (Exception) { }
            if (!gone) return ToolResult.Fail("INTERNAL", "Rhino did not close the layout '" + name + "'", "Close it manually in Rhino.", new { page_objects_deleted = deleted });
            return ToolResult.Ok(new { deleted_layout = name, page_objects_deleted = deleted, details_deleted = details, closed });
        }
    }
}

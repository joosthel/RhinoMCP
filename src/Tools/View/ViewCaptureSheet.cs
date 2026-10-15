using System;
using RhinoMCP.Tools.Shared;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;

namespace RhinoMCP.Tools.View
{
    public sealed class ViewCaptureSheetTool : ITool
    {
        static readonly string[] Order = { "Top", "Front", "Right", "Perspective" };

        public string Name { get { return "view_capture_sheet"; } }
        public string Description
        {
            get
            {
                return "One 2x2 PNG contact sheet of Top (top-left), Front (top-right), Right (bottom-left), Perspective (bottom-right), each zoomed to all (or to zoom_to, with margin). " +
                       "Panels are labelled by drawing the view name as text in the corner of each view during the capture (labels=true, default; no GDI+ needed); the order is also listed in the metadata, and labels_drawn says whether the text really rendered. " +
                       "display_mode: case-insensitive English name (Shaded, Wireframe, Rendered, Ghosted, X-Ray, Technical, Artistic, Pen, Arctic...). " +
                       "only_layers / isolate hide everything else during the capture only. The active viewport is driven briefly and restored. " +
                       "width/height are the sheet size (default 1024x768, max 1600).";
            }
        }
        public string InputSchema
        {
            get
            {
                return @"{""type"":""object"",""properties"":{
""display_mode"":{""type"":""string"",""description"":""English display mode name, e.g. Shaded, Wireframe, Rendered""},
""width"":{""type"":""integer"",""minimum"":32,""maximum"":1600,""default"":1024},
""height"":{""type"":""integer"",""minimum"":32,""maximum"":1600,""default"":768},
""highlight"":{""description"":""target to show selected during the capture""},
""zoom_to"":{""description"":""'all' (default) | target (handle | {set} | {query:{layer,include_sublayers,...}} | array) | {min,max}""},
""margin"":{""type"":""number"",""minimum"":0,""maximum"":0.45,""default"":0.05,""description"":""fraction of each panel left free on each side""},
""only_layers"":{""description"":""layer name or array of names (sublayers included): only these are drawn"",""oneOf"":[{""type"":""string""},{""type"":""array"",""items"":{""type"":""string""}}]},
""isolate"":{""description"":""target: only these objects are drawn (union with only_layers)""},
""labels"":{""type"":""boolean"",""default"":true,""description"":""draw the view name in each panel""},
""show_grid"":{""type"":""boolean"",""default"":false}},""additionalProperties"":false}";
            }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            var view = doc.Views.ActiveView;
            if (view == null || view is RhinoPageView)
                return ToolResult.Fail("NO_VIEW", "The active view is not a model view", "Click a model viewport in Rhino and retry.");

            int W = a.Int("width", false, 1024, 32, 1600), H = a.Int("height", false, 768, 32, 1600);
            bool grid = a.Bool("show_grid", false);
            DisplayModeDescription mode = null;
            string dm = a.String("display_mode");
            if (dm != null) { string e; mode = ViewCommon.FindDisplayMode(dm, out e); if (e != null) a.Error(e); }
            List<Rhino.DocObjects.RhinoObject> hl = a.Has("highlight") ? ObjectQuery.TargetOf(ctx, args, a, "highlight") : null;
            double margin = a.Double("margin", false, 0.05);
            if (margin < 0 || margin > 0.45) a.Error("'margin' must be in [0, 0.45]");
            bool labels = a.Bool("labels", true);
            string visDesc;
            var keep = ViewCommon.ResolveKeep(ctx, args, a, out visDesc);
            Rhino.Geometry.BoundingBox bb; string zoomLabel;
            if (!ViewCommon.ResolveZoom(a, args, ctx, out bb, out zoomLabel))
            {
                if (a.HasErrors) return a.ErrorResult();
                int cnt; bb = ViewCommon.VisibleBBox(doc, keep, out cnt);
                zoomLabel = keep == null ? "all" : "isolated";
            }
            if (a.HasErrors) return a.ErrorResult();

            int w2 = W / 2, h2 = H / 2;
            var sheet = new Raster(W, H, 255);
            var panels = new List<object>();
            bool labelsFailed = false;
            for (int i = 0; i < 4; i++)
            {
                var s = new RenderSpec { ViewName = Order[i], Mode = mode, Grid = grid, Axes = false, Width = w2, Height = h2, Highlight = hl, Margin = margin, Keep = keep, Label = labels ? Order[i] : null };
                if (bb.IsValid) { s.HasZoom = true; s.ZoomBox = bb; s.ZoomLabel = zoomLabel; }
                string label, mn; object cam;
                byte[] png = Renderer.Render(ctx, view, s, a, false, out label, out cam, out mn);
                if (a.HasErrors) return a.ErrorResult();
                if (png == null) return ToolResult.Fail("CAPTURE_FAILED", "ViewCaptureToFile produced no file for " + Order[i], null, new { log = ctx.Logs });
                Raster r;
                try { r = Png.Decode(png); }
                catch (Exception ex) { return ToolResult.Fail("DECODE_FAILED", "Could not read Rhino's capture for " + Order[i] + ": " + ex.Message); }
                int x = (i % 2) * w2, y = (i / 2) * h2;
                sheet.Blit(r, x, y, w2, h2);
                sheet.Rect(x, y, w2, h2, 128, 128, 128);
                if (s.LabelFailed) labelsFailed = true;
                panels.Add(new { panel = Order[i], position = (i / 2 == 0 ? "top" : "bottom") + "-" + (i % 2 == 0 ? "left" : "right"), camera = cam,
                                 framing = s.Frame == null ? null : new { fill_x = s.Frame.FillX, fill_y = s.Frame.FillY } });
            }
            byte[] outPng = Png.Encode(sheet);
            var res = ToolResult.Ok(new
            {
                layout = "2x2 row-major: " + string.Join(", ", Order),
                panels = panels,
                display_mode = dm,
                zoom_to = zoomLabel,
                margin = margin,
                visible_only = visDesc,
                labels_drawn = labels && !labelsFailed,
                scene = ViewCommon.SceneInfo(doc, keep),
                image = new { mime = "image/png", width = W, height = H, bytes = outPng.Length }
            });
            res.AddImage("image/png", Convert.ToBase64String(outPng));
            return res;
        }
    }
}

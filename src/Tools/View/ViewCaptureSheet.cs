using System;
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
                return "One 2x2 PNG contact sheet of Top (top-left), Front (top-right), Right (bottom-left), Perspective (bottom-right), each zoomed to all. " +
                       "No text labels are drawn (no GDI+ in this runtime): the order is fixed and listed in the metadata. The active viewport is driven briefly and restored. " +
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
            List<Rhino.DocObjects.RhinoObject> hl = a.Has("highlight") ? a.Target("highlight", ctx) : null;
            if (a.HasErrors) return a.ErrorResult();

            int cnt; var bb = ViewCommon.VisibleBBox(doc, out cnt);
            int w2 = W / 2, h2 = H / 2;
            var sheet = new Raster(W, H, 255);
            var panels = new List<object>();
            for (int i = 0; i < 4; i++)
            {
                var s = new RenderSpec { ViewName = Order[i], Mode = mode, Grid = grid, Axes = false, Width = w2, Height = h2, Highlight = hl };
                if (bb.IsValid) { s.HasZoom = true; s.ZoomBox = bb; s.ZoomLabel = "all"; }
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
                panels.Add(new { panel = Order[i], position = (i / 2 == 0 ? "top" : "bottom") + "-" + (i % 2 == 0 ? "left" : "right"), camera = cam });
            }
            byte[] outPng = Png.Encode(sheet);
            var res = ToolResult.Ok(new
            {
                layout = "2x2 row-major: " + string.Join(", ", Order),
                panels = panels,
                display_mode = dm,
                scene = ViewCommon.SceneInfo(doc),
                image = new { mime = "image/png", width = W, height = H, bytes = outPng.Length }
            });
            res.AddImage("image/png", Convert.ToBase64String(outPng));
            return res;
        }
    }
}

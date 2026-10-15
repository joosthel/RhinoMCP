using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.View
{
    /// <summary>What to render: resolved, validated request shared by view_capture and view_capture_sheet.</summary>
    internal sealed class RenderSpec
    {
        public string ViewName = "active";            // active | standard name | named view | custom
        public JObject Camera;                        // custom camera
        public DisplayModeDescription Mode;           // null = keep the view's
        public bool HasZoom; public BoundingBox ZoomBox; public string ZoomLabel;
        public bool Grid, Axes;
        public int Width, Height;
        public bool Jpeg;
        public List<RhinoObject> Highlight;
    }

    internal static class Renderer
    {
        /// <summary>Applies the view part of the spec to a viewport (a copy or the live one). Returns an error message or null.</summary>
        public static string Apply(RhinoDoc doc, RhinoViewport vp, RenderSpec s, ArgReader a, out string viewLabel)
        {
            viewLabel = s.ViewName;
            DefinedViewportProjection proj; string canon;
            if (s.Camera != null)
            {
                viewLabel = "custom";
                if (!ViewCommon.ApplyCamera(vp, s.Camera, a, "view")) return a.ErrorResult().Message;
            }
            else if (ViewCommon.TryStdProjection(s.ViewName, out proj, out canon))
            {
                vp.SetProjection(proj, null, false);
                viewLabel = canon;
            }
            else if (!string.Equals(s.ViewName, "active", StringComparison.OrdinalIgnoreCase))
            {
                int idx = doc.NamedViews.FindByName(s.ViewName);
                if (idx < 0)
                {
                    var names = new List<string>();
                    for (int i = 0; i < doc.NamedViews.Count; i++) names.Add(doc.NamedViews[i].Name);
                    return "unknown view '" + s.ViewName + "'. Use active, " + string.Join("/", ViewCommon.StdNames) +
                           ", a named view (" + (names.Count == 0 ? "none saved" : string.Join(", ", names)) + ") or a camera object";
                }
                var vi = doc.NamedViews[idx];
                vp.SetViewProjection(vi.Viewport, true);
                viewLabel = vi.Name;
            }
            else viewLabel = vp.Name;
            if (s.Mode != null) vp.DisplayMode = s.Mode;
            if (s.HasZoom) vp.ZoomBoundingBox(s.ZoomBox);
            return null;
        }

        /// <summary>
        /// Captures with Rhino's ViewCaptureToFile on the live active viewport: the camera / display mode /
        /// selection are temporarily set, then ALWAYS restored (try/finally) and redrawn. Returns the encoded file bytes.
        /// </summary>
        public static byte[] Render(ToolContext ctx, RhinoView view, RenderSpec s, ArgReader a, bool jpeg, out string viewLabel, out object camera, out string modeName)
        {
            var doc = ctx.Doc;
            viewLabel = s.ViewName; camera = null; modeName = null;
            var sel = new SelectionGuard(doc);
            var state = new ViewState(doc, view);
            try
            {
                if (s.Highlight != null && s.Highlight.Count > 0) { state.MarkSelectionTouched(); sel.SelectOnly(s.Highlight.Select(o => o.Id)); }
                var vp = view.ActiveViewport;
                string err = Apply(doc, vp, s, a, out viewLabel);
                if (err != null) { if (!a.HasErrors) a.Error(err); return null; }
                camera = ViewCommon.CameraInfo(vp);
                modeName = ViewCommon.DisplayModeName(vp);
                view.Redraw();
                return ViewCommon.CaptureToBytes(s.Width, s.Height, jpeg, s.Grid, s.Axes, ctx);
            }
            finally
            {
                sel.Restore();
                state.Restore();
                foreach (var p in state.Problems) ctx.Log("restore: " + p);
            }
        }
    }

    /// <summary>Temporarily replaces the selection and puts the original one back.</summary>
    internal sealed class SelectionGuard
    {
        readonly RhinoDoc _doc; readonly Guid[] _orig; bool _touched;
        public SelectionGuard(RhinoDoc doc) { _doc = doc; _orig = doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id).ToArray(); }
        public void SelectOnly(IEnumerable<Guid> ids)
        {
            _touched = true;
            _doc.Objects.UnselectAll(false);
            _doc.Objects.Select(ids.ToList(), true);
        }
        public void Restore()
        {
            if (!_touched) return;
            try { _doc.Objects.UnselectAll(false); if (_orig.Length > 0) _doc.Objects.Select(_orig, true); } catch (Exception) { }
            try { _doc.Views.Redraw(); } catch (Exception) { }
        }
    }

    public sealed class ViewCaptureTool : ITool
    {
        public string Name { get { return "view_capture"; } }
        public string Description
        {
            get
            {
                return "Render an image of a view. The active viewport is driven briefly (visible flicker) and restored exactly afterwards." +
                       "view: active (default) | Top|Bottom|Left|Right|Front|Back|Perspective | a named view | {location,target,lens?}. " +
                       "Standard views default to zoom_to all. display_mode by English name (Wireframe, Shaded, Rendered, Ghosted, X-Ray, Technical, Artic, Pen). " +
                       "zoom_to: 'all' | target | {min,max}. highlight: target to show selected. Returns an image block plus camera/scene metadata.";
            }
        }
        public string InputSchema
        {
            get
            {
                return @"{""type"":""object"",""properties"":{
""view"":{""description"":""active | Top|Bottom|Left|Right|Front|Back|Perspective | named view name | {location:[x,y,z],target:[x,y,z],lens?:mm}"",""oneOf"":[{""type"":""string""},{""type"":""object"",""properties"":{""location"":{""$ref"":""#/$defs/point""},""target"":{""$ref"":""#/$defs/point""},""lens"":{""type"":""number""}},""required"":[""location"",""target""]}]},
""display_mode"":{""type"":""string"",""description"":""English display mode name, e.g. Wireframe, Shaded, Rendered, Ghosted, X-Ray, Technical, Artic, Pen""},
""width"":{""type"":""integer"",""minimum"":16,""default"":768,""description"":""width*height must stay within limits.max_capture_pixels (default 1600x1200 = 1.92 MP); larger requests fail with RESOURCE_LIMIT""},
""height"":{""type"":""integer"",""minimum"":16,""default"":512},
""format"":{""type"":""string"",""enum"":[""png""],""default"":""png"",""description"":""Only PNG is produced (no JPEG encoder without GDI+); jpeg is accepted and answered with PNG plus a warning""},
""zoom_to"":{""description"":""'all' | target | {min:[x,y,z],max:[x,y,z]}""},
""highlight"":{""description"":""target to show selected during the capture (selection is restored afterwards)""},
""show_grid"":{""type"":""boolean"",""default"":false},
""show_axes"":{""type"":""boolean"",""default"":false}},""additionalProperties"":false}";
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

            var s = new RenderSpec();
            JToken vt;
            if (args.TryGetValue("view", out vt) && vt.Type != JTokenType.Null)
            {
                if (vt.Type == JTokenType.String) s.ViewName = (string)vt;
                else if (vt is JObject) s.Camera = (JObject)vt;
                else a.Error("'view' must be a string or a camera object");
            }
            s.Width = a.Int("width", false, 768, 16, 100000);
            s.Height = a.Int("height", false, 512, 16, 100000);
            string fmt = (a.String("format", false, "png") ?? "png").ToLowerInvariant();
            if (fmt != "png" && fmt != "jpeg" && fmt != "jpg") a.Error("'format' must be png or jpeg");
            bool wantedJpeg = fmt != "png"; s.Jpeg = false;
            s.Grid = a.Bool("show_grid", false);
            s.Axes = a.Bool("show_axes", false);
            string dm = a.String("display_mode");
            if (dm != null)
            {
                string e; s.Mode = ViewCommon.FindDisplayMode(dm, out e);
                if (e != null) a.Error(e);
            }
            s.HasZoom = ViewCommon.ResolveZoom(a, args, ctx, out s.ZoomBox, out s.ZoomLabel);
            if (a.Has("highlight")) s.Highlight = ObjectQuery.TargetOf(ctx, args, a, "highlight");
            if (a.HasErrors) return a.ErrorResult();

            var blocked = Resources.CheckRss("view_capture");
            if (blocked != null) return blocked;
            long px = (long)s.Width * s.Height, maxPx = Resources.Limits.MaxCapturePixels;
            if (px > maxPx)
            {
                double f = Math.Sqrt((double)maxPx / px);
                int sw = Math.Max(16, (int)(s.Width * f)), sh = Math.Max(16, (int)(s.Height * f));
                return Resources.LimitFail("max_capture_pixels", px, maxPx,
                    "capture " + s.Width + "x" + s.Height + " = " + px + " px exceeds max_capture_pixels (" + maxPx + "). Nothing was rendered.",
                    "Capture at about " + sw + "x" + sh + " (same aspect) or smaller; 1024 px wide is plenty to judge a model. Use zoom_to for detail instead of more pixels.", false);
            }
            var guard = Resources.Begin("view_capture");

            DefinedViewportProjection p; string c;
            if (!s.HasZoom && s.Camera == null && ViewCommon.TryStdProjection(s.ViewName, out p, out c))
            {
                int cnt; var bb = ViewCommon.VisibleBBox(doc, out cnt);
                if (bb.IsValid) { s.HasZoom = true; s.ZoomBox = bb; s.ZoomLabel = "all (default)"; }
            }

            string viewLabel, modeName; object camera;
            byte[] bytes = Renderer.Render(ctx, view, s, a, s.Jpeg, out viewLabel, out camera, out modeName);
            if (a.HasErrors) return a.ErrorResult();
            if (bytes == null || bytes.Length == 0)
                return ToolResult.Fail("CAPTURE_FAILED", "ViewCaptureToFile produced no file", "Make sure the Rhino window is not minimized and retry", new { log = ctx.Logs });
            string mime = Png.IsPng(bytes) ? "image/png" : (Png.IsJpeg(bytes) ? "image/jpeg" : null);
            if (mime == null) return ToolResult.Fail("CAPTURE_FAILED", "Rhino wrote a file that is neither PNG nor JPEG");
            int iw, ih; Png.Size(bytes, out iw, out ih);
            var res = ToolResult.Ok(new
            {
                view = viewLabel,
                camera = camera,
                display_mode = modeName,
                zoom_to = s.ZoomLabel,
                scene = ViewCommon.SceneInfo(doc),
                image = new { mime = mime, width = iw, height = ih, bytes = bytes.Length },
                highlighted = s.Highlight == null ? 0 : s.Highlight.Count,
                note = "The active viewport was temporarily driven and has been restored."
            });
            foreach (var l in ctx.Logs) if (l.StartsWith("restore:")) res.Warn(l);
            if (wantedJpeg) res.Warn("jpeg is not available in this runtime; returned PNG");
            res.AddImage(mime, Convert.ToBase64String(bytes));
            return guard.Finish(res);
        }
    }
}

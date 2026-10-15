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
        public double Margin = 0.05;                  // fraction of the image left free on each side by zoom_to
        public HashSet<Guid> Keep;                    // only these objects are drawn (null = all)
        public string Label;                          // text drawn in the top-left corner (null = none)
        public FrameInfo Frame;                       // filled by Apply when zooming
        public bool LabelFailed;
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
            if (s.HasZoom) s.Frame = Framing.Zoom(vp, s.ZoomBox, s.Margin, s.Width, s.Height);
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
            CaptureConduit conduit = null;
            try
            {
                if (s.Keep != null || s.Label != null)
                {
                    conduit = new CaptureConduit { Keep = s.Keep, Label = s.Label };
                    conduit.Enabled = true;
                }
                if (s.Highlight != null && s.Highlight.Count > 0) { state.MarkSelectionTouched(); sel.SelectOnly(s.Highlight.Select(o => o.Id)); }
                var vp = view.ActiveViewport;
                string err = Apply(doc, vp, s, a, out viewLabel);
                if (err != null) { if (!a.HasErrors) a.Error(err); return null; }
                camera = ViewCommon.CameraInfo(vp);
                modeName = ViewCommon.DisplayModeName(vp);
                ViewCommon.Settle(view);
                var bytes = ViewCommon.CaptureToBytes(s.Width, s.Height, jpeg, s.Grid, s.Axes, ctx);
                if (conduit != null) s.LabelFailed = conduit.LabelFailed;
                return bytes;
            }
            finally
            {
                if (conduit != null) { try { conduit.Enabled = false; } catch (Exception) { } }
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
                       "Camera object: {location,target,lens?|fov_deg?,up?}. Lens is the 35 mm focal length relative to a 24 mm frame on the SHORT side of the image (50 mm = 27 deg across the short side); " +
                       "fov_deg is that same short-side field of view (lens = 12/tan(fov/2)); give one of them. up = roll: the image 'up' direction (default world Z). " +
                       "Standard views default to zoom_to all. display_mode: case-insensitive English name (Wireframe, Shaded, Rendered, Ghosted, X-Ray, Technical, Artistic, Pen, Arctic, Raytraced; aliases like Artic, XRay); unknown names list the valid ones. " +
                       "zoom_to: 'all' (with only_layers/isolate: everything drawn) | any target (handle, set, {query:{layer,include_sublayers,...}}) | {min,max}; framed tightly (parallel and perspective) leaving margin (fraction of the image per side, default 0.05); the result's framing.fill_x/fill_y give the fraction of the image width/height the box covers. " +
                       "only_layers: layer names (full path or leaf, sublayers included) and/or isolate: target; everything else is hidden during the capture only (nothing in the document changes); both = union. " +
                       "highlight: target to show selected. Returns an image block plus camera/scene metadata.";
            }
        }
        public string InputSchema
        {
            get
            {
                return @"{""type"":""object"",""properties"":{
""view"":{""description"":""active | Top|Bottom|Left|Right|Front|Back|Perspective | named view name | {location:[x,y,z],target:[x,y,z],lens?:mm (35mm, 24mm frame on the short side),fov_deg?:short-side field of view,up?:[x,y,z] roll}"",""oneOf"":[{""type"":""string""},{""type"":""object"",""properties"":{""location"":{""$ref"":""#/$defs/point""},""target"":{""$ref"":""#/$defs/point""},""lens"":{""type"":""number""},""fov_deg"":{""type"":""number""},""up"":{""$ref"":""#/$defs/point""}},""required"":[""location"",""target""]}]},
""display_mode"":{""type"":""string"",""description"":""Case-insensitive English display mode name, e.g. Wireframe, Shaded, Rendered, Ghosted, X-Ray, Technical, Artistic, Pen, Arctic, Raytraced (aliases Artic, XRay accepted)""},
""width"":{""type"":""integer"",""minimum"":16,""default"":768,""description"":""width*height must stay within limits.max_capture_pixels (default 1600x1200 = 1.92 MP); larger requests fail with RESOURCE_LIMIT""},
""height"":{""type"":""integer"",""minimum"":16,""default"":512},
""format"":{""type"":""string"",""enum"":[""png""],""default"":""png"",""description"":""Only PNG is produced (no JPEG encoder without GDI+); jpeg is accepted and answered with PNG plus a warning""},
""zoom_to"":{""description"":""'all' | target (handle | {set} | {query:{layer,include_sublayers,...}} | array) | {min:[x,y,z],max:[x,y,z]}""},
""margin"":{""type"":""number"",""minimum"":0,""maximum"":0.45,""default"":0.05,""description"":""zoom_to: fraction of the image left free on each side""},
""only_layers"":{""description"":""layer name or array of names (full path or leaf; sublayers included): only these are drawn during the capture"",""oneOf"":[{""type"":""string""},{""type"":""array"",""items"":{""type"":""string""}}]},
""isolate"":{""description"":""target: only these objects are drawn during the capture (union with only_layers)""},
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
            s.Margin = a.Double("margin", false, 0.05);
            if (s.Margin < 0 || s.Margin > 0.45) a.Error("'margin' must be in [0, 0.45]");
            if (a.Has("highlight")) s.Highlight = ObjectQuery.TargetOf(ctx, args, a, "highlight");
            string visDesc;
            s.Keep = ViewCommon.ResolveKeep(ctx, args, a, out visDesc);
            s.HasZoom = ViewCommon.ResolveZoom(a, args, ctx, out s.ZoomBox, out s.ZoomLabel, s.Keep);
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
                int cnt; var bb = ViewCommon.VisibleBBox(doc, s.Keep, out cnt);
                if (bb.IsValid) { s.HasZoom = true; s.ZoomBox = bb; s.ZoomLabel = s.Keep == null ? "all (default)" : "isolated (default)"; }
            }

            string viewLabel, modeName; object camera;
            byte[] bytes;
            bool windowRestored;
            // A minimized Rhino window cannot draw or capture: restore it for the capture only and minimize it again afterwards.
            using (var lease = RhinoMCP.Tools.Script.WindowLease.Acquire())
            {
                if (lease.Error != null)
                    return ToolResult.Fail("WINDOW_UNAVAILABLE", "Cannot capture: " + lease.Error,
                        "Restore or unhide the Rhino window (click its Dock icon) and retry. system_status shows the window state.", new { window = RhinoMCP.Tools.Script.MacWindow.State() });
                windowRestored = lease.Restored;
                bytes = Renderer.Render(ctx, view, s, a, s.Jpeg, out viewLabel, out camera, out modeName);
            }
            if (a.HasErrors) return a.ErrorResult();
            if (bytes == null || bytes.Length == 0)
                return ToolResult.Fail("CAPTURE_FAILED", "ViewCaptureToFile produced no file", "Rhino reported no error and the window looks usable; retry once, then check system_status (window state) and Rhino's command history", new { log = ctx.Logs, window = RhinoMCP.Tools.Script.MacWindow.State() });
            string mime = Png.IsPng(bytes) ? "image/png" : (Png.IsJpeg(bytes) ? "image/jpeg" : null);
            if (mime == null) return ToolResult.Fail("CAPTURE_FAILED", "Rhino wrote a file that is neither PNG nor JPEG");
            int iw, ih; Png.Size(bytes, out iw, out ih);
            var res = ToolResult.Ok(new
            {
                view = viewLabel,
                camera = camera,
                display_mode = modeName,
                zoom_to = s.ZoomLabel,
                framing = s.Frame == null ? null : new { margin = s.Frame.Margin, fill_x = s.Frame.FillX, fill_y = s.Frame.FillY },
                visible_only = visDesc,
                scene = ViewCommon.SceneInfo(doc, s.Keep),
                image = new { mime = mime, width = iw, height = ih, bytes = bytes.Length },
                highlighted = s.Highlight == null ? 0 : s.Highlight.Count,
                note = "The active viewport was temporarily driven and has been restored."
            });
            foreach (var l in ctx.Logs) if (l.StartsWith("restore:")) res.Warn(l);
            if (windowRestored) res.Warn("the Rhino window was minimized: it was restored for the capture and minimized again");
            if (wantedJpeg) res.Warn("jpeg is not available in this runtime; returned PNG");
            res.AddImage(mime, Convert.ToBase64String(bytes));
            return guard.Finish(res);
        }
    }
}

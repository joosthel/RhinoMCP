using System;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;

namespace RhinoMCP.Tools.View
{
    /// <summary>
    /// Intentionally changes the user's active viewport. Registered as Read because camera changes are not
    /// undo-recorded and do not modify the document; use views_named save first if the old view matters.
    /// </summary>
    public sealed class ViewSetTool : ITool
    {
        public string Name { get { return "view_set"; } }
        public string Description
        {
            get
            {
                return "Change the user's ACTIVE viewport (visible to the user; not undoable, document untouched). " +
                       "Give any of: projection (Top|Bottom|Left|Right|Front|Back|Perspective), named_view, camera {location,target,lens?}, " +
                       "display_mode (English name), zoom_to ('all' | target | {min,max}). Applied in that order. Returns the resulting view. " +
                       "Use view_capture instead to just look. Save the current view first with views_named if you want to return to it.";
            }
        }
        public string InputSchema
        {
            get
            {
                return @"{""type"":""object"",""properties"":{
""projection"":{""type"":""string"",""enum"":[""Top"",""Bottom"",""Left"",""Right"",""Front"",""Back"",""Perspective""]},
""named_view"":{""type"":""string""},
""camera"":{""type"":""object"",""properties"":{""location"":{""$ref"":""#/$defs/point""},""target"":{""$ref"":""#/$defs/point""},""lens"":{""type"":""number""}},""required"":[""location"",""target""]},
""display_mode"":{""type"":""string""},
""zoom_to"":{""description"":""'all' | target | {min:[x,y,z],max:[x,y,z]}""}},""additionalProperties"":false}";
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

            string proj = a.String("projection"), named = a.String("named_view");
            DefinedViewportProjection dp = DefinedViewportProjection.Perspective; string canon = null;
            if (proj != null && !ViewCommon.TryStdProjection(proj, out dp, out canon))
                a.Error("'projection' must be one of: " + string.Join(", ", ViewCommon.StdNames));
            int namedIdx = -1;
            if (named != null)
            {
                namedIdx = doc.NamedViews.FindByName(named);
                if (namedIdx < 0) a.Error("named view '" + named + "' not found (see views_named list)");
            }
            JObject cam = args["camera"] as JObject;
            if (a.Has("camera") && cam == null) a.Error("'camera' must be {location,target,lens?}");
            DisplayModeDescription mode = null;
            string dm = a.String("display_mode");
            if (dm != null) { string e; mode = ViewCommon.FindDisplayMode(dm, out e); if (e != null) a.Error(e); }
            Rhino.Geometry.BoundingBox zb; string zl;
            bool zoom = ViewCommon.ResolveZoom(a, args, ctx, out zb, out zl);
            if (proj == null && named == null && cam == null && mode == null && !zoom && !a.HasErrors)
                a.Error("nothing to do: give projection, named_view, camera, display_mode or zoom_to");
            if (a.HasErrors) return a.ErrorResult();

            var vp = view.ActiveViewport;
            if (canon != null) vp.SetProjection(dp, null, false);
            if (namedIdx >= 0) { string keep = vp.Name; doc.NamedViews.Restore(namedIdx, vp); if (vp.Name != keep) vp.Name = keep; }
            if (cam != null && !ViewCommon.ApplyCamera(vp, cam, a, "camera")) return a.ErrorResult();
            if (mode != null) vp.DisplayMode = mode;
            if (zoom) vp.ZoomBoundingBox(zb);
            view.Redraw();

            return ToolResult.Ok(new
            {
                view = vp.Name,
                camera = ViewCommon.CameraInfo(vp),
                display_mode = ViewCommon.DisplayModeName(vp),
                zoom_to = zl,
                note = "The active viewport was changed. This is not recorded in undo."
            });
        }
    }
}

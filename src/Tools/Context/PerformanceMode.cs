using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;
using RhinoMCP.Tools.View;

namespace RhinoMCP.Tools.Context
{
    /// <summary>Lighter meshing and display for big models. Previous settings live in doc.Strings so "off" can restore them.</summary>
    public sealed class PerformanceModeTool : ITool
    {
        const string Key = "rhinomcp.perfmode";
        const string CustomDataKey = "rhinomcp.perfmode.custom";

        public string Name { get { return "performance_mode"; } }
        public ToolKind Kind { get { return ToolKind.Mutate; } }
        public string Description
        {
            get
            {
                return "Keep Rhino responsive on heavy models. action on: document render-mesh quality to Fast (jagged, coarse) and the active model viewport to a light display mode " +
                       "(display_mode, default Shaded; kept when already Wireframe/Shaded/Technical/Pen, skipped on page views). The previous settings are stored in the document. " +
                       "off: restores them. status: shows whether the mode is on. Existing render meshes are regenerated lazily by Rhino. Returns what changed. " +
                       "This changes how the model LOOKS (coarser shading), not its geometry.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"action\":{\"type\":\"string\",\"enum\":[\"on\",\"off\",\"status\"]}," +
                    "\"display_mode\":{\"type\":\"string\",\"description\":\"display mode for 'on' (default Shaded; Wireframe is lightest)\"}",
                    "\"action\"");
            }
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            string action = a.String("action", true);
            string dmName = a.String("display_mode", false, "Shaded");
            if (action != null && action != "on" && action != "off" && action != "status") a.Error("'action' must be on, off or status");
            if (a.HasErrors) return a.ErrorResult();

            string stored = doc.Strings.GetValue(Key);
            JObject prev = null;
            if (!string.IsNullOrEmpty(stored)) { try { prev = JObject.Parse(stored); } catch (Exception) { prev = null; } }
            var changed = new List<string>();
            var warns = new List<string>();

            if (action == "status")
                return ToolResult.Ok(new { on = prev != null, meshing_style = doc.MeshingParameterStyle.ToString(), stored = prev });

            if (action == "on")
            {
                if (prev != null)
                    return ToolResult.Ok(new { on = true, changed = new string[0], note = "already on; call action off to restore the stored settings", stored = prev });
                string dmErr; DisplayModeDescription target = ViewCommon.FindDisplayMode(dmName, out dmErr);
                if (dmErr != null) return ToolResult.Fail("INVALID_ARGS", dmErr, "Pass an English display mode name such as Shaded or Wireframe.");

                var state = new JObject();
                var style = doc.MeshingParameterStyle;
                state["meshing_style"] = style.ToString();
                if (style == MeshingParameterStyle.Custom)
                {
                    try { AppDomain.CurrentDomain.SetData(CustomDataKey, doc.GetMeshingParameters(MeshingParameterStyle.Custom)); }
                    catch (Exception) { warns.Add("custom meshing parameters could not be remembered; 'off' will set Quality"); }
                }
                var view = doc.Views.ActiveView;
                if (view != null && !(view is RhinoPageView))
                {
                    var vp = view.ActiveViewport;
                    var cur = vp.DisplayMode;
                    state["view_id"] = view.ActiveViewportID.ToString();
                    state["display_mode_id"] = cur == null ? "" : cur.Id.ToString();
                    state["display_mode"] = cur == null ? "" : cur.EnglishName;
                    if (cur != null && IsLight(cur.EnglishName)) changed.Add("display mode kept (" + cur.EnglishName + " is already light)");
                    else if (target != null)
                    {
                        vp.DisplayMode = target;
                        changed.Add("display mode " + (cur == null ? "?" : cur.EnglishName) + " -> " + target.EnglishName);
                    }
                }
                else warns.Add("active view is not a model viewport; display mode unchanged");
                try
                {
                    doc.MeshingParameterStyle = MeshingParameterStyle.Fast;
                    changed.Add("render mesh quality " + style + " -> Fast (jagged, coarse)");
                }
                catch (Exception ex) { warns.Add("could not set meshing style: " + ex.Message); }
                doc.Strings.SetString(Key, state.ToString(Newtonsoft.Json.Formatting.None));
                try { doc.Views.Redraw(); } catch (Exception) { }
                var r = ToolResult.Ok(new { on = true, changed = changed, stored = state });
                foreach (var w in warns) r.Warn(w);
                return r;
            }

            // off
            if (prev == null)
                return ToolResult.Ok(new { on = false, changed = new string[0], note = "performance mode was not on; nothing to restore" });
            string ms = (string)prev["meshing_style"];
            MeshingParameterStyle old;
            if (ms != null && Enum.TryParse(ms, out old) && old != MeshingParameterStyle.None)
            {
                try
                {
                    if (old == MeshingParameterStyle.Custom)
                    {
                        var mp = AppDomain.CurrentDomain.GetData(CustomDataKey) as MeshingParameters;
                        if (mp != null) { doc.SetCustomMeshingParameters(mp); changed.Add("render mesh quality restored to Custom"); }
                        else { doc.MeshingParameterStyle = MeshingParameterStyle.Quality; warns.Add("custom meshing parameters were lost (Rhino/tools reloaded); set to Quality instead"); changed.Add("render mesh quality -> Quality"); }
                    }
                    else { doc.MeshingParameterStyle = old; changed.Add("render mesh quality restored to " + old); }
                }
                catch (Exception ex) { warns.Add("could not restore meshing style: " + ex.Message); }
            }
            string idStr = (string)prev["display_mode_id"], vidStr = (string)prev["view_id"];
            Guid modeId, viewId;
            if (!string.IsNullOrEmpty(idStr) && Guid.TryParse(idStr, out modeId))
            {
                RhinoView view = null;
                if (Guid.TryParse(vidStr ?? "", out viewId)) view = doc.Views.Find(viewId);
                if (view == null) view = doc.Views.ActiveView;
                var d = DisplayModeDescription.GetDisplayModes().FirstOrDefault(m => m.Id == modeId);
                if (view != null && !(view is RhinoPageView) && d != null)
                {
                    if (view.ActiveViewport.DisplayMode == null || view.ActiveViewport.DisplayMode.Id != d.Id)
                    {
                        view.ActiveViewport.DisplayMode = d;
                        changed.Add("display mode restored to " + d.EnglishName);
                    }
                }
                else if (d == null) warns.Add("previous display mode no longer exists");
            }
            doc.Strings.Delete(Key);
            try { AppDomain.CurrentDomain.SetData(CustomDataKey, null); } catch (Exception) { }
            try { doc.Views.Redraw(); } catch (Exception) { }
            var res = ToolResult.Ok(new { on = false, changed = changed });
            foreach (var w in warns) res.Warn(w);
            return res;
        }

        static bool IsLight(string name)
        {
            if (name == null) return false;
            return name == "Wireframe" || name == "Shaded" || name == "Technical" || name == "Pen";
        }
    }
}

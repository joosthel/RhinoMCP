using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCP.Tools.View
{
    /// <summary>Shared helpers for the view tools: view/camera resolution, display modes, bitmap encoding, metadata.</summary>
    internal static class ViewCommon
    {
        public static readonly string[] StdNames = { "Top", "Bottom", "Left", "Right", "Front", "Back", "Perspective" };

        public static double[] Pt(Point3d p) { return new[] { Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4) }; }
        public static double[] Vec(Vector3d p) { return new[] { Math.Round(p.X, 5), Math.Round(p.Y, 5), Math.Round(p.Z, 5) }; }

        public static bool TryStdProjection(string s, out DefinedViewportProjection proj, out string canonical)
        {
            proj = DefinedViewportProjection.Perspective; canonical = null;
            if (s == null) return false;
            foreach (var n in StdNames)
                if (string.Equals(n, s.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    canonical = n;
                    proj = (DefinedViewportProjection)Enum.Parse(typeof(DefinedViewportProjection), n);
                    return true;
                }
            return false;
        }

        static string Norm(string s) { return new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant(); }

        public static DisplayModeDescription FindDisplayMode(string name, out string error)
        {
            error = null;
            var all = DisplayModeDescription.GetDisplayModes() ?? new DisplayModeDescription[0];
            string n = Norm(name);
            if (n == "arctic") n = "artic"; // Rhino 7 spells it "Artic"
            var hit = all.FirstOrDefault(d => Norm(d.EnglishName) == n) ?? all.FirstOrDefault(d => Norm(d.LocalName) == n);
            if (hit == null)
                error = "unknown display_mode '" + name + "'. Available: " + string.Join(", ", all.Select(d => d.EnglishName));
            return hit;
        }

        /// <summary>Bounding box of visible, normal/locked objects (what a user sees).</summary>
        public static BoundingBox VisibleBBox(RhinoDoc doc, out int count)
        {
            var bb = BoundingBox.Empty;
            count = 0;
            var s = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = false, VisibleFilter = true };
            foreach (var o in doc.Objects.GetObjectList(s))
            {
                count++;
                var b = o.Geometry.GetBoundingBox(true);
                if (b.IsValid) bb.Union(b);
            }
            return bb;
        }

        public static BoundingBox ReadBBox(JToken t, ArgReader a)
        {
            var o = t as JObject;
            JArray mn = o == null ? null : o["min"] as JArray, mx = o == null ? null : o["max"] as JArray;
            if (mn == null || mx == null || mn.Count != 3 || mx.Count != 3)
            { a.Error("'zoom_to' bbox must be {min:[x,y,z],max:[x,y,z]}"); return BoundingBox.Empty; }
            try
            {
                return new BoundingBox((double)mn[0], (double)mn[1], (double)mn[2], (double)mx[0], (double)mx[1], (double)mx[2]);
            }
            catch (Exception) { a.Error("'zoom_to' bbox must contain numbers"); return BoundingBox.Empty; }
        }

        /// <summary>Resolves zoom_to ("all" | target | {min,max}) to a box. False when not requested or on error.</summary>
        public static bool ResolveZoom(ArgReader a, JObject args, ToolContext ctx, out BoundingBox bb, out string label)
        {
            bb = BoundingBox.Empty; label = null;
            JToken z;
            if (!args.TryGetValue("zoom_to", out z) || z.Type == JTokenType.Null) return false;
            if (z.Type == JTokenType.String && string.Equals((string)z, "all", StringComparison.OrdinalIgnoreCase))
            {
                int c; bb = VisibleBBox(ctx.Doc, out c); label = "all";
                if (!bb.IsValid) a.Error("zoom_to 'all': the document has no visible objects");
                return bb.IsValid;
            }
            if (z is JObject && ((JObject)z)["min"] != null)
            {
                bb = ReadBBox(z, a); label = "bbox"; return bb.IsValid;
            }
            var objs = a.Target("zoom_to", ctx);
            if (a.HasErrors) return false;
            foreach (var o in objs) { var b = o.Geometry.GetBoundingBox(true); if (b.IsValid) bb.Union(b); }
            label = "target(" + objs.Count + ")";
            if (!bb.IsValid) a.Error("zoom_to target has no valid bounding box");
            return bb.IsValid;
        }

        /// <summary>Applies a custom camera {location,target,lens} to a viewport.</summary>
        public static bool ApplyCamera(RhinoViewport vp, JObject cam, ArgReader a, string argName)
        {
            JArray loc = cam["location"] as JArray, tgt = cam["target"] as JArray;
            if (loc == null || loc.Count != 3 || tgt == null || tgt.Count != 3)
            { a.Error("'" + argName + "' must be {location:[x,y,z], target:[x,y,z], lens?:mm}"); return false; }
            Point3d l, t;
            try
            {
                l = new Point3d((double)loc[0], (double)loc[1], (double)loc[2]);
                t = new Point3d((double)tgt[0], (double)tgt[1], (double)tgt[2]);
            }
            catch (Exception) { a.Error("'" + argName + "' location/target must be numbers"); return false; }
            if (l.DistanceTo(t) < 1e-9) { a.Error("'" + argName + "' location and target coincide"); return false; }
            JToken lensTok = cam["lens"];
            double lens = 0;
            if (lensTok != null && lensTok.Type != JTokenType.Null)
            {
                if ((lensTok.Type != JTokenType.Integer && lensTok.Type != JTokenType.Float) || (double)lensTok < 5 || (double)lensTok > 1000)
                { a.Error("'" + argName + ".lens' must be a 35mm focal length in [5..1000]"); return false; }
                lens = (double)lensTok;
            }
            if (vp.IsParallelProjection) vp.ChangeToPerspectiveProjection(true, lens > 0 ? lens : 50.0);
            var dir = t - l;
            dir.Unitize();
            var up = Vector3d.ZAxis;
            if (Math.Abs(dir * up) > 0.9999) up = Vector3d.YAxis;
            var info = new ViewportInfo(vp);
            info.SetCameraUp(up);
            vp.SetViewProjection(info, true);
            vp.SetCameraLocations(t, l);
            if (lens > 0) vp.Camera35mmLensLength = lens;
            return true;
        }

        public static object CameraInfo(RhinoViewport vp)
        {
            return new
            {
                projection = vp.IsParallelProjection ? "parallel" : "perspective",
                location = Pt(vp.CameraLocation),
                target = Pt(vp.CameraTarget),
                direction = Vec(vp.CameraDirection),
                up = Vec(vp.CameraUp),
                lens_mm = vp.IsParallelProjection ? (double?)null : Math.Round(vp.Camera35mmLensLength, 2)
            };
        }

        public static object SceneInfo(RhinoDoc doc)
        {
            int count;
            var bb = VisibleBBox(doc, out count);
            return new { bbox = bb.IsValid ? new { min = Pt(bb.Min), max = Pt(bb.Max) } : null, visible_objects = count };
        }

        public static string DisplayModeName(RhinoViewport vp)
        {
            var d = vp.DisplayMode;
            return d == null ? null : d.EnglishName;
        }

        /// <summary>
        /// Captures the ACTIVE viewport as it currently is to a temp file with Rhino's own ViewCaptureToFile command
        /// (no System.Drawing/GDI+ involved) and returns the file bytes. The file is always deleted.
        /// </summary>
        public static byte[] CaptureToBytes(int w, int h, bool jpeg, bool grid, bool axes, ToolContext ctx)
        {
            string path = Path.Combine(Path.GetTempPath(), "rhinomcp_view_" + Guid.NewGuid().ToString("N") + (jpeg ? ".jpg" : ".png"));
            try
            {
                // Options must come BEFORE the file name: giving the name first saves immediately with the sticky defaults.
                string cmd = "-_ViewCaptureToFile _LockAspectRatio=No _Width=" + w + " _Height=" + h +
                             " _DrawGrid=" + (grid ? "Yes" : "No") + " _DrawWorldAxes=" + (axes ? "Yes" : "No") +
                             " _DrawCPlaneAxes=No _TransparentBackground=No \"" + path + "\"";
                bool ok = RhinoApp.RunScript(cmd, false);
                ctx.Log("capture cmd ok=" + ok);
                if (!File.Exists(path)) return null;
                return File.ReadAllBytes(path);
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
            }
        }
    }

    /// <summary>Snapshot of everything a capture tool may touch on a live view, restorable in a finally block.</summary>
    internal sealed class ViewState
    {
        readonly RhinoView _view;
        readonly ViewportInfo _info;
        readonly Guid _modeId;
        readonly Point3d _target;
        readonly string _name;
        readonly bool _grid, _axes;
        readonly Guid[] _selected;
        readonly RhinoDoc _doc;
        bool _selTouched;
        public readonly List<string> Problems = new List<string>();

        public ViewState(RhinoDoc doc, RhinoView view)
        {
            _doc = doc; _view = view;
            var vp = view.ActiveViewport;
            _info = new ViewportInfo(vp);
            _target = vp.CameraTarget;
            _name = vp.Name;
            _modeId = vp.DisplayMode == null ? Guid.Empty : vp.DisplayMode.Id;
            _grid = vp.ConstructionGridVisible;
            _axes = vp.WorldAxesVisible;
            _selected = doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id).ToArray();
        }

        public void MarkSelectionTouched() { _selTouched = true; }

        public void Restore()
        {
            var vp = _view.ActiveViewport;
            try { vp.SetViewProjection(_info, false); vp.SetCameraTarget(_target, false); } catch (Exception ex) { Problems.Add("camera: " + ex.Message); }
            try { if (vp.Name != _name) vp.Name = _name; } catch (Exception ex) { Problems.Add("name: " + ex.Message); }
            try
            {
                if (_modeId != Guid.Empty && (vp.DisplayMode == null || vp.DisplayMode.Id != _modeId))
                {
                    var d = DisplayModeDescription.GetDisplayModes().FirstOrDefault(m => m.Id == _modeId);
                    if (d != null) vp.DisplayMode = d;
                    if (vp.DisplayMode == null || vp.DisplayMode.Id != _modeId) Problems.Add("display mode not restored");
                }
            }
            catch (Exception ex) { Problems.Add("display mode: " + ex.Message); }
            try { vp.ConstructionGridVisible = _grid; vp.WorldAxesVisible = _axes; } catch (Exception ex) { Problems.Add("grid: " + ex.Message); }
            if (_selTouched)
            {
                try { _doc.Objects.UnselectAll(false); if (_selected.Length > 0) _doc.Objects.Select(_selected, true); } catch (Exception ex) { Problems.Add("selection: " + ex.Message); }
            }
            try { _view.Redraw(); } catch (Exception) { }
        }
    }
}

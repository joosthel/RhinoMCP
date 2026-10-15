using System;
using RhinoMCP.Tools.Shared;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

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

        /// <summary>English names of all display modes installed in this Rhino.</summary>
        public static string[] DisplayModeNames()
        {
            var all = DisplayModeDescription.GetDisplayModes() ?? new DisplayModeDescription[0];
            return all.Select(d => d.EnglishName).ToArray();
        }

        // Alias (normalized) -> normalized candidate English names, tried in order. Rhino 7 spells the mode "Arctic" (older builds "Artic").
        static readonly Dictionary<string, string[]> ModeAliases = new Dictionary<string, string[]>
        {
            { "arctic", new[] { "arctic", "artic" } }, { "artic", new[] { "arctic", "artic" } },
            { "xray", new[] { "xray" } }, { "xray2", new[] { "xray" } },
            { "render", new[] { "rendered" } }, { "rendered", new[] { "rendered" } },
            { "raytrace", new[] { "raytraced" } }, { "raytraced", new[] { "raytraced" } },
            { "tech", new[] { "technical" } }, { "technical", new[] { "technical" } },
            { "ghost", new[] { "ghosted" } }, { "ghosted", new[] { "ghosted" } },
            { "wire", new[] { "wireframe" } }, { "wireframe", new[] { "wireframe" } },
            { "shade", new[] { "shaded" } }, { "shaded", new[] { "shaded" } },
            { "pen", new[] { "pen" } }, { "sketch", new[] { "pen" } },
            { "artistic", new[] { "artistic" } }
        };

        /// <summary>Case-insensitive display mode lookup by English (or local) name, with common aliases. Unknown names list the valid ones.</summary>
        public static DisplayModeDescription FindDisplayMode(string name, out string error)
        {
            error = null;
            var all = DisplayModeDescription.GetDisplayModes() ?? new DisplayModeDescription[0];
            string n = Norm(name);
            var cands = new List<string> { n };
            string[] extra;
            if (ModeAliases.TryGetValue(n, out extra)) cands.AddRange(extra);
            DisplayModeDescription hit = null;
            foreach (var cn in cands)
            {
                hit = all.FirstOrDefault(d => Norm(d.EnglishName) == cn) ?? all.FirstOrDefault(d => Norm(d.LocalName) == cn);
                if (hit != null) break;
            }
            if (hit == null)
                error = "unknown display_mode '" + name + "'. Valid (case-insensitive): " + string.Join(", ", all.Select(d => d.EnglishName)) +
                        ". Aliases: Artic=Arctic, XRay=X-Ray, Render=Rendered, Wire=Wireframe, Ghost=Ghosted, Tech=Technical, Sketch=Pen";
            return hit;
        }

        /// <summary>
        /// Makes the view really draw its current state before a capture reads it. Root cause of the "display_mode is ignored" bug:
        /// on Rhino 7 Mac a viewport redraw is asynchronous, and -_ViewCaptureToFile (which has no display-mode option of its own)
        /// renders whatever the viewport last drew, so the captured frame showed the previous call's mode. RhinoApp.Wait() alone is not
        /// enough; a short sleep between redraws lets the draw finish (verified: byte-identical frames per mode over repeated calls).
        /// </summary>
        public static void Settle(RhinoView view)
        {
            // Adaptive: count real frames of this viewport through a display conduit and wait (bounded) for them, instead of fixed
            // sleeps that were too short under load. Needs two consecutive completed frames after the last state change.
            var counter = new FrameCounter { ViewportId = view.ActiveViewportID };
            int drawn = 0;
            try
            {
                counter.Enabled = true;
                for (int pass = 0; pass < 5 && drawn < 2; pass++)
                {
                    long before = counter.Count;
                    try { view.Redraw(); } catch (Exception) { }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (counter.Count == before && sw.ElapsedMilliseconds < 2000)
                    {
                        try { RhinoApp.Wait(); } catch (Exception) { }
                        System.Threading.Thread.Sleep(10);
                    }
                    if (counter.Count > before) drawn++;
                }
            }
            catch (Exception) { }
            finally { try { counter.Enabled = false; } catch (Exception) { } }
            if (drawn < 2)
            {
                // no frame callbacks seen (window not drawing?): fall back to the fixed schedule
                for (int i = 0; i < 2; i++)
                {
                    try { view.Redraw(); } catch (Exception) { }
                    try { RhinoApp.Wait(); } catch (Exception) { }
                    System.Threading.Thread.Sleep(150);
                }
            }
            else
            {
                try { RhinoApp.Wait(); } catch (Exception) { }
                System.Threading.Thread.Sleep(150);  // the callback fires before the frame is presented; give the flush time
            }
        }

        sealed class FrameCounter : DisplayConduit
        {
            public Guid ViewportId;
            long _count;
            public long Count { get { return System.Threading.Interlocked.Read(ref _count); } }
            protected override void DrawForeground(DrawEventArgs e)
            {
                try { if (e.Viewport != null && e.Viewport.Id == ViewportId) System.Threading.Interlocked.Increment(ref _count); }
                catch (Exception) { }
            }
        }

        /// <summary>Bounding box of visible, normal/locked objects (what a user sees).</summary>
        public static BoundingBox VisibleBBox(RhinoDoc doc, out int count) { return VisibleBBox(doc, null, out count); }

        /// <summary>Same, restricted to the keep-set when given.</summary>
        public static BoundingBox VisibleBBox(RhinoDoc doc, HashSet<Guid> keep, out int count)
        {
            var bb = BoundingBox.Empty;
            count = 0;
            var s = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = false, VisibleFilter = true };
            foreach (var o in doc.Objects.GetObjectList(s))
            {
                if (keep != null && !keep.Contains(o.Id)) continue;
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
        public static bool ResolveZoom(ArgReader a, JObject args, ToolContext ctx, out BoundingBox bb, out string label, HashSet<Guid> keep = null)
        {
            bb = BoundingBox.Empty; label = null;
            JToken z;
            if (!args.TryGetValue("zoom_to", out z) || z.Type == JTokenType.Null) return false;
            if (z.Type == JTokenType.String && string.Equals((string)z, "all", StringComparison.OrdinalIgnoreCase))
            {
                // 'all' under only_layers/isolate means everything that is drawn, not the whole document (else the subject is a speck).
                int c; bb = VisibleBBox(ctx.Doc, keep, out c); label = keep == null ? "all" : "all (isolated)";
                // An empty document is not an error: capture/set the view without zooming.
                if (!bb.IsValid) label = keep == null ? "all (empty document, view not zoomed)" : "all (nothing visible, view not zoomed)";
                return bb.IsValid;
            }
            if (z is JObject && ((JObject)z)["min"] != null)
            {
                bb = ReadBBox(z, a); label = "bbox"; return bb.IsValid;
            }
            var objs = ObjectQuery.TargetOf(ctx, args, a, "zoom_to");
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
            JToken lensTok = cam["lens"], fovTok = cam["fov_deg"];
            double lens = 0;
            bool hasLens = lensTok != null && lensTok.Type != JTokenType.Null, hasFov = fovTok != null && fovTok.Type != JTokenType.Null;
            if (hasLens && hasFov) { a.Error("'" + argName + "': give lens OR fov_deg, not both"); return false; }
            if (hasLens)
            {
                if ((lensTok.Type != JTokenType.Integer && lensTok.Type != JTokenType.Float) || (double)lensTok < 5 || (double)lensTok > 1000)
                { a.Error("'" + argName + ".lens' must be a 35mm focal length in [5..1000]"); return false; }
                lens = (double)lensTok;
            }
            if (hasFov)
            {
                if (fovTok.Type != JTokenType.Integer && fovTok.Type != JTokenType.Float)
                { a.Error("'" + argName + ".fov_deg' must be a number"); return false; }
                lens = LensFromFov((double)fovTok);
                if (lens < 5 || lens > 1000)
                { a.Error("'" + argName + ".fov_deg' must be between " + Math.Round(FovFromLens(1000), 2) + " and " + Math.Round(FovFromLens(5), 1) + " degrees"); return false; }
            }
            var dir = t - l;
            dir.Unitize();
            Vector3d up = Vector3d.ZAxis;
            JToken upTok = cam["up"];
            bool userUp = upTok != null && upTok.Type != JTokenType.Null;
            if (userUp)
            {
                var ua = upTok as JArray;
                try { up = new Vector3d((double)ua[0], (double)ua[1], (double)ua[2]); }
                catch (Exception) { a.Error("'" + argName + ".up' must be [x,y,z]"); return false; }
                if (ua.Count != 3 || up.Length < 1e-9) { a.Error("'" + argName + ".up' must be a non-zero [x,y,z]"); return false; }
            }
            else if (Math.Abs(dir * up) > 0.9999) up = Vector3d.YAxis;
            up.Unitize();
            up = up - (up * dir) * dir;                 // roll: the component of 'up' perpendicular to the view direction
            if (up.Length < 1e-6) { a.Error("'" + argName + ".up' must not be parallel to the view direction"); return false; }
            up.Unitize();
            if (vp.IsParallelProjection) vp.ChangeToPerspectiveProjection(true, lens > 0 ? lens : 50.0);
            Framing.SetCamera(vp, t, l, up);
            if (lens > 0) vp.Camera35mmLensLength = lens;
            return true;
        }

        /// <summary>
        /// Rhino's 35 mm lens length is relative to a 24 mm frame laid on the SHORT side of the image
        /// (verified in view_capture), so the field of view across the short side is 2*atan(12/lens).
        /// </summary>
        public static double LensFromFov(double fovDeg) { return 12.0 / Math.Tan(fovDeg * Math.PI / 360.0); }
        public static double FovFromLens(double lens) { return 2.0 * Math.Atan(12.0 / lens) * 180.0 / Math.PI; }

        public static object CameraInfo(RhinoViewport vp)
        {
            return new
            {
                projection = vp.IsParallelProjection ? "parallel" : "perspective",
                location = Pt(vp.CameraLocation),
                target = Pt(vp.CameraTarget),
                direction = Vec(vp.CameraDirection),
                up = Vec(vp.CameraUp),
                lens_mm = vp.IsParallelProjection ? (double?)null : Math.Round(vp.Camera35mmLensLength, 2),
                fov_deg = vp.IsParallelProjection ? (double?)null : Math.Round(FovFromLens(vp.Camera35mmLensLength), 2)
            };
        }

        /// <summary>
        /// only_layers / isolate -> set of object ids to draw (null = everything). Both given = union.
        /// </summary>
        public static HashSet<Guid> ResolveKeep(ToolContext ctx, JObject args, ArgReader a, out string desc)
        {
            desc = null;
            JToken ol, iso;
            bool hasL = args.TryGetValue("only_layers", out ol) && ol.Type != JTokenType.Null;
            bool hasI = args.TryGetValue("isolate", out iso) && iso.Type != JTokenType.Null;
            if (!hasL && !hasI) return null;
            var doc = ctx.Doc;
            var keep = new HashSet<Guid>();
            var parts = new List<string>();
            if (hasL)
            {
                var names = new List<string>();
                if (ol.Type == JTokenType.String) names.Add((string)ol);
                else if (ol is JArray) { foreach (var x in (JArray)ol) { if (x.Type != JTokenType.String) { a.Error("'only_layers' entries must be strings"); break; } names.Add((string)x); } }
                else a.Error("'only_layers' must be a layer name or an array of names");
                var filters = new List<ObjectFilter>();
                foreach (var n in names)
                {
                    bool exists = false;
                    for (int i = 0; i < doc.Layers.Count && !exists; i++)
                    {
                        var l = doc.Layers[i];
                        if (!l.IsDeleted && (string.Equals(l.FullPath, n, StringComparison.OrdinalIgnoreCase) || string.Equals(l.Name, n, StringComparison.OrdinalIgnoreCase))) exists = true;
                    }
                    if (!exists) a.Error("'only_layers': no layer '" + n + "'. Use the full path (Parent::Child) or a leaf name; see layers_list.");
                    filters.Add(new ObjectFilter { Layer = n, IncludeSublayers = true });
                }
                if (!a.HasErrors)
                {
                    var st = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = false, VisibleFilter = true };
                    foreach (var o in doc.Objects.GetObjectList(st))
                        if (filters.Any(f => f.Matches(doc, o))) keep.Add(o.Id);
                }
                parts.Add("layers " + string.Join(", ", names));
            }
            if (hasI)
            {
                var objs = ObjectQuery.TargetOf(ctx, args, a, "isolate");
                foreach (var o in objs) keep.Add(o.Id);
                parts.Add("isolate(" + objs.Count + ")");
            }
            if (!a.HasErrors && keep.Count == 0)
                a.Error("only_layers/isolate matched no visible objects; nothing would be drawn");
            desc = string.Join(" + ", parts) + " -> " + keep.Count + " object(s)";
            return keep;
        }

        public static object SceneInfo(RhinoDoc doc) { return SceneInfo(doc, null); }

        public static object SceneInfo(RhinoDoc doc, HashSet<Guid> keep)
        {
            int count;
            var bb = VisibleBBox(doc, keep, out count);
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

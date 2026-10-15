using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;
using RhinoMCP.Tools.Script;
using RhinoMCP.Tools.View;

namespace RhinoMCP.Tools.Drawing
{
    /// <summary>
    /// Runs Rhino's own scripted -Make2D on the same selection and view, counts what it produced with the same
    /// metrics as our pipeline, and removes the output again (objects and the layers it created).
    /// </summary>
    internal static class NativeCompare
    {
        public static object Run(ToolContext ctx, List<RhinoObject> objs, ViewSpec view, ViewportInfo info, BoundingBox srcBox,
            List<Plane> clip, bool hidden, bool tangents, double tol, double angTol, List<string> warnings)
        {
            var doc = ctx.Doc;
            var rv = doc.Views.ActiveView;
            if (rv == null || rv is RhinoPageView)
                rv = doc.Views.GetViewList(true, false).FirstOrDefault(v => !(v is RhinoPageView));
            if (rv == null)
            {
                warnings.Add("compare_native skipped: no model viewport is available");
                return new { skipped = "no model viewport" };
            }
            var sw = Stopwatch.StartNew();
            var st = new ViewState(doc, rv);
            var before = new HashSet<Guid>(ObjectQuery.All(doc).Select(o => o.Id));
            var layersBefore = new HashSet<int>();
            for (int i = 0; i < doc.Layers.Count; i++) layersBefore.Add(i);
            var clipIds = new List<Guid>();
            bool ok = false;
            string cmd = null;
            var watchdog = new InputWatchdog();
            try
            {
                var vp = rv.ActiveViewport;
                ApplyView(vp, view, info, srcBox);
                st.MarkSelectionTouched();
                doc.Objects.UnselectAll(false);
                foreach (var o in objs) doc.Objects.Select(o.Id, true, true);
                double size = Math.Max(srcBox.Diagonal.Length, 1.0) * 2.0;
                foreach (var p in clip)
                {
                    var id = doc.Objects.AddClippingPlane(p, size, size, vp.Id);
                    if (id != Guid.Empty) clipIds.Add(id);
                }
                rv.Redraw();
                cmd = "-_Make2D _ShowTangents=" + (tangents ? "Yes" : "No") + " _CreateHiddenLines=" + (hidden ? "Yes" : "No") +
                      " _CreateSceneSilhouette=No _CreateClippingPlaneIntersections=" + (clip.Count > 0 ? "Yes" : "No") +
                      " _ShowViewRectangle=No _GroupOutput=No _Enter";
                UndoScope.CloseStray(doc);
                watchdog.Start(20);
                ok = RhinoApp.RunScript(cmd, false);
            }
            catch (Exception ex)
            {
                warnings.Add("compare_native: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                watchdog.Stop();
                if (watchdog.Fired) RhinoMCP.Tools.History.HistoryUndo.CloseStrayRecord(doc);
            }

            // everything new that is not our clipping plane helper
            var created = ObjectQuery.All(doc).Where(o => !before.Contains(o.Id) && !clipIds.Contains(o.Id)).ToList();
            var curves = new List<Curve>();
            foreach (var o in created)
            {
                var c = o.Geometry as Curve;
                if (c != null) curves.Add(c.DuplicateCurve());
            }
            var layerPaths = new HashSet<string>(created.Select(o => doc.Layers[o.Attributes.LayerIndex].FullPath));
            double makeMs = sw.Elapsed.TotalMilliseconds;

            object result;
            if (curves.Count == 0)
            {
                result = new { ok = ok, command = cmd, curves = 0, note = watchdog.Fired ? "Make2D waited for input and was cancelled" : "native Make2D produced no curves", new_objects = created.Count };
            }
            else result = Analyze(curves, tol, angTol, ok, cmd, layerPaths.Count, created.Count, makeMs);

            // clean up: output objects, helper clipping planes, then the layers Make2D created
            try
            {
                var del = created.Select(o => o.Id).Concat(clipIds).ToList();
                if (del.Count > 0) doc.Objects.Delete(del, true);
                var newLayers = new List<Layer>();
                for (int i = 0; i < doc.Layers.Count; i++)
                    if (!layersBefore.Contains(i) && !doc.Layers[i].IsDeleted) newLayers.Add(doc.Layers[i]);
                foreach (var l in newLayers.OrderByDescending(l => l.FullPath.Length))
                    doc.Layers.Delete(l.Index, true);
            }
            catch (Exception ex) { warnings.Add("compare_native cleanup: " + ex.Message); }
            finally
            {
                st.Restore();
                foreach (var c in curves) c.Dispose();
            }
            UndoScope.CloseStray(doc);
            return result;
        }

        static void ApplyView(RhinoViewport vp, ViewSpec v, ViewportInfo info, BoundingBox bb)
        {
            if (v.Perspective)
            {
                vp.SetViewProjection(info, true);
                return;
            }
            if (!vp.IsParallelProjection) vp.ChangeToParallelProjection(true);
            var look = v.Look; look.Unitize();
            var center = bb.IsValid ? bb.Center : Point3d.Origin;
            double D = Math.Max(bb.Diagonal.Length, 1.0) * 2.0;
            var inf = new ViewportInfo(vp);
            inf.SetCameraUp(v.Up);
            vp.SetViewProjection(inf, true);
            vp.SetCameraLocations(center, center - look * D);
            vp.CameraUp = v.Up;
            vp.ZoomExtents();
        }

        static object Analyze(List<Curve> curves, double tol, double angTol, bool ok, string cmd, int layers, int newObjects, double ms)
        {
            // bring the (possibly 3D-placed) native curves into the XY plane for 2D metrics
            var bb = BoundingBox.Empty;
            foreach (var c in curves) bb.Union(c.GetBoundingBox(false));
            var d = bb.Max - bb.Min;
            Transform? flat = null;
            if (d.Z > tol)
            {
                var t = Transform.Identity;
                t.M00 = 0; t.M01 = 0; t.M02 = 0; t.M10 = 0; t.M11 = 0; t.M12 = 0; t.M20 = 0; t.M21 = 0; t.M22 = 0;
                if (d.Y <= d.X && d.Y <= d.Z) { t.M00 = 1; t.M12 = 1; }   // x' = x, y' = z
                else { t.M01 = 1; t.M12 = 1; }                           // x' = y, y' = z
                flat = t;
            }
            var kinds = new Dictionary<string, int> { { "line", 0 }, { "arc", 0 }, { "polyline", 0 }, { "polycurve", 0 }, { "nurbs", 0 } };
            int nurbsSimple = 0, closed = 0;
            var pieces = new List<Piece>();
            var st = new DecomposeStats();
            var flatCurves = new List<Curve>();
            foreach (var c in curves)
            {
                var k = DrawingGeometry.KindOf(c);
                kinds[k]++;
                if (k == "nurbs" && (c.IsLinear(tol) || c.IsArc(tol))) nurbsSimple++;
                if (c.IsClosed) closed++;
                var fc = c.DuplicateCurve();
                if (flat.HasValue) fc.Transform(flat.Value);
                flatCurves.Add(fc);
                DrawingGeometry.Decompose(fc, tol, angTol, 0, Cat.Visible, pieces, st);
            }
            int openEnds = DrawingGeometry.OpenEnds(flatCurves, tol);
            int removed;
            DrawingGeometry.Deoverlap(pieces, tol, out removed);
            foreach (var c in flatCurves) c.Dispose();
            return new
            {
                ok = ok,
                command = cmd,
                curves = curves.Count,
                curves_by_kind = new { line = kinds["line"], arc = kinds["arc"], polyline = kinds["polyline"], polycurve = kinds["polycurve"], nurbs = kinds["nurbs"] },
                nurbs_that_are_lines_or_arcs = nurbsSimple,
                segments = pieces.Count,
                overlapping_pieces = removed,
                open_ends = openEnds,
                closed_curves = closed,
                layers = layers,
                new_objects = newObjects,
                time_ms = Math.Round(ms, 1),
                note = "native output was deleted again; Make2D option values stay sticky in Rhino"
            };
        }
    }
}

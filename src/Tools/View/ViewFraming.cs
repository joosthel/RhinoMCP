using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.View
{
    /// <summary>
    /// Display conduit used during a capture: hides every object outside the keep-set (nothing in the document is touched)
    /// and optionally draws a text label in the top-left corner of the view.
    /// </summary>
    internal sealed class CaptureConduit : DisplayConduit
    {
        public HashSet<Guid> Keep;      // null = draw everything
        public string Label;
        public bool LabelFailed;

        protected override void PreDrawObject(DrawObjectEventArgs e)
        {
            try
            {
                if (Keep != null && e.RhinoObject != null && !Keep.Contains(e.RhinoObject.Id)) e.DrawObject = false;
            }
            catch (Exception) { }
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (Label == null || LabelFailed) return;
            try { DrawLabel(e); }
            catch (Exception) { LabelFailed = true; }
        }

        // Separate method: a System.Drawing binding problem (MissingMethodException) surfaces when this is JIT-compiled and is caught above.
        void DrawLabel(DrawEventArgs e)
        {
            e.Display.Draw2dText(Label, System.Drawing.Color.Black, new Point2d(10, 10), false, 18);
        }
    }

    /// <summary>Result of fitting the camera to a box: what fraction of the image the box covers.</summary>
    internal sealed class FrameInfo
    {
        public double FillX, FillY;
        public double Margin;
    }

    internal static class Framing
    {
        static void Axes(RhinoViewport vp, out Vector3d dir, out Vector3d right, out Vector3d up)
        {
            dir = vp.CameraDirection; dir.Unitize();
            up = vp.CameraUp; up.Unitize();
            right = Vector3d.CrossProduct(dir, up); right.Unitize();
            up = Vector3d.CrossProduct(right, dir); up.Unitize();
        }

        /// <summary>Moves the camera to <paramref name="location"/> looking at <paramref name="target"/> and keeps <paramref name="up"/> (SetCameraLocations alone resets the roll).</summary>
        public static void SetCamera(RhinoViewport vp, Point3d target, Point3d location, Vector3d up)
        {
            vp.SetCameraLocations(target, location);
            var dir = target - location; dir.Unitize();
            up = up - (up * dir) * dir;
            if (up.Length < 1e-9) return;
            up.Unitize();
            var info = new ViewportInfo(vp);
            info.SetCameraUp(up);
            vp.SetViewProjection(info, false);
        }

        /// <summary>
        /// Frames the box tightly. margin = fraction of the image left free on each side. The framing is computed for the
        /// requested capture size W x H (Rhino keeps the SHORT side of the live frustum / the 24 mm lens frame when the capture
        /// aspect differs from the live viewport's).
        /// </summary>
        public static FrameInfo Zoom(RhinoViewport vp, BoundingBox bb, double margin, int W, int H)
        {
            Vector3d d, r, u;
            Axes(vp, out d, out r, out u);
            var corners = bb.GetCorners();
            var c = bb.Center;
            double A = (double)W / H;
            double fill = Math.Max(0.05, 1.0 - 2.0 * margin);
            double diag = Math.Max(bb.Diagonal.Length, 1e-6);

            if (vp.IsParallelProjection)
            {
                double hx = 0, hy = 0;
                foreach (var p in corners)
                {
                    var v = p - c;
                    hx = Math.Max(hx, Math.Abs(v * r)); hy = Math.Max(hy, Math.Abs(v * u));
                }
                double capHalfH = Math.Max(Math.Max(hy, hx / A) / fill, diag * 1e-6);
                double capHalfW = capHalfH * A;
                double s = Math.Min(capHalfH, capHalfW);          // short-side half extent
                var live = vp.Size;
                double Al = live.Height > 0 ? (double)live.Width / live.Height : 1.0;
                double fw = Al >= 1 ? s * Al : s, fh = Al >= 1 ? s : s / Al;
                double D = diag * 2.0;
                var info = new ViewportInfo(vp);
                info.SetCameraLocation(c - d * D);
                info.SetCameraDirection(d);
                info.SetCameraUp(u);
                info.SetFrustum(-fw, fw, -fh, fh, Math.Max(D - diag, diag * 0.01), D + diag);
                vp.SetViewProjection(info, true);
                SetCamera(vp, c, c - d * D, u);
            }
            else
            {
                double ts = 12.0 / Math.Max(vp.Camera35mmLensLength, 1.0);   // tan of the half angle across the short side
                double tw = A >= 1 ? ts * A : ts, th = A >= 1 ? ts : ts / A;
                double dist = diag * 0.05;
                foreach (var p in corners)
                {
                    var v = p - c;
                    double zc = v * d;
                    dist = Math.Max(dist, Math.Abs(v * r) / (tw * fill) - zc);
                    dist = Math.Max(dist, Math.Abs(v * u) / (th * fill) - zc);
                }
                dist = Math.Max(dist, diag * 0.05);
                SetCamera(vp, c, c - d * dist, u);
            }
            return Measure(vp, bb, W, H, margin);
        }

        /// <summary>Fraction of the capture width/height covered by the projected box (same assumptions as Zoom).</summary>
        public static FrameInfo Measure(RhinoViewport vp, BoundingBox bb, int W, int H, double margin)
        {
            Vector3d d, r, u;
            Axes(vp, out d, out r, out u);
            double A = (double)W / H;
            var loc = vp.CameraLocation;
            double minx = double.MaxValue, maxx = double.MinValue, miny = double.MaxValue, maxy = double.MinValue;
            double hw, hh;
            if (vp.IsParallelProjection)
            {
                double l, rr, b, t, n, f;
                vp.GetFrustum(out l, out rr, out b, out t, out n, out f);
                double s = Math.Min((rr - l) / 2.0, (t - b) / 2.0);
                hh = A >= 1 ? s : s / A; hw = A >= 1 ? s * A : s;
                foreach (var p in bb.GetCorners())
                {
                    var v = p - loc;
                    double x = v * r, y = v * u;
                    minx = Math.Min(minx, x); maxx = Math.Max(maxx, x); miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
                }
            }
            else
            {
                double ts = 12.0 / Math.Max(vp.Camera35mmLensLength, 1.0);
                hw = A >= 1 ? ts * A : ts; hh = A >= 1 ? ts : ts / A;
                foreach (var p in bb.GetCorners())
                {
                    var v = p - loc;
                    double z = v * d;
                    if (z < 1e-9) z = 1e-9;
                    double x = (v * r) / z, y = (v * u) / z;
                    minx = Math.Min(minx, x); maxx = Math.Max(maxx, x); miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
                }
            }
            return new FrameInfo { FillX = Math.Round((maxx - minx) / (2 * hw), 3), FillY = Math.Round((maxy - miny) / (2 * hh), 3), Margin = margin };
        }
    }
}

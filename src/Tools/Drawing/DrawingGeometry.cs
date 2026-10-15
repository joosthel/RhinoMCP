using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace RhinoMCP.Tools.Drawing
{
    /// <summary>Output line categories in priority order (when pieces of different categories overlap, the lower value wins).</summary>
    internal enum Cat { Section = 0, Silhouette = 1, Visible = 2, Tangent = 3, Hidden = 4 }

    /// <summary>One primitive of a 2D drawing in the XY plane: a line, a counter-clockwise arc/circle, or a free-form curve.</summary>
    internal sealed class Piece
    {
        public const int KLine = 0, KArc = 1, KFree = 2;
        public int Kind;
        public Line L;          // Kind == KLine
        public Arc A;           // Kind == KArc: always in a plane parallel to WorldXY with +Z normal (counter-clockwise)
        public Curve C;         // Kind == KFree
        public int Src;
        public Cat Cat;
        public double Length;

        public static Piece FromLine(Point3d a, Point3d b, int src, Cat cat)
        {
            var l = new Line(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0));
            return new Piece { Kind = KLine, L = l, Src = src, Cat = cat, Length = l.Length };
        }
        public static Piece FromArc(Arc a, int src, Cat cat)
        {
            return new Piece { Kind = KArc, A = a, Src = src, Cat = cat, Length = a.Length };
        }
        public static Piece FromFree(Curve c, int src, Cat cat)
        {
            return new Piece { Kind = KFree, C = c, Src = src, Cat = cat, Length = c.GetLength() };
        }

        /// <summary>Canonical counter-clockwise arc around (cx,cy) in the XY plane; span >= 2pi gives a full circle.</summary>
        public static Arc MakeArc(double cx, double cy, double r, double t0, double span)
        {
            var pl = Plane.WorldXY;
            pl.Origin = new Point3d(cx, cy, 0);
            if (span >= 2 * Math.PI - 1e-9) span = 2 * Math.PI;
            return new Arc(new Circle(pl, r), new Interval(t0, t0 + span));
        }

        /// <summary>Normalizes any arc to the canonical form (flat, counter-clockwise). Null if the arc is not parallel to the XY plane.</summary>
        public static bool Canon(Arc a, out Arc res)
        {
            res = Arc.Unset;
            if (!a.IsValid) return false;
            var n = a.Plane.ZAxis;
            if (Math.Abs(Math.Abs(n.Z) - 1.0) > 1e-6) return false;
            var c = a.Center;
            double t0, span = a.Angle;
            if (n.Z > 0) t0 = Math.Atan2(a.StartPoint.Y - c.Y, a.StartPoint.X - c.X);
            else t0 = Math.Atan2(a.EndPoint.Y - c.Y, a.EndPoint.X - c.X);
            res = MakeArc(c.X, c.Y, a.Radius, t0, span);
            return res.IsValid;
        }

        public Point3d Start { get { return Kind == KLine ? L.From : Kind == KArc ? A.StartPoint : C.PointAtStart; } }
        public Point3d End { get { return Kind == KLine ? L.To : Kind == KArc ? A.EndPoint : C.PointAtEnd; } }
        public bool IsFullCircle { get { return Kind == KArc && A.Angle >= 2 * Math.PI - 1e-9; } }

        public Curve ToCurve()
        {
            if (Kind == KLine) return new LineCurve(L);
            if (Kind == KArc) return new ArcCurve(A);
            return C.DuplicateCurve();
        }
    }

    internal sealed class DecomposeStats { public int Tiny; }

    internal static class DrawingGeometry
    {
        // ------------------------------------------------------------------ decomposition

        /// <summary>Breaks a curve into line / arc / free-form primitives in the XY plane. Pieces shorter than tol are counted and dropped.</summary>
        public static void Decompose(Curve c, double tol, double angTol, int src, Cat cat, List<Piece> outp, DecomposeStats st, int depth = 0)
        {
            if (c == null) return;
            var bb = c.GetBoundingBox(false);
            if (!bb.IsValid) return;
            if (bb.Max.Z - bb.Min.Z > 1e-9)
            {
                var proj = Curve.ProjectToPlane(c, Plane.WorldXY);
                if (proj == null) return;
                c = proj;
            }
            var pc = c as PolyCurve;
            if (pc != null)
            {
                for (int i = 0; i < pc.SegmentCount; i++) Decompose(pc.SegmentCurve(i), tol, angTol, src, cat, outp, st, depth);
                return;
            }
            var plc = c as PolylineCurve;
            if (plc != null)
            {
                for (int i = 0; i + 1 < plc.PointCount; i++) AddLine(plc.Point(i), plc.Point(i + 1), tol, src, cat, outp, st);
                return;
            }
            var lc = c as LineCurve;
            if (lc != null) { AddLine(lc.Line.From, lc.Line.To, tol, src, cat, outp, st); return; }
            var ac = c as ArcCurve;
            if (ac != null) { AddArc(ac.Arc, tol, src, cat, outp, st, c); return; }
            if (!c.IsClosed && c.IsLinear(tol)) { AddLine(c.PointAtStart, c.PointAtEnd, tol, src, cat, outp, st); return; }
            Arc arc;
            if (c.TryGetArc(out arc, tol)) { AddArc(arc, tol, src, cat, outp, st, c); return; }
            Polyline poly;
            if (c.TryGetPolyline(out poly))
            {
                for (int i = 0; i + 1 < poly.Count; i++) AddLine(poly[i], poly[i + 1], tol, src, cat, outp, st);
                return;
            }
            if (depth == 0)
            {
                Curve s = null;
                try { s = c.Simplify(CurveSimplifyOptions.All, tol, angTol); } catch (Exception) { s = null; }
                if (s != null && !ReferenceEquals(s, c))
                {
                    var spc = s as PolyCurve;
                    if (spc != null)
                    {
                        // one level deeper so a segment that stays free-form is not simplified again
                        for (int i = 0; i < spc.SegmentCount; i++) Decompose(spc.SegmentCurve(i), tol, angTol, src, cat, outp, st, 1);
                        return;
                    }
                    Decompose(s, tol, angTol, src, cat, outp, st, 1);
                    return;
                }
            }
            double len = c.GetLength();
            if (len < tol) { st.Tiny++; return; }
            var dup = c.DuplicateCurve();
            outp.Add(Piece.FromFree(Flat(dup), src, cat));
        }

        static Curve Flat(Curve c)
        {
            var bb = c.GetBoundingBox(false);
            if (Math.Abs(bb.Min.Z) > 1e-12 || Math.Abs(bb.Max.Z) > 1e-12) c.Transform(Transform.PlanarProjection(Plane.WorldXY));
            return c;
        }

        static void AddLine(Point3d a, Point3d b, double tol, int src, Cat cat, List<Piece> outp, DecomposeStats st)
        {
            if (a.DistanceTo(b) < tol) { st.Tiny++; return; }
            outp.Add(Piece.FromLine(a, b, src, cat));
        }

        static void AddArc(Arc a, double tol, int src, Cat cat, List<Piece> outp, DecomposeStats st, Curve fallback)
        {
            if (a.Length < tol) { st.Tiny++; return; }
            Arc ca;
            if (Piece.Canon(a, out ca)) { outp.Add(Piece.FromArc(ca, src, cat)); return; }
            var dup = fallback.DuplicateCurve();
            outp.Add(Piece.FromFree(Flat(dup), src, cat));
        }

        // ------------------------------------------------------------------ spatial hash helpers

        public struct Cell : IEquatable<Cell>
        {
            public readonly long X, Y;
            public Cell(long x, long y) { X = x; Y = y; }
            public bool Equals(Cell o) { return X == o.X && Y == o.Y; }
            public override bool Equals(object o) { return o is Cell && Equals((Cell)o); }
            public override int GetHashCode() { unchecked { return (int)(X * 73856093L ^ Y * 19349663L); } }
        }

        public sealed class Grid
        {
            readonly Dictionary<Cell, List<int>> _cells = new Dictionary<Cell, List<int>>();
            readonly List<int> _big = new List<int>();
            readonly double _cs;
            const int MaxCells = 4096;
            public Grid(double cellSize) { _cs = Math.Max(cellSize, 1e-9); }
            long Idx(double v) { return (long)Math.Floor(v / _cs); }
            public void Add(int id, BoundingBox b)
            {
                long x0 = Idx(b.Min.X), x1 = Idx(b.Max.X), y0 = Idx(b.Min.Y), y1 = Idx(b.Max.Y);
                if ((x1 - x0 + 1) * (y1 - y0 + 1) > MaxCells) { _big.Add(id); return; }
                for (long x = x0; x <= x1; x++)
                    for (long y = y0; y <= y1; y++)
                    {
                        List<int> l; var k = new Cell(x, y);
                        if (!_cells.TryGetValue(k, out l)) { l = new List<int>(); _cells[k] = l; }
                        l.Add(id);
                    }
            }
            public void Query(BoundingBox b, HashSet<int> into)
            {
                foreach (var id in _big) into.Add(id);
                long x0 = Idx(b.Min.X), x1 = Idx(b.Max.X), y0 = Idx(b.Min.Y), y1 = Idx(b.Max.Y);
                if ((x1 - x0 + 1) * (y1 - y0 + 1) > MaxCells)
                {
                    foreach (var l in _cells.Values) foreach (var id in l) into.Add(id);
                    return;
                }
                for (long x = x0; x <= x1; x++)
                    for (long y = y0; y <= y1; y++)
                    {
                        List<int> l;
                        if (_cells.TryGetValue(new Cell(x, y), out l)) foreach (var id in l) into.Add(id);
                    }
            }
        }

        static BoundingBox Inflate(BoundingBox b, double d)
        {
            return new BoundingBox(b.Min.X - d, b.Min.Y - d, 0, b.Max.X + d, b.Max.Y + d, 0);
        }

        static BoundingBox PieceBox(Piece p)
        {
            if (p.Kind == Piece.KLine)
                return new BoundingBox(Math.Min(p.L.FromX, p.L.ToX), Math.Min(p.L.FromY, p.L.ToY), 0, Math.Max(p.L.FromX, p.L.ToX), Math.Max(p.L.FromY, p.L.ToY), 0);
            if (p.Kind == Piece.KArc) { var b = p.A.BoundingBox(); return new BoundingBox(b.Min.X, b.Min.Y, 0, b.Max.X, b.Max.Y, 0); }
            var c = p.C.GetBoundingBox(false);
            return new BoundingBox(c.Min.X, c.Min.Y, 0, c.Max.X, c.Max.Y, 0);
        }

        static double CellSize(List<Piece> ps, double tol)
        {
            if (ps.Count == 0) return 1;
            var u = BoundingBox.Empty;
            foreach (var p in ps) u.Union(PieceBox(p));
            double ext = Math.Max(u.Max.X - u.Min.X, u.Max.Y - u.Min.Y);
            // about sqrt(n) cells per side keeps cells holding O(sqrt n) pieces in the worst case
            double cs = ext / Math.Max(8.0, Math.Sqrt(ps.Count) * 2.0);
            return Math.Max(cs, tol * 4);
        }

        // ------------------------------------------------------------------ weld

        /// <summary>Snaps endpoints that lie within tol of each other to their centroid. Returns the number of endpoints moved.</summary>
        public static int Weld(List<Piece> ps, double tol)
        {
            if (ps.Count == 0) return 0;
            double cs = Math.Max(tol * 2, 1e-9);
            var ends = new List<KeyValuePair<int, bool>>(); // piece index, isStart
            var pts = new List<Point3d>();
            for (int i = 0; i < ps.Count; i++)
            {
                if (ps[i].IsFullCircle) continue;
                ends.Add(new KeyValuePair<int, bool>(i, true)); pts.Add(ps[i].Start);
                ends.Add(new KeyValuePair<int, bool>(i, false)); pts.Add(ps[i].End);
            }
            var grid = new Dictionary<Cell, List<int>>();
            for (int i = 0; i < pts.Count; i++)
            {
                var k = new Cell((long)Math.Floor(pts[i].X / cs), (long)Math.Floor(pts[i].Y / cs));
                List<int> l; if (!grid.TryGetValue(k, out l)) { l = new List<int>(); grid[k] = l; }
                l.Add(i);
            }
            var done = new bool[pts.Count];
            var newPt = new Point3d[pts.Count];
            var moved = new bool[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                if (done[i]) continue;
                var cluster = new List<int> { i };
                done[i] = true;
                long cx = (long)Math.Floor(pts[i].X / cs), cy = (long)Math.Floor(pts[i].Y / cs);
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                    {
                        List<int> l;
                        if (!grid.TryGetValue(new Cell(cx + dx, cy + dy), out l)) continue;
                        foreach (int j in l)
                            if (!done[j] && pts[i].DistanceTo(pts[j]) <= tol) { done[j] = true; cluster.Add(j); }
                    }
                if (cluster.Count < 2) continue;
                double sx = 0, sy = 0;
                foreach (int j in cluster) { sx += pts[j].X; sy += pts[j].Y; }
                var m = new Point3d(sx / cluster.Count, sy / cluster.Count, 0);
                foreach (int j in cluster)
                    if (pts[j].DistanceTo(m) > 1e-12) { newPt[j] = m; moved[j] = true; }
            }
            int count = 0;
            for (int i = 0; i < ends.Count; i += 2)
            {
                int pi = ends[i].Key;
                bool ms = moved[i], me = moved[i + 1];
                if (!ms && !me) continue;
                var p = ps[pi];
                var s = ms ? newPt[i] : pts[i];
                var e = me ? newPt[i + 1] : pts[i + 1];
                if (p.Kind == Piece.KLine)
                {
                    p.L = new Line(s, e); p.Length = p.L.Length;
                }
                else if (p.Kind == Piece.KArc)
                {
                    var mid = p.A.MidPoint;
                    Arc na;
                    try
                    {
                        var a3 = new Arc(s, mid, e);
                        if (Piece.Canon(a3, out na)) { p.A = na; p.Length = na.Length; }
                    }
                    catch (Exception) { }
                }
                else
                {
                    if (ms) p.C.SetStartPoint(s);
                    if (me) p.C.SetEndPoint(e);
                }
                count += (ms ? 1 : 0) + (me ? 1 : 0);
            }
            return count;
        }

        // ------------------------------------------------------------------ de-overlap

        /// <summary>
        /// Removes coincident and collinear/co-circular overlapping pieces, keeping one copy. Higher-priority categories
        /// win; within a category longer pieces win. Partially covered pieces are trimmed to the uncovered remainder.
        /// Returns the number of pieces removed or trimmed.
        /// </summary>
        public static List<Piece> Deoverlap(List<Piece> input, double tol, out int removed)
        {
            removed = 0;
            var result = new List<Piece>();
            var order = input.OrderBy(p => (int)p.Cat).ThenByDescending(p => p.Length).ToList();
            double cs = CellSize(order, tol);

            var kept = new List<Piece>();                      // lines and arcs, indexed in grid
            var grid = new Grid(cs);
            var freeKeys = new HashSet<string>();

            foreach (var p in order)
            {
                if (p.Kind == Piece.KFree)
                {
                    string k = FreeKey(p.C, tol);
                    if (!freeKeys.Add(k)) { removed++; continue; }
                    result.Add(p);
                    continue;
                }
                var cand = new HashSet<int>();
                grid.Query(Inflate(PieceBox(p), tol), cand);
                List<double[]> covered = new List<double[]>();   // intervals in the piece's own parameter (length for lines, angle for arcs)
                foreach (int id in cand)
                {
                    var a = kept[id];
                    if (a.Kind != p.Kind) continue;
                    if (p.Kind == Piece.KLine) CoverLine(p, a, tol, covered);
                    else CoverArc(p, a, tol, covered);
                }
                if (covered.Count == 0) { Keep(p, kept, grid, result, tol); continue; }

                var gaps = Gaps(covered, p.Kind == Piece.KLine ? p.Length : p.A.Angle);
                removed++;
                if (p.Kind == Piece.KLine)
                {
                    var d = p.L.Direction; d.Unitize();
                    foreach (var g in gaps)
                    {
                        if (g[1] - g[0] < tol) continue;
                        var np = Piece.FromLine(p.L.From + d * g[0], p.L.From + d * g[1], p.Src, p.Cat);
                        Keep(np, kept, grid, result, tol);
                    }
                }
                else
                {
                    var c = p.A.Center; double r = p.A.Radius, t0 = p.A.StartAngle;
                    foreach (var g in gaps)
                    {
                        if ((g[1] - g[0]) * r < tol) continue;
                        var na = Piece.FromArc(Piece.MakeArc(c.X, c.Y, r, t0 + g[0], g[1] - g[0]), p.Src, p.Cat);
                        Keep(na, kept, grid, result, tol);
                    }
                }
            }
            return result;
        }

        static void Keep(Piece p, List<Piece> kept, Grid grid, List<Piece> result, double tol)
        {
            kept.Add(p);
            grid.Add(kept.Count - 1, Inflate(PieceBox(p), tol));
            result.Add(p);
        }

        static void CoverLine(Piece b, Piece a, double tol, List<double[]> covered)
        {
            var la = a.L; var lb = b.L;
            double ax = la.ToX - la.FromX, ay = la.ToY - la.FromY, al = a.Length;
            if (al < 1e-12) return;
            double ux = ax / al, uy = ay / al;
            // both endpoints of b within tol of the infinite line a
            double d1 = Math.Abs((lb.FromX - la.FromX) * uy - (lb.FromY - la.FromY) * ux);
            double d2 = Math.Abs((lb.ToX - la.FromX) * uy - (lb.ToY - la.FromY) * ux);
            if (d1 > tol || d2 > tol) return;
            // b's endpoints in a's parameter
            double t1 = (lb.FromX - la.FromX) * ux + (lb.FromY - la.FromY) * uy;
            double t2 = (lb.ToX - la.FromX) * ux + (lb.ToY - la.FromY) * uy;
            double lo = Math.Max(0, Math.Min(t1, t2)), hi = Math.Min(al, Math.Max(t1, t2));
            if (hi - lo <= tol * 0.5) return;
            // map the overlap back into b's own parameter (length from b.From)
            double bl = b.Length;
            double bx = (lb.ToX - lb.FromX) / bl, by = (lb.ToY - lb.FromY) / bl;
            var pLo = new Point3d(la.FromX + ux * lo, la.FromY + uy * lo, 0);
            var pHi = new Point3d(la.FromX + ux * hi, la.FromY + uy * hi, 0);
            double s1 = (pLo.X - lb.FromX) * bx + (pLo.Y - lb.FromY) * by;
            double s2 = (pHi.X - lb.FromX) * bx + (pHi.Y - lb.FromY) * by;
            double s0 = Math.Max(0, Math.Min(s1, s2)), s9 = Math.Min(bl, Math.Max(s1, s2));
            if (s9 - s0 > 0) covered.Add(new[] { s0, s9 });
        }

        static void CoverArc(Piece b, Piece a, double tol, List<double[]> covered)
        {
            var ca = a.A.Center; var cb = b.A.Center;
            if (Math.Abs(ca.X - cb.X) > tol || Math.Abs(ca.Y - cb.Y) > tol || ca.DistanceTo(cb) > tol) return;
            if (Math.Abs(a.A.Radius - b.A.Radius) > tol) return;
            const double TwoPi = 2 * Math.PI;
            double bs = b.A.Angle;
            double aStart = Mod(a.A.StartAngle - b.A.StartAngle), aSpan = a.A.Angle;
            // a covers [aStart, aStart+aSpan] and, shifted by -2pi, [aStart-2pi, aStart+aSpan-2pi]
            for (int k = 0; k < 2; k++)
            {
                double lo = aStart - k * TwoPi, hi = lo + aSpan;
                double l = Math.Max(0, lo), h = Math.Min(bs, hi);
                if (h - l > 1e-12 && (h - l) * b.A.Radius > tol * 0.5) covered.Add(new[] { l, h });
            }
        }

        static double Mod(double a)
        {
            double t = a % (2 * Math.PI);
            return t < 0 ? t + 2 * Math.PI : t;
        }

        static List<double[]> Gaps(List<double[]> covered, double total)
        {
            covered.Sort((x, y) => x[0].CompareTo(y[0]));
            var gaps = new List<double[]>();
            double cur = 0;
            foreach (var iv in covered)
            {
                if (iv[0] > cur) gaps.Add(new[] { cur, iv[0] });
                if (iv[1] > cur) cur = iv[1];
            }
            if (cur < total) gaps.Add(new[] { cur, total });
            return gaps;
        }

        static string FreeKey(Curve c, double tol)
        {
            string f = KeyFor(c, tol, false), r = KeyFor(c, tol, true);
            return string.CompareOrdinal(f, r) <= 0 ? f : r;
        }
        static string KeyFor(Curve c, double tol, bool rev)
        {
            var sb = new System.Text.StringBuilder();
            var dom = c.Domain;
            for (int i = 0; i <= 4; i++)
            {
                double t = dom.ParameterAt((rev ? 4 - i : i) / 4.0);
                var p = c.PointAt(t);
                sb.Append((long)Math.Round(p.X / (tol * 4))).Append(',').Append((long)Math.Round(p.Y / (tol * 4))).Append(';');
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ join + tidy

        /// <summary>Joins pieces end to end and reduces each result to the simplest primitive that represents it.</summary>
        public static List<Curve> JoinAndTidy(List<Piece> ps, double tol, double angTol)
        {
            var res = new List<Curve>();
            if (ps.Count == 0) return res;
            var curves = ps.Select(p => p.ToCurve()).ToList();
            Curve[] joined = null;
            try { joined = Curve.JoinCurves(curves, tol, false); } catch (Exception) { joined = null; }
            if (joined == null || joined.Length == 0) joined = curves.ToArray();
            foreach (var j in joined)
            {
                var t = Tidy(j, tol, angTol);
                if (t != null) res.Add(t);
            }
            foreach (var j in joined) j.Dispose();
            foreach (var c in curves) c.Dispose();
            return res;
        }

        public static Curve Tidy(Curve j, double tol, double angTol)
        {
            if (j == null) return null;
            var pc = j as PolyCurve;
            if (pc != null) pc.RemoveNesting();
            Curve s = null;
            try { s = j.Simplify(CurveSimplifyOptions.All, tol, angTol); } catch (Exception) { s = null; }
            if (s == null) s = j;

            var segs = new List<Curve>();
            var spc = s as PolyCurve;
            if (spc != null)
            {
                spc.RemoveNesting();
                for (int i = 0; i < spc.SegmentCount; i++) segs.Add(spc.SegmentCurve(i));
            }
            else segs.Add(s);

            bool allLines = segs.All(x => x is LineCurve || x is PolylineCurve);
            if (allLines)
            {
                var pts = new List<Point3d>();
                foreach (var sg in segs)
                {
                    var l = sg as LineCurve;
                    if (l != null) { Append(pts, l.Line.From, tol); Append(pts, l.Line.To, tol); }
                    else
                    {
                        var p = (PolylineCurve)sg;
                        for (int i = 0; i < p.PointCount; i++) Append(pts, p.Point(i), tol);
                    }
                }
                bool closed = pts.Count >= 4 && pts[0].DistanceTo(pts[pts.Count - 1]) <= tol;
                if (closed) pts[pts.Count - 1] = pts[0];
                RemoveCollinear(pts, tol, closed);
                if (pts.Count == 2) return new LineCurve(pts[0], pts[1]);
                if (pts.Count < 2) return null;
                return new PolylineCurve(pts);
            }
            if (segs.Count == 1 && segs[0] is ArcCurve) return segs[0].DuplicateCurve();
            return s.DuplicateCurve();
        }

        static void Append(List<Point3d> pts, Point3d p, double tol)
        {
            p.Z = 0;
            if (pts.Count > 0 && pts[pts.Count - 1].DistanceTo(p) <= tol * 0.5) return;
            pts.Add(p);
        }

        /// <summary>Drops vertices that lie on the straight line between their neighbours (and continue in the same direction).</summary>
        static void RemoveCollinear(List<Point3d> pts, double tol, bool closed)
        {
            bool changed = true;
            int guard = 0;
            while (changed && guard++ < 8)
            {
                changed = false;
                int n = pts.Count;
                var keep = new List<Point3d>();
                int last = closed ? n - 1 : n; // for closed polylines pts[n-1] duplicates pts[0]
                for (int i = 0; i < last; i++)
                {
                    bool isEnd = !closed && (i == 0 || i == n - 1);
                    if (isEnd) { keep.Add(pts[i]); continue; }
                    var prev = pts[(i - 1 + last) % last];
                    var next = pts[(i + 1) % last];
                    var cur = pts[i];
                    var d = new Line(prev, next);
                    double dist = d.DistanceTo(cur, true);
                    var v1 = cur - prev; var v2 = next - cur;
                    if (dist <= tol * 0.5 && v1 * v2 > 0 && d.Length > tol) { changed = true; continue; }
                    keep.Add(cur);
                }
                if (closed && keep.Count > 0) keep.Add(keep[0]);
                if (keep.Count < (closed ? 4 : 2)) return;
                pts.Clear(); pts.AddRange(keep);
            }
        }

        public static string KindOf(Curve c)
        {
            if (c is LineCurve) return "line";
            if (c is ArcCurve) return "arc";
            if (c is PolylineCurve) return "polyline";
            var pc = c as PolyCurve;
            if (pc != null)
            {
                for (int i = 0; i < pc.SegmentCount; i++)
                {
                    var sg = pc.SegmentCurve(i);
                    if (!(sg is LineCurve || sg is ArcCurve || sg is PolylineCurve)) return "nurbs";
                }
                return "polycurve";
            }
            return "nurbs";
        }

        // ------------------------------------------------------------------ analysis

        /// <summary>
        /// Counts endpoints of open curves that have neither another endpoint nor another curve within tol.
        /// (A line ending on another line, a T-junction, is not open.)
        /// </summary>
        public static int OpenEnds(IList<Curve> curves, double tol)
        {
            var eps = new List<Point3d>(); var owner = new List<int>();
            for (int i = 0; i < curves.Count; i++)
            {
                var c = curves[i];
                if (c.IsClosed) continue;
                var s = c.PointAtStart; var e = c.PointAtEnd;
                if (s.DistanceTo(e) <= tol) continue;
                eps.Add(s); owner.Add(i); eps.Add(e); owner.Add(i);
            }
            if (eps.Count == 0) return 0;
            double cs = Math.Max(tol * 2, 1e-9);
            var egrid = new Dictionary<Cell, List<int>>();
            for (int i = 0; i < eps.Count; i++)
            {
                var k = new Cell((long)Math.Floor(eps[i].X / cs), (long)Math.Floor(eps[i].Y / cs));
                List<int> l; if (!egrid.TryGetValue(k, out l)) { l = new List<int>(); egrid[k] = l; }
                l.Add(i);
            }
            var ub = BoundingBox.Empty;
            var cboxes = new BoundingBox[curves.Count];
            for (int i = 0; i < curves.Count; i++) { cboxes[i] = curves[i].GetBoundingBox(false); ub.Union(cboxes[i]); }
            double ext = Math.Max(ub.Max.X - ub.Min.X, ub.Max.Y - ub.Min.Y);
            var cgrid = new Grid(Math.Max(ext / Math.Max(8.0, Math.Sqrt(curves.Count) * 2.0), tol * 4));
            for (int i = 0; i < curves.Count; i++) cgrid.Add(i, Inflate(cboxes[i], tol));

            int open = 0;
            for (int i = 0; i < eps.Count; i++)
            {
                var p = eps[i];
                bool covered = false;
                long cx = (long)Math.Floor(p.X / cs), cy = (long)Math.Floor(p.Y / cs);
                for (long dx = -1; dx <= 1 && !covered; dx++)
                    for (long dy = -1; dy <= 1 && !covered; dy++)
                    {
                        List<int> l;
                        if (!egrid.TryGetValue(new Cell(cx + dx, cy + dy), out l)) continue;
                        foreach (int j in l)
                            if (j != i && owner[j] != owner[i] && eps[j].DistanceTo(p) <= tol) { covered = true; break; }
                    }
                if (!covered)
                {
                    var cand = new HashSet<int>();
                    cgrid.Query(new BoundingBox(p.X - tol, p.Y - tol, 0, p.X + tol, p.Y + tol, 0), cand);
                    foreach (int ci in cand)
                    {
                        if (ci == owner[i]) continue;
                        if (OnCurve(curves[ci], p, tol)) { covered = true; break; }
                    }
                }
                if (!covered) open++;
            }
            return open;
        }

        static bool OnCurve(Curve c, Point3d p, double tol)
        {
            var lc = c as LineCurve;
            if (lc != null) return lc.Line.DistanceTo(p, true) <= tol;
            double t;
            try { return c.ClosestPoint(p, out t, tol * 2) && c.PointAt(t).DistanceTo(p) <= tol; }
            catch (Exception) { return false; }
        }
    }
}

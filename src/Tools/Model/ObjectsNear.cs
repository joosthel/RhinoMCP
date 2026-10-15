using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Model
{
    /// <summary>RTree over object bounding boxes, rebuilt lazily when the change journal's epoch moves.</summary>
    internal static class NearCache
    {
        public static RTree Tree;
        public static Guid[] Ids;
        public static BoundingBox[] Boxes;
        public static long Epoch = -2;
        public static int DocCount = -1;
        public static RhinoDoc Doc;
        public static long BuildMs;

        public static bool Rebuilt;

        public static void Ensure(RhinoDoc doc)
        {
            Rebuilt = false;
            long ep = ChangeJournal.Installed ? ChangeJournal.Epoch : -1;
            int cnt = doc.Objects.Count;
            if (Tree != null && Doc == doc && ep >= 0 && Epoch == ep && DocCount == cnt) return;
            var sw = Stopwatch.StartNew();
            if (Tree != null) { try { Tree.Dispose(); } catch (Exception) { } Tree = null; }
            var tree = new RTree();
            var ids = new List<Guid>(cnt + 16); var boxes = new List<BoundingBox>(cnt + 16);
            foreach (var o in ObjectQuery.All(doc))
            {
                var bb = o.Geometry.GetBoundingBox(true);
                if (!bb.IsValid) continue;
                tree.Insert(bb, ids.Count);
                ids.Add(o.Id); boxes.Add(bb);
            }
            Tree = tree; Ids = ids.ToArray(); Boxes = boxes.ToArray(); Doc = doc; Epoch = ep; DocCount = cnt;
            BuildMs = sw.ElapsedMilliseconds; Rebuilt = true;
        }
    }

    public sealed class ObjectsNear : ITool
    {
        public ObjectsNear() { ChangeJournal.EnsureInstalled(); }

        public string Name { get { return "objects_near"; } }
        public string Description
        {
            get
            {
                return "Spatial query: objects near a point (point + optional radius) or inside/overlapping a bbox, nearest first, with distances. " +
                       "Uses a cached RTree over object bounding boxes (built on first use, rebuilt after the document changed); distance is to the bbox " +
                       "(0 = inside) unless exact:true, which measures to the real geometry for the candidates. Without radius, finds the limit nearest objects. " +
                       "Optional filters (objects_query filter syntax) narrow the candidates. Returns handles, bounded by limit (default 20, max 200).";
            }
        }
        public string InputSchema
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{" +
                       "\"point\":{\"$ref\":\"#/$defs/point\"}," +
                       "\"radius\":{\"type\":\"number\",\"exclusiveMinimum\":0,\"description\":\"Search radius in document units; omit to get the nearest `limit` objects\"}," +
                       "\"bbox\":{\"type\":\"object\",\"properties\":{\"min\":{\"$ref\":\"#/$defs/point\"},\"max\":{\"$ref\":\"#/$defs/point\"}},\"required\":[\"min\",\"max\"],\"description\":\"Alternative to point: objects whose bbox overlaps this box\"}," +
                       "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200,\"default\":20}," +
                       "\"exact\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Distance to real geometry (slower; point queries only)\"}," +
                       "\"filters\":" + ObjectQuery.FilterSchema + "," +
                       "\"include_guids\":{\"type\":\"boolean\",\"default\":false}" +
                       "},\"additionalProperties\":false}";
            }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        sealed class Hit { public int Index; public double D; public RhinoObject Obj; }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            ChangeJournal.EnsureInstalled();
            var sw = Stopwatch.StartNew();
            var a = new ArgReader(args);
            bool hasPoint = a.Has("point"), hasBox = a.Has("bbox");
            Point3d pt = a.Point3d("point");
            bool hasR = a.Has("radius");
            double radius = a.Double("radius", false, 0);
            if (hasR && radius <= 0) a.Error("'radius' must be > 0");
            int limit = a.Int("limit", false, 20, 1, 200);
            bool exact = a.Bool("exact");
            bool guids = a.Bool("include_guids");
            if (hasPoint == hasBox) a.Error("give exactly one of 'point' or 'bbox'");
            BoundingBox qbox = BoundingBox.Empty;
            if (hasBox)
            {
                var b = args["bbox"] as JObject;
                Point3d mn = Point3d.Unset, mx = Point3d.Unset;
                if (b == null || !TryPt(b["min"], out mn) || !TryPt(b["max"], out mx)) a.Error("'bbox' must be {min:[x,y,z], max:[x,y,z]}");
                else qbox = new BoundingBox(mn, mx);
                if (exact) a.Error("'exact' applies to point queries only");
            }
            ObjectFilter filter = null;
            JToken fl;
            if (args != null && args.TryGetValue("filters", out fl) && fl.Type != JTokenType.Null)
            {
                if (!(fl is JObject)) a.Error("'filters' must be an object");
                else filter = ObjectFilter.Parse((JObject)fl, e => a.Error(e), "'filters': ");
            }
            if (a.HasErrors) return a.ErrorResult();

            NearCache.Ensure(doc);
            var tree = NearCache.Tree; var boxes = NearCache.Boxes; var ids = NearCache.Ids;
            var hits = new List<Hit>();

            if (hasBox)
            {
                tree.Search(qbox, (s, e) => { hits.Add(new Hit { Index = e.Id, D = 0 }); });
            }
            else if (hasR)
            {
                SearchPoint(tree, boxes, pt, radius, hits);
            }
            else
            {
                // nearest-k: grow the radius until enough candidates are inside it
                var all = BoundingBox.Empty;
                foreach (var b in boxes) all.Union(b);
                double diag = all.IsValid ? all.Diagonal.Length + pt.DistanceTo(all.ClosestPoint(pt)) : 1;
                double r = Math.Max(ModelCommon.Tol(ctx) * 10, diag / 1024);
                for (int guard = 0; guard < 40; guard++)
                {
                    hits.Clear();
                    SearchPoint(tree, boxes, pt, r, hits);
                    // Count only candidates that pass the filters: otherwise unrelated nearby objects satisfy the
                    // limit, the radius stops growing, and the filter then removes them (returning too few rows).
                    int passing = filter == null ? hits.Count : hits.Count(h2 =>
                    {
                        var o2 = doc.Objects.FindId(ids[h2.Index]);
                        return o2 != null && !o2.IsDeleted && filter.Matches(doc, o2);
                    });
                    if (passing >= limit || r > diag * 2) break;
                    r *= 2;
                }
                radius = r;
            }

            // Resolve objects, apply filters, compute distances.
            var kept = new List<Hit>();
            foreach (var h in hits)
            {
                var o = doc.Objects.FindId(ids[h.Index]);
                if (o == null || o.IsDeleted) continue;
                if (filter != null && !filter.Matches(doc, o)) continue;
                h.Obj = o;
                if (hasPoint) h.D = BoxDist(boxes[h.Index], pt);
                kept.Add(h);
            }
            kept.Sort((p, q) => p.D.CompareTo(q.D));
            int examined = 0;
            if (exact && hasPoint)
            {
                var cand = kept.Take(Math.Max(limit * 4, 50)).ToList();
                foreach (var h in cand) { h.D = ExactDist(h.Obj, pt, h.D); examined++; }
                kept = cand.Where(h => !hasR || h.D <= radius + 1e-9).OrderBy(h => h.D).ToList();
            }

            var rows = kept.Take(limit).Select(h =>
            {
                var r = new Dictionary<string, object>();
                r["handle"] = ctx.Handles.For(h.Obj.Id);
                if (guids) r["guid"] = h.Obj.Id.ToString();
                r["type"] = ObjectQuery.TypeName(h.Obj);
                r["layer"] = ObjectQuery.LayerPath(doc, h.Obj);
                if (hasPoint) r["distance"] = ModelCommon.R(h.D, 4);
                return r;
            }).ToList();

            var data = new Dictionary<string, object>();
            data["total"] = kept.Count;
            data["returned"] = rows.Count;
            if (hasPoint) data["distance_basis"] = exact ? "geometry" : "bbox";
            if (hasPoint && !hasR) data["search_radius"] = ModelCommon.R(radius, 3);
            data["rows"] = rows;
            if (kept.Count > rows.Count) data["truncated"] = true;
            data["cache"] = new { objects = ids.Length, rebuilt = NearCache.Rebuilt, build_ms = NearCache.Rebuilt ? NearCache.BuildMs : 0, journal = ChangeJournal.Installed };
            data["elapsed_ms"] = sw.ElapsedMilliseconds;
            return ToolResult.Ok(data);
        }

        static void SearchPoint(RTree tree, BoundingBox[] boxes, Point3d pt, double radius, List<Hit> hits)
        {
            tree.Search(new Sphere(pt, radius), (s, e) =>
            {
                double d = BoxDist(boxes[e.Id], pt);
                if (d <= radius) hits.Add(new Hit { Index = e.Id, D = d });
            });
        }

        static double BoxDist(BoundingBox b, Point3d p)
        {
            double dx = Math.Max(Math.Max(b.Min.X - p.X, 0), p.X - b.Max.X);
            double dy = Math.Max(Math.Max(b.Min.Y - p.Y, 0), p.Y - b.Max.Y);
            double dz = Math.Max(Math.Max(b.Min.Z - p.Z, 0), p.Z - b.Max.Z);
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        static double ExactDist(RhinoObject o, Point3d p, double fallback)
        {
            try
            {
                var g = o.Geometry;
                var pnt = g as Rhino.Geometry.Point;
                if (pnt != null) return pnt.Location.DistanceTo(p);
                var c = g as Curve;
                if (c != null) { double t; if (c.ClosestPoint(p, out t)) return c.PointAt(t).DistanceTo(p); }
                var b = g as Brep;
                if (b != null) { var cp = b.ClosestPoint(p); if (cp.IsValid) return cp.DistanceTo(p); }
                var ext = g as Extrusion;
                if (ext != null) { using (var eb = ext.ToBrep()) { var cp = eb.ClosestPoint(p); if (cp.IsValid) return cp.DistanceTo(p); } }
                var m = g as Mesh;
                if (m != null) { var cp = m.ClosestPoint(p); if (cp.IsValid) return cp.DistanceTo(p); }
            }
            catch (Exception) { }
            return fallback;
        }

        static bool TryPt(JToken t, out Point3d p)
        {
            p = Point3d.Unset;
            var arr = t as JArray;
            if (arr == null || arr.Count != 3 || arr.Any(x => x.Type != JTokenType.Integer && x.Type != JTokenType.Float)) return false;
            p = new Point3d((double)arr[0], (double)arr[1], (double)arr[2]);
            return true;
        }
    }
}

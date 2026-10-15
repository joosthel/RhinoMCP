using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Model
{
    internal static class ModelCommon
    {
        public const double FarLimit = 1e5;

        public static double Tol(ToolContext ctx) { return ctx.Tol > 0 ? ctx.Tol : 0.01; }

        public static double MaxDim(BoundingBox b) { return Math.Max(b.Max.X - b.Min.X, Math.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z)); }

        public static double MaxAbs(BoundingBox b)
        {
            return Math.Max(Math.Max(Math.Abs(b.Min.X), Math.Abs(b.Max.X)),
                   Math.Max(Math.Max(Math.Abs(b.Min.Y), Math.Abs(b.Max.Y)), Math.Max(Math.Abs(b.Min.Z), Math.Abs(b.Max.Z))));
        }

        /// <summary>Objects whose size is meaningless for the "tiny" check (points, dots, text).</summary>
        public static bool SizeIsMeaningless(ObjectType t)
        {
            return t == ObjectType.Point || t == ObjectType.PointSet || t == ObjectType.TextDot || t == ObjectType.Annotation || t == ObjectType.Light;
        }

        /// <summary>64-bit hash of type + bbox corners rounded to the tolerance (duplicate candidates).</summary>
        public static long BoxHash(ObjectType t, BoundingBox b, double tol)
        {
            unchecked
            {
                long h = 1469598103934665603L;
                h = (h ^ (int)t) * 1099511628211L;
                h = (h ^ Q(b.Min.X, tol)) * 1099511628211L;
                h = (h ^ Q(b.Min.Y, tol)) * 1099511628211L;
                h = (h ^ Q(b.Min.Z, tol)) * 1099511628211L;
                h = (h ^ Q(b.Max.X, tol)) * 1099511628211L;
                h = (h ^ Q(b.Max.Y, tol)) * 1099511628211L;
                h = (h ^ Q(b.Max.Z, tol)) * 1099511628211L;
                return h;
            }
        }
        static long Q(double v, double tol) { return (long)Math.Round(v / tol); }

        public static int DefaultLayerIndex(RhinoDoc doc)
        {
            for (int i = 0; i < doc.Layers.Count; i++)
            {
                var l = doc.Layers[i];
                if (!l.IsDeleted && l.ParentLayerId == Guid.Empty && l.Name == "Default") return i;
            }
            return -1;
        }

        public static int LiveLayerCount(RhinoDoc doc)
        {
            int n = 0;
            for (int i = 0; i < doc.Layers.Count; i++) if (!doc.Layers[i].IsDeleted) n++;
            return n;
        }

        /// <summary>Definitions (by index) referenced by a document instance, directly or via nested definitions.</summary>
        public static HashSet<int> UsedDefinitions(RhinoDoc doc, out int maxNesting, Dictionary<int, int> directCounts = null, IEnumerable<RhinoObject> docObjects = null)
        {
            var used = new HashSet<int>();
            var queue = new Queue<int>();
            foreach (var o in (docObjects ?? ObjectQuery.All(doc)))
            {
                var io = o as InstanceObject;
                if (io == null) continue;
                var d = io.InstanceDefinition;
                if (d == null) continue;
                if (directCounts != null) { int c; directCounts.TryGetValue(d.Index, out c); directCounts[d.Index] = c + 1; }
                if (used.Add(d.Index)) queue.Enqueue(d.Index);
            }
            maxNesting = 0;
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                foreach (int child in Children(doc, idx))
                    if (used.Add(child)) queue.Enqueue(child);
            }
            return used;
        }

        public static List<int> Children(RhinoDoc doc, int defIndex)
        {
            var res = new List<int>();
            try
            {
                var d = doc.InstanceDefinitions[defIndex];
                if (d == null || d.IsDeleted) return res;
                var objs = d.GetObjects();
                if (objs == null) return res;
                foreach (var o in objs)
                {
                    var io = o as InstanceObject;
                    if (io != null && io.InstanceDefinition != null && !res.Contains(io.InstanceDefinition.Index)) res.Add(io.InstanceDefinition.Index);
                }
            }
            catch (Exception) { }
            return res;
        }

        /// <summary>Nesting depth of a definition: 1 = holds no blocks, 2 = holds blocks of depth 1, ...</summary>
        public static int NestDepth(RhinoDoc doc, int defIndex, Dictionary<int, int> memo, HashSet<int> visiting)
        {
            int v;
            if (memo.TryGetValue(defIndex, out v)) return v;
            if (!visiting.Add(defIndex)) return 1; // cycle guard
            int best = 0;
            foreach (int c in Children(doc, defIndex)) best = Math.Max(best, NestDepth(doc, c, memo, visiting));
            visiting.Remove(defIndex);
            memo[defIndex] = best + 1;
            return best + 1;
        }

        public static double R(double v, int digits = 3) { return Math.Round(v, digits); }
        public static double[] Pt(Point3d p) { return new[] { R(p.X), R(p.Y), R(p.Z) }; }

        public static string FirstLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", "");
            int nl = s.IndexOf('\n');
            // IsValidWithLog starts with a header line; prefer the first informative line.
            var lines = s.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            string pick = lines.FirstOrDefault(x => !x.StartsWith("Object is not valid") && !x.StartsWith("ON_")) ?? lines.FirstOrDefault() ?? "";
            return pick.Length > max ? pick.Substring(0, max) : pick;
        }
    }
}

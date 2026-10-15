using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;

namespace RhinoMCP.Tools.Context
{
    public sealed class ContextGet : ITool
    {
        public string Name { get { return "context_get"; } }
        public string Description
        {
            get
            {
                return "Compact snapshot of the active Rhino document: Rhino version, name/path, units, tolerances, " +
                       "object counts by type, top-level layers with counts, selection count, overall bbox, named views, active view. " +
                       "Call first in a session. Fails with NO_DOCUMENT if nothing is open.";
            }
        }
        public string InputSchema { get { return "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"; } }
        public ToolKind Kind { get { return ToolKind.Read; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");

            // Object counts by type, and per-layer counts (single pass).
            var byType = new Dictionary<string, int>();
            var byLayer = new Dictionary<int, int>();
            int total = 0;
            var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true };
            foreach (var o in doc.Objects.GetObjectList(settings))
            {
                total++;
                string t = o.ObjectType.ToString();
                int c; byType.TryGetValue(t, out c); byType[t] = c + 1;
                int li = o.Attributes.LayerIndex;
                byLayer.TryGetValue(li, out c); byLayer[li] = c + 1;
            }
            var types = byType.OrderByDescending(kv => kv.Value).Take(20).ToDictionary(kv => kv.Key, kv => kv.Value);

            // Top-level layers; children's counts roll up to their top-level ancestor.
            var layers = doc.Layers;
            var topOf = new Dictionary<int, int>();
            int layerCount = 0;
            for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                if (l.IsDeleted) continue;
                layerCount++;
                var cur = l;
                int guard = 0;
                while (cur.ParentLayerId != Guid.Empty && guard++ < 64)
                {
                    var p = layers.FindId(cur.ParentLayerId);
                    if (p == null) break;
                    cur = p;
                }
                topOf[i] = cur.Index;
            }
            var topCounts = new Dictionary<int, int>();
            for (int i = 0; i < layers.Count; i++)
                if (!layers[i].IsDeleted && layers[i].ParentLayerId == Guid.Empty) topCounts[i] = 0;
            foreach (var kv in byLayer)
            {
                int top;
                if (topOf.TryGetValue(kv.Key, out top) && topCounts.ContainsKey(top)) topCounts[top] += kv.Value;
            }
            var topLayers = topCounts.Take(30).Select(kv => new { name = layers[kv.Key].Name, objects = kv.Value }).ToList();

            int selected = doc.Objects.GetSelectedObjects(false, false).Count();

            object bbox = null;
            var bb = doc.Objects.BoundingBox;
            if (bb.IsValid) bbox = new { min = Pt(bb.Min), max = Pt(bb.Max) };

            var views = new List<string>();
            for (int i = 0; i < doc.NamedViews.Count && i < 20; i++) views.Add(doc.NamedViews[i].Name);

            var av = doc.Views.ActiveView;
            string activeView = av == null ? null : av.ActiveViewport.Name;

            var data = new
            {
                rhino = RhinoApp.Version.ToString(),
                document = new { name = doc.Name, path = doc.Path },
                units = ctx.Units,
                abs_tol = ctx.Tol,
                angle_tol_deg = ctx.AngleTolDeg,
                objects = new { total = total, by_type = types, types_shown_max = 20 },
                layers = new { total = layerCount, top_level = topLayers, shown = topLayers.Count },
                selected = selected,
                bbox = bbox,
                named_views = new { total = doc.NamedViews.Count, names = views },
                active_view = activeView
            };
            return ToolResult.Ok(data);
        }

        static double[] Pt(Rhino.Geometry.Point3d p)
        {
            return new[] { Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4) };
        }
    }
}

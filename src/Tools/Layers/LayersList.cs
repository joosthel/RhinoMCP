using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino.DocObjects;

namespace RhinoMCP.Tools.Layers
{
    public sealed class LayersList : ITool
    {
        public string Name { get { return "layers_list"; } }
        public string Description
        {
            get
            {
                return "Layer tree (depth-first, full paths with '::'). Counts come first, then per layer: objects (direct) and objects_total (incl. sublayers), " +
                       "color hex, linetype, print_width_mm (0 = default), plot (false = not printed), print_color, visible, locked, current. " +
                       "Use 'under' to list only the descendants of a layer path, 'max_depth' (1 = only the first level below the root/'under'), limit/cursor to page. " +
                       "Hidden/locked objects are counted. Fails with NOT_FOUND if 'under' does not exist.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"under\":{\"type\":\"string\",\"description\":\"Parent layer path, e.g. 'A::B'. Only its descendants are listed.\"}," +
                    "\"max_depth\":{\"type\":\"integer\",\"minimum\":1}," +
                    "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500,\"default\":50}," +
                    "\"cursor\":{\"type\":\"string\"}");
            }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            string under = a.String("under");
            int maxDepth = a.Int("max_depth", false, 0, 1, 64);
            int limit = a.Int("limit", false, 50, 1, 500);
            string cursor = a.String("cursor");
            int offset = 0;
            if (cursor != null && (!int.TryParse(cursor, out offset) || offset < 0)) a.Error("'cursor' is invalid; use the next_cursor value from the previous call");
            if (a.HasErrors) return a.ErrorResult();

            var tree = LayerTree.Build(doc);
            IEnumerable<Layer> start;
            int baseDepth = -1;
            if (under != null)
            {
                var u = tree.Find(under);
                if (u == null) return ToolResult.Fail("NOT_FOUND", "Layer '" + under + "' does not exist", "Call layers_list without 'under' to see the paths.");
                start = tree.Children(u);
                baseDepth = LayerTree.Depth(u);
            }
            else start = tree.Roots();

            var rows = new List<Layer>();
            foreach (var l in tree.Walk(start))
            {
                if (maxDepth > 0 && LayerTree.Depth(l) - baseDepth > maxDepth) continue;
                rows.Add(l);
            }

            int totalObjects = tree.Direct.Values.Sum();
            int emptyLayers = rows.Count(l => tree.Total[l.Id] == 0);
            int cur = doc.Layers.CurrentLayerIndex;
            var page = rows.Skip(offset).Take(limit).Select(l => Row(doc, tree, l, cur)).ToList();
            string next = offset + page.Count < rows.Count ? (offset + page.Count).ToString() : null;

            var res = ToolResult.Ok(new
            {
                layer_count = tree.All.Count,
                matching = rows.Count,
                returned = page.Count,
                objects_total = totalObjects,
                empty_layers = emptyLayers,
                current = doc.Layers.CurrentLayer.FullPath,
                layers = page
            });
            res.NextCursor = next;
            return res;
        }

        static object Row(Rhino.RhinoDoc doc, LayerTree tree, Layer l, int cur)
        {
            double w = l.PlotWeight;
            var pc = l.PlotColor;
            return new
            {
                path = l.FullPath,
                depth = LayerTree.Depth(l),
                objects = tree.Direct[l.Id],
                objects_total = tree.Total[l.Id],
                sublayers = tree.Children(l).Count,
                color = LayerUtil.Hex(l.Color),
                linetype = LayerUtil.LinetypeName(doc, l),
                print_width_mm = w < 0 ? 0.0 : w,
                plot = w >= 0,
                print_color = pc.A == 0 ? null : LayerUtil.Hex(pc),
                visible = l.IsVisible,
                locked = l.IsLocked,
                current = l.Index == cur
            };
        }
    }
}

using System;
using RhinoMCP.Tools.Shared;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino.DocObjects;

namespace RhinoMCP.Tools.Layers
{
    public sealed class LayersMoveObjects : ITool
    {
        public string Name { get { return "layers_move_objects"; } }
        public string Description
        {
            get
            {
                return "Move objects to a layer (changes only the layer, not geometry). target = handle | GUID | {set} | {query:{layer,type,name}} | array. " +
                       "layer = full path with '::'. The layer must exist unless create:true (then the parent chain is built). " +
                       "Objects already on the layer are skipped. {query} targets include hidden objects unless the query has include_hidden:false (same in every tool). " +
                       "Returns moved/skipped/failed counts, up to 100 handles and the bbox of the moved objects. Fails with NOT_FOUND if the layer is missing.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"target\":{\"$ref\":\"#/$defs/target\"}," +
                    "\"layer\":{\"type\":\"string\",\"description\":\"Destination layer path, e.g. 'A::Walls'\"}," +
                    "\"create\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Create the layer (and parents) if missing\"}",
                    "\"target\",\"layer\"");
            }
        }
        public ToolKind Kind { get { return ToolKind.Mutate; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            string path = (a.String("layer", true) ?? "").Trim();
            bool create = a.Bool("create", false);
            var objs = ObjectQuery.TargetOf(ctx, args, a, "target");
            if (path.Length > 0) { var pe = LayerUtil.ValidatePath(path); if (pe != null) a.Error("layer: " + pe); }
            if (a.HasErrors) return a.ErrorResult();
            if (objs.Count == 0) return ToolResult.Fail("NOT_FOUND", "The target matched no objects", "Check the handle/set/query.");

            var tree = LayerTree.Build(doc, false);
            var layer = tree.Find(path);
            var createdLayers = new List<string>();
            if (layer == null)
            {
                if (!create) return ToolResult.Fail("NOT_FOUND", "Layer '" + path + "' does not exist", "Create it first with layers_manage, or pass create:true.");
                string err;
                layer = LayerUtil.Ensure(doc, path, createdLayers, out err);
                if (layer == null) return ToolResult.Fail("INTERNAL", err);
            }

            int skipped = 0;
            var moved = new List<RhinoObject>();
            var failed = new List<string>();
            foreach (var o in objs)
            {
                if (o.Attributes.LayerIndex == layer.Index) { skipped++; continue; }
                bool ok;
                using (var attr = o.Attributes.Duplicate())
                {
                    attr.LayerIndex = layer.Index;
                    ok = doc.Objects.ModifyAttributes(o, attr, true);
                }
                if (ok) moved.Add(o);
                else failed.Add(ctx.Handles.For(o.Id));
            }
            var fresh = moved.Select(o => doc.Objects.FindId(o.Id)).Where(o => o != null).ToList();
            var res = ToolResult.Ok(new
            {
                layer = layer.FullPath,
                layers_created = createdLayers,
                requested = objs.Count,
                moved = moved.Count,
                skipped_already_on_layer = skipped,
                failed = failed.Count,
                failed_handles = failed.Take(20).ToList(),
                handles = fresh.Take(100).Select(o => ctx.Handles.For(o.Id)).ToList(),
                handles_truncated = fresh.Count > 100,
                bbox = LayerUtil.BBox(fresh)
            });
            res.Units = ctx.Units;
            if (failed.Count > 0) res.Warn(failed.Count + " object(s) could not be moved (see failed_handles).");
            if (layer.IsLocked) res.Warn("Destination layer is locked; moved objects are not selectable until it is unlocked.");
            return res;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Context
{
    public sealed class SystemStatusTool : ITool
    {
        public string Name { get { return "system_status"; } }
        public ToolKind Kind { get { return ToolKind.Read; } }
        public string Description
        {
            get
            {
                return "Hardware and load check for Rhino: process memory (RSS), free system RAM, Rosetta/x86_64 flag, CPU cores, document complexity " +
                       "(control points, mesh/SubD faces, render-mesh density), the 5 heaviest objects with handles, the active resource limits and recommendations. " +
                       "Call on big or slow models before heavy work. Rhino 7 Mac runs under Rosetta and is mostly single-threaded, so heavy geometry stalls it. " +
                       "Heavy tools return RESOURCE_LIMIT instead of computing past these limits. Document totals are cached per change epoch (refresh:true rescans, time-boxed to 2 s).";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"refresh\":{\"type\":\"boolean\",\"default\":false,\"description\":\"rescan the document instead of using the cached totals\"}," +
                    "\"top\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10,\"default\":5}");
            }
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var a = new ArgReader(args);
            bool refresh = a.Bool("refresh", false);
            int top = a.Int("top", false, 5, 1, 10);
            if (a.HasErrors) return a.ErrorResult();

            var lim = Resources.Limits;
            double rss = Resources.RssMb(), free = Resources.FreeSystemMb(), ram = Resources.TotalRamMb();
            bool rosetta = Resources.Rosetta();
            var rec = new List<string>();

            object doc = null;
            if (ctx.Doc != null)
            {
                var c = Resources.DocComplexity(ctx.Doc, refresh);
                var heavy = c.Top.Take(top).Select(h => new
                {
                    handle = ctx.Handles.For(h.Id),
                    type = h.Type,
                    layer = h.Layer,
                    control_points = h.ControlPoints,
                    max_surface_control_points = h.MaxSurfaceCp,
                    faces = h.Faces,
                    render_mesh_faces = h.RenderFaces
                }).ToList();
                doc = new
                {
                    objects = c.Objects,
                    undo_held_deleted = c.UndoHeldDeleted,
                    control_points_total = c.ControlPoints,
                    brep_faces = c.BrepFaces,
                    mesh_vertices = c.MeshVertices,
                    mesh_faces = c.MeshFaces,
                    subd_faces = c.SubdFaces,
                    render_meshes = new { objects = c.RenderMeshObjects, faces = c.RenderMeshFaces },
                    objects_over_surface_cp_limit = c.OverCpObjects,
                    partial = c.Partial,
                    scan_ms = c.Ms,
                    heaviest = heavy
                };
                if (c.OverCpObjects > 0)
                {
                    var hs = string.Join(", ", c.OverCp.Take(8).Select(id => ctx.Handles.For(id)));
                    rec.Add(c.OverCpObjects + " object(s) have a surface above " + lim.MaxSurfaceControlPoints + " control points (" + hs + (c.OverCp.Count > 8 ? ", ..." : "") +
                            "). Rebuild them with freeform_surface rebuild_surface (e.g. 20x20) before booleans, offsets or Make2D.");
                }
                if (c.RenderMeshFaces > lim.RenderFacesWarn)
                    rec.Add("Render meshes are very dense (" + c.RenderMeshFaces + " faces). Use performance_mode on (fast/jagged meshing, lighter display mode).");
                if (c.MeshFaces > lim.MaxMeshFaces)
                    rec.Add("Mesh objects total " + c.MeshFaces + " faces (limit " + lim.MaxMeshFaces + "). Reduce or hide them; avoid Make2D, booleans or SubD conversion on them.");
                if (c.SubdFaces > lim.MaxSubdFaces)
                    rec.Add("SubD objects total " + c.SubdFaces + " faces (limit " + lim.MaxSubdFaces + "). Keep control cages low; do not subdivide further.");
                if (c.BrepFaces > lim.Make2dMaxInputFaces)
                    rec.Add("The model has " + c.BrepFaces + " brep faces: run Make2D and clash_check per layer or set, not on everything at once.");
                if (c.UndoHeldDeleted > 1000 && c.UndoHeldDeleted > c.Objects)
                    rec.Add(c.UndoHeldDeleted + " deleted object(s) are still held in memory by the undo history (document has " + c.Objects + " live objects). " +
                            "Save the file and reopen it to free that memory (a restart is not needed); fewer undo steps (_Undo limit in Rhino options) avoids it in future.");
                if (c.Partial) rec.Add("The document scan hit its 2 s time box; totals are partial.");
            }
            if (rss >= lim.BlockRssMb) rec.Add("Rhino memory (" + Math.Round(rss) + " MB) is above block_rss_mb: heavy tools are refused. Save, delete or hide heavy objects, restart Rhino.");
            else if (rss >= lim.WarnRssMb) rec.Add("Rhino memory is high (" + Math.Round(rss) + " MB, warn at " + Math.Round(lim.WarnRssMb) + "). Save, avoid more heavy geometry, consider a restart.");
            if (free >= 0 && free < 2000) rec.Add("Only " + Math.Round(free) + " MB of system memory is free; close other apps before heavy work.");
            if (rosetta) rec.Add("Rhino runs under Rosetta (x86_64) and is mostly single-threaded: prefer modest control-point counts, low SubD levels and small captures.");
            if (rec.Count == 0) rec.Add("No resource problems detected.");

            var res = ToolResult.Ok(new
            {
                process = new { pid = System.Diagnostics.Process.GetCurrentProcess().Id, rss_mb = Math.Round(rss), private_mb = Math.Round(Resources.PrivateMb()), rosetta = rosetta, arch = IntPtr.Size == 8 ? "x64" : "x86" },
                system = new { ram_total_mb = ram, ram_free_mb = free < 0 ? (double?)null : free, cores_physical = Resources.PhysicalCores(), cores_logical = Resources.LogicalCores() },
                window = RhinoMCP.Tools.Script.MacWindow.State(),
                document = doc,
                limits = lim.ToJson(),
                recommendations = rec
            });
            return res;
        }
    }
}

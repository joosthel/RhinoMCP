using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Display;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Drawing
{
    public sealed class DrawingClippingPlane : ITool
    {
        public string Name { get { return "drawing_clipping_plane"; } }
        public ToolKind Kind { get { return ToolKind.Mutate; } }
        public string Description
        {
            get
            {
                return "Create, list or delete clipping planes used to cut plans and sections for drawing_make2d. action=create takes ONE of: " +
                       "{z, looking?:'down'|'up'} (plan cut at height z: looking down removes everything above z), " +
                       "{section_line:{from:[x,y,z],to:[x,y,z]}, looking?:'left'|'right'} (vertical section plane through the line; the viewer stands on the 'looking' side of from->to and sees the far side; default left), " +
                       "or {plane:{origin,x_axis,y_axis}} (the plane's normal side is removed). Returns the handle, the plane and the matching 'view' to pass to drawing_make2d. " +
                       "By default the plane clips no viewport (the model stays untouched on screen); give apply_to_views:['Top',...] to also clip those viewports. action=list returns all clipping planes, action=delete removes handles.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"action\":{\"enum\":[\"create\",\"list\",\"delete\"]}," +
                    "\"z\":{\"type\":\"number\",\"description\":\"plan cut height in document units\"}," +
                    "\"looking\":{\"type\":\"string\",\"description\":\"z cut: down|up; section_line: left|right\"}," +
                    "\"section_line\":{\"type\":\"object\",\"properties\":{\"from\":{\"$ref\":\"#/$defs/point\"},\"to\":{\"$ref\":\"#/$defs/point\"}},\"required\":[\"from\",\"to\"]}," +
                    "\"plane\":{\"$ref\":\"#/$defs/plane\"}," +
                    "\"size\":{\"type\":\"number\",\"description\":\"edge length of the plane widget; default from the model size\"}," +
                    "\"name\":{\"type\":\"string\"},\"layer\":{\"type\":\"string\"}," +
                    "\"apply_to_views\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"viewport names this plane should clip on screen\"}," +
                    "\"target\":{\"$ref\":\"#/$defs/target\",\"description\":\"for delete: clipping plane handles\"}",
                    "\"action\"");
            }
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            var a = new ArgReader(args);
            string action = (a.String("action", true) ?? "").ToLowerInvariant();
            if (action != "create" && action != "list" && action != "delete" && action != "") a.Error("'action' must be create, list or delete");
            if (a.HasErrors) return a.ErrorResult();
            if (action == "list") return List(ctx);
            if (action == "delete") return Delete(ctx, args, a);
            return Create(ctx, args, a);
        }

        static ToolResult List(ToolContext ctx)
        {
            var doc = ctx.Doc;
            var res = new List<object>();
            var s = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true, ObjectTypeFilter = ObjectType.ClipPlane };
            foreach (var o in doc.Objects.GetObjectList(s))
            {
                var cp = o as ClippingPlaneObject;
                if (cp == null) continue;
                var pl = cp.ClippingPlaneGeometry.Plane;
                var vps = new List<string>();
                foreach (var id in cp.ClippingPlaneGeometry.ViewportIds())
                {
                    var v = doc.Views.Find(id);
                    vps.Add(v == null ? id.ToString() : v.ActiveViewport.Name);
                }
                res.Add(new { handle = ctx.Handles.For(o.Id), name = o.Attributes.Name, plane = PlaneJson(pl), normal = GeoUtil.Arr(pl.ZAxis), clips_views = vps });
            }
            return ToolResult.Ok(new { total = res.Count, clipping_planes = res });
        }

        static ToolResult Delete(ToolContext ctx, JObject args, ArgReader a)
        {
            var objs = ObjectQuery.Resolve(ctx, args["target"], a, "target", true);
            if (a.HasErrors) return a.ErrorResult();
            var deleted = new List<string>();
            foreach (var o in objs)
            {
                if (!(o is ClippingPlaneObject)) { a.Error("'target': " + ctx.Handles.For(o.Id) + " is not a clipping plane"); continue; }
                string h = ctx.Handles.For(o.Id);
                if (ctx.Doc.Objects.Delete(o.Id, true)) deleted.Add(h);
            }
            if (a.HasErrors) return a.ErrorResult();
            return ToolResult.Ok(new { deleted = deleted });
        }

        ToolResult Create(ToolContext ctx, JObject args, ArgReader a)
        {
            var doc = ctx.Doc;
            Plane plane;
            Vector3d look, up = Vector3d.ZAxis;
            string desc;
            int modes = (args["z"] != null ? 1 : 0) + (args["section_line"] != null ? 1 : 0) + (args["plane"] != null ? 1 : 0);
            if (modes != 1) return ToolResult.Fail("INVALID_ARGS", "give exactly one of 'z', 'section_line' or 'plane' (got " + modes + ")",
                "Plan cut: {action:'create', z:1200, looking:'down'}. Section: {action:'create', section_line:{from:[0,0,0],to:[10,0,0]}}.");
            string looking = (a.String("looking", false, null) ?? "").ToLowerInvariant();
            if (args["z"] != null)
            {
                double z = a.Double("z", true);
                if (looking == "") looking = "down";
                if (looking != "down" && looking != "up") a.Error("'looking' must be down or up for a z cut");
                if (a.HasErrors) return a.ErrorResult();
                // The removed side is the side the plane's normal points to: looking down removes what is above.
                var n = looking == "down" ? Vector3d.ZAxis : -Vector3d.ZAxis;
                plane = new Plane(new Point3d(0, 0, z), Vector3d.XAxis, n.Z > 0 ? Vector3d.YAxis : -Vector3d.YAxis);
                look = -n; up = looking == "down" ? Vector3d.YAxis : -Vector3d.YAxis;
                desc = "plan cut at z=" + z + " looking " + looking;
            }
            else if (args["section_line"] != null)
            {
                var sl = args["section_line"] as JObject;
                double[] f, t;
                if (sl == null || !P.TryNums(sl["from"], 3, 3, out f) || !P.TryNums(sl["to"], 3, 3, out t))
                    return ToolResult.Fail("INVALID_ARGS", "'section_line' needs from and to as [x,y,z]");
                if (looking == "") looking = "left";
                if (looking != "left" && looking != "right") a.Error("'looking' must be left or right for a section line");
                if (a.HasErrors) return a.ErrorResult();
                var p0 = new Point3d(f[0], f[1], f[2]); var p1 = new Point3d(t[0], t[1], t[2]);
                var dir = p1 - p0; dir.Z = 0;
                if (!dir.Unitize()) return ToolResult.Fail("INVALID_ARGS", "'section_line' must have a horizontal extent (from and to differ in X/Y)");
                // left of the direction of travel = dir rotated +90 degrees about Z
                var left = new Vector3d(-dir.Y, dir.X, 0);
                // the viewer stands on the 'looking' side; that side is removed (normal points to the viewer) and the view looks the other way
                var normal = looking == "left" ? left : -left;
                var xaxis = Vector3d.CrossProduct(Vector3d.ZAxis, normal); // lies in the plane
                plane = new Plane(p0, xaxis, Vector3d.ZAxis);
                if (Vector3d.Multiply(plane.ZAxis, normal) < 0) plane = new Plane(p0, xaxis, -Vector3d.ZAxis);
                look = -normal; up = Vector3d.ZAxis;
                desc = "section through the line looking " + looking;
            }
            else
            {
                string perr;
                var fr = GeoUtil.ParsePlane(args["plane"], ctx, null, out perr);
                if (fr == null) return ToolResult.Fail("INVALID_ARGS", "'plane': " + perr);
                plane = fr.Plane; look = -plane.ZAxis;
                up = Math.Abs(Vector3d.Multiply(look, Vector3d.ZAxis)) > 0.9999 ? Vector3d.YAxis : Vector3d.ZAxis;
                desc = "custom plane";
            }

            var bb = BoundingBox.Empty;
            foreach (var o in ObjectQuery.All(doc)) { var b = o.Geometry.GetBoundingBox(true); if (b.IsValid) bb.Union(b); }
            double size = a.Double("size", false, double.NaN);
            if (double.IsNaN(size)) size = bb.IsValid ? Math.Max(bb.Diagonal.Length * 1.2, 10) : 1000;
            if (size <= 0) a.Error("'size' must be > 0");
            var vpIds = new List<Guid>();
            foreach (var vn in ReadStrings(args["apply_to_views"], a))
            {
                var v = doc.Views.Find(vn, false);
                if (v == null) a.Error("apply_to_views: no viewport named '" + vn + "'");
                else vpIds.Add(v.ActiveViewport.Id);
            }
            if (a.HasErrors) return a.ErrorResult();

            var at = new ObjectAttributes();
            string name = a.String("name");
            if (name != null) at.Name = name;
            string layer = a.String("layer");
            if (layer != null) { try { at.LayerIndex = GeoUtil.EnsureLayer(doc, layer); } catch (Exception ex) { return ToolResult.Fail("INVALID_ARGS", "'layer': " + ex.Message); } }
            // Plane widget centered on the model so it is visible next to the object
            var center = bb.IsValid ? plane.ClosestPoint(bb.Center) : plane.Origin;
            var shown = plane; shown.Origin = center;
            // Rhino refuses a clipping plane without a viewport: create it on the active view, then release the view again.
            var carrier = doc.Views.ActiveView;
            if (carrier == null || carrier is RhinoPageView) carrier = doc.Views.GetViewList(true, false).FirstOrDefault(v => !(v is RhinoPageView));
            var createIds = new List<Guid>(vpIds);
            bool releaseCarrier = false;
            if (createIds.Count == 0 && carrier != null) { createIds.Add(carrier.ActiveViewport.Id); releaseCarrier = true; }
            Guid id = doc.Objects.AddClippingPlane(shown, size, size, createIds, at);
            if (id == Guid.Empty) return ToolResult.Fail("CREATE_FAILED", "Rhino could not create the clipping plane");
            if (releaseCarrier)
            {
                var cpo = doc.Objects.FindId(id) as ClippingPlaneObject;
                if (cpo != null) cpo.RemoveClipViewport(carrier.ActiveViewport, true);
            }
            var viewHint = new { direction = GeoUtil.Arr(look), up = GeoUtil.Arr(up) };
            return ToolResult.Ok(new
            {
                handle = ctx.Handles.For(id),
                description = desc,
                plane = PlaneJson(plane),
                normal = GeoUtil.Arr(plane.ZAxis),
                removes = "everything on the side the normal points to",
                view = viewHint,
                clips_views = vpIds.Count
            });
        }

        static List<string> ReadStrings(JToken t, ArgReader a)
        {
            var res = new List<string>();
            var arr = t as JArray;
            if (arr == null) return res;
            foreach (var x in arr) if (x.Type == JTokenType.String) res.Add((string)x);
            return res;
        }

        static object PlaneJson(Plane p) { return new { origin = GeoUtil.Arr(p.Origin), x_axis = GeoUtil.Arr(p.XAxis), y_axis = GeoUtil.Arr(p.YAxis) }; }
    }
}

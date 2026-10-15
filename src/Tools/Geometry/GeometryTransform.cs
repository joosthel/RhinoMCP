using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Geometry
{
    public sealed class GeometryTransform : ITool
    {
        public string Name { get { return "geometry_transform"; } }
        public ToolKind Kind { get { return ToolKind.Mutate; } }
        public string Description
        {
            get
            {
                return "Transform objects. op: move{vector} | rotate{angle_deg, axis?=frame Z, center?=frame origin} | scale{factor | xyz[sx,sy,sz], origin?} | mirror{plane} | " +
                       "orient{from, to} (plane to plane) | array_linear{count, vector} | array_polar{count, angle_deg?=360, center?, axis?} | array_grid{nx,ny,nz, spacing(number|[dx,dy,dz])}. " +
                       "move/rotate/scale/mirror/orient modify in place (same handles) unless copy:true, which leaves the originals and returns new handles. " +
                       "Arrays always keep the original; 'count' (and nx*ny*nz) includes the original, new copies are returned (max 2000). " +
                       "Vectors, points and planes are in 'frame' (default world_xy) and converted to world. Returns affected handles and the resulting bbox. " +
                       "Non-uniform scale of extrusions/primitives may convert them to polysurfaces.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"target\":{\"$ref\":\"#/$defs/target\"}," +
                    "\"op\":{\"type\":\"string\",\"enum\":[\"move\",\"rotate\",\"scale\",\"mirror\",\"orient\",\"array_linear\",\"array_polar\",\"array_grid\"]}," +
                    "\"copy\":{\"type\":\"boolean\",\"description\":\"copy instead of modifying (move/rotate/scale/mirror/orient)\"}," +
                    "\"frame\":{\"$ref\":\"#/$defs/plane\",\"description\":\"coordinate frame for vectors/points/planes (default world_xy)\"}," +
                    "\"vector\":{\"$ref\":\"#/$defs/point\"}," +
                    "\"angle_deg\":{\"type\":\"number\"}," +
                    "\"axis\":{\"$ref\":\"#/$defs/point\"}," +
                    "\"center\":{\"$ref\":\"#/$defs/point\"}," +
                    "\"origin\":{\"$ref\":\"#/$defs/point\"}," +
                    "\"factor\":{\"type\":\"number\"}," +
                    "\"xyz\":{\"$ref\":\"#/$defs/point\"}," +
                    "\"plane\":{\"$ref\":\"#/$defs/plane\",\"description\":\"mirror plane\"}," +
                    "\"from\":{\"$ref\":\"#/$defs/plane\"},\"to\":{\"$ref\":\"#/$defs/plane\"}," +
                    "\"count\":{\"type\":\"integer\"},\"nx\":{\"type\":\"integer\"},\"ny\":{\"type\":\"integer\"},\"nz\":{\"type\":\"integer\"}," +
                    "\"spacing\":{\"oneOf\":[{\"type\":\"number\"},{\"$ref\":\"#/$defs/point\"}]}," +
                    "\"include_guids\":{\"type\":\"boolean\"}",
                    "\"target\",\"op\"");
            }
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var p = new P(args, ctx, new Frame(Plane.WorldXY, "world_xy"));
            var objs = p.Objs("target");
            string op = p.Str("op", true);
            bool copy = p.Bool("copy");
            bool guids = p.Bool("include_guids");
            if (p.Has("frame"))
            {
                string perr;
                var f = GeoUtil.ParsePlane(p.Tok("frame"), ctx, null, out perr);
                if (f == null) p.Err("'frame': " + perr); else p.Frame = f;
            }
            if (p.Errors.Count > 0) return Bad(p);
            if (objs.Count == 0) return ToolResult.Fail("NOT_FOUND", "target resolved to no objects", "Check the handles with objects_query / context_get.");
            var fr = p.Frame;

            // Per-object transform list: each entry is the transform of one resulting copy (arrays) or the single transform.
            var xforms = new List<Transform>();
            bool isArray = false;
            switch (op)
            {
                case "move":
                    {
                        var v = p.Vec("vector", true);
                        if (p.Errors.Count > 0) break;
                        xforms.Add(Transform.Translation(v));
                        break;
                    }
                case "rotate":
                    {
                        double ang = p.Num("angle_deg", true);
                        var axis = p.Has("axis") ? p.Vec("axis") : fr.Plane.ZAxis;
                        var center = p.Has("center") ? p.Pt("center") : fr.Pt(Point3d.Origin);
                        if (p.Errors.Count > 0) break;
                        if (axis.Length < 1e-12) { p.Err("'axis' has zero length"); break; }
                        xforms.Add(Transform.Rotation(RhinoMath.ToRadians(ang), axis, center));
                        break;
                    }
                case "scale":
                    {
                        var origin = p.Has("origin") ? p.Pt("origin") : fr.Pt(Point3d.Origin);
                        if (p.Has("factor") == p.Has("xyz")) { p.Err("give exactly one of 'factor' or 'xyz'"); break; }
                        if (p.Has("factor"))
                        {
                            double s = p.Num("factor", true);
                            if (p.Errors.Count > 0) break;
                            if (Math.Abs(s) < 1e-12) { p.Err("'factor' must be non-zero"); break; }
                            xforms.Add(Transform.Scale(origin, s));
                        }
                        else
                        {
                            var s = p.LocalPt("xyz", true);
                            if (p.Errors.Count > 0) break;
                            if (Math.Abs(s.X) < 1e-12 || Math.Abs(s.Y) < 1e-12 || Math.Abs(s.Z) < 1e-12) { p.Err("'xyz' components must be non-zero"); break; }
                            var pl = fr.Plane; pl.Origin = origin;
                            xforms.Add(Transform.Scale(pl, s.X, s.Y, s.Z));
                        }
                        break;
                    }
                case "mirror":
                    {
                        Plane pl;
                        if (!p.TryPlane("plane", out pl, true)) break;
                        xforms.Add(Transform.Mirror(pl));
                        break;
                    }
                case "orient":
                    {
                        Plane a, b;
                        bool ok = p.TryPlane("from", out a, true);
                        ok &= p.TryPlane("to", out b, true);
                        if (!ok) break;
                        xforms.Add(Transform.PlaneToPlane(a, b));
                        break;
                    }
                case "array_linear":
                    {
                        int n = p.Int("count", true); var v = p.Vec("vector", true);
                        if (p.Errors.Count > 0) break;
                        if (n < 2 || n > 2000) { p.Err("'count' must be 2..2000 (it includes the original)"); break; }
                        isArray = true;
                        for (int i = 1; i < n; i++) xforms.Add(Transform.Translation(v * i));
                        break;
                    }
                case "array_polar":
                    {
                        int n = p.Int("count", true); double ang = p.Num("angle_deg", false, 360);
                        var axis = p.Has("axis") ? p.Vec("axis") : fr.Plane.ZAxis;
                        var center = p.Has("center") ? p.Pt("center") : fr.Pt(Point3d.Origin);
                        if (p.Errors.Count > 0) break;
                        if (n < 2 || n > 2000) { p.Err("'count' must be 2..2000 (it includes the original)"); break; }
                        if (axis.Length < 1e-12) { p.Err("'axis' has zero length"); break; }
                        isArray = true;
                        double step = Math.Abs(ang) >= 360.0 - 1e-9 ? ang / n : ang / (n - 1);
                        for (int i = 1; i < n; i++) xforms.Add(Transform.Rotation(RhinoMath.ToRadians(step * i), axis, center));
                        break;
                    }
                case "array_grid":
                    {
                        int nx = p.Has("nx") ? p.Int("nx") : 1, ny = p.Has("ny") ? p.Int("ny") : 1, nz = p.Has("nz") ? p.Int("nz") : 1;
                        Vector3d sp = Vector3d.Unset;
                        var st = p.Tok("spacing");
                        if (st == null) p.Err("'spacing' is required (number or [dx,dy,dz])");
                        else if (st.Type == JTokenType.Integer || st.Type == JTokenType.Float) { double d = (double)st; sp = new Vector3d(d, d, d); }
                        else
                        {
                            double[] v;
                            if (!P.TryNums(st, 3, 3, out v)) p.Err("'spacing' must be a number or [dx,dy,dz]"); else sp = new Vector3d(v[0], v[1], v[2]);
                        }
                        if (p.Errors.Count > 0) break;
                        if (nx < 1 || ny < 1 || nz < 1 || (long)nx * ny * nz < 2 || (long)nx * ny * nz > 2000) { p.Err("nx, ny, nz must be >= 1 with 2 <= nx*ny*nz <= 2000 (includes the original)"); break; }
                        isArray = true;
                        for (int k = 0; k < nz; k++)
                            for (int j = 0; j < ny; j++)
                                for (int i = 0; i < nx; i++)
                                {
                                    if (i == 0 && j == 0 && k == 0) continue;
                                    xforms.Add(Transform.Translation(fr.Vec(new Vector3d(sp.X * i, sp.Y * j, sp.Z * k))));
                                }
                        break;
                    }
                default:
                    if (op != null) p.Err("unknown op '" + op + "'. Valid: move, rotate, scale, mirror, orient, array_linear, array_polar, array_grid");
                    break;
            }
            if (p.Errors.Count > 0) return Bad(p);
            if (objs.Count * xforms.Count > 5000) return ToolResult.Fail("INVALID_ARGS", "too many resulting objects (" + objs.Count * xforms.Count + ", max 5000)", "Reduce the target set or the array count.");

            var doc = ctx.Doc;
            var map = new List<object>();
            var boxes = new List<BoundingBox>();
            var newIds = new List<Guid>();
            var modified = new List<string>();
            int failed = 0;
            bool inPlace = !isArray && !copy;
            foreach (var o in objs)
            {
                var made = new List<string>();
                if (inPlace)
                {
                    var id = doc.Objects.Transform(o.Id, xforms[0], true);
                    if (id == Guid.Empty) { failed++; map.Add(new { source = ctx.Handles.For(o.Id), error = "Rhino could not transform this object (locked or unsupported type?)" }); continue; }
                    var ro = doc.Objects.FindId(id);
                    if (ro != null) boxes.Add(ro.Geometry.GetBoundingBox(true));
                    modified.Add(ctx.Handles.For(id));
                    continue;
                }
                foreach (var xf in xforms)
                {
                    var id = doc.Objects.Transform(o.Id, xf, false);
                    if (id == Guid.Empty) { failed++; continue; }
                    newIds.Add(id);
                    made.Add(ctx.Handles.For(id));
                    var ro = doc.Objects.FindId(id);
                    if (ro != null) boxes.Add(ro.Geometry.GetBoundingBox(true));
                }
                map.Add(new { source = ctx.Handles.For(o.Id), handles = made });
            }
            if (inPlace && modified.Count == 0)
                return ToolResult.Fail("GEOMETRY_FAILED", "No object could be transformed", "Objects may be locked; unlock them first.", new { items = map });
            var res = new Dictionary<string, object>();
            res["op"] = op;
            res["count"] = inPlace ? modified.Count : newIds.Count;
            res["bbox"] = GeoUtil.BBox(GeoUtil.Union(boxes));
            if (inPlace) res["modified"] = modified;
            else
            {
                res["created"] = newIds.Select(x => ctx.Handles.For(x)).ToList();
                res["by_source"] = map;
                if (guids) res["guids"] = newIds.Select(x => x.ToString()).ToList();
            }
            var tr = ToolResult.Ok(res);
            if (failed > 0) tr.Warn(failed + " transforms failed (locked objects?)");
            return tr;
        }

        static ToolResult Bad(P p)
        {
            return ToolResult.Fail("INVALID_ARGS", string.Join("; ", p.Errors), "Fix the arguments and call again. See the tool description for the parameters of each op.", new { errors = p.Errors });
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Freeform
{
    public sealed class SubDTool : ITool
    {
        public string Name { get { return "subd"; } }
        public ToolKind Kind { get { return ToolKind.Mutate; } }
        public string Description
        {
            get
            {
                return "SubD (subdivision surface) modelling, one op per call; creates a NEW object unless replace=true (then the source target is deleted). " +
                       "Common: layer?, name?, replace?, plane? (frame for points). Result: handle, faces, edges, vertices, is_solid, bbox. " +
                       "Ops: create_from_mesh{mesh (mesh/brep handle) | primitive:{type:'box',corner,size:[dx,dy,dz],nx?,ny?,nz?} | {type:'cylinder',base,radius,height,around?=8,vertical?=1,caps?=true,cap_edges?=crease|smooth}; corners?=smooth|sharp} | " +
                       "from_loft{curves[>=3] (curve specs), closed?, add_corners?, add_creases?, divisions?=1} | from_surface{target (surface or {target,face}), method?=fit|control_net, corners?=true} | " +
                       "interpolate_points{points: grid rows x cols of [x,y,z]} (quad SubD whose limit surface passes through the points) | subdivide{target, levels?=1 (1..3)} (Catmull-Clark on the control net) | " +
                       "offset{target, distance, solid?=false} | crease_edges{target, edges?:[index..] | all_sharp_by_angle?:deg | in_box?:{min,max}, tag?=crease|smooth} (edge indices = order in 'info' op) | " +
                       "to_brep{target} (NURBS brep) | join{targets[], tol?, creases?=true} | info{target} (counts and an edge list). " +
                       "R7 has no SubD booleans: convert with to_brep first. Unsupported or failing ops return an error with guidance.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"op\":{\"type\":\"string\",\"enum\":[\"create_from_mesh\",\"from_loft\",\"from_surface\",\"interpolate_points\",\"subdivide\",\"offset\",\"crease_edges\",\"to_brep\",\"join\",\"info\"]}," +
                    "\"target\":{\"$ref\":\"#/$defs/target\"},\"targets\":{\"$ref\":\"#/$defs/target\"}," +
                    "\"mesh\":{\"type\":\"string\"},\"primitive\":{\"type\":\"object\"},\"curves\":{\"type\":\"array\"},\"points\":{\"type\":\"array\"}," +
                    "\"levels\":{\"type\":\"integer\"},\"distance\":{\"type\":\"number\"},\"solid\":{\"type\":\"boolean\"}," +
                    "\"edges\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"all_sharp_by_angle\":{\"type\":\"number\"},\"in_box\":{\"type\":\"object\"},\"tag\":{\"type\":\"string\"}," +
                    "\"method\":{\"type\":\"string\"},\"corners\":{},\"closed\":{\"type\":\"boolean\"},\"add_corners\":{\"type\":\"boolean\"},\"add_creases\":{\"type\":\"boolean\"},\"divisions\":{\"type\":\"integer\"}," +
                    "\"tol\":{\"type\":\"number\"},\"creases\":{\"type\":\"boolean\"},\"replace\":{\"type\":\"boolean\"},\"force\":{\"type\":\"boolean\",\"description\":\"override max_subd_levels / max_subd_faces (not block_rss_mb)\"}," +
                    "\"plane\":{\"$ref\":\"#/$defs/plane\"},\"layer\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"user_text\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}}",
                    "\"op\"");
            }
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active document");
            var p = new P(args, ctx, new Frame(Plane.WorldXY, "world_xy"));
            string op = p.Str("op", true);
            if (p.Has("plane"))
            {
                string perr;
                var f = GeoUtil.ParsePlane(p.Tok("plane"), ctx, null, out perr);
                if (f == null) p.Err("'plane': " + perr); else p.Frame = f;
            }
            bool replace = p.Bool("replace");
            var attr = GeoUtil.Attributes(p, doc, null);
            if (p.Errors.Count > 0) return FF.ArgFail(p.Errors);

            var blocked = Resources.CheckRss("subd");
            if (blocked != null) return blocked;
            var guard = Resources.Begin("subd");
            var pre = Preflight(op, p, ctx);
            if (pre != null) return pre;

            var r = new Dictionary<string, object>();
            var ids = new List<Guid>();
            Guid deleteId = Guid.Empty;
            string err = null;
            using (var bag = new Bag())
            {
                try
                {
                    GeometryBase g = Run(op, p, ctx, bag, r, out deleteId);
                    if (p.Errors.Count > 0) err = string.Join("; ", p.Errors);
                    else if (op == "info") { /* r filled */ }
                    else
                    {
                        err = Add(ctx, g, attr, r, ids);
                        if (err == null && replace && deleteId != Guid.Empty) { doc.Objects.Delete(deleteId, true); r["deleted_source"] = true; }
                    }
                }
                catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
                if (err != null) foreach (var id in ids) { try { doc.Objects.Delete(id, true); } catch (Exception) { } }
            }
            if (err != null)
            {
                bool unsupported = err.StartsWith("UNSUPPORTED");
                return ToolResult.Fail(unsupported ? "UNSUPPORTED" : (p.Errors.Count > 0 ? "INVALID_ARGS" : "GEOMETRY_FAILED"), err,
                    "SubD ops need a valid SubD target (subd create_from_mesh/from_loft/from_surface) or valid inputs. Booleans are not available on SubD: use op to_brep first.");
            }
            r["op"] = op;
            return guard.Finish(ToolResult.Ok(r));
        }

        /// <summary>Resource pre-flight: SubD level and face-count estimates before anything is computed.</summary>
        static ToolResult Preflight(string op, P p, ToolContext ctx)
        {
            var lim = Resources.Limits;
            bool force = p.Bool("force");
            if (p.Errors.Count > 0) return null;       // let the normal argument errors surface
            if (op == "subdivide")
            {
                int lv = p.Int("levels", false, 1);
                if (p.Errors.Count > 0) { p.Errors.Clear(); return null; }
                if (lv > lim.MaxSubdLevels && !force)
                    return Resources.LimitFail("max_subd_levels", lv, lim.MaxSubdLevels,
                        "subdivide levels=" + lv + " exceeds max_subd_levels (" + lim.MaxSubdLevels + "). Each level multiplies the face count by 4.",
                        "Subdivide 1 level at a time and check subd info; usually 1-2 levels are enough. A SubD is already smooth at its control cage; you rarely need to subdivide it.", true);
                var t = p.Tok("target");
                var o = t == null ? null : FF.OneObject(p, t, "target");
                var sd = o == null ? null : o.Geometry as SubD;
                if (sd != null && lv >= 1)
                {
                    double faces = (double)sd.Faces.Count * Math.Pow(4, lv);
                    if (faces > lim.MaxSubdFaces && !force)
                        return Resources.LimitFail("max_subd_faces", faces, lim.MaxSubdFaces,
                            "subdividing " + sd.Faces.Count + " faces " + lv + " level(s) would give ~" + (long)faces + " faces (limit " + lim.MaxSubdFaces + ").",
                            "Subdivide fewer levels (1 level = x4 faces), or work on a lower-face cage.", true);
                }
                p.Errors.Clear();                      // errors found while peeking are reported by the real op
            }
            else if (op == "to_brep" || op == "offset")
            {
                var t = p.Tok("target");
                var o = t == null ? null : FF.OneObject(p, t, "target");
                var sd = o == null ? null : o.Geometry as SubD;
                if (sd != null && sd.Faces.Count > lim.MaxSubdFaces && !force)
                    return Resources.LimitFail("max_subd_faces", sd.Faces.Count, lim.MaxSubdFaces,
                        op + " on a SubD with " + sd.Faces.Count + " faces exceeds max_subd_faces (" + lim.MaxSubdFaces + ").",
                        "Work on a lower-face cage, or split the SubD into parts first.", true);
                p.Errors.Clear();
            }
            else if (op == "create_from_mesh" && p.Has("mesh"))
            {
                var o = FF.OneObject(p, p.Tok("mesh"), "mesh");
                var m = o == null ? null : o.Geometry as Mesh;
                if (m != null && m.Faces.Count > lim.MaxSubdFaces && !force)
                    return Resources.LimitFail("max_subd_faces", m.Faces.Count, lim.MaxSubdFaces,
                        "create_from_mesh with " + m.Faces.Count + " mesh faces exceeds max_subd_faces (" + lim.MaxSubdFaces + ").",
                        "Use a low-poly quad cage (a few hundred faces); reduce the mesh first.", true);
                p.Errors.Clear();
            }
            return null;
        }

        static string Add(ToolContext ctx, GeometryBase g, ObjectAttributes attr, Dictionary<string, object> r, List<Guid> ids)
        {
            if (g == null) return "operation returned no geometry";
            var sd = g as SubD;
            if (sd == null) return FF.AddAndDescribe(ctx, g, attr, r, ids);
            if (!sd.IsValid) return "result SubD is invalid";
            Guid id = ctx.Doc.Objects.AddSubD(sd, attr);
            if (id == Guid.Empty) return "Rhino rejected the SubD";
            ids.Add(id);
            r["handle"] = ctx.Handles.For(id);
            foreach (var kv in Info(sd)) r[kv.Key] = kv.Value;
            return null;
        }

        static Dictionary<string, object> Info(SubD sd)
        {
            var d = new Dictionary<string, object>();
            d["type"] = "subd";
            d["faces"] = sd.Faces.Count; d["edges"] = sd.Edges.Count; d["vertices"] = sd.Vertices.Count;
            d["is_solid"] = sd.IsSolid;
            d["bbox"] = GeoUtil.BBox(sd.GetBoundingBox(true));
            return d;
        }

        static SubD Target(P p, string field, Bag bag, out Guid id)
        {
            id = Guid.Empty;
            var t = p.Tok(field);
            if (t == null) { p.Err("'" + field + "' is required (handle of a SubD)"); return null; }
            var o = FF.OneObject(p, t, field);
            if (o == null) return null;
            var sd = o.Geometry as SubD;
            if (sd == null) { p.Err("'" + field + "': " + p.Ctx.Handles.For(o.Id) + " is " + o.ObjectType + ", not a SubD. Create one with create_from_mesh / from_loft / from_surface"); return null; }
            id = o.Id;
            return sd;
        }

        static SubD DupTarget(P p, string field, Bag bag, out Guid id)
        {
            var sd = Target(p, field, bag, out id);
            if (sd == null) return null;
            var d = sd.Duplicate() as SubD;
            if (d == null) { p.Err("could not duplicate the SubD"); return null; }
            return bag.Add(d);
        }

        static Vector3d FaceNormal(SubDFace f)
        {
            var n = Vector3d.Zero;
            int c = f.VertexCount;
            for (int i = 0; i < c; i++)
            {
                var a = f.VertexAt(i).ControlNetPoint; var b = f.VertexAt((i + 1) % c).ControlNetPoint;
                n.X += (a.Y - b.Y) * (a.Z + b.Z);
                n.Y += (a.Z - b.Z) * (a.X + b.X);
                n.Z += (a.X - b.X) * (a.Y + b.Y);
            }
            n.Unitize();
            return n;
        }

        static GeometryBase Run(string op, P p, ToolContext ctx, Bag bag, Dictionary<string, object> r, out Guid src)
        {
            src = Guid.Empty;
            double tol = ctx.Tol;
            switch (op)
            {
                case "create_from_mesh": return FromMesh(p, ctx, bag);
                case "from_loft":
                    {
                        var cs = FF.CurveList(p, "curves", 3, 200, bag);
                        bool closed = p.Bool("closed"), corners = p.Bool("add_corners"), creases = p.Bool("add_creases");
                        int div = p.Int("divisions", false, 1);
                        if (p.Errors.Count > 0) return null;
                        FF.AlignCurves(cs);
                        var ns = new List<NurbsCurve>();
                        foreach (var c in cs)
                        {
                            var n = NurbsCurve.CreateSubDFriendly(c);
                            if (n == null) n = c.ToNurbsCurve();
                            if (n == null) { p.Err("a curve could not be converted to a NURBS curve"); return null; }
                            ns.Add(bag.Add(n));
                        }
                        var sd = SubD.CreateFromLoft(ns, closed, corners, creases, div);
                        if (sd == null) { p.Err("SubD loft failed. Curves must be all open or all closed, in order, with similar point counts"); return null; }
                        return bag.Add(sd);
                    }
                case "from_surface":
                    {
                        var rf = FF.ResolveRef(p, p.Tok("target"), "target", true, false, bag);
                        string method = p.Str("method", false, "fit");
                        bool corners = true;
                        if (p.Has("corners")) corners = p.Bool("corners");
                        if (rf == null || p.Errors.Count > 0) return null;
                        var m = method == "control_net" ? SubDFromSurfaceMethods.FromNurbsControlNet : SubDFromSurfaceMethods.SubDFriendlyFit;
                        if (method != "fit" && method != "control_net") { p.Err("'method' must be fit | control_net"); return null; }
                        src = rf.Obj.Id;
                        var s = bag.Add(rf.Face.DuplicateSurface());
                        var sd = SubD.CreateFromSurface(s, m, corners);
                        if (sd == null) { p.Err("SubD from surface failed (try method control_net, or rebuild the surface to degree 3 with few points first)"); return null; }
                        return bag.Add(sd);
                    }
                case "interpolate_points": return InterpolatePoints(p, ctx, bag, r);
                case "subdivide":
                    {
                        int lv = p.Int("levels", false, 1);
                        var sd = DupTarget(p, "target", bag, out src);
                        if (sd == null || p.Errors.Count > 0) return null;
                        if (lv < 1 || lv > 3) { p.Err("'levels' must be 1..3"); return null; }
                        double faces = (double)sd.Faces.Count * Math.Pow(4, lv);
                        if (faces > 200000) { p.Err("result would have ~" + (long)faces + " faces (limit 200000); use fewer levels"); return null; }
                        if (!sd.Subdivide(lv)) { p.Err("subdivide failed"); return null; }
                        return sd;
                    }
                case "offset":
                    {
                        double dist = p.Num("distance", true);
                        bool solid = p.Bool("solid");
                        var sd = Target(p, "target", bag, out src);
                        if (sd == null || p.Errors.Count > 0) return null;
                        if (dist == 0) { p.Err("'distance' must not be 0"); return null; }
                        var o = sd.Offset(dist, solid);
                        if (o == null) { p.Err("SubD offset failed (distance too large for the local curvature?). Try a smaller distance"); return null; }
                        return bag.Add(o);
                    }
                case "crease_edges": return CreaseEdges(p, ctx, bag, r, out src);
                case "to_brep":
                    {
                        var sd = Target(p, "target", bag, out src);
                        if (sd == null) return null;
                        var b = sd.ToBrep();
                        if (b == null) { p.Err("SubD to Brep conversion failed"); return null; }
                        return bag.Add(b);
                    }
                case "join":
                    {
                        var objs = p.Objs("targets");
                        double jt = p.Num("tol", false, Math.Max(tol, 0.01));
                        bool cr = p.Bool("creases", true);
                        if (p.Errors.Count > 0) return null;
                        var list = new List<SubD>();
                        foreach (var o in objs) { var s = o.Geometry as SubD; if (s == null) { p.Err("target " + ctx.Handles.For(o.Id) + " is not a SubD"); return null; } list.Add(s); }
                        if (list.Count < 2) { p.Err("'targets' needs >= 2 SubDs"); return null; }
                        var res = SubD.JoinSubDs(list, jt, cr);
                        if (res == null || res.Length == 0) { p.Err("join failed"); return null; }
                        foreach (var s in res) bag.Add(s);
                        if (res.Length > 1) r["note"] = "join produced " + res.Length + " SubDs; the first was kept (edges did not all meet within tol)";
                        return res[0];
                    }
                case "info":
                    {
                        var sd = Target(p, "target", bag, out src);
                        if (sd == null) return null;
                        foreach (var kv in Info(sd)) r[kv.Key] = kv.Value;
                        var edges = new List<object>();
                        int k = 0, creased = 0;
                        foreach (var e in sd.Edges)
                        {
                            if (e.Tag == SubDEdgeTag.Crease) creased++;
                            if (k < 300)
                                edges.Add(new { i = k, from = GeoUtil.Arr(e.VertexFrom.ControlNetPoint), to = GeoUtil.Arr(e.VertexTo.ControlNetPoint), tag = e.Tag.ToString().ToLowerInvariant(), faces = e.FaceCount });
                            k++;
                        }
                        r["creased_edges"] = creased;
                        r["edge_list"] = edges;
                        if (k > 300) r["edge_list_truncated"] = true;
                        return null;
                    }
            }
            p.Err("unknown op '" + op + "'. Use create_from_mesh | from_loft | from_surface | interpolate_points | subdivide | offset | crease_edges | to_brep | join | info");
            return null;
        }

        static GeometryBase FromMesh(P p, ToolContext ctx, Bag bag)
        {
            Mesh mesh = null;
            string corners = p.Str("corners", false, "smooth");
            if (corners != "smooth" && corners != "sharp") p.Err("'corners' must be smooth | sharp");
            var prim = p.Tok("primitive") as JObject;
            if (p.Has("mesh"))
            {
                var o = FF.OneObject(p, p.Tok("mesh"), "mesh");
                if (o == null) return null;
                var m = o.Geometry as Mesh;
                if (m != null) mesh = bag.Add((Mesh)m.DuplicateMesh());
                else
                {
                    var b = GeoUtil.AsBrep(o.Geometry);
                    if (b == null) { p.Err("'mesh': " + ctx.Handles.For(o.Id) + " is neither a mesh nor a brep"); return null; }
                    var parts = Mesh.CreateFromBrep(b, MeshingParameters.Default);
                    if (parts == null || parts.Length == 0) { p.Err("could not mesh the brep"); return null; }
                    mesh = new Mesh();
                    foreach (var mp in parts) { mesh.Append(mp); mp.Dispose(); }
                    bag.Add(mesh);
                }
            }
            else if (prim != null)
            {
                var q = new P(prim, ctx, p.Frame);
                string type = q.Str("type", true);
                if (type == "box")
                {
                    var c = q.Pt("corner", true);
                    var sz = q.Tok("size");
                    double[] s;
                    if (sz == null || !P.TryNums(sz, 3, 3, out s) || s.Any(x => !(x > 0))) q.Err("'size' must be [dx,dy,dz] > 0");
                    else
                    {
                        int nx = q.Int("nx", false, 1), ny = q.Int("ny", false, 1), nz = q.Int("nz", false, 1);
                        if (nx < 1 || ny < 1 || nz < 1 || nx > 50 || ny > 50 || nz > 50) q.Err("nx/ny/nz must be 1..50");
                        else if (q.Errors.Count == 0)
                        {
                            var pl = p.Frame.Plane; pl.Origin = c;
                            var box = new Box(pl, new Interval(0, s[0]), new Interval(0, s[1]), new Interval(0, s[2]));
                            mesh = bag.Add(Mesh.CreateFromBox(box, nx, ny, nz));
                        }
                    }
                }
                else if (type == "cylinder")
                {
                    var bs = q.Pt("base", true); double rad = q.Num("radius", true), h = q.Num("height", true);
                    int around = q.Int("around", false, 8), vert = q.Int("vertical", false, 1);
                    bool caps = q.Bool("caps", true);
                    string ce = q.Str("cap_edges", false, "crease");
                    if (q.Errors.Count == 0)
                    {
                        if (!(rad > 0) || !(h > 0) || around < 3 || vert < 1) q.Err("radius, height > 0; around >= 3; vertical >= 1");
                        else
                        {
                            var pl = p.Frame.Plane; pl.Origin = bs;
                            var cyl = new Cylinder(new Circle(pl, rad), h);
                            var sd = SubD.CreateFromCylinder(cyl, (uint)around, (uint)vert, caps ? SubDEndCapStyle.Quads : SubDEndCapStyle.None,
                                ce == "smooth" ? SubDEdgeTag.Smooth : SubDEdgeTag.Crease, SubDComponentLocation.ControlNet);
                            if (sd == null) q.Err("SubD cylinder failed");
                            else { foreach (var e in q.Errors) p.Err("primitive: " + e); return bag.Add(sd); }
                        }
                    }
                }
                else if (q.Errors.Count == 0) q.Err("primitive.type must be box | cylinder");
                foreach (var e in q.Errors) p.Err("primitive: " + e);
            }
            else p.Err("give 'mesh' (handle) or 'primitive' ({type:'box'|'cylinder',...})");
            if (p.Errors.Count > 0) return null;
            if (mesh == null) { p.Err("could not create the mesh"); return null; }
            mesh.Normals.ComputeNormals();
            mesh.Compact();
            var opt = corners == "sharp" ? SubDCreationOptions.ConvexCornersAndInteriorCreases : SubDCreationOptions.Smooth;
            SubD res;
            try { res = SubD.CreateFromMesh(mesh, opt); }
            finally { try { opt.Dispose(); } catch (Exception) { } }
            if (res == null) { p.Err("SubD.CreateFromMesh failed: the mesh must be manifold with consistent orientation (quads preferred)"); return null; }
            return bag.Add(res);
        }

        static GeometryBase InterpolatePoints(P p, ToolContext ctx, Bag bag, Dictionary<string, object> r)
        {
            var g = p.Tok("points") as JArray;
            if (g == null || g.Count < 2) { p.Err("'points' must be a grid: rows of [x,y,z] (>= 2 x 2)"); return null; }
            int rows = g.Count, cols = 0;
            var pts = new List<Point3d>();
            for (int i = 0; i < rows; i++)
            {
                var row = g[i] as JArray;
                if (row == null || row.Count < 2 || (cols != 0 && row.Count != cols)) { p.Err("'points' rows must all have the same length (>= 2)"); return null; }
                cols = row.Count;
                foreach (var t in row)
                {
                    double[] v;
                    if (!P.TryNums(t, 2, 3, out v)) { p.Err("'points' contains an item that is not [x,y,z]"); return null; }
                    pts.Add(p.Frame.Pt(new Point3d(v[0], v[1], v.Length > 2 ? v[2] : 0)));
                }
            }
            var mesh = bag.Add(new Mesh());
            foreach (var pt in pts) mesh.Vertices.Add(pt);
            for (int i = 0; i < rows - 1; i++)
                for (int j = 0; j < cols - 1; j++)
                    mesh.Faces.AddFace(i * cols + j, (i + 1) * cols + j, (i + 1) * cols + j + 1, i * cols + j + 1);
            mesh.Normals.ComputeNormals();
            var opt = SubDCreationOptions.Smooth;
            SubD sd;
            try { sd = SubD.CreateFromMesh(mesh, opt); } finally { try { opt.Dispose(); } catch (Exception) { } }
            if (sd == null) { p.Err("could not create a SubD from the point grid"); return null; }
            bag.Add(sd);
            // match SubD vertex order to the grid via control-net position
            var targets = new List<Point3d>();
            for (var v = sd.Vertices.First; v != null; v = v.Next)
            {
                var cp = v.ControlNetPoint; int best = -1; double bd = double.MaxValue;
                for (int k = 0; k < pts.Count; k++) { double d = cp.DistanceTo(pts[k]); if (d < bd) { bd = d; best = k; } }
                targets.Add(pts[best]);
            }
            if (!sd.InterpolateSurfacePoints(targets.ToArray())) { p.Err("InterpolateSurfacePoints failed"); return null; }
            double maxDev = 0;
            for (var v = sd.Vertices.First; v != null; v = v.Next)
            {
                var sp = v.SurfacePoint();
                var cp = v.ControlNetPoint; int best = -1; double bd = double.MaxValue;
                for (int k = 0; k < pts.Count; k++) { double d = sp.DistanceTo(pts[k]); if (d < bd) { bd = d; best = k; } }
                maxDev = Math.Max(maxDev, bd);
            }
            r["max_interpolation_deviation"] = FF.R(maxDev);
            return sd;
        }

        static GeometryBase CreaseEdges(P p, ToolContext ctx, Bag bag, Dictionary<string, object> r, out Guid src)
        {
            var sd = DupTarget(p, "target", bag, out src);
            string tagS = p.Str("tag", false, "crease");
            bool hasEdges = p.Has("edges"), hasAngle = p.Has("all_sharp_by_angle"), hasBox = p.Has("in_box");
            double ang = p.Num("all_sharp_by_angle", false, 30);
            if (sd == null || p.Errors.Count > 0) return null;
            if (tagS != "crease" && tagS != "smooth") { p.Err("'tag' must be crease | smooth"); return null; }
            if (!hasEdges && !hasAngle && !hasBox) { p.Err("give one of: edges:[indices], all_sharp_by_angle:deg, in_box:{min,max}"); return null; }
            var all = new List<SubDEdge>();
            foreach (var e in sd.Edges) all.Add(e);
            var pick = new HashSet<int>();
            if (hasEdges)
            {
                var ea = p.Tok("edges") as JArray;
                if (ea == null) { p.Err("'edges' must be an array of integers"); return null; }
                foreach (var t in ea)
                {
                    if (t.Type != JTokenType.Integer) { p.Err("'edges' must contain integers"); return null; }
                    int k = (int)(long)t;
                    if (k < 0 || k >= all.Count) { p.Err("edge index " + k + " out of range 0.." + (all.Count - 1) + " (see op info)"); return null; }
                    pick.Add(k);
                }
            }
            if (hasAngle)
            {
                double cosT = Math.Cos(ang * Math.PI / 180.0);
                for (int k = 0; k < all.Count; k++)
                {
                    var e = all[k];
                    if (e.FaceCount != 2) continue;
                    var n0 = FaceNormal(e.FaceAt(0)); var n1 = FaceNormal(e.FaceAt(1));
                    if (n0.IsValid && n1.IsValid && n0 * n1 < cosT) pick.Add(k);
                }
            }
            if (hasBox)
            {
                var bo = p.Tok("in_box") as JObject;
                Point3d a = Point3d.Unset, b = Point3d.Unset;
                if (bo == null) { p.Err("'in_box' must be {min:[x,y,z], max:[x,y,z]}"); return null; }
                var bp = new P(bo, ctx, p.Frame);
                var mn = bp.LocalPt("min", true); var mx = bp.LocalPt("max", true);
                if (bp.Errors.Count > 0) { foreach (var e in bp.Errors) p.Err("in_box: " + e); return null; }
                // test in the item frame
                var inv = p.Frame.ToWorld; Transform back;
                if (!inv.TryGetInverse(out back)) back = Transform.Identity;
                var bb = new BoundingBox(Math.Min(mn.X, mx.X), Math.Min(mn.Y, mx.Y), Math.Min(mn.Z, mx.Z), Math.Max(mn.X, mx.X), Math.Max(mn.Y, mx.Y), Math.Max(mn.Z, mx.Z));
                double eps = ctx.Tol;
                bb.Inflate(eps);
                for (int k = 0; k < all.Count; k++)
                {
                    var q0 = all[k].VertexFrom.ControlNetPoint; q0.Transform(back);
                    var q1 = all[k].VertexTo.ControlNetPoint; q1.Transform(back);
                    if (bb.Contains(q0) && bb.Contains(q1)) pick.Add(k);
                }
            }
            if (pick.Count == 0) { p.Err("no edges matched the selection (see op info for edge indices)"); return null; }
            var tag = tagS == "crease" ? SubDEdgeTag.Crease : SubDEdgeTag.Smooth;
            sd.Edges.SetEdgeTags(pick.Select(k => all[k]).ToList(), tag);
            r["tagged"] = pick.Count; r["tag"] = tagS;
            r["tagged_edges"] = pick.OrderBy(x => x).Take(200).ToList();
            return sd;
        }
    }
}

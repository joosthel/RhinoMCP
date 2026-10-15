using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Layers;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Drawing
{
    /// <summary>One line category's layer settings.</summary>
    internal sealed class LayerSpec
    {
        public string Suffix;
        public double Width;
        public string Linetype;
        public int Rgb;
    }

    /// <summary>A source object of the drawing (a top-level Rhino object; blocks are exploded into it).</summary>
    internal sealed class SrcItem
    {
        public RhinoObject Obj;
        public int Index;
        public string Handle;
        public string LayerLeaf;
    }

    /// <summary>A resolved view: direction, up vector and the viewport used for the hidden-line computation.</summary>
    internal sealed class ViewSpec
    {
        public string Name;
        public Vector3d Look, Up;
        public bool Perspective;
        public ViewportInfo Info;     // set for perspective views (used as-is); parallel views are rebuilt around the model
    }

    public sealed class DrawingMake2D : ITool, IManagesOwnUndo
    {
        public string Name { get { return "drawing_make2d"; } }
        public ToolKind Kind { get { return ToolKind.Mutate; } }
        public string Description
        {
            get
            {
                return "Clean, editable 2D drawing from 3D objects (plan/section/elevation): runs Rhino's hidden-line engine, then drops duplicate/clipped/projecting/tiny pieces, " +
                       "reduces everything to lines, arcs and joined polylines, removes overlapping copies, assigns ISO 128 layers (Section/Silhouette/Visible/Hidden/Tangent) with print widths and linetypes, " +
                       "hatches closed section loops, and places the result at scale next to the model. Returns a numeric quality report " +
                       "(segments in/out, drops, curve kinds, overlaps removed, open_ends, closed_section_loops, hatches, bbox, scale_check). " +
                       "view: Top|Bottom|Front|Back|Left|Right (parallel), a named view, 'Perspective' (allowed but flagged), {plane:{origin,x_axis,y_axis}} (looks at the plane from its +normal side) or {direction:[x,y,z] (viewing direction), up?}. " +
                       "clipping_planes: handles of drawing_clipping_plane objects or inline {origin,x_axis,y_axis}; same convention as Rhino's own clipping planes (the side the normal points to stays); the cut appears as Section lines + hatch. " +
                       "scale '1:100' shrinks the output (model units); origin is where the drawing's lower-left corner lands (default: right of the model). compare_native:true also runs Rhino's own Make2D on the same input, counts its curves and removes its output. " +
                       "Mesh sources give jagged outlines (warned). Fails with NO_GEOMETRY when nothing drawable was selected.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"target\":{\"$ref\":\"#/$defs/target\"}," +
                    "\"layers\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"alternative to target: all objects on these layers (and sublayers)\"}," +
                    "\"view\":{\"description\":\"Top|Bottom|Front|Back|Left|Right|Perspective, a named view, {plane:{origin,x_axis,y_axis},name?} or {direction:[x,y,z],up?,name?}. Default Top\"}," +
                    "\"clipping_planes\":{\"type\":\"array\",\"items\":{},\"description\":\"clipping plane handles/GUIDs and/or inline {origin,x_axis,y_axis} planes; the side the normal points to stays (Rhino convention)\"}," +
                    "\"scale\":{\"description\":\"'1:100' (default 1:1), or a number = denominator\"}," +
                    "\"origin\":{\"$ref\":\"#/$defs/point\",\"description\":\"where the lower-left corner of the drawing is placed; default right of the model, same Y\"}," +
                    "\"tolerance\":{\"type\":\"number\",\"description\":\"default: document absolute tolerance\"}," +
                    "\"include_hidden\":{\"type\":\"boolean\",\"default\":false}," +
                    "\"include_tangent_edges\":{\"type\":\"boolean\",\"default\":false}," +
                    "\"include_seams\":{\"type\":\"boolean\",\"default\":false}," +
                    "\"layer_root\":{\"type\":\"string\",\"description\":\"default Drawing::<view-name>\"}," +
                    "\"layer_scheme\":{\"type\":\"object\",\"description\":\"category (section|silhouette|visible|hidden|tangent|hatch) -> {layer?, print_width_mm?, linetype?, color?'#rrggbb'}\"}," +
                    "\"hatch\":{\"description\":\"'none' | pattern name | {pattern,scale,rotation_deg}. Default Solid. Pattern scale is in output (post-scale) units\"}," +
                    "\"group_by\":{\"enum\":[\"source_object\",\"none\"],\"default\":\"source_object\"}," +
                    "\"keep_source_layer_sublayers\":{\"type\":\"boolean\",\"default\":false,\"description\":\"put output on <root>::<category>::<source layer>\"}," +
                    "\"force\":{\"type\":\"boolean\",\"default\":false,\"description\":\"override make2d_max_input_faces (never block_rss_mb)\"},\"compare_native\":{\"type\":\"boolean\",\"default\":false}," +
                    "\"keep_engine_duplicates\":{\"type\":\"boolean\",\"default\":false,\"description\":\"diagnostic: keep the segments Rhino flags Duplicate so the overlap pass has to remove them\"}");
            }
        }

        static readonly string[] CatKeys = { "section", "silhouette", "visible", "tangent", "hidden" };

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            var warnings = new List<string>();

            // ---- arguments
            var objs = ObjectQuery.Resolve(ctx, args["target"], a, "target", false);
            var layerNames = ReadStrings(args["layers"], a, "layers");
            if (layerNames.Count > 0)
            {
                var seen = new HashSet<Guid>(objs.Select(o => o.Id));
                foreach (var ln in layerNames)
                {
                    var f = new ObjectFilter { Layer = ln, IncludeSublayers = true };
                    var found = ObjectQuery.Run(doc, f);
                    if (found.Count == 0) warnings.Add("layers: no objects on '" + ln + "'");
                    foreach (var o in found) if (seen.Add(o.Id)) objs.Add(o);
                }
            }
            if (!a.Has("target") && layerNames.Count == 0) a.Error("give 'target' (handles/set/query) or 'layers'");
            double tol = a.Double("tolerance", false, ctx.Tol);
            if (tol <= 0) { a.Error("'tolerance' must be > 0"); tol = ctx.Tol > 0 ? ctx.Tol : 0.001; }
            bool includeHidden = a.Bool("include_hidden", false);
            bool includeTangent = a.Bool("include_tangent_edges", false);
            bool includeSeams = a.Bool("include_seams", false);
            bool keepSub = a.Bool("keep_source_layer_sublayers", false);
            bool compareNative = a.Bool("compare_native", false);
            bool keepDup = a.Bool("keep_engine_duplicates", false);
            string groupBy = (a.String("group_by", false, "source_object") ?? "").ToLowerInvariant();
            if (groupBy != "source_object" && groupBy != "none") a.Error("'group_by' must be source_object or none");
            double denom = ParseScale(args["scale"], a);
            Point3d originArg = a.Has("origin") ? a.Point3d("origin") : Point3d.Unset;
            var view = ResolveView(args["view"], ctx, a);
            string layerRoot = a.String("layer_root", false, null);
            if (layerRoot != null)
            {
                string lerr = LayerUtil.ValidatePath(layerRoot);
                if (lerr != null) a.Error("'layer_root': " + lerr);
            }
            var scheme = ParseScheme(args["layer_scheme"], a);
            HatchSpec hatch = ParseHatch(args["hatch"], a);
            var clipPlanes = ResolveClipping(args["clipping_planes"], ctx, a);
            if (a.HasErrors) return a.ErrorResult();
            if (view == null) return ToolResult.Fail("INVALID_ARGS", "view could not be resolved");
            var rssBlock = Resources.CheckRss("drawing_make2d");
            if (rssBlock != null) return rssBlock;
            var resGuard = Resources.Begin("drawing_make2d");
            bool forceRes = a.Bool("force", false);
            if (objs.Count == 0) return ToolResult.Fail("NOT_FOUND", "The target resolved to no objects", "Check the target with objects_query.");

            double angTol = Math.Max(ctx.AngleTolDeg * Math.PI / 180.0, 1e-4);
            if (angTol > 0.01) angTol = 0.01;
            double scale = 1.0 / denom;
            string viewName = view.Name;
            if (layerRoot == null) layerRoot = "Drawing::" + SafeName(viewName);

            var times = new Dictionary<string, double>();
            var sw = Stopwatch.StartNew();
            var temps = new List<IDisposable>();
            HiddenLineDrawing hld = null;
            try
            {
                // ---- sources
                var items = new List<SrcItem>();
                var skipped = new Dictionary<string, int>();
                int meshSources = 0;
                var added = new List<AddedGeo>();
                for (int i = 0; i < objs.Count; i++)
                {
                    var it = new SrcItem
                    {
                        Obj = objs[i], Index = items.Count, Handle = ctx.Handles.For(objs[i].Id),
                        LayerLeaf = SafeName(doc.Layers[objs[i].Attributes.LayerIndex].Name)
                    };
                    int before = added.Count;
                    Collect(objs[i], Transform.Identity, 0, it.Index, added, skipped, ref meshSources, temps);
                    if (added.Count > before) items.Add(it);
                }
                if (added.Count == 0)
                    return ToolResult.Fail("NO_GEOMETRY", "None of the " + objs.Count + " target objects can be drawn (" + DescribeSkipped(skipped) + ")",
                        "Select breps, extrusions, surfaces, meshes, SubDs, curves or blocks of those.");
                long inFaces = 0;
                foreach (var kv in added)
                {
                    var gb = kv.G; var bg = gb as Brep; var mg = gb as Mesh; var sg = gb as SubD;
                    inFaces += bg != null ? bg.Faces.Count : mg != null ? mg.Faces.Count : sg != null ? sg.Faces.Count : gb is Extrusion ? 6 : 1;
                }
                if (inFaces > Resources.Limits.Make2dMaxInputFaces && !forceRes)
                    return Resources.LimitFail("make2d_max_input_faces", inFaces, Resources.Limits.Make2dMaxInputFaces,
                        "Make2D input has " + inFaces + " faces (" + added.Count + " geometries); the limit is " + Resources.Limits.Make2dMaxInputFaces + ". Nothing was computed.",
                        "Split the drawing: run Make2D per layer or per set (layers:[...] or a {set} target) and combine the outputs, drop objects that are not visible in this view, or use a clipping plane to cut the model first.", true);
                if (skipped.Count > 0) warnings.Add("skipped non-drawable objects: " + DescribeSkipped(skipped));
                if (meshSources > 0) warnings.Add(meshSources + " mesh source(s): outlines follow the mesh facets and will look jagged. Prefer brep/SubD sources.");
                if (view.Perspective) warnings.Add("perspective view: the drawing is not dimensionally true and scale does not apply to real distances. Use a parallel view for plans, sections and elevations.");

                var sourceBox = BoundingBox.Empty;
                foreach (var kv in added) sourceBox.Union(kv.G.GetBoundingBox(kv.X));

                // ---- hidden-line drawing
                var vpInfo = BuildViewport(view, sourceBox, warnings);
                if (vpInfo == null) return ToolResult.Fail("VIEW_FAILED", "Could not build a valid viewport for view '" + viewName + "'");
                var hp = new HiddenLineDrawingParameters();
                hp.AbsoluteTolerance = tol;
                hp.IncludeHiddenCurves = includeHidden;
                hp.IncludeTangentEdges = includeTangent;
                hp.IncludeTangentSeams = includeSeams;
                hp.Flatten = true;
                hp.SetViewport(vpInfo);
                foreach (var kv in added) hp.AddGeometry(kv.G, kv.X, (object)kv.Src);
                foreach (var cp in clipPlanes) { var flipped = cp; flipped.Flip(); hp.AddClippingPlane(flipped); }   // HiddenLineDrawing keeps the opposite side to Rhino's clipping-plane objects

                hld = HiddenLineDrawing.Compute(hp, false);
                if (hld == null) return ToolResult.Fail("MAKE2D_FAILED", "Rhino's hidden line engine returned no result", "Try a looser tolerance or fewer / simpler objects.");
                times["hidden_line_ms"] = sw.Elapsed.TotalMilliseconds; sw.Restart();

                // ---- filter + classify + decompose
                int segIn = 0, dDup = 0, dClip = 0, dProj = 0, keptSecDup = 0;
                var st = new DecomposeStats();
                var pieces = new List<Piece>();
                var catCount = new Dictionary<Cat, int>();
                foreach (var seg in hld.Segments)
                {
                    segIn++;
                    var vis = seg.SegmentVisibility;
                    if (vis == HiddenLineDrawingSegment.Visibility.Duplicate)
                    {
                        // A section line shared by two solids (wall resting on a slab) is flagged Duplicate on one of them.
                        // Both solids need their own closed loop (for closed_section_loops and the hatch), so it is kept.
                        if (seg.ParentCurve != null && seg.ParentCurve.SilhouetteType == SilhouetteType.SectionCut) keptSecDup++;
                        else { dDup++; if (!keepDup) continue; }
                    }
                    if (vis == HiddenLineDrawingSegment.Visibility.Clipped) { dClip++; continue; }
                    if (vis == HiddenLineDrawingSegment.Visibility.Projecting) { dProj++; continue; }
                    var parent = seg.ParentCurve;
                    if (parent == null || seg.CurveGeometry == null) continue;
                    Cat cat;
                    if (!Classify(seg, parent, includeHidden, out cat)) continue;
                    int src = 0;
                    var so = parent.SourceObject;
                    if (so != null && so.Tag is int) src = (int)so.Tag;
                    DrawingGeometry.Decompose(seg.CurveGeometry, tol, angTol, src, cat, pieces, st);
                }
                int piecesIn = pieces.Count;
                times["classify_ms"] = sw.Elapsed.TotalMilliseconds; sw.Restart();

                // ---- weld + de-overlap
                int welded = DrawingGeometry.Weld(pieces, tol);
                int overlapsRemoved;
                pieces = DrawingGeometry.Deoverlap(pieces, tol, out overlapsRemoved);
                int segmentsOut = pieces.Count;
                times["cleanup_ms"] = sw.Elapsed.TotalMilliseconds; sw.Restart();

                // ---- join per (source, category)
                var outCurves = new List<OutCurve>();
                var groups = pieces.GroupBy(p => new Tuple<int, int>((int)p.Cat, (groupBy == "source_object" || p.Cat == Cat.Section) ? p.Src : -1));
                foreach (var g in groups)
                {
                    var list = g.ToList();
                    foreach (var c in DrawingGeometry.JoinAndTidy(list, tol, angTol))
                        outCurves.Add(new OutCurve { Curve = c, Cat = list[0].Cat, Src = list[0].Src, Kind = DrawingGeometry.KindOf(c) });
                }
                // output order: deterministic (category, source)
                outCurves = outCurves.OrderBy(o => (int)o.Cat).ThenBy(o => o.Src).ToList();
                times["join_ms"] = sw.Elapsed.TotalMilliseconds; sw.Restart();

                if (outCurves.Count == 0)
                    return ToolResult.Fail("EMPTY_DRAWING", "The hidden-line engine produced no drawable curves (" + segIn + " segments, " + dDup + " duplicate, " + dClip + " clipped, " + dProj + " projecting)",
                        "If you used clipping planes, check which side is kept (the normal side stays) and that the plane cuts the target.");

                // ---- quality numbers (before placement; invariant under translate/uniform scale except tolerances)
                var kinds = new Dictionary<string, int> { { "line", 0 }, { "arc", 0 }, { "polyline", 0 }, { "polycurve", 0 }, { "nurbs", 0 } };
                int joinedCurves = 0;
                foreach (var o in outCurves)
                {
                    kinds[o.Kind]++;
                    if (o.Kind == "polycurve" || o.Kind == "nurbs" || (o.Kind == "polyline" && ((PolylineCurve)o.Curve).PointCount > 2)) joinedCurves++;
                }
                int openEnds = DrawingGeometry.OpenEnds(outCurves.Select(o => o.Curve).ToList(), tol);
                var bySrcSection = outCurves.Where(o => o.Cat == Cat.Section).ToList();
                int loops = bySrcSection.Count(o => o.Curve.IsClosed);
                int openSection = bySrcSection.Count - loops;
                var gaps = DrawingGeometry.NearestEndGaps(bySrcSection.Where(o => !o.Curve.IsClosed).Select(o => o.Curve).ToList(), tol);
                foreach (var o in outCurves) { int n; catCount.TryGetValue(o.Cat, out n); catCount[o.Cat] = n + 1; }
                if (openSection > 0)
                    warnings.Add(openSection + " section curve(s) are not closed" + (gaps.Count > 0 ? " (nearest end gaps: " + string.Join(", ", gaps.Take(4).Select(g => Math.Round(g, 4).ToString(CultureInfo.InvariantCulture))) + ")" : "") +
                                 ", so they get no hatch. Likely causes: the section plane lies exactly on a surface seam or edge (shift it by a small offset or a few degrees), the source is not a closed solid, or the tolerance is tighter than the section accuracy.");

                // ---- scale + placement
                var dbb = BoundingBox.Empty;
                foreach (var o in outCurves) dbb.Union(o.Curve.GetBoundingBox(true));
                double dw = dbb.Max.X - dbb.Min.X, dh = dbb.Max.Y - dbb.Min.Y;
                var hlBox = hld.BoundingBox(includeHidden);
                double hw = hlBox.IsValid ? hlBox.Max.X - hlBox.Min.X : 0, hh = hlBox.IsValid ? hlBox.Max.Y - hlBox.Min.Y : 0;
                bool clipped = clipPlanes.Count > 0;
                // model extent as seen in the view is only exact for axis-aligned, unclipped views
                var projModel = BoundingBox.Empty;
                if (IsAxisAligned(view.Look) && !view.Perspective && !clipped)
                    foreach (var kv in added) projModel.Union(kv.G.GetBoundingBox(Compose(hld.WorldToHiddenLine, kv.X)));
                Point3d origin;
                if (originArg.IsValid) origin = originArg;
                else origin = DefaultOrigin(doc, objs, sourceBox, layerRoot, dw * scale, ctx.Tol);
                var xf = Transform.Translation(origin - new Point3d(dbb.Min.X * scale, dbb.Min.Y * scale, 0)) * Transform.Scale(Point3d.Origin, scale);
                foreach (var o in outCurves) o.Curve.Transform(xf);
                var final = BoundingBox.Empty;
                foreach (var o in outCurves) final.Union(o.Curve.GetBoundingBox(true));
                double fw = final.Max.X - final.Min.X, fh = final.Max.Y - final.Min.Y;
                // ratio: placed drawing vs the hidden-line engine's own extent x scale (checks our scaling arithmetic);
                // model_ratio: placed drawing vs the model's projected extent x scale (axis-aligned, unclipped views only)
                double rw = hw > tol ? fw / (hw * scale) : double.NaN, rh = hh > tol ? fh / (hh * scale) : double.NaN;
                double mw = projModel.IsValid ? projModel.Max.X - projModel.Min.X : double.NaN, mh = projModel.IsValid ? projModel.Max.Y - projModel.Min.Y : double.NaN;
                double mrw = mw > tol ? fw / (mw * scale) : double.NaN, mrh = mh > tol ? fh / (mh * scale) : double.NaN;
                // the cleaned drawing can only be smaller than the engine's extent (e.g. excluded hidden lines)
                bool scaleOk = (double.IsNaN(rw) || rw <= 1.002) && (double.IsNaN(rh) || rh <= 1.002)
                               && (double.IsNaN(mrw) || Math.Abs(mrw - 1) < 0.002) && (double.IsNaN(mrh) || Math.Abs(mrh - 1) < 0.002);
                times["place_ms"] = sw.Elapsed.TotalMilliseconds; sw.Restart();

                // ---- native comparison (before we touch the document with our own output)
                object nativeReport = null;
                if (compareNative)
                {
                    var nv = NativeCompare.Run(ctx, objs, view, vpInfo, sourceBox, clipPlanes, includeHidden, includeTangent, tol, angTol, warnings);
                    nativeReport = nv;
                    times["native_ms"] = sw.Elapsed.TotalMilliseconds; sw.Restart();
                }

                // ---- write to the document
                var created = new List<string>();
                var outputIds = new List<Guid>();
                var perLayer = new Dictionary<string, int>();
                int hatchesCreated = 0;
                int nGroups = 0;
                UndoScope.CloseStray(doc);
                using (new UndoScope(doc, Name))
                {
                    var layerIdx = new Dictionary<string, int>();   // layer path -> index
                    Func<string, LayerSpec, int> ensureLayer = (path, sp) =>
                    {
                        int idx;
                        if (layerIdx.TryGetValue(path, out idx)) return idx;
                        string lerr;
                        var l = LayerUtil.Ensure(doc, path, created, out lerr);
                        if (l == null) throw new InvalidOperationException(lerr);
                        var props = new LayerProps { Color = sp.Rgb, Linetype = sp.Linetype, Width = sp.Width };
                        var errs = new List<string>();
                        props.Apply(doc, l, warnings, errs);
                        foreach (var e in errs) warnings.Add(e);
                        layerIdx[path] = l.Index;
                        return l.Index;
                    };
                    Func<Cat?, SrcItem, int> layerFor = (cat, srcItem) =>
                    {
                        string key = cat.HasValue ? CatKeys[(int)cat.Value] : "hatch";
                        var sp = scheme[key];
                        string path = layerRoot + LayerUtil.Sep + sp.Suffix;
                        int baseIdx = ensureLayer(path, sp);
                        if (!keepSub || srcItem == null) return baseIdx;
                        return ensureLayer(path + LayerUtil.Sep + srcItem.LayerLeaf, sp);
                    };

                    var perSrcIds = new Dictionary<int, List<Guid>>();
                    Action<int, Guid> track = (src, id) =>
                    {
                        List<Guid> l; if (!perSrcIds.TryGetValue(src, out l)) { l = new List<Guid>(); perSrcIds[src] = l; }
                        l.Add(id);
                        outputIds.Add(id);
                    };

                    foreach (var o in outCurves)
                    {
                        var si = o.Src >= 0 && o.Src < items.Count ? items[o.Src] : null;
                        var at = new ObjectAttributes { LayerIndex = layerFor(o.Cat, si) };
                        at.SetUserString("make2d.category", CatKeys[(int)o.Cat]);
                        if (si != null) at.SetUserString("make2d.source", si.Handle);
                        Guid id = doc.Objects.AddCurve(o.Curve, at);
                        if (id == Guid.Empty) { warnings.Add("Rhino refused to add a " + o.Kind + " curve"); continue; }
                        track(o.Src, id);
                        string lp = doc.Layers[at.LayerIndex].FullPath;
                        int cnt; perLayer.TryGetValue(lp, out cnt); perLayer[lp] = cnt + 1;
                    }

                    // hatches: closed section loops per source object
                    if (hatch != null && loops > 0)
                    {
                        int pat = FindPattern(doc, hatch.Pattern);
                        if (pat < 0)
                        {
                            warnings.Add("hatch pattern '" + hatch.Pattern + "' not found in the document; using Solid. Available: " + string.Join(", ", Enumerable.Range(0, doc.HatchPatterns.Count).Where(i => !doc.HatchPatterns[i].IsDeleted).Select(i => doc.HatchPatterns[i].Name)));
                            pat = FindPattern(doc, "Solid");
                            if (pat < 0) pat = 0;
                        }
                        foreach (var g in outCurves.Where(o => o.Cat == Cat.Section && o.Curve.IsClosed).GroupBy(o => o.Src))
                        {
                            var loopsOfSrc = g.Select(o => o.Curve).ToList();
                            Hatch[] hs = null;
                            try { hs = Hatch.Create(loopsOfSrc, pat, hatch.RotationRad, hatch.Scale, tol); } catch (Exception ex) { warnings.Add("Hatch.Create failed: " + ex.Message); }
                            if (hs == null) continue;
                            var si = g.Key >= 0 && g.Key < items.Count ? items[g.Key] : null;
                            foreach (var h in hs)
                            {
                                var at = new ObjectAttributes { LayerIndex = layerFor(null, si) };
                                if (si != null) at.SetUserString("make2d.source", si.Handle);
                                at.SetUserString("make2d.category", "hatch");
                                Guid id = doc.Objects.AddHatch(h, at);
                                if (id == Guid.Empty) continue;
                                hatchesCreated++;
                                track(g.Key, id);
                                string lp = doc.Layers[at.LayerIndex].FullPath;
                                int cnt; perLayer.TryGetValue(lp, out cnt); perLayer[lp] = cnt + 1;
                            }
                        }
                    }
                    else if (hatch != null && loops == 0 && clipped) warnings.Add("clipping planes given but no closed section loop was produced: nothing to hatch (is the target a closed solid and does the plane cut it?)");

                    if (groupBy == "source_object")
                    {
                        foreach (var kv in perSrcIds)
                        {
                            if (kv.Value.Count < 2 || kv.Key < 0 || kv.Key >= items.Count) continue;
                            string gname = "Make2D_" + SafeName(viewName) + "_" + items[kv.Key].Handle;
                            string unique = gname; int n = 1;
                            while (doc.Groups.Find(unique) >= 0) unique = gname + "_" + (++n);
                            if (doc.Groups.Add(unique, kv.Value) >= 0) nGroups++;
                        }
                    }
                }
                times["write_ms"] = sw.Elapsed.TotalMilliseconds; sw.Restart();

                string setName = "make2d_" + SafeName(viewName);
                try { if (ObjectQuery.ValidateSetName(setName) == null) ObjectQuery.StoreSet(doc, setName, outputIds); else setName = null; } catch (Exception) { setName = null; }

                var report = new Dictionary<string, object>
                {
                    { "segments_in", segIn },
                    { "segments_out", segmentsOut },
                    { "dropped_duplicate", dDup },
                    { "kept_section_duplicates", keptSecDup },
                    { "dropped_clipped", dClip },
                    { "dropped_projecting", dProj },
                    { "dropped_tiny", st.Tiny },
                    { "pieces_before_cleanup", piecesIn },
                    { "welded_endpoints", welded },
                    { "overlaps_removed", overlapsRemoved },
                    { "curves_out", new { total = outCurves.Count, line = kinds["line"], arc = kinds["arc"], polyline = kinds["polyline"], polycurve = kinds["polycurve"], nurbs = kinds["nurbs"] } },
                    { "by_category", catCount.OrderBy(k => (int)k.Key).ToDictionary(k => CatKeys[(int)k.Key], k => k.Value) },
                    { "joined_curves", joinedCurves },
                    { "open_ends", openEnds },
                    { "closed_section_loops", loops },
                    { "open_section_curves", openSection },
                    { "open_section_end_gaps", gaps.Select(g => Math.Round(g, 6)).Take(12).ToList() },
                    { "hatches_created", hatchesCreated },
                    { "bbox", new { min = GeoUtil.Arr(final.Min), max = GeoUtil.Arr(final.Max), size = new[] { GeoUtil.R(fw), GeoUtil.R(fh) } } },
                    { "scale_check", new { scale = denom == 1 ? "1:1" : "1:" + denom.ToString("0.###", CultureInfo.InvariantCulture), factor = scale,
                                           hidden_line_extent = new[] { GeoUtil.R(hw), GeoUtil.R(hh) }, model_extent = double.IsNaN(mw) ? null : new[] { GeoUtil.R(mw), GeoUtil.R(mh) },
                                           drawing_extent = new[] { GeoUtil.R(fw), GeoUtil.R(fh) }, ratio = new[] { Rnd(rw), Rnd(rh) },
                                           model_ratio = double.IsNaN(mrw) ? null : new[] { Rnd(mrw), Rnd(mrh) }, clipped = clipped, ok = scaleOk } },
                    { "mesh_sources", meshSources },
                    { "sources", items.Count },
                    { "timings_ms", times.ToDictionary(k => k.Key, k => Math.Round(k.Value, 1)) }
                };
                var data = new Dictionary<string, object>
                {
                    { "view", new { name = viewName, perspective = view.Perspective, direction = GeoUtil.Arr(view.Look), up = GeoUtil.Arr(view.Up) } },
                    { "layer_root", layerRoot },
                    { "layers", perLayer },
                    { "layers_created", created.Count },
                    { "groups_created", nGroups },
                    { "output_objects", outputIds.Count },
                    { "output_set", setName },
                    { "origin", GeoUtil.Arr(origin) },
                    { "report", report }
                };
                if (nativeReport != null) data["native"] = nativeReport;
                var res = ToolResult.Ok(data);
                foreach (var w in warnings) res.Warn(w);
                return resGuard.Finish(res);
            }
            finally
            {
                if (hld != null) hld.Dispose();
                foreach (var d in temps) { try { d.Dispose(); } catch (Exception) { } }
            }
        }

        static int FindPattern(RhinoDoc doc, string name)
        {
            for (int i = 0; i < doc.HatchPatterns.Count; i++)
            {
                var p = doc.HatchPatterns[i];
                if (!p.IsDeleted && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        static bool IsAxisAligned(Vector3d v)
        {
            var u = v;
            if (!u.Unitize()) return false;
            return Math.Abs(Math.Abs(u.X) - 1) < 1e-6 || Math.Abs(Math.Abs(u.Y) - 1) < 1e-6 || Math.Abs(Math.Abs(u.Z) - 1) < 1e-6;
        }

        static double Rnd(double v) { return double.IsNaN(v) ? v : Math.Round(v, 5); }

        static Transform Compose(Transform world2hl, Transform inst) { return world2hl * inst; }

        sealed class AddedGeo
        {
            public GeometryBase G; public Transform X; public int Src;
            public AddedGeo(GeometryBase g, Transform x, int src) { G = g; X = x; Src = src; }
        }

        sealed class OutCurve { public Curve Curve; public Cat Cat; public int Src; public string Kind; }

        // ------------------------------------------------------------------ sources

        void Collect(RhinoObject o, Transform x, int depth, int srcIdx, List<AddedGeo> added,
            Dictionary<string, int> skipped, ref int meshSources, List<IDisposable> temps)
        {
            var g = o.Geometry;
            if (g is Brep || g is Extrusion || g is Surface || g is Curve)
            {
                if (g is Curve && !((Curve)g).IsValid) { Skip(skipped, "invalid curve"); return; }
                added.Add(new AddedGeo(g, x, srcIdx));
                return;
            }
            if (g is Mesh) { meshSources++; added.Add(new AddedGeo(g, x, srcIdx)); return; }
            var sd = g as SubD;
            if (sd != null)
            {
                var b = sd.ToBrep();
                if (b != null) { temps.Add(b); added.Add(new AddedGeo(b, x, srcIdx)); }
                else Skip(skipped, "subd (conversion failed)");
                return;
            }
            var inst = o as InstanceObject;
            if (inst != null)
            {
                if (depth > 8) { Skip(skipped, "block nesting too deep"); return; }
                var def = inst.InstanceDefinition;
                if (def == null) { Skip(skipped, "block without definition"); return; }
                var xf = x * inst.InstanceXform;
                foreach (var m in def.GetObjects()) Collect(m, xf, depth + 1, srcIdx, added, skipped, ref meshSources, temps);
                return;
            }
            Skip(skipped, ObjectQuery.TypeName(o));
        }

        static void Skip(Dictionary<string, int> d, string k) { int n; d.TryGetValue(k, out n); d[k] = n + 1; }
        static string DescribeSkipped(Dictionary<string, int> d) { return string.Join(", ", d.Select(k => k.Value + " " + k.Key)); }

        // ------------------------------------------------------------------ classification

        static bool Classify(HiddenLineDrawingSegment seg, HiddenLineDrawingObjectCurve parent, bool includeHidden, out Cat cat)
        {
            cat = Cat.Visible;
            var vis = seg.SegmentVisibility;
            var type = parent.SilhouetteType;
            if (vis == HiddenLineDrawingSegment.Visibility.Hidden)
            {
                if (!includeHidden) return false;
                cat = Cat.Hidden; return true;
            }
            if (type == SilhouetteType.SectionCut) { cat = Cat.Section; return true; }
            if (type == SilhouetteType.NonSilhouetteTangent || type == SilhouetteType.NonSilhouetteSeam) { cat = Cat.Tangent; return true; }
            cat = seg.IsSceneSilhouette ? Cat.Silhouette : Cat.Visible;
            return true;
        }

        // ------------------------------------------------------------------ argument parsing

        static List<string> ReadStrings(JToken t, ArgReader a, string name)
        {
            var res = new List<string>();
            if (t == null || t.Type == JTokenType.Null) return res;
            var arr = t as JArray;
            if (arr == null) { a.Error("'" + name + "' must be an array of strings"); return res; }
            foreach (var x in arr)
            {
                if (x.Type != JTokenType.String) { a.Error("'" + name + "' must contain only strings"); break; }
                res.Add((string)x);
            }
            return res;
        }

        static double ParseScale(JToken t, ArgReader a)
        {
            if (t == null || t.Type == JTokenType.Null) return 1.0;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
            {
                double d = (double)t;
                if (d <= 0) { a.Error("'scale' must be > 0"); return 1.0; }
                return d;
            }
            if (t.Type == JTokenType.String)
            {
                string s = ((string)t).Trim();
                var parts = s.Split(':');
                double n, m;
                if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out n) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out m) && n > 0 && m > 0)
                    return m / n; // "1:100" -> denominator 100; "2:1" -> 0.5 (enlarge)
                a.Error("'scale' must look like \"1:100\" (drawing:model), or be a number denominator");
                return 1.0;
            }
            a.Error("'scale' must be a string like \"1:100\" or a number");
            return 1.0;
        }

        static ViewSpec ResolveView(JToken t, ToolContext ctx, ArgReader a)
        {
            var doc = ctx.Doc;
            if (t == null || t.Type == JTokenType.Null) t = "Top";
            if (t.Type == JTokenType.String)
            {
                string s = ((string)t).Trim();
                switch (s.ToLowerInvariant())
                {
                    case "top": return Std("Top", new Vector3d(0, 0, -1), new Vector3d(0, 1, 0));
                    case "bottom": return Std("Bottom", new Vector3d(0, 0, 1), new Vector3d(0, -1, 0));
                    case "front": return Std("Front", new Vector3d(0, 1, 0), new Vector3d(0, 0, 1));
                    case "back": return Std("Back", new Vector3d(0, -1, 0), new Vector3d(0, 0, 1));
                    case "left": return Std("Left", new Vector3d(1, 0, 0), new Vector3d(0, 0, 1));
                    case "right": return Std("Right", new Vector3d(-1, 0, 0), new Vector3d(0, 0, 1));
                    case "perspective":
                        {
                            var rv = doc.Views.Find("Perspective", false);
                            if (rv == null) rv = doc.Views.GetViewList(true, false).FirstOrDefault(v => !v.ActiveViewport.IsParallelProjection);
                            if (rv == null) { a.Error("no Perspective viewport found in the document"); return null; }
                            var info = new ViewportInfo(rv.ActiveViewport);
                            return new ViewSpec { Name = "Perspective", Look = info.CameraDirection, Up = info.CameraUp, Perspective = true, Info = info };
                        }
                }
                int i = doc.NamedViews.FindByName(s);
                if (i < 0)
                {
                    var names = new List<string>();
                    for (int k = 0; k < doc.NamedViews.Count; k++) names.Add(doc.NamedViews[k].Name);
                    a.Error("unknown view '" + s + "'. Use Top|Bottom|Front|Back|Left|Right|Perspective, a named view (" + (names.Count == 0 ? "none saved" : string.Join(", ", names)) + "), {plane}, or {direction}");
                    return null;
                }
                var nvi = new ViewportInfo(doc.NamedViews[i].Viewport);
                var vs = new ViewSpec { Name = s, Look = nvi.CameraDirection, Up = nvi.CameraUp, Perspective = nvi.IsPerspectiveProjection };
                if (vs.Perspective) vs.Info = nvi;
                return vs;
            }
            var o = t as JObject;
            if (o == null) { a.Error("'view' must be a string or an object"); return null; }
            string name = o["name"] != null && o["name"].Type == JTokenType.String ? (string)o["name"] : null;
            JToken planeTok = o["plane"] ?? (o["origin"] != null ? (JToken)o : null);
            if (planeTok != null)
            {
                string perr;
                var fr = GeoUtil.ParsePlane(planeTok, ctx, null, out perr);
                if (fr == null) { a.Error("'view.plane': " + perr); return null; }
                return new ViewSpec { Name = name ?? "Plane", Look = -fr.Plane.ZAxis, Up = fr.Plane.YAxis };
            }
            if (o["direction"] != null)
            {
                double[] d, u = null;
                if (!P.TryNums(o["direction"], 3, 3, out d)) { a.Error("'view.direction' must be [x,y,z]"); return null; }
                var look = new Vector3d(d[0], d[1], d[2]);
                if (!look.Unitize()) { a.Error("'view.direction' must not be zero"); return null; }
                Vector3d up;
                if (o["up"] != null)
                {
                    if (!P.TryNums(o["up"], 3, 3, out u)) { a.Error("'view.up' must be [x,y,z]"); return null; }
                    up = new Vector3d(u[0], u[1], u[2]);
                }
                else up = Vector3d.ZAxis;
                if (Math.Abs(Vector3d.Multiply(look, up) / Math.Max(up.Length, 1e-12)) > 0.9999) up = Vector3d.YAxis;
                return new ViewSpec { Name = name ?? "Dir", Look = look, Up = up };
            }
            a.Error("'view' object needs 'plane' {origin,x_axis,y_axis} or 'direction' [x,y,z]");
            return null;
        }

        static ViewSpec Std(string n, Vector3d look, Vector3d up) { return new ViewSpec { Name = n, Look = look, Up = up }; }

        static Dictionary<string, LayerSpec> ParseScheme(JToken t, ArgReader a)
        {
            var s = new Dictionary<string, LayerSpec>
            {
                { "section",    new LayerSpec { Suffix = "Section",    Width = 0.50, Linetype = "Continuous", Rgb = 0x000000 } },
                { "silhouette", new LayerSpec { Suffix = "Silhouette", Width = 0.35, Linetype = "Continuous", Rgb = 0x000000 } },
                { "visible",    new LayerSpec { Suffix = "Visible",    Width = 0.35, Linetype = "Continuous", Rgb = 0x000000 } },
                { "hidden",     new LayerSpec { Suffix = "Hidden",     Width = 0.18, Linetype = "Hidden",     Rgb = 0x000000 } },
                { "tangent",    new LayerSpec { Suffix = "Tangent",    Width = 0.13, Linetype = "Continuous", Rgb = 0x000000 } },
                { "hatch",      new LayerSpec { Suffix = "Hatch",      Width = 0.13, Linetype = "Continuous", Rgb = 0x000000 } }
            };
            var o = t as JObject;
            if (t != null && t.Type != JTokenType.Null && o == null) { a.Error("'layer_scheme' must be an object"); return s; }
            if (o == null) return s;
            foreach (var p in o.Properties())
            {
                LayerSpec ls;
                if (!s.TryGetValue(p.Name.ToLowerInvariant(), out ls)) { a.Error("layer_scheme: unknown category '" + p.Name + "'. Valid: " + string.Join(", ", s.Keys)); continue; }
                var v = p.Value as JObject;
                if (v == null) { a.Error("layer_scheme." + p.Name + " must be an object {layer?, print_width_mm?, linetype?, color?}"); continue; }
                JToken x;
                if (v.TryGetValue("layer", out x) && x.Type == JTokenType.String && ((string)x).Trim().Length > 0) ls.Suffix = ((string)x).Trim();
                if (v.TryGetValue("print_width_mm", out x) && (x.Type == JTokenType.Float || x.Type == JTokenType.Integer) && (double)x >= 0 && (double)x <= 20) ls.Width = (double)x;
                else if (v.TryGetValue("print_width_mm", out x) && x.Type != JTokenType.Null) a.Error("layer_scheme." + p.Name + ".print_width_mm must be in [0..20]");
                if (v.TryGetValue("linetype", out x) && x.Type == JTokenType.String) ls.Linetype = (string)x;
                if (v.TryGetValue("color", out x) && x.Type == JTokenType.String)
                {
                    int rgb; if (LayerUtil.TryHex((string)x, out rgb)) ls.Rgb = rgb; else a.Error("layer_scheme." + p.Name + ".color must be \"#rrggbb\"");
                }
                string lerr = LayerUtil.ValidatePath(ls.Suffix);
                if (lerr != null) a.Error("layer_scheme." + p.Name + ".layer: " + lerr);
            }
            return s;
        }

        internal sealed class HatchSpec { public string Pattern = "Solid"; public double Scale = 1.0; public double RotationRad; }

        static HatchSpec ParseHatch(JToken t, ArgReader a)
        {
            if (t == null || t.Type == JTokenType.Null) return new HatchSpec();
            if (t.Type == JTokenType.Boolean) return (bool)t ? new HatchSpec() : null;
            if (t.Type == JTokenType.String)
            {
                string s = ((string)t).Trim();
                if (s.Length == 0 || s.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
                return new HatchSpec { Pattern = s };
            }
            var o = t as JObject;
            if (o == null) { a.Error("'hatch' must be 'none', a pattern name or {pattern, scale, rotation_deg}"); return null; }
            var h = new HatchSpec();
            JToken x;
            if (o.TryGetValue("pattern", out x) && x.Type == JTokenType.String)
            {
                if (((string)x).Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
                h.Pattern = (string)x;
            }
            if (o.TryGetValue("scale", out x) && (x.Type == JTokenType.Float || x.Type == JTokenType.Integer)) { h.Scale = (double)x; if (h.Scale <= 0) a.Error("hatch.scale must be > 0"); }
            if (o.TryGetValue("rotation_deg", out x) && (x.Type == JTokenType.Float || x.Type == JTokenType.Integer)) h.RotationRad = (double)x * Math.PI / 180.0;
            return h;
        }

        static List<Plane> ResolveClipping(JToken t, ToolContext ctx, ArgReader a)
        {
            var res = new List<Plane>();
            if (t == null || t.Type == JTokenType.Null) return res;
            var arr = t as JArray;
            if (arr == null) arr = new JArray(t);
            int i = 0;
            foreach (var it in arr)
            {
                string name = "clipping_planes[" + i++ + "]";
                if (it.Type == JTokenType.String)
                {
                    string s = (string)it;
                    Guid id;
                    if (!HandleMap.Instance.TryResolve(s, out id) && !Guid.TryParse(s, out id)) { a.Error(name + ": '" + s + "' is not a known handle or GUID"); continue; }
                    var obj = ctx.Doc.Objects.FindId(id) as ClippingPlaneObject;
                    if (obj == null) { a.Error(name + ": " + s + " is not a clipping plane object. Create one with drawing_clipping_plane."); continue; }
                    res.Add(obj.ClippingPlaneGeometry.Plane);
                    continue;
                }
                var o = it as JObject;
                if (o != null)
                {
                    string perr;
                    var fr = GeoUtil.ParsePlane(o, ctx, null, out perr);
                    if (fr == null) { a.Error(name + ": " + perr); continue; }
                    res.Add(fr.Plane);
                    continue;
                }
                a.Error(name + " must be a clipping-plane handle or {origin,x_axis,y_axis}");
            }
            return res;
        }

        // ------------------------------------------------------------------ viewport / placement

        static ViewportInfo BuildViewport(ViewSpec v, BoundingBox bb, List<string> warnings)
        {
            var center = bb.Center;
            double radius = Math.Max(bb.Diagonal.Length / 2.0, 1.0);
            if (v.Perspective && v.Info != null)
            {
                // Perspective: keep the user's viewing direction, up vector and lens, but move the camera so the SOURCE box fills the
                // frustum (temporary ViewportInfo; the live viewport is untouched). The drawing then does not depend on where the
                // user's Perspective camera happens to stand (or whether it is inside the model).
                var info = new ViewportInfo(v.Info);
                try
                {
                    double fl, fr, fb, ft, fn, ff;
                    if (info.GetFrustum(out fl, out fr, out fb, out ft, out fn, out ff) && fn > 0)
                    {
                        double tw = (fr - fl) / (2.0 * fn), th = (ft - fb) / (2.0 * fn);
                        var pdir = info.CameraDirection; pdir.Unitize();
                        var pup = info.CameraUp; pup.Unitize();
                        var pright = Vector3d.CrossProduct(pdir, pup); pright.Unitize();
                        double dist = Math.Max(radius * 0.05, 1e-6);
                        foreach (var p in bb.GetCorners())
                        {
                            var w = p - center;
                            double zc = w * pdir;
                            if (tw > 1e-9) dist = Math.Max(dist, Math.Abs(w * pright) / (tw * 0.9) - zc);
                            if (th > 1e-9) dist = Math.Max(dist, Math.Abs(w * pup) / (th * 0.9) - zc);
                        }
                        info.SetCameraLocation(center - pdir * dist);
                    }
                    info.SetFrustumNearFar(bb);
                }
                catch (Exception) { }
                return info.IsValidCamera ? info : null;
            }
            var look = v.Look; look.Unitize();
            var up = v.Up;
            double D = radius * 4.0;
            var vp = new ViewportInfo();
            vp.SetCameraLocation(center - look * D);
            vp.SetCameraDirection(look);
            vp.SetCameraUp(up);
            vp.SetScreenPort(0, 1000, 0, 1000, 0, 1);
            vp.ChangeToParallelProjection(true);
            double R = radius * 2.0;
            vp.SetFrustum(-R, R, -R, R, D - radius * 2.0, D + radius * 2.0);
            if (!vp.IsValidCamera || !vp.IsValidFrustum) return null;
            return vp;
        }

        static Point3d DefaultOrigin(RhinoDoc doc, List<RhinoObject> objs, BoundingBox sourceBox, string layerRoot, double drawingWidth, double tol)
        {
            // right of the model AND of everything already on the layer family of the output (so successive drawings sit side by side)
            var box = sourceBox;
            string top = LayerUtil.Split(layerRoot)[0];
            var tree = LayerTree.Build(doc, false);
            var topLayer = tree.Find(top);
            if (topLayer != null)
            {
                var ids = new HashSet<int>(tree.Subtree(topLayer).Select(l => l.Index));
                var s = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true };
                var targetIds = new HashSet<Guid>(objs.Select(o => o.Id));
                foreach (var o in doc.Objects.GetObjectList(s))
                {
                    if (!ids.Contains(o.Attributes.LayerIndex) || targetIds.Contains(o.Id)) continue;
                    var b = o.Geometry.GetBoundingBox(true);
                    if (b.IsValid) box.Union(b);
                }
            }
            double gap = Math.Max((box.Max.X - box.Min.X) * 0.1, Math.Max(tol * 100, 1.0));
            return new Point3d(box.Max.X + gap, sourceBox.Min.Y, 0);
        }

        static string SafeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "view";
            var chars = s.Trim().Select(c => (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ' ' || c == '.') ? c : '_').ToArray();
            return new string(chars);
        }
    }
}

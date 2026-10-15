using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Layers;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Model
{
    public sealed class ModelOverview : ITool
    {
        public ModelOverview() { ChangeJournal.EnsureInstalled(); }

        static readonly string[] Sections = { "totals", "layers", "blocks", "largest", "anomalies", "grid" };

        public string Name { get { return "model_overview"; } }
        public string Description
        {
            get
            {
                return "Bounded (about 1-3k tokens) summary of a large model: totals (objects by type, layers, blocks, groups, units, tolerance, bbox), " +
                       "layer tree (top_k per level by count + an others bucket, depth levels), block definitions with instance counts and nesting depth, " +
                       "largest objects by bbox volume, anomalies (invalid, tiny < 10x tolerance, far from origin > 1e5, duplicate candidates by bbox, " +
                       "objects on Default), and a coarse top-view occupancy grid. Use include to toggle sections and scope (objects_query filter) to limit it. " +
                       "Call this before objects_query on any non-trivial model; then drill down with objects_query / sets_save.";
            }
        }
        public string InputSchema
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{" +
                       "\"include\":{\"type\":\"array\",\"items\":{\"enum\":[\"totals\",\"layers\",\"blocks\",\"largest\",\"anomalies\",\"grid\"]},\"description\":\"Sections to return (default all)\"}," +
                       "\"depth\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":6,\"default\":2,\"description\":\"Layer tree levels expanded\"}," +
                       "\"top_k\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50,\"default\":8,\"description\":\"Max children per layer level / blocks listed\"}," +
                       "\"largest_n\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":25,\"default\":5}," +
                       "\"grid\":{\"type\":\"integer\",\"minimum\":2,\"maximum\":16,\"default\":8,\"description\":\"Occupancy grid cells per side\"}," +
                       "\"scope\":" + ObjectQuery.FilterSchema +
                       "},\"additionalProperties\":false}";
            }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        sealed class Big { public double Vol, Diag; public Guid Id; public ObjectType Type; public int Layer; public double X, Y, Z; }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var sw = Stopwatch.StartNew();
            var a = new ArgReader(args);
            int depth = a.Int("depth", false, 2, 1, 6);
            int topK = a.Int("top_k", false, 8, 1, 50);
            int largestN = a.Int("largest_n", false, 5, 0, 25);
            int gridN = a.Int("grid", false, 8, 2, 16);
            var inc = new HashSet<string>(Sections);
            JToken it;
            if (args != null && args.TryGetValue("include", out it) && it.Type != JTokenType.Null)
            {
                var arr = it as JArray;
                if (arr == null) a.Error("'include' must be an array of: " + string.Join(", ", Sections));
                else
                {
                    inc.Clear();
                    foreach (var x in arr)
                    {
                        string s = (string)x;
                        if (!Sections.Contains(s)) a.Error("'include': unknown section '" + s + "'. Valid: " + string.Join(", ", Sections));
                        else inc.Add(s);
                    }
                }
            }
            ObjectFilter filter = null;
            JToken sc;
            if (args != null && args.TryGetValue("scope", out sc) && sc.Type != JTokenType.Null)
            {
                if (!(sc is JObject)) a.Error("'scope' must be an object (objects_query filter)");
                else filter = ObjectFilter.Parse((JObject)sc, e => a.Error(e), "'scope': ");
            }
            if (a.HasErrors) return a.ErrorResult();

            double tol = ModelCommon.Tol(ctx);
            bool wantAnom = inc.Contains("anomalies"), wantLargest = inc.Contains("largest") && largestN > 0, wantGrid = inc.Contains("grid");
            bool wantBlocks = inc.Contains("blocks"), wantLayers = inc.Contains("layers"), wantTotals = inc.Contains("totals");

            // ---- single pass over the (scoped) objects
            var byType = new Dictionary<string, int>();
            var byLayer = new Dictionary<int, int>();
            var instCount = new Dictionary<int, int>();
            var bbox = BoundingBox.Empty;
            int total = 0, noBox = 0;
            var big = new List<Big>();
            double bigMin = -1;
            var cx = wantGrid ? new List<double>() : null;
            var cy = wantGrid ? new List<double>() : null;
            // anomalies
            int invalid = 0, invalidChecked = 0, tiny = 0, far = 0, onDefault = 0, dupObjs = 0;
            bool invalidPartial = false;
            var invalidEx = new List<string>(); var tinyEx = new List<string>(); var farEx = new List<string>(); var dupEx = new List<string>();
            var seen = wantAnom ? new Dictionary<long, Guid>() : null;
            var dupGroups = new HashSet<long>();
            int defIdx = wantAnom ? ModelCommon.DefaultLayerIndex(doc) : -1;
            const int Ex = 5;
            long invalidBudgetMs = 8000;

            foreach (var o in ObjectQuery.All(doc))
            {
                if (filter != null && !filter.Matches(doc, o)) continue;
                total++;
                string tn = ObjectQuery.TypeName(o);
                int c; byType.TryGetValue(tn, out c); byType[tn] = c + 1;
                int li = o.Attributes.LayerIndex;
                byLayer.TryGetValue(li, out c); byLayer[li] = c + 1;
                var io = o as InstanceObject;
                if (io != null && wantBlocks && io.InstanceDefinition != null)
                { instCount.TryGetValue(io.InstanceDefinition.Index, out c); instCount[io.InstanceDefinition.Index] = c + 1; }

                var bb = o.Geometry.GetBoundingBox(true);
                bool hasBox = bb.IsValid;
                if (!hasBox) noBox++; else bbox.Union(bb);

                if (hasBox && wantGrid) { cx.Add((bb.Min.X + bb.Max.X) * 0.5); cy.Add((bb.Min.Y + bb.Max.Y) * 0.5); }
                if (hasBox && wantLargest)
                {
                    double dx = bb.Max.X - bb.Min.X, dy = bb.Max.Y - bb.Min.Y, dz = bb.Max.Z - bb.Min.Z;
                    double vol = dx * dy * dz, diag = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    double key = vol > 0 ? vol : diag * 1e-12; // flat objects rank below volumetric ones, by diagonal
                    if (big.Count < largestN || key > bigMin)
                    {
                        big.Add(new Big { Vol = key, Diag = diag, Id = o.Id, Type = o.ObjectType, Layer = li, X = dx, Y = dy, Z = dz });
                        if (big.Count > largestN) { big.Sort((p, q) => q.Vol.CompareTo(p.Vol)); big.RemoveAt(big.Count - 1); }
                        if (big.Count >= largestN) bigMin = big.Min(b => b.Vol);
                    }
                }
                if (wantAnom)
                {
                    if (invalidChecked < int.MaxValue && !invalidPartial)
                    {
                        if (sw.ElapsedMilliseconds > invalidBudgetMs) invalidPartial = true;
                        else
                        {
                            invalidChecked++;
                            bool ok = true;
                            try { ok = o.Geometry.IsValid; } catch (Exception) { }
                            if (!ok) { invalid++; if (invalidEx.Count < Ex) invalidEx.Add(ctx.Handles.For(o.Id)); }
                        }
                    }
                    if (hasBox)
                    {
                        if (!ModelCommon.SizeIsMeaningless(o.ObjectType) && ModelCommon.MaxDim(bb) < 10 * tol)
                        { tiny++; if (tinyEx.Count < Ex) tinyEx.Add(ctx.Handles.For(o.Id)); }
                        if (ModelCommon.MaxAbs(bb) > ModelCommon.FarLimit)
                        { far++; if (farEx.Count < Ex) farEx.Add(ctx.Handles.For(o.Id)); }
                        long h = ModelCommon.BoxHash(o.ObjectType, bb, tol);
                        Guid first;
                        if (seen.TryGetValue(h, out first))
                        {
                            dupObjs++;
                            if (dupGroups.Add(h) && dupEx.Count < Ex) dupEx.Add(ctx.Handles.For(first) + "=" + ctx.Handles.For(o.Id));
                        }
                        else seen[h] = o.Id;
                    }
                    if (li == defIdx) onDefault++;
                }
            }

            var data = new Dictionary<string, object>();
            data["scope"] = filter == null ? "all" : "filtered";

            // ---- layer tree (needed for totals too)
            LayerTree tree = null;
            int liveLayers = ModelCommon.LiveLayerCount(doc);
            int emptyLayers = 0;
            if (wantLayers || wantTotals)
            {
                tree = LayerTree.Build(doc, false);
                foreach (var l in tree.All)
                {
                    int c;
                    if (!byLayer.TryGetValue(l.Index, out c) || c == 0) continue;
                    tree.Direct[l.Id] = c;
                    var cur = l; int guard = 0;
                    while (cur != null && guard++ < 64)
                    {
                        tree.Total[cur.Id] += c;
                        Layer p;
                        cur = tree.ById.TryGetValue(cur.ParentLayerId, out p) ? p : null;
                    }
                }
                emptyLayers = tree.All.Count(l => tree.Total[l.Id] == 0);
            }

            if (wantTotals)
            {
                int blocks = 0;
                for (int i = 0; i < doc.InstanceDefinitions.Count; i++) { var d = doc.InstanceDefinitions[i]; if (d != null && !d.IsDeleted) blocks++; }
                var t = new Dictionary<string, object>();
                t["objects"] = total;
                t["by_type"] = byType.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(15).ToDictionary(kv => kv.Key, kv => kv.Value);
                t["layers"] = liveLayers;
                t["empty_layers"] = emptyLayers;
                t["blocks"] = blocks;
                t["groups"] = doc.Groups.Count;
                t["named_views"] = doc.NamedViews.Count;
                t["units"] = ctx.Units;
                t["abs_tol"] = ctx.Tol;
                t["angle_tol_deg"] = ctx.AngleTolDeg;
                t["bbox"] = bbox.IsValid ? new { min = ModelCommon.Pt(bbox.Min), max = ModelCommon.Pt(bbox.Max), size = ModelCommon.Pt(new Point3d(bbox.Max.X - bbox.Min.X, bbox.Max.Y - bbox.Min.Y, bbox.Max.Z - bbox.Min.Z)) } : null;
                if (noBox > 0) t["without_bbox"] = noBox;
                data["totals"] = t;
            }

            if (wantLayers)
            {
                var roots = tree.Roots().Where(l => tree.Total[l.Id] > 0).OrderByDescending(l => tree.Total[l.Id]).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
                data["layer_tree"] = LevelNodes(tree, tree.Roots(), 1, depth, topK);
            }

            if (wantBlocks) data["blocks"] = BlockSection(doc, instCount, topK);

            if (wantLargest)
            {
                big.Sort((p, q) => q.Vol.CompareTo(p.Vol));
                data["largest"] = big.Select(b => new
                {
                    handle = ctx.Handles.For(b.Id),
                    type = b.Type.ToString().ToLowerInvariant(),
                    layer = ModelChanges.LayerName(doc, b.Layer),
                    size = new[] { ModelCommon.R(b.X), ModelCommon.R(b.Y), ModelCommon.R(b.Z) }
                }).ToList();
            }

            if (wantAnom)
            {
                var an = new Dictionary<string, object>();
                an["invalid"] = invalid == 0 ? (object)0 : new { count = invalid, examples = invalidEx };
                an["tiny"] = tiny == 0 ? (object)0 : new { count = tiny, threshold = ModelCommon.R(10 * tol, 6), examples = tinyEx };
                an["far_from_origin"] = far == 0 ? (object)0 : new { count = far, limit = ModelCommon.FarLimit, examples = farEx };
                an["duplicate_candidates"] = dupObjs == 0 ? (object)0 : new { objects = dupObjs, groups = dupGroups.Count, examples_first_eq_dup = dupEx };
                int otherLayers = tree == null ? liveLayers : liveLayers;
                an["on_default_layer"] = (liveLayers > 1 && onDefault > 0) ? (object)new { count = onDefault } : 0;
                if (invalidPartial) an["invalid_check_partial"] = invalidChecked + " of " + total + " objects checked (time budget)";
                data["anomalies"] = an;
            }

            if (wantGrid && bbox.IsValid && cx.Count > 0) data["grid_top_view"] = Grid(bbox, cx, cy, gridN);

            data["elapsed_ms"] = sw.ElapsedMilliseconds;
            return ToolResult.Ok(data);
        }

        // ---------------------------------------------------------------- sections

        static object LevelNodes(LayerTree t, List<Layer> siblings, int level, int maxDepth, int topK)
        {
            var live = siblings.Where(l => t.Total[l.Id] > 0).OrderByDescending(l => t.Total[l.Id]).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var shown = live.Take(topK).ToList();
            var nodes = new List<object>();
            foreach (var l in shown)
            {
                var n = new Dictionary<string, object>();
                n["name"] = l.Name;
                n["n"] = t.Total[l.Id];
                int own = t.Direct[l.Id];
                var kids = t.Children(l);
                if (own > 0 && own != t.Total[l.Id]) n["own"] = own;
                int kidsLive = kids.Count(k => t.Total[k.Id] > 0);
                if (kidsLive > 0)
                {
                    if (level < maxDepth) n["children"] = LevelNodes(t, kids, level + 1, maxDepth, topK);
                    else n["sublayers"] = t.Walk(kids).Count(k => t.Total[k.Id] > 0);
                }
                nodes.Add(n);
            }
            var rest = live.Skip(topK).ToList();
            int emptyHere = siblings.Count - live.Count;
            var res = new Dictionary<string, object>();
            res["layers"] = nodes;
            if (rest.Count > 0)
                res["others"] = new { layers = rest.Count, objects = rest.Sum(l => t.Total[l.Id]) };
            if (emptyHere > 0 && level == 1) res["empty"] = emptyHere;
            return res;
        }

        static object BlockSection(RhinoDoc doc, Dictionary<int, int> instCount, int topK)
        {
            var defs = new List<InstanceDefinition>();
            for (int i = 0; i < doc.InstanceDefinitions.Count; i++)
            {
                var d = doc.InstanceDefinitions[i];
                if (d != null && !d.IsDeleted) defs.Add(d);
            }
            var memo = new Dictionary<int, int>();
            var rows = defs.Select(d =>
            {
                int c; instCount.TryGetValue(d.Index, out c);
                return new { d = d, inst = c, depth = ModelCommon.NestDepth(doc, d.Index, memo, new HashSet<int>()) };
            }).OrderByDescending(r => r.inst).ThenByDescending(r => r.depth).ThenBy(r => r.d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var res = new Dictionary<string, object>();
            res["definitions"] = defs.Count;
            res["max_nesting"] = rows.Count == 0 ? 0 : rows.Max(r => r.depth);
            res["list"] = rows.Take(topK).Select(r => new { name = r.d.Name, instances = r.inst, nesting = r.depth, objects = r.d.ObjectCount }).ToList();
            if (rows.Count > topK) res["others"] = new { definitions = rows.Count - topK, instances = rows.Skip(topK).Sum(r => r.inst) };
            res["unused_in_scope"] = rows.Count(r => r.inst == 0);
            return res;
        }

        static object Grid(BoundingBox b, List<double> cx, List<double> cy, int n)
        {
            double w = b.Max.X - b.Min.X, h = b.Max.Y - b.Min.Y;
            double cw = w > 0 ? w / n : 1, ch = h > 0 ? h / n : 1;
            var cells = new int[n, n];
            for (int i = 0; i < cx.Count; i++)
            {
                int gx = w > 0 ? (int)((cx[i] - b.Min.X) / cw) : 0;
                int gy = h > 0 ? (int)((cy[i] - b.Min.Y) / ch) : 0;
                if (gx >= n) gx = n - 1; if (gy >= n) gy = n - 1; if (gx < 0) gx = 0; if (gy < 0) gy = 0;
                cells[gx, gy]++;
            }
            var rows = new List<string>();
            for (int gy = n - 1; gy >= 0; gy--)   // top of the map = +Y
            {
                var parts = new List<string>();
                for (int gx = 0; gx < n; gx++) parts.Add(Short(cells[gx, gy]));
                rows.Add(string.Join(" ", parts));
            }
            return new
            {
                note = "rows from +Y (top) to -Y, columns -X to +X; counts of object bbox centres",
                origin = new[] { ModelCommon.R(b.Min.X), ModelCommon.R(b.Min.Y) },
                cell = new[] { ModelCommon.R(cw), ModelCommon.R(ch) },
                rows = rows
            };
        }

        static string Short(int c)
        {
            if (c == 0) return ".";
            if (c < 1000) return c.ToString();
            if (c < 100000) return (c / 1000.0).ToString("0.#") + "k";
            return (c / 1000) + "k";
        }
    }
}

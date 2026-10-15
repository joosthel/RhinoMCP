using System;
using RhinoMCP.Tools.Shared;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino.Geometry;

namespace RhinoMCP.Tools.Measure
{
    public sealed class MeasureTool : ITool
    {
        public string Name { get { return "measure"; } }
        public string Description
        {
            get
            {
                return "Length, area, volume, centroid and bbox of objects, a set or a query, with optional group_by (layer | type | user_text:<key>) totals. " +
                       "Length: curves. Area: breps/surfaces/extrusions/meshes (not curves). Volume: closed solids only; open breps/meshes are skipped and counted under 'skipped' with example handles. " +
                       "Centroid is weighted by volume, else area, else length. Units are echoed (area unit^2, volume unit^3). per_object=N adds up to N per-object rows (default 0 = totals only).";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"target\":{\"$ref\":\"#/$defs/target\"}," +
                    "\"metrics\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"length\",\"area\",\"volume\",\"centroid\",\"bbox\"]},\"description\":\"Default: all\"}," +
                    "\"group_by\":{\"type\":\"string\",\"description\":\"layer | type | user_text:<key>\"}," +
                    "\"per_object\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500,\"default\":0,\"description\":\"Max per-object rows to return (0 = totals only)\"}," +
                    "\"include_guids\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Add object GUIDs to per-object rows\"}",
                    "\"target\"");
            }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        sealed class Acc
        {
            public int Count;
            public double Length, Area, Volume;
            public int LenN, AreaN, VolN;
            public Vector3d WVol, WArea, WLen, WPt;   // sum(weight * centroid)
            public double SVol, SArea, SLen; public int NPt;
            public BoundingBox Box = BoundingBox.Empty;

            public void Add(Row r)
            {
                Count++;
                if (r.Length.HasValue) { Length += r.Length.Value; LenN++; }
                if (r.Area.HasValue) { Area += r.Area.Value; AreaN++; }
                if (r.Volume.HasValue) { Volume += r.Volume.Value; VolN++; }
                if (r.Centroid.HasValue)
                {
                    var c = new Vector3d(r.Centroid.Value);
                    switch (r.Basis)
                    {
                        case "volume": WVol += c * r.Weight; SVol += r.Weight; break;
                        case "area": WArea += c * r.Weight; SArea += r.Weight; break;
                        case "length": WLen += c * r.Weight; SLen += r.Weight; break;
                        default: WPt += c; NPt++; break;
                    }
                }
                if (r.Box.IsValid) Box.Union(r.Box);
            }
            public Point3d? Centroid(out string basis)
            {
                basis = null;
                if (SVol > 0) { basis = "volume"; return new Point3d(WVol / SVol); }
                if (SArea > 0) { basis = "area"; return new Point3d(WArea / SArea); }
                if (SLen > 0) { basis = "length"; return new Point3d(WLen / SLen); }
                if (NPt > 0) { basis = "points"; return new Point3d(WPt / NPt); }
                return null;
            }
        }

        sealed class Row
        {
            public Geo G;
            public double? Length, Area, Volume;
            public Point3d? Centroid; public string Basis; public double Weight;
            public BoundingBox Box;
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var a = new ArgReader(args);
            var objs = ObjectQuery.TargetOf(ctx, args, a, "target");
            int perObject = a.Int("per_object", false, 0, 0, 500);
            string groupBy = a.String("group_by");
            bool guids = a.Bool("include_guids", false);
            var metrics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JToken mt;
            if (args != null && args.TryGetValue("metrics", out mt) && mt is JArray)
            {
                foreach (var m in (JArray)mt)
                {
                    string s = (string)m;
                    if (new[] { "length", "area", "volume", "centroid", "bbox" }.Contains(s)) metrics.Add(s);
                    else a.Error("metrics: unknown '" + s + "'. Use length|area|volume|centroid|bbox");
                }
            }
            if (metrics.Count == 0) foreach (var s in new[] { "length", "area", "volume", "centroid", "bbox" }) metrics.Add(s);
            string userKey = null;
            if (groupBy != null)
            {
                if (groupBy.StartsWith("user_text:", StringComparison.OrdinalIgnoreCase)) userKey = groupBy.Substring(10);
                else if (groupBy != "layer" && groupBy != "type") a.Error("'group_by' must be layer | type | user_text:<key>");
                if (userKey != null && userKey.Length == 0) a.Error("'group_by' user_text needs a key, e.g. user_text:ai.space");
            }
            if (a.HasErrors) return a.ErrorResult();
            if (objs.Count == 0) return ToolResult.Fail("NOT_FOUND", "Target resolved to no objects", "Check the handle/set/query (e.g. objects_query) and try again.");

            var skipped = new Dictionary<string, List<string>>();
            Action<string, string> skip = (reason, handle) =>
            {
                List<string> l; if (!skipped.TryGetValue(reason, out l)) { l = new List<string>(); skipped[reason] = l; }
                l.Add(handle);
            };

            var total = new Acc();
            var groups = new Dictionary<string, Acc>();
            var rows = new List<object>();
            int unsupported = 0;
            foreach (var o in objs)
            {
                var g = Geo.From(o, ctx);
                if (g.Kind == GKind.None) { skip(g.Reason, g.Handle); unsupported++; continue; }
                var r = Compute(g, metrics, skip);
                total.Add(r);
                if (groupBy != null)
                {
                    string key;
                    if (groupBy == "layer") key = g.Layer;
                    else if (groupBy == "type") key = o.ObjectType.ToString();
                    else { key = o.Attributes.GetUserString(userKey); if (string.IsNullOrEmpty(key)) key = "(none)"; }
                    Acc ga; if (!groups.TryGetValue(key, out ga)) { ga = new Acc(); groups[key] = ga; }
                    ga.Add(r);
                }
                if (rows.Count < perObject)
                {
                    var row = new JObject();
                    row["handle"] = g.Handle; if (guids) row["guid"] = o.Id.ToString(); row["type"] = o.ObjectType.ToString(); row["layer"] = g.Layer;
                    if (r.Length.HasValue) row["length"] = MU.R(r.Length.Value);
                    if (r.Area.HasValue) row["area"] = MU.R(r.Area.Value);
                    if (r.Volume.HasValue) row["volume"] = MU.R(r.Volume.Value);
                    if (r.Centroid.HasValue && metrics.Contains("centroid")) row["centroid"] = JToken.FromObject(MU.P(r.Centroid.Value));
                    rows.Add(row);
                }
            }

            var warnings = new List<string>();
            var data = new JObject();
            data["count"] = objs.Count;
            data["measured"] = total.Count;
            data["totals"] = Totals(total, metrics);
            data["units"] = new JObject { { "length", ctx.Units }, { "area", ctx.Units + "^2" }, { "volume", ctx.Units + "^3" } };
            if (groupBy != null)
            {
                var gl = new JArray();
                foreach (var kv in groups.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Take(100))
                {
                    var go = Totals(kv.Value, metrics);
                    go["key"] = kv.Key;
                    gl.Add(go);
                }
                data["group_by"] = groupBy;
                data["groups"] = gl;
                if (groups.Count > 100) warnings.Add("Showing the first 100 of " + groups.Count + " groups (alphabetical). Narrow the target.");
            }
            if (perObject > 0) { data["objects"] = new JArray(rows); if (objs.Count > perObject) data["objects_truncated"] = true; }
            if (skipped.Count > 0)
            {
                var so = new JObject();
                foreach (var kv in skipped)
                    so[kv.Key] = new JObject { { "count", kv.Value.Count }, { "examples", new JArray(kv.Value.Take(10)) } };
                data["skipped"] = so;
                foreach (var kv in skipped)
                    warnings.Add(kv.Value.Count + " object(s) skipped: " + Explain(kv.Key));
            }
            var res = ToolResult.Ok(data);
            res.Units = ctx.Units;
            foreach (var w in warnings) res.Warn(w);
            return res;
        }

        static string Explain(string reason)
        {
            switch (reason)
            {
                case "open_brep_no_volume": return "open brep/polysurface has no volume (area still counted). Close/cap it (geometry_validate / Cap) to include volume.";
                case "open_mesh_no_volume": return "open mesh has no volume (area still counted).";
                case "mass_properties_failed": return "mass properties computation failed (check validity with geometry_validate).";
            }
            if (reason.StartsWith("unsupported_type:")) return "unsupported object type " + reason.Substring(17) + " (blocks, annotations, ... are not measured).";
            return reason;
        }

        static JObject Totals(Acc acc, HashSet<string> metrics)
        {
            var o = new JObject();
            o["count"] = acc.Count;
            if (metrics.Contains("length")) { o["length"] = MU.R(acc.Length); o["length_objects"] = acc.LenN; }
            if (metrics.Contains("area")) { o["area"] = MU.R(acc.Area); o["area_objects"] = acc.AreaN; }
            if (metrics.Contains("volume")) { o["volume"] = MU.R(acc.Volume); o["volume_objects"] = acc.VolN; }
            if (metrics.Contains("centroid"))
            {
                string basis; var c = acc.Centroid(out basis);
                if (c.HasValue) { o["centroid"] = JToken.FromObject(MU.P(c.Value)); o["centroid_basis"] = basis; }
            }
            if (metrics.Contains("bbox") && acc.Box.IsValid)
                o["bbox"] = JToken.FromObject(MU.Box(acc.Box));
            return o;
        }

        static Row Compute(Geo g, HashSet<string> metrics, Action<string, string> skip)
        {
            var r = new Row { G = g, Box = g.Box };
            bool wantL = metrics.Contains("length"), wantA = metrics.Contains("area"), wantV = metrics.Contains("volume"), wantC = metrics.Contains("centroid");
            try
            {
                switch (g.Kind)
                {
                    case GKind.Point:
                        r.Centroid = ((Rhino.Geometry.Point)g.G).Location; r.Basis = "points";
                        break;
                    case GKind.Curve:
                        {
                            var c = (Curve)g.G;
                            if (wantL || wantC)
                            {
                                var lp = LengthMassProperties.Compute(c);
                                if (lp != null)
                                {
                                    r.Length = lp.Length; r.Centroid = lp.Centroid; r.Basis = "length"; r.Weight = lp.Length;
                                }
                                else { r.Length = c.GetLength(); skip("mass_properties_failed", g.Handle); }
                            }
                            break;
                        }
                    case GKind.Brep:
                    case GKind.Mesh:
                        {
                            bool solid = g.IsSolid;
                            AreaMassProperties ap = null;
                            if (wantA || wantC)
                                ap = g.Kind == GKind.Brep ? AreaMassProperties.Compute((Brep)g.G) : AreaMassProperties.Compute((Mesh)g.G);
                            if (ap != null) { r.Area = ap.Area; if (wantC) { r.Centroid = ap.Centroid; r.Basis = "area"; r.Weight = ap.Area; } }
                            else if (wantA || wantC) skip("mass_properties_failed", g.Handle);
                            if (wantV || wantC)
                            {
                                if (!solid) { if (wantV) skip(g.Kind == GKind.Brep ? "open_brep_no_volume" : "open_mesh_no_volume", g.Handle); }
                                else
                                {
                                    var vp = g.Kind == GKind.Brep ? VolumeMassProperties.Compute((Brep)g.G) : VolumeMassProperties.Compute((Mesh)g.G);
                                    if (vp != null)
                                    {
                                        r.Volume = Math.Abs(vp.Volume);
                                        if (wantC) { r.Centroid = vp.Centroid; r.Basis = "volume"; r.Weight = r.Volume.Value; }
                                    }
                                    else skip("mass_properties_failed", g.Handle);
                                }
                            }
                            break;
                        }
                }
            }
            catch (Exception) { skip("mass_properties_failed", g.Handle); }
            if (!wantL) r.Length = null;
            if (!wantA) r.Area = null;
            if (!wantV) r.Volume = null;
            return r;
        }
    }
}

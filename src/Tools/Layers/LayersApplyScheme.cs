using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino.DocObjects;

namespace RhinoMCP.Tools.Layers
{
    public sealed class LayersApplyScheme : ITool
    {
        public string Name { get { return "layers_apply_scheme"; } }
        public string Description
        {
            get
            {
                return "Create/update layers from a layer scheme: built-in id 'AIA-NCS' (compact US NCS architectural set) or 'ISO-13567' (illustrative example), or a custom scheme object " +
                       "(format: skills/rhino-layer-standards/references/scheme-format.md). Never renames, moves or deletes existing layers, and never touches objects; " +
                       "existing layers get only the listed fields updated. Instead it returns mapping_suggestions (existing layer -> scheme layer, with object counts) for a separate, confirmed rename/move step. " +
                       "clear_stored_scheme:true only forgets the stored scheme. dry_run defaults to TRUE and returns the plan; pass dry_run:false to apply. On apply the scheme id is stored in document user text 'rhino-mcp.layer-scheme' " +
                       "(custom schemes also as JSON in 'rhino-mcp.layer-scheme-json'). Unknown linetypes fall back to Continuous with a warning. Persist only after the user confirmed the scheme.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"scheme\":{\"description\":\"Built-in id (AIA-NCS, ISO-13567) or a scheme object {name, delimiter, layers:[{path,color,linetype,print_width_mm,plot,required}]}\",\"oneOf\":[{\"type\":\"string\"},{\"type\":\"object\"}]}," +
                    "\"dry_run\":{\"type\":\"boolean\",\"default\":true}," +
                    "\"persist\":{\"type\":\"boolean\",\"default\":true,\"description\":\"Store the scheme id in the document when applying\"}," +
                    "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500,\"default\":100,\"description\":\"Max plan / suggestion rows returned\"}," +
                    "\"clear_stored_scheme\":{\"type\":\"boolean\",\"description\":\"Only remove the scheme id/JSON stored in the document (no layers touched); 'scheme' is then not needed\"}",
                    "");
            }
        }
        public ToolKind Kind { get { return ToolKind.Mutate; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            bool dry = a.Bool("dry_run", true);
            bool persist = a.Bool("persist", true);
            int limit = a.Int("limit", false, 100, 1, 500);
            bool clear = a.Bool("clear_stored_scheme", false);
            if (clear && !a.HasErrors)
            {
                string old = doc.Strings.GetValue(LayerUtil.SchemeKey);
                doc.Strings.Delete(LayerUtil.SchemeKey);
                doc.Strings.Delete(LayerUtil.SchemeJsonKey);
                return ToolResult.Ok(new { cleared = true, previous = string.IsNullOrEmpty(old) ? null : old });
            }
            if (!a.Has("scheme")) a.Error("'scheme' is required (built-in id or scheme object)");
            if (a.HasErrors) return a.ErrorResult();

            var errs = new List<string>();
            var warnings = new List<string>();
            string source;
            var scheme = Scheme.Resolve(args["scheme"], doc, errs, warnings, out source);
            if (errs.Count > 0) return ToolResult.Fail("INVALID_ARGS", string.Join("; ", errs), "Fix the scheme and call again.", new { errors = errs });
            if (scheme == null) return ToolResult.Fail("INVALID_ARGS", "Could not resolve the scheme", "Pass a built-in id or a scheme object.");

            var tree = LayerTree.Build(doc);
            var plan = new List<Dictionary<string, object>>();
            int created = 0, updated = 0, unchanged = 0, failed = 0;
            var applyErrors = new List<string>();
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Implicit parents first (hierarchical schemes), then listed layers in order.
            var ordered = new List<KeyValuePair<string, SchemeLayer>>();
            var listed = new HashSet<string>(scheme.Layers.Select(l => l.RhinoPath), StringComparer.OrdinalIgnoreCase);
            foreach (var sl in scheme.Layers)
            {
                var segs = LayerUtil.Split(sl.RhinoPath);
                string cum = "";
                for (int i = 0; i < segs.Length - 1; i++)
                {
                    cum = cum.Length == 0 ? segs[i] : cum + LayerUtil.Sep + segs[i];
                    if (!listed.Contains(cum) && wanted.Add(cum)) ordered.Add(new KeyValuePair<string, SchemeLayer>(cum, null));
                }
                if (wanted.Add(sl.RhinoPath)) ordered.Add(new KeyValuePair<string, SchemeLayer>(sl.RhinoPath, sl));
            }

            foreach (var kv in ordered)
            {
                string path = kv.Key;
                var sl = kv.Value;
                var row = new Dictionary<string, object>();
                row["path"] = path;
                var existing = dry ? tree.Find(path) : LayerTree.Build(doc, false).Find(path);
                var errors = new List<string>();
                if (existing == null)
                {
                    if (dry)
                    {
                        row["action"] = "create";
                        if (sl != null) using (var tmp = new Layer()) sl.Props.Apply(doc, tmp, warnings, errors, false); // validates linetype names (warning only)
                        created++;
                    }
                    else
                    {
                        string err;
                        var l = LayerUtil.Ensure(doc, path, null, out err);
                        if (l == null) { failed++; row["action"] = "failed"; row["error"] = err; applyErrors.Add(err); plan.Add(row); continue; }
                        if (sl != null) sl.Props.Apply(doc, l, warnings, errors);
                        row["action"] = "create";
                        created++;
                    }
                }
                else if (sl == null) { row["action"] = "unchanged"; unchanged++; }
                else
                {
                    var changes = sl.Props.Apply(doc, existing, warnings, errors, !dry);
                    if (changes.Count > 0) { row["action"] = "update"; row["changes"] = changes; updated++; }
                    else { row["action"] = "unchanged"; unchanged++; }
                }
                if (errors.Count > 0) { row["error"] = string.Join("; ", errors); applyErrors.AddRange(errors); }
                plan.Add(row);
            }

            // Suggestions for existing layers outside the scheme (never applied here).
            var schemePaths = scheme.AllPaths();
            var suggestions = new List<object>();
            int suggestTotal = 0;
            foreach (var l in tree.All)
            {
                if (l.Index == 0 && string.Equals(l.Name, "Default", StringComparison.OrdinalIgnoreCase)) continue;
                if (schemePaths.Contains(l.FullPath)) continue;
                string s = scheme.Suggest(l.FullPath);
                if (s == null) continue;
                suggestTotal++;
                if (suggestions.Count < limit) suggestions.Add(new { from = l.FullPath, to = s, objects = tree.Direct[l.Id] });
            }

            if (!dry && persist && failed == 0)
            {
                string prev = doc.Strings.GetValue(LayerUtil.SchemeKey);
                doc.Strings.SetString(LayerUtil.SchemeKey, scheme.Name);
                if (scheme.IsBuiltIn) doc.Strings.Delete(LayerUtil.SchemeJsonKey);
                else doc.Strings.SetString(LayerUtil.SchemeJsonKey, scheme.Json.ToString(Newtonsoft.Json.Formatting.None));
                if (!string.IsNullOrEmpty(prev) && !string.Equals(prev, scheme.Name, StringComparison.OrdinalIgnoreCase))
                    warnings.Add("Replaced stored document scheme '" + prev + "' with '" + scheme.Name + "'.");
            }

            var res = ToolResult.Ok(new
            {
                scheme = scheme.Name,
                dry_run = dry,
                persisted = !dry && persist && failed == 0,
                created = created,
                updated = updated,
                unchanged = unchanged,
                failed = failed,
                plan_rows = plan.Count,
                plan = plan.Where(r => (string)r["action"] != "unchanged").Take(limit).ToList(),
                mapping_suggestions_total = suggestTotal,
                mapping_suggestions = suggestions,
                note = suggestTotal > 0 ? "Existing layers were NOT renamed or moved. Review mapping_suggestions, then use layers_manage (rename/move) and layers_move_objects after the user confirms." : null
            });
            foreach (var w in warnings.Distinct()) res.Warn(w);
            if (dry) res.Warn("dry_run: nothing was changed. Call again with dry_run:false to apply.");
            return res;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino.DocObjects;

namespace RhinoMCP.Tools.Layers
{
    public sealed class LayersAudit : ITool
    {
        public string Name { get { return "layers_audit"; } }
        public string Description
        {
            get
            {
                return "Read-only layer audit. Numbers first, then top examples per category: non_conforming (layers not in the scheme; needs a scheme), empty_layers (leaf layers without objects), " +
                       "objects_on_default (objects on the Default layer), objects_on_parent_layers (objects sitting directly on layers that have sublayers), " +
                       "non_english_names (non-ASCII characters or common non-English words), duplicate_names (same name ignoring case/spaces/-/_), missing_required (required scheme layers that do not exist). " +
                       "scheme: built-in id (AIA-NCS, ISO-13567), a scheme object, or omitted to use the scheme stored in document user text 'rhino-mcp.layer-scheme'. " +
                       "'under' limits the audited layers to one subtree (missing_required is always document-wide). Nothing is changed.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"scheme\":{\"description\":\"Built-in id or scheme object; default: the scheme stored in the document\",\"oneOf\":[{\"type\":\"string\"},{\"type\":\"object\"}]}," +
                    "\"under\":{\"type\":\"string\",\"description\":\"Audit only this layer path and its descendants\"}," +
                    "\"max_examples\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100,\"default\":10}");
            }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        static readonly HashSet<string> NonEnglish = new HashSet<string>
        {
            // German
            "wand", "waende", "wande", "tuer", "tur", "tueren", "fenster", "boden", "decke", "dach", "treppe", "bemassung", "hilfslinien", "achsen", "schnitt", "grundriss", "ansicht", "schraffur", "beschriftung", "gelaende", "mobel", "moebel", "stuetzen", "saeulen", "balken", "raum", "raeume", "entwurf", "bestand", "abbruch",
            // French
            "mur", "murs", "porte", "portes", "fenetre", "fenetres", "plancher", "toit", "escalier", "cotation", "coupe", "facade", "meubles", "plafond", "poteaux",
            // Spanish / Italian / Portuguese
            "muro", "muros", "puerta", "puertas", "ventana", "ventanas", "suelo", "techo", "escalera", "pared", "paredes", "porta", "finestra", "pavimento", "tetto", "scala", "parete", "pareti", "janela", "telhado",
            // Dutch
            "muur", "muren", "deur", "deuren", "raam", "ramen", "vloer", "dak", "trap", "maatvoering", "gevel", "kolommen", "plafond"
        };

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            string under = a.String("under");
            int maxEx = a.Int("max_examples", false, 10, 1, 100);
            if (a.HasErrors) return a.ErrorResult();

            var errs = new List<string>();
            var warnings = new List<string>();
            string source;
            var scheme = Scheme.Resolve(args == null ? null : args["scheme"], doc, errs, warnings, out source);
            if (errs.Count > 0) return ToolResult.Fail("INVALID_ARGS", string.Join("; ", errs), "Fix the scheme and call again.", new { errors = errs });

            var tree = LayerTree.Build(doc);
            IEnumerable<Layer> scope = tree.All;
            if (under != null)
            {
                var u = tree.Find(under);
                if (u == null) return ToolResult.Fail("NOT_FOUND", "Layer '" + under + "' does not exist", "Call layers_list to see the paths.");
                scope = tree.Subtree(u);
            }
            var layers = scope.ToList();
            bool IsDefault(Layer l) { return l.Index == 0 && string.Equals(l.Name, "Default", StringComparison.OrdinalIgnoreCase) && l.ParentLayerId == Guid.Empty; }

            // non-conforming
            List<object> nonConf = null; int nonConfN = 0;
            if (scheme != null)
            {
                var allowed = scheme.AllPaths();
                nonConf = new List<object>();
                foreach (var l in layers)
                {
                    if (IsDefault(l) || allowed.Contains(l.FullPath)) continue;
                    nonConfN++;
                    if (nonConf.Count < maxEx) nonConf.Add(new { path = l.FullPath, objects = tree.Direct[l.Id], suggestion = scheme.Suggest(l.FullPath) });
                }
            }
            else warnings.Add("No scheme given and none stored in the document (user text 'rhino-mcp.layer-scheme'); non_conforming and missing_required were skipped.");

            // empty leaf layers
            var empty = layers.Where(l => tree.Children(l).Count == 0 && tree.Direct[l.Id] == 0).ToList();

            // default / parent layers
            var def = layers.FirstOrDefault(IsDefault);
            int onDefault = def == null ? 0 : tree.Direct[def.Id];
            var onParents = layers.Where(l => tree.Children(l).Count > 0 && tree.Direct[l.Id] > 0 && !IsDefault(l)).ToList();

            // non-English
            var nonEn = new List<object>(); int nonEnN = 0;
            foreach (var l in layers)
            {
                if (IsDefault(l)) continue;
                string reason = null;
                if (l.FullPath.Any(c => c > 127)) reason = "non-ASCII characters";
                else
                {
                    var words = l.FullPath.Split(new[] { ':', '-', '_', ' ', '.', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant());
                    var hit = words.Where(w => NonEnglish.Contains(w)).Distinct().ToList();
                    if (hit.Count > 0) reason = "non-English word(s): " + string.Join(", ", hit);
                }
                if (reason == null) continue;
                nonEnN++;
                if (nonEn.Count < maxEx) nonEn.Add(new { path = l.FullPath, objects = tree.Direct[l.Id], reason = reason });
            }

            // duplicate-ish names
            var groups = layers.GroupBy(l => Scheme.Norm(l.FullPath)).Where(g => g.Key.Length > 0 && g.Count() > 1).ToList();
            var dups = groups.Take(maxEx).Select(g => new { paths = g.Select(l => l.FullPath).ToList(), objects = g.Sum(l => tree.Direct[l.Id]) }).ToList();

            // missing required
            List<string> missing = null; int missingN = 0, missingOptional = 0;
            if (scheme != null)
            {
                missing = new List<string>();
                foreach (var sl in scheme.Layers)
                {
                    if (tree.Find(sl.RhinoPath) != null) continue;
                    if (!sl.Required) { missingOptional++; continue; }
                    missingN++;
                    if (missing.Count < maxEx) missing.Add(sl.RhinoPath);
                }
            }

            var counts = new List<KeyValuePair<string, int>>();
            if (scheme != null) { counts.Add(new KeyValuePair<string, int>("non_conforming", nonConfN)); counts.Add(new KeyValuePair<string, int>("missing_required", missingN)); }
            counts.Add(new KeyValuePair<string, int>("empty_layers", empty.Count));
            counts.Add(new KeyValuePair<string, int>("objects_on_default", onDefault));
            counts.Add(new KeyValuePair<string, int>("objects_on_parent_layers", onParents.Sum(l => tree.Direct[l.Id])));
            counts.Add(new KeyValuePair<string, int>("non_english_names", nonEnN));
            counts.Add(new KeyValuePair<string, int>("duplicate_names", groups.Count));

            var res = ToolResult.Ok(new
            {
                scheme = scheme == null ? null : scheme.Name,
                scheme_source = source,
                layers_audited = layers.Count,
                objects_audited = layers.Sum(l => tree.Direct[l.Id]),
                issue_counts = counts.ToDictionary(kv => kv.Key, kv => kv.Value),
                top_issues = counts.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(3).ToList(),
                non_conforming = nonConf,
                missing_required = missing,
                missing_optional_count = scheme == null ? (int?)null : missingOptional,
                empty_layers = empty.Take(maxEx).Select(l => l.FullPath).ToList(),
                objects_on_default = onDefault,
                objects_on_parent_layers = onParents.Take(maxEx).Select(l => new { path = l.FullPath, objects = tree.Direct[l.Id] }).ToList(),
                non_english_names = nonEn,
                duplicate_names = dups
            });
            foreach (var w in warnings.Distinct()) res.Warn(w);
            return res;
        }
    }
}

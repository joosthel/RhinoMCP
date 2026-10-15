using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Model
{
    public sealed class VerifyAudit : ITool
    {
        public VerifyAudit() { ChangeJournal.EnsureInstalled(); }

        static readonly string[] Checks = { "invalid", "open_solid", "duplicates", "tiny", "far", "role_layer", "empty_layers", "orphan_blocks" };

        public string Name { get { return "verify_audit"; } }
        public string Description
        {
            get
            {
                return "Health check of the model or of a part of it. Scope: since=<journal token from model_changes> (only objects changed since), " +
                       "target (handle/set/query), or neither = whole model. Checks: invalid objects (with a short IsValidWithLog reason), open breps/extrusions " +
                       "where the layer name contains 'solid' or user text ai.solid=true / ai.role=solid, duplicate candidates (same type + bbox within tolerance), " +
                       "tiny objects (< 10x tolerance), far from origin (> 1e5 units), objects whose user text ai.role does not fit their layer, " +
                       "and (whole-model scope only) empty layers and orphan block definitions. Returns health ok|warn|fail, counts per check and top issues with handles. " +
                       "Read-only; fix with geometry_edit / objects_set_props after looking.";
            }
        }
        public string InputSchema
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{" +
                       "\"since\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Audit objects added/changed since this model_changes token\"}," +
                       "\"target\":" + ObjectQuery.TargetSchema + "," +
                       "\"checks\":{\"type\":\"array\",\"items\":{\"enum\":[\"invalid\",\"open_solid\",\"duplicates\",\"tiny\",\"far\",\"role_layer\",\"empty_layers\",\"orphan_blocks\"]},\"description\":\"Default all\"}," +
                       "\"examples\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20,\"default\":5,\"description\":\"Handles listed per issue\"}," +
                       "\"max_ms\":{\"type\":\"integer\",\"minimum\":500,\"maximum\":50000,\"default\":20000,\"description\":\"Time budget; result says truncated if hit\"}" +
                       "},\"additionalProperties\":false}";
            }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        sealed class Issue
        {
            public string Check, Severity; public int Count; public List<object> Ex = new List<object>();
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            ChangeJournal.EnsureInstalled();
            var sw = Stopwatch.StartNew();
            var a = new ArgReader(args);
            int ex = a.Int("examples", false, 5, 1, 20);
            int maxMs = a.Int("max_ms", false, 20000, 500, 50000);
            bool hasSince = a.Has("since"), hasTarget = a.Has("target");
            int since = a.Int("since", false, 0, 0, null);
            if (hasSince && hasTarget) a.Error("give either 'since' or 'target', not both");
            var checks = new HashSet<string>(Checks);
            JToken ct;
            if (args != null && args.TryGetValue("checks", out ct) && ct.Type != JTokenType.Null)
            {
                var arr = ct as JArray;
                if (arr == null) a.Error("'checks' must be an array of: " + string.Join(", ", Checks));
                else
                {
                    checks.Clear();
                    foreach (var x in arr)
                    {
                        string s = (string)x;
                        if (!Checks.Contains(s)) a.Error("'checks': unknown '" + s + "'. Valid: " + string.Join(", ", Checks));
                        else checks.Add(s);
                    }
                }
            }
            List<RhinoObject> objs = null;
            string scopeKind = "all";
            bool journalTruncated = false;
            if (hasTarget)
            {
                objs = ObjectQuery.Resolve(ctx, args["target"], a, "target");
                scopeKind = "target";
            }
            else if (hasSince && !a.HasErrors)
            {
                if (!ChangeJournal.Installed) a.Error("change journal unavailable: " + ChangeJournal.InstallError);
                else
                {
                    var ev = ChangeJournal.Since(since, out journalTruncated);
                    var ids = new List<Guid>(); var seen = new HashSet<Guid>();
                    for (int i = ev.Count - 1; i >= 0; i--)
                    {
                        var e = ev[i];
                        if (e.Kind >= JKind.LayerAdded || e.Kind == JKind.Delete) { if (e.Kind == JKind.Delete) seen.Add(e.Id); continue; }
                        if (seen.Add(e.Id)) ids.Add(e.Id);
                    }
                    ids.Reverse();   // oldest first, so "same_as" points at the earlier object
                    objs = new List<RhinoObject>();
                    foreach (var id in ids) { var o = doc.Objects.FindId(id); if (o != null && !o.IsDeleted) objs.Add(o); }
                    scopeKind = "since";
                }
            }
            if (a.HasErrors) return a.ErrorResult();
            bool whole = objs == null;
            IEnumerable<RhinoObject> source = whole ? ObjectQuery.All(doc) : (IEnumerable<RhinoObject>)objs;

            double tol = ModelCommon.Tol(ctx);
            var issues = new Dictionary<string, Issue>();
            Func<string, string, Issue> iss = (code, sev) =>
            {
                Issue i; if (!issues.TryGetValue(code, out i)) { i = new Issue { Check = code, Severity = sev }; issues[code] = i; }
                return i;
            };

            bool cInvalid = checks.Contains("invalid"), cSolid = checks.Contains("open_solid"), cDup = checks.Contains("duplicates"),
                 cTiny = checks.Contains("tiny"), cFar = checks.Contains("far"), cRole = checks.Contains("role_layer");
            var dupSeen = cDup ? new Dictionary<long, Guid>() : null;
            var dupGroups = new HashSet<long>();
            var layerSolid = new Dictionary<int, bool>();
            var layerRole = new Dictionary<int, string>();
            int scanned = 0; bool timedOut = false;

            foreach (var o in source)
            {
                if ((scanned & 63) == 0 && sw.ElapsedMilliseconds > maxMs) { timedOut = true; break; }
                scanned++;
                var g = o.Geometry;
                int li = o.Attributes.LayerIndex;
                if (cInvalid)
                {
                    bool ok = true;
                    try { ok = g.IsValid; } catch (Exception) { }
                    if (!ok)
                    {
                        string log; try { g.IsValidWithLog(out log); } catch (Exception) { log = ""; }
                        var s = iss("invalid", "error"); s.Count++;
                        if (s.Ex.Count < ex) s.Ex.Add(new { handle = ctx.Handles.For(o.Id), type = ObjectQuery.TypeName(o), why = ModelCommon.FirstLine(log, 100) });
                    }
                }
                if (cSolid && (g is Brep || g is Extrusion))
                {
                    bool wantSolid;
                    if (!layerSolid.TryGetValue(li, out wantSolid))
                    {
                        string lp = ModelChanges.LayerName(doc, li);
                        wantSolid = lp.IndexOf("solid", StringComparison.OrdinalIgnoreCase) >= 0;
                        layerSolid[li] = wantSolid;
                    }
                    if (!wantSolid)
                    {
                        string us = o.Attributes.GetUserString("ai.solid");
                        string role = o.Attributes.GetUserString("ai.role");
                        wantSolid = (us != null && us.Equals("true", StringComparison.OrdinalIgnoreCase)) || (role != null && role.Equals("solid", StringComparison.OrdinalIgnoreCase));
                    }
                    if (wantSolid)
                    {
                        bool solid = true;
                        try
                        {
                            var b = g as Brep; var e2 = g as Extrusion;
                            if (b != null) solid = b.IsSolid;
                            else if (e2 != null) solid = e2.IsSolid;
                        }
                        catch (Exception) { }
                        if (!solid)
                        {
                            var s = iss("open_solid", "error"); s.Count++;
                            if (s.Ex.Count < ex) s.Ex.Add(new { handle = ctx.Handles.For(o.Id), layer = ModelChanges.LayerName(doc, li), why = "expected solid, has naked edges" });
                        }
                    }
                }
                BoundingBox bb = default(BoundingBox);
                bool hasBox = false;
                if (cDup || cTiny || cFar) { bb = g.GetBoundingBox(true); hasBox = bb.IsValid; }
                if (hasBox)
                {
                    if (cTiny && !ModelCommon.SizeIsMeaningless(o.ObjectType) && ModelCommon.MaxDim(bb) < 10 * tol)
                    {
                        var s = iss("tiny", "warn"); s.Count++;
                        if (s.Ex.Count < ex) s.Ex.Add(new { handle = ctx.Handles.For(o.Id), type = ObjectQuery.TypeName(o), size = ModelCommon.R(ModelCommon.MaxDim(bb), 6) });
                    }
                    if (cFar && ModelCommon.MaxAbs(bb) > ModelCommon.FarLimit)
                    {
                        var s = iss("far", "warn"); s.Count++;
                        if (s.Ex.Count < ex) s.Ex.Add(new { handle = ctx.Handles.For(o.Id), dist = ModelCommon.R(ModelCommon.MaxAbs(bb), 0) });
                    }
                    if (cDup)
                    {
                        long h = ModelCommon.BoxHash(o.ObjectType, bb, tol);
                        Guid first;
                        if (dupSeen.TryGetValue(h, out first))
                        {
                            var s = iss("duplicates", "warn"); s.Count++;
                            if (dupGroups.Add(h) && s.Ex.Count < ex) s.Ex.Add(new { handle = ctx.Handles.For(o.Id), same_as = ctx.Handles.For(first) });
                        }
                        else dupSeen[h] = o.Id;
                    }
                }
                if (cRole)
                {
                    string role = o.Attributes.GetUserString("ai.role");
                    if (!string.IsNullOrEmpty(role))
                    {
                        string expect;
                        if (!layerRole.TryGetValue(li, out expect)) { expect = null; layerRole[li] = null; }
                        var layer = doc.Layers[li];
                        string lrole = layer.GetUserString("ai.role");
                        bool fits;
                        if (!string.IsNullOrEmpty(lrole)) fits = string.Equals(lrole, role, StringComparison.OrdinalIgnoreCase);
                        else
                        {
                            string stem = role.Trim().ToLowerInvariant();
                            if (stem.Length > 3 && stem.EndsWith("s")) stem = stem.Substring(0, stem.Length - 1);
                            fits = layer.FullPath.ToLowerInvariant().Contains(stem);
                        }
                        if (!fits)
                        {
                            var s = iss("role_layer", "warn"); s.Count++;
                            if (s.Ex.Count < ex) s.Ex.Add(new { handle = ctx.Handles.For(o.Id), role = role, layer = layer.FullPath });
                        }
                    }
                }
            }

            // ---- whole-model checks
            var globalInfo = new Dictionary<string, object>();
            if (whole && !timedOut)
            {
                if (checks.Contains("empty_layers"))
                {
                    var counts = new Dictionary<int, int>();
                    foreach (var o in ObjectQuery.All(doc)) { int c; int li = o.Attributes.LayerIndex; counts.TryGetValue(li, out c); counts[li] = c + 1; }
                    var withContent = new HashSet<Guid>();
                    for (int i = 0; i < doc.Layers.Count; i++)
                    {
                        var l = doc.Layers[i];
                        if (l.IsDeleted || !counts.ContainsKey(i)) continue;
                        var cur = l; int guard = 0;
                        while (cur != null && guard++ < 64) { withContent.Add(cur.Id); cur = doc.Layers.FindId(cur.ParentLayerId); }
                    }
                    var empty = new List<string>();
                    int cnt = 0;
                    for (int i = 0; i < doc.Layers.Count; i++)
                    {
                        var l = doc.Layers[i];
                        if (l.IsDeleted || withContent.Contains(l.Id)) continue;
                        cnt++;
                        if (empty.Count < 30) empty.Add(l.FullPath);
                    }
                    if (cnt > 0) { var s = iss("empty_layers", "info"); s.Count = cnt; s.Ex.AddRange(empty.Cast<object>()); }
                }
                if (checks.Contains("orphan_blocks"))
                {
                    int dummy;
                    var used = ModelCommon.UsedDefinitions(doc, out dummy);
                    var orphans = new List<string>(); int cnt = 0;
                    for (int i = 0; i < doc.InstanceDefinitions.Count; i++)
                    {
                        var d = doc.InstanceDefinitions[i];
                        if (d == null || d.IsDeleted || used.Contains(d.Index)) continue;
                        cnt++;
                        if (orphans.Count < 15) orphans.Add(d.Name);
                    }
                    if (cnt > 0) { var s = iss("orphan_blocks", "info"); s.Count = cnt; s.Ex.AddRange(orphans.Cast<object>()); }
                }
            }

            string health = issues.Values.Any(i => i.Severity == "error") ? "fail" : issues.Values.Any(i => i.Severity == "warn") ? "warn" : "ok";
            var order = new Dictionary<string, int> { { "error", 0 }, { "warn", 1 }, { "info", 2 } };
            var list = issues.Values.OrderBy(i => order[i.Severity]).ThenByDescending(i => i.Count)
                .Select(i => new { check = i.Check, severity = i.Severity, count = i.Count, examples = i.Ex }).ToList();

            var data = new Dictionary<string, object>();
            data["health"] = health;
            data["scope"] = new { kind = scopeKind, objects = whole ? scanned : objs.Count, scanned = scanned };
            data["summary"] = issues.Values.ToDictionary(i => i.Check, i => i.Count);
            data["issues"] = list;
            if (!whole) data["global_checks"] = "skipped (empty_layers, orphan_blocks run on whole-model scope only)";
            if (timedOut) data["truncated"] = "time budget " + maxMs + " ms hit after " + scanned + " objects; narrow the scope or raise max_ms";
            if (journalTruncated) data["journal_truncated"] = true;
            data["notes"] = "duplicates: count = redundant copies (same type and bbox within tolerance; candidates, verify before deleting)";
            data["elapsed_ms"] = sw.ElapsedMilliseconds;
            return ToolResult.Ok(data);
        }
    }
}

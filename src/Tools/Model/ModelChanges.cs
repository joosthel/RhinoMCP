using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Model
{
    public sealed class ModelChanges : ITool
    {
        public ModelChanges() { ChangeJournal.EnsureInstalled(); }

        public string Name { get { return "model_changes"; } }
        public string Description
        {
            get
            {
                return "What changed in the document since a journal token. Returns counts by kind (add/delete/replace/modify/undelete/layer_*), by actor " +
                       "(agent = made while an MCP tool ran, incl. run_command/run_python; user = anything else) and by layer, plus up to 50 affected handles " +
                       "(latest change per object) and next_token. Start: call with no args to get token, do work, call again with since=<token>. " +
                       "reset:true forgets the journal (token keeps counting). Undo/redo appear as add/delete/replace events. The journal is a bounded ring (131k events); truncated:true means older events were lost.";
            }
        }
        public string InputSchema
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{" +
                       "\"since\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Journal token from an earlier call (default 0 = everything retained)\"}," +
                       "\"limit\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":200,\"default\":50,\"description\":\"Max handles listed\"}," +
                       "\"kind\":{\"enum\":[\"add\",\"delete\",\"replace\",\"modify\",\"undelete\"],\"description\":\"Only list handles whose latest change is this kind\"}," +
                       "\"actor\":{\"enum\":[\"agent\",\"user\"],\"description\":\"Only list handles whose latest change was by this actor\"}," +
                       "\"reset\":{\"type\":\"boolean\",\"default\":false}" +
                       "},\"additionalProperties\":false}";
            }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            ChangeJournal.EnsureInstalled();
            if (!ChangeJournal.Installed)
                return ToolResult.Fail("INTERNAL", "Change journal could not subscribe to document events: " + ChangeJournal.InstallError);
            var a = new ArgReader(args);
            int since = a.Int("since", false, 0, 0, null);
            int limit = a.Int("limit", false, 50, 0, 200);
            string kindF = a.String("kind");
            string actorF = a.String("actor");
            bool reset = a.Bool("reset");
            JKind? kf = null;
            if (kindF != null)
            {
                switch (kindF) { case "add": kf = JKind.Add; break; case "delete": kf = JKind.Delete; break; case "replace": kf = JKind.Replace; break; case "modify": kf = JKind.Modify; break; case "undelete": kf = JKind.Undelete; break; default: a.Error("'kind' must be add|delete|replace|modify|undelete"); break; }
            }
            if (actorF != null && actorF != "agent" && actorF != "user") a.Error("'actor' must be agent or user");
            if (a.HasErrors) return a.ErrorResult();

            bool truncated;
            var ev = ChangeJournal.Since(since, out truncated);
            long token = ChangeJournal.Token;

            var byKind = new Dictionary<string, int>();
            var byLayer = new Dictionary<int, int>();
            int agent = 0, user = 0;
            var layerEvents = new List<object>();
            var last = new Dictionary<Guid, JEntry>();
            var first = new Dictionary<Guid, JKind>();
            var order = new List<Guid>();
            foreach (var e in ev)
            {
                string kn = ChangeJournal.KindName(e.Kind);
                int c; byKind.TryGetValue(kn, out c); byKind[kn] = c + 1;
                if (e.Agent) agent++; else user++;
                if (e.Kind >= JKind.LayerAdded)
                {
                    if (layerEvents.Count < 20) layerEvents.Add(new { ev = kn, layer = LayerName(doc, e.Layer), actor = e.Agent ? "agent" : "user" });
                    continue;
                }
                byLayer.TryGetValue(e.Layer, out c); byLayer[e.Layer] = c + 1;
                if (!last.ContainsKey(e.Id)) { order.Add(e.Id); first[e.Id] = e.Kind; }
                last[e.Id] = e;
            }

            // Net effect per object (latest event wins).
            int netAdd = 0, netDel = 0, netChanged = 0;
            foreach (var kv in last)
            {
                bool created = first[kv.Key] == JKind.Add;
                if (kv.Value.Kind == JKind.Delete) { if (!created) netDel++; }
                else if (created) netAdd++;
                else netChanged++;
            }

            var topLayers = byLayer.OrderByDescending(kv => kv.Value).Take(10)
                .Select(kv => new { layer = LayerName(doc, kv.Key), n = kv.Value }).ToList();

            var items = new List<object>();
            int matched = 0;
            for (int i = order.Count - 1; i >= 0; i--)
            {
                var e = last[order[i]];
                if (kf.HasValue && e.Kind != kf.Value) continue;
                if (actorF != null && (e.Agent ? "agent" : "user") != actorF) continue;
                matched++;
                if (items.Count < limit)
                    items.Add(new { handle = ctx.Handles.For(e.Id), kind = ChangeJournal.KindName(e.Kind), actor = e.Agent ? "agent" : "user", layer = LayerName(doc, e.Layer) });
            }

            var data = new Dictionary<string, object>();
            data["since"] = since;
            data["token"] = token;
            data["next_token"] = token;
            data["events"] = ev.Count;
            data["by_kind"] = byKind;
            data["by_actor"] = new { agent = agent, user = user };
            data["objects_touched"] = last.Count;
            data["net"] = /* created-and-deleted within the window cancel out */ new { added = netAdd, deleted = netDel, changed = netChanged };
            data["top_layers"] = topLayers;
            if (layerEvents.Count > 0) data["layer_events"] = layerEvents;
            data["handles"] = items;
            data["handles_matched"] = matched;
            if (matched > items.Count) data["handles_truncated"] = true;
            if (truncated) { data["truncated"] = true; data["oldest_token"] = ChangeJournal.OldestToken(); }
            data["state_token"] = StateToken.Current;
            data["journal_generation"] = ChangeJournal.Generation;
            if (ChangeJournal.Errors > 0) data["handler_errors"] = ChangeJournal.Errors;
            if (reset) { data["reset_at"] = ChangeJournal.Reset(); }
            return ToolResult.Ok(data);
        }

        public static string LayerName(RhinoDoc doc, int index)
        {
            try
            {
                if (index < 0 || index >= doc.Layers.Count) return "?";
                return doc.Layers[index].FullPath;
            }
            catch (Exception) { return "?"; }
        }
    }
}

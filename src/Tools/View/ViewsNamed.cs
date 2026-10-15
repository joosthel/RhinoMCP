using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;

namespace RhinoMCP.Tools.View
{
    public sealed class ViewsNamedTool : ITool
    {
        public string Name { get { return "views_named"; } }
        public string Description
        {
            get
            {
                return "Named views. action: list | save (active view as name; overwrites an existing name) | restore (applies to the active view, visible to the user) | delete. " +
                       "'name' is required for save/restore/delete. Fails with NOT_FOUND for an unknown name.";
            }
        }
        public string InputSchema
        {
            get
            {
                return @"{""type"":""object"",""properties"":{
""action"":{""type"":""string"",""enum"":[""list"",""save"",""restore"",""delete""]},
""name"":{""type"":""string""}},""required"":[""action""],""additionalProperties"":false}";
            }
        }
        public ToolKind Kind { get { return ToolKind.Mutate; } }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            string action = (a.String("action", true) ?? "").ToLowerInvariant();
            string name = a.String("name");
            if (action != "list" && action != "save" && action != "restore" && action != "delete" && action != "")
                a.Error("'action' must be list, save, restore or delete");
            if ((action == "save" || action == "restore" || action == "delete") && string.IsNullOrWhiteSpace(name))
                a.Error("'name' is required for " + action);
            if (a.HasErrors) return a.ErrorResult();

            var nv = doc.NamedViews;
            if (action == "list") return ToolResult.Ok(new { named_views = Names(doc) });

            if (action == "save")
            {
                var view = doc.Views.ActiveView;
                if (view == null || view is RhinoPageView)
                    return ToolResult.Fail("NO_VIEW", "The active view is not a model view", "Click a model viewport in Rhino and retry.");
                bool replaced = nv.FindByName(name) >= 0;
                int idx = nv.Add(name, view.ActiveViewport.Id);
                if (idx < 0) return ToolResult.Fail("SAVE_FAILED", "Rhino could not save the named view '" + name + "'");
                return ToolResult.Ok(new { saved = name, replaced = replaced, named_views = Names(doc) });
            }

            int i = nv.FindByName(name);
            if (i < 0)
                return ToolResult.Fail("NOT_FOUND", "Named view '" + name + "' does not exist",
                    "Call views_named with action=list. Existing: " + string.Join(", ", Names(doc)));

            if (action == "restore")
            {
                var view = doc.Views.ActiveView;
                if (view == null || view is RhinoPageView)
                    return ToolResult.Fail("NO_VIEW", "The active view is not a model view", "Click a model viewport in Rhino and retry.");
                if (!nv.Restore(i, view.ActiveViewport))
                    return ToolResult.Fail("RESTORE_FAILED", "Rhino could not restore '" + name + "'");
                view.Redraw();
                return ToolResult.Ok(new { restored = name, camera = ViewCommon.CameraInfo(view.ActiveViewport) });
            }

            // delete
            if (!nv.Delete(i)) return ToolResult.Fail("DELETE_FAILED", "Rhino could not delete '" + name + "'");
            return ToolResult.Ok(new { deleted = name, named_views = Names(doc) });
        }

        static List<string> Names(RhinoDoc doc)
        {
            var l = new List<string>();
            for (int i = 0; i < doc.NamedViews.Count; i++) l.Add(doc.NamedViews[i].Name);
            return l;
        }
    }
}

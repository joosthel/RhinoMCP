using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCP
{
    public enum ToolKind { Read, Mutate, Destructive, Script }

    public interface ITool
    {
        string Name { get; }
        string Description { get; }
        string InputSchema { get; }
        ToolKind Kind { get; }
        ToolResult Execute(ToolContext ctx, JObject args);
    }

    /// <summary>Marker: the tool's own Rhino commands manage undo records, so the registry must not open one.</summary>
    public interface IManagesOwnUndo { }

    public sealed class ToolContext
    {
        public RhinoDoc Doc;
        public string Units { get { return Doc == null ? null : Doc.ModelUnitSystem.ToString(); } }
        public double Tol { get { return Doc == null ? 0.0 : Doc.ModelAbsoluteTolerance; } }
        public double AngleTolDeg { get { return Doc == null ? 0.0 : Doc.ModelAngleToleranceDegrees; } }
        public HandleMap Handles { get { return HandleMap.Instance; } }
        public SetStore Sets { get { return SetStore.Instance; } }
        public List<string> Logs = new List<string>();
        public void Log(string s) { if (Logs.Count < 100) Logs.Add(s); }
    }

    /// <summary>Session map between short handles ("o12") and object GUIDs.</summary>
    public sealed class HandleMap
    {
        public static readonly HandleMap Instance = new HandleMap();
        readonly object _lock = new object();
        readonly Dictionary<Guid, string> _byGuid = new Dictionary<Guid, string>();
        readonly Dictionary<string, Guid> _byHandle = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        int _next = 1;

        public string For(Guid id)
        {
            lock (_lock)
            {
                string h;
                if (_byGuid.TryGetValue(id, out h)) return h;
                h = "o" + (_next++);
                _byGuid[id] = h; _byHandle[h] = id;
                return h;
            }
        }
        public bool TryResolve(string handle, out Guid id)
        {
            lock (_lock) return _byHandle.TryGetValue(handle, out id);
        }
        public void Clear() { lock (_lock) { _byGuid.Clear(); _byHandle.Clear(); } }
    }

    public sealed class SetStore
    {
        public static readonly SetStore Instance = new SetStore();
        readonly Dictionary<string, List<Guid>> _sets = new Dictionary<string, List<Guid>>();
        public void Save(string name, IEnumerable<Guid> ids) { lock (_sets) _sets[name] = ids.ToList(); }
        public bool TryGet(string name, out List<Guid> ids) { lock (_sets) return _sets.TryGetValue(name, out ids); }
        public bool Delete(string name) { lock (_sets) return _sets.Remove(name); }
        public string[] Names() { lock (_sets) return _sets.Keys.OrderBy(k => k).ToArray(); }
    }

    public sealed class ToolResult
    {
        public bool IsOk;
        public object Data;
        public string Code, Message, Guidance;
        public object Details;
        public List<string> Warnings = new List<string>();
        public string NextCursor;
        public List<KeyValuePair<string, string>> Images = new List<KeyValuePair<string, string>>();
        public string Units;
        public long StateToken = -1;

        public static ToolResult Ok(object data) { return new ToolResult { IsOk = true, Data = data }; }
        public static ToolResult Fail(string code, string message, string guidance = null, object details = null)
        {
            return new ToolResult { IsOk = false, Code = code, Message = message, Guidance = guidance, Details = details };
        }
        public ToolResult Warn(string w) { Warnings.Add(w); return this; }
        public ToolResult AddImage(string mime, string base64) { Images.Add(new KeyValuePair<string, string>(mime, base64)); return this; }

        public JObject ToJson()
        {
            var o = new JObject();
            o["ok"] = IsOk;
            if (IsOk)
            {
                if (Data != null) o["data"] = JToken.FromObject(Data);
                if (Units != null) o["units"] = Units;
                if (StateToken >= 0) o["state_token"] = StateToken;
                if (Warnings.Count > 0) o["warnings"] = new JArray(Warnings);
                o["next_cursor"] = NextCursor == null ? JValue.CreateNull() : (JToken)NextCursor;
            }
            else
            {
                var e = new JObject();
                e["code"] = Code; e["message"] = Message;
                if (Guidance != null) e["guidance"] = Guidance;
                if (Details != null) e["details"] = JToken.FromObject(Details);
                o["error"] = e;
            }
            return o;
        }

        /// <summary>MCP tools/call result: one compact text block plus image blocks.</summary>
        public JObject ToMcp()
        {
            var content = new JArray();
            content.Add(new JObject { { "type", "text" }, { "text", ToJson().ToString(Formatting.None) } });
            foreach (var img in Images)
                content.Add(new JObject { { "type", "image" }, { "data", img.Value }, { "mimeType", img.Key } });
            var r = new JObject { { "content", content } };
            if (!IsOk) r["isError"] = true;
            return r;
        }
    }

    /// <summary>Reads tool arguments and collects ALL errors so the caller can fix everything in one round trip.</summary>
    public sealed class ArgReader
    {
        readonly JObject _a;
        readonly List<string> _errors = new List<string>();
        public ArgReader(JObject args) { _a = args ?? new JObject(); }
        public bool HasErrors { get { return _errors.Count > 0; } }
        public void Error(string msg) { _errors.Add(msg); }
        public ToolResult ErrorResult()
        {
            return ToolResult.Fail("INVALID_ARGS", string.Join("; ", _errors),
                "Fix all listed arguments and call again. See the tool's inputSchema.", new { errors = _errors });
        }
        public bool Has(string name) { JToken t; return _a.TryGetValue(name, out t) && t.Type != JTokenType.Null; }

        public string String(string name, bool required = false, string def = null)
        {
            JToken t;
            if (!_a.TryGetValue(name, out t) || t.Type == JTokenType.Null)
            {
                if (required) _errors.Add("'" + name + "' is required (string)");
                return def;
            }
            if (t.Type != JTokenType.String) { _errors.Add("'" + name + "' must be a string"); return def; }
            return (string)t;
        }
        public int Int(string name, bool required = false, int def = 0, int? min = null, int? max = null)
        {
            JToken t;
            if (!_a.TryGetValue(name, out t) || t.Type == JTokenType.Null)
            {
                if (required) _errors.Add("'" + name + "' is required (integer)");
                return def;
            }
            if (t.Type != JTokenType.Integer) { _errors.Add("'" + name + "' must be an integer"); return def; }
            long v = (long)t;
            if ((min.HasValue && v < min.Value) || (max.HasValue && v > max.Value))
            { _errors.Add("'" + name + "' out of range [" + min + ".." + max + "]"); return def; }
            return (int)v;
        }
        public double Double(string name, bool required = false, double def = 0)
        {
            JToken t;
            if (!_a.TryGetValue(name, out t) || t.Type == JTokenType.Null)
            {
                if (required) _errors.Add("'" + name + "' is required (number)");
                return def;
            }
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) { _errors.Add("'" + name + "' must be a number"); return def; }
            return (double)t;
        }
        public bool Bool(string name, bool def = false)
        {
            JToken t;
            if (!_a.TryGetValue(name, out t) || t.Type == JTokenType.Null) return def;
            if (t.Type != JTokenType.Boolean) { _errors.Add("'" + name + "' must be a boolean"); return def; }
            return (bool)t;
        }
        public Point3d Point3d(string name, bool required = false)
        {
            JToken t;
            if (!_a.TryGetValue(name, out t) || t.Type == JTokenType.Null)
            {
                if (required) _errors.Add("'" + name + "' is required ([x,y,z])");
                return Rhino.Geometry.Point3d.Unset;
            }
            var arr = t as JArray;
            if (arr == null || arr.Count != 3 || arr.Any(x => x.Type != JTokenType.Integer && x.Type != JTokenType.Float))
            { _errors.Add("'" + name + "' must be [x,y,z] numbers"); return Rhino.Geometry.Point3d.Unset; }
            return new Point3d((double)arr[0], (double)arr[1], (double)arr[2]);
        }
        public T Enum<T>(string name, bool required = false, T def = default(T)) where T : struct
        {
            string s = String(name, required, null);
            if (s == null) return def;
            T v;
            if (System.Enum.TryParse<T>(s, true, out v) && System.Enum.IsDefined(typeof(T), v)) return v;
            _errors.Add("'" + name + "' must be one of: " + string.Join(", ", System.Enum.GetNames(typeof(T))));
            return def;
        }

        /// <summary>
        /// Resolves a target: handle "o12" | GUID string | {set:name} | {query:{layer,type,name}} | array of those.
        /// Returns an empty list (and records an error) on failure.
        /// </summary>
        public List<RhinoObject> Target(string name, ToolContext ctx, bool required = true)
        {
            var res = new List<RhinoObject>();
            JToken t;
            if (!_a.TryGetValue(name, out t) || t.Type == JTokenType.Null)
            {
                if (required) _errors.Add("'" + name + "' is required (handle, GUID, {set} or {query})");
                return res;
            }
            if (ctx.Doc == null) { _errors.Add("No active document"); return res; }
            var seen = new HashSet<Guid>();
            var items = t as JArray;
            if (items != null) foreach (var it in items) ResolveOne(name, it, ctx, res, seen);
            else ResolveOne(name, t, ctx, res, seen);
            return res;
        }

        void ResolveOne(string name, JToken t, ToolContext ctx, List<RhinoObject> res, HashSet<Guid> seen)
        {
            var doc = ctx.Doc;
            if (t.Type == JTokenType.String)
            {
                string s = (string)t;
                Guid id;
                if (!HandleMap.Instance.TryResolve(s, out id) && !Guid.TryParse(s, out id))
                { _errors.Add("'" + name + "': '" + s + "' is neither a known handle (e.g. o12) nor a GUID"); return; }
                var obj = doc.Objects.FindId(id);
                if (obj == null) { _errors.Add("'" + name + "': object '" + s + "' no longer exists"); return; }
                if (seen.Add(id)) res.Add(obj);
                return;
            }
            var o = t as JObject;
            if (o == null) { _errors.Add("'" + name + "' has an unsupported form"); return; }
            JToken setTok, qTok;
            if (o.TryGetValue("set", out setTok))
            {
                List<Guid> ids;
                if (!SetStore.Instance.TryGet((string)setTok, out ids))
                { _errors.Add("'" + name + "': set '" + (string)setTok + "' not found"); return; }
                foreach (var id in ids) { var obj = doc.Objects.FindId(id); if (obj != null && seen.Add(id)) res.Add(obj); }
                return;
            }
            if (o.TryGetValue("query", out qTok) && qTok is JObject)
            {
                var q = (JObject)qTok;
                string layer = (string)q["layer"], type = (string)q["type"], nm = (string)q["name"];
                ObjectType filter = ObjectType.AnyObject;
                if (type != null)
                {
                    ObjectType ot;
                    if (!System.Enum.TryParse<ObjectType>(type, true, out ot))
                    { _errors.Add("'" + name + "': query.type '" + type + "' unknown. Use e.g. Curve, Brep, Surface, Mesh, Point, Extrusion, Annotation"); return; }
                    filter = ot;
                }
                Regex rx = nm == null ? null : new Regex("^" + Regex.Escape(nm).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
                var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = false, ObjectTypeFilter = filter };
                foreach (var obj in doc.Objects.GetObjectList(settings))
                {
                    if (layer != null)
                    {
                        var l = doc.Layers[obj.Attributes.LayerIndex];
                        if (!string.Equals(l.FullPath, layer, StringComparison.OrdinalIgnoreCase) && !string.Equals(l.Name, layer, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    if (rx != null && !rx.IsMatch(obj.Attributes.Name ?? "")) continue;
                    if (seen.Add(obj.Id)) res.Add(obj);
                }
                return;
            }
            _errors.Add("'" + name + "' object must contain 'set' or 'query'");
        }
    }
}

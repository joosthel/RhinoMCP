using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RhinoMCP
{
    /// <summary>
    /// Top-level argument validation against a tool's inputSchema, run by <see cref="ToolRegistry"/> before Execute.
    /// Checks: unknown names (only when additionalProperties is false), required keys, basic JSON types
    /// (string, number, integer, boolean, array, object). All problems are collected. Nested structures are left to the tool.
    /// </summary>
    public static class ArgValidator
    {
        public sealed class Result
        {
            public List<string> Errors = new List<string>();
            public List<string> Unknown = new List<string>();
            public List<string> Valid = new List<string>();
            public bool Ok { get { return Errors.Count == 0; } }
        }

        public static Result Validate(JObject schema, JObject args)
        {
            var r = new Result();
            if (schema == null) return r;
            args = args ?? new JObject();
            var props = schema["properties"] as JObject;
            if (props != null) r.Valid.AddRange(props.Properties().Select(p => p.Name));

            JToken ap = schema["additionalProperties"];
            bool strict = ap != null && ap.Type == JTokenType.Boolean && !(bool)ap;
            if (strict)
            {
                foreach (var p in args.Properties())
                {
                    if (r.Valid.Contains(p.Name)) continue;
                    r.Unknown.Add(p.Name);
                    string close = Closest(p.Name, r.Valid);
                    r.Errors.Add("unknown argument '" + p.Name + "'" + (close != null ? " (did you mean '" + close + "'?)" : "") +
                                 ". Valid arguments: " + (r.Valid.Count == 0 ? "(none)" : string.Join(", ", r.Valid)));
                }
            }

            var req = schema["required"] as JArray;
            if (req != null)
                foreach (var t in req)
                {
                    string name = (string)t;
                    JToken v;
                    if (name == null) continue;
                    if (!args.TryGetValue(name, out v) || v.Type == JTokenType.Null)
                        r.Errors.Add("'" + name + "' is required" + TypeHint(props, name));
                }

            if (props != null)
                foreach (var p in args.Properties())
                {
                    var ps = props[p.Name] as JObject;
                    if (ps == null || p.Value.Type == JTokenType.Null) continue;
                    var types = TypesOf(ps);
                    if (types == null) continue;
                    if (!types.Any(t => IsType(p.Value, t)))
                        r.Errors.Add("'" + p.Name + "' must be " + string.Join(" or ", types) + " (got " + Describe(p.Value) + ")");
                }
            return r;
        }

        static string TypeHint(JObject props, string name)
        {
            var ps = props == null ? null : props[name] as JObject;
            var types = ps == null ? null : TypesOf(ps);
            return types == null ? "" : " (" + string.Join(" or ", types) + ")";
        }

        static List<string> TypesOf(JObject ps)
        {
            var t = ps["type"];
            if (t == null) return null;   // $ref, oneOf, enum only, ...: not checked here
            var res = new List<string>();
            if (t.Type == JTokenType.String) res.Add((string)t);
            else if (t is JArray) { foreach (var x in (JArray)t) if (x.Type == JTokenType.String) res.Add((string)x); }
            res = res.Where(x => x == "string" || x == "number" || x == "integer" || x == "boolean" || x == "array" || x == "object" || x == "null").ToList();
            return res.Count == 0 ? null : res;
        }

        static bool IsType(JToken v, string type)
        {
            switch (type)
            {
                case "string": return v.Type == JTokenType.String;
                case "number": return v.Type == JTokenType.Integer || v.Type == JTokenType.Float;
                case "integer":
                    if (v.Type == JTokenType.Integer) return true;
                    if (v.Type == JTokenType.Float) { double d = (double)v; return d == Math.Floor(d) && !double.IsInfinity(d); }
                    return false;
                case "boolean": return v.Type == JTokenType.Boolean;
                case "array": return v.Type == JTokenType.Array;
                case "object": return v.Type == JTokenType.Object;
                case "null": return v.Type == JTokenType.Null;
            }
            return true;
        }

        static string Describe(JToken v)
        {
            switch (v.Type)
            {
                case JTokenType.Integer: case JTokenType.Float: return "number";
                case JTokenType.String: return "string";
                case JTokenType.Boolean: return "boolean";
                case JTokenType.Array: return "array";
                case JTokenType.Object: return "object";
                default: return v.Type.ToString().ToLowerInvariant();
            }
        }

        public static string Closest(string name, IEnumerable<string> valid)
        {
            string best = null; int bd = int.MaxValue;
            string n = name.ToLowerInvariant();
            foreach (var v in valid)
            {
                string l = v.ToLowerInvariant();
                int d = Lev(n, l);
                if (l.Length > 2 && n.Length > 2 && (l.Contains(n) || n.Contains(l))) d = Math.Min(d, 1);
                if (d < bd) { bd = d; best = v; }
            }
            return best != null && bd <= Math.Max(2, name.Length / 3) ? best : null;
        }

        static int Lev(string a, string b)
        {
            if (a.Length > 64) a = a.Substring(0, 64);
            if (b.Length > 64) b = b.Substring(0, 64);
            var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }
}

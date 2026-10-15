using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RhinoMCP.Tools.Script
{
    /// <summary>Find Rhino command names (English) by substring / fuzzy match, with advice on how to call them via run_command.</summary>
    public sealed class CommandsSearch : ITool
    {
        public string Name { get { return "commands_search"; } }
        public string Description
        {
            get
            {
                return "Search Rhino command names (English, built-in and loaded plug-ins) by substring or fuzzy match, best match first. " +
                       "For each hit: name, whether the scripted dash form (-_Name) exists and is advised (the plain form opens a dialog), " +
                       "and whether run_command refuses it (blocked: modal window that would freeze Rhino). Use before run_command when " +
                       "unsure of a command name. Returns at most 'limit' hits (default 20) plus the total.";
            }
        }
        public string InputSchema
        {
            get { return "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"Substring or approximate name, e.g. box, offsetsrf\"},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100,\"default\":20}},\"required\":[\"query\"],\"additionalProperties\":false}"; }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        static string[] _cache;
        static DateTime _cacheTime;

        static string[] AllNames()
        {
            if (_cache == null || (DateTime.UtcNow - _cacheTime).TotalSeconds > 30)
            {
                _cache = Rhino.Commands.Command.GetCommandNames(true, true) ?? new string[0];
                _cacheTime = DateTime.UtcNow;
            }
            return _cache;
        }

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var a = new ArgReader(args);
            string q = a.String("query", true);
            int limit = a.Int("limit", false, 20, 1, 100);
            if (a.HasErrors) return a.ErrorResult();
            q = (q ?? "").Trim().TrimStart('-', '_', '\'', '!').ToLowerInvariant();
            if (q.Length == 0) return ToolResult.Fail("INVALID_ARGS", "'query' is empty", "Pass part of a command name, e.g. \"box\".");

            var names = AllNames();
            var scored = new List<KeyValuePair<int, string>>();
            foreach (string n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                int s = Score(n.ToLowerInvariant(), q);
                if (s > 0) scored.Add(new KeyValuePair<int, string>(s, n));
            }
            var ordered = scored.OrderByDescending(p => p.Key).ThenBy(p => p.Value.Length).ThenBy(p => p.Value, StringComparer.OrdinalIgnoreCase).ToList();
            var hits = ordered.Take(limit).Select(p => Describe(p.Value, p.Key)).ToList();
            return ToolResult.Ok(new { query = q, total_commands = names.Length, total_matches = ordered.Count, returned = hits.Count, matches = hits });
        }

        static object Describe(string name, int score)
        {
            bool always = RunCommand.AlwaysModal.Contains(name);
            bool unlessDash = RunCommand.ModalUnlessDash.Contains(name);
            bool dashExists = false;
            try { dashExists = Rhino.Commands.Command.IsCommand("-" + name); } catch (Exception) { }
            string note = null;
            if (always) note = "Blocked by run_command: opens a modal window that freezes Rhino. Use a dedicated tool.";
            else if (unlessDash) note = "Plain form opens a dialog; run_command refuses it. Use -_" + name + " with all options inline.";
            else if (dashExists) note = "A dash form exists (-_" + name + "): prefer it with all options inline to avoid dialogs.";
            return new
            {
                name = name,
                score = score,
                dash_form = dashExists ? "-_" + name : null,
                dash_advised = !always && (unlessDash || dashExists),
                blocked_in_run_command = always,
                blocked_unless_dash = unlessDash,
                note = note
            };
        }

        static int Score(string name, string q)
        {
            if (name == q) return 1000;
            if (name.StartsWith(q, StringComparison.Ordinal)) return 800 - Math.Min(name.Length - q.Length, 100);
            int idx = name.IndexOf(q, StringComparison.Ordinal);
            if (idx >= 0) return 600 - Math.Min(idx * 5 + (name.Length - q.Length), 200);
            if (q.Length >= 3 && IsSubsequence(q, name)) return 200 - Math.Min(name.Length - q.Length, 100) + 1;
            if (q.Length >= 4)
            {
                int d = Levenshtein(name.Length > q.Length + 2 ? name.Substring(0, q.Length + 2) : name, q);
                if (d <= 2) return 100 - d * 20;
            }
            return 0;
        }

        static bool IsSubsequence(string q, string s)
        {
            int i = 0;
            foreach (char c in s) if (i < q.Length && c == q[i]) i++;
            return i == q.Length;
        }

        static int Levenshtein(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
    }
}

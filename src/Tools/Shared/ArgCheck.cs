using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RhinoMCP.Tools.Shared
{
    /// <summary>
    /// Tool-side strictness for tools that must not silently ignore arguments even on a Core without schema validation
    /// (Core 0.2.0 does this for every tool before Execute).
    /// </summary>
    public static class ArgCheck
    {
        /// <summary>Records an error for each top-level key not in <paramref name="valid"/>, with a "did you mean" and optional hint.</summary>
        public static void RejectUnknown(JObject args, string[] valid, ArgReader a, Func<string, string> hint = null)
        {
            if (args == null) return;
            foreach (var p in args.Properties())
            {
                if (valid.Contains(p.Name)) continue;
                string msg = "unknown argument '" + p.Name + "'";
                string h = hint == null ? null : hint(p.Name);
                string close = Closest(p.Name, valid);
                if (h != null) msg += " (" + h + ")";
                else if (close != null) msg += " (did you mean '" + close + "'?)";
                a.Error(msg + ". Valid arguments: " + string.Join(", ", valid));
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
                if (l.Contains(n) || n.Contains(l)) d = Math.Min(d, 1);
                if (d < bd) { bd = d; best = v; }
            }
            return best != null && bd <= Math.Max(2, name.Length / 3) ? best : null;
        }

        static int Lev(string a, string b)
        {
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

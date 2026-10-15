using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCP.Tools.Shared
{
    /// <summary>
    /// Resource limits (config.json "limits", read here, Core Config is untouched). Defaults suit Rhino 7 Mac under Rosetta.
    /// </summary>
    public sealed class Limits
    {
        public int MaxSurfaceControlPoints = 2500;      // 50 x 50
        public int MaxBatchControlPoints = 20000;
        public int MaxSubdLevels = 2;
        public int MaxSubdFaces = 50000;
        public int MaxMeshFaces = 500000;
        public long MaxCapturePixels = 1600L * 1200L;
        public int Make2dMaxInputFaces = 20000;
        public int BooleanMaxFaceCount = 5000;
        public int ClashMaxPairs = 5000;
        public double WarnRssMb = 4000;
        public double BlockRssMb = 12000;
        public int WarnCallMs = 10000;
        public double RenderFacesWarn = 2000000;        // total cached render-mesh faces that count as "very dense"

        public JObject ToJson()
        {
            return new JObject
            {
                { "max_surface_control_points", MaxSurfaceControlPoints },
                { "max_batch_control_points", MaxBatchControlPoints },
                { "max_subd_levels", MaxSubdLevels },
                { "max_subd_faces", MaxSubdFaces },
                { "max_mesh_faces", MaxMeshFaces },
                { "max_capture_pixels", MaxCapturePixels },
                { "make2d_max_input_faces", Make2dMaxInputFaces },
                { "boolean_max_face_count", BooleanMaxFaceCount },
                { "clash_max_pairs", ClashMaxPairs },
                { "warn_rss_mb", WarnRssMb },
                { "block_rss_mb", BlockRssMb },
                { "warn_call_ms", WarnCallMs }
            };
        }
    }

    /// <summary>One entry of the "heaviest objects" list.</summary>
    public sealed class HeavyObject
    {
        public Guid Id; public string Type; public string Layer;
        public long ControlPoints, Faces, RenderFaces; public int MaxSurfaceCp;
        public long Weight { get { return ControlPoints + Faces; } }
    }

    /// <summary>Document complexity totals.</summary>
    public sealed class Complexity
    {
        public int Objects, Scanned;
        public long ControlPoints, MeshVertices, MeshFaces, SubdFaces, BrepFaces;
        public int RenderMeshObjects; public long RenderMeshFaces;
        public int OverCpObjects;                 // objects with a surface above max_surface_control_points
        public List<HeavyObject> Top = new List<HeavyObject>();
        public List<Guid> OverCp = new List<Guid>();
        public bool Partial; public int Ms;
        public long Epoch; public int Count; public DateTime At;
    }

    /// <summary>Process / system stats, limits, document complexity, pre-flight and post-call accounting for heavy tools.</summary>
    public static class Resources
    {
        // ------------------------------------------------------------------ limits
        static Limits _limits; static DateTime _limitsAt = DateTime.MinValue; static DateTime _limitsFileStamp;

        public static Limits Limits
        {
            get
            {
                var now = DateTime.UtcNow;
                if (_limits != null && (now - _limitsAt).TotalSeconds < 5) return _limits;
                _limitsAt = now;
                string path = null;
                try { path = RhinoMCP.Config.ConfigPath; } catch (Exception) { }
                DateTime stamp = DateTime.MinValue;
                try { if (path != null && File.Exists(path)) stamp = File.GetLastWriteTimeUtc(path); } catch (Exception) { }
                if (_limits != null && stamp == _limitsFileStamp) return _limits;
                _limitsFileStamp = stamp;
                var l = new Limits();
                double ram = TotalRamMb();
                if (ram > 0) l.BlockRssMb = Math.Min(l.BlockRssMb, Math.Round(ram * 0.4));
                try
                {
                    if (path != null && File.Exists(path))
                    {
                        var root = JObject.Parse(File.ReadAllText(path));
                        var o = root["limits"] as JObject;
                        if (o != null) Apply(l, o);
                    }
                }
                catch (Exception) { }
                _limits = l;
                return l;
            }
        }

        static void Apply(Limits l, JObject o)
        {
            l.MaxSurfaceControlPoints = I(o, "max_surface_control_points", l.MaxSurfaceControlPoints);
            l.MaxBatchControlPoints = I(o, "max_batch_control_points", l.MaxBatchControlPoints);
            l.MaxSubdLevels = I(o, "max_subd_levels", l.MaxSubdLevels);
            l.MaxSubdFaces = I(o, "max_subd_faces", l.MaxSubdFaces);
            l.MaxMeshFaces = I(o, "max_mesh_faces", l.MaxMeshFaces);
            l.Make2dMaxInputFaces = I(o, "make2d_max_input_faces", l.Make2dMaxInputFaces);
            l.BooleanMaxFaceCount = I(o, "boolean_max_face_count", l.BooleanMaxFaceCount);
            l.ClashMaxPairs = I(o, "clash_max_pairs", l.ClashMaxPairs);
            l.WarnCallMs = I(o, "warn_call_ms", l.WarnCallMs);
            l.WarnRssMb = D(o, "warn_rss_mb", l.WarnRssMb);
            l.BlockRssMb = D(o, "block_rss_mb", l.BlockRssMb);
            JToken t;
            if (o.TryGetValue("max_capture_pixels", out t))
            {
                var arr = t as JArray;
                if (arr != null && arr.Count == 2 && IsNum(arr[0]) && IsNum(arr[1])) l.MaxCapturePixels = (long)((double)arr[0] * (double)arr[1]);
                else if (IsNum(t) && (double)t > 0) l.MaxCapturePixels = (long)(double)t;
            }
        }
        static bool IsNum(JToken t) { return t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float); }
        static int I(JObject o, string k, int def) { JToken t; return o.TryGetValue(k, out t) && IsNum(t) && (double)t > 0 ? (int)(double)t : def; }
        static double D(JObject o, string k, double def) { JToken t; return o.TryGetValue(k, out t) && IsNum(t) && (double)t > 0 ? (double)t : def; }

        // ------------------------------------------------------------------ process / system stats
        static DateTime _rssAt = DateTime.MinValue; static double _rss, _priv;

        /// <summary>Resident set size of this (Rhino) process in MB, cached 2 s. Managed first, `ps` as fallback.</summary>
        public static double RssMb()
        {
            RefreshProc();
            return _rss;
        }
        public static double PrivateMb() { RefreshProc(); return _priv; }

        static void RefreshProc()
        {
            if ((DateTime.UtcNow - _rssAt).TotalSeconds < 2 && _rss > 0) return;
            double rss = 0, priv = 0;
            try
            {
                using (var p = Process.GetCurrentProcess())
                {
                    rss = p.WorkingSet64 / 1048576.0;
                    priv = p.PrivateMemorySize64 / 1048576.0;
                }
            }
            catch (Exception) { }
            if (rss <= 0)
            {
                string o = Run("/bin/ps", "-o rss= -p " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture), 2000);
                double kb;
                if (o != null && double.TryParse(o.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out kb)) rss = kb / 1024.0;
            }
            _rss = Math.Round(rss, 1); _priv = Math.Round(priv, 1); _rssAt = DateTime.UtcNow;
        }

        static string Run(string exe, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } return null; }
                    return p.ExitCode == 0 ? o : null;
                }
            }
            catch (Exception) { return null; }
        }

        static bool _staticDone; static bool _rosetta; static double _ramMb; static int _physCores;
        static void LoadStatic()
        {
            if (_staticDone) return;
            _staticDone = true;
            string t = Run("/usr/sbin/sysctl", "-n sysctl.proc_translated", 2000);
            _rosetta = t != null && t.Trim() == "1";
            double mem;
            string m = Run("/usr/sbin/sysctl", "-n hw.memsize", 2000);
            if (m != null && double.TryParse(m.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out mem)) _ramMb = Math.Round(mem / 1048576.0);
            int pc;
            string c = Run("/usr/sbin/sysctl", "-n hw.physicalcpu", 2000);
            if (c != null && int.TryParse(c.Trim(), out pc)) _physCores = pc;
        }
        public static bool Rosetta() { LoadStatic(); return _rosetta; }
        public static double TotalRamMb() { LoadStatic(); return _ramMb; }
        public static int PhysicalCores() { LoadStatic(); return _physCores; }
        public static int LogicalCores() { return Environment.ProcessorCount; }

        static DateTime _freeAt = DateTime.MinValue; static double _freeMb = -1;
        /// <summary>Free + reclaimable (inactive, speculative, purgeable) system memory in MB (vm_stat), cached 10 s. -1 if unknown.</summary>
        public static double FreeSystemMb()
        {
            if ((DateTime.UtcNow - _freeAt).TotalSeconds < 10) return _freeMb;
            _freeAt = DateTime.UtcNow; _freeMb = -1;
            string o = Run("/usr/bin/vm_stat", "", 3000);
            if (o == null) return _freeMb;
            double page = 4096, pages = 0;
            foreach (var line in o.Split('\n'))
            {
                if (line.Contains("page size of"))
                {
                    int i = line.IndexOf("page size of", StringComparison.Ordinal) + 12;
                    var digits = new string(line.Substring(i).Trim().TakeWhile(char.IsDigit).ToArray());
                    double ps; if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out ps) && ps > 0) page = ps;
                    continue;
                }
                if (line.StartsWith("Pages free") || line.StartsWith("Pages inactive") || line.StartsWith("Pages speculative") || line.StartsWith("Pages purgeable"))
                {
                    int k = line.IndexOf(':');
                    if (k < 0) continue;
                    double v; if (double.TryParse(line.Substring(k + 1).Trim().TrimEnd('.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) pages += v;
                }
            }
            _freeMb = Math.Round(pages * page / 1048576.0);
            return _freeMb;
        }

        // ------------------------------------------------------------------ document complexity
        static Complexity _cx;

        /// <summary>Totals over the document, computed with a time budget and cached by change-journal epoch (else object count + 5 s).</summary>
        public static Complexity DocComplexity(RhinoDoc doc, bool refresh = false, int budgetMs = 2000)
        {
            long epoch = -1;
            try { Model.ChangeJournal.EnsureInstalled(); epoch = Model.ChangeJournal.Epoch; } catch (Exception) { }
            int count = doc.Objects.Count;
            var c = _cx;
            if (!refresh && c != null && c.Epoch == epoch && c.Count == count && (epoch >= 0 || (DateTime.UtcNow - c.At).TotalSeconds < 5))
                return c;
            c = Scan(doc, budgetMs);
            c.Epoch = epoch; c.Count = count; c.At = DateTime.UtcNow;
            _cx = c;
            return c;
        }

        /// <summary>The last computed totals without scanning (null if none).</summary>
        public static Complexity CachedComplexity { get { return _cx; } }

        static int SurfaceCp(Surface s)
        {
            var ns = s as NurbsSurface;
            if (ns != null) return ns.Points.CountU * ns.Points.CountV;
            if (s is PlaneSurface) return 4;
            return 16;
        }

        static Complexity Scan(RhinoDoc doc, int budgetMs)
        {
            var sw = Stopwatch.StartNew();
            var c = new Complexity();
            int limitCp = Limits.MaxSurfaceControlPoints;
            var all = new List<HeavyObject>();
            var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true };
            int n = 0;
            foreach (var o in doc.Objects.GetObjectList(settings))
            {
                n++;
                if ((n & 31) == 0 && sw.ElapsedMilliseconds > budgetMs) { c.Partial = true; break; }
                c.Objects++; c.Scanned++;
                var g = o.Geometry;
                if (g == null) continue;
                long cp = 0, faces = 0; int maxSurf = 0; bool meshable = false;
                var brep = g as Brep;
                var mesh = g as Mesh;
                var sd = g as SubD;
                var ext = g as Extrusion;
                if (brep != null)
                {
                    foreach (var s in brep.Surfaces) { int k = SurfaceCp(s); cp += k; if (k > maxSurf) maxSurf = k; }
                    faces = brep.Faces.Count; c.BrepFaces += faces; meshable = true;
                }
                else if (g is Surface)
                {
                    maxSurf = SurfaceCp((Surface)g); cp = maxSurf; faces = 1; meshable = true;
                }
                else if (ext != null) { cp = 8; faces = 6; meshable = true; }
                else if (mesh != null)
                {
                    c.MeshVertices += mesh.Vertices.Count; c.MeshFaces += mesh.Faces.Count; faces = mesh.Faces.Count;
                }
                else if (sd != null) { c.SubdFaces += sd.Faces.Count; faces = sd.Faces.Count; meshable = true; }
                else continue;
                c.ControlPoints += cp;
                long rf = 0;
                if (meshable)
                {
                    try
                    {
                        var ms = o.GetMeshes(MeshType.Render);
                        if (ms != null && ms.Length > 0) { c.RenderMeshObjects++; foreach (var m in ms) if (m != null) rf += m.Faces.Count; c.RenderMeshFaces += rf; }
                    }
                    catch (Exception) { }
                }
                if (maxSurf > limitCp) { c.OverCpObjects++; if (c.OverCp.Count < 50) c.OverCp.Add(o.Id); }
                if (cp + faces > 50) all.Add(new HeavyObject { Id = o.Id, Type = o.ObjectType.ToString(), Layer = null, ControlPoints = cp, Faces = faces, RenderFaces = rf, MaxSurfaceCp = maxSurf });
            }
            c.Objects = doc.Objects.Count;
            foreach (var h in all.OrderByDescending(x => x.Weight + x.RenderFaces / 4).Take(10))
            {
                try { var ro = doc.Objects.FindId(h.Id); if (ro != null) { var l = doc.Layers[ro.Attributes.LayerIndex]; h.Layer = l.FullPath; } } catch (Exception) { }
                c.Top.Add(h);
            }
            c.Ms = (int)sw.ElapsedMilliseconds;
            return c;
        }

        // ------------------------------------------------------------------ guards
        /// <summary>RESOURCE_LIMIT result with numbers and concrete guidance.</summary>
        public static ToolResult LimitFail(string limitKey, double estimated, double limit, string message, string guidance, bool forceable)
        {
            return ToolResult.Fail("RESOURCE_LIMIT", message,
                guidance + (forceable ? " To override on purpose, pass force:true (Rhino may become slow)." : ""),
                new { limit = limitKey, estimated = estimated, max = limit, force_allowed = forceable, config = "limits." + limitKey + " in ~/Library/Application Support/RhinoMCP/config.json" });
        }

        /// <summary>Never forceable: refuses heavy work while Rhino's memory is above block_rss_mb.</summary>
        public static ToolResult CheckRss(string tool)
        {
            double rss = RssMb(), block = Limits.BlockRssMb;
            if (rss >= block)
                return ToolResult.Fail("RESOURCE_LIMIT", tool + " refused: Rhino uses " + Math.Round(rss) + " MB (block limit " + Math.Round(block) + " MB)",
                    "Free memory first: delete or hide heavy objects, run performance_mode on, save the file and restart Rhino if it stays high. Check system_status for the heaviest objects. This limit cannot be overridden with force.",
                    new { limit = "block_rss_mb", estimated = rss, max = block, force_allowed = false });
            return null;
        }

        /// <summary>Starts timing/memory accounting of one heavy call.</summary>
        public static Guard Begin(string tool) { return new Guard(tool); }

        public sealed class Guard
        {
            readonly string _tool; readonly Stopwatch _sw = Stopwatch.StartNew(); readonly double _rss0;
            public Guard(string tool) { _tool = tool; _rss0 = RssMb(); }

            /// <summary>Attaches `resources` (and a warning) to an ok result only when notable. Returns r.</summary>
            public ToolResult Finish(ToolResult r)
            {
                try
                {
                    if (r == null || !r.IsOk) return r;
                    var lim = Limits;
                    _rssAt = DateTime.MinValue;                 // fresh reading after the work
                    double rss = RssMb(), delta = Math.Round(rss - _rss0, 1);
                    long ms = _sw.ElapsedMilliseconds;
                    var reasons = new List<string>();
                    if (delta > 200) reasons.Add("memory grew by " + Math.Round(delta) + " MB");
                    if (ms > lim.WarnCallMs) reasons.Add("call took " + ms + " ms");
                    if (rss > lim.WarnRssMb) reasons.Add("Rhino uses " + Math.Round(rss) + " MB (warn at " + Math.Round(lim.WarnRssMb) + ")");
                    if (reasons.Count == 0) return r;
                    var res = new JObject { { "rss_mb", Math.Round(rss) }, { "rss_delta_mb", Math.Round(delta) }, { "call_ms", ms } };
                    var cx = CachedComplexity;
                    if (cx != null) res["doc_cp_total"] = cx.ControlPoints;
                    if (r.Data != null)
                    {
                        var jt = JToken.FromObject(r.Data);
                        var jo = jt as JObject;
                        if (jo != null) { jo["resources"] = res; r.Data = jo; }
                    }
                    r.Warn(_tool + ": " + string.Join(", ", reasons) + ". Keep Rhino light: smaller control-point counts, lower SubD levels, split heavy work, performance_mode on, or check system_status.");
                }
                catch (Exception) { }
                return r;
            }
        }
    }
}

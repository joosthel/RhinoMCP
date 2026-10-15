using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCP
{
    public static class ToolRegistry
    {
        static volatile Dictionary<string, ITool> _tools = new Dictionary<string, ITool>();
        static readonly Dictionary<string, JObject> _schemaCache = new Dictionary<string, JObject>();
        static Assembly _toolsAssembly;
        static string _toolsPath;

        [ThreadStatic] static string _currentTool;
        [ThreadStatic] static int _toolDepth;
        /// <summary>Name of the MCP tool whose Execute is running on this thread (null outside a tool call). Read by the change journal.</summary>
        public static string CurrentTool { get { return _currentTool; } }
        /// <summary>True while an MCP tool call is executing on this thread; document events raised now were caused by the agent.</summary>
        public static bool InToolCall { get { return _toolDepth > 0; } }

        public static int Count { get { return _tools.Count; } }
        public static string ToolsDll { get { return _toolsPath; } }
        public static IEnumerable<ITool> All { get { return _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal); } }
        public static bool TryGet(string name, out ITool tool) { return _tools.TryGetValue(name, out tool); }

        static ToolRegistry()
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate (object s, ResolveEventArgs e)
            {
                // Tools assemblies loaded from bytes must bind to the already-loaded core.
                var n = new AssemblyName(e.Name);
                return n.Name == "RhinoMCP" ? typeof(ToolRegistry).Assembly : null;
            };
        }

        public sealed class ReloadResult
        {
            public bool Ok; public string Error; public string Dll; public bool Changed;
            public string[] Names = new string[0];
        }

        /// <summary>Rebuilds the registry: core tools plus the newest tools dll. On failure the old set stays active.</summary>
        public static ReloadResult Reload(string explicitDll = null)
        {
            var res = new ReloadResult();
            try
            {
                var map = new Dictionary<string, ITool>();
                var cfg = RhinoMCPPlugin.Config;
                foreach (var t in CoreTools.Create(cfg != null && cfg.DevMode)) Add(map, t, "core");

                string path;
                Assembly asm = explicitDll != null ? ToolsLoader.Load(path = explicitDll) : ToolsLoader.LoadNewest(out path);
                if (asm != null)
                {
                    res.Changed = !object.ReferenceEquals(asm, _toolsAssembly);
                    foreach (var t in Discover(asm)) Add(map, t, Path.GetFileName(path));
                    _toolsAssembly = asm; _toolsPath = path; res.Dll = Path.GetFileName(path);
                }
                lock (_schemaCache) _schemaCache.Clear();
                _tools = map;
                res.Ok = true;
                res.Names = map.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            }
            catch (Exception ex)
            {
                res.Ok = false; res.Error = ex.Message;
                res.Names = _tools.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            }
            return res;
        }

        static void Add(Dictionary<string, ITool> map, ITool t, string source)
        {
            if (string.IsNullOrEmpty(t.Name)) throw new InvalidOperationException("Tool without a name in " + source);
            if (map.ContainsKey(t.Name))
                throw new InvalidOperationException("Duplicate tool name '" + t.Name + "' (" + map[t.Name].GetType().FullName + " and " + t.GetType().FullName + ")");
            map[t.Name] = t;
        }

        static IEnumerable<ITool> Discover(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                var msgs = ex.LoaderExceptions.Where(x => x != null).Select(x => x.Message).Distinct().Take(3);
                throw new InvalidOperationException("Cannot load types from tools assembly: " + string.Join(" | ", msgs));
            }
            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetInterface("RhinoMCP.ITool") == null) continue;
                if (!typeof(ITool).IsAssignableFrom(type))
                    throw new InvalidOperationException(type.FullName + " implements a different copy of ITool (core assembly identity mismatch)");
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;
                yield return (ITool)Activator.CreateInstance(type);
            }
        }

        public static JObject SchemaFor(ITool t)
        {
            lock (_schemaCache)
            {
                JObject s;
                if (!_schemaCache.TryGetValue(t.Name, out s))
                {
                    try { s = Schemas.Parse(t.InputSchema); }
                    catch (Exception) { s = new JObject { { "type", "object" } }; }
                    _schemaCache[t.Name] = s;
                }
                return (JObject)s.DeepClone();
            }
        }

        /// <summary>Runs on the UI thread.</summary>
        public static ToolResult Execute(ITool tool, JObject args)
        {
            var cfg = RhinoMCPPlugin.Config;
            var ctx = new ToolContext { Doc = RhinoDoc.ActiveDoc };
            var bad = ValidateArgs(tool, args);
            if (bad != null) return bad;
            if (tool.Kind == ToolKind.Script && cfg != null && !cfg.AllowScripts)
                return ToolResult.Fail("UNSUPPORTED", "Script tools are disabled (allowScripts=false in config.json)",
                    "Set allowScripts to true in ~/Library/Application Support/RhinoMCP/config.json and restart the server (MCPStop, MCPStart).");
            if (ctx.Doc == null && tool.Kind != ToolKind.Read)
                return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");

            bool mutating = tool.Kind != ToolKind.Read;
            bool wrapUndo = mutating && !(tool is IManagesOwnUndo);
            ToolResult r;
            UndoScope undo = null;
            string prevTool = _currentTool;
            _toolDepth++; _currentTool = tool.Name;
            try
            {
                if (wrapUndo) undo = new UndoScope(ctx.Doc, tool.Name);
                r = tool.Execute(ctx, args ?? new JObject());
                if (r == null) r = ToolResult.Fail("INTERNAL", "Tool '" + tool.Name + "' returned no result");
            }
            catch (Exception ex)
            {
                r = InternalError(ex);
            }
            finally
            {
                if (undo != null)
                {
                    try { undo.Dispose(); } catch (Exception) { }
                }
                _toolDepth--; _currentTool = prevTool;
            }
            if (mutating)
            {
                try { if (ctx.Doc != null) ctx.Doc.Views.Redraw(); } catch (Exception) { }
                r.StateToken = StateToken.Bump();
            }
            else r.StateToken = StateToken.Current;
            if (r.Units == null) r.Units = ctx.Units;
            return r;
        }

        /// <summary>
        /// Checks the arguments against the tool's inputSchema (unknown names when additionalProperties is false, required keys,
        /// basic types). Returns an INVALID_ARGS result listing every problem, or null when the call may proceed.
        /// </summary>
        public static ToolResult ValidateArgs(ITool tool, JObject args)
        {
            ArgValidator.Result v;
            try { v = ArgValidator.Validate(SchemaFor(tool), args); }
            catch (Exception) { return null; }   // a validator fault must never block a call
            if (v.Ok) return null;
            return ToolResult.Fail("INVALID_ARGS", string.Join("; ", v.Errors),
                "Fix all listed arguments and call again. Valid arguments for '" + tool.Name + "': " + (v.Valid.Count == 0 ? "(none)" : string.Join(", ", v.Valid)) + ". See the tool's inputSchema.",
                new { errors = v.Errors, unknown = v.Unknown, valid = v.Valid });
        }

        public static ToolResult InternalError(Exception ex)
        {
            var cfg = RhinoMCPPlugin.Config;
            object details = null;
            if (cfg != null && cfg.DevMode)
            {
                var lines = (ex.StackTrace ?? "").Split('\n').Take(8).Select(l => l.Trim()).ToArray();
                details = new { type = ex.GetType().FullName, stack = lines };
            }
            else details = new { type = ex.GetType().FullName };
            return ToolResult.Fail("INTERNAL", ex.GetType().Name + ": " + ex.Message,
                "This is a bug in the tool, not in your arguments. Try simpler arguments or another tool.", details);
        }
    }

    public static class ToolsLoader
    {
        const string Prefix = "RhinoMCP.Tools.";

        /// <summary>
        /// Newest tools dll from the dev/deploy folder, falling back to the plugin's own folder (Yak installs ship the
        /// tools dll next to the .rhp). File names carry a sortable build stamp.
        /// </summary>
        public static string NewestDll()
        {
            var dirs = new List<string> { Config.ToolsDir };
            try { dirs.Add(Path.GetDirectoryName(typeof(ToolsLoader).Assembly.Location)); } catch (Exception) { }
            return dirs.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
                .SelectMany(d => Directory.GetFiles(d, Prefix + "*.dll"))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).FirstOrDefault();
        }

        static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>();

        /// <summary>Loads the newest tools dll from bytes (so the file stays unlocked). Returns null if none exists.</summary>
        public static Assembly LoadNewest(out string path)
        {
            path = NewestDll();
            if (path == null) return null;
            return Load(path);
        }

        /// <summary>Loads a specific tools dll from bytes (cached per path).</summary>
        public static Assembly Load(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Tools dll not found", path);
            Assembly asm;
            if (Loaded.TryGetValue(path, out asm)) return asm;
            byte[] dll = File.ReadAllBytes(path);
            string pdb = Path.ChangeExtension(path, ".pdb");
            asm = File.Exists(pdb) ? Assembly.Load(dll, File.ReadAllBytes(pdb)) : Assembly.Load(dll);
            Loaded[path] = asm;
            return asm;
        }
    }
}

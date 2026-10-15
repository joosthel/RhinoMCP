using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCP.Tools.Script
{
    /// <summary>Compile diagnostic from any engine.</summary>
    sealed class CsDiag
    {
        public string Severity, Id, Message;
        public int Line, Column;
    }

    sealed class CsRunResult
    {
        public bool CompileFailed;
        public List<CsDiag> Diagnostics = new List<CsDiag>();
        public object Value;
        public bool HasValue;
        public Exception RuntimeError;
    }

    /// <summary>
    /// C# snippet engine. Roslyn 3.9 CSharpScript is loaded lazily and by reflection from the Mono 4.5 lib folder
    /// (the tools dll has no compile-time dependency on it, so Grasshopper's own Roslyn copy is never touched at load).
    /// (A Mono.CSharp fallback was tried and removed after a Rhino crash investigation.) The snippet sees doc, args and Log, injected by a one-line prelude
    /// that reads AppDomain data (no globals type needed, so no metadata reference to the in-memory tools assembly).
    /// </summary>
    static class CsEngine
    {
        public const string Prelude =
            "var doc = (Rhino.RhinoDoc)System.AppDomain.CurrentDomain.GetData(\"rhinomcp.doc\"); " +
            "var args = (Newtonsoft.Json.Linq.JObject)System.AppDomain.CurrentDomain.GetData(\"rhinomcp.args\"); " +
            "var Log = (System.Action<string>)System.AppDomain.CurrentDomain.GetData(\"rhinomcp.log\"); ";

        static readonly string[] Imports =
        {
            "System", "System.Linq", "System.Collections.Generic", "System.Text",
            "Rhino", "Rhino.Geometry", "Rhino.DocObjects", "Newtonsoft.Json.Linq"
        };

        static bool _resolverInstalled;
        static string _monoDir;
        public static string MonoDir
        {
            get
            {
                if (_monoDir == null) _monoDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
                return _monoDir;
            }
        }

        static void InstallResolver()
        {
            if (_resolverInstalled) return;
            _resolverInstalled = true;
            AppDomain.CurrentDomain.AssemblyResolve += delegate (object s, ResolveEventArgs e)
            {
                try
                {
                    string simple = new AssemblyName(e.Name).Name;
                    if (!(simple.StartsWith("Microsoft.CodeAnalysis") || simple.StartsWith("System.Collections.Immutable") ||
                          simple.StartsWith("System.Reflection.Metadata") || simple == "System.Memory" ||
                          simple == "System.Runtime.CompilerServices.Unsafe" || simple == "System.Threading.Tasks.Extensions" ||
                          simple == "System.Buffers" || simple == "System.Numerics.Vectors" || simple == "Mono.CSharp"))
                        return null;
                    string path = Path.Combine(MonoDir, simple + ".dll");
                    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
                }
                catch (Exception) { return null; }
            };
        }

        static List<Assembly> RefAssemblies()
        {
            var l = new List<Assembly>
            {
                typeof(object).Assembly, typeof(Uri).Assembly, typeof(Enumerable).Assembly,
                typeof(RhinoDoc).Assembly, typeof(JObject).Assembly
            };
            foreach (var n in new[] { "Microsoft.CSharp.dll", "System.Numerics.dll", "System.Drawing.dll", "Facades/System.Runtime.dll" })
            {
                string p = Path.Combine(MonoDir, n);
                if (!File.Exists(p)) continue;
                try { l.Add(Assembly.LoadFrom(p)); } catch (Exception) { }
            }
            return l.Distinct().ToList();
        }

        // ---- Roslyn -------------------------------------------------------------------------------------------

        public static CsRunResult RunRoslyn(string code)
        {
            InstallResolver();
            var res = new CsRunResult();
            Assembly scripting = Assembly.LoadFrom(Path.Combine(MonoDir, "Microsoft.CodeAnalysis.Scripting.dll"));
            Assembly csScripting = Assembly.LoadFrom(Path.Combine(MonoDir, "Microsoft.CodeAnalysis.CSharp.Scripting.dll"));
            Type optionsT = scripting.GetType("Microsoft.CodeAnalysis.Scripting.ScriptOptions", true);
            Type csScriptT = csScripting.GetType("Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript", true);

            object options = optionsT.GetProperty("Default").GetValue(null, null);
            options = optionsT.GetMethod("WithReferences", new[] { typeof(IEnumerable<Assembly>) })
                              .Invoke(options, new object[] { RefAssemblies() });
            options = optionsT.GetMethod("WithImports", new[] { typeof(IEnumerable<string>) })
                              .Invoke(options, new object[] { Imports });

            // Script<object> Create(string code, ScriptOptions options = null, Type globalsType = null, InteractiveAssemblyLoader loader = null)
            MethodInfo create = csScriptT.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "Create" && !m.IsGenericMethod && m.GetParameters().Length == 4 &&
                            m.GetParameters()[0].ParameterType == typeof(string));
            object script = create.Invoke(null, new object[] { Prelude + code, options, null, null });
            Type scriptT = script.GetType();

            MethodInfo compile = scriptT.GetMethod("Compile", new[] { typeof(CancellationToken) });
            IEnumerable diags = (IEnumerable)compile.Invoke(script, new object[] { CancellationToken.None });
            foreach (object d in diags) res.Diagnostics.Add(ParseRoslynDiag(d));
            if (res.Diagnostics.Any(d => d.Severity == "Error")) { res.CompileFailed = true; return res; }

            // Task<ScriptState<object>> RunAsync(object globals, CancellationToken ct)
            MethodInfo run = scriptT.GetMethods().First(m => m.Name == "RunAsync" && m.GetParameters().Length == 2 &&
                                                              m.GetParameters()[0].ParameterType == typeof(object) &&
                                                              m.GetParameters()[1].ParameterType == typeof(CancellationToken));
            try
            {
                var task = (Task)run.Invoke(script, new object[] { null, CancellationToken.None });
                task.Wait();
                object state = task.GetType().GetProperty("Result").GetValue(task, null);
                res.Value = state.GetType().GetProperty("ReturnValue").GetValue(state, null);
                res.HasValue = true;
            }
            catch (TargetInvocationException tie) { res.RuntimeError = Unwrap(tie); }
            catch (AggregateException ae) { res.RuntimeError = Unwrap(ae); }
            return res;
        }

        static Exception Unwrap(Exception e)
        {
            while ((e is TargetInvocationException || e is AggregateException) && e.InnerException != null) e = e.InnerException;
            return e;
        }

        static CsDiag ParseRoslynDiag(object d)
        {
            Type t = d.GetType();
            var cd = new CsDiag();
            cd.Severity = Convert.ToString(t.GetProperty("Severity").GetValue(d, null));
            cd.Id = (string)t.GetProperty("Id").GetValue(d, null);
            cd.Message = (string)t.GetMethod("GetMessage", new[] { typeof(IFormatProvider) }).Invoke(d, new object[] { null });
            try
            {
                object loc = t.GetProperty("Location").GetValue(d, null);
                object span = loc.GetType().GetMethod("GetLineSpan").Invoke(loc, null);
                object start = span.GetType().GetProperty("StartLinePosition").GetValue(span, null);
                cd.Line = (int)start.GetType().GetProperty("Line").GetValue(start, null) + 1;
                cd.Column = (int)start.GetType().GetProperty("Character").GetValue(start, null) + 1;
            }
            catch (Exception) { }
            return cd;
        }

    }

    public sealed class RunCSharp : ITool
    {
        public string Name { get { return "run_csharp"; } }
        public string Description
        {
            get
            {
                return "Compile and run a C# 7 script snippet inside Rhino (Roslyn scripting). Predefined: doc (RhinoDoc), " +
                       "args (Newtonsoft JObject from the 'args' input), Log(string) (messages are returned in 'log'). Usings: System, System.Linq, " +
                       "System.Collections.Generic, Rhino, Rhino.Geometry, Rhino.DocObjects, Newtonsoft.Json.Linq. Use 'return <value>;' (or end with " +
                       "an expression without semicolon) to return a JSON-serialisable result. Returns result, log, objects_added (handles), engine, " +
                       "elapsed_ms. Compile errors give SCRIPT_ERROR with diagnostics[{severity,id,message,line,column}] (line 1 column is offset by an " +
                       "injected prelude). The first call in a Rhino session is slow (compiler cold start). One undo record per run. " +
                       "Prefer dedicated tools or run_python for simple tasks.";
            }
        }
        public string InputSchema
        {
            get { return "{\"type\":\"object\",\"properties\":{\"code\":{\"type\":\"string\",\"description\":\"C# statements, e.g. return doc.Objects.Count;\"},\"args\":{\"type\":\"object\",\"description\":\"Exposed to the snippet as JObject 'args'\"}},\"required\":[\"code\"],\"additionalProperties\":false}"; }
        }
        public ToolKind Kind { get { return ToolKind.Script; } }

        static string _engineName;   // engine that worked last time
        static bool _roslynBroken;
        static string _roslynWhy;

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var a = new ArgReader(args);
            string code = a.String("code", true);
            JToken argTok;
            JObject scriptArgs = new JObject();
            if (args != null && args.TryGetValue("args", out argTok) && argTok.Type != JTokenType.Null)
            {
                scriptArgs = argTok as JObject;
                if (scriptArgs == null) a.Error("'args' must be an object");
            }
            if (a.HasErrors) return a.ErrorResult();
            if (string.IsNullOrWhiteSpace(code))
                return ToolResult.Fail("INVALID_ARGS", "'code' is empty", "Pass C# statements, e.g. \"return 1 + 1;\".");
            if (ctx.Doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active document", null);

            var added = new List<Guid>();
            EventHandler<Rhino.DocObjects.RhinoObjectEventArgs> onAdd = delegate (object s, Rhino.DocObjects.RhinoObjectEventArgs e) { added.Add(e.ObjectId); };
            uint undoBefore = ctx.Doc.CurrentUndoRecordSerialNumber;
            var log = new List<string>();
            var domain = AppDomain.CurrentDomain;
            domain.SetData("rhinomcp.doc", ctx.Doc);
            domain.SetData("rhinomcp.args", scriptArgs);
            domain.SetData("rhinomcp.log", new Action<string>(s => { if (log.Count < 200) log.Add(s); }));

            var sw = Stopwatch.StartNew();
            CsRunResult r = null;
            string engine = null;
            var engineErrors = new List<string>();
            RhinoDoc.AddRhinoObject += onAdd;
            try
            {
                if (!_roslynBroken)
                {
                    try { r = CsEngine.RunRoslyn(code); engine = "roslyn"; }
                    catch (Exception ex)
                    {
                        Exception inner = ex;
                        while (inner.InnerException != null && (inner is TargetInvocationException || inner is TypeInitializationException)) inner = inner.InnerException;
                        _roslynWhy = inner.GetType().Name + ": " + inner.Message;
                        engineErrors.Add("roslyn: " + _roslynWhy);
                        _roslynBroken = true; r = null;
                    }
                }
            }
            finally
            {
                RhinoDoc.AddRhinoObject -= onAdd;
                domain.SetData("rhinomcp.doc", null); domain.SetData("rhinomcp.args", null); domain.SetData("rhinomcp.log", null);
                RunPython.CloseOpenedRecords(ctx.Doc, undoBefore);
            }
            sw.Stop();
            if (r == null)
                return ToolResult.Fail("UNSUPPORTED", "No C# engine could be started: " + string.Join(" | ", engineErrors),
                    "Use run_python (IronPython 2.7) instead.");
            _engineName = engine;

            var live = added.Distinct().Where(id => ctx.Doc.Objects.FindId(id) != null).ToList();
            var diags = r.Diagnostics.Select(d => new { severity = d.Severity, id = d.Id, message = d.Message, line = d.Line, column = d.Column }).ToList();
            if (r.CompileFailed)
            {
                var first = r.Diagnostics.FirstOrDefault(d => d.Severity == "Error");
                return ToolResult.Fail("SCRIPT_ERROR",
                    "Compile error" + (first == null ? "" : " at line " + first.Line + ": " + first.Id + " " + first.Message),
                    "Fix the C# and retry. Snippets are C# 7.3 statements; use 'return x;' for a result. Nothing was executed.",
                    new { engine = engine, diagnostics = diags, log = log, elapsed_ms = sw.ElapsedMilliseconds });
            }
            if (r.RuntimeError != null)
            {
                string st = r.RuntimeError.StackTrace ?? "";
                if (st.Length > 1500) st = st.Substring(0, 1500);
                return ToolResult.Fail("SCRIPT_ERROR", r.RuntimeError.GetType().Name + ": " + r.RuntimeError.Message,
                    "The snippet compiled but threw at run time. Changes made before the error are kept; use history_undo to revert them.",
                    new
                    {
                        engine = engine, error = new { type = r.RuntimeError.GetType().Name, message = r.RuntimeError.Message, stack = st },
                        diagnostics = diags, log = log, objects_added = live.Take(200).Select(id => ctx.Handles.For(id)).ToList(),
                        elapsed_ms = sw.ElapsedMilliseconds
                    });
            }

            JToken result = JValue.CreateNull();
            if (r.Value != null)
            {
                try
                {
                    var ser = new JsonSerializer { ReferenceLoopHandling = ReferenceLoopHandling.Ignore };
                    result = JToken.FromObject(r.Value, ser);
                }
                catch (Exception) { result = JValue.CreateString(Convert.ToString(r.Value)); }
            }
            var warns = diags.Where(d => d.severity == "Warning").ToList();
            var data = new
            {
                result = result,
                log = log,
                objects_added = live.Take(200).Select(id => ctx.Handles.For(id)).ToList(),
                objects_added_total = live.Count,
                engine = engine,
                warnings = warns,
                elapsed_ms = sw.ElapsedMilliseconds
            };
            var tr = ToolResult.Ok(data);
            if (engineErrors.Count > 0) tr.Warn("Fell back from an earlier engine: " + string.Join(" | ", engineErrors));
            return tr;
        }
    }
}

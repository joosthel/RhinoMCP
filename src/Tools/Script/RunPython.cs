using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCP.Tools.Script
{
    /// <summary>IronPython 2.7 via Rhino.Runtime.PythonScript. Variables: doc, args, result; stdout is captured.</summary>
    public sealed class RunPython : ITool
    {
        public string Name { get { return "run_python"; } }
        public string Description
        {
            get
            {
                return "Run an IronPython 2.7 script inside Rhino. IronPython 2.7: no f-strings, no numpy, print is a statement unless " +
                       "'from __future__ import print_function'. Available: rhinoscriptsyntax as rs, Rhino, scriptcontext as sc, System, json. " +
                       "Predefined: doc (the active RhinoDoc), args (dict from the 'args' input). Assign a JSON-serialisable value (anything else " +
                       "is str()'d) to the variable 'result' to return it. Returns result, stdout (8 kB max), objects_added (handles). " +
                       "Errors give SCRIPT_ERROR with error{type,message,line,traceback} (line numbers refer to your code). " +
                       "The whole run is one undo record (history_undo reverts it). Prefer dedicated tools when they exist.";
            }
        }
        public string InputSchema
        {
            get { return "{\"type\":\"object\",\"properties\":{\"code\":{\"type\":\"string\",\"description\":\"IronPython 2.7 source\"},\"args\":{\"type\":\"object\",\"description\":\"Exposed to the script as the dict 'args'\"}},\"required\":[\"code\"],\"additionalProperties\":false}"; }
        }
        public ToolKind Kind { get { return ToolKind.Script; } }

        const string Driver = @"
import sys, json, traceback
import StringIO
import Rhino
import scriptcontext as sc
import rhinoscriptsyntax as rs
import System
doc = sc.doc
args = json.loads(__mcp_args)
result = None
__mcp_buf = StringIO.StringIO()
__mcp_old = sys.stdout
__mcp_err = None
def __mcp_run(src):
    global __mcp_err
    try:
        code = compile(src, '<mcp>', 'exec')
        exec code in globals()
    except SystemExit:
        pass
    except BaseException as e:
        tb = []
        line = None
        try:
            for fn, ln, fnm, txt in traceback.extract_tb(sys.exc_info()[2]):
                if fn == '<mcp>':
                    sl = src.split('\n')
                    tb.append('line %d in %s: %s' % (ln, fnm, (txt or (sl[ln-1] if 0 < ln <= len(sl) else '')).strip()))
                    line = ln
        except Exception:
            pass
        if isinstance(e, SyntaxError):
            line = e.lineno
            tb.append('line %s: %s' % (e.lineno, (e.text or '').strip()))
        __mcp_err = {'type': e.__class__.__name__, 'message': str(e), 'line': line, 'traceback': tb[-10:]}
sys.stdout = __mcp_buf
try:
    __mcp_run(__mcp_src)
finally:
    sys.stdout = __mcp_old
__mcp_out = __mcp_buf.getvalue()
try:
    __mcp_res = json.dumps(result, default=str)
except Exception as e:
    __mcp_res = json.dumps(str(result))
__mcp_errjson = json.dumps(__mcp_err, default=str)
";

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
                return ToolResult.Fail("INVALID_ARGS", "'code' is empty", "Pass IronPython 2.7 source.");
            if (ctx.Doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active document", null);

            var added = new List<Guid>();
            EventHandler<Rhino.DocObjects.RhinoObjectEventArgs> onAdd = delegate (object s, Rhino.DocObjects.RhinoObjectEventArgs e) { added.Add(e.ObjectId); };
            uint undoBefore = ctx.Doc.CurrentUndoRecordSerialNumber;
            var sw = Stopwatch.StartNew();
            string outText, resJson, errJson;
            Rhino.Runtime.PythonScript ps = null;
            RhinoDoc.AddRhinoObject += onAdd;
            try
            {
                ps = Rhino.Runtime.PythonScript.Create();
                if (ps == null)
                    return ToolResult.Fail("UNSUPPORTED", "Rhino's Python plugin is not available", "Check that the IronPython plug-in is installed.");
                ps.ScriptContextDoc = ctx.Doc;
                ps.SetVariable("__mcp_args", scriptArgs.ToString(Formatting.None));
                ps.SetVariable("__mcp_src", code);
                try { ps.ExecuteScript(Driver); }
                catch (Exception ex)
                {
                    return ToolResult.Fail("INTERNAL", "Python driver failed: " + ex.Message,
                        "This is a bug in run_python, not in your script.", new { exception = ex.GetType().Name, stack = Trunc(SafeStack(ps, ex), 2000) });
                }
                outText = ps.GetVariable("__mcp_out") as string ?? "";
                resJson = ps.GetVariable("__mcp_res") as string ?? "null";
                errJson = ps.GetVariable("__mcp_errjson") as string ?? "null";
            }
            finally
            {
                RhinoDoc.AddRhinoObject -= onAdd;
                try { var disp = ps as IDisposable; if (disp != null) disp.Dispose(); } catch (Exception) { }
                CloseOpenedRecords(ctx.Doc, undoBefore);
            }
            sw.Stop();

            bool truncated = outText.Length > 8192;
            JToken result;
            try { result = JToken.Parse(resJson); } catch (Exception) { result = JValue.CreateString(resJson); }
            JToken err = JToken.Parse(errJson);
            var live = added.Distinct().Where(id => ctx.Doc.Objects.FindId(id) != null).ToList();
            var data = new
            {
                result = result,
                stdout = truncated ? outText.Substring(0, 8192) : outText,
                stdout_truncated = truncated,
                objects_added = live.Take(200).Select(id => ctx.Handles.For(id)).ToList(),
                objects_added_total = live.Count,
                elapsed_ms = sw.ElapsedMilliseconds,
                error = err.Type == JTokenType.Null ? null : err
            };
            if (err.Type != JTokenType.Null)
            {
                string msg = (string)err["type"] + ": " + (string)err["message"] +
                             (err["line"] != null && err["line"].Type != JTokenType.Null ? " (line " + (string)err["line"] + ")" : "");
                return ToolResult.Fail("SCRIPT_ERROR", msg,
                    "Fix the script and retry. Remember: IronPython 2.7 (no f-strings, print is a statement). Changes made before the error are kept; use history_undo to revert them.",
                    data);
            }
            return ToolResult.Ok(data);
        }

        // A script that called doc.BeginUndoRecord without ending it would swallow later edits; close records opened by the script.
        internal static void CloseOpenedRecords(RhinoDoc doc, uint before)
        {
            try
            {
                for (int i = 0; i < 10; i++)
                {
                    uint cur = doc.CurrentUndoRecordSerialNumber;
                    if (cur == 0 || cur == before) break;
                    doc.EndUndoRecord(cur);
                }
            }
            catch (Exception) { }
        }

        static string SafeStack(Rhino.Runtime.PythonScript ps, Exception ex)
        {
            try { return ps.GetStackTraceFromException(ex); } catch (Exception) { return ex.ToString(); }
        }
        static string Trunc(string s, int n) { return s != null && s.Length > n ? s.Substring(0, n) : s; }
    }
}

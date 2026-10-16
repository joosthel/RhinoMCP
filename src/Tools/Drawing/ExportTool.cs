using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCP.Tools.Script;
using RhinoMCP.Tools.Shared;

namespace RhinoMCP.Tools.Drawing
{
    /// <summary>Writes files only (the document is not modified): PDF of layouts via FilePdf, everything else via scripted -_Export.</summary>
    public sealed class ExportTool : ITool
    {
        public string Name { get { return "export"; } }
        public string Description
        {
            get
            {
                return "Export to a file: pdf | dwg | dxf | svg | ai | 3dm | step | stl | obj. path must be absolute (directories are created); an existing file is refused unless overwrite:true. " +
                       "pdf: layout (name | [names] | 'all') -> one vector PDF page per layout via Rhino's FilePdf (print widths from layers); options dpi (300), color 'print' (default) | 'display' | 'bw', margins_mm (0), background (false). " +
                       "Other formats export a target (handle | set | {query}); hidden, locked and page-space objects cannot be exported and are reported. " +
                       "dwg/dxf: scheme (named export scheme, default 'Default'), curves 'as_is' (default) | 'polylines' (non-line/arc/circle/polyline curves become polylines of tolerance polyline_tolerance; arcs, circles and joined polylines are kept). " +
                       "options: extra raw command-line option tokens inserted before the final Enter (e.g. '_Schema _AP214AutomotiveDesign' for step). " +
                       "Returns path, size and a post-check: dxf entity counts by type (LINE, ARC, LWPOLYLINE, SPLINE, ...), pdf page count, header checks for the rest. " +
                       "Scripted exports briefly change the selection and restore it. A command that waits for input is cancelled after input_timeout_s (default 10) with the prompt in the error.";
            }
        }
        public string InputSchema
        {
            get
            {
                return Schemas.Object(
                    "\"format\":{\"enum\":[\"pdf\",\"dwg\",\"dxf\",\"svg\",\"ai\",\"3dm\",\"step\",\"stl\",\"obj\"]}," +
                    "\"path\":{\"type\":\"string\",\"description\":\"Absolute output path with the matching extension\"}," +
                    "\"overwrite\":{\"type\":\"boolean\",\"default\":false}," +
                    "\"layout\":{\"description\":\"pdf: layout name, array of names, or 'all'\",\"oneOf\":[{\"type\":\"string\"},{\"type\":\"array\",\"items\":{\"type\":\"string\"}}]}," +
                    "\"target\":{\"$ref\":\"#/$defs/target\"}," +
                    "\"dpi\":{\"type\":\"integer\",\"minimum\":72,\"maximum\":1200,\"default\":300}," +
                    "\"color\":{\"enum\":[\"print\",\"display\",\"bw\"],\"default\":\"print\"}," +
                    "\"margins_mm\":{\"type\":\"number\",\"minimum\":0,\"default\":0},\"background\":{\"type\":\"boolean\",\"default\":false}," +
                    "\"scheme\":{\"type\":\"string\",\"default\":\"Default\"}," +
                    "\"curves\":{\"enum\":[\"as_is\",\"polylines\"],\"default\":\"as_is\"},\"polyline_tolerance\":{\"type\":\"number\",\"exclusiveMinimum\":0}," +
                    "\"options\":{\"type\":\"string\"}," +
                    "\"input_timeout_s\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":120,\"default\":10}",
                    "\"format\",\"path\"");
            }
        }
        public ToolKind Kind { get { return ToolKind.Read; } }

        static readonly Dictionary<string, string[]> Exts = new Dictionary<string, string[]>
        {
            { "pdf", new[] { ".pdf" } }, { "dwg", new[] { ".dwg" } }, { "dxf", new[] { ".dxf" } }, { "svg", new[] { ".svg" } },
            { "ai", new[] { ".ai" } }, { "3dm", new[] { ".3dm" } }, { "step", new[] { ".step", ".stp" } }, { "stl", new[] { ".stl" } }, { "obj", new[] { ".obj" } }
        };
        // Enter presses needed to accept the option prompts that follow the file name (discovered on Rhino 7.39 Mac).
        static readonly Dictionary<string, int> Enters = new Dictionary<string, int>
        { { "dwg", 1 }, { "dxf", 1 }, { "ai", 1 }, { "step", 1 }, { "stl", 2 }, { "obj", 2 }, { "svg", 0 }, { "3dm", 0 } };

        public ToolResult Execute(ToolContext ctx, JObject args)
        {
            var doc = ctx.Doc;
            if (doc == null) return ToolResult.Fail("NO_DOCUMENT", "No active Rhino document", "Open or create a document in Rhino first.");
            var a = new ArgReader(args);
            string format = a.String("format", true);
            string path = a.String("path", true);
            bool overwrite = a.Bool("overwrite", false);
            if (format != null) format = format.ToLowerInvariant();
            if (format != null && !Exts.ContainsKey(format)) { a.Error("'format' must be one of " + string.Join(", ", Exts.Keys)); format = null; }
            if (path != null)
            {
                if (!Path.IsPathRooted(path)) a.Error("'path' must be an absolute path: " + path);
                else if (path.IndexOf('"') >= 0 || path.Any(ch => ch < 32)) a.Error("'path' contains a quote or control character");
                else if (format != null && !Exts[format].Contains(Path.GetExtension(path).ToLowerInvariant()))
                    a.Error("'path' must end with " + string.Join(" or ", Exts[format]) + " for format " + format);
            }
            if (a.HasErrors) return a.ErrorResult();
            if (File.Exists(path) && !overwrite)
                return ToolResult.Fail("FILE_EXISTS", "'" + path + "' already exists (" + new FileInfo(path).Length + " bytes)", "Pass overwrite:true to replace it, or choose another path.");
            if (Directory.Exists(path)) return ToolResult.Fail("INVALID_ARGS", "'" + path + "' is a directory", "Pass a file path.");

            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); }
            catch (Exception e) { return ToolResult.Fail("INVALID_ARGS", "cannot create the output folder: " + e.Message, "Choose a writable absolute path."); }

            return format == "pdf" ? ExportPdf(ctx, args, a, path, overwrite) : ExportScripted(ctx, args, a, format, path, overwrite);
        }

        // ------------------------------------------------------------------ PDF

        ToolResult ExportPdf(ToolContext ctx, JObject args, ArgReader a, string path, bool overwrite)
        {
            var doc = ctx.Doc;
            int dpi = a.Int("dpi", false, 300, 72, 1200);
            string color = a.String("color", false, "print");
            double margins = a.Double("margins_mm", false, 0);
            bool background = a.Bool("background", false);
            var names = new List<string>();
            JToken lt;
            if (!args.TryGetValue("layout", out lt) || lt.Type == JTokenType.Null) a.Error("'layout' is required for pdf: a layout name, an array of names, or 'all'. Existing layouts: " + DrawingUtil.PageNames(doc));
            else if (lt.Type == JTokenType.String) names.Add((string)lt);
            else if (lt is JArray) foreach (var t in (JArray)lt) { if (t.Type == JTokenType.String) names.Add((string)t); else a.Error("'layout' array must hold strings"); }
            else a.Error("'layout' must be a string or array of strings");
            if (color != "print" && color != "display" && color != "bw") a.Error("'color' must be print, display or bw");
            if (a.HasErrors) return a.ErrorResult();

            var pages = new List<RhinoPageView>();
            var all = doc.Views.GetPageViews() ?? new RhinoPageView[0];
            if (names.Count == 1 && string.Equals(names[0], "all", StringComparison.OrdinalIgnoreCase)) pages.AddRange(all.OrderBy(p => p.PageNumber));
            else foreach (var n in names)
                {
                    var pv = DrawingUtil.FindPage(doc, n);
                    if (pv == null) return ToolResult.Fail("NOT_FOUND", "no layout named '" + n + "'", "Existing layouts: " + DrawingUtil.PageNames(doc) + ".");
                    pages.Add(pv);
                }
            if (pages.Count == 0) return ToolResult.Fail("NOT_FOUND", "the document has no layouts to export", "Create one with layout_create.");

            string how = "FilePdf";
            try
            {
                if (File.Exists(path)) File.Delete(path);
                WritePdf(pages, path, dpi, color, margins, background);
            }
            catch (MissingMethodException e)
            {
                return ToolResult.Fail("UNSUPPORTED", "FilePdf/ViewCaptureSettings are not usable from the tools DLL on this Rhino build (" + e.Message + ")",
                    "Fallback: run_command with -_Print is not wired up here; export dwg/svg/ai instead.");
            }
            catch (Exception e)
            {
                return ToolResult.Fail("EXPORT_FAILED", "FilePdf failed: " + e.GetType().Name + ": " + e.Message, "Check that the layouts render (open one in Rhino) and the path is writable.");
            }
            return Finish(ctx, "pdf", path, how, null, pages.Count, new List<string>(), null);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void WritePdf(List<RhinoPageView> pages, string path, int dpi, string color, double marginsMm, bool background)
        {
            var pdf = Rhino.FileIO.FilePdf.Create();
            foreach (var pv in pages)
            {
                using (var vcs = new ViewCaptureSettings(pv, dpi))
                {
                    vcs.RasterMode = false;
                    vcs.UsePrintWidths = true;
                    vcs.DrawBackground = background;
                    vcs.SetMargins(UnitSystem.Millimeters, marginsMm, marginsMm, marginsMm, marginsMm);
                    var et = vcs.OutputColor.GetType();
                    string want = color == "print" ? "PrintColor" : color == "display" ? "DisplayColor" : "BlackAndWhite";
                    if (Enum.IsDefined(et, want)) vcs.OutputColor = (ViewCaptureSettings.ColorMode)Enum.Parse(et, want);
                    pdf.AddPage(vcs);
                }
            }
            pdf.Write(path);
        }

        // ------------------------------------------------------------------ scripted export

        ToolResult ExportScripted(ToolContext ctx, JObject args, ArgReader a, string format, string path, bool overwrite)
        {
            var doc = ctx.Doc;
            string scheme = a.String("scheme", false, "Default");
            string curves = a.String("curves", false, "as_is");
            double tol = a.Double("polyline_tolerance", false, doc.ModelAbsoluteTolerance * 10);
            string options = a.String("options");
            int timeout = a.Int("input_timeout_s", false, 10, 1, 120);
            if (curves != "as_is" && curves != "polylines") a.Error("'curves' must be as_is or polylines");
            if (!(tol > 0)) a.Error("'polyline_tolerance' must be > 0");
            if (scheme.IndexOf('"') >= 0) a.Error("'scheme' must not contain quotes");
            if (options != null && (options.Contains("\"") || options.Contains("\n"))) a.Error("'options' must not contain quotes or newlines");
            if ((scheme != "Default" || curves != "as_is") && format != "dwg" && format != "dxf") a.Error("'scheme' and 'curves' apply to dwg/dxf only");
            var targets = ObjectQuery.Resolve(ctx, args["target"], a, "target");
            if (a.HasErrors) return a.ErrorResult();
            if (targets.Count == 0) return ToolResult.Fail("NOT_FOUND", "'target' matched no objects", "Check the handles or query with objects_query.");

            var warnings = new List<string>();
            var usable = new List<RhinoObject>();
            int pageSpace = 0, hidden = 0;
            foreach (var o in targets)
            {
                if (o.Attributes.Space == ActiveSpace.PageSpace) { pageSpace++; continue; }
                if (!o.IsSelectable(true, true, true, true) || o.IsHidden) { hidden++; continue; }
                usable.Add(o);
            }
            if (pageSpace > 0) warnings.Add(pageSpace + " page-space object(s) skipped: layout annotation cannot be exported with a model-space file. Export format 'pdf' for the sheet.");
            if (hidden > 0) warnings.Add(hidden + " hidden/locked/non-selectable object(s) skipped. Show or unlock their layers first.");
            if (usable.Count == 0) return ToolResult.Fail("NOT_FOUND", "none of the target objects can be exported (page-space: " + pageSpace + ", hidden or locked: " + hidden + ")", "Unhide or unlock the layers, or target other objects.");

            var before = doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id).ToList();
            var temps = new List<Guid>();
            bool undoWas = true; bool undoChanged = false;
            string cmdOutput = "";
            bool success = false; var watchdog = new InputWatchdog();
            int exported = 0;
            try
            {
                doc.Objects.UnselectAll();
                var toSelect = new List<RhinoObject>();
                if (curves == "polylines")
                {
                    try { undoWas = doc.UndoRecordingEnabled; doc.UndoRecordingEnabled = false; undoChanged = true; } catch (Exception) { }
                    int converted = 0;
                    foreach (var o in usable)
                    {
                        var c = o.Geometry as Curve;
                        if (c == null || IsPlainCurve(c, doc.ModelAbsoluteTolerance)) { toSelect.Add(o); continue; }
                        var pl = c.ToPolyline(0, 0, 0.0873, 0.0, 0.0, tol, 0.0, 0.0, true);
                        if (pl == null) { warnings.Add("curve " + ctx.Handles.For(o.Id) + " could not be converted to a polyline; exported as is"); toSelect.Add(o); continue; }
                        var id = doc.Objects.AddCurve(pl, o.Attributes);
                        if (id == Guid.Empty) { toSelect.Add(o); continue; }
                        temps.Add(id); converted++;
                        toSelect.Add(doc.Objects.FindId(id));
                    }
                    if (converted > 0) warnings.Add(converted + " curve(s) were converted to polylines for export (tolerance " + tol + "); the model is unchanged");
                }
                else toSelect.AddRange(usable);

                int sel = 0;
                foreach (var o in toSelect) { if (o != null && o.Select(true) > 0) sel++; }
                exported = sel;
                if (sel == 0) return ToolResult.Fail("INTERNAL", "could not select any target object for export", "Unlock and show the objects, then retry.");
                if (sel < toSelect.Count) warnings.Add((toSelect.Count - sel) + " object(s) could not be selected and are not in the file");

                var sb = new StringBuilder();
                sb.Append("-_Export \"").Append(path).Append("\"");
                if ((format == "dwg" || format == "dxf") && !string.Equals(scheme, "Default", StringComparison.Ordinal))
                    sb.Append(" _Scheme \"").Append(scheme).Append("\"");
                if (!string.IsNullOrWhiteSpace(options)) sb.Append(" ").Append(options.Trim());
                for (int i = 0; i < Enters[format]; i++) sb.Append(" _Enter");

                if (File.Exists(path)) File.Delete(path);
                string histBefore = SafeHistory();
                watchdog.Start(timeout);
                try { success = RhinoApp.RunScript(sb.ToString(), true); }
                finally { watchdog.Stop(); }
                string histAfter = SafeHistory();
                cmdOutput = histAfter.Length >= histBefore.Length && histAfter.StartsWith(histBefore, StringComparison.Ordinal)
                    ? histAfter.Substring(histBefore.Length) : histAfter.Substring(Math.Max(0, histAfter.Length - 3000));
                if (watchdog.Fired)
                {
                    RhinoMCP.Tools.History.HistoryUndo.CloseStrayRecord(doc);
                    try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
                    return ToolResult.Fail("COMMAND_NEEDS_INPUT", "the export command waited for input at '" + watchdog.Prompt + "' and was cancelled after " + timeout + " s",
                        "Pass the missing answers in 'options' (raw command-line tokens, e.g. '_Schema _AP214AutomotiveDesign'), or check the prompt with run_command.",
                        new { last_prompt = watchdog.Prompt, command_output = Lines(cmdOutput) });
                }
            }
            finally
            {
                try
                {
                    foreach (var id in temps) doc.Objects.Delete(id, true);
                    if (undoChanged) doc.UndoRecordingEnabled = undoWas;
                    doc.Objects.UnselectAll();
                    foreach (var id in before) { var o = doc.Objects.FindId(id); if (o != null) o.Select(true); }
                    doc.Views.Redraw();
                }
                catch (Exception) { }
            }
            if (cmdOutput.Contains("Scheme not found")) warnings.Add("scheme '" + scheme + "' was not found; Rhino used the default scheme");
            if (!success && !File.Exists(path))
                return ToolResult.Fail("EXPORT_FAILED", "Rhino reported the export failed or was cancelled", "Check command_output; the target may contain nothing this format can hold (for example curves in stl/obj, which need meshes or surfaces).",
                    new { command_output = Lines(cmdOutput) });
            return Finish(ctx, format, path, "-_Export", cmdOutput, exported, warnings, null);
        }

        static bool IsPlainCurve(Curve c, double tol)
        {
            if (c is LineCurve || c is ArcCurve || c is PolylineCurve) return true;
            Polyline pl; Arc arc; Circle circle;
            if (c.IsLinear(tol) || c.TryGetArc(out arc, tol) || c.TryGetCircle(out circle, tol) || c.TryGetPolyline(out pl)) return true;
            var poly = c as PolyCurve;
            if (poly != null)
            {
                for (int i = 0; i < poly.SegmentCount; i++) { var s = poly.SegmentCurve(i); if (s == null || !IsPlainCurve(s, tol)) return false; }
                return true;
            }
            return false;
        }

        static string SafeHistory() { try { return RhinoApp.CommandHistoryWindowText ?? ""; } catch (Exception) { return ""; } }
        static List<string> Lines(string s) { return (s ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(40).ToList(); }

        // ------------------------------------------------------------------ result + post-check

        ToolResult Finish(ToolContext ctx, string format, string path, string how, string cmdOutput, int count, List<string> warnings, object extra)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0)
                return ToolResult.Fail("EXPORT_FAILED", "the file was not written" + (fi.Exists ? " (0 bytes)" : ""), "Check command_output and the target geometry.",
                    new { command_output = Lines(cmdOutput) });
            var data = new Dictionary<string, object>();
            data["path"] = path; data["format"] = format; data["bytes"] = fi.Length; data["exists"] = true; data["method"] = how;
            data[format == "pdf" ? "layouts" : "objects"] = count;
            var check = new Dictionary<string, object>();
            try { PostCheck(format, path, check); }
            catch (Exception e) { check["error"] = "post-check failed: " + e.Message; }
            data["check"] = check;
            if (cmdOutput != null) data["command_output"] = Lines(cmdOutput);
            var res = ToolResult.Ok(data);
            foreach (var w in warnings) res.Warn(w);
            return res;
        }

        static void PostCheck(string format, string path, Dictionary<string, object> check)
        {
            switch (format)
            {
                case "dxf": DxfCheck(path, check); break;
                case "pdf": PdfCheck(path, check); break;
                case "dwg":
                    {
                        var head = new byte[6];
                        using (var fs = File.OpenRead(path)) fs.Read(head, 0, 6);
                        string h = Encoding.ASCII.GetString(head);
                        check["header"] = h; check["valid_header"] = h.StartsWith("AC");
                        break;
                    }
                case "svg":
                    {
                        string t = File.ReadAllText(path);
                        check["valid_header"] = t.Contains("<svg");
                        check["path_elements"] = Regex.Matches(t, "<path\\b").Count;
                        check["line_elements"] = Regex.Matches(t, "<line\\b").Count + Regex.Matches(t, "<polyline\\b").Count;
                        check["text_elements"] = Regex.Matches(t, "<text\\b").Count;
                        break;
                    }
                case "ai":
                    {
                        var head = new byte[16];
                        using (var fs = File.OpenRead(path)) fs.Read(head, 0, 16);
                        string h = Encoding.ASCII.GetString(head);
                        check["header"] = h.Trim('\0', '\r', '\n'); check["valid_header"] = h.StartsWith("%!PS") || h.StartsWith("%PDF");
                        break;
                    }
                case "3dm":
                    {
                        var head = new byte[32];
                        using (var fs = File.OpenRead(path)) fs.Read(head, 0, 32);
                        check["valid_header"] = Encoding.ASCII.GetString(head).StartsWith("3D Geometry File Format");
                        break;
                    }
                case "step":
                    {
                        string t = File.ReadAllText(path);
                        check["valid_header"] = t.StartsWith("ISO-10303-21");
                        check["entities"] = Regex.Matches(t, "^#\\d+\\s*=", RegexOptions.Multiline).Count;
                        var m = Regex.Match(t, "FILE_SCHEMA\\s*\\(\\s*\\(\\s*'([^']*)'");
                        if (m.Success) check["schema"] = m.Groups[1].Value;
                        break;
                    }
                case "stl":
                    {
                        var bytes = File.ReadAllBytes(path);
                        bool ascii = bytes.Length > 5 && Encoding.ASCII.GetString(bytes, 0, 5) == "solid" && Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 400)).Contains("facet");
                        check["encoding"] = ascii ? "ascii" : "binary";
                        check["triangles"] = ascii ? Regex.Matches(Encoding.ASCII.GetString(bytes), "facet normal").Count : (bytes.Length >= 84 ? (int)BitConverter.ToUInt32(bytes, 80) : 0);
                        break;
                    }
                case "obj":
                    {
                        string t = File.ReadAllText(path);
                        check["vertices"] = Regex.Matches(t, "^v ", RegexOptions.Multiline).Count;
                        check["faces"] = Regex.Matches(t, "^f ", RegexOptions.Multiline).Count;
                        check["lines"] = Regex.Matches(t, "^l ", RegexOptions.Multiline).Count;
                        break;
                    }
            }
        }

        static readonly HashSet<string> DxfCurveTypes = new HashSet<string> { "LINE", "ARC", "CIRCLE", "ELLIPSE", "POLYLINE", "LWPOLYLINE", "SPLINE" };

        static void DxfCheck(string path, Dictionary<string, object> check)
        {
            var lines = File.ReadAllLines(path);
            var counts = new SortedDictionary<string, int>();
            bool inEntities = false; string acadver = null;
            for (int i = 0; i + 1 < lines.Length; i += 2)
            {
                string code = lines[i].Trim(), val = lines[i + 1].Trim();
                if (code == "9" && val == "$ACADVER" && i + 3 < lines.Length) acadver = lines[i + 3].Trim();
                if (code == "2" && val == "ENTITIES") { inEntities = true; continue; }
                if (code == "0" && val == "ENDSEC") { inEntities = false; continue; }
                if (inEntities && code == "0" && val != "VERTEX" && val != "SEQEND")
                { int c; counts.TryGetValue(val, out c); counts[val] = c + 1; }
            }
            check["entities"] = counts;
            check["curve_entities"] = counts.Where(k => DxfCurveTypes.Contains(k.Key)).Sum(k => k.Value);
            check["spline_entities"] = counts.ContainsKey("SPLINE") ? counts["SPLINE"] : 0;
            check["exploded_nurbs_risk"] = counts.ContainsKey("SPLINE");
            if (acadver != null) check["acadver"] = acadver;
            check["valid_header"] = lines.Length > 2 && lines[0].Trim() == "0" && lines[1].Trim() == "SECTION";
        }

        static void PdfCheck(string path, Dictionary<string, object> check)
        {
            string t = Encoding.GetEncoding("iso-8859-1").GetString(File.ReadAllBytes(path));
            check["valid_header"] = t.StartsWith("%PDF-");
            check["pages"] = Regex.Matches(t, "/Type\\s*/Page(?![A-Za-z])").Count;
            check["raster_images"] = Regex.Matches(t, "/Subtype\\s*/Image").Count;
            var mb = Regex.Match(t, "/MediaBox\\s*\\[\\s*([-\\d.]+)\\s+([-\\d.]+)\\s+([-\\d.]+)\\s+([-\\d.]+)");
            if (mb.Success)
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                double w = double.Parse(mb.Groups[3].Value, ci) - double.Parse(mb.Groups[1].Value, ci);
                double h = double.Parse(mb.Groups[4].Value, ci) - double.Parse(mb.Groups[2].Value, ci);
                check["first_page_mm"] = new[] { Math.Round(w / 72.0 * 25.4, 1), Math.Round(h / 72.0 * 25.4, 1) };
            }
        }
    }
}

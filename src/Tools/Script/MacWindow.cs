using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Rhino;

namespace RhinoMCP.Tools.Script
{
    /// <summary>One AppKit window of the Rhino process, as seen through the Objective-C runtime.</summary>
    internal sealed class WinInfo
    {
        public IntPtr Ptr;
        public long Number;
        public string Title;
        public bool Miniaturized, Visible, Main, Key, Titled, CanBeMain, Panel, HandleMatch;
        public int Score;
    }

    /// <summary>
    /// Finds Rhino's document window through [NSApp windows] and can deminiaturize / re-miniaturize it. [NSApp mainWindow]
    /// is nil while the window is minimized or the app is not active, which used to make the Escape injection (cancel of a
    /// command waiting for input) and ViewCaptureToFile silently fail. Everything here must run on the UI thread.
    /// </summary>
    internal static class MacWindow
    {
        const string ObjC = "/usr/lib/libobjc.dylib";
        [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendPtr(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendIdx(IntPtr r, IntPtr s, ulong i);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern long SendLong(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern ulong SendULong(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SendBool(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SendBoolPtr(IntPtr r, IntPtr s, IntPtr a);

        static IntPtr Sel(string n) { return sel_registerName(n); }
        static bool Has(IntPtr o, string sel) { return SendBool(o, Sel(sel)); }

        public static bool Available { get { return Rhino.Runtime.HostUtils.RunningOnOSX; } }

        static IntPtr App() { return Send(objc_getClass("NSApplication"), Sel("sharedApplication")); }

        public static bool AppActive() { try { IntPtr a = App(); return a != IntPtr.Zero && Has(a, "isActive"); } catch (Exception) { return false; } }
        public static bool AppHidden() { try { IntPtr a = App(); return a != IntPtr.Zero && Has(a, "isHidden"); } catch (Exception) { return false; } }

        static string Str(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return "";
            IntPtr c = Send(nsString, Sel("UTF8String"));
            return c == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(c) ?? "");
        }

        /// <summary>All AppKit windows of this process (empty off Mac).</summary>
        public static List<WinInfo> Windows()
        {
            var list = new List<WinInfo>();
            if (!Available) return list;
            IntPtr app = App();
            if (app == IntPtr.Zero) return list;
            IntPtr arr = Send(app, Sel("windows"));
            if (arr == IntPtr.Zero) return list;
            ulong n = SendULong(arr, Sel("count"));
            IntPtr panelCls = objc_getClass("NSPanel");
            long handle = 0;
            try { handle = RhinoApp.MainWindowHandle().ToInt64(); } catch (Exception) { }
            for (ulong i = 0; i < n && i < 200; i++)
            {
                IntPtr w = SendIdx(arr, Sel("objectAtIndex:"), i);
                if (w == IntPtr.Zero) continue;
                var wi = new WinInfo { Ptr = w };
                try
                {
                    wi.Number = SendLong(w, Sel("windowNumber"));
                    wi.Title = Str(Send(w, Sel("title")));
                    wi.Miniaturized = Has(w, "isMiniaturized");
                    wi.Visible = Has(w, "isVisible");
                    wi.Main = Has(w, "isMainWindow");
                    wi.Key = Has(w, "isKeyWindow");
                    wi.CanBeMain = Has(w, "canBecomeMainWindow");
                    wi.Titled = (SendULong(w, Sel("styleMask")) & 1UL) != 0;
                    wi.Panel = panelCls != IntPtr.Zero && SendBoolPtr(w, Sel("isKindOfClass:"), panelCls);
                    wi.HandleMatch = handle != 0 && (w.ToInt64() == handle || wi.Number == handle);
                }
                catch (Exception) { }
                list.Add(wi);
            }
            return list;
        }

        /// <summary>
        /// Picks the document window: a titled, main-capable, non-panel window that is visible or miniaturized.
        /// Ranked by: matches RhinoApp.MainWindowHandle(), title contains the document name, title contains "Rhino",
        /// is currently main / key. Pure function over the window list.
        /// </summary>
        public static WinInfo Pick(IList<WinInfo> wins, string docName)
        {
            WinInfo best = null;
            foreach (var w in wins)
            {
                if (!w.Titled || !w.CanBeMain || w.Panel || !(w.Visible || w.Miniaturized)) continue;
                int s = 1;
                string t = w.Title ?? "";
                if (w.HandleMatch) s += 100;
                if (!string.IsNullOrEmpty(docName) && t.IndexOf(docName, StringComparison.OrdinalIgnoreCase) >= 0) s += 20;
                if (t.IndexOf("Rhino", StringComparison.OrdinalIgnoreCase) >= 0) s += 10;
                if (w.Main) s += 5;
                if (w.Key) s += 3;
                w.Score = s;
                if (best == null || s > best.Score) best = w;
            }
            return best;
        }

        public static string DocName()
        {
            try
            {
                var d = RhinoDoc.ActiveDoc;
                if (d == null) return null;
                if (!string.IsNullOrEmpty(d.Path)) return Path.GetFileNameWithoutExtension(d.Path);
                return d.Name;
            }
            catch (Exception) { return null; }
        }

        public static WinInfo Find() { return Pick(Windows(), DocName()); }

        static void Pump(int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { try { RhinoApp.Wait(); } catch (Exception) { } System.Threading.Thread.Sleep(30); }
        }

        public static bool Deminiaturize(WinInfo w, int waitMs)
        {
            try
            {
                SendPtr(w.Ptr, Sel("deminiaturize:"), IntPtr.Zero);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < waitMs && Has(w.Ptr, "isMiniaturized"))
                {
                    try { RhinoApp.Wait(); } catch (Exception) { }
                    System.Threading.Thread.Sleep(30);
                }
                Pump(400); // let the un-minimize animation finish so the view is really on screen
                return !Has(w.Ptr, "isMiniaturized");
            }
            catch (Exception) { return false; }
        }

        public static void Miniaturize(WinInfo w)
        {
            try { SendPtr(w.Ptr, Sel("miniaturize:"), IntPtr.Zero); } catch (Exception) { }
        }

        /// <summary>JSON-friendly window state for system_status.</summary>
        public static object State()
        {
            if (!Available) return new { available = true, state = "non-mac" };
            try
            {
                var all = Windows();
                var w = Pick(all, DocName());
                if (w == null)
                    return new { available = false, state = "no_document_window", app_active = AppActive(), app_hidden = AppHidden(), windows = all.Count,
                                 hint = "Rhino has no visible or minimized document window; open or restore it." };
                string state = w.Miniaturized ? "minimized" : (!w.Visible ? "hidden" : (AppHidden() ? "app_hidden" : "visible"));
                return new
                {
                    available = state == "visible",
                    state = state,
                    title = w.Title,
                    window_number = w.Number,
                    minimized = w.Miniaturized,
                    visible = w.Visible,
                    is_main = w.Main,
                    is_key = w.Key,
                    app_active = AppActive(),
                    app_hidden = AppHidden(),
                    matched_by_handle = w.HandleMatch,
                    hint = state == "visible" ? null : "view_capture temporarily restores a minimized window; other states return WINDOW_UNAVAILABLE."
                };
            }
            catch (Exception ex) { return new { available = false, state = "unknown", error = ex.Message }; }
        }
    }

    /// <summary>
    /// Makes sure the Rhino document window is on screen for the duration of a capture: a minimized window is
    /// deminiaturized and put back (re-minimized) on Dispose. Other unusable states fail with a clear message.
    /// </summary>
    internal sealed class WindowLease : IDisposable
    {
        WinInfo _win;
        bool _restoreMini;
        public string Error;
        public bool Restored;   // true when the window was temporarily un-minimized

        public static WindowLease Acquire()
        {
            var l = new WindowLease();
            if (!MacWindow.Available) return l;
            try
            {
                var w = MacWindow.Find();
                if (w == null)
                { l.Error = "Rhino has no visible or minimized document window (app_active=" + MacWindow.AppActive() + ", app_hidden=" + MacWindow.AppHidden() + ")"; return l; }
                if (w.Miniaturized)
                {
                    if (!MacWindow.Deminiaturize(w, 3000))
                    { l.Error = "the Rhino window is minimized and could not be restored"; return l; }
                    l._win = w; l._restoreMini = true; l.Restored = true;
                }
                else if (!w.Visible)
                { l.Error = "the Rhino window is hidden (not on screen)"; return l; }
                else if (MacWindow.AppHidden())
                { l.Error = "the Rhino application is hidden (Cmd+H)"; return l; }
            }
            catch (Exception) { /* unknown state: try the capture anyway */ }
            return l;
        }

        public void Dispose()
        {
            if (_restoreMini && _win != null) { MacWindow.Miniaturize(_win); _restoreMini = false; }
        }
    }
}

using System;
using System.Runtime.InteropServices;
using Rhino;

namespace RhinoMCP.Tools.Script
{
    /// <summary>user32 / kernel32 calls used on Windows only. Every call site is guarded by Platform.IsWindows; on Mac the DllImports are never resolved.</summary>
    internal static class WinNative
    {
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public const int SW_MINIMIZE = 6;
        public const int SW_RESTORE = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        /// <summary>Physical RAM total/available in MB via GlobalMemoryStatusEx; false off Windows or on failure.</summary>
        public static bool MemoryMb(out double totalMb, out double availMb)
        {
            totalMb = 0; availMb = 0;
            if (!Platform.IsWindows) return false;
            try
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
                if (!GlobalMemoryStatusEx(ref m)) return false;
                totalMb = Math.Round(m.ullTotalPhys / 1048576.0);
                availMb = Math.Round(m.ullAvailPhys / 1048576.0);
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Rhino 7 for Windows main window handling (RhinoApp.MainWindowHandle + user32). Counterpart of MacWindow. UI thread only.
    /// Unverified on real Windows at the time of writing; every call is guarded and failures fall back to "try the capture anyway".
    /// </summary>
    internal static class WinWindow
    {
        public static IntPtr Handle()
        {
            try { return RhinoApp.MainWindowHandle(); } catch (Exception) { return IntPtr.Zero; }
        }

        static void Pump(int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { try { RhinoApp.Wait(); } catch (Exception) { } System.Threading.Thread.Sleep(30); }
        }

        public static bool Restore(IntPtr h, int waitMs)
        {
            try
            {
                WinNative.ShowWindow(h, WinNative.SW_RESTORE);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < waitMs && WinNative.IsIconic(h)) { try { RhinoApp.Wait(); } catch (Exception) { } System.Threading.Thread.Sleep(30); }
                Pump(300);
                return !WinNative.IsIconic(h);
            }
            catch (Exception) { return false; }
        }

        public static void Minimize(IntPtr h)
        {
            try { WinNative.ShowWindow(h, WinNative.SW_MINIMIZE); } catch (Exception) { }
        }

        /// <summary>Same shape as MacWindow.State(): state is visible | minimized | hidden | no_document_window | unknown.</summary>
        public static object State()
        {
            try
            {
                IntPtr h = Handle();
                if (h == IntPtr.Zero || !WinNative.IsWindow(h))
                    return new { available = false, state = "no_document_window", hint = "Rhino's main window handle is not available." };
                bool mini = WinNative.IsIconic(h), vis = WinNative.IsWindowVisible(h);
                string state = mini ? "minimized" : (!vis ? "hidden" : "visible");
                return new
                {
                    available = state == "visible",
                    state = state,
                    minimized = mini,
                    visible = vis,
                    hwnd = h.ToInt64(),
                    hint = state == "visible" ? null : "view_capture temporarily restores a minimized window; other states return WINDOW_UNAVAILABLE."
                };
            }
            catch (Exception ex) { return new { available = false, state = "unknown", error = ex.Message }; }
        }
    }

    /// <summary>Platform dispatch for window state (system_status, capture errors).</summary>
    internal static class HostWindow
    {
        public static object State() { return Platform.IsWindows ? WinWindow.State() : MacWindow.State(); }
    }

    /// <summary>Platform dispatch for cancelling a command that waits for input.</summary>
    internal static class CancelHelper
    {
        /// <summary>
        /// Delivers Escape. Mac: native NSEvent into the document window (plus SendKeystrokes from the 3rd watchdog attempt on or when it failed).
        /// Windows: give Rhino's main window the keyboard focus, then SendKeystrokes("!") ("!" = Escape on the Rhino command line).
        /// Returns the cancel_method string. UI thread only.
        /// </summary>
        public static string Escape(int attempt)
        {
            if (Platform.IsWindows)
            {
                try { RhinoApp.SetFocusToMainWindow(); } catch (Exception) { }
                RhinoApp.SendKeystrokes("!", true);
                return "sendkeystrokes+focus";
            }
            string m;
            bool posted = MacEscape.Post(out m);
            if (!posted || attempt >= 3) { RhinoApp.SendKeystrokes("!", true); m = posted ? m + "+sendkeystrokes" : "sendkeystrokes"; }
            return m;
        }
    }
}

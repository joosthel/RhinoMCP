using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace RhinoMCP
{
    /// <summary>
    /// Single source of truth for everything that differs between Rhino 7 for Mac and Rhino 7 for Windows:
    /// data folders, file protection, clipboard, loopback host names. Path computation is split into pure functions
    /// (<see cref="DataDirFor"/>, <see cref="IsLoopbackHost"/>) so both platforms can be unit-tested on either OS.
    ///
    /// Locations
    ///   Mac:     data  ~/Library/Application Support/RhinoMCP
    ///            plugin ~/Library/Application Support/McNeel/Rhinoceros/MacPlugIns/RhinoMCP.rhp/
    ///   Windows: data  %APPDATA%\RhinoMCP
    ///            plugin wherever the .rhp was installed (Yak: %APPDATA%\McNeel\Rhinoceros\packages\7.0\RhinoMCP\&lt;version&gt;\)
    /// Under the data folder: config.json (owner-only), tools\ (hot-reload tools dlls), instances\ (discovery files), crash.log.
    /// </summary>
    public static class Platform
    {
        static bool? _forceMac;
        static bool? _detectedMac;

        /// <summary>Test hook: pretend to run on Mac (true) / Windows (false). null restores real detection.</summary>
        public static void ForceForTest(bool? mac) { _forceMac = mac; }

        public static bool IsMac
        {
            get
            {
                if (_forceMac.HasValue) return _forceMac.Value;
                if (!_detectedMac.HasValue) _detectedMac = DetectMac();
                return _detectedMac.Value;
            }
        }

        /// <summary>Rhino 7 exists on exactly these two platforms.</summary>
        public static bool IsWindows { get { return !IsMac; } }

        public static string Name { get { return IsMac ? "mac" : "windows"; } }

        static bool DetectMac()
        {
            try { return HostOsx(); }
            catch (Exception) { return Path.DirectorySeparatorChar == '/'; }   // not hosted by Rhino (unit tests)
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool HostOsx() { return Rhino.Runtime.HostUtils.RunningOnOSX; }

        // ------------------------------------------------------------------ folders (pure functions + current-platform accessors)

        /// <summary>Data folder for a platform. home = user profile folder, appData = %APPDATA% (Windows only).</summary>
        public static string DataDirFor(bool mac, string home, string appData)
        {
            if (mac) return Path.Combine(Path.Combine(Path.Combine(home, "Library"), "Application Support"), "RhinoMCP");
            return Path.Combine(appData, "RhinoMCP");
        }

        public static string HomeDir
        {
            get
            {
                string h = IsMac ? Environment.GetEnvironmentVariable("HOME") : null;
                return string.IsNullOrEmpty(h) ? Environment.GetFolderPath(Environment.SpecialFolder.Personal) : h;
            }
        }

        static string AppDataDir { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }

        public static string DataDir { get { return DataDirFor(IsMac, HomeDir, AppDataDir); } }
        public static string ConfigPath { get { return Path.Combine(DataDir, "config.json"); } }
        public static string ToolsDir { get { return Path.Combine(DataDir, "tools"); } }
        public static string InstancesDir { get { return Path.Combine(DataDir, "instances"); } }
        public static string CrashLogPath { get { return Path.Combine(DataDir, "crash.log"); } }
        public static string TempDir { get { return Path.GetTempPath(); } }

        /// <summary>User-facing rendering of paths for messages and guidance (~ on Mac, %APPDATA% on Windows).</summary>
        public static string ConfigPathDisplay { get { return IsMac ? "~/Library/Application Support/RhinoMCP/config.json" : "%APPDATA%\\RhinoMCP\\config.json"; } }
        public static string ToolsDirDisplay { get { return IsMac ? "~/Library/Application Support/RhinoMCP/tools" : "%APPDATA%\\RhinoMCP\\tools"; } }

        // ------------------------------------------------------------------ loopback host names

        /// <summary>DNS-rebinding guard: the Host header must be exactly 127.0.0.1:port or localhost:port (case-insensitive).</summary>
        public static bool IsLoopbackHost(string hostHeader, int port)
        {
            string h = (hostHeader ?? "").ToLowerInvariant();
            return h == "127.0.0.1:" + port || h == "localhost:" + port;
        }

        // ------------------------------------------------------------------ Roslyn lookup (run_csharp on Windows)

        /// <summary>
        /// Windows candidate folders that may hold the Roslyn scripting assemblies, most likely first. Pure given the two seed paths
        /// (null = derive from RhinoCommon.dll's location / the install's Plug-ins folder), so it is testable on any OS.
        /// Rhino 7 layout: &lt;root&gt;\System\RhinoCommon.dll, &lt;root&gt;\Plug-ins\Grasshopper\(Components|Libraries)\..., &lt;root&gt;\Plug-ins\Rhino.Python\...
        /// Unverified on a real Windows install: the bounded recursive search of Plug-ins\ is the safety net.
        /// </summary>
        public static System.Collections.Generic.List<string> RoslynCandidates(string scriptingDll, string rhinoCommonDir, string pluginsRoot)
        {
            var l = new System.Collections.Generic.List<string>();
            if (rhinoCommonDir == null) { try { rhinoCommonDir = Path.GetDirectoryName(typeof(Rhino.RhinoApp).Assembly.Location); } catch (Exception) { } }
            if (string.IsNullOrEmpty(rhinoCommonDir)) return l;
            string root = Path.GetDirectoryName(rhinoCommonDir);
            var seeds = new System.Collections.Generic.List<string> { rhinoCommonDir };
            if (!string.IsNullOrEmpty(root))
            {
                string plug = Path.Combine(root, "Plug-ins");
                string gh = Path.Combine(plug, "Grasshopper");
                seeds.Add(Path.Combine(gh, "Components"));
                seeds.Add(gh);
                seeds.Add(Path.Combine(gh, "Libraries"));
                seeds.Add(Path.Combine(plug, "Rhino.Python"));
                if (pluginsRoot == null) pluginsRoot = plug;
            }
            foreach (string s in seeds) if (!l.Contains(s)) l.Add(s);
            try
            {
                if (pluginsRoot != null && Directory.Exists(pluginsRoot))
                    foreach (string f in Directory.GetFiles(pluginsRoot, scriptingDll, SearchOption.AllDirectories))
                    {
                        string d = Path.GetDirectoryName(f);
                        if (!l.Contains(d)) l.Add(d);
                    }
            }
            catch (Exception) { }
            return l;
        }

        // ------------------------------------------------------------------ owner-only files

        /// <summary>Restricts a file to the current user (Mac chmod 600; Windows: protected ACL with only the current user). Best effort.</summary>
        public static void SecureFile(string path)
        {
            try
            {
                if (IsMac)
                {
                    var psi = new ProcessStartInfo("/bin/chmod", "600 \"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true };
                    using (var p = Process.Start(psi)) { p.WaitForExit(3000); }
                }
                else SecureFileWindows(path);
            }
            catch (Exception) { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SecureFileWindows(string path)
        {
            var fi = new FileInfo(path);
            var sec = fi.GetAccessControl();
            sec.SetAccessRuleProtection(true, false);   // stop inheriting, drop inherited entries
            foreach (System.Security.AccessControl.FileSystemAccessRule r in sec.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
                sec.RemoveAccessRule(r);
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                me, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            fi.SetAccessControl(sec);
        }

        // ------------------------------------------------------------------ clipboard

        /// <summary>Copies text to the system clipboard (Mac pbcopy; Windows clip.exe, which needs no UI thread or STA). False on failure.</summary>
        public static bool CopyToClipboard(string text)
        {
            try
            {
                var psi = IsMac ? new ProcessStartInfo("/usr/bin/pbcopy") : new ProcessStartInfo("clip.exe");
                psi.UseShellExecute = false; psi.RedirectStandardInput = true; psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    p.StandardInput.Write(text);
                    p.StandardInput.Close();
                    return p.WaitForExit(3000);
                }
            }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ process stats

        /// <summary>Working set of this process in MB (managed API works on Mono/Mac and .NET Framework/Windows), -1 if unavailable.</summary>
        public static double RssMb()
        {
            try { using (var p = Process.GetCurrentProcess()) return Math.Round(p.WorkingSet64 / 1048576.0); }
            catch (Exception) { return -1; }
        }
    }
}

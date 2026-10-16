using System;
using System.Diagnostics;
using System.IO;
using RhinoMCP;

// Pure-logic tests for src/Core/Platform.cs (no running Rhino needed). Compiled together with Platform.cs against RhinoCommon.dll
// (only referenced for HostUtils / RhinoApp, which these tests never call) by scripts/test_platform.sh (Mac) and
// scripts/test_platform.ps1 (Windows). Exit code 0 = all passed.
static class PlatformTests
{
    static int _fail, _n;

    static void Eq(object expected, object actual, string what)
    {
        _n++;
        if (!object.Equals(expected, actual)) { _fail++; Console.WriteLine("FAIL " + what + ": expected [" + expected + "] got [" + actual + "]"); }
    }

    static void True(bool v, string what) { Eq(true, v, what); }

    static int Main()
    {
        char s = Path.DirectorySeparatorChar;

        // Data dir computation with an injected platform (works on any OS)
        Eq(Path.Combine(Path.Combine(Path.Combine("/Users/me", "Library"), "Application Support"), "RhinoMCP"),
           Platform.DataDirFor(true, "/Users/me", "ignored"), "mac data dir");
        Eq(Path.Combine("C:" + s + "Users" + s + "me" + s + "AppData" + s + "Roaming", "RhinoMCP"),
           Platform.DataDirFor(false, "ignored", "C:" + s + "Users" + s + "me" + s + "AppData" + s + "Roaming"), "windows data dir");
        True(Platform.DataDirFor(true, "/Users/me", "x").EndsWith("Application Support" + s + "RhinoMCP"), "mac data dir suffix");

        // Forced platform drives the derived paths and display strings
        Platform.ForceForTest(true);
        True(Platform.IsMac && !Platform.IsWindows, "force mac");
        Eq("mac", Platform.Name, "name mac");
        True(Platform.ConfigPathDisplay.StartsWith("~/Library/Application Support/RhinoMCP"), "mac display");
        True(Platform.ConfigPath.EndsWith("RhinoMCP" + s + "config.json"), "config path");
        True(Platform.ToolsDir.EndsWith("RhinoMCP" + s + "tools"), "tools dir");
        True(Platform.InstancesDir.EndsWith("RhinoMCP" + s + "instances"), "instances dir");
        Platform.ForceForTest(false);
        True(Platform.IsWindows && !Platform.IsMac, "force windows");
        Eq("windows", Platform.Name, "name windows");
        True(Platform.ConfigPathDisplay.StartsWith("%APPDATA%"), "windows display");
        Platform.ForceForTest(null);

        // Host-header guard: both loopback spellings, case-insensitive, exact port, nothing else
        True(Platform.IsLoopbackHost("127.0.0.1:7340", 7340), "host 127");
        True(Platform.IsLoopbackHost("localhost:7340", 7340), "host localhost");
        True(Platform.IsLoopbackHost("LocalHost:7340", 7340), "host case");
        True(!Platform.IsLoopbackHost("127.0.0.1:7341", 7340), "host wrong port");
        True(!Platform.IsLoopbackHost("evil.example:7340", 7340), "host evil");
        True(!Platform.IsLoopbackHost("127.0.0.1", 7340), "host no port");
        True(!Platform.IsLoopbackHost(null, 7340), "host null");
        True(!Platform.IsLoopbackHost("[::1]:7340", 7340), "host ipv6 not accepted");

        // Roslyn candidate search on a fake Rhino 7 Windows tree
        string tmp = Path.Combine(Path.GetTempPath(), "rhinomcp_plat_" + Guid.NewGuid().ToString("N"));
        try
        {
            string sys = Path.Combine(tmp, "System");
            string comp = Path.Combine(Path.Combine(Path.Combine(tmp, "Plug-ins"), "Grasshopper"), "Components");
            string odd = Path.Combine(Path.Combine(Path.Combine(tmp, "Plug-ins"), "SomethingElse"), "bin");
            Directory.CreateDirectory(sys); Directory.CreateDirectory(comp); Directory.CreateDirectory(odd);
            File.WriteAllText(Path.Combine(odd, "Microsoft.CodeAnalysis.Scripting.dll"), "x");
            var c = Platform.RoslynCandidates("Microsoft.CodeAnalysis.Scripting.dll", sys, null);
            Eq(sys, c[0], "candidate 0 is the RhinoCommon dir");
            True(c.Contains(comp), "candidate has Grasshopper\\Components");
            True(c.Contains(odd), "candidate found by Plug-ins search");
            Eq(0, Platform.RoslynCandidates("x.dll", "", null).Count, "no seed -> empty");
        }
        finally { try { Directory.Delete(tmp, true); } catch (Exception) { } }

        // SecureFile on the real platform: file stays readable/writable by us and is owner-only
        string f = Path.Combine(Path.GetTempPath(), "rhinomcp_sec_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(f, "{}");
            Platform.SecureFile(f);
            Eq("{}", File.ReadAllText(f), "secured file still readable");
            File.WriteAllText(f, "{\"a\":1}");
            if (Path.DirectorySeparatorChar == '/')
            {
                var psi = new ProcessStartInfo("/usr/bin/stat", "-f %Lp \"" + f + "\"") { UseShellExecute = false, RedirectStandardOutput = true };
                using (var p = Process.Start(psi)) { string o = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); Eq("600", o, "mode 600"); }
            }
            else
            {
                var sec = new FileInfo(f).GetAccessControl();
                True(sec.AreAccessRulesProtected, "ACL protected (no inheritance)");
                Eq(1, sec.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Count, "single ACE");
            }
        }
        finally { try { File.Delete(f); } catch (Exception) { } }

        True(Platform.RssMb() > 0, "rss");
        True(Platform.TempDir.Length > 0, "temp dir");

        Console.WriteLine(_fail == 0 ? "PLATFORM TESTS OK (" + _n + " checks)" : "PLATFORM TESTS FAILED (" + _fail + " of " + _n + ")");
        return _fail == 0 ? 0 : 1;
    }
}

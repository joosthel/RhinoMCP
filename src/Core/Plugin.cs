using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.PlugIns;

[assembly: Guid("995CF377-563C-482D-B295-F80F5E24840B")]
[assembly: AssemblyTitle("RhinoMCP")]
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
[assembly: PlugInDescription(DescriptionType.Organization, "RhinoMCP")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://github.com/joosthel/RhinoMCP")]
[assembly: PlugInDescription(DescriptionType.Address, "Research prototype: MCP server inside Rhino 7 for Mac")]

namespace RhinoMCP
{
    public sealed class RhinoMCPPlugin : PlugIn
    {
        public const string VersionString = "0.2.0";
        static readonly object Gate = new object();

        public static RhinoMCPPlugin Instance { get; private set; }
        public static Config Config { get; private set; }
        public static HttpServer Server { get; private set; }

        public RhinoMCPPlugin() { Instance = this; }

        public override PlugInLoadTime LoadTime { get { return PlugInLoadTime.AtStartup; } }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            // Stay light: defer all real work to the first idle tick.
            RhinoApp.Idle += OnFirstIdle;
            RhinoApp.Closing += OnClosing;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            return LoadReturnCode.Success;
        }

        /// <summary>
        /// Mono exits the process on an unhandled exception (e.g. thrown in a finalizer); we cannot stop that,
        /// but we can leave a trace in ~/Library/Application Support/RhinoMCP/crash.log for diagnosis.
        /// </summary>
        static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                string dir = RhinoMCP.Config.DataDir;
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"),
                    DateTime.Now.ToString("o") + " terminating=" + e.IsTerminating + "\n" + e.ExceptionObject + "\n\n");
            }
            catch (Exception) { }
        }

        static void OnFirstIdle(object sender, EventArgs e)
        {
            RhinoApp.Idle -= OnFirstIdle;
            try
            {
                var cfg = Config.Load();
                Config = cfg;
                if (cfg.AutoStart) StartServer();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("RhinoMCP: startup failed: " + ex.Message);
            }
        }

        static void OnClosing(object sender, EventArgs e)
        {
            try { StopServer(); } catch (Exception) { }
        }

        /// <summary>Starts (or returns the running) server. Must be called on the UI thread.</summary>
        public static HttpServer StartServer()
        {
            lock (Gate)
            {
                if (Config == null) Config = Config.Load();
                if (Server != null && Server.Running) return Server;
                var reload = ToolRegistry.Reload();
                if (!reload.Ok) RhinoApp.WriteLine("RhinoMCP: tool load problem: " + reload.Error);
                var s = new HttpServer(Config);
                s.Start();
                Server = s;
                RhinoApp.WriteLine("RhinoMCP: listening on http://127.0.0.1:" + s.Port + "/mcp (" + ToolRegistry.Count + " tools)");
                return s;
            }
        }

        public static void StopServer()
        {
            lock (Gate)
            {
                if (Server != null) { Server.Stop(); Server = null; }
            }
        }
    }
}

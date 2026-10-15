using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace RhinoMCP
{
    /// <summary>Persistent settings in ~/Library/Application Support/RhinoMCP/config.json (mode 0600).</summary>
    public sealed class Config
    {
        [JsonProperty("port")] public int Port = 7340;
        [JsonProperty("token")] public string Token = "";
        [JsonProperty("autoStart")] public bool AutoStart = true;
        [JsonProperty("allowScripts")] public bool AllowScripts = true;
        [JsonProperty("devMode")] public bool DevMode = true;

        public static string HomeDir
        {
            get
            {
                string h = Environment.GetEnvironmentVariable("HOME");
                return string.IsNullOrEmpty(h) ? Environment.GetFolderPath(Environment.SpecialFolder.Personal) : h;
            }
        }
        public static string DataDir { get { return Path.Combine(HomeDir, "Library/Application Support/RhinoMCP"); } }
        public static string ConfigPath { get { return Path.Combine(DataDir, "config.json"); } }
        public static string ToolsDir { get { return Path.Combine(DataDir, "tools"); } }
        public static string InstancesDir { get { return Path.Combine(DataDir, "instances"); } }

        public static Config Load()
        {
            Directory.CreateDirectory(DataDir);
            Config c = null;
            if (File.Exists(ConfigPath))
            {
                try { c = JsonConvert.DeserializeObject<Config>(File.ReadAllText(ConfigPath)); }
                catch (Exception) { c = null; }
            }
            bool dirty = false;
            if (c == null) { c = new Config(); dirty = true; }
            if (string.IsNullOrEmpty(c.Token)) { c.Token = NewToken(); dirty = true; }
            if (dirty) c.Save();
            return c;
        }

        public void Save()
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(this, Formatting.Indented));
            Chmod600(ConfigPath);
        }

        static string NewToken()
        {
            var bytes = new byte[32];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            var sb = new System.Text.StringBuilder(64);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static void Chmod600(string path)
        {
            try
            {
                var psi = new ProcessStartInfo("/bin/chmod", "600 \"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) { p.WaitForExit(3000); }
            }
            catch (Exception) { }
        }
    }
}

using System;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace RhinoMCP
{
    /// <summary>Persistent settings in config.json inside Platform.DataDir (owner-only: chmod 600 on Mac, user-only ACL on Windows).</summary>
    public sealed class Config
    {
        [JsonProperty("port")] public int Port = 7340;
        [JsonProperty("token")] public string Token = "";
        [JsonProperty("autoStart")] public bool AutoStart = true;
        [JsonProperty("allowScripts")] public bool AllowScripts = true;
        [JsonProperty("devMode")] public bool DevMode = true;

        public static string DataDir { get { return Platform.DataDir; } }
        public static string ConfigPath { get { return Platform.ConfigPath; } }
        public static string ToolsDir { get { return Platform.ToolsDir; } }
        public static string InstancesDir { get { return Platform.InstancesDir; } }

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
            Platform.SecureFile(ConfigPath);
        }

        static string NewToken()
        {
            var bytes = new byte[32];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            var sb = new System.Text.StringBuilder(64);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static void Chmod600(string path) { Platform.SecureFile(path); }
    }
}

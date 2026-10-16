// rhinomcp-bridge (Windows / .NET Framework 4.8; also builds and runs under Mono).
// stdio <-> HTTP bridge for the RhinoMCP plugin. Same behaviour as the Swift bridge in bridge/.
// stdin: newline-delimited JSON-RPC. stdout: one JSON line per response (nothing else, UTF-8 without BOM).
// stderr: logs. Only BCL is used. Kept to C# 5 syntax so the legacy in-box csc.exe can build it too.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace RhinoMcp.Bridge
{
    internal enum Kind { Ok, Accepted, Unauthorized, HttpError, ConnectionFailed }

    internal sealed class PostResult
    {
        public Kind Kind;
        public byte[] Data;
        public int Status;
        public string Why;
    }

    internal sealed class Instance
    {
        public int Pid;
        public int Port;
        public DateTime Mtime;
        public string PreferredHost; // remembered once a host worked
    }

    internal static class Program
    {
        const string BridgeVersion = "0.2.0";
        static readonly string[] SupportedProtocols = { "2025-11-25", "2025-06-18", "2025-03-26" };
        static readonly string[] Hosts = { "127.0.0.1", "localhost" };
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        static Stream stdout;
        static Stream stderr;
        static string dataDir;

        static Instance current;
        static bool? live;
        static bool initialized;

        const string StatusText = "Rhino is not reachable. Open Rhino 7 with the RhinoMCP plugin installed (run scripts\\windows\\install.ps1 or install the Yak package, then restart Rhino once). Once Rhino is running, call this tool again or retry your request; no restart of this client is needed.";

        static int Main()
        {
            stdout = Console.OpenStandardOutput();
            stderr = Console.OpenStandardError();
            dataDir = ResolveDataDir();
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 4;

            // Raw streams + explicit UTF-8 (no BOM): independent of the console code page.
            Stream stdin = Console.OpenStandardInput();
            StreamReader reader = new StreamReader(stdin, Utf8, false, 65536);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim(' ', '\t', '\r', '\n');
                if (trimmed.Length == 0) continue;
                try { Handle(trimmed); }
                catch (Exception e) { Log("unhandled error: " + e.Message); }
            }
            return 0;
        }

        static string ResolveDataDir()
        {
            string o = Environment.GetEnvironmentVariable("RHINOMCP_DATA_DIR");
            if (!string.IsNullOrEmpty(o)) return o;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RhinoMCP");
        }

        static void Log(string s)
        {
            try
            {
                byte[] b = Utf8.GetBytes("[rhinomcp-bridge] " + s + "\n");
                stderr.Write(b, 0, b.Length);
                stderr.Flush();
            }
            catch { }
        }

        // ---- output -------------------------------------------------------------------------------------------

        static void EmitObj(Dictionary<string, object> obj)
        {
            EmitRaw(Utf8.GetBytes(Json.Serialize(obj)));
        }

        static void EmitRaw(byte[] data)
        {
            // JSON strings never contain raw newlines, so any present are inter-token whitespace.
            byte[] outb = new byte[data.Length + 1];
            for (int i = 0; i < data.Length; i++)
            {
                byte c = data[i];
                outb[i] = (c == 10 || c == 13) ? (byte)32 : c;
            }
            outb[data.Length] = 10;
            stdout.Write(outb, 0, outb.Length);
            stdout.Flush();
        }

        // ---- discovery ----------------------------------------------------------------------------------------

        static bool PidAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(pid)) { return !p.HasExited; }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Exception) { return true; } // e.g. access denied: the process exists
        }

        static long ToLong(object o)
        {
            if (o is int) return (int)o;
            if (o is long) return (long)o;
            if (o is decimal) return (long)(decimal)o;
            if (o is double) return (long)(double)o;
            return -1;
        }

        static Dictionary<string, object> ReadJsonFile(string path)
        {
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                return Json.DeserializeObject(text) as Dictionary<string, object>;
            }
            catch { return null; }
        }

        static Instance Discover()
        {
            string dir = Path.Combine(dataDir, "instances");
            if (!Directory.Exists(dir)) return null;
            List<Instance> found = new List<Instance>();
            string[] files;
            try { files = Directory.GetFiles(dir, "*.json"); } catch { return null; }
            foreach (string f in files)
            {
                Dictionary<string, object> j = ReadJsonFile(f);
                if (j == null || !j.ContainsKey("pid") || !j.ContainsKey("port")) continue;
                long pid = ToLong(j["pid"]);
                long port = ToLong(j["port"]);
                if (pid <= 0 || port <= 0 || port > 65535) continue;
                if (!PidAlive((int)pid)) continue;
                DateTime m;
                try { m = File.GetLastWriteTimeUtc(f); } catch { m = DateTime.MinValue; }
                found.Add(new Instance { Pid = (int)pid, Port = (int)port, Mtime = m });
            }
            return found.OrderByDescending(i => i.Mtime).FirstOrDefault();
        }

        static string ReadToken()
        {
            Dictionary<string, object> j = ReadJsonFile(Path.Combine(dataDir, "config.json"));
            if (j == null) return null;
            object t;
            string s = j.TryGetValue("token", out t) ? t as string : null;
            return string.IsNullOrEmpty(s) ? null : s;
        }

        // ---- HTTP ---------------------------------------------------------------------------------------------

        static PostResult Post(string host, int port, string token, byte[] body)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://" + host + ":" + port + "/mcp");
            req.Method = "POST";
            req.Proxy = null; // never route loopback through a proxy
            req.Timeout = 600000;
            req.ReadWriteTimeout = 600000;
            req.KeepAlive = true;
            req.AllowAutoRedirect = false;
            req.ContentType = "application/json";
            req.Accept = "application/json, text/event-stream";
            req.Headers["Authorization"] = "Bearer " + token;
            req.ContentLength = body.Length;
            try
            {
                using (Stream rs = req.GetRequestStream()) { rs.Write(body, 0, body.Length); }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    byte[] d = ReadAll(resp);
                    int code = (int)resp.StatusCode;
                    if (code == 200) return new PostResult { Kind = Kind.Ok, Data = d, Status = code };
                    if (code == 202) return new PostResult { Kind = Kind.Accepted, Status = code };
                    return new PostResult { Kind = Kind.HttpError, Data = d, Status = code };
                }
            }
            catch (WebException we)
            {
                HttpWebResponse r = we.Response as HttpWebResponse;
                if (r != null)
                {
                    using (r)
                    {
                        int code = (int)r.StatusCode;
                        if (code == 401) return new PostResult { Kind = Kind.Unauthorized, Status = code };
                        return new PostResult { Kind = Kind.HttpError, Data = ReadAll(r), Status = code };
                    }
                }
                return new PostResult { Kind = Kind.ConnectionFailed, Why = we.Status + ": " + we.Message };
            }
            catch (Exception e)
            {
                return new PostResult { Kind = Kind.ConnectionFailed, Why = e.Message };
            }
        }

        static byte[] ReadAll(WebResponse r)
        {
            using (Stream s = r.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                if (s != null) s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        /// <summary>Tries 127.0.0.1 then localhost (the host that worked last time goes first).</summary>
        static PostResult PostToInstance(Instance inst, string token, byte[] body)
        {
            IEnumerable<string> order = Hosts;
            if (inst.PreferredHost != null) order = new[] { inst.PreferredHost }.Concat(Hosts.Where(h => h != inst.PreferredHost));
            PostResult last = null;
            foreach (string host in order)
            {
                last = Post(host, inst.Port, token, body);
                if (last.Kind != Kind.ConnectionFailed)
                {
                    inst.PreferredHost = host;
                    return last;
                }
                Log("connect to " + host + ":" + inst.Port + " failed (" + last.Why + ")");
            }
            return last;
        }

        // ---- fallback (no Rhino) ------------------------------------------------------------------------------

        static Dictionary<string, object> Result(object id, Dictionary<string, object> r)
        {
            return new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "result", r } };
        }

        static Dictionary<string, object> RpcError(object id, int code, string message)
        {
            return new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" }, { "id", id },
                { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } }
            };
        }

        static Dictionary<string, object> TextContent(string text, bool isError)
        {
            return new Dictionary<string, object>
            {
                { "content", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", text } } } },
                { "isError", isError }
            };
        }

        static Dictionary<string, object> Fallback(object id, string method, Dictionary<string, object> prms)
        {
            switch (method)
            {
                case "initialize":
                    object w;
                    string wanted = prms.TryGetValue("protocolVersion", out w) ? (w as string ?? "") : "";
                    string v = SupportedProtocols.Contains(wanted) ? wanted : "2025-06-18";
                    return Result(id, new Dictionary<string, object>
                    {
                        { "protocolVersion", v },
                        { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object> { { "listChanged", true } } } } },
                        { "serverInfo", new Dictionary<string, object> { { "name", "rhino-mcp" }, { "version", BridgeVersion + " (bridge, Rhino not running)" } } },
                        { "instructions", StatusText }
                    });
                case "ping":
                    return Result(id, new Dictionary<string, object>());
                case "tools/list":
                    Dictionary<string, object> tool = new Dictionary<string, object>
                    {
                        { "name", "rhino_status" },
                        { "description", "Rhino is not reachable right now. Call this to see how to connect: open Rhino 7 with the RhinoMCP plugin. Re-checks on every call." },
                        { "inputSchema", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", new Dictionary<string, object>() },
                                { "additionalProperties", false }
                            } },
                        { "annotations", new Dictionary<string, object> { { "readOnlyHint", true } } }
                    };
                    return Result(id, new Dictionary<string, object> { { "tools", new object[] { tool } } });
                case "tools/call":
                    object n;
                    string name = prms.TryGetValue("name", out n) ? (n as string ?? "") : "";
                    if (name == "rhino_status") return Result(id, TextContent(StatusText, false));
                    return Result(id, TextContent("Tool '" + name + "' is unavailable because Rhino is not running. " + StatusText, true));
                case "resources/list":
                    return Result(id, new Dictionary<string, object> { { "resources", new object[0] } });
                case "prompts/list":
                    return Result(id, new Dictionary<string, object> { { "prompts", new object[0] } });
                default:
                    return RpcError(id, -32601, "Method not found (Rhino is not running): " + method);
            }
        }

        // ---- main loop ----------------------------------------------------------------------------------------

        static void SetMode(bool isLive)
        {
            if (live.HasValue && live.Value != isLive && initialized)
            {
                EmitObj(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "method", "notifications/tools/list_changed" } });
            }
            live = isLive;
        }

        /// <summary>Returns null when no Rhino is reachable; otherwise the final result.</summary>
        static PostResult Forward(byte[] body)
        {
            int attempts = 0;
            string lastFailure = "";
            while (attempts < 2)
            {
                if (current == null) current = Discover();
                if (current == null) return null;
                string token = ReadToken();
                if (token == null) { Log("no token in config.json"); return null; }
                PostResult r = PostToInstance(current, token, body);
                if (r.Kind == Kind.ConnectionFailed)
                {
                    lastFailure = r.Why;
                    Log("connection to port " + current.Port + " failed (" + r.Why + "); re-discovering");
                    current = null;
                    attempts++;
                }
                else if (r.Kind == Kind.Unauthorized)
                {
                    Log("401 from port " + current.Port + "; re-discovering");
                    lastFailure = "unauthorized";
                    current = null;
                    attempts++;
                }
                else return r;
            }
            Log("giving up: " + lastFailure);
            return null;
        }

        static void Handle(string trimmed)
        {
            byte[] body = Utf8.GetBytes(trimmed);
            object parsed;
            try { parsed = Json.DeserializeObject(trimmed); }
            catch (Exception) { EmitObj(RpcError(null, -32700, "Parse error")); return; }

            Dictionary<string, object> msg = parsed as Dictionary<string, object>;
            object o;
            string method = (msg != null && msg.TryGetValue("method", out o)) ? (o as string ?? "") : "";
            object id = null;
            bool hasId = msg != null && msg.TryGetValue("id", out id) && id != null;
            Dictionary<string, object> prms = (msg != null && msg.TryGetValue("params", out o)) ? (o as Dictionary<string, object>) : null;
            if (prms == null) prms = new Dictionary<string, object>();

            if (method == "notifications/initialized") initialized = true;
            if (method == "initialize") initialized = false;

            PostResult r = Forward(body);
            if (r != null)
            {
                switch (r.Kind)
                {
                    case Kind.Ok:
                        SetMode(true);
                        EmitRaw(r.Data);
                        break;
                    case Kind.Accepted:
                        SetMode(true);
                        break;
                    case Kind.HttpError:
                        SetMode(true);
                        if (hasId)
                            EmitObj(RpcError(id, -32603, "Rhino returned HTTP " + r.Status + ": " + Utf8.GetString(r.Data ?? new byte[0])));
                        break;
                }
                return;
            }

            // Rhino unreachable: answer ourselves.
            SetMode(false);
            if (!hasId) return; // notifications get no output
            EmitObj(Fallback(id, method, prms));
        }
    }
}

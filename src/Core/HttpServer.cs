using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RhinoMCP
{
    public sealed class HttpServer
    {
        const int MaxBody = 8 * 1024 * 1024;
        readonly Config _cfg;
        HttpListener _listener;
        byte[] _tokenBytes;

        public int Port { get; private set; }
        /// <summary>Host name the listener is bound to: "127.0.0.1", or "localhost" after the Windows URL-ACL fallback.</summary>
        public string Host { get; private set; }
        public string BaseUrl { get { return "http://" + (Host ?? "127.0.0.1") + ":" + Port; } }
        public bool Running { get { return _listener != null && _listener.IsListening; } }
        public DateTime StartedAt { get; private set; }
        string DiscoveryPath { get { return Path.Combine(Config.InstancesDir, Process.GetCurrentProcess().Id + ".json"); } }

        public HttpServer(Config cfg) { _cfg = cfg; _tokenBytes = Encoding.UTF8.GetBytes(cfg.Token); }

        public void Start()
        {
            if (Running) return;
            Exception last = null;
            for (int port = _cfg.Port; port < _cfg.Port + 20; port++)
            {
                string host;
                var l = TryListen(port, out host, ref last);
                if (l == null) continue;
                _listener = l; Port = port; Host = host; StartedAt = DateTime.UtcNow;
                WriteDiscovery();
                Task.Run(() => AcceptLoop(l));
                return;
            }
            throw new InvalidOperationException("No free port in " + _cfg.Port + ".." + (_cfg.Port + 19) + ": " + (last == null ? "" : last.Message));
        }

        /// <summary>
        /// Binds http://127.0.0.1:port/. On Windows a non-admin user may not register that prefix (HttpListenerException, native error 5,
        /// "Access is denied"; it needs a URL ACL from `netsh http add urlacl`), while http://localhost:port/ is allowed for the current user.
        /// So on access-denied we retry with localhost. On Mac (Mono) the first prefix always works and behaviour is unchanged.
        /// </summary>
        static HttpListener TryListen(int port, out string host, ref Exception last)
        {
            host = null;
            foreach (string h in new[] { "127.0.0.1", "localhost" })
            {
                var l = new HttpListener();
                l.Prefixes.Add("http://" + h + ":" + port + "/");
                try { l.Start(); host = h; return l; }
                catch (HttpListenerException ex)
                {
                    last = ex; try { l.Close(); } catch (Exception) { }
                    if (ex.ErrorCode == 5 && h == "127.0.0.1" && Platform.IsWindows) continue;   // access denied -> try localhost
                    return null;
                }
                catch (Exception ex) { last = ex; try { l.Close(); } catch (Exception) { } return null; }
            }
            return null;
        }

        public void Stop()
        {
            var l = _listener; _listener = null;
            if (l != null)
            {
                try { l.Stop(); } catch (Exception) { }
                try { l.Close(); } catch (Exception) { }
            }
            try { if (File.Exists(DiscoveryPath)) File.Delete(DiscoveryPath); } catch (Exception) { }
        }

        void WriteDiscovery()
        {
            try
            {
                Directory.CreateDirectory(Config.InstancesDir);
                var o = new JObject
                {
                    { "pid", Process.GetCurrentProcess().Id }, { "port", Port }, { "host", Host ?? "127.0.0.1" },
                    { "version", RhinoMCPPlugin.VersionString }, { "startedAt", StartedAt.ToString("o") }
                };
                File.WriteAllText(DiscoveryPath, o.ToString(Formatting.None));
            }
            catch (Exception) { }
        }

        async Task AcceptLoop(HttpListener l)
        {
            while (l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await l.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { break; }
                var c = ctx;
                var ignore = Task.Run(() => Handle(c));
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                string path = req.Url.AbsolutePath;

                // DNS-rebinding guard; both loopback spellings are accepted whichever prefix was bound.
                if (!Platform.IsLoopbackHost(req.Headers["Host"], Port))
                { Send(ctx, 403, "{\"error\":\"forbidden host\"}"); return; }
                if (req.Headers["Origin"] != null)
                { Send(ctx, 403, "{\"error\":\"origin not allowed\"}"); return; }

                if (path == "/health")
                {
                    if (req.HttpMethod != "GET") { Send(ctx, 405, "{\"error\":\"method not allowed\"}"); return; }
                    Send(ctx, 200, new JObject { { "ok", true }, { "version", RhinoMCPPlugin.VersionString }, { "port", Port } }.ToString(Formatting.None));
                    return;
                }
                if (path != "/mcp") { Send(ctx, 404, "{\"error\":\"not found\"}"); return; }

                if (!Authorized(req.Headers["Authorization"]))
                {
                    ctx.Response.AddHeader("WWW-Authenticate", "Bearer");
                    Send(ctx, 401, "{\"error\":\"unauthorized\"}");
                    return;
                }
                if (req.HttpMethod == "GET")
                {
                    ctx.Response.AddHeader("Allow", "POST");
                    Send(ctx, 405, "{\"error\":\"this server does not offer an SSE stream; use POST\"}");
                    return;
                }
                if (req.HttpMethod != "POST") { Send(ctx, 405, "{\"error\":\"method not allowed\"}"); return; }
                if (req.ContentLength64 > MaxBody) { Send(ctx, 413, "{\"error\":\"body too large\"}"); return; }

                string body;
                using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
                if (body.Length > MaxBody) { Send(ctx, 413, "{\"error\":\"body too large\"}"); return; }

                string reply = McpProtocol.Handle(body);
                if (reply == null) { Send(ctx, 202, null); return; }
                Send(ctx, 200, reply);
            }
            catch (Exception ex)
            {
                try { Send(ctx, 500, new JObject { { "error", ex.Message } }.ToString(Formatting.None)); } catch (Exception) { }
            }
        }

        bool Authorized(string header)
        {
            if (header == null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
            var given = Encoding.UTF8.GetBytes(header.Substring(7).Trim());
            int diff = given.Length ^ _tokenBytes.Length;
            for (int i = 0; i < _tokenBytes.Length; i++)
                diff |= (i < given.Length ? given[i] : (byte)0) ^ _tokenBytes[i];
            return diff == 0;
        }

        static void Send(HttpListenerContext ctx, int status, string json)
        {
            var resp = ctx.Response;
            resp.StatusCode = status;
            if (json == null) { resp.ContentLength64 = 0; resp.Close(); return; }
            var bytes = Encoding.UTF8.GetBytes(json);
            resp.ContentType = "application/json; charset=utf-8";
            resp.ContentLength64 = bytes.Length;
            resp.OutputStream.Write(bytes, 0, bytes.Length);
            resp.Close();
        }
    }
}

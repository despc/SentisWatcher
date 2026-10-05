using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using NLog;
using SentisWatcher.Storage;

namespace SentisWatcher.Web
{
    /// <summary>
    /// The web view: a page (embedded in the plugin) and a read-only JSON API over the day files, on
    /// 127.0.0.1 only. Requests are answered on the thread pool, never on the game thread.
    /// </summary>
    public sealed class WebServer : IDisposable
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly HttpListener _listener = new HttpListener();
        private readonly WebData _data;
        private readonly Dictionary<string, string> _resources;
        private Thread _thread;

        public int Port { get; }

        /// <summary>On every address of the machine (the network), not only 127.0.0.1.</summary>
        public bool Lan { get; }

        public string Url => "http://127.0.0.1:" + Port + "/";
        private string Prefix => Lan ? "http://+:" + Port + "/" : Url;

        public WebServer(WatcherStore store, int port, bool lan = false)
        {
            Port = port;
            Lan = lan;
            _data = new WebData(store) { LiveName = LiveName, LiveBody = LiveBody };
            // the build names embedded files Web/lib\x.js: one separator
            _resources = typeof(WebServer).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith("Web/"))
                .ToDictionary(n => n.Substring(4).Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);
            _listener.Prefixes.Add(Prefix);
        }

        /// <summary>A player's or an entity's name from the running game (web thread: only reads, and any failure is no name).</summary>
        private static string LiveName(long id)
        {
            try
            {
                var identity = Sandbox.Game.World.MySession.Static?.Players?.TryGetIdentity(id);
                if (identity != null) return identity.DisplayName;
                if (!Sandbox.Game.Entities.MyEntities.TryGetEntityById(id, out var entity)) return null;
                // a block by its name, or its type and subtype when it has none
                if (entity is Sandbox.Game.Entities.MyCubeBlock block) return Recording.InventorySweep.BlockName(block);
                return entity.DisplayName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The character an identity has now, or 0 (web thread: only reads).</summary>
        private static long LiveBody(long identity)
        {
            try
            {
                return Sandbox.Game.World.MySession.Static?.Players?.TryGetIdentity(identity)?.Character?.EntityId ?? 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public void Start()
        {
            _listener.Start();
            _thread = new Thread(Loop) { IsBackground = true, Name = "SentisWatcher web" };
            _thread.Start();
            Log.Info("SentisWatcher: web view at " + (Lan ? "http://" + Environment.MachineName + ":" + Port + "/ (every address of the machine)" : Url));
        }

        /// <summary>
        /// The web view, on the network when asked and Windows allows it. A server not run as administrator may listen on
        /// every address only after the address is reserved for it (netsh http add urlacl); without that the listener
        /// refuses (access denied), and the view goes on at 127.0.0.1 with the command to run in the log.
        /// </summary>
        public static WebServer StartFor(WatcherStore store, int port, bool lan)
        {
            if (lan)
            {
                var server = new WebServer(store, port, true);
                try
                {
                    server.Start();
                    return server;
                }
                catch (HttpListenerException e) when (e.ErrorCode == 5)
                {
                    server.Dispose();
                    Log.Error("SentisWatcher: the web view may not listen on the network: the address is not reserved for this " +
                              "server. Run as administrator once: netsh http add urlacl url=http://+:" + port + "/ user=" +
                              Environment.UserDomainName + "\\" + Environment.UserName + " (watcher_lan.ps1 by the server does it and opens " +
                              "the firewall port), then restart. Until then the view is on 127.0.0.1 only.");
                }
            }
            var local = new WebServer(store, port);
            local.Start();
            return local;
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception)
            {
                // already down
            }
        }

        private void Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception)
                {
                    return;     // stopped
                }
                ThreadPool.QueueUserWorkItem(_ => Handle(context));
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var response = context.Response;
            try
            {
                var request = context.Request;
                // on the network (WebLan) any address may ask; otherwise only this machine
                if (!Lan && !IPAddress.IsLoopback(request.RemoteEndPoint.Address))
                {
                    Send(response, 403, "text/plain", "local only");
                    return;
                }
                if (request.HttpMethod != "GET")
                {
                    Send(response, 405, "text/plain", "read only");
                    return;
                }
                var path = request.Url.AbsolutePath.TrimEnd('/');
                if (path.StartsWith("/api/"))
                {
                    Http.Live.GameMs = 0;
                    var result = Api(path.Substring(5), request.QueryString);
                    // what the call took of the game thread (the calls that read the game as it is now)
                    if (Http.Live.GameMs > 0)
                    {
                        response.AddHeader("X-Game-Ms", Http.Live.GameMs.ToString("0.###", CultureInfo.InvariantCulture));
                        response.AddHeader("X-Game-Frames", Http.Live.GameFrames.ToString(CultureInfo.InvariantCulture));
                        response.AddHeader("X-Game-Frame-Ms", Http.Live.GameFrameMs.ToString("0.###", CultureInfo.InvariantCulture));
                    }
                    if (result == null)
                    {
                        Send(response, 404, "application/json", "{\"error\":\"no such call\"}");
                        return;
                    }
                    response.AddHeader("Cache-Control", "no-store");
                    SendJson(response, result);
                    return;
                }
                var file = path.Length == 0 ? "index.html" : path.TrimStart('/');
                if (!_resources.TryGetValue(file, out var resource))
                {
                    Send(response, 404, "text/plain", "not found");
                    return;
                }
                using (var stream = typeof(WebServer).Assembly.GetManifestResourceStream(resource))
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    // asked again each time: after an update the browser would run the old script from its cache
                    response.AddHeader("Cache-Control", "no-cache");
                    Send(response, 200, ContentType(file), memory.ToArray());
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "SentisWatcher: web request failed");
                try { Send(response, 500, "application/json", JsonConvert.SerializeObject(new { error = e.Message })); }
                catch (Exception) { }
            }
        }

        /// <summary>The API: the call's name and its query; null when there is no such call.</summary>
        public object Api(string call, System.Collections.Specialized.NameValueCollection q)
        {
            long L(string name, long fallback = 0) => long.TryParse(q[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            double D(string name) => double.Parse(q[name] ?? "0", CultureInfo.InvariantCulture);
            var now = Clock.Now;
            var to = L("to", now);
            var from = L("from", to - 3_600_000L);
            if (to - from > 7 * 86_400_000L) from = to - 7 * 86_400_000L;     // a week at most per call
            switch (call)
            {
                case "days": return _data.Days();
                case "search": return _data.Search(q["q"] ?? "", from, to, q["inventories"] == "1");
                case "track": return _data.Track(q["kind"] == "grid" ? "grid" : "player", L("id"), from, to);
                case "events": return _data.Events(L("id"), from, to);
                case "near": return _data.Near(D("x"), D("y"), D("z"), Math.Min(D("r"), 100_000), from, to);
                case "inventory": return _data.Inventory(q["kind"] ?? "entity", L("id"), L("t", now));
                case "alerts": return _data.Alerts(from, to);
                case "objects": return _data.Objects(from, to, q["q"], withInventory: q["inventories"] == "1");
                case "moment":
                    return _data.Moment(L("t", now), L("window", 300_000), 2000,
                        q["from"] == null ? (long?)null : from, q["to"] == null ? (long?)null : to);
                case "activity": return _data.Activity(from, to, (int)L("buckets", 400));
                case "world": return _data.World(L("t", now));
                case "hotspot": return _data.Hotspot(from, to);
                case "ledger": return _data.Ledger(q["kind"], L("id"), from, to);
                case "anomalies": return _data.Anomalies(from, to);
                case "perf": return _data.Perf(from, to, (int)L("points", 1500));
                case "damage": return _data.Damage(from, to, q["relation"] ?? "enemy", (int)L("buckets", 200), (int)L("recent", 300));
                case "load": return _data.Load(from, to, (int)Math.Min(L("top", 100), 1000));
                case "loadseries": return _data.LoadSeries(from, to, q["kind"], (int)Math.Min(L("top", 8), 60));
                case "holdings": return _data.Holdings(q["kind"], L("id"), L("t", now));
                case "relief": return Http.Relief.Of(L("id"), q["name"]);
                case "terrain": return Http.Relief.Patch(L("id"), q["name"], D("x"), D("y"), D("z"), D("size"), (int)L("n", 129));
                case "online": return Http.Live.Online();
                case "structures": return Http.Live.Structures();
                case "structure": return Http.Live.Structure(L("id"));
                case "now": return new { now, offsetMinutes = Clock.OffsetMinutes(now), zone = Clock.Zone(now) };
                default: return null;
            }
        }

        private static string ContentType(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "text/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".txt": return "text/plain; charset=utf-8";
                case ".svg": return "image/svg+xml";
                default: return "application/octet-stream";
            }
        }

        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>
        /// An answer of the API written straight into the response as it is serialized: the answers of the charts run
        /// to hundreds of kilobytes, and as a string and then its bytes each was two large arrays - garbage that only a
        /// full collection of the server's heap takes away.
        /// </summary>
        private static void SendJson(HttpListenerResponse response, object result)
        {
            response.StatusCode = 200;
            response.ContentType = "application/json; charset=utf-8";
            response.SendChunked = true;
            using (var writer = new StreamWriter(response.OutputStream, Utf8, 16 * 1024))
                JsonSerializer.CreateDefault().Serialize(writer, result);
        }

        private static void Send(HttpListenerResponse response, int status, string type, string text) =>
            Send(response, status, type.Contains("charset") ? type : type + "; charset=utf-8", Encoding.UTF8.GetBytes(text));

        private static void Send(HttpListenerResponse response, int status, string type, byte[] body)
        {
            response.StatusCode = status;
            response.ContentType = type;
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body, 0, body.Length);
            response.OutputStream.Close();
        }
    }
}

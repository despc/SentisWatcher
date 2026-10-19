using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// What the server's public site may show (a page outside, on the internet): who is online, and the server's load and
    /// simulation speed lately - on a port of its own, every address of the machine, nothing else of the records. Only
    /// names and faction tags go out: no positions, grids, identities or inventories (the web view has those, and stays
    /// local). Answers are kept for a while (online 10 s, the charts a minute), so asking often costs the game nothing more.
    /// </summary>
    public sealed class PublicServer : IDisposable
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>How far back the charts may go, hours.</summary>
        public const int MostHours = 24;

        private readonly HttpListener _listener = new HttpListener();
        private readonly WebData _data;
        private readonly string _origin;
        private Thread _thread;

        public int Port { get; }

        private PublicServer(WatcherStore store, int port, string origin, bool everywhere)
        {
            Port = port;
            _origin = string.IsNullOrWhiteSpace(origin) ? "*" : origin.Trim();
            _data = new WebData(store);
            _listener.Prefixes.Add(everywhere ? "http://+:" + port + "/" : "http://127.0.0.1:" + port + "/");
        }

        /// <summary>
        /// On every address of the machine when Windows allows it (the address reserved with netsh http add urlacl, as for
        /// the web view on the network), else on 127.0.0.1 with the command to run in the log - a site on this machine, or
        /// behind a proxy here, reaches it there too.
        /// </summary>
        public static PublicServer StartFor(WatcherStore store, int port, string origin)
        {
            var server = new PublicServer(store, port, origin, true);
            try
            {
                server.Start();
                Log.Info("SentisWatcher: public stats at http://" + Environment.MachineName + ":" + port + "/online and /perf (every address of the machine)");
                return server;
            }
            catch (HttpListenerException e) when (e.ErrorCode == 5)
            {
                server.Dispose();
                Log.Error("SentisWatcher: the public stats may not listen on the network: the address is not reserved for this server. " +
                          "Run as administrator once: netsh http add urlacl url=http://+:" + port + "/ user=" + Environment.UserDomainName + "\\" +
                          Environment.UserName + " and open the port in the firewall, then restart. Until then they are on 127.0.0.1 only.");
            }
            var local = new PublicServer(store, port, origin, false);
            local.Start();
            Log.Info("SentisWatcher: public stats at http://127.0.0.1:" + port + "/online and /perf");
            return local;
        }

        private void Start()
        {
            _listener.Start();
            _thread = new Thread(Loop) { IsBackground = true, Name = "SentisWatcher public" };
            _thread.Start();
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

        private readonly object _lock = new object();
        private (long At, string Json) _online;
        private readonly Dictionary<int, (long At, string Json)> _perf = new Dictionary<int, (long, string)>();

        private void Handle(HttpListenerContext context)
        {
            var response = context.Response;
            try
            {
                var request = context.Request;
                response.AddHeader("Access-Control-Allow-Origin", _origin);
                response.AddHeader("Access-Control-Allow-Methods", "GET, OPTIONS");
                if (request.HttpMethod == "OPTIONS") { Send(response, 204, ""); return; }
                if (request.HttpMethod != "GET") { Send(response, 405, "{\"error\":\"read only\"}"); return; }
                var path = request.Url.AbsolutePath.TrimEnd('/');
                var now = Clock.Now;
                string json;
                switch (path)
                {
                    case "/online":
                        lock (_lock)
                        {
                            if (_online.Json == null || now - _online.At > 10_000) _online = (now, JsonConvert.SerializeObject(Online(now)));
                            json = _online.Json;
                        }
                        response.AddHeader("Cache-Control", "public, max-age=10");
                        break;
                    case "/perf":
                        var hours = int.TryParse(request.QueryString["hours"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? h : 3;
                        hours = Math.Max(1, Math.Min(hours, MostHours));
                        lock (_lock)
                        {
                            if (!_perf.TryGetValue(hours, out var kept) || now - kept.At > 60_000) _perf[hours] = kept = (now, JsonConvert.SerializeObject(Perf(now, hours)));
                            json = kept.Json;
                        }
                        response.AddHeader("Cache-Control", "public, max-age=60");
                        break;
                    default:
                        Send(response, 404, "{\"error\":\"only /online and /perf\"}");
                        return;
                }
                Send(response, 200, json);
            }
            catch (Exception e)
            {
                Log.Warn(e, "SentisWatcher: public stats request failed");
                try { Send(response, 500, "{\"error\":\"failed\"}"); }
                catch (Exception) { }
            }
        }

        /// <summary>Who is online: names, faction tags, and whether it is a bot (SentisAi's always say so in their names).</summary>
        private static object Online(long now)
        {
            var list = (Http.Live.Online() as IEnumerable<Dictionary<string, object>> ?? Enumerable.Empty<Dictionary<string, object>>()).ToList();
            var players = list.Select(p =>
            {
                var name = p.TryGetValue("name", out var n) ? n as string ?? "" : "";
                var real = p.TryGetValue("real", out var r) && r is bool b && b;
                return new { name, faction = p.TryGetValue("faction", out var f) ? f as string : null, bot = !real || name.StartsWith("[BOT]", StringComparison.Ordinal) };
            }).ToList();
            return new { at = now, players = players.Count(p => !p.bot), bots = players.Count(p => p.bot), online = players };
        }

        /// <summary>
        /// The load lately, a point a minute: the simulation speed (1 is full speed); the load of the game thread, the one core
        /// the simulation runs on - its average frame (ms) and the part of a tick it takes (%: 16.7 ms is the whole of a tick
        /// at 60 a second; over 100 the simulation slows down) and its worst frame of the minute; the players online (bots too).
        /// </summary>
        private object Perf(long now, int hours)
        {
            var from = now - hours * 3_600_000L;
            var perf = _data.Perf(from, now, hours * 60) as Dictionary<string, object>;
            var points = (perf?["points"] as IEnumerable<Dictionary<string, object>> ?? Enumerable.Empty<Dictionary<string, object>>()).Select(p =>
            {
                var frame = p["frame"] is double ms ? ms : (double?)null;
                return new
                {
                    t = p["t"],
                    sim = p["sim"],
                    load = frame.HasValue ? Math.Round(frame.Value / (1000.0 / 60) * 100, 1) : (double?)null,
                    frame,
                    frameMax = p["frameMax"],
                    players = p["players"],
                };
            }).ToList();
            return new { from, to = now, minutes = 1, points };
        }

        private static void Send(HttpListenerResponse response, int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }
    }
}

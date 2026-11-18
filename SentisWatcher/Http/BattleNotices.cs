using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using NLog;
using SentisWatcher.Storage;

namespace SentisWatcher.Web
{
    /// <summary>
    /// The players of a battle are told in the chat when its report is on the site: the battle over and published
    /// (<see cref="PublicBattleApi.DelayMinutes"/> after its end), each of its players once - only those who took part in
    /// it (shot or were hit), and only the battles the site has (between players). The battles published before the
    /// first look (a restart) are taken as told: nobody gets a heap of old notices. <c>!br</c> opens a player's last one
    /// (<see cref="Latest"/>, <see cref="Url"/>).
    /// </summary>
    public sealed class BattleNotices : IDisposable
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private const string Tail = "Полные результаты - команда \"!br\" в чат";
        private const int CheckSeconds = 60;
        private const long WindowMs = 6 * 3600000L;

        private readonly Func<long, long, object> _read;
        private readonly Func<long, long, string, object> _report;
        private readonly Func<long> _clock;
        private readonly int _delayMinutes;
        private readonly Action<long, string> _tell;
        private readonly Dictionary<string, long> _told = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly object _lock = new object();
        private bool _primed;
        private Timer _timer;

        /// <param name="read">The battles the site is given, for a range of time (<see cref="PublicServer.Battles"/>).</param>
        /// <param name="report">One of them with its report: the outcome and the MVP (<see cref="PublicServer.BattleReport"/>).</param>
        /// <param name="tell">Tells a player (an identity id) a message; any thread.</param>
        public BattleNotices(Func<long, long, object> read, Func<long, long, string, object> report, int delayMinutes, Action<long, string> tell, Func<long> clock = null)
        {
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _report = report ?? throw new ArgumentNullException(nameof(report));
            _tell = tell ?? throw new ArgumentNullException(nameof(tell));
            _delayMinutes = delayMinutes;
            _clock = clock ?? (() => Clock.Now);
        }

        public void Start() => _timer = new Timer(_ => Tick(), null, CheckSeconds * 1000, CheckSeconds * 1000);

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private void Tick()
        {
            try
            {
                Check();
            }
            catch (Exception e)
            {
                Log.Error(e, "SentisWatcher: the battle notices failed");
            }
        }

        /// <summary>One look: the battles published since the last one, their players told. Returns who was told what.</summary>
        public List<(long Player, string Text)> Check()
        {
            lock (_lock)
            {
                var before = _clock() - _delayMinutes * 60000L;
                var from = before - WindowMs;
                var raw = JObject.FromObject(_read(from, before));
                var told = new List<(long, string)>();
                foreach (var battle in raw["battles"] as JArray ?? new JArray())
                {
                    if (!PublicBattleApi.Published(battle, before)) continue;
                    var ended = (long)battle["endedAt"];
                    var id = (string)battle["id"];
                    if (id == null || ended < from || _told.ContainsKey(id)) continue;
                    _told[id] = ended;
                    if (!_primed) continue;
                    var players = PlayerIds(battle).Distinct().ToList();
                    if (players.Count == 0) continue;
                    var text = Text(JObject.FromObject(_report(from, before, id))["report"]);
                    foreach (var player in players) told.Add((player, text));
                }
                _primed = true;
                foreach (var old in _told.Where(t => t.Value < from - 3600000L).Select(t => t.Key).ToList()) _told.Remove(old);
                foreach (var (player, text) in told) _tell(player, text);
                return told;
            }
        }

        /// <summary>
        /// "Сражение окончено, Победитель - X, MVP - Y. Полные результаты - ..." from a battle's report: the winning side (a
        /// player or a faction), a draw, or none known; the MVP (several on a tie), or left out when there is none. The names
        /// as the site shows them (an unknown one is no number).
        /// </summary>
        public static string Text(JToken report)
        {
            var outcome = report?["summary"]?["outcome"];
            var winner = "не определён";
            if ((string)outcome?["status"] == "draw") winner = "ничья";
            else if (outcome?["winner"] != null && outcome["winner"].Type != JTokenType.Null)
            {
                var side = (outcome["sides"] as JArray ?? new JArray()).FirstOrDefault(s => (string)s["id"] == (string)outcome["winner"]);
                var name = PublicBattleProjection.Text(side?["name"]);
                if (name.Length > 0) winner = name;
            }
            var mvp = report?["mvp"];
            var names = (string)mvp?["status"] == "winner" || (string)mvp?["status"] == "tie"
                ? (mvp["players"] as JArray ?? new JArray()).Select(p => PublicBattleProjection.Text(p["name"])).Where(n => n.Length > 0).Take(3).ToList()
                : new List<string>();
            return "Сражение окончено, Победитель - " + winner + (names.Count > 0 ? ", MVP - " + string.Join(", ", names) : "") + ". " + Tail;
        }

        private static IEnumerable<long> PlayerIds(JToken battle) =>
            (battle["playerIds"] as JArray ?? new JArray()).Select(id => long.TryParse((string)id, out var value) ? value : 0).Where(id => id != 0);

        /// <summary>
        /// The player's last battle on the site (the public id and when it ended), the last day first, then the week; or
        /// null. Reads the records: not on the game thread.
        /// </summary>
        public static (string Id, long EndedAt)? Latest(Func<long, long, object> read, long identity, int delayMinutes, long now)
        {
            var before = now - delayMinutes * 60000L;
            foreach (var hours in new[] { 24, PublicBattleApi.MaxHours })
            {
                var raw = JObject.FromObject(read(before - hours * 3600000L, before));
                var last = (raw["battles"] as JArray ?? new JArray())
                    .Where(b => PublicBattleApi.Published(b, before) && PlayerIds(b).Contains(identity))
                    .OrderByDescending(b => (long)b["endedAt"]).FirstOrDefault();
                if (last != null) return (PublicBattleProjection.Id((string)last["id"]), (long)last["endedAt"]);
            }
            return null;
        }

        /// <summary>
        /// The battle's page on the site: the page with the battle and the list's hours that hold it (the site lists the
        /// last 24, 72 or 168 hours of what is published).
        /// </summary>
        public static string Url(string page, string id, long endedAt, int delayMinutes, long now)
        {
            var age = now - delayMinutes * 60000L - endedAt;
            var hours = age <= 24 * 3600000L ? 24 : age <= 72 * 3600000L ? 72 : PublicBattleApi.MaxHours;
            return page + (page.Contains("?") ? "&" : "?") + "hours=" + hours + "&battle=" + id;
        }
    }
}

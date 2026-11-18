using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SentisWatcher.Web;
using Xunit;

namespace SentisWatcher.Tests
{
    public sealed class BattleNoticesTests
    {
        private const long Hour = 3600000L;
        private const long Start = 1800000000000;
        private const int Delay = 15;

        private static JObject Battle(string id, long endedAt, bool ongoing = false, params long[] players) => JObject.FromObject(new
        {
            id, endedAt = ongoing ? (long?)null : endedAt, ongoing, playerIds = players.Select(p => p.ToString()).ToArray(),
        });

        private static JObject Report(string status = "winner", string winner = "s1", string winnerName = "[ZRG] Zerg", string mvpStatus = "winner", params string[] mvp) =>
            JObject.FromObject(new
            {
                report = new
                {
                    summary = new { outcome = new { status, winner, sides = new[] { new { id = "s1", name = winnerName }, new { id = "s2", name = "[SVP] Vanguard" } } } },
                    mvp = new { status = mvpStatus, players = mvp.Select(n => new { name = n }).ToArray() },
                },
            });

        private sealed class World
        {
            public long Now = Start;
            public readonly List<JObject> Battles = new List<JObject>();
            public readonly Dictionary<string, JObject> Reports = new Dictionary<string, JObject>();
            public readonly List<(long, string)> Told = new List<(long, string)>();

            public BattleNotices Notices() => new BattleNotices(
                (from, to) => new JObject { ["battles"] = new JArray(Battles.Where(b => b["endedAt"].Type == JTokenType.Null || ((long)b["endedAt"] >= from && (long)b["endedAt"] <= to + 120000))) },
                (from, to, id) => Reports.TryGetValue(id, out var r) ? r : Report(),
                Delay, (player, text) => Told.Add((player, text)), () => Now);
        }

        [Fact]
        public void The_battles_published_before_the_first_look_are_not_told()
        {
            var world = new World();
            world.Battles.Add(Battle("1:1", Start - Hour, players: new long[] { 11, 12 }));
            var notices = world.Notices();
            Assert.Empty(notices.Check());
            Assert.Empty(world.Told);
        }

        [Fact]
        public void A_battle_published_later_is_told_to_its_players_once_with_its_winner_and_mvp()
        {
            var world = new World();
            var notices = world.Notices();
            notices.Check();
            // over a minute ago: not yet published (15 minutes after the end)
            world.Battles.Add(Battle("2:1", world.Now - 60000, players: new long[] { 21, 22 }));
            world.Reports["2:1"] = Report(mvp: "Alice");
            Assert.Empty(notices.Check());
            world.Now += 16 * 60000;
            var told = notices.Check();
            Assert.Equal(new long[] { 21, 22 }, told.Select(t => t.Player).OrderBy(p => p));
            Assert.All(told, t => Assert.Equal("Сражение окончено, Победитель - [ZRG] Zerg, MVP - Alice. Полные результаты - команда \"!br\" в чат", t.Text));
            world.Now += 60000;
            Assert.Empty(notices.Check());
            Assert.Equal(2, world.Told.Count);
        }

        [Fact]
        public void A_battle_still_going_on_is_not_told()
        {
            var world = new World();
            var notices = world.Notices();
            notices.Check();
            world.Battles.Add(Battle("3:1", 0, ongoing: true, players: new long[] { 31 }));
            world.Now += Hour;
            Assert.Empty(notices.Check());
        }

        [Fact]
        public void A_draw_an_unknown_outcome_a_tie_of_mvps_and_an_unknown_name_read_as_the_site_shows_them()
        {
            Assert.StartsWith("Сражение окончено, Победитель - ничья, MVP - Alice, Bob.", BattleNotices.Text(Report("draw", null, mvpStatus: "tie", mvp: new[] { "Alice", "Bob" })["report"]));
            Assert.StartsWith("Сражение окончено, Победитель - не определён. ", BattleNotices.Text(Report("unknown", null, mvpStatus: "none")["report"]));
            Assert.StartsWith("Сражение окончено, Победитель - Неизвестный участник, MVP - Alice.",
                BattleNotices.Text(Report(winnerName: "144115188075855928", mvp: "Alice")["report"]));
        }

        [Fact]
        public void The_page_lists_the_hours_that_hold_the_battle()
        {
            const string page = "https://sentis-project.online/battles.html";
            var now = Start;
            var published = now - Delay * 60000L;
            Assert.Equal(page + "?hours=24&battle=abc", BattleNotices.Url(page, "abc", published - 2 * Hour, Delay, now));
            Assert.Equal(page + "?hours=72&battle=abc", BattleNotices.Url(page, "abc", published - 30 * Hour, Delay, now));
            Assert.Equal(page + "?hours=168&battle=abc", BattleNotices.Url(page, "abc", published - 100 * Hour, Delay, now));
            Assert.Equal(page + "?x=1&hours=24&battle=abc", BattleNotices.Url(page + "?x=1", "abc", published - Hour, Delay, now));
        }

        [Fact]
        public void The_last_battle_of_a_player_is_the_latest_published_one_he_took_part_in()
        {
            var battles = new List<JObject>
            {
                Battle("4:1", Start - 5 * Hour, players: new long[] { 41, 42 }),
                Battle("4:2", Start - 3 * Hour, players: new long[] { 42 }),
                Battle("4:3", Start - 2 * Hour, players: new long[] { 43 }),
                Battle("4:4", Start - 60000, players: new long[] { 41 }), // not yet published
            };
            object Read(long from, long to) => new JObject { ["battles"] = new JArray(battles) };
            var last = BattleNotices.Latest(Read, 41, Delay, Start);
            Assert.Equal(PublicBattleProjection.Id("4:1"), last?.Id);
            Assert.Equal(PublicBattleProjection.Id("4:2"), BattleNotices.Latest(Read, 42, Delay, Start)?.Id);
            Assert.Null(BattleNotices.Latest(Read, 99, Delay, Start));
        }
    }
}

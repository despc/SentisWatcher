using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using SentisWatcher.Storage;
using SentisWatcher.Web;
using Xunit;

namespace SentisWatcher.Tests
{
    public class BattleOutcomeScoreTests
    {
        private static JObject Side(string id, double dealt, double taken) => new JObject
        {
            ["id"] = id, ["name"] = id, ["kind"] = "faction", ["dealt"] = dealt, ["taken"] = taken, ["balance"] = dealt - taken,
        };
        private static JObject Economy(decimal a, decimal b, string status = "ok") => new JObject
        {
            ["status"] = status, ["credits"] = (a + b).ToString("0", CultureInfo.InvariantCulture),
            ["sides"] = new JArray(new JObject { ["id"] = "a", ["credits"] = a.ToString("0", CultureInfo.InvariantCulture) },
                new JObject { ["id"] = "b", ["credits"] = b.ToString("0", CultureInfo.InvariantCulture) }),
        };
        private static JObject Score(JObject economy, bool provisional = false, params JObject[] sides) => (JObject)typeof(WebData)
            .GetMethod("ScoreOutcome", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { sides, economy, provisional });
        private static JObject Standard(JObject economy, bool provisional = false) => Score(economy, provisional, Side("a", 900, 100), Side("b", 100, 900));

        [Fact]
        public void Disproportionate_economic_losses_can_defeat_a_nine_times_higher_damage_dealer()
        {
            var result = Standard(Economy(9000000, 100000));
            Assert.Equal("b", (string)result["winner"]);
            Assert.True((bool)result["economyIncluded"]);
            Assert.Equal(0.35, (double)result["weights"]["dealt"]);
            Assert.Equal(0.15, (double)result["weights"]["taken"]);
            Assert.Equal(0.5, (double)result["weights"]["losses"]);
            Assert.Equal(-10.549451, (double)result["sides"].First()["score"], 6);
        }

        [Fact]
        public void Scaling_hp_or_currency_units_does_not_change_scores_or_the_winner()
        {
            var basic = Standard(Economy(9000000, 100000));
            var scaled = Score(Economy(900000000000000000000m, 10000000000000000000m), false, Side("a", 900000, 100000), Side("b", 100000, 900000));
            Assert.Equal((string)basic["winner"], (string)scaled["winner"]);
            Assert.Equal((double)basic["sides"].First()["score"], (double)scaled["sides"].First()["score"]);
            Assert.Equal("900000000000000000000", (string)scaled["sides"].Single(s => (string)s["id"] == "a")["lossCredits"]);
        }

        [Theory]
        [InlineData("partial")]
        [InlineData("unavailable")]
        public void Incomplete_economy_uses_damage_only_for_every_side_and_does_not_turn_unknown_losses_into_zero(string status)
        {
            var result = Standard(Economy(9000000, 100000, status));
            Assert.Equal("a", (string)result["winner"]);
            Assert.False((bool)result["economyIncluded"]);
            Assert.Equal("damage_only_incomplete_economy", (string)result["reason"]);
            Assert.Equal(0.7, (double)result["weights"]["dealt"]);
            Assert.All(result["sides"], s => Assert.Equal(JTokenType.Null, s["lossCredits"].Type));
        }

        [Fact]
        public void Missing_price_missing_side_and_truncated_economy_all_disable_the_economic_term()
        {
            var cases = new[] { Economy(9, 1), Economy(9, 1), Economy(9, 1), Economy(9, 1) };
            cases[0]["credits"] = null;
            cases[1]["sides"][0]["credits"] = null;
            ((JArray)cases[2]["sides"]).RemoveAt(0);
            cases[3]["truncated"] = true;
            foreach (var economy in cases) Assert.False((bool)Standard(economy)["economyIncluded"]);
        }

        [Theory]
        [InlineData("ok")]
        [InlineData("not_applicable")]
        public void Known_zero_losses_are_valid_and_neutral_not_missing_data(string status)
        {
            var result = Standard(Economy(0, 0, status));
            Assert.True((bool)result["economyIncluded"]);
            Assert.Equal("a", (string)result["winner"]);
            Assert.All(result["sides"], s => Assert.Equal("0", (string)s["lossCredits"]));
        }

        [Fact]
        public void Received_damage_changes_the_ranking_when_offense_and_costs_are_equal()
        {
            var result = Score(Economy(100, 100), false, Side("a", 100, 1000), Side("b", 100, 100));
            Assert.Equal("b", (string)result["winner"]);
        }

        [Fact]
        public void Equal_scores_draw_and_incomplete_battle_boundaries_never_confirm_a_winner()
        {
            var result = Score(Economy(100, 100), false, Side("a", 100, 100), Side("b", 100, 100));
            Assert.Equal("draw", (string)result["status"]);
            Assert.Equal(2, result["leaders"].Count());
            Assert.Equal(JTokenType.Null, result["winner"].Type);
            var unfinished = Standard(Economy(9, 1), true);
            Assert.Equal("provisional", (string)unfinished["status"]);
            Assert.Equal(JTokenType.Null, unfinished["winner"].Type);
        }

        [Fact]
        public void Public_side_limit_cannot_hide_the_winner_behind_many_defensive_only_rows()
        {
            var rows = Enumerable.Range(0, 100).Select(i => Side("defender:" + i, 0, 0.001))
                .Concat(new[] { Side("a", 100, 1000), Side("b", 1, 1000) }).ToArray();
            var result = Score(null, false, rows);
            Assert.Equal("a", (string)result["sides"].First()["id"]);
            var safe = PublicBattleProjection.Summary(new JObject { ["id"] = "fixture", ["outcome"] = result })["outcome"];
            Assert.Equal("s1", (string)safe["winner"]);
            Assert.True((bool)safe["truncated"]);
        }

        [Fact]
        public void Anonymous_sources_and_idle_sides_cannot_win_by_avoiding_penalties()
        {
            var result = Score(Economy(9000000, 100000), false, Side("a", 900, 100), Side("b", 100, 900), Side("unknown:0", 100000, 0), Side("idle", 0, 0));
            Assert.Equal("b", (string)result["winner"]);
            Assert.Equal(JTokenType.Null, result["sides"].Single(s => (string)s["id"] == "unknown:0")["score"].Type);
            Assert.Equal("unknown", (string)Score(null, false, Side("unknown:0", 100, 0))["status"]);
        }
    }
}

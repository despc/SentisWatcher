using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SentisWatcher.Storage;
using SentisWatcher.Web;
using Xunit;

namespace SentisWatcher.Tests
{
    public sealed class BattleTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "watcher-battles-" + Guid.NewGuid().ToString("N"));
        private readonly List<Row> _rows = new List<Row>();
        private const long A = 144115188075855928, B = A + 1;
        private static readonly long Start = Clock.ToMs(new DateTime(2026, 11, 11, 23, 59, 0, DateTimeKind.Utc));

        private void Event(int seconds, double x = 0, long attacker = A, long victim = B, string kind = "hit", string relation = "enemy",
            string attackerKind = "player", string weapon = "Bullet:LargeRailgun", double amount = 100, string af = "1|[AAA] Alpha", string vf = "2|[BBB] Beta",
            int? tracking = null, string credit = null, int count = 1, long target = 102, string vk = null)
        {
            var t = Start + seconds * 1000L;
            _rows.Add(new Row(Table.Damage, t, t, kind, attacker, attackerKind, 101L, weapon, target, "block", victim, relation, amount, count, x, 0.0, 0.0, af, vf, credit, tracking,
                null, null, vk));
        }

        private void Jump(int seconds, long actor = A, long grid = 101, string kind = "jump", double origin = 0, double destination = 100000)
        {
            var t = Start + seconds * 1000L;
            _rows.Add(new Row(Table.Events, t, t, kind, actor, grid, origin, 0.0, 0.0,
                Math.Abs(destination - origin), 1, "to=" + destination.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";0;0 Jump Ship"));
        }

        [Fact]
        public void Grinding_alone_and_shooting_under_the_threshold_are_not_battles()
        {
            // grinding a stranger's grid: no shot, however much it takes apart
            Event(0, kind: "grind", weapon: "Grind:AngleGrinder4Item", amount: 50000);
            Event(10, kind: "grind", weapon: "Grind:AngleGrinder4Item", amount: 50000);
            // a stray burst far away: under the threshold
            Event(0, x: 100000, amount: WebData.DefaultMinCombatDamage - 1);
            // shooting of the threshold exactly, with grinding beside it: a battle, the grinding a part of its damage
            Event(0, x: 200000, amount: WebData.DefaultMinCombatDamage / 2);
            Event(5, x: 200000, amount: WebData.DefaultMinCombatDamage / 2);
            Event(6, x: 200000, kind: "grind", weapon: "Grind:AngleGrinder4Item", amount: 3000);
            using (var store = Write())
            {
                var data = JToken.Parse(JsonConvert.SerializeObject(new WebData(store).Battles(Start - 120000, Start + 600000)));
                var battle = Assert.Single(data["battles"]);
                Assert.Equal(WebData.DefaultMinCombatDamage + 3000, (double)battle["combatDamage"]);
                var unknown = JToken.Parse(JsonConvert.SerializeObject(new WebData(store).Battles(Start - 120000, Start + 600000,
                    battle: (string)Call(store)["battles"].First(b => (double)b["combatDamage"] == 100000)["id"])));
                Assert.Equal(JTokenType.Null, unknown["report"].Type);
            }
        }

        [Fact]
        public void Only_battles_between_players_of_two_sides_are_published()
        {
            const long C = A + 2;
            // a player against an NPC's (pirates) and a bot's grids: no battle between players
            Event(0, amount: 2000, vk: "npc", vf: "9|[SPRT] Space Pirates");
            Event(0, x: 100000, amount: 2000, vk: "bot");
            // a bot shooting a player: neither
            Event(0, x: 200000, amount: 2000, attacker: B, attackerKind: "bot", victim: A, vk: "player");
            // a player's grid shot by a player of another faction: one, though the victim never fired back
            Event(0, x: 300000, amount: 2000, vk: "player");
            // rows of before victim_kind: the victim a player when it fired as one somewhere; C never did
            Event(0, x: 400000, amount: 2000, victim: C);
            Event(0, x: 500000, amount: 2000, attacker: B, victim: A, af: "2|[BBB] Beta", vf: "1|[AAA] Alpha");
            using (var store = Write())
            {
                var all = JToken.Parse(JsonConvert.SerializeObject(new WebData(store).Battles(Start - 120000, Start + 600000)));
                Assert.Equal(6, all["battles"].Count());
                var published = JToken.Parse(JsonConvert.SerializeObject(new WebData(store).Battles(Start - 120000, Start + 600000, betweenPlayers: true)));
                Assert.Equal(new[] { 300000.0, 500000.0 }, published["battles"].Select(b => (double)b["x"]).OrderBy(x => x).ToArray());
                // a report of a hidden one is not given either
                var hidden = (string)all["battles"].First(b => (double)b["x"] == 0)["id"];
                var report = JToken.Parse(JsonConvert.SerializeObject(new WebData(store).Battles(Start - 120000, Start + 600000, battle: hidden, betweenPlayers: true)));
                Assert.Equal(JTokenType.Null, report["report"].Type);
            }
        }

        private WatcherStore Write()
        {
            using (var writer = new WatcherStore(_folder, () => 100000))
            {
                writer.Add(new Row(Table.Names, Start, A, "player", "Alice", 0L, null, Start));
                writer.Add(new Row(Table.Names, Start, B, "player", "Bob", 0L, null, Start));
                foreach (var row in _rows) writer.Add(row);
            }
            return new WatcherStore(_folder, () => 100000);
        }

        private static JToken Call(WatcherStore store, string battle = null, long? from = null, long? to = null, int gap = 120, int radius = 5000) =>
            JToken.Parse(JsonConvert.SerializeObject(new WebData(store) { MinCombatDamage = 0 }.Battles(from ?? Start - 120000, to ?? Start + 600000, gap, radius, battle)));

        [Fact]
        public void Accuracy_uses_unique_round_hits_not_damaged_blocks_and_public_api_keeps_the_new_results_safe()
        {
            Event(0, count: 20, tracking: 3, credit: "cockpit");
            Event(-1, kind: "round", count: 10, tracking: 3, credit: "cockpit");
            Event(0, kind: "round_hit", count: 2, tracking: 3, credit: "cockpit");
            Jump(0); Jump(0, kind: "jump_passenger");
            using (var store = Write())
            {
                var report = Call(store, (string)Call(store)["battles"][0]["id"])["report"];
                var alice = report["participants"].Single(p => (string)p["person"]["id"] == A.ToString());
                Assert.Equal(10, (long)alice["shots"]); Assert.Equal(2, (long)alice["hitShots"]);
                Assert.Equal(20, (double)alice["accuracy"]); Assert.Equal("ok", (string)alice["accuracyStatus"]);
                Assert.Equal(1, (int)alice["jumpCount"]);
                Assert.Equal("winner", (string)report["mvp"]["status"]);
                Assert.Equal("winner", (string)report["summary"]["outcome"]["status"]);
                var published = PublicBattleProjection.Report(report);
                Assert.Equal(20, (double)published["participants"].Single(p => (string)p["name"] == "Alice")["accuracy"]);
                Assert.StartsWith("p", (string)published["mvp"]["players"][0]);
                Assert.StartsWith("s", (string)published["summary"]["outcome"]["winner"]);
                Assert.DoesNotContain(A.ToString(), published.ToString());
                Assert.DoesNotContain("faction:1", published.ToString());
                Assert.DoesNotContain("factionKey", published.ToString());
            }
        }

        [Fact]
        public void Mvp_breaks_damage_and_received_ties_by_successful_projectiles_not_damage_callbacks()
        {
            Event(0, tracking: 3, credit: "builder", count: 100);
            Event(10, attacker: B, victim: A, af: "2|[BBB] Beta", vf: "1|[AAA] Alpha", tracking: 3, credit: "cockpit");
            Event(0, kind: "round", count: 10, tracking: 3);
            Event(0, kind: "round_hit", count: 3, tracking: 3);
            Event(10, kind: "round", attacker: B, af: "2|[BBB] Beta", count: 10, tracking: 3);
            Event(10, kind: "round_hit", attacker: B, af: "2|[BBB] Beta", count: 5, tracking: 3);
            using (var store = Write())
            {
                var report = Call(store, (string)Call(store)["battles"][0]["id"])["report"];
                Assert.Equal(B.ToString(), (string)report["mvp"]["players"].Single()["id"]);
                Assert.Equal("draw", (string)report["summary"]["outcome"]["status"]);
            }
        }

        [Fact]
        public void Mvp_uses_less_received_damage_before_hits_and_does_not_choose_arbitrarily_when_hit_history_is_missing()
        {
            Event(0, victim: A + 2, vf: "3|[CCC] Gamma", tracking: 3);
            Event(1, attacker: B, af: "2|[BBB] Beta", victim: A + 2, vf: "3|[CCC] Gamma", tracking: 3);
            Event(2, attacker: A + 2, af: "3|[CCC] Gamma", victim: A, vf: "1|[AAA] Alpha", amount: 25, tracking: 3);
            Event(3, attacker: A + 2, af: "3|[CCC] Gamma", victim: B, vf: "2|[BBB] Beta", amount: 50, tracking: 3);
            using (var store = Write())
            {
                var report = Call(store, (string)Call(store)["battles"][0]["id"])["report"];
                Assert.Equal(A.ToString(), (string)report["mvp"]["players"].Single()["id"]);
            }
        }

        [Fact]
        public void An_mvp_tie_with_missing_shot_history_is_explicit_and_not_resolved_by_block_damage_counts()
        {
            Event(0, count: 50);
            Event(10, attacker: B, victim: A, af: "2|[BBB] Beta", vf: "1|[AAA] Alpha", count: 1);
            using (var store = Write())
            {
                var report = Call(store, (string)Call(store)["battles"][0]["id"])["report"];
                Assert.Equal("insufficient", (string)report["mvp"]["status"]);
                Assert.Equal(2, report["mvp"]["players"].Count());
            }
        }

        [Fact]
        public void Factionless_players_are_separate_sides_and_friendly_damage_cannot_win_or_make_mvp()
        {
            Event(0, amount: 100);
            Event(1, attacker: B, victim: A, amount: 25, af: "2|[BBB] Beta", vf: "1|[AAA] Alpha");
            Event(2, attacker: A + 2, af: "", amount: 200);
            Event(3, relation: "ally", amount: 1000000);
            using (var store = Write())
            {
                var report = Call(store, (string)Call(store)["battles"][0]["id"])["report"];
                var outcome = report["summary"]["outcome"];
                Assert.Equal("player:" + (A + 2), (string)outcome["winner"]);
                Assert.Equal(200, (double)outcome["sides"].First()["balance"]);
                Assert.Equal((A + 2).ToString(), (string)report["mvp"]["players"].Single()["id"]);
            }
        }

        [Fact]
        public void Unattributed_damage_is_not_awarded_to_a_source_and_does_not_block_the_known_damage_leader()
        {
            Event(0);
            Event(10, attacker: 0, attackerKind: "none", relation: "unknown", af: null, amount: 10000);
            using (var store = Write())
            {
                var outcome = Call(store, (string)Call(store)["battles"].Single()["id"])["report"]["summary"]["outcome"];
                Assert.Equal("winner", (string)outcome["status"]);
                Assert.Equal("damage_only_incomplete_economy", (string)outcome["reason"]);
                Assert.Equal("faction:1", (string)outcome["winner"]);
                Assert.Equal(10100, (double)outcome["sides"].Single(s => (string)s["id"] == "faction:2")["taken"]);
            }
        }

        [Fact]
        public void Legacy_marksmanship_stays_unknown_but_missing_factions_do_not_block_identified_individual_sides()
        {
            Event(0, af: null, vf: null, count: 50);
            Event(0, kind: "shot", count: 10, af: null, vf: null);
            using (var store = Write())
            {
                var report = Call(store, (string)Call(store)["battles"][0]["id"])["report"];
                var alice = report["participants"].Single(p => (string)p["person"]["id"] == A.ToString());
                Assert.Equal(JTokenType.Null, alice["accuracy"].Type);
                Assert.Equal("unavailable", (string)alice["accuracyStatus"]);
                Assert.Equal("winner", (string)report["summary"]["outcome"]["status"]);
                Assert.Equal("unknown:" + A, (string)report["summary"]["outcome"]["winner"]);
            }
        }

        [Fact]
        public void Damage_only_fallback_rewards_both_dealt_damage_and_lower_received_losses()
        {
            Event(0, amount: 100);
            Event(1, attacker: A + 2, victim: A, amount: 99, af: "3|Gamma", vf: "1|[AAA] Alpha");
            Event(2, attacker: B, victim: A + 2, amount: 10, af: "2|[BBB] Beta", vf: "3|Gamma");
            using (var store = Write())
            {
                var outcome = Call(store, (string)Call(store)["battles"].Single()["id"])["report"]["summary"]["outcome"];
                Assert.Equal("faction:3", (string)outcome["winner"]); // Almost equal damage dealt, but Gamma received far less.
            }
            using (var tie = new BattleTests())
            {
                tie.Event(0, amount: 100);
                tie.Event(1, attacker: A + 2, victim: A, amount: 100, af: "3|Gamma", vf: "1|[AAA] Alpha");
                tie.Event(2, attacker: B, victim: A + 2, amount: 10, af: "2|[BBB] Beta", vf: "3|Gamma");
                using (var store = tie.Write())
                    Assert.Equal("faction:3", (string)Call(store, (string)Call(store)["battles"].Single()["id"])["report"]["summary"]["outcome"]["winner"]);
            }
        }

        [Fact]
        public void Mvp_includes_received_only_snapshots_after_a_faction_change_in_the_identity_score()
        {
            Event(0, amount: 100);
            Event(1, attacker: A + 2, af: "3|Gamma", amount: 100);
            Event(2, attacker: 0, attackerKind: "none", victim: A, af: null, vf: "2|[BBB] Beta", relation: "unknown", amount: 50);
            using (var store = Write())
            {
                var report = Call(store, (string)Call(store)["battles"].Single()["id"])["report"];
                Assert.Equal("winner", (string)report["mvp"]["status"]);
                Assert.Equal((A + 2).ToString(), (string)report["mvp"]["players"].Single()["id"]);
            }
        }

        [Theory]
        [InlineData("player")]
        [InlineData("bot")]
        [InlineData("npc")]
        public void Identified_damage_dealers_can_be_mvp_regardless_of_human_or_npc_actor_kind(string kind)
        {
            Event(0, attackerKind: kind);
            using (var store = Write())
            {
                var report = Call(store, (string)Call(store)["battles"].Single()["id"])["report"];
                Assert.Equal("winner", (string)report["mvp"]["status"]);
                Assert.Equal(A.ToString(), (string)report["mvp"]["players"].Single()["id"]);
            }
        }

        [Fact]
        public void Equal_dealt_and_received_is_a_draw_but_anonymous_damage_and_friendly_fire_never_win_or_make_mvp()
        {
            Event(0); Event(1, attacker: B, victim: A, af: "2|[BBB] Beta", vf: "1|[AAA] Alpha");
            using (var store = Write())
            {
                var outcome = Call(store, (string)Call(store)["battles"].Single()["id"])["report"]["summary"]["outcome"];
                Assert.Equal("draw", (string)outcome["status"]); Assert.Equal(2, outcome["leaders"].Count());
            }
            using (var anonymous = new BattleTests())
            {
                anonymous.Event(0, attacker: 0, attackerKind: "none", af: null);
                anonymous.Event(1, relation: "ally", amount: 1000000);
                using (var store = anonymous.Write())
                {
                    var report = Call(store, (string)Call(store)["battles"].Single()["id"])["report"];
                    Assert.Equal("unknown", (string)report["summary"]["outcome"]["status"]);
                    Assert.Equal("none", (string)report["mvp"]["status"]);
                }
            }
        }

        [Fact]
        public void Simultaneous_fights_using_the_same_gun_owner_do_not_get_fabricated_accuracy()
        {
            Event(0, tracking: 3); Event(0, x: 50000, target: 103, tracking: 3);
            Event(0, kind: "round", count: 10, tracking: 3);
            Event(0, kind: "round_hit", count: 2, tracking: 3);
            using (var store = Write())
            {
                var report = Call(store, (string)Call(store)["battles"][0]["id"])["report"];
                var alice = report["participants"].Single(p => (string)p["person"]["id"] == A.ToString());
                Assert.Equal("ambiguous", (string)alice["accuracyStatus"]);
                Assert.Equal(JTokenType.Null, alice["accuracy"].Type);
            }
        }

        [Fact]
        public void Jump_totals_include_passengers_once_and_exclude_prebattle_arrival_context()
        {
            Event(0); Event(20);
            Jump(-20); Jump(10); Jump(10, kind: "jump_passenger"); Jump(10, actor: B, kind: "jump_passenger"); Jump(130);
            using (var store = Write())
            {
                var report = Call(store, (string)Call(store)["battles"][0]["id"])["report"];
                Assert.Equal(2, (int)report["participants"].Single(p => (string)p["person"]["id"] == A.ToString())["jumpCount"]);
                Assert.Equal(1, (int)report["participants"].Single(p => (string)p["person"]["id"] == B.ToString())["jumpCount"]);
            }
        }

        [Theory]
        [InlineData(11L, 22L, 11L)]
        [InlineData(0L, 22L, 22L)]
        [InlineData(0L, 0L, 0L)]
        public void Combat_credit_prefers_the_cockpit_pilot_then_builder_never_an_unrelated_owner(long pilot, long builder, long expected)
        {
            var type = typeof(Clock).Assembly.GetType("SentisWatcher.Recording.CombatCredit");
            var select = type.GetMethod("Select", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.Equal(expected, (long)select.Invoke(null, new object[] { pilot, builder }));
        }

        [Fact]
        public void A_projectile_can_only_credit_one_hit_even_with_parallel_callbacks()
        {
            var type = typeof(Clock).Assembly.GetType("SentisWatcher.Recording.CombatRoundCounter");
            var round = Activator.CreateInstance(type, nonPublic: true);
            var hit = type.GetMethod("TryHit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.Equal(1, Enumerable.Range(0, 1000).AsParallel().Count(i => (bool)hit.Invoke(round, null)));
        }

        [Fact]
        public void Space_time_components_merge_bridges_but_not_remote_or_later_fights()
        {
            Event(0, 0); Event(10, 8000); Event(20, 4000); // bridges the two components
            Event(25, 50000); // same players, different location
            Event(200, 4000); // same place, beyond silence threshold
            using (var store = Write())
            {
                var battles = Call(store)["battles"];
                Assert.Equal(3, battles.Count());
                Assert.Equal(300, (double)battles[0]["combatDamage"]);
                Assert.Equal(Start, (long)battles[0]["start"]);
                Assert.Equal(Start + 20000, (long)battles[0]["end"]);
            }
        }

        [Fact]
        public void Own_damage_weather_animals_and_shots_do_not_start_or_extend_a_battle()
        {
            Event(0, kind: "shot"); Event(2, relation: "own"); Event(4, relation: "unknown", attackerKind: "none", weapon: "Temperature:?");
            Event(6, attackerKind: "animal"); Event(10); Event(20, attacker: B, victim: A);
            Event(21, kind: "destroyed", amount: 1); Event(22, relation: "ally", amount: 25);
            Event(23, relation: "unknown", attacker: 0, attackerKind: "none", amount: 40);
            Event(40, relation: "own", amount: 500); Event(200, relation: "own");
            Event(100, relation: "unknown", attackerKind: "none", attacker: 0, weapon: "Temperature:?", amount: 77);
            using (var store = Write())
            {
                var summary = Call(store)["battles"].Single();
                Assert.Equal(Start + 10000, (long)summary["start"]);
                Assert.Equal(Start + 23000, (long)summary["end"]);
                Assert.Equal(Start + 143000, (long)summary["endedAt"]);
                Assert.False((bool)summary["ongoing"]);
                Assert.Equal(200, (double)summary["combatDamage"]);
                Assert.Equal(265, (double)summary["damage"]); // related collateral prolongs the fight
                Assert.Equal(1, (int)summary["destroyed"]);
            }
        }

        [Fact]
        public void Report_preserves_ids_factions_weapons_targets_and_received_damage()
        {
            Event(0, amount: 100); Event(10, attacker: B, victim: A, amount: 30, weapon: "Explosion:Missile", af: "2|[BBB] Beta", vf: "1|[AAA] Alpha");
            Event(10, attacker: B, victim: A, kind: "kill", amount: 1, af: "2|[BBB] Beta", vf: "1|[AAA] Alpha");
            Event(12, relation: "ally", amount: 5);
            using (var store = Write())
            {
                var summary = Call(store)["battles"].Single();
                var report = Call(store, (string)summary["id"])["report"];
                var alice = report["participants"].Single(p => (string)p["person"]["id"] == A.ToString());
                Assert.Equal(JTokenType.String, alice["person"]["id"].Type);
                Assert.Equal("[AAA] Alpha", (string)alice["person"]["faction"]);
                Assert.Equal(105, (double)alice["dealt"]);
                Assert.Equal(30, (double)alice["taken"]);
                Assert.Equal(1, (int)alice["deaths"]);
                Assert.Contains(report["breakdown"], r => (string)r["weapon"] == "Explosion:Missile" && (string)r["targetId"] == "102");
                Assert.Equal(135, report["timeline"].Sum(p => (double)p["damage"]));
                Assert.Equal(JTokenType.Null, Call(store, "missing")["report"].Type);
            }
        }

        [Fact]
        public void Narrow_query_finds_full_battle_across_day_files_without_manual_boundaries()
        {
            Event(30); Event(80);
            using (var store = Write())
            {
                var summary = Call(store)["battles"].Single();
                Assert.Equal(50000, (long)summary["duration"]);
                Assert.False((bool)summary["clippedStart"]);
                Assert.False((bool)summary["clippedEnd"]);
                var narrow = Call(store, from: Start + 60000, to: Start + 65000)["battles"].Single();
                Assert.Equal((string)summary["id"], (string)narrow["id"]);
                Assert.Equal(Start + 30000, (long)narrow["start"]);
                Assert.Equal(Start + 80000, (long)narrow["end"]);
                Assert.False((bool)narrow["clippedStart"]);
                Assert.False((bool)narrow["clippedEnd"]);
            }
        }

        [Fact]
        public void Legacy_databases_need_no_migration_to_read_and_unknown_is_not_no_faction()
        {
            Event(0, af: null, vf: "");
            using (var store = Write())
            {
                var report = Call(store, (string)Call(store)["battles"][0]["id"])["report"];
                Assert.Contains(report["participants"], p => (string)p["person"]["faction"] == "Фракция неизвестна");
                Assert.Contains(report["participants"], p => (string)p["person"]["faction"] == "Без фракции");
            }
            foreach (var file in Directory.GetFiles(_folder, "*.db"))
                using (var db = new SQLiteConnection("Data Source=" + file))
                {
                    db.Open();
                    using (var cmd = new SQLiteCommand("ALTER TABLE damage DROP COLUMN attacker_faction; ALTER TABLE damage DROP COLUMN victim_faction;", db)) cmd.ExecuteNonQuery();
                }
            using (var store = new WatcherStore(_folder, () => 100000))
            {
                var summary = Call(store)["battles"].Single();
                Assert.Equal("Фракция неизвестна", (string)summary["factions"].Single());
            }
        }

        [Fact]
        public void Web_api_routes_battle_parameters_and_selected_report()
        {
            Event(0, amount: WebData.DefaultMinCombatDamage);
            using (var store = Write())
            {
                var q = new System.Collections.Specialized.NameValueCollection
                {
                    ["from"] = (Start - 120000).ToString(), ["to"] = (Start + 120000).ToString(),
                    ["gap"] = "60", ["radius"] = "3000",
                };
                var server = new WebServer(store, 0);
                var list = JToken.Parse(JsonConvert.SerializeObject(server.Api("battles", q)));
                Assert.Equal(60, (int)list["gapSeconds"]);
                Assert.Equal(3000, (int)list["radius"]);
                q["battle"] = (string)list["battles"].Single()["id"];
                var detail = JToken.Parse(JsonConvert.SerializeObject(server.Api("battles", q)));
                Assert.Equal(q["battle"], (string)detail["report"]["summary"]["id"]);
            }
        }

        [Fact]
        public void Context_between_two_distinct_fights_is_not_double_counted()
        {
            Event(0, -4000); Event(0, 4000);
            Event(0, 0, relation: "unknown", attackerKind: "none", attacker: 0, amount: 500);
            using (var store = Write())
            {
                var result = Call(store);
                Assert.Equal(2, result["battles"].Count());
                Assert.Equal(200, result["battles"].Sum(b => (double)b["damage"]));
                Assert.Equal(1, (int)result["ignored"]);
                Assert.Single(Call(store, radius: 10000)["battles"]);
            }
        }

        [Fact]
        public void Gap_and_radius_boundaries_are_inclusive()
        {
            Event(0, 0); Event(120, 5000);
            using (var store = Write())
            {
                Assert.Single(Call(store)["battles"]);
                Assert.Equal(2, Call(store, gap: 119)["battles"].Count());
                Assert.Equal(2, Call(store, radius: 4999)["battles"].Count());
            }
        }

        [Fact]
        public void Related_damage_keeps_a_battle_open_as_long_as_it_continues_then_silence_ends_it()
        {
            Event(0);
            for (var s = 100; s <= 1000; s += 100)
                Event(s, attacker: 0, attackerKind: "none", relation: "unknown", weapon: "Explosion:?");
            Event(1121); // 121 seconds of silence: a new battle
            using (var store = Write())
            {
                var all = Call(store, from: Start + 400000, to: Start + 500000);
                var first = all["battles"].Single();
                Assert.Equal(Start, (long)first["start"]);
                Assert.Equal(Start + 1000000, (long)first["end"]);
                Assert.Equal(Start + 1120000, (long)first["endedAt"]);
                Assert.Equal(1100, (double)first["damage"]);
                Assert.Equal(2, Call(store, to: Start + 1200000)["battles"].Count());
            }
        }

        [Fact]
        public void Timeline_includes_arrival_departure_and_passengers_without_extending_battle()
        {
            Event(0); Event(20);
            Jump(-30, origin: 100000, destination: 0);
            Jump(10); Jump(10, kind: "jump_passenger"); Jump(11, actor: B, kind: "jump_passenger");
            Jump(100); // escape after the last damage, before the 120-second silence timer expires
            Jump(141); // outside the battle's inactivity window
            Jump(10, actor: A, grid: 555, origin: 100000, destination: 200000); // same person, remote grid
            Jump(10, actor: A + 20, grid: 556); // unrelated person/grid passing the area
            using (var store = Write())
            {
                var summary = Call(store)["battles"].Single();
                var report = Call(store, (string)summary["id"])["report"];
                Assert.Equal(Start, (long)report["summary"]["start"]);
                Assert.Equal(Start + 20000, (long)report["summary"]["end"]);
                var jumps = report["jumps"].ToList();
                Assert.Equal(3, jumps.Count);
                Assert.Equal("arrival", (string)jumps[0]["direction"]);
                Assert.Equal("departure", (string)jumps[1]["direction"]);
                Assert.Equal("Jump Ship", (string)jumps[1]["grid"]);
                Assert.Equal("101", (string)jumps[1]["gridId"]);
                Assert.Equal(2, jumps[1]["actors"].Count());
                Assert.Contains(jumps[1]["actors"], a => (string)a["id"] == A.ToString() && (string)a["name"] == "Alice" && (string)a["role"] == "pilot");
                Assert.Contains(jumps[1]["actors"], a => (string)a["id"] == B.ToString() && (string)a["role"] == "passenger");
                Assert.Equal(Start - 30000, (long)report["timelineFrom"]);
                Assert.Equal(Start + 102000, (long)report["timelineTo"]);
                Assert.Equal(200, report["timeline"].Sum(p => (double)p["damage"]));
                Assert.Contains(report["timeline"], p => (double)p["damage"] == 0);
                Assert.All(report["timeline"], p => Assert.True((long)p["t"] + (long)p["width"] <= (long)report["timelineTo"]));
            }
        }

        [Fact]
        public void Jump_of_a_combatant_arriving_on_another_grid_is_shown_and_missing_jump_logs_are_safe()
        {
            Event(0); Event(20);
            Jump(5, grid: 777, origin: 100000, destination: 0);
            using (var store = Write())
            {
                var summary = Call(store)["battles"].Single();
                Assert.Single(Call(store, (string)summary["id"])["report"]["jumps"]);
            }
            using (var db = new SQLiteConnection("Data Source=" + Directory.GetFiles(_folder, "*.db").First()))
            {
                db.Open();
                using (var cmd = new SQLiteCommand("DROP TABLE events", db)) cmd.ExecuteNonQuery();
            }
            using (var store = new WatcherStore(_folder, () => 100000))
            {
                var summary = Call(store)["battles"].Single();
                Assert.Empty(Call(store, (string)summary["id"])["report"]["jumps"]);
            }
        }

        [Fact]
        public void Battle_title_uses_opposing_faction_tags_once_and_ignores_collateral_factions()
        {
            Event(0, af: "1|[ZRG] Zerg", vf: "2|[SVP] Vanguard", amount: 100);
            Event(10, attacker: B, victim: A, af: "2|[SVP] Vanguard", vf: "1|[ZRG] Zerg", amount: 30);
            Event(11, attacker: A + 2, victim: B, relation: "ally", af: "3|[XYZ] Nearby", vf: "2|[SVP] Vanguard");
            using (var store = Write())
            {
                var summary = Call(store)["battles"].Single();
                Assert.Equal("ZRG vs SVP", (string)summary["title"]);
                Assert.Equal((string)summary["title"], (string)Call(store, (string)summary["id"])["report"]["summary"]["title"]);
            }
        }

        [Fact]
        public void Battle_title_includes_all_fighting_factions_without_inventing_coalitions()
        {
            Event(0, af: "1|[ZRG] Zerg", vf: "2|[SVP] Vanguard");
            Event(10, af: "3|[ABC] Third", vf: "2|[SVP] Vanguard");
            using (var store = Write())
                Assert.Equal("SVP vs ZRG vs ABC", (string)Call(store)["battles"].Single()["title"]);
        }

        [Fact]
        public void Battle_title_does_not_guess_missing_factions_and_handles_same_faction_combat()
        {
            Event(0, af: null, vf: "");
            Event(200, af: "1|[ZRG] Zerg", vf: "1|[ZRG] Zerg");
            using (var store = Write())
            {
                var battles = Call(store)["battles"];
                Assert.Contains(battles, b => (string)b["title"] == "Alice (фракция неизвестна) vs Bob (без фракции)");
                Assert.Contains(battles, b => (string)b["title"] == "Бой внутри ZRG");
            }
        }

        [Fact]
        public void Recent_damage_marks_battle_ongoing_and_future_interval_does_not_pretend_it_ended()
        {
            var seconds = (int)((Clock.Now - Start) / 1000) - 5;
            Event(seconds);
            using (var store = Write())
            {
                var summary = Call(store, from: Clock.Now - 60000, to: Clock.Now + 600000)["battles"].Single();
                Assert.True((bool)summary["ongoing"]);
                Assert.Equal(JTokenType.Null, summary["endedAt"].Type);
            }
        }

        [Fact]
        public void Empty_and_missing_damage_tables_are_supported_and_resources_are_embedded()
        {
            using (var store = Write())
            {
                Assert.Empty(Call(store)["battles"]);
                Assert.Equal(10, (int)Call(store, gap: -1)["gapSeconds"]);
                Assert.Equal(20000, (int)Call(store, radius: int.MaxValue)["radius"]);
            }
            using (var db = new SQLiteConnection("Data Source=" + Directory.GetFiles(_folder, "*.db").Single()))
            {
                db.Open();
                using (var cmd = new SQLiteCommand("DROP TABLE damage", db)) cmd.ExecuteNonQuery();
            }
            using (var store = new WatcherStore(_folder, () => 100000)) Assert.Empty(Call(store)["battles"]);
            var resources = typeof(WebServer).Assembly.GetManifestResourceNames().Select(n => n.Replace('\\', '/'));
            foreach (var file in new[] { "battles.html", "battles.js", "battles.css", "battle-timeline.js" }) Assert.Contains("Web/" + file, resources);
        }

        public void Dispose() { try { Directory.Delete(_folder, true); } catch { } }
    }
}

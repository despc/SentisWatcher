using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using SentisWatcher.Storage;
using SentisWatcher.Web;
using Xunit;

namespace SentisWatcher.Tests
{
    public sealed class BattleEconomyTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "watcher-economy-" + Guid.NewGuid().ToString("N"));
        private readonly List<Row> _rows = new List<Row>();
        private static readonly long Start = Clock.ToMs(new DateTime(2026, 11, 11, 23, 59, 0, DateTimeKind.Utc));
        private const long Alice = 144115188075855928, Bob = Alice + 1;
        private const string Alpha = "1|[AAA] Alpha", Beta = "2|[BBB] Beta";

        private string Hit(int seconds = 0, string key = "hit-0", int? tracking = 1, double x = 0, long target = 102)
        {
            var t = Start + seconds * 1000L;
            _rows.Add(new Row(Table.Damage, t, t, "hit", Alice, "player", 101L, "Bullet:LargeRailgun", target, "block", Bob,
                "enemy", 100d, 1, x, 0d, 0d, Alpha, Beta, "cockpit", 3, tracking, key));
            return key;
        }
        private void Loss(int seconds, string token, string[] keys, JArray components = null, bool destroyed = false,
            string faction = Beta, long owner = Bob, long grid = 102, int tracking = 1, bool damaged = true,
            double before = 1000, double after = 900, double x = 0)
        {
            var t = Start + seconds * 1000L;
            _rows.Add(new Row(Table.BlockEconomy, t, t, t, token, grid, owner, faction, "MyObjectBuilder_CubeBlock/LargeBlockArmorBlock",
                before, after, destroyed ? 1 : 0, (components ?? new JArray()).ToString(Formatting.None), x, 0d, 0d,
                tracking, keys == null ? null : JsonConvert.SerializeObject(keys), damaged ? 1 : 0));
        }
        private static JArray Parts(string id = "SteelPlate", long lost = 1, int? price = 13) => new JArray(new JObject
        { ["id"] = id, ["lost"] = lost, ["unitPrice"] = price.HasValue ? new JValue(price.Value) : JValue.CreateNull() });
        private WatcherStore Write()
        {
            using (var store = new WatcherStore(_folder, () => 100000))
            {
                store.Add(new Row(Table.Names, Start, Alice, "player", "Alice", 0L, null, Start));
                store.Add(new Row(Table.Names, Start, Bob, "player", "Bob", 0L, null, Start));
                foreach (var row in _rows) store.Add(row);
            }
            return new WatcherStore(_folder, () => 100000);
        }
        private static JToken Data(WatcherStore store, string id = null) => JToken.FromObject(new WebData(store) { MinCombatDamage = 0 }.Battles(Start - 120000, Start + 600000, battle: id));
        private static JToken Report(WatcherStore store, int index = 0) => Data(store, (string)Data(store)["battles"][index]["id"])["report"];
        private static JToken Side(JToken e, string name = "[BBB] Beta") => e["sides"].Single(s => (string)s["name"] == name);

        [Fact]
        public void Native_component_stack_counts_partial_damage_and_never_leaves_a_phantom_component_after_destruction()
        {
            var component = new MyComponentDefinition { MaxIntegrity = 100 };
            var def = new MyCubeBlockDefinition { MaxIntegrity = 2500, Components = new[] { new MyCubeBlockDefinition.Component { Count = 25, Definition = component } } };
            var stack = new MyComponentStack(def, 1f, 1f);
            var method = typeof(Clock).Assembly.GetType("SentisWatcher.Recording.DamageLog")
                .GetMethod("MountedComponents", BindingFlags.Static | BindingFlags.NonPublic);
            int Count() => (int)method.Invoke(null, new object[] { stack, 0 });
            Assert.Equal(25, Count());
            // Set the native post-application state without booting the world's floating-object/stockpile services.
            var set = typeof(MyComponentStack).GetMethod("SetIntegrity", BindingFlags.Instance | BindingFlags.NonPublic);
            set.Invoke(stack, new object[] { 2500f, 2470f }); Assert.Equal(25, Count());
            set.Invoke(stack, new object[] { 2500f, 2400f }); Assert.Equal(24, Count());
            set.Invoke(stack, new object[] { 2500f, 0f }); Assert.True(stack.IsDestroyed); Assert.Equal(0, Count());
            var incomplete = new MyComponentStack(def, 0.2f, 0.2f);
            Assert.Equal(5, (int)method.Invoke(null, new object[] { incomplete, 0 }));
        }

        [Fact]
        public void Repeated_hits_deduplicate_blocks_but_sum_real_components_and_snapshot_prices_across_midnight()
        {
            Hit(); Hit(70, "hit-70"); Hit(72, "hit-72");
            Loss(0, "one", new[] { "hit-0" }, Parts(lost: 2));
            Loss(70, "one", new[] { "hit-70" }, Parts(lost: 3), destroyed: true);
            Loss(72, "two", new[] { "hit-72" });
            using (var store = Write())
            {
                var e = Report(store)["economy"];
                Assert.Equal("ok", (string)e["status"]); Assert.Equal(2, (int)e["affectedBlocks"]);
                Assert.Equal(1, (int)e["destroyedBlocks"]); Assert.Equal(1, (int)e["damagedBlocks"]);
                Assert.Equal(5, (long)e["lostComponents"]); Assert.Equal("65", (string)e["credits"]);
                Assert.Equal(5, (long)Side(e)["components"][0]["lost"]);
                Assert.Equal("0", (string)Side(e, "[AAA] Alpha")["credits"]);
            }
        }

        [Fact]
        public void Selected_report_uses_saved_losses_from_damaged_and_destroyed_blocks_to_choose_the_winner_without_loading_them_in_the_catalog()
        {
            _rows.Add(new Row(Table.Damage, Start, Start, "hit", Alice, "player", 101L, "Bullet:Gun", 102L, "block", Bob,
                "enemy", 900d, 1, 0d, 0d, 0d, Alpha, Beta, "cockpit", 3, 1, "a"));
            _rows.Add(new Row(Table.Damage, Start + 1000, Start + 1000, "hit", Bob, "player", 102L, "Bullet:Gun", 101L, "block", Alice,
                "enemy", 100d, 1, 0d, 0d, 0d, Beta, Alpha, "cockpit", 3, 1, "b"));
            Loss(0, "damaged-beta", new[] { "a" }, Parts(price: 100000));
            Loss(1, "destroyed-alpha", new[] { "b" }, Parts(price: 9000000), destroyed: true, faction: Alpha, owner: Alice, grid: 101);
            using (var store = Write())
            {
                Assert.Equal(JTokenType.Null, Data(store)["battles"].Single()["outcome"].Type);
                var report = Report(store);
                Assert.Equal("9100000", (string)report["economy"]["credits"]);
                Assert.Equal(2, (long)report["economy"]["lostComponents"]);
                var outcome = report["summary"]["outcome"];
                Assert.Equal("faction:2", (string)outcome["winner"]);
                Assert.True((bool)outcome["economyIncluded"]);
                var published = PublicBattleProjection.Report(report)["summary"]["outcome"];
                Assert.Equal("weighted_shares_v1", (string)published["method"]);
                Assert.Equal("100000", (string)published["sides"].Single(s => (string)s["id"] == (string)published["winner"])["lossCredits"]);
                Assert.Equal(0.5, (double)published["weights"]["losses"]);
                Assert.DoesNotContain("faction:", published.ToString());
            }
        }

        [Fact]
        public void Partial_hp_damage_does_not_charge_for_an_entire_component_or_block()
        {
            Hit(); Loss(0, "partial", new[] { "hit-0" });
            using (var store = Write())
            {
                var e = Report(store)["economy"];
                Assert.Equal("ok", (string)e["status"]); Assert.Equal(1, (int)e["damagedBlocks"]);
                Assert.Equal(0, (long)e["lostComponents"]); Assert.Equal("0", (string)e["credits"]);
            }
        }

        [Fact]
        public void Repaired_blocks_still_count_as_having_received_damage_and_prices_may_differ_between_snapshots()
        {
            Hit(); Hit(1, "later");
            Loss(0, "repaired", new[] { "hit-0" }, Parts(lost: 2, price: 13), before: 1000, after: 1000);
            Loss(1, "repaired", new[] { "later" }, Parts(lost: 3, price: 20));
            using (var store = Write())
            {
                var e = Report(store)["economy"]; var c = Side(e)["components"][0];
                Assert.Equal(1, (int)e["damagedBlocks"]); Assert.Equal("86", (string)e["credits"]);
                Assert.Equal("mixed", (string)c["priceStatus"]); Assert.Equal(JTokenType.Null, c["unitPrice"].Type);
            }
        }

        [Fact]
        public void Missing_component_prices_are_unknown_not_free_and_explicit_zero_is_a_real_price()
        {
            Hit(); var parts = Parts(lost: 2); parts.Add(Parts("ModPart", 3, null)[0]); parts.Add(Parts("FreePart", 4, 0)[0]);
            Loss(0, "partial-price", new[] { "hit-0" }, parts);
            using (var store = Write())
            {
                var e = Report(store)["economy"];
                Assert.Equal("ok", (string)e["status"]); Assert.Equal(JTokenType.Null, e["credits"].Type);
                Assert.Equal("26", (string)e["knownCredits"]); Assert.Equal(3, (int)e["unpricedComponents"]);
                Assert.Equal("0", (string)Side(e)["components"].Single(c => (string)c["id"] == "FreePart")["credits"]);
            }
        }

        [Fact]
        public void Legacy_and_mixed_coverage_do_not_claim_zero_or_complete_loss()
        {
            Hit(tracking: null, key: null);
            using (var store = Write()) Assert.Equal("unavailable", (string)Report(store)["economy"]["status"]);
            Directory.Delete(_folder, true);
            Hit(1, "new"); Loss(1, "new-block", new[] { "new" }, Parts());
            using (var store = Write())
            {
                var e = Report(store)["economy"];
                Assert.Equal("partial", (string)e["status"]); Assert.Equal(JTokenType.Null, e["credits"].Type);
                Assert.Equal("13", (string)e["knownCredits"]); Assert.Equal(1, (int)e["missingAggregates"]);
            }
        }

        [Fact]
        public void Exact_damage_links_disambiguate_same_grid_simultaneous_distant_battles_without_spatial_guessing()
        {
            Hit(key: "left"); Hit(key: "right", x: 10000);
            Loss(0, "left-block", new[] { "left" }, Parts(lost: 2), x: 10000);
            Loss(0, "right-block", new[] { "right" }, Parts(lost: 7), x: 0);
            using (var store = Write())
            {
                Assert.Equal(2, Data(store)["battles"].Count());
                var values = new[] { Report(store, 0)["economy"], Report(store, 1)["economy"] };
                Assert.All(values, e => Assert.Equal("ok", (string)e["status"]));
                Assert.Equal(new long[] { 2, 7 }, values.Select(e => (long)e["lostComponents"]).OrderBy(x => x));
            }
        }

        [Fact]
        public void Losses_accumulated_from_two_battles_or_missing_source_links_are_not_guessed_or_counted_twice()
        {
            Hit(key: "left"); Hit(key: "right", x: 10000);
            Loss(0, "ambiguous", new[] { "left", "right" }, Parts(lost: 25));
            Loss(0, "orphan", null, Parts(lost: 50));
            using (var store = Write())
                for (var i = 0; i < 2; i++)
                {
                    var e = Report(store, i)["economy"];
                    Assert.Equal("unavailable", (string)e["status"]); Assert.Equal(0, (long)e["lostComponents"]);
                    Assert.Equal(1, (int)e["ambiguousRecords"]); Assert.Equal(JTokenType.Null, e["credits"].Type);
                }
        }

        [Fact]
        public void Historical_owner_snapshots_assign_materials_even_when_ownership_changes_and_global_blocks_stay_unique()
        {
            Hit(); Hit(1, "later");
            Loss(0, "shared", new[] { "hit-0" }, Parts(lost: 2));
            Loss(1, "shared", new[] { "later" }, Parts(lost: 3), destroyed: true, faction: Alpha, owner: Alice);
            using (var store = Write())
            {
                var e = Report(store)["economy"];
                Assert.Equal(1, (int)e["affectedBlocks"]); Assert.Equal(1, (int)e["destroyedBlocks"]);
                Assert.Equal(2, (int)Side(e)["lostComponents"]); Assert.Equal(3, (int)Side(e, "[AAA] Alpha")["lostComponents"]);
            }
        }

        [Fact]
        public void Public_economy_is_whitelisted_and_money_remains_exact_beyond_javascript_safe_integers()
        {
            Hit(); Loss(0, "PRIVATE-BLOCK-TOKEN", new[] { "hit-0" }, Parts(lost: 5000000000, price: 2000000000));
            using (var store = Write())
            {
                var report = Report(store);
                var raw = (JObject)report["economy"];
                raw["secret"] = "DO-NOT-PUBLISH"; raw["x"] = 12345;
                var published = PublicBattleProjection.Report(report); var e = published["economy"];
                Assert.Equal("10000000000000000000", (string)e["credits"]);
                Assert.All(e["sides"], s => Assert.StartsWith("l", (string)s["id"]));
                var json = published.ToString();
                foreach (var forbidden in new[] { "PRIVATE-BLOCK-TOKEN", "hit-0", "DO-NOT-PUBLISH", "faction:1", "faction:2", Alice.ToString(), Bob.ToString(), "damage_keys", "before_integrity" })
                    Assert.DoesNotContain(forbidden, json);
                Assert.Null(e["x"]);
            }
        }
        [Fact]
        public void Tied_grid_owners_do_not_assign_unowned_armor_to_an_arbitrary_faction_or_player()
        {
            var method = typeof(Clock).Assembly.GetType("SentisWatcher.Recording.DamageLog").GetMethod("EconomicOwnership", BindingFlags.Static | BindingFlags.NonPublic);
            (long, string) Pick(long owner, long[] grid, Func<long, string> faction) =>
                ((long, string))method.Invoke(null, new object[] { owner, grid, faction });
            Assert.Equal((Bob, Beta), Pick(Bob, new[] { Alice }, id => id == Bob ? Beta : Alpha));
            Assert.Equal((Alice, Alpha), Pick(0, new[] { Bob, Alice }, id => Alpha));
            Assert.Equal((0L, (string)null), Pick(0, new[] { Alice, Bob }, id => id == Alice ? Alpha : Beta));
            Assert.Equal((0L, (string)null), Pick(0, new[] { Alice, Bob }, id => ""));
            Assert.Equal((0L, ""), Pick(0, new long[0], id => null));
        }

        [Fact]
        public void Restarted_recording_does_not_claim_known_unique_block_counts_across_runtime_objects()
        {
            Hit(); Hit(2, "after-restart");
            Loss(0, "session-one", new[] { "hit-0" }, Parts());
            Loss(2, "session-two", new[] { "after-restart" }, Parts(lost: 2));
            var t = Start + 1000;
            _rows.Add(new Row(Table.Events, t, t, "server_start", 0L, 0L, 0d, 0d, 0d, 0d, 0, "recording"));
            using (var store = Write())
            {
                var report = Report(store); var e = report["economy"];
                Assert.True((bool)e["recordingInterrupted"]); Assert.Equal("partial", (string)e["status"]);
                Assert.Equal(JTokenType.Null, e["affectedBlocks"].Type);
                Assert.All(e["sides"], side => Assert.Equal(JTokenType.Null, side["destroyedBlocks"].Type));
                Assert.Equal("39", (string)e["knownCredits"]); Assert.Equal(JTokenType.Null, e["credits"].Type);
                Assert.True((bool)PublicBattleProjection.Report(report)["economy"]["recordingInterrupted"]);
            }
        }

        [Fact]
        public void An_unfinished_fatal_after_callback_is_not_flushed_as_a_permanent_missing_link()
        {
            var type = typeof(Clock).Assembly.GetType("SentisWatcher.Recording.DamageLog");
            var batchType = type.GetNestedType("LossBatch", BindingFlags.NonPublic);
            var contextType = type.GetNestedType("DamageContext", BindingFlags.NonPublic);
            var batch = Activator.CreateInstance(batchType, true); var context = Activator.CreateInstance(contextType, true);
            batchType.GetField("Created").SetValue(batch, 100L);
            ((System.Collections.IList)batchType.GetField("Pending").GetValue(batch)).Add(context);
            var ready = type.GetMethod("EconomyBatchReady", BindingFlags.NonPublic | BindingFlags.Static);
            bool Call(bool force, long now) => (bool)ready.Invoke(null, new[] { batch, (object)force, now });
            Assert.False(Call(false, 200));
            Assert.True(Call(true, 200)); Assert.True(Call(false, 120101));
            contextType.GetField("Completed").SetValue(context, 1);
            Assert.True(Call(false, 200));
        }

        [Fact]
        public void Geometric_before_only_callbacks_are_not_taken_as_a_real_damage_operation()
        {
            var type = typeof(Clock).Assembly.GetType("SentisWatcher.Recording.DamageLog");
            var field = type.GetField("_damageCalls", BindingFlags.NonPublic | BindingFlags.Static);
            var previous = field.GetValue(null);
            try
            {
                var block = (Sandbox.Game.Entities.Cube.MySlimBlock)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Sandbox.Game.Entities.Cube.MySlimBlock));
                var callType = type.GetNestedType("DamageCall", BindingFlags.NonPublic);
                var call = Activator.CreateInstance(callType, true);
                callType.GetField("Block").SetValue(call, block);
                var calls = (System.Collections.IList)Activator.CreateInstance(field.FieldType);
                calls.Add(call); field.SetValue(null, calls);
                type.GetField("_damageDepth", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, 1);
                var before = type.GetMethod("EconomyTouch", BindingFlags.NonPublic | BindingFlags.Static);
                var info = new VRage.Game.ModAPI.MyDamageInformation(true, 1, VRage.Game.MyDamageType.Deformation, 0);
                before.Invoke(null, new object[] { block, info });
                Assert.False((bool)callType.GetField("Observed").GetValue(call));
                Assert.Null(callType.GetField("Context").GetValue(call));
            }
            finally
            {
                field.SetValue(null, previous);
                type.GetField("_damageDepth", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, 0);
            }
        }
        public void Dispose() { try { Directory.Delete(_folder, true); } catch { } }
    }
}

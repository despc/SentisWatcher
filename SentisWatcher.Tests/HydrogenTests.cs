using System;
using System.Collections.Specialized;
using System.IO;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using SentisWatcher.Recording;
using SentisWatcher.Storage;
using SentisWatcher.Web;
using Xunit;

namespace SentisWatcher.Tests
{
    public class HydrogenTests
    {
        [Fact]
        public void Tank_transfers_are_not_burn_and_frozen_rates_are_not_extrapolated()
        {
            var type = typeof(WebData).Assembly.GetType("SentisWatcher.Recording.HydrogenTotals");
            Assert.NotNull(type);
            foreach (var frozen in new[] { false, true })
            {
                var totals = Activator.CreateInstance(type, new object[] { frozen });
                var add = type.GetMethod("Add");
                add.Invoke(totals, new object[] { "tank", 1000d, 2000d, 0d, 0d, 0d, 500d, 300d });
                add.Invoke(totals, new object[] { "engine", 0d, 0d, 50d, 0d, 5d, 40d, 0d });
                add.Invoke(totals, new object[] { "generator", 0d, 0d, 0d, 20d, 0d, 0d, 0d });
                double Value(string field) => (double)type.GetField(field).GetValue(totals);
                Assert.Equal(1000d, Value("Stored"));
                Assert.Equal(50d, Value("Buffer"));
                Assert.Equal(frozen ? 0 : 5d, Value("Consumed"));
                Assert.Equal(frozen ? 0 : 20d, Value("Produced"));
                Assert.Equal(frozen ? 0 : 500d, Value("TankIn"));
                Assert.Equal(frozen ? 0 : 300d, Value("TankOut"));
            }
        }

        [Fact]
        public void Samples_survive_restart_and_midnight_with_exact_grid_ids_and_separate_flows()
        {
            var folder = Path.Combine(Path.GetTempPath(), "watcher-hydrogen-" + Guid.NewGuid().ToString("N"));
            var t = Clock.ToMs(new DateTime(2026, 10, 30, 23, 59, 55, DateTimeKind.Utc));
            const long grid = 105745445387715906;
            try
            {
                using (var writer = new WatcherStore(folder, () => 100000))
                {
                    var table = (Table)Enum.Parse(typeof(Table), "Hydrogen");
                    writer.Add(new Row(table, t, t, grid, "Fixture", 1000d, 2000d, 50d, 20d, 5d, 100d, 0d, 0, "[{\"name\":\"Engine\",\"consumed\":5}]"));
                    writer.Add(new Row(table, t + 10000, t + 10000, grid, "Fixture", 900d, 2000d, 45d, 0d, 0d, 0d, 0d, 1, "[]"));
                    writer.Add(new Row(table, t, t, grid + 1, "Unrelated", 9999d, 9999d, 0d, 0d, 0d, 0d, 0d, 0, "[]"));
                }
                using (var store = new WatcherStore(folder, () => 100000))
                {
                    var data = JObject.Parse(JsonConvert.SerializeObject(new WebData(store).Hydrogen(grid.ToString(), t, t + 10000)));
                    var series = Assert.Single((JArray)data["series"]);
                    Assert.Equal(grid.ToString(), (string)series["id"]);
                    Assert.Equal(2, ((JArray)series["points"]).Count);
                    Assert.Equal(5d, (double)series["points"][0]["consumed"]);
                    Assert.Equal(100d, (double)series["points"][0]["tankIn"]);
                    Assert.True((bool)series["points"][1]["frozen"]);
                    Assert.Empty((JArray)series["latest"]);
                }
            }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }

        [Fact]
        public void History_keeps_a_short_stock_drop_when_thinning_a_long_range()
        {
            var folder = Path.Combine(Path.GetTempPath(), "watcher-hydrogen-" + Guid.NewGuid().ToString("N"));
            const long start = 1793400000000;
            try
            {
                using (var writer = new WatcherStore(folder, () => 100000))
                    for (var i = 0; i < 5000; i++)
                        writer.Add(new Row((Table)Enum.Parse(typeof(Table), "Hydrogen"), start + i * 10000L,
                            start + i * 10000L, 123L, "Fixture", i == 1 ? 1d : 1000d, 2000d, 0d, 0d, 0d, 0d, 0d, 0, "[]"));
                using (var store = new WatcherStore(folder, () => 100000))
                {
                    var data = JObject.Parse(JsonConvert.SerializeObject(new WebData(store).Hydrogen("123", start, start + 5000 * 10000L)));
                    var points = (JArray)data["series"][0]["points"];
                    Assert.Contains(points, p => (double)p["stored"] == 1d);
                    Assert.InRange(points.Count, 1, 1500);
                    Assert.Equal(5000, (int)data["series"][0]["total"]);
                }
            }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }

        [Fact]
        public void Old_day_files_without_hydrogen_are_read_without_migration()
        {
            var folder = Path.Combine(Path.GetTempPath(), "watcher-hydrogen-" + Guid.NewGuid().ToString("N"));
            const long t = 1793400000000;
            try
            {
                using (var writer = new WatcherStore(folder, () => 100000))
                    writer.Add(new Row(Table.Names, t, 123L, "grid", "Fixture", 0L, null, t));
                using (var db = new System.Data.SQLite.SQLiteConnection("Data Source=" + DayFiles.PathOf(folder, Clock.Day(t))))
                {
                    db.Open();
                    using (var cmd = new System.Data.SQLite.SQLiteCommand("DROP TABLE hydrogen", db)) cmd.ExecuteNonQuery();
                }
                using (var store = new WatcherStore(folder, () => 100000))
                {
                    var data = JObject.Parse(JsonConvert.SerializeObject(new WebData(store).Hydrogen("123", t, t + 10000)));
                    Assert.Empty((JArray)data["series"]);
                    Assert.Throws<ArgumentException>(() => new WebData(store).Hydrogen("123", t + 1, t));
                    Assert.Throws<ArgumentException>(() => new WebData(store).Hydrogen("123", t, t + 8 * 86400000L));
                }
            }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }

        [Fact]
        public void Samples_are_written_on_a_change_and_a_heartbeat_only()
        {
            var t = Clock.ToMs(new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc));
            HydrogenTotals Totals(bool frozen, double stored, double consumed)
            {
                var totals = new HydrogenTotals(frozen);
                totals.Add("tank", stored, 2000d, 0d, 0d, 0d, 0d, 0d);
                totals.Add("consumer", 0d, 0d, 0d, 0d, consumed, consumed, 0d);
                return totals;
            }
            HydrogenSampler.Written Step(HydrogenSampler.Written last, long now, HydrogenTotals totals, bool frozen, string blocks, out bool withBlocks) =>
                HydrogenSampler.Decide(last, now, "Ship", totals, frozen, "1/tank/Tank;", blocks, out withBlocks);

            var first = Step(null, t, Totals(false, 1000, 5), false, "[a]", out var with);
            Assert.NotNull(first); Assert.True(with);
            Assert.Null(Step(first, t + 10000, Totals(false, 1000.2, 5.001), false, "[a]", out _));
            var burn = Step(first, t + 20000, Totals(false, 990, 5), false, "[b]", out with);
            Assert.NotNull(burn); Assert.False(with);
            Assert.Null(Step(burn, t + 30000, Totals(false, 990, 5), false, "[b]", out _));
            var list = Step(burn, t + 80000, Totals(false, 990, 5), false, "[b]", out with);
            Assert.NotNull(list); Assert.True(with);
            var rebuilt = HydrogenSampler.Decide(burn, t + 30000, "Ship", Totals(false, 990, 5), false, "2/tank/Tank;", "[c]", out with);
            Assert.NotNull(rebuilt); Assert.True(with);
            var beat = Step(first, t + HydrogenSampler.HeartbeatMs, Totals(false, 1000, 5), false, "[a]", out with);
            Assert.NotNull(beat); Assert.True(with);

            var frozen = Step(first, t + 10000, Totals(true, 1000, 5), true, "[f]", out with);
            Assert.NotNull(frozen); Assert.True(with);
            Assert.Null(Step(frozen, t + HydrogenSampler.HeartbeatMs + 20000, Totals(true, 1000, 5), true, "[f]", out _));
            Assert.NotNull(Step(frozen, t + 10000 + HydrogenSampler.FrozenHeartbeatMs, Totals(true, 1000, 5), true, "[f]", out _));
            Assert.NotNull(Step(frozen, t + 20000, Totals(false, 1000, 5), false, "[a]", out _));

            var lateNight = Clock.ToMs(new DateTime(2026, 11, 3, 23, 59, 55, DateTimeKind.Utc));
            var night = Step(null, lateNight, Totals(true, 1000, 0), true, "[f]", out _);
            Assert.NotNull(Step(night, lateNight + 10000, Totals(true, 1000, 0), true, "[f]", out with));
            Assert.True(with);
        }

        [Fact]
        public void A_sparse_history_holds_the_row_before_the_range_and_the_last_block_list()
        {
            var folder = Path.Combine(Path.GetTempPath(), "watcher-hydrogen-" + Guid.NewGuid().ToString("N"));
            var t = Clock.ToMs(new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc));
            const long grid = 42;
            try
            {
                using (var writer = new WatcherStore(folder, () => 100000))
                {
                    writer.Add(new Row(Table.Hydrogen, t, t, grid, "Fixture", 1000d, 2000d, 0d, 0d, 0d, 0d, 0d, 1, "[{\"name\":\"Tank\"}]"));
                    writer.Add(new Row(Table.Hydrogen, t + 3000000, t + 3000000, grid, "Fixture", 900d, 2000d, 0d, 0d, 0d, 0d, 0d, 1, ""));
                }
                using (var store = new WatcherStore(folder, () => 100000))
                {
                    var data = JObject.Parse(JsonConvert.SerializeObject(new WebData(store).Hydrogen(grid.ToString(), t + 600000, t + 3600000)));
                    var series = Assert.Single((JArray)data["series"]);
                    var points = (JArray)series["points"];
                    Assert.Equal(2, points.Count);
                    Assert.Equal(1000d, (double)points[0]["stored"]);
                    Assert.Equal("Tank", (string)series["latest"][0]["name"]);
                    Assert.Equal(HydrogenSampler.FrozenHeartbeatMs, (int)data["frozenHeartbeatMs"]);
                }
            }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }

        [Fact]
        public void Hydrogen_history_is_available_without_samples()
        {
            var folder = Path.Combine(Path.GetTempPath(), "watcher-hydrogen-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var store = new WatcherStore(folder, () => 100000))
                {
                    var result = new WebServer(store, 0).Api("hydrogen", new NameValueCollection { ["id"] = "100", ["from"] = "100000", ["to"] = "200000" });
                    Assert.NotNull(result);
                    var json = JObject.Parse(JsonConvert.SerializeObject(result));
                    Assert.Empty((JArray)json["series"]);
                }
            }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }
    }
}

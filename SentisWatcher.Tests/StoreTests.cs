using System;
using System.Data.SQLite;
using System.IO;
using SentisWatcher.Storage;
using Xunit;

namespace SentisWatcher.Tests
{
    /// <summary>The store on a real SQLite file: rows written in their day's file, queries, retention.</summary>
    public class StoreTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "watcher-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_folder, true); } catch { }
        }

        // the rows of these tests have fixed dates (September 2026): kept however long ago that was. With 14 days the

        // store deleted their day files at its start from 06.10.2026 on, and the tests failed

        private const int KeepAll = 100000;

        private static long At(int day, int hour) => Clock.ToMs(new DateTime(2026, 9, day, hour, 0, 0, DateTimeKind.Utc));

        private static long Count(WatcherStore store, DateTime day, string table)
        {
            using (var db = store.OpenRead(day))
            using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM " + table, db))
                return (long)cmd.ExecuteScalar();
        }

        [Fact]
        public void Rows_go_to_the_file_of_their_day_and_can_be_queried()
        {
            var store = new WatcherStore(_folder, () => KeepAll);
            store.Add(new Row(Table.Names, At(22, 23), 7L, "player", "Tester", 0L, 76561198000000001L, At(22, 23)));
            store.Add(new Row(Table.Names, At(22, 23), 99L, "grid", "Big Ship", 7L, null, At(22, 23)));
            store.Add(new Row(Table.PlayerPos, At(22, 23), At(22, 23), 7L, 100.0, 0.0, 0.0, 0.0, 0.0, 0.0, 100.0, 0L, 0L));
            store.Add(new Row(Table.PlayerPos, At(23, 1), At(23, 1), 7L, 5000.0, 0.0, 0.0, 0.0, 0.0, 0.0, 100.0, 0L, 99L));
            store.Add(new Row(Table.GridPos, At(23, 1), At(23, 1), 99L, 5000.0, 0.0, 0.0, 0.0, 0.0, -1.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 120, 7L, 0, 30.0));
            store.Add(new Row(Table.Events, At(23, 2), At(23, 2), "grind", 7L, 99L, 5000.0, 0.0, 0.0, 250.0, 12, "Grind by 1"));
            store.Add(new Row(Table.Inventories, At(23, 2), At(23, 2), 555L, 0, 99L, 7L, "Ore/Iron:10", 0.1, 1.0));
            store.Add(new Row(Table.Alerts, At(23, 3), At(23, 3), "dupe_transfer", 7L, 99L, "made 60 Iron"));
            store.Dispose();

            Assert.True(File.Exists(DayFiles.PathOf(_folder, new DateTime(2026, 9, 22))));
            Assert.True(File.Exists(DayFiles.PathOf(_folder, new DateTime(2026, 9, 23))));
            var reader = new WatcherStore(_folder, () => KeepAll);
            try
            {
                Assert.Equal(1, Count(reader, new DateTime(2026, 9, 22), "player_pos"));
                Assert.Equal(1, Count(reader, new DateTime(2026, 9, 23), "player_pos"));
                Assert.Equal(1, Count(reader, new DateTime(2026, 9, 23), "player_pos_space"));
                Assert.Equal(1, Count(reader, new DateTime(2026, 9, 23), "grid_pos_space"));

                var query = new WatcherQuery(reader);
                var from = At(22, 0);
                var to = At(23, 12);
                Assert.Contains((7L, "Tester"), query.Find("player", "test", from, to));
                Assert.Contains((7L, "Tester"), query.Find("player", "76561198000000001", from, to));

                var player = query.Player(7, "Tester", from, to, 10);
                Assert.Contains("positions: 2", player);
                Assert.Contains("grind", player);
                Assert.Contains("ALERT dupe_transfer", player);

                var grid = query.Grid(99, "Big Ship", from, to, 10);
                Assert.Contains("120 blocks", grid);
                Assert.Contains("grind 7: 250", grid);

                // near the second point only; the grid's box reaches 30 m around its centre
                var near = query.Near(5020, 0, 0, 10, from, to);
                Assert.Contains("players: none", near);     // 20 m away
                Assert.Contains("Big Ship", near);
                Assert.Contains("Tester", query.Near(5020, 0, 0, 25, from, to));
                Assert.Contains("players: none", query.Near(-5000, 0, 0, 10, from, to));

                Assert.Contains("alerts", query.Alerts(from, to, 10));
                Assert.Contains("dupe_transfer", query.Alerts(from, to, 10));
            }
            finally
            {
                reader.Dispose();
            }
        }

        [Fact]
        public void Day_files_past_the_retention_are_deleted_when_the_store_starts()
        {
            Directory.CreateDirectory(_folder);
            var today = DateTime.UtcNow.Date;
            var old = DayFiles.PathOf(_folder, today.AddDays(-20));
            var kept = DayFiles.PathOf(_folder, today.AddDays(-2));
            File.WriteAllText(old, "");
            File.WriteAllText(old + "-wal", "");
            File.WriteAllText(kept, "");
            var store = new WatcherStore(_folder, () => 14);
            store.Dispose();
            Assert.False(File.Exists(old));
            Assert.False(File.Exists(old + "-wal"));
            Assert.True(File.Exists(kept));
        }
    }
}

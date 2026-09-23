using System;
using System.Collections.Generic;
using System.Linq;
using SentisWatcher.Anomalies;
using SentisWatcher.Recording;
using SentisWatcher.Storage;
using VRageMath;
using Xunit;

namespace SentisWatcher.Tests
{
    public class ChangeFilterTests
    {
        private static PlayerState Player(long t, double x, long grid = 0, float health = 100) =>
            new PlayerState { Time = t, Position = new Vector3D(x, 0, 0), Grid = grid, Health = health };

        private static GridState Grid(long t, double x, Vector3 forward, int blocks = 10, long owner = 1, bool isStatic = false) =>
            new GridState { Time = t, Position = new Vector3D(x, 0, 0), Forward = forward, Up = Vector3.Up, Blocks = blocks, Owner = owner, IsStatic = isStatic };

        [Fact]
        public void A_player_is_written_first_then_only_on_change_or_heartbeat()
        {
            Assert.True(ChangeFilter.PlayerChanged(null, Player(0, 0)));
            Assert.False(ChangeFilter.PlayerChanged(Player(0, 0), Player(1000, 1)));
            Assert.True(ChangeFilter.PlayerChanged(Player(0, 0), Player(1000, ChangeFilter.PlayerMoveM)));
            Assert.True(ChangeFilter.PlayerChanged(Player(0, 0), Player(1000, 0, grid: 5)));
            Assert.True(ChangeFilter.PlayerChanged(Player(0, 0), Player(1000, 0, health: 50)));
            Assert.True(ChangeFilter.PlayerChanged(Player(0, 0), Player(ChangeFilter.PlayerHeartbeatMs, 0)));
        }

        [Fact]
        public void A_standing_grid_is_written_once_in_a_long_while()
        {
            var still = Grid(0, 0, Vector3.Forward);
            Assert.True(ChangeFilter.GridChanged(null, still));
            Assert.False(ChangeFilter.GridChanged(still, Grid(60_000, 1, Vector3.Forward)));
            Assert.True(ChangeFilter.GridChanged(still, Grid(ChangeFilter.GridHeartbeatMs, 0, Vector3.Forward)));
        }

        [Fact]
        public void A_grid_is_written_when_it_moves_turns_or_changes()
        {
            var still = Grid(0, 0, Vector3.Forward);
            Assert.True(ChangeFilter.GridChanged(still, Grid(1000, ChangeFilter.GridMoveM, Vector3.Forward)));
            var turned = Vector3.Normalize(Vector3.Forward + Vector3.Right * 0.2f);     // ~11 degrees
            Assert.True(ChangeFilter.GridChanged(still, Grid(1000, 0, turned)));
            var nudged = Vector3.Normalize(Vector3.Forward + Vector3.Right * 0.02f);    // ~1 degree
            Assert.False(ChangeFilter.GridChanged(still, Grid(1000, 0, nudged)));
            Assert.True(ChangeFilter.GridChanged(still, Grid(1000, 0, Vector3.Forward, blocks: 11)));
            Assert.True(ChangeFilter.GridChanged(still, Grid(1000, 0, Vector3.Forward, owner: 2)));
            Assert.True(ChangeFilter.GridChanged(still, Grid(1000, 0, Vector3.Forward, isStatic: true)));
        }
    }

    public class AggregatorTests
    {
        [Fact]
        public void Hits_of_a_window_become_one_row_per_kind_actor_and_target()
        {
            var a = new Aggregator();
            for (var i = 0; i < 10; i++) a.Add(1000 + i, "damage", 1, 100, "Bullet", 5, Vector3D.Zero);
            a.Add(1005, "damage", 2, 100, "Bullet", 7, Vector3D.Zero);
            a.Add(1006, "grind", 1, 100, "Grind", 3, Vector3D.Zero);
            Assert.Null(a.Take(1000 + Aggregator.WindowMs - 1));
            var done = a.Take(1000 + Aggregator.WindowMs);
            Assert.Equal(3, done.Count);
            var first = done.Single(d => d.Kind == "damage" && d.Actor == 1);
            Assert.Equal(50, first.Amount);
            Assert.Equal(10, first.Count);
            Assert.Equal(1000, first.First);
            Assert.Null(a.Take(long.MaxValue));
        }

        [Fact]
        public void Forcing_takes_an_unfinished_window()
        {
            var a = new Aggregator();
            a.Add(0, "weld", 1, 2, null, 1, Vector3D.Zero);
            Assert.Single(a.Take(1, force: true));
        }
    }

    public class InventoryCodecTests
    {
        private static readonly List<ItemStack> Items = new List<ItemStack>
        {
            new ItemStack("Ore", "Iron", 1_234_500_000),
            new ItemStack("Component", "SteelPlate", 10_000_000),
            new ItemStack("Gas", "Hydrogen", 5),
        };

        [Fact]
        public void Items_read_back_as_they_were_written()
        {
            var text = InventoryCodec.Encode(Items);
            Assert.Equal("Ore/Iron:1234.5;Component/SteelPlate:10;Gas/Hydrogen:0.000005", text);
            var back = InventoryCodec.Decode(text);
            Assert.Equal(Items.Select(i => (i.Type, i.Subtype, i.Raw)), back.Select(i => (i.Type, i.Subtype, i.Raw)));
            Assert.Empty(InventoryCodec.Decode(""));
        }

        [Fact]
        public void The_hash_changes_with_any_amount_and_not_otherwise()
        {
            var same = new List<ItemStack>(Items);
            Assert.Equal(InventoryCodec.Hash(Items), InventoryCodec.Hash(same));
            var more = new List<ItemStack>(Items) { [0] = new ItemStack("Ore", "Iron", 1_234_500_001) };
            Assert.NotEqual(InventoryCodec.Hash(Items), InventoryCodec.Hash(more));
            Assert.NotEqual(InventoryCodec.Hash(Items), InventoryCodec.Hash(Items.Take(2).ToList()));
        }

        [Fact]
        public void The_builder_prefix_is_dropped()
        {
            Assert.Equal("Ore", InventoryCodec.ShortType("MyObjectBuilder_Ore"));
            Assert.Equal("Custom", InventoryCodec.ShortType("Custom"));
        }
    }

    public class InvariantTests
    {
        [Fact]
        public void A_transfer_that_makes_items_is_caught()
        {
            Assert.False(Invariants.TransferMadeItems(100, 0, 40, 60, false));   // moved 60
            Assert.False(Invariants.TransferMadeItems(100, 0, 100, 0, false));   // nothing moved
            Assert.False(Invariants.TransferMadeItems(100, 0, 40, 50, false));   // lost some: not a dupe
            Assert.True(Invariants.TransferMadeItems(100, 0, 100, 60, false));   // the source kept all
            Assert.True(Invariants.TransferMadeItems(100, 0, 40, 61, false));
            Assert.True(Invariants.TransferMadeItems(100, 100, 120, 100, true)); // inside one inventory
            Assert.False(Invariants.TransferMadeItems(100, 100, 100, 100, true));
        }

        [Fact]
        public void Impossible_amounts_are_named()
        {
            Assert.NotNull(Invariants.BadAmount("Ore", -1));
            Assert.Null(Invariants.BadAmount("Ore", 1_500_000));
            Assert.NotNull(Invariants.BadAmount("Component", 1_500_000));
            Assert.Null(Invariants.BadAmount("Component", 3_000_000));
        }

        [Fact]
        public void Overfilled_inventories_and_tanks_are_caught()
        {
            Assert.False(Invariants.Overfilled(1.0, 1.0));
            Assert.False(Invariants.Overfilled(1.005, 1.0));
            Assert.True(Invariants.Overfilled(1.02, 1.0));
            Assert.False(Invariants.Overfilled(5, 0));
            Assert.False(Invariants.TankOverfilled(1.0));
            Assert.True(Invariants.TankOverfilled(1.01));
            Assert.True(Invariants.TankOverfilled(double.NaN));
        }

        [Fact]
        public void An_alert_about_one_thing_is_let_through_once_an_hour()
        {
            var limiter = new AlertLimiter();
            var now = new DateTime(2026, 9, 23, 12, 0, 0);
            Assert.True(limiter.Allow("dupe", 1, now));
            Assert.False(limiter.Allow("dupe", 1, now.AddMinutes(30)));
            Assert.True(limiter.Allow("dupe", 2, now.AddMinutes(30)));
            Assert.True(limiter.Allow("overfilled", 1, now.AddMinutes(30)));
            Assert.True(limiter.Allow("dupe", 1, now + AlertLimiter.Every));
        }
    }

    public class DayFilesTests
    {
        [Fact]
        public void A_day_file_is_named_by_its_utc_day()
        {
            var day = new DateTime(2026, 9, 23);
            Assert.Equal("watcher-2026-09-23.db", DayFiles.FileName(day));
            Assert.Equal(day, DayFiles.DayOf(@"C:\x\watcher-2026-09-23.db"));
            Assert.Null(DayFiles.DayOf("watcher-2026-13-01.db"));
            Assert.Null(DayFiles.DayOf("watch-20260923.txt"));
        }

        [Fact]
        public void Files_past_the_retention_are_deleted_counting_today()
        {
            var today = new DateTime(2026, 9, 23);
            var files = Enumerable.Range(0, 20).Select(d => DayFiles.FileName(today.AddDays(-d))).Append("other.db").ToList();
            var expired = DayFiles.Expired(files, today, 14);
            Assert.Equal(6, expired.Count);
            Assert.Contains(DayFiles.FileName(today.AddDays(-14)), expired);
            Assert.DoesNotContain(DayFiles.FileName(today.AddDays(-13)), expired);
            Assert.Equal(19, DayFiles.Expired(files, today, 0).Count);
        }
    }
}

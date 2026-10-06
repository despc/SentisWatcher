using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SentisWatcher.Storage;
using SentisWatcher.Web;
using Xunit;

namespace SentisWatcher.Tests
{
    /// <summary>The web view's API over a real day file (without HTTP), and its embedded page.</summary>
    public class WebTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "watcher-web-" + Guid.NewGuid().ToString("N"));
        private readonly WatcherStore _store;
        private const long Player = 144115188075855928;     // more than a JS number holds exactly
        private const long Grid = 105745445387715906;
        private const long Cargo = 139215407649218072;
        private const long Vault = 139215407649218999;
        private const long Base = 105745445387715999;       // the vault's grid
        private const long Body = 139215407649218100;       // the player's character
        private const long OldBody = 139215407649218101;    // one it left behind, never named
        private const long Kit = 139215407649218102;        // a block with no name of its own
        private const long Rover = 105745445387715888;      // the kit's grid

        // the rows of these tests have fixed dates (September 2026): kept however long ago that was. With 14 days the

        // store deleted their day files at its start from 06.10.2026 on, and the tests failed

        private const int KeepAll = 100000;

        private static long At(int minute) => Clock.ToMs(new DateTime(2026, 9, 23, 10, minute, 0, DateTimeKind.Utc));

        public WebTests()
        {
            var writer = new WatcherStore(_folder, () => KeepAll);
            writer.Add(new Row(Table.Names, At(0), Player, "player", "Tester", 0L, 76561198000000001L, At(0)));
            writer.Add(new Row(Table.Names, At(0), Grid, "grid", "Big Ship", Player, null, At(0)));
            writer.Add(new Row(Table.Names, At(0), Cargo, "block", "Cargo 1", Grid, null, At(0)));
            for (var m = 0; m < 10; m++)
                writer.Add(new Row(Table.PlayerPos, At(m), At(m), Player, 100.0 * m, 0.0, 0.0, 0.0, 0.0, 0.0, 100.0, 0L, Grid));
            writer.Add(new Row(Table.GridPos, At(1), At(1), Grid, 0.0, 0.0, 0.0, 0.0, 0.0, -1.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 120, Player, 0, 30.0));
            writer.Add(new Row(Table.Events, At(2), At(2), "transfer", Player, Cargo, 0.0, 0.0, 0.0, 40.0, null, "Ore/Iron"));
            writer.Add(new Row(Table.Inventories, At(1), At(1), Cargo, 0, Grid, Player, "Ore/Iron:100", 0.1, 15.6));
            writer.Add(new Row(Table.Inventories, At(3), At(3), Cargo, 0, Grid, Player, "Ore/Iron:60;Ingot/Iron:5", 0.1, 15.6));
            writer.Add(new Row(Table.Alerts, At(4), At(4), "dupe_transfer", Player, Cargo, "made iron"));
            writer.Add(new Row(Table.Planets, At(0), 7L, "EarthLike-1", "EarthLike", 0.0, 0.0, 60000.0, 60000.0, 57000.0, 63000.0, 70000.0, 120000.0, At(0)));
            writer.Add(new Row(Table.Meta, At(0), "sun", "0.5,0.5,0.7071"));
            // the ledger: a vault a plugin filled with platinum, then given to another player
            writer.Add(new Row(Table.Inventories, At(1), At(1), Vault, 0, Base, Player, "Ingot/Platinum:10", 0.1, 15.6));
            writer.Add(new Row(Table.Inventories, At(6), At(6), Vault, 0, Base, Player, "Ingot/Platinum:510", 0.1, 15.6, "plugin:Evil|Ingot/Platinum:+500"));
            writer.Add(new Row(Table.Inventories, At(7), At(7), Vault, 0, Base, 777L, "Ingot/Platinum:510", 0.1, 15.6));
            writer.Add(new Row(Table.Alerts, At(6), At(6), "external_source", Player, Vault, "plugin:Evil gave Tester: Ingot/Platinum +500"));
            // two hits close together and one far away: where most happened
            writer.Add(new Row(Table.Events, At(5), At(5), "damage", 0L, 0L, 5100.0, 5100.0, 5100.0, 10.0, 1, "Bullet"));
            writer.Add(new Row(Table.Events, At(5), At(5) + 1000, "damage", 0L, 0L, 5300.0, 5300.0, 5300.0, 10.0, 1, "Bullet"));
            writer.Add(new Row(Table.Events, At(5), At(5) + 2000, "damage", 0L, 0L, 55000.0, 0.0, 0.0, 10.0, 1, "Bullet"));
            // the player's bodies (inventories on no grid): one named as a character, one recorded before
            // characters were named; a kit with no name of its own (an empty name in the day file)
            writer.Add(new Row(Table.Names, At(0), Body, "character", "Tester", Player, null, At(0)));
            writer.Add(new Row(Table.Inventories, At(2), At(2), Body, 0, null, Player, "Ore/Stone:5", 0.1, 1.0));
            writer.Add(new Row(Table.Inventories, At(2), At(2), OldBody, 0, null, Player, "Ore/Stone:7", 0.1, 1.0));
            writer.Add(new Row(Table.Names, At(0), Kit, "block", "", Rover, null, At(0)));
            writer.Add(new Row(Table.Inventories, At(2), At(2), Kit, 1, Rover, Player, "Ingot/Iron:3", 0.1, 1.0));
            writer.Dispose();
            _store = new WatcherStore(_folder, () => KeepAll);
        }

        public void Dispose()
        {
            _store.Dispose();
            try { Directory.Delete(_folder, true); } catch { }
        }

        private JToken Call(string call, params (string, object)[] query)
        {
            var q = new NameValueCollection();
            foreach (var (k, v) in query) q[k] = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            var result = new WebServer(_store, 0).Api(call, q);
            Assert.NotNull(result);
            return JToken.Parse(JsonConvert.SerializeObject(result));
        }

        [Fact]
        public void Ids_go_out_as_strings()
        {
            var found = Call("search", ("q", "tes"), ("from", At(0)), ("to", At(59)));
            Assert.Equal(Player.ToString(), (string)found[0]["id"]);
            Assert.Equal(JTokenType.String, found[0]["id"].Type);
        }

        [Fact]
        public void A_track_has_its_points_in_order()
        {
            var track = Call("track", ("kind", "player"), ("id", Player), ("from", At(0)), ("to", At(59)));
            Assert.Equal("Tester", (string)track["name"]);
            var points = (JArray)track["points"];
            Assert.Equal(10, points.Count);
            Assert.Equal(At(0), (long)points[0][0]);
            Assert.Equal(900.0, (double)points[9][1]);
            Assert.Equal(Grid.ToString(), (string)points[9][5]);

            var grid = Call("track", ("kind", "grid"), ("id", Grid), ("from", At(0)), ("to", At(59)));
            Assert.Equal(30.0, (double)grid["points"][0][13]);
        }

        [Fact]
        public void A_track_starts_with_where_the_object_was_before_the_range()
        {
            // the last position is at minute 9: a range after it still has it
            var track = Call("track", ("kind", "player"), ("id", Player), ("from", At(20)), ("to", At(30)));
            var points = (JArray)track["points"];
            Assert.Single(points);
            Assert.Equal(At(9), (long)points[0][0]);
            // and a range before the first one gets the first after it
            var early = Call("track", ("kind", "grid"), ("id", Grid), ("from", At(0)), ("to", At(0) + 30_000));
            Assert.Equal(At(1), (long)early["points"][0][0]);
        }

        [Fact]
        public void Thinning_keeps_the_ends_and_the_count()
        {
            var points = Enumerable.Range(0, 10001).ToList();
            var thin = WebData.Thin(points, 5000);
            Assert.Equal(5000, thin.Count);
            Assert.Equal(0, thin[0]);
            Assert.Equal(10000, thin[thin.Count - 1]);
            Assert.Same(points, WebData.Thin(points, 20000));
        }

        [Fact]
        public void Events_come_with_the_names_of_those_involved()
        {
            var events = Call("events", ("id", Player), ("from", At(0)), ("to", At(59)));
            Assert.Equal("transfer", (string)events["events"][0]["kind"]);
            Assert.Equal("Cargo 1", (string)events["names"][Cargo.ToString()]);

            // the grid's events include those of its blocks: the transfer into its container
            var grid = Call("events", ("id", Grid), ("from", At(0)), ("to", At(59)));
            Assert.Contains(grid["events"], e => (string)e["kind"] == "transfer");
        }

        [Fact]
        public void An_inventory_at_a_moment_is_its_last_record_with_the_one_before()
        {
            var early = Call("inventory", ("kind", "grid"), ("id", Grid), ("t", At(2)));
            Assert.Single(early["inventories"]);
            Assert.Equal(100.0, (double)early["inventories"][0]["items"][0]["amount"]);
            Assert.Equal(JTokenType.Null, early["inventories"][0]["prev"].Type);

            var later = Call("inventory", ("kind", "grid"), ("id", Grid), ("t", At(30)));
            var inv = later["inventories"][0];
            Assert.Equal("Cargo 1", (string)inv["name"]);
            Assert.Equal(2, ((JArray)inv["items"]).Count);
            Assert.Equal(100.0, (double)inv["prev"]["items"][0]["amount"]);

            Assert.Empty(Call("inventory", ("kind", "grid"), ("id", Grid), ("t", At(0)))["inventories"]);
            Assert.Single(Call("inventory", ("kind", "entity"), ("id", Cargo), ("t", At(30)))["inventories"]);
        }

        [Fact]
        public void Near_finds_who_was_there()
        {
            var near = Call("near", ("x", 850), ("y", 0), ("z", 0), ("r", 100), ("from", At(0)), ("to", At(59)));
            Assert.Equal(Player.ToString(), (string)near["players"][0]["id"]);
            Assert.Empty(near["grids"]);
            Assert.Equal("Tester", (string)near["names"][Player.ToString()]);
        }

        [Fact]
        public void Alerts_and_days_are_listed()
        {
            Assert.Equal("dupe_transfer", (string)Call("alerts", ("from", At(0)), ("to", At(59)))[0]["kind"]);
            Assert.Contains("2026-09-23", Call("days").Select(d => (string)d));
        }

        [Fact]
        public void The_objects_of_a_range_come_most_active_first_with_their_counts()
        {
            var objects = Call("objects", ("from", At(0)), ("to", At(59)));
            var player = objects["players"].Single();
            Assert.Equal(Player.ToString(), (string)player["id"]);
            Assert.Equal("Tester", (string)player["name"]);
            Assert.Equal(10, (long)player["positions"]);
            Assert.Equal(1, (long)player["events"]);
            // the transfer into its container counts for the grid
            var grid = objects["grids"].Single();
            Assert.Equal(Grid.ToString(), (string)grid["id"]);
            Assert.Equal(1, (long)grid["events"]);

            Assert.Empty(Call("objects", ("from", At(40)), ("to", At(59)))["players"]);
        }

        [Fact]
        public void The_filter_applies_before_the_list_is_cut()
        {
            var objects = Call("objects", ("from", At(0)), ("to", At(59)), ("q", "big"));
            Assert.Empty(objects["players"]);
            Assert.Equal("Big Ship", (string)objects["grids"][0]["name"]);
            Assert.Equal(1, (int)objects["gridsTotal"]);
            Assert.Single(Call("objects", ("from", At(0)), ("to", At(59)), ("q", Grid.ToString().Substring(3, 8)))["grids"]);
        }

        [Fact]
        public void A_moment_counts_the_events_of_the_range_it_is_given()
        {
            // the transfer is at minute 2: a moment at minute 9 sees it only with the range
            var near = Call("moment", ("t", At(9)), ("window", 60_000));
            Assert.Equal(0, (long)near["players"].Single()["events"]);
            var ranged = Call("moment", ("t", At(9)), ("window", 60_000), ("from", At(0)), ("to", At(10)));
            Assert.Equal(1, (long)ranged["players"].Single()["events"]);
            Assert.Equal(At(0), (long)ranged["eventsFrom"]);
        }

        [Fact]
        public void A_moment_has_everyone_at_their_nearest_position_and_their_activity()
        {
            var moment = Call("moment", ("t", At(2) + 20_000), ("window", 120_000));
            var player = moment["players"].Single();
            Assert.Equal(200.0, (double)player["x"]);               // the minute-2 position is the nearest
            Assert.Equal(1, (long)player["events"]);                // the transfer
            var grid = moment["grids"].Single();
            Assert.Equal(120, (long)grid["blocks"]);
            Assert.Equal(1, (long)grid["events"]);                  // its container's transfer
            Assert.Equal("Big Ship", (string)grid["name"]);

            // the grid was written at minute 1: a moment ten minutes later still finds it (a standing grid
            // is written every ten minutes), the player only while within the window
            var later = Call("moment", ("t", At(11)), ("window", 60_000));
            Assert.Empty(later["players"]);
            Assert.Single(later["grids"]);
            Assert.Equal(0, (long)later["grids"][0]["events"]);
        }

        [Fact]
        public void Activity_is_counted_per_slice_of_the_range()
        {
            var activity = Call("activity", ("from", At(0)), ("to", At(59) + 60_000), ("buckets", 60));
            Assert.Equal(60_000, (long)activity["width"]);
            var slices = ((JArray)activity["buckets"]).ToDictionary(b => (long)b["b"], b => (JObject)b["counts"]);
            Assert.Equal(10, slices.Count);                          // minutes 0..9 have positions, the rest nothing
            Assert.Equal(1, (long)slices[2]["transfer"]);
            Assert.Equal(1, (long)slices[2]["@players"]);
            Assert.Equal(1, (long)slices[1]["@grids"]);
            Assert.Null(slices[5]["transfer"]);
        }

        [Fact]
        public void The_world_has_its_planets_and_the_sun()
        {
            var world = Call("world", ("t", At(30)));
            var planet = world["planets"].Single();
            Assert.Equal("EarthLike", (string)planet["generator"]);
            Assert.Equal(60000.0, (double)planet["radius"]);
            Assert.Equal(70000.0, (double)planet["atmosphere"]);
            Assert.Equal(0.7071, (double)world["sun"][2], 4);
            // a later day without its own file still gets the planets of the last one that has them
            Assert.Single(Call("world", ("t", At(30) + 3 * 86_400_000L))["planets"]);
        }

        [Fact]
        public void The_hotspot_is_where_most_events_with_a_place_were()
        {
            var hot = Call("hotspot", ("from", At(0)), ("to", At(59)));
            Assert.True((bool)hot["found"]);
            Assert.Equal(2, (long)hot["count"]);
            Assert.Equal(5200.0, (double)hot["x"], 3);
            Assert.Equal(At(5) + 500, (long)hot["t"]);
            Assert.False((bool)Call("hotspot", ("from", At(30)), ("to", At(59)))["found"]);
        }

        [Fact]
        public void The_ledger_of_a_player_tells_where_each_change_came_from()
        {
            var ledger = Call("ledger", ("kind", "player"), ("id", Player), ("from", At(5)), ("to", At(59)));
            Assert.Equal("Tester", (string)ledger["name"]);
            Assert.Equal(10.0, (double)ledger["start"]["Ingot/Platinum"]);
            Assert.Null(ledger["end"]["Ingot/Platinum"]);
            Assert.Equal(500.0, (double)ledger["sources"]["Ingot/Platinum"]["plugin:Evil"]);
            Assert.Equal(-510.0, (double)ledger["sources"]["Ingot/Platinum"]["ownership"]);
            Assert.Empty(ledger["unexplained"]);
            Assert.Equal("external_source", (string)ledger["alerts"].Single(a => (string)a["kind"] == "external_source")["kind"]);
            Assert.Equal(510.0, ((JArray)ledger["series"]["Ingot/Platinum"]).Max(v => (double)v));

            // the grid keeps the vault after it changed hands
            var grid = Call("ledger", ("kind", "grid"), ("id", Base), ("from", At(5)), ("to", At(59)));
            Assert.Equal(510.0, (double)grid["end"]["Ingot/Platinum"]);
            Assert.Null(grid["sources"]["Ingot/Platinum"]["ownership"]);

            var anomalies = Call("anomalies", ("from", At(0)), ("to", At(59)));
            Assert.Equal(2, ((JArray)anomalies["alerts"]).Count);
            Assert.Equal("Tester", (string)anomalies["names"][Player.ToString()]);
        }

        [Fact]
        public void Holdings_are_what_each_inventory_held_at_the_moment()
        {
            var grid = Call("holdings", ("kind", "grid"), ("id", Base), ("t", At(6) + 1000));
            var vault = grid["inventories"].Single();
            Assert.Equal(Vault.ToString(), (string)vault["entity"]);
            Assert.Equal(510.0, (double)vault["items"]["Ingot/Platinum"]);
            Assert.Equal(10.0, (double)vault["prev"]["Ingot/Platinum"]);
            Assert.Equal("plugin:Evil", (string)vault["flows"][0][0]);
            // given away at minute 7: the player holds it no more, the grid still does
            Assert.Single(Call("holdings", ("kind", "player"), ("id", Player), ("t", At(6)))["inventories"].Where(i => (string)i["entity"] == Vault.ToString()));
            Assert.Empty(Call("holdings", ("kind", "player"), ("id", Player), ("t", At(8)))["inventories"].Where(i => (string)i["entity"] == Vault.ToString()));
            Assert.Single(Call("holdings", ("kind", "grid"), ("id", Base), ("t", At(8)))["inventories"]);
        }

        [Fact]
        public void A_players_bodies_are_marked_and_an_empty_name_is_no_name()
        {
            var held = Call("holdings", ("kind", "player"), ("id", Player), ("t", At(5)));
            var inventories = held["inventories"];
            // no running game in the test: no body is the player's current one
            Assert.Equal("old", (string)inventories.Single(i => (string)i["entity"] == Body.ToString())["body"]);
            Assert.Equal("old", (string)inventories.Single(i => (string)i["entity"] == OldBody.ToString())["body"]);
            var kit = inventories.Single(i => (string)i["entity"] == Kit.ToString());
            Assert.Null((string)kit["body"]);
            Assert.Equal(1, (int)kit["inv"]);
            Assert.Equal("Tester", (string)held["names"][Body.ToString()]);
            Assert.Null(held["names"][Kit.ToString()]);
        }

        [Fact]
        public void The_last_run_leaves_what_was_not_gone_by_the_later_day()
        {
            var folder = Path.Combine(Path.GetTempPath(), "watcher-last-" + Guid.NewGuid().ToString("N"));
            try
            {
                var yesterday = Clock.ToMs(new DateTime(2026, 9, 22, 20, 0, 0, DateTimeKind.Utc));
                using (var writer = new WatcherStore(folder, () => KeepAll))
                {
                    writer.Add(new Row(Table.Inventories, yesterday, yesterday, OldBody, 0, null, Player, "Ore/Stone:7", 0.1, 1.0));
                    writer.Add(new Row(Table.Inventories, yesterday, yesterday, Cargo, 0, Grid, Player, "Ore/Iron:1", 0.1, 1.0));
                    writer.Add(new Row(Table.Inventories, At(1), At(1), Body, 0, null, Player, "Ore/Stone:5", 0.1, 1.0));
                    writer.Add(new Row(Table.Inventories, At(2), At(2), Body, 0, null, Player, "Ore/Stone:6", 0.1, 1.0));
                    writer.Add(new Row(Table.Inventories, At(1), At(1), Kit, 1, Rover, Player, "Ingot/Iron:3", 0.1, 1.0));
                    // the cargo went away today: its last row has no items
                    writer.Add(new Row(Table.Inventories, At(3), At(3), Cargo, 0, Grid, Player, null, 0.0, 0.0, "gone|Ore/Iron:-1"));
                }
                using (var reader = new WatcherStore(folder, () => KeepAll))
                {
                    var held = reader.LastHeld(new DateTime(2026, 9, 22), new DateTime(2026, 9, 23)).ToDictionary(h => (h.Entity, h.Inv));
                    Assert.Equal(3, held.Count);
                    Assert.Equal("Ore/Stone:6", held[(Body, 0)].Items);
                    Assert.Equal("Ore/Stone:7", held[(OldBody, 0)].Items);
                    Assert.Equal(Rover, held[(Kit, 1)].Grid);
                    Assert.Equal(Player, held[(Kit, 1)].Owner);
                    Assert.False(held.ContainsKey((Cargo, 0)));
                }
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        [Fact]
        public void The_inventories_page_is_embedded()
        {
            var resources = typeof(WebServer).Assembly.GetManifestResourceNames().Select(n => n.Replace('\\', '/')).ToList();
            Assert.Contains("Web/ledger.html", resources);
            Assert.Contains("Web/ledger.js", resources);
        }

        [Fact]
        public void An_unknown_call_is_no_call()
        {
            Assert.Null(new WebServer(_store, 0).Api("drop_tables", new NameValueCollection()));
        }

        [Fact]
        public void The_page_and_its_libraries_are_in_the_plugin()
        {
            var names = typeof(WebServer).Assembly.GetManifestResourceNames().Select(n => n.Replace('\\', '/')).ToList();
            foreach (var file in new[] { "Web/index.html", "Web/app.js", "Web/style.css", "Web/lib/three.module.min.js", "Web/lib/OrbitControls.js" })
                Assert.Contains(file, names);
            Assert.DoesNotContain(names, n => n.EndsWith(".cs"));
        }
    }
}

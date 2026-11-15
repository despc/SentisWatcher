using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SentisWatcher.Web;
using Xunit;

namespace SentisWatcher.Tests
{
    public sealed class PublicBattleApiTests
    {
        // Deliberately fake test credential, not a deployed secret.
        private const string Key = "unit-test-battles-key-not-production-0123456789";
        private const string A = "144115188075855928", B = "144115188075855929", Grid = "105745445387715906";
        private const long Now = 1800000000000;
        private static string Auth => "Bearer " + Key;
        private static NameValueCollection Query(params (string Key, string Value)[] values)
        {
            var q = new NameValueCollection();
            foreach (var v in values) q.Add(v.Key, v.Value);
            return q;
        }
        private static JObject Summary(string id = "123:45", long ended = Now - 1000000) => JObject.FromObject(new
        {
            id, title = "ZRG vs SVP", start = ended - 180000, end = ended - 120000, endedAt = ended,
            duration = 60000, ongoing = false, clippedStart = false, clippedEnd = false,
            damage = 100.0, combatDamage = 90.0, hits = 2, kills = 0, destroyed = 1,
            participants = new[] { "Alice", B }, factions = new[] { "[ZRG] Zerg", "[SVP] Vanguard" }, weapons = new[] { "Bullet:Railgun" },
            x = 9876543.125, y = 8888888.25, z = 7777777.5, steam = "76561198012345678",
        });
        private static JObject Person(string id, string name) => new JObject
        {
            ["id"] = id, ["name"] = name, ["faction"] = "[ZRG] Zerg", ["factionKey"] = "999999999999999999|[ZRG] Zerg", ["steam"] = "76561198012345678",
        };
        private static JObject Report(JObject summary = null) => JObject.FromObject(new
        {
            summary = summary ?? Summary(),
            participants = new[]
            {
                new { person = Person(A, "Alice"), dealt = 100, taken = 0, kills = 0, deaths = 0, destroyed = 1, lost = 0 },
                new { person = Person(B, B), dealt = 0, taken = 100, kills = 0, deaths = 0, destroyed = 0, lost = 1 },
            },
            breakdown = new[]
            {
                new { attacker = Person(A, "Alice"), victim = Person(B, B), attackerKind = "player", targetKind = "block", sourceId = Grid,
                    targetId = "105745445387715907", source = "SECRET-GRID-NAME", target = "SECRET-BASE-NAME", weapon = "Bullet:Railgun", relation = "enemy",
                    damage = 100, hits = 2, kills = 0, destroyed = 1, x = 9876543.125 },
            },
            timelineFrom = Now - 1180000, timelineTo = Now - 1120000,
            timeline = new[] { new { t = Now - 1180000, width = 60000, damage = 100, combatDamage = 90, x = 9876543.125 } },
            jumps = new[]
            {
                new { t = Now - 1150000, gridId = Grid, grid = "SECRET-GRID-NAME", distance = 20000, direction = "departure",
                    actors = new[] { new { id = A, name = "Alice", role = "pilot", steam = "76561198012345678" } }, x = 9876543.125 },
            },
            inventories = new[] { "SECRET-INVENTORY" },
        });
        private static object Read(long from, long to, string id) => new JObject
        {
            ["battles"] = new JArray(Summary()), ["report"] = id == null ? JValue.CreateNull() : Report(),
        };

        [Fact]
        public void Disabled_missing_key_remote_and_wrong_credentials_fail_before_reading_any_data()
        {
            var reads = 0;
            object Reader(long f, long t, string id) { reads++; return Read(f, t, id); }
            Assert.Equal(404, new PublicBattleApi(Reader, false, Key).Get("/battles", null, Auth).Status);
            Assert.Equal(503, new PublicBattleApi(Reader, true, null).Get("/battles", null, Auth).Status);
            var api = new PublicBattleApi(Reader, true, Key);
            Assert.Equal(403, api.Get("/battles", null, Auth, loopback: false).Status);
            foreach (var auth in new[] { null, "", "Basic " + Key, "Bearer wrong", "Bearer " + new string('x', 64), "Bearer " + new string('x', 500) })
                Assert.Equal(401, api.Get("/battles", null, auth).Status);
            Assert.Equal(0, reads);
        }

        [Fact]
        public void Only_finished_delayed_complete_battles_are_published()
        {
            var old = Summary();
            var ongoing = Summary("live:1"); ongoing["ongoing"] = true; ongoing["endedAt"] = JValue.CreateNull();
            var fresh = Summary("fresh:1", Now - 899999);
            var clipped = Summary("clipped:1"); clipped["clippedEnd"] = true;
            var api = new PublicBattleApi((f, t, id) => new JObject { ["battles"] = new JArray(old, ongoing, fresh, clipped) }, true, Key, clock: () => Now);
            var reply = api.Get("/battles", null, Auth);
            Assert.Equal(200, reply.Status);
            var json = JObject.Parse(reply.Json);
            Assert.Single(json["battles"]);
            Assert.Equal(PublicBattleProjection.Id("123:45"), (string)json["battles"][0]["id"]);
            Assert.Equal(Now - 900000, (long)json["availableThrough"]);
            Assert.Equal(5, new PublicBattleApi(Read, true, Key, delayMinutes: -1).DelayMinutes);
        }

        [Fact]
        public void Publication_allows_ship_names_but_not_coordinates_internal_ids_or_private_data()
        {
            var api = new PublicBattleApi(Read, true, Key, clock: () => Now);
            var list = api.Get("/battles", null, Auth);
            var id = (string)JObject.Parse(list.Json)["battles"][0]["id"];
            Assert.Matches("^[a-f0-9]{32}$", id);
            var reply = api.Get("/battles/" + id, null, Auth);
            Assert.Equal(200, reply.Status);
            foreach (var json in new[] { list.Json, reply.Json })
                foreach (var secret in new[] { A, B, Grid, "76561198012345678", "999999999999999999", "9876543.125", "SECRET-INVENTORY", "factionKey", "\"x\"", "inventories" })
                    Assert.DoesNotContain(secret, json);
            var report = JObject.Parse(reply.Json)["report"];
            Assert.Equal("p1", (string)report["participants"][0]["id"]);
            Assert.Equal("Alice", (string)report["participants"][0]["name"]);
            Assert.Equal("Неизвестный участник", (string)report["participants"][1]["name"]);
            Assert.Equal("e1", (string)report["breakdown"][0]["source"]);
            Assert.Equal("e1", (string)report["jumps"][0]["grid"]);
            Assert.Equal("SECRET-GRID-NAME", (string)report["entities"][0]["name"]);
            Assert.Contains("SECRET-BASE-NAME", reply.Json);
            Assert.Equal(100, (int)report["timeline"][0]["damage"]);
            Assert.Equal("Alice", (string)report["jumps"][0]["actors"][0]["name"]);
        }

        [Fact]
        public void Missing_ship_names_never_publish_numeric_ids_or_raw_jump_destinations()
        {
            var raw = Report();
            raw["breakdown"][0]["source"] = Grid;
            raw["breakdown"][0]["target"] = raw["breakdown"][0]["targetId"].DeepClone();
            raw["jumps"][0]["grid"] = " to=9876543.125;8888888.25;7777777.5";
            var result = PublicBattleProjection.Report(raw);
            Assert.Equal("Грид 1", (string)result["entities"][0]["name"]);
            Assert.DoesNotContain(Grid, result.ToString());
            Assert.DoesNotContain("9876543.125", result.ToString());
        }

        [Fact]
        public void Published_ship_names_are_bounded_control_free_and_do_not_expose_embedded_game_ids()
        {
            var raw = Report();
            raw["jumps"] = new JArray();
            raw["breakdown"][0]["source"] = "Молот <script>\n" + A + new string('x', 200);
            var name = (string)PublicBattleProjection.Report(raw)["entities"][0]["name"];
            Assert.StartsWith("Молот <script>", name); // plain JSON text; the site must HTML-escape it
            Assert.DoesNotContain(A, name);
            Assert.DoesNotContain("\n", name);
            Assert.True(name.Length <= 120);
        }

        [Fact]
        public void Arbitrary_ranges_parameters_duplicate_keys_and_private_routes_are_rejected()
        {
            var reads = 0;
            var api = new PublicBattleApi((f, t, id) => { reads++; return Read(f, t, id); }, true, Key, clock: () => Now);
            foreach (var q in new[] { Query(("from", "0")), Query(("hours", "169")), Query(("hours", "-1")), Query(("pageSize", "1000")), Query(("page", "0")), Query(("page", "1"), ("page", "2")), Query(("token", Key)) })
                Assert.Equal(400, api.Get("/battles", q, Auth).Status);
            foreach (var path in new[] { "/api/inventory", "/battles/../../inventory", "/battles/123:45", "/battles/" })
                Assert.Equal(404, api.Get(path, null, Auth).Status);
            Assert.Equal(0, reads);
        }

        [Fact]
        public void Catalog_details_and_unknown_ids_use_bounded_caches_and_support_pagination()
        {
            var reads = 0; var now = Now;
            var api = new PublicBattleApi((f, t, id) => { reads++; return Read(f, t, id); }, true, Key, clock: () => now);
            var id = PublicBattleProjection.Id("123:45");
            Assert.Equal(200, api.Get("/battles", Query(("page", "2"), ("pageSize", "1")), Auth).Status);
            Assert.Equal(200, api.Get("/battles", null, Auth).Status);
            Assert.Equal(1, reads);
            Assert.Equal(404, api.Get("/battles/" + new string('a', 32), null, Auth).Status);
            Assert.Equal(2, reads); // Expand membership to 168h once, without loading a report for an unknown ID.
            Assert.Equal(200, api.Get("/battles/" + id, null, Auth).Status);
            Assert.Equal(200, api.Get("/battles/" + id, null, Auth).Status);
            Assert.Equal(3, reads);
            // the catalog lives CatalogMs; a published report is kept across its refreshes (it does not change)
            now += 60000;
            Assert.Equal(200, api.Get("/battles/" + id, null, Auth).Status);
            Assert.Equal(3, reads);
            now += PublicBattleApi.CatalogMs;
            Assert.Equal(200, api.Get("/battles/" + id, null, Auth).Status);
            Assert.Equal(4, reads);
        }

        [Fact]
        public void Unknown_ids_widen_the_catalog_once_in_its_lifetime_and_reports_are_made_a_few_a_minute()
        {
            var reads = new System.Collections.Generic.List<(long From, long To, string Id)>(); var now = Now;
            var many = Enumerable.Range(0, 20).Select(i => Summary("b:" + i)).ToArray();
            var api = new PublicBattleApi((f, t, id) =>
            {
                reads.Add((f, t, id));
                return id == null ? new JObject { ["battles"] = new JArray(many.Select(m => m.DeepClone())) }
                    : new JObject { ["report"] = Report(many.First(m => (string)m["id"] == id)) };
            }, true, Key, clock: () => now);
            Assert.Equal(200, api.Get("/battles", null, Auth).Status);
            var random = new Random(5);
            string Hex() { var b = new byte[16]; random.NextBytes(b); return string.Concat(b.Select(x => x.ToString("x2"))); }
            for (var i = 0; i < 50; i++) Assert.Equal(404, api.Get("/battles/" + Hex(), null, Auth).Status);
            Assert.Equal(2, reads.Count); // the 24 h list, and one widening to a week for the first unknown ID
            Assert.Equal(168 * 3600000L, reads[1].To - reads[1].From);
            // reports: ReportsPerMinute made, then busy with a Retry-After; kept ones are answered whatever
            var ids = many.Select(m => PublicBattleProjection.Id((string)m["id"])).ToList();
            for (var i = 0; i < PublicBattleApi.ReportsPerMinute; i++) Assert.Equal(200, api.Get("/battles/" + ids[i], null, Auth).Status);
            var busy = api.Get("/battles/" + ids[PublicBattleApi.ReportsPerMinute], null, Auth);
            Assert.Equal(503, busy.Status); Assert.True(busy.RetryAfter > 0);
            Assert.Equal(200, api.Get("/battles/" + ids[0], null, Auth).Status);
            Assert.Equal(2 + PublicBattleApi.ReportsPerMinute, reads.Count);
            now += 60000;
            Assert.Equal(200, api.Get("/battles/" + ids[PublicBattleApi.ReportsPerMinute], null, Auth).Status);
        }

        [Fact]
        public void Catalog_reads_only_the_requested_window_and_old_deep_links_expand_membership_without_prefetching_reports()
        {
            var reads = new System.Collections.Generic.List<(long From, long To, string Id)>();
            var recent = Summary(); var older = Summary("older:1", Now - 5L * 86400000);
            var api = new PublicBattleApi((from, to, id) =>
            {
                reads.Add((from, to, id));
                return id == null ? new JObject { ["battles"] = new JArray(recent.DeepClone(), older.DeepClone()) }
                    : new JObject { ["report"] = Report(id == "older:1" ? older : recent) };
            }, true, Key, clock: () => Now);
            var list = JObject.Parse(api.Get("/battles", Query(("hours", "24")), Auth).Json);
            Assert.Single(list["battles"]); Assert.Single(reads);
            Assert.Null(reads[0].Id); Assert.Equal(24 * 3600000L, reads[0].To - reads[0].From);
            Assert.Equal(200, api.Get("/battles", Query(("hours", "72")), Auth).Status);
            Assert.Equal(72 * 3600000L, reads[1].To - reads[1].From);
            Assert.Equal(200, api.Get("/battles", Query(("hours", "24"), ("page", "2")), Auth).Status);
            Assert.Equal(2, reads.Count);
            Assert.Equal(200, api.Get("/battles/" + PublicBattleProjection.Id("older:1"), null, Auth).Status);
            Assert.Equal(168 * 3600000L, reads[2].To - reads[2].From);
            Assert.Null(reads[2].Id); Assert.Equal("older:1", reads[3].Id);
        }

        [Fact]
        public void Incremental_hour_filters_share_three_bounded_scan_windows_instead_of_forcing_a_scan_per_hour()
        {
            var reads = 0;
            var api = new PublicBattleApi((f, t, id) => { reads++; return Read(f, t, id); }, true, Key, clock: () => Now);
            foreach (var hours in new[] { 1, 2, 12, 24 }) Assert.Equal(200, api.Get("/battles", Query(("hours", hours.ToString())), Auth).Status);
            Assert.Equal(1, reads);
            foreach (var hours in new[] { 25, 30, 48, 72 }) Assert.Equal(200, api.Get("/battles", Query(("hours", hours.ToString())), Auth).Status);
            Assert.Equal(2, reads);
            foreach (var hours in new[] { 73, 100, 168, 24 }) Assert.Equal(200, api.Get("/battles", Query(("hours", hours.ToString())), Auth).Status);
            Assert.Equal(3, reads);
        }

        [Fact]
        public void Detail_is_rechecked_so_a_changed_or_resumed_battle_is_not_leaked()
        {
            var api = new PublicBattleApi((f, t, id) =>
            {
                if (id == null) return Read(f, t, id);
                var report = Report(); report["summary"]["ongoing"] = true;
                return new JObject { ["report"] = report };
            }, true, Key, clock: () => Now);
            Assert.Equal(404, api.Get("/battles/" + PublicBattleProjection.Id("123:45"), null, Auth).Status);
        }

        [Fact]
        public void Rate_limit_is_global_bounded_and_resets_after_a_minute()
        {
            var now = Now;
            var api = new PublicBattleApi(Read, true, Key, clock: () => now);
            for (var i = 0; i < PublicBattleApi.RequestsPerMinute; i++) Assert.Equal(200, api.Get("/battles", null, Auth).Status);
            var reply = api.Get("/battles", null, Auth);
            Assert.Equal(429, reply.Status); Assert.Equal(60, reply.RetryAfter);
            now += 60000;
            Assert.Equal(200, api.Get("/battles", null, Auth).Status);
        }

        [Fact]
        public async Task Concurrent_database_work_returns_busy_instead_of_queuing_requests()
        {
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var api = new PublicBattleApi((f, t, id) => { entered.Set(); Assert.True(release.Wait(10000)); return Read(f, t, id); }, true, Key, clock: () => Now);
                var first = Task.Run(() => api.Get("/battles", null, Auth));
                Assert.True(entered.Wait(10000));
                try
                {
                    var timer = Stopwatch.StartNew();
                    Assert.Equal(503, api.Get("/battles", null, Auth).Status);
                    Assert.True(timer.ElapsedMilliseconds < 1000);
                }
                finally { release.Set(); }
                Assert.Equal(200, (await first).Status);
            }
        }

        [Fact]
        public void A_missing_key_file_is_made_private_once_and_an_existing_one_is_never_touched()
        {
            var folder = Path.Combine(Path.GetTempPath(), "watcher-key-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = PublicBattleApi.DefaultKeyFile(folder);
                Assert.True(PublicBattleApi.EnsureKeyFile(path));
                var key = PublicBattleApi.ReadKeyFile(path);
                Assert.NotNull(key);
                Assert.Equal(43, key.Length);
                Assert.False(PublicBattleApi.EnsureKeyFile(path));
                Assert.Equal(key, PublicBattleApi.ReadKeyFile(path));
                Assert.NotEqual(key, PublicBattleApi.ReadKeyFile(PublicBattleApi.EnsureKeyFile(path + "2") ? path + "2" : null));
                foreach (var security in new System.Security.AccessControl.FileSystemSecurity[] { File.GetAccessControl(path), Directory.GetAccessControl(Path.GetDirectoryName(path)) })
                {
                    Assert.True(security.AreAccessRulesProtected);
                    var who = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>()
                        .Select(r => r.IdentityReference.Value).ToList();
                    Assert.DoesNotContain(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null).Value, who);
                    Assert.DoesNotContain(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null).Value, who);
                    Assert.Contains(System.Security.Principal.WindowsIdentity.GetCurrent().User.Value, who);
                }
                var api = new PublicBattleApi(Read, true, key, clock: () => Now);
                Assert.Equal(200, api.Get("/battles", null, "Bearer " + key).Status);
            }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }

        [Fact]
        public void Catalog_and_projection_limits_are_explicit_and_old_records_are_not_addressable()
        {
            var items = new JArray(Enumerable.Range(0, PublicBattleApi.MaxCatalog + 2).Select(i => Summary("battle:" + i)));
            items.Add(Summary("too-old", Now - 10L * 86400000));
            var api = new PublicBattleApi((f, t, id) => new JObject { ["battles"] = items }, true, Key, clock: () => Now);
            var list = JObject.Parse(api.Get("/battles", Query(("pageSize", "50")), Auth).Json);
            Assert.Equal(50, list["battles"].Count());
            Assert.Equal(PublicBattleApi.MaxCatalog, (int)list["total"]);
            Assert.True((bool)list["truncated"]);
            Assert.Equal(404, api.Get("/battles/" + PublicBattleProjection.Id("too-old"), null, Auth).Status);
            var raw = Report();
            raw["breakdown"] = new JArray(Enumerable.Range(0, PublicBattleProjection.MaxBreakdown + 1).Select(i => raw["breakdown"][0].DeepClone()).ToList());
            var projected = PublicBattleProjection.Report(raw);
            Assert.Equal(PublicBattleProjection.MaxBreakdown, projected["breakdown"].Count());
            Assert.True((bool)projected["truncated"]);
        }

        [Fact]
        public void Secret_file_must_be_absolute_small_and_valid_and_is_not_reflected_in_errors()
        {
            var folder = Path.Combine(Path.GetTempPath(), "watcher-key-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var file = Path.Combine(folder, "key.txt");
                File.WriteAllText(file, Key + "\r\n");
                Assert.Equal(Key, PublicBattleApi.ReadKeyFile(file));
                File.WriteAllText(file, "short"); Assert.Null(PublicBattleApi.ReadKeyFile(file));
                File.WriteAllText(file, new string('a', 600)); Assert.Null(PublicBattleApi.ReadKeyFile(file));
                Assert.Null(PublicBattleApi.ReadKeyFile("relative.key"));
                Assert.Null(PublicBattleApi.ReadKeyFile(@"C:drive-relative.key"));
                Assert.Null(PublicBattleApi.ReadKeyFile(@"\root-relative.key"));
                Assert.Null(PublicBattleApi.ReadKeyFile(@"\\server\share\key"));
                Assert.Null(PublicBattleApi.ReadKeyFile(Path.Combine(folder, "missing")));
            }
            finally { Directory.Delete(folder, true); }
        }
    }
}

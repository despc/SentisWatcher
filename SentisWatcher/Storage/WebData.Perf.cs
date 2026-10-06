using System;
using System.Data.SQLite;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SentisWatcher.Storage
{
    public sealed partial class WebData
    {
        /// <summary>
        /// The server's load over the range (table perf), put together into at most <paramref name="points"/>
        /// points: averages averaged (weighted by frames), worst frames the worst, collections added up, memory
        /// averaged; the profiler's other blocks likewise, by name, and the session components (each frame) the same way.
        /// </summary>
        public object Perf(long from, long to, int points)
        {
            points = Math.Max(50, Math.Min(points, 3000));
            var width = Math.Max(1, (to - from) / points);
            var buckets = new SortedDictionary<long, Bucket>();
            foreach (var db in Days(from, to))
                using (db)
                {
                    if (!HasTable(db, "perf")) continue;
                    // (the session components by name only in the files written since 05.10.2026)
                    var withComponents = HasColumn(db, "perf", "components");
                    // (the number of grids since 06.10.2026)
                    var withGrids = withComponents && HasColumn(db, "perf", "grids");
                    using (var cmd = Command(db, "SELECT t, frames, frame, frame_max, physics, physics_max, gc0, gc1, gc2, gc_time, managed_mb, private_mb, working_mb, sim, players, blocks" +
                                                 (withComponents ? ", components" : "") + (withGrids ? ", grids" : "") + " FROM perf WHERE t BETWEEN @a AND @b ORDER BY t", ("@a", from), ("@b", to)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var t = r.GetInt64(0);
                            var key = (t - from) / width;
                            if (!buckets.TryGetValue(key, out var b)) buckets[key] = b = new Bucket { T = from + key * width + width / 2 };
                            var timed = r.IsDBNull(1) ? 0 : r.GetInt64(1);         // 0: the frame was not timed (an older version)
                            var frames = Math.Max(1, timed);
                            b.Rows++;
                            b.Frames += timed;
                            b.Frame += D(r, 2) * frames;
                            b.FrameMax = Math.Max(b.FrameMax, D(r, 3));
                            b.Physics += D(r, 4) * frames;
                            b.PhysicsMax = Math.Max(b.PhysicsMax, D(r, 5));
                            b.Gc0 += (long)D(r, 6); b.Gc1 += (long)D(r, 7); b.Gc2 += (long)D(r, 8);
                            b.GcTime += D(r, 9);
                            b.Managed += D(r, 10); b.Private += D(r, 11); b.Working += D(r, 12);
                            b.Sim += D(r, 13);
                            b.Players = Math.Max(b.Players, (int)D(r, 14));
                            if (!r.IsDBNull(15)) AddParts(b.Blocks, r.GetString(15), frames);
                            if (withComponents && !r.IsDBNull(16)) AddParts(b.Components, r.GetString(16), frames);
                            if (withGrids && !r.IsDBNull(17)) b.Grids = Math.Max(b.Grids, (int)D(r, 17));
                        }
                }
            var list = buckets.Values.Select(b =>
            {
                var frames = (double)Math.Max(1, b.Frames);
                var n = Math.Max(1, b.Rows);
                var timed = b.Frames > 0;
                object T(double v) => timed ? (object)R(v) : null;
                return new Dictionary<string, object>
                {
                    ["t"] = b.T,
                    ["frame"] = T(b.Frame / frames), ["frameMax"] = T(b.FrameMax),
                    ["physics"] = T(b.Physics / frames), ["physicsMax"] = T(b.PhysicsMax),
                    ["logic"] = T(Math.Max(0, (b.Frame - b.Physics) / frames)),
                    ["gc0"] = b.Gc0, ["gc1"] = b.Gc1, ["gc2"] = b.Gc2, ["gcTime"] = R(b.GcTime / n),
                    ["managed"] = R(b.Managed / n), ["private"] = R(b.Private / n), ["working"] = R(b.Working / n),
                    ["sim"] = R(b.Sim / n), ["players"] = b.Players, ["grids"] = b.Grids < 0 ? null : (object)b.Grids,
                    ["blocks"] = timed ? b.Blocks.ToDictionary(p => p.Key, p => new[] { R(p.Value.Sum / frames), R(p.Value.Max) }) : null,
                    ["components"] = timed && b.Components.Count > 0 ? b.Components.ToDictionary(p => p.Key, p => new[] { Math.Round(p.Value.Sum / frames, 4), R(p.Value.Max) }) : null,
                };
            }).ToList();
            return new Dictionary<string, object> { ["from"] = from, ["to"] = to, ["width"] = width, ["points"] = list };
        }

        /// <summary>Who loaded the game thread over the range (table load), the heaviest of each kind.</summary>
        public object Load(long from, long to, int top)
        {
            var report = LoadReport.Read(Days(from, to), from, to);
            object Items(IEnumerable<LoadReport.Item> items) => items.Take(top).Select(i => new Dictionary<string, object>
            {
                ["id"] = i.Id, ["name"] = i.Name, ["owner"] = i.Owner, ["ownerId"] = i.OwnerId,
                ["ms"] = Math.Round(i.Ms, 4), ["max"] = R(i.Max), ["grids"] = i.Grids,
            }).ToList();
            var kinds = report.ByKind.ToDictionary(p => p.Key, p => Items(p.Value));
            kinds["player"] = Items(report.Players);
            return new Dictionary<string, object>
            {
                ["frames"] = report.Frames, ["frame"] = R(report.FrameMs), ["frameMax"] = R(report.FrameMax),
                ["on"] = SentisWatcherPlugin.Config?.LoadSampling == true, ["kinds"] = kinds,
            };
        }

        /// <summary>
        /// One kind of load (session components, plugins) over time: for each minute row, each one's time in an average
        /// frame and its worst frame; the <paramref name="top"/> heaviest over the range by name, the others summed.
        /// </summary>
        public object LoadSeries(long from, long to, string kind, int top) =>
            Series(from, to, top, kind == Recording.PbSampler.Kind, new[] { kind ?? "" }, (k, name) => name);

        /// <summary>
        /// The grids over time, the <paramref name="top"/> heaviest: by "physics" - each one's share of the physics step
        /// (LoadSampler.GridPhysics, an estimate), by anything else - its logic: the updates of its blocks on the game
        /// thread and the ones the game runs in parallel (while the game thread waits). By the grid's name.
        /// </summary>
        public object GridSeries(long from, long to, string what, int top)
        {
            const string parallelGrid = "grid: ";
            return what == "physics"
                ? Series(from, to, top, false, new[] { Recording.LoadSampler.GridPhysics }, (k, name) => name)
                : Series(from, to, top, false, new[] { Recording.LoadSampler.Grid, Recording.LoadSampler.Parallel },
                    (k, name) => k == Recording.LoadSampler.Grid ? name : name.StartsWith(parallelGrid) ? name.Substring(parallelGrid.Length) : null);
        }

        /// <summary>Rows of the kinds over time by name (<paramref name="nameOf"/> gives the name a row goes by, null to leave it out).</summary>
        private object Series(long from, long to, int top, bool own, string[] kinds, Func<string, string, string> nameOf)
        {
            var times = new SortedSet<long>();
            var rows = new List<(long T, string Name, double Ms, double Max)>();
            var owners = new Dictionary<string, string>();
            // (for the block types: how many updates of the type a frame, averaged over its rows)
            var counts = new Dictionary<string, (double Sum, int Rows)>();
            // (own: the scripts' rows come at their own minute marks, the samples' marks are not theirs)
            foreach (var db in Days(from, to))
                using (db)
                {
                    using (var check = new SQLiteCommand("SELECT 1 FROM sqlite_master WHERE type='table' AND name='load'", db))
                        if (check.ExecuteScalar() == null) continue;
                    var withCount = WatcherStore.HasColumn(db, "load", "count");
                    using (var cmd = new SQLiteCommand("SELECT t, kind, name, ms, max_ms, owner_name" + (withCount ? ", count" : "") + " FROM load WHERE t BETWEEN @a AND @b AND (kind=@k OR kind=@k2 OR kind=@total)", db))
                    {
                        cmd.Parameters.AddWithValue("@a", from);
                        cmd.Parameters.AddWithValue("@b", to);
                        cmd.Parameters.AddWithValue("@k", kinds[0]);
                        cmd.Parameters.AddWithValue("@k2", kinds.Length > 1 ? kinds[1] : kinds[0]);
                        cmd.Parameters.AddWithValue("@total", Recording.LoadSampler.Total);
                        using (var r = cmd.ExecuteReader())
                            while (r.Read())
                            {
                                var t = r.GetInt64(0);
                                var total = r.GetString(1) == Recording.LoadSampler.Total;
                                if (total && own) continue;
                                // (to the minute for the scripts: a block's rows of one flush share the mark)
                                times.Add(t);
                                if (total) continue;
                                var name = nameOf(r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2));
                                if (name == null) continue;
                                rows.Add((t, name, r.IsDBNull(3) ? 0 : r.GetDouble(3), r.IsDBNull(4) ? 0 : r.GetDouble(4)));
                                if (!r.IsDBNull(5) && r.GetString(5).Length > 0) owners[name] = r.GetString(5);
                                if (withCount && !r.IsDBNull(6))
                                {
                                    counts.TryGetValue(name, out var was);
                                    counts[name] = (was.Sum + r.GetDouble(6), was.Rows + 1);
                                }
                            }
                    }
                }
            var t2i = times.Select((t, i) => (t, i)).ToDictionary(p => p.t, p => p.i);
            var heaviest = rows.GroupBy(r => r.Name).OrderByDescending(g => g.Sum(r => r.Ms)).Take(Math.Max(1, top)).Select(g => g.Key).ToList();
            var chosen = new HashSet<string>(heaviest);
            double[] Zeros() => new double[times.Count];
            var ms = heaviest.ToDictionary(n => n, n => Zeros());
            var max = heaviest.ToDictionary(n => n, n => Zeros());
            var others = Zeros();
            var othersMax = Zeros();
            foreach (var r in rows)
            {
                var i = t2i[r.T];
                if (chosen.Contains(r.Name))
                {
                    ms[r.Name][i] += r.Ms;
                    max[r.Name][i] = Math.Max(max[r.Name][i], r.Max);
                }
                else
                {
                    others[i] += r.Ms;
                    othersMax[i] = Math.Max(othersMax[i], r.Max);
                }
            }
            var series = heaviest.Select(n => new Dictionary<string, object>
            {
                ["name"] = n, ["ms"] = ms[n].Select(v => Math.Round(v, 4)).ToList(), ["max"] = max[n].Select(v => R(v)).ToList(),
                ["owner"] = owners.TryGetValue(n, out var owner) ? owner : "",
                ["count"] = counts.TryGetValue(n, out var count) && count.Rows > 0 ? (object)Math.Round(count.Sum / count.Rows, 1) : null,
            }).ToList();
            if (rows.Any(r => !chosen.Contains(r.Name)))
                series.Add(new Dictionary<string, object>
                {
                    ["name"] = "", ["ms"] = others.Select(v => Math.Round(v, 4)).ToList(), ["max"] = othersMax.Select(v => R(v)).ToList(),
                    // how many they are: "the others" of seventy block types is more than any one of the ten shown
                    ["others"] = rows.Where(r => !chosen.Contains(r.Name)).Select(r => r.Name).Distinct().Count(),
                });
            return new Dictionary<string, object>
            {
                ["t"] = times.ToList(), ["series"] = series, ["on"] = SentisWatcherPlugin.Config?.LoadSampling == true,
            };
        }

        /// <summary>
        /// The long frames written down over the range (table spikes: the frames over 16.7 ms among the timed ones - every
        /// frame while the detailed measurement runs), the longest first: when, how long, the collections in it, and its
        /// heaviest parts ("kind:name=ms").
        /// </summary>
        public object Spikes(long from, long to, int top)
        {
            var rows = new List<Dictionary<string, object>>();
            var total = 0;
            foreach (var db in Days(from, to))
                using (db)
                {
                    if (!HasTable(db, "spikes")) continue;
                    using (var count = Command(db, "SELECT COUNT(*) FROM spikes WHERE t BETWEEN @a AND @b", ("@a", from), ("@b", to)))
                        total += Convert.ToInt32(count.ExecuteScalar());
                    using (var cmd = Command(db, "SELECT t, frame_ms, gc0, gc1, gc2, untimed_ms, top FROM spikes WHERE t BETWEEN @a AND @b ORDER BY frame_ms DESC LIMIT @n",
                               ("@a", from), ("@b", to), ("@n", top)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            rows.Add(new Dictionary<string, object>
                            {
                                ["t"] = r.GetInt64(0), ["ms"] = R(D(r, 1)), ["gc"] = new[] { (int)D(r, 2), (int)D(r, 3), (int)D(r, 4) }, ["untimed"] = R(D(r, 5)),
                                ["parts"] = (r.IsDBNull(6) ? "" : r.GetString(6)).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(part =>
                                {
                                    var eq = part.LastIndexOf('=');
                                    var colon = part.IndexOf(':');
                                    if (eq < 0 || colon < 0 || colon > eq) return null;
                                    double.TryParse(part.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms);
                                    return new Dictionary<string, object> { ["kind"] = part.Substring(0, colon), ["name"] = part.Substring(colon + 1, eq - colon - 1), ["ms"] = ms };
                                }).Where(p => p != null).ToList(),
                            });
                }
            return new Dictionary<string, object>
            {
                ["total"] = total, ["rows"] = rows.OrderByDescending(x => (double)x["ms"]).Take(top).ToList(),
            };
        }

        /// <summary>A row's parts ('name:avg:max;...') into the bucket's sums, each average weighted by the row's frames.</summary>
        private static void AddParts(Dictionary<string, (double Sum, double Max)> into, string parts, double frames)
        {
            foreach (var part in parts.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var bits = part.Split(':');
                if (bits.Length < 3) continue;
                var avg = double.Parse(bits[1], CultureInfo.InvariantCulture);
                var max = double.Parse(bits[2], CultureInfo.InvariantCulture);
                into.TryGetValue(bits[0], out var was);
                into[bits[0]] = (was.Sum + avg * frames, Math.Max(was.Max, max));
            }
        }

        private static bool HasColumn(System.Data.SQLite.SQLiteConnection db, string table, string column)
        {
            using (var cmd = Command(db, "SELECT 1 FROM pragma_table_info(@t) WHERE name=@c", ("@t", table), ("@c", column)))
                return cmd.ExecuteScalar() != null;
        }

        private sealed class Bucket
        {
            public long T;
            public int Rows, Players, Grids = -1;
            public long Frames, Gc0, Gc1, Gc2;
            public double Frame, FrameMax, Physics, PhysicsMax, GcTime, Managed, Private, Working, Sim;
            public readonly Dictionary<string, (double Sum, double Max)> Blocks = new Dictionary<string, (double, double)>();
            public readonly Dictionary<string, (double Sum, double Max)> Components = new Dictionary<string, (double, double)>();
        }

        private static double D(System.Data.SQLite.SQLiteDataReader r, int column) => r.IsDBNull(column) ? 0 : Convert.ToDouble(r.GetValue(column), CultureInfo.InvariantCulture);

        private static double R(double v) => Math.Round(v, 3);

        private static bool HasTable(System.Data.SQLite.SQLiteConnection db, string table)
        {
            using (var cmd = Command(db, "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n", ("@n", table)))
                return cmd.ExecuteScalar() != null;
        }
    }
}

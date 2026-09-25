using System;
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
        /// averaged; the profiler's other blocks likewise, by name.
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
                    using (var cmd = Command(db, "SELECT t, frames, frame, frame_max, physics, physics_max, gc0, gc1, gc2, gc_time, managed_mb, private_mb, working_mb, sim, players, blocks " +
                                                 "FROM perf WHERE t BETWEEN @a AND @b ORDER BY t", ("@a", from), ("@b", to)))
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
                            if (!r.IsDBNull(15))
                                foreach (var part in r.GetString(15).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                                {
                                    var bits = part.Split(':');
                                    if (bits.Length < 3) continue;
                                    var avg = double.Parse(bits[1], CultureInfo.InvariantCulture);
                                    var max = double.Parse(bits[2], CultureInfo.InvariantCulture);
                                    b.Blocks.TryGetValue(bits[0], out var was);
                                    b.Blocks[bits[0]] = (was.Sum + avg * frames, Math.Max(was.Max, max));
                                }
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
                    ["sim"] = R(b.Sim / n), ["players"] = b.Players,
                    ["blocks"] = timed ? b.Blocks.ToDictionary(p => p.Key, p => new[] { R(p.Value.Sum / frames), R(p.Value.Max) }) : null,
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

        private sealed class Bucket
        {
            public long T;
            public int Rows, Players;
            public long Frames, Gc0, Gc1, Gc2;
            public double Frame, FrameMax, Physics, PhysicsMax, GcTime, Managed, Private, Working, Sim;
            public readonly Dictionary<string, (double Sum, double Max)> Blocks = new Dictionary<string, (double, double)>();
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

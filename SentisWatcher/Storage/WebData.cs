using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using SentisWatcher.Recording;

namespace SentisWatcher.Storage
{
    /// <summary>
    /// What the web view asks for, read from the day files. Ids go out as strings: entity ids are larger
    /// than a JavaScript number holds exactly.
    /// </summary>
    public sealed partial class WebData
    {
        /// <summary>Points of a track above this are thinned out evenly (the last one always stays).</summary>
        public const int MaxTrackPoints = 5000;
        public const int MaxEvents = 5000;

        private readonly WatcherStore _store;

        /// <summary>
        /// Who an id is when the day files do not say (a player never online that day, a grid never written):
        /// asked of the running game; null when it does not know either. Unset in tests.
        /// </summary>
        public Func<long, string> LiveName;

        /// <summary>
        /// The character a player's identity has now (0 for none): tells its own body from bodies it left
        /// behind. Unset in tests.
        /// </summary>
        public Func<long, long> LiveBody;

        public WebData(WatcherStore store)
        {
            _store = store;
        }

        private IEnumerable<SQLiteConnection> Days(long from, long to)
        {
            for (var day = Clock.Day(from); day <= Clock.Day(to); day = day.AddDays(1))
            {
                var connection = _store.OpenRead(day);
                if (connection != null) yield return connection;
            }
        }

        private static SQLiteCommand Command(SQLiteConnection connection, string sql, params (string, object)[] parameters)
        {
            var command = new SQLiteCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return command;
        }

        private static string Id(SQLiteDataReader r, int column) => r.IsDBNull(column) ? null : r.GetInt64(column).ToString(CultureInfo.InvariantCulture);

        private static double? Num(SQLiteDataReader r, int column) => r.IsDBNull(column) ? (double?)null : r.GetDouble(column);

        /// <summary>The days that have records, newest first.</summary>
        public object Days() => DayFiles.Days(_store.Folder).Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();

        /// <summary>Players and grids whose name or id matches, in the time range.</summary>
        public object Search(string q, long from, long to)
        {
            var found = new Dictionary<long, Dictionary<string, object>>();
            long.TryParse(q, out var asId);
            foreach (var db in Days(from, to))
                using (db)
                using (var cmd = Command(db, "SELECT id, kind, name, owner, steam FROM names WHERE kind IN ('player','grid') " +
                                             "AND (id=@id OR steam=@id OR name LIKE @n) ORDER BY t DESC LIMIT 100",
                           ("@id", asId), ("@n", "%" + (q ?? "") + "%")))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        found[r.GetInt64(0)] = new Dictionary<string, object>
                        {
                            ["id"] = Id(r, 0), ["kind"] = r.GetString(1), ["name"] = r.IsDBNull(2) ? "" : r.GetString(2),
                            ["owner"] = Id(r, 3), ["steam"] = Id(r, 4),
                        };
            return found.Values.OrderBy(v => (string)v["kind"]).ThenBy(v => (string)v["name"]).Take(50).ToList();
        }

        /// <summary>Names of ids (players, grids, blocks), newest known.</summary>
        public Dictionary<string, string> Names(IEnumerable<long> ids, long from, long to)
        {
            var wanted = new HashSet<long>(ids.Where(i => i != 0));
            var names = new Dictionary<string, string>();
            if (wanted.Count == 0) return names;
            foreach (var db in Days(from, to))
                using (db)
                using (var cmd = Command(db, "SELECT id, name FROM names WHERE id IN (" + string.Join(",", wanted) + ")"))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        // an empty name says nothing: the running game may know better
                        var name = r.IsDBNull(1) ? "" : r.GetString(1);
                        if (name.Length > 0) names[Id(r, 0)] = name;
                    }
            if (LiveName != null)
                foreach (var id in wanted)
                {
                    var key = id.ToString(CultureInfo.InvariantCulture);
                    if (names.ContainsKey(key)) continue;
                    var name = LiveName(id);
                    if (!string.IsNullOrEmpty(name)) names[key] = name;
                }
            return names;
        }

        /// <summary>
        /// A player's or a grid's positions over the range. A player point: t, x, y, z, health, grid; a grid
        /// point: t, x, y, z, forward xyz, up xyz, blocks, owner, static, radius.
        /// </summary>
        public object Track(string kind, long id, long from, long to)
        {
            var points = new List<object[]>();
            var columns = kind == "grid"
                ? "SELECT t, x, y, z, fx, fy, fz, ux, uy, uz, blocks, owner, static, radius FROM grid_pos WHERE grid=@id AND "
                : "SELECT t, x, y, z, health, grid FROM player_pos WHERE identity=@id AND ";
            void Read(SQLiteCommand cmd, List<object[]> into)
            {
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var row = new object[r.FieldCount];
                        row[0] = r.GetInt64(0);
                        for (var c = 1; c < r.FieldCount; c++)
                        {
                            var name = r.GetName(c);
                            row[c] = name == "grid" || name == "owner" ? (object)Id(r, c) : Num(r, c);
                        }
                        into.Add(row);
                    }
            }
            foreach (var db in Days(from, to))
                using (db)
                using (var cmd = Command(db, columns + "t BETWEEN @a AND @b ORDER BY t", ("@id", id), ("@a", from), ("@b", to)))
                    Read(cmd, points);
            // positions are written on change: a grid standing still has its last one before the range (up to
            // ten minutes, or a day before); without it the object would have no place at the range's start
            if (points.Count == 0 || (long)points[0][0] > from)
            {
                var before = new List<object[]>();
                foreach (var db in Days(from - 86_400_000L, from))
                    using (db)
                    using (var cmd = Command(db, columns + "t < @a ORDER BY t DESC LIMIT 1", ("@id", id), ("@a", from)))
                        Read(cmd, before);
                if (before.Count > 0) points.Insert(0, before.OrderBy(p => (long)p[0]).Last());
            }
            if (points.Count == 0)
            {
                // nothing before either: where it first showed up after the range
                foreach (var db in Days(to, to + 86_400_000L))
                    using (db)
                    using (var cmd = Command(db, columns + "t > @b ORDER BY t LIMIT 1", ("@id", id), ("@b", to)))
                        Read(cmd, points);
                if (points.Count > 1) points.RemoveRange(1, points.Count - 1);
            }
            return new Dictionary<string, object>
            {
                ["id"] = id.ToString(CultureInfo.InvariantCulture),
                ["kind"] = kind,
                ["name"] = Names(new[] { id }, from, to).TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var n) ? n : null,
                ["total"] = points.Count,
                ["points"] = Thin(points, MaxTrackPoints),
            };
        }

        /// <summary>Every n-th point so that at most max remain; the first and the last always stay.</summary>
        public static List<T> Thin<T>(List<T> points, int max)
        {
            if (points.Count <= max || max < 2) return points;
            var thinned = new List<T>(max);
            var step = (points.Count - 1) / (double)(max - 1);
            for (var i = 0; i < max - 1; i++) thinned.Add(points[(int)Math.Round(i * step)]);
            thinned.Add(points[points.Count - 1]);
            return thinned;
        }

        /// <summary>
        /// The events of the range that a player or grid did or had done to it - for a grid also those of its
        /// blocks (a transfer is about a container) - with the names of those involved.
        /// </summary>
        public object Events(long id, long from, long to)
        {
            var events = new List<Dictionary<string, object>>();
            var ids = new HashSet<long>();
            foreach (var db in Days(from, to))
                using (db)
                using (var cmd = Command(db, "SELECT t, kind, actor, entity, x, y, z, amount, count, detail FROM events " +
                                             "WHERE (actor=@id OR entity=@id OR entity IN (SELECT id FROM names WHERE kind='block' AND owner=@id)) " +
                                             "AND t BETWEEN @a AND @b ORDER BY t LIMIT " + MaxEvents,
                           ("@id", id), ("@a", from), ("@b", to)))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        if (!r.IsDBNull(2)) ids.Add(r.GetInt64(2));
                        if (!r.IsDBNull(3)) ids.Add(r.GetInt64(3));
                        events.Add(new Dictionary<string, object>
                        {
                            ["t"] = r.GetInt64(0), ["kind"] = r.GetString(1), ["actor"] = Id(r, 2), ["entity"] = Id(r, 3),
                            ["x"] = Num(r, 4), ["y"] = Num(r, 5), ["z"] = Num(r, 6), ["amount"] = Num(r, 7),
                            ["count"] = r.IsDBNull(8) ? (long?)null : r.GetInt64(8), ["detail"] = r.IsDBNull(9) ? null : r.GetString(9),
                        });
                    }
            return new Dictionary<string, object> { ["events"] = events, ["names"] = Names(ids, from, to) };
        }

        /// <summary>The players and grids that were within the radius of the point in the range, and when.</summary>
        public object Near(double x, double y, double z, double radius, long from, long to)
        {
            var result = new Dictionary<string, object>();
            var ids = new HashSet<long>();
            foreach (var (table, column, kind) in new[] { ("player_pos", "identity", "players"), ("grid_pos", "grid", "grids") })
            {
                var seen = new Dictionary<long, (long First, long Last)>();
                foreach (var db in Days(from, to))
                    using (db)
                    using (var cmd = Command(db,
                               $"SELECT p.{column}, MIN(p.t), MAX(p.t) FROM {table} p JOIN {table}_space s ON s.id = p.id " +
                               "WHERE s.x1 >= @x0 AND s.x0 <= @x1 AND s.y1 >= @y0 AND s.y0 <= @y1 AND s.z1 >= @z0 AND s.z0 <= @z1 " +
                               $"AND p.t BETWEEN @a AND @b GROUP BY p.{column} LIMIT 500",
                               ("@x0", x - radius), ("@x1", x + radius), ("@y0", y - radius), ("@y1", y + radius),
                               ("@z0", z - radius), ("@z1", z + radius), ("@a", from), ("@b", to)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var id = r.GetInt64(0);
                            var span = (r.GetInt64(1), r.GetInt64(2));
                            seen[id] = seen.TryGetValue(id, out var was) ? (Math.Min(was.First, span.Item1), Math.Max(was.Last, span.Item2)) : span;
                        }
                ids.UnionWith(seen.Keys);
                result[kind] = seen.Select(p => new Dictionary<string, object>
                {
                    ["id"] = p.Key.ToString(CultureInfo.InvariantCulture), ["first"] = p.Value.First, ["last"] = p.Value.Last,
                }).ToList();
            }
            result["names"] = Names(ids, from, to);
            return result;
        }

        /// <summary>
        /// The inventories of a grid, a player (their character and what they carry) or one block at a moment:
        /// for each, the last content written at or before it and the one before that (for what changed).
        /// The day's file starts with every inventory written, so the day of the moment is enough.
        /// </summary>
        public object Inventory(string kind, long id, long at)
        {
            var condition = kind == "grid" ? "grid=@id" : kind == "player" ? "owner=@id AND grid IS NULL" : "entity=@id";
            var latest = new Dictionary<(long, long), List<(long T, string Items, double Volume, double Max, string Grid, string Owner)>>();
            using (var db = _store.OpenRead(Clock.Day(at)))
            {
                if (db != null)
                    using (var cmd = Command(db, "SELECT entity, inv, t, items, volume, max_volume, grid, owner FROM inventories " +
                                                 $"WHERE {condition} AND t <= @t ORDER BY entity, inv, t DESC", ("@id", id), ("@t", at)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var key = (r.GetInt64(0), r.GetInt64(1));
                            if (!latest.TryGetValue(key, out var list)) latest[key] = list = new List<(long, string, double, double, string, string)>();
                            if (list.Count < 2)
                                list.Add((r.GetInt64(2), r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? 0 : r.GetDouble(4),
                                    r.IsDBNull(5) ? 0 : r.GetDouble(5), Id(r, 6), Id(r, 7)));
                        }
            }
            // the owners too: a body that nothing names goes by its player's name
            var owners = latest.Values.Select(v => long.TryParse(v[0].Item6, out var o) ? o : 0L);
            var names = Names(latest.Keys.Select(k => k.Item1).Concat(owners), at - 86_400_000L, at);
            object Items(string text) => InventoryCodec.Decode(text).Select(s => new Dictionary<string, object>
            {
                ["type"] = s.Type, ["subtype"] = s.Subtype, ["amount"] = s.Amount,
            }).ToList();
            var inventories = latest.OrderBy(p => p.Key.Item1).ThenBy(p => p.Key.Item2).Select(p =>
            {
                var now = p.Value[0];
                var entity = p.Key.Item1.ToString(CultureInfo.InvariantCulture);
                // on no grid: a character's inventory - the player's body now, or one it left behind
                string body = null;
                var name = names.TryGetValue(entity, out var n) ? n : null;
                if (string.IsNullOrEmpty(now.Grid) || now.Grid == "0")
                {
                    long.TryParse(now.Owner, out var owner);
                    body = LiveBody != null && owner != 0 && LiveBody(owner) == p.Key.Item1 ? "current" : "old";
                    if (name == null && now.Owner != null) names.TryGetValue(now.Owner, out name);
                }
                return new Dictionary<string, object>
                {
                    ["entity"] = entity,
                    ["inv"] = p.Key.Item2,
                    ["name"] = name,
                    ["body"] = body,
                    ["grid"] = now.Grid,
                    ["owner"] = now.Owner,
                    ["at"] = now.T,
                    ["volume"] = now.Volume,
                    ["max"] = now.Max,
                    ["items"] = Items(now.Items),
                    ["prev"] = p.Value.Count > 1 ? new Dictionary<string, object> { ["at"] = p.Value[1].T, ["items"] = Items(p.Value[1].Items) } : null,
                };
            }).ToList();
            return new Dictionary<string, object> { ["at"] = at, ["inventories"] = inventories };
        }

        /// <summary>
        /// The players and grids that did anything in the range - positions written, events as actor or
        /// target - most active first, those whose name or id holds <paramref name="q"/> when it is given;
        /// for the search's drop-down list. At most <paramref name="max"/> of each, and how many there are.
        /// </summary>
        public object Objects(long from, long to, string q = null, int max = 300)
        {
            var players = new Dictionary<long, long[]>();     // id -> positions, events
            var grids = new Dictionary<long, long[]>();
            void Add(Dictionary<long, long[]> into, long id, int what, long count)
            {
                if (id == 0) return;
                if (!into.TryGetValue(id, out var c)) into[id] = c = new long[2];
                c[what] += count;
            }
            foreach (var db in Days(from, to))
                using (db)
                {
                    using (var cmd = Command(db, "SELECT identity, COUNT(*) FROM player_pos WHERE t BETWEEN @a AND @b GROUP BY identity", ("@a", from), ("@b", to)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) Add(players, r.GetInt64(0), 0, r.GetInt64(1));
                    using (var cmd = Command(db, "SELECT grid, COUNT(*) FROM grid_pos WHERE t BETWEEN @a AND @b GROUP BY grid", ("@a", from), ("@b", to)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) Add(grids, r.GetInt64(0), 0, r.GetInt64(1));
                    // events: the actor is a player; the target is a player, a grid or a block of a grid
                    using (var cmd = Command(db,
                               "SELECT e.actor, e.entity, n.kind, n.owner, COUNT(*) FROM events e LEFT JOIN names n ON n.id = e.entity " +
                               "WHERE e.t BETWEEN @a AND @b GROUP BY e.actor, e.entity", ("@a", from), ("@b", to)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var count = r.GetInt64(4);
                            if (!r.IsDBNull(0)) Add(players, r.GetInt64(0), 1, count);
                            if (r.IsDBNull(1)) continue;
                            var kind = r.IsDBNull(2) ? null : r.GetString(2);
                            if (kind == "player") Add(players, r.GetInt64(1), 1, count);
                            else if (kind == "grid") Add(grids, r.GetInt64(1), 1, count);
                            else if (kind == "block" && !r.IsDBNull(3)) Add(grids, r.GetInt64(3), 1, count);
                        }
                }
            var names = Names(players.Keys.Concat(grids.Keys), from - 86_400_000L, to);
            q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            var result = new Dictionary<string, object>();
            foreach (var (key, of) in new[] { ("players", players), ("grids", grids) })
            {
                var matching = of
                    .Select(p => (Id: p.Key.ToString(CultureInfo.InvariantCulture), Positions: p.Value[0], Events: p.Value[1]))
                    .Where(p => names.ContainsKey(p.Id))       // ids nobody knows (an NPC without a name, id 0) are left out
                    .Where(p => q == null || p.Id.Contains(q) || names[p.Id].IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                result[key] = matching.OrderByDescending(p => p.Events * 10 + p.Positions).Take(max)
                    .Select(p => new Dictionary<string, object>
                    {
                        ["id"] = p.Id, ["name"] = names[p.Id], ["positions"] = p.Positions, ["events"] = p.Events,
                    }).ToList();
                result[key + "Total"] = matching.Count;
            }
            return result;
        }

        /// <summary>
        /// How much happened over the range, in <paramref name="buckets"/> equal slices: events by kind and
        /// positions written (players, grids), for the activity bars of the time line. Empty slices are left
        /// out.
        /// </summary>
        public object Activity(long from, long to, int buckets)
        {
            buckets = Math.Max(1, Math.Min(2000, buckets));
            var width = Math.Max(1L, (long)Math.Ceiling((to - from) / (double)buckets));
            var slices = new SortedDictionary<long, Dictionary<string, long>>();
            void Add(long bucket, string what, long count)
            {
                if (!slices.TryGetValue(bucket, out var s)) slices[bucket] = s = new Dictionary<string, long>();
                s[what] = (s.TryGetValue(what, out var c) ? c : 0) + count;
            }
            foreach (var db in Days(from, to))
                using (db)
                {
                    using (var cmd = Command(db, "SELECT (t - @a) / @w, kind, COUNT(*) FROM events WHERE t BETWEEN @a AND @b GROUP BY 1, 2",
                               ("@a", from), ("@b", to), ("@w", width)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) Add(r.GetInt64(0), r.GetString(1), r.GetInt64(2));
                    foreach (var (table, what) in new[] { ("player_pos", "@players"), ("grid_pos", "@grids") })
                        using (var cmd = Command(db, $"SELECT (t - @a) / @w, COUNT(*) FROM {table} WHERE t BETWEEN @a AND @b GROUP BY 1",
                                   ("@a", from), ("@b", to), ("@w", width)))
                        using (var r = cmd.ExecuteReader())
                            while (r.Read()) Add(r.GetInt64(0), what, r.GetInt64(1));
                }
            return new Dictionary<string, object>
            {
                ["from"] = from,
                ["width"] = width,
                ["buckets"] = slices.Select(s => new Dictionary<string, object> { ["b"] = s.Key, ["counts"] = s.Value }).ToList(),
            };
        }

        /// <summary>The edge of the cubes events are counted in, to find where most happened.</summary>
        public const double HotCellM = 2000;

        /// <summary>
        /// Where most happened in the range: space is cut into <see cref="HotCellM"/> cubes, the events with a
        /// place are counted in each, and the busiest one is given - the middle of its events, how many there
        /// were and when, on average. Null when no event of the range has a place.
        /// </summary>
        public object Hotspot(long from, long to)
        {
            var cells = new Dictionary<(long, long, long), (long Count, double X, double Y, double Z, double T)>();
            foreach (var db in Days(from, to))
                using (db)
                using (var cmd = Command(db,
                           "SELECT CAST(x / @c AS INTEGER), CAST(y / @c AS INTEGER), CAST(z / @c AS INTEGER), COUNT(*), SUM(x), SUM(y), SUM(z), SUM(t) " +
                           "FROM events WHERE t BETWEEN @a AND @b AND x IS NOT NULL AND NOT (x = 0 AND y = 0 AND z = 0) GROUP BY 1, 2, 3",
                           ("@c", HotCellM), ("@a", from), ("@b", to)))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var key = (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
                        var was = cells.TryGetValue(key, out var c) ? c : default;
                        cells[key] = (was.Count + r.GetInt64(3), was.X + r.GetDouble(4), was.Y + r.GetDouble(5), was.Z + r.GetDouble(6),
                            was.T + r.GetDouble(7));
                    }
            if (cells.Count == 0) return new Dictionary<string, object> { ["found"] = false };
            var best = cells.Values.OrderByDescending(c => c.Count).First();
            return new Dictionary<string, object>
            {
                ["found"] = true, ["x"] = best.X / best.Count, ["y"] = best.Y / best.Count, ["z"] = best.Z / best.Count,
                ["count"] = best.Count, ["t"] = (long)(best.T / best.Count), ["cell"] = HotCellM, ["cells"] = cells.Count,
            };
        }

        /// <summary>How far from a moment a grid's position is looked for: a standing grid is written every 10 minutes.</summary>
        public const long GridWindowMs = 11 * 60_000L;

        /// <summary>
        /// Who was where at a moment: every player and grid with a position written near it (the one nearest in
        /// time; a standing grid is written every 10 minutes, so a grid is looked for <see cref="GridWindowMs"/>
        /// around it), and how many events each had within <paramref name="window"/> of it. Most active first.
        /// </summary>
        public object Moment(long at, long window, int max = 2000, long? eventsFrom = null, long? eventsTo = null)
        {
            window = Math.Max(10_000, Math.Min(3_600_000, window));
            // the events and jumps: of the given range (the one chosen on the timeline), else of the window
            var ea = eventsFrom ?? at - window;
            var eb = eventsTo ?? at + window;
            if (eb < ea) (ea, eb) = (eb, ea);
            var players = new Dictionary<long, (long T, double X, double Y, double Z, long Grid)>();
            var grids = new Dictionary<long, (long T, double X, double Y, double Z, long Blocks, long Owner, double Radius, bool Static)>();
            var events = new Dictionary<long, long>();
            bool Nearer(long t, long than) => Math.Abs(t - at) < Math.Abs(than - at);
            void AddEvents(long id, long count)
            {
                if (id != 0) events[id] = (events.TryGetValue(id, out var c) ? c : 0) + count;
            }
            var reach = Math.Max(window, GridWindowMs);
            foreach (var db in Days(at - reach, at + reach))
                using (db)
                {
                    using (var cmd = Command(db, "SELECT identity, t, x, y, z, grid FROM player_pos WHERE t BETWEEN @a AND @b",
                               ("@a", at - window), ("@b", at + window)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var id = r.GetInt64(0);
                            var t = r.GetInt64(1);
                            if (!players.TryGetValue(id, out var was) || Nearer(t, was.T))
                                players[id] = (t, r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.IsDBNull(5) ? 0 : r.GetInt64(5));
                        }
                    using (var cmd = Command(db, "SELECT grid, t, x, y, z, blocks, owner, radius, static FROM grid_pos WHERE t BETWEEN @a AND @b",
                               ("@a", at - GridWindowMs), ("@b", at + GridWindowMs)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var id = r.GetInt64(0);
                            var t = r.GetInt64(1);
                            if (!grids.TryGetValue(id, out var was) || Nearer(t, was.T))
                                grids[id] = (t, r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.IsDBNull(5) ? 0 : r.GetInt64(5),
                                    r.IsDBNull(6) ? 0 : r.GetInt64(6), r.IsDBNull(7) ? 0 : r.GetDouble(7), !r.IsDBNull(8) && r.GetInt64(8) == 1);
                        }
                }
            foreach (var db in Days(ea, eb))
                using (db)
                using (var cmd = Command(db,
                           "SELECT e.actor, e.entity, n.kind, n.owner, COUNT(*) FROM events e LEFT JOIN names n ON n.id = e.entity " +
                           "WHERE e.t BETWEEN @a AND @b GROUP BY e.actor, e.entity", ("@a", ea), ("@b", eb)))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var count = r.GetInt64(4);
                        if (!r.IsDBNull(0)) AddEvents(r.GetInt64(0), count);
                        if (r.IsDBNull(1)) continue;
                        var kind = r.IsDBNull(2) ? null : r.GetString(2);
                        AddEvents(kind == "block" && !r.IsDBNull(3) ? r.GetInt64(3) : r.GetInt64(1), count);
                    }
            var jumps = new List<Dictionary<string, object>>();
            foreach (var db in Days(ea, eb))
                using (db)
                using (var cmd = Command(db, "SELECT t, actor, entity, x, y, z, amount, detail FROM events WHERE kind='jump' AND t BETWEEN @a AND @b ORDER BY t LIMIT 500",
                           ("@a", ea), ("@b", eb)))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        jumps.Add(new Dictionary<string, object>
                        {
                            ["t"] = r.GetInt64(0), ["actor"] = Id(r, 1), ["entity"] = Id(r, 2),
                            ["x"] = Num(r, 3), ["y"] = Num(r, 4), ["z"] = Num(r, 5), ["amount"] = Num(r, 6),
                            ["detail"] = r.IsDBNull(7) ? null : r.GetString(7),
                        });
            var names = Names(players.Keys.Concat(grids.Keys), at - 86_400_000L, at + window);
            string Key(long id) => id.ToString(CultureInfo.InvariantCulture);
            long Events(long id) => events.TryGetValue(id, out var c) ? c : 0;
            string Name(long id) => names.TryGetValue(Key(id), out var n) ? n : null;
            return new Dictionary<string, object>
            {
                ["at"] = at,
                ["window"] = window,
                ["eventsFrom"] = ea,
                ["eventsTo"] = eb,
                ["players"] = players.OrderByDescending(p => Events(p.Key)).Select(p => new Dictionary<string, object>
                {
                    ["id"] = Key(p.Key), ["name"] = Name(p.Key), ["t"] = p.Value.T, ["x"] = p.Value.X, ["y"] = p.Value.Y, ["z"] = p.Value.Z,
                    ["grid"] = p.Value.Grid == 0 ? null : Key(p.Value.Grid), ["events"] = Events(p.Key),
                }).ToList(),
                ["grids"] = grids.OrderByDescending(p => Events(p.Key)).ThenByDescending(p => p.Value.Blocks).Take(max)
                    .Select(p => new Dictionary<string, object>
                    {
                        ["id"] = Key(p.Key), ["name"] = Name(p.Key), ["t"] = p.Value.T, ["x"] = p.Value.X, ["y"] = p.Value.Y, ["z"] = p.Value.Z,
                        ["blocks"] = p.Value.Blocks, ["owner"] = Key(p.Value.Owner), ["radius"] = p.Value.Radius, ["static"] = p.Value.Static,
                        ["events"] = Events(p.Key),
                    }).ToList(),
                ["gridsTotal"] = grids.Count,
                ["jumps"] = jumps,
            };
        }

        /// <summary>
        /// The planets and the direction to the sun as the day file of the moment has them (or the nearest
        /// earlier day file that has them).
        /// </summary>
        public object World(long at)
        {
            var planets = new List<Dictionary<string, object>>();
            double[] sun = null;
            var days = DayFiles.Days(_store.Folder).Where(d => d <= Clock.Day(at)).ToList();
            if (days.Count == 0) days = DayFiles.Days(_store.Folder);
            foreach (var day in days)
            {
                using (var db = _store.OpenRead(day))
                {
                    if (db == null) continue;
                    // day files written before the planets were recorded have no such table
                    using (var has = Command(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='planets'"))
                        if ((long)has.ExecuteScalar() == 0) continue;
                    using (var cmd = Command(db, "SELECT id, name, generator, x, y, z, radius, min_radius, max_radius, atmosphere, gravity FROM planets"))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            planets.Add(new Dictionary<string, object>
                            {
                                ["id"] = Id(r, 0), ["name"] = r.IsDBNull(1) ? null : r.GetString(1), ["generator"] = r.IsDBNull(2) ? null : r.GetString(2),
                                ["x"] = r.GetDouble(3), ["y"] = r.GetDouble(4), ["z"] = r.GetDouble(5),
                                ["radius"] = Num(r, 6), ["min"] = Num(r, 7), ["max"] = Num(r, 8), ["atmosphere"] = Num(r, 9), ["gravity"] = Num(r, 10),
                            });
                    using (var cmd = Command(db, "SELECT value FROM meta WHERE key='sun'"))
                        if (cmd.ExecuteScalar() is string text)
                            sun = text.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                }
                if (planets.Count > 0) break;
            }
            return new Dictionary<string, object> { ["planets"] = planets, ["sun"] = sun };
        }

        /// <summary>The alerts of the range.</summary>
        public object Alerts(long from, long to)
        {
            var alerts = new List<Dictionary<string, object>>();
            foreach (var db in Days(from, to))
                using (db)
                using (var cmd = Command(db, "SELECT t, kind, actor, entity, detail FROM alerts WHERE t BETWEEN @a AND @b ORDER BY t LIMIT 1000",
                           ("@a", from), ("@b", to)))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        alerts.Add(new Dictionary<string, object>
                        {
                            ["t"] = r.GetInt64(0), ["kind"] = r.GetString(1), ["actor"] = Id(r, 2), ["entity"] = Id(r, 3),
                            ["detail"] = r.IsDBNull(4) ? null : r.GetString(4),
                        });
            return alerts;
        }
    }
}

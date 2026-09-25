using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SentisWatcher.Storage
{
    /// <summary>
    /// Reads the day files for the !watch commands (a thread of its own, never the game's): a player's
    /// timeline, a grid's history, who was near a point. Each answer covers the day files the time range
    /// touches.
    /// </summary>
    public sealed class WatcherQuery
    {
        private readonly WatcherStore _store;

        public WatcherQuery(WatcherStore store)
        {
            _store = store;
        }

        /// <summary>A time on the server's clock (the zone is said once, in the header of an answer).</summary>
        public static string Time(long ms) => Clock.Local(ms).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

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

        /// <summary>Ids and names matching a name (or an id), newest first: players or grids.</summary>
        public List<(long Id, string Name)> Find(string kind, string nameOrId, long from, long to)
        {
            var found = new Dictionary<long, string>();
            long.TryParse(nameOrId, out var asId);
            foreach (var db in Days(from, to))
                using (db)
                using (var cmd = Command(db, "SELECT id, name FROM names WHERE kind=@k AND (id=@id OR steam=@id OR name LIKE @n) ORDER BY t DESC LIMIT 20",
                           ("@k", kind), ("@id", asId), ("@n", "%" + nameOrId + "%")))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        found[r.GetInt64(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
            return found.Select(p => (p.Key, p.Value)).ToList();
        }

        /// <summary>A player's time range: where they were and what they did or had done to them.</summary>
        public string Player(long identity, string name, long from, long to, int maxEvents)
        {
            var text = new StringBuilder();
            text.AppendLine($"{name} ({identity}), {Time(from)} .. {Time(to)} {Clock.Zone(to)}");
            int positions = 0;
            string first = null, last = null;
            var events = new List<(long T, string Line)>();
            var counts = new Dictionary<string, int>();
            foreach (var db in Days(from, to))
                using (db)
                {
                    using (var cmd = Command(db, "SELECT t, x, y, z, controlled, grid FROM player_pos WHERE identity=@i AND t BETWEEN @a AND @b ORDER BY t",
                               ("@i", identity), ("@a", from), ("@b", to)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var line = $"{Time(r.GetInt64(0))} GPS:{name}:{r.GetDouble(1):0}:{r.GetDouble(2):0}:{r.GetDouble(3):0}:" +
                                       (r.GetInt64(5) != 0 ? " on grid " + r.GetInt64(5) : "");
                            first = first ?? line;
                            last = line;
                            positions++;
                        }
                    using (var cmd = Command(db, "SELECT t, kind, actor, entity, amount, count, detail FROM events " +
                                                 "WHERE (actor=@i OR entity=@i) AND t BETWEEN @a AND @b ORDER BY t", ("@i", identity), ("@a", from), ("@b", to)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var kind = r.GetString(1);
                            counts[kind] = counts.TryGetValue(kind, out var c) ? c + 1 : 1;
                            events.Add((r.GetInt64(0), FormatEvent(r)));
                        }
                    AppendAlerts(db, identity, from, to, events);
                }
            text.AppendLine($"positions: {positions}" + (first != null ? $"; first {first}; last {last}" : ""));
            text.AppendLine("events: " + (counts.Count == 0 ? "none" : string.Join(", ", counts.OrderByDescending(c => c.Value).Select(c => c.Key + " " + c.Value))));
            foreach (var e in events.OrderBy(e => e.T).Skip(Math.Max(0, events.Count - maxEvents))) text.AppendLine(e.Line);
            return text.ToString();
        }

        /// <summary>A grid's time range: its movement, owners, what was done to it and by whom.</summary>
        public string Grid(long grid, string name, long from, long to, int maxEvents)
        {
            var text = new StringBuilder();
            text.AppendLine($"{name} ({grid}), {Time(from)} .. {Time(to)} {Clock.Zone(to)}");
            int positions = 0;
            string first = null, last = null;
            var events = new List<(long T, string Line)>();
            var byActor = new Dictionary<(string, long), double>();
            foreach (var db in Days(from, to))
                using (db)
                {
                    using (var cmd = Command(db, "SELECT t, x, y, z, blocks, owner, static FROM grid_pos WHERE grid=@g AND t BETWEEN @a AND @b ORDER BY t",
                               ("@g", grid), ("@a", from), ("@b", to)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var line = $"{Time(r.GetInt64(0))} GPS:{name}:{r.GetDouble(1):0}:{r.GetDouble(2):0}:{r.GetDouble(3):0}: " +
                                       $"{r.GetInt64(4)} blocks, owner {r.GetInt64(5)}{(r.GetInt64(6) == 1 ? ", static" : "")}";
                            first = first ?? line;
                            last = line;
                            positions++;
                        }
                    using (var cmd = Command(db, "SELECT t, kind, actor, entity, amount, count, detail FROM events " +
                                                 "WHERE entity=@g AND t BETWEEN @a AND @b ORDER BY t", ("@g", grid), ("@a", from), ("@b", to)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var kind = r.GetString(1);
                            var actor = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                            var amount = r.IsDBNull(4) ? 0 : r.GetDouble(4);
                            if (kind == "damage" || kind == "grind" || kind == "destroyed" || kind == "weld" || kind == "drill")
                                byActor[(kind, actor)] = (byActor.TryGetValue((kind, actor), out var s) ? s : 0) + amount;
                            events.Add((r.GetInt64(0), FormatEvent(r)));
                        }
                }
            text.AppendLine($"positions: {positions}" + (first != null ? $"; first {first}; last {last}" : ""));
            if (byActor.Count > 0)
                text.AppendLine("by player: " + string.Join(", ", byActor.OrderByDescending(p => p.Value).Take(10)
                    .Select(p => $"{p.Key.Item1} {p.Key.Item2}: {p.Value:0}")));
            foreach (var e in events.OrderBy(e => e.T).Skip(Math.Max(0, events.Count - maxEvents))) text.AppendLine(e.Line);
            return text.ToString();
        }

        /// <summary>Players and grids that were within the radius of the point in the time range.</summary>
        public string Near(double x, double y, double z, double radius, long from, long to)
        {
            var players = new Dictionary<long, (long First, long Last)>();
            var grids = new Dictionary<long, (long First, long Last)>();
            var names = new Dictionary<long, string>();
            foreach (var db in Days(from, to))
                using (db)
                {
                    void Collect(string table, string idColumn, Dictionary<long, (long, long)> into)
                    {
                        using (var cmd = Command(db,
                                   $"SELECT p.{idColumn}, MIN(p.t), MAX(p.t) FROM {table} p JOIN {table}_space s ON s.id = p.id " +
                                   "WHERE s.x1 >= @x0 AND s.x0 <= @x1 AND s.y1 >= @y0 AND s.y0 <= @y1 AND s.z1 >= @z0 AND s.z0 <= @z1 " +
                                   $"AND p.t BETWEEN @a AND @b GROUP BY p.{idColumn}",
                                   ("@x0", x - radius), ("@x1", x + radius), ("@y0", y - radius), ("@y1", y + radius),
                                   ("@z0", z - radius), ("@z1", z + radius), ("@a", from), ("@b", to)))
                        using (var r = cmd.ExecuteReader())
                            while (r.Read())
                            {
                                var id = r.GetInt64(0);
                                var (f, l) = (r.GetInt64(1), r.GetInt64(2));
                                into[id] = into.TryGetValue(id, out var was) ? (Math.Min(was.Item1, f), Math.Max(was.Item2, l)) : (f, l);
                            }
                    }
                    Collect("player_pos", "identity", players);
                    Collect("grid_pos", "grid", grids);
                    using (var cmd = Command(db, "SELECT id, name FROM names"))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            names[r.GetInt64(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
                }
            string Name(long id) => names.TryGetValue(id, out var n) ? n : id.ToString();
            var text = new StringBuilder();
            text.AppendLine($"within {radius:0} m of {x:0}:{y:0}:{z:0}, {Time(from)} .. {Time(to)} {Clock.Zone(to)}");
            text.AppendLine("players: " + (players.Count == 0 ? "none" : string.Join(", ",
                players.OrderBy(p => p.Value.First).Select(p => $"{Name(p.Key)} {Time(p.Value.First)}..{Time(p.Value.Last)}"))));
            text.AppendLine("grids: " + (grids.Count == 0 ? "none" : string.Join(", ",
                grids.OrderBy(p => p.Value.First).Take(30).Select(p => $"{Name(p.Key)} ({p.Key})"))) +
                (grids.Count > 30 ? $" and {grids.Count - 30} more" : ""));
            return text.ToString();
        }

        /// <summary>What an entity's inventories held over the time range: one line each time the content changed.</summary>
        public string Inventory(long entity, long from, long to, int max)
        {
            var lines = new List<(long, string)>();
            foreach (var db in Days(from, to))
                using (db)
                using (var cmd = Command(db, "SELECT t, inv, owner, items, volume, max_volume FROM inventories " +
                                             "WHERE (entity=@e OR grid=@e) AND t BETWEEN @a AND @b ORDER BY t", ("@e", entity), ("@a", from), ("@b", to)))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        lines.Add((r.GetInt64(0), $"{Time(r.GetInt64(0))} inv {r.GetInt64(1)} owner {r.GetInt64(2)} " +
                                                  $"volume {r.GetDouble(4):0.###}/{r.GetDouble(5):0.###}: {(r.IsDBNull(3) ? "" : r.GetString(3))}"));
            var text = new StringBuilder();
            text.AppendLine($"inventories of {entity}, {Time(from)} .. {Time(to)} {Clock.Zone(to)}: {lines.Count} changes");
            foreach (var l in lines.Skip(Math.Max(0, lines.Count - max))) text.AppendLine(l.Item2);
            return text.ToString();
        }

        /// <summary>The alerts of the time range (all, or about one actor).</summary>
        /// <summary>Who loaded the game thread.</summary>
        public string Load(long from, long to, int top) => LoadReport.Read(Days(from, to), from, to).Describe(top);

        public string Alerts(long from, long to, int max)
        {
            var lines = new List<(long, string)>();
            foreach (var db in Days(from, to))
                using (db)
                    AppendAlerts(db, null, from, to, lines);
            var text = new StringBuilder();
            text.AppendLine($"alerts {Time(from)} .. {Time(to)} {Clock.Zone(to)}: {lines.Count}");
            foreach (var l in lines.OrderBy(l => l.Item1).Skip(Math.Max(0, lines.Count - max))) text.AppendLine(l.Item2);
            return text.ToString();
        }

        private static void AppendAlerts(SQLiteConnection db, long? actor, long from, long to, List<(long, string)> into)
        {
            var sql = "SELECT t, kind, actor, entity, detail FROM alerts WHERE t BETWEEN @a AND @b" + (actor.HasValue ? " AND actor=@i" : "") + " ORDER BY t";
            using (var cmd = Command(db, sql, ("@a", from), ("@b", to), ("@i", actor ?? 0)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    into.Add((r.GetInt64(0), $"{Time(r.GetInt64(0))} ALERT {r.GetString(1)}: {(r.IsDBNull(4) ? "" : r.GetString(4))}"));
        }

        private static string FormatEvent(SQLiteDataReader r)
        {
            var text = new StringBuilder();
            text.Append(Time(r.GetInt64(0))).Append(' ').Append(r.GetString(1));
            if (!r.IsDBNull(2) && r.GetInt64(2) != 0) text.Append(" actor ").Append(r.GetInt64(2));
            if (!r.IsDBNull(3) && r.GetInt64(3) != 0) text.Append(" on ").Append(r.GetInt64(3));
            if (!r.IsDBNull(4)) text.Append(" amount ").Append(r.GetDouble(4).ToString("0.##", CultureInfo.InvariantCulture));
            if (!r.IsDBNull(5)) text.Append(" x").Append(r.GetInt64(5));
            if (!r.IsDBNull(6)) text.Append(" | ").Append(r.GetString(6));
            return text.ToString();
        }
    }
}

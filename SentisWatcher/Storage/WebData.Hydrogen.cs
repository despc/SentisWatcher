using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace SentisWatcher.Storage
{
    public sealed partial class WebData
    {
        public object Hydrogen(string ids, long from, long to)
        {
            var wanted = (ids ?? "").Split(',').Select(s => long.TryParse(s, out var id) ? id : 0).Where(id => id != 0).Distinct().ToArray();
            if (wanted.Length > 128) throw new ArgumentException("At most 128 grids per request");
            if (to < from || (double)to - from > 7 * 86400000d) throw new ArgumentException("Invalid time range (maximum 7 days)");
            var series = new Dictionary<string, List<Dictionary<string, object>>>();
            var counts = new Dictionary<string, long>();
            var latest = new Dictionary<string, object>();
            var width = Math.Max(10000L, (long)Math.Ceiling(((double)to - from + 1) / 170));
            const string columns = "t,grid,name,stored,capacity,buffer,produced,consumed,tank_in,tank_out,frozen";
            void Point(System.Data.SQLite.SQLiteDataReader r)
            {
                var id = Id(r, 1);
                if (!series.TryGetValue(id, out var points)) series[id] = points = new List<Dictionary<string, object>>();
                points.Add(new Dictionary<string, object>
                {
                    ["t"] = r.GetInt64(0), ["name"] = r.GetString(2), ["stored"] = r.GetDouble(3),
                    ["capacity"] = r.GetDouble(4), ["buffer"] = r.GetDouble(5), ["produced"] = r.GetDouble(6),
                    ["consumed"] = r.GetDouble(7), ["tankIn"] = r.GetDouble(8), ["tankOut"] = r.GetDouble(9),
                    ["frozen"] = r.GetInt64(10) != 0,
                });
            }
            var first = true;
            foreach (var db in Days(from, to))
                using (db)
                {
                    using (var has = Command(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='hydrogen'"))
                        if (Convert.ToInt64(has.ExecuteScalar()) == 0) continue;
                    if (wanted.Length == 0) continue;
                    // rows are written on a change and a heartbeat: the one before the range still held at its start
                    if (first)
                        foreach (var grid in wanted)
                            using (var cmd = Command(db, "SELECT " + columns + " FROM hydrogen WHERE grid=@id AND t<@a ORDER BY t DESC,id DESC LIMIT 1", ("@id", grid), ("@a", from)))
                            using (var r = cmd.ExecuteReader())
                                if (r.Read()) Point(r);
                    first = false;
                    var filter = " FROM hydrogen WHERE grid IN (" + string.Join(",", wanted) + ") AND t BETWEEN @a AND @b";
                    using (var cmd = Command(db, "SELECT grid,COUNT(*)" + filter + " GROUP BY grid", ("@a", from), ("@b", to)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) { var id = Id(r, 0); counts[id] = (counts.TryGetValue(id, out var count) ? count : 0) + r.GetInt64(1); }
                    // SQLite's single MIN/MAX aggregate selects the accompanying id from that extreme row.
                    // Keep stock extrema and flow peaks, not uniform thinning that hides brief fuel losses.
                    var picks = new[] { "MIN(id)", "MAX(id)", "MIN(stored+buffer)", "MAX(stored+buffer)", "MAX(produced)", "MAX(consumed)", "MAX(tank_in)", "MAX(tank_out)" };
                    var selected = string.Join(" UNION ", picks.Select(p => "SELECT id" + filter + " GROUP BY grid,((t-@a)/@w) HAVING " + p + " IS NOT NULL"));
                    using (var cmd = Command(db, "SELECT " + columns + " FROM hydrogen WHERE id IN (" + selected + ") ORDER BY t,id", ("@a", from), ("@b", to), ("@w", width)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) Point(r);
                    // the per-block list goes with some rows only ("" otherwise): the last one written by the range's end
                    foreach (var grid in wanted)
                        using (var cmd = Command(db, "SELECT blocks FROM hydrogen WHERE grid=@id AND t<=@b AND blocks<>'' ORDER BY t DESC,id DESC LIMIT 1", ("@id", grid), ("@b", to)))
                            if (cmd.ExecuteScalar() is string json) latest[grid.ToString()] = JsonConvert.DeserializeObject(json);
                }
            return new
            {
                from, to, intervalMs = 10000,
                heartbeatMs = Recording.HydrogenSampler.HeartbeatMs, frozenHeartbeatMs = Recording.HydrogenSampler.FrozenHeartbeatMs,
                series = series.Select(pair => new
                {
                    id = pair.Key, name = pair.Value.Last()["name"], total = counts[pair.Key],
                    latest = latest.TryGetValue(pair.Key, out var rows) ? rows : null,
                    points = pair.Value,
                }).ToList(),
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SentisWatcher.Ledger;

namespace SentisWatcher.Storage
{
    public sealed partial class WebData
    {
        public const int MaxAnomalies = 2000;

        /// <summary>
        /// The inventories of a player (everything they own and carry), a grid or a block over the range: the
        /// amount of every item after each change, where the changes came from, the changes one by one and the
        /// alerts about them. Amounts go out in units, not millionths.
        /// </summary>
        public object Ledger(string kind, long id, long from, long to)
        {
            kind = kind == "grid" || kind == "entity" ? kind : "player";
            var condition = kind == "grid" ? "grid=@id" : kind == "entity" ? "entity=@id" : "owner=@id";
            Func<InventoryRow, bool> member = kind == "grid" ? (Func<InventoryRow, bool>)(r => r.Grid == id)
                : kind == "entity" ? r => r.Entity == id : r => r.Owner == id;
            // the day before too: what the inventories held when the range starts
            var first = Clock.ToMs(Clock.Day(from).AddDays(-1));

            var entities = new HashSet<long>();
            foreach (var db in Days(first, to))
                using (db)
                using (var cmd = Command(db, $"SELECT DISTINCT entity FROM inventories WHERE {condition} AND t <= @b", ("@id", id), ("@b", to)))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        entities.Add(r.GetInt64(0));

            var rows = new List<InventoryRow>();
            if (entities.Count > 0)
                foreach (var db in Days(first, to))
                    using (db)
                    {
                        var flows = WatcherStore.HasColumn(db, "inventories", "flows") ? "flows" : "NULL";
                        using (var cmd = Command(db, $"SELECT t, entity, inv, grid, owner, items, {flows} FROM inventories " +
                                                     $"WHERE entity IN ({string.Join(",", entities)}) AND t <= @b ORDER BY t, id", ("@b", to)))
                        using (var r = cmd.ExecuteReader())
                            while (r.Read())
                                rows.Add(new InventoryRow
                                {
                                    T = r.GetInt64(0), Entity = r.GetInt64(1), Inv = (int)r.GetInt64(2),
                                    Grid = r.IsDBNull(3) ? 0 : r.GetInt64(3), Owner = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                                    Items = r.IsDBNull(5) ? null : r.GetString(5), Flows = r.IsDBNull(6) ? null : r.GetString(6),
                                });
                    }
            var result = LedgerReplay.Run(rows, from, to, member);

            double U(long raw) => raw / 1_000_000.0;
            var sources = new Dictionary<string, Dictionary<string, double>>();
            foreach (var pair in result.Sources.Amounts)
            {
                if (!sources.TryGetValue(pair.Key.Item, out var byKind)) sources[pair.Key.Item] = byKind = new Dictionary<string, double>();
                byKind[pair.Key.Kind] = U(pair.Value);
            }
            var unexplained = new Dictionary<string, double>();
            foreach (var item in result.Start.Keys.Union(result.End.Keys).Union(sources.Keys))
            {
                result.Start.TryGetValue(item, out var a);
                result.End.TryGetValue(item, out var b);
                var explained = result.Sources.Amounts.Where(p => p.Key.Item == item).Sum(p => p.Value);
                var gap = b - a - explained;
                if (Math.Abs(gap) > LedgerMath.Tolerance(b - a)) unexplained[item] = U(gap);
            }

            var alerts = new List<Dictionary<string, object>>();
            foreach (var db in Days(from, to))
                using (db)
                using (var cmd = Command(db, "SELECT t, kind, actor, entity, detail FROM alerts WHERE t BETWEEN @a AND @b ORDER BY t", ("@a", from), ("@b", to)))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var actor = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                        var entity = r.IsDBNull(3) ? 0 : r.GetInt64(3);
                        var about = kind == "player" ? actor == id || result.Entities.Contains(entity) : entity == id || result.Entities.Contains(entity);
                        if (!about) continue;
                        alerts.Add(new Dictionary<string, object>
                        {
                            ["t"] = r.GetInt64(0), ["kind"] = r.GetString(1), ["actor"] = Id(r, 2), ["entity"] = Id(r, 3),
                            ["detail"] = r.IsDBNull(4) ? null : r.GetString(4),
                        });
                    }

            // names: the subject, the inventories, the grids items went to and came from
            var ids = new HashSet<long>(result.Entities) { id };
            foreach (var pair in result.Sources.Amounts.Keys)
                if (pair.Kind.StartsWith("transfer@") && long.TryParse(pair.Kind.Substring(9), out var peer)) ids.Add(peer);
            foreach (var row in rows) if (row.Grid != 0) ids.Add(row.Grid);
            var names = Names(ids, first, to);

            return new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["id"] = id.ToString(CultureInfo.InvariantCulture),
                ["name"] = names.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var name) ? name : null,
                ["from"] = from,
                ["to"] = to,
                ["times"] = result.Times,
                ["series"] = result.Series.ToDictionary(p => p.Key, p => p.Value.Select(U).ToList()),
                ["start"] = result.Start.ToDictionary(p => p.Key, p => U(p.Value)),
                ["end"] = result.End.ToDictionary(p => p.Key, p => U(p.Value)),
                ["sources"] = sources,
                ["unexplained"] = unexplained,
                ["changes"] = Enumerable.Reverse(result.Changes).Select(c => new Dictionary<string, object>
                {
                    ["t"] = c.T,
                    ["entity"] = c.Entity.ToString(CultureInfo.InvariantCulture),
                    ["inv"] = c.Inv,
                    ["delta"] = c.Delta.ToDictionary(p => p.Key, p => U(p.Value)),
                    ["flows"] = c.Flows.Amounts.Select(p => new object[] { p.Key.Kind, p.Key.Item, U(p.Value) }).ToList(),
                }).ToList(),
                ["inventories"] = result.Entities.Count,
                ["grids"] = rows.Where(r => r.Grid != 0).GroupBy(r => r.Entity).ToDictionary(
                    g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Last().Grid.ToString(CultureInfo.InvariantCulture)),
                ["alerts"] = alerts,
                ["names"] = names,
            };
        }

        /// <summary>
        /// What each inventory of a player, a grid or a block held at a moment: its last record at or before it,
        /// the record before that (to show what changed) and where that change came from. An inventory counts
        /// when its last record makes it the subject's; a gone one (no items) is left out.
        /// </summary>
        public object Holdings(string kind, long id, long at)
        {
            kind = kind == "grid" || kind == "entity" ? kind : "player";
            var condition = kind == "grid" ? "grid=@id" : kind == "entity" ? "entity=@id" : "owner=@id";
            var today = Clock.ToMs(Clock.Day(at));
            var yesterday = today - 86_400_000L;

            var entities = new HashSet<long>();
            foreach (var db in Days(yesterday, at))
                using (db)
                using (var cmd = Command(db, $"SELECT DISTINCT entity FROM inventories WHERE {condition} AND t <= @at", ("@id", id), ("@at", at)))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        entities.Add(r.GetInt64(0));

            // the last two records of each inventory: today's file first, yesterday's for what today lacks
            var rows = new Dictionary<(long Entity, long Inv), List<HeldRow>>();
            void Read(DateTime day, IEnumerable<long> which)
            {
                var list = which.ToList();
                if (list.Count == 0) return;
                using (var db = _store.OpenRead(day))
                {
                    if (db == null) return;
                    var flows = WatcherStore.HasColumn(db, "inventories", "flows") ? "i.flows" : "NULL";
                    using (var cmd = Command(db,
                               $"SELECT i.entity, i.inv, i.t, i.grid, i.owner, i.items, i.volume, i.max_volume, {flows} FROM inventories i " +
                               $"WHERE i.entity IN ({string.Join(",", list)}) AND i.t <= @at AND i.id IN (SELECT j.id FROM inventories j " +
                               "WHERE j.entity = i.entity AND j.inv = i.inv AND j.t <= @at ORDER BY j.t DESC, j.id DESC LIMIT 2)", ("@at", at)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var key = (r.GetInt64(0), r.GetInt64(1));
                            if (!rows.TryGetValue(key, out var two)) rows[key] = two = new List<HeldRow>();
                            if (two.Count < 2)
                                two.Add(new HeldRow
                                {
                                    T = r.GetInt64(2), Grid = r.IsDBNull(3) ? 0 : r.GetInt64(3), Owner = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                                    Items = r.IsDBNull(5) ? null : r.GetString(5), Volume = r.IsDBNull(6) ? 0 : r.GetDouble(6),
                                    Max = r.IsDBNull(7) ? 0 : r.GetDouble(7), Flows = r.IsDBNull(8) ? null : r.GetString(8),
                                });
                        }
                }
            }
            Read(Clock.Day(at), entities);
            var seen = new HashSet<long>(rows.Keys.Select(k => k.Entity));
            Read(Clock.Day(yesterday), entities.Where(e => !seen.Contains(e)));
            foreach (var two in rows.Values) two.Sort((x, y) => y.T.CompareTo(x.T));

            bool Member(HeldRow row, long entity) => kind == "grid" ? row.Grid == id : kind == "entity" ? entity == id : row.Owner == id;
            var held = rows.Where(p => p.Value[0].Items != null && Member(p.Value[0], p.Key.Entity)).ToList();
            var names = Names(held.Select(p => p.Key.Entity).Concat(held.Select(p => p.Value[0].Grid)).Append(id), yesterday, at);
            string Key(long v) => v.ToString(CultureInfo.InvariantCulture);
            string NameOf(long v) => names.TryGetValue(Key(v), out var n) ? n : "";
            return new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["id"] = Key(id),
                ["at"] = at,
                ["inventories"] = held.OrderBy(p => NameOf(p.Value[0].Grid)).ThenBy(p => NameOf(p.Key.Entity)).ThenBy(p => p.Key.Inv)
                    .Select(p =>
                    {
                        var now = p.Value[0];
                        return new Dictionary<string, object>
                        {
                            ["entity"] = Key(p.Key.Entity), ["inv"] = p.Key.Inv, ["t"] = now.T,
                            ["grid"] = now.Grid == 0 ? null : Key(now.Grid), ["owner"] = Key(now.Owner),
                            ["volume"] = now.Volume, ["max"] = now.Max,
                            ["items"] = HeldItems(now.Items),
                            ["prev"] = p.Value.Count > 1 ? HeldItems(p.Value[1].Items) : null,
                            ["prevT"] = p.Value.Count > 1 ? p.Value[1].T : (long?)null,
                            ["flows"] = FlowSet.Decode(now.Flows).Amounts.Select(f => new object[] { f.Key.Kind, f.Key.Item, f.Value / 1_000_000.0 }).ToList(),
                        };
                    }).ToList(),
                ["names"] = names,
            };
        }

        private sealed class HeldRow
        {
            public long T, Grid, Owner;
            public string Items, Flows;
            public double Volume, Max;
        }

        private static Dictionary<string, double> HeldItems(string text)
        {
            var result = new Dictionary<string, double>();
            if (text == null) return result;
            foreach (var stack in Recording.InventoryCodec.Decode(text))
            {
                if (stack.Type == "Gas") continue;
                var item = stack.Type + "/" + stack.Subtype;
                result.TryGetValue(item, out var was);
                result[item] = was + stack.Amount;
            }
            return result;
        }

        /// <summary>Every alert of the range with the names of those involved, newest last.</summary>
        public object Anomalies(long from, long to)
        {
            var alerts = new List<Dictionary<string, object>>();
            var ids = new HashSet<long>();
            foreach (var db in Days(from, to))
                using (db)
                using (var cmd = Command(db, "SELECT t, kind, actor, entity, detail FROM alerts WHERE t BETWEEN @a AND @b ORDER BY t DESC LIMIT " + MaxAnomalies,
                           ("@a", from), ("@b", to)))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        if (!r.IsDBNull(2)) ids.Add(r.GetInt64(2));
                        if (!r.IsDBNull(3)) ids.Add(r.GetInt64(3));
                        alerts.Add(new Dictionary<string, object>
                        {
                            ["t"] = r.GetInt64(0), ["kind"] = r.GetString(1), ["actor"] = Id(r, 2), ["entity"] = Id(r, 3),
                            ["detail"] = r.IsDBNull(4) ? null : r.GetString(4),
                        });
                    }
            alerts = alerts.OrderBy(a => (long)a["t"]).ToList();
            if (alerts.Count > MaxAnomalies) alerts = alerts.Skip(alerts.Count - MaxAnomalies).ToList();
            return new Dictionary<string, object> { ["alerts"] = alerts, ["names"] = Names(ids, from - 86_400_000L, to) };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using System.Text;
using SentisWatcher.Recording;

namespace SentisWatcher.Storage
{
    /// <summary>
    /// Who loaded the game thread over a range (table load): each one's time in an average frame, its share of
    /// the frame, its worst frame; players are their grids and characters together.
    /// </summary>
    public sealed class LoadReport
    {
        public sealed class Item
        {
            public string Kind, Name, Owner;
            public long Id, OwnerId;
            public double Ms, Max;
            public int Grids;
        }

        /// <summary>Frames sampled, and their average and worst.</summary>
        public int Frames;
        public double FrameMs, FrameMax;
        public readonly Dictionary<string, List<Item>> ByKind = new Dictionary<string, List<Item>>();
        public List<Item> Players = new List<Item>();

        public static LoadReport Read(IEnumerable<SQLiteConnection> days, long from, long to)
        {
            var report = new LoadReport();
            var sums = new Dictionary<string, Item>();
            double frameSum = 0;
            foreach (var db in days)
                using (db)
                {
                    using (var check = new SQLiteCommand("SELECT 1 FROM sqlite_master WHERE type='table' AND name='load'", db))
                        if (check.ExecuteScalar() == null) continue;
                    using (var cmd = new SQLiteCommand("SELECT kind, entity, name, owner, owner_name, frames, ms, max_ms FROM load WHERE t BETWEEN @a AND @b", db))
                    {
                        cmd.Parameters.AddWithValue("@a", from);
                        cmd.Parameters.AddWithValue("@b", to);
                        using (var r = cmd.ExecuteReader())
                            while (r.Read())
                            {
                                var kind = r.GetString(0);
                                var frames = r.IsDBNull(5) ? 0 : r.GetInt32(5);
                                var ms = r.IsDBNull(6) ? 0 : r.GetDouble(6);
                                var max = r.IsDBNull(7) ? 0 : r.GetDouble(7);
                                if (kind == LoadSampler.Total)
                                {
                                    report.Frames += frames;
                                    frameSum += ms * frames;
                                    report.FrameMax = Math.Max(report.FrameMax, max);
                                    continue;
                                }
                                var id = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                                var name = r.IsDBNull(2) ? "" : r.GetString(2);
                                var key = kind + "|" + (id != 0 ? id.ToString(CultureInfo.InvariantCulture) : name);
                                if (!sums.TryGetValue(key, out var item)) sums[key] = item = new Item { Kind = kind, Id = id };
                                item.Name = name;                                   // the latest
                                if (!r.IsDBNull(3) && r.GetInt64(3) != 0)
                                {
                                    item.OwnerId = r.GetInt64(3);
                                    item.Owner = r.IsDBNull(4) ? "" : r.GetString(4);
                                }
                                item.Ms += ms * frames;
                                item.Max = Math.Max(item.Max, max);
                            }
                    }
                }
            if (report.Frames == 0) return report;
            report.FrameMs = frameSum / report.Frames;
            foreach (var item in sums.Values) item.Ms /= report.Frames;
            foreach (var kind in sums.Values.GroupBy(i => i.Kind))
                report.ByKind[kind.Key] = kind.OrderByDescending(i => i.Ms).ToList();
            report.Players = sums.Values.Where(i => i.Kind == LoadSampler.Grid || i.Kind == LoadSampler.Character)
                .GroupBy(i => i.OwnerId)
                .Select(g => new Item
                {
                    Kind = "player", OwnerId = g.Key, Id = g.Key,
                    Name = g.Key == 0 ? "" : g.Select(i => i.Owner).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? g.Key.ToString(CultureInfo.InvariantCulture),
                    Ms = g.Sum(i => i.Ms), Max = g.Max(i => i.Max), Grids = g.Count(i => i.Kind == LoadSampler.Grid),
                })
                .OrderByDescending(i => i.Ms).ToList();
            return report;
        }

        /// <summary>For the chat: the heaviest of each kind.</summary>
        public string Describe(int top)
        {
            if (Frames == 0) return "nothing sampled in that time";
            var text = new StringBuilder();
            text.AppendLine($"{Frames} frames sampled, {FrameMs:0.00} ms on average, worst {FrameMax:0.0} ms");
            void Part(string title, List<Item> items)
            {
                if (items == null || items.Count == 0) return;
                text.AppendLine(title + ":");
                foreach (var i in items.Take(top))
                    text.AppendLine($"  {i.Ms:0.000} ms ({100 * i.Ms / FrameMs:0.0}%), worst {i.Max:0.00} ms  {(string.IsNullOrEmpty(i.Name) ? "(nobody)" : i.Name)}" +
                                    (i.Kind == LoadSampler.Grid && !string.IsNullOrEmpty(i.Owner) ? " [" + i.Owner + "]" : ""));
            }
            Part("players", Players);
            ByKind.TryGetValue(LoadSampler.Grid, out var grids); Part("grids", grids);
            ByKind.TryGetValue(LoadSampler.Plugin, out var plugins); Part("plugins", plugins);
            ByKind.TryGetValue(LoadSampler.Component, out var components); Part("session components", components);
            ByKind.TryGetValue(LoadSampler.System, out var systems); Part("engine", systems);
            ByKind.TryGetValue(LoadSampler.Other, out var other); Part("other entities", other);
            return text.ToString();
        }
    }
}

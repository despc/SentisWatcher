using System;
using System.Collections.Generic;
using System.Linq;
using SentisWatcher.Recording;

namespace SentisWatcher.Ledger
{
    /// <summary>One inventory row of a day file, as the ledger reads it back.</summary>
    public sealed class InventoryRow
    {
        public long T, Entity, Grid, Owner;
        public int Inv;
        public string Items;        // null: the inventory is gone
        public string Flows;
    }

    /// <summary>
    /// Plays the inventory rows of a player, a grid or a block over a time range: what it held of each item
    /// after every change, and where each change came from. An inventory counts while its row says it is
    /// the subject's (the owner, the grid); coming or going by a change of owner or grid is booked as such,
    /// so the sum of the sources of an item is its change over the range.
    /// </summary>
    public static class LedgerReplay
    {
        public const int MaxPoints = 1500;
        public const int MaxChanges = 3000;

        /// <summary>The source of a change recorded before the ledger kept sources.</summary>
        public const string Untracked = "untracked";

        public sealed class Change
        {
            public long T, Entity;
            public int Inv;
            public Dictionary<string, long> Delta;
            public FlowSet Flows;
        }

        public sealed class Result
        {
            public readonly List<long> Times = new List<long>();
            public readonly Dictionary<string, List<long>> Series = new Dictionary<string, List<long>>();
            public readonly Dictionary<string, long> Start = new Dictionary<string, long>();
            public readonly Dictionary<string, long> End = new Dictionary<string, long>();
            public readonly FlowSet Sources = new FlowSet();
            public readonly List<Change> Changes = new List<Change>();
            public readonly HashSet<long> Entities = new HashSet<long>();
        }

        /// <param name="rows">All rows of the inventories that were ever the subject's, oldest first, from before the range too.</param>
        /// <param name="member">Whether a row makes its inventory the subject's.</param>
        public static Result Run(IEnumerable<InventoryRow> rows, long from, long to, Func<InventoryRow, bool> member)
        {
            var result = new Result();
            var held = new Dictionary<(long, int), Dictionary<string, long>>();    // the last content of every inventory
            var mine = new Dictionary<(long, int), InventoryRow>();                // the last row, where it is the subject's
            var last = new Dictionary<(long, int), InventoryRow>();                // the last row of every inventory
            var totals = new Dictionary<string, long>();
            var points = new List<(long T, Dictionary<string, long> Totals)>();
            var started = false;

            void Add(Dictionary<string, long> into, Dictionary<string, long> what, int sign)
            {
                foreach (var pair in what)
                {
                    into.TryGetValue(pair.Key, out var was);
                    var now = was + sign * pair.Value;
                    if (now == 0) into.Remove(pair.Key);
                    else into[pair.Key] = now;
                }
            }

            void Start()
            {
                if (started) return;
                started = true;
                foreach (var pair in totals) result.Start[pair.Key] = pair.Value;
                points.Add((from, new Dictionary<string, long>(totals)));
            }

            foreach (var row in rows)
            {
                if (row.T > to) break;
                if (row.T >= from) Start();
                var key = (row.Entity, row.Inv);
                var items = Decode(row.Items);
                held.TryGetValue(key, out var before);
                last.TryGetValue(key, out var previous);
                last[key] = row;
                var how = previous == null || previous.Owner != row.Owner ? "ownership" : "regrid";
                var was = mine.ContainsKey(key);
                var now = member(row);
                if (was) Add(totals, before, -1);
                if (now) Add(totals, items, +1);
                if (now) mine[key] = row;
                else mine.Remove(key);
                held[key] = items;
                if (!started || (!was && !now)) continue;

                // where the subject's change came from
                var flows = FlowSet.Decode(row.Flows);
                var explained = new FlowSet();
                if (was && now)
                {
                    if (!flows.IsEmpty) explained.Add(flows);
                    else if (before != null)
                    {
                        // a change with no flow at all: written while the ledger was not keeping them (an older
                        // version of the plugin) - any change it books has a source, "unexplained" at least
                        foreach (var pair in items) explained.Add(Untracked, pair.Key, pair.Value);
                        foreach (var pair in before) explained.Add(Untracked, pair.Key, -pair.Value);
                    }
                }
                else if (now)
                {
                    // came to the subject: what it held before comes with it (or it is new, or first seen)
                    if (before != null) foreach (var pair in before) explained.Add(how, pair.Key, pair.Value);
                    if (before != null || flows.Amounts.Keys.Any(k => k.Kind.StartsWith("load"))) explained.Add(flows);
                    else foreach (var pair in items) explained.Add("seen", pair.Key, pair.Value);
                }
                else
                {
                    // left the subject: with its flows booked here, what it holds now leaves with it
                    explained.Add(flows);
                    foreach (var pair in items) explained.Add(how, pair.Key, -pair.Value);
                }
                result.Sources.Add(explained);
                result.Entities.Add(row.Entity);
                var delta = new Dictionary<string, long>();
                if (now) Add(delta, items, +1);
                if (was && before != null) Add(delta, before, -1);
                if (delta.Count > 0 || !explained.IsEmpty)
                {
                    result.Changes.Add(new Change { T = row.T, Entity = row.Entity, Inv = row.Inv, Delta = delta, Flows = explained });
                    if (delta.Count > 0) points.Add((row.T, new Dictionary<string, long>(totals)));
                }
            }
            Start();
            foreach (var pair in totals) result.End[pair.Key] = pair.Value;
            points.Add((Math.Max(from, to), new Dictionary<string, long>(totals)));
            foreach (var key in mine.Keys) result.Entities.Add(key.Item1);

            var items2 = new HashSet<string>(points.SelectMany(p => p.Totals.Keys));
            foreach (var item in items2) result.Series[item] = new List<long>();
            foreach (var point in Thin(points, MaxPoints))
            {
                result.Times.Add(point.T);
                foreach (var item in items2) result.Series[item].Add(point.Totals.TryGetValue(item, out var v) ? v : 0);
            }
            if (result.Changes.Count > MaxChanges) result.Changes.RemoveRange(0, result.Changes.Count - MaxChanges);
            return result;
        }

        private static Dictionary<string, long> Decode(string items)
        {
            var result = new Dictionary<string, long>();
            if (items == null) return result;
            foreach (var stack in InventoryCodec.Decode(items))
            {
                if (stack.Type == "Gas") continue;
                var name = stack.Type + "/" + stack.Subtype;
                result.TryGetValue(name, out var was);
                result[name] = was + stack.Raw;
            }
            return result;
        }

        /// <summary>Evenly fewer points, the first and the last kept; a change is never lost between two kept ones
        /// because each point holds the totals, not a difference.</summary>
        private static List<T> Thin<T>(List<T> points, int max)
        {
            if (points.Count <= max) return points;
            var result = new List<T>(max);
            var step = (points.Count - 1) / (double)(max - 1);
            for (var i = 0; i < max - 1; i++) result.Add(points[(int)Math.Round(i * step)]);
            result.Add(points[points.Count - 1]);
            return result;
        }
    }
}

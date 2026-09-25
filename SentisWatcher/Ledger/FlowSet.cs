using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SentisWatcher.Ledger
{
    /// <summary>
    /// Where the items of one inventory came from and went to between two of its snapshots: raw amounts
    /// (millionths) by kind of flow and item. Kinds are words like "refine", "assemble", "transfer@123" (from
    /// or to grid 123), "move" (within the grid), "load:paste", "plugin:Name", "unknown:MyType.Method".
    /// A day file keeps it as text next to the snapshot: "refine|Ingot/Iron:+4800;move|Ore/Iron:-100".
    /// </summary>
    public sealed class FlowSet
    {
        public readonly Dictionary<(string Kind, string Item), long> Amounts = new Dictionary<(string, string), long>();

        public bool IsEmpty => Amounts.Count == 0;

        public void Add(string kind, string item, long raw)
        {
            if (raw == 0) return;
            var key = (kind, item);
            Amounts.TryGetValue(key, out var was);
            var now = was + raw;
            if (now == 0) Amounts.Remove(key);
            else Amounts[key] = now;
        }

        public void Add(FlowSet other)
        {
            foreach (var pair in other.Amounts) Add(pair.Key.Kind, pair.Key.Item, pair.Value);
        }

        /// <summary>What the flows add up to for each item.</summary>
        public Dictionary<string, long> NetByItem()
        {
            var net = new Dictionary<string, long>();
            foreach (var pair in Amounts)
            {
                net.TryGetValue(pair.Key.Item, out var was);
                net[pair.Key.Item] = was + pair.Value;
            }
            return net;
        }

        public string Encode()
        {
            if (IsEmpty) return null;
            var text = new StringBuilder(Amounts.Count * 32);
            foreach (var pair in Amounts.OrderBy(p => p.Key.Kind, StringComparer.Ordinal).ThenBy(p => p.Key.Item, StringComparer.Ordinal))
            {
                if (text.Length > 0) text.Append(';');
                text.Append(Clean(pair.Key.Kind)).Append('|').Append(pair.Key.Item).Append(':')
                    .Append(pair.Value > 0 ? "+" : "").Append((pair.Value / 1_000_000.0).ToString("0.######", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        public static FlowSet Decode(string text)
        {
            var flows = new FlowSet();
            if (string.IsNullOrEmpty(text)) return flows;
            foreach (var part in text.Split(';'))
            {
                var bar = part.IndexOf('|');
                var colon = part.LastIndexOf(':');
                if (bar < 0 || colon < bar) continue;
                if (!double.TryParse(part.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) continue;
                flows.Add(part.Substring(0, bar), part.Substring(bar + 1, colon - bar - 1), (long)Math.Round(amount * 1_000_000));
            }
            return flows;
        }

        /// <summary>A kind may hold anything but the separators of the text form.</summary>
        public static string Clean(string kind) => kind.IndexOfAny(Separators) < 0 ? kind : new string(kind.Select(c => Array.IndexOf(Separators, c) < 0 ? c : '_').ToArray());

        private static readonly char[] Separators = { ';', '|' };
    }

    /// <summary>The arithmetic of the ledger, apart from the game.</summary>
    public static class LedgerMath
    {
        /// <summary>
        /// What changed in an inventory that no flow explains: now - before - flows, by item, zeros left out.
        /// An honest inventory has nothing here: everything that enters or leaves it goes through a hook.
        /// </summary>
        public static Dictionary<string, long> Unexplained(IReadOnlyDictionary<string, long> before, IReadOnlyDictionary<string, long> now, FlowSet flows)
        {
            var result = new Dictionary<string, long>();
            void Add(string item, long raw)
            {
                result.TryGetValue(item, out var was);
                result[item] = was + raw;
            }
            foreach (var pair in now) Add(pair.Key, pair.Value);
            foreach (var pair in before) Add(pair.Key, -pair.Value);
            if (flows != null)
                foreach (var pair in flows.NetByItem()) Add(pair.Key, -pair.Value);
            foreach (var zero in result.Where(p => p.Value == 0).Select(p => p.Key).ToList()) result.Remove(zero);
            return result;
        }

        /// <summary>The items where more arrived than left: a move that made items.</summary>
        public static Dictionary<string, long> Excess(IReadOnlyDictionary<string, long> arrived, IReadOnlyDictionary<string, long> left, IReadOnlyDictionary<string, long> allowance = null)
        {
            var result = new Dictionary<string, long>();
            foreach (var pair in arrived)
            {
                left.TryGetValue(pair.Key, out var gone);
                long extra = 0;
                allowance?.TryGetValue(pair.Key, out extra);
                var excess = pair.Value - gone - extra;
                if (excess > Tolerance(pair.Value)) result[pair.Key] = excess;
            }
            return result;
        }

        /// <summary>
        /// The inputs production took less of than it had to for what it made: required and taken by item.
        /// </summary>
        public static Dictionary<string, (long Required, long Taken)> Shortfall(IReadOnlyDictionary<string, long> required, IReadOnlyDictionary<string, long> taken)
        {
            var result = new Dictionary<string, (long, long)>();
            foreach (var pair in required)
            {
                taken.TryGetValue(pair.Key, out var got);
                if (pair.Value - got > Tolerance(pair.Value)) result[pair.Key] = (pair.Value, got);
            }
            return result;
        }

        /// <summary>
        /// Room for float rounding in the game's own sums: a thousandth, and at least a thousandth of a unit (a
        /// refinery's small partial batches round off more than that relative to their size).
        /// </summary>
        public static long Tolerance(long raw) => Math.Max(1000, Math.Abs(raw) / 1000);

        public static string Describe(IEnumerable<KeyValuePair<string, long>> items) =>
            string.Join(", ", items.Select(p => p.Key + " " + (p.Value > 0 ? "+" : "") + (p.Value / 1_000_000.0).ToString("0.######", CultureInfo.InvariantCulture)));
    }
}

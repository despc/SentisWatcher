using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SentisWatcher.Recording
{
    /// <summary>One stack of an inventory, as the day file keeps it.</summary>
    public struct ItemStack
    {
        public string Type;      // Ore, Ingot, Component... (the object builder type without its prefix)
        public string Subtype;
        public long Raw;         // MyFixedPoint.RawValue: the amount times 1 000 000

        public ItemStack(string type, string subtype, long raw)
        {
            Type = type;
            Subtype = subtype;
            Raw = raw;
        }

        public double Amount => Raw / 1_000_000.0;
    }

    /// <summary>
    /// The items of an inventory as text - "Ore/Iron:1234.5;Component/SteelPlate:10" - easy to read by eye,
    /// in SQL and in a web page; and a hash that tells whether an inventory changed since it was written.
    /// </summary>
    public static class InventoryCodec
    {
        private const string BuilderPrefix = "MyObjectBuilder_";

        public static string ShortType(string typeId) =>
            typeId != null && typeId.StartsWith(BuilderPrefix) ? typeId.Substring(BuilderPrefix.Length) : typeId ?? "";

        public static string Encode(IList<ItemStack> items)
        {
            var text = new StringBuilder(items.Count * 24);
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) text.Append(';');
                text.Append(items[i].Type).Append('/').Append(items[i].Subtype).Append(':')
                    .Append(items[i].Amount.ToString("0.######", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        public static List<ItemStack> Decode(string text)
        {
            var items = new List<ItemStack>();
            if (string.IsNullOrEmpty(text)) return items;
            foreach (var part in text.Split(';'))
            {
                var slash = part.IndexOf('/');
                var colon = part.LastIndexOf(':');
                if (slash < 0 || colon < slash) continue;
                var amount = double.Parse(part.Substring(colon + 1), CultureInfo.InvariantCulture);
                items.Add(new ItemStack(part.Substring(0, slash), part.Substring(slash + 1, colon - slash - 1), (long)System.Math.Round(amount * 1_000_000)));
            }
            return items;
        }

        /// <summary>FNV-1a over the stacks, in order; the same content gives the same hash.</summary>
        public static ulong Hash(IList<ItemStack> items, long extra = 0)
        {
            var hash = 14695981039346656037UL;
            void Mix(long value)
            {
                for (var b = 0; b < 8; b++)
                {
                    hash ^= (byte)(value >> (b * 8));
                    hash *= 1099511628211UL;
                }
            }
            Mix(items.Count);
            Mix(extra);
            foreach (var item in items)
            {
                Mix(item.Type.GetHashCode());
                Mix(item.Subtype.GetHashCode());
                Mix(item.Raw);
            }
            return hash;
        }
    }
}

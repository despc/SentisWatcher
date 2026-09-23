using System;
using System.Collections.Generic;

namespace SentisWatcher.Anomalies
{
    /// <summary>
    /// Checks that hold for every honest inventory and every honest transfer; a failed one is logged as an
    /// alert. Nothing is undone or punished.
    /// </summary>
    public static class Invariants
    {
        /// <summary>Room for rounding before an inventory counts as overfilled.</summary>
        public const double OverfillTolerance = 0.01;

        /// <summary>Types that only come in whole pieces.</summary>
        public static readonly HashSet<string> WholeTypes = new HashSet<string>
        {
            "Component", "AmmoMagazine", "PhysicalGunObject", "OxygenContainerObject", "GasContainerObject",
            "Datapad", "ConsumableItem", "Package", "PhysicalObject",
        };

        /// <summary>
        /// A transfer may move items, never make them: what the two inventories hold of the item together
        /// after it is at most what they held before (plus rounding).
        /// </summary>
        public static bool TransferMadeItems(long sourceBefore, long targetBefore, long sourceAfter, long targetAfter, bool sameInventory)
        {
            if (sameInventory) return sourceAfter > sourceBefore;
            return sourceAfter + targetAfter > sourceBefore + targetBefore;
        }

        /// <summary>What is wrong with a stack's amount, or null (raw amounts are millionths).</summary>
        public static string BadAmount(string type, long raw)
        {
            if (raw < 0) return "negative amount";
            if (WholeTypes.Contains(type) && raw % 1_000_000 != 0) return "a fraction of a " + type;
            return null;
        }

        /// <summary>Whether the inventory holds more than it can (a max of 0 or less is not checked).</summary>
        public static bool Overfilled(double volume, double maxVolume) =>
            maxVolume > 0 && !double.IsInfinity(maxVolume) && volume > maxVolume * (1 + OverfillTolerance);

        /// <summary>Whether a gas tank is fuller than full.</summary>
        public static bool TankOverfilled(double filledRatio) => double.IsNaN(filledRatio) || filledRatio > 1 + 1e-4;
    }

    /// <summary>Lets an alert of one kind about one thing through once an hour, not every second.</summary>
    public sealed class AlertLimiter
    {
        public static readonly TimeSpan Every = TimeSpan.FromHours(1);
        private readonly Dictionary<(string, long), DateTime> _last = new Dictionary<(string, long), DateTime>();

        public bool Allow(string kind, long entity, DateTime now)
        {
            lock (_last)
            {
                if (_last.TryGetValue((kind, entity), out var last) && now - last < Every) return false;
                _last[(kind, entity)] = now;
                return true;
            }
        }
    }
}

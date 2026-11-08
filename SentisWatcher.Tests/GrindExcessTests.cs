using System.Collections.Generic;
using SentisWatcher.Ledger;
using Xunit;

namespace SentisWatcher.Tests
{
    /// <summary>
    /// Grinding a block: what it gave past what it is built of and was welded with. A block not whole gives one item
    /// out of its construction stockpile, and a component counts as what it is ground into too - neither is an alert.
    /// </summary>
    public class GrindExcessTests
    {
        private const long U = 1_000_000;

        private static Dictionary<string, long> Items(params (string Item, long Count)[] items)
        {
            var result = new Dictionary<string, long>();
            foreach (var (item, count) in items) result[item] = count * U;
            return result;
        }

        private static readonly Dictionary<string, long> None = new Dictionary<string, long>();

        private static (string, string, long)[] Built(params (string Component, string Into, long Count)[] parts)
        {
            var result = new (string, string, long)[parts.Length];
            for (var i = 0; i < parts.Length; i++) result[i] = (parts[i].Component, parts[i].Into, parts[i].Count * U);
            return result;
        }

        private const string Steel = "Component/SteelPlate";
        private const string Cell = "Component/PowerCell";

        [Fact]
        public void A_block_giving_what_it_is_built_of_is_no_excess() =>
            Assert.Empty(LedgerMath.GrindExcess(Items((Steel, 21)), Built((Steel, Steel, 21)), None));

        [Fact]
        public void One_more_out_of_the_stockpile_is_no_excess()
        {
            // the projector on Starfier RAT: out 22, built of 21 (prod, 07.11.2026)
            Assert.Empty(LedgerMath.GrindExcess(Items((Steel, 22)), Built((Steel, Steel, 21)), None));
            // the landing gear on Light Supply Craft: out 3, built of 2 (prod, 08.11.2026)
            Assert.Empty(LedgerMath.GrindExcess(Items((Steel, 3)), Built((Steel, Steel, 2)), None));
        }

        [Fact]
        public void Two_more_is_an_excess()
        {
            var excess = LedgerMath.GrindExcess(Items((Steel, 23)), Built((Steel, Steel, 21)), None);
            Assert.Equal(2 * U, excess[Steel]);
        }

        [Fact]
        public void Much_more_is_an_excess()
        {
            var excess = LedgerMath.GrindExcess(Items((Steel, 500)), Built((Steel, Steel, 21)), None);
            Assert.Equal(479 * U, excess[Steel]);
        }

        [Fact]
        public void A_component_counts_as_itself_when_it_is_ground_into_something_else()
        {
            // a battery's power cells, ground into another item by its definition, came out as PowerCell: "built of 0"
            Assert.Empty(LedgerMath.GrindExcess(Items((Cell, 20)), Built((Cell, "Component/Scrap", 20)), None));
        }

        [Fact]
        public void A_component_counts_as_what_it_is_ground_into()
        {
            Assert.Empty(LedgerMath.GrindExcess(Items(("Ingot/PrototechScrap", 5)), Built(("Component/PrototechFrame", "Ingot/PrototechScrap", 5)), None));
        }

        [Fact]
        public void An_item_it_is_not_built_of_at_all_is_an_excess()
        {
            var excess = LedgerMath.GrindExcess(Items(("Component/Superconductor", 10)), Built((Steel, Steel, 21)), None);
            Assert.Equal(10 * U, excess["Component/Superconductor"]);
        }

        [Fact]
        public void One_item_it_is_not_built_of_is_not_an_alert() =>
            Assert.Empty(LedgerMath.GrindExcess(Items(("Component/Motor", 1)), Built((Steel, Steel, 21)), None));

        [Fact]
        public void What_was_welded_in_counts()
        {
            Assert.Empty(LedgerMath.GrindExcess(Items((Steel, 40)), Built((Steel, Steel, 21)), Items((Steel, 19))));
            Assert.Equal(5 * U, LedgerMath.GrindExcess(Items((Steel, 45)), Built((Steel, Steel, 21)), Items((Steel, 19)))[Steel]);
        }

        [Fact]
        public void Each_item_is_weighed_on_its_own()
        {
            var excess = LedgerMath.GrindExcess(Items((Steel, 22), ("Component/Construction", 30)),
                Built((Steel, Steel, 21), ("Component/Construction", "Component/Construction", 10)), None);
            Assert.False(excess.ContainsKey(Steel));
            Assert.Equal(20 * U, excess["Component/Construction"]);
        }

        [Fact]
        public void A_component_listed_twice_adds_up() =>
            Assert.Empty(LedgerMath.GrindExcess(Items((Steel, 30)), Built((Steel, Steel, 20), (Steel, Steel, 10)), None));
    }
}

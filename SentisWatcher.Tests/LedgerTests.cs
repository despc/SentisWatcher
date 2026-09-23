using System.Collections.Generic;
using System.Linq;
using SentisWatcher.Ledger;
using Xunit;

namespace SentisWatcher.Tests
{
    /// <summary>The inventory ledger's arithmetic: flows, the balance of an inventory, the checks, the replay.</summary>
    public class LedgerTests
    {
        private const long U = 1_000_000;

        [Fact]
        public void Flows_add_up_cancel_out_and_survive_their_text_form()
        {
            var flows = new FlowSet();
            flows.Add("refine", "Ingot/Iron", 4800 * U);
            flows.Add("transfer@123", "Ore/Iron", -100 * U + 5);
            flows.Add("move", "Ore/Iron", 7 * U);
            flows.Add("move", "Ore/Iron", -7 * U);         // in and out again: nothing left of it
            Assert.Equal(2, flows.Amounts.Count);

            var back = FlowSet.Decode(flows.Encode());
            Assert.Equal(4800 * U, back.Amounts[("refine", "Ingot/Iron")]);
            Assert.Equal(-100 * U + 5, back.Amounts[("transfer@123", "Ore/Iron")]);
            Assert.Null(new FlowSet().Encode());
            Assert.Equal("a_b_c", FlowSet.Clean("a|b;c"));
        }

        [Fact]
        public void What_no_flow_explains_is_unexplained()
        {
            var before = new Dictionary<string, long> { ["Ore/Iron"] = 100 * U };
            var now = new Dictionary<string, long> { ["Ore/Iron"] = 150 * U, ["Ingot/Platinum"] = 5 * U };
            var flows = new FlowSet();
            flows.Add("drill", "Ore/Iron", 50 * U);
            var unexplained = LedgerMath.Unexplained(before, now, flows);
            Assert.Equal(5 * U, unexplained.Single(p => p.Key == "Ingot/Platinum").Value);
            Assert.DoesNotContain("Ore/Iron", unexplained.Keys);
            Assert.Empty(LedgerMath.Unexplained(now, now, null));
        }

        [Fact]
        public void A_move_may_not_make_items_and_production_must_take_its_inputs()
        {
            var left = new Dictionary<string, long> { ["Ore/Iron"] = 100 * U };
            Assert.Empty(LedgerMath.Excess(new Dictionary<string, long> { ["Ore/Iron"] = 100 * U }, left));
            Assert.Empty(LedgerMath.Excess(new Dictionary<string, long> { ["Ore/Iron"] = 100 * U + 3 }, left));      // rounding
            Assert.Equal(100 * U, LedgerMath.Excess(new Dictionary<string, long> { ["Ore/Iron"] = 200 * U }, left)["Ore/Iron"]);
            // a grinder may give back what was welded in on top of what the block is built of
            var welded = new Dictionary<string, long> { ["Ore/Iron"] = 100 * U };
            Assert.Empty(LedgerMath.Excess(new Dictionary<string, long> { ["Ore/Iron"] = 200 * U }, left, welded));

            var required = new Dictionary<string, long> { ["Ingot/Iron"] = 30 * U, ["Ingot/Nickel"] = 5 * U };
            var shortfall = LedgerMath.Shortfall(required, new Dictionary<string, long> { ["Ingot/Iron"] = 30 * U });
            Assert.Equal((5 * U, 0L), shortfall.Single().Value);
            Assert.Empty(LedgerMath.Shortfall(required, required));
        }

        private static InventoryRow Row(long t, long entity, long owner, string items, string flows = null, long grid = 9) =>
            new InventoryRow { T = t, Entity = entity, Inv = 0, Owner = owner, Grid = grid, Items = items, Flows = flows };

        [Fact]
        public void The_replay_explains_every_change_of_the_subject()
        {
            var rows = new List<InventoryRow>
            {
                Row(0, 1, 7, "Ingot/Platinum:10"),                                              // before the range
                Row(20, 1, 7, "Ingot/Platinum:510", "plugin:Evil|Ingot/Platinum:+500"),
                Row(30, 2, 7, "Ore/Iron:5", "load:paste|Ore/Iron:+5"),                          // a new inventory
                Row(40, 1, 8, "Ingot/Platinum:510"),                                            // given away
                Row(50, 2, 7, null, "gone|Ore/Iron:-5"),                                        // ground down
            };
            var result = LedgerReplay.Run(rows, 10, 100, r => r.Owner == 7);
            Assert.Equal(10 * U, result.Start["Ingot/Platinum"]);
            Assert.Empty(result.End);
            var sources = result.Sources.Amounts;
            Assert.Equal(500 * U, sources[("plugin:Evil", "Ingot/Platinum")]);
            Assert.Equal(-510 * U, sources[("ownership", "Ingot/Platinum")]);
            Assert.Equal(5 * U, sources[("load:paste", "Ore/Iron")]);
            Assert.Equal(-5 * U, sources[("gone", "Ore/Iron")]);
            Assert.Equal(4, result.Changes.Count);
            // the amounts after each change: from, 20, 30, 40, 50, to
            Assert.Equal(new long[] { 10, 20, 30, 40, 50, 100 }, result.Times.ToArray());
            Assert.Equal(new[] { 10 * U, 510 * U, 510 * U, 0, 0, 0 }, result.Series["Ingot/Platinum"].ToArray());
        }

        [Fact]
        public void A_change_recorded_before_the_ledger_is_booked_as_untracked()
        {
            var rows = new[] { Row(0, 1, 7, "Ingot/Uranium:10"), Row(20, 1, 7, "Ingot/Uranium:9.5"), Row(30, 1, 7, "Ingot/Uranium:9", "consume|Ingot/Uranium:-0.5") };
            var result = LedgerReplay.Run(rows, 10, 100, r => r.Owner == 7);
            Assert.Equal(-U / 2, result.Sources.Amounts[(LedgerReplay.Untracked, "Ingot/Uranium")]);
            Assert.Equal(-U / 2, result.Sources.Amounts[("consume", "Ingot/Uranium")]);
        }

        [Fact]
        public void An_inventory_first_seen_in_the_range_is_booked_as_seen()
        {
            var result = LedgerReplay.Run(new[] { Row(20, 1, 7, "Ore/Iron:3") }, 10, 100, r => r.Owner == 7);
            Assert.Equal(3 * U, result.Sources.Amounts[("seen", "Ore/Iron")]);
        }
    }
}

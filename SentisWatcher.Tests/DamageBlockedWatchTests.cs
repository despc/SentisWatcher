using SentisWatcher.Recording;
using Xunit;

namespace SentisWatcher.Tests
{
    /// <summary>A grid shot that takes no damage: when it is told and why.</summary>
    public class DamageBlockedWatchTests
    {
        [Fact]
        public void Enough_hits_and_no_damage_is_blocked_and_any_damage_is_not()
        {
            Assert.True(DamageBlockedWatch.Blocked(DamageBlockedWatch.MinHits, 0));
            Assert.False(DamageBlockedWatch.Blocked(DamageBlockedWatch.MinHits - 1, 0));
            Assert.False(DamageBlockedWatch.Blocked(100, 0.5));
        }

        [Fact]
        public void The_grids_own_flags_are_named()
        {
            var why = DamageBlockedWatch.Reasons(true, false, true, 1f, 0, 0, 0, null);
            Assert.Contains("DestructibleBlocks is off", why);
            Assert.Contains("Immune", why);
            Assert.DoesNotContain("never reached", why);
        }

        [Fact]
        public void The_world_and_the_modifiers_and_the_handlers_are_named()
        {
            Assert.Contains("the world's blocks are indestructible", DamageBlockedWatch.Reasons(false, true, false, 1f, 0, 0, 0, null));
            Assert.Contains("damage modifier is 0", DamageBlockedWatch.Reasons(true, true, false, 0f, 3, 0, 0, null));
            Assert.Contains("4 hits on blocks of damage modifier 0", DamageBlockedWatch.Reasons(true, true, false, 1f, 4, 0, 4, null));
            Assert.Contains("made 0 by a damage handler (Shields.Mod.Before)", DamageBlockedWatch.Reasons(true, true, false, 1f, 5, 5, 0, "Shields.Mod.Before"));
        }

        [Fact]
        public void Nothing_known_and_nothing_reaching_the_blocks_is_something_in_between()
        {
            Assert.Contains("never reached a block's damage", DamageBlockedWatch.Reasons(true, true, false, 1f, 0, 0, 0, null));
        }
    }
}

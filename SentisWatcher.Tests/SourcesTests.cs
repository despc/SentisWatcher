using SentisWatcher.Ledger;
using Xunit;

namespace SentisWatcher.Tests
{
    public class SourcesTests
    {
        [Fact]
        public void A_patched_respawn_method_is_a_respawn()
        {
            var patched = Sources.FromPatched("Patched_SpaceEngineers.Game.World.MySpaceRespawnComponentSpawnInCockpit_0");
            Assert.Equal(("MySpaceRespawnComponent", "SpawnInCockpit"), patched);
            Assert.Equal("respawn", Sources.Known(patched.Value.Type, patched.Value.Method));
        }

        [Fact]
        public void The_longest_known_type_is_taken()
        {
            Assert.Equal(("MyRespawnComponentBase", "HandleRespawnRequest"), Sources.FromPatched("Patched_VRage.Game.Components.MyRespawnComponentBaseHandleRespawnRequest_3"));
        }

        [Fact]
        public void A_character_patched_method_is_known_by_its_method()
        {
            var patched = Sources.FromPatched("Patched_Sandbox.Game.Entities.Character.MyCharacterDie_0");
            Assert.Equal("bag", Sources.Known(patched.Value.Type, patched.Value.Method));
        }

        [Fact]
        public void Anything_else_is_not_named()
        {
            Assert.Null(Sources.FromPatched("Patched_Sandbox.Game.Entities.MyPlanetGetClosestSurfacePointGlobal_0"));
            Assert.Null(Sources.FromPatched("MySpaceRespawnComponentSpawnInCockpit"));
            Assert.Null(Sources.FromPatched(null));
        }

        [Fact]
        public void An_animal_spawn_kit_is_npc()
        {
            Assert.Equal("npc", Sources.Known("MyAgentDefinition", "AddItems"));
        }
    }
}

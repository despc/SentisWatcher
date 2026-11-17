using SentisWatcher.Recording;
using Xunit;

namespace SentisWatcher.Tests
{
    /// <summary>VirtualGarage's entries and where a grid comes from.</summary>
    public class GarageWatchTests
    {
        [Theory]
        [InlineData(@"C:\Garage\76561198000000000\Ship_2026.11.17_10.00_P-100_B-20_1234_unsaved.sbc")]
        [InlineData(@"C:\Garage\76561198000000000\Ship_2026.11.17_10.00_P-100_B-20_1234.sbc")]
        [InlineData(@"C:\Garage\76561198000000000\Ship_2026.11.17_10.00_P-100_B-20_1234.sbc_spawned_unsaved")]
        [InlineData(@"C:\Garage\76561198000000000\Ship_2026.11.17_10.00_P-100_B-20_1234.sbc_spawned")]
        public void A_garage_entry_is_one_key_in_every_state(string file)
        {
            Assert.Equal("76561198000000000/Ship_2026.11.17_10.00_P-100_B-20_1234", GarageWatch.Key(file));
        }

        [Fact]
        public void The_garage_the_services_terminal_and_a_paste_are_told_and_the_games_own_spawns_not()
        {
            Assert.Equal("garage", GridSpawnWatch.Known("VirtualGarage", "VirtualGarageLoad", "Spawn"));
            Assert.Equal("services_terminal", GridSpawnWatch.Known("Sandbox.Game", "MyGridStorageHelper", "LoadGrid_Internal_Phase2"));
            Assert.Equal("paste", GridSpawnWatch.Known("Sandbox.Game", "MyCubeGrid", "TryPasteGrid_ImplementationInternal"));
            Assert.Equal("admin", GridSpawnWatch.Known("Sandbox.Game", "MyGuiScreenAdminMenu", "Spawn"));
            Assert.Equal("", GridSpawnWatch.Known("Sandbox.Game", "MyCubeGrid", "CreateSplit"));
            Assert.Equal("", GridSpawnWatch.Known("Sandbox.Game", "MyCubeBuilder", "BuildGrid"));
            Assert.Equal("", GridSpawnWatch.Known("Sandbox.Game", "MyPrefabManager", "SpawnPrefab"));
            Assert.Null(GridSpawnWatch.Known("Sandbox.Game", "MyEntities", "Add"));
        }
    }
}

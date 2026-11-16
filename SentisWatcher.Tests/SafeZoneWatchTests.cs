using SentisWatcher.Recording;
using Xunit;

namespace SentisWatcher.Tests
{
    /// <summary>Why an admins' safe zone went, read from the stack of its closing (innermost frame first).</summary>
    public class SafeZoneWatchTests
    {
        [Fact]
        public void A_station_area_unloaded_is_the_game_and_normal()
        {
            var (reason, normal, unloading) = SafeZoneWatch.Explain(new[]
            {
                "Sandbox.Game.World.Generator.MyStationCellGenerator.CloseObjectSeed",
                "Sandbox.Game.World.Generator.MyProceduralWorldModule.CloseObjectSeeds",
                "Torch.VRageGame.DoStart",
            }, null);
            Assert.True(normal);
            Assert.False(unloading);
            Assert.Contains("station", reason);
        }

        [Fact]
        public void The_admin_menu_names_the_admin_and_is_not_normal()
        {
            var (reason, normal, _) = SafeZoneWatch.Explain(new[]
            {
                "Sandbox.Game.Entities.MySessionComponentSafeZones.DeleteSafeZone_Implementation",
                "Sandbox.Game.Entities.MySessionComponentSafeZones+DeleteSafeZone_Implementation_003C_003ESystem_Int64.Invoke",
                "VRage.Network.MyReplicationLayer.Invoke",
            }, "Admin (76561198000000000)");
            Assert.False(normal);
            Assert.Equal("deleted from the admin menu by Admin (76561198000000000)", reason);
        }

        [Fact]
        public void A_plugin_is_named_by_its_frames_not_the_game_loop_around_it()
        {
            var (reason, normal, _) = SafeZoneWatch.Explain(new[]
            {
                "Essentials.Commands.CleanupModule.Delete",
                "Torch.Commands.CommandManager.HandleCommand",
                ".Patched_Sandbox.Game.World.MySessionUpdateComponents_1",
                "Torch.VRageGame.DoStart",
            }, null);
            Assert.False(normal);
            Assert.Equal("closed by Essentials.Commands.CleanupModule.Delete <- Torch.Commands.CommandManager.HandleCommand", reason);
        }

        [Fact]
        public void A_change_names_only_what_changed_and_nothing_when_nothing_did()
        {
            var before = new System.Collections.Generic.Dictionary<string, string> { ["on"] = "on", ["radius"] = "150", ["allowed"] = "Welding" };
            var now = new System.Collections.Generic.Dictionary<string, string> { ["on"] = "off", ["radius"] = "150", ["allowed"] = "All" };
            Assert.Equal("on on -> off, allowed Welding -> All", SafeZoneWatch.Changes(before, now));
            Assert.Null(SafeZoneWatch.Changes(before, before));
            Assert.StartsWith("now on off", SafeZoneWatch.Changes(null, now));
        }

        [Fact]
        public void A_change_through_the_admin_menu_or_a_players_block_is_told_apart()
        {
            Assert.Equal("the admin menu by Admin (1)", SafeZoneWatch.Through(new[]
            {
                "Sandbox.Game.Entities.MySessionComponentSafeZones.UpdateSafeZone",
                "Sandbox.Game.Entities.MySessionComponentSafeZones.UpdateSafeZone_Implementation",
            }, "Admin (1)"));
            Assert.Equal("the admin menu", SafeZoneWatch.Through(new[]
            {
                "Sandbox.Game.Entities.MySessionComponentSafeZones.UpdateSafeZone",
                "Sandbox.Game.Entities.MySessionComponentSafeZones+UpdateSafeZone_Implementation<>Sandbox_Common_ObjectBuilders_MyObjectBuilder_SafeZone.Invoke",
                "VRage.Network.MyReplicationLayer.Invoke",
            }, null));
            Assert.StartsWith("a player's safe zone block's settings by Riot (2)", SafeZoneWatch.Through(new[]
            {
                "Sandbox.Game.Entities.MySessionComponentSafeZones.UpdateSafeZone",
                "Sandbox.Game.Entities.MySessionComponentSafeZones.UpdateSafeZone_ImplementationPlayer",
            }, "Riot (2)"));
            Assert.StartsWith("a player's safe zone block's settings", SafeZoneWatch.Through(new[]
            {
                "Sandbox.Game.Entities.MySessionComponentSafeZones.UpdateSafeZone",
                "Sandbox.Game.Entities.MySessionComponentSafeZones+UpdateSafeZone_ImplementationPlayer<>System_Int64#Sandbox_Common_ObjectBuilders_MyObjectBuilder_SafeZone.Invoke",
            }, null));
            Assert.Equal("by SomePlugin.Zones.Shrink", SafeZoneWatch.Through(new[] { "Sandbox.Game.Entities.MySessionComponentSafeZones.UpdateSafeZone", "SomePlugin.Zones.Shrink" }, null));
        }

        [Fact]
        public void The_world_unloading_is_left_out()
        {
            Assert.True(SafeZoneWatch.Explain(new[] { "Sandbox.Game.Entities.MyEntities.CloseAll", "Sandbox.Game.World.MySession.Unload" }, null).Unloading);
        }
    }
}

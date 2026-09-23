using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Game.ModAPI;

namespace SentisWatcher.Recording
{
    /// <summary>Who stands behind an entity: the player (identity id) it belongs to or who uses it.</summary>
    public static class Identities
    {
        public static long OfSteam(ulong steamId) =>
            steamId == 0 || MySession.Static?.Players == null ? 0 : MySession.Static.Players.TryGetIdentityId(steamId);

        /// <summary>The identity behind the entity that did something (a character, a hand tool, a block, a grid).</summary>
        public static long OfEntity(long entityId)
        {
            if (entityId == 0 || !MyEntities.TryGetEntityById(entityId, out var entity)) return 0;
            return OfEntity(entity);
        }

        public static long OfEntity(MyEntity entity)
        {
            switch (entity)
            {
                case null: return 0;
                case MyCharacter character: return character.GetPlayerIdentityId();
                case IMyHandheldGunObject<MyDeviceBase> tool: return tool.OwnerIdentityId;
                case MyCubeBlock block: return Controller(block.CubeGrid) ?? block.OwnerId;
                case MyCubeGrid grid: return Controller(grid) ?? Owner(grid);
                default: return 0;
            }
        }

        /// <summary>The first big owner of a grid, 0 when nobody owns it.</summary>
        public static long Owner(MyCubeGrid grid) => grid?.BigOwners != null && grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;

        /// <summary>The player piloting the grid, if one is.</summary>
        private static long? Controller(MyCubeGrid grid)
        {
            var controller = grid?.GridSystems?.ControlSystem?.GetController();
            var identity = controller?.Player?.Identity?.IdentityId;
            return identity is long id && id != 0 ? id : (long?)null;
        }

        /// <summary>The player's name, or the id.</summary>
        public static string NameOf(long identityId)
        {
            var identity = MySession.Static?.Players?.TryGetIdentity(identityId);
            return identity?.DisplayName ?? identityId.ToString();
        }

        /// <summary>The steam id of an identity, 0 when it is no player.</summary>
        public static ulong SteamOf(long identityId) =>
            MySession.Static?.Players?.TryGetSteamId(identityId) ?? 0;

        public static string DescribeGrid(MyCubeGrid grid) =>
            grid == null ? "?" : grid.DisplayName + " (" + grid.BlocksCount + " blocks, owner " + NameOf(Owner(grid)) + ")";

        public static string Short(this string text, int max) => text == null || text.Length <= max ? text : text.Substring(0, max) + "…";

        public static bool IsOnline(long identityId) =>
            MySession.Static?.Players?.GetOnlinePlayers().Any(p => p.Identity?.IdentityId == identityId) == true;
    }
}

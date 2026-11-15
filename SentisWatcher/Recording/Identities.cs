using System;
using System.Collections.Concurrent;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
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

        /// <summary>
        /// Whose items an inventory holds: a block's owner (the grid's when the block has none), a character's
        /// player, a bag's owner; 0 for anything else.
        /// </summary>
        public static long InventoryOwner(MyEntity entity)
        {
            switch (entity)
            {
                case MyCubeBlock block: return block.OwnerId != 0 ? block.OwnerId : Owner(block.CubeGrid);
                case MyCharacter character: return character.GetPlayerIdentityId();
                case MyInventoryBagEntity bag: return bag.OwnerIdentityId;
                default: return 0;
            }
        }

        /// <summary>
        /// A player's primary identity has a mapped PlayerId with a nonzero Steam id and SerialId zero,
        /// online or offline. A Steam id alone is not enough: game bots can share one with a nonzero serial,
        /// without being in the explicit IdentityIsNpc set. Unmapped, nobody's and marked NPC identities
        /// remain excluded. The ledger still records their items; only player-only alerts are suppressed.
        /// </summary>
        public static bool IsPlayer(long identityId) => IsPlayer(MySession.Static?.Players, identityId);

        internal static bool IsPlayer(MyPlayerCollection players, long identityId) =>
            identityId != 0 && players != null && !players.IdentityIsNpc(identityId) &&
            players.TryGetPlayerId(identityId, out var playerId) && playerId.SteamId != 0 && playerId.SerialId == 0;

        /// <summary>The first big owner of a grid, 0 when nobody owns it.</summary>
        public static long Owner(MyCubeGrid grid) => grid?.BigOwners != null && grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;

        /// <summary>The player piloting the grid, if one is.</summary>
        private static long? Controller(MyCubeGrid grid)
        {
            var controller = grid?.GridSystems?.ControlSystem?.GetController();
            var identity = controller?.Player?.Identity?.IdentityId;
            return identity is long id && id != 0 ? id : (long?)null;
        }

        /// <summary>A grid's pilot this old is asked again when it shoots (<see cref="DamageIdentity"/>).</summary>
        internal const long PilotFreshMs = 1000;

        /// <summary>
        /// A hit reaches its target after its shot (a bullet flies seconds): the pilot of the shooter's grid known that
        /// long is the one who fired, and the hit takes it without asking the grid.
        /// </summary>
        internal const long PilotOfHitMs = 30000;

        private sealed class Crew
        {
            public long Pilot, At;
        }

        // grid id -> its pilot when last asked; any thread (the clusters' shoot and damage in theirs)
        private static readonly ConcurrentDictionary<long, Crew> Crews = new ConcurrentDictionary<long, Crew>();
        private static long _crewsPruned;

        /// <summary>
        /// Combat credit: occupied controlling/main cockpit, otherwise the firing block's builder. Never its owner.
        /// The grid's pilot is kept for <paramref name="freshMs"/>: every shot and every hit ask it, and a grid nobody
        /// controls was asked by walking its seats.
        /// </summary>
        internal static long DamageIdentity(MyEntity entity, out string attribution, long freshMs = PilotFreshMs)
        {
            var grid = (entity as MyCubeBlock)?.CubeGrid ?? entity as MyCubeGrid;
            if (grid != null)
            {
                var pilot = PilotOf(grid, freshMs);
                var builder = (entity as MyCubeBlock)?.BuiltBy ?? 0;
                attribution = pilot != 0 ? "cockpit" : builder != 0 ? "builder" : "unknown";
                return CombatCredit.Select(pilot, builder);
            }
            var identity = OfEntity(entity);
            attribution = identity != 0 ? "character" : "unknown";
            return identity;
        }

        private static long PilotOf(MyCubeGrid grid, long freshMs)
        {
            var now = Storage.Clock.Now;
            Crews.TryGetValue(grid.EntityId, out var known);
            if (known != null && now - known.At <= freshMs && now >= known.At) return known.Pilot;
            long pilot;
            try
            {
                pilot = AskPilot(grid);
            }
            catch (InvalidOperationException) when (known != null)
            {
                // a seat taken or left on another thread while its set was read: the last answer
                return known.Pilot;
            }
            Crews[grid.EntityId] = new Crew { Pilot = pilot, At = now };
            return pilot;
        }

        /// <summary>The pilot of the controlling cockpit, else of the occupied seat (main first, then the lowest id).</summary>
        private static long AskPilot(MyCubeGrid grid)
        {
            var controlled = grid.GridSystems?.ControlSystem?.GetController()?.ControlledEntity as MyCockpit;
            if (controlled?.Pilot != null) return controlled.Pilot.GetPlayerIdentityId();
            // the grid's own set of seats with someone in them (MyCockpit registers itself as its pilot sits and leaves):
            // its few seats, not all of its blocks
            MyCockpit best = null;
            foreach (var seat in grid.OccupiedBlocks)
            {
                if (seat?.Pilot == null) continue;
                if (best == null || seat.IsMainCockpit && !best.IsMainCockpit ||
                    seat.IsMainCockpit == best.IsMainCockpit && seat.EntityId < best.EntityId)
                    best = seat;
            }
            return best?.Pilot?.GetPlayerIdentityId() ?? 0;
        }

        /// <summary>The grids' pilots not asked for a minute forgotten (closed grids among them). Game thread, every frame.</summary>
        internal static void PruneCrews()
        {
            var now = Storage.Clock.Now;
            if (now - _crewsPruned < 60000) return;
            _crewsPruned = now;
            foreach (var pair in Crews)
                if (now - pair.Value.At > 60000) Crews.TryRemove(pair.Key, out _);
        }

        internal static void ResetCrews() => Crews.Clear();

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

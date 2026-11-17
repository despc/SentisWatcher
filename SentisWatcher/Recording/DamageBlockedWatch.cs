using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisWatcher.Storage;
using VRage.Game.ObjectBuilders.Components;
using VRageMath;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// A grid shot at that takes no damage, out of any safe zone and out of SentisGameplayImprovements' PvE zone: event
    /// <c>damage_blocked</c>, a warning and an alert.
    ///
    /// The rounds' and missiles' direct hits of an enemy's or a neutral's grid (<c>round_hit</c>, the accuracy's) are counted
    /// per grid over <see cref="WindowMs"/>; at least <see cref="MinHits"/> of them and no damage to the grid in that time
    /// (the game's damage system, explosions and rams too), with damage allowed where the grid is (no safe zone forbids
    /// it, the grid not protected by SGI's PvE zone - PvECore.IsProtected - where players do no damage to grids), is told
    /// with why:
    /// - the world's blocks indestructible (DestructibleBlocks of the session);
    /// - the grid's DestructibleBlocks off or the grid Immune (an admin, a plugin, a scenario) - the game's bullets, missiles,
    ///   explosions and rams then stop before the block;
    /// - the grid's damage modifier (GridGeneralDamageModifier, its own or its root's) at 0;
    /// - its blocks' damage modifier (BlockGeneralDamageModifier) at 0;
    /// - a damage handler (a mod, a plugin: MyDamageSystem's before-damage handlers) making the damage 0 - Watcher's own
    ///   handler is the last one and sees what the others left;
    /// - none of those, the hits never reaching a block's damage: something in between (a plugin's patch, a mod).
    /// Each grid at most once in <see cref="RepeatMs"/> (the alert once an hour, as every alert).
    /// </summary>
    public static class DamageBlockedWatch
    {
        public const long WindowMs = 10000;
        public const int MinHits = 5;
        public const long RepeatMs = 10 * 60 * 1000;
        private const int MaxAttackers = 8;

        private static readonly Logger Log = LogManager.GetLogger("SentisWatcher.DamageBlocked");
        private static Recorder R => Recorder.Current;

        private sealed class Watch
        {
            public long First;
            public int Hits, Reached, Zeroed, BlockModifierZero;
            public double Damage;
            public Vector3D At;
            public readonly List<long> Attackers = new List<long>();
            public readonly HashSet<string> Weapons = new HashSet<string>();
        }

        private static readonly object Lock = new object();
        private static readonly Dictionary<long, Watch> Grids = new Dictionary<long, Watch>();
        private static readonly Dictionary<long, long> Told = new Dictionary<long, long>();
        private static long _lastCheck;
        // grids watched now: the damage's hot path looks at it before taking the lock
        private static volatile int _watched;

        /// <summary>A round's or a missile's direct hit of an enemy's or a neutral's grid (any thread).</summary>
        internal static void Hit(MyCubeGrid grid, long attacker, string weapon, Vector3D at)
        {
            if (grid == null || grid.IsPreview) return;
            var now = Clock.Now;
            lock (Lock)
            {
                if (!Grids.TryGetValue(grid.EntityId, out var watch))
                {
                    Grids[grid.EntityId] = watch = new Watch { First = now };
                    _watched = Grids.Count;
                }
                watch.Hits++;
                watch.At = at;
                if (attacker != 0 && watch.Attackers.Count < MaxAttackers && !watch.Attackers.Contains(attacker)) watch.Attackers.Add(attacker);
                if (weapon != null && watch.Weapons.Count < MaxAttackers) watch.Weapons.Add(weapon);
            }
        }

        /// <summary>Damage the grid took (the game's after-damage handler; any thread).</summary>
        internal static void Damaged(MyCubeGrid grid, double amount)
        {
            if (grid == null || amount <= 0 || _watched == 0) return;
            lock (Lock)
                if (Grids.TryGetValue(grid.EntityId, out var watch)) watch.Damage += amount;
        }

        /// <summary>
        /// A block's damage after every other before-damage handler (Watcher's own is the last): reached it, at 0 by the
        /// block's modifier or by a handler.
        /// </summary>
        internal static void Reached(MySlimBlock block, float amount)
        {
            if (_watched == 0) return;
            var grid = block?.CubeGrid;
            if (grid == null) return;
            lock (Lock)
            {
                if (!Grids.TryGetValue(grid.EntityId, out var watch)) return;
                watch.Reached++;
                if (amount > 0) return;
                // the block's own modifiers make it 0 before the handlers; the grid's is told from the grid
                if (block.BlockGeneralDamageModifier <= 0 || block.BlockDefinition.GeneralDamageMultiplier <= 0) watch.BlockModifierZero++;
                else if (grid.GridGeneralDamageModifier.Value > 0) watch.Zeroed++;
            }
        }

        /// <summary>The windows that ended, told (game thread, from the recorder's flush, once a second at most).</summary>
        internal static void Check(Recorder recorder, bool force = false)
        {
            var now = Clock.Now;
            if (!force && now - _lastCheck < 1000) return;
            _lastCheck = now;
            List<KeyValuePair<long, Watch>> ended;
            lock (Lock)
            {
                if (Grids.Count == 0) return;
                ended = Grids.Where(p => force || now - p.Value.First >= WindowMs).ToList();
                foreach (var p in ended) Grids.Remove(p.Key);
                _watched = Grids.Count;
                if (Told.Count > 4096) Told.Clear();
            }
            // the world going: the windows not over are not told
            if (force) return;
            foreach (var p in ended)
                if (Blocked(p.Value.Hits, p.Value.Damage)) Recorder.Safe("damage blocked", () => Tell(recorder, p.Key, p.Value, now));
        }

        private static Func<MyCubeGrid, bool> _pve;
        private static bool _pveLooked;

        /// <summary>The grid protected by SentisGameplayImprovements' PvE zone (false without SGI).</summary>
        private static bool PveProtected(MyCubeGrid grid)
        {
            if (!_pveLooked)
            {
                _pveLooked = true;
                try
                {
                    var method = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("SentisGameplayImprovements.PveZone.PvECore", false)).FirstOrDefault(t => t != null)
                        ?.GetMethod("IsProtected", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(MyCubeGrid) }, null);
                    if (method != null) _pve = (Func<MyCubeGrid, bool>)Delegate.CreateDelegate(typeof(Func<MyCubeGrid, bool>), method);
                }
                catch (Exception e)
                {
                    Log.Warn(e, "SentisWatcher: SentisGameplayImprovements' PvE zone is not looked at by the damage blocked watch");
                }
            }
            try
            {
                return _pve != null && _pve(grid);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Hits enough and no damage at all.</summary>
        public static bool Blocked(int hits, double damage) => hits >= MinHits && damage <= 0;

        private static void Tell(Recorder recorder, long gridId, Watch watch, long now)
        {
            if (!MyEntities.TryGetEntityById(gridId, out MyCubeGrid grid) || grid.MarkedForClose) return;
            // a safe zone's: the game's own rule, not told
            if (!MySessionComponentSafeZones.IsActionAllowed(grid, MySafeZoneAction.Damage) ||
                !MySessionComponentSafeZones.IsActionAllowed(watch.At, MySafeZoneAction.Damage)) return;
            // SGI's PvE zone: no damage from players to grids there, by its rule
            if (PveProtected(grid)) return;
            lock (Lock)
            {
                if (Told.TryGetValue(gridId, out var last) && now - last < RepeatMs) return;
                Told[gridId] = now;
            }
            var root = Sandbox.Engine.Physics.MyGridPhysicalHierarchy.Static?.GetRoot(grid) ?? grid;
            var why = Reasons(MySession.Static.Settings.DestructibleBlocks, grid.DestructibleBlocks, grid.Immune,
                Math.Min(grid.GridGeneralDamageModifier.Value, root.GridGeneralDamageModifier.Value), watch.Reached, watch.Zeroed, watch.BlockModifierZero, Handlers());
            var owner = Identities.Owner(grid);
            var who = string.Join(", ", watch.Attackers.Select(a => Identities.NameOf(a) + " (" + a + ")"));
            var what = $"\"{grid.DisplayName}\" {grid.EntityId} of {Identities.NameOf(owner)} ({owner}) at {watch.At.X:0} {watch.At.Y:0} {watch.At.Z:0}: " +
                       $"{watch.Hits} hits in {WindowMs / 1000} s, no damage, out of safe zones; {why}; by {(who.Length > 0 ? who : "?")}" +
                       (watch.Weapons.Count > 0 ? " with " + string.Join(", ", watch.Weapons) : "");
            var actor = watch.Attackers.FirstOrDefault();
            recorder.Event("damage_blocked", actor, grid.EntityId, watch.At, watch.Hits, watch.Hits, what);
            Log.Warn("SentisWatcher: a grid shot takes no damage " + what);
            recorder.Alert("damage_blocked", actor, grid.EntityId, what);
        }

        /// <summary>Why a grid shot took no damage, from its state and what reached its blocks' damage.</summary>
        public static string Reasons(bool worldDestructible, bool gridDestructible, bool immune, float gridModifier,
            int reached, int zeroed, int blockModifierZero, string handlers)
        {
            var why = new List<string>();
            if (!worldDestructible) why.Add("the world's blocks are indestructible (DestructibleBlocks of the session)");
            if (!gridDestructible) why.Add("the grid's DestructibleBlocks is off");
            if (immune) why.Add("the grid is Immune");
            if (gridModifier <= 0) why.Add("the grid's damage modifier is " + gridModifier);
            if (blockModifierZero > 0) why.Add(blockModifierZero + " hits on blocks of damage modifier 0");
            if (zeroed > 0) why.Add(zeroed + " hits made 0 by a damage handler (" + (string.IsNullOrEmpty(handlers) ? "none known" : handlers) + ")");
            if (why.Count == 0)
                why.Add(reached == 0 ? "the hits never reached a block's damage (a plugin's patch or a mod in between)" : "the damage did not come through (" + reached + " reached the blocks)");
            return string.Join("; ", why);
        }

        /// <summary>The before-damage handlers but Watcher's: whose they are.</summary>
        private static string Handlers()
        {
            try
            {
                var field = typeof(Sandbox.Game.GameSystems.MyDamageSystem).GetField("m_beforeDamageHandlers",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (!(field?.GetValue(Sandbox.Game.GameSystems.MyDamageSystem.Static) is System.Collections.IEnumerable list)) return null;
                var names = new List<string>();
                foreach (var item in list)
                {
                    var handler = item.GetType().GetProperty("Item2")?.GetValue(item) as Delegate;
                    var type = handler?.Method.DeclaringType;
                    if (type == null || type.FullName?.StartsWith("SentisWatcher.") == true) continue;
                    names.Add((type.DeclaringType ?? type).FullName + "." + handler.Method.Name);
                }
                return string.Join(", ", names.Distinct());
            }
            catch
            {
                return null;
            }
        }

        internal static void Reset()
        {
            lock (Lock)
            {
                Grids.Clear();
                Told.Clear();
                _watched = 0;
            }
        }
    }
}

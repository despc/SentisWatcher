using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Newtonsoft.Json;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using SentisWatcher.Storage;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisWatcher.Recording
{
    public static partial class DamageLog
    {
        // one DoDamageInternal of a block on this thread (they nest: a block's damage damages others); the objects are
        // kept for the thread's next calls, not made for each
        private sealed class DamageCall
        {
            public MySlimBlock Block;
            public DamageContext Context;
            public bool Observed, Consumed;
        }
        [ThreadStatic] private static List<DamageCall> _damageCalls;
        [ThreadStatic] private static int _damageDepth;
        private static DamageCall TopCall => _damageCalls != null && _damageDepth > 0 && _damageDepth <= _damageCalls.Count ? _damageCalls[_damageDepth - 1] : null;
        private sealed class DamageContext
        {
            public string Key;
            public bool Ignored;
            public int Completed;
        }
        private sealed class BlockIdentity
        {
            public readonly string Token = Guid.NewGuid().ToString("N");
            public long HitTime, HitGrid, Used;
            public Vector3D HitPosition;
            public bool Incomplete;
            public List<DamageContext> Pending = new List<DamageContext>();
        }
        private sealed class BlockCapture
        {
            public MyCubeBlockDefinition Definition;
            public string Token, Faction;
            public long Owner, Grid, Time;
            public float Integrity;
            public bool Suppressed, Unlinked;
            public List<DamageContext> Sources;
            public int[] Mounted;
            public Vector3D Position;
        }
        // one ApplyAccumulatedDamage on this thread: its block and what was captured before it (null: nothing to count)
        private struct Applying
        {
            public MySlimBlock Block;
            public BlockCapture Capture;
        }
        [ThreadStatic] private static List<Applying> _applying;
        private sealed class LossBatch
        {
            public BlockCapture Before;
            public long First, Last, Created;
            public bool Unlinked;
            public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<DamageContext> Pending = new List<DamageContext>();
            public float After;
            public bool Destroyed, Damaged;
            public readonly Dictionary<string, long> Lost = new Dictionary<string, long>(StringComparer.Ordinal);
        }

        private sealed class ByReference : IEqualityComparer<MySlimBlock>
        {
            public bool Equals(MySlimBlock x, MySlimBlock y) => ReferenceEquals(x, y);
            public int GetHashCode(MySlimBlock block) => RuntimeHelpers.GetHashCode(block);
        }

        /// <summary>
        /// A damaged block's identity is forgotten this long after its last damage (sooner once its grid is closed or
        /// it is destroyed). Longer than a battle's longest pause (5 min, WebData.Battles): within a battle a block keeps
        /// its token. A dictionary, not a ConditionalWeakTable: a dependent handle for every block ever hit lived as
        /// long as the block, and the collector walks all of them in every collection.
        /// </summary>
        private const long IdentityKeepMs = 10 * 60 * 1000;
        private const long IdentityPruneEveryMs = 30 * 1000;
        private static readonly Dictionary<MySlimBlock, BlockIdentity> _blockIdentities = new Dictionary<MySlimBlock, BlockIdentity>(new ByReference());
        private static readonly List<MySlimBlock> _identitiesGone = new List<MySlimBlock>();
        private static long _identitiesPruned;
        private static readonly Dictionary<(string, long, long, string), LossBatch> _blockLosses = new Dictionary<(string, long, long, string), LossBatch>();
        private static readonly ConcurrentDictionary<string, int?> _componentPrices = new ConcurrentDictionary<string, int?>(StringComparer.Ordinal);
        private static readonly object EconomyLock = new object();
        private static bool _economyPatched, _economyObserver;
        internal static void EconomyObserverRegistered() => _economyObserver = true;

        // under EconomyLock
        private static BlockIdentity IdentityOf(MySlimBlock block)
        {
            if (!_blockIdentities.TryGetValue(block, out var identity)) _blockIdentities[block] = identity = new BlockIdentity();
            identity.Used = Clock.Now;
            return identity;
        }

        /// <summary>The identities of blocks gone or not hit for <see cref="IdentityKeepMs"/> forgotten. Game thread.</summary>
        private static void PruneIdentities()
        {
            var now = Clock.Now;
            if (now - _identitiesPruned < IdentityPruneEveryMs) return;
            _identitiesPruned = now;
            lock (EconomyLock)
            {
                foreach (var pair in _blockIdentities)
                {
                    var block = pair.Key;
                    var grid = block.CubeGrid;
                    if (now - pair.Value.Used > IdentityKeepMs || grid == null || grid.Closed || block.IsDestroyed)
                        _identitiesGone.Add(block);
                }
                foreach (var block in _identitiesGone) _blockIdentities.Remove(block);
                _identitiesGone.Clear();
            }
        }

        /// <summary>A capture failed: coverage becomes partial rather than a false zero; the first failure is logged.</summary>
        private static void EconomyFailed(string what, Exception e)
        {
            if (Interlocked.Increment(ref _economyErrors) == 1) Recorder.Failed(what, e);
        }

        internal static void EconomyTouch(object target, ref MyDamageInformation info)
        {
            if (!(target is MySlimBlock block)) return;
            var call = TopCall;
            // The engine also raises BEFORE-only queries for geometric deformation, even on a just destroyed
            // block. Those are not damage applications and have no matching AFTER callback.
            if (call == null || call.Observed || call.Consumed || !ReferenceEquals(call.Block, block) ||
                info.IsDeformation || block.ComponentStack == null || block.ComponentStack.IsDestroyed) return;
            call.Observed = true;
            if (Recorder.Current == null) return;
            var positive = info.Amount > 0;
            try
            {
                lock (EconomyLock)
                {
                    var identity = IdentityOf(block);
                    if (!positive) return;
                    var context = call.Context = new DamageContext();
                    if (identity.Pending.Count >= 2048) { identity.Pending.Clear(); identity.Incomplete = true; Interlocked.Increment(ref _economyErrors); }
                    identity.Pending.Add(context);
                    identity.HitTime = Clock.Now; identity.HitGrid = block.CubeGrid.EntityId;
                    identity.HitPosition = block.CubeGrid.GridIntegerToWorld(block.Position);
                }
            }
            catch (Exception e)
            {
                EconomyFailed("component loss touch", e);
            }
        }
        private static DamageContext TakeEconomyContext(object target)
        {
            if (!(target is MySlimBlock block)) return null;
            var call = TopCall;
            if (call == null || call.Consumed || !ReferenceEquals(call.Block, block)) return null;
            call.Consumed = true;
            return call.Context;
        }
        private static void DamageCallBefore(MySlimBlock __instance)
        {
            var calls = _damageCalls ?? (_damageCalls = new List<DamageCall>());
            if (_damageDepth >= 64) { ReleaseCalls(0); Interlocked.Increment(ref _economyErrors); }
            if (_damageDepth >= calls.Count) calls.Add(new DamageCall());
            var call = calls[_damageDepth++];
            call.Block = __instance;
            call.Context = null;
            call.Observed = call.Consumed = false;
        }
        private static void DamageCallAfter(MySlimBlock __instance)
        {
            var calls = _damageCalls;
            if (calls == null) return;
            for (var i = Math.Min(_damageDepth, calls.Count) - 1; i >= 0; i--)
                if (ReferenceEquals(calls[i].Block, __instance))
                {
                    ReleaseCalls(i);
                    return;
                }
        }
        // the calls from this depth up ended (an exception may have skipped their own ends): no block or context kept
        private static void ReleaseCalls(int depth)
        {
            var calls = _damageCalls;
            for (var i = depth; i < Math.Min(_damageDepth, calls.Count); i++)
            {
                calls[i].Block = null;
                calls[i].Context = null;
            }
            _damageDepth = depth;
        }
        private static void CompleteEconomyContext(DamageContext context, string key, bool ignored)
        {
            if (context == null) return;
            context.Key = key; context.Ignored = ignored;
            Volatile.Write(ref context.Completed, 1);
        }
        private static int _economyErrors;
        private static long _economyWindow = -1;
        private static int EconomyTracking => _economyPatched && _economyObserver && Volatile.Read(ref _economyErrors) == 0 ? 1 : 0;

        private static void PatchEconomy(PatchContext ctx)
        {
            PatchGuard.Run("SentisWatcher.ComponentLoss", ctx, c =>
            {
                // ApplyAccumulatedDamage is a large class method, not an inlinable property/struct method.
                // It covers both immediate lethal hits and the engine's deferred damage application.
                var method = typeof(MySlimBlock).GetMethod("ApplyAccumulatedDamage", BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(bool), typeof(long) }, null) ?? throw new MissingMethodException("MySlimBlock.ApplyAccumulatedDamage");
                var scope = typeof(MySlimBlock).GetMethod("DoDamageInternal", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new MissingMethodException("MySlimBlock.DoDamageInternal");
                c.GetPattern(scope).Prefixes.Add(typeof(DamageLog).GetMethod(nameof(DamageCallBefore), BindingFlags.NonPublic | BindingFlags.Static));
                c.GetPattern(scope).Suffixes.Add(typeof(DamageLog).GetMethod(nameof(DamageCallAfter), BindingFlags.NonPublic | BindingFlags.Static));
                c.GetPattern(method).Prefixes.Add(typeof(DamageLog).GetMethod(nameof(EconomyBefore), BindingFlags.NonPublic | BindingFlags.Static));
                c.GetPattern(method).Suffixes.Add(typeof(DamageLog).GetMethod(nameof(EconomyAfter), BindingFlags.NonPublic | BindingFlags.Static));
                _economyPatched = true;
            });
        }

        private static (long Owner, string Faction) EconomicOwnership(long blockOwner, long[] gridOwners, Func<long, string> faction)
        {
            if (blockOwner != 0) return (blockOwner, faction(blockOwner));
            var owners = gridOwners.Where(id => id != 0).Distinct().OrderBy(id => id).ToArray();
            if (owners.Length == 0) return (0, "");
            if (owners.Length == 1) return (owners[0], faction(owners[0]));
            var snapshots = owners.Select(id => faction(id)).ToArray();
            // Multiple tied owners from one known faction are still one economic side. Mixed/factionless owners
            // must not give all unowned armor to whichever identity happens to occur first in BigOwners.
            if (snapshots.All(f => !string.IsNullOrEmpty(f)) && snapshots.Select(f => f.Split('|')[0]).Distinct().Count() == 1)
                return (owners[0], snapshots[0]);
            return (0, null);
        }

        private static int MountedComponents(MyComponentStack stack, int index) => stack.IsDestroyed ? 0 : stack.GetGroupInfo(index).MountedCount;

        private static void EconomyBefore(MySlimBlock __instance)
        {
            if (Recorder.Current == null) return;
            try
            {
                var applying = _applying ?? (_applying = new List<Applying>());
                if (applying.Count >= 16) { applying.Clear(); Interlocked.Increment(ref _economyErrors); }
                var index = applying.Count;
                // balances nested no-op/error calls as well as successful captures
                applying.Add(new Applying { Block = __instance });
                if (__instance.AccumulatedDamage <= 0 || __instance.ComponentStack.IsDestroyed) return;
                for (var i = index - 1; i >= 0; i--)
                    if (ReferenceEquals(applying[i].Block, __instance) && applying[i].Capture != null)
                    {
                        applying[i].Capture.Suppressed = true;
                        Interlocked.Increment(ref _economyErrors);
                        break;
                    }
                applying[index] = new Applying { Block = __instance, Capture = Capture(__instance, null) };
            }
            catch (Exception e)
            {
                EconomyFailed("component loss before", e);
            }
        }

        /// <summary>
        /// The block's components and owners now, with the hits seen since its last capture (and <paramref name="hit"/>,
        /// a hit known already: an explosion's taking it away whole).
        /// </summary>
        private static BlockCapture Capture(MySlimBlock block, DamageContext hit)
        {
            var definition = block.BlockDefinition;
            if (definition.Components.Length > 256) throw new InvalidOperationException("Component group capture limit exceeded");
            var mounted = new int[definition.Components.Length];
            for (var i = 0; i < mounted.Length; i++) mounted[i] = MountedComponents(block.ComponentStack, i);
            var grid = block.CubeGrid;
            var ownership = EconomicOwnership(block.OwnerId, grid.BigOwners?.ToArray() ?? new long[0], FactionAt);
            lock (EconomyLock)
            {
                var identity = IdentityOf(block);
                if (hit != null) identity.Pending.Add(hit);
                var observed = identity.HitTime > 0 && Clock.Now - identity.HitTime <= 120000;
                var capture = new BlockCapture
                {
                    Definition = definition, Mounted = mounted, Owner = ownership.Owner, Faction = ownership.Faction,
                    Grid = observed ? identity.HitGrid : grid.EntityId,
                    Position = observed ? identity.HitPosition : grid.GridIntegerToWorld(block.Position),
                    Time = observed ? identity.HitTime : Clock.Now,
                    Integrity = block.Integrity, Token = identity.Token,
                    Sources = identity.Pending, Unlinked = identity.Incomplete || identity.Pending.Count == 0,
                };
                identity.HitTime = 0; identity.Pending = new List<DamageContext>(); identity.Incomplete = false;
                return capture;
            }
        }

        private static void EconomyAfter(MySlimBlock __instance)
        {
            var applying = _applying;
            if (applying == null) return;
            BlockCapture before = null;
            var found = false;
            for (var i = applying.Count - 1; i >= 0; i--)
                if (ReferenceEquals(applying[i].Block, __instance))
                {
                    before = applying[i].Capture;
                    // the calls above it ended without their suffix (an exception): gone with it
                    applying.RemoveRange(i, applying.Count - i);
                    found = true;
                    break;
                }
            if (!found || Recorder.Current == null) return;
            if (before == null || before.Suppressed) return; // reentrant mutations: keep only verified inner losses, never double count
            try
            {
                Lost(__instance, before);
            }
            catch (Exception e)
            {
                EconomyFailed("component loss after", e);
            }
        }

        /// <summary>What the block lost since <paramref name="before"/>; all of it when it is <paramref name="gone"/> (taken away whole).</summary>
        private static void Lost(MySlimBlock __instance, BlockCapture before, bool gone = false)
        {
            if (!ReferenceEquals(before.Definition, __instance.BlockDefinition)) throw new InvalidOperationException("Block definition changed while applying damage");
            var destroyed = gone || __instance.ComponentStack.IsDestroyed || __instance.Integrity <= 0;
            var lost = new Dictionary<string, long>(StringComparer.Ordinal);
            for (var i = 0; i < before.Mounted.Length; i++)
            {
                // GetGroupInfo can expose the top index as one even after complete destruction: explicitly use zero.
                var remaining = destroyed ? 0 : MountedComponents(__instance.ComponentStack, i);
                var count = Math.Max(0, before.Mounted[i] - remaining);
                if (count == 0) continue;
                var id = before.Definition.Components[i].Definition.Id.SubtypeName;
                lost[id] = checked((lost.TryGetValue(id, out var previous) ? previous : 0) + count);
            }
            lock (EconomyLock)
            {
                if (_economyWindow < 0) _economyWindow = Clock.Now;
                var key = (before.Token, before.Grid, before.Owner, before.Faction);
                if (!_blockLosses.TryGetValue(key, out var batch))
                {
                    if (_blockLosses.Count >= 100000) { Interlocked.Increment(ref _economyErrors); return; }
                    _blockLosses[key] = batch = new LossBatch { Before = before, First = before.Time, Created = Clock.Now };
                }
                batch.First = Math.Min(batch.First, before.Time);
                batch.Last = Math.Max(batch.Last, before.Time);
                batch.Unlinked |= before.Unlinked;
                foreach (var source in before.Sources)
                {
                    if (Volatile.Read(ref source.Completed) == 0) batch.Pending.Add(source);
                    else if (!source.Ignored)
                    {
                        if (string.IsNullOrEmpty(source.Key)) batch.Unlinked = true;
                        else batch.Keys.Add(source.Key);
                    }
                }
                before.Sources = null;
                if (batch.Keys.Count + batch.Pending.Count > 4096) { batch.Unlinked = true; batch.Keys.Clear(); batch.Pending.Clear(); Interlocked.Increment(ref _economyErrors); }
                var after = gone ? 0 : __instance.Integrity;
                batch.Damaged |= before.Integrity > after || lost.Count > 0; batch.After = after; batch.Destroyed |= destroyed;
                foreach (var item in lost) batch.Lost[item.Key] = checked((batch.Lost.TryGetValue(item.Key, out var previous) ? previous : 0) + item.Value);
            }
        }

        // Called only by the game-thread flush, never by the HTTP worker or a physics worker.
        private static int? ComponentPrice(string subtype)
        {
            if (_componentPrices.TryGetValue(subtype, out var cached)) return cached;
            if (Sandbox.MySandboxGame.Static?.UpdateThread != Thread.CurrentThread) return null;
            var id = new MyDefinitionId(typeof(MyObjectBuilder_Component), subtype);
            if (!MyDefinitionManager.Static.TryGetDefinition<MyPhysicalItemDefinition>(id, out var definition)) return _componentPrices[subtype] = null;
            int? price = definition.MinimalPricePerUnit >= 0 ? (int?)definition.MinimalPricePerUnit : null;
            if (!price.HasValue)
            {
                var economy = MySession.Static?.GetComponent<MySessionComponentEconomy>();
                var budget = 2048;
                // The native calculator has no cycle guard. Do not let a malformed mod blueprint recurse on the game thread.
                if (economy != null && CompletePriceRecipe(id, new HashSet<MyDefinitionId>(), 0, ref budget))
                {
                    var calculated = economy.GetMinimumItemPrice(id);
                    if (calculated >= 0) price = calculated;
                }
            }
            return _componentPrices[subtype] = price;
        }

        private static bool CompletePriceRecipe(MyDefinitionId id, HashSet<MyDefinitionId> path, int depth, ref int budget)
        {
            if (--budget < 0 || depth > 32 || !path.Add(id)) return false;
            try
            {
                if (!MyDefinitionManager.Static.TryGetDefinition<MyPhysicalItemDefinition>(id, out var item)) return false;
                if (item.MinimalPricePerUnit >= 0) return true;
                if (!MyDefinitionManager.Static.TryGetBlueprintDefinitionByResultId(id, out var recipe) ||
                    recipe.BaseProductionTimeInSeconds < 0 || float.IsNaN(recipe.BaseProductionTimeInSeconds) || float.IsInfinity(recipe.BaseProductionTimeInSeconds) ||
                    !recipe.Results.Any(result => result.Id == id && (double)result.Amount > 0)) return false;
                foreach (var ingredient in recipe.Prerequisites)
                    if ((double)ingredient.Amount < 0 || !CompletePriceRecipe(ingredient.Id, path, depth + 1, ref budget)) return false;
                return MySession.Static.AssemblerEfficiencyMultiplier > 0 && MySession.Static.AssemblerSpeedMultiplier > 0 && MySession.Static.RefinerySpeedMultiplier > 0;
            }
            finally { path.Remove(id); }
        }

        private static bool EconomyBatchReady(LossBatch batch, bool force, long now) => force || now - batch.Created >= 120000 ||
            batch.Pending.All(source => Volatile.Read(ref source.Completed) != 0);

        internal static void FlushEconomy(Recorder recorder, bool force)
        {
            PruneIdentities();
            List<LossBatch> done;
            lock (EconomyLock)
            {
                if (_blockLosses.Count == 0 || !force && Clock.Now - _economyWindow < WindowMs) return;
                var now = Clock.Now;
                var ready = _blockLosses.Where(pair => EconomyBatchReady(pair.Value, force, now)).ToList();
                done = ready.Select(pair => pair.Value).ToList();
                foreach (var pair in ready) _blockLosses.Remove(pair.Key);
                _economyWindow = _blockLosses.Count == 0 ? -1 : now;
            }
            foreach (var batch in done)
            {
                var before = batch.Before;
                var components = batch.Lost.Select(item =>
                {
                    int? price = null;
                    try { price = ComponentPrice(item.Key); }
                    catch (Exception e) { _componentPrices[item.Key] = null; EconomyFailed("component price", e); }
                    return new { id = item.Key, lost = item.Value, unitPrice = price };
                }).ToList();
                foreach (var source in batch.Pending)
                {
                    if (Volatile.Read(ref source.Completed) == 0) batch.Unlinked = true;
                    else if (!source.Ignored)
                    {
                        if (string.IsNullOrEmpty(source.Key)) batch.Unlinked = true;
                        else batch.Keys.Add(source.Key);
                    }
                }
                recorder.Store.Add(new Row(Table.BlockEconomy, batch.Last, batch.Last, batch.First, before.Token, before.Grid,
                    before.Owner, before.Faction, before.Definition.Id.ToString(), before.Integrity, batch.After,
                    batch.Destroyed ? 1 : 0, new LateJson(components), before.Position.X, before.Position.Y, before.Position.Z, EconomyTracking,
                    // the JSON is made by the writer's thread: nothing touches the batch once it is out of _blockLosses
                    batch.Unlinked ? null : new LateJson(batch.Keys), batch.Damaged ? 1 : 0));
            }
        }

        internal static void ResetEconomy()
        {
            lock (EconomyLock)
            {
                _blockLosses.Clear(); _componentPrices.Clear(); _economyWindow = -1; _economyErrors = 0;
                if (_damageCalls != null) ReleaseCalls(0);
                _applying?.Clear();
                _blockIdentities.Clear();
            }
        }
    }
}

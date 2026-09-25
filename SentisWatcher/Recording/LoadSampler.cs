using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SentisWatcher.Storage;
using Torch.API.Plugins;
using Torch.Managers.PatchManager;
using VRage.Collections;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.ModAPI;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// Who the game thread works for: grids (their blocks included), characters, session components, Torch
    /// plugins. Timing each of them every frame would cost in proportion to the world, so only a sampled frame
    /// is timed - one in <see cref="MinGap"/>..<see cref="MaxGap"/>, at random so that the entities updated
    /// every 10th and 100th frame in turns are all met - and over a minute the samples say who is heavy
    /// and who once took a long frame.
    ///
    /// A sampled frame runs the game's own loops over the entities, components and plugins, the same calls
    /// in the same order, with a timestamp around each; any other frame goes the vanilla way, the hooks cost a
    /// flag check. <see cref="WatcherConfig.LoadSampling"/> turns it off and on while the server runs.
    ///
    /// Time is "own": what a component or a plugin spent in the entities it updated is the entities'.
    /// </summary>
    [PatchShim]
    public static class LoadSampler
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public const int MinGap = 30, MaxGap = 90;

        /// <summary>One set of rows this often.</summary>
        public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

        /// <summary>Rows per kind at most a period; the rest are small.</summary>
        private const int TopPerKind = 60;

        public const string Grid = "grid", Character = "character", Component = "component", Plugin = "plugin",
            System = "system", Other = "other", Parallel = "parallel", Total = "total";

        private sealed class Entry
        {
            public string Kind, Name;
            public long Id, Owner;
            public long Ticks, FrameTicks, Max;
            public bool Touched;
        }

        // game thread only
        private static bool _timing;
        private static int _countdown = MinGap;
        private static readonly Random Rng = new Random();
        private static long _nested;
        private static readonly Dictionary<long, Entry> ByEntity = new Dictionary<long, Entry>();
        private static readonly Dictionary<string, Entry> ByName = new Dictionary<string, Entry>();
        private static readonly List<Entry> Touched = new List<Entry>();
        private static int _frames;
        private static long _frameTicks, _frameMax;
        private static DateTime _last = DateTime.UtcNow;

        private static int _burstFrames;

        /// <summary>
        /// Times every frame for the next <paramref name="seconds"/> (sampling on or off): what a rare long frame
        /// is made of. It costs what the sampling costs, in every frame of it.
        /// </summary>
        public static void Burst(int seconds) => _burstFrames = Math.Max(0, Math.Min(seconds, 600)) * 60;

        public static bool Bursting => _burstFrames > 0;

        private static bool Enabled => SentisWatcherPlugin.Config?.LoadSampling == true && Recorder.Current != null;

        // ------------------------------------------------------------------ the hooks

        private static FieldInfo _update, _updateAfter, _update10, _update10Heavy, _update100, _update100Heavy, _sessionComponents;

        public static void Patch(PatchContext ctx)
        {
            PatchGuard.Run("SentisWatcher.Load", ctx, c =>
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var orchestrator = typeof(MyParallelEntityUpdateOrchestrator);
                FieldInfo F(Type t, string name) => t.GetField(name, any) ?? throw new MissingFieldException(t.Name, name);
                MethodInfo M(Type t, string name) => t.GetMethod(name, any, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(t.Name, name);
                MethodInfo Own(string name) => typeof(LoadSampler).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
                _update = F(orchestrator, "m_entitiesForUpdate");
                _updateAfter = F(orchestrator, "m_entitiesForUpdateAfter");
                _update10 = F(orchestrator, "m_entitiesForUpdate10");
                _update10Heavy = F(orchestrator, "m_entitiesForUpdate10Heavy");
                _update100 = F(orchestrator, "m_entitiesForUpdate100");
                _update100Heavy = F(orchestrator, "m_entitiesForUpdate100Heavy");
                _sessionComponents = F(typeof(MySession), "m_sessionComponentsForUpdate");

                // the found fields must be what the loops below take them for
                if (_update.FieldType != typeof(HashSet<MyEntity>) || _updateAfter.FieldType != typeof(HashSet<MyEntity>) ||
                    _update10.FieldType != typeof(MyDistributedTypeUpdater<MyEntity>) || _update100Heavy.FieldType != typeof(MyDistributedTypeUpdater<MyEntity>) ||
                    _sessionComponents.FieldType != typeof(Dictionary<int, SortedSet<MySessionComponentBase>>))
                    throw new InvalidOperationException("the game's update lists are not what LoadSampler knows");

                c.GetPattern(M(orchestrator, "UpdateBeforeSimulation")).Prefixes.Add(Own(nameof(Before1)));
                c.GetPattern(M(orchestrator, "UpdateBeforeSimulation10")).Prefixes.Add(Own(nameof(Before10)));
                c.GetPattern(M(orchestrator, "UpdateBeforeSimulation100")).Prefixes.Add(Own(nameof(Before100)));
                c.GetPattern(M(orchestrator, "UpdateAfterSimulation")).Prefixes.Add(Own(nameof(After1)));
                c.GetPattern(M(orchestrator, "UpdateAfterSimulation10")).Prefixes.Add(Own(nameof(After10)));
                c.GetPattern(M(orchestrator, "UpdateAfterSimulation100")).Prefixes.Add(Own(nameof(After100)));
                c.GetPattern(M(typeof(MySession), "UpdateComponents")).Prefixes.Add(Own(nameof(Components)));
                c.GetPattern(M(typeof(Torch.Managers.PluginManager), "UpdatePlugins")).Prefixes.Add(Own(nameof(Plugins)));

                // what is not a loop over something is timed whole
                Around(c, M(orchestrator, "DispatchOnceBeforeFrame"), nameof(OnceStart), nameof(OnceEnd));
                Around(c, orchestrator.GetMethod("PerformParallelUpdate", any), nameof(ParallelStart), nameof(ParallelEnd));
                Around(c, M(orchestrator, "DispatchSimulate"), nameof(SimulateStart), nameof(SimulateEnd));
                Around(c, M(typeof(Sandbox.Engine.Physics.MyPhysics), "Simulate"), nameof(PhysicsStart), nameof(PhysicsEnd));
                Around(c, M(orchestrator, "ProcessInvokeLater"), nameof(InvokeLaterStart), nameof(InvokeLaterEnd));
                Around(c, M(typeof(MyEntities), "DrainOutstandingEntityInitWork"), nameof(DrainStart), nameof(DrainEnd));
                // the parallel updates, each on whatever worker runs it
                Around(c, orchestrator.GetMethod("ParallelUpdateHandlerBeforeSimulation", any), nameof(ParallelOneStart), nameof(ParallelOneEnd));
                Around(c, orchestrator.GetMethod("ParallelUpdateHandlerAfterSimulation", any), nameof(ParallelOneStart), nameof(ParallelOneEnd));
                Around(c, M(orchestrator, "ApplyChanges"), nameof(ApplyStart), nameof(ApplyEnd));
            });
        }

        private static void Around(PatchContext c, MethodInfo target, string prefix, string suffix)
        {
            if (target == null) throw new MissingMethodException(prefix);
            c.GetPattern(target).Prefixes.Add(typeof(LoadSampler).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic));
            c.GetPattern(target).Suffixes.Add(typeof(LoadSampler).GetMethod(suffix, BindingFlags.Static | BindingFlags.NonPublic));
        }

        // ------------------------------------------------------------------ the frame (from PerfSampler)

        /// <summary>The start of a frame: is it one to time?</summary>
        internal static void FrameStart()
        {
            _timing = false;
            if (_burstFrames > 0)
            {
                // every frame, for a while: to catch what makes a rare long frame
                _burstFrames--;
                _timing = Recorder.Current != null;
                _nested = 0;
                return;
            }
            if (--_countdown > 0) return;
            _countdown = Rng.Next(MinGap, MaxGap + 1);
            if (!Enabled) return;
            _timing = true;
            _nested = 0;
        }

        internal static void FrameEnd(long frameTicks)
        {
            if (!_timing) return;
            _timing = false;
            _frames++;
            _frameTicks += frameTicks;
            ChargeParallel();
            if (frameTicks > _frameMax) _frameMax = frameTicks;
            foreach (var e in Touched)
            {
                e.Ticks += e.FrameTicks;
                if (e.FrameTicks > e.Max) e.Max = e.FrameTicks;
                e.FrameTicks = 0;
                e.Touched = false;
            }
            Touched.Clear();
            if (_flushTo != null)
            {
                var recorder = _flushTo;
                _flushTo = null;
                Flush(recorder);
            }
        }

        private static void Charge(Entry e, long ticks)
        {
            e.FrameTicks += ticks;
            if (e.Touched) return;
            e.Touched = true;
            Touched.Add(e);
        }

        private static Entry Named(string kind, string name)
        {
            var key = kind + "|" + name;
            if (!ByName.TryGetValue(key, out var e)) ByName[key] = e = new Entry { Kind = kind, Name = name };
            return e;
        }

        private static Entry Of(MyEntity entity)
        {
            if (entity is MyCubeBlock block && block.CubeGrid != null) entity = block.CubeGrid;
            if (entity is MyCubeGrid grid)
            {
                if (!ByEntity.TryGetValue(grid.EntityId, out var e)) ByEntity[grid.EntityId] = e = new Entry { Kind = Grid, Id = grid.EntityId, Name = grid.DisplayName };
                return e;
            }
            if (entity is MyCharacter character)
            {
                if (!ByEntity.TryGetValue(character.EntityId, out var e))
                    ByEntity[character.EntityId] = e = new Entry { Kind = Character, Id = character.EntityId, Name = character.DisplayName, Owner = character.GetPlayerIdentityId() };
                return e;
            }
            return Named(Other, entity.GetType().Name);
        }

        /// <summary>Time a call and charge it, less what the timed calls inside it were charged.</summary>
        private static long Begin(out long nested)
        {
            nested = _nested;
            return Stopwatch.GetTimestamp();
        }

        private static void End(Entry e, long started, long nested)
        {
            var elapsed = Stopwatch.GetTimestamp() - started;
            Charge(e, elapsed - (_nested - nested));
            _nested = nested + elapsed;
        }

        // ------------------------------------------------------------------ the entities: vanilla's loops, timed

        private const int B1 = 0, B10 = 1, B100 = 2, A1 = 3, A10 = 4, A100 = 5;

        private static void Update(MyEntity entity, int what)
        {
            var started = Begin(out var nested);
            try
            {
                switch (what)
                {
                    case B1: entity.UpdateBeforeSimulation(); break;
                    case B10: entity.UpdateBeforeSimulation10(); break;
                    case B100: entity.UpdateBeforeSimulation100(); break;
                    case A1: entity.UpdateAfterSimulation(); break;
                    case A10: entity.UpdateAfterSimulation10(); break;
                    case A100: entity.UpdateAfterSimulation100(); break;
                }
            }
            finally
            {
                End(Of(entity), started, nested);
            }
        }

        private static bool Due(MyEntity e, EntityFlags flag) => !e.MarkedForClose && (e.Flags & flag) != 0 && e.InScene;

        private static void Loop(HashSet<MyEntity> set, EntityFlags flag, int what)
        {
            foreach (var e in set)
                if (Due(e, flag)) Update(e, what);
        }

        private static void Loop(MyDistributedTypeUpdater<MyEntity> updater, EntityFlags flag, int what)
        {
            foreach (var e in updater)
                if (Due(e, flag)) Update(e, what);
        }

        private static HashSet<MyEntity> Set(FieldInfo f, object o) => (HashSet<MyEntity>)f.GetValue(o);
        private static MyDistributedTypeUpdater<MyEntity> Distributed(FieldInfo f, object o) => (MyDistributedTypeUpdater<MyEntity>)f.GetValue(o);

        private static bool Before1(object __instance)
        {
            if (!_timing) return true;
            Loop(Set(_update, __instance), EntityFlags.NeedsUpdate, B1);
            return false;
        }

        private static bool Before10(object __instance)
        {
            if (!_timing) return true;
            Loop(Distributed(_update10, __instance), EntityFlags.NeedsUpdate10, B10);
            Loop(Distributed(_update10Heavy, __instance), EntityFlags.NeedsUpdate10, B10);
            return false;
        }

        private static bool Before100(object __instance)
        {
            if (!_timing) return true;
            Loop(Distributed(_update100, __instance), EntityFlags.NeedsUpdate100, B100);
            Loop(Distributed(_update100Heavy, __instance), EntityFlags.NeedsUpdate100, B100);
            return false;
        }

        private static bool After1(object __instance)
        {
            if (!_timing) return true;
            Loop(Set(_update, __instance), EntityFlags.NeedsUpdate, A1);
            Loop(Set(_updateAfter, __instance), EntityFlags.NeedsUpdateAfter, A1);
            return false;
        }

        private static bool After10(object __instance)
        {
            if (!_timing) return true;
            var list = Distributed(_update10, __instance);
            Loop(list, EntityFlags.NeedsUpdate10, A10);
            list.Update();
            var heavy = Distributed(_update10Heavy, __instance);
            Loop(heavy, EntityFlags.NeedsUpdate10, A10);
            heavy.Update();
            return false;
        }

        private static bool After100(object __instance)
        {
            if (!_timing) return true;
            var list = Distributed(_update100, __instance);
            Loop(list, EntityFlags.NeedsUpdate100, A100);
            list.Update();
            var heavy = Distributed(_update100Heavy, __instance);
            Loop(heavy, EntityFlags.NeedsUpdate100, A100);
            heavy.Update();
            return false;
        }

        // ------------------------------------------------------------------ session components and plugins

        private static bool Components(MySession __instance)
        {
            if (!_timing) return true;
            var byStage = (Dictionary<int, SortedSet<MySessionComponentBase>>)_sessionComponents.GetValue(__instance);
            void Stage(int stage, int what)
            {
                if (!byStage.TryGetValue(stage, out var set)) return;
                foreach (var component in set)
                {
                    if (!component.UpdatedBeforeInit() && !Sandbox.MySandboxGame.IsGameReady) continue;
                    var started = Begin(out var nested);
                    try
                    {
                        if (what == 1) component.UpdateBeforeSimulation();
                        else if (what == 2) component.Simulate();
                        else component.UpdateAfterSimulation();
                    }
                    finally
                    {
                        End(Named(Component, ComponentName(component)), started, nested);
                    }
                }
            }
            Stage(1, 1);
            var replication = Sandbox.Engine.Multiplayer.MyMultiplayer.Static?.ReplicationLayer;
            if (replication != null)
            {
                var started = Begin(out var nested);
                try { replication.Simulate(); }
                finally { End(Named(System, "network.simulate"), started, nested); }
            }
            Stage(2, 2);
            Stage(4, 4);
            return false;
        }

        private static readonly Dictionary<Type, string> ComponentNames = new Dictionary<Type, string>();

        /// <summary>The component's type, and the mod it comes from.</summary>
        private static string ComponentName(MySessionComponentBase component)
        {
            var type = component.GetType();
            if (ComponentNames.TryGetValue(type, out var name)) return name;
            name = type.FullName;
            var mod = component.ModContext?.ModName;
            if (!string.IsNullOrEmpty(mod)) name = mod + ": " + type.Name;
            return ComponentNames[type] = name;
        }

        private static bool Plugins(Torch.Managers.PluginManager __instance)
        {
            if (!_timing) return true;
            foreach (var plugin in __instance.Plugins.Values)
            {
                var started = Begin(out var nested);
                try
                {
                    plugin.Update();
                }
                catch (Exception exception)
                {
                    // as Torch has it: a plugin's exception does not stop the others
                    Log.Error(exception, "Plugin " + plugin.Name + " threw an exception during update!");
                }
                finally
                {
                    End(Named(Plugin, plugin.Name), started, nested);
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ parts timed whole

        private static long _onceStart, _onceNested, _parallelStart, _parallelNested, _simulateStart, _simulateNested, _physicsStart, _physicsNested;

        private static void OnceStart() { if (_timing) _onceStart = Begin(out _onceNested); }
        private static void OnceEnd() { if (_timing && _onceStart != 0) End(Named(System, "entities.once_before_frame"), _onceStart, _onceNested); _onceStart = 0; }
        private static void ParallelStart() { if (_timing) _parallelStart = Begin(out _parallelNested); }
        private static void ParallelEnd() { if (_timing && _parallelStart != 0) End(Named(System, "entities.parallel"), _parallelStart, _parallelNested); _parallelStart = 0; }
        private static void SimulateStart() { if (_timing) _simulateStart = Begin(out _simulateNested); }
        private static void SimulateEnd() { if (_timing && _simulateStart != 0) End(Named(System, "entities.simulate"), _simulateStart, _simulateNested); _simulateStart = 0; }
        private static void PhysicsStart() { if (_timing) _physicsStart = Begin(out _physicsNested); }
        private static void PhysicsEnd() { if (_timing && _physicsStart != 0) End(Named(System, "physics"), _physicsStart, _physicsNested); _physicsStart = 0; }

        private static long _drainStart, _drainNested;
        private static void DrainStart() { if (_timing) _drainStart = Begin(out _drainNested); }
        private static void DrainEnd() { if (_timing && _drainStart != 0) End(Named(System, "entities.drain_init_work"), _drainStart, _drainNested); _drainStart = 0; }

        // One parallel update: timed on its worker, charged on the game thread at the end of the frame. Its time
        // is not taken off entities.parallel - the workers run side by side, the game thread waits for the last.
        [ThreadStatic] private static long _oneStart;
        private static readonly object ParallelLock = new object();
        private static readonly List<(IMyParallelUpdateable Entity, long Ticks)> ParallelDone = new List<(IMyParallelUpdateable, long)>();
        private static void ParallelOneStart() { if (_timing) _oneStart = Stopwatch.GetTimestamp(); }
        private static void ParallelOneEnd(IMyParallelUpdateable entity)
        {
            if (!_timing || _oneStart == 0) return;
            var ticks = Stopwatch.GetTimestamp() - _oneStart;
            _oneStart = 0;
            lock (ParallelLock) ParallelDone.Add((entity, ticks));
        }

        private static void ChargeParallel()
        {
            lock (ParallelLock)
            {
                foreach (var (entity, ticks) in ParallelDone)
                {
                    var e = entity is MyEntity my ? Of(my) : Named(Other, entity.GetType().Name);
                    Charge(Named(Parallel, e.Kind == Other ? e.Name : e.Kind + ": " + e.Name), ticks);
                }
                ParallelDone.Clear();
            }
        }

        private static long _invokeStart, _invokeNested, _applyStart, _applyNested;
        private static void InvokeLaterStart() { if (_timing) _invokeStart = Begin(out _invokeNested); }
        private static void InvokeLaterEnd() { if (_timing && _invokeStart != 0) End(Named(System, "entities.invoke_later"), _invokeStart, _invokeNested); _invokeStart = 0; }
        private static void ApplyStart() { if (_timing) _applyStart = Begin(out _applyNested); }
        private static void ApplyEnd() { if (_timing && _applyStart != 0) End(Named(System, "entities.apply_changes"), _applyStart, _applyNested); _applyStart = 0; }

        // ------------------------------------------------------------------ the rows

        /// <summary>Game thread, every frame: every <see cref="Every"/> the rows of the period that ended.</summary>
        public static void Tick(Recorder recorder)
        {
            if (DateTime.UtcNow - _last < Every) return;
            if (_timing) _flushTo = recorder;               // the frame's entries are still open: at its end
            else Flush(recorder);
        }

        private static Recorder _flushTo;

        private static void Flush(Recorder recorder)
        {
            _last = DateTime.UtcNow;
            if (_frames == 0)
            {
                Clear();
                return;
            }
            double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            var t = Clock.Now;
            var frames = _frames;
            recorder.Store.Add(new Row(Table.Load, t, t, Total, 0L, "", 0L, "", frames, Ms(_frameTicks) / frames, Ms(_frameMax)));
            var players = MySession.Static?.Players;
            foreach (var kind in ByEntity.Values.Concat(ByName.Values).GroupBy(e => e.Kind))
                foreach (var e in kind.OrderByDescending(e => e.Ticks).Take(TopPerKind))
                {
                    if (e.Ticks == 0) break;
                    var owner = e.Owner;
                    var name = e.Name;
                    if (e.Kind == Grid && MyEntities.TryGetEntityById(e.Id, out var entity) && entity is MyCubeGrid grid)
                    {
                        name = grid.DisplayName;
                        if (grid.BigOwners.Count > 0) owner = grid.BigOwners[0];
                    }
                    var ownerName = owner != 0 ? players?.TryGetIdentity(owner)?.DisplayName ?? "" : "";
                    recorder.Store.Add(new Row(Table.Load, t, t, e.Kind, e.Id, name ?? "", owner, ownerName, frames, Ms(e.Ticks) / frames, Ms(e.Max)));
                }
            Clear();
        }

        private static void Clear()
        {
            _frames = 0;
            _frameTicks = _frameMax = 0;
            ByEntity.Clear();
            ByName.Clear();
        }
    }
}

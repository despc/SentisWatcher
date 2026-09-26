using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using SentisWatcher.Storage;
using Torch.Managers.PatchManager;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// How hard the server works, every <see cref="Every"/> into the table perf, at a fixed cost whatever the
    /// world holds:
    ///  * the frame of the game thread (Game.UpdateInternal) and its parts, each timed where it starts and ends and
    ///    counted without the timed parts inside it (the entities run inside a session component, the physics is one):
    ///    the physics, the entities before and after it (grids, blocks, characters), the game logic components (mods'
    ///    scripts and some blocks'), the other session components, the replication, the network packets, the calls
    ///    queued from other threads (Torch, plugins), the finished background tasks, Torch's plugins, the save's
    ///    snapshot; "other" is what none of them took. Two timestamps at each, a few dozen a frame - not the game's
    ///    own simple profiler, which times every grid;
    ///  * the garbage collector's counts, and its share of the time (the ".NET CLR Memory" performance
    ///    counter), and the memory, read once a second on a thread of their own.
    /// </summary>
    [PatchShim]
    public static class PerfSampler
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>One row this often.</summary>
        public static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

        /// <summary>The rest of the frame, as the blocks column names it.</summary>
        public const string Other = "other";

        private enum Part
        {
            Physics = 0,
            EntitiesBefore = 1,
            EntitiesAfter = 2,
            GameLogic = 3,
            Session = 4,
            Replication = 5,
            Network = 6,
            Invoke = 7,
            Callbacks = 8,
            Plugins = 9,
            Save = 10,
            Torch = 11,
            GameLoop = 12,
            SessionOwn = 13,
            Count
        }

        /// <summary>The parts as the blocks column names them (the physics has columns of its own too).</summary>
        private static readonly string[] PartKeys = { "physics", "entities_before", "entities_after", "game_logic", "session", "replication", "network", "invoke", "callbacks", "plugins", "save", "torch", "game_loop", "session_own" };

        private sealed class Acc
        {
            public long Ticks, Max;
            public int Count;
            public void Add(long ticks) { Ticks += ticks; Count++; if (ticks > Max) Max = ticks; }
            public void Clear() { Ticks = Max = 0; Count = 0; }
        }

        // game thread only
        private static readonly Acc FrameAcc = new Acc(), OtherAcc = new Acc();
        private static readonly Acc[] PartAcc = Enumerable.Range(0, (int)Part.Count).Select(_ => new Acc()).ToArray();
        private static readonly long[] PartFrame = new long[(int)Part.Count];      // this frame's own time so far
        private static long _frameStart;
        private static Thread _gameThread;

        // the timed calls open now, innermost last: when each started and what the timed calls inside it took
        private const int MaxDepth = 32;
        private static readonly long[] OpenStart = new long[MaxDepth], OpenInner = new long[MaxDepth];
        private static int _depth;

        public static void Patch(PatchContext ctx)
        {
            PatchGuard.Run("SentisWatcher.Perf", ctx, c =>
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo M(Type t, string name) => t.GetMethod(name, any, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(t.Name + "." + name);
                MethodInfo Own(string name) => typeof(PerfSampler).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
                void Around(MethodInfo target, string prefix, string suffix)
                {
                    c.GetPattern(target).Prefixes.Add(Own(prefix));
                    c.GetPattern(target).Suffixes.Add(Own(suffix));
                }
                Around(M(typeof(Sandbox.Engine.Platform.Game), "UpdateInternal"), nameof(FrameStart), nameof(FrameEnd));
                Around(M(typeof(Sandbox.Engine.Physics.MyPhysics), "Simulate"), nameof(PhysicsStart), nameof(PhysicsEnd));
                Around(M(typeof(Sandbox.Game.Entities.MyEntities), "UpdateBeforeSimulation"), nameof(EntitiesBeforeStart), nameof(EntitiesBeforeEnd));
                Around(M(typeof(Sandbox.Game.Entities.MyEntities), "UpdateAfterSimulation"), nameof(EntitiesAfterStart), nameof(EntitiesAfterEnd));
                Around(M(typeof(Sandbox.Game.World.MySession), "UpdateComponents"), nameof(SessionStart), nameof(SessionEnd));

                // the rest where the game has it (a part that is not found stays in "other")
                Type T(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
                void Optional(string type, string method, string prefix, string suffix, Type[] args = null)
                {
                    MethodInfo target = null;
                    var owner = T(type);
                    if (owner != null && args != null) target = owner.GetMethod(method, any | BindingFlags.DeclaredOnly, null, args, null);
                    else if (owner != null)
                    {
                        var all = owner.GetMethods(any | BindingFlags.DeclaredOnly).Where(m => m.Name == method && !m.IsAbstract).ToList();
                        if (all.Count == 1) target = all[0];
                    }
                    if (target == null) { Log.Warn("SentisWatcher: no " + type + "." + method + " to time; it stays in the rest of the frame"); return; }
                    Around(target, prefix, suffix);
                }
                foreach (var logic in new[] { "VRage.Game.Components.MyGameLogic", "Sandbox.Game.Entities.MyGameLogic" })
                    if (T(logic) != null)
                    {
                        Optional(logic, "UpdateBeforeSimulation", nameof(GameLogicStart), nameof(GameLogicEnd), Type.EmptyTypes);
                        Optional(logic, "UpdateAfterSimulation", nameof(GameLogicStart), nameof(GameLogicEnd), Type.EmptyTypes);
                        break;
                    }
                Optional("VRage.Network.MyReplicationServer", "UpdateBefore", nameof(ReplicationStart), nameof(ReplicationEnd));
                Optional("VRage.Network.MyReplicationServer", "UpdateAfter", nameof(ReplicationStart), nameof(ReplicationEnd));
                Optional("VRage.Network.MyReplicationServer", "UpdateClientStateGroups", nameof(ReplicationStart), nameof(ReplicationEnd));
                Optional("Sandbox.Engine.Multiplayer.MyDedicatedServerBase", "Tick", nameof(ReplicationStart), nameof(ReplicationEnd));
                Optional("Sandbox.Engine.Networking.MyNetworkReader", "Process", nameof(NetworkStart), nameof(NetworkEnd));
                Optional("Sandbox.Engine.Multiplayer.MyTransportLayer", "Tick", nameof(NetworkStart), nameof(NetworkEnd));
                Optional("Sandbox.Engine.Networking.MyGameService", "Update", nameof(NetworkStart), nameof(NetworkEnd));
                Optional("Sandbox.MySandboxGame", "ProcessInvoke", nameof(InvokeStart), nameof(InvokeEnd));
                Optional("ParallelTasks.Parallel", "RunCallbacks", nameof(CallbacksStart), nameof(CallbacksEnd));
                Optional("Torch.Managers.PluginManager", "UpdatePlugins", nameof(PluginsStart), nameof(PluginsEnd));
                // what was "other": Torch's own frame work (its view models, its collections) without the plugins; the
                // game loop's own (the platform's memory reads, the stats, the GUI and input, the network monitor);
                // the session's own (the GPSs, the block limits sent, the ownership requests)
                Optional("Torch.TorchBase", "Update", nameof(TorchStart), nameof(TorchEnd), Type.EmptyTypes);
                Optional("Torch.Server.TorchServer", "Update", nameof(TorchStart), nameof(TorchEnd), Type.EmptyTypes);
                Optional("Sandbox.MySandboxGame", "Update", nameof(GameLoopStart), nameof(GameLoopEnd), Type.EmptyTypes);
                var timeSpan = T("VRage.Library.Utils.MyTimeSpan");
                if (timeSpan != null)
                    Optional("Sandbox.Game.World.MySession", "Update", nameof(SessionOwnStart), nameof(SessionOwnEnd), new[] { timeSpan });
                // SentisOptimisations builds the frozen grids of a save over the frames before it: counted with the save
                if (T("SentisOptimisationsPlugin.Freezer.FrozenGridSaveCache") != null)
                    Optional("SentisOptimisationsPlugin.Freezer.FrozenGridSaveCache", "FrameSuffix", nameof(SaveStart), nameof(SaveEnd), Type.EmptyTypes);
                var snapshot = T("Sandbox.Game.World.MySessionSnapshot");
                var progress = T("Sandbox.Game.World.SaveProgress");
                if (snapshot != null && progress != null)
                    Optional("Sandbox.Game.World.MySession", "Save", nameof(SaveStart), nameof(SaveEnd),
                        new[] { snapshot.MakeByRefType(), typeof(string), typeof(Action<>).MakeGenericType(progress) });
            });
        }

        private static void FrameStart()
        {
            _frameStart = Stopwatch.GetTimestamp();
            _gameThread = Thread.CurrentThread;
            _depth = 0;
            Array.Clear(PartFrame, 0, PartFrame.Length);
            LoadSampler.FrameStart();
        }

        private static void FrameEnd()
        {
            if (_frameStart == 0) return;
            var frame = Stopwatch.GetTimestamp() - _frameStart;
            FrameAcc.Add(frame);
            var parts = 0L;
            for (var i = 0; i < PartAcc.Length; i++)
            {
                PartAcc[i].Add(PartFrame[i]);
                parts += PartFrame[i];
            }
            OtherAcc.Add(Math.Max(0, frame - parts));
            _frameStart = 0;
            LoadSampler.FrameEnd(frame);
        }

        /// <summary>A timed call begins (game thread, inside a frame; anything else is not counted).</summary>
        private static void Open()
        {
            if (_frameStart == 0 || Thread.CurrentThread != _gameThread) return;
            if (_depth < MaxDepth)
            {
                OpenStart[_depth] = Stopwatch.GetTimestamp();
                OpenInner[_depth] = 0;
            }
            _depth++;
        }

        /// <summary>A timed call ends: its time less the timed calls inside it goes to its part, all of it to its caller's inner time.</summary>
        private static void Close(Part part)
        {
            if (_frameStart == 0 || Thread.CurrentThread != _gameThread || _depth == 0) return;
            _depth--;
            if (_depth >= MaxDepth) return;
            var elapsed = Stopwatch.GetTimestamp() - OpenStart[_depth];
            PartFrame[(int)part] += Math.Max(0, elapsed - OpenInner[_depth]);
            if (_depth > 0) OpenInner[_depth - 1] += elapsed;
        }

        private static void PhysicsStart() => Open();
        private static void PhysicsEnd() => Close(Part.Physics);
        private static void EntitiesBeforeStart() => Open();
        private static void EntitiesBeforeEnd() => Close(Part.EntitiesBefore);
        private static void EntitiesAfterStart() => Open();
        private static void EntitiesAfterEnd() => Close(Part.EntitiesAfter);
        private static void GameLogicStart() => Open();
        private static void GameLogicEnd() => Close(Part.GameLogic);
        private static void SessionStart() => Open();
        private static void SessionEnd() => Close(Part.Session);
        private static void ReplicationStart() => Open();
        private static void ReplicationEnd() => Close(Part.Replication);
        private static void NetworkStart() => Open();
        private static void NetworkEnd() => Close(Part.Network);
        private static void InvokeStart() => Open();
        private static void InvokeEnd() => Close(Part.Invoke);
        private static void CallbacksStart() => Open();
        private static void CallbacksEnd() => Close(Part.Callbacks);
        private static void PluginsStart() => Open();
        private static void PluginsEnd() => Close(Part.Plugins);
        private static void SaveStart() => Open();
        private static void SaveEnd() => Close(Part.Save);
        private static void TorchStart() => Open();
        private static void TorchEnd() => Close(Part.Torch);
        private static void GameLoopStart() => Open();
        private static void GameLoopEnd() => Close(Part.GameLoop);
        private static void SessionOwnStart() => Open();
        private static void SessionOwnEnd() => Close(Part.SessionOwn);

        // ------------------------------------------------------------------ gc and memory, off the game thread

        private static Timer _timer;
        private static PerformanceCounter _gcTime;
        private static readonly object Lock = new object();
        private static double _gcTimeSum, _managedSum, _privateSum, _workingSum;
        private static int _samples;
        private static bool _counterFailed;

        public static void Start()
        {
            for (var g = 0; g < 3; g++) GcCounts[g] = GC.CollectionCount(g);
            _last = DateTime.UtcNow;
            _timer = new Timer(_ => SampleMemory(), null, 1000, 1000);
        }

        public static void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            _gcTime?.Dispose();
            _gcTime = null;
        }

        private static void SampleMemory()
        {
            try
            {
                var gc = GcTime();
                // the memory of this process from the system directly: Process.PrivateMemorySize64 reads the
                // information of every process on the machine, thousands of objects a second
                var counters = new ProcessMemoryCounters { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(ProcessMemoryCounters)) };
                GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size);
                lock (Lock)
                {
                    _gcTimeSum += gc;
                    _managedSum += GC.GetTotalMemory(false) / 1048576.0;
                    _privateSum += counters.PrivateUsage.ToUInt64() / 1048576.0;
                    _workingSum += counters.WorkingSetSize.ToUInt64() / 1048576.0;
                    _samples++;
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "SentisWatcher: memory sample failed");
            }
        }

        /// <summary>
        /// The share of the time the collector took since the last sample, from the process's own ".NET CLR
        /// Memory" counter instance (found by its process id: several servers on one machine have #1, #2...).
        /// </summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint Size, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private static readonly int ProcessId = Process.GetCurrentProcess().Id;

        private static double GcTime()
        {
            if (_counterFailed) return 0;
            try
            {
                if (_gcTime == null)
                {
                    var category = new PerformanceCounterCategory(".NET CLR Memory");
                    string instance = null;
                    foreach (var name in category.GetInstanceNames())
                        using (var pid = new PerformanceCounter(".NET CLR Memory", "Process ID", name, true))
                            if ((int)pid.RawValue == ProcessId) { instance = name; break; }
                    if (instance == null) { _counterFailed = true; Log.Warn("SentisWatcher: no .NET CLR Memory counter for this process; the time in GC is not recorded"); return 0; }
                    _gcTime = new PerformanceCounter(".NET CLR Memory", "% Time in GC", instance, true);
                    _gcTime.NextValue();
                }
                return _gcTime.NextValue();
            }
            catch (Exception e)
            {
                _counterFailed = true;
                Log.Warn(e, "SentisWatcher: the .NET CLR Memory counters cannot be read; the time in GC is not recorded");
                return 0;
            }
        }

        // ------------------------------------------------------------------ the row

        private static DateTime _last = DateTime.UtcNow;
        private static readonly int[] GcCounts = new int[3];

        /// <summary>Game thread, every frame: every <see cref="Every"/> the row of the period that ended.</summary>
        public static void Tick(Recorder recorder)
        {
            var now = DateTime.UtcNow;
            if (now - _last < Every) return;
            _last = now;

            double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            double Avg(Acc a) => a.Count == 0 ? 0 : Ms(a.Ticks) / a.Count;
            double Max(Acc a) => Ms(a.Max);
            var frames = FrameAcc.Count;
            var physics = PartAcc[(int)Part.Physics];
            var blocks = string.Join(";", Enumerable.Range(0, PartAcc.Length).Where(i => i != (int)Part.Physics)
                .Select(i => (PartKeys[i], PartAcc[i])).Concat(new[] { (Other, OtherAcc) })
                .Select(p => p.Item1 + ":" + F(Avg(p.Item2)) + ":" + F(Max(p.Item2))));
            var frameAvg = Avg(FrameAcc); var frameMax = Max(FrameAcc); var physicsAvg = Avg(physics); var physicsMax = Max(physics);
            FrameAcc.Clear();
            OtherAcc.Clear();
            foreach (var acc in PartAcc) acc.Clear();

            var gc = new int[3];
            for (var g = 0; g < 3; g++)
            {
                var count = GC.CollectionCount(g);
                gc[g] = count - GcCounts[g];
                GcCounts[g] = count;
            }
            double gcTime, managed, priv, working;
            lock (Lock)
            {
                var n = Math.Max(1, _samples);
                gcTime = _gcTimeSum / n; managed = _managedSum / n; priv = _privateSum / n; working = _workingSum / n;
                _gcTimeSum = _managedSum = _privateSum = _workingSum = 0;
                _samples = 0;
            }
            var sim = (double)Sandbox.Game.Multiplayer.Sync.ServerSimulationRatio;
            // the animals have players of their own: not counted
            var players = Sandbox.Game.World.MySession.Static?.Players?.GetOnlinePlayers().Count(p => !Wildlife.IsAnimal(p)) ?? 0;

            // the server settling after the world loaded: counted and dropped, not charted
            if (!Warmup.Over) return;
            recorder.Store.Add(new Row(Table.Perf, Clock.Now, Clock.Now, frames,
                frameAvg, frameMax, physicsAvg, physicsMax,
                gc[0], gc[1], gc[2], gcTime, managed, priv, working, sim, players, blocks));
        }

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

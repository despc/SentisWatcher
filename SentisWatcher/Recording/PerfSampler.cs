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
    ///  * the frame of the game thread and its big parts timed where they start and end - the frame
    ///    (Game.UpdateInternal), the physics (MyPhysics.Simulate), the entities before and after it
    ///    (MyEntities.UpdateBeforeSimulation, UpdateAfterSimulation: grids, blocks, characters); the rest of the
    ///    frame (session components, mods, the network) is what is left. Two timestamps at each, a handful a
    ///    frame - not the game's own simple profiler, which times every grid (SentisOptimisations switches it
    ///    off for that);
    ///  * the garbage collector's counts, and its share of the time (the ".NET CLR Memory" performance
    ///    counter), and the memory, read once a second on a thread of their own.
    /// </summary>
    [PatchShim]
    public static class PerfSampler
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>One row this often.</summary>
        public static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

        /// <summary>The parts of the frame besides the physics, as the blocks column names them.</summary>
        public const string EntitiesBefore = "entities_before", EntitiesAfter = "entities_after", Other = "other";

        private sealed class Acc
        {
            public long Ticks, Max;
            public int Count;
            public void Add(long ticks) { Ticks += ticks; Count++; if (ticks > Max) Max = ticks; }
            public void Clear() { Ticks = Max = 0; Count = 0; }
        }

        // game thread only
        private static readonly Acc FrameAcc = new Acc(), PhysicsAcc = new Acc(), BeforeAcc = new Acc(), AfterAcc = new Acc(), OtherAcc = new Acc();
        private static long _frameStart, _physicsStart, _beforeStart, _afterStart;
        private static long _physicsFrame, _beforeFrame, _afterFrame;       // this frame's so far

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
                Around(M(typeof(Sandbox.Game.Entities.MyEntities), "UpdateBeforeSimulation"), nameof(BeforeStart), nameof(BeforeEnd));
                Around(M(typeof(Sandbox.Game.Entities.MyEntities), "UpdateAfterSimulation"), nameof(AfterStart), nameof(AfterEnd));
            });
        }

        private static void FrameStart() { _frameStart = Stopwatch.GetTimestamp(); _physicsFrame = _beforeFrame = _afterFrame = 0; LoadSampler.FrameStart(); }
        private static void FrameEnd()
        {
            if (_frameStart == 0) return;
            var frame = Stopwatch.GetTimestamp() - _frameStart;
            FrameAcc.Add(frame);
            PhysicsAcc.Add(_physicsFrame);
            BeforeAcc.Add(_beforeFrame);
            AfterAcc.Add(_afterFrame);
            OtherAcc.Add(Math.Max(0, frame - _physicsFrame - _beforeFrame - _afterFrame));
            LoadSampler.FrameEnd(frame);
        }
        private static void PhysicsStart() => _physicsStart = Stopwatch.GetTimestamp();
        private static void PhysicsEnd() { if (_physicsStart != 0) _physicsFrame += Stopwatch.GetTimestamp() - _physicsStart; }
        private static void BeforeStart() => _beforeStart = Stopwatch.GetTimestamp();
        private static void BeforeEnd() { if (_beforeStart != 0) _beforeFrame += Stopwatch.GetTimestamp() - _beforeStart; }
        private static void AfterStart() => _afterStart = Stopwatch.GetTimestamp();
        private static void AfterEnd() { if (_afterStart != 0) _afterFrame += Stopwatch.GetTimestamp() - _afterStart; }

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
            var blocks = string.Join(";", new[] { (EntitiesBefore, BeforeAcc), (EntitiesAfter, AfterAcc), (Other, OtherAcc) }
                .Select(p => p.Item1 + ":" + F(Avg(p.Item2)) + ":" + F(Max(p.Item2))));
            var frameAvg = Avg(FrameAcc); var frameMax = Max(FrameAcc); var physicsAvg = Avg(PhysicsAcc); var physicsMax = Max(PhysicsAcc);
            foreach (var acc in new[] { FrameAcc, PhysicsAcc, BeforeAcc, AfterAcc, OtherAcc }) acc.Clear();

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
            var players = Sandbox.Game.World.MySession.Static?.Players?.GetOnlinePlayerCount() ?? 0;

            recorder.Store.Add(new Row(Table.Perf, Clock.Now, Clock.Now, frames,
                frameAvg, frameMax, physicsAvg, physicsMax,
                gc[0], gc[1], gc[2], gcTime, managed, priv, working, sim, players, blocks));
        }

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

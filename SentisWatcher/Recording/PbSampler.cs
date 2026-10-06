using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using SentisWatcher.Storage;
using Torch.Managers.PatchManager;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// What the players' scripts cost: every run of every programmable block timed (two timestamps around the game's
    /// own call of the script - its Main, its Save, its constructor), and once a minute a row per block into the table
    /// load (kind "pb"): its time in an average frame of the minute, its longest run, the block's and its grid's name,
    /// its owner. Every run, not a sample: a script that runs once in a hundred frames is what a sample misses.
    /// </summary>
    [PatchShim]
    public static class PbSampler
    {
        public const string Kind = "pb";

        /// <summary>One set of rows this often (as LoadSampler's: they are charted together).</summary>
        public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

        private sealed class Entry
        {
            public long Ticks, Max;
            public int Runs;
            public MyProgrammableBlock Block;
        }

        private static readonly Dictionary<long, Entry> ByBlock = new Dictionary<long, Entry>();
        private static readonly object Lock = new object();
        [ThreadStatic] private static long _started;
        private static DateTime _last = DateTime.UtcNow;
        private static int _frames;

        public static void Patch(PatchContext ctx)
        {
            PatchGuard.Run("SentisWatcher.Pb", ctx, c =>
            {
                // every way into a script goes through it (Run, Save, the constructor); it returns an enum - safe to patch.
                // The core, not RunSandboxedProgramAction around it: SentisOptimisations replaces that one whole and calls
                // the core itself - a patch on the outer method never ran with it installed.
                var run = typeof(MyProgrammableBlock).GetMethod("RunSandboxedProgramActionCore", BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new MissingMethodException("MyProgrammableBlock.RunSandboxedProgramActionCore");
                c.GetPattern(run).Prefixes.Add(typeof(PbSampler).GetMethod(nameof(RunPrefix), BindingFlags.Static | BindingFlags.NonPublic));
                c.GetPattern(run).Suffixes.Add(typeof(PbSampler).GetMethod(nameof(RunSuffix), BindingFlags.Static | BindingFlags.NonPublic));
            });
        }

        private static void RunPrefix() => _started = Recorder.Current == null ? 0 : Stopwatch.GetTimestamp();

        private static void RunSuffix(MyProgrammableBlock __instance)
        {
            var started = _started;
            if (started == 0) return;
            _started = 0;
            var ticks = Stopwatch.GetTimestamp() - started;
            lock (Lock)
            {
                if (!ByBlock.TryGetValue(__instance.EntityId, out var e)) ByBlock[__instance.EntityId] = e = new Entry { Block = __instance };
                e.Ticks += ticks;
                e.Runs++;
                if (ticks > e.Max) e.Max = ticks;
            }
        }

        /// <summary>Game thread, every frame: the frames counted, and every <see cref="Every"/> the rows of the minute that ended.</summary>
        public static void Tick(Recorder recorder)
        {
            _frames++;
            if (DateTime.UtcNow - _last < Every) return;
            _last = DateTime.UtcNow;
            List<Entry> done;
            lock (Lock)
            {
                done = ByBlock.Values.ToList();
                ByBlock.Clear();
            }
            var frames = Math.Max(1, _frames);
            _frames = 0;
            if (done.Count == 0 || !Warmup.Over) return;
            double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            var t = Clock.Now;
            var players = MySession.Static?.Players;
            foreach (var e in done.OrderByDescending(e => e.Ticks))
            {
                var block = e.Block;
                var grid = block.CubeGrid;
                var owner = block.OwnerId != 0 ? block.OwnerId : Identities.Owner(grid);
                var ownerName = owner != 0 ? players?.TryGetIdentity(owner)?.DisplayName ?? "" : "";
                // the block's own name and its grid's: two "Programmable block" of two bases are two lines
                var name = InventorySweep.BlockName(block) + " @ " + (grid?.DisplayName ?? "?");
                recorder.Store.Add(new Row(Table.Load, t, t, Kind, block.EntityId, name, owner, ownerName, frames, Ms(e.Ticks) / frames, Ms(e.Max), null));
            }
        }
    }
}

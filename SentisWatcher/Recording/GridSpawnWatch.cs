using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// A grid that comes into the world from somewhere else than its building, a split or the world's load: event
    /// <c>grid_spawned</c> with where it came from - VirtualGarage's garage, the game's services terminal (its grid storage),
    /// a paste, the admin screens, a plugin or a mod - its owner, its blocks, PCU and its worth in components (what its
    /// blocks are built of, as far as they are built). The game's own spawns (NPC ships, encounters, respawn ships,
    /// building and splitting) are left out. An event only: a grid coming is no fault in itself (VirtualGarage's are checked
    /// against the garage by <see cref="GarageWatch"/>).
    /// </summary>
    public static class GridSpawnWatch
    {
        private static readonly Logger Log = LogManager.GetLogger("SentisWatcher.GridSpawns");
        private static Recorder R => Recorder.Current;

        public const int StacksPerSecond = 30;
        private static long _second;
        private static int _inSecond;

        /// <summary>Grids told since the start, the last one's line and the last <see cref="RecentKept"/> (for the tests).</summary>
        public const int RecentKept = 1024;
        public static long Count;
        public static string Last;
        public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Recent = new System.Collections.Concurrent.ConcurrentQueue<string>();

        /// <summary>A grid added to the world (MyEntities.OnEntityAdd): where it came from, told at once (the frame after from another thread).</summary>
        internal static void Added(MyCubeGrid grid)
        {
            // (the hooks are attached once the world is loaded: its own grids came before)
            if (grid == null || grid.IsPreview || grid.Parent != null) return;
            // a paste's (the services terminal's too) by its grids' names, noted as it started: no stack read, no limit
            var origin = Pasted(grid.DisplayName);
            if (origin == null)
            {
                if (!Allow()) return;
                origin = Origin(new StackTrace(2, false));
                // the garage's are told by GarageWatch with its take-out (whatever the limit here)
                if (origin == null || origin == "garage") return;
            }
            Told(R, grid, origin);
        }

        /// <summary>A grid come from <paramref name="origin"/>, told at once on the game thread (a grid may be gone before the next frame), else the frame after.</summary>
        internal static void Told(Recorder recorder, MyCubeGrid grid, string origin)
        {
            if (recorder == null || grid == null) return;
            // at once on the game thread (a grid may be gone before the next frame); from another, the frame after
            if (Sandbox.MySandboxGame.Static?.UpdateThread == Thread.CurrentThread)
            {
                Tell(recorder, grid, origin);
                return;
            }
            Sandbox.MySandboxGame.Static?.Invoke(() =>
            {
                if (recorder == null || Recorder.Current != recorder || grid.MarkedForClose) return;
                Recorder.Safe("grid spawned", () => Tell(recorder, grid, origin));
            }, "SentisWatcher grid spawned");
        }

        private static void Tell(Recorder recorder, MyCubeGrid grid, string origin)
        {
            var owner = Identities.Owner(grid);
            var components = Components(grid);
            var at = grid.PositionComp.GetPosition();
            var what = $"{origin}: \"{grid.DisplayName}\" {grid.EntityId}, {grid.BlocksCount} blocks, {grid.BlocksPCU} PCU, {components:0} components, " +
                       (grid.IsStatic ? "static" : "ship") + $", owner {Identities.NameOf(owner)} ({owner}) at {at.X:0} {at.Y:0} {at.Z:0}";
            recorder.Event("grid_spawned", owner, grid.EntityId, at, components, grid.BlocksCount, what);
            Interlocked.Increment(ref Count);
            Last = what;
            Recent.Enqueue(what);
            while (Recent.Count > RecentKept && Recent.TryDequeue(out _)) { }
            if (SentisWatcherPlugin.Config?.DiagnosticLogs == true) Log.Info("SentisWatcher: grid spawned " + what);
        }

        // the pastes going on, by their grids' names: the game adds a paste's grids later, from the paste's own callback,
        // and a services terminal's fetch is a paste (MyGridStorageHelper's OnPasteFinished) with no frame of the terminal
        // on the stack then
        private static readonly List<(string Name, string Origin, long At)> Pastes = new List<(string, string, long)>();
        private const long PasteMs = 60000;

        /// <summary>A paste starting (MyCubeGrid.TryPasteGrid_Implementation): its grids' names noted, the services terminal's apart.</summary>
        internal static void Pasting(MyCubeGrid.MyPasteGridParameters parameters)
        {
            var by = parameters.OnPasteFinished?.Method.DeclaringType;
            while (by != null && by.Name != "MyGridStorageHelper") by = by.DeclaringType;
            var origin = by != null ? "services_terminal" : "paste";
            var now = SentisWatcher.Storage.Clock.Now;
            lock (Pastes)
            {
                Pastes.RemoveAll(p => now - p.At > PasteMs);
                foreach (var grid in parameters.Entities) Pastes.Add((grid.DisplayName, origin, now));
            }
        }

        /// <summary>The origin of a paste going on whose grid has this name, taken off the list; null for none.</summary>
        private static string Pasted(string name)
        {
            var now = SentisWatcher.Storage.Clock.Now;
            lock (Pastes)
            {
                if (Pastes.Count == 0) return null;
                var i = Pastes.FindIndex(p => p.Name == name && now - p.At <= PasteMs);
                if (i < 0) return null;
                var origin = Pastes[i].Origin;
                Pastes.RemoveAt(i);
                return origin;
            }
        }

        /// <summary>What the grid's blocks are built of: their components, as far as each is built.</summary>
        public static double Components(MyCubeGrid grid)
        {
            double sum = 0;
            foreach (var block in grid.GetBlocks())
            {
                var definition = block.BlockDefinition;
                if (definition?.Components == null) continue;
                var whole = definition.Components.Sum(c => (double)c.Count);
                sum += whole * Math.Max(0, Math.Min(1, block.BuildLevelRatio));
            }
            return sum;
        }

        /// <summary>
        /// Where the grid comes from, from the stack of its adding; null for the game's own (built, split, loaded, NPC,
        /// respawn ship).
        /// </summary>
        public static string Origin(StackTrace trace)
        {
            // the garage and the services terminal first, wherever they are in the stack: a terminal's fetch pastes
            for (var i = 0; i < trace.FrameCount; i++)
            {
                var type = trace.GetFrame(i)?.GetMethod()?.DeclaringType;
                if (type == null) continue;
                var outer = Storage(type.Assembly.GetName().Name, type.Name);
                if (outer != null) return outer;
            }
            string plugin = null;
            for (var i = 0; i < trace.FrameCount; i++)
            {
                var method = trace.GetFrame(i)?.GetMethod();
                if (method == null) continue;
                var type = method.DeclaringType;
                // a game method patched by a plugin runs as a copy named after it ("Patched_Sandbox...MyCubeGridTryPasteGrid_...")
                if (type == null)
                {
                    if (method.Name.Contains("PasteGrid")) return "paste";
                    continue;
                }
                var origin = Known(type.Assembly.GetName().Name, type.Name, method.Name);
                // the game's own spawn: unless a plugin inside it made the grid (innermost first)
                if (origin == "") return plugin;
                if (origin != null) return origin;
                if (plugin == null) plugin = Outside(type);
            }
            return plugin;
        }

        /// <summary>What a frame says of a grid's coming: its origin, "" for the game's own spawn, null when it says nothing.</summary>
        public static string Known(string assembly, string type, string method)
        {
            var storage = Storage(assembly, type);
            if (storage != null) return storage;
            switch (type)
            {
                case "MyCubeGrid":
                    if (method.Contains("Paste")) return "paste";
                    if (method.Contains("Split") || method.Contains("Merge") || method.Contains("MoveBlocks") || method.Contains("CreateSplit")) return "";
                    return null;
                case "MyCubeBuilder":
                case "MyProjectorBase":
                case "MyMechanicalConnectionBlockBase":
                case "MyAttachableTopBlockBase":
                case "MySpaceRespawnComponent":
                case "MyRespawnComponentBase":
                case "MyPrefabManager":
                case "MyNeutralShipSpawner":
                case "MyGlobalEncountersGenerator":
                case "MyEncounterGenerator":
                case "MyPirateAntennas":
                case "MyStationCellGenerator":
                case "MyProceduralWorldModule": return "";
            }
            if (type.StartsWith("MyGuiScreenDebug") || type.StartsWith("MyGuiScreenAdmin")) return "admin";
            return null;
        }

        /// <summary>The garage's or the services terminal's frame: "garage", "services_terminal"; null for any other.</summary>
        public static string Storage(string assembly, string type)
        {
            if (assembly == "VirtualGarage") return "garage";
            return type == "MyGridStorageHelper" || type == "MyGridsStorageSessionComponent" || type == "MyGridsStorageEntityComponent" ? "services_terminal" : null;
        }

        /// <summary>"plugin:X" or "mod:X" for a frame of neither the game, Torch, the runtime nor this plugin.</summary>
        private static string Outside(Type type)
        {
            var assembly = type.Assembly;
            if (assembly == typeof(GridSpawnWatch).Assembly) return null;
            var name = assembly.GetName().Name ?? "?";
            if (name == "mscorlib" || name.StartsWith("System") || name.StartsWith("Microsoft") || name == "0Harmony" || name.StartsWith("Mono.") ||
                name == "NLog" || name.StartsWith("Sandbox") || name.StartsWith("VRage") || name.StartsWith("SpaceEngineers") ||
                name == "HavokWrapper" || name == "Torch" || name.StartsWith("Torch.")) return null;
            var mod = MyScriptManager.Static?.Scripts?.Values.Contains(assembly) == true;
            return (mod ? "mod:" : "plugin:") + name;
        }

        private static bool Allow()
        {
            var second = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            if (second != _second)
            {
                _second = second;
                _inSecond = 0;
            }
            return ++_inSecond <= StacksPerSecond;
        }
    }
}

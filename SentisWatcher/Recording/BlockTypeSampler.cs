using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using NLog;
using Sandbox.Game.Entities;
using SentisWatcher.Storage;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using VRage.Game;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// The game thread's time by the type of block: which kinds of block the server's frame goes to (rows of kind
    /// "block_type" in the load table, the chart "block types" of the performance page).
    ///
    /// It comes from two places, both in the frames the load sampler times (one in 30-90):
    /// - a block with an update of its own is an entity in the game's update lists, and the load sampler times it there
    ///   (and lays the time to its grid): the same time is laid to the block's type (<see cref="Charge"/>). Nothing is
    ///   added to the other frames;
    /// - a grid's own systems (gyros, conveyors, gas, mining) are run by the grid: MyCubeGrid.Dispatch walks the grid's
    ///   queue of scheduled updates and calls each one. The one call in that loop is made through
    ///   <see cref="RunBlockUpdate"/>, which puts a timestamp before and after it. In the other frames it is the call
    ///   and a flag read (1 ns a call measured; the stand has 14 such calls a frame).
    ///
    /// The updates a grid runs in parallel (on the workers, while the game thread waits) are not timed: their time is
    /// not the game thread's, and the grid's parallel time is in the load table as it is. What is not a block (a
    /// grid's system) goes by its class, in brackets. The same milliseconds are in the grid's row too.
    /// </summary>
    [PatchShim]
    public static class BlockTypeSampler
    {
        public const string Kind = "block_type";

        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private sealed class Entry
        {
            public string Name;
            public long Ticks, FrameTicks, Max;
            public long Calls;
            public bool Touched;
        }

        // game thread only
        private static readonly Dictionary<MyDefinitionId, Entry> ByDefinition = new Dictionary<MyDefinitionId, Entry>(MyDefinitionId.Comparer);
        private static readonly Dictionary<Type, Entry> ByClass = new Dictionary<Type, Entry>();
        private static readonly List<Entry> Touched = new List<Entry>();

        /// <summary>Whether the call in the grid's loop goes through here (false: the patch did not take, nothing is recorded).</summary>
        public static bool Installed { get; private set; }

        public static void Patch(PatchContext ctx)
        {
            PatchGuard.Run("SentisWatcher.BlockTypes", ctx, c =>
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                var dispatch = typeof(MyCubeGrid).GetMethods(any).Where(m => m.Name == "Dispatch").ToList();
                if (dispatch.Count != 1) throw new MissingMethodException("MyCubeGrid.Dispatch: found " + dispatch.Count);
                var parameters = dispatch[0].GetParameters();
                if (parameters.Length != 2 || parameters[1].ParameterType != typeof(bool))
                    throw new InvalidOperationException("MyCubeGrid.Dispatch is not (queue, parallel)");
                if (Invoke() == null || Callback() == null) throw new MissingMethodException("MyCubeGrid.Invoke / MyCubeGrid.Update.Callback");
                c.GetPattern(dispatch[0]).Transpilers.Add(typeof(BlockTypeSampler).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic));
            });
        }

        private static MethodInfo Invoke() => typeof(MyCubeGrid).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "Invoke" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.IsByRef &&
                                 m.GetParameters()[0].ParameterType.GetElementType() == UpdateType());

        private static Type UpdateType() => typeof(MyCubeGrid).GetNestedType("Update", BindingFlags.NonPublic | BindingFlags.Public);

        private static FieldInfo Callback()
        {
            var field = UpdateType()?.GetField("Callback", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field != null && field.FieldType == typeof(Action) ? field : null;
        }

        /// <summary>
        /// "this.Invoke(in update, queue)" in the loop becomes "RunBlockUpdate(this, update.Callback, parallel)": the
        /// queue is dropped from the stack, the callback read from the update. Anything else than the one call found
        /// leaves the method as it is (and nothing is recorded).
        /// </summary>
        private static IEnumerable<MsilInstruction> Transpiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            try
            {
                var invoke = Invoke();
                var calls = Enumerable.Range(0, list.Count).Where(i => list[i].OpCode == OpCodes.Call &&
                    list[i].Operand is MsilOperandInline<MethodBase> operand && operand.Value == invoke).ToList();
                if (calls.Count != 1)
                {
                    Log.Warn("SentisWatcher: block types are not timed: MyCubeGrid.Dispatch calls Invoke " + calls.Count + " times, one was expected");
                    return list;
                }
                var at = calls[0];
                var pop = new MsilInstruction(OpCodes.Pop);
                foreach (var label in list[at].Labels) pop.Labels.Add(label);
                var replaced = new List<MsilInstruction>(list.Count + 3);
                replaced.AddRange(list.Take(at));
                replaced.Add(pop);                                                                   // the queue
                replaced.Add(new MsilInstruction(OpCodes.Ldfld).InlineValue(Callback()));           // &update -> its callback
                replaced.Add(new MsilInstruction(OpCodes.Ldarg_2));                                  // parallel
                replaced.Add(new MsilInstruction(OpCodes.Call).InlineValue((MethodBase)typeof(BlockTypeSampler).GetMethod(nameof(RunBlockUpdate), BindingFlags.Static | BindingFlags.Public)));
                replaced.AddRange(list.Skip(at + 1));
                Installed = true;
                return replaced;
            }
            catch (Exception e)
            {
                Log.Warn(e, "SentisWatcher: block types are not timed");
                Installed = false;
                return list;
            }
        }

        /// <summary>One scheduled update of a grid, as its loop calls it (see the class). Game thread or, for a parallel one, a worker.</summary>
        public static void RunBlockUpdate(MyCubeGrid grid, Action callback, bool parallel)
        {
            if (parallel || !LoadSampler.Timing)
            {
                callback();
                return;
            }
            var started = Stopwatch.GetTimestamp();
            try
            {
                callback();
            }
            finally
            {
                Add(Of(callback.Target, callback), Stopwatch.GetTimestamp() - started);
            }
        }

        /// <summary>A block's timed update (its own time, ticks) to its type. Game thread, a timed frame.</summary>
        internal static void Charge(MyCubeBlock block, long ticks)
        {
            if (block.BlockDefinition == null) return;
            Add(Of(block), ticks);
        }

        private static void Add(Entry e, long ticks)
        {
            e.FrameTicks += ticks;
            e.Calls++;
            if (e.Touched) return;
            e.Touched = true;
            Touched.Add(e);
        }

        private static Entry Of(MyCubeBlock block)
        {
            var id = block.BlockDefinition.Id;
            if (!ByDefinition.TryGetValue(id, out var known)) ByDefinition[id] = known = new Entry { Name = BlockName(id) };
            return known;
        }

        private static Entry Of(object target, Action callback)
        {
            if (target is MyCubeBlock block && block.BlockDefinition != null) return Of(block);
            // a grid's own system, the grid itself, or a closure over one of them
            var type = target?.GetType() ?? callback.Method.DeclaringType ?? typeof(object);
            if (!ByClass.TryGetValue(type, out var e)) ByClass[type] = e = new Entry { Name = ClassName(type) };
            return e;
        }

        /// <summary>"Refinery/LargeRefinery"; a block with no subtype by its type alone.</summary>
        public static string BlockName(MyDefinitionId id)
        {
            var type = SentisWatcher.Recording.InventoryCodec.ShortType(id.TypeId.ToString());
            return string.IsNullOrEmpty(id.SubtypeName) ? type : type + "/" + id.SubtypeName;
        }

        /// <summary>"(MyGridConveyorSystem)": what is not a block, by its class - a compiler's closure by the class it is in.</summary>
        public static string ClassName(Type type)
        {
            while (type.DeclaringType != null && type.Name.StartsWith("<")) type = type.DeclaringType;
            return "(" + type.Name + ")";
        }

        /// <summary>The timed frame is over: its sums into the period's (the worst frame of a type is its whole time in one frame).</summary>
        internal static void FrameEnd()
        {
            foreach (var e in Touched)
            {
                e.Ticks += e.FrameTicks;
                if (e.FrameTicks > e.Max) e.Max = e.FrameTicks;
                e.FrameTicks = 0;
                e.Touched = false;
            }
            Touched.Clear();
        }

        /// <summary>The period's rows (the heaviest <paramref name="top"/>): each type's time in an average timed frame, its worst frame, its updates a frame.</summary>
        internal static void Flush(Recorder recorder, long t, int frames, int top)
        {
            if (frames <= 0) return;
            double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            foreach (var e in ByDefinition.Values.Concat(ByClass.Values).Where(e => e.Ticks > 0).OrderByDescending(e => e.Ticks).Take(top))
                recorder.Store.Add(new Row(Table.Load, t, t, Kind, 0L, e.Name, 0L, "", frames, Ms(e.Ticks) / frames, Ms(e.Max), null, (double)e.Calls / frames));
        }

        internal static void Clear()
        {
            ByDefinition.Clear();
            ByClass.Clear();
            Touched.Clear();
        }
    }
}

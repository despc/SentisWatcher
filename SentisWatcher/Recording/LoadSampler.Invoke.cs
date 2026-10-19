using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using NLog;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;

namespace SentisWatcher.Recording
{
    public static partial class LoadSampler
    {
        /// <summary>A call from the game's invoke queue that took this long or more is charged by its name.</summary>
        private static readonly long InvokeNamedTicks = Stopwatch.Frequency / 1000;

        private static readonly Dictionary<MethodInfo, string> InvokeNames = new Dictionary<MethodInfo, string>();

        /// <summary>One call of the invoke queue (see <see cref="InvokeQueueTiming"/>). Game thread.</summary>
        public static void RunInvoked(Action action)
        {
            if (!_timing) { action(); return; }
            var started = Begin(out var nested);
            try { action(); }
            finally { End(InvokedEntry(action.Method, Stopwatch.GetTimestamp() - started), started, nested); }
        }

        /// <summary>One call with its context of the invoke queue (see <see cref="InvokeQueueTiming"/>). Game thread.</summary>
        public static void RunInvokedWith(Action<object> action, object context)
        {
            if (!_timing) { action(context); return; }
            var started = Begin(out var nested);
            try { action(context); }
            finally { End(InvokedEntry(action.Method, Stopwatch.GetTimestamp() - started), started, nested); }
        }

        // the slow ones by their method's name (the type and the method a lambda was written in), the rest as the queue
        private static Entry InvokedEntry(MethodInfo method, long elapsed)
        {
            if (elapsed < InvokeNamedTicks || method == null) return Named(System, "game.invoke_queue");
            if (!InvokeNames.TryGetValue(method, out var name))
                InvokeNames[method] = name = "invoke: " + (method.DeclaringType?.FullName ?? "?") + "." + method.Name;
            return Named(System, name);
        }
    }

    /// <summary>
    /// The calls other threads and plugins queue for the game thread (MySandboxGame.ProcessInvoke) timed each: a heavy
    /// frame of "game.invoke_queue" said nothing of whose call it was - 355 ms of it on the stand, 19.10.2026, the plugins
    /// and Torch all queueing calls there. Each call of the queue goes through <see cref="LoadSampler.RunInvoked"/>.
    /// </summary>
    [PatchShim]
    public static class InvokeQueueTiming
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            PatchGuard.Run("SentisWatcher.InvokeQueue", ctx, c =>
            {
                var target = typeof(Sandbox.MySandboxGame).GetMethod("ProcessInvoke", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (target == null) throw new MissingMethodException("MySandboxGame.ProcessInvoke");
                c.GetPattern(target).Transpilers.Add(typeof(InvokeQueueTiming).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic));
            });
        }

        private static readonly MethodInfo ActionInvoke = typeof(Action).GetMethod("Invoke");
        private static readonly MethodInfo ContextInvoke = typeof(Action<object>).GetMethod("Invoke");

        /// <summary>
        /// "action()" and "contextualAction(context)" in the loop become RunInvoked(action) and RunInvokedWith(action,
        /// context): the same arguments on the stack. Anything else than one of each leaves the method as it is.
        /// </summary>
        private static IEnumerable<MsilInstruction> Transpiler(IEnumerable<MsilInstruction> instructions)
        {
            var list = instructions.ToList();
            try
            {
                bool Calls(MsilInstruction i, MethodInfo m) => (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call) &&
                                                               i.Operand is MsilOperandInline<MethodBase> operand && operand.Value == m;
                var plain = Enumerable.Range(0, list.Count).Where(i => Calls(list[i], ActionInvoke)).ToList();
                var withContext = Enumerable.Range(0, list.Count).Where(i => Calls(list[i], ContextInvoke)).ToList();
                if (plain.Count != 1 || withContext.Count != 1)
                {
                    Log.Warn($"SentisWatcher: the invoke queue's calls are not timed one by one: ProcessInvoke calls Action.Invoke {plain.Count} and Action<object>.Invoke {withContext.Count} times, one each was expected");
                    return list;
                }
                var result = new List<MsilInstruction>(list.Count);
                for (var i = 0; i < list.Count; i++)
                {
                    if (i != plain[0] && i != withContext[0]) { result.Add(list[i]); continue; }
                    var helper = i == plain[0] ? nameof(LoadSampler.RunInvoked) : nameof(LoadSampler.RunInvokedWith);
                    var call = new MsilInstruction(OpCodes.Call).InlineValue((MethodBase)typeof(LoadSampler).GetMethod(helper, BindingFlags.Static | BindingFlags.Public));
                    foreach (var label in list[i].Labels) call.Labels.Add(label);
                    result.Add(call);
                }
                return result;
            }
            catch (Exception e)
            {
                Log.Warn(e, "SentisWatcher: the invoke queue's calls are not timed one by one");
                return list;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Game.Entity;

namespace SentisWatcher.Ledger
{
    /// <summary>
    /// What the game is doing when an inventory changes: a hook on the method that does it (a refinery
    /// finishing a batch, a transfer, a pickup) opens a context, the change is booked under it, and closing
    /// it checks that the books balance (production took its inputs, a transfer made nothing).
    /// </summary>
    public sealed class Context
    {
        public string Kind;
        public string Flow;                 // the kind of flow booked under it; "load:paste" for a "load"
        public object Key;                  // what opened it, to close the right one
        public MyInventory Src, Dst;        // transfer
        public MyEntity Producer;           // refinery, assembler
        public MyBlueprintDefinitionBase Blueprint;
        public long Limit;                  // pickup: what the floating object held
        public MySlimBlock Block;           // grind, weld
        public MyInventory Where;           // the last inventory something was added to
        public readonly Dictionary<string, long> Added = new Dictionary<string, long>();
        public readonly Dictionary<string, long> Removed = new Dictionary<string, long>();

        internal void Reset(string kind, object key)
        {
            Kind = Flow = kind;
            Key = key;
            Src = Dst = null;
            Producer = null;
            Blueprint = null;
            Limit = 0;
            Block = null;
            Where = null;
            Added.Clear();
            Removed.Clear();
        }

        internal void Tally(MyInventory inventory, string item, long raw)
        {
            if (Kind == "transfer")
            {
                // only what left the source and reached the target counts, and not a restack in one inventory
                if (Src == Dst) return;
                if (raw > 0 && inventory != Dst) return;
                if (raw < 0 && inventory != Src) return;
            }
            if (raw > 0) Where = inventory;
            var into = raw > 0 ? Added : Removed;
            into.TryGetValue(item, out var was);
            into[item] = was + Math.Abs(raw);
        }
    }

    /// <summary>The open contexts of the thread, innermost last.</summary>
    public static class Contexts
    {
        private const int Depth = 16;
        [ThreadStatic] private static Context[] _stack;
        [ThreadStatic] private static int _depth;

        public static Context Top => _depth > 0 ? _stack[_depth - 1] : null;

        /// <summary>A new context, or null when too deep (then nothing is booked under it).</summary>
        public static Context Push(string kind, object key)
        {
            if (_stack == null) _stack = new Context[Depth];
            if (_depth >= Depth) return null;
            var context = _stack[_depth] ?? (_stack[_depth] = new Context());
            context.Reset(kind, key);
            _depth++;
            return context;
        }

        /// <summary>
        /// Closes the context the key and kind opened, and any left open inside it (a method that threw
        /// skips its suffix); null when there is none. The context stays readable until the next push.
        /// </summary>
        public static Context Pop(string kind, object key)
        {
            for (var i = _depth - 1; i >= 0; i--)
            {
                var context = _stack[i];
                if (ReferenceEquals(context.Key, key) && context.Kind == kind)
                {
                    _depth = i;
                    return context;
                }
            }
            return null;
        }

        /// <summary>Forgets everything open (game thread, between frames: nothing may be open there).</summary>
        public static void Clear() => _depth = 0;
    }
}

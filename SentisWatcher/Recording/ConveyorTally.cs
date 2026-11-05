using System.Collections.Generic;
using VRageMath;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// The items the conveyors (and the scripts and sorters using them) moved from one grid to another, summed over
    /// <see cref="WindowMs"/> per source block, destination block, item and who started it: a sorter draining a docked
    /// ship is a row a container and item every half minute, not one a stack. Thread safe.
    /// </summary>
    public sealed class ConveyorTally
    {
        public const long WindowMs = 30_000;

        public sealed class Move
        {
            public long Source, Destination, Actor;
            public string Detail;
            public double Amount;
            public int Count;
            public long First;
            public Vector3D Position;
        }

        private readonly Dictionary<(long, long, string), Move> _open = new Dictionary<(long, long, string), Move>();
        private readonly object _lock = new object();
        private long _windowStart = -1;

        /// <summary>
        /// Adds a move; <paramref name="detail"/> (the item, the blocks, who) is asked for only for the first of its
        /// window, and is the key with the blocks.
        /// </summary>
        public void Add(long time, long source, long destination, long actor, string key, System.Func<string> detail, double amount, Vector3D position)
        {
            lock (_lock)
            {
                if (_windowStart < 0) _windowStart = time;
                var k = (source, destination, key);
                if (!_open.TryGetValue(k, out var move))
                {
                    move = new Move { Source = source, Destination = destination, Actor = actor, Detail = detail(), First = time, Position = position };
                    _open[k] = move;
                }
                move.Amount += amount;
                move.Count++;
            }
        }

        /// <summary>The moves of a window that has ended by <paramref name="now"/> (all of them when forced).</summary>
        public List<Move> Take(long now, bool force = false)
        {
            lock (_lock)
            {
                if (_open.Count == 0 || (!force && now - _windowStart < WindowMs)) return null;
                var done = new List<Move>(_open.Values);
                _open.Clear();
                _windowStart = -1;
                return done;
            }
        }
    }
}

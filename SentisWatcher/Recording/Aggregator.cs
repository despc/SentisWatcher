using System.Collections.Generic;
using VRageMath;

namespace SentisWatcher.Recording
{
    /// <summary>One aggregated event: what, who, to what, how much in how many hits, where the last one was.</summary>
    public sealed class Aggregate
    {
        public string Kind;
        public long Actor;
        public long Entity;
        public string Detail;
        public double Amount;
        public int Count;
        public long First;
        public long Last;
        public Vector3D Position;
    }

    /// <summary>
    /// Sums frequent events (damage, welding, grinding, blocks built) over a short window, one row per
    /// kind, actor, entity and detail instead of one per hit. Thread safe.
    /// </summary>
    public sealed class Aggregator
    {
        public const long WindowMs = 2000;

        private readonly Dictionary<(string, long, long, string), Aggregate> _open = new Dictionary<(string, long, long, string), Aggregate>();
        private readonly object _lock = new object();
        private long _windowStart = -1;

        public void Add(long time, string kind, long actor, long entity, string detail, double amount, Vector3D position)
        {
            lock (_lock)
            {
                if (_windowStart < 0) _windowStart = time;
                var key = (kind, actor, entity, detail);
                if (!_open.TryGetValue(key, out var aggregate))
                {
                    aggregate = new Aggregate { Kind = kind, Actor = actor, Entity = entity, Detail = detail, First = time };
                    _open[key] = aggregate;
                }
                aggregate.Amount += amount;
                aggregate.Count++;
                aggregate.Last = time;
                aggregate.Position = position;
            }
        }

        /// <summary>The aggregates of a window that has ended by <paramref name="now"/> (all of them when forced).</summary>
        public List<Aggregate> Take(long now, bool force = false)
        {
            lock (_lock)
            {
                if (_open.Count == 0 || (!force && now - _windowStart < WindowMs)) return null;
                var done = new List<Aggregate>(_open.Values);
                _open.Clear();
                _windowStart = -1;
                return done;
            }
        }
    }
}

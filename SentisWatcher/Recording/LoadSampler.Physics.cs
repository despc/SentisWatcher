using System;
using System.Collections.Generic;
using System.Linq;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// The physics step laid to the grids (<see cref="GridPhysics"/>), by SentisOptimisations' count
    /// (<c>Optimizer.Optimizations.PhysicsCost.Share</c>): the one its physics guard punishes by. It times the physics
    /// clusters now and then (one stepped alone with a stopwatch, one frame in 30 at most) and shares a step out by
    /// cluster, then within a cluster by the work of each grid group (active bodies weighed by their blocks, mechanical
    /// connections); a group goes by its grid of the most blocks. Without SentisOptimisations the grids' physics is not
    /// recorded.
    /// </summary>
    public static partial class LoadSampler
    {
        private static readonly Dictionary<long, Entry> PhysicsByGrid = new Dictionary<long, Entry>();
        private static readonly Dictionary<long, double> PhysicsShares = new Dictionary<long, double>();
        private static Func<double, Dictionary<long, double>, int> _physicsShare;
        private static bool _physicsShareLooked;

        /// <summary>SentisOptimisations' share of the physics step, when it is loaded (looked for once, on the first timed step: the plugins load in any order).</summary>
        private static void FindPhysicsShare()
        {
            _physicsShareLooked = true;
            var cost = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                ?.GetType("Optimizer.Optimizations.PhysicsCost");
            var share = cost?.GetMethod("Share", new[] { typeof(double), typeof(Dictionary<long, double>) });
            if (share == null || share.ReturnType != typeof(int))
            {
                Log.Info("LoadSampler: SentisOptimisations' physics cost is not there, the grids' physics is not recorded");
                return;
            }
            _physicsShare = (Func<double, Dictionary<long, double>, int>)Delegate.CreateDelegate(typeof(Func<double, Dictionary<long, double>, int>), share);
            FindClusterParts();
        }

        /// <summary>A frame's step (ticks) to the grids.</summary>
        private static void SharePhysics(long stepTicks)
        {
            if (!_physicsShareLooked) FindPhysicsShare();
            if (_physicsShare == null || _physicsShare(stepTicks, PhysicsShares) == 0) return;
            ShareClusters();
            foreach (var pair in PhysicsShares)
            {
                var share = (long)pair.Value;
                if (share <= 0) continue;
                if (!PhysicsByGrid.TryGetValue(pair.Key, out var e)) PhysicsByGrid[pair.Key] = e = new Entry { Kind = GridPhysics, Id = pair.Key };
                e.Ticks += share;
                if (share > e.Max) e.Max = share;
            }
        }
    }
}

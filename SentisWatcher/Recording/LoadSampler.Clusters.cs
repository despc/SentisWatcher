using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using SentisWatcher.Storage;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// The clusters of the physics, when SentisClusters runs them (its parallel updates on): each one's physics and logic
    /// in an average frame, a row of each kind (<see cref="ClusterPhysics"/>, <see cref="ClusterLogic"/>) for the
    /// <see cref="TopClusters"/> heaviest every flush, by the cluster's id. The physics is SentisOptimisations' part of
    /// the step for the cluster (PhysicsCost.ClusterParts, the same count the grids' physics goes by), the logic is
    /// SentisClusters' time of the cluster's entities' updates; a detached cluster ticks on a thread of its own, and its
    /// own tick's step and the rest of it stand for them. The row's name is the cluster's grid of the most blocks, its
    /// owner 1 for a detached cluster, its owner's name "x;y;z;objects;players;sim" (the centre in km).
    /// </summary>
    public static partial class LoadSampler
    {
        public const string ClusterPhysics = "cluster_physics", ClusterLogic = "cluster_logic";
        private const int TopClusters = 30;

        private static readonly Dictionary<int, long> PhysicsByCluster = new Dictionary<int, long>();
        private static readonly Dictionary<int, double> ClusterParts = new Dictionary<int, double>();
        private static Func<Dictionary<int, double>, int> _clusterParts;

        private static void FindClusterParts()
        {
            var cost = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                ?.GetType("Optimizer.Optimizations.PhysicsCost");
            var parts = cost?.GetMethod("ClusterParts", new[] { typeof(Dictionary<int, double>) });
            if (parts != null && parts.ReturnType == typeof(int))
                _clusterParts = (Func<Dictionary<int, double>, int>)Delegate.CreateDelegate(typeof(Func<Dictionary<int, double>, int>), parts);
        }

        /// <summary>After a step shared to the grids: the same step's parts by cluster.</summary>
        private static void ShareClusters()
        {
            if (_clusterParts == null || _clusterParts(ClusterParts) == 0) return;
            foreach (var pair in ClusterParts)
            {
                var part = (long)pair.Value;
                if (part <= 0) continue;
                PhysicsByCluster.TryGetValue(pair.Key, out var was);
                PhysicsByCluster[pair.Key] = was + part;
            }
        }

        // ------------------------------------------------------------------ SentisClusters' snapshot, by reflection

        private sealed class Reader
        {
            public FieldInfo Current, World, Parallel, Clusters;
            public FieldInfo Id, Min, Max, Objects, Players, Ms, Detached, SimSpeed, TickMs, PhysicsMs;
            public PropertyInfo Config;
            public PropertyInfo Detach, DetachMs;
        }

        private static Reader _reader;
        private static bool _readerLooked;

        private static Reader Clusters()
        {
            if (_readerLooked) return _reader;
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisClusters");
            if (assembly == null) return null;   // not yet loaded, perhaps: asked again
            _readerLooked = true;
            try
            {
                var stats = assembly.GetType("SentisClusters.Stats.ClusterStats");
                var snapshot = assembly.GetType("SentisClusters.Stats.Snapshot") ?? stats?.GetField("Current")?.FieldType;
                var row = assembly.GetType("SentisClusters.Stats.ClusterRow");
                var plugin = assembly.GetType("SentisClusters.SentisClustersPlugin");
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public;
                var r = new Reader
                {
                    Current = stats?.GetField("Current", BindingFlags.Static | BindingFlags.Public),
                    World = snapshot?.GetField("World", inst), Parallel = snapshot?.GetField("Parallel", inst), Clusters = snapshot?.GetField("Clusters", inst),
                    Id = row?.GetField("Id", inst), Min = row?.GetField("Min", inst), Max = row?.GetField("Max", inst), Objects = row?.GetField("Objects", inst),
                    Players = row?.GetField("Players", inst), Ms = row?.GetField("Ms", inst), Detached = row?.GetField("Detached", inst),
                    SimSpeed = row?.GetField("SimSpeed", inst), TickMs = row?.GetField("TickMs", inst), PhysicsMs = row?.GetField("PhysicsMs", inst),
                    Config = plugin?.GetProperty("Config", BindingFlags.Static | BindingFlags.Public),
                };
                if (r.Config != null)
                {
                    r.Detach = r.Config.PropertyType.GetProperty("Detach");
                    r.DetachMs = r.Config.PropertyType.GetProperty("DetachMs");
                }
                if (r.Current == null || r.Clusters == null || r.Id == null || r.Ms == null)
                {
                    Log.Warn("LoadSampler: SentisClusters' statistics are not as expected, the clusters are not recorded");
                    return null;
                }
                _reader = r;
            }
            catch (Exception e)
            {
                Log.Warn(e, "LoadSampler: SentisClusters' statistics");
            }
            return _reader;
        }

        /// <summary>Whether SentisClusters is loaded and runs the clusters' updates now; its detaching threshold (ms, 0 when off).</summary>
        public static bool ClustersRunning(out double detachMs)
        {
            detachMs = 0;
            var r = Clusters();
            var snapshot = r?.Current.GetValue(null);
            if (snapshot == null) return false;
            try
            {
                var config = r.Config?.GetValue(null);
                if (config != null && r.Detach != null && (bool)r.Detach.GetValue(config) && r.DetachMs != null)
                    detachMs = Convert.ToDouble(r.DetachMs.GetValue(config), CultureInfo.InvariantCulture);
                return (bool)(r.World?.GetValue(snapshot) ?? false) && (bool)(r.Parallel?.GetValue(snapshot) ?? false);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The clusters' rows of this flush. Game thread.</summary>
        private static void FlushClusters(Recorder recorder, long t, int frames)
        {
            if (!ClustersRunning(out _)) return;
            var r = _reader;
            if (!(r.Clusters.GetValue(r.Current.GetValue(null)) is Array rows) || rows.Length == 0) return;
            double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

            var list = new List<(int Id, double Physics, double Logic, bool Detached, string Info)>();
            foreach (var row in rows)
            {
                var id = (int)r.Id.GetValue(row);
                var detached = r.Detached != null && (bool)r.Detached.GetValue(row);
                var ms = Convert.ToDouble(r.Ms.GetValue(row), CultureInfo.InvariantCulture);
                double physics, logic;
                if (detached)
                {
                    // its own tick: the world's step and the rest (its entities' updates)
                    var tick = r.TickMs == null ? ms : Convert.ToDouble(r.TickMs.GetValue(row), CultureInfo.InvariantCulture);
                    physics = r.PhysicsMs == null ? 0 : Convert.ToDouble(r.PhysicsMs.GetValue(row), CultureInfo.InvariantCulture);
                    logic = Math.Max(0, tick - physics);
                }
                else
                {
                    physics = PhysicsByCluster.TryGetValue(id, out var ticks) && frames > 0 ? Ms(ticks) / frames : 0;
                    logic = Math.Max(0, ms);
                }
                if (physics <= 0 && logic <= 0) continue;
                var min = r.Min?.GetValue(row) as double[];
                var max = r.Max?.GetValue(row) as double[];
                var centre = min != null && max != null && min.Length == 3 && max.Length == 3
                    ? string.Format(CultureInfo.InvariantCulture, "{0:F1};{1:F1};{2:F1}", (min[0] + max[0]) / 2000, (min[1] + max[1]) / 2000, (min[2] + max[2]) / 2000)
                    : ";;";
                var info = centre + ";" + r.Objects?.GetValue(row) + ";" + r.Players?.GetValue(row) + ";" +
                           Convert.ToDouble(r.SimSpeed?.GetValue(row) ?? 0.0, CultureInfo.InvariantCulture).ToString("F2", CultureInfo.InvariantCulture);
                list.Add((id, physics, logic, detached, info));
            }
            if (list.Count == 0) return;
            var top = list.OrderByDescending(c => c.Physics + c.Logic).Take(TopClusters).ToList();
            var names = BiggestGrids(new HashSet<int>(top.Select(c => c.Id)));
            foreach (var c in top)
            {
                names.TryGetValue(c.Id, out var name);
                var flag = c.Detached ? 1L : 0L;
                recorder.Store.Add(new Row(Table.Load, t, t, ClusterPhysics, (long)c.Id, name ?? "", flag, c.Info, frames, c.Physics, c.Physics, 0.0));
                recorder.Store.Add(new Row(Table.Load, t, t, ClusterLogic, (long)c.Id, name ?? "", flag, c.Info, frames, c.Logic, c.Logic, 0.0));
            }
        }

        /// <summary>The name of each cluster's grid of the most blocks (by its world of Havok).</summary>
        private static Dictionary<int, string> BiggestGrids(HashSet<int> wanted)
        {
            var names = new Dictionary<int, string>();
            var byWorld = new Dictionary<HkWorld, int>();
            var tree = MyPhysics.Clusters;
            if (tree == null) return names;
            foreach (var cluster in tree.GetClusters())
                if (wanted.Contains(cluster.ClusterId) && cluster.UserData is HkWorld world)
                    byWorld[world] = cluster.ClusterId;
            var best = new Dictionary<int, MyCubeGrid>();
            foreach (var entity in MyEntities.GetEntities())
            {
                if (!(entity is MyCubeGrid grid) || grid.MarkedForClose || grid.Physics == null) continue;
                var world = grid.Physics.HavokWorld;
                if (world == null || !byWorld.TryGetValue(world, out var id)) continue;
                if (!best.TryGetValue(id, out var was) || grid.BlocksCount > was.BlocksCount) best[id] = grid;
            }
            foreach (var pair in best) names[pair.Key] = pair.Value.DisplayName;
            return names;
        }
    }
}

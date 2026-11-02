using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using SpaceEngineers.Game.Entities.Blocks;
using SentisWatcher.Storage;

namespace SentisWatcher.Recording
{
    /// <summary>Storage, production and burn are distinct from gas passing into/out of a tank.</summary>
    public sealed class HydrogenTotals
    {
        public double Stored, Capacity, Buffer, Produced, Consumed, TankIn, TankOut;
        public readonly bool Frozen;
        public HydrogenTotals(bool frozen) { Frozen = frozen; }
        public void Add(string role, double stored, double capacity, double buffer, double produced, double consumed, double input, double output)
        {
            Stored += stored; Capacity += capacity; Buffer += buffer;
            if (Frozen) return;
            Produced += produced; Consumed += consumed;
            if (role == "tank") { TankIn += input; TankOut += output; }
        }
    }

    /// <summary>Game-thread, budgeted per block. Rates are instantaneous L/s, not integrated fuel counters.</summary>
    public sealed class HydrogenSampler
    {
        public const int IntervalMs = 10000;
        private readonly WatcherStore _store;
        private IEnumerator<int> _pass;
        private long _started;
        private readonly HashSet<long> _known = new HashSet<long>();
        private static readonly ConcurrentDictionary<long, Dictionary<string, object>> LatestRows = new ConcurrentDictionary<long, Dictionary<string, object>>();
        private Func<long, bool> _isFrozen;
        public HydrogenSampler(WatcherStore store) { _store = store; Clear(); BindFreezer(); }
        public static void Clear() => LatestRows.Clear();
        public static Dictionary<string, object> Latest(long grid) => LatestRows.TryGetValue(grid, out var value) ? value : null;

        private void BindFreezer()
        {
            // Optional integration, no reference to SO and no guessing a vanilla EntityFlags bit.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("SentisOptimisationsPlugin.Freezer.FreezeLogic");
                var set = type?.GetField("FrozenGrids", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var contains = set?.GetType().GetMethod("Contains", new[] { typeof(long) });
                if (contains != null) { _isFrozen = (Func<long, bool>)Delegate.CreateDelegate(typeof(Func<long, bool>), set, contains); break; }
            }
        }

        public void Tick()
        {
            if (_pass == null)
            {
                if (Clock.Now - _started < IntervalMs) return;
                _started = Clock.Now;
                _pass = Sample().GetEnumerator();
            }
            var start = Stopwatch.GetTimestamp();
            do
            {
                if (!_pass.MoveNext()) { _pass.Dispose(); _pass = null; break; }
            } while ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency < 0.2);
        }

        private IEnumerable<int> Sample()
        {
            var gas = MyResourceDistributorComponent.HydrogenId;
            var grids = MyEntities.GetEntities().OfType<MyCubeGrid>().ToArray();
            var alive = new HashSet<long>(grids.Select(g => g.EntityId));
            foreach (var id in LatestRows.Keys) if (!alive.Contains(id)) { LatestRows.TryRemove(id, out _); _known.Remove(id); }
            foreach (var grid in grids)
            {
                yield return 0;
                if (grid.Closed || grid.MarkedForClose || grid.IsPreview || grid.Physics == null) continue;
                var frozen = _isFrozen?.Invoke(grid.EntityId) == true;
                var totals = new HydrogenTotals(frozen);
                var rows = new List<object>();
                var thrustSinks = new HashSet<MyResourceSinkComponent>();
                var thrust = grid.Components.Get<MyEntityThrustComponent>();
                foreach (var block in grid.GetFatBlocks().ToArray())
                {
                    yield return 0;
                    if (block.Closed || block.MarkedForClose) continue;
                    var source = block.Components.Get<MyResourceSourceComponent>();
                    var sink = block.Components.Get<MyResourceSinkComponent>();
                    var role = "consumer";
                    double stored = 0, capacity = 0, buffer = 0, produced = 0, consumed = 0;
                    var name = InventorySweep.BlockName(block);
                    if (block is MyThrust)
                    {
                        sink = thrust?.ResourceSink(block);
                        if (sink == null || !sink.AcceptedResources.Contains(gas) || !thrustSinks.Add(sink)) continue;
                        role = "thrusters";
                        name = "Водородные движители (группа)";
                    }
                    var input = sink != null && sink.AcceptedResources.Contains(gas) ? (double)sink.CurrentInputByType(gas) : 0;
                    var output = source != null && source.ResourceTypes.Contains(gas) ? (double)source.CurrentOutputByType(gas) : 0;
                    if (block is MyGasTank tank)
                    {
                        if (tank.BlockDefinition.StoredGasId != gas) continue;
                        role = "tank"; capacity = tank.Capacity; stored = tank.FilledRatio * capacity;
                    }
                    else if (block is MyGasFueledPowerProducer engine)
                    {
                        var definition = engine.BlockDefinition;
                        if (definition.Fuel.FuelId != gas) continue;
                        role = "engine"; buffer = engine.Capacity;
                        input = engine.SinkComp.CurrentInputByType(gas);
                        if (definition.FuelProductionToCapacityMultiplier > 0)
                            consumed = engine.SourceComp.CurrentOutput / definition.FuelProductionToCapacityMultiplier;
                    }
                    else
                    {
                        if (output > 0 || source != null && source.ResourceTypes.Contains(gas)) { role = "generator"; produced = output; }
                        if (input > 0 || sink != null && sink.AcceptedResources.Contains(gas)) consumed = input;
                        else if (role != "generator") continue;
                    }
                    totals.Add(role, stored, capacity, buffer, produced, consumed, input, output);
                    rows.Add(new { id = block.EntityId.ToString(), name, type = block.BlockDefinition.DisplayNameText, role,
                        stored, capacity, buffer, produced = frozen ? 0 : produced, consumed = frozen ? 0 : consumed,
                        input = frozen ? 0 : input, output = frozen ? 0 : output });
                }
                // A thaw during this spread-out read mixes stale distributor rates with active ones: skip the sample.
                if (grid.Closed || grid.MarkedForClose || frozen != (_isFrozen?.Invoke(grid.EntityId) == true)) continue;
                if (rows.Count == 0 && !_known.Contains(grid.EntityId)) continue;
                _known.Add(grid.EntityId);
                var now = Clock.Now;
                var summary = new Dictionary<string, object>
                {
                    ["t"] = now, ["stored"] = totals.Stored, ["capacity"] = totals.Capacity, ["buffer"] = totals.Buffer,
                    ["produced"] = totals.Produced, ["consumed"] = totals.Consumed,
                    ["tankIn"] = totals.TankIn, ["tankOut"] = totals.TankOut, ["frozen"] = frozen,
                };
                LatestRows[grid.EntityId] = summary;
                _store.Add(new Row(Table.Hydrogen, now, now, grid.EntityId, grid.DisplayName ?? "", totals.Stored,
                    totals.Capacity, totals.Buffer, totals.Produced, totals.Consumed, totals.TankIn, totals.TankOut, frozen ? 1 : 0,
                    JsonConvert.SerializeObject(rows)));
            }
        }
    }
}

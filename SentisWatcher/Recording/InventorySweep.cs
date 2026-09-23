using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using SentisWatcher.Anomalies;
using SentisWatcher.Ledger;
using SentisWatcher.Storage;
using Sandbox.Game.Entities.Blocks;
using VRage.Game;
using VRage.Game.Entity;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// Goes through every inventory of the world - blocks, characters, bags - on the game thread, a slice of
    /// <see cref="BudgetMs"/> a frame, starting a new pass every <see cref="PassMs"/> at most. An inventory is
    /// written when its content differs from what was last written (a hash of its stacks), so a quiet world
    /// costs one pass of hashing; a new UTC day starts with every inventory written again. Each inventory is
    /// also checked for what no honest one holds (Invariants), and against the ledger: what it holds now must
    /// be what it held at the last visit plus the flows booked since (<see cref="InventoryLedger"/>); the
    /// flows are written with it. An inventory whose entity is gone gets a last row with no items.
    /// </summary>
    public sealed class InventorySweep
    {
        public const long PassMs = 5 * 60_000;
        public const double BudgetMs = 0.3;

        private readonly Recorder _recorder;
        private readonly Dictionary<(long, int), Seen> _seen = new Dictionary<(long, int), Seen>();

        /// <summary>An inventory as last visited: what it held (gas left out), whose it was, when it was written.</summary>
        private sealed class Seen
        {
            public ulong Hash;
            public Dictionary<string, long> Items;
            public long Owner, Grid;
            public DateTime Day;
        }
        private readonly List<ItemStack> _stacks = new List<ItemStack>();
        private MyEntity[] _entities = new MyEntity[0];
        private int _entityCursor;
        private List<MyCubeBlock> _blocks;
        private int _blockCursor;
        private long _passStarted = long.MinValue / 2;
        private DateTime _day;

        public int LastPassInventories { get; private set; }
        public int LastPassWritten { get; private set; }
        private int _inventories, _writtenThisPass;
        private List<(long, int)> _prune;
        private int _pruneCursor;

        public InventorySweep(Recorder recorder)
        {
            _recorder = recorder;
        }

        /// <summary>Game thread, every frame.</summary>
        public void Tick()
        {
            var now = Clock.Now;
            var today = DateTime.UtcNow.Date;
            if (today != _day) _day = today;      // every inventory is written again on its next visit
            var watch = Stopwatch.StartNew();
            var budget = (long)(BudgetMs * Stopwatch.Frequency / 1000);
            if (_prune != null)
            {
                // the inventories that are gone get their last row, a slice at a time like the rest
                while (_pruneCursor < _prune.Count && watch.ElapsedTicks < budget)
                {
                    var key = _prune[_pruneCursor++];
                    if (MyEntities.EntityExists(key.Item1)) continue;
                    _orphans.TryGetValue(key, out var flows);
                    _orphans.Remove(key);
                    _seen.TryGetValue(key, out var seen);
                    _seen.Remove(key);
                    Gone(key, seen, flows, now);
                }
                if (_pruneCursor < _prune.Count) return;
                _prune = null;
                _orphans.Clear();
            }
            if (_entityCursor >= _entities.Length && _blocks == null)
            {
                if (now - _passStarted < PassMs) return;
                StartPass(now);
            }
            while (watch.ElapsedTicks < budget)
            {
                if (_blocks != null)
                {
                    if (_blockCursor < _blocks.Count)
                    {
                        var block = _blocks[_blockCursor++];
                        if (!block.MarkedForClose) Visit(block, block.CubeGrid.EntityId, Identities.InventoryOwner(block), now);
                        continue;
                    }
                    _blocks = null;
                }
                if (_entityCursor >= _entities.Length)
                {
                    EndPass();
                    return;
                }
                var entity = _entities[_entityCursor++];
                if (entity == null || entity.MarkedForClose || entity.Closed) continue;
                if (entity is MyCubeGrid grid)
                {
                    if (grid.Physics == null) continue;    // projections
                    _blocks = grid.GetFatBlocks().Where(b => b.HasInventory || b is MyGasTank).ToList();
                    _blockCursor = 0;
                }
                else if (entity.HasInventory)
                {
                    Visit(entity, 0, Identities.InventoryOwner(entity), now);
                }
            }
        }

        /// <summary>Visits every inventory with flows not yet written (the server stops): nothing booked is lost.</summary>
        public void FlushPending()
        {
            var ledger = InventoryLedger.Current;
            if (ledger == null) return;
            var now = Clock.Now;
            foreach (var entity in ledger.PendingEntities())
            {
                if (entity.Closed || entity.MarkedForClose) continue;
                Visit(entity, entity is MyCubeBlock block ? block.CubeGrid.EntityId : 0, Identities.InventoryOwner(entity), now);
            }
        }

        /// <summary>Starts the next pass at the next frame instead of waiting for the period.</summary>
        public void StartNow() => _passStarted = long.MinValue / 2;

        /// <summary>Whether a pass is going on.</summary>
        public bool Busy => _entityCursor < _entities.Length || _blocks != null || _prune != null;

        private void StartPass(long now)
        {
            _entities = MyEntities.GetEntities().ToArray();
            _entityCursor = 0;
            _blocks = null;
            _passStarted = now;
            _inventories = 0;
            _writtenThisPass = 0;
        }

        private void EndPass()
        {
            LastPassInventories = _inventories;
            LastPassWritten = _writtenThisPass;
            _orphans = InventoryLedger.Current?.TakeOrphans() ?? new Dictionary<(long, int), InventoryLedger.Pending>();
            _prune = new List<(long, int)>(_seen.Keys.Union(_orphans.Keys));
            _pruneCursor = 0;
        }

        private Dictionary<(long, int), InventoryLedger.Pending> _orphans = new Dictionary<(long, int), InventoryLedger.Pending>();

        /// <summary>The last row of an inventory whose entity is gone: no items, and what it held leaving as "gone".</summary>
        private void Gone((long Entity, int Inv) key, Seen seen, InventoryLedger.Pending pending, long now)
        {
            if (seen == null && pending == null) return;
            var flows = pending?.Flows ?? new FlowSet();
            var held = new Dictionary<string, long>();
            if (seen != null) foreach (var pair in seen.Items) held[pair.Key] = pair.Value;
            foreach (var pair in flows.NetByItem())
            {
                held.TryGetValue(pair.Key, out var was);
                held[pair.Key] = was + pair.Value;
            }
            foreach (var pair in held) flows.Add("gone", pair.Key, -pair.Value);
            _recorder.Store.Add(new Row(Table.Inventories, now, now, key.Entity, key.Inv, seen == null || seen.Grid == 0 ? null : (object)seen.Grid,
                seen?.Owner ?? 0, null, 0.0, 0.0, flows.Encode()));
        }

        private void Visit(MyEntity owner, long gridId, long ownerIdentity, long now)
        {
            // the web view shows a block by its name; for a block, "owner" of the name is its grid
            if (owner is MyCubeBlock named) _recorder.Name(named.EntityId, "block", named.DisplayNameText, gridId);
            for (var i = 0; i < owner.InventoryCount; i++)
            {
                if (!(owner.GetInventory(i) is MyInventory inventory)) continue;
                _inventories++;
                _stacks.Clear();
                foreach (var item in inventory.GetItems())
                {
                    var id = item.Content.GetId();
                    _stacks.Add(new ItemStack(InventoryCodec.ShortType(id.TypeId.ToString()), id.SubtypeName, item.Amount.RawValue));
                }
                long gasRaw = 0;
                if (i == 0 && owner is MyGasTank tank)
                {
                    if (Invariants.TankOverfilled(tank.FilledRatio))
                        _recorder.Alert("tank_overfilled", ownerIdentity, owner.EntityId,
                            $"{tank.CustomName} on {tank.CubeGrid.DisplayName}: filled {tank.FilledRatio:0.####}");
                    gasRaw = (long)(tank.FilledRatio * tank.Capacity * 1_000_000);
                    _stacks.Add(new ItemStack("Gas", tank.BlockDefinition.StoredGasId.SubtypeName, gasRaw));
                }
                Check(owner, inventory, ownerIdentity);

                var items = new Dictionary<string, long>();
                foreach (var stack in _stacks)
                {
                    if (stack.Type == "Gas") continue;
                    var name = stack.Type + "/" + stack.Subtype;
                    items.TryGetValue(name, out var was);
                    items[name] = was + stack.Raw;
                }
                var key = (owner.EntityId, i);
                _seen.TryGetValue(key, out var seen);
                var pending = InventoryLedger.Current?.Take(inventory);
                var flows = pending?.Flows;
                if (seen != null || pending?.Born == true) Balance(owner, ownerIdentity, seen?.Items, items, ref flows);

                var hash = InventoryCodec.Hash(_stacks, ownerIdentity ^ (gridId << 1));
                var changed = seen == null || seen.Hash != hash || seen.Day != _day || (flows != null && !flows.IsEmpty);
                if (seen == null) _seen[key] = seen = new Seen();
                seen.Items = items;
                seen.Owner = ownerIdentity;
                seen.Grid = gridId;
                if (!changed) continue;
                seen.Hash = hash;
                seen.Day = _day;
                _writtenThisPass++;
                _recorder.Store.Add(new Row(Table.Inventories, now, now, owner.EntityId, i, gridId == 0 ? null : (object)gridId,
                    ownerIdentity, InventoryCodec.Encode(_stacks), (double)inventory.CurrentVolume, (double)inventory.MaxVolume, flows?.Encode()));
            }
        }

        /// <summary>
        /// What the inventory holds against what it held plus the flows since: anything else changed it past
        /// every hook. That goes into the flows as "unexplained", and a gain is an alert.
        /// </summary>
        private void Balance(MyEntity owner, long ownerIdentity, Dictionary<string, long> before, Dictionary<string, long> now, ref FlowSet flows)
        {
            if (!LedgerPatches.Booking || InventoryLedger.Current == null) return;
            var unexplained = LedgerMath.Unexplained(before ?? new Dictionary<string, long>(), now, flows);
            if (unexplained.Count == 0) return;
            if (flows == null) flows = new FlowSet();
            foreach (var pair in unexplained) flows.Add("unexplained", pair.Key, pair.Value);
            if (unexplained.Any(p => p.Value > LedgerMath.Tolerance(p.Value)))
                _recorder.Alert("bypass", ownerIdentity, owner.EntityId,
                    $"{Describe(owner)} changed past the ledger: {LedgerMath.Describe(unexplained)}");
        }

        private void Check(MyEntity owner, MyInventory inventory, long ownerIdentity)
        {
            foreach (var stack in _stacks)
            {
                if (stack.Type == "Gas") continue;
                var bad = Invariants.BadAmount(stack.Type, stack.Raw);
                if (bad != null)
                    _recorder.Alert("bad_amount", ownerIdentity, owner.EntityId,
                        $"{Describe(owner)}: {stack.Type}/{stack.Subtype} {stack.Amount} ({bad})");
            }
            if (Invariants.Overfilled((double)inventory.CurrentVolume, (double)inventory.MaxVolume))
                _recorder.Alert("overfilled", ownerIdentity, owner.EntityId,
                    $"{Describe(owner)}: volume {(double)inventory.CurrentVolume:0.###} of {(double)inventory.MaxVolume:0.###}");
        }

        private static string Describe(MyEntity owner) =>
            owner is MyCubeBlock block ? block.DisplayNameText + " on " + block.CubeGrid.DisplayName : owner.DisplayName ?? owner.GetType().Name;
    }
}

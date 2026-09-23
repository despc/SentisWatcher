using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SentisWatcher.Recording;
using VRage.Game;
using VRage.Game.Entity;

namespace SentisWatcher.Ledger
{
    /// <summary>
    /// The flows of every inventory since the sweep last saw it: the hooks write them from any thread, the
    /// sweep takes them when it visits the inventory and checks them against what the inventory holds. Also
    /// gathers the items that came from plugins, mods or unknown paths, for one alert per owner and source.
    /// </summary>
    public sealed class InventoryLedger
    {
        /// <summary>Null while nothing is recorded; every hook checks it first.</summary>
        public static InventoryLedger Current;

        public sealed class Pending
        {
            public readonly FlowSet Flows = new FlowSet();
            public bool Born;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<MyInventory, Pending> _pending = new Dictionary<MyInventory, Pending>();
        private readonly Dictionary<(string Alert, long Owner, string Source), Suspect> _suspects = new Dictionary<(string, long, string), Suspect>();

        private sealed class Suspect
        {
            public readonly FlowSet Items = new FlowSet();
            public readonly HashSet<long> Entities = new HashSet<long>();
            public long FirstEntity;
            public string Where;
        }

        public void Record(MyInventory inventory, string kind, string item, long raw)
        {
            lock (_lock)
            {
                if (!_pending.TryGetValue(inventory, out var pending)) _pending[inventory] = pending = new Pending();
                pending.Flows.Add(kind, item, raw);
            }
        }

        /// <summary>The inventory was just made (its items are loaded now): it had nothing before.</summary>
        public void MarkBorn(MyInventory inventory)
        {
            lock (_lock)
            {
                if (!_pending.TryGetValue(inventory, out var pending)) _pending[inventory] = pending = new Pending();
                pending.Born = true;
            }
        }

        /// <summary>The flows since the last visit, forgotten here (the sweep, game thread).</summary>
        public Pending Take(MyInventory inventory)
        {
            lock (_lock)
            {
                if (!_pending.TryGetValue(inventory, out var pending)) return null;
                _pending.Remove(inventory);
                return pending;
            }
        }

        /// <summary>The flows of inventories whose entity is gone, by entity and inventory index.</summary>
        public Dictionary<(long, int), Pending> TakeOrphans()
        {
            var result = new Dictionary<(long, int), Pending>();
            lock (_lock)
            {
                foreach (var pair in _pending.Where(p => !(p.Key.Entity is MyEntity e) || e.Closed || e.MarkedForClose).ToList())
                {
                    _pending.Remove(pair.Key);
                    if (!(pair.Key.Entity is MyEntity entity)) continue;
                    var key = (entity.EntityId, IndexOf(entity, pair.Key));
                    if (result.TryGetValue(key, out var was)) was.Flows.Add(pair.Value.Flows);
                    else result[key] = pair.Value;
                }
            }
            return result;
        }

        /// <summary>The entities whose inventories have flows not yet written.</summary>
        public List<MyEntity> PendingEntities()
        {
            lock (_lock) return _pending.Keys.Select(k => k.Entity as MyEntity).Where(e => e != null).Distinct().ToList();
        }

        public int PendingCount
        {
            get { lock (_lock) return _pending.Count; }
        }

        public static int IndexOf(MyEntity entity, MyInventory inventory)
        {
            for (var i = 0; i < entity.InventoryCount; i++)
                if (ReferenceEquals(entity.GetInventory(i), inventory)) return i;
            return 0;
        }

        // ------------------------------------------------------------------ items from outside the game's rules

        /// <summary>
        /// Items that came into a player's inventory from a plugin, a mod, a script or a path the ledger does
        /// not know: kept until <see cref="FlushSuspects"/> writes one alert per owner and source.
        /// </summary>
        public void Suspicious(string kind, MyInventory inventory, string item, long raw)
        {
            var source = kind.StartsWith("load:") ? kind.Substring(5) : kind;
            string alert;
            if (source.StartsWith("plugin:") || source.StartsWith("mod:") || source == "script") alert = "external_source";
            else if (source.StartsWith("unknown:")) alert = "unknown_source";
            else return;
            if (!(inventory.Entity is MyEntity entity)) return;
            var owner = Identities.InventoryOwner(entity);
            if (!Identities.IsPlayer(owner)) return;
            lock (_lock)
            {
                var key = (alert, owner, kind);
                if (!_suspects.TryGetValue(key, out var suspect))
                {
                    _suspects[key] = suspect = new Suspect { FirstEntity = entity.EntityId, Where = Describe(entity) };
                }
                suspect.Items.Add(kind, item, raw);
                suspect.Entities.Add(entity.EntityId);
            }
        }

        /// <summary>One alert for each owner and source since the last call (game thread, every frame).</summary>
        public void FlushSuspects(Recorder recorder)
        {
            List<KeyValuePair<(string Alert, long Owner, string Source), Suspect>> done;
            lock (_lock)
            {
                if (_suspects.Count == 0) return;
                done = _suspects.ToList();
                _suspects.Clear();
            }
            foreach (var pair in done)
            {
                var (alert, owner, source) = pair.Key;
                var items = LedgerMath.Describe(pair.Value.Items.NetByItem().Where(p => p.Value != 0));
                var more = pair.Value.Entities.Count > 1 ? " and " + (pair.Value.Entities.Count - 1) + " more inventories" : "";
                recorder.Alert(alert, owner, pair.Value.FirstEntity,
                    $"{source} gave {Identities.NameOf(owner)}: {items} ({pair.Value.Where}{more})",
                    limitBy: ((long)alert.GetHashCode() << 32) ^ owner ^ source.GetHashCode());
            }
        }

        public static string Describe(MyEntity entity) =>
            entity is MyCubeBlock block ? block.DisplayNameText + " on " + block.CubeGrid.DisplayName : entity.DisplayName ?? entity.GetType().Name;
    }

    /// <summary>"Ore/Iron" for an item, as the day files name it; cached, the hooks ask often.</summary>
    public static class ItemKeys
    {
        private static readonly ConcurrentDictionary<MyDefinitionId, string> Names = new ConcurrentDictionary<MyDefinitionId, string>();

        public static string Of(MyDefinitionId id) =>
            Names.GetOrAdd(id, i => InventoryCodec.ShortType(i.TypeId.ToString()) + "/" + i.SubtypeName);
    }
}

using System;
using System.Reflection;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SentisWatcher.Storage;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.Network;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// What moves items between grids with no player's hand in it, and what joins the grids for it:
    ///
    ///  - conveyor_out / conveyor_in: items the conveyors moved from a block of one grid to a block of another one not
    ///    joined to it by a rotor, a hinge or a piston - a docked ship emptied by a sorter, an inventory script or an
    ///    assembler pulling from it. One row at the source and one at the destination (each grid's events show it),
    ///    summed over <see cref="ConveyorTally.WindowMs"/>; who started it: a script (its programmable block and owner),
    ///    a sorter, or the conveyors. A player's own move is the transfer event and is not counted again;
    ///  - connect / disconnect: a connector locked to or let go of another grid's, with who did it (a player, a
    ///    script) or "auto" (locked by itself, the grid closed, a timer).
    ///
    /// The ledger books these moves as transfer@grid, with no time finer than its 5 minute sweep and no one behind them.
    /// </summary>
    [PatchShim]
    public static class ConveyorLog
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static readonly ConveyorTally Tally = new ConveyorTally();

        public static void Patch(PatchContext ctx)
        {
            PatchGuard.Run("SentisWatcher.Conveyor", ctx, c =>
            {
                var transfer = Method(typeof(MyInventory), "TransferItemsInternal");
                c.GetPattern(transfer).Prefixes.Add(Method(typeof(ConveyorLog), nameof(TransferPrefix)));
                c.GetPattern(transfer).Suffixes.Add(Method(typeof(ConveyorLog), nameof(TransferSuffix)));
            });
            PatchGuard.Run("SentisWatcher.Conveyor.Script", ctx, c =>
            {
                // every run of a script (Main, the constructor, Save) goes through it
                var run = Method(typeof(MyProgrammableBlock), "RunSandboxedProgramActionCore");
                c.GetPattern(run).Prefixes.Add(Method(typeof(ConveyorLog), nameof(ScriptPrefix)));
                c.GetPattern(run).Suffixes.Add(Method(typeof(ConveyorLog), nameof(StartedSuffix)));
            });
            PatchGuard.Run("SentisWatcher.Conveyor.Sorter", ctx, c =>
            {
                var update = Method(typeof(MyConveyorSorter), nameof(MyConveyorSorter.UpdateAfterSimulation10));
                c.GetPattern(update).Prefixes.Add(Method(typeof(ConveyorLog), nameof(SorterPrefix)));
                c.GetPattern(update).Suffixes.Add(Method(typeof(ConveyorLog), nameof(StartedSuffix)));
            });
            PatchGuard.Run("SentisWatcher.Connector", ctx, c =>
            {
                // once a pair, not the setter of Connected (called for each connector, and as a world loads):
                // WeldInternal locks the pair (on the master), RemoveLinks takes a locked pair's conveyors apart
                c.GetPattern(Method(typeof(MyShipConnector), "WeldInternal")).Suffixes.Add(Method(typeof(ConveyorLog), nameof(WeldSuffix)));
                c.GetPattern(Method(typeof(MyShipConnector), "RemoveLinks")).Prefixes.Add(Method(typeof(ConveyorLog), nameof(RemoveLinksPrefix)));
            });
        }

        private static MethodInfo Method(Type type, string name) =>
            type.GetMethod(name, Any) ?? throw new MissingMethodException(type.Name + "." + name);

        // ------------------------------------------------------------------ who started it

        [ThreadStatic] private static MyCubeBlock _startedBy;

        private static void ScriptPrefix(MyProgrammableBlock __instance) => _startedBy = __instance;
        private static void SorterPrefix(MyConveyorSorter __instance) => _startedBy = __instance;
        private static void StartedSuffix() => _startedBy = null;

        private static long Sender
        {
            get
            {
                var context = MyEventContext.Current;
                return context.IsLocallyInvoked || context.Sender.Value == 0 ? 0 : Identities.OfSteam(context.Sender.Value);
            }
        }

        /// <summary>Who did it, and "script ..." / "sorter ..." / the fallback for the detail.</summary>
        private static (long actor, string how) StartedBy(string fallback)
        {
            var block = _startedBy;
            if (block != null)
            {
                var what = block is MyProgrammableBlock ? "script" : "sorter";
                return (block.OwnerId, $"{what} \"{InventorySweep.BlockName(block)}\" on {block.CubeGrid.DisplayName} (owner {Identities.NameOf(block.OwnerId)})");
            }
            var player = Sender;
            return player != 0 ? (player, "player " + Identities.NameOf(player)) : (0, fallback);
        }

        // ------------------------------------------------------------------ conveyor

        private struct Pending
        {
            public MyCubeBlock Source, Destination;
            public MyDefinitionId Item;
        }

        [ThreadStatic] private static Pending _pending;

        private static void TransferPrefix(MyInventory src, MyInventory dst, uint srcItemId)
        {
            _pending = default;
            if (Recorder.Current == null || ReferenceEquals(src, dst) || RecordingPatches.InPlayerTransfer) return;
            if (!(src.Entity is MyCubeBlock from) || !(dst.Entity is MyCubeBlock to) || from.CubeGrid == to.CubeGrid) return;
            try
            {
                // a rotor's, a hinge's or a piston's head is the same ship
                var mechanical = MyCubeGridGroups.Static?.Mechanical;
                var group = mechanical?.GetGroup(from.CubeGrid);
                if (group != null && group == mechanical.GetGroup(to.CubeGrid)) return;
                foreach (var item in src.GetItems())
                    if (item.ItemId == srcItemId)
                    {
                        _pending = new Pending { Source = from, Destination = to, Item = item.Content.GetId() };
                        return;
                    }
            }
            catch (Exception)
            {
                _pending = default;
            }
        }

        private static void TransferSuffix(MyFixedPoint __result)
        {
            var pending = _pending;
            _pending = default;
            if (pending.Source != null && __result > 0) Count(pending, (double)__result);
        }

        private static void Count(Pending pending, double amount) => Recorder.Safe("conveyor", () =>
        {
            var item = InventoryCodec.ShortType(pending.Item.TypeId.ToString()) + "/" + pending.Item.SubtypeName;
            var (actor, how) = StartedBy("conveyor");
            var from = pending.Source;
            var to = pending.Destination;
            Tally.Add(Clock.Now, from.EntityId, to.EntityId, actor, item + "|" + how,
                () => $"{item} from={from.EntityId} \"{InventorySweep.BlockName(from)}\" on {from.CubeGrid.DisplayName} " +
                      $"to={to.EntityId} \"{InventorySweep.BlockName(to)}\" on {to.CubeGrid.DisplayName} by {how}",
                amount, from.PositionComp.GetPosition());
        });

        /// <summary>The conveyor moves of a window that ended, as events (game thread, every frame).</summary>
        public static void Flush(Recorder recorder, bool force)
        {
            var done = Tally.Take(Clock.Now, force);
            if (done == null) return;
            foreach (var m in done)
            {
                recorder.Store.Add(new Row(Table.Events, m.First, m.First, "conveyor_out", m.Actor, m.Source,
                    m.Position.X, m.Position.Y, m.Position.Z, m.Amount, m.Count, m.Detail));
                recorder.Store.Add(new Row(Table.Events, m.First, m.First, "conveyor_in", m.Actor, m.Destination,
                    m.Position.X, m.Position.Y, m.Position.Z, m.Amount, m.Count, m.Detail));
            }
        }

        // ------------------------------------------------------------------ connectors

        private static void WeldSuffix(MyShipConnector __instance) => Connection(__instance, __instance.Other, true);

        private static void RemoveLinksPrefix(MyShipConnector __instance, MyShipConnector otherConnector) => Connection(__instance, otherConnector, false);

        /// <summary>A pair of connectors locked or let go: an event at each (each grid's events show it).</summary>
        private static void Connection(MyShipConnector one, MyShipConnector other, bool locked) => Recorder.Safe("connector", () =>
        {
            if (other == null || other.CubeGrid == one.CubeGrid || !Warmup.Loaded) return;
            var (actor, how) = StartedBy("auto");
            Write(one, other, locked, actor, how);
            Write(other, one, locked, actor, how);
        });

        private static void Write(MyShipConnector connector, MyShipConnector other, bool locked, long actor, string how) =>
            Recorder.Current.Event(locked ? "connect" : "disconnect", actor, connector.EntityId, connector.PositionComp.GetPosition(),
                detail: $"\"{InventorySweep.BlockName(connector)}\" on {connector.CubeGrid.DisplayName} " +
                        $"{(locked ? "to" : "from")} \"{InventorySweep.BlockName(other)}\" on {other.CubeGrid.DisplayName} " +
                        $"other={other.EntityId} grid={other.CubeGrid.EntityId} by {how}");
    }
}

using System;
using System.Linq;
using System.Reflection;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using SentisWatcher.Anomalies;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Network;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// What the players do that the game has no event for: moving items between inventories (and the check
    /// that a move makes nothing), dropping items, pasting grids, money, welding, handing grids over.
    /// Every hook does nothing while nothing is recorded.
    /// </summary>
    [PatchShim]
    public static class RecordingPatches
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Patch(PatchContext ctx)
        {
            PatchGuard.Run("SentisWatcher.Transfer", ctx, c =>
            {
                var transfer = Method(typeof(MyInventory), "InventoryTransferItem_Implementation");
                c.GetPattern(transfer).Prefixes.Add(Method(typeof(RecordingPatches), nameof(TransferPrefix)));
                c.GetPattern(transfer).Suffixes.Add(Method(typeof(RecordingPatches), nameof(TransferSuffix)));
            });
            PatchGuard.Run("SentisWatcher.Drop", ctx, c =>
            {
                var drop = Method(typeof(MyInventory), "DropItem_Implementation");
                c.GetPattern(drop).Prefixes.Add(Method(typeof(RecordingPatches), nameof(DropPrefix)));
            });
            PatchGuard.Run("SentisWatcher.Paste", ctx, c =>
            {
                var paste = Method(typeof(MyCubeGrid), "TryPasteGrid_Implementation");
                c.GetPattern(paste).Prefixes.Add(Method(typeof(RecordingPatches), nameof(PastePrefix)));
            });
            PatchGuard.Run("SentisWatcher.Balance", ctx, c =>
            {
                var balance = typeof(MyBankingSystem).GetMethod(nameof(MyBankingSystem.ChangeBalance), BindingFlags.Static | BindingFlags.Public,
                    null, new[] { typeof(long), typeof(long) }, null) ?? throw new MissingMethodException("MyBankingSystem.ChangeBalance");
                c.GetPattern(balance).Suffixes.Add(Method(typeof(RecordingPatches), nameof(BalanceSuffix)));
            });
            PatchGuard.Run("SentisWatcher.Weld", ctx, c =>
            {
                var weld = typeof(MySlimBlock).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                               .FirstOrDefault(m => m.Name == nameof(MySlimBlock.IncreaseMountLevel) && m.GetParameters().Length > 2 &&
                                                    m.GetParameters()[1].Name == "welderOwnerIdentId")
                           ?? throw new MissingMethodException("MySlimBlock.IncreaseMountLevel(welderOwnerIdentId)");
                c.GetPattern(weld).Suffixes.Add(Method(typeof(RecordingPatches), nameof(WeldSuffix)));
            });
            PatchGuard.Run("SentisWatcher.Ownership", ctx, c =>
            {
                var owner = Method(typeof(MyCubeGrid), nameof(MyCubeGrid.ChangeGridOwnership));
                c.GetPattern(owner).Suffixes.Add(Method(typeof(RecordingPatches), nameof(OwnershipSuffix)));
            });
        }

        private static MethodInfo Method(Type type, string name) =>
            type.GetMethod(name, Any) ?? throw new MissingMethodException(type.Name + "." + name);

        private static long Sender => MyEventContext.Current.IsLocallyInvoked ? 0 : Identities.OfSteam(MyEventContext.Current.Sender.Value);

        // ------------------------------------------------------------------ item transfer

        private struct TransferState
        {
            public bool Active;
            public MyDefinitionId Item;
            public MyInventory Target;
            public long SourceBefore, TargetBefore;
        }

        [ThreadStatic] private static TransferState _transfer;

        private static void TransferPrefix(MyInventory __instance, uint itemId, long destinationOwnerId, byte destInventoryIndex)
        {
            _transfer = default;
            if (Recorder.Current == null) return;
            try
            {
                var item = __instance.GetItemByID(itemId);
                if (!item.HasValue) return;
                var target = MyEntities.TryGetEntityById(destinationOwnerId, out var owner) ? owner.GetInventory(destInventoryIndex) as MyInventory : null;
                if (target == null) return;
                var id = item.Value.Content.GetId();
                _transfer = new TransferState
                {
                    Active = true,
                    Item = id,
                    Target = target,
                    SourceBefore = __instance.GetItemAmount(id).RawValue,
                    TargetBefore = target.GetItemAmount(id).RawValue,
                };
            }
            catch (Exception)
            {
                _transfer = default;
            }
        }

        private static void TransferSuffix(MyInventory __instance) => Recorder.Safe("transfer", () =>
        {
            var state = _transfer;
            _transfer = default;
            if (!state.Active) return;
            var sourceAfter = __instance.GetItemAmount(state.Item).RawValue;
            var targetAfter = state.Target.GetItemAmount(state.Item).RawValue;
            var same = state.Target == __instance;
            var actor = Sender;
            var from = __instance.Owner;
            var to = state.Target.Owner;
            var item = InventoryCodec.ShortType(state.Item.TypeId.ToString()) + "/" + state.Item.SubtypeName;
            var moved = (state.SourceBefore - sourceAfter) / 1_000_000.0;
            Recorder.Current.Event("transfer", actor, to?.EntityId ?? 0, from?.PositionComp.GetPosition(), moved,
                detail: $"{item} from={from?.EntityId} ({Identities.OfEntity(from)}) to={to?.EntityId} ({Identities.OfEntity(to)})");
            if (Invariants.TransferMadeItems(state.SourceBefore, state.TargetBefore, sourceAfter, targetAfter, same))
                Recorder.Current.Alert("dupe_transfer", actor, to?.EntityId ?? 0,
                    $"{Identities.NameOf(actor)} moved {item}: source {state.SourceBefore / 1e6}->{sourceAfter / 1e6}, " +
                    $"target {state.TargetBefore / 1e6}->{targetAfter / 1e6} ({from?.DisplayName} -> {to?.DisplayName})");
        });

        // ------------------------------------------------------------------ drop

        private static void DropPrefix(MyInventory __instance, MyFixedPoint amount, uint itemIndex) => Recorder.Safe("drop", () =>
        {
            var item = __instance.GetItemByID(itemIndex);
            if (!item.HasValue) return;
            var id = item.Value.Content.GetId();
            var owner = __instance.Owner;
            Recorder.Current.Event("drop", Sender, owner?.EntityId ?? 0, owner?.PositionComp.GetPosition(), (double)amount,
                detail: InventoryCodec.ShortType(id.TypeId.ToString()) + "/" + id.SubtypeName);
        });

        // ------------------------------------------------------------------ paste

        private static void PastePrefix(MyCubeGrid.MyPasteGridParameters parameters) => Recorder.Safe("paste", () =>
        {
            var steam = MyEventContext.Current.IsLocallyInvoked ? 0UL : MyEventContext.Current.Sender.Value;
            var actor = Identities.OfSteam(steam);
            var grids = parameters.Entities;
            if (grids == null || grids.Count == 0) return;
            var blocks = grids.Sum(g => g.CubeBlocks?.Count ?? 0);
            var names = string.Join(", ", grids.Select(g => g.DisplayName)).Short(300);
            var level = steam == 0 ? MyPromoteLevel.Owner : MySession.Static.GetUserPromoteLevel(steam);
            var creative = MySession.Static.CreativeMode || (steam != 0 && MySession.Static.CreativeToolsEnabled(steam));
            var at = grids[0].PositionAndOrientation?.Position;
            Recorder.Current.Event("paste", actor, 0, at.HasValue ? (VRageMath.Vector3D)at.Value : (VRageMath.Vector3D?)null, count: blocks,
                detail: names + " | " + level + (creative ? " creative" : ""));
            if (level < MyPromoteLevel.Admin && !creative)
                Recorder.Current.Alert("paste_by_player", actor, 0, $"{Identities.NameOf(actor)} ({level}) pasted {blocks} blocks: {names}");
        });

        // ------------------------------------------------------------------ money, welding, ownership

        private static void BalanceSuffix(long identifierId, long amount, bool __result) => Recorder.Safe("balance", () =>
        {
            if (__result) Recorder.Current.Event("balance", identifierId, 0, amount: amount);
        });

        private static void WeldSuffix(MySlimBlock __instance, float welderMountAmount, long welderOwnerIdentId) => Recorder.Safe("weld", () =>
            Recorder.Current.Aggregator.Add(Storage.Clock.Now, "weld", welderOwnerIdentId, __instance.CubeGrid.EntityId, null,
                welderMountAmount, __instance.CubeGrid.GridIntegerToWorld(__instance.Position)));

        private static void OwnershipSuffix(MyCubeGrid __instance, long playerId, MyOwnershipShareModeEnum shareMode) => Recorder.Safe("ownership", () =>
            Recorder.Current.Event("grid_ownership", playerId, __instance.EntityId, __instance.PositionComp.GetPosition(),
                count: __instance.BlocksCount, detail: __instance.DisplayName + " share " + shareMode + " by " + Sender));
    }
}

using System;
using System.Collections.Generic;
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
            PatchGuard.Run("SentisWatcher.Drill", ctx, c =>
            {
                // a hand drill's cut and a ship drill's both end here, with what came out of the ground
                var results = Method(typeof(Sandbox.Game.Weapons.MyDrillBase), "OnDrillResults");
                c.GetPattern(results).Suffixes.Add(Method(typeof(RecordingPatches), nameof(DrillSuffix)));
            });
            PatchGuard.Run("SentisWatcher.Weld", ctx, c =>
            {
                var weld = typeof(MySlimBlock).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                               .FirstOrDefault(m => m.Name == nameof(MySlimBlock.IncreaseMountLevel) && m.GetParameters().Length > 2 &&
                                                    m.GetParameters()[1].Name == "welderOwnerIdentId")
                           ?? throw new MissingMethodException("MySlimBlock.IncreaseMountLevel(welderOwnerIdentId)");
                c.GetPattern(weld).Suffixes.Add(Method(typeof(RecordingPatches), nameof(WeldSuffix)));
            });
            PatchGuard.Run("SentisWatcher.Jump", ctx, c =>
            {
                var jump = Method(typeof(Sandbox.Game.GameSystems.MyGridJumpDriveSystem), "PerformJump");
                c.GetPattern(jump).Prefixes.Add(Method(typeof(RecordingPatches), nameof(JumpPrefix)));
                c.GetPattern(jump).Suffixes.Add(Method(typeof(RecordingPatches), nameof(JumpSuffix)));
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

        // ------------------------------------------------------------------ jumps

        private static readonly Func<Sandbox.Game.GameSystems.MyGridJumpDriveSystem, MyCubeGrid> JumpGrid =
            (Func<Sandbox.Game.GameSystems.MyGridJumpDriveSystem, MyCubeGrid>)Delegate.CreateDelegate(
                typeof(Func<Sandbox.Game.GameSystems.MyGridJumpDriveSystem, MyCubeGrid>),
                typeof(Sandbox.Game.GameSystems.MyUpdateableGridSystem).GetProperty("Grid", Any).GetGetMethod(true));
        private static readonly FieldInfo JumpUser = typeof(Sandbox.Game.GameSystems.MyGridJumpDriveSystem).GetField("m_userId", Any);

        [ThreadStatic] private static VRageMath.Vector3D? _jumpFrom;

        private static void JumpPrefix(Sandbox.Game.GameSystems.MyGridJumpDriveSystem __instance)
        {
            _jumpFrom = null;
            if (Recorder.Current == null) return;
            try { _jumpFrom = JumpGrid(__instance)?.WorldMatrix.Translation; }
            catch (Exception) { }
        }

        /// <summary>
        /// A jump: one event about the grid (who asked for it, from where, to where, how far), and one for every
        /// player sitting in a seat of the grid or of the grids joined to it, who went along.
        /// </summary>
        private static void JumpSuffix(Sandbox.Game.GameSystems.MyGridJumpDriveSystem __instance) => Recorder.Safe("jump", () =>
        {
            var from = _jumpFrom;
            _jumpFrom = null;
            var grid = JumpGrid(__instance);
            if (!from.HasValue || grid == null) return;
            var to = grid.WorldMatrix.Translation;
            var distance = VRageMath.Vector3D.Distance(from.Value, to);
            var user = JumpUser?.GetValue(__instance) is long u ? u : 0;
            var target = string.Format(System.Globalization.CultureInfo.InvariantCulture, "to={0:0};{1:0};{2:0}", to.X, to.Y, to.Z);
            Recorder.Current.Event("jump", user, grid.EntityId, from, distance, detail: target + " " + grid.DisplayName);
            var group = new HashSet<MyCubeGrid>(MyCubeGridGroups.Static.Logical.GetGroup(grid)?.Nodes.Select(n => n.NodeData) ?? new[] { grid });
            // aboard: a character in a seat of the group, or a player whose controls are a block of it (a remote
            // control does not count: its user stays where he is)
            var aboard = new HashSet<long>();
            foreach (var member in group)
                foreach (var seat in member.GetFatBlocks().OfType<Sandbox.Game.Entities.MyShipController>())
                {
                    var pilot = seat.Pilot?.GetPlayerIdentityId() ?? 0;
                    if (pilot != 0) aboard.Add(pilot);
                }
            foreach (var player in MySession.Static.Players.GetOnlinePlayers())
                if (player.Controller?.ControlledEntity?.Entity is MyCubeBlock block && !(block is Sandbox.Game.Entities.MyRemoteControl) &&
                    group.Contains(block.CubeGrid) && player.Identity != null)
                    aboard.Add(player.Identity.IdentityId);
            foreach (var passenger in aboard)
                Recorder.Current.Event("jump_passenger", passenger, grid.EntityId, from, distance, detail: target + " " + grid.DisplayName);
        });

        // ------------------------------------------------------------------ money, welding, ownership

        private static void BalanceSuffix(long identifierId, long amount, bool __result) => Recorder.Safe("balance", () =>
        {
            if (__result) Recorder.Current.Event("balance", identifierId, 0, amount: amount);
        });

        private static void WeldSuffix(MySlimBlock __instance, float welderMountAmount, long welderOwnerIdentId) => Recorder.Safe("weld", () =>
            Recorder.Current.Aggregator.Add(Storage.Clock.Now, "weld", welderOwnerIdentId, __instance.CubeGrid.EntityId, null,
                welderMountAmount, __instance.CubeGrid.GridIntegerToWorld(__instance.Position)));

        private static readonly FieldInfo DrillEntity = typeof(Sandbox.Game.Weapons.MyDrillBase).GetField("m_drillEntity", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Voxels drilled: by whom (the hand drill's holder, a ship drill's owner), on what (the character, the
        /// grid), the ore that most of it was, and how much ground in cubic metres; the drill's secondary
        /// action (clearing, nothing kept) is marked as such.
        /// </summary>
        private static void DrillSuffix(Sandbox.Game.Weapons.MyDrillBase __instance, Dictionary<VRage.Game.MyVoxelMaterialDefinition, int> materials,
            VRageMath.Vector3D hitPosition, bool collectOre) => Recorder.Safe("drill", () =>
        {
            if (materials == null || materials.Count == 0) return;
            var removed = 0;
            var most = 0;
            string ore = null;
            foreach (var material in materials)
            {
                removed += material.Value;
                if (material.Value <= most) continue;
                most = material.Value;
                ore = string.IsNullOrEmpty(material.Key.MinedOre) ? material.Key.Id.SubtypeName : material.Key.MinedOre;
            }
            if (removed <= 0) return;
            long actor = 0, entity = 0;
            switch (DrillEntity?.GetValue(__instance))
            {
                case Sandbox.Game.Weapons.MyHandDrill hand:
                    actor = hand.OwnerIdentityId;
                    entity = hand.Owner?.EntityId ?? 0;
                    break;
                case MyCubeBlock block:
                    actor = block.OwnerId;
                    entity = block.CubeGrid.EntityId;
                    break;
            }
            Recorder.Current.Aggregator.Add(Storage.Clock.Now, "drill", actor, entity, (collectOre ? "" : "clearing ") + ore, removed / 255.0, hitPosition);
        });

        private static void OwnershipSuffix(MyCubeGrid __instance, long playerId, MyOwnershipShareModeEnum shareMode) => Recorder.Safe("ownership", () =>
            Recorder.Current.Event("grid_ownership", playerId, __instance.EntityId, __instance.PositionComp.GetPosition(),
                count: __instance.BlocksCount, detail: __instance.DisplayName + " share " + shareMode + " by " + Sender));
    }
}

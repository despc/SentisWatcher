using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NLog;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Inventory;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SentisWatcher.Recording;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.Game.Entity;

namespace SentisWatcher.Ledger
{
    /// <summary>
    /// The inventory ledger's hooks. Every change of an inventory's items goes through a few methods of
    /// MyInventory (add, remove, the direct edits of ApplyChanges and UpdateItem, a silent clear); they book
    /// it under the open context (<see cref="Contexts"/>), or read the call stack when there is none. The
    /// methods that make, use or move items open the contexts, and closing one checks it: a transfer makes
    /// nothing, production takes what the blueprint asks, a pickup takes no more than lay there, a grinder
    /// gets no more out of a block than it can hold. Everything does nothing while nothing is recorded.
    /// </summary>
    [PatchShim]
    public static class LedgerPatches
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Patch(PatchContext ctx)
        {
            // the changes themselves: all of them or none, a ledger that misses one path only raises false alarms
            PatchGuard.Run("SentisWatcher.Ledger", ctx, c =>
            {
                var hooks = new[]
                {
                    (Find(typeof(MyInventory), "AddItemsInternal"), nameof(AddPrefix), nameof(AddSuffix)),
                    (Find(typeof(MyInventory), "RemoveItemsInternal"), nameof(RemovePrefix), nameof(RemoveSuffix)),
                    (Find(typeof(MyInventory), nameof(MyInventory.ApplyChanges)), nameof(EditPrefix), nameof(EditSuffix)),
                    (Find(typeof(MyInventory), nameof(MyInventory.UpdateItem)), nameof(EditPrefix), nameof(EditSuffix)),
                    // an item changed in place - a bottle's gas when the suit refills, a datapad's text, a magazine's
                    // rounds: the game takes it out and puts it back. Booked as a whole, it adds up to nothing; as two
                    // steps the putting back was an item "from an unknown source"
                    (Find(typeof(MyInventory), nameof(MyInventory.ModifyContent)), nameof(EditPrefix), nameof(EditSuffix)),
                    (Find(typeof(MyInventory), nameof(MyInventory.ModifyContentForRifle)), nameof(EditPrefix), nameof(EditSuffix)),
                    (Find(typeof(MyInventory), nameof(MyInventory.Clear)), nameof(ClearPrefix), null),
                };
                foreach (var (method, prefix, suffix) in hooks) Hook(c, method, prefix, suffix);
                _booking = true;
            });
            // the contexts, each on its own: without one, its changes are booked by the call stack instead
            Context(ctx, typeof(MyInventory), "TransferItemsInternal", nameof(TransferPrefix), nameof(TransferSuffix));
            Context(ctx, typeof(MyInventory), "InitItems", nameof(LoadPrefix), nameof(LoadSuffix));
            Context(ctx, typeof(MyInventory), nameof(MyInventory.TakeFloatingObject), nameof(TakeFloatingPrefix), nameof(PickupSuffix));
            Context(ctx, typeof(MyInventory), "PickupItem_Implementation", nameof(PickupPrefix), nameof(PickupSuffix));
            Context(ctx, typeof(MyInventory), nameof(MyInventory.RemoveItems), nameof(DropPrefix), nameof(DropSuffix));
            Context(ctx, typeof(MyInventory), "InventoryConsumeItem_Implementation", nameof(ConsumePrefix), nameof(ConsumeSuffix));
            Context(ctx, typeof(MyRefinery), "ChangeRequirementsToResults", nameof(RefinePrefix), nameof(RefineSuffix));
            Context(ctx, typeof(MyAssembler), "FinishAssembling", nameof(AssemblePrefix), nameof(AssembleSuffix));
            Context(ctx, typeof(MyAssembler), "FinishDisassembling", nameof(DisassemblePrefix), nameof(DisassembleSuffix));
            Context(ctx, typeof(MyDrillBase), "TryHarvestOreMaterial", nameof(DrillPrefix), nameof(DrillSuffix));
            Context(ctx, typeof(MySlimBlock), nameof(MySlimBlock.MoveItemsFromConstructionStockpile), nameof(GrindPrefix), nameof(GrindSuffix));
            Context(ctx, typeof(MySlimBlock), nameof(MySlimBlock.MoveUnneededItemsFromConstructionStockpile), nameof(UnneededPrefix), nameof(UnneededSuffix));
            Context(ctx, typeof(MySlimBlock), nameof(MySlimBlock.MoveItemsToConstructionStockpile), nameof(WeldPrefix), nameof(WeldSuffix));
            Context(ctx, typeof(MyComponentCombiner), nameof(MyComponentCombiner.RemoveItemsCombined), nameof(BuildPrefix), nameof(BuildSuffix));
            Context(ctx, typeof(MyCubeGrid), "PasteBlocksToGridServer_Implementation", nameof(BuildPrefix), nameof(BuildSuffix));
            Context(ctx, typeof(MyReactor), "ConsumeFuel", nameof(ConsumePrefix), nameof(ConsumeSuffix));
            Context(ctx, typeof(MyGasGenerator), "ConsumeFuel", nameof(ConsumePrefix), nameof(ConsumeSuffix));
            Context(ctx, typeof(MyGunBase), nameof(MyGunBase.ConsumeMagazine), nameof(ConsumePrefix), nameof(ConsumeSuffix));
            Context(ctx, typeof(MyShipConnector), "TryThrowOutItem", nameof(ThrowPrefix), nameof(ThrowSuffix));

            // SentisOptimisations' own production (the freezer making up for frozen time), when it is loaded
            var optimisations = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                ?.GetType("SentisOptimisationsPlugin.Freezer.FreezerPatches");
            if (optimisations == null) { if (SentisWatcher.SentisWatcherPlugin.Config?.DiagnosticLogs == true) Log.Info("SentisWatcher: SentisOptimisations is not loaded, its production is booked by the call stack"); }
            else
            {
                Context(ctx, optimisations, "ChangeRequirementsToResults", nameof(OptRefinePrefix), nameof(OptRefineSuffix));
                Context(ctx, optimisations, "FinishAssembling", nameof(OptAssemblePrefix), nameof(OptAssembleSuffix));
                Context(ctx, optimisations, "FinishDisassembling", nameof(OptDisassemblePrefix), nameof(OptDisassembleSuffix));
            }
        }

        /// <summary>Whether the changes are hooked: without them the ledger stays off.</summary>
        public static bool Booking => _booking;

        private static bool _booking;

        private static void Context(PatchContext ctx, Type type, string name, string prefix, string suffix) =>
            PatchGuard.Run("SentisWatcher.Ledger." + type.Name + "." + name, ctx, c => Hook(c, Find(type, name), prefix, suffix));

        private static MethodInfo Find(Type type, string name)
        {
            var methods = type.GetMethods(Any).Where(m => m.Name == name && m.DeclaringType == type).ToList();
            if (methods.Count != 1) throw new MissingMethodException(type.Name + "." + name + " (" + methods.Count + " found)");
            return methods[0];
        }

        private static void Hook(PatchContext c, MethodInfo method, string prefix, string suffix)
        {
            var pattern = c.GetPattern(method);
            if (prefix != null) pattern.Prefixes.Add(typeof(LedgerPatches).GetMethod(prefix, Any));
            if (suffix != null) pattern.Suffixes.Add(typeof(LedgerPatches).GetMethod(suffix, Any));
        }

        private static int _failures;

        private static void Failed(string what, Exception e)
        {
            if (++_failures <= 20) Log.Error(e, "SentisWatcher: ledger " + what + " failed");
        }

        // ------------------------------------------------------------------ booking

        [ThreadStatic] private static int _silent;     // inside ApplyChanges/UpdateItem: they are booked as a whole

        /// <summary>Game thread, between frames: whatever a method that threw left open is closed.</summary>
        public static void ResetThread()
        {
            Contexts.Clear();
            _addDepth = _removeDepth = _editDepth = _silent = 0;
        }

        private static void Book(MyInventory inventory, string item, long raw)
        {
            var ledger = InventoryLedger.Current;
            if (ledger == null || raw == 0 || !(inventory.Entity is MyEntity)) return;
            var context = Contexts.Top;
            string kind;
            if (context != null)
            {
                kind = FlowOf(context, inventory);
                context.Tally(inventory, item, raw);
            }
            else kind = raw > 0 ? Sources.Classify() : "other";
            ledger.Record(inventory, kind, item, raw);
            if (raw > 0 && (context == null || context.Kind == "load")) ledger.Suspicious(kind, inventory, item, raw);
        }

        private static string FlowOf(Context context, MyInventory inventory)
        {
            if (context.Kind != "transfer") return context.Flow;
            var other = ReferenceEquals(inventory, context.Dst) ? context.Src : ReferenceEquals(inventory, context.Src) ? context.Dst : null;
            if (other == null || ReferenceEquals(other, inventory)) return "move";
            var theirs = GridOf(other);
            return GridOf(inventory) == theirs ? "move" : TransferTo(theirs);
        }

        private static long GridOf(MyInventory inventory) =>
            inventory.Entity is MyCubeBlock block ? block.CubeGrid.EntityId : (inventory.Entity as MyEntity)?.EntityId ?? 0;

        private static readonly ConcurrentDictionary<long, string> TransferKinds = new ConcurrentDictionary<long, string>();

        private static string TransferTo(long grid)
        {
            if (TransferKinds.Count > 100_000) TransferKinds.Clear();
            return TransferKinds.GetOrAdd(grid, g => "transfer@" + g);
        }

        // ------------------------------------------------------------------ the changes themselves

        [ThreadStatic] private static long[] _adds;
        [ThreadStatic] private static int _addDepth;

        private static void AddPrefix(MyFixedPoint amount)
        {
            if (InventoryLedger.Current == null) return;
            if (_adds == null) _adds = new long[16];
            if (_addDepth < _adds.Length) _adds[_addDepth] = amount.RawValue;
            _addDepth++;
        }

        private static void AddSuffix(MyInventory __instance, MyObjectBuilder_PhysicalObject objectBuilder, MyFixedPoint __result)
        {
            if (InventoryLedger.Current == null || _addDepth == 0) return;
            _addDepth--;
            if (_addDepth >= _adds.Length || _silent > 0) return;
            try
            {
                Book(__instance, ItemKeys.Of(objectBuilder.GetId()), _adds[_addDepth] - __result.RawValue);
            }
            catch (Exception e)
            {
                Failed("add", e);
            }
        }

        [ThreadStatic] private static string[] _removes;
        [ThreadStatic] private static int _removeDepth;

        private static void RemovePrefix(MyInventory __instance, uint itemId)
        {
            if (InventoryLedger.Current == null) return;
            if (_removes == null) _removes = new string[16];
            if (_removeDepth < _removes.Length)
            {
                string item = null;
                foreach (var stack in __instance.GetItems())
                    if (stack.ItemId == itemId)
                    {
                        item = ItemKeys.Of(stack.Content.GetId());
                        break;
                    }
                _removes[_removeDepth] = item;
            }
            _removeDepth++;
        }

        private static void RemoveSuffix(MyInventory __instance, MyFixedPoint __result)
        {
            if (InventoryLedger.Current == null || _removeDepth == 0) return;
            _removeDepth--;
            if (_removeDepth >= _removes.Length || _silent > 0) return;
            var item = _removes[_removeDepth];
            if (item == null) return;
            try
            {
                Book(__instance, item, -__result.RawValue);
            }
            catch (Exception e)
            {
                Failed("remove", e);
            }
        }

        [ThreadStatic] private static Dictionary<string, long>[] _edits;
        [ThreadStatic] private static int _editDepth;

        private static Dictionary<string, long> Count(MyInventory inventory)
        {
            var counts = new Dictionary<string, long>();
            foreach (var stack in inventory.GetItems())
            {
                var item = ItemKeys.Of(stack.Content.GetId());
                counts.TryGetValue(item, out var was);
                counts[item] = was + stack.Amount.RawValue;
            }
            return counts;
        }

        // ApplyChanges and UpdateItem change stacks in place: booked as the difference, before against after
        private static void EditPrefix(MyInventory __instance)
        {
            if (InventoryLedger.Current == null) return;
            if (_edits == null) _edits = new Dictionary<string, long>[8];
            if (_editDepth < _edits.Length) _edits[_editDepth] = Count(__instance);
            _editDepth++;
            _silent++;
        }

        private static void EditSuffix(MyInventory __instance)
        {
            if (InventoryLedger.Current == null || _editDepth == 0) return;
            _editDepth--;
            if (_silent > 0) _silent--;
            if (_editDepth >= _edits.Length || _silent > 0) return;
            try
            {
                var before = _edits[_editDepth];
                var after = Count(__instance);
                foreach (var item in before.Keys.Union(after.Keys).ToList())
                {
                    before.TryGetValue(item, out var was);
                    after.TryGetValue(item, out var now);
                    Book(__instance, item, now - was);
                }
            }
            catch (Exception e)
            {
                Failed("edit", e);
            }
        }

        // Clear(sync: false) empties the list without RemoveItems
        private static void ClearPrefix(MyInventory __instance, bool sync)
        {
            if (sync || InventoryLedger.Current == null || _silent > 0) return;
            try
            {
                foreach (var pair in Count(__instance)) Book(__instance, pair.Key, -pair.Value);
            }
            catch (Exception e)
            {
                Failed("clear", e);
            }
        }

        // ------------------------------------------------------------------ transfer

        private static void TransferPrefix(MyInventory src, MyInventory dst)
        {
            if (InventoryLedger.Current == null) return;
            var context = Contexts.Push("transfer", src);
            if (context == null) return;
            context.Src = src;
            context.Dst = dst;
        }

        private static void TransferSuffix(MyInventory src, MyInventory dst)
        {
            if (InventoryLedger.Current == null) return;
            var context = Contexts.Pop("transfer", src);
            if (context == null || context.Added.Count == 0) return;
            try
            {
                var excess = LedgerMath.Excess(context.Added, context.Removed);
                if (excess.Count == 0) return;
                var owner = dst.Entity is MyEntity target ? Identities.InventoryOwner(target) : 0;
                Recorder.Current?.Alert("dupe_transfer", owner, (dst.Entity as MyEntity)?.EntityId ?? 0,
                    $"a transfer made {LedgerMath.Describe(excess)}: {Where(src)} -> {Where(dst)}");
            }
            catch (Exception e)
            {
                Failed("transfer check", e);
            }
        }

        private static string Where(MyInventory inventory) =>
            inventory.Entity is MyEntity entity ? InventoryLedger.Describe(entity) : "?";

        // ------------------------------------------------------------------ inventories that come into being

        [ThreadStatic] private static ulong _originFrame;
        [ThreadStatic] private static string _origin;

        private static void LoadPrefix(MyInventory __instance)
        {
            var ledger = InventoryLedger.Current;
            if (ledger == null) return;
            try
            {
                var context = Contexts.Push("load", __instance);
                if (context == null) return;
                context.Flow = "load:" + Origin();
                ledger.MarkBorn(__instance);
            }
            catch (Exception e)
            {
                Failed("load", e);
            }
        }

        private static void LoadSuffix(MyInventory __instance)
        {
            if (InventoryLedger.Current != null) Contexts.Pop("load", __instance);
        }

        /// <summary>Why entities are being made: read from the stack once a frame and thread, a paste makes many.</summary>
        private static string Origin()
        {
            if (MySession.Static?.Ready != true) return "save";
            var frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            if (_origin != null && frame == _originFrame) return _origin;
            _originFrame = frame;
            return _origin = Sources.Classify();
        }

        // ------------------------------------------------------------------ pickup

        private static void TakeFloatingPrefix(MyInventory __instance, MyFloatingObject obj)
        {
            if (InventoryLedger.Current == null) return;
            var context = Contexts.Push("pickup", __instance);
            if (context != null) context.Limit = obj?.Item.Amount.RawValue ?? 0;
        }

        private static void PickupPrefix(MyInventory __instance, long entityId)
        {
            if (InventoryLedger.Current == null) return;
            var context = Contexts.Push("pickup", __instance);
            if (context == null) return;
            context.Limit = MyEntities.TryGetEntityById(entityId, out MyFloatingObject floating) && floating != null ? floating.Item.Amount.RawValue : 0;
        }

        private static void PickupSuffix(MyInventory __instance)
        {
            if (InventoryLedger.Current == null) return;
            var context = Contexts.Pop("pickup", __instance);
            if (context == null || context.Added.Count == 0) return;
            try
            {
                var taken = context.Added.Values.Sum();
                if (taken - context.Limit <= LedgerMath.Tolerance(context.Limit)) return;
                var entity = __instance.Entity as MyEntity;
                Recorder.Current?.Alert("pickup_excess", entity != null ? Identities.InventoryOwner(entity) : 0, entity?.EntityId ?? 0,
                    $"picked up {LedgerMath.Describe(context.Added)} from a floating object of {context.Limit / 1e6} into {Where(__instance)}");
            }
            catch (Exception e)
            {
                Failed("pickup check", e);
            }
        }

        // ------------------------------------------------------------------ production

        private static void RefinePrefix(MyRefinery __instance, MyBlueprintDefinitionBase queueItem) => Produce("refine", __instance, queueItem, __instance);

        private static void AssemblePrefix(MyAssembler __instance, MyBlueprintDefinitionBase blueprint) => Produce("assemble", __instance, blueprint, __instance);

        private static void DisassemblePrefix(MyAssembler __instance, MyBlueprintDefinitionBase blueprint) => Produce("disassemble", __instance, blueprint, __instance);

        private static void RefineSuffix(MyRefinery __instance) => CheckRefine(Close("refine", __instance));

        private static void AssembleSuffix(MyAssembler __instance) => CheckAssemble(Close("assemble", __instance));

        private static void DisassembleSuffix(MyAssembler __instance) => CheckDisassemble(Close("disassemble", __instance));

        // SentisOptimisations makes up for the time a grid was frozen with its own batches: the same checks,
        // the producer known by where the results went (its static methods pass it under names a patch
        // cannot bind)
        private static void OptRefinePrefix(MyBlueprintDefinitionBase queueItem) => Produce("refine", null, queueItem, queueItem);

        private static void OptRefineSuffix(MyBlueprintDefinitionBase queueItem) => CheckRefine(Close("refine", queueItem));

        private static void OptAssemblePrefix(MyBlueprintDefinitionBase blueprint) => Produce("assemble", null, blueprint, blueprint);

        private static void OptAssembleSuffix(MyBlueprintDefinitionBase blueprint) => CheckAssemble(Close("assemble", blueprint));

        private static void OptDisassemblePrefix(MyBlueprintDefinitionBase blueprint) => Produce("disassemble", null, blueprint, blueprint);

        private static void OptDisassembleSuffix(MyBlueprintDefinitionBase blueprint) => CheckDisassemble(Close("disassemble", blueprint));

        private static void Produce(string kind, MyEntity producer, MyBlueprintDefinitionBase blueprint, object key)
        {
            if (InventoryLedger.Current == null) return;
            var context = Contexts.Push(kind, key);
            if (context == null) return;
            context.Producer = producer;
            context.Blueprint = blueprint;
        }

        /// <summary>The production context, closed, when it made something outside creative mode; else null.</summary>
        private static Context Close(string kind, object key)
        {
            if (InventoryLedger.Current == null) return null;
            var context = Contexts.Pop(kind, key);
            if (context?.Blueprint == null || context.Added.Count == 0 || MySession.Static?.CreativeMode != false) return null;
            if (context.Producer == null) context.Producer = context.Where?.Entity as MyEntity;
            return context.Producer == null ? null : context;
        }

        /// <summary>How many batches of the blueprint the items are: per batch, how much of each they take.</summary>
        private static double Batches(Dictionary<string, long> made, MyBlueprintDefinitionBase.Item[] items, double per)
        {
            double batches = 0;
            foreach (var item in items)
            {
                made.TryGetValue(ItemKeys.Of(item.Id), out var amount);
                var one = item.Amount.RawValue * per;
                if (one > 0) batches = Math.Max(batches, amount / one);
            }
            return batches;
        }

        private static void CheckRefine(Context context)
        {
            if (!(context?.Producer is MyRefinery refinery) || !(refinery.BlockDefinition is MyRefineryDefinition definition)) return;
            var efficiency = definition.MaterialEfficiency * (refinery.UpgradeValues.TryGetValue("Effectiveness", out var upgrade) ? upgrade : 1f);
            var batches = Batches(context.Added, context.Blueprint.Results, efficiency);
            CheckInputs(context, context.Blueprint.Prerequisites.ToDictionary(p => ItemKeys.Of(p.Id), p => (long)(batches * p.Amount.RawValue)));
        }

        private static void CheckAssemble(Context context)
        {
            if (!(context?.Producer is MyAssembler assembler)) return;
            var factor = (MyFixedPoint)(1f / assembler.GetEfficiencyMultiplierForBlueprint(context.Blueprint));
            var batches = Batches(context.Added, context.Blueprint.Results, 1);
            CheckInputs(context, context.Blueprint.Prerequisites.ToDictionary(p => ItemKeys.Of(p.Id), p => (long)(batches * (p.Amount * factor).RawValue)));
        }

        private static void CheckDisassemble(Context context)
        {
            if (!(context?.Producer is MyAssembler assembler)) return;
            var factor = (MyFixedPoint)(1f / assembler.GetEfficiencyMultiplierForBlueprint(context.Blueprint));
            double batches = 0;
            foreach (var prerequisite in context.Blueprint.Prerequisites)
            {
                context.Added.TryGetValue(ItemKeys.Of(prerequisite.Id), out var amount);
                var one = (double)(prerequisite.Amount * factor).RawValue;
                if (one > 0) batches = Math.Max(batches, amount / one);
            }
            CheckInputs(context, context.Blueprint.Results.ToDictionary(r => ItemKeys.Of(r.Id), r => (long)(batches * r.Amount.RawValue)));
        }

        private static void CheckInputs(Context context, Dictionary<string, long> required)
        {
            try
            {
                var shortfall = LedgerMath.Shortfall(required, context.Removed);
                if (shortfall.Count == 0) return;
                var missing = string.Join(", ", shortfall.Select(s => $"{s.Key} took {s.Value.Taken / 1e6} of {s.Value.Required / 1e6}"));
                Recorder.Current?.Alert("production_without_input", Identities.InventoryOwner(context.Producer), context.Producer.EntityId,
                    $"{context.Kind} {context.Blueprint.Id.SubtypeName} made {LedgerMath.Describe(context.Added)} but {missing} ({InventoryLedger.Describe(context.Producer)})",
                    limitBy: context.Producer.EntityId ^ context.Kind.GetHashCode());
            }
            catch (Exception e)
            {
                Failed(context.Kind + " check", e);
            }
        }

        // ------------------------------------------------------------------ drill, weld, grind

        private static void DrillPrefix(MyDrillBase __instance)
        {
            if (InventoryLedger.Current != null) Contexts.Push("drill", __instance);
        }

        private static void DrillSuffix(MyDrillBase __instance)
        {
            if (InventoryLedger.Current != null) Contexts.Pop("drill", __instance);
        }

        /// <summary>What went into a block by welding and came out by grinding, since the ledger has known it.</summary>
        private sealed class BlockBook
        {
            public readonly Dictionary<string, long> Welded = new Dictionary<string, long>();
            public readonly Dictionary<string, long> Out = new Dictionary<string, long>();
        }

        private static readonly ConditionalWeakTable<MySlimBlock, BlockBook> Books = new ConditionalWeakTable<MySlimBlock, BlockBook>();

        private static void GrindPrefix(MySlimBlock __instance) => Stockpile("grind", __instance);

        private static void UnneededPrefix(MySlimBlock __instance) => Stockpile("unneeded", __instance);

        private static void WeldPrefix(MySlimBlock __instance) => Stockpile("weld", __instance);

        private static void Stockpile(string kind, MySlimBlock block)
        {
            if (InventoryLedger.Current == null) return;
            var context = Contexts.Push(kind, block);
            if (context == null) return;
            context.Block = block;
            if (kind == "unneeded") context.Flow = "weld";     // a welder taking back what the block did not need
        }

        private static void WeldSuffix(MySlimBlock __instance)
        {
            if (InventoryLedger.Current == null) return;
            var context = Contexts.Pop("weld", __instance);
            if (context == null || context.Removed.Count == 0) return;
            lock (Books) Add(Books.GetOrCreateValue(__instance).Welded, context.Removed);
        }

        private static void GrindSuffix(MySlimBlock __instance) => Unstock("grind", __instance);

        private static void UnneededSuffix(MySlimBlock __instance) => Unstock("unneeded", __instance);

        /// <summary>Components out of a block: no more than it is built of, plus what was welded into it.</summary>
        private static void Unstock(string kind, MySlimBlock block)
        {
            if (InventoryLedger.Current == null) return;
            var context = Contexts.Pop(kind, block);
            if (context == null || context.Added.Count == 0) return;
            try
            {
                Dictionary<string, long> excess;
                lock (Books)
                {
                    var book = Books.GetOrCreateValue(block);
                    Add(book.Out, context.Added);
                    var built = new Dictionary<string, long>();
                    foreach (var component in block.BlockDefinition.Components)
                    {
                        var item = ItemKeys.Of(component.Definition.Id);
                        built.TryGetValue(item, out var was);
                        built[item] = was + component.Count * 1_000_000L;
                    }
                    excess = LedgerMath.Excess(book.Out.Where(p => p.Key != "Ore/Scrap").ToDictionary(p => p.Key, p => p.Value), built, book.Welded);
                }
                if (excess.Count == 0) return;
                var grid = block.CubeGrid;
                Recorder.Current?.Alert("grind_excess", Identities.Owner(grid), grid?.EntityId ?? 0,
                    $"{block.BlockDefinition.DisplayNameText} on {grid?.DisplayName} gave {LedgerMath.Describe(excess)} more than it is built of");
            }
            catch (Exception e)
            {
                Failed("grind check", e);
            }
        }

        private static void Add(Dictionary<string, long> into, Dictionary<string, long> what)
        {
            foreach (var pair in what)
            {
                into.TryGetValue(pair.Key, out var was);
                into[pair.Key] = was + pair.Value;
            }
        }

        // ------------------------------------------------------------------ what uses items up or throws them out

        private static void ConsumePrefix(object __instance)
        {
            if (InventoryLedger.Current != null) Contexts.Push("consume", __instance);
        }

        private static void ConsumeSuffix(object __instance)
        {
            if (InventoryLedger.Current != null) Contexts.Pop("consume", __instance);
        }

        private static void BuildPrefix(object __instance)
        {
            // placing blocks, and the component combiner a welder also goes through (then it stays "weld")
            if (InventoryLedger.Current != null && Contexts.Top == null) Contexts.Push("build", __instance);
        }

        private static void BuildSuffix(object __instance)
        {
            if (InventoryLedger.Current != null) Contexts.Pop("build", __instance);
        }

        private static void ThrowPrefix(MyShipConnector __instance)
        {
            if (InventoryLedger.Current != null) Contexts.Push("throw", __instance);
        }

        private static void ThrowSuffix(MyShipConnector __instance)
        {
            if (InventoryLedger.Current != null) Contexts.Pop("throw", __instance);
        }

        private static void DropPrefix(MyInventory __instance, bool spawn)
        {
            if (spawn && InventoryLedger.Current != null && Contexts.Top?.Kind != "drop") Contexts.Push("drop", __instance);
        }

        private static void DropSuffix(MyInventory __instance, bool spawn)
        {
            if (spawn && InventoryLedger.Current != null) Contexts.Pop("drop", __instance);
        }
    }
}

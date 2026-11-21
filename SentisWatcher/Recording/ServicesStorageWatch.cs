using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Sandbox.Game.Entities;
using SentisWatcher.Storage;
using SpaceEngineers.Game.SessionComponents;
using Torch.Managers.PatchManager;

namespace SentisWatcher.Recording
{
    internal static class ServicesStorageWatch
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        private static Func<MyGridsStorageSessionComponent, object> _peek;
        private static Type _deposit;
        private static FieldInfo _grid, _data, _owner, _entries, _blueprint, _newOwner;
        [ThreadStatic] private static Deposit _storing;
        [ThreadStatic] private static Retrieval _loading;

        private sealed class Deposit
        {
            public Recorder Recorder;
            public object Data;
            public long Owner;
            public string Name;
            public long[] Originals;
        }

        private sealed class Retrieval
        {
            public Recorder Recorder;
            public Guid Id;
            public long Owner;
        }

        internal static void Patch(PatchContext ctx)
        {
            PatchGuard.Run("SentisWatcher.ServicesDeposit", ctx, c =>
            {
                var type = typeof(MyGridsStorageSessionComponent);
                _deposit = type.GetNestedType("StoreRequest", BindingFlags.NonPublic) ?? throw new MissingMemberException(type.FullName, "StoreRequest");
                _grid = Field(_deposit, "GridToStore");
                _data = Field(_deposit, "GridData");
                _owner = Field(_data.FieldType, "OwnerId");
                _entries = Field(type, "m_storedGridsData");
                _peek = BuildPeek(type);
                // Do not patch StoreRequest.Dispatch: another plugin protects it with Harmony.
                var pattern = c.GetPattern(type.GetMethod(nameof(MyGridsStorageSessionComponent.UpdateAfterSimulation), Instance));
                pattern.Prefixes.Add(Own(nameof(DispatchPrefix)));
                pattern.Suffixes.Add(Own(nameof(DispatchSuffix)));
            });
            PatchGuard.Run("SentisWatcher.ServicesRetrieval", ctx, c =>
            {
                var type = typeof(Sandbox.Game.MyGridStorageHelper);
                var data = type.GetNestedType("LoadData", BindingFlags.NonPublic) ?? throw new MissingMemberException(type.FullName, "LoadData");
                _blueprint = Field(data, "PathBlueprint");
                _newOwner = Field(data, "NewOwner");
                var target = type.GetMethod("LoadGrid_Internal_Phase4", Instance, null, new[] { data }, null)
                             ?? throw new MissingMethodException(type.FullName, "LoadGrid_Internal_Phase4");
                var pattern = c.GetPattern(target);
                pattern.Prefixes.Add(Own(nameof(RetrievePrefix)));
                pattern.Suffixes.Add(Own(nameof(RetrieveSuffix)));
            });
        }

        private static MethodInfo Own(string name) => typeof(ServicesStorageWatch).GetMethod(name, Static);
        private static FieldInfo Field(Type type, string name) => type.GetField(name, Instance) ?? throw new MissingFieldException(type.FullName, name);

        private static Func<MyGridsStorageSessionComponent, object> BuildPeek(Type type)
        {
            var self = Expression.Parameter(type);
            var queue = Expression.Field(self, Field(type, "m_requests"));
            var peek = queue.Type.GetMethod("Peek", Type.EmptyTypes) ?? throw new MissingMethodException(queue.Type.FullName, "Peek");
            var head = Expression.Condition(Expression.Equal(Expression.Property(queue, "Count"), Expression.Constant(0)),
                Expression.Constant(null, typeof(object)), Expression.Convert(Expression.Call(queue, peek), typeof(object)));
            return Expression.Lambda<Func<MyGridsStorageSessionComponent, object>>(head, self).Compile();
        }

        private static void DispatchPrefix(MyGridsStorageSessionComponent __instance)
        {
            _storing = null;
            var recorder = Recorder.Current;
            if (recorder == null) return;
            Recorder.Safe("services deposit start", () =>
            {
                var next = _peek(__instance);
                if (next == null || next.GetType() != _deposit || !(_grid.GetValue(next) is MyCubeGrid root)) return;
                var data = _data.GetValue(next);
                if (data == null) return;
                // StoreGrid copies the physical group. The logical group used for removal need not match it.
                var nodes = MyCubeGridGroups.Static?.Physical.GetGroupNodes(root);
                var ids = nodes == null ? new[] { root.EntityId } : nodes.Select(g => g.EntityId).Append(root.EntityId).Distinct().ToArray();
                _storing = new Deposit { Recorder = recorder, Data = data, Owner = (long)_owner.GetValue(data), Name = root.DisplayName, Originals = ids };
            });
        }

        private static void DispatchSuffix(MyGridsStorageSessionComponent __instance)
        {
            var store = _storing;
            _storing = null;
            if (store == null || store.Recorder != Recorder.Current) return;
            Recorder.Safe("services deposit completion", () =>
            {
                if (!(_entries.GetValue(__instance) is IDictionary entries)) return;
                foreach (DictionaryEntry entry in entries)
                {
                    if (!ReferenceEquals(entry.Value, store.Data) || !(entry.Key is Guid id)) continue;
                    GarageWatch.ServicesStored(store.Recorder, id, store.Owner, store.Name, store.Originals,
                        () => __instance.TryGetStoredGridData(id, out _));
                    break;
                }
            });
        }

        private static void RetrievePrefix(object data)
        {
            _loading = null;
            var recorder = Recorder.Current;
            if (recorder == null || data == null) return;
            Recorder.Safe("services retrieval start", () =>
            {
                if (TryEntry((string)_blueprint.GetValue(data), out var id))
                    _loading = new Retrieval { Recorder = recorder, Id = id, Owner = (long)_newOwner.GetValue(data) };
            });
        }

        private static void RetrieveSuffix() => _loading = null;

        public static bool TryEntry(string path, out Guid id)
        {
            var name = Path.GetFileNameWithoutExtension(path) ?? "";
            if (name.StartsWith("gs-", StringComparison.OrdinalIgnoreCase)) name = name.Substring(3);
            return Guid.TryParseExact(name, "D", out id) && id != Guid.Empty;
        }

        internal static void WatchPaste(ref MyCubeGrid.MyPasteGridParameters parameters)
        {
            var load = _loading;
            var original = parameters.OnPasteFinished;
            if (load == null || original == null || load.Recorder != Recorder.Current) return;
            var declaring = original.Method.DeclaringType;
            while (declaring != null && declaring != typeof(Sandbox.Game.MyGridStorageHelper)) declaring = declaring.DeclaringType;
            if (declaring == null) return;
            // A placement retry can issue a new paste from inside this callback.
            parameters.OnPasteFinished = (success, grids) =>
            {
                var prior = _loading;
                _loading = load;
                try { original(success, grids); }
                finally
                {
                    _loading = prior;
                    if (success && Recorder.Current == load.Recorder)
                        Recorder.Safe("services retrieval", () => GarageWatch.ServicesRetrieved(load.Recorder, load.Id, load.Owner, grids));
                }
            };
        }
    }
}

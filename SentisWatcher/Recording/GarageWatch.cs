using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using Sandbox.Game.Entities;
using SentisWatcher.Storage;
using Torch.Managers.PatchManager;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// VirtualGarage's put-ins and take-outs checked against the world: alert <c>grid_dupe</c> (and a warning) when a grid
    /// is there twice.
    /// - Put into the garage (<c>VirtualGarageSave.Store</c> gave a file) and still in the world <see cref="VerifyMs"/>
    ///   later, the file there.
    /// - Taken out (<c>VirtualGarageLoad.Load</c>'s grids in the world) while the grid the file was made of is still in the
    ///   world: the file keeps the entity ids the grids and blocks had (the garage gives the copy new ones).
    /// - Taken out of the same garage entry again while the copy of its last take-out is still in the world (a file put
    ///   back by the garage after a crash comes with its copy gone with the world's rollback).
    /// - Taken out of an entry while a copy of the same ship taken out of another entry is still in the world: the
    ///   entries are tied by what the file was made of (the ids in it against what each entry was put in with), so a
    ///   file moved or copied to another garage is still the same ship. A sale on the trade console ties the seller's
    ///   entry to the buyer's at once (<see cref="Relink"/>, <see cref="MarketWatch"/>).
    /// The entries are kept in <c>garage-takeouts.json</c> by the day files: <see cref="KeepDays"/> days after their last
    /// operation, <see cref="KeepStoredDays"/> while what was put in has not been taken out.
    /// Events <c>garage_in</c> and <c>garage_out</c> for each. Patched as the session loads, if VirtualGarage is here.
    /// </summary>
    public static class GarageWatch
    {
        public const long VerifyMs = 3000;
        public const double KeepDays = 30;
        public const double KeepStoredDays = 365;

        private static readonly Logger Log = LogManager.GetLogger("SentisWatcher.Garage");
        private static Recorder R => Recorder.Current;
        private static readonly Regex EntityIds = new Regex(@"<EntityId>(-?\d+)</EntityId>", RegexOptions.Compiled);

        private sealed class PutIn
        {
            public string File, Name, Key, Source;
            public long Owner, At, NextCheck;
            public List<long> Grids;
            public Func<bool> Exists;
        }

        public sealed class TakeOut
        {
            public List<long> Grids = new List<long>();
            public long[] Originals;
            public DateTime Utc;
            public string Name;
            /// <summary>Put in and not taken out since: kept for <see cref="KeepStoredDays"/>.</summary>
            public bool Stored;
            /// <summary>The entry this one came from (a sale on the trade console), or null.</summary>
            public string From;
        }

        private static readonly object Lock = new object();
        private static readonly List<PutIn> Verifying = new List<PutIn>();
        private static Dictionary<string, TakeOut> _takeOuts;
        [ThreadStatic] private static PutIn _storing;
        private static bool _patched;

        /// <summary>VirtualGarage's put-in and take-out, followed, if it is here. Once, as the session loads.</summary>
        internal static void PatchVirtualGarage()
        {
            if (_patched) return;
            _patched = true;
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "VirtualGarage");
            if (assembly == null) return;
            try
            {
                const BindingFlags any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                var store = assembly.GetType("VirtualGarage.VirtualGarageSave")?.GetMethod("Store", any) ?? throw new MissingMethodException("VirtualGarageSave.Store");
                var load = assembly.GetType("VirtualGarage.VirtualGarageLoad")?.GetMethods(any).FirstOrDefault(m => m.Name == "Load" && m.GetParameters().Any(p => p.Name == "spawned"))
                           ?? throw new MissingMethodException("VirtualGarageLoad.Load(file, ..., spawned, failed)");
                var patches = (PatchManager)Torch.TorchBase.Instance.Managers.GetManager(typeof(PatchManager));
                var context = patches.AcquireContext();
                PatchGuard.Run("SentisWatcher.Garage", context, c =>
                {
                    c.GetPattern(store).Prefixes.Add(typeof(GarageWatch).GetMethod(nameof(StorePrefix), BindingFlags.NonPublic | BindingFlags.Static));
                    c.GetPattern(store).Suffixes.Add(typeof(GarageWatch).GetMethod(nameof(StoreSuffix), BindingFlags.NonPublic | BindingFlags.Static));
                    c.GetPattern(load).Prefixes.Add(typeof(GarageWatch).GetMethod(nameof(LoadPrefix), BindingFlags.NonPublic | BindingFlags.Static));
                });
                PatchGuard.Run("SentisWatcher.Market", context, c => MarketWatch.Patch(assembly, c));
                patches.Commit();
            }
            catch (Exception e)
            {
                Recorder.Failed("following VirtualGarage", e);
            }
        }

        // ------------------------------------------------------------------ put in

        private static void StorePrefix(long ownerIdentityId, List<MyCubeGrid> group)
        {
            _storing = null;
            if (R == null || group == null || group.Count == 0) return;
            _storing = new PutIn { Owner = ownerIdentityId, Name = group[0].DisplayName, Grids = group.Select(g => g.EntityId).ToList() };
        }

        private static void StoreSuffix(string __result) => Recorder.Safe("garage put-in", () =>
        {
            var putIn = _storing;
            _storing = null;
            if (putIn == null || __result == null) return;
            putIn.File = __result;
            putIn.Key = Key(__result);
            putIn.Source = "VirtualGarage";
            putIn.Exists = () => InGarage(putIn.File);
            putIn.At = Clock.Now;
            Remember(putIn.Key, putIn.Name, putIn.Grids.ToArray());
            lock (Lock) Verifying.Add(putIn);
            R.Event("garage_in", putIn.Owner, putIn.Grids.FirstOrDefault(), detail: $"\"{putIn.Name}\" ({putIn.Grids.Count} grids) into {Key(__result)}");
        });

        /// <summary>The put-ins whose grids should be gone by now (game thread, from the recorder's flush).</summary>
        internal static void Check(Recorder recorder)
        {
            List<PutIn> due;
            var now = Clock.Now;
            lock (Lock)
            {
                if (Verifying.Count == 0) return;
                due = Verifying.Where(p => now - p.At >= VerifyMs && now >= p.NextCheck).ToList();
                foreach (var p in due) Verifying.Remove(p);
            }
            foreach (var putIn in due)
                Recorder.Safe("garage put-in check", () =>
                {
                    var left = putIn.Grids.Where(GridIsLive).ToList();
                    if (left.Count == 0) return;
                    if (!putIn.Exists())
                    {
                        // The garage may still be writing its file off-thread.
                        if (putIn.File != null && now - putIn.At < 60000)
                        {
                            putIn.NextCheck = now + VerifyMs;
                            lock (Lock) Verifying.Add(putIn);
                        }
                        return;
                    }
                    Dupe(recorder, putIn.Owner, left[0], $"{putIn.Source} [{putIn.Key}]: stored \"{putIn.Name}\" but original grids are still in the world: {string.Join(", ", left)}");
                });
        }

        private static bool InGarage(string file) =>
            File.Exists(file) || File.Exists(file.Replace("_unsaved.sbc", ".sbc")) || File.Exists(file + "_spawned_unsaved") || File.Exists(file.Replace("_unsaved.sbc", ".sbc") + "_spawned_unsaved");

        // ------------------------------------------------------------------ take out

        private static void LoadPrefix(string file, long ownerIdentityId, ref Action<List<MyCubeGrid>> spawned)
        {
            var recorder = R;
            if (recorder == null || spawned == null || string.IsNullOrEmpty(file)) return;
            var original = spawned;
            // the ids of what the file was made of, read aside while the garage reads it too
            var ids = Task.Run(() => IdsIn(file));
            spawned = grids =>
            {
                try { original(grids); }
                finally
                {
                    if (R == recorder) Recorder.Safe("garage take-out", () => TakenOut(file, ownerIdentityId, grids, ids));
                }
            };
        }

        /// <summary>The entity ids a blueprint file holds (its grids' and blocks').</summary>
        public static HashSet<long> IdsIn(string file)
        {
            var ids = new HashSet<long>();
            try
            {
                foreach (Match m in EntityIds.Matches(File.ReadAllText(file)))
                    if (long.TryParse(m.Groups[1].Value, out var id) && id != 0) ids.Add(id);
            }
            catch (Exception e)
            {
                Log.Warn("SentisWatcher: the garage file " + file + " not read for its ids: " + e.Message);
            }
            return ids;
        }

        private static void TakenOut(string file, long owner, List<MyCubeGrid> grids, Task<HashSet<long>> ids)
        {
            if (grids == null || grids.Count == 0) return;
            var recorder = R;
            var key = Key(file);
            var name = grids[0].DisplayName;
            var mine = new HashSet<long>(grids.Select(g => g.EntityId));
            recorder.Event("garage_out", owner, grids[0].EntityId, grids[0].PositionComp.GetPosition(), count: grids.Count, detail: $"\"{name}\" ({grids.Count} grids) out of {key}");
            foreach (var grid in grids) GridSpawnWatch.Told(recorder, grid, "garage");

            var knownOriginals = ObserveRetrieval(recorder, "VirtualGarage", key, name, owner, grids[0].EntityId, mine);

            // what the file was made of (its ids: read aside; checked on the game thread): still in the world, and the
            // same ship taken out of another entry
            ids.ContinueWith(t => Sandbox.MySandboxGame.Static?.Invoke(() => Recorder.Safe("garage original check", () =>
            {
                if (Recorder.Current != recorder) return;
                if (!knownOriginals)
                {
                    var here = Present(t.Result, mine);
                    if (here.Count > 0)
                        Dupe(recorder, owner, grids[0].EntityId, $"\"{name}\" taken out of the garage ({key}) while what the garage file was made of is still in the world: " +
                                                                 $"{here.Count} of its {t.Result.Count} grids and blocks, e.g. {string.Join(", ", here.Take(5))}");
                }
                List<KeyValuePair<string, long[]>> kin;
                lock (Lock) kin = Kin(TakeOuts(), key, t.Result, mine, GridIsLive);
                foreach (var other in kin)
                    Dupe(recorder, owner, grids[0].EntityId, $"VirtualGarage [{key}]: \"{name}\" taken out while a copy of the same ship taken out of " +
                                                             $"{other.Key} is still in the world: {string.Join(", ", other.Value)}");
            }), "SentisWatcher garage original check"));
        }

        /// <summary>
        /// The other entries of the same ship (what they were put in with is in this file) with copies still in the world,
        /// and those copies; the entries tied to this one by a sale are not counted (their copies are this entry's).
        /// </summary>
        public static List<KeyValuePair<string, long[]>> Kin(IDictionary<string, TakeOut> all, string key, ICollection<long> fileIds,
            ICollection<long> current, Func<long, bool> live)
        {
            var found = new List<KeyValuePair<string, long[]>>();
            if (fileIds == null || fileIds.Count == 0) return found;
            all.TryGetValue(key, out var mine);
            foreach (var pair in all)
            {
                if (pair.Key == key || pair.Value?.Originals == null) continue;
                if (pair.Value.From == key || mine?.From == pair.Key) continue;
                if (!pair.Value.Originals.Any(fileIds.Contains)) continue;
                var copies = (pair.Value.Grids ?? new List<long>()).Where(id => !current.Contains(id) && live(id)).Distinct().ToArray();
                if (copies.Length > 0) found.Add(new KeyValuePair<string, long[]>(pair.Key, copies));
            }
            return found;
        }

        /// <summary>
        /// The entry of a file that moved to another key (sold on the trade console: from the seller's garage to the
        /// buyer's) takes over what the old one knows: what it was put in with, its copies.
        /// </summary>
        internal static void Relink(string fromKey, string toKey)
        {
            if (fromKey == null || toKey == null || fromKey == toKey) return;
            lock (Lock)
            {
                var all = TakeOuts();
                if (!all.TryGetValue(fromKey, out var from)) return;
                if (!all.TryGetValue(toKey, out var to)) all[toKey] = to = new TakeOut();
                to.Originals = to.Originals ?? from.Originals;
                to.Grids = (to.Grids ?? new List<long>()).Concat(from.Grids ?? new List<long>()).Distinct().ToList();
                to.Stored = from.Stored;
                to.Name = from.Name;
                to.From = fromKey;
                to.Utc = DateTime.UtcNow;
            }
            SaveTakeOuts();
        }

        /// <summary>What a test asks of an entry: what it was put in with, its copies, where it came from.</summary>
        public static TakeOut EntryOf(string key)
        {
            lock (Lock) return TakeOuts().TryGetValue(key, out var entry) ? entry : null;
        }

        internal static void ServicesStored(Recorder recorder, Guid id, long owner, string name, long[] originals, Func<bool> exists)
        {
            var key = "services:" + id.ToString("D");
            Remember(key, name, originals);
            lock (Lock) Verifying.Add(new PutIn
            {
                Owner = owner, Name = name, At = Clock.Now, Key = key, Source = "Services terminal",
                Grids = originals.ToList(), Exists = exists
            });
            recorder.Event("services_storage_in", owner, originals.FirstOrDefault(), count: originals.Length, detail: key + " | " + name);
        }

        internal static void ServicesRetrieved(Recorder recorder, Guid id, long owner, List<MyCubeGrid> grids, Task<HashSet<long>> fileIds)
        {
            if (grids == null || grids.Count == 0) return;
            var key = "services:" + id.ToString("D");
            var first = grids[0];
            var name = first.DisplayName;
            var mine = new HashSet<long>(grids.Select(g => g.EntityId));
            recorder.Event("services_storage_out", owner, first.EntityId, count: grids.Count, detail: key + " | " + name);
            var known = ObserveRetrieval(recorder, "Services terminal", key, name, owner, first.EntityId, mine);
            if (known || fileIds == null) return;
            // stored before it was followed (or while the recorder was off): what the record's file was made of
            fileIds.ContinueWith(t => Sandbox.MySandboxGame.Static?.Invoke(() => Recorder.Safe("services original check", () =>
            {
                if (Recorder.Current != recorder) return;
                var here = Present(t.Result, mine);
                if (here.Count > 0)
                    Dupe(recorder, owner, first.EntityId, $"Services terminal [{key}]: \"{name}\" retrieved while what the stored record was made of is still in the world: " +
                                                          $"{here.Count} of its {t.Result.Count} grids and blocks, e.g. {string.Join(", ", here.Take(5))}");
            }), "SentisWatcher services original check"));
        }

        private static void Remember(string key, string name, long[] originals)
        {
            lock (Lock)
            {
                var all = TakeOuts();
                if (!all.TryGetValue(key, out var entry)) all[key] = entry = new TakeOut();
                entry.Name = name;
                entry.Originals = originals;
                entry.Utc = DateTime.UtcNow;
                entry.Stored = true;
            }
            SaveTakeOuts();
        }

        private static bool GridIsLive(long id) => MyEntities.TryGetEntityById(id, out MyCubeGrid grid) &&
            !grid.Closed && !grid.MarkedForClose && !grid.IsPreview;

        public static long[] PreviousCopies(IEnumerable<long> previous, ISet<long> current, Func<long, bool> live)
        {
            var ids = previous.ToArray();
            // Observing the same paste twice is not a second copy.
            return ids.Any(current.Contains) ? Array.Empty<long>() : ids.Where(live).Distinct().ToArray();
        }

        private static bool ObserveRetrieval(Recorder recorder, string source, string key, string name, long owner, long entity, HashSet<long> current)
        {
            long[] old, originals;
            bool knownOriginals;
            lock (Lock)
            {
                var all = TakeOuts();
                if (!all.TryGetValue(key, out var entry)) all[key] = entry = new TakeOut();
                old = PreviousCopies(entry.Grids ?? new List<long>(), current, GridIsLive);
                knownOriginals = entry.Originals != null;
                originals = (entry.Originals ?? Array.Empty<long>()).Where(id => !current.Contains(id) && GridIsLive(id)).Distinct().ToArray();
                entry.Grids = (entry.Grids ?? new List<long>()).Where(GridIsLive).Concat(current).Distinct().ToList();
                entry.Utc = DateTime.UtcNow;
                entry.Name = name;
                entry.Stored = false;
            }
            if (old.Length > 0 || originals.Length > 0)
            {
                var reason = old.Length > 0
                    ? "again while the copy taken out before is still in the world: " + string.Join(", ", old)
                    : "while what the garage file was made of is still in the world: " + string.Join(", ", originals);
                if (source != "VirtualGarage" && old.Length == 0)
                    reason = "while the stored original grids are still in the world: " + string.Join(", ", originals);
                Dupe(recorder, owner, entity, $"{source} [{key}]: \"{name}\" retrieved {reason}");
            }
            SaveTakeOuts();
            return knownOriginals;
        }

        /// <summary>The ids of entities in the world now, but the ones given.</summary>
        public static List<long> Present(IEnumerable<long> ids, ICollection<long> except) =>
            ids.Where(id => !except.Contains(id) && MyEntities.TryGetEntityById(id, out var e) && !e.MarkedForClose).ToList();

        private static void Dupe(Recorder recorder, long owner, long entity, string what)
        {
            var text = what + "; owner " + Identities.NameOf(owner) + " (" + owner + ")";
            recorder.Event("grid_dupe", owner, entity, detail: text);
            Log.Warn("SentisWatcher: a grid twice in the world: " + text);
            recorder.Alert("grid_dupe", owner, entity, text);
        }

        /// <summary>"steamid/title" of a garage file, whatever its state (put in, saved, taken out).</summary>
        public static string Key(string file)
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
            var name = Path.GetFileName(file);
            var cut = name.IndexOf(".sbc", StringComparison.OrdinalIgnoreCase);
            if (cut >= 0) name = name.Substring(0, cut);
            if (name.EndsWith("_unsaved")) name = name.Substring(0, name.Length - "_unsaved".Length);
            return folder + "/" + name;
        }

        // ------------------------------------------------------------------ the take-outs kept

        private static string TakeOutsFile => Path.Combine(SentisWatcherPlugin.Instance?.Store?.Folder ?? ".", "garage-takeouts.json");

        private static Dictionary<string, TakeOut> TakeOuts()
        {
            lock (Lock)
            {
                if (_takeOuts != null) return _takeOuts;
                try
                {
                    _takeOuts = File.Exists(TakeOutsFile)
                        ? JsonConvert.DeserializeObject<Dictionary<string, TakeOut>>(File.ReadAllText(TakeOutsFile)) ?? new Dictionary<string, TakeOut>()
                        : new Dictionary<string, TakeOut>();
                }
                catch (Exception e)
                {
                    Log.Warn("SentisWatcher: " + TakeOutsFile + " not read: " + e.Message);
                    _takeOuts = new Dictionary<string, TakeOut>();
                }
                return _takeOuts;
            }
        }

        /// <summary>Whether an entry is out of date: <see cref="KeepDays"/> after its last operation, <see cref="KeepStoredDays"/> while stored.</summary>
        public static bool Expired(TakeOut entry, DateTime utcNow) =>
            entry == null || entry.Utc < utcNow.AddDays(-(entry.Stored ? KeepStoredDays : KeepDays));

        private static void SaveTakeOuts()
        {
            Dictionary<string, TakeOut> snapshot;
            long order;
            lock (Lock)
            {
                var now = DateTime.UtcNow;
                foreach (var key in TakeOuts().Where(p => Expired(p.Value, now)).Select(p => p.Key).ToList()) _takeOuts.Remove(key);
                // written aside: a year of entries is not serialized on the game thread (an entry's lists are replaced, never changed)
                snapshot = new Dictionary<string, TakeOut>(_takeOuts);
                order = ++_writeOrder;
            }
            var file = TakeOutsFile;
            Task.Run(() =>
            {
                try
                {
                    var json = JsonConvert.SerializeObject(snapshot);
                    lock (TakeOutsWrite)
                    {
                        if (WrittenOrders.TryGetValue(file, out var written) && order <= written) return;
                        File.WriteAllText(file + ".tmp", json);
                        if (File.Exists(file)) File.Replace(file + ".tmp", file, null);
                        else File.Move(file + ".tmp", file);
                        WrittenOrders[file] = order;
                    }
                }
                catch (Exception e)
                {
                    Log.Warn("SentisWatcher: " + file + " not written: " + e.Message);
                }
            });
        }

        private static readonly object TakeOutsWrite = new object();
        private static readonly Dictionary<string, long> WrittenOrders = new Dictionary<string, long>();
        private static long _writeOrder;

        internal static void Reset()
        {
            lock (Lock)
            {
                Verifying.Clear();
                _takeOuts = null;
            }
        }
    }
}

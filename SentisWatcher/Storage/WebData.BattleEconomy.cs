using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SentisWatcher.Storage
{
    public sealed partial class WebData
    {
        private sealed class EconomyRecord
        {
            public long First, Last, Grid, Owner;
            public string Token, Faction;
            public double Before, After, X, Y, Z;
            public bool Destroyed, Tracked, Damaged;
            public JArray Components;
            public string[] DamageKeys;
        }
        private sealed class ComponentCost
        {
            public string Id;
            public long Lost, Unpriced;
            public decimal Known;
            public readonly HashSet<long> Prices = new HashSet<long>();
            public JObject Json() => new JObject
            {
                ["id"] = Id, ["lost"] = Lost, ["unpriced"] = Unpriced,
                ["unitPrice"] = Prices.Count == 1 && Unpriced == 0 ? (JToken)new JValue(Prices.First()) : JValue.CreateNull(),
                ["priceStatus"] = Unpriced > 0 ? "unavailable" : Prices.Count > 1 ? "mixed" : "ok",
                ["knownCredits"] = Money(Known), ["credits"] = Unpriced == 0 ? Money(Known) : null,
            };
        }
        private static string Money(decimal value) => value.ToString("0", CultureInfo.InvariantCulture);

        // This table is read only for a selected report, never for every item in the battle catalogue.
        private JObject BattleEconomy(BattleGroup battle, List<BattleGroup> groups, Func<long, string> name, bool provisional, long gap)
        {
            const long slack = 2000;
            const int recordLimit = 100000;
            var hits = battle.Events.Where(e => e.Kind == "hit" && e.TargetKind == "block" && e.Amount > 0).ToList();
            var grids = new HashSet<long>(hits.Select(e => e.Target).Where(id => id != 0));
            var hitTimes = hits.GroupBy(e => e.Target).ToDictionary(g => g.Key, g => g.Select(e => e.T).OrderBy(t => t).ToArray());
            bool HasHitTime(EconomyRecord item)
            {
                if (!hitTimes.TryGetValue(item.Grid, out var times)) return false;
                var index = Array.BinarySearch(times, item.First - slack);
                if (index < 0) index = ~index;
                return index < times.Length && times[index] <= item.Last + slack;
            }
            var records = new List<EconomyRecord>();
            var byDamageKey = new Dictionary<string, HashSet<BattleGroup>>(StringComparer.Ordinal);
            foreach (var group in groups)
                foreach (var e in group.Events.Where(e => e.Kind == "hit" && e.TargetKind == "block" && !string.IsNullOrEmpty(e.DamageKey)))
                {
                    if (!byDamageKey.TryGetValue(e.DamageKey, out var owners)) byDamageKey[e.DamageKey] = owners = new HashSet<BattleGroup>();
                    owners.Add(group);
                }
            var scanned = 0; var invalid = 0; var ambiguous = 0; var unmatched = 0; var truncated = false;
            if (grids.Count > 0)
                foreach (var db in Days(battle.Start - slack, battle.End + gap + slack))
                    using (db)
                    {
                        if (!HasTable(db, "block_economy")) continue;
                        // Numeric DB ids only: no user strings are interpolated into SQL.
                        var gridFilter = string.Join(",", grids.OrderBy(id => id).Select(id => id.ToString(CultureInfo.InvariantCulture)));
                        using (var cmd = Command(db, "SELECT first_t,t,block_token,grid,owner,faction,before_integrity,after_integrity,destroyed,components,x,y,z,tracking,damage_keys,damaged " +
                            "FROM block_economy WHERE t>=@a AND first_t<=@b AND grid IN (" + gridFilter + ") ORDER BY t,id LIMIT @limit",
                            ("@a", battle.Start - slack), ("@b", battle.End + slack), ("@limit", recordLimit - scanned + 1)))
                        using (var r = cmd.ExecuteReader())
                            while (r.Read())
                            {
                                if (++scanned > recordLimit) { truncated = true; break; }
                                try
                                {
                                    var componentJson = S(r, 9); var keyJson = S(r, 14);
                                    if (componentJson.Length > 262144 || keyJson.Length > 262144) throw new FormatException("Economy payload limit exceeded");
                                    var keys = string.IsNullOrEmpty(keyJson) ? new JArray() : JArray.Parse(keyJson);
                                    if (keys.Count > 4096 || keys.Any(k => k.Type != JTokenType.String || string.IsNullOrEmpty((string)k) || ((string)k).Length > 64))
                                        throw new FormatException("Invalid damage links");
                                    var item = new EconomyRecord
                                    {
                                        First = L(r, 0), Last = L(r, 1), Token = S(r, 2), Grid = L(r, 3), Owner = L(r, 4),
                                        Faction = r.IsDBNull(5) ? null : r.GetString(5), Before = D(r, 6), After = D(r, 7),
                                        Destroyed = L(r, 8) != 0, Components = JArray.Parse(componentJson), DamageKeys = keys.Values<string>().Distinct().ToArray(), X = D(r, 10), Y = D(r, 11), Z = D(r, 12), Tracked = !r.IsDBNull(13) && L(r, 13) == 1, Damaged = L(r, 15) != 0,
                                    };
                                    if (item.First < 0 || item.Last > 253402300799000L || item.Last < item.First || string.IsNullOrEmpty(item.Token) || item.Components.Count > 256 ||
                                        item.Components.Any(c => !(c is JObject) || c["id"]?.Type != JTokenType.String ||
                                            string.IsNullOrEmpty((string)c["id"]) || ((string)c["id"]).Length > 120 ||
                                            c["lost"]?.Type != JTokenType.Integer || (long)c["lost"] <= 0 || (long)c["lost"] > int.MaxValue * 256L) ||
                                        new[] { item.X, item.Y, item.Z, item.Before, item.After }.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                                        throw new FormatException("Invalid economy record");
                                    // Exact private damage-aggregate links, not proximity guesses: a delayed application or a
                                    // moving/split grid must not give one fight another fight's components.
                                    var candidates = new HashSet<BattleGroup>();
                                    var orphan = item.DamageKeys.Length == 0;
                                    foreach (var key in item.DamageKeys)
                                        if (byDamageKey.TryGetValue(key, out var owners)) candidates.UnionWith(owners);
                                        else orphan = true;
                                    if (!orphan && candidates.Count == 1 && candidates.Contains(battle)) records.Add(item);
                                    else if (candidates.Contains(battle)) { if (candidates.Count > 1) ambiguous++; else unmatched++; }
                                    else if (orphan && HasHitTime(item)) unmatched++;
                                }
                                catch (Exception e) when (e is FormatException || e is Newtonsoft.Json.JsonException || e is InvalidCastException || e is OverflowException)
                                { invalid++; }
                            }
                        if (truncated) break;
                    }
            var interrupted = false;
            foreach (var db in Days(battle.Start, battle.End))
                using (db)
                {
                    if (!HasTable(db, "events")) continue;
                    using (var cmd = Command(db, "SELECT 1 FROM events WHERE t>@a AND t<=@b AND kind IN ('server_start','server_stop') LIMIT 1",
                        ("@a", battle.Start), ("@b", battle.End))) interrupted |= cmd.ExecuteScalar() != null;
                }
            // Check coverage per damage aggregate, not merely whether the battle has at least one new telemetry row.
            var covered = new HashSet<string>(records.SelectMany(r => r.DamageKeys), StringComparer.Ordinal);
            var missing = hits.Count(e => e.EconomyTracking != 1 || string.IsNullOrEmpty(e.DamageKey) || !covered.Contains(e.DamageKey));
            var status = hits.Count == 0 ? (interrupted ? "unavailable" : "not_applicable") : records.Count == 0 ? "unavailable" :
                missing > 0 || invalid > 0 || ambiguous > 0 || unmatched > 0 || truncated || interrupted || records.Any(r => !r.Tracked) ? "partial" : "ok";
            var sideRows = new JArray();
            decimal knownTotal = 0; long unpricedTotal = 0, lostTotal = 0;
            foreach (var side in records.GroupBy(r => SideKey(r.Owner, r.Faction)).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var first = side.First();
                var components = new Dictionary<string, ComponentCost>(StringComparer.Ordinal);
                foreach (var record in side)
                    foreach (var raw in record.Components.OfType<JObject>())
                    {
                        var id = (string)raw["id"];
                        if (string.IsNullOrEmpty(id) || id.Length > 120 || raw["lost"]?.Type != JTokenType.Integer ||
                            (long)raw["lost"] <= 0 || (long)raw["lost"] > int.MaxValue * 256L)
                        { invalid++; status = "partial"; continue; }
                        var count = (long)raw["lost"];
                        if (!components.TryGetValue(id, out var cost)) components[id] = cost = new ComponentCost { Id = id };
                        cost.Lost = checked(cost.Lost + count);
                        if (raw["unitPrice"]?.Type == JTokenType.Integer && (long)raw["unitPrice"] >= 0 && (long)raw["unitPrice"] <= int.MaxValue)
                        {
                            var price = (long)raw["unitPrice"];
                            cost.Prices.Add(price); cost.Known += (decimal)count * price;
                        }
                        else cost.Unpriced = checked(cost.Unpriced + count);
                    }
                var affected = side.Where(r => r.Damaged || r.Destroyed).GroupBy(r => r.Token).ToList();
                var destroyed = affected.Count(g => g.Any(r => r.Destroyed));
                var known = components.Values.Sum(c => c.Known);
                var unpriced = components.Values.Sum(c => c.Unpriced);
                var lost = components.Values.Sum(c => c.Lost);
                knownTotal += known; unpricedTotal += unpriced; lostTotal += lost;
                sideRows.Add(new JObject
                {
                    ["id"] = side.Key, ["kind"] = first.Faction == null ? "unknown" : first.Faction.Length == 0 ? (first.Owner == 0 ? "unowned" : "player") : "faction",
                    ["name"] = string.IsNullOrEmpty(first.Faction) ? name(first.Owner) : first.Faction.Substring(first.Faction.IndexOf('|') + 1),
                    ["affectedBlocks"] = affected.Count, ["destroyedBlocks"] = destroyed, ["damagedBlocks"] = affected.Count - destroyed,
                    ["lostComponents"] = lost, ["unpricedComponents"] = unpriced,
                    ["knownCredits"] = Money(known), ["credits"] = unpriced == 0 ? Money(known) : null,
                    ["components"] = new JArray(components.Values.OrderByDescending(c => c.Known).ThenBy(c => c.Id, StringComparer.Ordinal).Select(c => c.Json())),
                });
            }
            var present = new HashSet<string>(sideRows.Select(s => (string)s["id"]), StringComparer.Ordinal);
            foreach (var snapshot in battle.Events.SelectMany(e => new[] { (Id: e.Attacker, Faction: e.AttackerFaction), (Id: e.Victim, Faction: e.VictimFaction) })
                .Where(s => s.Id != 0).GroupBy(s => SideKey(s.Id, s.Faction)).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                if (!present.Add(snapshot.Key)) continue;
                var first = snapshot.First();
                sideRows.Add(new JObject
                {
                    ["id"] = snapshot.Key, ["kind"] = first.Faction == null ? "unknown" : first.Faction.Length == 0 ? "player" : "faction",
                    ["name"] = string.IsNullOrEmpty(first.Faction) ? name(first.Id) : first.Faction.Substring(first.Faction.IndexOf('|') + 1),
                    ["affectedBlocks"] = 0, ["destroyedBlocks"] = 0, ["damagedBlocks"] = 0, ["lostComponents"] = 0,
                    ["unpricedComponents"] = 0, ["knownCredits"] = "0", ["credits"] = "0", ["components"] = new JArray(),
                });
            }
            // Side totals are lower bounds too when some records could not be associated or captured.
            if (status != "ok" && status != "not_applicable")
                foreach (var side in sideRows.OfType<JObject>()) side["credits"] = null;
            var blocks = records.Where(r => r.Damaged || r.Destroyed).GroupBy(r => r.Token).ToList();
            var destroyedTotal = blocks.Count(g => g.Any(r => r.Destroyed));
            var result = new JObject
            {
                ["recordingInterrupted"] = interrupted,
                ["status"] = status, ["provisional"] = provisional, ["valuation"] = "game_minimum_item_price",
                ["scope"] = "mounted_components_lost_to_damage", ["affectedBlocks"] = blocks.Count,
                ["destroyedBlocks"] = destroyedTotal, ["damagedBlocks"] = blocks.Count - destroyedTotal,
                ["lostComponents"] = lostTotal, ["unpricedComponents"] = unpricedTotal,
                ["knownCredits"] = Money(knownTotal), ["credits"] = (status == "ok" || status == "not_applicable") && unpricedTotal == 0 ? Money(knownTotal) : null,
                ["sides"] = sideRows, ["ambiguousRecords"] = ambiguous, ["unmatchedRecords"] = unmatched,
                ["invalidRecords"] = invalid, ["missingAggregates"] = missing, ["truncated"] = truncated,
            };
            if (interrupted)
                foreach (var item in sideRows.OfType<JObject>().Concat(new[] { result }))
                    foreach (var field in new[] { "affectedBlocks", "destroyedBlocks", "damagedBlocks" }) item[field] = null;
            return result;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace SentisWatcher.Web
{
    /// <summary>Explicit publication whitelist. Never serialize an internal battle report directly to the site.</summary>
    public static class PublicBattleProjection
    {
        public const int MaxParticipants = 512, MaxBreakdown = 2000, MaxJumps = 500;
        private static readonly Regex LongId = new Regex(@"(?<!\d)\d{15,20}(?!\d)", RegexOptions.Compiled);

        public static string Id(string internalId)
        {
            using (var hash = SHA256.Create())
                return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes("battle:" + internalId)).Take(16).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        internal static string Text(JToken value, int max = 120)
        {
            var text = value?.Type == JTokenType.String ? (string)value : "";
            text = LongId.Replace(text ?? "", "Неизвестный участник");
            text = new string(text.Where(c => !char.IsControl(c)).Take(max).ToArray());
            return text;
        }

        private static JToken Number(JToken value) => value != null && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float) ? value.DeepClone() : JValue.CreateNull();
        private static void Numbers(JObject target, JToken source, params string[] names)
        {
            foreach (var name in names) target[name] = Number(source?[name]);
        }

        public static JObject Summary(JToken raw)
        {
            var result = new JObject { ["id"] = Id((string)raw["id"]), ["title"] = Text(raw["title"], 400) };
            Numbers(result, raw, "start", "end", "endedAt", "duration", "damage", "combatDamage", "hits", "kills", "destroyed");
            var participants = raw["participants"] as JArray ?? new JArray();
            var factions = raw["factions"] as JArray ?? new JArray();
            result["participants"] = new JArray(participants.Take(64).Select(n => Text(n)));
            result["factions"] = new JArray(factions.Take(32).Select(n => Text(n)));
            result["participantsTotal"] = participants.Count;
            result["truncated"] = participants.Count > 64 || factions.Count > 32;
            result["weapons"] = new JArray((raw["weapons"] as JArray ?? new JArray()).Take(3).Select(n => Text(n)));
            result["outcome"] = Outcome(raw["outcome"]);
            return result;
        }

        private static JObject Outcome(JToken raw)
        {
            if (!(raw is JObject)) return null;
            var sides = new JArray();
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            var source = raw["sides"] as JArray ?? new JArray();
            foreach (var side in source.Take(64))
            {
                var id = "s" + (sides.Count + 1);
                ids[(string)side["id"] ?? ""] = id;
                var item = new JObject { ["id"] = id, ["kind"] = Text(side["kind"], 20), ["name"] = Text(side["name"]) };
                Numbers(item, side, "dealt", "taken", "balance", "score");
                item["lossCredits"] = Credits(side["lossCredits"]);
                sides.Add(item);
            }
            string Ref(JToken id) => id != null && ids.TryGetValue((string)id ?? "", out var value) ? value : null;
            var weights = new JObject(); Numbers(weights, raw["weights"], "dealt", "taken", "losses");
            return new JObject
            {
                ["method"] = Text(raw["method"], 40), ["economyIncluded"] = (bool?)raw["economyIncluded"] == true, ["weights"] = weights,
                ["status"] = Text(raw["status"], 20), ["reason"] = Text(raw["reason"], 40),
                ["winner"] = Ref(raw["winner"]), ["leaders"] = new JArray((raw["leaders"] as JArray ?? new JArray()).Select(Ref).Where(id => id != null)),
                ["sides"] = sides, ["truncated"] = source.Count > 64,
            };
        }

        private static JToken Credits(JToken raw) => raw?.Type == JTokenType.String &&
            Regex.IsMatch((string)raw, @"\A[0-9]{1,29}\z") ? raw.DeepClone() : JValue.CreateNull();

        private static JObject Economy(JToken raw)
        {
            if (!(raw is JObject)) return null;
            var result = new JObject
            {
                ["status"] = Text(raw["status"], 20), ["scope"] = "mounted_components_lost_to_damage",
                ["valuation"] = "game_minimum_item_price", ["provisional"] = (bool?)raw["provisional"] == true,
                ["recordingInterrupted"] = (bool?)raw["recordingInterrupted"] == true,
                ["credits"] = Credits(raw["credits"]), ["knownCredits"] = Credits(raw["knownCredits"]),
            };
            Numbers(result, raw, "affectedBlocks", "destroyedBlocks", "damagedBlocks", "lostComponents", "unpricedComponents",
                "ambiguousRecords", "unmatchedRecords", "invalidRecords", "missingAggregates");
            var sides = new JArray(); var truncated = (bool?)raw["truncated"] == true;
            var source = raw["sides"] as JArray ?? new JArray();
            foreach (var side in source.Take(64))
            {
                var item = new JObject
                {
                    ["id"] = "l" + (sides.Count + 1), ["kind"] = Text(side["kind"], 20), ["name"] = Text(side["name"]),
                    ["credits"] = Credits(side["credits"]), ["knownCredits"] = Credits(side["knownCredits"]),
                };
                Numbers(item, side, "affectedBlocks", "destroyedBlocks", "damagedBlocks", "lostComponents", "unpricedComponents");
                var components = side["components"] as JArray ?? new JArray();
                item["components"] = new JArray(components.Take(256).Select(component =>
                {
                    var cost = new JObject
                    {
                        ["id"] = Text(component["id"]), ["priceStatus"] = Text(component["priceStatus"], 20),
                        ["credits"] = Credits(component["credits"]), ["knownCredits"] = Credits(component["knownCredits"]),
                    };
                    Numbers(cost, component, "lost", "unpriced", "unitPrice");
                    return cost;
                }));
                truncated |= components.Count > 256; sides.Add(item);
            }
            result["sides"] = sides; result["truncated"] = truncated || source.Count > 64;
            return result;
        }

        public static JObject Report(JToken raw)
        {
            var participants = new JArray();
            var people = new Dictionary<string, string>(StringComparer.Ordinal);
            var entities = new Dictionary<string, JObject>(StringComparer.Ordinal);
            string Person(JToken p)
            {
                if (p == null) return null;
                var key = (string)p["id"] + ":" + (p["factionKey"] == null || p["factionKey"].Type == JTokenType.Null ? "?" : "=" + (string)p["factionKey"]);
                if (people.TryGetValue(key, out var known)) return known;
                if (participants.Count >= MaxParticipants) return null;
                var id = "p" + (participants.Count + 1);
                people[key] = id;
                participants.Add(new JObject { ["id"] = id, ["name"] = Text(p["name"]), ["faction"] = Text(p["faction"]) });
                return id;
            }
            string Entity(JToken rawId, string kind, JToken rawName = null)
            {
                var key = (string)rawId;
                if (string.IsNullOrEmpty(key) || key == "0") return null;
                if (!entities.TryGetValue(key, out var entity))
                {
                    entity = new JObject { ["id"] = "e" + (entities.Count + 1), ["kind"] = kind };
                    entities[key] = entity;
                }
                else if (kind == "grid") entity["kind"] = "grid";
                var name = Text(rawName);
                if (!string.IsNullOrWhiteSpace(name) && (string)rawName != key && !name.TrimStart().StartsWith("to=", StringComparison.OrdinalIgnoreCase))
                    entity["name"] = name;
                return (string)entity["id"];
            }
            var rawParticipants = raw["participants"] as JArray ?? new JArray();
            foreach (var p in rawParticipants.Take(MaxParticipants))
            {
                var id = Person(p["person"]);
                var target = participants.OfType<JObject>().First(a => (string)a["id"] == id);
                Numbers(target, p, "dealt", "taken", "kills", "deaths", "destroyed", "lost", "combatDealt", "combatTaken", "shots", "hitShots", "accuracy", "jumpCount");
                target["accuracyStatus"] = Text(p["accuracyStatus"], 20);
                target["attributionKnown"] = p["attributionKnown"]?.Type == JTokenType.Boolean && (bool)p["attributionKnown"];
            }
            var rawBreakdown = raw["breakdown"] as JArray ?? new JArray();
            var breakdown = new JArray();
            foreach (var row in rawBreakdown.Take(MaxBreakdown))
            {
                var targetKind = Text(row["targetKind"], 20);
                var item = new JObject
                {
                    ["attacker"] = Person(row["attacker"]), ["victim"] = Person(row["victim"]),
                    ["attackerKind"] = Text(row["attackerKind"], 20), ["targetKind"] = targetKind,
                    ["source"] = Entity(row["sourceId"], "unknown", row["source"]),
                    ["target"] = Entity(row["targetId"], targetKind == "block" ? "grid" : "character", row["target"]),
                    ["weapon"] = Text(row["weapon"]), ["relation"] = Text(row["relation"], 20),
                };
                Numbers(item, row, "damage", "hits", "kills", "destroyed");
                breakdown.Add(item);
            }
            var timeline = new JArray();
            foreach (var row in (raw["timeline"] as JArray ?? new JArray()).Take(120))
            {
                var item = new JObject();
                Numbers(item, row, "t", "width", "damage", "combatDamage");
                timeline.Add(item);
            }
            var rawJumps = raw["jumps"] as JArray ?? new JArray();
            var jumps = new JArray();
            foreach (var row in rawJumps.Take(MaxJumps))
            {
                var actors = new JArray((row["actors"] as JArray ?? new JArray()).Take(64).Select(a => new JObject
                {
                    ["name"] = Text(a["name"]), ["role"] = Text(a["role"], 20),
                }));
                var item = new JObject { ["grid"] = Entity(row["gridId"], "grid", row["grid"]), ["direction"] = Text(row["direction"], 20), ["actors"] = actors };
                Numbers(item, row, "t", "distance");
                jumps.Add(item);
            }
            foreach (var entity in entities.Values.Where(e => string.IsNullOrWhiteSpace((string)e["name"])))
                entity["name"] = ((string)entity["kind"] == "grid" ? "Грид " : (string)entity["kind"] == "character" ? "Персонаж " : "Объект ") + ((string)entity["id"]).Substring(1);
            var mvp = raw["mvp"];
            var publicMvp = mvp is JObject ? new JObject
            {
                ["status"] = Text(mvp["status"], 20), ["criteria"] = Text(mvp["criteria"]),
                ["hitTieBreakAvailable"] = mvp["hitTieBreakAvailable"]?.Type == JTokenType.Boolean && (bool)mvp["hitTieBreakAvailable"],
                ["players"] = new JArray((mvp["players"] as JArray ?? new JArray()).Take(MaxParticipants).Select(Person).Where(id => id != null)),
            } : null;
            var result = new JObject
            {
                ["summary"] = Summary(raw["summary"]), ["mvp"] = publicMvp, ["participants"] = participants,
                ["economy"] = Economy(raw["economy"]),
                ["entities"] = new JArray(entities.Values), ["breakdown"] = breakdown, ["timeline"] = timeline, ["jumps"] = jumps,
                ["truncated"] = rawParticipants.Count > MaxParticipants || rawBreakdown.Count > MaxBreakdown || rawJumps.Count > MaxJumps || rawJumps.Any(j => (j["actors"] as JArray)?.Count > 64),
            };
            Numbers(result, raw, "timelineFrom", "timelineTo", "accuracyFrom", "accuracyTo");
            return result;
        }
    }
}

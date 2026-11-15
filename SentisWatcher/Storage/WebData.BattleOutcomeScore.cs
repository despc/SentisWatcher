using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SentisWatcher.Storage
{
    public sealed partial class WebData
    {
        private static bool ReadOutcomeCredits(JToken token, out decimal value)
        {
            value = 0;
            var text = token?.Type == JTokenType.String ? (string)token : null;
            return text != null && text.Length > 0 && text.Length <= 29 && text.All(c => c >= '0' && c <= '9') &&
                decimal.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static JObject ScoreOutcome(IEnumerable<JObject> source, JObject economy, bool provisional)
        {
            var sides = source.ToList();
            bool Identified(JObject side) => (string)side["id"] != "unknown:0" && (string)side["id"] != "player:0";
            var engaged = sides.Where(s => Identified(s) && ((double)s["dealt"] > 0 || (double)s["taken"] > 0)).ToList();
            var losses = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var economyIncluded = economy != null && ((string)economy["status"] == "ok" || (string)economy["status"] == "not_applicable") &&
                (bool?)economy["truncated"] != true && ReadOutcomeCredits(economy["credits"], out _);
            if (economyIncluded)
            {
                foreach (var side in (economy["sides"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var id = (string)side["id"];
                    if (id == null || losses.ContainsKey(id) || !ReadOutcomeCredits(side["credits"], out var loss))
                    { economyIncluded = false; break; }
                    losses.Add(id, loss);
                }
                economyIncluded &= engaged.All(s => losses.ContainsKey((string)s["id"]));
            }
            var dealtTotal = engaged.Sum(s => (double)s["dealt"]);
            var takenTotal = engaged.Sum(s => (double)s["taken"]);
            var lossTotal = economyIncluded ? engaged.Sum(s => losses[(string)s["id"]]) : 0;
            // Normalize each dimension separately. HP and credits are never added as raw quantities.
            // Without complete prices/coverage, drop the economic term for everyone, not just the unknown side.
            var dealtWeight = economyIncluded ? 0.35 : 0.70;
            var takenWeight = economyIncluded ? 0.15 : 0.30;
            var lossWeight = economyIncluded ? 0.50 : 0;
            foreach (var side in sides)
            {
                var id = (string)side["id"];
                side["lossCredits"] = economyIncluded && losses.TryGetValue(id, out var cost) ? (JToken)Money(cost) : null;
                var damage = dealtTotal > 0 ? (double)side["dealt"] / dealtTotal : 0;
                var received = takenTotal > 0 ? (double)side["taken"] / takenTotal : 0;
                var material = economyIncluded && lossTotal > 0 && losses.TryGetValue(id, out var loss) ? (double)(loss / lossTotal) : 0;
                side["score"] = Identified(side) ? (JToken)Math.Round(100 * (dealtWeight * damage - takenWeight * received - lossWeight * material), 6) : null;
            }
            // A zero-activity bystander or an anonymous source cannot win by simply avoiding all penalties.
            var candidates = engaged.Where(s => (double)s["dealt"] > 0).OrderByDescending(s => (double)s["score"])
                .ThenBy(s => (string)s["id"], StringComparer.Ordinal).ToList();
            var leaders = candidates.Where(s => (double)s["score"] == (double)candidates[0]["score"]).ToList();
            var ranked = sides.OrderByDescending(s => Identified(s) && (double)s["dealt"] > 0)
                .ThenByDescending(s => (double?)s["score"] ?? double.MinValue).ThenBy(s => (string)s["id"], StringComparer.Ordinal);
            return new JObject
            {
                ["status"] = provisional ? "provisional" : leaders.Count == 0 ? "unknown" : leaders.Count == 1 ? "winner" : "draw",
                ["reason"] = leaders.Count == 0 ? "no_attributed_combat_damage" : economyIncluded ? "weighted_damage_and_losses" : "damage_only_incomplete_economy",
                ["method"] = "weighted_shares_v1", ["economyIncluded"] = economyIncluded,
                ["weights"] = new JObject { ["dealt"] = dealtWeight, ["taken"] = takenWeight, ["losses"] = lossWeight },
                ["winner"] = !provisional && leaders.Count == 1 ? leaders[0]["id"].DeepClone() : JValue.CreateNull(),
                ["leaders"] = new JArray(leaders.Select(s => (string)s["id"])), ["sides"] = new JArray(ranked),
            };
        }
    }
}

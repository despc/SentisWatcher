using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SentisWatcher.Storage
{
    public sealed partial class WebData
    {
        private static string SideKey(long identity, string faction) => string.IsNullOrEmpty(faction)
            ? (faction == null ? "unknown:" : "player:") + identity
            : "faction:" + faction.Split('|')[0];

        private static bool Competitive(BattleEvent e) => e.Seed &&
            SideKey(e.Attacker, e.AttackerFaction) != SideKey(e.Victim, e.VictimFaction);

        private static JObject BattleOutcome(BattleGroup battle, Func<long, string> name, bool provisional, JObject economy)
        {
            var sides = new Dictionary<string, JObject>(StringComparer.Ordinal);
            JObject Side(long identity, string faction)
            {
                var key = SideKey(identity, faction);
                if (!sides.TryGetValue(key, out var side))
                {
                    side = new JObject
                    {
                        ["id"] = key, ["kind"] = faction == null ? "unknown" : faction.Length == 0 ? "player" : "faction",
                        ["name"] = string.IsNullOrEmpty(faction) ? name(identity) : faction.Substring(faction.IndexOf('|') + 1),
                        ["dealt"] = 0.0, ["taken"] = 0.0,
                    };
                    sides[key] = side;
                }
                return side;
            }
            foreach (var e in battle.Events.Where(Competitive))
            {
                var attacker = Side(e.Attacker, e.AttackerFaction);
                var victim = Side(e.Victim, e.VictimFaction);
                attacker["dealt"] = (double)attacker["dealt"] + e.Amount;
                victim["taken"] = (double)victim["taken"] + e.Amount;
            }
            foreach (var e in battle.Events.Where(e => e.Continues && !e.Seed && e.Relation != "ally" && e.Victim != 0))
            {
                var victim = Side(e.Victim, e.VictimFaction);
                victim["taken"] = (double)victim["taken"] + e.Amount;
            }
            foreach (var s in sides.Values)
            {
                s["balance"] = R((double)s["dealt"] - (double)s["taken"]);
                s["dealt"] = R((double)s["dealt"]); s["taken"] = R((double)s["taken"]);
            }
            return ScoreOutcome(sides.Values, economy, provisional);
        }

        private (List<JObject> Participants, JObject Mvp) BattleStatistics(BattleGroup battle, List<BattleGroup> all,
            List<Dictionary<string, object>> jumps, long gap, Func<long, string, object> person, bool ongoing)
        {
            var events = battle.Events;
            var shots = ReadBattleEvents(battle.Start - gap, Math.Min(Clock.Now, battle.End + gap), shots: true);
            var combatEntities = new HashSet<long>(events.SelectMany(e => new[] { e.Source, e.Target }).Where(id => id != 0));
            shots = shots.Where(s => combatEntities.Contains(s.Source)).ToList();
            var otherSources = all.Where(g => g != battle && g.Start - gap <= battle.End + gap && g.End + gap >= battle.Start - gap)
                .Select(g => new { Group = g, Sources = new HashSet<long>(g.Events.SelectMany(e => new[] { e.Source, e.Target }).Where(id => id != 0)) }).ToList();
            var people = events.SelectMany(e => new[] { (Id: e.Attacker, Faction: e.AttackerFaction), (Id: e.Victim, Faction: e.VictimFaction) }).Distinct().ToList();
            foreach (var s in shots.Where(e => e.Attacker != 0))
                if (!people.Contains((s.Attacker, s.AttackerFaction))) people.Add((s.Attacker, s.AttackerFaction));
            var recordedNames = Names(battle.Start - gap, battle.End + gap, people.Select(p => p.Id).Where(id => id != 0).Distinct().ToList(), resolveLive: false);
            var jumpActors = jumps.Where(j => (long)j["t"] >= battle.Start && (long)j["t"] <= battle.End + gap)
                .SelectMany(j => JArray.FromObject(j["actors"])).ToList();
            foreach (var actor in jumpActors)
                if (long.TryParse((string)actor["id"], out var id) && id != 0 && !people.Any(p => p.Id == id)) people.Add((id, null));
            var participants = new List<JObject>();
            foreach (var p in people)
            {
                var dealt = events.Where(e => e.Attacker == p.Id && e.AttackerFaction == p.Faction).ToList();
                var taken = events.Where(e => e.Victim == p.Id && e.VictimFaction == p.Faction).ToList();
                var fire = shots.Where(s => s.Attacker == p.Id && s.AttackerFaction == p.Faction).ToList();
                var ambiguous = fire.Any(s => otherSources.Any(o => s.T >= o.Group.Start - gap && s.T <= o.Group.End + gap && o.Sources.Contains(s.Source)));
                var legacy = dealt.Where(e => e.Kind == "hit").Any(e => e.Tracking != 3) || fire.Any(e => e.Tracking != 3);
                var launched = fire.Where(e => e.Kind == "round").Sum(e => (long)e.Count);
                var hits = fire.Where(e => e.Kind == "round_hit").Sum(e => (long)e.Count);
                var missingLaunches = fire.Where(e => e.Kind == "shot").GroupBy(e => e.Weapon.Split('/')[0])
                    .Any(g => g.Sum(e => (long)e.Count) > fire.Where(e => e.Kind == "round" && e.Weapon.Split('/')[0] == g.Key).Sum(e => (long)e.Count));
                var accuracyStatus = ambiguous ? "ambiguous" : legacy ? "unavailable" : hits > launched || missingLaunches ? "incomplete" : launched == 0 ? "no_shots" : "ok";
                var jumpCount = jumps.Count(j => (long)j["t"] >= battle.Start && (long)j["t"] <= battle.End + gap &&
                    JArray.FromObject(j["actors"]).Any(a => (string)a["id"] == p.Id.ToString()));
                double Damage(IEnumerable<BattleEvent> rows) => R(rows.Where(e => e.Kind == "hit" || e.Kind == "grind").Sum(e => e.Amount));
                var identity = JObject.FromObject(person(p.Id, p.Faction));
                if ((string)identity["name"] == p.Id.ToString() && recordedNames.TryGetValue(p.Id, out var recordedName)) identity["name"] = recordedName;
                var actorName = jumpActors.FirstOrDefault(a => (string)a["id"] == p.Id.ToString())?["name"];
                if ((string)identity["name"] == p.Id.ToString() && actorName != null) identity["name"] = actorName.DeepClone();
                participants.Add(new JObject
                {
                    ["person"] = identity,
                    ["dealt"] = Damage(dealt), ["taken"] = Damage(taken),
                    ["combatDealt"] = Damage(dealt.Where(Competitive)),
                    ["combatTaken"] = Damage(taken.Where(e => Competitive(e) || e.Continues && !e.Seed && e.Relation != "ally")),
                    ["kills"] = dealt.Where(e => e.Kind == "kill").Sum(e => e.Count), ["deaths"] = taken.Where(e => e.Kind == "kill").Sum(e => e.Count),
                    ["destroyed"] = dealt.Where(e => e.Kind == "destroyed").Sum(e => e.Count), ["lost"] = taken.Where(e => e.Kind == "destroyed").Sum(e => e.Count),
                    ["shots"] = launched, ["hitShots"] = hits, ["accuracy"] = accuracyStatus == "ok" ? (double?)R(100.0 * hits / launched) : null,
                    ["accuracyStatus"] = accuracyStatus, ["jumpCount"] = jumpCount,
                    ["attributionKnown"] = dealt.Where(e => e.Kind == "hit" || e.Kind == "grind").All(e => e.Attribution != null && e.Attribution != "unknown"),
                });
            }
            // A faction change must not split a person's MVP score. Keep the strongest snapshot as their display reference.
            var mvpIds = new HashSet<string>(participants.Where(p => (string)p["person"]["id"] != "0" && (double)p["combatDealt"] > 0)
                .Select(p => (string)p["person"]["id"]), StringComparer.Ordinal);
            var players = participants.Where(p => mvpIds.Contains((string)p["person"]["id"]))
                .GroupBy(p => (string)p["person"]["id"]).Select(g => new
                {
                    Person = g.OrderByDescending(p => (double)p["combatDealt"]).First()["person"],
                    Dealt = R(g.Sum(p => (double)p["combatDealt"])), Taken = R(g.Sum(p => (double)p["combatTaken"])),
                    Hits = g.Sum(p => (long)p["hitShots"]),
                    Complete = g.All(p => new[] { "ok", "no_shots" }.Contains((string)p["accuracyStatus"])),
                }).OrderByDescending(p => p.Dealt).ThenBy(p => p.Taken).ToList();
            var contenders = players.Where(p => p.Dealt == players[0].Dealt && p.Taken == players[0].Taken).ToList();
            var complete = contenders.All(p => p.Complete);
            if (complete && contenders.Count > 1) contenders = contenders.Where(p => p.Hits == contenders.Max(c => c.Hits)).ToList();
            var mvp = new JObject
            {
                ["status"] = ongoing ? "provisional" : contenders.Count == 0 ? "none" : contenders.Count == 1 ? "winner" : !complete ? "insufficient" : "tie",
                ["players"] = new JArray(contenders.Select(p => p.Person.DeepClone())),
                ["criteria"] = "combatDealt_desc,combatTaken_asc,hitShots_desc", ["hitTieBreakAvailable"] = complete,
            };
            return (participants.OrderByDescending(p => (double)p["taken"]).ToList(), mvp);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SentisWatcher.Storage
{
    public sealed partial class WebData
    {
        private sealed class BattleEvent
        {
            public long T, RowId, Attacker, Source, Target, Victim;
            public string Kind, AttackerKind, Weapon, TargetKind, Relation, AttackerFaction, VictimFaction, Attribution, DamageKey;
            public int? Tracking, EconomyTracking;
            public double Amount, X, Y, Z;
            public int Count;
            public bool Seed => (Kind == "hit" || Kind == "grind") && Amount > 0 &&
                (Relation == "enemy" || Relation == "neutral") && AttackerKind != "animal" && TargetKind != "animal";
            public bool Continues => Seed || (Kind == "hit" && Amount > 0 && Relation != "own" &&
                AttackerKind != "animal" && TargetKind != "animal" &&
                !new[] { "Temperature", "LowPressure", "Asphyxia", "Radioactivity" }.Any(k => Weapon.StartsWith(k + ":", StringComparison.OrdinalIgnoreCase)));
        }

        private sealed class BattleGroup
        {
            public readonly List<BattleEvent> Activity = new List<BattleEvent>();
            public readonly List<BattleEvent> Events = new List<BattleEvent>();
            public long Start = long.MaxValue, End = long.MinValue;
            public void Add(BattleEvent e)
            {
                Activity.Add(e);
                Start = Math.Min(Start, e.T);
                End = Math.Max(End, e.T);
            }
            public string Id => Activity.Where(e => e.Seed).OrderBy(e => e.T).ThenBy(e => e.RowId).Select(e => e.T + ":" + e.RowId).First();
        }

        private static bool BattleNear(BattleEvent a, BattleEvent b, long gap, double radius2) =>
            Math.Abs(a.T - b.T) <= gap &&
            (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z) <= radius2;

        private List<BattleEvent> ReadBattleEvents(long from, long to, bool shots = false, bool details = true)
        {
            var rows = new List<BattleEvent>();
            foreach (var db in Days(from, to))
                using (db)
                {
                    if (!HasTable(db, "damage")) continue;
                    var af = WatcherStore.HasColumn(db, "damage", "attacker_faction") ? "attacker_faction" : "NULL";
                    var vf = WatcherStore.HasColumn(db, "damage", "victim_faction") ? "victim_faction" : "NULL";
                    var credit = details && WatcherStore.HasColumn(db, "damage", "attribution") ? "attribution" : "NULL";
                    var tracking = details && WatcherStore.HasColumn(db, "damage", "shot_tracking") ? "shot_tracking" : "NULL";
                    var economy = details && WatcherStore.HasColumn(db, "damage", "economy_tracking") ? "economy_tracking" : "NULL";
                    var damageKey = details && WatcherStore.HasColumn(db, "damage", "damage_key") ? "damage_key" : "NULL";
                    var kinds = shots ? "'shot','round','round_hit'" : "'hit','grind','destroyed','kill'";
                    using (var cmd = Command(db, "SELECT id,t,kind,attacker,attacker_kind,attacker_entity,weapon,target,target_kind,victim,relation,amount,count,x,y,z," +
                        af + "," + vf + "," + credit + "," + tracking + "," + economy + "," + damageKey + " FROM damage WHERE t BETWEEN @a AND @b AND kind IN (" + kinds + ") ORDER BY t,id", ("@a", from), ("@b", to)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) rows.Add(new BattleEvent
                        {
                            RowId = L(r, 0), T = L(r, 1), Kind = S(r, 2), Attacker = L(r, 3), AttackerKind = S(r, 4), Source = L(r, 5),
                            Weapon = S(r, 6), Target = L(r, 7), TargetKind = S(r, 8), Victim = L(r, 9), Relation = S(r, 10),
                            Amount = D(r, 11), Count = (int)L(r, 12), X = D(r, 13), Y = D(r, 14), Z = D(r, 15),
                            AttackerFaction = r.IsDBNull(16) ? null : r.GetString(16), VictimFaction = r.IsDBNull(17) ? null : r.GetString(17),
                            Attribution = r.IsDBNull(18) ? null : r.GetString(18), Tracking = r.IsDBNull(19) ? (int?)null : (int)L(r, 19),
                            EconomyTracking = r.IsDBNull(20) ? (int?)null : (int)L(r, 20), DamageKey = r.IsDBNull(21) ? null : r.GetString(21),
                        });
                }
            return rows;
        }

        private static bool BattleRelated(BattleEvent a, BattleEvent b) =>
            (a.Attacker != 0 && (a.Attacker == b.Attacker || a.Attacker == b.Victim)) ||
            (a.Victim != 0 && (a.Victim == b.Attacker || a.Victim == b.Victim)) ||
            (a.Target != 0 && a.Target == b.Target) || (a.Source != 0 && a.Source == b.Source);

        // Activity is chronological, including after group merges. Search only the relevant time window,
        // rather than revisiting an entire hours-long fight for every collateral/destruction event.
        private static bool NearActivity(BattleGroup group, BattleEvent e, long gap, double radius2, bool related = false)
        {
            var activity = group.Activity;
            var low = 0; var high = activity.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (activity[middle].T <= e.T + gap) low = middle + 1; else high = middle;
            }
            for (var i = low - 1; i >= 0 && activity[i].T >= e.T - gap; i--)
                if (BattleNear(e, activity[i], gap, radius2) && (!related || BattleRelated(e, activity[i]))) return true;
            return false;
        }

        private static List<BattleGroup> DetectBattles(List<BattleEvent> rows, long gap, double radius2)
        {
            var groups = new List<BattleGroup>();
            var active = new List<BattleGroup>();
            foreach (var e in rows.Where(e => e.Continues))
            {
                active.RemoveAll(g => e.T - g.End > gap);
                var matches = active.Where(g => NearActivity(g, e, gap, radius2, related: !e.Seed)).ToList();
                // Keep provisional unattributed damage chains so a battle can be traced backwards across midnight.
                // Only chains containing hostile damage are exposed as battles. Ambiguous collateral never merges them.
                if (!e.Seed && matches.Count > 1) continue;
                var group = matches.FirstOrDefault();
                if (group == null)
                {
                    group = new BattleGroup();
                    groups.Add(group);
                    active.Add(group);
                }
                foreach (var other in matches.Skip(1))
                {
                    foreach (var hit in other.Activity) group.Add(hit);
                    groups.Remove(other);
                    active.Remove(other);
                }
                if (matches.Count > 1) group.Activity.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.RowId.CompareTo(b.RowId));
                group.Add(e);
            }
            foreach (var g in groups) g.Events.AddRange(g.Activity);
            var assigned = new HashSet<BattleEvent>(groups.SelectMany(g => g.Activity));
            // Destruction and other context do not refresh the inactivity timer and are never counted twice.
            foreach (var e in rows.Where(e => !assigned.Contains(e) && e.AttackerKind != "animal" && e.TargetKind != "animal"))
            {
                var matches = groups.Where(g => e.T >= g.Start - 2000 && e.T <= g.End + 2000 &&
                    NearActivity(g, e, gap, radius2)).Take(2).ToList();
                if (matches.Count == 1) matches[0].Events.Add(e);
            }
            return groups;
        }

        /// <summary>
        /// A battle stays open until two minutes without related damage. The requested range selects battles, not
        /// their boundaries: read whole days and follow connected fights across midnight until silence is observed.
        /// The 31-day scan cap bounds pathological continuous fights; incomplete boundaries are explicitly marked.
        /// </summary>
        public object Battles(long from, long to, int gapSeconds = 120, int radius = 5000, string battle = null)
        {
            gapSeconds = Math.Max(10, Math.Min(300, gapSeconds));
            radius = Math.Max(100, Math.Min(20000, radius));
            if (to < from) return new { error = "Конец интервала раньше начала" };
            var gap = gapSeconds * 1000L;
            var radius2 = (double)radius * radius;
            var scanFrom = Clock.ToMs(Clock.Day(from));
            var scanTo = Clock.ToMs(Clock.Day(to).AddDays(1)) - 1;
            var rows = ReadBattleEvents(scanFrom, scanTo, details: battle != null);
            List<BattleGroup> groups;
            List<BattleGroup> visible;
            while (true)
            {
                rows = rows.OrderBy(e => e.T).ThenBy(e => e.RowId).ToList();
                groups = DetectBattles(rows, gap, radius2);
                visible = groups.Where(g => g.Start <= to + gap && g.End + gap >= from).ToList();
                var before = visible.Any(g => g.Start - scanFrom < gap);
                var after = visible.Any(g => scanTo - g.End < gap);
                if ((!before && !after) || scanTo - scanFrom >= 31 * 86400000L) break;
                if (before)
                {
                    var earlier = scanFrom - 86400000L;
                    rows.AddRange(ReadBattleEvents(earlier, scanFrom - 1, details: battle != null));
                    scanFrom = earlier;
                }
                if (after)
                {
                    var later = scanTo + 86400000L;
                    rows.AddRange(ReadBattleEvents(scanTo + 1, later, details: battle != null));
                    scanTo = later;
                }
            }
            var now = Clock.Now;
            var contextGroups = groups.Where(g => g.Activity.Any(e => e.Seed)).ToList();
            var ignored = rows.Count - groups.Where(g => g.Activity.Any(e => e.Seed)).Sum(g => g.Events.Count);
            groups = visible.Where(g => g.Activity.Any(e => e.Seed) && g.Start <= to && g.End + gap >= from).ToList();
            var ids = groups.SelectMany(g => g.Events).SelectMany(e => battle == null ? new[] { e.Attacker, e.Victim } : new[] { e.Attacker, e.Victim, e.Source, e.Target }).Where(id => id != 0).Distinct().ToList();
            var names = Names(scanFrom, scanTo, ids, resolveLive: false);
            string Name(long id) => id == 0 ? "Неизвестно / без владельца" : names.TryGetValue(id, out var n) ? n : id.ToString();
            string Faction(string value) => value == null ? "Фракция неизвестна" : value.Length == 0 ? "Без фракции" : value.Substring(value.IndexOf('|') + 1);
            double Damage(IEnumerable<BattleEvent> events) => R(events.Where(e => e.Kind == "hit" || e.Kind == "grind").Sum(e => e.Amount));
            object Person(long id, string faction) => new { id = id.ToString(), name = Name(id), faction = Faction(faction), factionKey = faction };
            string Title(BattleGroup g)
            {
                // Use actual opposing combatants, not nearby collateral. Reciprocal attacks must not duplicate tags;
                // faction IDs distinguish renamed snapshots and unrelated factions with identical display names.
                (string Key, string Label) Side(long id, string faction)
                {
                    if (string.IsNullOrEmpty(faction))
                        return ("identity:" + id, Name(id) + (faction == null ? " (фракция неизвестна)" : " (без фракции)"));
                    var split = faction.IndexOf('|');
                    var label = faction.Substring(split + 1);
                    var close = label.IndexOf(']');
                    var tag = label.StartsWith("[", StringComparison.Ordinal) && close > 1 ? label.Substring(1, close - 1) : label;
                    return ("faction:" + (split >= 0 ? faction.Substring(0, split) : faction), tag);
                }
                var sides = g.Activity.Where(e => e.Seed).SelectMany(e => new[]
                {
                    (Side: Side(e.Attacker, e.AttackerFaction), Weight: e.Amount),
                    (Side: Side(e.Victim, e.VictimFaction), Weight: e.Amount),
                }).GroupBy(s => s.Side.Key).Select(s => new
                {
                    Label = s.First().Side.Label, Weight = s.Sum(p => p.Weight), Key = s.Key,
                }).OrderByDescending(s => s.Weight).ThenBy(s => s.Key, StringComparer.Ordinal).Select(s => s.Label).ToList();
                return sides.Count == 1 ? "Бой внутри " + sides[0] : string.Join(" vs ", sides);
            }
            object Summary(BattleGroup g, JObject economy = null) => new
            {
                title = Title(g),
                outcome = economy == null ? null : BattleOutcome(g, Name, now < g.End + gap || g.Start - scanFrom < gap || scanTo - g.End < gap, economy),
                id = g.Id, start = g.Start, end = g.End, duration = g.End - g.Start,
                clippedStart = g.Start - scanFrom < gap, clippedEnd = scanTo - g.End < gap,
                ongoing = now < g.End + gap,
                endedAt = now >= g.End + gap && scanTo - g.End >= gap ? (long?)(g.End + gap) : null,
                damage = Damage(g.Events), combatDamage = Damage(g.Activity.Where(e => e.Seed)), hits = g.Events.Where(e => e.Kind == "hit").Sum(e => e.Count),
                kills = g.Events.Where(e => e.Kind == "kill").Sum(e => e.Count), destroyed = g.Events.Where(e => e.Kind == "destroyed").Sum(e => e.Count),
                participants = g.Events.SelectMany(e => new[] { e.Attacker, e.Victim }).Where(id => id != 0).Distinct().Select(Name).OrderBy(n => n).ToList(),
                factions = g.Events.SelectMany(e => new[] { e.AttackerFaction, e.VictimFaction }).Distinct().Select(Faction).ToList(),
                weapons = g.Activity.Where(e => e.Seed).GroupBy(e => e.Weapon).OrderByDescending(w => Damage(w)).Take(3).Select(w => w.Key).ToList(),
                x = g.Activity.Average(e => e.X), y = g.Activity.Average(e => e.Y), z = g.Activity.Average(e => e.Z),
            };
            var selected = groups.FirstOrDefault(g => g.Id == battle);
            object report = null;
            if (selected != null)
            {
                var events = selected.Events;
                var jumps = BattleJumps(selected, gap, radius2);
                var statistics = BattleStatistics(selected, contextGroups, jumps, gap, Person,
                    now < selected.End + gap || selected.Start - scanFrom < gap || scanTo - selected.End < gap);
                var timelineFrom = Math.Min(events.Min(e => e.T), jumps.Count > 0 ? jumps.Min(j => (long)j["t"]) : selected.Start);
                var timelineTo = Math.Max(events.Max(e => e.T), jumps.Count > 0 ? jumps.Max(j => (long)j["t"]) : selected.End) + 2000;
                var width = Math.Max(2000L, (timelineTo - timelineFrom + 59) / 60);
                var bucketCount = (int)Math.Max(1, (timelineTo - timelineFrom + width - 1) / width);
                var damage = new double[bucketCount];
                var combat = new double[bucketCount];
                foreach (var e in events.Where(e => e.Kind == "hit" || e.Kind == "grind"))
                {
                    var index = (int)Math.Min(bucketCount - 1, Math.Max(0, (e.T - timelineFrom) / width));
                    damage[index] += e.Amount;
                    if (e.Seed) combat[index] += e.Amount;
                }
                var economy = BattleEconomy(selected, contextGroups, Name,
                    now < selected.End + gap || selected.Start - scanFrom < gap || scanTo - selected.End < gap);
                report = new
                {
                    summary = Summary(selected, economy),
                    participants = statistics.Participants,
                    mvp = statistics.Mvp,
                    economy,
                    accuracyFrom = selected.Start - gap, accuracyTo = selected.End + gap,
                    breakdown = events.GroupBy(e => (e.Attacker, e.AttackerFaction, e.AttackerKind, e.Source, e.Victim, e.VictimFaction, e.Target, e.TargetKind, e.Weapon, e.Relation)).Select(g => new
                    {
                        attacker = Person(g.Key.Attacker, g.Key.AttackerFaction), victim = Person(g.Key.Victim, g.Key.VictimFaction),
                        attackerKind = g.Key.AttackerKind, sourceId = g.Key.Source.ToString(), source = Name(g.Key.Source),
                        targetId = g.Key.Target.ToString(), target = Name(g.Key.Target), targetKind = g.Key.TargetKind,
                        weapon = g.Key.Weapon, relation = g.Key.Relation, damage = Damage(g),
                        hits = g.Where(e => e.Kind == "hit" || e.Kind == "grind").Sum(e => e.Count),
                        kills = g.Where(e => e.Kind == "kill").Sum(e => e.Count), destroyed = g.Where(e => e.Kind == "destroyed").Sum(e => e.Count),
                    }).OrderByDescending(g => g.damage).ToList(),
                    timelineFrom, timelineTo, jumps,
                    timeline = Enumerable.Range(0, bucketCount).Select(i => new
                    {
                        t = timelineFrom + i * width, width = Math.Min(width, timelineTo - timelineFrom - i * width),
                        damage = R(damage[i]), combatDamage = R(combat[i]),
                    }).ToList(),
                };
            }
            return new
            {
                from, to, scanFrom, scanTo, gapSeconds, radius, rows = rows.Count, ignored,
                battles = groups.OrderByDescending(g => Damage(g.Activity.Where(e => e.Seed))).ThenBy(g => g.Start).Select(g => Summary(g)).ToList(), report,
            };
        }
    }
}

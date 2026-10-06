using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;

namespace SentisWatcher.Storage
{
    public sealed partial class WebData
    {
        private sealed class DamageRow
        {
            public long T, Attacker, AttackerEntity, Target, Victim;
            public string Kind, AttackerKind, Weapon, TargetKind, Relation;
            public double Amount, X, Y, Z;
            public int Count;
        }

        /// <summary>
        /// Who fought whom over the range (table damage): per attacker (damage dealt, grinding, shots, kills, blocks
        /// destroyed, its weapons), per victim (damage taken and from whom), the damage over time by the attacker's kind,
        /// and the latest rows. <paramref name="relation"/> "enemy" keeps what was done to enemies (and to animals, and
        /// the shots, which have no target), "all" everything. With <paramref name="attacker"/> (an identity) only what that
        /// player did, with <paramref name="victim"/> only what was done to that player or theirs, with both - what the one
        /// did to the other; the lists to choose from are of all of it.
        /// </summary>
        public object Damage(long from, long to, string relation, int buckets, int recent, long attacker = 0, long victim = 0)
        {
            var rows = new List<DamageRow>();
            foreach (var db in Days(from, to))
                using (db)
                {
                    if (!HasTable(db, "damage")) continue;
                    using (var cmd = Command(db, "SELECT t, kind, attacker, attacker_kind, attacker_entity, weapon, target, target_kind, victim, relation, amount, count, x, y, z " +
                                                 "FROM damage WHERE t BETWEEN @a AND @b ORDER BY t", ("@a", from), ("@b", to)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            rows.Add(new DamageRow
                            {
                                T = r.GetInt64(0), Kind = r.GetString(1), Attacker = L(r, 2), AttackerKind = S(r, 3), AttackerEntity = L(r, 4), Weapon = S(r, 5),
                                Target = L(r, 6), TargetKind = S(r, 7), Victim = L(r, 8), Relation = S(r, 9), Amount = D(r, 10), Count = (int)L(r, 11),
                                X = D(r, 12), Y = D(r, 13), Z = D(r, 14),
                            });
                }
            var enemiesOnly = relation != "all";
            bool Kept(DamageRow d) => !enemiesOnly || d.Kind == Recording.DamageLog.Shot || d.Relation == "enemy";
            var kept = rows.Where(Kept).ToList();

            var names = Names(from, to, kept.SelectMany(d => new[] { d.Attacker, d.Victim, d.Target, d.AttackerEntity }).Where(id => id != 0).Distinct().ToList());
            string Name(long id) => id == 0 ? "" : names.TryGetValue(id, out var n) ? n : id.ToString();

            // who can be chosen, of all of it: as the source everyone who dealt something over the range (players and
            // bots; not the animals, which have no identity to tell them apart), as the target everyone who took something
            // themselves or by what is theirs (not what is nobody's). The ids as text: an identity does not fit a
            // JavaScript number, and the page sends it back as it got it
            List<Dictionary<string, object>> Choice(IEnumerable<(long Id, string Kind)> people) => people.GroupBy(p => p.Id).Select(g => new Dictionary<string, object>
            {
                ["id"] = g.Key.ToString(), ["name"] = Name(g.Key), ["kind"] = g.Select(p => p.Kind).FirstOrDefault(k => k.Length > 0) ?? "",
            }).OrderBy(p => (string)p["name"], StringComparer.CurrentCultureIgnoreCase).ToList();
            var sources = Choice(kept.Where(d => d.Attacker != 0 && (d.AttackerKind == "player" || d.AttackerKind == "bot")).Select(d => (d.Attacker, d.AttackerKind)));
            var targets = Choice(kept.Where(d => d.Victim != 0 && d.TargetKind != "animal").Select(d => (d.Victim, "")));
            if (attacker != 0) kept = kept.Where(d => d.Attacker == attacker).ToList();
            if (victim != 0) kept = kept.Where(d => d.Victim == victim).ToList();
            string Who(long identity, string kind) => kind == "animal" ? "зверь" : identity == 0 ? "" : Name(identity);

            object Attackers() => kept.GroupBy(d => (d.AttackerKind == "animal" ? 0 : d.Attacker, d.AttackerKind)).Select(g =>
            {
                var hits = g.Where(d => d.Kind == Recording.DamageLog.Hit).ToList();
                return new Dictionary<string, object>
                {
                    ["id"] = g.Key.Item1, ["kind"] = g.Key.AttackerKind, ["name"] = Who(g.Key.Item1, g.Key.AttackerKind),
                    ["damage"] = R(hits.Sum(d => d.Amount)), ["hits"] = hits.Sum(d => d.Count),
                    ["grind"] = R(g.Where(d => d.Kind == Recording.DamageLog.Grind).Sum(d => d.Amount)),
                    ["shots"] = g.Where(d => d.Kind == Recording.DamageLog.Shot).Sum(d => d.Count),
                    ["kills"] = g.Where(d => d.Kind == Recording.DamageLog.Kill).Sum(d => d.Count),
                    ["destroyed"] = g.Where(d => d.Kind == Recording.DamageLog.Destroyed).Sum(d => d.Count),
                    ["targets"] = g.Where(d => d.Target != 0).Select(d => d.Target).Distinct().Count(),
                    ["weapons"] = g.GroupBy(d => d.Weapon).OrderByDescending(w => w.Sum(d => d.Kind == Recording.DamageLog.Shot ? d.Count : d.Amount))
                        .Take(4).Select(w => w.Key).ToList(),
                    ["last"] = g.Max(d => d.T),
                };
            }).OrderByDescending(a => (double)((Dictionary<string, object>)a)["damage"] + (double)((Dictionary<string, object>)a)["grind"])
              .ThenByDescending(a => (int)((Dictionary<string, object>)a)["shots"]).ToList();

            object Victims() => kept.Where(d => d.Kind != Recording.DamageLog.Shot).GroupBy(d => (d.TargetKind == "animal" ? 0 : d.Victim, d.TargetKind)).Select(g =>
            {
                var hits = g.Where(d => d.Kind == Recording.DamageLog.Hit || d.Kind == Recording.DamageLog.Grind).ToList();
                return new Dictionary<string, object>
                {
                    ["id"] = g.Key.Item1, ["kind"] = g.Key.TargetKind, ["name"] = g.Key.TargetKind == "animal" ? "звери" : g.Key.Item1 == 0 ? "ничьё" : Name(g.Key.Item1),
                    ["damage"] = R(hits.Sum(d => d.Amount)), ["hits"] = hits.Sum(d => d.Count),
                    ["kills"] = g.Where(d => d.Kind == Recording.DamageLog.Kill).Sum(d => d.Count),
                    ["destroyed"] = g.Where(d => d.Kind == Recording.DamageLog.Destroyed).Sum(d => d.Count),
                    ["grids"] = g.Where(d => d.TargetKind == "block").Select(d => d.Target).Distinct().Select(Name).Take(5).ToList(),
                    ["by"] = g.GroupBy(d => (d.Attacker, d.AttackerKind)).OrderByDescending(a => a.Sum(d => d.Amount)).Take(4)
                        .Select(a => Who(a.Key.Attacker, a.Key.AttackerKind)).Distinct().ToList(),
                };
            }).OrderByDescending(v => (double)((Dictionary<string, object>)v)["damage"]).ToList();

            // over time: the damage done by each kind of attacker, and the shots, per bucket
            buckets = Math.Max(20, Math.Min(buckets, 600));
            var width = Math.Max(1, (to - from) / buckets);
            var kinds = new[] { "player", "bot", "animal", "npc", "none" };
            var series = kinds.ToDictionary(k => k, k => new double[buckets]);
            var shots = new int[buckets];
            foreach (var d in kept)
            {
                var i = (int)Math.Min(buckets - 1, Math.Max(0, (d.T - from) / width));
                if (d.Kind == Recording.DamageLog.Shot) shots[i] += d.Count;
                else if (d.Kind == Recording.DamageLog.Hit || d.Kind == Recording.DamageLog.Grind)
                    series[series.ContainsKey(d.AttackerKind ?? "") ? d.AttackerKind : "none"][i] += d.Amount;
            }

            var latest = kept.OrderByDescending(d => d.T).Take(Math.Max(10, Math.Min(recent, 2000))).Select(d => new Dictionary<string, object>
            {
                ["t"] = d.T, ["kind"] = d.Kind, ["attacker"] = Who(d.Attacker, d.AttackerKind), ["attackerId"] = d.Attacker, ["attackerKind"] = d.AttackerKind,
                ["from"] = d.AttackerEntity != 0 && d.AttackerEntity != d.Attacker ? Name(d.AttackerEntity) : "",
                ["weapon"] = d.Weapon, ["target"] = d.TargetKind == "block" ? Name(d.Target) : d.TargetKind == "animal" ? "зверь" : Name(d.Victim),
                ["targetId"] = d.Target, ["targetKind"] = d.TargetKind, ["victim"] = d.TargetKind == "animal" ? "" : Name(d.Victim), ["relation"] = d.Relation,
                ["amount"] = R(d.Amount), ["count"] = d.Count, ["x"] = Math.Round(d.X), ["y"] = Math.Round(d.Y), ["z"] = Math.Round(d.Z),
            }).ToList();

            return new Dictionary<string, object>
            {
                ["from"] = from, ["to"] = to, ["width"] = width, ["relation"] = enemiesOnly ? "enemy" : "all", ["rows"] = rows.Count,
                ["sources"] = sources, ["targets"] = targets, ["attacker"] = attacker.ToString(), ["victim"] = victim.ToString(),
                ["attackers"] = Attackers(), ["victims"] = Victims(),
                ["series"] = series.ToDictionary(p => p.Key, p => p.Value.Select(R).ToList()), ["shots"] = shots.ToList(),
                ["recent"] = latest,
            };
        }

        /// <summary>Names of ids (players, grids) as the day files have them, else as the game has them now.</summary>
        private Dictionary<long, string> Names(long from, long to, List<long> ids)
        {
            var names = new Dictionary<long, string>();
            if (ids.Count == 0) return names;
            foreach (var db in Days(Clock.ToMs(Clock.Day(from).AddDays(-1)), to))
                using (db)
                {
                    if (!HasTable(db, "names")) continue;
                    foreach (var chunk in ids.Select((id, i) => (id, i)).GroupBy(p => p.i / 500, p => p.id))
                        using (var cmd = Command(db, "SELECT id, name FROM names WHERE id IN (" + string.Join(",", chunk) + ")"))
                        using (var r = cmd.ExecuteReader())
                            while (r.Read())
                                if (!r.IsDBNull(1)) names[r.GetInt64(0)] = r.GetString(1);
                }
            // the rest as the game has them now - asked on the game thread (the game's collections are not to be read from
            // the web server's thread); the game not answering leaves them as numbers
            var missing = ids.Where(id => !names.ContainsKey(id)).Take(2000).ToList();
            if (missing.Count > 0)
                try
                {
                    foreach (var p in Http.Live.Names(missing)) names[p.Key] = p.Value;
                }
                catch (Exception) { }
            return names;
        }

        private static long L(SQLiteDataReader r, int column) => r.IsDBNull(column) ? 0 : r.GetInt64(column);
        private static string S(SQLiteDataReader r, int column) => r.IsDBNull(column) ? "" : r.GetString(column);
    }
}

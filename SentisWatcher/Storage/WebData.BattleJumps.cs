using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SentisWatcher.Storage
{
    public sealed partial class WebData
    {
        private sealed class BattleJump
        {
            public long T, Actor, Grid;
            public string Kind, Detail;
            public double Distance, X, Y, Z;
            public double[] Destination;
        }

        private static double[] JumpDestination(string detail)
        {
            if (string.IsNullOrEmpty(detail) || !detail.StartsWith("to=", StringComparison.Ordinal)) return null;
            var coords = detail.Substring(3).Split(' ')[0].Split(';');
            if (coords.Length != 3) return null;
            var result = new double[3];
            for (var i = 0; i < 3; i++)
                if (!double.TryParse(coords[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]) ||
                    double.IsNaN(result[i]) || double.IsInfinity(result[i])) return null;
            return result;
        }

        /// <summary>
        /// Jumps of the combat grids, or nearby jumps by combatants, including arrivals/departures in the two-minute
        /// margins. Passenger rows describe the same physical jump and are folded into its list of people.
        /// Jump events are annotations only: they never alter damage-based battle boundaries.
        /// </summary>
        private List<Dictionary<string, object>> BattleJumps(BattleGroup battle, long gap, double radius2)
        {
            var from = battle.Start - gap;
            var to = Math.Min(Clock.Now, battle.End + gap);
            var identities = new HashSet<long>(battle.Events.SelectMany(e => new[] { e.Attacker, e.Victim }).Where(id => id != 0));
            var entities = new HashSet<long>(battle.Events.SelectMany(e => new[] { e.Source, e.Target }).Where(id => id != 0));
            var jumps = new List<BattleJump>();
            bool Near(double x, double y, double z) => battle.Activity.Any(e =>
                (e.X - x) * (e.X - x) + (e.Y - y) * (e.Y - y) + (e.Z - z) * (e.Z - z) <= radius2);
            foreach (var db in Days(from, to))
                using (db)
                {
                    if (!HasTable(db, "events")) continue;
                    using (var cmd = Command(db, "SELECT t,kind,actor,entity,x,y,z,amount,detail FROM events " +
                        "WHERE t BETWEEN @a AND @b AND kind IN ('jump','jump_passenger') ORDER BY t,id", ("@a", from), ("@b", to)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var jump = new BattleJump
                            {
                                T = L(r, 0), Kind = S(r, 1), Actor = L(r, 2), Grid = L(r, 3), X = D(r, 4), Y = D(r, 5), Z = D(r, 6),
                                Distance = D(r, 7), Detail = S(r, 8),
                            };
                            jump.Destination = JumpDestination(jump.Detail);
                            jumps.Add(jump);
                        }
                }
            // Keep the passengers too when only the grid/pilot matched. Do not duplicate one jump per passenger.
            var grouped = new List<List<BattleJump>>();
            foreach (var grid in jumps.GroupBy(j => j.Grid))
            {
                List<BattleJump> current = null;
                foreach (var jump in grid.OrderBy(j => j.T))
                {
                    if (current == null || jump.T - current[0].T > 2000)
                    {
                        current = new List<BattleJump>();
                        grouped.Add(current);
                    }
                    current.Add(jump);
                }
            }
            var kept = grouped.Where(g => entities.Contains(g[0].Grid) ||
                (g.Any(j => identities.Contains(j.Actor)) && g.Any(j => Near(j.X, j.Y, j.Z) ||
                    (j.Destination != null && Near(j.Destination[0], j.Destination[1], j.Destination[2]))))).ToList();
            var names = Names(from, to, kept.SelectMany(g => g.SelectMany(j => new[] { j.Actor, j.Grid })).Where(id => id != 0).Distinct().ToList(), resolveLive: false);
            string Name(long id, string unknown) => id == 0 ? unknown : names.TryGetValue(id, out var n) ? n : id.ToString();
            return kept.Select(g =>
            {
                var main = g.FirstOrDefault(j => j.Kind == "jump") ?? g[0];
                var originNear = Near(main.X, main.Y, main.Z);
                var destinationNear = main.Destination != null && Near(main.Destination[0], main.Destination[1], main.Destination[2]);
                // Recorded detail retains the grid's name at this event, unlike names (the day's last snapshot).
                var separator = main.Detail.IndexOf(' ');
                var gridName = main.Detail.StartsWith("to=", StringComparison.Ordinal) && separator >= 0
                    ? main.Detail.Substring(separator + 1).Trim() : "";
                return new Dictionary<string, object>
                {
                    ["t"] = main.T, ["gridId"] = main.Grid.ToString(), ["grid"] = gridName.Length > 0 ? gridName : Name(main.Grid, "Неизвестный грид"),
                    ["direction"] = main.Destination == null ? "jump" : originNear && !destinationNear ? "departure" : !originNear && destinationNear ? "arrival" : "jump",
                    ["distance"] = R(main.Distance),
                    ["actors"] = g.Where(j => j.Actor != 0).GroupBy(j => j.Actor).Select(a => new
                    {
                        id = a.Key.ToString(), name = Name(a.Key, "Неизвестно"), role = a.Any(j => j.Kind == "jump") ? "pilot" : "passenger",
                    }).ToList(),
                };
            }).OrderBy(j => (long)j["t"]).ToList();
        }
    }
}

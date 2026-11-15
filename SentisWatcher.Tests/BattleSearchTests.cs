using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using SentisWatcher.Storage;
using Xunit;

namespace SentisWatcher.Tests
{
    public sealed class BattleSearchTests
    {
        private static readonly Type EventType = typeof(WebData).GetNestedType("BattleEvent", BindingFlags.NonPublic);
        private static readonly Type GroupType = typeof(WebData).GetNestedType("BattleGroup", BindingFlags.NonPublic);
        private static object Event(long t, double x, long actor = 1, long victim = 2, long source = 10, long target = 20, string kind = "hit")
        {
            var e = Activator.CreateInstance(EventType, true);
            foreach (var field in new[] { ("T", (object)t), ("RowId", t), ("X", x), ("Y", 0d), ("Z", 0d),
                ("Attacker", actor), ("Victim", victim), ("Source", source), ("Target", target),
                ("Kind", kind), ("AttackerKind", "player"), ("TargetKind", "block"), ("Relation", "enemy"), ("Amount", 1d) })
                EventType.GetField(field.Item1).SetValue(e, field.Item2);
            return e;
        }
        private static long L(object e, string field) => (long)EventType.GetField(field).GetValue(e);
        private static double X(object e) => (double)EventType.GetField("X").GetValue(e);
        private static IList Activity(object group) => (IList)GroupType.GetField("Activity").GetValue(group);

        [Fact]
        public void Time_window_search_matches_exhaustive_search_including_future_context_and_related_actors()
        {
            var random = new Random(7731);
            var search = typeof(WebData).GetMethod("NearActivity", BindingFlags.Static | BindingFlags.NonPublic);
            for (var sample = 0; sample < 60; sample++)
            {
                var group = Activator.CreateInstance(GroupType, true);
                for (var i = 0; i < 200; i++) Activity(group).Add(Event(i * 100L, random.Next(12) * 1000,
                    random.Next(4), random.Next(4), random.Next(4), random.Next(4)));
                for (var query = 0; query < 40; query++)
                {
                    var e = Event(random.Next(-1000, 22000), random.Next(12) * 1000,
                        random.Next(4), random.Next(4), random.Next(4), random.Next(4));
                    foreach (var related in new[] { false, true })
                    {
                        var expected = Activity(group).Cast<object>().Any(p => Math.Abs(L(p, "T") - L(e, "T")) <= 1200 &&
                            Math.Pow(X(p) - X(e), 2) <= 5000d * 5000 && (!related ||
                            L(e, "Attacker") != 0 && (L(e, "Attacker") == L(p, "Attacker") || L(e, "Attacker") == L(p, "Victim")) ||
                            L(e, "Victim") != 0 && (L(e, "Victim") == L(p, "Attacker") || L(e, "Victim") == L(p, "Victim")) ||
                            L(e, "Source") != 0 && L(e, "Source") == L(p, "Source") ||
                            L(e, "Target") != 0 && L(e, "Target") == L(p, "Target")));
                        Assert.Equal(expected, (bool)search.Invoke(null, new[] { group, e, (object)1200L, 5000d * 5000, related }));
                    }
                }
            }
        }

        [Fact]
        public void A_bridge_merging_interleaved_chains_keeps_activity_chronological_and_context_attached_once()
        {
            var rows = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(EventType));
            rows.Add(Event(0, 0)); rows.Add(Event(1, 8000)); rows.Add(Event(2, 0)); rows.Add(Event(3, 8000));
            rows.Add(Event(3, 8000, kind: "destroyed")); rows.Add(Event(4, 4000));
            var detect = typeof(WebData).GetMethod("DetectBattles", BindingFlags.Static | BindingFlags.NonPublic);
            var groups = ((IEnumerable)detect.Invoke(null, new object[] { rows, 2L, 5000d * 5000 })).Cast<object>().ToArray();
            var group = Assert.Single(groups);
            Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, Activity(group).Cast<object>().Select(e => L(e, "T")));
            Assert.Equal(6, ((IList)GroupType.GetField("Events").GetValue(group)).Count);
        }
    }
}

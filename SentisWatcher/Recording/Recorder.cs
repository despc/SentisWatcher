using System;
using System.Collections.Generic;
using NLog;
using SentisWatcher.Anomalies;
using SentisWatcher.Storage;
using VRageMath;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// What the hooks, the sampler and the sweep report goes through here into the store. <see cref="Current"/>
    /// is null while the plugin records nothing, and every hook checks it first.
    /// </summary>
    public sealed class Recorder
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly Logger AlertLog = LogManager.GetLogger("SentisWatcher.Alert");

        public static Recorder Current { get; set; }

        public readonly WatcherStore Store;
        public readonly Aggregator Aggregator = new Aggregator();
        private readonly AlertLimiter _alerts = new AlertLimiter();
        private readonly Dictionary<long, string> _namesToday = new Dictionary<long, string>();
        private DateTime _namesDay;

        public Recorder(WatcherStore store)
        {
            Store = store;
        }

        public void Event(string kind, long actor, long entity, Vector3D? at = null, double? amount = null, int? count = null, string detail = null)
        {
            var p = at ?? Vector3D.Zero;
            Store.Add(new Row(Table.Events, Clock.Now, Clock.Now, kind, actor, entity,
                at.HasValue ? (object)p.X : null, at.HasValue ? (object)p.Y : null, at.HasValue ? (object)p.Z : null,
                amount, count, detail));
        }

        /// <summary>Remembers who an id is, once a day per name (a new name is written again).</summary>
        public void Name(long id, string kind, string name, long owner = 0, ulong steam = 0)
        {
            if (id == 0) return;
            var today = DateTime.UtcNow.Date;
            var key = kind + "|" + name + "|" + owner;
            lock (_namesToday)
            {
                if (_namesDay != today)
                {
                    _namesToday.Clear();
                    _namesDay = today;
                }
                if (_namesToday.TryGetValue(id, out var known) && known == key) return;
                _namesToday[id] = key;
            }
            Store.Add(new Row(Table.Names, Clock.Now, id, kind, name, owner, steam == 0 ? null : (object)(long)steam, Clock.Now));
        }

        /// <summary>
        /// An anomaly, into the log (SentisWatcher.Alert) and the day file; one of a kind about one thing (the entity, or
        /// <paramref name="limitBy"/>) an hour. Any thread.
        /// </summary>
        public void Alert(string kind, long actor, long entity, string detail, long? limitBy = null)
        {
            if (!_alerts.Allow(kind, limitBy ?? entity, DateTime.UtcNow)) return;
            if (SentisWatcher.SentisWatcherPlugin.Config?.DiagnosticLogs == true) AlertLog.Warn("ALERT " + kind + " actor=" + actor + " entity=" + entity + ": " + detail);
            Store.Add(new Row(Table.Alerts, Clock.Now, Clock.Now, kind, actor, entity, detail));
        }

        /// <summary>Writes the aggregates of the window that ended (game thread, every frame).</summary>
        public void FlushAggregates(bool force = false)
        {
            var done = Aggregator.Take(Clock.Now, force);
            if (done == null) return;
            foreach (var a in done)
                Store.Add(new Row(Table.Events, a.First, a.First, a.Kind, a.Actor, a.Entity,
                    a.Position.X, a.Position.Y, a.Position.Z, a.Amount, a.Count, a.Detail));
        }

        /// <summary>
        /// The planets of the world and the direction to the sun, into the day file (game thread): the web
        /// view draws them to scale.
        /// </summary>
        public void World()
        {
            var now = Clock.Now;
            foreach (var entity in Sandbox.Game.Entities.MyEntities.GetEntities())
            {
                if (!(entity is Sandbox.Game.Entities.MyPlanet planet) || planet.MarkedForClose) continue;
                var p = planet.PositionComp.GetPosition();
                var gravity = (planet.Components.Get<Sandbox.Game.Entities.MyGravityProviderComponent>() as Sandbox.Game.Entities.MySphericalNaturalGravityComponent)?.GravityLimit ?? 0f;
                Store.Add(new Row(Table.Planets, now, planet.EntityId, planet.StorageName, planet.Generator?.Id.SubtypeName,
                    p.X, p.Y, p.Z, (double)planet.AverageRadius, (double)planet.MinimumRadius, (double)planet.MaximumRadius,
                    // where the air ends, as the game counts it (GetAirDensity), not the rendered shell (AtmosphereRadius)
                    planet.HasAtmosphere ? (double)(planet.AverageRadius + planet.AtmosphereAltitude) : 0.0, (double)gravity, now));
            }
            var sun = Sandbox.Game.World.MySector.DirectionToSunNormalized;
            Store.Add(new Row(Table.Meta, now, "sun", string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2}", sun.X, sun.Y, sun.Z)));
        }

        public static void Safe(string what, Action action)
        {
            try
            {
                if (Current != null) action();
            }
            catch (Exception e)
            {
                Log.Error(e, "SentisWatcher: " + what + " failed");
            }
        }
    }
}

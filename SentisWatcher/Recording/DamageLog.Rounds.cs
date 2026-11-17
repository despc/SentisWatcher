using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Common.ObjectBuilders;
using VRage.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRage.Utils;
using VRageMath;

namespace SentisWatcher.Recording
{
    // Independent of damage callbacks: one penetrating/explosive projectile must never count as many hits.
    internal sealed class CombatRoundCounter
    {
        private int _hit;
        internal bool TryHit() => Interlocked.CompareExchange(ref _hit, 1, 0) == 0;
    }

    internal static class CombatCredit
    {
        internal static long Select(long pilot, long builder) => pilot != 0 ? pilot : builder;
    }

    public static partial class DamageLog
    {
        public const string Round = "round", RoundHit = "round_hit";
        private sealed class FiredRound
        {
            private int _hit;
            internal long Actor, Grid, Launcher, Origin;
            internal string Kind, Weapon, Faction, Attribution;
            private HitName _hitName;

            /// <summary>The round's first hit of an enemy or a neutral; the others of a penetrating or exploding round are not.</summary>
            internal bool TryHit() => Interlocked.CompareExchange(ref _hit, 1, 0) == 0;

            private sealed class HitName
            {
                public MyStringHash Type;
                public string Text;
            }

            /// <summary>"Explosion:Launcher" for the damage its explosion does: made once, not for each of its blocks.</summary>
            internal string HitWeapon(MyStringHash type)
            {
                var known = _hitName;
                if (known != null && known.Type == type) return known.Text;
                var slash = Weapon?.IndexOf('/') ?? -1;
                var text = type.String + ":" + (Weapon == null ? "?" : slash < 0 ? Weapon : Weapon.Substring(0, slash));
                _hitName = new HitName { Type = type, Text = text };
                return text;
            }
        }

        /// <summary>
        /// The rounds of one list of projectiles, by their slot in it. The game has one (MyProjectiles.Static);
        /// SentisClusters gives each detached cluster its own, moved on the game thread at the cluster's hand-over.
        /// Each list is followed by its own events: a list's slots say nothing of another's.
        /// </summary>
        private sealed class Bullets
        {
            private readonly MyProjectiles _list;
            private readonly Dictionary<int, FiredRound> _rounds = new Dictionary<int, FiredRound>();
            private int _generation;

            internal Bullets(MyProjectiles list)
            {
                _list = list;
                _generation = _roundGeneration;
                list.OnProjectileAdded += Added;
                list.OnProjectileRemoving += Removing;
                list.AddOnHitInterceptor(int.MaxValue, Hit);
            }

            internal bool Of(MyProjectiles list) => ReferenceEquals(_list, list);

            // rounds of a recording that ended are forgotten at the first event of the next (under RoundLock)
            private void Current()
            {
                if (_generation == _roundGeneration) return;
                _rounds.Clear();
                _generation = _roundGeneration;
            }

            // inside MyProjectiles.AddInternal, on its thread, once the projectile has its slot: only a projectile really
            // added (with SentisClusters a cluster's thread does not add, the call is made on the game thread later)
            private void Added(ref MyProjectileInfo projectile, int index)
            {
                var weapon = _shotWeapon;
                var ammo = _shotAmmo;
                _shotWeapon = null;
                _shotAmmo = null;
                if (weapon == null || Recorder.Current == null || !ReferenceEquals(ammo, projectile.ProjectileAmmoDefinition)) return;
                try
                {
                    var round = Launch(weapon, WeaponName(BlockOf(weapon)) + "/" + ammo.Id.SubtypeName, projectile.Origin);
                    lock (RoundLock)
                    {
                        Current();
                        _rounds[index] = round;
                    }
                }
                catch (Exception e)
                {
                    Recorder.Failed("projectile launch", e);
                }
            }

            private void Removing(ref MyProjectileInfo projectile, int index)
            {
                lock (RoundLock)
                {
                    Current();
                    if (_rounds.Count == 0) return;
                    // SE removes by swapping the last struct into this slot. Never use a boxed struct as shot identity.
                    var last = _list.GetAllProjectileCount() - 1;
                    _rounds.Remove(index);
                    if (last != index && _rounds.TryGetValue(last, out var moved)) _rounds[index] = moved;
                    _rounds.Remove(last);
                }
            }

            private void Hit(ref MyProjectileInfo projectile, ref MyProjectileHitInfo hit)
            {
                if (Recorder.Current == null) return;
                FiredRound round;
                lock (RoundLock)
                {
                    Current();
                    if (!_rounds.TryGetValue(projectile.Index, out round)) return;
                }
                try
                {
                    Impact(round, hit.HitEntity as MyEntity);
                }
                catch (Exception e)
                {
                    Recorder.Failed("projectile impact", e);
                }
            }
        }

        private static ConditionalWeakTable<MyMissile, FiredRound> Missiles = new ConditionalWeakTable<MyMissile, FiredRound>();
        private static readonly ConditionalWeakTable<MyProjectiles, Bullets> BulletLists = new ConditionalWeakTable<MyProjectiles, Bullets>();
        private static readonly ConditionalWeakTable<MyProjectiles, Bullets>.CreateValueCallback NewBullets = list => new Bullets(list);
        private static volatile Bullets _lastBullets;
        private static int _roundGeneration;
        private static readonly object RoundLock = new object();
        // the shot MyProjectiles.AddInternal is making on this thread, for the list's OnProjectileAdded
        [ThreadStatic] private static MyEntity _shotWeapon;
        [ThreadStatic] private static MyProjectileAmmoDefinition _shotAmmo;
        private static bool _bulletHook, _missileHook;
        private static int RoundTracking => (_bulletHook ? 1 : 0) | (_missileHook ? 2 : 0);

        private static void PatchRounds(PatchContext context)
        {
            PatchGuard.Run("SentisWatcher.ProjectileAccuracy", context, c =>
            {
                var method = typeof(MyProjectiles).GetMethod("AddInternal", BindingFlags.Public | BindingFlags.Instance);
                if (method == null) throw new MissingMethodException("MyProjectiles.AddInternal");
                // a prefix: the shot's weapon for the list's own OnProjectileAdded, which AddInternal raises once the
                // projectile has its slot. A suffix would also run where SentisClusters puts the call off (a cluster's
                // thread, nothing added) and count the round twice, and would take its slot from a count that other
                // threads move
                c.GetPattern(method).Prefixes.Add(typeof(DamageLog).GetMethod(nameof(ProjectileAdding), BindingFlags.NonPublic | BindingFlags.Static));
                _bulletHook = true;
            });
            PatchExplosions(context);
            PatchGuard.Run("SentisWatcher.MissileAccuracy", context, c =>
            {
                var methods = new[] { "Init", "UpdateData", "OnContactStart" }.Select(name => typeof(MyMissile)
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Single(m => m.Name == name)).ToArray();
                foreach (var method in methods)
                    c.GetPattern(method).Suffixes.Add(typeof(DamageLog).GetMethod(method.Name == "OnContactStart" ? nameof(MissileContact) : nameof(MissileAdded), BindingFlags.NonPublic | BindingFlags.Static));
                _missileHook = true;
            });
        }

        private static FiredRound Launch(MyEntity source, string weapon, Vector3D origin)
        {
            var round = Credit(source, weapon, Identities.PilotFreshMs);
            Add(Round, round.Actor, round.Kind, round.Grid, weapon, 0, "", 0, "", 1, origin, round.Attribution, true, round.Faction);
            return round;
        }

        /// <summary>Who the shot or the blast of the entity (a block's part: the block) is, as a round's.</summary>
        private static FiredRound Credit(MyEntity source, string weapon, long freshMs)
        {
            source = BlockOf(source);
            var actor = Identities.DamageIdentity(source, out var attribution, freshMs);
            return new FiredRound
            {
                Actor = actor, Launcher = source?.EntityId ?? 0, Grid = source is MyCubeBlock block ? block.CubeGrid.EntityId : source?.EntityId ?? 0,
                Kind = KindOf(actor, source), Weapon = weapon, Faction = FactionAt(actor), Attribution = attribution,
            };
        }

        private static void ProjectileAdding(MyProjectiles __instance, MyEntity weapon, MyProjectileAmmoDefinition ammoDefinition)
        {
            _shotWeapon = null;
            _shotAmmo = null;
            // no weapon: explosion shrapnel/environmental projectiles are not player-fired rounds
            if (weapon == null || ammoDefinition == null || Recorder.Current == null) return;
            try
            {
                var bullets = _lastBullets;
                if (bullets == null || !bullets.Of(__instance)) _lastBullets = BulletLists.GetValue(__instance, NewBullets);
                _shotWeapon = weapon;
                _shotAmmo = ammoDefinition;
            }
            catch (Exception e)
            {
                Recorder.Failed("projectile launch", e);
            }
        }

        // MyMissile.Init and UpdateData: a missile taken from the pool or made new. A new one is made on a background
        // thread (MyEntities.CreateFromObjectBuilderParallel), inside the entity creation block, where MyEntities does not
        // find the launcher by its id: the launch is told on the game thread then, the next frame (the missile still flies)
        private static void MissileAdded(MyMissile __instance, MyObjectBuilder_EntityBase objectBuilder)
        {
            if (Recorder.Current == null) return;
            try
            {
                var launcherId = __instance.LauncherId;
                var origin = (objectBuilder as MyObjectBuilder_Missile)?.OriginEntity ?? 0;
                var at = __instance.PositionComp.GetPosition();
                if (MySandboxGame.Static?.UpdateThread == Thread.CurrentThread)
                {
                    MissileLaunched(__instance, launcherId, origin, at);
                    return;
                }
                var recorder = Recorder.Current;
                MySandboxGame.Static?.Invoke(() =>
                {
                    if (Recorder.Current == recorder) Recorder.Safe("missile launch", () => MissileLaunched(__instance, launcherId, origin, at));
                }, "SentisWatcher missile launch");
            }
            catch (Exception e)
            {
                Recorder.Failed("missile launch", e);
            }
        }

        private static void MissileLaunched(MyMissile missile, long launcherId, long origin, Vector3D at)
        {
            MyEntities.TryGetEntityById(launcherId, out var launcher);
            var round = Launch(launcher, WeaponName(BlockOf(launcher)) + "/missile", at);
            round.Origin = origin;
            lock (RoundLock) { Missiles.Remove(missile); Missiles.Add(missile, round); }
        }

        private static void MissileContact(MyMissile __instance)
        {
            FiredRound round;
            lock (RoundLock) { if (!Missiles.TryGetValue(__instance, out round)) return; }
            var target = __instance.CollidedEntity;
            // Havok contacts may run off-thread. Resolve faction/ownership on the game thread, not the physics worker.
            var recorder = Recorder.Current;
            MySandboxGame.Static?.Invoke(() =>
            {
                if (recorder != null && Recorder.Current == recorder) Recorder.Safe("missile impact", () => Impact(round, target));
            }, "SentisWatcher missile impact");
        }

        internal static void ResetRounds()
        {
            lock (RoundLock)
            {
                // the lists keep their events: their rounds are forgotten at their next (Bullets.Current)
                _roundGeneration++;
                Missiles = new ConditionalWeakTable<MyMissile, FiredRound>();
                ResetExplosions();
            }
        }

        private static void Impact(FiredRound round, MyEntity target)
        {
            target = BlockOf(target);
            var grid = (target as MyCubeBlock)?.CubeGrid ?? target as MyCubeGrid;
            if (grid == null && !(target is MyCharacter)) return; // voxels, floating items and empty space are misses
            if (target is MyCharacter animal && Wildlife.IsAnimal(animal)) return;
            var victim = target is MyCubeBlock block ? block.OwnerId : grid != null ? Identities.Owner(grid) : Identities.OfEntity(target);
            var relation = Relation(round.Actor, round.Kind, victim, false);
            if ((relation != "enemy" && relation != "neutral") || !round.TryHit()) return;
            Add(RoundHit, round.Actor, round.Kind, round.Grid, round.Weapon, grid?.EntityId ?? target.EntityId,
                grid != null ? "block" : "character", victim, relation, 1, target.PositionComp.GetPosition(), round.Attribution, true, round.Faction);
            if (grid != null) DamageBlockedWatch.Hit(grid, round.Actor, round.Weapon, target.PositionComp.GetPosition());
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using SentisWatcher.Storage;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisWatcher.Recording
{
    public static partial class DamageLog
    {
        private sealed class PendingExplosion
        {
            internal long T;
            internal Vector3D Center;
            internal FiredRound Round;
            // matched by the game's explosion already: kept a while for SGI's (ExplosionTweaks), which does the damage
            // itself later, from the same sphere and origin
            internal bool Taken;
        }
        private static readonly List<PendingExplosion> PendingExplosions = new List<PendingExplosion>();
        private static ConditionalWeakTable<object, FiredRound> Explosions = new ConditionalWeakTable<object, FiredRound>();
        private static FieldInfo _explosionInfoField;
        [ThreadStatic] private static Stack<FiredRound> _explosionScopes;
        private static FiredRound ExplosionCredit => _explosionScopes != null && _explosionScopes.Count > 0 ? _explosionScopes.Peek() : null;

        private static void PatchExplosions(PatchContext context) => PatchGuard.Run("SentisWatcher.ExplosionCredit", context, c =>
        {
            var missile = typeof(MyMissile).GetMethod("ExecuteExplosion", BindingFlags.NonPublic | BindingFlags.Instance);
            var type = typeof(MyMissile).Assembly.GetType("Sandbox.Game.MyExplosion", throwOnError: true);
            var root = type.GetMethod("ApplyVolumetricExplosion", BindingFlags.NonPublic | BindingFlags.Instance);
            var grid = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Single(m => m.Name == "ApplyVolumetricDamageToGrid");
            var entities = type.GetMethod("ApplyVolumetricExplosionOnEntities", BindingFlags.NonPublic | BindingFlags.Instance);
            _explosionInfoField = type.GetField("m_explosionInfo", BindingFlags.NonPublic | BindingFlags.Instance);
            if (missile == null || root == null || grid == null || entities == null || _explosionInfoField == null) throw new MissingMethodException("Missile/explosion attribution lifecycle");
            c.GetPattern(missile).Prefixes.Add(typeof(DamageLog).GetMethod(nameof(MissileExploding), BindingFlags.NonPublic | BindingFlags.Static));
            // Start and the public grid wrapper are small/inlinable. Scope the actual non-inlinable damage bodies.
            c.GetPattern(root).Prefixes.Add(typeof(DamageLog).GetMethod(nameof(ExplosionRootEntering), BindingFlags.NonPublic | BindingFlags.Static));
            c.GetPattern(root).Suffixes.Add(typeof(DamageLog).GetMethod(nameof(ExplosionLeaving), BindingFlags.NonPublic | BindingFlags.Static));
            foreach (var method in new[] { grid, entities })
            {
                c.GetPattern(method).Prefixes.Add(typeof(DamageLog).GetMethod(nameof(ExplosionEntering), BindingFlags.NonPublic | BindingFlags.Static));
                c.GetPattern(method).Suffixes.Add(typeof(DamageLog).GetMethod(nameof(ExplosionLeaving), BindingFlags.NonPublic | BindingFlags.Static));
            }
            // the blocks an explosion takes away whole, without damage (ApplyVolumetricDamageToGrid: those inside its
            // sphere, and armour weaker than its damage there) - a warhead's blast is mostly these, never told to the
            // damage system nor applied as accumulated damage
            var remove = typeof(MyCubeGrid).GetMethod("RemoveDestroyedBlock", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(MySlimBlock), typeof(long) }, null) ?? throw new MissingMethodException("MyCubeGrid.RemoveDestroyedBlock(MySlimBlock, long)");
            c.GetPattern(remove).Prefixes.Add(typeof(DamageLog).GetMethod(nameof(BlockBlownAway), BindingFlags.NonPublic | BindingFlags.Static));
        });

        private static void MissileExploding(MyMissile __instance) => Recorder.Safe("missile explosion credit", () =>
        {
            lock (RoundLock)
            {
                if (!Missiles.TryGetValue(__instance, out var round)) return;
                var now = Clock.Now;
                PendingExplosions.RemoveAll(p => now - p.T > 5000);
                if (PendingExplosions.Count >= 4096) PendingExplosions.RemoveAt(0);
                PendingExplosions.Add(new PendingExplosion { T = now, Center = __instance.PositionComp.GetPosition(), Round = round });
            }
        });

        private static void ExplosionStarted(object __instance, MyExplosionInfo explosionInfo) => Recorder.Safe("explosion credit", () =>
        {
            lock (RoundLock)
            {
                Explosions.Remove(__instance); // MyExplosion objects are pooled too
                var now = Clock.Now;
                PendingExplosions.RemoveAll(p => now - p.T > 5000);
                var matches = PendingExplosions.Where(p => !p.Taken && Matches(p, explosionInfo.ExplosionSphere.Center, explosionInfo.OriginEntity)).ToList();
                if (matches.Count > 1)
                {
                    // Identical queued explosions cannot identify a launcher reliably. Preserve one unknown marker per
                    // queued explosion so the NEXT explosion cannot accidentally inherit an arbitrary remaining owner.
                    var unknown = new FiredRound { Actor = 0, Kind = "none", Attribution = "unknown",
                        Grid = matches.All(m => m.Round.Grid == matches[0].Round.Grid) ? matches[0].Round.Grid : 0 };
                    foreach (var match in matches) match.Round = unknown;
                }
                if (matches.Count > 0)
                {
                    matches[0].Taken = true;
                    Explosions.Add(__instance, matches[0].Round);
                    return;
                }
                // The engine often reports a missile's OWNER CHARACTER, losing the launcher. Never credit that owner
                // instead of a pilot/builder. If exact launch correlation is ambiguous, only a known grid pilot is safe.
                // a warhead names no origin, only itself as the owner: its own grid then
                MyEntities.TryGetEntityById(explosionInfo.OriginEntity, out var origin);
                var block = BlockOf(explosionInfo.OwnerEntity) as MyCubeBlock;
                var grid = origin as MyCubeGrid ?? (origin as MyCubeBlock)?.CubeGrid ?? (origin == null ? block?.CubeGrid : null);
                if (grid == null) return;
                var source = block != null && block.CubeGrid == grid ? block : origin as MyCubeBlock;
                var actor = Identities.DamageIdentity(source ?? (VRage.Game.Entity.MyEntity)grid, out var attribution, Identities.PilotOfHitMs);
                Explosions.Add(__instance, new FiredRound
                {
                    Actor = actor, Grid = grid.EntityId, Attribution = attribution, Kind = KindOf(actor, grid), Faction = FactionAt(actor),
                    Weapon = source != null ? WeaponName(source) : null,
                });
            }
        });

        private static bool Matches(PendingExplosion p, Vector3D center, long origin) =>
            p.Center == center && (p.Round.Origin == origin || p.Round.Grid == origin || p.Round.Launcher == origin);

        private static void ExplosionRootEntering(object __instance)
        {
            ExplosionStarted(__instance, (MyExplosionInfo)_explosionInfoField.GetValue(__instance));
            ExplosionEntering(__instance);
        }

        private static void ExplosionEntering(object __instance)
        {
            FiredRound round;
            lock (RoundLock) { Explosions.TryGetValue(__instance, out round); }
            if (_explosionScopes == null) _explosionScopes = new Stack<FiredRound>();
            _explosionScopes.Push(round);
        }

        private static void ExplosionLeaving()
        {
            if (_explosionScopes != null && _explosionScopes.Count > 0) _explosionScopes.Pop();
        }

        private static void BlockBlownAway(MySlimBlock block)
        {
            // inside an explosion's damage only (other callers take away blocks already destroyed and told); a block whose
            // damage destroyed it is told by its damage already
            if (_explosionScopes == null || _explosionScopes.Count == 0 || Recorder.Current == null) return;
            if (block?.CubeGrid == null || block.IsDestroyed || block.Integrity <= 0 || block.ComponentStack == null || block.ComponentStack.IsDestroyed) return;
            try
            {
                BlownAway(block, _explosionScopes.Peek());
            }
            catch (Exception e)
            {
                Recorder.Failed("explosion's block taken away", e);
            }
        }

        private static void BlownAway(MySlimBlock block, FiredRound explosion)
        {
            var grid = block.CubeGrid;
            var attacker = explosion?.Actor ?? 0;
            var attribution = explosion?.Attribution ?? "unknown";
            var attackerKind = explosion?.Kind ?? KindOf(attacker, null);
            var attackerEntity = explosion?.Grid ?? 0;
            var weapon = explosion != null ? explosion.HitWeapon(VRage.Game.MyDamageType.Explosion) : "Explosion:?";
            var victim = block.OwnerId != 0 ? block.OwnerId : Identities.Owner(grid);
            var at = grid.GridIntegerToWorld(block.Position);
            var relation = Relation(attacker, attackerKind, victim, false);
            // what was left of it is its damage, as a hit that destroys it
            var key = Add(Hit, attacker, attackerKind, attackerEntity, weapon, grid.EntityId, "block", victim, relation, block.Integrity, at, attribution);
            Add(Destroyed, attacker, attackerKind, attackerEntity, weapon, grid.EntityId, "block", victim, relation, 1, at, attribution);
            if (EconomyTracking == 0) return;
            var hit = new DamageContext { Key = key, Completed = 1 };
            try
            {
                Lost(block, Capture(block, hit), gone: true);
            }
            catch (Exception e)
            {
                EconomyFailed("component loss of a block taken away", e);
            }
        }

        // ------------------------------------------------------------------ SentisGameplayImprovements' explosions

        // SGI's ExplosionTweaks replaces the game's explosion (its ApplyVolumetricExplosion prefix returns false) and does
        // the damage later, in MissilePatch.Finish from its queue: the game's methods scoped above do not run then. A
        // warhead's blast names its owner's identity as the attacker, not an entity: who set it off is taken as it goes
        // off (MissilePatch.ExplodeNow), by the warhead's entity, which the blast keeps as its Origin
        private sealed class WarheadCredit
        {
            public FiredRound Round;
            public long At;
        }
        private static readonly Dictionary<long, WarheadCredit> WarheadCredits = new Dictionary<long, WarheadCredit>();
        private static FieldInfo _blastOrigin, _blastAttacker, _blastSphere;
        private static bool _sgiPatched;

        /// <summary>SGI's explosions scoped as the game's are, if SGI is here. Once, as the session starts loading.</summary>
        internal static void PatchGameplayImprovements()
        {
            if (_sgiPatched) return;
            _sgiPatched = true;
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisGameplayImprovements.MissilePatch", false)).FirstOrDefault(t => t != null);
            if (type == null) return;
            try
            {
                const BindingFlags any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                var explodeNow = type.GetMethod("ExplodeNow", any) ?? throw new MissingMethodException("MissilePatch.ExplodeNow");
                var finish = type.GetMethod("Finish", any) ?? throw new MissingMethodException("MissilePatch.Finish");
                var blast = finish.GetParameters().FirstOrDefault(p => p.Name == "blast")?.ParameterType ?? throw new MissingMethodException("MissilePatch.Finish(blast, ...)");
                _blastOrigin = blast.GetField("Origin") ?? throw new MissingFieldException("Blast.Origin");
                _blastAttacker = blast.GetField("Attacker") ?? throw new MissingFieldException("Blast.Attacker");
                _blastSphere = blast.GetField("Sphere") ?? throw new MissingFieldException("Blast.Sphere");
                var patches = (PatchManager)Torch.TorchBase.Instance.Managers.GetManager(typeof(PatchManager));
                var context = patches.AcquireContext();
                PatchGuard.Run("SentisWatcher.GameplayImprovementsExplosions", context, c =>
                {
                    c.GetPattern(explodeNow).Prefixes.Add(typeof(DamageLog).GetMethod(nameof(SgiWarheadGoesOff), BindingFlags.NonPublic | BindingFlags.Static));
                    c.GetPattern(finish).Prefixes.Add(typeof(DamageLog).GetMethod(nameof(SgiBlastEntering), BindingFlags.NonPublic | BindingFlags.Static));
                    c.GetPattern(finish).Suffixes.Add(typeof(DamageLog).GetMethod(nameof(ExplosionLeaving), BindingFlags.NonPublic | BindingFlags.Static));
                });
                patches.Commit();
            }
            catch (Exception e)
            {
                Recorder.Failed("the scope of SentisGameplayImprovements' explosions", e);
            }
        }

        private static void SgiWarheadGoesOff(MyWarhead warhead)
        {
            if (Recorder.Current == null || warhead == null) return;
            try
            {
                var round = Credit(warhead, WeaponName(warhead), Identities.PilotOfHitMs);
                var now = Clock.Now;
                lock (RoundLock)
                {
                    if (WarheadCredits.Count > 256)
                        foreach (var gone in WarheadCredits.Where(p => now - p.Value.At > 60000).Select(p => p.Key).ToList()) WarheadCredits.Remove(gone);
                    WarheadCredits[warhead.EntityId] = new WarheadCredit { Round = round, At = now };
                }
            }
            catch (Exception e)
            {
                Recorder.Failed("warhead credit", e);
            }
        }

        private static void SgiBlastEntering(object blast)
        {
            FiredRound round = null;
            if (Recorder.Current != null && blast != null)
                try
                {
                    var origin = (long)_blastOrigin.GetValue(blast);
                    var attacker = (long)_blastAttacker.GetValue(blast);
                    var center = ((BoundingSphereD)_blastSphere.GetValue(blast)).Center;
                    lock (RoundLock)
                        if (WarheadCredits.TryGetValue(origin, out var warhead))
                        {
                            round = warhead.Round;
                            WarheadCredits.Remove(origin);
                        }
                        else
                        {
                            // a missile's: its launch, by the sphere and origin of the game's explosion it came as (the
                            // explosion's owner is the shooter's character, not the launcher)
                            var now = Clock.Now;
                            PendingExplosions.RemoveAll(p => now - p.T > 5000);
                            var missile = PendingExplosions.FirstOrDefault(p => p.Taken && Matches(p, center, origin)) ??
                                          PendingExplosions.FirstOrDefault(p => Matches(p, center, origin));
                            if (missile != null)
                            {
                                round = missile.Round;
                                PendingExplosions.Remove(missile);
                            }
                        }
                    // another explosion of the game through SGI (ammunition, explosives): the entity that owned it
                    if (round == null && attacker != 0 && MyEntities.TryGetEntityById(attacker, out var owner))
                    {
                        var source = BlockOf(owner);
                        // SE names a launcher owner's character: without the launch that is not proof of firing
                        round = source is Sandbox.Game.Entities.Character.MyCharacter
                            ? new FiredRound { Kind = "none", Attribution = "unknown" }
                            : Credit(source, WeaponName(source), Identities.PilotOfHitMs);
                    }
                }
                catch (Exception e)
                {
                    Recorder.Failed("explosion credit (SentisGameplayImprovements)", e);
                }
            // pushed whatever, for the suffix's pop
            if (_explosionScopes == null) _explosionScopes = new Stack<FiredRound>();
            _explosionScopes.Push(round);
        }

        private static void ResetExplosions()
        {
            WarheadCredits.Clear();
            PendingExplosions.Clear();
            Explosions = new ConditionalWeakTable<object, FiredRound>();
            _explosionScopes?.Clear();
        }
    }
}

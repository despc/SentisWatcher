using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SentisWatcher.Storage;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// Who fights whom (table damage): the hits, the grinding, the blocks destroyed and the characters killed, from the
    /// game's damage system, and the shots of every gun (hand rifles, turrets, ship guns, missiles) from MyGunBase.Shoot -
    /// each with the attacker's kind (a player, a bot, an animal, an NPC), its weapon, the target and its owner, and how
    /// they stand to each other (enemies, neutral, their own). Summed over <see cref="WindowMs"/> per attacker, weapon
    /// and target: a turret's burst is one row, not hundreds.
    /// </summary>
    [PatchShim]
    public static partial class DamageLog
    {
        public const long WindowMs = 2000;

        public const string Hit = "hit", Grind = "grind", Shot = "shot", Destroyed = "destroyed", Kill = "kill";

        private sealed class Aggregate
        {
            public string Kind, AttackerKind, Weapon, TargetKind, Relation, AttackerFaction, VictimFaction, Attribution, DamageKey, VictimKind;
            public int Tracking, EconomyTracking;
            public long Attacker, AttackerEntity, Target, Victim;
            public double Amount;
            public int Count;
            public long Last;
            public Vector3D Position;
        }

        // the bodies counted as killed (a dying one takes more hits); forgotten now and then
        private static readonly HashSet<long> Dead = new HashSet<long>();

        private static bool FirstDeath(long body)
        {
            lock (Dead)
            {
                if (Dead.Count > 10000) Dead.Clear();
                return Dead.Add(body);
            }
        }

        private static readonly Dictionary<(string, long, long, string, long, long, string, string, string, int, int), Aggregate> Open =
            new Dictionary<(string, long, long, string, long, long, string, string, string, int, int), Aggregate>();
        private static readonly object Lock = new object();
        private static long _windowStart = -1;

        public static void Patch(PatchContext ctx)
        {
            PatchGuard.Run("SentisWatcher.Shots", ctx, c =>
            {
                // the last of MyGunBase's Shoot overloads: every shot of every gun goes through it (the others call it)
                var shoot = typeof(MyGunBase).GetMethod("Shoot", BindingFlags.Instance | BindingFlags.Public, null,
                                new[] { typeof(Vector3D), typeof(Vector3), typeof(Vector3), typeof(MyEntity), typeof(uint?), typeof(bool) }, null)
                            ?? throw new MissingMethodException("MyGunBase.Shoot(Vector3D, Vector3, Vector3, MyEntity, uint?, bool)");
                c.GetPattern(shoot).Suffixes.Add(typeof(DamageLog).GetMethod(nameof(ShootSuffix), BindingFlags.Static | BindingFlags.NonPublic));
            });
            PatchRounds(ctx);
            PatchEconomy(ctx);
        }

        // ------------------------------------------------------------------ what happens

        // every shot of every gun, on the thread of the gun's update (a cluster's with SentisClusters): no lambda for
        // Recorder.Safe, it would be an allocation a shot
        private static void ShootSuffix(MyGunBase __instance, Vector3D initialPosition, MyEntity owner)
        {
            if (Recorder.Current == null) return;
            try
            {
                Fired(__instance, initialPosition, owner);
            }
            catch (Exception e)
            {
                Recorder.Failed("shot", e);
            }
        }

        private static void Fired(MyGunBase __instance, Vector3D initialPosition, MyEntity owner)
        {
            var user = __instance.User;
            if (user == null) return;
            var weapon = BlockOf(user.Weapon);
            // a rocket launcher names its grid as the weapon: the block is the gun's user itself
            if (!(weapon is MyCubeBlock) && !(weapon is MyCharacter) && user is MyCubeBlock self) weapon = self;
            var shooter = BlockOf(owner ?? user.Owner);
            var creditSource = weapon is MyCubeBlock || weapon is MyCharacter ? weapon : shooter ?? weapon;
            var attacker = Identities.DamageIdentity(creditSource, out var attribution);
            var name = WeaponName(weapon) + "/" + (__instance.CurrentAmmoDefinition?.Id.SubtypeName ?? "?");
            var entity = shooter is MyCubeBlock block ? block.CubeGrid.EntityId : shooter?.EntityId ?? weapon?.EntityId ?? 0;
            Add(Shot, attacker, KindOf(attacker, shooter), entity, name, 0, "", 0, "", 1, initialPosition, attribution);
        }

        /// <summary>
        /// Damage that is not recorded at all: the cold and the heat on an animal. Wolves and spiders freeze by the
        /// hundred where no one is, and it says nothing about who fights whom - the most frequent rows of a day
        /// ("Temperature by 0 to character", 214 of them on 08.10.2026).
        /// </summary>
        public static bool Skipped(string type, bool animalTarget) => animalTarget && type == "Temperature";

        /// <summary>A hit from the damage system (after it was applied): to a block or a character.</summary>
        public static void Damage(object target, MyDamageInformation info)
        {
            if (Recorder.Current == null) return;
            try
            {
                DamageApplied(target, info);
            }
            catch (Exception e)
            {
                Recorder.Failed("damage log", e);
            }
        }

        private static void DamageApplied(object target, MyDamageInformation info)
        {
            var economyContext = TakeEconomyContext(target);
            MyEntities.TryGetEntityById(info.AttackerId, out var source);
            source = BlockOf(source);
            var explosion = ExplosionCredit;
            long attacker;
            string attribution;
            // an explosion's blocks (hundreds in one call) take the credit of its launch, not each the shooter's grid again
            if (explosion != null) { attacker = explosion.Actor; attribution = explosion.Attribution; }
            else if (info.Type == MyDamageType.Explosion && source is MyCharacter)
            {
                // SE can substitute a launcher owner's character. Without a launch context that is not proof of firing.
                attacker = 0; attribution = "unknown";
            }
            else attacker = Identities.DamageIdentity(source, out attribution, Identities.PilotOfHitMs);
            var attackerKind = explosion?.Kind ?? KindOf(attacker, source);
            var attackerEntity = explosion?.Grid ?? (source is MyCubeBlock block ? block.CubeGrid.EntityId : source?.EntityId ?? 0);
            var weapon = explosion != null ? explosion.HitWeapon(info.Type) : info.Type.String + ":" +
                (source is MyCharacter c && Wildlife.IsAnimal(c) ? c.Definition?.Id.SubtypeName : WeaponName(source));
            switch (target)
            {
                case IMySlimBlock slim:
                {
                    var grid = (MyCubeGrid)slim.CubeGrid;
                    var victim = slim.OwnerId != 0 ? slim.OwnerId : Identities.Owner(grid);
                    var at = grid.GridIntegerToWorld(slim.Position);
                    var relation = Relation(attacker, attackerKind, victim, false);
                    var damageKey = Add(info.Type == MyDamageType.Grind ? Grind : Hit, attacker, attackerKind, attackerEntity, weapon, grid.EntityId, "block", victim, relation, info.Amount, at, attribution,
                        economyCoverage: economyContext != null ? EconomyTracking : 0);
                    CompleteEconomyContext(economyContext, damageKey, info.Amount <= 0);
                    if (slim.IsDestroyed || slim.Integrity <= 0)
                        Add(Destroyed, attacker, attackerKind, attackerEntity, weapon, grid.EntityId, "block", victim, relation, 1, at, attribution);
                    break;
                }
                case MyCharacter character:
                {
                    var animal = Wildlife.IsAnimal(character);
                    var victim = character.GetPlayerIdentityId();
                    var at = character.PositionComp.GetPosition();
                    var targetKind = animal ? "animal" : "character";
                    var relation = Relation(attacker, attackerKind, victim, animal);
                    Add(Hit, attacker, attackerKind, attackerEntity, weapon, character.EntityId, targetKind, victim, relation, info.Amount, at, attribution);
                    if ((character.IsDead || (character.StatComp?.Health?.Value ?? 1) <= 0) && FirstDeath(character.EntityId))
                        Add(Kill, attacker, attackerKind, attackerEntity, weapon, character.EntityId, targetKind, victim, relation, 1, at, attribution);
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ who and what

        /// <summary>Who the attacker is: an animal, a bot (no real player behind it), an NPC (pirates, the game's own), a player, or none.</summary>
        public static string KindOf(long identity, MyEntity entity)
        {
            if (entity is MyCharacter character && Wildlife.IsAnimal(character)) return "animal";
            if (identity == 0) return "none";
            var players = MySession.Static?.Players;
            if (players == null) return "none";
            if (players.TryGetPlayerId(identity, out var id) && players.TryGetPlayerById(id, out var player) && player != null)
            {
                if (Wildlife.IsAnimal(player)) return "animal";
                // SentisAi's bots and test clients are "real" to the game: their Steam ids are not people's (those begin 0x0110)
                return player.IsRealPlayer && player.Id.SteamId >> 56 == 0x01 ? "player" : "bot";
            }
            return players.IdentityIsNpc(identity) ? "npc" : "player";
        }

        /// <summary>How the attacker stands to the target's owner: enemy, neutral, ally, own, or nobody's.</summary>
        private static string Relation(long attacker, string attackerKind, long victim, bool victimAnimal)
        {
            if (victimAnimal || attackerKind == "animal") return "enemy";
            if (victim == 0) return "nobody";
            if (attacker == 0) return "unknown";
            if (attacker == victim) return "own";
            switch (MyIDModule.GetRelationPlayerPlayer(victim, attacker))
            {
                case MyRelationsBetweenPlayers.Self: return "own";
                case MyRelationsBetweenPlayers.Allies: return "ally";
                case MyRelationsBetweenPlayers.Neutral: return "neutral";
                default: return "enemy";
            }
        }

        /// <summary>A turret's barrel or base is a subpart: the block it belongs to, else the entity itself.</summary>
        private static MyEntity BlockOf(MyEntity entity)
        {
            for (var e = entity; e != null; e = e.Parent)
                if (e is MyCubeBlock || e is MyCharacter) return e;
            return entity;
        }

        private static string WeaponName(MyEntity entity)
        {
            switch (entity)
            {
                case null: return "?";
                case MyCubeBlock block: return string.IsNullOrEmpty(block.BlockDefinition.Id.SubtypeName)
                    ? block.BlockDefinition.Id.TypeId.ToString().Replace("MyObjectBuilder_", "") : block.BlockDefinition.Id.SubtypeName;
                case MyCharacter character: return character.Definition?.Id.SubtypeName ?? "character";
                case MyCubeGrid _: return "grid";
                default: return entity.DefinitionId?.SubtypeName ?? entity.GetType().Name;
            }
        }

        // null: unavailable; empty: explicitly no faction. Never resolve historical factions from the live world.
        private static string FactionAt(long identity)
        {
            var factions = MySession.Static?.Factions;
            if (factions == null) return null;
            var faction = factions.TryGetPlayerFaction(identity);
            return faction == null ? "" : faction.FactionId + "|[" + faction.Tag + "] " + faction.Name;
        }

        // ------------------------------------------------------------------ the rows

        private static string Add(string kind, long attacker, string attackerKind, long attackerEntity, string weapon, long target, string targetKind,
                                long victim, string relation, double amount, Vector3D at, string attribution = null,
                                bool factionSnapshot = false, string recordedFaction = null, int? economyCoverage = null)
        {
            var now = Clock.Now;
            var af = factionSnapshot ? recordedFaction : FactionAt(attacker);
            var vf = FactionAt(victim);
            // whose the target is: a player's, an NPC's, a bot's (the published battles are only those between players)
            var vk = targetKind == "animal" ? "animal" : KindOf(victim, null);
            var tracking = RoundTracking;
            var coverage = economyCoverage ?? EconomyTracking;
            lock (Lock)
            {
                if (_windowStart < 0) _windowStart = now;
                var key = (kind, attacker, attackerEntity, weapon, target, victim, af, vf, attribution, tracking, coverage);
                if (!Open.TryGetValue(key, out var a))
                    Open[key] = a = new Aggregate
                    {
                        Kind = kind, Attacker = attacker, AttackerKind = attackerKind, AttackerEntity = attackerEntity, Weapon = weapon,
                        Target = target, TargetKind = targetKind, Victim = victim, Relation = relation,
                        AttackerFaction = af, VictimFaction = vf, Attribution = attribution, Tracking = tracking, EconomyTracking = coverage, DamageKey = kind == Hit && targetKind == "block" ? Guid.NewGuid().ToString("N") : null,
                        VictimKind = vk,
                    };
                a.Amount += amount;
                a.Count++;
                a.Last = now;
                a.Position = at;
                return a.DamageKey;
            }
        }

        /// <summary>The rows of the window that ended (all of them when forced); game thread, every frame.</summary>
        public static void Flush(Recorder recorder, bool force = false)
        {
            Identities.PruneCrews();
            List<Aggregate> done;
            lock (Lock)
            {
                if (Open.Count == 0 || (!force && Clock.Now - _windowStart < WindowMs)) return;
                done = new List<Aggregate>(Open.Values);
                Open.Clear();
                _windowStart = -1;
            }
            foreach (var a in done)
                recorder.Store.Add(new Row(Table.Damage, a.Last, a.Last, a.Kind, a.Attacker, a.AttackerKind, a.AttackerEntity, a.Weapon,
                    a.Target, a.TargetKind, a.Victim, a.Relation, a.Amount, a.Count, a.Position.X, a.Position.Y, a.Position.Z, a.AttackerFaction, a.VictimFaction, a.Attribution, a.Tracking, Math.Min(a.EconomyTracking, EconomyTracking), a.DamageKey, a.VictimKind));
        }
    }
}

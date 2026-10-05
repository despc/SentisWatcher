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
    public static class DamageLog
    {
        public const long WindowMs = 2000;

        public const string Hit = "hit", Grind = "grind", Shot = "shot", Destroyed = "destroyed", Kill = "kill";

        private sealed class Aggregate
        {
            public string Kind, AttackerKind, Weapon, TargetKind, Relation;
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

        private static readonly Dictionary<(string, long, long, string, long, long), Aggregate> Open = new Dictionary<(string, long, long, string, long, long), Aggregate>();
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
        }

        // ------------------------------------------------------------------ what happens

        private static void ShootSuffix(MyGunBase __instance, Vector3D initialPosition, MyEntity owner) => Recorder.Safe("shot", () =>
        {
            var user = __instance.User;
            if (user == null) return;
            var weapon = BlockOf(user.Weapon);
            var shooter = BlockOf(owner ?? user.Owner);
            var attacker = shooter is MyCharacter character ? character.GetPlayerIdentityId() : user.OwnerId != 0 ? user.OwnerId : Identities.OfEntity(weapon);
            var name = WeaponName(weapon) + "/" + (__instance.CurrentAmmoDefinition?.Id.SubtypeName ?? "?");
            var entity = shooter is MyCubeBlock block ? block.CubeGrid.EntityId : shooter?.EntityId ?? weapon?.EntityId ?? 0;
            Add(Shot, attacker, KindOf(attacker, shooter), entity, name, 0, "", 0, "", 1, initialPosition);
        });

        /// <summary>A hit from the damage system (after it was applied): to a block or a character.</summary>
        public static void Damage(object target, MyDamageInformation info) => Recorder.Safe("damage log", () =>
        {
            MyEntities.TryGetEntityById(info.AttackerId, out var source);
            source = BlockOf(source);
            var attacker = Identities.OfEntity(source);
            var attackerKind = KindOf(attacker, source);
            var attackerEntity = source is MyCubeBlock block ? block.CubeGrid.EntityId : source?.EntityId ?? 0;
            var weapon = info.Type.String + ":" + (source is MyCharacter c && Wildlife.IsAnimal(c) ? c.Definition?.Id.SubtypeName : WeaponName(source));
            switch (target)
            {
                case IMySlimBlock slim:
                {
                    var grid = (MyCubeGrid)slim.CubeGrid;
                    var victim = slim.OwnerId != 0 ? slim.OwnerId : Identities.Owner(grid);
                    var at = grid.GridIntegerToWorld(slim.Position);
                    var relation = Relation(attacker, attackerKind, victim, false);
                    Add(info.Type == MyDamageType.Grind ? Grind : Hit, attacker, attackerKind, attackerEntity, weapon, grid.EntityId, "block", victim, relation, info.Amount, at);
                    if (slim.IsDestroyed || slim.Integrity <= 0)
                        Add(Destroyed, attacker, attackerKind, attackerEntity, weapon, grid.EntityId, "block", victim, relation, 1, at);
                    break;
                }
                case MyCharacter character:
                {
                    var animal = Wildlife.IsAnimal(character);
                    var victim = character.GetPlayerIdentityId();
                    var at = character.PositionComp.GetPosition();
                    var targetKind = animal ? "animal" : "character";
                    var relation = Relation(attacker, attackerKind, victim, animal);
                    Add(Hit, attacker, attackerKind, attackerEntity, weapon, character.EntityId, targetKind, victim, relation, info.Amount, at);
                    if ((character.IsDead || (character.StatComp?.Health?.Value ?? 1) <= 0) && FirstDeath(character.EntityId))
                        Add(Kill, attacker, attackerKind, attackerEntity, weapon, character.EntityId, targetKind, victim, relation, 1, at);
                    break;
                }
            }
        });

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
                case MyCubeBlock block: return block.BlockDefinition.Id.SubtypeName;
                case MyCharacter character: return character.Definition?.Id.SubtypeName ?? "character";
                case MyCubeGrid _: return "grid";
                default: return entity.DefinitionId?.SubtypeName ?? entity.GetType().Name;
            }
        }

        // ------------------------------------------------------------------ the rows

        private static void Add(string kind, long attacker, string attackerKind, long attackerEntity, string weapon, long target, string targetKind,
                                long victim, string relation, double amount, Vector3D at)
        {
            var now = Clock.Now;
            lock (Lock)
            {
                if (_windowStart < 0) _windowStart = now;
                var key = (kind, attacker, attackerEntity, weapon, target, victim);
                if (!Open.TryGetValue(key, out var a))
                    Open[key] = a = new Aggregate
                    {
                        Kind = kind, Attacker = attacker, AttackerKind = attackerKind, AttackerEntity = attackerEntity, Weapon = weapon,
                        Target = target, TargetKind = targetKind, Victim = victim, Relation = relation,
                    };
                a.Amount += amount;
                a.Count++;
                a.Last = now;
                a.Position = at;
            }
        }

        /// <summary>The rows of the window that ended (all of them when forced); game thread, every frame.</summary>
        public static void Flush(Recorder recorder, bool force = false)
        {
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
                    a.Target, a.TargetKind, a.Victim, a.Relation, a.Amount, a.Count, a.Position.X, a.Position.Y, a.Position.Z));
        }
    }
}

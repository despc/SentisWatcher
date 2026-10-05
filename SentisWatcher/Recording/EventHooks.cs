using System;
using System.Collections.Generic;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Gui;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.GameServices;
using VRageMath;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// The game's own events the recorder listens to: players joining and leaving, chat, factions, grids
    /// appearing and disappearing, blocks built and removed, damage and deaths.
    /// </summary>
    public sealed class EventHooks
    {
        private static bool _damageRegistered;
        private readonly HashSet<long> _deadReported = new HashSet<long>();
        private readonly HashSet<MyCubeGrid> _grids = new HashSet<MyCubeGrid>();

        private static Recorder R => Recorder.Current;

        public void Attach()
        {
            if (MyMultiplayer.Static != null)
            {
                MyMultiplayer.Static.ClientJoined += OnJoined;
                MyMultiplayer.Static.ClientLeft += OnLeft;
                MyMultiplayer.Static.ChatMessageReceived += OnChat;
            }
            MySession.Static.Factions.FactionStateChanged += OnFaction;
            MyEntities.OnEntityAdd += OnEntityAdd;
            MyEntities.OnEntityRemove += OnEntityRemove;
            foreach (var entity in MyEntities.GetEntities())
                if (entity is MyCubeGrid grid) Watch(grid);
            if (!_damageRegistered)
            {
                // cannot be unregistered: it does nothing while nothing is recorded
                MyAPIGateway.Session.DamageSystem.RegisterAfterDamageHandler(int.MaxValue, OnDamage);
                _damageRegistered = true;
            }
        }

        public void Detach()
        {
            if (MyMultiplayer.Static != null)
            {
                MyMultiplayer.Static.ClientJoined -= OnJoined;
                MyMultiplayer.Static.ClientLeft -= OnLeft;
                MyMultiplayer.Static.ChatMessageReceived -= OnChat;
            }
            if (MySession.Static?.Factions != null) MySession.Static.Factions.FactionStateChanged -= OnFaction;
            MyEntities.OnEntityAdd -= OnEntityAdd;
            MyEntities.OnEntityRemove -= OnEntityRemove;
            lock (_grids)
            {
                foreach (var grid in _grids)
                {
                    grid.OnBlockAdded -= OnBlockAdded;
                    grid.OnBlockRemoved -= OnBlockRemoved;
                }
                _grids.Clear();
            }
        }

        private void OnJoined(ulong steamId, string name) => Recorder.Safe("join", () =>
        {
            var identity = Identities.OfSteam(steamId);
            R.Name(identity, "player", name, 0, steamId);
            R.Event("join", identity, 0, detail: name + " " + steamId);
        });

        private void OnLeft(ulong steamId, MyChatMemberStateChangeEnum state) => Recorder.Safe("leave", () =>
        {
            var identity = Identities.OfSteam(steamId);
            R.Event("leave", identity, 0, detail: state + " " + steamId);
        });

        private void OnChat(ulong steamId, string text, ChatChannel channel, long target, ChatMessageCustomData? custom) =>
            Recorder.Safe("chat", () => R.Event("chat", Identities.OfSteam(steamId), target, detail: channel + ": " + text.Short(1000)));

        private void OnFaction(MyFactionStateChange change, long fromFaction, long toFaction, long player, long sender) =>
            Recorder.Safe("faction", () =>
            {
                string Tag(long id) => MySession.Static.Factions.TryGetFactionById(id)?.Tag ?? id.ToString();
                R.Event("faction", sender, player, detail: change + " " + Tag(fromFaction) + " -> " + Tag(toFaction));
            });

        private void OnEntityAdd(MyEntity entity) => Recorder.Safe("entity add", () =>
        {
            if (!(entity is MyCubeGrid grid) || grid.Physics == null) return;
            Watch(grid);
            var owner = Identities.Owner(grid);
            R.Name(grid.EntityId, "grid", grid.DisplayName, owner);
            R.Event("grid_added", owner, grid.EntityId, grid.PositionComp.GetPosition(), count: grid.BlocksCount, detail: grid.DisplayName);
        });

        private void OnEntityRemove(MyEntity entity) => Recorder.Safe("entity remove", () =>
        {
            if (!(entity is MyCubeGrid grid)) return;
            lock (_grids)
            {
                if (_grids.Remove(grid))
                {
                    grid.OnBlockAdded -= OnBlockAdded;
                    grid.OnBlockRemoved -= OnBlockRemoved;
                }
            }
            if (grid.Physics == null) return;
            R.Event("grid_removed", Identities.Owner(grid), grid.EntityId, grid.PositionComp.GetPosition(), count: grid.BlocksCount, detail: grid.DisplayName);
        });

        private void Watch(MyCubeGrid grid)
        {
            if (grid.Physics == null) return;
            lock (_grids)
            {
                if (!_grids.Add(grid)) return;
            }
            grid.OnBlockAdded += OnBlockAdded;
            grid.OnBlockRemoved += OnBlockRemoved;
        }

        private void OnBlockAdded(MySlimBlock block) => Recorder.Safe("block added", () =>
            R.Aggregator.Add(Storage.Clock.Now, "block_built", block.BuiltBy, block.CubeGrid.EntityId, null, 1,
                block.CubeGrid.GridIntegerToWorld(block.Position)));

        private void OnBlockRemoved(MySlimBlock block) => Recorder.Safe("block removed", () =>
            R.Aggregator.Add(Storage.Clock.Now, "block_removed", 0, block.CubeGrid.EntityId, null, 1,
                block.CubeGrid.GridIntegerToWorld(block.Position)));

        private void OnDamage(object target, MyDamageInformation info) => Recorder.Safe("damage", () =>
        {
            DamageLog.Damage(target, info);
            var attacker = Identities.OfEntity(info.AttackerId);
            var type = info.Type.String;
            switch (target)
            {
                case IMySlimBlock slim:
                {
                    var grid = (MyCubeGrid)slim.CubeGrid;
                    var at = grid.GridIntegerToWorld(slim.Position);
                    var kind = info.Type == MyDamageType.Grind ? "grind" : "damage";
                    R.Aggregator.Add(Storage.Clock.Now, kind, attacker, grid.EntityId, type + " by " + info.AttackerId, info.Amount, at);
                    if (slim.IsDestroyed || slim.Integrity <= 0)
                        R.Aggregator.Add(Storage.Clock.Now, "destroyed", attacker, grid.EntityId, type + " by " + info.AttackerId, 1, at);
                    break;
                }
                case MyCharacter character:
                {
                    var victim = character.GetPlayerIdentityId();
                    var at = character.PositionComp.GetPosition();
                    R.Aggregator.Add(Storage.Clock.Now, "damage", attacker, victim, type + " by " + info.AttackerId + " to character", info.Amount, at);
                    if (character.IsDead)
                    {
                        lock (_deadReported)
                        {
                            if (!_deadReported.Add(character.EntityId)) return;
                        }
                        R.Event("death", attacker, victim, at, info.Amount, detail: type + " by " + info.AttackerId);
                    }
                    break;
                }
            }
        });
    }
}

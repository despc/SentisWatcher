using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SentisWatcher.Storage;
using VRage.Game.Entity;
using VRageMath;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// Writes where the players and the grids are, on the game thread, a little every frame: the players
    /// once a second, the grids in slices of <see cref="GridBudgetMs"/> so that every grid is looked at about
    /// every <see cref="GridPassMs"/>. A position is written when it changed (ChangeFilter); a new UTC day
    /// starts with every player and grid written again.
    /// </summary>
    public sealed class Sampler
    {
        public const int PlayerEveryFrames = 60;
        public const long GridPassMs = 10_000;
        public const double GridBudgetMs = 0.2;

        private readonly Recorder _recorder;
        private readonly Dictionary<long, PlayerState> _players = new Dictionary<long, PlayerState>();
        private readonly Dictionary<long, GridState> _grids = new Dictionary<long, GridState>();
        private readonly Dictionary<long, long> _characters = new Dictionary<long, long>();
        private MyCubeGrid[] _pass = new MyCubeGrid[0];
        private int _cursor;
        private long _passStarted;
        private int _frame;
        private DateTime _day;

        public Sampler(Recorder recorder)
        {
            _recorder = recorder;
        }

        /// <summary>Game thread, every frame.</summary>
        public void Tick()
        {
            var today = DateTime.UtcNow.Date;
            if (today != _day)
            {
                // a new day file: everybody is written again, and the planets
                _day = today;
                _players.Clear();
                _grids.Clear();
                Recorder.Safe("world", _recorder.World);
            }
            if (++_frame % PlayerEveryFrames == 0) SamplePlayers();
            SampleGrids();
        }

        private void SamplePlayers()
        {
            var now = Clock.Now;
            foreach (var player in MySession.Static.Players.GetOnlinePlayers())
            {
                var identity = player.Identity?.IdentityId ?? 0;
                if (identity == 0) continue;
                var controlled = player.Controller?.ControlledEntity?.Entity;
                var character = player.Character;
                var body = (MyEntity)character ?? controlled;
                if (body == null || body.MarkedForClose) continue;
                var grid = (controlled as MyCubeBlock)?.CubeGrid;
                var state = new PlayerState
                {
                    Time = now,
                    Position = body.PositionComp.GetPosition(),
                    Controlled = controlled != null && controlled != character ? controlled.EntityId : 0,
                    Grid = grid?.EntityId ?? 0,
                    Health = character?.StatComp?.Health?.Value ?? 0,
                };
                _recorder.Name(identity, "player", player.DisplayName, 0, player.Id.SteamId);
                var last = _players.TryGetValue(identity, out var l) ? l : (PlayerState?)null;
                Transitions(identity, last, state, controlled, character);
                if (!ChangeFilter.PlayerChanged(last, state)) continue;
                _players[identity] = state;
                var v = (grid?.Physics ?? character?.Physics)?.LinearVelocity ?? Vector3.Zero;
                var p = state.Position;
                _recorder.Store.Add(new Row(Table.PlayerPos, now, now, identity, p.X, p.Y, p.Z, (double)v.X, (double)v.Y, (double)v.Z,
                    (double)state.Health, state.Controlled, state.Grid));
            }
        }

        /// <summary>
        /// What a player did that a position does not say: took or left the controls of a cockpit, a remote,
        /// a turret; got a new character (respawned).
        /// </summary>
        private void Transitions(long identity, PlayerState? last, PlayerState now, MyEntity controlled, MyCharacter character)
        {
            if (character != null)
            {
                if (_characters.TryGetValue(identity, out var was) && was != character.EntityId)
                    _recorder.Event("spawn", identity, character.EntityId, now.Position, detail: "new character (was " + was + ")");
                _characters[identity] = character.EntityId;
            }
            if (!last.HasValue || last.Value.Controlled == now.Controlled) return;
            if (last.Value.Controlled != 0)
                _recorder.Event("control_leave", identity, last.Value.Grid != 0 ? last.Value.Grid : last.Value.Controlled, now.Position,
                    detail: "left " + last.Value.Controlled);
            if (now.Controlled != 0)
            {
                var block = controlled as MyCubeBlock;
                _recorder.Event("control", identity, now.Grid != 0 ? now.Grid : now.Controlled, now.Position,
                    detail: block != null ? block.DisplayNameText + " on " + block.CubeGrid.DisplayName : controlled?.DisplayName);
            }
        }

        private void SampleGrids()
        {
            var now = Clock.Now;
            if (_cursor >= _pass.Length)
            {
                if (now - _passStarted < GridPassMs) return;
                var started = Stopwatch.GetTimestamp();
                _pass = MyEntities.GetEntities().OfType<MyCubeGrid>().ToArray();
                _cursor = 0;
                _passStarted = now;
                Prune();
                SentisWatcherPlugin.NotePart(6, Stopwatch.GetTimestamp() - started);
            }
            var watch = Stopwatch.StartNew();
            var budget = (long)(GridBudgetMs * Stopwatch.Frequency / 1000);
            while (_cursor < _pass.Length && watch.ElapsedTicks < budget)
                Sample(_pass[_cursor++], now);
        }

        private void Sample(MyCubeGrid grid, long now)
        {
            if (grid == null || grid.MarkedForClose || grid.Closed || grid.Physics == null) return;
            var matrix = grid.WorldMatrix;
            var owner = Identities.Owner(grid);
            var state = new GridState
            {
                Time = now,
                Position = grid.PositionComp.GetPosition(),
                Forward = matrix.Forward,
                Up = matrix.Up,
                Blocks = grid.BlocksCount,
                Owner = owner,
                IsStatic = grid.IsStatic,
            };
            _recorder.Name(grid.EntityId, "grid", grid.DisplayName, owner);
            var last = _grids.TryGetValue(grid.EntityId, out var l) ? l : (GridState?)null;
            if (!ChangeFilter.GridChanged(last, state)) return;
            if (last.HasValue && last.Value.Owner != owner)
                _recorder.Event("grid_owner", owner, grid.EntityId, state.Position, detail: "from " + last.Value.Owner + " to " + owner);
            _grids[grid.EntityId] = state;
            var v = grid.Physics.LinearVelocity;
            var p = state.Position;
            _recorder.Store.Add(new Row(Table.GridPos, now, now, grid.EntityId, p.X, p.Y, p.Z,
                (double)state.Forward.X, (double)state.Forward.Y, (double)state.Forward.Z,
                (double)state.Up.X, (double)state.Up.Y, (double)state.Up.Z,
                (double)v.X, (double)v.Y, (double)v.Z, state.Blocks, owner, state.IsStatic ? 1 : 0,
                grid.PositionComp.LocalVolume.Radius));
        }

        /// <summary>Forgets grids that are gone, once a pass.</summary>
        private void Prune()
        {
            foreach (var id in _grids.Keys.Where(id => !MyEntities.EntityExists(id)).ToList()) _grids.Remove(id);
        }
    }
}

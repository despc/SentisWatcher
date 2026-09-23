using System;
using VRageMath;

namespace SentisWatcher.Recording
{
    /// <summary>What was last written of a player.</summary>
    public struct PlayerState
    {
        public long Time;
        public Vector3D Position;
        public long Controlled;
        public long Grid;
        public float Health;
    }

    /// <summary>What was last written of a grid.</summary>
    public struct GridState
    {
        public long Time;
        public Vector3D Position;
        public Vector3 Forward;
        public Vector3 Up;
        public int Blocks;
        public long Owner;
        public bool IsStatic;
    }

    /// <summary>
    /// Whether a new sample says something the last written one does not: positions are written on change
    /// (and once in a while anyway), so a standing grid costs almost nothing.
    /// </summary>
    public static class ChangeFilter
    {
        public const double PlayerMoveM = 2;
        public const float PlayerHealthStep = 1;
        public const long PlayerHeartbeatMs = 60_000;

        public const double GridMoveM = 5;
        public const double GridTurnDegrees = 5;
        public const long GridHeartbeatMs = 10 * 60_000;

        private static readonly double TurnCos = Math.Cos(GridTurnDegrees * Math.PI / 180);

        public static bool PlayerChanged(PlayerState? last, PlayerState now)
        {
            if (last == null) return true;
            var l = last.Value;
            return now.Time - l.Time >= PlayerHeartbeatMs
                   || Vector3D.DistanceSquared(l.Position, now.Position) >= PlayerMoveM * PlayerMoveM
                   || l.Controlled != now.Controlled || l.Grid != now.Grid
                   || Math.Abs(l.Health - now.Health) >= PlayerHealthStep;
        }

        public static bool GridChanged(GridState? last, GridState now)
        {
            if (last == null) return true;
            var l = last.Value;
            return now.Time - l.Time >= GridHeartbeatMs
                   || Vector3D.DistanceSquared(l.Position, now.Position) >= GridMoveM * GridMoveM
                   || Vector3.Dot(l.Forward, now.Forward) < TurnCos || Vector3.Dot(l.Up, now.Up) < TurnCos
                   || l.Blocks != now.Blocks || l.Owner != now.Owner || l.IsStatic != now.IsStatic;
        }
    }
}

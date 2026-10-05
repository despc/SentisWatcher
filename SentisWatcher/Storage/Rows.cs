using System;

namespace SentisWatcher.Storage
{
    /// <summary>The tables a row goes to.</summary>
    public enum Table
    {
        Names,
        PlayerPos,
        GridPos,
        Events,
        Inventories,
        Alerts,
        Planets,
        Meta,
        Perf,
        Load,
        Spikes,
        Damage,
    }

    /// <summary>
    /// One row to write: its table and its values in the column order of <see cref="Columns"/>. Made on the
    /// game thread, written on the writer's.
    /// </summary>
    public sealed class Row
    {
        public readonly Table Table;
        public readonly long Time;
        public readonly object[] Values;

        public Row(Table table, long time, params object[] values)
        {
            Table = table;
            Time = time;
            Values = values;
        }

        /// <summary>Rows that may be dropped when the writer falls far behind: the next ones say the same.</summary>
        /// (Not inventories: their flows are the ledger, a lost row breaks it.)
        public bool Droppable => Table == Table.PlayerPos || Table == Table.GridPos;
    }

    /// <summary>The columns of each table, as <see cref="Row.Values"/> hold them (t first where the table has it).</summary>
    public static class Columns
    {
        public static string[] Of(Table table)
        {
            switch (table)
            {
                case Table.Names: return new[] { "id", "kind", "name", "owner", "steam", "t" };
                case Table.PlayerPos: return new[] { "t", "identity", "x", "y", "z", "vx", "vy", "vz", "health", "controlled", "grid" };
                case Table.GridPos: return new[] { "t", "grid", "x", "y", "z", "fx", "fy", "fz", "ux", "uy", "uz", "vx", "vy", "vz", "blocks", "owner", "static", "radius" };
                case Table.Events: return new[] { "t", "kind", "actor", "entity", "x", "y", "z", "amount", "count", "detail" };
                case Table.Inventories: return new[] { "t", "entity", "inv", "grid", "owner", "items", "volume", "max_volume", "flows" };
                case Table.Alerts: return new[] { "t", "kind", "actor", "entity", "detail" };
                case Table.Planets: return new[] { "id", "name", "generator", "x", "y", "z", "radius", "min_radius", "max_radius", "atmosphere", "gravity", "t" };
                case Table.Meta: return new[] { "key", "value" };
                case Table.Perf: return new[] { "t", "frames", "frame", "frame_max", "physics", "physics_max", "gc0", "gc1", "gc2", "gc_time", "managed_mb", "private_mb", "working_mb", "sim", "players", "blocks", "components" };
                case Table.Load: return new[] { "t", "kind", "entity", "name", "owner", "owner_name", "frames", "ms", "max_ms", "alloc_kb" };
                case Table.Spikes: return new[] { "t", "frame_ms", "gc0", "gc1", "gc2", "untimed_ms", "top" };
                case Table.Damage: return new[] { "t", "kind", "attacker", "attacker_kind", "attacker_entity", "weapon", "target", "target_kind", "victim", "relation", "amount", "count", "x", "y", "z" };
                default: throw new ArgumentOutOfRangeException(nameof(table));
            }
        }

        public static string TableName(Table table)
        {
            switch (table)
            {
                case Table.Names: return "names";
                case Table.PlayerPos: return "player_pos";
                case Table.GridPos: return "grid_pos";
                case Table.Events: return "events";
                case Table.Inventories: return "inventories";
                case Table.Alerts: return "alerts";
                case Table.Planets: return "planets";
                case Table.Meta: return "meta";
                case Table.Perf: return "perf";
                case Table.Load: return "load";
                case Table.Spikes: return "spikes";
                case Table.Damage: return "damage";
                default: throw new ArgumentOutOfRangeException(nameof(table));
            }
        }

        public static string Insert(Table table)
        {
            var columns = Of(table);
            var verb = table == Table.Names || table == Table.Planets || table == Table.Meta ? "INSERT OR REPLACE" : "INSERT";
            return verb + " INTO " + TableName(table) + "(" + string.Join(",", columns) + ") VALUES(@" +
                   string.Join(",@", columns) + ")";
        }
    }

    /// <summary>Time as the day files hold it.</summary>
    public static class Clock
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long Now => ToMs(DateTime.UtcNow);

        public static long ToMs(DateTime utc) => (long)(utc.ToUniversalTime() - Epoch).TotalMilliseconds;

        public static DateTime FromMs(long ms) => Epoch.AddMilliseconds(ms);

        /// <summary>The UTC day of the time, the day file it belongs to.</summary>
        public static DateTime Day(long ms) => FromMs(ms).Date;

        /// <summary>The server's time zone offset at that time, in minutes.</summary>
        public static int OffsetMinutes(long ms) => (int)TimeZoneInfo.Local.GetUtcOffset(FromMs(ms)).TotalMinutes;

        /// <summary>The server's time zone as people read it: UTC+03:00.</summary>
        public static string Zone(long ms)
        {
            var offset = TimeZoneInfo.Local.GetUtcOffset(FromMs(ms));
            return "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString("hh\\:mm");
        }

        /// <summary>The time on the server's clock.</summary>
        public static DateTime Local(long ms) => FromMs(ms).ToLocalTime();
    }
}

using System;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// The first seconds after the world loads are left out of the load charts: the server settles then (the code
    /// run for the first time, the grids and the voxels coming in, the plugins starting), and its peaks of a hundred
    /// milliseconds and more drowned what the charts are for.
    /// </summary>
    public static class Warmup
    {
        /// <summary>How long after the world loads the charts start.</summary>
        public static readonly TimeSpan Delay = TimeSpan.FromSeconds(30);

        private static DateTime _loadedAt = DateTime.MaxValue;

        /// <summary>The world has loaded: the wait starts.</summary>
        public static void Begin() => _loadedAt = DateTime.UtcNow;

        /// <summary>Whether the wait is over (never before the world has loaded).</summary>
        public static bool Over => Passed(DateTime.UtcNow, _loadedAt);

        public static bool Passed(DateTime now, DateTime loadedAt) => loadedAt != DateTime.MaxValue && now - loadedAt >= Delay;
    }
}

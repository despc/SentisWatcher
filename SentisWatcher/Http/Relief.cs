using System;
using System.Collections.Generic;
using System.Diagnostics;
using NLog;
using VRageMath;

namespace SentisWatcher.Http
{
    /// <summary>
    /// A planet's relief for the map: the surface's distance from the centre over a grid of directions, so
    /// the planet is drawn with its mountains and valleys and a base on a hillside sits on the ground
    /// rather than floating over a smooth sphere of the average radius. The directions are laid out as the
    /// vertices of the page's sphere (three.js <c>SphereGeometry</c>: <see cref="Width"/> segments round,
    /// <see cref="Height"/> from pole to pole), one sample a vertex. Read from the planet's own height map
    /// (what the game builds the terrain from, not dug holes) once a planet and kept.
    /// </summary>
    public static class Relief
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public const int Width = 512;
        public const int Height = 256;

        private static readonly Dictionary<long, object> Cache = new Dictionary<long, object>();

        /// <summary>{ id, w, h, base, relief } - relief as metres from the base radius, int16 little endian, base64; null for no such planet.</summary>
        public static object Of(long id, string name = null)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(id, out var known)) return known;
            }
            // by its id, else by its storage name (as the day file has them)
            var planets = Sandbox.Game.Entities.Planet.MyPlanets.GetPlanets();
            var planet = planets?.Find(p => p.EntityId == id) ?? (name == null ? null : planets?.Find(p => p.StorageName == name));
            if (planet == null)
            {
                Log.Info($"SentisWatcher: relief of planet {id} {name}: no such planet in the game");
                return null;
            }
            var watch = Stopwatch.StartNew();
            var center = planet.PositionComp.GetPosition();
            var average = (double)planet.AverageRadius;
            var above = planet.MaximumRadius + 1000.0;
            var bytes = new byte[(Width + 1) * (Height + 1) * 2];
            var k = 0;
            for (var iy = 0; iy <= Height; iy++)
            {
                var theta = Math.PI * iy / Height;
                for (var ix = 0; ix <= Width; ix++)
                {
                    var phi = 2 * Math.PI * ix / Width;
                    var dir = new Vector3D(-Math.Cos(phi) * Math.Sin(theta), Math.Cos(theta), Math.Sin(phi) * Math.Sin(theta));
                    var from = center + dir * above;
                    var surface = planet.GetClosestSurfacePointGlobal(ref from);
                    var offset = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(Vector3D.Distance(surface, center) - average)));
                    bytes[k++] = (byte)(offset & 0xFF);
                    bytes[k++] = (byte)((offset >> 8) & 0xFF);
                }
            }
            var result = new Dictionary<string, object>
            {
                ["id"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["w"] = Width, ["h"] = Height, ["base"] = average, ["relief"] = Convert.ToBase64String(bytes),
            };
            Log.Info($"SentisWatcher: relief of {planet.StorageName} read in {watch.ElapsedMilliseconds} ms");
            lock (Cache) Cache[id] = result;
            return result;
        }

        /// <summary>
        /// The surface near a point in detail, for the map close up: an <paramref name="n"/> by n grid of
        /// directions over a square of <paramref name="size"/> metres on the plane square to the planet's
        /// radius through the point (east and north on it), the surface's distance from the centre along each.
        /// { base, n, size, center, up, east, north, heights } - heights as float32 metres from base, little
        /// endian, base64; the page builds each direction as normalize(up * base + east * a + north * b) with
        /// a and b from -size/2 to size/2, as here. Null for no such planet.
        /// </summary>
        public static object Patch(long id, string name, double x, double y, double z, double size, int n)
        {
            var planets = Sandbox.Game.Entities.Planet.MyPlanets.GetPlanets();
            var planet = planets?.Find(p => p.EntityId == id) ?? (name == null ? null : planets?.Find(p => p.StorageName == name));
            if (planet == null) return null;
            n = Math.Max(2, Math.Min(257, n));
            size = Math.Max(100, Math.Min(100_000, size));
            var center = planet.PositionComp.GetPosition();
            var average = (double)planet.AverageRadius;
            var above = planet.MaximumRadius + 1000.0;
            var up = Vector3D.Normalize(new Vector3D(x, y, z) - center);
            // east: square to up and to the world's Y (the page's up), any other axis at a pole
            var east = Vector3D.Cross(Vector3D.Up, up);
            if (east.LengthSquared() < 1e-6) east = Vector3D.Cross(Vector3D.Right, up);
            east = Vector3D.Normalize(east);
            var north = Vector3D.Cross(up, east);
            var bytes = new byte[n * n * 4];
            var k = 0;
            for (var j = 0; j < n; j++)
            for (var i = 0; i < n; i++)
            {
                var a = (i / (double)(n - 1) - 0.5) * size;
                var b = (j / (double)(n - 1) - 0.5) * size;
                var dir = Vector3D.Normalize(up * average + east * a + north * b);
                var from = center + dir * above;
                var surface = planet.GetClosestSurfacePointGlobal(ref from);
                var h = BitConverter.GetBytes((float)(Vector3D.Distance(surface, center) - average));
                Buffer.BlockCopy(h, 0, bytes, k, 4);
                k += 4;
            }
            return new Dictionary<string, object>
            {
                ["base"] = average, ["n"] = n, ["size"] = size,
                ["center"] = new[] { center.X, center.Y, center.Z },
                ["up"] = new[] { up.X, up.Y, up.Z }, ["east"] = new[] { east.X, east.Y, east.Z }, ["north"] = new[] { north.X, north.Y, north.Z },
                ["heights"] = Convert.ToBase64String(bytes),
            };
        }
    }
}

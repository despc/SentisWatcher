using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NLog;

namespace SentisWatcher.Storage
{
    /// <summary>
    /// Loads the native SQLite before System.Data.SQLite needs it.
    ///
    /// Torch loads every *.dll under a plugin's folder, subfolders too, as a .NET assembly, and a native one
    /// stops the server; so the plugin ships it as SQLite.Interop.x64.bin. It is copied out to a folder of its
    /// own (the temp folder, by size and date so a new version gets a new copy) as x64\SQLite.Interop.dll and
    /// loaded by its full path; System.Data.SQLite then finds it by name.
    /// </summary>
    public static class NativeSqlite
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public const string ShippedName = "SQLite.Interop.x64.bin";
        private const string Library = "SQLite.Interop.dll";
        private static bool _loaded;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        public static void Load()
        {
            if (_loaded) return;
            var shipped = Candidates().Select(dir => Path.Combine(dir, ShippedName)).FirstOrDefault(File.Exists);
            if (shipped == null)
            {
                Log.Warn(ShippedName + " not found next to the plugin; leaving the native SQLite to System.Data.SQLite");
                return;
            }
            var info = new FileInfo(shipped);
            var folder = Path.Combine(Path.GetTempPath(), "SentisWatcher", info.Length + "-" + info.LastWriteTimeUtc.Ticks);
            var path = Path.Combine(folder, "x64", Library);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var partial = path + ".part";
                File.Copy(shipped, partial, true);
                File.Move(partial, path);
            }
            // System.Data.SQLite's own preloader looks here too
            Environment.SetEnvironmentVariable("PreLoadSQLite_BaseDirectory", folder);
            if (LoadLibrary(path) == IntPtr.Zero)
            {
                Log.Error("Could not load " + path + " (error " + Marshal.GetLastWin32Error() + ")");
                return;
            }
            Log.Info("Native SQLite loaded from " + path);
            _loaded = true;
        }

        private static IEnumerable<string> Candidates()
        {
            var own = typeof(NativeSqlite).Assembly.Location;     // empty when Torch loaded the plugin from bytes
            if (!string.IsNullOrEmpty(own)) yield return Path.GetDirectoryName(own);
            var plugins = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");
            yield return Path.Combine(plugins, "SentisWatcher");
            if (Directory.Exists(plugins))
                foreach (var dir in Directory.GetDirectories(plugins).Where(d => Path.GetFileName(d).IndexOf("Watcher", StringComparison.OrdinalIgnoreCase) >= 0))
                    yield return dir;
        }
    }
}

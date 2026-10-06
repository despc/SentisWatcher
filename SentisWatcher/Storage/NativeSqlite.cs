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
    /// stops the server; so it never lies there under its own name. The plugin carries it inside itself (an embedded
    /// resource), and beside itself as SQLite.Interop.x64.bin as before (used when the resource is not there). It is
    /// copied out to a folder of its own (the temp folder, by size and date so a new version gets a new copy) as
    /// x64\SQLite.Interop.dll and loaded by its full path; System.Data.SQLite then finds it by name.
    /// </summary>
    public static class NativeSqlite
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public const string ShippedName = "SQLite.Interop.x64.bin";
        public const string EmbeddedName = "Native/SQLite.Interop.x64.dll";
        private const string Library = "SQLite.Interop.dll";
        private static bool _loaded;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        public static void Load()
        {
            if (_loaded) return;
            string folder, path;
            var assembly = typeof(NativeSqlite).Assembly;
            using (var embedded = assembly.GetManifestResourceStream(EmbeddedName))
            {
                if (embedded != null)
                {
                    // from inside the plugin: a copy per build of the plugin (its module's id) and size
                    folder = Path.Combine(Path.GetTempPath(), "SentisWatcher", embedded.Length + "-" + assembly.ManifestModule.ModuleVersionId.ToString("N"));
                    path = Path.Combine(folder, "x64", Library);
                    if (!File.Exists(path) || new FileInfo(path).Length != embedded.Length)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        var partial = path + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".part";
                        using (var file = File.Create(partial)) embedded.CopyTo(file);
                        if (File.Exists(path)) File.Delete(path);
                        File.Move(partial, path);
                    }
                }
                else
                {
                    var shipped = Candidates().Select(dir => Path.Combine(dir, ShippedName)).FirstOrDefault(File.Exists);
                    if (shipped == null)
                    {
                        Log.Error("SentisWatcher: the native SQLite is neither inside the plugin nor beside it (" + ShippedName +
                                  " in Plugins/SentisWatcher); nothing will be recorded");
                        return;
                    }
                    var info = new FileInfo(shipped);
                    folder = Path.Combine(Path.GetTempPath(), "SentisWatcher", info.Length + "-" + info.LastWriteTimeUtc.Ticks);
                    path = Path.Combine(folder, "x64", Library);
                    if (!File.Exists(path))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        var partial = path + ".part";
                        File.Copy(shipped, partial, true);
                        File.Move(partial, path);
                    }
                }
            }
            // System.Data.SQLite's own preloader looks here too
            Environment.SetEnvironmentVariable("PreLoadSQLite_BaseDirectory", folder);
            if (LoadLibrary(path) == IntPtr.Zero)
            {
                Log.Error("Could not load " + path + " (error " + Marshal.GetLastWin32Error() + ")");
                return;
            }
            if (SentisWatcher.SentisWatcherPlugin.Config?.DiagnosticLogs == true) Log.Info("Native SQLite loaded from " + path);
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

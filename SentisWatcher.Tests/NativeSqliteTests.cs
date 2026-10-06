using System;
using System.Diagnostics;
using System.Linq;
using SentisWatcher.Storage;
using Xunit;

/// <summary>
/// The native SQLite travels inside the plugin: SentisWatcher.dll given to someone without SQLite.Interop.x64.bin
/// beside it recorded nothing ("Unable to load DLL 'SQLite.Interop.dll'" on every batch, 05.10.2026).
/// </summary>
public class NativeSqliteTests
{
    [Fact]
    public void The_native_library_is_inside_the_plugin()
    {
        using (var stream = typeof(NativeSqlite).Assembly.GetManifestResourceStream(NativeSqlite.EmbeddedName))
        {
            Assert.NotNull(stream);
            Assert.True(stream.Length > 1_000_000, "the embedded native SQLite is " + stream.Length + " bytes");
        }
    }

    [Fact]
    public void It_is_loaded_from_the_plugins_own_copy_and_opens_a_database()
    {
        NativeSqlite.Load();
        var loaded = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Where(m => string.Equals(m.ModuleName, "SQLite.Interop.dll", StringComparison.OrdinalIgnoreCase)).Select(m => m.FileName).ToList();
        Assert.Contains(loaded, path => path.IndexOf(@"\SentisWatcher\", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                        path.StartsWith(System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        using (var connection = new System.Data.SQLite.SQLiteConnection("Data Source=:memory:"))
        {
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "select sqlite_version()";
                Assert.False(string.IsNullOrEmpty((string)command.ExecuteScalar()));
            }
        }
    }
}

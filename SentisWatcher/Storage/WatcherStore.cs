using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using NLog;

namespace SentisWatcher.Storage
{
    /// <summary>
    /// Takes rows from any thread and writes them on a thread of its own, once a second, in one transaction,
    /// to the file of the row's UTC day. Nothing on the game thread waits for the disk.
    ///
    /// The queue is bounded: past <see cref="SoftLimit"/> rows positions and inventories are dropped (the
    /// next ones say the same, and the drop is logged); past <see cref="HardLimit"/> everything is.
    /// </summary>
    public sealed class WatcherStore : IDisposable
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public const int SoftLimit = 300_000;
        public const int HardLimit = 2_000_000;
        private const int BatchRows = 20_000;
        private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(1);

        private readonly string _folder;
        private readonly Func<int> _retentionDays;
        private readonly ConcurrentQueue<Row> _queue = new ConcurrentQueue<Row>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _thread;
        private volatile bool _stopping;
        private int _queued;
        private long _dropped, _written;
        private DateTime _lastDropLog = DateTime.MinValue;

        private SQLiteConnection _connection;
        private DateTime _connectionDay;
        private readonly Dictionary<Table, SQLiteCommand> _inserts = new Dictionary<Table, SQLiteCommand>();

        public string Folder => _folder;
        public long Written => Interlocked.Read(ref _written);
        public long Dropped => Interlocked.Read(ref _dropped);
        public int Queued => Volatile.Read(ref _queued);

        public WatcherStore(string folder, Func<int> retentionDays)
        {
            _folder = folder;
            _retentionDays = retentionDays;
            Directory.CreateDirectory(folder);
            _thread = new Thread(Loop) { IsBackground = true, Name = "SentisWatcher writer" };
            _thread.Start();
        }

        /// <summary>Queues a row (any thread).</summary>
        public void Add(Row row)
        {
            var queued = Volatile.Read(ref _queued);
            if (queued >= HardLimit || (queued >= SoftLimit && row.Droppable))
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            _queue.Enqueue(row);
            Interlocked.Increment(ref _queued);
        }

        /// <summary>Writes what is queued and waits for it (plugin unload).</summary>
        public void Dispose()
        {
            _stopping = true;
            _wake.Set();
            _thread.Join(TimeSpan.FromSeconds(30));
            CloseConnection();
        }

        /// <summary>A read-only connection to a day's file, or null when there is none (any thread).</summary>
        public SQLiteConnection OpenRead(DateTime day)
        {
            var path = DayFiles.PathOf(_folder, day);
            if (!File.Exists(path)) return null;
            var connection = new SQLiteConnection("Data Source=" + path + ";Read Only=True;Pooling=False;");
            connection.Open();
            return connection;
        }

        private void Loop()
        {
            DeleteExpired(DateTime.UtcNow.Date);
            while (true)
            {
                _wake.WaitOne(FlushEvery);
                try
                {
                    while (WriteBatch()) { }
                }
                catch (Exception e)
                {
                    Log.Error(e, "SentisWatcher: writing failed; the batch is lost");
                    CloseConnection();
                    Thread.Sleep(1000);
                }
                LogDrops();
                if (_stopping && _queue.IsEmpty) return;
            }
        }

        /// <summary>Writes up to a batch of rows; true when more are waiting.</summary>
        private bool WriteBatch()
        {
            if (_queue.IsEmpty) return false;
            var rows = new List<Row>(Math.Min(BatchRows, Volatile.Read(ref _queued)));
            while (rows.Count < BatchRows && _queue.TryDequeue(out var row))
            {
                Interlocked.Decrement(ref _queued);
                rows.Add(row);
            }
            var i = 0;
            while (i < rows.Count)
            {
                // one transaction a day file: rows of a new day switch the file
                var day = Clock.Day(rows[i].Time);
                if (day < _connectionDay) day = _connectionDay;    // a late row of yesterday goes to today
                EnsureConnection(day);
                using (var transaction = _connection.BeginTransaction())
                {
                    for (; i < rows.Count; i++)
                    {
                        if (Clock.Day(rows[i].Time) > _connectionDay) break;
                        Insert(rows[i]);
                    }
                    transaction.Commit();
                }
            }
            Interlocked.Add(ref _written, rows.Count);
            return !_queue.IsEmpty && rows.Count == BatchRows;
        }

        private void Insert(Row row)
        {
            var command = _inserts[row.Table];
            var parameters = command.Parameters;
            // a row may leave out the last columns: they are NULL, not what the previous row had
            for (var c = 0; c < parameters.Count; c++) parameters[c].Value = c < row.Values.Length ? row.Values[c] ?? DBNull.Value : DBNull.Value;
            command.ExecuteNonQuery();
        }

        /// <summary>An inventory's last row in a day file: whose it was, on what grid, what it held.</summary>
        public struct HeldRow
        {
            public long Entity, Grid, Owner;
            public int Inv;
            public string Items;
        }

        /// <summary>
        /// The inventories not gone by the end of the given days (as far as the files know): the last row of
        /// each, the later day's winning, with its items. What the server keeps in memory of the inventories
        /// is lost at a restart; with these it can still tell which of them went away while it was down.
        /// </summary>
        public List<HeldRow> LastHeld(params DateTime[] days)
        {
            var last = new Dictionary<(long, int), HeldRow>();
            foreach (var day in days.OrderBy(d => d))
                using (var db = OpenRead(day))
                {
                    if (db == null) continue;
                    using (var command = new SQLiteCommand(
                               "SELECT i.entity, i.inv, i.grid, i.owner, i.items FROM inventories i JOIN " +
                               "(SELECT entity, inv, MAX(id) AS id FROM inventories GROUP BY entity, inv) l ON l.id = i.id", db))
                    using (var r = command.ExecuteReader())
                        while (r.Read())
                            last[(r.GetInt64(0), (int)r.GetInt64(1))] = new HeldRow
                            {
                                Entity = r.GetInt64(0), Inv = (int)r.GetInt64(1), Grid = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                                Owner = r.IsDBNull(3) ? 0 : r.GetInt64(3), Items = r.IsDBNull(4) ? null : r.GetString(4),
                            };
                }
            return last.Values.Where(h => h.Items != null).ToList();
        }

        public static bool HasColumn(SQLiteConnection connection, string table, string column)
        {
            using (var command = new SQLiteCommand("PRAGMA table_info(" + table + ")", connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void EnsureConnection(DateTime day)
        {
            if (_connection != null && day == _connectionDay) return;
            CloseConnection();
            var path = DayFiles.PathOf(_folder, day);
            _connection = new SQLiteConnection("Data Source=" + path + ";Pooling=False;");
            _connection.Open();
            using (var create = new SQLiteCommand(Schema.Create, _connection)) create.ExecuteNonQuery();
            foreach (var (table, column, alter) in Schema.Added)
                if (!HasColumn(_connection, table, column))
                    using (var command = new SQLiteCommand(alter, _connection)) command.ExecuteNonQuery();
            using (var meta = new SQLiteCommand("INSERT OR REPLACE INTO meta(key, value) VALUES('schema', @v)", _connection))
            {
                meta.Parameters.AddWithValue("@v", Schema.Version.ToString());
                meta.ExecuteNonQuery();
            }
            foreach (Table table in Enum.GetValues(typeof(Table)))
            {
                var command = new SQLiteCommand(Columns.Insert(table), _connection);
                foreach (var column in Columns.Of(table)) command.Parameters.Add(new SQLiteParameter("@" + column));
                command.Prepare();
                _inserts[table] = command;
            }
            _connectionDay = day;
            Log.Info("SentisWatcher: writing to " + path);
            DeleteExpired(day);
        }

        private void CloseConnection()
        {
            foreach (var command in _inserts.Values) command.Dispose();
            _inserts.Clear();
            _connection?.Dispose();
            _connection = null;
        }

        private void DeleteExpired(DateTime today)
        {
            try
            {
                foreach (var file in DayFiles.Expired(Directory.GetFiles(_folder, "watcher-*.db"), today, _retentionDays()))
                {
                    foreach (var part in new[] { file, file + "-wal", file + "-shm" })
                        if (File.Exists(part)) File.Delete(part);
                    Log.Info("SentisWatcher: deleted " + Path.GetFileName(file) + " (older than " + _retentionDays() + " days)");
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "SentisWatcher: could not delete old day files");
            }
        }

        private void LogDrops()
        {
            var dropped = Interlocked.Read(ref _dropped);
            if (dropped == 0 || DateTime.UtcNow - _lastDropLog < TimeSpan.FromMinutes(1)) return;
            _lastDropLog = DateTime.UtcNow;
            Log.Warn("SentisWatcher: the writer fell behind; " + dropped + " rows dropped so far, " + Queued + " queued");
        }
    }
}

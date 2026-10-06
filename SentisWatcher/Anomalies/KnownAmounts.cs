using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Text.RegularExpressions;
using SentisWatcher.Storage;

namespace SentisWatcher.Anomalies
{
    /// <summary>
    /// A wrong amount is an alert once, not every hour and at every start of the server: the same stack - this
    /// inventory's owner, this item, this amount - stays as it is until somebody touches it, and a world came with
    /// seventeen of them that made 374 rows in a day of restarts (06.10.2026). What was alerted is remembered here and
    /// read back from the day files when the server starts; a stack whose amount changes is a new one.
    /// </summary>
    public sealed class KnownAmounts
    {
        public const string AlertKind = "bad_amount";

        // "... on Grid: ConsumableItem/MammalMeatRaw 2.666667 (a fraction of a ConsumableItem)": the item and the amount
        private static readonly Regex InDetail = new Regex(@"^.*: (\S+/\S+) (\S+) \([^()]*\)$", RegexOptions.Compiled | RegexOptions.Singleline);

        private readonly HashSet<string> _known = new HashSet<string>();
        private bool _read;

        public static string Key(long entity, string item, string amount) => entity + "|" + item + "|" + amount;

        /// <summary>The key of an alert row as <see cref="Key"/> gives it, or null when the detail is not of this kind.</summary>
        public static string KeyOf(long entity, string detail)
        {
            var match = InDetail.Match(detail ?? "");
            return match.Success ? Key(entity, match.Groups[1].Value, match.Groups[2].Value) : null;
        }

        /// <summary>Whether this is the first time the stack is seen with this amount; remembers it.</summary>
        public bool First(long entity, string item, string amount)
        {
            lock (_known) return _known.Add(Key(entity, item, amount));
        }

        public void Remember(string key)
        {
            if (key == null) return;
            lock (_known) _known.Add(key);
        }

        /// <summary>What the day files already hold of these alerts (once; a file that cannot be read is left out).</summary>
        public void ReadOnce(WatcherStore store)
        {
            if (_read) return;
            _read = true;
            foreach (var day in DayFiles.Days(store.Folder))
            {
                SQLiteConnection db = null;
                try
                {
                    db = store.OpenRead(day);
                    if (db == null) continue;
                    using (var check = new SQLiteCommand("SELECT 1 FROM sqlite_master WHERE type='table' AND name='alerts'", db))
                        if (check.ExecuteScalar() == null) continue;
                    using (var cmd = new SQLiteCommand("SELECT DISTINCT entity, detail FROM alerts WHERE kind=@k", db))
                    {
                        cmd.Parameters.AddWithValue("@k", AlertKind);
                        using (var r = cmd.ExecuteReader())
                            while (r.Read())
                                if (!r.IsDBNull(0) && !r.IsDBNull(1)) Remember(KeyOf(r.GetInt64(0), r.GetString(1)));
                    }
                }
                catch (Exception)
                {
                    // an old or a broken day file: its alerts may come once more
                }
                finally
                {
                    db?.Dispose();
                }
            }
        }
    }
}

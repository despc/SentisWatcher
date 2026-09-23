using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SentisWatcher.Storage
{
    /// <summary>One SQLite file a UTC day: watcher-2026-09-23.db. Keeping N days is deleting older files.</summary>
    public static class DayFiles
    {
        private const string Prefix = "watcher-";
        private const string Extension = ".db";

        public static string FileName(DateTime day) => Prefix + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + Extension;

        public static string PathOf(string folder, DateTime day) => Path.Combine(folder, FileName(day));

        /// <summary>The day of a day file's name, or null when the name is not one.</summary>
        public static DateTime? DayOf(string fileName)
        {
            var name = Path.GetFileName(fileName ?? "");
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                return null;
            var date = name.Substring(Prefix.Length, name.Length - Prefix.Length - Extension.Length);
            return DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                ? day.Date
                : (DateTime?)null;
        }

        /// <summary>
        /// The day files to delete: those older than <paramref name="retentionDays"/> days counting today
        /// (14 keeps today and the 13 days before). A retention under 1 keeps today only.
        /// </summary>
        public static List<string> Expired(IEnumerable<string> fileNames, DateTime today, int retentionDays)
        {
            var oldestKept = today.Date.AddDays(-(Math.Max(1, retentionDays) - 1));
            return fileNames.Where(f => DayOf(f) is DateTime day && day < oldestKept).ToList();
        }

        /// <summary>The days that have a file, newest first.</summary>
        public static List<DateTime> Days(string folder) =>
            Directory.Exists(folder)
                ? Directory.GetFiles(folder, Prefix + "*" + Extension).Select(DayOf).Where(d => d.HasValue)
                    .Select(d => d.Value).OrderByDescending(d => d).ToList()
                : new List<DateTime>();
    }
}

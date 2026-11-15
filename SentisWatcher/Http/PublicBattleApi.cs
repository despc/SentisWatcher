using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SentisWatcher.Storage;

namespace SentisWatcher.Web
{
    /// <summary>
    /// Read-only, delayed, authenticated publication on the stats listener, not a proxy for the private API.
    /// A site backend reaches it through a loopback TLS reverse proxy. Cached, bounded requests never wait on the
    /// game thread, and at most one caller may perform database work at a time (others receive 503, not a queue).
    /// </summary>
    public sealed class PublicBattleApi
    {
        public const int MaxHours = 168, MaxCatalog = 1000, MaxPageSize = 50, RequestsPerMinute = 60;
        private const int MaxDetailCache = 64, MaxJsonChars = 2 * 1024 * 1024, MaxDetailCacheChars = 16 * 1024 * 1024;

        /// <summary>
        /// The catalog is read anew this often at most (and on a wider window asked for). It is the battles of whole days,
        /// read in Torch's process: a week of production (11.11.2026, 18 000 rows a day) is 1.5-2 s of a core and 200 MB
        /// to collect, a day 0.25 s and 30 MB. Battles are published 15 minutes after their end anyway.
        /// </summary>
        public const long CatalogMs = 5 * 60000;

        /// <summary>
        /// Reports made (read from the base) a minute at most; the rest wait (503 with Retry-After). A published battle's
        /// report does not change: it is kept as long as the catalog lists it, not made again.
        /// </summary>
        public const int ReportsPerMinute = 6;
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml };
        private readonly Func<long, long, string, object> _read;
        private readonly Func<long> _clock;
        private readonly bool _enabled;
        private readonly byte[] _keyHash;
        private readonly object _work = new object(), _cacheLock = new object(), _rateLock = new object();
        private Catalog _catalog;
        private readonly Dictionary<string, (long At, string Json)> _details = new Dictionary<string, (long, string)>();
        private long _detailChars;
        private long _rateStart, _reportStart, _expandedAt = long.MinValue / 2;
        private int _requests, _reports;

        public int DelayMinutes { get; }
        public bool Ready => _keyHash != null;

        public sealed class Reply
        {
            public int Status { get; }
            public string Json { get; }
            public int RetryAfter { get; }
            public Reply(int status, string json, int retryAfter = 0) { Status = status; Json = json; RetryAfter = retryAfter; }
        }

        private sealed class Catalog
        {
            public long At, Before;
            public int Hours;
            public List<JToken> Items;
            public Dictionary<string, JToken> ById;
            public bool Truncated;
        }

        public PublicBattleApi(Func<long, long, string, object> read, bool enabled, string key, int delayMinutes = 15, Func<long> clock = null)
        {
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _enabled = enabled;
            _clock = clock ?? (() => Clock.Now);
            DelayMinutes = Math.Max(5, Math.Min(1440, delayMinutes));
            if (ValidKey(key))
                using (var hash = SHA256.Create()) _keyHash = hash.ComputeHash(Encoding.ASCII.GetBytes(key));
        }

        private static bool ValidKey(string key) => key != null && key.Length >= 32 && key.Length <= 256 && key.All(c => c > 32 && c < 127);

        /// <summary>The key file when the config names none: next to the instance's configs, in a folder of its own.</summary>
        public static string DefaultKeyFile(string instance) => Path.Combine(instance, "SentisWatcher-Secrets", "battles.key");

        /// <summary>
        /// A key file that does not exist is made: 256 random bits (base64url, 43 characters), in a folder and a file that
        /// only this account (Torch's), SYSTEM and the administrators may read - inherited access cut off. An existing
        /// file is never touched, whatever it holds (read and judged by <see cref="ReadKeyFile"/>). Returns whether it
        /// was made now. Never logs or returns the key.
        /// </summary>
        public static bool EnsureKeyFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || File.Exists(path)) return false;
            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder)) return false;
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            if (!Directory.Exists(folder))
            {
                var dirSecurity = new System.Security.AccessControl.DirectorySecurity();
                dirSecurity.SetAccessRuleProtection(true, false);
                foreach (var who in new[] { me, system, admins })
                    dirSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(who, System.Security.AccessControl.FileSystemRights.FullControl,
                        System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                        System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
                Directory.CreateDirectory(folder, dirSecurity);
            }
            var fileSecurity = new System.Security.AccessControl.FileSecurity();
            fileSecurity.SetAccessRuleProtection(true, false);
            foreach (var who in new[] { me, system, admins })
                fileSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(who, System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var key = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            try
            {
                using (var file = new FileStream(path, FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.Write, FileShare.None, 4096,
                           FileOptions.WriteThrough, fileSecurity))
                {
                    var data = Encoding.ASCII.GetBytes(key);
                    file.Write(data, 0, data.Length);
                }
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return false; // made by another start at the same moment
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        /// <summary>Read once at startup. Never return the path, file contents or I/O exception to an HTTP caller.</summary>
        public static string ReadKeyFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' ||
                    (path[2] != '\\' && path[2] != '/')) return null;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < 32 || info.Length > 512) return null;
                var key = File.ReadAllText(path, Encoding.UTF8).Trim();
                return ValidKey(key) ? key : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (System.Security.SecurityException) { return null; }
        }

        private bool Authorized(string authorization)
        {
            if (authorization == null || authorization.Length > 300 || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
            var key = authorization.Substring(7);
            if (!ValidKey(key)) return false;
            using (var hash = SHA256.Create())
            {
                var supplied = hash.ComputeHash(Encoding.ASCII.GetBytes(key));
                var difference = 0;
                for (var i = 0; i < supplied.Length; i++) difference |= supplied[i] ^ _keyHash[i];
                return difference == 0;
            }
        }

        private bool RateAllowed(long now)
        {
            lock (_rateLock)
            {
                if (now < _rateStart || now - _rateStart >= 60000) { _rateStart = now; _requests = 0; }
                return ++_requests <= RequestsPerMinute;
            }
        }

        private static Reply Error(int status, string message, int retry = 0) => new Reply(status, JsonConvert.SerializeObject(new { error = message }), retry);
        private static string Json(object value) => JsonConvert.SerializeObject(value, JsonSettings);
        private static bool Integer(NameValueCollection query, string key, int fallback, int min, int max, out int value)
        {
            value = fallback;
            return query[key] == null || (int.TryParse(query[key], NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max);
        }
        private static bool Published(JToken summary, long before) => summary != null && summary["ongoing"]?.Type == JTokenType.Boolean &&
            !(bool)summary["ongoing"] && summary["endedAt"]?.Type == JTokenType.Integer && (long)summary["endedAt"] <= before &&
            summary["clippedStart"]?.Value<bool>() != true && summary["clippedEnd"]?.Value<bool>() != true;

        public Reply Get(string path, NameValueCollection query, string authorization, bool loopback = true)
        {
            if (!_enabled) return Error(404, "not found");
            if (!loopback) return Error(403, "use the local TLS reverse proxy");
            if (!Ready) return Error(503, "battle publication unavailable", 60);
            if (!Authorized(authorization)) return Error(401, "authentication required");
            var now = _clock();
            if (!RateAllowed(now)) return Error(429, "too many requests", 60);
            query = query ?? new NameValueCollection();
            var list = path == "/battles";
            var id = path != null && path.StartsWith("/battles/", StringComparison.Ordinal) ? path.Substring(9) : "";
            if (!list && (id.Length != 32 || !id.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))) return Error(404, "not found");
            var allowed = list ? new[] { "hours", "page", "pageSize" } : new string[0];
            if (query.AllKeys.Any(k => k == null || !allowed.Contains(k) || query.GetValues(k).Length != 1)) return Error(400, "unsupported query");
            if (!Integer(query, "hours", 24, 1, MaxHours, out var hours) || !Integer(query, "page", 1, 1, 1000, out var page) ||
                !Integer(query, "pageSize", 20, 1, MaxPageSize, out var size)) return Error(400, "invalid pagination or hours");

            Catalog catalog;
            lock (_cacheLock) catalog = _catalog;
            // The common 24-hour list must not scan a week. A report ID the catalog does not list widens it to the full
            // publication age (an old deep link), but once in CatalogMs at most: unknown IDs must not make Torch read a
            // week each time (anyone may ask /battles/<any hex> through the site)
            var listHours = hours <= 24 ? 24 : hours <= 72 ? 72 : MaxHours;
            bool Fresh(Catalog value) => value != null && now >= value.At && now - value.At < CatalogMs;
            var requiredHours = list ? listHours : Fresh(catalog) && catalog.ById.ContainsKey(id) ? catalog.Hours : MaxHours;
            if (!list && Fresh(catalog) && catalog.Hours < MaxHours && !catalog.ById.ContainsKey(id))
                lock (_cacheLock)
                {
                    if (now >= _expandedAt && now - _expandedAt < CatalogMs) return Error(404, "not found");
                    _expandedAt = now;
                }
            bool Refresh(Catalog value) => !Fresh(value) || value.Hours < requiredHours;
            if (Refresh(catalog))
            {
                if (!Monitor.TryEnter(_work)) return Error(503, "refresh in progress", 2);
                try
                {
                    lock (_cacheLock) catalog = _catalog;
                    if (Refresh(catalog))
                    {
                        var before = now - DelayMinutes * 60000L;
                        var raw = JObject.FromObject(_read(before - requiredHours * 3600000L, before, null));
                        var eligible = (raw["battles"] as JArray ?? new JArray()).Where(s => Published(s, before) && (long)s["endedAt"] >= before - requiredHours * 3600000L)
                            .OrderByDescending(s => (long)s["start"]).Take(MaxCatalog + 1).ToList();
                        catalog = new Catalog
                        {
                            At = now, Before = before, Hours = requiredHours, Truncated = eligible.Count > MaxCatalog,
                            Items = eligible.Take(MaxCatalog).ToList(),
                        };
                        catalog.ById = catalog.Items.ToDictionary(s => PublicBattleProjection.Id((string)s["id"]));
                        // the reports kept stay: a published battle's report does not change, and one the new catalog no
                        // longer lists is not answered (ById first)
                        lock (_cacheLock) _catalog = catalog;
                    }
                }
                finally { Monitor.Exit(_work); }
            }
            if (list)
            {
                var from = catalog.Before - hours * 3600000L;
                var items = catalog.Items.Where(s => (long)s["endedAt"] >= from).ToList();
                return new Reply(200, Json(new
                {
                    schemaVersion = 1, generatedAt = catalog.At, availableThrough = catalog.Before, delayMinutes = DelayMinutes,
                    from, to = catalog.Before, page, pageSize = size, total = items.Count, truncated = catalog.Truncated,
                    battles = items.Skip((page - 1) * size).Take(size).Select(PublicBattleProjection.Summary).ToList(),
                }));
            }
            if (!catalog.ById.TryGetValue(id, out var summary)) return Error(404, "not found");
            lock (_cacheLock)
                if (_details.TryGetValue(id, out var cached)) return new Reply(200, cached.Json);
            if (!Monitor.TryEnter(_work)) return Error(503, "report in progress", 2);
            try
            {
                lock (_cacheLock)
                {
                    if (_details.TryGetValue(id, out var cached)) return new Reply(200, cached.Json);
                    if (now < _reportStart || now - _reportStart >= 60000) { _reportStart = now; _reports = 0; }
                    if (_reports >= ReportsPerMinute) return Error(503, "reports limit", (int)Math.Max(1, (60000 - (now - _reportStart) + 999) / 1000));
                    _reports++;
                }
                var raw = JObject.FromObject(_read((long)summary["start"], (long)summary["end"], (string)summary["id"]));
                var report = raw["report"];
                if (report == null || report.Type == JTokenType.Null || (string)report["summary"]?["id"] != (string)summary["id"] ||
                    !Published(report["summary"], catalog.Before)) return Error(404, "not found");
                var json = Json(new
                {
                    schemaVersion = 1, generatedAt = now, availableThrough = catalog.Before, delayMinutes = DelayMinutes,
                    report = PublicBattleProjection.Report(report),
                });
                if (json.Length > MaxJsonChars) return Error(503, "report exceeds publication limit", 60);
                lock (_cacheLock)
                {
                    while (_details.Count > 0 && (_details.Count >= MaxDetailCache || _detailChars + json.Length > MaxDetailCacheChars))
                    {
                        var oldest = _details.OrderBy(p => p.Value.At).First();
                        _details.Remove(oldest.Key);
                        _detailChars -= oldest.Value.Json.Length;
                    }
                    _details[id] = (now, json);
                    _detailChars += json.Length;
                }
                return new Reply(200, json);
            }
            finally { Monitor.Exit(_work); }
        }
    }
}

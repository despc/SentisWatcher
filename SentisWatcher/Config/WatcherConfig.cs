using System;
using SentisWatcher.GUI;
using Torch;

namespace SentisWatcher.Config
{
    /// <summary>The plugin's settings (SentisWatcher.cfg in the instance folder).</summary>
    public class WatcherConfig : ViewModel
    {
        private bool _enabled = true;
        private int _retentionDays = 14;
        private string _databaseFolder = "";
        private bool _diagnosticLogs;

        /// <summary>Record at all.</summary>
        [DisplayTab(Name = "Recording enabled", GroupName = "Recording", Tab = "Recording", Order = 0,
            Description = "Record positions, events and inventories. Takes effect at the next server start.")]
        public bool Enabled
        {
            get => _enabled;
            set => SetValue(ref _enabled, value);
        }

        /// <summary>Days of records kept; older day files are deleted.</summary>
        [DisplayTab(Name = "Keep days", GroupName = "Recording", Tab = "Recording", Order = 1, LiveUpdate = true,
            Description = "Days of records kept, today included. Older day files are deleted at start and at UTC midnight.")]
        public int RetentionDays
        {
            get => _retentionDays;
            set => SetValue(ref _retentionDays, value);
        }

        /// <summary>Where the day files go; empty is the plugin's storage folder (Instance\SentisWatcher).</summary>
        [DisplayTab(Name = "Database folder", GroupName = "Recording", Tab = "Recording", Order = 2,
            Description = "Folder of the day files (watcher-YYYY-MM-DD.db). Empty: Instance\\SentisWatcher. Takes effect at the next server start.")]
        public string DatabaseFolder
        {
            get => _databaseFolder;
            set => SetValue(ref _databaseFolder, value);
        }

        private bool _webEnabled = true;
        private int _webPort = 18950;

        /// <summary>The web view of the records, on this machine (127.0.0.1) unless <see cref="WebLan"/> is on.</summary>
        [DisplayTab(Name = "Web view enabled", GroupName = "Web view", Tab = "Web view", Order = 0,
            Description = "A page with a 3D map, a time slider, the events and the inventories, at http://127.0.0.1:<port>/ - " +
                          "on this machine only, or from the network with \"Web view on the network\". Takes effect at the next server start.")]
        public bool WebEnabled
        {
            get => _webEnabled;
            set => SetValue(ref _webEnabled, value);
        }

        [DisplayTab(Name = "Web view port", GroupName = "Web view", Tab = "Web view", Order = 1,
            Description = "The port of the web view. Takes effect at the next server start.")]
        public int WebPort
        {
            get => _webPort;
            set => SetValue(ref _webPort, value);
        }

        private bool _webLan;

        /// <summary>The web view on every address of the machine, so other machines of the network open it too.</summary>
        [DisplayTab(Name = "Web view on the network", GroupName = "Web view", Tab = "Web view", Order = 2,
            Description = "Listen on every address of the machine (http://<this machine>:<port>/), not only 127.0.0.1. Windows " +
                          "lets a server that is not run as administrator listen so only after a one-time reservation of the " +
                          "address (netsh http add urlacl, see watcher_lan.ps1 by the server) and an open firewall port; without " +
                          "them the view stays on 127.0.0.1 and the log says what to run. The page shows positions and inventories: " +
                          "only on a network you trust. Takes effect at the next server start.")]
        public bool WebLan
        {
            get => _webLan;
            set => SetValue(ref _webLan, value);
        }

        private int _publicPort;
        private string _publicOrigin = "*";

        /// <summary>The public stats for the server's site (PublicServer): 0 is off.</summary>
        [DisplayTab(Name = "Public stats port", GroupName = "Public stats", Tab = "Web view", Order = 10,
            Description = "A port for the server's public site to read who is online (/online) and the load of the simulation " +
                          "thread and the simulation speed (/perf?hours=3, up to 24) as JSON. Only names, faction tags and the " +
                          "charts go out - no positions, grids or inventories. Optional protected battle reports use this port too, " +
                          "but only through a local TLS reverse proxy and a backend API key. Listens on every address of the machine once the " +
                          "address is reserved (netsh http add urlacl url=http://+:<port>/, the log says the command), else on " +
                          "127.0.0.1. 0 is off. Takes effect at the next server start.")]
        public int PublicPort
        {
            get => _publicPort;
            set => SetValue(ref _publicPort, value);
        }

        [DisplayTab(Name = "Public stats site", GroupName = "Public stats", Tab = "Web view", Order = 11,
            Description = "The site allowed to read the public stats from a browser (CORS), e.g. https://example.org; * is any. " +
                          "Takes effect at the next server start.")]
        public string PublicOrigin
        {
            get => _publicOrigin;
            set => SetValue(ref _publicOrigin, value);
        }

        private int _battleEndMinutes = 2;

        /// <summary>Minutes without related damage after which a battle is over.</summary>
        [DisplayTab(Name = "Battle ends after silence (minutes)", GroupName = "Battles", Tab = "Web view", Order = 5, LiveUpdate = true,
            Description = "A battle is over when this many minutes pass without new related damage. Default 2, minimum 1, maximum 30. " +
                          "Battles are found when they are read, so this applies at once, to old records too: a longer pause joins fights " +
                          "a short one splits (their IDs change). Also the window of shots and jumps around a battle.")]
        public int BattleEndMinutes
        {
            get => _battleEndMinutes;
            set => SetValue(ref _battleEndMinutes, Math.Max(1, Math.Min(30, value)));
        }

        private bool _publicBattlesEnabled = true;
        private string _publicBattlesKeyFile = "";
        private int _publicBattlesDelayMinutes = 15;

        [DisplayTab(Name = "Publish battle reports", GroupName = "Battle REST API", Tab = "Web view", Order = 12,
            Description = "Enable GET /battles and /battles/<id> on the public stats port (on by default). Requires a private key file: " +
                          "with no file named below, one is made at the first start in Instance\\SentisWatcher-Secrets\\battles.key. " +
                          "Only loopback callers (the site on this machine, or a local TLS reverse proxy) are accepted; the website backend holds the key, never the browser. " +
                          "Only delayed, completed, sanitized reports are published. Takes effect after restart.")]
        public bool PublicBattlesEnabled
        {
            get => _publicBattlesEnabled;
            set => SetValue(ref _publicBattlesEnabled, value);
        }

        [DisplayTab(Name = "Battle API key file", GroupName = "Battle REST API", Tab = "Web view", Order = 13,
            Description = "Absolute path to a private UTF-8 file with a random 32-256 character ASCII bearer token. Empty: " +
                          "Instance\\SentisWatcher-Secrets\\battles.key, made at the first start if missing (this account, SYSTEM, administrators). " +
                          "Keep outside web roots; restrict file access to the Torch account and administrators. " +
                          "The token itself is never shown here. Empty/unreadable/invalid: fail closed. Read once after restart.")]
        public string PublicBattlesKeyFile
        {
            get => _publicBattlesKeyFile;
            set => SetValue(ref _publicBattlesKeyFile, value);
        }

        [DisplayTab(Name = "Battle publication delay (minutes)", GroupName = "Battle REST API", Tab = "Web view", Order = 14,
            Description = "Wait this long AFTER the battle is over (the silence above) before publication. Default 15, minimum 5, maximum 1440. " +
                          "No live battles, coordinates, internal entity/identity/Steam IDs or real grid names are exposed. Takes effect after restart.")]
        public int PublicBattlesDelayMinutes
        {
            get => _publicBattlesDelayMinutes;
            set => SetValue(ref _publicBattlesDelayMinutes, value);
        }

        private bool _loadSampling = true;

        /// <summary>Who loads the game thread: grids, characters, session components, plugins (LoadSampler).</summary>
        [DisplayTab(Name = "Load by grid and player", GroupName = "Performance", Tab = "Performance", Order = 0, LiveUpdate = true,
            Description = "Time the updates of every grid, character, session component and plugin in one frame of every 30-90, " +
                          "at random, and record who loads the game thread. Turns on and off at once (also: !watch load on|off).")]
        public bool LoadSampling
        {
            get => _loadSampling;
            set => SetValue(ref _loadSampling, value);
        }

        /// <summary>The plugin's diagnostic lines in the Torch log.</summary>
        [DisplayTab(Name = "Diagnostic logs", GroupName = "Logs", Tab = "Logs", Order = 0, LiveUpdate = true,
            Description = "Alerts copied into the Torch log (they are in the web view anyway), the recording cost, reliefs, file clean-up. Off by default; errors are logged anyway.")]
        public bool DiagnosticLogs
        {
            get => _diagnosticLogs;
            set => SetValue(ref _diagnosticLogs, value);
        }
    }
}

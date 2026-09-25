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

        /// <summary>The web view of the records, on this machine only (127.0.0.1).</summary>
        [DisplayTab(Name = "Web view enabled", GroupName = "Web view", Tab = "Web view", Order = 0,
            Description = "A page with a 3D map, a time slider, the events and the inventories, at http://127.0.0.1:<port>/ - " +
                          "on this machine only (use a tunnel to see it from elsewhere). Takes effect at the next server start.")]
        public bool WebEnabled
        {
            get => _webEnabled;
            set => SetValue(ref _webEnabled, value);
        }

        [DisplayTab(Name = "Web view port", GroupName = "Web view", Tab = "Web view", Order = 1,
            Description = "The port of the web view on 127.0.0.1. Takes effect at the next server start.")]
        public int WebPort
        {
            get => _webPort;
            set => SetValue(ref _webPort, value);
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
    }
}

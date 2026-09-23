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
    }
}

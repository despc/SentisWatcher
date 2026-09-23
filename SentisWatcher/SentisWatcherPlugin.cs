using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using NLog;
using SentisWatcher.Config;
using SentisWatcher.GUI;
using SentisWatcher.Recording;
using SentisWatcher.Storage;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Session;

namespace SentisWatcher
{
    /// <summary>
    /// Records what happens on the server - where players and grids are, what is done and by whom, what is in
    /// every inventory - into one SQLite file a day, and looks for what no honest inventory or transfer does.
    /// </summary>
    public class SentisWatcherPlugin : TorchPluginBase, IWpfPlugin
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static Persistent<WatcherConfig> _config;

        public static WatcherConfig Config => _config?.Data;
        public static SentisWatcherPlugin Instance { get; private set; }

        public WatcherStore Store { get; private set; }
        public Sampler Sampler { get; private set; }
        public InventorySweep Sweep { get; private set; }
        private EventHooks _hooks;
        private ConfigGUI _control;
        private string _configPath;

        /// <summary>The plugin's page in Torch.</summary>
        public UserControl GetControl() => _control ?? (_control = new ConfigGUI());

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            Instance = this;
            _configPath = Path.Combine(StoragePath, "SentisWatcher.cfg");
            _config = Persistent<WatcherConfig>.Load(_configPath);
            _config.Save();
            var sessions = Torch.Managers.GetManager<TorchSessionManager>();
            if (sessions != null) sessions.SessionStateChanged += OnSessionState;
        }

        private void OnSessionState(ITorchSession session, TorchSessionState state)
        {
            try
            {
                if (state == TorchSessionState.Loaded) Start();
                else if (state == TorchSessionState.Unloading) Stop();
            }
            catch (Exception e)
            {
                Log.Error(e, "SentisWatcher: " + state + " failed");
            }
        }

        private void Start()
        {
            if (!Config.Enabled)
            {
                Log.Info("SentisWatcher is disabled");
                return;
            }
            NativeSqlite.Load();
            var folder = string.IsNullOrWhiteSpace(Config.DatabaseFolder) ? Path.Combine(StoragePath, "SentisWatcher") : Config.DatabaseFolder;
            Store = new WatcherStore(folder, () => Config.RetentionDays);
            var recorder = new Recorder(Store);
            Sampler = new Sampler(recorder);
            Sweep = new InventorySweep(recorder);
            _hooks = new EventHooks();
            Recorder.Current = recorder;
            _hooks.Attach();
            recorder.Event("server_start", 0, 0, detail: "SentisWatcher recording");
            Log.Info("SentisWatcher: recording into " + folder + ", keeping " + Config.RetentionDays + " days");
        }

        private void Stop()
        {
            var recorder = Recorder.Current;
            if (recorder == null) return;
            _hooks?.Detach();
            recorder.FlushAggregates(force: true);
            recorder.Event("server_stop", 0, 0);
            Recorder.Current = null;
            Store?.Dispose();
            Store = null;
            Sampler = null;
            Sweep = null;
            Log.Info("SentisWatcher: stopped");
        }

        /// <summary>Game thread, every frame.</summary>
        public override void Update()
        {
            var recorder = Recorder.Current;
            if (recorder == null) return;
            var started = Cost.Start();
            try
            {
                Sampler?.Tick();
                Sweep?.Tick();
                recorder.FlushAggregates();
            }
            catch (Exception e)
            {
                Log.Error(e, "SentisWatcher: update failed");
            }
            Cost.Stop(started);
            if (Cost.Due(DateTime.UtcNow))
            {
                Log.Info("SentisWatcher: " + Cost.Describe() + "; " + Describe());
                Cost.Reset();
            }
        }

        /// <summary>What the plugin costs the game thread in its own update (hooks not counted).</summary>
        public FrameCost Cost { get; } = new FrameCost();

        public string Describe() =>
            Store == null ? "not recording" :
                $"written {Store.Written} rows, queued {Store.Queued}, dropped {Store.Dropped}; " +
                $"last inventory pass {Sweep?.LastPassInventories} inventories, {Sweep?.LastPassWritten} written";

        public override void Dispose()
        {
            Stop();
            _config?.Save(_configPath);
            base.Dispose();
        }
    }
}

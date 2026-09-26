using System;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using NLog;
using SentisWatcher.Config;
using SentisWatcher.GUI;
using SentisWatcher.Ledger;
using SentisWatcher.Recording;
using SentisWatcher.Storage;
using SentisWatcher.Web;
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
        public WebServer Web { get; private set; }
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
            Warmup.Begin();
            PerfSampler.Start();
            if (LedgerPatches.Booking) InventoryLedger.Current = new InventoryLedger();
            else Log.Warn("SentisWatcher: the inventory ledger is off, its hooks are missing");
            _hooks.Attach();
            recorder.Event("server_start", 0, 0, detail: "SentisWatcher recording");
            if (Config.WebEnabled)
            {
                try
                {
                    Web = new WebServer(Store, Config.WebPort);
                    Web.Start();
                }
                catch (Exception e)
                {
                    Log.Error(e, "SentisWatcher: the web view could not start on port " + Config.WebPort);
                    Web = null;
                }
            }
            Log.Info("SentisWatcher: recording into " + folder + ", keeping " + Config.RetentionDays + " days");
        }

        private void Stop()
        {
            var recorder = Recorder.Current;
            if (recorder == null) return;
            _hooks?.Detach();
            PerfSampler.Stop();
            Web?.Dispose();
            Web = null;
            try
            {
                Sweep?.FlushPending();
            }
            catch (Exception e)
            {
                Log.Error(e, "SentisWatcher: the last inventory flows could not be written");
            }
            recorder.FlushAggregates(force: true);
            InventoryLedger.Current?.FlushSuspects(recorder);
            InventoryLedger.Current = null;
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
                LedgerPatches.ResetThread();
                var t = System.Diagnostics.Stopwatch.GetTimestamp();
                void Part(int i)
                {
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (now - t > PartMax[i]) PartMax[i] = now - t;
                    t = now;
                }
                Sampler?.Tick(); Part(0);
                Sweep?.Tick(); Part(1);
                PerfSampler.Tick(recorder); Part(2);
                LoadSampler.Tick(recorder); Part(3);
                recorder.FlushAggregates(); Part(4);
                InventoryLedger.Current?.FlushSuspects(recorder); Part(5);
            }
            catch (Exception e)
            {
                Log.Error(e, "SentisWatcher: update failed");
            }
            Cost.Stop(started);
            if (Cost.Due(DateTime.UtcNow))
            {
                Log.Info("SentisWatcher: " + Cost.Describe() + " (at most: " + string.Join(", ", PartNames.Select((n, i) =>
                    n + " " + (PartMax[i] * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))) + " ms); " + Describe());
                Cost.Reset();
                Array.Clear(PartMax, 0, PartMax.Length);
            }
        }

        private static readonly string[] PartNames = { "sampler", "sweep", "perf", "load", "aggregates", "ledger", "sampler pass start", "sweep pass start", "sweep pass end" };
        private static readonly long[] PartMax = new long[9];

        /// <summary>The longest a part of the update took (ticks), for the cost line in the log.</summary>
        internal static void NotePart(int part, long ticks)
        {
            if (ticks > PartMax[part]) PartMax[part] = ticks;
        }

        /// <summary>What the plugin costs the game thread in its own update (hooks not counted).</summary>
        public FrameCost Cost { get; } = new FrameCost();

        public string Describe() =>
            Store == null ? "not recording" :
                $"written {Store.Written} rows, queued {Store.Queued}, dropped {Store.Dropped}; " +
                $"last inventory pass {Sweep?.LastPassInventories} inventories, {Sweep?.LastPassWritten} written; " +
                (InventoryLedger.Current != null ? $"ledger: {InventoryLedger.Current.PendingCount} inventories changed since their visit" : "ledger off");

        /// <summary>Keeps a setting changed by a command.</summary>
        public void SaveConfig() => _config?.Save(_configPath);

        public override void Dispose()
        {
            Stop();
            _config?.Save(_configPath);
            base.Dispose();
        }
    }
}

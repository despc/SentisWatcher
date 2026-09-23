using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SentisWatcher.GUI
{
    /// <summary>
    /// The plugin's page in Torch: what is being recorded (refreshed every few seconds) and the settings,
    /// built from their DisplayTab attributes like the other Sentis plugins.
    /// </summary>
    public class ConfigGUI : UserControl
    {
        private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(5);

        internal FilteredGrid MainFilteredGrid;
        private TextBlock _status;
        private readonly DispatcherTimer _timer;

        public ConfigGUI()
        {
            BuildUi();
            MainFilteredGrid.DataContext = SentisWatcherPlugin.Config;
            _timer = new DispatcherTimer { Interval = Refresh };
            _timer.Tick += (s, e) => UpdateStatus();
            Loaded += (s, e) =>
            {
                UpdateStatus();
                _timer.Start();
            };
            Unloaded += (s, e) => _timer.Stop();
        }

        private void BuildUi()
        {
            var root = new DockPanel();

            var header = new GroupBox { Header = "Status", Margin = new Thickness(3) };
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3) };
            header.Content = _status;
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            MainFilteredGrid = new FilteredGrid();
            root.Children.Add(MainFilteredGrid);

            Content = root;
        }

        private void UpdateStatus()
        {
            try
            {
                var plugin = SentisWatcherPlugin.Instance;
                var store = plugin?.Store;
                _status.Text = store == null
                    ? "Not recording (disabled, or no world loaded)."
                    : "Recording into " + store.Folder + "\n" + plugin.Describe() + "\n" + plugin.Cost.Describe();
            }
            catch (Exception e)
            {
                _status.Text = "Status unavailable: " + e.Message;
            }
        }
    }
}

using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using NLog;
using Sandbox.ModAPI;
using SentisWatcher.Storage;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace SentisWatcher.Commands
{
    /// <summary>
    /// !watch - what the records say, for admins, in the server's time. The answer is read on a thread of its
    /// own, sent to the chat and also saved as a text file next to the day files (a long answer is cut in the chat).
    /// </summary>
    [Category("watch")]
    public class WatchCommands : CommandModule
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const int ChatLimit = 3000;
        private const int MaxEvents = 40;

        [Command("status", "What is being recorded and where.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Status()
        {
            var plugin = SentisWatcherPlugin.Instance;
            var store = plugin?.Store;
            if (store == null)
            {
                Context.Respond("SentisWatcher records nothing (disabled or no world).");
                return;
            }
            Context.Respond($"SentisWatcher: {store.Folder}, keeps {SentisWatcherPlugin.Config.RetentionDays} days; " +
                            plugin.Describe() + "; " + plugin.Cost.Describe());
        }

        [Command("player", "!watch player <name|steam id|identity id> [minutes=60] [minutes ago it ended=0]: where the player was and what happened.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Player(string name, int minutes = 60, int endedMinutesAgo = 0)
        {
            Answer("player " + name, (query, from, to) =>
            {
                var found = query.Find("player", name, from, to);
                if (found.Count == 0) return "no player like '" + name + "' in the records of that time";
                if (found.Count > 1 && found.TrueForAll(f => !f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    return "several players match: " + string.Join(", ", found.ConvertAll(f => f.Name + " (" + f.Id + ")"));
                var exact = found.Find(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                var player = exact.Id != 0 ? exact : found[0];
                return query.Player(player.Id, player.Name, from, to, MaxEvents);
            }, minutes, endedMinutesAgo);
        }

        [Command("grid", "!watch grid <name|id> [minutes=60] [minutes ago it ended=0]: a grid's movement, owners and what was done to it.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Grid(string name, int minutes = 60, int endedMinutesAgo = 0)
        {
            Answer("grid " + name, (query, from, to) =>
            {
                var found = query.Find("grid", name, from, to);
                if (found.Count == 0) return "no grid like '" + name + "' in the records of that time";
                if (found.Count > 1 && found.TrueForAll(f => !f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    return "several grids match: " + string.Join(", ", found.ConvertAll(f => f.Name + " (" + f.Id + ")"));
                var exact = found.Find(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                var grid = exact.Id != 0 ? exact : found[0];
                return query.Grid(grid.Id, grid.Name, from, to, MaxEvents);
            }, minutes, endedMinutesAgo);
        }

        [Command("near", "!watch near <x> <y> <z> <radius> [minutes=60] [minutes ago it ended=0]: players and grids that were there.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Near(double x, double y, double z, double radius, int minutes = 60, int endedMinutesAgo = 0)
        {
            Answer("near", (query, from, to) => query.Near(x, y, z, radius, from, to), minutes, endedMinutesAgo);
        }

        [Command("inventory", "!watch inventory <block, character or grid id> [minutes=60] [minutes ago it ended=0]: what the inventories held, each time it changed.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Inventory(long entityId, int minutes = 60, int endedMinutesAgo = 0)
        {
            Answer("inventory " + entityId, (query, from, to) => query.Inventory(entityId, from, to, MaxEvents), minutes, endedMinutesAgo);
        }

        [Command("alerts", "!watch alerts [minutes=1440]: the anomalies found.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Alerts(int minutes = 1440)
        {
            Answer("alerts", (query, from, to) => query.Alerts(from, to, MaxEvents), minutes, 0);
        }

        [Command("load", "!watch load [on|off|burst<seconds>|minutes=10]: who loads the game thread (grids, players, components, plugins); on/off turns the sampling on and off.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Load(string what = "10")
        {
            var config = SentisWatcherPlugin.Config;
            if (what == "on" || what == "off")
            {
                config.LoadSampling = what == "on";
                SentisWatcherPlugin.Instance?.SaveConfig();
                Context.Respond("Load sampling is " + (config.LoadSampling ? "on" : "off"));
                return;
            }
            if (what.StartsWith("burst"))
            {
                var seconds = int.TryParse(what.Substring(5), out var s) ? s : 30;
                Recording.LoadSampler.Burst(seconds);
                Context.Respond($"Timing every frame for {seconds} s; then !watch load 2");
                return;
            }
            if (!int.TryParse(what, out var minutes)) minutes = 10;
            Answer("load", (query, from, to) => (config.LoadSampling ? "" : "(sampling is off now)" + Environment.NewLine) + query.Load(from, to, 15), minutes, 0);
        }

        private void Answer(string what, Func<WatcherQuery, long, long, string> read, int minutes, int endedMinutesAgo)
        {
            var store = SentisWatcherPlugin.Instance?.Store;
            if (store == null)
            {
                Context.Respond("SentisWatcher records nothing (disabled or no world).");
                return;
            }
            var to = Clock.Now - endedMinutesAgo * 60_000L;
            var from = to - Math.Max(1, minutes) * 60_000L;
            var context = Context;
            Task.Run(() =>
            {
                string text;
                try
                {
                    text = read(new WatcherQuery(store), from, to);
                    var file = Path.Combine(store.Folder, "watch-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
                    File.WriteAllText(file, "!watch " + what + "\n" + text);
                    if (text.Length > ChatLimit) text = text.Substring(0, ChatLimit) + "\n… (all of it in " + file + ")";
                }
                catch (Exception e)
                {
                    Log.Error(e, "SentisWatcher: !watch " + what + " failed");
                    text = "!watch " + what + " failed: " + e.Message;
                }
                MyAPIGateway.Utilities.InvokeOnGameThread(() => context.Respond(text));
            });
        }
    }
}

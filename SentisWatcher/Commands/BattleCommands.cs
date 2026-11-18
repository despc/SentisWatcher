using System;
using System.Threading.Tasks;
using NLog;
using Sandbox.Game;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using SentisWatcher.Storage;
using SentisWatcher.Web;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace SentisWatcher.Commands
{
    /// <summary>
    /// !br - the player's last battle on the site, opened in the Steam overlay (as Essentials opens its MOTD: a page the
    /// game does not whitelist goes through Steam's link filter). The battle is looked for on a thread of its own: reading
    /// a day of records takes a quarter of a second.
    /// </summary>
    public class BattleCommands : CommandModule
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        [Command("br", "Открыть в браузере результаты вашего последнего сражения.")]
        [Permission(MyPromoteLevel.None)]
        public void BattleReport()
        {
            var player = Context.Player;
            if (player == null)
            {
                Context.Respond("Команда для игрока в игре.");
                return;
            }
            var plugin = SentisWatcherPlugin.Instance;
            var page = SentisWatcherPlugin.Config?.BattleReportPage;
            var site = plugin?.Public;
            if (site == null || !site.BattlesPublished || string.IsNullOrWhiteSpace(page))
            {
                Context.Respond("Результаты сражений сейчас не публикуются.");
                return;
            }
            var context = Context;
            var identity = player.IdentityId;
            Task.Run(() =>
            {
                string answer;
                string url = null;
                try
                {
                    var now = Clock.Now;
                    var last = BattleNotices.Latest(site.Battles, identity, site.BattlesDelayMinutes, now);
                    if (last == null)
                        answer = "Ваших сражений на сайте за последнюю неделю нет. Сражение появляется там через " + site.BattlesDelayMinutes +
                                 " минут после окончания.";
                    else
                    {
                        url = BattleNotices.Url(page.Trim(), last.Value.Id, last.Value.EndedAt, site.BattlesDelayMinutes, now);
                        answer = "Открываю результаты сражения: " + url;
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "SentisWatcher: !br failed");
                    answer = "Не удалось найти сражение, попробуйте позже.";
                }
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    context.Respond(answer);
                    if (url != null) MyVisualScriptLogicProvider.OpenSteamOverlay(MyGuiSandbox.IsUrlWhitelisted(url) ? url : "https://steamcommunity.com/linkfilter/?url=" + url, identity);
                });
            });
        }
    }
}

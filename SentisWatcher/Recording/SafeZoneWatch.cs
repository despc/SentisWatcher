using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Network;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// The admins' safe zones (those of no block: an economy station's, ones made from the admin menu or by a plugin): which
    /// went and why, and which station stands without its own.
    ///
    /// The game takes a safe zone out of its list (<c>MySessionComponentSafeZones.OnRemoveSafeZone</c>) in the
    /// <c>MyEntity.Close</c> of whoever closes it, so the stack at that moment names the closer: the station generator
    /// (players gone from the station's area: normal, made again when they come back), the admin menu (with the admin who
    /// asked), Torch, a plugin, a mod. No patch: a handler of the game's own event.
    ///
    /// The game makes a station's safe zone only together with the station. One that went while the station stayed is not
    /// made again (<c>MyStationCellGenerator</c>): such a station stands open for ever. <see cref="CheckStations"/> tells
    /// of those at every world load.
    ///
    /// Their settings changed (<c>MySessionComponentSafeZones.OnSafeZoneUpdated</c>, raised by the admin menu's edit and by
    /// the safe zone blocks' requests): what changed (on/off, size, what is allowed, who may enter, the place) and through
    /// what - the admin menu (with the admin), a player's safe zone block (that should never reach a zone of no block:
    /// SentisAnticheat refuses it), a plugin or a mod. A plugin setting a zone's fields directly raises no event and is not seen.
    /// </summary>
    public static class SafeZoneWatch
    {
        private static readonly Logger Log = LogManager.GetLogger("SentisWatcher.SafeZones");
        private static bool _attached;
        // the admins' zones' settings as last seen, to tell what an update changed (game thread)
        private static readonly Dictionary<long, Dictionary<string, string>> Seen = new Dictionary<long, Dictionary<string, string>>();

        private static Recorder R => Recorder.Current;

        public static void Attach()
        {
            if (_attached) return;
            MySessionComponentSafeZones.OnRemoveSafeZone += OnRemoved;
            MySessionComponentSafeZones.OnAddSafeZone += OnAdded;
            MySessionComponentSafeZones.OnSafeZoneUpdated += OnUpdated;
            _attached = true;
            Seen.Clear();
            foreach (var zone in MySessionComponentSafeZones.SafeZones.ToList())
                if (zone.SafeZoneBlockId == 0) Seen[zone.EntityId] = Settings(zone);
            Recorder.Safe("safe zones of the stations", CheckStations);
        }

        public static void Detach()
        {
            if (!_attached) return;
            MySessionComponentSafeZones.OnRemoveSafeZone -= OnRemoved;
            MySessionComponentSafeZones.OnAddSafeZone -= OnAdded;
            MySessionComponentSafeZones.OnSafeZoneUpdated -= OnUpdated;
            _attached = false;
            Seen.Clear();
        }

        /// <summary>What closed a safe zone, from the frames of the stack (innermost first), our own and the closing itself left out.</summary>
        public static (string Reason, bool Normal, bool Unloading) Explain(string[] frames, string admin)
        {
            bool Has(string text) => frames.Any(f => f.Contains(text));
            if (Has("MyEntities.CloseAll") || Has("MySession.Unload") || Has(".UnloadData")) return ("the world unloading", true, true);
            if (Has("MyStationCellGenerator.CloseObjectSeed"))
                return ("the game: no players near the station, its area unloaded (made again with the station when someone comes)", true, false);
            if (Has("DeleteSafeZone_Implementation")) return ("deleted from the admin menu" + (admin != null ? " by " + admin : ""), false, false);
            // the first frames not of the game: Torch, a plugin, a mod (not the loops that only call the game: Torch's, Watcher's timing, patched copies)
            var outside = frames.Where(f => !f.StartsWith("Sandbox.") && !f.StartsWith("VRage.") && !f.StartsWith("System.") && !f.StartsWith("SpaceEngineers.") &&
                                            !f.StartsWith(".") && !f.StartsWith("Torch.VRageGame") && !f.StartsWith("SentisWatcher.Recording.LoadSampler")).Take(3).ToList();
            var first = outside.Count > 0 ? string.Join(" <- ", outside) : string.Join(" <- ", frames.Take(4));
            return ("closed by " + first, false, false);
        }

        private static void OnRemoved(object sender, EventArgs args) => Recorder.Safe("a safe zone removed", () =>
        {
            if (!(sender is MySafeZone zone) || zone.SafeZoneBlockId != 0) return; // a block's: the players' own, the block tells
            Seen.Remove(zone.EntityId);
            var frames = new StackTrace(1, false).GetFrames()?.Select(Frame).Where(f => f != null && !Inner(f)).ToArray() ?? new string[0];
            string admin = null;
            long actor = 0;
            var context = MyEventContext.Current;
            if (!context.IsLocallyInvoked && context.Sender.Value != 0 && frames.Any(f => f.Contains("DeleteSafeZone_Implementation")))
            {
                actor = Identities.OfSteam(context.Sender.Value);
                admin = Identities.NameOf(actor) + " (" + context.Sender.Value + ")";
            }
            var (reason, normal, unloading) = Explain(frames, admin);
            if (unloading) return;
            var at = zone.PositionComp.GetPosition();
            var what = $"\"{zone.DisplayName}\" {zone.EntityId}, radius {zone.Radius:0} m, {(zone.Enabled ? "on" : "off")}, at {at.X:0} {at.Y:0} {at.Z:0}";
            R.Event("safezone_removed", actor, zone.EntityId, at, zone.Radius, detail: what + ": " + reason);
            if (normal)
            {
                if (SentisWatcherPlugin.Config?.DiagnosticLogs == true) Log.Info("SentisWatcher: admin safe zone removed " + what + ": " + reason);
                return;
            }
            Log.Warn("SentisWatcher: admin safe zone removed " + what + ": " + reason + Environment.NewLine + "  stack: " + string.Join(" <- ", frames.Take(12)));
            R.Alert("safezone_removed", actor, zone.EntityId, what + ": " + reason);
        });

        // a new zone's settings the frame after: whoever made it (a station's generator) sets some of them right after making it
        private static void OnAdded(object sender, EventArgs args)
        {
            if (!(sender is MySafeZone zone) || zone.SafeZoneBlockId != 0) return;
            Sandbox.MySandboxGame.Static?.Invoke(() =>
            {
                if (!zone.MarkedForClose && !Seen.ContainsKey(zone.EntityId)) Seen[zone.EntityId] = Settings(zone);
            }, "Watcher safe zone seen");
        }

        private static void OnUpdated(MySafeZone zone) => Recorder.Safe("a safe zone changed", () =>
        {
            if (zone == null || zone.SafeZoneBlockId != 0) return;
            var now = Settings(zone);
            Seen.TryGetValue(zone.EntityId, out var before);
            Seen[zone.EntityId] = now;
            var changes = Changes(before, now);
            if (changes == null) return;
            var frames = new StackTrace(1, false).GetFrames()?.Select(Frame).Where(f => f != null && !Inner(f)).ToArray() ?? new string[0];
            var context = MyEventContext.Current;
            long actor = 0;
            string who = null;
            if (!context.IsLocallyInvoked && context.Sender.Value != 0)
            {
                actor = Identities.OfSteam(context.Sender.Value);
                who = Identities.NameOf(actor) + " (" + context.Sender.Value + ")";
            }
            var how = Through(frames, who);
            var at = zone.PositionComp.GetPosition();
            var what = $"\"{zone.DisplayName}\" {zone.EntityId} at {at.X:0} {at.Y:0} {at.Z:0}";
            R.Event("safezone_changed", actor, zone.EntityId, at, zone.Radius, detail: what + ": " + changes + "; " + how);
            Log.Warn("SentisWatcher: admin safe zone changed " + what + ": " + changes + "; " + how + Environment.NewLine + "  stack: " + string.Join(" <- ", frames.Take(12)));
            R.Alert("safezone_changed", actor, zone.EntityId, what + ": " + changes + "; " + how);
        });

        /// <summary>Through what a zone's settings were changed, from the stack (innermost first).</summary>
        public static string Through(string[] frames, string who)
        {
            // the event's call site (a nested class "+<handler><>...") stays on the stack: the handler itself, one line, may be inlined into it.
            // The players' first: their names begin as the admin menu's.
            bool Named(string handler) => frames.Any(f => f.EndsWith("." + handler) || f.Contains("+" + handler + "<") || f.Contains("+" + handler + "_003C"));
            if (Named("UpdateSafeZone_ImplementationPlayer") || Named("UpdateSafeZonePlayer_Implementation") || Named("UpdateSafeZoneRadius_ImplementationPlayer"))
                return "a player's safe zone block's settings" + (who != null ? " by " + who : "") + " (a zone of no block: SentisAnticheat should have refused it)";
            if (Named("UpdateSafeZone_Implementation")) return "the admin menu" + (who != null ? " by " + who : "");
            var outside = frames.Where(f => !f.StartsWith("Sandbox.") && !f.StartsWith("VRage.") && !f.StartsWith("System.") && !f.StartsWith("SpaceEngineers.") &&
                                            !f.StartsWith(".") && !f.StartsWith("Torch.VRageGame") && !f.StartsWith("SentisWatcher.Recording.LoadSampler")).Take(3).ToList();
            return "by " + (outside.Count > 0 ? string.Join(" <- ", outside) : string.Join(" <- ", frames.Take(4)));
        }

        /// <summary>A zone's settings, by name, as text.</summary>
        private static Dictionary<string, string> Settings(MySafeZone zone)
        {
            var c = CultureInfo.InvariantCulture;
            var at = zone.PositionComp.GetPosition();
            return new Dictionary<string, string>
            {
                ["on"] = zone.Enabled ? "on" : "off",
                ["shape"] = zone.Shape.ToString(),
                ["radius"] = zone.Radius.ToString("0.#", c),
                ["size"] = zone.Size.X.ToString("0.#", c) + "x" + zone.Size.Y.ToString("0.#", c) + "x" + zone.Size.Z.ToString("0.#", c),
                ["allowed"] = zone.AllowedActions.ToString(),
                ["players"] = zone.AccessTypePlayers + " " + zone.Players.Count,
                ["factions"] = zone.AccessTypeFactions + " " + zone.Factions.Count,
                ["grids"] = zone.AccessTypeGrids + " " + zone.Entities.Count,
                ["floating objects"] = zone.AccessTypeFloatingObjects.ToString(),
                ["place"] = at.X.ToString("0", c) + " " + at.Y.ToString("0", c) + " " + at.Z.ToString("0", c),
            };
        }

        /// <summary>"radius 150 -> 10, on on -> off"; null when nothing changed; all of them when nothing was seen before.</summary>
        public static string Changes(IDictionary<string, string> before, IDictionary<string, string> now)
        {
            if (before == null) return "now " + string.Join(", ", now.Select(p => p.Key + " " + p.Value)) + " (not seen before)";
            var changed = now.Where(p => !before.TryGetValue(p.Key, out var old) || old != p.Value)
                .Select(p => p.Key + " " + (before.TryGetValue(p.Key, out var old) ? old : "?") + " -> " + p.Value).ToList();
            return changed.Count == 0 ? null : string.Join(", ", changed);
        }

        /// <summary>Every economy station standing in the world without its safe zone: told at once, at the load.</summary>
        public static void CheckStations()
        {
            var factions = MySession.Static?.Factions;
            if (factions == null) return;
            var open = 0;
            foreach (var faction in factions.Factions.Values)
            foreach (var station in faction.Stations.OfType<MyFactionStation>())
            {
                if (station.StationEntityId == 0 || !MyEntities.TryGetEntityById(station.StationEntityId, out MyCubeGrid grid) || grid.MarkedForClose) continue;
                if (station.SafeZoneEntityId != 0 && MyEntities.TryGetEntityById(station.SafeZoneEntityId, out MySafeZone zone) && !zone.MarkedForClose) continue;
                open++;
                var at = grid.PositionComp.GetPosition();
                var text = $"the station \"{grid.DisplayName}\" {grid.EntityId} ({faction.Tag}, {station.Type}) has no safe zone: " +
                           (station.SafeZoneEntityId == 0 ? "none noted" : "its safe zone " + station.SafeZoneEntityId + " is not in the world") +
                           $"; at {at.X:0} {at.Y:0} {at.Z:0}. The game does not make it again while the station stands";
                Log.Warn("SentisWatcher: " + text);
                R.Event("station_no_safezone", 0, grid.EntityId, at, detail: text);
                R.Alert("station_no_safezone", 0, grid.EntityId, text);
            }
            if (open == 0 && SentisWatcherPlugin.Config?.DiagnosticLogs == true) Log.Info("SentisWatcher: every economy station in the world has its safe zone");
        }

        private static string Frame(StackFrame frame)
        {
            var method = frame.GetMethod();
            if (method == null) return null;
            return (method.DeclaringType?.FullName ?? "") + "." + method.Name;
        }

        // the closing itself and this handler: not the reason
        private static bool Inner(string frame) =>
            frame.StartsWith("SentisWatcher.Recording.SafeZoneWatch") || frame.StartsWith("SentisWatcher.Recording.Recorder") ||
            frame.Contains("MySessionComponentSafeZones.RemoveSafeZone") || frame.Contains("MySafeZone.Closing") ||
            frame == "VRage.Game.Entity.MyEntity.Close" || frame.Contains("MyEntityClose_") || frame.StartsWith("System.EventHandler") ||
            frame.Contains("<OnRemoved>");
    }
}

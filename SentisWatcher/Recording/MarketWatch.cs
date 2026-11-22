using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRageMath;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// VirtualGarage's trade console (<c>VirtualGarage.Market.ShipMarket</c>): who put up what for how much on which
    /// console, who bought it, and what came off sale and why. The market writes its listings after every change
    /// (<c>ShipMarket.Save</c>); the listings are compared with the ones it wrote before:
    /// <list type="bullet">
    /// <item><c>market_listed</c>: a new listing - the seller, the ship, the price, the console and where it stands;</item>
    /// <item><c>market_bought</c>: a listing bought - the buyer, the seller, the price, the console; the seller's garage
    /// entry is tied to the buyer's (<see cref="GarageWatch.Relink"/>: the same ship, followed on);</item>
    /// <item><c>market_unlisted</c>: a listing gone unbought - taken off by the seller (<c>Cancel</c>) or given back by the
    /// market (<c>GiveBack</c>: the console moved, made a ship, gone), with the reason;</item>
    /// <item><c>market_undone</c>: a purchase the world had not saved when the server stopped, undone as the world loads
    /// (the buyer has his money again, the ship is the seller's).</item>
    /// </list>
    /// A purchase the world saved leaves the list without an event. The money is in the ledger as the market moves it.
    /// </summary>
    public static class MarketWatch
    {
        private static readonly Logger Log = LogManager.GetLogger("SentisWatcher.Market");

        private static FieldInfo _listings;
        private static FieldInfo _shipyard, _sellerSteam, _sellerIdentity, _sellerName, _file, _ship, _price, _pending, _buyerSteam, _buyerFile, _x, _y, _z;
        private static readonly object Gate = new object();
        /// <summary>The listings the market wrote last, each with whether it was bought; null before the first look.</summary>
        private static Dictionary<object, bool> _known;
        [ThreadStatic] private static string _why;
        private static volatile bool _worldLoading;

        /// <summary>How many of each were told, and the last one's text: what a test asks.</summary>
        public static long Listings, Purchases, Unlistings, Undoings;
        public static string LastDetail;

        internal static void Patch(Assembly assembly, PatchContext c)
        {
            const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var market = assembly.GetType("VirtualGarage.Market.ShipMarket") ?? throw new TypeLoadException("VirtualGarage.Market.ShipMarket");
            var listing = assembly.GetType("VirtualGarage.Market.Listing") ?? throw new TypeLoadException("VirtualGarage.Market.Listing");
            _listings = market.GetField("_listings", statics) ?? throw new MissingFieldException("ShipMarket._listings");
            FieldInfo F(string name) => listing.GetField(name, BindingFlags.Instance | BindingFlags.Public) ?? throw new MissingFieldException("Listing." + name);
            _shipyard = F("ShipyardId"); _sellerSteam = F("SellerSteamId"); _sellerIdentity = F("SellerIdentity"); _sellerName = F("SellerName");
            _file = F("File"); _ship = F("ShipName"); _price = F("Price"); _pending = F("Pending"); _buyerSteam = F("BuyerSteamId");
            _buyerFile = F("BuyerFile"); _x = F("ConsoleX"); _y = F("ConsoleY"); _z = F("ConsoleZ");
            MethodInfo Game(string name) => market.GetMethod(name, statics) ?? throw new MissingMethodException("ShipMarket." + name);
            MethodInfo Own(string name) => typeof(MarketWatch).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            c.GetPattern(Game("Save")).Suffixes.Add(Own(nameof(SaveSuffix)));
            c.GetPattern(Game("Load")).Suffixes.Add(Own(nameof(LoadSuffix)));
            var loaded = c.GetPattern(Game("OnWorldLoaded"));
            loaded.Prefixes.Add(Own(nameof(WorldLoadedPrefix)));
            loaded.Suffixes.Add(Own(nameof(WorldLoadedSuffix)));
            var cancel = c.GetPattern(Game("Cancel"));
            cancel.Prefixes.Add(Own(nameof(CancelPrefix)));
            cancel.Suffixes.Add(Own(nameof(ReasonDone)));
            var giveBack = c.GetPattern(Game("GiveBack"));
            giveBack.Prefixes.Add(Own(nameof(GiveBackPrefix)));
            giveBack.Suffixes.Add(Own(nameof(ReasonDone)));
            // what the market holds now (it may have read its listings before the watcher came): the first change is told
            lock (Gate) _known = Snapshot(_listings.GetValue(null) as IList);
        }

        private static void CancelPrefix(long identity) => _why = "taken off by " + Identities.NameOf(identity) + " (" + identity + ")";
        private static void GiveBackPrefix(string why) => _why = "given back by the market: " + why;
        private static void ReasonDone() => _why = null;

        private static void WorldLoadedPrefix() => _worldLoading = true;

        private static void WorldLoadedSuffix()
        {
            _worldLoading = false;
            Recorder.Safe("market loaded", () => Look(record: false));
        }

        /// <summary>The listings as read from the disk: what is known before anything happens.</summary>
        private static void LoadSuffix(object __result) => Recorder.Safe("market read", () =>
        {
            lock (Gate) _known = Snapshot(__result as IList);
        });

        private static void SaveSuffix() => Recorder.Safe("market", () => Look(record: true));

        private static Dictionary<object, bool> Snapshot(IList listings)
        {
            var snapshot = new Dictionary<object, bool>();
            if (listings == null) return snapshot;
            foreach (var listing in listings)
                if (listing != null) snapshot[listing] = (bool)_pending.GetValue(listing);
            return snapshot;
        }

        private static void Look(bool record)
        {
            var now = Snapshot(_listings.GetValue(null) as IList);
            Dictionary<object, bool> before;
            lock (Gate)
            {
                before = _known;
                _known = now;
            }
            var recorder = Recorder.Current;
            if (before == null || !record || recorder == null) return;
            foreach (var pair in now)
            {
                if (!before.TryGetValue(pair.Key, out var wasBought)) { if (!pair.Value) Listed(recorder, pair.Key); }
                else if (!wasBought && pair.Value) Bought(recorder, pair.Key);
            }
            foreach (var pair in before)
            {
                if (now.ContainsKey(pair.Key)) continue;
                if (!pair.Value) Unlisted(recorder, pair.Key, _why ?? "off sale");
                else if (_worldLoading) Undone(recorder, pair.Key);
                // bought and the world saved since: done, nothing to tell
            }
        }

        private static T Get<T>(FieldInfo field, object listing) => (T)field.GetValue(listing);

        private static Vector3D At(object listing) => new Vector3D(Get<double>(_x, listing), Get<double>(_y, listing), Get<double>(_z, listing));

        /// <summary>"console 123 on Station Name": the station only on the game thread (the market's other threads only finish purchases).</summary>
        private static string Console(object listing)
        {
            var id = Get<long>(_shipyard, listing);
            string station = null;
            try
            {
                if (Sandbox.MySandboxGame.Static?.UpdateThread == System.Threading.Thread.CurrentThread)
                    station = (MyEntities.GetEntityById(id) as MyCubeBlock)?.CubeGrid?.DisplayName;
            }
            catch (Exception) { }
            return "console " + id + (station == null ? "" : " on \"" + station + "\"");
        }

        private static string Seller(object listing) => Get<string>(_sellerName, listing) + " (" + Get<long>(_sellerIdentity, listing) + ")";

        private static void Tell(Recorder recorder, string kind, long actor, object listing, string detail)
        {
            LastDetail = detail;
            recorder.Event(kind, actor, Get<long>(_shipyard, listing), At(listing), amount: Get<long>(_price, listing), detail: detail);
        }

        private static void Listed(Recorder recorder, object listing)
        {
            var price = Get<long>(_price, listing);
            System.Threading.Interlocked.Increment(ref Listings);
            Tell(recorder, "market_listed", Get<long>(_sellerIdentity, listing), listing,
                $"\"{Get<string>(_ship, listing)}\" put up by {Seller(listing)} for {price:N0} SC on {Console(listing)} | {GarageWatch.Key(Get<string>(_file, listing))}");
        }

        private static void Bought(Recorder recorder, object listing)
        {
            var price = Get<long>(_price, listing);
            var buyerSteam = Get<ulong>(_buyerSteam, listing);
            var buyer = MySession.Static?.Players.TryGetIdentityId(buyerSteam) ?? 0;
            var from = GarageWatch.Key(Get<string>(_file, listing));
            var to = GarageWatch.Key(Get<string>(_buyerFile, listing) ?? "");
            System.Threading.Interlocked.Increment(ref Purchases);
            Tell(recorder, "market_bought", buyer, listing, $"\"{Get<string>(_ship, listing)}\" bought by {Identities.NameOf(buyer)} ({buyer}, {buyerSteam}) from {Seller(listing)} for {price:N0} SC on {Console(listing)} | {from} -> {to}");
            GarageWatch.Relink(from, to);
        }

        private static void Unlisted(Recorder recorder, object listing, string why)
        {
            System.Threading.Interlocked.Increment(ref Unlistings);
            Tell(recorder, "market_unlisted", Get<long>(_sellerIdentity, listing), listing, $"\"{Get<string>(_ship, listing)}\" of {Seller(listing)} off sale on {Console(listing)}: {why}");
        }

        private static void Undone(Recorder recorder, object listing)
        {
            var buyerSteam = Get<ulong>(_buyerSteam, listing);
            var buyer = MySession.Static?.Players.TryGetIdentityId(buyerSteam) ?? 0;
            System.Threading.Interlocked.Increment(ref Undoings);
            Tell(recorder, "market_undone", buyer, listing, $"\"{Get<string>(_ship, listing)}\" bought by {Identities.NameOf(buyer)} ({buyer}) from {Seller(listing)}: undone, the world was not saved after it");
            Log.Warn("SentisWatcher: a purchase on the trade console undone (the world was not saved after it): " + Get<string>(_ship, listing));
        }
    }
}

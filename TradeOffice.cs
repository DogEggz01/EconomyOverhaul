using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EconomyOverhaul
{
    // ---- Trade office (the user's request of 2026-11-06; TRADE_OFFICE_DRAFT.md: the user's answers in §3A and §3B, the design as built in §4) ----
    // A bought house turned into an office: a sign beside the door, the port's own office table with its paper (the office's scroll), and
    // the upgrades bought there. Every part hangs on the port's island at a fixed island-local pose (§3B "The office's spots", the poses of
    // run os-22), built while the player is near and gone when far, so nothing runs away from the island.
    public static partial class Rules
    {
        // Converting needs reputation 6 in the house's region at that moment (Q4); the office keeps working if it falls later.
        public const int OfficeLevel = 6;
        // 10% of the house's price (the user's Q31), in Gold, rounded up: 17 to 130.
        public static int OfficePrice(int housePrice) => Math.Max(1, (int)Math.Ceiling(housePrice * .1));
        // Moving the clerk (ClerkPlacer): Q/E held turn him at 60 degrees a second (the user, 2026-11-09: "this is too fast, 60 degree a second",
        // in place of Claude's 90); dir -1 left (Q), +1 right (E); the heading kept in [0, 360).
        public const float ClerkTurnRate = 60f;
        public static float ClerkTurn(float yaw, int dir, float seconds)
        {
            float y = (yaw + Math.Sign(dir) * ClerkTurnRate * Math.Max(0f, seconds)) % 360f;
            return y < 0f ? y + 360f : y;
        }
        // His turn for a heading: about the up axis only, as Quaternion.Euler(0, yaw, 0) (built by hand, so the host tests can read it). Saved as
        // the spot's four rotation floats (OfficeState.clerkSpot); a turn is never all four zero, which the spot reads as "never moved".
        public static Quaternion ClerkFacing(float yaw)
        {
            double h = yaw * Math.PI / 360.0;
            return new Quaternion(0f, (float)Math.Sin(h), 0f, (float)Math.Cos(h));
        }
        // The move's hint in the user's words (2026-11-09: "Aim at where clerk stand. Left click or R to place. Q/E to rotate. Right click or F to
        // cancel. Make this notification show for 5 sec."), naming the player's own keys (the game's bindings; null when one is unbound or a mouse
        // button: that part drops out). Shown ClerkMoveHintSeconds.
        public const float ClerkMoveHintSeconds = 5f;
        public static string ClerkMoveHint(string place, string cancel, string turnLeft, string turnRight) =>
            "Aim at where clerk stand. Left click" + (place != null ? " or " + place : "") + " to place. " +
            (turnLeft != null && turnRight != null ? turnLeft + "/" + turnRight + " to rotate. " : turnLeft != null ? turnLeft + " to rotate. " : turnRight != null ? turnRight + " to rotate. " : "") +
            "Right click" + (cancel != null ? " or " + cancel : "") + " to cancel.";
        // The upgrades' prices in Gold (Q31: treasury 100, fleet 200, unique items 500; the others as proposed).
        public const int TreasuryPrice = 100, ClerkPrice = 50, CartPrice = 100, CooperationPrice = 500, CrewPrice = 100, FleetPrice = 200, ItemsPrice = 500;
        // The clerk's salary: about 1 gold-equivalent a day (Q31), in the region's coin at the daily rate, at least 1 coin.
        public const double ClerkSalaryValue = 1 / FxRules.Gold;
        public static int ClerkSalary(double dailyRate) => Math.Max(1, (int)Math.Round(ClerkSalaryValue * dailyRate));
        // His wages add up for at most 30 days; unpaid for 30 days, he quits and the clerk must be bought again (the user, 2026-11-08: "up to 30
        // days, if after 30 days not paid, clerk quit and you need to re unlock the clerk").
        public const int ClerkOwedDays = 30;
        // The cooperation book's "a good the port doesn't trade" (sold from other sources, if it can be unsealed): not in stock now and not made
        // there (production over 0, the game's own test in IslandMarket.EconCycle). The user (the deep review of 2026-11-09, question 3c): "judge by
        // what port makes": a good the port makes but has sold out is not sold from other sources; it waits for the port's own stock.
        public static bool OfficeAbsent(bool inStock, float production) => !inStock && !(production > 0f);
        // A fleet ship's whole-life tally (OfficeState.fleetPorts, fleetGoods): "index:count" pairs spaced. Tally adds to an index's count; Most is
        // the index with the largest count (the first of equals in the tally's order; -1 and 0 when empty); an unreadable pair is skipped.
        public static string Tally(string tally, int index, int add)
        {
            var d = ReadTally(tally); d[index] = (d.TryGetValue(index, out var c) ? c : 0) + add;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return string.Join(" ", d.Select(kv => kv.Key.ToString(ic) + ":" + kv.Value.ToString(ic)).ToArray());
        }
        public static Dictionary<int, int> ReadTally(string tally)
        {
            var d = new Dictionary<int, int>(); if (string.IsNullOrEmpty(tally)) return d;
            var ic = System.Globalization.CultureInfo.InvariantCulture; var style = System.Globalization.NumberStyles.Integer;
            foreach (var pair in tally.Split(' '))
            {
                int k = pair.IndexOf(':');
                if (k > 0 && int.TryParse(pair.Substring(0, k), style, ic, out int key) && int.TryParse(pair.Substring(k + 1), style, ic, out int n)) d[key] = (d.TryGetValue(key, out var c) ? c : 0) + n;
            }
            return d;
        }
        public static int Most(string tally, out int count)
        {
            count = 0; int best = -1;
            foreach (var kv in ReadTally(tally)) if (kv.Value > count) { count = kv.Value; best = kv.Key; }
            return best;
        }
        // The office's box: the daily rate, no fee, the live rates untouched (Q20). Sold coins are worth x / smooth[sold] in value, bought
        // coins value x smooth[bought]; the live rates come back unchanged, so the booth's own confirm writes nothing new.
        public static FxExchange FlatExchange(double[] smooth, double[] live, int sold, int bought, int amount, bool requestBuy)
        {
            var q = new FxExchange { Error = "Choose currencies and an amount" };
            if (smooth == null || live == null || smooth.Length != 4 || live.Length != 4 || sold < 0 || sold > 3 || bought < 0 || bought > 3 || sold == bought || amount <= 0) return q;
            double a = smooth[sold], b = smooth[bought];
            if (!(a > 0) || !(b > 0)) { q.Error = "Currency runs out!"; return q; }
            double x = requestBuy ? Math.Ceiling(amount * a / b - 1e-9) : amount;
            if (x > int.MaxValue) { q.Error = "Amount exceeds wallet limit"; return q; }
            double gross = x * b / a, receipt = Math.Floor(gross + 1e-9);
            if (receipt < 1) { q.Error = "Amount too small"; return q; }
            if (receipt > int.MaxValue) { q.Error = "Amount exceeds wallet limit"; return q; }
            q.Valid = true; q.Error = null; q.Sold = (int)x; q.Received = (int)receipt; q.GrossReceived = gross; q.Fee = 0; q.SellRate = live[sold]; q.BuyRate = live[bought];
            return q;
        }
    }

    // The offices' save (§3B "The save and the freeze": arrays, not lists of a new class; an office's upgrades one int of bits; a treasury
    // int[]; nothing that only grows). Optional on SaveState: an older save has none, read back as no office.
    // Most of its fields have no [OptionalField]: no released save holds an office yet (only test saves do), and the binary reader leaves a
    // field missing from the data null or 0 (measured on the host: EconomyOverhaul.Tests' BinarySave.cs), which the code reads as noted at
    // each field. A field added after the office's first release needs [OptionalField] (the save rule, SaveState's comment); BinarySave.cs
    // records each field's flag.
    [Serializable] public sealed class OfficeState
    {
        public int version = 1;
        // Per house 201-206 (index house - 201): the upgrades bought, as OfficeUpgrade bits.
        public int[] upgrades = new int[6];
        // Per region (0-2) and coin (0-3), region * 4 + coin: the treasury's coins, and what the region's fleet earned in all.
        public int[] treasury = new int[12];
        public int[] earned = new int[12];
        // Per house: the clerk's unpaid wages, in the region's coin; the last day his salary was settled.
        public int[] wages = new int[6];
        public int[] salaryDay = new int[6];
        // Per house: the day his wages first went unpaid, plus 1 (0: none owed); after 30 days of it he quits (the user, 2026-11-08). Null in
        // a test save made before it (no [OptionalField]: see the class's comment), read as none owed.
        public int[] owedSince;
        // Per house: the clerk's outfit (the game's NPC outfits, TraderHulls.Outfits), plus 1; 0 until one is picked.
        public int[] clerkOutfit = new int[6];
        // Per house, 7 floats: where the player put the clerk (Q18), his island-local position and turn (a quaternion); all 0 until moved.
        public float[] clerkSpot;
        // Per house: his look from Player Model's screen (Q16, "only B"), as that mod writes it; null until edited. And the player's own look
        // while the screen edits the clerk's (put back at the next load if the game stopped in between).
        public string[] clerkLook;
        public string playerLook;
        // Per house: the crew candidates hired (bits 0-4) in the 7-day period crewPeriod (§4.8: the candidates come from a seed of the office and
        // the period, so only these bits are saved).
        public int[] crewHired;
        public int[] crewPeriod;
        // Per house: the crates not on the local market bought at its book on day coopDay (at most 10 a day, Q22).
        public int[] coopDay;
        public int[] coopCount;
        // The trade fleet (§4.5), per ship in the order of SaveState.playerFleet: its office's house, hull, kind (OfficeFleet.Kinds), its name
        // (Shipwright's), its rig as trader plan text (null: the stock rig), the trip under way's cost and its last trip's share (value units),
        // its lifetime share, 30 day sums for the estimate (30 a ship) and the day of the newest, the day it joined, whether it waits for its fee.
        // Its kind is -1 (OfficeFleet.NotAssigned) until the player sets its route (the user, 2026-11-08: "newly add trader shouldn't be auto assign
        // route, it should be assign by player then start trading").
        public int[] fleetHouse, fleetHull, fleetKind, fleetCost, fleetLast, fleetLife, fleetSums, fleetSumDay, fleetJoined, fleetHeld;
        // Each ship's day its route was first set (the estimate counts the ship's days from it, not the days it waited for a route); 0 before, and
        // in a test save made before routes waited for the player (no [OptionalField]: see the class's comment).
        public int[] fleetStart;
        // Each ship's last trips with a sale (OfficeFleet.TripLog a ship, newest first; the user, 2026-11-08: "the bottom show the trade history (where
        // he sold, how much profit earned that trip(the share that gave to you)"): the day, the port the ship sold at plus 1 (0: none), the coin and
        // the signed coins of its share.
        public int[] fleetTripDay, fleetTripPort, fleetTripCoin, fleetTripCoins;
        // Beside them, each logged trip's cargo of the most value (the user, 2026-11-08 evening: "At the history between port and profit, add traded
        // cargo also, show cargo with most value and it's quantity"): its good plus 1 (0: none) and its crates of it.
        [System.Runtime.Serialization.OptionalField] public int[] fleetTripGood, fleetTripCrates;
        // Each ship's whole life in the fleet (the user, the same evening: "most visited port, most sold goods and biggest profit trip(show destination
        // and trip profit)"; asked whether the whole life or the trips kept: "Whole life"): its arrivals by port and its crates sold by good, as
        // "index:count" pairs spaced; its biggest trip by the share's value: the port plus 1 (0: none yet), the coin, the signed coins, the value units.
        [System.Runtime.Serialization.OptionalField] public string[] fleetPorts, fleetGoods;
        [System.Runtime.Serialization.OptionalField] public int[] fleetBestPort, fleetBestCoin, fleetBestCoins, fleetBestValue;
        // Each ship's last trip in the coin it was paid in (the user, 2026-11-08: "Last trip show the last local currency it earn"): the coin and
        // the signed coins of its share.
        public int[] fleetLastCoin, fleetLastCoins;
        public string[] fleetName, fleetRig;
        // The next fleet ship's id (§4.5: never reused, as a reused id leaves a stale departure); 0 before the first.
        public int fleetNextId;
        // The treasury's records (OfficeRecords, the Treasury book's right page), newest last: the day, the office (house), what (RecordKind), the
        // coin and the signed amount. Null in a test save made before them (no [OptionalField]: see the class's comment).
        public int[] recDay, recHouse, recKind, recCoin, recAmount;
    }

    [Flags] internal enum OfficeUpgrade { None = 0, Office = 1, Treasury = 2, Clerk = 4, Cart = 8, Cooperation = 16, Crew = 32, Fleet = 64, Items = 128 }

    internal static class Office
    {
        internal static readonly int[] Houses = { 201, 202, 203, 204, 205, 206 };
        internal const string TransportGuid = "DogEggz.ShipwrightTransport";
        // Only the trade fleet needs Shipwright and Transport (its ships join from that mod's storage); the office and its other upgrades don't
        // (the user, 2026-11-08: "make only trader fleet locked behind shipwright mod, other feature don't require it"). A fleet made earlier keeps
        // sailing without it (Q2).
        internal static bool Enabled => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(TransportGuid);
        internal static bool CrewInstalled => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.zorkinian.virtualcrew");
        internal static int Slot(int house) => house >= 201 && house <= 206 ? house - 201 : -1;
        internal static OfficeState State
        {
            get
            {
                var s = World.State.office;
                if (s == null) World.State.office = s = new OfficeState();
                if (s.upgrades == null || s.upgrades.Length != 6) s.upgrades = new int[6];
                if (s.treasury == null || s.treasury.Length != 12) s.treasury = new int[12];
                if (s.earned == null || s.earned.Length != 12) s.earned = new int[12];
                if (s.wages == null || s.wages.Length != 6) s.wages = new int[6];
                if (s.salaryDay == null || s.salaryDay.Length != 6) s.salaryDay = new int[6];
                if (s.clerkOutfit == null || s.clerkOutfit.Length != 6) s.clerkOutfit = new int[6];
                return s;
            }
        }
        // Read without making the state (the save keeps none until an office exists).
        internal static int Bits(int house) { var s = World.State?.office; int i = Slot(house); return s?.upgrades != null && i >= 0 && i < s.upgrades.Length ? s.upgrades[i] : 0; }
        internal static bool Has(int house, OfficeUpgrade u) => (Bits(house) & (int)u) == (int)u && u != OfficeUpgrade.None;
        internal static bool IsOffice(int house) => Has(house, OfficeUpgrade.Office);
        internal static int Region(int house) => Pledges.HouseRegion(house);
        // The coin of the house's region: Lions, Dragons, Crowns.
        internal static int Coin(int house) => Math.Max(0, Region(house));
        internal static bool Pledged(int house) => World.State.bottomry.Any(l => l.pledge == house);
        // The house's port (the nearest to it) and the port's island root (its ancestor straight under the shifting world), kept once found.
        private static readonly Dictionary<int, Port> ports = new Dictionary<int, Port>();
        internal static Port PortOf(int house)
        {
            if (ports.TryGetValue(house, out var p) && p) return p;
            var b = Pledges.Find(house); if (!b || Port.ports == null) return null;
            // Across the water only: a far island sinks with its distance from the player (IslandHorizon), its port kilometres under the house
            // that doesn't (ocart-1: from Fort Aestrin, Firefly Grotto's house found Fort Aestrin's port).
            p = Port.ports.Where(x => x).OrderBy(x => Flat(x.transform.position - b.transform.position)).FirstOrDefault();
            if (p) ports[house] = p;
            return p;
        }
        internal static float Flat(Vector3 v) => v.x * v.x + v.z * v.z;
        internal static Transform IslandRoot(int house)
        {
            var t = PortOf(house) ? PortOf(house).transform : null;
            while (t && t.parent && t.parent.name != "_shifting world") t = t.parent;
            return t && t.parent ? t : null;
        }
        // Live: the office's house is the player's (not taken by a lender) and the mod runs.
        internal static bool Live(int house) { var b = HouseBoat(house); return World.Ready && b && Pledges.Owned(b) && IsOffice(house); }
        // Pledges.Find(house) for the six houses, kept for the array Pledges.All() returned: Live runs every frame for each fleet ship in port
        // (Fleet.Tick -> OfficeFleet.Held -> Working), and each Find is a closure, a LINQ walk and a GetComponent a boat (2-5 µs, about 60 bytes).
        // All() keeps its array while the same boats stand with the same save numbers, and makes a new one when a boat comes or goes or a
        // number changes; within one array the answer can't change, so a house is searched again for each new array. Ownership
        // (Pledges.Owned) is still read each time. Any other number: Find.
        private static PurchasableBoat[] housesFrom; private static readonly PurchasableBoat[] houseBoats = new PurchasableBoat[6]; private static readonly bool[] houseLooked = new bool[6];
        private static PurchasableBoat HouseBoat(int house)
        {
            int i = Slot(house); if (i < 0) return Pledges.Find(house);
            var all = Pledges.All();
            if (!ReferenceEquals(all, housesFrom)) { housesFrom = all; Array.Clear(houseLooked, 0, houseLooked.Length); }
            if (!houseLooked[i]) { houseBoats[i] = Pledges.Find(house); houseLooked[i] = true; }
            return houseBoats[i];
        }

        internal sealed class UpgradeInfo { internal OfficeUpgrade Bit, Needs; internal string Name, Text; internal int Price; internal bool Built; }
        // In the scroll's order, top to bottom (the user, 2026-11-08: "Treasury / clerk / Cart Contrct / Trade fleet / Local office cooperation /
        // Unique item purchase / Office wharf(after we build it) / Crew recruitment, only draw this option when Crew mod is installed"). The treasury
        // comes first so the clerk's salary is paid from it; everything after the clerk needs him; unique items need the cooperation. The wharf
        // waits for its own build (choice 9: "add later is fine after we build wharf"). Texts in the user's words (2026-11-08), their spelling
        // corrected at their word ("correct the spelling for me"; "unsealable" kept: the design's word for crates that can be opened), wrapped to the
        // scroll when shown; the clerk's {0} {1} his wage and the region's coin.
        internal static readonly UpgradeInfo[] Upgrades =
        {
            new UpgradeInfo { Bit = OfficeUpgrade.Treasury, Needs = OfficeUpgrade.Office, Name = "Treasury", Price = Rules.TreasuryPrice, Built = true,
                Text = "Unlock a currency exchange chest on the table: you can exchange here without an FX fee.\n\nThis is the base of the operation: everything the trade office earns or spends will be taken from here. You can freely take and store gold here. It's shared within the region." },
            new UpgradeInfo { Bit = OfficeUpgrade.Clerk, Needs = OfficeUpgrade.Treasury, Name = "Clerk", Price = Rules.ClerkPrice, Built = true,
                Text = "A clerk to help you manage the office. You need to hire a clerk to unlock further upgrades.\n\n(Salary: {0} {1} a day)" },
            new UpgradeInfo { Bit = OfficeUpgrade.Cart, Needs = OfficeUpgrade.Clerk, Name = "Cart contract", Price = Rules.CartPrice, Built = true,
                Text = "Cart transport at this port becomes free. A cart service will be added beside the office entrance." },
            new UpgradeInfo { Bit = OfficeUpgrade.Fleet, Needs = OfficeUpgrade.Clerk, Name = "Trade fleet", Price = Rules.FleetPrice, Built = true,
                Text = "Start the trade fleet to earn gold for you. Add ships to the fleet in Shipwright's storage. The trader ship will look the same as how you rigged it. All the maintenance fees, trade losses and profits go into the treasury. If the treasury is empty, the traders will stop operating. Unlocks the trader summary tab in the Treasury UI. Each office supports 3 traders." },
            new UpgradeInfo { Bit = OfficeUpgrade.Cooperation, Needs = OfficeUpgrade.Clerk, Name = "Local office cooperation", Price = Rules.CooperationPrice, Built = true,
                Text = "The office's own trade book, connected to the local market. You can buy goods here with 10% off. It also provides all the unsealable trade goods, even if the local port doesn't produce them. They will be at a higher price, with a limit of 10 crates per day." },
            new UpgradeInfo { Bit = OfficeUpgrade.Items, Needs = OfficeUpgrade.Cooperation, Name = "Unique item purchase", Price = Rules.ItemsPrice, Built = true,
                Text = "Buy unique items, regional variant items and modded items sold at other ports, at a 10% higher price." },
            new UpgradeInfo { Bit = OfficeUpgrade.Crew, Needs = OfficeUpgrade.Clerk, Name = "Crew recruitment", Price = Rules.CrewPrice, Built = true,
                Text = "Requires the Virtual Crew mod. A separate pool to recruit crew from." },
        };
        internal static UpgradeInfo Info(OfficeUpgrade u) => Upgrades.First(x => x.Bit == u);
        // The upgrades the scroll lists: crew recruitment only with Virtual Crew loaded (the user: "only draw this option when Crew mod is installed").
        internal static UpgradeInfo[] Listed => CrewInstalled ? Upgrades : Upgrades.Where(u => u.Bit != OfficeUpgrade.Crew).ToArray();
        // An upgrade at work: bought and the office live; those that need the clerk only while he works there (the user, 2026-11-08, when he
        // quits: "Debt gone, upgrades paused": they stay bought and work again once a clerk is bought).
        internal static bool Working(int house, OfficeUpgrade u) => Live(house) && Has(house, u) && (u == OfficeUpgrade.Office || u == OfficeUpgrade.Treasury || u == OfficeUpgrade.Clerk || Has(house, OfficeUpgrade.Clerk));
        // Why the house can't become an office now (null: it can).
        internal static string ConvertRefusal(int house)
        {
            var b = Pledges.Find(house);
            if (!World.Ready || !b) return "Not available";
            if (!Pledges.Owned(b)) return "Buy the house first";
            if (IsOffice(house)) return "Already an office";
            if (Pledged(house)) return "The house is pledged";
            int region = Region(house);
            if (Reputations.Level(region) < Rules.OfficeLevel) return "Needs " + BookUI.RegionName(region) + " reputation " + Rules.OfficeLevel;
            if (PlayerGold.currency[3] < Rules.OfficePrice(b.price)) return "Not enough gold";
            return null;
        }
        internal static bool Convert(int house)
        {
            var refusal = ConvertRefusal(house); if (refusal != null) { Loans.Notify(refusal); return false; }
            var b = Pledges.Find(house); int price = Rules.OfficePrice(b.price);
            PlayerGold.currency[3] -= price;
            if (DayLogs.instance) DayLogs.instance.dayLogs[3].LogTransaction(-price, TransactionCategory.other);
            State.upgrades[Slot(house)] |= (int)OfficeUpgrade.Office;
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayGoldSound();
            Loans.Notify("The " + Pledges.Name(house) + " is now a trade office");
            Plugin.Log.LogInfo("Trade office: " + Pledges.Name(house) + " converted for " + price + " gold.");
            OfficeSites.Refresh(house);
            return true;
        }
        // Why an upgrade can't be bought here now (null: it can).
        internal static string UpgradeRefusal(int house, OfficeUpgrade u)
        {
            var info = Info(u);
            if (!Live(house)) return "Not available";
            if (Has(house, u)) return "Bought";
            if (!info.Built) return "Not ready in this build";
            if (u == OfficeUpgrade.Fleet && !Enabled) return "Needs Shipwright and Transport";
            if (!Has(house, info.Needs)) return "Needs " + Info(info.Needs == OfficeUpgrade.Office ? OfficeUpgrade.Treasury : info.Needs).Name.ToLowerInvariant().Insert(0, "the ");
            if (u == OfficeUpgrade.Crew && !CrewInstalled) return "Needs the Virtual Crew mod";
            if (Wages(house) > 0) return "Pay the clerk's wages first";
            if (PlayerGold.currency[3] < info.Price) return "Not enough gold";
            return null;
        }
        internal static bool Buy(int house, OfficeUpgrade u)
        {
            var refusal = UpgradeRefusal(house, u); if (refusal != null) { Loans.Notify(refusal); return false; }
            var info = Info(u);
            PlayerGold.currency[3] -= info.Price;
            if (DayLogs.instance) DayLogs.instance.dayLogs[3].LogTransaction(-info.Price, TransactionCategory.other);
            State.upgrades[Slot(house)] |= (int)u;
            if (u == OfficeUpgrade.Clerk) State.salaryDay[Slot(house)] = GameState.day;
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayGoldSound();
            Loans.Notify(info.Name + " bought");
            Plugin.Log.LogInfo("Trade office: " + info.Name + " bought at the " + Pledges.Name(house) + " for " + info.Price + " gold.");
            OfficeSites.Refresh(house);
            return true;
        }

        // ---- The treasury: one per region, its coins shared by the region's offices (the user's text) ----
        internal static int Treasury(int region, int coin) { var s = World.State?.office; return s?.treasury != null && region >= 0 && region < 3 && coin >= 0 && coin < 4 ? s.treasury[region * 4 + coin] : 0; }
        // Coins from the wallet into the treasury of the office's region, or back, recorded under the office; null when done, else why not.
        internal static string Move(int house, int coin, int amount, bool deposit)
        {
            if (!World.Ready || World.SaveBlocked) return "Not available";
            int region = Region(house);
            if (region < 0 || region > 2 || coin < 0 || coin > 3) return "Choose a coin";
            if (amount <= 0) return "Choose an amount";
            var s = State; int i = region * 4 + coin;
            if (deposit)
            {
                if (PlayerGold.currency[coin] < amount) return "Not enough money";
                if ((long)s.treasury[i] + amount > int.MaxValue) return "The treasury is full";
                PlayerGold.currency[coin] -= amount; s.treasury[i] += amount;
                if (DayLogs.instance) DayLogs.instance.dayLogs[coin].LogTransaction(-amount, TransactionCategory.other);
                OfficeRecords.Add(house, RecordKind.Put, coin, amount);
            }
            else
            {
                if (s.treasury[i] < amount) return "Not enough in the treasury";
                if ((long)PlayerGold.currency[coin] + amount > int.MaxValue) return "Amount exceeds wallet limit";
                s.treasury[i] -= amount; PlayerGold.currency[coin] += amount;
                if (DayLogs.instance) DayLogs.instance.dayLogs[coin].LogTransaction(amount, TransactionCategory.other);
                OfficeRecords.Add(house, RecordKind.Take, coin, -amount);
            }
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayGoldSound();
            return null;
        }

        // ---- The clerk's salary (Q17): due at midnight from the region's treasury; what it can't pay adds up as wages owed ----
        internal static int Salary(int house) => Rules.ClerkSalary(Fx.State.smooth[Coin(house)]);
        internal static int Wages(int house) { var s = World.State?.office; int i = Slot(house); return s?.wages != null && i >= 0 ? s.wages[i] : 0; }
        internal static void Midnight(int day)
        {
            var s = World.State?.office; if (s == null) return;
            OfficeFleet.Midnight(day);
            foreach (int house in Houses)
            {
                int i = Slot(house); if (!Has(house, OfficeUpgrade.Clerk)) continue;
                // A seized office pays no salary (Q5); the days it was taken aren't owed later.
                if (!Live(house)) { if (s.owedSince != null && i < s.owedSince.Length && s.owedSince[i] > 0) s.owedSince[i] += Math.Max(0, day - s.salaryDay[i]); s.salaryDay[i] = day; continue; }
                int days = Mathf.Clamp(day - s.salaryDay[i], 0, Rules.ClerkOwedDays); s.salaryDay[i] = day;
                if (days == 0) continue;
                long due = (long)Salary(house) * days + s.wages[i]; int region = Region(house), coin = Coin(house), t = region * 4 + coin;
                long paid = Math.Min(due, s.treasury[t]);
                s.treasury[t] -= (int)paid; s.wages[i] = (int)Math.Min(int.MaxValue, due - paid);
                OfficeRecords.Add(house, RecordKind.Wages, coin, -paid);
                if (s.owedSince == null || s.owedSince.Length != 6) s.owedSince = new int[6];
                if (s.wages[i] <= 0) { s.owedSince[i] = 0; continue; }
                if (s.owedSince[i] == 0) s.owedSince[i] = day + 1;
                Plugin.Log.LogInfo("Trade office: the " + Pledges.Name(house) + "'s clerk is owed " + s.wages[i] + " " + PlayerGold.GetCurrencyName(coin) + " (the treasury held too little).");
                if (OwedDays(house, day) >= Rules.ClerkOwedDays) Quit(house);
            }
        }
        // Days his wages have gone unpaid by a day (0: none owed).
        internal static int OwedDays(int house) => OwedDays(house, GameState.day);
        internal static int OwedDays(int house, int day) { var s = World.State?.office; int i = Slot(house); return s?.owedSince != null && i >= 0 && i < s.owedSince.Length && s.owedSince[i] > 0 ? Math.Max(0, day - (s.owedSince[i] - 1)) : 0; }
        // He quits (unpaid for 30 days): his debt goes with him, the clerk is no longer bought, and the upgrades after him pause until a clerk is
        // bought again (Working). His place stays; his look doesn't: the next clerk gets an outfit picked anew, and Player Model's look is cleared
        // so the player can design him again (the user, 2026-11-08: "reroll the clerk appearance, If player model mod installed, let player
        // redesign it").
        internal static void Quit(int house)
        {
            var s = State; int i = Slot(house);
            s.upgrades[i] &= ~(int)OfficeUpgrade.Clerk; s.wages[i] = 0; s.salaryDay[i] = 0; if (s.owedSince != null) s.owedSince[i] = 0;
            s.clerkOutfit[i] = 0; if (s.clerkLook != null && i < s.clerkLook.Length) s.clerkLook[i] = null;
            Loans.Notify("The " + Pledges.Name(house) + "'s clerk quit: his wages went unpaid for " + Rules.ClerkOwedDays + " days");
            Plugin.Log.LogInfo("Trade office: the " + Pledges.Name(house) + "'s clerk quit after " + Rules.ClerkOwedDays + " days unpaid; the upgrades after him pause until a clerk is bought.");
            OfficeSites.Refresh(house);
        }
        // At the office's scroll: the wages owed, from the treasury first, the rest from the wallet in the region's coin.
        internal static string PayWages(int house)
        {
            var s = State; int i = Slot(house), owed = s.wages[i]; if (owed <= 0) return null;
            int coin = Coin(house), t = Region(house) * 4 + coin;
            int fromTreasury = Math.Min(owed, s.treasury[t]), fromWallet = owed - fromTreasury;
            if (PlayerGold.currency[coin] < fromWallet) return "Not enough " + PlayerGold.GetCurrencyName(coin) + " (" + owed.ToString("N0") + " owed, " + fromTreasury.ToString("N0") + " in the treasury)";
            s.treasury[t] -= fromTreasury; PlayerGold.currency[coin] -= fromWallet; s.wages[i] = 0; if (s.owedSince != null) s.owedSince[i] = 0;
            OfficeRecords.Add(house, RecordKind.WagesPaid, coin, -fromTreasury);
            if (fromWallet > 0 && DayLogs.instance) DayLogs.instance.dayLogs[coin].LogTransaction(-fromWallet, TransactionCategory.other);
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayGoldSound();
            return null;
        }

        // ---- Bankruptcy (Q6, the user: "bankrupt make your office closed also (reset and house not owned anymore)") ----
        // The region's treasury to 0 and its offices closed: upgrades reset, each office's house no longer the player's, pledged or not. A house
        // with no office keeps today's rule. Called after the lenders' own part (Bottomry.Bankruptcy), so a house a lender took is already gone.
        internal static void Bankruptcy(int region)
        {
            var s = World.State?.office; if (s == null) return;
            OfficeFleet.Bankruptcy(region);
            for (int r = 0; r < 3; r++)
                if (region < 0 || r == region) for (int c = 0; c < 4; c++) { s.treasury[r * 4 + c] = 0; s.earned[r * 4 + c] = 0; }
            OfficeRecords.Clear(region);
            foreach (int house in Houses)
            {
                if (region >= 0 && Region(house) != region || !IsOffice(house)) continue;
                ClearHouse(s, Slot(house));
                var b = Pledges.Find(house);
                if (b && Pledges.Owned(b)) ForcedMove.Release(b);
                OfficeSites.Refresh(house);
                Plugin.Log.LogInfo("Trade office: the " + Pledges.Name(house) + " closed by the bankruptcy.");
            }
        }
        // A closed office's own record, all of it: its upgrades, the clerk's wages and their days, and (the deep review of 2026-11-09, question 3b:
        // "reset those 5 too") the clerk's place, his outfit, his Player Model look, the crew hired there and today's cooperation count. The player's
        // own look kept while Player Model's screen edits the clerk (playerLook) is the player's, not the office's: left.
        internal static void ClearHouse(OfficeState s, int i)
        {
            if (s == null || i < 0 || i >= 6) return;
            void Zero(int[] a) { if (a != null && i < a.Length) a[i] = 0; }
            Zero(s.upgrades); Zero(s.wages); Zero(s.salaryDay); Zero(s.owedSince); Zero(s.clerkOutfit);
            Zero(s.crewHired); Zero(s.crewPeriod); Zero(s.coopDay); Zero(s.coopCount);
            if (s.clerkSpot != null) for (int k = i * 7; k < i * 7 + 7 && k < s.clerkSpot.Length; k++) s.clerkSpot[k] = 0f;
            if (s.clerkLook != null && i < s.clerkLook.Length) s.clerkLook[i] = null;
        }
        // ---- "Prepare to uninstall" (the user's list item 6): the treasuries paid into the wallet, the office's objects gone, its data dropped ----
        internal static int Uninstall()
        {
            var s = World.State?.office; int paid = 0;
            if (s != null)
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 4; c++)
                    {
                        int i = r * 4 + c, v = s.treasury[i]; if (v <= 0) continue;
                        long room = (long)int.MaxValue - PlayerGold.currency[c]; int moved = (int)Math.Min(v, room);   // a wallet below 0 (charged first) can't wrap
                        PlayerGold.currency[c] += moved; s.treasury[i] -= moved; paid += moved;
                    }
            OfficeSites.Clear();
            OfficeCarts.Restore(); OfficeCarts.EmptySerpentIsle();
            World.State.office = null; World.State.playerFleet = null;
            return paid;
        }
    }

    // ---- The cart contract (§4.6, Q21): free carts at the office's port ----
    // The port's carrier priced at 0 for transport and storage, as vanilla Sunspire's is (every fee scales from them: the cart's own, its storage,
    // and the trade book's cart delivery, NewMarket.cs CartDelivery). Not saved: set while the contract is live, put back otherwise (a seized or
    // closed office, "Prepare to uninstall"). Serpent Isle has no carrier (its dude is switched off, §2.9): the office adds one for island 22,
    // only where it is wanted (the user, 2026-11-08: "no carrier should only show up after you upgrade the cart contract"): while Serpent Isle's
    // office holds the cart contract, and at a load whose save has an item stored in it (the game puts a saved item back into its cart two frames
    // after the load, SaveablePrefab.PutInInventory, and throws on a slot with no carrier); its fees the common 2 and 1, in Dragons. Dropped again,
    // when empty, once no contract holds it. "Prepare to uninstall" empties it 1 m in front of the player.
    internal static class OfficeCarts
    {
        internal const int SerpentIsle = 22, SerpentIsleHouse = 204;
        private static readonly Dictionary<int, Vector2> original = new Dictionary<int, Vector2>();
        private static readonly Dictionary<int, int> islands = new Dictionary<int, int>();
        internal static CargoCarrier Carrier(int index) => CargoCarrier.carriers != null && index >= 0 && index < CargoCarrier.carriers.Length ? CargoCarrier.carriers[index] : null;
        private static CargoCarrier added;
        // The added carrier gone, if it holds nothing: its slot cleared and its day count unhooked (CargoCarrier.Awake hooked it).
        internal static void DropSerpentIsle()
        {
            if (!added || added.cargo != null && added.cargo.Count > 0) return;
            Sun.OnNewDay -= added.RegisterDayPassed;
            if (Carrier(SerpentIsle) == added) CargoCarrier.carriers[SerpentIsle] = null;
            UnityEngine.Object.Destroy(added.gameObject); added = null;
            Plugin.Log.LogInfo("Trade office: Serpent Isle's added cart carrier removed (no cart contract there, nothing stored).");
        }
        internal static void EnsureSerpentIsle()
        {
            if (Carrier(SerpentIsle)) return;
            var go = new GameObject("EconomyOverhaul cart carrier 22"); go.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(go);
            var c = go.AddComponent<CargoCarrier>(); c.portIndex = SerpentIsle; c.currency = (Currency)1; c.transportPriceMult = 2f; c.storagePriceMult = 1f;
            // Kept at the camera: an item in a cart sits at its cart, and the game destroys an owned item over 600 m from the camera that no ship
            // or house holds (ItemRigidbody.FixedUpdate; ocart-3: the added cart at the world's origin lost its item once the player sailed off).
            go.AddComponent<CarrierAtCamera>();
            go.SetActive(true); added = c;
            Plugin.Log.LogInfo("Trade office: a cart carrier added for Serpent Isle (island 22), which has none.");
        }
        // The carrier of a house's port: its island's index (the game's carrier slots are island IDs).
        internal static int IslandOf(int house)
        {
            if (islands.TryGetValue(house, out var i)) return i;
            var p = Office.PortOf(house); var h = p && p.island ? p.island.GetComponentInParent<IslandHorizon>() : null;
            if (!h) return -1;
            return islands[house] = h.islandIndex;
        }
        internal static void Apply()
        {
            // Bought (even while paused or seized: its stored items wait in it), else dropped when empty.
            if (Office.Has(SerpentIsleHouse, OfficeUpgrade.Cart)) EnsureSerpentIsle(); else DropSerpentIsle();
            foreach (int house in Office.Houses)
            {
                int island = IslandOf(house); var c = Carrier(island); if (!c) continue;
                if (Office.Working(house, OfficeUpgrade.Cart))
                {
                    if (!original.ContainsKey(island)) original[island] = new Vector2(c.transportPriceMult, c.storagePriceMult);
                    c.transportPriceMult = 0f; c.storagePriceMult = 0f;
                }
                else if (original.TryGetValue(island, out var o)) { c.transportPriceMult = o.x; c.storagePriceMult = o.y; original.Remove(island); }
            }
        }
        internal static void Restore()
        {
            foreach (var kv in original) { var c = Carrier(kv.Key); if (c) { c.transportPriceMult = kv.Value.x; c.storagePriceMult = kv.Value.y; } }
            original.Clear();
        }
        // Serpent Isle's added carrier emptied (uninstall): each item out of it, made whole again, 1 m in front of the player and stacked up (ocart-4:
        // put at the office's drop point while far off, an item lands where the far island sinks and the game's 600 m clean-up takes it).
        internal static int EmptySerpentIsle()
        {
            var c = Carrier(SerpentIsle); if (!c || c.cargo == null || c.cargo.Count == 0) return 0;
            var eye = Refs.observerMirror ? Refs.observerMirror.transform : Camera.main ? Camera.main.transform : null;
            var flat = eye ? Vector3.ProjectOnPlane(eye.forward, Vector3.up).normalized : Vector3.forward;
            var at = eye ? eye.position + flat + Vector3.up * .3f : Vector3.zero;
            int n = 0;
            foreach (var item in c.cargo.ToList())
            {
                if (!item) continue;
                item.WithdrawFromCarrier(); item.GetItemRigidbody().ExitInventorySlot(); item.transform.localScale = Vector3.one;
                item.transform.position = at; item.GetItemRigidbody().transform.position = at; at += Vector3.up * .6f; n++;
            }
            c.cargo.Clear();
            if (n > 0) Plugin.Log.LogInfo("Prepared to uninstall: " + n + " items taken out of Serpent Isle's added cart, 1 m in front of the player.");
            return n;
        }
    }
    // A saved item stored in Serpent Isle's added cart (slot 100 + 22): the carrier made before the game puts it back (two frames later).
    [HarmonyPatch(typeof(SaveablePrefab), "Load")] internal static class OfficeCartLoadPatch
    {
        static void Prefix(SavePrefabData data)
        {
            if (data != null && data.inventorySlot == 100 + OfficeCarts.SerpentIsle)
                try { OfficeCarts.EnsureSerpentIsle(); } catch (Exception e) { Plugin.Log.LogWarning("Trade office: Serpent Isle's cart carrier not added for a stored item: " + e.Message); }
        }
    }

    // ---- Crew recruitment (§4.8, Q24: 5 candidates at every office with the hub's role odds, refreshed every 7 days; no legendary crew; Virtual
    // Crew's own limit and pay) ----
    // Virtual Crew 0.2.3 is called by name (no reference; its soft dependency stays). Its HireCrew needs the player at a port dude (away from one its
    // check clears the port's pool), so the office adds the crewman to its list as HireCrew itself does (VirtualCrewManager.Crew.Add), after its
    // limit: at most 2 First Officers (FirstOfficerCount). Pay, shares and saving walk that list, as in the mod. A candidate is made with the mod's
    // own constructor (name, role, a seeded random: the stats), its name from the mod's name pool, its role by the hub's odds out of 200
    // (Deckhand 100, Navigator 20, Pilot 20, the six others 10 each), all from a seed of the office and the period.
    internal static class OfficeCrew
    {
        internal const string Guid = "com.zorkinian.virtualcrew";
        internal static readonly string[] RoleNames = { "Deckhand", "Navigator", "Pilot", "First Officer", "Chef", "Quartermaster", "Supercargo", "Lookout", "Steward" };
        private static readonly string[] RoleKeys = { "Deckhand", "Navigator", "Pilot", "ChiefOfficer", "Chef", "Quartermaster", "Supercargo", "Lookout", "Steward" };
        private static readonly int[] HubOdds = { 100, 20, 20, 10, 10, 10, 10, 10, 10 };
        private static readonly string[] SpareNames = { "Abel", "Bram", "Cato", "Dirk", "Elias", "Fenn", "Gideon", "Hale", "Ivo", "Jory", "Kesh", "Lorne" };
        private static bool tried; private static Type manager, crewman, role; private static ConstructorInfo make; private static string[] names;
        internal sealed class Candidate { internal int Slot, Role; internal string Name, Stats; internal object Crewman; internal bool Hired; }
        internal static bool Available { get { Find(); return make != null; } }
        private static void Find()
        {
            if (tried) return; tried = true;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid)) return;
            // by FleetCompat.TypeOf, not AccessTools.TypeByName: the same type when it exists, and a miss (another Virtual Crew version) scans no
            // type list and logs no HarmonyX warning
            manager = FleetCompat.TypeOf("SailwindVirtualCrew.VirtualCrewManager"); crewman = FleetCompat.TypeOf("SailwindVirtualCrew.Crewman"); role = FleetCompat.TypeOf("SailwindVirtualCrew.ShipRole");
            if (manager == null || crewman == null || role == null) { Plugin.Log.LogWarning("Trade office: Virtual Crew is loaded, but its classes aren't as expected; no crew at the office."); return; }
            make = crewman.GetConstructor(new[] { typeof(string), role, typeof(System.Random) });
            names = (AccessTools.Field(manager, "CrewNamePool")?.GetValue(null) as string[]) ?? SpareNames;
            if (make == null || AccessTools.Property(manager, "Instance") == null || AccessTools.Property(manager, "Crew") == null) { make = null; Plugin.Log.LogWarning("Trade office: Virtual Crew's crewman or crew list isn't as expected; no crew at the office."); }
        }
        private static object Manager => AccessTools.Property(manager, "Instance").GetValue(null, null);
        internal static int Period => Math.Max(0, GameState.day) / 7;
        // shortcut: the candidates come from Virtual Crew's name pool (CrewNamePool, SpareNames without it) and its constructor's stats, so a
        // Virtual Crew version with another pool or stat rule gives the same week other candidates (the saved hired bits then mark other names);
        // replace with the candidates saved for the period if a Virtual Crew update must not change a week already shown.
        internal static List<Candidate> Candidates(int house)
        {
            var list = new List<Candidate>(); Find(); if (make == null) return list;
            int period = Period, slot = Office.Slot(house); var st = World.State?.office;
            int hired = st?.crewHired != null && st.crewPeriod != null && slot >= 0 && st.crewPeriod[slot] == period ? st.crewHired[slot] : 0;
            var rng = new System.Random(unchecked(house * 1000003 + period * 7919 + 17));
            for (int i = 0; i < 5; i++)
            {
                string name = names[rng.Next(names.Length)]; int roll = rng.Next(200), r = 0;
                for (int sum = HubOdds[0]; roll >= sum && r < HubOdds.Length - 1; ) { r++; sum += HubOdds[r]; }
                var c = make.Invoke(new object[] { name, Enum.Parse(role, RoleKeys[r]), new System.Random(rng.Next()) });
                list.Add(new Candidate { Slot = i, Role = r, Name = name, Crewman = c, Hired = (hired & (1 << i)) != 0, Stats = Stats(c) });
            }
            return list;
        }
        // The stats a candidate shows (the mod's advertised ones, Adv*, where it has them).
        private static string Stats(object c)
        {
            var parts = new List<string>();
            // Its advertised stats (AdvStrength ... AdvCharisma, public get: Virtual Crew 0.2.3's Crewman; ocrew-1 found none under shorter names).
            foreach (var s in new[] { "Strength", "Dexterity", "Constitution", "Intelligence", "Wisdom", "Charisma" })
            {
                var m = (MemberInfo)AccessTools.Property(crewman, "Adv" + s) ?? AccessTools.Field(crewman, "Adv" + s);
                object v = m is PropertyInfo p ? p.GetValue(c, null) : m is FieldInfo f ? f.GetValue(c) : null;
                if (v != null) parts.Add(s.Substring(0, 3) + " " + v);
            }
            return string.Join(" · ", parts.ToArray());
        }
        internal static string Refusal(int house, Candidate c)
        {
            if (!Available) return "Needs the Virtual Crew mod";
            if (c == null) return "Choose a candidate";
            if (c.Hired) return "Hired";
            if (c.Role == 3 && FirstOfficers() >= 2) return "At most 2 First Officers";
            return null;
        }
        private static int FirstOfficers() { var p = AccessTools.Property(manager, "FirstOfficerCount"); return p != null ? (int)p.GetValue(Manager, null) : 0; }
        internal static bool Hire(int house, Candidate c)
        {
            var why = Refusal(house, c); if (why != null) { Loans.Notify(why); return false; }
            try
            {
                var crew = AccessTools.Property(manager, "Crew").GetValue(Manager, null) as System.Collections.IList;
                if (crew == null) { Loans.Notify("Virtual Crew's crew isn't ready"); return false; }
                crew.Add(c.Crewman);
                var st = Office.State; int slot = Office.Slot(house);
                if (st.crewHired == null || st.crewHired.Length != 6) st.crewHired = new int[6];
                if (st.crewPeriod == null || st.crewPeriod.Length != 6) st.crewPeriod = new int[6];
                if (st.crewPeriod[slot] != Period) { st.crewPeriod[slot] = Period; st.crewHired[slot] = 0; }
                st.crewHired[slot] |= 1 << c.Slot; c.Hired = true;
                if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUIClickSound();
                Loans.Notify(c.Name + " hired as " + RoleNames[c.Role]);
                Plugin.Log.LogInfo("Trade office: " + c.Name + " (" + RoleNames[c.Role] + ") hired at the " + Pledges.Name(house) + ".");
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Trade office: the hire failed: " + (e.InnerException ?? e).Message); Loans.Notify("The hire failed"); return false; }
        }
    }

    // ---- Local office cooperation (§4.7; the user's text, Q22, Q23, list item 2) ----
    // A copy of the port dude's own trade book on the office's table (it keeps its dude, so it opens that port's market: officeparts). While it is
    // the book open (marked at its click, cleared when the book closes), on that port's market: a good the port trades costs 10% less and its
    // purchase moves supply half as much; a crate a player can unseal that the port doesn't trade (none in stock and not made there: Absent) is priced as at supply -50% of capacity, with no
    // discount, moves no supply, at most 10 a day per office. No loans at this book (its Loan button hidden). Its crates carry a mark and can't be sold at this port; selling here
    // otherwise pays the market's normal price (Q23). The cargo still lands in the port's own cargo area (CargoPlacement, unchanged).
    internal static class OfficeBook
    {
        // AbsentSupply: -50% of capacity (the user, 2026-11-09: "All unsealable crate sold in office trade book will price at -50% capacity instead
        // of -20%"); the drinkable barrels share the rule (Offered).
        internal const float Discount = .9f, AbsentSupply = -.5f;
        internal const int DailyLimit = 10;
        internal static int Opening, Current;
        // Unsealable: a crate with something in it (§8, "Cooperation book": ShipItemCrate with amount over 0), read from the good's prefab.
        internal static bool Unsealable(int g)
        {
            var p = PrefabsDirectory.instance ? PrefabsDirectory.instance.GetGood(g) : null; var c = p ? p.GetComponent<ShipItemCrate>() : null;
            return c && c.amount > 0f;
        }
        // What the office brings in from other sources when the port doesn't trade it: a crate the player can unseal, or a drinkable barrel (water,
        // rum, wine, beer, mead and cider, CargoCondition.LiquidCargo; the user, 2026-11-09: "i also want all drinkable barrel to be able to bought"),
        // under the same rules (the price at -50% of capacity, no 10% off, no supply drop, the same 10 a day).
        internal static bool Offered(int g) => Unsealable(g) || CargoCondition.LiquidCargo.Contains(g);
        // Such a good's count on the book's page is how many of today's 10 are left, in dark blue (the user, 2026-11-09: "Make it show quantity in
        // dark blue color, so player know this one is from 10 crate/day limit"); its row in the goods list drawn available while any are left.
        // The game's text shader brightens a colour by its gamma curve (drawn = 255 x (v / 255)^(1 / 2.2): #14287A drew at 79, 110, 184, a mid blue, in
        // obdc-2's picture; Unavailable's note), so the ink is the dark blue it should draw as, 20, 40, 122, taken back through that curve.
        internal const string LeftColor = "#010432";
        internal static bool FromOtherSources(EconomyUI ui, IslandMarket m, int g, out int office)
        {
            office = Active(ui);
            return office != 0 && m && g > 0 && g != 51 && Absent(m, g) && Offered(g);
        }
        // A good this port doesn't trade (Rules.OfficeAbsent): not in stock (HasGood, this mod's patch: stock and the origin rule) and not made here.
        internal static bool Absent(IslandMarket m, int g) =>
            !m.HasGood(g) && Rules.OfficeAbsent(false, m.production != null && g >= 0 && g < m.production.Length ? m.production[g] : 0f);
        // The office whose book is open on its own port's market, else 0.
        internal static int Active(EconomyUI ui)
        {
            if (Current == 0 || !ui || !ui.uiActive || !World.Ready) return 0;
            var m = Fields.Get<IslandMarket>(ui, "currentIsland"); var p = Office.PortOf(Current);
            return m && p && m.GetPort() == p && Office.Working(Current, OfficeUpgrade.Cooperation) ? Current : 0;
        }
        internal static int Left(int house)
        {
            var st = World.State?.office; int i = Office.Slot(house);
            if (st?.coopDay == null || st.coopCount == null || i < 0 || st.coopDay[i] != GameState.day) return DailyLimit;
            return Math.Max(0, DailyLimit - st.coopCount[i]);
        }
        internal static void Count(int house, int n)
        {
            var st = Office.State; int i = Office.Slot(house);
            if (st.coopDay == null || st.coopDay.Length != 6) st.coopDay = new int[6];
            if (st.coopCount == null || st.coopCount.Length != 6) st.coopCount = new int[6];
            if (st.coopDay[i] != GameState.day) { st.coopDay[i] = GameState.day; st.coopCount[i] = 0; }
            st.coopCount[i] += n;
        }
        // A unit's raw price at this book: the market's buy price at this supply with the trade book's spread, 10% off; a good the port doesn't
        // trade at -50% of capacity (AbsentSupply), with no discount (the user, 2026-11-08: "Make the unsealable good don't receive 10% discount").
        internal static int BuyRaw(IslandMarket m, int g, double stock, bool absent)
        {
            float p = absent ? m.GetGoodPriceAtSupply(g, AbsentSupply * Economy.PricingCap(m, g)) : m.GetGoodPriceAtSupply(g, (float)stock);
            return Mathf.RoundToInt(p * (1 + (float)Rules.Spread(p)) * (absent ? 1f : Discount));
        }
        internal static bool Marked(Good good, IslandMarket m)
        {
            var item = good ? good.GetComponent<ShipItem>() : null; var r = item ? CargoRecords.Of(item) : null;
            return r != null && m && r.officePort == m.GetPortIndex() + 1;
        }
    }
    public sealed class OfficeBookMark : MonoBehaviour { internal int house; }
    [HarmonyPatch(typeof(GPButtonPortMissions), "OnActivate")] internal static class OfficeBookClickPatch
    {
        static void Prefix(GPButtonPortMissions __instance) { var b = __instance ? __instance.GetComponent<OfficeBookMark>() : null; OfficeBook.Opening = b ? b.house : 0; }
        static Exception Finalizer(Exception __exception) { OfficeBook.Opening = 0; return __exception; }
    }
    [HarmonyPatch(typeof(EconomyUI), "OpenUI")] internal static class OfficeBookOpenPatch { static void Prefix() => OfficeBook.Current = OfficeBook.Opening; }
    [HarmonyPatch(typeof(EconomyUI), "CloseUI")] internal static class OfficeBookClosePatch { static void Postfix() => OfficeBook.Current = 0; }
    // The goods list draws a good the port doesn't have grey (EconomyUI.RefreshGoodsList reads HasGood); at the office's book a good it brings in
    // from other sources is drawn available while any of today's 10 are left (its count: BulkTradeUI.RefreshLabels).
    [HarmonyPatch(typeof(EconomyUI), "RefreshGoodsList")] internal static class OfficeBookListPatch
    {
        static void Postfix(EconomyUI __instance)
        {
            if (OfficeBook.Active(__instance) == 0) return;
            var m = Fields.Get<IslandMarket>(__instance, "currentIsland"); var buttons = Fields.Get<EconomyUIButton[]>(__instance, "goodsListButtons");
            var materials = Fields.Get<Material[]>(__instance, "buttonMaterials"); if (!m || buttons == null || materials == null || materials.Length == 0) return;
            foreach (var b in buttons)
            {
                if (!b) continue; int g = b.index;
                if (g == __instance.currentSelectedGood || g <= 0 || g >= m.currentPlayerGoods.Length || m.currentPlayerGoods[g] > 0) continue;
                if (OfficeBook.FromOtherSources(__instance, m, g, out int office) && OfficeBook.Left(office) > 0) b.SetButtonMaterial(materials[0]);
            }
        }
    }

    // ---- The trade fleet (§4.5; the user's text, Q8-Q15, and their decisions of 2026-11-07 in §3B) ----
    // A ship joins from Shipwright and Transport's storage (its button calls OfficeFleetApi by name, from that mod's Plugin.cs) and
    // becomes one of the player's traders: an ordinary trader record in SaveState.playerFleet, shaped by its kind as the NPC traders of that kind
    // (FleetDefinitions: speed, wait, slots, mass, network; Baghlah and Jong always 130 slots and 13,000 lb, the user's), sailing in the same
    // simulation (Fleet.Tick), left out of the trade book (its reports stay its own), and drawn as it was rigged (VisibleTraders.cs, TraderHulls' fleet part).
    // Earnings (the user's 2026-11-07 decisions): its trip's real profit (what it sold for less what it paid, in value units at the daily rates)
    // times its kind's share, in the sale port's coin, into its office region's treasury; a losing trip takes the same share of the loss from it,
    // never below 0 (Q11). Its route's fee each midnight from that treasury in the region's coin; unpaid, the ship waits in port; a seized office's
    // ships wait with no fee (Q5). "About X gold a day" before the fee: its shares of the last 30 days over the days it sailed in them.
    internal static class OfficeFleet
    {
        // TripLog: the trips a ship's trade history keeps (the user, 2026-11-08: "you should have page to see all the trade history"; 6 before, the
        // page's rows; 100 while they were away; then "only keep last 35 trip", TRADE_OFFICE_DRAFT.md §3B "The whole trade history"), the oldest
        // dropped beyond.
        internal const int PerOffice = 3, Window = 30, FirstId = 100000, TripLog = 35;
        // The kinds' short names (the Treasury book's Trader type column, the log): the user, 2026-11-08 night: "treasure book: change independent to free,
        // regional to inter-region" (were "Regional", "Independent").
        internal static readonly string[] Kinds = { "Local small", "Local medium", "Inter-region", "Free", "Chronos" };
        private static readonly string[] Groups = { "small", "medium", "regional", "independent", "chronos" };
        // The fee a day by route, in gold (the user: "small local = 0.5 per day / medium local = 2.5 / regional long range = 6 / Independant long range = 7 / Chronos = 15").
        internal static readonly float[] Fees = { .5f, 2.5f, 6f, 7f, 15f };
        // The share of a trip's profit by kind, set on the early game (the user: "Early game"): (the kind's target kept a day + its fee) / its profit a
        // day on days 91-180 of the study, rounded to 0.5%. The targets since 2026-11-08 evening (the user: "make local small target 2 gold, regional 55
        // slot to 8, independant 70 to 10, regional 130 slot to 14. indepent 130 to 16. Chronos to 25"; local medium 4 as before): 6%, 6%, regional 55
        // 24%, regional 130 15.5%, independent 70 16.5%, independent 130 13.5%, Chronos 40.5% (§3B "The fleet's targets raised"; were 3.5%, 6%, 20.5%,
        // 14%, 14.5%, 13%, 35%). Never shown.
        internal static float Share(int kind, int units) => kind < 0 ? 0f : kind == 0 ? .06f : kind == 1 ? .06f : kind == 2 ? (units >= 130 ? .155f : .24f) : kind == 3 ? (units >= 130 ? .135f : .165f) : .405f;
        // The kinds a hull may take (the user's lists; the Brig in both: §4.5's union): Dhow 10, Cog 40, Kakam 90 local small; Sanbuq 20, Junk 80
        // local medium, regional, independent; Brig 50 those and Chronos; Baghlah 30, Jong 70 regional, independent, Chronos.
        internal static int[] Allowed(int hull) => hull == 10 || hull == 40 || hull == 90 ? new[] { 0 } : hull == 20 || hull == 80 ? new[] { 1, 2, 3 } : hull == 50 ? new[] { 1, 2, 3, 4 } : hull == 30 || hull == 70 ? new[] { 2, 3, 4 } : new int[0];
        internal static int Capital(int region) => region == 0 ? 0 : region == 1 ? 9 : 15;
        internal static List<BoatState> Ships => World.State.playerFleet ?? (World.State.playerFleet = new List<BoatState>());
        internal static int IndexOf(BoatState b) { var l = World.State?.playerFleet; return l == null || b == null ? -1 : l.IndexOf(b); }
        internal static bool Is(BoatState b) => IndexOf(b) >= 0;
        // A ship that joined waits at its office's port, trading nothing and paying no fee, until the player sets its route on the Fleet tab (the user,
        // 2026-11-08: "newly add trader shouldn't be auto assign route, it should be assign by player then start trading"; "No fee"; "Assign route button").
        internal const int NotAssigned = -1;
        internal static bool Assigned(int i) { var s = World.State?.office; return s?.fleetKind != null && i >= 0 && i < s.fleetKind.Length && s.fleetKind[i] >= 0; }
        internal static bool Unassigned(int i) { var s = World.State?.office; return s?.fleetKind != null && i >= 0 && i < s.fleetKind.Length && s.fleetKind[i] < 0; }
        // The route's name on screen (the user, 2026-11-08 night: "the route name should be: Local small trader, Local medium trader, <region name> inter
        // region trader, free inter region trader, Chronos trader"; then "change the text inter region to inter-region"): the regional named by its
        // office's region. Kinds stays the short name, for the Treasury book's narrow Trader type column (KindShort) and the log.
        internal static string RouteName(int kind, int region) => kind == 0 ? "Local small trader" : kind == 1 ? "Local medium trader" :
            kind == 2 ? BookUI.RegionName(region) + " inter-region trader" : kind == 3 ? "Free inter-region trader" : kind == 4 ? "Chronos trader" : "Not assigned";
        internal static string KindName(int i) => Assigned(i) ? RouteName(Mathf.Clamp(Office.State.fleetKind[i], 0, 4), Office.Region(Office.State.fleetHouse[i])) : "Not assigned";
        internal static string KindShort(int i) => Assigned(i) ? Kinds[Mathf.Clamp(Office.State.fleetKind[i], 0, 4)] : "Not assigned";
        // On screen a ship is "it", never "her" (the user, 2026-11-08 evening: "Show non until it's route is set. i don't want to use her to mention ship").
        internal const string NoRouteYet = "none until its route is set";
        // A ship's summary, the same rows on the office scroll's Fleet tab and the Treasury book's Trade fleet page (the user, 2026-11-08: "please match the
        // text from trade office fleet control to the trade fleet summary"; the book's words: "change "about" to "estimated profit"", "Route fee ->
        // Maintenance fee , and it should show it's local currency", "Change position text to Location"). Location last.
        internal static readonly string[] SummaryLabels = { "Hull", "Trader type", "Maintenance fee", "Estimated profit", "Last trip", "Lifetime profit", "Office", "Joined", "Location" };
        internal static string[] SummaryValues(int i)
        {
            var s = Office.State; bool set = Assigned(i);
            return new[] { Pledges.Name(s.fleetHull[i]), KindName(i), set ? FeeText(i) : NoRouteYet,
                set ? Estimate(i).ToString("0.0") + " gold/day" : NoRouteYet, LastTripText(i), Gold(s.fleetLife[i]).ToString("0.0") + " gold",
                OfficeUiParts.OfficeName(s.fleetHouse[i]), s.fleetJoined != null && i < s.fleetJoined.Length ? "day " + s.fleetJoined[i] : "", Position(i) };
        }
        internal static IEnumerable<int> Of(int house) { var s = World.State?.office; var l = World.State?.playerFleet; if (s?.fleetHouse == null || l == null) yield break; for (int i = 0; i < l.Count && i < s.fleetHouse.Length; i++) if (s.fleetHouse[i] == house) yield return i; }
        // The parallel arrays kept the length of the list.
        private static void Fit()
        {
            var s = Office.State; int n = Ships.Count;
            int[] I(int[] a, int per = 1) { var r = new int[n * per]; if (a != null) Array.Copy(a, r, Math.Min(a.Length, r.Length)); return r; }
            string[] S(string[] a) { var r = new string[n]; if (a != null) Array.Copy(a, r, Math.Min(a.Length, r.Length)); return r; }
            s.fleetHouse = I(s.fleetHouse); s.fleetHull = I(s.fleetHull); s.fleetKind = I(s.fleetKind); s.fleetCost = I(s.fleetCost); s.fleetLast = I(s.fleetLast); s.fleetLife = I(s.fleetLife);
            s.fleetSums = I(s.fleetSums, Window); s.fleetSumDay = I(s.fleetSumDay); s.fleetJoined = I(s.fleetJoined); s.fleetHeld = I(s.fleetHeld); s.fleetName = S(s.fleetName); s.fleetRig = S(s.fleetRig);
            s.fleetLastCoin = I(s.fleetLastCoin); s.fleetLastCoins = I(s.fleetLastCoins); s.fleetStart = I(s.fleetStart);
            s.fleetTripDay = I(s.fleetTripDay, TripLog); s.fleetTripPort = I(s.fleetTripPort, TripLog); s.fleetTripCoin = I(s.fleetTripCoin, TripLog); s.fleetTripCoins = I(s.fleetTripCoins, TripLog);
            s.fleetTripGood = I(s.fleetTripGood, TripLog); s.fleetTripCrates = I(s.fleetTripCrates, TripLog); s.fleetPorts = S(s.fleetPorts); s.fleetGoods = S(s.fleetGoods);
            s.fleetBestPort = I(s.fleetBestPort); s.fleetBestCoin = I(s.fleetBestCoin); s.fleetBestCoins = I(s.fleetBestCoins); s.fleetBestValue = I(s.fleetBestValue);
        }
        // Whether every per-ship array holds the fleet (a save made before an array was added has it null or short: Fit then).
        private static bool Fitted()
        {
            var s = Office.State; int n = Ships.Count;
            bool I(int[] a, int per = 1) => a != null && a.Length >= n * per; bool S(string[] a) => a != null && a.Length >= n;
            return I(s.fleetLastCoin) && I(s.fleetLastCoins) && I(s.fleetStart) && I(s.fleetTripDay, TripLog) && I(s.fleetTripPort, TripLog) && I(s.fleetTripCoin, TripLog) &&
                I(s.fleetTripCoins, TripLog) && I(s.fleetTripGood, TripLog) && I(s.fleetTripCrates, TripLog) && S(s.fleetPorts) && S(s.fleetGoods) && I(s.fleetBestPort) &&
                I(s.fleetBestCoin) && I(s.fleetBestCoins) && I(s.fleetBestValue);
        }
        private static void RemoveAt(int i)
        {
            var s = Office.State; var l = Ships; if (i < 0 || i >= l.Count) return;
            int[] I(int[] a, int per = 1) { if (a == null) return null; var r = a.ToList(); r.RemoveRange(i * per, Math.Min(per, Math.Max(0, r.Count - i * per))); return r.ToArray(); }
            string[] S(string[] a) { if (a == null) return null; var r = a.ToList(); if (i < r.Count) r.RemoveAt(i); return r.ToArray(); }
            s.fleetHouse = I(s.fleetHouse); s.fleetHull = I(s.fleetHull); s.fleetKind = I(s.fleetKind); s.fleetCost = I(s.fleetCost); s.fleetLast = I(s.fleetLast); s.fleetLife = I(s.fleetLife);
            s.fleetSums = I(s.fleetSums, Window); s.fleetSumDay = I(s.fleetSumDay); s.fleetJoined = I(s.fleetJoined); s.fleetHeld = I(s.fleetHeld); s.fleetName = S(s.fleetName); s.fleetRig = S(s.fleetRig);
            s.fleetLastCoin = I(s.fleetLastCoin); s.fleetLastCoins = I(s.fleetLastCoins); s.fleetStart = I(s.fleetStart);
            s.fleetTripDay = I(s.fleetTripDay, TripLog); s.fleetTripPort = I(s.fleetTripPort, TripLog); s.fleetTripCoin = I(s.fleetTripCoin, TripLog); s.fleetTripCoins = I(s.fleetTripCoins, TripLog);
            s.fleetTripGood = I(s.fleetTripGood, TripLog); s.fleetTripCrates = I(s.fleetTripCrates, TripLog); s.fleetPorts = S(s.fleetPorts); s.fleetGoods = S(s.fleetGoods);
            s.fleetBestPort = I(s.fleetBestPort); s.fleetBestCoin = I(s.fleetBestCoin); s.fleetBestCoins = I(s.fleetBestCoins); s.fleetBestValue = I(s.fleetBestValue);
            l.RemoveAt(i);
        }
        // The ship's record shaped by its kind, as the NPC traders of that kind are (FleetDefinitions), from its office's region.
        private static bool Shape(BoatState b, int house, int hull, int kind)
        {
            int region = Office.Region(house);
            var def = kind == 4 ? FleetDefinitions.Current.FirstOrDefault(d => d.Group == "chronos" && d.Home == Capital(region))
                : FleetDefinitions.Current.FirstOrDefault(d => d.Group == Groups[kind] && (kind == 3 ? d.Home == -1 : d.Home == region) && !d.Ffl);
            if (def == null) return false;
            def.Apply(b); b.group = def.Group; b.home = def.Home; b.network = def.Network();
            if (hull == 30 || hull == 70) { b.units = 130; b.weight = 13000; }
            return true;
        }
        private static int NextId()
        {
            var s = Office.State; int id = Math.Max(FirstId, Math.Max(s.fleetNextId, Ships.Count == 0 ? FirstId : Ships.Max(x => x.id) + 1));
            s.fleetNextId = id + 1; return id;
        }
        // The region's office with room for one more (the yard's region, Q15), or why not.
        // shortcut: the first office in Office.Houses' order with room takes the ship (201 before 205, 202 before 204, 203 before 206), not the
        // player's choice or the yard's nearest office; replace if the player should pick the office (or the nearest should take it).
        internal static int Room(int region, out string why)
        {
            why = null;
            if (!World.Ready) { why = "Not available"; return 0; }
            var offices = Office.Houses.Where(h => Office.Region(h) == region && Office.Working(h, OfficeUpgrade.Fleet)).ToList();
            if (offices.Count == 0) { why = "No trade office with a trade fleet in " + BookUI.RegionName(region); return 0; }
            foreach (int h in offices) if (Of(h).Count() < PerOffice) return h;
            why = "The trade offices of " + BookUI.RegionName(region) + " have no room (3 ships each)"; return 0;
        }
        internal static string Refusal(int region, int hull)
        {
            if (Allowed(hull).Length == 0) return "Only the game's own hulls can trade";
            Room(region, out var why); return why;
        }
        // The first sail of a rig text a trader copy can't draw (Rules.FleetSailRefused), by its modsail line's name or its slot's sail now, else null.
        internal static string RefusedSail(string rig)
        {
            if (string.IsNullOrEmpty(rig)) return null;
            var sails = PrefabsDirectory.instance ? PrefabsDirectory.instance.sails : null;
            foreach (var p in TraderRigs.Read(rig.Split('\n')))
            {
                foreach (var m in p.ModSails) if (Rules.FleetSailRefused(m.SailName)) return m.SailName;
                foreach (var s in p.Sails)
                {
                    var sail = sails != null && s.Prefab >= 0 && s.Prefab < sails.Length && sails[s.Prefab] ? sails[s.Prefab].GetComponent<Sail>() : null;
                    if (sail && Rules.FleetSailRefused(sail.sailName)) return sail.sailName;
                }
            }
            return null;
        }
        internal static int Add(int region, int hull, string name, string rig)
        {
            var why = Refusal(region, hull); if (why != null) { Loans.Notify(why); return -1; }
            // a rig with a sail a trader copy can't draw (Rules.FleetSailRefused), whoever asks
            string refused = RefusedSail(rig); if (refused != null) { Plugin.Log.LogInfo("Trade office: a ship not added to the fleet: its rig has " + refused + "."); return -1; }
            int house = Room(region, out _); int shape = Allowed(hull)[0];
            var port = Office.PortOf(house); if (!port) return -1;
            var b = new BoatState { id = NextId(), origin = port.portIndex, remaining = 1.25 };
            // shaped meanwhile by its hull's first kind (its size and harbour for its berth and drawing), its kind itself not set (NotAssigned)
            if (!Shape(b, house, hull, shape)) return -1;
            // The ship starts in the office's port (the user, 2026-11-08: "always start at the port of the office, and start with port waiting time";
            // drawn, its berth is taken as any trader's) and waits there, trading nothing and paying no fee, until the player sets its route (the user,
            // the same day: "newly add trader shouldn't be auto assign route, it should be assign by player then start trading"); its port wait starts
            // then (SetKind).
            // shortcut: the office's port, not its wharf; replace when the wharf is built (the user: "If wharf is unlocked for the office, trader
            // spawn at office wharf").
            b.remaining = Math.Max(1.25, b.wait);
            foreach (int p in b.network) { var m = World.Market(p); if (!m) continue; var r = Fleet.Report(World.MarketData(p).reports, p); if (r != null) Fleet.PutReport(b.reports, r); }
            Ships.Add(b); Fit();
            var s = Office.State; int i = Ships.Count - 1;
            s.fleetHouse[i] = house; s.fleetHull[i] = hull; s.fleetKind[i] = NotAssigned; s.fleetName[i] = string.IsNullOrEmpty(name) ? Pledges.Name(hull) : name; s.fleetRig[i] = WithHull(rig, hull); s.fleetJoined[i] = GameState.day; s.fleetSumDay[i] = GameState.day;
            Loans.Notify(s.fleetName[i] + " joined the " + Pledges.Name(house) + "'s trade fleet: set its route at the office");
            Plugin.Log.LogInfo("Trade office: " + s.fleetName[i] + " (hull " + hull + ") joined the " + Pledges.Name(house) + "'s fleet, trader " + b.id + ", waiting for its route.");
            return b.id;
        }
        // The ship's rig's plan line given its hull as Add was told it: RigOf writes the boat's own save number, and Transport's copies have their own
        // (1010 and up: its CopyEngine.Spawn sets a copy's sceneIndex to its block; found by Transport's chat, 2026-11-08).
        internal static string WithHull(string rig, int hull)
        {
            if (string.IsNullOrEmpty(rig)) return rig;
            var lines = rig.Split('\n'); var f = lines[0].Split(' ');
            if (f.Length >= 3 && f[0] == "plan") { f[2] = hull.ToString(System.Globalization.CultureInfo.InvariantCulture); lines[0] = string.Join(" ", f); }
            return string.Join("\n", lines);
        }
        // The ship's kind set or changed at the office (Q: "assign what trader behavior they will adopt"): at once in port, else when it next arrives
        // (§4.5). The first time (a ship waiting for its route) it starts trading: its kind's time in port first, then its first trip.
        internal static string KindRefusal(int i, int kind)
        {
            var s = World.State?.office; if (s == null || i < 0 || i >= Ships.Count) return "No ship";
            if (!Allowed(s.fleetHull[i]).Contains(kind)) return "This hull can't take that route";
            if (kind == 4 && Reputations.Level(Reputations.Chronos) < 6) return "Needs Chronos reputation 6";
            return null;
        }
        internal static void SetKind(int i, int kind)
        {
            var why = KindRefusal(i, kind); if (why != null) { Loans.Notify(why); return; }
            var s = Office.State; var b = Ships[i]; bool first = s.fleetKind[i] < 0; s.fleetKind[i] = kind;
            if (b.destination < 0 && b.goods.Length == 0) Shape(b, s.fleetHouse[i], s.fleetHull[i], kind);
            if (!first) return;
            b.remaining = Math.Max(1.25, b.wait);
            if (s.fleetStart == null || s.fleetStart.Length < Ships.Count) Fit();
            s.fleetStart[i] = GameState.day;
            Loans.Notify(s.fleetName[i] + "'s route: " + RouteName(kind, Office.Region(s.fleetHouse[i])) + ". It sails after its time in port");
            Plugin.Log.LogInfo("Trade office: " + s.fleetName[i] + " (trader " + b.id + ") given its route, " + Kinds[kind] + ", on day " + GameState.day + ".");
        }
        // Dismissed (Q15): 80% of its hull's price into the treasury, in Gold; the ship leaves the fleet.
        internal static void Dismiss(int i)
        {
            string name = i >= 0 && i < Ships.Count ? Office.State.fleetName[i] : null; int paid = Leave(i, RecordKind.Dismissed);
            if (paid >= 0) Loans.Notify(name + " dismissed: " + paid + " gold into the treasury");
        }
        // The ship leaves the fleet with 80% of its hull's price in Gold paid into its office's region's treasury (Dismiss; the user, 2026-11-08, for a ship
        // removed with Shipyard Expansion: "80% like Dismiss"). The gold paid; -1: no such ship.
        private static int Leave(int i, RecordKind kind)
        {
            var s = Office.State; if (i < 0 || i >= Ships.Count) return -1;
            int house = s.fleetHouse[i], region = Office.Region(house); var hull = Pledges.Find(s.fleetHull[i]);
            int paid = hull ? (int)Math.Floor(hull.price * .8) : 0;
            s.treasury[region * 4 + 3] = (int)Math.Min(int.MaxValue, (long)s.treasury[region * 4 + 3] + paid);
            OfficeRecords.Add(house, kind, 3, paid);
            RemoveAt(i);
            return paid;
        }
        // A game loaded (World.Load): a ship whose rig needs a mod this game doesn't have leaves the fleet, paid as dismissed (the user, 2026-11-08: "if
        // player remove the shipyard expansion is remove mid save, remove that trader from trader fleet too"; their answers: every ship rigged with it,
        // 80% like Dismiss). Shipyard Expansion's rigs say so (OfficeFleetApi.RigOf's "needs" line, on a hull it changed): its parts at the end of
        // the hull's list, its options at the end of a part's and its longer masts, so the game's LoadData can't put the ship's rig on the hull without it.
        // So does a rig with another mod's sail no longer in the game (RigOf's "modsail" line; the user, 2026-11-09, asked what happens to such a ship:
        // "Leaves the fleet, 80% paid"); the sail mods add their sails in PrefabsDirectory.Start, before any save loads.
        // shortcut: a sail of the game's own on another mod's mast (Unstayed Junk Sail Mast's 96-127, MoreSailwindSails' 128 and up) isn't checked
        // here: without that mod the ship stays and is drawn on its hull's stock rig (StartFleet's Check); replace if such ships should leave too.
        internal static void Loaded()
        {
            Restride();
            var s = World.State?.office; if (s?.fleetRig == null || World.State.playerFleet == null) return;
            for (int i = Math.Min(Ships.Count, s.fleetRig.Length) - 1; i >= 0; i--)
            {
                var missing = TraderRigs.Missing(s.fleetRig[i]); if (missing.Count == 0) continue;
                string name = s.fleetName[i], mods = string.Join(", ", missing.Select(g => g == TraderRigs.ShipyardExpansionGuid ? "Shipyard Expansion" : g).ToArray());
                int paid = Leave(i, RecordKind.Removed);
                Loans.Notify(name + " left the trade fleet: its rig needs " + mods + ". " + paid + " gold into the treasury");
                Plugin.Log.LogInfo("Trade office: " + name + " left the fleet at the load: its rig needs " + mods + ", not loaded; " + paid + " gold (80% of its hull) into the treasury.");
            }
        }
        // A save whose trade histories hold another number of trips a ship (6 before 2026-11-08's "all the trade history", then 100 until "only keep
        // last 35 trip"; the trade office not released, so only test saves): each ship's trips put at its place for TripLog, as many as fit, newest
        // first as they were (the newest kept).
        private static void Restride()
        {
            var s = World.State?.office; int n = World.State?.playerFleet?.Count ?? 0; if (s == null || n == 0) return;
            int[] R(int[] a)
            {
                if (a == null || a.Length == n * TripLog || a.Length % n != 0) return a;
                int per = a.Length / n; var r = new int[n * TripLog];
                for (int i = 0; i < n; i++) Array.Copy(a, i * per, r, i * TripLog, Math.Min(per, TripLog));
                return r;
            }
            s.fleetTripDay = R(s.fleetTripDay); s.fleetTripPort = R(s.fleetTripPort); s.fleetTripCoin = R(s.fleetTripCoin); s.fleetTripCoins = R(s.fleetTripCoins);
            s.fleetTripGood = R(s.fleetTripGood); s.fleetTripCrates = R(s.fleetTripCrates);
        }
        // ---- The simulation's hooks (NewTraders.cs Fleet) ----
        // Waits in port: its route not set yet, its office not at work (seized or closed, or without its clerk: Office.Working), or its fee unpaid.
        internal static bool Held(BoatState b)
        {
            int i = IndexOf(b); var s = World.State?.office; if (i < 0 || s?.fleetHouse == null || i >= s.fleetHouse.Length) return false;
            return b.destination < 0 && (Unassigned(i) || !Office.Working(s.fleetHouse[i], OfficeUpgrade.Fleet) || s.fleetHeld != null && i < s.fleetHeld.Length && s.fleetHeld[i] != 0);
        }
        private static readonly Dictionary<BoatState, double> sales = new Dictionary<BoatState, double>();
        // The goods of the sale under way at the ship's port (Arrived reads and clears it): each good's value and crates.
        private sealed class Sold { internal readonly Dictionary<int, double> Value = new Dictionary<int, double>(); internal readonly Dictionary<int, int> Crates = new Dictionary<int, int>(); }
        private static readonly Dictionary<BoatState, Sold> soldGoods = new Dictionary<BoatState, Sold>();
        // Each crate the ship buys or sells, in value units (coins over the daily rate); a sold crate's good (Fleet's Arrive; -1: not told).
        internal static void Trade(BoatState b, int currency, double coins, bool buying, int good = -1)
        {
            int i = IndexOf(b); if (i < 0 || currency < 0 || currency > 3) return;
            double value = coins / Math.Max(1e-9, Fx.State.smooth[currency]);
            if (buying) { var s = Office.State; s.fleetCost[i] = (int)Math.Min(int.MaxValue, s.fleetCost[i] + Math.Round(value)); return; }
            sales[b] = (sales.TryGetValue(b, out var v) ? v : 0) + value;
            if (good < 0) return;
            if (!soldGoods.TryGetValue(b, out var sold)) soldGoods[b] = sold = new Sold();
            sold.Value[good] = (sold.Value.TryGetValue(good, out var gv) ? gv : 0) + value; sold.Crates[good] = (sold.Crates.TryGetValue(good, out var gc) ? gc : 0) + 1;
        }
        // The ship's trip settled at its port: the share of its profit into the treasury, or out of it on a loss; then the kind's new shape, if changed.
        internal static void Arrived(BoatState b, int saleCurrency)
        {
            int i = IndexOf(b); if (i < 0) return;
            var s = Office.State; int house = s.fleetHouse[i], region = Office.Region(house), coin = Mathf.Clamp(saleCurrency, 0, 3);
            double sold = sales.TryGetValue(b, out var v) ? v : 0; sales.Remove(b);
            double profit = sold - s.fleetCost[i]; s.fleetCost[i] = 0;
            double share = profit * Share(s.fleetKind[i], b.units);
            int coins = (int)Math.Floor(Math.Abs(share) * Fx.State.smooth[coin]), t = region * 4 + coin;
            if (share >= 0) { s.treasury[t] = (int)Math.Min(int.MaxValue, (long)s.treasury[t] + coins); s.earned[t] = (int)Math.Min(int.MaxValue, (long)s.earned[t] + coins); OfficeRecords.Add(house, RecordKind.Trip, coin, coins); }
            else { int taken = Math.Min(coins, s.treasury[t]); s.treasury[t] -= taken; OfficeRecords.Add(house, RecordKind.Trip, coin, -taken); }
            s.fleetLife[i] = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)s.fleetLife[i] + (long)Math.Round(share)));
            if (!Fitted()) Fit();
            int signed = share >= 0 ? coins : -coins;
            // the ship's whole life's tallies (the Treasury book's Trade fleet page): this port's arrival, each good's crates sold; the trip's cargo
            // of the most value (its history row) and, for a trip with cargo, the ship's biggest trip by its share's value
            soldGoods.TryGetValue(b, out var byGood); soldGoods.Remove(b);
            if (b.destination >= 0) s.fleetPorts[i] = Rules.Tally(s.fleetPorts[i], b.destination, 1);
            int top = -1; double most = double.MinValue;
            if (byGood != null) foreach (var kv in byGood.Value) { s.fleetGoods[i] = Rules.Tally(s.fleetGoods[i], kv.Key, byGood.Crates[kv.Key]); if (kv.Value > most) { most = kv.Value; top = kv.Key; } }
            // (a trip logged with no port would end the ship's history rows there: TripRows stops at port 0)
            if (b.goods != null && b.goods.Length > 0 && b.destination >= 0)
            {
                // "Last trip" is the last trip with cargo, as the history: one with none (Depart's fallback to the closest port) left it at
                // 0 and the Fleet tab read "none yet" (the deep review of 2026-11-09)
                s.fleetLast[i] = (int)Math.Round(share); s.fleetLastCoin[i] = coin; s.fleetLastCoins[i] = signed;
                LogTrip(i, b.destination, coin, signed, top, top >= 0 ? byGood.Crates[top] : 0);
                int value = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Round(share)));
                if (s.fleetBestPort[i] == 0 || value > s.fleetBestValue[i]) { s.fleetBestPort[i] = b.destination + 1; s.fleetBestCoin[i] = coin; s.fleetBestCoins[i] = signed; s.fleetBestValue[i] = value; }
            }
            Roll(i, GameState.day); s.fleetSums[i * Window + GameState.day % Window] += (int)Math.Round(share);
            // (a ship with no route, kind -1, is held in port and never arrives; guarded all the same: Shape would read Groups[-1])
            if (s.fleetKind[i] >= 0 && b.group != Groups[Mathf.Clamp(s.fleetKind[i], 0, 4)]) Shape(b, house, s.fleetHull[i], s.fleetKind[i]);
        }
        // A trip with a sale into the ship's trade history (the Treasury book's Trade fleet page), newest first, the oldest of TripLog dropped.
        // its cargo of the most value on the trip (its good, -1 none, and its crates)
        private static void LogTrip(int i, int port, int coin, int coins, int good, int crates)
        {
            var s = Office.State;
            if (!Fitted()) Fit();
            int at = i * TripLog;
            foreach (var a in new[] { s.fleetTripDay, s.fleetTripPort, s.fleetTripCoin, s.fleetTripCoins, s.fleetTripGood, s.fleetTripCrates }) Array.Copy(a, at, a, at + 1, TripLog - 1);
            s.fleetTripDay[at] = GameState.day; s.fleetTripPort[at] = port + 1; s.fleetTripCoin[at] = coin; s.fleetTripCoins[at] = coins;
            s.fleetTripGood[at] = good + 1; s.fleetTripCrates[at] = crates;
        }
        // The ship's trade history rows, newest first: the day, the port it sold at, its cargo of the most value and its crates, its share in the
        // share's coin (the coin's short name, as the Treasury book's records page writes it); none before its first sale.
        internal static List<string[]> TripRows(int i)
        {
            var s = World.State?.office; var rows = new List<string[]>(); if (s?.fleetTripPort == null || i < 0) return rows;
            for (int r = 0; r < TripLog; r++)
            {
                int at = i * TripLog + r; if (at >= s.fleetTripPort.Length || s.fleetTripPort[at] <= 0) break;
                int coins = at < s.fleetTripCoins.Length ? s.fleetTripCoins[at] : 0, coin = at < s.fleetTripCoin.Length ? Mathf.Clamp(s.fleetTripCoin[at], 0, 3) : 0;
                int good = s.fleetTripGood != null && at < s.fleetTripGood.Length ? s.fleetTripGood[at] - 1 : -1, crates = s.fleetTripCrates != null && at < s.fleetTripCrates.Length ? s.fleetTripCrates[at] : 0;
                rows.Add(new[] { at < s.fleetTripDay.Length ? s.fleetTripDay[at].ToString() : "", PortName(s.fleetTripPort[at] - 1), good >= 0 ? GoodName(good) + " x" + crates : "",
                    (coins > 0 ? "+" : "") + coins.ToString("N0") + " " + OfficeUiParts.ShortCoin(coin) });
            }
            return rows;
        }
        private static string PortName(int p) { var port = p >= 0 && Port.ports != null && p < Port.ports.Length ? Port.ports[p] : null; return port ? port.GetPortName() : "port " + p; }
        // A good's name as the game's trade book writes it (EconomyUI: the good's ShipItem name).
        internal static string GoodName(int g)
        {
            ShipItem item = null; try { item = PrefabsDirectory.instance ? PrefabsDirectory.instance.GetGood(g) : null; } catch { }
            return item ? item.name : "good " + g;
        }
        // The page's three rows from the ship's whole life in the fleet (the user, 2026-11-08 evening: "most visited port, most sold goods and biggest
        // profit trip(show destination and trip profit)"; "Whole life"): the port it arrived at most, the good it sold most crates of, its trip
        // with the biggest share by value (that trip's port and share in its coin); "none yet" before.
        internal static string[] LifeRows(int i)
        {
            var s = World.State?.office; const string none = "none yet";
            if (s == null || i < 0) return new[] { none, none, none };
            int port = Rules.Most(s.fleetPorts != null && i < s.fleetPorts.Length ? s.fleetPorts[i] : null, out int visits);
            int good = Rules.Most(s.fleetGoods != null && i < s.fleetGoods.Length ? s.fleetGoods[i] : null, out int crates);
            int best = s.fleetBestPort != null && i < s.fleetBestPort.Length ? s.fleetBestPort[i] - 1 : -1;
            int bestCoins = best >= 0 && i < s.fleetBestCoins.Length ? s.fleetBestCoins[i] : 0, bestCoin = best >= 0 && i < s.fleetBestCoin.Length ? Mathf.Clamp(s.fleetBestCoin[i], 0, 3) : 0;
            return new[] { port >= 0 ? PortName(port) + ", " + visits + (visits == 1 ? " visit" : " visits") : none,
                good >= 0 ? GoodName(good) + ", " + crates.ToString("N0") + (crates == 1 ? " crate" : " crates") : none,
                best >= 0 ? PortName(best) + ", " + (bestCoins > 0 ? "+" : "") + bestCoins.ToString("N0") + " " + OfficeUiParts.ShortCoin(bestCoin) : none };
        }
        // The ship's days in the fleet (the book's "Days in fleet"): since the day it joined.
        internal static int DaysIn(int i) { var s = World.State?.office; return s?.fleetJoined != null && i >= 0 && i < s.fleetJoined.Length ? Math.Max(0, GameState.day - s.fleetJoined[i]) : 0; }
        // The ship's 30 day sums moved on to a day (the days passed cleared).
        private static void Roll(int i, int day)
        {
            var s = Office.State; int last = s.fleetSumDay[i];
            if (day <= last) return;
            for (int d = last + 1; d <= day && d <= last + Window; d++) s.fleetSums[i * Window + d % Window] = 0;
            s.fleetSumDay[i] = day;
        }
        // About this many gold a day before the fee (the user's "real time profit estimate"): the ship's shares of the last 30 days over the days it
        // sailed in them (at least one), in gold at a coin's base value (value x 0.006, as the study reads it).
        internal static double Estimate(int i)
        {
            var s = World.State?.office; if (s?.fleetSums == null || i < 0) return 0;
            Roll(i, GameState.day);
            // the ship's days from its first route, not the days it waited for one (fleetStart; 0 in a test save before it, then from joining)
            int from = Math.Max(s.fleetJoined[i], s.fleetStart != null && i < s.fleetStart.Length ? s.fleetStart[i] : 0);
            int days = Mathf.Clamp(GameState.day - from + 1, 1, Window), sum = 0;
            for (int k = 0; k < Window; k++) sum += s.fleetSums[i * Window + k];
            // Before its first sale, its kind's early-game figure (§4.5's table, the study's 18 fleet ships on days 91-180).
            if (sum == 0 && s.fleetLast[i] == 0 && s.fleetLife[i] == 0 && i < Ships.Count) return Early(s.fleetKind[i], Ships[i].units);
            return sum * FxRules.Gold / days;
        }
        // Share x the kind's profit a day on days 91-180 (§3B "The fleet's targets raised"), before the fee.
        internal static double Early(int kind, int units) => kind == 0 ? 2.6 : kind == 1 ? 6.3 : kind == 2 ? (units >= 130 ? 19.7 : 13.9) : kind == 3 ? (units >= 130 ? 23.0 : 17.0) : 40.2;
        internal static double Gold(int value) => value * FxRules.Gold;
        // The ship's daily maintenance fee in its office port's coin at today's rate (its kind's fee in gold; at least 1 coin): what Midnight takes and
        // what the summaries show (the user, 2026-11-08: "Route fee -> Maintenance fee , and it should show it's local currency").
        internal static int FeeCoins(int house, int kind) => Math.Max(1, (int)Math.Ceiling(Fees[Mathf.Clamp(kind, 0, 4)] / FxRules.Gold * Fx.State.smooth[Office.Coin(house)] - 1e-9));
        // "/day", not "a day", and the coin without its region ("257 Lions/day", as the trade history writes coins), the estimate without "before the
        // fee" (the user, 2026-11-08 night: "change the fleet menu a day to /day to see if it can shorten it to 1 line only"; on obk-18's pictures, "/day"
        // alone left both on two lines: "shorten both").
        internal static string FeeText(int i) { var s = Office.State; return FeeCoins(s.fleetHouse[i], s.fleetKind[i]).ToString("N0") + " " + OfficeUiParts.ShortCoin(Office.Coin(s.fleetHouse[i])) + "/day"; }
        // The ship's last trip's share in the coin it was paid in ("Last trip show the last local currency it earn"); none before its first sale.
        internal static string LastTripText(int i)
        {
            var s = Office.State; bool has = s.fleetLastCoins != null && i < s.fleetLastCoins.Length && s.fleetLastCoin != null && i < s.fleetLastCoin.Length;
            if (!has || s.fleetLastCoins[i] == 0 && s.fleetLast[i] == 0) return "none yet";
            return (s.fleetLastCoins[i] > 0 ? "+" : "") + s.fleetLastCoins[i].ToString("N0") + " " + PlayerGold.GetCurrencyName(Mathf.Clamp(s.fleetLastCoin[i], 0, 3));
        }
        // Where the ship is (the Fleet tab, and the Traders tab's row the user asked for: "add a row for ship position behind the trader route"):
        // sailing and how far along its leg (trip and remaining count the same traffic units), or in port and when it sails (its wait, in traffic
        // units of 0.008 game hours: NewTraders.cs Wait), or waiting there and why.
        internal static string Position(int i)
        {
            if (i < 0 || i >= Ships.Count) return "unknown";
            var b = Ships[i];
            string P(int p) { var port = p >= 0 && Port.ports != null && p < Port.ports.Length ? Port.ports[p] : null; return port ? port.GetPortName() : "port " + p; }
            if (b.destination >= 0)
            {
                int done = b.trip > 0 ? Mathf.Clamp((int)Math.Round(100 * (1 - b.remaining / b.trip)), 0, 100) : -1;
                return "sailing from " + P(b.origin) + " to " + P(b.destination) + (done >= 0 ? ", " + done + "% of the way" : "");
            }
            var s = World.State?.office; int house = s?.fleetHouse != null && i < s.fleetHouse.Length ? s.fleetHouse[i] : 0;
            if (Held(b)) return "in port at " + P(b.origin) + ", waiting: " + (Unassigned(i) ? "its route is not set" : !Office.Live(house) ? "the office is closed" : !Office.Working(house, OfficeUpgrade.Fleet) ? "no clerk" : "the fee is unpaid");
            double hours = b.remaining * .008;
            return "in port at " + P(b.origin) + (hours >= .5 ? ", sails in about " + Math.Round(hours) + " h" : "");
        }
        // Midnight: each ship's route fee from its office region's treasury, in the region's coin at the daily rate; unpaid, the ship waits in port.
        // shortcut: a ship held for its fee resumes only at the next midnight's charge, even when the treasury is filled sooner; replace if the
        // player should see it sail as soon as the treasury can pay (charge it then, as Midnight does).
        internal static void Midnight(int day)
        {
            var s = World.State?.office; var l = World.State?.playerFleet; if (s?.fleetHouse == null || l == null) return;
            for (int i = 0; i < l.Count && i < s.fleetHouse.Length; i++)
            {
                // A seized or closed office's ships pay nothing (Q5); paused without a clerk they wait in port and still pay (the user, 2026-11-08:
                // "Trader still need to pay fee in port").
                int house = s.fleetHouse[i]; if (!Office.Live(house) || !Office.Has(house, OfficeUpgrade.Fleet)) { s.fleetHeld[i] = 0; continue; }
                // no route set yet: no fee (the user, 2026-11-08: "No fee"; the fee is by route), waiting for it holds the ship (Held)
                if (Unassigned(i)) { s.fleetHeld[i] = 0; Roll(i, day); continue; }
                int coin = Office.Coin(house), t = Office.Region(house) * 4 + coin;
                int fee = FeeCoins(house, s.fleetKind[i]);
                if (s.treasury[t] >= fee) { s.treasury[t] -= fee; s.fleetHeld[i] = 0; OfficeRecords.Add(house, RecordKind.Fees, coin, -fee); } else s.fleetHeld[i] = 1;
                Roll(i, day);
            }
        }
        // A region's bankruptcy (Q6): its offices' traders go, with nothing paid.
        internal static void Bankruptcy(int region)
        {
            var s = World.State?.office; var l = World.State?.playerFleet; if (s?.fleetHouse == null || l == null) return;
            for (int i = l.Count - 1; i >= 0; i--) if (i < s.fleetHouse.Length && (region < 0 || Office.Region(s.fleetHouse[i]) == region)) RemoveAt(i);
        }
    }
    // What Shipwright and Transport calls by name for its "Add to trade fleet" button (§4.5; keep these names stable, guide.md §1): why a ship of a
    // hull can't join from a yard of a region (null: it can), and the joining itself (the ship's id, or -1).
    public static class OfficeFleetApi
    {
        public static string Refusal(int region, int hull) { try { return OfficeFleet.Refusal(region, hull); } catch (Exception e) { return e.Message; } }
        public static int Add(int region, int hull, string name, string rig) { try { return OfficeFleet.Add(region, hull, name, rig); } catch (Exception e) { Plugin.Log.LogWarning("Trade office: a ship not added to the fleet: " + e.Message); return -1; } }
        // Why the ship's sails keep it out of the fleet (the parked ship, before its fee is charged), else null: a sail a trader copy can't draw,
        // MoreSailwindSails' Fisherman sails (Rules.FleetSailRefused; the user, 2026-11-09: "refuse sail from this mod to be admit to trader fleet.").
        // Two lines of at most 24 characters (Shipwright and Transport's scroll). Added 2026-11-09; an older Transport doesn't ask (Add refuses too).
        public static string SailRefusal(GameObject boat)
        {
            try
            {
                if (!boat) return null;
                foreach (var s in boat.GetComponentsInChildren<Sail>(true)) if (s && Rules.FleetSailRefused(s.sailName)) return Rules.FleetSailRefusal;
                return null;
            }
            catch (Exception e) { return e.Message; }
        }
        // The ship's rig as it stands (Transport's parked ship), the text Add takes: Data/trader_rigs.txt's form, so TraderRigs.Parse reads it. Its part
        // options carry the hull's options as well as the masts' (the user's list item 5: "Trade fleet's ship should save the hull option also, not
        // only rig"; again 2026-11-08: "remember the hull option need to be copy also"). Heights on the game's own masts: Shipyard Expansion's
        // added length taken off, as TraderRigs.PutOn adds it back. null when the ship has no rig record. The plan line's hull is the boat's save
        // number here (a Transport copy's is its block, 1010 and up); Add puts its own hull argument there (OfficeFleet.WithHull).
        public static string RigOf(GameObject boat)
        {
            try
            {
                var custom = boat ? boat.GetComponent<SaveableBoatCustomization>() : null; var d = custom ? custom.GetData() : null; if (d == null) return null;
                var refs = boat.GetComponent<BoatRefs>(); var so = boat.GetComponent<SaveableObject>(); var inv = System.Globalization.CultureInfo.InvariantCulture;
                var text = new System.Text.StringBuilder("plan fleet ").Append(so ? so.sceneIndex : 0).Append(' ').Append(string.Join(",", (d.partActiveOptions ?? new List<int>()).Select(o => o.ToString(inv)).ToArray())).Append(" -\n");
                // a hull Shipyard Expansion changed: the rig needs it (OfficeFleet.Loaded), and its heights stay as the game gives them on its longer
                // masts (TraderRigs.PutOn puts them on as they are). Its added length can't be taken off on a Shipwright and Transport copy: it
                // records a copy's masts, already lengthened, as their own, so MastAdded reads 0 there and PutOn would raise the ship's sails twice.
                bool se = TraderRigs.ShipyardExpansionChanged(boat.GetComponent<BoatCustomParts>());
                if (se) text.Append("needs ").Append(TraderRigs.ShipyardExpansionGuid).Append('\n');
                var onMast = new Dictionary<int, int>(); var modSails = new HashSet<int>();
                foreach (var s in d.sails ?? new List<SaveSailData>())
                {
                    // a sail from another mod, once per index (the user, 2026-11-09: "ship should be allowed to add into trade fleet even if it use
                    // modded sail and hull option"): drawn on the fleet ship, and the ship leaves the fleet, 80% paid, at a load without it (their
                    // answer, "Leaves the fleet, 80% paid"; OfficeFleet.Loaded through TraderRigs.Missing)
                    if (s.prefabIndex >= TraderRigs.FirstModSail && modSails.Add(s.prefabIndex)) { var line = TraderRigs.ModSailLine(s.prefabIndex); if (line != null) text.Append(line).Append('\n'); }
                    float height = s.installHeight - (!se && refs && s.mastIndex >= 0 && s.mastIndex < refs.masts.Length ? TraderRigs.MastAdded(refs.masts[s.mastIndex]) : 0f);
                    text.Append("sail ").Append(s.prefabIndex.ToString(inv)).Append(' ').Append(s.mastIndex.ToString(inv)).Append(' ').Append(height.ToString("R", inv)).Append(' ').Append(s.minAngle.ToString("R", inv)).Append(' ')
                        .Append(s.maxAngle.ToString("R", inv)).Append(' ').Append(s.sailColor.ToString(inv)).Append(' ').Append(s.scaleY.ToString("R", inv)).Append(' ').Append(s.scaleZ.ToString("R", inv)).Append('\n');
                    // Shipyard Expansion's own texture on this sail when it isn't its prefab's (the user, 2026-11-08 night: "can this be fix"; FleetCompat.SailTexture):
                    // the k-th sail on its mast, as GetData lists them; not a "needs" line (without Shipyard Expansion the sail shows its own texture)
                    int k = onMast.TryGetValue(s.mastIndex, out var n) ? n : 0; onMast[s.mastIndex] = k + 1;
                    var sail = FleetCompat.SailOf(refs, s.mastIndex, k, s.prefabIndex);
                    var texture = FleetCompat.SailTexture(sail, s.prefabIndex);
                    if (texture != null) text.Append("texture ").Append(texture).Append('\n');
                    // and its Shipyard Expansion angle when it isn't 0, and its jib side when flipped (the user, asked whether to carry them: "Look into it")
                    FleetCompat.SailShape(sail, out float angle, out bool flipped);
                    if (!float.IsNaN(angle)) text.Append("angle ").Append(angle.ToString("R", inv)).Append('\n');
                    if (flipped) text.Append("flipped\n");
                }
                return text.ToString();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Trade office: a ship's rig not read: " + e.Message); return null; }
        }
    }

    // ---- Unique items (§4.9; the user's Q25-Q27 and Q34-Q36) ----
    // A fixed table (the keepers and spawners load only with their islands): the items of the user's list, each with its own label (the game names
    // three "lantern" and seven "plant"), and in a tab of their own the regional variants of all three regions (Q35's "only show the the items that
    // is not at your region" lifted by the user, 2026-11-09: "Also allow unique item purchase to purchase local region item again"). The price: the item's value x 1.1 in the office port's coin at the daily rate, no reputation discount (Q26), at least
    // 1 coin. Bought, it is made 1 m over the office's drop point in the pose its vanilla shop spawner gives (probe officeposes), owned and saved:
    // the heap (the user: "Yes, keep the heap"). The modded items (Q36) in a tab of their own, grouped by mod (ModdedList).
    internal static class OfficeItems
    {
        internal sealed class Entry { internal int Prefab, Region = -1; internal string Label, Sellers, Mod; }
        private static Entry U(int p, string label, string sellers) => new Entry { Prefab = p, Label = label, Sellers = sellers };
        private static Entry R(int p, int region, string label, string sellers) => new Entry { Prefab = p, Region = region, Label = label + " (" + BookUI.RegionName(region) + ")", Sellers = sellers };
        internal static readonly Entry[] Unique = {
            U(96, "Energy elixir", "Alchemist's Island, Sanctuary, Fire Fish Town, Sen'na"), U(97, "Snake oil", "Alchemist's Island, Sen'na"), U(98, "Whimsy water", "Sen'na"),
            U(40, "Bottle of 10 sips (empty)", "Al'Nilem"), U(160, "Big spyglass", "Sunspire"), U(133, "Big lantern", "Firefly Grotto"),
            U(163, "Sun totem", "Turtle Island"), U(164, "Rain totem", "On'na"),
            U(190, "Potted plant 1", "Saffron Island"), U(191, "Potted plant 2", "Saffron Island"), U(192, "Potted plant 3", "Saffron Island"), U(193, "Potted plant 4", "Saffron Island"),
            U(194, "Small potted plant 1", "Saffron Island"), U(195, "Small potted plant 2", "Saffron Island"), U(196, "Big potted plant", "Saffron Island"),
            U(134, "Blue lantern", "Kicia Bay, Fire Fish Town"), U(137, "Big ship model", "Kicia Bay"), U(138, "Small ship model", "Kicia Bay"),
            U(112, "Red lantern", "Dragon Cliffs"), U(113, "Green lantern", "Dragon Cliffs, Sage Hills, Sen'na") };
        internal static readonly Entry[] Regional = {
            R(93, 1, "Chip log", "Dragon Cliffs, Gold Rock City"), R(92, 2, "Chip log", "Fort Aestrin, Chronos"),
            R(83, 0, "Chronocompass", "Gold Rock City"), R(84, 1, "Chronocompass", "Dragon Cliffs"), R(85, 2, "Chronocompass", "Fort Aestrin"),
            R(170, 0, "Chronometer", "Gold Rock City"), R(171, 1, "Chronometer", "Dragon Cliffs"), R(172, 2, "Chronometer", "Fort Aestrin, Chronos"),
            R(80, 0, "Compass", "Gold Rock City, Neverdin, Oasis"), R(81, 1, "Compass", "Dragon Cliffs, Crab Beach, New Port, Turtle Island, Dead Cove"), R(82, 2, "Compass", "Fort Aestrin, Siren Song, Chronos"),
            R(60, 0, "Bed", "Gold Rock City"), R(61, 1, "Bed", "Dragon Cliffs, Kicia Bay"), R(62, 2, "Bed", "Fort Aestrin"), R(63, 2, "Bed with curtains", "Fort Aestrin"),
            R(66, 0, "Small table", "Gold Rock City, Neverdin"), R(67, 0, "Large table", "Gold Rock City"), R(68, 1, "Small table", "Dragon Cliffs"), R(69, 1, "Large table", "Dragon Cliffs, Kicia Bay"),
            R(64, 2, "Small table", "Fort Aestrin"), R(65, 2, "Large table", "Fort Aestrin"),
            R(78, 0, "Chair", "Gold Rock City"), R(77, 1, "Chair", "Dragon Cliffs, Kicia Bay"), R(74, 2, "Small chair", "Fort Aestrin"), R(75, 2, "Large chair", "Fort Aestrin"), R(76, 2, "Large chair 2", "Fort Aestrin"),
            R(185, 0, "Wall shelf", "Gold Rock City"), R(186, 1, "Wall shelf", "Dragon Cliffs"), R(187, 2, "Wall shelf", "Fort Aestrin"),
            R(107, 0, "Stove", "Gold Rock City"), R(106, 1, "Stove", "Dragon Cliffs, Kicia Bay"), R(105, 2, "Stove", "Fort Aestrin"),
            R(377, 0, "Smoker", "Gold Rock City"), R(378, 1, "Smoker", "Dragon Cliffs, Kicia Bay"), R(379, 2, "Smoker", "Fort Aestrin"),
            R(374, 0, "Drying rack", "Gold Rock City"), R(375, 1, "Drying rack", "Dragon Cliffs, Kicia Bay"), R(376, 2, "Drying rack", "Fort Aestrin"),
            R(110, 0, "Lantern", "many ports"), R(111, 1, "Yellow lantern", "many ports"), R(114, 2, "Oil lantern", "many ports"),
            R(300, 0, "Pipe", "many ports"), R(301, 1, "Pipe", "many ports"), R(302, 2, "Pipe", "many ports"),
            R(370, 0, "Slicing knife", "many ports"), R(371, 1, "Slicing knife", "many ports"), R(372, 2, "Slicing knife", "many ports"),
            R(382, 0, "Kettle", "many ports"), R(383, 1, "Kettle", "many ports"), R(384, 2, "Kettle", "many ports") };
        // Every region's variants, the office's own included (the user, 2026-11-09: "Also allow unique item purchase to purchase local region item
        // again", in place of Q35's "only show the the items that is not at your region"); house kept for the callers.
        internal static List<Entry> List(int house, bool regional) => regional ? Regional.ToList() : Unique.ToList();
        // Every shop item of the user's mods (Q25, Q36; the user, 2026-11-08: "add all the modded item to be purchasable instead. not limited to
        // unique item"): §3B's 87 items sold in only one place and the ones sold in several (§3B "Every modded shop item", read from each mod's code
        // and asset bundles). Each shown only when its mod is loaded and its prefab stands at that index (looked up at each refresh of the items
        // page, OfficeItemsPage, twice a second while it is open: the mods register their items after loading). Its label the item's own name and
        // where ("several ports" for one sold in more than one: S), its
        // page's "Sold at" every port; the price 10% over what its shop charges (Q34): Realistic Skies' atlas sells at 0.8 of its value, its almanac
        // and quintant at 0.8 in the observatory and 1.0 at the merchants (the cheaper taken). Hanging Hammock and Sail Damage set their item's index
        // from their config: read from the item they registered (Found). Left out: items no shop sells (Wind Totem's quest reward, the BFO 5000 shown
        // at Bone Island, Windicators' 513, 521, 522, 525, Tobacco's 611-613) and this mod's own stock.
        // shortcut: a fixed table, so a mod not in it (or a later version's new items) isn't listed; replace with a read of the shop spawners if
        // modded items must be found without a table.
        private sealed class Modded { internal string Guid, Sellers, Where; internal int[] Prefabs; internal float Mult = 1f; internal string[] Labels; internal Func<GameObject> Find; }
        private const string Cities = "Gold Rock City, Dragon Cliffs, Fort Aestrin";
        private static Modded M(string guid, string sellers, params int[] prefabs) => new Modded { Guid = guid, Sellers = sellers, Prefabs = prefabs };
        private static Modded S(string guid, string sellers, params int[] prefabs) => new Modded { Guid = guid, Sellers = sellers, Where = "several ports", Prefabs = prefabs };
        // An item whose index its mod takes from its config: the prefab it registered, held in that mod's static field.
        private static Modded Found(string guid, string sellers, string type, string field) => new Modded { Guid = guid, Sellers = sellers, Where = "several ports", Prefabs = new int[0], Find = () =>
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(guid, out var info) || info?.Instance == null) return null;
            var f = info.Instance.GetType().Assembly.GetType(type)?.GetField(field, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            return f?.GetValue(null) as GameObject;
        } };
        private static readonly Modded[] modded = {
            M("com.nandbrew.Windicators", "Gold Rock City", 507, 510, 509, 503, 505, 520), M("com.nandbrew.Windicators", "Dragon Cliffs", 501, 502, 504, 515, 523, 518, 517),
            M("com.nandbrew.Windicators", "Fort Aestrin", 506, 511, 508, 516, 519, 524, 514), S("com.nandbrew.Windicators", Cities, 512),
            M("com.kemy.kemyfurniture", "Gold Rock City", 460, 462, 461, 463, 464, 476, 465, 468, 484, 487, 479, 480, 485, 486),
            M("com.kemy.kemyfurniture", "Dragon Cliffs", 469, 470, 471, 472, 473, 482, 474, 475, 478, 477),
            M("com.kemy.kemyfurniture", "Fort Aestrin", 455, 458, 459, 452, 453, 481, 451, 467, 483, 466), S("com.kemy.kemyfurniture", Cities, 454, 456, 457),
            M("DogEggz.Cigar", "Sage Hills", 610), M("DogEggz.Cigar", "Neverdin", 615), M("DogEggz.Cigar", "Siren Song", 616), M("DogEggz.Cigar", "Crab Beach", 617),
            M("DogEggz.Cigar", "Gold Rock City", 618, 627, 630), M("DogEggz.Cigar", "Fort Aestrin", 619, 625, 626, 629), M("DogEggz.Cigar", "Dragon Cliffs", 620, 628, 631),
            M("DogEggz.Cigar", "Eastwind", 621), M("DogEggz.Cigar", "island 39", 622), M("DogEggz.Cigar", "Oasis", 623), M("DogEggz.Cigar", "Chronos", 624), S("DogEggz.Cigar", Cities, 614),
            M("DogEggz.JetPump", "Al'Ankh Academy", 604, 605, 606), M("DogEggz.PowerfulOar", "Kicia Bay", 603), S("DogEggz.PowerfulOar", Cities, 602),
            // Both its tables are named "table" by Better Tables; the user, 2026-11-08: "make better table's table named "Leveled Table"" (the two differ
            // in their top, so numbered).
            new Modded { Guid = "com.nandbrew.bettertables", Sellers = "Fort Aestrin", Prefabs = new[] { 440, 441 }, Labels = new[] { "Leveled Table 1", "Leveled Table 2" } },
            M("com.dixiecapt.sailwind.tinystove", "Fort Aestrin", 32701),
            M("com.raddude.betterfishing", "Gold Rock City", 803, 806), M("com.raddude.betterfishing", "Dragon Cliffs", 804, 807), M("com.raddude.betterfishing", "Fort Aestrin", 805, 808),
            S("com.raddude.betterfishing", "Gold Rock City, Fort Aestrin, Dragon Cliffs, Fire Fish Town, Oasis, Happy Bay", 800, 802),
            M("com.kingcam16.realisticskies", "Kicia Bay", 705), new Modded { Guid = "com.kingcam16.realisticskies", Sellers = "Kicia Bay", Prefabs = new[] { 706 }, Mult = .8f },
            new Modded { Guid = "com.kingcam16.realisticskies", Sellers = Cities + ", Kicia Bay", Where = "several ports", Prefabs = new[] { 701, 702 }, Mult = .8f },
            S("com.kingcam16.realisticskies", Cities, 703),
            S("com.raddude.climate", Cities, 820, 821, 822, 823, 824, 825), S("com.larsonlogistics.sailwind.navsuite", Cities, 830, 831, 832),
            Found("fluffyjesus.sailwind.hanginghammock", Cities, "HangingHammock.HammockFactory", "Prefab"),
            Found("kap.saildamage", "Fort Aestrin, Gold Rock City, Dragon Cliffs, Kicia Bay, Siren Song, Neverdin, Dead Cove, New Port, Crab Beach, Al'Nilem, Sunspire", "SailDamage.Sailcloth", "Template") };
        private static readonly Dictionary<int, float> shopMult = new Dictionary<int, float>();
        internal static List<Entry> ModdedList()
        {
            var list = new List<Entry>(); var dir = PrefabsDirectory.instance ? PrefabsDirectory.instance.directory : null; if (dir == null) return list;
            foreach (var m in modded)
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(m.Guid, out var info)) continue;
                string mod = info?.Metadata?.Name ?? m.Guid;
                var prefabs = m.Prefabs;
                if (m.Find != null)
                {
                    GameObject found = null; try { found = m.Find(); } catch (Exception e) { Plugin.Log.LogWarning("Trade office: " + mod + "'s item not found: " + e.Message); }
                    // Only where the directory holds it: Sail Damage keeps its template's index when its slot is taken by another item (it then logs
                    // the clash and doesn't register it).
                    var fsp = found ? found.GetComponent<SaveablePrefab>() : null;
                    prefabs = fsp && fsp.prefabIndex >= 0 && fsp.prefabIndex < dir.Length && dir[fsp.prefabIndex] == found ? new[] { fsp.prefabIndex } : new int[0];
                }
                for (int i = 0; i < prefabs.Length; i++)
                {
                    int p = prefabs[i];
                    var go = p >= 0 && p < dir.Length ? dir[p] : null; var sp = go ? go.GetComponent<SaveablePrefab>() : null; var item = go ? go.GetComponent<ShipItem>() : null;
                    if (!sp || sp.prefabIndex != p || !item) continue;
                    shopMult[p] = m.Mult;
                    string name = m.Labels != null && i < m.Labels.Length ? m.Labels[i] : item.name;
                    list.Add(new Entry { Prefab = p, Label = name + " (" + (m.Where ?? m.Sellers) + ")", Sellers = m.Sellers, Mod = mod });
                }
            }
            // Each mod's items together, the mods by name, each mod's in the table's order (the user, 2026-11-08: "make the item from same mod group
            // together"; OrderBy keeps the order of equal keys).
            return list.OrderBy(e => e.Mod, StringComparer.OrdinalIgnoreCase).ToList();
        }
        // The world's up in each item's own frame, from its shop spawners (officeposes); +y where none was read.
        private static readonly Dictionary<int, Vector3> ups = new Dictionary<int, Vector3>();
        // Beds, tables and chairs stand on their own +z, as every one of their shop spawners stands them (officegrid, og-2: Gold Rock City's 7; officeposes
        // opose-3: the Emerald bed, large table and chair too), and so does the drying rack (opose-3: the Emerald one; Al'Ankh's and Aestrin's taken as
        // their family's, not read). Everything else read stands +y up (opose-3: the potions, the rain totem, the ship models, the blue, green and
        // yellow lanterns, the Emerald stove, smoker and pipe).
        internal static Vector3 Up(int prefab) => ups.TryGetValue(prefab, out var u) ? u : prefab >= 60 && prefab <= 78 || prefab >= 374 && prefab <= 376 ? Vector3.forward : Vector3.up;
        internal static Quaternion Pose(int prefab, float yaw) => Quaternion.Euler(0f, yaw, 0f) * Quaternion.FromToRotation(Up(prefab), Vector3.up);
        private static ShipItem Item(int prefab) { var d = PrefabsDirectory.instance ? PrefabsDirectory.instance.directory : null; return d != null && prefab >= 0 && prefab < d.Length && d[prefab] ? d[prefab].GetComponent<ShipItem>() : null; }
        internal static int Price(int house, Entry e)
        {
            var item = Item(e.Prefab); if (!item) return -1;
            float mult = shopMult.TryGetValue(e.Prefab, out var sm) ? sm : 1f;
            return Math.Max(1, (int)Math.Ceiling(ShopValue(e.Prefab, item.value) * mult * 1.1 * Fx.State.smooth[Office.Coin(house)] - 1e-9));
        }
        // What the item's shop really charges before reputation (Q34): its value, but with Kemy's Furniture loaded that mod's prices by name on the
        // vanilla furniture (its ShipItem patches, read in §3B: beds 950, tables 650, chairs 480, wall shelves 450).
        internal static int ShopValue(int prefab, int value)
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.kemy.kemyfurniture")) return value;
            if (prefab >= 60 && prefab <= 63) return 950;
            if (prefab >= 64 && prefab <= 69 || prefab == 72 || prefab == 73 || prefab == 440 || prefab == 441) return 650;
            if (prefab >= 74 && prefab <= 78) return 480;
            if (prefab >= 185 && prefab <= 187) return 450;
            return value;
        }
        internal static string Refusal(int house, Entry e)
        {
            if (!Office.Working(house, OfficeUpgrade.Items)) return "Not available";
            if (e == null || !Item(e.Prefab)) return "Not sold here";
            if (Office.Wages(house) > 0) return "Pay the clerk's wages first";
            int price = Price(house, e), coin = Office.Coin(house);
            if (PlayerGold.currency[coin] < price) return "Not enough " + PlayerGold.GetCurrencyName(coin);
            return null;
        }
        internal static ShipItem Buy(int house, Entry e)
        {
            var why = Refusal(house, e); if (why != null) { Loans.Notify(why); return null; }
            var island = Office.IslandRoot(house); if (!island || !OfficeSpots.TryGet(house, OfficePart.Drop, out var drop)) { Loans.Notify("Not available"); return null; }
            int price = Price(house, e), coin = Office.Coin(house);
            PlayerGold.currency[coin] -= price;
            if (DayLogs.instance) DayLogs.instance.dayLogs[coin].LogTransaction(-price, TransactionCategory.otherItems);
            var at = island.TransformPoint(drop.Position) + Vector3.up;
            float yaw = (island.rotation * drop.Turn).eulerAngles.y;
            var go = UnityEngine.Object.Instantiate(PrefabsDirectory.instance.directory[e.Prefab], at, Pose(e.Prefab, yaw), FloatingOriginManager.instance.transform);
            var item = go.GetComponent<ShipItem>(); item.sold = true; go.GetComponent<SaveablePrefab>().RegisterToSave();
            // The gold sound only, no notice (the user, 2026-11-09: "remove the notification when buy item from office").
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayGoldSound();
            Plugin.Log.LogInfo("Trade office: " + e.Label + " (prefab " + e.Prefab + ") bought at the " + Pledges.Name(house) + " for " + price + " " + PlayerGold.GetCurrencyName(coin) + ".");
            return item;
        }
    }

    public sealed class CarrierAtCamera : MonoBehaviour
    {
        private void LateUpdate() { var cam = Camera.main; if (cam) transform.position = cam.transform.position; }
    }

    // ---- The fixed spots (§3B, the user's OK on 2026-11-08: "OK, all six"; poses from run os-22) ----
    internal enum OfficePart { Table, Clerk, Paper, Book, Chest, Drop, Sign, CartDude, Cart }
    internal struct OfficePose
    {
        internal OfficePart Part; internal Vector3 Position, Scale; internal Quaternion Turn;
    }
    internal static class OfficeSpots
    {
        // Island-local position and turn and the world scale; the Cart row is its pose under the cart dude's root (its own scale kept).
        private static OfficePose P(OfficePart part, float x, float y, float z, float qx, float qy, float qz, float qw, float sx, float sy, float sz) =>
            new OfficePose { Part = part, Position = new Vector3(x, y, z), Turn = new Quaternion(qx, qy, qz, qw), Scale = new Vector3(sx, sy, sz) };
        private static readonly Dictionary<int, OfficePose[]> poses = new Dictionary<int, OfficePose[]>
        {
            { 201, new[] {
            P(OfficePart.Table, 1483.357f, 7.451f, -422.3593f, -0.63066f, -0.31979f, -0.31979f, 0.63066f, 1f, 1f, 1f),
            P(OfficePart.Clerk, 1481.746f, 6.16f, -423.4599f, 0f, -0.37511f, 0f, -0.92698f, 1f, 1f, 1f),
            P(OfficePart.Paper, 1482.609f, 7.58829f, -422.0315f, 0.0206f, -0.70681f, -0.70681f, -0.0206f, 0.65891f, 1f, 1f),
            P(OfficePart.Book, 1484.176f, 7.72261f, -422.9937f, 0.67395f, -0.21259f, -0.21161f, -0.67515f, 0.9f, 0.9f, 0.9f),
            P(OfficePart.Chest, 1482.219f, 7.5252f, -421.7002f, 0.57637f, -0.40825f, -0.4016f, -0.58296f, 0.5f, 0.5f, 0.5f),
            P(OfficePart.Drop, 1479.98f, 6.06f, -417.4f, 0f, 0.29519f, 0f, -0.95544f, 1f, 1f, 1f),
            P(OfficePart.Sign, 1485.678f, 8.25732f, -411.2229f, 0f, 0.46686f, 0f, 0.88433f, 1.31778f, 2f, 2.00008f),
            P(OfficePart.CartDude, 1489.543f, 5.78f, -415.3724f, 0f, -0.46686f, 0f, -0.88433f, 1f, 1f, 1f),
            P(OfficePart.Cart, 1.99001f, 0.26f, 0.89f, -0.43474f, -0.43474f, -0.55767f, 0.55767f, 0f, 0f, 0f) } },
            { 202, new[] {
            P(OfficePart.Table, -95.12244f, 3.64536f, -532.0798f, 0.67377f, -0.21456f, -0.21456f, -0.67377f, 1f, 1.2f, 1f),
            P(OfficePart.Clerk, -95.7446f, 3.17f, -531.4538f, 0f, 0.90354f, 0f, 0.42851f, 1f, 1f, 1f),
            P(OfficePart.Paper, -94.89932f, 3.77539f, -531.7388f, 0.3142f, 0.63347f, 0.63347f, -0.3142f, 0.65891f, 1f, 1f),
            P(OfficePart.Book, -95.31735f, 3.81955f, -532.4807f, -0.39253f, 0.58889f, 0.58734f, 0.39265f, 0.9f, 0.9f, 0.9f),
            P(OfficePart.Chest, -95.02292f, 3.74525f, -531.4939f, -0.19256f, 0.68508f, 0.67571f, 0.19236f, 0.5f, 0.5f, 0.5f),
            P(OfficePart.Drop, -97.60553f, 3.14f, -534.5459f, 0f, 0.38392f, 0f, 0.92336f, 1f, 1f, 1f),
            P(OfficePart.Sign, -93.79465f, 4.19515f, -535.6884f, 0f, 0.9284f, 0f, 0.37159f, 1.19803f, 1.81818f, 1.81819f),
            P(OfficePart.CartDude, -94.03271f, 2.89f, -536.4647f, 0f, 0.92339f, 0f, 0.38386f, 1f, 1f, 1f),
            P(OfficePart.Cart, 1.59418f, 0.57f, 0.79165f, 0.70174f, -0.08693f, 0.08693f, -0.70174f, 0f, 0f, 0f) } },
            { 203, new[] {
            P(OfficePart.Table, 35.25494f, 2.20823f, 50.04955f, -0.02302f, -0.70324f, -0.71019f, 0.02362f, 1f, 1f, 1f),
            P(OfficePart.Clerk, 36.16647f, 1.51f, 49.91183f, -0.00375f, 0.68776f, 0.0032f, -0.72592f, 1f, 1f, 1f),
            P(OfficePart.Paper, 35.25009f, 2.27724f, 49.67418f, 0.64849f, -0.27302f, -0.27606f, -0.65476f, 0.65891f, 1f, 1f),
            P(OfficePart.Book, 35.28165f, 2.32307f, 50.58957f, 0.5031f, 0.4916f, 0.49543f, -0.50968f, 0.9f, 0.9f, 0.9f),
            P(OfficePart.Chest, 35.37857f, 2.23751f, 49.35931f, 0.60143f, 0.3594f, 0.35938f, -0.6164f, 0.5f, 0.5f, 0.5f),
            P(OfficePart.Drop, 34.10489f, 1.5f, 45.66727f, 0f, 0.99732f, 0f, -0.07317f, 1f, 1f, 1f),
            P(OfficePart.Sign, 28.49008f, 2.76866f, 50.12129f, 0f, -0.65347f, 0f, 0.75695f, 1.31783f, 2f, 2f),
            P(OfficePart.CartDude, 19.99548f, 1.10001f, 45.74745f, 0f, -0.60316f, 0f, -0.79762f, 1f, 1f, 1f) } },
            { 204, new[] {
            P(OfficePart.Table, -9.08173f, -553.4279f, 5.77405f, 0f, 0f, -0.7156f, -0.69851f, 1f, 1f, 1f),
            P(OfficePart.Clerk, -9.32777f, -555.215f, 4.39002f, 0.01957f, 0.70684f, 0.70684f, 0.01957f, 1f, 1f, 1f),
            P(OfficePart.Paper, -8.32144f, -553.7532f, 5.7945f, 0f, 0f, 0.42714f, -0.90419f, 0.65891f, 1f, 1f),
            P(OfficePart.Book, -10.1985f, -553.7061f, 5.92999f, -0.00016f, 0.00109f, 0.99978f, -0.02085f, 0.9f, 0.9f, 0.9f),
            P(OfficePart.Chest, -7.83075f, -554.0832f, 5.67822f, -0.00244f, 0.00616f, 0.98053f, -0.19627f, 0.5f, 0.5f, 0.5f),
            P(OfficePart.Drop, -14.28584f, -553.6184f, 4.35004f, 0.50681f, 0.49309f, 0.49309f, 0.50681f, 1f, 1f, 1f),
            P(OfficePart.Sign, -10.98195f, -546.6915f, 6.52033f, 0.00218f, 0.7071f, 0.7071f, 0.00218f, 1.19803f, 1.81818f, 1.81819f),
            P(OfficePart.CartDude, -12.39359f, -545.9074f, 3.65002f, 0.01155f, 0.70701f, 0.70701f, 0.01155f, 1f, 1f, 1f),
            P(OfficePart.Cart, 1.59417f, 0.51f, 0.79167f, -0.70174f, 0.08693f, -0.08693f, 0.70174f, 0f, 0f, 0f) } },
            { 205, new[] {
            P(OfficePart.Table, -32.31406f, 14.17241f, -93.5509f, 0.7056f, 0.04607f, 0.04607f, -0.7056f, 1f, 1f, 1f),
            P(OfficePart.Clerk, -33.90811f, 13.18f, -93.54987f, 0f, 0.65011f, 0f, 0.75984f, 1f, 1f, 1f),
            P(OfficePart.Paper, -32.32385f, 14.42612f, -92.57526f, 0.05647f, 0.70485f, 0.70485f, -0.05647f, 0.65891f, 1f, 1f),
            P(OfficePart.Book, -32.01813f, 14.5257f, -94.27856f, -0.53745f, 0.45958f, 0.45834f, 0.53838f, 0.9f, 0.9f, 0.9f),
            P(OfficePart.Chest, -32.87021f, 14.32598f, -92.58008f, -0.41897f, 0.57227f, 0.56387f, 0.42311f, 0.5f, 0.5f, 0.5f),
            P(OfficePart.Drop, -31.30814f, 13.18f, -99.54987f, 0f, 0f, 0f, 1f, 1f, 1f, 1f),
            P(OfficePart.Sign, -26.27866f, 15.57732f, -90.66351f, 0f, 0.70711f, 0f, 0.70711f, 1.31778f, 2f, 2.00008f),
            P(OfficePart.CartDude, -24.90811f, 12.94f, -95.48987f, 0f, 0.70711f, 0f, 0.70711f, 1f, 1f, 1f),
            P(OfficePart.Cart, 1.65997f, 0.39f, 0.65999f, -0.70443f, 0.06145f, -0.16225f, 0.68824f, 0f, 0f, 0f) } },
            { 206, new[] {
            P(OfficePart.Table, -36.99274f, 3.94237f, -1100.723f, -0.56775f, -0.4215f, -0.4215f, 0.56775f, 1f, 1f, 1f),
            P(OfficePart.Clerk, -37.1454f, 3.23f, -1101.708f, 0f, -0.08706f, 0f, -0.9962f, 1f, 1f, 1f),
            P(OfficePart.Paper, -37.34946f, 3.95444f, -1100.855f, 0.30476f, -0.63806f, -0.63806f, -0.30476f, 0.65891f, 1f, 1f),
            P(OfficePart.Book, -36.49525f, 4.00795f, -1101.093f, 0.69662f, -0.11677f, -0.11679f, -0.69817f, 0.9f, 0.9f, 0.9f),
            P(OfficePart.Chest, -37.60294f, 3.9229f, -1100.888f, 0.68956f, -0.13753f, -0.13412f, -0.69828f, 0.5f, 0.5f, 0.5f),
            P(OfficePart.Drop, -33.17388f, 3.23f, -1100.344f, 0f, 0.61161f, 0f, -0.79116f, 1f, 1f, 1f),
            P(OfficePart.Sign, -35.49774f, 4.51866f, -1096.397f, 0f, 0.12697f, 0f, 0.99191f, 1.31783f, 2f, 2f),
            P(OfficePart.CartDude, -32.0435f, 2.73801f, -1095.676f, 0f, 0.61837f, 0f, -0.78589f, 1f, 1f, 1f),
            P(OfficePart.Cart, -0.008f, 0.522f, -0.882f, -0.43951f, -0.42994f, -0.55393f, 0.56138f, 0f, 0f, 0f) } },
        };
        internal static bool TryGet(int house, OfficePart part, out OfficePose pose)
        {
            pose = default;
            if (!poses.TryGetValue(house, out var list)) return false;
            foreach (var p in list) if (p.Part == part) { pose = p; return true; }
            return false;
        }
        // The office's table: the item whose mesh is the port table's (§4.2): Gold Rock City and Fort Aestrin table_1 (65), Dragon Cliffs and
        // Oasis table_3 (67), Serpent Isle and Firefly Grotto table_4 (72).
        internal static int TablePrefab(int house) => house == 201 || house == 203 ? 65 : house == 202 || house == 205 ? 67 : 72;
        // Put an object under the island at a pose: its world scale kept (three islands' roots are scaled 0.5; Serpent Isle's is turned 270
        // degrees about x, so the turn is a whole quaternion, never a yaw).
        internal static void Place(Transform t, Transform island, OfficePose pose)
        {
            t.SetParent(island, false);
            t.localPosition = pose.Position; t.localRotation = pose.Turn;
            var s = island.lossyScale;
            t.localScale = new Vector3(pose.Scale.x / s.x, pose.Scale.y / s.y, pose.Scale.z / s.z);
        }
    }

    // ---- The objects of the offices near the player ----
    internal static class OfficeSites
    {
        // Built within 450 m of the house, removed beyond 650 m (the house is drawn at that range; nothing of an office runs farther).
        private const float BuildRange = 450f, DropRange = 650f;
        private static readonly Dictionary<int, OfficeSite> sites = new Dictionary<int, OfficeSite>();
        private static float poll;
        internal static OfficeSite Site(int house) => sites.TryGetValue(house, out var s) && s ? s : null;
        internal static void Poll()
        {
            ClerkPlacer.Tick(); ClerkLook.Tick();
            if ((poll -= Time.unscaledDeltaTime) > 0) return; poll = 1;
            if (!World.Ready || Uninstall.Prepared || GameState.currentlyLoading || !GameState.playing && !Startup.BlocksGameplay) { if (!World.Ready || Uninstall.Prepared) Clear(); return; }
            OfficeCarts.Apply();
            var cam = Camera.main; if (!cam) return;
            foreach (int house in Office.Houses)
            {
                var b = Pledges.Find(house); if (!b) continue;
                float d = (b.transform.position - cam.transform.position).magnitude;
                var site = Site(house);
                bool wanted = Pledges.Owned(b);
                if (!wanted || d > DropRange) { if (site) Remove(house); continue; }
                if (!site && d < BuildRange) site = Make(house, b);
                if (site) site.Sync();
            }
        }
        private static OfficeSite Make(int house, PurchasableBoat b)
        {
            var go = new GameObject("trade office " + house);
            var site = go.AddComponent<OfficeSite>();
            if (!site.Init(house, b)) { UnityEngine.Object.Destroy(go); return null; }
            sites[house] = site;
            return site;
        }
        private static void Remove(int house) { if (sites.TryGetValue(house, out var s)) { if (s) s.Destroy(); sites.Remove(house); } }
        // A change of upgrades: the site redraws its parts at the next poll.
        internal static void Refresh(int house) { var s = Site(house); if (s) s.Sync(); poll = 0; }
        internal static void Clear() { foreach (var h in sites.Keys.ToList()) Remove(h); OfficePanel.Close(); }
    }

    // One house's office parts: the sign (until converted), the table and paper (an office), the money chest (the treasury). Each part is
    // a copy made under an inactive holder and woken once placed, so nothing runs before it stands where it belongs.
    public sealed class OfficeSite : MonoBehaviour
    {
        internal int house; private PurchasableBoat boat; private Transform island; private Port port;
        private GameObject sign, table, paper, chest; private TextMesh signText, signLabel; private LoanAction signButton;
        private float signTimer;
        internal bool Init(int index, PurchasableBoat b)
        {
            house = index; boat = b;
            port = Office.PortOf(index);
            // The port's island: its ancestor straight under the shifting world (the nearest island origin can be another island's).
            var t = port ? port.transform : null;
            while (t && t.parent && t.parent.name != "_shifting world") t = t.parent;
            island = t && t.parent ? t : null;
            if (!island) { Plugin.Log.LogWarning("Trade office: no island found for the " + Pledges.Name(index)); return false; }
            transform.SetParent(island, false);
            return true;
        }
        internal Transform Island => island;
        internal Port Port => port;
        internal void Destroy()
        {
            if (OfficePanel.IsOpenFor(house)) OfficePanel.Close();
            UnityEngine.Object.Destroy(gameObject);
        }
        // What should stand, from the state: the sign while the house isn't an office; the table and paper once it is; the chest with the treasury.
        internal void Sync()
        {
            bool office = Office.IsOffice(house) && Pledges.Owned(boat);
            // The sign only from the region's reputation 6 (choice 4, the user: "hide below rep 6").
            bool offered = !office && Pledges.Owned(boat) && Reputations.Level(Office.Region(house)) >= Rules.OfficeLevel;
            if (offered) { if (!sign) MakeSign(); } else if (sign) { UnityEngine.Object.Destroy(sign); sign = null; }
            if (office) { if (!table) MakeTable(); if (!paper) MakePaper(); } else { if (table) UnityEngine.Object.Destroy(table); if (paper) UnityEngine.Object.Destroy(paper); table = paper = null; }
            bool treasury = office && Office.Has(house, OfficeUpgrade.Treasury);
            if (treasury) { if (!chest) MakeChest(); } else if (chest) { UnityEngine.Object.Destroy(chest); chest = null; }
            bool clerk = office && Office.Has(house, OfficeUpgrade.Clerk);
            if (clerk) { if (!clerkRoot) MakeClerk(); } else if (clerkRoot) { UnityEngine.Object.Destroy(clerkRoot); clerkRoot = null; }
            // The cart dude while the contract works; paused without a clerk only he leaves and his cart stays (the user, 2026-11-08: "for cart
            // dude only NPC left, cart stay").
            bool cart = office && Office.Has(house, OfficeUpgrade.Cart);
            if (cart) { if (!cartDude) MakeCartDude(); if (cartDude) PauseCartDude(!Office.Working(house, OfficeUpgrade.Cart)); }
            else { if (cartDude) { UnityEngine.Object.Destroy(cartDude); cartDude = null; } cartSearched = false; CartDudePaused = false; }   // a remade one starts unpaused, as its flag
            bool coop = office && Office.Working(house, OfficeUpgrade.Cooperation);
            if (coop) { if (!book) MakeBook(); } else if (book) { UnityEngine.Object.Destroy(book); book = null; }
        }
        // The cooperation's trade book (§4.7): a working copy of the port dude's, marked as this office's.
        private GameObject book;
        internal GameObject Book => book;
        private void MakeBook()
        {
            book = Copy(MissionPart("ui_book")?.gameObject, OfficePart.Book, "trade office book", c =>
            {
                var b = c.GetComponentInChildren<GPButtonPortMissions>(true); if (!b) return false;
                b.gameObject.AddComponent<OfficeBookMark>().house = house;
                return true;
            });
        }
        // ---- The cart dude beside the door (§4.6; the user: "there will be a cart dude spawn in front of your office ... Even if the port don't have
        // cart transport in vanilla, spawn one anyway"; his spot the user's, §3B) ----
        // A working copy of the island's own cart dude (his CargoTransportDude opens the cart's screen; NPCAnimations moves him), found among the
        // loaded ones nearest the house, at his approved pose; his cart at its own row under him (Gold Rock City's switched on: the game keeps it
        // off; Fort Aestrin's as at his island). His carrier is the island's (island index, as the game's slots are), Serpent Isle's the one the
        // office adds (OfficeCarts). Looked for once per visit (the user, 2026-11-08: "don't keep scanning, only scan once when player is close
        // enough to island"): when the island's own scene has loaded (until then only its loaded flag is read); the site is made again on the next visit.
        private GameObject cartDude; private bool cartSearched;
        internal GameObject CartDude => cartDude;
        // Paused: everything of him but his cart switched off (his body and the rest), and his own colliders (the trigger that opens the cart's
        // screen) off; back on when a clerk works there again.
        internal bool CartDudePaused { get; private set; }
        private void PauseCartDude(bool paused)
        {
            if (!cartDude || CartDudePaused == paused) return;
            foreach (Transform child in cartDude.transform) if (child.name != "cart") child.gameObject.SetActive(!paused);
            foreach (var c in cartDude.GetComponents<Collider>()) c.enabled = !paused;
            CartDudePaused = paused;
        }
        private void MakeCartDude()
        {
            if (cartSearched) return;
            var island = port ? port.island : null; var horizon = island ? island.GetComponentInParent<IslandHorizon>() : null;
            if (!horizon || !OfficeSpots.TryGet(house, OfficePart.CartDude, out var pose)) return;
            int carrier = horizon.islandIndex;
            if (!OfficeCarts.Carrier(carrier)) return;
            if (!UnityEngine.SceneManagement.SceneManager.GetSceneByBuildIndex(horizon.islandIndex).isLoaded) return;
            cartSearched = true;
            var b = boat ? boat.transform.position : transform.position;
            var source = Resources.FindObjectsOfTypeAll<CargoTransportDude>().Where(d => d && d.gameObject.scene.IsValid() && !d.transform.IsChildOf(transform) && (d.transform.position - b).sqrMagnitude < 250f * 250f)
                .OrderBy(d => (d.transform.position - b).sqrMagnitude).FirstOrDefault();
            if (!source) { Plugin.Log.LogWarning("Trade office: no cart dude found near the " + Pledges.Name(house) + " to copy; none this visit."); return; }
            cartDude = Copy(source.gameObject, OfficePart.CartDude, "trade office cart dude", c =>
            {
                // Another mod's child already on the source (Talkative NPCs' bubble and hit talker) goes; that mod adds its own at Start.
                foreach (var t in c.GetComponentsInChildren<Transform>(true).Where(t => t.name.IndexOf("speech ui", StringComparison.OrdinalIgnoreCase) >= 0).ToList()) if (t) DestroyImmediate(t.gameObject);
                foreach (var m in c.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m && m.GetType().Name == "NPCHitTalker").ToList()) if (m) DestroyImmediate(m.gameObject);
                var dude = c.GetComponent<CargoTransportDude>(); if (!dude) return false;
                dude.carrierIndex = carrier;
                var cartPart = c.transform.Cast<Transform>().FirstOrDefault(t => t.name == "cart");
                if (cartPart && OfficeSpots.TryGet(house, OfficePart.Cart, out var cp)) { cartPart.localPosition = cp.Position; cartPart.localRotation = cp.Turn; }
                // Gold Rock City's cart: the object on, its renderers off in the game (os-18); switched on (the user: "GRC cart dude should have a cart").
                if (cartPart && house == 201) { cartPart.gameObject.SetActive(true); foreach (var r in cartPart.GetComponentsInChildren<Renderer>(true)) r.enabled = true; }
                c.SetActive(true);
                return true;
            });
            if (cartDude) Plugin.Log.LogInfo("Trade office: the " + Pledges.Name(house) + "'s cart dude copied from " + source.name + " (carrier " + carrier + ").");
        }
        // ---- The clerk (§4.3; the user: "don't need to be trimmed down, just normal npc", "Clerk should be fully animated like normal npc") ----
        // A copy of the port dude's own model (Modular NPC: its Animator, CharacterCustomizer, NPCAnimations and its 4 m trigger, ~720 clothes
        // parts), never of the port dude himself (PortDude.Awake would take over Port.dude, and his trigger hands in mission cargo). He stands on a
        // holder at his approved pose; NPCAnimations (breathing, the head turned to the player within its trigger) needs a parent, and the holder
        // is it. His look: an outfit of his region from the game's NPCs, picked once and saved; the port dude's own when there are none.
        private GameObject clerkRoot;
        internal GameObject Clerk => clerkRoot;
        private void MakeClerk()
        {
            var dude = port ? port.GetDude() : null;
            var model = dude ? dude.GetComponentsInChildren<NPCAnimations>(true).Select(a => a.transform).FirstOrDefault() : null;
            if (!model || !OfficeSpots.TryGet(house, OfficePart.Clerk, out var pose)) return;
            clerkRoot = new GameObject("trade office clerk");
            OfficeSpots.Place(clerkRoot.transform, transform, pose);
            if (ClerkSpot(out var moved, out var turned)) { clerkRoot.transform.localPosition = moved; clerkRoot.transform.localRotation = turned; }
            var hold = new GameObject("trade office hold"); hold.SetActive(false);
            var body = Instantiate(model.gameObject, hold.transform); body.name = model.name;
            // Another mod's child already on the source (Talkative NPCs' speech bubble, its hit talker) goes; the model's own children stay.
            foreach (Transform child in body.transform.Cast<Transform>().ToArray())
                if (child.name.IndexOf("speech", StringComparison.OrdinalIgnoreCase) >= 0 || child.GetComponents<MonoBehaviour>().Any(m => m && m.GetType().Name == "NPCHitTalker")) DestroyImmediate(child.gameObject);
            body.transform.SetParent(clerkRoot.transform, false);
            // Where the model stands under the port dude, and its world scale.
            body.transform.localPosition = dude.transform.InverseTransformPoint(model.position);
            body.transform.localRotation = Quaternion.Inverse(dude.transform.rotation) * model.rotation;
            var ws = model.lossyScale; var hs = clerkRoot.transform.lossyScale; body.transform.localScale = new Vector3(ws.x / hs.x, ws.y / hs.y, ws.z / hs.z);
            var guard = clerkRoot.AddComponent<ClerkGuard>(); guard.site = this; guard.model = body.transform;
            guard.kept = new HashSet<Transform>(body.GetComponentsInChildren<Transform>(true));
            UnityEngine.Object.Destroy(hold);
            body.SetActive(true);
            var talk = clerkRoot.AddComponent<ClerkTalk>(); talk.site = this;
            // Clicked to talk (the user: "start with a click on him"): a trigger for the game's pointer (its ray hits triggers, 1.8 m at most,
            // GoPointer.DoRaycast), his button on it. A trigger, so he stays as walk-through as before. On a child of its own with a renderer switched
            // off: a GoPointerButton requires a Renderer (adding one without failed, otrade-3). This capsule the size of a standing man only until
            // FitClerkArea puts his model's own shape there once he is dressed (and for good if that can't be made).
            var clickable = new GameObject("clerk button"); clickable.transform.SetParent(clerkRoot.transform, false);
            var hit = clickable.AddComponent<CapsuleCollider>(); hit.isTrigger = true; hit.direction = 1; hit.height = 1.8f; hit.radius = .3f; hit.center = new Vector3(0f, .9f, 0f);
            // No materials at all: the game's outline effect reads each of a pointed-at button's materials, and a new renderer's empty slot
            // threw in cakeslice.OutlineEffect.OnPreRender every frame (oc-3).
            var hidden = clickable.AddComponent<MeshRenderer>(); hidden.sharedMaterials = new Material[0]; hidden.enabled = false;
            var button = clickable.AddComponent<LoanAction>(); button.action = talk.Clicked; button.lookText = "Clerk";
            // His look text in one place, whichever part of him is pointed at (the user, 2026-11-09: "hint text showing up everywhere on clerk, could
            // you make it only in one place?"): the game draws a button's text at that button's own place (LookUI.ShowLookText), and his click area
            // is a button on each bone, so ClerkLookTextPatch moves it here. At chest height of a standing man until FitClerkArea puts it at his
            // model's chest.
            var text = new GameObject(ClerkTextName); text.transform.SetParent(clerkRoot.transform, false); text.transform.localPosition = new Vector3(0f, 1.25f, 0f);
            clickable.AddComponent<ClerkTextAt>().at = text.transform;
        }
        internal const string ClerkTextName = "clerk look text";
        // A new look (Player Model's screen) goes on a fresh copy of the port dude's model, not on the clerk that wears it now: his outfit
        // (TraderHulls.DressWhole) switched its parts on by itself and gave some of them its NPC's own meshes and materials, which the game's
        // CharacterCustomizer neither switches off nor knows (its ClearItems clears only what it switched on), so they stayed on under the new look:
        // the old look kept showing, a female look under the outfit's male parts, two hands drawn in one place (the user's video, 2026-11-09). His
        // place stays (MakeClerk reads his saved spot); the new copy is dressed by Dress, which puts the saved look on.
        internal void RemakeClerk()
        {
            if (clerkRoot) { UnityEngine.Object.Destroy(clerkRoot); clerkRoot = null; }
            if (Office.Has(house, OfficeUpgrade.Clerk)) MakeClerk();
        }
        // His click area (the user, 2026-11-08: "Same as clerk's model, same distance as pointer distance used to pick up cargo"): his drawn model's
        // own shape, moving with him. Each worn part (head, torso, each arm, hand and leg, the clothes) baked in his pose (BakeMesh: its vertices in
        // its own place and turn, its scale applied, as TraderHulls.BakeOutfit places them), its faces split into pieces of at most 128 corners (a
        // convex shell of 128 points has at most 252 triangles, under the 255 Unity cooks one to, so none is cut), each piece a convex trigger shell
        // on the nearest of its part's own bones, so it follows his idle animation, breathing and head turn (shells fitted once and left in place
        // missed his model by up to 4.7 cm, oc-7). A piece keeps whole faces, so together they cover every face of him; a flat piece (under 1 cm
        // thick along its faces' normal) is thickened 5 mm each way so it can be cooked. Each bone with pieces has its own clickable holder
        // ("clerk button <bone>": a renderer switched off with no materials, his LoanAction), as the game's pointer reads the button on the collider
        // it hits. The reach needs nothing of its own: the ray is the game's pointer ray that picks up cargo (GoPointer.DoRaycast: 1.8 m from the
        // pointer, mask -604165). Fitted once he is dressed (ClerkGuard) and again after Player Model's look (ClerkLook.Tick); his clerk guard
        // destroys the meshes and holders with him. If no shell can be made the first capsule stays.
        private const int ClerkPieceMost = 128;
        internal void FitClerkArea()
        {
            var button = clerkRoot ? clerkRoot.transform.Find("clerk button") : null;
            var model = clerkRoot ? clerkRoot.GetComponentInChildren<NPCAnimations>(true) : null;
            var guard = clerkRoot ? clerkRoot.GetComponent<ClerkGuard>() : null;
            var talk = clerkRoot ? clerkRoot.GetComponent<ClerkTalk>() : null;
            if (!button || !model || !guard || !talk) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var verts = new List<Vector3>(); var sub = new List<int>(); var tris = new List<int>(); var baked = new Mesh();
            var holders = new Dictionary<Transform, GameObject>(); var meshes = new List<Mesh>(); int parts = 0, shells = 0, points = 0; bool done = false;
            var lo = Vector3.one * float.MaxValue; var hi = -lo;   // his whole model's box, for his look text's place
            try
            {
                foreach (var s in model.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                {
                    if (!s.enabled || !s.gameObject.activeInHierarchy || !s.sharedMesh) continue;
                    var bones = (s.bones ?? new Transform[0]).Where(b => b).Distinct().ToArray(); if (bones.Length == 0) continue;
                    s.BakeMesh(baked); baked.GetVertices(verts); parts++;
                    var at = new Vector3[verts.Count]; for (int i = 0; i < at.Length; i++) at[i] = s.transform.position + s.transform.rotation * verts[i];
                    tris.Clear(); for (int m = 0; m < baked.subMeshCount; m++) { baked.GetTriangles(sub, m); tris.AddRange(sub); }
                    var pieces = new List<List<int>>(); ClerkPieces(at, tris, Enumerable.Range(0, tris.Count / 3).ToList(), pieces);
                    int k = 0;
                    foreach (var piece in pieces)
                    {
                        var pts = ClerkPiecePoints(at, tris, piece); if (pts.Count < 4) continue;
                        foreach (var p in pts) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
                        var centre = Vector3.zero; foreach (var p in pts) centre += p; centre /= pts.Count;
                        var bone = ClerkBone(bones, centre);
                        if (!holders.TryGetValue(bone, out var holder))
                        {
                            holder = new GameObject("clerk button " + bone.name); holder.transform.SetParent(bone, false);
                            var hidden = holder.AddComponent<MeshRenderer>(); hidden.sharedMaterials = new Material[0]; hidden.enabled = false;
                            holders[bone] = holder;
                        }
                        for (int i = 0; i < pts.Count; i++) pts[i] = holder.transform.InverseTransformPoint(pts[i]);
                        // The shell is cooked from the points alone; the triangles only make it a whole mesh.
                        var fan = new int[(pts.Count - 2) * 3];
                        for (int i = 0; i < pts.Count - 2; i++) { fan[i * 3] = 0; fan[i * 3 + 1] = i + 1; fan[i * 3 + 2] = i + 2; }
                        var mesh = new Mesh { name = "trade office clerk area " + s.name + " " + (k++) }; mesh.SetVertices(pts); mesh.triangles = fan; meshes.Add(mesh);
                        var shell = holder.AddComponent<MeshCollider>(); shell.convex = true; shell.isTrigger = true; shell.sharedMesh = mesh;
                        if (shell.bounds.size.sqrMagnitude <= 0f) { DestroyImmediate(shell); continue; }
                        shells++; points += pts.Count;
                    }
                }
                foreach (var h in holders.Values.ToList()) if (!h.GetComponent<MeshCollider>()) { holders.Remove(h.transform.parent); DestroyImmediate(h); }
                if (shells == 0) { Plugin.Log.LogWarning("Trade office: the clerk's click area kept as it was: " + parts + " parts drawn, no shell made."); return; }
                // His look text at his chest: the middle of his model's box across, 70% of its height up (the drawn model's own height, so a Player
                // Model look of another height moves it with him); one fixed point on him, not on a bone, so it holds still while he breathes.
                var text = clerkRoot.transform.Find(ClerkTextName);
                if (text) text.position = new Vector3((lo.x + hi.x) * .5f, lo.y + (hi.y - lo.y) * .7f, (lo.z + hi.z) * .5f);
                foreach (var h in holders.Values) { var b = h.AddComponent<LoanAction>(); b.action = talk.Clicked; b.lookText = "Clerk"; if (text) h.AddComponent<ClerkTextAt>().at = text; guard.kept?.Add(h.transform); }
                // The new shells stand; the old ones and the first capsule go.
                if (guard.holders != null) foreach (var h in guard.holders) if (h) Destroy(h);
                if (guard.area != null) foreach (var m in guard.area) if (m) Destroy(m);
                guard.holders = holders.Values.ToArray(); guard.area = meshes.ToArray(); done = true;
                var capsule = button.GetComponent<CapsuleCollider>(); if (capsule) Destroy(capsule);
                Plugin.Log.LogInfo("Trade office: the " + Pledges.Name(house) + "'s clerk's click area fitted to his model: " + parts + " parts, " + shells + " shells on " + holders.Count + " bones, " + points + " points, " + sw.Elapsed.TotalMilliseconds.ToString("F1") + " ms.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Trade office: the clerk's click area kept as it was: " + e.Message); }
            finally
            {
                Destroy(baked);
                if (!done) { foreach (var h in holders.Values) if (h) DestroyImmediate(h); foreach (var m in meshes) if (m) Destroy(m); }   // not taken into use
            }
        }
        // A part's faces split until each piece's corners are at most ClerkPieceMost: halved along the longest side of their centres' box, at the median.
        private static void ClerkPieces(Vector3[] at, List<int> tris, List<int> faces, List<List<int>> into)
        {
            var corners = new HashSet<int>(); foreach (int f in faces) { corners.Add(tris[f * 3]); corners.Add(tris[f * 3 + 1]); corners.Add(tris[f * 3 + 2]); }
            if (corners.Count <= ClerkPieceMost || faces.Count < 2) { into.Add(faces); return; }
            var centres = faces.Select(f => (at[tris[f * 3]] + at[tris[f * 3 + 1]] + at[tris[f * 3 + 2]]) / 3f).ToList();
            var lo = Vector3.one * float.MaxValue; var hi = -lo; foreach (var c in centres) { lo = Vector3.Min(lo, c); hi = Vector3.Max(hi, c); }
            var ext = hi - lo; int axis = ext.x >= ext.y && ext.x >= ext.z ? 0 : ext.y >= ext.z ? 1 : 2;
            var order = Enumerable.Range(0, faces.Count).OrderBy(i => centres[i][axis]).Select(i => faces[i]).ToList(); int half = order.Count / 2;
            ClerkPieces(at, tris, order.GetRange(0, half), into); ClerkPieces(at, tris, order.GetRange(half, order.Count - half), into);
        }
        // A piece's corners (the seams' doubles dropped, one per 1 mm cell), thickened 5 mm each way along its faces' normal when flatter than 1 cm.
        private static List<Vector3> ClerkPiecePoints(Vector3[] at, List<int> tris, List<int> faces)
        {
            var cells = new HashSet<Vector3Int>(); var pts = new List<Vector3>(); var normal = Vector3.zero;
            foreach (int f in faces)
            {
                Vector3 a = at[tris[f * 3]], b = at[tris[f * 3 + 1]], c = at[tris[f * 3 + 2]];
                normal += Vector3.Cross(b - a, c - a);
                foreach (var p in new[] { a, b, c }) if (cells.Add(new Vector3Int(Mathf.FloorToInt(p.x * 1000f), Mathf.FloorToInt(p.y * 1000f), Mathf.FloorToInt(p.z * 1000f)))) pts.Add(p);
            }
            var n = normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.up;
            float min = float.MaxValue, max = float.MinValue; foreach (var p in pts) { float d = Vector3.Dot(p, n); min = Mathf.Min(min, d); max = Mathf.Max(max, d); }
            if (max - min < .01f) { int m = pts.Count; for (int i = 0; i < m; i++) { pts.Add(pts[i] - n * .005f); pts[i] += n * .005f; } }
            return pts;
        }
        // The bone a piece follows: the nearest of its part's bones, each read as the segment to its nearest child among them (the game's meshes
        // aren't readable, so no bone weights).
        private static Transform ClerkBone(Transform[] bones, Vector3 at)
        {
            Transform best = bones[0]; float bestD = float.MaxValue;
            foreach (var b in bones)
            {
                var end = b.position; float near = float.MaxValue;
                foreach (var c in bones) if (c != b && c.parent == b) { float d = (c.position - b.position).sqrMagnitude; if (d < near) { near = d; end = c.position; } }
                var seg = end - b.position; float t = seg.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(at - b.position, seg) / seg.sqrMagnitude) : 0f;
                float dist = (at - (b.position + seg * t)).sqrMagnitude;
                if (dist < bestD) { bestD = dist; best = b; }
            }
            return best;
        }
        // Where the player put him (Q18), island-local; false until moved.
        internal bool ClerkSpot(out Vector3 position, out Quaternion turn)
        {
            position = Vector3.zero; turn = Quaternion.identity;
            var s = World.State?.office; int i = Office.Slot(house) * 7;
            if (s?.clerkSpot == null || s.clerkSpot.Length < 42 || i < 0) return false;
            turn = new Quaternion(s.clerkSpot[i + 3], s.clerkSpot[i + 4], s.clerkSpot[i + 5], s.clerkSpot[i + 6]);
            if (turn.x == 0 && turn.y == 0 && turn.z == 0 && turn.w == 0) return false;
            position = new Vector3(s.clerkSpot[i], s.clerkSpot[i + 1], s.clerkSpot[i + 2]);
            return true;
        }
        internal void SaveClerkSpot()
        {
            if (!clerkRoot) return;
            var s = Office.State; if (s.clerkSpot == null || s.clerkSpot.Length != 42) s.clerkSpot = new float[42];
            int i = Office.Slot(house) * 7; var p = clerkRoot.transform.localPosition; var q = clerkRoot.transform.localRotation;
            s.clerkSpot[i] = p.x; s.clerkSpot[i + 1] = p.y; s.clerkSpot[i + 2] = p.z; s.clerkSpot[i + 3] = q.x; s.clerkSpot[i + 4] = q.y; s.clerkSpot[i + 5] = q.z; s.clerkSpot[i + 6] = q.w;
        }
        // His outfit: the saved one, else one of his region's picked now (Rules.PickOutfit, the helmsmen's way) and saved. A look made in Player
        // Model's screen goes over it.
        internal void Dress(Transform model)
        {
            var look = World.State?.office?.clerkLook; int li = Office.Slot(house);
            if (look != null && li >= 0 && li < look.Length && look[li] != null && ClerkLook.Apply(model, look[li])) return;
            var all = TraderHulls.Outfits; if (all.Count == 0) return;
            var s = Office.State; int i = Office.Slot(house), pick = s.clerkOutfit[i] - 1;
            if (pick < 0 || pick >= all.Count) { pick = Rules.PickOutfit(Office.Region(house), all.Select(o => o.Kind).ToList(), new HashSet<int>(), UnityEngine.Random.value * .999999); s.clerkOutfit[i] = pick + 1; }
            if (pick >= 0 && TraderHulls.DressWhole(model, pick)) Plugin.Log.LogInfo("Trade office: the " + Pledges.Name(house) + "'s clerk wears outfit " + pick + " (" + all[pick].From + ").");
        }
        // A working copy at a pose: made under an inactive holder, the copied outlines taken off (the game adds its own at Start; the probe
        // officeparts found two on each copy), placed, then woken.
        private GameObject Copy(GameObject source, OfficePart part, string name, Func<GameObject, bool> prepare = null)
        {
            if (!source || !OfficeSpots.TryGet(house, part, out var pose)) return null;
            var hold = new GameObject("trade office hold"); hold.SetActive(false);
            var copy = Instantiate(source, hold.transform); copy.name = name;
            foreach (var o in copy.GetComponentsInChildren<cakeslice.Outline>(true)) DestroyImmediate(o);
            if (prepare != null && !prepare(copy)) { UnityEngine.Object.Destroy(hold); return null; }
            OfficeSpots.Place(copy.transform, transform, pose);
            UnityEngine.Object.Destroy(hold);
            copy.SetActive(true);
            return copy;
        }
        private Transform MissionPart(string name)
        {
            var dude = port ? port.GetDude() : null;
            var ui = dude ? dude.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == "mission UI") : null;
            return ui ? ui.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == name) : null;
        }
        // The sign beside the door: a copy of the house's own sale sign, its buy button replaced by the conversion (§4.1). Cloned from the
        // sign while it's switched off, so the copy's purchase button never registers itself as the house's sign (as the rental scroll is).
        private void MakeSign()
        {
            var sale = Fields.Get<GameObject>(boat, "purchaseUI"); if (!sale) return;
            bool was = sale.activeSelf; sale.SetActive(false);
            try
            {
                sign = Copy(sale, OfficePart.Sign, "trade office sign", c =>
                {
                    var buy = c.GetComponentInChildren<GPButtonPurchaseBoat>(true); if (!buy) return false;
                    var plate = buy.gameObject; DestroyImmediate(buy);
                    signButton = plate.AddComponent<LoanAction>(); signButton.action = ClickSign; signButton.lookText = "";
                    var texts = c.GetComponentsInChildren<TextMesh>(true);
                    signLabel = texts.FirstOrDefault(t => t.transform.parent == plate.transform.parent);
                    signText = texts.FirstOrDefault(t => t != signLabel);
                    if (signText) { var fit = signText.gameObject.AddComponent<LoanTextFit>(); fit.originalScale = signText.transform.localScale; fit.maxWidth = .40f; }
                    return signText;
                });
            }
            finally { sale.SetActive(was); }
            signTimer = 0; UpdateSign();
        }
        private void ClickSign()
        {
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUIClickSound();
            Office.Convert(house);
        }
        private void UpdateSign()
        {
            if (!sign || !signText) return;
            int region = Office.Region(house);
            string why = Office.ConvertRefusal(house);
            signText.text = "TRADE OFFICE\n\n" + (why == null || why == "Not enough gold" ? Rules.OfficePrice(boat.price) + " gold" :
                why.StartsWith("Needs ", StringComparison.Ordinal) && why.Contains("reputation") ? "needs " + BookUI.RegionName(region) + " rep. " + Rules.OfficeLevel : why.ToLowerInvariant());
            if (signLabel) signLabel.text = "Convert";
        }
        // The port's table (§4.2): the table item sharing its mesh, its scripts and body off, its colliders kept so the player can't walk
        // through it, on the default layer (an item's layer lets the player pass).
        private void MakeTable()
        {
            var dir = PrefabsDirectory.instance ? PrefabsDirectory.instance.directory : null; int prefab = OfficeSpots.TablePrefab(house);
            if (dir == null || prefab >= dir.Length || !dir[prefab]) return;
            table = Copy(dir[prefab], OfficePart.Table, "trade office table", c =>
            {
                for (int pass = 0; pass < 4; pass++)
                {
                    foreach (var mb in c.GetComponentsInChildren<MonoBehaviour>(true).Reverse()) DestroyImmediate(mb);
                    foreach (var j in c.GetComponentsInChildren<Joint>(true)) DestroyImmediate(j);
                    foreach (var rb in c.GetComponentsInChildren<Rigidbody>(true)) DestroyImmediate(rb);
                    if (c.GetComponentsInChildren<MonoBehaviour>(true).Length == 0) break;
                }
                foreach (var col in c.GetComponentsInChildren<Collider>(true)) { col.isTrigger = false; col.gameObject.layer = 0; }
                foreach (var t in c.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 0;
                return true;
            });
        }
        // The paper that opens the office's scroll: the mission paper, its button swapped for the mod's (§4.2, as RentandLoan.cs's mission scroll).
        private void MakePaper()
        {
            paper = Copy(MissionPart("mission_details_UI_bg_001")?.gameObject, OfficePart.Paper, "trade office paper", c =>
            {
                foreach (var b in c.GetComponentsInChildren<GPButtonPortMissions>(true))
                {
                    var go = b.gameObject; DestroyImmediate(b);
                    var a = go.AddComponent<LoanAction>(); a.action = () => { if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUIClickSound(); OfficePanel.Open(house); }; a.lookText = "Trade office";
                }
                return c.GetComponentInChildren<LoanAction>(true);
            });
        }
        // The treasury's money chest (§4.4): a copy of the port dude's (on at 8 of 32 ports; a copy is switched on, officeparts), marked as
        // this office's box.
        private void MakeChest()
        {
            chest = Copy(MissionPart("moneychest")?.gameObject, OfficePart.Chest, "trade office chest", c =>
            {
                var button = c.GetComponentInChildren<CurrencyExchangeUIButton>(true); if (!button) return false;
                button.gameObject.AddComponent<OfficeBox>().house = house;
                foreach (var r in c.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
                return true;
            });
        }
        private void Update()
        {
            if (!sign || (signTimer -= Time.unscaledDeltaTime) > 0) return;
            signTimer = .5f; UpdateSign();
        }
    }
    // The clerk's guard: once his model has woken (CharacterCustomizer.Start puts its default clothes on; Talkative NPCs adds its speech bubble
    // and hit talker to every NPCAnimations at Start), whatever another mod added under him goes (the user's list item 3: "Talktive NPC don't make
    // clerk speak") and his outfit goes on. Twice, the first two frames after he wakes; then it stops. He is built 450 m out, before any trigger
    // of his can meet the player.
    public sealed class ClerkGuard : MonoBehaviour
    {
        internal OfficeSite site; internal Transform model; internal HashSet<Transform> kept;
        internal Mesh[] area; internal GameObject[] holders;   // his click area's shells and their holders on his bones (OfficeSite.FitClerkArea)
        private int frames;
        private void Update()
        {
            if (!model) { Destroy(this); return; }
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
                if (t && !kept.Contains(t) && t.parent && kept.Contains(t.parent)) { Plugin.Log.LogInfo("Trade office: " + t.name + " taken off the clerk (another mod's)."); Destroy(t.gameObject); }
            foreach (var m in model.GetComponentsInChildren<MonoBehaviour>(true))
                if (m && m.GetType().Name == "NPCHitTalker") { if (m.gameObject == model.gameObject) Destroy(m); else Destroy(m.gameObject); }
            if (frames == 0 && site) site.Dress(model);
            // Dressed the frame before, and posed by his Animator since: his click area takes his model's shape.
            if (frames == 1 && site) site.FitClerkArea();
            if (++frames >= 2) enabled = false;
        }
        // Player Model's own material copy and his click area's mesh go with him.
        private void OnDestroy() { ClerkLook.Release(gameObject); if (area != null) foreach (var m in area) if (m) Destroy(m); }
    }

    // ---- The clerk's talk (§4.3, Q19: "Boss", six greetings picked at random, "<number> days, Boss. I would advise haste.") ----
    // A click on him starts it (the user, 2026-11-08: "start with a click on him"; his trigger and button added by OfficeSite.MakeClerk): he greets,
    // then tells the harbour's news (the last departure and the next arrival, in full detail) and then the demand (here or abroad, where the goods
    // are, how old the news is); a click on the reply (or on him) goes on, the last closes, walking away closes. Only this mod's lines (the user's
    // list item 3). With wages owed he asks for them instead. In the tavern's speech bubble: the loan tutorials' copy, taken at the start menu from
    // a starting island's tavern NPC (Siren Song, Crab Beach, Neverdin: the game loads them there) and kept, so it serves every port, Serpent Isle
    // and Firefly Grotto included (run ocm-1; the user asked "can you not just copy from other port?": it is). Only if no copy was ever taken, a
    // copy of the house's own sign, which is always loaded.
    public sealed class ClerkTalk : MonoBehaviour
    {
        internal OfficeSite site;
        // The greetings; the second, fourth and sixth in the user's words (2026-11-08), their spelling corrected at their word.
        internal static readonly string[] Greetings = {
            "Good day, Boss. The books are in order and the office is at your service. Shall I report?",
            "Welcome back, Boss. The ledgers are balanced and I have kept watch on the harbour. Shall I report?",
            "Ah, Boss! I have letters from the morning tide and news from the quay. Would you hear it?",
            "At your service, Boss. Nothing has left this harbour without me noting it. Shall I report?",
            "Good to see you, Boss. The office runs smoothly, and I have news of ships and markets. Shall I go on?",
            "Boss, a pleasure. The clerks of the other offices keep me well informed. Shall I report?" };
        internal const float Leave = 4.5f;
        private readonly List<KeyValuePair<string, string>> lines = new List<KeyValuePair<string, string>>();
        private int line; private bool fallback; private float timer;
        private GameObject ui; private TextMesh npcText, buttonText; private Transform bubble, bubbleGfx, buttonGfx;
        private Vector3 bubbleAt, bubbleScale, buttonScale, atPos, atScale = Vector3.one; private Quaternion atTurn = Quaternion.identity; private float baseW, baseH, margin = .2f, buttonW;
        internal bool Showing => ui && ui.activeSelf;
        internal string Text => npcText ? npcText.text : null;
        internal string Reply => buttonText ? buttonText.text : null;
        internal bool Fallback => fallback;
        private void Update()
        {
            if (ui && ui.activeSelf && Refs.observerMirror) ui.transform.rotation = Refs.observerMirror.transform.rotation;
            if (!Showing || (timer -= Time.unscaledDeltaTime) > 0) return; timer = .2f;
            var player = Refs.charController ? Refs.charController.transform.position : (Camera.main ? Camera.main.transform.position : transform.position + Vector3.one * 99f);
            var flat = player - transform.position; float d = new Vector2(flat.x, flat.z).magnitude, h = Mathf.Abs(flat.y);
            if (d > Leave || h > 3f) Close();
        }
        // A click on him: the talk begins, or goes on as the reply would.
        internal void Clicked() { if (Showing) Click(); else Begin(); }
        // The lines of one talk, made when it begins (the news as it stands then).
        private void Compose()
        {
            lines.Clear();
            int house = site ? site.house : 0, coin = Office.Coin(house);
            if (Office.Wages(house) > 0)
            {
                lines.Add(new KeyValuePair<string, string>("Forgive me, Boss, but my wages are owed: " + Office.Wages(house).ToString("N0") + " " + PlayerGold.GetCurrencyName(coin) + ". The treasury ran dry. You can settle them at the office's papers.", "I'll see to it."));
                return;
            }
            lines.Add(new KeyValuePair<string, string>(Greetings[UnityEngine.Random.Range(0, Greetings.Length)], "Let me hear it."));
            int port = site && site.Port ? site.Port.portIndex : -1;
            string left = null, expected = null;
            if (port >= 0) Fleet.ClerkShips(port, out left, out expected);
            if (left == null && expected == null) lines.Add(new KeyValuePair<string, string>("The harbour has been quiet, Boss. No trader news today.", "Any word of demand?"));
            else
            {
                if (left != null) lines.Add(new KeyValuePair<string, string>(left, expected != null ? "Go on." : "Any word of demand?"));
                if (expected != null) lines.Add(new KeyValuePair<string, string>(expected, "Any word of demand?"));
            }
            var told = port >= 0 ? Demand.News(port) : null;
            var here = port >= 0 && Port.ports != null && port < Port.ports.Length ? Port.ports[port] : null;
            var there = told != null && told.demand >= 0 && told.demand < Port.ports.Length ? Port.ports[told.demand] : null;
            var maker = told != null && told.producer >= 0 && told.producer < Port.ports.Length ? Port.ports[told.producer] : null;
            if (told == null || !here || !there || !maker) { lines.Add(new KeyValuePair<string, string>("No shortages of note at present, Boss.", "Thank you.")); return; }
            if (!Demand.State.heard.Contains(told.id)) Demand.State.heard.Add(told.id);
            var g = PrefabsDirectory.instance ? PrefabsDirectory.instance.GetGood(told.good) : null; string good = g ? g.name : "goods";
            bool local = there.region == here.region, makerLocal = maker.region == here.region;
            lines.Add(new KeyValuePair<string, string>(local ? "Our correspondents report that " + there.GetPortName() + " is in want of " + good + ", Boss. Whoever brings it will be paid handsomely."
                : "All is well in this region, Boss. But " + there.GetPortName() + " in " + BookUI.RegionName((int)there.region) + " reports a grave shortage of " + good + ".", "Where might such goods be had?"));
            lines.Add(new KeyValuePair<string, string>(makerLocal ? maker.GetPortName() + ", close by, holds more " + good + " than its merchants can sell, Boss."
                : "You would have to sail for it, Boss: " + maker.GetPortName() + " in " + BookUI.RegionName((int)maker.region) + " is selling " + good + " at a low price.", "How old is this news?"));
            int days = Math.Max(0, GameState.day - told.startDay);
            lines.Add(new KeyValuePair<string, string>(days + (days == 1 ? " day" : " days") + ", Boss. I would advise haste.", "Thank you."));
        }
        private void Begin()
        {
            if (!site || !Office.Live(site.house)) return;
            Compose(); line = 0;
            if (!Build()) return;
            ui.SetActive(true); Place(); Say();
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUISound(UISounds.smallPop, .75f, 1f);
        }
        internal void Close() { if (ui) ui.SetActive(false); }
        internal void Click()
        {
            if (!Showing) return;
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUISound(UISounds.smallPop, .75f, 1f);
            if (line >= lines.Count - 1) { Close(); return; }
            line++; Say();
        }
        private void Say()
        {
            var l = lines[Mathf.Clamp(line, 0, lines.Count - 1)];
            npcText.text = Rules.Wrap(l.Key, fallback ? 30 : LoanTutorial.WrapWidth); buttonText.text = l.Value;
            Fit(); Clear();
        }
        // The speech bubble: the tavern's copy (as the loan tutorials build it), else, if none was ever taken, the house's sign. Built once, kept
        // for the next talks.
        private bool Build()
        {
            if (ui && (fallback == !LoanTutorial.HasTemplate)) return npcText && buttonText;
            if (ui) { Destroy(ui); ui = null; }
            fallback = !LoanTutorial.HasTemplate;
            if (!fallback)
            {
                atPos = LoanTutorial.TemplatePos; atTurn = LoanTutorial.TemplateTurn; atScale = LoanTutorial.TemplateScale;
                ui = Instantiate(LoanTutorial.Template, transform, false); ui.name = "clerk talk";
                ui.transform.localPosition = atPos; ui.transform.localRotation = atTurn; ui.transform.localScale = atScale;
                var hook = ui.GetComponentInChildren<GPButtonInterface>(true);
                buttonGfx = hook ? hook.transform : null;
                var texts = ui.GetComponentsInChildren<TextMesh>(true);
                buttonText = buttonGfx ? texts.FirstOrDefault(t => t.transform.parent == buttonGfx.parent) : null;
                npcText = texts.FirstOrDefault(t => t != buttonText);
                bubble = npcText ? npcText.transform.parent : null;
                bubbleGfx = bubble ? bubble.GetComponentsInChildren<MeshRenderer>(true).Where(r => !r.GetComponent<TextMesh>()).Select(r => r.transform).FirstOrDefault() : null;
                if (hook) { hook.gameObject.AddComponent<ClerkTalkButton>().owner = this; hook.onActivateInterface = hook.gameObject; }
                if (buttonGfx) buttonGfx.parent.gameObject.SetActive(true);
                if (bubble) bubbleAt = bubble.localPosition;
                if (bubbleGfx) { bubbleScale = bubbleGfx.localScale; baseW = Across(bubbleGfx, Vector3.right) * 2; baseH = Across(bubbleGfx, Vector3.up) * 2; }
                if (buttonGfx) { buttonScale = buttonGfx.localScale; buttonW = Across(buttonGfx, Vector3.right) * 2; }
                margin = npcText ? Mathf.Max(.05f, baseH - Size(npcText, npcText.text).y) : .2f;
            }
            else
            {
                // The house's sale sign, cloned while switched off (its buy button never registers), its button the reply.
                var b = site ? Pledges.Find(site.house) : null; var sale = b ? Fields.Get<GameObject>(b, "purchaseUI") : null;
                if (!sale) return false;
                bool was = sale.activeSelf; sale.SetActive(false);
                try { ui = Instantiate(sale, transform, false); } finally { sale.SetActive(was); }
                ui.name = "clerk talk (sign)"; ui.SetActive(false);
                var buy = ui.GetComponentInChildren<GPButtonPurchaseBoat>(true); if (!buy) { Destroy(ui); ui = null; return false; }
                var plate = buy.gameObject; DestroyImmediate(buy);
                foreach (var o in ui.GetComponentsInChildren<cakeslice.Outline>(true)) DestroyImmediate(o);
                var a = plate.AddComponent<LoanAction>(); a.action = Click; a.lookText = "";
                var texts = ui.GetComponentsInChildren<TextMesh>(true);
                buttonText = texts.FirstOrDefault(t => t.transform.parent == plate.transform.parent);
                npcText = texts.FirstOrDefault(t => t != buttonText);
                if (npcText) { var fit = npcText.gameObject.AddComponent<LoanTextFit>(); fit.originalScale = npcText.transform.localScale; fit.maxWidth = .55f; }
                if (buttonText) { var fit = buttonText.gameObject.AddComponent<LoanTextFit>(); fit.originalScale = buttonText.transform.localScale; fit.maxWidth = .30f; }
                atPos = new Vector3(0f, 1.55f, 0f); atTurn = Quaternion.identity;
                var ws = sale.transform.lossyScale * 1.6f; var ps = transform.lossyScale; atScale = new Vector3(ws.x / ps.x, ws.y / ps.y, ws.z / ps.z);
                ui.transform.localScale = atScale; bubbleGfx = buttonGfx = null;
            }
            if (!npcText || !buttonText) { Plugin.Log.LogWarning("Trade office: the clerk's speech has no texts."); return false; }
            Plugin.Log.LogInfo("Trade office: the clerk speaks in " + (fallback ? "a copy of the house's sign (no tavern bubble was taken at the start menu)" : "the tavern's speech bubble") + ".");
            return true;
        }
        private float Across(Transform part, Vector3 axis) => ui.transform.InverseTransformVector(part.TransformVector(axis)).magnitude;
        // As LoanTutorial.Size: a text's width and height in the speech UI's units.
        private Vector2 Size(TextMesh tm, string text)
        {
            var font = tm.font; if (!font || string.IsNullOrEmpty(text)) return Vector2.zero;
            int size = tm.fontSize > 0 ? tm.fontSize : font.fontSize;
            font.RequestCharactersInTexture(text, size, tm.fontStyle);
            float widest = 0; var rows = text.Split('\n');
            foreach (var row in rows) { float w = 0; foreach (char ch in row) if (font.GetCharacterInfo(ch, out var ci, size, tm.fontStyle)) w += ci.advance; widest = Mathf.Max(widest, w); }
            float unit = tm.characterSize * .1f * Across(tm.transform, Vector3.right);
            float lineH = font.lineHeight * (float)size / Mathf.Max(1, font.fontSize) * tm.lineSpacing;
            return new Vector2(widest * unit, rows.Length * lineH * unit);
        }
        // As LoanTutorial.Fit: the bubble as big as its text needs, its lower edge above the reply; the reply's plate as wide as its label.
        private void Fit()
        {
            if (bubbleGfx && baseW > 0 && baseH > 0)
            {
                var need = Size(npcText, npcText.text);
                float w = Mathf.Max(baseW, need.x / .85f), hh = Mathf.Max(baseH, need.y + margin);
                bubbleGfx.localScale = new Vector3(bubbleScale.x * w / baseW, bubbleScale.y * hh / baseH, bubbleScale.z);
                bubble.localPosition = bubbleAt + Vector3.up * (hh - baseH) / 2f;
            }
            if (buttonGfx && buttonW > 0)
            {
                float w = Mathf.Max(buttonW, Size(buttonText, buttonText.text).x / .85f);
                buttonGfx.localScale = new Vector3(buttonScale.x * w / buttonW, buttonScale.y, buttonScale.z);
            }
        }
        // As LoanTutorial.Place: on the level line from him to the player, 1.5 m short of the player (never behind him, at most 1.6 m out).
        private Vector3 placed;
        private void Place()
        {
            var player = Refs.charController ? Refs.charController.transform.position : transform.position + transform.forward;
            var flat = Vector3.ProjectOnPlane(player - transform.position, transform.up); float d = flat.magnitude;
            var at = transform.position + (d > .01f ? flat / d : transform.forward) * Mathf.Clamp(d - 1.5f, 0f, 1.6f);
            var local = transform.InverseTransformPoint(at);
            ui.transform.localPosition = placed = new Vector3(local.x, atPos.y, local.z);
        }
        // As LoanTutorial.Clear: kept out of walls and ceilings, up to 1 m across the view and 0.4 m down.
        private static readonly Vector2[] Moves = Enumerable.Range(-10, 21).SelectMany(x => Enumerable.Range(0, 5).Select(y => new Vector2(x * .1f, -y * .1f)))
            .OrderBy(v => v.sqrMagnitude).ThenBy(v => -v.y).ToArray();
        private void Clear()
        {
            ui.transform.localPosition = placed;
            var cam = Camera.main; var eye = cam ? cam.transform.position : transform.position + transform.forward * 2f + Vector3.up * 1.5f;
            var facing = Quaternion.LookRotation(ui.transform.position - eye, Vector3.up);
            ui.transform.rotation = facing;
            Vector3 start = ui.transform.position, across = facing * Vector3.right;
            foreach (var move in Moves)
            {
                ui.transform.position = start + across * move.x + Vector3.up * move.y;
                if (!Blocked()) return;
            }
            ui.transform.position = start;
        }
        private bool Blocked()
        {
            foreach (var part in new[] { bubbleGfx, buttonGfx })
            {
                var mesh = part ? part.GetComponent<MeshFilter>() : null; if (!mesh || !mesh.sharedMesh) continue;
                var b = mesh.sharedMesh.bounds; var s = Vector3.Scale(b.extents, part.lossyScale);
                var half = new Vector3(Mathf.Max(Mathf.Abs(s.x), .03f), Mathf.Max(Mathf.Abs(s.y), .03f), Mathf.Max(Mathf.Abs(s.z), .03f));
                foreach (var c in Physics.OverlapBox(part.TransformPoint(b.center), half, part.rotation, ~0, QueryTriggerInteraction.Ignore))
                    if (!c.transform.IsChildOf(transform) && !(c.attachedRigidbody && !c.attachedRigidbody.isKinematic) && !c.CompareTag("Player") &&
                        !(Refs.charController && c.transform.IsChildOf(Refs.charController.transform))) return true;
            }
            return false;
        }
    }
    // ---- Moving the clerk (Q18, the user: "in trade office UI you should be able to reassign clerk's position"; any ground or floor on the
    // house's island, not aboard a ship, not in the sea) ----
    // From the office's scroll: he follows the point the player looks at (the view's middle, up to 8 m), turned to face the player; a left click
    // or R puts him there, a right click, F (or Escape) puts him back; Q and E turn him, and from then on he keeps that heading (the user,
    // 2026-11-09: "R also act as left click to place clerk, F also act as right click to cancel / Q/E to rotate the clerk, change where he face").
    // Saved island-local, his turn with his spot (OfficeSite.SaveClerkSpot's rotation), so he rides with the island. He himself is the ghost.
    // The keys are the game's own bindings (GameInput, the player's rebinds followed): R is its Activate key, F its PickUp key, Q and E its
    // RotateH and RotateV keys. The user's pairing is the other way round from the game's second keys (left click is PickUp's, right click
    // Activate's); the user's words win. While he is moved, and until those keys are let go after it, the game's own reads of them answer "not
    // pressed" (ClerkMoveKeyPatch), so they don't also click, pick up, drop, throw or turn what the pointer holds, or stop the view (Q in MouseLook).
    internal static class ClerkPlacer
    {
        private static OfficeSite site; private static Vector3 fromPos; private static Quaternion fromTurn; private static float started; private static bool valid;
        // His heading set with Q/E (world yaw, degrees); NaN while he still faces the player.
        private static float yaw = float.NaN;
        // The move's keys still held after it ended: the game's reads of them stay off until they are let go (no click, drop or throw on release).
        private static bool swallow;
        internal static bool Active => site;
        internal static bool Valid => valid;
        internal static OfficeSite Site => site;
        internal static float Yaw => yaw;
        // Read at every GameInput call (ClerkMoveKeyPatch): Unity's object check only while a site is set.
        internal static bool Holds => swallow || !ReferenceEquals(site, null) && site;
        internal static readonly InputName[] Taken = { InputName.PickUp, InputName.Activate, InputName.RotateH, InputName.RotateV };
        // The keyboard key the player bound to a game input (its first key); a mouse button there is left to the clicks themselves.
        internal static KeyCode Key(InputName input)
        {
            var k = GameInput.GetKeyCode(input, false, false);
            return k >= KeyCode.Mouse0 && k <= KeyCode.Mouse6 ? KeyCode.None : k;
        }
        private static string Name(InputName input) { var k = Key(input); return k == KeyCode.None ? null : k.ToString(); }
        private static bool Down(InputName input) { var k = Key(input); return k != KeyCode.None && Input.GetKeyDown(k); }
        private static bool Held(InputName input) { var k = Key(input); return k != KeyCode.None && Input.GetKey(k); }
        // Any key of the inputs the move takes (each one's two keys and its controller key) still held.
        private static bool AnyHeld()
        {
            foreach (var input in Taken)
                foreach (var k in new[] { GameInput.GetKeyCode(input, false, false), GameInput.GetKeyCode(input, true, false), GameInput.GetKeyCode(input, false, true) })
                    if (k != KeyCode.None && Input.GetKey(k)) return true;
            return false;
        }
        // The game's read of an input while the move holds it: "not pressed" (ClerkMoveKeyPatch).
        internal static bool Lets(InputName input, ref bool result)
        {
            if (!Holds || Array.IndexOf(Taken, input) < 0) return true;
            result = false; return false;
        }
        // from: the scroll's button that began the move; the game's pointer never sees its release (the move holds the key), so it is let go here.
        internal static void Begin(OfficeSite s, GoPointerButton from = null)
        {
            if (!s || !s.Clerk) return;
            site = s; fromPos = s.Clerk.transform.localPosition; fromTurn = s.Clerk.transform.localRotation; started = Time.unscaledTime; valid = false; yaw = float.NaN;
            if (from) from.Unclick();
            Loans.NotifyFor(Rules.ClerkMoveHint(Name(InputName.Activate), Name(InputName.PickUp), Name(InputName.RotateH), Name(InputName.RotateV)), Rules.ClerkMoveHintSeconds);
        }
        // The place the view's middle meets (his own and the player's colliders skipped); whether he may stand there.
        internal static bool Aim(Ray ray, out Vector3 point, out string why)
        {
            point = Vector3.zero; why = null;
            var clerk = site ? site.Clerk : null;
            var hits = Physics.RaycastAll(ray, 8f, ~0, QueryTriggerInteraction.Ignore).Where(h => !(clerk && h.collider.transform.IsChildOf(clerk.transform)) &&
                !(Refs.charController && h.collider.transform.IsChildOf(Refs.charController.transform)) && !h.collider.CompareTag("Player")).OrderBy(h => h.distance).ToArray();
            if (hits.Length == 0) { why = "nothing there"; return false; }
            var hit = hits[0]; point = hit.point;
            if (hit.normal.y < .7f) why = "not level ground";
            else if (hit.collider.GetComponentInParent<BoatRefs>() || hit.collider.GetComponentInParent<ItemRigidbody>() || hit.collider.GetComponentInParent<ShipItem>()) why = "not on a ship or an item";
            else if (site && hit.collider.transform.IsChildOf(site.transform)) why = "not on the office's table";
            else if (hit.point.y < .3f) why = "not in the sea";
            else { var b = site ? Pledges.Find(site.house) : null; if (!b || (b.transform.position - hit.point).magnitude > 150f) why = "too far from the office"; }
            return why == null;
        }
        internal static void Tick()
        {
            if (!site) { if (swallow && !AnyHeld()) swallow = false; return; }
            var clerk = site.Clerk; var cam = Camera.main;
            if (!clerk || !cam || !GameState.playing || !Office.Live(site.house)) { Cancel(); return; }
            valid = Aim(new Ray(cam.transform.position, cam.transform.forward), out var point, out var why);
            if (valid)
            {
                clerk.transform.position = point;
                if (float.IsNaN(yaw)) { var face = cam.transform.position - point; face.y = 0f; if (face.sqrMagnitude > .01f) clerk.transform.rotation = Quaternion.LookRotation(face.normalized, Vector3.up); }
            }
            // Q turns him left, E right, while held (Rules.ClerkTurn); the first turn starts from the way he faces now.
            int turn = (Held(InputName.RotateV) ? 1 : 0) - (Held(InputName.RotateH) ? 1 : 0);
            if (turn != 0) { if (float.IsNaN(yaw)) yaw = clerk.transform.eulerAngles.y; yaw = Rules.ClerkTurn(yaw, turn, Time.unscaledDeltaTime); }
            if (!float.IsNaN(yaw)) clerk.transform.rotation = Rules.ClerkFacing(yaw);
            if (Time.unscaledTime - started < .3f) return;
            if (Input.GetMouseButtonDown(0) || Down(InputName.Activate)) { if (valid) Place(); else Loans.Notify("He can't stand there: " + why); }
            else if (Input.GetMouseButtonDown(1) || Down(InputName.PickUp)) Cancel();
        }
        // Turned by the QA (officeclerkmove) as Q/E would: degrees, + to the right.
        internal static void Turn(float degrees)
        {
            if (!site || !site.Clerk) return;
            if (float.IsNaN(yaw)) yaw = site.Clerk.transform.eulerAngles.y;
            yaw = Rules.ClerkTurn(yaw, Math.Sign(degrees), Math.Abs(degrees) / Rules.ClerkTurnRate); site.Clerk.transform.rotation = Rules.ClerkFacing(yaw);
        }
        internal static void Place()
        {
            if (!site) return;
            site.SaveClerkSpot(); Loans.Notify("The clerk stands here now");
            Plugin.Log.LogInfo("Trade office: the " + Pledges.Name(site.house) + "'s clerk moved to " + site.Clerk.transform.localPosition + ", facing " +
                site.Clerk.transform.eulerAngles.y.ToString("0") + " degrees (island-local).");
            site = null; swallow = true;
        }
        internal static void Cancel()
        {
            if (site && site.Clerk) { site.Clerk.transform.localPosition = fromPos; site.Clerk.transform.localRotation = fromTurn; }
            if (site) { Loans.Notify("The clerk stays where he was"); swallow = true; }
            site = null;
        }
    }

    // ---- The clerk's look in Player Model's own screen (Q16, the user: "only B") ----
    // Read from the mod's public members by name (no reference to it; Virtual Crew's way of calling it): the player's look kept, the clerk's put in
    // its place, Player Model's character screen opened; when it closes, the look it holds is the clerk's and the player's goes back. The player's
    // look waits in the office's save meanwhile, and goes back at the next load if the game stopped between. On the clerk: Player Model's own
    // body material (BodyTemplate.SharedMaterial), so the colours it writes don't recolour every NPC sharing the game's material (§4.3's trap).
    internal static class ClerkLook
    {
        internal const string Guid = "com.diamondminer99.playermodel";
        private static bool tried; private static Type model, appearance, screen, menu, body;
        private static OfficeSite editing;
        private static bool Find()
        {
            if (tried) return model != null && appearance != null && screen != null;
            tried = true;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid)) return false;
            // by FleetCompat.TypeOf, not AccessTools.TypeByName: the same type when it exists, and a miss (ModPauseMenu or BodyTemplate in another
            // Player Model version) scans no type list and logs no HarmonyX warning
            model = FleetCompat.TypeOf("SailwindPlayerModel.PlayerModel"); appearance = FleetCompat.TypeOf("SailwindPlayerModel.PlayerAppearance");
            screen = FleetCompat.TypeOf("SailwindPlayerModel.CharacterScreen"); menu = FleetCompat.TypeOf("SailwindPlayerModel.ModPauseMenu"); body = FleetCompat.TypeOf("SailwindPlayerModel.BodyTemplate");
            bool ok = model != null && appearance != null && screen != null && AccessTools.Property(model, "LocalAppearance") != null && AccessTools.Method(appearance, "Serialize") != null && AccessTools.Method(screen, "Open") != null;
            if (!ok) { Plugin.Log.LogWarning("Trade office: Player Model is loaded, but its members aren't as expected; the clerk's look can't be edited."); model = null; }
            return ok;
        }
        internal static bool Available => Find();
        private static object Local { get => AccessTools.Property(model, "LocalAppearance").GetValue(null, null); set => AccessTools.Property(model, "LocalAppearance").SetValue(null, value, null); }
        private static string Serialize(object a) => (string)AccessTools.Method(appearance, "Serialize").Invoke(a, null);
        private static object Deserialize(string s) => AccessTools.Method(appearance, "Deserialize", new[] { typeof(string) }).Invoke(null, new object[] { s });
        // CharacterScreen.IsOpen, read every frame while the clerk's look is edited (Tick): its PropertyInfo looked up once, not by name each frame
        // (screen is fixed once Find has run); its value is still read each time.
        private static PropertyInfo isOpen; private static bool isOpenLooked;
        internal static bool ScreenOpen
        {
            get
            {
                if (!Find()) return false;
                if (!isOpenLooked) { isOpen = AccessTools.Property(screen, "IsOpen"); isOpenLooked = true; }
                return (bool)isOpen.GetValue(null, null);
            }
        }
        // The office's Escape stays off while Player Model's screen uses it (its own check: CharacterScreen.IsOpen or ConsumedPauseKeyThisFrame).
        internal static bool KeyTaken { get { if (!Find()) return false; var used = AccessTools.Property(screen, "ConsumedPauseKeyThisFrame"); return ScreenOpen || used != null && (bool)used.GetValue(null, null); } }
        internal static string Edit(OfficeSite s)
        {
            if (!Find()) return "Needs the Player Model mod";
            if (editing) return "Already editing";
            if (!s || !s.Clerk) return "No clerk here";
            try
            {
                var st = Office.State; string mine = Serialize(Local); st.playerLook = mine;
                var looks = st.clerkLook ?? (st.clerkLook = new string[6]); int i = Office.Slot(s.house);
                Local = Deserialize(looks[i] ?? mine);
                AccessTools.Method(screen, "Open").Invoke(null, null);
                if (!ScreenOpen) { Restore(); return "Player Model's screen didn't open"; }
                editing = s;
                Plugin.Log.LogInfo("Trade office: the clerk's look opened in Player Model's screen.");
                return null;
            }
            catch (Exception e) { Restore(); Plugin.Log.LogWarning("Trade office: Player Model's screen not opened: " + (e.InnerException ?? e).Message); return "Player Model's screen didn't open"; }
        }
        internal static void Tick()
        {
            if (!editing) { if (World.Ready && World.State?.office?.playerLook != null && Find()) Restore(); return; }
            if (ScreenOpen) return;
            var s = editing; editing = null;
            try
            {
                string edited = Serialize(Local); Restore();
                var st = Office.State; (st.clerkLook ?? (st.clerkLook = new string[6]))[Office.Slot(s.house)] = edited;
                var hide = menu != null ? AccessTools.Method(menu, "Hide") : null; if (hide != null) hide.Invoke(null, null);
                // A fresh clerk wears it (OfficeSite.RemakeClerk: the outfit's parts never under it); his guard dresses him (Dress puts the saved
                // look on) and fits his click area.
                if (s && s.Clerk) s.RemakeClerk();
                Plugin.Log.LogInfo("Trade office: the clerk's look saved from Player Model's screen; the player's own look put back.");
            }
            catch (Exception e) { Restore(); Plugin.Log.LogWarning("Trade office: the clerk's look not read back: " + (e.InnerException ?? e).Message); }
        }
        private static void Restore()
        {
            var st = World.State?.office; if (st?.playerLook == null || !Find()) return;
            try { Local = Deserialize(st.playerLook); st.playerLook = null; }
            catch (Exception e) { Plugin.Log.LogWarning("Trade office: the player's look not put back: " + (e.InnerException ?? e).Message); }
        }
        internal static bool Apply(Transform modelRoot, string look)
        {
            if (!Find() || string.IsNullOrEmpty(look) || !modelRoot) return false;
            var cc = modelRoot.GetComponentInChildren<PsychoticLab.CharacterCustomizer>(true); if (!cc) return false;
            try
            {
                var ensure = body != null ? AccessTools.Method(body, "Ensure") : null; if (ensure != null) ensure.Invoke(null, null);
                // The game's own part lists made again first, as Player Model does on its own copy (BodyTemplate.RebuildPartLists): every part switched
                // off and each group listed, so his female parts are there to pick (PlayerAppearance.Apply refuses female with no female torso listed)
                // and no part from before stays on.
                try { AccessTools.Method(typeof(PsychoticLab.CharacterCustomizer), "BuildLists")?.Invoke(cc, null); }
                catch (Exception e) { Plugin.Log.LogWarning("Trade office: the clerk's part lists not made again: " + (e.InnerException ?? e).Message); }
                var shared = body != null ? AccessTools.Property(body, "SharedMaterial")?.GetValue(null, null) as Material : null;
                if (shared) cc.mat = shared;
                AccessTools.Method(appearance, "ApplyLive", new[] { typeof(PsychoticLab.CharacterCustomizer) }).Invoke(Deserialize(look), new object[] { cc });
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Trade office: the clerk's look not put on: " + (e.InnerException ?? e).Message); return false; }
        }
        internal static void Release(GameObject clerk)
        {
            if (!clerk || !Find()) return;
            try { var m = AccessTools.Method(appearance, "ReleaseOwnedMaterial", new[] { typeof(GameObject) }); if (m != null) m.Invoke(null, new object[] { clerk }); } catch (Exception) { }
        }
    }

    // Where the clerk's look text is drawn, on each of his buttons (OfficeSite.MakeClerk, FitClerkArea).
    public sealed class ClerkTextAt : MonoBehaviour { internal Transform at; }
    // The game draws a pointed-at button's text at that button's own place (LookUI.ShowLookText); the clerk's at his one place instead. Last, so
    // no other patch of it moves his text after this one; any other button is left as it is.
    [HarmonyPatch(typeof(LookUI), "ShowLookText")] internal static class ClerkLookTextPatch
    {
        [HarmonyPriority(Priority.Last)]
        static void Postfix(LookUI __instance, GoPointerButton button)
        {
            var c = button ? button.GetComponent<ClerkTextAt>() : null; if (!c || !c.at) return;
            __instance.transform.position = c.at.position;
            if (Camera.main) __instance.transform.LookAt(Camera.main.transform);
        }
    }

    // The reply's own target (GPButtonInterface), as the tutorials' and the demand talk's.
    internal sealed class ClerkTalkButton : MonoBehaviour, IGPButton
    {
        internal ClerkTalk owner;
        public void Activate() { if (owner) owner.Click(); }
    }

    // Marks the money chest of an office: the exchange it opens is the office's (no fee, the daily rate) and its tab is the treasury's.
    public sealed class OfficeBox : MonoBehaviour
    {
        internal int house;
        // The chest whose click is opening the exchange (set by the click, cleared after it), read by the booth and the treasury page.
        internal static Component Opening;
        internal static int HouseOf(Component c) { var b = c ? c.GetComponent<OfficeBox>() : null; return b ? b.house : 0; }
    }

    // ---- The office's scroll: the upgrades, the wages owed (§4.2) ----
    // On the rental scroll's parchment (the mission screen's), the upgrades listed on the left, the chosen one's terms on the right, with Buy.
    public sealed class OfficePanel : MonoBehaviour
    {
        internal static OfficePanel Instance;
        private GameObject root; private Transform details;
        private TextMesh title, status, infoTitle, infoText, infoPrice, infoCorner;
        private readonly TextMesh[] rows = new TextMesh[8]; private readonly BoxCollider[] rowBoxes = new BoxCollider[8];
        private LoanAction confirm, close, extra; private TextMesh confirmText, closeText, extraText; private Material confirmMaterial, extraMaterial; private Color confirmColor;
        // The right side's text: its top, its own scale (shrunk for a long text), and its width.
        // The right column .04 lower than first built, its title clear of the tab row (obk-2: a tab over the column touched its title), and .06 lower
        // again under the bigger tabs on plates (the user's 2026-11-08 round).
        // "Bought" stands in the right side's upper right corner (the user, 2026-11-08 night: "move the bought status to upper right corner"): its right
        // edge at CornerX, on the title's line, the title narrowed to end CornerGap before it (TitleWidth otherwise).
        private const float InfoTitleTop = .565f, InfoTop = .445f, InfoWidth = .78f, TitleWidth = .85f, CornerX = -.72f, CornerGap = .04f; private Vector3 infoScale;
        // The text runs down to the status line, or to the buttons when there is no status (the user, 2026-11-08 night: "Use empty space"; before, it
        // ended at -.24 with the status line at -.32, an empty band under it): the status line StatusGap above the buttons' plates, which Measure reads.
        private const float StatusGap = .02f;
        private float buttonsTop = -.49f, statusY = -.43f, bottomStatus = -.37f, bottomFree = -.47f;
        // The route list (the user, 2026-11-08 night: "make route selection open a list to select from"): Assign route / Change route opens a plate for
        // each route the ship's hull may take, over the right side above the buttons (its middle RouteX, RouteW wide, RouteH high, RouteGap apart).
        private const int RouteMax = 5; private const float RouteX = -.35f, RouteW = .74f, RouteH = .12f, RouteGap = .015f;
        private readonly GameObject[] routePlates = new GameObject[RouteMax]; private readonly TextMesh[] routeTexts = new TextMesh[RouteMax];
        private readonly BoxCollider[] routeBoxes = new BoxCollider[RouteMax]; private readonly LoanAction[] routeActions = new LoanAction[RouteMax], routeLabelActions = new LoanAction[RouteMax];
        private bool routeOpen; private int[] routeKinds = new int[0];
        // The tabs' row: its middle, a plate's height, the room each side of a label (the plate's curled ends) and between plates, and how far a
        // label sits above the plate's middle (its font draws low from MiddleCenter) (the scroll's frame; obk-7: at .715 and .085 high the plates
        // were lower than their labels and pressed against the title).
        private const float TabY = .67f, TabH = .135f, TabPad = .065f, TabGap = .025f, TabLabelLift = .018f;
        private readonly GameObject[] tabPlates = new GameObject[6]; private Material tabOnMat, tabOffMat;
        private int house, selected, tab, crewSelected; private float timer, lockedUntil;
        // An upgrade that can't be bought now. The game has no grey ink of its own (its shipyard marks parts red): a mid grey, readable on the parchment.
        // In 8-bit steps (115 = 0.451), as a TextMesh keeps its colour: it reads back the same (of-4-a: 0.45 read back 0.451 and compared unequal).
        // The game's text shader brightens a colour by its gamma curve: 115 drew at 179, hardly darker than the parchment (196, 164, 131; of-6-a), and
        // the user (2026-11-08): "make the gray out text in trade office ui a bit darker, currently it's too bright". 48 draws at about 119.
        internal static readonly Color Unavailable = new Color32(48, 48, 48, 255);
        // The Items tab opens the unique items page (OfficeItemsPage; the user, 2026-11-08: "Use mission UI page for unique item purchase"), whose
        // own buttons hold the regional and modded lists: the Regional and Modded tabs left the scroll (their slots kept, never shown).
        internal static readonly string[] TabNames = { "Upgrades", "Crew", "Items", "Regional", "Modded", "Fleet" };
        private readonly TextMesh[] tabs = new TextMesh[6]; private readonly BoxCollider[] tabBoxes = new BoxCollider[6];
        private int shipSelected;
        internal int ShipSelected { get => shipSelected; set { shipSelected = value; routeOpen = false; Refresh(); } }
        private List<OfficeCrew.Candidate> candidates = new List<OfficeCrew.Candidate>(); private int candidatesPeriod = -1;
        // Whether a tab shows: Upgrades always, the others while their upgrade works (Crew with Virtual Crew loaded); the Fleet tab while the fleet
        // is bought (its ships can be seen and dismissed while paused).
        private bool TabShown(int t) => t == 0 || t == 1 && Office.Working(house, OfficeUpgrade.Crew) && OfficeCrew.Available || t == 2 && Office.Working(house, OfficeUpgrade.Items) ||
            t == 5 && Office.Has(house, OfficeUpgrade.Fleet);
        private string TabName(int t) => TabNames[t];
        internal int Tab { get => tab; set { tab = value; Refresh(); } }
        // For the tests (QA officebooks: which right-side texts SetInfo shrinks; the user, 2026-11-08: "show me which text is too long"): the upgrade
        // row chosen, the scale the last text got (1: full size) and its lines as wrapped.
        internal int Selected { get => selected; set { selected = value; Refresh(); } }
        internal int Upgrades => listed.Length;
        internal float InfoShrink { get; private set; } = 1f;
        internal int InfoLines { get; private set; }
        internal string InfoTitle => infoTitle ? infoTitle.text : "";
        // For the tests (QA officebooks): the corner's text, the route list (open, its labels, the plate on the chosen look), a route clicked as the
        // player clicks it, and where the measured parts stand.
        internal string Corner => infoCorner ? infoCorner.text : "";
        internal bool RouteOpen => routeOpen;
        internal string[] RouteLabels => routeTexts.Where(t => t && t.text.Length > 0).Select(t => t.text).ToArray();
        internal int RouteChosen
        {
            get
            {
                for (int r = 0; r < RouteMax; r++)
                {
                    var rr = routePlates[r] && routePlates[r].activeSelf ? routePlates[r].GetComponentInChildren<MeshRenderer>(true) : null;
                    if (rr && tabOnMat && rr.sharedMaterial == tabOnMat) return r;
                }
                return -1;
            }
        }
        internal void ClickRoute(int r) { if (r < 0 || r >= RouteMax) return; if (routeActions[r]) routeActions[r].OnActivate(); else routeLabelActions[r].OnActivate(); }
        internal float ButtonsTop => buttonsTop;
        internal List<OfficeCrew.Candidate> Candidates => candidates;
        internal int CrewSelected { get => crewSelected; set { crewSelected = value; Refresh(); } }
        // The upgrades listed (crew recruitment only with Virtual Crew), in the user's order.
        private Office.UpgradeInfo[] listed = Office.Upgrades;
        // The clerk's row, bought: Buy becomes "Move clerk" (Q18), the middle button "Clerk's look" with Player Model (Q16).
        private bool ClerkRow => selected < listed.Length && listed[selected].Bit == OfficeUpgrade.Clerk && Office.Has(house, OfficeUpgrade.Clerk);
        private float dismissAsked;
        private void Extra()
        {
            if (Time.unscaledTime < lockedUntil) return;
            // Dismiss asks twice (a ship leaves the fleet for good): the second click within 4 s does it.
            if (tab == 5)
            {
                var ships = OfficeFleet.Of(house).ToList(); if (shipSelected >= ships.Count) return;
                Click(); lockedUntil = Time.unscaledTime + BookUI.ClickLock;
                if (Time.unscaledTime > dismissAsked + 4f) { dismissAsked = Time.unscaledTime; Refresh(); return; }
                dismissAsked = 0; OfficeFleet.Dismiss(ships[shipSelected]); shipSelected = 0; Refresh(); return;
            }
            if (!ClerkRow) return;
            Click(); lockedUntil = Time.unscaledTime + BookUI.ClickLock;
            var why = ClerkLook.Edit(OfficeSites.Site(house)); if (why != null) Loans.Notify(why);
        }
        internal static bool IsOpen => Instance && Instance.root && Instance.root.activeSelf;
        internal static bool IsOpenFor(int h) => IsOpen && Instance.house == h;
        internal static void Open(int h)
        {
            if (!Office.Live(h)) return;
            if (!Instance || !Instance.root)
            {
                var anchor = CurrencyExchangeUI.instance ? CurrencyExchangeUI.instance.transform.parent : null; if (!anchor) return;
                var holder = new GameObject("trade office UI"); holder.layer = anchor.gameObject.layer; holder.transform.SetParent(anchor, false);
                holder.SetActive(false);
                Instance = holder.AddComponent<OfficePanel>();
                if (!Instance.Build(holder)) { Destroy(holder); Instance = null; return; }
            }
            Instance.Show(h);
        }
        internal static void Close() { if (IsOpen) Instance.Hide(); }
        private bool Build(GameObject holder)
        {
            root = holder;
            var mission = root.transform.parent.Find("mission UI");
            var bgSource = mission ? mission.Find("mission_details_UI_bg") : null; var source = bgSource ? bgSource.Find("details UI") : null;
            var titleSource = source ? source.Find("text name") : null;
            var labelSource = source ? source.Cast<Transform>().FirstOrDefault(t => t.name == "static text") : null;
            var valueSource = source ? source.Find("text (destination)") : null; var buttonSource = source ? source.Cast<Transform>().FirstOrDefault(t => t.name == "accept/cancel button") : null;
            if (!titleSource || !labelSource || !valueSource || !buttonSource) { Plugin.Log.LogWarning("Trade office scroll: mission screen parts not found"); return false; }
            var bg = Instantiate(bgSource.gameObject, root.transform); bg.name = "trade office scroll bg";
            foreach (var child in bg.transform.Cast<Transform>().ToArray()) DestroyImmediate(child.gameObject);
            bg.transform.localPosition = new Vector3(-.22f, -.008f, -.167f); bg.transform.localRotation = new Quaternion(-.707107f, 0, 0, .707107f);
            bg.transform.localScale = new Vector3(.658913f, 1, 1); bg.SetActive(true);
            details = new GameObject("details UI").transform; details.gameObject.layer = bg.layer; details.SetParent(bg.transform, false);
            details.localPosition = new Vector3(-.167f, -.005f, -.182f); details.localScale = new Vector3(.409766f, .27f, .270002f);
            var small = new Vector3(.0068f, .0085f, .0085f);
            title = Text(titleSource, details, "title", 0, .851f, .648f, null, null, null, 1.5f);
            // The tabs (§4.2: "a tab appears when its upgrade is bought"), under the title, each label on a plate copied from the port's mission list's
            // tab button, with its chosen and unchosen looks, the whole plate clickable (the user, 2026-11-08: "Make Upgrade, Item, Fleet option bigger,
            // move other downward a bit. and make that 3 have button background so player know it can be click"); the plates sized and placed in
            // Refresh. The chosen tab shows by its plate, as the game's tabs do (no brackets).
            var tabSource = mission ? mission.Find("current mission buttons/page buttons/mission button (local)") : null;
            var worldTab = MissionListUI.instance ? Fields.Get<GPButtonMissionListWorld>(MissionListUI.instance, "localButton") : null;
            tabOnMat = worldTab ? worldTab.activeMat : null; tabOffMat = worldTab ? worldTab.inactiveMat : null;
            for (int t = 0; t < tabs.Length; t++)
            {
                int k = t; Action act = () => { Click(); if (k == 2) { int h = house; Hide(); OfficeItemsPage.Open(h); return; } tab = k; routeOpen = false; Refresh(); };
                tabs[t] = Text(valueSource, details, "tab " + t, .676f - .27f * t, TabY, .648f, small * 1.6f, TextAnchor.MiddleCenter, TextAlignment.Center, .32f);
                tabBoxes[t] = tabs[t].gameObject.AddComponent<BoxCollider>();
                var a = tabs[t].gameObject.AddComponent<LoanAction>(); a.action = act; a.lookText = "";
                if (!tabSource) continue;
                var plate = OfficeUiParts.Clone(tabSource.gameObject, details, true); plate.name = "tab plate " + t;
                var label = plate.transform.Find("text"); if (label) DestroyImmediate(label.gameObject);
                var trigger = plate.transform.Find("bg+trigger"); if (trigger) OfficeUiParts.Action(trigger.gameObject, act, TabNames[t]);
                plate.SetActive(false); tabPlates[t] = plate;
            }
            // The route list's plates: the tabs' plates (the ship's route on the chosen look, the others on the unchosen), each label as the tabs' (shrunk
            // to the plate), the whole plate clickable; placed and shown by RefreshFleet (ShowRoutes).
            for (int r = 0; r < RouteMax; r++)
            {
                int k = r; Action act = () => PickRoute(k);
                routeTexts[r] = Text(valueSource, details, "route " + r, RouteX, 0, .648f, small * 1.6f, TextAnchor.MiddleCenter, TextAlignment.Center, RouteW - 2 * TabPad);
                routeBoxes[r] = routeTexts[r].gameObject.AddComponent<BoxCollider>(); routeBoxes[r].enabled = false;
                routeLabelActions[r] = routeTexts[r].gameObject.AddComponent<LoanAction>(); routeLabelActions[r].action = act; routeLabelActions[r].lookText = "";
                if (!tabSource) continue;
                var plate = OfficeUiParts.Clone(tabSource.gameObject, details, true); plate.name = "route plate " + r;
                var label = plate.transform.Find("text"); if (label) DestroyImmediate(label.gameObject);
                var trigger = plate.transform.Find("bg+trigger"); if (trigger) routeActions[r] = OfficeUiParts.Action(trigger.gameObject, act, "");
                plate.SetActive(false); routePlates[r] = plate;
            }
            for (int i = 0; i < rows.Length; i++)
            {
                int index = i;
                // .09 lower than first built, a little closer (.11 apart), under the bigger tabs; the eighth row still clears the status line.
                rows[i] = Text(valueSource, details, "row " + i, .676f, .53f - .11f * i, .648f, null, TextAnchor.UpperLeft, TextAlignment.Left, .62f);
                rowBoxes[i] = rows[i].gameObject.AddComponent<BoxCollider>();
                var a = rows[i].gameObject.AddComponent<LoanAction>(); a.action = () =>
                {
                    if (tab == 5) shipSelected = index; else if (tab == 1) crewSelected = index; else selected = index;
                    routeOpen = false; Click(); Refresh();
                };
                a.lookText = "Select";
            }
            infoTitle = Text(labelSource, details, "info title", .02f, InfoTitleTop, .648f, null, TextAnchor.UpperLeft, TextAlignment.Left, .85f);
            infoText = Text(labelSource, details, "info text", .02f, InfoTop, .648f, small, TextAnchor.UpperLeft, TextAlignment.Left, .85f);
            infoScale = infoText.transform.localScale;
            infoPrice = Text(valueSource, details, "info price", .02f, .05f, .648f, null, TextAnchor.UpperLeft, TextAlignment.Left, .85f);
            infoCorner = Text(valueSource, details, "info corner", CornerX, InfoTitleTop, .648f, null, TextAnchor.UpperRight, TextAlignment.Right, .3f);
            status = Text(valueSource, details, "status", 0, -.32f, .648f, null, TextAnchor.MiddleCenter, TextAlignment.Center, 1.55f);
            // Three buttons as Shipwright's storage page sets them (0.7 of the mission screen's, at 0.49, 0 and -0.49).
            close = Button(buttonSource, "close", .49f, () => { Click(); Hide(); }, out closeText, out _);
            extra = Button(buttonSource, "extra", 0f, Extra, out extraText, out extraMaterial);
            confirm = Button(buttonSource, "confirm", -.49f, Confirm, out confirmText, out confirmMaterial);
            foreach (var b in new[] { close, extra, confirm }) b.transform.parent.localScale = Vector3.Scale(b.transform.parent.localScale, new Vector3(.7f, .7f, 1f));
            confirmColor = confirmMaterial.HasProperty("_Color") ? confirmMaterial.color : Color.white;
            closeText.text = "Close"; close.lookText = "Close";
            Measure();
            return true;
        }
        // Where the buttons' plates begin (their top in the scroll's frame, as built and scaled): the status line just above them, the right side's text
        // down to it, or to the buttons when there is no status (SetInfo).
        private void Measure()
        {
            var r = close ? close.GetComponent<MeshRenderer>() : null; var mf = r ? r.GetComponent<MeshFilter>() : null;
            if (mf && mf.sharedMesh) buttonsTop = BookUI.InFrameBounds(r, details).max.y;
            float h = LineHeight(status);
            statusY = buttonsTop + StatusGap + h / 2; bottomStatus = statusY + h / 2 + StatusGap; bottomFree = buttonsTop + StatusGap;
            var p = status.transform.localPosition; status.transform.localPosition = new Vector3(p.x, statusY, p.z);
        }
        private static TextMesh Text(Transform source, Transform parent, string name, float x, float y, float z, Vector3? scale, TextAnchor? anchor, TextAlignment? alignment, float width)
        {
            var copy = GameText.Bare(source.gameObject, parent, name);
            copy.transform.localPosition = new Vector3(x, y, z); copy.transform.localRotation = source.localRotation; copy.transform.localScale = scale ?? source.localScale;
            var text = copy.GetComponent<TextMesh>(); text.text = ""; text.color = Color.black;
            if (anchor.HasValue) text.anchor = anchor.Value; if (alignment.HasValue) text.alignment = alignment.Value;
            if (width > 0) { var fit = copy.AddComponent<LoanTextFit>(); fit.originalScale = copy.transform.localScale; fit.maxWidth = width; }
            copy.SetActive(true);
            return text;
        }
        private LoanAction Button(Transform source, string name, float x, Action action, out TextMesh text, out Material material)
        {
            var copy = Instantiate(source.gameObject, details); copy.name = name;
            copy.transform.localPosition = new Vector3(x, -.602f, .639f);
            foreach (var part in new[] { "bg ", "text (mission count)", "bg (mission count)" }) { var c = copy.transform.Find(part); if (c) DestroyImmediate(c.gameObject); }
            var trigger = copy.transform.Find("bg+trigger");
            foreach (var extra in trigger.GetComponents<Component>()) if (!(extra is Transform) && !(extra is MeshFilter) && !(extra is MeshRenderer) && !(extra is Collider)) DestroyImmediate(extra);
            var button = trigger.gameObject.AddComponent<LoanAction>(); button.action = action;
            var renderer = trigger.GetComponent<MeshRenderer>(); material = new Material(renderer.sharedMaterial); renderer.sharedMaterial = material; button.ownedMaterial = material;
            text = copy.transform.Find("text").GetComponent<TextMesh>(); text.color = Color.black;
            copy.SetActive(true);
            return button;
        }
        private static void Click() { if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUIClickSound(); }
        private void Show(int h)
        {
            TreasuryBook.Close(); OfficeItemsPage.Close();
            house = h; listed = Office.Listed;
            selected = Math.Max(0, Array.FindIndex(listed, u => !Office.Has(h, u.Bit))); tab = 0; crewSelected = 0; candidatesPeriod = -1;
            shipSelected = 0; routeOpen = false;
            if (!root.activeSelf) { MouseLook.ToggleMouseLookAndCursor(false); root.SetActive(true); Refs.SetPlayerControl(false); }
            timer = 0; Refresh();
        }
        private void Hide()
        {
            if (!root.activeSelf) return;
            root.SetActive(false); MouseLook.ToggleMouseLookAndCursor(true); Refs.SetPlayerControl(true); house = 0;
        }
        private void Update()
        {
            if (!root || !root.activeSelf) return;
            if (!Office.Live(house)) { Hide(); return; }
            if ((timer -= Time.unscaledDeltaTime) > 0) return; timer = .5f;
            Refresh();
        }
        private void Enable(bool can) => Enable(confirm, confirmMaterial, can);
        private void Enable(LoanAction button, Material material, bool can)
        {
            button.unclickable = !can;
            if (material.HasProperty("_Color")) material.color = can ? confirmColor : new Color(.604f, .604f, .604f, confirmColor.a);
        }
        // Wages owed: the scroll opens only to pay them (Q17, the user: "you have to pay at office UI before you can proceed anything"). The
        // Treasury book stays open to them (its coins pay the wages).
        private bool Owed => Office.Wages(house) > 0;
        private void Confirm()
        {
            if (Time.unscaledTime < lockedUntil) return;
            Click();
            if (Owed) { var why = Office.PayWages(house); if (why != null) Loans.Notify(why); }
            else if (tab == 1) { if (crewSelected < candidates.Count) OfficeCrew.Hire(house, candidates[crewSelected]); }
            // Assign route / Change route opens the route list, Cancel closes it (RefreshFleet shows it; a plate's click picks: PickRoute).
            else if (tab == 5) routeOpen = !routeOpen;
            else if (ClerkRow) { var site = OfficeSites.Site(house); if (site && site.Clerk) { Hide(); ClerkPlacer.Begin(site, confirm); return; } Loans.Notify("The clerk isn't here yet"); }
            else if (selected < listed.Length) Office.Buy(house, listed[selected].Bit);
            lockedUntil = Time.unscaledTime + BookUI.ClickLock;
            Refresh();
        }
        internal void Refresh()
        {
            if (!Office.Live(house)) return;
            var site = OfficeSites.Site(house); string portName = site && site.Port ? site.Port.GetPortName() : Pledges.Name(house);
            int coin = Office.Coin(house);
            title.text = "Trade office · " + portName;
            if (!TabShown(tab)) tab = 0;
            // The shown tabs side by side from the left (obk-2: with Regional and Modded gone from the scroll, Items alone in the third slot sat over
            // the right column's title), each plate as wide as its label and the room each side, the next after a gap; the label centred on it.
            float tabX = .676f;
            for (int t = 0; t < tabs.Length; t++)
            {
                bool shown = !Owed && TabShown(t) && (t == 0 ? tabs.Skip(1).Select((_, j) => TabShown(j + 1)).Any(x => x) : true);
                tabs[t].text = shown ? TabName(t) : "";
                if (tabPlates[t]) tabPlates[t].SetActive(shown);
                if (shown)
                {
                    float w = Mathf.Min(.32f, BookUI.MeasureLine(tabs[t], TabName(t))) + 2 * TabPad, middle = tabX - w / 2; // x grows leftward
                    var at = tabs[t].transform.localPosition; tabs[t].transform.localPosition = new Vector3(middle, TabY + TabLabelLift, at.z);
                    if (tabPlates[t]) FitPlate(tabPlates[t], middle, w, t == tab);
                    tabX -= w + TabGap;
                }
                tabBoxes[t].enabled = shown; if (shown) FitBox(tabs[t], tabBoxes[t]);
                tabs[t].GetComponent<LoanAction>().lookText = TabName(t);
            }
            // The rows serve every tab; only the Upgrades tab greys some (below). The route list shows only on the Fleet tab.
            foreach (var r in rows) r.color = Color.black;
            confirm.transform.parent.gameObject.SetActive(true); Enable(extra, extraMaterial, true);
            if (Owed || tab != 5) { routeOpen = false; HideRoutes(); }
            if (Owed)
            {
                int owed = Office.Wages(house);
                for (int i = 0; i < rows.Length; i++) { rows[i].text = ""; rowBoxes[i].enabled = false; }
                infoTitle.text = "The clerk's wages";
                // The user's words (2026-11-08), wrapped to the scroll's right side; how long before he quits under them (30 days unpaid).
                SetInfo("You don't have enough gold in the treasury to pay the clerk. You cannot use the trade office until you have paid off their salary.",
                    "Owed: " + owed.ToString("N0") + " " + PlayerGold.GetCurrencyName(coin) + "\nTreasury: " + Office.Treasury(Office.Region(house), coin).ToString("N0") +
                    "\nHe quits in " + Days(Math.Max(1, Rules.ClerkOwedDays - Office.OwedDays(house))), "");
                confirmText.text = "Pay wages"; confirm.lookText = "Pay wages"; Enable(true);
                extra.transform.parent.gameObject.SetActive(false);
                return;
            }
            if (tab == 1) { RefreshCrew(); return; }
            if (tab == 5) { RefreshFleet(); return; }
            extra.transform.parent.gameObject.SetActive(ClerkRow && ClerkLook.Available);
            if (ClerkRow && ClerkLook.Available) { extraText.text = "Clerk's look"; extra.lookText = "Clerk's look"; }
            for (int i = 0; i < rows.Length; i++)
            {
                bool exists = i < listed.Length; var row = exists ? listed[i] : null;
                bool has = exists && Office.Has(house, row.Bit);
                // No "(bought)" after a bought upgrade (the user, 2026-11-08: "don't show (bought) behind the bought upgrade, it clog up ui too much");
                // a bought one paused (no clerk) keeps "(paused)".
                rows[i].text = exists ? (i == selected ? "> " : "") + row.Name + (has && !Office.Working(house, row.Bit) ? "  (paused)" : "") : "";
                rowBoxes[i].enabled = exists; if (exists) FitBox(rows[i], rowBoxes[i]);
                // Grey when it can't be bought now: not enough gold, or the upgrade it needs not bought yet (the user, 2026-11-08). Bought rows
                // stay black. Still clickable, so its text and reason can be read.
                if (exists && !has && Office.UpgradeRefusal(house, row.Bit) != null) rows[i].color = Unavailable;
            }
            var u = listed[Mathf.Clamp(selected, 0, listed.Length - 1)];
            infoTitle.text = u.Name;
            bool bought = Office.Has(house, u.Bit), paused = bought && !Office.Working(house, u.Bit);
            var refusal = bought ? null : Office.UpgradeRefusal(house, u.Bit);
            // A bought upgrade: "Bought" in the corner, no price under its text.
            SetInfo(u.Bit == OfficeUpgrade.Clerk ? string.Format(u.Text, Office.Salary(house).ToString("N0"), PlayerGold.GetCurrencyName(coin)) : u.Text,
                bought ? "" : "Price: " + u.Price.ToString("N0") + " gold\nYou have " + PlayerGold.currency[3].ToString("N0") + " gold",
                ClerkRow ? "" : paused ? "Paused until a clerk is hired again" : bought ? "" : refusal ?? "", bought ? "Bought" : "");
            if (ClerkRow) { confirmText.text = "Move clerk"; confirm.lookText = "Move clerk"; Enable(true); return; }
            confirmText.text = bought ? "Bought" : "Buy"; confirm.lookText = confirmText.text; Enable(!bought && refusal == null);
        }
        private static string Days(int n) => n + (n == 1 ? " day" : " days");
        // The right side's text, the price under it, the status line and the corner: the text wrapped to the side's width, shrunk (to 0.6 at most)
        // until both end above the status line, or above the buttons when there is no status (the user, 2026-11-08 night: "Use empty space"), the price
        // placed right under the text; the corner ("Bought") on the title's line, the title narrowed beside it.
        // wholeRows: the text's first rows kept on one line each where one size smaller allows it (the Fleet tab's summary: its rows grown to 0.92 wrapped
        // "Trader type: Free inter-region trader", obk-19, after the user's "shorten both" put the fee and the estimate on one line); else the biggest size that fits.
        private void SetInfo(string text, string price, string statusText, string cornerText = "", int wholeRows = 0)
        {
            status.text = statusText ?? ""; infoCorner.text = cornerText ?? "";
            var titleFit = infoTitle.GetComponent<LoanTextFit>();
            float room = infoCorner.text.Length == 0 ? TitleWidth : Mathf.Min(TitleWidth, .02f - CornerX - BookUI.MeasureLine(infoCorner, infoCorner.text) - CornerGap);
            // A title too long beside the corner goes on two lines at full size (shrunk to fit, "Local office cooperation" drew at about half its
            // size, obk-16), and the text starts a title's line lower.
            if (infoCorner.text.Length > 0) infoTitle.text = WrapTo(infoTitle, infoTitle.text.Replace('\n', ' '), room);
            if (titleFit) titleFit.maxWidth = room;
            float titleLine = LineHeight(infoTitle) / Mathf.Max(1e-5f, Mathf.Abs(infoTitle.transform.localScale.y)) * Mathf.Abs((titleFit ? titleFit.originalScale : infoTitle.transform.localScale).y);
            float top = InfoTop - (Lines(infoTitle.text) - 1) * titleLine;
            var tp = infoText.transform.localPosition; infoText.transform.localPosition = new Vector3(tp.x, top, tp.z);
            float bottom = status.text.Length == 0 ? bottomFree : bottomStatus;
            var fit = infoText.GetComponent<LoanTextFit>(); string wrapped = text ?? ""; float height = 0, priceH = Lines(price) * LineHeight(infoPrice);
            var sizes = new[] { 1f, .92f, .85f, .78f, .72f, .66f, .6f }; var paras = (text ?? "").Split('\n');
            bool Try(float s)
            {
                InfoShrink = s;
                if (fit) fit.originalScale = infoScale * s; infoText.transform.localScale = infoScale * s;
                wrapped = WrapTo(infoText, text ?? "", InfoWidth);
                height = Lines(wrapped) * LineHeight(infoText);
                return top - height - .02f - priceH >= bottom;
            }
            // the biggest size that fits; with wholeRows, one size smaller where that keeps those rows whole (not more: a ship with no route, its fee and
            // estimate "none until its route is set", went from 0.85 to 0.72 for them, obk-20)
            int normal = sizes.Length - 1;
            for (int k = 0; k < sizes.Length; k++) if (Try(sizes[k])) { normal = k; break; }
            int pick = normal;
            if (wholeRows > 0)
                for (int k = normal; k <= Math.Min(normal + 1, sizes.Length - 1); k++)
                    if (Try(sizes[k]) && paras.Take(wholeRows).All(r => BookUI.MeasureLine(infoText, r) <= InfoWidth)) { pick = k; break; }
            Try(sizes[pick]);
            infoText.text = wrapped; infoPrice.text = price ?? ""; InfoLines = Lines(wrapped);
            var p = infoPrice.transform.localPosition; infoPrice.transform.localPosition = new Vector3(p.x, top - height - (height > 0 ? .02f : 0f), p.z);
        }
        private static int Lines(string s) => string.IsNullOrEmpty(s) ? 0 : s.Split('\n').Length;
        // A line's height in the scroll's units, as the text draws it (font line height x character size x its scale).
        private static float LineHeight(TextMesh tm)
        {
            var font = tm.font; if (!font) return .085f;
            int size = tm.fontSize > 0 ? tm.fontSize : font.fontSize;
            return font.lineHeight * (float)size / Mathf.Max(1, font.fontSize) * tm.lineSpacing * tm.characterSize * .1f * Mathf.Abs(tm.transform.localScale.y);
        }
        // Each paragraph wrapped by words to a width, measured with the text's own font and scale (BookUI.MeasureLine).
        private static string WrapTo(TextMesh tm, string text, float width)
        {
            var result = new System.Text.StringBuilder();
            foreach (var para in text.Split('\n'))
            {
                if (result.Length > 0) result.Append('\n');
                string line = "";
                foreach (var word in para.Split(' '))
                {
                    string next = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && BookUI.MeasureLine(tm, next) > width) { result.Append(line).Append('\n'); line = word; } else line = next;
                }
                result.Append(line);
            }
            return result.ToString();
        }
        // The Crew tab (§4.8): the 5 candidates of this period on the left, the chosen one's role and stats on the right, Hire.
        private void RefreshCrew()
        {
            extra.transform.parent.gameObject.SetActive(false);
            if (candidatesPeriod != OfficeCrew.Period || candidates.Count == 0) { candidates = OfficeCrew.Candidates(house); candidatesPeriod = OfficeCrew.Period; }
            else foreach (var c in candidates) c.Hired = c.Hired || IsHired(c.Slot);
            for (int i = 0; i < rows.Length; i++)
            {
                bool exists = i < candidates.Count;
                rows[i].text = exists ? (i == crewSelected ? "> " : "") + candidates[i].Name + ", " + OfficeCrew.RoleNames[candidates[i].Role] + (candidates[i].Hired ? "  (hired)" : "") : "";
                rowBoxes[i].enabled = exists; if (exists) FitBox(rows[i], rowBoxes[i]);
            }
            var c0 = crewSelected < candidates.Count ? candidates[crewSelected] : null;
            infoTitle.text = c0 != null ? c0.Name : "No candidates";
            var why = OfficeCrew.Refusal(house, c0);
            SetInfo(c0 != null ? OfficeCrew.RoleNames[c0.Role] + "\n" + c0.Stats + "\n\nNew candidates every 7 days.\nPaid as Virtual Crew pays its crew." : "",
                c0 == null ? "" : c0.Hired ? "Hired" : "Hiring costs nothing", c0 != null && !c0.Hired ? why ?? "" : "");
            confirmText.text = c0 != null && c0.Hired ? "Hired" : "Hire"; confirm.lookText = confirmText.text; Enable(why == null);
        }
        // The Fleet tab (§4.5): this office's ships on the left; the chosen one's summary on the right, the Treasury book's Trade fleet rows
        // (OfficeFleet.SummaryLabels); Assign route for a ship waiting for its route, else Change route: either opens the route list (the routes its
        // hull may take, the user's names; a pick sets it, and a ship waiting starts trading), Cancel closes it; Dismiss (asked twice).
        private void RefreshFleet()
        {
            var s = Office.State; var ships = OfficeFleet.Of(house).ToList();
            if (shipSelected >= ships.Count) shipSelected = 0;
            for (int r = 0; r < rows.Length; r++)
            {
                bool ship = r < ships.Count;
                rows[r].text = ship ? (r == shipSelected ? "> " : "") + s.fleetName[ships[r]] + ", " + Pledges.Name(s.fleetHull[ships[r]]) : "";
                rowBoxes[r].enabled = ship; if (ship) FitBox(rows[r], rowBoxes[r]);
            }
            if (ships.Count == 0)
            {
                routeOpen = false; HideRoutes(); extra.transform.parent.gameObject.SetActive(false);
                infoTitle.text = "Trade fleet"; SetInfo("No ships yet. Add one at Shipwright's storage in a yard of this region: up to 3 ships for each office.", "", "");
                confirmText.text = "Change route"; confirm.lookText = "Change route"; Enable(false); return;
            }
            int k = ships[shipSelected];
            infoTitle.text = s.fleetName[k];
            bool set = OfficeFleet.Assigned(k);
            var allowed = OfficeFleet.Allowed(s.fleetHull[k]);
            // A hull with one route keeps it once set.
            bool only = set && allowed.Length < 2;
            if (only || allowed.Length == 0) routeOpen = false;
            if (routeOpen)
            {
                // The list over the right side (the summary hidden under it: a label draws over a plate whatever its depth); Dismiss hidden meanwhile.
                extra.transform.parent.gameObject.SetActive(false);
                SetInfo("Choose its route:", "", "");
                ShowRoutes(k, allowed);
                confirmText.text = "Cancel"; confirm.lookText = "Cancel"; Enable(true);
                return;
            }
            HideRoutes();
            extra.transform.parent.gameObject.SetActive(true);
            // The Treasury book's Trade fleet rows, label and value (the user, 2026-11-08: "please match the text from trade office fleet control to the
            // trade fleet summary").
            var labels = OfficeFleet.SummaryLabels; var values = OfficeFleet.SummaryValues(k);
            // every row but the last (Location, free text) on one line where a size allows it
            SetInfo(string.Join("\n", Enumerable.Range(0, labels.Length).Select(r => labels[r] + ": " + values[r]).ToArray()), "", only ? "This hull takes only this route" : "", "", labels.Length - 1);
            string button = set ? "Change route" : "Assign route";
            confirmText.text = button; confirm.lookText = button; Enable(!only && allowed.Length > 0);
            extraText.text = Time.unscaledTime < dismissAsked + 4f ? "Sure? Dismiss" : "Dismiss"; extra.lookText = extraText.text;
        }
        // The route list: a plate for each route the hull may take, top to bottom in the routes' order, right under "Choose its route:" (at the
        // buttons it stood apart from that line, obk-16); the ship's route on the tabs' chosen look, the others on the unchosen; every label at one size
        // (the longest one's fit: obk-16's labels were each shrunk to its own); a route it can't take now pale on the dark plate (the scroll's grey was
        // hardly seen there, obk-16), the scroll's grey on the light one; its click says why.
        internal static readonly Color RefusedOnPlate = new Color32(170, 170, 170, 255);
        private void ShowRoutes(int ship, int[] kinds)
        {
            routeKinds = kinds; int n = Math.Min(kinds.Length, RouteMax), current = Office.State.fleetKind[ship], region = Office.Region(Office.State.fleetHouse[ship]);
            float first = InfoTop - LineHeight(infoText) - 2 * RouteGap - RouteH / 2;
            float labelRoom = RouteW - 2 * TabPad, widest = 0;
            for (int r = 0; r < n; r++) widest = Mathf.Max(widest, BookUI.MeasureLine(routeTexts[r], OfficeFleet.RouteName(kinds[r], region)));
            float factor = widest > labelRoom ? labelRoom / widest : 1f;
            for (int r = 0; r < RouteMax; r++)
            {
                bool shown = r < n;
                if (routePlates[r]) routePlates[r].SetActive(shown);
                routeBoxes[r].enabled = shown;
                string name = shown ? OfficeFleet.RouteName(kinds[r], region) : "";
                var fit = routeTexts[r].GetComponent<LoanTextFit>(); if (fit && shown) fit.maxWidth = factor < 1f ? BookUI.MeasureLine(routeTexts[r], name) * factor : labelRoom;
                routeTexts[r].text = name;
                if (!shown) continue;
                float y = first - r * (RouteH + RouteGap);
                // The label at its plate's depth: at the labels' own depth (0.648) the UI camera's perspective drew each 9-15 px left of its plate, this
                // far right of the screen's middle (obk-16, obk-17, obk-18; the user: "the text is not in the middle of the button for every route").
                // A label draws over its plate whatever their depth.
                routeTexts[r].transform.localPosition = new Vector3(RouteX, y + TabLabelLift, PlateZ());
                bool chosen = kinds[r] == current;
                routeTexts[r].color = OfficeFleet.KindRefusal(ship, kinds[r]) == null ? Color.black : chosen ? Unavailable : RefusedOnPlate;
                if (routePlates[r]) FitPlate(routePlates[r], RouteX, y, RouteW, RouteH, chosen);
                FitBox(routeTexts[r], routeBoxes[r]);
                routeLabelActions[r].lookText = name; if (routeActions[r]) routeActions[r].lookText = name;
            }
        }
        private void HideRoutes()
        {
            for (int r = 0; r < RouteMax; r++) { if (routePlates[r]) routePlates[r].SetActive(false); if (routeTexts[r]) routeTexts[r].text = ""; if (routeBoxes[r]) routeBoxes[r].enabled = false; }
        }
        // A route picked: set for the chosen ship (a ship waiting for its route starts trading: OfficeFleet.SetKind) and the list closed; one it can't
        // take now leaves the list open with the reason in a notice; its own route only closes it.
        private void PickRoute(int r)
        {
            if (!routeOpen || tab != 5 || r >= routeKinds.Length) return;
            Click();
            var ships = OfficeFleet.Of(house).ToList(); if (shipSelected >= ships.Count) { routeOpen = false; Refresh(); return; }
            int i = ships[shipSelected], kind = routeKinds[r];
            var why = OfficeFleet.KindRefusal(i, kind); if (why != null) { Loans.Notify(why); return; }
            routeOpen = false;
            if (Office.State.fleetKind[i] != kind) OfficeFleet.SetKind(i, kind);
            lockedUntil = Time.unscaledTime + BookUI.ClickLock;
            Refresh();
        }
        private bool IsHired(int slot) { var st = World.State?.office; int i = Office.Slot(house); return st?.crewHired != null && st.crewPeriod != null && st.crewPeriod[i] == OfficeCrew.Period && (st.crewHired[i] & (1 << slot)) != 0; }
        // The row's click box: its text's drawn size, in its own frame.
        // A tab's plate (or a route's): the chosen or unchosen look, w wide and h high in the scroll's frame, its middle at (middle, y), as deep as the
        // Close button's plate (in front of the parchment; the labels draw over it whatever their depth).
        private void FitPlate(GameObject plate, float middle, float w, bool chosen) => FitPlate(plate, middle, TabY, w, TabH, chosen);
        private void FitPlate(GameObject plate, float middle, float y, float w, float h, bool chosen)
        {
            var r = plate.GetComponentInChildren<MeshRenderer>(true); if (!r) return;
            if (tabOnMat && tabOffMat) r.sharedMaterial = chosen ? tabOnMat : tabOffMat;
            var b = BookUI.InFrameBounds(r, details); var s = plate.transform.localScale;
            if (b.size.x > 1e-5f) s.x *= w / b.size.x; if (b.size.y > 1e-5f) s.y *= h / b.size.y;
            plate.transform.localScale = s;
            float z = PlateZ();
            b = BookUI.InFrameBounds(r, details);
            plate.transform.localPosition += new Vector3(middle - b.center.x, y - b.center.y, z - b.center.z);
        }
        // The plates' depth in the scroll's frame: the Close button's plate's.
        private float PlateZ() { var closeLook = close ? close.GetComponent<MeshRenderer>() : null; return closeLook ? BookUI.InFrameBounds(closeLook, details).center.z : .64f; }
        // A text button's collider: the text's own drawn box in its local space. Read with the text turned square to the world for that moment
        // (Unity 2019 has no Renderer.localBounds, and a TextMesh no MeshFilter for BookUI.InFrameBounds): the scroll hangs off the UI camera at a
        // slant, and its world box is larger than the text; taken back into local space it made each collider about twice the text's width, over
        // its neighbours, so the cursor lit the next row or none (officeuihover ouh-2: 100 stale; the user: "sometimes when cursor already on top
        // of text, it don't highlight it").
        private static void FitBox(TextMesh text, BoxCollider box)
        {
            var r = text.GetComponent<Renderer>(); if (!r) return;
            var t = text.transform; var turn = t.rotation;
            t.rotation = Quaternion.identity;
            var b = r.bounds; var c = t.InverseTransformPoint(b.center); var s = t.InverseTransformVector(b.size);
            t.rotation = turn;
            box.center = c; box.size = new Vector3(Mathf.Max(Mathf.Abs(s.x), .01f), Mathf.Max(Mathf.Abs(s.y), .01f), Mathf.Max(Mathf.Abs(s.z), .01f));
        }
    }
    // While the clerk is moved, and until its keys are let go after it: the game's own reads of the pick-up, activate and rotate inputs answer "not
    // pressed" (ClerkPlacer.Lets), so the clicks, R, F, Q and E don't also click what the pointer is on (him included), pick up, drop or throw a held
    // item, turn it, or stop the view (GoPointer, MouseLook, every other caller). ClerkPlacer reads the keys through Input, which isn't patched.
    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetKeyDown))] internal static class ClerkMoveKeyDownPatch
    { static bool Prefix(InputName inputName, ref bool __result) => ClerkPlacer.Lets(inputName, ref __result); }
    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetKey))] internal static class ClerkMoveKeyPatch
    { static bool Prefix(InputName inputName, ref bool __result) => ClerkPlacer.Lets(inputName, ref __result); }
    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetKeyUp))] internal static class ClerkMoveKeyUpPatch
    { static bool Prefix(InputName inputName, ref bool __result) => ClerkPlacer.Lets(inputName, ref __result); }
    [HarmonyPatch(typeof(StartMenu), "LateUpdate")] internal static class OfficePanelEscapePatch
    {
        static bool Prefix(bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!Input.GetKeyDown(KeyCode.Escape) && !Input.GetKeyDown(KeyCode.F10) && !Input.GetKeyDown(KeyCode.JoystickButton6)) return true;
            if (ClerkPlacer.Active) { ClerkPlacer.Cancel(); return false; }
            // Player Model's screen uses the key first (the clerk's look).
            if (ClerkLook.KeyTaken) return true;
            if (!GameState.playing || GameState.currentlyLoading || GameState.currentShipyard) return true;
            // The Treasury book's amount window, then the book; the items page.
            if (TreasuryBook.Escape() || OfficeItemsPage.Escape()) return false;
            if (!OfficePanel.IsOpen) return true;
            OfficePanel.Close(); return false;
        }
    }

    // ---- The Treasury tab on the office's exchange screen (§4.4; the user's list item 4: its button where the Bottomry button is) ----
    // At an office's chest the exchange (no fee, the daily rate) has a Treasury tab in the Bottomry tab's place; it closes the exchange and opens the
    // Treasury book (the user, 2026-11-08: "maybe you should use ui same as log book": TreasuryBook).
    public sealed class OfficeTreasuryPage : MonoBehaviour
    {
        private CurrencyExchangeUI ui; private LoanAction tab; private int house; private bool ready;
        internal static OfficeTreasuryPage For(CurrencyExchangeUI ui) { var p = ui.GetComponent<OfficeTreasuryPage>(); if (!p) { p = ui.gameObject.AddComponent<OfficeTreasuryPage>(); p.ui = ui; } return p; }
        internal int House => house;
        private void Build()
        {
            if (ready) return; ready = true;
            var exit = ui.transform.Find("button exit (1)");
            var tabObject = Instantiate(exit.gameObject, ui.transform); tabObject.name = "button treasury tab";
            DestroyImmediate(tabObject.GetComponent<CurrencyExchangeUIButton>());
            foreach (var o in tabObject.GetComponentsInChildren<cakeslice.Outline>(true)) DestroyImmediate(o);
            // The Bottomry tab's place (RentandLoan.cs, BottomryPage.Build).
            tabObject.transform.localPosition = new Vector3(-.3521f, .0706f, -.3643f); tabObject.transform.localRotation = new Quaternion(-.706f, .038f, -.038f, .706f);
            tabObject.transform.localScale = new Vector3(1.8253f, 1.0116f, .9607f);
            tab = tabObject.AddComponent<LoanAction>(); tab.action = OpenTreasury; tab.lookText = "Treasury";
            var tabText = tabObject.transform.Find("text").GetComponent<TextMesh>();
            tabText.transform.localPosition = new Vector3(.0376f, -.0069f, -.0196f); tabText.transform.localRotation = new Quaternion(0, 1, 0, 0);
            tabText.transform.localScale = new Vector3(.001658f, .00301f, .0051f); tabText.text = "Treasury";
        }
        // Opened from a chest: the Treasury tab at an office's box, nothing at any other.
        internal void Opened(Component chest)
        {
            house = OfficeBox.HouseOf(chest);
            if (house == 0) { if (ready) tab.gameObject.SetActive(false); return; }
            Build();
            tab.gameObject.SetActive(World.Ready && Office.Live(house) && Office.Has(house, OfficeUpgrade.Treasury));
        }
        internal void OpenTreasury()
        {
            if (!World.Ready || house == 0 || !Office.Live(house)) return;
            int h = house;
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUIClickSound();
            ui.CloseUI();
            TreasuryBook.Open(h);
        }
    }
    [HarmonyPatch(typeof(CurrencyExchangeUIButton), "OnActivate")] internal static class OfficeTreasuryClickPatch
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(CurrencyExchangeUIButton __instance)
        {
            if (Fields.Get<object>(__instance, "function").ToString() == "openUI") OfficeBox.Opening = __instance;
        }
        // Cleared after the click, every prefix and postfix of the opening done (the booth's and this tab's OpenUI postfixes run in no set order).
        static Exception Finalizer(Exception __exception) { OfficeBox.Opening = null; return __exception; }
    }
    [HarmonyPatch(typeof(CurrencyExchangeUI), "OpenUI")] internal static class OfficeTreasuryOpenPatch
    { static void Postfix(CurrencyExchangeUI __instance) => OfficeTreasuryPage.For(__instance).Opened(OfficeBox.Opening); }

    // ---- The office's books (from OfficeBooks.cs, merged here 2026-11-09 at the user's word: "Tradeoffice.cs and officebook.cs should be merged") ----
    // ---- The treasury's records (the user, 2026-11-08: "recent transaction record, which day, amount, which office"; TRADE_OFFICE_DRAFT.md §3B,
    // "The Treasury screen and the items page redesigned") ----
    // Every change to a region's treasury, saved in OfficeState's rec* arrays (newest last): the day, the office (house), what, the coin and the
    // signed amount. The clerk's wages, the route fees and the trips' shares of one office, coin and day add into one record each; the newest 40
    // of each region are kept.
    // Removed: a fleet ship whose rig needs Shipyard Expansion, in a game without it (OfficeFleet.Loaded; paid as Dismissed).
    internal enum RecordKind { Put = 0, Take = 1, Wages = 2, WagesPaid = 3, Fees = 4, Trip = 5, Dismissed = 6, Removed = 7 }
    internal static class OfficeRecords
    {
        internal const int PerRegion = 40;
        // The events in the words of the buttons and the fleet summary (the user, 2026-11-08: Withdraw and Deposit, "Maintenance fee"); a save keeps
        // the kind's number, so renaming changes nothing saved.
        internal static readonly string[] Names = { "Deposit", "Withdrawal", "Clerk's wages", "Wages paid", "Maintenance fees", "Trip shares", "Ship dismissed", "Removed: mod missing" };   // Removed: Shipyard Expansion or another mod's sail gone at a load (OfficeFleet.Loaded; "no SE" until 2026-11-09)
        private static bool Merges(RecordKind k) => k == RecordKind.Wages || k == RecordKind.Fees || k == RecordKind.Trip;
        internal sealed class Record { internal int Day, House, Kind, Coin, Amount; }
        internal static void Add(int house, RecordKind kind, int coin, long amount)
        {
            var s = World.State?.office; if (s == null || amount == 0) return;
            int day = GameState.day, n = s.recDay?.Length ?? 0;
            if (s.recHouse == null || s.recKind == null || s.recCoin == null || s.recAmount == null || s.recHouse.Length != n || s.recKind.Length != n || s.recCoin.Length != n || s.recAmount.Length != n) { Clear(s); n = 0; }
            // Records come in day order, so today's are the last ones.
            if (Merges(kind))
                for (int i = n - 1; i >= 0 && s.recDay[i] == day; i--)
                    if (s.recHouse[i] == house && s.recKind[i] == (int)kind && s.recCoin[i] == coin) { s.recAmount[i] = Clamp(s.recAmount[i] + amount); return; }
            var all = List(s); all.Add(new Record { Day = day, House = house, Kind = (int)kind, Coin = coin, Amount = Clamp(amount) });
            int region = Office.Region(house), mine = all.Count(r => Office.Region(r.House) == region);
            for (int i = 0; i < all.Count && mine > PerRegion; i++) if (Office.Region(all[i].House) == region) { all.RemoveAt(i--); mine--; }
            Store(s, all);
        }
        // A region's records, the newest first.
        internal static List<Record> Of(int region) { var s = World.State?.office; return s == null ? new List<Record>() : Enumerable.Reverse(List(s)).Where(r => Office.Region(r.House) == region).ToList(); }
        // A bankruptcy closes the region's offices: their records go with them.
        internal static void Clear(int region) { var s = World.State?.office; if (s == null) return; Store(s, List(s).Where(r => region >= 0 && Office.Region(r.House) != region).ToList()); }
        private static int Clamp(long v) => (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, v));
        private static void Clear(OfficeState s) { s.recDay = new int[0]; s.recHouse = new int[0]; s.recKind = new int[0]; s.recCoin = new int[0]; s.recAmount = new int[0]; }
        private static List<Record> List(OfficeState s)
        {
            var l = new List<Record>(); int n = s.recDay?.Length ?? 0;
            if (s.recHouse?.Length != n || s.recKind?.Length != n || s.recCoin?.Length != n || s.recAmount?.Length != n) return l;
            for (int i = 0; i < n; i++) l.Add(new Record { Day = s.recDay[i], House = s.recHouse[i], Kind = s.recKind[i], Coin = s.recCoin[i], Amount = s.recAmount[i] });
            return l;
        }
        private static void Store(OfficeState s, List<Record> l)
        {
            s.recDay = l.Select(r => r.Day).ToArray(); s.recHouse = l.Select(r => r.House).ToArray(); s.recKind = l.Select(r => r.Kind).ToArray();
            s.recCoin = l.Select(r => r.Coin).ToArray(); s.recAmount = l.Select(r => r.Amount).ToArray();
        }
    }

    // Shared by the Treasury book and the items page: copies of the game's own UI parts, made under a switched-off holder so none of their scripts
    // wakes, with their buttons' scripts taken off (LoanAction put on in their place by the caller).
    internal static class OfficeUiParts
    {
        private static GameObject stash;
        internal static GameObject Clone(GameObject source, Transform parent, bool worldPose)
        {
            if (!stash) { stash = new GameObject("trade office UI stash"); stash.SetActive(false); Object.DontDestroyOnLoad(stash); }
            var copy = Object.Instantiate(source, stash.transform, worldPose);
            foreach (var c in copy.GetComponentsInChildren<Component>(true))
                if (c is GoPointerButton && !(c is LoanAction) || c is cakeslice.Outline) Object.DestroyImmediate(c);
            copy.transform.SetParent(parent, worldPose);
            copy.SetActive(true);
            return copy;
        }
        internal static Transform Find(Transform root, string name) => root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
        internal static Transform FindStart(Transform root, string start) => root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.StartsWith(start, StringComparison.Ordinal));
        internal static LoanAction Action(GameObject go, Action action, string look)
        {
            var a = go.GetComponent<LoanAction>() ?? go.AddComponent<LoanAction>(); a.action = action; a.lookText = look; return a;
        }
        internal static Font FontOf(Component root, string name, Font fallback) =>
            root.GetComponentsInChildren<TextMesh>(true).Select(t => t.font).FirstOrDefault(f => f && f.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            ?? Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f && f.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) ?? fallback;
        internal static void Click() { if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUIClickSound(); }
        // The player's view given to a screen, and back (as the office's scroll does).
        internal static void Take() { MouseLook.ToggleMouseLookAndCursor(false); Refs.SetPlayerControl(false); }
        internal static void Give() { MouseLook.ToggleMouseLookAndCursor(true); Refs.SetPlayerControl(true); }
        internal static string ShortCoin(int c) => c == 0 ? "Lions" : c == 1 ? "Dragons" : c == 2 ? "Crowns" : "Gold";
        internal static string OfficeName(int house) { var p = Office.PortOf(house); return p ? p.GetPortName() : Pledges.Name(house); }
        // Words wrapped to a width, measured with the text's own font and scale (BookUI.MeasureLine).
        internal static string Wrap(TextMesh tm, string text, float width)
        {
            var result = new System.Text.StringBuilder(); string line = "";
            foreach (var word in (text ?? "").Split(' '))
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && BookUI.MeasureLine(tm, next) > width) { result.Append(line).Append('\n'); line = word; } else line = next;
            }
            return result.Append(line).ToString();
        }
    }

    // ---- The Treasury book (the user, 2026-11-08: "maybe you should use ui same as log book ... the infos should be presented in form") ----
    // Opened from the office chest's Treasury tab: a copy of the log book's book and bookmarks (the log book itself untouched), its pages drawn as
    // the Loan ledger's (BookUI, the same frame: a child of the game's mission UI). Bookmarks Treasury and, once an office of the region has the
    // fleet, Trade fleet; a Close bookmark under it. Treasury: the currency box's four coins to choose the coin (the user: "Use the currency box's
    // currency selection instead of coins"), a form of each coin's treasury and wallet, Take out and Put in (a window of their own, AmountWindow),
    // and the region's records. Trade fleet: the region's ships as a form, the chosen one's summary as a form.
    public sealed class TreasuryBook : MonoBehaviour
    {
        internal static TreasuryBook Instance;
        private int house, region, coin, tab, page, shipSelected; private float timer;
        private Font font, valueFont;
        private MeshRenderer strip, native; private Material rowNormal, rowSelected;
        private GameObject treasuryPage, fleetPage, logBook;
        private readonly GameObject[] marks = new GameObject[3]; private readonly Material[] markOn = new Material[3], markOff = new Material[3];
        private readonly Vector3[] markOnAt = new Vector3[3], markOffAt = new Vector3[3];
        private readonly Transform[] coins = new Transform[4]; private Transform outline; private readonly TextMesh[] coinNames = new TextMesh[4];
        private Font[] regionFonts;
        private readonly TextMesh[] coinRows = new TextMesh[4], treasuryValues = new TextMesh[4], walletValues = new TextMesh[4];
        private TextMesh subtitle, status, pageLabel, recordsSubtitle;
        // HistoryRows: a ship's trips a page (since the user's "you should have page to see all the trade history", 2026-11-08; its whole history is
        // OfficeFleet.TripLog trips, paged).
        // LifeRows: the rows under the line from the ship's whole life in the fleet (OfficeFleet.LifeRows; the user, 2026-11-08 evening).
        private const int RecordRows = 10, ShipRows = 6, FormRows = 2, HistoryRows = 7, LifeRows = 3;
        private readonly TextMesh[] recDay = new TextMesh[RecordRows], recOffice = new TextMesh[RecordRows], recWhat = new TextMesh[RecordRows], recAmount = new TextMesh[RecordRows];
        private LoanAction take, put; private readonly Dictionary<Material, Texture> plateTextures = new Dictionary<Material, Texture>();
        private TextMesh fleetSubtitle, fleetEmpty, shipTitle;
        private readonly TextMesh[] lifeLabels = new TextMesh[LifeRows], lifeValues = new TextMesh[LifeRows];
        private static readonly string[] LifeLabelTexts = { "Most visited port", "Most sold goods", "Biggest profit trip" };
        private readonly LoanAction[] shipRows = new LoanAction[ShipRows]; private readonly TextMesh[] shipNames = new TextMesh[ShipRows], shipHulls = new TextMesh[ShipRows], shipKinds = new TextMesh[ShipRows];
        private readonly TextMesh[] formLabels = new TextMesh[FormRows], formValues = new TextMesh[FormRows];
        private readonly TextMesh[] histDay = new TextMesh[HistoryRows], histPort = new TextMesh[HistoryRows], histCargo = new TextMesh[HistoryRows], histShare = new TextMesh[HistoryRows];
        private TextMesh historyEmpty, historyPageLabel;
        private Material dividerMaterial;
        private AmountWindow window;
        private static readonly Color Plate = new Color(.38f, .8f, 1), Disabled = new Color(.74f, .71f, .66f);

        internal static bool IsOpen => Instance && Instance.gameObject.activeSelf;
        internal static bool IsOpenFor(int h) => IsOpen && Instance.house == h;
        internal static bool Typing => IsOpen && Instance.window && Instance.window.Open;
        internal static bool Open(int h)
        {
            if (!Office.Live(h) || !Office.Has(h, OfficeUpgrade.Treasury)) return false;
            if (!Instance)
            {
                var log = MissionListUI.instance; if (!log || !EconomyUI.instance || !CurrencyExchangeUI.instance) return false;
                var go = new GameObject("trade office treasury book"); go.layer = log.gameObject.layer; go.SetActive(false); go.transform.SetParent(log.transform, false);
                Instance = go.AddComponent<TreasuryBook>();
                try { if (!Instance.Build(log)) throw new Exception("the log book's parts not found"); }
                catch (Exception e) { Plugin.Log.LogWarning("Trade office: the Treasury book not built: " + e.Message); Destroy(go); Instance = null; return false; }
            }
            Instance.Show(h); return true;
        }
        internal static void Close() { if (IsOpen) Instance.Hide(); }
        // Escape: the amount window first, then the book.
        internal static bool Escape()
        {
            if (!IsOpen) return false;
            if (Instance.window && Instance.window.Open) Instance.window.Cancel(); else Instance.Hide();
            return true;
        }

        private bool Build(MissionListUI log)
        {
            var book = Fields.Get<GameObject>(log, "book"); var modes = Fields.Get<GPButtonLogMode[]>(log, "logModeButtons");
            var trade = EconomyUI.instance;
            if (!book || modes == null) return false;
            logBook = book;
            font = OfficeUiParts.FontOf(log, "Architect", null); valueFont = OfficeUiParts.FontOf(log, "Kaushan", font);
            // Each coin's name in its region's font, the game's reputation page's as the Loan ledger's region buttons (the user, 2026-11-08: "make the
            // currecy name at treasure ui use regional font"); Gold in Chronos', as the ledger.
            var reputation = Fields.Get<GameObject>(log, "reputationUI"); Component fontRoot = reputation ? reputation.transform : log.transform;
            regionFonts = new[] { "Callimundial", "Polo-SemiBold", "IMMORTAL", "IMMORTAL" }.Select(n => OfficeUiParts.FontOf(fontRoot, n, font)).ToArray();
            native = Fields.Get<EconomyUIButton>(trade, "buyButton").GetComponent<MeshRenderer>();
            strip = Fields.Get<EconomyUIButton[]>(trade, "goodsListButtons").First(b => b && b.GetComponent<MeshFilter>()).GetComponent<MeshRenderer>();
            var materials = Fields.Get<Material[]>(trade, "buttonMaterials"); rowNormal = materials[0]; rowSelected = materials[2];
            if (!font || !native || !strip) return false;
            var copy = OfficeUiParts.Clone(book, transform, false); copy.name = "treasury book";
            foreach (Transform child in copy.transform.Cast<Transform>().ToArray()) DestroyImmediate(child.gameObject);
            // The bookmarks: copies of the log's, in its own places (Treasury where reputation is, Trade fleet where daily logs are, Close where
            // current missions is), each with its own selected and unselected look.
            var src = modes.FirstOrDefault(m => m)?.transform.parent; if (!src) return false;
            var holder = new GameObject("treasury bookmarks"); holder.layer = gameObject.layer; holder.transform.SetParent(transform, false);
            holder.transform.localPosition = src.localPosition; holder.transform.localRotation = src.localRotation; holder.transform.localScale = src.localScale;
            var from = new[] { MissionListMode.reputation, MissionListMode.dailyLog, MissionListMode.currentMissions };
            string[] labels = { "Treasury", "Trade fleet", "Close" };
            for (int i = 0; i < 3; i++)
            {
                var m = modes.FirstOrDefault(b => b && Fields.Get<MissionListMode>(b, "mode") == from[i]); if (!m) return false;
                markOn[i] = Fields.Get<Material>(m, "activeMat"); markOff[i] = Fields.Get<Material>(m, "inactiveMat");
                markOnAt[i] = Fields.Get<Vector3>(m, "activeLocalPos"); markOffAt[i] = Fields.Get<Vector3>(m, "inactiveLocalPos");
                marks[i] = OfficeUiParts.Clone(m.gameObject, holder.transform, false); marks[i].name = "bookmark " + labels[i];
                var t = marks[i].GetComponentInChildren<TextMesh>(true); if (t) { t.text = labels[i]; t.color = Color.black; }
                int k = i;
                OfficeUiParts.Action(marks[i], () => { OfficeUiParts.Click(); if (k == 2) { Hide(); return; } tab = k; page = 0; Refresh(); }, labels[i]);
            }
            // Close never shows as chosen.
            var closeLook = marks[2].GetComponent<Renderer>(); if (closeLook && markOff[2]) closeLook.sharedMaterial = markOff[2]; marks[2].transform.localPosition = markOffAt[2];
            BuildTreasury(); BuildFleet();
            window = new GameObject("treasury amount window").AddComponent<AmountWindow>();
            if (!window.Build(this, log, font, valueFont, native, strip)) return false;
            return true;
        }
        private TextMesh T(Transform p, string text, float x, float y, float size, float width = 0, TextAnchor anchor = TextAnchor.LowerLeft, Font f = null) =>
            BookUI.Text(p, f ?? font, text, x, y, size, width, anchor);
        // Emerald's font (Polo-SemiBold) draws its letters lower from a LowerLeft anchor than the book's: in obk-5 "Emerald Dragons" sat 9-10 px
        // below its row's figures at size .0135 (the other regional fonts within 2 px). shortcut: measured for that font at a LowerLeft anchor only,
        // replace with the fonts' own baselines if more regional text is added to the book.
        private static float Lift(Font f, float size) => f && f.name.IndexOf("Polo", StringComparison.OrdinalIgnoreCase) >= 0 ? .615f * size : 0f;
        private MeshRenderer Divider(Transform parent, float x, float y, float width, float thickness = .00055f)
        {
            var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Standard"); if (!shader) return null;
            if (!dividerMaterial) { dividerMaterial = new Material(shader); dividerMaterial.color = Color.black; }
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = "treasury row divider"; go.layer = parent.gameObject.layer; go.transform.SetParent(parent, false);
            go.transform.localPosition = BookUI.Position(x, y); go.transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.back);
            go.transform.localScale = new Vector3(width, thickness, .001f);
            var collider = go.GetComponent<Collider>(); if (collider) DestroyImmediate(collider);
            var r = go.GetComponent<MeshRenderer>(); r.sharedMaterial = dividerMaterial; return r;
        }
        // Every text on the plates' plane (.014; the plates lie at .012). The game's text draws over every mesh whatever its depth, and in front of
        // the page the camera's perspective spread it outward from the spine: at the ledger's depth (.032) the left page's text began left of the
        // book's edge and each button's label sat off its plate (obk-2). On one plane one mapping holds: about 1,090 px a unit at 1280x720, the axis
        // at the spine (x .165, y .154); the left page's inside is x -.176 to .144, the right page's .214 to .51, y .39 to -.04.
        private static void Finish(GameObject view)
        {
            foreach (var text in view.GetComponentsInChildren<TextMesh>(true)) { var at = text.transform.localPosition; text.transform.localPosition = new Vector3(at.x, .014f, at.z); BlackBookText.Apply(text); }
        }
        private void BuildTreasury()
        {
            treasuryPage = new GameObject("treasury page"); treasuryPage.layer = gameObject.layer; treasuryPage.transform.SetParent(transform, false);
            var p = treasuryPage.transform;
            // Left page: the title, the coins, the form, Take out and Put in.
            // The left page starts .035 lower than the Loan ledger's: the log book's top bookmarks lie over its first line (obk-1). Its lines from
            // x -.168 to .14 (inside the page, Finish).
            T(p, "Treasury", -.168f, .345f, .026f, .30f);
            subtitle = T(p, "", -.168f, .318f, .0145f, .30f);
            LedgerOrnament.Create(p, "treasury title ornament", -.172f, .144f, .303f);
            var fx = CurrencyExchangeUI.instance; var sellOutline = Fields.Get<Transform>(fx, "buttonOutlineSell");
            var sources = fx.GetComponentsInChildren<CurrencyExchangeUIButton>(true).Where(b => Fields.Get<object>(b, "function").ToString() == "selectSellCurrency").ToArray();
            for (int i = 0; i < 4; i++)
            {
                var b = sources.FirstOrDefault(x => Fields.Get<int>(x, "index") == i); if (!b) continue;
                var c = OfficeUiParts.Clone(b.gameObject, p, true).transform; c.name = "treasury coin " + i; coins[i] = c;
                foreach (Transform child in c.Cast<Transform>().ToArray()) { if (sellOutline && child.name == sellOutline.name) DestroyImmediate(child.gameObject); else { var t = child.GetComponent<TextMesh>(); if (t) t.text = ""; } }
                // Placed on the page and sized to 0.064 of the book's units, its turn kept (it faces the player as the book does): its face's width
                // from its mesh and scale (a world box's width changes with the way the player faces). On the plates' plane, inside the page.
                var mf = c.GetComponent<MeshFilter>(); float width = mf && mf.sharedMesh ? mf.sharedMesh.bounds.size.x * c.lossyScale.x / Mathf.Max(1e-5f, transform.lossyScale.x) : 0f;
                if (width > 1e-4f) c.localScale *= .064f / width;
                // The row's middle at x -.017, the page's middle between the book's edge and the spine (the user, 2026-11-08: "move the currency
                // icon to middle of the page, now its a little bit off to the right"; at -.12 its middle was 8 px right of it).
                c.position = transform.TransformPoint(BookUI.Position(-.128f + .074f * i, .255f, .014f));
                int k = i; OfficeUiParts.Action(c.gameObject, () => { OfficeUiParts.Click(); coin = k; Refresh(); }, PlayerGold.GetCurrencyName(i));
            }
            if (sellOutline) { var o = OfficeUiParts.Clone(sellOutline.gameObject, transform, false); o.name = "treasury coin outline"; outline = o.transform; }
            // One name a coin, each in its region's font (a font is its own material), the chosen one shown.
            for (int i = 0; i < 4; i++) coinNames[i] = T(p, PlayerGold.GetCurrencyName(i), -.017f, .205f, .018f, .30f, TextAnchor.MiddleCenter, regionFonts[i]);
            T(p, "Currency", -.168f, .172f, .0126f, .13f); T(p, "Treasury", .052f, .172f, .0115f, .095f, TextAnchor.LowerRight); T(p, "Wallet", .14f, .172f, .0115f, .08f, TextAnchor.LowerRight);
            Divider(p, -.014f, .166f, .312f);
            for (int i = 0; i < 4; i++)
            {
                float y = .140f - i * .032f;
                coinRows[i] = T(p, PlayerGold.GetCurrencyName(i), -.168f, y + Lift(regionFonts[i], .0135f), .0135f, .135f, TextAnchor.LowerLeft, regionFonts[i]);
                treasuryValues[i] = T(p, "", .052f, y, .0150f, .095f, TextAnchor.LowerRight, valueFont);
                walletValues[i] = T(p, "", .14f, y, .0150f, .08f, TextAnchor.LowerRight, valueFont);
                Divider(p, -.014f, y - .006f, .312f);
            }
            // The user (2026-11-08): "take out -> Withdraw", "put in -> Deposit".
            take = BookUI.Button(p, native, font, "Withdraw", -.097f, -.006f, .135f, .032f, () => Ask(false), Plate, out _);
            put = BookUI.Button(p, native, font, "Deposit", .069f, -.006f, .135f, .032f, () => Ask(true), Plate, out _);
            status = T(p, "", -.014f, .021f, .0125f, .30f, TextAnchor.MiddleCenter);
            // Right page: the region's records, from x .218 to .5; its title as low as the left page's (at .38 it lay on the book's top edge, obk-2),
            // its page buttons inside the page (at -.054 they hung off its bottom edge).
            T(p, "Recent records", .218f, .345f, .026f, .28f);
            recordsSubtitle = T(p, "", .218f, .318f, .0145f, .28f);
            LedgerOrnament.Create(p, "treasury records ornament", .212f, .504f, .303f);
            // The rows' text .012 and the columns spread (obk-3: "Gold Rock City" ran into the What column).
            T(p, "Day", .216f, .277f, .0115f, .028f); T(p, "Office", .246f, .277f, .0115f, .092f); T(p, "Event", .346f, .277f, .0115f, .08f); T(p, "Amount", .502f, .277f, .0115f, .07f, TextAnchor.LowerRight);
            Divider(p, .359f, .271f, .286f);
            for (int i = 0; i < RecordRows; i++)
            {
                float y = .247f - i * .027f;
                recDay[i] = T(p, "", .216f, y, .012f, .028f); recOffice[i] = T(p, "", .246f, y, .012f, .092f); recWhat[i] = T(p, "", .346f, y, .012f, .08f);
                recAmount[i] = T(p, "", .502f, y, .013f, .07f, TextAnchor.LowerRight, valueFont);
            }
            pageLabel = T(p, "", .36f, -.02f, .015f, .1f, TextAnchor.MiddleCenter);
            BookUI.Button(p, native, font, "<", .26f, -.02f, .03f, .024f, () => { page = Math.Max(0, page - 1); Refresh(); }, Color.white, out _);
            BookUI.Button(p, native, font, ">", .46f, -.02f, .03f, .024f, () => { page++; Refresh(); }, Color.white, out _);
            Finish(treasuryPage);
        }
        private void BuildFleet()
        {
            fleetPage = new GameObject("trade fleet page"); fleetPage.layer = gameObject.layer; fleetPage.transform.SetParent(transform, false);
            var p = fleetPage.transform;
            // The same frame as the treasury page (inside the pages, Finish; the right page's title as low as the left's, obk-2).
            T(p, "Trade fleet", -.168f, .345f, .026f, .30f);
            fleetSubtitle = T(p, "", -.168f, .318f, .0145f, .30f);
            LedgerOrnament.Create(p, "trade fleet title ornament", -.172f, .144f, .303f);
            T(p, "Ship", -.168f, .272f, .0126f, .12f); T(p, "Hull", -.035f, .272f, .0126f, .07f); T(p, "Trader type", .045f, .272f, .0126f, .095f);
            Divider(p, -.014f, .265f, .312f);
            for (int i = 0; i < ShipRows; i++)
            {
                float y = .235f - i * .040f; int k = i;
                shipRows[i] = BookUI.Button(p, strip, font, "", -.014f, y, .312f, .033f, () => { OfficeUiParts.Click(); shipSelected = k; page = 0; Refresh(); }, Color.white, out _);
                shipNames[i] = T(p, "", -.158f, y - .006f, .0135f, .115f); shipHulls[i] = T(p, "", -.035f, y - .006f, .0125f, .075f); shipKinds[i] = T(p, "", .045f, y - .006f, .0125f, .095f);
            }
            fleetEmpty = T(p, "", -.014f, .2f, .0135f, .30f, TextAnchor.MiddleCenter);
            // Right page (the user, 2026-11-08: "make the lifetime profit, Days in fleet, current location on top, and the bottom show the trade history
            // (where he sold, how much profit earned that trip(the share that gave to you). Don't need to show other info since it's in trade fleet UI
            // of trade office ui already"): those three as a form, then the ship's last trips in the records page's columns.
            // Then (the user, 2026-11-08: "you should have page to see all the trade history / Move the lifetime profit and days in fleet above the
            // separation line, remove the summary text. So history occupied more space on page"): its name, lifetime profit and days in fleet above the
            // line, which lay .025 lower than the left page's to hold the two rows (Claude's choice, OK'd by the user); then its whole trade history,
            // HistoryRows a page, with the records page's page buttons (the location under the line, and the line itself, until the user's changes below).
            shipTitle = T(p, "", .218f, .345f, .026f, .28f);
            // The ship's five rows, then the line (the user, 2026-11-08 evening: "move the separation line under biggest profit trip, separate it from
            // trade history"): Lifetime profit and Days in fleet, then from its whole life (the user, the same evening: "remove the location info, instead
            // replace it with most visited port, most sold goods and biggest profit trip(show destination and trip profit)"), .0235 apart.
            for (int i = 0; i < FormRows; i++)
            {
                float y = .318f - i * .0235f;
                formLabels[i] = T(p, "", .218f, y, .0125f, .11f); formValues[i] = T(p, "", .335f, y, .0145f, .165f, TextAnchor.LowerLeft, valueFont);
            }
            for (int i = 0; i < LifeRows; i++)
            {
                float y = .318f - (FormRows + i) * .0235f;
                lifeLabels[i] = T(p, LifeLabelTexts[i], .218f, y, .0125f, .11f); lifeValues[i] = T(p, "", .335f, y, .0135f, .165f, TextAnchor.LowerLeft, valueFont);
            }
            // the line .015 under the last row's baseline (as it lay under Days in fleet before), the history's title .028 under it (at .007 less a label
            // touched the line's ornament, obk-12)
            LedgerOrnament.Create(p, "trade fleet summary ornament", .212f, .504f, .209f);
            // the ship's trade history under the line: newest first, HistoryRows a page, in the records page's four columns (the user, the same evening: "At the
            // history between port and profit, add traded cargo also, show cargo with most value and it's quantity"); the share in the coin's short name.
            T(p, "Trade history", .218f, .181f, .0145f, .28f);
            T(p, "Day", .216f, .161f, .0115f, .028f); T(p, "Sold at", .246f, .161f, .0115f, .092f); T(p, "Cargo", .346f, .161f, .0115f, .08f); T(p, "Your share", .502f, .161f, .0115f, .07f, TextAnchor.LowerRight);
            Divider(p, .359f, .155f, .286f);
            for (int i = 0; i < HistoryRows; i++)
            {
                float y = .133f - i * .022f;
                histDay[i] = T(p, "", .216f, y, .012f, .028f); histPort[i] = T(p, "", .246f, y, .012f, .092f); histCargo[i] = T(p, "", .346f, y, .012f, .08f);
                histShare[i] = T(p, "", .502f, y, .013f, .07f, TextAnchor.LowerRight, valueFont);
            }
            historyEmpty = T(p, "", .359f, .11f, .0125f, .28f, TextAnchor.MiddleCenter);
            historyPageLabel = T(p, "", .36f, -.02f, .015f, .1f, TextAnchor.MiddleCenter);
            BookUI.Button(p, native, font, "<", .26f, -.02f, .03f, .024f, () => { page = Math.Max(0, page - 1); Refresh(); }, Color.white, out _);
            BookUI.Button(p, native, font, ">", .46f, -.02f, .03f, .024f, () => { page++; Refresh(); }, Color.white, out _);
            Finish(fleetPage);
        }

        private void Show(int h)
        {
            house = h; region = Office.Region(h); coin = Office.Coin(h); tab = 0; page = 0; shipSelected = 0;
            OfficePanel.Close(); OfficeItemsPage.Close();
            if (MissionListUI.instance) MissionListUI.instance.SetMenuActive(false);
            if (!gameObject.activeSelf) { gameObject.SetActive(true); OfficeUiParts.Take(); }
            window.Cancel(); timer = 0; Refresh();
        }
        private void Hide()
        {
            if (!gameObject.activeSelf) return;
            if (window) window.Cancel();
            gameObject.SetActive(false); OfficeUiParts.Give(); house = 0;
        }
        private void Update()
        {
            if (!Office.Live(house)) { Hide(); return; }
            // The log book opened over it (its own key): this book gives way.
            if (logBook && logBook.activeInHierarchy) { Hide(); return; }
            if ((timer -= Time.unscaledDeltaTime) > 0) return; timer = .5f; Refresh();
        }
        private bool FleetShown() => Office.Houses.Any(x => Office.Region(x) == region && Office.Live(x) && Office.Has(x, OfficeUpgrade.Fleet));
        internal void Refresh()
        {
            if (!Office.Live(house)) return;
            bool fleet = FleetShown(); if (!fleet && tab == 1) tab = 0;
            for (int i = 0; i < 2; i++)
            {
                bool on = tab == i; var r = marks[i].GetComponent<Renderer>();
                if (r) r.sharedMaterial = on ? markOn[i] ?? r.sharedMaterial : markOff[i] ?? r.sharedMaterial;
                marks[i].transform.localPosition = on ? markOnAt[i] : markOffAt[i];
            }
            marks[1].SetActive(fleet);
            treasuryPage.SetActive(tab == 0 && !window.Open); fleetPage.SetActive(tab == 1);
            if (tab == 0) RefreshTreasury(); else RefreshFleet();
        }
        private void RefreshTreasury()
        {
            int offices = Office.Houses.Count(x => Office.Region(x) == region && Office.Live(x));
            subtitle.text = BookUI.RegionName(region) + ", shared by its " + (offices == 1 ? "office" : offices + " offices");
            coin = Mathf.Clamp(coin, 0, 3);
            if (outline && coins[coin]) { outline.SetParent(coins[coin], false); }
            for (int i = 0; i < 4; i++) coinNames[i].gameObject.SetActive(i == coin);
            for (int i = 0; i < 4; i++)
            {
                coinRows[i].text = (i == coin ? "> " : "") + PlayerGold.GetCurrencyName(i);
                treasuryValues[i].text = Office.Treasury(region, i).ToString("N0"); walletValues[i].text = PlayerGold.currency[i].ToString("N0");
            }
            bool canTake = Office.Treasury(region, coin) > 0, canPut = PlayerGold.currency[coin] > 0;
            SetEnabled(take, canTake); SetEnabled(put, canPut);
            take.description = canTake ? "Withdraw " + PlayerGold.GetCurrencyName(coin) + " from the treasury" : "Nothing in the treasury";
            put.description = canPut ? "Deposit " + PlayerGold.GetCurrencyName(coin) + " into the treasury" : "None in your wallet";
            status.text = !canTake && !canPut ? "No " + PlayerGold.GetCurrencyName(coin) + " in the treasury or your wallet" : "";
            var records = OfficeRecords.Of(region);
            recordsSubtitle.text = "The offices of " + BookUI.RegionName(region);
            int pages = Math.Max(1, (records.Count + RecordRows - 1) / RecordRows); page = Mathf.Clamp(page, 0, pages - 1);
            for (int i = 0; i < RecordRows; i++)
            {
                int n = page * RecordRows + i; var r = n < records.Count ? records[n] : null;
                recDay[i].text = r != null ? r.Day.ToString() : ""; recOffice[i].text = r != null ? OfficeUiParts.OfficeName(r.House) : i == 0 && records.Count == 0 ? "No records yet" : "";
                recWhat[i].text = r != null && r.Kind >= 0 && r.Kind < OfficeRecords.Names.Length ? OfficeRecords.Names[r.Kind] : "";
                recAmount[i].text = r != null ? (r.Amount > 0 ? "+" : "") + r.Amount.ToString("N0") + " " + OfficeUiParts.ShortCoin(r.Coin) : "";
            }
            pageLabel.text = "Page " + (page + 1) + " / " + pages;
        }
        private void RefreshFleet()
        {
            var s = Office.State; var ships = Enumerable.Range(0, OfficeFleet.Ships.Count).Where(i => s.fleetHouse != null && i < s.fleetHouse.Length && Office.Region(s.fleetHouse[i]) == region).ToList();
            fleetSubtitle.text = BookUI.RegionName(region) + ": " + ships.Count + (ships.Count == 1 ? " ship" : " ships");
            if (shipSelected >= ships.Count) shipSelected = 0;
            for (int r = 0; r < ShipRows; r++)
            {
                bool ship = r < ships.Count; int k = ship ? ships[r] : -1;
                shipRows[r].transform.parent.gameObject.SetActive(ship);
                var plate = shipRows[r].GetComponent<MeshRenderer>(); if (plate) plate.sharedMaterial = r == shipSelected ? rowSelected : rowNormal;
                shipNames[r].text = ship ? s.fleetName[k] : ""; shipHulls[r].text = ship ? Pledges.Name(s.fleetHull[k]) : ""; shipKinds[r].text = ship ? OfficeFleet.KindShort(k) : "";
            }
            fleetEmpty.text = ships.Count == 0 ? "No ships in the trade fleet yet.\nAdd one at Shipwright's storage\nin a yard of this region." : "";
            if (ships.Count == 0)
            {
                shipTitle.text = ""; foreach (var t in formLabels.Concat(formValues).Concat(lifeLabels).Concat(lifeValues).Concat(histDay).Concat(histPort).Concat(histCargo).Concat(histShare)) t.text = "";
                historyEmpty.text = historyPageLabel.text = ""; return;
            }
            int i0 = ships[shipSelected];
            shipTitle.text = s.fleetName[i0];
            // The user (2026-11-08): "Life time profit stay gold"; then the page's own rows (BuildFleet): lifetime profit, days in fleet, the ship's
            // whole life's three rows and its trade history; the rest of its summary only on the office scroll's Fleet tab (OfficeFleet.SummaryLabels).
            string[] labels = { "Lifetime profit", "Days in fleet" };
            string[] values = { OfficeFleet.Gold(s.fleetLife[i0]).ToString("0.0") + " gold", OfficeFleet.DaysIn(i0).ToString() };
            for (int i = 0; i < FormRows; i++) { formLabels[i].text = labels[i]; formValues[i].text = values[i]; }
            // the ship's whole life's three rows (the location is on the office scroll's Fleet tab)
            var life = OfficeFleet.LifeRows(i0);
            for (int i = 0; i < LifeRows; i++) { lifeLabels[i].text = LifeLabelTexts[i]; lifeValues[i].text = life[i]; }
            // the ship's whole trade history (OfficeFleet.TripLog trips at most), newest first, HistoryRows a page
            var trips = OfficeFleet.TripRows(i0);
            int pages = Math.Max(1, (trips.Count + HistoryRows - 1) / HistoryRows); page = Mathf.Clamp(page, 0, pages - 1);
            for (int r = 0; r < HistoryRows; r++)
            {
                int n = page * HistoryRows + r; bool has = n < trips.Count;
                histDay[r].text = has ? trips[n][0] : ""; histPort[r].text = has ? trips[n][1] : ""; histCargo[r].text = has ? trips[n][2] : ""; histShare[r].text = has ? trips[n][3] : "";
            }
            historyEmpty.text = trips.Count > 0 ? "" : OfficeFleet.Assigned(i0) ? "No trips yet" : "No trips yet: its route is not set";
            historyPageLabel.text = "Page " + (page + 1) + " / " + pages;
        }
        // A plate that can't be used shows grey (its texture alone keeps it coloured under any tint), as the ledger's Repay.
        private void SetEnabled(LoanAction button, bool on)
        {
            button.unclickable = !on; var material = button.ownedMaterial; if (!material) return;
            if (!plateTextures.TryGetValue(material, out var texture)) plateTextures[material] = texture = material.mainTexture;
            material.mainTexture = on ? texture : null; button.SetTint(on ? Plate : Disabled);
        }
        // The window over the book: the page under it hidden meanwhile (the game's text draws over every mesh, so the page's lines showed through
        // the parchment, obk-1), shown again by Refresh once the window closes.
        private void Ask(bool deposit)
        {
            OfficeUiParts.Click();
            window.Show(deposit, house, coin); treasuryPage.SetActive(false);
        }
        private void OnDestroy() { if (dividerMaterial) Destroy(dividerMaterial); }

        // For the tests (QA officetreasury).
        internal int House => house;
        internal int Coin { get => coin; set { coin = value; Refresh(); } }
        internal int Tab { get => tab; set { tab = value; page = 0; Refresh(); } }
        internal int ShipSelected { get => shipSelected; set { shipSelected = value; Refresh(); } }
        internal int Page { get => page; set { page = value; Refresh(); } }
        internal AmountWindow Window => window;
        internal bool FleetBookmark => marks[1] && marks[1].activeSelf;
        internal string FormText => string.Join("\n", Enumerable.Range(0, 4).Select(i => coinRows[i].text + " | " + treasuryValues[i].text + " | " + walletValues[i].text).ToArray());
        internal string RecordsText => string.Join("\n", Enumerable.Range(0, RecordRows).Where(i => recDay[i].text.Length > 0 || recOffice[i].text.Length > 0).Select(i => recDay[i].text + " | " + recOffice[i].text + " | " + recWhat[i].text + " | " + recAmount[i].text).ToArray());
        internal string ShipsText => string.Join("\n", Enumerable.Range(0, ShipRows).Where(i => shipNames[i].text.Length > 0).Select(i => shipNames[i].text + " | " + shipHulls[i].text + " | " + shipKinds[i].text).ToArray());
        internal string SummaryText => shipTitle.text + "\n" + string.Join("\n", Enumerable.Range(0, FormRows).Where(i => formLabels[i].text.Length > 0).Select(i => formLabels[i].text + ": " + formValues[i].text).ToArray()) + "\n" +
            string.Join("\n", Enumerable.Range(0, LifeRows).Select(i => lifeLabels[i].text + ": " + lifeValues[i].text).ToArray()) +
            "\nTrade history: " + (historyEmpty.text.Length > 0 ? historyEmpty.text : string.Join("; ", Enumerable.Range(0, HistoryRows).Where(r => histDay[r].text.Length > 0).Select(r => "day " + histDay[r].text + " " + histPort[r].text + " " + histCargo[r].text + " " + histShare[r].text).ToArray()));
        // The Trade fleet page's page line ("Page 1 / 3") and its rows shown (QA officebooks).
        internal string HistoryPage => historyPageLabel ? historyPageLabel.text : "";
        internal int HistoryShown => Enumerable.Range(0, HistoryRows).Count(r => histDay[r].text.Length > 0);
        internal static int HistoryPerPage => HistoryRows;
        internal Transform OutlineCoin => outline ? outline.parent : null;
        internal Transform CoinButton(int i) => coins[i];
    }

    // ---- The amount window (the user: "make put in/take out promt a new window and player key in the amount they want to took/put in") ----
    // Over the book: what it does and the coin, how much there is, the typed amount. The game has no typed input of its own (no inputString or
    // input field anywhere in its code): the digits typed (the top row and the keypad, Input.inputString) build the number, Backspace deletes,
    // Enter confirms, Escape cancels (OfficePanelEscapePatch); All, Confirm and Cancel do the same by click. While it is open the game's 1-5
    // inventory keys are held (InventoryHotkeysTypingPatch): its InventoryHotkeys reads them whatever the player's control.
    public sealed class AmountWindow : MonoBehaviour
    {
        private TreasuryBook book; private bool deposit; private int house, coin; private string typed = "";
        private TextMesh title, coinText, have, amount, note;
        internal bool Open => gameObject.activeSelf;
        internal bool Build(TreasuryBook owner, MissionListUI log, Font font, Font valueFont, MeshRenderer native, MeshRenderer strip)
        {
            book = owner; gameObject.layer = owner.gameObject.layer;
            transform.SetParent(owner.transform, false); transform.localPosition = new Vector3(0, .05f, 0);
            var details = Fields.Get<GameObject>(log, "details"); if (!details) return false;
            // The mission page's parchment, three fifths its size, over the middle of the book; a collider over it so a click meant for it
            // doesn't reach the book behind.
            var bg = OfficeUiParts.Clone(details, transform, false); bg.name = "amount window parchment";
            foreach (Transform child in bg.transform.Cast<Transform>().ToArray()) DestroyImmediate(child.gameObject);
            // Three fifths high and nine tenths wide (obk-1: at three fifths across its Cancel ran past the edge).
            bg.transform.localScale = Vector3.Scale(bg.transform.localScale, new Vector3(.9f, .6f, .6f));
            var r = bg.GetComponent<Renderer>();
            if (r) { var want = transform.TransformPoint(BookUI.Position(.165f, .175f, -.008f)); bg.transform.position += want - r.bounds.center; }
            // The collider a thin sheet at the parchment's back, away from the UI camera the game's cursor casts from (MouseButtonPointer's uiCam;
            // the window's +y if it isn't there): the mesh's whole box reached in front of All, Confirm and Cancel over about two thirds of each, so
            // the cursor met the parchment there and lit nothing (officeuihover ouh-3; the user: "sometimes when cursor already on top of text, it
            // don't highlight it").
            var mf = bg.GetComponent<MeshFilter>();
            if (mf && mf.sharedMesh)
            {
                var box = bg.AddComponent<BoxCollider>(); var mb = mf.sharedMesh.bounds; Vector3 c = mb.center, s = mb.size;
                var uiCam = MouseButtonPointer.instance ? Fields.Get<Camera>(MouseButtonPointer.instance, "uiCam") : null;
                var toViewer = bg.transform.InverseTransformDirection(uiCam ? uiCam.transform.position - bg.transform.position : transform.up);
                int k = Mathf.Abs(toViewer.x) >= Mathf.Abs(toViewer.y) && Mathf.Abs(toViewer.x) >= Mathf.Abs(toViewer.z) ? 0 : Mathf.Abs(toViewer.y) >= Mathf.Abs(toViewer.z) ? 1 : 2;
                c[k] -= Mathf.Sign(toViewer[k]) * mb.extents[k]; s[k] = Mathf.Max(s[k] * .01f, 1e-4f);
                box.center = c; box.size = s;
            }
            var p = transform;
            title = BookUI.Text(p, font, "", .165f, .300f, .02f, .25f, TextAnchor.MiddleCenter);
            coinText = BookUI.Text(p, font, "", .165f, .268f, .016f, .25f, TextAnchor.MiddleCenter);
            have = BookUI.Text(p, font, "", .165f, .240f, .013f, .25f, TextAnchor.MiddleCenter);
            var field = BookUI.Button(p, strip, font, "", .165f, .196f, .2f, .042f, () => { }, Color.white, out _); field.unclickable = true; field.lookText = "Type the amount";
            amount = BookUI.Text(p, valueFont, "", .165f, .196f, .026f, .19f, TextAnchor.MiddleCenter);
            BookUI.Text(p, font, "Type the amount. Enter confirms, Esc cancels.", .165f, .154f, .0105f, .25f, TextAnchor.MiddleCenter);
            BookUI.Button(p, native, font, "All", .055f, .108f, .08f, .03f, () => { OfficeUiParts.Click(); typed = Available().ToString(); Redraw(); }, Color.white, out _);
            BookUI.Button(p, native, font, "Confirm", .165f, .108f, .09f, .03f, Confirm, new Color(.38f, .8f, 1), out _);
            BookUI.Button(p, native, font, "Cancel", .275f, .108f, .08f, .03f, () => { OfficeUiParts.Click(); Cancel(); }, Color.white, out _);
            note = BookUI.Text(p, font, "", .165f, .068f, .0105f, .25f, TextAnchor.MiddleCenter);
            // Its text on its plates' plane, as the book's (TreasuryBook.Finish; obk-3: in front of them the labels sat to the side of All and Cancel).
            foreach (var text in GetComponentsInChildren<TextMesh>(true)) { var at = text.transform.localPosition; text.transform.localPosition = new Vector3(at.x, .014f, at.z); BlackBookText.Apply(text); }
            gameObject.SetActive(false);
            return true;
        }
        internal void Show(bool putIn, int h, int c)
        {
            deposit = putIn; house = h; coin = c; typed = "";
            title.text = deposit ? "Deposit into the treasury" : "Withdraw from the treasury";
            coinText.text = PlayerGold.GetCurrencyName(coin);
            gameObject.SetActive(true); Redraw();
        }
        internal void Cancel() { if (gameObject.activeSelf) gameObject.SetActive(false); typed = ""; }
        private int Available() => deposit ? Math.Max(0, PlayerGold.currency[coin]) : Math.Max(0, Office.Treasury(Office.Region(house), coin));
        private void Update()
        {
            foreach (char ch in Input.inputString)
            {
                if (ch >= '0' && ch <= '9') { if (typed.Length < 10) typed = (typed == "0" ? "" : typed) + ch; }
                else if (ch == '\b') { if (typed.Length > 0) typed = typed.Substring(0, typed.Length - 1); }
                else if (ch == '\n' || ch == '\r') { Confirm(); return; }
            }
            Redraw();
        }
        private long Typed => typed.Length == 0 ? 0 : long.TryParse(typed, out long v) ? v : 0;
        // A number over what there is becomes all of it as it is typed (the user, 2026-11-08: "Make when player type more number than the
        // treasury/wallet actually have, auto cap it at the treasury/wallet amount"); also when what there is drops while the window is open.
        private void Redraw()
        {
            int most = Available();
            if (Typed > most) typed = most > 0 ? most.ToString() : "";
            have.text = (deposit ? "In your wallet: " : "In the treasury: ") + most.ToString("N0");
            bool caret = (int)(Time.unscaledTime * 2) % 2 == 0;
            amount.text = (typed.Length == 0 ? "" : Typed.ToString("N0")) + (caret ? "|" : " ");
            note.text = "";
        }
        // At most what there is (Redraw caps the typed number).
        private void Confirm()
        {
            int most = Available(), value = (int)Math.Min(most, Typed);
            if (value <= 0) { Loans.Notify(most <= 0 ? "There is none to move" : "Type an amount"); return; }
            var why = Office.Move(house, coin, value, deposit);
            if (why != null) { Loans.Notify(why); return; }
            Cancel(); book.Refresh();
        }
        // Closed any way (Cancel, Escape, the book closing): the page under it back.
        private void OnDisable() { if (book && book.gameObject.activeInHierarchy) book.Refresh(); }
        // For the tests.
        internal string TypedText { get => typed; set { typed = value ?? ""; Redraw(); } }
        internal void Press() => Confirm();
        internal bool Deposit => deposit;
        internal string Text => title.text + " | " + coinText.text + " | " + have.text + " | " + amount.text.TrimEnd('|', ' ') + " | " + note.text;
    }
    [HarmonyPatch(typeof(InventoryHotkeys), "Update")] internal static class InventoryHotkeysTypingPatch { static bool Prefix() => !TreasuryBook.Typing; }

    // ---- The unique items page (the user, 2026-11-08: "Use mission UI page for unique item purchase. redesign it.") ----
    // From the office scroll's Items tab: copies of the port's mission list and mission page (the game's own untouched), in the same frame. The
    // list's seven rows (the game's five and two more) with its page buttons and "1 / N"; its Local and World buttons are Unique and Regional, and
    // Modded joins them when that list has items; back returns to the office scroll. On Regional and Modded the first row is a filter, a drop-down
    // list made of the list's own rows (the game has none): pressed, the rows below list "All" and each region or mod with its count; picked, the
    // list shows only those (the user, 2026-11-08: "add a drop down list for filter function on the Ui so player can pick the mod and the list only
    // show item from that mod", and the same for the regional variants). An item opens the mission page as a form: the item, where it is sold, the
    // price in the port's coin and your wallet; Accept reads Buy and makes it at the office's drop point (OfficeItems.Buy), or says why it can't.
    public sealed class OfficeItemsPage : MonoBehaviour
    {
        internal static OfficeItemsPage Instance;
        private int house, kind, page, selected = -1, filter = -1, optionPage; private bool picking; private float timer;
        private List<OfficeItems.Entry> items = new List<OfficeItems.Entry>(); private List<string> filters = new List<string>(); private List<int> counts = new List<int>();
        private const int Rows = 7;
        private readonly GameObject[] rowButtons = new GameObject[Rows]; private readonly TextMesh[] rowTexts = new TextMesh[Rows]; private Material rowLook, filterLook;
        private void OnDestroy() { if (filterLook) Destroy(filterLook); }
        private TextMesh pageCount;
        private readonly GameObject[] tabButtons = new GameObject[3]; private Material tabOn, tabOff; private Vector3 localAt, worldAt, tabScale;
        private GameObject details; private TextMesh itemName, soldLabel, sold, atLabel, at, priceLabel, price, have, buyText;
        private static readonly string[] TabNames = { "unique", "regional", "modded" };
        internal static bool IsOpen => Instance && Instance.gameObject.activeSelf;
        internal static void Open(int h, int k = 0)
        {
            if (!Office.Working(h, OfficeUpgrade.Items)) return;
            if (!Instance)
            {
                var log = MissionListUI.instance; if (!log) return;
                var go = new GameObject("trade office items page"); go.layer = log.gameObject.layer; go.SetActive(false); go.transform.SetParent(log.transform, false);
                Instance = go.AddComponent<OfficeItemsPage>();
                try { if (!Instance.Build(log)) throw new Exception("the mission list's parts not found"); }
                catch (Exception e) { Plugin.Log.LogWarning("Trade office: the items page not built: " + e.Message); Destroy(go); Instance = null; return; }
            }
            Instance.Show(h, k);
        }
        internal static void Close() { if (IsOpen) Instance.Hide(); }
        internal static bool Escape() { if (!IsOpen) return false; Instance.Hide(); return true; }
        private bool Build(MissionListUI log)
        {
            var listBg = Fields.Get<GameObject>(log, "list"); var buttons = Fields.Get<GameObject>(log, "currentMissionsUI");
            var detailsSrc = Fields.Get<GameObject>(log, "details");
            var local = Fields.Get<GPButtonMissionListWorld>(log, "localButton");
            if (!listBg || !buttons || !detailsSrc || !local) return false;
            tabOn = local.activeMat; tabOff = local.inactiveMat;
            var bg = OfficeUiParts.Clone(listBg, transform, false); bg.name = "items list parchment";
            var list = OfficeUiParts.Clone(buttons, transform, false).transform; list.name = "items list";
            // Seven rows where the game has five, the list and its parchment longer by two rows into the place of the port's welcome box under it
            // (the user, 2026-11-08: "remove the "Unique items of [Port name]" part, remove both the text and the background parch. Make the left side
            // list longer instead to show more item per page"): rows 5 and 6 copies of the fifth, a row's step below each other.
            var fourth = OfficeUiParts.Find(list, "mission button (3)"); var fifth = OfficeUiParts.Find(list, "mission button (4)"); if (!fourth || !fifth) return false;
            // The parchment a little longer still than the two rows (obk-7: by the rows alone its bottom roll touched the back button).
            float step = fourth.localPosition.y - fifth.localPosition.y, extra = step * (Rows - 5), roll = .012f;
            for (int i = 5; i < Rows; i++) { var c = Instantiate(fifth.gameObject, fifth.parent).transform; c.name = "mission button (" + i + ")"; c.localPosition = fifth.localPosition - new Vector3(0, step * (i - 4), 0); }
            for (int i = 0; i < Rows; i++)
            {
                var row = OfficeUiParts.Find(list, i == 0 ? "mission button" : "mission button (" + i + ")"); if (!row) return false;
                rowButtons[i] = row.Find("bg+trigger").gameObject; rowTexts[i] = row.Find("text").GetComponent<TextMesh>();
                int k = i; OfficeUiParts.Action(rowButtons[i], () => PressRow(k), "Look");
            }
            rowLook = rowButtons[0].GetComponent<Renderer>() ? rowButtons[0].GetComponent<Renderer>().sharedMaterial : null;
            // The filter's row tinted light blue, the treasury's buttons' colour lightened (obk-7: in the chosen tab's look it read as one more item).
            if (rowLook) { filterLook = new Material(rowLook) { name = "items filter row" }; if (filterLook.HasProperty("_Color")) filterLook.color = new Color(.7f, .88f, 1f); }
            // The parchment stretched down by those two rows, its top kept (its mesh's +y is up on the screen, as the rows' frame).
            var bgMesh = bg.GetComponent<MeshFilter>() ? bg.GetComponent<MeshFilter>().sharedMesh : null;
            if (bgMesh && bgMesh.bounds.size.y > 1e-4f)
            {
                var top = new Vector3(0, bgMesh.bounds.max.y, 0); var before = bg.transform.TransformPoint(top); var sc = bg.transform.localScale;
                bg.transform.localScale = new Vector3(sc.x, sc.y * (bgMesh.bounds.size.y + (extra + roll) / Mathf.Max(1e-4f, sc.y)) / bgMesh.bounds.size.y, sc.z);
                bg.transform.position += before - bg.transform.TransformPoint(top);
            }
            var pages = OfficeUiParts.Find(list, "page buttons"); if (!pages) return false; pages.gameObject.SetActive(true);
            foreach (var n in new[] { "mission button (next", "mission button (previous", "mission button (back)", "text (page count)" })
            { var t = OfficeUiParts.FindStart(pages, n); if (t) t.localPosition -= new Vector3(0, extra, 0); }
            pageCount = OfficeUiParts.Find(pages, "text (page count)").GetComponent<TextMesh>();
            OfficeUiParts.Action(OfficeUiParts.FindStart(pages, "mission button (next").Find("bg+trigger").gameObject, () => Turn(1), "Next page");
            OfficeUiParts.Action(OfficeUiParts.FindStart(pages, "mission button (previous").Find("bg+trigger").gameObject, () => Turn(-1), "Previous page");
            var back = OfficeUiParts.FindStart(pages, "mission button (back)");
            OfficeUiParts.Action(back.Find("bg+trigger").gameObject, () => { OfficeUiParts.Click(); int h = house; Hide(); OfficePanel.Open(h); }, "Back to the office");
            var localT = OfficeUiParts.FindStart(pages, "mission button (local)"); var worldT = OfficeUiParts.FindStart(pages, "mission button (world");
            if (!localT || !worldT) return false;
            tabButtons[0] = localT.gameObject; tabButtons[1] = worldT.gameObject; localAt = localT.localPosition; worldAt = worldT.localPosition; tabScale = localT.localScale;
            // Modded: a third tab; with it the three share the two tabs' span (Refresh).
            tabButtons[2] = Instantiate(worldT.gameObject, worldT.parent); tabButtons[2].name = "items tab (modded)";
            for (int i = 0; i < 3; i++)
            {
                int k = i; var trigger = tabButtons[i].transform.Find("bg+trigger");
                OfficeUiParts.Action(trigger.gameObject, () => { OfficeUiParts.Click(); SetKind(k); }, TabNames[i]);
                var t = tabButtons[i].transform.Find("text").GetComponent<TextMesh>(); t.text = TabNames[i];
            }
            // The mission page: its map and the lines no item has taken off; its own lines given the item's form.
            details = OfficeUiParts.Clone(detailsSrc, transform, false); details.name = "items details";
            var d = details.transform;
            foreach (var n in new[] { "map", "map zoomed pos", "bg", "text (coords)", "text (amount)", "text (cargo)", "text (weight)", "text (due)", "static text (1)", "text (distance)", "text (gold per mile)", "text (gold per unit)", "text (liability)", "text (insurance level)",
                                      "EconomyOverhaul category label", "EconomyOverhaul category", "EconomyOverhaul capacity", "EconomyOverhaul capacity origin", "EconomyOverhaul capacity destination", "EconomyOverhaul reputation", "EconomyOverhaul note" })
                foreach (var t in d.GetComponentsInChildren<Transform>(true).Where(x => x.name == n).ToArray()) t.gameObject.SetActive(false);
            var statics = d.GetComponentsInChildren<TextMesh>(true).Where(t => t.name == "static text").ToArray();
            TextMesh S(string start) => statics.FirstOrDefault(t => t.text.TrimStart().StartsWith(start, StringComparison.Ordinal));
            soldLabel = S("Origin"); atLabel = S("Destination"); priceLabel = S("Reward");
            foreach (var t in statics) if (t != soldLabel && t != atLabel && t != priceLabel) t.gameObject.SetActive(false);
            TextMesh V(string n) => OfficeUiParts.Find(d, n)?.GetComponent<TextMesh>();
            itemName = V("text name"); sold = V("text (origin)"); at = V("text (destination)"); price = V("text (total gold)");
            if (!itemName || !sold || !at || !price || !soldLabel || !atLabel || !priceLabel) return false;
            // Under the price, where the game's page has its reputation line: the wallet.
            TextMesh Under(string n, float down, float scale) { var t = Instantiate(price.gameObject, price.transform.parent).GetComponent<TextMesh>(); t.name = n; t.transform.localPosition = price.transform.localPosition + new Vector3(0, -down, 0); t.transform.localScale = price.transform.localScale * scale; return t; }
            have = Under("items wallet", .085f, .8f);
            // The sellers wrapped at full size from under their label (Refresh; obk-2: fitted to .62, only 164 px of the page's 385, they shrunk
            // unreadable). The page's lines are about 264 px a unit across at 1280x720. The game's Destination lines hidden: the user (2026-11-08):
            // "remove both bought at and made at" (the office the item is bought at and its drop point); why an item can't be bought shows when Buy
            // is pressed, as the game's notification (Buy), with no line left on the page for it.
            sold.anchor = TextAnchor.UpperLeft; Move(sold, .71f);
            atLabel.gameObject.SetActive(false); at.gameObject.SetActive(false);
            var fit = sold.gameObject.GetComponent<LoanTextFit>() ?? sold.gameObject.AddComponent<LoanTextFit>(); fit.originalScale = sold.transform.localScale; fit.maxWidth = SoldWidth;
            var accept = OfficeUiParts.Find(d, "accept/cancel button"); if (!accept) return false;
            foreach (var n in new[] { "bg ", "text (mission count)", "bg (mission count)" }) { var c = accept.Find(n); if (c) c.gameObject.SetActive(false); }
            OfficeUiParts.Action(accept.Find("bg+trigger").gameObject, Buy, "Buy");
            buyText = accept.Find("text").GetComponent<TextMesh>(); buyText.text = "Buy";
            return true;
        }
        // The page's lines run from x .676 (its left margin) to about -.8; 1.4 of it is about 370 px.
        private const float SoldWidth = 1.4f;
        private static void Move(TextMesh t, float y) { var p = t.transform.localPosition; t.transform.localPosition = new Vector3(p.x, y, p.z); }
        private void Show(int h, int k)
        {
            house = h; kind = k; page = 0; selected = -1; filter = -1; picking = false; optionPage = 0;
            OfficePanel.Close(); TreasuryBook.Close();
            if (MissionListUI.instance) MissionListUI.instance.SetMenuActive(false);
            if (!gameObject.activeSelf) { gameObject.SetActive(true); OfficeUiParts.Take(); }
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayParchmentSound();
            timer = 0; Refresh();
        }
        private void Hide() { if (!gameObject.activeSelf) return; gameObject.SetActive(false); OfficeUiParts.Give(); house = 0; }
        private void Update()
        {
            if (!Office.Working(house, OfficeUpgrade.Items)) { Hide(); return; }
            if ((timer -= Time.unscaledDeltaTime) > 0) return; timer = .5f; Refresh();
        }
        // The filter's rows: the first row on Regional and Modded; the items (or, while picking, the filter's choices) in the rows under it.
        private bool Filtered => kind != 0;
        private int First => Filtered ? 1 : 0;
        private int PerPage => Rows - First;
        private string Key(OfficeItems.Entry e) => kind == 2 ? e.Mod ?? "" : BookUI.RegionName(e.Region);
        private string AllLabel => kind == 2 ? "All mods" : "All regions";
        private int Pages => Math.Max(1, ((picking ? filters.Count + 1 : items.Count) + PerPage - 1) / PerPage);
        private void SetKind(int k) { kind = k; page = 0; selected = -1; filter = -1; picking = false; optionPage = 0; Refresh(); }
        private void PressRow(int r)
        {
            OfficeUiParts.Click();
            if (Filtered && r == 0) { picking = !picking; optionPage = 0; Refresh(); return; }
            int n = r - First;
            if (picking)
            {
                int o = optionPage * PerPage + n; // 0 is All
                if (o <= filters.Count) { filter = o - 1; picking = false; page = 0; selected = -1; }
                Refresh(); return;
            }
            selected = page * PerPage + n; Refresh();
        }
        private void Turn(int by)
        {
            int at = picking ? optionPage : page, next = Mathf.Clamp(at + by, 0, Pages - 1);
            if (next == at) return;
            if (picking) optionPage = next; else page = next;
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayParchmentSound(); Refresh();
        }
        // modded: OfficeItems.ModdedList(), built once a refresh and used for the tabs and the Modded list (it was built 2-4 times a refresh,
        // twice a second; nothing it reads changes within one refresh).
        private bool TabShown(int k, List<OfficeItems.Entry> modded) => k < 2 || modded.Count > 0;
        internal void Refresh()
        {
            var modded = OfficeItems.ModdedList();
            if (!TabShown(kind, modded)) SetKindQuiet(0);
            var all = kind == 2 ? modded : OfficeItems.List(house, kind == 1);
            filters = Filtered ? all.Select(Key).Distinct().ToList() : new List<string>();
            counts = filters.Select(f => all.Count(x => Key(x) == f)).ToList();
            if (filter >= filters.Count) filter = -1;
            items = filter < 0 ? all : all.Where(x => Key(x) == filters[filter]).ToList();
            int pages = Pages; if (picking) optionPage = Mathf.Clamp(optionPage, 0, pages - 1); else page = Mathf.Clamp(page, 0, pages - 1);
            // Two tabs where the game has Local and World; three in the same span, two thirds as wide, when Modded shows.
            bool three = TabShown(2, modded); var step = worldAt - localAt;
            for (int i = 0; i < 3; i++)
            {
                tabButtons[i].transform.localPosition = three ? localAt - step / 6f + step * (2f * i / 3f) : i == 0 ? localAt : worldAt;
                tabButtons[i].transform.localScale = three ? new Vector3(tabScale.x * 2f / 3f, tabScale.y, tabScale.z) : tabScale;
                tabButtons[i].SetActive(TabShown(i, modded));
                var r = tabButtons[i].transform.Find("bg+trigger").GetComponent<Renderer>(); if (r) r.sharedMaterial = i == kind ? tabOn : tabOff;
            }
            for (int i = 0; i < Rows; i++)
            {
                bool exists; string text, look = "Look"; bool filterRow = Filtered && i == 0;
                if (filterRow) { exists = true; text = "Filter: " + (filter < 0 ? AllLabel : filters[filter]) + (picking ? "  (pick one)" : "  (change)"); look = "Filter"; }
                else if (picking)
                {
                    int o = optionPage * PerPage + i - First; exists = o <= filters.Count;
                    text = !exists ? "" : (o - 1 == filter ? "> " : "") + (o == 0 ? AllLabel + " (" + counts.Sum() + ")" : filters[o - 1] + " (" + counts[o - 1] + ")"); look = "Show";
                }
                else { int n = page * PerPage + i - First; exists = n < items.Count; text = exists ? items[n].Label : ""; }
                rowButtons[i].SetActive(exists); rowTexts[i].text = text;
                var a = rowButtons[i].GetComponent<LoanAction>(); if (a) a.lookText = look;
                // The filter's row tinted, so it reads as a control rather than an item.
                var r = rowButtons[i].GetComponent<Renderer>(); if (r) r.sharedMaterial = filterRow && filterLook ? filterLook : rowLook ?? r.sharedMaterial;
            }
            pageCount.text = ((picking ? optionPage : page) + 1) + " / " + pages;
            int coin = Office.Coin(house);
            var e = !picking && selected >= 0 && selected < items.Count ? items[selected] : null;
            details.SetActive(e != null);
            if (e == null) return;
            itemName.text = e.Label;
            soldLabel.text = "Sold at:"; sold.text = OfficeUiParts.Wrap(sold, e.Sellers, SoldWidth);
            priceLabel.text = "Price:";
            int cost = OfficeItems.Price(house, e);
            price.text = cost > 0 ? cost.ToString("N0") + " " + PlayerGold.GetCurrencyName(coin) : "-";
            have.text = "You have " + PlayerGold.currency[coin].ToString("N0");
        }
        private void Buy()
        {
            var e = selected >= 0 && selected < items.Count ? items[selected] : null; if (e == null) return;
            OfficeUiParts.Click();
            var why2 = OfficeItems.Refusal(house, e); if (why2 != null) { Loans.Notify(why2); return; }
            OfficeItems.Buy(house, e); Refresh();
        }
        private void SetKindQuiet(int k) { kind = k; page = 0; selected = -1; filter = -1; picking = false; optionPage = 0; }
        // For the tests (QA officebooks).
        internal int Kind { get => kind; set => SetKind(value); }
        internal int Filter => filter;
        internal List<string> Filters => filters;
        internal bool Picking => picking;
        internal void PressRowForTest(int r) => PressRow(r);
        internal int Page { get => page; set { page = value; Refresh(); } }
        internal int Selected { get => selected; set { selected = value; Refresh(); } }
        internal List<OfficeItems.Entry> Items => items;
        internal string RowsText => string.Join(" | ", rowTexts.Where(t => t.text.Length > 0).Select(t => t.text).ToArray()) + " (" + pageCount.text + ")";
        internal string DetailsText => details.activeSelf ? itemName.text + " | " + soldLabel.text + " " + sold.text.Replace("\n", " ") + " | " + priceLabel.text + " " + price.text + " | " + have.text : "(no item open)";
        internal bool BoughtAtShown => atLabel.gameObject.activeSelf || at.gameObject.activeSelf;
        internal bool TabVisible(int k) => tabButtons[k] && tabButtons[k].activeSelf;
        internal void PressBuy() => Buy();
    }
}

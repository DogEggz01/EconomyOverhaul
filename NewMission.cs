using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EconomyOverhaul
{
    // A good's mission category (MISSIONS_DRAFT.md stage 2). None: mail, the unused slot 45, or a mission accepted before
    // the rework (its record has no category).
    public enum MissionCategory { None, Staple, ConsumerGoods, Industrial, Luxury }
    // A mission's modifiers (saved as MissionState.modifiers).
    // Fragile (stage 3): the cargo breaks in hull impacts; shown as "(FRAGILE)" after the category, not in the name.
    [Flags] public enum MissionModifier { None = 0, Bulk = 1, Pristine = 2, Rush = 4, Fragile = 8 }

    public static partial class Rules
    {
        // An old warehouse's goods the migration sells back: those the market has a stock slot for (a good index past its stock,
        // from a mod's goods or a damaged save, would throw in the game's SellGood and stop the load).
        public static int[] MigrationSales(int[] warehouse, int stockLength) =>
            (warehouse ?? new int[0]).Where(g => g > 0 && g < stockLength).ToArray();
        // From the good's price curve; the Precious curve (gems, gold, silver) and goods on no named curve count as Luxury.
        public static MissionCategory Category(int good)
        {
            if (!PriceCurves.TryProfile(good, out var profile)) return MissionCategory.None;
            switch (profile)
            {
                case PriceProfile.Staple: return MissionCategory.Staple;
                case PriceProfile.ConsumerGoods: return MissionCategory.ConsumerGoods;
                case PriceProfile.Industrial: return MissionCategory.Industrial;
                default: return MissionCategory.Luxury;
            }
        }
        public static string CategoryName(MissionCategory c) =>
            c == MissionCategory.Staple ? "Staple" : c == MissionCategory.ConsumerGoods ? "Consumer goods" : c == MissionCategory.Industrial ? "Industrial" : c == MissionCategory.Luxury ? "Luxury" : "";
        // The reputation level a category needs (also the game's due-day tier, Port.GetDueDay), and its factors (gold and
        // reputation revised by the user, 2026-10-15). An old record (None) gets the game's reputation.
        public static int CategoryRep(MissionCategory c) => c == MissionCategory.Industrial ? 1 : c == MissionCategory.Luxury ? 2 : 0;
        // A good's own gate, over its category's: the dearest Luxury missions later (the user, 2026-10-30, MISSION_GATES_DRAFT.md:
        // "make silver, marble, blue tobacco, sculptures unlock at rep 3. Gem at rep 4, Gold at rep 5"). Every gate of 2 or more
        // gets the same due days in the game (Port.GetDueDay), so theirs stay Luxury's ("keep their mission due day same").
        public static int GoodRep(int good)
        {
            switch (good)
            {
                case 34: case 41: case 55: case 56: return 3;   // sculptures, blue tobacco, marble, silver
                case 20: return 4;                               // gems
                case 22: return 5;                               // gold
                default: return CategoryRep(Category(good));
            }
        }
        public static float CategoryGold(MissionCategory c) =>
            c == MissionCategory.Staple ? 1.1f : c == MissionCategory.ConsumerGoods ? 1.25f : c == MissionCategory.Industrial ? 1.5f : c == MissionCategory.Luxury ? 2 : 1;
        public static float CategoryReputation(MissionCategory c) =>
            c == MissionCategory.Staple ? 1.3f : c == MissionCategory.ConsumerGoods ? 1.1f : c == MissionCategory.Industrial ? .9f : c == MissionCategory.Luxury ? .7f : 1;
        // The distance part of the gold: x1.5 for Staple, x1.1 for Consumer goods (the user, 2026-10-15).
        public static float CategoryDistance(MissionCategory c) => c == MissionCategory.Staple ? 1.5f : c == MissionCategory.ConsumerGoods ? 1.1f : 1;
        // Cargo: the table x the factor, then the offset (the user, 2026-10-15: Staple x2 - 2, Consumer goods x2 - 3, Luxury - 1).
        public static float CategoryCargo(MissionCategory c) => c == MissionCategory.Staple || c == MissionCategory.ConsumerGoods ? 2 : 1;
        public static int CategoryCargoOffset(MissionCategory c) => c == MissionCategory.Staple ? -2 : c == MissionCategory.ConsumerGoods ? -3 : c == MissionCategory.Luxury ? -1 : 0;
        // Port capacity on completion, local (origin / destination): Staple 3/3, Consumer goods 2/2, Industrial 1/3, Luxury 3/1;
        // world twice that. A mission accepted before the rework keeps the old awards: 2/1 local, 6/3 world.
        public static int CapacityAward(MissionCategory c, bool world, bool origin)
        {
            int local;
            switch (c)
            {
                case MissionCategory.Staple: local = 3; break;
                case MissionCategory.ConsumerGoods: local = 2; break;
                case MissionCategory.Industrial: local = origin ? 1 : 3; break;
                case MissionCategory.Luxury: local = origin ? 3 : 1; break;
                default: return origin ? (world ? 6 : 2) : (world ? 3 : 1);
            }
            return world ? 2 * local : local;
        }
        // Cargo per mission by reputation level: 3-8 at levels 0-5 (the game's), 9 at 6, 10 at 7, 11 at 8, no limit from 9 (the
        // user, 2026-10-16; was no limit from 6).
        public const int NoCargoLimit = 999999;
        public static int CargoTable(int level) => level < 6 ? Math.Max(0, level) + 3 : level == 6 ? 9 : level == 7 ? 10 : level == 8 ? 11 : NoCargoLimit;
        // The table x the category's factor (2 or 1, so a whole number), plus its offset, at least 1 (the warehouse's stock caps
        // it where it is used).
        public static int MissionCargo(int level, MissionCategory c)
        {
            int table = CargoTable(level);
            return table == NoCargoLimit ? NoCargoLimit : Math.Max(1, (int)Math.Floor(table * (double)CategoryCargo(c) + .5) + CategoryCargoOffset(c));
        }
        // Gold before whole-crate rounding: the game's formula with its level24 tuning (profit share 0.02 local, 0.05 on the ocean
        // map: across regions, or to or from a port always shown on it, Mission.UseOceanMapFor; distance fee 0.5, x1.5 for Staple,
        // x1.1 for Consumer goods; x 2.5) x the category's factor. The game skips a mission
        // whose distance fee (the game's 0.5) exceeds its profit (MissionWorthwhile).
        public const float LocalProfitShare = .02f, WorldProfitShare = .05f, DistanceFee = .5f, RewardMult = 2.5f;
        public static float MissionGold(float profit, float distance, bool ocean, MissionCategory c) =>
            (profit * (ocean ? WorldProfitShare : LocalProfitShare) + distance * DistanceFee * CategoryDistance(c)) * RewardMult * CategoryGold(c);
        public static bool MissionWorthwhile(float profit, float distance) => distance * DistanceFee <= profit;
        // An offer's gold for its crates (unitProfit: one crate's profit), rounded down to whole crates. The game's
        // PruneNoSupplyMissions cuts an offer's count to the warehouse but keeps its gold (2 crates were paid as 12): a cut offer
        // is priced again by this for its new count (the user, 2026-10-18, D-B1 (b)).
        public static int MissionTotal(float unitProfit, int count, float distance, bool ocean, MissionCategory c) =>
            count <= 0 ? 0 : Mathf.RoundToInt(MissionGold(unitProfit * count, distance, ocean, c)) / count * count;
        // Missions open at once: the game's level + 2, at most 5 (the game's mission slots; 6 at rep 6 dropped by the user,
        // 2026-10-14).
        public static int MaxMissions(int level) => Math.Min(5, Math.Max(0, level) + 2);
        public const int MissionListCap = 30;
        // Which offers of a tab (in its sorted order) stay: the cap counts cargo offers only, the best paid first; every mail
        // offer stays and takes no room (the user, 2026-10-15: cargo and mail never limit each other). A cut of the mixed list
        // by pay dropped all mail at the big ports (Fort Aestrin, Dragon Cliffs), as mail pays the least per mile.
        public static bool[] KeepOffers(bool[] mail, int cap)
        {
            int cargo = 0;
            return mail.Select(m => m || cargo++ < cap).ToArray();
        }
        // The same cap by each offer's pay as it is now (pay: its pay a mile), the list left in its order: an offer cut to the
        // warehouse is paid less once priced again for its new count (D-B1), so its place in the game's order, made from its uncut
        // pay, is no longer its rank; the best paid stay (ties in list order). The deep review of 2026-10-19, Claude's fix.
        public static bool[] KeepOffersByPay(float[] pay, bool[] mail, int cap)
        {
            var byPay = Enumerable.Range(0, pay.Length).OrderByDescending(i => pay[i]).ToArray();   // a stable sort
            var kept = KeepOffers(byPay.Select(i => mail[i]).ToArray(), cap);
            var keep = new bool[pay.Length];
            for (int k = 0; k < byPay.Length; k++) keep[byPay[k]] = kept[k];
            return keep;
        }
        // After an accept, which of the origin's other offers of that good stay (counts: their crates; stock: what the warehouse
        // holds now): each one the warehouse still holds whole, on its own, as when the offers were made (each capped by the
        // warehouse alone, the other offers not counted: the user, 2026-10-17); each later accept checks again. An offer stays as
        // offered or goes (a cut Bulk would no longer double), so an offer that doesn't fit goes (the review of 2026-10-18,
        // Claude's fix; the game builds its lists again after every accept, from the warehouse the crates just left).
        public static bool[] KeepAfterAccept(int[] counts, int stock) => counts.Select(c => c > 0 && c <= stock).ToArray();
        public const int NoMissionsFrom = 33;   // Saffron Island

        // ---- The mission board's sort, region filter and quantity (BOARD_PLANK_DRAFT.md; the user's request of 2026-11-08) ----
        // The sort (the user: "Per mile, Gold, Rep"): every one high to low, by real value across the coins ("Make value sorting by actual
        // value, currently vanilla game sort with currency number, so mission paid in Emerald dragon always come up on top").
        public enum BoardSort { PerMile, Gold, Reputation }
        public static readonly string[] BoardSortNames = { "Gold per mile", "Gold reward", "Reputation reward" };
        // The region filter ("another one filter region, so it only show the quest from that region"): the destination's reputation
        // account (every offer starts at this port; Chronos' port 21 is "Chronos", its own account 3); index 0 all, 1-4 the accounts 0-3.
        public static readonly string[] BoardRegionNames = { "All regions", "Al'Ankh", "Emerald", "Aestrin", "Chronos" };
        // A coin amount's real value: the game turns a base price into a coin by multiplying by its rate (CurrencyMarket.currentPrices,
        // GetSellPriceInCurrency), so the amount over the rate.
        public static double RealValue(int amount, float rate) => rate > 0 ? amount / (double)rate : amount;
        public static double BoardKey(BoardSort sort, double realTotal, float distance, int reputation) =>
            sort == BoardSort.Gold ? realTotal : sort == BoardSort.Reputation ? reputation : distance > 0 ? realTotal / distance : realTotal;
        // The board's order: the offers whose region passes (region -1: all), high to low by key, ties in list order (a stable sort).
        public static int[] BoardOrder(double[] keys, int[] regions, int region) =>
            Enumerable.Range(0, keys.Length).Where(i => region < 0 || regions[i] == region).OrderByDescending(i => keys[i]).ToArray();
        // The quantity ("player can click the arrow to decide how many cargo he want to take from that mission. Reduce the reward
        // accordingly. mission with modifier cannot change the quantity"; "Quantity minimal 1, maximum as current's quantity"; the user's
        // answers: "Gold, rep and capacity", Fragile "No, only Bulk/Pristine/Rush"): 1 to the offered count; the gold, the reputation and the
        // port capacity award each x chosen / offered (gold and capacity rounded to the nearest).
        public static bool QuantityLocked(MissionModifier m) => (m & (MissionModifier.Bulk | MissionModifier.Pristine | MissionModifier.Rush)) != 0;
        public static int ChosenCount(int chosen, int offered) => Math.Max(1, Math.Min(Math.Max(1, offered), chosen));
        public static int ChosenShare(int whole, int chosen, int offered) =>
            offered <= 0 || chosen >= offered ? whole : (int)Math.Round(whole * (double)Math.Max(0, chosen) / offered, MidpointRounding.AwayFromZero);
        // The game's reputation per crate is the mission's distance / its crates x 3 (Mission.GetDeliveryRep): the same total whatever the
        // count, so a smaller mission's figure is brought back to the offered crate's: x chosen / offered.
        public static float RepScale(int chosen, int offered) => offered <= 0 || chosen >= offered ? 1 : Math.Max(0, chosen) / (float)offered;

        // ---- Mission modifiers (origin rep 3+; never on mail) ----
        public static string ModifierPrefix(MissionModifier m) =>
            ((m & MissionModifier.Bulk) != 0 ? "Bulk " : "") + ((m & MissionModifier.Pristine) != 0 ? "Pristine " : "") + ((m & MissionModifier.Rush) != 0 ? "Rush " : "");
        // Gold at generation: each modifier x2 (Bulk on its doubled cargo); a Pristine mission pays its x2 only when delivered dry.
        // Bulk's gold before its x2 (the user, 2026-10-15: option 1): the normal mission's pay per crate x Bulk's crates, so the
        // game's distance pay, made once per mission, is not spread thin over the bigger cargo.
        public static int BulkGold(int normalTotal, int normalCount, int bulkCount) => normalCount > 0 ? normalTotal / normalCount * bulkCount : normalTotal;
        public static int ModifierGold(MissionModifier m) =>
            ((m & MissionModifier.Bulk) != 0 ? 2 : 1) * ((m & MissionModifier.Pristine) != 0 ? 2 : 1) * ((m & MissionModifier.Rush) != 0 ? 2 : 1) * ((m & MissionModifier.Fragile) != 0 ? 2 : 1);
        // Extra port capacity on completion: Bulk +3 at each end; Rush +4 at the destination (Industrial) or the origin (Luxury).
        public static int ModifierCapacity(MissionCategory c, MissionModifier m, bool origin) =>
            ((m & MissionModifier.Bulk) != 0 ? 3 : 0) +
            ((m & MissionModifier.Rush) != 0 && (c == MissionCategory.Industrial && !origin || c == MissionCategory.Luxury && origin) ? 4 : 0);
        // Rush due days, Postal Expansion's way (MailDueDateCalculator): the mission distance / 10 in km over the speed in km/h
        // times the real day length in hours; 7.5 knots for Industrial, 8 for Luxury; rounded, no extra day (it may fall due today).
        public static float RushKnots(MissionCategory c) => c == MissionCategory.Luxury ? 8 : 7.5f;
        public static int RushDays(float missionDistance, float knots, float realDaySeconds) =>
            Mathf.RoundToInt(missionDistance / 10f / (knots * 1.852f * realDaySeconds / 3600f));
        // The water hint's damage percent (CargoCondition.RefreshHint): Pristine pays its x2 at 0% (fully dry); at 100% (ruined)
        // it costs 50 reputation a unit with both regions, as a reward is given (the user, 2026-10-14).
        public static int WaterHintPercent(float health) => Mathf.RoundToInt(100 - Mathf.Clamp(health, 0, 100));
        public const int PristineRuinedRep = -50;
        // Which offers get a modifier, rolled once per tab list: per category one mission at most (Luxury two), at random among
        // that category's offers (Pristine only on goods water can damage). Rep 3: each 50% (Luxury's two independently); rep 4+:
        // always, Luxury's Pristine and Rush on one mission or on two, 50/50. roll: random numbers in [0, 1).
        // Bulk (the user, 2026-10-17): twice the normal cargo, only where the port's warehouse holds that many of the good (the tab's
        // other offers not counted); an offer that can't double never gets Bulk. Was: up to twice the limit as far as the
        // warehouse held beyond the other offers, which often left it at the normal cargo with x2 gold.
        public static bool BulkDoubles(int normalCount, int stock) => normalCount > 0 && stock >= 2 * normalCount;
        // doublable: which offers Bulk may go to (Rules.BulkDoubles); null for all.
        public static MissionModifier[] RollModifiers(MissionCategory[] categories, bool[] damageable, int level, Func<double> roll, bool[] doublable = null)
        {
            var result = new MissionModifier[categories.Length];
            if (level < 3) return result;
            bool always = level >= 4;
            int[] Of(MissionCategory c, bool pristine) => Enumerable.Range(0, categories.Length).Where(i => categories[i] == c && (!pristine || damageable[i])).ToArray();
            int Pick(int[] from) => from[Math.Min(from.Length - 1, (int)(roll() * from.Length))];
            bool Chance() => always || roll() < .5;
            void Give(int[] from, MissionModifier m) { if (from.Length > 0 && Chance()) result[Pick(from)] |= m; }
            Give(Of(MissionCategory.Staple, false).Where(i => doublable == null || doublable[i]).ToArray(), MissionModifier.Bulk);
            Give(Of(MissionCategory.ConsumerGoods, true), MissionModifier.Pristine);
            Give(Of(MissionCategory.Industrial, false), MissionModifier.Rush);
            int[] luxury = Of(MissionCategory.Luxury, false), luxuryPristine = Of(MissionCategory.Luxury, true);
            if (!always) { Give(luxuryPristine, MissionModifier.Pristine); Give(luxury, MissionModifier.Rush); return result; }
            if (luxury.Length == 0) return result;
            if (luxuryPristine.Length == 0) { result[Pick(luxury)] |= MissionModifier.Rush; return result; }
            int pristine = Pick(luxuryPristine); result[pristine] |= MissionModifier.Pristine;
            var others = luxury.Where(i => i != pristine).ToArray();
            result[roll() < .5 || others.Length == 0 ? pristine : Pick(others)] |= MissionModifier.Rush;
            return result;
        }

        // ---- Fragile cargo (stage 3) ----
        // Gems, sculptures and marble always; tools when their mission rolled fragile (50%, FragileChance).
        public static bool FragileGood(int good) => good == 20 || good == 34 || good == 55;
        public const int FragileTools = 33; public const float FragileChance = .5f;
        // A crate's impact damage 0-100 (0 intact): each counted hull hit adds the hull damage it added x 100 x 2.5.
        public static float FragileDamage(float impact, float hullAdded) => Math.Min(100, Math.Max(0, impact) + Math.Max(0, hullAdded) * 250);
        // The hint's "Cargo Damage" line counts up from 0% intact (the user, 2026-10-16; was 100% counting down), as the water
        // line does; a fragile mission pays its x2 at 0%, at 100% 100 reputation a crate lost in both regions (and its gold / 2 x its
        // condition, a few coins at most). The crack look follows this rounded value, so it matches the hint's percent.
        public static int DamagePercent(float impact) => Mathf.RoundToInt(Mathf.Clamp(impact, 0, 100));
        public static float IntactFactor(float impact) => (100 - Mathf.Clamp(impact, 0, 100)) / 100f;
        public const int FragileRuinedRep = -100;
        // ---- The warehouse ----
        // Its slots: the port's capacity (rounded) + 100 (the user, 2026-10-15). A new game, and an older save once, start it 80% full.
        public static int WarehouseCapacity(float portCapacity) => Math.Max(1, (int)Math.Round(portCapacity, MidpointRounding.AwayFromZero)) + 100;
        public static int PrefillSlots(int capacity) => (int)Math.Round(capacity * .8, MidpointRounding.AwayFromZero);
        public static bool WarehouseGood(int good, float[] production) => good > 0 && good < production.Length && production[good] > 0 && Category(good) != MissionCategory.None;
        // The good for the next slot. counts: each good's slots besides the one being filled; production: the market's;
        // stocked: goods it can sell now; roll: a random number in [0, 1]. Each produced good is kept within ±15% of its
        // production share of the slots (the user, 2026-10-15; was ±10%): one under 85% comes first (the furthest under), else a
        // random one under 115%, weighted by its share. 0: no produced good in stock (the warehouse waits for production); -1:
        // every one in stock is at 115% or more (the slot stays as it is).
        public const double WarehouseBand = .15;
        public static int WarehousePick(int[] counts, float[] production, bool[] stocked, int capacity, double roll)
        {
            int size = Math.Min(production.Length, Math.Min(counts.Length, stocked.Length)); double total = 0;
            for (int g = 1; g < size; g++) if (WarehouseGood(g, production)) total += production[g];
            if (total <= 0) return 0;
            int under = 0, last = 0; double lowest = double.MaxValue, weights = 0; bool any = false;
            for (int g = 1; g < size; g++)
            {
                if (!WarehouseGood(g, production) || !stocked[g]) continue;
                any = true;
                double ratio = counts[g] / (production[g] / total * capacity);
                if (ratio < 1 - WarehouseBand && ratio < lowest) { under = g; lowest = ratio; }
                if (ratio < 1 + WarehouseBand) { weights += production[g]; last = g; }
            }
            if (!any) return 0;
            if (under > 0) return under;
            if (weights <= 0) return -1;
            double left = roll * weights;
            for (int g = 1; g < size; g++)
            {
                if (!WarehouseGood(g, production) || !stocked[g] || counts[g] / (production[g] / total * capacity) >= 1 + WarehouseBand) continue;
                left -= production[g]; if (left < 0) return g;
            }
            return last;
        }
    }

    // ---- Port capacity ----
    public sealed class PortCapacityUI : MonoBehaviour
    {
        private TextMesh label;
        private int port = -1, displayed = -1;

        internal static void Show(MissionListUI ui, PortDude dude)
        {
            if (Startup.BlocksGameplay) return;
            var display = ui.GetComponent<PortCapacityUI>() ?? ui.gameObject.AddComponent<PortCapacityUI>();
            display.port = dude && dude.GetPort() ? dude.GetPort().portIndex : -1;
            var title = Fields.Get<TextMesh>(ui, "portNameText");
            if (!title) return;
            if (!display.label)
            {
                display.label = Instantiate(title, title.transform.parent);
                display.label.name = "EconomyOverhaul Port Capacity";
                display.label.characterSize = title.characterSize * .52f;
                display.displayed = -1;
            }
            // Match the approved welcome-sign layout in its own coordinates, independent
            // of camera angle or PC/VR anchoring. Parent visibility follows the native sign.
            display.label.transform.localPosition = title.transform.localPosition + title.transform.localRotation * (Vector3.down * .127f);
            display.Refresh();
        }

        internal static void RefreshVisible(int port)
        {
            if (Startup.BlocksGameplay) return;
            var ui = MissionListUI.instance;
            var display = ui ? ui.GetComponent<PortCapacityUI>() : null;
            if (display && display.port == port && display.label && display.label.gameObject.activeInHierarchy)
                display.Refresh();
        }

        private void Refresh()
        {
            if (!label) return;
            var market = port >= 0 ? World.Market(port) : null;
            bool visible = World.Ready && market;
            label.gameObject.SetActive(visible);
            if (!visible) return;
            // Managed ports include earned increases. Other mod ports retain their own
            // market override; this is never the separate saffron-good pricing cap.
            float cap = Supply.Contains(port) ? Economy.Cap(port) :
                market.goodsSoftCapOverride != 0 ? market.goodsSoftCapOverride : DebugMarketTracker.goodsAmountSoftCap;
            int rounded = Mathf.RoundToInt(cap);
            if (rounded == displayed) return;
            displayed = rounded;
            label.text = "Port Capacity: " + rounded.ToString(CultureInfo.InvariantCulture);
        }

        private void OnDestroy() { if (label) Destroy(label.gameObject); }
    }

    [HarmonyPatch(typeof(MissionListUI), "EnablePortMissionUI")]
    internal static class PortCapacityOpenPatch
    {
        static void Postfix(MissionListUI __instance, PortDude dude) => PortCapacityUI.Show(__instance, dude);
    }

    // ---- The mission page (MISSIONS_DRAFT.md stage 2: the layout the user approved, mockup round 3) ----
    // Every mission page, mail included. Postal Expansion (1.1.3) draws no reputation line of its own while a type
    // EconomyOverhaul.MissionPage exists: keep this name and namespace. Runs after Postal Expansion's page postfixes and places
    // its letter lines too.
    internal static class MissionPage
    {
        // Heights in the page's own frame (+x is the screen's left; the left column at x .676).
        private const float Left = .676f, Indent = .646f, RewardX = -.306f, MapScale = .85f;
        // shortcut: `failed` stays set for the rest of the game session (a page found without its texts once is never tried again, even a
        // new page object); replace with a reset when `built` changes if a page ever fails only for a moment.
        private static MissionDetailsUI built; private static bool failed;
        private static TextMesh originLabel, origin, destinationLabel, destination, dueLabel, due, distance, perUnit, perMile, reward, gold;
        private static TextMesh categoryLabel, category, capacity, atOrigin, atDestination, reputation, note;
        private static readonly Func<Mission, int> DeliveryRep = AccessTools.MethodDelegate<Func<Mission, int>>(AccessTools.Method(typeof(Mission), "GetDeliveryRep"));

        internal static void Show(MissionDetailsUI page, Mission mission)
        {
            if (!page || mission == null || failed) return;
            if (built != page && !Build(page)) { failed = true; Plugin.Log.LogWarning("EconomyOverhaul: the mission page's texts were not found; the game's page is kept."); return; }
            Missions.Info(mission, out var c, out var mods);
            bool mail = Missions.Postal(mission);
            Y(originLabel, .748f); Y(origin, .674f); Y(destinationLabel, .572f); Y(destination, .501f);
            categoryLabel.gameObject.SetActive(c != MissionCategory.None); category.gameObject.SetActive(c != MissionCategory.None);
            category.text = Rules.CategoryName(c) + ((mods & MissionModifier.Fragile) != 0 ? " (FRAGILE)" : "");
            // Postal Expansion's letter lines: "Location:" in the hidden Category's place (or Destination's when it hides it),
            // "Delivery hours:" between Due and Distance, which then move down into the room the capacity lines leave.
            var location = Postal(destination, "postal_expansion_letter_location_label");
            if (location && location.gameObject.activeSelf)
            {
                bool there = destinationLabel.gameObject.activeSelf;
                Y(location, there ? .409f : .572f); Y(Postal(destination, "postal_expansion_letter_location_value"), there ? .333f : .501f);
            }
            var hours = Postal(distance, "postal_expansion_letter_delivery_hours_label");
            bool letter = hours && hours.gameObject.activeSelf;
            Y(dueLabel, -.235f); Y(due, -.238f);
            if (letter) { Y(hours, -.320f); Y(Postal(distance, "postal_expansion_letter_delivery_hours_value"), -.320f); }
            float drop = letter ? -.073f : 0;
            Y(distance, -.332f + drop); Y(perUnit, -.404f + drop); Y(perMile, -.475f + drop);
            capacity.gameObject.SetActive(!mail); atOrigin.gameObject.SetActive(!mail); atDestination.gameObject.SetActive(!mail);
            if (!mail)
            {
                bool world = Missions.IsWorld(mission); int offeredCount = Missions.OfferedCount(mission);   // fewer chosen: x chosen / offered
                atOrigin.text = "+" + Rules.ChosenShare(Rules.CapacityAward(c, world, true) + Rules.ModifierCapacity(c, mods, true), mission.goodCount, offeredCount) + " at origin";
                atDestination.text = "+" + Rules.ChosenShare(Rules.CapacityAward(c, world, false) + Rules.ModifierCapacity(c, mods, false), mission.goodCount, offeredCount) + " at destination";
            }
            // The reward: the gold, the reputation as big under it (the game's per-unit value with every mod's rules, x the
            // count, as of today), and the note just above Accept.
            reputation.text = DeliveryRep(mission) * mission.goodCount + " reputation";
            note.text = (mods & MissionModifier.Pristine) != 0 ? "Only get increased reward\nwhen fully dry" : (mods & MissionModifier.Fragile) != 0 ? "Only get increased reward\nwhen intact" : "";
            QuantityArrows.Show(page, mission);   // the cargo count's arrows (BOARD_PLANK_DRAFT.md, 2026-11-08)
        }

        private static bool Build(MissionDetailsUI page)
        {
            var root = Fields.Get<GameObject>(page, "UI"); if (!root) return false;
            TextMesh Field(string name) => Fields.Get<TextMesh>(page, name);
            TextMesh Static(string text) => root.GetComponentsInChildren<TextMesh>(true).FirstOrDefault(t => (t.text ?? "").Trim() == text);
            originLabel = Static("Origin port:"); origin = Field("origin"); destinationLabel = Static("Destination:"); destination = Field("destination");
            dueLabel = Static("Due:"); due = Field("due"); distance = Field("distance"); perUnit = Field("goldPerUnit"); perMile = Field("goldPerMile");
            reward = Static("Reward:"); gold = Field("totalGold");
            var map = Fields.Get<Renderer>(page, "mapRenderer"); var locationText = Field("locationText");
            if (new Component[] { originLabel, origin, destinationLabel, destination, dueLabel, due, distance, perUnit, perMile, reward, gold, map }.Any(x => !x)) return false;
            // New lines are clones of the page's own (their font and black ink).
            TextMesh Clone(TextMesh source, string name, float size, float x, float y)
            {
                var t = UnityEngine.Object.Instantiate(source, source.transform.parent); t.name = "EconomyOverhaul " + name;
                t.characterSize = source.characterSize * size; t.transform.localPosition = new Vector3(x, y, source.transform.localPosition.z); return t;
            }
            categoryLabel = Clone(destinationLabel, "category label", 1, Left, .409f); categoryLabel.text = "Category:";
            category = Clone(destination, "category", 1, Left, .333f);
            capacity = Clone(distance, "capacity", .85f, Left, -.563f); capacity.text = "Port capacity:";
            atOrigin = Clone(distance, "capacity origin", .85f, Indent, -.633f);
            atDestination = Clone(distance, "capacity destination", .85f, Indent, -.702f);
            Y(reward, -.088f); gold.characterSize *= .8f; Y(gold, -.170f);
            reputation = Clone(gold, "reputation", 1, RewardX, -.252f);
            note = Clone(distance, "note", .6f, RewardX, -.354f); note.anchor = TextAnchor.MiddleCenter; note.alignment = TextAlignment.Center;
            // The map 85% as big about its top right corner (the route and marker are its children; "map unavailable" and
            // Postal Expansion's coordinates move with it). The game puts the map back at mapInitialPos after a zoom.
            var t0 = map.transform; var parent = t0.parent; var b = map.GetComponent<MeshFilter>().sharedMesh.bounds;
            Vector3 pivot = Enumerable.Range(0, 4).Select(i => parent.InverseTransformPoint(t0.TransformPoint(new Vector3(i % 2 == 0 ? b.min.x : b.max.x, i < 2 ? b.min.y : b.max.y, b.center.z))))
                .OrderByDescending(p => p.y - p.x).First();
            Vector3 Scaled(Vector3 p) => pivot + (p - pivot) * MapScale;
            t0.localPosition = Scaled(t0.localPosition); t0.localScale *= MapScale; Fields.Set(page, "mapInitialPos", t0.localPosition);
            if (locationText && locationText.transform.parent == parent) { var p = Scaled(locationText.transform.localPosition); p.z = locationText.transform.localPosition.z; locationText.transform.localPosition = p; }
            built = page; return true;
        }
        private static void Y(Component t, float y) { if (!t) return; var p = t.transform.localPosition; p.y = y; t.transform.localPosition = p; }
        private static TextMesh Postal(TextMesh sibling, string name) { var t = sibling.transform.parent.Find(name); return t ? t.GetComponent<TextMesh>() : null; }
    }
    [HarmonyPatch(typeof(MissionDetailsUI), "InsertMissionData")] internal static class MissionPagePatch
    {
        [HarmonyAfter("com.DogEggz.postalexpansion")] static void Postfix(MissionDetailsUI __instance, Mission mission) => MissionPage.Show(__instance, mission);
    }

    // ---- Missions ----
    [Serializable] public sealed class MissionState
    {
        public int slot, origin, destination, prefab, dueDay, totalPrice;
        public bool world, awarded;
        public int creditedDeliveries;
        // The mission category (MissionCategory); a record from before the rework reads 0 (None): old awards, no factor.
        [System.Runtime.Serialization.OptionalField] public int category;
        // Its modifiers (MissionModifier flags); 0 for none and for records from before them.
        [System.Runtime.Serialization.OptionalField] public int modifiers;
        // The offer's crates when fewer were chosen with the board's arrows (BOARD_PLANK_DRAFT.md, 2026-11-08): its reputation per crate and
        // port capacity award are scaled by the count / this. 0 for a mission taken whole and for every record from before.
        [System.Runtime.Serialization.OptionalField] public int offered;
    }
    internal static class Missions
    {
        private static readonly Dictionary<int, List<Mission>> offers = new Dictionary<int, List<Mission>>();
        private static readonly Dictionary<Mission, bool> sources = new Dictionary<Mission, bool>();
        private static readonly Dictionary<Mission, MissionModifier> offerModifiers = new Dictionary<Mission, MissionModifier>();
        // Each cargo offer of this opening as offered (its crates and its gold in the destination's coin), for the board's arrows and order
        // (BOARD_PLANK_DRAFT.md, 2026-11-08): the arrows change the offer's own goodCount and totalPrice.
        private static readonly Dictionary<Mission, (int count, int total)> offered = new Dictionary<Mission, (int count, int total)>();
        internal static readonly HashSet<int> DeliveredItems = new HashSet<int>();
        [ThreadStatic] internal static Good Delivering;
        internal static void Reset() { offers.Clear(); sources.Clear(); offerModifiers.Clear(); offered.Clear(); Board.Changed(); Delivering = null; }
        // An offer's crates and gold as offered (the mission's own for one not in this opening's lists: Postal Expansion's letters, another
        // mod's); an accepted mission's crates as offered from its record.
        internal static int OfferedCount(Mission m)
        {
            if (m == null) return 0;
            if (m.missionIndex < 0) return offered.TryGetValue(m, out var o) ? o.count : m.goodCount;
            var r = Record(m); return r != null && r.offered > 0 ? r.offered : m.goodCount;
        }
        internal static int OfferedTotal(Mission m) => m != null && m.missionIndex < 0 && offered.TryGetValue(m, out var o) ? o.total : m?.totalPrice ?? 0;
        // The arrows show on an offer of this opening that is cargo of more than one crate, without Bulk, Pristine or Rush.
        internal static bool CanChoose(Mission m)
        {
            if (m == null || m.missionIndex >= 0 || Postal(m) || !offered.TryGetValue(m, out var o) || o.count <= 1) return false;
            Info(m, out _, out var mods); return !Rules.QuantityLocked(mods);
        }
        // One arrow's click: the offer's crates one more or one less (1 to the offered count), its gold the offered total x chosen / offered.
        internal static bool Choose(Mission m, int delta)
        {
            if (!CanChoose(m)) return false;
            var o = offered[m]; int to = Rules.ChosenCount(m.goodCount + delta, o.count); if (to == m.goodCount) return false;
            m.goodCount = to; m.totalPrice = Rules.ChosenShare(o.total, to, o.count); m.pricePerKm = m.totalPrice / m.distance;
            return true;
        }
        internal static bool Postal(Mission m)
        {
            if (m?.goodPrefab == null) return false;
            var p = m.goodPrefab.GetComponent<SaveablePrefab>();
            return p && (p.prefabIndex == 221 || p.prefabIndex == 238 || p.prefabIndex == 239);
        }
        internal static MissionState Record(Mission m)
        {
            if (m == null || m.missionIndex < 0) return null;
            int prefab = m.goodPrefab.GetComponent<SaveablePrefab>().prefabIndex;
            return World.State.missions.Find(s => s.slot == m.missionIndex && s.origin == m.originPort.portIndex &&
                s.destination == m.destinationPort.portIndex && s.prefab == prefab && s.dueDay == m.dueDay && s.totalPrice == m.totalPrice);
        }
        internal static void Accepted(Mission m)
        {
            if (m.missionIndex < 0 || PlayerMissions.missions[m.missionIndex] != m) return;
            if (!Postal(m))
            {
                World.State.missions.RemoveAll(s => s.slot == m.missionIndex);
                World.State.missions.Add(new MissionState { slot = m.missionIndex, origin = m.originPort.portIndex,
                    destination = m.destinationPort.portIndex, prefab = m.goodPrefab.GetComponent<SaveablePrefab>().prefabIndex,
                    dueDay = m.dueDay, totalPrice = m.totalPrice, world = sources.TryGetValue(m, out bool world) ? world : m.distance >= 140,
                    creditedDeliveries = m.GetDeliveredCount(), category = (int)Category(m),
                    modifiers = offerModifiers.TryGetValue(m, out var modifiers) ? (int)modifiers : 0,
                    offered = offered.TryGetValue(m, out var whole) && whole.count != m.goodCount ? whole.count : 0 });
            }
            Board.Changed();
            // The game builds its lists again after every accept (MissionDetailsUI.ClickButton), from the warehouse the accepted
            // crates just left. The lists of this opening stay (their rolls kept): the taken offer leaves them (it was left as an
            // empty row that still counted in the pages), and each of the origin's other offers of that good, on both tabs, stays
            // while the warehouse still holds it whole (Rules.KeepAfterAccept). Was: the other tab's offers still counted the crates
            // just taken (12 rice, an offer on each tab: 24 crates; the review of 2026-10-18, Claude's fix).
            int port = m.originPort.portIndex; bool worldTab = sources.TryGetValue(m, out bool fromWorld) ? fromWorld : m.distance >= 140;
            var lists = new[] { port * 2 + (worldTab ? 1 : 0), port * 2 + (worldTab ? 0 : 1) }.Where(offers.ContainsKey).Select(k => offers[k]).ToArray();
            foreach (var list in lists)
                list.RemoveAll(x => x == null || x.originPort == m.originPort && x.destinationPort == m.destinationPort && x.goodPrefab == m.goodPrefab);
            if (Postal(m)) return;
            int good = GoodIndex(m); var office = m.originPort.GetComponent<IslandMissionOffice>(); var warehouse = office ? office.GetData() : null;
            var same = lists.SelectMany(l => l.Where(x => !Postal(x) && GoodIndex(x) == good)).ToArray();
            // Each kept offer judged at its offered count, not the count chosen with the arrows: Choose can raise a kept offer back to it
            // without a stock check (the deep review of 2026-11-09: two offers of 12 rice on 12 in the warehouse, both cut to 6, one taken;
            // the other, kept at 6, raised to 12 and taken, made 12 crates from 6).
            var keep = Rules.KeepAfterAccept(same.Select(OfferedCount).ToArray(), warehouse == null ? 0 : warehouse.Count(x => x == good));
            var gone = new HashSet<Mission>(same.Where((x, i) => !keep[i]));
            if (gone.Count > 0) foreach (var list in lists) list.RemoveAll(gone.Contains);
        }
        internal static void Delivered(Mission m)
        {
            if (!World.Ready || Postal(m)) return;
            var r = Record(m);
            if (r == null)
            {
                // Existing accepted vanilla missions migrate without retroactive rewards.
                r = new MissionState { slot = m.missionIndex, origin = m.originPort.portIndex, destination = m.destinationPort.portIndex,
                    prefab = m.goodPrefab.GetComponent<SaveablePrefab>().prefabIndex, dueDay = m.dueDay, totalPrice = m.totalPrice,
                    world = m.distance >= 140, creditedDeliveries = Math.Max(0, m.GetDeliveredCount() - 1) };
                World.State.missions.Add(r);
            }
            int delivered = m.GetDeliveredCount(), delta = delivered - r.creditedDeliveries;
            if (delta > 0)
            {
                int g = PrefabsDirectory.ItemToGoodIndex(r.prefab); var market = World.Market(r.destination);
                // a delivery lifts a demanding port's supply as a sale does, so it can end its event too (it never went through
                // SellGood's check; the deep review of 2026-11-02, Claude's fix)
                if (market && g > 0 && g < market.currentSupply.Length)
                {
                    market.currentSupply[g] += .5f * delta; Economy.Refresh(market);
                    if (!Startup.Running) Demand.Check(r.destination, g);
                }
                r.creditedDeliveries = delivered;
            }
            if (delivered >= m.goodCount && !r.awarded)
            {
                var c = (MissionCategory)r.category; var mods = (MissionModifier)r.modifiers;
                r.awarded = true;
                // fewer crates chosen than offered: the award x chosen / offered, rounded (the user, 2026-11-08: "Gold, rep and capacity")
                Economy.Award(r.origin, Rules.ChosenShare(Rules.CapacityAward(c, r.world, true) + Rules.ModifierCapacity(c, mods, true), m.goodCount, r.offered));
                Economy.Award(r.destination, Rules.ChosenShare(Rules.CapacityAward(c, r.world, false) + Rules.ModifierCapacity(c, mods, false), m.goodCount, r.offered));
            }
        }
        internal static MissionCategory Category(Mission m) =>
            m?.goodPrefab == null || Postal(m) ? MissionCategory.None : Rules.Category(PrefabsDirectory.ItemToGoodIndex(m.goodPrefab.GetComponent<SaveablePrefab>().prefabIndex));
        // Each good prefab's reputation gate (Rules.GoodRep: its category's, or its own) (the game reads it only for missions: the warehouse fill, the
        // generation and Port.GetDueDay). Mail (51) is left to the game and Postal Expansion.
        private static PrefabsDirectory gated;   // Economy.Configure runs each frame: write the gates once per directory
        internal static void ApplyRepGates()
        {
            if (!PrefabsDirectory.instance || gated == PrefabsDirectory.instance) return;
            gated = PrefabsDirectory.instance;
            for (int g = 1; g <= 64; g++)
            {
                var c = Rules.Category(g); if (c == MissionCategory.None) continue;
                var item = PrefabsDirectory.instance.GetGood(g); var good = item ? item.GetComponent<Good>() : null;
                if (good) good.requiredRepLevel = Rules.GoodRep(g);
            }
        }
        // Whether a mission counts as a world one for capacity (the World tab, distance 140+): an accepted one's record, an offer's tab.
        internal static bool IsWorld(Mission m)
        {
            var r = Record(m); if (r != null) return r.world;
            return sources.TryGetValue(m, out bool world) ? world : m.distance >= 140;
        }
        // A mission's category and modifiers: an accepted one's from its record (None for mail and for missions accepted before
        // the rework), an offer's from its good and the roll.
        internal static void Info(Mission m, out MissionCategory category, out MissionModifier modifiers)
        {
            category = MissionCategory.None; modifiers = MissionModifier.None;
            if (m == null || Postal(m)) return;
            var r = Record(m);
            if (r != null) { category = (MissionCategory)r.category; modifiers = (MissionModifier)r.modifiers; return; }
            if (m.missionIndex >= 0) return;
            category = Category(m); offerModifiers.TryGetValue(m, out modifiers);
        }
        // Gold of one delivered unit (the game's, after lateness): x its condition; a Pristine one dry keeps its x2, else loses it.
        internal static int DeliveryGold(Mission m, int gold, ShipItem item)
        {
            Info(m, out _, out var mods);
            float condition = CargoCondition.Factor(item);
            bool pristine = (mods & MissionModifier.Pristine) != 0, fragile = (mods & MissionModifier.Fragile) != 0;
            if (!pristine && !fragile) return Mathf.RoundToInt(gold * condition);
            // Pristine keeps its x2 when dry, a fragile mission when intact (never both: fragile goods are water-proof).
            return Kept(item, pristine) ? gold : Mathf.RoundToInt(gold / 2f * condition);
        }
        private static bool Kept(ShipItem item, bool pristine) =>
            pristine ? Rules.WaterHintPercent(CargoCondition.Health(item)) == 0 : Rules.DamagePercent(CargoCondition.Impact(item)) == 0;
        private static bool Ruined(ShipItem item, bool pristine) =>
            pristine ? Rules.WaterHintPercent(CargoCondition.Health(item)) == 100 : Rules.DamagePercent(CargoCondition.Impact(item)) == 100;
        // Reputation of one unit (the game's, after lateness) x the category's factor (Bulk x2). Delivered (item set): x its
        // condition; Pristine x2 dry and -50 ruined; fragile x2 intact and -100 ruined. On the page (no item) the increased reward.
        internal static int DeliveryReputation(Mission m, int rep, ShipItem item)
        {
            Info(m, out var c, out var mods);
            bool pristine = (mods & MissionModifier.Pristine) != 0, fragile = (mods & MissionModifier.Fragile) != 0;
            if (item && pristine && Ruined(item, true)) return Rules.PristineRuinedRep;
            if (item && fragile && Ruined(item, false)) return Rules.FragileRuinedRep;
            if (rep <= 0) return rep;
            // fewer crates chosen than offered: the offered crate's figure (BOARD_PLANK_DRAFT.md, Rules.RepScale)
            float factor = Rules.CategoryReputation(c) * ((mods & MissionModifier.Bulk) != 0 ? 2 : 1) * Rules.RepScale(m.goodCount, OfferedCount(m));
            if (!item) factor *= (pristine ? 2 : 1) * (fragile ? 2 : 1);
            else if ((pristine || fragile) && Kept(item, pristine)) factor *= 2;
            else factor *= CargoCondition.Factor(item);
            return Mathf.RoundToInt(rep * factor);
        }
        // After a load the game rebuilds each mission's name from its good: the modifiers go back in front.
        // The option "Enable fragile cargo" off (the user, 2026-10-15): every mission drops its Fragile modifier for good, with
        // its x2 gold (a taken mission's record and price, the offers on show); a fragile mission's tools crates are plain again.
        // Runs when the option is turned off and after a load while it is off.
        internal static void DropFragile()
        {
            if (Settings.FragileEnabled || !World.Ready) return;
            if (PlayerMissions.missions != null)
                foreach (var m in PlayerMissions.missions)
                {
                    var r = Record(m); if (r == null || ((MissionModifier)r.modifiers & MissionModifier.Fragile) == 0) continue;
                    r.modifiers &= ~(int)MissionModifier.Fragile;
                    m.totalPrice = r.totalPrice = Mathf.RoundToInt(m.totalPrice / 2f); m.pricePerKm = m.totalPrice / m.distance;
                }
            foreach (var m in offerModifiers.Keys.ToList())
            {
                // A taken mission is still listed here; its record was handled above.
                if (m.missionIndex >= 0 || (offerModifiers[m] & MissionModifier.Fragile) == 0) continue;
                offerModifiers[m] &= ~MissionModifier.Fragile;
                m.totalPrice = Mathf.RoundToInt(m.totalPrice / 2f); m.pricePerKm = m.totalPrice / m.distance;
                if (offered.TryGetValue(m, out var o)) offered[m] = (o.count, Mathf.RoundToInt(o.total / 2f));   // as offered, for the arrows
            }
            Board.Changed();
            foreach (var c in World.Cargo.Values) c.fragile = false;
        }
        internal static void Loaded()
        {
            DropFragile();
            if (PlayerMissions.missions == null) return;
            foreach (var m in PlayerMissions.missions)
            {
                Info(m, out _, out var mods); string prefix = Rules.ModifierPrefix(mods);
                if (prefix.Length > 0 && !m.missionName.StartsWith(prefix)) m.missionName = prefix + m.missionName;
            }
        }
        internal static void Warehouse(IslandMissionOffice office)
        {
            if (!World.Running) return;
            AdvanceWarehouse(office, Time.deltaTime * Sun.sun.timescale * 125);
        }
        internal static void AdvanceWarehouse(IslandMissionOffice office, double elapsed)
        {
            var m = office.GetComponent<IslandMarket>(); if (!m || !Supply.Contains(m.GetPortIndex())) return;
            var s = World.MarketData(m.GetPortIndex());
            if (s.blocked) return;
            if (office.debugRunCycle) { office.debugRunCycle = false; s.warehouseTimer = Math.Min(0, s.warehouseTimer); }
            SimulationClock.Advance(ref s.warehouseTimer, elapsed,
                .1 / SimulationClock.NormalSpeed, SimulationClock.CycleBudget,
                (m: m, s: s), static x => { x.s.pending = 3; DrainWarehouse(x.m, x.s); }, static x => x.s.blocked);
        }
        internal static void DrainWarehouse(IslandMarket m, MarketState s)
        {
            var office = m.GetComponent<IslandMissionOffice>(); if (!office || !office.enabled) return;
            int[] goods = Resize(m, office, s);
            s.slot %= goods.Length;
            int size = Math.Min(m.production.Length, m.currentSupply.Length);
            var stocked = new bool[size];
            while (s.pending > 0)
            {
                for (int g = 1; g < size; g++) stocked[g] = m.currentSupply[g] > 0 && m.HasGood(g);
                int selected = Rules.WarehousePick(Counts(goods, s.slot, size), m.production, stocked, goods.Length, UnityEngine.Random.value);
                if (selected == 0) { s.blocked = true; return; }
                if (selected > 0 && selected != goods[s.slot])
                {
                    // Bought from the market; the good it replaces goes back (the game's way).
                    if (goods[s.slot] != 0) m.SellGood(goods[s.slot]);
                    goods[s.slot] = selected; m.PurchaseGood(selected);
                }
                s.slot = (s.slot + 1) % goods.Length; s.pending--;
            }
        }
        // Each good's slots, leaving out one slot (the one being filled; -1 for none).
        private static int[] Counts(int[] goods, int skip, int size)
        {
            var counts = new int[size];
            for (int i = 0; i < goods.Length; i++) if (i != skip && goods[i] > 0 && goods[i] < size) counts[goods[i]]++;
            return counts;
        }
        // The warehouse follows the port's capacity: grown with empty slots (filled first by the normal cycle), or, if the
        // capacity ever falls, its surplus sold back to the market.
        internal static int[] Resize(IslandMarket m, IslandMissionOffice office, MarketState s)
        {
            int[] goods = office.GetData() ?? new int[0];
            int capacity = Rules.WarehouseCapacity(Economy.Cap(m.GetPortIndex()));
            if (goods.Length == capacity) return goods;
            var next = new int[capacity]; int kept = 0;
            foreach (int g in goods) { if (g == 0) continue; if (kept < capacity) next[kept++] = g; else m.SellGood(g); }
            office.LoadData(next); s.slot = kept % capacity;
            return next;
        }
        // Fills a new warehouse to 80% (Rules.PrefillSlots) by the fill rule without taking the market's stock (a new game's preparation, and an
        // older save's migration); the normal cycle then fills the rest from the market.
        internal static void Prefill(IslandMarket m, IslandMissionOffice office, MarketState s)
        {
            int capacity = Rules.WarehouseCapacity(Economy.Cap(m.GetPortIndex())), filled = 0;
            var goods = new int[capacity]; var counts = new int[m.production.Length];
            var all = Enumerable.Repeat(true, m.production.Length).ToArray();
            for (; filled < Rules.PrefillSlots(capacity); filled++)
            {
                int g = Rules.WarehousePick(counts, m.production, all, capacity, UnityEngine.Random.value);
                if (g <= 0) break;
                goods[filled] = g; counts[g]++;
            }
            office.LoadData(goods); s.slot = filled % capacity; s.blocked = false;
        }
        // A save of the mod from before the warehouse rework (state version 4: 0.8.5-0.9.5), once: the old contents go back to
        // the market they were bought from, then the warehouse is resized and prefilled. A first install into a vanilla save does
        // not come here: its stock and warehouses are cleared by the new game's preparation (MIDSAVE_DRAFT.md).
        // The sales are batched as a trader's arrival is (Fleet.Arrive): the market's own report is set once afterwards, by
        // World.Load's Economy.Refresh of every market.
        internal static void Migrate(IslandMarket m, IslandMissionOffice office, MarketState s)
        {
            int[] goods = office.GetData() ?? new int[0];
            int[] sold = Rules.MigrationSales(goods, m.currentSupply?.Length ?? 0);
            int skipped = goods.Count(g => g > 0) - sold.Length;
            if (skipped > 0) Plugin.Log.LogWarning("Warehouse migration at port " + m.GetPortIndex() + ": " + skipped + " crate(s) of goods the market does not list were not sold back.");
            Economy.BatchDepth++;
            try { foreach (int g in sold) m.SellGood(g); }
            finally { Economy.BatchDepth--; }
            Prefill(m, office, s);
        }
        internal static Mission[] Generate(IslandMissionOffice office, int page, bool world)
        {
            var m = office.GetComponent<IslandMarket>();
            if (!m || !Supply.Contains(m.GetPortIndex()) || Uninstall.Prepared) return new Mission[5];
            Economy.EnsureKnownPrices(m); int key = m.GetPortIndex() * 2 + (world ? 1 : 0);
            if (!offers.TryGetValue(key, out var list))
            {
                list = Build(office, world); offers.Add(key, list);
                foreach (var mission in list) sources[mission] = world;
            }
            // The board's order and region (BOARD_PLANK_DRAFT.md, 2026-11-08): the game's paging and Mission Rework read this view.
            var view = Board.View(list);
            m.GetPort().SetMissonCount(view.Count);
            var result = new Mission[5];
            for (int i = 0; i < 5; i++) if (page * 5 + i >= 0 && page * 5 + i < view.Count) result[i] = view[page * 5 + i];
            return result;
        }
        private static int GoodIndex(Mission m) => PrefabsDirectory.ItemToGoodIndex(m.goodPrefab.GetComponent<SaveablePrefab>().prefabIndex);
        // The modifiers, rolled on the tab's final list (before its pay goes into the destination's currency).
        private static void ApplyModifiers(int[] warehouse, List<Mission> list, int level)
        {
            var categories = list.Select(Category).ToArray();
            var damageable = list.Select(x => !Postal(x) && !CargoCondition.WaterProof(GoodIndex(x))).ToArray();
            var doublable = list.Select(x => !Postal(x) && Rules.BulkDoubles(x.goodCount, warehouse.Count(w => w == GoodIndex(x)))).ToArray();
            var rolled = Rules.RollModifiers(categories, damageable, level, () => UnityEngine.Random.value, doublable);
            for (int i = 0; i < list.Count; i++)
            {
                var mission = list[i]; var c = categories[i]; var mods = rolled[i];
                // Fragile (stage 3): gems, sculptures and marble always, tools half the time; at any reputation; none with
                // the option "Enable fragile cargo" off.
                if (Settings.FragileEnabled && !Postal(mission) && (Rules.FragileGood(GoodIndex(mission)) || GoodIndex(mission) == Rules.FragileTools && UnityEngine.Random.value < Rules.FragileChance))
                    mods |= MissionModifier.Fragile;
                if (mods == MissionModifier.None) continue;
                if ((mods & MissionModifier.Bulk) != 0)
                {
                    // Bulk: twice the normal cargo (rolled only where the warehouse holds that many, Rules.BulkDoubles); paid as
                    // the normal mission per crate (Rules.BulkGold), then x2 below.
                    int count = 2 * mission.goodCount;
                    mission.totalPrice = Rules.BulkGold(mission.totalPrice, mission.goodCount, count); mission.goodCount = count;
                }
                mission.totalPrice *= Rules.ModifierGold(mods);
                if ((mods & MissionModifier.Rush) != 0) mission.dueDay = GameState.day + Rules.RushDays(mission.distance, Rules.RushKnots(c), Sun.sun.GetRealtimeDayLength());
                mission.missionName = Rules.ModifierPrefix(mods) + mission.missionName;
                offerModifiers[mission] = mods;
            }
        }
        private static bool Already(Port origin, Port dest, GameObject prefab) => PlayerMissions.missions != null && PlayerMissions.missions.Any(m => m != null && m.originPort == origin && m.destinationPort == dest && m.goodPrefab == prefab);
        private static List<Mission> Build(IslandMissionOffice office, bool world)
        {
            var m = office.GetComponent<IslandMarket>(); if (!m) return new List<Mission>();
            Economy.EnsureKnownPrices(m); var origin = m.GetPort();
            var list = new List<Mission>(); int[] warehouse = office.GetData();
            var pricing = new Dictionary<Mission, (float unit, bool ocean, MissionCategory category)>();
            if (origin.portIndex == Rules.NoMissionsFrom) return list;
            int level = Reputations.PortLevel(origin);
            // Each good's crates in the warehouse, counted once (was counted again for every destination and good).
            var stock = new int[m.currentSupply.Length];
            foreach (int x in warehouse) if (x > 0 && x < stock.Length) stock[x]++;
            foreach (var dest in Port.ports)
            {
                // Only to ports the mod manages (Supply): a mission to another mod's port fails the save's check of its port
                // (StateCodec.PortId) and saving goes off (the review of 2026-10-18, Claude's fix).
                if (!dest || dest == origin || dest.portIndex == 7 || dest.portIndex == 33 || !Supply.Contains(dest.portIndex)) continue;
                var report = m.knownPrices[dest.portIndex];
                if (report == null || !report.approved) continue;
                float distance = Mission.GetDistance(origin, dest);
                if ((distance >= 140) != world || distance > Reputations.MaxDistance(Reputations.Account(origin))) continue;
                bool ocean = Mission.UseOceanMapFor(origin, dest);
                int other = 0;
                for (int g = 1; g < m.currentSupply.Length; g++)
                {
                    var category = Rules.Category(g); if (category == MissionCategory.None) continue;
                    // Cargo: the reputation table x the category's factor (it replaces the office's own maxGoodsPerMission, twice
                    // that on the ocean map).
                    int count = Math.Min(Rules.MissionCargo(level, category), stock[g]); if (count <= 0) continue;
                    var item = PrefabsDirectory.instance.GetGood(g); if (!item) continue;
                    var good = item.GetComponent<Good>();
                    if (!good || good.requiredRepLevel > level || Already(origin, dest, item.gameObject)) continue;
                    float profit = (report.sellPrices[g] - m.GetGoodPrice(g)) * count;
                    if (!Rules.MissionWorthwhile(profit, distance)) continue;
                    float unit = report.sellPrices[g] - m.GetGoodPrice(g);
                    var offer = new Mission(origin, dest, item.gameObject, count, Rules.MissionTotal(unit, count, distance, ocean, category), 1, 0, origin.GetDueDay(dest, good));
                    list.Add(offer); pricing[offer] = (unit, ocean, category); other++;
                }
                var mail = PrefabsDirectory.instance.GetGood(51);
                if (mail && !Already(origin, dest, mail.gameObject))
                {
                    // Retain Postal Expansion's normal-mail quantity/reward patch point.
                    list.Add((Mission)Fields.Call(office, "GenerateMailMission", mail.GetComponent<Good>(), dest, distance, other));
                }
            }
            list.Sort((a, b) => b.pricePerKm.CompareTo(a.pricePerKm));
            var before = list.ToDictionary(x => x, x => x.goodCount);
            list = (List<Mission>)Fields.Call(office, "PruneNoSupplyMissions", list);
            // An offer the prune cut to the warehouse's stock is priced again for its new count (D-B1; Bulk then pays per crate of
            // the cut offer). The list keeps the game's order; the 30-offer cap counts each offer's pay as it is now.
            // A cut offer the game would not make at its new count (its worth test: the distance fee over the profit) is dropped
            // (the user, 2026-10-19, Q1: "b").
            var unworthy = new HashSet<Mission>();
            foreach (var mission in list)
                if (pricing.TryGetValue(mission, out var p) && mission.goodCount < before[mission])
                {
                    if (!Rules.MissionWorthwhile(p.unit * mission.goodCount, mission.distance)) { unworthy.Add(mission); continue; }
                    mission.totalPrice = Rules.MissionTotal(p.unit, mission.goodCount, mission.distance, p.ocean, p.category);
                    mission.pricePerKm = mission.totalPrice / mission.distance;
                }
            if (unworthy.Count > 0) list = list.Where(x => !unworthy.Contains(x)).ToList();
            var keep = Rules.KeepOffersByPay(list.Select(x => x.pricePerKm).ToArray(), list.Select(Postal).ToArray(), Rules.MissionListCap);
            list = list.Where((x, i) => keep[i]).ToList();
            ApplyModifiers(warehouse, list, level);
            foreach (var mission in list)
            {
                mission.totalPrice = CurrencyMarket.instance.GetSellPriceInCurrency((Currency)mission.destinationPort.region, mission.totalPrice, false);
                mission.pricePerKm = mission.totalPrice / mission.distance;
                if (!Postal(mission)) offered[mission] = (mission.goodCount, mission.totalPrice);   // as offered (the board's arrows)
            }
            return list;
        }
    }
    // ---- The mission board's order and region (BOARD_PLANK_DRAFT.md; the user's request of 2026-11-08) ----
    // The sort and the region chosen on the board's dropdowns, kept for the game session (not saved). A tab's view: its offers in that order
    // and region, made again only after a change (a choice, an accept, a new opening, the fragile option); an offer is sorted as offered, so
    // an arrow's click doesn't move it. Postal Expansion sorts its own list again (by the coin number): its result is put in this order too.
    internal static class Board
    {
        internal static Rules.BoardSort Sort = Rules.BoardSort.PerMile;
        internal static int Region = -1;   // -1 all regions, else the destination's reputation account 0-3 (3 Chronos)
        private static int stamp;
        private static readonly Dictionary<List<Mission>, (int stamp, int count, List<Mission> view)> views = new Dictionary<List<Mission>, (int, int, List<Mission>)>();
        internal static void Changed() { stamp++; views.Clear(); }
        // shortcut: the daily currency rate a sort reads (RealTotal; Fx.AdvanceDay sets it when the day changes) doesn't make the view
        // again, so a board kept open past midnight keeps the order of the old rates until the next change; replace with a Changed() at
        // the day's rate change if a player sees it.
        internal static List<Mission> View(List<Mission> list)
        {
            if (list == null) return new List<Mission>();
            if (views.TryGetValue(list, out var v) && v.stamp == stamp && v.count == list.Count) return v.view;
            var view = Ordered(list); views[list] = (stamp, list.Count, view); return view;
        }
        internal static List<Mission> Ordered(List<Mission> list)
        {
            var items = list.Where(x => x != null && x.destinationPort).ToList();
            // The filter's region is the reputation account (Chronos' port 21 lies in region 2 but is its own account 3, the filter's "Chronos";
            // the deep review of 2026-11-09). Rate keeps the game's region: a mission is paid in currency[(int)destinationPort.region].
            var keys = items.Select(Key).ToArray(); var regions = items.Select(x => Reputations.Account(x.destinationPort)).ToArray();
            return Rules.BoardOrder(keys, regions, Region).Select(i => items[i]).ToList();
        }
        internal static void InPlace(List<Mission> list) { if (list == null) return; var ordered = Ordered(list); list.Clear(); list.AddRange(ordered); }
        // A mission is paid in its destination's coin (Mission.DeliverGood).
        internal static float Rate(int region) =>
            CurrencyMarket.instance && CurrencyMarket.instance.currentPrices != null && region >= 0 && region < CurrencyMarket.instance.currentPrices.Length && CurrencyMarket.instance.currentPrices[region] > 0 ? CurrencyMarket.instance.currentPrices[region] : 1;
        internal static double RealTotal(Mission m) => Rules.RealValue(Missions.OfferedTotal(m), Rate((int)m.destinationPort.region));
        // The page's reputation figure as offered (its per-crate value with every mod's rules, Rules.RepScale bringing it to the offered crate's).
        internal static int Reputation(Mission m) => GameRep(m) * Missions.OfferedCount(m);
        private static readonly Func<Mission, int> GameRep = AccessTools.MethodDelegate<Func<Mission, int>>(AccessTools.Method(typeof(Mission), "GetDeliveryRep"));
        private static double Key(Mission m) => Rules.BoardKey(Sort, RealTotal(m), m.distance, Sort == Rules.BoardSort.Reputation ? Reputation(m) : 0);
    }
    // The two dropdowns on the port's mission list (the user: "Mission UI now have 2 drop down list as filter"): each a plate copied from the
    // list's back button (the game's row plate), narrower, left of the list, under the list's page buttons, so they show exactly while the port's
    // offers do (the game hides the page buttons in the log book, Mission Rework while it shows the player's own missions). A click on a plate
    // opens its choices as plates under it, the chosen one in the plate's own shade and the others darker; a choice closes it and the list goes
    // back to page 1 (the game's ResetPage, which Postal Expansion takes over for its own paging).
    internal sealed class BoardUI : MonoBehaviour
    {
        // In the page buttons' frame (+x the screen's left): the list's tabs stand at y .19, its rows .049 apart. X and Width (a share of the
        // back button's width) only where the room left of the list can't be measured; else the plates fill that room (Room).
        internal const float X = .40f, SortY = .19f, RegionY = .12f, Step = .049f, Width = .62f, Shade = .78f;
        // Between the plates and the list, and the screen's edge kept free (a share of its width: the room is read on the plates' pivot plane, and
        // their tilted, deep plates came out about 10 px wider there at 1280 x 720, mb-4); the narrowest plate (a share of the back button).
        internal const float ListGap = .016f, EdgeMargin = .02f, MinWidth = .3f;
        internal sealed class Drop
        {
            internal GameObject head; internal TextMesh headText; internal bool open;
            internal readonly List<(GameObject plate, TextMesh text, Material material, LoanAction button)> choices = new List<(GameObject, TextMesh, Material, LoanAction)>();
        }
        internal static BoardUI Instance;
        internal Drop SortDrop, RegionDrop;   // for tests
        // shortcut: `failed` stays set as long as this board object lives (plates not found once are never looked for again on it); replace
        // with a retry on the next opening if a board ever fails only for a moment.
        private MissionListUI ui; private bool failed, narrowLogged; private float builtRoom = float.NaN;
        private readonly List<GameObject> made = new List<GameObject>();
        internal static void Opened(MissionListUI ui)
        {
            if (Startup.BlocksGameplay || !ui) return;
            var b = ui.GetComponent<BoardUI>() ?? ui.gameObject.AddComponent<BoardUI>(); Instance = b;
            if (b.failed) return;
            // Fitted to the room left of the list on this screen (its shape and size): made again when that room changed (a new resolution).
            float room = Room(ui, out float listLeft);
            if (b.SortDrop != null && !(float.IsNaN(room) && float.IsNaN(b.builtRoom)) && !(Mathf.Abs(room - b.builtRoom) < .002f)) b.Unmake();
            if (b.SortDrop == null && !b.Build(ui, room, listLeft)) { b.failed = true; Plugin.Log.LogWarning("EconomyOverhaul: the mission board's plates were not found; no sort or region dropdown."); return; }
            b.Close(); b.Refresh();
        }
        // The room left of the list on the player's screen, in the page buttons' frame: from the list's left edge (its back button's and welcome
        // box's) to the screen's edge where the plates stand, seen through the camera that draws the board (the game's "UI camera", its field of
        // view fixed, found by name as MouseButtonPointer does). NaN where it can't be measured. Was a fixed place: at 1280 x 720 it ran 61 px off
        // the screen (mb-2, 2026-11-08), and the room changes with the screen's shape (about 190 px at 16:9, little at 4:3).
        internal static float Room(MissionListUI list, out float listLeft)
        {
            listLeft = 0;
            var pages = Fields.Get<GameObject>(list, "pageButtons"); if (!pages || !pages.transform.parent) return float.NaN;
            var frame = pages.transform; var source = OfficeUiParts.Find(frame.parent, "mission button (back)");
            var bg = source ? source.Find("bg+trigger") : null; var r = bg ? bg.GetComponent<MeshRenderer>() : null;
            if (!r || !r.GetComponent<MeshFilter>()) return float.NaN;
            listLeft = BookUI.InFrameBounds(r, frame).max.x;
            var welcome = Fields.Get<GameObject>(list, "portWelcome");
            if (welcome) foreach (var w in welcome.GetComponentsInChildren<MeshRenderer>(true)) if (w.GetComponent<MeshFilter>()) listLeft = Mathf.Max(listLeft, BookUI.InFrameBounds(w, frame).max.x);
            var camObject = GameObject.Find("UI camera"); var cam = camObject ? camObject.GetComponent<Camera>() : null; if (!cam) cam = Camera.main; if (!cam) return float.NaN;
            var at = frame.TransformPoint(new Vector3(listLeft, SortY, source.localPosition.z));
            var ray = cam.ScreenPointToRay(new Vector3(cam.pixelRect.xMin + cam.pixelWidth * EdgeMargin, cam.WorldToScreenPoint(at).y, 0));
            if (!new Plane(frame.TransformDirection(Vector3.forward), at).Raycast(ray, out float d)) return float.NaN;
            float edge = frame.InverseTransformPoint(ray.GetPoint(d)).x;
            return edge - listLeft - ListGap;
        }
        private void Unmake()
        {
            foreach (var go in made) if (go) UnityEngine.Object.Destroy(go);
            made.Clear(); SortDrop = RegionDrop = null;
        }
        private bool Build(MissionListUI list, float room, float listLeft)
        {
            ui = list;
            var pages = Fields.Get<GameObject>(list, "pageButtons"); if (!pages || !pages.transform.parent) return false;
            // Mission Rework moves the back button out of the page buttons (into the list's buttons): found wherever it is.
            var source = OfficeUiParts.Find(pages.transform.parent, "mission button (back)"); if (!source) return false;
            var sourceBg = source.Find("bg+trigger"); var sourceRenderer = sourceBg ? sourceBg.GetComponent<MeshRenderer>() : null;
            if (!sourceRenderer || !sourceRenderer.GetComponent<MeshFilter>()) return false;
            // The plates' width and middle: the room left of the list, at most Width of a row, at least MinWidth (then a narrow screen cuts them).
            float full = BookUI.InFrameBounds(sourceRenderer, pages.transform).size.x, share = Width, x = X;
            if (!float.IsNaN(room) && full > 0)
            {
                share = Mathf.Clamp(room / full, MinWidth, Width); x = listLeft + ListGap + full * share / 2;
                if (room / full < MinWidth && !narrowLogged) { narrowLogged = true; Plugin.Log.LogInfo("EconomyOverhaul: little room left of the mission board on this screen (" + (room / full * 100).ToString("F0") + "% of a row): its dropdowns may run off the screen's edge."); }
            }
            builtRoom = room;
            SortDrop = Make(pages.transform, source, "sort", x, SortY, share, Rules.BoardSortNames.Length, i => Pick(true, i), () => Toggle(true));
            RegionDrop = Make(pages.transform, source, "region", x, RegionY, share, Rules.BoardRegionNames.Length, i => Pick(false, i), () => Toggle(false));
            return SortDrop != null && RegionDrop != null;
        }
        private Drop Make(Transform parent, Transform source, string name, float x, float y, float share, int count, Action<int> pick, Action toggle)
        {
            var d = new Drop();
            var head = Plate(parent, source, "EconomyOverhaul " + name, x, y, share, toggle); if (head.plate == null) return null;
            made.Add(head.plate); d.head = head.plate; d.headText = head.text;
            for (int i = 0; i < count; i++)
            {
                int k = i; var c = Plate(parent, source, "EconomyOverhaul " + name + " choice " + i, x, y - Step * (i + 1), share, () => pick(k)); if (c.plate == null) return null;
                made.Add(c.plate); c.plate.SetActive(false); d.choices.Add(c);
            }
            return d;
        }
        private static (GameObject plate, TextMesh text, Material material, LoanAction button) Plate(Transform parent, Transform source, string name, float x, float y, float share, Action action)
        {
            var go = OfficeUiParts.Clone(source.gameObject, parent, false); go.name = name;
            go.transform.localPosition = new Vector3(x, y, source.localPosition.z);
            var bg = go.transform.Find("bg+trigger"); var text = go.transform.Find("text") ? go.transform.Find("text").GetComponent<TextMesh>() : null;
            var renderer = bg ? bg.GetComponent<MeshRenderer>() : null;
            if (!bg || !text || !renderer || !bg.GetComponent<Collider>()) { UnityEngine.Object.Destroy(go); return default; }
            // Narrower along the plate's own width (the axis of bg+trigger that lies along the plate's x), its text fitted inside it.
            Vector3 s = bg.localScale; int axis = 0; float most = -1;
            for (int a = 0; a < 3; a++) { var dir = bg.localRotation * (a == 0 ? Vector3.right : a == 1 ? Vector3.up : Vector3.forward); if (Mathf.Abs(dir.x) > most) { most = Mathf.Abs(dir.x); axis = a; } }
            s[axis] *= share; bg.localScale = s;
            var mesh = bg.GetComponent<MeshFilter>() ? bg.GetComponent<MeshFilter>().sharedMesh : null;
            float width = mesh ? mesh.bounds.size[axis] * s[axis] : 0;
            // its plate's middle at x (the back button's plate may stand off its pivot)
            if (mesh) { var p = go.transform.localPosition; p.x += x - BookUI.InFrameBounds(renderer, parent).center.x; go.transform.localPosition = p; }
            var material = new Material(renderer.sharedMaterial); renderer.sharedMaterial = material;
            var button = OfficeUiParts.Action(bg.gameObject, () => { OfficeUiParts.Click(); action(); }, ""); button.ownedMaterial = material;
            text.color = Color.black; text.text = "";
            if (width > 0) { var fit = text.GetComponent<LoanTextFit>() ?? text.gameObject.AddComponent<LoanTextFit>(); fit.originalScale = text.transform.localScale; fit.maxWidth = width * .9f; }
            return (go, text, material, button);
        }
        private void Toggle(bool sort)
        {
            var d = sort ? SortDrop : RegionDrop; bool open = !d.open;
            Close(); d.open = open; Refresh();
        }
        private void Pick(bool sort, int i)
        {
            if (sort) Board.Sort = (Rules.BoardSort)i; else Board.Region = i - 1;
            Board.Changed(); Close(); Refresh();
            if (ui && Fields.Get<PortDude>(ui, "currentPortDude")) ui.ResetPage();
        }
        internal void Close() { if (SortDrop != null) SortDrop.open = false; if (RegionDrop != null) RegionDrop.open = false; }
        internal void Refresh()
        {
            if (SortDrop == null || RegionDrop == null) return;
            SortDrop.headText.text = "Sort: " + Rules.BoardSortNames[(int)Board.Sort];
            RegionDrop.headText.text = "Region: " + Rules.BoardRegionNames[Board.Region + 1];
            Show(SortDrop, (int)Board.Sort, Rules.BoardSortNames); Show(RegionDrop, Board.Region + 1, Rules.BoardRegionNames);
            // the sort's choices fall over the region's plate: it hides while they show
            RegionDrop.head.SetActive(!SortDrop.open);
        }
        private static void Show(Drop d, int chosen, string[] names)
        {
            for (int i = 0; i < d.choices.Count; i++)
            {
                var c = d.choices[i]; c.text.text = names[i];
                if (c.material && c.material.HasProperty("_Color")) c.material.color = i == chosen ? Color.white : new Color(Shade, Shade, Shade, 1);
                if (c.plate.activeSelf != d.open) c.plate.SetActive(d.open);
            }
        }
        // For tests: a choice clicked as the player clicks it.
        internal void Click(bool sort, int i) { Toggle(sort); (sort ? SortDrop : RegionDrop).choices[i].button.OnActivate(); }
    }
    [HarmonyPatch(typeof(MissionListUI), "EnablePortMissionUI")] internal static class BoardOpenPatch
    { static void Postfix(MissionListUI __instance) => BoardUI.Opened(__instance); }

    // The quantity arrows beside the mission page's cargo count (the user: "Add 2 arrow beside mission UI's cargo quantity, player can click the
    // arrow to decide how many cargo he want to take from that mission"): the list's own page arrows (left, fewer; right, more), at half their
    // size, either side of the count, the count moved right by an arrow's width so the left one stands where the count stood. Shown only on an
    // offer whose crates can be chosen (Missions.CanChoose); a click changes the offer and the page shows it at once (its texts and lines).
    internal static class QuantityArrows
    {
        internal const float Size = .5f, Gap = .012f, LabelGap = .02f;
        // shortcut: `failed` stays set for the rest of the game session (arrows not made once are never tried again, even on a new page
        // object); replace with a reset when `built` changes if a page ever fails only for a moment.
        private static MissionDetailsUI built; private static bool failed;
        private static GameObject less, more; private static TextMesh amount, label; private static float amountX; private static float half, lessCentre, moreCentre;
        private static Mission current;
        internal static GameObject Less => less; internal static GameObject More => more;   // for tests
        internal static void Show(MissionDetailsUI page, Mission mission)
        {
            if (failed || !page) return;
            if (built != page && !Build(page)) { failed = true; Plugin.Log.LogWarning("EconomyOverhaul: the mission page's arrows were not made; the cargo count can't be chosen."); return; }
            current = mission; bool on = Missions.CanChoose(mission);
            var p = amount.transform.localPosition;
            if (!on) { p.x = amountX; amount.transform.localPosition = p; less.SetActive(false); more.SetActive(false); return; }
            // +x is the screen's left: the left arrow where the count began, or a gap right of the "Amount:" label where the count began closer to it
            // (mb-2, 2026-11-08: the arrow's plate 2 px into the label at the count's place); the count an arrow's width and a gap to its right,
            // the right arrow after it.
            float from = label ? Mathf.Min(amountX, LabelRight(label, amount.transform.parent) - LabelGap) : amountX;
            float start = from - 2 * half - Gap, width = BookUI.MeasureLine(amount, amount.text);
            p.x = start; amount.transform.localPosition = p;
            Place(less, from - half - lessCentre); Place(more, start - width - Gap - half - moreCentre);
            less.SetActive(true); more.SetActive(true);
        }
        private static void Place(GameObject arrow, float x) { var q = arrow.transform.localPosition; q.x = x; arrow.transform.localPosition = q; }
        // The label's right end on the screen in the count's frame (+x the screen's left, as in the label's own frame): its x less its measured
        // width by its anchor.
        private static float LabelRight(TextMesh text, Transform frame)
        {
            var a = text.anchor; float left = a == TextAnchor.UpperLeft || a == TextAnchor.MiddleLeft || a == TextAnchor.LowerLeft ? 1 : a == TextAnchor.UpperRight || a == TextAnchor.MiddleRight || a == TextAnchor.LowerRight ? 0 : .5f;
            var lp = text.transform.localPosition; lp.x -= BookUI.MeasureLine(text, (text.text ?? "").TrimEnd()) * left;
            return frame.InverseTransformPoint(text.transform.parent.TransformPoint(lp)).x;
        }
        private static bool Build(MissionDetailsUI page)
        {
            amount = Fields.Get<TextMesh>(page, "amount"); var list = MissionListUI.instance;
            var root = Fields.Get<GameObject>(page, "UI"); label = root ? root.GetComponentsInChildren<TextMesh>(true).FirstOrDefault(t => (t.text ?? "").Trim() == "Amount:") : null;
            var pages = list ? Fields.Get<GameObject>(list, "pageButtons") : null;
            var previous = pages ? OfficeUiParts.FindStart(pages.transform, "mission button (previous") : null;
            var next = pages ? OfficeUiParts.FindStart(pages.transform, "mission button (next") : null;
            if (!amount || !previous || !next) return false;
            var parent = amount.transform.parent; amountX = amount.transform.localPosition.x;
            less = Arrow(previous, parent, "EconomyOverhaul fewer crates", -1, out lessCentre);
            more = Arrow(next, parent, "EconomyOverhaul more crates", 1, out moreCentre);
            if (!less || !more) return false;
            built = page; return true;
        }
        // An arrow copied in its world pose and size, then halved, at the count's height; `centre`: its plate's middle from its pivot along x.
        private static GameObject Arrow(Transform source, Transform parent, string name, int delta, out float centre)
        {
            centre = 0;
            var go = OfficeUiParts.Clone(source.gameObject, parent, true); go.name = name;
            go.transform.localScale *= Size;
            var bg = go.transform.Find("bg+trigger"); var renderer = bg ? bg.GetComponent<MeshRenderer>() : null;
            if (!bg || !renderer || !bg.GetComponent<Collider>()) { UnityEngine.Object.Destroy(go); return null; }
            var p = go.transform.localPosition; p.y = amount.transform.localPosition.y; p.z = amount.transform.localPosition.z - .002f; go.transform.localPosition = p;
            var box = BookUI.InFrameBounds(renderer, parent); half = box.extents.x; centre = box.center.x - p.x;
            OfficeUiParts.Action(bg.gameObject, () => Clicked(delta), delta < 0 ? "Fewer" : "More");
            go.SetActive(false);
            return go;
        }
        private static void Clicked(int delta)
        {
            var page = built; if (!page || current == null || !Missions.Choose(current, delta)) return;
            OfficeUiParts.Click();
            Fields.Call(page, "UpdateTexts"); MissionPage.Show(page, current);
        }
        // For tests: an arrow clicked as the player clicks it.
        internal static void Click(int delta) { var a = delta < 0 ? less : more; if (a) a.GetComponentInChildren<LoanAction>(true)?.OnActivate(); }
    }

    // Postal Expansion (1.1.x) pages the list itself and sorts it by the coin number (PostalMissionUi.GetAvailableMissions): its result is put in
    // the board's order and region, its own letters too. Patched by hand at startup only when it is loaded (a soft dependency, loaded before
    // this mod), as the other mods' compatibility patches are (Plugin.cs).
    internal static class PostalBoardCompatibility
    {
        internal static void Install(Harmony harmony)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("PostalExpansion.PostalMissionUi", false)).FirstOrDefault(t => t != null);
            if (type == null) return;
            var method = AccessTools.GetDeclaredMethods(type).FirstOrDefault(x => x.Name == "GetAvailableMissions" && x.ReturnType == typeof(List<Mission>));
            if (method == null) { Plugin.Log?.LogWarning("Postal Expansion board compatibility unavailable: its mission list changed; its list keeps its own order."); return; }
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(PostalBoardCompatibility), nameof(Ordered)));
            Plugin.Log?.LogInfo("Postal Expansion: its mission list takes the board's sort and region.");
        }
        private static void Ordered(List<Mission> __result) { if (__result != null && !Startup.BlocksGameplay) Board.InPlace(__result); }
    }

    // Mission Rework (written for 1.0.3; 1.0.4 in the workspace) handles this method in its own prefix and shows the list there: the
    // offers are cleared before it runs (HarmonyBefore), and page 0 is built here so the count is set (the game fetches the same
    // page anyway).
    [HarmonyPatch(typeof(PortDude), "ActivateMissionListUI")] internal static class OfficeOpenPatch
    {
        [HarmonyBefore("com.nandbrew.missionrework")]
        static void Prefix(PortDude __instance, bool openEconomyUI)
        { if (openEconomyUI) return; Missions.Reset(); if (__instance.GetPort()) __instance.GetPort().GetMissions(0, false); }
    }
    [HarmonyPatch(typeof(IslandMissionOffice), "Update")] internal static class WarehousePatch { static bool Prefix(IslandMissionOffice __instance) { Missions.Warehouse(__instance); return false; } }
    [HarmonyPatch(typeof(IslandMissionOffice), "GenerateMissions")] internal static class GenerateMissionPatch
    { static bool Prefix(IslandMissionOffice __instance, int page, bool world, ref Mission[] __result) { __result = Missions.Generate(__instance, page, world); return false; } }
    [HarmonyPatch(typeof(PlayerMissions), "AcceptMission")] internal static class AcceptMissionPatch
    {
        static bool Prefix(Mission mission)
        {
            // At the limit the game's mission screen already greys Accept out; this only stops other callers.
            if(mission==null||mission.missionIndex>=0)return false;
            return PlayerMissions.missions==null||PlayerMissions.GetMissionCount()<Reputations.MaxMissions(mission.originPort);
        }
        // Only when the game's own method ran: a refused accept (this prefix's, or another mod's) took no mission (the deep review
        // of 2026-11-02, Claude's fix)
        static void Postfix(Mission mission, bool __runOriginal) { if (__runOriginal && mission != null) Missions.Accepted(mission); }
    }
    [HarmonyPatch(typeof(Good), "Deliver")] internal static class DeliveryContextPatch
    {
        static bool Prefix(Good __instance, out Good __state)
        {
            __state = Missions.Delivering;
            if (__instance.GetAssignedMission() == null) return false;
            if (!Missions.DeliveredItems.Add(__instance.GetComponent<SaveablePrefab>().instanceId)) return false;
            Missions.Delivering = __instance; return true;
        }
        static Exception Finalizer(Exception __exception, Good __state) { Missions.Delivering = __state; return __exception; }
    }
    [HarmonyPatch(typeof(Mission), "GetDeliveryPrice")] internal static class MissionGoldPatch
    {
        static void Postfix(Mission __instance, ref int __result)
        { if (__result > 0 && Missions.Delivering && Missions.Delivering.GetAssignedMission() == __instance) __result = Missions.DeliveryGold(__instance, __result, Missions.Delivering.GetComponent<ShipItem>()); }
    }
    [HarmonyPatch(typeof(Mission), "GetDeliveryRep")] internal static class MissionRepPatch
    {
        [HarmonyPriority(Priority.Last)] [HarmonyAfter("com.DogEggz.postalexpansion")]
        static void Postfix(Mission __instance, ref int __result)
        {
            bool delivering = Missions.Delivering && Missions.Delivering.GetAssignedMission() == __instance;
            __result = Missions.DeliveryReputation(__instance, __result, delivering ? Missions.Delivering.GetComponent<ShipItem>() : null);
        }
    }
    [HarmonyPatch(typeof(Mission), "DeliverGood")] internal static class MissionDeliveredPatch { static void Postfix(Mission __instance) => Missions.Delivered(__instance); }

    // ---- Mission reputation: rewards and penalties by account, the acceptance limit ----
    [HarmonyPatch(typeof(Mission),"DeliverGood")] internal static class MissionAccountRewardPatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            foreach (var c in instructions) {
                if (c.Calls(AccessTools.Method(typeof(PlayerReputation),"ChangeReputation"))) {
                    if (count++ == 0) { var load = new CodeInstruction(OpCodes.Ldarg_0); load.MoveLabelsFrom(c); yield return load; c.operand = AccessTools.Method(typeof(Reputations),nameof(Reputations.DeliveryReward)); }
                    else c.operand = AccessTools.Method(typeof(Reputations),nameof(Reputations.SkipDuplicateReward));
                }
                yield return c;
            }
            if (count != 2) throw new InvalidOperationException("Mission reputation reward hook changed");
        }
    }
    [HarmonyPatch(typeof(Mission),"EndMission")] internal static class MissionAccountPenaltyPatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            foreach (var c in instructions) {
                if (c.Calls(AccessTools.Method(typeof(PlayerReputation),"ChangeReputation"))) { var load = new CodeInstruction(OpCodes.Ldarg_0); load.MoveLabelsFrom(c); yield return load; c.operand = AccessTools.Method(typeof(Reputations),nameof(Reputations.MissionPenalty)); count++; }
                yield return c;
            }
            if (count != 1) throw new InvalidOperationException("Mission reputation penalty hook changed");
        }
    }
    [HarmonyPatch(typeof(MissionDetailsUI),"UpdateTexts")] internal static class MissionAccountLimitPatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList(); int count = 0;
            for (int i = 0; i + 5 < code.Count; i++) if (code[i].LoadsField(AccessTools.Field(typeof(PlayerReputation),"maxMissions")) && code[i+4].LoadsField(AccessTools.Field(typeof(Port),"region")) && code[i+5].opcode == OpCodes.Ldelem_I4) {
                code[i].opcode=OpCodes.Nop;code[i].operand=null;code[i+4].opcode=OpCodes.Nop;code[i+4].operand=null;
                code[i+5].opcode=OpCodes.Call;code[i+5].operand=AccessTools.Method(typeof(Reputations),nameof(Reputations.MaxMissions));count++;
            }
            if(count!=1)throw new InvalidOperationException("Mission acceptance limit hook changed"); return code;
        }
    }

    // Mission crates are placed as trade-book cargo is (the user, 2026-10-16): all of a mission's crates made at once at the game's
    // own spots (Port.DoSpawnGoods: 1 m apart along the port's forward, a new row up after 7) and registered with the mission as
    // the game does, then the whole order placed in the market's warehouse box by the trade book's rules (CargoPlacement.Place:
    // as faced, else a quarter turn either way; crate by crate at the rough ports). An order that finds no room stays at the
    // game's spots.
    [HarmonyPatch(typeof(Port), "SpawnGoods")] internal static class MissionSpawnPatch
    {
        static bool Prefix(Port __instance, Mission mission)
        {
            var market = World.Ready ? World.Market(__instance.portIndex) : null;
            if (!market || mission == null || !mission.goodPrefab || mission.goodCount <= 0) return true;
            var t = __instance.transform; var goods = new List<Good>(); Vector3 offset = Vector3.zero; int count = 0;
            for (int i = 0; i < mission.goodCount; i++)
            {
                var item = UnityEngine.Object.Instantiate(mission.goodPrefab, t.position + offset, t.rotation);
                mission.RegisterGood(item);
                offset += t.forward;
                if (++count > 6) { offset += new Vector3(0, 1, -count); count = 0; }
                var good = item.GetComponent<Good>(); if (good) goods.Add(good);
            }
            try
            {
                if (!CargoPlacement.Place(market, goods, out int found))
                    Plugin.Log?.LogInfo($"Mission crates at port {__instance.portIndex}: room for {found} of {goods.Count}; left at the game's spots.");
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"Mission crates at port {__instance.portIndex}: placement failed, left at the game's spots. {e.Message}"); }
            return false;
        }
    }}

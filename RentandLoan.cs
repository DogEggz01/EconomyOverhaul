using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EconomyOverhaul
{
    // ---- Rules ----
    public static partial class Rules
    {
        internal static double NormalizePenaltyResidue(double value) => value < 0 && value >= -1e-8 ? 0 : value;
        internal static int SplitPenalty(double penalty, out double remainder)
        {
            int whole = checked((int)Math.Floor(penalty + 1e-8));
            remainder = NormalizePenaltyResidue(penalty - whole);
            return whole;
        }
        internal static int RepaymentPenalty(double penalty, int reputation, out double remainder)
        {
            if (double.IsNaN(penalty) || penalty < 0) throw new ArgumentOutOfRangeException(nameof(penalty));
            // Tiny positive rates may produce infinite pressure, but reputation
            // cannot fall below zero. Saturate before any integer conversion.
            if (penalty >= reputation) { remainder = 0; return Math.Max(0, reputation); }
            return SplitPenalty(penalty, out remainder);
        }
        public static double CreditLimit(int level)
        {
            double[] limits = { 0, 10000, 20000, 40000, 60000, 80000, 100000, 200000, 400000 };
            return level >= 9 ? double.PositiveInfinity : limits[Math.Max(0, level)];
        }
        // Respondentia interest by the lending region's level: 90% at level 1, 10 points less per level to 10% at level 9, 5% at 10.
        public static double Interest(int level) => level >= 10 ? .05 : (10 - Clamp(level, 1, 9)) / 10;
        public static Settlement Settle(double debt, int proceeds, double lenderRate, double saleRate)
        {
            if (!(lenderRate > 0) || !(saleRate > 0)) throw new ArgumentOutOfRangeException(nameof(lenderRate));
            if (lenderRate == saleRate) return new Settlement {
                Payout = (int)Math.Floor(Math.Max(0, proceeds - debt)), Shortfall = (int)Math.Ceiling(Math.Max(0, debt - proceeds)) };
            double debtInSaleCoins = debt * saleRate / lenderRate;
            return new Settlement {
                Payout = proceeds >= debtInSaleCoins ? (int)Math.Floor(proceeds - debtInSaleCoins + 1e-8) : 0,
                Shortfall = proceeds < debtInSaleCoins ? (int)Math.Ceiling(debt - proceeds * lenderRate / saleRate - 1e-8) : 0
            };
        }
        public static double NewlyUnpaid(int before, int after, double rate) => Math.Max(0, Math.Max(0, -(double)after) - Math.Max(0, -(double)before)) / rate;
        // Levels a lending region loses when a ship carrying its loans sinks.
        public const int SinkingLevels = 2;
        // Bottomry terms by the lending region's reputation level: flat interest for the loan, and days to repay.
        public static double BottomryRate(int level) => new[] { 0, .40, .40, .40, .35, .30, .25, .20, .15, .10, .05 }[Math.Max(0, Math.Min(10, level))];
        public static int BottomryTerm(int level) => level < 1 ? 0 : level <= 3 ? 20 : level <= 6 ? 25 : 30;
        // Each day past the due day raises the rate by 10% of itself, up to double on day 10.
        public static double BottomryLateRate(double baseRate, int daysLate) => baseRate * (1 + .1 * Math.Max(0, Math.Min(10, daysLate)));
        public static int BottomryOwed(int principal, double rate) => Round(principal * (1 + rate));
        public static int BottomryRemote(int principal, double rate) => Round(principal * (1 + 2 * rate));
        // Repaying at an exchange box outside the lending region adds a flat 10 points to the interest rate.
        public const double BottomryBoxExtra = .10;
        public static int BottomryBoxOwed(int principal, double rate) => Round(principal * (1 + rate + BottomryBoxExtra));

        // Renting needs reputation level 3 in the ship's home region; rent is taken every 20 days.
        public const int RentalLevel = 3, RentalPeriod = 20;
        // A hull counts as clean when this share of its dirt texture is clean (a careful hand scrub passes).
        public const double RentalCleanShare = .95;
        // The ship's value in the rental currency: her price in Gold Lions, at the daily rate.
        public static double RentalValue(int price, double goldRate, double localRate) => price / goldRate * localRate;
        public static int RentalDeposit(double value) => Round(.20 * value);
        public static int RentalDaily(double value) => Round(.05 * value);
        // Days billed when the rental ends: those since the start not covered by a 20-day charge; a rental never charged costs at least one.
        public static int RentalDays(int startDay, int day, int charges)
        {
            int days = day - startDay - RentalPeriod * charges;
            return charges == 0 ? Math.Max(1, days) : Math.Max(0, days);
        }
        public static int NextRentDay(int startDay, int charges) => startDay + RentalPeriod * (charges + 1);
        // The game's shipyard prices (Shipyard.UpdateOrder), in single precision as the game computes them.
        public static int ShipyardBase(int price) => (int)Math.Round((float)price * 3.5f);
        public static int CleaningFee(int price, float rate) => (int)Math.Round((float)((float)ShipyardBase(price) * rate) * 2.25f);
        public static int RepairFee(int price, float rate, float hullDamage, float capacity) =>
            (int)Math.Round((float)((float)((float)((float)ShipyardBase(price) * rate) * hullDamage) * capacity) * .1f);
    }
    public struct Settlement { public int Payout, Shortfall; }

    // ---- Loans ----
    internal static class Loans
    {
        internal static int Currency(CargoState loan) => loan.goldLoan ? 3 : loan.region;
        // Coins per unit of value at the daily rate (credit, reservations, shortfalls): booth exchanges don't move it.
        internal static double Rate(int region) => Fx.State.smooth[region];
        internal static double Reserved(int region) => World.State.cargo.Where(c => c.loan && c.region == region).Sum(c => c.reservation);
        internal static double Limit(int region) => Rules.CreditLimit(Reputations.Level(region));
        internal static double Available(int region) => Math.Max(0, Limit(region) - Reserved(region));
        internal static CargoState Find(ShipItem item)
        {
            var save = item ? item.GetComponent<SaveablePrefab>() : null;
            return save && World.Cargo.TryGetValue(save.instanceId, out var c) && c.loan ? c : null;
        }
        internal static string BlockReason(int region, int currency = -1)
        {
            if (!World.Ready || World.SaveBlocked) return "Economy unavailable";
            if (PlayerGold.currency[currency < 0 ? region : currency] < 0) return "Negative balance";
            return LoanGate.Reason(region) ?? (Available(region) <= 0 ? "No available credit" : null);
        }
        internal static void Notify(string text) { if (NotificationUi.instance) NotificationUi.instance.ShowNotification(NoticeText.Wrap(text)); }
        // Repayment shortfalls can take an account down to 0 reputation points, never below.
        internal static int DebtPenalty(double penalty, int region, out double remainder) =>
            Rules.RepaymentPenalty(penalty, Math.Max(0, Reputations.Points(region)), out remainder);
        internal static void Debit(int region, int amount, int currency = -1, TransactionCategory category = TransactionCategory.other)
        {
            if (amount <= 0) return;
            if (currency < 0) currency = region;
            int before = PlayerGold.currency[currency];
            int after = checked(before - amount);
            double penalty = Rules.NewlyUnpaid(before, after, Rate(currency)) * .5 + World.State.fractionalPenalty[region];
            int whole = DebtPenalty(penalty, region, out double remainder);
            // All arithmetic and reputation validation precedes the wallet write.
            PlayerGold.currency[currency] = after;
            World.State.fractionalPenalty[region] = remainder;
            if (whole > 0) Reputations.Change(-whole, region);
            if (Reputations.Points(region) <= 0) World.State.fractionalPenalty[region] = 0;
            if (DayLogs.instance) DayLogs.instance.dayLogs[currency].LogTransaction(-amount, category);
            if (MoneyNotification.instance) MoneyNotification.instance.PlayNotif(-amount, currency);
        }
        internal static void Close(CargoState c)
        {
            if (c == null || !c.loan) return;
            c.loan = false; // Reservations are derived solely from open records: release exactly once.
            if (!World.State.cargo.Any(x => x.loan && x.region == c.region))
            {
                double remainder = World.State.fractionalPenalty[c.region];
                World.State.fractionalPenalty[c.region] = 0;
                int final = DebtPenalty(Math.Floor(remainder + .5 + 1e-8), c.region, out _);
                if (final > 0) Reputations.Change(-final, c.region);
            }
        }
        internal static int SettleSale(ShipItem item, int gross, int currency)
        {
            var loan = Find(item); if (loan == null) return gross;
            var result = Rules.Settle((double)loan.principal + loan.interest, gross, Rate(Currency(loan)), Rate(currency));
            Debit(loan.region, result.Shortfall, Currency(loan)); Close(loan); return result.Payout;
        }
        internal static bool Use(ShipItem item)
        {
            var loan = Find(item); if (loan == null) return true;
            if (PlayerGold.currency[Currency(loan)] < 0) { Notify("Cannot use financed cargo while its lender-currency balance is negative."); return false; }
            Collect(loan); return true;
        }
        internal static void Collect(CargoState loan)
        { if (loan == null || !loan.loan) return; Debit(loan.region, checked(loan.principal + loan.interest), Currency(loan)); Close(loan); }
        internal static void Cleanup(ShipItem item) { Collect(Find(item)); item.DestroyItem(); }
        internal static void Sacrifice(ShipItem item) { Collect(Find(item)); item.DestroyItem(); }
        internal static void UnexpectedRemoval(ShipItem item)
        {
            CartDelivery.Detach(item);
            // Unity does not guarantee trigger-exit callbacks on destruction. Keep the native
            // warehouse's physical list/count consistent for bankruptcy, cleanup and caching.
            var good = item.GetComponent<Good>();
            if (good && Port.ports != null)
            {
                int g = PrefabsDirectory.ItemToGoodIndex(item.GetComponent<SaveablePrefab>().prefabIndex);
                foreach (var port in Port.ports)
                {
                    if (!port) continue; var m = port.GetComponent<IslandMarket>(); if (!m) continue;
                    var area = m.GetWarehouseArea(); if (!area) continue;
                    var list = Fields.Get<List<Good>>(area, "goodsInArea");
                    if (list != null && list.Remove(good) && g >= 0 && g < m.currentPlayerGoods.Length) m.currentPlayerGoods[g] = Math.Max(0, m.currentPlayerGoods[g] - 1);
                }
            }
            if (!World.Ready || World.Loading || GameState.currentlyLoading) return;
            var save = item.GetComponent<SaveablePrefab>(); if (!save) return;
            // Both normal boat caching and sinking cache reconstruction destroy their live instances.
            if (save.GetParentObject() == -2 || save.GetParentObject() == -3) return;
            var loan = Find(item);
            if (plundered != null && loan != null && loan.loan) { plundered.Add(loan); return; }
            Close(loan);
        }
        // Dangerous Waters' boarding party carrying off financed cargo (DangerousWatersCompatibility): what one boarding
        // takes counts as cargo lost with a sinking, once for the boarding.
        private static List<CargoState> plundered;
        internal static void BeginPlunder() { plundered = new List<CargoState>(); }
        internal static void EndPlunder()
        {
            var lost = plundered; plundered = null;
            if (lost != null && lost.Count > 0 && World.Ready) SinkLoans(lost.Distinct().ToArray());
        }
        private static void MarkRemoved(CargoState c)
        {
            Close(c);
            if (World.Removed.Add(c.id)) World.State.removedCargo.Add(c.id);
        }
        // Why bankruptcy can't be declared there now (null: it can): not in the region of the ship the player rents, nor in all
        // regions, until she is returned.
        internal static string BankruptcyRefusal(int region)
        {
            var r = Rentals.Current;
            return r != null && (region < 0 || r.region == region) ? "Return the " + Rentals.Name(r.ship) + " first" : null;
        }
        internal static void Bankruptcy(int region)
        {
            if (!World.Ready || World.SaveBlocked) return;
            var refusal = BankruptcyRefusal(region); if (refusal != null) { Notify(refusal); return; }
            Rentals.Bankruptcy(region);
            Bottomry.Bankruptcy(region);
            for (int r = 0; r < 4; r++) if (region < 0 || r == region) World.State.fractionalPenalty[r] = 0;
            foreach (var c in World.State.cargo.Where(c => c.loan && (region < 0 || c.region == region)).ToArray()) MarkRemoved(c);
            for (int r = 0; r < 4; r++) if (region < 0 || r == region)
            {
                PlayerGold.currency[r] = 0;
                Reputations.Change(-Reputations.Points(r), r);
                World.State.fractionalPenalty[r] = 0;
            }
            // Each lender owns exactly one wallet, including Chronos / Gold.
            PurgeRemoved();
            Notify(region < 0 ? "Declared bankruptcy in all regions." : "Declared bankruptcy in " + BookUI.RegionName(region) + ".");
            if (EconomyUI.instance) EconomyUI.instance.RefreshPage();
        }
        internal static void Sinking(BoatLocalItems boat)
        {
            if (!World.Ready) return;
            int parent = boat.GetComponent<SaveableObject>().sceneIndex;
            foreach (var active in CargoCondition.Active) active.CaptureParent();
            var affected = World.State.cargo.Where(c => c.loan && c.parent == parent).ToArray();
            SinkLoans(affected);
        }
        // Financed cargo lost with its ship: each lending region drops 2 levels (to the start of the level) and
        // the shared 7-day loan pause starts at the moment of sinking.
        internal static void SinkLoans(CargoState[] affected)
        {
            if (affected.Length == 0) return;
            var levels = Reputations.Levels(affected.Select(c => c.region));
            foreach (var c in affected) MarkRemoved(c);
            Reputations.Sink(levels);
            LoanGate.StartPause();
        }
        internal static void PurgeRemoved()
        {
            if (!SaveLoadManager.instance) return;
            foreach (var save in SaveLoadManager.instance.GetCurrentPrefabs().ToArray())
                if (save && World.Removed.Contains(save.instanceId)) save.GetComponent<ShipItem>().DestroyItem();
            foreach (var obj in SaveLoadManager.instance.GetCurrentObjects())
                if (obj && obj.localItems && obj.localItems.GetCachedItems() != null)
                    obj.localItems.GetCachedItems().RemoveAll(d => World.Removed.Contains(d.instanceId));
            CargoRecords.Prune();
        }
        internal static Good Next(IslandMarket m, int g)
        {
            var ui=EconomyUI.instance;
            var controls=ui?ui.GetComponent<BulkTradeUI>():null;
            if(controls&&controls.CartSelected&&Fields.Get<IslandMarket>(ui,"currentIsland")==m) {
                var carrier=CartDelivery.Hired(m,out _);
                return carrier?BulkTrade.SaleGoods(m,g,carrier).FirstOrDefault():null;
            }
            return BulkTrade.SaleGoods(m,g).FirstOrDefault();
        }
        // Fee-adjusted sale proceeds, before repayment of any attached loan.
        internal static int SaleProceedsQuote(EconomyUI ui, IslandMarket market, int goodIndex) =>
            (int)Fields.Call(ui, "GetSellPrice", market.GetPortIndex(), goodIndex);
        internal static void Sell(EconomyUI ui) => BulkTrade.Execute(ui, TradeAction.Sell, BulkTrade.Quantity(Fields.Get<EconomyUIButton>(ui,"sellButton")));
        internal static IEnumerable<CodeInstruction> ReplaceDestroy(IEnumerable<CodeInstruction> code, string method)
        {
            int count = 0;
            foreach (var c in code)
            {
                if (c.Calls(AccessTools.Method(typeof(ShipItem), nameof(ShipItem.DestroyItem))))
                { c.opcode = OpCodes.Call; c.operand = AccessTools.Method(typeof(Loans), method); count++; }
                yield return c;
            }
            if (count != 1) throw new InvalidOperationException("Cargo removal hook does not match this game version: " + method);
        }
        internal static int PositiveFee(float value) => Mathf.RoundToInt(Mathf.Max(0, value));
    }
    [HarmonyPatch(typeof(EconomyUI), "SellGood")] internal static class WarehouseSalePatch { static bool Prefix(EconomyUI __instance) { Loans.Sell(__instance); return false; } }
    [HarmonyPatch(typeof(ShipItem), "DestroyItem")] internal static class RemovalFallbackPatch { static void Prefix(ShipItem __instance) => Loans.UnexpectedRemoval(__instance); }
    [HarmonyPatch(typeof(ItemRigidbody), "FixedUpdate")] internal static class CleanupDebtPatch
    { static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => Loans.ReplaceDestroy(instructions, nameof(Loans.Cleanup)); }
    [HarmonyPatch(typeof(KiciaAltar), "OnTriggerEnter")] internal static class SacrificeDebtPatch
    {
        static bool Prefix(Collider other)
        {
            var item = other.GetComponent<ShipItem>(); var c = Loans.Find(item);
            if (c == null || PlayerGold.currency[Loans.Currency(c)] >= 0) return true;
            Loans.Notify("Cannot sacrifice financed cargo while its lender-currency balance is negative."); return false;
        }
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => Loans.ReplaceDestroy(instructions, nameof(Loans.Sacrifice));
    }
    [HarmonyPatch(typeof(ShipItemCrate), "UnsealCrate")] internal static class UnsealDebtPatch
    { static bool Prefix(ShipItemCrate __instance) => __instance.amount <= 0 || !__instance.GetComponent<CrateInventory>() || Loans.Use(__instance); }
    [HarmonyPatch(typeof(ShipItemBottle), "Drink")] internal static class DrinkDebtPatch
    { static bool Prefix(ShipItemBottle __instance) => __instance.health <= 0 || Loans.Use(__instance); }
    [HarmonyPatch(typeof(ShipItemSalt), "SaltFood")] internal static class SaltDebtPatch
    { static bool Prefix(ShipItemSalt __instance, FoodState food) => __instance.amount <= 0 || food.GetRequiredSalt() <= 0 || Loans.Use(__instance); }
    [HarmonyPatch(typeof(ShipItemBottle), "OnItemClick")] internal static class PourDebtPatch
    {
        static bool Prefix(ShipItemBottle __instance, PickupableItem heldItem, ref bool __result)
        {
            var other = heldItem as ShipItemBottle;
            var good = __instance.GetComponent<Good>();
            if (!__instance.sold || (good && good.GetMissionIndex() >= 0)) return true;
            ShipItemBottle source = __instance, target = other;
            if (other)
            {
                var otherGood = other.GetComponent<Good>();
                if (!other.sold || (otherGood && otherGood.GetMissionIndex() >= 0)) return true;
                if (other.GetCapacity() > __instance.GetCapacity()) { source = other; target = __instance; }
                if (source.health <= 0 || target.health >= target.GetCapacity() || (target.health > 0 && target.amount != source.amount)) return true;
            }
            else if (!(heldItem is ShipItemSoup) || __instance.amount != 1 || __instance.health <= 0) return true;
            if (Loans.Use(source)) return true;
            __result = false; return false;
        }
    }
    [HarmonyPatch(typeof(BoatLocalItems), "CacheItemsOnSinking")] internal static class SinkingDebtPatch
    { static void Prefix(BoatLocalItems __instance) => Loans.Sinking(__instance); static void Postfix() => Loans.PurgeRemoved(); }
    [HarmonyPatch(typeof(BoatLocalItems), "DestroySunkCargo")] internal static class RemoveFinancedSunkPatch
    { static bool Prefix(SaveablePrefab prefab, ref bool __result) { if (!World.Removed.Contains(prefab.instanceId)) return true; __result = true; return false; } }
    [HarmonyPatch] internal static class RecoveryFeePatch
    {
        static MethodBase TargetMethod() => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(Recovery), "DoRecoverPlayer"));
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            foreach (var c in instructions)
            {
                if (c.Calls(AccessTools.Method(typeof(Mathf), nameof(Mathf.RoundToInt)))) { c.operand = AccessTools.Method(typeof(Loans), nameof(Loans.PositiveFee)); count++; }
                yield return c;
            }
            if (count != 1) throw new InvalidOperationException("Recovery fee hook does not match this game version");
        }
    }

    [HarmonyPatch(typeof(ShipItemOakum), "OnAltActivate", new Type[0])] internal static class OakumLoanPatch
    {
        static bool Prefix(ShipItemOakum __instance)
        {
            if (!__instance.sold || !GameState.currentBoat || __instance.amount <= .01f) return true;
            var damage = GameState.currentBoat.parent.GetComponent<BoatDamage>();
            return !damage || damage.hullDamage * damage.waterUnitsCapacity - damage.oakum <= .01f || Loans.Use(__instance);
        }
    }
    [HarmonyPatch(typeof(CrateSealUI), "ShowUI")] internal static class SealPromptPatch
    {
        private static TextMesh prompt;
        static void Postfix(CrateSealUI __instance, ShipItemCrate crate)
        {
            if (!prompt)
            {
                var source = __instance.GetComponentInChildren<TextMesh>(true);
                if (!source) return;
                prompt = UnityEngine.Object.Instantiate(source, source.transform.parent);
                prompt.name = "Respondentia repayment prompt";
                prompt.transform.localPosition += Vector3.down * .085f;
                prompt.characterSize *= .8f;
            }
            var c = Loans.Find(crate);
            prompt.gameObject.SetActive(c != null);
            if (c != null) prompt.text = "Opening repays " + (c.principal + c.interest).ToString("N0") + " " + PlayerGold.GetCurrencyName(Loans.Currency(c));
        }
    }

    // ---- Loan gate ----
    // What stops new loans of both kinds: the 7-day pause after a ship sinks with loans aboard (every region), and a
    // Bottomry default: the lending region lends nothing while the currency the default left below 0 stays there.
    internal static class LoanGate
    {
        internal const double PauseHours = 7 * 24;
        internal static bool Paused => World.State.loanPauseUntil > World.CalendarHour;
        internal static int PauseDays => (int)Math.Ceiling((World.State.loanPauseUntil - World.CalendarHour) / 24 - 1e-9);
        internal static void StartPause() => World.State.loanPauseUntil = Math.Max(World.State.loanPauseUntil, World.CalendarHour + PauseHours);
        // A default is over once its currency is back to 0 or more (checked every frame, so a later shortfall can't revive it).
        internal static void EndDefaults() { if (PlayerGold.currency != null) World.State.defaults.RemoveAll(d => PlayerGold.currency[d.currency] >= 0); }
        // The default still closing region r.
        internal static DefaultBlock Default(int region) { EndDefaults(); return World.State.defaults.FirstOrDefault(d => d.region == region); }
        // Why no new loan may be taken in region r (null: allowed, subject to each kind's own money and credit rules).
        internal static string Reason(int region)
        {
            if (Paused) return "Loans paused: " + Days(PauseDays) + " left (a ship sank)";
            var block = Default(region); if (block != null) return "No loans while your " + PlayerGold.GetCurrencyName(block.currency) + " are negative";
            return Reputations.Level(region) < 1 ? "Requires reputation level 1" : null;
        }
        // The same, short enough for the ledger tables.
        internal static string Status(int region)
        {
            if (Paused) return "Paused " + Days(PauseDays);
            var block = Default(region); if (block != null) return Rentals.Coin(block.currency) + " negative";
            return Reputations.Level(region) < 1 ? "Needs reputation 1" : null;
        }
        internal static string Days(int days) => days + (days == 1 ? " day" : " days");
    }

    // ---- Bottomry ----
    // A loan on a ship or house: twice its price, at a flat rate fixed when taken, repaid in the currency it was paid out in.
    [Serializable] public sealed class BottomryLoan
    {
        public int id, pledge, region, currency, principal, startDay, dueDay;
        public double baseRate;
    }
    // A pledge a lender took from the player, listed until the player buys her back: the game saves the place, rig and damage
    // of owned ships only, so she counts as owned while a save is written.
    [Serializable] public sealed class SeizedPledge
    {
        public int pledge;
    }
    // A Bottomry default left this currency below 0: the lending region lends nothing until it is back to 0 or more.
    [Serializable] public sealed class DefaultBlock
    {
        public int region, currency;
    }
    // The exchange box the player opened: its port, its island and the region that lends there.
    internal sealed class LoanBox
    {
        internal Port port; internal Transform island; internal int region;
        internal string PortName => port ? port.GetPortName() : "this port";
        internal static LoanBox Of(Component chest, int region)
        {
            var dude = chest ? chest.GetComponentInParent<PortDude>() : null;
            var port = dude && Port.ports != null ? Port.ports.FirstOrDefault(p => p && p.portIndex != 7 && p.GetDude() == dude) : null;
            if (!port && chest) port = chest.GetComponentInParent<Port>();
            return port ? At(port) : new LoanBox { island = Pledges.Island(chest), region = region };
        }
        internal static LoanBox At(Port port) => new LoanBox { port = port, island = Pledges.Island(port), region = Reputations.Account(port) };
    }

    // Ships and houses: every PurchasableBoat in the scene, vanilla or added by another mod.
    internal static class Pledges
    {
        // A berth: her place across the water relative to her nearest island, and her own height (an island's height follows
        // the horizon sinking, IslandHorizon); with no island found, her place without the world's shifts.
        private sealed class BerthPose { internal Transform island; internal Vector3 offset, real; internal Quaternion rotation; }
        private static readonly Dictionary<PurchasableBoat, BerthPose> berths = new Dictionary<PurchasableBoat, BerthPose>();
        private static PurchasableBoat[] cache; private static int cacheFrame = -1;
        // From the save manager's list, as the game's own recovery searches: while playing, the game switches off ships more
        // than 10 km from the player, and a scene search skips switched-off objects.
        internal static PurchasableBoat[] All()
        {
            if (cache != null && cacheFrame == Time.frameCount) return cache;
            cacheFrame = Time.frameCount;
            var objects = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentObjects() : null;
            return cache = objects == null ? new PurchasableBoat[0] :
                objects.Where(o => o && o.sceneIndex > 0).Select(o => o.GetComponent<PurchasableBoat>()).Where(b => b).ToArray();
        }
        internal static PurchasableBoat Find(int index) => All().FirstOrDefault(b => Index(b) == index);
        internal static int Index(PurchasableBoat b) => b.GetComponent<SaveableObject>().sceneIndex;
        internal static bool IsShip(PurchasableBoat b) => b && !b.isHouse && b.GetComponent<BoatRefs>();
        internal static bool Owned(PurchasableBoat b) => b && b.GetComponent<SaveableObject>().extraSetting;
        internal static bool Sunk(PurchasableBoat b) { var damage = b ? b.GetComponent<BoatDamage>() : null; return damage && damage.sunk; }
        // The region a rented ship was rented in; -1 for every other ship.
        internal static int RentedFrom(PurchasableBoat b) => Rentals.RegionOf(b);
        // Ships the player owns outright (not rented), afloat.
        internal static IEnumerable<PurchasableBoat> OwnShips() => All().Where(b => IsShip(b) && Owned(b) && RentedFrom(b) < 0 && !Sunk(b));
        // Houses carry their port's region; the game has no ship or house names, so vanilla ones get the shipwrights' names.
        internal static int HouseRegion(int index) => index == 201 || index == 205 ? 0 : index == 202 || index == 204 ? 1 : index == 203 || index == 206 ? 2 : -1;
        internal static string Name(int index)
        {
            switch (index)
            {
                case 10: return "Dhow"; case 20: return "Sanbuq"; case 30: return "Baghlah"; case 40: return "Cog"; case 50: return "Brig";
                case 70: return "Jong"; case 80: return "Junk"; case 90: return "Kakam";
                case 201: return "Gold Rock City house"; case 202: return "Dragon Cliffs house"; case 203: return "Fort Aestrin house";
                case 204: return "Serpent Isle house"; case 205: return "Oasis house"; case 206: return "Firefly Grotto house";
            }
            var boat = Find(index); return boat ? ModdedName(boat.name) : "ship " + index;
        }
        // Other mods' ships by their object name: "BOAT GLORIANA (182)(Clone)" reads "Gloriana". The Shattered Seas ships go by
        // the names their own mods use: the Shroud Large is the Clipper, the Shroud Small the Sh'ba.
        internal static string ModdedName(string name)
        {
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\(Clone\)|\(\d+\)", "").Replace("BOAT ", "").Trim();
            if (name == "happybayboat") return "Happy Bay boat";
            if (name == "DNG Cutter") return "Dinghy cutter";
            if (name == "Shroud Large") return "Clipper";
            if (name == "Shroud Small") return "Sh'ba";
            return name.Length > 1 && name.ToUpperInvariant() == name ? name.Substring(0, 1) + name.Substring(1).ToLowerInvariant() : name;
        }
        // The main-scene island (Refs.islands) a component stands on, through island scenery scenes too.
        internal static Transform Island(Component c)
        {
            if (!c || Refs.islands == null) return null;
            var scenery = c.GetComponentInParent<IslandSceneryScene>(); var horizon = c.GetComponentInParent<IslandHorizon>();
            int index = scenery ? scenery.parentIslandIndex : horizon ? horizon.islandIndex : -1;
            return index >= 0 && index < Refs.islands.Length ? Refs.islands[index] : null;
        }
        internal static Port PortOf(Transform island) =>
            island && Port.ports != null ? Port.ports.FirstOrDefault(p => p && p.portIndex != 7 && Island(p) == island) : null;
        internal static bool TiedAt(PurchasableBoat b, Transform island)
        {
            var ropes = b ? b.GetComponent<BoatMooringRopes>() : null;
            return ropes && island && ropes.ropes.Any(r => r && r.IsMoored() && r.transform.parent && r.transform.parent.IsChildOf(island));
        }
        // The port island one of its ropes is tied to (the Hideout and the Onsen have no port), or null.
        internal static Transform TiedPort(PurchasableBoat b)
        {
            var ropes = b ? b.GetComponent<BoatMooringRopes>() : null; if (!ropes) return null;
            foreach (var r in ropes.ropes)
                if (r && r.IsMoored() && r.transform.parent) { var island = Island(r.transform.parent); if (island && PortOf(island)) return island; }
            return null;
        }
        // Ties one of the ship's free ropes to a cleat (the game's own MoorTo); an occupied cleat gives way to the nearest free one
        // on the same island within 30 m.
        internal static bool TieTo(PurchasableBoat b, Transform cleat)
        {
            var ropes = b ? b.GetComponent<BoatMooringRopes>() : null; var button = cleat ? cleat.GetComponent<GPButtonDockMooring>() : null;
            if (!ropes || !button) return false;
            if (button.spring.connectedBody)
            {
                var island = Island(cleat);
                button = island ? island.GetComponentsInChildren<GPButtonDockMooring>().Where(c => c != button && !c.spring.connectedBody && (c.transform.position - cleat.position).sqrMagnitude < 900)
                    .OrderBy(c => (c.transform.position - cleat.position).sqrMagnitude).FirstOrDefault() : null;
                if (!button) return false;
            }
            var rope = ropes.ropes.Where(r => r && !r.IsMoored()).OrderBy(r => (r.transform.position - button.transform.position).sqrMagnitude).FirstOrDefault();
            if (!rope) return false;
            rope.MoorTo(button); return true;
        }
        // A ship sent to her berth is tied to her home cleats only once the player is near her island. Far away, the game
        // switches the island's cleats off and lowers the island (the earth's curve, kilometres down); a rope tied there is
        // lost when a save made meanwhile is loaded (the game puts a saved rope back where it was and re-ties it only to a
        // cleat that is switched on and right there; otherwise the rope end stays behind as the world shifts). Until then she
        // waits at her berth, untied; ships the player is far from are frozen, so she cannot drift.
        private static readonly HashSet<PurchasableBoat> waiting = new HashSet<PurchasableBoat>();
        internal static bool CleatsUp(PurchasableBoat b)
        {
            var r = b ? b.GetComponent<BoatMooringRopes>() : null;
            bool Up(Transform c) => c.gameObject.activeInHierarchy && Mathf.Abs(c.position.y - b.transform.position.y) < 15;
            return r && r.mooringFront && r.mooringBack && Up(r.mooringFront) && Up(r.mooringBack);
        }
        internal static void TieHome(PurchasableBoat b)
        {
            var r = b ? b.GetComponent<BoatMooringRopes>() : null; if (!r || !r.mooringFront || !r.mooringBack) return;
            if (!b.gameObject.activeInHierarchy || !CleatsUp(b)) { waiting.Add(b); return; }
            waiting.Remove(b); TieTo(b, r.mooringFront); TieTo(b, r.mooringBack);
        }
        // Each second: the waiting ships whose island is up are tied; one bought, rented or tied meanwhile leaves the list.
        internal static void TieWaiting()
        {
            foreach (var b in waiting.ToArray())
            {
                var r = b ? b.GetComponent<BoatMooringRopes>() : null;
                if (!r || Owned(b) || r.ropes.Any(x => x && x.IsMoored())) { waiting.Remove(b); continue; }
                if (CleatsUp(b)) TieHome(b);
            }
        }
        // Another ship lies at her berth (within 10 m of it).
        internal static bool BerthTaken(PurchasableBoat b, Vector3 berth) =>
            All().Any(s => s != b && IsShip(s) && (s.transform.position - berth).sqrMagnitude < 100);
        // Ships to move onto their berth later (saved): one sent home to a berth another ship lies at (she stays where she is
        // meanwhile), or a returned rental without home cleats left near it. Each second: once the berth is free and the
        // player is more than 1 km from her and from it, she goes there without a black screen. A ship the player owns
        // again (bought or rented) leaves the list.
        internal static void HomeLater(PurchasableBoat b) { if (b && !World.State.homeLater.Contains(Index(b))) World.State.homeLater.Add(Index(b)); }
        internal static bool GoingHomeLater(PurchasableBoat b) => b && World.State.homeLater.Contains(Index(b));
        internal static void SendHomeLater()
        {
            if (!World.Ready || World.Loading || GameState.currentlyLoading || World.State.homeLater.Count == 0) return;
            var player = Refs.observerMirror ? Refs.observerMirror.transform.position : Vector3.zero;
            foreach (int index in World.State.homeLater.ToArray())
            {
                var b = Find(index);
                if (!b || Owned(b) || !Berth(b, out var berth, out _)) { World.State.homeLater.Remove(index); continue; }
                if (BerthTaken(b, berth) || (berth - player).sqrMagnitude < 1e6f || (b.transform.position - player).sqrMagnitude < 1e6f) continue;
                World.State.homeLater.Remove(index); ForcedMove.Home(b);
            }
        }
        // Where each ship stood before any save loaded: its for-sale berth. Ships other mods add later are recorded when
        // they appear (a seized ship lying elsewhere is not). Kept relative to her nearest island, so the berth moves with
        // it: Scrambled Seas moves each island, and each unsold ship by her nearest island's offset, after this record
        // (a new game, or every load of a scrambled save).
        internal static void RecordBerths()
        {
            var shift = FloatingOriginManager.instance ? FloatingOriginManager.instance.outCurrentOffset : Vector3.zero;
            foreach (var b in All())
                if (IsShip(b) && !Owned(b) && !berths.ContainsKey(b) && World.State.seized.All(s => s.pledge != Index(b)))
                {
                    var p = b.transform.position; var island = NearestIsland(p);
                    berths[b] = new BerthPose { island = island, offset = island ? p - island.position : Vector3.zero, real = p - shift, rotation = b.transform.rotation };
                }
        }
        // The nearest of the game's islands across the water (Refs.islands from 1, the islands Scrambled Seas moves; the
        // same choice as its own nearest-island rule for ships).
        internal static Transform NearestIsland(Vector3 p)
        {
            Transform best = null; float nearest = float.MaxValue;
            if (Refs.islands != null)
                for (int i = 1; i < Refs.islands.Length; i++)
                {
                    var t = Refs.islands[i]; if (!t) continue;
                    float dx = t.position.x - p.x, dz = t.position.z - p.z, d = dx * dx + dz * dz;
                    if (d < nearest) { nearest = d; best = t; }
                }
            return best;
        }
        internal static bool Berth(PurchasableBoat b, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero; rotation = Quaternion.identity;
            if (!b || !berths.TryGetValue(b, out var pose)) return false;
            position = pose.real + (FloatingOriginManager.instance ? FloatingOriginManager.instance.outCurrentOffset : Vector3.zero);
            if (pose.island) { position.x = pose.island.position.x + pose.offset.x; position.z = pose.island.position.z + pose.offset.z; }
            rotation = pose.rotation;
            return true;
        }
        internal static GPButtonPurchaseBoat SaleButton(PurchasableBoat b)
        {
            var sign = b ? Fields.Get<GameObject>(b, "purchaseUI") : null;
            return sign ? sign.GetComponentInChildren<GPButtonPurchaseBoat>(true) : null;
        }
        // The sale sign out for a ship no longer the player's, at her price.
        internal static void ShowSale(PurchasableBoat b)
        {
            var sign = Fields.Get<GameObject>(b, "purchaseUI"); if (sign) sign.SetActive(!Owned(b));
            var button = SaleButton(b); if (button && button.priceText) button.priceText.text = "FOR SALE\n\n" + b.price + " gold";
        }
        // A ship back to her shipwright (taken by a lender, a rental returned): anchor up, no longer the player's, sale sign
        // out again, and no longer the ship the game recovers the player to.
        internal static void Unown(PurchasableBoat b)
        {
            var ropes = b.GetComponent<BoatMooringRopes>();
            if (ropes && ropes.GetAnchorController()) ropes.GetAnchorController().ResetAnchor();
            b.GetComponent<SaveableObject>().extraSetting = false;
            ShowSale(b);
            if (GameState.lastOwnedBoat == b.transform) GameState.lastOwnedBoat = OwnShips().Select(s => s.transform).FirstOrDefault();
        }
    }

    internal static class Bottomry
    {
        internal static bool Valid(BottomryLoan l) => l != null && l.id > 0 && l.pledge > 0 && l.region >= 0 && l.region < 4 && l.currency >= 0 && l.currency < 4 &&
            l.principal > 0 && l.baseRate > 0 && l.baseRate <= 1 && l.dueDay >= l.startDay;
        internal static string Name(int pledge) => Pledges.Name(pledge);
        internal static string Percent(double rate) => (rate * 100).ToString("0.#") + "%";
        internal static int DaysLate(BottomryLoan l) => Math.Max(0, GameState.day - l.dueDay);
        internal static double Rate(BottomryLoan l) => Rules.BottomryLateRate(l.baseRate, DaysLate(l));
        internal static int Owed(BottomryLoan l) => Rules.BottomryOwed(l.principal, Rate(l));
        internal static int RemoteCost(BottomryLoan l) => Rules.BottomryRemote(l.principal, Rate(l));
        // At an exchange box: what is owed in the lending region, 10 more points of interest anywhere else.
        internal static int BoxCost(BottomryLoan l, int boxRegion) => boxRegion == l.region ? Owed(l) : Rules.BottomryBoxOwed(l.principal, Rate(l));
        internal static IEnumerable<BottomryLoan> On(int pledge) => World.State.bottomry.Where(l => l.pledge == pledge);
        internal static bool Pledged(int pledge) => World.State.bottomry.Any(l => l.pledge == pledge);
        internal static SeizedPledge Seized(int pledge) => World.State.seized.FirstOrDefault(s => s.pledge == pledge);
        internal static int LocalCurrency(int region) => region == Reputations.Chronos ? 3 : region;
        // Twice the price in the lending region's own coins, at the daily rate.
        internal static int LocalPrincipal(PurchasableBoat b, int region) =>
            Rules.Round(2.0 * b.price / Fx.State.smooth[3] * Fx.State.smooth[LocalCurrency(region)]);
        // Another currency is paid out as the box would exchange it (its fee), at the posted rate.
        internal static int Payout(int localPrincipal, int region, int currency)
        {
            int local = LocalCurrency(region); if (currency == local) return localPrincipal;
            double fee = FxRules.BoothFee(local, currency, Reputations.Level(region), region == Reputations.Chronos);
            double paid = Math.Floor(localPrincipal * Fx.State.rates[currency] / Fx.State.rates[local] * (1 - fee));
            return paid > int.MaxValue ? int.MaxValue : (int)paid;
        }

        // Why this pledge can't take a loan at this box in this currency (null: it can).
        internal static string Refusal(PurchasableBoat b, LoanBox box, int currency)
        {
            if (!World.Ready || World.SaveBlocked) return "Economy unavailable";
            string region = BookUI.RegionName(box.region);
            if (Reputations.Level(box.region) < 1) return "No loans: reputation 0 in " + region;
            if (LoanGate.Paused) return "Loans paused: " + LoanGate.Days(LoanGate.PauseDays) + " left (a ship sank)";
            var block = LoanGate.Default(box.region); if (block != null) return "No loans in " + region + " while your " + PlayerGold.GetCurrencyName(block.currency) + " are negative";
            if (PlayerGold.currency[currency] < 0) return "No loans while your " + PlayerGold.GetCurrencyName(currency) + " are negative";
            int index = Pledges.Index(b); string name = Name(index);
            if (World.State.burned.Contains(index)) return "The " + name + " can never be pledged again";
            if (World.State.bottomry.Any(l => l.pledge == index && l.region == box.region)) return "Already pledged in " + region;
            if (b.isHouse)
            {
                if (!Pledges.Owned(b)) return "Not your house";
                if (Pledges.HouseRegion(index) != box.region) return "A house is pledged in its own region only";
            }
            else
            {
                int rented = Pledges.RentedFrom(b);
                if (!Pledges.Owned(b)) return "Not your ship";
                if (Pledges.Sunk(b)) return "The " + name + " has sunk";
                if (!Pledges.TiedAt(b, box.island)) return "The " + name + " is not tied here";
                if (rented == box.region) return "Rented from " + region + ": can't be pledged here";
                if (!Pledged(index) && !Pledges.OwnShips().Any(s => s != b && !Pledged(Pledges.Index(s))))
                    return rented >= 0 ? "Keep one ship of your own unpledged" : "You need another free ship";
            }
            if (LocalPrincipal(b, box.region) <= 0) return "The " + name + " is worth nothing to lend on";
            return null;
        }
        internal static BottomryLoan Take(PurchasableBoat b, LoanBox box, int currency)
        {
            Fx.AdvanceDay(GameState.day);
            var refusal = Refusal(b, box, currency); if (refusal != null) { Loans.Notify(refusal); return null; }
            int level = Reputations.Level(box.region), paid = Payout(LocalPrincipal(b, box.region), box.region, currency);
            if (paid <= 0) { Loans.Notify("The " + Name(Pledges.Index(b)) + " is worth nothing to lend on"); return null; }
            if ((long)PlayerGold.currency[currency] + paid > int.MaxValue) { Loans.Notify("Amount exceeds wallet limit"); return null; }
            var loan = new BottomryLoan { id = ++World.State.bottomryNextId, pledge = Pledges.Index(b), region = box.region, currency = currency, principal = paid,
                baseRate = Rules.BottomryRate(level), startDay = GameState.day, dueDay = GameState.day + Rules.BottomryTerm(level) };
            World.State.bottomry.Add(loan);
            Receive(currency, paid);
            return loan;
        }
        // Money the lender pays or receives moves the market like goods trade: money received weakens its currency,
        // money paid back strengthens it, settled at midnight.
        private static void Receive(int currency, int amount)
        {
            PlayerGold.currency[currency] += amount;
            if (DayLogs.instance) DayLogs.instance.dayLogs[currency].LogTransaction(amount, TransactionCategory.other);
            if (MoneyNotification.instance) MoneyNotification.instance.PlayNotif(amount, currency);
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayGoldSound();
            Fx.Record(currency, amount, false, TradeSource.Cargo);
        }
        private static void Pay(int currency, int amount)
        {
            if (amount <= 0) return;
            PlayerGold.currency[currency] -= amount;
            if (DayLogs.instance) DayLogs.instance.dayLogs[currency].LogTransaction(-amount, TransactionCategory.other);
            if (MoneyNotification.instance) MoneyNotification.instance.PlayNotif(-amount, currency);
            Fx.Record(currency, amount, true, TradeSource.Cargo);
        }

        // Repaying from the Log: the whole cost at once, from the loan's currency, or nothing.
        internal static bool RepayRemote(BottomryLoan l)
        {
            if (l == null || !World.Ready || World.SaveBlocked || !World.State.bottomry.Contains(l)) return false;
            int cost = RemoteCost(l); string currency = PlayerGold.GetCurrencyName(l.currency);
            if (PlayerGold.currency[l.currency] < cost) { Loans.Notify("Not enough " + currency + " to repay the " + Name(l.pledge)); return false; }
            Pay(l.currency, cost); World.State.bottomry.Remove(l);
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayGoldSound();
            return true;
        }
        // A pledged ship inside a shipyard's range can't be repaid at a box (it could be stripped there after a seizure).
        internal static bool InShipyard(PurchasableBoat b)
        {
            if (!b || b.isHouse) return false;
            foreach (var yard in Resources.FindObjectsOfTypeAll<Shipyard>())
            {
                if (!yard || !yard.gameObject.activeInHierarchy) continue;
                var inside = Fields.Get<List<GameObject>>(yard, "boatsInTrigger");
                if (inside != null && inside.Contains(b.gameObject)) return true;
            }
            return false;
        }
        // Repaying in person at any exchange box. Short of money: everything in that currency is paid, down to 0, and the rest
        // of that loan is a default; the pledge's other loans run on to their own day 11.
        internal static void RepayAtBox(BottomryLoan l, LoanBox box)
        {
            if (l == null || !World.Ready || World.SaveBlocked || !World.State.bottomry.Contains(l)) return;
            var pledge = Pledges.Find(l.pledge);
            if (InShipyard(pledge)) { Loans.Notify("Move the " + Name(l.pledge) + " out of the shipyard first"); return; }
            int due = BoxCost(l, box.region), have = Math.Max(0, PlayerGold.currency[l.currency]);
            if (have >= due)
            {
                Pay(l.currency, due); World.State.bottomry.Remove(l);
                if (UISoundPlayer.instance) UISoundPlayer.instance.PlayGoldSound();
                return;
            }
            // The part paid clears the same share of the debt; the rest stays owed at the lending region's own terms.
            int rest = Math.Max(0, Rules.Round(Owed(l) * (1 - (double)have / due)));
            Pay(l.currency, have); World.State.bottomry.Remove(l);
            Default(pledge, l, rest);
        }
        // Her price in a currency, at the daily rate.
        internal static int PriceIn(PurchasableBoat b, int currency) => Rules.Round(b.price / Fx.State.smooth[3] * Fx.State.smooth[currency]);
        // A loan (already removed) left unpaid after what its currency paid: the rest, at the lending region's own terms. A
        // rented ship goes home with every loan on her charged (Rentals.TakenByLender). The player's own ship, unless a lender
        // took her before, is taken by this lender and her price counts against the rest (no change given). What is still
        // owed is charged to the loan's currency, even below 0.
        internal static void Default(PurchasableBoat b, BottomryLoan l, int rest)
        {
            if (Rentals.Is(b)) { Rentals.TakenByLender(b, l, rest); return; }
            bool take = b && Pledges.Owned(b) && Seized(l.pledge) == null;
            int left = take ? rest - Math.Min(rest, PriceIn(b, l.currency)) : rest;
            if (take) Take(b, l.pledge);
            Charge(l, left);
            string owed = left > 0 ? left.ToString("N0") + " " + PlayerGold.GetCurrencyName(l.currency) : null;
            if (take) Loans.Notify("The " + BookUI.RegionName(l.region) + " lender took the " + Name(l.pledge) + (owed != null ? ": " + owed + " still owed" : ""));
            else if (owed != null) Loans.Notify("Bottomry on the " + Name(l.pledge) + ": " + owed + " charged");
        }
        // An unpaid loan charged to its currency: the coins there repay it (pressure as any repayment), the rest takes the
        // wallet below 0 with the shortfall's reputation loss in the lending region, which then lends nothing until the
        // currency is back to 0 or more (LoanGate.Default).
        internal static void Charge(BottomryLoan l, int amount)
        {
            if (amount <= 0) return;
            int paid = Math.Min(amount, Math.Max(0, PlayerGold.currency[l.currency]));
            if (paid > 0) Fx.Record(l.currency, paid, true, TradeSource.Cargo);
            Loans.Debit(l.region, amount, l.currency);
            if (PlayerGold.currency[l.currency] < 0 && !World.State.defaults.Any(d => d.region == l.region && d.currency == l.currency))
                World.State.defaults.Add(new DefaultBlock { region = l.region, currency = l.currency });
        }
        // The lender takes the pledge: listed as seized and released where she belongs (ForcedMove.Release).
        private static void Take(PurchasableBoat b, int index)
        {
            if (Seized(index) == null) World.State.seized.Add(new SeizedPledge { pledge = index });
            ForcedMove.Release(b);
        }

        // Daily: reminders, and on day 11 after its due day each loan still open, oldest first, is paid from its currency
        // down to 0 and the rest is a default (one forced move for all).
        internal static void AdvanceDay(int day)
        {
            if (!World.Ready || World.Loading || World.SaveBlocked || Startup.BlocksGameplay || GameState.currentlyLoading) return;
            LoanGate.EndDefaults();
            if (World.State.bottomryDay == 0) World.State.bottomryDay = day;
            bool newDay = day > World.State.bottomryDay; World.State.bottomryDay = Math.Max(World.State.bottomryDay, day);
            if (newDay)
                foreach (var l in World.State.bottomry.OrderBy(x => x.dueDay))
                {
                    int left = l.dueDay - day, late = -left;
                    if (left == 1) Loans.Notify("Bottomry: the " + Name(l.pledge) + " is due tomorrow (" + BookUI.RegionName(l.region) + ")");
                    else if (late >= 1 && late <= 9) Loans.Notify("Bottomry: the " + Name(l.pledge) + " is " + LoanGate.Days(late) + " overdue, interest " + Percent(Rate(l)));
                    else if (late == 10) Loans.Notify("Bottomry: last day before the " + Name(l.pledge) + " is seized");
                }
            if (!GameState.playing || GameState.recovering || ForcedMove.Moving || GameState.currentShipyard) return;
            foreach (var l in World.State.bottomry.Where(x => day >= x.dueDay + 11).OrderBy(x => x.dueDay).ThenBy(x => x.id).ToArray())
            {
                if (!World.State.bottomry.Contains(l)) continue; // charged when an earlier one ended her rental
                int owed = Owed(l), paid = Math.Min(owed, Math.Max(0, PlayerGold.currency[l.currency]));
                Pay(l.currency, paid); World.State.bottomry.Remove(l);
                Default(Pledges.Find(l.pledge), l, owed - paid);
            }
        }
        // A pledged ship of the player's sinks: its loans are cancelled, each lending region loses 2 levels (on top of any
        // Respondentia penalty), loans pause for 7 days and the ship can never be pledged again. A rental that sinks ends
        // its rental instead, and its loans are collected rather than cancelled.
        internal static void Sinking(SaveableObject hull, List<ShipItem> ghosts = null)
        {
            if (!World.Ready || !hull) return;
            if (Rentals.IsIndex(hull.sceneIndex)) { Rentals.Sank(hull, ghosts); return; }
            int index = hull.sceneIndex; var loans = On(index).ToArray(); if (loans.Length == 0) return;
            foreach (int region in loans.Select(l => l.region).Distinct())
            {
                int level = Reputations.Level(region), rep = Reputations.Required(region, Math.Max(0, level - Rules.SinkingLevels));
                Reputations.Change(rep - Reputations.Points(region), region);
            }
            foreach (var l in loans) World.State.bottomry.Remove(l);
            if (!World.State.burned.Contains(index)) World.State.burned.Add(index);
            LoanGate.StartPause();
            Loans.Notify("The " + Name(index) + " sank: its Bottomry loans are cancelled");
        }
        // Bankruptcy (one region, or all with -1): each of the bankrupt lender's loans takes back what it paid out from the
        // coins left in that currency (never below 0) and is forgiven; the lender takes the pledge, which can never be pledged
        // again. The pledge's loans from other regions run on to their own day 11. A rented ship is handled by Rentals first.
        internal static void Bankruptcy(int region)
        {
            foreach (var l in World.State.bottomry.Where(l => region < 0 || l.region == region).ToArray())
            {
                TakeBack(l); World.State.bottomry.Remove(l);
                if (!World.State.burned.Contains(l.pledge)) World.State.burned.Add(l.pledge);
                var b = Pledges.Find(l.pledge);
                if (b && Pledges.Owned(b) && Seized(l.pledge) == null) Take(b, l.pledge);
            }
        }
        internal static void TakeBack(BottomryLoan l) => Pay(l.currency, Math.Min(l.principal, Math.Max(0, PlayerGold.currency[l.currency])));
        // Bought back: no longer listed as seized.
        internal static void BoughtBack(PurchasableBoat b)
        {
            var seized = Seized(Pledges.Index(b)); if (seized != null && Pledges.Owned(b)) World.State.seized.Remove(seized);
        }
        // After a load: seized pledges were saved as owned (so the game kept their place, rig and damage); hand them back.
        internal static void Loaded()
        {
            Pledges.RecordBerths();
            foreach (var seized in World.State.seized)
            {
                var b = Pledges.Find(seized.pledge); if (!b) continue;
                b.GetComponent<SaveableObject>().extraSetting = false;
                if (GameState.lastOwnedBoat == b.transform) GameState.lastOwnedBoat = null;
                Pledges.ShowSale(b);
                // Saved while waiting at her berth for the player to come near: she waits again.
                var ropes = b.GetComponent<BoatMooringRopes>();
                if (ropes && !ropes.ropes.Any(r => r && r.IsMoored()) && Rentals.NearBerth(b, 15)) Pledges.TieHome(b);
            }
        }
        // While a save runs, seized pledges count as owned so the game stores their place, rig and damage.
        internal static List<SaveableObject> MarkOwnedForSave()
        {
            var marked = new List<SaveableObject>();
            if (!World.Ready) return marked;
            foreach (var seized in World.State.seized)
            {
                var b = Pledges.Find(seized.pledge); var save = b ? b.GetComponent<SaveableObject>() : null;
                if (save && !save.extraSetting && !b.isHouse) { save.extraSetting = true; marked.Add(save); }
            }
            return marked;
        }
    }
    [HarmonyPatch] internal static class SeizedSavePatch
    {
        static MethodBase TargetMethod() => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(SaveLoadManager), "DoSaveGame"));
        static void Prefix(out List<SaveableObject> __state) => __state = Bottomry.MarkOwnedForSave();
        static Exception Finalizer(Exception __exception, List<SaveableObject> __state)
        { if (__state != null) foreach (var save in __state) if (save) save.extraSetting = false; return __exception; }
    }
    // A finalizer: the game's purchase can throw after it has completed (0.39's jib reset meets an empty mast slot).
    [HarmonyPatch(typeof(GPButtonPurchaseBoat), "OnActivate")] internal static class BuyBackPatch
    {
        static Exception Finalizer(Exception __exception, GPButtonPurchaseBoat __instance)
        {
            var b = __instance.boat;
            if (b && World.Ready && Pledges.Owned(b)) Bottomry.BoughtBack(b);
            return __exception;
        }
    }
    // For a sinking rental, the items aboard are noted first: those the game leaves behind as ghosts (parent -3) are
    // removed when she is moved home, and her cached copies respawn there.
    [HarmonyPatch(typeof(BoatLocalItems), "CacheItemsOnSinking")] internal static class BottomrySinkingPatch
    {
        static void Prefix(BoatLocalItems __instance, out List<ShipItem> __state)
        {
            __state = null; var hull = __instance.GetComponent<SaveableObject>();
            if (!World.Ready || !hull || !Rentals.IsIndex(hull.sceneIndex) || !SaveLoadManager.instance) return;
            __state = SaveLoadManager.instance.GetCurrentPrefabs().Where(p => p && p.GetParentObject() == hull.sceneIndex).Select(p => p.GetComponent<ShipItem>()).Where(i => i).ToList();
        }
        static void Postfix(BoatLocalItems __instance, List<ShipItem> __state) => Bottomry.Sinking(__instance.GetComponent<SaveableObject>(), __state);
    }

    // ---- Forced moves ----
    // A ship taken from the player goes her own way: she stays where she is tied in a port, or returns to her for-sale
    // berth; a rental always goes home. The player is moved only when aboard her: aboard their nearest own ship (near or
    // far), or to the nearest port with none (a seized house: out of its door to the port). The black screen says why.
    // Everything queued in the same tick (a failed rent charge and a day 11) is one move.
    internal static class ForcedMove
    {
        // Busy: a move is queued or running. Moving: it has started; what is queued from then on follows in a second move.
        internal static bool Busy, Moving;
        // Two lines: on one, the game's font fills a 16:9 screen and runs off narrower ones.
        internal const string BlackScreen = "\n\n\nThey've come\nto take your debt";
        private sealed class Trip { internal PurchasableBoat ship; internal bool sunk, restore; internal List<ShipItem> ghosts; }
        private static bool movePlayer;
        private static readonly List<Trip> trips = new List<Trip>();
        private static readonly MethodInfo disembark = AccessTools.Method(typeof(PlayerEmbarkerNew), "PlayerDisembark");
        internal static bool Aboard(PurchasableBoat b) => b && GameState.currentBoat && GameState.currentBoat.IsChildOf(b.transform);
        // Un-own a ship a lender took and put her where she belongs; the player moves if aboard her.
        internal static void Release(PurchasableBoat b)
        {
            if (b.isHouse)
            {
                if (GameState.currentHouse && GameState.currentHouse.transform.IsChildOf(b.transform)) Plugin.Instance.StartCoroutine(OutOfHouse(b));
                b.GetComponent<SaveableObject>().extraSetting = false; Pledges.ShowSale(b);
                return;
            }
            bool player = Aboard(b);
            Pledges.Unown(b);
            bool stays = Pledges.TiedPort(b) != null;
            Queue(player, stays ? null : b);
        }
        // A ship going home: a sunk one is refloated (the game's ghost copies of its items removed, its cached items
        // respawned there); a returned rental is repaired and cleaned.
        internal static void Queue(bool player, PurchasableBoat ship, bool sunk = false, bool restore = false, List<ShipItem> ghosts = null)
        {
            if (!player && !ship) return;
            movePlayer |= player;
            if (ship && trips.All(t => t.ship != ship)) trips.Add(new Trip { ship = ship, sunk = sunk, restore = restore, ghosts = ghosts });
            if (!Busy) { Busy = true; Plugin.Instance.StartCoroutine(Run()); }
        }
        private static IEnumerator Run()
        {
            bool faded = false;
            try
            {
                yield return null;
                Moving = true;
                while (movePlayer || trips.Count > 0)
                {
                    bool player = movePlayer; var ships = trips.ToArray(); movePlayer = false; trips.Clear();
                    if (CurrencyExchangeUI.instance && CurrencyExchangeUI.instance.uiActive) CurrencyExchangeUI.instance.CloseUI();
                    RentalPanel.Close();
                    if (GameState.sleeping && Sleep.instance) Sleep.instance.WakeUp();
                    faded = true; SetText(BlackScreen); yield return Blackout.FadeTo(1f, .3f);
                    if (player) yield return ToNearestShip();
                    foreach (var trip in ships) if (trip.ship) yield return ToBerth(trip);
                    yield return Blackout.FadeTo(0f, .3f); faded = false; SetText("");
                }
            }
            finally
            {
                Busy = false; Moving = false; movePlayer = false; trips.Clear(); SetText("");
                if (faded && Plugin.Instance) Plugin.Instance.StartCoroutine(Blackout.FadeTo(0f, .3f));
            }
        }
        // The game's black-screen line (its recovery writes "recovering..." there).
        private static void SetText(string text) { if (Sleep.instance && Sleep.instance.recoveryText) Sleep.instance.recoveryText.text = text; }
        // Aboard the nearest own ship (with none, to the nearest port recovery spot), the way NANDcommand's "tpto boat" lands a
        // player (the same steps, no use of that mod): GameState.recovering stays on for the whole move, as in the game's own
        // recovery (ships held still, world shifts at once); only the player's body moves (the view follows it); it leaves the
        // ship the player is on in the same frame as they disembark, so the game cannot board them on her again; it goes high
        // over the new place first (the world shifts there and a ship the game switched off far away is switched on), then to
        // DropPoint, from where the player drops onto her and the game's boarding does the rest.
        internal static IEnumerator ToNearestShip()
        {
            var view = Refs.observerMirror ? Refs.observerMirror.transform : null; var body = Refs.charController ? Refs.charController.transform : null;
            if (!view || !body) yield break;
            var ship = Pledges.OwnShips().Where(s => !(GameState.currentBoat && GameState.currentBoat.IsChildOf(s.transform)))
                .OrderBy(s => (s.transform.position - view.position).sqrMagnitude).FirstOrDefault();
            var port = ship ? null : NearestRecoveryPort(view.position);
            if (!ship && !port) yield break;
            // Read again after each wait: the world shifts under the player.
            Vector3 Target() => ship ? DropPoint(ship) : port.position;
            bool recovering = GameState.recovering; GameState.recovering = true;
            try
            {
                if (GameState.currentBoat)
                {
                    var embarker = UnityEngine.Object.FindObjectOfType<PlayerEmbarkerNew>();
                    if (embarker && disembark != null) disembark.Invoke(embarker, null);
                }
                PutBody(Target() + Vector3.up * 200);
                for (int i = 0; i < 3; i++) yield return new WaitForFixedUpdate();
                // The islands there load first, as the game's own recovery waits for them (GameState.loadingScenes): boarding a
                // ship while an island scene is still loading (Kicia Bay's above all) can crash the game with other mods installed,
                // as teleports such as NANDcommand's are known to.
                yield return new WaitForSecondsRealtime(.5f);
                for (float waited = 0; GameState.loadingScenes > 0 && waited < 30; waited += Time.unscaledDeltaTime) yield return null;
                // The game's own switcher switches a ship near the player back on (as for any player arriving): waited for, and
                // done here only if it has not, in a normal update (other mods react to a ship being switched on).
                for (int frame = 0; ship && frame < 60 && !ship.gameObject.activeInHierarchy; frame++) yield return null;
                if (ship && !ship.gameObject.activeInHierarchy)
                {
                    var horizon = ship.GetComponentInChildren<BoatHorizon>(true);
                    if (horizon) horizon.transform.parent.gameObject.SetActive(true);
                    yield return new WaitForFixedUpdate();
                }
                // A ship just switched on can take a few physics steps before her walk collider answers: wait for her deck.
                for (int i = 0; ship && i < 10 && DeckDrop(ship) == null; i++) yield return new WaitForFixedUpdate();
                PutBody(Target());
                if (!ship) body.rotation = port.rotation;
                yield return new WaitForSeconds(.5f);
                // Not boarded within a second: the game boards a player when the copy of them it keeps on her walk collider touches
                // her deck, and once in about fifty test moves it saw the player at her but never that touch. Then the game's
                // boarding runs for her from the drop point (Board); last, once more from 10 m over her origin (NANDcommand's
                // point). Only while playing: nothing boards otherwise.
                if (ship && GameState.playing)
                {
                    for (float waited = 0; waited < 1 && !Aboard(ship); waited += Time.deltaTime) yield return null;
                    if (!Aboard(ship)) { PutBody(Target()); yield return null; Board(ship); yield return new WaitForSeconds(.5f); }
                    if (!Aboard(ship)) { PutBody(ship.transform.position + Vector3.up * 10); yield return new WaitForSeconds(1f); }
                }
            }
            finally { GameState.recovering = recovering; }
        }
        // The game's own boarding steps, called as its physics calls them: the player's view enters her boarding trigger (the game
        // then keeps a copy of the player on her walk collider, where the view is over her), and that copy touches her deck.
        private static void Board(PurchasableBoat ship)
        {
            var embarker = UnityEngine.Object.FindObjectOfType<PlayerEmbarkerNew>();
            var link = Link(ship); var trigger = link ? link.GetComponent<Collider>() : null;
            var deck = link ? link.walkCollider.GetComponentsInChildren<Collider>().FirstOrDefault(c => !c.isTrigger && c.transform != link.walkCollider) : null;
            if (!embarker || !trigger || !deck) return;
            embarker.ObserverTriggerEnter(trigger);
            embarker.OnTriggerEnter(deck);
        }
        // The player's body to a place, its CharacterController off for the jump: one left on can put back its old place on its
        // next move (it keeps its own copy of the position until physics catches up), and the player would stay where they were.
        private static void PutBody(Vector3 position)
        {
            var cc = Refs.charController; bool on = cc.enabled;
            cc.enabled = false; cc.transform.position = position; cc.enabled = on;
        }
        // Where the player drops onto a ship from: 1.5 m over the spot of her deck nearest her middle, found on her walk collider
        // (the copy the player walks on), linked as the game's boarding links it (her boarding collider's walkCollider, mapped
        // to that collider's parent). A spot counts only where the same surface goes on 0.4 m all round, within 0.5 m of its
        // height: a cambered deck passes; a mast, yard, rail or narrow hatch does not. Spots are tried outward from the middle
        // along both axes. With none found, 10 m over her origin (NANDcommand's "tpto boat" point).
        // Straight up in the world from the spot, so a ship heeled over or aground is still landed on.
        internal static Vector3 DropPoint(PurchasableBoat ship) => DeckDrop(ship) ?? ship.transform.position + Vector3.up * 10;
        // The deck spot's drop point, or null when her walk collider shows none.
        private static Vector3? DeckDrop(PurchasableBoat ship)
        {
            var link = Link(ship);
            var walk = link ? link.walkCollider : null; var frame = link ? link.transform.parent : null;
            if (walk && frame)
                foreach (float d in new[] { 0f, 1.5f, 3f, 4.5f, 6f })
                    foreach (var at in d == 0 ? new[] { Vector2.zero } : new[] { new Vector2(d, 0), new Vector2(-d, 0), new Vector2(0, d), new Vector2(0, -d) })
                    {
                        var spot = Surface(walk, at.x, at.y); if (spot == null) continue;
                        var top = spot.Value; float y = walk.InverseTransformPoint(top.point).y;
                        bool flat = new[] { new Vector2(.4f, 0), new Vector2(-.4f, 0), new Vector2(0, .4f), new Vector2(0, -.4f) }
                            .All(o => { var s = Surface(walk, at.x + o.x, at.y + o.y); return s != null && s.Value.collider == top.collider && Mathf.Abs(walk.InverseTransformPoint(s.Value.point).y - y) <= .5f; });
                        if (flat) return frame.TransformPoint(walk.InverseTransformPoint(top.point)) + Vector3.up * 1.5f;
                    }
            return null;
        }
        // Her boarding trigger that names her walk collider, the player-sized one first (the game boards from either).
        private static BoatEmbarkCollider Link(PurchasableBoat ship) => ship.GetComponentsInChildren<BoatEmbarkCollider>(true)
            .Where(c => c.walkCollider).OrderBy(c => c.CompareTag("EmbarkColPlayer") ? 0 : 1).FirstOrDefault();
        // The top of the walk collider straight down through (x, z) of its own frame.
        private static RaycastHit? Surface(Transform walk, float x, float z)
        {
            var hits = Physics.RaycastAll(walk.TransformPoint(new Vector3(x, 15, z)), -walk.up, 40, ~0, QueryTriggerInteraction.Ignore)
                .Where(h => h.collider.transform.IsChildOf(walk)).OrderBy(h => h.distance).ToArray();
            return hits.Length > 0 ? hits[0] : (RaycastHit?)null;
        }
        // Out of the player's sight: onto her berth without a black screen.
        internal static void Home(PurchasableBoat b) { if (b && Plugin.Instance) Plugin.Instance.StartCoroutine(ToBerth(new Trip { ship = b })); }
        private static Transform NearestRecoveryPort(Vector3 from)
        {
            var ports = Fields.Find(typeof(Recovery), "ports").GetValue(null) as List<RecoveryPort>;
            return ports?.Where(p => p).OrderBy(p => (p.transform.position - from).sqrMagnitude).Select(p => p.transform).FirstOrDefault();
        }
        private static IEnumerator OutOfHouse(PurchasableBoat house)
        {
            var spot = NearestRecoveryPort(house.transform.position); if (!spot) yield break;
            yield return Blackout.FadeTo(1f, .3f);
            Refs.observerMirror.transform.position = spot.position; Refs.observerMirror.transform.rotation = spot.rotation;
            PutBody(spot.position); Refs.charController.transform.rotation = spot.rotation;
            yield return new WaitForFixedUpdate();
            yield return Blackout.FadeTo(0f, .3f);
        }
        // Back to its for-sale berth and tied to its home cleats, the way the game's recovery moves a ship (and refloats a
        // sunk one). A rental already tied at her berth stays.
        private static IEnumerator ToBerth(Trip trip)
        {
            var b = trip.ship;
            if (trip.ghosts != null)
                foreach (var item in trip.ghosts)
                {
                    var save = item ? item.GetComponent<SaveablePrefab>() : null;
                    if (save && save.GetParentObject() == -3) item.DestroyItem();
                }
            var ropes = b.GetComponent<BoatMooringRopes>(); var damage = b.GetComponent<BoatDamage>();
            if (trip.restore && damage) damage.hullDamage = 0;
            bool known = Pledges.Berth(b, out var position, out var rotation);
            bool stay = !trip.sunk && (!known || trip.restore && Rentals.AtHomeCleats(b));
            // Another ship lies at her berth: she stays where she is and goes there once it is free (a sunk one is refloated anyway).
            if (!stay && known && !trip.sunk && Pledges.BerthTaken(b, position)) { Pledges.HomeLater(b); stay = true; }
            if (!stay)
            {
                bool recovering = GameState.recovering; GameState.recovering = true;
                try
                {
                    if (ropes) { ropes.UnmoorAllRopes(); if (ropes.GetAnchorController()) ropes.GetAnchorController().ResetAnchor(); }
                    if (damage) { damage.waterLevel = 0; damage.enabled = true; }
                    yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
                    // Read again: the world may have shifted under the player meanwhile.
                    if (known && Pledges.Berth(b, out position, out rotation)) b.transform.position = position;
                    if (trip.sunk && damage)
                    {
                        var sinking = damage.GetSinkRotation();
                        if (sinking.x * sinking.x + sinking.y * sinking.y + sinking.z * sinking.z + sinking.w * sinking.w > .5f) b.transform.rotation = sinking;
                        var items = b.GetComponent<BoatLocalItems>(); if (items) items.SetItemsLoaded(false);
                    }
                    yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
                    if (known) b.transform.rotation = rotation;
                    Pledges.TieHome(b);
                    yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
                }
                finally { GameState.recovering = recovering; }
            }
            if (trip.restore) Rentals.Restore(b);
        }
    }

    // A copy of one of the game's text objects for the mod's UI (the Bottomry page, the rental panel): only its text and its
    // renderer kept, no children.
    internal static class GameText
    {
        internal static GameObject Bare(GameObject source, Transform parent, string name)
        {
            var copy = UnityEngine.Object.Instantiate(source, parent); copy.name = name;
            foreach (var extra in copy.GetComponents<Component>()) if (!(extra is Transform) && !(extra is TextMesh) && !(extra is MeshRenderer)) UnityEngine.Object.DestroyImmediate(extra);
            foreach (var child in copy.transform.Cast<Transform>().ToArray()) UnityEngine.Object.DestroyImmediate(child.gameObject);
            return copy;
        }
    }
    // ---- Bottomry page on the exchange screen ----
    // A tab beside the X turns the exchange screen into the Bottomry page: the ships tied here (and this region's houses)
    // to borrow on, every active loan to repay, payout coins, the terms on the price card and the ribbon.
    public sealed class BottomryPage : MonoBehaviour
    {
        internal static Component OpenedFrom;
        private sealed class Row { internal PurchasableBoat pledge; internal BottomryLoan loan; }
        private CurrencyExchangeUI ui; private Transform root, top, bottom;
        private LoanAction tab; private TextMesh tabText, title, caption, note, terms, ribbon;
        private readonly LoanAction[] rowCards = new LoanAction[4]; private readonly MeshRenderer[] rowPlates = new MeshRenderer[4];
        private readonly TextMesh[,] rowTexts = new TextMesh[4, 3];
        private readonly TextMesh[] coins = new TextMesh[4];
        private LoanAction previous, next; private TextMesh previousLabel, nextLabel; private MeshRenderer ribbonPlate; private Material ribbonMaterial, ribbonShared; private Color ribbonColor;
        private readonly List<GameObject> hidden = new List<GameObject>(), shown = new List<GameObject>();
        private string ribbonOriginal; private Material normalRow, selectedRow;
        private List<Row> rows = new List<Row>(); private int selected, page, currency = -1;
        private int shownCost = -1;   // the payout or the cost on the page when it was drawn
        private LoanBox box; private float timer; private bool ready;
        internal bool Open { get; private set; }
        internal static BottomryPage For(CurrencyExchangeUI ui) { var p = ui.GetComponent<BottomryPage>(); if (!p) { p = ui.gameObject.AddComponent<BottomryPage>(); p.ui = ui; } return p; }

        private void Build()
        {
            if (ready) return; ready = true;
            root = ui.transform; top = root.Find("panel  top (SELL)"); bottom = root.Find("panel bottom (BUY)");
            var exit = root.Find("button exit (1)");
            var tabObject = Instantiate(exit.gameObject, root); tabObject.name = "button bottomry tab";
            DestroyImmediate(tabObject.GetComponent<CurrencyExchangeUIButton>());
            tabObject.transform.localPosition = new Vector3(-.3521f, .0706f, -.3643f); tabObject.transform.localRotation = new Quaternion(-.706f, .038f, -.038f, .706f);
            tabObject.transform.localScale = new Vector3(1.8253f, 1.0116f, .9607f);
            tab = tabObject.AddComponent<LoanAction>(); tab.action = Toggle; tab.lookText = "Bottomry";
            tabText = tabObject.transform.Find("text").GetComponent<TextMesh>();
            tabText.transform.localPosition = new Vector3(.0376f, -.0069f, -.0196f); tabText.transform.localRotation = new Quaternion(0, 1, 0, 0);
            tabText.transform.localScale = new Vector3(.001658f, .00301f, .0051f); tabText.text = "Bottomry";

            var titleSource = top.Find("text currency name").GetComponent<TextMesh>();
            title = Clone(titleSource, top, "Bottomry ships title", new Vector3(.068f, 0, -.308f), titleSource.transform.localRotation, titleSource.transform.localScale);
            Hide(titleSource.gameObject);
            var card = top.Find("bg title").GetComponent<MeshRenderer>();
            normalRow = card.sharedMaterial; selectedRow = new Material(card.sharedMaterial) { color = new Color(.827f, .902f, .722f) };
            float[] rowZ = { -.10f, .04f, .18f, .32f };
            for (int i = 0; i < 4; i++)
            {
                int index = i;
                var plate = Instantiate(card.gameObject, top); plate.name = "Bottomry row " + i;
                plate.transform.localPosition = new Vector3(.0879f, 0, rowZ[i]); plate.transform.localRotation = new Quaternion(-.5f, .5f, -.5f, .5f);
                plate.transform.localScale = new Vector3(15.5465f, 5.0425f, 5.1951f);
                if (!plate.GetComponent<Collider>()) plate.AddComponent<BoxCollider>();
                rowCards[i] = plate.AddComponent<LoanAction>(); rowCards[i].action = () => Select(page * 4 + index); rowCards[i].lookText = "Select";
                rowPlates[i] = plate.GetComponent<MeshRenderer>(); shown.Add(plate);
                var scale = new Vector3(.012972f, .014444f, .0157f);
                rowTexts[i, 0] = Clone(titleSource, top, "Bottomry row name", new Vector3(.068f, -.66f, rowZ[i]), titleSource.transform.localRotation, scale, TextAnchor.MiddleLeft, TextAlignment.Left);
                rowTexts[i, 1] = Clone(titleSource, top, "Bottomry row value", new Vector3(.068f, -.02f, rowZ[i]), titleSource.transform.localRotation, scale, TextAnchor.MiddleCenter, TextAlignment.Center);
                rowTexts[i, 2] = Clone(titleSource, top, "Bottomry row state", new Vector3(.068f, .66f, rowZ[i]), titleSource.transform.localRotation, scale, TextAnchor.MiddleRight, TextAlignment.Right);
                // Three columns across the card; a long entry shrinks instead of running into its neighbour.
                float[] widths = { .42f, .40f, .44f };
                for (int c = 0; c < 3; c++) { var fit = rowTexts[i, c].gameObject.AddComponent<LoanTextFit>(); fit.originalScale = rowTexts[i, c].transform.localScale; fit.maxWidth = widths[c]; }
            }
            // Paging arrows at the title line's ends, from the hidden amount buttons' plates.
            var amount = top.GetComponentsInChildren<CurrencyExchangeUIButton>(true).First(FxQuantityButton.IsAmountButton);
            previous = Arrow(amount, new Vector3(.119f, -.86f, -.24f), "<", () => { page = Math.Max(0, page - 1); Refresh(); }, out previousLabel);
            next = Arrow(amount, new Vector3(.119f, .86f, -.24f), ">", () => { page++; Refresh(); }, out nextLabel);

            var captionSource = bottom.Find("text currency name (1)").GetComponent<TextMesh>();
            caption = Clone(captionSource, bottom, "Bottomry caption", new Vector3(.067f, 0, .313f), captionSource.transform.localRotation, captionSource.transform.localScale);
            note = Clone(captionSource, bottom, "Bottomry note", new Vector3(.067f, 0, -.30f), captionSource.transform.localRotation, new Vector3(.010152f, .011304f, .0157f));
            Hide(captionSource.gameObject);
            for (int c = 0; c < 4; c++)
            {
                var source = top.Find("money icon (" + (c + 1) + ")").GetChild(0).GetComponent<TextMesh>();
                var coin = bottom.Find("money icon (" + (c + 1) + ")");
                coins[c] = Clone(source, coin, "Bottomry coin", source.transform.localPosition, source.transform.localRotation, source.transform.localScale);
                var fit = coins[c].gameObject.AddComponent<LoanTextFit>(); fit.originalScale = coins[c].transform.localScale;
                fit.maxWidth = BookUI.MeasureLine(coins[c], "88888");
            }
            var priceSource = root.Find("text price info").GetComponent<TextMesh>();
            terms = Clone(priceSource, root, "Bottomry terms", priceSource.transform.localPosition, priceSource.transform.localRotation, priceSource.transform.localScale);
            Hide(priceSource.gameObject);
            var confirm = root.Find("button confirm"); ribbon = confirm.Find("text").GetComponent<TextMesh>(); ribbonOriginal = ribbon.text;
            ribbonPlate = confirm.GetComponent<MeshRenderer>(); ribbonShared = ribbonPlate.sharedMaterial; ribbonMaterial = new Material(ribbonShared); ribbonColor = ribbonMaterial.HasProperty("_Color") ? ribbonMaterial.color : Color.white;
            foreach (var name in new[] { "money icon (1)", "money icon (2)", "money icon (3)", "money icon (4)", "text (buttons) (1)" }) Hide(top.Find(name).gameObject);
            Hide(bottom.Find("text (buttons)").gameObject);
            foreach (var panel in new[] { top, bottom })
                foreach (Transform child in panel)
                    if (child.name == "button amount" || child.name.StartsWith("Exchange amount", StringComparison.Ordinal)) Hide(child.gameObject);
            foreach (var name in new[] { "bg (center) alt", "text (amount)", "text (arrow" }) Hide(root.Find(name).gameObject);
            foreach (var go in shown) go.SetActive(false);
        }
        private TextMesh Clone(TextMesh source, Transform parent, string name, Vector3 position, Quaternion rotation, Vector3 scale,
            TextAnchor? anchor = null, TextAlignment? alignment = null)
        {
            var copy = GameText.Bare(source.gameObject, parent, name);
            copy.transform.localPosition = position; copy.transform.localRotation = rotation; copy.transform.localScale = scale;
            var text = copy.GetComponent<TextMesh>(); if (anchor.HasValue) text.anchor = anchor.Value; if (alignment.HasValue) text.alignment = alignment.Value;
            text.text = ""; shown.Add(copy); return text;
        }
        private LoanAction Arrow(CurrencyExchangeUIButton source, Vector3 position, string label, Action action, out TextMesh text)
        {
            var copy = Instantiate(source.gameObject, top); copy.name = "Bottomry page " + label;
            foreach (var extra in copy.GetComponents<Component>()) if (extra is CurrencyExchangeUIButton || extra is FxQuantityButton) DestroyImmediate(extra);
            copy.transform.localPosition = position; copy.transform.localScale = source.transform.localScale * .8f;
            var button = copy.AddComponent<LoanAction>(); button.action = action; button.lookText = label == "<" ? "Previous" : "Next";
            text = Clone(rowTexts[0, 1], top, "Bottomry page label " + label, new Vector3(.05f, position.y, position.z), rowTexts[0, 1].transform.localRotation, rowTexts[0, 1].transform.localScale);
            text.text = label; shown.Add(copy); return button;
        }
        private void Hide(GameObject go) { if (go && !hidden.Contains(go)) hidden.Add(go); }

        internal void Opened(Component chest)
        {
            Build(); box = LoanBox.Of(chest, FxBooth.For(ui).region);
            tab.gameObject.SetActive(World.Ready);
            if (Open) ShowExchange();
        }
        internal void Closed() { if (Open) ShowExchange(); }
        private void Toggle() { if (Open) ShowExchange(); else ShowPage(); }
        private void ShowPage()
        {
            if (!World.Ready || box == null) return;
            Open = true; tabText.text = "Exchange"; tab.lookText = "Exchange";
            foreach (var panel in new[] { top, bottom }) foreach (Transform child in panel) if (child.name.StartsWith("Exchange amount", StringComparison.Ordinal)) Hide(child.gameObject);
            foreach (var go in hidden) if (go) go.SetActive(false);
            foreach (var go in shown) if (go) go.SetActive(true);
            ribbonPlate.sharedMaterial = ribbonMaterial; selected = 0; page = 0; currency = -1;
            Refresh();
        }
        private void ShowExchange()
        {
            Open = false; tabText.text = "Bottomry"; tab.lookText = "Bottomry";
            foreach (var go in shown) if (go) go.SetActive(false);
            foreach (var go in hidden) if (go) go.SetActive(true);
            ribbon.text = ribbonOriginal; ribbonMaterial.color = ribbonColor; ribbonPlate.sharedMaterial = ribbonShared;
            Outline(Fields.Get<int>(ui, "currentBuyCurrency"));
            ui.RefreshUI();
        }
        private void Update()
        {
            if (!Open || !ui.uiActive) return;
            timer -= Time.unscaledDeltaTime; if (timer <= 0) { timer = .5f; Refresh(); }
        }
        private void Select(int index) { selected = index; currency = -1; Refresh(); }
        // Payout currency, chosen with the bottom coins (the loan currency is fixed when repaying).
        internal void Coin(int index, Transform coin)
        {
            if (selected >= rows.Count || rows[selected].loan != null) return;
            currency = index; Refresh();
        }
        private void Outline(int c)
        {
            var outline = Fields.Get<Transform>(ui, "buttonOutlineBuy"); var coin = bottom.Find("money icon (" + (c + 1) + ")");
            if (!outline || !coin) return;
            outline.parent = coin; outline.localPosition = new Vector3(0, 0, outline.localPosition.z);
        }

        private void Rows()
        {
            var list = new List<Row>();
            foreach (var b in Pledges.All().Where(b => Pledges.Owned(b) && !World.State.bottomry.Any(l => l.pledge == Pledges.Index(b) && l.region == box.region)))
            {
                if (b.isHouse ? Pledges.HouseRegion(Pledges.Index(b)) == box.region : Pledges.IsShip(b) && Pledges.TiedAt(b, box.island)) list.Add(new Row { pledge = b });
            }
            list = list.OrderBy(r => r.pledge.isHouse).ThenBy(r => Pledges.Name(Pledges.Index(r.pledge))).ToList();
            list.AddRange(World.State.bottomry.OrderBy(l => l.dueDay).ThenBy(l => l.id).Select(l => new Row { loan = l, pledge = Pledges.Find(l.pledge) }));
            rows = list;
            if (selected >= rows.Count) selected = Math.Max(0, rows.Count - 1);
        }
        internal void Refresh()
        {
            if (!Open || box == null) return;
            Rows();
            int pages = Math.Max(1, (rows.Count + 3) / 4); page = Mathf.Clamp(page, 0, pages - 1);
            if (selected / 4 != page && rows.Count > 0) selected = Math.Min(rows.Count - 1, page * 4);
            string region = BookUI.RegionName(box.region);
            for (int i = 0; i < 4; i++)
            {
                int n = page * 4 + i; bool exists = n < rows.Count;
                rowCards[i].gameObject.SetActive(exists); rowPlates[i].sharedMaterial = n == selected ? selectedRow : normalRow;
                for (int c = 0; c < 3; c++) rowTexts[i, c].text = "";
                if (!exists) continue;
                var row = rows[n]; int index = row.loan != null ? row.loan.pledge : Pledges.Index(row.pledge);
                rowTexts[i, 0].text = Pledges.Name(index);
                if (row.loan != null)
                {
                    int left = row.loan.dueDay - GameState.day;
                    rowTexts[i, 1].text = "pledged: " + BookUI.RegionName(row.loan.region);
                    rowTexts[i, 2].text = left >= 0 ? "due day " + row.loan.dueDay : LoanGate.Days(-left) + " late";
                }
                else
                {
                    var regions = World.State.bottomry.Where(l => l.pledge == index).Select(l => BookUI.RegionName(l.region)).ToArray();
                    rowTexts[i, 1].text = regions.Length > 0 ? "pledged: " + string.Join(", ", regions) : row.pledge.price + " gold";
                    rowTexts[i, 2].text = row.pledge.isHouse ? "house" : "tied here";
                }
            }
            previous.gameObject.SetActive(page > 0); next.gameObject.SetActive(page < pages - 1);
            previousLabel.gameObject.SetActive(page > 0); nextLabel.gameObject.SetActive(page < pages - 1);
            string status = null, header = box.PortName + " · " + region + " reputation " + Reputations.Level(box.region);
            bool can = false;
            if (rows.Count == 0)
            {
                status = Reputations.Level(box.region) < 1 ? "No loans: reputation 0 in " + region : "No ship of yours is tied here";
                caption.text = "\nLOAN"; note.text = ""; terms.text = ""; for (int c = 0; c < 4; c++) coins[c].text = ""; shownCost = -1;
                ribbon.text = "Take loan";
            }
            else if (rows[selected].loan == null)
            {
                var b = rows[selected].pledge; int index = Pledges.Index(b), local = Bottomry.LocalCurrency(box.region);
                if (currency < 0) currency = local;
                int principal = Bottomry.LocalPrincipal(b, box.region), level = Reputations.Level(box.region);
                for (int c = 0; c < 4; c++) coins[c].text = Bottomry.Payout(principal, box.region, c).ToString();
                int paid = Bottomry.Payout(principal, box.region, currency); double rate = Rules.BottomryRate(level); int term = Rules.BottomryTerm(level);
                shownCost = paid;
                caption.text = "Loan paid in " + PlayerGold.GetCurrencyName(currency) + "\nLOAN";
                note.text = "own currency: no fee · others: exchange rate";
                terms.text = Pledges.Name(index) + "\nloan " + paid + "\n" + PlayerGold.GetCurrencyName(currency) + (level >= 1 ?
                    "\ninterest " + Bottomry.Percent(rate) + "\nrepay " + Rules.BottomryOwed(paid, rate) + "\nwithin " + term + " days\n(day " + (GameState.day + term) + ")" : "");
                status = Bottomry.Refusal(b, box, currency); can = status == null;
                ribbon.text = "Take loan"; Outline(currency);
            }
            else
            {
                var l = rows[selected].loan; currency = l.currency;
                int due = Bottomry.BoxCost(l, box.region), have = PlayerGold.currency[l.currency], left = l.dueDay - GameState.day;
                for (int c = 0; c < 4; c++) coins[c].text = PlayerGold.currency[c].ToString();
                caption.text = "Repay in " + PlayerGold.GetCurrencyName(l.currency) + "\nREPAY";
                note.text = l.region == box.region ? "a loan is repaid in the currency it was taken in" : "outside " + BookUI.RegionName(l.region) + ": 10% more interest";
                terms.text = Pledges.Name(l.pledge) + "\nowed " + due + "\n" + PlayerGold.GetCurrencyName(l.currency) + "\ndue day " + l.dueDay +
                    "\n" + (left >= 0 ? LoanGate.Days(left) + " left" : LoanGate.Days(-left) + " late") + "\ninterest " +
                    Bottomry.Percent(Bottomry.Rate(l) + (l.region == box.region ? 0 : Rules.BottomryBoxExtra)) + "\nyou have " + have;
                if (Bottomry.InShipyard(rows[selected].pledge)) status = "Move the " + Pledges.Name(l.pledge) + " out of the shipyard first";
                can = status == null;
                bool taken = rows[selected].pledge && !Pledges.Owned(rows[selected].pledge);
                ribbon.text = have >= due ? "Repay" : taken ? "Pay " + Math.Max(0, have) + ", owe the rest" : "Pay " + Math.Max(0, have) + " & lose " + (rows[selected].pledge && rows[selected].pledge.isHouse ? "house" : "ship");
                shownCost = due;
                Outline(l.currency);
            }
            title.text = "SHIPS\n" + (status ?? header);
            ribbonMaterial.color = can ? ribbonColor : ribbonColor * .55f + new Color(0, 0, 0, ribbonColor.a * .45f);
        }
        internal void Confirm()
        {
            if (!Open || box == null || selected >= rows.Count) return;
            var row = rows[selected];
            // Terms that changed since the page was drawn (midnight's late interest, a new daily rate) are shown again first.
            Fx.AdvanceDay(GameState.day);
            int now = row.loan == null ? (row.pledge ? Bottomry.Payout(Bottomry.LocalPrincipal(row.pledge, box.region), box.region, currency < 0 ? Bottomry.LocalCurrency(box.region) : currency) : -1)
                : Bottomry.BoxCost(row.loan, box.region);
            if (now != shownCost) { Loans.Notify("Terms changed: check and confirm again"); Refresh(); return; }
            if (row.loan == null) { if (Bottomry.Take(row.pledge, box, currency < 0 ? Bottomry.LocalCurrency(box.region) : currency) != null) { selected = 0; currency = -1; } }
            else if (Bottomry.InShipyard(row.pledge)) Loans.Notify("Move the " + Pledges.Name(row.loan.pledge) + " out of the shipyard first");
            else Bottomry.RepayAtBox(row.loan, box);
            Refresh();
        }
    }
    [HarmonyPatch(typeof(CurrencyExchangeUIButton), "OnActivate")] internal static class BottomryPageClickPatch
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(CurrencyExchangeUIButton __instance)
        {
            string function = Fields.Get<object>(__instance, "function").ToString();
            if (function == "openUI") { BottomryPage.OpenedFrom = __instance; return true; }
            var ui = CurrencyExchangeUI.instance; var page = ui ? ui.GetComponent<BottomryPage>() : null;
            if (!page || !page.Open) return true;
            if (function == "selectBuyCurrency") page.Coin(Fields.Get<int>(__instance, "index"), __instance.transform);
            else if (function == "confirm") page.Confirm();
            else if (function == "exit") return true;
            return false;
        }
    }
    [HarmonyPatch(typeof(CurrencyExchangeUI), "OpenUI")] internal static class BottomryPageOpenPatch
    { static void Postfix(CurrencyExchangeUI __instance) { BottomryPage.For(__instance).Opened(BottomryPage.OpenedFrom); BottomryPage.OpenedFrom = null; } }
    [HarmonyPatch(typeof(CurrencyExchangeUI), "CloseUI")] internal static class BottomryPageClosePatch
    { static void Postfix(CurrencyExchangeUI __instance) => __instance.GetComponent<BottomryPage>()?.Closed(); }
    // The exchange screen's own refreshes must not redraw over the Bottomry page.
    [HarmonyPatch(typeof(CurrencyExchangeUI), "UpdateTexts")] internal static class BottomryPageTextPatch
    { [HarmonyPriority(Priority.Last)] static void Postfix(CurrencyExchangeUI __instance) { var page = __instance.GetComponent<BottomryPage>(); if (page && page.Open) page.Refresh(); } }

    // ---- Loan books ----
    // A Bankruptcy button that asks first: a click puts Yes and No in its place; Yes declares it, No (or 15 seconds, or the
    // book closing) puts the button back. While renting, the refusal shows instead.
    internal sealed class BankruptcyPrompt
    {
        // Each button's plate (the ledger style puts the clickable surface under it) and its label.
        private readonly GameObject button, yes, no, buttonLabel, yesLabel, noLabel; private readonly Func<int> region; private float until;
        internal bool Asking => yes && yes.activeSelf;
        internal BankruptcyPrompt(Transform parent, MeshRenderer native, Font font, string label, float x, float y, float width, float height,
            Func<int> region, Action after, out TextMesh text)
        {
            this.region = region; var tint = new Color(1, .55f, .22f);
            GameObject Plate(LoanAction a) => a.transform.parent != parent ? a.transform.parent.gameObject : a.gameObject;
            button = Plate(BookUI.Button(parent, native, font, label, x, y, width, height, Ask, tint, out text)); buttonLabel = text.gameObject;
            float half = (width - .012f) / 2, offset = (width - half) / 2;
            yes = Plate(BookUI.Button(parent, native, font, "Yes", x + offset, y, half, height, () => { Stop(); Loans.Bankruptcy(region()); after?.Invoke(); }, tint, out var yesText));
            no = Plate(BookUI.Button(parent, native, font, "No", x - offset, y, half, height, Stop, Color.white, out var noText));
            yesLabel = yesText.gameObject; noLabel = noText.gameObject;
            Stop();
        }
        private void Show(bool asking)
        {
            button.SetActive(!asking); buttonLabel.SetActive(!asking);
            yes.SetActive(asking); yesLabel.SetActive(asking); no.SetActive(asking); noLabel.SetActive(asking);
        }
        private void Ask()
        {
            var refusal = Loans.BankruptcyRefusal(region()); if (refusal != null) { Loans.Notify(refusal); return; }
            Show(true); until = Time.unscaledTime + 15;
        }
        internal void Stop() { if (yes) Show(false); }
        // Called while the book is open: the question lapses after 15 seconds.
        internal void Tick() { if (Asking && Time.unscaledTime > until) Stop(); }
    }
    public sealed class LoanAction : GoPointerButton
    {
        internal Action action;
        internal Material ownedMaterial;
        internal Mesh ownedMesh;
        internal void SetTint(Color tint)
        {
            if (ownedMaterial && ownedMaterial.HasProperty("_Color")) ownedMaterial.color = tint;
        }
        public override void OnActivate() { if (!unclickable) action?.Invoke(); }
        private void OnDestroy() { if (ownedMaterial) Destroy(ownedMaterial); if (ownedMesh) Destroy(ownedMesh); }
    }
    internal static class BookUI
    {
        internal static readonly Color Ink = Color.black, Warning = Color.black;
        private const float TextScale = .8f;
        internal static string RegionName(int r) => r == 0 ? "Al'Ankh" : r == 1 ? "Emerald" : r == 2 ? "Aestrin" : "Chronos";
        internal static string Credit(double value, int r) => double.IsPositiveInfinity(value) ? "Unlimited" : Math.Floor(value * Loans.Rate(r)).ToString("N0");
        internal static float MeasureLine(TextMesh text, string value)
        {
            if (!text || !text.font) return float.PositiveInfinity;
            text.font.RequestCharactersInTexture(value, text.fontSize, text.fontStyle);
            var fit = text.GetComponent<LoanTextFit>(); float scale = fit ? fit.originalScale.x : text.transform.localScale.x;
            float width = 0;
            foreach (char character in value)
                if (text.font.GetCharacterInfo(character, out var info, text.fontSize, text.fontStyle))
                    width += info.advance * .1f * text.characterSize * scale;
            return width;
        }
        internal static string Money(int amount, string currency) => amount.ToString("N0") + " " + currency;
        internal static bool TryWalletAfterDebit(int before, int debit, out int after)
        {
            long candidate = (long)before - debit;
            if (candidate < int.MinValue || candidate > int.MaxValue) { after = before; return false; }
            after = (int)candidate; return true;
        }
        internal static Vector3 Position(float x, float y, float depth = .022f) => new Vector3(-x, depth, -y);
        internal static TextMesh Text(Transform parent, Font font, string text, float x, float y, float size,
            float width = 0, TextAnchor anchor = TextAnchor.LowerLeft, Color? color = null,
            TextMesh nativeStyle = null, bool keepNativeSize = false)
        {
            var go = new GameObject("Respondentia text"); go.layer = parent.gameObject.layer; go.transform.SetParent(parent, false);
            go.transform.localPosition = Position(x, y);
            go.transform.localRotation = nativeStyle
                ? Quaternion.Inverse(parent.rotation) * nativeStyle.transform.rotation
                : Quaternion.LookRotation(Vector3.down, Vector3.back);
            var parentScale = parent.lossyScale;
            var nativeScale = nativeStyle ? nativeStyle.transform.lossyScale : Vector3.zero;
            go.transform.localScale = nativeStyle && keepNativeSize
                ? new Vector3(nativeScale.x / Mathf.Max(parentScale.x, .0001f), nativeScale.y / Mathf.Max(parentScale.y, .0001f), nativeScale.z / Mathf.Max(parentScale.z, .0001f))
                : Vector3.one * (size * TextScale / 6.4f);
            var t = go.AddComponent<TextMesh>(); t.font = nativeStyle ? nativeStyle.font : font;
            t.fontSize = nativeStyle ? nativeStyle.fontSize : 64; t.characterSize = nativeStyle ? nativeStyle.characterSize : 1;
            t.fontStyle = nativeStyle ? nativeStyle.fontStyle : FontStyle.Normal;
            t.anchor = nativeStyle ? nativeStyle.anchor : anchor;
            t.alignment = nativeStyle ? nativeStyle.alignment : anchor == TextAnchor.LowerRight ? TextAlignment.Right : anchor == TextAnchor.MiddleCenter ? TextAlignment.Center : TextAlignment.Left;
            t.color = color ?? Ink; t.richText = false; t.lineSpacing = nativeStyle ? nativeStyle.lineSpacing : .58f; t.text = text;
            go.GetComponent<MeshRenderer>().sharedMaterial = t.font.material;
            if (width > 0) { var fit = go.AddComponent<LoanTextFit>(); fit.maxWidth = width; fit.originalScale = go.transform.localScale; }
            return t;
        }
        internal static LoanAction Button(Transform parent, MeshRenderer native, Font font, string label,
            float x, float y, float width, float height, Action action, Color tint, out TextMesh text, bool useNativeLabelStyle = false)
        {
            var go = new GameObject("Respondentia " + label); go.layer = native.gameObject.layer; go.transform.SetParent(parent, false);
            var mesh = native.GetComponent<MeshFilter>().sharedMesh;
            var material = new Material(native.sharedMaterial); if (material.HasProperty("_Color")) material.color = tint;
            Vector3 targetCenter = Position(x, y, .012f);
            LoanAction button;
            if (useNativeLabelStyle)
            {
                // The trade-book plate has a rotated transform and an off-center mesh.
                // Clone its full renderer transform and move its mesh center by the native
                // Buy-to-Sell pitch. Fitting projected AABB dimensions distorted the plate.
                Vector3 sourceCenter = parent.InverseTransformPoint(native.transform.TransformPoint(mesh.bounds.center));
                targetCenter.y = sourceCenter.y;
                go.transform.localPosition = parent.InverseTransformPoint(native.transform.position) + targetCenter - sourceCenter;
                go.transform.localRotation = Quaternion.Inverse(parent.rotation) * native.transform.rotation;
                var parentScale = parent.lossyScale; var sourceScale = native.transform.lossyScale;
                go.transform.localScale = new Vector3(sourceScale.x / Mathf.Max(parentScale.x, .0001f),
                    sourceScale.y / Mathf.Max(parentScale.y, .0001f), sourceScale.z / Mathf.Max(parentScale.z, .0001f));
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                var nativeCollider = native.GetComponent<BoxCollider>();
                var collider = go.AddComponent<BoxCollider>();
                if (nativeCollider) { collider.center = nativeCollider.center; collider.size = nativeCollider.size; collider.isTrigger = nativeCollider.isTrigger; }
                else { collider.center = mesh.bounds.center; collider.size = mesh.bounds.size; }
                button = go.AddComponent<LoanAction>();
            }
            else
            {
                // Generic ledger buttons share the native mesh but fit the requested region.
                var frame = native.transform.parent.parent.parent;
                var rotation = Quaternion.Inverse(frame.rotation) * native.transform.rotation;
                var raw = mesh.bounds; var bounds = new Bounds(rotation * raw.center, Vector3.zero);
                for (int i = 0; i < 8; i++) bounds.Encapsulate(rotation * (raw.center + Vector3.Scale(raw.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
                go.transform.localPosition = targetCenter;
                go.transform.localScale = new Vector3(width / bounds.size.x, .006f / Mathf.Max(bounds.size.y, .001f), height / bounds.size.z);
                var surface = new GameObject(label + " bookmark"); surface.layer = go.layer; surface.transform.SetParent(go.transform, false);
                surface.transform.localPosition = -bounds.center; surface.transform.localRotation = rotation;
                surface.AddComponent<MeshFilter>().sharedMesh = mesh;
                surface.AddComponent<MeshRenderer>().sharedMaterial = material;
                var collider = surface.AddComponent<BoxCollider>(); collider.center = raw.center; collider.size = raw.size;
                button = surface.AddComponent<LoanAction>();
            }
            button.action = action; button.ownedMaterial = material; button.lookText = label;
            var nativeText = useNativeLabelStyle ? native.GetComponentInChildren<TextMesh>(true) : null;
            text = Text(parent, font, label, x, y, .019f, width * .94f, TextAnchor.MiddleCenter, null, nativeText, useNativeLabelStyle);
            if (nativeText)
            {
                var frameBounds = InFrameBounds(native, parent);
                Vector3 nativeTextPosition = parent.InverseTransformPoint(nativeText.transform.position);
                Vector3 relativeLabel = nativeTextPosition - frameBounds.center;
                text.transform.localPosition = targetCenter + relativeLabel;
            }
            return button;
        }
        internal static Bounds InFrameBounds(MeshRenderer renderer, Transform frame)
        {
            var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
            var raw = mesh.bounds;
            var bounds = new Bounds(frame.InverseTransformPoint(renderer.transform.TransformPoint(raw.center)), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var corner = raw.center + Vector3.Scale(raw.extents, new Vector3(
                    (i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                bounds.Encapsulate(frame.InverseTransformPoint(renderer.transform.TransformPoint(corner)));
            }
            return bounds;
        }
    }
    public sealed class LoanTextFit : MonoBehaviour
    {
        internal float maxWidth; internal Vector3 originalScale;
        private string last; private TextMesh text;
        private void LateUpdate()
        {
            if (!text) text = GetComponent<TextMesh>();
            if (last == text.text) return; last = text.text;
            transform.localScale = originalScale;
            text.font.RequestCharactersInTexture(text.text, text.fontSize, text.fontStyle);
            float widest = 0, current = 0;
            foreach (char c in text.text)
            {
                if (c == '\n') { widest = Mathf.Max(widest, current); current = 0; }
                else if (text.font.GetCharacterInfo(c, out var info, text.fontSize, text.fontStyle)) current += info.advance * .1f * text.characterSize * originalScale.x;
            }
            widest = Mathf.Max(widest, current);
            if (widest > maxWidth) transform.localScale = originalScale * (maxWidth / widest);
        }
    }
    public sealed class TradeLoanUI : MonoBehaviour
    {
        private EconomyUI ui; private GameObject root; private LoanAction loan; private BankruptcyPrompt bankrupt;
        private TextMesh loanText, info, warning, warningLabel, walletLabel, walletAfter, bankLabel, consequence;
        private float timer;
        private bool quoteInitialized, lastWaterEnabled;
        private int lastPort = -1, lastGood = -1, lastCargoId, lastCurrency = -1, lastGross;
        internal static void Attach(EconomyUI ui) { if (!ui.GetComponent<TradeLoanUI>()) ui.gameObject.AddComponent<TradeLoanUI>().Build(ui); }
        internal void InvalidateQuote() { quoteInitialized = false; timer = 0; }
        private void Build(EconomyUI target)
        {
            ui = target;
            // The affected trade-book view uses per-instance black ink; no font
            // asset or shared material is changed.
            foreach (var text in ui.GetComponentsInChildren<TextMesh>(true)) text.color = BookUI.Ink;
            var source = Fields.Get<EconomyUIButton>(ui, "buyButton").GetComponent<MeshRenderer>();
            var frame = source.transform.parent.parent.parent; // economy UI, the preview's shared book coordinates
            root = new GameObject("Respondentia trade controls"); root.layer = source.gameObject.layer; root.transform.SetParent(frame, false);
            var nativeBuy = BookUI.InFrameBounds(source, frame);
            var nativeSell = BookUI.InFrameBounds(Fields.Get<EconomyUIButton>(ui, "sellButton").GetComponent<MeshRenderer>(), frame);
            // Anchor Sell and reduce both gaps equally. The native Buy label and
            // collider move with its transform; Loan then copies the moved plate.
            const float buttonGap = .007f;
            float buyZ = nativeSell.min.z - buttonGap - nativeBuy.size.z * .5f;
            source.transform.position += frame.TransformVector(Vector3.forward * (buyZ - nativeBuy.center.z));
            nativeBuy = BookUI.InFrameBounds(source, frame);
            float loanY = -nativeBuy.center.z + nativeBuy.size.z + buttonGap;
            var font = Fields.Get<TextMesh>(ui, "textBuyPrice").font;
            loan = BookUI.Button(root.transform, source, font, "Loan", -nativeBuy.center.x, loanY, nativeBuy.size.x, nativeBuy.size.z,
                () => BulkTrade.Execute(ui, TradeAction.Loan, BulkTrade.Quantity(loan)), new Color(.38f, .8f, 1), out loanText, true);
            info = BookUI.Text(root.transform, font, "", .449f, .080f, .013f, .24f, TextAnchor.LowerRight); info.lineSpacing = 1.05f;
            warningLabel = BookUI.Text(root.transform, font, "", .205f, .043f, .012f, .20f, TextAnchor.UpperLeft, Color.red);
            warning = BookUI.Text(root.transform, font, "", .205f, .027f, .012f, .20f, TextAnchor.UpperLeft, Color.black);
            walletLabel = BookUI.Text(root.transform, font, "", .205f, .008f, .012f, .20f, TextAnchor.UpperLeft, Color.red);
            walletAfter = BookUI.Text(root.transform, font, "", .205f, -.008f, .012f, .20f, TextAnchor.UpperLeft, Color.black);
            bankrupt = new BankruptcyPrompt(root.transform, source, font, "Bankruptcy", -.111f, -.032f, .285f, .031f,
                () => { var m = Fields.Get<IslandMarket>(ui, "currentIsland"); return m ? Reputations.Account(m) : 0; }, null, out bankLabel);
            consequence = BookUI.Text(root.transform, font, "", -.111f, -.052f, .012f, .3f, TextAnchor.UpperCenter, BookUI.Warning);
            foreach (var row in Fields.Get<EconomyUIButton[]>(ui, "goodsListButtons"))
            {
                var p = frame.InverseTransformPoint(row.transform.position); float frontY = -p.z;
                p.z = -(.431f + (frontY - .431f) * .88f); row.transform.position = frame.TransformPoint(p);
                var s = row.transform.localScale; s.y *= .88f; row.transform.localScale = s;
            }
            var conversion = Fields.Get<TextMesh>(ui, "textConversionInfo").transform;
            conversion.position += frame.TransformVector(Vector3.back * .041f);
            BulkTradeUI.Attach(ui, loan, loanText);
            root.SetActive(false);
        }
        private void LateUpdate()
        {
            if (!root) return; root.SetActive(ui.uiActive);
            if (!ui.uiActive) bankrupt.Stop(); else bankrupt.Tick();
            if (!ui.uiActive || !World.Ready || !CurrencyMarket.instance || PlayerGold.currency == null) return;
            timer -= Time.unscaledDeltaTime; if (timer > 0) return; timer = .15f;
            var m = Fields.Get<IslandMarket>(ui, "currentIsland"); if (!m) return;
            int good = ui.currentSelectedGood;
            var next = Loans.Next(m, good);
            int cargoId = next ? next.GetComponent<SaveablePrefab>().instanceId : 0;
            int saleCurrency = (int)Fields.Get<Currency>(ui, "currentPlayerCurrency");
            int grossQuote = Loans.SaleProceedsQuote(ui, m, good);
            bool waterEnabled = Settings.WaterEnabled;
            if (!quoteInitialized || lastPort != m.GetPortIndex() || lastGood != good || lastCargoId != cargoId ||
                lastCurrency != saleCurrency || lastGross != grossQuote || lastWaterEnabled != waterEnabled)
            {
                quoteInitialized = true; lastPort = m.GetPortIndex(); lastGood = good; lastCargoId = cargoId;
                lastCurrency = saleCurrency; lastGross = grossQuote; lastWaterEnabled = waterEnabled;
                // OpenUI refreshes its native page before validating the warehouse and
                // refreshing the current-port report. Correct it once the live quote is known.
                ui.RefreshPage();
            }
            int r = Reputations.Account(m);
            var quote = BulkTrade.Quote(ui, TradeAction.Loan, BulkTrade.Quantity(loan));
            bool can = quote.Valid; string reason = quote.Error;
            int payment = Fx.Currency(m);
            info.text = "Respondentia loan\nCredit: " + BookUI.Credit(Loans.Available(r), payment) + " / " + BookUI.Credit(Loans.Limit(r), payment) + " " + PlayerGold.GetCurrencyName(payment) +
                "\n" + (can ? "Interest rate: " + (Rules.Interest(Reputations.Level(r)) * 100).ToString("0") + "%" : reason);
            bankLabel.text = "Bankruptcy (" + BookUI.RegionName(r) + ")";
            var rental = Rentals.Current; bool lender = rental != null && Bottomry.On(rental.ship).Any(l => l.region == r);
            // Two lines (three with a rental); while asking, the question and the short form.
            consequence.text = Loans.BankruptcyRefusal(r) ?? (bankrupt.Asking
                ? "Declare bankruptcy in " + BookUI.RegionName(r) + "?\n" + PlayerGold.GetCurrencyName(r) + " and reputation to 0, its loans cancelled"
                : PlayerGold.GetCurrencyName(r) + " and " + BookUI.RegionName(r) + " reputation to 0\nIts loans cancelled: financed cargo and pledges taken") +
                (lender ? "\nThe rented " + Rentals.Name(rental.ship) + " goes back" : "");
            warningLabel.text = warning.text = walletLabel.text = walletAfter.text = "";
            if (!next || !Fx.TryBookQuote(ui,m.GetPortIndex(),good,false,out _)) return;
            // The shortfalls of the whole batch the Sell button would sell now (1, 5, 10 or 20), by lender currency.
            var sale = BulkTrade.Quote(ui, TradeAction.Sell, BulkTrade.Quantity(Fields.Get<EconomyUIButton>(ui, "sellButton"))); if (!sale.Valid) return;
            var owed = sale.Units.Where(u => u.Shortfall > 0).GroupBy(u => u.DebtCurrency).Select(g => new KeyValuePair<int, long>(g.Key, g.Sum(u => (long)u.Shortfall))).ToArray();
            if (owed.Length == 0) return;
            warningLabel.text = "Loan Shortfall";
            warning.text = string.Join(" + ", owed.Select(o => BookUI.Money((int)Math.Min(int.MaxValue, o.Value), PlayerGold.GetCurrencyName(o.Key))).ToArray());
            walletLabel.text = "Wallet After";
            walletAfter.text = string.Join(" + ", owed.Select(o => o.Value <= int.MaxValue && BookUI.TryWalletAfterDebit(PlayerGold.currency[o.Key], (int)o.Value, out int after)
                ? BookUI.Money(after, PlayerGold.GetCurrencyName(o.Key)) : "Wallet limit exceeded").ToArray());
        }
    }
    public sealed class LedgerUI : MonoBehaviour
    {
        internal int mode; private GameObject page, bottomryPage; private bool showBottomry;
        private TextMesh title, total, pageLabel, allConsequence; private BankruptcyPrompt allBankrupt;
        private TextMesh[] names = new TextMesh[5], details = new TextMesh[5], owed = new TextMesh[5];
        private TextMesh[] creditAvailable = new TextMesh[4], creditLimits = new TextMesh[4], creditStatus = new TextMesh[4];
        private LoanAction[] regionActions = new LoanAction[4];
        private MeshRenderer[] regionPlates = new MeshRenderer[4];
        private TextMesh bottomryTitle, bottomryTotal, bottomryPageLabel;
        private TextMesh[] bottomryNames = new TextMesh[4], bottomryDetails = new TextMesh[4], bottomryOwed = new TextMesh[4], bottomryDays = new TextMesh[4], repayLabels = new TextMesh[4];
        private TextMesh[] termRates = new TextMesh[4], termDays = new TextMesh[4], termStatus = new TextMesh[4];
        private LoanAction[] repayActions = new LoanAction[4], bottomryRegionActions = new LoanAction[4];
        private MeshRenderer[] bottomryRegionPlates = new MeshRenderer[4];
        private readonly BottomryLoan[] shownLoans = new BottomryLoan[4];
        private Material regionNormal, regionSelected;
        private static readonly string[] RegionLabels = { "Al'Ankh", "Emerald + Fire Fish Lagoon", "Aestrin", "Chronos" };
        private static readonly Color RepayTint = new Color(.38f, .8f, 1), Disabled = new Color(.74f, .71f, .66f);
        private readonly Dictionary<Material, Texture> plateTextures = new Dictionary<Material, Texture>();
        private MeshRenderer[] separators = new MeshRenderer[4], bottomrySeparators = new MeshRenderer[3]; private Material dividerMaterial;
        private int selected, pageIndex, bottomryPageIndex;
        private float timer;
        internal static void Attach(MissionListUI ui)
        { if (EconomyUI.instance && !ui.GetComponent<LedgerUI>()) ui.gameObject.AddComponent<LedgerUI>().Build(ui); }
        private void Build(MissionListUI ui)
        {
            var tabs = Fields.Get<GPButtonLogMode[]>(ui, "logModeButtons");
            mode = Math.Max(8, tabs.Max(t => (int)Fields.Get<MissionListMode>(t, "mode")) + 1);
            var original = tabs.First(t => Fields.Get<MissionListMode>(t, "mode") == MissionListMode.currentMissions);
            var tab = Instantiate(original, original.transform.parent);
            // Leave the far-right lower bookmark position free for Economic Events.
            tab.name = "Respondentia Loan bookmark"; tab.transform.localPosition = new Vector3(-.34f, -.012f, .075f);
            Fields.Set(tab, "inactiveLocalPos", tab.transform.localPosition); Fields.Set(tab, "activeLocalPos", tab.transform.localPosition + new Vector3(0, -.002f, .012f));
            Fields.Set(tab, "mode", (MissionListMode)mode); Fields.Set(tab, "missionList", ui);
            tab.GetComponentInChildren<TextMesh>().text = "Loan"; tab.GetComponentInChildren<TextMesh>().color=Color.black;
            Fields.Set(ui, "logModeButtons", tabs.Concat(new[] { tab }).ToArray());
            var frame = Fields.Get<GameObject>(ui, "book").transform.parent;
            var texts = ui.GetComponentsInChildren<TextMesh>(true);
            Font font = texts.Select(t => t.font).FirstOrDefault(f => f && f.name.IndexOf("Architect", StringComparison.OrdinalIgnoreCase) >= 0) ?? original.GetComponentInChildren<TextMesh>().font;
            var trade = EconomyUI.instance;
            var native = Fields.Get<EconomyUIButton>(trade, "buyButton").GetComponent<MeshRenderer>();
            var strip = Fields.Get<EconomyUIButton[]>(trade, "goodsListButtons").First(b => b && b.GetComponent<MeshFilter>()).GetComponent<MeshRenderer>();
            var materials = Fields.Get<Material[]>(trade, "buttonMaterials");
            regionNormal = materials[0]; regionSelected = materials[2];
            var reputation = Fields.Get<GameObject>(ui, "reputationUI");
            Font[] regionFonts = { RegionalFont(reputation, "Callimundial", font), RegionalFont(reputation, "Polo-SemiBold", font), RegionalFont(reputation, "IMMORTAL", font), RegionalFont(reputation, "IMMORTAL", font) };

            // Respondentia: credit by region and the regional cargo loans.
            page = NewPage(frame, "Respondentia Loan Ledger", tab.gameObject.layer);
            var p = page.transform;
            BuildLeft(p, font, regionFonts, strip, native, "Respondentia", "Bottomry >", () => Turn(true), regionActions, regionPlates,
                "Available Regional Credit", "Available\ncredit", "Credit\nlimit", creditAvailable, creditLimits, creditStatus);
            allBankrupt = new BankruptcyPrompt(p, native, font, "Bankruptcy (All regions)", -.065f, -.024f, .33f, .032f, () => -1, Refresh, out _);
            allConsequence = BookUI.Text(p, font, "", -.065f, -.041f, .012f, .36f, TextAnchor.UpperCenter, BookUI.Warning);
            title = BookUI.Text(p, font, "", .235f, .395f, .025f, .35f);
            BookUI.Text(p, font, "Respondentia Loan", .235f, .361f, .025f, .35f);
            total = BookUI.Text(p, font, "", .235f, .334f, .0175f, .31f);
            LedgerOrnament.Create(p, "Respondentia ledger total ornament", .185f, .55f, .318f);
            for (int i = 0; i < 5; i++)
            {
                float y = .270f - i * .067f;
                names[i] = BookUI.Text(p, font, "", .235f, y, .023f, .23f);
                details[i] = BookUI.Text(p, font, "", .235f, y - .0175f, .0132f, .33f);
                owed[i] = BookUI.Text(p, font, "", .235f, y - .035f, .0165f, .30f);
                if (i < 4) separators[i] = BuildDivider(p, .3925f, y - .033f, .315f);
            }
            pageLabel = BookUI.Text(p, font, "", .396f, -.054f, .015f, .13f, TextAnchor.MiddleCenter);
            BookUI.Button(p, native, font, "<", .29f, -.054f, .03f, .024f, () => { pageIndex = Math.Max(0, pageIndex - 1); Refresh(); }, Color.white, out _);
            BookUI.Button(p, native, font, ">", .50f, -.054f, .03f, .024f, () => { pageIndex++; Refresh(); }, Color.white, out _);
            Finish(page);

            // Bottomry: terms by region and the region's ship and house loans, each repayable from here.
            bottomryPage = NewPage(frame, "Bottomry Loan Ledger", tab.gameObject.layer);
            var b = bottomryPage.transform;
            BuildLeft(b, font, regionFonts, strip, native, "Bottomry", "< Respondentia", () => Turn(false), bottomryRegionActions, bottomryRegionPlates,
                "Bottomry Terms", "Interest", "Repay\nwithin", termRates, termDays, termStatus);
            BookUI.Text(b, font, "Repay on loan interface will double the interest\nRepay at exchange box: Free at lending region, " + Math.Round(Rules.BottomryBoxExtra * 100) + "% more interest in others",
                -.04f, -.032f, .012f, .32f, TextAnchor.MiddleCenter, BookUI.Warning);
            bottomryTitle = BookUI.Text(b, font, "", .235f, .395f, .025f, .35f);
            BookUI.Text(b, font, "Bottomry Loans", .235f, .361f, .025f, .35f);
            bottomryTotal = BookUI.Text(b, font, "", .235f, .334f, .0175f, .31f);
            LedgerOrnament.Create(b, "Bottomry ledger total ornament", .185f, .55f, .318f);
            // Rows in the Respondentia format: name, principal + interest (rate), total owed. The days left to the due day,
            // or past it, end the name line; a two-line Repay button with the remote cost sits beside the last two lines.
            for (int i = 0; i < 4; i++)
            {
                int row = i; float y = .270f - i * .080f;
                bottomryNames[i] = BookUI.Text(b, font, "", .235f, y, .023f, .21f);
                bottomryDays[i] = BookUI.Text(b, font, "", .55f, y, .0145f, .095f, TextAnchor.LowerRight);
                bottomryDetails[i] = BookUI.Text(b, font, "", .235f, y - .0175f, .0132f, .215f);
                bottomryOwed[i] = BookUI.Text(b, font, "", .235f, y - .037f, .0165f, .215f);
                repayActions[i] = BookUI.Button(b, native, font, "Repay", .505f, y - .017f, .09f, .034f, () => RepayRow(row), RepayTint, out repayLabels[i]);
                ScaleLabel(repayLabels[i], .72f);
                repayActions[i].lookText = "Repay";
                if (i < 3) bottomrySeparators[i] = BuildDivider(b, .3925f, y - .047f, .315f);
            }
            bottomryPageLabel = BookUI.Text(b, font, "", .396f, -.054f, .015f, .13f, TextAnchor.MiddleCenter);
            BookUI.Button(b, native, font, "<", .29f, -.054f, .03f, .024f, () => { bottomryPageIndex = Math.Max(0, bottomryPageIndex - 1); Refresh(); }, Color.white, out _);
            BookUI.Button(b, native, font, ">", .50f, -.054f, .03f, .024f, () => { bottomryPageIndex++; Refresh(); }, Color.white, out _);
            Finish(bottomryPage);
        }
        private static GameObject NewPage(Transform frame, string name, int layer)
        {
            var go = new GameObject(name); go.layer = layer; go.transform.SetParent(frame, false); return go;
        }
        private static void Finish(GameObject view)
        {
            foreach (var text in view.GetComponentsInChildren<TextMesh>(true)) { text.transform.localPosition += new Vector3(0, .01f, 0); BlackBookText.Apply(text); }
            view.SetActive(false);
        }
        // The left page both ledgers share: title, page-turn button, region selectors and a four-region table with two
        // value columns. A region where no loan can be taken shows why across both columns instead.
        private void BuildLeft(Transform p, Font font, Font[] regionFonts, MeshRenderer strip, MeshRenderer native, string name, string turnLabel, Action turn,
            LoanAction[] actions, MeshRenderer[] plates, string tableTitle, string first, string second, TextMesh[] firstValues, TextMesh[] secondValues, TextMesh[] status)
        {
            BookUI.Text(p, font, name, -.217f, .380f, .026f, .35f);
            BookUI.Text(p, font, "Loan Ledger", -.217f, .350f, .026f, .35f);
            BookUI.Button(p, native, font, turnLabel, .075f, .367f, .13f, .026f, turn, Color.white, out _);
            LedgerOrnament.Create(p, name + " ledger title ornament", -.22f, .145f, .344f);
            for (int i = 0; i < 4; i++)
            {
                int index = i;
                actions[i] = BookUI.Button(p, strip, regionFonts[i], RegionLabels[i], -.065f, .315f - i * .032f, .35f, .025f,
                    () => { selected = index; pageIndex = bottomryPageIndex = 0; Refresh(); }, Color.white, out _);
                plates[i] = actions[i].GetComponent<MeshRenderer>();
            }
            BookUI.Text(p, font, tableTitle, -.217f, .176f, .022f, .39f);
            BookUI.Text(p, font, "Region Name", -.22f, .151f, .0126f, .17f);
            BookUI.Text(p, font, first, .04f, .157f, .0115f, .10f, TextAnchor.LowerRight);
            BookUI.Text(p, font, second, .145f, .157f, .0115f, .10f, TextAnchor.LowerRight);
            BuildDivider(p, -.0375f, .145f, .365f, .00055f);
            for (int i = 0; i < 4; i++)
            {
                float y = .116f - i * .034f;
                BookUI.Text(p, regionFonts[i], i == 1 ? "Emerald + FFL" : RegionLabels[i], -.22f, y + (i == 1 ? .017f : .012f), .0122f, .17f);
                BookUI.Text(p, font, PlayerGold.GetCurrencyName(i), -.22f, y, .0105f, .17f);
                firstValues[i] = BookUI.Text(p, font, "", .04f, y, .0145f, .10f, TextAnchor.LowerRight);
                secondValues[i] = BookUI.Text(p, font, "", .145f, y, .0145f, .10f, TextAnchor.LowerRight);
                status[i] = BookUI.Text(p, font, "", .145f, y, .0125f, .20f, TextAnchor.LowerRight);
                BuildDivider(p, -.0375f, y - .003f, .365f, .00055f);
            }
        }
        private static Font RegionalFont(GameObject reputation, string name, Font fallback)
        {
            // These are the native regional fonts also used by Sail-a-dex.
            var native = reputation ? reputation.GetComponentsInChildren<TextMesh>(true).Select(t => t.font)
                .FirstOrDefault(f => f && f.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) : null;
            return native ? native : Resources.FindObjectsOfTypeAll<Font>()
                .FirstOrDefault(f => f && f.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) ?? fallback;
        }
        private void OnDestroy() { if (dividerMaterial) Destroy(dividerMaterial); }
        private MeshRenderer BuildDivider(Transform parent, float x, float y, float width, float thickness = .0008f)
        {
            var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            if (!shader) return null;
            if (!dividerMaterial) { dividerMaterial = new Material(shader); dividerMaterial.color = Color.black; }
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = "Respondentia loan row divider";
            go.layer = parent.gameObject.layer; go.transform.SetParent(parent, false);
            go.transform.localPosition = BookUI.Position(x, y); go.transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.back);
            go.transform.localScale = new Vector3(width, thickness, .001f);
            var collider = go.GetComponent<Collider>(); if (collider) Destroy(collider);
            var renderer = go.GetComponent<MeshRenderer>(); renderer.sharedMaterial = dividerMaterial;
            return renderer;
        }
        internal void Select(MissionListMode selectedMode)
        {
            if (!page) return;
            bool open = (int)selectedMode == mode;
            page.SetActive(open && !showBottomry); bottomryPage.SetActive(open && showBottomry);
            if (open) Refresh();
        }
        internal void Hide() { if (page) page.SetActive(false); if (bottomryPage) bottomryPage.SetActive(false); }
        internal void Turn(bool bottomry)
        {
            bool open = page.activeSelf || bottomryPage.activeSelf;
            showBottomry = bottomry;
            page.SetActive(open && !bottomry); bottomryPage.SetActive(open && bottomry);
            Refresh();
        }
        private void LateUpdate()
        {
            if (!page) return;
            if (!page.activeInHierarchy) allBankrupt.Stop(); else allBankrupt.Tick();
            if (!page.activeInHierarchy && !bottomryPage.activeInHierarchy) return;
            timer -= Time.unscaledDeltaTime; if (timer <= 0) { timer = .2f; Refresh(); }
        }
        private void Refresh()
        {
            if (!World.Ready || !CurrencyMarket.instance) return;
            allConsequence.text = Loans.BankruptcyRefusal(-1) ?? (allBankrupt.Asking ? "Declare bankruptcy in all regions?\nWallets and reputation to 0, loans cancelled."
                : "All wallets and reputation reset to 0.\nAll loans cancelled; financed cargo and pledges taken.");
            if (showBottomry) RefreshBottomry(); else RefreshRespondentia();
        }
        private void RefreshRespondentia()
        {
            // FFL uses vanilla Emerald currency/reputation and shares that lender pool.
            int r = selected;
            var loans = World.State.cargo.Where(c => c.loan && c.region == r).OrderBy(c => c.id).ToArray();
            int pages = Math.Max(1, (loans.Length + 4) / 5); pageIndex = Mathf.Clamp(pageIndex, 0, pages - 1);
            for (int i = 0; i < 4; i++)
            {
                regionPlates[i].sharedMaterial = i == selected ? regionSelected : regionNormal;
                // Keep eligibility/cooldown information available on hover.
                regionActions[i].description = Loans.BlockReason(i) ?? (i == 1 ? "Shared Emerald and Fire Fish Lagoon loans" : "View regional loans");
                string status = LoanGate.Status(i) ?? (PlayerGold.currency[i] < 0 ? "Negative balance" : null);
                creditStatus[i].text = status ?? "";
                creditAvailable[i].text = status == null ? BookUI.Credit(Loans.Available(i), i) : "";
                creditLimits[i].text = status == null ? BookUI.Credit(Loans.Limit(i), i) : "";
            }
            title.text = r == 1 ? "Emerald + Fire Fish Lagoon" : BookUI.RegionName(r);
            total.text = "Total owed: " + string.Join(" + ", loans.GroupBy(c => Loans.Currency(c)).Select(group => group.Sum(c => (long)c.principal + c.interest).ToString("N0") + " " + PlayerGold.GetCurrencyName(group.Key)).ToArray());
            if(loans.Length==0)total.text="Total owed: 0 "+PlayerGold.GetCurrencyName(r);
            for (int i = 0; i < 5; i++)
            {
                int n = pageIndex * 5 + i; bool exists = n < loans.Length;
                var c = exists ? loans[n] : null;
                names[i].text = exists ? c.name : i == 0 ? "No outstanding loans" : "";
                details[i].text = exists ? "Principal " + c.principal.ToString("N0") + " + interest " + c.interest.ToString("N0") + " (" + (c.rate * 100).ToString("0") + "%)" : "";
                owed[i].text = exists ? "Total owed: " + ((long)c.principal + c.interest).ToString("N0") + " " + PlayerGold.GetCurrencyName(Loans.Currency(c)) : "";
                if (i < separators.Length && separators[i]) separators[i].enabled = exists && n + 1 < loans.Length;
            }
            pageLabel.text = "Page " + (pageIndex + 1) + " / " + pages;
        }
        private void RefreshBottomry()
        {
            int r = selected;
            for (int i = 0; i < 4; i++)
            {
                bottomryRegionPlates[i].sharedMaterial = i == selected ? regionSelected : regionNormal;
                bottomryRegionActions[i].description = LoanGate.Reason(i) ?? "View Bottomry loans";
                string status = LoanGate.Status(i); int level = Reputations.Level(i);
                termStatus[i].text = status ?? "";
                termRates[i].text = status == null ? Bottomry.Percent(Rules.BottomryRate(level)) : "";
                termDays[i].text = status == null ? Rules.BottomryTerm(level) + " days" : "";
            }
            bottomryTitle.text = r == 1 ? "Emerald + Fire Fish Lagoon" : BookUI.RegionName(r);
            var loans = World.State.bottomry.Where(l => l.region == r).OrderBy(l => l.dueDay).ThenBy(l => l.id).ToArray();
            int rows = loans.Length;
            bottomryTotal.text = "Total owed: " + (loans.Length == 0 ? "0 " + PlayerGold.GetCurrencyName(r) : string.Join(" + ",
                loans.GroupBy(l => l.currency).OrderBy(g => g.Key).Select(g => g.Sum(l => (long)Bottomry.Owed(l)).ToString("N0") + " " + PlayerGold.GetCurrencyName(g.Key)).ToArray()));
            int pages = Math.Max(1, (rows + 3) / 4); bottomryPageIndex = Mathf.Clamp(bottomryPageIndex, 0, pages - 1);
            for (int i = 0; i < 4; i++)
            {
                int n = bottomryPageIndex * 4 + i;
                var l = shownLoans[i] = n < loans.Length ? loans[n] : null;
                if (l != null)
                {
                    int owedNow = Bottomry.Owed(l), cost = Bottomry.RemoteCost(l), left = l.dueDay - GameState.day; string currency = PlayerGold.GetCurrencyName(l.currency);
                    bool afford = PlayerGold.currency[l.currency] >= cost;
                    bottomryNames[i].text = Bottomry.Name(l.pledge);
                    bottomryDetails[i].text = "Principal " + l.principal.ToString("N0") + " + interest " + (owedNow - l.principal).ToString("N0") + " (" + Bottomry.Percent(Bottomry.Rate(l)) + ")";
                    bottomryOwed[i].text = "Total owed: " + owedNow.ToString("N0") + " " + currency;
                    bottomryDays[i].text = left > 0 ? LoanGate.Days(left) + " left" : left == 0 ? "Due today" : LoanGate.Days(-left) + " late";
                    repayLabels[i].text = "Repay\n" + cost.ToString("N0");
                    SetEnabled(repayActions[i], afford);
                    repayActions[i].description = afford ? "Pay " + cost.ToString("N0") + " " + currency + " now (double interest)" : "Not enough " + currency;
                }
                else { bottomryNames[i].text = i == 0 && rows == 0 ? "No Bottomry loans" : ""; bottomryDetails[i].text = bottomryOwed[i].text = bottomryDays[i].text = ""; }
                repayActions[i].transform.parent.gameObject.SetActive(l != null); repayLabels[i].gameObject.SetActive(l != null);
                if (i < bottomrySeparators.Length && bottomrySeparators[i]) bottomrySeparators[i].enabled = n + 1 < rows;
            }
            bottomryPageLabel.text = "Page " + (bottomryPageIndex + 1) + " / " + pages;
        }
        private void RepayRow(int row) { if (Bottomry.RepayRemote(shownLoans[row])) Refresh(); }
        // Two label lines on one plate need a smaller font than BookUI.Button gives.
        private static void ScaleLabel(TextMesh label, float factor)
        {
            label.transform.localScale *= factor; var fit = label.GetComponent<LoanTextFit>();
            if (fit) { fit.originalScale = label.transform.localScale; fit.maxWidth *= factor; }
        }
        // A button that can't be used shows as a plain grey plate (the plate texture alone keeps it green under any tint).
        private void SetEnabled(LoanAction button, bool on)
        {
            button.unclickable = !on; var material = button.ownedMaterial; if (!material) return;
            if (!plateTextures.TryGetValue(material, out var texture)) plateTextures[material] = texture = material.mainTexture;
            material.mainTexture = on ? texture : null; button.SetTint(on ? RepayTint : Disabled);
        }
    }
    [HarmonyPatch(typeof(EconomyUI), "Awake")] internal static class TradeLoanAttachPatch { static void Postfix(EconomyUI __instance) => TradeLoanUI.Attach(__instance); }
    [HarmonyPatch(typeof(EconomyUI), "OpenUI")] internal static class TradeQuoteOpenPatch
    { static void Postfix(EconomyUI __instance) => __instance.GetComponent<TradeLoanUI>()?.InvalidateQuote(); }
    [HarmonyPatch(typeof(EconomyUI), "CloseUI")] internal static class TradeQuoteClosePatch
    { static void Postfix(EconomyUI __instance) => __instance.GetComponent<TradeLoanUI>()?.InvalidateQuote(); }
    [HarmonyPatch(typeof(MissionListUI), "Start")] internal static class LedgerAttachPatch
    { [HarmonyAfter("com.raddude.sailadex")] static void Postfix(MissionListUI __instance) => LedgerUI.Attach(__instance); }
    [HarmonyPatch(typeof(MissionListUI), "SwitchMode")] internal static class LedgerModePatch
    { [HarmonyAfter("com.raddude.sailadex")] static void Postfix(MissionListUI __instance, MissionListMode mode) => __instance.GetComponent<LedgerUI>()?.Select(mode); }
    [HarmonyPatch(typeof(MissionListUI), "HideUI")] internal static class LedgerHidePatch { static void Postfix(MissionListUI __instance) => __instance.GetComponent<LedgerUI>()?.Hide(); }
    [HarmonyPatch(typeof(MissionListUI), "EnablePortMissionUI")] internal static class LedgerOfficePatch { static void Postfix(MissionListUI __instance) => __instance.GetComponent<LedgerUI>()?.Hide(); }

    // A vector trace of the supplied four-curl ornament, rendered in the book plane.
    // Curl geometry, stroke width and line spacing share one uniform scale. Only the
    // straight middle extends to fit a page, so the curls cannot become flattened.
    internal sealed class LedgerOrnament : MonoBehaviour
    {
        private const float ReferenceScale = .00032f;
        private const float HalfStroke = 2f * ReferenceScale;
        private const float Depth = .022f;
        private Mesh ownedMesh;
        private Material ownedMaterial;

        private void OnDestroy()
        {
            if (ownedMesh) Destroy(ownedMesh);
            if (ownedMaterial) Destroy(ownedMaterial);
        }

        internal static void Create(Transform parent, string name, float left, float right, float centerY)
        {
            var curl = new List<Vector2> { new Vector2(91, 123) };
            Bezier(curl, new Vector2(78, 123), new Vector2(69, 116), new Vector2(69, 105));
            Bezier(curl, new Vector2(69, 98), new Vector2(74, 91), new Vector2(81, 91));
            Bezier(curl, new Vector2(87, 91), new Vector2(93, 96), new Vector2(93, 102));
            Bezier(curl, new Vector2(93, 107), new Vector2(89, 111), new Vector2(84, 111));

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            foreach (float mirrorY in new[] { 1f, -1f })
            {
                var path = new List<Vector2>();
                for (int i = curl.Count - 1; i >= 0; i--)
                    path.Add(Map(curl[i], left, 1, centerY, mirrorY));
                // One perfectly straight span connects the two complete curls.
                for (int i = 0; i < curl.Count; i++)
                    path.Add(Map(curl[i], right, -1, centerY, mirrorY));
                Stroke(path, vertices, triangles);
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds(); mesh.RecalculateNormals();
            var go = new GameObject(name) { layer = parent.gameObject.layer };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(Shader.Find("Unlit/Color")) { color = Color.black };
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            var owner = go.AddComponent<LedgerOrnament>();
            owner.ownedMesh = mesh; owner.ownedMaterial = material;
        }

        private static Vector2 Map(Vector2 point, float edgeX, float mirrorX, float centerY, float mirrorY)
        {
            // The reference's outer stroke edges are x=67 and y=89/170.
            return new Vector2(edgeX + mirrorX * (point.x - 67) * ReferenceScale,
                centerY + mirrorY * (129.5f - point.y) * ReferenceScale);
        }

        private static void Bezier(List<Vector2> points, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            Vector2 p0 = points[points.Count - 1];
            for (int step = 1; step <= 24; step++)
            {
                float t = step / 24f, u = 1f - t;
                points.Add(u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3);
            }
        }

        private static void Stroke(List<Vector2> path, List<Vector3> vertices, List<int> triangles)
        {
            int first = vertices.Count;
            for (int i = 0; i < path.Count; i++)
            {
                Vector2 tangent = (path[Mathf.Min(i + 1, path.Count - 1)] - path[Mathf.Max(0, i - 1)]).normalized;
                Vector2 offset = new Vector2(-tangent.y, tangent.x) * HalfStroke;
                // Transform both the point and its offset using the same mapping.
                vertices.Add(Position(path[i] + offset)); vertices.Add(Position(path[i] - offset));
                if (i == 0) continue;
                int a = first + (i - 1) * 2;
                DoubleSidedTriangle(triangles, a, a + 1, a + 2);
                DoubleSidedTriangle(triangles, a + 1, a + 3, a + 2);
            }
            RoundCap(path[0], vertices, triangles);
            RoundCap(path[path.Count - 1], vertices, triangles);
        }

        private static void RoundCap(Vector2 center, List<Vector3> vertices, List<int> triangles)
        {
            int first = vertices.Count; vertices.Add(Position(center));
            const int steps = 16;
            for (int i = 0; i < steps; i++)
            {
                float angle = i * Mathf.PI * 2f / steps;
                vertices.Add(Position(center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * HalfStroke));
            }
            for (int i = 0; i < steps; i++) DoubleSidedTriangle(triangles, first, first + i + 1, first + (i + 1) % steps + 1);
        }

        private static Vector3 Position(Vector2 point) => BookUI.Position(point.x, point.y, Depth);

        private static void DoubleSidedTriangle(List<int> triangles, int a, int b, int c)
        {
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
            triangles.Add(c); triangles.Add(b); triangles.Add(a);
        }
    }

    // The running rental (one at a time).
    [Serializable] public sealed class RentalState
    {
        public int ship, region, currency, startDay, charges, deposit, daily, lastDay;
    }

    // How clean a hull is: the share of its dirt texture's pixels that carry (almost) no dirt. The dirt is the alpha
    // channel: one day's coat is about 0.16, a shipyard cleaning sets it to 0.
    internal static class RentalDirt
    {
        internal const byte CleanPixel = 13;
        internal static Color32[] Pixels(Texture texture)
        {
            if (texture is Texture2D readable && readable.isReadable) return readable.GetPixels32();
            var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active; Texture2D copy = null;
            try
            {
                Graphics.Blit(texture, target); RenderTexture.active = target;
                copy = new Texture2D(texture.width, texture.height, TextureFormat.ARGB32, false);
                copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); copy.Apply();
                return copy.GetPixels32();
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); if (copy) UnityEngine.Object.Destroy(copy); }
        }
        internal static double CleanShare(Texture texture)
        {
            if (!texture) return 1;
            var pixels = Pixels(texture); if (pixels.Length == 0) return 1;
            int clean = 0; foreach (var p in pixels) if (p.a <= CleanPixel) clean++;
            return (double)clean / pixels.Length;
        }
    }

    // ---- Ship rental ----
    internal static class Rentals
    {
        internal enum Ending { Unpaid, Sank, Lender }
        internal struct Bill
        {
            internal int days, rent, repair, clean, deposit;
            internal int Total => rent + repair + clean - deposit;
        }
        internal static RentalState Current => World.State.rental;
        internal static PurchasableBoat Ship() => Current != null ? Pledges.Find(Current.ship) : null;
        internal static bool Is(PurchasableBoat b) => b && Current != null && Pledges.Index(b) == Current.ship;
        internal static bool IsIndex(int index) => Current != null && Current.ship == index;
        internal static int RegionOf(PurchasableBoat b) => Is(b) ? Current.region : -1;
        internal static string Name(int index) => Pledges.Name(index);
        internal static string Coin(int currency) => currency == 0 ? "Lions" : currency == 1 ? "Dragons" : currency == 2 ? "Crowns" : "Gold";
        internal static string Money(int amount, int currency) => amount.ToString("N0") + " " + Coin(currency);

        // Her home port: the port of her home cleats, or the port nearest her berth (other mods' ships without cleats, or
        // cleats on an island without a port).
        private static readonly Dictionary<PurchasableBoat, Port> homes = new Dictionary<PurchasableBoat, Port>();
        internal static Port HomePort(PurchasableBoat b)
        {
            if (!b || Port.ports == null) return null;
            if (homes.TryGetValue(b, out var known) && known) return known;
            Port port = null; var ropes = b.GetComponent<BoatMooringRopes>();
            if (ropes) foreach (var cleat in new[] { ropes.mooringFront, ropes.mooringBack }) if (!port && cleat) port = Pledges.PortOf(Pledges.Island(cleat));
            if (!port)
            {
                var at = Pledges.Berth(b, out var berth, out _) ? berth : b.transform.position;
                port = Port.ports.Where(p => p && p.portIndex != 7).OrderBy(p => (p.transform.position - at).sqrMagnitude).FirstOrDefault();
            }
            if (port) homes[b] = port;
            return port;
        }
        internal static int HomeRegion(PurchasableBoat b) { var port = HomePort(b); return port ? Reputations.Account(port) : -1; }
        internal static int Currency(int region) => Bottomry.LocalCurrency(region);
        internal static double Value(PurchasableBoat b, int currency) => Rules.RentalValue(b.price, Fx.State.smooth[3], Fx.State.smooth[currency]);

        internal static bool HasCleats(PurchasableBoat b) { var r = b ? b.GetComponent<BoatMooringRopes>() : null; return r && r.mooringFront && r.mooringBack; }
        // One of her ropes is tied to this cleat.
        internal static bool Tied(PurchasableBoat b, Transform cleat)
        {
            var ropes = b ? b.GetComponent<BoatMooringRopes>() : null; var button = cleat ? cleat.GetComponent<GPButtonDockMooring>() : null;
            return ropes && button && button.spring && ropes.ropes.Any(r => r && r.IsMoored() && Fields.Get<SpringJoint>(r, "mooredToSpring") == button.spring);
        }
        internal static bool AtHomeCleats(PurchasableBoat b) { var r = b.GetComponent<BoatMooringRopes>(); return HasCleats(b) && Tied(b, r.mooringFront) && Tied(b, r.mooringBack); }
        internal static bool NearBerth(PurchasableBoat b, float metres) => Pledges.Berth(b, out var p, out _) && (b.transform.position - p).sqrMagnitude < metres * metres;
        // Back where she was rented: tied to both home cleats (either way round); a ship without home cleats is anchored
        // or tied within 25 m of her berth.
        internal static bool Moored(PurchasableBoat b)
        {
            if (!b) return false;
            if (HasCleats(b)) return AtHomeCleats(b);
            var ropes = b.GetComponent<BoatMooringRopes>();
            return NearBerth(b, 25) && ropes && (ropes.AnyRopeMoored() || ropes.anchor && ropes.anchor.IsSet());
        }
        // For rent: a ship (not a house) for sale at her berth (one without home cleats within the 25 m she may be returned
        // at), not seized from the player, worth something, and not one that sank rented and pledged.
        internal static bool ForRent(PurchasableBoat b) => Pledges.IsShip(b) && !Pledges.Owned(b) && b.price > 0 && Bottomry.Seized(Pledges.Index(b)) == null &&
            !World.State.unrentable.Contains(Pledges.Index(b)) && !Pledges.Sunk(b) && NearBerth(b, HasCleats(b) ? 15 : 25) && Pledges.SaleButton(b) && HomeRegion(b) >= 0;

        // Why this ship can't be rented now (null: it can).
        internal static string Refusal(PurchasableBoat b)
        {
            if (!World.Ready || World.SaveBlocked) return "Economy unavailable";
            if (!ForRent(b)) return "Not for rent";
            int region = HomeRegion(b), level = Reputations.Level(region);
            if (level < Rules.RentalLevel) return "Needs " + BookUI.RegionName(region) + " reputation " + Rules.RentalLevel + " (you have " + level + ")";
            if (Current != null) return "You already rent the " + Name(Current.ship);
            if (PlayerGold.currency.Any(c => c < 0)) return "No rentals while your money is negative";
            int currency = Currency(region); double value = Value(b, currency);
            if (Rules.RentalDaily(value) <= 0) return "The " + Name(Pledges.Index(b)) + " can't be rented";
            int deposit = Rules.RentalDeposit(value);
            if (PlayerGold.currency[currency] < deposit) return "Not enough " + Coin(currency) + " for the deposit (need " + deposit.ToString("N0") + ")";
            return null;
        }
        // Renting: the deposit is paid, she is made usable as a purchase would (without the price or a purchase entry),
        // repaired and cleaned; the deposit and daily rent are fixed now.
        internal static bool Rent(PurchasableBoat b)
        {
            Fx.AdvanceDay(GameState.day);
            var refusal = Refusal(b); if (refusal != null) { Loans.Notify(refusal); return false; }
            int index = Pledges.Index(b), region = HomeRegion(b), currency = Currency(region); double value = Value(b, currency);
            int deposit = Rules.RentalDeposit(value), daily = Rules.RentalDaily(value);
            Spend(currency, deposit);
            b.GetComponent<SaveableObject>().extraSetting = true;
            var sign = Fields.Get<GameObject>(b, "purchaseUI"); if (sign) sign.SetActive(false);
            ResetJibs(b); Restore(b);
            World.State.rental = new RentalState { ship = index, region = region, currency = currency, startDay = GameState.day, deposit = deposit, daily = daily, lastDay = GameState.day };
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayGoldSound();
            return true;
        }
        // As the game's purchase does in 0.39: every jib hinge back to rest (an empty mast slot is skipped).
        private static void ResetJibs(PurchasableBoat b)
        {
            var refs = b.GetComponent<BoatRefs>(); if (!refs || refs.masts == null) return;
            foreach (var mast in refs.masts)
                if (mast && mast.sails != null)
                    foreach (var sail in mast.sails) { var jib = sail ? sail.GetComponent<JibAngleMaster>() : null; if (jib) jib.ResetHingeRestingRot(); }
        }
        // Repaired, emptied and cleaned, as the shipyard leaves a hull.
        internal static void Restore(PurchasableBoat b)
        {
            var damage = b.GetComponent<BoatDamage>(); if (damage) { damage.hullDamage = 0; damage.waterLevel = 0; }
            var dirt = b.GetComponent<SaveableObject>().GetCleanable(); if (dirt) dirt.CleanFully();
        }

        // ---- The bill ----
        internal static float Rate(int currency) => CurrencyMarket.instance ? CurrencyMarket.instance.currentPrices[currency] : (float)Fx.State.rates[currency];
        internal static bool Repaired(PurchasableBoat b) { var d = b.GetComponent<BoatDamage>(); return !d || d.hullDamage <= 0; }
        internal static int RepairFee(PurchasableBoat b, int currency) { var d = b.GetComponent<BoatDamage>(); return d ? Rules.RepairFee(b.price, Rate(currency), d.hullDamage, d.waterUnitsCapacity) : 0; }
        internal static bool Dirtable(PurchasableBoat b) => b.GetComponent<SaveableObject>().GetCleanable();
        internal static double CleanShare(PurchasableBoat b) { var dirt = b.GetComponent<SaveableObject>().GetCleanable(); return dirt ? RentalDirt.CleanShare(dirt.GetCurrentDirtTex()) : 1; }
        internal static bool Clean(PurchasableBoat b) => !Dirtable(b) || CleanShare(b) >= Rules.RentalCleanShare;
        internal static int CleaningFee(PurchasableBoat b, int currency) => Dirtable(b) ? Rules.CleaningFee(b.price, Rate(currency)) : 0;
        internal static int DaysDue => Rules.RentalDays(Current.startDay, GameState.day, Current.charges);
        // Rent for the days given, double the repair if the hull isn't at 100%, double the cleaning if it is less than
        // 95% clean, the deposit credited.
        internal static Bill BillFor(PurchasableBoat b, int days)
        {
            var r = Current;
            return new Bill { days = days, rent = checked(days * r.daily), repair = Repaired(b) ? 0 : 2 * RepairFee(b, r.currency),
                clean = Clean(b) ? 0 : 2 * CleaningFee(b, r.currency), deposit = r.deposit };
        }

        // ---- Money ----
        private static void Spend(int currency, int amount)
        {
            if (amount <= 0) return;
            PlayerGold.currency[currency] -= amount;
            if (DayLogs.instance) DayLogs.instance.dayLogs[currency].LogTransaction(-amount, TransactionCategory.boat);
            if (MoneyNotification.instance) MoneyNotification.instance.PlayNotif(-amount, currency);
        }
        private static void Receive(int currency, int amount)
        {
            if (amount <= 0) return;
            PlayerGold.currency[currency] += amount;
            if (DayLogs.instance) DayLogs.instance.dayLogs[currency].LogTransaction(amount, TransactionCategory.boat);
            if (MoneyNotification.instance) MoneyNotification.instance.PlayNotif(amount, currency);
        }
        // A bill is paid from the rental currency even below 0 (a shortfall costs reputation like a Respondentia shortfall);
        // a negative bill is paid out. Other coins are never touched.
        private static void Settle(RentalState r, int total)
        {
            if (total > 0) Loans.Debit(r.region, total, r.currency, TransactionCategory.boat);
            else Receive(r.currency, -total);
        }
        // When the rental ends, every Bottomry loan on her is charged to the player in its own currency, even below 0
        // (Bottomry.Charge).
        private static void ChargeLoans(int index)
        {
            foreach (var l in Bottomry.On(index).ToArray()) { World.State.bottomry.Remove(l); Bottomry.Charge(l, Bottomry.Owed(l)); }
        }

        // ---- Returning ----
        internal static string ReturnRefusal(PurchasableBoat b)
        {
            if (!World.Ready || World.SaveBlocked) return "Economy unavailable";
            if (!Is(b)) return "Not your rental";
            var lenders = Bottomry.On(Current.ship).Select(l => BookUI.RegionName(l.region)).Distinct().ToArray();
            if (lenders.Length > 0) return "Pledged in " + string.Join(", ", lenders) + ": repay the loan first";
            if (!Moored(b)) return HasCleats(b) ? "Moor her at the two red cleats of her berth" : "Anchor or tie her up at her berth";
            return null;
        }
        internal static bool Return(PurchasableBoat b)
        {
            Fx.AdvanceDay(GameState.day);
            var refusal = ReturnRefusal(b); if (refusal != null) { Loans.Notify(refusal); return false; }
            var r = Current; var bill = BillFor(b, DaysDue);
            Settle(r, bill.Total);
            Pledges.Unown(b); Restore(b);
            // A ship without home cleats is returned anchored or tied near her berth: she is put exactly on it out of sight.
            if (!HasCleats(b) && !NearBerth(b, 1)) Pledges.HomeLater(b);
            World.State.rental = null;
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayGoldSound();
            return true;
        }
        // ---- Forced ends ----
        // The rental ends without the player: the bill is charged, she is handed back and goes home to her berth, repaired
        // and cleaned; the player is moved only if aboard her (ForcedMove).
        private static void ForcedEnd(PurchasableBoat b, Ending ending, int days, List<ShipItem> ghosts, string notice)
        {
            var r = Current; string after = "";
            if (b)
            {
                Settle(r, BillFor(b, days).Total);
                if (PlayerGold.currency[r.currency] < 0) after = ". " + Coin(r.currency) + ": " + PlayerGold.currency[r.currency].ToString("N0");
            }
            World.State.rental = null;
            RentalPanel.Close();
            if (b)
            {
                bool aboard = ForcedMove.Aboard(b);
                Pledges.Unown(b);
                ForcedMove.Queue(aboard, b, ending == Ending.Sank, true, ghosts);
            }
            Loans.Notify(notice + after);
        }
        // Midnight: a reminder the day before the rent is due; on the 20th, 40th... day the rent is taken, and if the
        // rental currency can't cover it the rental ends.
        internal static void AdvanceDay(int day)
        {
            var r = Current;
            if (r == null || !World.Ready || World.Loading || World.SaveBlocked || Startup.BlocksGameplay || GameState.currentlyLoading) return;
            if (day > r.lastDay)
            {
                r.lastDay = day;
                if (day == Rules.NextRentDay(r.startDay, r.charges) - 1) Loans.Notify("Rent due tomorrow: " + Money(Rules.RentalPeriod * r.daily, r.currency) + " (" + Name(r.ship) + ")");
            }
            if (!GameState.playing || GameState.recovering || ForcedMove.Moving || GameState.currentShipyard) return;
            var b = Ship(); if (!b) return; // a ship missing from the scene ends the rental when a save loads
            while (Current != null && day >= Rules.NextRentDay(Current.startDay, Current.charges))
            {
                r = Current; int due = checked(Rules.RentalPeriod * r.daily);
                if (PlayerGold.currency[r.currency] >= due)
                {
                    Spend(r.currency, due); r.charges++;
                    Loans.Notify("Rent paid: " + Money(due, r.currency) + " (" + Name(r.ship) + ")");
                    continue;
                }
                ChargeLoans(r.ship);
                ForcedEnd(b, Ending.Unpaid, Math.Max(Rules.RentalPeriod, DaysDue), null, "Couldn't pay the rent: the " + Name(r.ship) + " was taken back");
            }
        }
        // She sank: the game's recovery fee from every wallet above 0, then her Bottomry loans (charged, 2 levels per lending
        // region, the loan pause, never pledged or rented again), then the rental bill.
        internal static void Sank(SaveableObject hull, List<ShipItem> ghosts)
        {
            var b = hull.GetComponent<PurchasableBoat>(); int index = hull.sceneIndex;
            int percent = Recovery.GetRecoveryPercentageCost();
            for (int c = 0; c < 4; c++)
            {
                if (PlayerGold.currency[c] <= 0) continue;
                int fee = Mathf.RoundToInt(PlayerGold.currency[c] * (percent * .01f)); if (fee <= 0) continue;
                PlayerGold.currency[c] -= fee;
                if (DayLogs.instance) DayLogs.instance.dayLogs[c].LogRecovery(-fee);
            }
            var levels = Reputations.Levels(Bottomry.On(index).Select(l => l.region));
            if (levels.Count > 0)
            {
                ChargeLoans(index);
                Reputations.Sink(levels);
                if (!World.State.burned.Contains(index)) World.State.burned.Add(index);
                if (!World.State.unrentable.Contains(index)) World.State.unrentable.Add(index);
                LoanGate.StartPause();
            }
            PlayerNeeds.water = 100; PlayerNeeds.food = 100; PlayerNeeds.foodDebt = 100; PlayerNeeds.sleepDebt = 100;
            PlayerNeeds.sleep = 100; PlayerNeeds.vitamins = 100; PlayerNeeds.protein = 100;
            ForcedEnd(b, Ending.Sank, DaysDue, ghosts, "The " + Name(index) + " sank and was taken back");
        }
        // A bankrupt lender's loans on her take back what they paid out (as on any pledge) and are forgiven; the lender takes
        // her back (never pledged again), her other loans are charged and the rental ends with its bill. Bankruptcy in her
        // home region, or in all regions, is refused while she is rented (Loans.BankruptcyRefusal).
        internal static void Bankruptcy(int region)
        {
            var r = Current; if (r == null) return;
            var forgiven = Bottomry.On(r.ship).Where(l => l.region == region).ToArray(); if (forgiven.Length == 0) return;
            foreach (var l in forgiven) { Bottomry.TakeBack(l); World.State.bottomry.Remove(l); }
            if (!World.State.burned.Contains(r.ship)) World.State.burned.Add(r.ship);
            ChargeLoans(r.ship);
            ForcedEnd(Ship(), Ending.Lender, DaysDue, null, "Bankruptcy in " + BookUI.RegionName(region) + ": the lender took the " + Name(r.ship) + " back");
        }
        // A lender takes the pledged rental (day 11, or short at a box): the rest of that loan and every other loan on her are
        // charged, and the rental ends with its bill.
        internal static void TakenByLender(PurchasableBoat b, BottomryLoan l, int rest)
        {
            int index = Pledges.Index(b);
            Bottomry.Charge(l, rest); ChargeLoans(index);
            ForcedEnd(b, Ending.Lender, DaysDue, null, "The " + BookUI.RegionName(l.region) + " lender took the " + Name(index) + " back");
        }
        // After a load: the rented hull came back as owned (the game saved it so); a rental whose ship is gone ends.
        internal static void Loaded()
        {
            homes.Clear();
            var r = Current; if (r == null) return;
            var b = Ship();
            if (!b) { World.State.rental = null; Plugin.Log.LogWarning("The rented ship " + r.ship + " is not in the scene; the rental ended."); return; }
            b.GetComponent<SaveableObject>().extraSetting = true;
            var sign = Fields.Get<GameObject>(b, "purchaseUI"); if (sign) sign.SetActive(false);
        }
    }

    // ---- The small scroll on each ship: "Boat rental", or "Return boat" when she is back at her berth ----
    public sealed class RentalSign : MonoBehaviour
    {
        // Above or beside each vanilla ship's sale sign (hull space); other ships get it just above their sign.
        private static readonly Dictionary<int, Vector3> places = new Dictionary<int, Vector3>
        {
            { 10, new Vector3(-.082f, 4.067f, 2.117f) }, { 20, new Vector3(-.067f, 4.498f, 4.387f) }, { 30, new Vector3(-.045f, 4.067f, 2.035f) },
            { 90, new Vector3(.104f, 3.681f, 3.248f) }, { 80, new Vector3(-.012f, 4.326f, 11.724f) }, { 70, new Vector3(0, 5.494f, -10.442f) },
            { 40, new Vector3(.7065f, 2.237f, -.2875f) }, { 50, new Vector3(-.7113f, 4.047f, 6.1093f) },
        };
        private static float poll;
        internal PurchasableBoat boat; internal GameObject scroll; internal TextMesh text, label; internal LoanAction button;
        private GPButtonDockMooring[] marked = new GPButtonDockMooring[0];
        private float timer; private bool occupiedNoted;

        // Every ship gets its scroll once her sale sign exists (other mods add ships after the scene loads); berths are
        // recorded the same way.
        internal static void Poll()
        {
            if ((poll -= Time.unscaledDeltaTime) > 0) return; poll = 1;
            Pledges.RecordBerths(); Pledges.TieWaiting(); Pledges.SendHomeLater();
            foreach (var b in Pledges.All())
                if (Pledges.IsShip(b) && !b.GetComponent<RentalSign>() && Fields.Get<GameObject>(b, "purchaseUI"))
                    b.gameObject.AddComponent<RentalSign>().Build(b);
        }
        private void Build(PurchasableBoat b)
        {
            boat = b; var sign = Fields.Get<GameObject>(b, "purchaseUI");
            // Cloned while inactive, so the copy's purchase button never registers itself as the ship's sale sign.
            bool was = sign.activeSelf; sign.SetActive(false);
            scroll = Instantiate(sign, sign.transform.parent);
            sign.SetActive(was);
            scroll.name = "boat rental UI";
            var buy = scroll.GetComponentInChildren<GPButtonPurchaseBoat>(true);
            if (!buy) { Destroy(scroll); scroll = null; return; }
            var plate = buy.gameObject; DestroyImmediate(buy);
            foreach (var extra in plate.GetComponents<Component>()) if (extra && extra.GetType().Name == "Outline") DestroyImmediate(extra);
            button = plate.AddComponent<LoanAction>(); button.action = Open; button.lookText = "Boat rental";
            var texts = scroll.GetComponentsInChildren<TextMesh>(true);
            label = texts.FirstOrDefault(t => t.transform.parent == plate.transform.parent);
            text = texts.FirstOrDefault(t => t != label);
            if (!text || !label) { Destroy(scroll); scroll = null; return; }
            var fit = text.gameObject.AddComponent<LoanTextFit>(); fit.originalScale = text.transform.localScale; fit.maxWidth = .40f;
            var t0 = scroll.transform; t0.localRotation = sign.transform.localRotation; t0.localScale = sign.transform.localScale;
            if (places.TryGetValue(Pledges.Index(b), out var place) && sign.transform.parent == b.transform) t0.localPosition = place;
            else
            {
                var mesh = sign.GetComponent<MeshFilter>();
                float height = (mesh && mesh.sharedMesh ? mesh.sharedMesh.bounds.size.y : .418f) * sign.transform.localScale.y;
                t0.localPosition = sign.transform.localPosition + sign.transform.localRotation * Vector3.up * (height + .06f);
            }
            scroll.SetActive(false);
        }
        private void Open()
        {
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUIClickSound();
            RentalPanel.Open(boat);
        }
        private void Update()
        {
            if (!boat) { Destroy(this); return; }
            if ((timer -= Time.unscaledDeltaTime) > 0) return; timer = .25f;
            // A copy of the ship another mod made (Dangerous Waters' pirates): with no save record she is not for sale or rent.
            if (!boat.GetComponent<SaveableObject>()) { if (scroll) Destroy(scroll); Destroy(this); return; }
            Cleats();
            if (!scroll) return;
            bool rented = World.Ready && Rentals.Is(boat);
            bool show = World.Ready && (rented ? Rentals.Moored(boat) : Rentals.ForRent(boat));
            if (scroll.activeSelf != show) scroll.SetActive(show);
            if (!show) return;
            if (rented) { text.text = "RETURN BOAT\n\nmoored at her berth"; label.text = "Return"; button.lookText = "Return boat"; return; }
            int region = Rentals.HomeRegion(boat), currency = Rentals.Currency(region);
            text.text = Reputations.Level(region) < Rules.RentalLevel ? "BOAT RENTAL\n\nneeds " + BookUI.RegionName(region) + " rep. " + Rules.RentalLevel
                : "BOAT RENTAL\n\n" + Rentals.Money(Rules.RentalDaily(Rentals.Value(boat, currency)), currency) + " a day";
            label.text = "Rent"; button.lookText = "Boat rental";
        }
        // While the player is aboard the rental: her home cleats glow red until one of her ropes is tied there, then in
        // the game's orange-yellow hint colour; with both tied the glow stops. Notes once when another ship holds one.
        private void Cleats()
        {
            var ropes = boat.GetComponent<BoatMooringRopes>();
            bool aboard = World.Ready && Rentals.Is(boat) && !GameState.currentlyLoading && GameState.currentBoat && GameState.currentBoat.IsChildOf(boat.transform) && Rentals.HasCleats(boat);
            var cleats = aboard ? new[] { ropes.mooringFront, ropes.mooringBack }.Select(c => c.GetComponent<GPButtonDockMooring>()).Where(c => c).ToArray() : new GPButtonDockMooring[0];
            foreach (var c in marked) if (c && !cleats.Contains(c)) c.enableRedOutline = false;
            marked = cleats;
            if (cleats.Length == 0) { occupiedNoted = false; return; }
            bool both = cleats.All(c => Rentals.Tied(boat, c.transform));
            foreach (var c in cleats)
            {
                bool tied = Rentals.Tied(boat, c.transform);
                c.enableRedOutline = !both && !tied;
                if (tied && !both) c.FlashOutline(.6f);
            }
            bool occupied = cleats.Any(c => c.spring && c.spring.connectedBody && !Rentals.Tied(boat, c.transform));
            if (occupied && Rentals.NearBerth(boat, 80)) { if (!occupiedNoted) Loans.Notify("Berth occupied: move the other ship"); occupiedNoted = true; }
            else if (!Rentals.NearBerth(boat, 120)) occupiedNoted = false;
        }
        private void OnDisable() { foreach (var c in marked) if (c) c.enableRedOutline = false; marked = new GPButtonDockMooring[0]; }
    }

    // ---- The rental scroll near the player: the terms (Rent) or the bill (Return) ----
    public sealed class RentalPanel : MonoBehaviour
    {
        internal static RentalPanel Instance;
        private GameObject root, rentPage, returnPage; private Transform details;
        private TextMesh title, info, status, note, totalLabel, totalAmount, rule;
        private readonly TextMesh[] leftLabels = new TextMesh[3], leftValues = new TextMesh[3], leftNotes = new TextMesh[3];
        private readonly TextMesh[] rightLabels = new TextMesh[2], rightValues = new TextMesh[2];
        private readonly TextMesh[] billLabels = new TextMesh[4], billAmounts = new TextMesh[4], walletLabels = new TextMesh[2], walletAmounts = new TextMesh[2];
        private LoanAction confirm, close; private TextMesh confirmText, closeText; private Material confirmMaterial; private Color confirmColor;
        private PurchasableBoat boat; private float timer;
        internal static bool IsOpen => Instance && Instance.root && Instance.root.activeSelf;
        internal static void Open(PurchasableBoat b)
        {
            if (!b || !World.Ready) return;
            if (!Instance || !Instance.root)
            {
                var anchor = CurrencyExchangeUI.instance ? CurrencyExchangeUI.instance.transform.parent : null; if (!anchor) return;
                var holder = new GameObject("ship rental UI"); holder.layer = anchor.gameObject.layer; holder.transform.SetParent(anchor, false);
                holder.SetActive(false);
                Instance = holder.AddComponent<RentalPanel>();
                if (!Instance.Build(holder)) { Destroy(holder); Instance = null; return; }
            }
            Instance.Show(b);
        }
        internal static void Close() { if (IsOpen) Instance.Hide(); }

        private bool Build(GameObject holder)
        {
            root = holder;
            var mission = root.transform.parent.Find("mission UI");
            var bgSource = mission ? mission.Find("mission_details_UI_bg") : null; var source = bgSource ? bgSource.Find("details UI") : null;
            var titleSource = source ? source.Find("text name") : null;
            var labelSource = source ? source.Cast<Transform>().FirstOrDefault(t => t.name == "static text") : null;
            // The button's name holds a slash, which Find would read as a path.
            var valueSource = source ? source.Find("text (destination)") : null; var buttonSource = source ? source.Cast<Transform>().FirstOrDefault(t => t.name == "accept/cancel button") : null;
            if (!titleSource || !labelSource || !valueSource || !buttonSource) { Plugin.Log.LogWarning("Rental scroll: mission screen parts not found"); return false; }
            // The mission screen's parchment, without its contents.
            var bg = Instantiate(bgSource.gameObject, root.transform); bg.name = "rental scroll bg";
            foreach (var child in bg.transform.Cast<Transform>().ToArray()) DestroyImmediate(child.gameObject);
            bg.transform.localPosition = new Vector3(-.22f, -.008f, -.167f); bg.transform.localRotation = new Quaternion(-.707107f, 0, 0, .707107f);
            bg.transform.localScale = new Vector3(.658913f, 1, 1); bg.SetActive(true);
            details = new GameObject("details UI").transform; details.gameObject.layer = bg.layer; details.SetParent(bg.transform, false);
            details.localPosition = new Vector3(-.167f, -.005f, -.182f); details.localScale = new Vector3(.409766f, .27f, .270002f);
            rentPage = Group("rent page"); returnPage = Group("return page");
            var small = new Vector3(.0068f, .0085f, .0085f);
            title = Text(titleSource, details, "title", 0, .851f, .621f, null, null, null, 1.5f);
            info = Text(labelSource, rentPage.transform, "info", 0, -.30f, .648f, null, TextAnchor.MiddleCenter, TextAlignment.Center, 1.55f);
            for (int i = 0; i < 3; i++)
            {
                leftLabels[i] = Text(labelSource, rentPage.transform, "label " + i, .676f, .70f - .28f * i, .648f, null, null, null, .62f);
                leftValues[i] = Text(valueSource, rentPage.transform, "value " + i, .676f, .624f - .28f * i, .648f, null, null, null, .62f);
                leftNotes[i] = Text(labelSource, rentPage.transform, "note " + i, .676f, .55f - .28f * i, .648f, small, null, null, .62f);
            }
            for (int j = 0; j < 2; j++)
            {
                rightLabels[j] = Text(labelSource, rentPage.transform, "right label " + j, .02f, .70f - .34f * j, .648f, null, null, null, .85f);
                rightValues[j] = Text(valueSource, rentPage.transform, "right value " + j, .02f, .661f - .34f * j, .648f, null, TextAnchor.UpperLeft, TextAlignment.Left, .85f);
            }
            for (int i = 0; i < 4; i++)
            {
                billLabels[i] = Text(labelSource, returnPage.transform, "bill " + i, .676f, .70f - .10f * i, .648f, null, null, null, .90f);
                billAmounts[i] = Text(valueSource, returnPage.transform, "amount " + i, -.70f, .70f - .10f * i, .648f, null, TextAnchor.MiddleRight, TextAlignment.Right, .40f);
            }
            rule = Text(labelSource, returnPage.transform, "rule", .676f, .33f, .648f, new Vector3(.0084f, .006f, .01f), null, null, 0);
            rule.text = new string('_', 32);
            totalLabel = Text(valueSource, returnPage.transform, "total", .676f, .25f, .648f, null, null, null, .90f);
            totalAmount = Text(valueSource, returnPage.transform, "total amount", -.70f, .25f, .648f, null, TextAnchor.MiddleRight, TextAlignment.Right, .50f);
            for (int i = 0; i < 2; i++)
            {
                walletLabels[i] = Text(labelSource, returnPage.transform, "wallet " + i, .676f, .08f - .10f * i, .648f, null, null, null, .90f);
                walletAmounts[i] = Text(valueSource, returnPage.transform, "wallet amount " + i, -.70f, .08f - .10f * i, .648f, null, TextAnchor.MiddleRight, TextAlignment.Right, .40f);
            }
            note = Text(labelSource, returnPage.transform, "note", .676f, -.16f, .648f, small, null, null, 1.4f);
            status = Text(valueSource, details, "status", 0, -.32f, .648f, null, TextAnchor.MiddleCenter, TextAlignment.Center, 1.55f);
            close = Button(buttonSource, "close", .30f, () => { Click(); Hide(); }, out closeText, out _);
            confirm = Button(buttonSource, "confirm", -.433f, Confirm, out confirmText, out confirmMaterial);
            confirmColor = confirmMaterial.HasProperty("_Color") ? confirmMaterial.color : Color.white;
            closeText.text = "Close"; close.lookText = "Close";
            return true;
        }
        private GameObject Group(string name)
        {
            var go = new GameObject(name); go.layer = details.gameObject.layer; go.transform.SetParent(details, false); return go;
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
        // The mission screen's Accept button: its plate and label, with this mod's action.
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

        private void Show(PurchasableBoat b)
        {
            boat = b;
            if (!root.activeSelf) { MouseLook.ToggleMouseLookAndCursor(false); root.SetActive(true); Refs.SetPlayerControl(false); }
            timer = 0; Refresh();
        }
        private void Hide()
        {
            if (!root.activeSelf) return;
            root.SetActive(false); MouseLook.ToggleMouseLookAndCursor(true); Refs.SetPlayerControl(true); boat = null;
        }
        private void Update()
        {
            if (!root || !root.activeSelf) return;
            if (!boat || !World.Ready) { Hide(); return; }
            if ((timer -= Time.unscaledDeltaTime) > 0) return; timer = .5f;
            Refresh();
        }
        private void Confirm()
        {
            if (!boat) return;
            Click();
            if (Rentals.Is(boat) ? Rentals.Return(boat) : Rentals.Rent(boat)) Hide(); else Refresh();
        }
        private void Enable(bool can)
        {
            confirm.unclickable = !can;
            if (confirmMaterial.HasProperty("_Color")) confirmMaterial.color = can ? confirmColor : new Color(.604f, .604f, .604f, confirmColor.a);
        }
        internal void Refresh()
        {
            if (!boat) return;
            int index = Pledges.Index(boat); string name = Pledges.Name(index);
            bool returning = Rentals.Is(boat);
            rentPage.SetActive(!returning); returnPage.SetActive(returning);
            if (!returning)
            {
                int region = Rentals.HomeRegion(boat), currency = Rentals.Currency(region), level = Reputations.Level(region);
                double value = Rentals.Value(boat, currency);
                title.text = "Rent the " + name;
                leftLabels[0].text = "Deposit:"; leftValues[0].text = Rentals.Money(Rules.RentalDeposit(value), currency); leftNotes[0].text = "refunded on return";
                leftLabels[1].text = "Daily rent:"; leftValues[1].text = Rentals.Money(Rules.RentalDaily(value), currency); leftNotes[1].text = "taken every " + Rules.RentalPeriod + " days";
                leftLabels[2].text = "Needs:"; leftValues[2].text = BookUI.RegionName(region) + " reputation " + Rules.RentalLevel; leftNotes[2].text = "you have level " + level;
                rightLabels[0].text = "To return her:";
                rightValues[0].text = Rentals.HasCleats(boat) ? "moor her at the two\nred cleats of this berth" : "anchor or tie her up\nat this berth";
                rightLabels[1].text = "Not repaired or not clean?"; rightValues[1].text = "you pay double the repair\nand cleaning fee";
                // A refusal takes the info line.
                var refusal = Rentals.Refusal(boat);
                info.text = refusal ?? "If you can't pay the rent when it is due, she is\ntaken home and you are sent to your own ship.";
                status.text = "";
                confirmText.text = "Confirm rent"; confirm.lookText = "Confirm rent"; Enable(refusal == null);
                return;
            }
            var r = Rentals.Current; var bill = Rentals.BillFor(boat, Rentals.DaysDue);
            title.text = "Return the " + name;
            billLabels[0].text = "Rent, " + LoanGate.Days(bill.days) + " x " + r.daily.ToString("N0"); billAmounts[0].text = bill.rent.ToString("N0");
            billLabels[1].text = bill.repair > 0 ? "Repair not done, 2 x " + (bill.repair / 2).ToString("N0") : Rentals.Repaired(boat) ? "Repaired" : "Repair (almost nothing to do)";
            billAmounts[1].text = bill.repair.ToString("N0");
            billLabels[2].text = !Rentals.Dirtable(boat) ? "No hull cleaning needed" : bill.clean > 0 ? "Cleaning not done, 2 x " + (bill.clean / 2).ToString("N0") : "Clean";
            billAmounts[2].text = bill.clean.ToString("N0");
            billLabels[3].text = "Deposit refunded"; billAmounts[3].text = "-" + bill.deposit.ToString("N0");
            int total = bill.Total, have = PlayerGold.currency[r.currency];
            totalLabel.text = total >= 0 ? "Total to pay" : "You receive"; totalAmount.text = Rentals.Money(Math.Abs(total), r.currency);
            walletLabels[0].text = "Your " + PlayerGold.GetCurrencyName(r.currency); walletAmounts[0].text = have.ToString("N0");
            walletLabels[1].text = "After paying"; walletAmounts[1].text = ((long)have - total).ToString("N0");
            note.text = "Items left aboard stay on her.";
            var why = Rentals.ReturnRefusal(boat);
            status.text = why ?? "";
            confirmText.text = "Return"; confirm.lookText = "Return"; Enable(why == null);
        }
    }

    // ---- Shipyard: a rented ship may only be cleaned and repaired ----
    internal static class RentalYard
    {
        internal static readonly string[] Allowed = { "exitShipyard", "confirmOrder", "cancelOrder", "cleanHull", "repair", "scrollOrder" };
        private static readonly List<GameObject> hidden = new List<GameObject>();
        internal const string Notice = "A rented ship: only cleaning and repair";
        internal static bool Limited
        {
            get
            {
                var yard = GameState.currentShipyard; var ship = yard ? yard.GetCurrentBoat() : null;
                return World.Ready && ship && Rentals.Is(ship.GetComponent<PurchasableBoat>());
            }
        }
        // The rig and parts menus and their category buttons are hidden while a rented ship is in the yard.
        internal static void Hide()
        {
            var ui = ShipyardUI.instance; if (!ui || !Limited) return;
            var panel = Fields.Get<GameObject>(ui, "ui");
            var targets = new List<GameObject> { Fields.Get<GameObject>(ui, "sailMenu"), Fields.Get<GameObject>(ui, "newPartsMenu") };
            if (panel) targets.AddRange(panel.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("mode button", StringComparison.Ordinal)).Select(t => t.gameObject));
            foreach (var go in targets) if (go && go.activeSelf) { go.SetActive(false); if (!hidden.Contains(go)) hidden.Add(go); }
        }
        internal static void Restore()
        {
            foreach (var go in hidden) if (go) go.SetActive(true);
            hidden.Clear();
        }
        // Safety net: an order on a rented ship may hold nothing but cleaning, repair and the yard's fee.
        internal static bool OnlyCleanAndRepair(Shipyard yard)
        {
            var refunds = Fields.Get<List<ShipyardRefund>>(yard, "currentRefunds"); if (refunds != null && refunds.Count > 0) return false;
            var parts = Fields.Get<ShipyardPartsInstaller>(yard, "partsInstaller"); var order = parts ? parts.GetCurrentOrder() : null;
            if (order != null && (order.orderTotal != 0 || order.partOrderLines != null && order.partOrderLines.Count > 0)) return false;
            var refs = yard.GetCurrentBoat().GetComponent<BoatRefs>();
            if (refs && refs.masts != null)
                foreach (var mast in refs.masts)
                    if (mast && mast.sails != null && mast.sails.Any(s => s && s.GetComponent<Sail>() && !s.GetComponent<Sail>().IsInstalled())) return false;
            return true;
        }
        // Shipyard Expansion's own buttons (unfurl, textures, sail scale, pages) are blocked too; its other pointer objects
        // (a ladder, a table) are not shipyard controls.
        internal static void PatchOtherYardButtons(Harmony harmony)
        {
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(RentalYard), nameof(BlockOtherButton)));
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == typeof(GoPointerButton).Assembly || assembly == typeof(RentalYard).Assembly) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch (Exception) { continue; }
                foreach (var type in types)
                {
                    if (type == null || !type.IsSubclassOf(typeof(GoPointerButton)) || !(type.Namespace ?? "").StartsWith("ShipyardExpansion", StringComparison.Ordinal) ||
                        !type.Name.EndsWith("Button", StringComparison.Ordinal)) continue;
                    foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if ((method.Name == "OnActivate" || method.Name == "OnAltActivate") && method.ReturnType == typeof(void) && !method.IsAbstract)
                        {
                            try { harmony.Patch(method, prefix); Plugin.Log.LogInfo("Rental shipyard limit covers " + type.FullName + "." + method.Name); }
                            catch (Exception e) { Plugin.Log.LogWarning("Rental shipyard limit could not cover " + type.FullName + "." + method.Name + ": " + e.Message); }
                        }
                }
            }
        }
        private static bool BlockOtherButton()
        {
            if (!Limited) return true;
            Loans.Notify(Notice); return false;
        }
    }
    [HarmonyPatch(typeof(ShipyardButton), "OnActivate")] internal static class RentalYardButtonPatch
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(ShipyardButton __instance)
        {
            if (!RentalYard.Limited) return true;
            if (RentalYard.Allowed.Contains(Fields.Get<object>(__instance, "function").ToString())) return true;
            Loans.Notify(RentalYard.Notice); return false;
        }
    }
    [HarmonyPatch(typeof(ShipyardUI), "ShowUI")] internal static class RentalYardShowPatch { static void Postfix() { if (RentalYard.Limited) RentalYard.Hide(); else RentalYard.Restore(); } }
    [HarmonyPatch(typeof(ShipyardUI), "RefreshButtons")] internal static class RentalYardRefreshPatch { static void Postfix() { if (RentalYard.Limited) RentalYard.Hide(); } }
    [HarmonyPatch(typeof(Shipyard), "DischargeShip")] internal static class RentalYardDischargePatch { static void Postfix() => RentalYard.Restore(); }
    [HarmonyPatch(typeof(Shipyard), "ConfirmOrder")] internal static class RentalYardConfirmPatch
    {
        static bool Prefix(Shipyard __instance)
        {
            if (!RentalYard.Limited || RentalYard.OnlyCleanAndRepair(__instance)) return true;
            Loans.Notify(RentalYard.Notice); __instance.CancelOrder(); return false;
        }
    }
}

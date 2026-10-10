using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        public static int PurchaseDeficit(double cap, int tier)
        {
            if (tier < 1 || tier > 4) return 0;
            double fraction = tier == 1 ? .1 : tier == 2 ? .05 : .02;
            return checked((int)Math.Round(cap * fraction, MidpointRounding.AwayFromZero));
        }
        public static bool PurchaseOriginAllowed(int port, int good)
        {
            return good != 64 || port == 33; // Only saffron retains its purchase-origin restriction.
        }
        public static double FlowFactor(double supply, double production, double cap, bool saffron, double productionDeficitLimit)
        {
            double remaining = Clamp(1 - Math.Abs(supply) / cap, 0, 1);
            if (saffron) return remaining * remaining;
            if (production > 0)
                return supply >= 0 ? remaining * remaining :
                    1 + (productionDeficitLimit > 0 ? Clamp(-supply / productionDeficitLimit, 0, 1) : 1);
            return supply >= 0 ? 1 : remaining * remaining;
        }
        public static double Price(double value, double supply, double cap, double surplus, double deficit, int good = 0)
        {
            double response = PriceCurves.Response(supply, cap, good);
            if (PriceCurves.TryProfile(good, out _))
            {
                surplus = PriceCurves.SurplusMultiplier((float)surplus, good);
                deficit = PriceCurves.DeficitMultiplier((float)deficit, good);
                // Match the native float arithmetic and rounding used by the patched
                // market, including its inverse-response representation.
                float inverse = (float)Math.Sqrt(1-response), r = 1-inverse*inverse;
                float signed = supply >= 0 ? r : -r;
                float original = (float)value;
                // Native GetGoodPriceAtSupply passes a float to Mathf.RoundToInt.
                // Force that same precision before our double-based rounding helper.
                return Round((float)(original-original*(float)(supply >= 0 ? surplus : deficit)*signed));
            }
            return Math.Round(value * (supply >= 0 ? 1 - surplus * response : 1 + deficit * response), MidpointRounding.ToEven);
        }
        public static double Spread(double price) => .005 + (.0001 - .005) * Clamp((price - 1000) / 29000, 0, 1);
        public static int Quote(double value, double supply, double cap, double surplus, double deficit, bool buy, int good = 0)
        {
            double raw = Price(value, supply, cap, surplus, deficit, good);
            return Round(raw * (1 + (buy ? 1 : -1) * Spread(raw)));
        }
        // Cargo mission pay before whole-crate rounding: local 0.2 x profit + 2 x distance, world 0.35 x profit + 1.75 x distance.
        public static float MissionReward(float profit, float distance, bool world) => world ? .35f * profit + 1.75f * distance : .2f * profit + 2f * distance;
    }

    // ---- Port supply and market cycles ----
    internal sealed class SupplyRow { public int Port, Tier, Region; public float[] Production; }
    internal static class Supply
    {
        internal static readonly Dictionary<int, SupplyRow> Rows = new Dictionary<int, SupplyRow>();
        private sealed class CoefficientOffset { internal string Kind; internal int Port, Good; internal float Amount; }
        private static SupplyRow bottleneck;
        private static List<CoefficientOffset> offsets;
        private static Dictionary<int, SupplyRow> effectiveRows;
        // Round decimal data before converting to Unity's float coefficient arrays.
        internal static float ParseCoefficient(string value) =>
            (float)Math.Round(decimal.Parse(value, CultureInfo.InvariantCulture), 2, MidpointRounding.AwayFromZero);
        internal static void Load()
        {
            foreach (var cells in Embedded.Csv("supply.csv"))
            {
                var row = new SupplyRow { Port = int.Parse(cells[0]), Tier = int.Parse(cells[1]), Region = int.Parse(cells[2]), Production = new float[65] };
                for (int i = 0; i < 65; i++) row.Production[i] = ParseCoefficient(cells[i + 3]);
                Rows.Add(row.Port, row);
            }
            if (Rows.Count != 32) throw new InvalidDataException("Expected Revision 37's 31 ports plus Saffron Island");
            {
                string[] cells = Embedded.Csv("bottleneck_supply.csv").FirstOrDefault();
                if (cells == null || cells.Length != 68) throw new InvalidDataException("Bottleneck row must contain 65 coefficients");
                bottleneck = new SupplyRow { Port = int.Parse(cells[0]), Tier = int.Parse(cells[1]), Region = int.Parse(cells[2]), Production = new float[65] };
                for (int i = 0; i < 65; i++) bottleneck.Production[i] = ParseCoefficient(cells[i + 3]);
                if (bottleneck.Port != 67 || bottleneck.Tier != 3 || bottleneck.Region != 1 || bottleneck.Production[45] != 0 || bottleneck.Production[51] != 0)
                    throw new InvalidDataException("Bottleneck row identity or reviewed coefficients changed");
            }
            offsets = new List<CoefficientOffset>();
            foreach (var cells in Embedded.Csv("bottleneck_offsets.csv"))
            {
                var offset = new CoefficientOffset { Kind = cells[0], Port = int.Parse(cells[1]), Good = int.Parse(cells[2]), Amount = ParseCoefficient(cells[3]) };
                if ((offset.Kind != "cut" && offset.Kind != "compensation" && offset.Kind != "compatibility") || offset.Good < 1 || offset.Good >= 65 || offset.Good == 45 || offset.Good == 51 ||
                    offset.Port < 0 || offset.Port >= 34 || !Rows.ContainsKey(offset.Port) || offset.Amount == 0 || float.IsNaN(offset.Amount) || float.IsInfinity(offset.Amount))
                    throw new InvalidDataException("Invalid Bottleneck coefficient offset: " + string.Join(",", cells));
                offsets.Add(offset);
            }
            // The optional scenario must preserve the base world total for each good.
            var delta = bottleneck.Production.Select(value => (double)value).ToArray();
            foreach (var offset in offsets) delta[offset.Good] += offset.Amount;
            for (int good = 1; good < delta.Length; good++)
                if (good != 45 && good != 51 && (double.IsNaN(delta[good]) || double.IsInfinity(delta[good]) || Math.Abs(delta[good]) > .001))
                    throw new InvalidDataException("Bottleneck scenario does not balance good " + good);
            SetBottleneckEnabled(false);
        }
        internal static bool Contains(int port) => effectiveRows != null && effectiveRows.ContainsKey(port);
        internal static bool TryGet(int port, out SupplyRow row)
        {
            row = null; return effectiveRows != null && effectiveRows.TryGetValue(port, out row);
        }
        internal static SupplyRow[] ManagedRows() => effectiveRows.Values.OrderBy(r => r.Port).ToArray();
        internal static void SetBottleneckEnabled(bool enabled)
        {
            var rebuilt = new Dictionary<int, SupplyRow>();
            foreach (var original in Rows.Values)
                rebuilt.Add(original.Port, new SupplyRow { Port = original.Port, Tier = original.Tier, Region = original.Region, Production = (float[])original.Production.Clone() });
            if (enabled)
            {
                foreach (var offset in offsets)
                {
                    var row = rebuilt[offset.Port];
                    row.Production[offset.Good] = (float)Math.Round((double)row.Production[offset.Good] + offset.Amount, 2, MidpointRounding.AwayFromZero);
                    // Recipe adjustments can affect consuming cells as well as exporters.
                    if (float.IsNaN(row.Production[offset.Good]) || float.IsInfinity(row.Production[offset.Good]))
                        throw new InvalidDataException("Invalid Bottleneck result at port " + offset.Port + ", good " + offset.Good);
                }
                rebuilt.Add(bottleneck.Port, new SupplyRow { Port = bottleneck.Port, Tier = bottleneck.Tier, Region = bottleneck.Region, Production = (float[])bottleneck.Production.Clone() });
            }
            effectiveRows = rebuilt;
        }
        internal static void Apply(IslandMarket m)
        {
            if (!TryGet(m.GetPortIndex(), out var row)) return;
            // Do not resize or renumber native commodity arrays.
            Array.Copy(row.Production, m.production, Math.Min(m.production.Length, row.Production.Length));
            m.goodsSoftCapOverride = Economy.Cap(m.GetPortIndex());
            Economy.ApplyPurchaseLimit(m);
            m.econCycleDuration = .72f; // .72 / .0003 / (.008 * 100) = 3000 real seconds.
        }
    }
    [Serializable] public sealed class MarketState
    {
        public int port;
        public float bonus;
        public double timer = 1, warehouseTimer = 2;
        public int slot, pending;
        public bool blocked;
        public List<ReportState> reports = new List<ReportState>();
        public DepartureState lastDeparture;
        // Reports delivered since the last midnight; merged into reports (and knownPrices) at the day change.
        public List<ReportState> incoming = new List<ReportState>();
        // Trade-book entries this port published at midnight (boats leaving carry these on), the view released at the last
        // midnight (snapshot ids), and the entries delivered since the last midnight.
        public List<BookEntry> book = new List<BookEntry>();
        public List<int> released = new List<int>();
        public List<BookEntry> bookIncoming = new List<BookEntry>();
    }
    internal static partial class Economy
    {
        internal static int BatchDepth;
        internal static float BaseCap(int port)
        {
            if (!Supply.TryGet(port, out var r)) return 100;
            return r.Tier == 0 ? 50 : 250 - 50 * r.Tier;
        }
        internal static float Cap(int port) => Mathf.Min(BaseCap(port) * 2, BaseCap(port) + World.MarketData(port).bonus);
        internal static float PricingCap(IslandMarket m, int good) => good == 64 ? 100 : Cap(m.GetPortIndex());
        internal static void ApplyPurchaseLimit(IslandMarket market)
        {
            int port = market.GetPortIndex();
            if (!Supply.TryGet(port, out var row) || row.Tier < 1 || row.Tier > 4) return;
            // HasGood checks the stock BEFORE subtracting one cargo. This also makes
            // the native available count floor(max(0, stock + allowed deficit)).
            market.supplyPurchaseLimit = 1 - Rules.PurchaseDeficit(Cap(port), row.Tier);
        }
        internal static void Award(int port, float amount)
        {
            if (port == 33 || !Supply.Contains(port)) return;
            var s = World.MarketData(port); s.bonus = Mathf.Min(BaseCap(port), s.bonus + amount);
            var m = World.Market(port); if (m) { m.goodsSoftCapOverride = Cap(port); ApplyPurchaseLimit(m); Refresh(m); }
            PortCapacityUI.RefreshVisible(port);
        }
        internal static void Configure()
        {
            var d = DebugMarketTracker.instance; if (!d) return;
            d.finalMarketSpeed = .0003f; d.missionProfitShareLocal = .05f; d.missionProfitShareWorld = .1f;
            d.missionDistanceFee = .5f; d.missionFinalMult = 2.5f;
            DebugMarketTracker.marketSpeedMult = SimulationClock.NormalSpeed;
            DebugMarketTracker.marketProductionMult = 1.05f;
            DebugMarketTracker.goodsAmountSoftCap = 100;
        }
        internal static void Refresh(IslandMarket m)
        {
            EnsureKnownPrices(m);
            if (m.knownPrices[m.GetPortIndex()] == null) m.knownPrices[m.GetPortIndex()] = new PriceReport();
            m.UpdateSelfPriceReportForPlayer();
        }
        internal static void EnsureKnownPrices(IslandMarket market, int minimumLength = 0, bool clear = false)
        {
            if (!market) return;
            PriceReport[] current = market.knownPrices;
            int length = Math.Max(minimumLength, Math.Max(100, Math.Max(current?.Length ?? 0, Math.Max(Port.ports?.Length ?? 0, market.GetPortIndex() + 1))));
            if (current == null || current.Length < length)
            {
                var expanded = new PriceReport[length];
                if (current != null) Array.Copy(current, expanded, current.Length);
                market.knownPrices = expanded; current = expanded;
            }
            if (clear) Array.Clear(current, 0, current.Length);
        }
        internal static void Cycle(IslandMarket m)
        {
            int port = m.GetPortIndex(); if (!World.Ready || !Supply.TryGet(port, out var row)) return;
            // The mod replaces native EconCycle and uses the full cap, never 0.39's 0.33 factor.
            float portCap = Cap(port);
            int deficitLimit = Rules.PurchaseDeficit(portCap, row.Tier);
            for (int i = 1; i < Math.Min(m.currentSupply.Length, m.production.Length); i++)
            {
                if (i == 45 || i == 51) continue;
                float cap = i == 64 ? (port == 0 || port == 9 || port == 15 ? 100 : 50) : portCap;
                m.currentSupply[i] += m.production[i] * UnityEngine.Random.Range(.1f, .4f) *
                    (float)Rules.FlowFactor(m.currentSupply[i], m.production[i], cap, i == 64, deficitLimit) * (m.production[i] > 0 ? 1.05f : 1);
            }
            Refresh(m);
            var s = World.MarketData(port);
            if (s.blocked) { s.blocked = false; Missions.DrainWarehouse(m, s); }
            if (EconomyUI.instance) EconomyUI.instance.RefreshPage();
        }
        internal static void UpdateMarket(IslandMarket m)
        {
            if (!World.Running) return;
            AdvanceMarket(m, Time.deltaTime * Sun.sun.timescale * 100);
            if (m.debugOpenUI && EconomyUI.instance) { EconomyUI.instance.OpenUI(m); m.debugOpenUI = false; }
        }
        internal static void AdvanceMarket(IslandMarket m, double elapsed)
        {
            if (!Supply.Contains(m.GetPortIndex())) return;
            var s = World.MarketData(m.GetPortIndex());
            SimulationClock.Advance(ref s.timer, elapsed,
                m.econCycleDuration / (double)SimulationClock.NormalSpeed, SimulationClock.CycleBudget, () => Cycle(m));
        }
    }
    [HarmonyPatch(typeof(IslandMarket), "Awake")] internal static class MarketAwakePatch
    {
        static void Postfix(IslandMarket __instance)
        {
            if (__instance.GetPortIndex() == BottleneckCompatibility.PortId) BottleneckCompatibility.Refresh();
            else Supply.Apply(__instance);
        }
    }
    [HarmonyPatch(typeof(IslandMarket), "Start")] internal static class NoVanillaPreheatPatch { static bool Prefix() => false; }
    [HarmonyPatch(typeof(IslandMarket), "Update")] internal static class MarketUpdatePatch { static bool Prefix(IslandMarket __instance) { Economy.UpdateMarket(__instance); return false; } }
    [HarmonyPatch(typeof(IslandMarket), "EconCycle")] internal static class CyclePatch { static bool Prefix(IslandMarket __instance) { Economy.Cycle(__instance); return false; } }
    [HarmonyPatch(typeof(DebugMarketTracker), "Update")] internal static class TrackerPatch { static bool Prefix() { Economy.Configure(); return false; } }
    [HarmonyPatch(typeof(EconomyUI), "RefreshPage")] internal static class BatchedPagePatch { static bool Prefix() => Economy.BatchDepth == 0 && !Startup.BlocksGameplay; }
    [HarmonyPatch(typeof(IslandMarket), "HasGood")] internal static class PurchaseAvailabilityPatch
    {
        static bool Prefix(IslandMarket __instance, int goodIndex, ref bool __result)
        {
            // One origin policy for players, the fleet and mission warehouse allocation.
            // Retain catalog/stock checks and the explicit saffron-only origin rule.
            __result = false;
            if (goodIndex <= 0 || goodIndex == 45 || __instance.currentSupply == null || goodIndex >= __instance.currentSupply.Length ||
                !Rules.PurchaseOriginAllowed(__instance.GetPortIndex(), goodIndex)) return false;
            var directory = PrefabsDirectory.instance;
            if (!directory || !directory.GetGood(goodIndex)) return false;
            __result = __instance.currentSupply[goodIndex] >= __instance.supplyPurchaseLimit;
            return false;
        }
    }
    [HarmonyPatch(typeof(IslandMarket), "GetGoodPriceAtSupply")] internal static class CapPricePatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var deficitField = AccessTools.Field(typeof(DebugMarketTracker), "negativePriceMult");
            int deficitIndex = code.FindIndex(c => c.LoadsField(deficitField));
            if (deficitIndex < 0 || code.Count(c => c.LoadsField(deficitField)) != 1)
                throw new InvalidOperationException("Expected one native shortage margin");
            bool hasVariation = code.Any(c => c.operand is FieldInfo field && field.DeclaringType == typeof(Good) && field.Name == "priceVariationMult");
            if (hasVariation)
            {
                // Remove both 0.39 additions from surplus AND shortage margins.
                // Keep the approved base margins and shared category curve in both quote paths.
                foreach (string name in new[] { "positivePriceMult", "negativePriceMult" })
                {
                    var marginField = AccessTools.Field(typeof(DebugMarketTracker), name);
                    int margin = code.FindIndex(c => c.LoadsField(marginField));
                    if (margin < 0 || code.Count(c => c.LoadsField(marginField)) != 1 || margin + 6 >= code.Count ||
                        !code[margin + 1].opcode.Name.StartsWith("ldloc") ||
                        !(code[margin + 2].operand is FieldInfo variation) || variation.DeclaringType != typeof(Good) || variation.Name != "priceVariationMult" ||
                        code[margin + 3].opcode != OpCodes.Mul || code[margin + 4].opcode != OpCodes.Ldc_R4 ||
                        !Equals(code[margin + 4].operand, 1.15f) || code[margin + 5].opcode != OpCodes.Mul ||
                        !code[margin + 6].opcode.Name.StartsWith("stloc") ||
                        code.Skip(margin + 1).Take(5).Any(c => Fields.Get<System.Collections.ICollection>(c, "labels").Count != 0 || c.blocks.Count != 0))
                        throw new InvalidOperationException("Native " + name + " does not match the reviewed 0.39 calculation");
                    code.RemoveRange(margin + 1, 5);
                }
                if (code.Any(c => c.operand is FieldInfo field && field.DeclaringType == typeof(Good) && field.Name == "priceVariationMult"))
                    throw new InvalidOperationException("Unrecognized native price variation remains");
            }
            int count = 0, curves = 0;
            for (int i = 0; i < code.Count; i++)
            {
                var c = code[i];
                if (c.LoadsField(AccessTools.Field(typeof(DebugMarketTracker), "goodsAmountSoftCap")))
                {
                    var first = new CodeInstruction(OpCodes.Ldarg_0); first.MoveLabelsFrom(c); first.MoveBlocksFrom(c);
                    yield return first; yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return CodeInstruction.Call(typeof(Economy), nameof(Economy.PricingCap)); count++;
                }
                else if (c.Calls(AccessTools.Method(typeof(Mathf), nameof(Mathf.InverseLerp))))
                {
                    var good = new CodeInstruction(OpCodes.Ldarg_1); good.MoveLabelsFrom(c); good.MoveBlocksFrom(c);
                    yield return good; yield return CodeInstruction.Call(typeof(PriceCurves), nameof(PriceCurves.NativeInverseResponse)); curves++;
                }
                else yield return c;
                if (c.LoadsField(AccessTools.Field(typeof(DebugMarketTracker), "positivePriceMult"))) {
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return CodeInstruction.Call(typeof(PriceCurves), nameof(PriceCurves.SurplusMultiplier));
                }
                if (c.LoadsField(deficitField))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return CodeInstruction.Call(typeof(PriceCurves), nameof(PriceCurves.DeficitMultiplier));
                }
            }
            if (count != 1 || curves != 2) throw new InvalidOperationException("Price curve integration does not match this game version");
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

    // ---- Price curves ----
    public enum PriceProfile { Essential, BasicSupply, Industrial, Precious, Luxury }

    // One shape provider for native market quotes and the fleet's stale-report forecasts.
    public static class PriceCurves
    {
        private static readonly double[] knots = { 0, .2, .5, .7, 1 };
        private static readonly double[][] values = {
            Anchors(.5, .65, .85), Anchors(.6, .8, 1), null,
            null, null
        };
        private static readonly double[][] slopes = MakeSlopes();
        // Basic Supply shortages: a gentle 1.30x -> 1.50x band, then up to 2.00x.
        private static readonly double[] basicDeficitKnots = { 0, .15, .50, 1 };
        private static readonly double[] basicDeficitValues = { 0, .30, .50, 1 };
        private static readonly double[] basicDeficitSlopes = MakeSlopes(basicDeficitKnots, basicDeficitValues);
        // Essential shortages: 1.15x at 20% short and 1.25x at 70% short, then up to 1.80x (x DeficitMultiplier 0.8).
        private static readonly double[] essentialDeficitKnots = { 0, .2, .7, 1 };
        private static readonly double[] essentialDeficitValues = { 0, .15 / .8, .25 / .8, 1 };
        private static readonly double[] essentialDeficitSlopes = MakeSlopes(essentialDeficitKnots, essentialDeficitValues);
        public static bool TryProfile(int good, out PriceProfile profile)
        {
            profile = PriceProfile.Luxury;
            if (good < 1 || good > 64 || good == 45 || good == 51) return false;
            switch (good)
            {
                case 1: case 2: case 3: case 4: case 9: case 10:
                case 16: case 17: case 18: case 25:
                case 42: case 46: case 54: profile = PriceProfile.Essential; break;
                case 6: case 7: case 8: case 11: case 12: case 13: case 14: case 15:
                case 19: case 26: case 27: case 29: case 36: case 43: case 48: case 49:
                case 52: case 58: case 59: case 63: profile = PriceProfile.BasicSupply; break;
                case 21: case 23: case 33: case 35: case 47: case 57: case 61:
                    profile = PriceProfile.Industrial; break;
                case 20: case 22: case 56: profile = PriceProfile.Precious; break;
            }
            return true;
        }
        private static double[] Anchors(double a, double b, double c) =>
            new[] { 0, a / (2.06 + .38 * a), b / (2.06 + .38 * b), c / (2.06 + .38 * c), 1 };
        private static double[][] MakeSlopes()
        {
            var result = new double[5][];
            for (int p = 0; p < 5; p++)
            {
                if (values[p] == null) continue;
                result[p] = MakeSlopes(knots, values[p]);
            }
            result[0][4] = result[1][4] = Math.Min(result[0][4], result[1][4]);
            return result;
        }
        private static double[] MakeSlopes(double[] x, double[] v)
        {
            int last = x.Length - 1;
            var h = new double[last]; var d = new double[last]; var m = new double[x.Length];
            for (int i = 0; i < last; i++) { h[i] = x[i + 1] - x[i]; d[i] = (v[i + 1] - v[i]) / h[i]; }
            for (int i = 1; i < last; i++)
            {
                double w1 = 2 * h[i] + h[i - 1], w2 = h[i] + 2 * h[i - 1];
                m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i]);
            }
            int k = last - 1;
            m[last] = Rules.Clamp(((2 * h[k] + h[k - 1]) * d[k] - h[k] * d[k - 1]) / (h[k] + h[k - 1]), 0, 3 * d[k]);
            return m;
        }
        private static double Interpolate(double y, double[] x, double[] v, double[] m)
        {
            int i = 0;
            while (i < x.Length - 2 && y > x[i + 1]) i++;
            double h = x[i + 1] - x[i], t = (y - x[i]) / h, t2 = t * t, t3 = t2 * t;
            return Rules.Clamp((2 * t3 - 3 * t2 + 1) * v[i] + (t3 - 2 * t2 + t) * h * m[i]
                + (-2 * t3 + 3 * t2) * v[i + 1] + (t3 - t2) * h * m[i + 1], 0, 1);
        }
        public static double Shape(double fraction, PriceProfile profile)
        {
            double y = Rules.Clamp(fraction, 0, 1);
            if (profile == PriceProfile.Industrial) return y;
            if (profile == PriceProfile.Precious || profile == PriceProfile.Luxury)
                return 2 * y - y * y;
            int p = (int)profile;
            if (p < 0 || p >= values.Length) throw new ArgumentOutOfRangeException(nameof(profile));
            return Interpolate(y, knots, values[p], slopes[p]);
        }
        public static double Response(double supply, double cap, int good)
        {
            if (!(cap > 0) || double.IsInfinity(cap) || double.IsNaN(supply) || double.IsInfinity(supply))
                throw new ArgumentOutOfRangeException(nameof(cap), "Pricing requires finite supply and a positive finite cap");
            double y = Rules.Clamp(Math.Abs(supply) / cap, 0, 1);
            if (!TryProfile(good, out var profile)) return 1 - (1 - y) * (1 - y);
            if (supply < 0 && (profile == PriceProfile.Luxury || profile == PriceProfile.Precious)) {
                const double crossing = .15, premium = .30, width = .10, slope = 4;
                if (y <= crossing) return Hermite(y / crossing, 0, premium, 0, slope, crossing) / 1.4;
                if (y < crossing + width) {
                    double u1 = width / (1 - crossing);
                    double end = premium + (1.4 - premium) * (2 * u1 - u1 * u1);
                    double endSlope = 2 * (1.4 - premium) * (1 - u1) / (1 - crossing);
                    return Hermite((y - crossing) / width, premium, end, slope, endSlope, width) / 1.4;
                }
                double u = (y - crossing) / (1 - crossing);
                return (premium + (1.4 - premium) * (2 * u - u * u)) / 1.4;
            }
            if (supply < 0 && profile == PriceProfile.BasicSupply) return Interpolate(y, basicDeficitKnots, basicDeficitValues, basicDeficitSlopes);
            if (supply < 0 && profile == PriceProfile.Essential) return Interpolate(y, essentialDeficitKnots, essentialDeficitValues, essentialDeficitSlopes);
            return Shape(y, profile);
        }
        private static double Hermite(double t, double a, double b, double ma, double mb, double h) =>
            (2*t*t*t-3*t*t+1)*a+(t*t*t-2*t*t+t)*h*ma+(-2*t*t*t+3*t*t)*b+(t*t*t-t*t)*h*mb;
        internal static float SurplusMultiplier(float vanilla, int good) {
            if (!TryProfile(good, out var p)) return vanilla;
            return p == PriceProfile.Essential ? .2f : p == PriceProfile.BasicSupply ? .25f : p == PriceProfile.Industrial ? .3f : .4f;
        }
        // Category shortage ceilings; the forecast uses the same margin.
        // The native transpiler removes 0.39's extra factors before this is called.
        internal static float DeficitMultiplier(float vanilla, int good)
        {
            if (!TryProfile(good, out var profile)) return vanilla;
            switch (profile)
            {
                case PriceProfile.Essential: return .8f;
                case PriceProfile.BasicSupply: return 1f;
                case PriceProfile.Industrial: return 1.2f;
                default: return 1.4f; // Luxury and Precious: 2.4x at the shortage cap.
            }
        }
        // Native code subsequently evaluates 1 - result*result. Replacing only this
        // interpolation keeps base-food valuation, other price hooks and both rounding stages intact.
        internal static float NativeInverseResponse(float cap, float zero, float supply, int good) =>
            (float)Math.Sqrt(1 - Response(supply, Math.Abs(cap), good));
    }

    // ---- Shops ----
    [HarmonyPatch(typeof(PlayerReputation), "UpdateReputation")] internal static class DiscountPatch
    {
        static void Postfix()
        {
            for (int r = 0; r < 4; r++)
            { PlayerReputation.retailDiscounts[r] = .02f * Mathf.Clamp(PlayerReputation.GetRepLevel(r), 0, 10); PlayerReputation.bulkDiscounts[r] = 0; }
        }
    }
    [HarmonyPatch(typeof(Shopkeeper), "GetPrice")] internal static class BulkDiscountPatch
    {
        static bool Prefix(Shopkeeper __instance, ShipItem item, ref int __result)
        {
            if (item.sold || !item.IsBulk()) return true;
            
            __result = Mathf.RoundToInt((int)Fields.Call(__instance, "GetBulkBuyPrice", item) * (1 - Reputations.ShopDiscount(__instance)));
            return false;
        }
    }
    [HarmonyPatch(typeof(ShopItemSpawner), "Update")] internal static class RestockPatch
    {
        internal static float Delay(ShopItemSpawner spawner) => spawner.itemPrefab && spawner.itemPrefab.GetComponent<Good>() ? 600 : 120;
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            foreach (var c in instructions)
            {
                if (c.opcode == OpCodes.Ldc_R4 && (float)c.operand == 120f)
                {
                    var load = new CodeInstruction(OpCodes.Ldarg_0); load.MoveLabelsFrom(c); load.MoveBlocksFrom(c);
                    yield return load; yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RestockPatch), nameof(Delay))); count++;
                }
                else yield return c;
            }
            if (count != 1) throw new InvalidOperationException("Shop restock hook does not match this game version");
        }
    }

    // ---- Currency ----
    // rates: the booth's live rate (booth exchanges move it at once). basis: the same without booth exchanges (only goods
    // trade moves it, at midnight). smooth: the daily rate, which follows basis; goods, shipyards, missions, rentals and
    // loans use it.
    [Serializable] public sealed class FxState
    {
        public int version = 1, day;
        public double[] rates = { .22, 1.9, .5, .006 };
        public double[] basis = { .22, 1.9, .5, .006 };
        public double[] smooth = { .22, 1.9, .5, .006 };
        public double[] cargo = new double[4], shops = new double[4], npc = new double[4];
        // Closing booth rates of Lions, Dragons and Crowns for up to FxRules.CloseDays days, oldest first.
        public double[] closes = new double[0];
    }
    public struct FxExchange
    {
        public bool Valid;
        public string Error;
        public int Sold, Received;
        public double GrossReceived, Fee, SellRate, BuyRate;
    }
    // Currency arithmetic is independent of Unity and shared by quote/commit paths.
    public static class FxRules
    {
        // K: the booth's impact per coin; goods trade moves the daily rate a tenth of that (shops half, traders 5% of it).
        public const double K = .000002, CargoImpact = K * .10, ShopImpact = CargoImpact * .5,
            NpcImpact = CargoImpact * .05, Recovery = .03, Gold = .006;
        public static readonly double[] Initial = { .22, 1.9, .5, Gold };
        // The daily rate closes half its gap to the booth-free rate in 10 days.
        public static readonly double Alpha = 1 - Math.Pow(2, -1.0 / 10);
        public static bool Positive(double value) => value > 0 && !double.IsInfinity(value) && !double.IsNaN(value) && (float)value > 0 && !float.IsInfinity((float)value);
        // Base fee B: 5% at rep 0, 0.5% less per level to 0.5% at rep 9, 0.1% at rep 10.
        public static double Fee(int reputation) { int level = Math.Max(0, Math.Min(10, reputation)); return level == 10 ? .001 : .05 - .005 * level; }
        // Chronos treats Lions, Dragons and Crowns alike (B); Gold received there costs half (B/2), at the
        // booth as for goods. Elsewhere selling Gold at the booth is free and receiving it costs 2B.
        public static double BoothFee(int sold, int bought, int reputation, bool chronos = false) => sold == bought ? 0 : chronos ? Fee(reputation) * (bought == 3 ? .5 : 1) : sold == 3 ? 0 : Fee(reputation) * (bought == 3 ? 2 : 1);
        public static double GoodsFee(int native, int paid, bool buying, int reputation, bool chronos = false) => paid == 3 ? (buying ? 0 : (chronos ? .5 : 2) * Fee(reputation)) : native == paid ? 0 : Fee(reputation);
        // Market value in the coins paid, at the daily rate (local or foreign coins alike).
        public static double Quote(double raw, int paid, double[] smooth) => raw * smooth[paid];
        public static int Coins(double amount, bool buying, double fee)
        {
            double value = fee == 0 ? Math.Round(amount, MidpointRounding.ToEven) : buying ? Math.Ceiling(amount * (1 + fee)) : Math.Floor(amount * (1 - fee));
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > int.MaxValue) throw new OverflowException("Price exceeds wallet limit");
            return (int)value;
        }
        public const int CloseDays = 5;
        // The day's closing rates, before its pressure settles, feed the booth's five-day table.
        public static void RecordClose(FxState state)
        {
            var old = state.closes ?? new double[0];
            int keep = Math.Min(old.Length, (CloseDays - 1) * 3);
            var closes = new double[keep + 3];
            Array.Copy(old, old.Length - keep, closes, 0, keep);
            Array.Copy(state.rates, 0, closes, keep, 3);
            state.closes = closes;
        }
        // Booth table: Gold Lions for 1000 coins of `currency`, `daysAgo` days back (0 = today, the live rate),
        // to four significant figures as shown; NaN for a day not recorded yet.
        public static double TableValue(FxState state, int daysAgo, int currency)
        {
            double rate;
            if (daysAgo == 0) rate = state.rates[currency];
            else
            {
                var closes = state.closes ?? new double[0];
                int index = closes.Length - 3 * daysAgo + currency;
                if (daysAgo > CloseDays || index < 0) return double.NaN;
                rate = closes[index];
            }
            return Significant(1000 * Gold / rate);
        }
        // +1 stronger than the day before (more Gold for 1000 coins), -1 weaker, 0 unchanged or unknown.
        public static int Trend(FxState state, int daysAgo, int currency)
        {
            double now = TableValue(state, daysAgo, currency), before = TableValue(state, daysAgo + 1, currency);
            return double.IsNaN(now) || double.IsNaN(before) || now == before ? 0 : now > before ? 1 : -1;
        }
        private static int Decimals(double value) => value > 0 ? Math.Max(0, 3 - (int)Math.Floor(Math.Log10(value))) : 0;
        public static double Significant(double value) => value > 0 ? Math.Round(value, Decimals(value), MidpointRounding.AwayFromZero) : value;
        public static string TableText(double value) => double.IsNaN(value) ? "-" : value.ToString("F" + Decimals(value), System.Globalization.CultureInfo.InvariantCulture);
        // Queue trade pressure for the next settlement. Returns the magnitude queued (0 for Gold or no coins).
        internal static double Queue(FxState s, int currency, double coins, bool buying, TradeSource source)
        {
            if (currency < 0 || currency >= 3 || !(coins > 0)) return 0;
            var queue = source == TradeSource.Shop ? s.shops : source == TradeSource.Npc ? s.npc : s.cargo;
            double coefficient = source == TradeSource.Shop ? ShopImpact : source == TradeSource.Npc ? NpcImpact : CargoImpact;
            double pressure = coins * coefficient, value = queue[currency] + pressure * (buying ? -1 : 1);
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new OverflowException("Currency pressure overflow");
            queue[currency] = value;
            return pressure;
        }
        // Round-trip neutral cargo: a sale in the currency its trade-book purchase strengthened queues exactly that
        // pressure back, whatever the profit; any other sale weakens the received currency by its proceeds.
        internal static void QueueSale(FxState s, int currency, double coins, TradeSource source, bool remembered, int purchaseCurrency, double purchasePressure)
        {
            if (!remembered || purchaseCurrency != currency) { Queue(s, currency, coins, false, source); return; }
            if (currency < 0 || currency >= 3 || !(purchasePressure > 0)) return;
            double value = s.cargo[currency] + purchasePressure;
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new OverflowException("Currency pressure overflow");
            s.cargo[currency] = value;
        }
        // Midnight: the day's goods pressure moves the booth rate and the booth-free rate alike, both are pulled 3% back toward
        // the starting rate (so what booth exchanges did fades too), and the daily rate follows the booth-free rate.
        public static void SettleDay(FxState state)
        {
            RecordClose(state);
            for (int c = 0; c < 3; c++)
            {
                double pressure = state.cargo[c] + state.shops[c] + state.npc[c];
                if (Positive(state.rates[c] + pressure)) state.rates[c] += pressure;
                if (Positive(state.basis[c] + pressure)) state.basis[c] += pressure;
                state.rates[c] += Recovery * (Initial[c] - state.rates[c]);
                state.basis[c] += Recovery * (Initial[c] - state.basis[c]);
                state.smooth[c] += Alpha * (state.basis[c] - state.smooth[c]);
            }
            state.rates[3] = state.basis[3] = state.smooth[3] = Gold;
            Array.Clear(state.cargo, 0, 4); Array.Clear(state.shops, 0, 4); Array.Clear(state.npc, 0, 4);
        }
        private static double Log1P(double x) => Math.Abs(x) < 1e-6 ? x * (1 - x * (.5 - x / 3)) : Math.Log(1 + x);
        private static double ExpM1(double x) => Math.Abs(x) < 1e-6 ? x * (1 + x * (.5 + x / 6)) : Math.Exp(x) - 1;
        public static FxExchange Exchange(double[] rates, int sold, int bought, int amount, bool requestBuy, double fee)
        {
            var q = new FxExchange { Error = "Currency runs out!" };
            if (rates == null || rates.Length != 4 || sold < 0 || sold > 3 || bought < 0 || bought > 3 || sold == bought || amount <= 0 || fee < 0 || fee >= 1) { q.Error = "Choose currencies and an amount"; return q; }
            double a = rates[sold], b = rates[bought];
            if (!Positive(a) || !Positive(b)) return q;
            // One market impact for every pair (approved Option A). Directional
            // Gold fees remain separate; doubling only Gold impact permits arbitrage.
            double k = K;
            double x = amount;
            if (requestBuy)
            {
                double y = amount / (1 - fee);
                if (sold == 3)
                {
                    if (k * y >= b) return q;
                    x = -a / k * Log1P(-k * y / b);
                }
                else if (bought == 3) x = a / k * ExpM1(k * y / b);
                else
                {
                    if (k * y >= b) return q;
                    x = a * y / (b - k * y);
                }
                x = Math.Ceiling(x);
            }
            if (double.IsInfinity(x) || double.IsNaN(x) || x > int.MaxValue) { q.Error = "Amount exceeds wallet limit"; return q; }
            if (requestBuy)
            {
                // Resolve integer payment against the forward quote itself, avoiding
                // an extra coin from floating-point error in the analytic inverse.
                int hi = (int)x;
                var top = Exchange(rates, sold, bought, hi, false, fee);
                if (top.Valid && top.Received < amount && hi < int.MaxValue) top = Exchange(rates, sold, bought, ++hi, false, fee);
                if (!top.Valid || top.Received < amount) return top.Valid ? q : top;
                int lo = 1;
                while (lo < hi) {
                    int mid = lo + (hi - lo) / 2;
                    var candidate = Exchange(rates, sold, bought, mid, false, fee);
                    if (candidate.Valid && candidate.Received >= amount) hi = mid; else lo = mid + 1;
                }
                return Exchange(rates, sold, bought, lo, false, fee);
            }
            double gross, nextA = a, nextB = b;
            if (sold == 3) { nextB = b * Math.Exp(-k * x / a); gross = -b / k * ExpM1(-k * x / a); }
            else if (bought == 3) { nextA = a + k * x; gross = b / k * Log1P(k * x / a); }
            else { nextA = a + k * x; nextB = b * (a / nextA); gross = b * x / nextA; }
            if (!Positive(nextA) || !Positive(nextB) || bought != 3 && k * gross >= b) return q;
            double receipt = Math.Floor(gross * (1 - fee));
            if (receipt < 1) { q.Error = "Amount too small"; return q; }
            if (receipt > int.MaxValue) { q.Error = "Amount exceeds wallet limit"; return q; }
            q.Valid = true; q.Error = null; q.Sold = (int)x; q.Received = (int)receipt;
            q.GrossReceived = gross; q.Fee = gross * fee; q.SellRate = nextA; q.BuyRate = nextB;
            return q;
        }
    }

    internal enum TradeSource { Cargo, Shop, Npc }
    internal struct MoneyQuote { internal int Gross, Total; internal double Fee; }
    internal static class Fx
    {
        internal static FxState State => Ensure();
        internal static FxState Ensure()
        {
            if (World.State.fx == null) World.State.fx = new FxState { day = GameState.day };
            return World.State.fx;
        }
        internal static void ResetInitial() { World.State.fx = new FxState { day = GameState.day }; Sync(); }
        // The game's own prices (shipyards, mission rewards) use the daily rate; the booth reads the live rate itself.
        internal static void Sync()
        {
            if (!CurrencyMarket.instance) return;
            var s = Ensure();
            for (int i = 0; i < 4; i++) CurrencyMarket.instance.currentPrices[i] = (float)s.smooth[i];
        }
        internal static void Validate(FxState s)
        {
            bool valid = s.version == 1 && s.day >= 0 && new[] { s.rates, s.basis, s.smooth, s.cargo, s.shops, s.npc }.All(a => a != null && a.Length == 4 && a.All(v => !double.IsNaN(v) && !double.IsInfinity(v))) &&
                s.rates.All(FxRules.Positive) && s.basis.All(FxRules.Positive) && s.smooth.All(FxRules.Positive) && s.rates[3] == FxRules.Gold && s.basis[3] == FxRules.Gold && s.smooth[3] == FxRules.Gold &&
                s.cargo[3] == 0 && s.shops[3] == 0 && s.npc[3] == 0 &&
                (s.closes == null || s.closes.Length % 3 == 0 && s.closes.Length <= FxRules.CloseDays * 3 && s.closes.All(FxRules.Positive));
            if (!valid) throw new InvalidDataException("Invalid currency state");
        }
        internal static void AdvanceDay(int day)
        {
            if (!World.Ready || World.Loading || World.SaveBlocked || Startup.BlocksGameplay || GameState.currentlyLoading) return;
            var s = Ensure();
            if (day <= s.day) return;
            while (s.day < day) { FxRules.SettleDay(s); s.day++; }
            Sync();
            if (CurrencyExchangeUI.instance && CurrencyExchangeUI.instance.uiActive) CurrencyExchangeUI.instance.RefreshUI();
            if (EconomyUI.instance && EconomyUI.instance.uiActive) EconomyUI.instance.RefreshPage();
        }
        internal static int Currency(IslandMarket m) => m.GetPortIndex() == 21 ? 3 : m.GetPortRegion();
        internal static int LocalRegion()
        {
            if (FxBooth.OpeningAccount.HasValue) return FxBooth.OpeningAccount.Value;
            if (CurrencyExchangeUI.instance && CurrencyExchangeUI.instance.uiActive) {
                var booth=CurrencyExchangeUI.instance.GetComponent<FxBooth>(); if(booth)return booth.region;
            }
            if (RegionBlender.instance) {
                var r = Fields.Get<Region>(RegionBlender.instance, "currentTargetRegion");
                if (r) return Math.Max(0, Math.Min(2, (int)r.portRegion));
            }
            return Math.Max(0, Math.Min(2, GameState.currentCurrency));
        }
        internal static int Reputation(int region) => Reputations.Level(Math.Max(0, Math.Min(3, region)));
        internal static MoneyQuote Quote(double raw, IslandMarket port, int payment, bool buying, double condition = 1)
        {
            var s = Ensure(); int native = Currency(port);
            double value = FxRules.Quote(raw, payment, s.smooth);
            int gross = FxRules.Coins(value, buying, 0);
            if (!buying) gross = (int)Math.Floor(gross * condition);
            double fee = FxRules.GoodsFee(native, payment, buying, Reputation(Reputations.Account(port)), Reputations.Account(port) == Reputations.Chronos);
            return new MoneyQuote { Gross = gross, Total = FxRules.Coins(gross, buying, fee), Fee = fee };
        }
        internal static double NpcQuote(IslandMarket port, int good, bool buying)
        {
            if (Startup.BlocksGameplay) return 0;
            int currency = Currency(port);
            return Math.Round(FxRules.Quote(buying ? port.GetBuyPrice(good) : port.GetSellPrice(good), currency, State.smooth));
        }
        private static bool Recording => World.Ready && !World.Loading && !World.SaveBlocked && !Startup.BlocksGameplay && !GameState.currentlyLoading;
        // Returns the pressure magnitude queued, 0 when nothing was queued.
        internal static double Record(int currency, double coins, bool buying, TradeSource source)
        {
            if (!Recording || currency == 3 || coins <= 0) return 0;
            AdvanceDay(GameState.day);
            return FxRules.Queue(Ensure(), currency, coins, buying, source);
        }
        // Round-trip neutral cargo: a trade-book purchase remembers the pressure it queued on the unit's record.
        internal static void RememberPurchase(ShipItem item, int currency, double pressure)
        {
            var record = CargoRecords.Of(item);
            if (record == null) return;
            record.fxCurrency = currency; record.fxPressure = pressure > 0 && currency >= 0 && currency < 3 ? pressure : 0;
        }
        // Read before the sold item is destroyed (its record is retired with it).
        internal static bool PurchasePressure(ShipItem item, out int currency, out double pressure)
        {
            var record = CargoRecords.Of(item);
            currency = record?.fxCurrency ?? -1; pressure = record?.fxPressure ?? 0;
            return pressure > 0 && currency >= 0 && currency < 3;
        }
        internal static void RecordSale(int currency, double coins, TradeSource source, bool remembered, int purchaseCurrency, double purchasePressure)
        {
            if (!Recording || currency == 3) return;
            AdvanceDay(GameState.day);
            FxRules.QueueSale(Ensure(), currency, coins, source, remembered, purchaseCurrency, purchasePressure);
        }
        internal static MoneyQuote BookQuote(EconomyUI ui, int portIndex, int good, bool buying)
        {
            var current = Fields.Get<IslandMarket>(ui, "currentIsland"); var port = World.Market(portIndex);
            int payment = (int)Fields.Get<Currency>(ui, "currentPlayerCurrency");
            int raw = buying ? current.knownPrices[portIndex].buyPrices[good] : current.knownPrices[portIndex].sellPrices[good];
            double condition = 1;
            if (!buying && current == port) { var cargo = Loans.Next(port, good); if (cargo) condition = CargoCondition.Factor(cargo.GetComponent<ShipItem>()); }
            return Quote(raw, port, payment, buying, condition);
        }
        internal static bool TryBookQuote(EconomyUI ui, int port, int good, bool buy, out MoneyQuote quote)
        {
            try { quote = BookQuote(ui, port, good, buy); return true; }
            catch (OverflowException) { quote = default; return false; }
        }
    }
    [HarmonyPatch(typeof(CurrencyMarket), "MarketCycle")] internal static class FxDayPatch
    { static bool Prefix() { Fx.AdvanceDay(GameState.day); return false; } }
    [HarmonyPatch(typeof(CurrencyMarket), "GetExchangeFee")] internal static class FxFeePatch
    { static bool Prefix(ref float __result) { __result = (float)FxRules.Fee(Fx.Reputation(Fx.LocalRegion())); return false; } }
    [HarmonyPatch(typeof(CurrencyMarket), "GetExchangeRate")] internal static class FxPairPatch
    {
        static bool Prefix(int sellCurrency, int buyCurrency, bool withConversionFee, ref float __result)
        {
            var r = Fx.State.rates;
            __result = sellCurrency == buyCurrency ? 1 : (float)(r[buyCurrency] / r[sellCurrency] * (withConversionFee ? 1 - FxRules.BoothFee(sellCurrency,buyCurrency,Fx.Reputation(Fx.LocalRegion()), Fx.LocalRegion() == Reputations.Chronos) : 1));
            return false;
        }
    }
    [HarmonyPatch(typeof(EconomyUI), "GetBuyPrice")] internal static class FxBookBuyQuotePatch
    { static bool Prefix(EconomyUI __instance, int portIndex, int goodIndex, ref int __result) { __result = Fx.TryBookQuote(__instance,portIndex,goodIndex,true,out var q) ? q.Total : int.MaxValue; return false; } }
    [HarmonyPatch(typeof(EconomyUI), "GetSellPrice")] internal static class FxBookSellQuotePatch
    { static bool Prefix(EconomyUI __instance, int portIndex, int goodIndex, ref int __result) { __result = Fx.TryBookQuote(__instance,portIndex,goodIndex,false,out var q) ? q.Total : int.MaxValue; return false; } }
    [HarmonyPatch(typeof(EconomyUI), "GetBuyPriceString")] internal static class FxBookBuyLabelPatch
    { static bool Prefix(EconomyUI __instance, int portIndex, int goodIndex, ref string __result) { if(Fx.TryBookQuote(__instance,portIndex,goodIndex,true,out _))return true; __result="Unavailable";return false; } }
    [HarmonyPatch(typeof(EconomyUI), "GetSellPriceString")] internal static class FxBookSellLabelPatch
    { static bool Prefix(EconomyUI __instance, int portIndex, int goodIndex, ref string __result) { if(Fx.TryBookQuote(__instance,portIndex,goodIndex,false,out _))return true; __result="Unavailable";return false; } }
    [HarmonyPatch] internal static class FxBookProfitLabelPatch
    {
        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(EconomyUI),"GetProfitString");
            var percentage=AccessTools.Method(typeof(EconomyUI),"GetProfitPecentString");
            if(percentage!=null)yield return percentage; // Added in native 0.39.
        }
        static bool Prefix(EconomyUI __instance, int portIndex, int goodIndex, ref string __result) { var m=Fields.Get<IslandMarket>(__instance,"currentIsland"); if(Fx.TryBookQuote(__instance,m.GetPortIndex(),goodIndex,true,out _)&&Fx.TryBookQuote(__instance,portIndex,goodIndex,false,out _))return true; __result="-";return false; }
    }
    [HarmonyPatch(typeof(EconomyUI), "OpenUI")] internal static class FxChronosBookPatch
    {
        static void Postfix(EconomyUI __instance)
        {
            var m=Fields.Get<IslandMarket>(__instance,"currentIsland");
            if(m && m.GetPortIndex()==21) { Fields.Set(__instance,"currentPlayerCurrency",(Currency)3); Fields.Call(__instance,"RefreshCurrencyButtons"); __instance.RefreshPage(); }
        }
    }

    [HarmonyPatch(typeof(CurrencyExchangeUIButton),"OnActivate")] internal static class FxBoothLocationPatch
    {
        static void Prefix(CurrencyExchangeUIButton __instance, out int? __state)
        {
            __state=FxBooth.OpeningAccount;
            if(Convert.ToInt32(Fields.Find(typeof(CurrencyExchangeUIButton),"function").GetValue(__instance))==6)
                FxBooth.OpeningAccount=Reputations.Location(__instance,Fx.LocalRegion());
        }
        static Exception Finalizer(Exception __exception,int? __state){FxBooth.OpeningAccount=__state;return __exception;}
    }
    public sealed class FxBooth : MonoBehaviour
    {
        private CurrencyExchangeUI ui;
        internal bool requestBuy;
        internal int requested, region;
        private FxExchange quote;
        private double lastA, lastB, lastFee;
        internal static FxBooth For(CurrencyExchangeUI ui)
        { var b=ui.GetComponent<FxBooth>(); if(!b){b=ui.gameObject.AddComponent<FxBooth>(); b.ui=ui;} return b; }
        internal static int? OpeningAccount;
        internal void Open() { region=OpeningAccount ?? Fx.LocalRegion(); requested=0; requestBuy=false; Refresh(); }
        internal void Change(int change,bool buy)
        {
            long before=buy ? Fields.Get<int>(ui,"currentBuyAmount") : Fields.Get<int>(ui,"currentSellAmount");
            requested=(int)Math.Max(0,Math.Min(int.MaxValue,before+change)); requestBuy=buy; Refresh();
        }
        private FxExchange Calculate()
        {
            int a=Fields.Get<int>(ui,"currentSellCurrency"),b=Fields.Get<int>(ui,"currentBuyCurrency");
            lastA=Fx.State.rates[a]; lastB=Fx.State.rates[b]; lastFee=FxRules.BoothFee(a,b,Fx.Reputation(region),region==Reputations.Chronos);
            return FxRules.Exchange(Fx.State.rates,a,b,requested,requestBuy,lastFee);
        }
        internal void Refresh()
        {
            quote=Calculate();
            Fields.Set(ui,"currentSellAmount",quote.Valid?quote.Sold:requestBuy?0:requested);
            Fields.Set(ui,"currentBuyAmount",quote.Valid?quote.Received:requestBuy?requested:0);
            Fields.Set(ui,"currentExchangeRate",quote.Valid?(float)((double)quote.Received/quote.Sold):0f);
            Fields.Call(ui,"UpdateTexts");
            FxRateTable.For(ui).Refresh(Fx.State);
        }
        internal void Text()
        {
            var label=Fields.Get<TextMesh>(ui,"textPricePanel"); if(!label)return;
            label.text=quote.Valid ? "Average rate\n"+((double)quote.Received/quote.Sold).ToString("0.######")+"\nFX fee "+(lastFee*100).ToString("0.##")+"%\n"+quote.Fee.ToString("0.##")+" "+PlayerGold.GetCurrencyName(Fields.Get<int>(ui,"currentBuyCurrency"),true) : requested>0 ? quote.Error : "Choose amount";
        }
        private void Update()
        {
            if(!ui || !ui.uiActive)return;
            int a=Fields.Get<int>(ui,"currentSellCurrency"),b=Fields.Get<int>(ui,"currentBuyCurrency");
            if(lastA!=Fx.State.rates[a]||lastB!=Fx.State.rates[b]||lastFee!=FxRules.BoothFee(a,b,Fx.Reputation(region),region==Reputations.Chronos))Refresh();
        }
        internal void Confirm()
        {
            if(!World.Ready||World.SaveBlocked||Startup.BlocksGameplay)return;
            var old=quote;
            Fx.AdvanceDay(GameState.day);
            var current=Calculate();
            if(!current.Valid){ Refresh(); Loans.Notify(current.Error); return; }
            if(!old.Valid || old.Sold!=current.Sold || old.Received!=current.Received || old.Fee!=current.Fee) { Refresh(); Loans.Notify("Exchange quote updated. Check the amounts and confirm again."); return; }
            int a=Fields.Get<int>(ui,"currentSellCurrency"),b=Fields.Get<int>(ui,"currentBuyCurrency");
            if(PlayerGold.currency[a]<current.Sold){Loans.Notify("Not enough money");return;}
            if((long)PlayerGold.currency[b]+current.Received>int.MaxValue){Loans.Notify("Amount exceeds wallet limit");return;}
            PlayerGold.currency[a]-=current.Sold;PlayerGold.currency[b]+=current.Received;
            Fx.State.rates[a]=current.SellRate;Fx.State.rates[b]=current.BuyRate;Fx.Sync();
            if(DayLogs.instance){DayLogs.instance.dayLogs[a].LogTransaction(-current.Sold,TransactionCategory.currencyExchange);DayLogs.instance.dayLogs[b].LogTransaction(current.Received,TransactionCategory.currencyExchange);}
            if(UISoundPlayer.instance)UISoundPlayer.instance.PlayGoldSound();
            requested=0;Refresh();
        }
    }
    [HarmonyPatch(typeof(CurrencyExchangeUI),"OpenUI")] internal static class FxBoothOpenPatch {static void Postfix(CurrencyExchangeUI __instance)=>FxBooth.For(__instance).Open();}
    [HarmonyPatch(typeof(CurrencyExchangeUI),"ChangeBuyAmount")] internal static class FxBoothBuyPatch {static bool Prefix(CurrencyExchangeUI __instance,int change){FxBooth.For(__instance).Change(change,true);return false;}}
    [HarmonyPatch(typeof(CurrencyExchangeUI),"ChangeSellAmount")] internal static class FxBoothSellPatch {static bool Prefix(CurrencyExchangeUI __instance,int change){FxBooth.For(__instance).Change(change,false);return false;}}
    [HarmonyPatch(typeof(CurrencyExchangeUI),"RecalculateBuyAmount")] internal static class FxBoothRebuyPatch {static bool Prefix(CurrencyExchangeUI __instance){FxBooth.For(__instance).Refresh();return false;}}
    [HarmonyPatch(typeof(CurrencyExchangeUI),"RecalculateSellAmount")] internal static class FxBoothResellPatch {static bool Prefix(CurrencyExchangeUI __instance){FxBooth.For(__instance).Refresh();return false;}}
    [HarmonyPatch(typeof(CurrencyExchangeUI),"RefreshUI")] internal static class FxBoothRefreshPatch {static bool Prefix(CurrencyExchangeUI __instance){FxBooth.For(__instance).Refresh();return false;}}
    [HarmonyPatch(typeof(CurrencyExchangeUI),"UpdateTexts")] internal static class FxBoothTextPatch {static void Postfix(CurrencyExchangeUI __instance)=>FxBooth.For(__instance).Text();}
    [HarmonyPatch(typeof(CurrencyExchangeUI),"ConfirmExchange")] internal static class FxBoothConfirmPatch {static bool Prefix(CurrencyExchangeUI __instance){FxBooth.For(__instance).Confirm();return false;}}
    [HarmonyPatch(typeof(CurrencyExchangeUI),"ResetExchange")] internal static class FxBoothResetPatch {static void Postfix(CurrencyExchangeUI __instance){var booth=FxBooth.For(__instance);booth.requested=0;booth.Refresh();}}

    [DefaultExecutionOrder(1000)]
    public sealed class FxQuantityButton : MonoBehaviour
    {
        private CurrencyExchangeUIButton button;
        private TextMesh label;
        private string original;
        internal static bool ShiftHeld() => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        internal static bool IsAmountButton(CurrencyExchangeUIButton button)
        {
            string function = Fields.Get<object>(button,"function").ToString();
            return function == "changeBuyAmount" || function == "changeSellAmount";
        }
        internal static int Change(CurrencyExchangeUIButton button) => checked(Fields.Get<int>(button,"index") * (ShiftHeld() && ButtonHover.IsOver(button) ? 1000 : 1));
        internal static void Attach(CurrencyExchangeUI ui)
        {
            // Vanilla draws all six labels in one TextMesh per row. Separate
            // them so a long hovered quantity cannot move neighbouring labels.
            var rows=ui.GetComponentsInChildren<TextMesh>(true)
                .Where(t=>t.text.Contains("-100")&&t.text.Contains("+100")).ToArray();
            foreach(var b in ui.GetComponentsInChildren<CurrencyExchangeUIButton>(true))
                if(IsAmountButton(b) && !b.GetComponent<FxQuantityButton>()){
                    var plate=b.GetComponent<MeshRenderer>();
                    var row=rows.OrderBy(t=>(t.GetComponent<Renderer>().bounds.center-plate.bounds.center).sqrMagnitude).FirstOrDefault();
                    if(!row)continue;
                    var v=b.gameObject.AddComponent<FxQuantityButton>();v.button=b;
                    int step=Fields.Get<int>(b,"index");string text=(step>0?"+":"")+step;
                    v.label=Instantiate(row,row.transform.parent);v.label.name="Exchange amount "+text;
                    v.label.GetComponent<Renderer>().enabled=true;
                    var p=row.transform.InverseTransformPoint(plate.bounds.center);p.z=0;
                    v.label.transform.position=row.transform.TransformPoint(p);
                    v.label.anchor=TextAnchor.MiddleCenter;v.label.alignment=TextAlignment.Center;
                    v.label.text=v.original=text;
                    var fit=v.label.gameObject.AddComponent<LoanTextFit>();fit.originalScale=v.label.transform.localScale;
                    fit.maxWidth=BookUI.InFrameBounds(plate,v.label.transform.parent).size.x*.9f;
                }
            foreach(var row in rows)row.GetComponent<Renderer>().enabled=false;
        }
        internal void Refresh()
        {
            if(!label)return;
            int change=Change(button);
            label.text=ShiftHeld() && ButtonHover.IsOver(button) ? (change>0?"+":"")+change.ToString() : original;
        }
        private void LateUpdate() => Refresh();
        private void OnDisable(){if(label)label.text=original;}
    }
    [HarmonyPatch(typeof(CurrencyExchangeUI),"OpenUI")] internal static class FxQuantityOpenPatch
    {static void Postfix(CurrencyExchangeUI __instance)=>FxQuantityButton.Attach(__instance);}
    [HarmonyPatch(typeof(CurrencyExchangeUIButton),"OnActivate")] internal static class FxQuantityClickPatch
    {
        static bool Prefix(CurrencyExchangeUIButton __instance)
        {
            if(!FxQuantityButton.IsAmountButton(__instance))return true;
            if(__instance.unclickable || !CurrencyExchangeUI.instance.uiActive)return false;
            int change=FxQuantityButton.Change(__instance);
            bool buy=Fields.Get<object>(__instance,"function").ToString()=="changeBuyAmount";
            FxBooth.For(CurrencyExchangeUI.instance).Change(change,buy);return false;
        }
    }

    // Five-day Gold Lion rate table to the left of the exchange booth. Built once from the booth's own
    // vertical scroll and rate text, mirrored across the board's centre line so it matches the native look.
    internal sealed class FxRateTable : MonoBehaviour
    {
        // Geometry from the native booth: the board's centre on the UI's local x axis (screen left is +x);
        // the scroll is 1.34x the right-hand scroll's width, moved out by half the extra width so its inner
        // edge stays behind the board, and 0.75x its length; the text is the mirrored rate text, moved out and down.
        private const float BoardCentre = -.021f, ScrollShift = .0306f, ScrollWiden = 1.34f, ScrollLength = .75f, TextOffset = .03f, TextDown = .045f;
        // Layout in the rate text's local units (x right, y up; x is squashed 2:3 like that text).
        private static readonly float[] ColumnRight = { -3, 16.5f, 36 };
        private const float LabelLeft = -36, TitleY = 28, SubtitleY = 20.5f, HeaderY = 13, FirstRowY = 4.5f, RowStep = 7.4f, LegendStep = 5.6f;
        private static readonly string[] Days = { "Today", "Yesterday", "2 days ago", "3 days ago", "4 days ago" };
        private static readonly Color Stronger = new Color(.13f, .52f, .2f), Weaker = new Color(.72f, .1f, .08f), Rule = new Color(.35f, .22f, .16f);
        private static Material marks;
        private static bool warned;
        private readonly TextMesh[,] cells = new TextMesh[5, 3];
        private readonly GameObject[,] ups = new GameObject[5, 3], downs = new GameObject[5, 3];
        private GameObject table;
        private string shown;
        internal GameObject Table => table;
        internal static FxRateTable For(CurrencyExchangeUI ui)
        {
            var t = ui.GetComponent<FxRateTable>();
            if (!t) { t = ui.gameObject.AddComponent<FxRateTable>(); t.Build(ui.transform); }
            return t;
        }
        internal void Refresh(FxState state)
        {
            if (!table) return;
            var values = new double[5, 3];
            for (int d = 0; d < 5; d++) for (int c = 0; c < 3; c++) values[d, c] = FxRules.TableValue(state, d, c);
            string key = string.Join(",", values.Cast<double>());
            if (key == shown) return;
            shown = key;
            for (int d = 0; d < 5; d++)
                for (int c = 0; c < 3; c++)
                {
                    cells[d, c].text = FxRules.TableText(values[d, c]);
                    int trend = FxRules.Trend(state, d, c);
                    if (ups[d, c]) ups[d, c].SetActive(trend > 0);
                    if (downs[d, c]) downs[d, c].SetActive(trend < 0);
                }
        }
        private void Build(Transform root)
        {
            var textSource = root.Find("text price info");
            var scrollSource = root.Find("panel  top (SELL)/bg (1)");
            if (!textSource || !textSource.GetComponent<TextMesh>() || !scrollSource || !scrollSource.GetComponent<MeshFilter>()?.sharedMesh)
            {
                if (!warned) Plugin.Log?.LogWarning("Exchange booth layout not recognised; the five-day rate table is not shown.");
                warned = true; return;
            }
            if (!marks)
            {
                // Arrows and rules use the numbers' own font shader, which draws over the paper as the numbers do;
                // a depth-tested shader lets the curled paper hide them.
                var font = textSource.GetComponent<MeshRenderer>() ? textSource.GetComponent<MeshRenderer>().sharedMaterial : null;
                var shader = font ? font.shader : Shader.Find("GUI/Text Shader") ?? Shader.Find("Sprites/Default");
                if (shader) marks = new Material(shader) { color = Color.white, mainTexture = Texture2D.whiteTexture, name = "EconomyOverhaul rate marks", renderQueue = font ? font.renderQueue : 3000 };
            }
            // Laid out in the booth's own space (local +z is screen-down under its camera anchor, +y faces the camera) with
            // local transforms only: world axes would tilt the layout with the view pitch at the moment the booth first
            // opens, and world-space round trips pick up rounding from the player rig's distant parents.
            table = new GameObject("EconomyOverhaul rate table") { layer = root.gameObject.layer };
            table.transform.SetParent(root, false);
            var frame = new GameObject("text") { layer = table.layer }.transform;
            frame.SetParent(table.transform, false); frame.localScale = textSource.localScale;
            Mirror(root, frame, textSource, TextOffset, false);
            frame.localPosition += new Vector3(0, 0, TextDown);
            TextMesh Text(string text, float x, float y, float size, TextAnchor anchor)
            {
                var o = Instantiate(textSource.gameObject, frame, false); o.name = text;
                o.transform.localPosition = new Vector3(x, y, 0); o.transform.localRotation = Quaternion.identity; o.transform.localScale = Vector3.one;
                var m = o.GetComponent<TextMesh>(); m.text = text; m.characterSize = size; m.anchor = anchor; m.lineSpacing = 1; m.color = Color.black;
                m.alignment = anchor == TextAnchor.MiddleLeft ? TextAlignment.Left : anchor == TextAnchor.MiddleRight ? TextAlignment.Right : TextAlignment.Center;
                return m;
            }
            GameObject Shape(Vector3[] v, Color colour)
            {
                if (!marks) return null;
                var o = new GameObject("mark") { layer = table.layer }; o.transform.SetParent(frame, false);
                var triangles = new List<int>();
                for (int i = 1; i + 1 < v.Length; i++) triangles.AddRange(new[] { 0, i, i + 1, 0, i + 1, i });   // both faces
                var mesh = new Mesh { vertices = v, colors = v.Select(_ => colour).ToArray(), uv = new Vector2[v.Length], triangles = triangles.ToArray() };
                mesh.RecalculateBounds();
                o.AddComponent<MeshFilter>().sharedMesh = mesh; o.AddComponent<MeshRenderer>().sharedMaterial = marks;
                return o;
            }
            GameObject Arrow(float x, float y, bool up)
            {
                float w = 3.4f, h = 2.3f, s = up ? 1 : -1;
                return Shape(new[] { new Vector3(x - w / 2, y - s * h / 2, -.2f), new Vector3(x + w / 2, y - s * h / 2, -.2f), new Vector3(x, y + s * h / 2, -.2f) }, up ? Stronger : Weaker);
            }
            float right = ColumnRight[2] + 5;
            void Line(float y) => Shape(new[] { new Vector3(LabelLeft, y - .15f, -.1f), new Vector3(right, y - .15f, -.1f), new Vector3(right, y + .15f, -.1f), new Vector3(LabelLeft, y + .15f, -.1f) }, Rule);
            float mid = (LabelLeft + right) / 2;
            Text("Gold Lion rates", mid, TitleY, .95f, TextAnchor.MiddleCenter);
            Text("Gold Lions for 1000 coins", mid, SubtitleY, .62f, TextAnchor.MiddleCenter);
            string[] names = { "Lions", "Dragons", "Crowns" };
            for (int c = 0; c < 3; c++) Text(names[c], ColumnRight[c] - 5.5f, HeaderY, .64f, TextAnchor.MiddleCenter);
            Line(HeaderY - 3.6f);
            for (int r = 0; r < 5; r++)
            {
                int daysAgo = 4 - r;   // rows run from 4 days ago down to today
                float y = FirstRowY - RowStep * r - (daysAgo == 0 ? 1.2f : 0);
                if (daysAgo == 0) Line(y + RowStep / 2 + .3f);
                Text(Days[daysAgo], LabelLeft, y, .58f, TextAnchor.MiddleLeft);
                for (int c = 0; c < 3; c++)
                {
                    cells[daysAgo, c] = Text("-", ColumnRight[c], y, .68f, TextAnchor.MiddleRight);
                    ups[daysAgo, c] = Arrow(ColumnRight[c] + 3.2f, y + .2f, true);
                    downs[daysAgo, c] = Arrow(ColumnRight[c] + 3.2f, y + .2f, false);
                }
            }
            float legend = FirstRowY - RowStep * 5 - 2.6f;
            Arrow(LabelLeft + 2, legend, true); Text("stronger than the day before", LabelLeft + 5, legend, .58f, TextAnchor.MiddleLeft);
            Arrow(LabelLeft + 2, legend - LegendStep, false); Text("weaker than the day before", LabelLeft + 5, legend - LegendStep, .58f, TextAnchor.MiddleLeft);
            // Background: the booth's scroll behind the rate panel, mirrored, widened outward, shortened and
            // centred on the text (the title's glyphs reach higher above its line than the legend's below).
            var scroll = Instantiate(scrollSource.gameObject, table.transform, false); scroll.name = "scroll";
            foreach (var c in scroll.GetComponentsInChildren<Collider>(true)) Destroy(c);   // never blocks the booth's buttons
            Mirror(root, scroll.transform, scrollSource, ScrollShift, true);
            BoothPose(root, scrollSource, out _, out _, out var scrollScale);
            scroll.transform.localScale = Vector3.Scale(scrollScale, new Vector3(1, ScrollLength, ScrollWiden));
            // Centred on the text along the booth's screen-up axis (local -z); mesh bounds work while the booth is hidden.
            float centre = InTable(frame, new Vector3(0, (TitleY + 3.3f + legend - LegendStep - 2) / 2, 0)).z;
            var shape = scroll.GetComponent<MeshFilter>().sharedMesh;
            scroll.transform.localPosition += new Vector3(0, 0, centre - InTable(scroll.transform, shape.bounds.center).z);
        }
        // `source`'s pose in the booth's space from local transforms (its parents are uniformly scaled, so this is exact).
        private static void BoothPose(Transform root, Transform source, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            position = source.localPosition; rotation = source.localRotation; scale = source.localScale;
            for (var p = source.parent; p && p != root; p = p.parent)
            {
                position = p.localPosition + p.localRotation * Vector3.Scale(p.localScale, position);
                rotation = p.localRotation * rotation; scale = Vector3.Scale(p.localScale, scale);
            }
        }
        // A point of a table child's own space in the table's (the booth's) space.
        private static Vector3 InTable(Transform t, Vector3 point) => t.localPosition + t.localRotation * Vector3.Scale(t.localScale, point);
        // Mirror image across the UI's local YZ plane through the board's centre of `source`'s pose, moved
        // `outward` (screen left), for a child of the table. `flip` turns it half a turn about its own Y axis: the
        // scroll is symmetric across its width, not its thickness, so without it its back would face the player.
        private static void Mirror(Transform root, Transform target, Transform source, float outward, bool flip)
        {
            BoothPose(root, source, out var p, out var q, out _);
            target.localPosition = new Vector3(2 * BoardCentre - p.x + outward, p.y, p.z);
            target.localRotation = new Quaternion(q.x, -q.y, -q.z, q.w) * (flip ? Quaternion.Euler(0, 180, 0) : Quaternion.identity);
        }
    }

    internal static class FxShops
    {
        internal static IslandMarket Market(Shopkeeper shop)
        { var economy=Fields.Get<IslandEconomy>(shop,"economy");return economy?economy.GetComponent<IslandMarket>():null; }
        internal static MoneyQuote Quote(Shopkeeper shop,ShipItem item)
        {
            var m=Market(shop);
            if(!item.sold&&WoodPlanks.IsBundle(item))return WoodPlanks.BundleQuote(m);
            int raw=(int)Fields.Call(shop,"GetPrice",item);
            return Fx.Quote(raw,m,Fx.Currency(m),!item.sold,item.sold?CargoCondition.Factor(item):1);
        }
        internal static bool TryQuote(Shopkeeper shop, ShipItem item, out MoneyQuote quote)
        { try { quote=Quote(shop,item);return true; } catch(OverflowException) { quote=default;return false; } }
        internal static void Trade(Shopkeeper shop,ShipItem item,bool playerBuy)
        {
            if(!World.Ready||World.SaveBlocked||Startup.BlocksGameplay||!item||item.sold==playerBuy)return;
            Fx.AdvanceDay(GameState.day);
            var m=Market(shop);if(!m)return;int currency=Fx.Currency(m);
            var inventory=item.GetComponent<CrateInventory>();
            if(!playerBuy&&inventory&&inventory.containedItems.Count>0){Loans.Notify("Cannot sell with items inside");return;}
            MoneyQuote quote;
            try{quote=Quote(shop,item);}catch(OverflowException){Loans.Notify("Price exceeds wallet limit");return;}
            int price=quote.Total;
            if(playerBuy&&PlayerGold.currency[currency]<price){Loans.Notify("Not enough money");return;}
            if(!playerBuy){
                var c=Loans.Find(item);int net=price;
                if(c!=null){var settlement=Rules.Settle((double)c.principal+c.interest,price,Loans.Rate(Loans.Currency(c)),Loans.Rate(currency));net=settlement.Payout;
                    if((long)PlayerGold.currency[Loans.Currency(c)]-settlement.Shortfall<int.MinValue){Loans.Notify("Wallet limit exceeded");return;}}
                if((long)PlayerGold.currency[currency]+net>int.MaxValue){Loans.Notify("Wallet limit exceeded");return;}
                price=Loans.SettleSale(item,price,currency);
            }
            var good=item.GetComponent<Good>();int g=good?PrefabsDirectory.ItemToGoodIndex(item.GetComponent<SaveablePrefab>().prefabIndex):-1;
            int purchaseCurrency=-1;double purchasePressure=0;
            bool remembered=!playerBuy&&Fx.PurchasePressure(item,out purchaseCurrency,out purchasePressure);
            if(playerBuy){Fields.Get<ShopArea>(shop,"shop").itemsForSale.Remove(item);bool bundle=WoodPlanks.IsBundle(item);item.Sell();PlayerGold.currency[currency]-=price;if(g>0)m.PurchaseGood(g);if(bundle)WoodPlanks.BundleSold(m);}
            else{if(g>0)m.SellGood(g);PlayerGold.currency[currency]+=price;item.DestroyItem();}
            if(playerBuy)Fx.Record(currency,quote.Gross,true,TradeSource.Shop);
            else Fx.RecordSale(currency,quote.Gross,TradeSource.Shop,remembered,purchaseCurrency,purchasePressure);
            if(BuyItemUI.instance){BuyItemUI.instance.DeactivateUI();if(playerBuy)BuyItemUI.instance.recentlyBoughtItem=item;}
            if(DayLogs.instance)DayLogs.instance.dayLogs[currency].LogTransaction(playerBuy?-price:price,item);
            if(MoneyNotification.instance)MoneyNotification.instance.PlayNotif(playerBuy?-price:price,currency);
            if(UISoundPlayer.instance)UISoundPlayer.instance.PlayGoldSound();
        }
    }
    [HarmonyPatch(typeof(Shopkeeper),"GetLocalPrice")] internal static class FxShopQuotePatch
    {static bool Prefix(Shopkeeper __instance,ShipItem item,ref int __result){if(!FxShops.Market(__instance))return true;__result=FxShops.TryQuote(__instance,item,out var q)?q.Total:int.MaxValue;return false;}}
    [HarmonyPatch(typeof(Shopkeeper),"GetLocalPriceString")] internal static class FxShopLabelPatch
    {static bool Prefix(Shopkeeper __instance,ShipItem item,ref string __result){var m=FxShops.Market(__instance);if(!m)return true;__result=FxShops.TryQuote(__instance,item,out var q)?q.Total.ToString()+" "+PlayerGold.GetCurrencyName(Fx.Currency(m)):"Unavailable";return false;}}
    [HarmonyPatch(typeof(Shopkeeper),"TryToSellItem")] internal static class FxShopPurchasePatch
    {static bool Prefix(Shopkeeper __instance,ShipItem item){FxShops.Trade(__instance,item,true);return false;}}
    [HarmonyPatch(typeof(Shopkeeper),"TryToBuyItem")] internal static class FxShopSalePatch
    {static bool Prefix(Shopkeeper __instance,ShipItem item){FxShops.Trade(__instance,item,false);return false;}}

    [HarmonyPatch(typeof(BuyItemUI), "Update")] internal static class ActiveShopQuotePatch
    {
        private static float timer;
        static void Postfix(BuyItemUI __instance)
        {
            timer -= Time.unscaledDeltaTime;
            if (timer > 0) return;
            timer = .15f;
            if (!__instance || !__instance.menu || !__instance.menu.activeInHierarchy || !__instance.activeItem) return;
            var shopkeeper = Fields.Get<Shopkeeper>(__instance, "activeShopkeeper");
            if (!shopkeeper || !__instance.buttonText || !FxShops.Market(shopkeeper)) return;
            string action = Fields.Get<bool>(__instance,"playerIsSelling") ? "Sell" : "Buy";
            string current = FxShops.TryQuote(shopkeeper,__instance.activeItem,out var quote) ? action+" (" + quote.Total + ")" : action+" unavailable";
            if (__instance.buttonText.text != current) __instance.buttonText.text = current;
        }
    }

    // ---- Trade-book trading ----
    internal enum TradeAction { Buy, Loan, Sell }
    internal sealed class TradeUnit
    {
        internal Good Cargo;
        internal MoneyQuote Quote;
        internal int Principal, Interest, Payout, Shortfall, DebtCurrency;
    }
    internal sealed class TradeBatch
    {
        internal TradeAction Action;
        internal IslandMarket Market;
        internal int Good, Currency, Count, Region;
        internal string Error;
        internal CargoCarrier Carrier;
        internal int CartCurrency, CartUnitFee;
        internal long SaleCartFee, StorageFee;
        internal long CartFee => Action == TradeAction.Sell ? SaleCartFee : (long)CartUnitFee * Count;
        internal long CartCharge => CartFee + StorageFee;
        internal List<TradeUnit> Units = new List<TradeUnit>();
        internal bool Valid => Error == null && Units.Count == Count;
        internal long Total => Units.Sum(u => (long)u.Quote.Total);
        internal long Gross => Units.Sum(u => (long)u.Quote.Gross);
        internal long Payout => Units.Sum(u => (long)u.Payout);
        internal long NetPayout => Payout - (Carrier && CartCurrency == Currency ? CartCharge : 0);
        internal string Breakdown()
        {
            if(!Valid)return Error ?? "Cargo unavailable";
            string coin=PlayerGold.GetCurrencyName(Currency);
            if(Action==TradeAction.Sell) {
                string text="Sale value: "+Gross.ToString("N0")+" "+coin+"\nFX fee: "+(Gross-Total).ToString("N0")+"\nLoan repayment: "+(Total-Payout).ToString("N0");
                foreach(var group in Units.Where(u=>u.Shortfall>0).GroupBy(u=>u.DebtCurrency))text+="\nAdditional debit: "+group.Sum(u=>(long)u.Shortfall).ToString("N0")+" "+PlayerGold.GetCurrencyName(group.Key);
                if(Carrier) {
                    string cartCoin=PlayerGold.GetCurrencyName(CartCurrency);
                    text+="\nCart selling (20x): "+CartFee.ToString("N0")+" "+cartCoin+"\nStorage: "+StorageFee.ToString("N0")+" "+cartCoin;
                    text+="\nCart cash required upfront: "+CartCharge.ToString("N0")+" "+cartCoin;
                }
                text+="\nReceive: "+NetPayout.ToString("N0")+" "+coin;
                if(Carrier&&CartCurrency!=Currency)text+="\nSeparate cart debit: "+CartCharge.ToString("N0")+" "+PlayerGold.GetCurrencyName(CartCurrency);
                return text;
            }
            long principal=Units.Sum(u=>(long)u.Principal);
            string result="Cargo cost: "+Total.ToString("N0")+" "+coin+(Action==TradeAction.Loan?"\nBorrow: "+principal.ToString("N0")+" "+coin:"");
            long cash=Total-principal;
            if(Carrier)result+="\nCart loading (10x): "+CartFee.ToString("N0")+" "+PlayerGold.GetCurrencyName(CartCurrency);
            result+="\nCash required: "+(cash+(Carrier&&CartCurrency==Currency?CartFee:0)).ToString("N0")+" "+coin;
            if(Carrier&&CartCurrency!=Currency)result+=" + "+CartFee.ToString("N0")+" "+PlayerGold.GetCurrencyName(CartCurrency);
            return result;
        }
    }
    internal static class BulkTrade
    {
        internal static int KeyQuantity(bool alt, bool ctrl, bool shift) => shift ? 20 : ctrl ? 10 : alt ? 5 : 1;
        internal static int HeldQuantity() => KeyQuantity(Input.GetKey(KeyCode.LeftAlt)||Input.GetKey(KeyCode.RightAlt),
            Input.GetKey(KeyCode.LeftControl)||Input.GetKey(KeyCode.RightControl), Input.GetKey(KeyCode.LeftShift)||Input.GetKey(KeyCode.RightShift));
        internal static int Quantity(GoPointerButton button) => ButtonHover.IsOver(button)?HeldQuantity():1;
        internal static IEnumerable<Good> SaleGoods(IslandMarket market,int goodIndex,CargoCarrier carrier=null)
        {
            var area=market.GetWarehouseArea();
            if(!area)return Enumerable.Empty<Good>();
            IEnumerable<Good> source=carrier ? carrier.cargo.Where(item=>item&&item.GetCurrentInventorySlot()==carrier.portIndex+100).Select(item=>item.GetComponent<Good>()) :
                Fields.Get<List<Good>>(area,"goodsInArea").Where(g=>g&&g.GetComponent<ShipItem>().GetCurrentInventorySlot()<100);
            return source.Where(g=>g&&g.GetComponent<ShipItem>().sold&&g.GetMissionIndex()==-1&&
                PrefabsDirectory.ItemToGoodIndex(g.GetComponent<SaveablePrefab>().prefabIndex)==goodIndex&&(bool)Fields.Call(area,"IsGoodValid",g)).Distinct();
        }
        internal static TradeBatch Quote(EconomyUI ui, TradeAction action, int count)
        {
            var batch=new TradeBatch { Action=action,Count=count,Market=Fields.Get<IslandMarket>(ui,"currentIsland"),Good=ui.currentSelectedGood };
            var m=batch.Market;
            if(!World.Ready||World.SaveBlocked||Startup.BlocksGameplay||!m||count<1||count>20) {batch.Error="Economy unavailable";return batch;}
            batch.Region=Reputations.Account(m); batch.Currency=action==TradeAction.Loan?Fx.Currency(m):(int)Fields.Get<Currency>(ui,"currentPlayerCurrency");
            if(batch.Currency!=Fx.Currency(m)&&!m.allowCurrencyConversion){batch.Error="Wrong currency";return batch;}
            int g=batch.Good;
            var prefab=PrefabsDirectory.instance.GetGood(g);
            if(!prefab||g<=0||g==45||g==51){batch.Error="Cargo unavailable";return batch;}
            if(action!=TradeAction.Sell&&!Rules.PurchaseOriginAllowed(m.GetPortIndex(),g)){batch.Error="Saffron can only be purchased at Saffron Island";return batch;}
            if(action==TradeAction.Loan){batch.Error=Loans.BlockReason(batch.Region,batch.Currency);if(batch.Error!=null)return batch;}
            long[] wallets=PlayerGold.currency.Select(x=>(long)x).ToArray();
            double credit=Loans.Available(batch.Region),stock=m.currentSupply[g];
            var eligible=new List<Good>();
            try {
                if(ui.GetComponent<BulkTradeUI>() && ui.GetComponent<BulkTradeUI>().CartSelected){
                    batch.Carrier=CartDelivery.Hired(m,out batch.Error);if(!batch.Carrier)return batch;
                    batch.CartCurrency=(int)batch.Carrier.currency;
                }
                if(action==TradeAction.Sell){
                    eligible=SaleGoods(m,g,batch.Carrier).Take(count).ToList();
                    if(eligible.Count<count){batch.Error="Not enough "+(batch.Carrier?"cart":"warehouse")+" cargo for the full batch";return batch;}
                }
                if(batch.Carrier){
                    if(action==TradeAction.Sell) foreach(var good in eligible) {
                        var item=good.GetComponent<ShipItem>();
                        batch.SaleCartFee+=CartDelivery.Fee(batch.Carrier,item,20);
                        int storage=batch.Carrier.GetWithdrawPrice(batch.Carrier.cargo.IndexOf(item));
                        if(storage<0)throw new OverflowException("Storage fee exceeds wallet limit");
                        batch.StorageFee+=storage;
                    }
                    else batch.CartUnitFee=CartDelivery.Fee(batch.Carrier,prefab.GetComponent<ShipItem>());
                    if(batch.CartCharge>int.MaxValue)throw new OverflowException("Cart fee exceeds wallet limit");
                    wallets[batch.CartCurrency]-=batch.CartCharge;
                    if(wallets[batch.CartCurrency]<0){batch.Error="Not enough cash for cart fees of the full order";return batch;}
                }
                for(int i=0;i<count;i++){
                    bool buy=action!=TradeAction.Sell;
                    if(buy&&stock<m.supplyPurchaseLimit){batch.Error="Not enough stock for the full batch";return batch;}
                    float basePrice=m.GetGoodPriceAtSupply(g,(float)(stock+(buy?0:1)));
                    int raw=Mathf.RoundToInt(basePrice*(1+(buy?1:-1)*(float)Rules.Spread(basePrice)));
                    var cargo=buy?null:eligible[i];
                    var u=new TradeUnit {Cargo=cargo,Quote=Fx.Quote(raw,m,batch.Currency,buy,cargo?CargoCondition.Factor(cargo.GetComponent<ShipItem>()):1)};
                    if(action==TradeAction.Loan){
                        u.Principal=(int)Math.Min(u.Quote.Total,Math.Floor(credit*Loans.Rate(batch.Currency)));
                        if(u.Principal<=0){batch.Error="Not enough credit for the full batch";return batch;}
                        u.Interest=checked((int)Math.Ceiling(u.Principal*Rules.Interest(Reputations.Level(batch.Region))-1e-8));
                        if((long)u.Principal+u.Interest>int.MaxValue)throw new OverflowException("Loan repayment exceeds wallet limit");
                        credit-=u.Principal/Loans.Rate(batch.Currency);
                    }
                    if(buy){wallets[batch.Currency]-=u.Quote.Total-u.Principal;if(wallets[batch.Currency]<0){batch.Error="Not enough money for the full batch";return batch;}}
                    else{
                        u.Payout=u.Quote.Total;
                        var loan=Loans.Find(cargo.GetComponent<ShipItem>());
                        if(loan!=null){
                            u.DebtCurrency=Loans.Currency(loan);
                            var settle=Rules.Settle((double)loan.principal+loan.interest,u.Quote.Total,Loans.Rate(u.DebtCurrency),Loans.Rate(batch.Currency));
                            // As one sale at a time: a shortfall is charged even below 0 (the trade book shows the batch's total).
                            u.Payout=settle.Payout;u.Shortfall=settle.Shortfall;
                            wallets[u.DebtCurrency]-=u.Shortfall;
                        }
                        wallets[batch.Currency]+=u.Payout;
                    }
                    if(wallets.Any(v=>v<int.MinValue||v>int.MaxValue)){batch.Error="Wallet limit exceeded";return batch;}
                    batch.Units.Add(u);stock+=(buy?-1:1);
                }
            } catch(OverflowException){batch.Error="Price or wallet limit exceeded";}
            return batch;
        }
        internal static void Execute(EconomyUI ui,TradeAction action,int count)
        {
            Fx.AdvanceDay(GameState.day);
            var batch=Quote(ui,action,count);
            if(!batch.Valid){Loans.Notify(batch.Error??"Cargo unavailable");ui.RefreshPage();return;}
            var m=batch.Market;int g=batch.Good,currency=batch.Currency;
            // Stage every physical purchase before touching money, stock or credit.
            var staged=new List<Good>();
            if(action!=TradeAction.Sell){
                try{
                    for(int i=0;i<count;i++){
                        var obj=UnityEngine.Object.Instantiate(PrefabsDirectory.instance.GetGood(g).gameObject,m.transform.position+Vector3.up,CargoPlacement.Frame(m));
                        var good=obj.GetComponent<Good>();staged.Add(good);
                        var item=obj.GetComponent<ShipItem>();item.sold=true;
                        obj.GetComponent<SaveablePrefab>().RegisterToSave();good.RegisterAsMissionless();
                        if(CargoCondition.Track(item)==null)throw new InvalidOperationException("Cargo registration failed");
                    }
                    if(batch.Carrier)CartDelivery.Stage(batch,staged);
                    else if(!CargoPlacement.Place(m,staged,out int found)){
                        // No room even turned: a bulk order is refused; a single unit drops the game's own way.
                        if(count>1)throw new CargoPlacement.NoRoom(found,count);
                        CargoPlacement.Drop(m,staged[0]);
                    }
                }catch(Exception e){
                    foreach(var good in staged)if(good)good.GetComponent<ShipItem>().DestroyItem();
                    Plugin.Log?.LogWarning(e);Loans.Notify(e is CargoPlacement.NoRoom?CargoPlacement.NoRoom.Notice:e.Message+"; no trade was charged.");return;
                }
            }
            int[] walletBefore=(int[])PlayerGold.currency.Clone();
            float stockBefore=m.currentSupply[g];
            float tradedBefore=m.debugTotalGoodsTraded;
            double[] pressureBefore=(double[])Fx.State.cargo.Clone();
            Economy.BatchDepth++;
            try{
                if(batch.Carrier)PlayerGold.currency[batch.CartCurrency]=checked(PlayerGold.currency[batch.CartCurrency]-(int)batch.CartCharge);
                for(int i=0;i<count;i++){
                    var u=batch.Units[i];ShipItem item;
                    if(action==TradeAction.Sell){
                        item=u.Cargo.GetComponent<ShipItem>();
                        bool remembered=Fx.PurchasePressure(item,out int purchaseCurrency,out double purchasePressure);
                        int net=Loans.SettleSale(item,u.Quote.Total,currency);
                        if(Fields.Get<List<Good>>(m.GetWarehouseArea(),"goodsInArea").Remove(u.Cargo))
                            m.currentPlayerGoods[g]=Math.Max(0,m.currentPlayerGoods[g]-1);
                        if(batch.Carrier)CartDelivery.Detach(item);
                        item.DestroyItem();m.SellGood(g);PlayerGold.currency[currency]=checked(PlayerGold.currency[currency]+net);
                        Fx.RecordSale(currency,u.Quote.Gross,TradeSource.Cargo,remembered,purchaseCurrency,purchasePressure);
                    }else{
                        item=staged[i].GetComponent<ShipItem>();
                        if(action==TradeAction.Loan){var loan=CargoCondition.Track(item);loan.loan=true;loan.region=batch.Region;loan.goldLoan=currency==3;loan.principal=u.Principal;loan.interest=u.Interest;loan.rate=Rules.Interest(Reputations.Level(batch.Region));loan.reservation=u.Principal/Loans.Rate(currency);}
                        PlayerGold.currency[currency]-=u.Quote.Total-u.Principal;m.PurchaseGood(g);
                        Fx.RememberPurchase(item,currency,Fx.Record(currency,u.Quote.Gross,true,TradeSource.Cargo));
                    }
                }
            }catch(Exception e){
                if(action!=TradeAction.Sell){
                    Array.Copy(walletBefore,PlayerGold.currency,4);m.currentSupply[g]=stockBefore;m.debugTotalGoodsTraded=tradedBefore;Array.Copy(pressureBefore,Fx.State.cargo,4);
                    foreach(var good in staged)if(good){var item=good.GetComponent<ShipItem>();var record=Loans.Find(item);if(record!=null)record.loan=false;item.DestroyItem();}
                    Plugin.Log?.LogError(e);Loans.Notify("Order cancelled; no trade was charged.");return;
                }
                throw;
            }finally{Economy.BatchDepth--;}
            // Receipt/audio callbacks are presentation, after the complete commit.
            // A failed display must not roll back already logged transaction units.
            try{
                for(int i=0;i<count;i++){
                    var u=batch.Units[i];bool sale=action==TradeAction.Sell;
                    var item=(sale?u.Cargo:staged[i]).GetComponent<ShipItem>();
                    if(DayLogs.instance)DayLogs.instance.dayLogs[currency].LogTransaction(sale?u.Payout:-(u.Quote.Total-u.Principal),item);
                    if(EconomyUIReceiptScribe.instance)EconomyUIReceiptScribe.instance.AddTransaction(g,sale?-1:1,sale?u.Payout:u.Quote.Total,currency);
                }
            }catch(Exception e){Plugin.Log?.LogError("Trade completed, but receipt display failed: "+e);}
            if(batch.Carrier){
                if(action!=TradeAction.Sell)GameState.loadedCargoIntoCart=true;
                if(DayLogs.instance)DayLogs.instance.dayLogs[batch.CartCurrency].LogTransaction(-(int)batch.CartCharge,TransactionCategory.other);
            }
            Economy.Refresh(m); if(UISoundPlayer.instance)UISoundPlayer.instance.PlayGoldSound();ui.RefreshPage();
        }
    }
    [HarmonyPatch(typeof(EconomyUI),"BuyGood")] internal static class BulkBuyPatch
    { static bool Prefix(EconomyUI __instance){BulkTrade.Execute(__instance,TradeAction.Buy,BulkTrade.Quantity(Fields.Get<EconomyUIButton>(__instance,"buyButton")));return false;} }
    // The trade book uses Ctrl for x10 (HeldQuantity), and vanilla toggles crouch on the crouch key whenever the
    // game is playing, so Ctrl in the open book crouched the player. The key press is ignored while the book is open.
    // GameInput and InputName live in the game's Oculus.VR assembly, so the key check is found by name and
    // filtered right after it instead of being replaced.
    [HarmonyPatch(typeof(PlayerCrouching),"Update")] internal static class BookCrouchPatch
    {
        internal static bool NotInBook(bool pressed) => pressed && !(EconomyUI.instance && EconomyUI.instance.uiActive);
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var keyDown = AccessTools.Method(AccessTools.TypeByName("GameInput"), "GetKeyDown");
            var list = instructions.ToList();
            int at = keyDown == null ? -1 : list.FindIndex(c => c.Calls(keyDown));
            if (at < 0 || list.Count(c => c.Calls(keyDown)) != 1) { Plugin.Log?.LogWarning("Crouch key check not found; Ctrl in the trade book may still crouch."); return list; }
            list.Insert(at + 1, new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(BookCrouchPatch), nameof(NotInBook))));
            return list;
        }
    }

    [DefaultExecutionOrder(1000)]
    public sealed class BulkTradeUI : MonoBehaviour
    {
        private EconomyUI ui;
        private EconomyUIButton buy, sell;
        private LoanAction loan;
        private TextMesh loanText;
        private float timer;
        private int previousKey=-1, previousHover=-1;
        private LoanAction cart;
        private TextMesh cartMark, cartLabel;
        private GameObject cartRoot;
        private IslandMarket cartMarket;
        private bool cartSelected;
        private float buyTextSize, loanTextSize, sellTextSize;
        private TextMesh storageLabel;
        private string storageLabelText;
        internal bool CartSelected => cartSelected && ui && ui.uiActive && !World.Loading && cartMarket == Fields.Get<IslandMarket>(ui,"currentIsland");
        internal static void Attach(EconomyUI ui,LoanAction loan,TextMesh text)
        {
            var v=ui.GetComponent<BulkTradeUI>();if(!v)v=ui.gameObject.AddComponent<BulkTradeUI>();
            v.ui=ui;v.loan=loan;v.loanText=text;v.buy=Fields.Get<EconomyUIButton>(ui,"buyButton");v.sell=Fields.Get<EconomyUIButton>(ui,"sellButton");
            v.buyTextSize=Fields.Get<TextMesh>(v.buy,"text").characterSize;v.loanTextSize=text.characterSize;
            v.sellTextSize=Fields.Get<TextMesh>(v.sell,"text").characterSize;
            v.storageLabel=ui.GetComponentsInChildren<TextMesh>(true).FirstOrDefault(t=>t.text.Contains("storage area"));
            if(v.storageLabel)v.storageLabelText=v.storageLabel.text;
            if(!v.cartRoot)v.CreateCartControl();
            foreach(var button in new[]{v.buy,v.sell}){
                var label=Fields.Get<TextMesh>(button,"text");
                if(label&&!label.GetComponent<LoanTextFit>()){
                    var fit=label.gameObject.AddComponent<LoanTextFit>();fit.originalScale=label.transform.localScale;
                    // Keep the label within its native plate in parent coordinates.
                    var plate=button.GetComponent<MeshRenderer>();fit.maxWidth=BookUI.InFrameBounds(plate,label.transform.parent).size.x * .92f;
                }
            }
        }
        // The parts the health check looks for (TradeLoanUI.Check).
        internal IEnumerable<string> Missing()
        {
            if(!ui)yield return "bulk controls' book link";
            if(!cartRoot)yield return "cart box";
            if(!cart)yield return "cart box button";
            if(!buy||!sell)yield return "Buy/Sell buttons the bulk labels use";
        }
        private void LateUpdate()
        {
            try{Tick();}catch(Exception e){BookLog.Error("updating the bulk and cart controls",e);}
        }
        private void Tick()
        {
            if(cartRoot)cartRoot.SetActive(ui&&ui.uiActive);
            if(!ui||!ui.uiActive||World.Loading){cartSelected=false;return;}
            if(!ui||!ui.uiActive||!World.Ready||Startup.BlocksGameplay)return;
            int key=BulkTrade.HeldQuantity(),hover=ButtonHover.IsOver(buy)?1:ButtonHover.IsOver(sell)?2:ButtonHover.IsOver(loan)?3:0;
            timer-=Time.unscaledDeltaTime;
            if(key!=previousKey||hover!=previousHover||timer<=0){previousKey=key;previousHover=hover;timer=.15f;Refresh();}
        }
        internal void Refresh()
        {
            try{RefreshLabels();}catch(Exception e){BookLog.Error("refreshing the bulk labels",e);}
        }
        private void RefreshLabels()
        {
            if(!ui||!ui.uiActive||!World.Ready||Startup.BlocksGameplay)return;
            var current=Fields.Get<IslandMarket>(ui,"currentIsland");
            if(cartMarket!=current){cartMarket=current;cartSelected=false;}
            var carrier=CartDelivery.Hired(current,out string cartError);
            // Receive a click even without a hire so its reason can be shown.
            // ToggleCart still validates service eligibility before selecting it.
            cart.unclickable=false;
            cart.description=cartSelected?"Use warehouse cargo for purchases and sales instead":cartError??"Buy/Loan: load into your hired cart (10x normal transport fee). Sell: sell from that cart (20x normal transport fee plus storage). Cart fees require cash upfront.";
            cartMark.text=cartSelected?"x":"";
            cartLabel.color=Color.black;
            if(current) {
                var amounts=Fields.Get<TextMesh>(ui,"textGoodAmounts");
                int stock=Mathf.FloorToInt(Mathf.Max(0,current.currentSupply[ui.currentSelectedGood]-current.supplyPurchaseLimit+1));
                if(!Rules.PurchaseOriginAllowed(current.GetPortIndex(),ui.currentSelectedGood))stock=0;
                int available=CartSelected ? (carrier?BulkTrade.SaleGoods(current,ui.currentSelectedGood,carrier).Count():0) : current.currentPlayerGoods[ui.currentSelectedGood];
                amounts.text=stock+"\n"+available;
                if(storageLabel)storageLabel.text=CartSelected?storageLabelText.Replace("storage area","hired cart"):storageLabelText;
            }
            Apply(buy,TradeAction.Buy,BulkTrade.Quantity(buy));Apply(sell,TradeAction.Sell,BulkTrade.Quantity(sell));Apply(loan,TradeAction.Loan,BulkTrade.Quantity(loan));
            var m=Fields.Get<IslandMarket>(ui,"currentIsland");
            var note=Fields.Get<TextMesh>(ui,"textConversionInfo");
            if(m) {
                int selected=(int)Fields.Get<Currency>(ui,"currentPlayerCurrency");
                note.text=selected!=Fx.Currency(m)?"FX fees included":m.GetPortIndex()==21?"Gold Lions":"";
            }
        }
        internal void ResetCart() { cartSelected=false;cartMarket=ui?Fields.Get<IslandMarket>(ui,"currentIsland"):null; }
        internal void ToggleCart() {
            if(!CartSelected && !CartDelivery.Hired(Fields.Get<IslandMarket>(ui,"currentIsland"),out string error)){
                Loans.Notify(error);Refresh();return;
            }
            cartSelected=!CartSelected;ui.RefreshPage();
        }
        private void CreateCartControl()
        {
            var source=buy.GetComponent<MeshRenderer>();var frame=source.transform.parent.parent.parent;
            cartRoot=new GameObject("Trade book cart delivery");cartRoot.layer=source.gameObject.layer;cartRoot.transform.SetParent(frame,false);
            var font=Fields.Get<TextMesh>(ui,"textBuyPrice").font;
            // Approved gap: below storage quantity, above Currency, left of Receipt.
            cartLabel=BookUI.Text(cartRoot.transform,font,"Load to/sell from cart",.311f,-.032f,.012f,.122f,TextAnchor.MiddleLeft);
            cartMark=BookUI.Text(cartRoot.transform,font,"",.299f,-.032f,.014f,.009f,TextAnchor.MiddleCenter);
            var box=new GameObject("Cart delivery checkbox");box.layer=cartRoot.layer;box.transform.SetParent(cartRoot.transform,false);
            box.transform.localPosition=BookUI.Position(.299f,-.032f);
            var vertices=new System.Collections.Generic.List<Vector3>();var triangles=new System.Collections.Generic.List<int>();
            const float h=.0055f,t=.0006f;
            AddRect(vertices,triangles,-h,-h,h,-h+t);AddRect(vertices,triangles,-h,h-t,h,h);
            AddRect(vertices,triangles,-h,-h,-h+t,h);AddRect(vertices,triangles,h-t,-h,h,h);
            var mesh=new Mesh();mesh.vertices=vertices.ToArray();mesh.triangles=triangles.ToArray();mesh.RecalculateNormals();mesh.RecalculateBounds();
            box.AddComponent<MeshFilter>().sharedMesh=mesh;
            var material=new Material(source.sharedMaterial);material.color=Color.black;box.AddComponent<MeshRenderer>().sharedMaterial=material;
            cart=box.AddComponent<LoanAction>();cart.action=ToggleCart;cart.ownedMesh=mesh;cart.ownedMaterial=material;
            var hit=box.AddComponent<BoxCollider>();hit.center=new Vector3(-.064f,0,0);hit.size=new Vector3(.142f,.008f,.019f);
            // The old conversion note overlapped the bottom price-table rows.
            // Put its compact equivalent left of the checkbox, below debt warnings.
            var oldNote=Fields.Get<TextMesh>(ui,"textConversionInfo");oldNote.gameObject.SetActive(false);
            var note=BookUI.Text(cartRoot.transform,oldNote.font,"",.205f,-.032f,.011f,.083f,TextAnchor.MiddleLeft);
            note.fontStyle=FontStyle.Italic;Fields.Set(ui,"textConversionInfo",note);
        }
        private static void AddRect(System.Collections.Generic.List<Vector3> v,System.Collections.Generic.List<int> t,float x1,float z1,float x2,float z2)
        {
            int n=v.Count;v.Add(new Vector3(x1,0,z1));v.Add(new Vector3(x2,0,z1));v.Add(new Vector3(x2,0,z2));v.Add(new Vector3(x1,0,z2));
            t.AddRange(new[]{n,n+2,n+1,n,n+3,n+2});
        }
        private void Apply(GoPointerButton button,TradeAction action,int count)
        {
            var q=BulkTrade.Quote(ui,action,count);button.unclickable=!q.Valid;button.description=q.Breakdown();
            string name=action.ToString()+(count>1?" "+count:"");
            string label=!q.Valid ? name+" unavailable" : name+" ("+(action==TradeAction.Sell?q.NetPayout:q.Total).ToString("N0")+")";
            if(q.Valid&&q.Carrier)label+="\nCart: "+q.CartCharge.ToString("N0")+" "+PlayerGold.GetCurrencyName(q.CartCurrency);
            if(button==loan){loanText.characterSize=loanTextSize*(q.Carrier ? .78f : 1);loanText.text=label;return;}
            var native=(EconomyUIButton)button;native.SetButtonText(label);
            if(button==buy)Fields.Get<TextMesh>(buy,"text").characterSize=buyTextSize*(q.Carrier ? .78f : 1);
            if(button==sell)Fields.Get<TextMesh>(sell,"text").characterSize=sellTextSize*(label.Contains("\n") ? .78f : 1);
            native.SetButtonMaterial(Fields.Get<Material[]>(ui,"buttonMaterials")[q.Valid?0:1]);
        }
    }
    [HarmonyPatch(typeof(EconomyUI),"RefreshPage")] internal static class BulkRefreshPatch
    {static void Postfix(EconomyUI __instance){var v=__instance.GetComponent<BulkTradeUI>();if(v)v.Refresh();}}
    [HarmonyPatch(typeof(EconomyUI),"OpenUI")] internal static class CartSessionPatch
    {static void Prefix(EconomyUI __instance){var v=__instance.GetComponent<BulkTradeUI>();if(v)v.ResetCart();}}

    // Native IsLookedAt stops being refreshed when a button is unclickable.
    // Eligibility must not change which batch the pointer is asking for.
    internal static class ButtonHover
    {
        internal static bool IsOver(GoPointerButton button)
        {
            if (!button || !button.gameObject.activeInHierarchy) return false;
            if (!GameState.inCursorMenu) return button.IsLookedAt();
            var pointer = MouseButtonPointer.instance;
            if (!pointer) return false;
            var uiCamera = Fields.Get<Camera>(pointer, "uiCam");
            var worldCamera = Camera.main;
            if (!uiCamera || !worldCamera) return false;
            bool mouse = Fields.Get<bool>(pointer, "trackMouse");
            Vector3 uiScreen = mouse ? Input.mousePosition : uiCamera.WorldToScreenPoint(pointer.controllerPointer.position);
            Vector3 worldScreen = mouse ? Input.mousePosition : worldCamera.WorldToScreenPoint(pointer.controllerPointer.position);
            RaycastHit hit;
            if (!Physics.Raycast(uiCamera.ScreenPointToRay(uiScreen), out hit, 160f, 32) &&
                !Physics.Raycast(worldCamera.ScreenPointToRay(worldScreen), out hit, 160f, 8388608)) return false;
            return hit.collider.GetComponent<GoPointerButton>() == button;
        }
    }

    // Uses native carrier ownership/save slots. The higher fee applies only to
    // trade-book delivery; normal cart loading and later storage are unchanged.
    internal static class CartDelivery
    {
        internal static CargoCarrier Hired(IslandMarket market, out string error)
        {
            error = "Hire this port's cargo transport first";
            var storage = CargoStorageUI.instance;
            if (!market || !storage) return null;
            var carrier = Fields.Get<CargoCarrier>(storage, "currentCarrier");
            var dude = Fields.Get<Transform>(storage, "currentDude");
            var port = market.GetComponent<Port>();
            var island = port && port.island ? port.island.GetComponentInParent<IslandHorizon>() : market.GetComponentInParent<IslandHorizon>();
            if (!island) { error = "Cargo transport unavailable at this port"; return null; }
            // Vanilla carrier slots use island IDs, not market IDs (Neverdin 3 vs 2).
            // Where a streamed NPC is available, validate its explicit registration too.
            var npc = dude ? dude.GetComponent<CargoTransportDude>() : null;
            var scenery = dude ? dude.GetComponentInParent<IslandSceneryScene>() : null;
            int service = scenery && scenery.parentIslandIndex == island.islandIndex && npc ? npc.carrierIndex : island.islandIndex;
            if (!carrier || CargoCarrier.carriers == null || service < 0 || service >= CargoCarrier.carriers.Length ||
                carrier != CargoCarrier.carriers[service] || carrier.portIndex != service || !dude ||
                (scenery && scenery.parentIslandIndex != island.islandIndex) || (npc && npc.carrierIndex != service) ||
                !Refs.observerMirror || Vector3.Distance(Refs.observerMirror.transform.position, dude.position) > 180) return null;
            error = null; return carrier;
        }
        internal static int Fee(CargoCarrier carrier, ShipItem item, int multiplier = 10)
        {
            int normal = carrier.GetTransportPrice(item);
            if (normal < 0) throw new OverflowException("Cart fee exceeds wallet limit");
            return checked(normal * multiplier);
        }
        internal static void Stage(TradeBatch batch, List<Good> goods)
        {
            var carrier = Hired(batch.Market, out string error);
            if (!carrier || carrier != batch.Carrier) throw new InvalidOperationException(error ?? "Cart service changed");
            foreach (var good in goods)
            {
                var item = good.GetComponent<ShipItem>();
                if (!item.GetItemRigidbody() || !item.GetComponent<Collider>() || item is ShipItemCrate && item.amount <= 0 || Fee(carrier,item) != batch.CartUnitFee)
                    throw new InvalidOperationException("Cargo cannot be loaded into the cart");
            }
            // The adapter performs the native attachment steps without native
            // per-item charging. Aggregate cash is validated by the batch quote.
            foreach (var good in goods)
            {
                var item = good.GetComponent<ShipItem>();
                item.GetItemRigidbody().EnterInventorySlot(carrier.transform);
                item.InsertIntoCargoCarrier(carrier);
                item.transform.localScale = Vector3.zero;
                item.daysInStorage = 0;
                carrier.cargo.Add(item);
            }
        }
        internal static void Detach(ShipItem item)
        {
            if (CargoCarrier.carriers == null) return;
            bool removed=false;
            foreach (var carrier in CargoCarrier.carriers) if (carrier) removed |= carrier.cargo.Remove(item);
            // Native EnterInventorySlot starts a next-frame layer update on the
            // separate rigidbody object. Destruction otherwise leaves that update
            // racing ShipItem.OnDestroy, notably during a rolled-back order.
            if(removed && item.GetItemRigidbody())item.GetItemRigidbody().StopAllCoroutines();
        }
    }

    internal static class CargoPlacement
    {
        internal const float Gap = .03f;
        internal static Bounds Footprint(Good good, Quaternion rotation)
        {
            bool initialized=false;var bounds=new Bounds();var inverse=Quaternion.Inverse(rotation);
            foreach(var collider in good.GetComponentsInChildren<Collider>()){
                Bounds local;
                if(collider is BoxCollider box)local=new Bounds(box.center,box.size);
                else if(collider is MeshCollider mesh && mesh.sharedMesh)local=mesh.sharedMesh.bounds;
                else if(collider is SphereCollider sphere)local=new Bounds(sphere.center,Vector3.one*sphere.radius*2);
                else if(collider is CapsuleCollider capsule){var size=Vector3.one*capsule.radius*2;size[capsule.direction]=Mathf.Max(capsule.height,size[capsule.direction]);local=new Bounds(capsule.center,size);}
                else throw new InvalidOperationException("Unsupported cargo shape");
                for(int i=0;i<8;i++){
                    var p=local.center+Vector3.Scale(local.extents,Corner(i));
                    p=inverse*(collider.transform.TransformPoint(p)-good.transform.position);
                    if(!initialized){bounds=new Bounds(p,Vector3.zero);initialized=true;}else bounds.Encapsulate(p);
                }
            }
            if(!initialized)throw new InvalidOperationException("Cargo shape unavailable");
            return bounds;
        }
        private static Vector3 Corner(int i)=>new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1);
        internal static bool Inside(BoxCollider area,Vector3 center,Vector3 half,Quaternion rotation)
        {
            for(int i=0;i<8;i++){
                Vector3 p=area.transform.InverseTransformPoint(center+rotation*Vector3.Scale(half,Corner(i)))-area.center;
                if(Mathf.Abs(p.x)>area.size.x*.5f || Mathf.Abs(p.y)>area.size.y*.5f || Mathf.Abs(p.z)>area.size.z*.5f)return false;
            }
            return true;
        }
        private static bool Cargo(Collider c)=>c.GetComponentInParent<ShipItem>() || c.GetComponentInParent<ItemRigidbody>();
        private sealed class Candidate
        {
            internal Vector3 Center;
            internal bool ReservedSupport, Stack;
        }
        // How far a crate's bottom would sit above the ground (not cargo) under its centre.
        private static float AboveGround(Vector3 center, float halfHeight, HashSet<Collider> own)
        {
            var bottom = center - Vector3.up * (halfHeight + Gap);
            foreach (var hit in Physics.RaycastAll(bottom + Vector3.up * .01f, Vector3.down, 30, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
                if (!own.Contains(hit.collider) && !Cargo(hit.collider)) return bottom.y - hit.point.y;
            return float.PositiveInfinity;
        }
        private static bool Ignored(Collider c, HashSet<Collider> own) => own.Contains(c) || (c.isTrigger && !Cargo(c));
        private static bool StableCargo(Collider c)
        {
            if (!Cargo(c)) return false;
            var item = c.GetComponentInParent<ShipItem>();
            var rigid = item ? item.GetItemRigidbody() : c.GetComponentInParent<ItemRigidbody>();
            if (!item && rigid) item = rigid.GetShipItem();
            if (item && (item.held || item.GetCurrentInventorySlot() >= 100)) return false;
            var body = rigid ? rigid.GetBody() : c.attachedRigidbody;
            return !body || body.isKinematic || (body.velocity.sqrMagnitude < .04f && body.angularVelocity.sqrMagnitude < .04f);
        }
        private static bool Supported(Vector3 center, Vector3 half, Quaternion rotation, HashSet<Collider> own)
        {
            float top = center.y - half.y - Gap;
            // Center and all four corners must rest on the same local height. Each
            // cell has its own support height; no shared pickup-floor/roof constraint.
            for (int i = 0; i < 5; i++) {
                var offset = i == 4 ? Vector3.zero : new Vector3((i & 1) == 0 ? -half.x + .005f : half.x - .005f, 0,
                    (i & 2) == 0 ? -half.z + .005f : half.z - .005f);
                var start = center + rotation * offset; start.y = top + .06f;
                var hits = Physics.RaycastAll(start, Vector3.down, .12f, ~0, QueryTriggerInteraction.Collide)
                    .Where(h => !Ignored(h.collider, own)).OrderBy(h => h.distance).ToArray();
                if (hits.Length == 0) return false;
                var hit = hits[0];
                if (hit.normal.y < .98f || Mathf.Abs(hit.point.y - top) > .025f ||
                    (Cargo(hit.collider) ? !StableCargo(hit.collider) : hit.collider.attachedRigidbody != null)) return false;
            }
            return true;
        }
        // Uneven floors and slopes (the user, 2026-10-08: placed cargo must never slide or move). Where the level test above fails,
        // the crate rests on the plane through the highest ground under it (an upper face of the ground points' hull with its
        // centre inside), tilted to that plane, if the tilt is within its shape's limit. Tested in game (-SlopeProbe, 10 s of the
        // game's physics): nothing slid below 30°; tall thin cargo tips first, at atan(thickness / height) (rabbit furs 16°,
        // lumber 8°). The limit keeps TipMargin under that, and under MaxTilt (3°, see RoughPorts).
        // Only at the ports whose warehouses are rough ground (the user, 2026-10-08): Al'Nilem (1) and Mirage Mountain (32) (Dragon Cliffs tried: it lost capacity). Measured
        // with the game's physics (-WarehouseSurvey, 10 s after a full warehouse): there nothing moved at 3°, a 5 cm dip,
        // stacked only on level crates (40 and 20 standard crates; 29 and 12 before). MaxStack (no limit) is for measuring.
        internal static readonly HashSet<int> RoughPorts = new HashSet<int> { 1, 32 };
        internal static float MaxTilt = 3f, TipMargin = 4f, MaxStack = float.PositiveInfinity, MaxDip = .05f;
        private static float FloorNormal => Mathf.Cos(MaxTilt * Mathf.Deg2Rad);
        private const float LevelTop = .99995f;   // cos 0.57°
        internal static float TiltLimit(Vector3 size) => Mathf.Min(MaxTilt, Mathf.Atan2(Mathf.Min(size.x, size.z), size.y) * Mathf.Rad2Deg - TipMargin);
        // level: the crate stands on a level floor (the test above); only such a crate takes another on top.
        private static bool Rest(ref Vector3 center, ref Quaternion rotation, Vector3 half, HashSet<Collider> own, bool rough, out bool level)
        {
            level = Supported(center, half, rotation, own); if (level || !rough) return level;
            float limit = TiltLimit(half * 2); if (limit <= 0) return false;
            float floorY = center.y - half.y - Gap, rise = .1f + .55f * Mathf.Max(half.x, half.z);
            int nx = half.x > 1.2f ? 5 : 4, nz = half.z > 1.2f ? 5 : 4;
            var points = new List<Vector3>();
            for (int ix = 0; ix < nx; ix++) for (int iz = 0; iz < nz; iz++)
            {
                var local = new Vector3(Mathf.Lerp(-half.x + .02f, half.x - .02f, ix / (nx - 1f)), 0, Mathf.Lerp(-half.z + .02f, half.z - .02f, iz / (nz - 1f)));
                var world = center + rotation * local; var start = new Vector3(world.x, floorY + rise, world.z);
                var hit = Physics.RaycastAll(start, Vector3.down, rise * 2, ~0, QueryTriggerInteraction.Collide).Where(h => !Ignored(h.collider, own)).OrderBy(h => h.distance).Select(h => (RaycastHit?)h).FirstOrDefault();
                if (!hit.HasValue) return false;   // a hole or an overhang under the crate
                var c = hit.Value.collider;
                if (Cargo(c) ? !StableCargo(c) : c.attachedRigidbody != null) return false;
                points.Add(new Vector3(local.x, hit.Value.point.y, local.z));
            }
            if (points.Count < 3) return false;
            // The face the crate's centre (0, 0) rests on: three points around the centre (not near an edge), all others below.
            for (int i = 0; i < points.Count; i++) for (int j = i + 1; j < points.Count; j++) for (int k = j + 1; k < points.Count; k++)
            {
                Vector3 a = points[i], b = points[j], c = points[k];
                var n = Vector3.Cross(b - a, c - a); if (Mathf.Abs(n.y) < 1e-5f) continue; if (n.y < 0) n = -n;
                float area2 = (b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z);
                if (Mathf.Abs(area2) < 1e-6f) continue;
                float u = (b.x * c.z - c.x * b.z) / area2, v = (c.x * a.z - a.x * c.z) / area2, w = 1 - u - v;   // barycentric of (0, 0)
                if (u < .1f || v < .1f || w < .1f) continue;
                n.Normalize(); bool below = true;
                foreach (var q in points) { float off = Vector3.Dot(q - a, n); if (off > .002f || off < -MaxDip) { below = false; break; } }
                if (!below) continue;
                // n is in the crate's level frame (x, z level; y up): its tilt, and the plane's height under the centre.
                if (Vector3.Angle(n, Vector3.up) > limit) return false;
                float y0 = a.y - (n.x * (0 - a.x) + n.z * (0 - a.z)) / n.y;
                var normal = (rotation * new Vector3(n.x, 0, n.z) + Vector3.up * n.y).normalized;
                rotation = Quaternion.FromToRotation(Vector3.up, normal) * rotation;
                center = new Vector3(center.x, y0, center.z) + normal * (Gap + half.y);
                return true;
            }
            return false;
        }
        // A crate's box, turned its own way, measured along the order's frame (level crates: exactly half its size).
        private static Vector3 Extent(Quaternion rotation, Quaternion frame, Vector3 size)
        {
            var r = Quaternion.Inverse(frame) * rotation; Vector3 e = Vector3.zero;
            for (int k = 0; k < 3; k++) { var axis = r * new Vector3(k == 0 ? 1 : 0, k == 1 ? 1 : 0, k == 2 ? 1 : 0) * size[k] * .5f; e += new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z)); }
            return e;
        }
        // Follow connected paving from a known low floor, not a ray cast from above
        // the building. Cargo is never a floor-map surface (stacking is separate).
        private sealed class FloorMap
        {
            private const float Step = .35f, Rise = 1.2f;
            private readonly float cell, baseHeight;
            private readonly Vector3 origin, min;
            private readonly Quaternion rotation;
            private readonly float[,] heights;
            private readonly HashSet<Collider> own;
            internal FloorMap(Vector3 origin, Quaternion rotation, Vector3 min, Vector3 max,
                Vector3 seed, HashSet<Collider> own)
            {
                this.origin=origin;this.rotation=rotation;this.min=min;this.own=own;baseHeight=seed.y;
                cell=Mathf.Max(.25f,Mathf.Max(max.x-min.x,max.z-min.z)/160);
                int nx=Mathf.CeilToInt((max.x-min.x)/cell)+1,nz=Mathf.CeilToInt((max.z-min.z)/cell)+1;
                heights=new float[nx,nz];
                for(int x=0;x<nx;x++)for(int z=0;z<nz;z++)heights[x,z]=float.NaN;
                var queue=new Queue<Vector2Int>();
                // Seed low, unobstructed surfaces at the established ground level.
                // Multiple seeds keep pillars/cargo from cutting off separate floor patches.
                for(int x=0;x<nx;x++)for(int z=0;z<nz;z++)
                    if(Surface(Point(x,z),baseHeight,out float start)){heights[x,z]=start;queue.Enqueue(new Vector2Int(x,z));}
                int[] dx={-1,1,0,0},dz={0,0,-1,1};
                while(queue.Count>0){var p=queue.Dequeue();for(int i=0;i<4;i++){
                    int x=p.x+dx[i],z=p.y+dz[i];
                    if(x<0||z<0||x>=nx||z>=nz)continue;
                    // Upgrade a buried low hit when a neighbouring step reaches the
                    // real paving above it. Heights only increase, bounding revisits.
                    if(Surface(Point(x,z),heights[p.x,p.y],out float h)&&
                        (float.IsNaN(heights[x,z])||h>heights[x,z]+.01f)){heights[x,z]=h;queue.Enqueue(new Vector2Int(x,z));}
                }}
            }
            private Vector3 Point(int x,int z)=>origin+rotation*new Vector3(min.x+x*cell,0,min.z+z*cell);
            private bool Surface(Vector3 point,float near,out float height)
            {
                point.y=near+Step+.01f;height=0;
                foreach(var hit in Physics.RaycastAll(point,Vector3.down,Step*2+.02f,~0,QueryTriggerInteraction.Ignore).OrderBy(h=>h.distance)){
                    if(Ignored(hit.collider,own)||Cargo(hit.collider)||hit.collider.attachedRigidbody||hit.normal.y<.98f||
                        hit.point.y>baseHeight+Rise||Mathf.Abs(hit.point.y-near)>Step)continue;
                    height=hit.point.y;return true;
                }
                return false;
            }
            internal bool Height(Vector3 point,out float height)
            {
                var local=Quaternion.Inverse(rotation)*(point-origin);
                int x=Mathf.Clamp(Mathf.RoundToInt((local.x-min.x)/cell),0,heights.GetLength(0)-1),z=Mathf.Clamp(Mathf.RoundToInt((local.z-min.z)/cell),0,heights.GetLength(1)-1);
                height=0;return !float.IsNaN(heights[x,z])&&Surface(point,heights[x,z],out height);
            }
        }
        // The placement's upright turn. A market standing upright (every port but Serpent Isle) keeps its own turn exactly;
        // Serpent Isle's market (and its warehouse box) is tipped 90° on its side: its most level axis becomes "forward".
        internal static Quaternion Frame(IslandMarket market)
        {
            var t = market.transform;
            if (Vector3.Dot(t.up, Vector3.up) > .9999f) return t.rotation;
            var forward = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            if (forward.sqrMagnitude < .25f) forward = Vector3.ProjectOnPlane(t.up, Vector3.up);
            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }
        // An order that fits nowhere, even turned. The log keeps the count; the player sees the notice.
        internal sealed class NoRoom : InvalidOperationException
        {
            internal const string Notice = "Not enough space for Bulk Trading. Please buy one by one";
            internal NoRoom(int found, int count) : base("Found space for " + found + " of " + count + " cargo; full order needs more clear warehouse space") { }
        }
        // The whole order as the market faces, else turned a quarter turn one way, else the other (the user, 2026-10-08). At the rough
        // ports (RoughPorts) crate by crate instead: as many as fit as faced, then the rest turned, until a round places none.
        internal static bool Place(IslandMarket market, List<Good> goods, out int found)
        {
            var frame = Frame(market); var left = new List<Good>(goods); found = 0; bool progress = true;
            var turns = new[] { frame, frame * Quaternion.Euler(0, 90, 0), frame * Quaternion.Euler(0, -90, 0) };
            bool rough = RoughPorts.Contains(market.GetPortIndex());
            if (!rough)
            {
                foreach (var turn in turns)
                {
                    foreach (var good in goods) good.transform.rotation = turn;
                    int placed = Plan(market, goods, turn, false, false);
                    if (placed == goods.Count) { found = placed; return true; }
                    found = Math.Max(found, placed);
                }
                return false;
            }
            while (left.Count > 0 && progress)
            {
                progress = false;
                foreach (var turn in turns)
                {
                    if (left.Count == 0) break;
                    foreach (var good in left) good.transform.rotation = turn;
                    int placed = Plan(market, left, turn, rough, true);
                    if (placed > 0) { found += placed; left.RemoveRange(0, placed); progress = true; }
                }
            }
            return left.Count == 0;
        }
        // The game's own way (IslandMarket.SpawnGood): 1 m above the market, left to physics; upright.
        internal static void Drop(IslandMarket market, Good good)
        {
            good.transform.SetPositionAndRotation(market.transform.position + Vector3.up, Frame(market));
            Physics.SyncTransforms();
            CoverDropWatcher.Attach(good.GetComponent<ShipItem>());
        }
        // Places as many of the goods (in order) as fit with this turn; returns how many.
        // rough: the rough-ground rules above; partial: place what fits (else only a full order is placed).
        private static int Plan(IslandMarket market, List<Good> goods, Quaternion rotation, bool rough, bool partial)
        {
            float floorNormal = rough ? FloorNormal : .98f, stackTop = rough ? LevelTop : .98f, maxStack = rough ? MaxStack : float.PositiveInfinity;
            var area = market.GetWarehouseArea().GetComponent<BoxCollider>();
            if (!area) throw new InvalidOperationException("Warehouse placement area unavailable");
            Physics.SyncTransforms();
            var own = new HashSet<Collider>(goods.SelectMany(g => g.GetComponentsInChildren<Collider>()));
            var origin = market.transform.position;
            // No warehouse floor under the sea (beside Serpent Isle's docks the box takes in the sea bed).
            float sea = Crest.OceanRenderer.Instance ? Crest.OceanRenderer.Instance.transform.position.y : float.NegativeInfinity;
            var bounds = Footprint(goods[0], rotation); var half = bounds.extents;
            var inverse = Quaternion.Inverse(rotation);
            var min = Vector3.one * float.PositiveInfinity; var max = Vector3.one * float.NegativeInfinity;
            for (int i = 0; i < 8; i++) {
                var p = inverse * (area.transform.TransformPoint(area.center + Vector3.Scale(area.size * .5f, Corner(i))) - origin);
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
            float width = Mathf.Max(.1f, bounds.size.x + Gap), depth = Mathf.Max(.1f, bounds.size.z + Gap);
            int x0 = Mathf.CeilToInt((min.x + half.x) / width), x1 = Mathf.FloorToInt((max.x - half.x) / width);
            int z0 = Mathf.CeilToInt((min.z + half.z) / depth), z1 = Mathf.FloorToInt((max.z - half.z) / depth);
            var floor = new List<Candidate>(); var stacks = new List<Candidate>();
            for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++) {
                var p = origin + rotation * new Vector3(x * width, 0, z * depth);
                // Native pickup height prevents treating a building roof as floor.
                var start = new Vector3(p.x, origin.y + .1f, p.z);
                float distance = Mathf.Max(4, start.y - area.bounds.min.y + 1);
                foreach (var hit in Physics.RaycastAll(start, Vector3.down, distance, ~0, QueryTriggerInteraction.Collide).OrderBy(h => h.distance)) {
                    if (Ignored(hit.collider, own) || Cargo(hit.collider) || hit.collider.attachedRigidbody || hit.normal.y < floorNormal) continue;
                    floor.Add(new Candidate { Center = new Vector3(p.x, hit.point.y + Gap + half.y, p.z) }); break;
                }
                start.y = area.bounds.max.y + .1f;
                foreach (var hit in Physics.RaycastAll(start, Vector3.down, area.bounds.size.y + .2f, ~0, QueryTriggerInteraction.Collide)) {
                    if (!Ignored(hit.collider, own) && StableCargo(hit.collider) && hit.normal.y > stackTop)
                        stacks.Add(new Candidate { Center = new Vector3(p.x, hit.point.y + Gap + half.y, p.z), Stack = true });
                }
            }
            floor = floor.OrderBy(c => (c.Center - origin).sqrMagnitude).ToList();
            var positions = new List<Vector3>(); var turns = new List<Quaternion>();
            Vector3? seed=floor.Count>0?(Vector3?)(floor[0].Center-Vector3.up*(Gap+half.y)):null;
            if(!seed.HasValue)foreach(var hit in Physics.RaycastAll(origin+Vector3.up*.1f,Vector3.down,Mathf.Max(4,origin.y-area.bounds.min.y+1),~0,QueryTriggerInteraction.Ignore).OrderBy(h=>h.distance))
                if(!Ignored(hit.collider,own)&&!Cargo(hit.collider)&&!hit.collider.attachedRigidbody&&hit.normal.y>=floorNormal){seed=hit.point;break;}
            bool searchedGaps=false,stacksSorted=false;
            int floorIndex=0;

            // Reserve the full batch before moving goods or touching the transaction.
            while (floorIndex < floor.Count || stacks.Count > 0 || !searchedGaps) {
                if(floorIndex==floor.Count&&!searchedGaps){
                    searchedGaps=true;
                    floor.Clear();floorIndex=0;
                    // Quarter-cell offsets also cover narrow boundary strips excluded by
                    // the pickup-anchored coarse grid. Only pay this cost when necessary.
                    var map=seed.HasValue?new FloorMap(origin,rotation,min,max,seed.Value,own):null;
                    float left=min.x+half.x+.006f,right=max.x-half.x-.006f;
                    float back=min.z+half.z+.006f,front=max.z-half.z-.006f;
                    int nx=Mathf.Max(1,Mathf.CeilToInt((right-left)/(width*.25f))),nz=Mathf.Max(1,Mathf.CeilToInt((front-back)/(depth*.25f)));
                    for(int z=0;z<=nz&&front>=back;z++)for(int x=0;x<=nx&&right>=left;x++){
                        var p=origin+rotation*new Vector3(Mathf.Lerp(left,right,(float)x/nx),0,Mathf.Lerp(back,front,(float)z/nz));
                        float h=0;bool found=map!=null&&map.Height(p,out h);
                        // Fine offsets must retain every floor accepted by the original
                        // pickup-height probe, even across a step larger than the map limit.
                        var lowStart=new Vector3(p.x,origin.y+.1f,p.z);
                        foreach(var hit in Physics.RaycastAll(lowStart,Vector3.down,Mathf.Max(4,lowStart.y-area.bounds.min.y+1),~0,QueryTriggerInteraction.Collide).OrderBy(hit=>hit.distance)){
                            if(Ignored(hit.collider,own)||Cargo(hit.collider)||hit.collider.attachedRigidbody||hit.normal.y<floorNormal)continue;
                            if(!found||hit.point.y>h)h=hit.point.y;found=true;break;
                        }
                        if(found)floor.Add(new Candidate{Center=new Vector3(p.x,h+Gap+half.y,p.z)});
                        // Existing stacks can also be offset from the original grid.
                        var start=new Vector3(p.x,area.bounds.max.y+.1f,p.z);
                        foreach(var hit in Physics.RaycastAll(start,Vector3.down,area.bounds.size.y+.2f,~0,QueryTriggerInteraction.Collide))
                            if(!Ignored(hit.collider,own)&&StableCargo(hit.collider)&&hit.normal.y>stackTop)
                                stacks.Add(new Candidate{Center=new Vector3(p.x,hit.point.y+Gap+half.y,p.z),Stack=true});
                    }
                    floor=floor.OrderBy(c=>(c.Center-origin).sqrMagnitude).ToList();
                    if(floor.Count==0&&stacks.Count==0)break;
                }
                Candidate candidate;
                if (floorIndex < floor.Count) { candidate = floor[floorIndex++]; }
                else {
                    if(!stacksSorted){stacks=stacks.OrderByDescending(c=>c.Center.y).ThenByDescending(c=>(c.Center-origin).sqrMagnitude).ToList();stacksSorted=true;}
                    candidate=stacks[stacks.Count-1];stacks.RemoveAt(stacks.Count-1);
                }
                var center = candidate.Center; var turn = rotation;
                if (candidate.Stack && maxStack < float.PositiveInfinity && AboveGround(center, half.y, own) > maxStack) continue;
                bool level = true;
                if (!candidate.ReservedSupport && !Rest(ref center, ref turn, half, own, rough, out level)) continue;
                var extent = Extent(turn, rotation, bounds.size);
                if (center.y - extent.y - Gap < sea) continue;
                if (!Inside(area, center, half + Vector3.one * .005f, turn)) continue;
                bool fits = true;
                for (int i = 0; i < positions.Count; i++) {
                    var d = inverse * (center - positions[i]); var e = extent + Extent(turns[i], rotation, bounds.size);
                    if (Mathf.Abs(d.x) < e.x + Gap-.0001f && Mathf.Abs(d.y) < e.y + Gap-.0001f && Mathf.Abs(d.z) < e.z + Gap-.0001f) { fits = false; break; }
                }
                if (!fits) continue;
                // Keep vertical tolerance below the drop gap: distant ports lose
                // millimetres of precision in world-space floor ray hits.
                foreach (var hit in Physics.OverlapBox(center, half + new Vector3(Gap-.005f,.005f,Gap-.005f), turn, ~0, QueryTriggerInteraction.Collide)) {
                    if (own.Contains(hit) || hit == area || hit.isTrigger && !Cargo(hit)) continue;
                    fits = false; break;
                }
                if (!fits) continue;
                positions.Add(center); turns.Add(turn);
                if (positions.Count == goods.Count) break;
                // Only a level crate takes another on top before it is placed.
                if (level) { stacks.Add(new Candidate { Center = center + Vector3.up * (bounds.size.y + Gap), ReservedSupport = true, Stack = true }); stacksSorted=false; }
            }
            if (!partial && positions.Count < goods.Count) return positions.Count;
            for (int i = 0; i < positions.Count; i++) goods[i].transform.SetPositionAndRotation(positions[i] - turns[i] * bounds.center, turns[i]);
            Physics.SyncTransforms();
            // A crate stacked on existing cargo covers it: rescan what lies below once each settles.
            for (int i = 0; i < positions.Count; i++) CoverDropWatcher.Attach(goods[i].GetComponent<ShipItem>());
            return positions.Count;
        }
    }

    // ---- Missions ----
    [Serializable] public sealed class MissionState
    {
        public int slot, origin, destination, prefab, dueDay, totalPrice;
        public bool world, awarded;
        public int creditedDeliveries;
    }
    internal static class Missions
    {
        private static readonly Dictionary<int, List<Mission>> offers = new Dictionary<int, List<Mission>>();
        private static readonly Dictionary<Mission, bool> sources = new Dictionary<Mission, bool>();
        internal static readonly HashSet<int> DeliveredItems = new HashSet<int>();
        [ThreadStatic] internal static Good Delivering;
        internal static void Reset() { offers.Clear(); sources.Clear(); Delivering = null; }
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
                    creditedDeliveries = m.GetDeliveredCount() });
            }
            foreach (var list in offers.Values) for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].originPort == m.originPort && list[i].destinationPort == m.destinationPort && list[i].goodPrefab == m.goodPrefab) list[i] = null;
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
                if (market && g > 0 && g < market.currentSupply.Length) { market.currentSupply[g] += .5f * delta; Economy.Refresh(market); }
                r.creditedDeliveries = delivered;
            }
            if (delivered >= m.goodCount && !r.awarded)
            {
                r.awarded = true; Economy.Award(r.origin, r.world ? 6 : 2); Economy.Award(r.destination, r.world ? 3 : 1);
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
                () => { s.pending = 3; DrainWarehouse(m, s); }, () => s.blocked);
        }
        internal static void DrainWarehouse(IslandMarket m, MarketState s)
        {
            var office = m.GetComponent<IslandMissionOffice>(); if (!office || !office.enabled) return;
            int[] goods = office.GetData(); if (goods == null || goods.Length == 0) return;
            s.slot %= goods.Length;
            while (s.pending > 0)
            {
                int selected = 0, ties = 0; float highest = 0;
                for (int g = 1; g < m.currentSupply.Length; g++)
                {
                    if (g == 45 || g == 51 || m.currentSupply[g] <= 0 || !m.HasGood(g)) continue;
                    var item = PrefabsDirectory.instance.GetGood(g); if (!item) continue;
                    var good = item.GetComponent<Good>();
                    if (s.slot < goods.Length / 2f && (!good || good.requiredRepLevel != 0)) continue;
                    if (m.currentSupply[g] > highest) { selected = g; highest = m.currentSupply[g]; ties = 1; }
                    else if (m.currentSupply[g] == highest && UnityEngine.Random.Range(0, ++ties) == 0) selected = g;
                }
                if (selected == 0) { s.blocked = true; return; }
                if (goods[s.slot] != 0) m.SellGood(goods[s.slot]);
                goods[s.slot] = selected; m.PurchaseGood(selected);
                s.slot = (s.slot + 1) % goods.Length; s.pending--;
            }
        }
        internal static Mission[] Generate(IslandMissionOffice office, int page, bool world)
        {
            var m = office.GetComponent<IslandMarket>();
            if (!m || !Supply.Contains(m.GetPortIndex())) return new Mission[5];
            Economy.EnsureKnownPrices(m); int key = m.GetPortIndex() * 2 + (world ? 1 : 0);
            if (!offers.TryGetValue(key, out var list))
            {
                list = Build(office, world); offers.Add(key, list);
                foreach (var mission in list) sources[mission] = world;
            }
            m.GetPort().SetMissonCount(list.Count);
            var result = new Mission[5];
            for (int i = 0; i < 5; i++) if (page * 5 + i >= 0 && page * 5 + i < list.Count) result[i] = list[page * 5 + i];
            return result;
        }
        private static bool Already(Port origin, Port dest, GameObject prefab) => PlayerMissions.missions != null && PlayerMissions.missions.Any(m => m != null && m.originPort == origin && m.destinationPort == dest && m.goodPrefab == prefab);
        private static List<Mission> Build(IslandMissionOffice office, bool world)
        {
            var m = office.GetComponent<IslandMarket>(); if (!m) return new List<Mission>();
            Economy.EnsureKnownPrices(m); var origin = m.GetPort();
            var list = new List<Mission>(); int[] warehouse = office.GetData();
            foreach (var dest in Port.ports)
            {
                if (!dest || dest == origin || dest.portIndex == 7 || dest.portIndex == 33) continue;
                var report = m.knownPrices[dest.portIndex];
                if (report == null || !report.approved) continue;
                float distance = Mission.GetDistance(origin, dest);
                if ((distance >= 140) != world || distance > Reputations.MaxDistance(Reputations.Account(origin))) continue;
                bool ocean = Mission.UseOceanMapFor(origin, dest);
                int max = Mathf.Min(Reputations.MaxGoods(Reputations.Account(origin)), Fields.Get<int>(office, "maxGoodsPerMission") * (ocean ? 2 : 1));
                int other = 0;
                for (int g = 1; g < m.currentSupply.Length; g++)
                {
                    if (g == 45 || g == 51) continue;
                    int count = Math.Min(max, warehouse.Count(x => x == g)); if (count <= 0) continue;
                    var item = PrefabsDirectory.instance.GetGood(g); if (!item) continue;
                    var good = item.GetComponent<Good>();
                    if (!good || good.requiredRepLevel > Reputations.PortLevel(origin) || Already(origin, dest, item.gameObject)) continue;
                    float profit = (report.sellPrices[g] - m.GetGoodPrice(g)) * count, distanceFee = distance * .5f;
                    if (distanceFee > profit) continue;
                    // Pay: local 0.2 x profit + 2 x distance, world 0.35 x profit + 1.75 x distance (Rules.MissionReward).
                    int total = Mathf.RoundToInt(Rules.MissionReward(profit, distance, ocean)) / count * count;
                    list.Add(new Mission(origin, dest, item.gameObject, count, total, 1, 0, origin.GetDueDay(dest, good))); other++;
                }
                var mail = PrefabsDirectory.instance.GetGood(51);
                if (mail && !Already(origin, dest, mail.gameObject))
                {
                    // Retain Postal Expansion's normal-mail quantity/reward patch point.
                    list.Add((Mission)Fields.Call(office, "GenerateMailMission", mail.GetComponent<Good>(), dest, distance, other));
                }
            }
            list.Sort((a, b) => b.pricePerKm.CompareTo(a.pricePerKm));
            list = (List<Mission>)Fields.Call(office, "PruneNoSupplyMissions", list);
            if (list.Count > 25) list.RemoveRange(25, list.Count - 25);
            foreach (var mission in list)
            {
                mission.totalPrice = CurrencyMarket.instance.GetSellPriceInCurrency((Currency)mission.destinationPort.region, mission.totalPrice, false);
                mission.pricePerKm = mission.totalPrice / mission.distance;
            }
            return list;
        }
    }
    [HarmonyPatch(typeof(PortDude), "ActivateMissionListUI")] internal static class OfficeOpenPatch { static void Prefix(bool openEconomyUI) { if (!openEconomyUI) Missions.Reset(); } }
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
        static void Postfix(Mission mission) { if (mission != null) Missions.Accepted(mission); }
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
        { if (__result > 0 && Missions.Delivering && Missions.Delivering.GetAssignedMission() == __instance) __result = Mathf.RoundToInt(__result * CargoCondition.Factor(Missions.Delivering.GetComponent<ShipItem>())); }
    }
    [HarmonyPatch(typeof(Mission), "GetDeliveryRep")] internal static class MissionRepPatch
    {
        [HarmonyPriority(Priority.Last)] [HarmonyAfter("com.DogEggz.postalexpansion")]
        static void Postfix(Mission __instance, ref int __result)
        {
            if (__result <= 0) return;
            float factor = Missions.Postal(__instance) ? 1 : Settings.MissionRepFactor;
            if (Missions.Delivering && Missions.Delivering.GetAssignedMission() == __instance) factor *= CargoCondition.Factor(Missions.Delivering.GetComponent<ShipItem>());
            __result = Mathf.RoundToInt(__result * factor);
        }
    }
    [HarmonyPatch(typeof(Mission), "DeliverGood")] internal static class MissionDeliveredPatch { static void Postfix(Mission __instance) => Missions.Delivered(__instance); }

    // ---- Reputation ----
    // Account IDs are deliberately separate from PortRegion. Native slot 3 still means none.
    internal static class Reputations
    {
        internal const int Chronos = 3, Cap = 999999;
        internal static int Account(Port port) => port.portIndex == 21 ? Chronos : (int)port.region;
        internal static int Account(IslandMarket market) => Account(market.GetPort());
        internal static int Points(int account) => account == Chronos ? World.State.chronosReputation : PlayerReputation.GetRep(account);
        internal static int Required(int account, int level) => account != Chronos ? PlayerReputation.GetRequiredRep(level) : level >= 10 ? Cap : 2 * PlayerReputation.GetRequiredRep(level);
        internal static int LevelAt(int account, int points)
        { if (account != Chronos) return PlayerReputation.GetLevel(points); for (int level = 10; level > 0; level--) if (points >= Required(account, level)) return level; return 0; }
        internal static int Level(int account) => LevelAt(account, Points(account));
        // A sinking: each region, at the level it had before the loss (Levels), goes to the start of the level
        // Rules.SinkingLevels lower (Sink).
        internal static Dictionary<int, int> Levels(IEnumerable<int> regions) => regions.Distinct().ToDictionary(r => r, Level);
        internal static void Sink(Dictionary<int, int> levels)
        {
            foreach (var pair in levels) Change(Required(pair.Key, Math.Max(0, pair.Value - Rules.SinkingLevels)) - Points(pair.Key), pair.Key);
        }
        internal static int PortLevel(Port port) => Level(Account(port));
        internal static int MaxMissions(Port port) => Math.Min(5, Level(Account(port)) + 2);
        internal static int MaxGoods(int account) { int level = Level(account); return level < 6 ? level + 3 : 999999; }
        internal static float MaxDistance(int account)
        {
            if (account != Chronos) return PlayerReputation.GetMaxDistance((PortRegion)account);
            return Distances[Math.Min(8, Level(account))] * MapFactor(r => PlayerReputation.GetMaxDistance((PortRegion)r), Level);
        }
        private static readonly float[] Distances = { 96.45f, 514.4f, 964.5f, 1286f, 1446.75f, 1768.25f, 1929f, 2250.5f, 999999f };
        // A map mod (Scrambled Seas) stretches the game's range table for the three regions; Chronos' own table follows by
        // the same factor: the game's range over the table's for the first region below level 8, else at level 8 (Scrambled
        // Seas multiplies "no limit" too). A factor that isn't finite and positive counts as 1.
        internal static float MapFactor(Func<int, float> gameMax, Func<int, int> level)
        {
            int region = 0;
            while (region < 2 && level(region) >= 8) region++;
            float factor = gameMax(region) / Distances[Math.Min(8, level(region))];
            return float.IsNaN(factor) || float.IsInfinity(factor) || factor <= 0 ? 1 : factor;
        }
        internal static float PortDistance(Port port) => MaxDistance(Account(port));
        internal static void Change(int amount, int account)
        {
            if (account != Chronos) { PlayerReputation.ChangeReputation(amount, (PortRegion)account); return; }
            World.State.chronosReputation = (int)Math.Max(0, Math.Min(Cap, (long)Points(Chronos) + amount));
        }
        internal static int Location(Component component, int fallback)
        {
            var scenery = component ? component.GetComponentInParent<IslandSceneryScene>() : null;
            if (scenery && scenery.parentIslandIndex == 25) return Chronos;
            var island = component ? component.GetComponentInParent<IslandHorizon>() : null;
            return island && island.islandIndex == 25 ? Chronos : fallback;
        }
        internal static float ShopDiscount(Shopkeeper shop)
        { var market = FxShops.Market(shop); int account = market ? Account(market) : Location(shop, (int)Fields.Get<Region>(shop,"parentRegion").portRegion); return .02f * Level(account); }
        internal static float TavernDiscount(Tavern tavern) => .02f * Level(Location(tavern, (int)tavern.region));
        internal static void DeliveryReward(int amount, PortRegion ignored, Mission mission)
        { int origin = Account(mission.originPort), destination = Account(mission.destinationPort); Change(amount, origin); if (destination != origin) Change(amount, destination); }
        internal static void SkipDuplicateReward(int amount, PortRegion ignored) { }
        internal static void MissionPenalty(int amount, PortRegion ignored, Mission mission) => Change(amount, Account(mission.originPort));

        // Replace only a region load immediately feeding a reputation query. Payment/map regions stay native.
        // The trade book (clerk) opens from level 0: its single "level < 1" check becomes "level < 0".
        internal static IEnumerable<CodeInstruction> PortQueries(IEnumerable<CodeInstruction> instructions, bool tradeBookFromZero = false)
        {
            var code = instructions.ToList(); int count = 0, thresholds = 0;
            for (int i = 0; i < code.Count; i++)
                if (i + 1 < code.Count && code[i].LoadsField(AccessTools.Field(typeof(Port),"region")))
                {
                    string helper = code[i+1].Calls(AccessTools.Method(typeof(PlayerReputation),"GetRepLevel",new[]{typeof(PortRegion)})) ? nameof(PortLevel) :
                        code[i+1].Calls(AccessTools.Method(typeof(PlayerReputation),"GetMaxDistance")) ? nameof(PortDistance) : null;
                    if (helper != null) { code[i].opcode = OpCodes.Nop; code[i].operand = null; code[i+1].opcode = OpCodes.Call; code[i+1].operand = AccessTools.Method(typeof(Reputations),helper); count++; }
                    if (tradeBookFromZero && helper == nameof(PortLevel) && i + 2 < code.Count && code[i+2].opcode == OpCodes.Ldc_I4_1) { code[i+2].opcode = OpCodes.Ldc_I4_0; thresholds++; }
                }
            if(count==0)throw new InvalidOperationException("Missing port reputation query hook");
            if(tradeBookFromZero&&thresholds!=1)throw new InvalidOperationException("Trade book reputation threshold hook changed");
            return code;
        }
    }
    [HarmonyPatch] internal static class PortReputationQueriesPatch
    {
        static IEnumerable<MethodBase> TargetMethods() { yield return AccessTools.Method(typeof(PortDude),"ActivateMissionListUI"); }
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Reputations.PortQueries(instructions, __originalMethod.Name == "ActivateMissionListUI");
    }
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
    [HarmonyPatch] internal static class LocalRetailReputationPatch
    {
        static IEnumerable<MethodBase> TargetMethods() { yield return AccessTools.Method(typeof(Shopkeeper),"GetPrice"); yield return AccessTools.Method(typeof(Tavern),"UpdateTextAndPrice"); }
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code=instructions.ToList();int count=0;
            for(int i=0;i<code.Count;i++)if(code[i].LoadsField(AccessTools.Field(typeof(PlayerReputation),"retailDiscounts"))) {
                int end=i+1;while(end<code.Count&&code[end].opcode!=OpCodes.Ldelem_R4)end++;
                if(end==code.Count)throw new InvalidOperationException("Retail discount hook changed");
                code[i].opcode=OpCodes.Ldarg_0;code[i].operand=null;
                for(int n=i+1;n<end;n++){code[n].opcode=OpCodes.Nop;code[n].operand=null;}
                code[end].opcode=OpCodes.Call;code[end].operand=AccessTools.Method(typeof(Reputations),__originalMethod.DeclaringType==typeof(Tavern)?nameof(Reputations.TavernDiscount):nameof(Reputations.ShopDiscount));count++;
            }
            if(count==0)throw new InvalidOperationException("Missing retail discount hook");return code;
        }
    }
    [HarmonyPatch(typeof(PlayerReputation),"GetHighestLevel")] internal static class HighestAccountLevelPatch
    { static void Postfix(ref int __result) => __result=Math.Max(__result,Reputations.Level(Reputations.Chronos)); }

    // Own cloned text materials, keeping dynamic-font atlas updates and lifetime local to the page.
    public sealed class BlackBookText : MonoBehaviour
    {
        private Material material;
        private TextMesh text;
        internal static void Apply(TextMesh text)
        { text.color=Color.black; if(!text.GetComponent<BlackBookText>())text.gameObject.AddComponent<BlackBookText>().Build(text); }
        private void Build(TextMesh value)
        {
            text=value; var shader=Shader.Find("GUI/Text Shader");
            material=new Material(text.font.material);if(shader)material.shader=shader;
            text.GetComponent<Renderer>().sharedMaterial=material;
            Font.textureRebuilt+=Refresh;
        }
        private void Refresh(Font font){if(text&&text.font==font&&material)material.mainTexture=font.material.mainTexture;}
        private void OnDestroy(){Font.textureRebuilt-=Refresh;if(material)Destroy(material);}
    }
    [HarmonyPatch(typeof(ReputationUI),"UpdateTexts")] internal static class ReputationBookPatch
    {
        static bool Prefix(ReputationUI __instance)
        {
            var ui=__instance;
            if(ui.reputations.Length==3){
                var rows=ui.reputations.Select(t=>t.transform.parent).ToList();var bars=ui.repBars.Select(t=>t.parent).ToList();
                var row=UnityEngine.Object.Instantiate(rows[2],rows[2].parent);row.name="Chronos reputation";rows.Add(row);
                var bar=UnityEngine.Object.Instantiate(bars[2],bars[2].parent);bars.Add(bar);
                Func<TextMesh,TextMesh> copy=t=>row.GetComponentsInChildren<TextMesh>(true).First(x=>x.name==t.name);
                ui.reputations=ui.reputations.Concat(new[]{copy(ui.reputations[2])}).ToArray();
                ui.levelTexts=ui.levelTexts.Concat(new[]{copy(ui.levelTexts[2])}).ToArray();
                ui.infoTexts=ui.infoTexts.Concat(new[]{copy(ui.infoTexts[2])}).ToArray();
                ui.repBars=ui.repBars.Concat(new[]{bar.GetComponentsInChildren<Transform>(true).First(t=>t.name==ui.repBars[2].name)}).ToArray();
                foreach(var text in row.GetComponentsInChildren<TextMesh>(true))if(text.text=="Aestrin")text.text="Chronos";
                for(int i=0;i<4;i++){
                    foreach(var text in rows[i].GetComponentsInChildren<TextMesh>(true))if(text.name=="rep number"||text.name=="level text"||text.name=="static text (5)"){var v=text.transform.localPosition;v.x/=.8f;text.transform.localPosition=v;}
                    var p=rows[i].localPosition;p.y=.24f-i*.14f;rows[i].localPosition=p;rows[i].localScale*=.8f;
                    p=bars[i].localPosition;p.y=.24f-i*.14f-.027f;bars[i].localPosition=p;var s=bars[i].localScale;s.y*=.8f;bars[i].localScale=s;
                }
                foreach(var text in ui.GetComponentsInChildren<TextMesh>(true))BlackBookText.Apply(text);
            }
            for(int i=0;i<4;i++){
                int level=Reputations.Level(i),points=Reputations.Points(i),start=Reputations.Required(i,level),target=level==10?0:Reputations.Required(i,level+1)-start;
                ui.reputations[i].text=target<=0?points.ToString("N0"):(points-start).ToString("N0")+" / "+target.ToString("N0");
                ui.repBars[i].localScale=new Vector3(target<=0?1:Mathf.Clamp01((float)(points-start)/target),1,1);
                ui.levelTexts[i].text=level.ToString();
                float distance=Reputations.MaxDistance(i);int goods=Reputations.MaxGoods(i);
                ui.infoTexts[i].text=(level*2)+"%\n"+Math.Min(5,level+2)+"\n"+(distance>50000?"no limit":(string)Fields.Call(ui,"GetDistanceText",distance))+"\n"+(goods>100?"no limit":goods.ToString());
            }
            return false;
        }
    }
    [HarmonyPatch(typeof(ReputationNotifUI),"Awake")] internal static class ReputationNoticeBuildPatch
    {
        static void Postfix(ReputationNotifUI __instance)
        {
            var ui=__instance;var original=ui.barParents[2];var clone=UnityEngine.Object.Instantiate(original,original.parent);clone.name="Chronos reputation notification";
            Func<Transform,Transform> copy=t=>clone.GetComponentsInChildren<Transform>(true).First(x=>x.name==t.name);
            ui.barParents=ui.barParents.Concat(new[]{clone}).ToArray();
            ui.repBars=ui.repBars.Concat(new[]{copy(ui.repBars[2])}).ToArray();
            ui.repTexts=ui.repTexts.Concat(new[]{copy(ui.repTexts[2].transform).GetComponent<TextMesh>()}).ToArray();
            ui.levelTexts=ui.levelTexts.Concat(new[]{copy(ui.levelTexts[2].transform).GetComponent<TextMesh>()}).ToArray();
            ui.levelupParticles=ui.levelupParticles.Concat(new[]{copy(ui.levelupParticles[2].transform).GetComponent<ParticleSystem>()}).ToArray();
            foreach(var t in clone.GetComponentsInChildren<TextMesh>(true)){if(t.text=="Aestrin")t.text="Chronos";t.color=Color.black;}
            Fields.Set(ui,"localReputations",new int[4]);Fields.Set(ui,"repBarOn",new bool[4]);
        }
    }
    [HarmonyPatch(typeof(ReputationNotifUI),"HardCopyReps")] internal static class ReputationNoticeCopyPatch
    { static bool Prefix(ReputationNotifUI __instance){Fields.Set(__instance,"localReputations",Enumerable.Range(0,4).Select(Reputations.Points).ToArray());return false;} }
    [HarmonyPatch(typeof(ReputationNotifUI),"FixedUpdate")] internal static class ReputationNoticeUpdatePatch
    {
        static bool Prefix(ReputationNotifUI __instance)
        {
            var ui=__instance;var points=Fields.Get<int[]>(ui,"localReputations");var shown=Fields.Get<bool[]>(ui,"repBarOn");
            bool changed=false;for(int i=0;i<4;i++){shown[i]=points[i]!=Reputations.Points(i);changed|=shown[i];}
            if(changed){
                Fields.Set(ui,"fadeTimer",3.43f);if(ui.transform.localScale==Vector3.zero)Juicebox.juice.TweenScale(ui.gameObject,Vector3.one,.25f,JuiceboxTween.overshootOut);
                int height=0;for(int i=3;i>=0;i--){
                    ui.barParents[i].gameObject.SetActive(shown[i]);if(!shown[i])continue;
                    Fields.Call(ui,"SetBarHeight",i,height++*ui.heightOffset);
                    int oldLevel=Reputations.LevelAt(i,points[i]);
                    int goal=Reputations.Points(i);int next=Mathf.RoundToInt(Mathf.Lerp(points[i],goal,ui.lerpSpeed));
                    points[i]=goal>points[i]?Math.Min(goal,next+ui.fixedGainSpeed*(oldLevel+1)):Math.Max(goal,next-ui.fixedGainSpeed*(oldLevel+1));
                    // Render after advancing: the next update may already be the fade phase.
                    int level=Reputations.LevelAt(i,points[i]),start=Reputations.Required(i,level),target=level==10?0:Reputations.Required(i,level+1)-start;
                    ui.levelTexts[i].text=level.ToString();ui.repTexts[i].text=target<=0?points[i].ToString():(points[i]-start)+" / "+target;
                    ui.repBars[i].localScale=new Vector3(target<=0?1:Mathf.Clamp01((float)(points[i]-start)/target),1,1);
                    if(level>oldLevel)ui.levelupParticles[i].Play();
                }
            }else{float timer=Fields.Get<float>(ui,"fadeTimer");if(timer>0){timer-=Time.deltaTime;Fields.Set(ui,"fadeTimer",timer);if(timer<=0)Juicebox.juice.TweenScale(ui.gameObject,Vector3.zero,.6f,JuiceboxTween.overshootIn);}}
            return false;
        }
    }
}

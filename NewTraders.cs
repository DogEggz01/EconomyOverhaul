using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EconomyOverhaul
{
    // ---- Rules ----
    public static partial class Rules
    {
        // Trade book (0.7.0). A delivered report unlocks with the player's rep in the delivering boat's home
        // region: four of each region's seven small locals at 1 (since 0.8.0 the other three, "small2", at 2, like
        // medium and heavy locals and the FFL-Emerald shuttle); regional traders at 3; the region's Chronos trader at 5. The Happy Bay trader unlocks at rep 2 in any region; each region
        // at rep 4 unlocks one group of three independents (home = the independent's group, 0-2).
        public static int BookTier(string group) => group == "small" ? 1 : group == "small2" || group == "medium" || group == "heavy" || group == "shuttle" || group == "happy" ? 2 :
            group == "regional" ? 3 : group == "independent" ? 4 : group == "chronos" ? 5 : int.MaxValue;
        public static bool BookUnlocked(string group, int home, int[] reps)
        {
            if (group == "happy") { foreach (int r in reps) if (r >= 2) return true; return false; }
            if (group == "independent") { int count = 0; foreach (int r in reps) if (r >= 4) count++; return home >= 0 && count > home; }
            return home >= 0 && home < reps.Length && reps[home] >= BookTier(group);
        }
        // Whether a port releases trader reports to the player at midnight: not where the player has rep 0. Reports are not
        // held back by reputation otherwise (the 0.7.0 waits of 3/2/1 days were removed).
        public static bool BookReleases(int portLevel) => portLevel > 0;
        // Whether a report delivered by boat kind f is unlocked whenever one delivered by kind e is (so a newer f replaces an older e).
        public static bool BookCovers(string fg, int fh, string eg, int eh)
        {
            if (fg == "happy") return eg != "small";
            if (fg == "independent") return eg == "independent" && eh >= fh || fh == 0 && eg == "chronos";
            if (eg == "happy" || eg == "independent") return false;
            return fh == eh && BookTier(fg) <= BookTier(eg);
        }
        // Tavern rumours by drink (the game's liquid number): rum, wine and coconut wine (2-4) buy the boat type and
        // the load size; mead, honey beer, rice beer and cider (5-8) the boat type; coffee and teas (10-13) basic news.
        // Water (1) and sea water (9) are refused.
        public static bool RumorAccepted(int drink) => drink >= 2 && drink <= 13 && drink != 9;
        public static bool RumorBoatType(int drink) => drink >= 2 && drink <= 8;
        public static bool RumorLoadSize(int drink) => drink >= 2 && drink <= 4;
        public static bool ValidRoute(string group, int origin, int destination, int home, int originRegion, int destinationRegion, double distance)
        {
            if (origin == destination || destination == 7 || destination == 33) return false;
            if (group == "chronos") return (origin == 21 && destination == home) || (origin == home && destination == 21);
            // Only Chronos traders sail to Chronos.
            if (destination == 21) return false;
            // Every Happy Bay trader route has Happy Bay at exactly one end.
            if (group == "happy") return (origin == 20) != (destination == 20);
            // Heavy routes must touch Eastwind, Aestra Abbey or Firefly Grotto.
            // This also filters the empty nearest-port fallback before it is chosen.
            if (group == "heavy")
                return origin == 19 || origin == 26 || origin == 28 || destination == 19 || destination == 26 || destination == 28;
            if (group == "regional" || group == "independent")
                return distance > 140 && (group == "independent" || (originRegion == home) != (destinationRegion == home));
            if (group == "shuttle") return (origin >= 22 && origin <= 25) != (destination >= 22 && destination <= 25);
            return true; // Local destination networks are filtered before calling.
        }
    }

    // ---- Fleet and tavern rumours ----
    internal sealed class FleetDefinition
    {
        internal readonly string Group;
        internal readonly int Home, Count, Units, Weight, MinimumMass, StartPort;
        internal readonly int[] StartPorts;
        internal readonly float Speed, Wait;
        internal readonly bool Ffl;
        internal FleetDefinition(string group, int home, int count, int units, int weight, float speed, float hours, bool ffl = false, int minimumMass = 0, int startPort = -1, int[] startPorts = null)
        { Group = group; Home = home; Count = count; Units = units; Weight = weight; Speed = speed; Wait = hours / .008f; Ffl = ffl; MinimumMass = minimumMass; StartPorts = startPorts; StartPort = startPorts != null && startPorts.Length > 0 ? startPorts[0] : startPort; }
        internal bool Matches(BoatState b) => b.group == Group && b.home == Home &&
            (Group != "small" || b.network.Any(p => p >= 22 && p <= 25) == Ffl);
        // The n-th boat of this definition starts at the n-th listed port (cycling); otherwise at StartPort or a random network port.
        internal int Origin(int index, int[] network) => StartPorts != null && StartPorts.Length > 0 ? StartPorts[index % StartPorts.Length] :
            StartPort >= 0 ? StartPort : RandomPort(network);
        // Kept separate so the listed-port path never needs Unity's native random call (the standalone tests run outside Unity).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int RandomPort(int[] network) => network[UnityEngine.Random.Range(0, network.Length)];
        internal int[] Network()
        {
            // Happy Bay, Emerald (with FFL) and Aestrin; every route touches Happy Bay (Rules.ValidRoute). Chronos and Saffron excluded.
            if (Group == "happy") return Supply.ManagedRows().Where(p => p.Port != 21 && p.Port != 33 && p.Region != 0).Select(p => p.Port).OrderBy(p => p).ToArray();
            if (Group == "chronos") return new[] { Home, 21 };
            if (Ffl) return new[] { 22, 23, 24, 25 };
            return Supply.ManagedRows().Where(p => p.Port != 21 && p.Port != 33 &&
                (Group == "regional" || Group == "independent" || p.Region == Home) &&
                !(Home == 2 && (Group == "small" || Group == "heavy" || Group == "medium") && p.Port == 20) &&
                !(Home == 1 && Group == "small" && p.Port >= 22 && p.Port <= 25)).Select(p => p.Port).OrderBy(p => p).ToArray();
        }
        internal void Apply(BoatState b) { b.units = Units; b.weight = Weight; b.speed = Speed; b.wait = Wait; }
    }
    internal static class FleetDefinitions
    {
        // Speeds are m/s, converted from the approved knots and rounded to two decimals.
        internal static readonly FleetDefinition[] Current = {
            new FleetDefinition("small", 0, 7, 20, 1000, 2.32f, 2.5f),
            new FleetDefinition("small", 1, 7, 20, 1000, 2.32f, 2.5f),
            new FleetDefinition("small", 2, 7, 20, 1000, 2.32f, 2.5f),
            new FleetDefinition("small", 1, 2, 20, 1000, 2.32f, 2.5f, ffl: true),
            new FleetDefinition("heavy", 2, 2, 30, 10000, 2.06f, 24, minimumMass: 220, startPorts: new[] { 19, 28 }),
            new FleetDefinition("medium", 0, 1, 40, 4000, 2.57f, 4),
            new FleetDefinition("medium", 1, 1, 40, 4000, 2.57f, 4),
            new FleetDefinition("medium", 2, 1, 40, 4000, 2.57f, 4),
            new FleetDefinition("regional", 0, 3, 40, 10000, 3.60f, 8),
            new FleetDefinition("regional", 1, 3, 40, 10000, 3.60f, 8),
            new FleetDefinition("regional", 2, 3, 40, 10000, 3.60f, 8),
            new FleetDefinition("independent", -1, 9, 70, 10000, 4.12f, 12),
            new FleetDefinition("shuttle", 1, 1, 40, 5000, 3.09f, 6.5f),
            new FleetDefinition("happy", 2, 1, 40, 5000, 3.09f, 6.5f, startPort: 20),
            new FleetDefinition("chronos", 0, 1, 130, 13000, 4.37f, 24),
            new FleetDefinition("chronos", 9, 1, 130, 13000, 4.37f, 24),
            new FleetDefinition("chronos", 15, 1, 130, 13000, 4.37f, 24)
        };
        internal static int Total => Current.Sum(d => d.Count);
        internal static FleetDefinition For(BoatState b) => Current.First(d => d.Matches(b));
        internal static int MinimumMass(string group) => Current.FirstOrDefault(d => d.Group == group)?.MinimumMass ?? 0;
        internal static bool KnownGroup(string group) => Current.Any(d => d.Group == group);
    }

    [Serializable] public sealed class BoatState
    {
        public int id, home, origin, destination = -1;
        public string group;
        public int units, weight;
        public float speed, wait;
        public double remaining, trip;
        public int[] network, goods = new int[0];
        public List<ReportState> reports = new List<ReportState>();
        // Trade-book snapshots carried for the ports on this boat's route, newest per port.
        public List<int> book = new List<int>();
    }

    // The map's stretch (a map mod such as Scrambled Seas spreads the islands): the vanilla ports' live distances, all pairs
    // summed, over the same pairs on the vanilla map (Reference). Under 1.05 it is 1. Traders sail distances divided by it
    // (Fleet.Distance), so trade keeps the vanilla pace on a bigger map. Nothing saved: worked out at most once a frame.
    // Distances across the water (x, z): the game sinks each island by its distance from the player (IslandHorizon), so
    // heights change as the player sails. Chronos (a lone far outlier) and the test port 7 are left out.
    internal static class MapStretch
    {
        internal const double DeadBand = 1.05;
        // Port index, then x and z from port 0, in Sailwind 0.39.2 (-economicMapReference measures it).
        private static readonly float[] Reference = {
            0, 0.0f, 0.0f, 1, -5397.4f, 1166.5f, 2, -5248.4f, -1884.0f, 3, 3166.7f, -2574.5f, 4, -183.1f, -3884.3f, 5, -4672.7f, 3778.7f,
            6, -2057.0f, 14801.7f, 9, 86582.0f, 2186.5f, 10, 89814.6f, 6992.6f, 11, 87622.1f, 5294.4f, 12, 86291.2f, 9046.2f,
            13, 84222.9f, -1868.8f, 14, 83250.2f, 6177.5f, 15, 49354.7f, 83094.3f, 16, 52079.1f, 81941.6f, 17, 54103.5f, 79507.9f,
            18, 48822.2f, 86866.6f, 19, 45583.5f, 88232.4f, 20, 75868.1f, 38358.7f, 22, 69676.9f, -26907.4f, 23, 72036.0f, -26600.5f,
            24, 69077.1f, -25340.7f, 25, 68228.9f, -25746.6f, 26, 46754.4f, 83295.0f, 27, 50223.4f, 86487.4f, 28, 50794.3f, 84932.9f,
            29, 90940.6f, 3011.2f, 30, 82203.0f, 3945.7f, 31, -1537.5f, -3232.2f, 32, 3127.8f, 17682.4f, 33, 5883.0f, 26877.8f };
        private static int frame = -1; private static double current = 1, logged = 1;
        internal static int ReferencePorts => Reference.Length / 3;
        internal static double Rule(double ratio) => double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio < DeadBand ? 1 : ratio;
        internal static double Ratio(IList<Vector2> live, IList<Vector2> vanilla)
        {
            double sumLive = 0, sumVanilla = 0;
            for (int i = 0; i < live.Count; i++)
                for (int j = i + 1; j < live.Count; j++) { sumLive += Vector2.Distance(live[i], live[j]); sumVanilla += Vector2.Distance(vanilla[i], vanilla[j]); }
            return sumVanilla > 0 ? sumLive / sumVanilla : 1;
        }
        // The raw ratio over the reference ports the map has now (each at its own index).
        internal static double Measure()
        {
            var live = new List<Vector2>(); var vanilla = new List<Vector2>();
            for (int k = 0; k + 2 < Reference.Length; k += 3)
            {
                int index = (int)Reference[k];
                var port = Port.ports != null && index < Port.ports.Length ? Port.ports[index] : null;
                if (!port || port.portIndex != index) continue;
                var p = port.transform.position; live.Add(new Vector2(p.x, p.z)); vanilla.Add(new Vector2(Reference[k + 1], Reference[k + 2]));
            }
            return Ratio(live, vanilla);
        }
        internal static double Current
        {
            get
            {
                if (Time.frameCount == frame) return current;
                frame = Time.frameCount; double ratio = Measure(); current = Rule(ratio);
                if (Math.Abs(current - logged) > .005) { logged = current; Plugin.Log?.LogInfo("Map stretch " + ratio.ToString("F3") + ": traders sail distances / " + current.ToString("F3") + "."); }
                return current;
            }
        }
    }

    internal static class Fleet
    {
        private static double? eventTime;
        private static double EventTime => eventTime ?? World.State.trafficTime;
        internal static float RecoveryAfter;
        internal static void BeginRegistrationGrace() => RecoveryAfter = Time.realtimeSinceStartup + 5;
        internal static ReportState Report(List<ReportState> reports, int port) => reports.Find(r => r.port == port);
        internal static void PutReport(List<ReportState> reports, ReportState report)
        {
            if (reports == null || report == null) return;
            int i = reports.FindIndex(r => r.port == report.port);
            if (i < 0) reports.Add(report); else if (reports[i].day <= report.day) reports[i] = report;
        }
        internal static void ReconcileNetworks(bool bottleneckAvailable)
        {
            if (World.State?.fleet == null) return;
            foreach (var boat in World.State.fleet)
            {
                boat.network = ReconcileNetwork(boat.network, boat.group, boat.home, bottleneckAvailable);
            }
            if (bottleneckAvailable && World.Ready)
            {
                var market = World.Market(BottleneckCompatibility.PortId);
                if (!market || !Supply.Contains(BottleneckCompatibility.PortId)) return;
                Economy.Refresh(market);
                var report = Report(World.MarketData(BottleneckCompatibility.PortId).reports, BottleneckCompatibility.PortId);
                if (report != null)
                {
                    // Traders plan with their port's report, so a port that has never heard of the island learns its
                    // opening prices; ports and boats that already know it keep what traders brought.
                    foreach (var row in Supply.ManagedRows())
                    {
                        if (row.Port == BottleneckCompatibility.PortId) continue;
                        var data = World.MarketData(row.Port); var m = World.Market(row.Port);
                        if (m && Report(data.reports, BottleneckCompatibility.PortId) == null) Publish(m, data, report);
                    }
                    foreach (var boat in World.State.fleet)
                        if (boat.network.Contains(BottleneckCompatibility.PortId) && Report(boat.reports, BottleneckCompatibility.PortId) == null) PutReport(boat.reports, report);
                }
            }
        }
        internal static int[] ReconcileNetwork(int[] network, string group, int home, bool bottleneckAvailable)
        {
            bool emeraldSmall = group == "small" && home == 1 && !network.Any(p => p >= 22 && p <= 25);
            bool eligible = emeraldSmall || group == "medium" && home == 1 || group == "regional" || group == "independent" || group == "shuttle" || group == "happy";
            bool include = bottleneckAvailable && eligible;
            int occurrences = network.Count(p => p == BottleneckCompatibility.PortId);
            if (include && occurrences == 1 || !include && occurrences == 0) return network;
            if (include) return network.Where(p => p != BottleneckCompatibility.PortId).Concat(new[] { BottleneckCompatibility.PortId }).ToArray();
            return network.Where(p => p != BottleneckCompatibility.PortId).ToArray();
        }
        internal static void Initialize()
        {
            var steps = InitializeSteps();
            while (steps.MoveNext()) { }
        }
        internal static IEnumerator InitializeSteps()
        {
            World.State.fleet.Clear();
            foreach (var definition in FleetDefinitions.Current)
                for (int i = 0; i < definition.Count; i++) { Add(definition, 1); yield return null; }
        }
        private static void Add(FleetDefinition definition, int count)
        {
            int[] network = definition.Network();
            for (int j = 0; j < count; j++)
            {
                var b = new BoatState { id = World.State.fleet.Count == 0 ? 0 : World.State.fleet.Max(boat => boat.id) + 1, group = definition.Group, home = definition.Home, network = network,
                    origin = definition.Origin(World.State.fleet.Count(definition.Matches), network), remaining = 1.25 };
                definition.Apply(b);
                foreach (int p in network)
                {
                    var m = World.Market(p); if (!m) continue;
                    if (!Startup.Running) Economy.Refresh(m); var r = Report(World.MarketData(p).reports, p); if (r != null) PutReport(b.reports, r);
                }
                World.State.fleet.Add(b);
            }
        }
        private static Port LivePort(int port) => Port.ports != null && port >= 0 && port < Port.ports.Length && Port.ports[port] && Port.ports[port].portIndex == port ? Port.ports[port] : null;
        // A trader's distance: the map's over its stretch (trip times and the route rules keep their vanilla scale).
        internal static double Distance(int a, int b)
        {
            var from = LivePort(a); var to = LivePort(b);
            return from && to ? Mission.GetDistance(from, to) / MapStretch.Current : double.PositiveInfinity;
        }
        private static bool Available(int port) => LivePort(port) && World.Market(port) && Supply.Contains(port);
        private static bool RegistrationPending(BoatState b) => Time.realtimeSinceStartup < RecoveryAfter &&
            (!Available(b.origin) || b.destination >= 0 && !Available(b.destination));
        internal static void Tick(double dt)
        {
            var fleet = World.State.fleet;
            foreach (var b in fleet) if (!RegistrationPending(b)) b.remaining -= dt;
            // Process the earliest event across the fleet first. This preserves event order
            // across frame sizes; the bounded backlog remains in saved remaining timers.
            try
            {
                for (int count = 0; count < SimulationClock.FleetEventBudget; count++)
                {
                    BoatState next = null;
                    foreach (var b in fleet)
                        if (b.remaining <= 0 && !RegistrationPending(b) && (next == null || b.remaining < next.remaining || b.remaining == next.remaining && b.id < next.id)) next = b;
                    if (next == null) break;
                    double overdue = next.remaining;
                    eventTime = World.State.trafficTime + overdue;
                    if (!Recover(next)) { next.remaining = Math.Max(1.25, next.wait); }
                    else if (next.destination >= 0) Arrive(next); else Depart(next);
                    next.remaining += overdue;
                }
            }
            finally { eventTime = null; }
        }
        internal static IEnumerator StartupEvent(BoatState b)
        {
            if (!Recover(b)) { b.remaining = Math.Max(1.25, b.wait); yield break; }
            if (b.destination >= 0) Arrive(b);
            else
            {
                var departure = DepartSteps(b);
                while (departure.MoveNext()) yield return null;
            }
        }
        private static bool Recover(BoatState b)
        {
            if (b.destination >= 0 && Available(b.destination)) return true;
            if (b.destination < 0 && Available(b.origin))
            {
                // A recovery completed at its origin can still have cargo to unload.
                if (b.goods.Length > 0) b.destination = b.origin;
                return true;
            }
            int fallback = Available(b.origin) ? b.origin : b.network.Where(Available).OrderBy(p => p).DefaultIfEmpty(-1).First();
            if (fallback < 0) return false; // Keep cargo until a permitted port exists again.
            Plugin.Log?.LogWarning($"Trader {b.id}: unavailable port; recovering {b.goods.Length} cargo at port {fallback}.");
            // A vanished endpoint has no coordinates to sail from. Resolve at the valid
            // origin (or deterministic network port); delivery below still happens once.
            b.destination = fallback;
            return true;
        }
        private static void Arrive(BoatState b)
        {
            var m = World.Market(b.destination);
            if (!m || !Supply.Contains(b.destination)) { b.remaining = b.wait; return; }
            Economy.BatchDepth++;
            try { foreach (int g in b.goods) { double coins = Fx.NpcQuote(m, g, false); m.SellGood(g); Fx.Record(Fx.Currency(m), coins, false, TradeSource.Npc); } }
            finally { Economy.BatchDepth--; }
            b.goods = new int[0];
            if (!Startup.BlocksGameplay) {
                if (Available(b.origin) && b.origin != b.destination) Economy.Award(b.origin, .1f);
                Economy.Award(b.destination, .1f);
            }
            m.debugTraderVisits++;
            var data = World.MarketData(b.destination);
            foreach (var r in b.reports)
            {
                if (!r.approved) continue;
                // The port publishes delivered reports at midnight (TradeBook.AdvanceDay); during new-game
                // preparation they join its report at once.
                if (Startup.Running) Publish(m, data, r);
                else PutReport(data.incoming ?? (data.incoming = new List<ReportState>()), r);
            }
            TradeBook.Arrive(b, b.destination);
            Economy.Refresh(m); b.origin = b.destination; b.destination = -1; b.remaining = b.wait;
        }
        // A report joins the port's own report (traders plan with it and pass it on) and its native price list (missions).
        internal static void Publish(IslandMarket m, MarketState data, ReportState r)
        {
            PutReport(data.reports, r);
            Economy.EnsureKnownPrices(m);
            var known = TradeBook.Native(m);
            if (known[r.port] == null || known[r.port].day <= r.day)
                known[r.port] = new PriceReport { day = r.day, approved = r.approved, buyPrices = (int[])r.buy.Clone(), sellPrices = (int[])r.sell.Clone() };
        }
        private sealed class Candidate { internal int destination; internal List<int> goods = new List<int>(); internal double cost, profit; }
        private sealed class Unit { internal int good, index; internal double ratio, cost, profit, weight; }
        private sealed class Heap
        {
            private readonly List<Unit> items = new List<Unit>();
            private static bool Before(Unit a, Unit b) => a.ratio > b.ratio || a.ratio == b.ratio && a.good < b.good;
            internal void Push(Unit u)
            {
                int n = items.Count; items.Add(u);
                while (n > 0) { int p = (n - 1) / 2; if (!Before(u, items[p])) break; items[n] = items[p]; n = p; }
                items[n] = u;
            }
            internal Unit Pop()
            {
                if (items.Count == 0) return null;
                Unit top = items[0], last = items[items.Count - 1]; items.RemoveAt(items.Count - 1);
                if (items.Count == 0) return top;
                int n = 0;
                while (n * 2 + 1 < items.Count) {
                    int child = n * 2 + 1; if (child + 1 < items.Count && Before(items[child + 1], items[child])) child++;
                    if (!Before(items[child], last)) break; items[n] = items[child]; n = child;
                }
                items[n] = last; return top;
            }
        }
        private static Candidate Plan(BoatState b, int dest, double[,] buys)
            => PlanWithCurrency(b, dest, buys, PlanningCurrency());
        internal static double[] PlanningCurrency()
        {
            // Freeze all currency valuations for one departure. Startup remains FX-neutral. Prices follow the daily rate,
            // a coin is worth the booth-free rate: booth exchanges don't steer the traders.
            var factors = new[] { 1.0, 1.0, 1.0, 1.0 };
            if (!Startup.BlocksGameplay) {
                var fx = Fx.State;
                for (int i = 0; i < factors.Length; i++) factors[i] = fx.smooth[i] / fx.basis[i];
            }
            return factors;
        }
        private static Candidate PlanWithCurrency(BoatState b, int dest, double[,] buys, double[] factors)
        {
            var result = new Candidate { destination = dest };
            var m = World.Market(b.origin); var destinationMarket = World.Market(dest);
            if (!m || !destinationMarket || !Supply.Contains(b.origin) || !Supply.Contains(dest)) return result;
            // A port the published report doesn't cover is skipped; no price is fetched from it live (0.7.0).
            var report = Report(b.reports, dest);
            if (report == null || !report.approved || report.supply == null) return result;
            // Common-value comparison, rescaled to origin price units so same-currency
            // planning and the cached raw purchase curves remain exactly unchanged.
            double saleFactor = factors[Fx.Currency(destinationMarket)] / factors[Fx.Currency(m)];
            int[] preceding = new int[65], available = new int[65];
            var departure = World.MarketData(b.origin).lastDeparture;
            double arrival = EventTime + 100 * Distance(b.origin, dest) / b.speed;
            if (StateCodec.ValidDeparture(departure) && departure.boat != b.id && departure.destination == dest &&
                departure.arrival > EventTime && departure.arrival < arrival)
                foreach (int g in departure.goods) preceding[g]++;
            for (int g = 1; g < 65; g++) if (g != 45 && g != 51 && m.HasGood(g)) available[g] = Math.Max(0, (int)Math.Ceiling(m.currentSupply[g] - 1e-9) - 1);
            double weight = 0; var heap = new Heap();
            int minimumMass = FleetDefinitions.MinimumMass(b.group);
            Action<int, int> enqueue = (g, index) =>
            {
                if (index >= available[g] || index >= b.units) return;
                var item = PrefabsDirectory.instance.GetGood(g);
                if (!item || item.mass > b.weight || item.mass < minimumMass) return;
                double buy = buys[g, index];
                if (buy == 0) {
                    int raw = m.GetGoodPriceAtSupply(g, m.currentSupply[g] - index);
                    buys[g, index] = buy = Rules.Round(raw * (1 + Rules.Spread(raw)));
                }
                double sell = Rules.Quote(report.values[g], report.supply[g] + preceding[g] + index + 1,
                    g == 64 ? 100 : report.cap, report.surplus, report.deficit, false, g) * saleFactor;
                if (buy > 0 && sell > buy) heap.Push(new Unit { good = g, index = index, cost = buy, profit = sell - buy, ratio = (sell - buy) / buy, weight = item.mass });
            };
            for (int g = 1; g < 65; g++) enqueue(g, 0);
            while (result.goods.Count < b.units)
            {
                var unit = heap.Pop(); if (unit == null) break;
                if (weight + unit.weight > b.weight) continue;
                result.goods.Add(unit.good); weight += unit.weight;
                result.cost += unit.cost; result.profit += unit.profit;
                enqueue(unit.good, unit.index + 1);
            }
            return result;
        }
        private static void Depart(BoatState b)
        {
            var steps = DepartSteps(b);
            while (steps.MoveNext()) { }
        }
        private static IEnumerator DepartSteps(BoatState b)
        {
            var m = World.Market(b.origin); if (!m || !Supply.Contains(b.origin)) { b.remaining = b.wait; yield break; }
            // Traders plan with, and carry on, the port's report as published at the last midnight (0.7.0).
            b.reports = World.MarketData(b.origin).reports.Where(r => r.approved).ToList();
            Candidate best = null; int closest = -1; double closestDistance = double.MaxValue;
            var buys = new double[65, b.units];
            var factors = PlanningCurrency();
            foreach (int p in b.network)
            {
                var destination = LivePort(p); var destinationMarket = World.Market(p);
                if (!destination || !destinationMarket || !Supply.Contains(p)) continue;
                double distance = Distance(b.origin, p);
                if (!Rules.ValidRoute(b.group, b.origin, p, b.home, (int)m.GetPort().region, (int)destination.region, distance)) continue;
                if (distance < closestDistance) { closest = p; closestDistance = distance; }
                var candidate = PlanWithCurrency(b, p, buys, factors);
                if (candidate.cost > 0 && candidate.profit > 0 && (best == null || candidate.profit / candidate.cost > best.profit / best.cost)) best = candidate;
                yield return null;
            }
            if (best == null && closest >= 0) best = new Candidate { destination = closest };
            if (best == null) { b.remaining = b.wait; yield break; }
            Economy.BatchDepth++;
            try { foreach (int g in best.goods) { double coins = Fx.NpcQuote(m, g, true); m.PurchaseGood(g); Fx.Record(Fx.Currency(m), coins, true, TradeSource.Npc); } }
            finally { Economy.BatchDepth--; }
            b.goods = best.goods.ToArray(); b.destination = best.destination;
            b.trip = b.remaining = 100 * Distance(b.origin, b.destination) / b.speed;
            Economy.Refresh(m); var own = Report(World.MarketData(b.origin).reports, b.origin); PutReport(b.reports, own);
            TradeBook.Depart(b, own, EventHour);
            World.MarketData(b.origin).lastDeparture = new DepartureState { boat = b.id, group = b.group, origin = b.origin, destination = b.destination,
                departure = EventTime, arrival = EventTime + b.trip, calendarHour = EventHour, goods = (int[])b.goods.Clone() };
        }
        // Calendar hour of the event being processed (startup preparation runs on its own calendar).
        private static double EventHour => Startup.CalendarHour + (EventTime - World.State.trafficTime) * .008;
        internal static int RumorDay(double when, double now, double localOffset) =>
            (int)(Math.Floor((when + localOffset) / 24) - Math.Floor((now + localOffset) / 24));
        internal static bool RecentDeparture(double when, double now, double localOffset)
        {
            int day = RumorDay(when, now, localOffset);
            return day >= -2 && day <= 0;
        }
        internal static bool UpcomingArrival(double when, double now, double localOffset)
        {
            int day = RumorDay(when, now, localOffset);
            return day >= 0 && day <= 2;
        }
        // The drink being handed to the tavern's rumour NPC (its liquid number), or -1. Set around ClickDrinkButton.
        internal static int RumorDrink = -1;
        internal static string RumorVessel(string group, bool boatType)
        {
            if (!boatType) return "A trader";
            switch (group)
            {
                case "small": return "A small boat";
                case "medium": case "shuttle": case "happy": return "A medium ship";
                case "regional": case "independent": case "heavy": return "A large ship";
                case "chronos": return "A grand vessel";
                default: return "A trader";
            }
        }
        internal static string RumorCargo(string good, int count, bool loadSize) => count <= 0 ? "The hold was empty." :
            "Carrying " + (loadSize && count > 12 ? "a large load of " : loadSize && count > 4 ? "a sizeable load of " : "some ") + good + ".";
        internal static string Rumor(int port, int detail)
        {
            // 0.7.0: the drink decides the detail (Rules.RumorBoatType / RumorLoadSize). With no drink known, vanilla's own
            // level: 0 for drink numbers 5 and up, 2 below 5 (the bottle's "amount" holds the liquid number, not a quantity).
            int drink = RumorDrink;
            bool boatType = drink > 0 ? Rules.RumorBoatType(drink) : detail == 0, loadSize = drink > 0 ? Rules.RumorLoadSize(drink) : detail > 0;
            var currentPort = LivePort(port);
            if (!currentPort || !Supply.Contains(port)) return "It's been quiet around here.\nNo trader news today.";
            double offset = FloatingOriginManager.instance.GetGlobeCoords(currentPort.transform).x / 15;
            double now = World.CalendarHour;
            var last = World.MarketData(port).lastDeparture;
            int other; int[] goods; double when; string text;
            // Keep the historical record for fleet planning, but do not repeat stale news.
            if (StateCodec.ValidDeparture(last) && RecentDeparture(last.calendarHour, now, offset))
            {
                other = last.destination; goods = last.goods; when = last.calendarHour;
                text = RumorVessel(last.group, boatType) + " left for ";
            }
            else
            {
                var incoming = World.State.fleet.Where(b => b.destination == port &&
                    UpcomingArrival(now + Math.Max(0, b.remaining) * .008, now, offset)).OrderBy(b => b.remaining).FirstOrDefault();
                if (incoming == null) return "It's been quiet around here.\nNo trader news today.";
                other = incoming.origin; goods = incoming.goods; when = now + Math.Max(0, incoming.remaining) * .008;
                text = RumorVessel(incoming.group, boatType) + " is expected from ";
            }
            var otherPort = LivePort(other);
            if (!otherPort || !currentPort || !Supply.Contains(port) || !Supply.Contains(other)) return "It's been quiet around here.\nNo trader news today.";
            double local = when + offset;
            double hour = (local % 24 + 24) % 24; int day = RumorDay(when, now, offset);
            string dayText = day == 0 ? "today" : day == -1 ? "yesterday" : day == 1 ? "tomorrow" : day < 0 ? (-day) + " days ago" : "in " + day + " days";
            text += otherPort.GetPortName() + ", " + dayText + " " + (hour >= 6 && hour < 12 ? "in the morning" : hour >= 12 && hour < 18 ? "in the afternoon" : "at night") + ". ";
            var main = goods.GroupBy(g => g).OrderByDescending(g => g.Count()).FirstOrDefault();
            text += RumorCargo(main == null ? null : PrefabsDirectory.instance.GetGood(main.Key).name, main?.Count() ?? 0, loadSize);
            return Wrap(text, 33);
        }
        private static string Wrap(string text, int width)
        {
            var result = new System.Text.StringBuilder(); int line = 0;
            foreach (string word in text.Split(' ')) { if (line > 0 && line + word.Length + 1 > width) { result.Append('\n'); line = 0; } else if (line > 0) { result.Append(' '); line++; } result.Append(word); line += word.Length; }
            return result.ToString();
        }
    }
    // Native components/save-array slots stay intact. Only their autonomous updates are replaced.
    [HarmonyPatch(typeof(TraderBoat), "Update")] internal static class NativeFleetPatch { static bool Prefix() => false; }
    [HarmonyPatch(typeof(PortRumors), "GenerateRumorText")] internal static class TraderRumorPatch
    {
        static bool Prefix(PortRumors __instance, int level, ref string __result)
        { if (!World.Ready) return true; __result = Fleet.Rumor(__instance.GetComponent<Port>().portIndex, level); return false; }
    }
    // 0.7.0: the drink handed over decides the rumour's detail; sea water is refused like water.
    [HarmonyPatch(typeof(TavernRumorsDude), "ClickDrinkButton")] internal static class RumorDrinkPatch
    {
        static void Prefix(ShipItemBottle ___currentDrink) => Fleet.RumorDrink = ___currentDrink ? Mathf.RoundToInt(___currentDrink.amount) : -1;
        static Exception Finalizer(Exception __exception) { Fleet.RumorDrink = -1; return __exception; }
    }
    [HarmonyPatch(typeof(TavernRumorsDude), "OnTriggerEnter")] internal static class RumorRefusedDrinkPatch
    {
        static void Prefix(ShipItemBottle ___currentDrink, out ShipItemBottle __state) => __state = ___currentDrink;
        static void Postfix(TavernRumorsDude __instance, ref ShipItemBottle ___currentDrink, ShipItemBottle __state)
        {
            if (!___currentDrink || ___currentDrink == __state || Rules.RumorAccepted(Mathf.RoundToInt(___currentDrink.amount))) return;
            ___currentDrink = __state;
            if (__instance.drinkButton) __instance.drinkButton.SetActive((bool)__state);
        }
    }

    // ---- Price reports ----
    [Serializable] public sealed class ReportState
    {
        public int port, day;
        public bool approved;
        public float cap;
        public float[] supply, values;
        public int[] buy, sell;
        public float surplus, deficit;
    }
    [Serializable] public sealed class DepartureState
    {
        public int boat, origin, destination;
        public string group;
        public double departure, arrival, calendarHour;
        public int[] goods;
    }

    internal static partial class Economy
    {
        internal static void CaptureReport(IslandMarket m)
        {
            if (!World.Ready || !Supply.Contains(m.GetPortIndex()) || m.currentSupply == null || m.currentSupply.Length < 65) return;
            var native = m.GetSelfPriceReport(); if (native == null) return;
            if (Startup.Running) native.day = (int)Math.Floor(Startup.CalendarHour / 24);
            var r = new ReportState { port = m.GetPortIndex(), day = native.day, approved = native.approved,
                cap = Cap(m.GetPortIndex()), supply = (float[])m.currentSupply.Clone(), values = new float[m.currentSupply.Length],
                buy = (int[])native.buyPrices.Clone(), sell = (int[])native.sellPrices.Clone(),
                surplus = DebugMarketTracker.instance.positivePriceMult, deficit = DebugMarketTracker.instance.negativePriceMult };
            for (int i = 1; i < r.values.Length; i++) if (i != 45 && i != 51) r.values[i] = m.GetGoodPriceAtSupply(i, 0);
            Fleet.PutReport(World.MarketData(r.port).reports, r);
        }
    }

    [HarmonyPatch(typeof(IslandMarket), "UpdateSelfPriceReport")] internal static class SelfReportPatch
    { static bool Prefix() => Economy.BatchDepth == 0; static void Postfix(IslandMarket __instance) { if (Economy.BatchDepth == 0) Economy.CaptureReport(__instance); } }
    [HarmonyPatch(typeof(IslandMarket), "ReceivePriceReports")]
    internal static class ReceivePriceReportsPatch
    {
        static void Prefix(IslandMarket __instance, PriceReport[] reports) => Economy.EnsureKnownPrices(__instance, reports?.Length ?? 0);
    }

    // ---- Trade book ----
    [Serializable] public sealed class BookSnapshot
    {
        public int id, port, day;
        public double seen; // calendar hour when the prices were seen at their own port
        public int[] buy, sell;
    }
    [Serializable] public sealed class BookEntry
    {
        public int snapshot, port, boat, home; // home: region of the delivering boat; for independents, its unlock group
        public string group;
        public double seen;
    }

    // Daily price reports.
    // Traders: reports a boat delivers wait in the port's incoming list and join the port's report at midnight.
    //   Traders plan with and pass on that published report; missions read the port's native list, also updated
    //   at midnight. During new-game preparation deliveries join at once.
    // Trade book: a separate list that travels the same way. Boats carry frozen snapshots for the ports on their own
    //   route (second-hand included) and deliver them classed by the delivering boat's kind; a port publishes the day's
    //   deliveries at midnight, and a boat leaving carries only what its port has published. At midnight each port also
    //   releases, per port, the newest entry the player has unlocked (Rules.BookUnlocked: rep with the boat's home
    //   region); a port where the player has rep 0 releases nothing new. Released rows are never withdrawn.
    //   The player's own list is frozen and immediate; the current port always shows live prices.
    internal static class TradeBook
    {
        private static SaveState indexed;
        private static readonly Dictionary<int, BookSnapshot> index = new Dictionary<int, BookSnapshot>();
        // While the trade book is open its market's knownPrices is the player's view; native is the port's own list.
        private static IslandMarket swapped;
        private static PriceReport[] native, view;

        private static Dictionary<int, BookSnapshot> Snapshots
        {
            get
            {
                var state = World.State;
                if (!ReferenceEquals(indexed, state))
                {
                    index.Clear();
                    if (state.bookSnapshots == null) state.bookSnapshots = new List<BookSnapshot>();
                    foreach (var s in state.bookSnapshots) index[s.id] = s;
                    indexed = state;
                }
                return index;
            }
        }
        internal static BookSnapshot Get(int id) => Snapshots.TryGetValue(id, out var s) ? s : null;
        internal static BookSnapshot Add(int port, int day, double seen, int[] buy, int[] sell)
        {
            var map = Snapshots; var state = World.State;
            var s = new BookSnapshot { id = ++state.bookNextId, port = port, day = day, seen = seen, buy = (int[])buy.Clone(), sell = (int[])sell.Clone() };
            state.bookSnapshots.Add(s); map[s.id] = s; return s;
        }
        // The port's native price list, even while the trade book shows the player's view in its place.
        internal static PriceReport[] Native(IslandMarket m) => m && m == swapped ? native : m.knownPrices;

        // Unlock region of a delivering boat: its home region (a Chronos trader's is its capital's); for an
        // independent, its group of three in boat-number order; -1 for the Happy Bay trader (any region).
        internal static int Home(BoatState b)
        {
            if (b.group == "happy") return -1;
            if (b.group == "chronos") return Supply.TryGet(b.home, out var row) ? row.Region : -1;
            if (b.group == "independent")
            {
                int i = World.State.fleet.Where(x => x.group == "independent").OrderBy(x => x.id).ToList().FindIndex(x => x.id == b.id);
                return i < 0 ? -1 : i / 3;
            }
            return b.home;
        }

        // The class a delivering boat files under: the last three of each region's seven small locals (by boat number) are
        // "small2", released at rep 2; the Lagoon's two small locals stay "small".
        internal static string BookGroup(BoatState b)
        {
            if (b.group != "small") return b.group;
            var definition = FleetDefinitions.Current.FirstOrDefault(d => d.Matches(b));
            if (definition == null || definition.Ffl) return b.group;
            int i = World.State.fleet.Where(definition.Matches).OrderBy(x => x.id).ToList().FindIndex(x => x.id == b.id);
            return i >= 4 ? "small2" : "small";
        }
        // A departing boat carries, like the traders' report, only what its port has published: for each port on its
        // route, the port's newest published entry, and a frozen copy of the port's own prices.
        internal static void Depart(BoatState b, ReportState own, double hour)
        {
            b.book = new List<int>();
            var data = World.MarketData(b.origin);
            if (data.book != null)
                foreach (int port in b.network)
                {
                    if (port == b.origin) continue;
                    BookEntry known = null;
                    foreach (var e in data.book) if (e.port == port && (known == null || e.seen > known.seen)) known = e;
                    if (known != null) Keep(b.book, Get(known.snapshot));
                }
            if (own != null && own.approved && own.buy != null && own.sell != null) Keep(b.book, Add(b.origin, own.day, hour, own.buy, own.sell));
        }
        // An arriving boat delivers what it carries, classed by the boat; the port publishes it at midnight
        // (during new-game preparation at once).
        internal static void Arrive(BoatState b, int port)
        {
            if (b.book == null || b.book.Count == 0) return;
            var data = World.MarketData(port);
            if (data.book == null) data.book = new List<BookEntry>();
            if (data.bookIncoming == null) data.bookIncoming = new List<BookEntry>();
            var target = Startup.Running ? data.book : data.bookIncoming;
            int home = Home(b);
            foreach (int id in b.book)
            {
                var s = Get(id); if (s == null || s.port == port) continue;
                File(target, new BookEntry { snapshot = id, port = s.port, seen = s.seen, group = BookGroup(b), home = home, boat = b.id });
            }
        }
        // Keeps only entries that could still be the newest one shown: a newer (or equally new) entry whose kind is
        // unlocked whenever an older one's is replaces it.
        internal static void File(List<BookEntry> book, BookEntry e)
        {
            foreach (var o in book) if (o.port == e.port && o.seen >= e.seen && Rules.BookCovers(o.group, o.home, e.group, e.home)) return;
            book.RemoveAll(o => o.port == e.port && e.seen >= o.seen && Rules.BookCovers(e.group, e.home, o.group, o.home));
            book.Add(e);
        }
        // One snapshot per port in the list: a newer (or equally new) one replaces the old one.
        internal static void Keep(List<int> list, BookSnapshot s)
        {
            if (s == null) return;
            int i = list.FindIndex(id => Get(id)?.port == s.port);
            if (i < 0) list.Add(s.id);
            else { var old = Get(list[i]); if (old == null || old.seen <= s.seen) list[i] = s.id; }
        }

        // Midnight: ports publish the day's deliveries (traders' reports and trade-book entries), then release trade-book rows.
        internal static void AdvanceDay(int day)
        {
            if (!World.Ready || Startup.BlocksGameplay) return;
            var state = World.State;
            // The clock went back (a load or a debug change): start again from this day.
            if (day < state.reportDay) { state.reportDay = day; return; }
            if (day == state.reportDay) return;
            state.reportDay = day;
            PublishPending();
            PublishBook();
            Release();
            Collect();
        }
        internal static void PublishPending()
        {
            foreach (var data in World.State.markets)
            {
                if (data.incoming == null || data.incoming.Count == 0) continue;
                var m = World.Market(data.port);
                if (m)
                {
                    foreach (var r in data.incoming) if (r.port != data.port) Fleet.Publish(m, data, r);
                    Economy.Refresh(m);
                }
                data.incoming.Clear();
            }
        }
        internal static void PublishBook()
        {
            foreach (var data in World.State.markets)
            {
                if (data.bookIncoming == null || data.bookIncoming.Count == 0) continue;
                if (data.book == null) data.book = new List<BookEntry>();
                foreach (var e in data.bookIncoming) File(data.book, e);
                data.bookIncoming.Clear();
            }
        }
        internal static int[] Reps() => new[] { Reputations.Level(0), Reputations.Level(1), Reputations.Level(2) };
        // Each port shows, per port, the newest published entry the player has unlocked; nothing new where the player has rep 0.
        internal static void Release()
        {
            var reps = Reps();
            foreach (var data in World.State.markets)
            {
                if (data.book == null || data.book.Count == 0) continue;
                var market = World.Market(data.port); var port = market ? market.GetPort() : null;
                if (!port || !Rules.BookReleases(Reputations.PortLevel(port))) continue;
                if (data.released == null) data.released = new List<int>();
                foreach (var e in data.book) if (Rules.BookUnlocked(e.group, e.home, reps)) Keep(data.released, Get(e.snapshot));
            }
        }
        // Drops snapshots nothing refers to, and references to snapshots that no longer exist.
        internal static void Collect()
        {
            var state = World.State; var map = Snapshots; var used = new HashSet<int>();
            foreach (var m in state.markets)
            {
                m.book?.RemoveAll(e => !map.ContainsKey(e.snapshot)); m.bookIncoming?.RemoveAll(e => !map.ContainsKey(e.snapshot));
                m.released?.RemoveAll(id => !map.ContainsKey(id));
                if (m.book != null) foreach (var e in m.book) used.Add(e.snapshot);
                if (m.bookIncoming != null) foreach (var e in m.bookIncoming) used.Add(e.snapshot);
                if (m.released != null) foreach (int id in m.released) used.Add(id);
            }
            foreach (var b in state.fleet) { b.book?.RemoveAll(id => !map.ContainsKey(id)); if (b.book != null) foreach (int id in b.book) used.Add(id); }
            state.playerBook?.RemoveAll(id => !map.ContainsKey(id));
            if (state.playerBook != null) foreach (int id in state.playerBook) used.Add(id);
            if (state.bookSnapshots.RemoveAll(s => !used.Contains(s.id)) > 0) indexed = null;
        }

        // The player reads a port's own prices now.
        internal static void See(IslandMarket market)
        {
            var known = Native(market); int self = market.GetPortIndex();
            var own = known != null && self < known.Length ? known[self] : null;
            if (own == null || !own.approved || own.buyPrices == null || own.sellPrices == null) return;
            if (World.State.playerBook == null) World.State.playerBook = new List<int>();
            Keep(World.State.playerBook, Add(self, own.day, World.CalendarHour, own.buyPrices, own.sellPrices));
        }
        private static int Length(PriceReport[] known) => Math.Max(known?.Length ?? 0, Math.Max(100, Port.ports?.Length ?? 0));
        private static PriceReport Copy(BookSnapshot s) => new PriceReport { day = s.day, approved = true, buyPrices = (int[])s.buy.Clone(), sellPrices = (int[])s.sell.Clone() };
        // The player's frozen list as native reports (vanilla hands it to each port's native list on opening its book).
        internal static PriceReport[] PlayerArray(int length)
        {
            var result = new PriceReport[length];
            if (World.State.playerBook != null)
                foreach (int id in World.State.playerBook) { var s = Get(id); if (s != null && s.port >= 0 && s.port < length) result[s.port] = Copy(s); }
            return result;
        }
        // What the trade book shows at this port: its live own prices, and for every other port the player's list.
        internal static PriceReport[] View(IslandMarket market, PriceReport[] known)
        {
            var result = PlayerArray(Length(known));
            int self = market.GetPortIndex(); result[self] = known[self];
            return result;
        }

        // Trade book opened: the player takes in this port's released rows and its own prices, then the view
        // replaces the port's knownPrices until the book closes, so every read of the page sees the same rows.
        internal static void Open(EconomyUI ui)
        {
            Close(); if (!ui || !ui.uiActive || !World.Ready || Startup.BlocksGameplay) return;
            var market = Fields.Get<IslandMarket>(ui, "currentIsland"); if (!market || market.knownPrices == null || !Supply.Contains(market.GetPortIndex())) return;
            if (World.State.playerBook == null) World.State.playerBook = new List<int>();
            // A port where the player has reputation 0 hands over none of its reports.
            var data = World.MarketData(market.GetPortIndex());
            if (data.released != null && Rules.BookReleases(Reputations.PortLevel(market.GetPort())))
                foreach (int id in data.released) Keep(World.State.playerBook, Get(id));
            See(market);
            native = market.knownPrices; swapped = market;
            market.knownPrices = view = View(market, native);
            GameState.playerKnownPrices = PlayerArray(native.Length);
            ui.RefreshPage();
        }
        // Before vanilla CloseUI: restore the port's native list; the player keeps its prices as seen at closing.
        internal static void Close()
        {
            var market = swapped;
            if (market) { if (market.knownPrices != native) market.knownPrices = native; if (World.Ready) See(market); }
            swapped = null; native = view = null;
        }
        // A save while the book is open (the autosave does not wait for it to close) writes the port's own list, not the
        // player's view: the view steps aside for each step of the save and comes back after it.
        internal static bool HideView()
        {
            if (!swapped || view == null || swapped.knownPrices != view) return false;
            swapped.knownPrices = native; return true;
        }
        internal static void ShowView() { if (swapped && view != null && swapped.knownPrices == native) swapped.knownPrices = view; }
        // After vanilla CloseUI: the player's list stays frozen instead of linked to the port.
        internal static void Closed() { if (World.Ready && !Startup.BlocksGameplay && World.State.playerBook != null && World.State.playerBook.Count > 0) GameState.playerKnownPrices = PlayerArray(Length(GameState.playerKnownPrices)); }
        internal static void Loaded() { Close(); indexed = null; Closed(); }
        internal static void Reset() { Close(); indexed = null; index.Clear(); }
    }
    [HarmonyPatch(typeof(EconomyUI), "OpenUI")] internal static class TradeBookOpenPatch
    { [HarmonyPriority(Priority.First)] static void Postfix(EconomyUI __instance) => TradeBook.Open(__instance); }
    [HarmonyPatch(typeof(EconomyUI), "CloseUI")] internal static class TradeBookClosePatch
    {
        static void Prefix() => TradeBook.Close();
        static void Postfix() => TradeBook.Closed();
    }
    // Vanilla stores every market's knownPrices in the save inside DoSaveGame's coroutine steps.
    [HarmonyPatch] internal static class TradeBookSavePatch
    {
        static MethodBase TargetMethod() => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(SaveLoadManager), "DoSaveGame"));
        static void Prefix(out bool __state) => __state = TradeBook.HideView();
        static Exception Finalizer(Exception __exception, bool __state) { if (__state) TradeBook.ShowView(); return __exception; }
    }
    // Vanilla shows "?" as the age of a report dated day 0 or earlier. The startup history dates its reports before
    // day 0, so show their real age (today minus the report's day) like any other report.
    [HarmonyPatch(typeof(EconomyUI), "ShowGoodPage")] internal static class TradeBookAgePatch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList(); var day = AccessTools.Field(typeof(PriceReport), nameof(PriceReport.day)); int count = 0;
            for (int i = 0; i + 2 < code.Count; i++)
                if (code[i].LoadsField(day) && code[i + 1].opcode == System.Reflection.Emit.OpCodes.Ldc_I4_0 &&
                    (code[i + 2].opcode == System.Reflection.Emit.OpCodes.Bgt_S || code[i + 2].opcode == System.Reflection.Emit.OpCodes.Bgt))
                { code[i + 1].opcode = System.Reflection.Emit.OpCodes.Ldc_I4; code[i + 1].operand = int.MinValue; count++; }
            if (count != 1) throw new InvalidOperationException("Trade book age hook does not match this game version");
            return code;
        }
    }
}

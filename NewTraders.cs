using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace EconomyOverhaul
{
    // ---- Rules ----
    public static partial class Rules
    {
        // Trade book. A delivered report unlocks with the player's rep in the delivering boat's home region (the user,
        // 2026-10-14; small locals 3/2/2 in Al'Ankh, 2/2/3 in Emerald and Aestrin, since 2026-10-15; TradeBook.BookGroup):
        // rep 1, three (two in Emerald and Aestrin) of each region's seven small locals ("small") and
        // Emerald's two FFL small locals; rep 2, two more small locals ("small2"), the medium, the first Aestrin heavy
        // ("heavy"), the FFL-Emerald shuttle; rep 3, the last two small locals ("small3"), the second heavy ("heavy2"), the regionals and the Happy Bay trader (rep 3 in any
        // region); rep 4, one group of three independents per region (home = the independent's group, 0-2); rep 5, the
        // region's Chronos trader.
        public static int BookTier(string group) => group == "small" ? 1 : group == "small2" || group == "medium" || group == "heavy" || group == "shuttle" ? 2 :
            group == "small3" || group == "heavy2" || group == "regional" || group == "happy" ? 3 : group == "independent" ? 4 : group == "chronos" ? 5 : int.MaxValue;
        public static bool BookUnlocked(string group, int home, int[] reps)
        {
            if (group == "happy") { foreach (int r in reps) if (r >= BookTier(group)) return true; return false; }
            if (group == "independent") { int count = 0; foreach (int r in reps) if (r >= 4) count++; return home >= 0 && count > home; }
            return home >= 0 && home < reps.Length && reps[home] >= BookTier(group);
        }
        // Whether a port releases trader reports to the player at midnight: not where the player has rep 0. Reports are not
        // held back by reputation otherwise (the 0.7.0 waits of 3/2/1 days were removed).
        public static bool BookReleases(int portLevel) => portLevel > 0;
        // Whether a report delivered by boat kind f is unlocked whenever one delivered by kind e is (so a newer f replaces an older e).
        public static bool BookCovers(string fg, int fh, string eg, int eh)
        {
            if (fg == "happy") return BookTier(eg) >= BookTier(fg);   // any region at the Happy Bay tier or more unlocks it
            if (fg == "independent") return eg == "independent" && eh >= fh || fh == 0 && eg == "chronos";
            if (eg == "happy" || eg == "independent") return false;
            return fh == eh && BookTier(fg) <= BookTier(eg);
        }
        // Tavern rumours by drink (the game's liquid number): rum, wine and coconut wine (2-4) buy the boat type and
        // the load size; mead, honey beer, rice beer and cider (5-8) the boat type; coffee and teas (10-13) basic news.
        // Water (1) and sea water (9) are refused.
        // Port capacity a trader adds at each end of a trip (the user, 2026-10-14; was 0.1).
        public const float TraderCapacity = .25f;
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
            // Happy Bay, Emerald (with FFL) and Aestrin, and of Al'Ankh Oasis and Mirage Mountain (the user, 2026-10-19: "Happy bay
            // trader now also goes to Oasis and Mirage Mountain"); every route touches Happy Bay (Rules.ValidRoute). Chronos and
            // Saffron excluded.
            if (Group == "happy") return Supply.ManagedRows().Where(p => p.Port != 21 && p.Port != 33 && (p.Region != 0 || HappyAlAnkh.Contains(p.Port))).Select(p => p.Port).OrderBy(p => p).ToArray();
            if (Group == "chronos") return new[] { Home, 21 };
            if (Ffl) return new[] { 22, 23, 24, 25 };
            return Supply.ManagedRows().Where(p => p.Port != 21 && p.Port != 33 &&
                (Group == "regional" || Group == "independent" || p.Region == Home) &&
                !(Home == 2 && (Group == "small" || Group == "heavy" || Group == "medium") && p.Port == 20) &&
                !(Home == 1 && Group == "small" && p.Port >= 22 && p.Port <= 25)).Select(p => p.Port).OrderBy(p => p).ToArray();
        }
        internal void Apply(BoatState b) { b.units = Units; b.weight = Weight; b.speed = Speed; b.wait = Wait; }
        internal static readonly int[] HappyAlAnkh = { 6, 32 };   // Oasis, Mirage Mountain
    }
    internal static class FleetDefinitions
    {
        // Speeds are m/s, converted from the approved knots and rounded to two decimals. The small locals 5 knots and the heavy
        // ones 4.5 since the user's buff of 2026-10-18 (4.5 and 4 before; VISIBLE_TRADERS_DRAFT.md, its last section); a save's
        // traders take these speeds and waits when it loads (StateCodec.Read). The medium locals 5.5 knots and 5 h, the Happy Bay
        // trader 6.5 knots and 7 h since the sail plans (the user, 2026-10-19: "Medium local: 5.5 knots", "make wait stay 5 hour",
        // "Happy bay trader: 6.5 knots, wait 7 hour"; 5 knots and 4 h, 6 knots and 6.5 h before; TRADER_RIGS_DRAFT.md).
        internal static readonly FleetDefinition[] Current = {
            new FleetDefinition("small", 0, 7, 20, 1000, 2.57f, 2.5f),
            new FleetDefinition("small", 1, 7, 20, 1000, 2.57f, 2.5f),
            new FleetDefinition("small", 2, 7, 20, 1000, 2.57f, 2.5f),
            new FleetDefinition("small", 1, 2, 20, 1000, 2.57f, 2.5f, ffl: true),
            new FleetDefinition("heavy", 2, 2, 30, 10000, 2.32f, 24, minimumMass: 220, startPorts: new[] { 19, 28 }),
            new FleetDefinition("medium", 0, 1, 40, 4000, 2.83f, 5),
            new FleetDefinition("medium", 1, 1, 40, 4000, 2.83f, 5),
            new FleetDefinition("medium", 2, 1, 40, 4000, 2.83f, 5),
            new FleetDefinition("regional", 0, 3, 40, 10000, 3.60f, 8),
            new FleetDefinition("regional", 1, 3, 40, 10000, 3.60f, 8),
            new FleetDefinition("regional", 2, 3, 40, 10000, 3.60f, 8),
            new FleetDefinition("independent", -1, 9, 70, 10000, 4.12f, 12),
            new FleetDefinition("shuttle", 1, 1, 40, 5000, 3.09f, 6.5f),
            new FleetDefinition("happy", 2, 1, 40, 5000, 3.34f, 7, startPort: 20),
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
        // Dynamic demand events this trader knows of (learnt at their ports; 2026-10-16): optional, none in older saves.
        [System.Runtime.Serialization.OptionalField] public List<int> knownEvents;
        // Visible traders (VISIBLE_TRADERS.md): which of a pair's two or three lanes between regions this trip takes, picked at
        // its departure; optional, 0 (the first lane) in older saves.
        [System.Runtime.Serialization.OptionalField] public int lane;
        // State version 6 (SaveState.reportTable): reports written as numbers into the save's report table; null in memory, and in
        // a version-4 or -5 save.
        [System.Runtime.Serialization.OptionalField] public int[] reportIds;
        internal BoatState Copy() => (BoatState)MemberwiseClone();
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
        // How far the ports the map has now lie from their vanilla places, relative to port 0 (the largest, m; 0 with no port 0).
        // Visible traders are not drawn when the islands were moved (Scrambled Seas: the user's rule).
        internal static double MaxMove()
        {
            var zero = Port.ports != null && Port.ports.Length > 0 ? Port.ports[0] : null;
            if (!zero) return 0;
            var o = zero.transform.position; double most = 0;
            for (int k = 0; k + 2 < Reference.Length; k += 3)
            {
                int index = (int)Reference[k];
                var port = index < Port.ports.Length ? Port.ports[index] : null;
                if (!port || port.portIndex != index) continue;
                var p = port.transform.position;
                most = Math.Max(most, Math.Sqrt(Math.Pow(p.x - o.x - Reference[k + 1], 2) + Math.Pow(p.z - o.z - Reference[k + 2], 2)));
            }
            return most;
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
            // a Happy Bay trader saved before the sail plans gains Oasis and Mirage Mountain (FleetDefinition.Network)
            if (group == "happy") { var missing = FleetDefinition.HappyAlAnkh.Where(p => !network.Contains(p)).ToArray(); if (missing.Length > 0) network = network.Concat(missing).ToArray(); }
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
            // A trader drawn and under way sets its own timer from its place along its lanes (VisibleTraders.Clocked).
            foreach (var b in fleet) if (!RegistrationPending(b) && !VisibleTraders.Clocked(b, dt)) b.remaining -= dt;
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
            // Also during the new game's preparation (the user, 2026-10-17: capacity should already grow there; was play only).
            if (Available(b.origin) && b.origin != b.destination) Economy.Award(b.origin, Rules.TraderCapacity);
            Economy.Award(b.destination, Rules.TraderCapacity);
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
            Demand.Learn(b, b.destination);
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
        // knowsLast: the 40% roll on the last departure from this port; forcedGood/forcedCount: a dynamic demand event's good,
        // loaded first whatever its profit, up to the count (the rest of the hold by profit, so more of it may follow).
        private static Candidate PlanWithCurrency(BoatState b, int dest, double[,] buys, double[] factors, bool knowsLast = true, int forcedGood = 0, int forcedCount = 0)
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
            if (knowsLast && StateCodec.ValidDeparture(departure) && departure.boat != b.id && departure.destination == dest &&
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
            int forced = 0; var forcedItem = forcedGood > 0 ? PrefabsDirectory.instance.GetGood(forcedGood) : null;
            for (; forcedItem && forced < forcedCount && forced < available[forcedGood] && forced < b.units && weight + forcedItem.mass <= b.weight; forced++)
            {
                double buy = buys[forcedGood, forced];
                if (buy == 0) { int raw = m.GetGoodPriceAtSupply(forcedGood, m.currentSupply[forcedGood] - forced); buys[forcedGood, forced] = buy = Rules.Round(raw * (1 + Rules.Spread(raw))); }
                double sell = Rules.Quote(report.values[forcedGood], report.supply[forcedGood] + preceding[forcedGood] + forced + 1, report.cap, report.surplus, report.deficit, false, forcedGood) * saleFactor;
                result.goods.Add(forcedGood); weight += forcedItem.mass; result.cost += buy; result.profit += sell - buy;
            }
            for (int g = 1; g < 65; g++) enqueue(g, g == forcedGood ? forced : 0);
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
            bool knowsLast = Rules.KnowsLastDeparture(UnityEngine.Random.value);
            Demand.Learn(b, b.origin); Demand.LearnFromReports(b);
            // A trip this trader may sail: the destination in its network, its route rule from that origin.
            bool CanSail(int from, int to) => from != to && LivePort(from) && LivePort(to) && World.Market(to) && Supply.Contains(to) && b.network.Contains(to) &&
                Rules.ValidRoute(b.group, from, to, b.home, (int)LivePort(from).region, (int)LivePort(to).region, Distance(from, to));
            var serve = Demand.Serve(b, m, p => CanSail(b.origin, p), out int need);
            // Not able to serve from here: an event it knows whose two ports it can both reach sends it to the producer (the user,
            // 2026-10-16), carrying whatever profits on the way.
            var fetch = serve == null ? Demand.Fetch(b, CanSail) : null;
            Candidate served = null, fetched = null;
            foreach (int p in b.network)
            {
                var destination = LivePort(p); var destinationMarket = World.Market(p);
                if (!destination || !destinationMarket || !Supply.Contains(p)) continue;
                double distance = Distance(b.origin, p);
                if (!Rules.ValidRoute(b.group, b.origin, p, b.home, (int)m.GetPort().region, (int)destination.region, distance)) continue;
                if (distance < closestDistance) { closest = p; closestDistance = distance; }
                bool serving = serve != null && p == serve.demand;
                var candidate = PlanWithCurrency(b, p, buys, factors, knowsLast, serving ? serve.good : 0, serving ? need : 0);
                if (serving && candidate.goods.Contains(serve.good)) served = candidate;
                if (fetch != null && p == fetch.producer) fetched = candidate;
                if (candidate.cost > 0 && candidate.profit > 0 && (best == null || candidate.profit / candidate.cost > best.profit / best.cost)) best = candidate;
                yield return null;
            }
            if (served != null) best = served;   // an informed trader serves the dynamic demand event first
            else if (fetched != null) best = fetched;   // or sails to its producer to fetch the good
            int purpose = served != null ? 1 : fetched != null ? 2 : 0;
            if (best == null && closest >= 0) best = new Candidate { destination = closest };
            if (best == null) { b.remaining = b.wait; yield break; }
            Economy.BatchDepth++;
            try { foreach (int g in best.goods) { double coins = Fx.NpcQuote(m, g, true); m.PurchaseGood(g); Fx.Record(Fx.Currency(m), coins, true, TradeSource.Npc); } }
            finally { Economy.BatchDepth--; }
            b.goods = best.goods.ToArray(); b.destination = best.destination;
            b.trip = b.remaining = 100 * Distance(b.origin, b.destination) / b.speed;
            VisibleTraders.OnDepart(b, EventTime);
            Demand.OnDepart?.Invoke(b, purpose);
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
            return Rules.Wrap(text, 33);
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

        // The class a delivering boat files under, by boat number within its kind: of each region's seven small locals the
        // first three "small" (rep 1), the next two "small2" (rep 2), the last two "small3" (rep 3; the user, 2026-10-15,
        // was two, two, three); in Emerald (home 1) and Aestrin (home 2) two, two, three (the user, 2026-10-15); the Lagoon's two small
        // locals stay "small"; of the two Aestrin heavies the first "heavy" (rep 2), the second "heavy2" (rep 3).
        internal static string BookGroup(BoatState b)
        {
            if (b.group != "small" && b.group != "heavy") return b.group;
            var definition = FleetDefinitions.Current.FirstOrDefault(d => d.Matches(b));
            if (definition == null || definition.Ffl) return b.group;
            int i = World.State.fleet.Where(definition.Matches).OrderBy(x => x.id).ToList().FindIndex(x => x.id == b.id);
            if (b.group == "heavy") return i >= 1 ? "heavy2" : "heavy";
            int rep1 = b.home == 1 || b.home == 2 ? 2 : 3, rep2 = b.home == 1 || b.home == 2 ? 4 : 5;
            return i >= rep2 ? "small3" : i >= rep1 ? "small2" : "small";
        }
        // Entries saved before the 2026-10-14 tiers carry their boat's old class: once per loaded game, each entry whose boat
        // is still in the fleet takes the class it files under now.
        private static SaveState reclassed;
        internal static void Reclass()
        {
            var state = World.State; if (ReferenceEquals(reclassed, state) || state?.markets == null) return;
            reclassed = state;
            var boats = state.fleet.Where(x => x != null).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First());
            foreach (var data in state.markets)
                foreach (var list in new[] { data.book, data.bookIncoming })
                    if (list != null) foreach (var e in list) if (boats.TryGetValue(e.boat, out var b)) e.group = BookGroup(b);
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
            Reclass();
            PublishPending();
            PublishBook();
            Release();
            Collect();
            Demand.Midnight(day);
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

    // ======== Visible traders (stage 4; until 2026-10-20 in VisibleTraders.cs) ========

    // ---- Rules ----
    // Visible traders (stage 4; VISIBLE_TRADERS.md as built, VISIBLE_TRADERS_DRAFT.md the design and the user's rules).
    public static partial class Rules
    {
        // A trader is drawn within 3 km of the player (the user, 2026-10-17, question 14), dropped beyond 3.2 km (Claude's margin
        // against blinking at the edge); within 1 km it also turns aside for ships ahead (question 11).
        public const double TraderShow = 3000, TraderHide = 3200, TraderNear = 1000;
        // Which traders of one hull are drawn (the user, 2026-10-18), from those within range: a drawn one keeps its copy while it is in
        // the player's sight; free copies go to the nearest, wherever they are; once a drawn one is out of sight a nearer one may take
        // its copy, but only if that one's own place is out of sight too (nothing pops in where the player looks).
        public static bool[] PickDrawn(IList<double> distance, IList<bool> drawn, IList<bool> inSight, int pool)
        {
            int n = distance.Count, drawnIn = 0, drawnAll = 0; var keep = new bool[n];
            for (int i = 0; i < n; i++) { if (!drawn[i]) continue; drawnAll++; if (inSight[i]) { keep[i] = true; drawnIn++; } }
            int slots = Math.Max(0, pool - drawnIn), free = Math.Max(0, pool - drawnAll);
            foreach (int i in Enumerable.Range(0, n).Where(i => !(drawn[i] && inSight[i])).OrderBy(i => distance[i]).ThenBy(i => i))
            {
                if (slots == 0) break;
                if (drawn[i]) { keep[i] = true; slots--; }
                else if (free > 0) { keep[i] = true; slots--; free--; }
                else if (!inSight[i]) { keep[i] = true; slots--; }   // takes the copy of a farther drawn one out of sight
            }
            return keep;
        }
        // Copies made at the start, per sail plan (TRADER_RIGS_DRAFT.md, the user, 2026-10-19: "5 local small for each region / 2
        // newly customized local small for each region (included FFL) / 2 regional long range / 1 copy for each customized
        // brig/junk/sanbuq"): the stock Dhow, Cog and Kakam 4 each (5 until 2026-10-22, the user: "reduce all default local small
        // trader copy by 1"), the stock Sanbuq, Brig and Junk 2 each, the Baghlah and Jong 1; the four small plans 2 each, every
        // other plan 1 (the two heavies share theirs); 44 in all (47 before, 41 before that, by hull).
        public static int TraderPool(string key)
        {
            if (key == null) return 0;
            if (key.StartsWith(StockKey)) { int.TryParse(key.Substring(StockKey.Length), out int hull); return HullClass(hull) == 0 ? 4 : HullClass(hull) == 2 ? 1 : 2; }
            return key.StartsWith("small-") || key == "ffl-small" ? 2 : 1;
        }
        // A copy's key: its plan, else its hull's stock rig.
        public const string StockKey = "stock-";
        public static string TraderKey(string plan, int hull) => plan ?? StockKey + hull;
        // Each hull's lantern, its region's own (the user's feedback on 0.10.4, V2; BetterNPCBoats' choice): the Al'Ankh hulls (Dhow,
        // Sanbuq, Baghlah) 110 lantern A, the Aestrin ones (Cog, Brig) 114 lantern M, the Emerald ones (Kakam, Junk, Jong) 111
        // lantern E yellow.
        public static int TraderLantern(int hull) => hull == 40 || hull == 50 ? 114 : hull == 70 || hull == 80 || hull == 90 ? 111 : 110;
        // By trader where the user chose (2026-10-19): the Happy Bay trader keeps 111 lantern E yellow on its Brig ("Happy bay trader
        // still use EA yellow lantern"), the FFL-Emerald shuttle and the FFL small pair 134 lantern E blu ("the lantern on it will be
        // EA blue lantern", "ffl-small will use blue EA lantern like Shuttle"), and the third independent Junk 134 too ("independant
        // junk 3 please use blue EA lantern", 2026-10-19); the rest their hull's.
        public static int TraderLantern(string plan, int hull) => plan == "happy" ? 111 : plan == "shuttle" || plan == "ffl-small" || plan == "indep-junk-3" ? 134 : TraderLantern(hull);
        // The helmsmen's outfits (HELMSMEN_CHRONOS_DRAFT.md, the user, 2026-10-21): each outfit's style is its material, the game's
        // NPC AlAnkh, NPC East (Emerald), NPC Medi (Aestrin), NPC East Swamp (Fire Fish Lagoon), NPC Senna, NPC Generic.
        public const int OutfitAlAnkh = 0, OutfitEmerald = 1, OutfitAestrin = 2, OutfitLagoon = 3, OutfitSenna = 4, OutfitGeneric = 5;
        public static readonly string[] OutfitNames = { "Al'Ankh", "Emerald", "Aestrin", "Lagoon", "Senna", "generic" };
        public static int OutfitKind(string material)
        {
            if (material == null) return -1;
            bool Has(string s) => material.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
            return Has("AlAnkh") ? OutfitAlAnkh : Has("East Swamp") ? OutfitLagoon : Has("East") ? OutfitEmerald : Has("Medi") ? OutfitAestrin : Has("Senna") ? OutfitSenna : Has("Generic") ? OutfitGeneric : -1;
        }
        // An outfit's style: the one most of its worn parts' materials have (-1 when none is known).
        public static int OutfitKind(IEnumerable<string> partMaterials)
        {
            var counts = new int[6];
            foreach (var m in partMaterials) { int k = OutfitKind(m); if (k >= 0) counts[k]++; }
            int best = -1; for (int k = 0; k < counts.Length; k++) if (counts[k] > 0 && (best < 0 || counts[k] > counts[best])) best = k;
            return best;
        }
        // The style a trader's helmsman wears: its home region's (0 Al'Ankh, 1 Emerald, 2 Aestrin); the Fire Fish Lagoon small pair
        // the Lagoon's; the Chronos traders Aestrin's ("H1: Aestrin"); the Happy Bay trader Emerald's ("H2: Emerald"); an independent
        // any (-1: "Independent trader just pick random one").
        public static int HelmsmanStyle(string group, int home, bool ffl)
        {
            if (group == "independent") return -1;
            if (group == "chronos") return OutfitAestrin;
            if (group == "happy") return OutfitEmerald;
            if (group == "small" && ffl) return OutfitLagoon;
            return home >= 0 && home < 3 ? home : -1;
        }
        // The outfit a trader drawn now wears, by its index in `kinds` (each outfit's style); `worn`: the outfits the other drawn
        // traders wear ("H3: No same outfit drawn at same time"). Its style's not worn; else a generic one not worn (the user: "if
        // regional outfit is running out because too many trader drawn, it can pick from general one to keep it still look different
        // from others"); else any not worn (Claude's choice); only with every outfit worn, any. An independent: any not worn, else any.
        // roll in [0, 1) picks among them; -1 when there is no outfit.
        // A line in two rows of even width (TraderBubble.Layout): the break between words that makes the wider row narrowest.
        public static string EvenRows(string line, Func<string, float> width)
        {
            var words = line.Split(' ');
            if (words.Length < 2) return line;
            string best = line; float bestWidth = float.MaxValue;
            for (int k = 1; k < words.Length; k++)
            {
                string a = string.Join(" ", words, 0, k), b = string.Join(" ", words, k, words.Length - k);
                float w = Math.Max(width(a), width(b));
                if (w < bestWidth) { bestWidth = w; best = a + "\n" + b; }
            }
            return best;
        }
        // A trader's bubble line: any but its last (Claude's choice, to confirm), roll in [0, 1).
        public static int PickLine(int count, int last, double roll)
        {
            if (count <= 0) return -1;
            if (count == 1 || last < 0 || last >= count) return Math.Max(0, Math.Min(count - 1, (int)(roll * count)));
            int k = Math.Max(0, Math.Min(count - 2, (int)(roll * (count - 1))));
            return k >= last ? k + 1 : k;
        }
        public static int PickOutfit(int style, IList<int> kinds, ICollection<int> worn, double roll)
        {
            var all = Enumerable.Range(0, kinds.Count).ToList();
            List<int> Free(Func<int, bool> fits) => all.Where(i => fits(i) && !worn.Contains(i)).ToList();
            var pool = style >= 0 ? Free(i => kinds[i] == style) : new List<int>();
            if (pool.Count == 0 && style >= 0) pool = Free(i => kinds[i] == OutfitGeneric);
            if (pool.Count == 0) pool = Free(i => true);
            if (pool.Count == 0) pool = all;
            if (pool.Count == 0) return -1;
            return pool[Math.Max(0, Math.Min(pool.Count - 1, (int)(roll * pool.Count)))];
        }
        // Which traders carry the user's captured sail plans (TRADER_RIGS_DRAFT.md, "Which traders get a plan", the user's words
        // there): the Happy Bay trader, the shuttle, each region's medium local, the two heavies (one plan), the FFL small pair (one
        // plan), the last 2 by id of each region's 7 small locals (smallFromLast 0 and 1: the highest ids), the Chronos trader of
        // Fort Aestrin and the nine independents, three plans a hull by their rank. null: the hull's stock rig.
        public static readonly string[] RegionNames = { "alankh", "emerald", "aestrin" };
        public static string TraderPlan(string group, int home, bool ffl, int independentRank, int smallFromLast)
        {
            string region = home >= 0 && home < 3 ? RegionNames[home] : null;
            switch (group)
            {
                case "small": return ffl ? "ffl-small" : region != null && smallFromLast >= 0 && smallFromLast < 2 ? "small-" + region : null;
                case "medium": return region != null ? "medium-" + region : null;
                case "heavy": case "happy": case "shuttle": return group;
                case "chronos": return home == 15 ? "chronos-brig" : null;
                case "independent": return independentRank >= 0 && independentRank < 9 ? "indep-" + (independentRank / 3 == 0 ? "sanbuq" : independentRank / 3 == 1 ? "junk" : "brig") + "-" + (independentRank % 3 + 1) : null;
                default: return null;
            }
        }
        // In a storm's rain (the user's feedback on 0.10.4, point 8): within the storm's radius plus the share of the region's storm
        // range where the game turns the weather to rain (WeatherStorms.ApplyStorm, rainBorder 0.33 in the scene).
        public const double StormRainBorder = .33;
        public static bool InStormRain(double distance, double radius, double range, double border = StormRainBorder) => distance <= radius + border * Math.Max(0, range);
        // How fast a drawn trader's tilt and height follow the water (seconds to ease): under way 0.25 s and the height at once; at a
        // berth or anchored damped by hull size (the user's feedback on 0.10.4, V3: "Damped in port"): the tilt over 1, 1.5 or 2 s
        // (small, medium, large), the height over 1 s.
        public static double TiltEase(int hullClass, bool held) => !held ? .25 : hullClass == 0 ? 1 : hullClass == 1 ? 1.5 : 2;
        public static double HeightEase(bool held) => held ? 1 : 0;
        // A drawn trader's wait in port counts from when it is moored or anchored (the user's feedback on 0.10.4, V5): until then
        // its wait timer is held. Out of sight the wait runs from the approach point as before.
        public static bool WaitHeld(bool waitingInPort, bool atItsPlace) => waitingInPort && !atItsPlace;
        // A drawn trader's sails (question 10): the fore-and-aft sails (lateen, gaff, junk) and the jibs out from the centreline to
        // leeward, 25 degrees (the user, 2026-10-20: "Reduce all fore and aft sail of trader angle to only 25 degree, jib's reduce to
        // 25 degree too"; 40, and a jib its sheeted-in angle and 20 more, before); the jibs 30 since 1.0.2 (the user, 2026-10-22:
        // "make all trader's jib angle at 30 instead of current").
        public const double SailOut = 25, JibOut = 30;
        // The speed rule while visible (the user, 2026-10-18 after 0.10.2, VISIBLE_TRADERS_DRAFT.md's last section; question 12
        // before): today's speed x its strength part x its course part. Strength: 0.75 at wind 7 to 1.25 at wind 21, in a straight
        // line between, held at both ends (the game's own wind numbers, as Windicators' dial shows them: 1.0 at 14). Course, by the
        // angle between its course and the way the wind blows (0 a dead run, 180 a head wind), in straight lines between: dead run
        // 1.10, broad reach 1.20 (the fastest, the user's), beam 1.00, close reach 0.86, head wind 0.80.
        public const double TraderWindLow = 7, TraderWindHigh = 21, TraderStrengthLow = .75, TraderStrengthHigh = 1.25;
        private static readonly double[] TraderCourseFactor = { 1.10, 1.20, 1.00, .86, .80 };   // at 0, 45, 90, 135, 180 degrees
        public static double TraderStrength(double strength) =>
            TraderStrengthLow + (TraderStrengthHigh - TraderStrengthLow) * Clamp((strength - TraderWindLow) / (TraderWindHigh - TraderWindLow), 0, 1);
        public static double TraderCourse(double courseCos)
        {
            double angle = Math.Acos(Clamp(courseCos, -1, 1)) * 180 / Math.PI;
            int i = Math.Min((int)(angle / 45), TraderCourseFactor.Length - 2);
            return TraderCourseFactor[i] + (TraderCourseFactor[i + 1] - TraderCourseFactor[i]) * (angle - 45 * i) / 45;
        }
        public static double TraderSpeed(double speed, double strength, double courseCos) => speed * TraderStrength(strength) * TraderCourse(courseCos);
        // Out of sight a trader's place along its lanes is the share of its trip's time passed (question 4, changed 2026-10-18);
        // in sight its place sets the timer, so the place carries over both ways.
        public static double TripPlace(double trip, double remaining, double length) => trip > 0 && length > 0 ? length * Clamp(1 - remaining / trip, 0, 1) : 0;
        public static double TripRemaining(double trip, double place, double length) => length > 0 ? trip * Clamp(1 - place / length, 0, 1) : 0;
        // Each trader's hull (the user's list, 2026-10-14, question 5). independentRank: the independent's place among the nine by
        // id; each three a hull: Sanbuq, Junk, Brig (Claude's order: the regions' order, Al'Ankh, Emerald, Aestrin). The Happy Bay
        // trader a Brig since the sail plans (the user, 2026-10-19: "I will change the happy bay trader to brig hull"; a Junk before).
        public static int TraderHull(string group, int home, int independentRank)
        {
            switch (group)
            {
                case "small": return home == 0 ? 10 : home == 2 ? 40 : 90;
                case "medium": case "regional": return home == 0 ? 20 : home == 2 ? 50 : 80;
                case "heavy": case "happy": return 50;
                case "shuttle": return 80;
                case "independent": return independentRank / 3 == 0 ? 20 : independentRank / 3 == 1 ? 80 : 50;
                case "chronos": return home == 0 ? 30 : home == 9 ? 70 : 50;
                default: return -1;
            }
        }
        // Berth sizes: 0 small (Dhow, Cog, Kakam), 1 medium (Sanbuq, Brig, Junk), 2 large (Baghlah, Jong).
        public static int HullClass(int hull) => hull == 10 || hull == 40 || hull == 90 ? 0 : hull == 30 || hull == 70 ? 2 : 1;
        public static int BerthClass(string size) => size == "small" ? 0 : size == "large" ? 2 : 1;
        // The Junk's deck cargo with its cabin back (the user, 2026-10-19: "The big cargo could let most of it stay inside said cabin,
        // just need to poke out to the front deck so player can observe it"; measured by the QA's junkcabin, run junk-cabin-1, in
        // the hull's frame): the cabin's hold floor 0.82-0.90 m from z -1.2 to 6.4, its front wall's inner face at z 6.62, the main
        // mast at z 1.8, the roof running on to z 8.4 over the doorway's stairs (x +-0.6: 1.04 m at z 6.8, 1.28 at 7.2, 1.47 at 7.6,
        // 1.69 at 8.0, 1.93 at 8.4, the front deck's height).
        // Since 2026-10-19 (the user: "if the good on junk can fit on the front deck, make it sit there, if not use current poking
        // out method, but let it poke out as much as possible to front deck"): a good that fits the front deck lies there
        // (FrontDeckPlace); one that does not lies on the hold floor, its front out of the front wall into the doorway as far as
        // half its height still shows over the stairs, at most to the deck's edge (8.4), its back no nearer the stern than 2.1 m
        // (clear of the mast). Was: out by 0.35 of its length, at most 0.6 m.
        public const double CabinWall = 6.62, CabinBack = 2.1, CabinFloor = .87, CabinDoorEnd = 8.4;
        private static readonly double[] stairZ = { 6.62, 6.8, 7.2, 7.6, 8.0, 8.4 }, stairY = { .85, 1.04, 1.28, 1.47, 1.69, 1.93 };
        public static double StairsAt(double z)
        {
            if (z <= stairZ[0]) return stairY[0];
            for (int i = 1; i < stairZ.Length; i++)
                if (z <= stairZ[i]) return stairY[i - 1] + (stairY[i] - stairY[i - 1]) * (z - stairZ[i - 1]) / (stairZ[i] - stairZ[i - 1]);
            return stairY[stairY.Length - 1];
        }
        // How far forward a good of that height standing on the hold floor may reach: where the stairs rise to half its height.
        public static double CabinFront(double height)
        {
            double limit = CabinFloor + height / 2;
            if (limit <= stairY[0]) return CabinWall;
            for (int i = 1; i < stairZ.Length; i++)
                if (limit <= stairY[i]) return stairZ[i - 1] + (stairZ[i] - stairZ[i - 1]) * (limit - stairY[i - 1]) / (stairY[i] - stairY[i - 1]);
            return CabinDoorEnd;
        }
        public static double CabinCargoZ(double length, double height) => Math.Max(CabinFront(height) - length / 2, CabinBack + length / 2);
        // The Junk's front deck, measured in the hull's frame (vt_dress run 7's deck grid, 0.2 m cells, forward of the cabin roof's
        // edge, where taking the cabin off changed nothing; junk-cabin-1): flat at 1.93-2.04 m from the trapdoor grating's aft
        // frame (cells at z 8.76 sloped) to the foremast (z 11.96, radius 0.24); each row's half-width of deck cells centred on the
        // centreline (cell centres; the grating's one-cell slot at x 0.6 counted as floor, run 7's rule), rows from z 8.96. A good
        // lies centred, its back at z 8.8, along the hull when it fits so, else across; every cell under it on the deck.
        public const double FrontDeckY = 1.93, FrontDeckBack = 8.8;
        private const double FrontDeckRow0 = 8.96, FrontDeckCell = .2;
        private static readonly double[] frontDeckHalf = { 1.8, 1.6, 1.8, 1.6, 1.6, 1.6, 1.4, 1.4, 1.4, 1.2, 1.2, 1.0, 1.0, .8 };
        public static bool FrontDeckFits(double across, double along)
        {
            double front = FrontDeckBack + along, need = Math.Floor((across / 2 + 1e-3) / FrontDeckCell) * FrontDeckCell;
            for (int k = 0; FrontDeckRow0 + k * FrontDeckCell <= front + 1e-3; k++)
                if (k >= frontDeckHalf.Length || frontDeckHalf[k] < need - 1e-6) return false;
            return true;
        }
        public static bool FrontDeckPlace(double across, double along, out bool turned, out double z)
        {
            turned = false; z = 0;
            if (FrontDeckFits(across, along)) { z = FrontDeckBack + along / 2; return true; }
            if (FrontDeckFits(along, across)) { turned = true; z = FrontDeckBack + across / 2; return true; }
            return false;
        }
        // A hull fits a berth when its length and beam fit the berth's rectangle (5 cm to spare for rounding): every hull of a
        // size fits the approved berths of that size (each size's rectangle holds its largest hull); a berth at a ship-for-sale
        // spot where not every hull of its size fits has the rectangle of those that do (the user's feedback on 0.10.4, V1;
        // lanemap/tools/sale_berths.py: Gold Rock's and Dragon Cliffs' take no Brig, Crab Beach's no Cog). A small berth needs only
        // its own region's small hull (the user, 2026-10-19: "since small boat only sail in their region, small berth in each
        // region don't have to consider other region's small boat"; FleetDefinition.Network): each small sale-spot berth holds it
        // (Neverdin the Dhow, Siren Song the Cog, Crab Beach the Kakam), so no Cog ever comes to Crab Beach's.
        public static bool BerthFits(double berthLength, double berthBeam, double hullLength, double hullBeam) =>
            hullLength <= berthLength + .05 && hullBeam <= berthBeam + .05;
        // A trader takes a free berth of the smallest size its hull fits, the lowest number among them (the user, 2026-10-17); -1
        // when none is free (it anchors).
        public static int PickBerth(IList<int> classes, IList<bool> taken, int hullClass)
        {
            int best = -1;
            for (int i = 0; i < classes.Count; i++)
            {
                if (taken[i] || classes[i] < hullClass) continue;
                if (best < 0 || classes[i] < classes[best]) best = i;
            }
            return best;
        }
        // Each trader's own place across its lane, -0.9..0.9 of the lane's half-width, fixed by its id (question 13 (a)).
        public static double LaneShare(int id)
        {
            double f = (id * 0.6180339887498949 + .5) % 1.0;
            return (f * 2 - 1) * .9;
        }
        // The game's ocean factor of the wind (Wind.SetNewWindTarget: 0 within 1.5 km of an island's centre, 1 at 4 km).
        public static double OceanLerp(double distance) => distance >= 999999 ? 0 : Clamp((distance - 1500) / 2500, 0, 1);
        // How far the game lowers an island at a distance (IslandHorizon.ApplyNewHorizon): the trader beyond 1.5 km sinks the same
        // (question 14).
        public static double HorizonDrop(double distance, double cameraHeight, double emerged = 1500)
        {
            double drop = distance * distance / (2 * 515662.0) - 2 * cameraHeight;
            drop *= Clamp((distance - emerged) / (emerged * 1.25), 0, 1);
            return Math.Max(drop, -20);
        }
        // Lanterns lit from 16:30 to 7:30 local time, the game's street lights' hours (IslandStreetlightsManager), and in a storm's
        // rain at any hour (InStormRain).
        public static bool LanternsLit(double localHour) => localHour > 16.5 || localHour < 7.5;
        public static bool LanternsLit(double localHour, bool inStorm) => inStorm || LanternsLit(localHour);
        // A drawn trader is solid (its kinematic body pushes the player's ships with its own speed, question 16) only up to 15 m/s:
        // its speed follows the game's clock (the speed rule times the traffic seconds of a frame), so a faster clock (sleeping, a
        // test's 400 times) makes it a wall moving hundreds of metres a second. In the play session's second launch (2026-10-20,
        // runs release-0105, ims-0105, cp-0105, cp-0105-playload-2) a trader drawn at Gold Rock City beside the player's tied Sanbuq
        // under the 400-times clock stalled the game (thousands of the game's boat impacts, no frame finishing for minutes); the same
        // launch with the traders off passed (cp-0105-playload-tradersoff). The fastest real trader is 5.5 m/s (Chronos 4.37 x 1.25).
        public const double CopySolidSpeed = 15;
        public static bool CopySolid(double speed) => speed <= CopySolidSpeed;
        // Under a fast clock (the game's sleep, x9; Sun.timescale, never Time.timeScale) a trader moves along its lanes by game
        // time but the game's cloth, physics and animations run at real speed (the user's report, 2026-10-20: the sails billowed as
        // in a gale and kept their own furling pace; and the hull fell behind its lane's turns at Dragon Cliffs). clock: the
        // traders' seconds a real second (1 at the game's own speed, 9 asleep, 0 paused).
        // - The sails' apparent wind from its speed through the water at the game's own pace: its velocity a real second over the
        //   clock (none while paused).
        public static double SailingSpeed(double realSpeed, double clock) => clock > 0 ? realSpeed / clock : 0;
        // - Unity's Cloth also pushes the cloth by its transform's own movement (worldVelocityScale, worldAccelerationScale): a copy
        //   moving clock times as fast is scaled down by the clock, back to the cloth's own value at the game's speed.
        public static double ClothWorldScale(double original, double clock) => clock > 1 ? original / clock : original;
        // - Its heading, its place across the lane, its furl and its sails' swing follow the traders' seconds (dt), so it turns as
        //   much a metre and furls as much a traders' second at any clock: 8, 5 or 4 degrees a second plus a fifth (as before at the
        //   game's speed, when dt equals the real frame), the furl 0.15 a second (the game's NPC boats).
        public static double TurnRate(int hullClass) => (hullClass == 0 ? 8 : hullClass == 1 ? 5 : 4) * 1.2;
        public const double FurlRate = .15;
        // On a harbour leg a trader spreads across it by up to 20 m (none within 10 m of its berth, the full 20 from 50 m), but no
        // further than the water there allows: its hull's side 3 m from the nearest rock or dock (the leg's room, measured on the
        // lane map's 2 m charts, Data/lanes.txt's R records; the user's report of 2026-10-20 at Dragon Cliffs' sea gate, where
        // every leg ran 23-28 m from the east wall of a 68-74 m channel, and their choice "Narrow spread + centre").
        public const double LegSpread = 20, LegRoomSpare = 3;
        public static double LegHalfWidth(double fromBerth, double room, double beam) =>
            Math.Max(0, Math.Min(LegSpread * Clamp((fromBerth - 10) / 40, 0, 1), room - beam / 2 - LegRoomSpare));

        // ---- The traders meeting the player's ships (TRADER_COLLISION_DRAFT.md, the user's answers C1-C6 and K1-K7, 2026-10-21) ----
        // In port a trader eases into its place across a leg from its berth as LegHalfWidth does: none within 10 m, the whole from 50 m.
        public static double PortEase(double fromBerth) => Clamp((fromBerth - 10) / 40, 0, 1);
        // In port a trader stops short of a player's ship blocking the rest of its way (part 2, K3 (a)): full speed until 28 m from
        // it, stopped 8 m short (gap: from its bow to where the ship blocks the way).
        public const double StopShort = 8, StopEase = 20;
        public static double InPortSpeed(double speed, double gap) => speed * Clamp((gap - StopShort) / StopEase, 0, 1);
        // Getting round a player's ship lying in the way at one place (part 1, "Blocked"): the trader keeps WayMargin from it and
        // from the water's edge. The offsets its middle may take across the leg there (+ to starboard), as far as the water allows
        // in port ("not only its 20 m spread"), eased from its berth (PortEase); the middle of the water where it is narrower than
        // the beam and both margins.
        public const double WayMargin = 2;
        public static void PortOffsets(double fromBerth, double left, double right, double halfBeam, out double min, out double max)
        {
            min = -(left - halfBeam - WayMargin); max = right - halfBeam - WayMargin;
            if (min > max) min = max = (right - left) / 2;
            double k = PortEase(fromBerth); min *= k; max *= k;
        }
        // The offset within min..max nearest `at` that keeps WayMargin from a ship lying lo..hi across the way; NaN when none does
        // (the way is blocked there).
        public static double PassOffset(double min, double max, double lo, double hi, double halfBeam, double at)
        {
            double a = lo - halfBeam - WayMargin, b = hi + halfBeam + WayMargin, best = double.NaN;
            if (min <= Math.Min(max, a)) best = Clamp(at, min, Math.Min(max, a));
            if (Math.Max(min, b) <= max) { double r = Clamp(at, Math.Max(min, b), max); if (double.IsNaN(best) || Math.Abs(r - at) < Math.Abs(best - at)) best = r; }
            return best;
        }
        // Single file (part 4b, C5): where the water is narrower than both beams + 6 m (2 m between the hulls and 2 m to each side).
        // A trader does not start into such a stretch while a player's ship is in it or within the trader's own length of either end.
        public const double SingleFileSpare = 6;
        public static bool SingleFile(double width, double beamA, double beamB) => width < beamA + beamB + SingleFileSpare;
        // Keeping right in narrow water (part 4a, C4): where the water is under 40 m wide, its starboard side 2 m from the water's
        // edge on its right (its port side at least 2 m from the left edge; the middle where both do not fit), eased from its berth.
        public const double KeepRightUnder = 40, KeepRightEdge = 2;
        public static bool KeepsRight(double left, double right) => left + right < KeepRightUnder;
        public static double KeepRight(double fromBerth, double left, double right, double beam)
        {
            double o = right - KeepRightEdge - beam / 2, least = -(left - KeepRightEdge - beam / 2);
            if (least > o) o = (right - left) / 2;
            return o * PortEase(fromBerth);
        }
        // Beside a player's ship (part 2, K4 (a)): within 3 m of its hull, the trader's move loses its part toward that ship (unit
        // toward it: tx, tz); along or away it may still move.
        public const double BesideGap = 3;
        public static void WithoutToward(ref double mx, ref double mz, double tx, double tz)
        {
            double d = mx * tx + mz * tz;
            if (d > 0) { mx -= d * tx; mz -= d * tz; }
        }
        // Its place is kept as floats, which round x and z each its own way (in 3.9 mm steps 33-66 km from the world's origin; Sage
        // Hills lies at x 42,900, z -41,800): a step with no part toward a ship could still go a little toward it, the same way every
        // frame (vtcollide vtc-12: lying still beside a ship, 0.8 and 1.6 m nearer in 30 s, under 2 mm a frame). From (fx, fz) to
        // (px, pz): a coordinate rounded toward the ship (unit tx, tz) goes one float step back (rounding is at most half a step, so
        // both back always do); false where it still goes toward it.
        public static bool NoFloatToward(float fx, float fz, ref float px, ref float pz, double tx, double tz)
        {
            if (FloatToward(fx, fz, px, pz, tx, tz) <= 0) return true;
            float bx = FloatBack(px, tx), bz = FloatBack(pz, tz);
            if (FloatToward(fx, fz, bx, pz, tx, tz) <= 0) { px = bx; return true; }
            if (FloatToward(fx, fz, px, bz, tx, tz) <= 0) { pz = bz; return true; }
            if (FloatToward(fx, fz, bx, bz, tx, tz) <= 0) { px = bx; pz = bz; return true; }
            return false;
        }
        private static double FloatToward(float fx, float fz, float px, float pz, double tx, double tz) => (double)(px - fx) * tx + (double)(pz - fz) * tz;
        // one float step against t (0.75 of 2^-23 of the value rounds to the next float that way)
        private static float FloatBack(float p, double t)
        {
            if (t == 0) return p;
            double u = Math.Max(Math.Abs((double)p) * 8.940696716308594e-8, 1e-30);
            return (float)(t > 0 ? p - u : p + u);
        }
        // An anchorage slot holds a player's ship when its hull lies within half the trader's length + 2 m of the slot (part 2).
        public static bool SlotHeld(double slotX, double slotZ, HullBox ship, double traderLength) => BoxDistance(ship, slotX, slotZ) <= traderLength / 2 + 2;
        // The berth a trader takes on its way in (part 1): the user's rule (PickBerth) among the berths with a clear way, by the
        // shorter clear way (Claude's choice). ways[i]: berth i's first and second way's lengths, NaN where that way is blocked or
        // missing. way: 1 or 2 (0 with no berth).
        public static int PickBerthWay(IList<int> classes, IList<bool> taken, int hullClass, IList<double[]> ways, out int way)
        {
            var t = new bool[classes.Count];
            for (int i = 0; i < t.Length; i++) t[i] = taken[i] || ShorterWay(ways[i]) == 0;
            int k = PickBerth(classes, t, hullClass);
            way = k >= 0 ? ShorterWay(ways[k]) : 0;
            return k;
        }
        // The shorter of a place's clear ways: 1 or 2, 0 when neither is clear (NaN: blocked or none).
        public static int ShorterWay(double[] lengths)
        {
            int best = 0;
            for (int w = 0; w < lengths.Length; w++) if (!double.IsNaN(lengths[w]) && (best == 0 || lengths[w] < lengths[best - 1])) best = w + 1;
            return best;
        }
        // Boxes on the water (part 3, C3 "check ship's whole hull"): two overlap within `margin` of each other (separating axes).
        public static bool BoxesOverlap(HullBox p, HullBox q, double margin) =>
            !(Apart(p, q, p.Fx, p.Fz, margin) || Apart(p, q, p.Rx, p.Rz, margin) || Apart(p, q, q.Fx, q.Fz, margin) || Apart(p, q, q.Rx, q.Rz, margin));
        private static bool Apart(HullBox p, HullBox q, double x, double z, double margin)
        {
            double rp = p.A * Math.Abs(p.Fx * x + p.Fz * z) + p.B * Math.Abs(p.Rx * x + p.Rz * z);
            double rq = q.A * Math.Abs(q.Fx * x + q.Fz * z) + q.B * Math.Abs(q.Rx * x + q.Rz * z);
            return Math.Abs((q.X - p.X) * x + (q.Z - p.Z) * z) > rp + rq + margin;
        }
        // From a point to a box (0 inside it).
        public static double BoxDistance(HullBox h, double x, double z)
        {
            double dx = x - h.X, dz = z - h.Z;
            double u = Math.Max(0, Math.Abs(dx * h.Fx + dz * h.Fz) - h.A), v = Math.Max(0, Math.Abs(dx * h.Rx + dz * h.Rz) - h.B);
            return Math.Sqrt(u * u + v * v);
        }
        // The nearest point of a box to (x, z) (the point itself inside it).
        public static void BoxNearest(HullBox h, double x, double z, out double nx, out double nz)
        {
            double dx = x - h.X, dz = z - h.Z;
            double u = Clamp(dx * h.Fx + dz * h.Fz, -h.A, h.A), v = Clamp(dx * h.Rx + dz * h.Rz, -h.B, h.B);
            nx = h.X + u * h.Fx + v * h.Rx; nz = h.Z + u * h.Fz + v * h.Rz;
        }
        // The axis two boxes lie farthest apart along (of their sides' four directions), as a unit from p toward q, and that gap
        // (negative where they overlap: the least overlap). Moving across it, p keeps that gap to q (the beside rule, part 2: the way
        // from p's middle to q's nearest point let a long hull slide round q's corner, nearer along its whole side; vtcollide vtc-10).
        public static double BoxApart(HullBox p, HullBox q, out double tx, out double tz)
        {
            double best = double.NegativeInfinity; tx = 0; tz = 0;
            for (int i = 0; i < 4; i++)
            {
                double x = i == 0 ? p.Fx : i == 1 ? p.Rx : i == 2 ? q.Fx : q.Rx, z = i == 0 ? p.Fz : i == 1 ? p.Rz : i == 2 ? q.Fz : q.Rz;
                double d = (q.X - p.X) * x + (q.Z - p.Z) * z;
                double rp = p.A * Math.Abs(p.Fx * x + p.Fz * z) + p.B * Math.Abs(p.Rx * x + p.Rz * z);
                double rq = q.A * Math.Abs(q.Fx * x + q.Fz * z) + q.B * Math.Abs(q.Rx * x + q.Rz * z);
                double gap = Math.Abs(d) - rp - rq;
                if (gap > best) { best = gap; double s = d < 0 ? -1 : 1; tx = x * s; tz = z * s; }
            }
            return best;
        }
        // What a box takes across a way at one place: the part of it within `band` m along the way (direction dx, dz) of the point
        // (px, pz), as offsets lo..hi across it (+ to starboard). False when none of it is there.
        // Each edge of the box clipped to the band; its ends' offsets give the span (no allocation: it runs for every place along a
        // leg a ship lies near).
        public static bool BoxAcross(HullBox h, double px, double pz, double dx, double dz, double band, out double lo, out double hi)
        {
            lo = double.PositiveInfinity; hi = double.NegativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                h.Corner(i, out double ax, out double az); h.Corner((i + 1) % 4, out double bx, out double bz);
                double a0 = (ax - px) * dx + (az - pz) * dz, a1 = (ax - px) * dz - (az - pz) * dx;     // along, across (+ starboard)
                double b0 = (bx - px) * dx + (bz - pz) * dz, b1 = (bx - px) * dz - (bz - pz) * dx;
                double t0 = 0, t1 = 1;
                if (Math.Abs(b0 - a0) < 1e-9) { if (Math.Abs(a0) > band) continue; }
                else
                {
                    double ta = (-band - a0) / (b0 - a0), tb = (band - a0) / (b0 - a0);
                    t0 = Math.Max(0, Math.Min(ta, tb)); t1 = Math.Min(1, Math.Max(ta, tb));
                    if (t0 > t1) continue;
                }
                double c0 = a1 + (b1 - a1) * t0, c1 = a1 + (b1 - a1) * t1;
                lo = Math.Min(lo, Math.Min(c0, c1)); hi = Math.Max(hi, Math.Max(c0, c1));
            }
            return lo <= hi;
        }
        // A sail plan's lanterns on its own masts (the user, 2026-10-20: "move the lantern to fit the mast"; the hull's lantern places
        // were measured on the stock masts, 0.3-0.6 m off them, and hung up to 8 m from any mast on a plan whose masts moved, rig-9's
        // VT TRADERRIG lanterns). Each measured place (x, y, z in the hull's frame) stays where a mast still stands within 1 m of it at
        // its height; one whose mast is gone moves to the nearest mast left without a lantern, at its own height, 0.4 m off that
        // mast's axis toward where it hung; a mast still without one gets one at the height of the lantern nearest along the hull,
        // 0.4 m off its axis toward the hull's middle; a place with no mast left is dropped (a lantern on every mast, question 15).
        // A mast: a point on its axis and the axis' direction, both in the hull's frame (x, y, z, dx, dy, dz).
        public const double LanternMastNear = 1.0, LanternMastOff = .4;
        public static List<double[]> LanternsOnMasts(IList<double[]> places, IList<double[]> masts)
        {
            double[] Axis(double[] m, double y) { double t = (y - m[1]) / m[4]; return new[] { m[0] + m[3] * t, y, m[2] + m[5] * t }; }
            double Across(double[] m, double[] p) { var a = Axis(m, p[1]); return Math.Sqrt((a[0] - p[0]) * (a[0] - p[0]) + (a[2] - p[2]) * (a[2] - p[2])); }
            int Nearest(double[] p, bool[] skip) { int best = -1; for (int k = 0; k < masts.Count; k++) if (!skip[k] && (best < 0 || Across(masts[k], p) < Across(masts[best], p))) best = k; return best; }
            var held = new bool[masts.Count]; var none = new bool[masts.Count]; var result = new List<double[]>(); var moved = new List<double[]>();
            foreach (var p in places)
            {
                int k = Nearest(p, none);
                if (k >= 0 && Across(masts[k], p) <= LanternMastNear) { result.Add(p); held[k] = true; } else moved.Add(p);
            }
            foreach (var p in moved)
            {
                int k = Nearest(p, held); if (k < 0) continue;
                var a = Axis(masts[k], p[1]); double dx = p[0] - a[0], dz = p[2] - a[2], d = Math.Sqrt(dx * dx + dz * dz);
                if (d < 1e-6) { dx = 0; dz = a[2] > 0 ? -1 : 1; d = 1; }
                result.Add(new[] { a[0] + dx / d * LanternMastOff, p[1], a[2] + dz / d * LanternMastOff }); held[k] = true;
            }
            for (int k = 0; k < masts.Count; k++)
            {
                if (held[k] || places.Count == 0) continue;
                var from = result.Count > 0 ? result : places.ToList();
                double y = from.OrderBy(p => Math.Abs(p[2] - Axis(masts[k], p[1])[2])).First()[1];
                var a = Axis(masts[k], y);
                result.Add(new[] { a[0], y, a[2] + (a[2] > 0 ? -LanternMastOff : LanternMastOff) }); held[k] = true;
            }
            return result;
        }
        // The good on a drawn trader's deck: the hold's most carried good; with the hold sold (the fleet sells it at the approach
        // point, Fleet.Arrive) the last trip's good stays on deck on its way in, until it is moored or anchored (the user's rule of
        // question 17: "at sea and on its way in; the deck is empty while it waits in port").
        public static int DeckGood(int mainGood, bool inPort, bool settled, int lastGood) => mainGood > 0 ? mainGood : inPort && !settled ? lastGood : 0;
        // The most carried good (most units; a tie to the one loaded first), 0 for none.
        public static int MainGood(IList<int> goods)
        {
            if (goods == null || goods.Count == 0) return 0;
            var count = new Dictionary<int, int>(); int best = 0, most = 0;
            foreach (int g in goods) count[g] = count.TryGetValue(g, out int n) ? n + 1 : 1;
            foreach (int g in goods) if (count[g] > most) { most = count[g]; best = g; }
            return best;
        }
        // The lane picked between two regions at a departure (one of the pair's two or three), fixed by the trader and the time.
        public static int PickLane(int id, double time, int choices)
        {
            if (choices <= 1) return 0;
            unchecked
            {
                long h = (long)Math.Floor(time) * 2654435761L + id * 40503L + 977;
                h ^= h >> 13; h *= 1274126177L; h ^= h >> 16;
                return (int)(((h % choices) + choices) % choices);
            }
        }
    }

    // A hull's box on the water, in real (unshifted) metres: its middle, its bow's direction (unit), half its length and half its
    // beam (TRADER_COLLISION_DRAFT.md, part 3: the player's ships by their whole hull; a trader's own; a berth's rectangle).
    public sealed class HullBox
    {
        public readonly double X, Z, Fx, Fz, A, B;
        public HullBox(double x, double z, double fx, double fz, double halfLength, double halfBeam)
        {
            double n = Math.Sqrt(fx * fx + fz * fz); if (n < 1e-9) { fx = 0; fz = 1; n = 1; }
            X = x; Z = z; Fx = fx / n; Fz = fz / n; A = halfLength; B = halfBeam;
        }
        public double Rx => Fz;     // to starboard (the mod's Across)
        public double Rz => -Fx;
        // corner i (0 bow starboard, 1 bow port, 2 stern port, 3 stern starboard)
        public void Corner(int i, out double x, out double z)
        {
            double u = i < 2 ? A : -A, v = i == 0 || i == 3 ? B : -B;
            x = X + u * Fx + v * Rx; z = Z + u * Fz + v * Rz;
        }
    }

    // ---- Lane map ----
    // A polyline in real (unshifted) x, z: places along it in metres.
    internal sealed class LanePath
    {
        internal readonly Vector2[] Points;
        internal readonly float[] Along;
        internal readonly float Length;
        internal LanePath(IEnumerable<Vector2> points)
        {
            var list = new List<Vector2>();
            foreach (var p in points) if (list.Count == 0 || (p - list[list.Count - 1]).sqrMagnitude > 1e-4f) list.Add(p);
            if (list.Count == 1) list.Add(list[0]);
            Points = list.ToArray(); Along = new float[Points.Length];
            for (int i = 1; i < Points.Length; i++) Along[i] = Along[i - 1] + Vector2.Distance(Points[i - 1], Points[i]);
            Length = Along[Along.Length - 1];
            Min = Max = Points[0];
            foreach (var p in Points) { Min = Vector2.Min(Min, p); Max = Vector2.Max(Max, p); }
        }
        internal Vector2 Start => Points[0];
        internal Vector2 End => Points[Points.Length - 1];
        internal readonly Vector2 Min, Max;      // its bounds
        internal LanePath Reversed() => new LanePath(Points.Reverse()) { room = room, roomStep = roomStep, roomFromEnd = !roomFromEnd, left = left, right = right, widthStep = widthStep, widthFromEnd = !widthFromEnd };
        // A harbour leg's water each side (Data/lanes.txt's W records, at its hull's depth): to its left and right every widthStep m from
        // its first point as stored; on the reversed leg read from its end with the sides swapped. None: wide water (WideWater each side).
        private float[] left, right; private float widthStep; private bool widthFromEnd;
        internal const float WideWater = 60f;
        internal bool HasWidth => left != null;
        internal void SetWidth(float step, float[] l, float[] r) { widthStep = step; left = l; right = r; widthFromEnd = false; }
        internal void WidthAt(float s, out float l, out float r)
        {
            if (left == null || left.Length == 0 || widthStep <= 0) { l = r = WideWater; return; }
            float t = Mathf.Clamp(widthFromEnd ? Length - s : s, 0, Length) / widthStep; int i = Mathf.Min((int)t, left.Length - 1);
            float a = i + 1 < left.Length ? Mathf.Lerp(left[i], left[i + 1], t - i) : left[i], b = i + 1 < right.Length ? Mathf.Lerp(right[i], right[i + 1], t - i) : right[i];
            if (widthFromEnd) { l = b; r = a; } else { l = a; r = b; }
        }
        // Whether a box lies within `margin` of the leg's bounds (a cheap first look before the places along it).
        internal bool Near(HullBox h, float margin)
        {
            float reach = (float)(h.A + h.B) + margin;
            return h.X >= Min.x - reach && h.X <= Max.x + reach && h.Z >= Min.y - reach && h.Z <= Max.y + reach;
        }
        // A harbour leg's room (Data/lanes.txt's R records): the distance from its middle to the nearest rock or dock every roomStep m
        // from its first point as stored (kept the other way round on the reversed leg); none: room enough everywhere.
        private float[] room; private float roomStep; private bool roomFromEnd;
        internal void SetRoom(float step, float[] samples) { roomStep = step; room = samples; roomFromEnd = false; }
        internal float RoomAt(float s)
        {
            if (room == null || room.Length == 0 || roomStep <= 0) return float.PositiveInfinity;
            float t = Mathf.Clamp(roomFromEnd ? Length - s : s, 0, Length) / roomStep; int i = Mathf.Min((int)t, room.Length - 1);
            return i + 1 < room.Length ? Mathf.Lerp(room[i], room[i + 1], t - i) : room[i];
        }
        private int Segment(float s)
        {
            int lo = 0, hi = Along.Length - 1;
            while (hi - lo > 1) { int m = (lo + hi) / 2; if (Along[m] <= s) lo = m; else hi = m; }
            return lo;
        }
        internal Vector2 At(float s)
        {
            if (s <= 0) return Points[0];
            if (s >= Length) return Points[Points.Length - 1];
            int i = Segment(s); float span = Along[i + 1] - Along[i];
            return span <= 0 ? Points[i] : Vector2.Lerp(Points[i], Points[i + 1], (s - Along[i]) / span);
        }
        // The way along it at s, smoothed over span metres each side (corners turned gradually).
        internal Vector2 Direction(float s, float span)
        {
            var a = At(s - span); var b = At(s + span);
            var d = b - a;
            if (d.sqrMagnitude < 1e-6f) { int i = Math.Min(Segment(Mathf.Clamp(s, 0, Length)), Points.Length - 2); d = Points[i + 1] - Points[i]; }
            return d.sqrMagnitude < 1e-8f ? Vector2.up : d.normalized;
        }
    }

    internal sealed class TraderBerth
    {
        internal int Port, Number, Class, Side;
        internal Vector2 Centre;
        internal float Heading, Length, Beam, WayOut;
        internal LanePath Leg;   // from the berth to the port's approach point
        private LanePath inLeg;
        internal LanePath InLeg => inLeg ?? (inLeg = Leg.Reversed());   // from the approach point to the berth
        // its second way (TRADER_COLLISION_DRAFT.md, part 1; Data/lanes.txt's S records), from the berth to the approach point; null
        // where it has none
        internal LanePath Second;
        private LanePath secondIn;
        // its direct way from the port's anchorage (way 3; Data/lanes.txt's D records, 2026-10-21 evening), from the anchorage's
        // centre to the berth; null where it has none (only in-bound: out of a berth a trader leaves by its first or second way)
        internal LanePath Direct;
        internal LanePath Out(int way) => way == 2 ? Second : Leg;
        internal LanePath In(int way) => way == 3 ? Direct : way == 2 ? (Second == null ? null : secondIn ?? (secondIn = Second.Reversed())) : InLeg;
    }
    internal sealed class TraderHarbour
    {
        internal int Port;
        internal Vector2 Approach, PortPoint;
        internal readonly List<TraderBerth> Berths = new List<TraderBerth>();
        internal Vector2 Anchorage;
        internal float AnchorRadius;
        internal LanePath AnchorLeg;   // from the approach point to the anchorage's centre
        internal LanePath AnchorSecond;   // its second way, the same way round (null: none)
        private LanePath anchorOut, anchorOut2;
        internal LanePath AnchorIn(int way) => way == 2 ? AnchorSecond : AnchorLeg;
        internal LanePath AnchorOut(int way) => way == 2 ? (AnchorSecond == null ? null : anchorOut2 ?? (anchorOut2 = AnchorSecond.Reversed())) : AnchorLeg == null ? null : anchorOut ?? (anchorOut = AnchorLeg.Reversed());
    }

    // The rest of a harbour leg against the player's ships (TRADER_COLLISION_DRAFT.md, parts 1 and 4): whether one blocks it, where
    // (along the leg), whether there by a single-file stretch (and where that stretch starts), and the offset across the leg that
    // gets round the nearest one that does not block it (NaN: none in the way).
    internal struct WayLook { internal bool Blocked, SingleFile; internal float At, Aside, StretchStart; }
    internal static class TraderWays
    {
        // the places looked at every Step m, each the part of a ship within Band m along the leg; the first PlaceSkip m from a berth
        // or the anchorage are not a single-file stretch (the dock beside the berth, as the draft's measures left out)
        internal const float Step = 5f, Band = 2.5f, PlaceSkip = 30f;
        // leg: the way the trader sails it; from: its middle's place along it; placeAtEnd: the leg ends at its berth or anchorage
        // (in-bound), else starts there (out-bound); length, beam: its hull; offset: its place across the leg now. Only the leg
        // ahead of its bow counts (a ship beside it is the beside rule's).
        internal static WayLook Look(LanePath leg, float from, bool placeAtEnd, IList<HullBox> ships, float length, float beam, float offset)
        {
            var w = new WayLook { At = float.PositiveInfinity, Aside = float.NaN, StretchStart = -1 };
            if (leg == null || ships == null || ships.Count == 0) return w;
            float bow = from + length / 2, asideAt = float.PositiveInfinity;
            foreach (var h in ships)
            {
                if (!leg.Near(h, LanePath.WideWater + Band)) continue;
                float reach = (float)(h.A + h.B) + Band + LanePath.WideWater;
                bool[] single = null;
                for (int i = Mathf.Max(0, Mathf.CeilToInt(bow / Step)); i * Step <= leg.Length + .01f; i++)
                {
                    float s = Mathf.Min(i * Step, leg.Length); var p = leg.At(s);
                    if ((new Vector2((float)h.X, (float)h.Z) - p).sqrMagnitude > reach * reach) continue;
                    var d = leg.Direction(s, Step);
                    if (!Rules.BoxAcross(h, p.x, p.y, d.x, d.y, Band, out double lo, out double hi)) continue;
                    leg.WidthAt(s, out float l, out float r);
                    if (hi < -l - 1 || lo > r + 1) continue;    // beyond the water's edge there: not in this water
                    if (single == null) single = SingleFileNear(leg, placeAtEnd, length, beam, (float)(2 * h.B));
                    bool inSingle = single[Mathf.Min(i, single.Length - 1)];
                    double off = double.NaN;
                    if (!inSingle)
                    {
                        Rules.PortOffsets(placeAtEnd ? leg.Length - s : s, l, r, beam / 2, out double min, out double max);
                        off = Rules.PassOffset(min, max, lo, hi, beam / 2, offset);
                    }
                    if (inSingle || double.IsNaN(off))
                    {
                        if (s < w.At) { w.At = s; w.Blocked = true; w.SingleFile = inSingle; w.StretchStart = inSingle ? StretchStart(leg, placeAtEnd, beam, (float)(2 * h.B), s, length) : -1; }
                        break;
                    }
                    if (s < asideAt) { asideAt = s; w.Aside = (float)off; }
                }
            }
            if (w.Blocked && asideAt > w.At) w.Aside = float.NaN;
            return w;
        }
        // Single file at each place (every Step m): the water there narrower than both beams + 6 m (Rules.SingleFile), or within the
        // trader's length of such a place (part 4b: "within its own length of either end").
        private static bool[] SingleFileNear(LanePath leg, bool placeAtEnd, float length, float beam, float shipBeam)
        {
            int n = Mathf.FloorToInt(leg.Length / Step) + 2; var raw = new bool[n]; var near = new bool[n];
            for (int i = 0; i < n; i++) raw[i] = Narrow(leg, placeAtEnd, Mathf.Min(i * Step, leg.Length), beam, shipBeam);
            int k = Mathf.CeilToInt(length / Step);
            for (int i = 0; i < n; i++) if (raw[i]) for (int j = Mathf.Max(0, i - k); j <= Mathf.Min(n - 1, i + k); j++) near[j] = true;
            return near;
        }
        private static bool Narrow(LanePath leg, bool placeAtEnd, float s, float beam, float shipBeam)
        {
            if ((placeAtEnd ? leg.Length - s : s) < PlaceSkip) return false;
            leg.WidthAt(s, out float l, out float r);
            return Rules.SingleFile(l + r, beam, shipBeam);
        }
        // Where the single-file stretch met at s starts along the leg: the narrow place nearest s within the trader's length (in whole
        // steps, as SingleFileNear widens the stretches), then back to the first of its run.
        internal static float StretchStart(LanePath leg, bool placeAtEnd, float beam, float shipBeam, float s, float length)
        {
            int i0 = Mathf.RoundToInt(s / Step), k = Mathf.CeilToInt(length / Step), last = Mathf.FloorToInt(leg.Length / Step) + 1, found = -1;
            for (int d = 0; d <= k && found < 0; d++)
                foreach (int j in new[] { i0 - d, i0 + d })
                    if (j >= 0 && j <= last && Narrow(leg, placeAtEnd, Mathf.Min(j * Step, leg.Length), beam, shipBeam)) { found = j; break; }
            if (found < 0) return s;
            while (found - 1 >= 0 && Narrow(leg, placeAtEnd, (found - 1) * Step, beam, shipBeam)) found--;
            return found * Step;
        }
        // Whether the water at s is narrow enough to back out stern first (part 4c: "turning round is no option there"; in water
        // 40 m wide or more it turns round).
        internal static bool BackAstern(LanePath leg, float s) { leg.WidthAt(s, out float l, out float r); return l + r < Rules.KeepRightUnder; }
    }

    // The approved lane map and berths (2026-10-17), embedded as Data/lanes.txt (Data/MakeLanes.py makes it from the drawing).
    internal static class LaneMap
    {
        private sealed class Graph
        {
            internal readonly Dictionary<string, int> Index = new Dictionary<string, int>();
            internal readonly List<string> Names = new List<string>();
            internal readonly List<List<KeyValuePair<int, int>>> Out = new List<List<KeyValuePair<int, int>>>();   // (to, edge; negative = walked backwards)
            internal readonly List<Vector2[]> Edges = new List<Vector2[]>();
            internal readonly List<float> EdgeLength = new List<float>();
            internal int Node(string id, string name)
            {
                if (!Index.TryGetValue(id, out int i)) { i = Names.Count; Index[id] = i; Names.Add(name); Out.Add(new List<KeyValuePair<int, int>>()); }
                if (name != "-") Index[name] = i;
                return i;
            }
            // The shortest way between two named nodes, as one polyline (null when they are not joined).
            internal List<Vector2> Path(string from, string to)
            {
                if (!Index.TryGetValue(from, out int a) || !Index.TryGetValue(to, out int b)) return null;
                if (a == b) return new List<Vector2>();
                int n = Names.Count; var dist = Enumerable.Repeat(double.PositiveInfinity, n).ToArray(); var via = new int[n]; var prev = new int[n]; var done = new bool[n];
                dist[a] = 0;
                for (int k = 0; k < n; k++)
                {
                    int u = -1; for (int i = 0; i < n; i++) if (!done[i] && (u < 0 || dist[i] < dist[u])) u = i;
                    if (u < 0 || double.IsInfinity(dist[u])) break;
                    done[u] = true; if (u == b) break;
                    foreach (var e in Out[u])
                    {
                        double d = dist[u] + EdgeLength[Math.Abs(e.Value) - 1];
                        if (d < dist[e.Key]) { dist[e.Key] = d; prev[e.Key] = u; via[e.Key] = e.Value; }
                    }
                }
                if (double.IsInfinity(dist[b])) return null;
                var chain = new List<int>(); for (int v = b; v != a; v = prev[v]) chain.Add(via[v]);
                chain.Reverse();
                var pts = new List<Vector2>();
                foreach (int e in chain)
                {
                    var p = Edges[Math.Abs(e) - 1];
                    if (e > 0) pts.AddRange(p); else for (int i = p.Length - 1; i >= 0; i--) pts.Add(p[i]);
                }
                return pts;
            }
        }
        private static readonly Dictionary<int, string> group = new Dictionary<int, string>();
        private static readonly Dictionary<string, string> regionOf = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> exit = new Dictionary<string, string>();
        private static readonly Dictionary<string, Graph> graphs = new Dictionary<string, Graph>();
        private static readonly Dictionary<string, List<Vector2[]>> inter = new Dictionary<string, List<Vector2[]>>();
        private static readonly Dictionary<int, TraderHarbour> harbours = new Dictionary<int, TraderHarbour>();
        private static readonly Dictionary<long, LanePath> trips = new Dictionary<long, LanePath>();
        private static Vector2[] lagoon = new Vector2[0];
        internal static bool Loaded { get; private set; }
        internal static IEnumerable<TraderHarbour> Harbours => harbours.Values;
        internal static int InterLanes => inter.Values.Sum(l => l.Count);
        internal static void Load()
        {
            if (Loaded) return;
            using (var reader = new System.IO.StreamReader(typeof(LaneMap).Assembly.GetManifestResourceStream("EconomyOverhaul.Data.lanes.txt")))
                Parse(reader.ReadToEnd().Split('\n'));
        }
        private static Vector2[] Points(string[] f, int from)
        {
            var list = new List<Vector2>();
            for (int i = from; i + 1 < f.Length; i += 2) list.Add(new Vector2(F(f[i]), F(f[i + 1])));
            return list.ToArray();
        }
        private static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        internal static void Parse(IEnumerable<string> lines)
        {
            group.Clear(); regionOf.Clear(); exit.Clear(); graphs.Clear(); inter.Clear(); harbours.Clear(); trips.Clear();
            foreach (string raw in lines)
            {
                string line = raw.Trim(); if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split(' ');
                switch (f[0])
                {
                    case "L": lagoon = Points(f, 1); break;
                    case "G": group[int.Parse(f[1])] = f[2]; break;
                    case "RG": regionOf[f[1]] = f[2]; break;
                    case "X": exit[f[1] + ">" + f[2]] = f[3]; break;
                    case "N": Region(f[1]).Node(f[2], f[3]); break;
                    case "E":
                        {
                            var g = Region(f[1]); int a = g.Node(f[2], "-"), b = g.Node(f[3], "-"); var p = Points(f, 4);
                            g.Edges.Add(p); g.EdgeLength.Add(new LanePath(p).Length); int e = g.Edges.Count;
                            g.Out[a].Add(new KeyValuePair<int, int>(b, e)); g.Out[b].Add(new KeyValuePair<int, int>(a, -e));
                            break;
                        }
                    case "I":
                        {
                            string key = f[1] + "|" + f[2];
                            if (!inter.TryGetValue(key, out var list)) inter[key] = list = new List<Vector2[]>();
                            list.Add(Points(f, 3)); break;
                        }
                    case "P": { var h = Harbour(int.Parse(f[1])); h.Approach = new Vector2(F(f[2]), F(f[3])); if (f.Length >= 6) h.PortPoint = new Vector2(F(f[4]), F(f[5])); break; }
                    case "B":
                        {
                            var h = Harbour(int.Parse(f[1]));
                            h.Berths.Add(new TraderBerth { Port = h.Port, Number = int.Parse(f[2]), Class = Rules.BerthClass(f[3]), Centre = new Vector2(F(f[4]), F(f[5])),
                                Heading = F(f[6]), Side = int.Parse(f[7]), Length = F(f[8]), Beam = F(f[9]), WayOut = F(f[10]), Leg = new LanePath(Points(f, 11)) });
                            break;
                        }
                    case "A":
                        {
                            var h = Harbour(int.Parse(f[1]));
                            h.Anchorage = new Vector2(F(f[2]), F(f[3])); h.AnchorRadius = F(f[4]); h.AnchorLeg = new LanePath(Points(f, 5));
                            break;
                        }
                    case "R": case "RS": case "RD":
                        {
                            var leg = LegOf(f[1], f[2], f[0] == "RS" ? 2 : f[0] == "RD" ? 3 : 1);
                            var room = new float[f.Length - 4]; for (int k = 4; k < f.Length; k++) room[k - 4] = F(f[k]);
                            leg?.SetRoom(F(f[3]), room);
                            break;
                        }
                    // the trader collision records (2026-10-21): a second way, the water each side of a way
                    case "S":
                        {
                            var h = Harbour(int.Parse(f[1])); var p = new LanePath(Points(f, 3));
                            if (f[2] == "A") h.AnchorSecond = p;
                            else { var b = h.Berths.FirstOrDefault(x => x.Number == int.Parse(f[2])); if (b != null) b.Second = p; }
                            break;
                        }
                    // a direct way from the anchorage to a berth (2026-10-21 evening)
                    case "D":
                        {
                            var b = Harbour(int.Parse(f[1])).Berths.FirstOrDefault(x => x.Number == int.Parse(f[2]));
                            if (b != null) b.Direct = new LanePath(Points(f, 3));
                            break;
                        }
                    case "W":
                        {
                            var leg = LegOf(f[1], f[2], int.Parse(f[3]));
                            int n = (f.Length - 5) / 2; var l = new float[n]; var r = new float[n];
                            for (int k = 0; k < n; k++) { l[k] = F(f[5 + 2 * k]); r[k] = F(f[6 + 2 * k]); }
                            leg?.SetWidth(F(f[4]), l, r);
                            break;
                        }
                }
            }
            Loaded = true;
        }
        // A berth's (or the anchorage's, "A") leg as stored: way 1 its first, 2 its second, 3 a berth's direct way from the anchorage.
        private static LanePath LegOf(string port, string place, int way)
        {
            var h = Harbour(int.Parse(port));
            if (place == "A") return way == 2 ? h.AnchorSecond : h.AnchorLeg;
            var b = h.Berths.FirstOrDefault(x => x.Number == int.Parse(place));
            return b == null ? null : way == 3 ? b.Direct : way == 2 ? b.Second : b.Leg;
        }
        private static Graph Region(string name) { if (!graphs.TryGetValue(name, out var g)) graphs[name] = g = new Graph(); return g; }
        private static TraderHarbour Harbour(int port) { if (!harbours.TryGetValue(port, out var h)) harbours[port] = h = new TraderHarbour { Port = port }; return h; }
        internal static TraderHarbour HarbourOf(int port) => harbours.TryGetValue(port, out var h) ? h : null;
        // Inside the lagoon a lane is 40 m wide, at sea 120 m (the user, 2026-10-17, question 3 (b)): the half-width traders spread over.
        internal static float HalfWidth(Vector2 p) => InLagoon(p) ? 20f : 60f;
        internal static bool InLagoon(Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = lagoon.Length - 1; i < lagoon.Length; j = i++)
                if ((lagoon[i].y > p.y) != (lagoon[j].y > p.y) && p.x < (lagoon[j].x - lagoon[i].x) * (p.y - lagoon[i].y) / (lagoon[j].y - lagoon[i].y) + lagoon[i].x) inside = !inside;
            return inside;
        }
        private static List<Vector2[]> InterBetween(string a, string b, out bool reversed)
        {
            reversed = false;
            if (inter.TryGetValue(a + "|" + b, out var list)) return list;
            if (inter.TryGetValue(b + "|" + a, out list)) { reversed = true; return list; }
            return null;
        }
        // How many lanes a trip may take: 1 inside a region, the pair's two or three between regions, 0 when it has none (the
        // test port, Saffron Island).
        internal static int Choices(int from, int to)
        {
            if (!group.TryGetValue(from, out string gf) || !group.TryGetValue(to, out string gt) || !harbours.ContainsKey(from) || !harbours.ContainsKey(to)) return 0;
            if (regionOf[gf] == regionOf[gt]) return 1;
            if (!exit.TryGetValue(gf + ">" + gt, out string nf) || !exit.TryGetValue(gt + ">" + gf, out string nt)) return 0;
            return InterBetween(nf, nt, out _)?.Count ?? 0;
        }
        // A trip's lanes, from the origin's approach point to the destination's (the shortest way over the region's graph; between
        // regions to its departure area, one of the pair's lanes, on through the other region's graph). Null when there is none.
        internal static LanePath Trip(int from, int to, int lane)
        {
            int choices = Choices(from, to); if (choices == 0 || from == to) return null;
            lane = ((lane % choices) + choices) % choices;
            long key = ((long)from * 1000 + to) * 16 + lane;
            if (trips.TryGetValue(key, out var cached)) return cached;
            string gf = group[from], gt = group[to], rf = regionOf[gf], rt = regionOf[gt];
            List<Vector2> pts;
            if (rf == rt) pts = graphs[rf].Path("p" + from, "p" + to);
            else
            {
                string nf = exit[gf + ">" + gt], nt = exit[gt + ">" + gf];
                var a = graphs[rf].Path("p" + from, nf); var b = graphs[rt].Path(nt, "p" + to);
                var lanes = InterBetween(nf, nt, out bool reversed);
                if (a == null || b == null || lanes == null) pts = null;
                else
                {
                    pts = new List<Vector2>(a); var l = lanes[lane];
                    if (reversed) for (int i = l.Length - 1; i >= 0; i--) pts.Add(l[i]); else pts.AddRange(l);
                    pts.AddRange(b);
                }
            }
            LanePath path = null;
            if (pts != null)
            {
                // From the approach point exactly (the graph's port node is the routing cell's centre, the same point).
                pts.Insert(0, harbours[from].Approach); pts.Add(harbours[to].Approach);
                path = new LanePath(pts);
            }
            trips[key] = path;
            return path;
        }
    }

    // ---- Hulls ----
    // Each hull's numbers, from the probes (VISIBLE_TRADERS_DRAFT.md): the real ships' roots over the water and their solid boxes
    // (vt_measure run 2), the 17 lantern places and the 8 deck cargo places with the goods too big for them (vt_dress run 7,
    // the hull's own frame: x to starboard, y up, z to the bow).
    // The user's sail plans (TRADER_RIGS_DRAFT.md): the game's own rig record of each capture, embedded as Data/trader_rigs.txt
    // (outputs/trader_rig_captures/MakeTraderRigs.py makes it). Read by this parser, not JsonUtility (it drops a mod's lists).
    internal sealed class RigSail { internal int Prefab, Mast, Color; internal float Height, Min, Max, ScaleY, ScaleZ; }
    internal sealed class RigPlan { internal string Name; internal int Hull; internal int[] Options = new int[0]; internal int[] Locked = new int[0]; internal readonly List<RigSail> Sails = new List<RigSail>(); }
    internal static class TraderRigs
    {
        // Sails from index 131 on are other mods' (the user's sail mods: 131-132 and 200-201 Unstayed Junk Sail Mast, 135-136
        // Crystal Isles); the plans use vanilla sails only (the user, 2026-10-19).
        internal const int FirstModSail = 131;
        private static readonly Dictionary<string, RigPlan> plans = new Dictionary<string, RigPlan>();
        internal static IEnumerable<RigPlan> Plans => plans.Values;
        internal static RigPlan Of(string name) => name != null && plans.TryGetValue(name, out var p) ? p : null;
        internal static bool Loaded { get; private set; }
        internal static void Load()
        {
            if (Loaded) return;
            using (var reader = new System.IO.StreamReader(typeof(TraderRigs).Assembly.GetManifestResourceStream("EconomyOverhaul.Data.trader_rigs.txt")))
                Parse(reader.ReadToEnd().Split('\n'));
        }
        private static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        internal static void Parse(IEnumerable<string> lines)
        {
            plans.Clear(); RigPlan cur = null;
            foreach (string raw in lines)
            {
                string line = raw.Trim(); if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split(' ');
                if (f[0] == "plan" && f.Length >= 3)
                {
                    cur = new RigPlan { Name = f[1], Hull = int.Parse(f[2]), Options = f.Length > 3 && f[3].Length > 0 ? f[3].Split(',').Select(int.Parse).ToArray() : new int[0],
                        Locked = f.Length > 4 && f[4] != "-" ? f[4].Split(',').Select(int.Parse).ToArray() : new int[0] };
                    plans[cur.Name] = cur;
                }
                else if (f[0] == "sail" && f.Length >= 9 && cur != null)
                    cur.Sails.Add(new RigSail { Prefab = int.Parse(f[1]), Mast = int.Parse(f[2]), Height = F(f[3]), Min = F(f[4]), Max = F(f[5]), Color = int.Parse(f[6]), ScaleY = F(f[7]), ScaleZ = F(f[8]) });
            }
            Loaded = true;
        }
        // A plan checked against the game as it runs (the build plan, step 2): each part option exists on the hull, each sail's
        // prefab is a vanilla sail, each mast exists. null when it fits, else why not (its traders then keep the stock rig).
        internal static string Check(RigPlan p, BoatCustomParts parts, BoatRefs refs)
        {
            if (!parts || !refs) return "the ship has no parts or masts";
            if (p.Options.Length > parts.availableParts.Count) return p.Options.Length + " part options for " + parts.availableParts.Count + " parts";
            for (int i = 0; i < p.Options.Length; i++)
                if (p.Options[i] < 0 || p.Options[i] >= parts.availableParts[i].partOptions.Count) return "part " + i + " has no option " + p.Options[i];
            var sails = PrefabsDirectory.instance ? PrefabsDirectory.instance.sails : null;
            foreach (var s in p.Sails)
            {
                if (s.Prefab < 0 || s.Prefab >= FirstModSail || sails == null || s.Prefab >= sails.Length || !sails[s.Prefab] || !sails[s.Prefab].GetComponent<Sail>()) return "sail " + s.Prefab + " is not a vanilla sail";
                if (s.Mast < 0 || s.Mast >= refs.masts.Length || !refs.masts[s.Mast]) return "no mast " + s.Mast;
            }
            return null;
        }
        // A new record for each load: other mods' LoadData patches change the record they are given (Unstayed Junk Sail Mast's
        // index migration), so a kept one is never passed.
        // The hull parts the user chose, from the masts the sails sit on (rig-5: the F4 tool wrote the ship's part options, not the
        // shipyard order's; the shipyard keeps the chosen parts in ShipyardPartsInstaller.currentOrder.orderedOptions until the
        // order is confirmed, while the sails are put on the previewed masts at once). For each sail's mast, the part option that
        // switches it on (the option's own object or one of its child objects above the mast) is chosen; the other parts keep the
        // capture's options. names: the parts changed, "part: option name", for the log.
        internal static int[] MastParts(RigPlan p, BoatCustomParts parts, BoatRefs refs, out string names)
        {
            // the parts read from the user's pictures (outputs/trader_rig_captures/parts_from_pictures.txt) and from the masts are kept
            var options = p.Options.ToArray(); var changed = new List<string>(); var locked = new HashSet<int>(p.Locked);
            foreach (var s in p.Sails)
            {
                var mast = s.Mast >= 0 && s.Mast < refs.masts.Length ? refs.masts[s.Mast] : null; if (!mast) continue;
                for (int i = 0; i < parts.availableParts.Count && i < options.Length; i++)
                {
                    var part = parts.availableParts[i]; int found = -1;
                    for (int j = 0; j < part.partOptions.Count && found < 0; j++)
                    {
                        var o = part.partOptions[j]; if (!o) continue;
                        if (mast.transform.IsChildOf(o.transform) || (o.childOptions != null && o.childOptions.Any(c => c && mast.transform.IsChildOf(c.transform)))) found = j;
                    }
                    if (found >= 0 && options[i] != found) { changed.Add(i + ": " + part.partOptions[found].optionName + (locked.Contains(i) ? " (the picture said " + part.partOptions[options[i]].optionName + ")" : "")); options[i] = found; }
                    if (found >= 0) locked.Add(i);
                }
            }
            // and what those options require (BoatPartOption.requires, as the shipyard checks it: a long-bowsprit forestay needs the
            // long bowsprit), until nothing changes
            for (bool more = true; more;)
            {
                more = false;
                for (int i = 0; i < parts.availableParts.Count && i < options.Length; i++)
                {
                    var chosen = parts.availableParts[i].partOptions[options[i]]; if (!chosen || chosen.requires == null) continue;
                    foreach (var need in chosen.requires)
                        for (int k = 0; k < parts.availableParts.Count && k < options.Length; k++)
                        {
                            int j = parts.availableParts[k].partOptions.IndexOf(need);
                            if (j < 0 || options[k] == j || parts.availableParts[k].partOptions.Any(o => o && chosen.requires.Contains(o) && parts.availableParts[k].partOptions.IndexOf(o) == options[k])) continue;
                            // a kept part is never changed by a requirement: the clash is logged (the stay or mast may be drawn without
                            // what it leans on)
                            if (locked.Contains(k)) { changed.Add(i + ": " + chosen.optionName + " needs " + need.optionName + " (not changed: " + parts.availableParts[k].partOptions[options[k]].optionName + " kept)"); continue; }
                            options[k] = j; changed.Add(k + ": " + need.optionName + " (required by " + chosen.optionName + ")"); more = true;
                        }
                }
            }
            names = changed.Count > 0 ? string.Join(", ", changed.Distinct().ToArray()) : "none";
            return options;
        }
        internal static SaveBoatCustomizationData Data(RigPlan p) => Data(p, p.Options);
        internal static SaveBoatCustomizationData Data(RigPlan p, int[] options)
        {
            var d = new SaveBoatCustomizationData { partActiveOptions = options.ToList() };
            foreach (var s in p.Sails)
                d.sails.Add(new SaveSailData { prefabIndex = s.Prefab, mastIndex = s.Mast, installHeight = s.Height, minAngle = s.Min, maxAngle = s.Max, health = 100f, sailColor = s.Color, scaleY = s.ScaleY, scaleZ = s.ScaleZ });
            return d;
        }
        // A plan put on the game's ship by the game's own LoadData (TRADER_RIGS_DRAFT.md, "The Brigs' sails under Shipyard
        // Expansion"). The user's plans were captured without Shipyard Expansion, so their heights are on the game's own masts:
        // with it each sail's height is raised by its mast's added length (its own rule for older saves, SaveCleaner.ConvertSave),
        // and the hull's Shipyard Expansion sail config ("SEboatSails.<scene>", which its LoadData postfix applies mast by mast:
        // texture, angle, jib side) is taken out of the mod data first, so the plan's sails keep their own, as captured. The caller
        // keeps the mod data and puts it back (PlanStep, PutStockBack).
        internal static void PutOn(SaveableBoatCustomization custom, RigPlan p, int[] options)
        {
            var refs = custom.GetComponent<BoatRefs>(); var so = custom.GetComponent<SaveableObject>();
            if (so && GameState.modData != null) GameState.modData.Remove("SEboatSails." + so.sceneIndex);
            var d = Data(p, options);
            if (refs) foreach (var s in d.sails) if (s.mastIndex >= 0 && s.mastIndex < refs.masts.Length) s.installHeight += MastAdded(refs.masts[s.mastIndex]);
            custom.LoadData(d);
        }
        // The length Shipyard Expansion added to a mast (its mastHeight less the mast's own, ShipyardExpansion.Plugin.mastHeights,
        // kept by its SaveableBoatCustomization.Awake postfix); 0 without it or for a mast it left alone.
        private static Dictionary<Mast, float> seMastHeights; private static bool seLooked;
        internal static float MastAdded(Mast m)
        {
            if (!m) return 0f;
            if (!seLooked)
            {
                seLooked = true;
                try { seMastHeights = AccessTools.Field(AccessTools.TypeByName("ShipyardExpansion.Plugin"), "mastHeights")?.GetValue(null) as Dictionary<Mast, float>; }
                catch (Exception e) { Plugin.Log?.LogWarning("Visible traders: Shipyard Expansion's mast heights not read (" + e.Message + "); the plans' heights as captured."); }
            }
            return seMastHeights != null && seMastHeights.TryGetValue(m, out float own) ? m.mastHeight - own : 0f;
        }
        internal static SaveBoatCustomizationData Clone(SaveBoatCustomizationData d)
        {
            var c = new SaveBoatCustomizationData { partActiveOptions = new List<int>(d.partActiveOptions ?? new List<int>()) };
            if (d.masts != null && d.masts.Length > c.masts.Length) c.masts = new bool[d.masts.Length];
            foreach (var s in d.sails ?? new List<SaveSailData>())
                c.sails.Add(new SaveSailData { prefabIndex = s.prefabIndex, mastIndex = s.mastIndex, installHeight = s.installHeight, minAngle = s.minAngle, maxAngle = s.maxAngle, health = s.health, sailColor = s.sailColor, scaleY = s.scaleY, scaleZ = s.scaleZ });
            return c;
        }
    }

    internal static class HullData
    {
        internal static readonly int[] Hulls = { 10, 20, 30, 40, 50, 70, 80, 90 };
        internal static float Draft(int hull)
        {
            switch (hull) { case 10: return -.57f; case 20: return -.48f; case 30: return -.42f; case 40: return -.50f; case 50: return -.44f; case 70: return .78f; case 80: return -.53f; default: return -.60f; }
        }
        // Solid box length and beam (m).
        internal static Vector2 Size(int hull)
        {
            switch (hull) { case 10: return new Vector2(14.3f, 3.2f); case 20: return new Vector2(22.6f, 5f); case 30: return new Vector2(36.3f, 5.3f); case 40: return new Vector2(14.8f, 4.6f);
                case 50: return new Vector2(29f, 6.9f); case 70: return new Vector2(32.4f, 6f); case 80: return new Vector2(24.5f, 4.8f); default: return new Vector2(12.9f, 3.2f); }
        }
        // The lanterns' centres (run 7's LANTERNPLACE lines; the Junk's middle mast 2.0 m over its cabin's roof, run 4's, the cabin
        // back since 2026-10-19).
        internal static Vector3[] Lanterns(int hull)
        {
            switch (hull)
            {
                case 10: return new[] { new Vector3(0, 3.63f, 1.83f) };
                case 20: return new[] { new Vector3(.02f, 4.41f, 4.30f), new Vector3(.01f, 7.82f, -9.95f) };
                case 30: return new[] { new Vector3(0, 4.30f, 2.50f), new Vector3(0, 8.25f, -9.01f) };
                case 40: return new[] { new Vector3(0, 3.84f, -.44f) };
                case 50: return new[] { new Vector3(0, 7.13f, -6.04f), new Vector3(0, 5.34f, 6.03f) };
                case 70: return new[] { new Vector3(0, 3.61f, -13.05f), new Vector3(0, 3.69f, 13.04f), new Vector3(0, 8.74f, 2.64f), new Vector3(0, 8.74f, -5.91f) };
                case 80: return new[] { new Vector3(0, 4.49f, 11.58f), new Vector3(0, 6.05f, 1.72f), new Vector3(0, 5.10f, -9.70f) };
                default: return new[] { new Vector3(-.30f, 4.73f, 3.57f), new Vector3(0, 6.91f, -4.38f) };
            }
        }
        // The cargo place: x, the deck's height, z (run 7's CARGOPLACE lines).
        internal static Vector3 Cargo(int hull)
        {
            switch (hull) { case 10: return new Vector3(.13f, 1.07f, .51f); case 20: return new Vector3(.86f, 1.87f, -.03f); case 30: return new Vector3(-.08f, 1.80f, 6.43f); case 40: return new Vector3(.09f, 3.78f, -5.51f);
                case 50: return new Vector3(.03f, 2.32f, .41f); case 70: return new Vector3(-.03f, 1.19f, 10.18f); case 80: return new Vector3(-.03f, .85f, 4.93f); default: return new Vector3(-.03f, 1.19f, 1.52f); }
        }
        // The Junk's cargo lies on its front deck where it fits (Rules.FrontDeckPlace), else in its cabin (Rules.CabinCargoZ), on
        // the hold floor at 0.85 m (its place's height), along the hull; the goods' fit table is not used for it.
        internal const int CabinHull = 80;
        // The goods too big for the place, drawn anyway (the user, 2026-10-18): by the good's shape (its meshes, across x along, m),
        // laid along or across the hull at (x, z) on the place's deck (run 7's FITGOOD lines; the user, 2026-10-18: no big good need
        // keep clear of the helm, so run 7's rule replayed with the wheel and helmsman scored as any deck cell,
        // outputs/visible_traders_research/dress/refit_goods.py: the Dhow's sculptures and logs, the Cog's logs and lumber and the
        // Kakam's logs moved, up to 24% of a footprint at the helm; VISIBLE_TRADERS.md).
        internal sealed class Fit { internal string Mesh; internal float W, L; internal bool Across; internal float X, Z; }
        private static readonly Dictionary<int, Fit[]> fits = new Dictionary<int, Fit[]>
        {
            { 10, new[] { F("huge_crate", 2.46f, 2.90f, false, 0f, -.04f), F("logs_cargo", 1.28f, 5.10f, false, -.21f, -.74f), F("lumber", .68f, 3.49f, false, .69f, .47f), F("long_package", 1.07f, 1.87f, false, .10f, .48f), F("long_package", 1.79f, 2.15f, false, .14f, .54f) } },
            { 20, new[] { F("huge_crate", 2.46f, 2.90f, false, .92f, .02f), F("logs_cargo", 1.28f, 5.10f, false, .92f, .92f), F("lumber", .68f, 3.49f, false, .81f, .13f) } },
            { 30, new[] { F("logs_cargo", 1.28f, 5.10f, true, .07f, 6.49f) } },
            { 40, new[] { F("huge_crate", 2.46f, 2.90f, false, .16f, -5.46f), F("logs_cargo", 1.28f, 5.10f, false, .75f, -4.96f), F("lumber", .68f, 3.49f, false, .05f, -5.55f), F("long_package", 1.07f, 1.87f, false, .06f, -5.54f), F("long_package", 1.79f, 2.15f, false, .10f, -5.48f) } },
            { 50, new[] { F("huge_crate", 2.46f, 2.90f, true, .09f, .48f), F("logs_cargo", 1.28f, 5.10f, true, -.02f, .47f), F("lumber", .68f, 3.49f, true, -.01f, .37f), F("long_package", 1.07f, 1.87f, false, 0f, .38f), F("long_package", 1.79f, 2.15f, false, .04f, .44f) } },
            { 70, new[] { F("logs_cargo", 1.28f, 5.10f, false, .04f, 10.53f) } },
            { 90, new[] { F("huge_crate", 2.46f, 2.90f, false, .03f, 1.77f), F("logs_cargo", 1.28f, 5.10f, false, .03f, 1.47f), F("lumber", .68f, 3.49f, false, -.48f, 2.07f), F("long_package", 1.07f, 1.87f, false, -.07f, 1.49f), F("long_package", 1.79f, 2.15f, false, -.03f, 1.55f) } },
        };
        private static Fit F(string mesh, float w, float l, bool across, float x, float z) => new Fit { Mesh = mesh, W = w, L = l, Across = across, X = x, Z = z };
        // Each fore-and-aft sail's range by its mast, as the game finds it once a game is under way (Mast.CheckInitialAngles: the
        // sail's collision check; vt_dress run 7's SAIL lines). At the start menu, where the templates are taken, the check has not
        // run yet (every range reads -89..89, vt-2); the Sanbuq's main lateen turns only 5 degrees to starboard (question 10).
        private static readonly Dictionary<string, Vector2> ranges = new Dictionary<string, Vector2>
        {
            { "10 mast", new Vector2(-89, 89) }, { "20 mast", new Vector2(-5, 75) }, { "20 mast_002", new Vector2(-89, 55) },
            { "30 mast_main_0", new Vector2(-89, 60) }, { "30 mast_mizzen_1__raked", new Vector2(-89, 89) }, { "50 mast_Back_0", new Vector2(-40, 40) },
            { "70 mast_back", new Vector2(-50, 55) }, { "70 mast_front", new Vector2(-55, 65) },
            { "80 mast_front_", new Vector2(-55, 65) }, { "80 mast_mid_0", new Vector2(-50, 70) }, { "80 mast_mizzen_0", new Vector2(-89, 89) },
            { "90 mast", new Vector2(-85, 60) }, { "90 mast_001", new Vector2(-55, 55) },
        };
        internal static bool Range(int hull, string mast, out Vector2 range) => ranges.TryGetValue(hull + " " + mast, out range);
        internal static Fit FitOf(int hull, string mesh, float w, float l) =>
            fits.TryGetValue(hull, out var list) ? list.FirstOrDefault(f => f.Mesh == mesh && Math.Abs(f.W - w) < .03f && Math.Abs(f.L - l) < .03f) : null;
    }

    internal sealed class SailRig { internal int[] Path; internal Transform T; internal Quaternion Rest, ToStarboard, ToPort; }
    internal sealed class HullTemplate
    {
        internal int Hull;
        internal string Plan, Key;   // the sail plan (null: the hull's stock rig) and the pool's key (Rules.TraderKey)
        internal GameObject Root;
        internal int Lanterns;   // the lanterns hung on it (the hull's places, a plan's on its own masts: Rules.LanternsOnMasts)
        internal readonly List<SailRig> Sails = new List<SailRig>();
        internal bool[] RendererOn;
        internal string Report;
        internal List<int[]> MergedPaths = new List<int[]>();   // the parts drawn as the merged meshes (TraderHulls.MergeStatic)
    }
    internal sealed class TraderCopy
    {
        internal HullTemplate Template;
        internal GameObject Go;
        internal Rigidbody Body;
        internal Renderer[] Renderers;
        internal Behaviour[] Toggled;
        internal Cloth[] Cloths;
        internal float[] ClothVelocity, ClothAcceleration;   // each cloth's own world velocity and acceleration scales (ClothClock)
        internal float ClothClockSet = 1;
        internal Light[] Lights;
        internal ParticleSystem[] Flames;
        internal ReefEffectAnimUniversal[] Reefs; internal Animator[] ReefAnimators = new Animator[0];
        internal Sail[] Sails;
        internal Transform[] SailT;
        internal Collider[] Colliders;
        // its ropes (the game's RopeEffect, each with its line; drawn by the game while the copy is shown, TraderHulls.ShowRopes)
        internal RopeEffect[] Ropes = new RopeEffect[0];
        // its helmsman's parts (TraderHulls.HelmParts' order) and the outfit he wears while drawn (-1: none chosen)
        internal GameObject[] HelmParts; internal Renderer[] HelmRenderers; internal int Outfit = -1;
        // its speech bubble while shown (TraderBubble), the time it is gone once shrinking (0 while it shows), its line
        internal GameObject Bubble; internal float BubbleGone; internal int BubbleLine = -1;
        internal GameObject Cargo;
        internal int CargoGood;
        internal bool Shown, Lit;
        internal BoatState Boat;   // the trader drawn with it, null while it is free
        internal readonly Dictionary<int, float> Ignored = new Dictionary<int, float>();
        // its four water samples (bow, stern, both sides), kept with the copy so a trader drawn again makes no new Crest queries
        internal readonly Crest.SampleHeightHelper[] Water = { new Crest.SampleHeightHelper(), new Crest.SampleHeightHelper(), new Crest.SampleHeightHelper(), new Crest.SampleHeightHelper() };
    }

    // The templates (one stripped copy of each hull, inactive) and the pool of copies made from them.
    internal static class TraderHulls
    {
        private static GameObject holder, helmsman;
        // each region's lantern (Rules.TraderLantern), its looks only, and its drawn box
        private static readonly Dictionary<int, GameObject> lanterns = new Dictionary<int, GameObject>();
        private static readonly Dictionary<int, Bounds> lanternBoxes = new Dictionary<int, Bounds>();
        private static Texture2D cleanDirt;
        // The player's collision layer: the game's own hull player collider stands on it (HullPlayerCollider.Start).
        private const int PlayerHullLayer = 12;
        private static readonly Dictionary<string, HullTemplate> templates = new Dictionary<string, HullTemplate>();
        private static readonly Dictionary<string, List<TraderCopy>> pool = new Dictionary<string, List<TraderCopy>>();
        private static readonly Dictionary<int, GameObject> goodLooks = new Dictionary<int, GameObject>();
        private static readonly Dictionary<int, KeyValuePair<Quaternion, Bounds>> goodShapes = new Dictionary<int, KeyValuePair<Quaternion, Bounds>>();
        private static readonly System.Reflection.FieldInfo reefFurled = AccessTools.Field(typeof(ReefEffectAnimUniversal), "isFurled"),
            reefMaterial = AccessTools.Field(typeof(ReefEffectAnimUniversal), "unfurledMaterial");
        private static int frames;
        internal static bool Captured { get; private set; }
        internal static int Built => pool.Values.Sum(l => l.Count);
        internal static int Total => templates.Keys.Sum(Rules.TraderPool);
        internal static HullTemplate Template(int hull) => Template(Rules.TraderKey(null, hull));
        internal static HullTemplate Template(string key) => key != null && templates.TryGetValue(key, out var t) ? t : null;
        internal static IEnumerable<HullTemplate> Templates => templates.Values;
        internal static IEnumerable<TraderCopy> Copies => pool.Values.SelectMany(l => l);
        private static readonly HashSet<Rigidbody> bodies = new HashSet<Rigidbody>();
        internal static bool IsCopy(Rigidbody body) => body && bodies.Contains(body);
        // The copies' own cloth drivers, whose cloth resets are skipped (WindClothResetPatch, ReefClothResetPatch)
        private static readonly HashSet<WindCloth> copyWinds = new HashSet<WindCloth>();
        private static readonly HashSet<ReefEffectAnimUniversal> copyReefs = new HashSet<ReefEffectAnimUniversal>();
        internal static bool IsCopy(WindCloth w) => copyWinds.Contains(w);
        internal static bool IsCopy(ReefEffectAnimUniversal r) => copyReefs.Contains(r);
        private static readonly HashSet<string> Keep = new HashSet<string> {
            "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "Cloth", "Animator", "LODGroup", "Sail", "SailConnections",
            "WindCloth", "ReefEffectAnimUniversal", "ReefEffectScaleUniversal", "ReefEffectJib", "ReefEffectJunkSail", "ReefEffectSquareBoom",
            // the ship's ropes, sheets among them, as the game draws them (the user, 2026-10-20: "all sail don't have sheet line")
            "RopeEffect", "LineRenderer" };

        // A few frames into the start menu, every ship has taken its stock sails (Mast.Start) and no save has given any its
        // player's rig yet: the templates are taken then (VISIBLE_TRADERS.md, "Chosen by Claude"), or at a game's start if sooner.
        internal static void Poll()
        {
            if (Captured) { if (!PlansDone) PlanStep(); return; }
            if (!SaveLoadManager.instance || !Refs.shiftingWorld) return;
            if (++frames >= 5 && !GameState.playing && !GameState.currentlyLoading) Capture(true);
        }
        // At a game's start or load (the prefixes below): the stock templates if not taken yet (no plans then: they need frames at the
        // start menu), and a plan's work still under way put back.
        internal static void Capture() { if (Captured) { if (!PlansDone) StopPlans("the game started before every plan was taken"); return; } Capture(false); }
        private static void Capture(bool atMenu)
        {
            if (Captured) return;
            Captured = true;
            var sw = Stopwatch.StartNew();
            try
            {
                LaneMap.Load();
                VisibleTraders.LookForChaoticWind();
                holder = new GameObject("EconomyOverhaul trader templates"); holder.SetActive(false); Object.DontDestroyOnLoad(holder);
                helmsman = HelmsmanTemplate();
                foreach (int prefab in HullData.Hulls.Select(Rules.TraderLantern).Concat(new[] { Rules.TraderLantern("shuttle", 80) }).Distinct())
                {
                    var lanternPrefab = PrefabsDirectory.instance && PrefabsDirectory.instance.directory.Length > prefab ? PrefabsDirectory.instance.directory[prefab] : null;
                    if (!lanternPrefab) continue;
                    var look = Visual(lanternPrefab); lanterns[prefab] = look; lanternBoxes[prefab] = ItemBounds(look);
                    foreach (var l in look.GetComponentsInChildren<Light>(true)) { l.shadows = LightShadows.None; l.enabled = false; }   // the user: no shadow
                }
                cleanDirt = new Texture2D(1, 1); cleanDirt.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f)); cleanDirt.Apply();
                foreach (int hull in HullData.Hulls)
                {
                    try { var t = MakeTemplate(hull, null); if (t != null) { templates[t.Key] = t; Plugin.Log?.LogInfo("Visible traders: hull " + hull + " template " + t.Report); } }
                    catch (Exception e) { Plugin.Log?.LogError("Visible traders: the template of hull " + hull + " failed: " + e); }
                }
                // the sail plans after them, a few frames each (PlanStep), grouped by hull
                try
                {
                    TraderRigs.Load();
                    if (TakePlans) foreach (var p in TraderRigs.Plans.OrderBy(p => p.Hull).ThenBy(p => p.Name, StringComparer.Ordinal))
                        if (templates.ContainsKey(Rules.TraderKey(null, p.Hull))) planQueue.Enqueue(p.Name);
                        else Plugin.Log?.LogWarning("Visible traders: plan " + p.Name + " not taken: no stock template of hull " + p.Hull + ".");
                    if (!atMenu && planQueue.Count > 0) { Plugin.Log?.LogWarning("Visible traders: the game started before the start menu's templates: no sail plans this session (" + planQueue.Count + "); their traders keep the stock rig."); planQueue.Clear(); }
                }
                catch (Exception e) { planQueue.Clear(); Plugin.Log?.LogError("Visible traders: reading the sail plans failed: " + e); }
            }
            catch (Exception e) { Plugin.Log?.LogError("Visible traders: taking the hull templates failed: " + e); }
            Plugin.Log?.LogInfo("Visible traders: " + templates.Count + " of 8 hull templates taken in " + sw.ElapsedMilliseconds + " ms (helmsman " + (helmsman ? "yes, " + outfitReport : "none") + ", lanterns " + (lanterns.Count > 0 ? string.Join(", ", lanterns.Keys.Select(k => k.ToString()).ToArray()) : "none") + "); " + planQueue.Count + " sail plans to take.");
            planWatch.Reset();
        }

        // ---- The sail plans' templates (TRADER_RIGS_DRAFT.md, build plan step 4) ----
        // At the start menu, on the game's own ship of the plan's hull: its rig read (GetData), the plan put on with the game's own
        // LoadData (the shipyard's Cancel does the same, Shipyard.CancelOrder), two frames for the old sails' deferred destroys and
        // the new sails' Start, the template taken; after the hull's last plan its own rig put back the same way. What that touches
        // (the build plan's step 1, the other mods' patches on these calls): Shipyard Expansion and Unstayed Junk Sail Mast write
        // GameState.modData in LoadData and GetData (SEboatSails.<hull>, their layout keys), so it is kept and put back as it was;
        // Unstayed Junk Sail Mast changes the record it is given, so each load gets a new one (TraderRigs.Data, Clone). The stock
        // sails' swing check (Mast.CheckInitialAngles, waiting for the game to start) is stopped with the mast's coroutines before
        // the first plan (it would log "sail is null!" for the sails taken off) and started again for the sails put back, as
        // Mast.AttachInitialSail starts it.
        private static readonly Queue<string> planQueue = new Queue<string>();
        private static int planHull = -1, planWait;
        private static string planLoaded;
        private static SaveBoatCustomizationData planStock;
        private static Dictionary<string, string> planModData;
        private static readonly Stopwatch planWatch = new Stopwatch();
        internal static readonly Dictionary<string, int[]> planOptions = new Dictionary<string, int[]>();   // the part options each plan was put on with
        internal static readonly Dictionary<int, SaveBoatCustomizationData> StockRigs = new Dictionary<int, SaveBoatCustomizationData>();   // each touched ship's own rig as read, for the tests
        private static readonly System.Reflection.MethodInfo checkInitialAngles = AccessTools.Method(typeof(Mast), "CheckInitialAngles");
        internal static bool PlansDone => planQueue.Count == 0 && planHull < 0;
        internal static bool TakePlans = true;   // off only for the test's baseline (QA traderrigsbase: the ships for sale untouched)
        internal static int PlanTemplates => templates.Values.Count(t => t.Plan != null);
        internal static double PlanMs => planWatch.Elapsed.TotalMilliseconds;   // the time spent in the plans' frames, for the tests
        private static void PlanStep()
        {
            if (GameState.playing || GameState.currentlyLoading) { StopPlans("the game started before every plan was taken"); return; }
            planWatch.Start();
            try
            {
                if (planLoaded != null)
                {
                    if (--planWait > 0) return;
                    string name = planLoaded; planLoaded = null;
                    var t = MakeTemplate(planHull, name);
                    int want = TraderRigs.Of(name)?.Sails.Count ?? 0;
                    // a plan whose sails did not all go on (a mast left switched off: another mod's hull parts, rig-1 with Shipyard
                    // Expansion) is not used: its traders keep the stock rig
                    if (t != null && t.Sails.Count < want)
                    {
                        var off = Original(planHull)?.GetComponent<BoatRefs>()?.masts.Where(m => m && m.sails.Count > 0 && !m.gameObject.activeInHierarchy).Select(m => m.name + " under " + FirstOff(m.transform)).ToArray() ?? new string[0];
                        Plugin.Log?.LogWarning("Visible traders: plan " + name + " not used: " + t.Sails.Count + " of its " + want + " sails on" + (off.Length > 0 ? " (masts switched off: " + string.Join(", ", off) + ")" : "") + "; its traders keep the stock rig. " + t.Report);
                        Object.Destroy(t.Root);
                    }
                    else if (t != null) { templates[t.Key] = t; Plugin.Log?.LogInfo("Visible traders: plan " + name + " template " + t.Report); }
                    return;
                }
                if (planQueue.Count == 0 || planHull >= 0 && TraderRigs.Of(planQueue.Peek())?.Hull != planHull) { PutStockBack(); return; }
                var plan = TraderRigs.Of(planQueue.Dequeue()); if (plan == null) return;
                var boat = Original(plan.Hull);
                var custom = boat ? boat.GetComponent<SaveableBoatCustomization>() : null;
                if (!custom) { Plugin.Log?.LogWarning("Visible traders: plan " + plan.Name + " not taken: hull " + plan.Hull + " has no rig."); return; }
                string why = TraderRigs.Check(plan, boat.GetComponent<BoatCustomParts>(), boat.GetComponent<BoatRefs>());
                if (why != null) { Plugin.Log?.LogWarning("Visible traders: plan " + plan.Name + " not used (" + why + "): its traders keep the stock rig."); return; }
                if (planHull < 0)
                {
                    planHull = plan.Hull;
                    planModData = GameState.modData != null ? new Dictionary<string, string>(GameState.modData) : null;
                    foreach (var m in boat.GetComponent<BoatRefs>().masts) if (m) m.StopAllCoroutines();
                    planStock = TraderRigs.Clone(custom.GetData()); StockRigs[plan.Hull] = TraderRigs.Clone(planStock);
                }
                var options = TraderRigs.MastParts(plan, boat.GetComponent<BoatCustomParts>(), boat.GetComponent<BoatRefs>(), out string mastParts);
                planOptions[plan.Name] = options;
                Plugin.Log?.LogInfo("Visible traders: plan " + plan.Name + "'s hull parts from its sails' masts: " + mastParts + " (options " + string.Join(",", options.Select(o => o.ToString()).ToArray()) + ")");
                TraderRigs.PutOn(custom, plan, options);
                var offNow = boat.GetComponent<BoatRefs>().masts.Where(m => m && m.sails.Count > 0 && !m.gameObject.activeInHierarchy).Select(m => m.name + " under " + FirstOff(m.transform)).ToArray();
                if (offNow.Length > 0) Plugin.Log?.LogWarning("Visible traders: plan " + plan.Name + " loaded with masts switched off: " + string.Join(", ", offNow));
                planLoaded = plan.Name; planWait = 2;
            }
            catch (Exception e) { Plugin.Log?.LogError("Visible traders: a sail plan failed: " + e); planLoaded = null; PutStockBack(); }
            finally { planWatch.Stop(); }
        }
        // The hull's own rig put back on the game's ship, its sails' swing check started again, the mod data as it was.
        private static void PutStockBack()
        {
            int hull = planHull; planHull = -1; planLoaded = null;
            if (hull < 0 || planStock == null) return;
            try
            {
                var boat = Original(hull);
                var custom = boat ? boat.GetComponent<SaveableBoatCustomization>() : null;
                if (custom)
                {
                    // the mod data as it was first (the plans left the last plan's Shipyard Expansion sail config in it, which its
                    // LoadData postfix would put on the ship's own sails), and again below
                    if (planModData != null && GameState.modData != null) { GameState.modData.Clear(); foreach (var kv in planModData) GameState.modData[kv.Key] = kv.Value; }
                    custom.LoadData(TraderRigs.Clone(planStock));
                    int checks = 0;
                    if (checkInitialAngles != null)
                        foreach (var m in boat.GetComponent<BoatRefs>().masts)
                            if (m) foreach (var s in m.sails.ToList()) if (s) { m.StartCoroutine((System.Collections.IEnumerator)checkInitialAngles.Invoke(m, new object[] { s })); checks++; }
                    Plugin.Log?.LogInfo("Visible traders: hull " + hull + "'s own rig put back (" + planStock.sails.Count + " sails, " + checks + " swing checks started).");
                }
            }
            catch (Exception e) { Plugin.Log?.LogError("Visible traders: putting hull " + hull + "'s own rig back failed: " + e); }
            finally
            {
                if (planModData != null && GameState.modData != null) { GameState.modData.Clear(); foreach (var kv in planModData) GameState.modData[kv.Key] = kv.Value; }
                planModData = null; planStock = null;
            }
        }
        // the first switched-off object from a part up to its ship (for the log)
        private static string FirstOff(Transform t) { string off = null; for (var x = t; x; x = x.parent) { if (!x.gameObject.activeSelf) off = x.name; if (x.GetComponent<BoatRefs>()) break; } return off ?? "none"; }
        private static void StopPlans(string why)
        {
            int left = planQueue.Count + (planLoaded != null ? 1 : 0);
            planQueue.Clear(); PutStockBack();
            if (left > 0) Plugin.Log?.LogWarning("Visible traders: " + left + " sail plans not taken (" + why + "); their traders keep the stock rig.");
        }

        // The game's own ship: the one its save manager registers (vt-1: a scan of every SaveableObject found Shipwright and
        // Storage's inactive hull templates first, taken before the stock sails go on), else one under an active scene root.
        private static PurchasableBoat Original(int hull)
        {
            var objects = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentObjects() : null;
            var registered = objects != null && hull < objects.Length && objects[hull] ? objects[hull].GetComponent<PurchasableBoat>() : null;
            if (registered && registered.GetComponent<BoatRefs>()) return registered;
            foreach (var s in Resources.FindObjectsOfTypeAll<SaveableObject>())
                if (s && s.sceneIndex == hull && s.gameObject.scene.IsValid() && s.transform.root.gameObject.activeSelf && s.GetComponent<PurchasableBoat>() && s.GetComponent<BoatRefs>()) return s.GetComponent<PurchasableBoat>();
            return null;
        }
        private static bool ActiveUnder(Transform t, Transform root) { for (var c = t; c && c != root; c = c.parent) if (!c.gameObject.activeSelf) return false; return true; }
        private static int[] IndexPath(Transform t, Transform root) { var l = new List<int>(); for (var c = t; c && c != root; c = c.parent) l.Insert(0, c.GetSiblingIndex()); return l.ToArray(); }
        internal static Transform FindIndex(Transform root, int[] path) { var c = root; foreach (int i in path) { if (i >= c.childCount) return null; c = c.GetChild(i); } return c; }

        private sealed class SailData
        {
            internal int[] Path; internal string Kind = "none"; internal float Min, Max, Closest, CloseL = float.NaN; internal Vector3 Axis, InitEuler; internal Quaternion InitRot;
            internal bool Furled; internal Material Unfurled; internal int[] ClothPath;
        }
        // The sails of the game's own ship as installed (vt_dress' ReadSails): each one's control and range.
        private static List<SailData> ReadSails(PurchasableBoat boat, int hull, bool plan)
        {
            var list = new List<SailData>(); var root = boat.transform;
            var angles = root.GetComponentsInChildren<RopeControllerSailAngle>(true); var jibs = root.GetComponentsInChildren<RopeControllerSailAngleJib>(true);
            foreach (var s in root.GetComponentsInChildren<Sail>(true))
            {
                if (!ActiveUnder(s.transform, root)) continue;
                var h = s.GetComponent<HingeJoint>();
                var d = new SailData { Path = IndexPath(s.transform, root), Min = s.minAngle, Max = s.maxAngle };
                if (h) d.Axis = h.axis;
                var a = h ? angles.FirstOrDefault(c => c && c.sailHinge == h) : null;
                var j = h ? jibs.Where(c => c && c.jibAngleMaster && c.jibAngleMaster.sailHinge == h).ToArray() : new RopeControllerSailAngleJib[0];
                if (a)
                {
                    d.Kind = "angle"; d.Closest = a.limitBothClosestAngle; d.InitEuler = Fields.Get<Vector3>(a, "initialEulerAngles");
                    // a plan's sails keep the range of its capture (LoadSail gives it them), the stock ones the measured range
                    if (!plan && s.transform.parent && HullData.Range(hull, s.transform.parent.name, out var range)) { d.Min = range.x; d.Max = range.y; }
                }
                else if (j.Length > 0)
                {
                    d.Kind = "jib";
                    foreach (var c in j) if (c.side == RopeControllerSailAngleJib.JibWinch.left) d.CloseL = c.closestAngle;
                    d.InitRot = Fields.Get<Quaternion>(j[0].jibAngleMaster, "initialLocalRot");
                }
                else if (s.GetComponent<SquareAngleMaster>()) d.Kind = "square";
                // a sail furled now (a ship for sale) shows the empty material on its cloth: the copy must keep the real one
                var reef = s.GetComponent<ReefEffectAnimUniversal>();
                if (reef && s.cloth && reefFurled != null && (bool)reefFurled.GetValue(reef))
                {
                    d.Furled = true; d.Unfurled = reefMaterial?.GetValue(reef) as Material; d.ClothPath = IndexPath(s.cloth.transform, root);
                }
                list.Add(d);
            }
            return list;
        }

        // The pose rule (the user, question 10): square sails squared; fore-and-aft sails and jibs out from the centreline
        // (Rules.SailOut 25 and Rules.JibOut 30 degrees: the user, 2026-10-20 and 2026-10-22; 40 and 16 + 20 before), to leeward
        // by the game's own force rule (vt_measure run 2's PoseSails, as vt_dress).
        private static Quaternion Pose(Transform t, SailData d, Vector3 fwd, Vector3 right, Vector3 windDir)
        {
            var keep = t.localRotation;
            if (d.Kind == "square") { var e = t.localEulerAngles; return Quaternion.Euler(e.x, e.y, 0f); }
            if (d.Kind != "angle" && d.Kind != "jib") return keep;
            Func<float, Quaternion> pose;
            if (d.Kind == "angle") { var cur = t.localEulerAngles; float baseZ = d.InitEuler.sqrMagnitude < 1e-6f ? cur.z : d.InitEuler.z; pose = a => Quaternion.Euler(cur.x, cur.y, baseZ + a); }
            else { var rest = d.InitRot.x == 0 && d.InitRot.y == 0 && d.InitRot.z == 0 && d.InitRot.w == 0 ? t.localRotation : d.InitRot; pose = a => rest * Quaternion.AngleAxis(a, d.Axis); }
            float lo = d.Max > d.Min ? d.Min : -89f, hi = d.Max > d.Min ? d.Max : 89f, a0 = 0f, across = -1f;
            for (float a = lo; a <= hi; a += .5f)
            {
                t.localRotation = pose(a);
                var n = t.up; n.y = 0; if (n.magnitude < .3f) continue;
                float x = Mathf.Abs(Vector3.Dot(n.normalized, right));
                if (x > across) { across = x; a0 = a; }
            }
            float outAngle = (float)(d.Kind == "jib" ? Rules.JibOut : Rules.SailOut);
            float pick = a0, bestDrive = float.MinValue;
            foreach (float a in new[] { a0 + outAngle, a0 - outAngle })
            {
                t.localRotation = pose(a);
                var n = t.up; n.y = 0; n = n.normalized; if (Vector3.Dot(n, windDir) < 0) n = -n;
                float drive = Vector3.Dot(n, fwd);
                if (drive > bestDrive) { bestDrive = drive; pick = a; }
            }
            // the angle even past the sail's limit (the user, 2026-10-19: "Go to the angle anyway", "Keep 40°"; clamped to it before):
            // where the limit is smaller the sail is drawn through what it would meet; the range still picks the side
            var result = pose(pick);
            if (pick < lo - .01f || pick > hi + .01f) pastLimit.Add(d.Kind + " " + pick.ToString("F0") + " (limit " + lo.ToString("F0") + ".." + hi.ToString("F0") + ")");
            t.localRotation = keep;
            return result;
        }

        private static readonly List<string> pastLimit = new List<string>();   // the poses past their sail's limit, for the report
        // A plan's lantern places on the masts its parts switched on (the game's own masts, BoatRefs.masts: the ones standing, not
        // the stays, bowsprits or topmasts; each axis from the mast's transform, its up kept where it leans under 45 degrees, else
        // upright), by Rules.LanternsOnMasts; the report names each mast and its lantern.
        private static Vector3[] LanternsOnMasts(PurchasableBoat boat, int hull, out string why)
        {
            var root = boat.transform; var refs = boat.GetComponent<BoatRefs>();
            var masts = (refs ? refs.masts : new Mast[0]).Where(m => m && m.gameObject.activeInHierarchy && !m.onlyStaysails &&
                m.name.IndexOf("stay", StringComparison.OrdinalIgnoreCase) < 0 && m.name.IndexOf("bowsprit", StringComparison.OrdinalIgnoreCase) < 0 &&
                m.name.IndexOf("extension", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            var axes = masts.Select(m =>
            {
                Vector3 p = root.InverseTransformPoint(m.transform.position), u = root.InverseTransformDirection(m.transform.up);
                if (u.y < .7f) u = Vector3.up;
                return new double[] { p.x, p.y, p.z, u.x, u.y, u.z };
            }).ToList();
            var stock = HullData.Lanterns(hull).Select(v => new double[] { v.x, v.y, v.z }).ToList();
            var placed = Rules.LanternsOnMasts(stock, axes).Select(a => new Vector3((float)a[0], (float)a[1], (float)a[2])).ToArray();
            why = " on " + string.Join(", ", masts.Select(m => m.name).ToArray()) + " at " + string.Join(" ", placed.Select(v => v.ToString("F2")).ToArray());
            return placed;
        }

        private static HullTemplate MakeTemplate(int hull, string plan)
        {
            var boat = Original(hull);
            if (!boat) { Plugin.Log?.LogWarning("Visible traders: hull " + hull + " not found in the scene."); return null; }
            var root = boat.transform;
            var sails = ReadSails(boat, hull, plan != null);
            pastLimit.Clear();
            // a plan's hull parts that differ from the stock rig's (the lantern and cargo places were measured on the stock parts)
            string partsDiffer = "";
            if (plan != null && planOptions.TryGetValue(plan, out var used) && planStock != null)
                partsDiffer = string.Join(" ", Enumerable.Range(0, used.Length).Where(i => i >= planStock.partActiveOptions.Count || planStock.partActiveOptions[i] != used[i])
                    .Select(i => i + ":" + (i < planStock.partActiveOptions.Count ? planStock.partActiveOptions[i].ToString() : "-") + ">" + used[i]).ToArray());
            var wheel = boat.GetComponentsInChildren<GPButtonSteeringWheel>(true).FirstOrDefault(w => ActiveUnder(w.transform, root));
            Vector3 wheelAt = wheel ? root.InverseTransformPoint(wheel.transform.position) : new Vector3(0, 2, -HullData.Size(hull).x * .3f);
            var lanternAt = HullData.Lanterns(hull); string lanternWhy = "";
            if (plan != null) lanternAt = LanternsOnMasts(boat, hull, out lanternWhy);
            var copy = Object.Instantiate(boat.gameObject, holder.transform, true);
            copy.name = boat.gameObject.name + " (trader)";
            var t = new HullTemplate { Hull = hull, Plan = plan, Key = Rules.TraderKey(plan, hull), Root = copy };
            // every hull as the game builds it (the Junk with its cabin again since the user's word of 2026-10-19: "i want to add the
            // junk's house cabin back so it looks like default hull"; from 2026-10-18 it was built "(no cabin)")
            string build = plan ?? "stock";
            // sails: the rest pose as installed (furled in port), the two set poses (the wind over the port side: out to
            // starboard; over the starboard side: out to port)
            Vector3 fwd = copy.transform.forward, right = copy.transform.right; fwd.y = 0; right.y = 0; fwd.Normalize(); right.Normalize();
            foreach (var d in sails)
            {
                var st = FindIndex(copy.transform, d.Path); if (!st) continue;
                t.Sails.Add(new SailRig { T = st, Rest = st.localRotation, ToStarboard = Pose(st, d, fwd, right, right), ToPort = Pose(st, d, fwd, right, -right) });
                if (d.Furled && d.Unfurled && d.ClothPath != null)
                {
                    var cloth = FindIndex(copy.transform, d.ClothPath); var r = cloth ? cloth.GetComponent<Renderer>() : null;
                    if (r) r.sharedMaterial = d.Unfurled;
                    var reef = st.GetComponent<ReefEffectAnimUniversal>(); if (reef && reef.furledSail) reef.furledSail.enabled = false;
                }
            }
            // the user's feedback on 0.10.4: the hull independent of the game's ship (it shared that ship's damage, dirt and sail
            // colour through their materials), no mooring ropes, no purchase paper
            int materials = OwnMaterials(copy);
            int ropes = DropMooring(copy);
            var paper = AccessTools.Field(typeof(PurchasableBoat), "purchaseUI")?.GetValue(boat) as GameObject;
            bool paperOff = DropPaper(copy, root, paper);
            // the game's own hull collider for the player (HullPlayerCollider: its hull's mesh, on the player's layer), read before
            // the scripts are stripped
            var hullForPlayer = copy.GetComponentsInChildren<HullPlayerCollider>(true).Select(p => p.GetComponent<MeshCollider>()).FirstOrDefault(m => m && m.sharedMesh);
            // a hull without one (the Jong, vt-10): its largest solid mesh collider outside the sails
            if (!hullForPlayer)
            {
                var sailRoots = copy.GetComponentsInChildren<Sail>(true).Select(s => s.transform).ToArray();
                hullForPlayer = copy.GetComponentsInChildren<MeshCollider>(true).Where(m => m.sharedMesh && !m.isTrigger && !m.name.Contains("walk") && !m.name.Contains("embark") && !sailRoots.Any(s => m.transform.IsChildOf(s)))
                    .OrderByDescending(Volume).FirstOrDefault();
            }
            var hullT = hullForPlayer ? hullForPlayer.transform : null;   // kept by reference: dropping parts changes the order numbers
            var playerHullMesh = hullForPlayer ? hullForPlayer.sharedMesh : null;
            int frozenRopes = DropClothRopes(copy);
            var notDrawn = ReefAndAnchorRopes(copy);   // read while the game's rope controllers are still on it
            int removed = Strip(copy);
            int dropped = DropParts(copy);
            int ropeLines = TrimRopes(copy, notDrawn, out int ropesOff, out int reefAnchor);
            int colliders = TrimColliders(copy);
            // no walk collider (layer 8): the game keeps a ship's apart from it, and its shipyard sail check outlines any it meets
            // (ShipyardSailColChecker.OnTriggerEnter, layer 8 only), which threw in ObstructionOutline with a trader's (vt-19-load;
            // the Dhow, Cog, Junk and Kakam kept one since 0.10.2)
            foreach (var c in copy.GetComponentsInChildren<Collider>(true).Where(c => c.gameObject.layer == 8).ToList()) { Object.DestroyImmediate(c); colliders--; }
            int unbound = DropUnbound(copy);
            // the player meets the trader as the game's NPC boats (the user's feedback on 0.10.4, V6): its hull collider on the
            // player's layer, moving with it; no boarding
            bool forPlayer = false;
            if (hullT && playerHullMesh)
            {
                var pc = new GameObject("player collider") { layer = PlayerHullLayer };
                pc.transform.SetParent(hullT, false);
                pc.AddComponent<MeshCollider>().sharedMesh = playerHullMesh;
                forPlayer = true;
            }
            else
            {
                // no hull mesh collider at all (the Jong, vt-12): the hull's own solid colliders kept above, copied on the player's layer
                var sailRoots = copy.GetComponentsInChildren<Sail>(true).Select(s => s.transform).ToArray();
                foreach (var c in copy.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger && !sailRoots.Any(s => c.transform.IsChildOf(s))).ToList())
                {
                    var pc = new GameObject("player collider") { layer = PlayerHullLayer };
                    pc.transform.SetParent(c.transform, false);
                    if (c is BoxCollider b) { var n = pc.AddComponent<BoxCollider>(); n.center = b.center; n.size = b.size; }
                    else if (c is CapsuleCollider k) { var n = pc.AddComponent<CapsuleCollider>(); n.center = k.center; n.radius = k.radius; n.height = k.height; n.direction = k.direction; }
                    else if (c is SphereCollider s) { var n = pc.AddComponent<SphereCollider>(); n.center = s.center; n.radius = s.radius; }
                    else if (c is MeshCollider m && m.sharedMesh) { var n = pc.AddComponent<MeshCollider>(); n.sharedMesh = m.sharedMesh; n.convex = m.convex; }
                    else { Object.DestroyImmediate(pc); continue; }
                    forPlayer = true;
                }
            }
            // each sail's place in the template as it is now (dropping parts changes the children's order numbers)
            t.Sails.RemoveAll(s => !s.T);
            foreach (var s in t.Sails) s.Path = IndexPath(s.T, copy.transform);
            foreach (var c in copy.GetComponentsInChildren<Collider>(true)) if (c.CompareTag("Boat")) c.tag = "Untagged";   // question 16: no Boat tag
            // not interpolated: the drawn place is set every frame (VisibleTraders.Place; vt-3: an interpolated body slid up to 135 m
            // during a world shift); the game's continuous modes are not for kinematic bodies (Unity warns once a body, and per copy)
            foreach (var rb in copy.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb.collisionDetectionMode == CollisionDetectionMode.Continuous || rb.collisionDetectionMode == CollisionDetectionMode.ContinuousDynamic)
                    rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                rb.isKinematic = true; rb.interpolation = RigidbodyInterpolation.None; rb.detectCollisions = true;
            }
            foreach (var s in copy.GetComponentsInChildren<Sail>(true)) s.enabled = false;
            // its animators (the sails' furling) stand still while nothing of it is drawn (a hidden copy's renderers are off)
            foreach (var a in copy.GetComponentsInChildren<Animator>(true)) a.cullingMode = AnimatorCullingMode.CullCompletely;
            // the helmsman at the wheel (Shipwright's offset, in the hull's frame; static: the user, question 9)
            if (helmsman)
            {
                var h = Object.Instantiate(helmsman, copy.transform, false); h.name = "helmsman"; h.SetActive(true);
                h.transform.localPosition = wheelAt + new Vector3(0f, -1f, -.7f); h.transform.localRotation = Quaternion.identity;
            }
            // a lantern on every mast (the user, question 15), its region's (Rules.TraderLantern), at the measured places, unlit; a
            // plan's on its own masts (Rules.LanternsOnMasts)
            int lanternKind = Rules.TraderLantern(plan, hull);
            if (lanterns.TryGetValue(lanternKind, out var lantern) && lantern)
                foreach (var at in lanternAt)
                {
                    var l = Object.Instantiate(lantern, copy.transform, false); l.name = "lantern"; l.SetActive(true);
                    l.transform.localPosition = at - lanternBoxes[lanternKind].center; t.Lanterns++;
                }
            int merged = MergeStatic(copy, t);
            t.RendererOn = copy.GetComponentsInChildren<Renderer>(true).Select(r => r.enabled).ToArray();
            copy.SetActive(false);   // a copy made from it wakes only where it is put
            t.Report = "(" + build + ", from " + boat.name + (boat.gameObject.activeInHierarchy ? ", active" : ", inactive") + "): " + t.Sails.Count + " sails, " + removed + " components removed, " + dropped + " parts dropped, " + colliders + " solid colliders kept, " +
                       t.RendererOn.Length + " renderers (" + merged + " static parts merged: " + mergeWhy + "), wheel at " + wheelAt.ToString("F2") + "; sail ranges " + string.Join(", ", sails.Select(s => s.Kind + " " + s.Min.ToString("F0") + ".." + s.Max.ToString("F0")).ToArray()) +
                       "; " + materials + " materials its own, " + ropes + " mooring parts and " + unbound + " meshes bound outside it taken off, ropes drawn " + ropeLines +
                       " (" + reefAnchor + " reef and anchor ropes and " + ropesOff + " not drawn on it taken off, " + frozenRopes + " cloth ropes as captured taken off), purchase paper " + (paperOff ? "taken off" : "none") +
                       ", player collider " + (forPlayer ? "yes" : "none") + ", lantern " + lanternKind + lanternWhy + "; drawn outside the hull: " + Outside(copy, hull) +
                       "; posed past the limit: " + (pastLimit.Count > 0 ? string.Join(", ", pastLimit.ToArray()) : "none") + (plan != null ? "; parts unlike the stock rig: " + (partsDiffer.Length > 0 ? partsDiffer : "none") : "");
            return t;
        }

        // The hull's static parts drawn as one mesh per material (vt-cost-2: with 9 traders drawn their 720 renderers cost 2.5 ms a
        // frame, shadows off saved nothing and the 477 parts under 3 m 2.65 ms: the count of renderers, not their triangles). Merged:
        // a plain part (its object holds only a mesh, its renderer and colliders: nothing a script reads), drawn, with a readable mesh
        // and a material on each sub-mesh, not mirrored, not under anything that moves or is switched at run time (a sail's posed
        // transform, a sail, cloth, an animator, an LOD group, the furled sail looks, the lanterns, the helmsman). Grouped by
        // material, shadow and probe settings and layer; the parts keep their meshes and colliders, their renderers are removed.
        // Every copy shares the template's merged meshes. Returns the count of parts merged.
        internal static bool MergeParts = true;   // off only for the test's look and cost against the parts unmerged (QA vtcost2nomerge)
        private static string mergeWhy = "";
        private static int MergeStatic(GameObject copy, HullTemplate t)
        {
            var root = copy.transform; mergeWhy = "";
            if (copy.GetComponent<Animator>()) { mergeWhy = "an animator on the root"; return 0; }
            var moving = new HashSet<Transform>(t.Sails.Where(s => s.T).Select(s => s.T));
            foreach (var c in copy.GetComponentsInChildren<Sail>(true)) moving.Add(c.transform);
            foreach (var c in copy.GetComponentsInChildren<Cloth>(true)) moving.Add(c.transform);
            // an animator holding every sail is the model's own (each hull's model root has one: its sails' reef animations, played by
            // ReefEffectAnimUniversal on bones under the sails, which stay apart): its other parts do not move (vtcost2's pose check)
            var sailTs = copy.GetComponentsInChildren<Sail>(true).Select(s => s.transform).ToList();
            foreach (var c in copy.GetComponentsInChildren<Animator>(true))
                if (sailTs.Count == 0 || !sailTs.All(s => s.IsChildOf(c.transform))) moving.Add(c.transform);
            foreach (var c in copy.GetComponentsInChildren<LODGroup>(true)) moving.Add(c.transform);
            // a rope turns both its ends toward each other each frame (RopeEffect.UpdateTension)
            foreach (var c in copy.GetComponentsInChildren<RopeEffect>(true)) { moving.Add(c.transform); if (c.attachment) moving.Add(c.attachment); }
            foreach (var c in copy.GetComponentsInChildren<ReefEffectAnimUniversal>(true)) if (c.furledSail) moving.Add(c.furledSail.transform);
            var culprits = new Dictionary<string, int>();
            bool Moves(Transform x)
            {
                for (; x && x != root; x = x.parent)
                    if (moving.Contains(x) || x.name == "lantern" || x.name == "helmsman")
                    {
                        string why = x.name + (x.GetComponent<Animator>() ? " (animator)" : x.GetComponent<LODGroup>() ? " (LOD group)" : x.GetComponent<Sail>() ? " (sail)" : x.GetComponent<Cloth>() ? " (cloth)" : "");
                        culprits[why] = culprits.TryGetValue(why, out int n) ? n + 1 : 1; return true;
                    }
                return false;
            }
            bool Plain(GameObject g) { foreach (var k in g.GetComponents<Component>()) if (!(k is Transform || k is MeshFilter || k is MeshRenderer || k is Collider)) return false; return true; }
            var toRoot = root.worldToLocalMatrix;
            var groups = new Dictionary<string, List<CombineInstance>>(); var looks = new Dictionary<string, MeshRenderer>(); var parts = new List<MeshRenderer>();
            int off = 0, notPlain = 0, moves = 0, unreadable = 0, odd = 0, mirrored = 0;
            foreach (var r in copy.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.GetType() != typeof(MeshRenderer) || !r.enabled || !ActiveUnder(r.transform, root)) { off++; continue; }
                if (!Plain(r.gameObject)) { notPlain++; continue; }
                if (Moves(r.transform)) { moves++; continue; }
                var mesh = r.GetComponent<MeshFilter>()?.sharedMesh; var mats = r.sharedMaterials;
                if (mesh && !mesh.isReadable) { unreadable++; continue; }
                if (!mesh || mesh.subMeshCount != mats.Length || mats.Any(m => !m)) { odd++; continue; }
                var m4 = toRoot * r.transform.localToWorldMatrix;
                if (m4.determinant <= 0) { mirrored++; continue; }
                for (int s = 0; s < mats.Length; s++)
                {
                    string key = mats[s].GetInstanceID() + "|" + (int)r.shadowCastingMode + "|" + r.receiveShadows + "|" + (int)r.lightProbeUsage + "|" + (int)r.reflectionProbeUsage + "|" + r.gameObject.layer;
                    if (!groups.TryGetValue(key, out var list)) { groups[key] = list = new List<CombineInstance>(); looks[key] = r; }
                    list.Add(new CombineInstance { mesh = mesh, subMeshIndex = s, transform = m4 });
                }
                parts.Add(r);
            }
            // (the skinned parts, ropes, are left skinned: every one has a bone on a sail's moving rig, vt-cost-6)
            mergeWhy = parts.Count + " mergeable; left apart: " + off + " off, " + notPlain + " with scripts or other parts, " + moves + " moving or switched, " + unreadable +
                       " meshes not readable, " + odd + " without a mesh or a material per sub-mesh, " + mirrored + " mirrored; moving under " +
                       string.Join(", ", culprits.OrderByDescending(k => k.Value).Take(6).Select(k => k.Key + " x" + k.Value).ToArray());
            if (!MergeParts || parts.Count < 2) return 0;
            foreach (var g in groups)
            {
                var look = looks[g.Key]; var mat = look.sharedMaterials[g.Value[0].subMeshIndex];
                var mesh = new Mesh { name = "merged " + mat.name };
                if (g.Value.Sum(c => (long)c.mesh.vertexCount) > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.CombineMeshes(g.Value.ToArray(), true, true);
                var go = new GameObject(mesh.name) { layer = look.gameObject.layer };
                go.transform.SetParent(root, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
                mr.shadowCastingMode = look.shadowCastingMode; mr.receiveShadows = look.receiveShadows; mr.lightProbeUsage = look.lightProbeUsage; mr.reflectionProbeUsage = look.reflectionProbeUsage;
            }
            t.MergedPaths = parts.Select(r => IndexPath(r.transform, root)).ToList();   // for the test's check that none of them moves
            foreach (var r in parts) Object.DestroyImmediate(r);
            return parts.Count;
        }
        // Its own copy of every material (cloned once each, so parts that shared one still share it), the hull's dirt a clean texture
        // (CleanableObject.Start's own fallback) and the damage overlay as on an undamaged hull (HullDamageTexture.UpdateMaterial at
        // no damage): the game's ship changes its own materials in play (its damage, its dirt, a sail's colour), and a copy that
        // shared them showed them (the user's feedback on 0.10.4, point 2).
        private static int OwnMaterials(GameObject copy)
        {
            var own = new Dictionary<Material, Material>();
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials; bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (!mats[i]) continue;
                    if (!own.TryGetValue(mats[i], out var m)) { m = new Material(mats[i]); m.name = mats[i].name + " (trader)"; own[mats[i]] = m; }
                    mats[i] = m; changed = true;
                }
                if (changed) r.sharedMaterials = mats;
            }
            foreach (var c in copy.GetComponentsInChildren<CleanableObject>(true))
            {
                var r = c.GetComponent<Renderer>(); if (!r || !r.sharedMaterial) continue;
                r.sharedMaterial.mainTexture = cleanDirt;
            }
            foreach (var d in copy.GetComponentsInChildren<HullDamageTexture>(true))
            {
                var r = d.GetComponent<Renderer>(); if (!r || !r.sharedMaterial || !RefsDirectory.instance) continue;
                var colour = RefsDirectory.instance.caulkColor;
                colour.a = (d.alphaCurve != null ? d.alphaCurve.Evaluate(0f) : 0f) * .75f;
                r.sharedMaterial.SetColor("_Color", colour);
            }
            return own.Count;
        }
        // The mooring set (rope ends, hanging ropes, coils) and any rope end or length adjuster elsewhere on the ship: a trader is
        // never moored (the user's feedback on 0.10.4, point 5).
        private static int DropMooring(GameObject copy)
        {
            var parts = new HashSet<GameObject>();
            // the mooring set whole, from the ship's own child that holds it ("mooring set prefb", vt-9)
            foreach (var s in copy.GetComponentsInChildren<MooringSet>(true))
            {
                var top = s.transform; while (top.parent && top.parent != copy.transform) top = top.parent;
                parts.Add(top.parent == copy.transform && top.name.IndexOf("mooring", StringComparison.OrdinalIgnoreCase) >= 0 ? top.gameObject : s.gameObject);
            }
            foreach (var r in copy.GetComponentsInChildren<PickupableBoatMooringRope>(true)) parts.Add(r.gameObject);
            foreach (var a in copy.GetComponentsInChildren<MooringRopeLengthAdjuster>(true)) parts.Add(a.gameObject);
            // the hanging ropes lie outside the set on the copy (the Dhow's four mooring_rope_hanger, vt-10): by the game's own names
            foreach (var t in copy.GetComponentsInChildren<Transform>(true)) if (t.name.StartsWith("mooring_rope")) parts.Add(t.gameObject);
            int n = 0;
            foreach (var g in parts) if (g && g != copy && !parts.Any(p => p && p != g && g.transform.IsChildOf(p.transform))) { Object.DestroyImmediate(g); n++; }
            return n;
        }
        // The ship's purchase sign (PurchasableBoat.purchaseUI, "purchase boat UI": its paper, button and text), by its place in the
        // ship (point 6).
        private static bool DropPaper(GameObject copy, Transform original, GameObject paper)
        {
            var signs = new List<Transform>();
            if (paper && paper.transform.IsChildOf(original)) { var t = FindIndex(copy.transform, IndexPath(paper.transform, original)); if (t) signs.Add(t); }
            // and every other sign by the game's name (the Sanbuq carries two, vt-11)
            signs.AddRange(copy.GetComponentsInChildren<Transform>(true).Where(x => x.name.StartsWith("purchase boat UI")));
            int n = 0;
            foreach (var t in signs.Distinct().ToList()) if (t && t != copy.transform) { Object.DestroyImmediate(t.gameObject); n++; }
            return n > 0;
        }
        // A mesh bound to bones outside the copy draws where those bones are, at the game's ship (a rope end moored to a dock is
        // moved under the dock's spring, PickupableBoatMooringRope): taken off (points 5 and 7).
        private static int DropUnbound(GameObject copy)
        {
            int n = 0;
            foreach (var s in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                bool outside = s.rootBone && !s.rootBone.IsChildOf(copy.transform);
                if (!outside && s.bones != null) foreach (var b in s.bones) if (b && !b.IsChildOf(copy.transform)) { outside = true; break; }
                if (outside) { Object.DestroyImmediate(s); n++; }
            }
            return n;
        }
        // The game's cloth ropes (its "cloth ropes" setting: RopeEffect.Start makes one for each rope, under the rope's parent, and
        // places its bones along the rope every frame) as the game's ship had them when the template was taken: frozen there, they
        // stood where its sail lay, not where the copy's is posed (the user, 2026-10-20: "stock dhow trader still have line is not
        // correct position"; probe dhow_ropes: on the small Dhow a rope from the bow over the mast's top to the yard's resting end).
        // A copy's own ropes make their own, as the game does.
        private static int DropClothRopes(GameObject copy)
        {
            int n = 0;
            foreach (var c in copy.GetComponentsInChildren<ClothRope>(true).ToList()) if (c && c.gameObject != copy) { Object.DestroyImmediate(c.gameObject); n++; }
            return n;
        }
        // The reef (halyard) and anchor ropes by the game's own parts: the rope of each reef winch's controller
        // (RopeControllerSailReef, and each sail's SailConnections.reefController), the ropes a sail's reef line runs on from the
        // mast (SailConnections.mastReefAttachment, mastReefAttExtension), the anchor winch's rope and its rope to the anchor
        // (RopeControllerAnchor and its anchorRope). Not drawn on a trader (the user, 2026-10-20: "don't draw reef/halyard rope
        // and anchor rope, and draw the rest").
        private static readonly System.Reflection.FieldInfo anchorRopeField = AccessTools.Field(typeof(RopeControllerAnchor), "anchorRope");
        private static HashSet<RopeEffect> ReefAndAnchorRopes(GameObject copy)
        {
            var set = new HashSet<RopeEffect>();
            void Add(Component c) { var r = c ? c.GetComponent<RopeEffect>() : null; if (r) set.Add(r); }
            foreach (var c in copy.GetComponentsInChildren<RopeControllerSailReef>(true)) Add(c);
            foreach (var c in copy.GetComponentsInChildren<RopeControllerAnchor>(true)) { Add(c); if (anchorRopeField?.GetValue(c) is RopeEffect a && a) set.Add(a); }
            foreach (var s in copy.GetComponentsInChildren<SailConnections>(true)) { Add(s.reefController); Add(s.mastReefAttachment); Add(s.mastReefAttExtension); }
            return set;
        }
        // The ship's ropes kept as the game draws them (RopeEffect: a line, or a cloth rope under the setting, from its place to its
        // attachment), so they follow the copy's posed sails; the sheets among them (the user, 2026-10-20: "all sail don't have
        // sheet line, either jibs or main sails. it should have it"). Taken off: the reef and anchor ropes (ReefAndAnchorRopes),
        // and a rope not drawn on the copy (switched off, or ending on a part not drawn or outside it, such as the ship's own
        // unused sail). Drawn taut (the game's sheet pulled in, RopeControllerSailAngle at no slack: length 1 of 1, a straight
        // line): the controllers that set the slack are gone. Drawn by the game while the copy is shown (TraderHulls.ShowRopes). Returns the
        // ropes kept.
        private static int TrimRopes(GameObject copy, HashSet<RopeEffect> notDrawn, out int off, out int reefAnchor)
        {
            off = 0; reefAnchor = 0; int kept = 0; var root = copy.transform;
            foreach (var r in copy.GetComponentsInChildren<RopeEffect>(true))
            {
                var a = r.attachment;
                if (notDrawn.Contains(r))
                {
                    var line = r.GetComponent<LineRenderer>();
                    Object.DestroyImmediate(r); if (line) Object.DestroyImmediate(line);
                    reefAnchor++; continue;
                }
                if (!ActiveUnder(r.transform, root) || !a || !a.IsChildOf(root) || !ActiveUnder(a, root))
                {
                    var line = r.GetComponent<LineRenderer>();
                    Object.DestroyImmediate(r); if (line) Object.DestroyImmediate(line);
                    off++; continue;
                }
                r.currentRopeLength = 1f; r.totalRopeLength = 1f;
                kept++;
            }
            // a line no rope draws would stay where the game's ship had it (its points are in the world)
            foreach (var l in copy.GetComponentsInChildren<LineRenderer>(true)) if (!l.GetComponent<RopeEffect>()) { Object.DestroyImmediate(l); off++; }
            return kept;
        }
        // The parts drawn more than 3 m outside the hull's box (its length and beam round its root), sails apart: for the log, so
        // a stray part shows in the first look (point 7).
        private static string Outside(GameObject copy, int hull)
        {
            var size = HullData.Size(hull); var sails = copy.GetComponentsInChildren<Sail>(true).Select(s => s.transform).ToArray();
            var names = new List<string>();
            foreach (var mf in copy.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh || sails.Any(s => mf.transform.IsChildOf(s))) continue;
                var b = mf.sharedMesh.bounds;
                for (int k = 0; k < 8; k++)
                {
                    var p = copy.transform.InverseTransformPoint(mf.transform.TransformPoint(b.center + Vector3.Scale(b.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1))));
                    if (Mathf.Abs(p.x) > size.y / 2 + 3f || Mathf.Abs(p.z) > size.x / 2 + 3f) { names.Add(mf.name); break; }
                }
            }
            return names.Count == 0 ? "none" : names.Count + " (" + string.Join(", ", names.Take(8).ToArray()) + ")";
        }

        // Every behaviour, trigger, joint, body, sound, light and line removed but what draws the hull and its sails (the probes'
        // rule, vt_hulls run 1).
        private static int Strip(GameObject root)
        {
            int destroyed = 0;
            for (int pass = 0; pass < 6; pass++)
            {
                int now = 0;
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                {
                    if (!c || c is Transform || Wanted(c, root) || Required(c)) continue;
                    try { Object.DestroyImmediate(c); if (!c) now++; } catch { }
                }
                destroyed += now; if (now == 0) break;
            }
            return destroyed;
        }
        // Unity refuses to remove a component that another one on its object requires ([RequireComponent]) and logs an error for
        // each try (vt-3: about 1,700 of them in the log): such a component waits for a later pass, after what requires it is
        // gone. The same components end up removed as before.
        private static readonly Dictionary<Type, Type[]> requires = new Dictionary<Type, Type[]>();
        private static Type[] Requires(Type t)
        {
            if (requires.TryGetValue(t, out var r)) return r;
            var list = new List<Type>();
            foreach (var a in t.GetCustomAttributes(typeof(RequireComponent), true))
            {
                var q = (RequireComponent)a;
                if (q.m_Type0 != null) list.Add(q.m_Type0); if (q.m_Type1 != null) list.Add(q.m_Type1); if (q.m_Type2 != null) list.Add(q.m_Type2);
            }
            return requires[t] = list.ToArray();
        }
        private static readonly List<Component> siblings = new List<Component>();
        private static bool Required(Component c)
        {
            var type = c.GetType();
            c.GetComponents(siblings);
            foreach (var other in siblings)
            {
                if (!other || other == c) continue;
                foreach (var need in Requires(other.GetType())) if (need.IsAssignableFrom(type)) return true;
            }
            return false;
        }
        private static bool Wanted(Component c, GameObject root)
        {
            string n = c.GetType().Name;
            if (c is Collider col) return !col.isTrigger;
            if (c is Rigidbody) return c.gameObject == root || c.GetComponent<Sail>();
            if (c is HingeJoint) return c.GetComponent<Sail>();
            if (c is Joint) return false;
            if (Keep.Contains(n)) return true;
            return c.GetType().Assembly.GetName().Name == "Crest" && n.StartsWith("Register");
        }
        // The parts not installed (inactive) dropped, but inside a sail (its furled look may be switched on later); and any part
        // drawn more than 200 m across (a Shipyard Expansion part on the Jong is drawn tens of km off, vt_measure run 2).
        private static int DropParts(GameObject root)
        {
            var sailRoots = root.GetComponentsInChildren<Sail>(true).Select(s => s.transform).ToArray();
            var off = root.GetComponentsInChildren<Transform>(true).Where(t => t != root.transform && !t.gameObject.activeSelf && ActiveUnder(t.parent, root.transform) &&
                !sailRoots.Any(s => t.IsChildOf(s))).ToList();
            foreach (var t in off) if (t) Object.DestroyImmediate(t.gameObject);
            int huge = 0;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf || !mf.sharedMesh) continue;
                var b = mf.sharedMesh.bounds; var s = mf.transform.lossyScale;
                var size = Vector3.Scale(b.size, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
                if (size.magnitude > 200f || (root.transform.InverseTransformPoint(mf.transform.TransformPoint(b.center))).magnitude > 200f) { Object.DestroyImmediate(mf.gameObject); huge++; }
            }
            return off.Count + huge;
        }
        // Only the hull's main solid parts collide (the player's ships meet a wall; other ships are made to pass through, which
        // takes a call per pair): the root's capsule and the 15 largest other colliders on the root body; none on the sails.
        private static int TrimColliders(GameObject root)
        {
            var body = root.GetComponent<Rigidbody>();
            var all = root.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger).ToList();
            var keep = new HashSet<Collider>();
            var capsule = root.GetComponents<CapsuleCollider>().FirstOrDefault(c => !c.isTrigger); if (capsule) keep.Add(capsule);
            // the masts' capsules the sails' cloth collides with stay (a cloth with a destroyed collider in its list)
            foreach (var cl in root.GetComponentsInChildren<Cloth>(true))
            {
                if (cl.capsuleColliders != null) foreach (var c in cl.capsuleColliders) if (c) keep.Add(c);
                if (cl.sphereColliders != null) foreach (var p in cl.sphereColliders) { if (p.first) keep.Add(p.first); if (p.second) keep.Add(p.second); }
            }
            foreach (var c in all.Where(c => !keep.Contains(c) && !Parent<Sail>(c.transform, root.transform) && Parent<Rigidbody>(c.transform, root.transform) == body)
                                 .OrderByDescending(Volume).Take(15)) keep.Add(c);
            foreach (var c in all) if (!keep.Contains(c)) Object.DestroyImmediate(c);
            return keep.Count;
        }
        // The nearest component of a kind on t or its parents up to root (GetComponentInParent skips inactive objects here).
        private static T Parent<T>(Transform t, Transform root) where T : Component
        {
            for (var c = t; c; c = c.parent) { var k = c.GetComponent<T>(); if (k) return k; if (c == root) break; }
            return null;
        }
        private static float Volume(Collider c)
        {
            var s = c.transform.lossyScale; float k = Mathf.Abs(s.x * s.y * s.z);
            if (c is BoxCollider b) return b.size.x * b.size.y * b.size.z * k;
            if (c is CapsuleCollider cp) return Mathf.PI * cp.radius * cp.radius * cp.height * k;
            if (c is SphereCollider sp) return 4.2f * sp.radius * sp.radius * sp.radius * k;
            if (c is MeshCollider m && m.sharedMesh) { var z = m.sharedMesh.bounds.size; return z.x * z.y * z.z * k; }
            return 0;
        }

        // The helmsman from the game's own NPC boats (the user, question 9: static), in his pose, dressed in an outfit of the game's
        // NPCs (HELMSMEN_CHRONOS_DRAFT.md). Every person in the game is one model (Modular NPC: its clothes under Modular_Characters,
        // ~720 parts, about 15 switched on, and its bones under Root); an outfit is which parts are on and their material, set by hand
        // in its scene. The outfits of every Modular NPC of the main scene (the port dudes and the NPC boats' sailors, loaded at sea
        // too) are read once and kept in memory only (the user: "This don't need to be save"). The template keeps only the model's
        // own parts (another child, such as Talkative NPCs' speech bubble or the game's empty mesh combiner, goes) and of its clothes
        // those some outfit wears, switched off; Dress switches a copy's on.
        internal sealed class Outfit { internal int Kind; internal int[] Parts; internal Material[][] Materials; internal Mesh[] Meshes; internal string From; }
        private static readonly List<Outfit> outfits = new List<Outfit>();
        private static string[] helmParts = new string[0];   // the template's clothes, as paths under it
        private static Mesh[] helmMeshes = new Mesh[0];      // and their own meshes
        private static string outfitReport = "no outfits read";
        internal static IList<Outfit> Outfits => outfits;
        private static Transform NpcRoot(Transform t) { for (var c = t; c; c = c.parent) if (c.Find("Modular_Characters")) return c; return null; }
        private static string PathUnder(Transform t, Transform root) { var l = new List<string>(); for (var c = t; c && c != root; c = c.parent) l.Insert(0, c.name); return string.Join("/", l.ToArray()); }
        private static IEnumerable<SkinnedMeshRenderer> Clothes(Transform root) { var mc = root.Find("Modular_Characters"); return mc ? mc.GetComponentsInChildren<SkinnedMeshRenderer>(true) : Enumerable.Empty<SkinnedMeshRenderer>(); }
        private static GameObject HelmsmanTemplate()
        {
            NPCAnimations source = null;
            foreach (var b in Resources.FindObjectsOfTypeAll<NPCBoatController>())
            {
                if (!b || !b.gameObject.scene.IsValid()) continue;
                source = b.GetComponentsInChildren<NPCAnimations>(true).FirstOrDefault(); if (source) break;
            }
            if (!source) return null;
            outfits.Clear(); helmParts = new string[0];
            Transform rootT = NpcRoot(source.transform);
            if (!rootT) { rootT = source.transform; for (var c = source.transform; c; c = c.parent) if (c.name == "Modular NPC") { rootT = c; break; } }
            // the outfits: each Modular NPC's worn clothes (on under it, with a mesh) and their materials, alike ones counted once
            var read = new List<KeyValuePair<string[], Material[][]>>(); var froms = new List<string>(); var meshes = new List<Mesh[]>(); var boneCounts = new List<int[]>(); var keys = new HashSet<string>(); int npcs = 0;
            if (NpcRoot(source.transform))
                foreach (var npc in Resources.FindObjectsOfTypeAll<NPCAnimations>())
                {
                    if (!npc || npc.gameObject.scene != rootT.gameObject.scene) continue;
                    var root = NpcRoot(npc.transform); if (!root) continue;
                    var worn = Clothes(root).Where(r => r.sharedMesh && ActiveUnder(r.transform, root)).Select(r => new KeyValuePair<string, SkinnedMeshRenderer>(PathUnder(r.transform, root), r)).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
                    if (worn.Count == 0) continue;
                    npcs++;
                    string key = string.Join("|", worn.Select(p => p.Key + "=" + string.Join(",", p.Value.sharedMaterials.Select(m => m ? m.GetInstanceID().ToString() : "-").ToArray())).ToArray());
                    if (!keys.Add(key)) continue;
                    read.Add(new KeyValuePair<string[], Material[][]>(worn.Select(p => p.Key).ToArray(), worn.Select(p => p.Value.sharedMaterials).ToArray()));
                    meshes.Add(worn.Select(p => p.Value.sharedMesh).ToArray()); boneCounts.Add(worn.Select(p => p.Value.bones.Length).ToArray());
                    froms.Add(root.parent ? root.parent.name : root.name);
                }
            var go = Object.Instantiate(rootT.gameObject, holder.transform, false); go.name = "helmsman template";
            bool modular = go.transform.Find("Modular_Characters");
            if (modular) foreach (var child in go.transform.Cast<Transform>().ToList()) if (child.name != "Modular_Characters" && child.name != "Root") Object.DestroyImmediate(child.gameObject);
            foreach (var c in go.GetComponentsInChildren<Component>(true))
                if (c && !(c is Transform) && !(c is Renderer) && !(c is MeshFilter) && !Required(c)) { try { Object.DestroyImmediate(c); } catch { } }
            if (modular && read.Count > 0)
            {
                // its clothes: those some outfit wears kept (switched off, drawn when switched on), the rest dropped
                var wanted = new HashSet<string>(read.SelectMany(o => o.Key));
                var mine = new Dictionary<string, SkinnedMeshRenderer>(); int twins = 0;
                foreach (var r in Clothes(go.transform)) { string p = PathUnder(r.transform, go.transform); if (mine.ContainsKey(p)) twins++; else mine[p] = r; }
                foreach (var p in mine) if (!wanted.Contains(p.Key) && p.Value && p.Value.GetComponentsInChildren<SkinnedMeshRenderer>(true).All(r => r == p.Value)) Object.DestroyImmediate(p.Value.gameObject);
                helmParts = wanted.Where(mine.ContainsKey).OrderBy(p => p, StringComparer.Ordinal).ToArray();
                var index = new Dictionary<string, int>(); for (int i = 0; i < helmParts.Length; i++) index[helmParts[i]] = i;
                int unlike = 0, missing = 0, unfit = 0;
                for (int o = 0; o < read.Count; o++)
                {
                    var paths = read[o].Key;
                    if (paths.Any(p => !index.ContainsKey(p))) { missing++; continue; }
                    // a part whose mesh differs from the helmsman's at that place wears the NPC's own (the same rig: its bone count alike)
                    var own = new Mesh[paths.Length];
                    for (int k = 0; k < paths.Length; k++)
                        if (meshes[o][k] != mine[paths[k]].sharedMesh) { unlike++; if (boneCounts[o][k] == mine[paths[k]].bones.Length) own[k] = meshes[o][k]; else unfit++; }
                    var mats = read[o].Value;
                    outfits.Add(new Outfit { Parts = paths.Select(p => index[p]).ToArray(), Materials = mats, Meshes = own, From = froms[o],
                                             Kind = Rules.OutfitKind(mats.Select(m => m.Length > 0 && m[0] ? m[0].name : null)) });
                }
                helmMeshes = helmParts.Select(p => mine[p].sharedMesh).ToArray();
                foreach (var p in helmParts)
                {
                    var r = mine[p];
                    for (var c = r.transform.parent; c && c != go.transform; c = c.parent) if (!c.gameObject.activeSelf) c.gameObject.SetActive(true);
                    r.gameObject.SetActive(false); r.enabled = true;
                }
                outfitReport = outfits.Count + " outfits from " + npcs + " NPCs (" + string.Join(", ", Enumerable.Range(-1, 7).Select(k => (k < 0 ? "unknown" : Rules.OutfitNames[k]) + " " + outfits.Count(x => x.Kind == k)).ToArray()) +
                               "), " + helmParts.Length + " of " + mine.Count + " parts kept" + (missing > 0 ? ", " + missing + " outfits with parts the helmsman lacks dropped" : "") + ", meshes unlike the helmsman's " + unlike + " (worn as the NPC's own but " + unfit + " of another rig)" + (twins > 0 ? ", " + twins + " parts named alike" : "");
            }
            else
            {
                // no outfits: as before, the sailor's own clothes, the others dropped
                foreach (var t in go.GetComponentsInChildren<Transform>(true).Where(t => t != go.transform && !t.gameObject.activeSelf && ActiveUnder(t.parent, go.transform)).ToList())
                    if (t) Object.DestroyImmediate(t.gameObject);
                outfitReport = "no outfits read (his own clothes)";
            }
            foreach (var s in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) s.updateWhenOffscreen = true;
            go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity;
            return go;
        }
        // A copy's helmsman put in an outfit (its parts on, their materials).
        internal static void Dress(TraderCopy c, int outfit)
        {
            if (c.HelmParts == null || outfit < 0 || outfit >= outfits.Count) return;
            var o = outfits[outfit]; var on = new bool[c.HelmParts.Length];
            foreach (int k in o.Parts) on[k] = true;
            for (int i = 0; i < c.HelmParts.Length; i++) if (c.HelmParts[i] && c.HelmParts[i].activeSelf != on[i]) c.HelmParts[i].SetActive(on[i]);
            for (int k = 0; k < o.Parts.Length; k++)
            {
                var r = c.HelmRenderers[o.Parts[k]]; if (!r) continue;
                r.sharedMaterials = o.Materials[k];
                var mesh = o.Meshes != null && o.Meshes[k] ? o.Meshes[k] : helmMeshes[o.Parts[k]];
                if (r is SkinnedMeshRenderer s && s.sharedMesh != mesh) s.sharedMesh = mesh;
            }
            c.Outfit = outfit;
        }

        // A prefab's looks only (meshes, renderers, lights, particles), as an inactive template.
        private static GameObject Visual(GameObject prefab)
        {
            var go = Object.Instantiate(prefab, holder.transform, false);
            for (int pass = 0; pass < 6; pass++)
            {
                int now = 0;
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                {
                    if (!c || c is Transform || c is MeshFilter || c is Renderer || c is Light || c is ParticleSystem || c is LODGroup || Required(c)) continue;
                    try { Object.DestroyImmediate(c); if (!c) now++; } catch { }
                }
                if (now == 0) break;
            }
            go.transform.localPosition = Vector3.zero;
            return go;
        }
        // An item's drawn box in its own frame, its own turn and scale applied but not its position (vt_dress' ItemBounds).
        private static Bounds ItemBounds(GameObject g)
        {
            var m = Matrix4x4.TRS(Vector3.zero, g.transform.localRotation, g.transform.localScale) * g.transform.worldToLocalMatrix;
            var b = new Bounds(); bool any = false;
            foreach (var mf in g.GetComponentsInChildren<MeshFilter>(true))
            {
                var r = mf.GetComponent<Renderer>(); if (!mf.sharedMesh || !r || !r.enabled || !ActiveUnder(mf.transform, g.transform)) continue;
                var mb = mf.sharedMesh.bounds; var mm = m * mf.transform.localToWorldMatrix;
                for (int k = 0; k < 8; k++)
                {
                    var p = mm.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1)));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            return b;
        }
        private static Bounds Turned(Bounds b, Quaternion r)
        {
            var o = new Bounds(); bool any = false;
            for (int k = 0; k < 8; k++)
            {
                var p = r * (b.center + Vector3.Scale(b.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1)));
                if (!any) { o = new Bounds(p, Vector3.zero); any = true; } else o.Encapsulate(p);
            }
            return o;
        }

        // One copy a call until each hull has its pool (made at the start: the user, question 7); then the pool is done (no count
        // made each frame). A hull whose copy fails is given up alone: the others' pools are still made (its copies made before
        // stay counted; the review of 2026-10-19: the counts then stopped the build short for the other hulls).
        private static bool poolDone;
        internal static bool PoolDone => poolDone;
        internal static bool HasPool => pool.Count > 0;
        // One copy a call, the stock rigs first (by hull), then the plans; not before every plan's template is taken.
        // a copy made is made inactive at the next call (its scripts' Start runs the frame after it is made, as before)
        private static readonly List<TraderCopy> made = new List<TraderCopy>();
        internal static bool BuildStep()
        {
            if (!PlansDone) return false;
            foreach (var c in made) Sleep(c);
            made.Clear();
            foreach (var t in templates.Values.OrderBy(x => x.Plan != null).ThenBy(x => x.Hull).ThenBy(x => x.Key, StringComparer.Ordinal).ToList())
            {
                if (!pool.TryGetValue(t.Key, out var list)) pool[t.Key] = list = new List<TraderCopy>();
                if (list.Count >= Rules.TraderPool(t.Key)) continue;
                try { var c = MakeCopy(t); list.Add(c); made.Add(c); }
                catch (Exception e) { Plugin.Log?.LogError("Visible traders: a copy of " + t.Key + " failed: " + e); templates.Remove(t.Key); }
                return true;
            }
            poolDone = true;
            return false;
        }
        private static TraderCopy MakeCopy(HullTemplate t)
        {
            var go = Object.Instantiate(t.Root, Refs.shiftingWorld, false);
            go.name = t.Root.name;
            var c = new TraderCopy { Template = t, Go = go, Body = go.GetComponent<Rigidbody>(), CargoGood = -1 };
            c.Renderers = go.GetComponentsInChildren<Renderer>(true);
            c.Cloths = go.GetComponentsInChildren<Cloth>(true);
            c.ClothVelocity = c.Cloths.Select(x => x.worldVelocityScale).ToArray(); c.ClothAcceleration = c.Cloths.Select(x => x.worldAccelerationScale).ToArray();
            c.Lights = go.GetComponentsInChildren<Light>(true);
            c.Flames = go.GetComponentsInChildren<ParticleSystem>(true);
            c.Reefs = go.GetComponentsInChildren<ReefEffectAnimUniversal>(true);
            // the animators the reef effects play (ReefEffectAnimUniversal.Start: its override, else its parent's)
            c.ReefAnimators = c.Reefs.Select(r => r ? (r.overrideAnimator ? r.overrideAnimator : r.transform.parent ? r.transform.parent.GetComponent<Animator>() : null) : null)
                                     .Where(a => a).Distinct().ToArray();
            // (a LINQ copy of GetComponentsInChildren<Behaviour> throws ArrayTypeMismatchException in the game's Mono: vt-1)
            var toggled = new List<Behaviour>();
            foreach (var comp in go.GetComponentsInChildren<Component>(true)) if (comp is Behaviour b && !(b is Sail) && !(b is SailConnections) && !(b is Light) && !(b is Animator)) toggled.Add(b);
            c.Toggled = toggled.ToArray();
            foreach (var b in c.Toggled) { if (b is WindCloth w) copyWinds.Add(w); else if (b is ReefEffectAnimUniversal r) copyReefs.Add(r); }
            c.Colliders = go.GetComponentsInChildren<Collider>(true);
            c.Ropes = go.GetComponentsInChildren<RopeEffect>(true);
            c.SailT = t.Sails.Select(s => FindIndex(go.transform, s.Path)).ToArray();
            c.Sails = c.SailT.Select(s => s ? s.GetComponent<Sail>() : null).ToArray();
            var helm = go.transform.Find("helmsman");
            if (helm && helmParts.Length > 0) { c.HelmParts = helmParts.Select(p => { var x = helm.Find(p); return x ? x.gameObject : null; }).ToArray(); c.HelmRenderers = c.HelmParts.Select(g => g ? g.GetComponent<Renderer>() : null).ToArray(); }
            if (c.Body) bodies.Add(c.Body);
            Park(c);
            go.SetActive(true);
            Hide(c);
            return c;
        }
        // A hidden copy lies far under the sea where the player is (its colliders meet nothing there).
        internal static void Park(TraderCopy c)
        {
            var p = Refs.observerMirror ? Refs.observerMirror.transform.position : Vector3.zero;
            var at = new Vector3(p.x, -2000f, p.z);
            c.Go.transform.SetPositionAndRotation(at, Quaternion.identity);
            if (c.Body) { c.Body.position = at; c.Body.rotation = Quaternion.identity; }
        }
        internal static void Hide(TraderCopy c)
        {
            foreach (var r in c.Renderers) if (r) r.enabled = false;
            foreach (var b in c.Toggled) if (b) b.enabled = false;
            foreach (var a in c.ReefAnimators) if (a) a.cullingMode = AnimatorCullingMode.CullCompletely;
            // a cloth switched off (the test's KeepClothsOn) goes back on one a frame (EnableQueued), not all at once
            foreach (var cl in c.Cloths) if (cl) { if (ClothsKeptOn && !cl.enabled) QueueCloth(cl); else cl.enabled = ClothsKeptOn; }
            // its ropes stopped with its behaviours; the cloth ropes they made (under the copy, after it was made) hidden too
            if (c.Ropes.Length > 0) foreach (var cr in c.Go.GetComponentsInChildren<ClothRope>(true)) cr.gameObject.SetActive(false);
            SetLit(c, false); c.Shown = false;
        }

        // ---- Its ropes: drawn by the game ----
        // Each kept rope's RopeEffect runs while the copy is shown (one of its Toggled behaviours), as on any ship (the user,
        // 2026-10-20: "just use vanilla rope effect, cancel the freezing change, it will be less buggy and cleaner in code"): a
        // line, or a cloth rope under the game's setting, nothing beyond its 250 m. (From vt-ropes-5 to -10 the ropes were drawn
        // only while the sails moved, then pinned: pinned lines swung off their ropes, EconomyOverhaul_FAILED.md.) On showing, its
        // distance check runs at once: the copy comes from under the sea, where the check found it out of range.
        private static readonly System.Reflection.FieldInfo ropeDistanceTimer = AccessTools.Field(typeof(RopeEffect), "distanceCheckTimer");
        private static void ShowRopes(TraderCopy c) { foreach (var r in c.Ropes) if (r) ropeDistanceTimer?.SetValue(r, 0f); }

        // A hidden copy is inactive with its cloth off (the user, 2026-10-22: "keep hidden copies inactive, switch the copy on
        // without its cloth when drawn, then its cloth one a frame"): active and hidden, the 47 copies cost 1.2-1.5 ms every frame
        // with none drawn (vtc6-1); switched on without cloth 0.55-1.43 ms, then a cloth a showing step (one large cloth a trader
        // 7.7-10.4 ms, vtc7-1). Was: cloth left on (the user's "(B) Leave cloth on" of 2026-10-18, from vt-5). On only for the
        // test's measure of the other way (KeepClothsOn).
        internal static bool ClothsKeptOn { get; private set; }
        internal static void KeepClothsOn(bool on)
        {
            ClothsKeptOn = on;
            foreach (var c in Copies) if (c.Go && !c.Shown) foreach (var cl in c.Cloths) if (cl) cl.enabled = on;
        }
        // A hidden copy's cloth switched off (only by the test's measure, KeepClothsOn) goes back on one a frame, each told to forget
        // the moves meanwhile.
        private static readonly List<Cloth> clothQueue = new List<Cloth>();
        private static void QueueCloth(Cloth cl) { if (!clothQueue.Contains(cl)) clothQueue.Add(cl); }
        // The reef animators of a drawn trader whose sails have lain furled 1 s: culled (they hold the furled pose they last drew),
        // always animating again as it sets out (junk-cargo-5: culled, they never left the pose they were copied in).
        // Its cloth stays on (2026-10-19, vt-22 to vt-24's FrameParts: switching a parked cloth back on took about 10 ms a cloth,
        // as a trader set out and when its copy went back to the pool, 7 such frames in a row; parked, it saved 0.98 ms a frame
        // with 9 drawn: the user's choice for the hidden copies, "(B) Leave cloth on", for the same hitches).
        internal static void ParkSails(TraderCopy c) { foreach (var a in c.ReefAnimators) if (a) a.cullingMode = AnimatorCullingMode.CullCompletely; }
        internal static void UnparkSails(TraderCopy c) { foreach (var a in c.ReefAnimators) if (a) a.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
        internal static void EnableQueued()
        {
            while (clothQueue.Count > 0)
            {
                var cl = clothQueue[0]; clothQueue.RemoveAt(0);
                if (!cl || cl.enabled) continue;
                cl.enabled = true; cl.ClearTransformMotion(); return;
            }
        }
        internal static int ClothQueued => clothQueue.Count;
        // Shown a step a frame (vt-2: all at once took 12-16 ms in one frame; vt-3: all its cloth in one step 11.7 ms): its
        // behaviours (the sails' reef effects and cloth, Crest's inputs), then each cloth on its own while cloth is not kept on,
        // then what draws it.
        private static int ClothSteps(TraderCopy c) => ClothsKeptOn ? 0 : c.Cloths.Length;
        internal static int ShowSteps(TraderCopy c) => 2 + ClothSteps(c);
        // The kind of a step, for the timings: 0 the behaviours, 1 a cloth, 2 what draws it.
        internal static int StepKind(TraderCopy c, int step) => step == 0 ? 0 : step <= ClothSteps(c) ? 1 : 2;
        // One cloth switched on a frame over every trader drawn (vts-2: one a trader's step let two traders' large cloths, 7.7 and
        // 10.5 ms, fall in one frame of 34.3 ms; a copy's large cloth is not at the same step on every hull or plan): a cloth step
        // waits for the next frame when another cloth went on in this one.
        // (An extra frame without a cloth after a large one was measured and dropped: no difference, vtg-3, VISIBLE_TRADERS.md.)
        private static int clothFrame = -1;
        internal static bool MayStep(TraderCopy c, int step) => StepKind(c, step) != 1 || clothFrame != Time.frameCount;
        internal static void ShowStep(TraderCopy c, int step)
        {
            if (StepKind(c, step) == 1) clothFrame = Time.frameCount;
            if (step == 0)
            {
                // switched on (its cloth off: a cloth a step below); a hidden copy is inactive (Sleep)
                if (!c.Go.activeSelf) c.Go.SetActive(true);
                // The game's cloth resets never run on a copy (WindClothResetPatch, ReefClothResetPatch): each cloth told to
                // forget the copy's move from where it lay hidden instead.
                foreach (var cl in c.Cloths) if (cl && cl.enabled) cl.ClearTransformMotion();
                foreach (var b in c.Toggled) if (b) b.enabled = true;
                ShowRopes(c);
                return;
            }
            if (step <= ClothSteps(c)) { var cl = c.Cloths[step - 1]; if (cl && !cl.enabled) { cl.enabled = true; cl.ClearTransformMotion(); } return; }
            var on = c.Template.RendererOn;
            for (int i = 0; i < c.Renderers.Length; i++) if (c.Renderers[i]) c.Renderers[i].enabled = i >= on.Length || on[i];
            // a sail's furled look is the reef effect's to switch: as it is now
            foreach (var r in c.Reefs) if (r && r.furledSail) r.furledSail.enabled = reefFurled != null && (bool)reefFurled.GetValue(r);
            // drawn, the reef effects' animators always animate: culled completely they stayed at the furled pose though drawn
            // (junk-cargo-5: a Junk under way, unroll 1, its sail animators at time 0.99 and its battens within 2.7 m; set to
            // always animate, time 0.00 and 15.7 m, the sails set)
            foreach (var a in c.ReefAnimators) if (a) a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (c.Cargo) foreach (var r in c.Cargo.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            c.Shown = true;
        }
        internal static void SetLit(TraderCopy c, bool lit)
        {
            foreach (var l in c.Lights) if (l) l.enabled = lit;
            foreach (var f in c.Flames) if (f) { var e = f.emission; e.enabled = lit; }
            c.Lit = lit;
        }
        internal static TraderCopy Take(string key)
        {
            if (key == null || !pool.TryGetValue(key, out var list)) return null;
            return list.FirstOrDefault(c => c.Go && !c.Shown && c.Boat == null);
        }
        // Every good's looks made ahead (one a call, after the pool), so a trader drawn takes its deck cargo at once.
        private static int nextLook = 1;
        internal static bool LooksReady => nextLook > 64;
        internal static void LookStep()
        {
            while (nextLook <= 64 && (nextLook == 45 || nextLook == 51)) nextLook++;
            if (nextLook > 64) return;
            try { GoodLook(nextLook); } catch (Exception e) { Plugin.Log?.LogWarning("Visible traders: good " + nextLook + "'s looks: " + e.Message); goodLooks[nextLook] = null; }
            nextLook++;
        }
        internal static int FreeCount(string key) => key != null && pool.TryGetValue(key, out var list) ? list.Count(c => c.Go && c.Boat == null) : 0;
        internal static void Give(TraderCopy c)
        {
            if (c == null) return;
            c.Boat = null; c.Outfit = -1;
            if (c.Bubble) Object.Destroy(c.Bubble);
            c.Bubble = null; c.BubbleGone = 0;
            if (!c.Go) return;
            Hide(c); Park(c);
            if (c.Body) c.Body.detectCollisions = true;   // a fast clock may have left it passing through (Rules.CopySolid)
            SetCargo(c, 0);
            Sleep(c);
        }
        // A hidden copy made inactive (its cloth already off by Hide): active and hidden the pool cost 1.2-1.5 ms a frame (vtc6-1).
        // Switched on again by its first showing step.
        internal static double MaxSleepMs;
        internal static void Sleep(TraderCopy c)
        {
            if (!c.Go || !c.Go.activeSelf || c.Shown || c.Boat != null) return;
            var sw = Stopwatch.StartNew(); c.Go.SetActive(false);
            MaxSleepMs = Math.Max(MaxSleepMs, sw.Elapsed.TotalMilliseconds);
        }
        // The deck cargo: the good's looks only (it cannot be picked up), at the hull's place, laid as measured (question 17).
        internal static void SetCargo(TraderCopy c, int good)
        {
            if (c.CargoGood == good) return;
            if (c.Cargo) Object.Destroy(c.Cargo);
            c.Cargo = null; c.CargoGood = good;
            if (good <= 0) return;
            var look = GoodLook(good); if (!look) return;
            var shape = goodShapes[good]; var box0 = shape.Value; var rot = shape.Key;
            var turned = Turned(box0, rot);
            string mesh = string.Join(" ", look.GetComponentsInChildren<MeshFilter>(true).Where(m => m.sharedMesh).Select(m => m.sharedMesh.name).Distinct().ToArray());
            int hull = c.Template.Hull; var place = HullData.Cargo(hull);
            var fit = hull == HullData.CabinHull ? null : HullData.FitOf(hull, mesh, turned.size.x, turned.size.z);
            // the Junk: on its front deck when the good fits there, else in its cabin (Rules.FrontDeckPlace, CabinCargoZ)
            bool front = false, frontTurned = false; double frontZ = 0;
            if (hull == HullData.CabinHull) front = Rules.FrontDeckPlace(turned.size.x, turned.size.z, out frontTurned, out frontZ);
            var across = (fit != null && fit.Across) || frontTurned ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
            var box = Turned(box0, across * rot);
            var go = Object.Instantiate(look, c.Go.transform, false); go.name = "deck cargo";
            go.transform.localRotation = across * rot * look.transform.localRotation;
            float x = fit != null ? fit.X : place.x, y = place.y, z = fit != null ? fit.Z : place.z;
            if (front) { x = 0f; y = (float)Rules.FrontDeckY; z = (float)frontZ; }
            else if (hull == HullData.CabinHull) z = (float)Rules.CabinCargoZ(box.size.z, box.size.y);
            go.transform.localPosition = new Vector3(x, y + .02f, z) - new Vector3(box.center.x, box.min.y, box.center.z);
            go.SetActive(true);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = c.Shown;
            c.Cargo = go;
        }
        private static GameObject GoodLook(int good)
        {
            if (goodLooks.TryGetValue(good, out var look)) return look;
            ShipItem item = null;
            try { item = PrefabsDirectory.instance.GetGood(good); } catch { }
            look = item ? Visual(item.gameObject) : null;
            if (look)
            {
                foreach (var l in look.GetComponentsInChildren<Light>(true)) Object.DestroyImmediate(l);
                // laid down when it stands taller than it is wide (the logs), then its long side along the hull (vt_dress' rule)
                var b0 = ItemBounds(look);
                var lay = b0.size.y > 1.6f * Mathf.Max(b0.size.x, b0.size.z) ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
                var b1 = Turned(b0, lay);
                var rot = (b1.size.x > b1.size.z ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity) * lay;
                goodShapes[good] = new KeyValuePair<Quaternion, Bounds>(rot, b0);
            }
            goodLooks[good] = look;
            return look;
        }
        // The copy's cloths see its movement as at the game's own speed: their world velocity and acceleration scales over the clock
        // while it runs fast, their own values back at the game's speed (Rules.ClothWorldScale; set only when the clock changes).
        internal static void ClothClock(TraderCopy c, float clock)
        {
            float k = clock > 1f ? clock : 1f;
            if (Mathf.Abs(k - c.ClothClockSet) <= .01f * k) return;
            c.ClothClockSet = k;
            for (int i = 0; i < c.Cloths.Length; i++)
            {
                var cl = c.Cloths[i]; if (!cl) continue;
                cl.worldVelocityScale = (float)Rules.ClothWorldScale(c.ClothVelocity[i], k); cl.worldAccelerationScale = (float)Rules.ClothWorldScale(c.ClothAcceleration[i], k);
            }
        }
        // Returns the largest turn left to any sail before this step, in degrees.
        internal static float PoseSails(TraderCopy c, int side, float blend)
        {
            var rigs = c.Template.Sails; float left = 0;
            for (int i = 0; i < rigs.Count && i < c.SailT.Length; i++)
            {
                var t = c.SailT[i]; if (!t) continue;
                var target = side > 0 ? rigs[i].ToStarboard : side < 0 ? rigs[i].ToPort : rigs[i].Rest;
                left = Mathf.Max(left, Quaternion.Angle(t.localRotation, target));
                t.localRotation = blend >= 1 ? target : Quaternion.Slerp(t.localRotation, target, blend);
            }
            return left;
        }
        // The setting off: every copy destroyed (made again when it is turned on).
        internal static void ClearPool()
        {
            foreach (var c in Copies) if (c.Go) Object.Destroy(c.Go);
            pool.Clear(); made.Clear(); bodies.Clear(); copyWinds.Clear(); copyReefs.Clear(); poolDone = false;
        }
    }

    // ---- The traders drawn ----
    // A trader within 3 km of the player gets a copy of its hull from the pool; beyond 3.2 km it goes back. Out of sight nothing
    // changes (the fleet's timer); in sight its place along its lanes moves at the speed rule and sets the timer (Clocked).
    internal static class VisibleTraders
    {
        internal enum Phase { Sail, LegOut, Wait, LegIn, Moored, Anchored }
        internal sealed class Visual
        {
            internal BoatState Boat; internal TraderCopy Copy; internal int Hull, Class; internal string Key; internal Vector2 Size; internal float Draft;
            internal Phase Phase;
            internal LanePath Path; internal int From = -1, To = -1, Lane = -1; internal float S;     // the trip, approach point to approach point
            internal LanePath Leg; internal float U; internal TraderBerth Berth; internal int Slot = -1, Port = -1;   // a harbour leg; the berth or anchorage slot held
            internal Vector2 Pos; internal float Yaw, Pitch, Roll, Offset, Avoid, AvoidTill, Unroll, Speed, Wind, NextWind = -1, NextContact, NextCheck;
            internal Vector2 Velocity; internal int Side;
            internal bool Placed;
            internal bool Settled;     // moored or anchored once in this stay: its wait runs (Rules.WaitHeld)
            internal int LastGood;     // the main good of the trip it sails or last sailed (Rules.DeckGood)
            internal float Heave = float.NaN, NextShips;
            internal float FurledSince = -1; internal bool SailsParked;   // its reef animators culled while furled in port (TraderHulls.ParkSails)
            internal int ShowStep;
            internal Vector3 TargetReal; internal Quaternion TargetRot;
            // Meeting the player's ships (TRADER_COLLISION_DRAFT.md; nothing of it saved: a load puts each trader as before):
            // Way: its harbour leg's way, 1 the first, 2 the second (part 1). Back: going back along its leg (U falling) to BackTo,
            // Astern: stern first while the water is under 40 m wide (part 4c; it turns round in wider water). HoldAt: waiting on its
            // leg there for its way to clear. StopGap: from its bow to a player's ship blocking the rest of its way (-1: none; it
            // stops 8 m short). Aside: its place across the leg that gets round a player's ship (NaN: none), until AsideTill.
            // Beside: unit vectors toward the player's ships within 3 m of its hull (part 2), until BesideTill.
            internal int Way = 1;
            internal bool Back, Astern;
            internal float BackTo = -1, HoldAt = -1, StopGap = -1, Aside = float.NaN, AsideTill, NextWays, NextAlt, NextLeave;
            internal int BesideCount; internal readonly Vector2[] Beside = new Vector2[4]; internal float BesideTill;
            // each water sample's last good height (a Crest query that fails keeps it; NaN before the first)
            internal readonly float[] Water = { float.NaN, float.NaN, float.NaN, float.NaN };
        }
        // This frame's traffic seconds (World.Tick sets it; 0 while the fleet stands still: paused, a shipyard, the trade book).
        internal static double TrafficDt;
        private static readonly Dictionary<int, Visual> shown = new Dictionary<int, Visual>();
        private static readonly Dictionary<int, int> berthHolders = new Dictionary<int, int>();   // port * 1000 + berth number -> boat id
        private static readonly Dictionary<int, int[]> anchorSlots = new Dictionary<int, int[]>();
        private static readonly Dictionary<int, IslandHorizon> islandOf = new Dictionary<int, IslandHorizon>();
        // all layers are asked (the copies, the player's ships and the game's NPC boats share layer 0 with the docks), so the buffer
        // is large enough that a port's dock colliders cannot push a ship out of it
        private static readonly Collider[] found = new Collider[256];
        private static float nextScan;
        private static bool? scrambled;
        private static bool failedOnce;
        private static System.Reflection.FieldInfo windTarget;
        private static System.Reflection.MethodInfo chaoticLerp; private static bool chaoticLooked;
        internal static IEnumerable<Visual> Shown => shown.Values;
        internal static Visual Of(int id) => shown.TryGetValue(id, out var v) ? v : null;
        // Timings for the tests (ms): the last and the largest showing step of a trader, the largest frame of this code; the
        // largest step of each kind (the behaviours, one cloth, what draws it); each cloth's time, in order, the first 60; the
        // showing steps run so far (the test times the whole frames they fall in).
        internal static double LastShowMs, MaxShowMs, MaxFrameMs, LastFrameMs, BeginMs;
        internal static int ShowStepsRun;
        internal static readonly double[] ShowStepMs = new double[3];
        internal static readonly List<string> ClothMs = new List<string>();
        private static readonly Stopwatch watch = new Stopwatch();

        internal static int HullOf(BoatState b)
        {
            int rank = -1;
            if (b.group == "independent") rank = World.State.fleet.Where(x => x.group == "independent").OrderBy(x => x.id).ToList().FindIndex(x => x.id == b.id);
            return Rules.TraderHull(b.group, b.home, rank);
        }
        // A trader's sail plan (Rules.TraderPlan): the independents by their rank among the nine by id, the small locals by their
        // place from the last among their region's 7 by id (not the FFL pair); ids are saved, so the same traders carry the plans
        // in every load of a game.
        // (kept per fleet: a scan asks for every trader each half second)
        private static object planFleet; private static int planFleetCount;
        private static readonly Dictionary<int, string> planCache = new Dictionary<int, string>();
        internal static string PlanOf(BoatState b)
        {
            var fleet = World.State.fleet;
            if (!ReferenceEquals(planFleet, fleet) || planFleetCount != fleet.Count) { planCache.Clear(); planFleet = fleet; planFleetCount = fleet.Count; }
            if (planCache.TryGetValue(b.id, out var known)) return known;
            return planCache[b.id] = FindPlan(b, fleet);
        }
        private static string FindPlan(BoatState b, List<BoatState> fleet)
        {
            bool ffl = b.group == "small" && b.network != null && b.network.Any(p => p >= 22 && p <= 25);
            int rank = -1, fromLast = -1;
            if (b.group == "independent") rank = fleet.Where(x => x.group == "independent").OrderBy(x => x.id).ToList().FindIndex(x => x.id == b.id);
            else if (b.group == "small" && !ffl)
                fromLast = fleet.Where(x => x.group == "small" && x.home == b.home && !(x.network != null && x.network.Any(p => p >= 22 && p <= 25))).OrderByDescending(x => x.id).ToList().FindIndex(x => x.id == b.id);
            return Rules.TraderPlan(b.group, b.home, ffl, rank, fromLast);
        }
        // Its pool's key: its plan where that plan's template was taken (a plan not taken leaves its traders on the stock rig).
        internal static string KeyOf(BoatState b)
        {
            int hull = HullOf(b); if (hull < 0) return null;
            string plan = PlanOf(b);
            return plan != null && TraderHulls.Template(plan) != null ? plan : Rules.TraderKey(null, hull);
        }
        // Its helmsman's outfit, chosen at each draw (not saved: the user, "every time it could be different NPC model"), never one
        // another drawn trader wears (Rules.PickOutfit).
        private static void Wear(BoatState b, TraderCopy copy)
        {
            var all = TraderHulls.Outfits; if (all.Count == 0 || copy.HelmParts == null) return;
            bool ffl = b.group == "small" && b.network != null && b.network.Any(p => p >= 22 && p <= 25);
            int style = Rules.HelmsmanStyle(b.group, b.home, ffl);
            var worn = new HashSet<int>(TraderHulls.Copies.Where(c => c != copy && c.Boat != null && c.Outfit >= 0).Select(c => c.Outfit));
            int pick = Rules.PickOutfit(style, all.Select(o => o.Kind).ToList(), worn, UnityEngine.Random.value * .999999);
            TraderHulls.Dress(copy, pick);
            if (pick >= 0) Plugin.Log?.LogInfo("Visible traders: trader " + b.id + " (" + b.group + ", style " + (style >= 0 ? Rules.OutfitNames[style] : "any") + ") wears outfit " + pick + " (" + (all[pick].Kind >= 0 ? Rules.OutfitNames[all[pick].Kind] : "unknown") + ", from " + all[pick].From + "); outfits worn by the other drawn: " + worn.Count + ".");
        }
        internal static LanePath TripOf(BoatState b) => b.destination >= 0 ? LaneMap.Trip(b.origin, b.destination, b.lane) : null;
        // Where a trader out of sight is: its share of the trip along its lanes; while it waits, where it would be drawn: its port's
        // anchorage while the island is not loaded (Begin puts it there), else the approach point (a berth is chosen when it is
        // drawn, and with the island loaded the player is near). Was the approach point always: a trader drawn at an anchorage
        // beyond 3.2 km was let go at the next scan and drawn again at the one after, over and over (15 ports have their anchorage
        // 205-943 m past the approach point; the deep review of 2026-10-19, Claude's fix).
        internal static bool PlaceOf(BoatState b, out Vector2 at)
        {
            at = default;
            if (b.destination >= 0)
            {
                var p = TripOf(b); if (p == null) return false;
                at = p.At((float)Rules.TripPlace(b.trip, b.remaining, p.Length)); return true;
            }
            var h = LaneMap.HarbourOf(b.origin); if (h == null) return false;
            var kept = KeptBerth(b); if (kept != null) { at = kept.Centre; return true; }
            at = h.AnchorLeg != null && Refs.observerMirror && !PortLoaded(b.origin) ? h.Anchorage : h.Approach; return true;
        }
        // The lane picked between two regions at a departure (Fleet.DepartSteps).
        internal static void OnDepart(BoatState b, double time)
        {
            int choices = LaneMap.Loaded ? LaneMap.Choices(b.origin, b.destination) : 0;
            b.lane = Rules.PickLane(b.id, time, choices);
        }

        // ---- Real and shifted places ----
        // The game's conversion (FloatingOriginManager, its offset) plus any move the game has already made to the shifting world
        // but not yet to its offset: a world shift moves every child of the shifting world and updates the offset, with a physics
        // step between the two only when the manager's waitForFixedBefore or waitForFixedAfter is on (FloatingOriginManager.NewShift).
        // The scene has both off, so on 0.39.2 the lag is always zero (the QA met no lag step in vt-19 to vt-26; the deep review of
        // 2026-10-19); kept as a guard should a mod turn them on. A marker under the shifting world is moved with it, so in such a
        // step it stands one shift away from where the offset puts it. (vt-3's slide of up to 135 m was the body's interpolation.)
        private static Transform marker;
        private static int lagSteps;
        internal static Vector3 WorldLag()
        {
            var fom = FloatingOriginManager.instance; if (!fom || !Refs.shiftingWorld) return Vector3.zero;
            var origin = fom.RealPosToShiftingPos(Vector3.zero);
            if (!marker)
            {
                marker = new GameObject("EconomyOverhaul world marker").transform;
                marker.SetParent(Refs.shiftingWorld, false); marker.position = origin; lagSteps = 0;
                return Vector3.zero;
            }
            var lag = marker.position - origin; lag.y = 0;
            return lag.sqrMagnitude < 1e-4f ? Vector3.zero : lag;
        }
        // Once a physics step: a lag kept over 5 steps is not a shift under way (the marker was put down inside one): put it again.
        private static void CheckMarker()
        {
            if (WorldLag() == Vector3.zero) { lagSteps = 0; return; }
            if (++lagSteps > 5) { marker.position = FloatingOriginManager.instance.RealPosToShiftingPos(Vector3.zero); lagSteps = 0; }
        }
        internal static Vector2 PlayerReal()
        {
            var p = FloatingOriginManager.instance.ShiftingPosToRealPos(Refs.observerMirror.transform.position - WorldLag());
            return new Vector2(p.x, p.z);
        }
        private static Vector3 Shift(Vector2 real, float y = 0) => Shifted(new Vector3(real.x, y, real.y));
        internal static Vector3 Shifted(Vector3 real) => FloatingOriginManager.instance.RealPosToShiftingPos(real) + WorldLag();
        private static float YawOf(Vector2 d) => Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
        private static Vector2 DirOf(float yaw) => new Vector2(Mathf.Sin(yaw * Mathf.Deg2Rad), Mathf.Cos(yaw * Mathf.Deg2Rad));
        private static Vector2 WindBlows()
        {
            var w = new Vector2(global::Wind.currentWind.x, global::Wind.currentWind.z);
            return w.sqrMagnitude > 1e-4f ? w.normalized : Vector2.up;
        }

        // The setting (on by default; off: the fleet as today, unseen).
        internal static void SettingChanged()
        {
            if (!Settings.VisibleTradersEnabled) Off();
        }
        internal static void Off() { ReleaseAll(); TraderHulls.ClearPool(); NpcBoats(false); }

        // ---- The game's own NPC boats: off while visible traders are on (the user's feedback on 0.10.4, V7; their answer to the
        // draft's question 8, "we will disable all vanilla one") ----
        // Switched off whole (their objects inactive), checked every 5 s; the game still saves and loads their data
        // (SaveLoadManager.npcBoats: GetSaveData and LoadSaveData read only fields). Back on with the setting off or under Scrambled
        // Seas; with the mod removed the scene has them on.
        private static readonly List<GameObject> npcOff = new List<GameObject>(), npcBoats = new List<GameObject>();
        private static float nextNpc;
        private static System.Reflection.FieldInfo npcField;
        internal static int NpcBoatsOff => npcOff.Count(g => g && !g.activeSelf);
        // The game's own BoatHorizonPerformanceSwitcher sets every ship on or off each frame by its distance (on within 3 km,
        // vt-14: two NPC boats came back at once): its SetHidden keeps an NPC boat off while this is true (NpcBoatHiddenPatch).
        internal static bool NpcBoatsHidden { get; private set; }
        // Every 5 s; true when it looked this call (Update then leaves the scan and the next trader to the next frame).
        private static bool NpcBoats(bool off)
        {
            NpcBoatsHidden = off;
            if (!off)
            {
                foreach (var g in npcOff) if (g) g.SetActive(true);
                npcOff.Clear(); nextNpc = 0; return false;
            }
            if (Time.realtimeSinceStartup < nextNpc) return false;
            nextNpc = Time.realtimeSinceStartup + 5f;
            if (npcField == null) npcField = AccessTools.Field(typeof(SaveLoadManager), "npcBoats");
            npcBoats.Clear();
            if (SaveLoadManager.instance && npcField?.GetValue(SaveLoadManager.instance) is NPCBoatController[] registered)
                foreach (var b in registered) if (b) npcBoats.Add(b.gameObject);
            // the scene's fishing boats and the NPC boats not in the save manager's list (2 of them, vt-13): the active ones directly
            // under the shifting world, by its children (the same boats a search of the whole scene found, FindObjectsOfType: that
            // took 4-9 ms, so it ran only once a game and after each scene load, vt-22 to vt-26; the shifting world holds about 140
            // children in the scene: the deep review of 2026-10-19). One switched off in between is kept off by the game's distance
            // switcher's prefix (NpcBoatHiddenPatch).
            var world = Refs.shiftingWorld;
            if (world)
                for (int i = 0; i < world.childCount; i++)
                {
                    var c = world.GetChild(i);
                    if (c.gameObject.activeSelf && (c.GetComponent<NPCFishingBoat>() || c.GetComponent<NPCBoatController>())) npcBoats.Add(c.gameObject);
                }
            foreach (var g in npcBoats)
            {
                if (!g || !g.activeSelf || OtherModsBoat(g.transform)) continue;
                g.SetActive(false);
                if (!npcOff.Contains(g)) npcOff.Add(g);
            }
            return true;
        }
        // BetterNPCBoats' fishing boats stay on (the user, 2026-10-19: "please make sure you didn't turn better NPC boat mod's npc
        // boat off"). Its boats are copies of the game's NPC boats with the NPCBoatController taken off and its own
        // BetterNPCFishingBoat added, lying under an island (its FishingBoats.CreateBoat, FishingBoatScenes.SceneLoaded), so the
        // shifting-world checks above already pass them by; this keeps them off the list whatever a later version parents them to.
        internal static bool OtherModsBoat(Transform boat) => boat && boat.GetComponent("BetterNPCFishingBoat");
        // At a load or a new game: the registered ships looked up again, and an error logged again (once a game). The copies' marks
        // of the ships they passed through stay (Meet: a ship the player owns since is made solid again).
        internal static void Reset()
        {
            ReleaseAll(); keptBerth.Clear(); scrambled = null; islandOf.Clear(); islandMiss.Clear(); failedOnce = false; nextRegistered = 0; nextHulls = -1; hullPartOf.Clear();
        }
        private static void ReleaseAll() { foreach (var v in shown.Values.ToList()) Release(v); berthHolders.Clear(); anchorSlots.Clear(); toBegin.Clear(); }
        // The traders the last scan picked to be drawn, nearest first, not yet taken from the pool (Update takes one a frame).
        private static readonly List<BoatState> toBegin = new List<BoatState>();
        private static readonly List<Visual> stepping = new List<Visual>();   // the drawn traders stepped this frame

        internal static void Update()
        {
            long t0 = FrameParts.Now;
            try { UpdateParts(); TraderBubble.Step(); }
            finally { FrameParts.End(t0, shown.Count); }
        }
        private static void UpdateParts()
        {
            long tp = FrameParts.Now; TraderHulls.Poll(); FrameParts.Add(FrameParts.Templates, tp);
            double dt = TrafficDt; TrafficDt = 0;
            // Off with the setting, and after the configurator's "Prepare to uninstall" (the mod stopped: its copies freed and the
            // game's NPC boats given back; they stayed off until the game was restarted: the deep review of 2026-10-19, Claude's fix)
            if (!Settings.VisibleTradersEnabled || Uninstall.Prepared)
            {
                if (shown.Count > 0 || TraderHulls.HasPool || npcOff.Count > 0 || NpcBoatsHidden) { long r = FrameParts.Now; Off(); FrameParts.Add(FrameParts.Release, r); }
                return;
            }
            if (!TraderHulls.Captured) return;
            long t = FrameParts.Now;
            if (!TraderHulls.PoolDone && !GameState.currentlyLoading) { TraderHulls.BuildStep(); FrameParts.Add(FrameParts.Build, t); }
            else if (!TraderHulls.LooksReady && !GameState.currentlyLoading) { TraderHulls.LookStep(); FrameParts.Add(FrameParts.Looks, t); }
            if (!World.Ready || World.Loading || Startup.BlocksGameplay || GameState.currentlyLoading || !Refs.observerMirror || !FloatingOriginManager.instance || !LaneMap.Loaded)
            { if (shown.Count > 0) ReleaseAll(); return; }
            if (scrambled == null)
            {
                // Under Scrambled Seas no trader is drawn (the user's rule): a port moved from its vanilla place relative to port 0.
                double moved = MapStretch.MaxMove();
                scrambled = moved > 50;
                Plugin.Log?.LogInfo(scrambled.Value ? "Visible traders: the ports are moved (by " + moved.ToString("F0") + " m): no trader is drawn in this game." : "Visible traders: on.");
            }
            // (the flag too: a game before in the session may have left it on with no boat in the list, and the game's distance
            // switcher's prefix would then keep them off here: the deep review of 2026-10-19)
            if (scrambled.Value) { if (npcOff.Count > 0 || NpcBoatsHidden) NpcBoats(false); return; }
            t = FrameParts.Now; bool searched = NpcBoats(true); FrameParts.Add(FrameParts.Npc, t);
            watch.Restart();
            try
            {
                // the NPC boats' search, a scan and a trader taken from the pool each in a frame of its own (vt-25: at the game's
                // start all three fell in one frame, 9.33 + 7.62 + 3.21 ms); each waits a frame for the one before
                t = FrameParts.Now; bool scanned = false;
                if (!searched && Time.realtimeSinceStartup >= nextScan) { nextScan = Time.realtimeSinceStartup + .5f; Scan(); scanned = true; FrameParts.Add(FrameParts.Scan, t); }
                // one trader taken from the pool a frame, the nearest first (the user, 2026-10-18: several first drawn in one scan
                // took 18-23 ms in one frame, vt-5 and vt-7, about 2.5 ms each); the next scan makes the list again
                t = FrameParts.Now;
                while (!scanned && !searched && toBegin.Count > 0) { var b = toBegin[0]; toBegin.RemoveAt(0); if (!shown.ContainsKey(b.id)) { Begin(b); FrameParts.Add(FrameParts.Begin, t); break; } }
                t = FrameParts.Now;
                TraderHulls.EnableQueued();   // a hidden copy's cloth the test switched off back on, one a frame (KeepClothsOn)
                FrameParts.Add(FrameParts.Cloth, t);
                float fdt = Time.deltaTime;
                stepping.Clear(); stepping.AddRange(shown.Values);   // a list kept (was a new one each frame)
                foreach (var v in stepping)
                {
                    try { Step(v, (float)dt, fdt); if (shown.ContainsKey(v.Boat.id)) Place(v); }
                    catch (Exception e) { if (!failedOnce) { failedOnce = true; Plugin.Log?.LogError("Visible traders: trader " + v.Boat.id + " failed: " + e); } Release(v); }
                }
            }
            catch (Exception e) { if (!failedOnce) { failedOnce = true; Plugin.Log?.LogError("Visible traders: " + e); } }
            LastFrameMs = watch.Elapsed.TotalMilliseconds; if (LastFrameMs > MaxFrameMs) MaxFrameMs = LastFrameMs;
        }

        // The drawn place, every frame (Update): the copy's transform set where the trader is (no interpolation to blend it across a
        // world shift). The first time, its body too.
        private static void Place(Visual v)
        {
            var c = v.Copy; if (!c.Go) return;
            var at = Shifted(v.TargetReal);
            c.Go.transform.SetPositionAndRotation(at, v.TargetRot);
            if (!v.Placed && c.Body) { c.Body.position = at; c.Body.rotation = v.TargetRot; }
            v.Placed = true;
        }
        // The physics step: its kinematic body moved by MovePosition a step ahead along its way, so it meets the player's ships
        // with its own speed (a wall that pushes them, the user's rule); the next frame's Place draws it where it is.
        internal static void FixedUpdate()
        {
            if (!Settings.VisibleTradersEnabled || !FloatingOriginManager.instance) return;
            CheckMarker();
            if (shown.Count == 0) return;
            float ahead = Time.fixedDeltaTime;
            foreach (var v in shown.Values)
            {
                var c = v.Copy; if (!c.Go || !c.Body || !v.Placed) continue;
                bool solid = Rules.CopySolid(v.Velocity.magnitude);
                if (c.Body.detectCollisions != solid) c.Body.detectCollisions = solid;
                c.Body.MovePosition(Shifted(v.TargetReal) + new Vector3(v.Velocity.x, 0f, v.Velocity.y) * ahead);
                c.Body.MoveRotation(v.TargetRot);
            }
        }

        // Which traders are drawn: within 3 km (3.2 km for those already drawn), at most the pool per hull, chosen by Rules.PickDrawn
        // (one in the player's sight keeps its copy; a swap only out of sight).
        private sealed class Want { internal BoatState B; internal float D; internal int Hull; internal string Key; internal bool Drawn, Sight; }
        private static void Scan()
        {
            var player = PlayerReal();
            var cam = Camera.main; bool seeing = cam; if (seeing) GeometryUtility.CalculateFrustumPlanes(cam, frustum);
            var want = new List<Want>();
            long t = FrameParts.Now;
            foreach (var b in World.State.fleet)
            {
                Vector2 at;
                if (shown.TryGetValue(b.id, out var v)) at = v.Pos; else if (!PlaceOf(b, out at)) continue;
                float d = (at - player).magnitude;
                if (d > (v != null ? Rules.TraderHide : Rules.TraderShow)) continue;
                int hull = HullOf(b); string key = v != null ? v.Key : KeyOf(b); if (hull < 0 || TraderHulls.Template(key) == null) continue;
                want.Add(new Want { B = b, D = d, Hull = hull, Key = key, Drawn = v != null, Sight = seeing && (v != null ? InSight(v) : PlaceInSight(b, hull, at)) });
            }
            FrameParts.Add(FrameParts.ScanPlaces, t);
            // at most the pool of a plan (or a hull's stock rig) drawn (Rules.PickDrawn: the user's rule for when there are more)
            var keep = new HashSet<int>();
            foreach (var g in want.GroupBy(w => w.Key))
            {
                var list = g.ToList();
                var pick = Rules.PickDrawn(list.Select(w => (double)w.D).ToList(), list.Select(w => w.Drawn).ToList(), list.Select(w => w.Sight).ToList(), Rules.TraderPool(g.Key));
                for (int i = 0; i < list.Count; i++) if (pick[i]) keep.Add(list[i].B.id);
            }
            t = FrameParts.Now;
            foreach (var v in shown.Values.Where(x => !keep.Contains(x.Boat.id)).ToList()) Release(v);
            FrameParts.Add(FrameParts.Release, t);
            toBegin.Clear();
            foreach (var w in want.OrderBy(w => w.D)) if (keep.Contains(w.B.id) && !shown.ContainsKey(w.B.id)) toBegin.Add(w.B);
            t = FrameParts.Now;
            ReadStorms();
            foreach (var v in shown.Values)
            {
                bool lit = Lit(v.Pos);
                if (v.Copy.Lit != lit) TraderHulls.SetLit(v.Copy, lit);
                SetDeck(v);
            }
            FrameParts.Add(FrameParts.LitCargo, t);
        }

        // The most carried good on deck while it carries any, kept on its way in (Rules.DeckGood; the user, question 17: nothing
        // while it waits in port).
        private static void SetDeck(Visual v)
        {
            var b = v.Boat; int main = b.goods != null && b.goods.Length > 0 ? Rules.MainGood(b.goods) : 0;
            if (b.destination >= 0) v.LastGood = main;
            TraderHulls.SetCargo(v.Copy, Rules.DeckGood(main, b.destination < 0, v.Settled, v.LastGood));
        }

        // In the player's sight: its box (its length round it, 30 m up from under the water) inside the view of the game's camera.
        // Only the view counts, not what stands in the way (a trader behind an island in that direction counts as in sight).
        private static readonly Plane[] frustum = new Plane[6];
        private static bool InSight(Visual v)
        {
            if (!v.Copy.Go || !v.Copy.Shown) return false;
            float len = Mathf.Max(v.Size.x, v.Size.y);
            var box = new Bounds(v.Copy.Go.transform.position + Vector3.up * 12f, new Vector3(len, 30f, len));
            return GeometryUtility.TestPlanesAABB(frustum, box);
        }
        // Where a hidden trader would appear: in port anywhere in its harbour (its approach point, its anchorage, every berth: which
        // it takes is chosen when it is drawn); under way its place on its lanes and the lane's width round it.
        private static bool PlaceInSight(BoatState b, int hull, Vector2 at)
        {
            var size = HullData.Size(hull); float len = Mathf.Max(size.x, size.y);
            var h = b.destination < 0 ? LaneMap.HarbourOf(b.origin) : null;
            Bounds box;
            if (h != null)
            {
                box = new Bounds(Shift(h.Approach, 12f), new Vector3(len, 30f, len));
                float r = len + 2f * h.AnchorRadius;
                box.Encapsulate(new Bounds(Shift(h.Anchorage, 12f), new Vector3(r, 30f, r)));
                foreach (var berth in h.Berths) box.Encapsulate(new Bounds(Shift(berth.Centre, 12f), new Vector3(len, 30f, len)));
            }
            else box = new Bounds(Shift(at, 12f), new Vector3(len + 120f, 30f, len + 120f));
            return GeometryUtility.TestPlanesAABB(frustum, box);
        }

        private static bool SetTrip(Visual v)
        {
            var b = v.Boat; var p = TripOf(b); if (p == null) return false;
            v.Path = p; v.From = b.origin; v.To = b.destination; v.Lane = b.lane;
            return true;
        }

        private static void Begin(BoatState b)
        {
            var sw = Stopwatch.StartNew();
            int hull = HullOf(b); string key = KeyOf(b); var copy = TraderHulls.Take(key); if (copy == null) return;
            var v = new Visual { Boat = b, Copy = copy, Hull = hull, Key = key, Class = Rules.HullClass(hull), Size = HullData.Size(hull), Draft = HullData.Draft(hull) };
            if (b.destination >= 0)
            {
                if (!SetTrip(v)) return;
                v.Phase = Phase.Sail; v.S = (float)Rules.TripPlace(b.trip, b.remaining, v.Path.Length);
                var dir = v.Path.Direction(v.S, v.Size.x * .5f);
                v.Offset = (float)Rules.LaneShare(b.id) * LaneMap.HalfWidth(v.Path.At(v.S));
                v.Pos = v.Path.At(v.S) + new Vector2(dir.y, -dir.x) * v.Offset; v.Yaw = YawOf(dir); v.Unroll = 1;
            }
            else
            {
                var h = LaneMap.HarbourOf(b.origin); if (h == null) return;
                v.Port = b.origin; v.Unroll = 0;
                // first seen in port it is already in its place: the berth it was let go at in this stay while that is free (Q5),
                // else a berth while its island is loaded (a load, the player close by), else the anchorage (the draft's question 11
                // and first step: the berth only where the island is loaded)
                var kept = KeptBerth(b);
                if (kept != null && !(berthHolders.TryGetValue(kept.Port * 1000 + kept.Number, out int holder) && holder != b.id) && !ShipIn(kept))
                {
                    FreePlace(v); v.Berth = kept; berthHolders[kept.Port * 1000 + kept.Number] = b.id;
                    v.Phase = Phase.Moored; v.Pos = kept.Centre; v.Yaw = BerthYaw(kept, YawOf(kept.InLeg.Direction(kept.InLeg.Length, 10)));
                }
                else if (TakePlace(v, h, PortLoaded(b.origin), false))
                {
                    if (v.Berth != null) { v.Phase = Phase.Moored; v.Pos = v.Berth.Centre; v.Yaw = BerthYaw(v.Berth, YawOf(v.Berth.InLeg.Direction(v.Berth.InLeg.Length, 10))); }
                    else { v.Phase = Phase.Anchored; v.Yaw = YawOf(-WindBlows()); v.Pos = AnchorPoint(v, h); }
                }
                else { v.Phase = Phase.Wait; v.Yaw = YawOf(-WindBlows()); v.Pos = WaitPoint(v, h); }
            }
            copy.Boat = b; shown[b.id] = v;
            Wear(b, copy);
            v.Placed = false; v.ShowStep = 0;
            Pose(v, 0, 0, true);
            // first drawn in port, its wait has been running unseen: it goes on
            if (b.destination < 0) v.Settled = true;
            ReadStorms(); TraderHulls.SetLit(copy, Lit(v.Pos));
            SetDeck(v);
            foreach (var s in copy.Sails) if (s) s.currentUnroll = v.Unroll;
            TraderHulls.PoseSails(copy, v.Unroll > .5f ? SailSide(v) : 0, 1);
            // at its place now, before its first show step tells its cloth to forget the move (it ran while the copy still lay where
            // it was parked, 2 km down: the deep review of 2026-10-19)
            Place(v);
            BeginMs = Math.Max(BeginMs, sw.Elapsed.TotalMilliseconds);
        }

        // A trader let go while moored remembers its berth for the rest of that stay (its wait only goes down within a stay): out of
        // sight it lies there, and drawn again it is put back there if the berth is still free, not at the anchorage (the user,
        // 2026-10-19, Q5 of the deep review: "b"; a moored trader let go at 3.2 km was drawn again at once at a nearer anchorage).
        private sealed class Kept { internal TraderBerth Berth; internal double Remaining; }
        private static readonly Dictionary<int, Kept> keptBerth = new Dictionary<int, Kept>();
        private static TraderBerth KeptBerth(BoatState b)
        {
            if (b.destination >= 0 || !keptBerth.TryGetValue(b.id, out var k)) return null;
            if (k.Berth.Port != b.origin || b.remaining > k.Remaining + 1e-6) { keptBerth.Remove(b.id); return null; }
            return k.Berth;
        }
        private static void Release(Visual v)
        {
            if (v.Phase == Phase.Moored && v.Berth != null && v.Boat.destination < 0) keptBerth[v.Boat.id] = new Kept { Berth = v.Berth, Remaining = v.Boat.remaining };
            else keptBerth.Remove(v.Boat.id);
            FreePlace(v);
            shown.Remove(v.Boat.id);
            TraderHulls.Give(v.Copy);
        }

        // ---- The clock (Fleet.Tick): a drawn trader under way sets its own timer from its place ----
        internal static bool Clocked(BoatState b, double dt)
        {
            if (shown.Count == 0 || !shown.TryGetValue(b.id, out var v)) return false;
            try
            {
                float d = (float)(v.Speed * dt);
                switch (v.Phase)
                {
                    case Phase.Sail:
                        if (b.destination < 0 || v.Path == null || b.origin != v.From || b.destination != v.To) return false;
                        v.S = Mathf.Min(v.S + d, v.Path.Length);
                        b.remaining = Rules.TripRemaining(b.trip, v.S, v.Path.Length);
                        return true;
                    case Phase.LegOut:
                        v.U = v.Back ? Mathf.Max(v.BackTo, v.U - d) : Mathf.Min(v.U + d, v.Leg.Length);
                        // the trip's timer starts at the approach point, where today's straight line starts (VISIBLE_TRADERS.md)
                        if (b.destination >= 0) { b.remaining = b.trip; return true; }
                        // going to the approach point to choose again: its wait held until it is moored or anchored (it runs on for
                        // one that was: Settled)
                        return Rules.WaitHeld(true, v.Settled);
                    case Phase.LegIn:
                        if (v.Back) v.U = Mathf.Max(v.BackTo, v.U - d);
                        else v.U = Mathf.Min(v.U + d * Mathf.Clamp((v.Leg.Length - v.U) / 30f, .25f, 1f), v.Leg.Length);
                        return Rules.WaitHeld(b.destination < 0, v.Settled);
                    // waiting at the approach point for its place: held too (the wait starts moored or anchored)
                    case Phase.Wait: return Rules.WaitHeld(b.destination < 0, v.Settled);
                    // its wait over but no way out clear of the player's ships: it stays, its trip clock held at the full trip
                    // (TRADER_COLLISION_DRAFT.md, part 1)
                    case Phase.Moored: case Phase.Anchored:
                        if (b.destination >= 0) { b.remaining = b.trip; return true; }
                        return false;
                    default: return false;
                }
            }
            catch { return false; }
        }

        // ---- One frame of a drawn trader ----
        private static void Step(Visual v, float dt, float fdt)
        {
            var b = v.Boat;
            long t = FrameParts.Now;
            if (v.ShowStep < TraderHulls.ShowSteps(v.Copy) && TraderHulls.MayStep(v.Copy, v.ShowStep))
            {
                var sw = Stopwatch.StartNew(); TraderHulls.ShowStep(v.Copy, v.ShowStep);
                double ms = sw.Elapsed.TotalMilliseconds; int kind = TraderHulls.StepKind(v.Copy, v.ShowStep);
                ShowStepMs[kind] = Math.Max(ShowStepMs[kind], ms); LastShowMs = ms; if (ms > MaxShowMs) MaxShowMs = ms;
                if (kind == 1 && ClothMs.Count < 60) ClothMs.Add("hull " + v.Hull + " trader " + b.id + " cloth " + v.ShowStep + " of " + v.Copy.Cloths.Length + ": " + ms.ToString("F2") + " ms");
                v.ShowStep++; ShowStepsRun++;
                FrameParts.Add(FrameParts.Show, t);
            }
            t = FrameParts.Now;
            if (Time.time >= v.NextWind) { v.NextWind = Time.time + 2f; v.Wind = Strength(v.Pos); }
            FrameParts.Add(FrameParts.Wind, t); t = FrameParts.Now;
            // what the fleet's events changed
            if (v.Phase == Phase.Sail && b.destination < 0) Arrived(v);
            else if (v.Phase == Phase.Sail && (b.origin != v.From || b.destination != v.To))
            {
                if (!SetTrip(v)) { Release(v); return; }
                v.S = (float)Rules.TripPlace(b.trip, b.remaining, v.Path.Length);
            }
            // its wait over: out by a clear way (Leave; one with none clear stays at its place, its trip clock held); not while it goes
            // back to its berth or anchorage on its way out (LegOut, Back), which Leave would turn round again
            if (b.destination >= 0 && Time.time >= v.NextLeave && (v.Phase == Phase.Wait || v.Phase == Phase.LegIn || v.Phase == Phase.Moored || v.Phase == Phase.Anchored)) { if (!Leave(v)) { Release(v); return; } }
            if (v.Back && v.U <= v.BackTo + .01f) EndBack(v);
            if (v.Phase == Phase.LegOut && !v.Back && v.U >= v.Leg.Length - .01f)
            {
                // the place it left (held while it might go back to it) is let go at the approach point
                if (b.destination >= 0) { FreePlace(v); if (!SetTrip(v)) { Release(v); return; } v.Phase = Phase.Sail; v.S = 0; }
                else { FreePlace(v); v.Phase = Phase.Wait; v.NextCheck = 0; }
            }
            FrameParts.Add(FrameParts.Trips, t); t = FrameParts.Now;
            // waiting at the approach point: a berth while its island is loaded, else the anchorage (question 11), each only by a way
            // clear of the player's ships (TRADER_COLLISION_DRAFT.md, part 1); none: it waits here with its wait held (the user:
            // "waiting time still start counting until trader enter berth/anchorage"), but at a port with neither (none today)
            if (v.Phase == Phase.Wait && b.destination < 0 && Time.time >= v.NextCheck)
            {
                v.NextCheck = Time.time + 1f;
                var h = LaneMap.HarbourOf(v.Port);
                if (h != null && TakePlace(v, h, PortLoaded(v.Port), true)) StartLeg(v, v.Berth != null ? v.Berth.In(v.Way) : h.AnchorIn(v.Way), v.Way, Phase.LegIn);
                else if (h == null || h.Berths.Count == 0 && h.AnchorLeg == null) v.Settled = true;
            }
            if (v.Phase == Phase.LegIn && !v.Back && v.U >= v.Leg.Length - .01f) { v.Phase = v.Berth != null ? Phase.Moored : Phase.Anchored; v.Settled = true; v.HoldAt = -1; }
            // on a harbour leg: a player's ship in the rest of its way (part 1; 4 times a second)
            if ((v.Phase == Phase.LegIn || v.Phase == Phase.LegOut) && Time.time >= v.NextWays) Ways(v);
            // on its way in it looks at its berth every 2 s, as a moored one does (part 1, "The fix"): a ship come into it, and it
            // goes back to the approach point to choose again; an anchorage slot a player's ship has come into: another slot
            if (v.Phase == Phase.LegIn && !v.Back && b.destination < 0 && Time.time >= v.NextCheck)
            {
                v.NextCheck = Time.time + 2f;
                var h = LaneMap.HarbourOf(v.Port);
                if (v.Berth != null && ShipIn(v.Berth)) GoBack(v, 0f);
                else if (v.Berth == null && v.Slot >= 0 && h != null && SlotHeld(v, h, v.Slot)) { if (!MoveSlot(v, h)) GoBack(v, 0f); }
            }
            // a ship in a moored trader's berth (a recovery, the player mooring there): it goes to another berth or the anchorage
            // (the user, 2026-10-17), out by a clear way; with none clear it stays (and moves nothing into the ship, part 2)
            if (v.Phase == Phase.Moored && b.destination < 0 && Time.time >= v.NextCheck)
            {
                v.NextCheck = Time.time + 2f;
                int way = ShipIn(v.Berth) ? OutWay(v, 0) : 0;
                if (way > 0) { var leg = v.Berth.Out(way); FreePlace(v); StartLeg(v, leg, way, Phase.LegOut); }
            }
            // anchored while its island was not loaded or the berths were full: once the island is loaded and a berth it fits is free
            // with a clear way, it goes there by the shorter: its direct way from the anchorage (2026-10-21 evening, the user: "yes
            // after fix"; its slot let go, the berth held), or out to the approach point to take it there (its anchorage slot held
            // until there, the berth not; VISIBLE_TRADERS.md, "Chosen by Claude", and the collision build's choice (7))
            if (v.Phase == Phase.Anchored && b.destination < 0 && Time.time >= v.NextCheck)
            {
                v.NextCheck = Time.time + 2f;
                var h = LaneMap.HarbourOf(v.Port); int way = 0;
                int k = h != null && h.AnchorLeg != null && PortLoaded(v.Port) ? FreeBerthWay(v, h, out way, null, 0, true) : -1;
                if (k >= 0 && way == 3)
                {
                    FreePlace(v); v.Berth = h.Berths[k]; berthHolders[h.Port * 1000 + v.Berth.Number] = v.Boat.id;
                    StartLeg(v, v.Berth.Direct, 3, Phase.LegIn);
                }
                else if (k >= 0) { int outWay = OutWay(v, 0); if (outWay > 0) StartLeg(v, h.AnchorOut(outWay), outWay, Phase.LegOut); }
            }
            FrameParts.Add(FrameParts.Berths, t); t = FrameParts.Now;
            v.Speed = (float)Rules.TraderSpeed(b.speed, v.Wind, Vector2.Dot(DirOf(v.Yaw), WindBlows()));
            if (v.Phase != Phase.Sail && v.Phase != Phase.LegOut && v.Phase != Phase.LegIn) v.Speed = 0;
            // in port: waiting on its leg it stands; a player's ship blocking the rest of its way, it stops 8 m short (part 2)
            else if (v.Phase != Phase.Sail && !v.Back)
            {
                if (v.HoldAt >= 0) v.Speed = 0;
                else if (v.StopGap >= 0) v.Speed = (float)Rules.InPortSpeed(v.Speed, v.StopGap);
            }
            if (Time.time >= v.NextShips) { SaleShips(v); FrameParts.Add(FrameParts.SaleShips, t); t = FrameParts.Now; }
            if (Time.time >= v.NextContact) { Contacts(v, v.Phase == Phase.Sail || v.Phase == Phase.LegOut || v.Phase == Phase.LegIn); FrameParts.Add(FrameParts.Contacts, t); t = FrameParts.Now; }
            Pose(v, dt, fdt, false);
            FrameParts.Add(FrameParts.Pose, t); t = FrameParts.Now;
            Sails(v, dt, fdt);
            FrameParts.Add(FrameParts.Sails, t);
        }

        private static void Arrived(Visual v)
        {
            v.Phase = Phase.Wait; v.Port = v.Boat.origin; v.Path = null; v.Leg = null; v.NextCheck = 0; v.Settled = false;
        }
        // The wait is over (the fleet departed it): out by the shorter way clear of the player's ships, then the lanes. With neither
        // clear it stays at its berth or anchorage (sails furled, its trip clock held at the full trip: Clocked) and looks again
        // every second (TRADER_COLLISION_DRAFT.md, part 1). Its place stays held on its way out, so it can go back to it (GoBack);
        // it is let go at the approach point.
        private static bool Leave(Visual v)
        {
            var h = LaneMap.HarbourOf(v.Port);
            switch (v.Phase)
            {
                case Phase.Wait:
                    FreePlace(v);
                    if (!SetTrip(v)) return false;
                    v.Phase = Phase.Sail; v.S = 0; return true;
                case Phase.Moored: case Phase.Anchored:
                    {
                        if (v.Phase == Phase.Anchored && h?.AnchorLeg == null) { FreePlace(v); return false; }
                        int way = OutWay(v, 0);
                        if (way == 0) { v.NextLeave = Time.time + 1f; return true; }
                        StartLeg(v, v.Phase == Phase.Moored ? v.Berth.Out(way) : h.AnchorOut(way), way, Phase.LegOut);
                        break;
                    }
                // on a direct way from the anchorage it goes back there and leaves from it (its way out to the approach point)
                case Phase.LegIn when v.Way == 3: { if (!v.Back) GoBack(v, 0f); v.NextLeave = Time.time + 1f; break; }
                case Phase.LegIn: { var leg = v.Leg; float u = v.U; FreePlace(v); StartLeg(v, leg.Reversed(), v.Way, Phase.LegOut, Mathf.Max(0, leg.Length - u)); break; }
            }
            return true;
        }
        // On a harbour leg from u, nothing of the leg before carried over (going back, waiting, a stop, an aside); its ways looked at
        // at once.
        private static void StartLeg(Visual v, LanePath leg, int way, Phase phase, float u = 0)
        {
            v.Leg = leg; v.Way = way; v.U = u; v.Phase = phase;
            v.Back = v.Astern = false; v.BackTo = v.HoldAt = v.StopGap = -1; v.Aside = float.NaN; v.NextWays = 0;
        }

        // ---- In port: the player's ships (TRADER_COLLISION_DRAFT.md, the user's answers of 2026-10-21) ----
        // The player's ships by their whole hull (part 3, C3): every registered ship the player bought, and the one the player is
        // aboard; only these (the user: "Player model shouldn't be recognized by trader, only player ship"). Each by the box of its
        // hull mesh, the mesh the game's HullPlayerCollider copies for the swimming player (on every ship, modded ones too), turned
        // with the hull; a ship without one by its Boat-tagged capsule. Looked up again 4 times a second.
        private static readonly List<HullBox> playerHulls = new List<HullBox>();
        private static readonly List<Transform> playerShips = new List<Transform>();
        private static float nextHulls = -1;
        private static readonly Dictionary<int, Component> hullPartOf = new Dictionary<int, Component>();
        internal static List<HullBox> PlayerHulls()
        {
            if (Time.time < nextHulls) return playerHulls;
            nextHulls = Time.time + .25f; playerHulls.Clear(); playerShips.Clear();
            if (!FloatingOriginManager.instance) return playerHulls;
            foreach (var p in RegisteredShips()) if (p && PlayersShip(p.transform)) playerShips.Add(p.transform);
            var on = GameState.currentBoat;
            if (on)
            {
                Transform ship = null;
                for (var x = on; x && x != Refs.shiftingWorld && !ship; x = x.parent) if (x.GetComponent<PurchasableBoat>() || x.GetComponent<BoatRefs>()) ship = x;
                if (ship && !playerShips.Contains(ship)) playerShips.Add(ship);
            }
            foreach (var s in playerShips) { var box = HullOf(s); if (box != null) playerHulls.Add(box); }
            return playerHulls;
        }
        internal static HullBox HullOf(Transform ship)
        {
            int id = ship.GetInstanceID();
            if (!hullPartOf.TryGetValue(id, out var part) || !part)
            {
                part = ship.GetComponentInChildren<HullPlayerCollider>(true);
                if (!part) foreach (var c in ship.GetComponentsInChildren<CapsuleCollider>(true)) if (c.CompareTag("Boat")) { part = c; break; }
                if (hullPartOf.Count > 256) hullPartOf.Clear();
                hullPartOf[id] = part;
            }
            Bounds local; Transform t;
            if (part is HullPlayerCollider hp)
            {
                var mesh = hp.GetComponent<MeshCollider>() ? hp.GetComponent<MeshCollider>().sharedMesh : null; if (!mesh) return null;
                local = mesh.bounds; t = hp.transform;
            }
            else if (part is CapsuleCollider cap)
            {
                var size = Vector3.one * cap.radius * 2; size[cap.direction] = Mathf.Max(cap.height, cap.radius * 2);
                local = new Bounds(cap.center, size); t = cap.transform;
            }
            else return null;
            return Box(t, local);
        }
        // The box on the water of a box in a transform's frame: its middle, and the bounds in the frame of its longest level axis.
        private static HullBox Box(Transform t, Bounds local)
        {
            var c = Real(t.TransformPoint(local.center));
            var e = local.extents; var axes = new[] { t.TransformVector(new Vector3(e.x, 0, 0)), t.TransformVector(new Vector3(0, e.y, 0)), t.TransformVector(new Vector3(0, 0, e.z)) };
            var f = new Vector2(axes[0].x, axes[0].z);
            foreach (var a in axes) if (new Vector2(a.x, a.z).sqrMagnitude > f.sqrMagnitude) f = new Vector2(a.x, a.z);
            f = f.sqrMagnitude > 1e-6f ? f.normalized : Vector2.up; var r = Across(f);
            float ha = 0, hb = 0; foreach (var a in axes) { var h = new Vector2(a.x, a.z); ha += Mathf.Abs(Vector2.Dot(h, f)); hb += Mathf.Abs(Vector2.Dot(h, r)); }
            return new HullBox(c.x, c.y, f.x, f.y, ha, hb);
        }
        private static Vector2 Real(Vector3 shifted) { var p = FloatingOriginManager.instance.ShiftingPosToRealPos(shifted - WorldLag()); return new Vector2(p.x, p.z); }
        // The player's distance to a drawn trader's hull box, across the water (TraderBubble's 60 m).
        internal static double HullDistance(Visual v, Vector2 p) => Rules.BoxDistance(BoxOf(v), p.x, p.y);
        private static HullBox BoxOf(Visual v) => new HullBox(v.Pos.x, v.Pos.y, DirOf(v.Yaw).x, DirOf(v.Yaw).y, v.Size.x / 2, v.Size.y / 2);

        // A way clear of the player's ships from its start (part 1): nothing blocking it ahead (TraderWays.Look). placeAtEnd: it ends at
        // the berth or anchorage (in-bound).
        private static bool WayClear(Visual v, LanePath leg, bool placeAtEnd) =>
            leg != null && !TraderWays.Look(leg, 0f, placeAtEnd, PlayerHulls(), v.Size.x, v.Size.y, 0f).Blocked;
        // Out from its berth or anchorage: the shorter clear way, 0 for none; skip: a way not to take (the one it found blocked).
        private static int OutWay(Visual v, int skip)
        {
            var h = LaneMap.HarbourOf(v.Port); if (h == null) return 0;
            var len = new double[2];
            for (int w = 1; w <= 2; w++)
            {
                var leg = v.Berth != null ? v.Berth.Out(w) : h.AnchorOut(w);
                len[w - 1] = w != skip && WayClear(v, leg, false) ? leg.Length : double.NaN;
            }
            return Rules.ShorterWay(len);
        }
        // An anchorage slot's place for this trader (AnchorPoint's), and whether a player's ship holds it (part 2).
        private static Vector2 SlotPoint(Visual v, TraderHarbour h, int slot) => h.Anchorage + Across(-WindBlows()) * (slot - 1) * (v.Size.y + 4f);
        private static bool SlotHeld(Visual v, TraderHarbour h, int slot)
        {
            var p = SlotPoint(v, h, slot);
            foreach (var s in PlayerHulls()) if (Rules.SlotHeld(p.x, p.y, s, v.Size.x)) return true;
            return false;
        }
        // A slot no player's ship holds, one no other trader holds first; past three the traders share them (the user's rule) but
        // never one a player's ship holds; -1 when the player's ships hold all three.
        private static int FreeSlot(Visual v, TraderHarbour h, int[] slots)
        {
            int open = -1, shared = 0; var ok = new bool[3];
            for (int k = 0; k < 3; k++) { ok[k] = !SlotHeld(v, h, k); if (ok[k]) shared++; if (ok[k] && open < 0 && (slots[k] == -1 || slots[k] == v.Boat.id)) open = k; }
            if (open >= 0 || shared == 0) return open;
            int pick = v.Boat.id % shared;
            for (int k = 0; k < 3; k++) if (ok[k] && pick-- == 0) return k;
            return -1;
        }
        private static bool MoveSlot(Visual v, TraderHarbour h)
        {
            if (!anchorSlots.TryGetValue(h.Port, out var slots)) return false;
            if (v.Slot >= 0 && slots[v.Slot] == v.Boat.id) slots[v.Slot] = -1;
            int k = FreeSlot(v, h, slots); if (k < 0) return false;
            v.Slot = k; if (slots[k] == -1) slots[k] = v.Boat.id;
            return true;
        }

        // The ways check, 4 times a second on a harbour leg (parts 1, 2 and 4): a player's ship it can get round, and it moves aside
        // as far as the water allows; one blocking the rest of its way, and it stops 8 m short (Step), then goes back to the approach
        // point (in-bound) or its berth or anchorage (out-bound) when another way, berth or the anchorage is clear there; with none
        // clear it backs out of a single-file stretch it is in (stern first, part 4c), else waits where it stopped (K3 (a)), looking
        // again every second.
        private static void Ways(Visual v)
        {
            v.NextWays = Time.time + .25f;
            if (v.Back) return;
            var look = TraderWays.Look(v.Leg, v.U, v.Phase == Phase.LegIn, PlayerHulls(), v.Size.x, v.Size.y, v.Offset);
            if (!look.Blocked)
            {
                v.StopGap = -1; v.HoldAt = -1;
                if (!float.IsNaN(look.Aside)) { v.Aside = look.Aside; v.AsideTill = Time.time + 2f; }
                return;
            }
            v.StopGap = Mathf.Max(0, look.At - (v.U + v.Size.x / 2));
            // still slowing toward it (the speed falls with the gap, so the last metre takes long: it counts as stopped within 1 m)
            if (v.StopGap > Rules.StopShort + 1f && v.HoldAt < 0) return;
            if (Time.time < v.NextAlt) return;
            v.NextAlt = Time.time + 1f;
            if (Alternative(v)) GoBack(v, 0f);
            // inside a single-file stretch: out of it behind (its whole hull, and 2 m)
            else if (look.SingleFile && v.U + v.Size.x / 2 > look.StretchStart) GoBack(v, Mathf.Max(0f, look.StretchStart - v.Size.x / 2 - (float)Rules.WayMargin));
            else v.HoldAt = v.U;
        }
        // Another way from where going back ends: in-bound, from the approach point a berth with a clear way (not the way it is on),
        // or the anchorage by a clear way with a slot free of the player's ships (by its other way when it is going there now);
        // out-bound, its berth's or anchorage's other way, while it still holds that place.
        private static bool Alternative(Visual v)
        {
            var h = LaneMap.HarbourOf(v.Port); if (h == null) return false;
            // on a direct way from the anchorage: back to the anchorage, its own end, to choose again there (BackToAnchorage)
            if (v.Phase == Phase.LegIn && v.Way == 3) return true;
            if (v.Phase == Phase.LegOut) return (v.Berth != null || v.Slot >= 0) && OutWay(v, v.Way) > 0;
            if (PortLoaded(v.Port) && FreeBerthWay(v, h, out _, v.Berth, v.Way) >= 0) return true;
            if (h.AnchorLeg == null) return false;
            if (v.Berth == null) return WayClear(v, h.AnchorIn(v.Way == 2 ? 1 : 2), true);
            int[] slots = anchorSlots.TryGetValue(h.Port, out var s) ? s : new[] { -1, -1, -1 };
            return FreeSlot(v, h, slots) >= 0 && (WayClear(v, h.AnchorIn(1), true) || WayClear(v, h.AnchorIn(2), true));
        }
        // Back along its leg to `to` (0: its start), stern first while the water is under 40 m wide (part 4c).
        private static void GoBack(Visual v, float to)
        {
            v.Back = true; v.BackTo = Mathf.Min(to, v.U); v.HoldAt = -1; v.StopGap = -1; v.Aside = float.NaN;
            v.Astern = TraderWays.BackAstern(v.Leg, v.U);
        }
        // Gone back: at the approach point (in-bound) it chooses again; at its berth or anchorage (out-bound) it lies there and
        // leaves by another way (Leave); elsewhere on its leg it waits there for its way to clear.
        private static void EndBack(Visual v)
        {
            v.Back = false; v.StopGap = -1;
            if (v.BackTo > .01f) { v.HoldAt = v.U; return; }
            if (v.Phase == Phase.LegIn && v.Way == 3) BackToAnchorage(v);
            else if (v.Phase == Phase.LegIn) { FreePlace(v); v.Phase = Phase.Wait; v.NextCheck = 0; v.Leg = null; }
            else if (v.Berth != null || v.Slot >= 0) { v.Phase = v.Berth != null ? Phase.Moored : Phase.Anchored; v.NextLeave = 0; v.NextCheck = Time.time + 2f; }
            else v.HoldAt = v.U;
        }
        // Back at the anchorage by a berth's direct way: anchored again in a slot no player's ship holds, the berth let go, and it
        // chooses again there (or leaves, its wait over); with no slot it waits at the way's start, the berth still held.
        private static void BackToAnchorage(Visual v)
        {
            var h = LaneMap.HarbourOf(v.Port); var berth = v.Berth;
            if (h != null && h.AnchorLeg != null && TakePlace(v, h, false, false))
            {
                v.Phase = Phase.Anchored; v.Leg = null; v.NextLeave = 0; v.NextCheck = Time.time + 2f;
                return;
            }
            if (berth != null) { v.Berth = berth; berthHolders[berth.Port * 1000 + berth.Number] = v.Boat.id; }
            v.HoldAt = v.U;
        }

        // ---- In port ----
        // "Fully loaded at normal height" (the user's rule; the draft's item 6): the island's scene loaded and the player within its
        // fully emerged distance of its centre.
        internal static bool PortLoaded(int port)
        {
            var island = Island(port);
            if (!island) { var p = Port.ports != null && port >= 0 && port < Port.ports.Length ? Port.ports[port] : null; return p && Vector3.Distance(p.transform.position, Refs.observerMirror.transform.position) < 1500; }
            // a modded island's index need not be a scene of the game's build (Bottleneck Island's 67 threw "Invalid build index"
            // here, vt-27): its scene is not looked at, its distance decides
            if (island.islandIndex > 0 && island.islandIndex < SceneManager.sceneCountInBuildSettings && !SceneManager.GetSceneByBuildIndex(island.islandIndex).isLoaded) return false;
            var c = island.GetPosition(); var me = Refs.observerMirror.transform.position;
            return new Vector2(c.x - me.x, c.z - me.z).magnitude <= island.fullyEmergedDistance;
        }
        // A port with no island found is looked for again only after 5 s (it was searched every frame for a trader there).
        private static readonly Dictionary<int, float> islandMiss = new Dictionary<int, float>();
        private static IslandHorizon Island(int port)
        {
            if (islandOf.TryGetValue(port, out var h) && h) return h;
            if (islandMiss.TryGetValue(port, out float retry) && Time.realtimeSinceStartup < retry) return null;
            var p = Port.ports != null && port >= 0 && port < Port.ports.Length ? Port.ports[port] : null;
            if (!p || !IslandDistanceTracker.instance) return null;
            h = IslandDistanceTracker.instance.islands.FirstOrDefault(i => i && p.island && i.economy == p.island);
            if (h) { islandOf[port] = h; islandMiss.Remove(port); } else islandMiss[port] = Time.realtimeSinceStartup + 5f;
            return h;
        }
        // The free berth this trader would take (the smallest size its hull fits, the lowest number), -1 for none; a berth its hull
        // is too long or wide for counts as taken (Rules.BerthFits).
        // Taken for this trader: another trader holds it, its hull does not fit, or a ship lies in it.
        private static bool BerthTaken(Visual v, TraderBerth x) =>
            berthHolders.TryGetValue(x.Port * 1000 + x.Number, out int id) && id != v.Boat.id || !Rules.BerthFits(x.Length, x.Beam, v.Size.x, v.Size.y) || ShipIn(x);
        // Since 2026-10-21 only by a way clear of the player's ships (FreeBerthWay; QA vtsaleships reads this one).
        private static int FreeBerth(Visual v, TraderHarbour h) => FreeBerthWay(v, h, out _, null, 0);
        // The free berth on its way in (TRADER_COLLISION_DRAFT.md, part 1: the user's rule among the berths with a clear way in, by the
        // shorter clear way, Rules.PickBerthWay); skipBerth and skipWay: the way it found blocked. anchored: from the anchorage, its
        // ways 1 and 2 counted with the anchorage's shorter clear way out to the approach point, and way 3 the berth's direct way.
        private static int FreeBerthWay(Visual v, TraderHarbour h, out int way, TraderBerth skipBerth, int skipWay, bool anchored = false)
        {
            var classes = h.Berths.Select(x => x.Class).ToList();
            var taken = h.Berths.Select(x => BerthTaken(v, x)).ToList();
            var ways = new List<double[]>();
            double outLen = double.NaN;
            if (anchored) { int o = OutWay(v, 0); if (o > 0) outLen = h.AnchorOut(o).Length; }
            for (int i = 0; i < h.Berths.Count; i++)
            {
                var len = new[] { double.NaN, double.NaN, double.NaN };
                if (!taken[i])
                    for (int w = 1; w <= (anchored ? 3 : 2); w++)
                    {
                        var leg = h.Berths[i].In(w);
                        if (leg == null || h.Berths[i] == skipBerth && w == skipWay || anchored && w < 3 && double.IsNaN(outLen) || !WayClear(v, leg, true)) continue;
                        len[w - 1] = (anchored && w < 3 ? outLen : 0) + leg.Length;
                    }
                ways.Add(len);
            }
            return Rules.PickBerthWay(classes, taken, v.Class, ways, out way);
        }
        // The berth (when berth: its island loaded), else the anchorage (a slot of three, side by side, none a player's ship holds).
        // ways: only by a way clear of the player's ships (part 1; its first drawing in port puts it at its place, any way); v.Way
        // set. False when there is none.
        private static bool TakePlace(Visual v, TraderHarbour h, bool berth, bool ways)
        {
            FreePlace(v);
            int way = 1, k = !berth ? -1 : ways ? FreeBerthWay(v, h, out way, null, 0) : Rules.PickBerth(h.Berths.Select(x => x.Class).ToList(), h.Berths.Select(x => BerthTaken(v, x)).ToList(), v.Class);
            if (k >= 0) { v.Berth = h.Berths[k]; berthHolders[h.Port * 1000 + v.Berth.Number] = v.Boat.id; v.Port = h.Port; v.Way = Math.Max(1, way); return true; }
            if (h.AnchorLeg == null) return false;
            if (ways) way = Rules.ShorterWay(new[] { WayClear(v, h.AnchorIn(1), true) ? h.AnchorLeg.Length : double.NaN, WayClear(v, h.AnchorIn(2), true) ? h.AnchorSecond.Length : double.NaN });
            if (way == 0) return false;
            if (!anchorSlots.TryGetValue(h.Port, out var slots)) anchorSlots[h.Port] = slots = new[] { -1, -1, -1 };
            // more than three: they pass through each other (the user's rule), never in a slot a player's ship holds (part 2)
            int free = FreeSlot(v, h, slots); if (free < 0) return false;
            v.Slot = free; if (slots[free] == -1) slots[free] = v.Boat.id;
            v.Berth = null; v.Port = h.Port; v.Way = way;
            return true;
        }
        private static void FreePlace(Visual v)
        {
            if (v.Berth != null && berthHolders.TryGetValue(v.Berth.Port * 1000 + v.Berth.Number, out int id) && id == v.Boat.id) berthHolders.Remove(v.Berth.Port * 1000 + v.Berth.Number);
            if (v.Slot >= 0 && v.Port >= 0 && anchorSlots.TryGetValue(v.Port, out var slots) && slots[v.Slot] == v.Boat.id) slots[v.Slot] = -1;
            v.Berth = null; v.Slot = -1;
        }
        // A berth counts as taken while a ship lies in it (any Boat-tagged collider in its hull's rectangle and 2 m round it), or
        // while a registered ship's place is in it, switched on or not: a ship for sale far off is switched off until the game's
        // distance check (10-20 s after a load, the probe vt_saleships), and a berth at its spot must not be taken before (the
        // user's feedback on 0.10.4, V1: "when that spot don't have ship(player buy it and sail away), trader can use it").
        // The player's ships also by their whole hull's box (TRADER_COLLISION_DRAFT.md, part 3, C3: "check ship's whole hull"; a
        // Baghlah's ends reach 7.75 m past its capsule; a ship across two berths takes both).
        private static bool ShipIn(TraderBerth berth)
        {
            if (berth == null) return false;
            var box = new HullBox(berth.Centre.x, berth.Centre.y, DirOf(berth.Heading).x, DirOf(berth.Heading).y, berth.Length / 2, berth.Beam / 2);
            foreach (var s in PlayerHulls()) if (Rules.BoxesOverlap(box, s, 2)) return true;
            var centre = Shift(berth.Centre, 1f); var turn = Quaternion.Euler(0, berth.Heading, 0);
            int n = Physics.OverlapBoxNonAlloc(centre, new Vector3(berth.Beam / 2 + 2, 4f, berth.Length / 2 + 2), found, turn, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++) if (found[i] && found[i].CompareTag("Boat")) return true;
            var inv = Quaternion.Inverse(turn);
            foreach (var p in RegisteredShips())
            {
                if (!p) continue;
                var d = inv * (p.transform.position - centre);
                if (Mathf.Abs(d.x) <= berth.Beam / 2 + 2 && Mathf.Abs(d.z) <= berth.Length / 2 + 2) return true;
            }
            return false;
        }
        // How much of its height a trader in port takes from its island's (the game lowers a far island by the island's own
        // distance, IslandHorizon): 1 at a berth or the anchorage, along a harbour leg the share of the way from the approach
        // point, 0 under way and waiting at the approach point (its own distance), so it never jumps between the two.
        internal static float PortWeight(Visual v)
        {
            switch (v.Phase)
            {
                case Phase.Moored: case Phase.Anchored: return 1f;
                case Phase.LegIn when v.Way == 3: return 1f;      // the direct way from the anchorage: in port all along
                case Phase.LegIn: return v.Leg != null && v.Leg.Length > 0 ? Mathf.Clamp01(v.U / v.Leg.Length) : 0f;
                case Phase.LegOut: return v.Leg != null && v.Leg.Length > 0 ? 1f - Mathf.Clamp01(v.U / v.Leg.Length) : 0f;
                default: return 0f;
            }
        }
        // At a berth the bow points the way it came in, along the berth.
        private static float BerthYaw(TraderBerth berth, float arriving) => Mathf.Abs(Mathf.DeltaAngle(arriving, berth.Heading)) <= 90 ? berth.Heading : berth.Heading + 180;
        private static Vector2 Across(Vector2 d) => new Vector2(d.y, -d.x);
        private static Vector2 AnchorPoint(Visual v, TraderHarbour h)
        {
            var into = -WindBlows(); float gap = v.Size.y + 4f;
            return h.Anchorage + Across(into) * (v.Slot - 1) * gap;
        }
        private static Vector2 WaitPoint(Visual v, TraderHarbour h) => h.Approach + Across(-WindBlows()) * (float)Rules.LaneShare(v.Boat.id) * 20f;

        // ---- Where it is, how it lies on the water ----
        private static void Pose(Visual v, float dt, float fdt, bool snap)
        {
            Vector2 at, dir; float half = 0, yawTarget, legTarget = float.NaN; bool held = false;
            var h = v.Port >= 0 ? LaneMap.HarbourOf(v.Port) : null;
            float span = Mathf.Max(8f, v.Size.x * .6f);
            switch (v.Phase)
            {
                case Phase.Sail: at = v.Path.At(v.S); dir = v.Path.Direction(v.S, span); half = LaneMap.HalfWidth(at); break;
                case Phase.LegOut: case Phase.LegIn:
                    {
                        at = v.Leg.At(v.U); dir = v.Leg.Direction(v.U, span);
                        // traders spread 20 m each side, none within 10-50 m of the berth, and no further than the water there allows
                        // (Rules.LegHalfWidth: the leg's room, the user's report at Dragon Cliffs' sea gate, 2026-10-20)
                        float fromBerth = v.Phase == Phase.LegIn && (v.Berth != null || v.Slot >= 0) ? v.Leg.Length - v.U : v.Phase == Phase.LegOut ? v.U : 999f;
                        float room = v.Leg.RoomAt(v.U);
                        half = (float)Rules.LegHalfWidth(fromBerth, room, v.Size.y);
                        v.Leg.WidthAt(v.U, out float wl, out float wr);
                        // round a player's ship in its way, as far as the water allows (TRADER_COLLISION_DRAFT.md, part 1; the room
                        // record's limit too, 3 m from rock); else in water under 40 m its starboard side 2 m from the edge (part 4a)
                        if (!float.IsNaN(v.Aside) && Time.time < v.AsideTill)
                        {
                            Rules.PortOffsets(fromBerth, wl, wr, v.Size.y / 2, out double min, out double max);
                            float lim = float.IsPositiveInfinity(room) ? float.PositiveInfinity : Mathf.Max(0f, room - v.Size.y / 2 - (float)Rules.LegRoomSpare);
                            legTarget = Mathf.Clamp(Mathf.Clamp(v.Aside, (float)min, (float)max), -lim, lim);
                        }
                        else if (Rules.KeepsRight(wl, wr)) legTarget = (float)Rules.KeepRight(fromBerth, wl, wr, v.Size.y);
                        break;
                    }
                case Phase.Moored: at = v.Berth.Centre; dir = DirOf(BerthYaw(v.Berth, v.Yaw)); held = true; break;
                case Phase.Anchored: at = h != null ? AnchorPoint(v, h) : v.Pos; dir = -WindBlows(); held = true; break;
                default: at = h != null ? WaitPoint(v, h) : v.Pos; dir = -WindBlows(); held = true; break;
            }
            yawTarget = YawOf(dir);
            // going back along its leg: stern first while the water is under 40 m wide, turned round once it is wider (part 4c)
            if (v.Back)
            {
                if (v.Astern && !TraderWays.BackAstern(v.Leg, v.U)) v.Astern = false;
                if (!v.Astern) yawTarget = YawOf(-dir);
            }
            // coming alongside: the last 30 m turn it along the berth
            else if (v.Phase == Phase.LegIn && v.Berth != null)
            {
                float left = v.Leg.Length - v.U;
                if (left < 30f) yawTarget = Mathf.LerpAngle(BerthYaw(v.Berth, yawTarget), yawTarget, left / 30f);
            }
            Vector2 pos;
            if (held) pos = snap ? at : Vector2.MoveTowards(v.Pos, at, 1.5f * dt);
            else
            {
                float target = !float.IsNaN(legTarget) ? legTarget : Mathf.Clamp((float)Rules.LaneShare(v.Boat.id) * half + v.Avoid, -half, half);
                if (v.Back) target = v.Offset;     // going back it keeps its place across the leg
                v.Offset = snap ? target : Mathf.MoveTowards(v.Offset, target, (1.5f + .2f * v.Speed) * dt);
                var aim = at + Across(dir) * v.Offset;
                pos = snap ? aim : Vector2.MoveTowards(v.Pos, aim, (v.Speed + 3f) * dt);
            }
            // beside a player's ship (part 2): the move loses its part toward it (under way only its part across: it still moves along,
            // and at sea shoves what lies ahead as before), and it does not turn (its ends would sweep); anchored or waiting it stops
            // swinging with the wind
            if (!snap && v.BesideCount > 0 && Time.time < v.BesideTill)
            {
                double mx = pos.x - v.Pos.x, mz = pos.y - v.Pos.y; var rb = Across(DirOf(v.Yaw));
                Vector2 Toward(Vector2 to) { if (held) return to; float side = Vector2.Dot(to, rb); return Mathf.Abs(side) > .3f ? rb * Mathf.Sign(side) : Vector2.zero; }
                for (int i = 0; i < v.BesideCount; i++)
                {
                    var t = Toward(v.Beside[i]);
                    if (t != Vector2.zero) Rules.WithoutToward(ref mx, ref mz, t.x, t.y);
                }
                // stored as floats it must not round toward a ship either (Rules.NoFloatToward); where it would, it stays this frame
                float px = v.Pos.x + (float)mx, pz = v.Pos.y + (float)mz; bool off = true;
                for (int i = 0; i < v.BesideCount; i++) { var t = Toward(v.Beside[i]); if (t != Vector2.zero) Rules.NoFloatToward(v.Pos.x, v.Pos.y, ref px, ref pz, t.x, t.y); }
                for (int i = 0; i < v.BesideCount; i++) { var t = Toward(v.Beside[i]); if (t != Vector2.zero && (double)(px - v.Pos.x) * t.x + (double)(pz - v.Pos.y) * t.y > 0) off = false; }
                pos = off ? new Vector2(px, pz) : v.Pos;
                yawTarget = v.Yaw;
                if (!held) v.Offset = Vector2.Dot(pos - at, Across(dir));   // its place across follows where it is
            }
            if (!snap && fdt > 0) v.Velocity = (pos - v.Pos) / fdt;
            v.Pos = pos;
            // its moves along and across the lane, its turn and its drift to a held place by the traders' seconds (dt): the same a metre
            // at any clock (the user's report of 2026-10-20: asleep it turned 2.8 times as fast while it sailed 9 times as fast, and
            // its hull swung into the cliffs at Dragon Cliffs); the velocity per real second (the body's push, Rules.CopySolid)
            v.Yaw = snap ? yawTarget : Mathf.MoveTowardsAngle(v.Yaw, yawTarget, (float)Rules.TurnRate(v.Class) * dt);
            // the water: four Crest samples (bow, stern, both sides) where the player can see the waves, else the sea level
            var player = PlayerReal(); float dist = (pos - player).magnitude;
            var f2 = DirOf(v.Yaw); var r2 = Across(f2);
            float sea = Crest.OceanRenderer.Instance ? Crest.OceanRenderer.Instance.SeaLevel : 0f;
            float hb = sea, hs = sea, hp = sea, hr = sea;
            if (dist < 1500f && Crest.OceanRenderer.Instance)
            {
                float lx = v.Size.x * .4f, bx = v.Size.y * .4f; var w = v.Copy.Water;
                hb = Sample(w[0], pos + f2 * lx, v.Water, 0, sea); hs = Sample(w[1], pos - f2 * lx, v.Water, 1, sea);
                hp = Sample(w[2], pos - r2 * bx, v.Water, 2, sea); hr = Sample(w[3], pos + r2 * bx, v.Water, 3, sea);
            }
            else for (int i = 0; i < 4; i++) v.Water[i] = float.NaN;   // not sampled: no stale height kept for later
            float mean = (hb + hs + hp + hr) / 4f;
            float pitchT = -Mathf.Atan2(hb - hs, v.Size.x * .8f) * Mathf.Rad2Deg, rollT = Mathf.Atan2(hr - hp, v.Size.y * .8f) * Mathf.Rad2Deg;
            // eased: under way its tilt over 0.25 s, its height at once; at a berth or anchored damped by hull size (Rules.TiltEase,
            // HeightEase: the user's feedback on 0.10.4, V3)
            bool still = v.Phase == Phase.Moored || v.Phase == Phase.Anchored;
            float k = snap ? 1f : 1f - Mathf.Exp(-fdt / (float)Rules.TiltEase(v.Class, still));
            v.Pitch += (pitchT - v.Pitch) * k; v.Roll += (rollT - v.Roll) * k;
            float heaveEase = (float)Rules.HeightEase(still);
            if (snap || heaveEase <= 0f || float.IsNaN(v.Heave)) v.Heave = mean;
            else v.Heave += (mean - v.Heave) * (1f - Mathf.Exp(-fdt / heaveEase));
            mean = v.Heave;
            float camY = Refs.ovrCameraRig ? Refs.ovrCameraRig.position.y : 2f;
            float drop = (float)Rules.HorizonDrop(dist, camY), weight = PortWeight(v);
            if (weight > 0f)
            {
                // in port it sinks with its island (the plan: "at a berth it moves with its island's height")
                var island = Island(v.Port);
                if (island)
                {
                    var c = island.GetPosition(); var me = Refs.observerMirror.transform.position;
                    float fromIsland = new Vector2(c.x - me.x, c.z - me.z).magnitude;
                    drop = Mathf.Lerp(drop, (float)Rules.HorizonDrop(fromIsland, camY, island.fullyEmergedDistance), weight);
                }
            }
            float y = mean + v.Draft - drop;
            v.TargetReal = new Vector3(pos.x, y, pos.y);
            v.TargetRot = Quaternion.Euler(v.Pitch, v.Yaw, v.Roll);
            if (snap) v.Placed = false;
        }
        // A Crest query that fails (its first after a move, a few in thousands: probe vt_hulls run 4) keeps the point's last good
        // height, else the sea level.
        private static float Sample(Crest.SampleHeightHelper helper, Vector2 real, float[] last, int i, float sea)
        {
            helper.Init(Shift(real), .5f, false, null);
            if (helper.Sample(out float h)) { last[i] = h; return h; }
            return float.IsNaN(last[i]) ? sea : last[i];
        }

        // Sails unfurled under way and furled while it waits (the user's rule), 0.15 a traders' second as the game's NPC boats; set to
        // leeward. Under a fast clock (Rules.SailingSpeed, ClothWorldScale) the cloth sees the wind and the hull's movement as at the
        // game's own speed (the user's report of 2026-10-20: asleep the sails billowed as in a gale and furled at their own pace).
        private static void Sails(Visual v, float dt, float fdt)
        {
            // (waiting on its harbour leg for its way to clear it furls them too: TRADER_COLLISION_DRAFT.md, part 1)
            bool moving = v.Phase == Phase.Sail || (v.Phase == Phase.LegOut || v.Phase == Phase.LegIn) && v.HoldAt < 0;
            v.Unroll = Mathf.MoveTowards(v.Unroll, moving ? 1f : 0f, (float)Rules.FurlRate * dt);
            // furled in port its reef animators culled, always animating again as it sets out (TraderHulls.ParkSails)
            if (!moving && v.Unroll < .04f)
            {
                if (v.FurledSince < 0) v.FurledSince = Time.time;
                else if (!v.SailsParked && Time.time - v.FurledSince > 1f && v.ShowStep >= TraderHulls.ShowSteps(v.Copy)) { TraderHulls.ParkSails(v.Copy); v.SailsParked = true; }
            }
            else
            {
                v.FurledSince = -1;
                if (v.SailsParked) { TraderHulls.UnparkSails(v.Copy); v.SailsParked = false; }
            }
            int side = v.Unroll > .02f && moving ? SailSide(v) : v.Unroll > .5f ? v.Side : 0;
            v.Side = side;
            TraderHulls.PoseSails(v.Copy, side, 1f - Mathf.Exp(-dt / .8f));
            float clock = fdt > 0 ? dt / fdt : 0f;   // the traders' seconds a real second
            var through = v.Velocity * (float)Rules.SailingSpeed(1, clock);
            var wind = new Vector3(WindBlows().x, 0, WindBlows().y) * v.Wind - new Vector3(through.x, 0, through.y);
            TraderHulls.ClothClock(v.Copy, clock);
            foreach (var s in v.Copy.Sails) if (s) { s.currentUnroll = v.Unroll; s.apparentWind = wind; }
        }
        // +1 the wind over the port side (the sails out to starboard), -1 over the starboard side.
        private static int SailSide(Visual v)
        {
            var r = Across(DirOf(v.Yaw));
            return Vector2.Dot(WindBlows(), r) >= 0 ? 1 : -1;
        }

        // ---- Lanterns: the street lights' hours, or in a storm's rain (the user's feedback on 0.10.4, point 8) ----
        // The game's storms are its WanderingStorm objects (Better Storm sets their radius, Climate reads them); the weather turns to
        // rain within the radius plus 0.33 of the region's storm range (WeatherStorms.ApplyStorm). Read twice a second.
        private static readonly List<Vector3> stormAt = new List<Vector3>();
        private static readonly List<float> stormRadius = new List<float>();
        private static float stormRange;
        private static System.Reflection.FieldInfo stormsField;
        private static void ReadStorms()
        {
            stormAt.Clear(); stormRadius.Clear();
            var ws = WeatherStorms.instance; if (!ws) return;
            if (stormsField == null) stormsField = AccessTools.Field(typeof(WeatherStorms), "storms");
            var storms = stormsField?.GetValue(ws) as WanderingStorm[]; if (storms == null) return;
            stormRange = Weather.instance && Weather.instance.currentRegion ? Weather.instance.currentRegion.stormRange : 0f;
            foreach (var s in storms)
            {
                if (!s || !s.active) continue;
                stormAt.Add(s.transform.position); stormRadius.Add(s.GetRadius());
            }
        }
        private static bool Lit(Vector2 real)
        {
            bool storm = false;
            if (stormAt.Count > 0)
            {
                var at = Shift(real);
                for (int i = 0; i < stormAt.Count && !storm; i++) storm = Rules.InStormRain(new Vector2(stormAt[i].x - at.x, stormAt[i].z - at.z).magnitude, stormRadius[i], stormRange);
            }
            return Rules.LanternsLit(Sun.sun ? Sun.sun.localTime : 12f, storm);
        }

        // ---- The wind at its place (question 12: the actual wind there) ----
        // The player's wind target with its ocean part worked out at the trader's own distance from land (Chaotic Wind's rule
        // when it is installed), times the gust of the moment (the player's wind over its target); storms as at the player.
        // shortcut: a storm counts only as it changes the player's wind (within 3 km a trader is near the player), not by its
        // own place; replace with Better Storm's storm wind by place if its code gives one (the draft's question 12, left open).
        private static float Strength(Vector2 at)
        {
            float wind = global::Wind.currentWind.magnitude;
            if (!global::Wind.instance) return wind;
            if (windTarget == null) windTarget = AccessTools.Field(typeof(global::Wind), "currentWindTarget");
            float target = windTarget != null ? ((Vector3)windTarget.GetValue(global::Wind.instance)).magnitude : 0f;
            float dp = GameState.distanceToLand;
            if (target <= .01f || dp >= 999999f) return wind;
            float baseWind = global::Wind.currentBaseWind.magnitude, dt = LandDistance(at);
            float mine = target - baseWind * .66f * Ocean(dp) + baseWind * .66f * Ocean(dt);
            return Mathf.Max(0f, mine) * wind / target;
        }
        private static float Ocean(float distance)
        {
            LookForChaoticWind();
            if (chaoticLerp != null) { try { return (float)chaoticLerp.Invoke(null, new object[] { 1500f, 4000f, distance }); } catch { chaoticLerp = null; } }
            return (float)Rules.OceanLerp(distance);
        }
        // Chaotic Wind's ocean rule, looked up once: AccessTools.TypeByName goes through every loaded assembly's types, 10.89 ms in
        // the frame the first trader was drawn (vt-22, FrameParts' "wind"); so it is done at the start menu with the templates
        // (TraderHulls.Capture), and here only if that did not run.
        internal static void LookForChaoticWind()
        {
            if (chaoticLooked) return;
            chaoticLooked = true;
            var type = AccessTools.TypeByName("ChaoticWind.ChaoticWindPlugin");
            chaoticLerp = type != null ? AccessTools.Method(type, "GetConfiguredOceanLerp", new[] { typeof(float), typeof(float), typeof(float) }) : null;
        }
        // Its distance to the nearest island's centre (the game's IslandDistanceTracker measure).
        private static float LandDistance(Vector2 at)
        {
            if (!IslandDistanceTracker.instance) return 999999f;
            var s = Shift(at); float best = 999999f;
            foreach (var i in IslandDistanceTracker.instance.islands)
            {
                if (!i) continue; var c = i.GetPosition();
                float d = new Vector2(c.x - s.x, c.z - s.z).magnitude; if (d < best) best = d;
            }
            return best;
        }

        // ---- Contacts (within 1 km; the user's rules, questions 2 and 16) ----
        // A box ahead of the bow, 4 times a second: the player, the player's ships, another trader, a game NPC boat or another mod's
        // ship in it, and the trader turns aside (its place across the lane moves to the far side); if they still meet, it is a wall
        // to the player's ships and passes through the others.
        // ahead: under way (the box ahead and turning aside); in every phase the box round its own hull (3 m clear), so a ship that is
        // not the player's passes through it whichever way they meet, at a berth too (question 16).
        // Since 2026-10-21 (TRADER_COLLISION_DRAFT.md): the player is not seen, only the player's ships (the user: "Player model shouldn't
        // be recognized by trader, only player ship"; the copy's hull stays solid to the swimming player, K5 (a)); on a harbour leg
        // the player's ships ahead are the ways check's (Ways: round them or stop short), at sea as before (C2: "keep the ahead as
        // now"); and in every phase, at any distance, the player's ships within 3 m of its hull are noted for the beside rule (Pose).
        private static void Contacts(Visual v, bool ahead)
        {
            v.NextContact = Time.time + .25f;
            Beside(v);
            var player = PlayerReal();
            if ((v.Pos - player).magnitude > Rules.TraderNear) return;
            var f2 = DirOf(v.Yaw); var turn = Quaternion.Euler(0, v.Yaw, 0);
            bool inPort = v.Phase == Phase.LegIn || v.Phase == Phase.LegOut;
            if (ahead)
            {
                float look = Mathf.Max(20f, v.Speed * 8f);
                var centre = Shift(v.Pos + f2 * (v.Size.x * .5f + look * .5f), 2f);
                int n = Physics.OverlapBoxNonAlloc(centre, new Vector3(v.Size.y / 2 + 3f, 8f, look / 2), found, turn, ~0, QueryTriggerInteraction.Ignore);
                float side = 0; bool any = false; var me = Shift(v.Pos); var right = new Vector3(Across(f2).x, 0, Across(f2).y);
                for (int i = 0; i < n; i++)
                {
                    var c = found[i]; if (!c) continue;
                    var body = c.attachedRigidbody;
                    if (body && body == v.Copy.Body) continue;
                    bool trader = body && TraderHulls.IsCopy(body);
                    var ship = trader ? null : ShipOf(c);
                    if (!trader && !ship) continue;
                    if (ship) Meet(v, ship);
                    if (ship && inPort && PlayersShip(ship)) continue;
                    float s = Vector3.Dot(c.bounds.center - me, right);
                    if (!any || Mathf.Abs(s) < Mathf.Abs(side)) side = s;
                    any = true;
                }
                if (any)
                {
                    // away from it; head-on, to starboard (as ships keep right)
                    float away = Mathf.Abs(side) < 1f ? 1f : -Mathf.Sign(side);
                    v.Avoid = away * Mathf.Max(v.Size.y * 2f, 12f);
                    v.AvoidTill = Time.time + 6f;
                }
                else if (Time.time > v.AvoidTill) v.Avoid = Mathf.MoveTowards(v.Avoid, 0f, 1f);
            }
            int m = Physics.OverlapBoxNonAlloc(Shift(v.Pos, 2f), new Vector3(v.Size.y / 2 + 3f, 8f, v.Size.x / 2 + 3f), found, turn, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < m; i++)
            {
                var c = found[i]; if (!c) continue;
                var body = c.attachedRigidbody;
                if (body && (body == v.Copy.Body || TraderHulls.IsCopy(body))) continue;
                var ship = ShipOf(c); if (ship) Meet(v, ship);
            }
        }
        // The player's ships within 3 m of its hull (part 2, the beside rule; K4 (a): at sea too): the way toward each, hull to hull
        // (Rules.BoxApart), kept until the next look.
        private static void Beside(Visual v)
        {
            v.BesideCount = 0; v.BesideTill = Time.time + .5f;
            var hulls = PlayerHulls(); if (hulls.Count == 0) return;
            var me = BoxOf(v);
            foreach (var s in hulls)
            {
                if (v.BesideCount >= v.Beside.Length || !Rules.BoxesOverlap(me, s, Rules.BesideGap)) continue;
                Rules.BoxApart(me, s, out double tx, out double tz);
                v.Beside[v.BesideCount++] = new Vector2((float)tx, (float)tz);
            }
        }
        // The ship a collider belongs to: the nearest parent with PurchasableBoat, BoatRefs or NPCBoatController (a sail or boom has a
        // body of its own, hinged to the hull: its collider's own body is not the ship; the user's feedback on 0.10.4, point 1), or
        // a Boat-tagged collider's body; null for anything else.
        // Only colliders on a body can be a ship's (docks and land have none); each body's ship is looked up once.
        private static readonly Dictionary<int, Transform> shipOfBody = new Dictionary<int, Transform>();
        private static Transform ShipOf(Collider c)
        {
            var body = c.attachedRigidbody; if (!body) return null;
            int id = body.GetInstanceID();
            if (shipOfBody.TryGetValue(id, out var known) && (known || (object)known == null)) return known;
            Transform ship = null;
            for (var t = body.transform; t && t != Refs.shiftingWorld && !ship; t = t.parent)
                if (t.GetComponent<PurchasableBoat>() || t.GetComponent<BoatRefs>() || t.GetComponent<NPCBoatController>()) ship = t;
            if (!ship && c.CompareTag("Boat")) ship = body.transform;
            if (shipOfBody.Count > 4096) shipOfBody.Clear();
            shipOfBody[id] = ship;
            return ship;
        }
        // A ship met: the player's is a wall (the user's rule), any other passes through, every collider of it (its sails and booms
        // too); a ship passed through before that is the player's now (bought since, or the player aboard) is a wall again.
        private static void Meet(Visual v, Transform ship)
        {
            if (!PlayersShip(ship)) IgnoreWith(v.Copy, ship);
            else if (v.Copy.Ignored.Remove(ship.GetInstanceID()))
            {
                var theirs = ship.GetComponentsInChildren<Collider>();
                foreach (var a in v.Copy.Colliders)
                    if (a && a.enabled && a.gameObject.activeInHierarchy)
                        foreach (var t in theirs) if (t && t.enabled && !t.isTrigger) Physics.IgnoreCollision(a, t, false);
            }
        }
        internal static bool PlayersShip(Transform ship)
        {
            var boat = ship.GetComponent<PurchasableBoat>();
            if (boat && boat.isPurchased()) return true;
            var on = GameState.currentBoat;
            return on && (on == ship || on.IsChildOf(ship) || ship.IsChildOf(on));
        }
        // Set at every look (a ship for sale within 300 m every 2 s, a ship in a contact box four times a second), each pair of the
        // colliders switched on now (Unity refuses a pair with one switched off): Unity forgets a pair's pass-through whenever one
        // of the two is switched off, and the game switches a ship's colliders off and on itself (the whole ship by distance, a
        // moored rope end's as it moors, PickupableBoatMooringRope). Was: set once and marked, again only after a drawn trader saw
        // the ship switched off, so a ship switched off and on while none was drawn (the player off at sea) or a part switched off
        // and on kept a stale mark, and a copy could shove the ship (the user's 0.10.4 report; vtsale-1: a trader 176 m from the
        // Sanbuq for sale had all its pairs only 15 s after it was drawn; the deep review of 2026-10-19, Claude's fix). Most of a
        // ship's and a copy's colliders are switched off (the Sanbuq for sale 5 of 60 on, a Sanbuq copy 4), so a look is ~20 calls.
        // The mark (Ignored) stays for Meet: a ship passed through that is the player's now is made solid again.
        private static void IgnoreWith(TraderCopy copy, Transform ship)
        {
            copy.Ignored[ship.GetInstanceID()] = Time.time;
            var theirs = ship.GetComponentsInChildren<Collider>();
            foreach (var a in copy.Colliders)
                if (a && a.enabled && a.gameObject.activeInHierarchy)
                    foreach (var t in theirs) if (t && t.enabled && !t.isTrigger) Physics.IgnoreCollision(a, t, true);
        }
        // The ships for sale near a drawn trader pass through it before they meet (the user's feedback on 0.10.4, point 1: "make
        // trader boat don't have collision with unpurchased player boat"): within 300 m, set when it is drawn and every 2 s.
        private static readonly List<PurchasableBoat> saleShips = new List<PurchasableBoat>();
        private static float nextRegistered;
        // The game's registered ships (its saveable objects with PurchasableBoat, not houses), bought or not; looked up again every
        // 2 s and at a load (was: when the registry's length changed, which it never does: 255, or 2,048 with Shipwright, so a ship
        // registered later, a mod's or a load's, was never seen: the deep review of 2026-10-19).
        private static List<PurchasableBoat> RegisteredShips()
        {
            var objects = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentObjects() : null;
            if (objects == null) { saleShips.Clear(); nextRegistered = 0; return saleShips; }
            if (Time.realtimeSinceStartup >= nextRegistered)
            {
                nextRegistered = Time.realtimeSinceStartup + 2f; saleShips.Clear();
                foreach (var o in objects) { var p = o ? o.GetComponent<PurchasableBoat>() : null; if (p && !p.isHouse) saleShips.Add(p); }
            }
            return saleShips;
        }
        private static void SaleShips(Visual v)
        {
            v.NextShips = Time.time + 2f;
            if (!SaveLoadManager.instance || SaveLoadManager.instance.GetCurrentObjects() == null) return;
            var me = Shift(v.Pos);
            foreach (var p in RegisteredShips())
            {
                if (!p || !p.gameObject.activeInHierarchy || PlayersShip(p.transform)) continue;
                var d = p.transform.position - me; d.y = 0;
                if (d.sqrMagnitude < 300f * 300f) IgnoreWith(v.Copy, p.transform);
            }
        }
    }

    // The visible traders' own time a frame, split by part (the user, 2026-10-19: "Investigate the 18-23ms frame"): a clock read at
    // each part's start and end. The whole Update counts, the hull templates' build steps and the goods' looks too (the older
    // MaxFrameMs starts after them). The largest frame's parts and the 20 largest frames over SlowMs (vt-23: the first 60 were
    // all the start's and the test's own rebuilds, so a later 73 ms frame went unnamed) are kept for the QA to log, with how
    // many frames passed SlowMs; nothing is logged by the mod itself. Parts may hold others (the scan holds its places, releases and
    // lanterns and cargo; a trader's step its show steps, wind, trips, berths, ships for sale, contacts, pose and sails).
    internal static class FrameParts
    {
        internal const int Build = 0, Looks = 1, Npc = 2, Scan = 3, ScanPlaces = 4, Release = 5, LitCargo = 6, Begin = 7, Cloth = 8,
            Show = 9, Wind = 10, Trips = 11, Berths = 12, SaleShips = 13, Contacts = 14, Pose = 15, Sails = 16, Templates = 17;
        private static readonly string[] names = { "build", "looks", "npc boats", "scan", "scan places", "release", "lanterns and cargo", "begin", "cloth",
            "show steps", "wind", "trips", "berths", "sale ships", "contacts", "pose", "sails", "templates" };
        private static readonly double[] cur = new double[names.Length];
        internal const double SlowMs = 8;
        internal static double LargestMs; internal static string Largest = ""; internal static int SlowCount;
        private static readonly List<KeyValuePair<double, string>> slow = new List<KeyValuePair<double, string>>();
        // the 20 largest frames over SlowMs without their build steps, largest first
        internal static IEnumerable<string> Slow => slow.OrderByDescending(x => x.Key).Select(x => x.Value);
        internal static long Now => Stopwatch.GetTimestamp();
        internal static void Add(int part, long since) => cur[part] += (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;
        internal static void End(long since, int drawn)
        {
            double whole = (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;
            if (whole > SlowMs) SlowCount++;
            // ranked without the templates and the copies' build steps (made at the start menu; the copies again only when the
            // setting is switched on)
            double rank = whole - cur[Build] - cur[Templates], least = SlowMs;
            if (rank > SlowMs && slow.Count >= 20) { least = slow[0].Key; foreach (var x in slow) if (x.Key < least) least = x.Key; }
            if (whole > LargestMs || rank > least)
            {
                var s = new System.Text.StringBuilder();
                s.Append(whole.ToString("F2")).Append(" ms at ").Append(Time.realtimeSinceStartup.ToString("F1")).Append(" s, ").Append(drawn).Append(" drawn:");
                for (int i = 0; i < cur.Length; i++) if (cur[i] >= .05) s.Append(' ').Append(names[i]).Append(' ').Append(cur[i].ToString("F2")).Append(',');
                string line = s.ToString().TrimEnd(',');
                if (whole > LargestMs) { LargestMs = whole; Largest = line; }
                if (rank > least)
                {
                    if (slow.Count >= 20) slow.RemoveAt(slow.FindIndex(x => x.Key == least));
                    slow.Add(new KeyValuePair<double, string>(rank, line));
                }
            }
            Array.Clear(cur, 0, cur.Length);
        }
    }

    // The game's NPC boats stay off while visible traders are on (the user's feedback on 0.10.4, V7): the game's distance switcher
    // would switch them on again within 3 km of the player.
    [HarmonyPatch(typeof(BoatHorizonPerformanceSwitcher), "SetHidden")] internal static class NpcBoatHiddenPatch
    {
        static void Prefix(BoatHorizon horizon, ref bool newState)
        {
            if (!newState || !VisibleTraders.NpcBoatsHidden || !horizon || !horizon.transform.parent) return;
            var boat = horizon.transform.parent;
            if (boat.parent == Refs.shiftingWorld && (boat.GetComponent<NPCBoatController>() || boat.GetComponent<NPCFishingBoat>()) && !VisibleTraders.OtherModsBoat(boat)) newState = false;
        }
    }
    // The game's cloth resets skipped on the traders' copies (the user, 2026-10-19: "3. skip"; vt-29's VT TURNFRAMES: turning the
    // view away from 7 drawn traders gave frames of 32.9 and 86.2 ms, the same turn with the traders off none over 10.1 ms).
    // WindCloth.Update switches its cloth off and on the first frame its sail is out of sight after 3 s in it: a copy's stays
    // marked done. The game's own ships keep both resets.
    [HarmonyPatch(typeof(WindCloth), "Update")] internal static class WindClothResetPatch
    {
        static void Prefix(WindCloth __instance, ref bool ___refreshed, ref float ___refreshTimer)
        {
            if (TraderHulls.IsCopy(__instance)) { ___refreshed = true; ___refreshTimer = 0f; }
        }
    }
    // ReefEffectAnimUniversal.RefreshCloth (from its Start, the game's Furl, NANDFixes' Unfurl) switches the cloth off, plays the
    // set pose, and a frame later switches the cloth on and plays the reef pose again: skipped whole on a copy. Its cloth stays on,
    // following the reef animator as it moves (Update plays the pose of its unroll each frame); the set pose's jump and back with
    // the cloth on would fling it.
    [HarmonyPatch(typeof(ReefEffectAnimUniversal), "RefreshCloth")] internal static class ReefClothResetPatch
    {
        static bool Prefix(ReefEffectAnimUniversal __instance) => !TraderHulls.IsCopy(__instance);
    }
    [HarmonyPatch(typeof(StartMenu), "StartNewGame")] internal static class TraderTemplatesNewGamePatch { [HarmonyPriority(Priority.First)] static void Prefix() => TraderHulls.Capture(); }
    [HarmonyPatch(typeof(SaveLoadManager), "LoadGame")] internal static class TraderTemplatesLoadPatch { [HarmonyPriority(Priority.First)] static void Prefix() => TraderHulls.Capture(); }

    // ---- A trader speaks when a player's ship hits it (TRADER_DIALOGUE_DRAFT.md, the user, 2026-10-21) ----
    // A bubble over the trader's helmsman: its lower edge 2.3 m over his feet ("probally 2.3 meter height over his feet"), the
    // tavern bubble 5 times over ("10m looks like 2m from tavern"), one size in the world, drawn over everything ("keep it not hide
    // by both ship and land"), turned to the view each frame as the game turns the tavern's (TavernRumorsDude.Update). It shows at
    // the first contact between a player's ship and the trader, at any speed, whoever moved (the game's contact on the ship's body,
    // BoatImpactSoundsCollider.OnCollisionEnter, before its own speed checks), until the player is 60 m from the trader's hull
    // ("Change the bubble fade out to 60m"), then shrinks away with the game's tween; a contact after that gives a new one. One of
    // the user's 17 lines (word for word) at random, never the trader's last (Claude's choice), on one row, the bubble as long as
    // it needs (Layout). Nothing saved.
    internal static class TraderBubble
    {
        internal static readonly string[] Lines = {
            "I shouldn't skip that insurance...",
            "OI! WHAT YE DOING?!",
            "EVEN ME GRANDMA SAIL BETTER THAN YE!",
            "GET OUT OF THE DAMN WAY!",
            "(Staring at you menacingly)",
            "BY POSEIDON'S BARNACLED BALLS!!",
            "YOU DRANK TOO MUCH BILGE??",
            "Shiver me timber! A Landlubber!",
            "YE 'AVE OAKUM IN YE EYE??",
            "Your approach lacks... subtlety.",
            "Remarkable! All this water, and yet here you are...",
            "... I see you\u2019ve chosen the direct route.",
            "... A most intimate maneuver.",
            "Do you generally navigate by impact?",
            "An ambitious approach.",
            "A bold maneuver. Not a good one, but certainly bold.",
            "YE BLIND SWINE! KEEP OFF!" };
        internal const float Times = 5f, LowerEdge = 2.3f, Away = 60f, ShrinkTime = .25f;
        internal const float TavernWidth = 1.10f, TavernHeight = .38f;   // the tavern backing as drawn (TRADER_DIALOGUE_DRAFT.md; vtn-2 to vtn-5)
        // The tavern bubble's look, from the first rumour NPC loaded, by its field TavernRumorsDude.speechUI (never by name: Talkative
        // NPCs adds a second "speech ui"): the backing's mesh and colour, the text's font, size and colour, both placed as there.
        private static bool taken; private static Mesh backMesh; private static Color backColour = new Color(.95f, .93f, .86f), textColour = Color.black;
        private static Font font; private static int fontSize; private static float characterSize = 1f, lineSpacing = 1f;
        private static TextAnchor anchor = TextAnchor.MiddleCenter; private static TextAlignment alignment = TextAlignment.Center;
        private static Vector3 backAt, textAt, backScale = Vector3.one, textScale = Vector3.one;
        private static Quaternion backTurn = Quaternion.identity, textTurn = Quaternion.identity;
        private static float halfHeight = .19f, halfWidth = .55f;   // the tavern backing's (1.10 x 0.38 m) until taken
        private static Material backMaterial, textMaterial; private static Texture2D white, rounded;
        internal static string Look { get; private set; } = "none yet";
        internal static bool KeepForTest;   // the picture test: bubbles shown far from the player stay
        private static readonly Dictionary<int, int> lastLine = new Dictionary<int, int>();
        private static readonly HashSet<int> noted = new HashSet<int>();
        internal static void TakeLook(TavernRumorsDude dude)
        {
            if (taken || !dude || !dude.speechUI || !dude.text || !dude.text.font) return;
            var su = dude.speechUI.transform; var inv = Quaternion.Inverse(su.rotation);
            var text = dude.text; var textR = text.GetComponent<Renderer>();
            var back = su.GetComponentsInChildren<MeshRenderer>(true).Where(r => r != textR && !r.GetComponent<TextMesh>() && r.GetComponent<MeshFilter>() && r.GetComponent<MeshFilter>().sharedMesh)
                         .OrderByDescending(r => r.GetComponent<MeshFilter>().sharedMesh.name.IndexOf("bubble", StringComparison.OrdinalIgnoreCase) >= 0).ThenByDescending(r => r.GetComponent<MeshFilter>().sharedMesh.bounds.size.sqrMagnitude).FirstOrDefault();
            if (!back) return;
            taken = true;
            var mesh = back.GetComponent<MeshFilter>().sharedMesh;
            if (mesh.isReadable) { backMesh = Object.Instantiate(mesh); backMesh.colors = Enumerable.Repeat(Color.white, backMesh.vertexCount).ToArray(); }
            if (back.sharedMaterial && back.sharedMaterial.HasProperty("_Color")) backColour = back.sharedMaterial.color;
            backColour.a = 1f;
            font = text.font; fontSize = text.fontSize; characterSize = text.characterSize; lineSpacing = text.lineSpacing; anchor = text.anchor; alignment = text.alignment; textColour = text.color;
            var centre = inv * (back.transform.TransformPoint(mesh.bounds.center) - su.position);
            backAt = inv * (back.transform.position - su.position) - centre; backTurn = inv * back.transform.rotation; backScale = back.transform.lossyScale;
            textAt = inv * (text.transform.position - su.position) - centre; textTurn = inv * text.transform.rotation; textScale = text.transform.lossyScale;
            // its width and height: the plate's two longest edges in the world, through its whole matrix (vtn-3 and vtn-4 read 0.50 x
            // 0.20 m through lossyScale, which a turned part under a parent stretched unevenly gets wrong; its drawn box at the tavern
            // is about 1.1 by 0.38 m, the draft's 1.10 x 0.38)
            var m4 = back.transform.localToWorldMatrix; var bs = mesh.bounds.size;
            var dims = new[] { m4.MultiplyVector(new Vector3(bs.x, 0, 0)).magnitude, m4.MultiplyVector(new Vector3(0, bs.y, 0)).magnitude, m4.MultiplyVector(new Vector3(0, 0, bs.z)).magnitude }.OrderByDescending(d => d).ToArray();
            // ... but when it wakes the tavern's backing reads smaller than it is drawn (vtn-5: 0.50 x 0.20 m read here, its drawn box 0.38 m
            // tall in every run), its text not (vtn-6: scaled with it, the text came out twice the tavern's against its backing): the
            // backing takes the size it is drawn at (the draft's 1.10 x 0.38 m, the same at all 18 taverns), the text its own
            float k = dims[1] > 1e-4f ? TavernHeight / dims[1] : 1f;
            backAt *= k; backScale *= k;
            halfWidth = TavernWidth / 2f; halfHeight = TavernHeight / 2f;
            backMaterial = textMaterial = null;
            Look = "the tavern's (port " + dude.portIndex + ": backing " + mesh.name + (mesh.isReadable ? "" : " not readable, a plain shape instead") + " read " + dims[0].ToString("F2") + " x " + dims[1].ToString("F2") + " m on waking (x" + k.ToString("F2") + "), drawn " + (2 * halfWidth).ToString("F2") + " x " + (2 * halfHeight).ToString("F2") +
                   " m, colour " + backColour + "; font " + font.name + " size " + fontSize + " x " + characterSize + ", text scale " + textScale.x.ToString("F3") + ", text colour " + textColour + ")";
            Plugin.Log?.LogInfo("Trader bubble: the look taken, " + Look + ".");
        }
        // Before any tavern is loaded (a save loaded at sea): a plain bubble made in code, with the game's notice font (Claude's pick).
        private static void Materials()
        {
            if (backMaterial && textMaterial) return;
            if (!font)
            {
                var notice = NotificationUi.instance ? NotificationUi.instance.GetComponentsInChildren<TextMesh>(true).FirstOrDefault(t => t.font) : null;
                font = notice ? notice.font : Resources.GetBuiltinResource<Font>("Arial.ttf");
                fontSize = 64; characterSize = .0115f; textScale = Vector3.one; textColour = Color.black; anchor = TextAnchor.MiddleCenter; alignment = TextAlignment.Center;
                Look = "plain (no tavern loaded yet; font " + (font ? font.name : "none") + ")";
            }
            // the game's text shader (GUI/Text Shader: drawn over everything, ZTest Always) for both, the backing just before its text
            var shader = font && font.material ? font.material.shader : Shader.Find("GUI/Text Shader");
            if (!white) { white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply(); }
            if (!rounded) rounded = RoundedMask(128, 48, 20);
            backMaterial = new Material(shader) { name = "trader bubble backing", mainTexture = backMesh ? (Texture)white : rounded, color = backColour, renderQueue = 3100 };
            textMaterial = new Material(font.material) { name = "trader bubble text", renderQueue = 3101 };
        }
        private static Texture2D RoundedMask(int w, int h, int r)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                float dx = Mathf.Max(0, Mathf.Max(r - x - .5f, x + .5f - (w - r))), dy = Mathf.Max(0, Mathf.Max(r - y - .5f, y + .5f - (h - r)));
                t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(r + .5f - Mathf.Sqrt(dx * dx + dy * dy))));
            }
            t.Apply(); return t;
        }
        private static Mesh quad;
        private static Mesh Quad()
        {
            if (quad) return quad;
            quad = new Mesh { name = "trader bubble plain", vertices = new[] { new Vector3(-1, -1), new Vector3(1, -1), new Vector3(-1, 1), new Vector3(1, 1) },
                              uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) }, colors = Enumerable.Repeat(Color.white, 4).ToArray(), triangles = new[] { 0, 2, 1, 1, 2, 3 } };
            quad.RecalculateBounds(); return quad;
        }
        // A new bubble on a drawn trader, its line chosen; `line` set: that line (the tests).
        internal static void Show(TraderCopy c, int boatId, int line = -1)
        {
            if (c == null || !c.Go) return;
            if (c.Bubble) Object.Destroy(c.Bubble);
            Materials();
            if (line < 0) line = Rules.PickLine(Lines.Length, lastLine.TryGetValue(boatId, out int last) ? last : -1, UnityEngine.Random.value * .999999);
            lastLine[boatId] = line;
            var root = new GameObject("trader bubble"); root.transform.SetParent(c.Go.transform, false);
            var helm = c.Go.transform.Find("helmsman");
            var feet = helm ? helm.localPosition : Vector3.up * 3f;
            root.transform.localPosition = feet + Vector3.up * (LowerEdge + halfHeight * Times);
            var back = new GameObject("backing"); back.transform.SetParent(root.transform, false);
            back.AddComponent<MeshFilter>().sharedMesh = backMesh ? backMesh : Quad();
            if (backMesh) { back.transform.localPosition = backAt * Times; back.transform.localRotation = backTurn; back.transform.localScale = backScale * Times; }
            else back.transform.localScale = new Vector3(halfWidth * Times, halfHeight * Times, 1f);   // the tavern's size 5 times over (the quad is 2 x 2)
            var br = back.AddComponent<MeshRenderer>(); br.sharedMaterial = backMaterial;
            var words = new GameObject("text"); words.transform.SetParent(root.transform, false);
            // with the game's backing the text placed as in the tavern; on the plain shape at its centre (vtn-2: the tavern's offset, made
            // for its own mesh, put the text off the shape)
            if (backMesh) { words.transform.localPosition = textAt * Times; words.transform.localRotation = textTurn; }
            else { anchor = TextAnchor.MiddleCenter; alignment = TextAlignment.Center; }
            words.transform.localScale = textScale * Times;
            var tm = words.AddComponent<TextMesh>(); tm.font = font; tm.fontSize = fontSize; tm.characterSize = characterSize; tm.lineSpacing = lineSpacing; tm.anchor = anchor; tm.alignment = alignment; tm.color = textColour; tm.richText = false;
            tm.text = Layout(Lines[line], out float width);
            if (!backMesh) back.transform.localScale = new Vector3(width / 2f, halfHeight * Times, 1f);
            var tr = words.GetComponent<MeshRenderer>(); tr.sharedMaterial = textMaterial;
            foreach (var r in new Renderer[] { br, tr }) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false; r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off; }
            root.layer = back.layer = words.layer = 0;
            if (Refs.observerMirror) root.transform.rotation = Refs.observerMirror.transform.rotation;
            c.Bubble = root; c.BubbleGone = 0; c.BubbleLine = line;
            if (Juicebox.juice) { root.transform.localScale = Vector3.one * .9f; Juicebox.juice.TweenScale(root, Vector3.one, .1f); }
            Plugin.Log?.LogInfo("Trader bubble: trader " + boatId + " says line " + (line + 1) + ".");
        }
        // A text's width in metres as drawn at `times` the tavern's: the font's own advances, a TextMesh drawing characterSize / 10 of a
        // unit a font pixel, times its scale; its widest row.
        internal static float TextMetres(string text, float times)
        {
            Materials();
            if (!font || string.IsNullOrEmpty(text)) return 0f;
            font.RequestCharactersInTexture(text, fontSize, FontStyle.Normal);
            float widest = 0;
            foreach (var row in text.Split('\n'))
            {
                float w = 0;
                foreach (char ch in row) if (font.GetCharacterInfo(ch, out var ci, fontSize, FontStyle.Normal)) w += ci.advance;
                widest = Mathf.Max(widest, w);
            }
            return widest * characterSize * .1f * textScale.x * times;
        }
        // The user, 2026-10-21 (after vtn-8): "i saw the text only have 1 or 2 word at 2nd line, i don't want that, either make it average
        // or fit in 1 line, you can make the bubble longer": a line is drawn on one row, the bubble as long as it needs (its text 85% of
        // the width, never narrower than the tavern's 5 times over); only a row longer than twice that width splits into two rows of
        // even width (Rules.EvenRows). The text keeps the tavern's size.
        internal const float TextShare = .85f, MostWidth = 2f;
        internal static string Layout(string line, out float width)
        {
            float baseWidth = 2f * halfWidth * Times;
            string text = line;
            if (TextMetres(line, Times) > MostWidth * baseWidth * TextShare) text = Rules.EvenRows(line, t => TextMetres(t, Times));
            width = Mathf.Max(baseWidth, TextMetres(text, Times) / TextShare);
            return text;
        }
        // A contact on a ship's body (the game's impact listener): a trader copy met by a player's ship.
        internal static void Contact(Transform at, Rigidbody body)
        {
            Transform ship = null;
            for (var t = at; t && t != Refs.shiftingWorld && !ship; t = t.parent) if (t.GetComponent<PurchasableBoat>() || t.GetComponent<BoatRefs>()) ship = t;
            if (!ship || !VisibleTraders.PlayersShip(ship)) { if (noted.Add(at.GetInstanceID())) Plugin.Log?.LogInfo("Trader bubble: a trader met by " + at.name + (ship ? ", not the player's ship" : ", no ship") + ": no bubble."); return; }
            var v = VisibleTraders.Shown.FirstOrDefault(x => x.Copy != null && x.Copy.Body == body);
            if (v == null || (v.Copy.Bubble && v.Copy.BubbleGone == 0)) return;   // one bubble a trader: contacts while it shows change nothing
            Show(v.Copy, v.Boat.id);
        }
        // Each frame: turned to the view; 60 m from the trader's hull it shrinks away, then goes.
        internal static void Step()
        {
            if (!Refs.observerMirror) return;
            Vector2 player = Vector2.zero; bool known = false;
            foreach (var v in VisibleTraders.Shown)
            {
                var c = v.Copy; if (c == null || !c.Bubble) continue;
                if (c.BubbleGone > 0) { if (Time.time >= c.BubbleGone) { Object.Destroy(c.Bubble); c.Bubble = null; c.BubbleGone = 0; } continue; }
                c.Bubble.transform.rotation = Refs.observerMirror.transform.rotation;
                if (!known) { player = VisibleTraders.PlayerReal(); known = true; }
                if (!KeepForTest && VisibleTraders.HullDistance(v, player) > Away)
                {
                    c.BubbleGone = Time.time + ShrinkTime;
                    if (Juicebox.juice) Juicebox.juice.TweenScale(c.Bubble, Vector3.zero, ShrinkTime); else c.BubbleGone = Time.time;
                }
            }
        }
    }
    [HarmonyPatch(typeof(TavernRumorsDude), "Awake")] internal static class TraderBubbleLookPatch
    { static void Postfix(TavernRumorsDude __instance) { try { TraderBubble.TakeLook(__instance); } catch (Exception e) { Plugin.Log?.LogWarning("Trader bubble: the tavern's look not taken: " + e.Message); } } }
    // A prefix: the game's method then calls its sounds, which a ship without them would stop with an error before a postfix.
    [HarmonyPatch(typeof(BoatImpactSoundsCollider), "OnCollisionEnter")] internal static class TraderBubbleContactPatch
    {
        private static bool warned;
        static void Prefix(BoatImpactSoundsCollider __instance, Collision collision)
        {
            try { var body = collision != null ? collision.rigidbody : null; if (body && TraderHulls.IsCopy(body)) TraderBubble.Contact(__instance.transform, body); }
            catch (Exception e) { if (!warned) { warned = true; Plugin.Log?.LogWarning("Trader bubble: a contact failed: " + e); } }
        }
    }
}

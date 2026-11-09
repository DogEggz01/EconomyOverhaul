using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
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
        // 2026-10-14; small locals 3/2/2 in Al'Ankh, 2/2/3 in Emerald and Aestrin, since 2026-10-15, Al'Ankh's eighth at rep 3 since 2026-11-04; TradeBook.BookGroup):
        // rep 1, three (two in Emerald and Aestrin) of each region's small locals ("small") and
        // Emerald's two FFL small locals; rep 2, two more small locals ("small2"), the mediums (Al'Ankh's two since 2026-10-24, M2 (a)), the first Aestrin heavy
        // ("heavy"), the FFL-Emerald shuttle; rep 3, the other small locals ("small3"), the second heavy ("heavy2"), the regionals and the Happy Bay trader (rep 3 in any
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
        // A level up releases the trade-book reports at once instead of at the next midnight (the user, 2026-10-23,
        // SMALL_REQUESTS_DRAFT.md: "every level up"): whether any account's level is above its last known one.
        public static bool LevelRose(int[] before, int[] now)
        {
            if (before == null || now == null) return false;
            for (int i = 0; i < Math.Min(before.Length, now.Length); i++) if (now[i] > before[i]) return true;
            return false;
        }
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
        // The pull toward a port no trader has called at lately (TRADER_NEXTLEG_DRAFT.md; the user, 2026-11-04: "test the unvisit port get
        // more attractive", "Yes, draft both", 2026-11-06: "Use 0.05 for all"): a plan's percent grows with the age of the trader's report of that port, so a
        // port with nothing worth carrying stays at 0.
        public const double QuietPortPull = .05;
        public static double ReportPull(double percent, int ageDays, double perDay) => percent * (1 + perDay * Math.Max(0, ageDays));
        // forEvent: a trip toward a dynamic demand event the trader knows (to its producer, or with its good to its demanding port);
        // eventRegion: the region both of that event's ports are in, -1 when they are in two regions.
        public static bool ValidRoute(string group, int origin, int destination, int home, int originRegion, int destinationRegion, double distance,
            bool forEvent = false, int eventRegion = -1)
        {
            if (origin == destination || destination == 7 || destination == 33) return false;
            // A Chronos trader sails between its home capital and Chronos; one elsewhere (a trade fleet ship given the Chronos route at an office
            // outside its region's capital, or switched to it there) first sails home (the deep review of 2026-11-09: it waited 48 h for good).
            if (group == "chronos") return (origin == 21 && destination == home) || (origin == home && destination == 21) || (origin != 21 && origin != home && destination == home);
            // Only Chronos traders sail to Chronos.
            if (destination == 21) return false;
            // Every Happy Bay trader route has Happy Bay at exactly one end.
            if (group == "happy") return (origin == 20) != (destination == 20);
            // Heavy routes must touch Eastwind, Aestra Abbey or Firefly Grotto.
            // This also filters the empty nearest-port fallback before it is chosen.
            if (group == "heavy")
                return origin == 19 || origin == 26 || origin == 28 || destination == 19 || destination == 26 || destination == 28;
            // An event's trips at any range (the user, 2026-10-30, EVENT_REACH_WATER_DRAFT.md, "E1: yes", "E2: yes"): an independent
            // anywhere; a regional only for an event whose two ports are both in its home region, to a port there (inside it too).
            if (forEvent && (group == "independent" || group == "regional" && eventRegion == home && destinationRegion == home)) return true;
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
        // Speeds are m/s, converted from the approved knots and rounded to two decimals. The small locals 5 knots since the
        // user's buff of 2026-10-18 (4.5 before; VISIBLE_TRADERS_DRAFT.md, its last section), the heavy ones 4 knots again since
        // 2026-10-30 (the user: "reduce heavy ship weight by 0.5 knots", HEAVY_CARGO_DRAFT.md; 4.5 from 2026-10-18); a save's
        // traders take these speeds and waits when it loads (StateCodec.Finish). The medium locals 5.5 knots and 5 h, the Happy Bay
        // trader 6.5 knots and 7 h since the sail plans (the user, 2026-10-19: "Medium local: 5.5 knots", "make wait stay 5 hour",
        // "Happy bay trader: 6.5 knots, wait 7 hour"; 5 knots and 4 h, 6 knots and 6.5 h before; TRADER_RIGS_DRAFT.md).
        internal static readonly FleetDefinition[] Current = {
            // Al'Ankh 2 medium locals since 2026-10-24 (the user: "use 2 local medium and 7 local small"; SMALL_REQUESTS_DRAFT.md section 5)
            // and 8 small locals since 2026-11-04 (the user: "add 1 more small local to al'ankh, default rigged"; TRADER_NEXTLEG_DRAFT.md):
            // 53 traders. A save with fewer (1.1.5 and older 52, 1.0.2 and older 51) gets the missing ones at load (Fleet.TopUp).
            new FleetDefinition("small", 0, 8, 20, 1000, 2.57f, 2.5f),
            new FleetDefinition("small", 1, 7, 20, 1000, 2.57f, 2.5f),
            new FleetDefinition("small", 2, 7, 20, 1000, 2.57f, 2.5f),
            new FleetDefinition("small", 1, 2, 20, 1000, 2.57f, 2.5f, ffl: true),
            new FleetDefinition("heavy", 2, 2, 30, 10000, 2.06f, 24, minimumMass: 220, startPorts: new[] { 19, 28 }),
            new FleetDefinition("medium", 0, 2, 40, 4000, 2.83f, 5),
            new FleetDefinition("medium", 1, 1, 40, 4000, 2.83f, 5),
            new FleetDefinition("medium", 2, 1, 40, 4000, 2.83f, 5),
            // Regionals carry 55 units since 2026-11-07 (the user: "make regional trader got 55 slot instead"; 40 before, which filled 96% of
            // their slots and 27% of their weight limit; TRADE_OFFICE_DRAFT.md §3B). A saved game's regionals take it at the load (StateCodec.Finish).
            new FleetDefinition("regional", 0, 3, 55, 10000, 3.60f, 8),
            new FleetDefinition("regional", 1, 3, 55, 10000, 3.60f, 8),
            new FleetDefinition("regional", 2, 3, 55, 10000, 3.60f, 8),
            new FleetDefinition("independent", -1, 9, 70, 10000, 4.12f, 12),
            new FleetDefinition("shuttle", 1, 1, 40, 5000, 3.09f, 6.5f),
            new FleetDefinition("happy", 2, 1, 40, 5000, 3.34f, 7, startPort: 20),
            // Chronos traders wait 48 h since 2026-10-30 (the user: "Change chronos trader waiting time to 48 hours"; 24 before).
            new FleetDefinition("chronos", 0, 1, 130, 13000, 4.37f, 48),
            new FleetDefinition("chronos", 9, 1, 130, 13000, 4.37f, 48),
            new FleetDefinition("chronos", 15, 1, 130, 13000, 4.37f, 48)
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
        // A save made with fewer traders than the table (a game of 1.1.5 or older has 7 Al'Ankh small locals, the table 8 since 2026-11-04; of
        // 1.0.2 or older 1 Al'Ankh medium local, the table 2 since 2026-10-24): each definition short of its count gets new traders with the next ids, waiting at a port of its network (in
        // turn), the hold empty, given each network port's own report from the save's markets, as a new game's traders are. Only
        // while the fleet is short of the table's total (a made-up fleet of that total, as the tests build, is left alone).
        internal static int TopUp(SaveState s)
        {
            if (s?.fleet == null || s.markets == null || s.fleet.Any(b => b?.network == null) || s.fleet.Count >= FleetDefinitions.Total) return 0;
            int added = 0;
            foreach (var d in FleetDefinitions.Current)
            {
                int have = s.fleet.Count(d.Matches); var network = d.Network();
                for (; have < d.Count && s.fleet.Count < FleetDefinitions.Total && network.Length > 0; have++, added++)
                {
                    var b = new BoatState { id = s.fleet.Count == 0 ? 0 : s.fleet.Max(x => x.id) + 1, group = d.Group, home = d.Home, network = network,
                        origin = network[have % network.Length], remaining = 1.25 };
                    d.Apply(b);
                    foreach (int p in network)
                    {
                        var m = s.markets.Find(x => x != null && x.port == p);
                        var r = m?.reports == null ? null : Report(m.reports, p); if (r != null) PutReport(b.reports, r);
                    }
                    s.fleet.Add(b);
                }
            }
            return added;
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
            // The trade offices' own ships (TradeOffice.cs, OfficeFleet): one held in port (OfficeFleet.Held: no route yet, its office not at work, or
            // its fee unpaid) waits; drawn and under way it sets its own timer as the traders do.
            var mine = World.State.playerFleet;
            if (mine != null) foreach (var b in mine) if (!RegistrationPending(b) && !OfficeFleet.Held(b) && !VisibleTraders.Clocked(b, dt)) b.remaining -= dt;
            // Process the earliest event across the fleet first. This preserves event order
            // across frame sizes; the bounded backlog remains in saved remaining timers.
            try
            {
                for (int count = 0; count < SimulationClock.FleetEventBudget; count++)
                {
                    BoatState next = null;
                    foreach (var b in fleet)
                        if (b.remaining <= 0 && !RegistrationPending(b) && (next == null || b.remaining < next.remaining || b.remaining == next.remaining && b.id < next.id)) next = b;
                    if (mine != null)
                        foreach (var b in mine)
                            if (b.remaining <= 0 && !RegistrationPending(b) && !OfficeFleet.Held(b) && (next == null || b.remaining < next.remaining || b.remaining == next.remaining && b.id < next.id)) next = b;
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
            bool mine = OfficeFleet.Is(b);
            Economy.BatchDepth++;
            try { foreach (int g in b.goods) { double coins = Fx.NpcQuote(m, g, false); m.SellGood(g); Fx.Record(Fx.Currency(m), coins, false, TradeSource.Npc); OnTrade?.Invoke(b, Fx.Currency(m), coins, false); if (mine) OfficeFleet.Trade(b, Fx.Currency(m), coins, false, g); } }
            finally { Economy.BatchDepth--; }
            if (mine) OfficeFleet.Arrived(b, Fx.Currency(m));
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
            if (!mine) TradeBook.Arrive(b, b.destination);   // the office's ships stay out of the trade book (TRADE_OFFICE_DRAFT.md §4.5)
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
                // The game writes only a market's own entry in place (IslandMarket.UpdateSelfPriceReport), and no installed mod writes any: another port's entry holds the report's arrays,
                // which never change, so the game's save holds them once (the user's Q38: "share arrays"); a market's own entry keeps copies.
                known[r.port] = r.port == m.GetPortIndex()
                    ? new PriceReport { day = r.day, approved = r.approved, buyPrices = (int[])r.buy.Clone(), sellPrices = (int[])r.sell.Clone() }
                    : new PriceReport { day = r.day, approved = r.approved, buyPrices = r.buy, sellPrices = r.sell };
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
        // Told of each plan: the trader, its milliseconds, whether the pull changed the destination, the chosen trip's percent and the
        // best percent before the pull. Set only by -DemandStudy; null in play.
        internal static Action<BoatState, double, bool, double, double> OnPlanned;
        // The pull's strength: a destination scored by its plan's percent times 1 + this x its report's age in days (Rules.ReportPull,
        // TRADER_NEXTLEG_DRAFT.md). The -DemandStudy suite may set it from EO_PULL; at 0 the choice is the hold's profit over its cost alone.
        internal static double PullPerDay = Rules.QuietPortPull;
        // Told of each port a trader may trade with, with its plan's percent (0: nothing worth carrying). Set only by -DemandStudy; null in play.
        internal static Action<BoatState, int, double> OnCandidate;
        // Told of each crate a trader buys or sells: the trader, the coin's currency, the coins, whether buying. Set only by -DemandStudy; null in play.
        internal static Action<BoatState, int, double, bool> OnTrade;
        // How old a trader's report of a port is, in days, on the day stamp reports carry (Economy.CaptureReport). A report only reaches
        // another port on a trader leaving the port it describes, so this is what a trader can know of "no one has called there lately".
        internal static int ReportAge(BoatState b, int port)
        {
            var report = Report(b.reports, port); if (report == null) return 0;
            int today = Startup.Running ? (int)Math.Floor(Startup.CalendarHour / 24) : GameState.day;
            return Math.Max(0, today - report.day);
        }
        private static void Depart(BoatState b)
        {
            var steps = DepartSteps(b);
            while (steps.MoveNext()) { }
        }
        private static IEnumerator DepartSteps(BoatState b)
        {
            var m = World.Market(b.origin); if (!m || !Supply.Contains(b.origin)) { b.remaining = b.wait; yield break; }
            bool mine = OfficeFleet.Is(b);
            // Traders plan with, and carry on, the port's report as published at the last midnight (0.7.0).
            b.reports = World.MarketData(b.origin).reports.Where(r => r.approved).ToList();
            Candidate best = null; int closest = -1; double closestDistance = double.MaxValue;
            var buys = new double[65, b.units];
            var factors = PlanningCurrency();
            bool knowsLast = Rules.KnowsLastDeparture(UnityEngine.Random.value);
            Demand.Learn(b, b.origin); Demand.LearnFromReports(b);
            // A trip this trader may sail: the destination in its network, its route rule from that origin (for a trip toward an event
            // it knows, the event's rule: Rules.ValidRoute's forEvent).
            int EventRegion(DemandEvent e) { var demanding = LivePort(e.demand); var maker = LivePort(e.producer); return demanding && maker && demanding.region == maker.region ? (int)demanding.region : -1; }
            bool CanSail(int from, int to, DemandEvent e = null) => from != to && LivePort(from) && LivePort(to) && World.Market(to) && Supply.Contains(to) && b.network.Contains(to) &&
                Rules.ValidRoute(b.group, from, to, b.home, (int)LivePort(from).region, (int)LivePort(to).region, Distance(from, to), e != null, e != null ? EventRegion(e) : -1);
            var serve = Demand.Serve(b, m, e => CanSail(b.origin, e.demand, e), out int need);
            // Not able to serve from here: an event it knows whose two ports it can both reach sends it to the producer (the user,
            // 2026-10-16), carrying whatever profits on the way.
            var fetch = serve == null ? Demand.Fetch(b, (e, from, to) => CanSail(from, to, e)) : null;
            Candidate served = null, fetched = null;
            // The best percent after the pull so far and, apart for the study, the best before it.
            double bestScore = 0; Candidate aloneBest = null; var watch = OnPlanned != null ? Stopwatch.StartNew() : null;
            foreach (int p in b.network)
            {
                var destination = LivePort(p); var destinationMarket = World.Market(p);
                if (!destination || !destinationMarket || !Supply.Contains(p)) continue;
                double distance = Distance(b.origin, p);
                // A port the trader may sail to only for the event it serves or fetches for is planned for that event alone: never the
                // best trade or the closest port otherwise.
                bool normal = Rules.ValidRoute(b.group, b.origin, p, b.home, (int)m.GetPort().region, (int)destination.region, distance);
                if (!normal && !(serve != null && p == serve.demand) && !(fetch != null && p == fetch.producer)) continue;
                if (normal && distance < closestDistance) { closest = p; closestDistance = distance; }
                bool serving = serve != null && p == serve.demand;
                var candidate = PlanWithCurrency(b, p, buys, factors, knowsLast, serving ? serve.good : 0, serving ? need : 0);
                if (serving && candidate.goods.Contains(serve.good)) served = candidate;
                if (fetch != null && p == fetch.producer) fetched = candidate;
                if (normal) OnCandidate?.Invoke(b, p, candidate.cost > 0 && candidate.profit > 0 ? candidate.profit / candidate.cost : 0);
                if (normal && candidate.cost > 0 && candidate.profit > 0)
                {
                    double percent = candidate.profit / candidate.cost, pulled = Rules.ReportPull(percent, ReportAge(b, p), PullPerDay);
                    if (pulled > bestScore) { bestScore = pulled; best = candidate; }
                    if (aloneBest == null || percent > aloneBest.profit / aloneBest.cost) aloneBest = candidate;
                }
                yield return null;
            }
            if (served != null) best = served;   // an informed trader serves the dynamic demand event first
            else if (fetched != null) best = fetched;   // or sails to its producer to fetch the good
            int purpose = served != null ? 1 : fetched != null ? 2 : 0;
            if (best == null && closest >= 0) best = new Candidate { destination = closest };
            if (best == null) { b.remaining = b.wait; yield break; }
            OnPlanned?.Invoke(b, watch?.Elapsed.TotalMilliseconds ?? 0, purpose == 0 && PullPerDay > 0 && best.destination != (aloneBest != null ? aloneBest.destination : closest),
                best.cost > 0 ? best.profit / best.cost : 0, aloneBest != null && aloneBest.cost > 0 ? aloneBest.profit / aloneBest.cost : 0);
            Economy.BatchDepth++;
            try { foreach (int g in best.goods) { double coins = Fx.NpcQuote(m, g, true); m.PurchaseGood(g); Fx.Record(Fx.Currency(m), coins, true, TradeSource.Npc); OnTrade?.Invoke(b, Fx.Currency(m), coins, true); if (mine) OfficeFleet.Trade(b, Fx.Currency(m), coins, true); } }
            finally { Economy.BatchDepth--; }
            b.goods = best.goods.ToArray(); b.destination = best.destination;
            b.trip = b.remaining = 100 * Distance(b.origin, b.destination) / b.speed;
            VisibleTraders.OnDepart(b, EventTime);   // its lane (a trade fleet ship's too: it is drawn)
            Demand.OnDepart?.Invoke(b, purpose);
            Economy.Refresh(m); var own = Report(World.MarketData(b.origin).reports, b.origin); PutReport(b.reports, own);
            if (!mine) TradeBook.Depart(b, own, EventHour);
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
        // The trade office's clerk (TradeOffice.cs; TRADE_OFFICE_DRAFT.md §4.3, Q19): the last departure and the next arrival, each when there is
        // one, in full detail (boat type and load size, as the best drink), "Boss" in place of the tavern's voice. Null for one there isn't.
        internal static void ClerkShips(int port, out string left, out string expected)
        {
            left = expected = null;
            var currentPort = LivePort(port);
            if (!currentPort || !Supply.Contains(port) || !FloatingOriginManager.instance) return;
            double offset = FloatingOriginManager.instance.GetGlobeCoords(currentPort.transform).x / 15;
            double now = World.CalendarHour;
            string When(double when)
            {
                double hour = ((when + offset) % 24 + 24) % 24; int day = RumorDay(when, now, offset);
                string dayText = day == 0 ? "today" : day == -1 ? "yesterday" : day == 1 ? "tomorrow" : day < 0 ? (-day) + " days ago" : "in " + day + " days";
                return dayText + " " + (hour >= 6 && hour < 12 ? "in the morning" : hour >= 12 && hour < 18 ? "in the afternoon" : "at night");
            }
            string Cargo(int[] goods)
            {
                var main = goods?.GroupBy(g => g).OrderByDescending(g => g.Count()).FirstOrDefault();
                var good = main != null && PrefabsDirectory.instance ? PrefabsDirectory.instance.GetGood(main.Key) : null;
                return RumorCargo(good ? good.name : null, good ? main.Count() : 0, true);
            }
            var last = World.MarketData(port).lastDeparture;
            if (StateCodec.ValidDeparture(last) && RecentDeparture(last.calendarHour, now, offset) && LivePort(last.destination) && Supply.Contains(last.destination))
            {
                var vessel = RumorVessel(last.group, true);
                left = "From the harbour, Boss: " + char.ToLowerInvariant(vessel[0]) + vessel.Substring(1) + " left for " + LivePort(last.destination).GetPortName() + ", " + When(last.calendarHour) + ". " + Cargo(last.goods);
            }
            var incoming = World.State.fleet.Where(b => b.destination == port && UpcomingArrival(now + Math.Max(0, b.remaining) * .008, now, offset)).OrderBy(b => b.remaining).FirstOrDefault();
            if (incoming != null && LivePort(incoming.origin) && Supply.Contains(incoming.origin))
                expected = RumorVessel(incoming.group, true) + " is expected from " + LivePort(incoming.origin).GetPortName() + ", " + When(now + Math.Max(0, incoming.remaining) * .008) + ", Boss. " + Cargo(incoming.goods);
        }
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
        // A good's price at supply 0 comes out the same in every report (compared here at each capture): the reports hold one array, never written once made (a report never changes), so the
        // save holds it once (TRADE_OFFICE_DRAFT.md, the user's Q38: "share arrays"; every number stays).
        private static float[] sharedValues, valuesBuffer;
        private static float[] SharedValues(IslandMarket m)
        {
            int n = m.currentSupply.Length;
            if (valuesBuffer == null || valuesBuffer.Length != n) valuesBuffer = new float[n];
            for (int i = 1; i < n; i++) if (i != 45 && i != 51) valuesBuffer[i] = m.GetGoodPriceAtSupply(i, 0);
            if (sharedValues == null || sharedValues.Length != n || !sharedValues.SequenceEqual(valuesBuffer)) sharedValues = (float[])valuesBuffer.Clone();
            return sharedValues;
        }
        internal static void CaptureReport(IslandMarket m)
        {
            if (!World.Ready || !Supply.Contains(m.GetPortIndex()) || m.currentSupply == null || m.currentSupply.Length < 65) return;
            var native = m.GetSelfPriceReport(); if (native == null) return;
            if (Startup.Running) native.day = (int)Math.Floor(Startup.CalendarHour / 24);
            var r = new ReportState { port = m.GetPortIndex(), day = native.day, approved = native.approved,
                cap = Cap(m.GetPortIndex()), supply = (float[])m.currentSupply.Clone(), values = SharedValues(m),
                buy = (int[])native.buyPrices.Clone(), sell = (int[])native.sellPrices.Clone(),
                surplus = DebugMarketTracker.instance.positivePriceMult, deficit = DebugMarketTracker.instance.negativePriceMult };
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
        internal static BookSnapshot Add(int port, int day, double seen, int[] buy, int[] sell) => AddShared(port, day, seen, (int[])buy.Clone(), (int[])sell.Clone());
        // The arrays kept as they are: for a report's, which never change once made, so the save holds them once (the user's Q38: "share arrays"). Not for a market's own
        // native prices, which the game writes in place (TradeBook.See goes through Add).
        internal static BookSnapshot AddShared(int port, int day, double seen, int[] buy, int[] sell)
        {
            var map = Snapshots; var state = World.State;
            var s = new BookSnapshot { id = ++state.bookNextId, port = port, day = day, seen = seen, buy = buy, sell = sell };
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

        // The class a delivering boat files under, by boat number within its kind: of Al'Ankh's eight small locals the
        // first three "small" (rep 1), the next two "small2" (rep 2), the last three "small3" (rep 3; the user, 2026-10-15,
        // was two, two, three; the eighth since 2026-11-04, S2 (a) of SMALL_REQUESTS_DRAFT.md); in Emerald (home 1) and Aestrin (home 2) two, two, three (the user, 2026-10-15); the Lagoon's two small
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
            if (own != null && own.approved && own.buy != null && own.sell != null) Keep(b.book, AddShared(b.origin, own.day, hour, own.buy, own.sell));
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
            Office.Midnight(day);
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
        // A level up in any region or at Chronos releases at once what midnight would (Rules.LevelRose). Watched each frame, so
        // every way a level changes counts (missions, loans, other mods, commands). Outside play (a load, the menu) the levels
        // are only noted, so a load releases nothing.
        // levels: the last frame's (null: none noted); spare: the array this frame's are read into (two arrays swapped, none made a frame)
        private static int[] levels, spare = new int[4];
        internal static void Watch()
        {
            if (!World.Ready || Startup.BlocksGameplay) { levels = null; return; }
            var now = spare;
            now[0] = Reputations.Level(0); now[1] = Reputations.Level(1); now[2] = Reputations.Level(2); now[3] = Reputations.Level(Reputations.Chronos);
            bool rose = GameState.playing && !World.Loading && !GameState.currentlyLoading && Rules.LevelRose(levels, now);
            spare = levels ?? new int[4]; levels = now;
            if (rose) { Release(); Plugin.Log?.LogInfo("Reputation level up (" + string.Join(", ", now.Select(x => x.ToString()).ToArray()) + "): trade-book reports released at once."); }
        }
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
        internal static void Loaded() { Close(); indexed = null; levels = null; Closed(); }
        internal static void Reset() { Close(); indexed = null; levels = null; index.Clear(); }
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
    // The good page's two lines (the user, 2026-11-01, TUTORIAL_UI_DRAFT.md U3 and §3): the good's category on a smaller line under
    // its name (none for goods outside the four), and "Port price update at midnight everyday" above the price list. Each a copy of
    // the name's own text (its font, ink and turn), made once under the same parent.
    internal static class GoodPageLines
    {
        // The price line on one row in the free band above the price list's headings, under Size and Weight (their rows end about
        // .83, the headings' row tops at .751): tut-7, at .700 across the page, it ran through the headings; tut-8, in their left cell
        // on two rows, through "days ago" and the first port.
        internal const string PriceLine = "Port price update at midnight everyday";
        // In the parent's units: the name at (.294, .872); the category under it at .790, clear of a "g" or "y" in the name (the
        // user's option 3 of four pictured, tut-24; at .808 the tails touched it); the price line centred at x -.25 (Size's label
        // ends at -.208, its value starts at -.258), 65% of the port names' size.
        internal const float CategoryY = .790f, CategorySize = .55f, PriceX = -.25f, PriceY = .79f, PriceSize = .65f;
        private static TextMesh category, price;
        private static TextMesh Copy(TextMesh source, string label, float x, float y, float size)
        {
            var o = Object.Instantiate(source.gameObject, source.transform.parent, false); o.name = label;
            o.transform.localPosition = new Vector3(x, y, source.transform.localPosition.z); o.transform.localScale = source.transform.localScale * size;
            return o.GetComponent<TextMesh>();
        }
        internal static void Show(EconomyUI ui, int good)
        {
            var name = Fields.Get<TextMesh>(ui, "textGoodName"); var ports = Fields.Get<TextMesh>(ui, "textIslandNames"); if (!name) return;
            if (!category) category = Copy(name, "economy good category", name.transform.localPosition.x, CategoryY, CategorySize);
            if (!price && ports)
            {
                price = Copy(ports, "economy price update", PriceX, PriceY, PriceSize);
                price.anchor = TextAnchor.MiddleCenter; price.alignment = TextAlignment.Center; price.text = PriceLine;
            }
            category.text = Rules.CategoryName(Rules.Category(good));
        }
    }
    [HarmonyPatch(typeof(EconomyUI), "ShowGoodPage")] internal static class GoodPageLinesPatch
    { static void Postfix(EconomyUI __instance, int goodIndex) { try { GoodPageLines.Show(__instance, goodIndex); } catch (Exception e) { Plugin.Log?.LogWarning("Trade book: the good page's lines not drawn: " + e.Message); } } }
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

    // ---- Visible traders on a Scrambled Seas map (SCRAMBLED_TRADERS_DRAFT.md section 2, the user's answers S1-S6; built 2026-11-02) ----
    // Once the islands are moved and read (VisibleTraders' first scan after a load), the harbours and routes for this map are made on a
    // background thread from the island charts and the lane map's harbour data; no trader is drawn until they are ready (S2). Every
    // port's harbour moves with its island; a berth another island's land comes within 20 m of is closed (S1); each arrival point is
    // made again by S5's rule; one lane per pair of Scrambled Seas' groups, A* only round each blocked stretch (the prototype
    // outputs/economyoverhaul_scrambled/tools/routetime, measured in the draft's section 5). Nothing is saved.

    // One island's own land (probe scrambled_charts, Data/islands.bin made by Data/MakeIslands.py): 10 m cells where it lies in the
    // vanilla game, as bit rows. Cell (i, j) is the square x 10 i .. 10 i + 10, z 10 j .. 10 j + 10 (real, vanilla).
    internal sealed class LandChart
    {
        internal const double Cell = 10;
        internal readonly int Index, I0, J0, W, H, Count;
        internal readonly double RootX, RootZ;
        private readonly int words;
        private readonly ulong[] bits;
        // runs: row (from J0), column (from I0), count
        internal LandChart(int index, double rootX, double rootZ, int i0, int j0, IList<int[]> runs)
        {
            Index = index; RootX = rootX; RootZ = rootZ; I0 = i0; J0 = j0;
            foreach (var r in runs) { W = Math.Max(W, r[1] + r[2]); H = Math.Max(H, r[0] + 1); }
            words = (W + 63) >> 6; bits = new ulong[Math.Max(1, words * H)];
            foreach (var r in runs) for (int c = r[1]; c < r[1] + r[2]; c++) { bits[r[0] * words + (c >> 6)] |= 1UL << (c & 63); Count++; }
        }
        internal double MinX => I0 * Cell;
        internal double MinZ => J0 * Cell;
        internal double MaxX => (I0 + W) * Cell;
        internal double MaxZ => (J0 + H) * Cell;
        internal bool Has(int i, int j)
        {
            i -= I0; j -= J0;
            return i >= 0 && j >= 0 && i < W && j < H && (bits[j * words + (i >> 6)] >> (i & 63) & 1UL) != 0;
        }
        // any land in row j between columns a and b (absolute, inclusive)
        private bool RowAny(int j, int a, int b)
        {
            j -= J0; if (j < 0 || j >= H) return false;
            a = Math.Max(a - I0, 0); b = Math.Min(b - I0, W - 1); if (a > b) return false;
            int row = j * words, wa = a >> 6, wb = b >> 6;
            for (int w = wa; w <= wb; w++)
            {
                ulong m = ulong.MaxValue;
                if (w == wa) m &= ulong.MaxValue << (a & 63);
                if (w == wb && (b & 63) != 63) m &= (1UL << ((b & 63) + 1)) - 1;
                if ((bits[row + w] & m) != 0) return true;
            }
            return false;
        }
        // Land closer than d to (x, z) in the chart's vanilla frame (to the nearest point of a cell's square).
        internal bool Near(double x, double z, double d)
        {
            if (x < MinX - d || x > MaxX + d || z < MinZ - d || z > MaxZ + d) return false;
            int j0 = (int)Math.Floor((z - d) / Cell), j1 = (int)Math.Floor((z + d) / Cell);
            for (int j = j0; j <= j1; j++)
            {
                double lo = j * Cell, dz = z < lo ? lo - z : z > lo + Cell ? z - lo - Cell : 0;
                if (dz >= d) continue;
                double w = Math.Sqrt(d * d - dz * dz);
                if (RowAny(j, (int)Math.Floor((x - w) / Cell), (int)Math.Ceiling((x + w) / Cell) - 1)) return true;
            }
            return false;
        }
        // Every land cell (absolute i, j).
        internal void Each(Action<int, int> cell)
        {
            for (int j = 0; j < H; j++)
                for (int w = 0; w < words; w++)
                {
                    ulong v = bits[j * words + w];
                    for (int b = 0; v != 0; b++, v >>= 1) if ((v & 1UL) != 0) cell(I0 + (w << 6) + b, J0 + j);
                }
        }
    }

    // The embedded island charts (Data/islands.bin): each charted island's land, the land that belongs to no island (the lagoon's
    // terrain plane, which Scrambled Seas never moves), and the island each port is on.
    internal static class IslandCharts
    {
        private static readonly object gate = new object();
        private static Dictionary<int, LandChart> islands;
        private static List<LandChart> fixedLand;
        private static Dictionary<int, int> portIsland;
        internal static Dictionary<int, LandChart> Islands { get { Load(); return islands; } }
        internal static List<LandChart> Fixed { get { Load(); return fixedLand; } }
        internal static Dictionary<int, int> PortIsland { get { Load(); return portIsland; } }
        internal static void Load()
        {
            lock (gate)
            {
                if (islands != null) return;
                using (var s = typeof(IslandCharts).Assembly.GetManifestResourceStream("EconomyOverhaul.Data.islands.bin"))
                using (var r = new System.IO.BinaryReader(s)) Parse(r);
            }
        }
        private static void Parse(System.IO.BinaryReader r)
        {
            var magic = r.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != 'E' || magic[1] != 'O' || magic[2] != 'I' || magic[3] != '1') throw new InvalidOperationException("islands.bin: not an island chart file");
            var isl = new Dictionary<int, LandChart>(); var fx = new List<LandChart>(); var ports = new Dictionary<int, int>();
            int n = r.ReadInt32();
            for (int k = 0; k < n; k++) { var c = Read(r); isl[c.Index] = c; }
            n = r.ReadInt32();
            for (int k = 0; k < n; k++) fx.Add(Read(r));
            n = r.ReadInt32();
            for (int k = 0; k < n; k++) { int port = r.ReadInt32(), island = r.ReadInt32(); ports[port] = island; }
            fixedLand = fx; portIsland = ports; islands = isl;
        }
        private static LandChart Read(System.IO.BinaryReader r)
        {
            int index = r.ReadInt32(); float rx = r.ReadSingle(), rz = r.ReadSingle(); int i0 = r.ReadInt32(), j0 = r.ReadInt32(), runs = r.ReadInt32();
            var list = new List<int[]>(runs);
            for (int k = 0; k < runs; k++) { int row = r.ReadInt16(), col = r.ReadInt16(), count = r.ReadInt16(); list.Add(new[] { row, col, count }); }
            return new LandChart(index, rx, rz, i0, j0, list);
        }
    }

    // Scrambled Seas' archipelagos (7.1.9's WorldScrambler.GenerateDefs, no regions.json): island 30 is in groups 6 and 7 and group
    // 7, placed later, wins; an island in no group joins its closest region's group: Better Ports' Bottleneck (67) Emerald's (its
    // region 15.8 km off, probe scrambled_charts' REGION lines).
    internal static class ScrambleGroups
    {
        private static readonly int[][] Defs = {
            new[] { 1, 2, 3, 4, 5, 6, 7, 8, 40 }, new[] { 9, 10, 11, 12, 13, 22, 37, 38, 39, 67 }, new[] { 15, 16, 17, 19, 21, 23, 33, 34, 35, 36 },
            new[] { 20, 41, 42 }, new[] { 18 }, new[] { 25 }, new[] { 26, 27, 28, 29, 31 }, new[] { 30 }, new[] { 32 } };
        internal static readonly int[] LagoonIslands = { 26, 27, 28, 29, 31 };
        internal static int Of(int island)
        {
            for (int g = 0; g < Defs.Length; g++) if (Array.IndexOf(Defs[g], island) >= 0) return g;
            return -1;
        }
    }

    // The map as it is when the routes are asked for, read on the main thread (the islands' live places), so the thread makes no
    // Unity call: each charted island's offset from its vanilla place, the islands without a chart as circles.
    internal sealed class ScrambledInput
    {
        internal readonly Dictionary<int, Vector2> Offsets = new Dictionary<int, Vector2>();
        internal readonly List<Vector3> Circles = new List<Vector3>();    // x, z (real, live), radius
        internal readonly List<string> Notes = new List<string>();
        internal string Key;
        private static readonly Dictionary<int, float> radiusOf = new Dictionary<int, float>();
        internal const float CircleRadius = 2000f;
        internal static ScrambledInput Read()
        {
            var fom = FloatingOriginManager.instance; var input = new ScrambledInput(); var key = new StringBuilder();
            var charts = IslandCharts.Islands;
            for (int i = 0; Refs.islands != null && i < Refs.islands.Length; i++)
            {
                var t = Refs.islands[i]; if (!t) continue;
                var real = fom.ShiftingPosToRealPos(t.position);
                if (charts.TryGetValue(i, out var c))
                {
                    var o = new Vector2((float)(real.x - c.RootX), (float)(real.z - c.RootZ)); input.Offsets[i] = o;
                    key.Append(i).Append(':').Append(Mathf.RoundToInt(o.x)).Append(',').Append(Mathf.RoundToInt(o.y)).Append(';');
                }
                else
                {
                    if (!radiusOf.TryGetValue(i, out float radius)) radiusOf[i] = radius = UnchartedRadius(t);
                    input.Circles.Add(new Vector3(real.x, real.z, radius));
                    key.Append(i).Append("c:").Append(Mathf.RoundToInt(real.x)).Append(',').Append(Mathf.RoundToInt(real.z)).Append(';');
                    input.Notes.Add("Scrambled lanes: island " + i + " (" + t.name + ") has no chart: kept clear as a circle of " + radius.ToString("F0") + " m round its root.");
                }
            }
            input.Key = key.ToString();
            return input;
        }
        // An island an island mod adds since the charts were made: its own colliders' reach from its root (under it, and its scene's
        // while loaded), else 2 km.
        private static float UnchartedRadius(Transform t)
        {
            try
            {
                var colliders = new List<Collider>(t.GetComponentsInChildren<Collider>(true));
                var hz = t.GetComponent<IslandHorizon>();
                if (hz && hz.islandIndex > 0 && hz.islandIndex < SceneManager.sceneCountInBuildSettings)
                {
                    var scene = SceneManager.GetSceneByBuildIndex(hz.islandIndex);
                    if (scene.isLoaded) foreach (var g in scene.GetRootGameObjects()) colliders.AddRange(g.GetComponentsInChildren<Collider>(true));
                }
                float most = 0; var root = t.position;
                foreach (var c in colliders)
                {
                    if (!c || c.isTrigger) continue;
                    var b = c.bounds;
                    foreach (float x in new[] { b.min.x, b.max.x }) foreach (float z in new[] { b.min.z, b.max.z }) most = Mathf.Max(most, new Vector2(x - root.x, z - root.z).magnitude);
                }
                return most > 50 && most < 20000 ? most : CircleRadius;
            }
            catch { return CircleRadius; }
        }
    }

    // The harbours and lanes of one scrambled map (made on the thread, then used on the main thread only).
    internal sealed class ScrambledMap
    {
        internal string Key;
        internal int Generation;
        internal readonly Dictionary<int, TraderHarbour> Harbours = new Dictionary<int, TraderHarbour>();   // ports drawn
        internal readonly Dictionary<int, int> GroupOf = new Dictionary<int, int>();
        internal readonly Dictionary<int, Vector2> Offsets = new Dictionary<int, Vector2>();
        internal readonly List<Vector2> LagoonPorts = new List<Vector2>();
        internal readonly Dictionary<long, Vector2> Exits = new Dictionary<long, Vector2>();          // group a * 100 + group b: a's exit toward b
        internal readonly Dictionary<long, List<Vector2>> InGroup = new Dictionary<long, List<Vector2>>();   // port a * 1000 + port b (a < b)
        internal readonly Dictionary<long, List<Vector2>> PortExit = new Dictionary<long, List<Vector2>>();  // port * 100 + other group
        internal readonly Dictionary<long, List<Vector2>> Lanes = new Dictionary<long, List<Vector2>>();     // group a * 100 + group b (a < b)
        internal readonly HashSet<int> Shut = new HashSet<int>();
        internal readonly List<string> Log = new List<string>();
        internal double Ms, HarbourMs, GridMs, SearchMs;
        internal int Searches, Straight, Detoured, NoWay, BerthsClosed, AnchoragesClosed, DirectDropped, HarbourWays, Blocked;
        internal float LongestWay;
        internal const float LagoonReach = 800f, SeaHalf = 60f, LagoonHalf = 20f;
        private readonly Dictionary<long, LanePath> trips = new Dictionary<long, LanePath>();
        internal TraderHarbour HarbourOf(int port) => Harbours.TryGetValue(port, out var h) ? h : null;
        internal int Choices(int from, int to) => Trip(from, to) != null ? 1 : 0;
        // Lanes 120 m wide at sea, 40 m within 800 m of a lagoon port (LaneMap.InLagoon's lagoon ring no longer exists).
        internal float HalfWidth(Vector2 p)
        {
            foreach (var q in LagoonPorts) if ((q - p).sqrMagnitude < LagoonReach * LagoonReach) return LagoonHalf;
            return SeaHalf;
        }
        private static List<Vector2> Way(Dictionary<long, List<Vector2>> ways, long a, long b, long scale)
        {
            if (a < b) return ways.TryGetValue(a * scale + b, out var w) ? w : null;
            if (!ways.TryGetValue(b * scale + a, out var back) || back == null) return null;
            var r = new List<Vector2>(back); r.Reverse(); return r;
        }
        // A trip from the origin's approach point to the destination's: in one group the way between them; between groups to the
        // origin group's exit toward the other, the pair's lane, then from the other group's exit to the port. Null when none.
        internal LanePath Trip(int from, int to)
        {
            if (from == to) return null;
            long key = (long)from * 1000 + to;
            if (trips.TryGetValue(key, out var cached)) return cached;
            LanePath path = null;
            if (Harbours.ContainsKey(from) && Harbours.ContainsKey(to) && GroupOf.TryGetValue(from, out int ga) && GroupOf.TryGetValue(to, out int gb))
            {
                List<Vector2> pts = null;
                if (ga == gb) pts = Way(InGroup, from, to, 1000);
                else
                {
                    PortExit.TryGetValue((long)from * 100 + gb, out var a); PortExit.TryGetValue((long)to * 100 + ga, out var b);
                    var lane = Way(Lanes, ga, gb, 100);
                    if (a != null && b != null && lane != null) { pts = new List<Vector2>(a); pts.AddRange(lane); for (int i = b.Count - 1; i >= 0; i--) pts.Add(b[i]); }
                }
                if (pts != null && pts.Count >= 2) path = new LanePath(pts);
            }
            trips[key] = path;
            return path;
        }
    }

    // Makes a ScrambledMap: no Unity call (Vector2 and Mathf are plain code).
    internal sealed class ScrambleBuilder
    {
        // A land at its place on this map: a chart moved by its island's offset, the fixed land, or an uncharted island's circle.
        private sealed class Placed
        {
            internal LandChart Chart; internal double Ox, Oz, Cx, Cz, R; internal int Island; internal bool Lagoon;
            internal double X0, Z0, X1, Z1;
            internal bool Near(double x, double z, double d)
            {
                if (x < X0 - d || x > X1 + d || z < Z0 - d || z > Z1 + d) return false;
                if (Chart != null) return Chart.Near(x - Ox, z - Oz, d);
                double dx = x - Cx, dz = z - Cz, r = R + d; return dx * dx + dz * dz < r * r;
            }
        }
        // The harbours' rules (section 2): the arrival point keeps 120 m clear of every island (40 m at a lagoon port); a berth with
        // another island's land within 20 m of it is closed (S1); a harbour way keeps 12 m from its own island and S1's 20 m from
        // any other, dearer within 40 m of land.
        internal const double ClearSea = 120, ClearLagoon = 40, Close = 20, OwnPass = 12, NearLand = 40, Walk = 5;
        internal static readonly int[] LagoonPortIds = { 22, 23, 24, 25 };
        private readonly ScrambledInput input;
        private readonly IReadOnlyDictionary<int, TraderHarbour> vanilla;
        private readonly Func<bool> cancelled;
        private readonly List<Placed> lands = new List<Placed>();
        private readonly ScrambledMap map = new ScrambledMap();
        private RouteGrid grid;
        private ScrambleBuilder(ScrambledInput input, IReadOnlyDictionary<int, TraderHarbour> vanilla, Func<bool> cancelled)
        { this.input = input; this.vanilla = vanilla; this.cancelled = cancelled; }
        private void Check() { if (cancelled != null && cancelled()) throw new OperationCanceledException(); }

        internal static ScrambledMap Build(ScrambledInput input, IReadOnlyDictionary<int, TraderHarbour> vanilla, Func<bool> cancelled)
        {
            var b = new ScrambleBuilder(input, vanilla, cancelled);
            var all = Stopwatch.StartNew();
            b.Place();
            var t = Stopwatch.StartNew(); b.MakeHarbours(); b.map.HarbourMs = t.Elapsed.TotalMilliseconds;
            t = Stopwatch.StartNew(); b.grid = new RouteGrid(b.lands, b.map.LagoonPorts, b.Check); b.map.Blocked = b.grid.Blocked.Count; b.map.GridMs = t.Elapsed.TotalMilliseconds;
            t = Stopwatch.StartNew(); b.MakeLanes(); b.map.SearchMs = t.Elapsed.TotalMilliseconds;
            b.map.Ms = all.Elapsed.TotalMilliseconds; b.map.Key = input.Key;
            b.map.Log.AddRange(input.Notes);
            b.map.Log.Add("Scrambled lanes: made in " + b.map.Ms.ToString("F0") + " ms (harbours " + b.map.HarbourMs.ToString("F0") + ", land kept clear " + b.map.GridMs.ToString("F0") +
                " (" + b.map.Blocked + " cells of 50 m), routes " + b.map.SearchMs.ToString("F0") + "); ports drawn " + b.map.Harbours.Count + ", shut in " + b.map.Shut.Count +
                "; berths closed " + b.map.BerthsClosed + ", anchorages closed " + b.map.AnchoragesClosed + ", direct ways dropped " + b.map.DirectDropped + ", harbour ways " + b.map.HarbourWays +
                "; searches " + b.map.Searches + " (straight " + b.map.Straight + ", round land " + b.map.Detoured + ", no way " + b.map.NoWay + "), the longest " + (b.map.LongestWay / 1000).ToString("F0") + " km.");
            return b.map;
        }

        // ---- The land at its new places ----
        private void Place()
        {
            foreach (var kv in input.Offsets)
            {
                if (!IslandCharts.Islands.TryGetValue(kv.Key, out var c)) continue;
                map.Offsets[kv.Key] = kv.Value;
                lands.Add(new Placed { Chart = c, Ox = kv.Value.x, Oz = kv.Value.y, Island = kv.Key, Lagoon = Array.IndexOf(ScrambleGroups.LagoonIslands, kv.Key) >= 0,
                    X0 = c.MinX + kv.Value.x, Z0 = c.MinZ + kv.Value.y, X1 = c.MaxX + kv.Value.x, Z1 = c.MaxZ + kv.Value.y });
            }
            foreach (var c in IslandCharts.Fixed)
                lands.Add(new Placed { Chart = c, Island = -1, Lagoon = true, X0 = c.MinX, Z0 = c.MinZ, X1 = c.MaxX, Z1 = c.MaxZ });
            int n = 0;
            foreach (var c in input.Circles)
                lands.Add(new Placed { Cx = c.x, Cz = c.y, R = c.z, Island = -1000 - n++, X0 = c.x - c.z, Z0 = c.y - c.z, X1 = c.x + c.z, Z1 = c.y + c.z });
            foreach (int p in LagoonPortIds)
                if (vanilla.TryGetValue(p, out var h) && IslandCharts.PortIsland.TryGetValue(p, out int i) && input.Offsets.TryGetValue(i, out var o)) map.LagoonPorts.Add(h.PortPoint + o);
        }
        // land within d of (x, z) in `set`, but not of island `skip` (int.MinValue: none skipped)
        private static bool Near(List<Placed> set, double x, double z, double d, int skip)
        {
            for (int k = 0; k < set.Count; k++) { var p = set[k]; if (p.Island != skip && p.Near(x, z, d)) return true; }
            return false;
        }
        private static bool NearOwn(List<Placed> set, double x, double z, double d, int own)
        {
            for (int k = 0; k < set.Count; k++) { var p = set[k]; if (p.Island == own && p.Near(x, z, d)) return true; }
            return false;
        }

        // ---- The harbours (section 2: "Harbours kept", S1, S5) ----
        private sealed class Harbour
        {
            internal int Port, Island; internal Vector2 O; internal double Clear; internal List<Placed> Lands;
        }
        private void MakeHarbours()
        {
            var moves = new List<float>();
            foreach (var v in vanilla.Values.OrderBy(h => h.Port))
            {
                Check();
                if (!IslandCharts.PortIsland.TryGetValue(v.Port, out int island) || !input.Offsets.TryGetValue(island, out var o)) continue;
                int group = ScrambleGroups.Of(island); if (group < 0) continue;
                var h = MakeHarbour(v, island, o, moves);
                if (h != null) { map.Harbours[v.Port] = h; map.GroupOf[v.Port] = group; }
            }
            if (moves.Count > 0)
            {
                moves.Sort();
                map.Log.Add("Scrambled lanes: arrival points made again " + moves.Count + ", from the vanilla one moved with its island: median " + moves[moves.Count / 2].ToString("F0") + " m, at most " + moves[moves.Count - 1].ToString("F0") + " m.");
            }
        }
        private static IEnumerable<Vector2> PointsOf(TraderHarbour v)
        {
            yield return v.Approach; yield return v.PortPoint; yield return v.Anchorage;
            foreach (var b in v.Berths)
            {
                yield return b.Centre;
                foreach (var p in b.Leg.Points) yield return p;
                if (b.Second != null) foreach (var p in b.Second.Points) yield return p;
                if (b.Direct != null) foreach (var p in b.Direct.Points) yield return p;
            }
            if (v.AnchorLeg != null) foreach (var p in v.AnchorLeg.Points) yield return p;
            if (v.AnchorSecond != null) foreach (var p in v.AnchorSecond.Points) yield return p;
        }
        private TraderHarbour MakeHarbour(TraderHarbour v, int island, Vector2 o, List<float> moves)
        {
            var hb = new Harbour { Port = v.Port, Island = island, O = o, Clear = Array.IndexOf(LagoonPortIds, v.Port) >= 0 ? ClearLagoon : ClearSea };
            // the land within reach of this harbour (its own island's too)
            double x0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, z1 = double.MinValue;
            foreach (var p in PointsOf(v)) { x0 = Math.Min(x0, p.x + o.x); z0 = Math.Min(z0, p.y + o.y); x1 = Math.Max(x1, p.x + o.x); z1 = Math.Max(z1, p.y + o.y); }
            const double reach = 2000;
            hb.Lands = lands.Where(l => l.X1 >= x0 - reach && l.X0 <= x1 + reach && l.Z1 >= z0 - reach && l.Z0 <= z1 + reach).ToList();
            var h = new TraderHarbour { Port = v.Port, PortPoint = v.PortPoint + o, Anchorage = v.Anchorage + o, AnchorRadius = v.AnchorRadius };
            var berths = v.Berths.OrderBy(b => b.Number).ToList();
            // The arrival point (S5): berth 1's leg (else the anchorage's, from its centre) walked out to its first point clear of every
            // island; a leg that never gets there goes on by a harbour way to the nearest clear water.
            LanePath first = berths.Count > 0 ? berths[0].Leg.Moved(o) : v.AnchorLeg?.Moved(o).Reversed();
            if (first == null) return null;
            float cut = Cut(hb, first);
            Vector2 arrival; List<Vector2> firstWay = null;
            if (cut >= 0) arrival = first.At(cut);
            else
            {
                firstWay = HarbourWay(hb, first.End, null);
                if (firstWay == null) { map.Log.Add("Scrambled lanes: port " + v.Port + ": no clear water reached from its harbour: not drawn."); return null; }
                arrival = firstWay[firstWay.Count - 1]; map.HarbourWays++;
            }
            h.Approach = arrival; moves.Add(Vector2.Distance(arrival, v.Approach + o));
            var closed = new List<int>();
            foreach (var b in berths)
            {
                Check();
                var leg = b.Leg.Moved(o);
                var kept = Kept(hb, leg, out float c);
                var joined = c >= 0 || b != berths[0] || firstWay == null ? Join(hb, leg, c, arrival) : leg.Part(0, leg.Length, firstWay);
                LanePath second = null, keptSecond = null;
                if (b.Second != null)
                {
                    var s = b.Second.Moved(o); keptSecond = Kept(hb, s, out float cs);
                    second = Join(hb, s, cs, arrival);
                }
                // S1: another island's land within 20 m of its hull, the kept part of its leg or of its second way
                if (joined == null || OthersNear(hb, Outline(b.Centre + o, b.Heading, b.Length, b.Beam)) || OthersNear(hb, Samples(kept)) || keptSecond != null && OthersNear(hb, Samples(keptSecond)))
                { closed.Add(b.Number); map.BerthsClosed++; continue; }
                var nb = new TraderBerth { Port = b.Port, Number = b.Number, Class = b.Class, Side = b.Side, Centre = b.Centre + o, Heading = b.Heading, Length = b.Length, Beam = b.Beam, WayOut = b.WayOut, Leg = joined, Second = second };
                if (b.Direct != null)
                {
                    var d = b.Direct.Moved(o);
                    if (OthersNear(hb, Samples(d))) map.DirectDropped++; else nb.Direct = d;
                }
                h.Berths.Add(nb);
            }
            bool anchorOpen = false;
            if (v.AnchorLeg != null)
            {
                // stored from the approach point to the anchorage: walked out from the anchorage's centre, then turned back
                var leg = v.AnchorLeg.Moved(o).Reversed(); var kept = Kept(hb, leg, out float c); var joined = Join(hb, leg, c, arrival);
                LanePath keptSecond = null, second = null;
                if (v.AnchorSecond != null) { var s = v.AnchorSecond.Moved(o).Reversed(); keptSecond = Kept(hb, s, out float cs); second = Join(hb, s, cs, arrival); }
                if (joined == null || OthersNear(hb, h.Anchorage, v.AnchorRadius + Close) || OthersNear(hb, Samples(kept)) || keptSecond != null && OthersNear(hb, Samples(keptSecond))) map.AnchoragesClosed++;
                else { h.AnchorLeg = joined.Reversed(); h.AnchorSecond = second?.Reversed(); anchorOpen = true; }
            }
            if (closed.Count > 0 || !anchorOpen && v.AnchorLeg != null)
                map.Log.Add("Scrambled lanes: port " + v.Port + ": another island's land within 20 m: " + (closed.Count > 0 ? "berths " + string.Join(", ", closed.Select(n => n.ToString()).ToArray()) + " closed" : "no berth closed") +
                    (!anchorOpen && v.AnchorLeg != null ? ", the anchorage closed" : "") + ".");
            if (h.Berths.Count == 0 && !anchorOpen) { map.Log.Add("Scrambled lanes: port " + v.Port + ": no berth or anchorage left: not drawn."); return null; }
            return h;
        }
        // The first place (every 5 m from the leg's start, then its end) clear of every island by the harbour's clearance; -1: none.
        private float Cut(Harbour hb, LanePath leg)
        {
            for (float s = 0; ; s += (float)Walk)
            {
                if (s > leg.Length) s = leg.Length;
                var p = leg.At(s);
                if (!Near(hb.Lands, p.x, p.y, hb.Clear, int.MinValue)) return s;
                if (s >= leg.Length) return -1;
            }
        }
        // The kept part of a leg: up to its first clear place (all of it when it never gets there).
        private LanePath Kept(Harbour hb, LanePath leg, out float cut)
        {
            cut = Cut(hb, leg);
            return cut >= 0 ? leg.Part(0, cut, null) : leg;
        }
        // A leg cut at its first clear place (or its end) and joined to the arrival point: straight where the line keeps the clearance,
        // else a harbour way; null when there is none.
        private LanePath Join(Harbour hb, LanePath leg, float cut, Vector2 arrival)
        {
            float at = cut >= 0 ? cut : leg.Length; var from = leg.At(at);
            if ((from - arrival).sqrMagnitude < 1f) return leg.Part(0, at, new[] { arrival });   // its own cut, or within a metre of it
            List<Vector2> more;
            if (Clear(hb, from, arrival)) more = new List<Vector2> { arrival };
            else { more = HarbourWay(hb, from, arrival); if (more == null) return null; map.HarbourWays++; }
            return leg.Part(0, at, more);
        }
        private bool Clear(Harbour hb, Vector2 a, Vector2 b)
        {
            float len = Vector2.Distance(a, b); int n = Mathf.Max(1, Mathf.CeilToInt(len / (float)Walk));
            for (int k = 0; k <= n; k++) { var p = Vector2.Lerp(a, b, (float)k / n); if (Near(hb.Lands, p.x, p.y, hb.Clear, int.MinValue)) return false; }
            return true;
        }
        private bool OthersNear(Harbour hb, IEnumerable<Vector2> pts)
        {
            foreach (var p in pts) if (Near(hb.Lands, p.x, p.y, Close, hb.Island)) return true;
            return false;
        }
        private bool OthersNear(Harbour hb, Vector2 p, double d) => Near(hb.Lands, p.x, p.y, d, hb.Island);
        private static IEnumerable<Vector2> Samples(LanePath leg)
        {
            for (float s = 0; s < leg.Length; s += 2.5f) yield return leg.At(s);
            yield return leg.End;
        }
        // A berth's hull rectangle's edge, every 2 m (heading in degrees from +z toward +x).
        private static IEnumerable<Vector2> Outline(Vector2 c, float heading, float length, float beam)
        {
            float t = heading * Mathf.Deg2Rad; var f = new Vector2(Mathf.Sin(t), Mathf.Cos(t)); var r = new Vector2(Mathf.Cos(t), -Mathf.Sin(t));
            var corners = new[] { c + f * length / 2 + r * beam / 2, c + f * length / 2 - r * beam / 2, c - f * length / 2 - r * beam / 2, c - f * length / 2 + r * beam / 2 };
            for (int k = 0; k < 4; k++)
            {
                var a = corners[k]; var b = corners[(k + 1) % 4]; int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / 2f));
                for (int i = 0; i < n; i++) yield return Vector2.Lerp(a, b, (float)i / n);
            }
        }
        // A harbour way on a 10 m grid (as the lane map routed its berth legs, as far as the charts go): cells keeping 12 m from the
        // harbour's own island and 20 m from any other land, dearer (x3) within 40 m of land; cells within 30 m of its ends free. To
        // `to`, or with `to` null to the nearest water clear by the harbour's clearance. Its points after `from`, smoothed; null: none.
        private List<Vector2> HarbourWay(Harbour hb, Vector2 from, Vector2? to)
        {
            foreach (double margin in new[] { 400.0, 1500.0 })
            {
                var way = HarbourSearch(hb, from, to, margin);
                if (way != null) return way;
            }
            return null;
        }
        private List<Vector2> HarbourSearch(Harbour hb, Vector2 from, Vector2? to, double margin)
        {
            const double cell = 10, free = 30;
            var goal = to ?? from;
            double ox = Math.Floor((Math.Min(from.x, goal.x) - margin) / cell) * cell, oz = Math.Floor((Math.Min(from.y, goal.y) - margin) / cell) * cell;
            int w = (int)Math.Ceiling((Math.Max(from.x, goal.x) + margin - ox) / cell) + 1, h = (int)Math.Ceiling((Math.Max(from.y, goal.y) + margin - oz) / cell) + 1;
            var state = new sbyte[w * h];   // 0 unknown, 1 passable, 2 passable near land, -1 blocked
            var g = new float[w * h]; var prev = new int[w * h]; var done = new bool[w * h];
            for (int i = 0; i < g.Length; i++) g[i] = float.MaxValue;
            int Index(Vector2 p) => Mathf.Clamp((int)((p.y - oz) / cell), 0, h - 1) * w + Mathf.Clamp((int)((p.x - ox) / cell), 0, w - 1);
            Vector2 Centre(int i) => new Vector2((float)(ox + (i % w + .5) * cell), (float)(oz + (i / w + .5) * cell));
            bool EndZone(Vector2 p) => (p - from).sqrMagnitude < free * free || to.HasValue && (p - to.Value).sqrMagnitude < free * free;
            sbyte State(int i)
            {
                if (state[i] != 0) return state[i];
                var p = Centre(i);
                bool pass = EndZone(p) || !NearOwn(hb.Lands, p.x, p.y, OwnPass, hb.Island) && !Near(hb.Lands, p.x, p.y, Close, hb.Island);
                return state[i] = !pass ? (sbyte)-1 : Near(hb.Lands, p.x, p.y, NearLand, int.MinValue) ? (sbyte)2 : (sbyte)1;
            }
            int start = Index(from), target = to.HasValue ? Index(to.Value) : -1, gx = target >= 0 ? target % w : 0, gz = target >= 0 ? target / w : 0;
            float H(int i) { if (target < 0) return 0; int dx = Math.Abs(i % w - gx), dz = Math.Abs(i / w - gz); return Math.Max(dx, dz) + .41421f * Math.Min(dx, dz); }
            var heap = new MinHeap(); g[start] = 0; prev[start] = -1; heap.Push(start, H(start));
            int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 }, DZ = { 0, 0, 1, -1, 1, -1, 1, -1 }; float[] C = { 1, 1, 1, 1, 1.41421f, 1.41421f, 1.41421f, 1.41421f };
            int found = -1, expanded = 0;
            while (heap.Count > 0)
            {
                int u = heap.Pop(); if (done[u]) continue; done[u] = true;
                if (++expanded % 2048 == 0) Check();
                if (u == target || target < 0 && !Near(hb.Lands, Centre(u).x, Centre(u).y, hb.Clear, int.MinValue)) { found = u; break; }
                int ux = u % w, uz = u / w;
                for (int k = 0; k < 8; k++)
                {
                    int vx = ux + DX[k], vz = uz + DZ[k]; if (vx < 0 || vz < 0 || vx >= w || vz >= h) continue;
                    int v = vz * w + vx; if (done[v]) continue;
                    sbyte s = State(v); if (s < 0) continue;
                    if (k >= 4 && (State(uz * w + vx) < 0 || State(vz * w + ux) < 0)) continue;
                    float nd = g[u] + C[k] * (s == 2 ? 3f : 1f);
                    if (nd < g[v]) { g[v] = nd; prev[v] = u; heap.Push(v, nd + H(v)); }
                }
            }
            if (found < 0) return null;
            // the start cell itself qualifies (it holds `to`, or is clear water already): one point, as the smoothing below needs two
            // cells and gave none (MakeHarbour's last point threw, Join left a leg short; the deep review of 2026-11-02, Claude's fix)
            if (prev[found] < 0) return new List<Vector2> { to ?? Centre(found) };
            var cells = new List<Vector2>();
            for (int k = found; k >= 0; k = prev[k]) cells.Add(Centre(k));
            cells.Reverse(); cells[0] = from;
            if (to.HasValue) cells[cells.Count - 1] = to.Value;
            // smoothed: each point kept only where the straight line past it would leave the passable water
            bool LineOk(Vector2 a, Vector2 b)
            {
                float len = Vector2.Distance(a, b); int n = Mathf.Max(1, Mathf.CeilToInt(len / 2.5f));
                for (int k = 1; k < n; k++)
                {
                    var p = Vector2.Lerp(a, b, (float)k / n);
                    if (!EndZone(p) && (NearOwn(hb.Lands, p.x, p.y, OwnPass, hb.Island) || Near(hb.Lands, p.x, p.y, Close, hb.Island))) return false;
                }
                return true;
            }
            var outp = new List<Vector2>(); int i0 = 0;
            while (i0 < cells.Count - 1)
            {
                int j = i0 + 1;
                while (j + 1 < cells.Count && LineOk(cells[i0], cells[j + 1])) j++;
                outp.Add(cells[j]); i0 = j;
            }
            return outp;
        }

        // ---- The lanes (section 2: "The lanes") ----
        private void MakeLanes()
        {
            var ports = map.Harbours.Keys.OrderBy(p => p).ToList();
            // a port shut in by land (its arrival point joined to no open sea through the water kept clear): no lanes, not drawn
            foreach (int p in ports) { Check(); if (!grid.Open(map.Harbours[p].Approach)) map.Shut.Add(p); }
            foreach (int p in map.Shut) { var q = map.Harbours[p].Approach; map.Log.Add("Scrambled lanes: port " + p + " is shut in by land (no open sea from its arrival point at (" + q.x.ToString("F0") + ", " + q.y.ToString("F0") + ")): not drawn."); }
            var byGroup = ports.GroupBy(p => map.GroupOf[p]).ToDictionary(g => g.Key, g => g.ToList());
            var mid = new Dictionary<int, Vector2>(); var rad = new Dictionary<int, float>();
            foreach (var g in byGroup)
            {
                var c = Vector2.zero; foreach (int p in g.Value) c += map.Harbours[p].Approach; c /= g.Value.Count;
                mid[g.Key] = c; rad[g.Key] = g.Value.Max(p => Vector2.Distance(map.Harbours[p].Approach, c)) + 1500f;
            }
            var open = ports.Where(p => !map.Shut.Contains(p)).ToList();
            var groups = byGroup.Keys.OrderBy(k => k).ToList();
            // inside a group: the way between each pair of arrival points
            for (int a = 0; a < open.Count; a++)
                for (int b = a + 1; b < open.Count; b++)
                    if (map.GroupOf[open[a]] == map.GroupOf[open[b]])
                        map.InGroup[(long)open[a] * 1000 + open[b]] = Search(map.Harbours[open[a]].Approach, map.Harbours[open[b]].Approach, open[a] + "-" + open[b]);
            // between groups: one lane per pair, between an exit on each group's edge toward the other (its ports' middle, out past its
            // farthest port by 1.5 km, then the nearest clear cell joined to open sea)
            for (int a = 0; a < groups.Count; a++)
                for (int b = a + 1; b < groups.Count; b++)
                {
                    int ga = groups[a], gb = groups[b]; var d = (mid[gb] - mid[ga]).normalized;
                    var ea = grid.NearestOpen(mid[ga] + d * rad[ga]); var eb = grid.NearestOpen(mid[gb] - d * rad[gb]);
                    map.Exits[(long)ga * 100 + gb] = ea; map.Exits[(long)gb * 100 + ga] = eb;
                    map.Lanes[(long)ga * 100 + gb] = Search(ea, eb, "group " + ga + "|" + gb);
                }
            // each port to its group's exit toward every other group
            foreach (int p in open)
                foreach (int g in groups)
                {
                    int own = map.GroupOf[p]; if (g == own) continue;
                    map.PortExit[(long)p * 100 + g] = Search(map.Harbours[p].Approach, map.Exits[(long)own * 100 + g], "port " + p + " to the exit toward group " + g);
                }
            foreach (int p in map.Shut) map.Harbours.Remove(p);
        }
        private List<Vector2> Search(Vector2 a, Vector2 b, string name)
        {
            Check();
            map.Searches++;
            var way = grid.Way(a, b, out bool straight);
            if (way == null) { map.NoWay++; map.Log.Add("Scrambled lanes: no way found " + name + "."); return null; }
            if (straight) map.Straight++; else map.Detoured++;
            float len = 0; for (int k = 1; k < way.Count; k++) len += Vector2.Distance(way[k - 1], way[k]);
            map.LongestWay = Mathf.Max(map.LongestWay, len);
            return way;
        }

        // The land kept clear on a 50 m grid (sparse, so a 4x map costs no more than a 1x one): every cell whose square comes within
        // 100 m of a land cell's (5 x 5 cells round it), so a lane through free cells keeps 100 m from the charted land; lines are walked
        // cell by cell. Within 600 m of a way's ends 50 m (3 x 3: an arrival point between an island's own rocks, Firefly Grotto's, is
        // shut in by the 100 m on some of the grid's alignments), within 200 m none (the prototype's); lagoon land within 800 m of a
        // lagoon port only its own cells (the prototype's 20 m disk on 50 m cells: the lagoon's 40 m lanes). The rest is the prototype's
        // (routetime/Program.cs).
        private sealed class RouteGrid
        {
            internal const double Cell = 50, EndFree = 200, EndNear = 600;
            internal readonly HashSet<long> Blocked = new HashSet<long>(), BlockedNear = new HashSet<long>();
            private readonly List<double[]> boxes = new List<double[]>();   // each land's blocked cells' bounds: x0, z0, x1, z1
            private readonly Action check;
            private HashSet<long> tiles;
            private readonly HashSet<long> shutIn = new HashSet<long>(), openCells = new HashSet<long>();
            internal static long Key(long cx, long cz) => (cx << 32) ^ (cz & 0xffffffffL);
            internal RouteGrid(List<Placed> lands, List<Vector2> lagoonPorts, Action check)
            {
                this.check = check;
                foreach (var l in lands)
                {
                    check();
                    var cells = new HashSet<long>();
                    if (l.Chart != null)
                    {
                        double ox = l.Ox, oz = l.Oz;
                        l.Chart.Each((i, j) =>
                        {
                            // every 50 m cell the moved 10 m square touches
                            double x0 = i * LandChart.Cell + ox, z0 = j * LandChart.Cell + oz;
                            long a0 = (long)Math.Floor(x0 / Cell), a1 = (long)Math.Floor((x0 + LandChart.Cell - 1e-6) / Cell), b0 = (long)Math.Floor(z0 / Cell), b1 = (long)Math.Floor((z0 + LandChart.Cell - 1e-6) / Cell);
                            for (long a = a0; a <= a1; a++) for (long b = b0; b <= b1; b++) cells.Add(Key(a, b));
                        });
                    }
                    else
                    {
                        long r = (long)Math.Ceiling(l.R / Cell) + 1, cx = (long)Math.Floor(l.Cx / Cell), cz = (long)Math.Floor(l.Cz / Cell);
                        for (long a = -r; a <= r; a++) for (long b = -r; b <= r; b++)
                            {
                                double x = (cx + a + .5) * Cell, z = (cz + b + .5) * Cell;
                                if ((x - l.Cx) * (x - l.Cx) + (z - l.Cz) * (z - l.Cz) <= (l.R + Cell) * (l.R + Cell)) cells.Add(Key(cx + a, cz + b));
                            }
                    }
                    if (cells.Count == 0) continue;
                    long bx0 = long.MaxValue, bz0 = long.MaxValue, bx1 = long.MinValue, bz1 = long.MinValue;
                    foreach (long k in cells)
                    {
                        long cx = k >> 32, cz = (int)(k & 0xffffffffL);
                        bool lagoon = false;
                        if (l.Lagoon) foreach (var q in lagoonPorts) if (Sq((cx + .5) * Cell - q.x) + Sq((cz + .5) * Cell - q.y) <= ScrambledMap.LagoonReach * ScrambledMap.LagoonReach) { lagoon = true; break; }
                        // the cells whose square lies nearer than the clearance to this one's (|offset| - 1 cells between them)
                        int r = lagoon ? 0 : 2, rn = lagoon ? 0 : 1;
                        for (int a = -r; a <= r; a++) for (int b = -r; b <= r; b++) Blocked.Add(Key(cx + a, cz + b));
                        for (int a = -rn; a <= rn; a++) for (int b = -rn; b <= rn; b++) BlockedNear.Add(Key(cx + a, cz + b));
                        bx0 = Math.Min(bx0, cx - r); bz0 = Math.Min(bz0, cz - r); bx1 = Math.Max(bx1, cx + r); bz1 = Math.Max(bz1, cz + r);
                    }
                    boxes.Add(new[] { bx0 * Cell, bz0 * Cell, (bx1 + 1) * Cell, (bz1 + 1) * Cell });
                }
            }
            private static double Sq(double v) => v * v;
            private bool IsBlocked(long cx, long cz) => Blocked.Contains(Key(cx, cz));

            // Open sea: a cell whose 1 km tile no land's box touches. A cell is open when clear cells join it to open sea (8 moves, no
            // corner cut, as A*); with `end`, cells within 200 m of p are clear. A fill's result is kept only when it had no free end
            // (one through an end's free cells holds water joined only that way: EconomyOverhaul_FAILED.md).
            private bool OpenSea(long cx, long cz)
            {
                if (tiles == null)
                {
                    tiles = new HashSet<long>();
                    foreach (var b in boxes)
                        for (long tx = (long)Math.Floor(b[0] / 1000); tx <= (long)Math.Floor(b[2] / 1000); tx++)
                            for (long tz = (long)Math.Floor(b[1] / 1000); tz <= (long)Math.Floor(b[3] / 1000); tz++) tiles.Add(Key(tx, tz));
                }
                return !tiles.Contains(Key((long)Math.Floor(cx * Cell / 1000), (long)Math.Floor(cz * Cell / 1000)));
            }
            private static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 }, DZ = { 0, 0, 1, -1, 1, -1, 1, -1 };
            internal bool Open(Vector2 p, bool end = true)
            {
                long sx = (long)Math.Floor(p.x / Cell), sz = (long)Math.Floor(p.y / Cell);
                bool Free(long x, long z) => end ? !BlockedFor(x, z, p, p) : !Blocked.Contains(Key(x, z));
                if (!Free(sx, sz) || shutIn.Contains(Key(sx, sz))) return false;
                if (openCells.Contains(Key(sx, sz))) return true;
                var seen = new HashSet<long> { Key(sx, sz) }; var queue = new Queue<long>(); queue.Enqueue(Key(sx, sz));
                while (queue.Count > 0 && seen.Count < 300000)
                {
                    long k = queue.Dequeue(); long x = k >> 32, z = (int)(k & 0xffffffffL);
                    if ((seen.Count & 4095) == 0) check();
                    if (OpenSea(x, z) || openCells.Contains(k)) { if (!end) openCells.UnionWith(seen); return true; }
                    for (int d = 0; d < 8; d++)
                    {
                        long vx = x + DX[d], vz = z + DZ[d];
                        if (!Free(vx, vz) || d >= 4 && (!Free(x + DX[d], z) || !Free(x, z + DZ[d])) || !seen.Add(Key(vx, vz))) continue;
                        queue.Enqueue(Key(vx, vz));
                    }
                }
                if (!end && queue.Count == 0) shutIn.UnionWith(seen);
                return false;
            }
            // The nearest clear cell joined to open sea (an exit point).
            internal Vector2 NearestOpen(Vector2 p)
            {
                long cx = (long)Math.Floor(p.x / Cell), cz = (long)Math.Floor(p.y / Cell);
                var ring = new List<KeyValuePair<long, long>>();
                for (int r = 0; r < 4000; r++)
                {
                    check(); ring.Clear();
                    for (int a = -r; a <= r; a++) for (int b = -r; b <= r; b++)
                            if (Math.Max(Math.Abs(a), Math.Abs(b)) == r && !IsBlocked(cx + a, cz + b)) ring.Add(new KeyValuePair<long, long>(a * a + b * b, Key(cx + a, cz + b)));
                    foreach (var c in ring.OrderBy(c => c.Key))
                    {
                        var q = new Vector2((float)(((c.Value >> 32) + .5) * Cell), (float)(((int)(c.Value & 0xffffffffL) + .5) * Cell));
                        if (Open(q, false)) return q;
                    }
                }
                return p;
            }
            // The 50 m cells a segment passes through, in order (Amanatides and Woo), with the distance along it where each is entered.
            private void Cells(Vector2 a, Vector2 b, double from, double to, Func<long, long, double, bool> cell)
            {
                double dx = b.x - a.x, dz = b.y - a.y, len = Math.Sqrt(dx * dx + dz * dz); if (len < 1e-9) { cell((long)Math.Floor(a.x / Cell), (long)Math.Floor(a.y / Cell), 0); return; }
                double ux = dx / len, uz = dz / len, sx0 = a.x + ux * from, sz0 = a.y + uz * from;
                long x = (long)Math.Floor(sx0 / Cell), z = (long)Math.Floor(sz0 / Cell);
                int stepX = ux > 0 ? 1 : -1, stepZ = uz > 0 ? 1 : -1;
                double tMaxX = Math.Abs(ux) < 1e-12 ? double.MaxValue : from + ((ux > 0 ? (x + 1) * Cell : x * Cell) - sx0) / ux;
                double tMaxZ = Math.Abs(uz) < 1e-12 ? double.MaxValue : from + ((uz > 0 ? (z + 1) * Cell : z * Cell) - sz0) / uz;
                double tDX = Math.Abs(ux) < 1e-12 ? double.MaxValue : Cell / Math.Abs(ux), tDZ = Math.Abs(uz) < 1e-12 ? double.MaxValue : Cell / Math.Abs(uz);
                double t = from;
                while (t <= to)
                {
                    if (!cell(x, z, t)) return;
                    if (tMaxX < tMaxZ) { t = tMaxX; tMaxX += tDX; x += stepX; } else { t = tMaxZ; tMaxZ += tDZ; z += stepZ; }
                }
            }
            // The parts of a-b inside the land's boxes (Liang-Barsky), as distances along it.
            private List<double[]> Spans(Vector2 a, Vector2 b)
            {
                double dx = b.x - a.x, dz = b.y - a.y, len = Math.Sqrt(dx * dx + dz * dz); var spans = new List<double[]>();
                foreach (var bx in boxes)
                {
                    double t0 = 0, t1 = 1; bool hit = true;
                    double[] pp = { -dx, dx, -dz, dz }, qq = { a.x - bx[0], bx[2] - a.x, a.y - bx[1], bx[3] - a.y };
                    for (int k = 0; k < 4 && hit; k++)
                    {
                        if (Math.Abs(pp[k]) < 1e-12) { if (qq[k] < 0) hit = false; continue; }
                        double t = qq[k] / pp[k];
                        if (pp[k] < 0) t0 = Math.Max(t0, t); else t1 = Math.Min(t1, t);
                        if (t0 > t1) hit = false;
                    }
                    if (hit) spans.Add(new[] { t0 * len, t1 * len });
                }
                return spans;
            }
            // Kept clear for a way between start and goal: free within 200 m of either, the 50 m rule within 600 m, else the 100 m one.
            private bool BlockedFor(long cx, long cz, Vector2 start, Vector2 goal)
            {
                double x = (cx + .5) * Cell, z = (cz + .5) * Cell, d = Math.Min(Sq(x - start.x) + Sq(z - start.y), Sq(x - goal.x) + Sq(z - goal.y));
                return d >= EndFree * EndFree && (d < EndNear * EndNear ? BlockedNear : Blocked).Contains(Key(cx, cz));
            }
            // The stretches of a-b (distances along it) through kept-clear land, cells within 200 m of its ends free; runs closer than
            // 500 m are one stretch.
            private List<double[]> BlockedStretches(Vector2 a, Vector2 b)
            {
                var hits = new List<double>();
                foreach (var sp in Spans(a, b))
                    Cells(a, b, sp[0], sp[1], (x, z, t) => { if (BlockedFor(x, z, a, b)) hits.Add(t); return true; });
                hits.Sort();
                var merged = new List<double[]>();
                foreach (double d in hits)
                {
                    if (merged.Count > 0 && d - merged[merged.Count - 1][1] < 500) merged[merged.Count - 1][1] = d;
                    else merged.Add(new[] { d, d });
                }
                return merged;
            }
            private bool LineClear(Vector2 a, Vector2 b, Vector2 start, Vector2 goal)
            {
                bool clear = true;
                foreach (var sp in Spans(a, b))
                {
                    Cells(a, b, sp[0], sp[1], (x, z, t) => { if (BlockedFor(x, z, start, goal)) { clear = false; return false; } return true; });
                    if (!clear) return false;
                }
                return true;
            }
            // The way from a to b: straight, with A* round each blocked stretch (windows 2, 8, 30 km), smoothed; null when a stretch has
            // no way round it. A stretch whose start or end lies in water walled in by land is searched as one way end to end.
            internal List<Vector2> Way(Vector2 a, Vector2 b, out bool straight)
            {
                var stretches = BlockedStretches(a, b); straight = stretches.Count == 0;
                if (straight) return new List<Vector2> { a, b };
                var path = new List<Vector2> { a };
                double len = Vector2.Distance(a, b); var u = (b - a) / (float)len;
                var spans = new List<Vector2[]>(); bool pocket = false;
                foreach (var st in stretches)
                {
                    double s0 = Math.Max(0, st[0] - 2 * Cell), s1 = Math.Min(len, st[1] + 2 * Cell);
                    if (s0 < EndFree) s0 = 0; if (len - s1 < EndFree) s1 = len;
                    var p0 = a + u * (float)s0; var p1 = a + u * (float)s1;
                    if (s0 > 0 && !Open(p0, false) || s1 < len && !Open(p1, false)) { pocket = true; break; }
                    spans.Add(new[] { s0 > 0 ? p0 : a, s1 < len ? p1 : b });
                }
                if (pocket) { spans.Clear(); spans.Add(new[] { a, b }); }
                foreach (var sp in spans)
                {
                    List<Vector2> detour = null;
                    foreach (double margin in new[] { 2000.0, 8000.0, 30000.0 }) { detour = AStar(sp[0], sp[1], a, b, margin); if (detour != null) break; }
                    if (detour == null) return null;
                    path.AddRange(detour);
                }
                path.Add(b);
                return Smooth(path, a, b);
            }
            private List<Vector2> Smooth(List<Vector2> path, Vector2 start, Vector2 goal)
            {
                var outp = new List<Vector2> { path[0] }; int i = 0;
                while (i < path.Count - 1)
                {
                    int j = i + 1;
                    while (j + 1 < path.Count && LineClear(path[i], path[j + 1], start, goal)) j++;
                    outp.Add(path[j]); i = j;
                }
                return outp;
            }
            // A* on the 50 m grid in the box round p0-p1 grown by margin; cells within 200 m of the way's ends free.
            private List<Vector2> AStar(Vector2 p0, Vector2 p1, Vector2 start, Vector2 goal, double margin)
            {
                long sx = (long)Math.Floor(p0.x / Cell), sz = (long)Math.Floor(p0.y / Cell), gx = (long)Math.Floor(p1.x / Cell), gz = (long)Math.Floor(p1.y / Cell);
                long m = (long)(margin / Cell), x0 = Math.Min(sx, gx) - m, z0 = Math.Min(sz, gz) - m;
                long wl = Math.Max(sx, gx) + m - x0 + 1, hl = Math.Max(sz, gz) + m - z0 + 1;
                if (wl * hl > 40000000L) return null;
                int w = (int)wl, h = (int)hl;
                var g = new float[w * h]; var from = new int[w * h]; var closed = new bool[w * h];
                for (int i = 0; i < g.Length; i++) g[i] = float.MaxValue;
                int S = (int)((sz - z0) * w + (sx - x0)), G = (int)((gz - z0) * w + (gx - x0));
                float H(int i) { float ddx = Math.Abs(i % w - (gx - x0)), ddz = Math.Abs(i / w - (gz - z0)); return Math.Max(ddx, ddz) + .41421f * Math.Min(ddx, ddz); }
                bool Free(long wx, long wz) => wx == sx && wz == sz || wx == gx && wz == gz || !BlockedFor(wx, wz, start, goal);
                var heap = new MinHeap(); g[S] = 0; from[S] = -1; heap.Push(S, H(S));
                float[] C = { 1, 1, 1, 1, 1.41421f, 1.41421f, 1.41421f, 1.41421f };
                int nodes = 0;
                while (heap.Count > 0)
                {
                    int cur = heap.Pop(); if (closed[cur]) continue; closed[cur] = true;
                    if (++nodes % 4096 == 0) check();
                    if (cur == G)
                    {
                        var cells = new List<Vector2>();
                        for (int k = cur; k >= 0; k = from[k]) cells.Add(new Vector2((float)((k % w + x0 + .5) * Cell), (float)((k / w + z0 + .5) * Cell)));
                        cells.Reverse(); return cells;
                    }
                    int ux = cur % w, uz = cur / w;
                    for (int k = 0; k < 8; k++)
                    {
                        int vx = ux + DX[k], vz = uz + DZ[k]; if (vx < 0 || vz < 0 || vx >= w || vz >= h) continue;
                        int v = vz * w + vx; if (closed[v] || !Free(vx + x0, vz + z0)) continue;
                        if (k >= 4 && (!Free(ux + DX[k] + x0, uz + z0) || !Free(ux + x0, uz + DZ[k] + z0))) continue;
                        float nd = g[cur] + C[k]; if (nd < g[v]) { g[v] = nd; from[v] = cur; heap.Push(v, nd + H(v)); }
                    }
                }
                return null;
            }
        }
    }

    // A binary min-heap of (item, key) (netstandard2.0 has no PriorityQueue).
    internal sealed class MinHeap
    {
        private int[] items = new int[256]; private float[] keys = new float[256]; private int count;
        internal int Count => count;
        internal void Push(int item, float key)
        {
            if (count == items.Length) { Array.Resize(ref items, count * 2); Array.Resize(ref keys, count * 2); }
            int i = count++;
            while (i > 0) { int p = (i - 1) >> 1; if (keys[p] <= key) break; items[i] = items[p]; keys[i] = keys[p]; i = p; }
            items[i] = item; keys[i] = key;
        }
        internal int Pop()
        {
            int top = items[0]; int last = items[--count]; float lk = keys[count]; int i = 0;
            while (true)
            {
                int c = 2 * i + 1; if (c >= count) break;
                if (c + 1 < count && keys[c + 1] < keys[c]) c++;
                if (keys[c] >= lk) break;
                items[i] = items[c]; keys[i] = keys[c]; i = c;
            }
            if (count > 0) { items[i] = last; keys[i] = lk; }
            return top;
        }
    }

    // The routes' work on a background thread (S2): asked for as soon as the islands are moved while a game loads or prepares
    // (VisibleTraders.AskEarly, the user's 10) and again at the first scan after, dropped by the next load or new game
    // (VisibleTraders.Reset: Cancel); the last map made is kept for the session and taken again for the same islands' places (a
    // reload of the same game). The thread is a background one, so quitting the game ends it.
    internal static class ScrambledLanes
    {
        private static int generation;
        private static ScrambledMap finished;     // handed over by the thread
        private static ScrambledMap ready, last;
        private static string failure;
        private static string asked;              // the islands' places (ScrambledInput.Key) of the work under way or made
        internal static bool Running { get; private set; }
        internal static string Failure => failure;
        internal static float RequestedAt { get; private set; } = -1;
        internal static float ReadyAt { get; private set; } = -1;
        internal static void Cancel()
        {
            Interlocked.Increment(ref generation); ready = null; failure = null; Running = false; RequestedAt = ReadyAt = -1; asked = null;
        }
        // Main thread: the routes for the map as it is now are made, or the session's last ones taken when the islands lie the same.
        // Asked again for the same islands' places (the first scan after an early ask) while that work goes on or its map is made: it
        // is kept; any other place (an island registered late, a world shift mid-read) starts the work again.
        internal static void Request(ScrambledInput input)
        {
            if (asked != null && asked == input.Key && (Running || ready != null))
            {
                Plugin.Log?.LogInfo("Scrambled lanes: asked again for the same islands' places: the routes asked for " + (Time.realtimeSinceStartup - RequestedAt).ToString("F1") + " s ago are kept.");
                return;
            }
            Cancel(); asked = input.Key;
            RequestedAt = Time.realtimeSinceStartup;
            if (last != null && last.Key == input.Key)
            {
                ready = last; ReadyAt = RequestedAt;
                Plugin.Log?.LogInfo("Scrambled lanes: the islands lie as in the last map made this session: its routes are taken again.");
                return;
            }
            int gen = Volatile.Read(ref generation); Running = true;
            var vanilla = LaneMap.VanillaHarbours;
            var thread = new Thread(() => Run(input, vanilla, gen)) { IsBackground = true, Name = "EconomyOverhaul scrambled lanes", Priority = System.Threading.ThreadPriority.BelowNormal };
            thread.Start();
        }
        private static void Run(ScrambledInput input, IReadOnlyDictionary<int, TraderHarbour> vanilla, int gen)
        {
            try
            {
                var map = ScrambleBuilder.Build(input, vanilla, () => Volatile.Read(ref generation) != gen);
                map.Generation = gen;
                if (Volatile.Read(ref generation) == gen) Interlocked.Exchange(ref finished, map);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (Volatile.Read(ref generation) == gen) Interlocked.Exchange(ref failure, e.ToString()); }
        }
        // Main thread, each frame: the map once its routes are ready (null before, and after a failure).
        internal static ScrambledMap Ready
        {
            get
            {
                var f = Interlocked.Exchange(ref finished, null);
                if (f != null && f.Generation == Volatile.Read(ref generation))
                {
                    ready = last = f; Running = false; ReadyAt = Time.realtimeSinceStartup;
                    foreach (var line in f.Log) Plugin.Log?.LogInfo(line);
                }
                if (Running && failure != null) Running = false;
                return ready;
            }
        }
    }
}

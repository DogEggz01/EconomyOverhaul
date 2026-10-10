using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization.Json;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

[assembly: InternalsVisibleTo("EconomyOverhaul.QA")]
[assembly: InternalsVisibleTo("EconomyOverhaul.Tests")]

namespace EconomyOverhaul
{
    [BepInPlugin(Guid, "EconomyOverhaul", Version)]
    // Before 0.7.1 this mod was EconomicRebalance: if that copy is still installed, BepInEx skips this one instead of loading both.
    [BepInIncompatibility("DogEggz.EconomicRebalance")]
    [BepInDependency(TradeSaleCompatibility.DizzyGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(NandCommandCompatibility.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.raddude.sailadex", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.DogEggz.postalexpansion", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.winter.customislandapi", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.winter.betterports", BepInDependency.DependencyFlags.SoftDependency)]
    // Loaded first when present, so a rented ship's shipyard limit can cover its extra buttons.
    [BepInDependency("com.nandbrew.shipyardexpansion", BepInDependency.DependencyFlags.SoftDependency)]
    // Loaded first when present, so its plunder can be hooked (DangerousWatersCompatibility).
    [BepInDependency(DangerousWatersCompatibility.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    // Loaded first when present, so its new-game start can wait for the economy (ScrambledSeasCompatibility).
    [BepInDependency(ScrambledSeasCompatibility.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "DogEggz.EconomyOverhaul";
        public const string Version = "0.9.2";
        internal static ManualLogSource Log;
        internal static Plugin Instance;
        private Harmony harmony;
        private void Awake()
        {
            Instance = this; Log = Logger;
            try
            {
                Settings.Bind(Config);
                Supply.Load();
                CargoStainMaterial.Load();
                harmony = new Harmony(Guid);
                harmony.PatchAll(typeof(Plugin).Assembly);
                TradeSaleCompatibility.Install(harmony);
                NandCommandCompatibility.Install(harmony);
                RentalYard.PatchOtherYardButtons(harmony);
                try { DangerousWatersCompatibility.Install(harmony); }
                catch (Exception e) { Logger.LogWarning("Dangerous Waters plunder compatibility unavailable: " + e.Message); }
                try { ScrambledSeasCompatibility.Install(harmony); }
                catch (Exception e) { Logger.LogWarning("Scrambled Seas start compatibility unavailable: " + e.Message); }
                Logger.LogInfo("EconomyOverhaul " + Version + " loaded; zero starting stock for new games, experimental port economy and capacity-based purchase floors.");
            }
            catch (Exception e)
            {
                harmony?.UnpatchSelf();
                TradeSaleCompatibility.Restore();
                Logger.LogError("EconomyOverhaul could not initialize. No partial patch set retained. " + e);
                enabled = false;
            }
        }
        private void Update()
        {
            WoodPlanks.Tick();
            RentalSign.Poll();
            if (!Startup.Running) BottleneckCompatibility.Poll();
            if (Startup.IsPreparing) Startup.Pump(); else World.Tick();
            Startup.SaveWhenReady();
        }
        private void OnGUI() { Startup.Draw(); World.DrawSaveWarning(); }
        private IEnumerator Start()
        {
            // Native Awake order can leave UI dependencies unavailable until all scene Starts finish.
            yield return null; yield return null;
            if (EconomyUI.instance) TradeLoanUI.Attach(EconomyUI.instance);
            if (MissionListUI.instance) LedgerUI.Attach(MissionListUI.instance);
            Pledges.RecordBerths();
            foreach (var water in UnityEngine.Object.FindObjectsOfType<BoatDamageWater>()) BoatWater.Register(water);
        }
        private void OnDestroy() { Startup.Cancel(); harmony?.UnpatchSelf(); TradeSaleCompatibility.Restore(); }
    }
    internal static class Fields
    {
        private static readonly Dictionary<string, FieldInfo> cache = new Dictionary<string, FieldInfo>();
        public static FieldInfo Find(Type type, string name)
        {
            string key = type.FullName + "." + name;
            if (!cache.TryGetValue(key, out FieldInfo f)) cache[key] = f = AccessTools.Field(type, name) ?? throw new MissingFieldException(key);
            return f;
        }
        public static T Get<T>(object obj, string name) => (T)Find(obj.GetType(), name).GetValue(obj);
        public static void Set(object obj, string name, object value) => Find(obj.GetType(), name).SetValue(obj, value);
        public static object Call(object obj, string name, params object[] args) => AccessTools.Method(obj.GetType(), name).Invoke(obj, args);
    }
    internal static class Embedded
    {
        // The rows of a CSV file in Data/ (embedded in the DLL), split at commas; with a header, its first line is skipped.
        internal static IEnumerable<string[]> Csv(string name, bool header = true)
        {
            using (var reader = new StreamReader(typeof(Embedded).Assembly.GetManifestResourceStream("EconomyOverhaul.Data." + name)))
            {
                if (header) reader.ReadLine();
                for (string line; (line = reader.ReadLine()) != null;) yield return line.Split(',');
            }
        }
    }
    internal static class World
    {
        internal static SaveState State = new SaveState();
        internal static bool Ready, Loading, SaveBlocked;
        // The game is being played in a world the mod has ready: nothing loading, the game not paused, saving not blocked.
        internal static bool Running => Ready && !Loading && !Startup.BlocksGameplay && !SaveBlocked && !GameState.currentlyLoading && Sun.sun && GameState.playing && !Sun.SunPaused();
        internal static readonly Dictionary<int, MarketState> Markets = new Dictionary<int, MarketState>();
        internal static readonly Dictionary<int, CargoState> Cargo = new Dictionary<int, CargoState>();
        internal static readonly HashSet<int> Removed = new HashSet<int>();
        internal static double CalendarHour => GameState.day * 24.0 + (Sun.sun ? Sun.sun.globalTime : 0);
        internal static IslandMarket Market(int port) => Port.ports != null && port >= 0 && port < Port.ports.Length && Port.ports[port] ? Port.ports[port].GetComponent<IslandMarket>() : null;
        internal static MarketState MarketData(int port)
        {
            if (!Markets.TryGetValue(port, out MarketState s)) { s = new MarketState { port = port }; Markets.Add(port, s); State.markets.Add(s); }
            return s;
        }
        internal static void Reset()
        {
            Startup.Cancel();
            TradeBook.Reset();
            Ready = false; Loading = false; SaveBlocked = false; SaveOffAdvice = ReloadAdvice; State = new SaveState();
            Fleet.BeginRegistrationGrace();
            Markets.Clear(); Cargo.Clear(); Removed.Clear(); Missions.Reset(); Missions.DeliveredItems.Clear();
            Tarps.Clear();
        }
        internal static void NewGame()
        {
            Reset(); Startup.Begin(zeroStock: true);
        }
        internal static void Tick()
        {
            if (!Ready || Loading || Startup.BlocksGameplay || GameState.currentlyLoading || !Sun.sun || SaveBlocked) return;
            Fx.AdvanceDay(GameState.day);
            TradeBook.AdvanceDay(GameState.day);
            // The rental first: a failed rent charge and a Bottomry day 11 on the same midnight make one move.
            Rentals.AdvanceDay(GameState.day);
            Bottomry.AdvanceDay(GameState.day);
            Economy.Configure();
            if (!GameState.playing || GameState.currentShipyard || (EconomyUI.instance && EconomyUI.instance.uiActive)) return;
            double dt = Time.deltaTime * Sun.sun.timescale / .008;
            State.trafficTime += dt;
            Fleet.Tick(dt);
        }
        internal static void Save()
        {
            if (Startup.BlocksGameplay) throw new InvalidOperationException("Economy preparation is not complete; save deferred");
            if (!Ready || SaveBlocked) return;
            foreach (var c in CargoCondition.Active) c.PrepareSave();
            CargoRecords.Prune();
            TradeBook.Collect();
            State.tarps = Tarps.Records();
            StateCodec.Validate(State);
            GameState.modData[Plugin.Guid] = StateCodec.Write(State);
        }
        // What the player can do while saving is off: a save this version cannot read (one from an older version) never will,
        // so start a new game; otherwise the last save was kept as it was, so reload it.
        private const string ReloadAdvice = "Reload your save", NewGameAdvice = "Start a new game";
        private static string SaveOffAdvice = ReloadAdvice;
        internal static string SaveOffNotice => "EconomyOverhaul save error. " + SaveOffAdvice + "; see BepInEx log.";
        // Every save the game tries while saving is off says so again.
        internal static bool CanSave()
        {
            if (SaveBlocked) { Loans.Notify(SaveOffNotice); return false; }
            if (Startup.BlocksGameplay) return false;
            if (!Ready) return true;
            try { Save(); return true; }
            catch (Exception e)
            {
                SaveBlocked = true;
                Plugin.Log?.LogError("Economic save validation failed before writing; previous save protected. " + e);
                Loans.Notify(SaveOffNotice);
                return false;
            }
        }
        private static GUIStyle warningStyle;
        // While saving is off, a line in the top-left corner says so (the notice at loading shows during the black screen;
        // the corner keeps clear of the game's notifications at the top).
        internal static void DrawSaveWarning()
        {
            if (!SaveBlocked || !GameState.playing) return;
            if (warningStyle == null)
            {
                var paper = new Texture2D(1, 1); paper.SetPixel(0, 0, new Color(.93f, .87f, .74f, .92f)); paper.Apply();
                warningStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16, padding = new RectOffset(12, 12, 4, 4) };
                warningStyle.normal.textColor = Color.black; warningStyle.normal.background = paper;
            }
            GUI.Label(new Rect(10, 10, 640, 30), "EconomyOverhaul: saving is off. " + SaveOffAdvice + "; see the BepInEx log.", warningStyle);
        }
        internal static void Load()
        {
            BottleneckCompatibility.Refresh();
            bool firstInstall = !GameState.modData.TryGetValue(Plugin.Guid, out string json);
            if (firstInstall)
            {
                State = new SaveState();
            }
            else
            {
                try
                {
                    // Saves from before this version are not read (new games only): saving stays off to protect them.
                    State = StateCodec.Read(json);
                }
                catch (Exception e)
                {
                    SaveBlocked = true; SaveOffAdvice = NewGameAdvice; Ready = false; Plugin.Log.LogError("Economic save could not be restored; saving is blocked to protect the original data. " + e);
                    Loans.Notify(SaveOffNotice); return;
                }
            }
            if (ReputationNotifUI.instance) ReputationNotifUI.instance.HardCopyReps();
            Markets.Clear(); Cargo.Clear(); Removed.Clear();
            foreach (var s in State.markets) Markets.Add(s.port, s);
            foreach (var c in State.cargo) Cargo.Add(c.id, c);
            foreach (int id in State.removedCargo) Removed.Add(id);
            foreach (var row in Supply.ManagedRows())
            {
                var m = Market(row.Port);
                if (m) { Supply.Apply(m); Economy.EnsureKnownPrices(m); if (firstInstall) MarketData(row.Port); }
            }
            Ready = true; Loading = false; Fx.Ensure(); Fx.Sync(); Fleet.BeginRegistrationGrace(); Economy.Configure(); TradeBook.Loaded(); Bottomry.Loaded(); Rentals.Loaded(); Tarps.Loaded();
            // Refresh each market's own current quotes after a curve upgrade. Foreign
            // reports and fleet-carried stock observations remain stale as before.
            foreach (var row in Supply.ManagedRows()) { var market = Market(row.Port); if (market) Economy.Refresh(market); }
            foreach (var c in new List<CargoCondition>(CargoCondition.Active)) c.Restore();
            Loans.PurgeRemoved();
            if (firstInstall)
            {
                Startup.Begin(zeroStock: false);
                Plugin.Log.LogInfo("First installation: queued 10 preheat + 50 combined cycles using loaded stocks.");
                return;
            }
            Fleet.ReconcileNetworks(BottleneckCompatibility.Active);
            Plugin.Log.LogInfo("Restored EconomyOverhaul state; normal simulation speed, no startup rerun.");
        }
    }
    // First of all prefixes: Scrambled Seas' prefix starts its own start routine (and skips the game's), which must find the
    // preparation already begun to wait for it.
    [HarmonyPatch(typeof(StartMenu), "StartNewGame")] internal static class NewGamePatch { [HarmonyPriority(Priority.First)] static void Prefix() => World.NewGame(); }
    [HarmonyPatch(typeof(SaveLoadManager), "LoadGame")] internal static class BeginLoadPatch
    {
        static void Prefix() { World.Reset(); World.Loading = true; GameState.modData.Remove(Plugin.Guid); }
        static void Postfix() { World.Loading = false; }
    }
    [HarmonyPatch(typeof(SaveLoadManager), "SaveModData")] internal static class SavePatch { static void Prefix() => World.Save(); }
    [HarmonyPatch(typeof(SaveLoadManager), "LoadModData")] internal static class LoadPatch { static void Postfix() => World.Load(); }
    [HarmonyPatch(typeof(SaveLoadManager), "SaveGame")] internal static class ProtectSavePatch { static bool Prefix(bool compressed) => Startup.RequestSave(compressed); }

    // ---- Settings ----
    internal static class Settings
    {
        internal static ConfigEntry<bool> WaterDamage, WaterDamageHint, CoverHint, TarpWear;
        internal static bool CoverHintEnabled => CoverHint?.Value ?? false;
        internal static ConfigEntry<string> MissionReputation;
        internal static bool WaterEnabled => WaterDamage?.Value ?? true;
        internal static bool TarpWearing => TarpWear?.Value ?? true;
        private static bool tarpWearWas = true;   // the option's value before its last change
        internal static bool WaterHintEnabled => WaterDamageHint?.Value ?? true;
        internal static float MissionRepFactor => MissionReputation?.Value == "Vanilla (100%)" ? 1 : .75f;
        internal static void Bind(ConfigFile config)
        {
            WaterDamage = config.Bind("Cargo", "Enable water damage", true,
                "Enable cargo water damage, drying, wet appearance and condition-based value/reward reductions. When off, saved condition pauses and cargo has full condition value. Re-enabling resumes the saved condition.");
            WaterDamageHint = config.Bind("Cargo", "Show water damage percentage", true,
                "Show remaining water condition (100% to 0%) below vulnerable cargo hints. Hidden when water damage is disabled. This setting only changes the hint, not damage or cargo value.");
            MissionReputation = config.Bind("Missions", "Mission reputation reward", "75%",
                new ConfigDescription("Ordinary cargo-mission reputation before water condition. Vanilla mail and Postal Expansion are excluded. Applies immediately to deliveries.",
                    new AcceptableValueList<string>("75%", "Vanilla (100%)")));
            CoverHint = config.Bind("Cargo", "Show cover percentage", false,
                "Show how much of vulnerable cargo's top is under cover (a deck, a roof or a canvas tarp), 0% to 100%, below its water damage line. Hidden when water damage is disabled. This setting only changes the hint.");
            CoverHint.SettingChanged += (s,e) => { foreach(var c in CargoCondition.Active) c.RefreshHint(); };
            TarpWear = config.Bind("Canvas", "Tarp wearing", true,
                "Tied canvas tarps lose 10 health a game day and tear at 0. When off, every tarp's health, colour and holes stay as they are. Turning it back on continues wearing from there.");
            WaterDamage.SettingChanged += (s,e) => { foreach(var c in CargoCondition.Active) c.RefreshSettings(); };
            WaterDamageHint.SettingChanged += (s,e) => { foreach(var c in CargoCondition.Active) c.RefreshHint(); };
            // Each tarp's health so far, worn as the option was, becomes its start from now (user, 2026-10-09).
            TarpWear.SettingChanged += (s,e) => { tarpWearWas = Tarps.Rebase(tarpWearWas); };
            tarpWearWas = TarpWear.Value;
        }
    }

    // ---- Notifications ----
    // The game shows a notification as one text over a parchment of fixed size and never wraps it (about 33 characters of
    // its font fill the parchment's width). Each line of the mod's messages is wrapped at word gaps to 92% of that width,
    // measured in the text's own font and units.
    internal static class NoticeText
    {
        internal const float Fill = .92f;
        private static float width;
        internal static string Wrap(string text)
        {
            var ui = NotificationUi.instance; var mesh = ui ? Fields.Get<TextMesh>(ui, "text") : null;
            if (!mesh || !mesh.font || string.IsNullOrEmpty(text)) return text;
            if (width <= 0) width = BoxWidth(ui, mesh) * Fill;
            if (width <= 0) return text;
            return string.Join("\n", text.Split('\n').Select(line => WrapLine(mesh, line)).ToArray());
        }
        // A line's width in the text's own units (before its scale).
        internal static float Measure(TextMesh mesh, string line)
        {
            mesh.font.RequestCharactersInTexture(line, mesh.fontSize, mesh.fontStyle); float w = 0;
            foreach (char c in line) if (mesh.font.GetCharacterInfo(c, out var info, mesh.fontSize, mesh.fontStyle)) w += info.advance * .1f * mesh.characterSize;
            return w;
        }
        private static string WrapLine(TextMesh mesh, string line)
        {
            var lines = new List<string>(); string current = "";
            foreach (var word in line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string next = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && Measure(mesh, next) > width) { lines.Add(current); current = word; } else current = next;
            }
            if (current.Length > 0) lines.Add(current);
            return string.Join("\n", lines.ToArray());
        }
        // The parchment's width in the text's units: its mesh width and the text's unit, both scaled up to their shared parent
        // (the shown parchment's own scale runs from 0 to 1, so neither is read in world units).
        internal static float BoxWidth(NotificationUi ui, TextMesh mesh)
        {
            var box = Fields.Get<GameObject>(ui, "UI"); if (!box) return 0;
            float best = 0; Transform parchment = null;
            foreach (var f in box.GetComponentsInChildren<MeshFilter>(true))
                if (f.gameObject != mesh.gameObject && f.sharedMesh && f.sharedMesh.bounds.size.x * Scale(f.transform, box.transform) > best)
                { best = f.sharedMesh.bounds.size.x * Scale(f.transform, box.transform); parchment = f.transform; }
            float unit = Scale(mesh.transform, box.transform);
            return parchment && unit > 0 ? best / unit : 0;
        }
        // x scale from t up to (not including) root.
        private static float Scale(Transform t, Transform root) { float s = 1; for (; t && t != root; t = t.parent) s *= Mathf.Abs(t.localScale.x); return s; }
    }

    // ---- Rules ----
    // Pure rules: shared by runtime and boundary/transaction tests.
    public static partial class Rules
    {
        public static double Clamp(double x, double lo, double hi) => Math.Max(lo, Math.Min(hi, x));
        public static int Round(double x) => checked((int)Math.Round(x, MidpointRounding.ToEven));
    }

    // ---- Save state ----
    // Every field is required: a save without one (an older version) is refused.
    [Serializable] public sealed class SaveState
    {
        public int version = 4;
        public int chronosReputation;
        public FxState fx;
        public double trafficTime;
        public List<MarketState> markets = new List<MarketState>();
        public List<BoatState> fleet = new List<BoatState>();
        public List<CargoState> cargo = new List<CargoState>();
        public List<MissionState> missions = new List<MissionState>();
        public double[] fractionalPenalty = new double[4];
        public List<int> removedCargo = new List<int>();
        // Loans: the calendar hour until which no loan of either kind may be taken after a sinking, Bottomry loans, pledges
        // a lender took (until bought back), and the regions a Bottomry default closed while its currency is below 0.
        public double loanPauseUntil;
        public List<BottomryLoan> bottomry = new List<BottomryLoan>();
        public int bottomryNextId;
        public List<SeizedPledge> seized = new List<SeizedPledge>();
        public List<DefaultBlock> defaults = new List<DefaultBlock>();
        // Pledges whose loans were cancelled by a sinking or forgiven by a bankruptcy: never pledged again. The last day processed.
        public List<int> burned = new List<int>();
        public int bottomryDay;
        // Ship rental: the running rental, and the ships that sank rented and pledged: never for rent again.
        public RentalState rental;
        public List<int> unrentable = new List<int>();
        // Ships to move onto their berth once it is free and the player is away (Pledges.SendHomeLater).
        public List<int> homeLater = new List<int>();
        // Trade-book layer (0.7.0): price snapshots shared by port entries, boat routes and the player's list.
        public List<BookSnapshot> bookSnapshots = new List<BookSnapshot>();
        public int bookNextId;
        // The player's frozen price list: snapshot ids, newest per port.
        public List<int> playerBook = new List<int>();
        // Last game day whose midnight report update ran (set when new-game preparation finishes).
        public int reportDay;
        // Canvas tarps (after 0.8.5): optional, so a 0.8.5 save without it loads (read back as none).
        [System.Runtime.Serialization.OptionalField] public List<TarpState> tarps = new List<TarpState>();
    }

    internal static class StateCodec
    {
        // Unity 2019 JsonUtility omits nested plugin-defined record lists. Use the
        // game's .NET serializer and verify the complete graph on the actual player.
        private static DataContractJsonSerializer Serializer() => new DataContractJsonSerializer(typeof(SaveState),
            new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = int.MaxValue });
        internal static string Write(SaveState state)
        {
            using (var stream = new MemoryStream()) { Serializer().WriteObject(stream, state); return Encoding.UTF8.GetString(stream.ToArray()); }
        }
        internal static SaveState Read(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var result = (SaveState)Serializer().ReadObject(stream);
                if (result != null && result.tarps == null) result.tarps = new List<TarpState>();   // a 0.8.5 save
                Validate(result); return result;
            }
        }
        private static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
        private static bool PortId(int id) => id == BottleneckCompatibility.PortId || id >= 0 && id < 34 && id != 7 && id != 8;
        private static bool Report(ReportState r) => r != null && PortId(r.port) && r.cap > 0 && Finite(r.cap) &&
            r.supply?.Length == 65 && r.values?.Length == 65 && r.buy?.Length == 65 && r.sell?.Length == 65 &&
            r.supply.All(x => Finite(x)) && r.values.All(x => Finite(x));
        internal static bool ValidDeparture(DepartureState d) => d != null && d.boat >= 0 && PortId(d.origin) && PortId(d.destination) &&
            d.goods != null && d.goods.Length <= 200 && d.goods.All(g => g > 0 && g < 65 && g != 45 && g != 51) &&
            Finite(d.departure) && Finite(d.arrival) && d.arrival >= d.departure && Finite(d.calendarHour) &&
            (d.group == null || FleetDefinitions.KnownGroup(d.group));
        internal static void Validate(SaveState s)
        {
            bool valid = s != null && s.version == 4 && s.chronosReputation >= 0 && s.chronosReputation <= Reputations.Cap && s.markets != null && s.fleet != null && s.cargo != null && s.missions != null &&
                s.fractionalPenalty?.Length == 4 && s.removedCargo != null &&
                Finite(s.trafficTime) && s.fractionalPenalty.All(x => Finite(x) && x >= 0 && x < 1) &&
                Finite(s.loanPauseUntil) && s.bottomry != null && s.seized != null && s.bottomryNextId >= 0 &&
                s.bottomry.All(Bottomry.Valid) && s.bottomry.Select(b => b.id).Distinct().Count() == s.bottomry.Count &&
                s.seized.All(p => p != null && p.pledge > 0) && s.seized.Select(p => p.pledge).Distinct().Count() == s.seized.Count &&
                s.defaults != null && s.defaults.All(d => d != null && d.region >= 0 && d.region < 4 && d.currency >= 0 && d.currency < 4) &&
                s.burned != null && s.burned.All(i => i > 0) && s.bottomryDay >= 0 &&
                (s.rental == null || s.rental.ship > 0 && s.rental.region >= 0 && s.rental.region < 4 && s.rental.currency >= 0 && s.rental.currency < 4 &&
                    s.rental.charges >= 0 && s.rental.deposit >= 0 && s.rental.daily > 0 && s.rental.startDay >= 0 && s.rental.lastDay >= s.rental.startDay) &&
                s.unrentable != null && s.unrentable.All(i => i > 0) && s.homeLater != null && s.homeLater.All(i => i > 0);
            if (!valid) throw new InvalidDataException("Incomplete or unsupported economic save");
            if (s.fx != null) Fx.Validate(s.fx);
            foreach (var market in s.markets)
                if (market?.lastDeparture != null && (!ValidDeparture(market.lastDeparture) || market.lastDeparture.origin != market.port))
                { Plugin.Log?.LogWarning("Discarded invalid historical departure report at port " + market.port); market.lastDeparture = null; }
            valid = s.markets.All(m => m != null && PortId(m.port) && Finite(m.bonus) && m.bonus >= 0 && Finite(m.timer) && Finite(m.warehouseTimer) &&
                m.reports != null && m.reports.All(Report)) && s.markets.Select(m => m.port).Distinct().Count() == s.markets.Count;
            valid &= s.cargo.All(c => c != null && c.id > 0 && c.good >= 1 && c.good <= 64 && c.good != 45 && Finite(c.health) && c.health >= 0 && c.health <= 100 &&
                Finite(c.fxPressure) && c.fxPressure >= 0 && (c.fxPressure == 0 || c.fxCurrency >= 0 && c.fxCurrency < 3) &&
                (c.stainHistory == null || c.stainHistory.Length == CargoStainHistory.Bytes) &&
                (!c.loan || c.region >= 0 && c.region < 4 && c.principal > 0 && c.interest >= 0 &&
                Finite(c.rate) && c.rate >= 0 && c.rate <= 1 && Finite(c.reservation) && c.reservation > 0)) && s.cargo.Select(c => c.id).Distinct().Count() == s.cargo.Count;
            valid &= s.fleet.Count == FleetDefinitions.Total && s.fleet.All(b => b != null && PortId(b.origin) && (b.destination == -1 || PortId(b.destination)) &&
                b.units > 0 && b.units <= (b.group == "chronos" ? 150 : 120) && b.weight > 0 && b.speed > 0 && Finite(b.speed) && Finite(b.wait) && Finite(b.remaining) && Finite(b.trip) &&
                b.network != null && b.network.Length > 0 && b.network.All(PortId) && b.goods != null && b.goods.Length <= b.units &&
                b.goods.All(g => g > 0 && g < 65 && g != 45 && g != 51) && b.reports != null && b.reports.All(Report) &&
                FleetDefinitions.KnownGroup(b.group)) && s.fleet.Select(b => b.id).Distinct().Count() == s.fleet.Count;
            valid &= s.missions.All(m => m != null && PortId(m.origin) && PortId(m.destination) && m.creditedDeliveries >= 0);
            // 0.7.0 daily reports and trade book.
            bool Entry(BookEntry e) => e != null && PortId(e.port) && Finite(e.seen) && (FleetDefinitions.KnownGroup(e.group) || e.group == "small2");
            valid &= s.markets.All(m => (m.incoming == null || m.incoming.All(Report)) &&
                (m.book == null || m.book.All(Entry)) && (m.bookIncoming == null || m.bookIncoming.All(Entry)) &&
                (m.released == null || m.released.Count <= 128));
            valid &= s.bookSnapshots == null || s.bookSnapshots.All(x => x != null && PortId(x.port) && Finite(x.seen) && x.buy != null && x.buy.Length >= 65 && x.sell != null && x.sell.Length >= 65) &&
                s.bookSnapshots.Select(x => x.id).Distinct().Count() == s.bookSnapshots.Count && s.bookSnapshots.All(x => x.id > 0 && x.id <= s.bookNextId);
            valid &= s.fleet.All(b => b.book == null || b.book.Count <= 128) && (s.playerBook == null || s.playerBook.Count <= 128);
            valid &= s.tarps != null && s.tarps.All(Tarps.ValidRecord);
            if (!valid) throw new InvalidDataException("Economic save records failed integrity checks");
        }
    }

    internal static class CargoRecords
    {
        internal static CargoState Of(ShipItem item)
        {
            var save = item ? item.GetComponent<SaveablePrefab>() : null;
            return save && World.Cargo.TryGetValue(save.instanceId, out var record) ? record : null;
        }
        internal static void Destroyed(int id)
        {
            if (id <= 0 || !World.Cargo.ContainsKey(id)) return;
            if (World.Removed.Add(id)) World.State.removedCargo.Add(id);
            Prune();
        }
        internal static void Prune()
        {
            // Only explicit permanent removals establish absence. Unknown old records,
            // cached/stored items and temporary load teardown are deliberately retained.
            foreach (var c in World.State.cargo.Where(c => World.Removed.Contains(c.id) && !c.loan).ToArray())
            { World.State.cargo.Remove(c); World.Cargo.Remove(c.id); }
        }
    }
    [HarmonyPatch(typeof(ShipItem), "DestroyItem")]
    internal static class RetireCargoRecordPatch
    {
        static void Prefix(ShipItem __instance, out int __state)
        {
            __state = 0;
            if (!World.Ready || World.Loading || GameState.currentlyLoading || World.SaveBlocked) return;
            var save = __instance.GetComponent<SaveablePrefab>();
            if (save && save.GetParentObject() != -2 && save.GetParentObject() != -3) __state = save.instanceId;
        }
        static void Postfix(int __state) { CargoRecords.Destroyed(__state); }
    }

    // ---- Startup ----
    // All native economy calls stay on the main thread. Rendering time never advances
    // this clock; yielding between route candidates cannot create simulation debt.
    internal static class Startup
    {
        // Preparation prioritizes loading speed; normal gameplay retains its own budgets.
        internal const double FrameBudgetMs = 250;
        internal const int MaxBatchSteps = 16384;
        private static IEnumerator job;
        private static UnityEngine.Random.State random;
        private static float readyAfter;
        private static bool restorePlaying, restoreControl;
        private static bool? deferredSave;
        private static double anchorHour, horizon, now;
        private static int generation;
        private static GUIStyle progressStyle;
        internal static bool Running { get; private set; }
        internal static bool Failed { get; private set; }
        internal static bool IsPreparing => job != null;
        internal static bool BlocksGameplay => IsPreparing || Failed;
        internal static string Progress { get; private set; }
        internal static int Preheated { get; private set; }
        internal static int Combined { get; private set; }
        internal static int Arrivals { get; private set; }
        internal static int Batches { get; private set; }
        internal static double CpuMs { get; private set; }
        internal static double PeakBatchMs { get; private set; }
        internal static double PeakStepMs { get; private set; }
        internal static double CalendarHour => Running ? anchorHour + (now - horizon) * .008 : World.CalendarHour;

        internal static void Begin(bool zeroStock)
        {
            Cancel();
            restorePlaying = GameState.playing;
            restoreControl = restorePlaying && !GameState.currentlyLoading;
            GameState.playing = false;
            if (restoreControl) Refs.SetPlayerControl(false);
            anchorHour = World.CalendarHour; now = 0;
            horizon = SimulationClock.MarketInterval(.72f) * SimulationClock.WarmupCycles;
            Preheated = Combined = Arrivals = Batches = 0;
            CpuMs = PeakBatchMs = PeakStepMs = 0;
            // Reserve a seed, then isolate the job from random calls made by other frames.
            int seed = UnityEngine.Random.Range(1, int.MaxValue);
            var outer = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed); random = UnityEngine.Random.state;
            UnityEngine.Random.state = outer;
            readyAfter = Time.realtimeSinceStartup + 5;
            Progress = "Preparing economy: waiting for ports";
            job = Simulate(zeroStock);
        }

        internal static void Cancel()
        {
            (job as IDisposable)?.Dispose(); job = null;
            Running = Failed = false; deferredSave = null; generation++;
        }

        internal static void Pump(double budgetMs = FrameBudgetMs, int maxSteps = MaxBatchSteps)
        {
            if (!IsPreparing || Time.realtimeSinceStartup < readyAfter) return;
            var outer = UnityEngine.Random.state;
            var watch = Stopwatch.StartNew();
            UnityEngine.Random.state = random;
            bool finished = false;
            try
            {
                for (int i = 0; i < maxSteps; i++)
                {
                    double before = watch.Elapsed.TotalMilliseconds;
                    bool more = job.MoveNext();
                    PeakStepMs = Math.Max(PeakStepMs, watch.Elapsed.TotalMilliseconds - before);
                    if (!more) { finished = true; break; }
                    if (watch.Elapsed.TotalMilliseconds >= budgetMs) break;
                }
            }
            catch (Exception e)
            {
                (job as IDisposable)?.Dispose(); job = null; Running = false; Failed = true;
                World.SaveBlocked = true;
                Progress = "Economy preparation failed. Reload your save; see BepInEx log.";
                Plugin.Log?.LogError(Progress + " " + e);
            }
            finally
            {
                random = UnityEngine.Random.state; UnityEngine.Random.state = outer;
                double elapsed = watch.Elapsed.TotalMilliseconds;
                CpuMs += elapsed; PeakBatchMs = Math.Max(PeakBatchMs, elapsed); Batches++;
            }
            if (finished) Finish();
        }

        private sealed class MarketEvent
        {
            internal IslandMarket market;
            internal MarketState state;
            internal double interval, production, warehouse;
            internal int cycles;
        }

        private static IEnumerator Simulate(bool zeroStock)
        {
            // Optional island registration has had five real seconds to settle. Freeze
            // the managed-port set until this startup run completes.
            BottleneckCompatibility.Refresh();
            Running = true; World.Ready = true; Economy.Configure();
            var ports = new List<MarketEvent>();
            foreach (var row in Supply.ManagedRows())
            {
                var m = World.Market(row.Port); if (!m) continue;
                Supply.Apply(m);
                if (zeroStock)
                {
                    m.currentSupply = new float[m.production.Length];
                    Economy.EnsureKnownPrices(m, clear: true);
                    var office = m.GetComponent<IslandMissionOffice>();
                    if (office) office.LoadData(new int[office.GetData().Length]);
                }
                var s = World.MarketData(row.Port);
                s.reports.Clear();
                // Loaded vanilla reports describe the old snapshot, not future startup
                // history. Re-date them together so chronological replacements work.
                int historyDay = (int)Math.Floor(CalendarHour / 24);
                foreach (var report in m.knownPrices) if (report != null) report.day = historyDay;
                Economy.Refresh(m);
                double interval = SimulationClock.MarketInterval(m.econCycleDuration);
                ports.Add(new MarketEvent { market = m, state = s, interval = interval,
                    production = interval, warehouse = SimulationClock.WarehouseInterval });
                yield return null;
            }
            if (ports.Count == 0) throw new InvalidOperationException("No managed markets registered for startup");
            for (int cycle = 0; cycle < SimulationClock.PreheatCycles; cycle++)
            {
                Progress = $"Preparing economy: preheat {cycle + 1} / {SimulationClock.PreheatCycles}";
                foreach (var p in ports) { Economy.Cycle(p.market); yield return null; }
                Preheated = cycle + 1;
            }
            Progress = "Preparing economy: preparing traders";
            // Traders plan with their port's report (0.7.0): every port starts knowing every port's opening prices.
            foreach (var p in ports)
                foreach (var q in ports)
                {
                    if (p == q) continue;
                    var opening = Fleet.Report(q.state.reports, q.market.GetPortIndex());
                    if (opening != null && opening.approved) Fleet.Publish(p.market, p.state, opening);
                }
            var initialize = Fleet.InitializeSteps();
            while (initialize.MoveNext()) yield return null;
            double startTraffic = World.State.trafficTime;
            while (true)
            {
                // Stable ties: production, warehouse, boat; ports and boats retain ID order.
                double nextTime = double.PositiveInfinity;
                MarketEvent nextMarket = null; BoatState nextBoat = null; bool warehouse = false;
                foreach (var p in ports)
                    if (p.cycles < SimulationClock.WarmupCycles && p.production < nextTime)
                    { nextTime = p.production; nextMarket = p; }
                foreach (var p in ports)
                    if (!p.state.blocked && p.warehouse < nextTime)
                    { nextTime = p.warehouse; nextMarket = p; warehouse = true; }
                foreach (var boat in World.State.fleet)
                    if (now + boat.remaining < nextTime)
                    { nextTime = now + boat.remaining; nextBoat = boat; }
                double target = Math.Min(nextTime, horizon);
                double elapsed = Math.Max(0, target - now);
                foreach (var boat in World.State.fleet) boat.remaining = Math.Max(0, boat.remaining - elapsed);
                now = target; World.State.trafficTime = startTraffic + now;
                if (nextTime > horizon) break;
                if (nextBoat != null)
                {
                    if (nextBoat.destination >= 0) Arrivals++;
                    var step = Fleet.StartupEvent(nextBoat);
                    try { while (step.MoveNext()) yield return null; }
                    finally { (step as IDisposable)?.Dispose(); }
                }
                else if (warehouse)
                {
                    nextMarket.state.pending = 3;
                    Missions.DrainWarehouse(nextMarket.market, nextMarket.state);
                    nextMarket.warehouse = now + SimulationClock.WarehouseInterval;
                }
                else
                {
                    bool wasBlocked = nextMarket.state.blocked;
                    Economy.Cycle(nextMarket.market);
                    if (wasBlocked) nextMarket.warehouse = now + SimulationClock.WarehouseInterval;
                    nextMarket.cycles++;
                    nextMarket.production = (nextMarket.cycles + 1) * nextMarket.interval;
                    Combined = ports.Min(p => p.cycles);
                }
                Progress = $"Preparing economy: combined warmup {Combined} / {SimulationClock.WarmupCycles}";
                yield return null;
            }
            foreach (var p in ports)
            {
                p.state.timer = Math.Max(0, p.production - horizon) * .008 * 100;
                p.state.warehouseTimer = (p.state.blocked ? SimulationClock.WarehouseInterval : Math.Max(0, p.warehouse - horizon)) * .008 * 125;
            }
            // The final production event can refill a previously blocked warehouse.
            // Its single pending allocation has run; no historical event queue survives.
            Fx.ResetInitial();
            // Daily report updates start from the game's current day.
            World.State.reportDay = GameState.day;
        }

        private static void Finish()
        {
            (job as IDisposable)?.Dispose(); job = null; Running = false;
            try
            {
                StateCodec.Validate(World.State);
                // Never off here: a start routine that ran meanwhile (another mod's copy of the game's) has begun play.
                if (!GameState.playing) GameState.playing = restorePlaying;
                else if (!restorePlaying) Plugin.Log?.LogInfo("Play began during the economy preparation; it stays on.");
                if (restoreControl) Refs.SetPlayerControl(true);
                if (EconomyUI.instance && EconomyUI.instance.uiActive) EconomyUI.instance.RefreshPage();
                foreach (var p in World.State.markets) PortCapacityUI.RefreshVisible(p.port);
                Plugin.Log?.LogInfo($"Economy ready: {Preheated} preheat + {Combined} combined cycles; {World.State.fleet.Count} boats; {Arrivals} arrivals; {Batches} batches, {CpuMs:F1} ms CPU, peak batch {PeakBatchMs:F2} ms / step {PeakStepMs:F2} ms. No startup backlog.");
            }
            catch (Exception e)
            {
                Failed = true; World.SaveBlocked = true; GameState.playing = false;
                Progress = "Economy preparation failed. Reload your save; see BepInEx log.";
                Plugin.Log?.LogError(Progress + " " + e);
            }
        }

        internal static bool RequestSave(bool compressed)
        {
            if (IsPreparing) { deferredSave = compressed; return false; }
            return World.CanSave();
        }
        internal static void SaveWhenReady()
        {
            if (!deferredSave.HasValue || BlocksGameplay || World.SaveBlocked || !SaveLoadManager.readyToSave || !GameState.playing || GameState.currentlyLoading) return;
            bool compressed = deferredSave.Value; deferredSave = null;
            SaveLoadManager.instance.SaveGame(compressed);
        }
        internal static IEnumerator Gate(IEnumerator native)
        {
            int run = generation;
            try
            {
                while (run == generation)
                {
                    while (BlocksGameplay && run == generation) yield return null;
                    if (run != generation || !native.MoveNext()) yield break;
                    // LoadGameAnimation can start preparation inside MoveNext.
                    run = generation;
                    while (BlocksGameplay && run == generation) yield return null;
                    if (run != generation) yield break;
                    yield return native.Current;
                }
            }
            finally { (native as IDisposable)?.Dispose(); }
        }
        // Scrambled Seas' start routine runs on time: it moves the player to the start in the frame it scrambles the map, as
        // without this mod (user, 2026-10-06: held until the economy was ready, on a widened map the player started in the
        // ocean). Only the step after the disclaimer's F, which begins play, waits for the preparation; a restarted preparation
        // never drops the routine (that would leave the player at the menu).
        internal static IEnumerator StartOnTime(IEnumerator native, StartMenu menu)
        {
            try
            {
                while (true)
                {
                    while (BlocksGameplay && menu && Fields.Get<bool>(menu, "fPressed")) yield return null;
                    if (!native.MoveNext()) yield break;
                    yield return native.Current;
                }
            }
            finally { (native as IDisposable)?.Dispose(); }
        }
        internal static void Draw()
        {
            if (!BlocksGameplay) return;
            float scale = Mathf.Clamp(Screen.height / 1080f, .8f, 2);
            var rect = new Rect((Screen.width - 580 * scale) / 2f, Screen.height * .62f, 580 * scale, 94 * scale);
            GUI.Box(rect, "");
            if (progressStyle == null) progressStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            progressStyle.fontSize = Mathf.RoundToInt(20 * scale);
            GUI.Label(new Rect(rect.x + 16 * scale, rect.y + 8 * scale, rect.width - 32 * scale, rect.height - 16 * scale), Progress, progressStyle);
        }
    }
    [HarmonyPatch(typeof(StartMenu), "MovePlayerToStartPos")]
    internal static class PrepareNewGamePatch { static void Postfix(ref IEnumerator __result) => __result = Startup.Gate(__result); }
    [HarmonyPatch(typeof(StartMenu), "LoadGameAnimation")]
    internal static class PrepareLoadPatch { static void Postfix(ref IEnumerator __result) => __result = Startup.Gate(__result); }
    [HarmonyPatch(typeof(Sun), "SunPaused")]
    internal static class PrepareClockPatch { static void Postfix(ref bool __result) { if (Startup.BlocksGameplay) __result = true; } }
    [HarmonyPatch(typeof(EconomyUI), "OpenUI")]
    internal static class PrepareTradePatch { static bool Prefix() => !Startup.BlocksGameplay; }
    [HarmonyPatch(typeof(MissionListUI), "EnablePortMissionUI")]
    internal static class PrepareMissionsPatch { static bool Prefix() => !Startup.BlocksGameplay; }

    internal static class SimulationClock
    {
        internal const int PreheatCycles = 10, WarmupCycles = 50, CycleBudget = 8, FleetEventBudget = 128;
        internal const float NormalSpeed = .0003f;
        internal static double MarketInterval(float duration) => duration / (double)NormalSpeed / (.008 * 100);
        internal static double WarehouseInterval => .1 / NormalSpeed / (.008 * 125);
        internal static int Advance(ref double remaining, double elapsed, double interval, int budget, Action cycle, Func<bool> blocked = null)
        {
            remaining -= elapsed;
            int count = 0;
            while (remaining <= 0 && count < budget && !(blocked?.Invoke() ?? false))
            {
                remaining += interval;
                cycle(); count++;
            }
            return count; // Any negative remainder is saved backlog, not discarded time.
        }
    }

    // ---- Compatibility ----
    internal static class BottleneckCompatibility
    {
        internal const int PortId = 67;
        private const string PortName = "Bottleneck Is.";
        private static bool initialized, active;
        private static Port registeredPort;
        private static IslandMarket registeredMarket;
        private static IslandMarket nativeMarket;
        private static float[] nativeProduction;
        private static float nativeCap, nativeCycle, nativePurchaseLimit;
        private static float nextPoll;
        internal static bool Active => active;

        internal static void Refresh()
        {
            if (Startup.Running) return;
            Port foundPort = null; IslandMarket foundMarket = null;
            if (Port.ports != null && Port.ports.Length > PortId)
            {
                var candidate = Port.ports[PortId];
                if (candidate && candidate.portIndex == PortId && candidate.region == (PortRegion)1 &&
                    string.Equals(candidate.GetPortName(), PortName, StringComparison.OrdinalIgnoreCase))
                {
                    var market = candidate.GetComponent<IslandMarket>();
                    if (market && market.GetPortIndex() == PortId && market.production != null && market.production.Length >= 65)
                    { foundPort = candidate; foundMarket = market; }
                }
            }
            bool nowActive = foundPort && foundMarket;
            bool changed = !initialized || nowActive != active || foundPort != registeredPort || foundMarket != registeredMarket;
            if (changed)
            {
                if (nativeMarket && nativeMarket != foundMarket) RestoreNativeMarket();
                if (nowActive && (nativeMarket != foundMarket || nativeProduction == null)) CaptureNativeMarket(foundMarket);
                active = nowActive; registeredPort = foundPort; registeredMarket = foundMarket; initialized = true;
                Supply.SetBottleneckEnabled(active);
                if (Port.ports != null)
                    foreach (var port in Port.ports)
                    {
                        if (!port) continue;
                        var market = port.GetComponent<IslandMarket>();
                        if (market)
                        {
                            Supply.Apply(market);
                            if (Supply.Contains(market.GetPortIndex())) Economy.EnsureKnownPrices(market);
                        }
                    }
                if (!active) RestoreNativeMarket();
                if (World.Ready)
                {
                    if (active)
                    {
                        var market = World.Market(PortId);
                        if (market) { World.MarketData(PortId); Economy.Refresh(market); }
                    }
                    Fleet.ReconcileNetworks(active);
                }
            }
            SyncEconomyUI(EconomyUI.instance);
        }

        internal static void Poll()
        {
            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + .5f;
            Refresh();
        }

        private static void CaptureNativeMarket(IslandMarket market)
        {
            nativeMarket = market; nativeProduction = (float[])market.production.Clone();
            nativeCap = market.goodsSoftCapOverride; nativeCycle = market.econCycleDuration;
            nativePurchaseLimit = market.supplyPurchaseLimit;
        }

        private static void RestoreNativeMarket()
        {
            if (nativeMarket && nativeProduction != null)
            {
                Array.Copy(nativeProduction, nativeMarket.production, Math.Min(nativeProduction.Length, nativeMarket.production.Length));
                nativeMarket.goodsSoftCapOverride = nativeCap; nativeMarket.econCycleDuration = nativeCycle;
                nativeMarket.supplyPurchaseLimit = nativePurchaseLimit;
            }
            nativeMarket = null; nativeProduction = null;
        }

        internal static void SyncEconomyUI(EconomyUI ui)
        {
            if (!ui) return;
            int[][] islands = Fields.Get<int[][]>(ui, "bookmarkIslands");
            if (islands == null || islands.Length <= 1 || islands[1] == null) return;
            int[] current = islands[1];
            int occurrences = current.Count(port => port == PortId);
            if (active && occurrences != 1)
                islands[1] = current.Where(port => port != PortId).Concat(new[] { PortId }).ToArray();
            else if (!active && occurrences > 0)
                islands[1] = current.Where(port => port != PortId).ToArray();
            Fields.Set(ui, "bookmarkIslands", islands);
        }
    }

    [HarmonyPatch(typeof(EconomyUI), "InitializeRegionIslands")]
    internal static class BottleneckEconomyTabsPatch
    {
        private static void Postfix(EconomyUI __instance) => BottleneckCompatibility.SyncEconomyUI(__instance);
    }

    internal static class TradeSaleCompatibility
    {
        internal const string DizzyGuid = "com.dizzy.sailwind.fixes";
        private static readonly MethodInfo Sale = AccessTools.Method(typeof(EconomyUI), "SellGood");
        private static Patch suspended;

        internal static void Install(Harmony harmony)
        {
            if (suspended != null) return;
            var patch = Harmony.GetPatchInfo(Sale)?.Prefixes.FirstOrDefault(p =>
                p.owner == DizzyGuid &&
                p.PatchMethod.DeclaringType?.FullName == "Dizzy.Fixes.EconomyUiSellGoodPatch" &&
                p.PatchMethod.Name == "Prefix");
            if (patch == null) return;

            // HarmonyX can run both replacement prefixes even when one returns false.
            // Ordering them cannot prevent duplicate sales or cancellation before repayment.
            // Our sale handler already checks full cargo and settles the exact crate.
            suspended = patch;
            harmony.Unpatch(Sale, patch.PatchMethod);
            Plugin.Log.LogInfo("Dizzy compatibility: EconomyOverhaul handles trade-book sales; only Dizzy's overlapping sale prefix is suspended.");
        }

        internal static void Restore()
        {
            var patch = suspended;
            if (patch == null) return;
            if (Harmony.GetPatchInfo(Sale)?.Prefixes.Any(p =>
                p.owner == patch.owner && p.PatchMethod == patch.PatchMethod) != true)
            {
                new Harmony(patch.owner).Patch(Sale, prefix: new HarmonyMethod(patch.PatchMethod)
                {
                    priority = patch.priority,
                    before = patch.before,
                    after = patch.after
                });
            }
            suspended = null;
        }
    }

    // Dangerous Waters (optional): a boarding party's plunder (Plunder.TakeCargo destroys each item it takes) counts the
    // financed cargo it carries off as lost with a sinking (Loans.BeginPlunder / EndPlunder).
    internal static class DangerousWatersCompatibility
    {
        internal const string Guid = "com.roy.dangerouswaters";
        internal static void Install(Harmony harmony)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("DangerousWaters.Plunder", false)).FirstOrDefault(t => t != null);
            if (type == null) return;
            var take = AccessTools.Method(type, "TakeCargo", new[] { typeof(int) });
            if (take == null) { Plugin.Log?.LogWarning("Dangerous Waters plunder compatibility unavailable: Plunder.TakeCargo changed."); return; }
            harmony.Patch(take, prefix: new HarmonyMethod(typeof(DangerousWatersCompatibility), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(DangerousWatersCompatibility), nameof(End)));
            Plugin.Log?.LogInfo("Dangerous Waters: financed cargo a boarding party takes counts as lost with a sinking.");
        }
        private static void Begin() => Loans.BeginPlunder();
        private static Exception End(Exception __exception) { Loans.EndPlunder(); return __exception; }
    }
    // Scrambled Seas (optional): with a scramble on, its StartNewGame prefix moves the world and runs its own copy of the
    // game's start routine (MovePlayerToStartPos), skipping the game's. That copy runs on time and only its step that begins
    // play waits for the economy preparation (Startup.StartOnTime); otherwise the player could start mid-preparation.
    internal static class ScrambledSeasCompatibility
    {
        internal const string Guid = "com.nandbrew.scrambledseas";
        internal static void Install(Harmony harmony)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ScrambledSeas.Patches+StartNewGamePatch", false)).FirstOrDefault(t => t != null);
            if (type == null)
            {
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid)) Plugin.Log?.LogWarning("Scrambled Seas start compatibility unavailable: its start routine changed.");
                return;
            }
            var start = AccessTools.Method(type, "MovePlayerToStartPos");
            if (start == null || start.ReturnType != typeof(IEnumerator)) { Plugin.Log?.LogWarning("Scrambled Seas start compatibility unavailable: its start routine changed."); return; }
            if (start.GetParameters().FirstOrDefault()?.ParameterType != typeof(StartMenu)) { Plugin.Log?.LogWarning("Scrambled Seas start compatibility unavailable: its start routine changed."); return; }
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(ScrambledSeasCompatibility), nameof(Gate)));
            Plugin.Log?.LogInfo("Scrambled Seas: its new-game start runs on time; play begins once the economy is prepared.");
        }
        private static void Gate(ref IEnumerator __result, object[] __args) => __result = Startup.StartOnTime(__result, __args.Length > 0 ? __args[0] as StartMenu : null);
    }
    internal static class NandCommandCompatibility
    {
        internal const string Guid = "com.nandbrew.nandcommand";
        internal static void Install(Harmony harmony)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("NANDCommand.Commands.SetLevelCommand", false)).FirstOrDefault(t => t != null);
            if (type == null) return;
            var run = AccessTools.Method(type, "OnRun", new[] { typeof(List<string>) });
            if (run == null) { Plugin.Log?.LogWarning("NANDcommand SetLevel compatibility unavailable: command signature changed."); return; }
            harmony.Patch(run, prefix: new HarmonyMethod(typeof(NandCommandCompatibility), nameof(SetLevel)));
            var usage = AccessTools.PropertyGetter(type, "Usage");
            if (usage != null) harmony.Patch(usage, postfix: new HarmonyMethod(typeof(NandCommandCompatibility), nameof(Usage)));
            Plugin.Log?.LogInfo("NANDcommand: SetLevel 3 <level 0-10> now controls Chronos reputation.");
        }
        private static void Usage(ref string __result) => __result = "<region (0-2 native, 3 Chronos)> <level (0-10)>";
        internal static bool SetLevel(List<string> __0)
        {
            if (__0 == null || __0.Count == 0 || !int.TryParse(__0[0], out int region) || region != Reputations.Chronos) return true;
            string message;
            if (__0.Count != 2 || !int.TryParse(__0[1], out int level) || level < 0 || level > 10)
                message = "Usage: setlevel 3 <level 0-10>";
            else if (!World.Ready || World.Loading || World.SaveBlocked || Startup.BlocksGameplay || GameState.currentlyLoading)
                message = "Chronos reputation unavailable until the economy is ready.";
            else {
                World.State.chronosReputation = Reputations.Required(Reputations.Chronos, level);
                if (EconomyUI.instance && EconomyUI.instance.uiActive) EconomyUI.instance.RefreshPage();
                message = "Set Chronos to level " + level + " (" + World.State.chronosReputation + " reputation).";
            }
            Plugin.Log?.LogInfo(message); Loans.Notify(message);
            return false;
        }
    }
}

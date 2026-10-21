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
        public const string Version = "1.0.1";
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
            World.SaveRetryTick();
            VisibleTraders.Update();
        }
        private void FixedUpdate() => VisibleTraders.FixedUpdate();
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
        // The method looked up once per type and name, as Find caches fields (the same AccessTools result each call).
        private static readonly Dictionary<string, MethodInfo> methods = new Dictionary<string, MethodInfo>();
        public static object Call(object obj, string name, params object[] args)
        {
            string key = obj.GetType().FullName + "." + name;
            if (!methods.TryGetValue(key, out MethodInfo m)) methods[key] = m = AccessTools.Method(obj.GetType(), name);
            return m.Invoke(obj, args);
        }
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
            saveFailures = 0; retryAt = -1; keepLastGood = false;
            Fleet.BeginRegistrationGrace();
            Markets.Clear(); Cargo.Clear(); Removed.Clear(); Missions.Reset(); Missions.DeliveredItems.Clear();
            Tarps.Clear(); Uninstall.Reset(); VisibleTraders.Reset();
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
            VisibleTraders.TrafficDt = dt;
            Fleet.Tick(dt);
        }
        internal static void Save() => Save(true);
        // write false: everything but the writing, for the check before the game saves (CanSave). The writing (since state version 7
        // binary, StateCodec.Encode, about 15 ms; the JSON before it 100-110 ms, the probe savecodec) is done once a save, in the
        // game's SaveModData (SaveForGame); it was done in both until 2026-10-20 (the user: "make the mod serialize its economy once
        // per save (keep the safety check)").
        private static void Save(bool write)
        {
            // After the uninstall button the save carries no data of the mod (MIDSAVE_DRAFT.md).
            if (Uninstall.Prepared) { GameState.modData.Remove(Plugin.Guid); return; }
            if (Startup.BlocksGameplay) throw new InvalidOperationException("Economy preparation is not complete; save deferred");
            if (!Ready || SaveBlocked) return;
            foreach (var c in CargoCondition.Active) c.PrepareSave();
            CargoRecords.Prune();
            TradeBook.Collect();
            State.tarps = Tarps.Records();
            State.savedHour = CalendarHour;
            StateCodec.Validate(State);
            if (write) GameState.modData[Plugin.Guid] = StateCodec.Encode(State);
        }
        // The game's save (SaveLoadManager.DoSaveGame, through SaveModData at the end of the frame CanSave passed in): the state
        // checked again and written. Should it fail there, the game's save is stopped before its file is made (the exception ends
        // DoSaveGame; SaveSlots.PushBackups and the file come after SaveModData), so the last save stays as it was; saving turns
        // off as after a failed check, and the game's busy flag is cleared (the stopped coroutine never clears it, and every later
        // save would be refused as "not ready").
        private static readonly System.Reflection.FieldInfo saveBusy = AccessTools.Field(typeof(SaveLoadManager), "busy");
        internal static void SaveForGame(SaveLoadManager manager)
        {
            // the economy left as the last save wrote it (SaveFailed: the tries used up): the game saves with that
            if (keepLastGood) { keepLastGood = false; return; }
            try { Save(true); saveFailures = 0; }
            catch (Exception e)
            {
                // (preparation not done is a deferred save, not a damaged state: CanSave refuses it before, so only a save the
                // guard did not see reaches here; stopped all the same)
                if (manager) saveBusy?.SetValue(manager, false);
                if (Startup.BlocksGameplay) throw;
                if (SaveFailed(e, true, "while the game saved")) throw;   // this save stopped before its file; tried again soon
            }
        }
        // A save whose economy fails (the user, 2026-10-20, of saving turned off until a reload: "no this is very inconvient,
        // default save time is 15min and what you do here waste player time"; then "1+2"): (1) that save waits and is tried again
        // 5 s later, up to 3 times; (2) then the game saves with the economy's last good data (the payload the last save wrote; the
        // other mods' and the game's data as now) and the notice says so; the next save tries the economy again. Saving is never
        // turned off for it (SaveBlocked stays for a save whose economy could not be read at its load). A failure is a fault, not
        // play (StateCodec.Validate: numbers out of range, a port the mod does not manage, a missing list), so each is logged.
        // Returns whether this save is to wait.
        internal const int SaveRetries = 3;
        internal const float SaveRetryDelay = 5f;
        private static int saveFailures;
        private static float retryAt = -1;
        private static bool retryCompressed = true, keepLastGood;
        internal static int SaveFailures => saveFailures;
        internal static bool SaveRetryPending => retryAt >= 0;
        internal const string SaveRetryNotice = "EconomyOverhaul could not save the economy; trying again in a few seconds.";
        internal const string LastGoodNotice = "EconomyOverhaul could not save the economy; the game was saved with the economy of your last save. See BepInEx log.";
        private static bool SaveFailed(Exception e, bool compressed, string when)
        {
            saveFailures++;
            if (saveFailures <= SaveRetries)
            {
                Plugin.Log?.LogError("Economic save failed " + when + " (try " + saveFailures + " of " + (SaveRetries + 1) + "); this save waits, tried again in " + SaveRetryDelay + " s. " + e);
                retryAt = Time.realtimeSinceStartup + SaveRetryDelay; retryCompressed = compressed;
                Loans.Notify(SaveRetryNotice);
                return true;
            }
            Plugin.Log?.LogError("Economic save failed " + when + " (try " + saveFailures + " of " + (SaveRetries + 1) + "); the game saves with the economy of the last save. " + e);
            saveFailures = 0; retryAt = -1;
            Loans.Notify(LastGoodNotice);
            return false;
        }
        // Each frame (Plugin.Update): a waiting save tried again once the game can save.
        internal static void SaveRetryTick()
        {
            if (retryAt < 0 || Time.realtimeSinceStartup < retryAt) return;
            var m = SaveLoadManager.instance;
            if (!m || !GameState.playing || GameState.currentlyLoading || !SaveLoadManager.readyToSave || GameState.inBed || GameState.currentShipyard ||
                (saveBusy != null && (bool)saveBusy.GetValue(m))) { retryAt = Time.realtimeSinceStartup + 1f; return; }
            retryAt = -1;
            m.SaveGame(retryCompressed);
        }
        // What the player can do while saving is off: a save this version cannot read (one from an older version) never will,
        // so start a new game; otherwise the last save was kept as it was, so reload it.
        private const string ReloadAdvice = "Reload your save", NewGameAdvice = "Start a new game";
        private static string SaveOffAdvice = ReloadAdvice;
        internal static string SaveOffNotice => "EconomyOverhaul save error. " + SaveOffAdvice + "; see BepInEx log.";
        // Every save the game tries while saving is off (a save whose economy could not be read at its load) says so again; a
        // failed check waits and is tried again, then saves with the last good economy (SaveFailed).
        internal static bool CanSave(bool compressed = true)
        {
            if (SaveBlocked) { Loans.Notify(SaveOffNotice); return false; }
            if (Startup.BlocksGameplay) return false;
            if (!Ready) return true;
            try { Save(false); return true; }
            catch (Exception e)
            {
                if (SaveFailed(e, compressed, "at the check before the game's save")) return false;
                keepLastGood = true;   // the tries used up: the game saves now, its economy as the last save wrote it
                return true;
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
                    // A save this version cannot read (a later version's, a damaged one) is kept: saving stays off to protect it.
                    // Binary from state version 7, JSON for the released versions 4-6 (StateCodec.Decode).
                    State = StateCodec.Decode(json);
                }
                catch (Exception e)
                {
                    SaveBlocked = true; SaveOffAdvice = NewGameAdvice; Ready = false; Plugin.Log.LogError("Economic save could not be restored; saving is blocked to protect the original data. " + e);
                    Loans.Notify(SaveOffNotice); return;
                }
                // The game saved after this data without the mod (removed, played, put back): the data is from before, so the
                // save starts as a first install (MIDSAVE_DRAFT.md).
                if (Rules.StaleSave(State.savedHour, CalendarHour))
                {
                    Plugin.Log.LogWarning("EconomyOverhaul data from hour " + State.savedHour.ToString("0.00") + " in a save of hour " + CalendarHour.ToString("0.00") +
                        ": the game saved without the mod since; starting as a first installation.");
                    State = new SaveState(); firstInstall = true;
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
            // State version 5 (the warehouse rework): a version-4 save (0.8.5-0.9.5) has the game's warehouses, bought in this
            // mod's economy; each is migrated once (the game loaded them before this mod's data). A first install skips it: its
            // stock and warehouses are cleared by the preparation below (MIDSAVE_DRAFT.md).
            if (!firstInstall && State.version < 5)
            {
                foreach (var row in Supply.ManagedRows())
                {
                    var m = Market(row.Port); var office = m ? m.GetComponent<IslandMissionOffice>() : null;
                    // One port's failure is logged and the others go on: an exception here ends the game's LoadGame, and the
                    // save hangs on the loading screen (a player's 0.10.1 report). That port's warehouse keeps its old goods;
                    // the warehouse cycle resizes it in play (Missions.Resize).
                    try { if (office) Missions.Migrate(m, office, MarketData(row.Port)); }
                    catch (Exception e) { Plugin.Log.LogError("Warehouse migration failed at port " + row.Port + "; its old warehouse is kept. " + e); }
                }
                Plugin.Log.LogInfo("Mission warehouses migrated to state version 5 (port capacity + 100, 80% filled).");
            }
            // Every migration done (a version-4 or -5 save's reports joined at its read, StateCodec.Share; a version-6 save's table
            // read, StateCodec.Untable; a version-4 save's warehouses above): written as the current version (binary) from now on.
            State.version = SaveState.Current;
            Ready = true; Loading = false; Fx.Ensure(); Fx.Sync(); Fleet.BeginRegistrationGrace(); Economy.Configure(); TradeBook.Loaded(); Bottomry.Loaded(); Rentals.Loaded(); Tarps.Loaded(); Missions.Loaded();
            // Refresh each market's own current quotes after a curve upgrade. Foreign
            // reports and fleet-carried stock observations remain stale as before.
            foreach (var row in Supply.ManagedRows()) { var market = Market(row.Port); if (market) Economy.Refresh(market); }
            foreach (var c in new List<CargoCondition>(CargoCondition.Active)) c.Restore();
            Loans.PurgeRemoved();
            if (firstInstall)
            {
                // Installed into a vanilla save (the user, 2026-10-15): market stock and warehouses cleared, then the new game's
                // preparation (MIDSAVE_DRAFT.md).
                Startup.Begin(zeroStock: true);
                Plugin.Log.LogInfo("First installation: market stock and warehouses cleared; the new game's preparation queued.");
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
    [HarmonyPatch(typeof(SaveLoadManager), "SaveModData")] internal static class SavePatch { static void Prefix(SaveLoadManager __instance) => World.SaveForGame(__instance); }
    [HarmonyPatch(typeof(SaveLoadManager), "LoadModData")] internal static class LoadPatch { static void Postfix() => World.Load(); }
    [HarmonyPatch(typeof(SaveLoadManager), "SaveGame")] internal static class ProtectSavePatch { static bool Prefix(bool compressed) => Startup.RequestSave(compressed); }

    // ---- Settings ----
    internal static class Settings
    {
        internal static ConfigEntry<bool> WaterDamage, WaterDamageHint, CoverHint, TarpWear, CargoDamageHint, FragileCargo;
        internal static bool FragileEnabled => FragileCargo?.Value ?? true;
        internal static bool CoverHintEnabled => CoverHint?.Value ?? false;
        internal static bool CargoDamageHintEnabled => CargoDamageHint?.Value ?? true;
        internal static ConfigEntry<KeyboardShortcut> BraceKey;   // a shortcut: the configurator sets it by a key press (the user, 2026-10-13)
        internal static bool WaterEnabled => WaterDamage?.Value ?? true;
        internal static bool TarpWearing => TarpWear?.Value ?? true;
        private static bool tarpWearWas = true;   // the option's value before its last change
        internal static bool WaterHintEnabled => WaterDamageHint?.Value ?? true;
        // Visible traders (the user, 2026-10-18: on by default; off, the traders run exactly as today, unseen).
        internal static ConfigEntry<bool> VisibleTraders;
        internal static bool VisibleTradersEnabled => VisibleTraders?.Value ?? true;
        internal static void Bind(ConfigFile config)
        {
            WaterDamage = config.Bind("Cargo", "Enable water damage", true,
                "Enable cargo water damage, drying, wet appearance and condition-based value/reward reductions. When off, saved condition pauses and cargo has full condition value. Re-enabling resumes the saved condition.");
            WaterDamageHint = config.Bind("Cargo", "Show water damage percentage", true,
                "Show remaining water condition (100% to 0%) below vulnerable cargo hints. Hidden when water damage is disabled. This setting only changes the hint, not damage or cargo value.");
            // "Mission reputation reward" (Missions) is gone (2026-10-14): the mission category sets the factor. Its line stays
            // in older config files, unused.
            CoverHint = config.Bind("Cargo", "Show cover percentage", false,
                "Show how much of vulnerable cargo's top is under cover (a deck, a roof or a canvas tarp), 0% to 100%, below its water damage line. Hidden when water damage is disabled. This setting only changes the hint.");
            CoverHint.SettingChanged += (s,e) => { foreach(var c in CargoCondition.Active) c.RefreshHint(); };
            CargoDamageHint = config.Bind("Cargo", "Show cargo damage percentage", true,
                "Show the damage of fragile cargo (gems, sculptures, marble, a fragile mission's tools), 0% intact to 100% ruined, below its other hint lines. This setting only changes the hint.");
            CargoDamageHint.SettingChanged += (s,e) => { foreach(var c in CargoCondition.Active) c.RefreshHint(); };
            // The user, 2026-10-15: on by default; off stops fragile damage and the Fragile mission modifier.
            FragileCargo = config.Bind("Cargo", "Enable fragile cargo", true,
                "Fragile cargo (gems, sculptures, marble, a fragile mission's tools) is damaged by hull impacts, and fragile missions pay x2 when delivered intact. When off, no mission is fragile: missions already taken lose their fragile x2 for good; saved damage pauses and cargo has full value. Re-enabling resumes the saved damage.");
            FragileCargo.SettingChanged += (s,e) => { Missions.DropFragile(); foreach(var c in CargoCondition.Active) { c.RefreshHint(); c.RefreshAppearance(); } };
            BraceKey = config.Bind("Wood pole", "Brace key", new KeyboardShortcut(KeyCode.H),
                "While holding a wood pole, this key braces it (held at your side, pointing forward: with the pick-up key held (the left mouse by default) it pushes boats, docks or the sea bed) or holds it loosely again.");
            // "Braced pole hand force" and "Braced pole collision" were removed (the user, 2026-10-13: a braced pole has no collision);
            // a config file from 0.9.5 keeps their lines, unused.
            TarpWear = config.Bind("Canvas", "Tarp wearing", true,
                "Tied canvas tarps lose 10 health a game day and tear at 0. When off, every tarp's health, colour and holes stay as they are. Turning it back on continues wearing from there.");
            WaterDamage.SettingChanged += (s,e) => { foreach(var c in CargoCondition.Active) c.RefreshSettings(); };
            WaterDamageHint.SettingChanged += (s,e) => { foreach(var c in CargoCondition.Active) c.RefreshHint(); };
            // Each tarp's health so far, worn as the option was, becomes its start from now (user, 2026-10-09).
            TarpWear.SettingChanged += (s,e) => { tarpWearWas = Tarps.Rebase(tarpWearWas); };
            tarpWearWas = TarpWear.Value;
            VisibleTraders = config.Bind("Traders", "Visible traders", true,
                "Draw the trader fleet's ships on the sea within 3 km of you: sailing their lanes, lying at their ports' berths and anchorages, a lantern on each mast at night, their main cargo on deck. When off, the traders sail unseen as before; the trade is the same either way.");
            VisibleTraders.SettingChanged += (s,e) => EconomyOverhaul.VisibleTraders.SettingChanged();
            // A button, not a setting (MIDSAVE_DRAFT.md): its stored value is never used.
            config.Bind("Uninstall", "Prepare to uninstall", "", new ConfigDescription(
                "Before removing the mod from a running game: ends a ship rental and charges open loans, removes every item of the mod (wood planks and their bundles, canvas packs and tarps, wood poles and their bundles) and stops the mod. Then save, quit and remove the mod. Click twice to confirm.",
                null, new ConfigurationManagerAttributes { CustomDrawer = Uninstall.Draw, HideDefaultButton = true }));
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
        // The mod's data is stale when the game saved after it without the mod (MIDSAVE_DRAFT.md): the hour stored with the
        // data differs from the save's own. 0: a save from before the hour was stored, never stale.
        public static bool StaleSave(double savedHour, double gameHour) => savedHour > 0 && Math.Abs(savedHour - gameHour) > .01;
        // The mod's own items (wood plank bundle and plank, canvas pack, wood pole and pole bundle), removed by the uninstall.
        public static bool ModItem(int prefabIndex) => prefabIndex >= 640 && prefabIndex <= 644;
    }

    // ---- Save state ----
    // Both serializers (DataContractJsonSerializer for the released JSON saves, BinaryFormatter since version 7) refuse a save that
    // lacks a field unless the field is [OptionalField]: a field added after 0.8.5 (the first public release) must be, and reads 0
    // or null when missing (no constructor runs; StateCodec.Finish fills the lists an older save lacks). StateCodec.Validate then
    // refuses a save whose records are missing or out of range. The binary save names every class and field: never rename, retype
    // or move one (to another namespace); a change that must happen needs a mapping in StateCodec.TypeBinder and a version step.
    [Serializable] public sealed class SaveState
    {
        // 7 since the slot is binary (BINARY_SAVE_DRAFT.md, 2026-10-21), 6 since each price report is written once
        // (SAVE_SIZE_DRAFT.md, 2026-10-20), 5 since the warehouse rework (2026-10-14). A version-4, -5 or -6 save (JSON) is read and
        // migrated (StateCodec.Read joins or untables its reports, World.Load its warehouses); 1.0.0 and older refuse a version-7
        // save (the user: "only implement old saves into new mod version").
        internal const int Current = 7;
        public int version = Current;
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
        // The calendar hour the save was written at (MIDSAVE_DRAFT.md): optional, 0 in older saves (no stale check).
        [System.Runtime.Serialization.OptionalField] public double savedHour;
        // Dynamic demand events (2026-10-16): optional, none in older saves (5 made at the first midnight).
        [System.Runtime.Serialization.OptionalField] public DemandState demand;
        // Version 6: the distinct price reports, which the markets' and traders' report lists point into by number
        // (MarketState.reportIds and incomingIds, BoatState.reportIds). Only in the JSON copy (StateCodec.Write); null in memory and
        // in the binary save (the formatter keeps shared reports shared by itself).
        [System.Runtime.Serialization.OptionalField] public List<ReportState> reportTable;
        internal SaveState Copy() => (SaveState)MemberwiseClone();
    }

    internal static class StateCodec
    {
        // Unity 2019 JsonUtility omits nested plugin-defined record lists. Use the
        // game's .NET serializer and verify the complete graph on the actual player.
        private static DataContractJsonSerializer Serializer() => new DataContractJsonSerializer(typeof(SaveState),
            new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = int.MaxValue });

        // State version 7 (BINARY_SAVE_DRAFT.md; the user, 2026-10-21): the game's slot holds BinaryPrefix and the Base64 of
        // BinaryFormatter, the formatter the game writes its own save with. On the user's saves, in the game's Mono (the probe
        // savecodec): written in 15-18 ms against the JSON's 99-110, read in 17-24 ms against 364-1,201, the same economy back. It
        // keeps shared objects shared by itself, so the live state is written as it is (no report table). Write and Read stay JSON:
        // they read the released saves (versions 4-6), and the tests and the QA build and compare states as JSON text.
        internal const string BinaryPrefix = "EOB7:";
        private static System.Runtime.Serialization.Formatters.Binary.BinaryFormatter Formatter() =>
            new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter { Binder = TypeBinder.Instance };
        internal static string Encode(SaveState state)
        {
            if (state == null || state.version != SaveState.Current) throw new InvalidDataException("Economic state of version " + state?.version + " is not migrated to " + SaveState.Current);
            using (var stream = new MemoryStream()) { Formatter().Serialize(stream, state); return BinaryPrefix + Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length); }
        }
        // The one entry point for the game's slot: binary from version 7 on, anything else the JSON of a released save (Read). A
        // damaged slot (cut short, a bad character, a class the binder cannot find) throws, and World.Load keeps the save untouched.
        internal static SaveState Decode(string slot)
        {
            if (slot == null || !slot.StartsWith(BinaryPrefix, StringComparison.Ordinal)) return Read(slot);
            SaveState result;
            using (var stream = new MemoryStream(Convert.FromBase64String(slot.Substring(BinaryPrefix.Length))))
                result = Formatter().Deserialize(stream) as SaveState ?? throw new InvalidDataException("Economic save's binary data holds no economic state");
            if (result.version < 7) throw new InvalidDataException("Economic binary save of state version " + result.version);
            return Finish(result);
        }
        // For inspection only (the QA and scratchpad tools; the user's B2: no setting in the game): a slot as JSON text, a binary
        // slot read and written with Write, a JSON slot as it is.
        internal static string SlotJson(string slot) => slot != null && slot.StartsWith(BinaryPrefix, StringComparison.Ordinal) ? Write(Decode(slot)) : slot;
        // BinaryFormatter names each class with its assembly's full name, the version included, and the version changes with each
        // release (EconomyOverhaul.csproj): every mention of this mod's assembly, also inside a generic type's arguments (a
        // List<MarketState> names MarketState with its assembly), is read as the assembly running now, whatever its version. Other
        // assemblies (mscorlib) as named, the one loaded first.
        internal sealed class TypeBinder : System.Runtime.Serialization.SerializationBinder
        {
            internal static readonly TypeBinder Instance = new TypeBinder();
            private static readonly Assembly Own = typeof(SaveState).Assembly;
            private static readonly string OwnName = Own.GetName().Name;
            private readonly Dictionary<string, Type> found = new Dictionary<string, Type>();
            public override Type BindToType(string assemblyName, string typeName)
            {
                string name = typeName + ", " + assemblyName;
                lock (found)
                {
                    if (!found.TryGetValue(name, out var type)) found.Add(name, type = Type.GetType(name, Resolve, null, true));
                    return type;
                }
            }
            private static Assembly Resolve(AssemblyName name) => name.Name == OwnName ? Own :
                AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name.Name) ?? Assembly.Load(name);
        }

        internal static string Write(SaveState state)
        {
            // Only a migrated state (World.Load sets the version after its migrations): a version-4 state written as 6 would skip
            // its warehouse migration at the next load.
            if (state == null || state.version != SaveState.Current) throw new InvalidDataException("Economic state of version " + state?.version + " is not migrated to " + SaveState.Current);
            using (var stream = new MemoryStream()) { Serializer().WriteObject(stream, Tabled(state)); return Encoding.UTF8.GetString(stream.ToArray()); }
        }
        // Version 6 (SAVE_SIZE_DRAFT.md, option A): a report is made once and passed on by reference (a market's own and incoming
        // lists, the traders'), but the serializer writes each reference in full (2,805 reports for 510 distinct, 3.6 MB of the
        // 4.8, vt-ropes-6). The written copy holds each report once in reportTable, numbered in the order first met, and every
        // list as numbers into it; the live state is not changed (shallow copies of the state, the markets and the traders).
        private static SaveState Tabled(SaveState state)
        {
            var table = new List<ReportState>(); var numbers = new Dictionary<ReportState, int>();   // ReportState has no Equals: by reference
            int[] Numbers(List<ReportState> reports)
            {
                if (reports == null) return null;
                var ids = new int[reports.Count];
                for (int i = 0; i < ids.Length; i++)
                {
                    var r = reports[i] ?? throw new InvalidDataException("Economic state holds an empty price report");
                    if (!numbers.TryGetValue(r, out int id)) { id = table.Count; numbers.Add(r, id); table.Add(r); }
                    ids[i] = id;
                }
                return ids;
            }
            var copy = state.Copy(); copy.reportTable = table;
            copy.markets = state.markets?.Select(m =>
            {
                if (m == null) return null;
                var c = m.Copy(); c.reportIds = Numbers(m.reports); c.incomingIds = Numbers(m.incoming); c.reports = c.incoming = null; return c;
            }).ToList();
            copy.fleet = state.fleet?.Select(b =>
            {
                if (b == null) return null;
                var c = b.Copy(); c.reportIds = Numbers(b.reports); c.reports = null; return c;
            }).ToList();
            return copy;
        }
        // A version-6 save: every list's numbers turned back into the table's reports, shared again as before the save. A table
        // missing or holding a damaged report, or a number outside it, refuses the save; a list without numbers is left null
        // (Validate refuses a market's or trader's missing reports; a market's incoming may be null, as in play).
        private static void Untable(SaveState s)
        {
            var table = s.reportTable;
            if (table == null || !table.All(Report)) throw new InvalidDataException("Economic save's price report table is missing or damaged");
            List<ReportState> Reports(int[] ids)
            {
                if (ids == null) return null;
                var list = new List<ReportState>(ids.Length);
                foreach (int id in ids)
                {
                    if (id < 0 || id >= table.Count) throw new InvalidDataException("Economic save's price report " + id + " is outside its table of " + table.Count);
                    list.Add(table[id]);
                }
                return list;
            }
            if (s.markets != null) foreach (var m in s.markets) if (m != null) { m.reports = Reports(m.reportIds); m.incoming = Reports(m.incomingIds); m.reportIds = m.incomingIds = null; }
            if (s.fleet != null) foreach (var b in s.fleet) if (b != null) { b.reports = Reports(b.reportIds); b.reportIds = null; }
            s.reportTable = null;
        }
        // The migration of a version-4 or -5 save (0.8.5-0.10.8; the user: "remember about the migration when you build a"): it
        // wrote every reference to a report in full, and its read made each a separate object (2,805 for 510). A report never
        // changes once made (Economy.CaptureReport), so copies equal in every field are one report: each list's copies are
        // joined into one shared object, as in play before that save, and the next save (version 6) writes each once.
        private static void Share(SaveState s)
        {
            var shared = new Dictionary<ReportState, ReportState>(SameReport.Instance); int total = 0;
            void Join(List<ReportState> reports)
            {
                if (reports == null) return;
                for (int i = 0; i < reports.Count; i++, total++)
                    if (shared.TryGetValue(reports[i], out var one)) reports[i] = one; else shared.Add(reports[i], reports[i]);
            }
            foreach (var m in s.markets) { Join(m.reports); Join(m.incoming); }
            foreach (var b in s.fleet) Join(b.reports);
            Plugin.Log?.LogInfo("Economic save of state version " + s.version + ": its " + total + " price reports joined into " + shared.Count + " shared ones (written as version " + SaveState.Current + " from the next save).");
        }
        // Equal in every field (after Validate: no report or array is null).
        private sealed class SameReport : IEqualityComparer<ReportState>
        {
            internal static readonly SameReport Instance = new SameReport();
            public bool Equals(ReportState a, ReportState b) => ReferenceEquals(a, b) || a != null && b != null && a.port == b.port && a.day == b.day &&
                a.approved == b.approved && a.cap.Equals(b.cap) && a.surplus.Equals(b.surplus) && a.deficit.Equals(b.deficit) &&
                a.supply.SequenceEqual(b.supply) && a.values.SequenceEqual(b.values) && a.buy.SequenceEqual(b.buy) && a.sell.SequenceEqual(b.sell);
            public int GetHashCode(ReportState r) => unchecked(((r.port * 397) ^ r.day) * 397 ^ r.cap.GetHashCode());
        }
        internal static SaveState Read(string json)
        {
            SaveState result;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json))) result = (SaveState)Serializer().ReadObject(stream);
            // JSON of version 6 (1.0.0's slot) and 7 (Write's text) holds the report table.
            if (result?.version >= 6) Untable(result);
            return Finish(result);
        }
        // After the read, JSON or binary alike: the lists an older save lacks, the check, an older save's copies of the reports
        // joined, the fleet table.
        private static SaveState Finish(SaveState result)
        {
            if (result != null && result.tarps == null) result.tarps = new List<TarpState>();   // a 0.8.5 save
            // Demand data from a build before the tavern (stage 3) has no heard list: an empty one, not a refused save.
            if (result?.demand != null && result.demand.heard == null) result.demand.heard = new List<int>();
            Validate(result);
            if (result.version < 6) Share(result);
            // Every trader sails at its group's speed in the fleet table, also in a game saved before the table changed (the
            // user, 2026-10-18: the small and heavy buff reaches existing games), and waits its group's wait in port (the medium
            // locals' and the Happy Bay trader's since the sail plans, 2026-10-19); a trip or a wait under way keeps its time left.
            foreach (var b in result.fleet) { var d = FleetDefinitions.Current.FirstOrDefault(x => x.Matches(b)); if (d != null) { b.speed = d.Speed; b.wait = d.Wait; } }
            return result;
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
            bool valid = s != null && s.version >= 4 && s.version <= SaveState.Current && s.chronosReputation >= 0 && s.chronosReputation <= Reputations.Cap && s.markets != null && s.fleet != null && s.cargo != null && s.missions != null &&
                s.fractionalPenalty?.Length == 4 && s.removedCargo != null &&
                Finite(s.trafficTime) && s.fractionalPenalty.All(x => Finite(x) && x >= 0 && x < 1) && Finite(s.savedHour) && s.savedHour >= 0 &&
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
            valid &= s.cargo.All(c => c != null && c.id > 0 && c.good >= 1 && c.good <= 64 && c.good != 45 && Finite(c.health) && c.health >= 0 && c.health <= 100 && Finite(c.impact) && c.impact >= 0 && c.impact <= 100 &&
                Finite(c.fxPressure) && c.fxPressure >= 0 && (c.fxPressure == 0 || c.fxCurrency >= 0 && c.fxCurrency < 3) &&
                (c.stainHistory == null || c.stainHistory.Length == CargoStainHistory.Bytes) &&
                (!c.loan || c.region >= 0 && c.region < 4 && c.principal > 0 && c.interest >= 0 &&
                Finite(c.rate) && c.rate >= 0 && c.rate <= 1 && Finite(c.reservation) && c.reservation > 0)) && s.cargo.Select(c => c.id).Distinct().Count() == s.cargo.Count;
            valid &= s.fleet.Count == FleetDefinitions.Total && s.fleet.All(b => b != null && PortId(b.origin) && (b.destination == -1 || PortId(b.destination)) &&
                b.units > 0 && b.units <= (b.group == "chronos" ? 150 : 120) && b.weight > 0 && b.speed > 0 && Finite(b.speed) && Finite(b.wait) && Finite(b.remaining) && Finite(b.trip) &&
                b.network != null && b.network.Length > 0 && b.network.All(PortId) && b.goods != null && b.goods.Length <= b.units &&
                b.goods.All(g => g > 0 && g < 65 && g != 45 && g != 51) && b.reports != null && b.reports.All(Report) && b.lane >= 0 && b.lane < 16 &&
                FleetDefinitions.KnownGroup(b.group)) && s.fleet.Select(b => b.id).Distinct().Count() == s.fleet.Count;
            valid &= s.missions.All(m => m != null && PortId(m.origin) && PortId(m.destination) && m.creditedDeliveries >= 0 &&
                m.category >= (int)MissionCategory.None && m.category <= (int)MissionCategory.Luxury && m.modifiers >= 0 && m.modifiers <= 15);
            // 0.7.0 daily reports and trade book.
            bool Entry(BookEntry e) => e != null && PortId(e.port) && Finite(e.seen) && (FleetDefinitions.KnownGroup(e.group) || e.group == "small2" || e.group == "small3" || e.group == "heavy2");
            valid &= s.markets.All(m => (m.incoming == null || m.incoming.All(Report)) &&
                (m.book == null || m.book.All(Entry)) && (m.bookIncoming == null || m.bookIncoming.All(Entry)) &&
                (m.released == null || m.released.Count <= 128));
            valid &= s.bookSnapshots == null || s.bookSnapshots.All(x => x != null && PortId(x.port) && Finite(x.seen) && x.buy != null && x.buy.Length >= 65 && x.sell != null && x.sell.Length >= 65) &&
                s.bookSnapshots.Select(x => x.id).Distinct().Count() == s.bookSnapshots.Count && s.bookSnapshots.All(x => x.id > 0 && x.id <= s.bookNextId);
            valid &= s.fleet.All(b => b.book == null || b.book.Count <= 128) && (s.playerBook == null || s.playerBook.Count <= 128);
            valid &= s.tarps != null && s.tarps.All(Tarps.ValidRecord);
            valid &= s.demand == null || s.demand.events != null && s.demand.pickedGoods != null && s.demand.pickedPorts != null && s.demand.heard != null &&
                s.demand.events.All(e => e != null && PortId(e.demand) && PortId(e.producer) && Rules.DemandGood(e.good) && Finite(e.amount) && Finite(e.added));
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
                    if (office) Missions.Prefill(m, office, World.MarketData(row.Port));
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
            // Daily report updates start from the game's current day; the new game's 5 demand events.
            World.State.reportDay = GameState.day;
            Demand.Midnight(GameState.day);
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
            return World.CanSave(compressed);
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
        // The same, the cycle's state passed in: the markets' and the warehouses' clocks advance every frame, and their lambdas then
        // capture nothing, so no closure or delegate is made each time (the review of 2026-10-18).
        internal static int Advance<T>(ref double remaining, double elapsed, double interval, int budget, T state, Action<T> cycle, Func<T, bool> blocked = null)
        {
            remaining -= elapsed;
            int count = 0;
            while (remaining <= 0 && count < budget && !(blocked != null && blocked(state)))
            {
                remaining += interval;
                cycle(state); count++;
            }
            return count;
        }
    }

    // ---- Compatibility ----
    // ---- Uninstall (MIDSAVE_DRAFT.md, the user 2026-10-15) ----
    // The configurator's "Prepare to uninstall": the rental and loans settled, the mod's items and tarps removed, the mod
    // stopped. The next save then holds no data of the mod and no market stock, known prices or warehouses, which the game's
    // load skips: loaded without the mod, the game keeps the markets it preheated at the start menu and empty warehouses.
    internal static class Uninstall
    {
        internal static bool Prepared { get; private set; }
        private static float armedUntil;
        internal static void Reset() { Prepared = false; armedUntil = 0; }
        internal static string Refusal() =>
            !GameState.playing || GameState.currentlyLoading || !World.Ready && !World.SaveBlocked ? "Load a game first" :
            Startup.BlocksGameplay ? "Wait until the economy preparation is done" :
            ForcedMove.Busy ? "Wait until the ship move is done" : null;
        // The configurator's button: two clicks within 5 seconds.
        internal static void Draw(ConfigEntryBase entry)
        {
            if (Prepared) { GUILayout.Label("Done: save, quit, then remove the mod."); return; }
            var refusal = Refusal();
            if (refusal != null) { GUILayout.Label(refusal); return; }
            bool armed = Time.realtimeSinceStartup < armedUntil;
            if (!GUILayout.Button(armed ? "Click again to confirm" : "Prepare to uninstall", GUILayout.ExpandWidth(true))) return;
            if (!armed) { armedUntil = Time.realtimeSinceStartup + 5; return; }
            armedUntil = 0; Run();
        }
        internal static int Run()
        {
            if (Prepared || Refusal() != null) return -1;
            // Settled automatically (the user): the rental ends as a forced end does; each loan is charged in full.
            Rentals.EndForUninstall();
            foreach (var c in World.State.cargo.Where(c => c.loan).ToList()) Loans.Collect(c);
            foreach (var l in World.State.bottomry.ToList()) { World.State.bottomry.Remove(l); Bottomry.Charge(l, Bottomry.Owed(l)); }
            Tarps.Clear();
            int removed = RemoveItems();
            // Shop stalls stop offering them (each slot is the mod's own object).
            foreach (var s in Resources.FindObjectsOfTypeAll<ShopItemSpawner>())
            {
                var p = s && s.gameObject.scene.IsValid() && s.itemPrefab ? s.itemPrefab.GetComponent<SaveablePrefab>() : null;
                if (p && Rules.ModItem(p.prefabIndex)) UnityEngine.Object.Destroy(s.gameObject);
            }
            Missions.Reset();
            Prepared = true; World.Ready = false; World.SaveBlocked = false;
            Plugin.Log.LogInfo("Prepared to uninstall: " + removed + " items of the mod removed; the next save holds no data of the mod.");
            Loans.Notify("EconomyOverhaul removed from this game (" + removed + " items). Save, quit, then remove the mod.");
            return removed;
        }
        // Every item 640-644: the live ones (held, in the inventory, in crates, aboard, ashore, shop stock) and the save records
        // a far ship keeps instead of live items. The game's load would fail on any of them without the mod.
        internal static int RemoveItems()
        {
            var manager = SaveLoadManager.instance; int removed = 0;
            var prefabs = manager.GetCurrentPrefabs().Where(p => p).ToList();
            foreach (var p in prefabs)
            {
                if (!Rules.ModItem(p.prefabIndex)) continue;
                var item = p.GetComponent<ShipItem>(); if (!item) continue;
                if (p.currentCrateId != 0)
                    foreach (var crate in prefabs.Where(c => c.instanceId == p.currentCrateId).Select(c => c.GetComponent<CrateInventory>()).Where(c => c))
                        crate.containedItems.Remove(item);
                item.DestroyItem(); removed++;
            }
            foreach (var o in manager.GetCurrentObjects() ?? new SaveableObject[0])
            {
                var cached = o && o.localItems ? o.localItems.GetCachedItems() : null;
                if (cached != null) removed += cached.RemoveAll(d => d != null && Rules.ModItem(d.prefabIndex));
            }
            return removed;
        }
        // What the save writes for each port (UninstallSavePatch): null once prepared, which the game's load skips.
        internal static float[] SavedSupply(IslandMarket m) => Prepared ? null : m.currentSupply;
        internal static PriceReport[] SavedPrices(IslandMarket m) => Prepared ? null : m.knownPrices;
        internal static int[] SavedWarehouse(IslandMissionOffice o) => Prepared ? null : o.GetData();
    }
    // The configurator (BepInEx Configuration Manager) reads an entry's options from a tag of this name.
    internal sealed class ConfigurationManagerAttributes { public Action<ConfigEntryBase> CustomDrawer; public bool? HideDefaultButton; }
    // The game's save reads each port's market stock, known prices and warehouse through Uninstall (the coroutine builds and
    // writes the save in one frame, so nothing needs restoring).
    [HarmonyPatch] internal static class UninstallSavePatch
    {
        static MethodBase TargetMethod() => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(SaveLoadManager), "DoSaveGame"));
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var supply = AccessTools.Field(typeof(IslandMarket), "currentSupply"); var prices = AccessTools.Field(typeof(IslandMarket), "knownPrices");
            var warehouse = AccessTools.Method(typeof(IslandMissionOffice), "GetData");
            int s = 0, p = 0, w = 0;
            foreach (var c in instructions)
            {
                if (c.LoadsField(supply)) { c.opcode = OpCodes.Call; c.operand = AccessTools.Method(typeof(Uninstall), nameof(Uninstall.SavedSupply)); s++; }
                else if (c.LoadsField(prices)) { c.opcode = OpCodes.Call; c.operand = AccessTools.Method(typeof(Uninstall), nameof(Uninstall.SavedPrices)); p++; }
                else if (c.Calls(warehouse)) { c.opcode = OpCodes.Call; c.operand = AccessTools.Method(typeof(Uninstall), nameof(Uninstall.SavedWarehouse)); w++; }
                yield return c;
            }
            if (s != 1 || p != 1 || w != 1) throw new InvalidOperationException("The game's save of markets changed (" + s + "/" + p + "/" + w + ")");
        }
    }

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

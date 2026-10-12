using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Crest;
using HarmonyLib;
using UnityEngine;

namespace EconomyOverhaul
{
    // ---- Rules ----
    public static partial class Rules
    {
        // Hull repair: whole planks per 1% of hull, so planks cut from lumber at its internal value (1/128 of it) cost
        // what the shipyard charges for 1% (fee = boat price x 3.5; repair = fee x damage x capacity x 0.1; the
        // currency rate cancels). Boats without a list price fall back to capacity / 120. At least one plank.
        public static int PlanksPerPercent(int boatPrice, double capacity, double lumberValue)
        {
            double planks = boatPrice > 0 && lumberValue > 0 ? boatPrice * 3.5 * .01 * capacity * .1 / (lumberValue / 128) : capacity / 120;
            return double.IsNaN(planks) || planks < 1.5 ? 1 : (int)Math.Min(1000000, Math.Round(planks, MidpointRounding.AwayFromZero));
        }
        // A caulked hull halves plank hammering: its oakum covers 1% of the hull's capacity, or all of the remaining damage
        // when less than 1% is left (oakum never exceeds damage x capacity). Vanilla oakum cannot fill the last 0.01, so
        // that shortfall is tolerated.
        public static bool Caulked(double oakum, double damage, double capacity) =>
            capacity > 0 && damage > 0 && oakum + .01 >= Math.Min(.01, damage) * capacity;
        // A hull plank's hint: its own hammering progress in whole percent (rounded down, so 99% until the plank is
        // done), the hull damage to two decimals and the planks that restore 1%.
        public static string HullHint(double progress, double damage, int planks)
        {
            int percent = progress >= 1 ? 100 : progress > 0 ? Math.Min(99, (int)Math.Floor(progress * 100 + 1e-4)) : 0;
            return percent + "%\nShip hull damage: " + (damage * 100).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%\nPlanks required to fix 1%: " + planks;
        }
        // Which of the 24 plank looks an unbundled plank gets, at column i (item x), layer k (item z) and length j
        // (item y): a bundle's plank is the half of the board it replaces (0-7), a lumber plank the piece of the top
        // layer above it (8-23), the same for every layer.
        public static int PlankLook(bool lumber, int i, int k, int j) => lumber ? 8 + 4 * i + j : 4 * i + 2 * k + j;

        // The wood pole (user, 2026-10-12): 0.5-5 m in 0.1 m steps, 2 kg a metre, price 60; 2 m in the shops.
        public const float PoleMin = .5f, PoleMax = 5, PoleStep = .1f, PoleShopLength = 2, PoleKgPerMetre = 2;
        public const int PoleValue = 60;
        // The wood pole bundle the shops sell (user, 2026-10-12): four 2 m poles, four poles' price and weight.
        public const int PoleBundleCount = 4, PoleBundleValue = PoleValue * PoleBundleCount;
        public static float PoleBundleMass => PoleMass(PoleShopLength) * PoleBundleCount;
        public static int PoleTenths(double metres) => double.IsNaN(metres) || double.IsInfinity(metres) ? (int)Math.Round(PoleShopLength * 10) : (int)Math.Round(Clamp(metres, PoleMin, PoleMax) * 10);
        public static float PoleLength(double metres) => PoleTenths(metres) / 10f;
        public static float PoleMass(double metres) => PoleLength(metres) * PoleKgPerMetre;

        // Canvas tarps: health 0-100, worn 10 a game day while tied. Repair with a held pack (user, 2026-10-09): the tarp's
        // health plus the pack's, at most 100 (the rest is lost; the pack is always used up).
        public const float TarpFullHealth = 100, TarpWearPerDay = 10;
        public static float RepairedHealth(float tarp, float pack) =>
            Math.Min(TarpFullHealth, Math.Max(0, Finite(tarp)) + Math.Max(0, Finite(pack)));
        // A tarp's health from its health and the game's clock when it was tied, loaded or re-based. With the option "Tarp
        // wearing" off (user, 2026-10-09) it stays where it is.
        public static float TarpHealth(float startHealth, double startClock, double now, bool wearing) =>
            (float)Clamp(wearing ? Finite(startHealth) - TarpWearPerDay * Math.Max(0, now - startClock) : Finite(startHealth), 0, TarpFullHealth);
        private static float Finite(float x) => float.IsNaN(x) || float.IsInfinity(x) ? 0 : x;
    }

    // ---- Planks ----
    // Wood plank bundle (prefab 640) and wood plank (641): registration, the Kicia Bay furniture stall's stock,
    // unbundling bundles and lumber into stacked planks, and hull repair by hammering planks attached to the hull.
    internal static class WoodPlanks
    {
        internal const int BundleIndex = 640, PlankIndex = 641, LumberIndex = 217, PaintingIndex = 120, LumberGood = 47, KiciaScene = 27;
        internal const float PlankSeconds = 20, SwingSpeed = 275;   // vanilla nailing swings at 550 degrees per second
        internal const float BundleLumber = 1f / 16;   // a bundle holds 1/16 of a lumber's wood
        internal const float SurfaceTolerance = .01f;   // a hull plank's back face lies on the boat within 1 cm
        // Plank looks: 0-7 are the halves of the bundle's 4 boards, 8-23 the pieces of the lumber's top layer
        // (4 columns x 4 lengths). A plank keeps its look in its saved health, which vanilla leaves unused on plain items.
        internal const int BundleLooks = 8, Looks = 24;
        internal static GameObject BundleTemplate, PlankTemplate;
        internal static Mesh[] PlankMeshes;
        internal static bool Registered => BundleTemplate && PlankTemplate;
        private static Mesh bundleMesh;

        internal static int Index(ShipItem item) { var save = item ? item.GetComponent<SaveablePrefab>() : null; return save ? save.prefabIndex : -1; }
        internal static bool IsBundle(ShipItem item) => Registered && Index(item) == BundleIndex;
        internal static bool IsPlank(ShipItem item) => Registered && Index(item) == PlankIndex;
        internal static int LumberValue => PrefabsDirectory.instance && PrefabsDirectory.instance.directory.Length > LumberIndex && PrefabsDirectory.instance.directory[LumberIndex]
            ? PrefabsDirectory.instance.directory[LumberIndex].GetComponent<ShipItem>().value : 6000;

        // Templates live under an inactive holder: instances made from them start active, the templates never run.
        internal static void Register(PrefabsDirectory directory)
        {
            if (Registered || !directory || directory.directory == null) return;
            var dir = directory.directory;
            if (dir.Length > PlankIndex && (dir[BundleIndex] || dir[PlankIndex]))
            { Plugin.Log?.LogError("Prefab slots 640/641 are used by another mod; wood plank bundles and planks are disabled."); return; }
            if (dir.Length <= LumberIndex || !dir[LumberIndex] || !dir[PaintingIndex])
            { Plugin.Log?.LogError("Lumber or painting prefab missing; wood plank bundles and planks are disabled."); return; }
            try
            {
                bundleMesh = ObjMesh.Load("EconomyOverhaul.Data.wood_planks_bundle.obj", "wood plank bundle");
                var looks = ObjMesh.LoadAll("EconomyOverhaul.Data.wood_plank_variants.obj", "wood plank");
                if (looks.Count != Looks) throw new InvalidDataException("Expected " + Looks + " wood plank looks, found " + looks.Count);
                // Wall items attach with their pivot on the wall, facing into it (+z): the back face on the pivot keeps the plank flush.
                foreach (var look in looks) ObjMesh.Shift(look, new Vector3(0, 0, -look.bounds.max.z));
                var holder = new GameObject("EconomyOverhaul item templates"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder);
                var wood = dir[LumberIndex].GetComponent<MeshRenderer>().sharedMaterial;
                var bundle = Template(dir[LumberIndex], holder.transform, "640 wood plank bundle", bundleMesh, wood, BundleIndex, "wood plank bundle", 375, 75, true);
                UnityEngine.Object.DestroyImmediate(bundle.GetComponent<Good>());
                var plank = Template(dir[PaintingIndex], holder.transform, "641 wood plank", looks[0], wood, PlankIndex, "wood plank", 46, 9.375f, false);
                var plankItem = plank.GetComponent<ShipItem>(); plankItem.health = 0; plankItem.amount = 0;   // look 0, no hammering yet
                if (dir.Length <= PlankIndex) { var resized = new GameObject[PlankIndex + 1]; Array.Copy(dir, resized, dir.Length); directory.directory = dir = resized; }
                dir[BundleIndex] = bundle; dir[PlankIndex] = plank; BundleTemplate = bundle; PlankTemplate = plank; PlankMeshes = looks.ToArray();
                Plugin.Log?.LogInfo("Registered wood plank bundle (640) and wood plank (641, " + Looks + " looks).");
            }
            catch (Exception e) { BundleTemplate = PlankTemplate = null; PlankMeshes = null; Plugin.Log?.LogError("Wood plank registration failed; bundles and planks are disabled. " + e); }
        }
        internal static GameObject Template(GameObject source, Transform holder, string objectName, Mesh mesh, Material material, int index, string itemName, int value, float mass, bool big)
        {
            var o = UnityEngine.Object.Instantiate(source, holder, false); o.name = objectName;
            o.transform.localPosition = Vector3.zero; o.transform.localRotation = Quaternion.identity; o.transform.localScale = Vector3.one;
            // Exactly one material: the painting the plank is built from has a second one (its frame's flat colour), which
            // Unity would draw over the whole plank again, fighting the wood for every pixel.
            o.GetComponent<MeshFilter>().sharedMesh = mesh; o.GetComponent<MeshRenderer>().sharedMaterials = new[] { material };
            var box = o.GetComponent<BoxCollider>(); box.size = mesh.bounds.size; box.center = mesh.bounds.center;
            var item = o.GetComponent<ShipItem>();
            item.name = itemName; item.value = value; item.mass = mass; item.big = big; item.category = TransactionCategory.toolsAndSupplies;
            item.sold = false; item.lookText = ""; item.description = "";
            o.GetComponent<SaveablePrefab>().prefabIndex = index;
            return o;
        }
        internal static int Look(ShipItem plank) => plank && plank.health >= 0 && plank.health <= Looks - 1 ? Mathf.RoundToInt(plank.health) : 0;
        internal static void SetLook(ShipItem plank, int look) { plank.health = look >= 0 && look < Looks ? look : 0; ApplyLook(plank); }
        // After spawning or loading: the mesh of the plank's saved look.
        internal static void ApplyLook(ShipItem plank)
        {
            var filter = plank && PlankMeshes != null ? plank.GetComponent<MeshFilter>() : null;
            if (filter) filter.sharedMesh = PlankMeshes[Look(plank)];
        }

        // Kicia Bay furniture stall: 8 bundles stacked 2 wide x 4 high on the stall floor between the bed and the stove,
        // in the stall area's own (scaled) frame: x across, y up, z along.
        internal static readonly float[] StockAcross = { -.16063f, -.11537f }, StockHeight = { -.30504f, -.27711f, -.24918f, -.22125f };
        internal const float StockAlong = .0153f;
        internal const string StallName = "shop area (15)";
        internal static void AddStock(ShopArea area)
        {
            if (!Registered || !area || area.name != StallName || area.gameObject.scene.buildIndex != KiciaScene) return;
            var stall = area.transform; var along = Vector3.ProjectOnPlane(stall.forward, Vector3.up).normalized;
            var rotation = Quaternion.LookRotation(Vector3.up, along);   // bundle length (mesh y) along the stall, thickness (mesh z) up
            foreach (float x in StockAcross)
                foreach (float y in StockHeight)
                {
                    var slot = new GameObject("EconomyOverhaul wood plank bundle stock");
                    slot.transform.SetParent(stall.parent, false);
                    slot.transform.SetPositionAndRotation(stall.TransformPoint(new Vector3(x, y, StockAlong)) - rotation * bundleMesh.bounds.center, rotation);
                    var spawner = slot.AddComponent<ShopItemSpawner>(); spawner.itemPrefab = BundleTemplate; spawner.availableAtNight = false; spawner.priceMult = 1;
                }
        }
        // 1/10 of what this market's trade book asks for one lumber right now (native currency, rounded up).
        internal static MoneyQuote BundleQuote(IslandMarket m)
        {
            float basePrice = m.GetGoodPriceAtSupply(LumberGood, m.currentSupply[LumberGood]);
            int raw = Mathf.RoundToInt(basePrice * (1 + (float)Rules.Spread(basePrice)));
            var lumber = Fx.Quote(raw, m, Fx.Currency(m), true);
            return new MoneyQuote { Gross = (int)Math.Ceiling(lumber.Gross / 10.0), Total = (int)Math.Ceiling(lumber.Total / 10.0), Fee = lumber.Fee };
        }
        internal static void BundleSold(IslandMarket m) { if (!m) return; m.currentSupply[LumberGood] -= BundleLumber; Economy.Refresh(m); }

        // Unbundling: the crate-seal question, then the item is replaced by planks exactly where its boards were.
        internal static ShipItem Pending;
        private static Vector3 popupOffset; private static int popupFrame;
        private static TextMesh title, body, button; private static string titleText, bodyText, buttonText;
        internal static bool Unbundleable(ShipItem item) => item && item.sold && !item.held && (Registered && (Index(item) == BundleIndex || Index(item) == LumberIndex) || WoodPoles.IsBundle(item));
        // null: allowed; "": silently not allowed; otherwise the notification.
        internal static string BlockReason(ShipItem item)
        {
            if (!Unbundleable(item) || item.GetCurrentInventorySlot() != -1) return "";
            var good = item.GetComponent<Good>();
            if (good && good.GetMissionIndex() != -1) return "Mission cargo cannot be unbundled";
            var record = CargoRecords.Of(item);
            if (record != null && record.loan) return "Repay or sell the financed lumber first";
            if (record != null && Rules.Ruined(record.health)) return "Ruined lumber cannot be unbundled";
            return null;
        }
        // Water-damaged lumber gives fewer planks: the full count times its sale factor (77 of 128 at 50% water damage).
        internal static int PlankCount(ShipItem item) => Rules.Round((Index(item) == LumberIndex ? 128 : 8) * CargoCondition.Factor(item));
        internal static void Ask(ShipItem item)
        {
            string reason = BlockReason(item); if (reason != null) { if (reason.Length > 0) Loans.Notify(reason); return; }
            var ui = CrateSealUI.instance; var camera = Camera.main;
            if (!ui || !camera) { Unbundle(item); return; }
            if (ui.showingUI) ui.HideUI(false);
            var t = ui.transform; var panel = t.GetChild(0);
            panel.gameObject.SetActive(true); ui.showingUI = true; ui.currentCrate = null;
            t.position = camera.transform.position + camera.transform.forward; t.LookAt(camera.transform);
            for (int guard = 0; guard < 20 && Vector3.Distance(t.position, item.transform.position) < .7f; guard++) t.Translate(Vector3.forward * .2f, Space.Self);
            popupOffset = t.position - item.transform.position; popupFrame = Time.frameCount;
            if (!title) { title = Text(panel, "text (1)"); body = Text(panel, "text (2)"); button = Text(panel, "break seal button/text"); }
            if (title) { titleText = title.text; title.text = "BUNDLED\n"; }
            if (body) { bodyText = body.text; body.text = WoodPoles.IsBundle(item) ? "Unbundling removes the ropes.\n\nYou get " + Rules.PoleBundleCount + " wood poles." : "Unbundling removes the ropes.\n\nYou get " + PlankCount(item) + " wood planks.\nPlanks cannot be sold\nin the trade book."; }
            if (button) { buttonText = button.text; button.text = "Unbundle\n(remove ropes)"; }
            foreach (var mesh in ui.GetComponentsInChildren<TextMesh>(true)) if (mesh.name == "Respondentia repayment prompt") mesh.gameObject.SetActive(false);
            Pending = item;
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUISound(UISounds.crateOpen, 1f, 1f);
        }
        private static TextMesh Text(Transform panel, string path) { var t = panel.Find(path); return t ? t.GetComponent<TextMesh>() : null; }
        // Replaces CrateSealUI.Update while an unbundle question shows; false skips the native update.
        internal static bool PopupUpdate(CrateSealUI ui)
        {
            if (ReferenceEquals(Pending, null)) return true;
            var camera = Camera.main;
            if (!Pending || !camera || Pending.held) { ui.HideUI(false); return false; }
            ui.transform.position = Pending.transform.position + popupOffset; ui.transform.LookAt(camera.transform);
            if (Time.frameCount != popupFrame && ActivateDown() || Vector3.Distance(ui.transform.position, camera.transform.position) > 2.25f) ui.HideUI(true);
            return false;
        }
        internal static bool PopupConfirm(CrateSealUI ui)
        {
            if (ReferenceEquals(Pending, null)) return true;
            var item = Pending; ui.HideUI(false);
            if (item) Unbundle(item);
            return false;
        }
        internal static void PopupClosed()
        {
            if (ReferenceEquals(Pending, null)) return;
            if (title) title.text = titleText; if (body) body.text = bodyText; if (button) button.text = buttonText;
            Pending = null;
        }
        private static Func<bool> activateDown;
        private static bool ActivateDown()
        {
            if (activateDown == null)
            {
                var input = AccessTools.TypeByName("GameInput"); var names = AccessTools.TypeByName("InputName");
                var method = input != null && names != null ? AccessTools.Method(input, "GetKeyDown", new[] { names }) : null;
                object activate = method != null ? Enum.Parse(names, "Activate") : null;
                activateDown = method != null ? () => (bool)method.Invoke(null, new[] { activate }) : (Func<bool>)(() => false);
            }
            return activateDown();
        }
        // Planks in the item's own frame: boards 0.152 wide (item x), 0.045 thick (item z) and a whole number of 0.872 m
        // planks long (item y); 3 mm gaps. Bundle 2 x 2 x 2 = 8, lumber 4 x 8 x 4 = 128. Without the ropes the stack
        // settles onto whatever the item lies on: it drops along the item's axis nearest to straight down, by the gap
        // between the stack and the item's collider on that side. Each plank's back (its pivot) faces the item's -z, or
        // its +z when that side is down, so the bottom layer of an item lying on a broad face lies flat on the surface.
        // Looks: a bundle's plank is the half of the board it replaces; a lumber plank is the piece of the top layer above it.
        internal static List<KeyValuePair<Pose, int>> Layout(ShipItem item)
        {
            bool lumber = Index(item) == LumberIndex;
            int wide = lumber ? 4 : 2, high = lumber ? 8 : 2, along = lumber ? 4 : 2;
            var step = new Vector3(.155f, .8745f, .048f);
            var filter = item.GetComponent<MeshFilter>(); var centre = filter && filter.sharedMesh ? filter.sharedMesh.bounds.center : Vector3.zero;
            var plankMesh = PlankTemplate ? PlankTemplate.GetComponent<MeshFilter>().sharedMesh : null;
            var plankCentre = plankMesh ? plankMesh.bounds.center : Vector3.zero;
            var plankSize = plankMesh ? plankMesh.bounds.size : new Vector3(.872f, .152f, .045f);
            var half = new Vector3(((wide - 1) * step.x + plankSize.y) / 2, ((along - 1) * step.y + plankSize.x) / 2, ((high - 1) * step.z + plankSize.z) / 2);
            var down = item.transform.InverseTransformDirection(Vector3.down);
            int axis = Mathf.Abs(down.x) >= Mathf.Abs(down.y) && Mathf.Abs(down.x) >= Mathf.Abs(down.z) ? 0 : Mathf.Abs(down.y) >= Mathf.Abs(down.z) ? 1 : 2;
            float side = down[axis] < 0 ? -1 : 1;
            var box = item.GetComponent<BoxCollider>();
            if (box)
            {
                float drop = side * (box.center[axis] + side * box.size[axis] / 2 - (centre[axis] + side * half[axis]));
                if (drop > 0) centre[axis] += side * drop;
            }
            // Plank length (x) along the item's y, width (y) along its x, back (+z) toward the side the item lies on.
            var rotation = item.transform.rotation * (axis == 2 && side > 0 ? Quaternion.LookRotation(Vector3.forward, Vector3.left) : Quaternion.LookRotation(Vector3.back, Vector3.right));
            var layout = new List<KeyValuePair<Pose, int>>();
            for (int i = 0; i < wide; i++)
                for (int k = 0; k < high; k++)
                    for (int j = 0; j < along; j++)
                        layout.Add(new KeyValuePair<Pose, int>(new Pose(item.transform.TransformPoint(centre + new Vector3((i - (wide - 1) / 2f) * step.x, (j - (along - 1) / 2f) * step.y, (k - (high - 1) / 2f) * step.z))
                            - rotation * plankCentre, rotation), Rules.PlankLook(lumber, i, k, j)));
            return layout;
        }
        internal static void Unbundle(ShipItem item)
        {
            string reason = BlockReason(item); if (reason != null) { if (reason.Length > 0) Loans.Notify(reason); return; }
            // Fewer planks from wet lumber: the lowest places of the stack, so none is left hanging. A pole bundle: its four poles.
            bool poles = WoodPoles.IsBundle(item);
            var layout = poles ? WoodPoles.BundleLayout(item).Select(x => new KeyValuePair<Pose, int>(x, 0)).ToList() : Layout(item).OrderBy(p => p.Key.position.y).Take(PlankCount(item)).ToList();
            var boat = item.currentActualBoat; Collider embark = null;
            if (boat) try { embark = Fields.Get<Collider>(item, "currentBoatCollider"); } catch (MissingFieldException) { }
            var holder = item.transform.parent ? item.transform.parent : FloatingOriginManager.instance ? FloatingOriginManager.instance.transform : null;
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUISound(UISounds.crateSealBreak, 1f, 1f);
            Retire(item);
            var planks = new List<ShipItem>(layout.Count); var onBoat = new List<Pose>(layout.Count);
            foreach (var entry in layout)
            {
                var o = UnityEngine.Object.Instantiate(poles ? WoodPoles.Template : PlankTemplate, entry.Key.position, entry.Key.rotation, holder);
                var plank = o.GetComponent<ShipItem>(); plank.sold = true; plank.amount = 0; if (poles) WoodPoles.SetLength(plank, Rules.PoleShopLength); else SetLook(plank, entry.Value);
                o.GetComponent<SaveablePrefab>().RegisterToSave(); planks.Add(plank);
                if (boat) onBoat.Add(new Pose(boat.InverseTransformPoint(entry.Key.position), Quaternion.Inverse(boat.rotation) * entry.Key.rotation));
            }
            if (boat && embark && Plugin.Instance) Plugin.Instance.StartCoroutine(Board(item, planks, onBoat, boat, embark));
            else item.DestroyItem();
        }
        // The bundle leaves at once in every way that shows or saves, but its physics body is switched off rather than
        // removed: removing it would put the body back at the visible bundle, inside the boat's hull, where the physics
        // engine throws the two apart. Switched off, the body still weighs on the boat until the planks have boarded.
        private static void Retire(ShipItem item)
        {
            var save = item.GetComponent<SaveablePrefab>();
            if (save && World.Ready && !World.Loading && !GameState.currentlyLoading && !World.SaveBlocked && save.GetParentObject() != -2 && save.GetParentObject() != -3)
                CargoRecords.Destroyed(save.instanceId);
            if (save) save.Unregister();
            if (item.itemRigidbodyC) item.itemRigidbodyC.gameObject.SetActive(false);
            foreach (var r in item.GetComponentsInChildren<Renderer>()) r.enabled = false;
            foreach (var c in item.GetComponentsInChildren<Collider>()) c.enabled = false;
        }
        internal static int Spawning;   // unbundlings aboard whose planks have not boarded yet
        private static readonly MethodInfo enterBoat = AccessTools.Method(typeof(ShipItem), "EnterBoat");
        // Planks unbundled aboard join the boat through the bundle's embark collider once their physics bodies exist (one
        // physics step), at their place on the boat however far it sailed meanwhile (they are frozen like wall items until
        // then). The bundle leaves the boat in that same step, so the boat's load and balance never change.
        private static IEnumerator Board(ShipItem old, List<ShipItem> planks, List<Pose> onBoat, Transform boat, Collider embark)
        {
            Spawning++;
            try
            {
                yield return new WaitForFixedUpdate();
                for (int n = 0; n < planks.Count; n++)
                {
                    var plank = planks[n];
                    if (!plank || !boat || !embark || plank.held || !plank.itemRigidbodyC) continue;
                    if (!plank.currentActualBoat)
                    {
                        try { enterBoat.Invoke(plank, new object[] { embark }); }
                        catch (Exception e) { Plugin.Log?.LogWarning("A wood plank could not board its boat: " + (e.InnerException ?? e).Message); continue; }
                    }
                    if (plank.currentActualBoat != boat) continue;
                    plank.transform.SetPositionAndRotation(boat.TransformPoint(onBoat[n].position), boat.rotation * onBoat[n].rotation);
                    plank.itemRigidbodyC.ForceRigidbodyToWalkCol();
                }
            }
            finally { Spawning--; if (old) old.DestroyItem(); }
        }

        // Hull repair. A plank counts when it is attached with its back face on a solid part of a boat that has hull
        // wear: a side, the deck, a roof or a ceiling. A boat's solid surfaces live on its walk collider (a copy away
        // from the visible boat), where an item aboard keeps its physics body; vanilla attaches that body with its pivot
        // on the surface, facing into it. The check looks along the body's forward, from just in front of the pivot, for
        // the boat's surface within 1 cm of the pivot, so a plank lying on another plank or on cargo never counts.
        internal static BoatDamage Hull(ShipItem plank)
        {
            if (!IsPlank(plank) || !plank.sold || plank.held || !plank.currentActualBoat || !plank.currentWalkCol) return null;
            var body = plank.itemRigidbodyC; var walk = plank.currentWalkCol;
            if (!body || !body.attached || !body.transform.IsChildOf(walk)) return null;
            var damage = plank.currentActualBoat.GetComponentInParent<BoatDamage>();
            if (!damage || damage.durabilityDays <= 0) return null;
            var t = body.transform;
            foreach (var hit in Physics.RaycastAll(t.position - t.forward * .1f, t.forward, .25f, ~0, QueryTriggerInteraction.Ignore))
                if (Mathf.Abs(hit.distance - .1f) <= SurfaceTolerance && hit.collider.transform.IsChildOf(walk)
                    && !hit.collider.GetComponentInParent<ItemRigidbody>() && !hit.collider.GetComponentInParent<ShipItem>()) return damage;
            return null;
        }
        internal static int PlanksPerPercent(BoatDamage damage)
        {
            var boat = damage.GetComponent<PurchasableBoat>();
            return Rules.PlanksPerPercent(boat ? boat.price : 0, damage.waterUnitsCapacity, LumberValue);
        }
        internal static string HullHint(ShipItem plank)
        {
            var hull = plank ? Hull(plank) : null; if (!hull) return null;
            return Rules.HullHint(Progress(plank), hull.hullDamage, PlanksPerPercent(hull));
        }
        private sealed class Job { internal ShipItemHammer hammer; internal ShipItem plank; internal float startOffset; internal bool back; internal int frame; }
        private static Job job;
        // Each plank's own hammering progress (0-1), kept in its saved amount, which vanilla leaves unused on plain items;
        // picking the plank up resets it.
        internal static float Progress(ShipItem plank) => plank && plank.amount > 0 ? Mathf.Min(1, plank.amount) : 0;
        internal static bool Hammering => job != null;
        // OnAltActivate: false skips native nailing for hull planks.
        internal static bool HammerStart(ShipItemHammer hammer)
        {
            if (!hammer.sold || !hammer.held) return true;
            var plank = hammer.held.GetPointedAtItem(); var hull = Hull(plank);
            if (!hull) return true;
            if (hull.hullDamage <= 0) { Loans.Notify("The hull needs no repair"); return false; }
            if (job == null || job.plank != plank) { EndJob(); job = new Job { hammer = hammer, plank = plank, startOffset = hammer.heldRotationOffset }; }
            job.frame = Time.frameCount;
            return false;
        }
        // OnAltHeld: 20 s per plank (10 s when caulked); each finished plank restores 1/N of 1% hull.
        internal static bool HammerHeld(ShipItemHammer hammer)
        {
            if (job == null || job.hammer != hammer) return true;
            var hull = Hull(job.plank);
            if (!hull || !hammer.held || hammer.held.GetPointedAtItem() != job.plank || hull.hullDamage <= 0) { EndJob(); return true; }
            job.frame = Time.frameCount;
            float capacity = Mathf.Max(0, hull.waterUnitsCapacity);
            bool caulked = Rules.Caulked(hull.oakum, hull.hullDamage, capacity);
            float done = Progress(job.plank) + Time.deltaTime / (caulked ? PlankSeconds / 2 : PlankSeconds);
            // The native nailing swing at half speed: animation only.
            if (hammer.heldRotationOffset < -95f) { job.back = true; if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUISound(UISounds.winchClick, 1f, .7f); }
            if (hammer.heldRotationOffset > -35f) job.back = false;
            hammer.heldRotationOffset += Time.deltaTime * SwingSpeed * (job.back ? 1 : -1);
            if (done < 1) { job.plank.amount = done; return false; }
            Finish(job.plank, hull, caulked);
            return false;
        }
        internal static void Finish(ShipItem plank, BoatDamage hull, bool caulked)
        {
            float capacity = Mathf.Max(0, hull.waterUnitsCapacity); int planks = PlanksPerPercent(hull);
            hull.hullDamage = Mathf.Max(0, hull.hullDamage - .01f / planks);
            if (caulked) hull.oakum = Mathf.Max(0, hull.oakum - .01f * capacity / planks);
            hull.oakum = Mathf.Min(hull.oakum, hull.hullDamage * capacity);
            if (job != null && job.plank == plank) EndJob();
            // Its physics body switched off first, as for an unbundled bundle (Retire): removed as it is, the game's ExitBoat
            // would put the body back at the visible plank, against the hull, still kinematic for one physics step, and the
            // boat would be thrown off it once (user, 2026-10-11). Cargo under it is rescanned with the bounds it has now
            // (Retire hides it, and DestroyItem's own rescan would then see a point).
            CoverEvents.RecheckBeneath(plank, CoverEvents.WorldBounds(plank));
            Retire(plank);
            plank.DestroyItem();
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUISound(UISounds.winchUnclick, 1f, .6f);
        }
        // Every frame: the key was released, the hammer put away or the plank gone.
        internal static void Tick()
        {
            if (job != null && (Time.frameCount - job.frame > 1 || !job.hammer || !job.plank || job.hammer.held == null)) EndJob();
        }
        private static void EndJob() { if (job != null && job.hammer) job.hammer.heldRotationOffset = job.startOffset; job = null; }
    }

    // Minimal OBJ reader for the embedded plank meshes (positions, UVs, normals, polygon faces), x mirrored back into Unity's space.
    internal static class ObjMesh
    {
        // The whole file as one mesh.
        internal static Mesh Load(string resource, string name) => Read(resource, name, false)[0];
        // One mesh per object ("o" line), in file order; positions, UVs and normals are indexed across the whole file.
        internal static List<Mesh> LoadAll(string resource, string name) => Read(resource, name, true);
        private static List<Mesh> Read(string resource, string name, bool split)
        {
            var v = new List<Vector3>(); var vt = new List<Vector2>(); var vn = new List<Vector3>(); var meshes = new List<Mesh>();
            List<Vector3> positions = null, normals = null; List<Vector2> uvs = null; List<int> triangles = null; Dictionary<string, int> map = null;
            var face = new List<int>();
            void Begin() { positions = new List<Vector3>(); uvs = new List<Vector2>(); normals = new List<Vector3>(); triangles = new List<int>(); map = new Dictionary<string, int>(); }
            void End()
            {
                if (positions.Count == 0 || triangles.Count == 0) return;
                var mesh = new Mesh { name = split ? name + " " + meshes.Count : name };
                if (positions.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(positions); mesh.SetUVs(0, uvs); mesh.SetNormals(normals); mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
                meshes.Add(mesh);
            }
            Begin();
            using (var stream = typeof(ObjMesh).Assembly.GetManifestResourceStream(resource))
            using (var reader = new StreamReader(stream ?? throw new InvalidDataException("Missing resource " + resource)))
            {
                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    var p = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 0 || p[0].StartsWith("#")) continue;
                    float N(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
                    if (p[0] == "o" && split) { End(); Begin(); }
                    else if (p[0] == "v") v.Add(new Vector3(-N(1), N(2), N(3)));
                    else if (p[0] == "vt") vt.Add(new Vector2(N(1), N(2)));
                    else if (p[0] == "vn") vn.Add(new Vector3(-N(1), N(2), N(3)));
                    else if (p[0] == "f")
                    {
                        face.Clear();
                        for (int i = 1; i < p.Length; i++)
                        {
                            if (!map.TryGetValue(p[i], out int index))
                            {
                                var parts = p[i].Split('/');
                                int Ix(int k, int count) { if (k >= parts.Length || parts[k].Length == 0) return -1; int n = int.Parse(parts[k], CultureInfo.InvariantCulture); return n > 0 ? n - 1 : count + n; }
                                int a = Ix(0, v.Count), b = Ix(1, vt.Count), c = Ix(2, vn.Count);
                                index = positions.Count; map[p[i]] = index;
                                positions.Add(v[a]); uvs.Add(b >= 0 ? vt[b] : Vector2.zero); normals.Add(c >= 0 ? vn[c] : Vector3.up);
                            }
                            face.Add(index);
                        }
                        for (int i = 1; i + 1 < face.Count; i++) { triangles.Add(face[0]); triangles.Add(face[i + 1]); triangles.Add(face[i]); }
                    }
                }
            }
            End();
            if (meshes.Count == 0) throw new InvalidDataException("Empty mesh " + resource);
            return meshes;
        }
        internal static void Shift(Mesh mesh, Vector3 offset)
        {
            var v = mesh.vertices; for (int i = 0; i < v.Length; i++) v[i] += offset;
            mesh.vertices = v; mesh.RecalculateBounds();
        }
    }

    [HarmonyPatch(typeof(PrefabsDirectory), "Start")] internal static class PlankRegistrationPatch
    { static void Prefix(PrefabsDirectory __instance) { WoodPlanks.Register(__instance); Tarps.Register(__instance); WoodPoles.Register(__instance); } }
    [HarmonyPatch(typeof(ShopArea), "Awake")] internal static class PlankStockPatch
    { static void Postfix(ShopArea __instance) { WoodPlanks.AddStock(__instance); Tarps.AddStock(__instance); } }
    [HarmonyPatch(typeof(GoPointerButton), "OnAltActivate", new[] { typeof(GoPointer) })] internal static class UnbundlePatch
    {
        static void Postfix(GoPointerButton __instance, GoPointer activatingPointer)
        {
            if (__instance is ShipItem item && activatingPointer && !activatingPointer.GetHeldItem() && WoodPlanks.Unbundleable(item)) WoodPlanks.Ask(item);
        }
    }
    [HarmonyPatch(typeof(CrateSealUI), "Update")] internal static class UnbundlePopupPatch { static bool Prefix(CrateSealUI __instance) => WoodPlanks.PopupUpdate(__instance); }
    [HarmonyPatch(typeof(CrateSealUI), "Activate")] internal static class UnbundleConfirmPatch { static bool Prefix(CrateSealUI __instance) => WoodPlanks.PopupConfirm(__instance); }
    [HarmonyPatch(typeof(CrateSealUI), "HideUI")] internal static class UnbundleClosePatch { static void Postfix() => WoodPlanks.PopupClosed(); }
    [HarmonyPatch(typeof(CrateSealUI), "ShowUI")] internal static class UnbundleCratePatch { static void Prefix() => WoodPlanks.PopupClosed(); }
    [HarmonyPatch(typeof(ShipItemHammer), "OnAltActivate", new Type[0])] internal static class PlankHammerPatch { static bool Prefix(ShipItemHammer __instance) => WoodPlanks.HammerStart(__instance); }
    [HarmonyPatch(typeof(ShipItemHammer), "OnAltHeld", new Type[0])] internal static class PlankHammerHeldPatch { static bool Prefix(ShipItemHammer __instance) => WoodPlanks.HammerHeld(__instance); }
    // Taking a plank off a wall, deck or roof resets its hammering progress.
    [HarmonyPatch(typeof(ShipItem), "OnPickup")] internal static class PlankPickupPatch
    { static void Postfix(ShipItem __instance) { if (WoodPlanks.IsPlank(__instance)) __instance.amount = 0; } }
    // A loaded plank shows its saved look.
    [HarmonyPatch(typeof(SaveablePrefab), "Load")] internal static class PlankLoadPatch
    { static void Postfix(SaveablePrefab __instance) { var item = __instance.GetComponent<ShipItem>(); if (WoodPlanks.IsPlank(item)) WoodPlanks.ApplyLook(item); else if (WoodPoles.IsPole(item)) WoodPoles.ApplyLength(item); } }
    [HarmonyPatch(typeof(ShipItem), "UpdateLookText")] internal static class PlankLookTextPatch
    {
        static void Postfix(ShipItem __instance)
        {
            if (!WoodPlanks.IsPlank(__instance)) return;
            var hint = WoodPlanks.HullHint(__instance);
            if (hint != null) { __instance.lookText = hint; __instance.description = ""; }
        }
    }
    [HarmonyPatch(typeof(LookUI), "ShowLookText")] internal static class PlankLookUIPatch
    {
        private static readonly MethodInfo hideIcons = AccessTools.Method(typeof(LookUI), "HideIcons"), showL = AccessTools.Method(typeof(LookUI), "ShowLicon"), showR = AccessTools.Method(typeof(LookUI), "ShowRicon");
        static void Postfix(LookUI __instance, GoPointerButton button)
        {
            if (!(button is ShipItem item) || !WoodPlanks.Registered) return;
            if (WoodPlanks.IsPlank(item) && WoodPlanks.HullHint(item) != null)
            {
                // Only the hull lines (progress, damage, planks per 1%): no name, controls or hint.
                Fields.Get<TextMesh>(__instance, "controlsText").text = ""; Fields.Get<TextMesh>(__instance, "hintText").text = "";
                hideIcons.Invoke(__instance, null);
                return;
            }
            var pointer = Fields.Get<GoPointer>(__instance, "pointer");
            if (global::Settings.controlsTextEnabled && WoodPlanks.Unbundleable(item) && pointer && !pointer.GetHeldItem() && WoodPlanks.BlockReason(item) == null)
            {
                showL.Invoke(__instance, null); showR.Invoke(__instance, null);
                Fields.Get<TextMesh>(__instance, "controlsText").text = item.nailed ? "\nunbundle" : "pick up\nunbundle";
            }
        }
    }

    // ---- Wood pole ----
    // The wood pole (prefab 643; the user, 2026-10-12): a round pole of the lumber's wood, 8 cm across, built like the planks
    // on the painting (a wall item). Held to a wall it sticks straight out; to a deck, straight up (the game sets a wall item's
    // +z into the surface: the pole runs from its pivot along -z). Once the hammer has nailed it, tarp corners tie to it, at
    // any angle (Tarps.Find), aboard and on land. Q and E set its length while it is held; it is kept in the saved health.
    internal static class WoodPoles
    {
        internal const int PoleIndex = 643, BundleIndex = 644;
        internal const float Thickness = .08f;   // 8 cm across (the user, 2026-10-12; was 12)
        private const int Sides = 12;
        // The lumber's material is the items' atlas (senna items paint), mostly black: the pole takes one long board strip of it,
        // the one the planks use (u .7637-.7773, v .236-.861), at the planks' scale (a 0.87 m board face spans 0.08 along the grain).
        private const float UvPerMetre = .0925f, U0 = .7645f, UWidth = .0120f, V0 = .25f;
        internal static GameObject Template, BundleTemplate;
        internal static bool Registered => Template;
        internal static readonly HashSet<ShipItem> Live = new HashSet<ShipItem>();   // every wood pole in the game (PoleHeldLook)
        private static readonly Dictionary<int, Mesh> meshes = new Dictionary<int, Mesh>();
        internal static bool IsPole(ShipItem item) => Registered && WoodPlanks.Index(item) == PoleIndex;
        internal static bool IsBundle(ShipItem item) => BundleTemplate && WoodPlanks.Index(item) == BundleIndex;
        internal static float Length(ShipItem pole) => Rules.PoleLength(pole.health);
        internal static string Format(float metres) => Rules.PoleLength(metres).ToString("F1", CultureInfo.InvariantCulture) + " m";

        internal static void Register(PrefabsDirectory directory)
        {
            if (Registered || !directory || directory.directory == null) return;
            var dir = directory.directory;
            if (dir.Length > PoleIndex && dir[PoleIndex])
            { Plugin.Log?.LogError("Prefab slot 643 is used by another mod; wood poles are disabled."); return; }
            if (dir.Length <= WoodPlanks.LumberIndex || !dir[WoodPlanks.LumberIndex] || !dir[WoodPlanks.PaintingIndex])
            { Plugin.Log?.LogError("Lumber or painting prefab missing; wood poles are disabled."); return; }
            try
            {
                var holder = new GameObject("EconomyOverhaul pole template"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder);
                var wood = dir[WoodPlanks.LumberIndex].GetComponent<MeshRenderer>().sharedMaterial;
                var o = WoodPlanks.Template(dir[WoodPlanks.PaintingIndex], holder.transform, "643 wood pole", Mesh(Rules.PoleShopLength), wood, PoleIndex,
                    "wood pole", Rules.PoleValue, Rules.PoleMass(Rules.PoleShopLength), false);
                var item = o.GetComponent<ShipItem>(); item.wallAttachment = true; item.health = Rules.PoleShopLength; item.amount = 0;
                var rootLook = o.GetComponent<MeshRenderer>();
                var held = new GameObject(HeldLookName); held.transform.SetParent(o.transform, false); held.SetActive(false);
                held.AddComponent<MeshFilter>().sharedMesh = Mesh(Rules.PoleShopLength);
                var heldRenderer = held.AddComponent<MeshRenderer>(); heldRenderer.sharedMaterials = rootLook.sharedMaterials;
                heldRenderer.shadowCastingMode = rootLook.shadowCastingMode; heldRenderer.receiveShadows = rootLook.receiveShadows;
                HeldPose(held.transform, Rules.PoleShopLength); o.AddComponent<PoleHeldLook>();
                if (dir.Length <= BundleIndex) { var resized = new GameObject[BundleIndex + 1]; Array.Copy(dir, resized, dir.Length); directory.directory = dir = resized; }
                dir[PoleIndex] = o; Template = o;
                Plugin.Log?.LogInfo("Registered wood pole (643).");
                // The bundle the shops sell (user, 2026-10-12): four 2 m poles, carried in both hands, unbundled as the planks' bundle.
                if (dir[BundleIndex]) Plugin.Log?.LogError("Prefab slot 644 is used by another mod; wood pole bundles are disabled (no poles in shops).");
                else
                {
                    var bundle = WoodPlanks.Template(dir[WoodPlanks.PaintingIndex], holder.transform, "644 wood pole bundle", BundleMesh(), wood, BundleIndex,
                        "wood pole bundle", Rules.PoleBundleValue, Rules.PoleBundleMass, true);
                    var bundled = bundle.GetComponent<ShipItem>(); bundled.wallAttachment = false; bundled.amount = 0;
                    dir[BundleIndex] = bundle; BundleTemplate = bundle;
                    Plugin.Log?.LogInfo("Registered wood pole bundle (644).");
                }
            }
            catch (Exception e) { Template = null; Plugin.Log?.LogError("Wood pole registration failed; wood poles are disabled. " + e); }
        }

        // One mesh per length, made once: 12 sides with smooth normals, flat end caps, the pivot at the base (z 0), the pole
        // along -z; the grain runs along it.
        internal static Mesh Mesh(float metres)
        {
            int key = Rules.PoleTenths(metres);
            if (meshes.TryGetValue(key, out var cached) && cached) return cached;
            float length = key / 10f, r = Thickness / 2;
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            for (int i = 0; i <= Sides; i++)   // across the strip and back again (mirrored: no seam, half the stretch)
            {
                float a = i * 2 * Mathf.PI / Sides; var out_ = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                float u = U0 + UWidth * (1 - Mathf.Abs(2f * i / Sides - 1));
                v.Add(out_ * r); n.Add(out_); uv.Add(new Vector2(u, V0));
                v.Add(out_ * r + Vector3.back * length); n.Add(out_); uv.Add(new Vector2(u, V0 + length * UvPerMetre));
            }
            for (int i = 0; i < Sides; i++) { int p = 2 * i; t.AddRange(new[] { p, p + 1, p + 2, p + 2, p + 1, p + 3 }); }
            foreach (var (z, normal) in new[] { (0f, Vector3.forward), (-length, Vector3.back) })
            {
                int centre = v.Count; v.Add(new Vector3(0, 0, z)); n.Add(normal); uv.Add(new Vector2(U0 + UWidth / 2, V0 + r * UvPerMetre));
                for (int i = 0; i < Sides; i++)
                {
                    float a = i * 2 * Mathf.PI / Sides;
                    v.Add(new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, z)); n.Add(normal);
                    uv.Add(new Vector2(U0 + (Mathf.Cos(a) + 1) / 2 * UWidth, V0 + (Mathf.Sin(a) + 1) * r * UvPerMetre));
                }
                for (int i = 0; i < Sides; i++)
                {
                    int a = centre + 1 + i, b = centre + 1 + (i + 1) % Sides;
                    if (z == 0) t.AddRange(new[] { centre, a, b }); else t.AddRange(new[] { centre, b, a });
                }
            }
            var m = new Mesh { name = "wood pole " + Format(length) };
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(t, 0);
            m.RecalculateBounds(); m.RecalculateTangents();
            meshes[key] = m;
            return m;
        }

        // The bundle: four 2 m poles 2 x 2 (touching), its pivot at one end (z 0) and the poles along -z as a pole's; two dark
        // rope bands (the atlas's black), 0.4 m from each end.
        internal const float BundleBand = .1f;   // the bands' radius: the bundle's reach from its axis
        private static Mesh bundleMesh;
        internal static Mesh BundleMesh()
        {
            if (bundleMesh) return bundleMesh;
            float r = Thickness / 2, length = Rules.PoleShopLength;
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            void Tube(Vector2 at, float radius, float z0, float z1, bool wood)
            {
                int start = v.Count; var dark = new Vector2(.01f, .01f);
                for (int i = 0; i <= Sides; i++)
                {
                    float a = i * 2 * Mathf.PI / Sides, u = U0 + UWidth * (1 - Mathf.Abs(2f * i / Sides - 1)); var out_ = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                    v.Add((Vector3)at + out_ * radius + Vector3.forward * z0); n.Add(out_); uv.Add(wood ? new Vector2(u, V0) : dark);
                    v.Add((Vector3)at + out_ * radius + Vector3.forward * z1); n.Add(out_); uv.Add(wood ? new Vector2(u, V0 + (z0 - z1) * UvPerMetre) : dark);
                }
                for (int i = 0; i < Sides; i++) { int p = start + 2 * i; t.AddRange(new[] { p, p + 1, p + 2, p + 2, p + 1, p + 3 }); }
                if (!wood) return;
                foreach (var (z, normal) in new[] { (z0, Vector3.forward), (z1, Vector3.back) })
                {
                    int centre = v.Count; v.Add((Vector3)at + Vector3.forward * z); n.Add(normal); uv.Add(new Vector2(U0 + UWidth / 2, V0 + r * UvPerMetre));
                    for (int i = 0; i < Sides; i++)
                    {
                        float a = i * 2 * Mathf.PI / Sides;
                        v.Add((Vector3)at + new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, z)); n.Add(normal);
                        uv.Add(new Vector2(U0 + (Mathf.Cos(a) + 1) / 2 * UWidth, V0 + (Mathf.Sin(a) + 1) * r * UvPerMetre));
                    }
                    for (int i = 0; i < Sides; i++)
                    {
                        int a = centre + 1 + i, b = centre + 1 + (i + 1) % Sides;
                        if (normal == Vector3.forward) t.AddRange(new[] { centre, a, b }); else t.AddRange(new[] { centre, b, a });
                    }
                }
            }
            foreach (var at in new[] { new Vector2(-r, -r), new Vector2(r, -r), new Vector2(r, r), new Vector2(-r, r) }) Tube(at, r, 0, -length, true);
            foreach (float z in new[] { -.4f, -length + .4f }) Tube(Vector2.zero, BundleBand, z + .025f, z - .025f, false);
            var m = new Mesh { name = "wood pole bundle" };
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(t, 0);
            m.RecalculateBounds(); m.RecalculateTangents();
            return bundleMesh = m;
        }
        // Unbundled: its four poles where they lie in it, each from the bundle's end along it.
        internal static List<Pose> BundleLayout(ShipItem bundle)
        {
            float r = Thickness / 2;
            return new[] { new Vector2(-r, -r), new Vector2(r, -r), new Vector2(r, r), new Vector2(-r, r) }
                .Select(a => new Pose(bundle.transform.TransformPoint(new Vector3(a.x, a.y, 0)), bundle.transform.rotation)).ToList();
        }

        // A length on a pole: its health, its mesh (the game holds the root at scale 1; the placement ghost and the outline use
        // the root mesh), its box and its physics copy's box (copied from it once, ItemRigidbody.AddCollider), its weight.
        internal static void SetLength(ShipItem pole, float metres)
        {
            if (!pole) return;
            float length = Rules.PoleLength(metres); pole.health = length;
            var mesh = Mesh(length);
            var filter = pole.GetComponent<MeshFilter>(); if (filter) filter.sharedMesh = mesh;
            var held = pole.transform.Find(HeldLookName); if (held) { held.GetComponent<MeshFilter>().sharedMesh = mesh; HeldPose(held, length); }
            var box = pole.GetComponent<BoxCollider>(); if (box) { box.size = mesh.bounds.size; box.center = mesh.bounds.center; }
            var body = pole.itemRigidbodyC; var copy = body ? Fields.Get<BoxCollider>(body, "boxCol") : null;
            if (copy) { copy.size = mesh.bounds.size; copy.center = mesh.bounds.center; }
            pole.mass = Rules.PoleMass(length);
            if (body && body.GetBody()) body.UpdateMass();
        }
        internal static void ApplyLength(ShipItem pole) => SetLength(pole, pole.health);

        // Held, a wall item points its +z ahead (to aim it at a wall), so the pole, along -z, would run back through the player's
        // eyes. Held, it is drawn standing up from the hand like a staff, tilted 15 degrees forward, a third of it below the hand, 30 cm right of the crosshair (where the game holds a wall item)
        // (the user, 2026-10-12). The root keeps its own mesh and direction: the placement ghost shows where it will stand.
        internal const string HeldLookName = "wood pole held look";
        private static readonly Vector3 HeldAxis = new Vector3(0, Mathf.Cos(15 * Mathf.Deg2Rad), Mathf.Sin(15 * Mathf.Deg2Rad));
        internal static void HeldPose(Transform look, float length)
        { look.localRotation = Quaternion.FromToRotation(Vector3.back, HeldAxis); look.localPosition = -HeldAxis * (length / 3) + Vector3.right * .3f; look.localScale = Vector3.one; }

        // Q longer, E shorter, 0.1 m a press; held, repeating after 0.4 s. Only a held pole that is bought and isn't nailed (a
        // shop's pole can be picked up before buying it: resized, it would go back to its stall at the new length).
        private static KeyCode repeating; private static float repeatAt;
        // On another pole (the user, 2026-10-12: only on a nailed one). The game's own wall ray starts at the held pole's base,
        // about 1 m ahead, and runs along the pole, so it hardly ever meets an 8 cm pole (measured by -PoleAttach). With the
        // crosshair on a nailed pole within reach (2.55 m, as a tarp's corner) the held pole attaches where it points: on its side
        // sticking straight out of it, on its end standing on it; loose items are seen through, solid surfaces are not. The game's green ghost shows it, its drop attaches it there
        // (ShipItem.OnDrop) and its hammer nails it, as on any wall.
        private static readonly AccessTools.FieldRef<ShipItem, bool> inRangeOfWall = AccessTools.FieldRefAccess<ShipItem, bool>("inRangeOfWall");
        private static readonly AccessTools.FieldRef<ShipItem, Vector3> attachPos = AccessTools.FieldRefAccess<ShipItem, Vector3>("attachPos");
        private static readonly AccessTools.FieldRef<ShipItem, Quaternion> attachRot = AccessTools.FieldRefAccess<ShipItem, Quaternion>("attachRot");

        internal const float AttachReach = 2.55f;
        internal static ShipItem AimedPole;   // the nailed pole the held pole last pointed at (for tests)
        internal static Pose AimedAt;   // where on it, as drawn (for tests)
        // Aimed at a pole aboard a ship: the held pole, that frame, the ship and its place in her frame (OnPoleDrop).
        private static ShipItem aimedBy; private static int aimedFrame; private static Transform aimedShip; private static Pose aimedLocal;
        private static readonly MethodInfo enterBoat = AccessTools.Method(typeof(ShipItem), "EnterBoat");
        internal static void AimAtPole(ShipItem held, Ray eye)
        {
            AimedPole = null; if (!held.held) return;
            // The nearest nailed pole the ray meets, from its box where it is drawn: aboard a moving ship the physics of an item's
            // drawn collider does not follow her (measured by -PoleProbe), so the box is taken from its transform.
            ShipItem target = null; float near = AttachReach; Vector3 normal = default;
            foreach (var pole in Live)
            {
                if (!pole || pole == held || !pole.sold || !pole.nailed) continue;
                var box = pole.GetComponent<BoxCollider>(); if (!box) continue;
                var t = pole.transform; Vector3 o = t.InverseTransformPoint(eye.origin) - box.center, d = t.InverseTransformDirection(eye.direction), half = box.size / 2;
                float enter = 0, leave = near; int axis = -1; float sign = 0; bool miss = false;
                for (int k = 0; k < 3 && !miss; k++)
                {
                    if (Mathf.Abs(d[k]) < 1e-6f) { miss = Mathf.Abs(o[k]) > half[k]; continue; }
                    float a = (-half[k] - o[k]) / d[k], b = (half[k] - o[k]) / d[k];
                    if (a > b) { var x = a; a = b; b = x; }
                    if (a > enter) { enter = a; axis = k; sign = d[k] > 0 ? -1 : 1; }
                    leave = Mathf.Min(leave, b); miss = enter > leave;
                }
                if (miss || axis < 0 || enter < .1f || enter >= near) continue;   // missed, started inside it, or beyond a nearer one
                var n = Vector3.zero; n[axis] = sign;
                target = pole; near = enter; normal = t.TransformDirection(n).normalized;
            }
            if (!target) return;
            // A solid surface nearer than the pole (a wall, her deck) is in the way: the game's own wall test decides.
            foreach (var h in Physics.RaycastAll(eye, near - .05f, ~0, QueryTriggerInteraction.Ignore))
            {
                var c = h.collider;
                if (h.distance < .1f || c.transform.IsChildOf(held.transform) || held.itemRigidbodyC && c.transform.IsChildOf(held.itemRigidbodyC.transform) || c.GetComponentInParent<ItemRigidbody>() || c.GetComponentInParent<ShipItem>()) continue;
                return;
            }
            var at = eye.GetPoint(near);
            var turn = Mathf.Abs(normal.y) > .8f ? Quaternion.LookRotation(-normal, held.transform.up) : Quaternion.LookRotation(-normal, Vector3.up);
            inRangeOfWall(held) = true; attachPos(held) = at; attachRot(held) = turn; AimedPole = target; AimedAt = new Pose(at, turn);
            // The green ghost straight at the spot, by the game's targeter: its own SetUpTargeter takes the spot as relative to the
            // held item's physics copy, which is not where the item is drawn (aboard, on her walk copy; on land, measured 0.9 m
            // off), so the ghost was drawn far off (until 2026-10-13: the user found pole on pole too hard to use).
            held.held.GetTargeter().DisplayTargeter(at, turn, held.GetComponent<MeshFilter>().sharedMesh);
            aimedBy = held; aimedFrame = Time.frameCount; aimedShip = target.currentActualBoat;
            if (aimedShip) aimedLocal = new Pose(aimedShip.InverseTransformPoint(at), Quaternion.Inverse(aimedShip.rotation) * turn);
        }
        // Dropped onto a pole aboard a ship: on her, at its place in her frame, its body on her walk copy (as a returned canvas
        // pack is put aboard), however it was held; the game's drop alone places it in the world, where she has moved on by the
        // time it boards her.
        internal static void OnPoleDrop(ShipItem pole)
        {
            if (pole != aimedBy || Time.frameCount - aimedFrame > 1 || !aimedShip || !pole.itemRigidbodyC) return;
            var ship = aimedShip; aimedBy = null;
            if (pole.currentActualBoat != ship)
            {
                var embark = Tarps.Link(ship)?.GetComponent<Collider>();
                if (embark) try { enterBoat.Invoke(pole, new object[] { embark }); } catch (Exception e) { Plugin.Log?.LogWarning("A wood pole could not board her: " + (e.InnerException ?? e).Message); }
            }
            if (pole.currentActualBoat != ship) return;
            pole.transform.SetPositionAndRotation(ship.TransformPoint(aimedLocal.position), ship.rotation * aimedLocal.rotation);
            pole.itemRigidbodyC.ForceRigidbodyToWalkCol(); pole.itemRigidbodyC.attached = true;
        }

        internal static void Keys(GoPointer pointer)
        {
            var held = pointer ? pointer.GetHeldItem() as ShipItem : null;
            if (!GameState.playing || !IsPole(held) || !held.sold || held.nailed) { repeating = KeyCode.None; return; }
            int step = 0;
            foreach (var (key, d) in new[] { (KeyCode.Q, 1), (KeyCode.E, -1) })
            {
                if (Input.GetKeyDown(key)) { step = d; repeating = key; repeatAt = Time.unscaledTime + .4f; }
                else if (repeating == key && Input.GetKey(key) && Time.unscaledTime >= repeatAt) { step = d; repeatAt = Time.unscaledTime + .08f; }
            }
            if (repeating != KeyCode.None && !Input.GetKey(repeating)) repeating = KeyCode.None;
            if (step == 0) return;
            float before = Length(held), after = Rules.PoleLength(before + step * Rules.PoleStep);
            if (after != before) SetLength(held, after);
            Loans.Notify("Wood pole " + Format(after) + (after != before ? "" : step > 0 ? " (longest)" : " (shortest)"));
        }
    }
    // Shows the held look while the pole is in a hand, the pole itself otherwise; switched only when that changes.
    internal sealed class PoleHeldLook : MonoBehaviour
    {
        private ShipItem item; private MeshRenderer root; private GameObject look; private bool shown;
        private void Awake() { item = GetComponent<ShipItem>(); root = GetComponent<MeshRenderer>(); var t = transform.Find(WoodPoles.HeldLookName); look = t ? t.gameObject : null; if (item) WoodPoles.Live.Add(item); }
        private void OnDestroy() => WoodPoles.Live.Remove(item);
        private void LateUpdate()
        {
            bool held = item && item.held;
            if (held == shown) return;
            shown = held; if (look) look.SetActive(held); if (root) root.enabled = !held;
        }
    }
    [HarmonyPatch(typeof(GoPointer), "LateUpdate")] internal static class PoleKeysPatch
    { static void Postfix(GoPointer __instance) { if (WoodPoles.Registered) WoodPoles.Keys(__instance); } }
    // Dropped onto a pole aboard a ship: put on her where it was aimed (WoodPoles.OnPoleDrop), after the game's own drop.
    [HarmonyPatch(typeof(ShipItem), "OnDrop")] internal static class PoleDropPatch
    { static void Postfix(ShipItem __instance) { if (WoodPoles.Registered && WoodPoles.IsPole(__instance)) WoodPoles.OnPoleDrop(__instance); } }
    // A held pole aimed at a nailed pole attaches to it (WoodPoles.AimAtPole), after the game's own wall test of the frame.
    [HarmonyPatch(typeof(ShipItem), "Update")] internal static class PoleOnPolePatch
    { static void Postfix(ShipItem __instance) { if (__instance.held && WoodPoles.Registered && __instance.sold && WoodPoles.IsPole(__instance)) WoodPoles.AimAtPole(__instance, new Ray(__instance.held.transform.position, __instance.held.transform.forward)); } }
    // Looked at: "wood pole" and its length under it.
    [HarmonyPatch(typeof(ShipItem), "UpdateLookText")] internal static class PoleLookTextPatch
    { static void Postfix(ShipItem __instance) { if (WoodPoles.IsPole(__instance)) __instance.description = WoodPoles.Format(WoodPoles.Length(__instance)); } }
    // Picked up: its length and the keys.
    [HarmonyPatch(typeof(ShipItem), "OnPickup")] internal static class PolePickupPatch
    { static void Postfix(ShipItem __instance) { if (WoodPoles.IsPole(__instance) && __instance.sold) Loans.Notify("Wood pole " + WoodPoles.Format(WoodPoles.Length(__instance)) + " (Q/E: length)"); } }
    // Removed (the game's clean-up of items left off a ship 600 m away, or any other way): a tarp tied to it is untied, and one it
    // held up drops back to its shape without it.
    [HarmonyPatch(typeof(ShipItem), "DestroyItem")] internal static class PoleRemovePatch
    { static void Prefix(ShipItem __instance) { if (WoodPoles.IsPole(__instance) && Tarps.Registered && __instance.nailed) { Tarps.UntieFrom(__instance); Tarps.PoleMoved(__instance, true); } } }
    // Nailed with the hammer (the game's ShipItemHammer.NailItem, which may refuse): a wood pole under a tarp holds it up.
    [HarmonyPatch(typeof(ShipItemHammer), "NailItem")] internal static class PoleNailPatch
    { static void Postfix(ShipItem item) { if (item && item.nailed && WoodPoles.IsPole(item)) Tarps.PoleMoved(item, false); } }

    // ---- Canvas tarp ----
    // The canvas pack (prefab 642): a tarp folded up, drawn with the mail parcel's model while it is an item. Tying its four
    // corners makes a tarp (as built: CANVAS.md); untying gives a pack back.
    internal static class Tarps
    {
        internal const int PackIndex = 642, MailGood = 51;
        // Price 500, mass 30 (user, 2026-10-05; oakum is 180 and 9).
        internal const int PackValue = 500; internal const float PackMass = 30;
        internal const float PackScale = .7f; internal const string LookName = "canvas pack look";
        // Wear (user, 2026-10-05): a tied tarp loses 10 health a game day, 100 to 0 in 10 days; a folded pack keeps its health
        // (in ShipItem.health, which the game saves with every item). The health is never shown.
        internal const float FullHealth = Rules.TarpFullHealth, WearPerDay = Rules.TarpWearPerDay;
        internal static double Now => GameState.day + (Sun.sun ? Sun.sun.globalTime / 24.0 : 0);
        internal static GameObject PackTemplate;
        internal static bool Registered => PackTemplate;
        internal static bool IsPack(ShipItem item) => Registered && WoodPlanks.Index(item) == PackIndex;

        // The template lives under an inactive holder, as the plank templates do: instances start active, the template never runs.
        internal static void Register(PrefabsDirectory directory)
        {
            if (Registered || !directory || directory.directory == null) return;
            var dir = directory.directory; int mail = PrefabsDirectory.GoodToItemIndex(MailGood);
            if (dir.Length > PackIndex && dir[PackIndex])
            { Plugin.Log?.LogError("Prefab slot 642 is used by another mod; canvas packs are disabled."); return; }
            if (dir.Length <= mail || !dir[mail] || !dir[mail].GetComponent<ShipItem>())
            { Plugin.Log?.LogError("Mail parcel prefab missing; canvas packs are disabled."); return; }
            try
            {
                var holder = new GameObject("EconomyOverhaul canvas template"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder);
                var o = UnityEngine.Object.Instantiate(dir[mail], holder.transform, false); o.name = "642 canvas pack";
                // Scale 1, as the game holds every item outside a pocket (ItemRigidbody.LateUpdate).
                o.transform.localPosition = Vector3.zero; o.transform.localRotation = Quaternion.identity; o.transform.localScale = Vector3.one;
                UnityEngine.Object.DestroyImmediate(o.GetComponent<Good>());
                var label = o.transform.Find("label"); if (label) UnityEngine.Object.DestroyImmediate(label.gameObject);   // no mail sticker
                // 70% of the parcel (user, 2026-10-05, to fit the small stalls). The game holds an item's root at scale 1 and the
                // parcel's mesh can't be read, so a scaled child draws it; the root keeps its mesh (the game reads it) unseen, and
                // its box (the click box, and the physics copy the game makes from it) shrinks with it.
                var rootLook = o.GetComponent<MeshRenderer>();
                var look = new GameObject(LookName); look.transform.SetParent(o.transform, false); look.transform.localScale = Vector3.one * PackScale;
                look.AddComponent<MeshFilter>().sharedMesh = o.GetComponent<MeshFilter>().sharedMesh;
                var lookRenderer = look.AddComponent<MeshRenderer>(); lookRenderer.sharedMaterials = rootLook.sharedMaterials;
                lookRenderer.shadowCastingMode = rootLook.shadowCastingMode; lookRenderer.receiveShadows = rootLook.receiveShadows;
                rootLook.enabled = false;
                var box = o.GetComponent<BoxCollider>(); box.center *= PackScale; box.size *= PackScale;
                o.AddComponent<PackLook>();
                // Carried in both hands as the parcel is (big: no pocket), never a wall item.
                var item = o.GetComponent<ShipItem>();
                item.name = "canvas pack"; item.value = PackValue; item.mass = PackMass; item.big = true; item.wallAttachment = false; item.health = FullHealth;
                item.category = TransactionCategory.toolsAndSupplies; item.sold = false; item.lookText = ""; item.description = "";
                o.GetComponent<SaveablePrefab>().prefabIndex = PackIndex;
                if (dir.Length <= PackIndex) { var resized = new GameObject[PackIndex + 1]; Array.Copy(dir, resized, dir.Length); directory.directory = dir = resized; }
                dir[PackIndex] = o; PackTemplate = o;
                Plugin.Log?.LogInfo("Registered canvas pack (642).");
                // Found while the start menu's world is loaded (the bamboo there uses it), and kept loaded from then on.
                if (!FindTornShader()) Plugin.Log?.LogInfo("Canvas: " + TornShaderName + " not loaded yet; torn tarps show no holes until it is.");
            }
            catch (Exception e) { PackTemplate = null; Plugin.Log?.LogError("Canvas pack registration failed; canvas packs are disabled. " + e); }
        }

        // ---- Tying ----
        // A corner: on a ship, in her boarding frame (the frame the game maps her walk copy to); on land, a real world position
        // and a world turn (a world shift moves the shifting world's children, not its root).
        internal sealed class Corner
        {
            internal Transform frame; internal bool onShip; internal Vector3 local; internal Quaternion rotation; internal Collider part; internal GameObject knot;
            internal bool Land => !onShip;
            internal Vector3 Position => onShip ? frame.TransformPoint(local) : FloatingOriginManager.instance.RealPosToShiftingPos(local);
            internal Quaternion Turn => onShip ? frame.rotation * rotation : rotation;
        }
        internal const float Reach = 2.55f, Nearest = .1f, MaxSide = 50, MinApart = .3f;
        internal const string NotFixed = "Tie it to a fixed part", Mixed = "All four corners on one ship, or all on land",
            TooLong = "Tarp sides are 50 m at most", TooClose = "Too close to another corner", Cleared = "Tarp corners cleared";
        internal static readonly List<Corner> Pending = new List<Corner>();
        internal static ShipItem PendingPack;

        // The ship the player is aboard: her boarding frame and walk copy (the game's PlayerEmbarkerNew pair), or none on land.
        internal static void Aboard(out Transform frame, out Transform walk)
        {
            frame = GameState.currentBoat; walk = null;
            if (!frame) return;
            var link = Link(frame); walk = link ? link.walkCollider : null;
            if (!walk) frame = null;
        }
        // A boarding frame's boarding trigger that names her walk copy, the player-sized one first (as the game boards).
        internal static BoatEmbarkCollider Link(Transform frame) => frame && frame.parent ? frame.parent.GetComponentsInChildren<BoatEmbarkCollider>(true)
            .Where(c => c.walkCollider && c.transform.parent == frame).OrderBy(c => c.CompareTag("EmbarkColPlayer") ? 0 : 1).FirstOrDefault() : null;

        // Where the crosshair ray (eye, forward, 2.55 m: the game's wall-attach reach of 1.3 m past a one-handed item's hold point)
        // would tie a corner, or why not. Aboard, the ray runs on the ship's walk copy, where her fixed parts are solid: hull,
        // deck, rails, masts, cabins, stays and shrouds; trigger volumes are skipped, item copies, hatches and doors refused.
        // Sails (booms, yards) and the game's interactive things are not in the walk copy, so the same ray in the world refuses
        // a corner when one of them is in front (a sail's invisible wind shadow box is skipped). On land: a solid part without a
        // rigidbody that isn't an item or a door.
        internal static Corner Find(Ray eye, Transform frame, Transform walk, ShipItem held, out string refusal)
        {
            refusal = NotFixed;
            // All layers: an item's solid physics copy is on Ignore Raycast (its visible collider is a trigger).
            var world = Physics.RaycastAll(eye, Reach, ~0, QueryTriggerInteraction.Ignore)
                .Where(h => h.distance >= Nearest && !Own(h.collider, held) && !OnWalkCopy(h.collider.transform) && !(SailPart(h.collider) && !Visible(h.collider))).OrderBy(h => h.distance).ToArray();
            RaycastHit? real = world.Length > 0 ? world[0] : (RaycastHit?)null, onShip = null; var ray = eye;
            if (frame && walk)
            {
                ray = new Ray(walk.TransformPoint(frame.InverseTransformPoint(eye.origin)), walk.TransformDirection(frame.InverseTransformDirection(eye.direction)));
                var hits = Physics.RaycastAll(ray, Reach, ~0, QueryTriggerInteraction.Ignore)
                    .Where(h => h.distance >= Nearest && h.collider.transform.IsChildOf(walk) && !Own(h.collider, held)).OrderBy(h => h.distance).ToArray();
                if (hits.Length > 0) onShip = hits[0];
            }
            if (real != null && Moving(real.Value.collider) && !LandPole(real.Value.collider) && (onShip == null || real.Value.distance < onShip.Value.distance - .05f))
            { Why = "moving part in front: " + Path(real.Value.collider.transform) + " at " + real.Value.distance.ToString("F2") + " m" + (onShip != null ? " (ship part at " + onShip.Value.distance.ToString("F2") + " m)" : ""); return null; }
            // Her own stays as they are in the world, where her walk copy has nothing nearer (user, 2026-10-06): some stays' walk
            // colliders don't reach their lower ends (the Cog's two forestays start 0.9 and 1.7 m up them, the part in reach
            // from her bow). The knot goes on the stay's rope line.
            if (frame && walk && real != null && OwnStay(real.Value.collider, frame) && (onShip == null || real.Value.distance < onShip.Value.distance - .3f))
            {
                var h = real.Value; var at = h.collider is CapsuleCollider c ? OnAxis(c, eye) : h.point;
                Why = null;
                return new Corner { frame = frame, onShip = true, local = frame.InverseTransformPoint(at),
                    rotation = Quaternion.LookRotation(-frame.InverseTransformDirection(h.normal), Vector3.up), part = h.collider };
            }
            if (onShip != null)
            {
                var h = onShip.Value;
                // An item nailed with the hammer is fixed to her too (user, 2026-10-10): whatever the game let a hammer nail
                // (big items such as crates, trade goods and furniture; hangables; wall items). Loose items stay refused.
                if (!NailedItem(h.collider, held) && (h.collider.GetComponentInParent<ItemRigidbody>() || h.collider.GetComponentInParent<ShipItem>()) || MovingWalk(frame, h.collider.transform))
                { Why = "item, hatch or door on the walk copy: " + Path(h.collider.transform); return null; }
                Why = null;
                // On a stay's walk copy too, the knot goes on its rope line rather than on its collider's surface.
                var at = h.collider is CapsuleCollider c && WalkStay(frame, h.collider.transform) ? OnAxis(c, ray) : h.point;
                return new Corner { frame = frame, onShip = true, local = walk.InverseTransformPoint(at),
                    rotation = Quaternion.LookRotation(-walk.InverseTransformDirection(h.normal), Vector3.up), part = h.collider };
            }
            if (real == null || real.Value.collider.attachedRigidbody && !LandPole(real.Value.collider))
            { Why = real == null ? "nothing in reach" : "not fixed (has a rigidbody): " + Path(real.Value.collider.transform); return null; }
            Why = null;
            var land = real.Value;
            return new Corner { local = FloatingOriginManager.instance.ShiftingPosToRealPos(land.point), rotation = Quaternion.LookRotation(-land.normal, Vector3.up), part = land.collider };
        }
        internal static string Why;   // why the last Find gave no corner (for logs and tests)
        private static string Path(Transform t) => t.parent ? t.parent.name + "/" + t.name : t.name;
        private static bool Own(Collider c, ShipItem held) => held && (c.transform.IsChildOf(held.transform) || held.itemRigidbodyC && c.transform.IsChildOf(held.itemRigidbodyC.transform));
        // A collider of a bought item's physics copy that the hammer has nailed (the game's ShipItem.nailed).
        internal static ShipItem Nailed(Collider c) { var body = c ? c.GetComponentInParent<ItemRigidbody>() : null; var item = body ? body.GetShipItem() : null; return item && item.sold && item.nailed ? item : null; }
        private static bool NailedItem(Collider c, ShipItem held) { var item = Nailed(c); return item && item != held; }
        // On land, a wood pole nailed with the hammer is a fixed part (user, 2026-10-12); other items on land are not.
        private static bool LandPole(Collider c) { var item = Nailed(c); return item && WoodPoles.IsPole(item) && !item.currentActualBoat; }
        // A surface a dropped pack may rest on: not an item, unless nailed (or the one given, just un-nailed).
        private static bool Floor(Collider c, ShipItem also)
        {
            var body = c.GetComponentInParent<ItemRigidbody>();
            return !body || Nailed(c) || also && body.GetShipItem() == also;
        }
        // Walk copies sit apart from the world; a ray in the world never ties to one.
        private static bool OnWalkCopy(Transform t) { for (; t; t = t.parent) if (t.CompareTag("WalkColBoat")) return true; return false; }
        // A sail's invisible volumes (its wind shadow box) never block a corner; its visible parts and every item or control do.
        private static bool Visible(Collider c) { var r = c.GetComponent<Renderer>(); return r && r.enabled; }
        private static bool SailPart(Collider c) => c.GetComponentInParent<Sail>() || c.GetComponentInParent<SailConnections>();
        // Sails and anything the player handles (items, doors, hatches, winches, the helm) are not fixed parts.
        private static bool Moving(Collider c) => c.GetComponentInParent<GoPointerButton>() || c.GetComponentInParent<ItemRigidbody>() || SailPart(c);
        // A hatch's or door's box on the walk copy, which the game moves with it.
        private static bool MovingWalk(Transform frame, Transform t)
        {
            var ship = frame.parent ? frame.parent : frame;
            foreach (var hatch in ship.GetComponentsInChildren<GPButtonTrapdoor>(true)) { var w = Fields.Get<Transform>(hatch, "walkCol"); if (w && t.IsChildOf(w)) return true; }
            foreach (var door in ship.GetComponentsInChildren<GPButtonHouseDoor>(true)) if (door.walkCol && t.IsChildOf(door.walkCol)) return true;
            return false;
        }
        // A stay: the game's Mast that carries only staysails, not a Bermuda mast (the test Swappable Staysail uses).
        internal static bool IsStay(Mast mast)
        {
            if (!mast || !mast.onlyStaysails) return false;
            var option = mast.GetComponent<BoatPartOption>();
            var id = (option && !string.IsNullOrEmpty(option.optionName) ? option.optionName : mast.name).ToLowerInvariant().Replace('_', ' ');
            return !(id.Contains("bermuda") && id.Contains("mast") && !id.Contains("stay"));
        }
        // A collider of one of her own stays in the world (on her body, the ship whose boarding frame this is).
        private static bool OwnStay(Collider c, Transform frame)
        {
            var mast = Up<Mast>(c.transform); var body = Up<Rigidbody>(frame);
            return IsStay(mast) && body && c.attachedRigidbody == body;
        }
        // A collider on the walk copy of one of her stays (the game links each mast and stay to its walk copy).
        private static bool WalkStay(Transform frame, Transform t)
        {
            var ship = Up<PurchasableBoat>(frame); if (!ship) return false;
            foreach (var mast in ship.GetComponentsInChildren<Mast>(true)) if (mast.walkColMast && t.IsChildOf(mast.walkColMast) && IsStay(mast)) return true;
            return false;
        }
        // The point on a capsule's axis (its rope line, for a stay) nearest the ray.
        internal static Vector3 OnAxis(CapsuleCollider c, Ray ray)
        {
            var t = c.transform; var axis = c.direction == 0 ? Vector3.right : c.direction == 1 ? Vector3.up : Vector3.forward;
            float half = Mathf.Max(0, c.height / 2 - c.radius);
            Vector3 a = t.TransformPoint(c.center - axis * half), b = t.TransformPoint(c.center + axis * half), u = b - a, v = ray.direction, w = a - ray.origin;
            float uu = Vector3.Dot(u, u), uv = Vector3.Dot(u, v), vv = Vector3.Dot(v, v), uw = Vector3.Dot(u, w), vw = Vector3.Dot(v, w), den = uu * vv - uv * uv;
            float s = uu < 1e-8f ? 0 : den < 1e-8f ? Mathf.Clamp01(-uw / uu) : Mathf.Clamp01((uv * vw - vv * uw) / den);
            return a + u * s;
        }

        // R on a held pack: ties the corner the crosshair points at; the fourth completes the tarp's corners. Returns the notice
        // shown (null when none).
        internal static string Tie(ShipItem pack, Ray eye, Transform frame, Transform walk)
        {
            if (PendingPack != pack) Cancel(false);
            var corner = Find(eye, frame, walk, pack, out var refusal);
            if (corner == null) return refusal;
            if (Pending.Count > 0 && (Pending[0].onShip != corner.onShip || Pending[0].frame != corner.frame)) return Mixed;
            var at = corner.Position;
            if (Pending.Any(c => Vector3.Distance(c.Position, at) < MinApart)) return TooClose;
            // Two corners further apart than two sides can never share a tarp; the fourth corner checks the four sides themselves.
            if (Pending.Any(c => Vector3.Distance(c.Position, at) > 2 * MaxSide)) return TooLong;
            if (Pending.Count == 3 && Sides(Pending.Select(c => c.Position).Concat(new[] { at }).ToArray()).Any(s => s > MaxSide)) return TooLong;
            corner.knot = Knot(corner); Pending.Add(corner); PendingPack = pack;
            if (Pending.Count < 4) return "Corner " + Pending.Count + " of 4";
            var four = Pending.ToList(); Pending.Clear(); PendingPack = null;
            return Make(pack, four);
        }
        // The fourth corner makes the tarp and uses the pack up: no item stays hanging. A nailed pole under it too tall to hold it
        // up is named.
        internal static List<Corner> LastMade; internal static Tarp LastTarp;
        private static string Make(ShipItem pack, List<Corner> corners)
        {
            foreach (var c in corners) if (c.knot) UnityEngine.Object.Destroy(c.knot);
            LastMade = corners;
            var order = Order(corners.Select(c => c.Position).ToArray());
            var parent = corners[0].Land ? FloatingOriginManager.instance.transform : corners[0].frame;
            var locals = order.Select(i => parent.InverseTransformPoint(corners[i].Position)).ToArray();
            var turns = order.Select(i => Quaternion.Inverse(parent.rotation) * corners[i].Turn).ToArray();
            LastTarp = Build(parent, !corners[0].Land, locals, turns, pack.health);
            var hand = pack.held; if (hand) hand.DropItem();
            pack.DestroyItem();
            return LastTarp && LastTarp.layout.tallTops.Count > 0 ? TooTall : null;
        }
        // The corners' order around their middle (so the tarp never twists), and the four sides in that order.
        internal static int[] Order(Vector3[] p)
        {
            var mid = p.Aggregate(Vector3.zero, (a, b) => a + b) / p.Length;
            var n = Vector3.Cross(p[1] - p[0], p[2] - p[0]) + Vector3.Cross(p[2] - p[0], p[3] - p[0]); if (n.sqrMagnitude < 1e-8f) n = Vector3.up; if (n.y < 0) n = -n;
            var axis = Vector3.ProjectOnPlane(p[0] - mid, n).normalized; var other = Vector3.Cross(n, axis);
            return Enumerable.Range(0, p.Length).OrderBy(i => Mathf.Atan2(Vector3.Dot(p[i] - mid, other), Vector3.Dot(p[i] - mid, axis))).ToArray();
        }
        internal static Vector3[] Ordered(Vector3[] p) => Order(p).Select(i => p[i]).ToArray();

        // ---- The tarp ----
        // Points about every 0.5 m, 8 to 32 along a side; rope cells at most 6 m; cloth laced to the ropes about every 1 m; the
        // modelled sheet drops 9% of a cell's mean span in its middle; a free point may move at most 16% x 1.41 x its distance
        // to the nearest laced point (meant as a safety net; measured in game, gravity rests the cloth on it between lacings).
        internal const float Spacing = .5f, CellMax = 6, Lacing = 1, Drop = .09f, Cap = .16f, RopeWidth = .03f;
        internal const int MinPoints = 8, MaxPoints = 32;
        internal sealed class Layout
        {
            internal int cellsU, cellsV, segU, segV; internal Vector3[] points; internal bool[] pinned; internal float[] cap; internal Vector2[] uv; internal int[] triangles;
            internal List<Vector3[]> ropes = new List<Vector3[]>();
            // The free ends of the poles holding it up, and of those too tall to (Raise), in its frame.
            internal List<Vector3> heldTops = new List<Vector3>(), tallTops = new List<Vector3>();
            internal int Index(int i, int j) => j * (segU + 1) + i;
            // The sides along u and v (m).
            internal float lu, lv;
        }
        // The tarp's layout from its four corners in order (c0-c1 and c3-c2 along u, c0-c3 and c1-c2 along v) and its parent's
        // down. The sheet is bilinear through the corners: a straight line between matching points of opposite sides lies on it,
        // so the ropes across follow it whatever the corners' heights. Grid lines fall on every rope. `poles`: the nailed wood
        // poles near it (base, free end), in the same frame (Raise).
        internal static Layout Plan(Vector3[] c, Vector3 down, IList<(Vector3 a, Vector3 b)> poles = null)
        {
            float lu = Mathf.Max(Vector3.Distance(c[0], c[1]), Vector3.Distance(c[3], c[2])), lv = Mathf.Max(Vector3.Distance(c[0], c[3]), Vector3.Distance(c[1], c[2]));
            int nu = Cells(lu), nv = Cells(lv), mu = PerCell(lu, nu), mv = PerCell(lv, nv);
            var L = new Layout { cellsU = nu, cellsV = nv, segU = nu * mu, segV = nv * mv, lu = lu, lv = lv };
            int pu = L.segU + 1, pv = L.segV + 1, count = pu * pv;
            L.points = new Vector3[count]; L.pinned = new bool[count]; L.cap = new float[count]; L.uv = new Vector2[count];
            var flats = new Vector3[count]; var sag = new float[count];   // the sheet without its sag, and the sag
            float depth = Drop * (lu / nu + lv / nv) / 2;
            int ku = Mathf.Max(1, Mathf.RoundToInt(Lacing / (lu / L.segU))), kv = Mathf.Max(1, Mathf.RoundToInt(Lacing / (lv / L.segV)));
            Vector3 At(int i, int j) { float u = (float)i / L.segU, v = (float)j / L.segV; return Vector3.Lerp(Vector3.Lerp(c[0], c[1], u), Vector3.Lerp(c[3], c[2], u), v); }
            for (int j = 0; j < pv; j++) for (int i = 0; i < pu; i++)
            {
                int k = L.Index(i, j); float a = (float)(i % mu) / mu, b = (float)(j % mv) / mv;
                flats[k] = At(i, j); sag[k] = depth * 16 * a * (1 - a) * b * (1 - b); L.points[k] = flats[k] + down * sag[k];
                bool alongV = i % mu == 0, alongU = j % mv == 0;   // on a rope running along v (constant i) or along u (constant j)
                L.pinned[k] = alongV && (j % kv == 0 || alongU) || alongU && (i % ku == 0 || alongV);
                L.uv[k] = StripUV((float)i / L.segU, (float)j / L.segV, lu, lv);
            }
            if (poles != null && poles.Count > 0) Raise(L, c, down, depth, flats, sag, poles);
            var pins = Enumerable.Range(0, count).Where(k => L.pinned[k]).Select(k => L.points[k]).ToArray();
            for (int k = 0; k < count; k++) L.cap[k] = L.pinned[k] ? 0 : Cap * 1.41f * pins.Min(p => Vector3.Distance(p, L.points[k]));
            // A rope along each rope line, through its points: one straight piece, or one per straight run where it bends over a pole.
            for (int i = 0; i <= nu; i++) L.ropes.Add(Rope(L, Enumerable.Range(0, pv).Select(j => L.Index(i * mu, j))));
            for (int j = 0; j <= nv; j++) L.ropes.Add(Rope(L, Enumerable.Range(0, pu).Select(i => L.Index(i, j * mv))));
            var t = new List<int>();
            for (int j = 0; j < L.segV; j++) for (int i = 0; i < L.segU; i++)
            { int a = L.Index(i, j), b = L.Index(i + 1, j), d = L.Index(i, j + 1), e = L.Index(i + 1, j + 1); t.AddRange(new[] { a, d, b, b, d, e }); }
            L.triangles = t.ToArray();
            return L;
        }
        // A rope line's points, leaving out those where it runs straight on (its two ends kept).
        private static Vector3[] Rope(Layout L, IEnumerable<int> line)
        {
            var p = line.Select(k => L.points[k]).ToList(); var kept = new List<Vector3> { p[0] };
            for (int n = 1; n < p.Count - 1; n++) if (Vector3.Angle(p[n] - kept[kept.Count - 1], p[n + 1] - p[n]) > .5f) kept.Add(p[n]);
            kept.Add(p[p.Count - 1]); return kept.ToArray();
        }
        // Nailed wood poles hold it up (user, 2026-10-12), the whole pole wherever it is under the canvas: a standing pole by its
        // top, a lying or leaning one along its length (a ridge). Each pole is taken as points every 10 cm. A point touches the
        // canvas inside the four sides, or up to 15 cm outside one (under its rope), when it is no lower than the canvas can hang
        // there (its sag and its cap; at a side, no lower than its rope). Within 0.5 m of a side it holds that side up: the side's
        // rope runs from its corners over the pole, over its point nearest the side's line there (a standing pole's top, a lying
        // pole's end), so none of it is left outside the canvas. A pole that crosses a side seen from above holds that side up
        // where it crosses it (the user's picture, 2026-10-12: the edge was pulled in along the pole): the rope stays on the
        // side's line and rests on the pole's highest point within 5 cm of it, and the pole's points near that side hold the
        // canvas as further in. Further in it lifts the canvas with the sides staying. No slope over 45 degrees (the user's
        // limit): a point lifts the canvas over the plain sheet by at most its distance to the corners and to the sides that
        // stay (not a side the pole holds up where it crosses it, nor at a side its own); a pole with any point higher holds
        // nothing (tallTops). The canvas rests on the pole (the sheet's point nearest each touching point fixed 7 cm over its
        // line, the highest first) and runs in straight slopes down to the sides (the highest where several meet); its sag fades
        // to nothing at a pole, and the ropes across rise with it.
        internal const float EdgeBand = .5f, EdgeOutside = .15f, PoleCap = .07f, PoleStep = .1f, RopeOn = .05f;   // the canvas 7 cm over a pole's line: 3 cm over its top, room for the cloth beside it to dip
        private static void Raise(Layout L, Vector3[] c, Vector3 down, float depth, Vector3[] flats, float[] sag, IList<(Vector3 a, Vector3 b)> poles)
        {
            var up = -down.normalized; var e1 = Vector3.ProjectOnPlane(c[1] - c[0], up).normalized; var e2 = Vector3.Cross(up, e1);
            Vector2 P2(Vector3 x) => new Vector2(Vector3.Dot(x, e1), Vector3.Dot(x, e2));   // seen from above
            var q = c.Select(P2).ToArray();
            Vector2 B(float u, float v) => Vector2.LerpUnclamped(Vector2.LerpUnclamped(q[0], q[1], u), Vector2.LerpUnclamped(q[3], q[2], u), v);
            Vector3 At(float u, float v) => Vector3.Lerp(Vector3.Lerp(c[0], c[1], u), Vector3.Lerp(c[3], c[2], u), v);
            float ToSide(Vector2 x, Vector2 a, Vector2 b) { var ab = b - a; float f = Mathf.Clamp01(Vector2.Dot(x - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-8f)); return (a + ab * f - x).magnitude; }
            float[] ToSides(Vector2 x) => new[] { ToSide(x, q[0], q[1]), ToSide(x, q[1], q[2]), ToSide(x, q[2], q[3]), ToSide(x, q[3], q[0]) };
            // The sheet's point a touching point fixes: the nearest, never a corner; inside, never one on a side.
            int Vertex(float u, float v, int side)
            {
                int i = Mathf.RoundToInt(u * L.segU), j = Mathf.RoundToInt(v * L.segV);
                if (side != 1 && side != 3) i = Mathf.Clamp(i, 1, L.segU - 1);
                if (side != 0 && side != 2) j = Mathf.Clamp(j, 1, L.segV - 1);
                return L.Index(i, j);
            }
            var laced = Enumerable.Range(0, L.points.Length).Where(k => L.pinned[k]).Select(k => L.points[k]).ToArray();
            // Sides: 0 c0-c1 (v 0), 1 c1-c2 (u 1), 2 c2-c3 (v 1), 3 c3-c0 (u 0); -1 inside.
            var held = new List<(float u, float v, float h, Vector3 at, int side)>();   // every touching point: it lifts the canvas around it
            var fixes = new List<(float h, Vector3 at, int k)>();   // the sheet's points fixed on the poles
            foreach (var pole in poles)
            {
                var points = new List<(float u, float v, float h, Vector3 at, int side)>(); var pins = new List<(float h, Vector3 at, int k)>(); bool steep = false;
                // The sides it crosses, no lower than their rope: the rope rests on its highest point within 5 cm of the side's line,
                // kept on that line (a standing pole leaning over a side: its top; a lying one: where it crosses).
                var raised = new bool[4];
                for (int k = 0; k < 4; k++)
                {
                    Vector2 a2 = P2(pole.a), r = P2(pole.b) - a2, s = q[(k + 1) % 4] - q[k];
                    if (!Crossing(a2, a2 + r, q[k], q[(k + 1) % 4], out float t, out _)) continue;
                    float slope = Mathf.Abs(r.x * s.y - r.y * s.x) / s.magnitude, dt = slope > 1e-6f ? RopeOn / slope : 1;   // its distance from the line grows by `slope` along it
                    float t0 = Mathf.Clamp01(t - dt), t1 = Mathf.Clamp01(t + dt);
                    var p = Vector3.Lerp(pole.a, pole.b, Vector3.Dot(pole.b - pole.a, up) >= 0 ? t1 : t0);
                    float f = Mathf.Clamp01(Vector2.Dot(P2(p) - q[k], s) / s.sqrMagnitude);
                    float su = k == 0 ? f : k == 1 ? 1 : k == 2 ? 1 - f : 0, sv = k == 0 ? 0 : k == 1 ? f : k == 2 ? 1 : 1 - f;
                    float lift = Vector3.Dot(p - At(su, sv), up);
                    if (lift < 0) continue;   // under the side's rope (a rope never hangs)
                    raised[k] = true; var on = At(su, sv) + up * lift;
                    points.Add((su, sv, lift + PoleCap, on, k)); pins.Add((lift + PoleCap, on, Vertex(su, sv, k)));
                }
                // How high a point may lift the canvas (45 degrees): its distance to the corners and to the sides that stay; `own` left out.
                float Room(Vector2 x, float[] to, int own) { float m = q.Min(z => (z - x).magnitude); for (int k = 0; k < 4; k++) if (k != own && !raised[k]) m = Mathf.Min(m, to[k]); return m; }
                foreach (var z in points) { var x = P2(z.at); if (z.h - PoleCap > Room(x, ToSides(x), z.side)) steep = true; }
                var byEdge = new List<(float u, float v, float h, Vector3 at, int side, float outward)>();   // near a side it does not cross
                int n = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(pole.a, pole.b) / PoleStep));
                for (int st = 0; st <= n; st++)
                {
                    // Where this point is on the sheet (u, v), seen from above (Newton's method on the bilinear sheet).
                    var p = Vector3.Lerp(pole.a, pole.b, (float)st / n); var x = P2(p); float u = .5f, v = .5f;
                    for (int it = 0; it < 20; it++)
                    {
                        var r = x - B(u, v); var du = Vector2.LerpUnclamped(q[1] - q[0], q[2] - q[3], v); var dv = Vector2.LerpUnclamped(q[3], q[2], u) - Vector2.LerpUnclamped(q[0], q[1], u);
                        float det = du.x * dv.y - du.y * dv.x; if (Mathf.Abs(det) < 1e-8f) break;
                        u += (r.x * dv.y - r.y * dv.x) / det; v += (du.x * r.y - du.y * r.x) / det;
                    }
                    if (float.IsNaN(u) || float.IsNaN(v)) continue;
                    var to = ToSides(x);
                    int side = 0; for (int k = 1; k < 4; k++) if (to[k] < to[side]) side = k;
                    bool inside = u >= 0 && u <= 1 && v >= 0 && v <= 1;
                    if (!inside && to[side] > EdgeOutside) continue;   // beside it
                    float cu = Mathf.Clamp01(u), cv = Mathf.Clamp01(v), fa = cu * L.cellsU % 1, fb = cv * L.cellsV % 1, sagHere = depth * 16 * fa * (1 - fa) * fb * (1 - fb);
                    float lift = Vector3.Dot(p - At(cu, cv), up);
                    var model = At(cu, cv) - up * sagHere; float hang = Cap * 1.41f * laced.Min(z => Vector3.Distance(z, model));
                    if (lift + PoleCap < -sagHere - hang) continue;   // under the canvas, not touching it
                    if (raised[side] && to[side] < EdgeBand)
                    {
                        // By a side it holds up where it crosses it: inside, on the canvas as further in, never fixing that side's own
                        // points (the crossing does); outside, past the crossing.
                        if (!inside) continue;
                        if (lift > Room(x, to, -1)) steep = true;
                        points.Add((cu, cv, lift + PoleCap, p, -1));
                        int i = Mathf.RoundToInt(cu * L.segU), j = Mathf.RoundToInt(cv * L.segV);
                        if (!(side == 0 && j == 0 || side == 1 && i == L.segU || side == 2 && j == L.segV || side == 3 && i == 0)) pins.Add((lift + PoleCap, p, Vertex(cu, cv, -1)));
                        continue;
                    }
                    if (inside && to[side] >= EdgeBand) { if (lift > Room(x, to, -1)) steep = true; points.Add((cu, cv, lift + PoleCap, p, -1)); pins.Add((lift + PoleCap, p, Vertex(cu, cv, -1))); continue; }
                    if (lift < 0) continue;   // under the side's rope (a rope never hangs)
                    if (lift > Room(x, to, side)) steep = true;
                    if (side == 0) cv = 0; else if (side == 1) cu = 1; else if (side == 2) cv = 1; else cu = 0;
                    points.Add((cu, cv, lift + PoleCap, p, side)); byEdge.Add((cu, cv, lift + PoleCap, p, side, inside ? -to[side] : to[side]));
                }
                // Near a side it does not cross, each of the side's points it holds up rests on the pole's point nearest the side's
                // line (of those within 10 cm of it, the highest).
                foreach (var g in byEdge.GroupBy(z => Vertex(z.u, z.v, z.side)))
                {
                    float most = g.Max(z => z.outward);
                    var on = g.Where(z => z.outward >= most - PoleStep).OrderByDescending(z => z.h).ThenByDescending(z => z.outward).First();
                    pins.Add((on.h, on.at, g.Key));
                }
                if (steep) { L.tallTops.Add(pole.b); continue; }
                if (points.Count == 0) continue;
                held.AddRange(points); fixes.AddRange(pins); L.heldTops.Add(pole.b);
            }
            if (held.Count == 0) return;
            for (int k = 0; k < L.points.Length; k++)
            {
                float u = (float)(k % (L.segU + 1)) / L.segU, v = (float)(k / (L.segU + 1)) / L.segV, lift = 0, near = 0;
                foreach (var h in held)
                {
                    if (h.h <= 0) continue;
                    // How far on from the point through this one the sheet's edge is (in u, v); its share of the lift falls in a
                    // straight line from 1 at the point to 0 at the edge (at a side, along it to its corners).
                    float du = u - h.u, dv = v - h.v, reach = float.MaxValue;
                    if (du > 1e-6f) reach = Mathf.Min(reach, (1 - h.u) / du); else if (du < -1e-6f) reach = Mathf.Min(reach, h.u / -du);
                    if (dv > 1e-6f) reach = Mathf.Min(reach, (1 - h.v) / dv); else if (dv < -1e-6f) reach = Mathf.Min(reach, h.v / -dv);
                    float share = reach == float.MaxValue ? 1 : Mathf.Clamp01(1 - 1 / reach);
                    lift = Mathf.Max(lift, h.h * share); near = Mathf.Max(near, share);
                }
                L.points[k] = flats[k] + up * lift - up * sag[k] * (1 - near);
            }
            // Each touching point fixes the sheet's point nearest it (never a corner; inside, never one on a side), the highest first.
            var claimed = new HashSet<int>();
            foreach (var f in fixes.OrderByDescending(x => x.h))
            {
                if (!claimed.Add(f.k)) continue;
                L.points[f.k] = f.at + up * PoleCap; L.pinned[f.k] = true;
            }
        }
        // Where segment a-b crosses segment c-d seen from above: t along a-b, f along c-d (parallel: none; a standing pole seen
        // from above is a point).
        private static bool Crossing(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out float t, out float f)
        {
            Vector2 r = b - a, s = d - c, w = c - a; float den = r.x * s.y - r.y * s.x; t = f = 0;
            if (Mathf.Abs(den) < 1e-8f) return false;
            t = (w.x * s.y - w.y * s.x) / den; f = (w.x * r.y - w.y * r.x) / den;
            return t >= 0 && t <= 1 && f >= 0 && f <= 1;
        }
        // The canvas strip stretched once over the sheet (a, b: 0-1 along its u and v sides), its long side (the texture's v)
        // along the tarp's longer side.
        internal static Vector2 StripUV(float a, float b, float lu, float lv) => lu > lv ? new Vector2(b, a) : new Vector2(a, b);
        private static int Cells(float length) => Mathf.Max(1, Mathf.CeilToInt(length / CellMax - 1e-4f));
        private static int PerCell(float length, int cells) => Mathf.Clamp(Mathf.RoundToInt(length / Spacing / cells), Mathf.CeilToInt((MinPoints - 1) / (float)cells), (MaxPoints - 1) / cells);

        // The tarp as the game's cloth roofs are made: a Unity Cloth with the small Dhow roof's settings and gravity, driven by
        // the game's WindClothSimple (on a ship her apparent wind, wind minus her speed; on land the plain wind), its free points
        // capped, its laced points fixed; the Dhow roof's double-sided cloth; straight ropes in the game's rope material (no
        // collider: players walk through it); a rope knot at each corner. Parented to the ship's boarding frame or the
        // shifting world, so it moves, switches off and jumps with her.
        internal static Tarp Build(Transform parent, bool onShip, Vector3[] corners, Quaternion[] turns, float health = FullHealth, Transform placedLike = null, ShipItem leaving = null)
        {
            var root = new GameObject("canvas tarp"); root.transform.SetParent(parent, false);
            // In the place of a tarp it replaces (a world shift moves a land tarp itself, not the shifting world).
            if (placedLike) { root.transform.localPosition = placedLike.localPosition; root.transform.localRotation = placedLike.localRotation; }
            var tarp = root.AddComponent<Tarp>(); tarp.onShip = onShip; tarp.corners = corners; tarp.turns = turns;
            tarp.startHealth = Mathf.Clamp(float.IsNaN(health) ? FullHealth : health, 0, FullHealth); tarp.startClock = Now; tarp.torn = tarp.startHealth <= 0;
            // Torn: its holes in its own texture (the whole cloth, mapped once over the sheet).
            bool cutout = tarp.torn && FindTornShader();
            // Nailed wood poles under it hold it up (Raise); `leaving`: one about to be removed.
            var layout = Plan(corners, parent.InverseTransformDirection(Vector3.down), PoleLines(root.transform, onShip, corners, leaving)); tarp.layout = layout;
            if (cutout) for (int k = 0; k < layout.uv.Length; k++) layout.uv[k] = new Vector2((float)(k % (layout.segU + 1)) / layout.segU, (float)(k / (layout.segU + 1)) / layout.segV);
            var sheet = new GameObject("canvas"); sheet.transform.SetParent(root.transform, false);
            var mesh = new Mesh { name = "canvas tarp" };
            mesh.vertices = layout.points; mesh.uv = layout.uv; mesh.triangles = layout.triangles; mesh.RecalculateNormals(); mesh.RecalculateBounds();
            // Its own copy of the cloth, to fade as it wears.
            tarp.material = cutout ? TornMaterial(layout, Sides(corners), out tarp.texture) : WholeMaterial(); Fade(tarp, tarp.Health);
            var skin = sheet.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh; skin.sharedMaterial = tarp.material;
            var cloth = sheet.AddComponent<Cloth>(); tarp.cloth = cloth;
            cloth.damping = .25f; cloth.stretchingStiffness = 1; cloth.bendingStiffness = 1; cloth.useTethers = false; cloth.useGravity = true;
            cloth.clothSolverFrequency = 60; cloth.worldVelocityScale = 0; cloth.worldAccelerationScale = 0;
            tarp.Caps();   // now, or once she is switched on (a cloth switched off has no points yet)
            var wind = sheet.AddComponent<WindClothSimple>(); wind.staticMultiplier = 1; wind.shipRigidbody = onShip ? Up<Rigidbody>(parent) : null;
            // Each rope drawn by the game's own rope code, as its mooring lines and rigging are (user, 2026-10-06): with the game's
            // "3D ropes" option off the line as before (3 cm, the rope material, tiled), with it on the game's 3D rope; hidden
            // beyond 250 m as the game's ropes are. Its slack is 0, so it runs straight; the code turns its two ends to face each
            // other and touches nothing else.
            foreach (var r in layout.ropes.SelectMany(l => Enumerable.Range(0, l.Length - 1).Select(n => new[] { l[n], l[n + 1] })))   // bent over a pole: a piece per straight run
            {
                var o = new GameObject("canvas rope"); o.transform.SetParent(root.transform, false); o.transform.localPosition = r[0];
                var end = new GameObject("canvas rope end"); end.transform.SetParent(root.transform, false); end.transform.localPosition = r[1];
                var line = o.AddComponent<LineRenderer>(); line.useWorldSpace = true; line.positionCount = 2; line.SetPositions(new[] { o.transform.position, end.transform.position });
                line.startWidth = line.endWidth = RopeWidth; line.sharedMaterial = RopeMaterial; line.textureMode = LineTextureMode.Tile;
                var rope = o.AddComponent<RopeEffect>(); rope.attachment = end.transform; rope.ropeWidth = RopeWidth; rope.currentRopeLength = 0;
            }
            tarp.knots = new GameObject[4];
            for (int i = 0; i < 4; i++)
            {
                // The game's mooring knot, 0.15 m (user, 2026-10-06). The button's own object keeps the knot's mesh unseen (the
                // game's outline is made from it) and a child draws it; PackLook points the outline at the child.
                var k = new GameObject("canvas knot"); k.transform.SetParent(root.transform, false); k.transform.localPosition = corners[i]; k.transform.localRotation = turns[i];
                k.AddComponent<MeshFilter>().sharedMesh = KnotMesh; var unseen = k.AddComponent<MeshRenderer>(); unseen.sharedMaterial = KnotMaterial; unseen.enabled = false;
                KnotLook(k.transform, KnotSize, KnotMaterial, KnotLookName); k.AddComponent<PackLook>().child = KnotLookName; tarp.knots[i] = k;
                // The pointer clicks it (its ray hits triggers): unties the tarp.
                var touch = k.AddComponent<SphereCollider>(); touch.isTrigger = true; touch.radius = .15f; touch.center = new Vector3(0, 0, -KnotSize / 2);
                var button = k.AddComponent<TarpKnot>(); button.tarp = tarp; button.corner = i; button.lookText = TarpKnot.UntieText; button.description = "";
            }
            tarp.SetCover(layout.points); RecheckUnder(tarp);
            Plugin.Log?.LogInfo("Canvas tarp " + (tarp.torn ? "torn" : "tied") + " " + (onShip ? "on " + parent.name : "on land") + ": sides " + string.Join(" ", Sides(corners).Select(s => s.ToString("F1"))) + " m, "
                + layout.points.Length + " points, " + layout.pinned.Count(p => p) + " laced, " + layout.ropes.Count + " ropes" + (layout.heldTops.Count > 0 ? ", held up by " + layout.heldTops.Count + " pole(s)" : "") + ", health " + tarp.startHealth.ToString("F1") + ".");
            return tarp;
        }

        // ---- Wear ----
        // Worn out: the tarp tears where it hangs (rebuilt with its holes); it covers nothing and stays tied until untied.
        internal static Tarp Tear(Tarp old)
        {
            Tarp.All.Remove(old);
            var torn = Build(old.transform.parent, old.onShip, old.corners, old.turns, 0, old.transform);
            if (LastTarp == old) LastTarp = torn;
            UnityEngine.Object.Destroy(old.gameObject);
            return torn;
        }

        // ---- Wood poles holding tarps up (user, 2026-10-12; POLE_SUPPORT_DRAFT.md) ----
        internal const string TooTall = "Pole too tall for this tarp";
        // The nailed wood poles that could hold up a tarp, each from its base to its free end, in its frame (`root`): aboard her
        // (their item on this boarding frame; read from its physics copy on her walk copy, as UntieFrom does) or on land. A pole
        // a corner is tied to (its knot within 5 cm) never holds it up; `leaving`: a pole about to be removed.
        internal static List<(Vector3 a, Vector3 b)> PoleLines(Transform root, bool onShip, Vector3[] corners, ShipItem leaving = null)
        {
            var lines = new List<(Vector3 a, Vector3 b)>(); if (!root || !WoodPoles.Registered) return lines;
            var frame = onShip ? root.parent : null; var walk = frame ? Link(frame)?.walkCollider : null;
            if (onShip && !walk) return lines;
            WoodPoles.Live.RemoveWhere(x => !x);
            foreach (var pole in WoodPoles.Live) { if (pole == leaving) continue; var line = PoleLine(pole, root, frame, walk, corners); if (line != null) lines.Add(line.Value); }
            return lines;
        }
        private static (Vector3 a, Vector3 b)? PoleLine(ShipItem pole, Transform root, Transform frame, Transform walk, Vector3[] corners)
        {
            bool onShip = frame;
            if (!pole || !pole.sold || !pole.nailed || !pole.itemRigidbodyC || (onShip ? pole.currentActualBoat != frame : pole.currentActualBoat)) return null;
            var body = pole.itemRigidbodyC.transform;
            if (corners != null)
            {
                var colliders = body.GetComponentsInChildren<Collider>(true).Where(x => x && !x.isTrigger).ToArray();
                foreach (var k in corners)
                {
                    var p = root.TransformPoint(k); if (onShip) p = walk.TransformPoint(frame.InverseTransformPoint(p));
                    if (colliders.Any(x => (Closest(x, p) - p).sqrMagnitude <= OnItem * OnItem)) return null;
                }
            }
            Vector3 Mapped(Vector3 p) => root.InverseTransformPoint(onShip ? frame.TransformPoint(walk.InverseTransformPoint(p)) : p);
            return (Mapped(body.position), Mapped(body.position - body.forward * WoodPoles.Length(pole)));   // the pole runs along -z from where it is nailed
        }
        // A tarp built again where it hangs, with its wear, holding up the nailed poles under it now (as a repair rebuilds it; it
        // has no weight and no solid collider, so the boat feels nothing).
        internal static Tarp Reshape(Tarp old, ShipItem leaving = null)
        {
            Tarp.All.Remove(old);
            var fresh = Build(old.transform.parent, old.onShip, old.corners, old.turns, old.Health, old.transform, leaving);
            if (LastTarp == old) LastTarp = fresh;
            UnityEngine.Object.Destroy(old.gameObject);
            return fresh;
        }
        // A wood pole nailed, un-nailed or about to be removed (`leaving`): every tarp on her (or on land) whose poles holding it
        // up change is built again at once (the user: it jumps to its new shape). Nailed under a tarp but too tall: the notice.
        internal static int PoleMoved(ShipItem pole, bool leaving)
        {
            if (!Registered || !WoodPoles.Registered || !pole) return 0;
            int reshaped = 0; bool tall = false;
            foreach (var tarp in Tarp.All.ToList())
            {
                if (!tarp || tarp.corners == null || tarp.layout == null || !tarp.transform.parent) continue;
                var frame = tarp.onShip ? tarp.transform.parent : null; var walk = frame ? Link(frame)?.walkCollider : null;
                if (tarp.onShip ? !walk || pole.currentActualBoat != frame : pole.currentActualBoat) continue;
                var plan = Plan(tarp.corners, tarp.transform.parent.InverseTransformDirection(Vector3.down), PoleLines(tarp.transform, tarp.onShip, tarp.corners, leaving ? pole : null));
                var mine = leaving ? null : PoleLine(pole, tarp.transform, frame, walk, null)?.b;
                if (mine != null && plan.tallTops.Any(t => (t - mine.Value).sqrMagnitude < 1e-4f)) tall = true;
                if (plan.heldTops.Count == tarp.layout.heldTops.Count && plan.heldTops.All(t => tarp.layout.heldTops.Any(o => (o - t).sqrMagnitude < 1e-4f))) continue;
                Reshape(tarp, leaving ? pole : null); reshaped++;
            }
            if (tall) Loans.Notify(TooTall);
            if (reshaped > 0 || tall) Plugin.Log?.LogInfo("Wood pole " + (leaving ? "removed" : pole.nailed ? "nailed" : "un-nailed") + ": " + reshaped + " canvas tarp(s) reshaped" + (tall ? "; too tall for a tarp over it" : "") + ".");
            return reshaped;
        }

        // ---- Repair ----
        // R on a corner knot with a bought pack in hand (user, 2026-10-09): the tarp is rebuilt where it hangs (as a tear
        // rebuilds it) with its health plus the pack's, at most 100: no holes (a pack is never at 0), the colour of that
        // health, cover again. The pack is always used up; nothing comes back from the old canvas.
        internal const string Repaired = "Canvas repaired";
        internal static Tarp LastRepaired;
        internal static string Repair(Tarp old, ShipItem pack)
        {
            if (!old || !pack || !Registered) return null;
            float was = old.Health, added = pack.health, health = Rules.RepairedHealth(was, added);
            Cancel(false);
            Tarp.All.Remove(old);
            var fresh = Build(old.transform.parent, old.onShip, old.corners, old.turns, health, old.transform);
            if (LastTarp == old) LastTarp = fresh;
            LastRepaired = fresh;
            UnityEngine.Object.Destroy(old.gameObject);
            var hand = pack.held; if (hand) hand.DropItem();
            pack.DestroyItem();
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUISound(UISounds.itemPickup, .55f, 1);
            Plugin.Log?.LogInfo("Canvas tarp repaired: health " + was.ToString("F1") + " + pack " + added.ToString("F1") + " = " + health.ToString("F1") + ".");
            return Repaired;
        }
        // The corner knot the game's pointer points at (its own private choice of button), if any.
        internal static TarpKnot PointedKnot(GoPointer pointer)
        {
            if (!pointer) return null;
            try { return Fields.Get<GoPointerButton>(pointer, "pointedAtButton") as TarpKnot; }
            catch (Exception e) { Plugin.Log?.LogWarning("Canvas: the pointed knot not read; repair is off. " + e.Message); return null; }
        }
        // Where a torn tarp's holes go (user, 2026-10-05: small frayed holes scattered round the middle section, the edges
        // mostly intact), seeded by its size, so the same after a load: 2 to 12 small holes (one per 3 m²; 0.15-0.3 m, up to 1.5x
        // on big tarps) with ragged outlines (waves of 3, 5 and 9 round a circle), scattered over the middle 60% of each side,
        // their widest reach (2.1x) 0.15 m clear of the edges (smaller holes on a narrow tarp) and apart from each other where
        // there is room (8 tries); 0-2 short crooked rips out of a hole (0.25-0.6 m from its middle), stopping 0.25 m short of
        // the edges.
        internal sealed class Tears
        {
            internal readonly List<(Vector2 at, float r, float p1, float p2, float p3)> holes = new List<(Vector2 at, float r, float p1, float p2, float p3)>();
            internal readonly List<Vector2[]> rips = new List<Vector2[]>(); internal System.Random rnd;
            // A hole's outline (m from its middle) at angle a.
            internal static float Outline((Vector2 at, float r, float p1, float p2, float p3) h, float a) => h.r * (1 + .3f * Mathf.Sin(3 * a + h.p1) + .18f * Mathf.Sin(5 * a + h.p2) + .1f * Mathf.Sin(9 * a + h.p3));
        }
        internal static Tears PlanTears(float lu, float lv, float[] sides)
        {
            int seed = 17; foreach (var s in sides) seed = unchecked(seed * 31 + Mathf.RoundToInt(s * 10));
            var T = new Tears { rnd = new System.Random(seed) }; var rnd = T.rnd; float R() => (float)rnd.NextDouble();
            float area = lu * lv, grow = Mathf.Clamp(Mathf.Sqrt(area) / 3.5f, 1, 1.5f);
            for (int n = Mathf.Clamp(Mathf.RoundToInt(area / 3), 2, 12); n > 0; n--)
            {
                float r = Mathf.Clamp((.15f + .15f * R()) * grow, .02f, (Mathf.Min(lu, lv) / 2 - .15f) / 2.1f), clear = 2.1f * r + .15f;
                float U(float side) => side > 2 * clear ? Mathf.Clamp(side * (.2f + .6f * R()), clear, side - clear) : side / 2;
                var at = new Vector2(U(lu), U(lv));
                for (int tries = 1; tries < 8 && T.holes.Any(o => (o.at - at).magnitude < 1.3f * (o.r + r)); tries++) at = new Vector2(U(lu), U(lv));
                T.holes.Add((at, r, 6.283f * R(), 6.283f * R(), 6.283f * R()));
            }
            for (int n = rnd.Next(3); n > 0; n--)
            {
                var p = T.holes[rnd.Next(T.holes.Count)].at; float dir = 6.283f * R(); var line = new List<Vector2> { p };
                for (int s = 2 + rnd.Next(2); s > 0; s--)
                {
                    dir += (R() - .5f) * 1.2f; var next = p + new Vector2(Mathf.Cos(dir), Mathf.Sin(dir)) * (.12f + .08f * R());
                    if (next.x < .25f || next.x > lu - .25f || next.y < .25f || next.y > lv - .25f) break;
                    line.Add(p = next);
                }
                if (line.Count > 1) T.rips.Add(line.ToArray());
            }
            return T;
        }
        // ---- The torn look from a texture (user, 2026-10-05) ----
        // With the game's double-sided cutout cloth shader (the Dhow roof's shader with holes; the game's bamboo wind chimes
        // use it) a torn tarp keeps its whole cloth and gets its own texture: the canvas as it tiles on a whole tarp (every 3 m),
        // drawn once over the sheet, its holes cut out (alpha 0) where PlanTears puts them, fringed at the pixel: a jagged rim
        // of loose fibres, a thread or two left across some holes, thin rips narrowing to nothing, a few small nicks at the
        // edges. Without that shader a torn tarp shows no holes (it is still faded and covers nothing).
        internal const string TornShaderName = "Ciconia Studio/Double Sided/Transparent/Diffuse Bump Cutout";
        internal const float TornPixelsPerMetre = 128; internal const int TornTextureMax = 1024;
        internal static Shader TornShader; private static Material tornKeeper;   // the keeper keeps the shader loaded
        internal static bool FindTornShader()
        {
            if (TornShader) return true;
            TornShader = Resources.FindObjectsOfTypeAll<Shader>().FirstOrDefault(s => s && s.name == TornShaderName) ?? Shader.Find(TornShaderName);
            if (!TornShader) return false;
            tornKeeper = new Material(TornShader) { name = "canvas torn cloth" };
            // Matte, as cloth (user, 2026-10-06): the shader's defaults shine (glossiness 0.5, white specular 0.2); tarps (whole
            // and torn) are made from this material.
            tornKeeper.SetFloat("_Glossiness", 0); tornKeeper.SetFloat("_SpecularIntensity", 0); tornKeeper.SetColor("_SpecColor", Color.black);
            Plugin.Log?.LogInfo("Canvas: torn tarps show their holes with " + TornShaderName + ".");
            return true;
        }
        internal static Material TornMaterial(Layout L, float[] sides, out Texture2D texture)
        {
            texture = TornTexture(L, sides);
            var m = new Material(tornKeeper) { name = "canvas tarp torn cloth", mainTexture = texture };
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", .5f);
            return m;
        }
        // A whole tarp's cloth: its own material (to fade), drawing the canvas strip once over the sheet with the same double-sided
        // cutout shader as a torn tarp (no holes: the strip is opaque), so whole and torn tarps light alike (user, 2026-10-06:
        // with the roof's opaque shader a whole tarp looked dark from above, a torn one bright). Without that shader, a copy of
        // the roof's material.
        internal static Material WholeMaterial()
        {
            if (FindTornShader())
            {
                var t = new Material(tornKeeper) { name = "canvas tarp cloth", mainTexture = Canvas };
                if (t.HasProperty("_Cutoff")) t.SetFloat("_Cutoff", .5f);
                return t;
            }
            return new Material(ClothMaterial) { name = "canvas tarp cloth", mainTexture = Canvas, mainTextureScale = Vector2.one, mainTextureOffset = Vector2.zero };
        }

        // ---- The canvas (user, 2026-10-06) ----
        // The roof's material draws from a sheet shared by the small Dhow's cloth parts (her lateen sail, her roof, plain grey);
        // the roof uses only its own box of it. A tarp shows one plain strip of the roof's canvas: the inside of the left of its
        // two strips, clear of the hems and the middle seam (10-44% across the box, 14-86% down it; measured: no line in it),
        // copied once through the GPU and stretched once over the sheet. The box is read from the roof's mesh, else 0.39.2's.
        internal static readonly Rect RoofBox039 = Rect.MinMaxRect(.6413f, .0022f, .9967f, .4071f);
        internal const float StripLeft = .10f, StripRight = .44f, StripTop = .14f, StripBottom = .86f;
        internal static Rect StripBox(Rect roof) => Rect.MinMaxRect(roof.xMin + StripLeft * roof.width, roof.yMax - StripBottom * roof.height,
            roof.xMin + StripRight * roof.width, roof.yMax - StripTop * roof.height);
        internal static Rect RoofBox(out string from)
        {
            from = "0.39.2's box";
            try
            {
                var mesh = Resources.FindObjectsOfTypeAll<Mesh>().FirstOrDefault(m => m && m.name == "roof_cloth" && m.isReadable && m.vertexCount > 0);
                if (!mesh) return RoofBox039;
                var uv = mesh.uv; if (uv == null || uv.Length == 0) return RoofBox039;
                var box = Rect.MinMaxRect(uv.Min(p => p.x), uv.Min(p => p.y), uv.Max(p => p.x), uv.Max(p => p.y));
                if (box.xMin < 0 || box.yMin < 0 || box.xMax > 1 || box.yMax > 1 || box.width < .05f || box.height < .05f) return RoofBox039;
                from = "the roof's mesh"; return box;
            }
            catch (Exception e) { Plugin.Log?.LogWarning("Canvas: the roof's box not read (" + e.Message + "); 0.39.2's is used."); return RoofBox039; }
        }
        private static Texture2D canvas;
        internal static Texture2D Canvas
        {
            get
            {
                if (canvas) return canvas;
                var src = ClothMaterial.mainTexture; var box = StripBox(RoofBox(out var from));
                int w = src ? Mathf.Clamp(Mathf.RoundToInt(box.width * src.width), 16, 2048) : 16, h = src ? Mathf.Clamp(Mathf.RoundToInt(box.height * src.height), 16, 2048) : 16;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "canvas tarp strip", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontUnloadUnusedAsset };
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32); var was = RenderTexture.active;
                try
                {
                    if (src) Graphics.Blit(src, rt, box.size, box.position);
                    RenderTexture.active = rt; if (!src) GL.Clear(false, true, Color.white);
                    tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                    var px = tex.GetPixels32(); for (int i = 0; i < px.Length; i++) px[i].a = 255;   // opaque under the cutout shader
                    tex.SetPixels32(px); tex.Apply(true, true);
                }
                finally { RenderTexture.active = was; RenderTexture.ReleaseTemporary(rt); }
                Plugin.Log?.LogInfo("Canvas: the strip " + box.xMin.ToString("F4") + "-" + box.xMax.ToString("F4") + " x " + box.yMin.ToString("F4") + "-" + box.yMax.ToString("F4")
                    + " of " + (src ? src.name : "no texture") + " (" + w + " x " + h + " px), from " + from + ".");
                return canvas = tex;
            }
        }
        internal static Texture2D TornTexture(Layout L, float[] sides)
        {
            float lu = L.lu, lv = L.lv;
            int w = Mathf.Clamp(Mathf.RoundToInt(lu * TornPixelsPerMetre), 64, TornTextureMax), h = Mathf.Clamp(Mathf.RoundToInt(lv * TornPixelsPerMetre), 64, TornTextureMax);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "canvas torn", wrapMode = TextureWrapMode.Clamp };
            // The canvas strip as on a whole tarp (stretched once, its long side along the longer side: StripUV), through the GPU
            // (the game's textures can't be read); this texture runs along the tarp's sides, so a turned strip is read across.
            bool turned = lu > lv; int bw = turned ? h : w, bh = turned ? w : h;
            var rt = RenderTexture.GetTemporary(bw, bh, 0, RenderTextureFormat.ARGB32); var was = RenderTexture.active;
            var read = new Texture2D(bw, bh, TextureFormat.RGBA32, false); Color32[] strip;
            try
            {
                Graphics.Blit(Canvas, rt); RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, bw, bh), 0, 0, false); strip = read.GetPixels32();
            }
            finally { RenderTexture.active = was; RenderTexture.ReleaseTemporary(rt); UnityEngine.Object.Destroy(read); }
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var c = turned ? strip[x * bw + y] : strip[y * w + x]; c.a = 255; px[y * w + x] = c; }
            var T = PlanTears(lu, lv, sides); var rnd = T.rnd; float R() => (float)rnd.NextDouble();
            float mx = lu / w, my = lv / h; int salt = rnd.Next();
            // A pixel's own grain (0-1), from its place and the tarp's seed.
            float Grain(int x, int y) { unchecked { uint k = (uint)(x * 73856093 ^ y * 19349663 ^ salt); k ^= k >> 13; k *= 0x5bd1e995; k ^= k >> 15; return (k & 0xffffff) / 16777216f; } }
            // The pixels of a box in metres, with their middles.
            IEnumerable<(int x, int y, Vector2 p)> Box(Vector2 lo, Vector2 hi)
            {
                for (int y = Mathf.Max(0, (int)(lo.y / my)); y <= Mathf.Min(h - 1, (int)(hi.y / my)); y++)
                    for (int x = Mathf.Max(0, (int)(lo.x / mx)); x <= Mathf.Min(w - 1, (int)(hi.x / mx)); x++) yield return (x, y, new Vector2((x + .5f) * mx, (y + .5f) * my));
            }
            foreach (var hole in T.holes)
            {
                // A jagged rim (finer waves on the outline) with loose fibres at the pixel, and 0-2 threads left across, 1 cm wide.
                float q4 = 6.283f * R(), q5 = 6.283f * R();
                var threads = Enumerable.Range(0, rnd.Next(3)).Select(_ => (o: hole.at + new Vector2(R() - .5f, R() - .5f) * hole.r, a: 3.1416f * R(), q: 6.283f * R())).ToList();
                float reach = 1.9f * hole.r;
                foreach (var (x, y, p) in Box(hole.at - Vector2.one * reach, hole.at + Vector2.one * reach))
                {
                    var d = p - hole.at; float a = Mathf.Atan2(d.y, d.x);
                    float s = d.magnitude / (Tears.Outline(hole, a) * (1 + .06f * Mathf.Sin(23 * a + q4) + .04f * Mathf.Sin(41 * a + q5)));
                    if (s > .9f + .18f * Grain(x, y)) continue;
                    if (threads.Any(t => { var along = new Vector2(Mathf.Cos(t.a), Mathf.Sin(t.a)); var off = p - t.o;
                        return Mathf.Abs(Vector2.Dot(off, new Vector2(-along.y, along.x)) + .01f * Mathf.Sin(40 * Vector2.Dot(off, along) + t.q)) < .005f; })) continue;
                    px[y * w + x].a = 0;
                }
            }
            foreach (var line in T.rips)
            {
                // Thin rips, 3 cm wide at the hole narrowing to nothing at their end, fibrous at the edges.
                float total = 0; for (int s = 0; s + 1 < line.Length; s++) total += (line[s + 1] - line[s]).magnitude;
                var lo = line.Aggregate(Vector2.Min) - Vector2.one * .03f; var hi = line.Aggregate(Vector2.Max) + Vector2.one * .03f;
                foreach (var (x, y, p) in Box(lo, hi))
                {
                    float best = float.MaxValue, at = 0, from = 0;
                    for (int s = 0; s + 1 < line.Length; s++)
                    {
                        Vector2 a = line[s], ab = line[s + 1] - a; float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-8f));
                        float dist = (p - (a + ab * t)).magnitude; if (dist < best) { best = dist; at = from + t * ab.magnitude; }
                        from += ab.magnitude;
                    }
                    if (best < .015f * (1 - at / Mathf.Max(total, 1e-4f)) * (.6f + .8f * Grain(x, y))) px[y * w + x].a = 0;
                }
            }
            for (int n = rnd.Next(4); n > 0; n--)
            {
                // A small nick at an edge (2-4 cm), away from the corners.
                int side = rnd.Next(4); float length = side < 2 ? lu : lv, along = .2f + (length - .4f) * R(), r = .02f + .02f * R();
                if (length < .6f) continue;
                var c = side == 0 ? new Vector2(along, 0) : side == 1 ? new Vector2(along, lv) : side == 2 ? new Vector2(0, along) : new Vector2(lu, along);
                foreach (var (x, y, p) in Box(c - Vector2.one * r * 1.2f, c + Vector2.one * r * 1.2f)) if ((p - c).magnitude < r * (.8f + .4f * Grain(x, y))) px[y * w + x].a = 0;
            }
            tex.SetPixels32(px); tex.Apply(true);
            return tex;
        }
        // The cloth darkens gently with wear (user, 2026-10-06): new, the light grey of FadedColor (a torn tarp's colour in 0.8.7);
        // at 0, 25% darker; a torn tarp shows that final colour.
        internal const float WornDarker = .25f;
        internal static Color WornColor { get { var c = FadedColor * (1 - WornDarker); c.a = FadedColor.a; return c; } }
        internal static void Fade(Tarp t, float health)
        {
            t.shown = health;
            if (t.material && t.material.HasProperty("_Color")) t.material.color = Color.Lerp(FadedColor, WornColor, 1 - health / FullHealth);
        }
        // The option "Tarp wearing" changed (wasWearing: its value before): every tied tarp, also on ships switched off far
        // away, keeps the health it has now and wears (or stays) from here. Returns the option's value now.
        internal static bool Rebase(bool wasWearing)
        {
            // Also tarps built under a ship that has stayed switched off (they are not in Tarp.All until she is switched on).
            var tarps = Tarp.All.Concat(Resources.FindObjectsOfTypeAll<Tarp>()).Where(t => t).Distinct().ToList();
            foreach (var tarp in tarps) tarp.Rebase(wasWearing);
            Plugin.Log?.LogInfo("Tarp wearing " + (Settings.TarpWearing ? "on" : "off") + ": " + tarps.Count + " tarp(s) re-based.");
            return Settings.TarpWearing;
        }
        internal static Color ClothColor => ClothMaterial.HasProperty("_Color") ? ClothMaterial.color : Color.white;
        // Weathered: the cloth as seen (the canvas strip's mean times its colour) turned to a grey of the same brightness, 15% darker;
        // as a tint on its colour (the Dhow roof's cream cloth goes a dull grey).
        private static Color? fadedColor;
        internal static Color FadedColor
        {
            get
            {
                if (fadedColor.HasValue) return fadedColor.Value;
                Color b = ClothColor, m = Mean(Canvas), seen = new Color(m.r * b.r, m.g * b.g, m.b * b.b);
                float l = .299f * seen.r + .587f * seen.g + .114f * seen.b;
                var target = new Color(l, l, l) * .85f;
                float Tint(float want, float has) => has > .01f ? Mathf.Clamp(want / has, 0, 2) : 1;
                fadedColor = new Color(b.r * Tint(target.r, seen.r), b.g * Tint(target.g, seen.g), b.b * Tint(target.b, seen.b), b.a);
                Plugin.Log?.LogInfo("Canvas cloth: colour " + b + ", texture mean " + m + ", faded " + fadedColor.Value + ".");
                return fadedColor.Value;
            }
        }
        // A texture's mean colour, through the GPU (the game's textures can't be read): drawn into 8 x 8 and read back.
        private static Color Mean(Texture tex)
        {
            if (!tex) return Color.white;
            var rt = RenderTexture.GetTemporary(8, 8, 0, RenderTextureFormat.ARGB32); var was = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt); RenderTexture.active = rt;
                var read = new Texture2D(8, 8, TextureFormat.RGBA32, false); read.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
                var px = read.GetPixels(); UnityEngine.Object.Destroy(read);
                var sum = px.Aggregate(Color.clear, (s, c) => s + c); return sum / px.Length;
            }
            catch (Exception e) { Plugin.Log?.LogWarning("Canvas cloth colour not read; fading assumes a white texture. " + e.Message); return Color.white; }
            finally { RenderTexture.active = was; RenderTexture.ReleaseTemporary(rt); }
        }
        // ---- Untying ----
        // A click on any corner knot: the tarp, its ropes and knots go, and a canvas pack comes back with the health left: into
        // free hands, else onto the deck or ground under that corner (on a ship over open water: her deck's middle). A torn
        // tarp gives nothing back (user, 2026-10-05).
        internal static ShipItem LastPack;
        // floor: an item the pack may land on although it is no longer nailed (the one just un-nailed under that corner).
        internal static void Untie(Tarp tarp, int corner, GoPointer pointer, ShipItem floor = null)
        {
            if (!tarp || !Registered) return;
            float health = tarp.Health; LastPack = null;
            var knot = tarp.knots[corner].transform; var frame = tarp.onShip ? tarp.transform.parent : null;
            bool hand = pointer && !pointer.GetHeldItem();
            Vector3 at; Quaternion turn;
            var flat = Vector3.ProjectOnPlane(pointer ? pointer.transform.forward : -knot.forward, Vector3.up); if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            if (hand) { at = pointer.transform.position + pointer.transform.forward * 1.3f - pointer.transform.up * .5f; turn = Quaternion.LookRotation(flat.normalized); }
            else { at = Under(knot, frame, floor) + Vector3.up * .6f; turn = Quaternion.LookRotation(flat.normalized); }
            RecheckUnder(tarp);   // runs next frame, when the tarp is gone
            Tarp.All.Remove(tarp);   // at once (the object goes at the frame's end): nothing finds it again meanwhile
            UnityEngine.Object.Destroy(tarp.gameObject);
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUISound(UISounds.itemPickup, .55f, 1);
            if (health > 0 && Plugin.Instance) Plugin.Instance.StartCoroutine(Give(at, turn, frame, hand ? pointer : null, health));
        }
        // Un-nailing an item a corner is tied to unties that tarp (user, 2026-10-10): its pack drops under that corner, as a
        // click on a knot does (a torn tarp gives none). "Tied to it": the knot within 5 cm of the item's physics copy (on her
        // walk copy), found from geometry, so it holds after a load too. Corners still waiting on it are cleared.
        internal const float OnItem = .05f;
        internal static int UntieFrom(ShipItem item)
        {
            if (!item || !item.itemRigidbodyC) return 0;
            var colliders = item.itemRigidbodyC.GetComponentsInChildren<Collider>(true).Where(c => c && !c.isTrigger).ToArray();
            if (colliders.Length == 0) return 0;
            if (Pending.Any(c => c.part && colliders.Contains(c.part))) Cancel(true);
            int untied = 0;
            foreach (var tarp in Tarp.All.ToList())
            {
                if (!tarp || tarp.knots == null) continue;
                // Ship tarps compare on her walk copy, where the item's physics copy lies; land tarps in the world.
                Transform frame = null, walk = null;
                if (tarp.onShip) { frame = tarp.transform.parent; walk = frame ? Link(frame)?.walkCollider : null; if (!walk) continue; }
                for (int i = 0; i < tarp.knots.Length; i++)
                {
                    if (!tarp.knots[i]) continue;
                    var at = tarp.knots[i].transform.position; var p = tarp.onShip ? walk.TransformPoint(frame.InverseTransformPoint(at)) : at;
                    if (!colliders.Any(c => (Closest(c, p) - p).sqrMagnitude <= OnItem * OnItem)) continue;
                    Plugin.Log?.LogInfo("Canvas tarp untied: " + item.name + " was un-nailed or removed under corner " + i + ".");
                    Untie(tarp, i, null, item); untied++; break;
                }
            }
            return untied;
        }
        // The game answers ClosestPoint only for primitives and convex meshes; a concave mesh uses its bounds.
        private static Vector3 Closest(Collider c, Vector3 p) => c is MeshCollider m && !m.convex ? c.bounds.ClosestPoint(p) : c.ClosestPoint(p);
        // The deck or ground under a corner knot, found from just off the part it is tied to (the knot faces into it).
        // A nailed item is floor (a corner on top of a nailed crate drops its pack on the crate, not inside it), and so is floor.
        private static Vector3 Under(Transform knot, Transform frame, ShipItem floor = null)
        {
            var from = knot.position - knot.forward * .3f + Vector3.up * .1f;
            var walk = frame ? Link(frame)?.walkCollider : null;
            if (frame && walk)
            {
                var p = walk.TransformPoint(frame.InverseTransformPoint(from));
                var hit = Physics.RaycastAll(p, -walk.up, 30, ~0, QueryTriggerInteraction.Ignore)
                    .Where(h => h.collider.transform.IsChildOf(walk) && Floor(h.collider, floor)).OrderBy(h => h.distance).FirstOrDefault();
                if (hit.collider) return frame.TransformPoint(walk.InverseTransformPoint(hit.point));
                var ship = Up<PurchasableBoat>(frame);
                return ship ? ForcedMove.DropPoint(ship) - Vector3.up * 1.5f : knot.position;
            }
            var ground = Physics.RaycastAll(from, Vector3.down, 30, ~0, QueryTriggerInteraction.Ignore)
                .Where(h => !h.collider.GetComponentInParent<ItemRigidbody>() && !h.collider.GetComponentInParent<ShipItem>()).OrderBy(h => h.distance).FirstOrDefault();
            return ground.collider ? ground.point : knot.position;
        }
        private static readonly System.Reflection.MethodInfo enterBoat = AccessTools.Method(typeof(ShipItem), "EnterBoat");
        // A new owned pack; aboard it joins the ship once its physics copy exists (as unbundled planks do), then into the hand.
        private static System.Collections.IEnumerator Give(Vector3 at, Quaternion turn, Transform frame, GoPointer hand, float health)
        {
            var pack = UnityEngine.Object.Instantiate(PackTemplate, at, turn, FloatingOriginManager.instance.transform).GetComponent<ShipItem>();
            pack.sold = true; pack.health = health; pack.GetComponent<SaveablePrefab>().RegisterToSave(); LastPack = pack;
            var local = frame ? new Pose(frame.InverseTransformPoint(at), Quaternion.Inverse(frame.rotation) * turn) : default;
            // Its physics copy and collision checker come with its Start, a frame or more after it is made.
            for (float waited = 0; pack && (!pack.itemRigidbodyC || !pack.colChecker) && waited < 2; waited += Time.unscaledDeltaTime) yield return null;
            yield return new WaitForFixedUpdate();
            if (!pack) yield break;
            var link = frame ? Link(frame) : null; var embark = link ? link.GetComponent<Collider>() : null;
            if (frame && embark && pack.itemRigidbodyC)
            {
                if (!pack.currentActualBoat)
                    try { enterBoat.Invoke(pack, new object[] { embark }); }
                    catch (Exception e) { Plugin.Log?.LogWarning("A canvas pack could not board its ship: " + (e.InnerException ?? e).Message); }
                if (pack.currentActualBoat == frame)
                { pack.transform.SetPositionAndRotation(frame.TransformPoint(local.position), frame.rotation * local.rotation); pack.itemRigidbodyC.ForceRigidbodyToWalkCol(); }
            }
            yield return null;
            if (hand && pack && !hand.GetHeldItem() && pack.colChecker) hand.PickUpItem(pack);
        }

        // ---- Shop stock ----
        // A canvas pack at each vendor that sells oakum (user, 2026-10-06: leaning on a counter or table, its foot on the ground,
        // facing outward, else at a side, never the shopkeeper's side; else lying under it, half out; Old Ankh Town added, at a side;
        // Crab Beach: lying on its porch beside the table (its open table and the stock around it leave no other way)) (spots found and pictured by the QA probe -ShopSpots,
        // clear of the vendor's stock, nothing moved or replaced): the island scene, the vendor's shop area (its name and its
        // place in the scene's root object) and the pack's pose in the area's frame.
        internal struct Stock { internal string port; internal int scene; internal string area; internal Vector3 areaInRoot, local; internal Quaternion turn; }
        internal static readonly Stock[] Stocks = {
            new Stock { port = "Gold Rock City", scene = 1, area = "shop (9)", areaInRoot = new Vector3(1542.870f, 8.400f, -381.341f), local = new Vector3(-0.324971974f, -0.211410135f, 0.271434247f), turn = new Quaternion(-0.09504408f, -0.6795426f, -0.0894634947f, 0.7219314f) },   // leaning 15 deg on its long edge against market_stall (5), facing outward, on gold_rock_dock_027
            new Stock { port = "Al'Nilem", scene = 2, area = "shop area (3)", areaInRoot = new Vector3(269.461f, 15.300f, 54.128f), local = new Vector3(-0.1670038f, -0.2798886f, -0.367811561f), turn = new Quaternion(0.08897021f, 0.725439847f, 0.09550597f, -0.675795853f) },   // leaning 15 deg on its long edge against market_stall (3), facing outward, on Terrain
            new Stock { port = "Neverdin", scene = 3, area = "shop area (2)", areaInRoot = new Vector3(1.060f, 15.350f, 7.100f), local = new Vector3(-0.0324442275f, -0.3643311f, -0.199857071f), turn = new Quaternion(0.479126215f, -0.520036638f, -0.520036638f, -0.479126215f) },   // lying under market_stall (3), half out facing outward, on Terrain (the first ground under the table)
            new Stock { port = "Dragon Cliffs", scene = 9, area = "shop area (4)", areaInRoot = new Vector3(-83.711f, 4.650f, -550.090f), local = new Vector3(0.4848143f, -0.1413865f, 0.158377022f), turn = new Quaternion(-0.0821990445f, 0.770151258f, 0.101392329f, 0.6243638f) },   // leaning 15 deg on its long edge against market_stall, facing outward, on Terrain
            new Stock { port = "New Port", scene = 12, area = "shop  supplies", areaInRoot = new Vector3(280.923f, 3.949f, -308.519f), local = new Vector3(0.0298999548f, -0.309635848f, 0.3119994f), turn = new Quaternion(-0.0999682f, 0.08461268f, 0.700004637f, 0.7020262f) },   // leaning 15 deg on its short edge over the top edge of east_market_stall 1, facing outward, on east_house_platform
            new Stock { port = "Sage Hills", scene = 13, area = "shop area", areaInRoot = new Vector3(-47.422f, 4.360f, 24.670f), local = new Vector3(-0.28263104f, -0.1685237f, 0.06307918f), turn = new Quaternion(-0.5652245f, -0.4359985f, 0.424877942f, 0.5566914f) },   // leaning 15 deg on its short edge over the top edge of east_market_stall 1 (2), at a side, on east_house_platform (3)
            new Stock { port = "Fort Aestrin", scene = 15, area = "shop area (1)", areaInRoot = new Vector3(-74.840f, 4.150f, 43.781f), local = new Vector3(-0.01783118f, -0.200141758f, 0.0917044654f), turn = new Quaternion(-0.13052541f, 0.00342229521f, 0.0004505537f, 0.991439f) },   // leaning 15 deg on its long edge against market stall medi 1 (1), facing outward, on stone dock shaped
            new Stock { port = "Sunspire", scene = 16, area = "shop area (2)", areaInRoot = new Vector3(-332.379f, 2.530f, -379.117f), local = new Vector3(-0.0225794148f, -0.141178161f, 0.042308677f), turn = new Quaternion(-0.00194590562f, 0.9913347f, 0.1305117f, 0.0147806108f) },   // leaning 15 deg on its long edge against market stall medi 1 (2), facing outward, on Terrain
            new Stock { port = "Oasis", scene = 20, area = "shop area (3)", areaInRoot = new Vector3(72.260f, 18.000f, -141.949f), local = new Vector3(0.1334403f, -0.1547255f, 0.1221445f), turn = new Quaternion(-0.1736383f, 0.0105025321f, 0.0018518822f, 0.9847517f) },   // leaning 20 deg on its long edge against market_stall, facing outward, on Terrain
            new Stock { port = "Siren Song", scene = 21, area = "shop area (4)", areaInRoot = new Vector3(27.930f, 3.420f, 31.820f), local = new Vector3(-0.118838891f, -0.218512654f, 0.07966131f), turn = new Quaternion(0.09367367f, 0.6904341f, 0.09089737f, -0.711522162f) },   // leaning 15 deg on its long edge against market stall medi 1 (1), facing outward, on stone dock (1)
            new Stock { port = "Chronos", scene = 25, area = "shop area", areaInRoot = new Vector3(552.125f, 8.078f, 439.258f), local = new Vector3(0.0179261826f, -0.218050033f, -0.08878778f), turn = new Quaternion(0.700995147f, 0.7011194f, 0.09276806f, -0.09182382f) },   // leaning 15 deg on its short edge against market stall medi 1 (3), facing outward, on Terrain
            new Stock { port = "Kicia Bay", scene = 27, area = "shop area (13)", areaInRoot = new Vector3(-29.547f, 2.790f, 153.430f), local = new Vector3(-0.162967876f, -0.238130927f, -0.37696436f), turn = new Quaternion(0.6861876f, 0.7069929f, 0.170724f, -0.0126916608f) },   // leaning 15 deg on its short edge over the top edge of east_market_stall 2 (3), facing outward, on Terrain (1)
            new Stock { port = "Firefly Grotto", scene = 33, area = "shop area (2)", areaInRoot = new Vector3(-105.531f, 3.270f, -1042.711f), local = new Vector3(0.008862548f, -0.366745532f, -0.104049332f), turn = new Quaternion(-0.0938405544f, 0.689125538f, 0.09072512f, 0.7127896f) },   // leaning 15 deg on its long edge against market stall medi 1 (2), facing outward, on Cube.002
            new Stock { port = "Fey Valley", scene = 35, area = "shop area (2)", areaInRoot = new Vector3(114.309f, 57.370f, -155.547f), local = new Vector3(0.136717424f, -0.188405186f, -0.0418390781f), turn = new Quaternion(0.089505814f, 0.7216287f, 0.09500424f, -0.679864f) },   // leaning 15 deg on its long edge against market stall medi 2, facing outward, on Terrain
            new Stock { port = "Dead Cove", scene = 37, area = "shop area (1)", areaInRoot = new Vector3(-52.859f, 2.020f, 10.980f), local = new Vector3(-0.07870445f, -0.109501205f, 0.172861472f), turn = new Quaternion(0.13052541f, 0.003439378f, 0.0004528009f, -0.9914389f) },   // leaning 15 deg on its long edge over the top edge of east_market_stall 1, facing outward, on east_dock (66)
            new Stock { port = "Old Ankh Town", scene = 40, area = "shop area", areaInRoot = new Vector3(28.729f, 15.262f, 8.484f), local = new Vector3(0.0924444348f, -0.129416883f, 0.0935314149f), turn = new Quaternion(0.000131709792f, 0.9914444f, 0.13052614f, -0.00100042112f) },   // leaning 15 deg on its long edge against furniture_table_4 (2), at a side, on Cube_022
            new Stock { port = "Crab Beach", scene = 11, area = "shop area (1)", areaInRoot = new Vector3(384.922f, 14.010f, -217.920f), local = new Vector3(0.0482834578f, -0.327959627f, -0.134715319f), turn = new Quaternion(-0.5760863f, 0.410030037f, 0.410030037f, 0.5760863f) },   // lying on the porch 0.33 m from furniture_table_4 (2), at a side, on east_dock (23)
        };
        internal const string StockName = "EconomyOverhaul canvas pack stock";
        // A wood pole bundle (four 2 m poles; the user, 2026-10-12: bundles, not single poles) beside each canvas pack stall:
        // leaning on the counter's top edge, else on a post of the
        // stall or a post or wall beside it; at New Port and Dead Cove beside the vendor's oar; at Sunspire, Siren Song, Chronos and
        // Firefly Grotto lying on the ground in front of the pack; at Old Ankh Town on the wall (spots found and pictured by the QA
        // probe -PoleSpots for the bundle's 10 cm round, clear of the vendor's stock and the pack, nothing moved or replaced). As the
        // packs: the stall's shop area and the bundle's end and turn in the area's frame.
        internal static readonly Stock[] PoleStocks = {
            new Stock { port = "Gold Rock City", scene = 1, area = "shop (9)", areaInRoot = new Vector3(1542.870f, 8.400f, -381.341f), local = new Vector3(-0.317941368f, -0.274739236f, 0.4399269f), turn = new Quaternion(0.6086764f, -0.01326741f, 0.0101804724f, 0.793242455f) },   // leaning 15 deg over the top edge of market_stall (5), facing outward, resting 0.84 m up, its foot on gold_rock_dock_027 1.19 m from the pack
            new Stock { port = "Al'Nilem", scene = 2, area = "shop area (3)", areaInRoot = new Vector3(269.461f, 15.300f, 54.128f), local = new Vector3(-0.176211089f, -0.353755534f, -0.316438049f), turn = new Quaternion(-0.438141555f, 0.56051445f, -0.470327467f, -0.5221568f) },   // leaning 10 deg over the top edge of market_stall (3), facing outward, resting 0.90 m up, its foot on Terrain (no renderer on it) 0.70 m from the pack
            new Stock { port = "Neverdin", scene = 3, area = "shop area (2)", areaInRoot = new Vector3(1.060f, 15.350f, 7.100f), local = new Vector3(0.174184561f, -0.3987029f, -0.202169746f), turn = new Quaternion(-0.417365253f, -0.5775461f, 0.443166733f, -0.543921f) },   // leaning 15 deg against the post or frame of market_stall (3), by the stall, resting 1.86 m up, its foot on Terrain (no renderer on it) 1.93 m from the pack
            new Stock { port = "Dragon Cliffs", scene = 9, area = "shop area (4)", areaInRoot = new Vector3(-83.711f, 4.650f, -550.090f), local = new Vector3(0.481723368f, -0.210326359f, 0.337741822f), turn = new Quaternion(0.6393036f, 0.07965092f, -0.06683504f, 0.7618923f) },   // leaning 10 deg against the post or frame of market_stall, by the stall, resting 1.89 m up, its foot on east_house_platform (1) (no renderer on it) 0.90 m from the pack
            new Stock { port = "New Port", scene = 12, area = "shop  supplies", areaInRoot = new Vector3(280.923f, 3.949f, -308.519f), local = new Vector3(-0.1638691f, -0.447944134f, 0.335214168f), turn = new Quaternion(0.608724952f, -0.008688566f, 0.00666696f, 0.7933059f) },   // leaning 15 deg over the top edge of east_market_stall 1, facing outward, resting 0.85 m up, its foot on east_house_platform (no renderer on it) 0.44 m from the oar's foot (1.67 m from the pack)
            new Stock { port = "Sage Hills", scene = 13, area = "shop area", areaInRoot = new Vector3(-47.422f, 4.360f, 24.670f), local = new Vector3(-0.2353538f, -0.291307747f, 0.154015735f), turn = new Quaternion(0.608746052f, -0.005651837f, 0.00433681626f, 0.7933333f) },   // leaning 15 deg over the top edge of east_market_stall 1 (2), facing outward, resting 0.83 m up, its foot on east_house_platform (3) (no renderer on it) 0.86 m from the pack
            new Stock { port = "Fort Aestrin", scene = 15, area = "shop area (1)", areaInRoot = new Vector3(-74.840f, 4.150f, 43.781f), local = new Vector3(0.190743268f, -0.236277178f, 0.158665523f), turn = new Quaternion(-0.264767259f, 0.733883262f, -0.467535228f, -0.415601343f) },   // leaning 25 deg against the wall banner M post (4), by the stall, resting 1.76 m up, its foot on stone dock shaped (no renderer on it) 1.63 m from the pack
            new Stock { port = "Sunspire", scene = 16, area = "shop area (2)", areaInRoot = new Vector3(-332.379f, 2.530f, -379.117f), local = new Vector3(0.09982989f, -0.181111723f, 0.00611003442f), turn = new Quaternion(-0.000875937869f, 0.696486056f, 0.0008502014f, 0.7175693f) },   // lying on the ground in front of the pack, along the counter, 0.39 m from the pack's middle (shifted 0.0 m along, 0.00 m out), on Terrain (no renderer on it), 0.1 deg off level, at most 0.9 cm over the ground
            new Stock { port = "Oasis", scene = 20, area = "shop area (3)", areaInRoot = new Vector3(72.260f, 18.000f, -141.949f), local = new Vector3(0.0424064137f, -0.219553858f, 0.120945156f), turn = new Quaternion(0.642751038f, 0.008169531f, -0.006855039f, 0.7660009f) },   // leaning 10 deg over the top edge of market_stall, facing outward, resting 0.85 m up, its foot on Terrain (no renderer on it) 0.70 m from the pack
            new Stock { port = "Siren Song", scene = 21, area = "shop area (4)", areaInRoot = new Vector3(27.930f, 3.420f, 31.820f), local = new Vector3(-0.16582343f, -0.249237821f, -0.0236135013f), turn = new Quaternion(0f, 0.99988693f, 0f, -0.0150402347f) },   // lying on the ground in front of the pack, along the counter, 0.40 m from the pack's middle (shifted 0.0 m along, 0.00 m out), on stone dock (1) (no renderer on it), 0.0 deg off level, at most 0.0 cm over the ground
            new Stock { port = "Chronos", scene = 25, area = "shop area", areaInRoot = new Vector3(552.125f, 8.078f, 439.258f), local = new Vector3(0.181523189f, -0.299461573f, -0.163973868f), turn = new Quaternion(0.00138198957f, 0.7066293f, -0.00138013f, 0.7075814f) },   // lying on the ground in front of the pack, along the counter, 0.44 m from the pack's middle (shifted -0.1 m along, 0.00 m out), on Terrain (no renderer on it), 0.2 deg off level, at most 1.6 cm over the ground
            new Stock { port = "Kicia Bay", scene = 27, area = "shop area (13)", areaInRoot = new Vector3(-29.547f, 2.790f, 153.430f), local = new Vector3(-0.0837563649f, -0.325767279f, -0.365969956f), turn = new Quaternion(0.06861346f, 0.78829813f, -0.6048824f, 0.08941888f) },   // leaning 15 deg over the top edge of east_market_stall 2 (3), facing outward, resting 0.74 m up, its foot on east_dock (105) 0.79 m from the pack
            new Stock { port = "Firefly Grotto", scene = 33, area = "shop area (2)", areaInRoot = new Vector3(-105.531f, 3.270f, -1042.711f), local = new Vector3(0.0602152944f, -0.423622936f, 0.0265933946f), turn = new Quaternion(0f, -0.01687739f, 0f, 0.999857664f) },   // lying on the ground in front of the pack, along the counter, 0.39 m from the pack's middle (shifted 0.0 m along, 0.00 m out), on Cube.002, 0.0 deg off level, at most 0.0 cm over the ground
            new Stock { port = "Fey Valley", scene = 35, area = "shop area (2)", areaInRoot = new Vector3(114.309f, 57.370f, -155.547f), local = new Vector3(0.07102031f, -0.245766982f, 0.0201575384f), turn = new Quaternion(-0.417446256f, 0.57744664f, -0.4430904f, -0.5440266f) },   // leaning 15 deg over the top edge of market stall medi 2, facing outward, resting 0.93 m up, its foot on Terrain (no renderer on it) 0.64 m from the pack
            new Stock { port = "Dead Cove", scene = 37, area = "shop area (1)", areaInRoot = new Vector3(-52.859f, 2.020f, 10.980f), local = new Vector3(0.156767413f, -0.1931217f, 0.209706709f), turn = new Quaternion(-0.573556542f, 0.00682601752f, -0.00477964524f, -0.8191237f) },   // leaning 20 deg over the top edge of east_market_stall 2, facing outward, resting 0.72 m up, its foot on east_dock (66) 0.40 m from the oar's foot (1.63 m from the pack)
            new Stock { port = "Old Ankh Town", scene = 40, area = "shop area", areaInRoot = new Vector3(28.729f, 15.262f, 8.484f), local = new Vector3(-0.139207661f, -0.194941863f, 0.176224738f), turn = new Quaternion(-0.008938359f, 0.8190526f, -0.573506832f, -0.0127653144f) },   // leaning 20 deg against the wall building_arab_door_arch, by the stall, resting 1.82 m up, its foot on Cube_022 1.93 m from the pack
            new Stock { port = "Crab Beach", scene = 11, area = "shop area (1)", areaInRoot = new Vector3(384.922f, 14.010f, -217.920f), local = new Vector3(-0.193760812f, -0.37430945f, -0.0322473757f), turn = new Quaternion(0.458284229f, -0.5371512f, 0.450723439f, 0.5461618f) },   // leaning 10 deg over the top edge of furniture_table_4 (2), facing outward, resting 0.62 m up, its foot on east_dock (23) (no renderer on it) 1.36 m from the pack
        };
        internal const string PoleStockName = "EconomyOverhaul wood pole stock";
        internal static void AddStock(ShopArea area)
        {
            if (!area) return;
            if (Registered) AddSlots(area, Stocks, StockName, PackTemplate);
            if (WoodPoles.BundleTemplate) AddSlots(area, PoleStocks, PoleStockName, WoodPoles.BundleTemplate);
        }
        private static void AddSlots(ShopArea area, Stock[] table, string name, GameObject prefab)
        {
            int scene = area.gameObject.scene.buildIndex; var root = area.transform.root;
            foreach (var s in table)
            {
                if (s.scene != scene || area.name != s.area || Vector3.Distance(root.InverseTransformPoint(area.transform.position), s.areaInRoot) > .5f) continue;
                // Under an unscaled parent that moves with the island (an item takes its slot's scale).
                var parent = area.transform.parent && Mathf.Abs(area.transform.parent.lossyScale.x - 1) < 1e-3f && Mathf.Abs(area.transform.parent.lossyScale.y - 1) < 1e-3f ? area.transform.parent : root;
                var slot = new GameObject(name); slot.transform.SetParent(parent, false);
                slot.transform.SetPositionAndRotation(area.transform.TransformPoint(s.local), area.transform.rotation * s.turn);
                var spawner = slot.AddComponent<ShopItemSpawner>(); spawner.itemPrefab = prefab; spawner.availableAtNight = false; spawner.priceMult = 1;
            }
        }

        // ---- Save and load ----
        // Records of the tied tarps (and of those still waiting to be rebuilt after a load). On a ship: her save index and the
        // corners in her boarding frame as loading finds it; on land: real positions and world turns.
        private static List<TarpState> waiting;
        internal static List<TarpState> Records()
        {
            var list = waiting != null ? new List<TarpState>(waiting) : new List<TarpState>();
            foreach (var t in Tarp.All)
            {
                var parent = t ? t.transform.parent : null; if (!parent || t.corners == null) continue;
                // Through the tarp's own frame: a world shift moves the shifting world's children, the tarp among them.
                Vector3[] at = t.corners.Select(t.transform.TransformPoint).ToArray(); Quaternion[] turn = t.turns.Select(q => t.transform.rotation * q).ToArray();
                int ship = -1;
                if (t.onShip)
                {
                    var boat = Up<PurchasableBoat>(parent); var frame = boat ? FrameOf(boat) : null;
                    ship = boat ? Pledges.Index(boat) : -1; if (!frame || ship < 0) continue;
                    at = at.Select(frame.InverseTransformPoint).ToArray(); turn = turn.Select(q => Quaternion.Inverse(frame.rotation) * q).ToArray();
                }
                else at = at.Select(FloatingOriginManager.instance.ShiftingPosToRealPos).ToArray();
                list.Add(new TarpState { ship = ship, corners = at.SelectMany(v => new[] { v.x, v.y, v.z }).ToArray(), turns = turn.SelectMany(q => new[] { q.x, q.y, q.z, q.w }).ToArray(),
                    wear = FullHealth - t.Health });
            }
            return list;
        }
        // The nearest T on t or above it, switched off or not (GetComponentInParent skips objects switched off, as the game
        // switches off ships far away).
        internal static T Up<T>(Transform t) where T : Component { for (; t; t = t.parent) { var c = t.GetComponent<T>(); if (c) return c; } return null; }
        internal static bool ValidRecord(TarpState s) => s != null && s.ship >= -1 && s.corners != null && s.corners.Length == 12 && s.turns != null && s.turns.Length == 16 &&
            s.corners.Concat(s.turns).Concat(new[] { s.wear }).All(x => !float.IsNaN(x) && !float.IsInfinity(x));
        // Her boarding frame: the parent of her boarding trigger that names her walk copy, the player-sized one first.
        internal static Transform FrameOf(PurchasableBoat ship)
        {
            var link = ship ? ship.GetComponentsInChildren<BoatEmbarkCollider>(true).Where(c => c.walkCollider).OrderBy(c => c.CompareTag("EmbarkColPlayer") ? 0 : 1).FirstOrDefault() : null;
            return link ? link.transform.parent : null;
        }
        // A new game or a load: no tarps from before, and no corners waiting.
        internal static void Clear()
        {
            Cancel(false); waiting = null;
            foreach (var t in Tarp.All.ToList()) if (t) UnityEngine.Object.Destroy(t.gameObject);
        }
        // After a load: each saved tarp rebuilt once loading is over (the world's offset is final then); one whose ship is gone
        // is dropped (it leaves the save at the next save).
        internal static void Loaded()
        {
            var saved = World.State.tarps; if (saved == null || saved.Count == 0 || !Plugin.Instance) return;
            waiting = saved.ToList(); Plugin.Instance.StartCoroutine(Rebuild(waiting));
        }
        private static System.Collections.IEnumerator Rebuild(List<TarpState> saved)
        {
            while (World.Loading || GameState.currentlyLoading) yield return null;
            yield return null; yield return null;
            if (waiting != saved) yield break;   // another load or a new game meanwhile
            foreach (var s in saved)
            {
                var at = Enumerable.Range(0, 4).Select(i => new Vector3(s.corners[3 * i], s.corners[3 * i + 1], s.corners[3 * i + 2])).ToArray();
                var turn = Enumerable.Range(0, 4).Select(i => new Quaternion(s.turns[4 * i], s.turns[4 * i + 1], s.turns[4 * i + 2], s.turns[4 * i + 3])).ToArray();
                float health = FullHealth - Mathf.Clamp(s.wear, 0, FullHealth);   // a record without wear reads as new
                try
                {
                    if (s.ship >= 0)
                    {
                        var frame = FrameOf(Pledges.Find(s.ship));
                        if (!frame) { Plugin.Log?.LogWarning("A canvas tarp's ship (" + s.ship + ") is gone; the tarp is dropped."); continue; }
                        Build(frame, true, at, turn, health);
                    }
                    else
                    {
                        var root = FloatingOriginManager.instance.transform;
                        Build(root, false, at.Select(p => root.InverseTransformPoint(FloatingOriginManager.instance.RealPosToShiftingPos(p))).ToArray(), turn.Select(q => Quaternion.Inverse(root.rotation) * q).ToArray(), health);
                    }
                }
                catch (Exception e) { Plugin.Log?.LogError("A canvas tarp could not be rebuilt; it is dropped. " + e); }
            }
            waiting = null;
        }

        // ---- Cover ----
        // The game's cloth roofs that count as cover: on ships, by name, only while their shipyard option is fitted (shown).
        // Flags, pennants, banners and roofs on land stay open sky.
        internal static readonly string[] RoofNames = { "roof_cloth", "canopy", "canopy_001", "cabin_cloth_only", "canvas_roof_001" };
        private static readonly List<Cloth> roofs = new List<Cloth>(); private static int roofsAt = -1;
        private static readonly Dictionary<Mesh, int[]> roofTriangles = new Dictionary<Mesh, int[]>();
        internal static List<Cloth> CountedRoofs()
        {
            if (roofsAt == Time.frameCount) return roofs;
            roofsAt = Time.frameCount; roofs.Clear();
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Cloth>()) if (Array.IndexOf(RoofNames, c.name) >= 0 && c.GetComponentInParent<PurchasableBoat>()) roofs.Add(c);
            return roofs;
        }
        // One of a cargo's cover rays: straight up from p (1 cm over its top) for `reach` metres, through a tarp's cover shape
        // or a counted roof's cloth (neither has a collider). Cargo poking through a tarp starts its rays above it: open. A tarp
        // worn to 0 covers nothing.
        internal static bool CoverAbove(Vector3 p, float reach)
        {
            foreach (var t in Tarp.All)
                if (t && t.cloth && t.isActiveAndEnabled && t.coverShape != null && t.Health > 0 && Crosses(t.cloth.transform, t.coverShape, t.layout.triangles, t.coverBounds, p, reach)) return true;
            foreach (var c in CountedRoofs())
            {
                var skin = c.GetComponent<SkinnedMeshRenderer>(); var mesh = skin ? skin.sharedMesh : null; if (!mesh) continue;
                if (!roofTriangles.TryGetValue(mesh, out var tris)) roofTriangles[mesh] = tris = mesh.isReadable ? mesh.triangles : null;
                if (tris == null) continue;
                var v = c.vertices; if (v.Length != mesh.vertexCount) v = mesh.vertices;   // welded cloth points: the modelled roof
                var b = new Bounds(v[0], Vector3.zero); foreach (var x in v) b.Encapsulate(x);
                if (Crosses(c.transform, v, tris, b, p, reach)) return true;
            }
            return false;
        }
        private static bool Crosses(Transform t, Vector3[] v, int[] tri, Bounds b, Vector3 p, float reach)
        {
            Vector3 o = t.InverseTransformPoint(p), d = t.InverseTransformVector(Vector3.up * reach);
            var seg = new Bounds(o, Vector3.zero); seg.Encapsulate(o + d); if (!seg.Intersects(b)) return false;
            for (int i = 0; i + 2 < tri.Length; i += 3) if (Hits(o, d, v[tri[i]], v[tri[i + 1]], v[tri[i + 2]])) return true;
            return false;
        }
        // The segment o to o + d through triangle abc (Moller-Trumbore), either face.
        internal static bool Hits(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 e1 = b - a, e2 = c - a, h = Vector3.Cross(d, e2); float det = Vector3.Dot(e1, h);
            if (Mathf.Abs(det) < 1e-12f) return false;
            float inv = 1 / det; var s = o - a; float u = inv * Vector3.Dot(s, h); if (u < 0 || u > 1) return false;
            var q = Vector3.Cross(s, e1); float w = inv * Vector3.Dot(d, q); if (w < 0 || u + w > 1) return false;
            float t = inv * Vector3.Dot(e2, q); return t > 0 && t <= 1;
        }
        // Tying, settling and untying rescan the placed cargo under the tarp (as an opened hatch does).
        internal static void RecheckUnder(Tarp t)
        {
            if (!t || t.coverShape == null) return;
            var sheet = t.cloth ? t.cloth.transform : t.transform;
            var area = new Bounds(sheet.TransformPoint(t.coverShape[0]), Vector3.zero); foreach (var v in t.coverShape) area.Encapsulate(sheet.TransformPoint(v));
            if (t.onShip) { if (t.transform.parent) CoverEvents.RecheckUnder(t.transform.parent, area); return; }
            foreach (var c in CargoCondition.Active)
            {
                if (!c || !c.Item || c.Item.currentActualBoat) continue;
                var o = c.WorldBounds;
                if (o.max.x > area.min.x - .25f && o.min.x < area.max.x + .25f && o.max.z > area.min.z - .25f && o.min.z < area.max.z + .25f && o.min.y < area.max.y) c.RequestCoverRecheck();
            }
        }

        private static Material clothMaterial, ropeMaterial;
        // The small Dhow roof's cloth ("dhow small cloth paint", double-sided), else any cloth roof's.
        internal static Material ClothMaterial
        {
            get
            {
                if (clothMaterial) return clothMaterial;
                clothMaterial = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m => m.name == "dhow small cloth paint")
                    ?? Resources.FindObjectsOfTypeAll<WindClothSimple>().Select(w => w.GetComponent<SkinnedMeshRenderer>()).Where(r => r && r.sharedMaterial).Select(r => r.sharedMaterial).FirstOrDefault();
                return clothMaterial ? clothMaterial : PackTemplate.GetComponent<MeshRenderer>().sharedMaterial;
            }
        }
        // The rigging's rope (the game's rope lines), else a mooring rope's.
        internal static Material RopeMaterial
        {
            get
            {
                if (ropeMaterial) return ropeMaterial;
                ropeMaterial = Resources.FindObjectsOfTypeAll<RopeEffect>().Select(r => r.GetComponent<LineRenderer>()).Where(l => l && l.sharedMaterial).Select(l => l.sharedMaterial).FirstOrDefault()
                    ?? Resources.FindObjectsOfTypeAll<LineRenderer>().Where(l => l.sharedMaterial && l.sharedMaterial.name.ToLowerInvariant().Contains("rope")).Select(l => l.sharedMaterial).FirstOrDefault();
                return ropeMaterial ? ropeMaterial : ClothMaterial;
            }
        }
        internal static float[] Sides(Vector3[] p) { var o = Ordered(p); return Enumerable.Range(0, 4).Select(i => Vector3.Distance(o[i], o[(i + 1) % 4])).ToArray(); }
        internal static void Cancel(bool notify)
        {
            bool any = Pending.Count > 0;
            foreach (var c in Pending) if (c.knot) UnityEngine.Object.Destroy(c.knot);
            Pending.Clear(); PendingPack = null;
            if (notify && any) Loans.Notify(Cleared);
        }
        // Each frame with a pack in hand: the targeter shows a knot where R would tie; corners wait only while their pack is held.
        // Whether the player's pointer holds a bought canvas pack (set each frame; the knots' look text follows it).
        internal static bool HoldingPack;
        internal static bool HoldsPack(GoPointer pointer) { var held = pointer ? pointer.GetHeldItem() as ShipItem : null; return held && held.sold && IsPack(held); }
        internal static void Watch(GoPointer pointer)
        {
            if (Pending.Count > 0 && (!PendingPack || !PendingPack.held || Pending.Any(c => c.onShip && !c.frame))) Cancel(true);
            var pack = pointer.GetHeldItem() as ShipItem;
            HoldingPack = HoldsPack(pointer);
            if (!HoldingPack) { ShowGhost(null); return; }
            // On a knot R repairs rather than ties: no ghost of a corner there.
            if (PointedKnot(pointer)) { ShowGhost(null); return; }
            Aboard(out var frame, out var walk);
            ShowGhost(Find(new Ray(pointer.transform.position, pointer.transform.forward), frame, walk, pack, out _));
        }

        // A waiting corner: the ghost knot (0.5 m, the targeter's look) on the part, moving with her ship or the world.
        private static GameObject Knot(Corner c)
        {
            var o = new GameObject("canvas corner"); o.transform.SetParent(c.onShip ? c.frame : FloatingOriginManager.instance.transform, false);
            o.transform.SetPositionAndRotation(c.Position, c.Turn);
            KnotLook(o.transform, GhostSize, GhostMaterial, KnotLookName);
            return o;
        }
        // ---- Knots (user, 2026-10-06) ----
        // A corner's knot is the game's mooring-line knot ("paint_att_Rope_Att", in its "paint att paint"), 0.15 m across; the
        // ghost that shows where a corner goes is the same knot, 0.5 m, in the game's targeter green (the game's targeter draws
        // a mesh only at its own size, so the mod draws it). Without the game's knot, a rope ring of the mod's own.
        internal const float KnotSize = .15f, GhostSize = .5f; internal const string KnotLookName = "canvas knot look";
        private static Mesh knotMesh; private static Material knotMaterial; private static float knotLooked = -10;
        internal static Mesh KnotMesh { get { FindKnot(); return knotMesh ? knotMesh : RingMesh; } }
        internal static Material KnotMaterial { get { FindKnot(); return knotMesh && knotMaterial ? knotMaterial : RopeMaterial; } }
        private static void FindKnot()
        {
            if (knotMesh || Time.realtimeSinceStartup - knotLooked < 5) return;
            knotLooked = Time.realtimeSinceStartup;
            var filter = Resources.FindObjectsOfTypeAll<MeshFilter>().FirstOrDefault(f => f && f.sharedMesh && f.sharedMesh.name == "paint_att_Rope_Att" && f.GetComponent<MeshRenderer>() && f.GetComponent<MeshRenderer>().sharedMaterial);
            if (!filter) return;
            knotMesh = filter.sharedMesh; knotMaterial = filter.GetComponent<MeshRenderer>().sharedMaterial;
            Plugin.Log?.LogInfo("Canvas: corner knots are the game's " + knotMesh.name + " in " + knotMaterial.name + ".");
        }
        // The knot drawn `size` across, lying on the part in front of the corner (the corner's +z points into the part).
        internal static GameObject KnotLook(Transform parent, float size, Material material, string name)
        {
            var mesh = KnotMesh; var b = mesh.bounds; float s = size / Mathf.Max(b.size.x, b.size.y, b.size.z, 1e-3f);
            var o = new GameObject(name); o.transform.SetParent(parent, false); o.transform.localScale = Vector3.one * s;
            o.transform.localPosition = -b.center * s - new Vector3(0, 0, b.extents.z * s);
            o.AddComponent<MeshFilter>().sharedMesh = mesh; o.AddComponent<MeshRenderer>().sharedMaterial = material;
            return o;
        }
        private static Material targeterMaterial;
        internal static Material GhostMaterial
        {
            get
            {
                if (targeterMaterial) return targeterMaterial;
                var targeter = UnityEngine.Object.FindObjectOfType<GoPointerTargeter>();
                targeterMaterial = targeter ? Fields.Get<Material>(targeter, "targeterMaterial") : null;
                return targeterMaterial ? targeterMaterial : PackTemplate.GetComponent<MeshRenderer>().sharedMaterial;
            }
        }
        // The aiming ghost: one object, moved to the spot each frame a held pack finds one, hidden otherwise.
        private static GameObject ghost; private static Mesh ghostMesh;
        internal static GameObject Ghost => ghost;
        private static void ShowGhost(Corner corner)
        {
            if (corner == null) { if (ghost) ghost.SetActive(false); return; }
            if (!ghost || ghostMesh != KnotMesh)
            {
                if (ghost) UnityEngine.Object.Destroy(ghost);
                ghost = new GameObject("canvas corner ghost"); ghostMesh = KnotMesh; KnotLook(ghost.transform, GhostSize, GhostMaterial, KnotLookName);
            }
            ghost.transform.SetPositionAndRotation(corner.Position, corner.Turn); ghost.SetActive(true);
        }
        private static Mesh ringMesh;
        // A rope ring 10 cm across standing off the part (local +z into the part, as the game's wall attach faces).
        internal static Mesh RingMesh
        {
            get
            {
                if (ringMesh) return ringMesh;
                const int a = 16, b = 8; const float R = .05f, r = .02f;
                var v = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
                for (int i = 0; i <= a; i++) for (int j = 0; j <= b; j++)
                {
                    float u = 2 * Mathf.PI * i / a, w = 2 * Mathf.PI * j / b;
                    v.Add(new Vector3((R + r * Mathf.Cos(w)) * Mathf.Cos(u), (R + r * Mathf.Cos(w)) * Mathf.Sin(u), -r - r * Mathf.Sin(w)));
                    uv.Add(new Vector2(4f * i / a, (float)j / b));
                }
                for (int i = 0; i < a; i++) for (int j = 0; j < b; j++)
                {
                    int p = i * (b + 1) + j, q = p + b + 1;
                    t.AddRange(new[] { p, q, p + 1, p + 1, q, q + 1 });
                }
                ringMesh = new Mesh { name = "canvas knot" }; ringMesh.SetVertices(v); ringMesh.SetUVs(0, uv); ringMesh.SetTriangles(t, 0); ringMesh.RecalculateNormals(); ringMesh.RecalculateBounds();
                return ringMesh;
            }
        }
    }

    // A tied tarp: its corners (in order, in its parent's frame), layout, cloth and knots.
    internal sealed class Tarp : MonoBehaviour
    {
        internal static readonly List<Tarp> All = new List<Tarp>();
        internal bool onShip; internal Vector3[] corners; internal Quaternion[] turns; internal Tarps.Layout layout; internal Cloth cloth; internal GameObject[] knots;
        // Wear: its health when tied (or loaded) and the game's clock then; torn at 0. Worked out from the clock, so a tarp on a
        // ship switched off far away wears too (it tears once she is back).
        internal float startHealth = Tarps.FullHealth; internal double startClock; internal bool torn; internal Material material; internal Texture2D texture; internal float shown = -1;
        internal float Health => torn ? 0 : Rules.TarpHealth(startHealth, startClock, Tarps.Now, Settings.TarpWearing);
        // The option "Tarp wearing" changed: the health so far (worn as the option was) becomes the start, from now.
        internal void Rebase(bool wasWearing)
        {
            if (!torn) startHealth = Rules.TarpHealth(startHealth, startClock, Tarps.Now, wasWearing);
            startClock = Tarps.Now;
        }
        // The cover shape: the modelled sheet, then what the cloth settled to 2 s after tying (in the cloth's own frame).
        internal Vector3[] coverShape; internal Bounds coverBounds;
        private bool capped;
        internal void Caps()
        {
            if (capped || !cloth || cloth.coefficients.Length != layout.cap.Length) return;
            cloth.coefficients = layout.cap.Select(m => new ClothSkinningCoefficient { maxDistance = m, collisionSphereDistance = 0 }).ToArray(); capped = true;
        }
        private void Update()
        {
            if (!capped) Caps();
            float health = Health;
            if (health <= 0 && !torn) { Tarps.Tear(this); return; }
            if (Mathf.Abs(health - shown) >= .25f) Tarps.Fade(this, health);
        }
        internal void SetCover(Vector3[] shape) { coverShape = shape; coverBounds = new Bounds(shape[0], Vector3.zero); foreach (var v in shape) coverBounds.Encapsulate(v); }
        private System.Collections.IEnumerator Start()
        {
            yield return new WaitForSeconds(2);
            if (!torn && cloth && cloth.vertices.Length == layout.points.Length) { SetCover(cloth.vertices); Tarps.RecheckUnder(this); }
        }
        private void Awake() => All.Add(this);
        private void OnDestroy() { All.Remove(this); if (material) Destroy(material); if (texture) Destroy(texture); }
    }

    // The pack's look is its 70% child (a knot's, its sized child): once the game has given the button its outline
    // (GoPointerButton.Start), the outline draws the child, as the game's ItemTriggerOverride redirects one.
    internal sealed class PackLook : MonoBehaviour
    {
        internal string child = Tarps.LookName;
        private void Update()
        {
            var outline = GetComponent<cakeslice.Outline>(); var look = transform.Find(child); if (!outline || !look) return;
            outline.meshFilter = look.GetComponent<MeshFilter>(); outline.meshRenderer = look.GetComponent<MeshRenderer>(); enabled = false;
        }
    }

    // A corner knot: the pointer's look text "untie canvas"; a click unties the tarp. With a bought canvas pack in hand (and
    // only then, user 2026-10-09) a second line: R repairs it (TarpTiePatch).
    internal sealed class TarpKnot : GoPointerButton
    {
        internal const string UntieText = "untie canvas", RepairText = UntieText + "\nR: repair canvas";
        internal Tarp tarp; internal int corner;
        public override void OnActivate(GoPointer activatingPointer) => Tarps.Untie(tarp, corner, activatingPointer);
        // The game's per-frame hook on every button (GoPointerButton.LateUpdate): by what the pointer looking at it holds (the
        // game's pointedAtBy), else by the player's pointer, so the text is ready before it is looked at.
        public override void ExtraLateUpdate()
        {
            var by = isLookedAt ? pointedAtBy : null;
            lookText = (by ? Tarps.HoldsPack(by) : Tarps.HoldingPack) ? RepairText : UntieText;
        }
    }

    // R with a canvas pack in hand ties a corner (the game's R on a held item calls this; a plain item does nothing with it).
    [HarmonyPatch(typeof(GoPointerButton), "OnAltActivate", new[] { typeof(GoPointer) })] internal static class TarpTiePatch
    {
        static bool Prefix(GoPointerButton __instance, GoPointer activatingPointer)
        {
            if (!(__instance is ShipItem pack) || !activatingPointer || activatingPointer.GetHeldItem() != pack || !pack.sold || !Tarps.IsPack(pack)) return true;
            // On a corner knot (the game's pointer points at it while a pack is held): repair that tarp; anywhere else: tie.
            var knot = Tarps.PointedKnot(activatingPointer);
            if (knot && knot.tarp) { var repaired = Tarps.Repair(knot.tarp, pack); if (repaired != null) Loans.Notify(repaired); return false; }
            Tarps.Aboard(out var frame, out var walk);
            var notice = Tarps.Tie(pack, new Ray(activatingPointer.transform.position, activatingPointer.transform.forward), frame, walk);
            if (notice != null) Loans.Notify(notice);
            return false;
        }
    }
    // R with the hammer on a nailed item un-nails it (the game's ShipItemHammer.OnAltActivate); a tarp tied to it is then untied.
    [HarmonyPatch(typeof(ShipItemHammer), "OnAltActivate", new Type[0])] internal static class TarpUnnailPatch
    {
        // First of every prefix: another mod's prefix may un-nail the item itself (Dizzy's Nudge does, and skips the game's
        // code), after which the item no longer reads as nailed. The postfix runs whoever un-nailed it.
        [HarmonyPriority(Priority.First), HarmonyBefore("com.dizzy.sailwind.nudge")]
        static void Prefix(ShipItemHammer __instance, out ShipItem __state)
        {
            var item = __instance && __instance.sold && __instance.held ? __instance.held.GetPointedAtItem() : null;
            __state = item && item.nailed ? item : null;
        }
        static void Postfix(ShipItem __state)
        {
            if (!__state || __state.nailed || !Tarps.Registered) return;
            int untied = Tarps.UntieFrom(__state);
            Plugin.Log?.LogInfo("Hammer un-nailed " + __state.name + ": " + untied + " canvas tarp(s) untied from it.");
            if (WoodPoles.IsPole(__state)) Tarps.PoleMoved(__state, false);   // a tarp it held up drops back
        }
    }
    [HarmonyPatch(typeof(GoPointer), "LateUpdate")] internal static class TarpAimPatch
    { static void Postfix(GoPointer __instance) { if (Tarps.Registered) Tarps.Watch(__instance); } }
    // A canvas pack is held low (user, 2026-10-06), so the spot a corner would go stays in view: the game keeps a two-handed
    // item where it was in the view when picked up (GoPointer.PickUpItem); a pack goes 1 m ahead and 0.6 m below the eye, level.
    [HarmonyPatch(typeof(GoPointer), "PickUpItem")] internal static class PackHoldPatch
    {
        internal static readonly Vector3 Hold = new Vector3(0, -.6f, 1);
        static void Postfix(GoPointer __instance, PickupableItem item)
        {
            if (!item || !item.big || !(item is ShipItem pack) || !Tarps.IsPack(pack)) return;
            try { Fields.Set(__instance, "bigItemLocalPos", Hold); Fields.Set(__instance, "decolLocalPos", Hold); Fields.Set(__instance, "bigItemLocalRot", Quaternion.identity); }
            catch (Exception e) { Plugin.Log?.LogWarning("Canvas: the pack's hold point not set; it is held where it was picked up. " + e.Message); }
        }
    }

    // A tied tarp: its ship's save index (Pledges.Index) or -1 on land, and its four corners in order (x, y, z each) with their
    // knots' turns (x, y, z, w each): in the ship's boarding frame, or real world positions and world turns on land.
    [Serializable] public sealed class TarpState
    {
        public int ship = -1;
        public float[] corners;
        public float[] turns;
        // The health it has lost (0 new, 100 torn): a record without it reads as new.
        [System.Runtime.Serialization.OptionalField] public float wear;
    }
}

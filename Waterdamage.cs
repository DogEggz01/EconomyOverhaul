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
        // Fragile cargo's cracks (crack mockup 10, the user 2026-10-16): the game's hull damage texture shows a texel where its
        // alpha passes the cut. The game's own cut (0.033 x the damage) marks 17% of the surface at full damage, two thirds of
        // it by 30%; this cut marks a share growing evenly from 1% at damage 1 to the same 17% at 100: the texture alpha's
        // quantiles at damage 0.01, 0.1, 0.2 ... 1 ("damage tex 4" read from the game's files). 2 (nothing) at 0.
        private static readonly float[] CrackCuts = { .8471f, .5725f, .4824f, .3804f, .2941f, .2275f, .1647f, .1098f, .0745f, .0471f, .0353f };
        public static float CrackCut(int damagePercent)
        {
            if (damagePercent <= 0) return 2;
            float d = Math.Min(100, damagePercent) / 100f;
            if (d <= .1f) return CrackCuts[0] + (CrackCuts[1] - CrackCuts[0]) * (d - .01f) / .09f;
            float i = d * 10; int k = Math.Min(9, (int)i);
            return CrackCuts[k] + (CrackCuts[k + 1] - CrackCuts[k]) * (i - k);
        }
        // The faces of the cargo box (+X -X +Y -Y +Z -Z) whose planks run along v, as bits, per fragile crate mesh (from
        // -FragileLook's square-on face pictures); -1 for a mesh that gets no cracks.
        public static int CrackGrain(string mesh) => mesh == "crate" ? 0 : mesh == "huge_crate" || mesh == "very_big_crate" ? 51 : -1;
        public static double ConditionFactor(double health, bool alcohol, bool exempt = false)
        {
            health = Clamp(health, 0, 100);
            return exempt || (alcohol ? health > 0 : health >= 90) ? 1 : alcohol ? .1 : .1 + health / 100;
        }
        // Water condition changes in points per game hour (1 game hour = 125 s at normal speed).
        public const double ImmersionPerHour = 12.5;
        // Rain intensity at which fully exposed cargo wets exactly as fast as bilge-water immersion
        // (the vanilla priority-1 storm peak in Al'Ankh; 10 in the other regions). No cap above it.
        public const double RainAtImmersionRate = 8;
        // Rain below this counts as none. Also absorbs Climate's smoothed rain, which stalls at ~4e-43.
        public const float RainThreshold = .1f;
        // Below this condition (more than 80.5% water damage) cargo is ruined and cannot dry. The hint rounds half to even,
        // so exactly 19.5 shows as 80%: everything shown as 80% or less can still dry.
        public const float RuinedCondition = 19.5f;
        public static bool Ruined(double health) => health < RuinedCondition;
        // How far cargo dries back: to 80 (20% water damage); goods on the luxury price curve only to 50 (50%, 60% of
        // their price).
        public const float DryingCeiling = 80, LuxuryDryingCeiling = 50;
        public static float Ceiling(int good) => PriceCurves.TryProfile(good, out var profile) && profile == PriceProfile.Luxury ? LuxuryDryingCeiling : DryingCeiling;
        // In clear weather fully open cargo dries 30 points a game day, 1.25 an hour on average: any 24 h window holds
        // 11 h of day (06:00-17:00) and 13 h of night, whatever the start time. Clear night and cloudy day dry at 3/4 of
        // the clear-day rate, cloudy night at 1/2.
        public const double ClearDayDryingPerHour = 30 / (11 + 13 * .75);
        public static float EffectiveRain(float raw) => float.IsNaN(raw) || float.IsInfinity(raw) || raw < RainThreshold ? 0 : raw;
        // Water aboard (bilge) or the sea damages cargo once it reaches 14% of the cargo's height (user, 2026-10-10; was 5%).
        // Shallower water neither damages nor lets it dry.
        public const double ImmersionStart = .14;
        public static bool DamagingImmersion(double depth, double cargoHeight) => cargoHeight > 0 && depth > 0 && depth >= cargoHeight * ImmersionStart;
        // Proportional to rain with no minimum, scaled by the exposed share of the cargo's top face.
        public static double RainDamagePerHour(double rain, double exposed) =>
            ImmersionPerHour * Math.Max(0, rain) / RainAtImmersionRate * Clamp(exposed, 0, 1);
        public static double OpenDryingPerHour(double clouds, double hour)
        {
            bool day = hour >= 6 && hour < 17, cloudy = clouds > 2;
            return ClearDayDryingPerHour * (day ? (cloudy ? .75 : 1) : (cloudy ? .5 : .75));
        }
        // Only the open share dries, with the weather: drying falls in a straight line to nothing at full cover.
        public static double DryingPerHour(double exposed, double clouds, double hour) => Clamp(exposed, 0, 1) * OpenDryingPerHour(clouds, hour);
        // noDry: sealed liquids (alcohol and water). coverKnown: the shelter scan has run since the
        // cargo settled. exposed: open share of the top face, 0-1. ceiling: how far this good dries back.
        public static float AdvanceCondition(float health, double gameHours, bool noDry, bool waterContact,
            bool damagingWater, double rain, bool coverKnown, double exposed, double clouds, double hour, float ceiling = DryingCeiling)
        {
            gameHours = Math.Max(0, gameHours);
            double damage = (damagingWater ? ImmersionPerHour : 0) + (coverKnown ? RainDamagePerHour(rain, exposed) : 0);
            if (damage > 0) return (float)Math.Max(0, health - gameHours * damage);
            if (waterContact || rain > 0 || noDry || !coverKnown || Ruined(health) || health >= ceiling) return health;
            return (float)Math.Min(ceiling, health + gameHours * DryingPerHour(exposed, clouds, hour));
        }
        // The cargo hint's own lines, added to the item's description below the game's look text. LookUI draws the look text
        // (extraText: name, crate quantity, a mission's destination and due day) down from its anchor, and the description
        // (hintText) centred on an anchor 1.3 look lines lower, in lines 0.6 as tall (QA -HintProbe, 0.39.1). So a blank line
        // before our lines moves them down only half a hint line, 0.3 of a look line, and each line after the first lifts them
        // as much. Three blank lines clear a two-line look text (the crate quantity layout of 0.5.5); each further look line
        // needs 10/3 more, rounded up.
        // cover: the share of the cargo's top under cover in whole percent, or -1 for no line (user, 2026-10-10: an option, off
        // by default). With a one-line look text a third line lifts the block too: one more blank line per line beyond two.
        public static string HintSuffix(string description, string lookText, int percent, bool loan, int cover = -1, int damage = -1)
        {
            var lines = new List<string>(3);
            if (percent >= 0) lines.Add("Water Damage: " + percent.ToString(CultureInfo.InvariantCulture) + "%");
            if (cover >= 0) lines.Add("Cover: " + cover.ToString(CultureInfo.InvariantCulture) + "%");
            // Fragile cargo (option "Show cargo damage percentage", on by default): its damage, 0% intact up to 100% ruined.
            if (damage >= 0) lines.Add("Cargo Damage: " + damage.ToString(CultureInfo.InvariantCulture) + "%");
            if (loan) lines.Add("Respondentia");
            if (lines.Count == 0) return "";
            int look = TextLines(lookText), own = TextLines(description);
            int padding = Math.Max(1, look - own + 1) + Math.Max(0, lines.Count - 2);
            if (look >= 2) padding = Math.Max(padding, 3 + (lines.Count - 1) + ((look - 2) * 10 + 2) / 3 - Math.Max(1, own) + 1);
            return new string('\n', padding) + string.Join("\n", lines);
        }
        private static int TextLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int lines = 1; foreach (char c in text) if (c == '\n') lines++;
            return lines;
        }
    }

    // ---- Cargo condition ----
    [Serializable] public sealed class CargoState
    {
        public int id, prefab, good, parent;
        public string name;
        public float health = 100;
        [NonSerialized] public int exposure; // 0 unknown, 1 covered, 2 exposed; not saved (rescanned after loading).
        public byte[] stainHistory;
        public bool goldLoan;
        public bool loan;
        public int region, principal, interest;
        public double reservation, rate;
        // Currency pressure this unit's trade-book purchase queued (strengthening fxCurrency); a sale of the
        // unit in the same currency queues exactly this back. Zero when there is none to unwind.
        public int fxCurrency;
        public double fxPressure;
        // Fragile cargo (stage 3 of MISSIONS_DRAFT.md): impact damage 0-100 (0 intact; a save from before reads 0), and whether
        // this crate is fragile though its good is not (tools spawned by a fragile mission).
        [System.Runtime.Serialization.OptionalField] public float impact;
        [System.Runtime.Serialization.OptionalField] public bool fragile;
    }
    public sealed class CargoCondition : MonoBehaviour
    {
        internal static readonly HashSet<CargoCondition> Active = new HashSet<CargoCondition>();
        private static readonly HashSet<int> Immune = new HashSet<int> { 3, 20, 21, 22, 23, 33, 34, 35, 48, 55, 56, 57, 61 };
        // Barrelled liquids (water and alcohol) never dry and keep full value until condition 0.
        // Not named Liquids: the game has a static Liquids class for drink effects.
        private static readonly HashSet<int> LiquidCargo = new HashSet<int> { 10, 11, 12, 13, 36, 58 };
        private const int CoverGrid = 5;                                  // 5x5 rays over the top face
        private static readonly int[] CoverFirst = { 0, 4, 20, 24, 12 };  // corner cells and centre cell
        // Rays start this far above the cargo: under the invisible ceilings the cities have for the player (Gold Rock City's
        // COL slab 10-35 m over its streets, its col_1/col_2 at 139 m; the user, 2026-10-11: cargo there read 100% cover in
        // open air; was 400 m). Real shelter (roofs, cabins, tarps, stacked cargo) is within a few metres.
        private const float CoverRayHeight = 15;
        // Layer 12 (OnlyPlayerCol): the invisible walls and ceilings the cities have for the player (Gold Rock City's COL slab is
        // one). Cargo bodies pass through them (the physics settings: layer 12 ignores loose items, 2, and those aboard, 8), so
        // they are never shelter. The player, boats and small items (18) do collide with them.
        private const int PlayerOnlyLayers = 1 << 12;
        private const float UpdateSeconds = 2;                            // condition updates, each crate on its own phase
        private const float MoveRecheck = 100;                            // aboard: one rescan once the boat has gone this far
        private float phase;
        private bool moveRecheck;           // a scan aboard is waiting for the boat to go MoveRecheck from moveFrom
        private Vector3 moveFrom;           // the boat's place at that scan, without world shifts
        private float coveredFraction;      // share of the top face under something; valid while state.exposure != 0
        private bool[] coveredCells;        // per stain-map cell, true = under cover; replaced by each scan, never mutated
        private int coverRecheckAt;         // frame from which a "something on top changed" rescan runs (0 = none)
        // Cargo under this item where it last came to rest, rescanned when it is pushed or slides off.
        // Kept as references, not positions: boats move and the floating origin shifts loose items.
        private bool settled, resting, moved, leftRechecked;
        private List<CargoCondition> beneath, left;
        private float leftAt;
        private ShipItem item;
        private SaveablePrefab save;
        private CargoState state;
        private Renderer[] geometry;
        private CargoStainHistory history;
        private float stainTimer;
        private readonly List<CargoWetVolume> wetVolumes = new List<CargoWetVolume>(8);
        private float sampledSeaHeight;
        private bool seaSampleValid;
        private readonly List<AppearanceBinding> appearance = new List<AppearanceBinding>();
        private float timer, appearanceTimer, visualHealth = -1;
        private double conditionHours;
        private readonly SampleHeightHelper seaHeight = new SampleHeightHelper();
        private Transform exposureBoat;
        private Vector3 exposurePosition;
        private Quaternion exposureRotation;
        private float exposureSettling;
        private bool exposurePoseKnown;
        private bool visualWaterEnabled = true;
        private string suffix;
        private string hintDescription;
        private string hintLookText;
        private int hintPercent = -2, hintCover = -2, hintDamage = -2;
        private bool hintLoan;
        private sealed class AppearanceBinding
        {
            internal MeshRenderer renderer;
            internal Mesh mesh;
            internal Material[] originals, owned;
        }
        private void Awake() { item = GetComponent<ShipItem>(); save = GetComponent<SaveablePrefab>(); }
        internal ShipItem Item => item;
        internal Bounds WorldBounds => Bounds();
        internal float CoveredFraction => coveredFraction;
        internal bool CoverRecheckPending => coverRecheckAt != 0;
        private void ResetConditionClock() { timer = -phase; conditionHours = 0; }
        private void OnEnable()
        {
            uint spread = unchecked((uint)GetInstanceID() * 2654435761u);
            phase = spread % 2000 / 1000f; stainTimer = spread % 3000 / 1000f;
            Active.Add(this); ResetConditionClock(); appearanceTimer = .35f; visualHealth = -1;
        }
        // A place without the world's floating-origin shifts.
        private static Vector3 Real(Vector3 p) => FloatingOriginManager.instance ? FloatingOriginManager.instance.ShiftingPosToRealPos(p) : p;
        private void OnDisable() { history?.Flush(); Active.Remove(this); ResetConditionClock(); RecheckLeft(); }
        private void Start() { Restore(); }
        internal static CargoState Track(ShipItem item)
        {
            if (!item || !item.GetComponent<Good>()) return null;
            var save = item.GetComponent<SaveablePrefab>();
            if (!save || save.instanceId == 0) return null;
            int g = PrefabsDirectory.ItemToGoodIndex(save.prefabIndex);
            if (g < 1 || g > 64 || g == 45) return null;
            var component = item.GetComponent<CargoCondition>() ?? item.gameObject.AddComponent<CargoCondition>();
            if (!World.Ready || World.Loading || World.Removed.Contains(save.instanceId)) return null;
            if (!World.Cargo.TryGetValue(save.instanceId, out var record))
            {
                record = new CargoState { id = save.instanceId, prefab = save.prefabIndex, good = g,
                    parent = save.GetParentObject(), name = item.name };
                World.Cargo.Add(record.id, record); World.State.cargo.Add(record);
            }
            component.state = record; return record;
        }
        internal void Restore()
        {
            if (!World.Ready || World.Loading) return;
            var restored = Track(item); if (restored == null) return;
            if (history != null && !history.BelongsTo(restored)) { history.Dispose(); history = null; }
            state = restored;
            state.health = Mathf.Clamp(state.health, 0, 100); visualHealth = -1; ClearExposure();
            CaptureParent(); RefreshAppearance();
        }
        internal void CaptureParent()
        {
            if (state == null || !save) return;
            int p = save.GetParentObject();
            if (p != -2 && p != -3) state.parent = p;
        }
        internal void PrepareSave() { history?.Flush(); CaptureParent(); }
        // Mesh bounds in cargo-local space; shared by the stain map and the cover grid.
        private Bounds LocalMeshBounds()
        {
            if (geometry == null) geometry = SourceRenderers();
            Bounds local = new Bounds(Vector3.zero, Vector3.zero); bool found = false;
            foreach (var renderer in geometry)
            {
                if (!renderer) continue;
                var filter = renderer.GetComponent<MeshFilter>(); if (!filter || !filter.sharedMesh) continue;
                var b = filter.sharedMesh.bounds; var matrix = transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
                {
                    var p = matrix.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3(x,y,z)));
                    if (!found) { local = new Bounds(p, Vector3.zero); found = true; } else local.Encapsulate(p);
                }
            }
            return found ? local : new Bounds(Vector3.zero, Vector3.one);
        }
        private void EnsureHistory() { if (history == null) history = new CargoStainHistory(state, LocalMeshBounds()); }
        // Goods water can't damage (no Pristine missions for them).
        internal static bool WaterProof(int good) => Immune.Contains(good);
        // A crate's water condition, 100 if it has none or water damage is off.
        internal static float Health(ShipItem item)
        {
            if (!Settings.WaterEnabled || !item) return 100;
            var save = item.GetComponent<SaveablePrefab>();
            return save && World.Cargo.TryGetValue(save.instanceId, out var c) ? c.health : 100;
        }
        // The cargo's value factor: its water condition (when water damage is on) x, for fragile cargo, how intact it is.
        internal static float Factor(ShipItem item)
        {
            if (!item) return 1;
            var save = item.GetComponent<SaveablePrefab>();
            if (!save || !World.Cargo.TryGetValue(save.instanceId, out var c)) return 1;
            float water = Settings.WaterEnabled ? (float)Rules.ConditionFactor(c.health, LiquidCargo.Contains(c.good), Immune.Contains(c.good)) : 1;
            return water * (Fragile(item, c) ? Rules.IntactFactor(c.impact) : 1);
        }
        // Fragile: gems, sculptures, marble, or a crate of a fragile mission (remembered on the crate, so it stays fragile).
        // With the option "Enable fragile cargo" off nothing is: no hull damage, no damage line or cracks, full value.
        internal static bool Fragile(ShipItem item, CargoState c)
        {
            if (c == null || !Settings.FragileEnabled) return false;
            if (Rules.FragileGood(c.good) || c.fragile) return true;
            var mission = item ? item.GetComponent<Good>()?.GetAssignedMission() : null;
            if (mission == null) return false;
            Missions.Info(mission, out _, out var mods);
            return c.fragile = (mods & MissionModifier.Fragile) != 0;
        }
        internal static float Impact(ShipItem item)
        {
            var save = item ? item.GetComponent<SaveablePrefab>() : null;
            return save && World.Cargo.TryGetValue(save.instanceId, out var c) && Fragile(item, c) ? c.impact : 0;
        }
        // A counted hull hit (BoatDamage.Impact added `added` to the hull's damage): every fragile crate aboard that ship.
        internal static void HullHit(BoatDamage hull, float added)
        {
            foreach (var c in Active)
            {
                if (!c || !c.item || c.state == null || !c.item.currentActualBoat || c.item.currentActualBoat.GetComponentInParent<BoatDamage>() != hull) continue;
                if (!Fragile(c.item, c.state)) continue;
                c.state.impact = Rules.FragileDamage(c.state.impact, added); c.RefreshHint(); c.RefreshAppearance();
            }
        }
        private bool Placed => item && save && item.sold && !item.held && save.currentCrateId == 0 &&
            item.GetCurrentInventorySlot() < 0 && item.itemRigidbodyC && save.GetParentObject() != -2 && save.GetParentObject() != -3;
        private Bounds Bounds()
        {
            if (geometry == null) geometry = SourceRenderers();
            var b = new Bounds(transform.position, Vector3.zero); bool found = false;
            foreach (var r in geometry)
            {
                if (!r || !r.enabled || !r.gameObject.activeInHierarchy || r.GetComponentInParent<ShipItem>() != item) continue;
                if (!found) { b = r.bounds; found = true; } else b.Encapsulate(r.bounds);
            }
            return b;
        }
        internal void ClearExposure()
        {
            if (state != null) state.exposure = 0;
            exposurePoseKnown = false; exposureSettling = 0;
            coveredFraction = 0; coveredCells = null; coverRecheckAt = 0; moveRecheck = false;
            // Pickup, drop, inventory and cart have their own "something on top" hooks; this covers
            // being caught or put away while sliding off other cargo.
            RecheckLeft(); settled = resting = false; beneath = null;
        }
        // The cargo it was pushed off is rescanned once it lands, is put away or is destroyed on the way.
        private void RecheckLeft() { if (moved) CoverEvents.Recheck(left); moved = false; left = null; }
        private void UpdateExposure()
        {
            // Still moving, or floating away and never settling: rescan what it left after two seconds.
            if (moved && !leftRechecked && Time.time - leftAt > 2) { leftRechecked = true; CoverEvents.Recheck(left); }
            var boat = item.currentActualBoat;
            // Loose cargo is compared without the world's shifts, which move it without anything changing around it.
            var position = boat ? boat.InverseTransformPoint(transform.position) : Real(transform.position);
            var rotation = boat ? Quaternion.Inverse(boat.rotation) * transform.rotation : transform.rotation;
            if (!exposurePoseKnown || boat != exposureBoat || (position - exposurePosition).sqrMagnitude > .0025f ||
                Quaternion.Angle(rotation, exposureRotation) > 2)
            {
                state.exposure = 0; exposureSettling = 0; exposurePoseKnown = true; settled = false;
                exposureBoat = boat; exposurePosition = position; exposureRotation = rotation;
                // Pushed, slid or knocked off the spot it rested on: what it covered may now be open.
                if (resting) { resting = false; moved = true; leftRechecked = false; left = beneath; beneath = null; leftAt = Time.time; }
                return;
            }
            // Sample once the placement settles, not at the hand position before a throw lands.
            // Ship movement itself does not invalidate a placement fixed relative to its boat.
            if (settled || (exposureSettling += Time.deltaTime) < .35f) return;
            settled = resting = true;
            beneath = CoverEvents.Beneath(item, Bounds());
            // Came to rest after being pushed off another spot: rescan that spot again (the two-second
            // rescan may have caught it half way off) and the cargo it now rests on.
            if (moved) { RecheckLeft(); CoverEvents.Recheck(beneath); }
            Placement();
        }
        internal void RefreshSettings() { ResetConditionClock(); appearanceTimer = 0; visualHealth = -1; ClearExposure(); RefreshAppearance(); RefreshHint(); }
        // Runs once the cargo has settled (UpdateExposure) and is cached until it moves.
        internal void Placement()
        {
            if (!Settings.WaterEnabled || state == null || Immune.Contains(state.good) || !Placed) return;
            ScanCover(full: false);
        }
        // Something was set on, lifted off or removed from above this cargo. Runs a frame later
        // so a destroyed item is gone and a lifted one is already in a hand.
        internal void RequestCoverRecheck() { if (state != null && state.exposure != 0) coverRecheckAt = Time.frameCount + 1; }
        // Downward rays over a 5x5 grid on the cargo's outline seen from above; each blocked ray covers 1/25 of it. Corners
        // and centre first: if they agree (open deck or a full roof) the rest take the same value, unless something was
        // just set on top, when all 25 are cast so a small item still counts. A scan aboard (other than the one the boat's
        // own move asks for) is checked once more after the boat has gone 100 m: a pier or a cliff it found may be behind.
        private void ScanCover(bool full, bool afterMove = false)
        {
            var boat = item.currentActualBoat; var walk = item.currentWalkCol;
            // Straight up in the world: whatever lies above the cargo counts, whatever the boat's heel or the cargo's tilt.
            Vector3 up = Vector3.up;
            moveRecheck = boat && !afterMove; if (moveRecheck) moveFrom = Real(boat.position);
            Bounds local = LocalMeshBounds();
            Matrix4x4 m = transform.localToWorldMatrix;
            var frame = CoverFrame.Of(m, local, up);
            const int excluded = PlayerOnlyLayers | (1 << 2) | (1 << 4) | (1 << 5) | (1 << 8) | (1 << 11) | (1 << 13) |
                (1 << 14) | (1 << 16) | (1 << 21) | (1 << 22) | (1 << 23) | (1 << 24) | (1 << 26) | (1 << 27);
            Physics.SyncTransforms();
            Vector3 walkUp = boat && walk ? walk.TransformDirection(boat.InverseTransformDirection(up)).normalized : up;
            // Visible item colliders are triggers; their solid physics copies sit on Ignore Raycast
            // (loose items) or on the boat's walk colliders. The second ray counts only those copies,
            // so cargo stacked on a dock or in a warehouse covers what is below it.
            bool Blocked(int n)
            {
                Vector3 p = frame.Point(n % CoverGrid, n / CoverGrid, CoverGrid);
                return Covered(p, up, ~excluded, null) || Covered(p, up, 1 << 2, null, itemsOnly: true) ||
                    boat && walk && Covered(walk.TransformPoint(boat.InverseTransformPoint(p)), walkUp, ~PlayerOnlyLayers, walk) ||
                    Tarps.CoverAbove(p, CoverRayHeight);   // canvas tarps and the ships' cloth roofs (no colliders)
            }
            var rays = new bool[CoverGrid * CoverGrid];
            foreach (int n in CoverFirst) rays[n] = Blocked(n);
            bool uniform = CoverFirst.All(n => rays[n] == rays[CoverFirst[0]]);
            for (int n = 0; n < rays.Length; n++)
                if (Array.IndexOf(CoverFirst, n) < 0) rays[n] = full || !uniform ? Blocked(n) : rays[CoverFirst[0]];
            coveredFraction = rays.Count(r => r) / (float)rays.Length;
            coveredCells = frame.MapCells(rays, CoverGrid, local, m);
            state.exposure = coveredFraction >= 1 ? 1 : 2;
        }
        // The cargo's footprint seen along `up`: the two cargo axes nearest horizontal, flattened.
        private struct CoverFrame
        {
            internal Vector3 a, b, up;
            internal float minA, maxA, minB, maxB, top;
            internal static CoverFrame Of(Matrix4x4 m, Bounds local, Vector3 up)
            {
                var f = new CoverFrame { up = up, minA = float.MaxValue, maxA = float.MinValue,
                    minB = float.MaxValue, maxB = float.MinValue, top = float.MinValue };
                int vertical = 0;
                for (int i = 1; i < 3; i++)
                    if (Mathf.Abs(Vector3.Dot(((Vector3)m.GetColumn(i)).normalized, up)) >
                        Mathf.Abs(Vector3.Dot(((Vector3)m.GetColumn(vertical)).normalized, up))) vertical = i;
                f.a = Vector3.ProjectOnPlane(m.GetColumn((vertical + 1) % 3), up).normalized;
                f.b = Vector3.Cross(up, f.a);
                for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 w = m.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3(x, y, z)));
                    float da = Vector3.Dot(w, f.a), db = Vector3.Dot(w, f.b);
                    f.minA = Mathf.Min(f.minA, da); f.maxA = Mathf.Max(f.maxA, da);
                    f.minB = Mathf.Min(f.minB, db); f.maxB = Mathf.Max(f.maxB, db);
                    f.top = Mathf.Max(f.top, Vector3.Dot(w, up));
                }
                return f;
            }
            // Centre of grid cell (i, j), 1 cm above the cargo's highest point.
            internal Vector3 Point(int i, int j, int grid) =>
                a * Mathf.Lerp(minA, maxA, (i + .5f) / grid) + b * Mathf.Lerp(minB, maxB, (j + .5f) / grid) + up * (top + .01f);
            // Each stain-map cell takes the value of the grid cell it sits under.
            internal bool[] MapCells(bool[] rays, int grid, Bounds local, Matrix4x4 m)
            {
                var cells = new bool[CargoStainHistory.Cells];
                for (int n = 0; n < cells.Length; n++)
                {
                    Vector3 w = m.MultiplyPoint3x4(CargoStainHistory.CellPoint(local, n));
                    int i = Mathf.Clamp((int)((Vector3.Dot(w, a) - minA) / Mathf.Max(1e-4f, maxA - minA) * grid), 0, grid - 1);
                    int j = Mathf.Clamp((int)((Vector3.Dot(w, b) - minB) / Mathf.Max(1e-4f, maxB - minB) * grid), 0, grid - 1);
                    cells[n] = rays[i + grid * j];
                }
                return cells;
            }
        }
        private bool Covered(Vector3 target, Vector3 up, int mask, Transform required, bool itemsOnly = false)
        {
            foreach (var hit in Physics.RaycastAll(target + up * CoverRayHeight, -up, CoverRayHeight, mask, QueryTriggerInteraction.Ignore))
            {
                if (required && !hit.transform.IsChildOf(required)) continue;
                var copy = hit.collider.GetComponentInParent<ItemRigidbody>();
                if (itemsOnly && !copy) continue;
                if (hit.transform.IsChildOf(transform) || copy == item.itemRigidbodyC) continue;
                if (CoverEvents.NotCover(hit.collider)) continue;   // an item in a hand, the inventory or the cart is never a roof
                return true;
            }
            return false;
        }
        private void LateUpdate()
        {
            if (!World.Ready || World.Loading || Startup.BlocksGameplay || GameState.currentlyLoading) { ResetConditionClock(); return; }
            if (state == null || !World.Cargo.TryGetValue(save.instanceId, out var current) || current != state) Restore();
            if (state == null) { ResetConditionClock(); return; }
            stainTimer += Time.unscaledDeltaTime;
            if (stainTimer >= CargoStainHistory.UpdateSeconds)
            {
                stainTimer %= CargoStainHistory.UpdateSeconds;
                if (history != null && history.Flush()) visualHealth = -1;
            }
            RefreshHint();
            appearanceTimer += Time.unscaledDeltaTime;
            float targetHealth = Settings.WaterEnabled ? state.health : 100;
            if (appearanceTimer >= .35f || visualHealth >= 0 && Math.Abs(visualHealth - targetHealth) >= .1f || visualWaterEnabled != Settings.WaterEnabled)
                RefreshAppearance();
            if (!Settings.WaterEnabled) { ResetConditionClock(); return; }
            if (!Placed || GameState.loadingBoatLocalItems || GameState.loadingScenes != 0) { ResetConditionClock(); return; }
            // A rescan after something was set on, lifted off or removed from above also runs while the
            // clock is paused (the trade book pauses it while placing an order on existing cargo).
            if (coverRecheckAt != 0 && Time.frameCount >= coverRecheckAt && state.exposure != 0) { coverRecheckAt = 0; ScanCover(full: true); }
            if (!Sun.sun || Sun.SunPaused()) { ResetConditionClock(); return; }
            // Immune cargo tracks where it rests too, so cargo under it is rescanned when it is pushed off.
            UpdateExposure();
            if (Immune.Contains(state.good)) { ResetConditionClock(); return; }
            // Crest's asynchronous height queries must be submitted every frame to
            // receive the previous frame's result; quarter-second polling never settles.
            bool loose = !item.currentActualBoat;
            Bounds b = loose ? Bounds() : default;
            float depth = loose ? WaterDepth(b) : 0;
            // Same elapsed game hours as Sun.Update: deltaTime includes sleep's
            // simulation acceleration; Sun.timescale includes its additional clock skip.
            // Accumulate each frame so changing sleep state cannot rescale an earlier tick.
            conditionHours += (double)Time.deltaTime * Mathf.Max(0, Sun.sun.timescale);
            timer += Time.deltaTime;
            if (timer < UpdateSeconds) return;
            double elapsedHours = conditionHours; conditionHours = 0; timer = 0;
            var boat = item.currentActualBoat;
            if (moveRecheck && boat && state.exposure != 0 && (Real(boat.position) - moveFrom).sqrMagnitude > MoveRecheck * MoveRecheck)
                ScanCover(full: false, afterMove: true);
            wetVolumes.Clear();
            if (!loose) { b = Bounds(); depth = BoatWater.Depth(boat, b, wetVolumes, transform.localToWorldMatrix); }
            else if (seaSampleValid && depth > 0) wetVolumes.Add(CargoWetVolume.Sea(sampledSeaHeight, transform.localToWorldMatrix));
            // The weather is where the player is: rain and drying run only within 1 km of them. Water aboard or the sea wets
            // cargo anywhere.
            bool near = CargoWeather.Near(transform.position);
            float clouds = CargoWeather.Clouds, rain = near ? CargoWeather.Rain : 0;
            bool damagingWater = Rules.DamagingImmersion(depth, b.size.y);
            double hour = Sun.sun.localTime;
            bool coverKnown = state.exposure != 0;
            double exposed = coverKnown ? 1 - coveredFraction : 0;
            double waterDamage = damagingWater ? elapsedHours * Rules.ImmersionPerHour : 0;
            // Stains: open cells get the full rain rate, covered cells none (the history applies the mask).
            double rainDamage = coverKnown && exposed > 0 ? elapsedHours * Rules.RainDamagePerHour(rain, 1) : 0;
            // Surface moisture may finish evaporating at the ceiling; the permanent stain never decreases. Liquids and
            // ruined cargo cannot dry; open cells dry with the weather and time of day, covered cells not at all.
            bool canDry = near && !LiquidCargo.Contains(state.good) && !Rules.Ruined(state.health) && depth <= 0 && rain == 0
                && coverKnown && exposed > 0;
            double dryOpen = canDry ? elapsedHours * Rules.OpenDryingPerHour(clouds, hour) : 0;
            if (waterDamage > 0 || rainDamage > 0 || history != null && canDry)
            {
                EnsureHistory();
                history.Accumulate(wetVolumes, waterDamage, rainDamage, dryOpen, coveredCells, transform.localToWorldMatrix, b);
            }
            state.health = Rules.AdvanceCondition(state.health, elapsedHours, LiquidCargo.Contains(state.good) || !near, depth > 0,
                damagingWater, rain, coverKnown, exposed, clouds, hour, Rules.Ceiling(state.good));
            CaptureParent(); if (Math.Abs(visualHealth - state.health) >= .1f) RefreshAppearance();
        }
        private float WaterDepth(Bounds bounds)
        {
            // Onboard cargo uses water inside its own hull. Ocean height must not wet a dry hold.
            if (item.currentActualBoat) return BoatWater.Depth(item.currentActualBoat, bounds);
            seaSampleValid = false;
            // Well above the sea (on a quay, a hill or a roof) no wave reaches it: no height query.
            if (!OceanRenderer.Instance || bounds.min.y > OceanRenderer.Instance.transform.position.y + 5) return 0;
            seaHeight.Init(bounds.center, .2f);
            seaSampleValid = seaHeight.Sample(out sampledSeaHeight);
            return seaSampleValid ? Mathf.Max(0, sampledSeaHeight - bounds.min.y) : 0;
        }
        internal void RefreshHint()
        {
            if (!item || state == null) return;
            // Water damage, 0% undamaged to 100%; ruined (health below 19.5) shows 81% or more.
            int percent = Settings.WaterEnabled && Settings.WaterHintEnabled && !Immune.Contains(state.good)
                ? Rules.WaterHintPercent(state.health) : -1;
            // Cover, 0% open sky to 100% fully covered, once its cover is scanned (option "Show cover percentage", off by default).
            int cover = Settings.WaterEnabled && Settings.CoverHintEnabled && !Immune.Contains(state.good) && state.exposure != 0
                ? Mathf.RoundToInt(100 * Mathf.Clamp01(coveredFraction)) : -1;
            // Fragile cargo's damage line (-1 for none) counts too: a hull hit changes only it (fragile goods are water-proof).
            int damage = Settings.CargoDamageHintEnabled && Fragile(item, state) ? Rules.DamagePercent(state.impact) : -1;
            if (hintPercent == percent && hintCover == cover && hintDamage == damage && hintLoan == state.loan && item.description == hintDescription && hintLookText == item.lookText) return;
            // Never replace vanilla mission/name/fullness information.
            if (!string.IsNullOrEmpty(suffix) && item.description != null && item.description.EndsWith(suffix, StringComparison.Ordinal))
                item.description = item.description.Substring(0, item.description.Length - suffix.Length);
            suffix = Rules.HintSuffix(item.description, item.lookText, percent, state.loan, cover, damage);
            hintDescription = item.description = (item.description ?? "") + suffix;
            hintLookText = item.lookText;
            hintPercent = percent; hintCover = cover; hintDamage = damage; hintLoan = state.loan;
        }
        internal void RefreshAppearance()
        {
            if (state == null) return;
            appearanceTimer = 0;
            // Water: goods water can damage. Cracks: fragile cargo with impact damage, on the crates that have crack marks.
            float health = Settings.WaterEnabled && !Immune.Contains(state.good) ? state.health : 100;
            var sources = SourceRenderers();
            int grain = state.impact > 0 && Fragile(item, state) ? CargoStainMaterial.CrackGrain(sources) : -1;
            var cracks = grain >= 0 ? CargoStainMaterial.CrackTexture() : null;
            bool looked = health < 100 || cracks;
            bool stale = !BindingsMatch(sources);
            if (stale)
            {
                ReleaseBindings(); geometry = sources;
                if (looked) BindSources(sources);
            }
            else geometry = sources;
            if (!looked)
            {
                ReleaseBindings(); geometry = sources;
                visualHealth = 100; visualWaterEnabled = Settings.WaterEnabled; appearanceTimer = 0; return;
            }
            if (appearance.Count == 0 || !appearance.Any(b => b.owned != null && b.owned.Any(m => m)))
            { visualHealth = -1; return; }
            EnsureHistory();
            if (!history.Texture || history.Dirty && appearance.Any(b => b.renderer && b.renderer.isVisible)) history.Upload();
            Vector3 size = history.LocalBounds.size;
            foreach (var binding in appearance)
            {
                for (int i = 0; i < binding.owned.Length; i++)
                {
                    var material = binding.owned[i]; if (!material) continue;
                    material.SetTexture(CargoStainMaterial.HistoryId,history.Texture);
                    material.SetTexture(CargoStainMaterial.NoiseId,CargoStainMaterial.Noise);
                    material.SetMatrix(CargoStainMaterial.MatrixId,transform.worldToLocalMatrix * binding.renderer.transform.localToWorldMatrix);
                    material.SetVector(CargoStainMaterial.MinId,history.LocalBounds.min);
                    material.SetVector(CargoStainMaterial.SizeId,new Vector4(1/Mathf.Max(.001f,size.x),1/Mathf.Max(.001f,size.y),1/Mathf.Max(.001f,size.z),0));
                    material.SetFloat(CargoStainMaterial.VisibleId,health <= 90 ? 1 : 0);
                    material.SetTexture(CargoStainMaterial.CrackId,cracks ? cracks : Texture2D.blackTexture);
                    material.SetFloat(CargoStainMaterial.CrackCutId,cracks ? Rules.CrackCut(Rules.DamagePercent(state.impact)) : 2);
                    material.SetFloat(CargoStainMaterial.CrackGrainId,Mathf.Max(0,grain));
                    material.SetFloat(CargoStainMaterial.CrackSeedId,Mathf.Repeat(state.id * .618034f,1));
                    material.SetColor(CargoStainMaterial.CrackColorId,CargoStainMaterial.CrackColor());
                }
            }
            visualHealth = health; visualWaterEnabled = Settings.WaterEnabled; appearanceTimer = 0;
        }
        private MeshRenderer[] SourceRenderers()
        {
            return GetComponentsInChildren<MeshRenderer>(true)
                .Where(r => r && r.GetComponentInParent<ShipItem>() == item && Own(r) && Opaque(r)).ToArray();
        }
        // Only the cargo's own surfaces get the wet look: renderers at a place its prefab has one. A mark another mod adds later
        // keeps its own look (NANDTweaks 1.7.5's mission label: a copy of the crate's mesh, "<size>(Clone)", with an opaque
        // Standard material at queue 2000, which the queue rule below cannot tell from the crate). No prefab found: all count.
        private HashSet<string> ownParts;
        internal const int WetQueue = 1999;   // the wet look's material queue (see BindSources)
        private bool Own(MeshRenderer r)
        {
            if (ownParts == null)
            {
                ownParts = new HashSet<string>();
                var directory = PrefabsDirectory.instance ? PrefabsDirectory.instance.directory : null;
                var prefab = save && directory != null && save.prefabIndex >= 0 && save.prefabIndex < directory.Length ? directory[save.prefabIndex] : null;
                if (prefab) foreach (var part in prefab.GetComponentsInChildren<MeshRenderer>(true)) ownParts.Add(PartPath(prefab.transform, part.transform));
            }
            return ownParts.Count == 0 || ownParts.Contains(PartPath(transform, r.transform));
        }
        private static string PartPath(Transform root, Transform part)
        {
            var path = ""; for (; part && part != root; part = part.parent) path = "/" + part.name + path;
            return path;
        }
        // A cut-out or transparent part keeps its material too: the stain shader is opaque, so an overlay would turn solid and
        // fight the crate's surface for the same pixels.
        private bool Opaque(MeshRenderer r)
        {
            var bound = appearance.Find(b => b.renderer == r);
            return (bound != null ? bound.originals : r.sharedMaterials).All(m => !m || m.renderQueue <= 2000);
        }
        private bool BindingsMatch(MeshRenderer[] sources)
        {
            if (appearance.Count != sources.Length) return false;
            for (int n = 0; n < sources.Length; n++)
            {
                var binding = appearance[n]; var r = sources[n];
                if (!binding.renderer || binding.renderer != r) return false;
                var filter = r.GetComponent<MeshFilter>(); Mesh mesh = filter ? filter.sharedMesh : null;
                if (binding.mesh != mesh) return false;
                var current = r.sharedMaterials;
                if (current.Length != binding.owned.Length) return false;
                for (int i = 0; i < current.Length; i++) if (current[i] != binding.owned[i]) return false;
            }
            return true;
        }
        private void BindSources(MeshRenderer[] sources)
        {
            foreach (var r in sources)
            {
                var filter = r.GetComponent<MeshFilter>(); var mesh = filter ? filter.sharedMesh : null;
                var original = r.sharedMaterials; var owned = new Material[original.Length];
                for (int i = 0; i < original.Length; i++)
                {
                    var source = original[i];
                    // Queue 1999: drawn before anything at the opaque queue (2000), so a mark another mod draws on the crate's
                    // surface without writing depth (NANDTweaks' mission logo: Standard in fade mode at 2000) paints over the wet
                    // crate as over the dry one; the shader's Offset covers marks that do write depth (user, 2026-10-10).
                    if (source) { owned[i] = new Material(source) { shader = CargoStainMaterial.Shader, name = source.name + " (EconomyOverhaul instance)" }; owned[i].renderQueue = WetQueue; }
                }
                var binding = new AppearanceBinding { renderer = r, mesh = mesh, originals = original, owned = owned };
                r.sharedMaterials = owned;
                // Access materials once after assignment so Unity's lazy renderer clones
                // become the tracked instances before any other component reads renderer.material.
                var installed = r.materials;
                for (int i = 0; i < owned.Length; i++)
                    if (owned[i] && (i >= installed.Length || owned[i] != installed[i])) Destroy(owned[i]);
                binding.owned = installed;
                appearance.Add(binding);
            }
            geometry = sources;
        }
        private void ReleaseBindings()
        {
            foreach (var binding in appearance)
            {
                var r = binding.renderer;
                if (r)
                {
                    var current = r.sharedMaterials; bool changed = false;
                    int count = Math.Min(current.Length, Math.Min(binding.owned?.Length ?? 0, binding.originals?.Length ?? 0));
                    for (int i = 0; i < count; i++)
                        if (current[i] == binding.owned[i]) { current[i] = binding.originals[i]; changed = true; }
                    if (changed) r.sharedMaterials = current;
                }
                if (binding.owned != null) foreach (var material in binding.owned) if (material) Destroy(material);
            }
            appearance.Clear();
        }
        private void OnDestroy()
        {
            Active.Remove(this);
            ReleaseBindings();
            history?.Dispose(); history = null;
        }
    }
    // Sample the rendered water triangles, independent of the interaction collider's enabled state.
    internal static class BoatWater
    {
        private static readonly List<BoatDamageWater> waters = new List<BoatDamageWater>();
        private static readonly Dictionary<Mesh, Tuple<Vector3[], int[]>> meshes = new Dictionary<Mesh, Tuple<Vector3[], int[]>>();
        internal static void Register(BoatDamageWater water) { if (!waters.Contains(water)) waters.Add(water); }
        // Two buffers a triangle is clipped between (a triangle cut by four sides has at most seven corners).
        private static Vector3[] clip = new Vector3[8], clipped = new Vector3[8];
        internal static float Depth(Transform boat, Bounds cargo, List<CargoWetVolume> contacts = null, Matrix4x4 cargoToWorld = default)
        {
            if (!boat) return 0;
            var damage = boat.GetComponentInParent<BoatDamage>();
            if (!damage) return 0;
            float depth = 0;
            for (int i = waters.Count - 1; i >= 0; i--)
            {
                var w = waters[i]; if (!w) { waters.RemoveAt(i); continue; }
                // ShipItem.currentActualBoat is the inner mesh parent; BoatDamage is on
                // its outer parent. Compare the owning component, not hierarchy direction.
                if (w.damage != damage) continue;
                var r = w.GetComponent<MeshRenderer>(); var f = w.GetComponent<MeshFilter>();
                if (!r || !r.enabled || !w.gameObject.activeInHierarchy || !f || !f.sharedMesh) continue;
                Bounds wb = r.bounds;
                if (cargo.max.x < wb.min.x || cargo.min.x > wb.max.x || cargo.max.z < wb.min.z || cargo.min.z > wb.max.z) continue;
                if (!meshes.TryGetValue(f.sharedMesh, out var data)) meshes.Add(f.sharedMesh, data = WaterGeometry.Read(f.sharedMesh));
                for (int n = 0; n < data.Item2.Length; n += 3)
                {
                    Vector3 a = w.transform.TransformPoint(data.Item1[data.Item2[n]]), b = w.transform.TransformPoint(data.Item1[data.Item2[n + 1]]), c = w.transform.TransformPoint(data.Item1[data.Item2[n + 2]]);
                    if (Mathf.Max(a.x, Mathf.Max(b.x, c.x)) < cargo.min.x || Mathf.Min(a.x, Mathf.Min(b.x, c.x)) > cargo.max.x ||
                        Mathf.Max(a.z, Mathf.Max(b.z, c.z)) < cargo.min.z || Mathf.Min(a.z, Mathf.Min(b.z, c.z)) > cargo.max.z) continue;
                    // Clip this triangle to the cargo's horizontal footprint; the highest remaining
                    // point detects shallow edge contact as well as centre immersion.
                    clip[0] = a; clip[1] = b; clip[2] = c;
                    int count = Clip(Clip(Clip(Clip(3, 0, cargo.min.x, true), 0, cargo.max.x, false), 2, cargo.min.z, true), 2, cargo.max.z, false);
                    bool above = false;
                    for (int k = 0; k < count; k++) { depth = Mathf.Max(depth, clip[k].y - cargo.min.y); above |= clip[k].y > cargo.min.y; }
                    if (contacts != null && above && Mathf.Abs(Vector3.Cross(b-a,c-a).y) > .000001f)
                        contacts.Add(CargoWetVolume.Triangle(a,b,c,cargoToWorld));
                }
            }
            return depth;
        }
        // One side of the footprint: the corners in `clip` that are inside, plus the crossings; the result is left in `clip`.
        private static int Clip(int count, int axis, float bound, bool lower)
        {
            if (count == 0) return 0;
            int n = 0; Vector3 a = clip[count - 1];
            bool insideA = lower ? a[axis] >= bound : a[axis] <= bound;
            for (int i = 0; i < count; i++)
            {
                var b = clip[i]; bool insideB = lower ? b[axis] >= bound : b[axis] <= bound;
                if (insideA != insideB) clipped[n++] = Vector3.LerpUnclamped(a, b, (bound - a[axis]) / (b[axis] - a[axis]));
                if (insideB) clipped[n++] = b; a = b; insideA = insideB;
            }
            var swap = clip; clip = clipped; clipped = swap;
            return n;
        }
    }
    // The weather and the player's place, read once a frame for every crate.
    internal static class CargoWeather
    {
        private const float Reach = 1000;
        private static int frame = -1; private static float clouds, rain; private static Vector3 player; private static bool hasPlayer;
        private static void Read()
        {
            if (frame == Time.frameCount) return; frame = Time.frameCount;
            clouds = Weather.instance ? Fields.Get<WeatherParticlesSettings>(Weather.instance, "finalParticles").cloudDensity : 0;
            rain = Rules.EffectiveRain(GameState.rainIntensity);
            var t = Refs.observerMirror ? Refs.observerMirror.transform : null; hasPlayer = t; if (t) player = t.position;
        }
        internal static float Clouds { get { Read(); return clouds; } }
        internal static float Rain { get { Read(); return rain; } }
        // Within 1 km of the player.
        internal static bool Near(Vector3 p) { Read(); return !hasPlayer || (p - player).sqrMagnitude <= Reach * Reach; }
    }
    [HarmonyPatch(typeof(BoatDamageWater), "Start")] internal static class WaterRegisterPatch { static void Postfix(BoatDamageWater __instance) => BoatWater.Register(__instance); }
    [HarmonyPatch(typeof(SaveablePrefab), "RegisterToSave")] internal static class CargoRegisterPatch
    { static void Postfix(SaveablePrefab __instance) { CargoCondition.Track(__instance.GetComponent<ShipItem>()); } }
    [HarmonyPatch(typeof(SaveablePrefab), "Load")] internal static class CargoLoadPatch
    { static void Postfix(SaveablePrefab __instance) { CargoCondition.Track(__instance.GetComponent<ShipItem>()); } }
    [HarmonyPatch(typeof(SaveablePrefab), "AssignRandomInstanceId")] internal static class CargoIdentityPatch
    {
        static void Postfix(SaveablePrefab __instance)
        {
            while (World.Cargo.ContainsKey(__instance.instanceId) || World.Removed.Contains(__instance.instanceId) || Missions.DeliveredItems.Contains(__instance.instanceId))
            {
                int id = UnityEngine.Random.Range(1, int.MaxValue);
                if (SaveablePrefab.existingInstanceIds == null || !SaveablePrefab.existingInstanceIds.Contains(id)) __instance.instanceId = id;
            }
        }
    }
    internal static class CoverEvents
    {
        // Tracked cargo under `top`, on the same boat (or also loose): footprints overlap on the boat's
        // axes and its top is below top's middle, as resting meshes can overlap their colliders.
        // A loose test on purpose: a false positive only costs one rescan.
        internal static List<CargoCondition> Beneath(ShipItem top, Bounds topBounds)
        {
            List<CargoCondition> found = null;
            if (!top) return found;
            var boat = top.currentActualBoat;
            var footprint = Local(topBounds, boat);
            float reach = footprint.extents.x + footprint.extents.z + 3;
            foreach (var c in CargoCondition.Active)
            {
                var other = c.Item;
                if (!other || other == top || other.currentActualBoat != boat) continue;
                Vector3 near = boat ? boat.InverseTransformPoint(other.transform.position) : other.transform.position;
                if (Mathf.Abs(near.x - footprint.center.x) > reach || Mathf.Abs(near.z - footprint.center.z) > reach) continue;
                Bounds o = Local(c.WorldBounds, boat);
                if (o.max.x > footprint.min.x && o.min.x < footprint.max.x && o.max.z > footprint.min.z && o.min.z < footprint.max.z && o.max.y <= footprint.center.y)
                    (found ?? (found = new List<CargoCondition>())).Add(c);
            }
            return found;
        }
        internal static void Recheck(List<CargoCondition> cargo) { if (cargo != null) foreach (var c in cargo) if (c) c.RequestCoverRecheck(); }
        internal static Bounds HatchBounds(GPButtonTrapdoor hatch)
        {
            var c = hatch.GetComponent<Collider>(); return c && c.enabled ? c.bounds : new Bounds(hatch.transform.position, Vector3.one);
        }
        internal static IEnumerator AfterHatch(GPButtonTrapdoor hatch, Bounds before)
        {
            yield return null;
            for (float waited = 0; hatch && waited < 3 && Fields.Get<bool>(hatch, "inMotion"); waited += Time.deltaTime) yield return null;
            if (!hatch || !hatch.importedActualBoat) yield break;
            var area = HatchBounds(hatch); area.Encapsulate(before);
            RecheckUnder(hatch.importedActualBoat, area);
        }
        // Placed cargo aboard this boat under the area (on the boat's axes, 25 cm to spare each side) and below its top.
        internal static void RecheckUnder(Transform boat, Bounds area)
        {
            var hull = boat.GetComponentInParent<BoatDamage>();
            var footprint = Local(area, boat); footprint.Expand(new Vector3(.5f, 0, .5f));
            foreach (var c in CargoCondition.Active)
            {
                var on = c.Item ? c.Item.currentActualBoat : null;
                if (!on || on.GetComponentInParent<BoatDamage>() != hull) continue;
                var o = Local(c.WorldBounds, boat);
                if (o.max.x > footprint.min.x && o.min.x < footprint.max.x && o.max.z > footprint.min.z && o.min.z < footprint.max.z && o.min.y < footprint.max.y)
                    c.RequestCoverRecheck();
            }
        }
        // Something was set on, lifted off or removed from above `top`: rescan placed cargo beneath it.
        internal static void RecheckBeneath(ShipItem top, Bounds topBounds) => Recheck(Beneath(top, topBounds));
        // World bounds on the boat's axes (world axes for loose items).
        internal static Bounds Local(Bounds world, Transform boat)
        {
            if (!boat) return world;
            var b = new Bounds(boat.InverseTransformPoint(world.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                b.Encapsulate(boat.InverseTransformPoint(world.center + Vector3.Scale(world.extents,
                    new Vector3((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1))));
            return b;
        }
        internal static Bounds WorldBounds(ShipItem item)
        {
            var b = new Bounds(item.transform.position, Vector3.zero); bool found = false;
            foreach (var r in item.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || r.GetComponentInParent<ShipItem>() != item) continue;
                if (!found) { b = r.bounds; found = true; } else b.Encapsulate(r.bounds);
            }
            return b;
        }
        // True for colliders of an item in a hand, an inventory slot or the cart, including its physics
        // copy: a copy entering the cart keeps a solid collider until the next physics step.
        internal static bool NotCover(Collider c)
        {
            var s = c.GetComponentInParent<ShipItem>();
            if (!s) { var rb = c.GetComponentInParent<ItemRigidbody>(); s = rb ? rb.GetShipItem() : null; }
            return s && (s.held || s.itemRigidbodyC && s.itemRigidbodyC.GetCurrentInventorySlot());
        }
    }
    // Added to an item when it is dropped or placed by the trade book. Once the item has been still
    // relative to its boat for 0.35 s, rescans cargo under it, then removes itself.
    internal sealed class CoverDropWatcher : MonoBehaviour
    {
        private ShipItem item; private Transform boat; private Vector3 pos; private Quaternion rot;
        private float still, life; private bool known;
        internal static void Attach(ShipItem item) { if (item && !item.GetComponent<CoverDropWatcher>()) item.gameObject.AddComponent<CoverDropWatcher>(); }
        private void Awake() => item = GetComponent<ShipItem>();
        private void LateUpdate()
        {
            life += Time.deltaTime;
            if (!item || item.held || item.GetCurrentInventorySlot() >= 0 || life > 15) { Destroy(this); return; }
            var b = item.currentActualBoat;
            var p = b ? b.InverseTransformPoint(transform.position) : transform.position;
            var r = b ? Quaternion.Inverse(b.rotation) * transform.rotation : transform.rotation;
            if (!known || b != boat || (p - pos).sqrMagnitude > .0025f || Quaternion.Angle(r, rot) > 2)
            { known = true; boat = b; pos = p; rot = r; still = 0; return; }
            if ((still += Time.deltaTime) < .35f) return;
            CoverEvents.RecheckBeneath(item, CoverEvents.WorldBounds(item));
            Destroy(this);
        }
    }
    [HarmonyPatch(typeof(GoPointer), "DropItem")] internal static class CargoPlacementPatch
    {
        static void Prefix(GoPointer __instance, out ShipItem __state) { var held = __instance.GetHeldItem(); __state = held ? held.GetComponent<ShipItem>() : null; }
        static void Postfix(ShipItem __state)
        {
            if (!__state) return;
            __state.GetComponent<CargoCondition>()?.ClearExposure();
            CoverDropWatcher.Attach(__state);   // rescan cargo under it once it settles
        }
    }
    // Bounds are taken before the item leaves its place; the rescans run next frame and ignore held items.
    [HarmonyPatch(typeof(GoPointer), "PickUpItem")] internal static class CoverPickupPatch
    {
        static void Prefix(PickupableItem item)
        { var ship = item ? item.GetComponent<ShipItem>() : null; if (ship) CoverEvents.RecheckBeneath(ship, CoverEvents.WorldBounds(ship)); }
    }
    // Sold, used-up or opened items stop covering what was under them.
    [HarmonyPatch(typeof(ShipItem), "DestroyItem")] internal static class CoverRemovePatch
    {
        static void Prefix(ShipItem __instance) => CoverEvents.RecheckBeneath(__instance, CoverEvents.WorldBounds(__instance));
    }
    // A hatch opened or closed changes what covers the cargo under it: once it has stopped, the cargo beneath where it was
    // and where it is now is rescanned.
    [HarmonyPatch(typeof(GPButtonTrapdoor), "OnActivate")] internal static class CoverHatchPatch
    {
        static void Prefix(GPButtonTrapdoor __instance, out Bounds __state) => __state = CoverEvents.HatchBounds(__instance);
        static void Postfix(GPButtonTrapdoor __instance, Bounds __state) { if (World.Ready && Plugin.Instance) Plugin.Instance.StartCoroutine(CoverEvents.AfterHatch(__instance, __state)); }
    }
    // The storage screen's cart-loading mode moves cargo straight into the cart, without a pickup.
    [HarmonyPatch(typeof(CargoCarrier), "InsertItem")] internal static class CoverCartPatch
    {
        static void Prefix(ShipItem item) { if (item) CoverEvents.RecheckBeneath(item, CoverEvents.WorldBounds(item)); }
    }
    [HarmonyPatch(typeof(ShipItem), "OnPickup")] internal static class CargoPickupPatch { static void Postfix(ShipItem __instance) => __instance.GetComponent<CargoCondition>()?.ClearExposure(); }
    [HarmonyPatch(typeof(ShipItem), "OnEnterInventory")] internal static class CargoInventoryPatch { static void Postfix(ShipItem __instance) => __instance.GetComponent<CargoCondition>()?.ClearExposure(); }

    // ---- Wet stains ----
    // A water triangle extruded downwards, expressed in cargo-local coordinates.
    // Four half-spaces retain the actual floodwater footprint, including sloping surfaces.
    internal struct CargoWetVolume
    {
        internal Vector4 top, a, b, c;
        internal bool ocean;
        private static float Side(Vector4 plane, Vector3 p) => plane.x*p.x+plane.y*p.y+plane.z*p.z+plane.w;
        internal bool Contains(Vector3 p) => Side(top,p) <= .0001f &&
            (ocean || Side(a,p) <= .0001f && Side(b,p) <= .0001f && Side(c,p) <= .0001f);
        private static Vector4 LocalPlane(Vector3 normal, Vector3 origin, Matrix4x4 cargoToWorld)
        {
            var n = cargoToWorld.transpose.MultiplyVector(normal);
            return new Vector4(n.x,n.y,n.z,Vector3.Dot(normal,cargoToWorld.MultiplyPoint3x4(Vector3.zero)-origin));
        }
        internal static CargoWetVolume Sea(float height, Matrix4x4 matrix) => new CargoWetVolume {
            top=LocalPlane(Vector3.up,new Vector3(0,height,0),matrix),ocean=true };
        internal static CargoWetVolume Triangle(Vector3 a, Vector3 b, Vector3 c, Matrix4x4 matrix)
        {
            Vector3 normal=Vector3.Cross(b-a,c-a).normalized;
            if(normal.y<0) normal=-normal;
            return new CargoWetVolume { top=LocalPlane(normal,a,matrix),
                a=Edge(a,b,c,matrix),b=Edge(b,c,a,matrix),c=Edge(c,a,b,matrix) };
        }
        private static Vector4 Edge(Vector3 from,Vector3 to,Vector3 inside,Matrix4x4 matrix)
        {
            Vector3 d=to-from,n=new Vector3(d.z,0,-d.x);
            if(Vector3.Dot(n,inside-from)>0)n=-n;
            return LocalPlane(n,from,matrix);
        }
    }

    internal sealed class CargoStainHistory : IDisposable
    {
        internal const int Resolution=10, Cells=1000, Bytes=Cells*4;
        internal const float UpdateSeconds=3;
        internal readonly Bounds LocalBounds;
        internal Texture3D Texture { get; private set; }
        internal bool Dirty { get; private set; } = true;
        private readonly CargoState state;
        internal bool BelongsTo(CargoState record) => state == record;
        private readonly Vector3[] points=new Vector3[Cells];
        private readonly Color32[] pixels=new Color32[Cells];
        private readonly List<Exposure> pending=new List<Exposure>(16);
        private sealed class Exposure
        {
            internal CargoWetVolume[] water;
            internal double immersion,rain,dryOpen;
            internal bool[] cover;                        // per cell, true = under cover: no rain, no drying
            internal Vector3 heightDirection;
            internal float heightOffset,heightScale;
        }
        // Cell i of the 10x10x10 map, in cargo-local coordinates (same layout as `points`).
        internal static Vector3 CellPoint(Bounds b,int i) =>
            b.min+Vector3.Scale(b.size,new Vector3(i%Resolution/9f,i/Resolution%Resolution/9f,i/(Resolution*Resolution)/9f));
        internal CargoStainHistory(CargoState state, Bounds bounds)
        {
            this.state=state; LocalBounds=bounds;
            if(state.stainHistory==null) state.stainHistory=new byte[Bytes];
            for(int i=0;i<Cells;i++) points[i]=CellPoint(bounds,i);
        }
        internal static double Read(byte[] data,int offset) => (data[offset] | data[offset+1]<<8)/65535d;
        private static void Write(byte[] data,int offset,double value)
        {
            int v=(int)Math.Round(Math.Max(0,Math.Min(1,value))*65535);
            data[offset]=(byte)(v&255);data[offset+1]=(byte)(v>>8);
        }
        internal void Accumulate(List<CargoWetVolume> water,double immersion,double rain,double dryOpen,
            bool[] cover,Matrix4x4 matrix,Bounds worldBounds)
        {
            if(immersion<=0 && rain<=0 && dryOpen<=0)return;
            var up=new Vector3(matrix.m10,matrix.m11,matrix.m12);
            var sample=new Exposure { water=immersion>0 ? water.ToArray() : null,immersion=immersion,rain=rain,
                dryOpen=dryOpen,cover=cover,
                heightDirection=up,heightOffset=matrix.m13-worldBounds.min.y,heightScale=1/Math.Max(.001f,worldBounds.size.y) };
            // Capture exposure poses cheaply between grid updates. Stationary adjacent
            // samples coalesce; rotation history is retained across the three-second interval.
            if(pending.Count>0 && Equivalent(pending[pending.Count-1],sample))
            {
                var previous=pending[pending.Count-1];previous.immersion+=immersion;previous.rain+=rain;
                previous.dryOpen+=dryOpen;
            }
            else pending.Add(sample);
        }
        private static bool Equivalent(Exposure a,Exposure b)
        {
            if((a.dryOpen>0)!=(b.dryOpen>0) || a.cover!=b.cover ||
                (a.rain>0)!=(b.rain>0) || (a.immersion>0)!=(b.immersion>0) ||
                (a.heightDirection-b.heightDirection).sqrMagnitude>.000001f || Math.Abs(a.heightOffset-b.heightOffset)>.001f ||
                Math.Abs(a.heightScale-b.heightScale)>.001f || (a.water?.Length ?? 0)!=(b.water?.Length ?? 0)) return false;
            if(a.water!=null) for(int i=0;i<a.water.Length;i++)
                if((a.water[i].top-b.water[i].top).sqrMagnitude>.000001f || (a.water[i].a-b.water[i].a).sqrMagnitude>.000001f ||
                    (a.water[i].b-b.water[i].b).sqrMagnitude>.000001f || (a.water[i].c-b.water[i].c).sqrMagnitude>.000001f)return false;
            return true;
        }
        internal bool Flush()
        {
            if(pending.Count==0)return false;
            bool changed=false;
            for(int i=0;i<Cells;i++)
            {
                int offset=i*4;double stain=Read(state.stainHistory,offset),wet=Read(state.stainHistory,offset+2);
                double oldStain=stain,oldWet=wet;Vector3 p=points[i];
                foreach(var sample in pending)
                {
                    bool sheltered=sample.cover!=null && sample.cover[i];
                    double amount=0;
                    if(sample.water!=null) foreach(var volume in sample.water) if(volume.Contains(p)) { amount=sample.immersion;break; }
                    if(sample.rain>0 && !sheltered)
                    {
                        // Cells under a covered part of the top face get no rain. Rain favours
                        // the upward region and exposed upper sides rather than a tide band.
                        double height=(Vector3.Dot(sample.heightDirection,p)+sample.heightOffset)*sample.heightScale;
                        double exposure=Rules.Clamp((height-.15)/.7,0,1); exposure=exposure*exposure*(3-2*exposure);
                        amount+=sample.rain*exposure;
                    }
                    double dry=sheltered?0:sample.dryOpen;
                    stain=Math.Min(1,stain+amount/40);wet=Math.Max(0,Math.Min(1,wet+amount/25)-dry/20);
                }
                if(stain!=oldStain || wet!=oldWet)
                {Write(state.stainHistory,offset,stain);Write(state.stainHistory,offset+2,wet);changed=true;}
            }
            pending.Clear();Dirty|=changed;return changed;
        }
        internal void Upload()
        {
            if(!Dirty && Texture)return;
            if(!Texture) Texture=new Texture3D(Resolution,Resolution,Resolution,TextureFormat.RGBA32,false) {
                name="Cargo contact history 10x10x10",wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear };
            for(int i=0;i<Cells;i++)pixels[i]=new Color32(state.stainHistory[i*4+1],state.stainHistory[i*4+3],0,255);
            Texture.SetPixels32(pixels);Texture.Apply(false,false);Dirty=false;
        }
        public void Dispose() { Flush();if(Texture)UnityEngine.Object.Destroy(Texture);Texture=null; }
    }

    internal static class CargoStainMaterial
    {
        internal static Shader Shader;
        internal static Texture3D Noise;
        internal static readonly int HistoryId = UnityEngine.Shader.PropertyToID("_CargoHistory");
        internal static readonly int NoiseId = UnityEngine.Shader.PropertyToID("_StainNoise");
        internal static readonly int MatrixId = UnityEngine.Shader.PropertyToID("_RendererToCargo");
        internal static readonly int MinId = UnityEngine.Shader.PropertyToID("_CargoMin");
        internal static readonly int SizeId = UnityEngine.Shader.PropertyToID("_CargoInvSize");
        internal static readonly int VisibleId = UnityEngine.Shader.PropertyToID("_StainVisible");
        internal static readonly int CrackId = UnityEngine.Shader.PropertyToID("_CrackMask");
        internal static readonly int CrackCutId = UnityEngine.Shader.PropertyToID("_CrackCut");
        internal static readonly int CrackGrainId = UnityEngine.Shader.PropertyToID("_CrackGrain");
        internal static readonly int CrackSeedId = UnityEngine.Shader.PropertyToID("_CrackSeed");
        internal static readonly int CrackColorId = UnityEngine.Shader.PropertyToID("_CrackColor");
        // Fragile cargo's cracks: the grain bits of the first source renderer whose mesh is a fragile crate (Rules.CrackGrain), or -1.
        internal static int CrackGrain(MeshRenderer[] sources)
        {
            foreach (var r in sources)
            {
                var mesh = r ? r.GetComponent<MeshFilter>()?.sharedMesh : null;
                int grain = mesh ? Rules.CrackGrain(mesh.name) : -1;
                if (grain >= 0) return grain;
            }
            return -1;
        }
        // The game's hull damage texture, from its own ships' damage overlays (HullDamageTexture); null until a scene with ships
        // is loaded, looked for again at most every 5 s.
        private static Texture crackTexture; private static float crackSearched = -10;
        internal static Texture CrackTexture()
        {
            if (crackTexture || Time.unscaledTime - crackSearched < 5) return crackTexture;
            crackSearched = Time.unscaledTime;
            foreach (var overlay in Resources.FindObjectsOfTypeAll<HullDamageTexture>())
            {
                var renderer = overlay ? overlay.GetComponent<Renderer>() : null;
                var texture = renderer && renderer.sharedMaterial ? renderer.sharedMaterial.mainTexture : null;
                if (texture) return crackTexture = texture;
            }
            return null;
        }
        // The game's damage colour (RefsDirectory.damageColor, 0.18, 0.20, 0.21 in the scene).
        internal static Color CrackColor() => RefsDirectory.instance ? RefsDirectory.instance.damageColor : new Color(.177f, .204f, .208f);
        internal static void Load()
        {
            if (Shader) return;
            using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream("EconomyOverhaul.Data.cargo-stain.bundle"))
            using (var memory = new MemoryStream())
            {
                if (stream == null) throw new InvalidDataException("Missing embedded cargo stain shader");
                stream.CopyTo(memory);
                var bundle = AssetBundle.LoadFromMemory(memory.ToArray());
                if (!bundle) throw new InvalidDataException("Cannot load cargo stain shader bundle");
                Shader = bundle.LoadAsset<Shader>("Assets/CargoStain.shader");
                bundle.Unload(false);
                if (!Shader || !Shader.isSupported) throw new NotSupportedException("Cargo stain shader unsupported on this graphics device");
            }
            Noise = new Texture3D(24,24,24,TextureFormat.RGBA32,false) {
                name="Shared soft cargo stain detail",wrapMode=TextureWrapMode.Repeat,filterMode=FilterMode.Bilinear };
            var pixels = new Color32[24*24*24];
            for (int z=0;z<24;z++) for (int y=0;y<24;y++) for (int x=0;x<24;x++)
            {
                float n = (Mathf.PerlinNoise(x*.18f,y*.18f)+Mathf.PerlinNoise(y*.18f,z*.18f)+Mathf.PerlinNoise(z*.18f,x*.18f))/3;
                byte v=(byte)Mathf.RoundToInt(n*255); pixels[x+24*(y+24*z)]=new Color32(v,v,v,255);
            }
            Noise.SetPixels32(pixels); Noise.Apply(false,true);
        }
    }

    // ---- Bilge water ----
    internal static class WaterGeometry
    {
        private sealed class Entry { internal int count; internal Bounds bounds; internal Tuple<Vector3[], int[]> triangles; }
        private static readonly List<Entry> entries = new List<Entry>();
        internal static Tuple<Vector3[], int[]> Read(Mesh mesh)
        {
            if (mesh.isReadable) return Tuple.Create(mesh.vertices, mesh.triangles);
            // Some native water surfaces discard CPU buffers. These small query-only
            // triangles are extracted from the same game assets, matched by geometry.
            if (entries.Count == 0)
                foreach (var c in Embedded.Csv("water.csv", header: false))
                {
                    Func<int,float> f = i => float.Parse(c[i], CultureInfo.InvariantCulture);
                    var entry = new Entry { count = int.Parse(c[1]), bounds = new Bounds(new Vector3(f(2),f(3),f(4)), new Vector3(f(5),f(6),f(7))*2) };
                    var vertices = new Vector3[(c.Length-8)/3]; var indices = new int[vertices.Length];
                    for(int i=0;i<vertices.Length;i++) { vertices[i]=new Vector3(f(8+i*3),f(9+i*3),f(10+i*3)); indices[i]=i; }
                    entry.triangles=Tuple.Create(vertices,indices); entries.Add(entry);
                }
            foreach(var e in entries) if(e.count==mesh.vertexCount && (e.bounds.center-mesh.bounds.center).sqrMagnitude < .00001f && (e.bounds.extents-mesh.bounds.extents).sqrMagnitude < .00001f) return e.triangles;
            Plugin.Log.LogWarning("Unrecognized unreadable boat-water mesh: " + mesh.name + ". This surface cannot be sampled.");
            return Tuple.Create(new Vector3[0],new int[0]);
        }
    }
}

namespace EconomyOverhaul
{
    // Fragile cargo: the hull damage a counted impact added (the game's own skips apply: cooldown, the sea bed, items, low
    // speed, sleeping moored) reaches every fragile crate aboard.
    [HarmonyLib.HarmonyPatch(typeof(BoatDamage), "Impact")] internal static class FragileImpactPatch
    {
        static void Prefix(BoatDamage __instance, out float __state) => __state = __instance.hullDamage;
        static void Postfix(BoatDamage __instance, float __state) { float added = __instance.hullDamage - __state; if (added > 0) CargoCondition.HullHit(__instance, added); }
    }
}
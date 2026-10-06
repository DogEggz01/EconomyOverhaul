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
        public static bool DamagingImmersion(double depth, double cargoHeight) => cargoHeight > 0 && depth > 0 && depth >= cargoHeight * .05;
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
        public static string HintSuffix(string description, string lookText, int percent, bool loan)
        {
            var lines = new List<string>(2);
            if (percent >= 0) lines.Add("Water Damage: " + percent.ToString(CultureInfo.InvariantCulture) + "%");
            if (loan) lines.Add("Respondentia");
            if (lines.Count == 0) return "";
            int look = TextLines(lookText), own = TextLines(description);
            int padding = Math.Max(1, look - own + 1);
            if (look >= 2) padding = Math.Max(padding, 3 + (lines.Count - 1) + ((look - 2) * 10 + 2) / 3 - Math.Max(1, own) + 1);
            return new string('\n', padding) + string.Join("\n", lines);
        }
        private static int TextLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int lines = 1; foreach (char c in text) if (c == '\n') lines++;
            return lines;
        }

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
        private const float CoverRayHeight = 400;                         // rays start this far above the cargo
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
        private int hintPercent = -2;
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
        internal static float Factor(ShipItem item)
        {
            if (!Settings.WaterEnabled) return 1;
            if (!item) return 1;
            var save = item.GetComponent<SaveablePrefab>();
            if (!save || !World.Cargo.TryGetValue(save.instanceId, out var c)) return 1;
            return (float)Rules.ConditionFactor(c.health, LiquidCargo.Contains(c.good), Immune.Contains(c.good));
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
            const int excluded = (1 << 2) | (1 << 4) | (1 << 5) | (1 << 8) | (1 << 11) | (1 << 13) |
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
                    boat && walk && Covered(walk.TransformPoint(boat.InverseTransformPoint(p)), walkUp, -1, walk) ||
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
                ? Mathf.RoundToInt(100 - Mathf.Clamp(state.health, 0, 100)) : -1;
            if (hintPercent == percent && hintLoan == state.loan && item.description == hintDescription && hintLookText == item.lookText) return;
            // Never replace vanilla mission/name/fullness information.
            if (!string.IsNullOrEmpty(suffix) && item.description != null && item.description.EndsWith(suffix, StringComparison.Ordinal))
                item.description = item.description.Substring(0, item.description.Length - suffix.Length);
            suffix = Rules.HintSuffix(item.description, item.lookText, percent, state.loan);
            hintDescription = item.description = (item.description ?? "") + suffix;
            hintLookText = item.lookText;
            hintPercent = percent; hintLoan = state.loan;
        }
        private void RefreshAppearance()
        {
            if (state == null || Immune.Contains(state.good)) return;
            appearanceTimer = 0;
            float health = Settings.WaterEnabled ? state.health : 100;
            var sources = SourceRenderers();
            bool stale = !BindingsMatch(sources);
            if (stale)
            {
                ReleaseBindings(); geometry = sources;
                if (health < 100) BindSources(sources);
            }
            else geometry = sources;
            if (health >= 100)
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
                    if (source) owned[i] = new Material(source) { shader = CargoStainMaterial.Shader, name = source.name + " (EconomyOverhaul instance)" };
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
        private static GameObject Template(GameObject source, Transform holder, string objectName, Mesh mesh, Material material, int index, string itemName, int value, float mass, bool big)
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
        internal static bool Unbundleable(ShipItem item) => Registered && item && item.sold && !item.held && (Index(item) == BundleIndex || Index(item) == LumberIndex);
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
            if (body) { bodyText = body.text; body.text = "Unbundling removes the ropes.\n\nYou get " + PlankCount(item) + " wood planks.\nPlanks cannot be sold\nin the trade book."; }
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
            // Fewer planks from wet lumber: the lowest places of the stack, so none is left hanging.
            var layout = Layout(item).OrderBy(p => p.Key.position.y).Take(PlankCount(item)).ToList();
            var boat = item.currentActualBoat; Collider embark = null;
            if (boat) try { embark = Fields.Get<Collider>(item, "currentBoatCollider"); } catch (MissingFieldException) { }
            var holder = item.transform.parent ? item.transform.parent : FloatingOriginManager.instance ? FloatingOriginManager.instance.transform : null;
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUISound(UISounds.crateSealBreak, 1f, 1f);
            Retire(item);
            var planks = new List<ShipItem>(layout.Count); var onBoat = new List<Pose>(layout.Count);
            foreach (var entry in layout)
            {
                var o = UnityEngine.Object.Instantiate(PlankTemplate, entry.Key.position, entry.Key.rotation, holder);
                var plank = o.GetComponent<ShipItem>(); plank.sold = true; plank.amount = 0; SetLook(plank, entry.Value);
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
    { static void Prefix(PrefabsDirectory __instance) { WoodPlanks.Register(__instance); Tarps.Register(__instance); } }
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
    { static void Postfix(SaveablePrefab __instance) { var item = __instance.GetComponent<ShipItem>(); if (WoodPlanks.IsPlank(item)) WoodPlanks.ApplyLook(item); } }
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

    // ---- Canvas tarp ----
    // The canvas pack (prefab 642): a tarp folded up, drawn with the mail parcel's model while it is an item. Tying its four
    // corners makes a tarp (CANVAS_DRAFT.md); untying gives a pack back.
    internal static class Tarps
    {
        internal const int PackIndex = 642, MailGood = 51;
        // Price 500, mass 30 (user, 2026-10-05; oakum is 180 and 9).
        internal const int PackValue = 500; internal const float PackMass = 30;
        internal const float PackScale = .7f; internal const string LookName = "canvas pack look";
        // Wear (user, 2026-10-05): a tied tarp loses 10 health a game day, 100 to 0 in 10 days; a folded pack keeps its health
        // (in ShipItem.health, which the game saves with every item). The health is never shown.
        internal const float FullHealth = 100, WearPerDay = 10;
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
            if (real != null && Moving(real.Value.collider) && (onShip == null || real.Value.distance < onShip.Value.distance - .05f))
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
                if (h.collider.GetComponentInParent<ItemRigidbody>() || h.collider.GetComponentInParent<ShipItem>() || MovingWalk(frame, h.collider.transform))
                { Why = "item, hatch or door on the walk copy: " + Path(h.collider.transform); return null; }
                Why = null;
                // On a stay's walk copy too, the knot goes on its rope line rather than on its collider's surface.
                var at = h.collider is CapsuleCollider c && WalkStay(frame, h.collider.transform) ? OnAxis(c, ray) : h.point;
                return new Corner { frame = frame, onShip = true, local = walk.InverseTransformPoint(at),
                    rotation = Quaternion.LookRotation(-walk.InverseTransformDirection(h.normal), Vector3.up), part = h.collider };
            }
            if (real == null || real.Value.collider.attachedRigidbody)
            { Why = real == null ? "nothing in reach" : "not fixed (has a rigidbody): " + Path(real.Value.collider.transform); return null; }
            Why = null;
            var land = real.Value;
            return new Corner { local = FloatingOriginManager.instance.ShiftingPosToRealPos(land.point), rotation = Quaternion.LookRotation(-land.normal, Vector3.up), part = land.collider };
        }
        internal static string Why;   // why the last Find gave no corner (for logs and tests)
        private static string Path(Transform t) => t.parent ? t.parent.name + "/" + t.name : t.name;
        private static bool Own(Collider c, ShipItem held) => held && (c.transform.IsChildOf(held.transform) || held.itemRigidbodyC && c.transform.IsChildOf(held.itemRigidbodyC.transform));
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
        // The fourth corner makes the tarp and uses the pack up: no item stays hanging.
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
            return null;
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
            internal int Index(int i, int j) => j * (segU + 1) + i;
            // The sides along u and v (m).
            internal float lu, lv;
        }
        // The tarp's layout from its four corners in order (c0-c1 and c3-c2 along u, c0-c3 and c1-c2 along v) and its parent's
        // down. The sheet is bilinear through the corners: a straight line between matching points of opposite sides lies on it,
        // so the ropes across follow it whatever the corners' heights. Grid lines fall on every rope.
        internal static Layout Plan(Vector3[] c, Vector3 down)
        {
            float lu = Mathf.Max(Vector3.Distance(c[0], c[1]), Vector3.Distance(c[3], c[2])), lv = Mathf.Max(Vector3.Distance(c[0], c[3]), Vector3.Distance(c[1], c[2]));
            int nu = Cells(lu), nv = Cells(lv), mu = PerCell(lu, nu), mv = PerCell(lv, nv);
            var L = new Layout { cellsU = nu, cellsV = nv, segU = nu * mu, segV = nv * mv, lu = lu, lv = lv };
            int pu = L.segU + 1, pv = L.segV + 1, count = pu * pv;
            L.points = new Vector3[count]; L.pinned = new bool[count]; L.cap = new float[count]; L.uv = new Vector2[count];
            float depth = Drop * (lu / nu + lv / nv) / 2;
            int ku = Mathf.Max(1, Mathf.RoundToInt(Lacing / (lu / L.segU))), kv = Mathf.Max(1, Mathf.RoundToInt(Lacing / (lv / L.segV)));
            Vector3 At(int i, int j) { float u = (float)i / L.segU, v = (float)j / L.segV; return Vector3.Lerp(Vector3.Lerp(c[0], c[1], u), Vector3.Lerp(c[3], c[2], u), v); }
            for (int j = 0; j < pv; j++) for (int i = 0; i < pu; i++)
            {
                int k = L.Index(i, j); float a = (float)(i % mu) / mu, b = (float)(j % mv) / mv;
                L.points[k] = At(i, j) + down * depth * 16 * a * (1 - a) * b * (1 - b);
                bool alongV = i % mu == 0, alongU = j % mv == 0;   // on a rope running along v (constant i) or along u (constant j)
                L.pinned[k] = alongV && (j % kv == 0 || alongU) || alongU && (i % ku == 0 || alongV);
                L.uv[k] = StripUV((float)i / L.segU, (float)j / L.segV, lu, lv);
            }
            var pins = Enumerable.Range(0, count).Where(k => L.pinned[k]).Select(k => L.points[k]).ToArray();
            for (int k = 0; k < count; k++) L.cap[k] = L.pinned[k] ? 0 : Cap * 1.41f * pins.Min(p => Vector3.Distance(p, L.points[k]));
            for (int i = 0; i <= nu; i++) L.ropes.Add(new[] { At(i * mu, 0), At(i * mu, L.segV) });
            for (int j = 0; j <= nv; j++) L.ropes.Add(new[] { At(0, j * mv), At(L.segU, j * mv) });
            var t = new List<int>();
            for (int j = 0; j < L.segV; j++) for (int i = 0; i < L.segU; i++)
            { int a = L.Index(i, j), b = L.Index(i + 1, j), d = L.Index(i, j + 1), e = L.Index(i + 1, j + 1); t.AddRange(new[] { a, d, b, b, d, e }); }
            L.triangles = t.ToArray();
            return L;
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
        internal static Tarp Build(Transform parent, bool onShip, Vector3[] corners, Quaternion[] turns, float health = FullHealth, Transform placedLike = null)
        {
            var root = new GameObject("canvas tarp"); root.transform.SetParent(parent, false);
            // In the place of a tarp it replaces (a world shift moves a land tarp itself, not the shifting world).
            if (placedLike) { root.transform.localPosition = placedLike.localPosition; root.transform.localRotation = placedLike.localRotation; }
            var tarp = root.AddComponent<Tarp>(); tarp.onShip = onShip; tarp.corners = corners; tarp.turns = turns;
            tarp.startHealth = Mathf.Clamp(float.IsNaN(health) ? FullHealth : health, 0, FullHealth); tarp.startClock = Now; tarp.torn = tarp.startHealth <= 0;
            // Torn: its holes in its own texture (the whole cloth, mapped once over the sheet).
            bool cutout = tarp.torn && FindTornShader();
            var layout = Plan(corners, parent.InverseTransformDirection(Vector3.down)); tarp.layout = layout;
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
            foreach (var r in layout.ropes)
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
                var button = k.AddComponent<TarpKnot>(); button.tarp = tarp; button.corner = i; button.lookText = "untie canvas"; button.description = "";
            }
            tarp.SetCover(layout.points); RecheckUnder(tarp);
            Plugin.Log?.LogInfo("Canvas tarp " + (tarp.torn ? "torn" : "tied") + " " + (onShip ? "on " + parent.name : "on land") + ": sides " + string.Join(" ", Sides(corners).Select(s => s.ToString("F1"))) + " m, "
                + layout.points.Length + " points, " + layout.pinned.Count(p => p) + " laced, " + layout.ropes.Count + " ropes, health " + tarp.startHealth.ToString("F1") + ".");
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
        internal static void Untie(Tarp tarp, int corner, GoPointer pointer)
        {
            if (!tarp || !Registered) return;
            float health = tarp.Health; LastPack = null;
            var knot = tarp.knots[corner].transform; var frame = tarp.onShip ? tarp.transform.parent : null;
            bool hand = pointer && !pointer.GetHeldItem();
            Vector3 at; Quaternion turn;
            var flat = Vector3.ProjectOnPlane(pointer ? pointer.transform.forward : -knot.forward, Vector3.up); if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            if (hand) { at = pointer.transform.position + pointer.transform.forward * 1.3f - pointer.transform.up * .5f; turn = Quaternion.LookRotation(flat.normalized); }
            else { at = Under(knot, frame) + Vector3.up * .6f; turn = Quaternion.LookRotation(flat.normalized); }
            RecheckUnder(tarp);   // runs next frame, when the tarp is gone
            UnityEngine.Object.Destroy(tarp.gameObject);
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUISound(UISounds.itemPickup, .55f, 1);
            if (health > 0 && Plugin.Instance) Plugin.Instance.StartCoroutine(Give(at, turn, frame, hand ? pointer : null, health));
        }
        // The deck or ground under a corner knot, found from just off the part it is tied to (the knot faces into it).
        private static Vector3 Under(Transform knot, Transform frame)
        {
            var from = knot.position - knot.forward * .3f + Vector3.up * .1f;
            var walk = frame ? Link(frame)?.walkCollider : null;
            if (frame && walk)
            {
                var p = walk.TransformPoint(frame.InverseTransformPoint(from));
                var hit = Physics.RaycastAll(p, -walk.up, 30, ~0, QueryTriggerInteraction.Ignore)
                    .Where(h => h.collider.transform.IsChildOf(walk) && !h.collider.GetComponentInParent<ItemRigidbody>()).OrderBy(h => h.distance).FirstOrDefault();
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
        internal static void AddStock(ShopArea area)
        {
            if (!Registered || !area) return;
            int scene = area.gameObject.scene.buildIndex; var root = area.transform.root;
            foreach (var s in Stocks)
            {
                if (s.scene != scene || area.name != s.area || Vector3.Distance(root.InverseTransformPoint(area.transform.position), s.areaInRoot) > .5f) continue;
                // Under an unscaled parent that moves with the island (an item takes its slot's scale).
                var parent = area.transform.parent && Mathf.Abs(area.transform.parent.lossyScale.x - 1) < 1e-3f && Mathf.Abs(area.transform.parent.lossyScale.y - 1) < 1e-3f ? area.transform.parent : root;
                var slot = new GameObject(StockName); slot.transform.SetParent(parent, false);
                slot.transform.SetPositionAndRotation(area.transform.TransformPoint(s.local), area.transform.rotation * s.turn);
                var spawner = slot.AddComponent<ShopItemSpawner>(); spawner.itemPrefab = PackTemplate; spawner.availableAtNight = false; spawner.priceMult = 1;
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
        internal static void Watch(GoPointer pointer)
        {
            if (Pending.Count > 0 && (!PendingPack || !PendingPack.held || Pending.Any(c => c.onShip && !c.frame))) Cancel(true);
            var pack = pointer.GetHeldItem() as ShipItem;
            if (!pack || !pack.sold || !IsPack(pack)) { ShowGhost(null); return; }
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
        internal float Health => torn ? 0 : Mathf.Clamp(startHealth - (float)(Tarps.WearPerDay * Math.Max(0, Tarps.Now - startClock)), 0, Tarps.FullHealth);
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

    // A corner knot: the pointer's look text "untie canvas"; a click unties the tarp.
    internal sealed class TarpKnot : GoPointerButton
    {
        internal Tarp tarp; internal int corner;
        public override void OnActivate(GoPointer activatingPointer) => Tarps.Untie(tarp, corner, activatingPointer);
    }

    // R with a canvas pack in hand ties a corner (the game's R on a held item calls this; a plain item does nothing with it).
    [HarmonyPatch(typeof(GoPointerButton), "OnAltActivate", new[] { typeof(GoPointer) })] internal static class TarpTiePatch
    {
        static bool Prefix(GoPointerButton __instance, GoPointer activatingPointer)
        {
            if (!(__instance is ShipItem pack) || !activatingPointer || activatingPointer.GetHeldItem() != pack || !pack.sold || !Tarps.IsPack(pack)) return true;
            Tarps.Aboard(out var frame, out var walk);
            var notice = Tarps.Tie(pack, new Ray(activatingPointer.transform.position, activatingPointer.transform.forward), frame, walk);
            if (notice != null) Loans.Notify(notice);
            return false;
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

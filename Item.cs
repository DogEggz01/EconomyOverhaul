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

        // The wood plank's size and attach mode (PALLET_PLANK_DRAFT.md, the user, 2026-10-24: "press Q/E to length/shorten the plank,
        // from 0.5m to 4m"; "narrowest should be half the current width, widest should be 4 times"; "K4 use 0.25x then"; K3 "press
        // H(same as bracing button) to switch attachment mode, default is current mode"), kept in its saved health with its look: look +
        // 24 x (length code + 37 x (width code + 15 x mode)). Length code 0 today's 0.872 m, 1-36 0.5-4.0 m in 0.1 m steps; width code 0
        // today's 0.152 m (1.0x), 1-12 1.25x-4.0x, 13 and 14 0.5x and 0.75x (steps of 0.25 from 1.0x, -2 to 12); mode 0 flat, 1 by one
        // end. Every plank saved before (0-23) reads as today's plank, flat. Weight by area (K5 (a)), 9.375 lb at today's size.
        public const int PlankLooks = 24, PlankLengthCodes = 37, PlankWidthCodes = 15, PlankMinWidthSteps = -2, PlankMaxWidthSteps = 12;
        public const float PlankLength0 = .872f, PlankWidth0 = .152f, PlankThickness = .045f, PlankMass0 = 9.375f;
        public const float PlankMinLength = .5f, PlankMaxLength = 4, PlankLengthStep = .1f, PlankWidthStep = .25f;
        public struct PlankShape { public int look, length, width; public bool end; }   // length: its code; width: steps from 1.0x
        public static float PlankCode(PlankShape s) =>
            Clamp(s.look, 0, PlankLooks - 1) + PlankLooks * (Clamp(s.length, 0, PlankLengthCodes - 1) + PlankLengthCodes * ((Clamp(s.width, PlankMinWidthSteps, PlankMaxWidthSteps) + PlankWidthCodes) % PlankWidthCodes + PlankWidthCodes * (s.end ? 1 : 0)));
        public static PlankShape PlankDecode(double health)
        {
            if (double.IsNaN(health) || double.IsInfinity(health) || health < 0 || health > PlankCode(new PlankShape { look = PlankLooks - 1, length = PlankLengthCodes - 1, width = -1, end = true })) return default;
            int c = (int)Math.Round(health), look = c % PlankLooks; c /= PlankLooks;
            int length = c % PlankLengthCodes; c /= PlankLengthCodes;
            int width = c % PlankWidthCodes; c /= PlankWidthCodes;
            return new PlankShape { look = look, length = length, width = width <= PlankMaxWidthSteps ? width : width - PlankWidthCodes, end = c == 1 };
        }
        private static int Clamp(int x, int lo, int hi) => x < lo ? lo : x > hi ? hi : x;
        public static float PlankLength(int code) => code <= 0 || code >= PlankLengthCodes ? PlankLength0 : (float)Math.Round(PlankMinLength + (code - 1) * PlankLengthStep, 1);
        // One step longer (+1) or shorter (-1): from today's 0.872 m to 0.9 or 0.8; at 0.5 and 4.0 it stays.
        public static int PlankLengthStepped(int code, int step)
        {
            if (step == 0) return code;
            if (code <= 0 || code >= PlankLengthCodes) { double to = step > 0 ? Math.Ceiling(PlankLength0 * 10 + 1e-6) / 10 : Math.Floor(PlankLength0 * 10 - 1e-6) / 10; return (int)Math.Round((to - PlankMinLength) / PlankLengthStep) + 1; }
            return Clamp(code + step, 1, PlankLengthCodes - 1);
        }
        public static float PlankWidthScale(int steps) => 1 + PlankWidthStep * Clamp(steps, PlankMinWidthSteps, PlankMaxWidthSteps);
        public static float PlankWidth(int steps) => PlankWidth0 * PlankWidthScale(steps);
        public static float PlankMass(PlankShape s) => PlankMass0 * PlankLength(s.length) * PlankWidth(s.width) / (PlankLength0 * PlankWidth0);
        // Today's plank exactly: the look meshes are used for it.
        public static bool PlankPlain(PlankShape s) => s.length == 0 && s.width == 0 && !s.end;
        // How far a rod (a pole, or a plank by one end) attached to a surface is lifted out of it so it rests on it, not in it (the
        // user, 2026-10-24: a plank leaned 90 degrees "attached to the middle of the other plank"; asked, "Same, poles too"). It turns
        // about the middle of its end face, so tilted or leaned part of that face would go into the surface. `side` and `up`: how much
        // of the end face's two axes (a plank's width and thickness; a pole's across) lie along the surface's normal (their dot with
        // it); `halfSide` and `halfUp` the face's half sizes. A pole's end is round (its corners on the circle), a plank's a rectangle.
        // 0 standing straight out; leaned 90 a plank rests on its long edge (half its width), tilted 90 on its wide face.
        public static float RestLift(float side, float up, float halfSide, float halfUp, bool round) =>
            round ? halfSide * (float)Math.Sqrt(side * side + up * up) : halfSide * Math.Abs(side) + halfUp * Math.Abs(up);

        // The wood pole (user, 2026-10-12): 0.2-8 m in 0.1 m steps (user, 2026-10-13; was 0.5-5), 2 kg a metre, price 60; 2 m in
        // the shops.
        public const float PoleMin = .2f, PoleMax = 8, PoleStep = .1f, PoleShopLength = 2, PoleKgPerMetre = 2;
        // Pushing with a braced pole (user, 2026-10-13): 1.25 times the game's own push without one.
        public const float PolePush = 1.25f;
        // The fender from aboard (the user, 2026-10-14; 1.115 for 5 knots before): the dock push 1.908 times, so an 8 m pole stops at
        // most 3.09 m/s (6 knots) within its 7.6 m reach (a 2 m pole about 1.8 m/s).
        public const float PoleFender = 1.908f;
        // The pole's wood strip in the items' atlas is one dark brown only from v .25 to .61, a lighter wood above it (the user's
        // picture, 2026-10-13: a light tip from about 3.9 m; pole-brace-48 measured the change at .61) and a lighter line below .25
        // (an 8 m pole, turning back at v .25, showed a brown ring there: the user). 3.4 m of it is used, from v .26 to .5745 (clear
        // of both edges), a longer pole running back along it, mirrored (the user: one colour along the whole pole). Where along the
        // strip a point d metres from the pole's base lies.
        public const double PoleStrip = 3.4;
        public static double PoleGrain(double d) => Mirrored(d, PoleStrip);
        // A point d along a strip `strip` long, run to and fro (mirrored at each end): the pole's grain, a long plank's.
        public static double Mirrored(double d, double strip) { double period = 2 * strip, m = (d % period + period) % period; return m <= strip ? m : period - m; }
        // Eased 0-1 at both ends (smoothstep): a pole laid down and raised.
        public static double Eased(double t) { t = Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
        public const int PoleValue = 60;
        // The wood pole bundle the shops sell (user, 2026-10-12): four 2 m poles, four poles' price and weight.
        public const int PoleBundleCount = 4, PoleBundleValue = PoleValue * PoleBundleCount;
        public static float PoleBundleMass => PoleMass(PoleShopLength) * PoleBundleCount;
        public static int PoleTenths(double metres) => double.IsNaN(metres) || double.IsInfinity(metres) ? (int)Math.Round(PoleShopLength * 10) : (int)Math.Round(Clamp(metres, PoleMin, PoleMax) * 10);
        public static float PoleLength(double metres) => PoleTenths(metres) / 10f;
        public static float PoleMass(double metres) => PoleLength(metres) * PoleKgPerMetre;
        // The pole's diameter (the user, 2026-10-24: "press O/L to increase/decrease diameter. Current diameter is the minimum, up to
        // 10x thickness, each step 0.2x"): 1.0x (8 cm) to 10.0x in 0.2x steps (0-45). Kept in the saved health with the length:
        // length + 10 x steps, so every pole saved before (health 0.2-8) reads as 1.0x. Weight by cross-section (the user: "By
        // cross-section"): 2 a metre x the scale squared (a 2 m pole 4 lb at 1.0x, 16 at 2.0x, 400 at 10.0x).
        public const int PoleMaxDiameterSteps = 45; public const float PoleDiameterStep = .2f, PoleCodeSpan = 10;
        public static int PoleDiameterSteps(double health) =>
            double.IsNaN(health) || double.IsInfinity(health) || health < PoleCodeSpan ? 0 : Clamp((int)Math.Floor(health / PoleCodeSpan), 0, PoleMaxDiameterSteps);
        public static float PoleLengthOf(double health) => PoleLength(health - PoleCodeSpan * PoleDiameterSteps(health));
        public static float PoleCode(double metres, int steps) => PoleLength(metres) + PoleCodeSpan * Clamp(steps, 0, PoleMaxDiameterSteps);
        public static float PoleDiameterScale(int steps) => 1 + PoleDiameterStep * Clamp(steps, 0, PoleMaxDiameterSteps);
        public static float PoleMass(double metres, int steps) { float k = PoleDiameterScale(steps); return PoleMass(metres) * k * k; }

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
        // A torn tarp's holes are seeded by its sides (to 0.1 m), in order of length: the order of the sides round the tarp can
        // start at either of two corners after a load (Tarps.Order: the corner opposite the first lies at an angle of +-pi, its sign
        // a float's), which moved the holes (the review of 2026-10-18, Claude's fix; each torn tarp's holes move once with it).
        // shortcut: a side within float noise of a 0.05 m boundary can still round the other way after a load; replace it by a
        // seed saved with the tarp if holes are seen to move.
        public static int TearSeed(float[] sides)
        {
            int seed = 17;
            foreach (int s in sides.Select(x => (int)Math.Round((double)(x * 10))).OrderBy(x => x)) seed = unchecked(seed * 31 + s);
            return seed;
        }
        // Which of the touching points that would fix one sheet point (each its height, whether it is a pole's end, its distance
        // seen from above to that sheet point's spot) fixes it: the highest, heights within 1 cm a tie, a pole's end first, then the
        // nearest. By height alone float noise decided along a level pole and one end could be left with no fixed point (the pole
        // end poking through: up to 40% of the grid's spacing; the user, 2026-10-18, B-B2 (b)). -1 when there are none.
        public const float TarpTie = .01f;
        public static int TarpClaim(IList<(float h, bool end, float dist)> c)
        {
            if (c == null || c.Count == 0) return -1;
            float top = c.Max(x => x.h); int best = -1;
            for (int i = 0; i < c.Count; i++)
            {
                if (c[i].h < top - TarpTie) continue;
                if (best < 0 || c[i].end && !c[best].end || c[i].end == c[best].end && c[i].dist < c[best].dist) best = i;
            }
            return best;
        }

        // ---- The wooden pallet and the wood vendors ----
        // The wood prices (PALLET_JUNK_CREW_DRAFT.md §4): hundredths of what Kicia Bay's trade book asks for one lumber (Kicia Bay
        // makes the lumber), at every vendor in its own port's coin. Kicia Bay: a plank bundle 10/100 and a pallet 20/100, as before
        // ("Kicia bay wood bundle price stay as it is", "Pallet price is double from wood plank bundle (in code use the actual
        // number...)"); the other ports 20% more (the user, 2026-10-24: "Change the price to let it be only 20% more expensive at
        // this 3 city compare to kicia bay"): 12/100 and 24/100.
        public const int BundleHundredthsKicia = 10, BundleHundredthsElsewhere = 12, PalletHundredthsKicia = 20, PalletHundredthsElsewhere = 24;
        public const int KiciaPort = 22;
        public static int WoodHundredths(bool pallet, int port) =>
            pallet ? (port == KiciaPort ? PalletHundredthsKicia : PalletHundredthsElsewhere) : (port == KiciaPort ? BundleHundredthsKicia : BundleHundredthsElsewhere);
        // A share of a lumber price, rounded up (as the bundle's 1/10 was); an exact quotient stays exact in the division.
        public static int WoodShare(int lumber, int hundredths) => (int)Math.Ceiling(lumber * (long)hundredths / 100.0);

        // The wooden pallet (the user, 2026-10-24): its top 1.45 x 1.28 m (P8), 0.25 m high, 80 lb (the user, 2026-10-24; the game's
        // mass is in pounds, as its mission board prints it). A sale takes 2/16 of a lumber from the port's stock (a plank bundle 1/16).
        public const float PalletWidth = 1.45f, PalletDepth = 1.28f, PalletHeight = .25f, PalletMass = 80, PalletLumber = 2f / 16;
        public const int PalletValue = 750;
        // Its top boards 40 mm (PALLET_PLANK_DRAFT.md P9, the user: "Make the wood plank at top of pallet be thicker a bit"; was 25 mm;
        // Data/MakePallet.py), the height kept. Its wood the planks' material 15% darker (P8, the user: "pick 15% darker for pallet",
        // from the pictures of probe palletshow run 20261024-162516): the material's colour times 0.85.
        public const float PalletTopBoards = .04f, PalletShade = .85f;

        // The ropes round a full pallet whose every crate is nailed (P4, the user: "rope appear when pallet is full and every crate is
        // nailed"; "couple rope to tie up all cargo, vertical one goes below pallet"): two vertical loops across the pallet's width,
        // under its top boards through the openings between its three runners (z ±0.295, the openings' middles: the runners stand at
        // the ends and the middle, 0.10 m wide), and one band round the load at 55% of its height. Each loop in the pallet's frame
        // (x across, y up, z along), the rope's middle line, `r` its half thickness; closed (the last point joins the first).
        public const float RopeHalf = .0125f, RopeOpening = .295f, RopeBand = .55f;
        public static bool Roped(int slots, int loaded, int nailed) => slots > 0 && loaded == slots && nailed == slots;
        public static (Vector3 min, Vector3 max) LoadBox(PalletLoad load)
        {
            var size = LoadSize(load); Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            foreach (var s in LoadSlots(load))
            {
                var c = s + new Vector3(0, PalletHeight + size.y / 2, 0);
                lo = Vector3.Min(lo, c - size / 2); hi = Vector3.Max(hi, c + size / 2);
            }
            return (lo, hi);
        }
        public static List<Vector3[]> RopeLoops(PalletLoad load)
        {
            var loops = new List<Vector3[]>(); if (LoadSlots(load).Length == 0) return loops;
            var (lo, hi) = LoadBox(load); float r = RopeHalf;
            float x = Math.Max(Math.Max(-lo.x, hi.x), PalletWidth / 2) + r, top = hi.y + r, under = PalletHeight - PalletTopBoards - r;
            foreach (float z in new[] { -RopeOpening, RopeOpening })
                loops.Add(new[] { new Vector3(-x, under, z), new Vector3(-x, top, z), new Vector3(x, top, z), new Vector3(x, under, z) });
            float y = PalletHeight + (hi.y - PalletHeight) * RopeBand, bx = Math.Max(-lo.x, hi.x) + r, bz = Math.Max(-lo.z, hi.z) + r;
            loops.Add(new[] { new Vector3(-bx, y, -bz), new Vector3(-bx, y, bz), new Vector3(bx, y, bz), new Vector3(bx, y, -bz) });
            return loops;
        }
        // What one pallet holds, one size at a time (the first crate decides), no higher than the large crate (P4 (c), P2, P8).
        public enum PalletLoad { None, LargeCrate, StandardCrate, SmallCrate, LargePackage, MediumPackage, StandardPackage, SmallPackage }
        public static PalletLoad LoadOf(string sizeDescription)
        {
            switch ((sizeDescription ?? "").Trim().ToLowerInvariant())
            {
                case "large crate": return PalletLoad.LargeCrate;
                case "standard crate": return PalletLoad.StandardCrate;
                case "small crate": return PalletLoad.SmallCrate;
                case "large package": return PalletLoad.LargePackage;
                case "medium package": return PalletLoad.MediumPackage;
                case "standard package": return PalletLoad.StandardPackage;
                case "small package": return PalletLoad.SmallPackage;
                default: return PalletLoad.None;
            }
        }
        // Each load's drawn size as it stands on the pallet (across its 1.45 m, up, along its 1.28 m: the meshes measured by the probe
        // palletshow, 2026-10-24) and its slots: the bottom middles on the pallet's top, in the pallet's frame (x across, z along).
        public static Vector3 LoadSize(PalletLoad load)
        {
            switch (load)
            {
                case PalletLoad.LargeCrate: return new Vector3(1.270f, 1.093f, 1.229f);         // upright
                case PalletLoad.StandardCrate: return new Vector3(.710f, 1.029f, 1.270f);       // on its long side
                case PalletLoad.SmallCrate: return new Vector3(.462f, 1.056f, .626f);           // on its end
                case PalletLoad.LargePackage: return new Vector3(1.436f, .968f, 1.460f);        // lying, 9 cm over each end
                case PalletLoad.MediumPackage: return new Vector3(.698f, 1.016f, 1.216f);       // on its side
                case PalletLoad.StandardPackage: return new Vector3(1.866f, 1.066f, .606f);     // on its side, 21 cm over each side
                case PalletLoad.SmallPackage: return new Vector3(1.466f, 1.066f, .406f);        // on its side
                default: return Vector3.zero;
            }
        }
        public static Vector3[] LoadSlots(PalletLoad load)
        {
            switch (load)
            {
                case PalletLoad.LargeCrate: case PalletLoad.LargePackage: return new[] { Vector3.zero };
                case PalletLoad.StandardCrate: return new[] { new Vector3(-.360f, 0, 0), new Vector3(.360f, 0, 0) };
                case PalletLoad.SmallCrate: return new[] { -.478f, 0f, .478f }.SelectMany(x => new[] { new Vector3(x, 0, -.32f), new Vector3(x, 0, .32f) }).ToArray();
                case PalletLoad.MediumPackage: return new[] { new Vector3(-.352f, 0, 0), new Vector3(.352f, 0, 0) };
                case PalletLoad.StandardPackage: return new[] { new Vector3(0, 0, -.31f), new Vector3(0, 0, .31f) };
                case PalletLoad.SmallPackage: return new[] { new Vector3(0, 0, -.42f), Vector3.zero, new Vector3(0, 0, .42f) };
                default: return new Vector3[0];
            }
        }
        // The turn that shows an item of drawn size `size` (in its own frame) closest to `want` (across, up, along): the axis order
        // with the least difference; then which item axis lies along each of the pallet's (0 x, 1 y, 2 z), as (along z, up y).
        public static (int z, int y) LoadAxes(Vector3 size, Vector3 want)
        {
            int[] best = null; float err = float.MaxValue;
            foreach (var perm in new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } })
            {
                float e = Math.Abs(size[perm[0]] - want.x) + Math.Abs(size[perm[1]] - want.y) + Math.Abs(size[perm[2]] - want.z);
                if (e < err) { err = e; best = perm; }
            }
            return (Array.IndexOf(best, 2), Array.IndexOf(best, 1));
        }
        // The vendor's stock (the user, 2026-10-24: "they will sell multiple pallet at once, stacking high"; then "make plank bundle
        // sell 12 at GRC, Dragon cliff and Fort Aestrin", "pole bundle sell 10 at GRC, Dragoncliff and fort aestrin"): 8 pallets a
        // stack, 2 mm apart so no two touch; 12 plank bundles (2 across, 6 high); 9 pole bundles 3 across and 3 high, 0.60 m, lower
        // than the planks' 0.71 m ("please make the pole stack higher instead of wider", "remove 2 bundle of wood pole so it's still
        // lower than plank", then "9 bundle, 3x3"). Kicia Bay keeps 6 pole bundles (3 across, 2 high; "all vendor sell 6 wood poles
        // bundle"); its plank bundles are the stall's own 8.
        public const int VendorPallets = 8, VendorPlankBundles = 12, VendorPoleBundles = 9, KiciaPoleBundles = 6;
        public static int PoleBundlesAt(bool shared) => shared ? KiciaPoleBundles : VendorPoleBundles;
        public static int PoleColumnsAt(bool shared) => 3;
        public const float PalletStep = .252f, PlankBundleStep = .119f, PlankBundleAcross = .34f, PoleBundleStep = .2f;
        // Rows (against a building), the middles from the block's back: the plank bundles (0.71 m high) right before the pallets, at z
        // 1.43..2.11, the lower pole stack (0.60 m) in front at z 2.21..2.81, so the rows step down to the front and none hides
        // another (the user: "so pallet stack don't block other 2 item").
        public const float PlankRowsZ = 1.77f, PoleRowsZ = 2.51f;
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
                // By one end it is held as the pole's staff (WoodPoles.HeldPose): the same held look, shown by PlankMark.
                var rootLook = plank.GetComponent<MeshRenderer>();
                var held = new GameObject(WoodPoles.HeldLookName); held.transform.SetParent(plank.transform, false); held.SetActive(false);
                held.AddComponent<MeshFilter>().sharedMesh = looks[0];
                var heldRenderer = held.AddComponent<MeshRenderer>(); heldRenderer.sharedMaterials = rootLook.sharedMaterials;
                heldRenderer.shadowCastingMode = rootLook.shadowCastingMode; heldRenderer.receiveShadows = rootLook.receiveShadows;
                plank.AddComponent<PlankMark>();
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
        // Its look, size and attach mode, all in its saved health (Rules.PlankCode; PALLET_PLANK_DRAFT.md, "The plank's build plan").
        internal static readonly HashSet<ShipItem> Live = new HashSet<ShipItem>();   // every wood plank in the game (PlankMark)
        internal static Rules.PlankShape Shape(ShipItem plank) => Rules.PlankDecode(plank ? plank.health : 0);
        internal static int Look(ShipItem plank) => Shape(plank).look;
        internal static bool EndMode(ShipItem plank) => IsPlank(plank) && Shape(plank).end;
        internal static float Length(ShipItem plank) => Rules.PlankLength(Shape(plank).length);
        internal static float Width(ShipItem plank) => Rules.PlankWidth(Shape(plank).width);
        internal static string SizeText(ShipItem plank) =>
            Length(plank).ToString(Shape(plank).length == 0 ? "F2" : "F1", CultureInfo.InvariantCulture) + " m x " + Width(plank).ToString("F2", CultureInfo.InvariantCulture) + " m";   // today's 0.87 m, not rounded to 0.9
        internal static void SetLook(ShipItem plank, int look) { var s = Shape(plank); s.look = look >= 0 && look < Looks ? look : 0; SetShape(plank, s); }
        internal static void SetShape(ShipItem plank, Rules.PlankShape s) { if (!plank) return; plank.health = Rules.PlankCode(s); ApplyLook(plank); }
        // After spawning, loading or a change: today's look mesh for today's plank lying flat, else one made at its size and mode
        // (MadeMesh); its box and its physics copy's box (copied from it once, ItemRigidbody.AddCollider) and its weight follow, as the
        // pole's do; the staff in the hand by one end.
        internal static void ApplyLook(ShipItem plank)
        {
            if (!plank || PlankMeshes == null) return;
            var s = Shape(plank); var mesh = Rules.PlankPlain(s) ? PlankMeshes[s.look] : MadeMesh(s);
            var filter = plank.GetComponent<MeshFilter>(); if (filter) filter.sharedMesh = mesh;
            var held = plank.transform.Find(WoodPoles.HeldLookName); if (held) { held.GetComponent<MeshFilter>().sharedMesh = mesh; WoodPoles.HeldPose(held, Length(plank), WoodPoles.Lean(plank)); }
            var box = plank.GetComponent<BoxCollider>(); if (box) { box.size = mesh.bounds.size; box.center = mesh.bounds.center; }
            var body = plank.itemRigidbodyC; var copy = body ? PoleBrace.boxCol(body) : null;
            if (copy) { copy.size = mesh.bounds.size; copy.center = mesh.bounds.center; }
            plank.mass = Rules.PlankMass(s);
            if (body && body.GetBody()) body.UpdateMass();
        }
        // A plank at another size or by one end: a box of its length, width and 45 mm, its wood from its look's piece of the atlas by the
        // look mesh's own mapping (MapOf): along its length mirrored back every 0.872 m (as the pole's grain), across its width one board
        // stretched to it. Flat, as the look meshes lie (length along x, width along y, from its back face at z 0 out to -z, centred where
        // they are); by one end as the pole stands (its end at the pivot, its length along -z, its width along x, its thickness along y).
        private static readonly Dictionary<float, Mesh> made = new Dictionary<float, Mesh>();
        internal static Mesh MadeMesh(Rules.PlankShape s)
        {
            float key = Rules.PlankCode(s);
            if (made.TryGetValue(key, out var cached) && cached) return cached;
            var map = MapOf(s.look);
            float L = Rules.PlankLength(s.length), W = Rules.PlankWidth(s.width), T = Rules.PlankThickness, L0 = Rules.PlankLength0, W0 = Rules.PlankWidth0;
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            // A point along it (sl from its end), across it (sw from its side), out of its back (z): where it lies, and a normal turned the same.
            Vector3 At(float sl, float sw, float z) => s.end ? new Vector3(sw - W / 2, z + T / 2, -sl) : new Vector3(map.centre.x - L / 2 + sl, map.centre.y - W / 2 + sw, z);
            Vector3 Turn(Vector3 d) => s.end ? new Vector3(d.y, d.z, -d.x) : d;
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
            {
                int i = v.Count; v.Add(a); v.Add(b); v.Add(c); v.Add(d); uv.Add(ua); uv.Add(ub); uv.Add(uc); uv.Add(ud);
                for (int k = 0; k < 4; k++) n.Add(normal);
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) >= 0) tri.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
                else tri.AddRange(new[] { i, i + 2, i + 1, i, i + 3, i + 2 });
            }
            // Cut points along (every 0.872 m, its grain turning back). Across, one board at any width: the look's board stretched over
            // the whole width (the user, 2026-10-24: "i want the widen plank to show widen plank, not multiple plank attached together";
            // repeated every 0.152 m until then, which drew a wide plank as boards side by side).
            var alongCuts = new List<float> { 0 }; for (float d = L0; d < L - .001f; d += L0) alongCuts.Add(d); alongCuts.Add(L);
            var acrossCuts = new List<float> { 0, W };
            float Grain(float d) => (float)Rules.Mirrored(d, L0);
            Vector2 Wood(float along, float across) => map.Uv(along, across * W0 / W);
            for (int i = 0; i + 1 < alongCuts.Count; i++)
            {
                float a0 = alongCuts[i], a1 = alongCuts[i + 1], g0 = Grain(a0), g1 = i % 2 == 0 ? g0 + (a1 - a0) : g0 - (a1 - a0);
                for (int j = 0; j + 1 < acrossCuts.Count; j++)
                {
                    float c0 = acrossCuts[j], c1 = acrossCuts[j + 1], w1 = c1 - c0;
                    foreach (float z in new[] { 0f, -T })   // its back and its face
                        Quad(At(a0, c0, z), At(a1, c0, z), At(a1, c1, z), At(a0, c1, z), Turn(z == 0 ? Vector3.forward : Vector3.back), Wood(g0, 0), Wood(g1, 0), Wood(g1, w1), Wood(g0, w1));
                }
                foreach (var (sw, normal) in new[] { (0f, Vector3.down), (W, Vector3.up) })   // its two long edges
                    Quad(At(a0, sw, 0), At(a1, sw, 0), At(a1, sw, -T), At(a0, sw, -T), Turn(normal), map.Uv(g0, 0), map.Uv(g1, 0), map.Uv(g1, T), map.Uv(g0, T));   // its thickness, not stretched
            }
            for (int j = 0; j + 1 < acrossCuts.Count; j++)
            {
                float c0 = acrossCuts[j], c1 = acrossCuts[j + 1], w1 = c1 - c0;
                foreach (var (sl, normal) in new[] { (0f, Vector3.left), (L, Vector3.right) })   // its two ends
                    Quad(At(sl, c0, 0), At(sl, c1, 0), At(sl, c1, -T), At(sl, c0, -T), Turn(normal), Wood(0, 0), Wood(0, w1), Wood(T, w1), Wood(T, 0));
            }
            var m = new Mesh { name = "wood plank " + L.ToString("F1", CultureInfo.InvariantCulture) + " x " + W.ToString("F3", CultureInfo.InvariantCulture) + (s.end ? " by one end" : "") };
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(tri, 0);
            m.RecalculateBounds(); m.RecalculateTangents();
            return made[key] = m;
        }
        // A look mesh's mapping of its face to the atlas, fitted once (least squares) on its face's corners: the atlas point of a spot
        // `along` metres from its end and `across` from its side, as that look lies on today's plank.
        internal sealed class WoodMap
        {
            internal Vector2 centre, start; internal Vector2 u, v;   // u, v: (per metre along x, per metre along y, at 0)
            internal float u0, v0;
            internal Vector2 Uv(float along, float across) { float x = start.x + along, y = start.y + across; return new Vector2(u.x * x + u.y * y + u0, v.x * x + v.y * y + v0); }
        }
        private static readonly Dictionary<int, WoodMap> maps = new Dictionary<int, WoodMap>();
        private static WoodMap MapOf(int look)
        {
            if (maps.TryGetValue(look, out var cached)) return cached;
            var mesh = PlankMeshes[look]; var pos = mesh.vertices; var nor = mesh.normals; var tex = mesh.uv;
            double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, cnt = 0, sxu = 0, syu = 0, su = 0, sxv = 0, syv = 0, sv = 0;
            float xmin = float.MaxValue, ymin = float.MaxValue;
            for (int i = 0; i < pos.Length; i++)
            {
                if (nor[i].z > -.5f) continue;   // its face, away from the wall
                double x = pos[i].x, y = pos[i].y, uu = tex[i].x, vv = tex[i].y;
                sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y; cnt++; sxu += x * uu; syu += y * uu; su += uu; sxv += x * vv; syv += y * vv; sv += vv;
                xmin = Mathf.Min(xmin, pos[i].x); ymin = Mathf.Min(ymin, pos[i].y);
            }
            var a = new double[,] { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, cnt } };
            var pu = Solve3(a, new[] { sxu, syu, su }); var pv = Solve3(a, new[] { sxv, syv, sv });
            var b = mesh.bounds;
            var map = new WoodMap { centre = new Vector2(b.center.x, b.center.y), start = new Vector2(xmin, ymin), u = new Vector2((float)pu[0], (float)pu[1]), u0 = (float)pu[2], v = new Vector2((float)pv[0], (float)pv[1]), v0 = (float)pv[2] };
            return maps[look] = map;
        }
        private static double[] Solve3(double[,] a, double[] r)
        {
            double Det(double[,] m) => m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
            double d = Det(a); var x = new double[3]; if (Math.Abs(d) < 1e-12) return x;
            for (int k = 0; k < 3; k++) { var m = (double[,])a.Clone(); for (int i = 0; i < 3; i++) m[i, k] = r[i]; x[k] = Det(m) / d; }
            return x;
        }
        // H (the brace key) on a held plank, bought and not nailed (K3, the user: "press H(same as bracing button) to switch attachment
        // mode, default is current mode"): flat or by one end, upright again, handed to Deft Hands afresh (its kept roll back to 0).
        internal static void ModeKey(GoPointer pointer, ShipItem held)
        {
            if (!pointer || !IsPlank(held) || !held.sold || held.nailed || !GameState.playing || GameState.inCursorMenu || !PoleBrace.KeyDown()) return;
            SwitchMode(held);
        }
        // H's two modes as the user names them (2026-10-24, word for word: "add notification when change between mode for plank, placement
        // mode (after press H), and repair mode"): repair mode flat (today's, the hull repair's), placement mode by one end.
        // The user's words (2026-10-24): 'Only show "Placement mode (Q/E length, O/L width)"', 'only show "Repair mode"'.
        internal const string PlacementNotice = "Placement mode (Q/E length, O/L width)", RepairNotice = "Repair mode";
        internal static void SwitchMode(ShipItem plank)
        {
            if (PoleCarry.Carries(plank)) { Loans.Notify(PoleCarry.LengthFixed); return; }   // what is on it would be left off it
            var s = Shape(plank); s.end = !s.end; SetShape(plank, s);
            WoodPoles.ResetLean(plank); DeftHands.Hold(plank);
            Loans.Notify(s.end ? WoodPlanks.PlacementNotice : WoodPlanks.RepairNotice);
        }
        // No pick-up notice for a plank (the user, 2026-10-24: "cancel this"): H's notices say its mode.

        // Kicia Bay furniture stall: 8 bundles stacked 2 wide x 4 high on the stall floor between the bed and the stove,
        // in the stall area's own (scaled) frame: x across, y up, z along. The heights re-spaced for the low-poly bundle (0.117 m
        // thick, was 0.136; the stall's up scale 4.9341, read by the probe palletshow, 2026-10-24): the bottom bundle's foot kept, the
        // same 1.8 mm between bundles.
        // A shop slot (or the mod's shop area) under the game's scenery drawn at its real size: scaled against its parent's scale. The
        // game sets a spawned item's local scale to 1 every frame (ItemRigidbody.LateUpdate), so an item takes its slot's world scale;
        // Gold Rock City's whole island scenery is at 0.5 (its own keepers at 2), and the wood vendor's stock there was drawn at half
        // size, outside its halved shop area, so nothing could be bought (the user, 2026-10-24). Nothing changes under an unscaled parent.
        internal static void ScaleOne(Transform t)
        {
            var p = t && t.parent ? t.parent.lossyScale : Vector3.one;
            if (Mathf.Abs(p.x) > 1e-4f && Mathf.Abs(p.y) > 1e-4f && Mathf.Abs(p.z) > 1e-4f) t.localScale = new Vector3(1 / p.x, 1 / p.y, 1 / p.z);
        }
        internal static readonly float[] StockAcross = { -.16063f, -.11537f }, StockHeight = { -.30697f, -.28289f, -.25881f, -.23473f };
        internal static Mesh StockMesh => bundleMesh;
        internal const float StockAlong = .0153f;
        internal const string StallName = "shop area (15)";
        internal static void AddStock(ShopArea area)
        {
            if (!Registered || Uninstall.Prepared || !area || area.name != StallName || area.gameObject.scene.buildIndex != KiciaScene) return;
            var stall = area.transform; var along = Vector3.ProjectOnPlane(stall.forward, Vector3.up).normalized;
            var rotation = Quaternion.LookRotation(Vector3.up, along);   // bundle length (mesh y) along the stall, thickness (mesh z) up
            foreach (float x in StockAcross)
                foreach (float y in StockHeight)
                {
                    var slot = new GameObject("EconomyOverhaul wood plank bundle stock");
                    slot.transform.SetParent(stall.parent, false); ScaleOne(slot.transform);
                    slot.transform.SetPositionAndRotation(stall.TransformPoint(new Vector3(x, y, StockAlong)) - rotation * bundleMesh.bounds.center, rotation);
                    var spawner = slot.AddComponent<ShopItemSpawner>(); spawner.itemPrefab = BundleTemplate; spawner.availableAtNight = false; spawner.priceMult = 1;
                }
        }
        // A share of what Kicia Bay's trade book asks for one lumber right now, in this market's coin with its fees (rounded up): a
        // plank bundle 10/100 at Kicia Bay and 12/100 elsewhere, a pallet 20/100 and 24/100 (Rules.WoodHundredths). The lumber taken
        // for it still comes off this market's stock (BundleSold).
        internal static MoneyQuote BundleQuote(IslandMarket m, bool pallet = false)
        {
            var kicia = World.Market(Rules.KiciaPort) ?? m;
            float basePrice = kicia.GetGoodPriceAtSupply(LumberGood, kicia.currentSupply[LumberGood]);
            int raw = Mathf.RoundToInt(basePrice * (1 + (float)Rules.Spread(basePrice)));
            var lumber = Fx.Quote(raw, m, Fx.Currency(m), true); int share = Rules.WoodHundredths(pallet, m.GetPortIndex());
            return new MoneyQuote { Gross = Rules.WoodShare(lumber.Gross, share), Total = Rules.WoodShare(lumber.Total, share), Fee = lumber.Fee };
        }
        internal static void BundleSold(IslandMarket m, bool pallet = false) { if (!m) return; m.currentSupply[LumberGood] -= pallet ? Rules.PalletLumber : BundleLumber; Economy.Refresh(m); }

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
            if (!IsPlank(plank) || !plank.sold || plank.held || !plank.currentActualBoat || !plank.currentWalkCol || Shape(plank).end) return null;   // only flat (K3)
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
    { static void Prefix(PrefabsDirectory __instance) { WoodPlanks.Register(__instance); Tarps.Register(__instance); WoodPoles.Register(__instance); WoodPallets.Register(__instance); } }
    [HarmonyPatch(typeof(ShopArea), "Awake")] internal static class PlankStockPatch
    { static void Postfix(ShopArea __instance) { WoodPlanks.AddStock(__instance); Tarps.AddStock(__instance); WoodVendors.Build(__instance); } }
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
        // 8 sides (the user, 2026-10-24: "reduce the poly counts"; was 12: a 2 m pole 48 triangles, now 32; the bundle 240, now 160).
        // Not 6: a hexagon's bounds are 13% narrower across its flats, and the pole's box collider is its mesh's bounds.
        private const int Sides = 8;
        // The lumber's material is the items' atlas (senna items paint), mostly black: the pole takes one long board strip of it,
        // the one the planks use (u .7637-.7773, v .236-.861; one dark brown only from v .25 to .61, used from .26: Rules.PoleStrip),
        // at the planks' scale (a 0.87 m board face spans 0.08 along the grain).
        private const float UvPerMetre = .0925f, U0 = .7645f, UWidth = .0120f, V0 = .26f;
        internal static GameObject Template, BundleTemplate;
        internal static bool Registered => Template;
        internal static readonly HashSet<ShipItem> Live = new HashSet<ShipItem>();   // every wood pole in the game (PoleHeldLook)
        private static readonly Dictionary<int, Mesh> meshes = new Dictionary<int, Mesh>();
        internal static bool IsPole(ShipItem item) => Registered && WoodPlanks.Index(item) == PoleIndex;
        internal static bool IsBundle(ShipItem item) => BundleTemplate && WoodPlanks.Index(item) == BundleIndex;
        internal static float Length(ShipItem pole) => Rules.PoleLengthOf(pole.health);
        // Its diameter (O/L: Rules.PoleDiameterSteps), kept in the health with its length.
        internal static int DiameterSteps(ShipItem pole) => Rules.PoleDiameterSteps(pole.health);
        internal static float Diameter(ShipItem pole) => Thickness * Rules.PoleDiameterScale(DiameterSteps(pole));
        // Its size as the line under the view and the item's text show it: the length, and the diameter once thicker than 1.0x.
        internal static string SizeText(ShipItem pole) => Format(Length(pole)) + (DiameterSteps(pole) > 0 ? " x " + (Diameter(pole) * 100).ToString("F1", CultureInfo.InvariantCulture) + " cm" : "");
        // A rod: a pole, or a plank by one end (PALLET_PLANK_DRAFT.md: "it receive same aiming and attaching mechanics as wooden pole
        // now"): held as the staff, tilted and leaned, Q/E its length, Deft Hands' roll counted.
        internal static bool IsRod(ShipItem item) => IsPole(item) || WoodPlanks.EndMode(item);
        internal static float RodLength(ShipItem item) => IsPole(item) ? Length(item) : WoodPlanks.Length(item);
        internal static bool ShowsHeldLook(ShipItem item) => item && item.held && IsRod(item);
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

        // One mesh per length and diameter, made once: 8 sides with smooth normals, flat end caps, the pivot at the base (z 0), the
        // pole along -z; the grain runs along it (the same strip across it at any diameter).
        internal static Mesh Mesh(float metres, int steps = 0)
        {
            int tenths = Rules.PoleTenths(metres); steps = Mathf.Clamp(steps, 0, Rules.PoleMaxDiameterSteps); int key = tenths + 1000 * steps;
            if (meshes.TryGetValue(key, out var cached) && cached) return cached;
            float length = tenths / 10f, r = Thickness * Rules.PoleDiameterScale(steps) / 2;
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            // Along it the strip's dark wood runs to and fro over 3.4 m lengths, mirrored (Rules.PoleGrain): one colour at any length
            // (the user, 2026-10-13: a pole changed colour beyond 3.9 m, and an 8 m pole showed a ring where it turned at v .25).
            var rings = new List<float> { 0 };
            for (double d = Rules.PoleStrip; d < length - .001; d += Rules.PoleStrip) rings.Add((float)d);
            rings.Add(length);
            for (int i = 0; i <= Sides; i++)   // across the strip and back again (mirrored: no seam, half the stretch)
            {
                float a = i * 2 * Mathf.PI / Sides; var out_ = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                float u = U0 + UWidth * (1 - Mathf.Abs(2f * i / Sides - 1));
                foreach (var d in rings) { v.Add(out_ * r + Vector3.back * d); n.Add(out_); uv.Add(new Vector2(u, V0 + (float)Rules.PoleGrain(d) * UvPerMetre)); }
            }
            int k = rings.Count;
            for (int i = 0; i < Sides; i++) for (int j = 0; j < k - 1; j++) { int p = i * k + j, q = p + k; t.AddRange(new[] { p, p + 1, q, q, p + 1, q + 1 }); }
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
            var m = new Mesh { name = "wood pole " + Format(length) + (steps > 0 ? " x" + Rules.PoleDiameterScale(steps).ToString("F1", CultureInfo.InvariantCulture) : "") };
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

        // A length on a pole, its diameter kept (SetShape).
        internal static void SetLength(ShipItem pole, float metres) { if (pole) SetShape(pole, metres, DiameterSteps(pole)); }
        // A length and diameter on a pole: its health (Rules.PoleCode), its mesh (the game holds the root at scale 1; the placement
        // ghost and the outline use the root mesh), its box and its physics copy's box (copied from it once, ItemRigidbody.AddCollider),
        // its weight.
        internal static void SetShape(ShipItem pole, float metres, int steps)
        {
            if (!pole) return;
            float length = Rules.PoleLength(metres); steps = Mathf.Clamp(steps, 0, Rules.PoleMaxDiameterSteps); pole.health = Rules.PoleCode(length, steps);
            var mesh = Mesh(length, steps);
            var filter = pole.GetComponent<MeshFilter>(); if (filter) filter.sharedMesh = mesh;
            var held = pole.transform.Find(HeldLookName); if (held) { held.GetComponent<MeshFilter>().sharedMesh = mesh; HeldPose(held, length, Lean(pole)); }
            var box = pole.GetComponent<BoxCollider>(); if (box) { box.size = mesh.bounds.size; box.center = mesh.bounds.center; }
            var body = pole.itemRigidbodyC; var copy = body ? Fields.Get<BoxCollider>(body, "boxCol") : null;
            if (copy) { copy.size = mesh.bounds.size; copy.center = mesh.bounds.center; }
            pole.mass = Rules.PoleMass(length, steps);
            if (body && body.GetBody()) body.UpdateMass();
        }
        internal static void ApplyLength(ShipItem pole) => SetShape(pole, Length(pole), DiameterSteps(pole));

        // Held, a wall item points its +z ahead (to aim it at a wall), so the pole, along -z, would run back through the player's
        // eyes. Held, it is drawn standing up from the hand like a staff, tilted 15 degrees forward, a third of it below the hand, 30 cm right of the crosshair (where the game holds a wall item)
        // (the user, 2026-10-12). The root keeps its own mesh and direction: the placement ghost shows where it will stand.
        internal const string HeldLookName = "wood pole held look";
        private static readonly Vector3 HeldAxis = new Vector3(0, Mathf.Cos(15 * Mathf.Deg2Rad), Mathf.Sin(15 * Mathf.Deg2Rad));
        // Leaned by the wheel (below): turned about the view's line at the hand, its top to the right for a positive lean.
        internal static void HeldLocal(float length, float lean, out Vector3 at, out Quaternion turn)
        {
            var roll = Quaternion.AngleAxis(-lean, Vector3.forward);
            turn = roll * Quaternion.FromToRotation(Vector3.back, HeldAxis); at = Vector3.right * .3f + roll * (-HeldAxis * (length / 3));
        }
        internal static void HeldPose(Transform look, float length, float lean = 0)
        { HeldLocal(length, lean, out var at, out var turn); look.localRotation = turn; look.localPosition = at; look.localScale = Vector3.one; }

        // ---- Tilting and leaning (the user, 2026-10-17) ----
        // Held, the mouse wheel tilts the pole toward or away, as the game turns any held item (its heldRotationOffset, about the
        // view's side-to-side axis), and with the game's rotate key held (RotateH, Q by default; the game's own turn with it does
        // nothing to a small item) it leans the pole left or right in the view. Each 3 degrees a notch (the user, 2026-10-17; the game sends 2.5 a notch:
        // the scroll axis, 0.1, x 25), at most 90 degrees either way; braced, the wheel tilts the brace, at most 80 (PoleBrace.MaxTilt; TiltLimit).
        // The staff in the hand shows both, and the pole attaches at both on any surface, the game's green ghost showing it: on a deck
        // or a roof (its normal more than 0.8 up or down; the game itself tells apart only one facing up) leaned toward the view's right or left and tilted
        // away or toward; on a wall, leaned along it level and tilted up or down. The length line shows the angle changed last.
        // Upright again at each pick-up.
        internal const float MaxLean = 90, DegreesPerInput = 1.2f;
        private static ShipItem leanPole; private static float lean; private static char changed;   // 't' tilt, 'l' lean, last changed
        internal static float Lean(ShipItem pole) => pole && pole == leanPole ? lean : 0;
        // The tilt's limit: 90 held, the brace's 80 while braced, so the wheel and the length line stop where the brace does. With
        // Deft Hands installed no limit held (its tilt turns a held item without one): the pole attaches at the angle it draws
        // (the user, 2026-10-18: "If deft hand is present, no 90 degree limit"; A-B4). The brace keeps its 80.
        internal const float NoTiltLimit = 180;
        internal static float TiltLimit(ShipItem pole) => pole && PoleBrace.Braced == pole ? PoleBrace.MaxTilt : DeftHands.Installed ? NoTiltLimit : MaxLean;
        internal static float Tilt(ShipItem pole) => pole ? Mathf.Clamp(Mathf.DeltaAngle(0, pole.heldRotationOffset), -TiltLimit(pole), TiltLimit(pole)) : 0;
        internal static void ResetLean(ShipItem pole)
        {
            if (!pole) return;
            if (pole == leanPole) { lean = 0; changed = default; }
            pole.heldRotationOffset = 0;   // the game keeps a small item's wheel turn from one pick-up to the next
            var look = pole.transform.Find(HeldLookName); if (look) HeldPose(look, RodLength(pole));
        }
        private static void Own(ShipItem pole) { if (pole != leanPole) { leanPole = pole; lean = 0; changed = default; } }
        // PickupableItem.OnScroll (the wheel without the rotate key): false when it tilted a held pole (the game's own turn skipped).
        internal static bool Scroll(PickupableItem item, float input)
        {
            var pole = item as ShipItem;
            if (!IsRod(pole) || !pole.sold || !pole.held) return true;
            Own(pole); changed = 't';
            float limit = TiltLimit(pole), tilt = Tilt(pole) + input * DegreesPerInput;
            pole.heldRotationOffset = limit >= NoTiltLimit ? Mathf.DeltaAngle(0, tilt) : Mathf.Clamp(tilt, -limit, limit);   // no limit: it turns on round
            return false;
        }
        // GoPointer.LateUpdate, before the game's own: the wheel with the rotate key held leans a held pole. The game would turn it
        // about the view's up (undone the same frame for a small item) and set its tilt to 0: the wheel is kept from it this frame
        // (GameInput.GetScrollAxis 0 until the game's LateUpdate is done). Braced, it leans nothing (the user, 2026-10-17: Q is only
        // the length key there); the wheel is still kept from the game, whose turn would set the brace's tilt to 0.
        internal static bool KeepScroll;
        private static int leanFrame = -1;   // the frame the wheel last leaned the pole (Keys: Q let go after it lengthens nothing)
        internal static Func<bool> TestLeanKey;   // tests hold the rotate key with this (no key can be pressed in a test)
        internal static float TestWheel;   // and turn the wheel with this (as the game reads it: 2.5 a notch), used once
        internal static void LeanScroll(GoPointer pointer)
        {
            KeepScroll = false; float test = TestWheel; TestWheel = 0;
            var pole = pointer ? pointer.GetHeldItem() as ShipItem : null;
            if (!IsRod(pole) || !pole.sold || !(TestLeanKey?.Invoke() ?? GameInput.GetKey(InputName.RotateH))) return;
            float input = test != 0 ? test : GameInput.GetScrollAxis() * 25;
            if (input == 0) return;
            KeepScroll = true;
            if (PoleBrace.Braced == pole) return;
            Own(pole); changed = 'l'; leanFrame = Time.frameCount;
            lean = LeanLimit >= NoTiltLimit ? Mathf.DeltaAngle(0, lean + input * DegreesPerInput) : Mathf.Clamp(lean + input * DegreesPerInput, -MaxLean, MaxLean);
            var look = pole.transform.Find(HeldLookName); if (look) HeldPose(look, RodLength(pole), lean);
        }
        // Other mods' roll of the held pole about the view's line (Deft Hands' "Wall attachment rotation": its Alt and the mouse;
        // the user, 2026-10-17: attach it at the angle Deft Hands shows), measured after every mod's turn of the frame
        // (GoPointer.LateUpdate) from its turn in the hand beyond the game's tilt; it adds to the lean (SeenLean). Only a turn about
        // the view's line counts (the game's pick-up still swinging it into the hand does not).
        private static ShipItem rolledPole; private static float otherRoll; private static int rolledFrame = -10;
        internal static void SeeHeld(GoPointer pointer)
        {
            var pole = pointer ? pointer.GetHeldItem() as ShipItem : null;
            if (!IsRod(pole) || !pole.sold || PoleBrace.Braced == pole) return;
            var beyond = Quaternion.Inverse(pointer.transform.rotation * Quaternion.Euler(pole.heldRotationOffset, 0, 0)) * pole.transform.rotation;
            float roll = Vector3.Angle(beyond * Vector3.forward, Vector3.forward) > 1 ? 0 : Vector3.SignedAngle(Vector3.up, beyond * Vector3.up, Vector3.forward);
            rolledPole = pole; otherRoll = Mathf.Abs(roll) < .05f ? 0 : roll; rolledFrame = Time.frameCount;
        }
        // The lean the held pole is seen at: its own (on the held look) and other mods' roll of it (a positive roll tips its top left).
        // With Deft Hands installed no 90-degree limit on the lean either (its roll reaches 180): the pole attaches at the angle it
        // draws (the user, 2026-10-19, Q2 of the deep review: "b", as A-B4 for the tilt).
        internal static float LeanLimit => DeftHands.Installed ? NoTiltLimit : MaxLean;
        internal static float SeenLean(ShipItem pole)
        {
            float l = Lean(pole) - (pole && pole == rolledPole && Time.frameCount - rolledFrame <= 2 ? otherRoll : 0);
            return LeanLimit >= NoTiltLimit ? Mathf.DeltaAngle(0, l) : Mathf.Clamp(l, -MaxLean, MaxLean);
        }
        // The angle changed last, as "tilt +15" (away or toward) or "lean -20" (right or left) with a degree sign, "deg" where the
        // font has none.
        internal static string AngleText(ShipItem pole, bool degreeSign)
        {
            if (!pole || pole != leanPole || changed == default) return "";
            int v = Mathf.RoundToInt(changed == 't' ? Tilt(pole) : lean);
            return ", " + (changed == 't' ? "tilt " : "lean ") + (v > 0 ? "+" : "") + v + (degreeSign ? "\u00B0" : " deg");
        }
        // A pole's turn on a surface (its normal out of it) tilted and leaned, `right` the view's right in the same space. The
        // surface's own frame: its side on the view's right (on a deck along the view's right, on a wall along it level), out of it,
        // and the third way (on a deck away from the view, on a wall up); the pole turned in it as the staff in the hand is: leaned
        // about the third way (positive: toward the side on the right), then tilted about the side (positive: the third way).
        internal static Quaternion Tipped(Vector3 normal, Quaternion straight, float tilt, float lean, Vector3 right)
        {
            if (Mathf.Abs(tilt) < .01f && Mathf.Abs(lean) < .01f) return straight;
            Vector3 side;
            if (Mathf.Abs(normal.y) > .8f) side = Vector3.ProjectOnPlane(right, normal);
            else { side = Vector3.Cross(Vector3.up, normal); if (Vector3.Dot(side, right) < 0) side = -side; }
            if (side.sqrMagnitude < 1e-6f) return straight;
            var frame = Quaternion.LookRotation(Vector3.Cross(side.normalized, normal), normal);
            return frame * Quaternion.Euler(tilt, 0, 0) * Quaternion.AngleAxis(-lean, Vector3.forward) * Quaternion.Inverse(frame) * straight;
        }
        // The lift that rests a rod turned `turn` on a surface of normal `normal` (Rules.RestLift): its end face's axes are its x (a
        // plank's width) and y (its thickness), its length along -z.
        internal static float RestLift(ShipItem rod, Quaternion turn, Vector3 normal)
        {
            if (!IsRod(rod)) return 0;
            float side = Vector3.Dot(turn * Vector3.right, normal), up = Vector3.Dot(turn * Vector3.up, normal);
            return IsPole(rod) ? Rules.RestLift(side, up, Diameter(rod) / 2, Diameter(rod) / 2, true) : Rules.RestLift(side, up, WoodPlanks.Width(rod) / 2, Rules.PlankThickness / 2, false);
        }
        private static readonly MethodInfo setUpTargeter = AccessTools.Method(typeof(ShipItem), "SetUpTargeter");
        // After the game's own wall test of the frame (ShipItem.Update): its spot (in the space of the pole's physics copy, which the
        // drop puts there) tilted and leaned, and the game's ghost drawn again from it (its own SetUpTargeter maps that space to
        // where the pole is drawn). The game aims its ray along the copy, which the wheel's tilt turns with the pole, so the spot slid
        // as one tilted; tilted, the ray is cast again as the game casts it with the tilt taken out, and the pole tilts about the spot
        // it is aimed at, as it leans (the user, 2026-10-17). A spot pole on pole found (AimAtPole) is turned there.
        internal static void LeanOnWall(ShipItem held)
        {
            float l = SeenLean(held), t = Tilt(held);
            if (Mathf.Abs(l) < .01f && Mathf.Abs(t) < .01f || !IsRod(held) || !held.held || !held.itemRigidbodyC || AimedPole && aimedBy == held && aimedFrame == Time.frameCount) return;
            var copy = held.itemRigidbodyC.transform;
            var toCopy = copy.rotation * Quaternion.Inverse(held.transform.rotation);   // from where it is drawn to its copy's space
            if (Mathf.Abs(t) >= .01f && !Untilted(held, copy, toCopy)) return;
            if (!inRangeOfWall(held)) return;
            var straight = attachRot(held); var normal = straight * Vector3.back;
            attachRot(held) = Tipped(normal, straight, t, l, toCopy * held.held.transform.right);
            attachPos(held) += normal * RestLift(held, attachRot(held), normal);   // resting on the surface, not half in it
            try { setUpTargeter.Invoke(held, new object[] { attachPos(held), attachRot(held), held.GetComponent<MeshFilter>().sharedMesh }); }
            catch (Exception e) { Plugin.Log?.LogWarning("The tilted pole's ghost could not be drawn: " + (e.InnerException ?? e).Message); }
        }
        // The game's wall test (ShipItem.Update: a ray from the copy along its forward, 1.3 m; nearer than 0.1 m or nothing: out of
        // range) with the wheel's tilt taken out (the game turns a held item by it about the view's side-to-side axis, GoPointer). False
        // when out of range then, its ghost hidden this frame.
        private static readonly AccessTools.FieldRef<GoPointerTargeter, bool> targeterActive = AccessTools.FieldRefAccess<GoPointerTargeter, bool>("targeterActive");
        private static bool Untilted(ShipItem held, Transform copy, Quaternion toCopy)
        {
            float offset = Mathf.DeltaAngle(0, held.heldRotationOffset); var side = held.held.transform.right;
            var forward = Quaternion.AngleAxis(-offset, toCopy * side) * copy.forward;
            if (Physics.Raycast(new Ray(copy.position, forward), out var hit, 1.3f) && hit.distance >= .1f)
            {
                attachPos(held) = hit.point;
                attachRot(held) = Quaternion.LookRotation(-hit.normal, hit.normal.y > .8f ? Quaternion.AngleAxis(-offset, side) * held.transform.up : Vector3.up);
                inRangeOfWall(held) = true; return true;
            }
            inRangeOfWall(held) = false;
            var targeter = held.held.GetTargeter(); if (targeter) targeterActive(targeter) = false;
            return false;
        }

        // Q longer, E shorter, 0.1 m a press; E held repeats after 0.4 s. Only a held pole that is bought and isn't nailed (a
        // shop's pole can be picked up before buying it: resized, it would go back to its stall at the new length). Q is also the
        // game's rotate key, held with the wheel to lean the pole (LeanScroll; the user, 2026-10-17): not braced, a tap lengthens when
        // it is let go, and Q held without the wheel lengthens after 0.4 s and repeats, both only while the wheel hasn't leaned since
        // Q went down. Braced, Q is only the length key (the wheel leans nothing then): it lengthens as it goes down and, held,
        // repeats after 0.4 s, as E does.
        private static KeyCode repeating; private static float repeatAt;
        internal const float TapSeconds = .4f;
        private static bool qHeld, qBraced, qRepeated; private static int qDownFrame; private static float qRepeatAt;
        internal static Func<bool> TestQKey;   // tests hold Q with this (no key can be pressed in a test)
        private static bool testQWas;
        // On a nailed pole (the user, 2026-10-12: only on a nailed one), any wall item (2026-10-13: not only another pole). The game's own wall ray starts at the held pole's base,
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
        // What a held wall item attaches to by the crosshair: the poles, and the planks (K7, the user: plank on a nailed plank, "yes";
        // a plank's wide faces are found by the game's own wall ray too, its edges and ends hardly).
        private static IEnumerable<ShipItem> Targets()
        {
            foreach (var p in Live) yield return p;
            foreach (var p in WoodPlanks.Live) yield return p;
        }
        internal static void AimAtPole(ShipItem held, Ray eye)
        {
            // Its aim of an earlier frame is dropped, so a drop just after looking off a pole doesn't land on it (the review of
            // 2026-10-18, Claude's fix).
            AimedPole = null; if (aimedBy == held) aimedBy = null;
            if (!held.held) return;
            // The nearest nailed pole the ray meets, from its box where it is drawn: aboard a moving ship the physics of an item's
            // drawn collider does not follow her (measured by -PoleProbe), so the box is taken from its transform.
            ShipItem target = null; float near = AttachReach; Vector3 normal = default;
            foreach (var pole in Targets())
            {
                if (!pole || pole == held || !pole.sold || !pole.nailed || PoleCarry.Riding(pole)) continue;
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
            if (IsRod(held)) { turn = Tipped(normal, turn, Tilt(held), SeenLean(held), held.held.transform.right); at += normal * RestLift(held, turn, normal); }   // a held rod's tilt and lean, resting on it (LeanOnWall)
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
            // Aimed this frame (ShipItem.Update, where AimAtPole runs, comes before GoPointer.LateUpdate, where the game drops it).
            if (pole != aimedBy || aimedFrame != Time.frameCount || !aimedShip || !pole.itemRigidbodyC) return;
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

        // Picked up: its length and the keys, or that things locked on it fix its length (the user, 2026-10-13).
        internal static string PickupNotice(ShipItem pole) => "Wood pole " + SizeText(pole) + (PoleCarry.Carries(pole) ? " (things on it: size fixed)" : " (Q/E: length, O/L: diameter)");
        internal static void Keys(GoPointer pointer)
        {
            var held = pointer ? pointer.GetHeldItem() as ShipItem : null;
            PoleBrace.Keys(pointer, held);
            WoodPlanks.ModeKey(pointer, held);
            // Q/E the length of a rod (a plank only by one end: the user, "length and width only work in placement mode (after pressing
            // H)"), O/L a plank's width.
            if (!GameState.playing || !IsRod(held) || !held.sold || held.nailed) { repeating = KeyCode.None; qHeld = false; widthRepeating = KeyCode.None; return; }
            WidthKeys(held);   // a plank's width, a pole's diameter
            int step = 0; float now = Time.unscaledTime;
            if (Input.GetKeyDown(KeyCode.E)) { step = -1; repeating = KeyCode.E; repeatAt = now + .4f; }
            else if (repeating == KeyCode.E && Input.GetKey(KeyCode.E) && now >= repeatAt) { step = -1; repeatAt = now + .08f; }
            if (repeating == KeyCode.E && !Input.GetKey(KeyCode.E)) repeating = KeyCode.None;
            bool q, qDown;
            if (TestQKey != null) { q = TestQKey(); qDown = q && !testQWas; testQWas = q; }
            else { q = Input.GetKey(KeyCode.Q); qDown = Input.GetKeyDown(KeyCode.Q); testQWas = false; }
            if (qDown)
            {
                qHeld = true; qDownFrame = Time.frameCount; qBraced = PoleBrace.Braced == held; qRepeated = false; qRepeatAt = now + TapSeconds;
                if (qBraced) step = 1;
            }
            else if (qHeld && q && now >= qRepeatAt && (qBraced || leanFrame < qDownFrame)) { step = 1; qRepeated = true; qRepeatAt = now + .08f; }
            if (qHeld && !q) { if (!qBraced && !qRepeated && leanFrame < qDownFrame) step = 1; qHeld = false; }
            if (step == 0) return;
            if (PoleCarry.Carries(held)) { Loans.Notify(PoleCarry.LengthFixed); return; }   // what is on it would be left off it (PoleCarry)
            if (WoodPlanks.IsPlank(held))
            {
                var s = WoodPlanks.Shape(held); int to = Rules.PlankLengthStepped(s.length, step);
                if (to != s.length) { s.length = to; WoodPlanks.SetShape(held, s); }
                float metres = Rules.PlankLength(to);
                if (metres >= Rules.PlankMaxLength || metres <= Rules.PlankMinLength) Loans.Notify("Wood plank " + Format(metres) + (metres >= Rules.PlankMaxLength ? " (longest)" : " (shortest)"));
                return;
            }
            float before = Length(held), after = Rules.PoleLength(before + step * Rules.PoleStep);
            if (after != before) SetLength(held, after);
            // A notice only at the shortest or longest (user, 2026-10-13): the pole's hint text shows its length.
            if (after >= Rules.PoleMax || after <= Rules.PoleMin) Loans.Notify("Wood pole " + Format(after) + (after >= Rules.PoleMax ? " (longest)" : " (shortest)"));
        }
        // O wider, L narrower (the user, 2026-10-24: "change to O/L instead"; Z/C until then, and C is the game's camera mode key), a
        // quarter of today's width a press (K4, the user: "K4 use 0.25x then"), held: repeats after 0.4 s, as E. A pole's diameter the
        // same keys, 0.2x a press from 1.0x to 10.0x, whenever held as Q/E (the user, 2026-10-24: "press O/L to increase/decrease
        // diameter"; "Whenever held, as Q/E").
        internal const KeyCode WiderKey = KeyCode.O, NarrowerKey = KeyCode.L;
        internal const string SizeFixed = "Diameter fixed: things on the pole";
        private static KeyCode widthRepeating; private static float widthRepeatAt;
        internal static Func<KeyCode, bool> TestWidthKey;   // tests press O or L with this, once each call that returns true
        private static void WidthKeys(ShipItem rod)
        {
            if (IsPole(rod)) { DiameterKeys(rod); return; }
            if (WoodPlanks.IsPlank(rod)) PlankWidthKeys(rod);
        }
        private static void DiameterKeys(ShipItem pole)
        {
            int step = WidthStep(); if (step == 0) return;
            if (PoleCarry.Carries(pole)) { Loans.Notify(SizeFixed); return; }
            int was = DiameterSteps(pole), to = Mathf.Clamp(was + step, 0, Rules.PoleMaxDiameterSteps);
            if (to != was) SetShape(pole, Length(pole), to);
            if (to >= Rules.PoleMaxDiameterSteps || to <= 0) Loans.Notify("Wood pole " + (Diameter(pole) * 100).ToString("F1", CultureInfo.InvariantCulture) + " cm across" + (to >= Rules.PoleMaxDiameterSteps ? " (thickest)" : " (thinnest)"));
        }
        // O (+1) or L (-1) this frame, held: repeating after 0.4 s.
        private static int WidthStep()
        {
            int step = 0; float now = Time.unscaledTime;
            if (TestWidthKey != null) { step = TestWidthKey(WiderKey) ? 1 : TestWidthKey(NarrowerKey) ? -1 : 0; }
            else
            {
                foreach (var (key, dir) in new[] { (WiderKey, 1), (NarrowerKey, -1) })
                {
                    if (Input.GetKeyDown(key)) { step = dir; widthRepeating = key; widthRepeatAt = now + TapSeconds; }
                    else if (widthRepeating == key && Input.GetKey(key) && now >= widthRepeatAt) { step = dir; widthRepeatAt = now + .08f; }
                    if (widthRepeating == key && !Input.GetKey(key)) widthRepeating = KeyCode.None;
                }
            }
            return step;
        }
        private static void PlankWidthKeys(ShipItem plank)
        {
            int step = WidthStep(); if (step == 0) return;
            if (PoleCarry.Carries(plank)) { Loans.Notify(PoleCarry.LengthFixed); return; }
            var s = WoodPlanks.Shape(plank); int to = Mathf.Clamp(s.width + step, Rules.PlankMinWidthSteps, Rules.PlankMaxWidthSteps);
            if (to != s.width) { s.width = to; WoodPlanks.SetShape(plank, s); }
            if (to >= Rules.PlankMaxWidthSteps || to <= Rules.PlankMinWidthSteps) Loans.Notify("Wood plank " + WoodPlanks.Width(plank).ToString("F2", CultureInfo.InvariantCulture) + " m wide" + (to >= Rules.PlankMaxWidthSteps ? " (widest)" : " (narrowest)"));
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
    // Each wood plank in the game (WoodPlanks.Live: a nailed one takes wall items as a nailed pole does); in the hand by one end it is
    // shown as the pole's staff, flat as the game holds it. Switched only when that changes.
    internal sealed class PlankMark : MonoBehaviour
    {
        private ShipItem item; private MeshRenderer root; private GameObject look; private bool shown;
        private void Awake() { item = GetComponent<ShipItem>(); root = GetComponent<MeshRenderer>(); var t = transform.Find(WoodPoles.HeldLookName); look = t ? t.gameObject : null; if (item) WoodPlanks.Live.Add(item); }
        private void OnDestroy() => WoodPlanks.Live.Remove(item);
        private void LateUpdate()
        {
            bool staff = WoodPoles.ShowsHeldLook(item);
            if (staff == shown) return;
            shown = staff; if (look) look.SetActive(staff); if (root) root.enabled = !staff;
        }
    }
    // ---- The length while a pole is held (the user, 2026-10-13) ----
    // The game's one text holder (LookUI) shows either the held item's text or a pointed thing's (a dock's "push"), one at a time, so
    // the length would vanish while pushing a dock. The mod draws its own line in the game's hint-line style (a copy of LookUI's hint
    // line, where the game shows a pole's length when one looks at it), at a fixed spot 0.75 m ahead of the eye and 0.37 m down (0.12 m below 0.25),
    // under the push text: the game puts that 0.75 m from the eye toward the push part's origin, then 0.25 m down
    // (LookUI.ShowLookText), straight ahead only when that origin lies on the view line, so the two never overlap there.
    internal static class PoleText
    {
        internal const float Ahead = .75f, Down = .25f, Below = .12f;
        internal static TextMesh Text;
        private static Transform root; private static GoPointer owner; private static bool degreeSign;
        private static readonly AccessTools.FieldRef<LookUI, TextMesh> hintText = AccessTools.FieldRefAccess<LookUI, TextMesh>("hintText");
        internal static void Update(GoPointer pointer)
        {
            var held = pointer ? pointer.GetHeldItem() as ShipItem : null; var cam = Camera.main;
            bool holds = held && WoodPoles.IsRod(held) && held.sold && cam && pointer.lookUI && GameState.playing && !GameState.inCursorMenu && !GameState.sleeping;
            if (!holds)
            {
                if (pointer == owner || !owner) { owner = null; if (root) root.gameObject.SetActive(false); }
                return;
            }
            if ((!root || !Text) && !Make(pointer.lookUI)) return;
            owner = pointer;
            var eye = cam.transform;
            root.position = eye.position + eye.forward * Ahead - Vector3.up * (Down + Below);
            root.LookAt(eye);   // as LookUI turns itself to the camera
            string line = (WoodPlanks.IsPlank(held) ? "wood plank " + WoodPlanks.SizeText(held) : "wood pole " + WoodPoles.SizeText(held)) + WoodPoles.AngleText(held, degreeSign);
            if (Text.text != line) Text.text = line;
            if (!root.gameObject.activeSelf) root.gameObject.SetActive(true);
        }
        private static bool Make(LookUI look)
        {
            var source = hintText(look); if (!source) return false;
            if (root) UnityEngine.Object.Destroy(root.gameObject);
            root = new GameObject("EconomyOverhaul pole length").transform; UnityEngine.Object.DontDestroyOnLoad(root.gameObject);
            var copy = UnityEngine.Object.Instantiate(source.gameObject, root, false);
            foreach (Transform child in copy.transform) UnityEngine.Object.Destroy(child.gameObject);
            foreach (var c in copy.GetComponents<Component>()) if (!(c is Transform || c is TextMesh || c is MeshRenderer || c is MeshFilter)) UnityEngine.Object.Destroy(c);
            // Turned and sized as the game's own line is on its holder.
            copy.transform.localPosition = Vector3.zero; copy.transform.localRotation = Quaternion.Inverse(look.transform.rotation) * source.transform.rotation;
            copy.transform.localScale = source.transform.lossyScale;
            Text = copy.GetComponent<TextMesh>();
            degreeSign = Text && Text.font && Text.font.HasCharacter('\u00B0');   // the game's font may have no degree sign
            return Text;
        }
    }
    // ---- A braced wood pole (the user, 2026-10-13; as built in CANVAS.md) ----
    // The brace key (the option "Brace key", H) braces a held pole that is bought and not nailed: from the staff in the hand it is laid
    // down over a second to the brace, held at the right side of the body pointing where one looks, tilted by the mouse wheel as the
    // game tilts any held item (at most 80 degrees), from its butt 0.4 m behind the hand (a length change moves only its far tip). The
    // key again raises it back over a second, and it is held as before. It is never solid (the user, 2026-10-13: no collision): its
    // physics copy is a trigger, as the game makes a held item's, and follows the hold exactly, kinematic, as any held item does (the
    // user: no hands' force); it knocks nothing and nothing catches it. It ignores the water's floating. It lives where items live,
    // in the world on land and on her walk copy aboard, so aboard it moves with her; a jump of the hand no hands could make (boarding
    // or leaving her while braced, a teleport) puts it at the hold at once.
    // What the pole meets in the world (a 2 cm sphere along it from the hand) its tip rests on, outlined as the game outlines a push
    // part; there, with the pick-up input held, it pushes 1.25 times as hard as the game's own push without one: a boat from land
    // (GPButtonBoatPushCol's push, with her own values), one's own boat off a dock, the ground or the sea bed from aboard
    // (DockPushCol's), and from aboard another boat, both apart. When that closes in on the hand it fends on its own: from land as it
    // pushes, from aboard the dock push 1.908 times over the closing speed (an 8 m pole stops 3.09 m/s), two boats' masses as one.
    // The pick-up input doesn't drop it; the throw key still drops or throws it, which ends the brace.
    // Deft Hands (com.keevi.defthands), when installed: its tilt of a held item has no 90-degree limit on a pole (A-B4), and the
    // braced pole is handed back to it after each push (A-B2). Its HeldItemTracker.SetCurrentItem(item, wasAttached) tells its
    // rotation, push and pull and wall roll which item is held: the game's pick-up sets it, its DropItem prefix clears it, and
    // that prefix runs on every push of a braced pole (the game's key-up drop path, this mod refusing the drop itself), so its
    // push and pull and Alt-tilt stopped on the pole until it was picked up again (the user, 2026-10-18: A-B2 (c), A-B4).
    internal static class DeftHands
    {
        internal const string Guid = "com.keevi.defthands";
        private static bool looked; private static bool installed; private static MethodInfo setCurrent;
        internal static bool Installed { get { Look(); return installed; } }
        private static void Look()
        {
            if (looked) return; looked = true;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || !info.Instance) return;
                installed = true;
                setCurrent = AccessTools.Method(info.Instance.GetType().Assembly.GetType("DeftHands.Patches.HeldItemTracker"), "SetCurrentItem", new[] { typeof(PickupableItem), typeof(bool) });
                if (setCurrent == null) Plugin.Log?.LogWarning("Deft Hands: its HeldItemTracker.SetCurrentItem is gone; the braced pole is not handed back to it after a push.");
            }
            catch (Exception e) { Plugin.Log?.LogWarning("Deft Hands could not be looked up: " + e.Message); }
        }
        // The pole handed to Deft Hands as a fresh pick-up (its HeldItemTracker.SetCurrentItem, not attached): after the game's drop
        // path cleared it from a braced pole (PoleBraceDropPatch), and at every pick-up of a pole (PoleDeftPickupPatch).
        internal static void Hold(ShipItem pole)
        {
            if (!pole || !pole.held || !Installed || setCurrent == null) return;
            try { setCurrent.Invoke(null, new object[] { pole, false }); }
            catch (Exception e) { setCurrent = null; Plugin.Log?.LogWarning("The pole could not be handed to Deft Hands: " + (e.InnerException ?? e).Message); }
        }
    }
    internal static class PoleBrace
    {
        internal const float GripRight = .3f, GripDown = .45f, GripAhead = .35f, Behind = .4f, Radius = .02f, TipGap = .01f;
        internal const float FenderSpeed = .05f, LetGoAngle = 30;
        // Round 4 (the user, 2026-10-13): laid down and raised over a second each; tilted by the wheel at most 80 degrees; put straight
        // at the hold on a jump of the hand over 2 m.
        internal const float LaySeconds = 1, MaxTilt = 80, Jump = 2;
        // The game's DockPushCol value (its own boat pushed back from where one looks): -0.55 in the game, or what a mod writes into
        // every dock push collider each physics step (the user's Hercules Throw: its "Dock push force multiplier"; the user,
        // 2026-10-13). Kept from the last active one seen (DockSeen, after the game's step and every mod's prefix: HarmonyX runs them
        // even when this mod skips the game's own push); away from every dock the last one seen, -0.55 before any.
        internal const float GameDockPush = -.55f;
        internal static float DockPush { get; private set; } = GameDockPush;
        private static readonly AccessTools.FieldRef<DockPushCol, float> dockPushMult = AccessTools.FieldRefAccess<DockPushCol, float>("pushForceMult");
        internal static void DockSeen(DockPushCol col) { if (col) DockPush = dockPushMult(col); }
        internal static ShipItem Braced;
        internal static float Lay { get; private set; }   // 0 the staff in the hand, 1 braced
        internal static bool Raising { get; private set; }
        // What its tip rests on: a collider and the point on it in its own frame (moving with her, or with a world shift).
        internal static Collider ContactCol; internal static Rigidbody ContactBoat; private static Vector3 contactLocal;
        internal static Vector3 ContactPoint => ContactCol ? ContactCol.transform.TransformPoint(contactLocal) : default;
        internal static Vector3 LastForce, LastAxis, LastButt; internal static float LastSpeed, LastClosing; internal static Rigidbody LastForceOn; internal static bool LastFender;   // for tests
        internal static bool TestPush;   // tests: as if the pick-up input were held
        internal static Func<Transform> TestEye;   // tests: the eye to hold it from
        internal static readonly AccessTools.FieldRef<ItemRigidbody, BoxCollider> boxCol = AccessTools.FieldRefAccess<ItemRigidbody, BoxCollider>("boxCol");
        internal static readonly AccessTools.FieldRef<ItemRigidbody, bool> onBoat = AccessTools.FieldRefAccess<ItemRigidbody, bool>("onBoat");
        private static readonly AccessTools.FieldRef<ItemRigidbody, SimpleFloatingObject> floater = AccessTools.FieldRefAccess<ItemRigidbody, SimpleFloatingObject>("floater");
        internal static Behaviour Floater(ItemRigidbody copy) => copy ? floater(copy) : null;   // the water's floating on an item's body (Crest's SimpleFloatingObject)
        internal static readonly AccessTools.FieldRef<GoPointerButton, bool> isClicked = AccessTools.FieldRefAccess<GoPointerButton, bool>("isClicked");
        private static readonly AccessTools.FieldRef<GoPointer, float> throwPower = AccessTools.FieldRefAccess<GoPointer, float>("currentThrowPower");

        // From GoPointer.LateUpdate (WoodPoles.Keys): the brace key on the pole in that hand (again: raised, or laid down again while
        // it rises); a pole no longer held lets go.
        internal static void Keys(GoPointer pointer, ShipItem held)
        {
            if ((object)Braced != null && (!Braced || !Braced.held)) Release();   // also one destroyed while braced
            if (!pointer || !WoodPoles.IsPole(held) || !held.sold || held.nailed || !GameState.playing || GameState.inCursorMenu) return;
            if (KeyDown()) { if (Braced == held) Raising = !Raising; else Brace(held); }
        }
        // The brace key (the option "Brace key", H) went down this frame: its key and its modifiers, as BepInEx's KeyboardShortcut.IsDown,
        // but not refused while another key is held: IsDown also fails while any key but its own is down, so the key did nothing while
        // walking, running or holding Q, E or F (the review of 2026-10-18, Claude's fix). Also a plank's mode key (WoodPlanks.ModeKey).
        internal static Func<bool> TestKey;   // tests press it with this (no key can be pressed in a test)
        internal static bool KeyDown()
        {
            if (TestKey != null) return TestKey();
            var key = Settings.BraceKey?.Value ?? new BepInEx.Configuration.KeyboardShortcut(KeyCode.H);
            return key.MainKey != KeyCode.None && Input.GetKeyDown(key.MainKey) && key.Modifiers.All(k => Input.GetKey(k));
        }
        // Braced at once, not laid down over a second: a braced pole taken out of an inventory slot (InventoryBrace).
        internal static void Laid() { if (Braced) { Lay = 1; Raising = false; } }
        internal static void Brace(ShipItem pole)
        {
            if ((object)Braced != null) Release();
            var copy = pole ? pole.itemRigidbodyC : null; var body = copy ? copy.GetBody() : null;
            if (!body || !pole.held || !pole.sold || pole.nailed) return;
            Braced = pole; pole.wallAttachment = false; hasLast = false; Lay = 0; Raising = false; HandedBack = false;   // no wall ghost, and no attaching on a drop
            // It starts where it is seen: the staff in the hand.
            worldBody = !Aboard(pole, copy); var (sp, sr) = Staff(pole); var (p, r) = ToBody(pole, copy, sp, sr);
            copy.transform.SetPositionAndRotation(p, r); body.position = p; body.rotation = r;
            PoleCarry.Hook();
        }
        // The brace key again (tests call it for the key): raised back to the staff, then held as before.
        internal static void Raise() { if (Braced) Raising = true; }
        internal static void Release()
        {
            var pole = Braced; Braced = null; ContactCol = null; ContactBoat = null; LastForce = Vector3.zero; LastForceOn = null; planted = false;
            Lay = 0; Raising = false; Unlight();
            if (!pole) return;
            ToWalkCopy(pole);
            pole.wallAttachment = true;
            // Handed back to Deft Hands during the brace (PoleBraceDropPatch), its wall roll took no item (it takes only a wall item,
            // and a braced pole is none): handed back again now that it is one, so its roll works on the pole again (the deep
            // review of 2026-10-19, Claude's fix to A-B2).
            if (HandedBack) { HandedBack = false; DeftHands.Hold(pole); }
            WoodPoles.SetLength(pole, WoodPoles.Length(pole));   // its whole box again, and the held look back in the hand
        }
        internal static bool HandedBack;   // the braced pole was handed back to Deft Hands after a push (PoleBraceDropPatch)
        // Deft Hands' tilt (its Alt and the mouse) adds to the held turn without a limit: while braced, the turn is held at the
        // brace's 80 as the wheel's is (past 180 the brace would jump from one side's 80 to the other's; the deep review of
        // 2026-10-19, Claude's fix). Its tilt reads the turn back when another writes it (its RotateQuadrant).
        internal static void HoldTilt()
        {
            var pole = Braced; if (!pole) return;
            float t = Mathf.DeltaAngle(0, pole.heldRotationOffset);
            if (Mathf.Abs(t) > MaxTilt) pole.heldRotationOffset = Mathf.Clamp(t, -MaxTilt, MaxTilt);
        }
        // A pole the game had boarded on a ship the player is not on was held in the world (ToBody: Aboard false), but the game's
        // own step reads such a body in her walk copy's frame (ItemRigidbody.FixedUpdate: MoveItemToWalkColRigidbody), which put a
        // pole let go there far off (lost, deleted 600 m away). Its body goes onto her walk copy where it is (the review of
        // 2026-10-18, Claude's fix).
        private static bool worldBody;   // the last step put its body in the world (not by the game's own step, not on her walk copy)
        private static void ToWalkCopy(ShipItem pole)
        {
            var copy = pole.itemRigidbodyC;
            if (!worldBody || !copy || !onBoat(copy) || !pole.currentWalkCol || !pole.currentActualBoat) return;
            Transform ship = pole.currentActualBoat, walk = pole.currentWalkCol, t = copy.transform;
            var p = walk.TransformPoint(ship.InverseTransformPoint(t.position)); var r = walk.rotation * (Quaternion.Inverse(ship.rotation) * t.rotation);
            t.SetPositionAndRotation(p, r);
            var body = copy.GetBody(); if (body) { body.position = p; body.rotation = r; }
        }
        // The wheel's tilt (the game's heldRotationOffset, which it turns a held item by about the view's side-to-side axis), at most
        // 80 degrees either way so the pole never points back through the player.
        internal static float Tilt(ShipItem pole) => Mathf.Clamp(Mathf.DeltaAngle(0, pole.heldRotationOffset), -MaxTilt, MaxTilt);
        // The staff in the hand (WoodPoles.HeldPose on the item the game holds ahead of the eye): where the pole is seen before it is
        // braced, its butt a third of it below the hand.
        internal static (Vector3, Quaternion) Staff(ShipItem pole)
        {
            WoodPoles.HeldLocal(WoodPoles.Length(pole), WoodPoles.Lean(pole), out var at, out var turn);
            return (pole.transform.TransformPoint(at), pole.transform.rotation * turn);
        }

        // Each physics step in place of the game's own (ItemRigidbody.FixedUpdate) for the braced pole's copy: laid down or raised,
        // driven to the hold, then its push. False: not braced any more, the game's own step runs.
        internal static bool Step(ItemRigidbody copy)
        {
            var pole = Braced; var body = copy ? copy.GetBody() : null;
            if (!pole || !pole.held || pole.nailed || !body) { Release(); return false; }
            if (!GameState.playing || GameState.sleeping || GameState.recovering) { worldBody = false; return false; }   // paused: held as any item (the game's step places its body)
            var drift = floater(copy); if (drift && drift.enabled) drift.enabled = false;   // not floated by the water (a mod may switch it on: RadFixes)
            Lay = Mathf.Clamp01(Lay + (Raising ? -1 : 1) * Time.fixedDeltaTime / LaySeconds);
            if (Raising && Lay <= 0) { Release(); return false; }
            var eye = TestEye?.Invoke() ?? pole.held.transform;
            var box = boxCol(copy);
            // With poles fixed straight on its far end, one longer pole: its reach, its body's length and its weight (PoleCarry).
            float length = PoleCarry.Reach(pole); body.mass = PoleCarry.Mass(pole);
            bool braced = Lay >= 1 && !Raising;
            if (braced) { Aim(pole, eye, out var g, out var a, out _); Touch(g, a, length - Behind); } else { ContactCol = null; ContactBoat = null; planted = false; }
            var (butt, turn, grip, axis) = Pose(pole, eye, Lay, braced);
            LastButt = butt; worldBody = !Aboard(pole, copy);
            // Never solid (the user, 2026-10-13: no collision): a trigger along its whole reach, as the game makes a held item's box.
            if (box)
            {
                box.enabled = true; box.isTrigger = true;
                float across = WoodPoles.Diameter(pole); box.size = new Vector3(across, across, Mathf.Max(.05f, length)); box.center = new Vector3(0, 0, -length / 2);
            }
            var (p, r) = ToBody(pole, copy, butt, turn);
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative; body.isKinematic = true;
            Move(body, p, r, ToBody(pole, copy, grip, turn).Item1);
            if (braced) Push(eye, grip, axis); else { LastForce = Vector3.zero; LastForceOn = null; LastFender = false; }
            return true;
        }
        // The hold from the hand as it is now: the grip at the right side, the line along the view tilted by the wheel.
        private static void Aim(ShipItem pole, Transform eye, out Vector3 grip, out Vector3 axis, out Vector3 up)
        {
            var aim = eye.rotation * Quaternion.Euler(Tilt(pole), 0, 0); axis = aim * Vector3.forward; up = aim * Vector3.up;
            grip = eye.position + eye.right * GripRight - eye.up * GripDown + eye.forward * GripAhead;
        }
        // Where the pole lies from the hand as it is now (each physics step for its body, each frame for its look): laid down by lay
        // (0 the staff in the hand, 1 braced; between them eased at both ends), braced its butt 0.4 m behind the hand, or its tip
        // resting on the point it meets (the line from the hand to it).
        private static (Vector3 butt, Quaternion turn, Vector3 grip, Vector3 axis) Pose(ShipItem pole, Transform eye, float lay, bool braced)
        {
            Aim(pole, eye, out var grip, out var axis, out var up);
            var butt = grip - axis * Behind;
            if (braced && ContactCol) { var at = ContactPoint; axis = (at - grip).normalized; butt = at - axis * (PoleCarry.Reach(pole) + TipGap); }
            var turn = Quaternion.LookRotation(-axis, up);
            if (!braced)
            {
                // From the staff: its line swung to the hold's in the plane the two share, and its turn about itself blended
                // alongside. A slerp of the two turns whole swung the tip 1.2 m out to the side mid-way, back at the end (the user's
                // flick, 2026-10-17; measured by -PoleLean), as the two also differ by a twist about the pole.
                var (sp, sr) = Staff(pole); float s = (float)Rules.Eased(lay);
                Vector3 from = sr * Vector3.back, to = turn * Vector3.back, line = Vector3.Slerp(from, to, s);
                var swung = Quaternion.FromToRotation(from, line) * sr;
                butt = Vector3.Lerp(sp, butt, s); turn = Quaternion.Slerp(swung, Quaternion.LookRotation(-line, turn * Vector3.up), s);
            }
            return (butt, turn, grip, axis);
        }
        // From where it lies to its body: with the player aboard her, from her frame to her walk copy (as the game's
        // MoveRigidbodyToWalkCol); else the world, also when the held pole has boarded a ship the player stands beside (the game
        // boards a held item near her: its body on her walk copy would hit her copy's hull, measured stuck 6 m off).
        private static bool Aboard(ShipItem pole, ItemRigidbody copy) => pole.currentWalkCol && pole.currentActualBoat && onBoat(copy) && GameState.currentBoat == pole.currentActualBoat;
        private static (Vector3, Quaternion) ToBody(ShipItem pole, ItemRigidbody copy, Vector3 p, Quaternion r) => !Aboard(pole, copy) ? (p, r) :
            (pole.currentWalkCol.TransformPoint(pole.currentActualBoat.InverseTransformPoint(p)), pole.currentWalkCol.rotation * (Quaternion.Inverse(pole.currentActualBoat.rotation) * r));
        // To the hold each physics step, exactly, as the game holds any item (kinematic; the user, 2026-10-13: no hands' force). A
        // jump of the hand no hands could make (boarding or leaving her while braced: the hand moves between the world and her walk
        // copy; a teleport) puts it there at once instead of sweeping it through the frames between; a world shift moves the body with
        // the hand. The hand, not the hold: the hold slides back along the pole by up to its length when its tip meets something
        // (pole-brace-45).
        private static Vector3 lastBody, lastHand; private static bool hasLast;
        internal static int Placed;   // times put at the hold after a jump (for tests)
        internal static Vector3 DrawnAt; internal static bool DrawnPlanted;   // how the pole was last drawn: planted, resting there (for tests)
        private static void Move(Rigidbody body, Vector3 at, Quaternion turn, Vector3 hand)
        {
            if (hasLast && (hand - lastHand).magnitude > Jump && (body.position - lastBody - (hand - lastHand)).magnitude > Jump)
            { body.position = at; body.rotation = turn; Placed++; }
            else { body.MovePosition(at); body.MoveRotation(turn); }
            lastBody = at; lastHand = hand; hasLast = true;
        }
        // What the pole meets in the world, along it from the hand: the nearest, found afresh each step; while it pushes or fends
        // (planted), the same point kept as long as it stays within its reach and 30 degrees of the aim line (the view turned by the
        // wheel's tilt), moving with what it rests
        // on (a ship pushed off, the sea bed punted from). Items, the player, one's own boat, triggers and the cities' invisible
        // walls (layer 12) are not met.
        private static bool planted;
        private static void Touch(Vector3 grip, Vector3 axis, float reach)
        {
            if (ContactCol && !planted) { ContactCol = null; ContactBoat = null; }
            if (ContactCol)
            {
                var to = ContactPoint - grip;
                if (ContactCol.enabled && ContactCol.gameObject.activeInHierarchy && to.magnitude <= reach + .05f && Vector3.Angle(to, axis) <= LetGoAngle) return;
                ContactCol = null; ContactBoat = null;
            }
            if (reach <= 0) return;
            RaycastHit best = default; float near = float.MaxValue;
            foreach (var h in Physics.SphereCastAll(grip, Radius, axis, reach, ~0, QueryTriggerInteraction.Ignore))
                if (h.distance > 0 && h.distance < near && !Skip(h.collider)) { near = h.distance; best = h; }
            if (!best.collider) return;
            ContactCol = best.collider; contactLocal = best.collider.transform.InverseTransformPoint(best.point); ContactBoat = Boat(best.collider);
        }
        private static bool Skip(Collider c)
        {
            if (c.gameObject.layer == 12 || c.GetComponentInParent<ShipItem>() || c.GetComponentInParent<ItemRigidbody>()) return true;
            if (Refs.charController && c.transform.IsChildOf(Refs.charController.transform)) return true;
            var own = GameState.currentBoat ? GameState.currentBoat.parent : null;
            return own && c.transform.IsChildOf(own);
        }
        internal static Rigidbody Boat(Collider c)
        {
            var rb = c ? c.attachedRigidbody : null;
            return rb && (rb.GetComponent<PurchasableBoat>() || BoatPart(rb)) ? rb : null;
        }
        // Each boat's own push part, looked up once; a body with none (a visible trader's hull, a rock's body) is remembered as
        // none too (it was looked up again each physics step while the tip rested on it); one destroyed is looked up again.
        private static readonly Dictionary<Rigidbody, GPButtonBoatPushCol> parts = new Dictionary<Rigidbody, GPButtonBoatPushCol>();
        internal static GPButtonBoatPushCol BoatPart(Rigidbody boat)
        {
            if (!boat) return null;
            if (!parts.TryGetValue(boat, out var part) || (object)part != null && !part) parts[boat] = part = boat.GetComponentInChildren<GPButtonBoatPushCol>(true);
            return part;
        }
        // The push, with the pick-up input held: from land on the boat met, the game's GPButtonBoatPushCol push with her own values;
        // from aboard on one's own boat, DockPushCol's, and the boat met (if any) the equal opposite push; each 1.25 times as hard,
        // and over the pushed boat's speed (at least 1) as the game's. As a fender (the contact closing in on the hand faster than
        // 5 cm/s, nothing held): from land the same push; from aboard (the user, 2026-10-13) the dock push 1.908 times (Rules.PoleFender:
        // an 8 m pole stops 3.09 m/s, 6 knots) over the closing speed (at least 1), her mass off a dock or the sea bed, two boats' masses as one
        // (the same stop for every ship, from either deck).
        private static void Push(Transform eye, Vector3 grip, Vector3 axis)
        {
            LastForce = Vector3.zero; LastForceOn = null; LastFender = false; planted = false;
            if (!ContactCol) return;
            var at = ContactPoint;
            var own = GameState.currentBoat ? GameState.currentBoat.parent.GetComponent<Rigidbody>() : null;
            var closing = Vector3.Dot((own ? own.GetPointVelocity(grip) : Vector3.zero) - (ContactBoat ? ContactBoat.GetPointVelocity(at) : Vector3.zero), axis);
            LastClosing = closing;
            bool pushing = TestPush || !GameState.inCursorMenu && GameInput.GetKey(InputName.PickUp), fending = closing > FenderSpeed;
            if (!pushing && !fending) return;
            LastFender = fending && !pushing; planted = true;
            if (own)
            {
                if (ContactBoat == own) return;
                LastAxis = axis; LastSpeed = own.velocity.magnitude;
                Vector3 f;
                if (pushing) f = Rules.PolePush * DockPush * own.mass * axis / Mathf.Max(1, own.velocity.magnitude);
                else
                {
                    float mass = ContactBoat ? own.mass * ContactBoat.mass / (own.mass + ContactBoat.mass) : own.mass;
                    f = Rules.PoleFender * Rules.PolePush * DockPush * mass * axis / Mathf.Max(1, closing);
                }
                own.AddForceAtPosition(f, eye.position); LastForce = f; LastForceOn = own;
                if (ContactBoat) ContactBoat.AddForceAtPosition(-f, at);
            }
            else if (ContactBoat)
            {
                if (PlayerSwimming.swimming || Refs.charController && !Refs.charController.isGrounded) return;
                var col = BoatPart(ContactBoat);
                float mult = col ? Fields.Get<float>(col, "pushForceMult") : 3, up = col ? Fields.Get<float>(col, "upForceMult") : 1;
                float below = col ? Fields.Get<float>(col, "verticalOffset") : -2, mass = col ? Fields.Get<float>(col, "baseMass") : ContactBoat.mass;
                LastAxis = axis; LastSpeed = ContactBoat.velocity.magnitude;
                var f = Rules.PolePush * (mult * mass * axis + up * mass * Vector3.up) / Mathf.Max(1, ContactBoat.velocity.magnitude);
                ContactBoat.AddForceAtPosition(f, new Vector3(at.x, eye.position.y + below, at.z)); LastForce = f; LastForceOn = ContactBoat;
            }
        }
        // What its tip rests on is outlined as the game outlines a push part (GoPointerButton.UpdateColor; the user, 2026-10-13): a
        // boat's push part (GPButtonBoatPushCol) or a dock's (DockPushCol), as looked at while the tip rests there and as pushed (the
        // game's FlashOutline) while it pushes or fends. The sea bed and other ground: none.
        private static readonly AccessTools.FieldRef<GoPointerButton, bool> outlined = AccessTools.FieldRefAccess<GoPointerButton, bool>("overrideEnableOutline");
        internal static GoPointerButton Lit { get; private set; }
        internal static void Highlight()
        {
            GoPointerButton part = null;
            if (Braced && ContactCol) part = ContactBoat ? (GoPointerButton)BoatPart(ContactBoat) : ContactCol.GetComponentInParent<DockPushCol>();
            if (Lit && Lit != part) outlined(Lit) = false;
            Lit = part; if (!part) return;
            if (planted) { outlined(part) = false; part.FlashOutline(.1f); } else outlined(part) = true;
        }
        private static void Unlight() { if (Lit) outlined(Lit) = false; Lit = null; }
        // Drawn each frame, after everything else has moved, from the hand as it is then, as the game draws any held item (its
        // GoPointer.LateUpdate sets a held item's transform every frame); not from its body, which moves only on physics steps and so
        // was drawn a step behind the view, juddering as it turned (the user, 2026-10-13: glitchy, unlike a held item). Resting on,
        // pushing or fending off a point, from the hands straight to it, whatever it is (a boat, a dock, the ground, the sea bed: from
        // aboard it touches what it pushes). Laying down and raising run on smoothly between physics steps.
        internal static void Draw()
        {
            var pole = Braced; if (!pole || !pole.held) return;
            var look = pole.transform.Find(WoodPoles.HeldLookName); if (!look) return;
            var eye = TestEye?.Invoke() ?? pole.held.transform;
            bool braced = Lay >= 1 && !Raising;
            float since = GameState.playing ? Mathf.Clamp(Time.time - Time.fixedTime, 0, Time.fixedDeltaTime) : 0;   // since the last physics step
            float lay = braced ? 1 : Mathf.Clamp01(Lay + (Raising ? -1 : 1) * since / LaySeconds);
            var (butt, turn, _, _) = Pose(pole, eye, lay, braced);
            look.SetPositionAndRotation(butt, turn);
            DrawnPlanted = planted && ContactCol; if (DrawnPlanted) DrawnAt = ContactPoint;
        }
        // GoPointer.LateUpdate: no throw charged by the left mouse (the game's auto-throw), and no drop by it (DropItem refused
        // while the flag is up); the throw key still charges, drops and throws.
        internal static bool InPointer;
        internal static void BeforePointer(GoPointer pointer)
        {
            InPointer = true;
            if (Braced && Braced.held == pointer && !GameInput.GetKey(InputName.Throw)) throwPower(pointer) = 0;
        }
        internal static bool AllowsDrop(GoPointer pointer) => !(InPointer && Braced && Braced.held == pointer && !GameInput.GetKeyUp(InputName.Throw));
    }
    [HarmonyPatch(typeof(ItemRigidbody), "FixedUpdate")] internal static class PoleBraceStepPatch
    { static bool Prefix(ItemRigidbody __instance) => !(PoleBrace.Braced && PoleBrace.Braced.itemRigidbodyC == __instance && PoleBrace.Step(__instance)); }
    [HarmonyPatch(typeof(GoPointer), "LateUpdate")] internal static class PoleBracePointerPatch
    {
        static void Prefix(GoPointer __instance) => PoleBrace.BeforePointer(__instance);
        static void Postfix() => PoleBrace.InPointer = false;
        static Exception Finalizer(Exception __exception) { PoleBrace.InPointer = false; return __exception; }
    }
    // A push of a braced pole: the game's drop refused; Deft Hands' own DropItem prefix still let go of the pole, so it is handed
    // back to it (DeftHands.Hold; HarmonyX runs every prefix and the postfixes whatever a prefix returns).
    [HarmonyPatch(typeof(GoPointer), "DropItem")] internal static class PoleBraceDropPatch
    {
        static bool Prefix(GoPointer __instance, out bool __state) { __state = !PoleBrace.AllowsDrop(__instance); return !__state; }
        static void Postfix(bool __state) { if (__state && PoleBrace.Braced) { DeftHands.Hold(PoleBrace.Braced); PoleBrace.HandedBack = true; } }
    }
    // The game's own pushes add nothing while a pole is braced (the pole pushes instead).
    [HarmonyPatch(typeof(GPButtonBoatPushCol), "ExtraFixedUpdate")] internal static class PoleBraceBoatPushPatch
    { static bool Prefix(GPButtonBoatPushCol __instance) => !(PoleBrace.Braced && PoleBrace.isClicked(__instance)); }
    [HarmonyPatch(typeof(DockPushCol), "ExtraFixedUpdate")] internal static class PoleBraceDockPushPatch
    {
        static bool Prefix(DockPushCol __instance) => !(PoleBrace.Braced && PoleBrace.isClicked(__instance));
        static void Postfix(DockPushCol __instance) => PoleBrace.DockSeen(__instance);   // the value pushes from aboard follow
    }
    // After the game's pointer and its text holder: the length keys, the brace key, the outline of what a braced pole rests on, the
    // length line.
    // After Deft Hands' roll of the held item (its HeldItemRollPatch): the pole's turn in the hand as drawn (WoodPoles.SeeHeld).
    [HarmonyPatch(typeof(GoPointer), "LateUpdate"), HarmonyAfter("com.keevi.defthands")] internal static class PoleKeysPatch
    { static void Postfix(GoPointer __instance) { if (!WoodPoles.Registered) return; WoodPoles.Keys(__instance); WoodPoles.SeeHeld(__instance); PoleBrace.HoldTilt(); PoleBrace.Highlight(); PoleText.Update(__instance); } }
    // Dropped onto a pole aboard a ship: put on her where it was aimed (WoodPoles.OnPoleDrop), after the game's own drop.
    [HarmonyPatch(typeof(ShipItem), "OnDrop")] internal static class PoleDropPatch
    { static void Postfix(ShipItem __instance) { if (WoodPoles.Registered && __instance.wallAttachment) WoodPoles.OnPoleDrop(__instance); } }
    // Any held wall item (a pole, a lantern hook, a hangable...; the user, 2026-10-13: not only poles) aimed at a nailed pole
    // attaches to it (WoodPoles.AimAtPole), after the game's own wall test of the frame. A braced pole is no wall item.
    // After Deft Hands' own turn of the spot (its WallAttachmentRollPatch): the pole's tilt and lean are turned last.
    [HarmonyPatch(typeof(ShipItem), "Update"), HarmonyAfter("com.keevi.defthands")] internal static class PoleOnPolePatch
    { static void Postfix(ShipItem __instance) { if (__instance.held && __instance.wallAttachment && WoodPoles.Registered && __instance.sold) { WoodPoles.AimAtPole(__instance, new Ray(__instance.held.transform.position, __instance.held.transform.forward)); WoodPoles.LeanOnWall(__instance); } } }
    // The mouse wheel on a held pole tilts it toward or away, 3 degrees a notch (WoodPoles.Scroll), in place of the game's turn; not
    // when another mod took the wheel first (Deft Hands' push and pull: its prefix, run before this one, skips the game's turn).
    [HarmonyPatch(typeof(PickupableItem), "OnScroll"), HarmonyAfter("com.keevi.defthands")] internal static class PoleLeanScrollPatch
    { static bool Prefix(PickupableItem __instance, float input, bool __runOriginal) => __runOriginal && WoodPoles.Scroll(__instance, input); }
    // With the game's rotate key held, the wheel leans a held pole left or right (WoodPoles.LeanScroll), the game's own use of it
    // that frame kept from it (no wheel until its LateUpdate is done).
    [HarmonyPatch(typeof(GoPointer), "LateUpdate")] internal static class PoleLeanPointerPatch
    {
        static void Prefix(GoPointer __instance) { if (WoodPoles.Registered) WoodPoles.LeanScroll(__instance); }
        static void Postfix() => WoodPoles.KeepScroll = false;
    }
    [HarmonyPatch(typeof(GameInput), "GetScrollAxis")] internal static class PoleLeanWheelPatch
    { static bool Prefix(ref float __result) { if (!WoodPoles.KeepScroll) return true; __result = 0; return false; } }
    // Looked at: "wood pole" and its length under it.
    [HarmonyPatch(typeof(ShipItem), "UpdateLookText")] internal static class PoleLookTextPatch
    { static void Postfix(ShipItem __instance) { if (WoodPoles.IsPole(__instance)) __instance.description = WoodPoles.SizeText(__instance); else if (WoodPlanks.IsPlank(__instance) && __instance.lookText == "" && !Rules.PlankPlain(WoodPlanks.Shape(__instance))) __instance.description = WoodPlanks.SizeText(__instance); } }
    // Picked up: its length and the keys (or that things on it fix its length).
    [HarmonyPatch(typeof(ShipItem), "OnPickup")] internal static class PolePickupPatch
    {
        static void Prefix(ShipItem __instance) { if (WoodPoles.Registered) PoleCarry.PickedUp(__instance); }   // before the game frees it
        static void Postfix(ShipItem __instance)
        {
            if (WoodPlanks.IsPlank(__instance)) { if (WoodPlanks.EndMode(__instance)) WoodPoles.ResetLean(__instance); return; }   // flat: the game's own turn kept
            if (!WoodPoles.IsPole(__instance)) return;
            WoodPoles.ResetLean(__instance);   // upright at each pick-up
            if (__instance.sold) InventoryBrace.TakenOut(__instance);
            if (__instance.sold) Loans.Notify(WoodPoles.PickupNotice(__instance));
        } }
    // Upright at each pick-up with Deft Hands too (the user, 2026-10-20; POLE_ROUND5_DRAFT.md): Deft Hands keeps an attached item's
    // roll as it stood when it is picked up (its PickUpItemPatch, WallAttachmentRoll.ReadAttachedRoll), and the pole counts Deft
    // Hands' roll in its attach (SeenLean), so a pole attached leaning came back leaning and the angle grew with each attach and
    // pick-up. After Deft Hands' own pick-up, the pole is handed to it again as not attached: its kept roll back to 0, the pole not
    // turned (its RollHeldItem sees no change).
    [HarmonyPatch(typeof(GoPointer), "PickUpItem")] internal static class PoleDeftPickupPatch
    {
        [HarmonyAfter(DeftHands.Guid)] static void Postfix(PickupableItem item) { if (item is ShipItem pole && WoodPoles.IsRod(pole) && pole.sold) DeftHands.Hold(pole); }
    }
    // ---- A braced pole kept braced through the inventory (the user, 2026-10-24: "i want to implement brace preserve after taken out
    // from inventory also"; over saves: "if it's not affect performance, let it save across the saves") ----
    // Put in an inventory slot braced (GPButtonInventorySlot.InsertItem), the pole is marked in its saved amount (unused on a pole: 1
    // braced), which the game saves with the item; taken out (its pick-up from the slot), it is braced again at once. A plank keeps its
    // attach mode anyway: it is in its saved health.
    internal static class InventoryBrace
    {
        internal const float Mark = 1;
        internal static void PutAway(ShipItem item) { if (item && PoleBrace.Braced == item) item.amount = Mark; }
        internal static void TakenOut(ShipItem pole)
        {
            if (!WoodPoles.IsPole(pole) || pole.amount < Mark) return;
            pole.amount = 0;
            PoleBrace.Brace(pole); PoleBrace.Laid();
            if (PoleBrace.Braced == pole) Plugin.Log?.LogInfo("Wood pole taken out of the inventory braced.");
        }
    }
    [HarmonyPatch(typeof(GPButtonInventorySlot), "InsertItem")] internal static class PoleInventoryBracePatch
    { static void Prefix(ShipItem item) { if (WoodPoles.Registered) InventoryBrace.PutAway(item); } }
    // Taken out of an open crate (the game's crate screen withdraws it, then hands it over in the same call): marked, so its pick-up
    // takes nothing (PoleCarry.Stored).
    [HarmonyPatch(typeof(CrateInventory), "WithdrawItem")] internal static class PoleCrateWithdrawPatch
    { static void Prefix(ShipItem item) { if (WoodPoles.IsPole(item)) PoleCarry.WithdrawnFromCrate(item); } }
    // Removed (the game's clean-up of items left off a ship 600 m away, or any other way): a tarp tied to it is untied, and one it
    // held up drops back to its shape without it.
    [HarmonyPatch(typeof(ShipItem), "DestroyItem")] internal static class PoleRemovePatch
    {
        static void Prefix(ShipItem __instance)
        {
            if (PoleCarry.Active) PoleCarry.Removed(__instance);
            if (WoodPoles.IsPole(__instance) && Tarps.Registered && __instance.nailed) { Tarps.UntieFrom(__instance); Tarps.PoleMoved(__instance, true); }
        }
    }
    // Nailed with the hammer (the game's ShipItemHammer.NailItem, which may refuse): a wood pole under a tarp holds it up.
    [HarmonyPatch(typeof(ShipItemHammer), "NailItem")] internal static class PoleNailPatch
    { static void Postfix(ShipItem item) { if (item && item.nailed && WoodPoles.IsPole(item)) Tarps.PoleMoved(item, false); } }

    // ---- Things on a pole go with it (the user, 2026-10-13; as built in CANVAS.md) ----
    // Picked up, a pole takes with it what is locked (nailed) on it: the wall items whose attachment point is on it (within 2 cm,
    // facing into it) and the poles nailed on it with what is nailed on those, found by geometry at that moment (no save data); a
    // lantern hung on a carried hook goes with its hook (the game hangs it on the hook's body by a joint and draws it under the hook).
    // What is on it but not locked is let go and falls (the user, round 4). They keep their place on it as it is drawn (in the hand
    // on its held look) while it is held, braced, thrown or lying loose, aboard on the pole's ship: their physics copies frozen (as any
    // fixed wall item) and triggers, so they add no weight and knock nothing. Poles nailed straight on its far end make one longer pole
    // with it (the user: its reach, its body along the whole length (solid only while loose) and 2 kg a metre, no limit). Attached to a wall again, they stay on it
    // as plain wall items (found again at its next pick-up) and the nailed poles among them hold up a tarp they stand under (the user:
    // the reverse case). One picked up leaves it; the pole removed or put away lets them go and they fall. At pick-up a tarp tied to
    // one of them is untied (its pack dropped, as un-nailing gives it; the user: untie automatically) and one a carried pole held up
    // drops back to its shape. Its length is fixed while things are on it.
    internal static class PoleCarry
    {
        internal const float Near = .02f, Straight = .996f;   // on its surface within 2 cm; straight on within 5 degrees
        internal const string LengthFixed = "Length fixed: things on the pole";
        internal sealed class Thing { internal ShipItem item, hook; internal Vector3 at; internal Quaternion turn; internal bool hung; }   // at, turn: in the pole's frame; hook: the one it hangs on
        internal sealed class Load
        {
            internal ShipItem pole; internal float reach, mass;
            internal readonly List<Thing> things = new List<Thing>();
        }
        private static readonly Dictionary<ItemRigidbody, Load> loads = new Dictionary<ItemRigidbody, Load>();   // by the pole's physics copy
        private static readonly Dictionary<ShipItem, Load> riding = new Dictionary<ShipItem, Load>();
        private static readonly HashSet<ItemRigidbody> ridingCopies = new HashSet<ItemRigidbody>();
        private static readonly MethodInfo exitBoat = AccessTools.Method(typeof(ShipItem), "ExitBoat"), enterBoat = AccessTools.Method(typeof(ShipItem), "EnterBoat");
        private static readonly MethodInfo hangLate = AccessTools.Method(typeof(HangableItem), "LateUpdate");
        private static readonly AccessTools.FieldRef<ShipItem, Collider> boatCollider = AccessTools.FieldRefAccess<ShipItem, Collider>("currentBoatCollider");
        internal static bool Active => loads.Count > 0;
        internal static bool Riding(ShipItem item) => item && riding.ContainsKey(item);
        internal static Load Of(ShipItem pole) => pole && pole.itemRigidbodyC && loads.TryGetValue(pole.itemRigidbodyC, out var load) ? load : null;
        internal static bool Carries(ShipItem pole) => (Of(pole)?.things.Count ?? 0) > 0;
        internal static float Reach(ShipItem pole) => Of(pole)?.reach ?? WoodPoles.Length(pole);
        internal static float Mass(ShipItem pole) => Of(pole)?.mass ?? pole.mass;
        // Where the pole is drawn: in the hand its held look (braced, posed from its body), else itself.
        private static Transform Frame(ShipItem pole)
        {
            var look = WoodPoles.ShowsHeldLook(pole) ? pole.transform.Find(WoodPoles.HeldLookName) : null;   // a plank held flat: itself
            return look ? look : pole.transform;
        }
        // Fixed where it is: nailed, or a wall item attached (as the game holds one on a wall, or any wall item after a load). Not
        // stored, hung or cooking: in a slot or a box, or the game's inStove (in a crate or waiting for its crate after a load, a
        // lantern on a hook, food on a stove), which also sets attached. A lantern on a carried hook comes along by its hook (HungOn).
        private static bool Fixed(ShipItem item) => item && item.sold && !item.held && item.itemRigidbodyC && (item.nailed || item.wallAttachment && item.itemRigidbodyC.attached)
            && !item.itemRigidbodyC.GetCurrentInventorySlot() && !item.itemRigidbodyC.GetCurrentBox() && !item.itemRigidbodyC.inStove;
        // Taken out of storage, a pole takes nothing: put away, its things were let go (Follow), and its frame is not where it is. The
        // game calls OnPickup before it frees an item from an inventory slot (or the port's cart), so the pole is still parked and
        // shrunk for the UI here (0.1 with the panel open, toward 0 with it closed, when every point measured from it comes out at
        // its base: every thing nailed anywhere facing one way was taken, 2026-10-17's report). Outside storage the game holds every
        // item at scale 1 each frame (ItemRigidbody.LateUpdate; localScale, so a scaled ship doesn't count); storage shrinks it. A
        // crate's withdrawal resets all that but leaves it at the crate screen's button: marked (PoleCrateWithdrawPatch).
        private static ShipItem crateWithdrawn; private static int crateWithdrawnFrame = -1;
        internal static void WithdrawnFromCrate(ShipItem pole) { crateWithdrawn = pole; crateWithdrawnFrame = Time.frameCount; }
        internal static bool Stored(ShipItem pole) => pole.itemRigidbodyC.GetCurrentInventorySlot() || pole.transform.localScale != Vector3.one
            || pole == crateWithdrawn && Time.frameCount == crateWithdrawnFrame;
        // On the pole: its attachment point (its root, where the game puts it) within 2 cm of the pole's box, facing into it: on an end
        // along it, on a side toward its line.
        internal static bool On(ShipItem pole, ShipItem item)
        {
            if (WoodPlanks.IsPlank(pole)) return OnBox(pole, item);
            var t = pole.transform; float length = WoodPoles.Length(pole), half = WoodPoles.Diameter(pole) / 2 + Near;
            var q = t.InverseTransformPoint(item.transform.position); var into = t.InverseTransformDirection(item.transform.forward);
            if (Mathf.Abs(q.x) > half || Mathf.Abs(q.y) > half || q.z > Near || q.z < -length - Near) return false;
            if (q.z <= -length + .01f && into.z > .9f || q.z >= -.01f && into.z < -.9f) return true;
            var toLine = new Vector3(-q.x, -q.y, 0);
            return toLine.magnitude > .01f && Vector3.Dot(into, toLine.normalized) > .7f;
        }
        // On a plank: its attachment point within 2 cm of the plank's box, facing into the face it is nearest (K7).
        internal static bool OnBox(ShipItem plank, ShipItem item)
        {
            var box = plank.GetComponent<BoxCollider>(); if (!box) return false;
            var t = plank.transform; var q = t.InverseTransformPoint(item.transform.position) - box.center; var half = box.size / 2;
            var into = t.InverseTransformDirection(item.transform.forward);
            int face = -1; float gap = float.MaxValue;
            for (int k = 0; k < 3; k++)
            {
                if (Mathf.Abs(q[k]) > half[k] + Near) return false;
                float g = Mathf.Abs(half[k] - Mathf.Abs(q[k])); if (g < gap) { gap = g; face = k; }
            }
            if (gap > Near) return false;
            var outward = Vector3.zero; outward[face] = q[face] >= 0 ? 1 : -1;
            return Vector3.Dot(into, -outward) > .9f;
        }
        // A lantern (any hangable) the game hangs on this hook: by a joint on the hook's body (ShipItemLampHook.CreateJoint).
        private static ShipItem HungOn(ShipItem hook)
        {
            if (!hook || !hook.GetComponent<ShipItemLampHook>() || !hook.itemRigidbodyC) return null;
            var joint = hook.itemRigidbodyC.GetComponent<ConfigurableJoint>(); var body = joint ? joint.connectedBody : null;
            var copy = body ? body.GetComponent<ItemRigidbody>() : null; var item = copy ? copy.GetShipItem() : null;
            var hangs = item ? item.GetComponent<HangableItem>() : null;
            return hangs && hangs.IsHanging() ? item : null;
        }
        private static bool StillHung(Thing t)
        {
            var hangs = t.item ? t.item.GetComponent<HangableItem>() : null;
            return hangs && hangs.IsHanging() && t.hook && !t.hook.held && HungOn(t.hook) == t.item;
        }

        // Picked up (ShipItem.OnPickup, before the game frees it): a thing riding a pole leaves it; a pole takes what is locked on it
        // and lets go of what is not.
        internal static void PickedUp(ShipItem item)
        {
            Leave(item);
            if (!(WoodPoles.IsPole(item) || WoodPlanks.IsPlank(item)) || !item.sold || !item.itemRigidbodyC || Stored(item)) return;   // a plank too (K7)
            var pole = item; Prune(); Forget(Of(pole));   // found afresh
            var all = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentPrefabs() : null; if (all == null) return;
            var candidates = new List<ShipItem>();
            foreach (var p in all) { var c = p ? p.GetComponent<ShipItem>() : null; if (c != pole && Fixed(c)) candidates.Add(c); }
            var found = new List<ShipItem>(); var loose = new List<ShipItem>(); var open = new Queue<ShipItem>(); open.Enqueue(pole);
            while (open.Count > 0)
            {
                var on = open.Dequeue();
                foreach (var c in candidates)
                {
                    if (found.Contains(c) || loose.Contains(c) || !On(on, c)) continue;
                    // on through planks too (the user, 2026-10-24: "let chain goes on plank"; before, what was nailed on a carried
                    // plank stayed nailed in the air)
                    if (c.nailed) { found.Add(c); if (WoodPoles.IsPole(c) || WoodPlanks.IsPlank(c)) open.Enqueue(c); }
                    else loose.Add(c);
                }
            }
            // Not locked: let go, they fall (the user, 2026-10-13: only locked things stay on a pole).
            foreach (var c in loose) { Leave(c); c.itemRigidbodyC.attached = false; }
            if (found.Count == 0)
            {
                if (loose.Count > 0) Plugin.Log?.LogInfo("Wood pole picked up: " + loose.Count + " thing(s) on it not locked, let go.");
                return;
            }
            var load = new Load { pole = pole }; var frame = pole.transform;
            foreach (var c in found)
            {
                Leave(c);   // from another pole it rode
                load.things.Add(new Thing { item = c, at = frame.InverseTransformPoint(c.transform.position), turn = Quaternion.Inverse(frame.rotation) * c.transform.rotation });
                riding[c] = load; ridingCopies.Add(c.itemRigidbodyC);
            }
            int hung = 0;
            foreach (var c in found)
            {
                var lantern = HungOn(c); if (!lantern || riding.TryGetValue(lantern, out var with) && with == load) continue;
                Leave(lantern); load.things.Add(new Thing { item = lantern, hook = c, hung = true }); riding[lantern] = load; hung++;
            }
            Chain(load); loads[pole.itemRigidbodyC] = load; Hook();
            // Tarps: one tied to a carried thing is untied (its colliders still solid here), one a carried pole held up drops back.
            int untied = 0;
            if (Tarps.Registered) foreach (var c in found) { untied += Tarps.UntieFrom(c); if (WoodPoles.IsPole(c)) Tarps.PoleMoved(c, true); }
            Plugin.Log?.LogInfo((WoodPlanks.IsPlank(pole) ? "Wood plank" : "Wood pole") + " picked up with " + found.Count + " locked thing(s) on it" + (hung > 0 ? ", " + hung + " hung on its hooks" : "") + (loose.Count > 0 ? ", " + loose.Count + " not locked let go" : "") +
                ", reach " + WoodPoles.Format(load.reach) + (untied > 0 ? "; " + untied + " canvas tarp(s) untied" : "") + ".");
        }
        // The straight run on its far end: a pole nailed on the far end of the one before, pointing on (within 5 degrees), and so on.
        private static void Chain(Load load)
        {
            var pole = load.pole; float reach = pole ? WoodPoles.RodLength(pole) : 0, mass = pole ? pole.mass : 0;
            if (!WoodPoles.IsPole(pole)) { load.reach = reach; load.mass = mass; return; }   // a plank: no straight run
            var end = new Vector3(0, 0, -reach); float radius = WoodPoles.Diameter(pole) / 2;   // the end's pole's own radius
            for (int n = 0; n < load.things.Count; n++)
            {
                bool more = false;
                foreach (var t in load.things)
                {
                    if (t.hung || !t.item || !WoodPoles.IsPole(t.item) || (t.turn * Vector3.forward).z < Straight) continue;
                    if (Mathf.Abs(t.at.z - end.z) > Near || new Vector2(t.at.x - end.x, t.at.y - end.y).magnitude > radius + Near) continue;
                    end = t.at + t.turn * (Vector3.back * WoodPoles.Length(t.item)); reach = -end.z; mass += t.item.mass; radius = WoodPoles.Diameter(t.item) / 2; more = true; break;
                }
                if (!more) break;
            }
            load.reach = reach; load.mass = mass;
        }

        // After each physics step of an item's copy (ItemRigidbody.FixedUpdate, the game's or the brace's): a carried thing's copy
        // made a trigger again; a pole's load set on it.
        internal static void AfterStep(ItemRigidbody copy)
        {
            if (ridingCopies.Contains(copy)) { foreach (var c in copy.GetComponentsInChildren<Collider>(true)) if (c) c.isTrigger = true; return; }
            if (loads.TryGetValue(copy, out var load)) Follow(load);
        }
        private static void Follow(Load load)
        {
            var pole = load.pole; var copy = pole ? pole.itemRigidbodyC : null;
            // Put away: in a slot, in a crate (the game's inStove), or lying in a box. The game's box takes in a held item it passes
            // through too (ItemBox.OnTriggerEnter), and its things would be let go while still carried: a box counts only for a
            // pole not held and not attached.
            if (!copy || copy.GetCurrentInventorySlot() || copy.inStove || copy.GetCurrentBox() && !pole.held && !copy.attached) { LetGo(load); return; }
            if (load.things.RemoveAll(t => { bool gone = !t.item || !t.item.itemRigidbodyC || t.item.held || t.hung && !StillHung(t); if (gone) Unride(t.item); return gone; }) > 0) Chain(load);
            if (load.things.Count == 0) { End(load); return; }
            var frame = Frame(pole); var boat = pole.currentActualBoat;
            foreach (var t in load.things)
            {
                if (!t.hung)
                {
                    var p = frame.TransformPoint(t.at); var r = frame.rotation * t.turn;
                    t.item.transform.SetPositionAndRotation(p, r);
                    if (t.item.currentActualBoat != boat) Board(t.item, pole);
                    var c = t.item.itemRigidbodyC;
                    if (PoleBrace.onBoat(c) && t.item.currentActualBoat && t.item.currentWalkCol)
                        c.transform.SetPositionAndRotation(t.item.currentWalkCol.TransformPoint(t.item.currentActualBoat.InverseTransformPoint(p)), t.item.currentWalkCol.rotation * (Quaternion.Inverse(t.item.currentActualBoat.rotation) * r));
                    else c.transform.SetPositionAndRotation(p, r);
                }
                else if (t.item.currentActualBoat != boat) Board(t.item, pole);   // hung on its hook as the game hangs it: it only changes ship with it
            }
            if (!pole.held && (pole.nailed || copy.attached)) { End(load); return; }   // fixed again: they stay on it as wall items
            if (PoleBrace.Braced == pole || !WoodPoles.IsPole(pole)) return;   // the brace sets its body; a plank's box is its own
            // Loose or held: its body runs the whole straight length, with its weight.
            var box = PoleBrace.boxCol(copy); var body = copy.GetBody();
            if (box && Mathf.Abs(box.size.z - load.reach) > 1e-3f) { box.size = new Vector3(box.size.x, box.size.y, load.reach); box.center = new Vector3(box.center.x, box.center.y, -load.reach / 2); }
            if (body && Mathf.Abs(body.mass - load.mass) > 1e-3f) body.mass = load.mass;
        }
        // A carried thing on the pole's ship, or off any (the game boards an attached item but never takes it off a ship, and its own
        // boarding is skipped while it rides: PoleCarryBoardPatch).
        private static void Board(ShipItem item, ShipItem pole)
        {
            var copy = item.itemRigidbodyC; bool attached = copy.attached;   // leaving a ship frees it (ItemRigidbody.ExitBoat)
            try
            {
                if (item.currentActualBoat) exitBoat.Invoke(item, null);
                var embark = pole.currentActualBoat ? boatCollider(pole) : null;
                if (embark) enterBoat.Invoke(item, new object[] { embark });
            }
            catch (Exception e) { Plugin.Log?.LogWarning("A thing on a wood pole could not change ship: " + (e.InnerException ?? e).Message); }
            copy.attached = attached;
        }
        // Drawn each frame on the pole in the hand (posed after everything else, after the brace's own drawing); a hung lantern
        // under its hook after that, by the game's own hanging (HangableItem.LateUpdate).
        private static bool hooked;
        internal static void Hook() { if (hooked) return; Application.onBeforeRender += Render; hooked = true; }
        private static void Render()
        {
            PoleBrace.Draw();
            foreach (var load in loads.Values)
            {
                if (!load.pole || !load.pole.held) continue;
                var frame = Frame(load.pole);
                foreach (var t in load.things) if (t.item && !t.hung) t.item.transform.SetPositionAndRotation(frame.TransformPoint(t.at), frame.rotation * t.turn);
                foreach (var t in load.things)
                {
                    var hangs = t.hung && t.item ? t.item.GetComponent<HangableItem>() : null;
                    if (hangs) try { hangLate.Invoke(hangs, null); } catch (Exception) { }
                }
            }
        }

        private static void Unride(ShipItem item)
        {
            if ((object)item == null) return;
            riding.Remove(item);
            if (item && item.itemRigidbodyC) ridingCopies.Remove(item.itemRigidbodyC); else ridingCopies.RemoveWhere(c => !c);
        }
        // Loads of poles gone without DestroyItem (a scene unloaded): dropped.
        private static void Prune()
        {
            foreach (var dead in loads.Where(x => !x.Key || !x.Value.pole).Select(x => x.Value).ToList()) { Remove(dead); dead.things.Clear(); }
            foreach (var dead in riding.Keys.Where(k => !k).ToList()) riding.Remove(dead);
            ridingCopies.RemoveWhere(c => !c);
        }
        // One thing leaves its pole (picked up, removed).
        private static void Leave(ShipItem item)
        {
            if (!item || !riding.TryGetValue(item, out var load)) return;
            Unride(item); load.things.RemoveAll(t => t.item == item); Chain(load);
            if (load.things.Count == 0) End(load);
        }
        private static void Remove(Load load)
        {
            if (load.pole && load.pole.itemRigidbodyC) loads.Remove(load.pole.itemRigidbodyC);
            foreach (var key in loads.Where(x => x.Value == load).Select(x => x.Key).ToList()) loads.Remove(key);
            foreach (var t in load.things) Unride(t.item);
        }
        // Found again (a pole picked up once more): no tarp rebuilt in between.
        private static void Forget(Load load) { if (load == null) return; Remove(load); load.things.Clear(); }
        // Fixed again: they stay on it as wall items; its own box and weight back; the nailed poles among them hold up a tarp.
        private static void End(Load load)
        {
            var nailedPoles = load.things.Where(t => !t.hung && t.item && t.item.nailed && WoodPoles.IsPole(t.item)).Select(t => t.item).ToList();
            Remove(load); load.things.Clear();
            if (WoodPlanks.IsPlank(load.pole)) WoodPlanks.ApplyLook(load.pole);
            else if (load.pole && PoleBrace.Braced != load.pole) WoodPoles.SetLength(load.pole, WoodPoles.Length(load.pole));
            if (Tarps.Registered) foreach (var p in nailedPoles) Tarps.PoleMoved(p, false);
        }
        // The pole removed or put away: what is on it is let go and falls (un-nailed); a lantern stays on its hook.
        private static void LetGo(Load load)
        {
            Remove(load); int n = 0;
            foreach (var t in load.things) { if (t.hung || !t.item || !t.item.itemRigidbodyC) continue; t.item.nailed = false; t.item.itemRigidbodyC.attached = false; n++; }
            load.things.Clear();
            if (n > 0) Plugin.Log?.LogInfo("A wood pole was removed or put away: " + n + " thing(s) on it let go.");
        }
        // ShipItem.DestroyItem: a pole destroyed lets go of its load; a thing destroyed leaves its pole.
        internal static void Removed(ShipItem item)
        {
            var load = Of(item); if (load != null) LetGo(load);
            Leave(item);
        }
    }
    // ---- The green ghost of what a held pole carries (the user, 2026-10-20; POLE_ROUND5_DRAFT.md) ----
    // The game's ghost is one mesh (GoPointerTargeter: DisplayTargeter keeps a mesh, a place and a turn; its Update draws it in the
    // ghost material for that frame). Every way the pole's ghost is shown (the game's wall test, LeanOnWall, AimAtPole) hands it the
    // pole's own mesh at the pole's attach pose; when it is about to draw that, each locked thing on the pole is drawn too, in the
    // same material, where the drop puts it: the pole's pose times the thing's place on the pole (PoleCarry.Thing.at / turn, the
    // numbers that keep it there while held). A lantern on a carried hook hangs straight down under its hook's ghost, as it hangs
    // now. Unity's Graphics.DrawMesh for that frame (the game's item meshes are not readable, so they cannot be combined into one).
    internal static class PoleGhost
    {
        private static readonly AccessTools.FieldRef<GoPointerTargeter, bool> active = AccessTools.FieldRefAccess<GoPointerTargeter, bool>("targeterActive");
        private static readonly AccessTools.FieldRef<GoPointerTargeter, Mesh> activeMesh = AccessTools.FieldRefAccess<GoPointerTargeter, Mesh>("activeMesh");
        private static readonly AccessTools.FieldRef<GoPointerTargeter, Vector3> activePos = AccessTools.FieldRefAccess<GoPointerTargeter, Vector3>("activePos");
        private static readonly AccessTools.FieldRef<GoPointerTargeter, Quaternion> activeRot = AccessTools.FieldRefAccess<GoPointerTargeter, Quaternion>("activeRot");
        private static readonly AccessTools.FieldRef<GoPointerTargeter, Material> material = AccessTools.FieldRefAccess<GoPointerTargeter, Material>("targeterMaterial");
        // What was drawn in the last frame a loaded pole's ghost showed (the tests read it): each thing and the pose its ghost has.
        internal static readonly List<KeyValuePair<ShipItem, Pose>> Shown = new List<KeyValuePair<ShipItem, Pose>>();
        internal static int ShownFrame = -1, Parts;
        // The pose a carried thing's ghost has, the pole's ghost at `pole` (its root's pose).
        internal static Pose Place(PoleCarry.Load load, PoleCarry.Thing t, Pose pole) =>
            t.hung ? Hung(load, t, pole) : new Pose(pole.position + pole.rotation * t.at, pole.rotation * t.turn);
        // A hung lantern (apart: it reads the game's transforms, which the standalone tests cannot).
        private static Pose Hung(PoleCarry.Load load, PoleCarry.Thing t, Pose pole)
        {
            var hook = load.things.Find(h => h.item == t.hook && !h.hung);
            var hookAt = hook != null ? pole.position + pole.rotation * hook.at : pole.position;
            return new Pose(hookAt + (t.item.transform.position - t.hook.transform.position), t.item.transform.rotation);
        }
        internal static void Draw(GoPointerTargeter targeter)
        {
            if (!PoleCarry.Active || !targeter || !active(targeter)) return;
            var pointer = targeter.GetComponent<GoPointer>(); var pole = pointer ? pointer.GetHeldItem() as ShipItem : null;
            var load = PoleCarry.Of(pole); if (load == null || load.things.Count == 0) return;
            var filter = pole.GetComponent<MeshFilter>(); var paint = material(targeter);
            if (!filter || activeMesh(targeter) != filter.sharedMesh || !paint) return;
            var at = new Pose(activePos(targeter), activeRot(targeter));
            Shown.Clear(); ShownFrame = Time.frameCount; Parts = 0;
            foreach (var t in load.things)
            {
                if (!t.item || t.hung && !t.hook) continue;
                var place = Place(load, t, at); Shown.Add(new KeyValuePair<ShipItem, Pose>(t.item, place));
                var toPart = Matrix4x4.TRS(place.position, place.rotation, Vector3.one) * t.item.transform.worldToLocalMatrix;
                foreach (var r in t.item.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                    var mesh = r is SkinnedMeshRenderer skin ? skin.sharedMesh : r is MeshRenderer ? r.GetComponent<MeshFilter>()?.sharedMesh : null;
                    if (!mesh) continue;
                    var m = toPart * r.transform.localToWorldMatrix;
                    for (int i = 0; i < mesh.subMeshCount; i++) Graphics.DrawMesh(mesh, m, paint, 0, null, i);
                    Parts++;
                }
            }
        }
    }
    [HarmonyPatch(typeof(GoPointerTargeter), "Update")] internal static class PoleGhostPatch
    { static void Prefix(GoPointerTargeter __instance) { if (WoodPoles.Registered) PoleGhost.Draw(__instance); } }
    [HarmonyPatch(typeof(ItemRigidbody), "FixedUpdate")] internal static class PoleCarryStepPatch
    { static void Postfix(ItemRigidbody __instance) { if (PoleCarry.Active) PoleCarry.AfterStep(__instance); } }
    // A carried thing changes ship with its pole only (PoleCarry.Board), not by the game's boarding triggers.
    [HarmonyPatch(typeof(ShipItem), "ExtraFixedUpdate")] internal static class PoleCarryBoardPatch
    { static bool Prefix(ShipItem __instance) => !(PoleCarry.Active && PoleCarry.Riding(__instance)); }

    // ---- The wooden pallet ----
    // Prefab 645 (the user, 2026-10-24): a two-handed item of the planks' wood 15% darker (Data/wood_pallet.obj, written by
    // MakePallet.py), nailed with the game's hammer as any two-handed item on the floor. Once it is nailed, a held trade crate or
    // package aimed at it shows the game's green ghost in the next free slot of its size; R puts it there and holds it still (the
    // game's ItemRigidbody.attached: kinematic, rides with her, cleared by a pick-up; PALLET_PLANK_DRAFT.md P1: "held still in it's
    // slot"). The hammer then nails it there as the game nails a crate on the floor (P2, P3: only a snapped crate). Full and every crate
    // nailed: ropes round the load (P4). Un-nailed and picked up, the pallet takes its nailed crates (PoleCarry) and lets the others fall.
    // Nothing new is saved: which slot a crate is in is found from where it stands (within 2 cm), and a snapped crate is held still
    // again after a load (Rehold).
    internal static class WoodPallets
    {
        internal const int PalletIndex = 645;
        internal const string RopesName = "EconomyOverhaul pallet ropes";
        internal static GameObject Template; internal static Material Wood;
        internal static bool Registered => Template;
        internal static readonly HashSet<ShipItem> Live = new HashSet<ShipItem>();
        internal static bool IsPallet(ShipItem item) => Registered && WoodPlanks.Index(item) == PalletIndex;

        internal static void Register(PrefabsDirectory directory)
        {
            if (Registered || !directory || directory.directory == null) return;
            var dir = directory.directory;
            if (dir.Length > PalletIndex && dir[PalletIndex]) { Plugin.Log?.LogError("Prefab slot 645 is used by another mod; wood pallets are disabled."); return; }
            if (dir.Length <= WoodPlanks.LumberIndex || !dir[WoodPlanks.LumberIndex]) { Plugin.Log?.LogError("Lumber prefab missing; wood pallets are disabled."); return; }
            try
            {
                var holder = new GameObject("EconomyOverhaul pallet template"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder);
                var wood = dir[WoodPlanks.LumberIndex].GetComponent<MeshRenderer>().sharedMaterial;
                var paint = new Material(wood) { name = wood.name + " (pallet)" };
                if (paint.HasProperty("_Color")) { var c = wood.color; float k = Rules.PalletShade; paint.color = new Color(c.r * k, c.g * k, c.b * k, c.a); }
                var mesh = ObjMesh.Load("EconomyOverhaul.Data.wood_pallet.obj", "wood pallet");
                var o = WoodPlanks.Template(dir[WoodPlanks.LumberIndex], holder.transform, "645 wood pallet", mesh, paint, PalletIndex, "wood pallet", Rules.PalletValue, Rules.PalletMass, true);
                UnityEngine.Object.DestroyImmediate(o.GetComponent<Good>());
                o.AddComponent<PalletMark>();
                // The ropes (P4): their own child, drawn in the planks' material (the atlas's black, as the pole bundle's bands), shown
                // only while the pallet is full and every crate on it nailed (PalletMark).
                var ropes = new GameObject(RopesName); ropes.transform.SetParent(o.transform, false);
                ropes.AddComponent<MeshFilter>(); ropes.AddComponent<MeshRenderer>().sharedMaterial = wood; ropes.SetActive(false);
                Wood = wood;
                if (dir.Length <= PalletIndex) { var resized = new GameObject[PalletIndex + 1]; Array.Copy(dir, resized, dir.Length); directory.directory = dir = resized; }
                dir[PalletIndex] = o; Template = o;
                Plugin.Log?.LogInfo("Registered wood pallet (645).");
            }
            catch (Exception e) { Template = null; Plugin.Log?.LogError("Wood pallet registration failed; wood pallets are disabled. " + e); }
        }

        // ---- Loads ----
        internal static Rules.PalletLoad LoadOf(ShipItem item)
        {
            if (!item || !item.big || IsPallet(item)) return Rules.PalletLoad.None;
            var good = item.GetComponent<Good>(); return good ? Rules.LoadOf(good.sizeDescription) : Rules.PalletLoad.None;
        }
        // An item's drawn bounds in its own frame, once per prefab, from the prefab itself (what the mod or the game adds to a live
        // item later, such as the wet look, is left out).
        private static readonly Dictionary<int, Bounds> drawn = new Dictionary<int, Bounds>();
        private static Bounds Drawn(ShipItem item)
        {
            int index = WoodPlanks.Index(item);
            if (drawn.TryGetValue(index, out var cached)) return cached;
            var source = PrefabsDirectory.instance && index >= 0 && index < PrefabsDirectory.instance.directory.Length && PrefabsDirectory.instance.directory[index] ? PrefabsDirectory.instance.directory[index] : item.gameObject;
            var inv = source.transform.worldToLocalMatrix; bool any = false; var b = new Bounds();
            foreach (var mf in source.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh || !mf.GetComponent<MeshRenderer>()) continue;
                var mb = mf.sharedMesh.bounds; var m = inv * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            drawn[index] = b;
            return b;
        }
        // Where an item of `load` stands in slot `slot`, in the pallet's frame: turned to the load's size, its drawn bottom middle on
        // the slot (as the probe palletshow laid them out).
        internal static Pose SlotPose(ShipItem item, Rules.PalletLoad load, int slot)
        {
            var s = Drawn(item); var (zTo, yTo) = Rules.LoadAxes(s.size, Rules.LoadSize(load));
            Vector3 Axis(int k) => k == 0 ? Vector3.right : k == 1 ? Vector3.up : Vector3.forward;
            var turn = Quaternion.LookRotation(Axis(zTo), Axis(yTo));
            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var c = turn * (s.center + Vector3.Scale(s.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                lo = Vector3.Min(lo, c); hi = Vector3.Max(hi, c);
            }
            var bottomMid = new Vector3((lo.x + hi.x) / 2, lo.y, (lo.z + hi.z) / 2);
            return new Pose(Rules.LoadSlots(load)[slot] + new Vector3(0, Rules.PalletHeight, 0) - bottomMid, turn);
        }
        internal static Pose World(ShipItem pallet, Pose local) => new Pose(pallet.transform.TransformPoint(local.position), pallet.transform.rotation * local.rotation);
        // The crates in a pallet's slots now (snapped: held still, or nailed) and the slots they take: the saved items within 2 m of
        // it, each matched to a slot where it is drawn (within 2 cm). Found from the drawn items, as PoleCarry.PickedUp finds a pole's
        // load, not from the physics copies: aboard, a physics overlap at the pallet's copy found nothing at its pick-up under way
        // (pallet-17), its copy not where the crates' copies were. `any`: also a crate resting there loose (Rehold, after a load).
        // `within`: how far from its slot it may be (the pick-up's wider match, PickedUp).
        internal static List<(ShipItem crate, Rules.PalletLoad load, int slot)> Loaded(ShipItem pallet, bool any = false, float within = SlotMatch)
        {
            var found = new List<(ShipItem, Rules.PalletLoad, int)>();
            if (!pallet || !pallet.itemRigidbodyC) return found;
            var all = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentPrefabs() : null; if (all == null) return found;
            var at = pallet.transform.position;
            foreach (var p in all)
            {
                if (!p || (p.transform.position - at).sqrMagnitude > 4) continue;
                var crate = p.GetComponent<ShipItem>();
                if (!crate || crate == pallet || crate.held || !crate.itemRigidbodyC || !any && !crate.nailed && !crate.itemRigidbodyC.attached) continue;
                var load = LoadOf(crate); if (load == Rules.PalletLoad.None) continue;
                int slot = SlotOf(pallet, crate, load, within); if (slot >= 0) found.Add((crate, load, slot));
            }
            return found;
        }
        internal const float SlotMatch = .02f, PickupMatch = .1f;
        internal static int SlotOf(ShipItem pallet, ShipItem crate, Rules.PalletLoad load, float within = SlotMatch)
        {
            var local = pallet.transform.InverseTransformPoint(crate.transform.position);
            for (int k = 0; k < Rules.LoadSlots(load).Length; k++) if ((SlotPose(crate, load, k).position - local).magnitude < within) return k;
            return -1;
        }
        // The nailed pallet a held crate is aimed at (the crosshair on the pallet or its load's room above it, within 2.55 m, where it
        // is drawn: aboard a moving ship the physics of a drawn collider does not follow her) and the slot it would take; -1 if none.
        internal const float Reach = 2.55f;
        internal static ShipItem Aimed(ShipItem held, out int slot)
        {
            slot = -1; var pointer = held ? held.held : null; if (!pointer || !held.sold) return null;
            var load = LoadOf(held); if (load == Rules.PalletLoad.None) return null;
            var eye = new Ray(pointer.transform.position, pointer.transform.forward);
            ShipItem best = null; float near = Reach;
            foreach (var p in Live)
            {
                if (!p || !p.sold || !p.nailed || p.held) continue;   // the crate changes to the pallet's boat when put on it (Snap)
                var box = p.GetComponent<BoxCollider>(); if (!box) continue;
                float room = Rules.LoadSize(Rules.PalletLoad.LargeCrate).y;
                var centre = box.center + new Vector3(0, room / 2, 0); var half = box.size / 2 + new Vector3(0, room / 2, 0);
                if (Enter(p.transform, centre, half, eye, near, out float at)) { best = p; near = at; }
            }
            if (!best) return null;
            var loaded = Loaded(best);
            if (loaded.Count > 0 && loaded[0].load != load) return null;   // one size a pallet
            for (int k = 0; k < Rules.LoadSlots(load).Length; k++) if (!loaded.Any(x => x.slot == k)) { slot = k; return best; }
            return null;
        }
        private static bool Enter(Transform t, Vector3 centre, Vector3 half, Ray eye, float reach, out float at)
        {
            Vector3 o = t.InverseTransformPoint(eye.origin) - centre, d = t.InverseTransformDirection(eye.direction);
            float enter = 0, leave = reach; at = 0;
            for (int k = 0; k < 3; k++)
            {
                if (Mathf.Abs(d[k]) < 1e-6f) { if (Mathf.Abs(o[k]) > half[k]) return false; continue; }
                float a = (-half[k] - o[k]) / d[k], b = (half[k] - o[k]) / d[k];
                if (a > b) { var x = a; a = b; b = x; }
                enter = Mathf.Max(enter, a); leave = Mathf.Min(leave, b);
                if (enter > leave) return false;
            }
            at = enter; return true;
        }

        // ---- R: the held crate into the slot ----
        private static readonly System.Reflection.MethodInfo enterBoat = AccessTools.Method(typeof(ShipItem), "EnterBoat"), exitBoat = AccessTools.Method(typeof(ShipItem), "ExitBoat");
        internal static bool Snap(ShipItem crate, GoPointer pointer)
        {
            if (!crate || !pointer || crate.held != pointer || !GameState.playing) return false;
            var pallet = Aimed(crate, out int slot); if (!pallet) return false;
            var pose = World(pallet, SlotPose(crate, LoadOf(crate), slot));
            crate.OnDrop(); pointer.DropItem();
            // On the pallet's boat, or ashore with it: a held crate can count as aboard a boat whose trigger it passes (the pallet run
            // of 2026-10-24: a crate carried on the dock beside the starting dhow was the dhow's).
            if (crate.currentActualBoat != pallet.currentActualBoat)
            {
                try
                {
                    if (crate.currentActualBoat) exitBoat.Invoke(crate, null);
                    var embark = pallet.currentActualBoat ? Fields.Get<Collider>(pallet, "currentBoatCollider") : null;
                    if (embark) enterBoat.Invoke(crate, new object[] { embark });
                }
                catch (Exception e) { Plugin.Log?.LogWarning("Wood pallet: " + crate.name + " could not change boat: " + (e.InnerException ?? e).Message); }
            }
            // Held still in its slot, not nailed (P1): the game's attached (kinematic, rides with her walk copy aboard, cleared by leaving
            // a ship, so set after the boat change; the game's pick-up clears it only for a wall item, so TakenOff does at a pick-up). The hammer nails it there (RaycastHitsFloor below).
            if (crate.itemRigidbodyC) crate.itemRigidbodyC.attached = true;
            Place(crate, pose);
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUISound(UISounds.winchUnclick, 1f, .6f);
            Plugin.Log?.LogInfo("Wood pallet: " + crate.name + " put in slot " + slot + " (" + LoadOf(crate) + ").");
            return true;
        }
        // Snapped: held still in a slot of a nailed pallet (not picked up since). Only such a crate may be nailed on a pallet (P3:
        // "only snapped cargo can be nailed"; a crate put down there by hand stays refused, as the game refuses any item on an item).
        // What the game's floor ray (straight down from its physics copy, 4 m) meets first is a pallet, drawn or its physics copy.
        internal static bool StandsOnPallet(ShipItem item)
        {
            var body = item && item.itemRigidbodyC ? item.itemRigidbodyC.transform : null;
            if (!body || IsPallet(item) || !Physics.Raycast(body.position, Vector3.down, out var hit, 4f, -1)) return false;
            var under = hit.collider.GetComponentInParent<ShipItem>(); if (!under) { var copy = hit.collider.GetComponentInParent<ItemRigidbody>(); under = copy ? copy.GetShipItem() : null; }
            return IsPallet(under);
        }
        internal static bool Snapped(ShipItem crate) => crate && crate.sold && !crate.held && crate.itemRigidbodyC && crate.itemRigidbodyC.attached &&
            LoadOf(crate) != Rules.PalletLoad.None && OnPallet(crate);
        // After a load (attached is not saved): every crate standing in a slot of a nailed pallet is held still again, a nailed one
        // too (un-nailed later, it is a snapped crate again, which the hammer can nail once more).
        internal static void Rehold(ShipItem pallet)
        {
            if (!pallet || !pallet.nailed || pallet.held) return;
            int n = 0;
            foreach (var (crate, _, _) in Loaded(pallet, true))
                if (crate.itemRigidbodyC && !crate.itemRigidbodyC.attached) { crate.itemRigidbodyC.attached = true; n++; }
            if (n > 0) Plugin.Log?.LogInfo("Wood pallet: " + n + " crate(s) held in their slots again.");
        }
        // The drawn item and its physics copy at `pose` (aboard, the copy on her walk copy), still.
        internal static void Place(ShipItem item, Pose pose)
        {
            item.transform.SetPositionAndRotation(pose.position, pose.rotation);
            var body = item.itemRigidbodyC; if (!body) return;
            if (item.currentActualBoat && item.currentWalkCol) body.ForceRigidbodyToWalkCol();
            else body.transform.SetPositionAndRotation(pose.position, pose.rotation);
            var rb = body.GetBody(); if (!rb) return;
            rb.position = body.transform.position; rb.rotation = body.transform.rotation;
            if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        }
        // The green ghost of the held crate in its slot, each frame it is aimed (the game's targeter material, every part of the crate).
        private static readonly AccessTools.FieldRef<GoPointerTargeter, Material> targeterMaterial = AccessTools.FieldRefAccess<GoPointerTargeter, Material>("targeterMaterial");
        internal static ShipItem GhostPallet; internal static int GhostSlot = -1, GhostFrame = -1;   // for tests
        internal static void Ghost(GoPointer pointer)
        {
            var crate = pointer ? pointer.GetHeldItem() as ShipItem : null;
            if (!crate || LoadOf(crate) == Rules.PalletLoad.None) return;
            var pallet = Aimed(crate, out int slot); if (!pallet) return;
            var targeter = pointer.GetTargeter(); var paint = targeter ? targeterMaterial(targeter) : null; if (!paint) return;
            var pose = World(pallet, SlotPose(crate, LoadOf(crate), slot));
            var toSlot = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one) * crate.transform.worldToLocalMatrix;
            foreach (var r in crate.GetComponentsInChildren<MeshRenderer>())
            {
                var mesh = r.enabled && r.gameObject.activeInHierarchy ? r.GetComponent<MeshFilter>()?.sharedMesh : null; if (!mesh) continue;
                var m = toSlot * r.transform.localToWorldMatrix;
                for (int i = 0; i < mesh.subMeshCount; i++) Graphics.DrawMesh(mesh, m, paint, 0, null, i);
            }
            GhostPallet = pallet; GhostSlot = slot; GhostFrame = Time.frameCount;
        }
        // A crate in a slot is picked up as the game picks up any crate: a snapped one at once (TakenOff clears attached), a nailed
        // one not until the hammer un-nails it (the game never picks up a nailed item).
        internal static bool OnPallet(ShipItem crate)
        {
            var load = LoadOf(crate);
            foreach (var p in Live) if (p && p.nailed && (p.transform.position - crate.transform.position).sqrMagnitude < 4 && SlotOf(p, crate, load) >= 0) return true;
            return false;
        }

        // ---- The ropes (P4) ----
        private static readonly Dictionary<Rules.PalletLoad, Mesh> ropeMeshes = new Dictionary<Rules.PalletLoad, Mesh>();
        // Full and every crate nailed: the load it holds, else None.
        internal static Rules.PalletLoad RopedLoad(ShipItem pallet)
        {
            var loaded = Loaded(pallet); if (loaded.Count == 0) return Rules.PalletLoad.None;
            var load = loaded[0].load;
            return Rules.Roped(Rules.LoadSlots(load).Length, loaded.Select(x => x.slot).Distinct().Count(), loaded.Count(x => x.crate.nailed)) ? load : Rules.PalletLoad.None;
        }
        // Each loop's sides as square bars 2.5 cm across, each running on past its corners by the rope's half thickness (so the corners
        // close), in the atlas's black (the pole bundle's bands' spot): 3 loops of 4 bars, 144 triangles.
        internal static Mesh RopeMesh(Rules.PalletLoad load)
        {
            if (ropeMeshes.TryGetValue(load, out var cached)) return cached;
            var v = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<int>(); float r = Rules.RopeHalf;
            foreach (var loop in Rules.RopeLoops(load))
                for (int i = 0; i < loop.Length; i++)
                {
                    Vector3 a = loop[i], b = loop[(i + 1) % loop.Length], d = (b - a).normalized;
                    a -= d * r; b += d * r;
                    var side = Vector3.Cross(d, Mathf.Abs(d.y) > .9f ? Vector3.forward : Vector3.up).normalized; var up = Vector3.Cross(side, d);
                    foreach (var (u, w) in new[] { (side, up), (up, -side), (-side, -up), (-up, side) })
                    {
                        int s = v.Count;
                        v.Add(a + (u + w) * r); v.Add(a + (u - w) * r); v.Add(b + (u - w) * r); v.Add(b + (u + w) * r);
                        for (int k = 0; k < 4; k++) n.Add(u);
                        t.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 });
                    }
                    foreach (var (end, normal) in new[] { (a, -d), (b, d) })
                    {
                        int s = v.Count;
                        foreach (var (p, q) in new[] { (1, 1), (1, -1), (-1, -1), (-1, 1) }) { v.Add(end + (side * p + up * q) * r); n.Add(normal); }
                        if (normal == d) t.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 }); else t.AddRange(new[] { s, s + 2, s + 1, s, s + 3, s + 2 });
                    }
                }
            var m = new Mesh { name = "wood pallet ropes " + load };
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, Enumerable.Repeat(new Vector2(.01f, .01f), v.Count).ToList()); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return ropeMeshes[load] = m;
        }
        internal static void ShowRopes(ShipItem pallet, Rules.PalletLoad load)
        {
            var ropes = pallet ? pallet.transform.Find(RopesName) : null; if (!ropes) return;
            bool on = load != Rules.PalletLoad.None;
            if (on) { var f = ropes.GetComponent<MeshFilter>(); var mesh = RopeMesh(load); if (f.sharedMesh != mesh) f.sharedMesh = mesh; }
            if (ropes.gameObject.activeSelf != on) ropes.gameObject.SetActive(on);
        }

        // ---- A loaded pallet carried (P5-P7: "you can pick up pallet and all crate on top that is nailed will following the pallet.
        // Unnailed crate will drop") ----
        // Picked up (ShipItem.OnPickup, before the game frees it): its nailed crates ride it, kept in their slots as it is drawn (in
        // the hand, thrown or lying loose), aboard on its ship, adding no weight (P5), their physics copies triggers while the pallet is
        // held and solid while it is down (AfterStep); a snapped crate not nailed is let go and falls. Nailed again (not held), they are plain nailed crates in its slots again.
        // The pallet itself stays a plain two-handed item (the user: "pallet doesn't need pole behavoir, it should just be normal
        // shipitem.big").
        internal sealed class Rider { internal ShipItem crate; internal Vector3 at; internal Quaternion turn; internal List<Collider> ignored; }
        private static readonly Dictionary<ShipItem, List<Rider>> carried = new Dictionary<ShipItem, List<Rider>>();
        private static readonly Dictionary<ItemRigidbody, ShipItem> riderCopies = new Dictionary<ItemRigidbody, ShipItem>();   // a rider's copy: its pallet
        private static readonly HashSet<ShipItem> riders = new HashSet<ShipItem>();
        internal static bool Carrying => carried.Count > 0;
        internal static bool Riding(ShipItem crate) => crate && riders.Contains(crate);
        internal static List<Rider> Riders(ShipItem pallet) => pallet && carried.TryGetValue(pallet, out var list) ? list : null;
        internal static void PickedUp(ShipItem item)
        {
            if (!IsPallet(item)) { TakenOff(item); return; }
            if (!item.sold || !item.itemRigidbodyC) return;
            End(item);
            var list = new List<Rider>(); int dropped = 0;
            // Within 10 cm of a slot, and carried exactly in it: un-nailed, the pallet itself can settle under its nailed crates before
            // it is picked up (pallet-20: aboard, placed on a deck 1.1 cm uneven and nailed at once, it settled 3 cm and 1.5 degrees, and
            // the 2 cm match found no crate).
            foreach (var (crate, load, slot) in Loaded(item, false, PickupMatch))
            {
                if (!crate.nailed) { crate.itemRigidbodyC.attached = false; dropped++; continue; }
                var pose = SlotPose(crate, load, slot);
                var rider = new Rider { crate = crate, at = pose.position, turn = pose.rotation, ignored = new List<Collider>() };
                // Solid while the pallet is down, so its own colliders are kept from touching the crate's (the crate, moved onto the pallet
                // each step, would shove the loose pallet it rests on).
                foreach (var a in crate.itemRigidbodyC.GetComponentsInChildren<Collider>(true))
                    foreach (var b in item.itemRigidbodyC.GetComponentsInChildren<Collider>(true))
                        if (a && b) { Physics.IgnoreCollision(a, b, true); rider.ignored.Add(a); rider.ignored.Add(b); }
                list.Add(rider);
                riders.Add(crate); riderCopies[crate.itemRigidbodyC] = item;
            }
            if (list.Count > 0) { carried[item] = list; Hook(); }
            if (list.Count + dropped > 0) Plugin.Log?.LogInfo("Wood pallet picked up with " + list.Count + " nailed crate(s)" + (dropped > 0 ? "; " + dropped + " not nailed let go" : "") + ".");
        }
        // A crate or package taken off a pallet by hand, held still in its slot: let go of. The game clears `attached` at a pick-up only for
        // a wall item (ShipItem.OnPickup), so a snapped crate kept it and hung in the air where it was dropped (the user, 2026-10-24:
        // "After taking cargo out from pallet, it no longer be affect by gravity, when drop it will stay in air"). Not an item the game
        // holds for its crate after a load (inStove).
        internal static void TakenOff(ShipItem item)
        {
            var body = item ? item.itemRigidbodyC : null;
            if (!body || !body.attached || item.wallAttachment || body.inStove || LoadOf(item) == Rules.PalletLoad.None) return;
            body.attached = false;
            Plugin.Log?.LogInfo("Wood pallet: " + item.name + " taken off its slot.");
        }
        // After each physics step of an item's copy: a rider's copy solid while its pallet is down, a trigger while the pallet is held, as
        // the held pallet is (the user, 2026-10-24: "When unnailed the pallet with crate nailed on top, the crate on top won't have
        // collision, i want to change that"; asked, solid "when pallet is down"; triggers always until then: P5); a pallet's riders set on it.
        internal static void AfterStep(ItemRigidbody copy)
        {
            if (riderCopies.TryGetValue(copy, out var on)) { bool held = !on || on.held; foreach (var c in copy.GetComponentsInChildren<Collider>(true)) if (c && c.isTrigger != held) c.isTrigger = held; return; }
            var pallet = copy.GetShipItem(); if (pallet && carried.ContainsKey(pallet)) Follow(pallet);
        }
        private static void Follow(ShipItem pallet)
        {
            var list = carried[pallet];
            // Un-nailed by the hammer on a pallet carried or lying loose: it is let go and falls (P7: "Unnailed crate will drop").
            list.RemoveAll(r =>
            {
                bool gone = !r.crate || !r.crate.itemRigidbodyC || r.crate.held || !r.crate.nailed; if (!gone) return false;
                if (r.crate && r.crate.itemRigidbodyC && !r.crate.held) r.crate.itemRigidbodyC.attached = false;
                Unride(r); return true;
            });
            if (list.Count == 0) { End(pallet); return; }
            var frame = pallet.transform; var boat = pallet.currentActualBoat;
            foreach (var r in list)
            {
                var p = frame.TransformPoint(r.at); var q = frame.rotation * r.turn;
                r.crate.transform.SetPositionAndRotation(p, q);
                if (r.crate.currentActualBoat != boat) Board(r.crate, pallet);
                var c = r.crate.itemRigidbodyC;
                if (PoleBrace.onBoat(c) && r.crate.currentActualBoat && r.crate.currentWalkCol)
                    c.transform.SetPositionAndRotation(r.crate.currentWalkCol.TransformPoint(r.crate.currentActualBoat.InverseTransformPoint(p)), r.crate.currentWalkCol.rotation * (Quaternion.Inverse(r.crate.currentActualBoat.rotation) * q));
                else c.transform.SetPositionAndRotation(p, q);
            }
            if (!pallet.held && pallet.nailed) End(pallet);   // nailed again: plain nailed crates in its slots
        }
        // A rider on the pallet's ship, or off any (its own boarding skipped while it rides: PalletRiderBoardPatch).
        private static void Board(ShipItem crate, ShipItem pallet)
        {
            try
            {
                if (crate.currentActualBoat) exitBoat.Invoke(crate, null);
                var embark = pallet.currentActualBoat ? Fields.Get<Collider>(pallet, "currentBoatCollider") : null;
                if (embark) enterBoat.Invoke(crate, new object[] { embark });
            }
            catch (Exception e) { Plugin.Log?.LogWarning("Wood pallet: " + crate.name + " could not change ship: " + (e.InnerException ?? e).Message); }
        }
        // Drawn each frame on the pallet in the hand (the game sets a held item's transform every frame; the bodies move on physics steps).
        private static bool hooked;
        private static void Hook() { if (hooked) return; Application.onBeforeRender += Render; hooked = true; }
        private static void Render()
        {
            foreach (var pair in carried)
            {
                if (!pair.Key || !pair.Key.held) continue;
                var frame = pair.Key.transform;
                foreach (var r in pair.Value) if (r.crate) r.crate.transform.SetPositionAndRotation(frame.TransformPoint(r.at), frame.rotation * r.turn);
            }
        }
        private static void Unride(Rider r)
        {
            if (r == null || (object)r.crate == null) return;
            var crate = r.crate;
            riders.Remove(crate);
            if (crate && crate.itemRigidbodyC) riderCopies.Remove(crate.itemRigidbodyC); else foreach (var gone in riderCopies.Keys.Where(k => !k).ToList()) riderCopies.Remove(gone);
            // The pallet's colliders and the crate's touch again; its own (a plain nailed or loose crate's) the game sets each step.
            if (r.ignored != null) for (int i = 0; i + 1 < r.ignored.Count; i += 2) if (r.ignored[i] && r.ignored[i + 1]) Physics.IgnoreCollision(r.ignored[i], r.ignored[i + 1], false);
            r.ignored = null;
        }
        private static void End(ShipItem pallet)
        {
            if (!carried.TryGetValue(pallet, out var list)) return;
            foreach (var r in list) Unride(r);
            carried.Remove(pallet);
        }
        // ShipItem.DestroyItem: a pallet removed lets its riders go (un-nailed, they fall) and its snapped crates; a crate removed leaves.
        internal static void Removed(ShipItem item)
        {
            if (IsPallet(item))
            {
                if (carried.TryGetValue(item, out var list)) foreach (var r in list) if (r.crate) { r.crate.nailed = false; if (r.crate.itemRigidbodyC) r.crate.itemRigidbodyC.attached = false; }
                End(item);
                if (item.itemRigidbodyC) foreach (var (crate, _, _) in Loaded(item)) if (!crate.nailed && crate.itemRigidbodyC) crate.itemRigidbodyC.attached = false;
                return;
            }
            if (!riders.Contains(item)) return;
            foreach (var list in carried.Values) list.RemoveAll(r => { if (r.crate != item) return false; Unride(r); return true; });
        }
    }
    // Each pallet in the game (WoodPallets.Live); a snapped crate held again after a load (Rehold); its ropes shown or hidden every
    // half second (P4).
    internal sealed class PalletMark : MonoBehaviour
    {
        private ShipItem item; private float next;
        void OnEnable() { item = GetComponent<ShipItem>(); if (item) WoodPallets.Live.Add(item); }
        void OnDisable() { if (item) WoodPallets.Live.Remove(item); }
        System.Collections.IEnumerator Start()
        {
            for (int i = 0; i < 30; i++) yield return new WaitForFixedUpdate();
            if (item && item.sold) WoodPallets.Rehold(item);
        }
        void Update()
        {
            if (!item || !item.sold || Time.time < next) return;
            next = Time.time + .5f;
            WoodPallets.ShowRopes(item, WoodPallets.RopedLoad(item));
        }
    }
    [HarmonyPatch(typeof(ShipItemCrate), "OnAltActivate", new[] { typeof(GoPointer) })] internal static class PalletCrateSnapPatch
    { static bool Prefix(ShipItemCrate __instance, GoPointer activatingPointer) => !(WoodPallets.Registered && WoodPallets.Snap(__instance, activatingPointer)); }
    [HarmonyPatch(typeof(GoPointerButton), "OnAltActivate", new[] { typeof(GoPointer) })] internal static class PalletItemSnapPatch
    { static void Postfix(GoPointerButton __instance, GoPointer activatingPointer) { if (WoodPallets.Registered && __instance is ShipItem item && !(item is ShipItemCrate)) WoodPallets.Snap(item, activatingPointer); } }
    [HarmonyPatch(typeof(GoPointer), "LateUpdate")] internal static class PalletPointerPatch
    { static void Postfix(GoPointer __instance) { if (WoodPallets.Registered) WoodPallets.Ghost(__instance); } }
    // The hammer nails a snapped crate on a pallet (P2: "Cargo snapped on nailed pallet can be nailed, vanilla don't allow this"): the
    // game's floor test (a ray down from the crate's body, refused when it meets an item) passes for it.
    // The pallet itself: its pivot is at its base, so lying on the floor the game's ray starts on the floor's surface and can miss it
    // ("FALSE Floor raycast NO HIT" on a pallet resting on the dock, pallet-12 and -13; pallet-9 hit). The same test again from 10 cm up
    // inside the pallet (a ray never meets the collider it starts in), the game's rules: the floor layer (8) or anything not an item.
    [HarmonyPatch(typeof(ShipItemHammer), "RaycastHitsFloor")] internal static class PalletNailPatch
    {
        static void Postfix(ShipItem item, ref bool __result)
        {
            if (!WoodPallets.Registered || !item) return;
            // Aboard, the game's ray meets a pallet's physics copy (on her walk copy, no ShipItem there) and passes any item standing on
            // it (pallet-16: "TRUE Floor raycast hit: wood pallet:ItemRigidbody"): one not snapped there is refused, as on land (P3).
            if (__result) { if (!WoodPallets.Snapped(item) && WoodPallets.StandsOnPallet(item)) __result = false; return; }
            if (WoodPallets.Snapped(item)) { __result = true; return; }
            if (!WoodPallets.IsPallet(item) || !item.itemRigidbodyC) return;
            var body = item.itemRigidbodyC.transform;
            if (!Physics.Raycast(body.position + body.up * .1f, Vector3.down, out var hit, 4f, -1)) return;
            __result = hit.collider.gameObject.layer == 8 || !hit.collider.GetComponent<ShipItem>();
        }
    }
    [HarmonyPatch(typeof(ShipItem), "OnPickup")] internal static class PalletPickupPatch
    { static void Prefix(ShipItem __instance) { if (WoodPallets.Registered) WoodPallets.PickedUp(__instance); } }   // before the game frees it
    [HarmonyPatch(typeof(ItemRigidbody), "FixedUpdate")] internal static class PalletCarryStepPatch
    { static void Postfix(ItemRigidbody __instance) { if (WoodPallets.Carrying) WoodPallets.AfterStep(__instance); } }
    // A rider changes ship with its pallet only (WoodPallets.Board), not by the game's boarding triggers.
    [HarmonyPatch(typeof(ShipItem), "ExtraFixedUpdate")] internal static class PalletRiderBoardPatch
    { static bool Prefix(ShipItem __instance) => !(WoodPallets.Carrying && WoodPallets.Riding(__instance)); }
    [HarmonyPatch(typeof(ShipItem), "DestroyItem")] internal static class PalletRemovePatch
    { static void Prefix(ShipItem __instance) { if (WoodPallets.Registered) WoodPallets.Removed(__instance); } }

    // ---- The wood vendors ----
    // A new vendor beside the shipyard keeper of Gold Rock City, Dragon Cliffs and Fort Aestrin's vanilla yard (none at Chronos), and
    // at Kicia Bay the furniture stall's keeper (the user, 2026-10-24): 8 pallets in a stack, 8 plank bundles and 6 pole bundles, at
    // fixed places in the island scenery's frame (the probe palletshow found and pictured them by the user's rules, §4 "Placed by the
    // user's rules"). Vanilla first: the vendor is a copy of the port's nearest other shopkeeper (his look, region and hours), selling
    // through a ShopArea of his own with one ShopItemSpawner per item (the game's restock 120 s after a sale, by day only).
    internal static class WoodVendors
    {
        // rows: against a building, the pallet stack at z 0..1.28 from its back, the plank bundles at z 1.43..2.11, the pole bundles
        // at z 2.21..2.81, all on the block's floor (Gold Rock City, Dragon Cliffs); apart: each group at its own place and floor, its
        // bottom middle, long along its x: the stack, the plank bundles and the pole bundles (Fort Aestrin, where the floor steps
        // between them; Kicia Bay, without plank bundles: the stall sells its own).
        internal enum Layout { Rows, Apart }
        internal sealed class Place
        {
            internal string port, keeper; internal int scene; internal Layout layout;
            internal bool shared;                                   // sold by Kicia Bay's furniture stall keeper (no vendor, no plank bundles of its own)
            internal Vector3 block; internal float blockYaw;        // rows: the block's origin; apart: the stack's bottom middle
            internal Vector3 vendor; internal float vendorYaw;      // none when shared
            internal Vector3 planks; internal float planksYaw;      // apart, not shared
            internal Vector3 poles; internal float polesYaw;        // apart
        }
        internal static readonly Place[] Places = {
            // The probe palletshow's places by the user's rounds of 2026-10-24 (PALLET_JUNK_CREW_DRAFT.md §4).
            // Gold Rock City (third round, run 20261024-151333): the stack against the building, the plank and pole bundles in front,
            // square to the vendor, all on his side (away from the keeper); he stands under the awning beside the keeper, facing out as
            // he does.
            new Place { port = "Gold Rock City", scene = 1, keeper = "shipyardkeeper (11)", layout = Layout.Rows, block = new Vector3(1561.023f, 5.496f, -322.986f), blockYaw = 56.90f, vendor = new Vector3(1564.179f, 5.492f, -324.767f), vendorYaw = 56.9f },
            // Dragon Cliffs (third round, run 20261024-152136; the fourth round's 3 x 3 poles, run -155724, the same place): the stock against the wall
            // across from the keeper (east_house_1 (30), 6 m in front of him), on the sand's mean height; the vendor beside the stack,
            // facing the keeper.
            new Place { port = "Dragon Cliffs", scene = 9, keeper = "shipyardkeeper", layout = Layout.Rows, block = new Vector3(-157.266f, 1.534f, -547.594f), blockYaw = 337.07f, vendor = new Vector3(-156.391f, 1.557f, -546.527f), vendorYaw = 313.7f },
            // Fort Aestrin: the stack against the building (ita_building_4 (9)), 0.84 m from the small crates by it, its back 0.40 m off
            // the wall (run 20261024-113231); the plank bundles and then the pole bundles in front of it, 4 cm lower (third round, run
            // 20261024-151333; the 9 pole bundles' place run -155724); the vendor's back to the small crates, facing the keeper.
            new Place { port = "Fort Aestrin", scene = 15, keeper = "shopkeeper (shipyard)", layout = Layout.Apart, block = new Vector3(12.435f, 2.305f, 85.557f), blockYaw = 25.05f, planks = new Vector3(12.871f, 2.264f, 86.492f), planksYaw = 25.05f, poles = new Vector3(13.184f, 2.264f, 87.156f), polesYaw = 25.05f, vendor = new Vector3(10.156f, 2.263f, 87.797f), vendorYaw = 38.2f },
            // Kicia Bay: the stack just outside the furniture stall's front corner by its front table, past the far front post, out
            // from under the roof, on the sand's mean height (option A, the user: "Option a is fine", run 20261024-153217); the pole
            // bundles beside the stall's own plank bundles, lying as they do, 0.15 m off them (the user: "place the wood pole beside
            // wood plank bundle", run 20261024-152047).
            new Place { port = "Kicia Bay", scene = 27, keeper = "shipyardkeeper (12)", layout = Layout.Apart, shared = true, block = new Vector3(-27.633f, 1.324f, 106.543f), blockYaw = 293.65f, poles = new Vector3(-29.547f, 1.360f, 112.627f), polesYaw = 24.02f },
        };
        internal const string VendorName = "EconomyOverhaul wood vendor", AreaName = "EconomyOverhaul wood stock", SlotName = "EconomyOverhaul wood stock item";
        internal const string KiciaStall = "shop area (15)";

        // Built when a shop area of the port's scene wakes (ShopArea.Awake, PlankStockPatch): the shipyard keeper found by his name
        // under the island scenery (he is not a Shopkeeper: the palletkicia run of 2026-10-24 found none of that name), once a scene.
        internal static void Build(ShopArea any)
        {
            if (!any || Uninstall.Prepared || !WoodPallets.Registered || !WoodPlanks.Registered || !WoodPoles.BundleTemplate) return;
            var scene = any.gameObject.scene; var place = Places.FirstOrDefault(p => p.scene == scene.buildIndex); if (place == null) return;
            var keeper = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                .FirstOrDefault(t => t.name == place.keeper && t.parent && t.parent.GetComponent<IslandSceneryScene>());
            var root = keeper ? keeper.parent : null;
            if (!root || root.Find(AreaName)) return;
            Quaternion Yaw(float y) => root.rotation * Quaternion.Euler(0, y, 0);
            var groups = new List<(string what, Pose at)>();
            if (place.layout == Layout.Apart)
            {
                groups.Add(("stack", new Pose(root.TransformPoint(place.block), Yaw(place.blockYaw))));
                if (!place.shared) groups.Add(("planks", new Pose(root.TransformPoint(place.planks), Yaw(place.planksYaw))));
                groups.Add(("poles", new Pose(root.TransformPoint(place.poles), Yaw(place.polesYaw))));
            }
            else
            {
                var b = new Pose(root.TransformPoint(place.block), Yaw(place.blockYaw));
                Pose At(Vector3 local) => new Pose(b.position + b.rotation * local, b.rotation);
                groups.Add(("stack", At(new Vector3(0, 0, .64f)))); groups.Add(("planks", At(new Vector3(0, 0, Rules.PlankRowsZ)))); groups.Add(("poles", At(new Vector3(0, 0, Rules.PoleRowsZ))));
            }
            int poles = Rules.PoleBundlesAt(place.shared), poleCols = Rules.PoleColumnsAt(place.shared), poleLayers = poles / poleCols;
            // The shop area: one trigger box over each group (its own frame, unscaled), on a kinematic body of its own.
            var areaObject = new GameObject(AreaName); areaObject.transform.SetParent(root, false); WoodPlanks.ScaleOne(areaObject.transform);
            areaObject.transform.SetPositionAndRotation(groups[0].at.position, groups[0].at.rotation);
            foreach (var g in groups)
            {
                var box = new GameObject(AreaName + " " + g.what) { layer = 2 }; box.transform.SetParent(areaObject.transform, false);   // Ignore Raycast, as the game's shop areas (ShopArea.Awake sets only its own)
                box.transform.SetPositionAndRotation(g.at.position, g.at.rotation);
                var c = box.AddComponent<BoxCollider>(); c.isTrigger = true;
                var size = g.what == "stack" ? new Vector3(Rules.PalletWidth, Rules.VendorPallets * Rules.PalletStep, Rules.PalletDepth) : g.what == "planks" ? new Vector3(1.75f, Rules.VendorPlankBundles / 2 * Rules.PlankBundleStep, 2 * Rules.PlankBundleAcross) : new Vector3(2f, poleLayers * Rules.PoleBundleStep, poleCols * Rules.PoleBundleStep);
                c.size = size + new Vector3(.3f, .3f, .3f); c.center = new Vector3(0, size.y / 2, 0);
            }
            var area = areaObject.AddComponent<ShopArea>();
            if (area.itemsForSale == null) area.itemsForSale = new List<ShipItem>();
            if (place.shared) areaObject.AddComponent<SharedKeeper>().source = root.GetComponentsInChildren<ShopArea>(true).FirstOrDefault(a => a.name == KiciaStall);
            else
            {
                var source = root.GetComponentsInChildren<Shopkeeper>(true).Where(s => s.transform != keeper && s.transform.parent == root && !s.name.ToLowerInvariant().Contains("shipyard") && !s.name.StartsWith(VendorName))
                    .OrderBy(s => (s.transform.position - keeper.position).sqrMagnitude).FirstOrDefault();
                if (!source) { Plugin.Log?.LogWarning("Wood vendor at " + place.port + ": no shopkeeper to copy; the stock is not sold there."); UnityEngine.Object.Destroy(areaObject); return; }
                var vendor = UnityEngine.Object.Instantiate(source.gameObject, root).GetComponent<Shopkeeper>(); vendor.name = VendorName;
                vendor.transform.SetPositionAndRotation(root.TransformPoint(place.vendor), Yaw(place.vendorYaw));
                vendor.RegisterShop(area); Fields.Set(area, "keeper", vendor);
            }
            foreach (var g in groups)
                if (g.what == "stack") for (int i = 0; i < Rules.VendorPallets; i++) Slot(root, WoodPallets.Template, new Pose(g.at.position + g.at.rotation * new Vector3(0, i * Rules.PalletStep, 0), g.at.rotation));
                else if (g.what == "planks")
                {
                    var rot = g.at.rotation * Quaternion.LookRotation(Vector3.up, Vector3.right);   // a bundle's length (mesh y) along the group's x, its thickness (mesh z) up
                    var mesh = WoodPlanks.StockMesh; float half = mesh.bounds.size.z / 2;
                    for (int j = 0; j < Rules.VendorPlankBundles / 2; j++) for (int i = 0; i < 2; i++)
                        Slot(root, WoodPlanks.BundleTemplate, new Pose(g.at.position + g.at.rotation * new Vector3(0, half + j * Rules.PlankBundleStep, (i - .5f) * Rules.PlankBundleAcross) - rot * mesh.bounds.center, rot));
                }
                else
                {
                    var rot = g.at.rotation * Quaternion.LookRotation(Vector3.right, Vector3.up);   // a bundle's poles (mesh -z) along the group's x
                    var mesh = WoodPoles.BundleMesh(); float half = mesh.bounds.size.y / 2;
                    for (int j = 0; j < poleLayers; j++) for (int i = 0; i < poleCols; i++)
                        Slot(root, WoodPoles.BundleTemplate, new Pose(g.at.position + g.at.rotation * new Vector3(0, half + j * Rules.PoleBundleStep, (i - (poleCols - 1) / 2f) * Rules.PoleBundleStep) - rot * mesh.bounds.center, rot));
                }
            Plugin.Log?.LogInfo("Wood vendor at " + place.port + ": " + Rules.VendorPallets + " pallets, " + (place.shared ? "the stall's own plank bundles, " : Rules.VendorPlankBundles + " plank bundles, ") + poles + " pole bundles" + (place.shared ? " (the furniture stall's keeper)." : "."));
        }
        private static void Slot(Transform root, GameObject prefab, Pose at)
        {
            var slot = new GameObject(SlotName); slot.transform.SetParent(root, false); WoodPlanks.ScaleOne(slot.transform); slot.transform.SetPositionAndRotation(at.position, at.rotation);
            var spawner = slot.AddComponent<ShopItemSpawner>(); spawner.itemPrefab = prefab; spawner.availableAtNight = false; spawner.priceMult = 1;
        }
    }
    // Kicia Bay's pallets and pole bundles sell through the furniture stall's keeper (the user: "kicia bay don't need additional
    // vendor"): their own shop area outside the stall's tent takes his keeper once the stall has found him.
    internal sealed class SharedKeeper : MonoBehaviour
    {
        internal ShopArea source;
        void Update()
        {
            var area = GetComponent<ShopArea>(); if (!area || !source || area.GetShopkeeper()) return;
            var keeper = source.GetShopkeeper(); if (keeper) Fields.Set(area, "keeper", keeper);
        }
    }

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
        internal const float FullHealth = Rules.TarpFullHealth;   // the wear a day: Rules.TarpWearPerDay
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
        // rope runs from its corners over the pole, over its point farthest out toward the side there (a standing pole's top, a lying
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
            // A point a pole holds up on a side, kept off its corners as Vertex keeps the fixed points: at a corner's (u, v) it
            // lifted the corner itself off its knot (a pole up to 15 cm outside a corner; the review of 2026-10-18, Claude's fix).
            float Off(float x, int seg) => seg < 2 ? x : Mathf.Clamp(x, 1f / seg, 1 - 1f / seg);
            (float u, float v) OnSide(float u, float v, int side) => side == 0 || side == 2 ? (Off(u, L.segU), v) : (u, Off(v, L.segV));
            var laced = Enumerable.Range(0, L.points.Length).Where(k => L.pinned[k]).Select(k => L.points[k]).ToArray();
            // Sides: 0 c0-c1 (v 0), 1 c1-c2 (u 1), 2 c2-c3 (v 1), 3 c3-c0 (u 0); -1 inside.
            var held = new List<(float u, float v, float h, Vector3 at, int side)>();   // every touching point: it lifts the canvas around it
            var fixes = new List<(float h, Vector3 at, int k, bool end)>();   // the sheet's points fixed on the poles (end: at a pole's end)
            foreach (var pole in poles)
            {
                var points = new List<(float u, float v, float h, Vector3 at, int side)>(); var pins = new List<(float h, Vector3 at, int k, bool end)>(); bool steep = false;
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
                    raised[k] = true; var on = At(su, sv) + up * lift; var (hu, hv) = OnSide(su, sv, k);
                    points.Add((hu, hv, lift + PoleCap, on, k)); pins.Add((lift + PoleCap, on, Vertex(su, sv, k), false));
                }
                // How high a point may lift the canvas (45 degrees): its distance to the corners and to the sides that stay; `own` left out.
                float Room(Vector2 x, float[] to, int own) { float m = q.Min(z => (z - x).magnitude); for (int k = 0; k < 4; k++) if (k != own && !raised[k]) m = Mathf.Min(m, to[k]); return m; }
                foreach (var z in points) { var x = P2(z.at); if (z.h - PoleCap > Room(x, ToSides(x), z.side)) steep = true; }
                var byEdge = new List<(float u, float v, float h, Vector3 at, int side, float outward, bool end)>();   // near a side it does not cross
                int n = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(pole.a, pole.b) / PoleStep));
                for (int st = 0; st <= n; st++)
                {
                    bool end = st == 0 || st == n;
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
                        if (!(side == 0 && j == 0 || side == 1 && i == L.segU || side == 2 && j == L.segV || side == 3 && i == 0)) pins.Add((lift + PoleCap, p, Vertex(cu, cv, -1), end));
                        continue;
                    }
                    if (inside && to[side] >= EdgeBand) { if (lift > Room(x, to, -1)) steep = true; points.Add((cu, cv, lift + PoleCap, p, -1)); pins.Add((lift + PoleCap, p, Vertex(cu, cv, -1), end)); continue; }
                    if (lift < 0) continue;   // under the side's rope (a rope never hangs)
                    if (lift > Room(x, to, side)) steep = true;
                    if (side == 0) cv = 0; else if (side == 1) cu = 1; else if (side == 2) cv = 1; else cu = 0;
                    (cu, cv) = OnSide(cu, cv, side);
                    points.Add((cu, cv, lift + PoleCap, p, side)); byEdge.Add((cu, cv, lift + PoleCap, p, side, inside ? -to[side] : to[side], end));
                }
                // Near a side it does not cross, each of the side's points it holds up rests on the pole's point farthest out toward the side's
                // line (of those within 10 cm of it, the highest).
                foreach (var g in byEdge.GroupBy(z => Vertex(z.u, z.v, z.side)))
                {
                    float most = g.Max(z => z.outward);
                    var on = g.Where(z => z.outward >= most - PoleStep).OrderByDescending(z => z.h).ThenByDescending(z => z.outward).First();
                    pins.Add((on.h, on.at, g.Key, on.end));
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
            // Each touching point fixes the sheet's point nearest it (never a corner; inside, never one on a side), the highest first;
            // heights within 1 cm a tie, a pole's end first, then the one nearest the sheet point's spot (Rules.TarpClaim, B-B2).
            foreach (var g in fixes.GroupBy(x => x.k))
            {
                var by = g.ToList(); var spot = P2(flats[g.Key]);
                int w = Rules.TarpClaim(by.Select(x => (x.h, x.end, (P2(x.at) - spot).magnitude)).ToList());
                if (w < 0) continue;
                L.points[g.Key] = by[w].at + up * PoleCap; L.pinned[g.Key] = true;
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

        // ---- Wood poles holding tarps up (user, 2026-10-12; as built in CANVAS.md) ----
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
            if (!pole || !pole.sold || !pole.nailed || PoleCarry.Riding(pole) || !pole.itemRigidbodyC || (onShip ? pole.currentActualBoat != frame : pole.currentActualBoat)) return null;
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
            var T = new Tears { rnd = new System.Random(Rules.TearSeed(sides)) }; var rnd = T.rnd; float R() => (float)rnd.NextDouble();
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
        // use it) a torn tarp keeps its whole cloth and gets its own texture: the canvas as on a whole tarp (the strip stretched once),
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
        // packs: the stall's shop area and the bundle's end and turn in the area's frame. Not at Gold Rock City, Dragon Cliffs, Fort
        // Aestrin and Kicia Bay since 2026-10-24 (the user: "Remove wood poles that sold in these port to sell here instead"): their
        // pole bundles are sold by the wood vendors (WoodVendors).
        internal static readonly Stock[] PoleStocks = {
            new Stock { port = "Al'Nilem", scene = 2, area = "shop area (3)", areaInRoot = new Vector3(269.461f, 15.300f, 54.128f), local = new Vector3(-0.176211089f, -0.353755534f, -0.316438049f), turn = new Quaternion(-0.438141555f, 0.56051445f, -0.470327467f, -0.5221568f) },   // leaning 10 deg over the top edge of market_stall (3), facing outward, resting 0.90 m up, its foot on Terrain (no renderer on it) 0.70 m from the pack
            new Stock { port = "Neverdin", scene = 3, area = "shop area (2)", areaInRoot = new Vector3(1.060f, 15.350f, 7.100f), local = new Vector3(0.0790600851f, -0.385084838f, -0.313387871f), turn = new Quaternion(-0.0148007022f, -0.73526746f, 0.016064465f, -0.6774252f) },   // lying on the ground in front of the pack, along the counter, 0.68 m from the pack's middle, on Terrain, 2.5 deg off level, at most 0.6 cm over the ground (2026-10-24, run polespots-neverdin-5; the lean against market_stall (3) rested on its canvas's edge: the user, "it's now lean on the air")
            new Stock { port = "New Port", scene = 12, area = "shop  supplies", areaInRoot = new Vector3(280.923f, 3.949f, -308.519f), local = new Vector3(-0.1638691f, -0.447944134f, 0.335214168f), turn = new Quaternion(0.608724952f, -0.008688566f, 0.00666696f, 0.7933059f) },   // leaning 15 deg over the top edge of east_market_stall 1, facing outward, resting 0.85 m up, its foot on east_house_platform (no renderer on it) 0.44 m from the oar's foot (1.67 m from the pack)
            new Stock { port = "Sage Hills", scene = 13, area = "shop area", areaInRoot = new Vector3(-47.422f, 4.360f, 24.670f), local = new Vector3(-0.2353538f, -0.291307747f, 0.154015735f), turn = new Quaternion(0.608746052f, -0.005651837f, 0.00433681626f, 0.7933333f) },   // leaning 15 deg over the top edge of east_market_stall 1 (2), facing outward, resting 0.83 m up, its foot on east_house_platform (3) (no renderer on it) 0.86 m from the pack
            new Stock { port = "Sunspire", scene = 16, area = "shop area (2)", areaInRoot = new Vector3(-332.379f, 2.530f, -379.117f), local = new Vector3(0.09982989f, -0.181111723f, 0.00611003442f), turn = new Quaternion(-0.000875937869f, 0.696486056f, 0.0008502014f, 0.7175693f) },   // lying on the ground in front of the pack, along the counter, 0.39 m from the pack's middle (shifted 0.0 m along, 0.00 m out), on Terrain (no renderer on it), 0.1 deg off level, at most 0.9 cm over the ground
            new Stock { port = "Oasis", scene = 20, area = "shop area (3)", areaInRoot = new Vector3(72.260f, 18.000f, -141.949f), local = new Vector3(0.0424064137f, -0.219553858f, 0.120945156f), turn = new Quaternion(0.642751038f, 0.008169531f, -0.006855039f, 0.7660009f) },   // leaning 10 deg over the top edge of market_stall, facing outward, resting 0.85 m up, its foot on Terrain (no renderer on it) 0.70 m from the pack
            new Stock { port = "Siren Song", scene = 21, area = "shop area (4)", areaInRoot = new Vector3(27.930f, 3.420f, 31.820f), local = new Vector3(-0.16582343f, -0.249237821f, -0.0236135013f), turn = new Quaternion(0f, 0.99988693f, 0f, -0.0150402347f) },   // lying on the ground in front of the pack, along the counter, 0.40 m from the pack's middle (shifted 0.0 m along, 0.00 m out), on stone dock (1) (no renderer on it), 0.0 deg off level, at most 0.0 cm over the ground
            new Stock { port = "Chronos", scene = 25, area = "shop area", areaInRoot = new Vector3(552.125f, 8.078f, 439.258f), local = new Vector3(0.181523189f, -0.299461573f, -0.163973868f), turn = new Quaternion(0.00138198957f, 0.7066293f, -0.00138013f, 0.7075814f) },   // lying on the ground in front of the pack, along the counter, 0.44 m from the pack's middle (shifted -0.1 m along, 0.00 m out), on Terrain (no renderer on it), 0.2 deg off level, at most 1.6 cm over the ground
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
            if (Uninstall.Prepared) return;
            int scene = area.gameObject.scene.buildIndex; var root = area.transform.root;
            foreach (var s in table)
            {
                if (s.scene != scene || area.name != s.area || Vector3.Distance(root.InverseTransformPoint(area.transform.position), s.areaInRoot) > .5f) continue;
                // Under an unscaled parent that moves with the island (an item takes its slot's scale).
                var parent = area.transform.parent && Mathf.Abs(area.transform.parent.lossyScale.x - 1) < 1e-3f && Mathf.Abs(area.transform.parent.lossyScale.y - 1) < 1e-3f ? area.transform.parent : root;
                var slot = new GameObject(name); slot.transform.SetParent(parent, false); WoodPlanks.ScaleOne(slot.transform);
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
            // Also the tarps rebuilt after a load under a ship the game had already switched off far away
            // (BoatHorizonPerformanceSwitcher deactivates her): no Awake yet, so not in Tarp.All, and once dropped from this
            // list they were lost at the next save (Claude's fix, 2026-10-17). Only inactive ones: a tarp just replaced or untied
            // is still active until the frame ends.
            var tarps = Tarp.All.Concat(Resources.FindObjectsOfTypeAll<Tarp>().Where(x => x && x.gameObject.scene.IsValid() && !x.gameObject.activeInHierarchy)).Where(x => x).Distinct();
            foreach (var t in tarps)
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
        // A new game, a load or the uninstall button: no tarps from before, and no corners waiting. Also the tarps under a ship
        // switched off far away (not in Tarp.All until she is switched on; Records saves them too): one left by the uninstall
        // could give a new pack (item 642) into a save meant to be vanilla-safe (the review of 2026-10-18, Claude's fix).
        internal static void Clear()
        {
            Cancel(false); waiting = null;
            foreach (var t in Tarp.All.Concat(Resources.FindObjectsOfTypeAll<Tarp>().Where(x => x && x.gameObject.scene.IsValid())).Distinct().ToList())
                if (t) UnityEngine.Object.Destroy(t.gameObject);
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

        // A waiting corner: the ghost knot (a knot's size, the targeter's look) on the part, moving with her ship or the world.
        private static GameObject Knot(Corner c)
        {
            var o = new GameObject("canvas corner"); o.transform.SetParent(c.onShip ? c.frame : FloatingOriginManager.instance.transform, false);
            o.transform.SetPositionAndRotation(c.Position, c.Turn);
            KnotLook(o.transform, GhostSize, GhostMaterial, KnotLookName);
            return o;
        }
        // ---- Knots (user, 2026-10-06) ----
        // A corner's knot is the game's mooring-line knot ("paint_att_Rope_Att", in its "paint att paint"), 0.15 m across; the
        // ghost that shows where a corner goes is the same knot at the same size (the user, 2026-10-17; 0.5 m until then), in the
        // game's targeter green (the game's targeter draws a mesh only at its own size, so the mod draws it). Without the game's
        // knot, a rope ring of the mod's own.
        internal const float KnotSize = .15f, GhostSize = KnotSize; internal const string KnotLookName = "canvas knot look";
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

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
    // ======== Visible traders (stage 4; in VisibleTraders.cs until 2026-10-20, then in NewTraders.cs; here again since 2026-11-09, at the user's word: "VisibleTrader.cs: holds the visible traders with the reputation code merged") ========

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
        // trader copy by 1"), the stock Sanbuq, Brig and Junk 2 each, the Baghlah and Jong 1; the four small plans 2 each, Al'Ankh's
        // medium plan 2 since 2026-10-24 (its two medium locals, the user: "add 1 more copy for local medium"), every other plan 1
        // (the two heavies share theirs); the stock Dhow 5 since 2026-11-04 (Al'Ankh's eighth small local on it, the user: "use 5"); 46 in
        // all (45 before, 44 before that, 47 before that, 41 before that, by hull).
        public static int TraderPool(string key)
        {
            if (key == null) return 0;
            if (key.StartsWith(StockKey)) { int.TryParse(key.Substring(StockKey.Length), out int hull); return hull == 10 ? 5 : HullClass(hull) == 0 ? 4 : HullClass(hull) == 2 ? 1 : 2; }
            return key.StartsWith("small-") || key == "ffl-small" || key == "medium-alankh" ? 2 : 1;
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
        // The outfit a trader drawn now wears, by its index in `kinds` (each outfit's style); `worn`: the outfits the other drawn
        // traders wear ("H3: No same outfit drawn at same time"). Its style's not worn; else a generic one not worn (the user: "if
        // regional outfit is running out because too many trader drawn, it can pick from general one to keep it still look different
        // from others"); else any not worn (Claude's choice); only with every outfit worn, any. An independent: any not worn, else any.
        // roll in [0, 1) picks among them; -1 when there is no outfit.
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
        // there): the Happy Bay trader, the shuttle, each region's medium locals (Al'Ankh's two share theirs), the two heavies (one
        // plan), the FFL small pair (one plan), the 6th and 7th by id of each region's small locals (smallPlace 5 and 6 from the first:
        // Al'Ankh's eighth, the highest id in a new game and on a save it was added to, sails the stock rig; the user, 2026-11-04: "default
        // rigged"), the Chronos trader of Fort Aestrin and the nine independents, three plans a hull by their rank. null: the hull's
        // stock rig.
        public static readonly string[] RegionNames = { "alankh", "emerald", "aestrin" };
        public static string TraderPlan(string group, int home, bool ffl, int independentRank, int smallPlace)
        {
            string region = home >= 0 && home < 3 ? RegionNames[home] : null;
            switch (group)
            {
                case "small": return ffl ? "ffl-small" : region != null && (smallPlace == 5 || smallPlace == 6) ? "small-" + region : null;
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
        // A drawn trader's sails trimmed by the wind it feels (TRADER_TRIM_DRAFT.md, "The self-trim design", approved by the user on
        // 2026-10-23; it replaces the fixed angles: fore-and-aft sails 25 degrees out and jibs 30, of 2026-10-20 and -22). Angles in
        // degrees; awa the apparent wind's angle off the bow, 0 dead ahead to 180 dead astern. Fore-and-aft sails (lateens, gaffs,
        // junk sails) and jibs out to leeward by half the awa (the user's "C: half the AWA"), at least the angle the game lets the sail
        // be sheeted in to (T2 "as vanilla": a fore-and-aft sail to the centreline, a jib 16), at most 80 (T1 (b): past the sail's
        // own range where that is smaller).
        public const double TrimMost = 80;
        public static double TrimOut(double awa, double sheetedIn) => Clamp(Math.Abs(awa) / 2, sheetedIn, TrimMost);
        // The game's square junk sails (prefabs 27-29, 100-103: square sails to the game, a SquareAngleMaster's yard) trimmed as
        // fore-and-aft sails on the traders (the user, 2026-10-24: "Junk sqaure will be trim like fore-and-aft sail for trader";
        // PALLET_JUNK_CREW_DRAFT.md §1): the yard turned 90 degrees from squared to lie along the centreline, eased out by TrimOut,
        // sheeted in to 0 (J2: "0, like other fore and aft sail"), posed past its range like the others (J1 (a)), never furled at sea.
        public static bool JunkSquare(int prefab) => prefab >= 27 && prefab <= 29 || prefab >= 100 && prefab <= 103;
        // The push onto a sail's own face, as a share of the game's push (WindCloth: the wind x 0.25 x unroll x shadow): the part that
        // fills a sail the wind meets at `attack` degrees (its sine), at least 0.3 (the user's "Game's fill, min 0.3").
        public const double PushLeast = .3;
        public static double PushShare(double attack) => Math.Max(PushLeast, Math.Sin(Clamp(attack, -90, 90) * Math.PI / 180));
        // Goosewing (T1: "when AWA is 170-190 degree, make it goosewing"), at sea only, with a buffer against small wind changes (the
        // user: "have buffer so small wind change don't keep fliping sail to other side"; Claude's reading 4): on once the awa has
        // stayed 170 or more for 7 s, off once it has stayed under 165 for 7 s; heldFor: how long GooseTurns has held.
        public const double TrimHold = 7, GooseOn = 170, GooseOff = 165;
        public static bool GooseTurns(bool on, double awa) => on ? Math.Abs(awa) < GooseOff : Math.Abs(awa) >= GooseOn;
        public static bool GooseHeld(bool on, double awa, double heldFor, bool atSea) => atSea && (GooseTurns(on, awa) && heldFor >= TrimHold ? !on : on);
        // In the split (reading 1, "Alternate from aft"): the fore-and-aft sails and jibs by their order from aft (0 the aftmost, the
        // jibs the foremost), the aftmost on the side the sails were on, the next on the other, and so on.
        public static int GooseSide(int order, int side) => order % 2 == 0 ? side : -side;
        // Square sails (the user: "Sqaure sail can brace up to 70 degree", "70°, set from AWA 36"; reading 6): the yard braced to
        // leeward until the wind meets it at 16 degrees (full force by the game's rule, Sail.UpdateWindForceOnSail), 0 (squared) to
        // 70; the wind meets it at the awa + the brace - 90.
        public const double BraceMost = 70, SquareSetAt = 36, SquareFurlUnder = 33;
        // A trader whose every sail is a square sail (today the plan small-emerald, a Kakam with one square sail; the user,
        // 2026-10-26: "keep the square set, can be braced at 80 degree, never furled."): braced to 80 at most, set at sea and on the
        // harbour legs whatever the awa, and waiting at the approach point; else it sailed upwind with bare masts. Furled at a berth
        // or the anchorage like every trader (the user, 2026-11-09: "kakam's special trader should furl their sail when they are in
        // berth and anchorage", replacing "no kakam square trader shouldn't furl in port also" there: SailsOut).
        public const double AllSquareBraceMost = 80;
        public static double SquareBraceMost(bool allSquare) => allSquare ? AllSquareBraceMost : BraceMost;
        public static double SquareBrace(double awa, bool allSquare = false) => Clamp(106 - Math.Abs(awa), 0, SquareBraceMost(allSquare));
        public static double SquareAttack(double awa, double brace) => Math.Abs(awa) + brace - 90;
        // At sea a square sail sets once the awa has stayed 36 or more for 7 s and furls once it has stayed under 33 for 7 s (T5
        // "held longer"; "hold time change to 7 sec."); off the sea it lies furled (the harbour legs: reading 2, "in harbour leg just
        // let sqaure sail stay furled, unfurled when at approaching point"); leaving, at the approach point it sets at once if the awa
        // is 36 or more there (reading 2b).
        public static bool SquareTurns(bool set, double awa) => set ? Math.Abs(awa) < SquareFurlUnder : Math.Abs(awa) >= SquareSetAt;
        public static bool SquareSet(bool set, double awa, double heldFor, bool atSea) => atSea && (SquareTurns(set, awa) && heldFor >= TrimHold ? !set : set);
        public static bool SquareAtApproach(double awa) => Math.Abs(awa) >= SquareSetAt;
        // allSquare: every sail of the trader a square sail (a junk square is a fore-and-aft sail here): its square sails never
        // furled by the awa, at sea or on the harbour legs (AllSquareBraceMost); in port it furls them all with SailsOut
        public static bool SquareSet(bool set, double awa, double heldFor, bool atSea, bool allSquare) => allSquare || SquareSet(set, awa, heldFor, atSea);
        public static bool AllSquare(IEnumerable<int> kinds) => kinds.Any() && kinds.All(k => k == 3);
        // Whether a trader's sails are out (VisibleTraders.Sails; the furl and set at FurlRate). underWay: sailing, or on a harbour leg
        // not held; berthed: at a berth or the anchorage. Every trader sets them under way and furls them otherwise (the user's rule);
        // one with square sails only keeps them set waiting too, and furls them at a berth or the anchorage (the user, 2026-11-09).
        public static bool SailsOut(bool allSquare, bool underWay, bool berthed) => underWay || allSquare && !berthed;
        // MoreSailwindSails' Fisherman sails (its Flying Sail, index 400, and Staysails Mk.A-C, 401-403; sailName "Fisherman's ..."): skinned cloths
        // whose every bone its own scripts place each frame, which a trader copy doesn't keep, so drawn set they crumple (the user's picture,
        // 2026-11-09, TRADE_OFFICE_FLEET_DRAWING.md, last section). Refused for the trade fleet (the user, 2026-11-09: "refuse sail from this mod to be
        // admit to trader fleet."): OfficeFleetApi.SailRefusal, OfficeFleet.Add, and a fleet rig with one drawn on its hull's stock rig (TraderRigs.Check).
        public static bool FleetSailRefused(string sailName) => sailName != null && sailName.StartsWith("Fisherman's ", StringComparison.Ordinal);
        // its refusal, as Shipwright and Transport shows it on its scroll (24 characters a line, 2 lines)
        public const string FleetSailRefusal = "Fisherman's sails can't\njoin the trade fleet.";
        // The side a sail lies on (T7 "as vanilla", reading 3, the buffer of reading 4). wind: the apparent wind's angle off the bow,
        // + over the port side (the sails out to starboard: side +1), - over the starboard side; line: the angle the sail lies at
        // sheeted in (0 a fore-and-aft sail, 16 a jib). It goes over once the wind has come 5 degrees or more onto its own side, past
        // its line toward the bow and past dead astern toward the stern, and stayed so for 7 s (heldFor: how long SidePast has held).
        public const double SidePastBy = 5;
        public static bool SidePast(int side, double wind, double line) { double onItsSide = -side * wind; return side != 0 && onItsSide >= line + SidePastBy && onItsSide <= 180 - SidePastBy; }
        public static int SailSideHeld(int side, double wind, double line, double heldFor) => SidePast(side, wind, line) && heldFor >= TrimHold ? -side : side;
        public static int Leeward(double wind) => wind >= 0 ? 1 : -1;
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
        // Vector2.MoveTowards in double (a trader's place, VisibleTraders.Pose; the 4x stall): from (x, z) toward (tx, tz) by at most step, onto
        // it when nearer.
        public static void StepToward(double x, double z, double tx, double tz, double step, out double rx, out double rz)
        {
            double dx = tx - x, dz = tz - z, d = Math.Sqrt(dx * dx + dz * dz);
            if (d <= step || d == 0) { rx = tx; rz = tz; return; }
            rx = x + dx / d * step; rz = z + dz / d * step;
        }
        // Not used by Pose since 2026-11-08 (a trader's place is kept in double there: the 4x stall); kept for its test. Before:
        // its place is kept as floats, which round x and z each its own way (in 3.9 mm steps 33-66 km from the world's origin; Sage
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
        // The anchorage's places by hull size (ANCHORAGE_SPREAD_DRAFT.md; the user, 2026-11-08: "6 place, also implemet a and b"). Every
        // hull lies bow into the wind; a place is its centre, x across the wind and y along it from the anchorage's centre (the frame
        // turns with the wind, so places never meet as it turns). Each hull's corners within the circle (its radius + AnchorTolerance:
        // the data's radius is rounded to 0.1 m), AnchorGap between hulls. A new hull takes, of the places beside every hull there and
        // at the circle's edge, the one farthest across first (the circle's sides inward), then farthest along, left and aft first
        // (chosen in a prototype of six orders for the most hulls of mixed sizes; three medium hulls lie in today's line). As built, a
        // hull alone (the host test AnchoragePlaceRules' log, 2026-11-09): the 20.5 m circle holds 3 Brigs, 4 Sanbuqs, Junks or Cogs,
        // 6 Kakams, 8 Dhows, 2 Baghlahs or Jongs; the 34.2 m one 8 Brigs, 12 Sanbuqs or Junks, 6 Baghlahs or Jongs (the prototype
        // put 8 Kakams in the 20.5 m circle; this packing holds 6). placed: {x, y, half length, half beam} of each hull
        // holding a place; blocked: a place a player's ship holds. False when none is free.
        public const double AnchorGap = 4, AnchorTolerance = .1;
        public static bool AnchorPlace(double radius, IList<double[]> placed, double length, double beam, Func<double, double, bool> blocked, out double x, out double y)
        {
            double hl = length / 2, hb = beam / 2, r2 = (radius + AnchorTolerance) * (radius + AnchorTolerance);
            var xs = new HashSet<double> { 0 }; var ys = new HashSet<double> { 0 };
            foreach (var p in placed)
            {
                xs.Add(p[0]); xs.Add(p[0] + p[3] + hb + AnchorGap); xs.Add(p[0] - p[3] - hb - AnchorGap);
                ys.Add(p[1]); ys.Add(p[1] + p[2] + hl + AnchorGap); ys.Add(p[1] - p[2] - hl - AnchorGap); ys.Add(p[1] + p[2] - hl); ys.Add(p[1] - p[2] + hl);
            }
            // the circle's edge: across for each row, then along for each column
            foreach (double v in ys.ToList()) { double s = r2 - (Math.Abs(v) + hl) * (Math.Abs(v) + hl); if (s > 0 && Math.Sqrt(s) >= hb) { xs.Add(Math.Sqrt(s) - hb); xs.Add(hb - Math.Sqrt(s)); } }
            foreach (double v in xs.ToList()) { double s = r2 - (Math.Abs(v) + hb) * (Math.Abs(v) + hb); if (s > 0 && Math.Sqrt(s) >= hl) { ys.Add(Math.Sqrt(s) - hl); ys.Add(hl - Math.Sqrt(s)); } }
            bool found = false; double bx = 0, by = 0, bw = 0;
            foreach (double cx in xs)
                foreach (double cy in ys)
                {
                    if ((Math.Abs(cx) + hb) * (Math.Abs(cx) + hb) + (Math.Abs(cy) + hl) * (Math.Abs(cy) + hl) > r2) continue;
                    bool clear = true;
                    foreach (var p in placed) if (Math.Abs(cx - p[0]) < hb + p[3] + AnchorGap - 1e-6 && Math.Abs(cy - p[1]) < hl + p[2] + AnchorGap - 1e-6) { clear = false; break; }
                    if (!clear) continue;
                    double w = Math.Round(Math.Abs(cx) + hb, 3);
                    bool better = !found || w > bw || w == bw && (cx < bx || cx == bx && (Math.Abs(cy) > Math.Abs(by) || Math.Abs(cy) == Math.Abs(by) && cy < by));
                    if (!better || blocked != null && blocked(cx, cy)) continue;
                    found = true; bx = cx; by = cy; bw = w;
                }
            x = bx; y = by; return found;
        }
        // Whether a hull fits a given place: inside the circle (the same allowance) and AnchorGap from every hull in placed (a trader
        // that shared a place takes it over only so: the place was found for its holder's hull, anch-2).
        public static bool AnchorFits(double radius, IList<double[]> placed, double length, double beam, double x, double y)
        {
            double hl = length / 2, hb = beam / 2, r = radius + AnchorTolerance;
            if ((Math.Abs(x) + hb) * (Math.Abs(x) + hb) + (Math.Abs(y) + hl) * (Math.Abs(y) + hl) > r * r) return false;
            foreach (var p in placed) if (Math.Abs(x - p[0]) < hb + p[3] + AnchorGap - 1e-6 && Math.Abs(y - p[1]) < hl + p[2] + AnchorGap - 1e-6) return false;
            return true;
        }
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
        // (one counting table kept and cleared, read by index: it runs for each drawn trader at every scan, and nothing in it calls out)
        private static readonly Dictionary<int, int> mainCount = new Dictionary<int, int>();
        public static int MainGood(IList<int> goods)
        {
            if (goods == null || goods.Count == 0) return 0;
            var count = mainCount; count.Clear(); int best = 0, most = 0;
            for (int i = 0; i < goods.Count; i++) { int g = goods[i]; count[g] = count.TryGetValue(g, out int n) ? n + 1 : 1; }
            for (int i = 0; i < goods.Count; i++) { int g = goods[i]; if (count[g] > most) { most = count[g]; best = g; } }
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
        // Scrambled Seas (SCRAMBLED_TRADERS_DRAFT.md section 2): a harbour way moved with its island, its room and water records kept.
        internal LanePath Moved(Vector2 by) => new LanePath(Points.Select(p => p + by)) { room = room, roomStep = roomStep, roomFromEnd = roomFromEnd, left = left, right = right, widthStep = widthStep, widthFromEnd = widthFromEnd };
        // Its part from s0 to s1 (s1 < s0: walked backwards), then the points of `more` (new water: wide), as one way; its room and
        // water records sampled again along it in its own direction, the new part's as wide water.
        internal const float WideRoom = 999f;
        internal LanePath Part(float s0, float s1, IList<Vector2> more)
        {
            var pts = new List<Vector2> { At(s0) };
            if (s1 >= s0) { for (int i = 0; i < Points.Length; i++) if (Along[i] > s0 && Along[i] < s1) pts.Add(Points[i]); }
            else for (int i = Points.Length - 1; i >= 0; i--) if (Along[i] < s0 && Along[i] > s1) pts.Add(Points[i]);
            pts.Add(At(s1));
            if (more != null) pts.AddRange(more);
            var p = new LanePath(pts); float kept = Math.Abs(s1 - s0), dir = s1 >= s0 ? 1f : -1f;
            if (room != null && roomStep > 0)
            {
                var r = new float[Mathf.FloorToInt(p.Length / roomStep) + 1];
                for (int k = 0; k < r.Length; k++) r[k] = k * roomStep <= kept ? RoomAt(s0 + dir * k * roomStep) : WideRoom;
                p.SetRoom(roomStep, r);
            }
            if (left != null && widthStep > 0)
            {
                int n = Mathf.FloorToInt(p.Length / widthStep) + 1; var l = new float[n]; var rr = new float[n];
                for (int k = 0; k < n; k++)
                {
                    if (k * widthStep > kept) { l[k] = rr[k] = WideWater; continue; }
                    WidthAt(s0 + dir * k * widthStep, out float a, out float b);
                    if (dir > 0) { l[k] = a; rr[k] = b; } else { l[k] = b; rr[k] = a; }   // walked backwards its sides swap
                }
                p.SetWidth(widthStep, l, rr);
            }
            return p;
        }
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
        // At from an exact distance, the point worked out in double (a trader's aim on a lane over 1,000 km, where a float s and a float
        // point both move in steps: the 4x stall, VisibleTraders.Pose).
        internal void AtExact(double s, out double x, out double z)
        {
            if (s <= 0) { x = Points[0].x; z = Points[0].y; return; }
            if (s >= Length) { var e = Points[Points.Length - 1]; x = e.x; z = e.y; return; }
            int i = Math.Min(Segment((float)s), Points.Length - 2); double span = (double)Along[i + 1] - Along[i];
            if (span <= 0) { x = Points[i].x; z = Points[i].y; return; }
            double t = Math.Max(0, Math.Min(1, (s - Along[i]) / span));
            x = Points[i].x + ((double)Points[i + 1].x - Points[i].x) * t; z = Points[i].y + ((double)Points[i + 1].y - Points[i].y) * t;
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
        // margin: from the ship when getting round it (at 7-10 10 m where the water allows, else today's: Rules.PassOffset); spare: the
        // single-file rule's (at 7-10 10 m; TRADER_REPUTATION_DRAFT.md 5.1, R18)
        internal static WayLook Look(LanePath leg, float from, bool placeAtEnd, IList<HullBox> ships, float length, float beam, float offset, double margin = Rules.WayMargin, double spare = Rules.SingleFileSpare)
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
                    if (single == null) single = SingleFileNear(leg, placeAtEnd, length, beam, (float)(2 * h.B), spare);
                    bool inSingle = single[Mathf.Min(i, single.Length - 1)];
                    double off = double.NaN;
                    if (!inSingle)
                    {
                        Rules.PortOffsets(placeAtEnd ? leg.Length - s : s, l, r, beam / 2, out double min, out double max);
                        off = Rules.PassOffset(min, max, lo, hi, beam / 2, offset, margin);
                    }
                    if (inSingle || double.IsNaN(off))
                    {
                        if (s < w.At) { w.At = s; w.Blocked = true; w.SingleFile = inSingle; w.StretchStart = inSingle ? StretchStart(leg, placeAtEnd, beam, (float)(2 * h.B), s, length, spare) : -1; }
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
        private static bool[] SingleFileNear(LanePath leg, bool placeAtEnd, float length, float beam, float shipBeam, double spare = Rules.SingleFileSpare)
        {
            int n = Mathf.FloorToInt(leg.Length / Step) + 2; var raw = new bool[n]; var near = new bool[n];
            for (int i = 0; i < n; i++) raw[i] = Narrow(leg, placeAtEnd, Mathf.Min(i * Step, leg.Length), beam, shipBeam, spare);
            int k = Mathf.CeilToInt(length / Step);
            for (int i = 0; i < n; i++) if (raw[i]) for (int j = Mathf.Max(0, i - k); j <= Mathf.Min(n - 1, i + k); j++) near[j] = true;
            return near;
        }
        private static bool Narrow(LanePath leg, bool placeAtEnd, float s, float beam, float shipBeam, double spare = Rules.SingleFileSpare)
        {
            if ((placeAtEnd ? leg.Length - s : s) < PlaceSkip) return false;
            leg.WidthAt(s, out float l, out float r);
            return Rules.SingleFile(l + r, beam, shipBeam, spare);
        }
        // Where the single-file stretch met at s starts along the leg: the narrow place nearest s within the trader's length (in whole
        // steps, as SingleFileNear widens the stretches), then back to the first of its run.
        internal static float StretchStart(LanePath leg, bool placeAtEnd, float beam, float shipBeam, float s, float length, double spare = Rules.SingleFileSpare)
        {
            int i0 = Mathf.RoundToInt(s / Step), k = Mathf.CeilToInt(length / Step), last = Mathf.FloorToInt(leg.Length / Step) + 1, found = -1;
            for (int d = 0; d <= k && found < 0; d++)
                foreach (int j in new[] { i0 - d, i0 + d })
                    if (j >= 0 && j <= last && Narrow(leg, placeAtEnd, Mathf.Min(j * Step, leg.Length), beam, shipBeam, spare)) { found = j; break; }
            if (found < 0) return s;
            while (found - 1 >= 0 && Narrow(leg, placeAtEnd, (found - 1) * Step, beam, shipBeam, spare)) found--;
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
        // A Scrambled Seas map's harbours and lanes (ScrambledMap, at this file's end), in place of the vanilla ones while that game is played; null
        // on the vanilla map (VisibleTraders sets it at its first scan after each load).
        private static ScrambledMap scrambled;
        internal static ScrambledMap Scrambled => scrambled;
        internal static void Use(ScrambledMap map) => scrambled = map;
        internal static IReadOnlyDictionary<int, TraderHarbour> VanillaHarbours => harbours;
        internal static IEnumerable<TraderHarbour> Harbours => scrambled != null ? scrambled.Harbours.Values : harbours.Values;
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
        internal static TraderHarbour HarbourOf(int port) => scrambled != null ? scrambled.HarbourOf(port) : harbours.TryGetValue(port, out var h) ? h : null;
        // Inside the lagoon a lane is 40 m wide, at sea 120 m (the user, 2026-10-17, question 3 (b)): the half-width traders spread over.
        // On a scrambled map the lagoon ring is gone: 40 m within 800 m of a lagoon port (ScrambledMap.HalfWidth).
        internal static float HalfWidth(Vector2 p) => scrambled != null ? scrambled.HalfWidth(p) : InLagoon(p) ? 20f : 60f;
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
            // a scrambled map: one lane per pair of groups (the user, 2026-11-02); a saved lane is read modulo 1
            if (scrambled != null) return scrambled.Choices(from, to);
            if (!group.TryGetValue(from, out string gf) || !group.TryGetValue(to, out string gt) || !harbours.ContainsKey(from) || !harbours.ContainsKey(to)) return 0;
            if (regionOf[gf] == regionOf[gt]) return 1;
            if (!exit.TryGetValue(gf + ">" + gt, out string nf) || !exit.TryGetValue(gt + ">" + gf, out string nt)) return 0;
            return InterBetween(nf, nt, out _)?.Count ?? 0;
        }
        // A trip's lanes, from the origin's approach point to the destination's (the shortest way over the region's graph; between
        // regions to its departure area, one of the pair's lanes, on through the other region's graph). Null when there is none.
        internal static LanePath Trip(int from, int to, int lane)
        {
            if (scrambled != null) return scrambled.Trip(from, to);
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
    // Shipyard Expansion's own settings for the sail (a trade fleet ship's lines after its sail line, OfficeFleetApi.RigOf; FleetCompat.PutSails):
    // Texture its texture when it isn't its prefab's, else null; Angle its angle when it isn't 0 ("angle"), else NaN; Flipped its jib side ("flipped").
    internal sealed class RigSail { internal int Prefab, Mast, Color; internal float Height, Min, Max, ScaleY, ScaleZ; internal string Texture; internal float Angle = float.NaN; internal bool Flipped; }
    // A sail from another mod on a trade fleet ship (its "modsail" line, OfficeFleetApi.RigOf): its index in PrefabsDirectory.sails, its Sail's
    // sailName (for the player) and its prefab's object name (the sail as it was when the ship joined: a mod's sails sit at fixed indices, and
    // Crystal Isles writes 135 and 136 over whatever is there).
    internal sealed class RigModSail { internal int Index; internal string SailName, Object; }
    // Needs: the plugins (BepInEx GUIDs) a trade fleet ship's rig was read with and can't be put on without (its "needs" lines,
    // OfficeFleetApi.RigOf; OfficeFleet.Loaded removes a ship whose rig needs one that isn't loaded). ModSails: its sails from other mods,
    // removed the same way when one is missing (TraderRigs.Missing).
    internal sealed class RigPlan { internal string Name; internal int Hull; internal int[] Options = new int[0]; internal int[] Locked = new int[0]; internal readonly List<RigSail> Sails = new List<RigSail>(); internal readonly List<string> Needs = new List<string>(); internal readonly List<RigModSail> ModSails = new List<RigModSail>(); }
    internal static class TraderRigs
    {
        // Sails from index 131 on are other mods' (Shipyard Expansion grows the game's 135 slots to 512 and adds its lug sails at 156-158;
        // Crystal Isles 135-136, Unstayed Junk Sail Mast 200-201 (131-132 before), MoreSailwindSails 400-403); the 20 plans use vanilla sails only
        // (the user, 2026-10-19), a trade fleet ship's rig may hold any (the user, 2026-11-09: "ship should be allowed to add into trade fleet
        // even if it use modded sail and hull option"; Check's fleet).
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
            plans.Clear();
            foreach (var p in Read(lines)) plans[p.Name] = p;
            Loaded = true;
        }
        // The plans of a rig text in order, read without touching the trader plans (a trade fleet ship's rig, OfficeFleetApi.RigOf's text, is
        // read this way: Parse clears the 20 plans).
        internal static List<RigPlan> Read(IEnumerable<string> lines)
        {
            var list = new List<RigPlan>(); RigPlan cur = null;
            foreach (string raw in lines)
            {
                string line = raw.Trim(); if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split(' ');
                if (f[0] == "plan" && f.Length >= 3)
                {
                    cur = new RigPlan { Name = f[1], Hull = int.Parse(f[2]), Options = f.Length > 3 && f[3].Length > 0 ? f[3].Split(',').Select(int.Parse).ToArray() : new int[0],
                        Locked = f.Length > 4 && f[4] != "-" ? f[4].Split(',').Select(int.Parse).ToArray() : new int[0] };
                    list.Add(cur);
                }
                else if (f[0] == "sail" && f.Length >= 9 && cur != null)
                    cur.Sails.Add(new RigSail { Prefab = int.Parse(f[1]), Mast = int.Parse(f[2]), Height = F(f[3]), Min = F(f[4]), Max = F(f[5]), Color = int.Parse(f[6]), ScaleY = F(f[7]), ScaleZ = F(f[8]) });
                else if (f[0] == "needs" && f.Length >= 2 && cur != null) cur.Needs.Add(f[1]);
                // the sail before it: its Shipyard Expansion texture, the rest of the line (its names hold spaces: "dhow small cloth paint")
                else if (f[0] == "texture" && f.Length >= 2 && cur != null && cur.Sails.Count > 0) cur.Sails[cur.Sails.Count - 1].Texture = line.Substring("texture".Length).Trim();
                // and its Shipyard Expansion angle and jib side
                else if (f[0] == "angle" && f.Length >= 2 && cur != null && cur.Sails.Count > 0) cur.Sails[cur.Sails.Count - 1].Angle = F(f[1]);
                else if (f[0] == "flipped" && cur != null && cur.Sails.Count > 0) cur.Sails[cur.Sails.Count - 1].Flipped = true;
                // a sail from another mod: "modsail <index> <sailName>|<object name>" (both names hold spaces)
                else if (f[0] == "modsail" && f.Length >= 3 && cur != null)
                {
                    string rest = line.Substring("modsail".Length).Trim(); int sp = rest.IndexOf(' '); var names = rest.Substring(sp + 1).Split('|');
                    cur.ModSails.Add(new RigModSail { Index = int.Parse(rest.Substring(0, sp)), SailName = names[0].Trim(), Object = names.Length > 1 ? names[1].Trim() : "" });
                }
            }
            return list;
        }
        // The rig text's line for a sail from another mod (OfficeFleetApi.RigOf), from the sail in the game's directory at that index; null for the
        // game's own sails or an empty slot.
        internal static string ModSailLine(int index)
        {
            if (index < FirstModSail) return null;
            var sails = PrefabsDirectory.instance ? PrefabsDirectory.instance.sails : null;
            var go = sails != null && index < sails.Length ? sails[index] : null; var sail = go ? go.GetComponent<Sail>() : null;
            return sail ? "modsail " + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + (sail.sailName ?? "").Replace('|', ' ').Trim() + "|" + go.name : null;
        }
        // Whether that sail is still in this game: its slot holds a Sail of that index with the same object name (Shipyard Expansion's own clean load
        // drops a sail whose slot is empty from a player's ship, so an empty slot means its mod is gone). null: present; else its name for the player.
        internal static string ModSailMissing(RigModSail m)
        {
            var sails = PrefabsDirectory.instance ? PrefabsDirectory.instance.sails : null;
            var go = sails != null && m.Index >= 0 && m.Index < sails.Length ? sails[m.Index] : null; var sail = go ? go.GetComponent<Sail>() : null;
            return sail && sail.prefabIndex == m.Index && go.name == m.Object ? null : "the sail " + (string.IsNullOrEmpty(m.SailName) ? m.Index.ToString() : m.SailName);
        }
        // Shipyard Expansion's GUID: a fleet rig read from a hull it changed needs it (its parts at the end of the hull's list, its options at the end
        // of a part's, its longer masts: the game's LoadData can't put such a rig on the hull without it; Dinghies' davits come after its parts).
        internal const string ShipyardExpansionGuid = "com.nandbrew.shipyardexpansion";
        internal static bool HasPlugin(string guid) => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(guid);
        // The plugins a rig text needs that this game doesn't have, and its other mods' sails that this game doesn't have ("the sail <name>"; none:
        // an empty list).
        internal static List<string> Missing(string rig)
        {
            var missing = new List<string>();
            try
            {
                foreach (var p in Read((rig ?? "").Split('\n')))
                {
                    foreach (var g in p.Needs) if (!HasPlugin(g) && !missing.Contains(g)) missing.Add(g);
                    foreach (var m in p.ModSails) { string gone = ModSailMissing(m); if (gone != null && !missing.Contains(gone)) missing.Add(gone); }
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning("Trade office: a fleet rig not read (" + e.Message + ")."); }
            return missing;
        }
        // Shipyard Expansion changed this ship's hull (its own list of the hulls it patched, Plugin.moddedBoats; Shipwright and Transport adds its
        // copies of them there too, Compat.ShipMods.RegisterWithShipyardExpansion).
        internal static bool ShipyardExpansionChanged(BoatCustomParts parts)
        {
            if (!parts || !HasPlugin(ShipyardExpansionGuid)) return false;
            try { return AccessTools.Field(FleetCompat.TypeOf("ShipyardExpansion.Plugin"), "moddedBoats")?.GetValue(null) is List<BoatCustomParts> modded && modded.Contains(parts); }
            catch (Exception e) { Plugin.Log?.LogWarning("Trade office: Shipyard Expansion's ship list not read (" + e.Message + ")."); return false; }
        }
        // A plan checked against the game as it runs (the build plan, step 2): each part option exists on the hull, each sail's
        // prefab is a vanilla sail (for a trade fleet ship, fleet: any sail in the game's directory, one from another mod the same as when the
        // ship joined), each mast exists. null when it fits, else why not (its traders then keep the stock rig).
        internal static string Check(RigPlan p, BoatCustomParts parts, BoatRefs refs, bool fleet = false)
        {
            if (!parts || !refs) return "the ship has no parts or masts";
            if (p.Options.Length > parts.availableParts.Count) return p.Options.Length + " part options for " + parts.availableParts.Count + " parts";
            for (int i = 0; i < p.Options.Length; i++)
                if (p.Options[i] < 0 || p.Options[i] >= parts.availableParts[i].partOptions.Count) return "part " + i + " has no option " + p.Options[i];
            var sails = PrefabsDirectory.instance ? PrefabsDirectory.instance.sails : null;
            foreach (var s in p.Sails)
            {
                if (s.Prefab < 0 || sails == null || s.Prefab >= sails.Length || !sails[s.Prefab] || !sails[s.Prefab].GetComponent<Sail>()) return "sail " + s.Prefab + " is not in the game";
                if (s.Prefab >= FirstModSail && !fleet) return "sail " + s.Prefab + " is not a vanilla sail";
                // a sail a copy can't draw (Rules.FleetSailRefused): a fleet ship that joined with one before the refusal keeps the stock rig
                if (s.Prefab >= FirstModSail && Rules.FleetSailRefused(sails[s.Prefab].GetComponent<Sail>().sailName)) return "sail " + s.Prefab + " (" + sails[s.Prefab].GetComponent<Sail>().sailName + ") can't be drawn on a trader";
                var mod = s.Prefab >= FirstModSail ? p.ModSails.FirstOrDefault(m => m.Index == s.Prefab) : null;
                if (mod != null && ModSailMissing(mod) != null) return "sail " + s.Prefab + " is not " + mod.Object + " as when the ship joined";
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
            // long bowsprit), until nothing changes; at most a pass per part and one more: two requirements wanting different options
            // of one part not kept would change it back and forth for ever, so the options stay as they are and the clash is logged
            // (the deep review of 2026-11-02, Claude's fix)
            int passes = 0;
            for (bool more = true; more;)
            {
                if (++passes > parts.availableParts.Count + 1) { changed.Add("requirements still changing after " + (passes - 1) + " passes (a clash): options kept as they are"); break; }
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
        // heights: the ship whose masts the added lengths are read from (a trade fleet ship's clone registers a mast only once a part puts it up:
        // the game's ship it was copied from, TraderHulls.FleetJobStep); null: the ship itself. A rig that needs Shipyard Expansion (RigOf's "needs"
        // line) was read from a hull it changed, its heights as the game gave them on its longer masts: put on as they are.
        internal static void PutOn(SaveableBoatCustomization custom, RigPlan p, int[] options, BoatRefs heights = null)
        {
            var refs = heights ? heights : custom.GetComponent<BoatRefs>(); var so = custom.GetComponent<SaveableObject>();
            if (so && GameState.modData != null) GameState.modData.Remove("SEboatSails." + so.sceneIndex);
            var d = Data(p, options);
            if (refs && !p.Needs.Contains(ShipyardExpansionGuid)) foreach (var s in d.sails) if (s.mastIndex >= 0 && s.mastIndex < refs.masts.Length) s.installHeight += MastAdded(refs.masts[s.mastIndex]);
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
                try { seMastHeights = AccessTools.Field(FleetCompat.TypeOf("ShipyardExpansion.Plugin"), "mastHeights")?.GetValue(null) as Dictionary<Mast, float>; }
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

    // A template's sail (TRADER_TRIM_DRAFT.md, the code plan's step 2): its rest pose (as installed: furled in port) and what posing
    // it at any angle needs. Kind: 0 none (no angle control: it stays at rest), 1 fore-and-aft (lateen, gaff, junk sail), 2 jib,
    // 3 square. A pose turns it from Base about Axis (its own frame) by Zero + Star x side x the angle out (a square sail: its
    // brace); Zero its centreline (a square yard squared), Star the turn's sign that puts it out to starboard (the game's force rule
    // picking leeward, as vt_measure run 2). Closest: the angle the game lets it be sheeted in to. Lo, Hi: its range, in the pose's
    // angle. Order: its place from aft among the fore-and-aft sails and jibs, the jibs the foremost (the goosewing order; -1 for the
    // others). Face: its cloth's face in the sail's rotation frame (from the cloth's rest mesh, one side of it), else the game's
    // force axis (its up; a yard's right). Yard: a square yard, turned about its local z from squared with its force axis its
    // right: kind 3, or kind 1 for a junk square trimmed as a fore-and-aft sail (Rules.JunkSquare; its Zero the yard along the
    // centreline, 90 degrees from squared).
    internal sealed class SailRig
    {
        internal int[] Path; internal Transform T; internal Quaternion Rest, Base; internal Vector3 Axis = Vector3.forward, Face = Vector3.up;
        internal int Kind, Order = -1; internal float Zero, Star = 1, Closest, Lo = -89, Hi = 89; internal bool Yard;
        internal Quaternion At(int side, float angle) => Kind == 0 ? Rest : Base * Quaternion.AngleAxis(Zero + Star * side * angle, Axis);
    }
    // A template's flag (or any cloth the game drives by its WindClothSimple, which a copy does not keep): its place, the game's
    // multiplier and what the game drives it by (Drive 0: the wind less its ship's velocity, the ship's flags; 1: its sail's
    // apparent wind, Sail the sail's index in the template; 2: the wind alone). Without it a copy's flag kept the push it had when the
    // template was taken (vttrue-1: the stock Brig's two flags at (18.29, 0, -4.30) at every wind, 13 to 167 degrees off it; the
    // user's report of 2026-10-24, "the sail trim didn't follow wind direction", whose picture shows the flag against the sails).
    internal sealed class FlagRig { internal Transform T; internal int[] Path; internal float Mult = 1; internal int Drive, Sail = -1; }
    internal sealed class HullTemplate
    {
        internal int Hull;
        internal readonly List<FlagRig> Flags = new List<FlagRig>();
        internal string Plan, Key;   // the sail plan (null: the hull's stock rig) and the pool's key (Rules.TraderKey)
        internal bool Fleet;         // a trade fleet ship's own (Plan and Key "fleet-<its id>": TraderHulls' fleet part)
        internal int[] Options;      // a fleet ship's: its clone's part options as LoadData left them, read before the template (for the log and the tests)
        internal GameObject Root;
        internal int Lanterns;   // the lanterns hung on it (the hull's places, a plan's on its own masts: Rules.LanternsOnMasts)
        internal readonly List<SailRig> Sails = new List<SailRig>();
        internal bool AllSquare;   // every sail a square sail (kind 3): set at sea, on the harbour legs and waiting, furled at a berth or the anchorage, braced to 80 (Rules.AllSquare, Rules.SailsOut)
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
        internal Cloth[] SailCloths; internal Renderer[] SailClothLooks;   // each sail's cloth and its renderer, by SailT's order (PoseSails)
        internal Cloth[] FlagCloths = new Cloth[0];   // its flags' cloths, by the template's Flags (VisibleTraders.Trim drives them)
        internal Collider[] Colliders;
        // its ropes (the game's RopeEffect, each with its line; drawn by the game while the copy is shown, TraderHulls.ShowRopes)
        internal RopeEffect[] Ropes = new RopeEffect[0];
        // its helmsman, one object drawing his outfit's one mesh (TraderHulls.Dress; null without outfits), and the outfit he wears while drawn (-1: none chosen)
        internal MeshFilter HelmLook; internal MeshRenderer HelmRenderer; internal int Outfit = -1;
        // its speech bubble while shown (TraderBubble), the time it is gone once shrinking (0 while it shows), its line
        internal GameObject Bubble; internal float BubbleGone; internal int BubbleLine = -1;
        // its kind (TraderBubble.Kind*: a collision line, an apology, a greeting, a close-by line, a berth line) and when it shrinks away by
        // its time (0: only beyond 60 m)
        internal int BubbleKind; internal float BubbleTill;
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
        internal static int PlanTemplates => templates.Values.Count(t => t.Plan != null && !t.Fleet);
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
            internal bool Furled, Junk; internal Material Unfurled; internal int[] ClothPath;
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
                else if (s.GetComponent<SquareAngleMaster>()) { d.Kind = "square"; d.Junk = Rules.JunkSquare(s.prefabIndex) || s.name.IndexOf("square junk", StringComparison.OrdinalIgnoreCase) >= 0; }
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

        // A sail's rig (the self-trim, TRADER_TRIM_DRAFT.md; the rules in Rules.TrimOut and on): a fore-and-aft sail turns about its
        // hinge from the centreline (the turn that lays its force axis, its up, most across the hull: vt_measure run 2's scan), a jib
        // about its hinge from its rest, a square yard about its local z from squared (the game's own SquareAngleMaster turn); the side
        // a turn puts it to leeward by the game's force rule with the wind abeam (as vt_dress).
        private static SailRig Rig(Transform t, SailData d, Vector3 fwd, Vector3 right, bool allSquare)
        {
            var keep = t.localRotation;
            var rig = new SailRig { T = t, Rest = keep, Base = keep };
            if (d.Max > d.Min) { rig.Lo = d.Min; rig.Hi = d.Max; }
            // a junk square: the fore-and-aft rule from the yard along the centreline, sheeted in to 0 (J2) (Rules.JunkSquare)
            if (d.Kind == "square") { var e = t.localEulerAngles; rig.Kind = d.Junk ? 1 : 3; rig.Yard = true; rig.Base = Quaternion.Euler(e.x, e.y, 0f); }
            else if (d.Kind == "angle")
            {
                // Euler(x, y, z + a) = Euler(x, y, z) x a turn of a about the local z
                var cur = t.localEulerAngles; float baseZ = d.InitEuler.sqrMagnitude < 1e-6f ? cur.z : d.InitEuler.z;
                rig.Kind = 1; rig.Base = Quaternion.Euler(cur.x, cur.y, baseZ); rig.Closest = Mathf.Abs(d.Closest);
            }
            else if (d.Kind == "jib")
            {
                rig.Kind = 2; rig.Base = d.InitRot.x == 0 && d.InitRot.y == 0 && d.InitRot.z == 0 && d.InitRot.w == 0 ? keep : d.InitRot; rig.Axis = d.Axis;
                rig.Closest = float.IsNaN(d.CloseL) ? 16f : Mathf.Abs(d.CloseL);
            }
            rig.Face = rig.Yard ? Vector3.right : Vector3.up;
            if (rig.Kind == 0) return rig;
            Vector3 Force() => rig.Yard ? t.right : t.up;
            if (rig.Kind != 3)
            {
                // a junk square's centreline is 90 degrees from squared, past a -35..35 yard's range (J1 (a): posed there anyway)
                float across = -1f, from = rig.Yard ? -90f : rig.Lo, to = rig.Yard ? 90f : rig.Hi;
                for (float a = from; a <= to; a += .5f)
                {
                    t.localRotation = rig.Base * Quaternion.AngleAxis(a, rig.Axis);
                    var n = Force(); n.y = 0; if (n.magnitude < .3f) continue;
                    float x = Mathf.Abs(Vector3.Dot(n.normalized, right));
                    if (x > across) { across = x; rig.Zero = a; }
                }
            }
            float bestDrive = float.MinValue;
            foreach (float s in new[] { 1f, -1f })
            {
                t.localRotation = rig.Base * Quaternion.AngleAxis(rig.Zero + s * 30f, rig.Axis);
                var n = Force(); n.y = 0; n = n.normalized; if (Vector3.Dot(n, right) < 0) n = -n;   // the wind abeam over the port side
                float drive = Vector3.Dot(n, fwd);
                if (drive > bestDrive) { bestDrive = drive; rig.Star = s; }
            }
            t.localRotation = keep;
            // the most each way it is posed out (80 a fore-and-aft sail or jib, a yard 70: the user, T1 (b) and "brace up to 70"; a
            // yard 80 on a trader with square sails only, Rules.AllSquareBraceMost), even past its range (the user, 2026-10-19: "Go
            // to the angle anyway"): where the range is smaller the sail is drawn through what it would meet (listed in the report)
            float most = (float)(rig.Kind == 3 ? Rules.SquareBraceMost(allSquare) : Rules.TrimMost);
            foreach (int side in new[] { 1, -1 })
            {
                float a = rig.Zero + rig.Star * side * most;
                if (a < rig.Lo - .01f || a > rig.Hi + .01f) pastLimit.Add(d.Kind + " " + (side > 0 ? "starboard" : "port") + " " + most.ToString("F0") + " (range " + rig.Lo.ToString("F0") + ".." + rig.Hi.ToString("F0") + " about " + rig.Zero.ToString("F0") + ")");
            }
            return rig;
        }
        // A sail's face from its cloth's rest mesh (the probe's fix for double-sided meshes, vtsc-2): the triangles facing the way of
        // the largest one, their summed normal; taken when it is flat enough (its sum at least half their area), turned into the
        // sail's rotation frame. The mesh's points are in its bind space: to the world through a bone and its bind pose (its root
        // bone, which the furl does not move; through the renderer's own transform every face came out 90 degrees off, vttrim-1).
        // The Junk's jib's face lies 66 degrees off its up (vtsc-1).
        private static void ReadFace(SailRig rig, System.Text.StringBuilder why)
        {
            var cl = rig.T.GetComponentInChildren<Cloth>(true); var r = cl ? cl.GetComponent<SkinnedMeshRenderer>() : null; var m = r ? r.sharedMesh : null;
            string fail = !cl ? "no cloth" : !m ? "no mesh" : !m.isReadable ? "mesh not readable" : null;
            if (fail == null)
            {
                var p = m.vertices; var tris = m.triangles;
                Vector3 Tri(int i) => Vector3.Cross(p[tris[i + 1]] - p[tris[i]], p[tris[i + 2]] - p[tris[i]]);
                var refN = Vector3.zero; for (int i = 0; i + 2 < tris.Length; i += 3) { var c = Tri(i); if (c.sqrMagnitude > refN.sqrMagnitude) refN = c; }
                Vector3 sum = Vector3.zero; float area = 0;
                for (int i = 0; i + 2 < tris.Length; i += 3) { var c = Tri(i); if (Vector3.Dot(c, refN) > 0) { sum += c; area += c.magnitude; } }
                float flat = area > 1e-6f ? sum.magnitude / area : 0f;
                if (flat < .5f) fail = "flat " + flat.ToString("F2");
                else
                {
                    var bones = r.bones; var binds = m.bindposes; int b = r.rootBone ? Array.IndexOf(bones, r.rootBone) : -1;
                    if (b < 0 || b >= binds.Length) b = binds.Length > 0 && bones.Length > 0 && bones[0] ? 0 : -1;
                    var toWorld = b >= 0 ? bones[b].localToWorldMatrix * binds[b] : r.transform.localToWorldMatrix;
                    var world = toWorld.inverse.transpose.MultiplyVector(sum).normalized;
                    rig.Face = (Quaternion.Inverse(rig.T.rotation) * world).normalized;
                    var axis = rig.Yard ? rig.T.right : rig.T.up;
                    why.Append(rig.T.name + " " + (Mathf.Acos(Mathf.Min(1f, Mathf.Abs(Vector3.Dot(world, axis)))) * Mathf.Rad2Deg).ToString("F0") + " off its force axis (flat " + flat.ToString("F2") + ", " + (b >= 0 ? "bone " + bones[b].name : "its own transform") + ")");
                    return;
                }
            }
            why.Append(rig.T.name + " its force axis (" + fail + ")");
        }

        private static readonly List<string> pastLimit = new List<string>();   // the trim angles past their sail's range, for the report
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

        private static HullTemplate MakeTemplate(int hull, string plan) => MakeTemplate(Original(hull), hull, plan, Rules.TraderKey(plan, hull), null);
        // From a given ship under a given key: a trade fleet ship's clone (FleetClone), whose wheels' scripts were taken off it (wheels: their places).
        private static HullTemplate MakeTemplate(PurchasableBoat boat, int hull, string plan, string key, IList<Transform> wheels)
        {
            if (!boat) { Plugin.Log?.LogWarning("Visible traders: hull " + hull + " not found in the scene."); return null; }
            var root = boat.transform;
            var sails = ReadSails(boat, hull, plan != null);
            pastLimit.Clear();
            // a plan's hull parts that differ from the stock rig's (the lantern and cargo places were measured on the stock parts)
            string partsDiffer = "";
            if (plan != null && planOptions.TryGetValue(plan, out var used) && planStock != null)
                partsDiffer = string.Join(" ", Enumerable.Range(0, used.Length).Where(i => i >= planStock.partActiveOptions.Count || planStock.partActiveOptions[i] != used[i])
                    .Select(i => i + ":" + (i < planStock.partActiveOptions.Count ? planStock.partActiveOptions[i].ToString() : "-") + ">" + used[i]).ToArray());
            var wheel = wheels != null ? wheels.FirstOrDefault(w => w && ActiveUnder(w, root))
                : boat.GetComponentsInChildren<GPButtonSteeringWheel>(true).Where(w => ActiveUnder(w.transform, root)).Select(w => w.transform).FirstOrDefault();
            Vector3 wheelAt = wheel ? root.InverseTransformPoint(wheel.position) : new Vector3(0, 2, -HullData.Size(hull).x * .3f);
            var lanternAt = HullData.Lanterns(hull); string lanternWhy = "";
            if (plan != null) lanternAt = LanternsOnMasts(boat, hull, out lanternWhy);
            var copy = Object.Instantiate(boat.gameObject, holder.transform, true);
            copy.name = boat.gameObject.name + " (trader)";
            var t = new HullTemplate { Hull = hull, Plan = plan, Key = key, Root = copy };
            // every hull as the game builds it (the Junk with its cabin again since the user's word of 2026-10-19: "i want to add the
            // junk's house cabin back so it looks like default hull"; from 2026-10-18 it was built "(no cabin)")
            string build = plan ?? "stock";
            // sails: the rest pose as installed (furled in port) and what the self-trim needs to pose each at any angle (Rig), its
            // face (ReadFace) and its place in the goosewing order (from aft, the jibs the foremost)
            Vector3 fwd = copy.transform.forward, right = copy.transform.right; fwd.y = 0; right.y = 0; fwd.Normalize(); right.Normalize();
            var faces = new System.Text.StringBuilder();
            // square sails only (kind 3 is a square that is not a junk square: Rig)
            bool allSquare = Rules.AllSquare(sails.Select(d => d.Kind == "square" && !d.Junk ? 3 : 0));
            foreach (var d in sails)
            {
                var st = FindIndex(copy.transform, d.Path); if (!st) continue;
                var rig = Rig(st, d, fwd, right, allSquare); t.Sails.Add(rig);
                if (faces.Length > 0) faces.Append(", ");
                ReadFace(rig, faces);
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
            // the paper first: it is found by its place in the ship, which DropMooring's removals renumber (its path then led to the
            // part after it; the deep review of 2026-11-02, Claude's fix)
            var paper = AccessTools.Field(typeof(PurchasableBoat), "purchaseUI")?.GetValue(boat) as GameObject;
            bool paperOff = DropPaper(copy, root, paper);
            int ropes = DropMooring(copy);
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
            // its flags as the game drives them (FlagRig), read before Strip takes their WindClothSimple off; none under a sail (its
            // cloth is the sail's own WindCloth's)
            {
                var sailRoots = copy.GetComponentsInChildren<Sail>(true).Select(s => s.transform).ToArray();
                foreach (var w in copy.GetComponentsInChildren<WindClothSimple>(true))
                {
                    if (!w.GetComponent<Cloth>() || sailRoots.Any(s => w.transform.IsChildOf(s))) continue;
                    int sail = w.sail ? t.Sails.FindIndex(s => s.T == w.sail.transform) : -1;
                    t.Flags.Add(new FlagRig { T = w.transform, Mult = w.staticMultiplier, Drive = w.shipRigidbody ? 0 : sail >= 0 ? 1 : 2, Sail = sail });
                }
            }
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
            t.AllSquare = Rules.AllSquare(t.Sails.Select(s => s.Kind));
            foreach (var s in t.Sails) s.Path = IndexPath(s.T, copy.transform);
            t.Flags.RemoveAll(f => !f.T);
            foreach (var f in t.Flags) f.Path = IndexPath(f.T, copy.transform);
            var trimmed = t.Sails.Where(s => s.Kind == 1 || s.Kind == 2).OrderBy(s => s.Kind).ThenBy(s => copy.transform.InverseTransformPoint(s.T.position).z).ToList();
            for (int i = 0; i < trimmed.Count; i++) trimmed[i].Order = i;
            string trim = string.Join(", ", t.Sails.Select(s => s.T.name + " " + (s.Kind == 1 ? (s.Yard ? "fore-and-aft (junk square)" : "fore-and-aft") : s.Kind == 2 ? "jib" : s.Kind == 3 ? "square" : "none") +
                                                               (s.Order >= 0 ? " " + s.Order + ", in to " + s.Closest.ToString("F0") : "")).ToArray());
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
            int undrawnOff = DropUndrawn(copy, out int undrawnEmpty);
            // each place in the template again, as dropping parts changes the children's order numbers
            foreach (var s in t.Sails) s.Path = IndexPath(s.T, copy.transform);
            foreach (var f in t.Flags) f.Path = IndexPath(f.T, copy.transform);
            t.MergedPaths = mergedParts.Where(x => x).Select(x => IndexPath(x, copy.transform)).ToList();
            t.RendererOn = copy.GetComponentsInChildren<Renderer>(true).Select(r => r.enabled).ToArray();
            copy.SetActive(false);   // a copy made from it wakes only where it is put
            t.Report = "(" + build + ", from " + boat.name + (boat.gameObject.activeInHierarchy ? ", active" : ", inactive") + "): " + t.Sails.Count + " sails, " + removed + " components removed, " + dropped + " parts dropped, " + colliders + " solid colliders kept, " +
                       t.RendererOn.Length + " renderers (" + merged + " static parts merged: " + mergeWhy + "; never drawn taken off: " + undrawnOff + " renderers, " + undrawnEmpty + " empty objects), wheel at " + wheelAt.ToString("F2") + "; sail ranges " + string.Join(", ", sails.Select(s => s.Kind + " " + s.Min.ToString("F0") + ".." + s.Max.ToString("F0")).ToArray()) +
                       "; " + materials + " materials its own, " + ropes + " mooring parts and " + unbound + " meshes bound outside it taken off, ropes drawn " + ropeLines +
                       " (" + reefAnchor + " reef and anchor ropes and " + ropesOff + " not drawn on it taken off, " + frozenRopes + " cloth ropes as captured taken off), purchase paper " + (paperOff ? "taken off" : "none") +
                       ", player collider " + (forPlayer ? "yes" : "none") + ", lantern " + lanternKind + lanternWhy + "; drawn outside the hull: " + Outside(copy, hull) +
                       "; sails trimmed (kind, goosewing order from aft, sheeted in): " + trim + (t.AllSquare ? " (square sails only: never furled under way, furled at a berth or the anchorage, braced to " + Rules.AllSquareBraceMost.ToString("F0") + ")" : "") + "; their faces: " + faces +
                       "; trimmed past the range: " + (pastLimit.Count > 0 ? string.Join(", ", pastLimit.ToArray()) : "none") +
                       "; flags driven: " + (t.Flags.Count > 0 ? string.Join(", ", t.Flags.Select(f => f.T.name + " " + (f.Drive == 0 ? "its apparent wind" : f.Drive == 1 ? "sail " + f.Sail : "the wind") + " x" + f.Mult.ToString("F2")).ToArray()) : "none") + (plan != null ? "; parts unlike the stock rig: " + (partsDiffer.Length > 0 ? partsDiffer : "none") : "");
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
        private static List<Transform> mergedParts = new List<Transform>();
        private static int MergeStatic(GameObject copy, HullTemplate t)
        {
            var root = copy.transform; mergeWhy = ""; mergedParts = new List<Transform>();
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
            mergedParts = parts.Select(r => r.transform).ToList();   // for the test's check that none of them moves (HullTemplate.MergedPaths)
            foreach (var r in parts) Object.DestroyImmediate(r);
            return parts.Count;
        }
        // What the copy holds and never draws (TRADE_OFFICE_DRAFT.md §3B, "The cut, as planned"; the user: "do cut object"): every loaded object costs each save, as the game's unload walks them all.
        // Taken off: a renderer that is off or under something off, on an object holding nothing but a mesh and a renderer, unless something still reads it; a mesh with no renderer left; then any object
        // left with nothing on it and no children (the merged parts' shells among them). "Reads it": the fields of every script kept on the copy (the sails' furled looks, ropes' attachments, a sail's parts),
        // a skinned mesh's bones, a cloth's colliders, an LOD group's renderers, a joint's body. Only ReefEffectAnimUniversal (furledSail), WindCloth (on its own object, which holds a script) and RopeEffect
        // switch a renderer on (read in the decompile), and the reef clips switch none: `copylook` counts the renderers drawn furled and set. Returns the renderers taken off.
        private static int DropUndrawn(GameObject copy, out int empty)
        {
            var root = copy.transform; int off = 0; empty = 0;
            var read = new HashSet<Object>();
            foreach (var c in copy.GetComponentsInChildren<Component>(true)) if (c) MarkReads(c, read);
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true))
            {
                if (r.enabled && ActiveUnder(r.transform, root)) continue;
                if (read.Contains(r) || read.Contains(r.gameObject)) continue;
                bool plain = true;
                foreach (var k in r.GetComponents<Component>()) if (!(k is Transform || k is MeshFilter || k is MeshRenderer || k is SkinnedMeshRenderer)) { plain = false; break; }
                if (!plain) continue;
                Object.DestroyImmediate(r); off++;
            }
            foreach (var f in copy.GetComponentsInChildren<MeshFilter>(true)) if (!f.GetComponent<Renderer>() && !read.Contains(f)) Object.DestroyImmediate(f);
            // what the renderers just taken off read (their bones) reads nothing now
            read.Clear();
            foreach (var c in copy.GetComponentsInChildren<Component>(true)) if (c) MarkReads(c, read);
            var all = copy.GetComponentsInChildren<Transform>(true);
            for (int i = all.Length - 1; i >= 0; i--)   // children first: a parent left with none goes in the same pass
            {
                var t = all[i];
                if (t == root || t.childCount > 0 || t.GetComponents<Component>().Length > 1 || read.Contains(t) || read.Contains(t.gameObject)) continue;
                Object.DestroyImmediate(t.gameObject); empty++;
            }
            return off;
        }
        private static readonly Dictionary<Type, System.Reflection.FieldInfo[]> scriptFields = new Dictionary<Type, System.Reflection.FieldInfo[]>();
        private static System.Reflection.FieldInfo[] ScriptFields(Type type)
        {
            if (scriptFields.TryGetValue(type, out var known)) return known;
            var list = new List<System.Reflection.FieldInfo>();
            for (var t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
                list.AddRange(t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly));
            return scriptFields[type] = list.Where(f => !f.FieldType.IsPrimitive && !f.FieldType.IsEnum && f.FieldType != typeof(string)).ToArray();
        }
        private static void MarkRead(Object o, HashSet<Object> read)
        {
            if (!o) return;
            read.Add(o);
            if (o is Component c) { read.Add(c.gameObject); read.Add(c.transform); }
            else if (o is GameObject g) read.Add(g.transform);
        }
        private static void MarkValue(object value, HashSet<Object> read, int depth)
        {
            if (value == null) return;
            if (value is Object o) { MarkRead(o, read); return; }
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string || value is Delegate) return;
            if (value is System.Collections.IEnumerable list)
            {
                var element = type.GetElementType();
                if (element != null && element.IsValueType && (element.IsPrimitive || element.Namespace == "UnityEngine")) return;
                foreach (var x in list) MarkValue(x, read, depth);
                return;
            }
            // a script's own serialized class or struct, as a sail's connections may hold parts in one
            if (depth < 3 && type.IsSerializable && type.Namespace != "UnityEngine")
                foreach (var f in ScriptFields(type)) { object v; try { v = f.GetValue(value); } catch { continue; } MarkValue(v, read, depth + 1); }
        }
        private static void MarkReads(Component c, HashSet<Object> read)
        {
            if (c is SkinnedMeshRenderer s) { foreach (var b in s.bones) MarkRead(b, read); MarkRead(s.rootBone, read); return; }
            if (c is Cloth k)
            {
                if (k.capsuleColliders != null) foreach (var x in k.capsuleColliders) MarkRead(x, read);
                if (k.sphereColliders != null) foreach (var p in k.sphereColliders) { MarkRead(p.first, read); MarkRead(p.second, read); }
                return;
            }
            if (c is LODGroup l) { foreach (var lod in l.GetLODs()) if (lod.renderers != null) foreach (var r in lod.renderers) MarkRead(r, read); return; }
            if (c is Joint j) { MarkRead(j.connectedBody, read); return; }
            if (!(c is MonoBehaviour)) return;
            foreach (var f in ScriptFields(c.GetType())) { object v; try { v = f.GetValue(c); } catch { continue; } MarkValue(v, read, 0); }
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
        // A mesh bound to bones outside the copy draws where those bones are, at the game's ship (a rope end moored to a dock leaves
        // the ship: PickupableBoatMooringRope.MoorTo puts it under the shifting world from Sailwind 0.40, under the dock's spring up
        // to 0.39.2; the dock's spring is read from mooredToSpring, Pledges.MooredTo): taken off (points 5 and 7).
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
        // Mesh, MeshMaterials: the outfit's worn parts as one mesh in his pose, made when it is first worn (Dress, BakeOutfit).
        internal sealed class Outfit { internal int Kind; internal int[] Parts; internal Material[][] Materials; internal Mesh[] Meshes; internal string From; internal Mesh Mesh; internal Material[] MeshMaterials; }
        private static readonly List<Outfit> outfits = new List<Outfit>();
        private static string[] helmParts = new string[0];   // the rig's clothes, as paths under it
        private static Mesh[] helmMeshes = new Mesh[0];      // and their own meshes
        private static GameObject helmRig;   // the one full model, its clothes off, kept only to make the outfits' meshes; null without outfits
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
            if (outfits.Count == 0) return go;
            // He never moves (the user, question 9: static), so a copy needs no bones or clothes parts: 993 objects each on 74 templates and copies (`sm-1`; TRADE_OFFICE_DRAFT.md §3B, "The cut, as planned"). Each
            // holds one object drawing its outfit's one mesh. The full model stays once, for making those meshes: active (its bones only: its clothes are off, so it draws nothing), as a part baked
            // under the switched-off holder is not known to work.
            var first = go.transform.Find(helmParts[0]).GetComponent<Renderer>();
            var rigHolder = new GameObject("EconomyOverhaul helmsman rig"); Object.DontDestroyOnLoad(rigHolder);
            go.transform.SetParent(rigHolder.transform, false); go.SetActive(true); helmRig = go;
            var plain = new GameObject("helmsman template") { layer = first.gameObject.layer };
            plain.transform.SetParent(holder.transform, false);
            plain.AddComponent<MeshFilter>();
            var look = plain.AddComponent<MeshRenderer>();
            look.shadowCastingMode = first.shadowCastingMode; look.receiveShadows = first.receiveShadows; look.lightProbeUsage = first.lightProbeUsage; look.reflectionProbeUsage = first.reflectionProbeUsage;
            return plain;
        }
        // A copy's helmsman put in an outfit: its mesh and materials, the mesh made when the outfit is first worn.
        internal static void Dress(TraderCopy c, int outfit)
        {
            if (!c.HelmLook || outfit < 0 || outfit >= outfits.Count) return;
            var o = outfits[outfit];
            if (!o.Mesh) BakeOutfit(o, outfit);
            c.HelmLook.sharedMesh = o.Mesh; c.HelmRenderer.sharedMaterials = o.MeshMaterials;
            c.Outfit = outfit;
        }
        // An outfit on a whole NPC copy (the trade office's clerk, TradeOffice.cs): its clothes off, the outfit's parts on with their meshes and
        // materials (the parts' paths are under the model's root, Modular NPC, as on the helmsman's rig). False when there are no outfits.
        internal static bool DressWhole(Transform modelRoot, int outfit)
        {
            if (!modelRoot || outfit < 0 || outfit >= outfits.Count || helmParts.Length == 0) return false;
            var o = outfits[outfit];
            foreach (var s in Clothes(modelRoot)) s.gameObject.SetActive(false);
            for (int k = 0; k < o.Parts.Length; k++)
            {
                var part = modelRoot.Find(helmParts[o.Parts[k]]); var s = part ? part.GetComponent<SkinnedMeshRenderer>() : null; if (!s) continue;
                if (o.Meshes != null && k < o.Meshes.Length && o.Meshes[k]) s.sharedMesh = o.Meshes[k];
                s.sharedMaterials = o.Materials[k]; part.gameObject.SetActive(true);
            }
            return true;
        }
        internal static double BakeMsMax;   // the longest an outfit's mesh took, for the tests
        // The outfit's worn parts in his pose as one mesh, a sub-mesh per material. BakeMesh gives a part's vertices in its own place and turn with its scale already applied, so it is placed with no scale.
        private static void BakeOutfit(Outfit o, int index)
        {
            var sw = Stopwatch.StartNew();
            var rig = helmRig.transform;
            var toHelmsman = Matrix4x4.TRS(rig.position, rig.rotation, Vector3.one).inverse;
            var groups = new Dictionary<Material, List<CombineInstance>>(); var order = new List<Material>(); var made = new List<Mesh>();
            for (int k = 0; k < o.Parts.Length; k++)
            {
                var part = rig.Find(helmParts[o.Parts[k]]); var s = part ? part.GetComponent<SkinnedMeshRenderer>() : null; if (!s) continue;
                s.sharedMesh = o.Meshes != null && o.Meshes[k] ? o.Meshes[k] : helmMeshes[o.Parts[k]];
                part.gameObject.SetActive(true);
                var baked = new Mesh(); s.BakeMesh(baked);
                part.gameObject.SetActive(false);
                made.Add(baked);
                var place = toHelmsman * Matrix4x4.TRS(part.position, part.rotation, Vector3.one);
                var materials = o.Materials[k];
                for (int sub = 0; sub < baked.subMeshCount && sub < materials.Length; sub++)
                {
                    if (!materials[sub]) continue;
                    if (!groups.TryGetValue(materials[sub], out var list)) { groups[materials[sub]] = list = new List<CombineInstance>(); order.Add(materials[sub]); }
                    list.Add(new CombineInstance { mesh = baked, subMeshIndex = sub, transform = place });
                }
            }
            // Merged by hand: the model's material picks each colour through a second UV channel, which Mesh.CombineMeshes didn't keep (`cl-new`: clothes drawn pale; `cl-new3`: right; EconomyOverhaul_FAILED.md).
            var whole = new Mesh { name = "helmsman outfit " + index };
            var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var tangents = new List<Vector4>();
            var uvs = new[] { new List<Vector2>(), new List<Vector2>(), new List<Vector2>(), new List<Vector2>() }; var triangles = new List<List<int>>();
            var points = new List<Vector3>(); var fours = new List<Vector4>(); var twos = new List<Vector2>(); var indices = new List<int>();
            foreach (var material in order)
            {
                var tris = new List<int>();
                foreach (var c in groups[material])
                {
                    int start = vertices.Count, count = c.mesh.vertexCount;
                    c.mesh.GetVertices(points); foreach (var p in points) vertices.Add(c.transform.MultiplyPoint3x4(p));
                    c.mesh.GetNormals(points); for (int i = 0; i < count; i++) normals.Add(i < points.Count ? c.transform.MultiplyVector(points[i]) : Vector3.up);
                    c.mesh.GetTangents(fours);
                    for (int i = 0; i < count; i++) { if (i < fours.Count) { var t = c.transform.MultiplyVector(fours[i]); tangents.Add(new Vector4(t.x, t.y, t.z, fours[i].w)); } else tangents.Add(new Vector4(1f, 0f, 0f, 1f)); }
                    for (int channel = 0; channel < uvs.Length; channel++) { c.mesh.GetUVs(channel, twos); for (int i = 0; i < count; i++) uvs[channel].Add(i < twos.Count ? twos[i] : Vector2.zero); }
                    c.mesh.GetTriangles(indices, c.subMeshIndex); foreach (int i in indices) tris.Add(start + i);
                }
                triangles.Add(tris);
            }
            if (vertices.Count > 65000) whole.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            whole.SetVertices(vertices); whole.SetNormals(normals); whole.SetTangents(tangents);
            for (int channel = 0; channel < uvs.Length; channel++) if (uvs[channel].Any(u => u != Vector2.zero)) whole.SetUVs(channel, uvs[channel]);
            whole.subMeshCount = triangles.Count;
            for (int i = 0; i < triangles.Count; i++) whole.SetTriangles(triangles[i], i, true);
            foreach (var m in made) Object.DestroyImmediate(m);
            o.Mesh = whole; o.MeshMaterials = order.ToArray();
            BakeMsMax = Math.Max(BakeMsMax, sw.Elapsed.TotalMilliseconds);
            Plugin.Log?.LogInfo("Visible traders: outfit " + index + " (from " + o.From + ") made one mesh in " + sw.Elapsed.TotalMilliseconds.ToString("F2") + " ms: " + o.Parts.Length + " parts, " + whole.vertexCount + " vertices, " + order.Count + " materials.");
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
            c.SailCloths = c.SailT.Select(s => s ? s.GetComponentInChildren<Cloth>(true) : null).ToArray();
            c.SailClothLooks = c.SailCloths.Select(k => k ? k.GetComponent<Renderer>() : null).ToArray();
            c.FlagCloths = t.Flags.Select(f => { var x = FindIndex(go.transform, f.Path); return x ? x.GetComponent<Cloth>() : null; }).ToArray();
            var helm = go.transform.Find("helmsman");
            if (helm && helmRig) { c.HelmLook = helm.GetComponent<MeshFilter>(); c.HelmRenderer = helm.GetComponent<MeshRenderer>(); }
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
            // shortcut: a trade fleet ship's deck cargo is placed by its hull's stock parts (HullData.Cargo, the fits, the Junk's cabin rule); a Junk
            // rigged without its cabin part shows it where the cabin would stand; replace if a player reports cargo floating on a fleet ship
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
        // The sails eased toward their trim (the self-trim, TRADER_TRIM_DRAFT.md): awa the apparent wind's angle off the bow; side 0
        // every sail at rest (in port), else each sail on its own side (sides, by the template's order; 0 or none: `side`), a
        // fore-and-aft sail or jib out by Rules.TrimOut (80 in the goosewing band), a square yard braced by Rules.SquareBrace.
        // holdUnseen: a sail whose cloth is on but drawn by no camera stays as it is, and turns once its cloth is seen again (the
        // user, 2026-10-23: "Hold trim while unseen"): Unity simulates a cloth only while it is seen, and a sail turned meanwhile
        // came back with its cloth stretched (probe vttrimunseen, run vttu-1: TRADER_TRIM_DRAFT.md, "Sails turned while out of view").
        // Returns the largest turn left to any sail before this step, in degrees.
        internal static float PoseSails(TraderCopy c, int side, float awa, float blend, int[] sides = null, bool holdUnseen = false)
        {
            var rigs = c.Template.Sails; float left = 0;
            for (int i = 0; i < rigs.Count && i < c.SailT.Length; i++)
            {
                var t = c.SailT[i]; if (!t) continue;
                if (holdUnseen && c.SailCloths != null && i < c.SailCloths.Length) { var k = c.SailCloths[i]; var r = c.SailClothLooks[i]; if (k && k.enabled && r && r.enabled && !r.isVisible) continue; }
                var rig = rigs[i]; int s = side == 0 ? 0 : sides != null && i < sides.Length && sides[i] != 0 ? sides[i] : side;
                var target = s == 0 ? rig.Rest : rig.At(s, (float)(rig.Kind == 3 ? Rules.SquareBrace(awa, c.Template.AllSquare) : Rules.TrimOut(awa, rig.Closest)));
                left = Mathf.Max(left, Quaternion.Angle(t.localRotation, target));
                t.localRotation = blend >= 1 ? target : Quaternion.Slerp(t.localRotation, target, blend);
            }
            return left;
        }
        // The setting off: every copy destroyed (made again when it is turned on).
        internal static void ClearPool()
        {
            foreach (var c in Copies) if (c.Go) Object.Destroy(c.Go);
            pool.Clear(); made.Clear(); fleetMade.Clear(); bodies.Clear(); copyWinds.Clear(); copyReefs.Clear(); poolDone = false;
            // the fleet job's work dropped too: switched off and on between its template and its copy, a copy made by BuildStep was then
            // replaced in the pool and never destroyed, and its clone stayed awake meanwhile (the deep review of 2026-11-09)
            FleetAbort();
        }

        // ---- The trade fleet's ships (TRADE_OFFICE_FLEET_DRAWING.md; the user: "The trader ship will look the same as how you rigged it",
        // "remember the hull option need to be copy also") ----
        // Each fleet ship gets its own template and copy (key "fleet-<its id>", a pool of one), made after a load and when it joins: its rig
        // (OfficeFleetApi.RigOf's text: its hull options and sails) is put by the game's own LoadData on a clone of the game's ship of its hull,
        // never on that ship itself, which can be the player's own (point 3, probe officeclone, runs ocl-1 to ocl-3), and the template is taken
        // from the clone. One step a frame: the clone; it wakes; its rig; two frames for the new sails' Start; the template; its copy. A rig
        // that can't be drawn (another mod's sail, more part options than the hull has, a sail left off) leaves the ship on its hull's stock rig.
        internal const string FleetPrefix = "fleet-";
        internal static string FleetKey(int id) => FleetPrefix + id;
        private sealed class FleetJob
        {
            internal string Key, Rig, Report; internal int Hull, Step, Wait; internal RigPlan Plan; internal GameObject Clone; internal PurchasableBoat Original;
            internal readonly List<Transform> Wheels = new List<Transform>(); internal double Ms;
        }
        private static FleetJob fleetJob;
        private static readonly Dictionary<string, string> fleetRigs = new Dictionary<string, string>();    // a fleet key: the rig its template was made from
        private static readonly Dictionary<string, string> fleetStock = new Dictionary<string, string>();   // a fleet key drawn on its hull's stock rig: its rig
        private static readonly List<TraderCopy> fleetMade = new List<TraderCopy>();
        private static float nextFleetLook;
        internal static double FleetMaxStepMs;   // the largest step, for the tests
        internal static bool FleetBusy => fleetJob != null;
        // What a fleet ship is drawn with: its own template once that and its copy are made, its hull's stock rig if its own rig can't be drawn, else
        // nothing yet.
        internal static string FleetDrawKey(int id, int hull)
        {
            string key = FleetKey(id);
            if (templates.ContainsKey(key)) return pool.TryGetValue(key, out var l) && l.Count > 0 ? key : null;
            return fleetStock.ContainsKey(key) ? Rules.TraderKey(null, hull) : null;
        }
        internal static string FleetReport(int id) => fleetRigs.ContainsKey(FleetKey(id)) ? "own" : fleetStock.ContainsKey(FleetKey(id)) ? "stock" : fleetJob != null && fleetJob.Key == FleetKey(id) ? "making" : "none";
        // A step of the fleet's templates (VisibleTraders.UpdateParts, once the pool is made and a game is under way): true when it did work.
        internal static bool FleetStep()
        {
            foreach (var c in fleetMade) Sleep(c);
            fleetMade.Clear();
            if (fleetJob != null) { FleetJobStep(); return true; }
            if (Time.realtimeSinceStartup < nextFleetLook) return false;
            nextFleetLook = Time.realtimeSinceStartup + .5f;
            var ships = World.State?.playerFleet; var s = World.State?.office;
            var want = new Dictionary<string, KeyValuePair<int, string>>();
            if (ships != null && s?.fleetHull != null && s.fleetRig != null)
                for (int i = 0; i < ships.Count && i < s.fleetHull.Length && i < s.fleetRig.Length; i++) want[FleetKey(ships[i].id)] = new KeyValuePair<int, string>(s.fleetHull[i], s.fleetRig[i] ?? "");
            // a ship gone from the fleet (dismissed, a bankruptcy, another game) or rigged otherwise: its template and copy go (once not drawn)
            foreach (var key in fleetRigs.Keys.Concat(fleetStock.Keys).Distinct().ToList())
            {
                string had = fleetRigs.TryGetValue(key, out var r) ? r : fleetStock.TryGetValue(key, out var st) ? st : null;
                if (had != null && (!want.TryGetValue(key, out var w) || w.Value != had)) DropFleet(key);
            }
            // a template without its copy (the setting was switched off and on): its copy
            foreach (var key in fleetRigs.Keys)
                if ((!pool.TryGetValue(key, out var l) || l.Count == 0) && templates.TryGetValue(key, out var t))
                {
                    var c = MakeCopy(t); pool[key] = new List<TraderCopy> { c }; fleetMade.Add(c);
                    return true;
                }
            foreach (var kv in want)
                if (!fleetRigs.ContainsKey(kv.Key) && !fleetStock.ContainsKey(kv.Key)) { StartFleet(kv.Key, kv.Value.Key, kv.Value.Value); return true; }
            return false;
        }
        private static void StartFleet(string key, int hull, string rig)
        {
            var sw = Stopwatch.StartNew();
            RigPlan plan = null; PurchasableBoat boat = null; string why;
            try
            {
                plan = TraderRigs.Read((rig ?? "").Split('\n')).FirstOrDefault();
                boat = HullData.Hulls.Contains(hull) ? Original(hull) : null;
                // checked against the game's ship (read only): a clone's masts under parts not installed register only once a part puts them up
                why = plan == null ? "no rig" : !boat ? "the game's ship of hull " + hull + " not found" : TraderRigs.Check(plan, boat.GetComponent<BoatCustomParts>(), boat.GetComponent<BoatRefs>(), fleet: true);
            }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; }
            var job = new FleetJob { Key = key, Hull = hull, Rig = rig, Plan = plan, Original = boat };
            if (why != null) { FleetFail(job, why); return; }
            plan.Name = key;
            try { job.Clone = FleetClone(boat, job.Wheels, out job.Report); }
            catch (Exception e) { FleetFail(job, "its clone failed: " + e); return; }
            job.Ms = sw.Elapsed.TotalMilliseconds; FleetMaxStepMs = Math.Max(FleetMaxStepMs, job.Ms);
            fleetJob = job;
        }
        private static void FleetJobStep()
        {
            var job = fleetJob; var sw = Stopwatch.StartNew();
            try
            {
                // (the clone goes once the template is made: the copy's step has none; ofd-2 failed the ship there and listed it twice)
                if (job.Step < 3 && !job.Clone) { FleetFail(job, "its clone is gone"); return; }
                switch (job.Step)
                {
                    case 0:
                        // woken where the hidden copies lie, 2 km under the sea
                        var p = Refs.observerMirror ? Refs.observerMirror.transform.position : Vector3.zero;
                        job.Clone.transform.position = new Vector3(p.x, -2000f, p.z);
                        job.Clone.transform.SetParent(Refs.shiftingWorld, true);
                        FleetCompat.Forget(job.Clone);   // off the lists other mods' wake hooks put it on (NANDTweaks': a save died on it, ofd-6-a)
                        job.Step = 1; break;
                    case 1:
                        // its rig by the game's own LoadData, its own part options as they were; the mod data as it was after (other mods' LoadData patches
                        // write it: Shipyard Expansion's sail config, as the start menu's plans keep it)
                        var keep = GameState.modData != null ? new Dictionary<string, string>(GameState.modData) : null;
                        try { TraderRigs.PutOn(job.Clone.GetComponent<SaveableBoatCustomization>(), job.Plan, job.Plan.Options, job.Original ? job.Original.GetComponent<BoatRefs>() : null); }
                        finally { if (keep != null && GameState.modData != null) { GameState.modData.Clear(); foreach (var kv in keep) GameState.modData[kv.Key] = kv.Value; } }
                        // the ship's sails' Shipyard Expansion angles, jib sides and textures, on the new sails before their Start (as its own LoadData postfix
                        // puts them on a ship)
                        string sePut = FleetCompat.PutSails(job.Clone.GetComponent<BoatRefs>(), job.Plan, out var seMissed);
                        if (sePut != null || seMissed != null) job.Report += "; Shipyard Expansion's sail settings put on: " + (sePut ?? "none") + (seMissed != null ? "; not put: " + seMissed : "");
                        foreach (var rb in job.Clone.GetComponentsInChildren<Rigidbody>(true)) FleetHold(rb);
                        job.Step = 2; job.Wait = 2; break;
                    case 2:
                        // two frames for the new sails' Start (their rope controllers' first angles: ReadSails), then the template
                        if (--job.Wait > 0) break;
                        var cloneParts = job.Clone.GetComponent<BoatCustomParts>();
                        var applied = cloneParts ? cloneParts.availableParts.Select(x => x.activeOption).ToArray() : new int[0];
                        var t = MakeTemplate(job.Clone.GetComponent<PurchasableBoat>(), job.Hull, job.Key, job.Key, job.Wheels);
                        if (t == null) { FleetFail(job, "no template made"); return; }
                        if (t.Sails.Count < job.Plan.Sails.Count) { DestroyTemplate(t); FleetFail(job, t.Sails.Count + " of its " + job.Plan.Sails.Count + " sails on"); return; }
                        t.Fleet = true; t.Options = applied; templates[job.Key] = t; fleetRigs[job.Key] = job.Rig; fleetStock.Remove(job.Key);
                        Object.Destroy(job.Clone); job.Clone = null;
                        Plugin.Log?.LogInfo("Visible traders: fleet ship " + job.Key + " (hull " + job.Hull + ") template " + t.Report + "; its clone: " + job.Report + "; its part options " + string.Join(",", applied.Select(o => o.ToString()).ToArray()));
                        job.Step = 3; break;
                    default:
                        var c = MakeCopy(templates[job.Key]); pool[job.Key] = new List<TraderCopy> { c }; fleetMade.Add(c);
                        fleetJob = null;
                        Plugin.Log?.LogInfo("Visible traders: fleet ship " + job.Key + " drawn as rigged from now (" + (job.Ms + sw.Elapsed.TotalMilliseconds).ToString("F1") + " ms in its steps, the largest step " + Math.Max(FleetMaxStepMs, sw.Elapsed.TotalMilliseconds).ToString("F1") + " ms).");
                        break;
                }
            }
            catch (Exception e) { FleetFail(job, e.ToString()); }
            finally { double ms = sw.Elapsed.TotalMilliseconds; job.Ms += ms; FleetMaxStepMs = Math.Max(FleetMaxStepMs, ms); }
        }
        private static void FleetFail(FleetJob job, string why)
        {
            if (job.Clone) Object.Destroy(job.Clone);
            job.Clone = null; fleetStock[job.Key] = job.Rig ?? ""; if (fleetJob == job) fleetJob = null;
            Plugin.Log?.LogWarning("Visible traders: fleet ship " + job.Key + "'s own template not made (" + why + "): it is drawn on its hull's stock rig.");
        }
        // A game left (a load, a new game): the work under way dropped; the templates stay for the next game's same ships (FleetStep drops the others).
        internal static void FleetAbort()
        {
            if (fleetJob == null) return;
            if (fleetJob.Clone) Object.Destroy(fleetJob.Clone);
            fleetJob = null;
        }
        private static void DropFleet(string key)
        {
            pool.TryGetValue(key, out var list);
            if (list != null && list.Any(c => c.Boat != null)) return;   // drawn: let go at the next scan, then dropped
            if (list != null)
                foreach (var c in list)
                {
                    if (c.Body) bodies.Remove(c.Body);
                    foreach (var b in c.Toggled) { if (b is WindCloth w) copyWinds.Remove(w); else if (b is ReefEffectAnimUniversal r) copyReefs.Remove(r); }
                    if (c.Go) Object.Destroy(c.Go);
                    fleetMade.Remove(c);
                }
            pool.Remove(key);
            if (templates.TryGetValue(key, out var t)) { DestroyTemplate(t); templates.Remove(key); }
            fleetRigs.Remove(key); fleetStock.Remove(key);
            Plugin.Log?.LogInfo("Visible traders: fleet ship " + key + "'s template and copy dropped (it left the fleet or this game).");
        }
        // A template destroyed with what was made for it alone: its merged meshes (MergeStatic) and its own materials (OwnMaterials).
        private static void DestroyTemplate(HullTemplate t)
        {
            if (!t.Root) return;
            foreach (var f in t.Root.GetComponentsInChildren<MeshFilter>(true)) if (f.sharedMesh && f.sharedMesh.name.StartsWith("merged ", StringComparison.Ordinal)) Object.Destroy(f.sharedMesh);
            var mats = new HashSet<Material>();
            foreach (var r in t.Root.GetComponentsInChildren<Renderer>(true)) foreach (var m in r.sharedMaterials) if (m && m.name.EndsWith(" (trader)", StringComparison.Ordinal)) mats.Add(m);
            foreach (var m in mats) Object.Destroy(m);
            Object.Destroy(t.Root);
        }
        private static void FleetHold(Rigidbody rb)
        {
            if (rb.collisionDetectionMode != CollisionDetectionMode.Discrete) rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb.isKinematic = true; rb.detectCollisions = false; rb.interpolation = RigidbodyInterpolation.None;
        }

        // The clone a fleet ship's rig is put on (probe officeclone; EconomyOverhaul_FAILED.md, "The fleet ship's clone"): the game's ship copied under
        // the inactive holder, switched on itself (a far ship's root is off), and stripped before it wakes so that nothing of it reaches the game or
        // the ship it was copied from. Taken off: its purchase signs, items, saved prefabs, mooring set and ropes, cloth ropes (DropMooring's and
        // DropClothRopes' rules), every sail with its parts and the sail rope controllers left without one, every script but those the game's
        // LoadData and MakeTemplate use (FleetKept; the wheels' places kept in wheels for the helmsman: GPButtonSteeringWheel.Awake makes an object at
        // the world's root). A part option's walk and child objects out of it become dummies inside it (BoatPart.SetOptionEnabled switches them: the
        // original's deck otherwise; SaveableObject.Awake would take the original's save slot, so it goes); any other reference out of it is
        // nulled; the masts' start sails and registry cleared (Mast.Start, Mast.Awake); the kept scripts switched off but the ones that load the rig
        // (FleetLive: their Awake alone runs, so no Sun.OnNewDay, Crest input or pointer is registered); sounds and lights off; bodies kinematic.
        private static readonly string[] FleetKeepNames = { "SaveableBoatCustomization", "BoatRefs", "BoatCustomParts", "BoatPartOption", "Mast", "GPButtonRopeWinch", "PurchasableBoat",
            "RopeEffect", "WindCloth", "WindClothSimple", "HullPlayerCollider", "CleanableObject", "HullDamageTexture", "Sail", "SailConnections", "ShipyardSailColChecker",
            "JibAngleMaster", "SquareAngleMaster", "SquareTopsailAngleMirror" };
        private static readonly string[] FleetLiveNames = { "SaveableBoatCustomization", "BoatRefs", "BoatCustomParts", "BoatPartOption", "Mast", "PurchasableBoat" };
        private static bool FleetKept(Type t) => FleetKeepNames.Contains(t.Name) || t.Name.StartsWith("RopeController", StringComparison.Ordinal) || t.Name.StartsWith("ReefEffect", StringComparison.Ordinal) ||
            t.Assembly.GetName().Name == "Crest" && t.Name.StartsWith("Register", StringComparison.Ordinal);
        // The fleet clones alive (FleetClone's, and the probe officeclone's own): other mods' wake and LoadData hooks are skipped for them (FleetCompat).
        internal static readonly HashSet<GameObject> FleetClones = new HashSet<GameObject>();
        internal static bool IsFleetClone(Component c) => c && FleetClones.Contains(c.gameObject);
        internal static void MarkFleetClone(GameObject c) { FleetCompat.Ensure(); FleetClones.RemoveWhere(g => !g); FleetClones.Add(c); }
        private static GameObject FleetClone(PurchasableBoat original, List<Transform> wheels, out string report)
        {
            var c = Object.Instantiate(original.gameObject, holder.transform, true); c.name = original.gameObject.name + " (fleet clone)"; MarkFleetClone(c); c.SetActive(true);
            var root = c.transform; var done = new List<string>();
            var paper = AccessTools.Field(typeof(PurchasableBoat), "purchaseUI")?.GetValue(original) as GameObject;
            done.Add("signs " + (DropPaper(c, original.transform, paper) ? "off" : "none"));
            // and this mod's own "Boat rental" scroll, made in play on each ship for sale (RentalSign; ofd-1: 4 renderers more than the start menu's
            // template of the same rig, which never has it)
            int rental = 0;
            foreach (var rs in c.GetComponentsInChildren<RentalSign>(true)) if (rs.scroll && rs.scroll.transform.IsChildOf(root)) { Object.DestroyImmediate(rs.scroll); rental++; }
            foreach (var x in c.GetComponentsInChildren<Transform>(true).Where(x => x.name == "boat rental UI").ToList()) if (x && x != root) { Object.DestroyImmediate(x.gameObject); rental++; }
            done.Add("rental scrolls " + rental);
            done.Add("mooring parts " + DropMooring(c) + ", cloth ropes " + DropClothRopes(c));
            int items = 0;
            foreach (var go in c.GetComponentsInChildren<Transform>(true).Select(x => x.gameObject).Where(g => g.GetComponent<ShipItem>() || g.GetComponent<SaveablePrefab>()).ToList())
                if (go && go != c) { Object.DestroyImmediate(go); items++; }
            int sails = 0, stale = 0;
            foreach (var s in c.GetComponentsInChildren<Sail>(true).ToList())
            {
                if (!s) continue;
                var sc = s.GetComponent<SailConnections>();
                if (sc) foreach (var part in new Component[] { sc.colChecker, sc.reefController, sc.angleControllerMid, sc.angleControllerLeft, sc.angleControllerRight, sc.midRopeAttachment, sc.mastReefAttachment, sc.mastReefAttExtension })
                        if (part && part.transform.IsChildOf(root) && part.transform != root) Object.DestroyImmediate(part.gameObject);
                if (s) { Object.DestroyImmediate(s.gameObject); sails++; }
            }
            foreach (var rc in c.GetComponentsInChildren<RopeController>(true).ToList())
                if (rc && (rc is RopeControllerSailAngle || rc is RopeControllerSailAngleJib || rc is RopeControllerSailAngleSquare || rc is RopeControllerSailReef) && rc.gameObject != c) { Object.DestroyImmediate(rc.gameObject); stale++; }
            done.Add("items " + items + ", sails " + sails + ", sail rope controllers left " + stale);
            foreach (var w in c.GetComponentsInChildren<GPButtonSteeringWheel>(true)) wheels.Add(w.transform);
            int scripts = 0;
            for (int pass = 0; pass < 6; pass++)
            {
                int now = 0;
                foreach (var m in c.GetComponentsInChildren<MonoBehaviour>(true)) if (m && !FleetKept(m.GetType()) && !Required(m)) { Object.DestroyImmediate(m); now++; }
                scripts += now; if (now == 0) break;
            }
            int dummies = 0, nulled = 0;
            foreach (var r in PointingOut(c))
            {
                object value = null;
                if (r.Comp is BoatPartOption && (r.Field.Name == "walkColObject" || r.Field.Name == "childOptions") && r.Target is GameObject walk)
                {
                    var d = new GameObject("walk dummy " + r.Comp.name); d.transform.SetParent(root, false); d.SetActive(walk.activeSelf); value = d; dummies++;
                }
                else nulled++;
                if (r.Index < 0) r.Field.SetValue(r.Owner, value); else ((IList)r.Field.GetValue(r.Owner))[r.Index] = value;
            }
            int joints = 0;
            foreach (var j in c.GetComponentsInChildren<Joint>(true)) if (j.connectedBody && !j.connectedBody.transform.IsChildOf(root)) { Object.DestroyImmediate(j); joints++; }
            foreach (var cl in c.GetComponentsInChildren<Cloth>(true)) if (cl.capsuleColliders != null && cl.capsuleColliders.Any(k => k && !k.transform.IsChildOf(root))) cl.capsuleColliders = cl.capsuleColliders.Where(k => k && k.transform.IsChildOf(root)).ToArray();
            done.Add("scripts removed " + scripts + ", deck dummies " + dummies + ", references out nulled " + nulled + ", joints out " + joints + " (unbound meshes: the template's DropUnbound)");
            foreach (var m in c.GetComponentsInChildren<Mast>(true)) { m.sails = new List<GameObject>(); m.startSailPrefab = null; m.startSailPrefabs = new GameObject[0]; m.startSailsHeightOffsets = new float[0]; }
            // emptied, as long as the original's: an empty one BoatRefs.RegisterMast makes 48 long, and Shipyard Expansion's masts (its Awake hook
            // makes the array 128 long) and MoreSailwindSails' stay mounts (256) sit past that (FleetCompat skips those hooks on the clone)
            var refs = c.GetComponent<BoatRefs>(); var originalRefs = original.GetComponent<BoatRefs>();
            if (refs) refs.masts = new Mast[originalRefs && originalRefs.masts != null ? originalRefs.masts.Length : 0];
            foreach (var m in c.GetComponentsInChildren<MonoBehaviour>(true)) if (m && m.enabled && !FleetLiveNames.Contains(m.GetType().Name)) m.enabled = false;
            foreach (var b in c.GetComponentsInChildren<Behaviour>(true)) if (b && (b is Light || b.GetType().Name == "AudioSource")) b.enabled = false;
            foreach (var rb in c.GetComponentsInChildren<Rigidbody>(true)) FleetHold(rb);
            report = string.Join("; ", done.ToArray()) + ", wheels " + wheels.Count;
            return c;
        }
        // Every serialized field (public or [SerializeField], into [Serializable] classes, arrays and lists, 3 deep) of the scripts under root that points at a
        // scene object outside it (Instantiate remaps only what is inside); the fields of each type kept once.
        private sealed class OutRef { internal Component Comp; internal object Owner; internal System.Reflection.FieldInfo Field; internal int Index = -1; internal Object Target; }
        private static readonly Dictionary<Type, System.Reflection.FieldInfo[]> serializedFields = new Dictionary<Type, System.Reflection.FieldInfo[]>();
        private static System.Reflection.FieldInfo[] SerializedFields(Type type)
        {
            if (serializedFields.TryGetValue(type, out var known)) return known;
            var list = new List<System.Reflection.FieldInfo>();
            for (var t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(Behaviour) && t != typeof(Component) && t != typeof(Object) && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                    if (!f.IsNotSerialized && (f.IsPublic || f.IsDefined(typeof(SerializeField), true)) && !typeof(Delegate).IsAssignableFrom(f.FieldType)) list.Add(f);
            return serializedFields[type] = list.ToArray();
        }
        private static List<OutRef> PointingOut(GameObject root)
        {
            var found = new List<OutRef>();
            foreach (var m in root.GetComponentsInChildren<MonoBehaviour>(true)) if (m) PointingOut(m, m, root.transform, found, 0);
            return found;
        }
        private static bool OutOf(Object o, Transform root)
        {
            var t = o is GameObject g ? g.transform : o is Component k ? k.transform : null;
            return t && t.gameObject.scene.IsValid() && !t.IsChildOf(root);
        }
        private static void PointingOut(object owner, Component comp, Transform root, List<OutRef> found, int depth)
        {
            foreach (var f in SerializedFields(owner.GetType()))
            {
                object v; try { v = f.GetValue(owner); } catch { continue; }
                if (v == null) continue;
                var ft = f.FieldType;
                if (typeof(Object).IsAssignableFrom(ft)) { if (v is Object o && o && OutOf(o, root)) found.Add(new OutRef { Comp = comp, Owner = owner, Field = f, Target = o }); continue; }
                var elem = ft.IsArray ? ft.GetElementType() : ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(List<>) ? ft.GetGenericArguments()[0] : null;
                if (elem != null)
                {
                    if (!(v is IList list)) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var e = list[i]; if (e == null) continue;
                        if (typeof(Object).IsAssignableFrom(elem)) { if (e is Object o && o && OutOf(o, root)) found.Add(new OutRef { Comp = comp, Owner = owner, Field = f, Index = i, Target = o }); }
                        else if (depth < 3 && elem.IsClass && elem != typeof(string) && elem.IsSerializable) PointingOut(e, comp, root, found, depth + 1);
                    }
                    continue;
                }
                if (depth < 3 && ft.IsClass && ft != typeof(string) && ft.IsSerializable) PointingOut(v, comp, root, found, depth + 1);
            }
        }
    }

    // Other mods' hooks on a ship's wake and LoadData that a fleet clone (TraderHulls.FleetClone) must not go through
    // (TRADE_OFFICE_FLEET_DRAWING.md point 3, "With the user's ship mods"; from their decompiles, 2026-11-08):
    //  Shipyard Expansion 0.12.2: its SaveableBoatCustomization.Awake postfix (Patch.PartsPatch.Adder) adds its parts and masts to a hull by the ship's
    //   save number, read from its SaveableObject. The clone has none (SaveableObject.Awake would take the original's save slot), so it threw; given one,
    //   it would add them a second time (the clone carries the original's, cloned). Its LoadData and GetData postfixes read and write the hull's sail
    //   config by the same number (SailDataManager.LoadSailConfig, SaveSailConfig): they threw out of LoadData, so the rig failed and the ship fell
    //   back to its stock rig. Skipped (TraderRigs.PutOn takes that config out first anyway); the ship's sails' angles, jib sides and textures are put
    //   on by PutSails below, from its rig text.
    //  MoreSailwindSails: its Awake postfix (FishermansStayRegistry.Register) would register the Fisherman's Stays again (with a new registry: the
    //   clone's is taken off) beside the original's cloned ones, as Shipwright and Transport found on its copies (Compat.ShipMods).
    // TowableBoats (MooringSet, TowingSet), NANDFixes (its clipping mask on SaveableObject.Awake, its Dhow fix with "Mast item fix" off) and Dinghies
    // (its cutter's masts) don't reach the clone: their scripts are taken off, or the clone has no SaveableObject, or not their ship.
    // A mod not loaded: its hook isn't found, nothing done. Patched once, at the first clone (every plugin is loaded by then).
    internal static class FleetCompat
    {
        private static bool done;
        private static readonly string[][] Hooks =
        {
            new[] { "ShipyardExpansion.Patches.Patch+PartsPatch", "Adder" },
            new[] { "ShipyardExpansion.SailDataManager", "LoadSailConfig" },
            new[] { "ShipyardExpansion.SailDataManager", "SaveSailConfig" },
            new[] { "MoreSailwindSails.Stays.FishermansStay.FishermansStayRegistry", "Register" },
        };
        internal static readonly List<string> Hooked = new List<string>();
        internal static void Ensure()
        {
            if (done) return; done = true;
            var harmony = new Harmony(Plugin.Guid + ".fleetclone");
            foreach (var h in Hooks)
            {
                try
                {
                    var type = TypeOf(h[0]); var method = type != null ? AccessTools.Method(type, h[1]) : null;
                    if (method == null) continue;
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(FleetCompat), nameof(Skip)));
                    Hooked.Add(h[0] + "." + h[1]);
                }
                catch (Exception e) { Plugin.Log?.LogWarning("Visible traders: " + h[0] + "." + h[1] + " not skipped for the fleet's clones (" + e.Message + "); a fleet ship may be drawn on its stock rig."); }
            }
            Plugin.Log?.LogInfo("Visible traders: the fleet's clones skip " + (Hooked.Count > 0 ? string.Join(", ", Hooked.ToArray()) : "no other mod's hook (none loaded)") + ".");
        }
        // The hooked method's first argument (the ship's SaveableBoatCustomization or BoatRefs): false skips it for a fleet clone.
        private static bool Skip(object[] __args) => !(__args != null && __args.Length > 0 && __args[0] is Component c && TraderHulls.IsFleetClone(c));

        // Other mods' lists a clone joins as it wakes and nothing takes it off again: taken off right after the wake (Forget), so the list never
        // holds the clone, alive or destroyed. NANDTweaks' BoatListCreator (a SaveableBoatCustomization.Awake postfix adds every ship's BoatRefs and
        // never removes one; its SaveModData prefix calls GetComponent<PurchasableBoat>().isPurchased() on each): in ofd-6-a a destroyed clone's
        // entry threw there and every save after it died (0 bytes). A list read directly, not its one-line postfix skipped: Mono may inline that.
        private static readonly string[][] Lists = { new[] { "NANDTweaks.Patches.BoatListCreator", "boatList" } };
        private static List<System.Reflection.FieldInfo> lists;
        private static List<System.Reflection.FieldInfo> ListFields()
        {
            if (lists != null) return lists;
            lists = new List<System.Reflection.FieldInfo>();
            foreach (var l in Lists) { var t = TypeOf(l[0]); var f = t != null ? AccessTools.Field(t, l[1]) : null; if (f != null && f.IsStatic && typeof(System.Collections.IList).IsAssignableFrom(f.FieldType)) lists.Add(f); }
            return lists;
        }
        // A type by its full name ("Namespace.Outer+Nested") in the loaded assemblies, or null: each assembly's own lookup by name. Not
        // AccessTools.TypeByName, which on a miss scans every assembly's types and logs a warning: at the first fleet clone of a game without
        // Shipyard Expansion, MoreSailwindSails and NANDTweaks its five misses made one step of 156 ms and five HarmonyX warnings (run of18-1).
        internal static Type TypeOf(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                try { var t = a.GetType(name, false); if (t != null) return t; } catch (Exception) { }
            return null;
        }
        internal static void Forget(GameObject clone)
        {
            if (!clone) return;
            foreach (var f in ListFields())
                try
                {
                    if (!(f.GetValue(null) is System.Collections.IList list)) continue;
                    for (int i = list.Count - 1; i >= 0; i--) if (list[i] is Component c && c && c.transform.IsChildOf(clone.transform)) list.RemoveAt(i);
                }
                catch (Exception e) { Plugin.Log?.LogWarning("Visible traders: a fleet clone not taken off " + f.DeclaringType?.FullName + "." + f.Name + " (" + e.Message + ")."); }
        }
        // For the tests: how many entries of those lists belong to the given object (0 once Forget has run).
        internal static int Listed(GameObject clone)
        {
            int n = 0; if (!clone) return 0;
            foreach (var f in ListFields()) if (f.GetValue(null) is System.Collections.IList list) foreach (var e in list) if (e is Component c && c && c.transform.IsChildOf(clone.transform)) n++;
            return n;
        }

        // Shipyard Expansion's own sail textures on a fleet ship (the user, 2026-11-08 night: "can this be fix"; TRADE_OFFICE_FLEET_DRAWING.md, "Shipyard
        // Expansion's sail textures"). From its decompile (0.12.2): a sail's texture is a name on the sail's SailTextureChanger (textureIndex; the static
        // textures holds each name's texture, filled from every sail prefab's own as PrefabsDirectory starts), put on the cloth's own material by
        // SetTexture (cloth.material.mainTexture) and saved in the hull's config, SEboatSails.<save number>, which a clone can't read (it has no save
        // number; LoadSailConfig is skipped above). So OfficeFleetApi.RigOf writes a sail's texture when it isn't its prefab's own (a "texture" line
        // after its sail line), and the fleet job puts it on the clone's new sail by Shipyard Expansion's own SetTexture right after LoadData, as its
        // LoadData postfix does on a ship; the template and its copy take the cloth's material with it (MakeTemplate, OwnMaterials).
        // Its angles and jib sides the same way (the user, asked whether to carry them: "Look into it"; TRADE_OFFICE_FLEET_DRAWING.md, "Its angles and jib
        // sides"): the sail's SailScaler keeps them (Angle: the local y of the part it turns, a tilt; Flipped: a staysail's or square's inner part turned
        // over to the other side of its stay), put on by its SetAngle and FlipJib(true), in LoadSailConfig's order (angle, jib side, texture). The trader's
        // trim keeps a sail's local y (SailRig.At) and doesn't touch the inner part, so the template's pose carries both.
        private static bool texLooked; private static Type texType, scalerType; private static System.Reflection.FieldInfo texName, texAll; private static System.Reflection.MethodInfo texSet, angleGet, flippedGet, angleSet, flipSet;
        private static bool Textures()
        {
            if (!texLooked)
            {
                texLooked = true;
                if (TraderRigs.HasPlugin(TraderRigs.ShipyardExpansionGuid))
                    try
                    {
                        texType = TypeOf("ShipyardExpansion.Scripts.SailTextureChanger");
                        if (texType != null) { texName = AccessTools.Field(texType, "textureIndex"); texAll = AccessTools.Field(texType, "textures"); texSet = AccessTools.Method(texType, "SetTexture", new[] { typeof(string) }); }
                        scalerType = TypeOf("ShipyardExpansion.SailScaler");
                        if (scalerType != null)
                        {
                            angleGet = AccessTools.PropertyGetter(scalerType, "Angle"); flippedGet = AccessTools.PropertyGetter(scalerType, "Flipped");
                            angleSet = AccessTools.Method(scalerType, "SetAngle", new[] { typeof(float) }); flipSet = AccessTools.Method(scalerType, "FlipJib", new[] { typeof(bool) });
                        }
                    }
                    catch (Exception e) { Plugin.Log?.LogWarning("Visible traders: Shipyard Expansion's sail settings not read (" + e.Message + "); fleet ships show their sails' own."); }
            }
            return texType != null && texName != null && texSet != null;
        }
        private static bool Scalers() => Textures() && scalerType != null && angleGet != null && flippedGet != null && angleSet != null && flipSet != null;
        // A sail's Shipyard Expansion angle (NaN when it is 0, or without it) and jib side.
        internal static void SailShape(GameObject sail, out float angle, out bool flipped)
        {
            angle = float.NaN; flipped = false;
            if (!sail || !Scalers()) return;
            var c = sail.GetComponent(scalerType); if (!c) return;
            float a = angleGet.Invoke(c, null) is float f ? f : 0f; a = Mathf.Repeat(a, 360f);
            if (a > .01f && a < 359.99f) angle = a;
            flipped = flippedGet.Invoke(c, null) is bool b && b;
        }
        // The k-th sail on a ship's mast (its order there: the game's GetData lists them so, LoadData puts them on so), if it is that prefab's.
        internal static GameObject SailOf(BoatRefs refs, int mast, int k, int prefab)
        {
            var m = refs && refs.masts != null && mast >= 0 && mast < refs.masts.Length ? refs.masts[mast] : null;
            var g = m && m.sails != null && k >= 0 && k < m.sails.Count ? m.sails[k] : null; var s = g ? g.GetComponent<Sail>() : null;
            return s && s.prefabIndex == prefab ? g : null;
        }
        // A sail's Shipyard Expansion texture when it isn't its prefab's own; null when it is, or without Shipyard Expansion.
        internal static string SailTexture(GameObject sail, int prefab)
        {
            if (!sail || !Textures()) return null;
            var c = sail.GetComponent(texType); var name = c ? texName.GetValue(c) as string : null;
            if (string.IsNullOrEmpty(name) || name.IndexOf('\n') >= 0) return null;
            var prefabs = PrefabsDirectory.instance ? PrefabsDirectory.instance.sails : null;
            var own = prefabs != null && prefab >= 0 && prefab < prefabs.Length && prefabs[prefab] ? prefabs[prefab].GetComponent(texType) : null;
            return own && texName.GetValue(own) as string == name ? null : name;
        }
        // A rig's Shipyard Expansion settings put on a ship's sails just put on by LoadData (the fleet job's clone), in LoadSailConfig's order: each
        // sail's angle, jib side, texture. Returns what was put ("2 textures, 1 angle, 1 jib side"; null: nothing to put); missed, what wasn't and why
        // (null: none).
        internal static string PutSails(BoatRefs refs, RigPlan p, out string missed)
        {
            int textures = 0, angles = 0, flips = 0; var miss = new List<string>(); var onMast = new Dictionary<int, int>();
            foreach (var s in p.Sails)
            {
                int k = onMast.TryGetValue(s.Mast, out var n) ? n : 0; onMast[s.Mast] = k + 1;
                bool shape = !float.IsNaN(s.Angle) || s.Flipped;
                if (string.IsNullOrEmpty(s.Texture) && !shape) continue;
                string which = "sail " + s.Prefab + " on mast " + s.Mast;
                if (!Textures()) { miss.Add(which + " (Shipyard Expansion not loaded)"); continue; }
                var sail = SailOf(refs, s.Mast, k, s.Prefab);
                if (!sail) { miss.Add(which + " (not found)"); continue; }
                if (shape)
                {
                    var sc = Scalers() ? sail.GetComponent(scalerType) : null;
                    if (!sc) miss.Add(which + "'s angle and jib side (no SailScaler)");
                    else
                        try
                        {
                            if (!float.IsNaN(s.Angle)) { angleSet.Invoke(sc, new object[] { s.Angle }); angles++; }
                            if (s.Flipped) { flipSet.Invoke(sc, new object[] { true }); flips++; }
                        }
                        catch (Exception e) { miss.Add(which + "'s angle or jib side (" + (e.InnerException ?? e).Message + ")"); }
                }
                if (string.IsNullOrEmpty(s.Texture)) continue;
                var c = sail.GetComponent(texType);
                if (!c) { miss.Add(which + "'s texture " + s.Texture + " (no texture changer)"); continue; }
                // not among its textures: SetTexture would log an error and change nothing
                if (texAll?.GetValue(null) is System.Collections.IDictionary all && !all.Contains(s.Texture)) { miss.Add(which + "'s texture " + s.Texture + " (not among its textures)"); continue; }
                try { texSet.Invoke(c, new object[] { s.Texture }); textures++; }
                catch (Exception e) { miss.Add(which + "'s texture " + s.Texture + " (" + (e.InnerException ?? e).Message + ")"); }
            }
            missed = miss.Count > 0 ? string.Join(", ", miss.ToArray()) : null;
            var put = new List<string>();
            if (textures > 0) put.Add(textures + (textures == 1 ? " texture" : " textures"));
            if (angles > 0) put.Add(angles + (angles == 1 ? " angle" : " angles"));
            if (flips > 0) put.Add(flips + (flips == 1 ? " jib side" : " jib sides"));
            return put.Count > 0 ? string.Join(", ", put.ToArray()) : null;
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
            internal LanePath Path; internal int From = -1, To = -1; internal float S;     // the trip, approach point to approach point
            // S as the clock last set it, exactly (Clocked): on a lane over 1,000 km (a 4x Scrambled Seas map) a frame's way is under a
            // float's step at S, and S alone would stop growing
            internal double SExact; internal float SKept = float.NaN;
            internal LanePath Leg; internal float U; internal TraderBerth Berth; internal int Slot = -1, Port = -1;   // a harbour leg; the berth or anchorage place held (Slot: the place's number there, -1 none)
            internal Vector2 AnchorAt; internal bool Shares;   // its anchorage place (x across the wind, y along it: Rules.AnchorPlace); sharing another trader's (past the places)
            // Its place (real). PX, PZ hold it exactly: on a 4x Scrambled Seas map past 524 km from the real origin a float holds a place only
            // every 6.25 cm and a frame's step under half of that was lost, so a trader stood still at high frame rates (SCRAMBLED_TRADERS_DRAFT.md
            // §6, "Measured, 2026-11-08"; the user: "Build the fix"). Pos is that place as floats, for every reader; a set puts it exactly.
            internal double PX, PZ;
            internal Vector2 Pos { get => new Vector2((float)PX, (float)PZ); set { PX = value.x; PZ = value.y; } }
            internal float Yaw, Pitch, Roll, Offset, Avoid, AvoidTill, Unroll, Speed, Wind, NextWind = -1, NextContact, NextCheck;
            internal Vector2 Velocity; internal int Side;
            internal bool Placed;
            internal bool Settled;     // moored or anchored once in this stay: its wait runs (Rules.WaitHeld)
            internal int LastGood;     // the main good of the trip it sails or last sailed (Rules.DeckGood)
            internal float Heave = float.NaN, NextShips;
            internal float FurledSince = -1; internal bool SailsParked;   // its reef animators culled while furled in port (TraderHulls.ParkSails)
            internal int ShowStep;
            internal Vector3 TargetReal; internal Quaternion TargetRot;
            internal double TX, TZ;   // TargetReal's x and z exactly (Pose); the copy is drawn from them (ShiftedTarget)
            // Its sails' self-trim (TRADER_TRIM_DRAFT.md; nothing of it saved, set afresh when it is drawn): the apparent wind (its
            // wind less its way through the water, world) and its angle off the bow (+ over the port side); Side (above) the side the
            // sails go to with the wind's buffer (Rules.SailSideHeld, the centreline), SideFor its timer; each sail's own side and
            // timer (by the template's order); the goosewing split, its timer and the side its aftmost sail keeps; the square sails'
            // set state at sea, its timer, their own unroll; the phase the trim last saw (the approach point passed).
            internal Vector3 Apparent; internal float Awa, SideFor; internal int[] SailSides; internal float[] SailSideFor;
            internal bool Goose; internal float GooseFor; internal int GooseSide;
            internal bool SquareSet; internal float SquareFor, SquareUnroll; internal Phase TrimPhase;
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
            // its band by the player's reputation and its state against the player's ships (TraderRep, at this file's end; nothing saved)
            internal readonly TraderRep.State Rep = new TraderRep.State();
        }
        // This frame's traffic seconds (World.Tick sets it; 0 while the fleet stands still: paused, a shipyard, the trade book).
        internal static double TrafficDt;
        private static readonly Dictionary<int, Visual> shown = new Dictionary<int, Visual>();
        private static readonly Dictionary<int, int> berthHolders = new Dictionary<int, int>();   // port * 1000 + berth number -> boat id
        private static readonly Dictionary<int, List<Visual>> anchorPlaces = new Dictionary<int, List<Visual>>();   // port -> every trader holding an anchorage place there, sharers too
        private static readonly Dictionary<int, IslandHorizon> islandOf = new Dictionary<int, IslandHorizon>();
        // all layers are asked (the copies, the player's ships and the game's NPC boats share layer 0 with the docks), so the buffer
        // is large enough that a port's dock colliders cannot push a ship out of it
        private static readonly Collider[] found = new Collider[256];
        private static float nextScan;
        private static bool? scrambled;
        private static bool askedEarly; private static float nextEarly;
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

        // An independent's rank among the nine by id, kept per fleet as the plans are (a scan asks for every trader near each half second).
        private static object rankFleet; private static int rankFleetCount;
        private static readonly Dictionary<int, int> rankCache = new Dictionary<int, int>();
        internal static int HullOf(BoatState b)
        {
            // a trade fleet ship (OfficeFleet): its own hull (its record's group and home are its kind's, not its hull's); -1 not drawn
            int mine = OfficeFleet.IndexOf(b);
            if (mine >= 0) { var s = World.State.office; return s?.fleetHull != null && mine < s.fleetHull.Length && HullData.Hulls.Contains(s.fleetHull[mine]) ? s.fleetHull[mine] : -1; }
            int rank = -1;
            if (b.group == "independent")
            {
                var fleet = World.State.fleet;
                if (!ReferenceEquals(rankFleet, fleet) || rankFleetCount != fleet.Count) { rankCache.Clear(); rankFleet = fleet; rankFleetCount = fleet.Count; }
                if (!rankCache.TryGetValue(b.id, out rank))
                    rankCache[b.id] = rank = fleet.Where(x => x.group == "independent").OrderBy(x => x.id).ToList().FindIndex(x => x.id == b.id);
            }
            return Rules.TraderHull(b.group, b.home, rank);
        }
        // A trader's sail plan (Rules.TraderPlan): the independents by their rank among the nine by id, the small locals by their
        // place from the first among their region's by id (not the FFL pair); ids are saved, so the same traders carry the plans
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
            int rank = -1, place = -1;
            if (b.group == "independent") rank = fleet.Where(x => x.group == "independent").OrderBy(x => x.id).ToList().FindIndex(x => x.id == b.id);
            else if (b.group == "small" && !ffl)
                place = fleet.Where(x => x.group == "small" && x.home == b.home && !(x.network != null && x.network.Any(p => p >= 22 && p <= 25))).OrderBy(x => x.id).ToList().FindIndex(x => x.id == b.id);
            return Rules.TraderPlan(b.group, b.home, ffl, rank, place);
        }
        // Its pool's key: its plan where that plan's template was taken (a plan not taken leaves its traders on the stock rig).
        internal static string KeyOf(BoatState b)
        {
            int hull = HullOf(b); if (hull < 0) return null;
            // a trade fleet ship: its own template, as it was rigged (TraderHulls.FleetStep; none until that and its copy are made), or its hull's stock
            // rig when its own can't be drawn
            if (OfficeFleet.IndexOf(b) >= 0) return TraderHulls.FleetDrawKey(b.id, hull);
            string plan = PlanOf(b);
            return plan != null && TraderHulls.Template(plan) != null ? plan : Rules.TraderKey(null, hull);
        }
        // Its helmsman's outfit, chosen at each draw (not saved: the user, "every time it could be different NPC model"), never one
        // another drawn trader wears (Rules.PickOutfit).
        private static void Wear(BoatState b, TraderCopy copy)
        {
            var all = TraderHulls.Outfits; if (all.Count == 0 || !copy.HelmLook) return;
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
        // Update's traders read the lag and the player's place once for the frame (Frame): nothing in that part moves the world or the
        // player (each trader read them two or three times a frame); outside it they are read at each call.
        private static bool framed; private static Vector3 frameLag; private static Vector2 framePlayer;
        private static void Frame(bool on)
        {
            framed = false;
            if (on) { frameLag = WorldLag(); framePlayer = PlayerReal(); framed = true; }
        }
        internal static Vector3 WorldLag() => framed ? frameLag : MeasureLag();
        private static Vector3 MeasureLag()
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
            if (framed) return framePlayer;
            var p = FloatingOriginManager.instance.ShiftingPosToRealPos(Refs.observerMirror.transform.position - WorldLag());
            return new Vector2(p.x, p.z);
        }
        private static Vector3 Shift(Vector2 real, float y = 0) => Shifted(new Vector3(real.x, y, real.y));
        internal static Vector3 Shifted(Vector3 real) => FloatingOriginManager.instance.RealPosToShiftingPos(real) + WorldLag();
        // A trader's drawn place from its exact one (Pose's TX, TZ), as Shifted does (the real place plus the world's offset): the offset is a
        // float of whole 512 m shifts, exact in a double, so the small shifted place keeps the double's precision far from the real origin (the
        // 4x stall: through Shifted(TargetReal) the hull moved in 6.25 cm steps there).
        private static Vector3 ShiftedTarget(Visual v)
        {
            var o = FloatingOriginManager.instance.outCurrentOffset; var lag = WorldLag();
            return new Vector3((float)(v.TX + o.x) + lag.x, v.TargetReal.y + o.y + lag.y, (float)(v.TZ + o.z) + lag.z);
        }
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
        // (SaveLoadManager.npcBoats: GetSaveData and LoadSaveData read only fields). Back on with the setting off, after "Prepare to
        // uninstall", or under Scrambled Seas until the routes for its map are ready (for that whole game if they fail); with the mod
        // removed the scene has them on.
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
        // Whether a ship is one of the game's NPC boats kept off (NpcBoatHiddenPatch): its parts looked at once a ship (the game's
        // switcher asks each frame for every ship near the player), forgotten at a load.
        private static readonly Dictionary<int, bool> npcBoatOf = new Dictionary<int, bool>();
        internal static bool GameNpcBoat(Transform boat)
        {
            int id = boat.GetInstanceID();
            if (npcBoatOf.TryGetValue(id, out bool known)) return known;
            if (npcBoatOf.Count > 4096) npcBoatOf.Clear();
            return npcBoatOf[id] = (boat.GetComponent<NPCBoatController>() || boat.GetComponent<NPCFishingBoat>()) && !OtherModsBoat(boat);
        }
        // At a load or a new game: the registered ships looked up again, and an error logged again (once a game). The copies' marks
        // of the ships they passed through stay (Meet: a ship the player owns since is made solid again).
        internal static void Reset()
        {
            ReleaseAll(); TraderHulls.FleetAbort(); TraderRep.Reset(); keptBerth.Clear(); scrambled = null; askedEarly = false; islandOf.Clear(); islandMiss.Clear(); failedOnce = false; nextRegistered = 0; nextHulls = -1; hullPartOf.Clear(); npcBoatOf.Clear();
            // a scrambled map's routes under way are dropped, and the vanilla lanes are used until the next first scan decides
            ScrambledLanes.Cancel(); LaneMap.Use(null);
        }
        private static void ReleaseAll() { foreach (var v in shown.Values.ToList()) Release(v); berthHolders.Clear(); anchorPlaces.Clear(); toBegin.Clear(); }
        // The traders the last scan picked to be drawn, nearest first, not yet taken from the pool (Update takes one a frame).
        private static readonly List<BoatState> toBegin = new List<BoatState>();
        private static readonly List<Visual> stepping = new List<Visual>();   // the drawn traders stepped this frame

        internal static void Update()
        {
            long t0 = FrameParts.Now;
            try { UpdateParts(); TraderRep.Tick(); TraderBubble.Step(); }
            finally { FrameParts.End(t0, shown.Count); }
        }
        // The user's 10 (2026-11-02, "Yes, go"): on a scrambled map the routes are asked for as soon as the islands are moved, while
        // the game loads or prepares its economy (8-9 s at a new game), not at the first scan after; that scan asks again and the work
        // under way for the same islands' places goes on (ScrambledLanes.Request). Looked at twice a second, only while a game starts.
        private static void AskEarly()
        {
            if (askedEarly || scrambled != null || !(Startup.IsPreparing || GameState.currentlyLoading || World.Loading)) return;
            if (!FloatingOriginManager.instance || !LaneMap.Loaded || Refs.islands == null || Time.realtimeSinceStartup < nextEarly) return;
            nextEarly = Time.realtimeSinceStartup + .5f;
            double moved = MapStretch.MaxMove(); if (moved <= 50) return;
            askedEarly = true;
            Plugin.Log?.LogInfo("Visible traders: the ports are moved (by " + moved.ToString("F0") + " m) while the game " + (Startup.IsPreparing ? "prepares its economy" : "loads") + ": the routes for this map are asked for now.");
            try { ScrambledLanes.Request(ScrambledInput.Read()); }
            catch (Exception e) { Plugin.Log?.LogError("Visible traders: the routes for this map could not be started early (they are asked for again once play begins): " + e); }
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
            { if (shown.Count > 0) ReleaseAll(); AskEarly(); return; }
            if (scrambled == null)
            {
                // Under Scrambled Seas (a port moved from its vanilla place relative to port 0) the routes for this map are made on a
                // thread (ScrambledLanes, SCRAMBLED_TRADERS_DRAFT.md section 2); no trader is drawn until they are ready (S2, the user:
                // "draw no trader until the route is ready"). Was: no trader drawn in a scrambled game.
                double moved = MapStretch.MaxMove();
                scrambled = moved > 50;
                if (scrambled.Value)
                {
                    Plugin.Log?.LogInfo("Visible traders: the ports are moved (by " + moved.ToString("F0") + " m): the routes for this map are being made; no trader is drawn until they are ready.");
                    try { ScrambledLanes.Request(ScrambledInput.Read()); }
                    catch (Exception e) { Plugin.Log?.LogError("Visible traders: the routes for this map could not be started: " + e); }
                }
                else { if (askedEarly) ScrambledLanes.Cancel(); LaneMap.Use(null); Plugin.Log?.LogInfo("Visible traders: on."); }
            }
            if (scrambled.Value)
            {
                var map = ScrambledLanes.Ready;
                if (map != null && LaneMap.Scrambled != map)
                {
                    LaneMap.Use(map);
                    Plugin.Log?.LogInfo("Visible traders: the routes for this map are ready (" + (Time.realtimeSinceStartup - ScrambledLanes.RequestedAt).ToString("F1") + " s after they were asked for): traders are drawn.");
                }
                // (the flag too: a game before in the session may have left it on with no boat in the list, and the game's distance
                // switcher's prefix would then keep them off here: the deep review of 2026-10-19)
                if (map == null)
                {
                    if (ScrambledLanes.Failure != null && !failedOnce) { failedOnce = true; Plugin.Log?.LogError("Visible traders: the routes for this map failed; no trader is drawn in this game: " + ScrambledLanes.Failure); }
                    if (npcOff.Count > 0 || NpcBoatsHidden) NpcBoats(false);
                    if (shown.Count > 0) ReleaseAll();
                    return;
                }
            }
            // the trade fleet's own templates and copies, a step a frame, once the pool and the goods' looks are made, and not in a frame that takes a
            // trader from the pool (TraderHulls.FleetStep)
            if (TraderHulls.PoolDone && TraderHulls.LooksReady && toBegin.Count == 0) { t = FrameParts.Now; if (TraderHulls.FleetStep()) FrameParts.Add(FrameParts.Build, t); }
            t = FrameParts.Now; bool searched = NpcBoats(true); FrameParts.Add(FrameParts.Npc, t);
            watch.Restart();
            try
            {
                Frame(true);
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
            finally { Frame(false); }
            LastFrameMs = watch.Elapsed.TotalMilliseconds; if (LastFrameMs > MaxFrameMs) MaxFrameMs = LastFrameMs;
        }

        // The drawn place, every frame (Update): the copy's transform set where the trader is (no interpolation to blend it across a
        // world shift). The first time, its body too.
        private static void Place(Visual v)
        {
            var c = v.Copy; if (!c.Go) return;
            var at = ShiftedTarget(v);
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
                c.Body.MovePosition(ShiftedTarget(v) + new Vector3(v.Velocity.x, 0f, v.Velocity.y) * ahead);
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
            void Consider(BoatState b)
            {
                Vector2 at;
                if (shown.TryGetValue(b.id, out var v)) at = v.Pos; else if (!PlaceOf(b, out at)) return;
                float d = (at - player).magnitude;
                if (d > (v != null ? Rules.TraderHide : Rules.TraderShow)) return;
                int hull = HullOf(b); string key = v != null ? v.Key : KeyOf(b); if (hull < 0 || TraderHulls.Template(key) == null) return;
                want.Add(new Want { B = b, D = d, Hull = hull, Key = key, Drawn = v != null, Sight = seeing && (v != null ? InSight(v) : PlaceInSight(b, hull, at)) });
            }
            foreach (var b in World.State.fleet) Consider(b);
            // and the trade offices' own ships (OfficeFleet), each on its own template (a pool of one)
            var mine = World.State.playerFleet;
            if (mine != null) foreach (var b in mine) Consider(b);
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
            v.Path = p; v.From = b.origin; v.To = b.destination;
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
                v.Port = b.origin;
                // first seen in port it is already in its place: the berth it was let go at in this stay while that is free (Q5),
                // else a berth while its island is loaded (a load, the player close by), else the anchorage (the draft's question 11
                // and first step: the berth only where the island is loaded)
                var kept = KeptBerth(b);
                // (not a berth kept for the player from this trader: TraderRep.KeptFor)
                if (kept != null && !(berthHolders.TryGetValue(kept.Port * 1000 + kept.Number, out int holder) && holder != b.id) && !ShipIn(kept) && !TraderRep.KeptFor(v, kept))
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
                // furled at its berth or the anchorage; square sails only: set while it waits (Rules.SailsOut)
                v.Unroll = Rules.SailsOut(copy.Template.AllSquare, false, v.Phase == Phase.Moored || v.Phase == Phase.Anchored) ? 1 : 0;
            }
            copy.Boat = b; shown[b.id] = v;
            Wear(b, copy);
            v.Placed = false; v.ShowStep = 0;
            Pose(v, 0, 0, true);
            // first drawn in port, its wait has been running unseen: it goes on
            if (b.destination < 0) v.Settled = true;
            ReadStorms(); TraderHulls.SetLit(copy, Lit(v.Pos));
            SetDeck(v);
            Trim(v, v.Unroll > .5f, 0f, 1f, true);
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
                        {
                            // from the exact place while S still holds what the clock stored (S set elsewhere since: from S); backing away
                            // from the player's ship (TraderRep.Speed: its speed negative) never past its trip's start
                            double s = Math.Max(0, Math.Min((v.S == v.SKept ? v.SExact : v.S) + d, v.Path.Length));
                            v.SExact = s; v.S = v.SKept = (float)s;
                            b.remaining = Rules.TripRemaining(b.trip, s, v.Path.Length);
                        }
                        return true;
                    case Phase.LegOut:
                        v.U = v.Back ? Mathf.Max(v.BackTo, v.U - d) : Mathf.Clamp(v.U + d, 0f, v.Leg.Length);   // (backing for the player: never past the leg's start)
                        // the trip's timer starts at the approach point, where today's straight line starts (VISIBLE_TRADERS.md)
                        if (b.destination >= 0) { b.remaining = b.trip; return true; }
                        // going to the approach point to choose again: its wait held until it is moored or anchored (it runs on for
                        // one that was: Settled)
                        return Rules.WaitHeld(true, v.Settled);
                    case Phase.LegIn:
                        if (v.Back) v.U = Mathf.Max(v.BackTo, v.U - d);
                        else v.U = Mathf.Clamp(v.U + d * Mathf.Clamp((v.Leg.Length - v.U) / 30f, .25f, 1f), 0f, v.Leg.Length);
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
                // (a trader that yielded its berth to the player by its usual way out goes to the anchorage: TraderRep, YieldBerth)
                if (h != null && TakePlace(v, h, PortLoaded(v.Port) && !v.Rep.ToAnchor, true)) { v.Rep.ToAnchor = false; StartLeg(v, v.Berth != null ? v.Berth.In(v.Way) : h.AnchorIn(v.Way), v.Way, Phase.LegIn); }
                // (also a port whose anchorage is closed while its island isn't loaded: Scrambled Seas can close an anchorage and keep berths,
                // and the trader waited at the approach point for good, its wait held; the deep review of 2026-11-09)
                else if (h == null || h.AnchorLeg == null && (h.Berths.Count == 0 || !PortLoaded(v.Port))) v.Settled = true;
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
                // (and at 7-10 a berth by the office kept for the player coming in: R21 (a), "no 7-10 trader takes any of the 2 or 3 berths")
                if (v.Berth != null && (ShipIn(v.Berth) || TraderRep.KeptFor(v, v.Berth))) GoBack(v, 0f);
                else if (v.Berth == null && v.Slot >= 0 && h != null && PlaceHeld(v, h, v.AnchorAt)) { if (!MovePlace(v, h)) GoBack(v, 0f); }
            }
            // a ship in a moored trader's berth (a recovery, the player mooring there): it goes to another berth or the anchorage
            // (the user, 2026-10-17), out by a clear way; with none clear it stays (and moves nothing into the ship, part 2)
            if (v.Phase == Phase.Moored && b.destination < 0 && Time.time >= v.NextCheck)
            {
                v.NextCheck = Time.time + 2f;
                int way = ShipIn(v.Berth) ? OutWay(v, 0) : 0;
                if (way > 0) { var leg = v.Berth.Out(way); FreePlace(v); StartLeg(v, leg, way, Phase.LegOut); }
            }
            // at 7-10, moored in a berth by the port's trade office kept for the player, the player's ship within 60 m: it yields the berth
            // (TRADER_REPUTATION_DRAFT.md 5.2; looked at twice a second)
            if (v.Phase == Phase.Moored && TraderRep.YieldDue(v)) YieldBerth(v);
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
                // sharing a place (past the anchorage's places): one that has freed, if any (ANCHORAGE_SPREAD_DRAFT.md, (a)); where the
                // place it shares has no holder left (its holder gone, its hull too big to take it over), a held one to share
                else if (v.Shares && h != null)
                {
                    if (FindPlace(v, h, out var freed, out _, false)) { v.AnchorAt = freed; v.Shares = false; }
                    else if (!anchorPlaces[v.Port].Any(o => !o.Shares && (o.AnchorAt - v.AnchorAt).sqrMagnitude < .01f) && FindPlace(v, h, out var other, out bool still)) { v.AnchorAt = other; v.Shares = still; }
                }
            }
            FrameParts.Add(FrameParts.Berths, t); t = FrameParts.Now;
            v.Speed = (float)Rules.TraderSpeed(b.speed, v.Wind, Vector2.Dot(DirOf(v.Yaw), WindBlows()));
            float own = v.Speed;
            if (v.Phase != Phase.Sail && v.Phase != Phase.LegOut && v.Phase != Phase.LegIn) v.Speed = 0;
            // in port: waiting on its leg it stands; a player's ship blocking the rest of its way, it stops 8 m short (part 2)
            else if (v.Phase != Phase.Sail && !v.Back)
            {
                if (v.HoldAt >= 0) v.Speed = 0;
                else if (v.StopGap >= 0) v.Speed = (float)Rules.InPortSpeed(v.Speed, v.StopGap);
            }
            // by the player's reputation (TraderRep.Speed): giving way at 7-10 (slowing, stopped 20 m short, backing: negative), stopped short
            // of a ship it can't get round at sea, gathering speed as its sails set again
            v.Speed = TraderRep.Speed(v, v.Speed, own, dt);
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
        // A 7-10 trader yields a berth by the port's trade office to the player (TRADER_REPUTATION_DRAFT.md 5.2): one of the berth lines
        // shown 30 s, and off to the anchorage: back along its berth's direct way where that is clear of the player's ships (it walks the way
        // the anchored come in by, its berth let go at once; BackToAnchorage anchors it), else out by its usual way and in to the anchorage
        // from the approach point (ToAnchor). With neither clear it stays and looks again (TraderRep.YieldDue). Its wait runs on throughout
        // (the user's rule of 2026-11-08: "wait in anchorage count as wait time").
        private static void YieldBerth(Visual v)
        {
            var h = LaneMap.HarbourOf(v.Port); var berth = v.Berth; if (h == null || berth == null || h.AnchorLeg == null) return;
            string how;
            if (berth.Direct != null && WayClear(v, berth.Direct.Reversed(), true))
            {
                FreePlace(v); StartLeg(v, berth.Direct, 3, Phase.LegIn, berth.Direct.Length); GoBack(v, 0f);
                how = "back along its direct way to the anchorage";
            }
            else
            {
                int way = OutWay(v, 0); if (way == 0) return;
                var leg = berth.Out(way); FreePlace(v); StartLeg(v, leg, way, Phase.LegOut); v.Rep.ToAnchor = true;
                how = "out by its way " + way + ", then to the anchorage";
            }
            TraderBubble.Say(v, TraderBubble.KindBerth);
            Plugin.Log?.LogInfo("Trader reputation: trader " + v.Boat.id + " (" + v.Boat.group + ", hull " + v.Hull + ") yields berth " + berth.Number + " at port " + v.Port + " to the player: " + how + ".");
        }

        // ---- In port: the player's ships (TRADER_COLLISION_DRAFT.md, the user's answers of 2026-10-21) ----
        // The player's ships by their whole hull (part 3, C3): every registered ship the player bought, and the one the player is
        // aboard; only these (the user: "Player model shouldn't be recognized by trader, only player ship"). Each by the box of its
        // hull mesh, the mesh the game's HullPlayerCollider copies for the swimming player (on every ship, modded ones too), turned
        // with the hull; a ship without one by its Boat-tagged capsule. Looked up again every 0.5 s (the user, R16 of
        // TRADER_REPUTATION_DRAFT.md: "we make it 0.5 sec per scan, test if this cause problem"; 4 times a second before), with each ship's
        // velocity (its body's; PlayerVelocities, in the same order), the box of the ship the player is aboard (Aboard; null when not
        // aboard) and of the last ship the player was aboard while it is still the player's (LastShip, its velocity LastShipVelocity).
        private static readonly List<HullBox> playerHulls = new List<HullBox>();
        private static readonly List<Vector2> playerVelocities = new List<Vector2>();
        private static readonly List<Transform> playerShips = new List<Transform>();
        private static float nextHulls = -1;
        private static readonly Dictionary<int, Component> hullPartOf = new Dictionary<int, Component>();
        private static readonly Dictionary<int, Rigidbody> bodyOfShip = new Dictionary<int, Rigidbody>();
        private static Transform lastAboard;
        // how often the list is made (R16: 0.5 s); the QA's vtrep sets 0.25 s to compare the built rate with
        internal static float HullsEvery = .5f;
        internal static IList<Vector2> PlayerVelocities => playerVelocities;
        internal static float HullsAt { get; private set; }
        internal static HullBox Aboard { get; private set; }
        internal static HullBox LastShip { get; private set; }
        internal static Vector2 LastShipVelocity { get; private set; }
        internal static List<HullBox> PlayerHulls()
        {
            if (Time.time < nextHulls) return playerHulls;
            nextHulls = Time.time + HullsEvery; HullsAt = Time.time; playerHulls.Clear(); playerVelocities.Clear(); playerShips.Clear(); Aboard = LastShip = null; LastShipVelocity = Vector2.zero;
            if (!FloatingOriginManager.instance) return playerHulls;
            foreach (var p in RegisteredShips()) if (p && PlayersShip(p.transform)) playerShips.Add(p.transform);
            var on = GameState.currentBoat; Transform aboard = null;
            if (on)
            {
                for (var x = on; x && x != Refs.shiftingWorld && !aboard; x = x.parent) if (x.GetComponent<PurchasableBoat>() || x.GetComponent<BoatRefs>()) aboard = x;
                if (aboard && !playerShips.Contains(aboard)) playerShips.Add(aboard);
                if (aboard) lastAboard = aboard;
            }
            foreach (var s in playerShips)
            {
                var box = HullOf(s); if (box == null) continue;
                var vel = VelocityOf(s); playerHulls.Add(box); playerVelocities.Add(vel);
                if (s == aboard) Aboard = box;
                if (s == lastAboard) { LastShip = box; LastShipVelocity = vel; }
            }
            return playerHulls;
        }
        // A ship's velocity on the water: its own body's, else its mooring ropes' boat body, else the first body under it (as the QA's stand-ins).
        private static Vector2 VelocityOf(Transform ship)
        {
            int id = ship.GetInstanceID();
            if (!bodyOfShip.TryGetValue(id, out var body) || !body)
            {
                body = ship.GetComponent<Rigidbody>();
                if (!body) { var ropes = ship.GetComponent<BoatMooringRopes>(); body = ropes && ropes.ropes != null && ropes.ropes.Length > 0 && ropes.ropes[0] ? ropes.ropes[0].GetBoatRigidbody() : ship.GetComponentInChildren<Rigidbody>(); }
                if (bodyOfShip.Count > 256) bodyOfShip.Clear();
                bodyOfShip[id] = body;
            }
            return body ? new Vector2(body.velocity.x, body.velocity.z) : Vector2.zero;
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
            leg != null && !TraderWays.Look(leg, 0f, placeAtEnd, PlayerHulls(), v.Size.x, v.Size.y, 0f, TraderRep.PassMargin(v), TraderRep.Spare(v)).Blocked;
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
        // The anchorage's places (ANCHORAGE_SPREAD_DRAFT.md; the user, 2026-11-08: "6 place, also implemet a and b"): by hull size
        // inside its circle (Rules.AnchorPlace; the three main cities' circles hold 6 medium hulls, the others 3), never one a player's
        // ship holds (part 2). Past them a trader shares a place another holds and they pass through each other (the user's rules:
        // "if there is no more berth, let the new trader spawn at anchorage", and traders pass through each other); every sharer is
        // recorded, takes the place over when its holder leaves (FreePlace), and takes a place that frees (the Anchored check, every
        // 2 s). A place: x across the wind, y along it, from the anchorage's centre (the places turn with the wind).
        private static Vector2 AnchorWorld(TraderHarbour h, Vector2 at) { var into = -WindBlows(); return h.Anchorage + Across(into) * at.x + into * at.y; }
        // a player's ship holds a place when its hull lies within half the trader's length + 2 m of it (part 2)
        private static bool PlaceHeld(Visual v, TraderHarbour h, Vector2 at)
        {
            var p = AnchorWorld(h, at);
            foreach (var s in PlayerHulls()) if (Rules.SlotHeld(p.x, p.y, s, v.Size.x)) return true;
            return false;
        }
        // The place this trader would take: a free one for its size, else (share) one another trader holds, picked by its id among
        // those no player's ship holds; false when the player's ships hold them all (not sharing: when none is free).
        private static bool FindPlace(Visual v, TraderHarbour h, out Vector2 at, out bool shares, bool share = true)
        {
            at = Vector2.zero; shares = false;
            var holders = anchorPlaces.TryGetValue(h.Port, out var list) ? list.Where(o => o != v && !o.Shares).ToList() : new List<Visual>();
            var placed = holders.Select(o => new double[] { o.AnchorAt.x, o.AnchorAt.y, o.Size.x / 2, o.Size.y / 2 }).ToList();
            if (Rules.AnchorPlace(h.AnchorRadius, placed, v.Size.x, v.Size.y, (x, y) => PlaceHeld(v, h, new Vector2((float)x, (float)y)), out double px, out double py))
            { at = new Vector2((float)px, (float)py); return true; }
            if (!share) return false;
            var open = holders.Where(o => !PlaceHeld(v, h, o.AnchorAt)).ToList();
            if (open.Count == 0) return false;
            at = open[(v.Boat.id % open.Count + open.Count) % open.Count].AnchorAt; shares = true; return true;
        }
        private static void HoldPlace(Visual v, int port, Vector2 at, bool shares)
        {
            if (!anchorPlaces.TryGetValue(port, out var list)) anchorPlaces[port] = list = new List<Visual>();
            int n = 0; while (list.Any(o => o.Slot == n)) n++;
            list.Add(v); v.Slot = n; v.AnchorAt = at; v.Shares = shares;
        }
        // on its way in, a player's ship come into its place: another (false: none, and it goes back)
        private static bool MovePlace(Visual v, TraderHarbour h)
        {
            FreePlace(v);
            if (!FindPlace(v, h, out var at, out bool shares)) return false;
            HoldPlace(v, h.Port, at, shares); return true;
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
            // at 7-10 it gives way to a player's ship in its way (TraderRep.PortGiveWay); else these harbour rules, at 7-10 with 10 m from the
            // ship where the water allows and single file sooner (TRADER_REPUTATION_DRAFT.md 5.1, R18)
            if (TraderRep.PortGiveWay(v)) return;
            var look = TraderWays.Look(v.Leg, v.U, v.Phase == Phase.LegIn, PlayerHulls(), v.Size.x, v.Size.y, v.Offset, TraderRep.PassMargin(v), TraderRep.Spare(v));
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
            return FindPlace(v, h, out _, out _) && (WayClear(v, h.AnchorIn(1), true) || WayClear(v, h.AnchorIn(2), true));
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
        // A berth by the port's trade office kept for the player counts as taken for a 7-10 trader (TraderRep.KeptFor; TRADER_REPUTATION_DRAFT.md 5.2).
        private static bool BerthTaken(Visual v, TraderBerth x) =>
            berthHolders.TryGetValue(x.Port * 1000 + x.Number, out int id) && id != v.Boat.id || !Rules.BerthFits(x.Length, x.Beam, v.Size.x, v.Size.y) || ShipIn(x) || TraderRep.KeptFor(v, x);
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
        // The berth (when berth: its island loaded), else the anchorage (a place by its size, else a shared one, none a player's ship
        // holds: FindPlace).
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
            // past its places they share them and pass through each other (the user's rule), never one a player's ship holds (part 2)
            if (!FindPlace(v, h, out var at, out bool shares)) return false;
            HoldPlace(v, h.Port, at, shares);
            v.Berth = null; v.Port = h.Port; v.Way = way;
            return true;
        }
        private static void FreePlace(Visual v)
        {
            if (v.Berth != null && berthHolders.TryGetValue(v.Berth.Port * 1000 + v.Berth.Number, out int id) && id == v.Boat.id) berthHolders.Remove(v.Berth.Port * 1000 + v.Berth.Number);
            // its anchorage place let go; a trader sharing it takes it over where its own hull fits there (else the Anchored check finds
            // it a place for its hull, or a held one to share)
            if (v.Slot >= 0 && v.Port >= 0 && anchorPlaces.TryGetValue(v.Port, out var list) && list.Remove(v) && !v.Shares)
            {
                var h = LaneMap.HarbourOf(v.Port);
                var placed = list.Where(o => !o.Shares).Select(o => new double[] { o.AnchorAt.x, o.AnchorAt.y, o.Size.x / 2, o.Size.y / 2 }).ToList();
                var heir = h == null ? null : list.FirstOrDefault(o => o.Shares && (o.AnchorAt - v.AnchorAt).sqrMagnitude < .01f && Rules.AnchorFits(h.AnchorRadius, placed, o.Size.x, o.Size.y, o.AnchorAt.x, o.AnchorAt.y));
                if (heir != null) heir.Shares = false;
            }
            v.Berth = null; v.Slot = -1; v.Shares = false;
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
        private static Vector2 AnchorPoint(Visual v, TraderHarbour h) => AnchorWorld(h, v.AnchorAt);
        private static Vector2 WaitPoint(Visual v, TraderHarbour h) => h.Approach + Across(-WindBlows()) * (float)Rules.LaneShare(v.Boat.id) * 20f;

        // ---- Where it is, how it lies on the water ----
        private static void Pose(Visual v, float dt, float fdt, bool snap)
        {
            Vector2 at, dir; float half = 0, yawTarget, legTarget = float.NaN; bool held = false;
            var h = v.Port >= 0 ? LaneMap.HarbourOf(v.Port) : null;
            float span = Mathf.Max(8f, v.Size.x * .6f);
            switch (v.Phase)
            {
                // (going round a player's ship at 4-10, as far across the lane as that needs: TraderRep.SeaTarget, NaN when not)
                case Phase.Sail: at = v.Path.At(v.S); dir = v.Path.Direction(v.S, span); half = LaneMap.HalfWidth(at); legTarget = TraderRep.SeaTarget(v); break;
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
            // its new place worked out in double (px, pz), from its exact one (the 4x stall: Visual.PX); pos is it as floats
            double px, pz; bool bent = false;
            if (held) { if (snap) { px = at.x; pz = at.y; } else Rules.StepToward(v.PX, v.PZ, at.x, at.y, 1.5 * dt, out px, out pz); }
            else
            {
                float target = !float.IsNaN(legTarget) ? legTarget : Mathf.Clamp((float)Rules.LaneShare(v.Boat.id) * half + v.Avoid, -half, half);
                if (v.Back) target = v.Offset;     // going back it keeps its place across the leg
                v.Offset = snap ? target : Mathf.MoveTowards(v.Offset, target, (1.5f + .2f * Mathf.Abs(v.Speed)) * dt);   // (its speed's size: negative while it backs for the player)
                // on a lane, its point from the exact distance the clock set (SExact, while S still holds what the clock stored)
                double ax = at.x, az = at.y;
                if (v.Phase == Phase.Sail && v.S == v.SKept) v.Path.AtExact(v.SExact, out ax, out az);
                var across = Across(dir) * v.Offset; ax += across.x; az += across.y;
                // coming to anchor (the user, 2026-11-08 evening, of the slide to its place after the leg, up to 18 m: "Shorten it"; ANCHORAGE_SPREAD_DRAFT.md
                // §9): over the leg's last 30 m (at most the anchorage's radius) its aim bends from the leg to its own place, eased (smoothstep: no kink
                // where it starts), so the leg ends at its place. The leg's last stretch lies inside the anchorage's circle, as its place does, so every
                // aim between them does too. Its bow follows the bent way, and over the last 10 m turns into the wind as it lies anchored.
                float toEnd = v.Leg != null ? v.Leg.Length - v.U : 999f, bend = h != null ? Mathf.Clamp(h.AnchorRadius, 1f, 30f) : 0f;
                if (v.Phase == Phase.LegIn && v.Berth == null && v.Slot >= 0 && !v.Back && h != null && toEnd < bend)
                {
                    var place = AnchorPoint(v, h); double ease = toEnd / bend, legW = ease * ease * (3 - 2 * ease), legDw = 6 * ease * (1 - ease);
                    // the way's direction as the leg's end nears: d/d(left) of place + (leg - place)·legW, the leg moving along dir as left falls
                    double wx = (place.x - ax) * legDw / bend + dir.x * legW, wz = (place.y - az) * legDw / bend + dir.y * legW;
                    ax = place.x + (ax - place.x) * legW; az = place.y + (az - place.y) * legW; bent = true;
                    if (wx * wx + wz * wz > 1e-9) yawTarget = YawOf(new Vector2((float)wx, (float)wz));
                    if (toEnd < 10f) yawTarget = Mathf.LerpAngle(YawOf(-WindBlows()), yawTarget, toEnd / 10f);
                }
                if (snap) { px = ax; pz = az; } else Rules.StepToward(v.PX, v.PZ, ax, az, (Mathf.Abs(v.Speed) + 3f) * dt, out px, out pz);   // (its speed negative while it backs for the player)
            }
            var pos = new Vector2((float)px, (float)pz);
            // beside a player's ship (part 2): the move loses its part toward it (under way only its part across: it still moves along,
            // and at sea shoves what lies ahead as before), and it does not turn (its ends would sweep); anchored or waiting it stops
            // swinging with the wind
            if (!snap && v.BesideCount > 0 && Time.time < v.BesideTill)
            {
                double mx = px - v.PX, mz = pz - v.PZ; var rb = Across(DirOf(v.Yaw));
                Vector2 Toward(Vector2 to) { if (held) return to; float side = Vector2.Dot(to, rb); return Mathf.Abs(side) > .3f ? rb * Mathf.Sign(side) : Vector2.zero; }
                for (int i = 0; i < v.BesideCount; i++)
                {
                    var t = Toward(v.Beside[i]);
                    if (t != Vector2.zero) Rules.WithoutToward(ref mx, ref mz, t.x, t.y);
                }
                // its place kept in double rounds toward no ship (stored as floats it did: Rules.NoFloatToward, vtc-12); where the move left
                // still goes toward one (two ships beside), it stays this frame (over 1e-9 m: not the double's own rounding of a removed part)
                bool off = true;
                for (int i = 0; i < v.BesideCount; i++) { var t = Toward(v.Beside[i]); if (t != Vector2.zero && mx * t.x + mz * t.y > 1e-9) off = false; }
                if (off) { px = v.PX + mx; pz = v.PZ + mz; } else { px = v.PX; pz = v.PZ; }
                pos = new Vector2((float)px, (float)pz);
                yawTarget = v.Yaw;
                // its place across follows where it is (not while coming to anchor: the bend, not Offset, carries it off the leg there)
                if (!held && !bent) v.Offset = Vector2.Dot(pos - at, Across(dir));
            }
            if (!snap && fdt > 0) v.Velocity = new Vector2((float)((px - v.PX) / fdt), (float)((pz - v.PZ) / fdt));
            v.PX = px; v.PZ = pz;
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
            v.TargetReal = new Vector3(pos.x, y, pos.y); v.TX = px; v.TZ = pz;
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
            // (waiting on its harbour leg for its way to clear it furls them too: TRADER_COLLISION_DRAFT.md, part 1). A trader with
            // square sails only keeps them set waiting too, braced to the wind as under way, and furls them at a berth or the anchorage
            // (the user, 2026-11-09: "kakam's special trader should furl their sail when they are in berth and anchorage")
            bool moving = Rules.SailsOut(v.Copy.Template.AllSquare, v.Phase == Phase.Sail || (v.Phase == Phase.LegOut || v.Phase == Phase.LegIn) && v.HoldAt < 0,
                v.Phase == Phase.Moored || v.Phase == Phase.Anchored);
            // furled while it stands or backs for the player at 7-10, the square-sail Kakam too (R8 "B"), or stops short of a player's ship it
            // can't get round at sea (TraderRep.SailsSet)
            moving = TraderRep.SailsSet(v, moving);
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
            float clock = fdt > 0 ? dt / fdt : 0f;   // the traders' seconds a real second
            TraderHulls.ClothClock(v.Copy, clock);
            Trim(v, moving, dt, clock, false);
        }

        // The self-trim (TRADER_TRIM_DRAFT.md, "The self-trim design", approved by the user on 2026-10-23; the rules in Rules.TrimOut
        // and on): each sail set to the apparent wind, its cloth pushed onto the face it is set to. Under way every sail on its own
        // side (the game's rule with the wind's buffer), split in the goosewing band at sea; the square sails set and furled at sea
        // by the awa, furled on the harbour legs; furling in port the sides kept, furled the rest pose. The speed is not touched (T6:
        // "These sail change is just visual"). snap: drawn just now (no buffer, no ease, the square sails at their state at once).
        private static void Trim(Visual v, bool moving, float dt, float clock, bool snap)
        {
            var c = v.Copy; var rigs = c.Template.Sails; int n = c.SailT.Length;
            var through = v.Velocity * (float)Rules.SailingSpeed(1, clock);
            var blows = WindBlows();
            var wind = new Vector3(blows.x, 0, blows.y) * v.Wind - new Vector3(through.x, 0, through.y);
            v.Apparent = wind;
            // its angle off the bow, + over the port side (the true wind's way while it has no strength: just drawn)
            Vector2 f = DirOf(v.Yaw), from = wind.sqrMagnitude > .0001f ? new Vector2(-wind.x, -wind.z) : -blows;
            if (from.sqrMagnitude > 1e-8f) v.Awa = Vector2.Angle(f, from) * (Vector2.Dot(from, Across(f)) <= 0 ? 1f : -1f);
            float awa = Mathf.Abs(v.Awa); bool atSea = v.Phase == Phase.Sail;
            if (v.SailSides == null || v.SailSides.Length != n) { v.SailSides = new int[n]; v.SailSideFor = new float[n]; }
            int pose;
            if (moving && (v.Unroll > .02f || snap))
            {
                if (v.Side == 0 || snap) { v.Side = Rules.Leeward(v.Awa); v.SideFor = 0; }
                v.SideFor = Rules.SidePast(v.Side, v.Awa, 0) ? v.SideFor + dt : 0;
                int was = v.Side; v.Side = Rules.SailSideHeld(v.Side, v.Awa, 0, v.SideFor); if (v.Side != was) v.SideFor = 0;
                // goosewing at sea only, with its buffer; ended, every sail to leeward at once (at the approach point too)
                v.GooseFor = Rules.GooseTurns(v.Goose, awa) ? v.GooseFor + dt : 0;
                bool goose = !snap && Rules.GooseHeld(v.Goose, awa, v.GooseFor, atSea);
                if (goose != v.Goose)
                {
                    v.Goose = goose; v.GooseFor = 0;
                    if (goose) { v.GooseSide = v.Side; for (int i = 0; i < n && i < rigs.Count; i++) if (rigs[i].Order == 0 && v.SailSides[i] != 0) v.GooseSide = v.SailSides[i]; }
                    else { v.Side = Rules.Leeward(v.Awa); v.SideFor = 0; for (int i = 0; i < n; i++) { v.SailSides[i] = v.Side; v.SailSideFor[i] = 0; } }
                }
                for (int i = 0; i < n && i < rigs.Count; i++)
                {
                    var rig = rigs[i];
                    if (v.Goose && rig.Order >= 0) { v.SailSides[i] = Rules.GooseSide(rig.Order, v.GooseSide); v.SailSideFor[i] = 0; continue; }
                    if (v.SailSides[i] == 0 || snap) { v.SailSides[i] = Rules.Leeward(v.Awa); v.SailSideFor[i] = 0; }
                    double line = rig.Kind == 1 || rig.Kind == 2 ? rig.Closest : 0;
                    v.SailSideFor[i] = Rules.SidePast(v.SailSides[i], v.Awa, line) ? v.SailSideFor[i] + dt : 0;
                    int s0 = v.SailSides[i]; v.SailSides[i] = Rules.SailSideHeld(s0, v.Awa, line, v.SailSideFor[i]); if (v.SailSides[i] != s0) v.SailSideFor[i] = 0;
                }
                pose = v.Side;
            }
            else if (v.Unroll > .5f && v.Side != 0) pose = v.Side;   // furling in port: the sides kept
            else
            {
                pose = 0; v.Side = 0; v.Goose = false; v.GooseFor = 0;
                for (int i = 0; i < n; i++) { v.SailSides[i] = 0; v.SailSideFor[i] = 0; }
            }
            // the square sails: at sea set from awa 36 and furled under 33 (7 s held); leaving, set at the approach point at once from
            // 36; off the sea furled. A trader with square sails only: set always here; at a berth or the anchorage its v.Unroll furls
            // them (Sails, Rules.SailsOut)
            bool allSquare = c.Template.AllSquare;
            if (snap || (atSea && v.TrimPhase != Phase.Sail)) { v.SquareSet = allSquare || atSea && Rules.SquareAtApproach(awa); v.SquareFor = 0; }
            else
            {
                v.SquareFor = Rules.SquareTurns(v.SquareSet, awa) ? v.SquareFor + dt : 0;
                bool set = Rules.SquareSet(v.SquareSet, awa, v.SquareFor, atSea, allSquare); if (set != v.SquareSet) { v.SquareSet = set; v.SquareFor = 0; }
            }
            v.TrimPhase = v.Phase;
            v.SquareUnroll = snap ? (v.SquareSet ? 1f : 0f) : Mathf.MoveTowards(v.SquareUnroll, v.SquareSet ? 1f : 0f, (float)Rules.FurlRate * dt);
            TraderHulls.PoseSails(c, pose, awa, snap ? 1f : 1f - Mathf.Exp(-dt / .8f), v.SailSides, !snap);
            // each cloth pushed onto the face its sail is set to, toward its own side (a square sail's toward the bow), sized as the
            // part of the game's push that fills a sail the wind meets at that angle (Rules.PushShare); at rest the wind as it is
            var hull = c.Go.transform; Vector3 hullFwd = hull.forward, hullRight = hull.right;
            float strength = wind.magnitude; var blow = strength > 1e-4f ? wind / strength : Vector3.zero;
            for (int i = 0; i < c.Sails.Length && i < rigs.Count; i++)
            {
                var s = c.Sails[i]; if (!s) continue;
                var rig = rigs[i];
                s.currentUnroll = rig.Kind == 3 ? Mathf.Min(v.Unroll, v.SquareUnroll) : v.Unroll;
                int side = pose == 0 ? 0 : v.SailSides[i] != 0 ? v.SailSides[i] : pose;
                if (side == 0 || strength <= 1e-4f || !c.SailT[i]) { s.apparentWind = wind; continue; }
                var face = c.SailT[i].rotation * rig.Face;
                if (Vector3.Dot(face, rig.Kind == 3 ? hullFwd : hullRight * side) < 0) face = -face;
                float attack = Mathf.Asin(Mathf.Clamp(Vector3.Dot(blow, face), -1f, 1f)) * Mathf.Rad2Deg;
                s.apparentWind = face * (strength * (float)Rules.PushShare(attack));
            }
            // its flags as the game's WindClothSimple drives them, by the trader's own wind: its apparent wind (the wind at its place
            // less its way through the water, as the game takes the wind less the ship's velocity), its sail's push, or the wind
            var flags = c.Template.Flags; var trueWind = new Vector3(blows.x, 0, blows.y) * v.Wind;
            for (int i = 0; i < c.FlagCloths.Length && i < flags.Count; i++)
            {
                var k = c.FlagCloths[i]; if (!k) continue; var flag = flags[i];
                var by = flag.Drive == 0 ? wind : flag.Drive == 1 && flag.Sail < c.Sails.Length && c.Sails[flag.Sail] ? c.Sails[flag.Sail].apparentWind : trueWind;
                k.externalAcceleration = by * flag.Mult;
            }
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
            if (chaoticLerp != null)
            {
                try { return (float)chaoticLerp.Invoke(null, new object[] { 1500f, 4000f, distance }); }
                catch (Exception e)
                {
                    // dropped for good (looked up once): this line is logged once
                    chaoticLerp = null;
                    Plugin.Log?.LogWarning("Visible traders: Chaotic Wind's GetConfiguredOceanLerp failed (" + (e.InnerException ?? e).Message + "); the traders' wind uses the game's ocean rule from now on.");
                }
            }
            return (float)Rules.OceanLerp(distance);
        }
        // Chaotic Wind's ocean rule, looked up once: AccessTools.TypeByName goes through every loaded assembly's types, 10.89 ms in
        // the frame the first trader was drawn (vt-22, FrameParts' "wind"); so it is done at the start menu with the templates
        // (TraderHulls.Capture), and here only if that did not run.
        internal static void LookForChaoticWind()
        {
            if (chaoticLooked) return;
            chaoticLooked = true;
            var type = FleetCompat.TypeOf("ChaoticWind.ChaoticWindPlugin");   // each assembly's own lookup: a miss scans no type list (the deep review of 2026-11-09)
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
        // Since 2026-11-09 (TRADER_REPUTATION_DRAFT.md): its band by the player's reputation read at each look first; the player's ships out of
        // the bow box at sea too (0-3 holds its line: "make rep 0-3 don't shift sideway at all in open sea"; 4-10 go round or give way by
        // TraderRep.Look along the lane); the beside rule's distance by band (TraderRep.BesideGap).
        private static void Contacts(Visual v, bool ahead)
        {
            v.NextContact = Time.time + .25f; met.Clear();
            TraderRep.Read(v);
            Beside(v);
            TraderRep.Look(v);
            var player = PlayerReal();
            // far from the player nothing is looked for, but a turn aside still eases off as when nothing is ahead (it stayed aside
            // until the player came within 1 km; the deep review of 2026-11-02, Claude's fix)
            if ((v.Pos - player).magnitude > Rules.TraderNear) { if (ahead && Time.time > v.AvoidTill) v.Avoid = Mathf.MoveTowards(v.Avoid, 0f, 1f); return; }
            var f2 = DirOf(v.Yaw); var turn = Quaternion.Euler(0, v.Yaw, 0);
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
                    if (ship && met.Add(ship.GetInstanceID())) Meet(v, ship);
                    if (ship && PlayersShip(ship)) continue;     // (in port Ways' look, at sea TraderRep.Look's)
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
                var ship = ShipOf(c); if (ship && met.Add(ship.GetInstanceID())) Meet(v, ship);
            }
        }
        // The ships met in this look: each met once, however many of its colliders lie in the boxes (Meet sets every pair of a ship's
        // colliders with the copy's, so once is the same).
        private static readonly HashSet<int> met = new HashSet<int>();
        // The player's ships within 3 m of its hull (part 2, the beside rule; K4 (a): at sea too): the way toward each, hull to hull
        // (Rules.BoxApart), kept until the next look.
        private static void Beside(Visual v)
        {
            v.BesideCount = 0; v.BesideTill = Time.time + .5f;
            // the distance by its band (TraderRep.BesideGap): at sea none at 0-3 (it may turn into the ship, R3, R19), 15 m at 4-10; in port 3 m
            double gap = TraderRep.BesideGap(v); if (double.IsNaN(gap)) return;
            var hulls = PlayerHulls(); if (hulls.Count == 0) return;
            var me = BoxOf(v);
            foreach (var s in hulls)
            {
                if (v.BesideCount >= v.Beside.Length || !Rules.BoxesOverlap(me, s, gap)) continue;
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
                var theirs = theirColliders; theirs.Clear(); ship.GetComponentsInChildren(false, theirs);
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
        // The ship's colliders on active objects (GetComponentsInChildren's own list form: the same ones in the same order as its array,
        // without a new array at each look; filled again at each use, never while another use reads it).
        private static readonly List<Collider> theirColliders = new List<Collider>();
        private static void IgnoreWith(TraderCopy copy, Transform ship)
        {
            copy.Ignored[ship.GetInstanceID()] = Time.time;
            var theirs = theirColliders; theirs.Clear(); ship.GetComponentsInChildren(false, theirs);
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
            if (boat.parent == Refs.shiftingWorld && VisibleTraders.GameNpcBoat(boat)) newState = false;
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
        // The bubble's kinds (TRADER_REPUTATION_DRAFT.md sections 4.3, 5.2, 5.3): a collision line (the user's 17), an apology (a collision
        // at 7-10, TraderLines.ApologyLines), a greeting (4-6, at most 2 on screen), a close-by line (7-10, admiring, surprised or honored),
        // a berth line (7-10, its berth yielded to the player; no two the same at once). Each kind's line is never the trader's last of it.
        // The player's own trade fleet's three (section 13: "fleet ship only use it's unique set of dialogue"): its greeting, apology and berth
        // line. (Kinds stay under 8: lastLine's key is the boat's id times 8 plus the kind.)
        internal const int KindCollision = 0, KindApology = 1, KindGreet = 2, KindClose = 3, KindBerth = 4, KindFleetGreet = 5, KindFleetApology = 6, KindFleetBerth = 7;
        internal static string[] LinesOf(int kind) => kind == KindApology ? TraderLines.ApologyLines : kind == KindGreet ? TraderLines.GreetLines : kind == KindClose ? TraderLines.CloseLines : kind == KindBerth ? TraderLines.BerthLines :
            kind == KindFleetGreet ? TraderLines.FleetGreetLines : kind == KindFleetApology ? TraderLines.FleetApologyLines : kind == KindFleetBerth ? TraderLines.FleetBerthLines : Lines;
        // A fleet ship's kind for the one asked: a greeting or close-by line its greeting, a collision line or apology its apology (a fleet ship
        // reads 7-10, so only the apology is ever asked), a berth line its berth line.
        internal static int FleetKind(int kind) => kind == KindGreet || kind == KindClose ? KindFleetGreet : kind == KindCollision || kind == KindApology ? KindFleetApology : kind == KindBerth ? KindFleetBerth : kind;
        internal static int KindFor(VisibleTraders.Visual v, int kind) => v != null && OfficeFleet.IndexOf(v.Boat) >= 0 ? FleetKind(kind) : kind;
        internal static bool IsBerthKind(int kind) => kind == KindBerth || kind == KindFleetBerth;
        internal static bool IsGreetKind(int kind) => kind == KindGreet || kind == KindClose || kind == KindFleetGreet;
        internal static bool IsApologyKind(int kind) => kind == KindApology || kind == KindFleetApology;
        // A trader's bubble of a kind by the player's reputation (TraderRep), shown its time: a greeting or a close-by line 15 s, a berth line
        // 30 s (and sooner gone past 60 m, as every bubble); `line` set: that line (the tests).
        internal static void Say(VisibleTraders.Visual v, int kind, int line = -1)
        {
            if (v == null || v.Copy == null) return;
            // a fleet ship its own set (section 13)
            kind = KindFor(v, kind);
            var list = LinesOf(kind);
            if (line < 0 && IsBerthKind(kind))
            {
                // (no two of the same set at once)
                var taken = new HashSet<int>(VisibleTraders.Shown.Where(o => o != v && o.Copy != null && o.Copy.Bubble && o.Copy.BubbleGone == 0 && o.Copy.BubbleKind == kind).Select(o => o.Copy.BubbleLine));
                line = Rules.PickLineFree(list.Length, lastLine.TryGetValue(v.Boat.id * 8 + kind, out int was) ? was : -1, UnityEngine.Random.value * .999999, taken);
            }
            Show(v.Copy, v.Boat.id, line, kind, IsBerthKind(kind) ? (float)Rules.BerthLineShow : IsGreetKind(kind) ? (float)Rules.GreetShow : 0f);
        }
        // A new bubble on a drawn trader, its line chosen; `line` set: that line (the tests). kind: its list (LinesOf); hold: shown that many
        // seconds (0: until the player is 60 m off).
        internal static void Show(TraderCopy c, int boatId, int line = -1, int kind = KindCollision, float hold = 0f)
        {
            if (c == null || !c.Go) return;
            if (c.Bubble) Object.Destroy(c.Bubble);
            Materials();
            var list = LinesOf(kind); int key = boatId * 8 + kind;
            if (line < 0) line = Rules.PickLine(list.Length, lastLine.TryGetValue(key, out int last) ? last : -1, UnityEngine.Random.value * .999999);
            lastLine[key] = line;
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
            tm.text = Layout(list[line], out float width);
            if (!backMesh) back.transform.localScale = new Vector3(width / 2f, halfHeight * Times, 1f);
            var tr = words.GetComponent<MeshRenderer>(); tr.sharedMaterial = textMaterial;
            foreach (var r in new Renderer[] { br, tr }) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false; r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off; }
            root.layer = back.layer = words.layer = 0;
            if (Refs.observerMirror) root.transform.rotation = Refs.observerMirror.transform.rotation;
            c.Bubble = root; c.BubbleGone = 0; c.BubbleLine = line; c.BubbleKind = kind; c.BubbleTill = hold > 0 ? Time.time + hold : 0f;
            if (Juicebox.juice) { root.transform.localScale = Vector3.one * .9f; Juicebox.juice.TweenScale(root, Vector3.one, .1f); }
            Plugin.Log?.LogInfo("Trader bubble: trader " + boatId + " says " + (kind == KindApology ? "apology" : kind == KindGreet ? "greeting" : kind == KindClose ? "close-by line" : kind == KindBerth ? "berth line" :
                kind == KindFleetGreet ? "fleet greeting" : kind == KindFleetApology ? "fleet apology" : kind == KindFleetBerth ? "fleet berth line" : "line") + " " + (line + 1) + (hold > 0 ? " (" + hold.ToString("F0") + " s)" : "") + ".");
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
            // one bubble a trader: contacts while a collision line or an apology shows change nothing; one replaces a greeting, a close-by or a
            // berth line at once (TRADER_REPUTATION_DRAFT.md 4.3). At 7-10 an apology instead (5.3).
            if (v == null || (v.Copy.Bubble && v.Copy.BubbleGone == 0 && (v.Copy.BubbleKind == KindCollision || IsApologyKind(v.Copy.BubbleKind)))) return;
            // (a fleet ship its own apology: section 13)
            Show(v.Copy, v.Boat.id, -1, TraderRep.BandOf(v) >= 2 ? KindFor(v, KindApology) : KindCollision);
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
                // (and a greeting, close-by or berth line once its time is up: BubbleTill)
                if (!KeepForTest && (VisibleTraders.HullDistance(v, player) > Away || c.BubbleTill > 0 && Time.time >= c.BubbleTill))
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

    // ======== The visible traders by local reputation (in TraderReputation.cs until 2026-11-09) ========
    // ---- The visible traders by the player's local reputation (TRADER_REPUTATION_DRAFT.md: the user's request of 2026-11-08, R1-R24
    // answered 2026-11-09; built 2026-11-09 at the user's word, "after this test, continued to build the rest, /afk") ----
    // Each drawn trader reads one reputation (its home region's; an independent's where it is; a trade office ship always 7-10) and acts
    // by its band: 0-3 holds its line at sea, 4-6 goes round the player's ships everywhere and greets, 7-10 gives way (stops 20 m short,
    // furls, backs off), steers clear further and yields the berths nearest the port's own trade office. The rules are here; the
    // runtime is TraderRep below, called from VisibleTraders (above) at the places the draft's section 8 names. Nothing saved.
    public static partial class Rules
    {
        // The band of a reputation level (the user's three): 0 for 0-3, 1 for 4-6, 2 for 7-10.
        public static int RepBand(int level) => level >= 7 ? 2 : level >= 4 ? 1 : 0;
        // Whose reputation a trader reads (R1 "B, independant trader follow where trader is"; R22 "A"): its home region's (0 Al'Ankh,
        // 1 Emerald, 2 Aestrin), the Chronos traders their home port's region (their home is a port: Gold Rock City, Dragon Cliffs, Fort
        // Aestrin); -1 for an independent, which reads where it is (PlaceAccount).
        public static int HomeAccount(string group, int home, Func<int, int> portRegion)
        {
            if (group == "independent") return -1;
            if (group == "chronos") return portRegion != null && home >= 0 ? portRegion(home) : -1;
            return home >= 0 && home < 3 ? home : -1;
        }
        // An independent's account (R1): in port its port's (Chronos' own at Chronos: Reputations.Account), under way the nearer end of
        // its trip, its origin over the first half and its destination over the second.
        public static int PlaceAccount(bool inPort, int port, int origin, int destination, double trip, double remaining, Func<int, int> portAccount)
        {
            int at = inPort ? port : trip > 0 && remaining > trip / 2 ? origin : destination;
            return at >= 0 && portAccount != null ? portAccount(at) : -1;
        }
        // In the way (4.1, R4 (a)): a player's ship whose box (a moving one's stretched along its own way over the next 8 s) comes within
        // the trader's half beam + 3 m (rep 4-6) or + 10 m (rep 7-10, R18) of its way over the next 8 s of it, at least 60 m and at most
        // 300 m past its bow.
        public const double RepLookSeconds = 8, RepLookLeast = 60, RepLookMost = 300, RepCorridorMid = 3, RepCorridorHigh = 10;
        public static double RepLookAhead(double speed) => Clamp(speed * RepLookSeconds, RepLookLeast, RepLookMost);
        public static double RepCorridor(int band) => band >= 2 ? RepCorridorHigh : RepCorridorMid;
        // A ship moving under 0.3 m a second counts as lying still (R7).
        public const double StillUnder = .3;
        // A moving ship's box over the next `seconds` (R4 (a)): stretched along its velocity in its own frame, its middle moved half way.
        public static HullBox Stretched(HullBox h, double vx, double vz, double seconds)
        {
            double sx = vx * seconds, sz = vz * seconds, along = sx * h.Fx + sz * h.Fz, across = sx * h.Rx + sz * h.Rz;
            return new HullBox(h.X + sx / 2, h.Z + sz / 2, h.Fx, h.Fz, h.A + Math.Abs(along) / 2, h.B + Math.Abs(across) / 2);
        }
        // How far across its lane a trader goes round a player's ship at sea (4.1): up to the lane's clearance (120 m from its centre at
        // sea, 40 m in the lagoon: twice the traders' usual spread, the lane map's rule) less half its beam and 3 m; on a Scrambled Seas map
        // only up to the lane's own half-width (60 m, 20 m near a lagoon port: its ways keep less land clear near the ports; Claude's choice).
        public const double RepAsideSpare = 3;
        public static double RepAsideMost(double halfWidth, double beam, bool scrambled) => Math.Max(0, (scrambled ? halfWidth : 2 * halfWidth) - beam / 2 - RepAsideSpare);
        // Passing a player's ship: today's harbour margin (WayMargin), 10 m from it at rep 7-10 where the water allows (R18; never closer
        // than today's margin, today's rules where even that is not there).
        public const double RepClearHigh = 10;
        public static double RepPassMargin(int band) => band >= 2 ? RepClearHigh : WayMargin;
        // Single file at rep 7-10 (R18): water narrower than both beams + 10 m (today + 6 m), so it waits outside a narrow stretch sooner.
        public const double SingleFileSpareHigh = 10;
        public static double RepSingleFileSpare(int band) => band >= 2 ? SingleFileSpareHigh : SingleFileSpare;
        public static bool SingleFile(double width, double beamA, double beamB, double spare) => width < beamA + beamB + spare;
        // PassOffset with a wider margin from the ship where the water allows it, else today's (R18).
        public static double PassOffset(double min, double max, double lo, double hi, double halfBeam, double at, double margin)
        {
            double best = PassOffsetBy(min, max, lo, hi, halfBeam, at, margin);
            return double.IsNaN(best) && margin > WayMargin ? PassOffsetBy(min, max, lo, hi, halfBeam, at, WayMargin) : best;
        }
        private static double PassOffsetBy(double min, double max, double lo, double hi, double halfBeam, double at, double margin)
        {
            double a = lo - halfBeam - margin, b = hi + halfBeam + margin, best = double.NaN;
            if (min <= Math.Min(max, a)) best = Clamp(at, min, Math.Min(max, a));
            if (Math.Max(min, b) <= max) { double r = Clamp(at, Math.Max(min, b), max); if (double.IsNaN(best) || Math.Abs(r - at) < Math.Abs(best - at)) best = r; }
            return best;
        }
        // The beside rule's distance (today 3 m, BesideGap): at sea none at rep 0-3 (R3 "for rep 0-3, trader will still turn into player in
        // open sea", R19 (a)), 15 m at rep 4-10 (R3 (a): its ends sweep 6-18 m from its middle as it turns); in port 3 m at every band (its
        // berths lie 4 m apart). NaN: no beside rule.
        public const double BesideGapSea = 15;
        public static double RepBesideGap(int band, bool atSea) => !atSea ? BesideGap : band == 0 ? double.NaN : BesideGapSea;
        // Going round (4.1): the offset across the way, within min..max, that keeps `margin` from a ship lying lo..hi across it (+ to
        // starboard); `at` itself where it is clear already. Head-on (the ship's middle within 1 m of `at`) to starboard, else away from the
        // side it lies on (as the bow box's step aside today), the other side where that one has no room. NaN: neither side.
        public static double GoRound(double min, double max, double lo, double hi, double halfBeam, double at, double margin)
        {
            double a = lo - halfBeam - margin, b = hi + halfBeam + margin;
            if ((at <= a || at >= b) && at >= min && at <= max) return at;
            bool leftOk = min <= Math.Min(max, a), rightOk = Math.Max(min, b) <= max;
            double left = Math.Min(max, a), right = Math.Max(min, b), middle = (lo + hi) / 2;
            bool starboard = Math.Abs(middle - at) < 1 || middle < at;
            if (starboard) return rightOk ? right : leftOk ? left : double.NaN;
            return leftOk ? left : rightOk ? right : double.NaN;
        }
        // Giving way at rep 7-10 (5.1, R9 "A, but when player is closer then 8m, it back away at 75% of the speed"): slowing from 60 m, stopped
        // 20 m short (bow to the ship's box); stopped, a ship that keeps coming within 12 m and it goes astern at a quarter of its speed (at
        // most 1 m a second), within 8 m at 75% of its speed, until the gap is 20 m again, at most its own length in one meeting; on 3 s after
        // the ship has left its way; a ship lying still in its way 60 s (R7 "A but wait for 60s"), and it goes round it.
        public const double GiveSlowFrom = 60, GiveStop = 20, GiveBackUnder = 12, GiveBackFastUnder = 8, GiveBackShare = .25, GiveBackMost = 1, GiveBackFast = .75, GiveClear = 3, StillWait = 60;
        public static double GiveSpeed(double speed, double gap) => speed * Clamp((gap - GiveStop) / (GiveSlowFrom - GiveStop), 0, 1);
        public static bool BackStarts(double gap) => gap < GiveBackUnder;
        public static bool BackEnds(double gap, double backed, double length) => gap >= GiveStop || backed >= length;
        // its speed astern while it backs (a positive number)
        public static double BackSpeed(double speed, double gap) => gap < GiveBackFastUnder ? speed * GiveBackFast : Math.Min(GiveBackMost, speed * GiveBackShare);
        // The greeting (4.3, R5 "A, but shown 15 sec. Allow maximum 2 greeting.", R20 (a); 5.3, R6): the ship the player is aboard within 40 m
        // of a drawn trader's hull (hull to hull); shown 15 s; that trader again only once the ship has been 300 m from it; at most 2
        // greetings on screen at rep 4-6, none capped at 7-10. The berth line shown 30 s (R14).
        public const double GreetWithin = 40, GreetAgain = 300, GreetShow = 15, BerthLineShow = 30; public const int GreetMost = 2;
        // The berths a port's 7-10 traders yield (5.2; R10 "B", R17 "for port with 2 medium as closet berth, release 2 trader. For other port,
        // release 3 trader."; R24, Neverdin: its three small ones, a large ship fitting their water, 1.7): the nearest the port's trade office
        // (its PortDude), by the straight distance from each berth's centre; 2 where those two are both medium or larger, else 3 (every one a
        // port has where it has fewer). The berths' indices, nearest first.
        public static List<int> OfficeBerths(IList<int> classes, IList<double> distance)
        {
            var order = Enumerable.Range(0, classes.Count).OrderBy(i => distance[i]).ThenBy(i => i).ToList();
            int n = order.Count >= 2 && classes[order[0]] >= 1 && classes[order[1]] >= 1 ? 2 : 3;
            return order.Take(n).ToList();
        }
        // The berths are kept for the player (5.2, R11 "A, but wait for 600s instead", R14, R21 (a)): from when the ship the player is aboard
        // comes into the harbour (nearer the port than its approach point is, + 50 m; moving 0.5 m a second or more; the island loaded) until
        // that ship has lain still in the harbour 600 s or has left it; a 7-10 trader moored in one leaves within 60 m of it (hull to hull).
        public const double HarbourSpare = 50, ComingIn = .5, YieldWithin = 60, KeptStill = 600;
        public static bool InHarbour(double shipToPort, double approachToPort) => shipToPort < approachToPort + HarbourSpare;
        // A bubble's line from `count`, never the trader's last, never one in `taken` (the berth lines shown by other traders now: "no two of
        // them the same at once"), the roll in [0, 1); where every line is excluded, PickLine's.
        public static int PickLineFree(int count, int last, double roll, ICollection<int> taken)
        {
            var free = Enumerable.Range(0, Math.Max(0, count)).Where(i => i != last && (taken == null || !taken.Contains(i))).ToList();
            if (free.Count == 0) free = Enumerable.Range(0, Math.Max(0, count)).Where(i => taken == null || !taken.Contains(i)).ToList();
            if (free.Count == 0) return PickLine(count, last, roll);
            return free[Math.Max(0, Math.Min(free.Count - 1, (int)(roll * free.Count)))];
        }
    }

    // The lines (TRADER_REPUTATION_DRAFT.md section 6, Claude's, the user's to change; word for word from the draft, checked by the tests).
    internal static class TraderLines
    {
        internal static readonly string[] GreetLines = {
            "Ahoy, captain! Fair winds to you!",
            "Well met on the water, friend!",
            "Ho there! Calm seas and full holds!",
            "A familiar sail! Safe passage, captain.",
            "Ahoy! May your cargo arrive dry.",
            "Good trading to you, captain!",
            "Ahoy! Good wind on your sails today!",
            "Hail, captain! May the sea treat you kindly.",
            "Good to see a friendly sail out here!",
            "OI! Nice day fer it, eh?!",
            "AHOY THERE! Ye look shipshape today!",
            "Oi oi, cap'n! Fair winds, savvy?",
            "HEY! Don't go rammin' me now, eh!",
            "Ahoy, matey! Grog's on me next port!",
            "YO HO! Look who's sailin' about!" };
        internal static readonly string[] AdmireLines = {
            "Now THAT is a captain worth following!",
            "Look at that trim! Fine sailing, captain!",
            "Every port from here to Chronos speaks of you.",
            "Fine ship, finer captain!",
            "I'd sail under your flag any day, captain.",
            "They say your cargo never arrives late!",
            "BLIMEY! That's a proper fine vessel, that is!",
            "HO HO! Now THAT'S how ye sail a ship!",
            "Wish me old tub sailed like yours, cap'n!" };
        internal static readonly string[] SurpriseLines = {
            "By the winds... is that really you?!",
            "Wait... THE captain? Out here?!",
            "Blow me down! It's the famous captain!",
            "Never thought I'd cross your wake!",
            "Crew! Look who it is! No, really, LOOK!",
            "Is that... it is! Wait till the tavern hears!",
            "OI! Is that... it IS ye! Blimey!",
            "WELL I'LL BE! The famous cap'n, in me waters!",
            "Pinch me, lads! Am I seein' things?!" };
        internal static readonly string[] HonorLines = {
            "An honor to share the water with you.",
            "Make way, lads! An honored captain passes.",
            "You honor these waters, captain.",
            "Our humble boat salutes you, captain!",
            "My grandchildren will hear of this day.",
            "Please, captain, the water is yours.",
            "Hats off, lads! Proper legend comin' through!",
            "Make way, ye dogs! Respect the cap'n!",
            "'Tis an honor, cap'n! Truly it is!" };
        internal static readonly string[] ApologyLines = {
            "My deepest apologies, captain!",
            "Forgive me, captain! Is your hull alright?",
            "A thousand pardons! That was all my fault.",
            "So sorry! I was admiring your rig.",
            "Terribly sorry! I'll pay for the paint.",
            "Pardon me, captain! I should have kept clear.",
            "Oh no, captain! Please forgive my clumsiness!",
            "My fault, captain! I'll keep a better watch.",
            "Sorry! A gust took us. It won't happen again!",
            "Beg pardon, captain! Clumsy hands at the helm!",
            "OI, sorry! Me hand slipped on the wheel!",
            "Whoops! That's on me, cap'n! Sorry!",
            "Blimey, sorry! Didn't see ye there!",
            "ARGH! Me fault! Don't tell the harbormaster!",
            "Sorry, cap'n! Too much grog this mornin'..." };
        internal static readonly string[] BerthLines = {
            "Berth's all yours, captain!",
            "Making room for you, captain. Moor well!",
            "We'll wait at anchor. The berth is yours!",
            "Clearing the dock for you, captain!",
            "After you, captain. We'll lie at anchor.",
            "Take our berth, captain. We're off to anchor.",
            "The best berth for the best captain!",
            "We'll make way. Welcome to port, captain!",
            "All clear by the office, captain. Moor away!",
            "Your berth awaits, captain. We'll anchor out.",
            "Room by the office for you, captain!",
            "You made it in! The dock is yours, captain.",
            "Oi, cap'n! Berth's all yers! Off I go!",
            "Shove off, lads! Cap'n needs the spot!",
            "Right then! Movin' me tub fer ye!",
            "All yers, cap'n! We'll bob about at anchor.",
            "Clear the dock, ye lubbers! Cap'n's comin'!",
            "Here, have me spot! I owe ye one!" };
        // The 27 close-by lines at rep 7-10, one at random from all of them (R6 (a)): admiring, surprised, honored.
        internal static readonly string[] CloseLines = AdmireLines.Concat(SurpriseLines).Concat(HonorLines).ToArray();
        // The player's own trade fleet's lines (section 13, the user's request of 2026-11-09, settled "final"): a fleet ship says only these ("fleet
        // ship only use it's unique set of dialogue"), its greeting in place of the close-by lines (no cap: "No cap"), its apology and its berth line
        // in place of the others' (TraderBubble.FleetKind).
        internal static readonly string[] FleetGreetLines = {
            "Ahoy, boss! Your fleet salutes you!",
            "Fair winds, boss! The hold is full!",
            "Good to see the guildmaster on the water!",
            "All well aboard, chief. Profit's on the way!",
            "The crew sends their regards, guildmaster!",
            "Another fat haul, boss! Clerk'll be grinnin'!",
            "OI, BOSS! We're makin' ye rich out here!",
            "Ahoy, boss! Yer ship's runnin' sweet as rum!" };
        internal static readonly string[] FleetApologyLines = {
            "Sorry, boss! Please don't dock my wages!",
            "Blimey, chief! Bumped yer own ship, I did!",
            "Of all the ships to hit... the guildmaster's!",
            "Beg pardon, chief! Eyes front from now on!",
            "Sorry, boss! Don't tell the clerk!",
            "My fault, boss. The repairs are on us.",
            "ARGH! Rammed the boss! I'm done for...",
            "Sorry, chief! Take it out o' me grog ration!" };
        internal static readonly string[] FleetBerthLines = {
            "The chief's berth! Making way, lads!",
            "Your berth, boss. We'll wait at anchor.",
            "Clearing the dock for the guildmaster!",
            "Movin' off, boss! Moor up by the office, eh!",
            "Make way, lads! The boss is coming in!",
            "Welcome home, guildmaster! The berth is yours.",
            "Right, boss! Movin' out fer ye!",
            "Oi, lads! Boss is here! Off to anchor, quick!" };
    }

    // The runtime: each drawn trader's band and its state against the player's ships, the berths kept for the player, the greetings.
    internal static class TraderRep
    {
        internal sealed class State
        {
            internal int Band;
            // at sea: its place across the lane that gets round a player's ship (NaN: none), until AsideTill; the gap from its bow to one in
            // its way that it can't get round (-1: none; it stops 8 m short as on a harbour leg)
            internal float Aside = float.NaN, AsideTill, BlockGap = -1;
            // giving way (7-10): on, the gap from its bow to the ship in its way, the hull gap (for backing), when a ship was last in its way,
            // held (the way clear under 3 s), going astern and how far in this meeting, how long the ships in its way have lain still, a ship
            // astern in the way back, gathering speed again as its sails set
            internal bool Giving, Hold, Backing, AsternBlocked, Resume;
            internal float GiveGap = -1, Gap = 999f, LastInWay = -999f, LastNear = -999f, Backed, StillFor, LastLook = -1;
            internal bool Rounding, Greeted, ToAnchor;
            internal float RoundGap = -1;
            internal float NextYield;
        }
        // The QA's level for every trader (-1: the game's reputation), and its shorter wait for the kept berths (-1: Rules.KeptStill, 600 s).
        internal static int TestLevel = -1;
        internal static float TestKeptStill = -1;
        private static int PortRegion(int port) => Port.ports != null && port >= 0 && port < Port.ports.Length && Port.ports[port] ? (int)Port.ports[port].region : -1;
        private static int PortAccount(int port) => Port.ports != null && port >= 0 && port < Port.ports.Length && Port.ports[port] ? Reputations.Account(Port.ports[port]) : -1;
        // Its band now (section 2): the trade office's own ships always 7-10 (R12 "b"); else its account's level.
        internal static int BandOf(VisibleTraders.Visual v)
        {
            var b = v.Boat;
            if (OfficeFleet.IndexOf(b) >= 0) return 2;
            if (TestLevel >= 0) return Rules.RepBand(TestLevel);
            int account = Rules.HomeAccount(b.group, b.home, PortRegion);
            // (in port: any phase but under way, or waiting with no trip, as when it is first drawn in port before its phase is set)
            if (account < 0) account = Rules.PlaceAccount(v.Phase != VisibleTraders.Phase.Sail || b.destination < 0, v.Port >= 0 ? v.Port : b.origin, b.origin, b.destination, b.trip, b.remaining, PortAccount);
            return account < 0 ? 0 : Rules.RepBand(Reputations.Level(account));
        }
        private static HullBox BoxOf(VisibleTraders.Visual v) => new HullBox(v.PX, v.PZ, Mathf.Sin(v.Yaw * Mathf.Deg2Rad), Mathf.Cos(v.Yaw * Mathf.Deg2Rad), v.Size.x / 2, v.Size.y / 2);
        internal static double Gap(HullBox p, HullBox q) => Rules.BoxApart(p, q, out _, out _);
        private static string Name(VisibleTraders.Visual v) => "trader " + v.Boat.id + " (" + v.Boat.group + ", hull " + v.Hull + ", rep band " + (v.Rep.Band == 2 ? "7-10" : v.Rep.Band == 1 ? "4-6" : "0-3") + ")";

        // ---- At each look (VisibleTraders.Contacts, 4 times a second) ----
        // The band read again (a reputation gained in port changes the traders there at once), then at sea the look along its lane.
        internal static void Read(VisibleTraders.Visual v) { v.Rep.Band = BandOf(v); }
        internal static double BesideGap(VisibleTraders.Visual v) => Rules.RepBesideGap(v.Rep.Band, v.Phase == VisibleTraders.Phase.Sail);
        internal static double PassMargin(VisibleTraders.Visual v) => Rules.RepPassMargin(v.Rep.Band);
        internal static double Spare(VisibleTraders.Visual v) => Rules.RepSingleFileSpare(v.Rep.Band);

        // The player's ships against a trader's way: each ship near its stretch (stern to `ahead` m past its bow) by its span across the way
        // (lo..hi, + to starboard), whether that comes into its corridor, the first place past the bow it does (m from the bow), its hull gap
        // to the trader and whether it moves. A ship's box is moved by its velocity over the time since the list was made (made every 0.5 s,
        // R16), and a moving one's stretched along its way over the next 8 s.
        private struct Span { internal double Lo, Hi, Gap; internal float First; internal bool InWay, Moving; }
        private static readonly List<Span> spans = new List<Span>();
        private static void Scan(VisibleTraders.Visual v, LanePath way, float place, double corridor, float ahead)
        {
            spans.Clear(); v.Rep.AsternBlocked = false;
            var hulls = VisibleTraders.PlayerHulls(); var vels = VisibleTraders.PlayerVelocities;
            if (hulls.Count == 0 || way == null) return;
            float L = v.Size.x, B = v.Size.y, bow = place + L / 2, stern = Mathf.Max(0f, place - L / 2), end = Mathf.Min(way.Length, bow + ahead);
            var me = BoxOf(v); float age = Mathf.Max(0f, Time.time - VisibleTraders.HullsAt);
            // its bow on the way (its place across it): the gap to a ship ahead is measured from here to the ship's box, exactly (the samples
            // every 5 m alone put a stop 17.5-22.5 m short)
            var bowDir = way.Direction(Mathf.Min(bow, way.Length), TraderWays.Step); var bowAt = way.At(Mathf.Min(bow, way.Length)) + new Vector2(bowDir.y, -bowDir.x) * v.Offset;
            for (int i = 0; i < hulls.Count; i++)
            {
                var vel = i < vels.Count ? vels[i] : Vector2.zero; bool moving = vel.magnitude >= Rules.StillUnder; var h0 = hulls[i];
                var h = moving && age > 0 ? new HullBox(h0.X + vel.x * age, h0.Z + vel.y * age, h0.Fx, h0.Fz, h0.A, h0.B) : h0;
                var box = moving ? Rules.Stretched(h, vel.x, vel.y, Rules.RepLookSeconds) : h;
                double dx = box.X - v.PX, dz = box.Z - v.PZ;
                if (Math.Sqrt(dx * dx + dz * dz) > ahead + L + box.A + box.B + 160) continue;
                var sp = new Span { Lo = double.PositiveInfinity, Hi = double.NegativeInfinity, First = float.PositiveInfinity, Moving = moving, Gap = Gap(me, h) };
                for (float k = stern; k <= end + .01f; k += TraderWays.Step)
                {
                    float s = Mathf.Min(k, way.Length); var p = way.At(s); var d = way.Direction(s, TraderWays.Step);
                    if (!Rules.BoxAcross(box, p.x, p.y, d.x, d.y, TraderWays.Band, out double lo, out double hi)) continue;
                    sp.Lo = Math.Min(sp.Lo, lo); sp.Hi = Math.Max(sp.Hi, hi);
                    if (hi >= v.Offset - B / 2 - corridor && lo <= v.Offset + B / 2 + corridor) { sp.InWay = true; if (s >= bow - TraderWays.Band) sp.First = Mathf.Min(sp.First, (float)Rules.BoxDistance(box, bowAt.x, bowAt.y)); }
                }
                // a box astern (its stern to 20 m behind it), its beam + 3 m each side: no backing into it
                for (float k = Mathf.Max(0f, stern - 20f); k < stern; k += TraderWays.Step)
                {
                    var p = way.At(k); var d = way.Direction(k, TraderWays.Step);
                    if (Rules.BoxAcross(h, p.x, p.y, d.x, d.y, TraderWays.Band, out double lo, out double hi) && hi >= v.Offset - B / 2 - 3 && lo <= v.Offset + B / 2 + 3) v.Rep.AsternBlocked = true;
                }
                if (sp.Lo <= sp.Hi) spans.Add(sp);
            }
        }

        // At sea (4.1, 5.1): rep 4-6 goes round a player's ship in its way (or stops 8 m short where it can't); rep 7-10 gives way to one
        // ahead that moves or has lain still under 60 s, and goes round one lying still longer or beside its way, 10 m clear where the lane
        // allows. Rep 0-3: nothing (the player's ships are no longer in its bow box at sea).
        internal static void Look(VisibleTraders.Visual v)
        {
            var st = v.Rep;
            // (before the step is taken: on a harbour leg LastLook is PortGiveWay's, which runs on its own 0.25 s timer; set here too, it left
            // PortGiveWay only the time since this look, and the 60 s a ship lying still in its way counted up to 15 times too slowly in port (at
            // 60 fps); the deep review of 2026-11-09)
            if (v.Phase != VisibleTraders.Phase.Sail || st.Band == 0 || v.Path == null) { EndSea(v); return; }
            float now = Time.time, step = st.LastLook < 0 ? .25f : Mathf.Clamp(now - st.LastLook, 0f, 1f); st.LastLook = now;
            Scan(v, v.Path, v.S, Rules.RepCorridor(st.Band), (float)Rules.RepLookAhead(Mathf.Max(0f, v.Speed)));
            if (st.Band >= 2 && Give(v, step)) { st.Aside = float.NaN; st.BlockGap = -1; st.Rounding = false; return; }
            // its band fallen below 7-10 while it gave way (an independent past its trip's middle reads its destination's reputation): it
            // stops giving way, else Speed kept it stopped at sea on its last gap for good (the deep review of 2026-11-09)
            if (st.Giving) { st.Giving = st.Hold = st.Backing = false; st.Resume = true; Plugin.Log?.LogInfo("Trader reputation: " + Name(v) + " stops giving way: its band is no longer 7-10."); }
            Round(v, Rules.RepAsideMost(LaneMap.HalfWidth(v.Path.At(v.S)), v.Size.y, LaneMap.Scrambled != null));
        }
        // Giving way (7-10), at sea and on a harbour leg: true while it gives way (the speed then Speed's).
        private static bool Give(VisibleTraders.Visual v, float step)
        {
            var st = v.Rep; float now = Time.time; bool any = false, near = false, still = true; float first = float.PositiveInfinity; double gap = 999;
            foreach (var sp in spans)
            {
                if (!sp.InWay) continue;
                near = true; gap = Math.Min(gap, sp.Gap); if (sp.Moving) still = false;
                if (float.IsPositiveInfinity(sp.First)) continue;
                any = true; first = Mathf.Min(first, sp.First);
            }
            st.Gap = (float)gap;
            // how long the player's ships in its way have lain still, for one meeting: counted while one lies ahead, kept while it lies beside
            // (going round it), started again when one moves or none has been in its way for 3 s (vtrep-1: started again as the ship came
            // beside, the trader gave way again in the middle of going round it and passed 5.9 m off)
            // (counted only while it stands for the ship, stopped short or backing: R7 "A but wait for 60s"; vtrep-4: counted from first seen 60 m
            // ahead, a slow trader reached 60 s on its way and went round without stopping)
            if (near) st.LastNear = now;
            if (!still) st.StillFor = 0f; else if (any && st.Giving && first <= Rules.GiveStop + 1) st.StillFor += step; else if (!any && now - st.LastNear >= Rules.GiveClear) st.StillFor = 0f;
            if (any)
            {
                st.LastInWay = now; st.Hold = false;
                if (still && st.StillFor >= Rules.StillWait)
                {
                    if (st.Giving) { st.Giving = st.Backing = false; st.Resume = true; Plugin.Log?.LogInfo("Trader reputation: " + Name(v) + " goes round the player's ship lying still in its way (" + st.StillFor.ToString("F0") + " s)."); }
                    return false;
                }
                st.GiveGap = first;
                if (!st.Giving) { st.Giving = true; st.Backed = 0; Plugin.Log?.LogInfo("Trader reputation: " + Name(v) + " gives way to the player's ship " + first.ToString("F1") + " m ahead (" + (still ? "lying still" : "moving") + ")."); }
                return true;
            }
            if (!st.Giving) return false;
            if (now - st.LastInWay < Rules.GiveClear) { st.Hold = true; return true; }
            st.Giving = st.Hold = st.Backing = false; st.StillFor = 0f; st.Resume = true;
            Plugin.Log?.LogInfo("Trader reputation: " + Name(v) + " goes on, its way clear for " + Rules.GiveClear + " s (backed " + st.Backed.ToString("F1") + " m).");
            return false;
        }
        // Going round at sea: its own place across the lane (as Pose takes it with no ship) where that keeps clear of every ship's span over its
        // stretch, else the side GoRound picks from where it is now, kept until its stern has passed (the stretch starts at its stern).
        private static void Round(VisibleTraders.Visual v, double most)
        {
            var st = v.Rep; double hb = v.Size.y / 2, margin = Rules.RepPassMargin(st.Band);
            float half = LaneMap.HalfWidth(v.Path.At(v.S));
            double home = Mathf.Clamp((float)Rules.LaneShare(v.Boat.id) * half + v.Avoid, -half, half);
            // (a loop, as All reads it: no lambda and enumerator made at each look while a player's ship is near its way)
            bool Clear(double at, double m) { foreach (var sp in spans) if (!(at <= sp.Lo - hb - m || at >= sp.Hi + hb + m)) return false; return true; }
            st.BlockGap = -1;
            if (spans.Count == 0 || Clear(home, margin)) { if (st.Rounding) { st.Rounding = false; Plugin.Log?.LogInfo("Trader reputation: " + Name(v) + " is past the player's ship, back to its place in the lane."); } st.Aside = float.NaN; return; }
            double target = v.Offset; float blocked = float.PositiveInfinity; bool none = false;
            for (int pass = 0; pass < 2; pass++)
                foreach (var sp in spans.OrderBy(x => x.First))
                {
                    if (target <= sp.Lo - hb - margin || target >= sp.Hi + hb + margin) continue;
                    double t = Rules.GoRound(-most, most, sp.Lo, sp.Hi, hb, target, margin);
                    if (double.IsNaN(t) && margin > Rules.WayMargin) t = Rules.GoRound(-most, most, sp.Lo, sp.Hi, hb, target, Rules.WayMargin);
                    if (double.IsNaN(t)) { none = true; blocked = Mathf.Min(blocked, sp.First); continue; }
                    target = t;
                }
            if (!none && !Clear(target, Rules.WayMargin)) { none = true; foreach (var sp in spans) if (!(target <= sp.Lo - hb - Rules.WayMargin || target >= sp.Hi + hb + Rules.WayMargin)) blocked = Mathf.Min(blocked, sp.First); }
            if (none)
            {
                st.Aside = float.NaN;
                if (!float.IsPositiveInfinity(blocked)) { if (st.BlockGap < 0) Plugin.Log?.LogInfo("Trader reputation: " + Name(v) + " can't get round the player's ship at sea: it stops 8 m short (" + blocked.ToString("F1") + " m ahead)."); st.BlockGap = blocked; }
                return;
            }
            if (!st.Rounding) { st.Rounding = true; Plugin.Log?.LogInfo("Trader reputation: " + Name(v) + " goes round the player's ship at sea, to " + target.ToString("F1") + " m across its lane (from " + v.Offset.ToString("F1") + ")."); }
            st.Aside = (float)target; st.AsideTill = Time.time + .6f;
            // the nearest ship ahead it is not yet clear of where it is now: until out at its place across, it slows from 28 m and stops 8 m
            // short of it (Speed; vtrep-2: going round from 20 m off it passed 8.8 m off, not 10, still moving across as it went on)
            st.RoundGap = -1;
            foreach (var sp in spans)
                // (not yet clear by its band's margin, 10 m at 7-10: vtrep-3 held it only until 2 m clear and it passed 8.6 m off)
                if (!float.IsPositiveInfinity(sp.First) && !(v.Offset <= sp.Lo - hb - margin + .5 || v.Offset >= sp.Hi + hb + margin - .5) && (st.RoundGap < 0 || sp.First < st.RoundGap)) st.RoundGap = sp.First;
        }
        private static void EndSea(VisibleTraders.Visual v)
        {
            var st = v.Rep; st.Aside = float.NaN; st.BlockGap = -1; st.Rounding = false;
            if (v.Phase == VisibleTraders.Phase.Sail && st.Giving) { st.Giving = st.Hold = st.Backing = false; st.Resume = true; }
        }
        // On a harbour leg (VisibleTraders.Ways, 4 times a second): rep 7-10 gives way to a player's ship in its way that moves or has lain
        // still under 60 s (true: the harbour leg's own look waits); else the harbour rules, with its wider margins (PassMargin, Spare).
        internal static bool PortGiveWay(VisibleTraders.Visual v)
        {
            var st = v.Rep; float now = Time.time, step = st.LastLook < 0 ? .25f : Mathf.Clamp(now - st.LastLook, 0f, 1f); st.LastLook = now;
            if (st.Band < 2 || v.Leg == null) { if (st.Giving) { st.Giving = st.Hold = st.Backing = false; st.Resume = true; } return false; }
            Scan(v, v.Leg, v.U, Rules.RepCorridorHigh, (float)Rules.RepLookAhead(Mathf.Max(0f, v.Speed)));
            if (!Give(v, step)) return false;
            v.StopGap = -1; v.HoldAt = -1; v.Aside = float.NaN;
            return true;
        }
        // Where Pose puts it across its lane at sea while it goes round (NaN: as today).
        internal static float SeaTarget(VisibleTraders.Visual v) => v.Phase == VisibleTraders.Phase.Sail && !float.IsNaN(v.Rep.Aside) && Time.time < v.Rep.AsideTill ? v.Rep.Aside : float.NaN;
        // Its speed (Step, after the harbour rules): giving way it slows from 60 m and stops 20 m short, backs off a ship that keeps coming
        // (negative: astern; never past its leg's or trip's start, never into a ship astern, at most its own length a meeting), and gathers
        // speed again as its sails set; at sea a ship it can't get round, and it stops 8 m short. own: its speed by the speed rule.
        internal static float Speed(VisibleTraders.Visual v, float speed, float own, float dt)
        {
            var st = v.Rep; bool sea = v.Phase == VisibleTraders.Phase.Sail, leg = v.Phase == VisibleTraders.Phase.LegIn || v.Phase == VisibleTraders.Phase.LegOut;
            if (!sea && !leg) { st.Backing = st.Resume = false; return speed; }
            if (v.Back) { st.Backing = false; return speed; }
            if (st.Giving)
            {
                float place = sea ? v.S : v.U;
                if (!st.Backing && !st.Hold && st.GiveGap <= Rules.GiveStop + 1 && Rules.BackStarts(st.Gap) && !st.AsternBlocked && place > 1f && st.Backed < v.Size.x)
                { st.Backing = true; Plugin.Log?.LogInfo("Trader reputation: " + Name(v) + " backs away from the player's ship (" + st.Gap.ToString("F1") + " m)."); }
                if (st.Backing && (Rules.BackEnds(st.Gap, st.Backed, v.Size.x) || st.AsternBlocked || place <= .5f))
                { st.Backing = false; Plugin.Log?.LogInfo("Trader reputation: " + Name(v) + " stops backing (gap " + st.Gap.ToString("F1") + " m, backed " + st.Backed.ToString("F1") + " m" + (st.AsternBlocked ? ", a ship astern" : "") + ")."); }
                if (st.Backing) { double back = Rules.BackSpeed(own, st.Gap); st.Backed += (float)(back * dt); return (float)-back; }
                return st.Hold ? 0f : (float)Rules.GiveSpeed(own, st.GiveGap);
            }
            if (sea && st.BlockGap >= 0) return (float)Rules.InPortSpeed(speed, st.BlockGap);
            // going round, not yet out at its place across with a ship near ahead: slowing from 28 m, stopped 8 m short until it is (the
            // harbour rule's numbers)
            if (sea && st.Rounding && !float.IsNaN(st.Aside) && st.RoundGap >= 0 && Mathf.Abs(v.Offset - st.Aside) > 1f) return (float)Rules.InPortSpeed(speed, st.RoundGap);
            if (st.Resume) { if (v.Unroll >= .99f) st.Resume = false; else return speed * Mathf.Max(.1f, v.Unroll); }
            return speed;
        }
        // Its sails (Sails): furled while it stands or backs for the player at 7-10 (the square-sail Kakam too, R8 "B"), and stopped short of a
        // ship it can't get round at sea.
        internal static bool SailsSet(VisibleTraders.Visual v, bool moving)
        {
            var st = v.Rep;
            if (st.Giving && (st.Backing || st.Hold || st.GiveGap <= Rules.GiveStop + 1)) return false;
            if (v.Phase == VisibleTraders.Phase.Sail && st.BlockGap >= 0 && st.BlockGap <= Rules.StopShort + 1) return false;
            return moving;
        }

        // ---- The berths kept for the player (5.2) ----
        private static int keptPort = -1, visitPort = -1; private static bool doneThisVisit; private static float stillFor, nextTick, lastTick;
        private static readonly Dictionary<TraderHarbour, List<TraderBerth>> officeBerths = new Dictionary<TraderHarbour, List<TraderBerth>>();
        internal static int KeptPort => keptPort;
        internal static void Reset() { keptPort = visitPort = -1; doneThisVisit = false; stillFor = 0; officeBerths.Clear(); }
        // The berths nearest a port's trade office (Rules.OfficeBerths), from the game's own record of its office NPC (Port.GetDude, set in
        // PortDude.Awake); none where it has none yet (looked for again next time).
        internal static List<TraderBerth> OfficeBerths(TraderHarbour h)
        {
            if (h == null) return null;
            if (officeBerths.TryGetValue(h, out var known)) return known;
            var port = Port.ports != null && h.Port >= 0 && h.Port < Port.ports.Length ? Port.ports[h.Port] : null;
            var dude = port ? port.GetDude() : null;
            if (!dude || !FloatingOriginManager.instance) return null;
            var at = FloatingOriginManager.instance.ShiftingPosToRealPos(dude.transform.position - VisibleTraders.WorldLag());
            var picked = Rules.OfficeBerths(h.Berths.Select(b => b.Class).ToList(), h.Berths.Select(b => (double)Vector2.Distance(b.Centre, new Vector2(at.x, at.z))).ToList()).Select(i => h.Berths[i]).ToList();
            officeBerths[h] = picked;
            Plugin.Log?.LogInfo("Trader reputation: " + port.GetPortName() + "'s berths nearest its trade office (" + dude.name + "): " + string.Join(", ", picked.Select(b => b.Number + " (" + (b.Class == 0 ? "small" : b.Class == 1 ? "medium" : "large") + ", " + Vector2.Distance(b.Centre, new Vector2(at.x, at.z)).ToString("F0") + " m)").ToArray()) + ".");
            return picked;
        }
        // A berth kept from this trader: a kept berth at the port the player comes into, the trader at 7-10 (each trader by its own band: R23).
        internal static bool KeptFor(VisibleTraders.Visual v, TraderBerth x)
        {
            if (keptPort < 0 || x == null || x.Port != keptPort) return false;
            var list = OfficeBerths(LaneMap.HarbourOf(keptPort));
            return list != null && list.Contains(x) && BandOf(v) >= 2;
        }
        // A 7-10 trader moored in a kept berth, the player's ship within 60 m of its hull (looked at twice a second): it yields the berth.
        internal static bool YieldDue(VisibleTraders.Visual v)
        {
            var st = v.Rep;
            if (v.Phase != VisibleTraders.Phase.Moored || v.Berth == null || v.Boat.destination >= 0 || Time.time < st.NextYield) return false;
            st.NextYield = Time.time + .5f;
            var ship = VisibleTraders.LastShip;
            return ship != null && KeptFor(v, v.Berth) && Gap(ship, BoxOf(v)) <= Rules.YieldWithin;
        }
        // The harbour the player's ship is in (the nearest port it is within its approach point's distance + 50 m of), -1 none.
        private static int HarbourAt(double x, double z)
        {
            int best = -1; double bestD = double.MaxValue;
            foreach (var h in LaneMap.Harbours)
            {
                double d = Math.Sqrt((x - h.PortPoint.x) * (x - h.PortPoint.x) + (z - h.PortPoint.y) * (z - h.PortPoint.y));
                if (Rules.InHarbour(d, Vector2.Distance(h.Approach, h.PortPoint)) && d < bestD) { bestD = d; best = h.Port; }
            }
            return best;
        }
        private static void Kept(float dt)
        {
            var ship = VisibleTraders.LastShip; if (ship == null) return;
            int port = HarbourAt(ship.X, ship.Z);
            if (port < 0)
            {
                if (keptPort >= 0) Plugin.Log?.LogInfo("Trader reputation: the player's ship left port " + keptPort + "'s harbour: its berths by the office are free again.");
                keptPort = visitPort = -1; doneThisVisit = false; stillFor = 0; return;
            }
            if (port != visitPort) { visitPort = port; doneThisVisit = false; stillFor = 0; if (keptPort >= 0 && keptPort != port) keptPort = -1; }
            float speed = VisibleTraders.LastShipVelocity.magnitude;
            if (keptPort < 0 && !doneThisVisit && speed >= Rules.ComingIn && VisibleTraders.PortLoaded(port))
            {
                var list = OfficeBerths(LaneMap.HarbourOf(port));
                if (list == null || list.Count == 0) return;
                keptPort = port; stillFor = 0;
                Plugin.Log?.LogInfo("Trader reputation: the player's ship comes into " + Port.ports[port].GetPortName() + "'s harbour (" + speed.ToString("F1") + " m/s): berths " + string.Join(", ", list.Select(b => b.Number.ToString()).ToArray()) + " kept from its 7-10 traders.");
            }
            if (keptPort != port) return;
            if (speed < Rules.StillUnder) stillFor += dt; else if (speed >= Rules.ComingIn) stillFor = 0;
            float wait = TestKeptStill >= 0 ? TestKeptStill : (float)Rules.KeptStill;
            if (stillFor >= wait) { keptPort = -1; doneThisVisit = true; Plugin.Log?.LogInfo("Trader reputation: the player's ship has lain still " + wait + " s in port " + port + "'s harbour: its berths by the office are free again."); }
        }

        // ---- Four times a second (VisibleTraders.Update): the kept berths and the greetings ----
        internal static void Tick()
        {
            if (!Settings.VisibleTradersEnabled || Time.time < nextTick) return;
            float dt = lastTick > 0 ? Mathf.Clamp(Time.time - lastTick, 0f, 2f) : 0f; lastTick = Time.time; nextTick = Time.time + .25f;
            if (!FloatingOriginManager.instance || !Refs.observerMirror || !LaneMap.Loaded) return;
            VisibleTraders.PlayerHulls();
            Kept(dt);
            Greet();
        }
        // The greetings (4.3, 5.3): the ship the player is aboard within 40 m of a drawn trader at 4-6 or 7-10 that has not greeted since the
        // ship was last 300 m from it; at 4-6 the nearest first, at most 2 on screen; at 7-10 every one (no cap). Not over a bubble showing.
        private static readonly List<KeyValuePair<VisibleTraders.Visual, double>> mid = new List<KeyValuePair<VisibleTraders.Visual, double>>();
        private static void Greet()
        {
            var aboard = VisibleTraders.Aboard; if (aboard == null) return;
            mid.Clear();
            foreach (var v in VisibleTraders.Shown)
            {
                if (v.Copy == null || !v.Copy.Go) continue;
                double gap = Gap(aboard, BoxOf(v));
                if (gap > Rules.GreetAgain) v.Rep.Greeted = false;
                if (v.Rep.Band == 0 || v.Rep.Greeted || gap > Rules.GreetWithin || v.Copy.Bubble && v.Copy.BubbleGone == 0) continue;
                if (v.Rep.Band >= 2) { TraderBubble.Say(v, TraderBubble.KindClose); v.Rep.Greeted = true; }
                else mid.Add(new KeyValuePair<VisibleTraders.Visual, double>(v, gap));
            }
            if (mid.Count == 0) return;
            int showing = VisibleTraders.Shown.Count(x => x.Copy != null && x.Copy.Bubble && x.Copy.BubbleGone == 0 && x.Copy.BubbleKind == TraderBubble.KindGreet);
            foreach (var kv in mid.OrderBy(k => k.Value).Take(Math.Max(0, Rules.GreetMost - showing))) { TraderBubble.Say(kv.Key, TraderBubble.KindGreet); kv.Key.Rep.Greeted = true; }
        }
    }
}

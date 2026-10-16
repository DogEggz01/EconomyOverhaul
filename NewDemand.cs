using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EconomyOverhaul
{
    // ---- Rules ----
    // Dynamic demand (the user, 2026-10-16; plan: always 5 events, each a port starved of one good and another glutted with it).
    public static partial class Rules
    {
        public const int DemandEvents = 5, DemandExpiryDays = 30;
        public const float DemandDoneShare = .2f, LastDepartureKnown = .4f;
        // Saffron Island (33), Chronos (21) and the test ports (7, 8) never take part.
        public static bool DemandPort(int port) => port != 33 && port != 21 && port != 7 && port != 8;
        // Not the special slots (45, 51), saffron (64), Chronos' own goods (32, 33) or the goods no port trades (1, 46, 52).
        public static bool DemandGood(int good) => good > 1 && good < 64 && good != 45 && good != 51 && good != 32 && good != 33 && good != 46 && good != 52;
        // The demanding port takes a good it doesn't produce, whose supply is below 1 and above the drop's target.
        public static bool DemandFits(float production, float supply, float cap) => production <= 0 && supply < 1 && supply > DemandTarget(cap);
        // The producing port must make the good, no exception (the user, 2026-10-16; was any port).
        public static bool DemandProducer(float production) => production > 0;
        // The demanding port's supply drops to -its capacity, and the producing port's rises to +its own capacity (kept if already
        // higher). The user, 2026-10-16: was -100 (-capacity where less) and the same amount added at the producer.
        public static float DemandTarget(float cap) => -cap;
        public static float ProducerTarget(float cap, float supply) => Math.Max(supply, cap);
        // Solved once the demanding port's supply rises above -20% of its capacity (the user, 2026-10-16; was a flat -20).
        public static float DemandDone(float cap) => -DemandDoneShare * cap;
        public static bool DemandComplete(float supply, float cap) => supply > DemandDone(cap);
        public static bool DemandExpired(int startDay, int day) => day - startDay >= DemandExpiryDays;
        // Units still needed to lift the supply above that, less what other traders already carry there.
        public static int DemandNeed(float supply, int inFlight, float cap) =>
            DemandComplete(supply, cap) ? 0 : Math.Max(0, (int)Math.Floor(DemandDone(cap) - supply) + 1 - inFlight);
        // A departing trader counts the last departure from its port only on a 40% roll (the user, 2026-10-16; was always).
        public static bool KnowsLastDeparture(float roll) => roll < LastDepartureKnown;
        // A trader sails to an event's producer to fetch the good while the need is more than the holds already heading there can
        // carry (the user, 2026-10-16: traders don't over-ship).
        public static bool DemandFetch(int need, int unitsHeadingThere) => need > unitsHeadingThere;
        // News travels with the price reports (the user, 2026-10-16): a report of the demanding port, dated on or after the event's
        // start, that shows the good still short (at -20% of the capacity the report gives, or below).
        public static bool DemandReported(int reportDay, float reportSupply, int startDay, float reportCap) => reportDay >= startDay && !DemandComplete(reportSupply, reportCap);
    }

    [Serializable] public sealed class DemandEvent
    {
        public int id, demand, producer, good, startDay;
        // amount: the units the demanding port lost; added: the units the producing port gained (0 when it was already at capacity).
        public float amount;
        [System.Runtime.Serialization.OptionalField] public float added;
    }
    [Serializable] public sealed class DemandState
    {
        public List<DemandEvent> events = new List<DemandEvent>();
        // Picked once per round (goods; ports as demanding or producing), and the events the player has heard of.
        public List<int> pickedGoods = new List<int>(), pickedPorts = new List<int>(), heard = new List<int>();
        public int nextId;
    }

    internal static class Demand
    {
        internal static DemandState State => World.State.demand ?? (World.State.demand = new DemandState());
        internal static IEnumerable<DemandEvent> Active => World.State?.demand?.events ?? Enumerable.Empty<DemandEvent>();
        private static int[] Ports() => Supply.ManagedRows().Select(r => r.Port).Where(p => Rules.DemandPort(p) && World.Market(p)).ToArray();
        private static int[] Goods() => Enumerable.Range(1, 64).Where(Rules.DemandGood).ToArray();

        // Midnight (and the end of a new game's preparation): end finished and expired events, then fill up to 5.
        // Set only by the -DemandStudy suite's control run (no events at all); false in play.
        internal static bool StudyOff;
        internal static void Midnight(int day)
        {
            if (!World.Ready || StudyOff) return;
            var s = State;
            s.events.RemoveAll(e => !Supply.Contains(e.demand) || !Supply.Contains(e.producer) || !World.Market(e.demand) || !World.Market(e.producer));
            // Completion first: an event solved on its last day is complete, not expired (demand-study-9 counted 3 of 47 so).
            CheckAll();
            foreach (var e in s.events.ToArray()) if (Rules.DemandExpired(e.startDay, day)) End(e, "expired");
            for (int i = s.events.Count; i < Rules.DemandEvents; i++) if (!Create(day)) break;
        }
        internal static void CheckAll() { foreach (var e in Active.ToArray()) Check(e.demand, e.good); }
        // An event is complete once its demanding port's supply rises above -20% of its capacity.
        internal static void Check(int port, int good)
        {
            var s = World.State?.demand; if (s == null) return;
            foreach (var e in s.events.ToArray())
            {
                if (e.demand != port || e.good != good) continue;
                var m = World.Market(port);
                if (m && Rules.DemandComplete(m.currentSupply[good], Economy.Cap(port))) End(e, "complete");
            }
        }
        private static void End(DemandEvent e, string how)
        {
            State.events.Remove(e);
            foreach (var b in World.State.fleet) b.knownEvents?.Remove(e.id);
            Plugin.Log?.LogInfo($"Demand event {e.id} {how}: good {e.good} at port {e.demand} (producer {e.producer}), day {e.startDay}.");
        }
        private static bool Create(int day)
        {
            var s = State; int[] ports = Ports(), goods = Goods();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (goods.All(s.pickedGoods.Contains)) { s.pickedGoods.Clear(); s.pickedPorts.Clear(); }
                if (ports.Count(p => !s.pickedPorts.Contains(p)) < 2) s.pickedPorts.Clear();
                var free = ports.Where(p => !s.pickedPorts.Contains(p)).OrderBy(_ => UnityEngine.Random.value).ToArray();
                foreach (int port in free)
                {
                    var m = World.Market(port); if (!Supply.TryGet(port, out var row)) continue;
                    float cap = Economy.Cap(port);
                    // A good fits only if some other eligible port makes it, and not while this port's own event for it runs (a new
                    // round of goods can pick it again; Claude's fix 2026-10-17: two events would share one shortage).
                    int[] Makers(int g) => ports.Where(p => p != port && Supply.TryGet(p, out var r) && Rules.DemandProducer(r.Production[g])).ToArray();
                    var fit = goods.Where(g => !s.pickedGoods.Contains(g) && g < m.currentSupply.Length && Rules.DemandFits(row.Production[g], m.currentSupply[g], cap) &&
                        !s.events.Any(e => e.demand == port && e.good == g) && Makers(g).Length > 0).ToArray();
                    if (fit.Length == 0) continue;
                    int good = fit[UnityEngine.Random.Range(0, fit.Length)];
                    // Its makers not yet picked this round first; when every maker is picked, a maker may repeat.
                    var makers = Makers(good); var producers = makers.Where(free.Contains).ToArray(); if (producers.Length == 0) producers = makers;
                    int producer = producers[UnityEngine.Random.Range(0, producers.Length)];
                    var supplier = World.Market(producer);
                    float target = Rules.DemandTarget(cap), amount = m.currentSupply[good] - target;
                    m.currentSupply[good] = target;
                    float before = supplier.currentSupply[good];
                    supplier.currentSupply[good] = Rules.ProducerTarget(Economy.Cap(producer), before);
                    Economy.Refresh(m); Economy.Refresh(supplier);
                    var e = new DemandEvent { id = ++s.nextId, demand = port, producer = producer, good = good, startDay = day, amount = amount, added = supplier.currentSupply[good] - before };
                    s.events.Add(e); s.pickedGoods.Add(good); s.pickedPorts.Add(port); s.pickedPorts.Add(producer);
                    Plugin.Log?.LogInfo($"Demand event {e.id}: good {good} at port {port} drops to {target} ({amount:F1} short); port {producer} raised to {supplier.currentSupply[good]:F0}, day {day}.");
                    return true;
                }
                s.pickedPorts.Clear();   // no unpicked port fits: start the ports' round again (plan: rounds of picking)
            }
            return false;
        }
        // A trader learns the events of the reports it carries (a demanding port's report that shows the shortage).
        internal static void LearnFromReports(BoatState b)
        {
            foreach (var e in Active)
                if (b.reports.Any(r => r.port == e.demand && r.supply != null && e.good < r.supply.Length && Rules.DemandReported(r.day, r.supply[e.good], e.startDay, r.cap)) &&
                    !(b.knownEvents ?? (b.knownEvents = new List<int>())).Contains(e.id)) b.knownEvents.Add(e.id);
        }
        // A trader learns the events of a port it arrives at or leaves (their demanding or producing port).
        internal static void Learn(BoatState b, int port)
        {
            foreach (var e in Active)
                if ((e.demand == port || e.producer == port) && !(b.knownEvents ?? (b.knownEvents = new List<int>())).Contains(e.id)) b.knownEvents.Add(e.id);
        }
        // The event a rumour NPC at this port tells (Rules.PickNews).
        internal static DemandEvent News(int port)
        {
            var here = Port.ports != null && port >= 0 && port < Port.ports.Length ? Port.ports[port] : null; if (!here) return null;
            var list = Active.Where(e => e.demand >= 0 && e.demand < Port.ports.Length && Port.ports[e.demand])
                .Select(e => (e.id, Port.ports[e.demand].region == here.region, Fleet.Distance(port, e.demand))).ToList();
            int id = Rules.PickNews(list, State.heard);
            return Active.FirstOrDefault(e => e.id == id);
        }
        // Event goods other traders carry to a port (they know what is on the way, the user's rule).
        internal static int InFlight(BoatState self, int port, int good) =>
            World.State.fleet.Where(o => o != self && o.destination == port).Sum(o => o.goods.Count(g => g == good));
        // Told of every trader departure and its purpose: 0 normal trade, 1 serving an event, 2 sailing to an event's producer. Set
        // only by the -DemandStudy suite; null in play.
        internal static Action<BoatState, int> OnDepart;
        // The event a trader that can't serve from here sails to fetch: one it knows whose producing port it can reach from here and
        // whose demanding port it can reach from there, with more need than the informed holds already heading to that producer
        // can carry; the most needed first. canSail(from, to): the trader's own route rule.
        internal static DemandEvent Fetch(BoatState b, Func<int, int, bool> canSail)
        {
            if (b.knownEvents == null || b.knownEvents.Count == 0) return null;
            DemandEvent best = null; int most = 0;
            foreach (var e in Active)
            {
                if (!b.knownEvents.Contains(e.id) || e.producer == b.origin || !canSail(b.origin, e.producer) || !canSail(e.producer, e.demand)) continue;
                var m = World.Market(e.demand); if (!m) continue;
                int need = Rules.DemandNeed(m.currentSupply[e.good], InFlight(b, e.demand, e.good), Economy.Cap(e.demand));
                int heading = World.State.fleet.Where(o => o != b && o.destination == e.producer && o.knownEvents != null && o.knownEvents.Contains(e.id)).Sum(o => o.units);
                if (Rules.DemandFetch(need, heading) && need - heading > most) { most = need - heading; best = e; }
            }
            return best;
        }
        // The event an informed trader at this port serves first: one whose good it can buy here and whose demanding port it
        // may sail to, with need left; the most needed first.
        internal static DemandEvent Serve(BoatState b, IslandMarket origin, Func<int, bool> route, out int need)
        {
            need = 0; DemandEvent best = null;
            if (b.knownEvents == null || b.knownEvents.Count == 0) return null;
            foreach (var e in Active)
            {
                if (!b.knownEvents.Contains(e.id) || e.demand == b.origin || !origin.HasGood(e.good) || !route(e.demand)) continue;
                var m = World.Market(e.demand); if (!m) continue;
                int n = Rules.DemandNeed(m.currentSupply[e.good], InFlight(b, e.demand, e.good), Economy.Cap(e.demand));
                if (n > need) { need = n; best = e; }
            }
            return best;
        }
    }
    // Any sale (player, trader, mission delivery) can lift a demanding port's supply above -20.
    [HarmonyPatch(typeof(IslandMarket), "SellGood")] internal static class DemandSalePatch
    {
        static void Postfix(IslandMarket __instance, int goodIndex) { if (World.Ready && !Startup.Running) Demand.Check(__instance.GetPortIndex(), goodIndex); }
    }

    // ---- The tavern news (stage 3; DEMAND.md, the approved dialogue) ----
    public static partial class Rules
    {
        public const float TavernFoodReach = 10;   // a tavern's own counter: 11-36 spawners within 10 m of every rumour NPC (level files)
        public const string AskButton = "What news of other captains?", GiveButton = "Give food", WhereButton = "And where might I find such goods?", AgeButton = "How old is this news?";
        public static string FoodLine(string food) => "Talk of other captains, is it? That's hungry work. Fetch us a plate of " + food + " and I'll tell you what I hear.";
        private static readonly string[] LocalDemand = {
            "Captains are flocking to {0}, mark me. They cannot get enough {1} there, and there's gold in it for whoever brings it.",
            "Word is {0} is crying out for {1}. Half the captains in this house have set their course for it already.",
            "There's not a crate of {1} left in {0}, they say. Any captain with a full hold will be paid handsomely." };
        private static readonly string[] ForeignDemand = {
            "We don't need anything here, praise the {4}. But {0} in {2} is in sore straits: there's no {1} to be had.",
            "Things are well enough here. 'Tis {0} in {2} that suffers; not a barrel of {1} in the whole town, I'm told.",
            "A crew put in from {2}. They say {0} has gone so long without {1} its merchants pay whatever is asked." };
        private static readonly string[] LocalProducer = {
            "Fortune smiles on you, captain! A patron told me {0} is drowning in {1} and selling it for a dime.",
            "Not far at all. {0} has more {1} than it knows what to do with; its merchants will take near any price.",
            "Try {0}, close by. Their warehouses are bursting with {1}, and they'll sell it cheap to be rid of it." };
        private static readonly string[] ForeignProducer = {
            "A fellow just back from {2} was boasting how cheap {1} is in {0}. Worth the voyage, I'd wager.",
            "You'll have to sail for it. In {0}, over in {2}, there's so much {1} they all but give it away.",
            "Ask in {2}. A merchant who sat at that very table swore {0} sells {1} cheaper than anywhere." };
        private static readonly string[] Age = {
            "The first ships set out for {0} {3}, so you'd best make haste. My thanks for the meal!",
            "This began {3}. Others will beat you to it if you dally. Much obliged for the food!",
            "It started {3}; the first sails are already over the horizon. Fair winds, and thank you kindly for the meal." };
        public static string When(int days) => days <= 0 ? "this very day" : days == 1 ? "but a day ago" : days + " days ago";
        // The ruler praised in the NPC's own region (the user, 2026-10-16): the Sultan in Al'Ankh, the Trade Prince in Emerald
        // (Dragon Cliffs), the King in Aestrin.
        public static string Ruler(int region) => region == 0 ? "Sultan" : region == 1 ? "Trade Prince" : "King";
        // kind: 0 demand, 1 producer, 2 age; local: the port in the NPC's own region; ruler: his own region's.
        public static string NewsLine(int kind, bool local, int variant, string port, string good, string region, int days, string ruler = "Sultan")
        {
            var set = kind == 0 ? (local ? LocalDemand : ForeignDemand) : kind == 1 ? (local ? LocalProducer : ForeignProducer) : Age;
            return string.Format(set[((variant % set.Length) + set.Length) % set.Length], port, good, region, When(days), ruler);
        }
        // Which news (the user's rule): the latest unheard event whose demanding port is in this region, else the closest unheard;
        // with all heard, the latest local one, else the closest. events: (id, local, distance); returns the id or -1.
        public static int PickNews(IList<(int id, bool local, double distance)> events, ICollection<int> heard)
        {
            int Pick(IEnumerable<(int id, bool local, double distance)> from)
            {
                var list = from.ToList(); if (list.Count == 0) return -1;
                var locals = list.Where(e => e.local).ToList();
                return locals.Count > 0 ? locals.Max(e => e.id) : list.OrderBy(e => e.distance).ThenByDescending(e => e.id).First().id;
            }
            int unheard = Pick(events.Where(e => !heard.Contains(e.id)));
            return unheard >= 0 ? unheard : Pick(events);
        }
        public static string Wrap(string text, int width = 33)
        {
            var result = new System.Text.StringBuilder(); int line = 0;
            foreach (string word in text.Split(' ')) { if (line > 0 && line + word.Length + 1 > width) { result.Append('\n'); line = 0; } else if (line > 0) { result.Append(' '); line++; } result.Append(word); line += word.Length; }
            return result.ToString();
        }
    }

    // One rumour NPC's talk: its button (a clone of the drink button aimed at this component), the food asked for, the event told.
    internal sealed class DemandTalk : MonoBehaviour, IGPButton
    {
        internal enum Step { None, Asking, WaitFood, FoodHere, Where, Age }
        internal TavernRumorsDude dude; internal Step step; internal int foodPrefab = -1; internal ShipItemFood food; internal DemandEvent told;
        private GameObject button; private TextMesh label;
        internal static DemandTalk For(TavernRumorsDude dude)
        {
            var talk = dude.GetComponent<DemandTalk>(); if (talk) return talk;
            talk = dude.gameObject.AddComponent<DemandTalk>(); talk.dude = dude; return talk;
        }
        private void Build()
        {
            if (button || !dude.drinkButton) return;
            var drink = dude.drinkButton;
            button = Instantiate(drink, drink.transform.parent); button.name = "economic news button";
            var size = drink.GetComponent<BoxCollider>() ? drink.GetComponent<BoxCollider>().size.y * drink.transform.localScale.y : .2f;
            // In the drink button's place, just under the bubble; one slot lower only while the drink button shows too (the user's
            // test of 0.10.1: always one slot lower, it stood far from the bubble once the drink button was gone; tavern-10).
            atDrink = drink.transform.localPosition; belowDrink = atDrink - drink.transform.localRotation * Vector3.up * size * 1.3f;
            button.transform.localPosition = atDrink;
            // The game's button activates the first IGPButton on its target (GPButtonInterface.OnActivate): on the NPC's own object
            // that is TavernRumorsDude (its drink), so the clone targets itself, where DemandButton passes the click to this talk
            // (the user's in-game test of 0.10.1: the button did nothing).
            var hook = button.GetComponentInChildren<GPButtonInterface>(true);
            if (hook) { hook.gameObject.AddComponent<DemandButton>().talk = this; hook.onActivateInterface = hook.gameObject; }
            label = button.GetComponentInChildren<TextMesh>(true);
            button.SetActive(false);
        }
        private Vector3 plateScale, labelScale, atDrink, belowDrink;
        private void LateUpdate()
        {
            if (!button || !button.activeSelf || !dude.drinkButton) return;
            var at = dude.drinkButton.activeSelf ? belowDrink : atDrink;
            if (button.transform.localPosition != at) button.transform.localPosition = at;
        }
        // The drink button's plate fits its short label: widened to fit ours (the label kept its own shape), the collider with it.
        internal void Show(string text)
        {
            Build(); if (!button) return;
            if (plateScale == Vector3.zero) { plateScale = button.transform.localScale; labelScale = label ? label.transform.localScale : Vector3.one; }
            button.transform.localScale = plateScale; if (label) { label.transform.localScale = labelScale; label.text = text; }
            button.SetActive(true);
            var shape = label ? label.GetComponent<Renderer>() : null; if (!shape || !label.transform.IsChildOf(button.transform)) return;
            // Widths across the bubble (its right, as the player sees it), the plate's from its own renderers.
            Vector3 across = dude.speechUI.transform.right;
            float Width(Bounds b) => Mathf.Abs(b.size.x * across.x) + Mathf.Abs(b.size.y * across.y) + Mathf.Abs(b.size.z * across.z);
            var plates = button.GetComponentsInChildren<Renderer>(true).Where(r => r != shape && !r.GetComponent<TextMesh>()).ToArray();
            if (plates.Length == 0) return;
            var plate = plates[0].bounds; foreach (var r in plates) plate.Encapsulate(r.bounds);
            float k = Mathf.Clamp(Width(shape.bounds) * 1.15f / Mathf.Max(.001f, Width(plate)), 1, 4);
            if (k <= 1.01f) return;
            int Axis(Transform tr) { var d = new[] { tr.right, tr.up, tr.forward }.Select(v => Mathf.Abs(Vector3.Dot(v.normalized, across))).ToArray(); return d[0] >= d[1] && d[0] >= d[2] ? 0 : d[1] >= d[2] ? 1 : 2; }
            Vector3 Stretch(Vector3 v, int axis, float f) { v[axis] *= f; return v; }
            button.transform.localScale = Stretch(plateScale, Axis(button.transform), k);
            label.transform.localScale = Stretch(labelScale, Axis(label.transform), 1 / k);
        }
        internal void Hide() { if (button) button.SetActive(false); }
        internal void Say(string text) { if (dude.text) dude.text.text = Rules.Wrap(text); dude.speechUI.SetActive(true); }
        // The game's buttons call this (GPButtonInterface).
        public void Activate()
        {
            UISoundPlayer.instance.PlayUISound(UISounds.smallPop, .75f, 1f);
            if (step == Step.Asking) { AskFood(); return; }
            if (step == Step.FoodHere) { GiveFood(); return; }
            if (step == Step.Where && told != null) { Say(Line(1)); step = Step.Age; Show(Rules.AgeButton); return; }
            if (step == Step.Age && told != null) { Say(Line(2)); step = Step.None; told = null; Hide(); }
        }
        private void AskFood()
        {
            var foods = Foods(true); if (foods.Count == 0) foods = Foods(false);
            var pick = foods.Count > 0 ? foods[UnityEngine.Random.Range(0, foods.Count)] : null;
            foodPrefab = pick ? pick.GetComponent<SaveablePrefab>().prefabIndex : -1;
            Say(Rules.FoodLine(pick ? pick.name : "something hot"));
            step = Step.WaitFood; Hide();
        }
        // Food sold at this tavern's counter (spawners within 10 m of him), else anywhere in his scene; walked from the scene's
        // own roots (no scene-wide object search: memory note on native crashes).
        private List<ShipItemFood> Foods(bool counter)
        {
            var result = new List<ShipItemFood>();
            foreach (var root in dude.gameObject.scene.GetRootGameObjects())
                foreach (var spawner in root.GetComponentsInChildren<ShopItemSpawner>(true))
                {
                    var food = spawner.itemPrefab ? spawner.itemPrefab.GetComponent<ShipItemFood>() : null;
                    if (!food || !food.GetComponent<SaveablePrefab>()) continue;
                    if (counter && Vector3.Distance(spawner.transform.position, dude.transform.position) > Rules.TavernFoodReach) continue;
                    if (!result.Contains(food)) result.Add(food);
                }
            return result;
        }
        internal void FoodArrived(ShipItemFood item)
        {
            if (step != Step.WaitFood || !item || !item.sold) return;
            var save = item.GetComponent<SaveablePrefab>();
            if (foodPrefab >= 0 && (!save || save.prefabIndex != foodPrefab)) return;
            food = item; step = Step.FoodHere; Show(Rules.GiveButton);
        }
        private void GiveFood()
        {
            if (!food || !food.sold) { step = Step.WaitFood; Hide(); return; }
            told = Demand.News(dude.portIndex);
            food.DestroyItem(); food = null; foodPrefab = -1;
            if (told == null) { Say("It's been quiet among the captains, I'm afraid. Thank you for the meal all the same."); step = Step.None; Hide(); return; }
            if (!Demand.State.heard.Contains(told.id)) Demand.State.heard.Add(told.id);
            Say(Line(0)); step = Step.Where; Show(Rules.WhereButton);
        }
        private string Line(int kind)
        {
            int port = kind == 1 ? told.producer : told.demand; var here = Port.ports[dude.portIndex]; var there = Port.ports[port];
            bool local = here && there && here.region == there.region;
            string good = PrefabsDirectory.instance.GetGood(told.good) ? PrefabsDirectory.instance.GetGood(told.good).name : "goods";
            return Rules.NewsLine(kind, local, UnityEngine.Random.Range(0, 3), there ? there.GetPortName() : "a far port", good,
                there ? BookUI.RegionName((int)there.region) : "abroad", GameState.day - told.startDay, Rules.Ruler(here ? (int)here.region : 0));
        }
        // The player went away: the button hides with the speech; the food asked for stays asked for.
        internal void Left() { Hide(); if (step == Step.FoodHere) { food = null; step = Step.WaitFood; } }
        // Back: the button of the step reached shows again (the user's test of 0.10.1: after a drink, the ask button never came back);
        // food asked for and already lying by him counts as brought.
        internal void Returned()
        {
            if (step == Step.Asking) Show(Rules.AskButton);
            else if (step == Step.Where) Show(Rules.WhereButton);
            else if (step == Step.Age) Show(Rules.AgeButton);
            else if (step == Step.WaitFood)
            {
                var zone = dude.GetComponent<Collider>(); if (!zone) return;
                foreach (var c in Physics.OverlapBox(zone.bounds.center, zone.bounds.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Collide))
                { var f = c.GetComponent<ShipItemFood>(); if (f) { FoodArrived(f); if (step == Step.FoodHere) return; } }
            }
        }
    }
    // The news button's own target (GPButtonInterface): the click goes to the talk, not to the NPC's drink.
    internal sealed class DemandButton : MonoBehaviour, IGPButton
    {
        internal DemandTalk talk;
        public void Activate() { if (talk) talk.Activate(); }
    }
    [HarmonyPatch(typeof(TavernRumorsDude), "ClickDrinkButton")] internal static class DemandAskPatch
    {
        static void Prefix(ShipItemBottle ___currentDrink, out bool __state) => __state = ___currentDrink;
        static void Postfix(TavernRumorsDude __instance, ShipItemBottle ___currentDrink, bool __state)
        {
            if (!__state || ___currentDrink || !World.Ready || !Demand.Active.Any()) return;   // a drink was taken; there is news
            var talk = DemandTalk.For(__instance);
            if (talk.step == DemandTalk.Step.None || talk.step == DemandTalk.Step.Asking) { talk.step = DemandTalk.Step.Asking; talk.Show(Rules.AskButton); }
        }
    }
    [HarmonyPatch(typeof(TavernRumorsDude), "OnTriggerEnter")] internal static class DemandFoodPatch
    {
        static void Postfix(TavernRumorsDude __instance, Collider other)
        {
            var talk = __instance.GetComponent<DemandTalk>(); if (!talk) return;
            var food = other.GetComponent<ShipItemFood>(); if (food) { talk.FoodArrived(food); return; }
            if (other.CompareTag("Player")) talk.Returned();
        }
    }
    [HarmonyPatch(typeof(TavernRumorsDude), "OnTriggerExit")] internal static class DemandLeavePatch
    {
        static void Postfix(TavernRumorsDude __instance, Collider other) { var talk = __instance.GetComponent<DemandTalk>(); if (talk && other.CompareTag("Player")) talk.Left(); }
    }
}

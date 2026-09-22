using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

// Full-act route search. The map is a DAG from the current point to the boss, so
// value(point) = node value + discount * best child value: this looks past the
// next room instead of greedily taking the highest immediate node.
internal static class RoutePlanner
{
    internal sealed record Route(IReadOnlyList<MapPoint> Path, double Score);

    // Future rooms are worth slightly less than an equally good room now, which
    // keeps the planner from hoarding value behind a long detour.
    private const double Discount = 0.92;

    /// <summary>
    /// Can the GAME actually build this room in this act? Route selection may only
    /// choose rooms the game can construct.
    /// </summary>
    /// <remarks>
    /// <see cref="RunManager.CreateRoom"/> builds a treasure room with
    /// <c>new TreasureRoom(State.CurrentActIndex)</c> and does NOT range-check the act,
    /// while <c>TreasureRoom(int actIndex)</c> throws unless <c>0 &lt;= actIndex &lt;= 2</c>
    /// (`TreasureRoom.cs:25`). <c>Glory</c> is the fourth act, index 3 — so a treasure node
    /// on its map throws <c>ArgumentOutOfRangeException</c> the moment it is entered,
    /// through the game's own click chain (<c>NMapScreen.TravelToMapCoord</c> ←
    /// <c>MoveToMapCoordAction</c>), which leaves the run with <c>room=null roomStack=0</c>
    /// and no way forward. Measured live 2026-09-20: it ended a 25-fight unattended run on
    /// floor 54. A human clicking the same node crashes identically, so this is a vanilla
    /// bug — the only thing to do on our side is not walk into it.
    ///
    /// <para>
    /// This is the ONE refusal R4 allows: a room that cannot be constructed is not
    /// executable. It is deliberately narrow — it does not price the room differently,
    /// it does not prefer other room types, and it changes nothing in acts 0-2, where
    /// every room type still builds. Every skip is logged (see
    /// <see cref="UnbuildableChildren"/>), so a reader can never mistake "the router
    /// avoided it" for "the router happened not to want it".
    /// </para>
    /// </remarks>
    internal static bool IsBuildable(MapPointType type, int act)
        => UnbuildableReason(type, act).Length == 0;

    /// <summary>Why the game cannot build this room here, or "" when it can.</summary>
    internal static string UnbuildableReason(MapPointType type, int act)
        => type == MapPointType.Treasure && act > 2
            ? $"vanilla TreasureRoom only supports act 0-2 but this is act {act + 1} (index {act})"
            : "";

    /// <summary>
    /// The DP value that means "there is no route through here at all".
    /// </summary>
    /// <remarks>
    /// It is deliberately NON-FINITE, not "a very negative number". A finite sentinel looks
    /// equivalent and is not: <c>score = Discount * childScore</c> and
    /// <c>value(point) + bestChild</c> are sums, so a finite penalty can be outweighed by a
    /// rich subtree and the planner will route straight into a room the game cannot build.
    /// Non-finite absorbs every sum and comparison it touches, which is what makes the room
    /// genuinely unreachable rather than merely unattractive.
    /// </remarks>
    internal static double UnreachableValue => double.NegativeInfinity;

    /// <summary>
    /// The children of the current point this router will refuse to travel to, with the
    /// reason. Empty when everything reachable is buildable. Callers log it — the planner
    /// itself must not write to the log.
    /// </summary>
    internal static IEnumerable<(MapCoord Coord, string Reason)> UnbuildableChildren(RunState state)
    {
        var from = state.CurrentMapPoint ?? state.Map?.StartingMapPoint;
        if (from is null) yield break;
        var act = state.CurrentActIndex;
        foreach (var child in from.Children)
        {
            var reason = UnbuildableReason(child.PointType, act);
            if (reason.Length > 0) yield return (child.coord, reason);
        }
    }

    /// <summary>
    /// EVERY map point this router will refuse, anywhere in the act — not just the current
    /// node's children.
    /// </summary>
    /// <remarks>
    /// The children-only version above is what a caller logs when it is about to vote, and that
    /// turned out to be the wrong set for diagnosing a NO-ROUTE. Measured live 2026-09-20
    /// (live9 and live12, both stranded at `room=Map floor=48` on the act-4 Glory map): the router
    /// returned null for every vote for four straight minutes, and the children log stayed silent
    /// — because the blocking node is DEEPER in the map. Silence from a log that only covers one
    /// row is not evidence, and it very nearly cleared the exclusion that caused the stall.
    /// </remarks>
    internal static IEnumerable<(MapCoord Coord, string Reason)> UnbuildableNodes(RunState state)
    {
        if (state.Map is null) yield break;
        var act = state.CurrentActIndex;
        foreach (var point in state.Map.GetAllMapPoints())
        {
            var reason = UnbuildableReason(point.PointType, act);
            if (reason.Length > 0) yield return (point.coord, reason);
        }
    }

    internal static Route? Plan(RunState state, Player human, bool teamRoute = false)
    {
        var start = state.CurrentMapPoint ?? state.Map.StartingMapPoint;
        var boss = state.Map.BossMapPoint;
        if (start is null || boss is null) return null;
        var alive = state.Players.Where(p => p.Creature.IsAlive).ToArray();
        var health = alive
            .Select(p => (double)p.Creature.CurrentHp / Math.Max(1, p.Creature.MaxHp))
            .DefaultIfEmpty(1).Min();
        var shoppers = alive.Count(p => p.Gold >= 100);
        var act = state.CurrentActIndex;
        // ALL-BOT ROUTE: one shared vote. MapSelectionSynchronizer picks a RANDOM
        // submitted coordinate, so four per-seat routes can random-walk the team past
        // the room the healthiest deck wanted. Use the party's average deck when every
        // seat is driven, and the same inputs for every seat, so all four votes agree.
        var deckPlayers = teamRoute && alive.Length > 0 ? alive : new[] { human };
        var deckSize = (int)Math.Round(deckPlayers.Average(p => (double)p.Deck.Cards.Count));
        // Walk the act with resource state: a second rest is worth less than the
        // first, a shop after spending is worth less than one while rich.
        var maturity = deckPlayers.Average(DeckMaturity);
        // Shared route => every living seat's relics can change the room; a single-seat
        // recommendation uses that seat's own relics. A dead seat adds no route value.
        var relics = RouteRelicProfile.FromPlayers(teamRoute ? alive : new[] { human });
        // A room the game cannot build is not merely worth less — it is unreachable, and
        // the DP already reads non-finite as unreachable (it skips such children and
        // returns null rather than a route that ends in one). So the executability rule
        // rides on machinery that already exists instead of a second filter in the search.
        Func<MapPointType, int, int, double> Value = (type, hpTier, goldTier) =>
            ValueForNode(type, hpTier, goldTier, act, deckSize, maturity, shoppers, teamRoute, relics);
        return Plan(start, boss, Value, Tier(health, .4, .75), GoldTier(human.Gold));
    }

    /// <summary>
    /// One node's route value with the state the DP carries. Kept separate from the
    /// closure so the rich-shop rule is testable without building a whole RunState/map.
    /// </summary>
    internal static double ValueForNode(MapPointType type, int hpTier, int goldTier, int act,
        int deckSize, double maturity, int shoppers, bool teamRoute = false,
        RouteRelicProfile? relics = null)
    {
        if (!IsBuildable(type, act)) return UnreachableValue;
        var value = NodeValue(type, RepresentativeHealth(hpTier), (int)RepresentativeGold(goldTier),
            act, deckSize, maturity, teamRoute, relics);
        return type == MapPointType.Shop ? value + Math.Min(15, shoppers * 5) : value;
    }

    /// <summary>
    /// Gold at or above this makes “reach a shop and spend it” the route's first job.
    /// The live case was 500+ gold while the planner kept shopping for value as if the
    /// purse would still be there next act; the gold is dead weight until spent, and the
    /// route is the only thing that can get it to a counter.
    /// </summary>
    internal const int RichGoldThreshold = 500;

    /// <summary>
    /// A rich shop is worth far more than a normal one. It is still a finite node score,
    /// so a map whose only path runs through something unbuildable remains unreachable;
    /// the 0.92 discount then makes the NEAREST shop the best rich route.
    /// </summary>
    private const double RichShopValue = 450;

    internal static int Tier(double ratio, double low, double high) => ratio < low ? 0 : ratio < high ? 1 : 2;
    internal static int GoldTier(int gold) =>
        gold < 100 ? 0 : gold < 250 ? 1 : gold < RichGoldThreshold ? 2 : 3;
    internal static double RepresentativeHealth(int tier) => tier switch { 0 => .3, 1 => .6, _ => .9 };
    internal static double RepresentativeGold(int tier) => tier switch { 0 => 50, 1 => 180, 2 => 350, _ => 600 };

    /// <summary>
    /// State-propagating DP: the value of a room depends on the resources the
    /// team would have when reaching it, and visiting a room changes them.
    /// </summary>
    internal static Route? Plan(MapPoint start, MapPoint boss, Func<MapPointType, int, int, double> value,
        int healthTier, int goldTier, int maxNodes = 512)
    {
        var memo = new Dictionary<(MapPoint, int, int), (double Score, MapPoint? Next)>();
        var visiting = new HashSet<(MapPoint, int, int)>();
        var expanded = 0;

        static (int Health, int Gold) Advance(MapPointType type, int health, int gold) => type switch
        {
            // The >=500 "rich" tier has to SURVIVE the rooms between it and the shop, or
            // the shop cannot be found in time. It must not be CREATED by a fight either:
            // tier 2 is 250-499, and a normal room reward does not always cross 500.
            MapPointType.Monster => (Math.Max(0, health - 1), gold >= 3 ? 3 : Math.Min(2, gold + 1)),
            MapPointType.Elite => (Math.Max(0, health - 2), gold >= 3 ? 3 : Math.Min(2, gold + 1)),
            MapPointType.RestSite => (Math.Min(2, health + 2), gold),
            MapPointType.Shop => (health, Math.Max(0, gold - 1)),
            MapPointType.Treasure => (health, gold >= 3 ? 3 : Math.Min(2, gold + 1)),
            MapPointType.Unknown => (health, gold >= 3 ? 3 : Math.Min(2, gold + 1)),
            _ => (health, gold),
        };

        (double Score, MapPoint? Next) Best(MapPoint point, int health, int gold)
        {
            if (ReferenceEquals(point, boss)) return (value(point.PointType, health, gold), null);
            var key = (point, health, gold);
            if (memo.TryGetValue(key, out var cached)) return cached;
            if (!visiting.Add(key)) return (double.NegativeInfinity, null); // cycle guard
            var (nextHealth, nextGold) = Advance(point.PointType, health, gold);
            var bestChild = double.NegativeInfinity;
            MapPoint? bestNext = null;
            foreach (var child in point.Children.OrderBy(c => c.coord.ToString(), StringComparer.Ordinal))
            {
                if (expanded++ >= maxNodes) break;
                var (childScore, _) = Best(child, nextHealth, nextGold);
                if (!double.IsFinite(childScore)) continue;
                var score = Discount * childScore;
                if (score > bestChild) { bestChild = score; bestNext = child; }
            }
            visiting.Remove(key);
            var result = bestNext is null
                ? (double.NegativeInfinity, (MapPoint?)null)
                : (value(point.PointType, health, gold) + bestChild, bestNext);
            memo[key] = result;
            return result;
        }

        var (total, _) = Best(start, healthTier, goldTier);
        if (!double.IsFinite(total)) return null;
        var path = new List<MapPoint> { start };
        var cursor = start;
        var hp = healthTier;
        var gold = goldTier;
        while (path.Count <= maxNodes && memo.TryGetValue((cursor, hp, gold), out var step) && step.Next is not null)
        {
            (hp, gold) = Advance(cursor.PointType, hp, gold);
            cursor = step.Next;
            path.Add(cursor);
        }
        return new Route(path, total);
    }

    /// <summary>
    /// Value of one map room. Unknown rooms outrank a plain fight: they are the
    /// event/relic/removal route at little or no combat cost, which matters most
    /// in the opening act. Later acts make them riskier, and a fight is worth a
    /// little more while the deck still needs cards.
    /// </summary>
    /// <summary>
    /// What a first card removal costs. Below this a shop cannot do the thing it is entered for,
    /// so the node is scored as wasted — see <see cref="NodeValue"/>.
    /// </summary>
    private const int ShopRemovalCost = 75;

    internal static double NodeValue(MapPointType type, double health, int gold, int act, int deckSize,
        double maturity = .5, bool teamRoute = false, RouteRelicProfile? relics = null)
    {
        var unknown = (act <= 0 ? 22 : act == 1 ? 16 : 13) - (health < .4 ? 12 : 0);
        var monster = (act <= 0 ? 9 : act == 1 ? 7 : 5) + (deckSize < 15 ? 4 : 0) - (health < .4 ? 25 : 0);
        var value = type switch
        {
            MapPointType.RestSite => health < .65 ? 60 : 18,
            // A SHOP YOU CANNOT REMOVE A CARD AT IS A WASTED NODE.
            //
            // The threshold was 180 and the penalty below it only -10 — mild enough that the
            // shop still won whenever the alternatives scored lower, which is exactly what was
            // watched live 2026-09-21: `shop: entered for seat …` immediately followed by
            // `shop: seat … settled without a purchase`. The node was spent and nothing bought.
            //
            // A first card removal costs 75 gold (BotShopPlanner names the same number), so
            // that is the least a shop can be useful with. Below it the node is worth about a
            // wasted step, and the penalty now says so. Deliberately NOT an absolute exclusion:
            // a shop that is the only way forward must stay reachable, and -60 loses to almost
            // everything without becoming unreachable.
            MapPointType.Shop => gold >= RichGoldThreshold
                ? RichShopValue
                : gold >= ShopRemovalCost ? 35 : -60,
            MapPointType.Elite => EliteValue(health, act, maturity, teamRoute),
            MapPointType.Treasure => 45,
            MapPointType.Monster => monster,
            MapPointType.Unknown => unknown,
            MapPointType.Ancient => 40,
            _ => 0,
        };
        return relics is null ? value : value + RelicNodeBonus(type, health, gold, act, relics);
    }

    /// <summary>
    /// Relic-driven room value, split by the three batches the relic scan produced:
    /// 1. shared-room relics (Juzu Bracelet, Miniature Tent) use Any(): one holder changes the
    ///    shared room for everyone;
    /// 2. self-only relics (Eternal Feather, Shovel, …) count holders, because only those seats
    ///    collect the room's extra benefit;
    /// 3. conditional relics (Black Star, Membership Card, Maw Bank, …) are priced only in the
    ///    room they modify and keep their downside.
    ///
    /// Magnitudes are deliberately small relative to the base room values: a relic must move the
    /// branch, not replace the room's own HP/act/deck evidence.
    ///
    /// PATH-CONTROL RELICS ARE DELIBERATELY ABSENT. Golden Compass replaces the Act 2 map before
    /// the DP sees it, so the topology already encodes its effect; Winged Boots changes which
    /// nodes are REACHABLE, not what a room is worth, and would need a map-adjacency change
    /// rather than a node bonus (faking it as node value would route toward unrelated rooms).
    /// </summary>
    private static double RelicNodeBonus(MapPointType type, double health, int gold, int act,
        RouteRelicProfile relics) => type switch
    {
        MapPointType.Unknown => UnknownRelicBonus(health, relics),
        MapPointType.Monster => MonsterRelicBonus(health, relics),
        MapPointType.Elite => EliteRelicBonus(health, relics),
        MapPointType.RestSite => RestSiteRelicBonus(relics),
        MapPointType.Shop => ShopRelicBonus(gold, relics),
        MapPointType.Treasure => TreasureRelicBonus(relics),
        MapPointType.Boss => BossRelicBonus(act, relics),
        _ => 0,
    };

    private static double UnknownRelicBonus(double health, RouteRelicProfile relics)
    {
        var value = 0.0;
        // One holder removes regular enemy combats from the shared ? room; +12 also cancels the
        // low-health unknown penalty above, which is exactly the risk this relic removes.
        if (relics.Any("JuzuBracelet")) value += 12;
        value += relics.HolderCount("Planisphere") * 5;
        // Fur Coat marks random combats; when a ? room rolls into one it is the marked case.
        if (relics.Any("FurCoat")) value += 4;
        return value;
    }

    private static double MonsterRelicBonus(double health, RouteRelicProfile relics)
    {
        var value = 0.0;
        if (relics.Any("FurCoat")) value += 10;
        value += relics.HolderCount("PrayerWheel") * 6;
        value += relics.HolderCount("AmethystAubergine") * 4;
        value += relics.HolderCount("BowlerHat") * 4;
        value += relics.HolderCount("WhiteBeastStatue") * 4;
        value += relics.HolderCount("FishingRod") * 3;
        value += relics.HolderCount("LastingCandy") * 3;
        value += relics.HolderCount("PaelsWing") * 3;
        value += relics.HolderCount("Glitter") * 2;
        value += relics.HolderCount("WingCharm") * 2;
        value += relics.HolderCount("DingyRug") * 2;
        value += relics.HolderCount("Driftwood") * 2;
        value += relics.HolderCount("PrismaticGem") * 2;
        // Lava Lamp only pays on a no-damage fight, so it cannot rescue a hurt party.
        if (health >= .85) value += relics.HolderCount("LavaLamp") * 5;
        return value;
    }

    private static double EliteRelicBonus(double health, RouteRelicProfile relics)
    {
        // The base already keeps a hurt party out of elites (-45 below .7 health). Reward relics
        // must not override that survival gate; the team still has to be able to beat the elite.
        if (health < .7) return 0;
        var value = 0.0;
        value += relics.HolderCount("BlackStar") * 14;
        value += relics.HolderCount("WhiteStar") * 9;
        value += relics.HolderCount("WarHammer") * 8;
        value += relics.HolderCount("SwordOfStone") * 4;
        value += relics.HolderCount("BoomingConch") * 6;
        value += relics.HolderCount("SlingOfCourage") * 6;
        if (relics.Any("FurCoat")) value += 4;
        return value;
    }

    private static double RestSiteRelicBonus(RouteRelicProfile relics)
    {
        var value = 0.0;
        // Shared: one holder can choose any number of options, which in multiplayer includes
        // MEND for a teammate while still smithing/healing.
        if (relics.Any("MiniatureTent")) value += 18;
        value += relics.HolderCount("EternalFeather") * 4;
        value += relics.HolderCount("DreamCatcher") * 5;
        value += relics.HolderCount("RegalPillow") * 4;
        value += relics.HolderCount("TinyMailbox") * 3;
        value += relics.HolderCount("Shovel") * 6;
        value += relics.HolderCount("Girya") * 5;
        value += relics.HolderCount("MeatCleaver") * 4;
        value += relics.HolderCount("StoneHumidifier") * 4;
        value += (relics.HolderCount("VenerableTeaSet") + relics.HolderCount("FakeVenerableTeaSet")) * 4;
        value += relics.HolderCount("PumpkinCandle") * 3;
        return value;
    }

    private static double ShopRelicBonus(int gold, RouteRelicProfile relics)
    {
        var value = 0.0;
        // One holder can buy out the entire merchant; the rest of the party does not have to
        // reach the removal threshold for this to be worth a detour.
        if (relics.Any("LordsParasol")) value += 120;
        var membership = relics.HolderCount("MembershipCard");
        var courier = relics.HolderCount("TheCourier");
        if (gold >= ShopRemovalCost)
        {
            value += membership * 12 + courier * 18;
        }
        else if (gold > 0)
        {
            // A discount can make a below-removal purse useful, but it is still a weak shop.
            value += membership * 6 + courier * 6;
        }
        value += relics.HolderCount("MealTicket") * 5;
        // Maw Bank pays per floor and stops forever on the first purchase; entering a shop has a
        // real opportunity cost for its holder.
        value -= relics.HolderCount("MawBank") * 18;
        // No future gold means the shop's usual jobs (removal, cards) are mostly gone.
        value -= relics.HolderCount("Ectoplasm") * 25;
        // No potions removes one of the shop's shelves; Seal of Gold drains the purse it needs.
        value -= relics.HolderCount("Sozu") * 8;
        value -= relics.HolderCount("SealOfGold") * 8;
        return value;
    }

    private static double TreasureRelicBonus(RouteRelicProfile relics)
        => relics.HolderCount("SilverCrucible") * -30;

    private static double BossRelicBonus(int act, RouteRelicProfile relics)
    {
        var value = relics.HolderCount("Pantograph") * 5;
        // Lava Rock only adds to the Act 1 boss reward; later bosses get no bonus.
        if (act <= 0) value += relics.HolderCount("LavaRock") * 8;
        return value;
    }

    // Elites scale much harder than the act's normal fights, so their value must
    // fall with the act and with how ready the deck is. A healthy team with a
    // decent deck should still TAKE the act-1/2 elite: the relic/gold/card is the
    // payoff that makes the boss winnable, and "avoid every elite to save HP"
    // under-builds the exact deck the boss checks. Act 3 stays cautious unless the
    // deck is genuinely mature; the all-bot team route adds a small aggression
    // term because there is no human seat whose HP must be protected.
    private static double EliteValue(double health, int act, double maturity, bool teamRoute)
    {
        if (health < .7) return -45;
        var byAct = act <= 0 ? 28 : act == 1 ? 14 : -8;
        var deckAdjust = (Math.Clamp(maturity, 0, 1) - 0.55) * 60;
        var healthyBonus = health >= .85 ? 8 : 0;
        var teamBonus = teamRoute ? 6 : 0;
        return byAct + deckAdjust + healthyBonus + teamBonus;
    }

    /// <summary>
    /// Rough deck readiness: size, scaling powers and upgrades. Used only as a
    /// coarse signal for long-term choices (elite worth, advice), not combat.
    /// </summary>
    internal static double DeckMaturity(Player player)
    {
        var deck = player.Deck.Cards;
        var size = Math.Clamp((deck.Count(context => context.Type != CardType.Curse) - 10) / 15.0, 0, 1) * .5;
        var powers = Math.Min(1, deck.Count(card => card.Type == CardType.Power) / 3.0) * .3;
        var upgrades = Math.Min(1, deck.Count(card => card.IsUpgraded) / 6.0) * .2;
        return Math.Clamp(size + powers + upgrades, 0, 1);
    }

    internal static Route? Plan(MapPoint start, MapPoint boss, Func<MapPoint, double> value, int maxNodes = 512)
    {
        var memo = new Dictionary<MapPoint, (double Score, MapPoint? Next)>();
        var visiting = new HashSet<MapPoint>();
        var expanded = 0;

        (double Score, MapPoint? Next) Best(MapPoint point)
        {
            if (ReferenceEquals(point, boss)) return (value(point), null);
            if (memo.TryGetValue(point, out var cached)) return cached;
            if (!visiting.Add(point)) return (double.NegativeInfinity, null); // cycle guard
            var bestChild = double.NegativeInfinity;
            MapPoint? bestNext = null;
            foreach (var child in point.Children.OrderBy(c => c.coord.ToString(), StringComparer.Ordinal))
            {
                if (expanded++ >= maxNodes) break;
                var (childScore, _) = Best(child);
                if (!double.IsFinite(childScore)) continue;
                var score = Discount * childScore;
                if (score > bestChild) { bestChild = score; bestNext = child; }
            }
            visiting.Remove(point);
            var result = bestNext is null
                ? (double.NegativeInfinity, (MapPoint?)null)
                : (value(point) + bestChild, bestNext);
            memo[point] = result;
            return result;
        }

        var (total, _) = Best(start);
        if (!double.IsFinite(total)) return null;
        var path = new List<MapPoint> { start };
        var cursor = start;
        while (path.Count <= maxNodes && memo.TryGetValue(cursor, out var step) && step.Next is not null)
        {
            cursor = step.Next;
            path.Add(cursor);
        }
        return new Route(path, total);
    }
}

/// <summary>
/// The party relics that change a room's worth, counted once per living holder.
///
/// WHY THIS IS NOT A FLAT LIST: the three batches the route model cares about behave
/// differently in multiplayer. A shared-room relic (Juzu Bracelet, Miniature Tent) only
/// needs ONE holder to change the whole team's room value; a self-only relic (Eternal
/// Feather, Shovel) is worth something per holder; and an elite/shop relic (Black Star,
/// Membership Card) is conditional on the team paying the room's cost. Keeping the holder
/// count lets each room apply the right aggregation instead of flattening all three into
/// "has relic".
/// </summary>
internal sealed class RouteRelicProfile
{
    private readonly Dictionary<string, int> holders = new(StringComparer.Ordinal);
    public int PartySize { get; }
    private RouteRelicProfile(int partySize) => PartySize = Math.Max(1, partySize);
    public int HolderCount(string relicType) => holders.GetValueOrDefault(relicType);
    public bool Any(string relicType) => HolderCount(relicType) > 0;
    public bool All(string relicType) => HolderCount(relicType) >= PartySize;

    public static RouteRelicProfile None { get; } = new(1);

    public static RouteRelicProfile FromPlayers(IEnumerable<Player> players)
    {
        var alive = players.Where(player => player.Creature.IsAlive).ToArray();
        var profile = new RouteRelicProfile(alive.Length);
        foreach (var player in alive)
        {
            foreach (var relic in player.Relics)
            {
                // A used-up relic is not a future room benefit. Maw Bank is the live case:
                // once any gold has been spent, the per-floor income is gone and the relic
                // must stop pulling the route toward shops.
                if (relic.IsUsedUp) continue;
                var type = relic.GetType().Name;
                profile.holders[type] = profile.HolderCount(type) + 1;
            }
        }
        return profile;
    }

    /// <summary>Fixture-only profile: each named type counts as one holder.</summary>
    internal static RouteRelicProfile ForTest(int partySize, params string[] relicTypes)
    {
        var profile = new RouteRelicProfile(partySize);
        foreach (var name in relicTypes)
            profile.holders[name] = profile.HolderCount(name) + 1;
        return profile;
    }
}

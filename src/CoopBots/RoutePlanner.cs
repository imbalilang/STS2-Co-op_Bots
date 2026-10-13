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
    /// Owner policy 2026-10-04: a chosen route may not be more than 60% plain
    /// monster rooms. If every reachable route is above that cap, choose the
    /// route with the fewest monster rooms (route value breaks ties).
    /// </summary>
    internal const double MaxMonsterShare = 0.60;
    private const double MonsterNodePenalty = 1_000_000;

    /// <summary>
    /// Share of the chosen route that is a plain fight. The start node is the
    /// room already visited and the final node is the boss, so only the rooms
    /// actually being chosen between them count.
    /// </summary>
    internal static double MonsterShare(IReadOnlyList<MapPoint> path)
    {
        if (path.Count <= 2) return 0;
        var upcoming = path.Skip(1).Take(path.Count - 2).ToArray();
        return upcoming.Length == 0
            ? 0
            : upcoming.Count(point => point.PointType == MapPointType.Monster) / (double)upcoming.Length;
    }

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

    internal static Route? Plan(RunState state, Player human, bool teamRoute = false,
        List<(MapPoint Child, double Total)>? ranking = null)
    {
        var start = state.CurrentMapPoint ?? state.Map.StartingMapPoint;
        // StandardActMap links BossMapPoint -> SecondBossMapPoint at A10+.
        // Ending at the first boss produces a one-node path after that fight,
        // so both the bot voter and the human route advisor stop advancing.
        var boss = state.Map.SecondBossMapPoint ?? state.Map.BossMapPoint;
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
        // THE HEALTHY BOUNDARY IS 70%, NOT 75%. The DP only carries a tier, and
        // RepresentativeHealth(2) = .9 is what the elite's 70% survival gate is
        // actually evaluated against. With the boundary at .75 a party anywhere in
        // 70-75% was represented as 60% and refused the elite outright — the live
        // 2026-09-30 run read `chose UnknownMapCoord (1,13) (minHp=73 %)`, where the
        // elite had already been priced -45 by the bucket before anything else ran.
        // Moving the boundary to the gate makes "70% is healthy enough" true for the
        // tier ladder and for the elite rule at the same time.
        var eliteDone = state.MapPointHistory.FirstOrDefault()?.Any(point => point.MapPointType == MapPointType.Elite
            || point.HasRoomOfType(MegaCrit.Sts2.Core.Rooms.RoomType.Elite)) == true;
        return Plan(start, boss, Value, HealthTier(health), GoldTier(human.Gold), ranking: ranking,
            requireElite: act == 0 && !eliteDone, enforceMonsterShare: true);
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

    /// <summary>
    /// The health ladder the route DP carries: hurt below <see cref="HurtHealthRatio"/>,
    /// healthy at or above <see cref="HealthyHealthRatio"/>. Named (rather than literals in
    /// <see cref="Plan(RunState, Player, bool, List{ValueTuple{MapPoint, double}}?)"/>) so the
    /// elite rule and the tier ladder cannot disagree about where "healthy" starts — they did,
    /// and it cost every elite in the 70-75% band.
    /// </summary>
    internal const double HurtHealthRatio = .40;

    /// <inheritdoc cref="HurtHealthRatio"/>
    internal const double HealthyHealthRatio = .70;

    internal static int HealthTier(double ratio) => Tier(ratio, HurtHealthRatio, HealthyHealthRatio);

    /// <summary>
    /// The health ladder's per-room cost. Extracted from the DP's local <c>Advance</c> so the rule
    /// is directly testable: it was a one-line `health - 2` buried in a local function, and because
    /// the ladder only has three tiers that silently meant "a party entering an elite at ≥70% is
    /// modelled at &lt;40% for the REST of the act" — the tier-0 band taxes every following room
    /// (monster -25, unknown -12). No healthy route ever took an elite (live 2026-09-30).
    /// </summary>
    internal static int HealthAfterRoom(MapPointType type, int healthTier) => type switch
    {
        MapPointType.Monster => Math.Max(0, healthTier - 1),
        // MORE than a fight, but not a whole extra tier from the top.
        MapPointType.Elite => healthTier >= 2 ? healthTier - 1 : Math.Max(0, healthTier - 2),
        MapPointType.RestSite => Math.Min(2, healthTier + 2),
        _ => healthTier,
    };

    internal static int GoldTier(int gold) =>
        gold < 100 ? 0 : gold < 250 ? 1 : gold < RichGoldThreshold ? 2 : 3;
    internal static double RepresentativeHealth(int tier) => tier switch { 0 => .3, 1 => .6, _ => .9 };
    internal static double RepresentativeGold(int tier) => tier switch { 0 => 50, 1 => 180, 2 => 350, _ => 600 };

    // Shared with the DP and the monster-share re-score. Gold is separate from
    // health: a rich purse must survive the rooms between it and the shop, and a
    // normal room reward does not always create the >=500 rich tier.
    private static (int Health, int Gold) Advance(MapPointType type, int health, int gold) =>
        (HealthAfterRoom(type, health), type switch
        {
            MapPointType.Shop => Math.Max(0, gold - 1),
            MapPointType.RestSite => gold,
            _ => gold >= 3 ? 3 : Math.Min(2, gold + 1),
        });

    private static double ScorePath(IReadOnlyList<MapPoint> path, Func<MapPointType, int, int, double> value,
        int healthTier, int goldTier)
    {
        var total = 0.0;
        var weight = 1.0;
        var hp = healthTier;
        var gold = goldTier;
        foreach (var point in path)
        {
            total += weight * value(point.PointType, hp, gold);
            (hp, gold) = Advance(point.PointType, hp, gold);
            weight *= Discount;
        }
        return total;
    }

    /// <summary>
    /// State-propagating DP: the value of a room depends on the resources the
    /// team would have when reaching it, and visiting a room changes them.
    /// </summary>
    internal static Route? Plan(MapPoint start, MapPoint boss, Func<MapPointType, int, int, double> value,
        int healthTier, int goldTier, int maxNodes = 512,
        List<(MapPoint Child, double Total)>? ranking = null, bool requireElite = false,
        bool enforceMonsterShare = false)
    {
        // Native route voting imposes no elite quota. Owner 2026-10-04 asks
        // that our first-act route must include an elite. Carry the
        // obligation through merges in the DAG; a finite bonus can be outscored.
        // If a save is already beyond every elite, still allow reaching the boss.
        var reachable = new HashSet<(MapPoint, bool)>();
        bool CanFinishWithElite(MapPoint point, bool seen)
        {
            seen |= point.PointType == MapPointType.Elite;
            if (!reachable.Add((point, seen))) return false;
            if (ReferenceEquals(point, boss)) return seen;
            return point.Children.Any(child => CanFinishWithElite(child, seen));
        }
        requireElite &= CanFinishWithElite(start, false);
        var memo = new Dictionary<(MapPoint, int, int, bool), (double Score, MapPoint? Next)>();
        var visiting = new HashSet<(MapPoint, int, int, bool)>();
        var expanded = 0;

        (double Score, MapPoint? Next) Best(MapPoint point, int health, int gold, bool needsElite)
        {
            var key = (point, health, gold, needsElite);
            needsElite &= point.PointType != MapPointType.Elite;
            if (ReferenceEquals(point, boss)) return (needsElite ? UnreachableValue : value(point.PointType, health, gold), null);
            if (memo.TryGetValue(key, out var cached)) return cached;
            if (!visiting.Add(key)) return (double.NegativeInfinity, null); // cycle guard
            var (nextHealth, nextGold) = Advance(point.PointType, health, gold);
            var bestChild = double.NegativeInfinity;
            MapPoint? bestNext = null;
            foreach (var child in point.Children.OrderBy(c => c.coord.ToString(), StringComparer.Ordinal))
            {
                if (expanded++ >= maxNodes) break;
                var (childScore, _) = Best(child, nextHealth, nextGold, needsElite);
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

        var (total, _) = Best(start, healthTier, goldTier, requireElite);
        if (!double.IsFinite(total)) return null;
        // DIAGNOSTIC ONLY, and deliberately AFTER the decision is already made: every child's
        // reached total, in the same units the DP compared them in. Without it a vote is
        // invisible — the live log printed only the winning coordinate, which cannot tell
        // "the elite was the only way forward" from "the elite outscored the alternative".
        // Two 2026-09-26 runs died at an act-1 elite while the gate below says that elite was
        // worth -45, and neither question could be answered from the log.
        if (ranking is not null)
        {
            var (childHealth, childGold) = Advance(start.PointType, healthTier, goldTier);
            foreach (var child in start.Children)
            {
                var (childScore, _) = Best(child, childHealth, childGold, requireElite && start.PointType != MapPointType.Elite);
                ranking.Add((child, double.IsFinite(childScore) ? Discount * childScore : double.NegativeInfinity));
            }
        }
        var path = new List<MapPoint> { start };
        var cursor = start;
        var hp = healthTier;
        var gold = goldTier;
        var needsElite = requireElite;
        while (path.Count <= maxNodes && memo.TryGetValue((cursor, hp, gold, needsElite), out var step) && step.Next is not null)
        {
            needsElite &= cursor.PointType != MapPointType.Elite;
            (hp, gold) = Advance(cursor.PointType, hp, gold);
            cursor = step.Next;
            path.Add(cursor);
        }
        if (enforceMonsterShare && MonsterShare(path) > MaxMonsterShare)
        {
            // A per-node penalty larger than any real route value makes the
            // alternate DP minimize monster count first; normal value breaks
            // ties among routes with the same count. If every reachable route
            // is still over the cap, this is exactly the "fewest monsters"
            // fallback the owner asked for.
            var fewestMonsters = Plan(start, boss,
                (type, health, gold) => value(type, health, gold)
                    - (type == MapPointType.Monster ? MonsterNodePenalty : 0),
                healthTier, goldTier, maxNodes, ranking: null, requireElite: requireElite);
            if (fewestMonsters is not null)
                return new Route(fewestMonsters.Path, ScorePath(fewestMonsters.Path, value, healthTier, goldTier));
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
    //
    // "DECENT" IS A GATE, NOT A SLOPE. The route policy's own decent-deck line is 0.5
    // — the maturity every act-1/2/3 branch of RouteScenarios pins — so an elite below
    // it must LOSE to the normal fight it competes with, not merely be worth less.
    // The slope alone could never say that: the act-1 base of 28 exceeded the whole
        // maturity swing, so an unready act-1 elite stayed POSITIVE (+21 at maturity 0.2,
        // +27 at 0.3) against a monster's +13, and the DP walked into it whenever the party
        // happened to be at full health. The slope's own pivot stays at 0.55; this is a
        // separate gate.
    private const double EliteReadyMaturity = 0.5;

    private static double EliteValue(double health, int act, double maturity, bool teamRoute)
    {
        if (health < .7) return -45;
        // A second absolute gate, for the same reason as the health one: an unready deck
        // loses the elite's damage race, and the relic it would win does not pay for the
        // run it ends.
        //
        // Measured 2026-09-25, 4 BOT / A5 / perf=ultra, three runs, 0 clears: every one
        // died at or immediately after an act-1 elite. BYGONE_EFFIGY (row 8) left the party
        // at 10/18/7/25 and the next normal fight finished it; TERROR_EEL (floor 14) and
        // PHANTASMAL_GARDENERS (floor 8) wiped the party outright. The decks at those
        // points were 12-16 cards with no scaling, i.e. maturity ~0.2-0.4 — below this
        // gate, and above nothing the old slope could stop. See
        // work/live-a5/2026-09-25-baseline/.
        // Still -45 and not unreachable: a map whose only way forward is the elite must
        // stay routable, which is the same contract the shop's -60 keeps.
        if (act <= 1 && maturity < EliteReadyMaturity) return -45;
        // Raised +4 (act 1/2) and +2 (healthy bonus) on 2026-09-30: the node was
        // already the right side of the comparison (45 vs an unknown's 22), but the
        // DP's own health propagation was eating ~50 and no healthy route ever took
        // one. The relic/gold/card an elite pays is what the boss check needs, and a
        // party at 70%+ can afford it — see the Advance() and Tier() notes above.
        var byAct = act <= 0 ? 32 : act == 1 ? 18 : -8;
        var deckAdjust = (Math.Clamp(maturity, 0, 1) - 0.55) * 60;
        var healthyBonus = health >= .85 ? 10 : 0;
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

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

    internal static Route? Plan(RunState state, Player human)
    {
        var start = state.CurrentMapPoint ?? state.Map.StartingMapPoint;
        var boss = state.Map.BossMapPoint;
        if (start is null || boss is null) return null;
        var health = state.Players.Where(p => p.Creature.IsAlive)
            .Select(p => (double)p.Creature.CurrentHp / Math.Max(1, p.Creature.MaxHp))
            .DefaultIfEmpty(1).Min();
        var shoppers = state.Players.Count(p => p.Creature.IsAlive && p.Gold >= 100);
        var act = state.CurrentActIndex;
        var deckSize = human.Deck.Cards.Count;
        // Walk the act with resource state: a second rest is worth less than the
        // first, a shop after spending is worth less than one while rich.
        var maturity = DeckMaturity(human);
        // A room the game cannot build is not merely worth less — it is unreachable, and
        // the DP already reads non-finite as unreachable (it skips such children and
        // returns null rather than a route that ends in one). So the executability rule
        // rides on machinery that already exists instead of a second filter in the search.
        Func<MapPointType, int, int, double> Value = (type, hpTier, goldTier) =>
            !IsBuildable(type, act) ? UnreachableValue
            : NodeValue(type, RepresentativeHealth(hpTier), (int)RepresentativeGold(goldTier), act, deckSize, maturity)
            + (type == MapPointType.Shop ? Math.Min(15, shoppers * 5) : 0);
        return Plan(start, boss, Value, Tier(health, .4, .75), GoldTier(human.Gold));
    }

    internal static int Tier(double ratio, double low, double high) => ratio < low ? 0 : ratio < high ? 1 : 2;
    internal static int GoldTier(int gold) => gold < 100 ? 0 : gold < 250 ? 1 : 2;
    internal static double RepresentativeHealth(int tier) => tier switch { 0 => .3, 1 => .6, _ => .9 };
    internal static double RepresentativeGold(int tier) => tier switch { 0 => 50, 1 => 180, _ => 350 };

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
            MapPointType.Monster => (Math.Max(0, health - 1), Math.Min(2, gold + 1)),
            MapPointType.Elite => (Math.Max(0, health - 2), Math.Min(2, gold + 1)),
            MapPointType.RestSite => (Math.Min(2, health + 2), gold),
            MapPointType.Shop => (health, Math.Max(0, gold - 1)),
            MapPointType.Treasure => (health, Math.Min(2, gold + 1)),
            MapPointType.Unknown => (health, Math.Min(2, gold + 1)),
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
        double maturity = .5)
    {
        var unknown = (act <= 0 ? 22 : act == 1 ? 16 : 13) - (health < .4 ? 12 : 0);
        var monster = (act <= 0 ? 9 : act == 1 ? 7 : 5) + (deckSize < 15 ? 4 : 0) - (health < .4 ? 25 : 0);
        return type switch
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
            MapPointType.Shop => gold >= ShopRemovalCost ? 35 : -60,
            MapPointType.Elite => EliteValue(health, act, maturity),
            MapPointType.Treasure => 45,
            MapPointType.Monster => monster,
            MapPointType.Unknown => unknown,
            MapPointType.Ancient => 40,
            _ => 0,
        };
    }

    // Elites scale much harder than the act's normal fights, so their value must
    // fall with the act and with how ready the deck is: a healthy team with an
    // unfinished deck should still skip an Act 2/3 elite.
    private static double EliteValue(double health, int act, double maturity)
    {
        if (health < .7) return -45;
        var byAct = act <= 0 ? 22 : act == 1 ? 2 : -16;
        var deckAdjust = (Math.Clamp(maturity, 0, 1) - 0.55) * 60;
        return byAct + deckAdjust;
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

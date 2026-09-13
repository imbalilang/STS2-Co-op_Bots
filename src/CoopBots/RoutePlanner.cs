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
        Func<MapPointType, int, int, double> Value = (type, hpTier, goldTier) =>
            NodeValue(type, RepresentativeHealth(hpTier), (int)RepresentativeGold(goldTier), act, deckSize, maturity)
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
    internal static double NodeValue(MapPointType type, double health, int gold, int act, int deckSize,
        double maturity = .5)
    {
        var unknown = (act <= 0 ? 22 : act == 1 ? 16 : 13) - (health < .4 ? 12 : 0);
        var monster = (act <= 0 ? 9 : act == 1 ? 7 : 5) + (deckSize < 15 ? 4 : 0) - (health < .4 ? 25 : 0);
        return type switch
        {
            MapPointType.RestSite => health < .65 ? 60 : 18,
            MapPointType.Shop => gold >= 180 ? 35 : -10,
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

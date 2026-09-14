using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

/// <summary>
/// How much run is left, and what is still reachable — the one place that reads
/// the map forward. Act index alone cannot tell a middle-of-act-3 shop from the
/// last one before the boss, and that difference decides whether gold is still
/// worth anything: the reviewed party met the act-3 boss holding 646 gold and
/// six potions because every one of those decisions was made as if there were
/// another shop behind it.
///
/// Everything here degrades to "no opinion" when the map cannot be read (a test
/// run with no map, a room that is not a map node): the act still counts, but no
/// special case fires, so nothing changes where the depth is unknown.
/// </summary>
internal static class RunDepth
{
    // The map is a small DAG; the cap only exists so a malformed or modded map
    // cannot make this walk unbounded.
    private const int MaxNodes = 256;

    internal static int Act(Player player)
    {
        try { return player.RunState.CurrentActIndex; }
        catch { return 0; }
    }

    /// <summary>True while another shop is reachable before the boss.</summary>
    internal static bool ShopAhead(Player player) => Ahead(player).Shops > 0;

    internal static bool CampAhead(Player player) => Ahead(player).Camps > 0;

    /// <summary>
    /// The party is standing in a shop that is the last one this run can reach:
    /// any gold still in the purse is spent on nothing, so the buying bar drops
    /// to "does not make the deck worse".
    /// </summary>
    internal static bool LastShopBeforeBoss(Player player)
        => At(player, MapPointType.Shop) && !ShopAhead(player);

    /// <summary>The last chance to heal before the boss, for the same reason.</summary>
    internal static bool LastCampBeforeBoss(Player player)
        => At(player, MapPointType.RestSite) && !CampAhead(player);

    /// <summary>
    /// How much worse a card the deck did not ask for is, later in the run. A
    /// filler card taken in act 1 can still be removed at any of the shops
    /// ahead; the same card taken once no shop remains is permanent. Plans are
    /// not priced with this — only the cards nothing asked for.
    /// </summary>
    internal static double BloatFactor(Player player) => BloatFactor(Act(player), ShopAhead(player));

    // Act 3 with a shop still ahead pays 1.5x; with no shop left it pays just
    // over 2x, because that dilution can never be undone.
    internal static double BloatFactor(int act, bool shopAhead)
        => (1 + 0.25 * Math.Max(0, act)) * (shopAhead ? 1.0 : 1.35);

    /// <summary>
    /// The share of its price a purchase must be worth before it is bought. The
    /// margin only shrinks — it is never inverted — so a purchase that would
    /// make the deck worse is still refused at the very last shop.
    /// </summary>
    internal static double ShopThresholdFactor(int act, bool lastShopBeforeBoss)
        => lastShopBeforeBoss ? 0.35 : act >= BotShopPlanner.FinalAct ? 0.8 : 1.0;

    internal static double ShopThresholdFactor(Player player)
        => ShopThresholdFactor(Act(player), LastShopBeforeBoss(player));

    private static bool At(Player player, MapPointType type)
    {
        try { return player.RunState.CurrentMapPoint?.PointType == type; }
        catch { return false; }
    }

    // Reachable rooms between here and the boss, counted once each. Reference
    // identity is what the map itself uses for its nodes.
    private static (int Shops, int Camps) Ahead(Player player)
    {
        try
        {
            var state = player.RunState;
            var from = state.CurrentMapPoint;
            var boss = state.Map.BossMapPoint;
            if (from is null || boss is null) return (1, 1);
            var seen = new HashSet<MapPoint>();
            var queue = new Queue<MapPoint>();
            foreach (var child in from.Children) queue.Enqueue(child);
            int shops = 0, camps = 0, visited = 0;
            while (queue.Count > 0 && visited++ < MaxNodes)
            {
                var point = queue.Dequeue();
                if (!seen.Add(point)) continue;
                // The boss itself is the end of the walk, not a room to value.
                if (ReferenceEquals(point, boss)) continue;
                if (point.PointType == MapPointType.Shop) shops++;
                if (point.PointType == MapPointType.RestSite) camps++;
                foreach (var child in point.Children) queue.Enqueue(child);
            }
            return (shops, camps);
        }
        catch
        {
            // "I cannot see the map" must not read as "there is nothing ahead":
            // that would turn every unknown room into a final one, and every
            // unknown act into the last.
            return (1, 1);
        }
    }
}

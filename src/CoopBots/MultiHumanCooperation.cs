using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots;

internal static class MultiHumanCooperation
{
    // End-turn is explicit; an empty hand or zero energy does not imply the human is finished.
    internal static bool HumansReady(IReadOnlyList<Player> party, Func<Player, bool> ready)
        => party.Where(p => !BotRegistry.IsBot(p.NetId) && p.Creature.IsAlive)
            .All(ready);

    // Allocate the small number of bot votes as closely as possible to the humans'
    // actual distribution. Never turn two disagreeing humans into a 3:1 host vote.
    internal static T? Vote<T>(IReadOnlyList<T?> humanVotes, int botCount, int botIndex) where T : struct
    {
        if (humanVotes.Count == 0 || humanVotes.Any(v => !v.HasValue) || botIndex < 0 || botIndex >= botCount) return null;
        var groups = humanVotes.Select(v => v!.Value).GroupBy(v => v)
            .Select(g => (Value: g.Key, Count: g.Count())).ToList();
        var assigned = new int[groups.Count];
        for (var i = 0; i <= botIndex; i++)
        {
            var chosen = Enumerable.Range(0, groups.Count)
                .OrderByDescending(g => (double)botCount * groups[g].Count / humanVotes.Count - assigned[g])
                .ThenBy(g => g).First();
            if (i == botIndex) return groups[chosen].Value;
            assigned[chosen]++;
        }
        return null;
    }

    internal static Player? LocalHuman(IReadOnlyList<Player> party, ulong localId)
        => party.FirstOrDefault(p => p.NetId == localId && !BotRegistry.IsBot(p.NetId));
}

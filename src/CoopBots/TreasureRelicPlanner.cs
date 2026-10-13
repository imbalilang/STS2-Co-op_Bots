using System.Diagnostics;
using System.Runtime.CompilerServices;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots;

// One asynchronous score table per unchanged chest/team. Votes only reserve
// columns; they do not change a relic's value, so later seats reuse the table.
internal sealed class TreasureRelicPlanner
{
    private readonly ConstructionPoll<Dictionary<(ulong Player, int Relic), double>> poll = new();
    internal void Clear() => poll.Clear();

    internal bool TryAssign(IReadOnlyList<Player> party, IReadOnlyList<RelicModel> relics,
        IReadOnlyList<Player> waiting, IReadOnlySet<int> reserved, out Dictionary<ulong, int?> assignment)
    {
        assignment = new();
        if (waiting.Count == 0) { Clear(); return true; }
        var players = party.OrderBy(p => p.NetId).ToArray();
        var offered = relics.ToArray();
        string Context() => $"{RuntimeHelpers.GetHashCode(players[0].RunState.CurrentRoom ?? (object)players[0].RunState)}:"
            + string.Join(',', players.Select(p => p.NetId)) + ":"
            + string.Join(';', offered.Select(r => $"{RuntimeHelpers.GetHashCode(r)}:{r.Id}:"
                + string.Join(',', r.DynamicVars.Select(v => $"{v.Key}={v.Value.BaseValue}"))));
        var key = ConstructionDecision.Fingerprint(players[0]) + Context();
        try
        {
            if (!poll.TryGet(key, token => ComputeAsync(players, offered, Context, token), out var scores)) return false;
            assignment = TeamCoordinator.AssignRelicScores(waiting, offered.Length, reserved, (p, i) => scores[(p.NetId, i)]);
            return true;
        }
        catch { Clear(); throw; }
    }

    private static async Task<Dictionary<(ulong Player, int Relic), double>> ComputeAsync(
        Player[] players, RelicModel[] relics, Func<string> context, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        Log.Info($"CoopBots treasure planning: started seats={players.Length} relics={relics.Length}");
        var scores = await ConstructionDecision.RunAsync(players[0], () => TeamCoordinator.EvaluateRelics(players, relics), context, token);
        Log.Info($"CoopBots treasure planning: ready pairs={scores.Count} elapsedMs={timer.Elapsed.TotalMilliseconds:F1}");
        return scores;
    }
}

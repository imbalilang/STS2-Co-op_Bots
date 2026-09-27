using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

/// <summary>Replay the selected plan without its opening bottle. This tests the
/// bottle's contribution while holding the card sequence fixed, instead of
/// mistaking a different search's better card ordering for potion value.</summary>
public sealed class KernelPotionPlanAudit
{
    private readonly TournamentResult original;
    private readonly IReadOnlyList<Player> party;
    private readonly KernelSession replay;
    private readonly List<KernelTeamSearch.Action> actions = new();
    private readonly List<ulong[]> alive = new();
    private readonly List<Creature[]> enemies = new();
    private int index = 1;
    public TournamentResult? Result { get; private set; }
    public bool Removed { get; private set; }

    public KernelPotionPlanAudit(KernelSession root, IReadOnlyList<Player> party, TournamentResult result)
    {
        original = result; this.party = party; replay = root.Fork();
        replay.AutoResolveTurnStartChoices = true; replay.AutoResolveEnemyChoices = true;
        if (result.FirstAction?.Potion is null || result.Record is not { Verified: true }
            || result.Script is not { Count: > 1 }) Result = original;
    }

    public bool Advance(TimeSpan slice)
    {
        if (Result is not null) return true;
        var watch = Stopwatch.StartNew();
        do
        {
            if (replay.HasTerminal)
            {
                var record = TerminalRecord.Capture(replay, party, true) with
                    { ResourcesConsumed = actions.Count(a => a.Potion is not null) };
                if (actions.Count > 0 && TerminalComparer.Compare(record, original.Record!, KernelPotionPolicy.Objective) >= 0)
                {
                    Removed = true;
                    Result = original with { FirstAction = actions[0], Record = record,
                        Script = actions.ToArray(), ScriptAliveStates = alive.ToArray(), ScriptEnemyStates = enemies.ToArray(),
                        StopReason = original.StopReason + ":redundant-potion-removed" };
                }
                else Result = original;
                return true;
            }
            // A truncated or no-longer-legal counterfactual cannot prove redundancy.
            if (index >= original.Script!.Count || index > 600) { Result = original; return true; }
            var action = original.Script[index++];
            alive.Add(party.Where(p => replay.Hp(p.Creature) > 0).Select(p => p.NetId).OrderBy(id => id).ToArray());
            enemies.Add(replay.Enemies.ToArray());
            if (!replay.ReplayPlanned(action, 80, out _)) { Result = original; return true; }
            actions.Add(action);
        } while (watch.Elapsed < slice);
        return false;
    }
}

using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    // Human readiness changes native submission permissions, not search
    // strength. Keep these method names for diagnostic reflection callers.
    private bool tournamentInteractive;
    private TournamentResult? mixedReadyProposal;

    private static bool InteractiveMixedTurn(CombatState combat, bool humansFinished)
        => !humansFinished && MixedTable(combat);

    private static int DecisionPassBudget(CombatState combat, bool humansFinished)
        => TournamentBudget(combat);

    private static int DecisionConcurrency(CombatState combat, bool humansFinished)
        => TournamentParallelism;

    private TournamentOptions InteractiveOptions(TournamentOptions options)
        => options;
}

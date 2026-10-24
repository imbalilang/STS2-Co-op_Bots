using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    // Upstream has no mixed human/Bot interaction scheduling contract.
    // We compare native one-round continuations under a short request while
    // a human is acting, and can submit a suffix replayed on the new root.
    // Owner request 2026-10-24: Bot progress must not depend on human EndTurn.
    // Cutoffs remain estimates, never victories; the actual-state contract stays.
    internal const int MixedPassBudgetMs = 200;
    private bool tournamentInteractive;
    private TournamentResult? mixedReadyProposal;

    private static bool InteractiveMixedTurn(CombatState combat, bool humansFinished)
        => !humansFinished && MixedTable(combat);

    private static int DecisionPassBudget(CombatState combat, bool humansFinished)
        => InteractiveMixedTurn(combat, humansFinished) ? MixedPassBudgetMs : TournamentBudget(combat);

    private static int DecisionConcurrency(CombatState combat, bool humansFinished)
        => InteractiveMixedTurn(combat, humansFinished) ? 1 : TournamentParallelism;

    private TournamentOptions InteractiveOptions(TournamentOptions options)
        => !tournamentInteractive ? options : options with {
            TopK = Math.Min(6, options.TopK), ReserveMs = 0,
            MaxDegreeOfParallelism = 1, UsePolicyPortfolio = false, EnableJointSearch = false,
            Rollout = (options.Rollout ?? new()) with { MaxRounds = 1, MaxSteps = 32 },
        };
}

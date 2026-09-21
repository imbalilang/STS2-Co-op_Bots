using System;
using System.Collections.Generic;
using System.Threading;
using CoopBots.Kernel.Vendor;
using MegaCrit.Sts2.Core.Combat;

namespace CoopBots.Kernel;

/// <summary>
/// CombatSolver's own planner, reached through its public entry point rather than by
/// re-implementing any of it.
///
/// The vendored engine has always shipped the real search — the beam-width portfolio,
/// the ~55 tuned <c>SolverWeights</c>, the strategic-effect five-vector, the retention
/// policies — but nothing in this mod called it. The live path used
/// <c>KernelTeamSearch</c>, whose expansion is genuinely multi-actor (it walks every
/// seat's hand per node) but whose leaf evaluation is our own reduced
/// <c>KernelCombatEvaluation</c>. That mismatch is the "limited card sense" this exists
/// to remove: the team search knew what every seat held and then priced it with a
/// simplified evaluator.
///
/// The whole surface is four one-line factories:
/// <code>
/// CombatSearchCoordinator.Solve(
///     CombatRootSnapshot.Capture(combat),
///     SolverDisplayNames.Capture(combat),
///     BattleDamageTracker.Observe(combat),
///     NeutralSearchPolicy.Create(), ct, null)
/// </code>
///
/// SCOPE, and the reason this is a separate type rather than a rewrite of the planner:
/// the engine's expansion is single-player — <c>root.PlayerCount != 1</c> throws
/// ("first version supports single-player only"), and the expansion reads
/// <c>GetPlayerCombatState(_player).Hand</c>. So this route plans ONE seat at a time.
/// The multi-player half already exists on our side, so the join belongs here rather
/// than in a fork of a third-party project — see VENDOR_DEVIATIONS.md.
///
/// It is deliberately not yet wired into the planner: the conversion from
/// <c>SolverResult</c> to a plan this mod can deploy, and the choice of how four
/// single-seat routes combine, are decisions that must be made against a real board
/// rather than guessed at compile time.
/// </summary>
internal static class CombatSolverRoute
{
    /// <summary>
    /// Runs the engine's search for one seat and returns its plan, or null when the
    /// engine declines the board (multi-player root, wrong phase, unmodelled state).
    ///
    /// The caller holds ONE <see cref="NeutralSearchPolicy.Create"/> snapshot and one
    /// cancellation token for the fight; nothing here caches, because the engine's own
    /// run context is built per solve inside <c>CombatSearchCoordinator</c>.
    /// </summary>
    internal static SolverResult? Solve(CombatState combat, CancellationToken cancellationToken = default)
    {
        try
        {
            return CombatSearchCoordinator.Solve(
                CombatRootSnapshot.Capture(combat),
                SolverDisplayNames.Capture(combat),
                BattleDamageTracker.Observe(combat),
                NeutralSearchPolicy.Create(),
                cancellationToken,
                progressCallback: null);
        }
        catch (NotSupportedException)
        {
            // The engine's own refusals: PlayerCount != 1, or more than 64 enemies. Not an
            // error — it is this route declining a board the caller must handle another
            // way, and swallowing it here keeps that decision with the caller.
            return null;
        }
        catch (InvalidOperationException)
        {
            // Wrong phase, or a combat root the engine will not evaluate. Same reasoning.
            return null;
        }
    }

    /// <summary>
    /// The plan's actions in order, or an empty list when the engine returned nothing
    /// usable. Kept here so callers do not reach into <c>SolverResult</c> internals.
    /// </summary>
    internal static IReadOnlyList<PlanAction> Actions(SolverResult? result)
        => result?.BestNode.Actions ?? (IReadOnlyList<PlanAction>)Array.Empty<PlanAction>();
}

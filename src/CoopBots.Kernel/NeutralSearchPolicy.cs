using CoopBots.Kernel.Vendor;

namespace CoopBots.Kernel;

/// <summary>
/// Builds a <see cref="SearchPolicySnapshot"/> from outside the solver, so the vendored
/// engine's state evaluation can be reached without constructing a
/// <c>CombatBeamSolver</c> — see VENDOR_DEVIATIONS.md, deviation D1.
///
/// The record has 19 positional members and no default or factory, and five of the six
/// solver fields the evaluation reads are derived from it. Four of its members are
/// session-scoped classes the evaluation never touches — verified, not assumed:
/// <c>Diagnostics</c>, <c>FramePressureSignal</c>, <c>MemoryPressureSignal</c> and
/// <c>PotionStrategy</c> have **zero** references in
/// <c>Vendor/Search/CombatBeamSolver.StateEvaluation.cs</c>, the file holding the
/// evaluation and every helper it calls. (The one <c>Diagnostics</c> hit there is
/// <c>using System.Diagnostics;</c>.) They are passed as placeholders on purpose.
///
/// CAVEAT, and the reason this factory must stay next to that verification: the
/// placeholders are safe for the *evaluation* path only. <c>CombatBeamSolver</c>'s
/// constructor reads <c>policy.FramePressureSignal</c> and
/// <c>policy.MeasurePhasePerformance</c> while building its run context, so a snapshot
/// from here may only be consumed by code that does not build a solver from it. If a
/// future change routes it through the constructor, these placeholders become a
/// null-dereference on a path that does not run in tests.
/// </summary>
internal static class NeutralSearchPolicy
{
    /// <summary>
    /// The numbers the evaluation does read, supplied as neutral-but-real values.
    /// Profiles come from the engine's own presets rather than being invented, so the
    /// budgets stay consistent with what the engine expects.
    /// </summary>
    internal static SearchPolicySnapshot Create(int acceptableBattleHpLoss = 0) => new(
        // 0.41 collapsed the short/deep profile pair into a single Profile and folded
        // ForceShortOnly into FixedBudget; the engine's own Default is the preset that
        // matches what Short used to stand for.
        Profile: SolverSearchProfile.Default,
        PotionPolicy: SolverPotionPolicy.Disabled,
        PotionStrategy: null!,
        DetailedDiagnostics: false,
        VerifyIncrementalSearch: false,
        FixedBudget: false,
        MeasurePhasePerformance: false,
        MaxDegreeOfParallelism: 1,
        BudgetOverrideMilliseconds: null,
        IncludeTurnSetup: false,
        TheftPolicy: null,
        // Neutral on both: the evaluation reads these through ActEndingBossPolicy
        // resolution, and "minimise HP loss" is the choice that does not assume the
        // caller is chasing progression.
        ActTransitionBossHpStrategy: BossHpStrategy.MinimizeHpLoss,
        FinalBossHpStrategy: BossHpStrategy.MinimizeHpLoss,
        AcceptableBattleHpLoss: acceptableBattleHpLoss,
        Diagnostics: null!,
        FramePressureSignal: null!,
        MemoryPressureSignal: null!);
}

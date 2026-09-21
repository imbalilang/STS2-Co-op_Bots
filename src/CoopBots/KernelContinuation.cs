using System.Collections.Generic;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;

namespace CoopBots;

/// <summary>
/// A finished search adopted as this fight's script, plus the cursor that walks it.
///
/// <para>
/// This is an object of its own for the same reason CombatSolver keeps its plan on
/// <c>_combat.ContinuationSource</c> instead of among the controller's working fields:
/// a script has a different lifetime from the state that feeds it. A deployment session
/// is created, cancelled and completed all the time — <c>CancelDeployment</c> and
/// <c>CompleteDeployment</c> touch <c>_deployment</c> and nothing else — while the
/// continuation survives every one of them. Upstream assigns
/// <c>ContinuationSource = null</c> in exactly 20 places, each one a named replan
/// decision (<c>potion_missing</c>, <c>card_unplayable</c>, <c>turn_plan_exhausted</c>,
/// <c>turn_drift</c>, …), and never as part of a general teardown.
/// </para>
///
/// <para>
/// The same seven values used to be loose fields next to the search state here, and
/// <see cref="KernelCombatPlanner.Reset"/> cleared them along with everything else. With
/// 24 Reset() call sites, every "I am not acting this tick" path was silently also a
/// "delete the script" path. Three of them were caught firing in ordinary play — the
/// PlayPhase check and the no-eligible-seat check in <c>BotRuntime.Tick</c>, and the
/// idle shortcut — each killing the script at a turn boundary without a log line, which
/// is why live fights read <c>planRefusals=0</c> with <c>noScript=14..26</c> and never
/// resumed past turn one.
/// </para>
///
/// <para>
/// Storage is the point, not the shape: keeping the script here means there is no field
/// for a generic reset to clear. Only <c>CommitContinuation</c>, <c>Advance</c> and
/// <c>DropContinuation</c> touch it.
/// </para>
/// </summary>
internal sealed class KernelContinuation
{
    /// <summary>The search result being replayed.</summary>
    public required KernelTeamSearch.Result Result { get; init; }

    /// <summary>
    /// Index of the next action of <see cref="Result"/> to emit. Starts at 1 because the
    /// path that adopts a plan has already submitted action 0.
    /// </summary>
    public int Index { get; set; }

    /// <summary>The live combat this script belongs to. Deployment refuses a script from
    /// another combat by reference equality.</summary>
    public required CombatState Combat { get; init; }

    /// <summary>The combat round the script was captured in.</summary>
    public int Round { get; init; }

    /// <summary>
    /// The script's own prediction for every turn boundary it crosses: for each EndTurn
    /// on the path, the action index the next turn starts at and the branch's
    /// visible-state text at that moment. Deployment compares the live board against
    /// each one before it will carry the plan past that boundary — the actions after it
    /// were computed against a SIMULATED enemy turn, and the real one is played by the
    /// game. Upstream calls the same check <c>validation=exact_state_text</c>.
    ///
    /// <para>Every boundary, not just the first: validating one and then trusting the
    /// rest would carry the whole tail of a long plan on a single check. Empty means the
    /// script never crosses a turn.</para>
    /// </summary>
    public IReadOnlyList<(int Index, string Text)> Boundaries { get; init; } = [];

    /// <summary>
    /// L1 sentinel: the branch's visible-state text after EACH planned action, indexed to
    /// match the action list. Deploying step i is what produced entry i, so before step
    /// i+1 is submitted the live board can be compared against the state the search said
    /// step i would leave behind. Without it, an action that silently resolved
    /// differently than predicted is not noticed until the next turn boundary — by which
    /// point every later action was chosen for a board that never existed. Empty means
    /// no sentinel was recorded and the script must be treated as unverified.
    /// </summary>
    public IReadOnlyList<(string Before, string After)> ActionStates { get; init; } = [];

    /// <summary>
    /// The last action index that was actually EXECUTED, or -1 before any has been.
    /// </summary>
    /// <remarks>
    /// The L1 sentinel used to compare against <c>Index - 1</c>, which is the same thing only
    /// while every step is executed. Steps CAN be skipped now (see the seat skip in
    /// <c>TryEmitFromPlan</c>): a seat that died mid-script leaves steps that nobody can play,
    /// and dropping the whole script for them threw away a good line for the surviving seats.
    /// Once skipping exists, "the previous index" and "the previous executed index" are different
    /// numbers, and comparing the live board against a step that never ran produces a phantom
    /// drift plus a 120-tick stall on the `has not been applied yet` path.
    /// </remarks>
    public int LastExecuted { get; set; } = -1;

    /// <summary>
    /// Set once a step has been skipped: every later <see cref="ActionStates"/> entry was
    /// predicted for a board where the skipped step HAD happened, so comparing them is
    /// meaningless — not evidence of drift. Logged once, then the sentinel stands down instead of
    /// emitting one drift line per remaining step (measured: 72 drift lines for 7 fields).
    /// </summary>
    public bool SentinelInvalidated { get; set; }

    /// <summary>
    /// How many rounds of delay from the capture have had their boundary prediction
    /// VALIDATED against the live board.
    /// </summary>
    /// <remarks>
    /// The round guard used to read "the round has changed and there is no boundary entry at
    /// THIS index", which can only ever pass on the crossing index itself: the round stays
    /// changed for the rest of the turn, and the plan carries one boundary entry per
    /// crossing, not one per step. So the first step after the crossing was validated and
    /// every step after it was refused — measured live as
    /// `plan step refused: index=9/116 ... round=2/plan_round=1 boundary=none`.
    ///
    /// What the guard is actually for: refuse a step in a later round that the plan never
    /// predicted a crossing for. Recording how many crossings have been validated makes
    /// that check possible without demanding a boundary at every index.
    /// </remarks>
    public int ValidatedRoundDelta { get; set; }

    /// <summary>
    /// What the script itself expects to lose: deaths over its horizon, from the same
    /// projection the end-turn risk check compares against (see LiveEndTurnRisk).
    /// </summary>
    public int ProjectedDeaths { get; init; }
}

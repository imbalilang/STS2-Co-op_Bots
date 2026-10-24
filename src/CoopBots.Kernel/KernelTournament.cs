using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel;

public sealed record TournamentOptions(
    // How many first actions to try. Each one costs a fork of the root plus a full rollout, so
    // this is the knob that converts directly into latency: measured on the dev machine a
    // complete rollout is ~7 MB and ~50-130 ms, and the plan's budget is 1500 ms ideal /
    // 5000 ms ceiling.
    int TopK = 6,
    RolloutPolicyKind Policy = RolloutPolicyKind.Kill,
    RolloutOptions? Rollout = null,
    // Never start a rollout with less than this much of the budget left. A rollout cannot be
    // preempted, so starting one on a nearly-spent budget is how a deadline gets missed.
    int ReserveMs = 300,
    /// <summary>
    /// Whether potions are candidates at all.
    ///
    /// The caller may first establish a no-potion baseline, then compare a
    /// potion-only pass under an explicit consumable price. Keeping the two
    /// passes separate preserves the baseline when bottles cannot improve it.
    /// </summary>
    bool IncludePotions = true,
    // False evaluates only Rollout.Policy (or Policy if Rollout is absent), for A/B probes.
    bool UsePolicyPortfolio = true,
    /// <summary>
    /// How many rollouts may be in flight at once. 1 is the historical sequential loop.
    ///
    /// The rollouts are independent by construction — each gets its own fork — so this is a
    /// pure throughput knob. See <see cref="TournamentRun"/> for what may NOT be parallelised.
    /// </summary>
    int MaxDegreeOfParallelism = 1,
    /// <summary>
    /// Whether “end this seat’s turn” is a candidate.
    ///
    /// True is the all-bot/solo default. A mixed table must pass false while a human is still
    /// acting: BotRuntime only submits a ConfirmedEndTurn once every human has finished, so an
    /// end-turn answer produced earlier is a decision the executor must refuse — and the same
    /// decision point would then be recomputed on every following tick.
    /// </summary>
    bool IncludeEndTurns = true,
    ObjectiveConfig? Objective = null,
    bool PotionsOnly = false,
    // Opening actors can be a subset because some bots have already ended this
    // turn. Those bots still play future turns; only actual humans stay passive.
    IReadOnlyList<Player>? ContinuationActors = null,
    // Production searches multi-action team prefixes before cheap rollout probes.
    // Explicit switch keeps the old algorithm available for controlled comparisons.
    bool EnableJointSearch = false)
{
    public bool DiverseJointContinuations { get; init; } = true;
    public TournamentResult? ReferenceResult { get; init; }
    public KernelDecisionRequest? Request { get; init; }
    // Human intent is an opening preference. Native ending quality remains
    // authoritative: a preferred target cannot displace a safer or winning line.
    public uint? ManualFocusTargetCombatId { get; init; }

    internal bool FollowsManualFocus(KernelTeamSearch.Action? action)
        => ManualFocusTargetCombatId is { } target && action?.Card?.Type == CardType.Attack
            && action.Target?.CombatId == target;

    internal int Compare(TerminalRecord candidate, KernelTeamSearch.Action? action,
        TerminalRecord current, KernelTeamSearch.Action? currentAction)
    {
        var quality = TerminalComparer.Compare(candidate, current, Objective);
        if (quality != 0) return quality;
        // Equal team totals can hide trading a living human for another seat.
        // An expressed target never licenses that survival trade.
        if (!candidate.PostCombatHp.Select(hp => hp > 0)
            .SequenceEqual(current.PostCombatHp.Select(hp => hp > 0))) return 0;
        return FollowsManualFocus(action).CompareTo(FollowsManualFocus(currentAction));
    }
}

/// <summary>
/// What the tournament decided. <see cref="FirstAction"/> is the deployable answer and is
/// non-null whenever any candidate was legal — this is the plan's "始终保留可执行方案"
/// (§06) made structural rather than promised.
/// </summary>
public sealed record TournamentResult(
    KernelTeamSearch.Action? FirstAction,
    TerminalRecord? Record,
    IReadOnlyList<TerminalRecord> Considered,
    int Rollouts,
    int Cutoffs,
    string StopReason)
{
    /// <summary>
    /// The full action sequence of the winning roll-out, first action included. The live
    /// planner caches this so later decisions can follow the same line while a fresh
    /// tournament cannot beat its ending (see KernelCombatPlanner's tournament script).
    /// Null when no candidate produced a script (unforkable root / no legal action).
    /// </summary>
    public IReadOnlyList<KernelTeamSearch.Action>? Script { get; init; }

    /// <summary>
    /// Index-aligned with <see cref="Script"/>: the living NetIds immediately before each
    /// action in the winning roll-out. A script is allowed to contain its own deaths; this
    /// tells the replayer whether a live death happened at the moment the script expected.
    /// </summary>
    public IReadOnlyList<ulong[]>? ScriptAliveStates { get; init; }

    /// <summary>
    /// Index-aligned with <see cref="Script"/>: the living enemies immediately before each
    /// action in the winning roll-out. The party side alone cannot catch a line whose planned
    /// kill did not happen — every later step of such a line aims at the wrong enemy.
    /// </summary>
    public IReadOnlyList<Creature[]>? ScriptEnemyStates { get; init; }
    internal IReadOnlyList<KernelActionCheckpoint>? ScriptCheckpoints { get; init; }
    public int JointNodes { get; init; }
    public int JointPrefixes { get; init; }
    public int JointMaxActions { get; init; }
    public bool JointSelected { get; init; }
    public string JointDiagnostics { get; init; } = "";
    public long? RootGeneration { get; init; }
    internal KernelDecisionRequest? RequestProvenance { get; init; }
    // Generation is diagnostic. Request identity prevents an old record from
    // becoming new-root evidence just by changing that diagnostic number.
    // This does not replace native legality, replay or terminal verification.
    public bool IsFor(KernelDecisionRequest request) => ReferenceEquals(RequestProvenance, request)
        && RootGeneration == request.RootGeneration;
}

/// <summary>
/// Rolls the fight out from each of the best few first actions and picks the one whose ending
/// is best under the shared objective. This is the plan's P1-02 + P2-01 in their smallest
/// useful form (§06 "以分级估值筛选，并对领先候选续演到终局").
///
/// Why this rather than a deeper search: a bounded search cannot reach a terminal by
/// construction, so it ranks lines by an estimate of an ending it never saw. A rollout reaches
/// a REAL ending, and measured on this machine one costs ~27 ms against ~0.40 ms for a single
/// expanded search node — so for the price of a few hundred search nodes you get an outcome
/// instead of a guess.
///
/// Enabled by the live planner only when the experimental tournament switch is selected.
/// </summary>
public static class KernelTournament
{
    /// <summary>
    /// Run the whole tournament in one call. The live planner uses <see cref="TournamentRun"/>
    /// instead so the main thread can return to the frame between actions; this façade is what
    /// the tests and the shadow probe use.
    /// </summary>
    public static TournamentResult Run(KernelSession root, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, TournamentOptions? options, int budgetMs)
    {
        var run = new TournamentRun(root, party, actors, options, budgetMs);
        // A LOOP, not one call: the parallel path launches one wave per Advance and collects it
        // on the next, so a single enormous slice would return with Result still null.
        //
        // IT MUST YIELD BETWEEN WAVES. Spinning here would burn a core on the caller's thread
        // while the workers run at BelowNormal priority — the caller would starve the very
        // threads it is waiting for, and on a small machine it would also fight the game. The
        // serial path never reaches the sleep: its Advance runs to completion in one call.
        while (!run.Advance(TimeSpan.MaxValue)) Thread.Sleep(1);
        return run.Result!;
    }

    /// <summary>
    /// The first actions worth rolling out, best-looking first. Deliberately the SAME cheap
    /// scoring the rollout policy uses: the tournament's job is to compare ENDINGS, so spending
    /// the evaluator on ranking the first move would pay twice for the same guess.
    /// </summary>
    internal static List<KernelTeamSearch.Action> FirstActions(KernelSession root,
        IReadOnlyList<Player> actors, int topK, TournamentOptions o, Func<bool>? stopRequested = null)
    {
        using var budgetScope = KernelSearchBudgetScope.Enter(o.Request);
        bool Stopped() => o.Request?.IsStopped == true || stopRequested?.Invoke() == true;
        var scored = new List<(KernelTeamSearch.Action Action, double Score, ActionRole Role)>();
        var seenCardStates = new Dictionary<string, int>(StringComparer.Ordinal);
        var party = root.Party.Count > 0 ? root.Party : actors;
        IReadOnlyList<KernelTeamSearch.Action>? attritionSequence = null;
        foreach (var player in actors)
        {
            if (Stopped()) break;
            if (!root.CanAct(player)) continue;
            // POTIONS ARE FIRST-CLASS CANDIDATES, exactly as they are for the search
            // (KernelTeamSearch builds potion actions the same way). Leaving them out was why a
            // tournament-driven build never drank: the tournament could not SEE a potion, so no
            // amount of evaluating would ever choose one. A potion is a free action, so it is
            // scored by what it enables rather than by damage — the roll-out decides, since the
            // candidate list only has to contain it.
            foreach (var potion in o.IncludePotions ? root.UsablePotions(player) : [])
            {
                if (Stopped()) break;
                if (!KernelSession.CanModelPotion(potion)) continue;
                if (!PotionHasImmediateValue(root, potion)) continue;
                if (KernelSession.RequiresPotionChoiceContract(potion))
                {
                    // Native target x physical choice openings, funded by this
                    // same request/TopK. A human-owned menu is not auto-planned.
                    foreach (var target in root.PotionTargets(potion))
                    {
                        if (Stopped()) break;
                        foreach (var action in root.PotionActions(potion, target, controlledActors: actors))
                        {
                            if (Stopped()) break;
                            scored.Add((action, 1e8, ActionRole.Potion));
                        }
                    }
                    continue;
                }
                var potionTarget = BestPotionTarget(root, potion);
                // Above every card: a potion costs no energy, so it must not be crowded out of
                // the top-K by a card that merely deals a little more damage. Whether it is
                // actually worth spending is the ROLL-OUT's question, not this ranking's.
                scored.Add((new KernelTeamSearch.Action(player, null, potionTarget, potion), 1e8,
                    ActionRole.Potion));
            }
            if (o.PotionsOnly) continue;
            var withering = root.HasWitheringPresence(player);
            var profitablePlay = false;
            var holdValue = withering ? KernelTacticalPolicy.Value(root,party,actors) : 0;
            seenCardStates.Clear();
            foreach (var card in root.Hand(player))
            {
                if (Stopped()) break;
                var key = root.CardStateKey(card);
                var occurrence = seenCardStates.GetValueOrDefault(key);
                seenCardStates[key] = occurrence + 1;
                if (!root.CanPlay(card)) continue;
                // Enumerate legal targets; complete rollouts decide whether a shielded
                // low-HP enemy is preferable to a vulnerable attacker. Keep distinct
                // card states, not four identical opening Strikes per seat.
                if (occurrence > 0) continue;
                foreach (var target in root.Targets(card))
                {
                    if (Stopped()) break;
                    if(withering && !profitablePlay)
                    {
                        var trial=root.CardBranches(card,target,1).FirstOrDefault(b=>b.Boundary.Length==0);
                        profitablePlay=trial is not null && (trial.State.HasWon
                            || KernelTacticalPolicy.ActionValue(root,trial.State,party,actors)>holdValue);
                    }
                    var (damage, lethal) = root.AttackHint(card, target);
                    var block = root.UsefulBlockHint(card, target);
                    var rider = KernelTemporaryBuffs.Timing(card) is not null
                        ? RolloutRun.RelevantRemaining(root, party, player, card, target) * 1_000_000d
                        : 0;
                    var poisonReady = !CardPoisonEffects.RequiresPoison(card)
                        || target is not null && root.Power<MegaCrit.Sts2.Core.Models.Powers.PoisonPower>(target) > 0;
                    var poison = poisonReady ? CardPoisonEffects.TwoTicks(CardPoisonEffects.DirectAmount(card)) : -1e6;
                    var teamSetup = KernelTeamCoordination.SetupPayoff(root, actors, card, target);
                    // OUR scheduling preference, shared with the continuation. A
                    // defensive one-shot into no pressure stays searchable, but
                    // must not spend the first rollout merely because it has a
                    // TeamDefence label (Knowledge Demon, 2026-10-03).
                    var hold = !lethal && RolloutRun.ShouldHold(root, party, player, card);
                    var role = lethal ? ActionRole.Lethal
                        : hold ? ActionRole.Other
                        : KernelActionCoverage.TeamDefence(card) ? ActionRole.TeamDefence
                        : KernelActionCoverage.Cleanup(card) && KernelActionCoverage.HasHarmfulHand(root, party) ? ActionRole.Cleanup
                        : teamSetup > 0 ? ActionRole.TeamSetup
                        : damage + poison > 0 ? ActionRole.Damage
                        : block > 0 ? ActionRole.Block
                        : card.Type == CardType.Power ? ActionRole.Power
                        : ActionRole.Other;
                    scored.Add((new KernelTeamSearch.Action(player, card, target,
                        CardStateKey: key, CardStateOccurrence: occurrence),
                        (lethal ? 1e9 : 0) + damage + poison + rider
                        + teamSetup * 1e6 - (hold ? 1e8 : 0)
                        + (o.ManualFocusTargetCombatId.HasValue && target?.CombatId == o.ManualFocusTargetCombatId
                            && card.Type == CardType.Attack ? 1e4 : 0), role));
                }
            }
            // A costly first step can enable a profitable sequence. A one-step
            // veto must not promote Pass ahead of every continuation under TopK.
            if (!Stopped() && withering && !profitablePlay)
            {
                attritionSequence ??= RolloutRun.FindAttritionSequence(root, party, actors, o.Rollout ?? new());
                profitablePlay = attritionSequence.Any(a => a.Player == player);
            }
            // NOT PLAYING IS A CANDIDATE, not an absence of one. Only added when a cheap
            // hold rule actually fires, so a normal turn still offers every card and no
            // pass; Wisp with a wasted energy gain and Piercing Wail into a 2-damage turn
            // are exactly the cases where the tournament must be able to choose “end the
            // turn and keep the card”. The roll-out then decides whether holding wins.
            //
            // A mixed table suppresses this while a human is still acting: see
            // TournamentOptions.IncludeEndTurns.
            if (o.IncludeEndTurns && (withering
                || root.Hand(player).Any(card => RolloutRun.ShouldHold(root, party, player, card))
                // Holding one exhaust/setup card does not mean passing the whole
                // hand. Otherwise Pass ranks first and a tied losing rollout
                // skips affordable Strikes while preserving a useless Wisp.
                && !root.Hand(player).Any(card => root.CanPlay(card)
                    && !RolloutRun.ShouldHold(root, party, player, card))))
                // V14 live R5 ended seat one with 3 energy and useful defence:
                // unconditional Pass coverage consumed the first rollout budget.
                // Keep an optional pass searchable, but reserve early coverage
                // for it only when neither a single play nor a sequence improves
                // the current position after its native costs.
                scored.Add((new KernelTeamSearch.Action(player, null, null, EndTurn: true),
                    withering && profitablePlay ? -1e6 : 1e6,
                    withering && profitablePlay ? ActionRole.Other : ActionRole.Pass));
        }

        // COVERAGE BEFORE SCORE. When the deadline binds — 53% of the 2026-09-22 live
        // decisions did — only the first few candidates ever get a rollout. The old
        // `OrderByDescending(score)` made those few the biggest printed damage numbers, so a
        // block/power/support card could never be evaluated in a long fight even though the
        // rollout portfolio itself has a Defend and a Growth policy. Take one best per role,
        // then one best per seat, then fill by score; the rollout outcome still decides the
        // actual move. This only changes WHICH candidates survive the deadline, never the
        // comparator or the winning criterion.
        var ranked = scored.OrderByDescending(entry => entry.Score).ToList();
        var chosen = new List<(KernelTeamSearch.Action Action, double Score, ActionRole Role)>();
        var used = new bool[ranked.Count];
        void TakeFirst(Func<(KernelTeamSearch.Action Action, double Score, ActionRole Role), bool> predicate)
        {
            for (var i = 0; i < ranked.Count; i++)
            {
                if (used[i] || !predicate(ranked[i])) continue;
                used[i] = true;
                chosen.Add(ranked[i]);
                return;
            }
        }
        foreach (var role in new[]
                 {
                     ActionRole.Pass, ActionRole.Lethal, ActionRole.Potion, ActionRole.TeamDefence, ActionRole.Cleanup,
                 })
            TakeFirst(entry => entry.Role == role);
        // A chooser bottle has several physical menu alternatives, but remains
        // one bottle. In the existing potion-only phase, retain one opening per
        // bottle before its extra choices fill TopK and hide ordinary bottles.
        // Other phases and the ending comparator retain their existing policy.
        if (o.PotionsOnly && ranked.Any(e => e.Action.Potion is { } p
            && KernelSession.RequiresPotionChoiceContract(p)))
            foreach (var potion in ranked.Where(e => e.Action.Potion is not null)
                .Select(e => e.Action.Potion!).Distinct())
                TakeFirst(e => ReferenceEquals(e.Action.Potion, potion));
        // Different engines must not compete for a single generic Power slot.
        // Keep the same TopK and reserve at most half of it for distinct powers.
        foreach (var power in ranked.Where(e => e.Action.Card?.Type == CardType.Power)
            .DistinctBy(e => (e.Action.Player, e.Action.CardStateKey)).Take(Math.Max(1, topK / 2)))
            TakeFirst(e => e.Action == power.Action);
        foreach (var role in new[] { ActionRole.TeamSetup, ActionRole.Damage, ActionRole.Block, ActionRole.Other })
            TakeFirst(entry => entry.Role == role);
        foreach (var player in ranked.Select(entry => entry.Action.Player).Distinct())
            TakeFirst(entry => ReferenceEquals(entry.Action.Player, player));
        foreach (var target in ranked.Select(entry => entry.Action.Target).Where(t => t is not null).Distinct())
            TakeFirst(entry => ReferenceEquals(entry.Action.Target, target));
        foreach (var entry in ranked)
            if (chosen.Count >= Math.Max(1, topK)) break;
            else TakeFirst(_ => true);
        return chosen.Take(Math.Max(1, topK)).Select(entry => entry.Action).ToList();
    }

    /// <summary>
    /// Whether a one-turn stat potion has anything left to amplify this turn.
    /// Flex Potion is temporary Strength (attacks); Speed Potion is temporary
    /// Dexterity (block cards). With no matching card left, the bottle is pure
    /// waste and is not offered at all.
    /// </summary>
    private static bool PotionHasImmediateValue(KernelSession root, PotionModel potion) =>
        potion.GetType().Name switch
        {
            "FlexPotion" => root.PotionTargets(potion).Any(target =>
                target?.Player is { } ally && RolloutRun.RemainingAttacks(root, ally) > 0),
            "SpeedPotion" => root.PotionTargets(potion).Any(target =>
                target?.Player is { } ally && RolloutRun.RemainingBlocks(root, ally) > 0),
            _ => true,
        };

    /// <summary>
    /// Target for a potion. For the one-turn stat bottles this is the player with the
    /// most matching cards left; everything else keeps the existing lowest-HP choice.
    /// </summary>
    private static Creature? BestPotionTarget(KernelSession root, PotionModel potion)
    {
        Func<Player, int>? remaining = potion.GetType().Name switch
        {
            "FlexPotion" => ally => RolloutRun.RemainingAttacks(root, ally),
            "SpeedPotion" => ally => RolloutRun.RemainingBlocks(root, ally),
            _ => null,
        };
        Creature? best = null;
        var bestCount = -1;
        foreach (var candidate in root.PotionTargets(potion))
        {
            if (candidate is not { } target) continue;
            if (remaining is null || target.Player is not { } ally)
            {
                if (best is null || root.Hp(target) < root.Hp(best)) best = target;
                continue;
            }
            var count = remaining(ally);
            if (count > bestCount || (count == bestCount && (best is null || root.Hp(target) < root.Hp(best))))
            {
                best = target;
                bestCount = count;
            }
        }
        return best;
    }

    /// <summary>
    /// The roll-out candidate's coarse role. Only used to spread the first few candidates
    /// across the card types when the budget cuts the list; it is deliberately cheaper and
    /// blunter than the evaluator.
    /// </summary>
    private enum ActionRole { Pass, Lethal, TeamDefence, Cleanup, TeamSetup, Damage, Block, Power, Other, Potion }
}

/// <summary>
/// A tournament that can be advanced a little at a time, and — when asked — several rollouts at
/// once.
///
/// WHY SLICED: the tournament is ~18 rollouts of ~27 ms, i.e. 500-1200 ms of uninterrupted work.
/// Run in one call on Godot's main thread that lands squarely on top of the card-play animation —
/// measured live 2026-09-22, `[ActionExecutor] Completed execution of action` was followed
/// immediately by `decision-ms=1003.1`, and the visual tail of the card froze mid-flight. A user
/// reads a frozen animation as a bug, not as thinking.
///
/// WHY PARALLEL: the rollouts are independent, so a tournament can use more than one core. The
/// engine already does this for its own expansion (CombatBeamSolver.ParallelExpansion), and it is
/// where the shape below comes from.
///
/// THE ONE THING THAT MAY NOT BE PARALLELISED is forking. From that same upstream file:
///
///   "A parent simulator cannot be forked concurrently: prediction history seals its mutable
///    tail and several COW containers publish a shared bit during Fork. Lanes serialize seed
///    creation through the parent's gate; each worker then consumes only its private fork."
///
/// So every `Fork()` below happens on the CALLER's thread, in the launch loop, and a worker only
/// ever touches the private session it was handed. A `Parallel.For` over the rollout loop would
/// fork the shared parent concurrently and corrupt it silently.
///
/// RESULTS DO NOT DEPEND ON COMPLETION ORDER: lanes are consumed in launch order, so a tie
/// between two equal endings picks the same winner the sequential loop would. Legacy calls
/// retain their compute budget. Explicit requests share one absolute wall deadline, while
/// summed worker time remains telemetry rather than another wall-clock allowance.
/// </summary>
public sealed class TournamentRun : IDisposable
{
    private readonly KernelSession root;
    private readonly IReadOnlyList<Player> party;
    private readonly IReadOnlyList<Player> actors;
    private readonly TournamentOptions o;
    private readonly int budgetMs;
    private readonly int dop;
    private readonly RolloutOptions rolloutOptions;
    private readonly List<KernelTeamSearch.Action> candidates;
    private readonly RolloutPolicyKind[] policies;
    private string? cancelled;
    private readonly Stopwatch? phaseClock;
    private bool StopRequested => Volatile.Read(ref cancelled) is not null || o.Request?.IsStopped == true
        || phaseClock is not null && phaseClock.Elapsed.TotalMilliseconds >= budgetMs;
    private bool IsCancelled => Volatile.Read(ref cancelled) is not null || o.Request?.IsCancelled == true;
    private string RequestStop => Volatile.Read(ref cancelled) is { } reason
        ? "request-cancelled:" + reason : o.Request is { IsStopped: true } request ? request.StopReason : "phase-deadline";
    public long? RootGeneration => o.Request?.RootGeneration;
    public int ActiveWorkerJobs => inFlight.Count(lane => !lane.Finished);
    public double PhaseWallMs => phaseClock?.Elapsed.TotalMilliseconds ?? 0;
    private double BudgetElapsed(Stopwatch? scope = null) => phaseClock?.Elapsed.TotalMilliseconds
        ?? computeMs + (scope?.Elapsed.TotalMilliseconds ?? 0);

    private readonly List<TerminalRecord> considered = new();
    private readonly HashSet<(Player, string, int, Creature?, PotionModel?, bool)> evaluatedOpenings = new();
    private KernelTeamSearch.Action? bestAction;
    private TerminalRecord? bestRecord;
    // The winning roll-out's whole line, not just its first action. Kept beside bestRecord so
    // the planner can keep playing a line the fresh tournament can no longer reproduce.
    private IReadOnlyList<KernelTeamSearch.Action>? bestScript;
    private IReadOnlyList<ulong[]>? bestScriptAliveStates;
    private IReadOnlyList<Creature[]>? bestScriptEnemyStates;
    private IReadOnlyList<KernelActionCheckpoint>? bestScriptCheckpoints;
    private int rollouts;
    private int cutoffs;
    private string stop = "top-k-exhausted";

    private int candidateIndex;
    private int policyIndex;
    private KernelSession? branch;
    /// <summary>
    /// The action the in-flight candidate would play. Nullable only for the constructor paths
    /// that finish immediately (an unforkable root, no legal action) — it is assigned by
    /// <see cref="TryOpenNextRollout"/> before any rollout exists, so the `!` in
    /// <see cref="Consume"/> cannot see null.
    /// </summary>
    private KernelTeamSearch.Action? currentAction;
    private RolloutRun? rollout;
    private double rolloutStartedMs, rolloutCapMs;

    /// <summary>Workers still running. Empty on the sequential path.</summary>
    private readonly List<Lane> inFlight = new();
    /// <summary>A launch attempt hit the deadline or ran out of candidates while lanes were out.</summary>
    private bool pendingFinish;

    /// <summary>Time actually spent computing, across every Advance. Never wall time.</summary>
    private double computeMs;
    // Rollout.Step includes policy selection and the native action/choice tail.
    // These are elapsed worker intervals, not CPU or disjoint game-thread time.
    private long rolloutStepCount, rolloutStepMaxTicks, rolloutStepDeadlineTailTicks;
    private long rolloutStepsCrossingDeadline;
    private KernelJointTurnRun? joint;
    private bool jointFinished;
    private bool jointSelected;
    private RolloutRun? referenceRun;
    private RolloutRun? manualFocusRun;
    private KernelTeamSearch.Action? manualFocusAction;
    private bool manualFocusFinished;
    private double manualFocusStartedMs;
    // Only this tournament's own completed Kill reference may seed joint work.
    // bestScript can already describe a bottle or a different policy, and an
    // external ReferenceResult does not prove the same physical search root.
    private TournamentResult? ownedCompletedCardReference;
    private bool referenceFinished;
    private double referenceStartedMs;
    private string referenceStop = "not-run";
    private int? referenceSteps;
    private static readonly RolloutPolicyKind[] PolicyReferences =
        [RolloutPolicyKind.Defend, RolloutPolicyKind.Growth];
    private readonly List<string> policyReferenceStops = new();
    private readonly List<TournamentResult> policyReferenceResults = new();
    private int policyReferenceIndex;
    private RolloutRun? policyReferenceRun;
    private double policyReferenceDeadlineMs = -1, policyReferenceStartedMs, policyReferenceCapMs;
    private bool policyReferencesFinished;
    // A stopped policy is not a completed ending. Its already settled actions
    // can still supply an executable prefix when no accepted result exists.
    // Keep that evidence separate so it never replaces a terminal/reference.
    private TournamentResult? interruptedPolicyPrefix;
    private RolloutPolicyKind? interruptedPolicyPrefixKind;
    // A short scheduling share is a pause, not the end of this native line.
    // Retain state, choices and RNG so failed-reference alternatives can use
    // the remaining fixed deadline before spending it on another beam.
    private readonly Queue<(RolloutPolicyKind Policy, RolloutRun Run)> pausedPolicyReferences = new();
    private int resumedPolicyReferences;
    private KernelPotionContinuationRun? potionReferences;
    private bool potionReferencesFinished;
    private int consumedPotionReferences;

    private bool CanCompareReferencePotions => o.IncludePotions && o.UsePolicyPortfolio && !o.PotionsOnly
        && candidates.Any(action => action.Potion is not null);

    public TournamentResult? Result { get; private set; }
    public bool Done => Result is not null;

    /// <summary>Rollouts finished so far — the unit the budget is really spent in.</summary>
    public int Rollouts => rollouts;
    public int EvaluatedOpenings => evaluatedOpenings.Count;

    /// <summary>
    /// Time this run actually spent computing, summed over every <see cref="Advance"/> and over
    /// every worker. It is not wall time: workers can overlap, and per-frame slicing makes
    /// callers' wall time include everything the engine did between ticks. Callers that
    /// report a cost ratio (the planner's gcPause share, B1) need this
    /// denominator rather than their own stopwatch, or the ratio is not the one being gated.
    /// </summary>
    public double ComputeMs => computeMs;

    public long RolloutStepCount => Interlocked.Read(ref rolloutStepCount);
    public double RolloutStepMaxMs => Interlocked.Read(ref rolloutStepMaxTicks) * 1000d / Stopwatch.Frequency;
    public double RolloutStepDeadlineTailMs => Interlocked.Read(ref rolloutStepDeadlineTailTicks) * 1000d / Stopwatch.Frequency;
    public long RolloutStepsCrossingDeadline => Interlocked.Read(ref rolloutStepsCrossingDeadline);

    private void StepRollout(RolloutRun run)
    {
        var started = Stopwatch.GetTimestamp();
        var request = o.Request;
        var remainingBeforeMs = request is null ? double.PositiveInfinity : Math.Max(0, request.BudgetMs - request.ElapsedMs);
        var stoppedBefore = request?.IsStopped == true;
        try { run.Step(); }
        finally
        {
            var elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Increment(ref rolloutStepCount);
            var maximum = Interlocked.Read(ref rolloutStepMaxTicks);
            while (elapsed > maximum)
            {
                var previous = Interlocked.CompareExchange(ref rolloutStepMaxTicks, elapsed, maximum);
                if (previous == maximum) break;
                maximum = previous;
            }
            if (request is not null && !stoppedBefore && request.DeadlineReached)
            {
                Interlocked.Increment(ref rolloutStepsCrossingDeadline);
                var tail = Math.Max(0, elapsed - (long)(remainingBeforeMs * Stopwatch.Frequency / 1000d));
                Interlocked.Add(ref rolloutStepDeadlineTailTicks, tail);
            }
        }
    }

    public TournamentRun(KernelSession root, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, TournamentOptions? options, int budgetMs)
    {
        this.root = root;
        this.party = party;
        this.actors = actors;
        o = options ?? new TournamentOptions();
        using var budgetScope = KernelSearchBudgetScope.Enter(o.Request);
        this.budgetMs = budgetMs;
        phaseClock = o.Request is null ? null : Stopwatch.StartNew();
        rolloutOptions = o.Rollout ?? new RolloutOptions(o.Policy);
        dop = Math.Max(1, Math.Min(o.MaxDegreeOfParallelism, o.Request?.MaxConcurrency ?? int.MaxValue));
        var acceptedReference = o.ReferenceResult is { FirstAction: not null, Record: not null }
            && (o.Request is null || o.ReferenceResult.IsFor(o.Request));
        if (acceptedReference && o.ReferenceResult is { } reference)
        {
            bestAction = reference.FirstAction; bestRecord = reference.Record;
            bestScript = reference.Script; bestScriptAliveStates = reference.ScriptAliveStates;
            bestScriptEnemyStates = reference.ScriptEnemyStates; bestScriptCheckpoints = reference.ScriptCheckpoints;
            considered.Add(reference.Record!); // acceptedReference already proved this immutable record non-null.
        }

        if (StopRequested)
        {
            candidates = []; policies = []; stop = RequestStop;
            Finish(); return;
        }
        if (!root.CanFork)
        {
            Result = new TournamentResult(null, null, [], 0, 0, "unresolved-root") {
                RootGeneration = RootGeneration, RequestProvenance = o.Request,
            };
            candidates = [];
            policies = [];
            Result = AttachDiagnostics(Result);
            return;
        }

        var watch = Stopwatch.StartNew();
        candidates = KernelTournament.FirstActions(root, actors, o.TopK, o, () => StopRequested);
        computeMs = watch.Elapsed.TotalMilliseconds;
        if (StopRequested)
        {
            policies = []; stop = RequestStop;
            Finish(); return;
        }
        if (candidates.Count == 0)
        {
            Result = acceptedReference ? o.ReferenceResult
                : new TournamentResult(null, null, [], 0, 0, "no-legal-action");
            Result = Result! with { RootGeneration = RootGeneration, RequestProvenance = o.Request };
            policies = [];
            Result = AttachDiagnostics(Result);
            return;
        }

        // Rollout.Run mutates its argument. Every policy must start from an independent
        // copy of the SAME opened position, never from another policy's terminal state.
        policies = o.UsePolicyPortfolio
            ? new[] { RolloutPolicyKind.Tactical, RolloutPolicyKind.Growth, rolloutOptions.Policy, RolloutPolicyKind.Kill, RolloutPolicyKind.Defend }.Distinct().ToArray()
            : new[] { rolloutOptions.Policy };
    }

    /// <summary>
    /// Do at most <paramref name="slice"/> of COMPUTE, then return. Returns true once
    /// <see cref="Result"/> is final. Passing a very large slice runs it to completion, which
    /// is exactly what the one-shot façade does.
    ///
    /// On the parallel path the slice BOUNDS THE MAIN-THREAD LAUNCH LOOP, not the worker
    /// completion. Forking a candidate branch and opening its first rollout happen on the
    /// caller's thread; on a late-act boss those forks can each cost more than a frame, and
    /// launching all N-2 lanes in one call was the measured animation stutter. The caller
    /// returns to the frame after the slice, then collects/completes on later ticks.
    /// </summary>
    public bool Advance(TimeSpan slice)
    {
        using var budgetScope = KernelSearchBudgetScope.Enter(o.Request);
        if (Done)
        {
            if (IsCancelled) Result = Result! with { FirstAction = null, Record = null,
                Script = null, StopReason = RequestStop };
            return true;
        }
        if (StopRequested) return SettleStoppedRequest();
        var complete = AdvanceCurrent(slice);
        return StopRequested ? SettleStoppedRequest() : complete;
    }

    // Stop only at settled native action boundaries. A cancelled caller can
    // drop this object immediately; its workers observe the same signal without
    // requiring another Poll. Completed evidence remains in BestCompleted.
    public void Cancel(string reason = "cancelled")
        => Interlocked.CompareExchange(ref cancelled, string.IsNullOrEmpty(reason) ? "cancelled" : reason, null);
    public void Dispose() => Cancel("disposed");

    private bool SettleStoppedRequest()
    {
        foreach (var lane in inFlight)
            if (!lane.Finished) return false; // nonblocking drain
        foreach (var lane in inFlight)
        {
            if ((lane.Error ?? lane.Worker?.Error) is { } error) throw error;
            Consume(lane.Run, lane.Action); computeMs += lane.ElapsedMs;
        }
        inFlight.Clear();
        if (rollout is { } unlaunched)
        {
            unlaunched.FinishAtBudget();
            if (unlaunched.Result is { Terminal: true } && currentAction is { } opening)
                Consume(unlaunched, opening);
            rollout = null;
        }
        if (referenceRun is { } reference)
        {
            PreserveTerminalReference(reference, rememberCardReference: true);
            referenceStop = reference.Result!.StopReason;
            referenceSteps = reference.Result.Steps;
            referenceRun = null;
        }
        if (manualFocusRun is { } focused)
        {
            focused.FinishAtBudget();
            if (focused.Result is { Terminal: true } && manualFocusAction is { } opening)
                Consume(focused, opening);
            manualFocusRun = null;
        }
        if (policyReferenceRun is { } policyReference)
        {
            PreserveTerminalReference(policyReference, rememberCardReference: false);
            PreserveInterruptedPolicyPrefix(policyReference, PolicyReferences[policyReferenceIndex]);
            policyReferenceRun = null;
        }
        foreach (var pending in pausedPolicyReferences)
        {
            PreserveTerminalReference(pending.Run, pending.Policy == RolloutPolicyKind.Kill);
            PreserveInterruptedPolicyPrefix(pending.Run, pending.Policy);
            if (pending.Policy == RolloutPolicyKind.Kill)
            {
                referenceStop = pending.Run.Result!.StopReason;
                referenceSteps = pending.Run.Result.Steps;
            }
            policyReferenceStops.Add($"{pending.Policy}:{pending.Run.Result!.StopReason}");
        }
        pausedPolicyReferences.Clear();
        stop = RequestStop;
        Finish();
        // A native action may have completed the tournament just as the clock
        // stopped. Keep its evidence, but publish the actual scheduling reason.
        Result = IsCancelled
            ? Result! with { FirstAction = null, Record = null, Script = null, StopReason = stop }
            : Result! with { StopReason = stop };
        return true;
    }

    private bool AdvanceCurrent(TimeSpan slice)
    {
        // OUR anytime reference: obtain a cheap, whole policy line before
        // spending the allowance on beam expansion or nested investment probes.
        // It chooses its own opening, so root enumeration cannot bypass holds.
        if (!referenceFinished && o.EnableJointSearch && !o.PotionsOnly && bestRecord is null)
            return AdvanceReference(slice);
        // Fund one actual focused opening before optional beam work can spend
        // the remainder. Retargeting a live card without replaying its ending
        // would silently discard the defense and emergency guarantees.
        if (!manualFocusFinished && o.EnableJointSearch && !o.PotionsOnly && o.ManualFocusTargetCombatId.HasValue)
            return AdvanceManualFocus(slice);
        // A complete card line is also a cheap replay proposal for a bottle
        // opening. Fund that real different opening before another whole card
        // policy repeats the reference's first move under the same deadline.
        if (!potionReferencesFinished && o.EnableJointSearch && CanCompareReferencePotions
            && bestRecord is { Verified: true } && bestScript is { Count: > 0 })
            return AdvancePotionReferences(slice);
        // Different policies must choose their own opening from the same root.
        // Reusing Kill's paid attack prefix can make its Defend continuation
        // too late to buy the block that was available before that attack.
        // Complete these bounded native comparisons before expanding a beam;
        // nested Tactical probes must not spend their allowance first.
        if (!policyReferencesFinished && o.EnableJointSearch && o.UsePolicyPortfolio
            && o.DiverseJointContinuations && !o.PotionsOnly)
            return AdvancePolicyReferences(slice);
        if (pausedPolicyReferences.Count > 0 && bestRecord is not { Verified: true, Victory: true })
            return AdvancePausedPolicyReferences(slice);
        // Joint prefixes inherit the potion-only opening constraint, while
        // retaining multi-bottle combinations after that first action.
        if (o.EnableJointSearch && !jointFinished)
        {
            var watch = Stopwatch.StartNew();
            joint ??= new KernelJointTurnRun(root, party, actors, o,
                Math.Max(1, (int)((budgetMs - BudgetElapsed() - o.ReserveMs) * .6)),
                hasVerifiedReference: bestRecord is { Verified: true },
                ownedCompletedCardReference: (o.ContinuationActors ?? actors).SequenceEqual(actors)
                    ? ownedCompletedCardReference : null) { StopRequested = () => StopRequested };
            var complete = joint.Advance(slice);
            computeMs += watch.Elapsed.TotalMilliseconds;
            if (!complete) return false;
            jointFinished = true;
            if (joint.Result is { FirstAction: not null, Record: not null } found
                && (bestRecord is null || o.Compare(found.Record, found.FirstAction, bestRecord, bestAction) > 0))
            {
                bestAction = found.FirstAction; bestRecord = found.Record;
                bestScript = found.Script; bestScriptAliveStates = found.ScriptAliveStates;
                bestScriptEnemyStates = found.ScriptEnemyStates; bestScriptCheckpoints = found.ScriptCheckpoints;
                considered.AddRange(found.Considered);
                rollouts += joint.Prefixes; cutoffs += found.Considered.Count(r => !r.Verified);
                jointSelected = true;
            }
            return false;
        }
        return dop <= 1 ? AdvanceSerial(slice) : AdvanceParallel(slice);
    }

    private bool AdvanceManualFocus(TimeSpan slice)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (manualFocusRun is null)
            {
                var action = candidates.FirstOrDefault(o.FollowsManualFocus);
                if (action?.Card is null || o.FollowsManualFocus(bestAction)
                    || BudgetElapsed() + o.ReserveMs >= budgetMs)
                { manualFocusFinished = true; return false; }
                manualFocusStartedMs = BudgetElapsed();
                var opened = root.CardBranches(action.Card, action.Target, maximumBranches: 1)
                    .FirstOrDefault(b => b.Boundary.Length == 0);
                if (opened is null) { manualFocusFinished = true; return false; }
                manualFocusAction = action with { Choices = opened.Choices,
                    ChoiceOrdinals = opened.ChoiceOrdinals, TurnStartChoices = opened.TurnStartChoices };
                manualFocusRun = new RolloutRun(opened.State, party, o.ContinuationActors ?? actors,
                    rolloutOptions with { Policy = RolloutPolicyKind.Kill,
                        InvestmentLookahead = false, SequenceLookahead = true,
                        SequenceHandLookahead = false, CaptureActionCheckpoints = true })
                    { StopRequested = () => StopRequested };
            }
            do
            {
                if (StopRequested) return false;
                StepRollout(manualFocusRun);
                if (!manualFocusRun.Done && (BudgetElapsed(watch) + o.ReserveMs >= budgetMs
                    || BudgetElapsed(watch) - manualFocusStartedMs >= Math.Max(1, (budgetMs - o.ReserveMs) / 3d)))
                    manualFocusRun.FinishAtBudget();
            } while (!manualFocusRun.Done && watch.Elapsed < slice);
            if (!manualFocusRun.Done) return false;
            Consume(manualFocusRun, manualFocusAction!);
            manualFocusRun = null; manualFocusFinished = true;
            return false;
        }
        finally { computeMs += watch.Elapsed.TotalMilliseconds; }
    }

    private bool AdvanceReference(TimeSpan slice)
    {
        var watch = Stopwatch.StartNew();
        if (referenceRun is null)
        {
            referenceStartedMs = BudgetElapsed();
            referenceRun = new RolloutRun(root.Fork(), party, o.ContinuationActors ?? actors,
                rolloutOptions with { Policy = RolloutPolicyKind.Kill,
                    InvestmentLookahead = false, SequenceLookahead = true,
                    SequenceHandLookahead = false, CaptureActionCheckpoints = true }) { StopRequested = () => StopRequested };
        }
        do
        {
            if (StopRequested) return false;
            StepRollout(referenceRun);
            if (!referenceRun.Done && BudgetElapsed(watch) + o.ReserveMs >= budgetMs)
                referenceRun.FinishAtBudget();
            else if (!referenceRun.Done && !CanCompareReferencePotions && BudgetElapsed(watch) - referenceStartedMs
                >= Math.Max(1, (budgetMs - o.ReserveMs) / 3d))
            {
                pausedPolicyReferences.Enqueue((RolloutPolicyKind.Kill, referenceRun));
                referenceStop = "paused";
                referenceSteps = referenceRun.CommittedSteps;
                referenceFinished = true;
                referenceRun = null;
                computeMs += watch.Elapsed.TotalMilliseconds;
                return false;
            }
        } while (!referenceRun.Done && watch.Elapsed < slice);
        computeMs += watch.Elapsed.TotalMilliseconds;
        if (!referenceRun.Done) return false;
        referenceFinished = true;
        var result = referenceRun.Result!;
        referenceStop = result.StopReason;
        referenceSteps = result.Steps;
        RememberCompletedCardReference(result);
        if (result.Actions.FirstOrDefault() is { } first && (!first.EndTurn || o.IncludeEndTurns))
        {
            bestAction = first; bestRecord = result.Record;
            bestScript = result.Actions; bestScriptAliveStates = result.AliveStates;
            bestScriptEnemyStates = result.EnemyStates; bestScriptCheckpoints = result.Checkpoints;
            considered.Add(result.Record); rollouts++; if (result.Cutoff) cutoffs++;
            evaluatedOpenings.Add((first.Player, first.CardStateKey, first.CardStateOccurrence,
                first.Target, first.Potion, first.EndTurn));
        }
        referenceRun = null;
        return false;
    }

    private void RememberCompletedCardReference(RolloutOutcome outcome)
    {
        if (!outcome.Terminal || !outcome.Record.Verified
            || outcome.Actions.FirstOrDefault() is not { Potion: null } first
            || first.EndTurn && !o.IncludeEndTurns) return;
        ownedCompletedCardReference = new(first, outcome.Record, [outcome.Record], 1, 0,
            outcome.StopReason)
        {
            RootGeneration = RootGeneration, RequestProvenance = o.Request,
            Script = outcome.Actions,
            ScriptAliveStates = outcome.AliveStates,
            ScriptCheckpoints = outcome.Checkpoints, ScriptEnemyStates = outcome.EnemyStates,
        };
    }

    // A slice may return just after the last paid action, before the next Step
    // observes the terminal. Finalize only that real ending at a request stop;
    // unfinished reference states remain cutoffs and cannot displace evidence.
    private void PreserveTerminalReference(RolloutRun run, bool rememberCardReference)
    {
        run.FinishAtBudget();
        if (run.Result is not { Terminal: true, Record.Verified: true } outcome
            || !run.State.CanFork
            || outcome.Actions.FirstOrDefault() is not { } first
            || first.EndTurn && !o.IncludeEndTurns) return;
        if (rememberCardReference) RememberCompletedCardReference(outcome);
        considered.Add(outcome.Record); rollouts++;
        evaluatedOpenings.Add((first.Player, first.CardStateKey, first.CardStateOccurrence,
            first.Target, first.Potion, first.EndTurn));
        if (bestRecord is null || o.Compare(outcome.Record, first, bestRecord, bestAction) > 0)
        {
            bestAction = first; bestRecord = outcome.Record;
            bestScript = outcome.Actions; bestScriptAliveStates = outcome.AliveStates;
            bestScriptEnemyStates = outcome.EnemyStates; bestScriptCheckpoints = outcome.Checkpoints;
        }
    }

    private bool AdvancePotionReferences(TimeSpan slice)
    {
        var watch = Stopwatch.StartNew();
        potionReferences ??= new(root, party, o.ContinuationActors ?? actors,
            new(bestAction, bestRecord, [bestRecord!], 1, 0, "reference") {
                Script = bestScript, ScriptAliveStates = bestScriptAliveStates, ScriptCheckpoints = bestScriptCheckpoints, ScriptEnemyStates = bestScriptEnemyStates },
            Math.Max(0, (int)(budgetMs - BudgetElapsed() - o.ReserveMs)), rolloutOptions with {
                Policy = RolloutPolicyKind.Kill, InvestmentLookahead = false,
                SequenceLookahead = true, SequenceHandLookahead = false }, o.Request) { StopRequested = () => StopRequested };
        var complete = potionReferences.Advance(slice);
        while (consumedPotionReferences < potionReferences.CompletedResults.Count)
        {
            var candidate = potionReferences.CompletedResults[consumedPotionReferences++];
            var record = candidate.Record!; var first = candidate.FirstAction!;
            if (!record.Verified) continue;
            considered.Add(record); rollouts++;
            evaluatedOpenings.Add((first.Player, first.CardStateKey, first.CardStateOccurrence,
                first.Target, first.Potion, first.EndTurn));
            if (bestRecord is null || o.Compare(record, first, bestRecord, bestAction) > 0)
            {
                bestAction = first; bestRecord = record; bestScript = candidate.Script;
                bestScriptAliveStates = candidate.ScriptAliveStates; bestScriptEnemyStates = candidate.ScriptEnemyStates; bestScriptCheckpoints = candidate.ScriptCheckpoints;
            }
        }
        computeMs += watch.Elapsed.TotalMilliseconds;
        potionReferencesFinished = complete;
        if (BudgetElapsed() + o.ReserveMs >= budgetMs)
        { policyReferencesFinished = true; jointFinished = true; stop = "deadline"; Finish(); }
        return Done;
    }

    private bool AdvancePolicyReferences(TimeSpan slice)
    {
        var watch = Stopwatch.StartNew();
        if (policyReferenceDeadlineMs < 0)
            policyReferenceDeadlineMs = BudgetElapsed() + Math.Max(0,
                (budgetMs - BudgetElapsed() - o.ReserveMs) * .35);
        do
        {
            if (StopRequested) break;
            if (policyReferenceIndex >= PolicyReferences.Length)
            { policyReferencesFinished = true; break; }
            if (policyReferenceRun is null)
            {
                var available = policyReferenceDeadlineMs - BudgetElapsed(watch);
                if (available <= 0)
                {
                    policyReferenceStops.AddRange(PolicyReferences.Skip(policyReferenceIndex)
                        .Select(policy => $"{policy}:not-funded"));
                    policyReferencesFinished = true; break;
                }
                policyReferenceStartedMs = BudgetElapsed(watch);
                policyReferenceCapMs = available / (PolicyReferences.Length - policyReferenceIndex);
                policyReferenceRun = new RolloutRun(root.Fork(), party, o.ContinuationActors ?? actors,
                    rolloutOptions with { Policy = PolicyReferences[policyReferenceIndex],
                        InvestmentLookahead = false, SequenceLookahead = true,
                        SequenceHandLookahead = false, CaptureActionCheckpoints = true }) { StopRequested = () => StopRequested };
            }
            StepRollout(policyReferenceRun);
            var used = BudgetElapsed(watch);
            if (!policyReferenceRun.Done && used + o.ReserveMs >= budgetMs) policyReferenceRun.FinishAtBudget();
            else if (!policyReferenceRun.Done && used - policyReferenceStartedMs >= policyReferenceCapMs)
            {
                pausedPolicyReferences.Enqueue((PolicyReferences[policyReferenceIndex], policyReferenceRun));
                policyReferenceStops.Add($"{PolicyReferences[policyReferenceIndex]}:paused");
                policyReferenceRun = null; policyReferenceIndex++;
                continue;
            }
            if (!policyReferenceRun.Done) continue;
            var outcome = policyReferenceRun.Result!;
            policyReferenceStops.Add($"{PolicyReferences[policyReferenceIndex]}:{outcome.StopReason}");
            // A deadline is an explicit cutoff, never a completed comparison.
            // Keep its diagnostic, but do not let it displace a real ending.
            if (outcome.StopReason != "continuation-budget"
                && outcome.Actions.FirstOrDefault() is { } first && (!first.EndTurn || o.IncludeEndTurns))
            {
                var candidate = new TournamentResult(first, outcome.Record, [outcome.Record], 1,
                    outcome.Cutoff ? 1 : 0, "policy-reference")
                { Script = outcome.Actions, ScriptAliveStates = outcome.AliveStates,
                    ScriptCheckpoints = outcome.Checkpoints, ScriptEnemyStates = outcome.EnemyStates };
                policyReferenceResults.Add(candidate);
                considered.Add(outcome.Record); rollouts++; if (outcome.Cutoff) cutoffs++;
                evaluatedOpenings.Add((first.Player, first.CardStateKey, first.CardStateOccurrence,
                    first.Target, first.Potion, first.EndTurn));
                if (bestRecord is null || o.Compare(outcome.Record, first, bestRecord, bestAction) > 0)
                {
                    bestAction = first; bestRecord = outcome.Record;
                    bestScript = outcome.Actions; bestScriptAliveStates = outcome.AliveStates;
                    bestScriptEnemyStates = outcome.EnemyStates; bestScriptCheckpoints = outcome.Checkpoints;
                }
            }
            else if (outcome.StopReason == "continuation-budget")
            { cutoffs++; PreserveInterruptedPolicyPrefix(policyReferenceRun, PolicyReferences[policyReferenceIndex]); }
            policyReferenceRun = null; policyReferenceIndex++;
        } while (watch.Elapsed < slice);
        computeMs += watch.Elapsed.TotalMilliseconds;
        return false;
    }

    private bool AdvancePausedPolicyReferences(TimeSpan slice)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            if (StopRequested) break;
            var (policy, run) = pausedPolicyReferences.Dequeue();
            // One native action per lane makes both independent openings
            // progress. A slow policy cannot consume another lane's share.
            StepRollout(run); resumedPolicyReferences++;
            if (policy == RolloutPolicyKind.Kill) referenceSteps = run.CommittedSteps;
            if (!run.Done && BudgetElapsed(watch) + o.ReserveMs >= budgetMs)
                run.FinishAtBudget();
            if (!run.Done) pausedPolicyReferences.Enqueue((policy, run));
            else
            {
                var outcome = run.Result!;
                if (policy == RolloutPolicyKind.Kill)
                {
                    referenceStop = outcome.StopReason;
                    referenceSteps = outcome.Steps;
                    RememberCompletedCardReference(outcome);
                }
                policyReferenceStops.Add($"{policy}:resumed-{outcome.StopReason}");
                if (outcome.StopReason == "continuation-budget")
                { cutoffs++; PreserveInterruptedPolicyPrefix(run, policy); }
                else if (outcome.Actions.FirstOrDefault() is { } first && (!first.EndTurn || o.IncludeEndTurns))
                {
                    considered.Add(outcome.Record); rollouts++; if (outcome.Cutoff) cutoffs++;
                    evaluatedOpenings.Add((first.Player, first.CardStateKey, first.CardStateOccurrence,
                        first.Target, first.Potion, first.EndTurn));
                    if (bestRecord is null || o.Compare(outcome.Record, first, bestRecord, bestAction) > 0)
                    {
                        bestAction = first; bestRecord = outcome.Record;
                        bestScript = outcome.Actions; bestScriptAliveStates = outcome.AliveStates;
                        bestScriptEnemyStates = outcome.EnemyStates; bestScriptCheckpoints = outcome.Checkpoints;
                    }
                }
            }
            if (BudgetElapsed(watch) + o.ReserveMs >= budgetMs)
            {
                while (pausedPolicyReferences.TryDequeue(out var pending))
                {
                    PreserveTerminalReference(pending.Run, pending.Policy == RolloutPolicyKind.Kill);
                    PreserveInterruptedPolicyPrefix(pending.Run, pending.Policy);
                    if (pending.Run.Result!.Cutoff) cutoffs++;
                    if (pending.Policy == RolloutPolicyKind.Kill)
                    {
                        referenceStop = pending.Run.Result!.StopReason;
                        referenceSteps = pending.Run.Result.Steps;
                    }
                    policyReferenceStops.Add($"{pending.Policy}:{pending.Run.Result!.StopReason}");
                }
                policyReferencesFinished = true; jointFinished = true;
                stop = "deadline"; Finish(); break;
            }
        } while (pausedPolicyReferences.Count > 0 && watch.Elapsed < slice);
        computeMs += watch.Elapsed.TotalMilliseconds;
        return Done;
    }

    private bool AdvanceSerial(TimeSpan slice)
    {
        if (Done) return true;
        var watch = Stopwatch.StartNew();
        while (!Done)
        {
            if (StopRequested) break;
            // ONLY OPEN WHEN NOTHING IS IN FLIGHT. TryOpenNextRollout always builds a new
            // rollout, so calling it every iteration would discard the half-stepped one and no
            // rollout would ever reach its end — the loop would burn every candidate and finish
            // with `rollouts=0, no-playable-first-action`. Step the open one to completion first.
            if (rollout is null)
            {
                if (TryOpenNextRollout(watch) != OpenOutcome.Opened) { Finish(); break; }
                continue;
            }
            StepRollout(rollout);
            if (!rollout.Done && (BudgetElapsed(watch) + o.ReserveMs >= budgetMs
                || BudgetElapsed(watch) - rolloutStartedMs >= rolloutCapMs))
                rollout.FinishAtBudget();
            if (rollout.Done) { Consume(rollout, currentAction!); rollout = null; }
            if (watch.Elapsed >= slice) break;
        }
        computeMs += watch.Elapsed.TotalMilliseconds;
        return Done;
    }

    private bool AdvanceParallel(TimeSpan slice)
    {
        // COLLECT FIRST, IN LAUNCH ORDER — never in completion order. `TerminalComparer` keeps
        // the FIRST of two equal endings, so consuming out of order would make a tie-break
        // depend on which worker happened to finish first.
        if (inFlight.Count > 0)
        {
            foreach (var lane in inFlight)
                if (!lane.Finished) return false;
            foreach (var lane in inFlight)
            {
                // An exception belongs to the CALLER's thread: TryTournamentDecision catches it
                // and declines the tick, exactly as it did when the loop ran inline.
                if ((lane.Error ?? lane.Worker?.Error) is { } error) { inFlight.Clear(); throw error; }
                Consume(lane.Run, lane.Action);
                computeMs += lane.ElapsedMs;
            }
            inFlight.Clear();
            if (pendingFinish) { pendingFinish = false; Finish(); }
            return Done;
        }

        // ONE WAVE, BUT BOUNDED BY THE FRAME SLICE. `TryOpenNextRollout` forks/opens on this
        // thread; a large Act 3 boss can make one fork cost several ms, so `while (count < dop)`
        // could blow a whole frame budget before the first worker even starts. Always open at
        // least one lane, then stop once this call has spent the slice.
        var launch = Stopwatch.StartNew();
        var launchBudget = slice == TimeSpan.MaxValue
            ? TimeSpan.MaxValue
            : TimeSpan.FromMilliseconds(Math.Max(1, slice.TotalMilliseconds));
        while (inFlight.Count < dop)
        {
            if (StopRequested) break;
            if (inFlight.Count > 0 && launch.Elapsed >= launchBudget) break;
            if (rollout is null && TryOpenNextRollout(launch) != OpenOutcome.Opened) { pendingFinish = true; break; }
            if (!Launch(rollout!, currentAction!, Math.Min(rolloutCapMs,
                Math.Max(1, (budgetMs - BudgetElapsed(launch) - o.ReserveMs) / (o.Request is null ? dop : 1))))) break;
            rollout = null;
        }
        computeMs += launch.Elapsed.TotalMilliseconds;
        if (inFlight.Count == 0 && rollout is null) { pendingFinish = false; Finish(); }
        return Done;
    }

    /// <summary>
    /// Hand a private fork to a bounded BelowNormal worker. A full queue never
    /// blocks the caller or loses the already-opened native branch.
    /// </summary>
    private bool Launch(RolloutRun run, KernelTeamSearch.Action action, double capMs)
    {
        var lane = new Lane { Run = run, Action = action };
        if (!KernelSearchWorkPool.Shared.TryQueue(o.Request, () =>
        {
            using var budgetScope = KernelSearchBudgetScope.Enter(o.Request);
            var watch = Stopwatch.StartNew();
            try
            {
                while (!run.Done)
                {
                    if (StopRequested) { run.FinishAtBudget(); break; }
                    StepRollout(run);
                    if (StopRequested || watch.Elapsed.TotalMilliseconds >= capMs) run.FinishAtBudget();
                }
                lane.ElapsedMs = watch.Elapsed.TotalMilliseconds;
            }
            catch (Exception error)
            {
                lane.Error = error;
            }
            finally { lane.ElapsedMs = watch.Elapsed.TotalMilliseconds; }
        }, out var worker)) return false;
        lane.Worker = worker;
        inFlight.Add(lane);
        return true;
    }

    /// <summary>
    /// A rollout cannot be preempted mid-action, so the budget is only ever checked BETWEEN
    /// them. Legacy compute budgets admit the first candidate; a cancelled or expired
    /// explicit request never starts another action merely to create a fallback.
    /// Same shape as the one-shot version, in the same two places (before a candidate's branch
    /// is opened, and before each of its rollouts).
    ///
    /// <paramref name="watch"/> HAS to be part of this sum. `computeMs` only folds in an Advance
    /// when that Advance RETURNS, so on its own it is stale for the whole of a call — and the
    /// one-shot façade passes one enormous slice, which would have made the budget never trip at
    /// all and quietly changed what every non-sliced caller decides.
    /// </summary>
    private bool BudgetSpent(Stopwatch watch)
        => StopRequested || rollouts > 0 && BudgetElapsed(watch) + o.ReserveMs > budgetMs;

    private enum OpenOutcome { Opened, Exhausted, Deadline }

    /// <summary>
    /// Open the next (candidate, policy) rollout, or say why there is none. Doing nothing means
    /// the caller finishes the run — it is NOT done here because on the parallel path there may
    /// still be lanes in flight whose results have to be collected first.
    /// </summary>
    private OpenOutcome TryOpenNextRollout(Stopwatch watch)
    {
        while (policyIndex < policies.Length && candidates.Count > 0)
        {
            if (branch is null)
            {
                if (BudgetSpent(watch)) { stop = "deadline"; return OpenOutcome.Deadline; }
                var candidate = candidates[candidateIndex];
                if (candidate.EndTurn)
                {
                    var forked = root.Fork();
                    if (!forked.EndTurn(candidate.Player, rolloutOptions.MaxRounds, out _, out var resolvedEnd))
                    {
                        NextCandidate();
                        continue;
                    }
                    branch = forked;
                    currentAction = resolvedEnd;
                }
                else if (candidate.Potion is { } potion)
                {
                    var forked = root.Fork();
                    var opened = KernelSession.RequiresPotionChoiceContract(potion)
                        ? forked.ReplayPlanned(candidate, rolloutOptions.MaxRounds, out _)
                        : forked.UsePotion(potion, candidate.Target, out _);
                    if (!opened) { NextCandidate(); continue; }
                    branch = forked;
                    currentAction = candidate with { TurnStartChoices = forked.LastTurnStartChoices };
                }
                else
                {
                    var opened = root.CardBranches(candidate.Card!, candidate.Target, maximumBranches: 1)
                        .FirstOrDefault(b => b.Boundary.Length == 0);
                if (opened is null) { NextCandidate(); continue; }
                branch = opened.State;
                currentAction = candidate with { Choices = opened.Choices, ChoiceOrdinals = opened.ChoiceOrdinals,
                    TurnStartChoices = opened.TurnStartChoices };
                }
            }
            if (BudgetSpent(watch))
            {
                PreserveTerminalOpening();
                stop = "deadline"; return OpenOutcome.Deadline;
            }
            // The opened branch is already private and NextCandidate releases
            // it. Avoid another full fork. Rotate policies across openings so
            // every first-wave candidate need not pay the expensive Tactical path.
            var policy = policies[(policyIndex + candidateIndex) % policies.Length];
            var cheap = o.UsePolicyPortfolio && policy is (RolloutPolicyKind.Kill or RolloutPolicyKind.KillAdds);
            rollout = new RolloutRun(branch, party, o.ContinuationActors ?? actors,
                rolloutOptions with {
                    // The first wave must compare defence and growth too;
                    // repeating Kill for every opening buys no policy diversity.
                    Policy = policy,
                    InvestmentLookahead = !cheap && rolloutOptions.InvestmentLookahead,
                    // See KernelJointTurnRun: a cheap first wave can still be
                    // the adopted line, so it must not skip ordering fixes.
                    SequenceLookahead = o.EnableJointSearch || rolloutOptions.SequenceLookahead }) { StopRequested = () => StopRequested };
            rolloutStartedMs = BudgetElapsed(watch);
            // Finish a reference first. Once one is available, reserve time for
            // a different opening instead of letting one nested probe own the
            // remainder. Check between native actions in both serial and lanes.
            rolloutCapMs = Math.Max(1, (budgetMs - rolloutStartedMs - o.ReserveMs)
                / (bestRecord is null ? 1 : 2));
            NextCandidate();
            return OpenOutcome.Opened;
        }
        return OpenOutcome.Exhausted;
    }

    private void NextCandidate()
    {
        // Fund one complete continuation per opening before buying a second
        // policy for the same opening. Otherwise the first card consumes the
        // deadline and later investments never receive a comparison.
        candidateIndex++;
        if (candidateIndex >= candidates.Count) { candidateIndex = 0; policyIndex++; }
        branch = null;
    }

    // The opening is already a settled native branch. A deadline cannot erase
    // its genuine ending merely because no continuation/worker was admitted.
    // An unfinished branch is never promoted to completed evidence here.
    private void PreserveTerminalOpening()
    {
        if (branch is not { CanFork: true, HasTerminal: true } settled || currentAction is null) return;
        var terminal = new RolloutRun(settled, party, o.ContinuationActors ?? actors, rolloutOptions);
        terminal.FinishAtBudget();
        Consume(terminal, currentAction);
        branch = null;
    }

    private void Consume(RolloutRun run, KernelTeamSearch.Action action)
    {
        var outcome = run.Result!;
        evaluatedOpenings.Add((action.Player, action.CardStateKey, action.CardStateOccurrence, action.Target, action.Potion, action.EndTurn));
        rollouts++;
        // Rollouts do not drink in their suffix; the opening action is the
        // consumed bottle and must be charged even if its effect later expires.
        var record = outcome.Record with { ResourcesConsumed = action.Potion is null ? 0 : 1 };
        considered.Add(record);
        if (outcome.Cutoff) cutoffs++;
        // A scheduler interruption supplies no new evidence about the ending.
        // Keep a completed reference; boundary/round-cap semantics stay intact.
        if (outcome.StopReason == "continuation-budget" && bestRecord is not null) return;
        if (bestRecord is null || bestRecord.CutoffReason == "continuation-budget"
            && outcome.StopReason != "continuation-budget"
            || o.Compare(record, action, bestRecord, bestAction) > 0)
        {
            jointSelected = false;
            bestRecord = record;
            bestAction = action;
            // The rollout starts AFTER the opening action. The live executor emits
            // that opening now and resumes the cached script at index 1. Include it
            // here, or the executor silently skips the first action of the suffix.
            bestScript = new[] { action }.Concat(outcome.Actions).ToArray();
            bestScriptAliveStates = new[]
            {
                party.Where(p => root.Hp(p.Creature) > 0).Select(p => p.NetId).OrderBy(id => id).ToArray(),
            }.Concat(outcome.AliveStates).ToArray();
            bestScriptEnemyStates = new[] { root.Enemies.ToArray() }.Concat(outcome.EnemyStates).ToArray();
            bestScriptCheckpoints = outcome.Checkpoints is { } checkpoints
                ? new[] { root.ActionCheckpoint() }.Concat(checkpoints).ToArray() : null;
        }
    }

    private void PreserveInterruptedPolicyPrefix(RolloutRun run, RolloutPolicyKind policy)
    {
        // These runs belong to this tournament's own root.Fork() factories.
        // The checkpoint checks the first action boundary; factory ownership
        // binds its full root state. CanFork excludes unresolved native choices.
        // No new action or replay is started after the deadline to fabricate it.
        if (run.Result is not { Terminal: false, StopReason: "continuation-budget" } outcome
            || !run.State.CanFork
            || outcome.Actions.FirstOrDefault() is not { Potion: null } first
            || first.EndTurn && !o.IncludeEndTurns
            || outcome.AliveStates.Count != outcome.Actions.Count
            || outcome.EnemyStates.Count != outcome.Actions.Count
            || outcome.Checkpoints is not { Count: > 0 } checkpoints
            || checkpoints.Count != outcome.Actions.Count
            || !checkpoints[0].SameBoundary(root.ActionCheckpoint())) return;
        if (interruptedPolicyPrefix is { Record: { } prior }
            && o.Compare(outcome.Record, first, prior, interruptedPolicyPrefix.FirstAction) <= 0) return;
        interruptedPolicyPrefixKind = policy;
        interruptedPolicyPrefix = new(first, outcome.Record, [], 0, 1, "settled-policy-prefix")
        {
            RootGeneration = RootGeneration, RequestProvenance = o.Request,
            Script = outcome.Actions.ToArray(), ScriptAliveStates = outcome.AliveStates.ToArray(),
            ScriptEnemyStates = outcome.EnemyStates.ToArray(), ScriptCheckpoints = checkpoints.ToArray(),
        };
    }

    private void Finish()
    {
        if (Done) return;
        Result = IsCancelled
            ? new TournamentResult(null, null, considered.ToArray(), rollouts, cutoffs, RequestStop) {
                RootGeneration = RootGeneration,
                RequestProvenance = o.Request,
            }
            : bestAction is null && interruptedPolicyPrefix is { } prefix
            ? prefix with { Considered = considered.ToArray(), Rollouts = rollouts, Cutoffs = cutoffs,
                StopReason = StopRequested ? RequestStop : stop }
            : bestAction is null
            ? new TournamentResult(null, null, considered, rollouts, cutoffs, StopRequested ? RequestStop : "no-playable-first-action") {
                RootGeneration = RootGeneration,
                RequestProvenance = o.Request,
            }
            : new TournamentResult(bestAction, bestRecord, considered, rollouts, cutoffs, stop)
            {
                Script = bestScript,
                RootGeneration = RootGeneration,
                RequestProvenance = o.Request,
                ScriptAliveStates = bestScriptAliveStates,
                ScriptCheckpoints = bestScriptCheckpoints, ScriptEnemyStates = bestScriptEnemyStates,
            };
        Result = AttachDiagnostics(Result);
    }

    private TournamentResult AttachDiagnostics(TournamentResult result)
    {
        // Failed searches need the same evidence as successful ones. Retain
        // stop/paused-reference facts without inventing a deployable action.
        result = result with {
            JointNodes = joint?.Nodes ?? 0,
            JointPrefixes = joint?.Prefixes ?? 0,
            JointMaxActions = joint?.MaxPrefixActions ?? 0,
            JointSelected = jointSelected,
            JointDiagnostics = (joint?.Diagnostics ?? "") + $" baseline={referenceStop} "
                + $"baselineSteps={referenceSteps?.ToString() ?? "unavailable"} policyReferences=[{string.Join(',', policyReferenceStops)}] "
                + $"pausedReferences={pausedPolicyReferences.Count} resumedPolicySteps={resumedPolicyReferences} "
                + $"settledPrefixPolicy={interruptedPolicyPrefixKind?.ToString() ?? "none"} settledPrefixSteps={interruptedPolicyPrefix?.Script?.Count ?? 0} "
                + $"openings={EvaluatedOpenings} candidates={candidates.Count} repairs={potionReferences?.RepairedActions} resumes={potionReferences?.ResumedScripts} formal={potionReferences?.FormalActions} {potionReferences?.RepairDiagnostics}"
                + $" rolloutSteps={RolloutStepCount} rolloutStepMaxMs={RolloutStepMaxMs:F1}"
                + $" rolloutStepDeadlineCrossings={RolloutStepsCrossingDeadline} rolloutStepDeadlineTailMs={RolloutStepDeadlineTailMs:F1}"
                + $" cpu={Environment.ProcessorCount} configuredDop={o.MaxDegreeOfParallelism} effectiveDop={dop}",
        };
        if (o.Request is { } request)
            result = result with { JointDiagnostics = result.JointDiagnostics
                + $" request={request.RootGeneration} phaseWallMs={PhaseWallMs:F1} requestWallMs={request.ElapsedMs:F1}"
                + $" remainingMs={request.RemainingMs} requestStop={request.StopReason}"
                + $" activeJobs={request.ActiveJobs} queuedJobs={request.QueuedJobs} peakJobs={request.PeakActiveJobs}"
                + $" requestCap={request.MaxConcurrency}" };
        return result;
    }

    // Only accepted, completed continuations are published. A caller stopping
    // between frames cannot accidentally adopt a half-played rollout.
    public TournamentResult? BestCompleted => bestAction is null || bestRecord is null ? null
        : new TournamentResult(bestAction, bestRecord, considered.ToArray(), rollouts, cutoffs, "completed-so-far") {
            RootGeneration = RootGeneration,
            RequestProvenance = o.Request,
            Script = bestScript, ScriptAliveStates = bestScriptAliveStates, ScriptCheckpoints = bestScriptCheckpoints, ScriptEnemyStates = bestScriptEnemyStates,
        };

    /// <summary>One rollout plus the worker running it.</summary>
    private sealed class Lane
    {
        public required RolloutRun Run { get; init; }
        public required KernelTeamSearch.Action Action { get; init; }
        public KernelSearchWorkPool.WorkItem? Worker { get; set; }
        /// <summary>Written by the worker before <see cref="Finished"/>; read by the caller after.</summary>
        public Exception? Error { get; set; }
        /// <summary>Volatile write publishes <see cref="ElapsedMs"/> and <see cref="Error"/>.</summary>
        public bool Finished => Worker?.Finished == true;
        public double ElapsedMs;
    }
}

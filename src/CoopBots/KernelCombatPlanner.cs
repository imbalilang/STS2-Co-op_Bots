using System.Diagnostics;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots;

// Host game-thread scheduler. No live objects are read by a background worker.
// Only one first action is returned; all subsequent actions are replanned.
internal sealed class KernelCombatPlanner
{
    internal enum Status { Pending, Ready, Fallback }
    /// <summary>
    /// Why a completed search recommended nothing. The reviewed final boss had 40
    /// of 61 plays come from the legacy planner, and the log could not say whether
    /// the kernel had run out of cards, run out of better ideas, or been stopped
    /// by a boundary — three completely different problems.
    /// </summary>
    internal enum NoActionKind
    {
        None,
        /// <summary>No playable card and no usable potion on the live board.</summary>
        Idle,
        /// <summary>Cards were simulated and none beat doing nothing.</summary>
        Tie,
        /// <summary>The search was cut short by an unmodeled mechanic.</summary>
        Boundary,
    }
    /// <summary>The classification of the last no-action verdict, for the play log.</summary>
    internal NoActionKind LastNoAction { get; private set; }
    // True while a search is still expanding. The runtime must keep feeding it
    // frames rather than treating the inter-action interval as a reason to wait.
    internal bool IsSearching => search is not null;
    // True while a committed script still has an unemitted step: a card, a potion, or
    // one seat's EndTurn. The runtime's idle shortcut asks a narrower question — "does
    // any eligible bot hold a playable card or a usable potion" — and the answer is NO
    // at exactly the moment a script's next step is its own EndTurn, because ending the
    // turn is what a team does when it has run out of cards. Without this, the shortcut
    // skips the poll, the plan's EndTurn is never emitted, and the cursor never consumes
    // the boundary entry that turn was supposed to have produced.
    internal bool HasPendingStep => ContinuationSource is { } script
        && script.Index < script.Result.Actions.Count;
    // Invariant probes for the kernel regression. "A script exists" and "a script was
    // explicitly given up" are the two facts no live log could state directly: three
    // separate Reset() call sites in the runtime destroyed the script silently, and the
    // only trace was noScript climbing while planRefusals stayed 0.
    internal bool HasContinuationForTesting => ContinuationSource is not null;
    internal bool OpeningSearchForTesting => openingSearch;
    internal int ContinuationDropsForTesting => continuationDrops;
    internal void DropContinuationForTesting(string reason) => DropContinuation(reason);
    // How long the in-flight search has been running, and the wall-clock budget it was
    // given. The panel shows both: an all-bot opening may legitimately think for five
    // minutes, and a status line that says only "searching" is indistinguishable from
    // one that is stuck. Zero when nothing is in flight.
    // The in-combat panel clock. During the opening LADDER it reports the WHOLE episode
    // against the ten-minute ceiling, not the current attempt: "33.0s / 1:00" read as if
    // the search were nearly finished, when what the player is actually waiting out is
    // the cap. A repair search mid-fight is NOT part of that ladder — it has no episode
    // clock — so it keeps showing its own per-attempt budget.
    internal long SearchingElapsedMs => !IsSearching
        ? 0
        : openingSearch && openingEpisodeStartMs != 0
            ? Environment.TickCount64 - openingEpisodeStartMs
            : Environment.TickCount64 - planningStartMs;
    internal int SearchingWallBudgetMs => !IsSearching
        ? 0
        : openingSearch && openingEpisodeStartMs != 0
            ? OpeningSearchHardCapMs
            : wallBudgetMs;
    // High-water mark for the panel's round counter. The portfolio runs several
    // KernelTeamSearch members in succession and each one builds its own MaxRoundsReached,
    // so reading the live instance made the displayed depth jump BACKWARDS mid-search —
    // observed in game as 13 -> 1 when the portfolio swapped members. This keeps the
    // deepest round reached by any member of the CURRENT search session.
    private int searchingRoundsHighWater;

    /// <summary>
    /// Deepest fight round the current search session has simulated, for the panel.
    ///
    /// Deliberately monotonic within a session. The portfolio's member swap and the
    /// ladder's per-attempt restart both construct a fresh KernelTeamSearch, so reporting
    /// the live instance alone reset the count partway through what the player sees as one
    /// continuous "searching for the optimal line".
    /// </summary>
    internal int SearchingRoundsReached
    {
        get
        {
            if (!IsSearching) return 0;
            if (search is { } inFlight)
                searchingRoundsHighWater = Math.Max(searchingRoundsHighWater, inFlight.MaxRoundsReached);
            return searchingRoundsHighWater;
        }
    }
    /// <summary>
    /// A potion the plan wants used, on whom, and why.
    /// </summary>
    /// <remarks>
    /// There is deliberately NO choice field. A CHOOSER potion (Attack/Skill/Power/Colorless:
    /// roll three cards, the player picks one, it is added to hand) is only HALF modelled — see
    /// `CardGenerationPotionMirrors`, whose result says `AddsToHand: false` and whose docstring
    /// says the generated cards are produced "without … applying combat piles and hooks".
    /// `KernelTeamSearch` also builds the potion action with no `Choices`, so the search plans
    /// the rest of the fight as if the bottle added NOTHING while the live game adds a card.
    /// Measured live 2026-09-20 (`SLIMES_WEAK`): live hand +1 (`BLOODLETTING`), the plan's step 4
    /// never replayed, eleven `plan step drift` lines that were all that one displacement, and
    /// `plan step refused: card-left-hand` at step 12. A choice field here would only ever read
    /// null until the capture side exists, so it is not carried — see §7 of AGENT.md.
    /// </remarks>
    internal sealed record PotionPlan(PotionModel Potion, Creature? Target, string Reason);
    internal PotionPlan? ConfirmedPotion { get; private set; }
    internal Player? ConfirmedEndTurn { get; private set; }
    private Player[] actorList = [];
    private KernelTeamSearch? search;
    // The capture the current search started from. Kept so a no-action verdict can
    // be classified against the state the search actually saw.
    private KernelSession? rootSession;
    private KernelCombatEvaluation? evaluation;
    private KernelCombatEvaluation.Metrics? rootMetrics;
    private CombatState? rootCombat;
    private CombatState? disabledCombat;
    private string stamp = "";
    private ulong[] actorIds = [];
    private uint queueVersion;
    private long revision;
    private int round;
    private uint? focus;
    // Bot-visible board fingerprint for the last completed search that found
    // nothing. Ending a human turn changes nothing the team can see, so the
    // verdict still holds and re-searching would only pay for it twice.
    private string noActionStamp = "";
    private long nextLog;
    private double computeMs;
    // Which search-complete report is the real one. See SearchMetricsGate for the
    // 738-lines-for-116-searches measurement that made this necessary.
    private readonly SearchMetricsGate searchMetrics = new();
    private int staleCount;
    // How many captures re-projected the live board and could not reproduce it exactly.
    // This is the L0 sentinel: the single-player solver is allowed to ASSERT this
    // (CombatRootSnapshot.Capture throws), but here it is a counted report, because a
    // kernel branch that cannot mirror one live detail is still a usable search — it
    // just cannot promise the script replays. A non-zero count is the upstream cause of
    // the drift that otherwise surfaces much later as an unexplained state mismatch.
    private int projectionMismatches;
    // How many kept plans were dropped because a step had already landed somewhere the
    // search did not predict. This is the L1 sentinel firing: the count of times the old
    // code would have kept playing a script against the wrong board until the next turn
    // boundary — every step in between having been chosen for a board that never existed.
    private int planDriftDetected;
    private int planSentinelMissing;
    private int planSentinelPending;
    // Consecutive ticks a submitted step has failed to land. Upstream awaits the action's
    // completion task, so it has no equivalent; ours has to bound the wait or a step that
    // never resolves leaves the bots idle forever.
    private const int MaxSentinelPendingTicks = 120;
    private int planSentinelPendingTicks;
    // End-turn risk readings, kept as observations. Upstream has no enforcement of these;
    // see the note at the measurement site.
    private int endTurnRiskDeaths;
    // Crossings whose recorded prediction differed from the live board. Evidence, not a gate.
    private int planBoundaryMismatch;
    // Last heartbeat reading, on the same clock SearchingElapsedMs uses. Deliberately NOT
    // reset per search: the opening ladder's clock is per EPISODE, so a continuation does
    // not re-announce itself as a fresh search.
    private long lastSearchHeartbeatMs;
    private const int SearchHeartbeatMs = 45_000;
    private int endTurnRiskUnsimulated;
    // Read-only probes for the kernel regression, which has to tell "the search found
    // nothing to do" apart from "the search found something and deployment refused it".
    // Those two produce the same Status.Fallback and differ only in which counter moved.
    internal int PlansForTesting => plans;
    internal int PlanRefusalsForTesting => planRefusals;
    internal int FirstActionRefusalsForTesting => fallbackFirstAction;
    internal int ExceptionFallbacksForTesting => fallbackException;
    internal int NoActionFallbacksForTesting => fallbackNoAction;
    internal int StaleFallbacksForTesting => fallbackStale;
    internal int NoScriptTicksForTesting => planTicksWithoutScript;
    // Times the L1 sentinel refused a kept plan because no per-action prediction was
    // available for the last step (the path did not replay). Counted apart from
    // planDriftDetected: "the plan drifted" and "the plan was never verifiable" are
    // different failures and used to look identical in a log.
    internal int PlanSentinelMissingForTesting => planSentinelMissing;
    internal int PlanDriftDetectedForTesting => planDriftDetected;
    // Refusals where the last step simply had not resolved yet — neither drift nor a
    // missing sentinel.
    internal int PlanSentinelPendingForTesting => planSentinelPending;
    /// <summary>How many steps the committed script has; pairs with the seek below.</summary>
    internal int PlanLengthForTesting => ContinuationSource?.Result.Actions.Count ?? 0;
    /// <summary>
    /// Whether the last DEPLOYED script's search reported a route (a line that ends the fight or
    /// reaches the bounded horizon). A seam because `search` is disposed by the time a caller can
    /// ask, and because this flag is read by three places whose disagreement is the bug it guards.
    /// </summary>
    internal bool HasRouteForTesting => lastRouteFound;
    /// <summary>Steps skipped so far because their seat could not act.</summary>
    internal int SeatSkipsForTesting => seatStepsSkipped;
    /// <summary>
    /// How many turn crossings the COMMITTED script carries. Zero means the script never left
    /// round one — i.e. it is a stub, not a horizon line. This is the discriminator the horizon
    /// value needs, because a stub can still be many actions long: without the bonus the search
    /// happily plays every card in hand and then stops, which is both long AND short of the
    /// horizon.
    /// </summary>
    internal int PlanBoundariesForTesting => planBoundaries.Count;
    /// <summary>
    /// Move the script's cursor. Exhaustion is decided by the cursor alone, so putting it
    /// under test this way is exact and does not require replaying a whole fight.
    /// </summary>
    internal void SeekPlanForTesting(int index)
    {
        if (ContinuationSource is { } script) script.Index = index;
    }
    // The rest of the last completed plan, kept so the turn is not re-searched after
    // every single card. CombatSolver does the same through TryCreateContinuation, and
    // this copies its SHAPE as well as its behaviour: upstream keeps the plan on
    // `_combat.ContinuationSource`, in an object of its own, so tearing down a
    // deployment session cannot reach it. See KernelContinuation for the history of why
    // that separation matters here.
    //
    // The storage below is the continuation; the names beneath it are read-only proxies
    // so the existing call sites stay legible. Reset() has no field to clear — that is
    // the whole point, not a side effect.
    private KernelContinuation? ContinuationSource { get; set; }
    private KernelTeamSearch.Result? plan => ContinuationSource?.Result;
    private CombatState? planCombat => ContinuationSource?.Combat;
    private int planRound => ContinuationSource?.Round ?? 0;
    private IReadOnlyList<(int Index, string Text)> planBoundaries => ContinuationSource?.Boundaries ?? [];
    private IReadOnlyList<(string Before, string After)> planActionStates => ContinuationSource?.ActionStates ?? [];
    private int planProjectedDeaths => ContinuationSource?.ProjectedDeaths ?? 0;
    private int planIndex
    {
        get => ContinuationSource?.Index ?? 0;
        // Only a live script advances; with none, there is no cursor to move. Every read
        // site is paired with a `plan is not null` guard.
        set { if (ContinuationSource is { } script) script.Index = value; }
    }
    // The last step actually EXECUTED. See KernelContinuation.LastExecuted: once a step can be
    // skipped, "index - 1" is no longer the step whose prediction the live board should match.
    private int planLastExecuted
    {
        get => ContinuationSource?.LastExecuted ?? -1;
        set { if (ContinuationSource is { } script) script.LastExecuted = value; }
    }
    // Set once a step has been skipped, which makes every later prediction incomparable.
    private bool planSentinelInvalidated
    {
        get => ContinuationSource?.SentinelInvalidated ?? false;
        set { if (ContinuationSource is { } script) script.SentinelInvalidated = value; }
    }
    // True until the first plan of this fight is deployed. Only the opening search gets
    // the long budget below; a search that finds nothing leaves it set, so the fight
    // still gets its one long attempt at being solved up front.
    private bool openingSearch;
    // A plan has been deployed in this combat, so the fight is now being played from a
    // script. From that moment the kernel does NOT search again: strict plan execution,
    // as asked for. When the plan runs out or stops matching, the tick hands the action
    // to the legacy planner instead of re-searching — searching mid-fight is what turned
    // every card into another full-budget search.
    private bool planCommitted;
    // Set at each deployment: did the search behind it report a route? See HasRouteForTesting.
    private bool lastRouteFound;
    private int sliceMs = DefaultSliceMs;
    // X3: the super-brain mode is "the engine scores and the plan must reach the end of
    // the fight". Both halves live behind the same condition, so a table with a human in
    // it keeps the old behaviour entirely.
    private bool superBrain;
    private int plansWithRoute;
    // Steps skipped because their seat could not act (died, or already ended its turn). Counted
    // apart from refusals: nothing about the plan was violated, its seat simply left the fight.
    private int seatStepsSkipped;
    // Two different numbers, because conflating them hid a real failure: how many ticks
    // the script was refused (recoverable — it is retried next tick) and how many ticks
    // there was no script left at all.
    private int planRefusals;
    private int planTicksWithoutScript;
    // How many times a live script was explicitly given up, and by which decision. A drop
    // is a named event now (see DropContinuation) rather than something that happened to
    // the plan while nobody was looking.
    private int continuationDrops;
    // The opening episode: how many attempts it has taken, when it started, and how
    // many times it ran out of its hard cap without ever finding a terminal line.
    // The cap is wall clock rather than a retry count because the question the user
    // asked is "how long may this think", not "how many times may it try".
    private int openingAttempts;
    private long openingEpisodeStartMs;
    private int openingNoRouteAtCap;
    private int openingExtensionMs = OpeningSearchExtensionMs;

    // Beam-width portfolio, ported from CombatSolver's BeamWidthPortfolio: run several
    // widths in sequence over ONE shared node budget and keep the best, instead of
    // betting the whole budget on a single width. Members are the baseline first, then
    // a narrower and a wider refinement (ratios 2/3 and 3/2 — the same two the upstream
    // portfolio uses). A member that stopped at the node limit did not finish, so its
    // score is not comparable and it is dropped rather than compared; the first member
    // to finish stands until another strictly beats it, so ties keep the baseline.
    private const double NarrowRefinementRatio = 2.0 / 3.0;
    private const double WideRefinementRatio = 3.0 / 2.0;
    private int[] portfolioWidths = [];
    private int portfolioIndex;
    private int portfolioNodesLeft;
    private KernelTeamSearch? portfolioWinner;
    private double portfolioBestScore = double.NegativeInfinity;
    // Held as fields because every portfolio member after the first is created long
    // after Poll's locals for depth and rounds have gone out of scope.
    private int searchDepth;
    private int searchRounds;
    // planProjectedDeaths lives on KernelContinuation (see its doc): it is a property of
    // the script, not of the search that produced it.

    /// <summary>The widths to try, baseline first, deduped and never below 1.</summary>
    private static int[] PortfolioWidths(int baseline)
    {
        var widths = new List<int>();
        void Add(int candidate)
        {
            if (candidate < 1 || widths.Contains(candidate)) return;
            widths.Add(candidate);
        }
        Add(baseline);
        Add((int)Math.Round(baseline * NarrowRefinementRatio, MidpointRounding.AwayFromZero));
        Add((int)Math.Round(baseline * WideRefinementRatio, MidpointRounding.AwayFromZero));
        return [.. widths];
    }

    // The opening search of a fight, when every seat is driven.
    //
    // That search's plan is the one the rest of the fight may replay — no human action
    // can invalidate it, and it is carried across turn boundaries against the boundary
    // stamp — so a long first search pays for itself in a way the same minute spent
    // mid-fight would not. Every other search keeps the old per-tier budget.
    // The first attempt gets five minutes. Asking for a line that ENDS the fight is a
    // different question from asking for the best line within a budget, and the budget
    // was the thing that decided the answer: at 60 s the opening search stopped with
    // `route=false` and the rest of the fight was played to wherever depth ran out.
    // USER SPEC 2026-09-20: with the horizon bounded to LookaheadRounds, the budget only has to
    // buy a five-round line rather than a whole-fight solution, so the ladder shrank from
    // 5min + 5x1min (10min cap) to 3min + 5x1min (8min cap).
    private const int OpeningSearchBudgetMs = 180_000;
    // Each further attempt adds one minute, and the whole episode stops at ten. The
    // ladder is 5 + 1 + 1 + 1 + 1 + 1 = 10 minutes: if the fight has not been solved by
    // then the search reports an error and the best partial line is played — which is
    // the honest outcome, not a silent one.
    private const int OpeningSearchExtensionMs = 60_000;
    private const int OpeningSearchHardCapMs = 480_000;
    // The ladder is five one-minute extensions on top of the five-minute opening search.
    //
    // Bounded by COUNT, not only by the ten-minute clock. A search that stops early
    // (node-budget, depth-cap) buys its next minute sooner, so a clock-only gate let one
    // fight run "20 attempt(s)" inside the same ceiling — each attempt re-walked a shape
    // already known to close. The clock still caps the total; this caps the retries.
    /// <summary>
    /// BOUNDED LOOKAHEAD: how many rounds one search plans before the script is deployed and
    /// played out. See the user spec of 2026-09-20 — a run is a sequence of consecutive scripts.
    /// </summary>
    /// <remarks>
    /// With the horizon finite a search cannot reach a terminal, so three things had to move
    /// together or the first attempt (MaxRounds=3, same day) made the mod WORSE — it planned a
    /// two-action stub and then disabled the kernel for the whole fight:
    /// <list type="number">
    /// <item><c>KernelTeamSearch.Score</c> pays <c>HorizonBonus</c> for reaching the horizon,
    /// otherwise the deepest line is worth nothing and a stub outscores it;</item>
    /// <item><c>HasRoute</c> counts a horizon-reaching line as success, because deployment, the
    /// opening ladder and the ladder's cap branch all read it — a false there meant the ladder
    /// fired on every fight and its cap branch disabled the kernel;</item>
    /// <item><c>CompleteContinuation</c> clears <c>planCommitted</c> so finishing a script starts
    /// the next one instead of handing the rest of the fight to the legacy planner.</item>
    /// </list>
    /// 5 rounds is the user's number: enough to cover a full enemy phase plus the player turns
    /// around it, without the node growth that made the whole-fight search unaffordable.
    /// </remarks>
    private const int LookaheadRounds = 5;
    private const int OpeningSearchMaxExtensions = 5;

    // The node ceiling has to move with the clock. Measured in this project's own logs
    // the search spends roughly 1.5 ms per node (about 300 nodes per 450 ms run), so the
    // default 768/1152 ceiling would stop at node-budget within a second and the minute
    // would buy nothing at all. 200k nodes is five minutes at that rate: the clock and
    // the node cap should run out together, so neither one hides the other's limit.
    // Scaled with the clock: the old 200k was sized as "five minutes at ~1.5ms/node", so a
    // three-minute base gets 120k. The clock and the node cap are supposed to run out together,
    // otherwise one hides the other's limit.
    private const int OpeningSearchNodes = 120_000;
    // The search SHAPE has to move too, and it is the part that actually binds. Measured
    // on a live full-bot run: every opening search stopped at `depth-or-exhausted` after
    // ~1300 nodes, not at time-budget — with depth 9 and width 8 there is no larger tree
    // to spend a minute on, and every plan came back exactly `sequence:9`, i.e. the plan
    // length was capped by the depth, not by the clock.
    //
    // THE DEPTH IS AN ACTION BOUND, and the number here used to be a turn bound. CombatSolver
    // caps its beam with TWO separate limits — `MaxActions = 128` and `MaxTurns = 32`
    // (NoveltySearchOptions.cs:10-11), applied as `parent.ActionCount >= MaxActions ||
    // parent.Turn >= start + MaxTurns` (CombatBeamSolver.NoveltySearch.cs:101). Our
    // `Options.Depth` counts ACTIONS, one per expansion level, so 32 was its turn bound
    // read as an action bound — roughly an order of magnitude short. A 4-seat round is
    // ~16 actions, so 32 could never cover a fight, and the live log said so: the opening
    // search stopped at 10_284 of its 40_000 nodes with `route=false` and a plan whose
    // first turn boundary was action 10 of 26.
    //
    // 512, measured rather than inherited. With the round wall gone (see MaxRounds) the
    // live search reached `stop=depth-cap` at 69_492 nodes over 128 levels — ~543 nodes
    // per level — so the depth stopped it at a third of the 200_000 node ceiling. That
    // is the "the CPU looks idle while it searches" report: the search FINISHED, it was
    // not throttled. At 543 nodes/level the node ceiling needs ~368 levels; 512 puts the
    // budget back in charge, and 200k nodes at the measured 0.65 ms/node is ~130 s of
    // compute, which the frame-sliced clock turns into ~290 s of the 300 s budget. The
    // clock, the node cap and the depth now run out together instead of the depth first.
    //
    // SUPERSEDED 2026-09-20. The reasoning above tuned a knob that upstream does not have:
    // SolverSearchProfile is (BeamWidth, MaxExpandedNodes, MaxCardBranchesPerNode,
    // MaxPileChoiceBranchesPerAction, MaxHandChoiceBranchesPerAction,
    // SoftTimeBudgetMilliseconds) — there is no Depth. A search there is bounded by nodes,
    // by the clock, and by the frontier emptying.
    //
    // Keeping a tuned depth was the mistake. Every attempt to make it "run out together"
    // with the other budgets only moved which one bound first, and the live log shows it:
    // the 2026-09-20 SLIMES opening search stopped with `stop=depth-cap` at 155_960 of its
    // 200_000 nodes — cut off by our own knob with 44_000 nodes of budget unspent. The
    // doubling ladder (512 -> 2048) was an invented axis on top of an invented knob.
    //
    // The value is deliberately far past anything the node cap can reach, so the frontier
    // and the real budgets decide — upstream's model. The per-attempt extension still
    // exists because it is a user-specified policy (5 minutes, then up to five 1-minute
    // extensions, then error), but it no longer has a depth axis to double.
    private const int OpeningSearchDepth = int.MaxValue;
    // Upstream's `BeamWidth = 45` (CombatSearchCoordinator.cs:244). The shapes are not
    // identical — its beam keeps routes under a Pareto retention policy while ours keeps
    // `width` frontier nodes per depth — so it is an evidence-based starting point, not
    // a proven equivalence.
    private const int OpeningSearchWidth = 45;
    // Per-frame CPU slice. The default is 4 ms, which is the real ceiling on how much
    // the opening budget can buy: the search advances once per frame, so 4 ms framing
    // spends about 3.6 CPU-seconds inside a 60-second wall window and the rest of the
    // minute is idle. The opening search widens the slice so the budget is actually
    // spent — it is the one search where a frame hitch is expected and paid for.
    private const int DefaultSliceMs = 4;
    // Measured on the live machine (16 hardware threads, 60 fps target): the search is
    // single-threaded, so 8 ms per frame is ~0.48 of one core — about 3% of the machine,
    // which is what "the CPU looks idle while it searches" actually is. A frame is
    // 16.7 ms, so the slice is the whole knob: 14 ms leaves the game ~2.7 ms and the
    // search gets ~0.84 of a core, nearly double the compute per wall second. The wall
    // budget is what binds either way (compute accrues at the slice fraction of it), so
    // this is the difference between spending 5 minutes and spending ~2.5 of them.
    //
    // The ceiling is one core: KernelTeamSearch pins itself to its capturing thread
    // (`Advance` throws off-thread), so no slice value can make this look busy on a
    // 16-thread CPU. Using the rest would mean parallelising the expansion, which the
    // vendored engine has (`MaxDegreeOfParallelism`, ParallelExpansionExecutor) but our
    // joint multi-seat search never wired up.
    private const int OpeningSliceMs = 14;
    private int timeBudgetMs = 200;
    private int wallBudgetMs = 300;
    private long planningStartMs;
    // Samples for the per-search GC reading (see ReportSearch).
    private TimeSpan gcPauseAtStart;
    private int gen0AtStart;
    private long allocatedAtStart;
    // Telemetry: why the kernel handed a tick to the legacy planner, and how
    // often it actually produced a plan. Logged once per combat.
    private int plans;
    private int fallbackBoundary;
    private int fallbackNoAction;
    private int fallbackStale;
    // Split of the stale counter: a search discarded because the world really
    // moved (wasted but necessary) versus one discarded by an unrelated combat
    // notification that changed nothing the branch depended on. Notification
    // churn no longer cancels an in-flight search, so this second bucket should
    // now be near zero; a non-zero value means a real board change arrived.
    private int fallbackStaleRoot;
    private int fallbackStaleNoise;
    private int fallbackException;
    // A completed search whose FIRST action is no longer submittable. This is not
    // staleness — the live stamp matched — so it has its own counter and its own
    // per-reason breakdown. It is the number that decides whether "search once,
    // then play the plan" holds: each one is a search paid for and thrown away,
    // followed by a legacy play, which is what a per-card search loop looks like.
    private int fallbackFirstAction;
    private readonly Dictionary<string, int> firstActionKinds = new(StringComparer.Ordinal);
    // The engine probes (boundary text agreement, 0.41 reachability) are diagnostics
    // about the engine, not about this board, so one answer per combat is enough.
    private bool probesDone;
    private bool shadowDone;
    /// <summary>
    /// The shadow tournament's verdict for this combat's FIRST decision, held until the REAL
    /// first action is known so both can be printed on ONE line.
    ///
    /// That join is the point. The first version logged the tournament's choice on its own at
    /// search time, which answers "what would it play" but not the question the plan asks —
    /// "is it choosing BETTER than the search". Two lines in different places cannot be compared
    /// without hand-correlating them, and the live batches produced exactly that: five
    /// tournament choices and no reading of whether any of them beat what was played.
    /// </summary>
    private string? shadowChoice;
    /// <summary>
    /// Which combat <see cref="shadowChoice"/> belongs to. The tournament runs ONCE per combat at
    /// its first decision, but the line is printed later, from the deployment report — and if
    /// that combat never deploys, a stale choice would be printed against the NEXT fight's
    /// action. Measured 2026-09-21: one batch logged 21 shadow lines against 10 fights, which is
    /// more lines than fights, so at least some pairings were wrong. A comparison printed beside
    /// the wrong action is worse than no comparison.
    /// </summary>
    private CombatState? shadowCombat;
    /// <summary>Decisions this combat that came from the tournament rather than the search.</summary>
    private int tournamentDriven;
    /// <summary>
    /// Fresh tournament passes run this combat (one per no-potion/potion pass). The count that
    /// must stay near the number of DECISIONS, not the number of stored script STEPS: the
    /// 2026-09-23 mixed table re-searched every step (553 stored steps, one decision each) and
    /// the main thread paid ~6 ms every frame for the rest of the fight. Logged so the next
    /// live read can tell "the script is being followed" from "the script is being re-derived".
    /// </summary>
    private int tournamentPasses;
    /// <summary>
    /// SHADOW TOURNAMENT — OFF BY DEFAULT, and it stays off until a live run has been read.
    ///
    /// When on, the first decision of each combat also runs <see cref="KernelTournament"/> on a
    /// FORK of the same root and logs what it would have chosen. It never submits anything: the
    /// action played is still whatever the existing planner decided. That is the whole point —
    /// the plan's §20 release order is 离线回放 → 影子 → 全 Bot → 人机, and this is the second
    /// step. One live run with this on answers "is the tournament choosing better than the
    /// search?" without risking a single wrong card in a real game.
    ///
    /// COST: measured on the development machine, one tournament at TopK=4 is ~283 ms and
    /// ~29 MB allocated. That is why it runs ONCE per combat and not once per decision —
    /// per-decision it would double a 1.5 s budget on its own.
    ///
    /// VALIDATED ONCE, 2026-09-21 (batch live-solver-2): it ran in a real game at every fight's
    /// opening position and its prediction matched the outcome in all 5 fights — four VICTORY
    /// calls on fights won, and `hp=0/0/0/0` on the act-1 boss that actually ended the run.
    /// Cost 43-393 ms once per combat.
    ///
    /// Back OFF after that run, on purpose. §20's order puts shadow BEFORE full-bot, not before
    /// being read once; a build that leaves this machine should not carry an always-on extra
    /// 400 ms per combat that only produces log lines. Flip to true to collect more.
    ///
    /// SHIPPED OFF in 0.37.0 (2026-09-21). It had been flipped back on to collect the A/B
    /// batches (ab-1..ab-5), which is why it read `true` in the tree; a released build is
    /// exactly the case this comment was written for. Nothing else changed with it: the
    /// shadow never submits a decision, so turning it off removes one tournament per combat
    /// and its log line, and no game behaviour.
    /// </summary>
    private const bool ShadowTournamentEnabled = false;
    /// <summary>
    /// WHICH POLICY DECIDES A COMBAT — the roll-out tournament (C) or the segmented search (B).
    /// Null = the shipped default.
    ///
    /// DEFAULT: THE TOURNAMENT DECIDES EVERY TABLE WITH AT LEAST ONE DRIVEN SEAT. It replaced
    /// the all-bot segmented search because B answers by searching for three minutes — up to
    /// eight with the retry ladder — BEFORE the first card, and C replans from the live board on
    /// every action. A mixed table now uses the same path: the non-driven human seats are
    /// simulated as "end their turn" rather than fed invented actions (see KernelRollout).
    ///
    /// false: force the segmented search. The kernel suite's planner fixtures set this — they
    /// guard the segmented search and must keep exercising it (the ~18 assertions that went red
    /// the one time this was a compile-time constant are that guard).
    /// true: force the tournament, including on a mixed table.
    /// </summary>
    internal static bool? TournamentOverride { get; set; }

    /// <summary>Is every seat in this combat answered by the bot? The gate the whole
    /// "the kernel is authoritative" family shares — the deep opening budget, the engine
    /// scorer, and the probes. It deliberately does NOT gate the tournament: a mixed table
    /// runs the tournament with only its driven seats as actors.</summary>
    internal static bool AllSeatsDriven(CombatState combat)
        => combat.Players.All(player => AutoPilot.Drives(player.NetId));

    /// <summary>Does the roll-out tournament decide THIS combat? It runs whenever there is at
    /// least one driven seat, i.e. all-bot and mixed tables both; a handed-over human seat is an
    /// actor like a synthetic bot.</summary>
    internal static bool TournamentDrives(CombatState combat, IReadOnlyList<Player> actors)
        => actors.Count > 0 && TournamentOverride != false;

    /// <summary>May the tournament put “end this seat's turn” into its candidate list?</summary>
    internal static bool TournamentCanEndTurn(CombatState combat, bool humansFinished)
        => humansFinished || AllSeatsDriven(combat);
    /// <summary>
    /// The decision point the tournament has already been asked about AND declined.
    ///
    /// WITHOUT THIS IT RUNS ON EVERY TICK. `Poll` is called frame after frame; when the
    /// tournament finds no action it returns false, the caller falls through to the ordinary
    /// path, that path returns Pending, and the next tick asks the tournament again — a full
    /// tournament per frame. Measured live 2026-09-21: 8142 potion-pass lines in one run, every
    /// one of them a wasted tournament, on a machine where the whole point of the budget work
    /// was to stop burning CPU. A declined decision point is remembered until the board moves.
    /// </summary>
    private string? tournamentDeclined;

    // THE TOURNAMENT'S OWN SCRIPT.
    // UPSTREAM: CombatSolver keeps the finished search as `_combat.ContinuationSource` and
    // deploys its line (TryCreateContinuation / DeployCurrentTurn), so the segmented path here
    // already had a plan to keep (see KernelContinuation).
    // OURS: the tournament path had no equivalent — it took the first action of the winning
    // roll-out and threw the rest away, then re-searched every card. The measured final-boss
    // failure was: card one found VICTORY (hp 0/0/0/28), the next eighty decisions found WIPE
    // hp=0/0/0/0, and the party lost the line it had already found.
    // WHY WE DEVIATE: user request 2026-09-23 — cache the winning script and keep stepping it
    // while no fresh candidate's ending beats the cached one. Index 0 is the action already
    // submitted, so the cursor starts at 1.
    private IReadOnlyList<KernelTeamSearch.Action>? tournamentScript;
    private TerminalRecord? tournamentScriptRecord;
    private int tournamentScriptIndex;
    private CombatState? tournamentScriptCombat;
    // Index-aligned with the cached script: the living seats the roll-out itself had immediately
    // BEFORE each action. A script may predict a death; the replayer may continue only while the
    // live living-seat set matches that step's prediction. Measured live 2026-09-23: after the
    // host died earlier than the script's line, the old code silently skipped the dead seat's
    // steps and kept using a VICTORY record priced before the death, overriding fresh WIPEs.
    private IReadOnlyList<ulong[]>? tournamentScriptAliveStates;

    /// <summary>
    /// How much the tournament is allowed to spend, as a player-facing tier.
    ///
    /// ONE TABLE INSTEAD OF THREE CONSTANTS. The three knobs move together — widening the
    /// candidate list without widening the budget just moves the deadline, and a wider list on
    /// fewer workers makes the decision slower rather than better — so they are chosen from the
    /// tier rather than tuned one at a time.
    ///
    /// WHY THE BUDGET IS IN THE TABLE AND NOT JUST RAISED: measured live 2026-09-21 over 385
    /// decisions at TopK=6, 86 % stopped at `top-k-exhausted` and only 14 % at `deadline`. The
    /// budget was never what ended a decision, so raising it alone would not have bought one
    /// extra rollout. <see cref="TournamentDriveTopK"/> is the strength knob; the budget only
    /// has to be large enough not to clip the list.
    /// </summary>
    internal enum TournamentPerf
    {
        /// <summary>No extra threads at all: one sliced rollout at a time, ~6 ms per frame.</summary>
        Low,
        Mid,
        /// <summary>
        /// The shipped default, and what `Ultra` was before the ladder was re-cut: TopK 20 x 3
        /// policies = 60 rollouts, every core but two, 3200 ms. Measured on the 2026-09-22 live
        /// run it reached `rollouts=60` at p90 604 ms / max 1015 ms.
        /// </summary>
        High,
        /// <summary>
        /// THE CANDIDATE LIST IS NOT TRUNCATED AT ALL — every legal first action the table has is
        /// rolled out, so the damage-only pre-ranking stops deciding what is even considered.
        ///
        /// That is the only lever left above <see cref="High"/>: 20 was already close to the whole
        /// list (4 seats x ~5-7 cards each), so raising the number again would usually change
        /// nothing, while removing the cap always means "consider everything". Where the list is
        /// shorter than 20 the two tiers coincide — expected, not a bug.
        /// </summary>
        Ultra,
    }

    /// <summary>
    /// The active tier. Set from the live-test sentinel's `perf=` line; everything else gets the
    /// default.
    ///
    /// AS A TIER RATHER THAN A RAW NUMBER ON PURPOSE: whatever we measure here is one machine's
    /// number, and the plan is to ship these as low/mid/high choices the player makes. The
    /// values below are estimates from a single 8-physical/16-logical box, and the ones that
    /// scale with hardware are expressed as fractions of the core count so a 4-core laptop does
    /// not get handed a 16-way pool.
    /// </summary>
    internal static TournamentPerf Perf { get; set; } = TournamentPerf.High;

    /// <summary>Parse a `perf=` sentinel value. An unknown value keeps the current tier and says so.</summary>
    internal static void ApplyPerf(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (Enum.TryParse<TournamentPerf>(name.Trim(), ignoreCase: true, out var tier)) Perf = tier;
        else Log.Warn($"CoopBots: unknown perf tier '{name}'; keeping {Perf}.");
    }

    /// <summary>
    /// How many first actions the tournament rolls out, per pass. THIS, not the budget, is the
    /// strength knob. TopK x 3 policies is the rollout count, so 20 is already close to "every
    /// legal first action this table has" and `Ultra` removes the cap rather than raising it.
    /// </summary>
    private static int TournamentDriveTopK => Perf switch
    {
        TournamentPerf.Low => 4,              // 12 rollouts
        TournamentPerf.Mid => 8,              // 24
        TournamentPerf.Ultra => int.MaxValue, // no cap: every legal first action
        _ => 20,                              // High: 60 rollouts
    };

    /// <summary>
    /// Compute budget for ONE tournament pass. The caller asks twice (without potions, then —
    /// only if that line does not win — with), so a decision can spend this twice over.
    /// ~30 ms per rollout, plus the 300 ms reserve a rollout is never started inside.
    /// </summary>
    private static int TournamentDriveBudgetMs => Perf switch
    {
        TournamentPerf.Low => 600,    // 12 rollouts is ~360 ms; the deadline never binds
        TournamentPerf.Mid => 1200,   // 24 is ~720 ms
        TournamentPerf.Ultra => 6000, // uncapped candidate list, both passes possible
        _ => 3200,                    // High: 60 is ~1.8 s, and the second pass may follow
    };

    /// <summary>
    /// Rollout workers. <see cref="TournamentPerf.Low"/> deliberately parks on the sliced single
    /// -threaded path: that tier exists to cost the renderer nothing, not to be fast.
    /// </summary>
    private static int TournamentParallelism => Perf switch
    {
        TournamentPerf.Low => 1,
        TournamentPerf.Mid => Math.Clamp(Environment.ProcessorCount / 4, 1, 4),
        // High and Ultra both take everything the machine can spare: the candidate list, not the
        // worker count, is what separates them.
        _ => Math.Clamp(Environment.ProcessorCount - 2, 1, 16),
    };
    private const int ShadowTournamentTopK = 4;
    private const int ShadowTournamentBudgetMs = 1500;
    // Proactive potions the search proposed but that were declined as redundant.
    // Kept in the summary so a tuning change can be judged against real runs.
    private int potionDeclined;
    // Post-mortem context: which encounter this was, how far it got, and the
    // party state, so a wipe can be reviewed from the log alone.
    private string combatLabel = "";
    private int maxRoundSeen;
    /// <summary>
    /// Highest round the live combat has reached in this fight. The summary prints it,
    /// and the liveness scenario asserts it, because it is the only field that says how
    /// long a fight actually ran — see the sampling note in <see cref="Poll"/>.
    /// </summary>
    internal int MaxRoundSeen => maxRoundSeen;
    private string partyState = "";
    // Which unmodeled card/mechanic truncated a search, tallied per combat. This
    // is the data the optimization plan wants model work to be ordered by,
    // instead of guessing which card matters: a boundary that never appears
    // cannot be the reason a fight went wrong.
    private readonly Dictionary<string, int> boundaryKinds = new(StringComparer.Ordinal);
    // Bottles the party held that the kernel cannot simulate. They are refused by
    // every branch, so without this the only trace of a full slot at the wipe is
    // the potion list in the run save; naming them here says *why* they were kept.
    private readonly HashSet<string> unmodeledPotions = new(StringComparer.Ordinal);
    // No-action verdicts by kind, so the summary separates "out of cards" from
    // "nothing better than passing" from "stopped by an unmodeled mechanic".
    private readonly Dictionary<NoActionKind, int> noActionKinds = new();
    private void ObserveCombat(CombatState combat, IReadOnlyList<Player> actors)
    {
        combatLabel = $"{combat.Encounter?.Id.Entry ?? "unknown"} act={combat.RunState.CurrentActIndex + 1}";
        maxRoundSeen = Math.Max(maxRoundSeen, combat.RoundNumber);
        partyState = string.Join(", ", combat.Players.Select(p =>
            $"{p.NetId}:{p.Creature.CurrentHp}/{p.Creature.MaxHp} deck={p.Deck.Cards.Count}"));
        foreach (var player in combat.Players)
            foreach (var potion in player.Potions)
                if (!potion.HasBeenRemovedFromState && !KernelSession.CanModelPotion(potion))
                    unmodeledPotions.Add(potion.Id.Entry);
    }
    /// <summary>
    /// Drops a plan the runtime has not consumed yet. <see cref="Reset"/> keeps it
    /// on purpose — <see cref="Poll"/> stores a plan and only then resets the
    /// search, so clearing it there would destroy the very decision it just made —
    /// which means the one caller that abandons the planner without asking it
    /// again has to say so. A confirmation is only ever valid for the tick that
    /// produced it.
    /// </summary>
    /// <summary>
    /// Adopt a finished search as this fight's script.
    /// </summary>
    /// <remarks>
    /// Upstream's `_combat.ContinuationSource = result`, with the cursor starting past the
    /// action the caller has already submitted. Deliberately the ONLY way a script comes
    /// into existence: it is a single named decision, so "who can create a script" and
    /// "who can destroy one" are both enumerable.
    /// </remarks>
    private void CommitContinuation(KernelTeamSearch.Result result, CombatState combat,
        IReadOnlyList<(int Index, string Text)> boundaries,
        IReadOnlyList<(string Before, string After)> actionStates, int projectedDeaths)
    {
        ContinuationSource = new KernelContinuation
        {
            // Index 1 because the adopting path has already submitted action 0 (see
            // KernelContinuation.Index), and LastExecuted follows it: the sentinel compares the
            // live board against planActionStates[LastExecuted], so "-1" here would stand the
            // sentinel down for the whole first step. An existing assertion caught exactly that
            // ("an unresolved first step must WAIT with the script intact").
            Result = result, Index = 1, LastExecuted = 0, Combat = combat, Round = combat.RoundNumber,
            Boundaries = boundaries, ActionStates = actionStates, ProjectedDeaths = projectedDeaths,
        };
        planCommitted = true;
        planSentinelPendingTicks = 0;
    }

    /// <summary>
    /// Count a plan the kernel actually DEPLOYED, and whether the search behind it had found a
    /// line that ends the fight.
    /// </summary>
    /// <remarks>
    /// The two counters have to move together or `routes=N/M` cannot be read. `plansWithRoute++`
    /// used to sit in the CARD-deployment branch alone, so any plan whose first action was an
    /// end-turn or a potion was counted in `plans` and NOT in `plansWithRoute`. Measured live
    /// 2026-09-20 (round 8, SOUL_FYSH_BOSS): the opening search printed
    /// `action=potion, route=True, actions=30` and the summary printed `routes=0/3` — a route
    /// found and then not counted, which made the solve rate read as 14/19 when it is 19/19.
    /// That misreading cost a round of analysis, so the pairing is structural now.
    ///
    /// <para>
    /// Deliberately NOT used by the end-turn REUSE path: that one re-counts an existing script at
    /// a turn boundary, which is a handout rather than a new deployment, and it has no route of
    /// its own to credit.
    /// </para>
    /// </remarks>
    private void CountDeployedPlan()
    {
        plans++;
        lastRouteFound = search is { HasRoute: true };
        if (lastRouteFound) plansWithRoute++;
    }

    /// <summary>
    /// Give the script up, naming the decision that did it.
    /// </summary>
    /// <remarks>
    /// This is upstream's `_combat.ContinuationSource = null`, which appears 20 times in
    /// SolverController and never as part of a general teardown — always at a named
    /// replan decision. Two rules are load-bearing here:
    ///
    /// <list type="bullet">
    /// <item>The caller must name the reason, because a script that vanishes without a
    /// line is indistinguishable in a log from one that ran out. Both fights analysed on
    /// 2026-09-20 read <c>planRefusals=0</c> while 25 and 22 actions went to the legacy
    /// planner; the three separate silent kill sites that produced that took three
    /// rounds of investigation to find.</item>
    /// <item>Reported unthrottled. The throttled channel swallowed every one of the
    /// refusals in that same investigation, which is what left `planRefusals=24` with no
    /// way to say which guard had fired.</item>
    /// </list>
    /// </remarks>
    private void DropContinuation(string reason)
    {
        if (ContinuationSource is null) return;
        ReportSearchLine($"plan dropped: {reason}; the rest of this combat goes to the legacy planner");
        ContinuationSource = null;
        continuationDrops++;
    }

    /// <summary>
    /// The script played its own last step. This is COMPLETION, not a refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It used to be reported as <c>plan step refused: index=9/9 reason=plan-exhausted</c>
    /// and then handed to <see cref="DropContinuation"/>, which counts it in
    /// <c>planRefusals</c> and prints "the rest of this combat goes to the legacy planner"
    /// as if something had gone wrong. Two consequences, both measured live on 2026-09-20:
    /// </para>
    /// <list type="bullet">
    /// <item>The acceptance metric could not tell "the script was violated" from "the script
    /// finished". A fight that played its whole script and then handed the rest over was
    /// indistinguishable in the summary from a fight whose script was abandoned at step 2.</item>
    /// <item>The STRICT EXECUTION branch below — the one whose counter is written to say how
    /// much of the fight the legacy planner covered — was unreachable, because exhaustion was
    /// intercepted one block earlier. <b>That is why `noScript` read 0 in every summary of
    /// every round while plans were being exhausted</b>, and why it looked like the script
    /// always covered the whole fight.</item>
    /// </list>
    /// <para>
    /// <see cref="DropContinuation"/>'s own remarks name "a script that vanishes without a
    /// line is indistinguishable from one that ran out" as the thing to avoid. This is
    /// "ran out", so it gets its own line and touches neither counter.
    /// </para>
    /// </remarks>
    private void CompleteContinuation(KernelTeamSearch.Result result)
    {
        if (ContinuationSource is null) return;
        // BOUNDED LOOKAHEAD: a finished script is not the end of the kernel's involvement.
        // Clearing planCommitted is what makes the next tick search for the NEXT script;
        // leaving it set is what routed the rest of the fight into the STRICT EXECUTION branch
        // and, with it, the legacy planner. That was correct under "one search, then play the
        // whole fight" and is exactly wrong once the horizon is five rounds: the kernel would
        // plan a fifth of the fight and then stop thinking.
        ReportSearchLine($"plan complete: all {result.Actions.Count} step(s) executed; "
            + "planning the next script from the live board");
        ContinuationSource = null;
        planCommitted = false;
    }

    internal void DiscardConfirmation()
    {
        ConfirmedPotion = null;
        ConfirmedEndTurn = null;
    }

    /// <summary>
    /// Discards everything about the SEARCH in flight — the expander, its root session,
    /// the evaluation and the metrics computed from it. Called from 24 places, most of
    /// them "I am not acting this tick" early returns.
    ///
    /// <para>
    /// It deliberately does NOT touch <see cref="ContinuationSource"/>. This used to be
    /// the function that killed the script, which made every early return in the runtime
    /// a silent plan-drop. The script has its own lifetime and its own explicit
    /// teardown — see <see cref="DropContinuation"/> and <see cref="KernelContinuation"/>.
    /// A reset of working state must never be able to reach it; that is enforced by the
    /// storage, not by remembering.
    /// </para>
    /// </summary>
    internal void Reset(bool newCombat = false)
    {
        search?.Dispose(); search = null; rootSession = null;
        evaluation = null; rootMetrics = null; rootCombat = null;
        if (newCombat)
        {
            disabledCombat = null; staleCount = 0; noActionStamp = "";
            // A sliced tournament belongs to the fight that is over. Dropping it here is what
            // stops a half-finished run from being resumed against a board that no longer exists.
            ClearTournament();
            ClearTournamentScript("new combat");
            openingSearch = true;
            planCommitted = false;
            ContinuationSource = null;
            // openingAttempts / openingNoRouteAtCap are deliberately NOT cleared here:
            // the summary further down describes the fight that just ENDED, and zeroing
            // them first made both fields print 0 for every fight. That is exactly how a
            // real ten-minute extension ladder (1 opening search + 5 extensions + the
            // cap error) came to read as "never triggered" — the evidence was destroyed
            // by the reporting, not absent. They are cleared with the rest of the
            // per-fight counters, AFTER the summary.
            openingExtensionMs = OpeningSearchExtensionMs;
            openingEpisodeStartMs = 0;
            if (plans + fallbackBoundary + fallbackNoAction + fallbackStale + fallbackException
                + potionDeclined + unmodeledPotions.Count > 0)
            {
                // Coverage is the metric the optimization plan is built around:
                // how often a kernel search actually ends in a deployable plan.
                // Boundary is an annotation on a plan, not a separate outcome, so
                // it is excluded from the denominator.
                var resolved = plans + fallbackNoAction + fallbackStale + fallbackFirstAction;
                var coverage = resolved == 0 ? 0 : 100.0 * plans / resolved;
                var topBoundaries = boundaryKinds.Count == 0 ? "none"
                    : string.Join(";", boundaryKinds.OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(3)
                        .Select(pair => $"{pair.Key}x{pair.Value}"));
                var kindSummary = noActionKinds.Count == 0 ? "none"
                    : string.Join(";", noActionKinds.OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key).Select(pair => $"{pair.Key}x{pair.Value}"));
                var refusalSummary = firstActionKinds.Count == 0 ? "none"
                    : string.Join(";", firstActionKinds.OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key).Select(pair => $"{pair.Key}x{pair.Value}"));
                Log.Info($"CoopBots kernel combat summary: encounter={combatLabel}, "
                    + $"tournamentDriven={tournamentDriven}, tournamentPasses={tournamentPasses}, "
                    + $"plans={plans} (coverage={coverage:F0}%), boundary={fallbackBoundary}, no-action={fallbackNoAction}, "
                    // X4 acceptance reading: how many of the deployed plans were routes
                    // that actually reached the end of the fight, and how often the search
                    // had to deepen because it had not found one. A fight where routes=0 is
                    // a fight where every plan stopped where the depth ran out — which is
                    // the difference between "playing the solved line" and "playing to the
                    // horizon", and the number to compare against plans=.
                    // opening= is the episode: how many attempts the five-to-ten-minute
                    // ladder took, and how often it hit the ten-minute cap with no line
                    // that ends the fight. A fight that ends with openingNoRoute>0 is a
                    // fight the search could not solve inside its ceiling.
                    + $"routes={plansWithRoute}/{plans} "
                    // A bare planRefusals count cannot answer "why did the script stop":
                    // drift (the board really moved), pending (the step has not resolved
                    // yet) and missing (the path never replayed, so there was nothing to
                    // compare) are three different failures with three different fixes.
                    + $"planRefusals={planRefusals}"
                    + $"(drift={planDriftDetected},pending={planSentinelPending},missing={planSentinelMissing}) "
                    + $"noScript={planTicksWithoutScript} "
                    + $"seatSkips={seatStepsSkipped} "
                    + $"openingAttempts={openingAttempts} "
                    + $"openingNoRoute={openingNoRouteAtCap} "
                    + $"stale={fallbackStale}(root={fallbackStaleRoot},notification={fallbackStaleNoise}), "
                    + $"first-action-refused={fallbackFirstAction}({refusalSummary}), "
                    + $"potion-declined={potionDeclined}, "
                    + $"exception={fallbackException}, maxRound={maxRoundSeen}, "
                    + $"party={partyState}, boundaries={topBoundaries}, "
                    + $"no-action-kinds={kindSummary}, "
                    + $"unmodeled-potions={(unmodeledPotions.Count == 0 ? "none" : string.Join(";", unmodeledPotions.OrderBy(n => n, StringComparer.Ordinal)))}");
            }
            plans = fallbackBoundary = fallbackNoAction = fallbackStale = fallbackException = 0;
            // `plansWithRoute` is the numerator of `routes=` and it was the FOURTH counter
            // this block forgot — the same miss named in the comment below, whose own
            // example (`routes=3/2`) is exactly what it went on printing. Measured live
            // 2026-09-20: a single round printed routes=1/6, 2/5, 3/3, 4/6, 6/4, 7/4, 8/6,
            // 9/4, 10/14, 11/3 … 14/2 — a run-cumulative numerator over a per-fight
            // denominator, so no single number in `routes=` could be read as anything.
            plansWithRoute = 0;
            fallbackStaleRoot = fallbackStaleNoise = 0; potionDeclined = 0; noActionKinds.Clear();
            planRefusals = 0; planTicksWithoutScript = 0; seatStepsSkipped = 0;
            fallbackFirstAction = 0; firstActionKinds.Clear(); probesDone = false; shadowDone = false; shadowChoice = null; shadowCombat = null; tournamentDriven = 0; tournamentPasses = 0; tournamentDeclined = null;
            openingAttempts = 0; openingNoRouteAtCap = 0;
            // The three L1 counters were missing from this reset, so they accumulated
            // across fights while planRefusals beside them was cleared — a summary would
            // read `planRefusals=0(drift=13)`, or `routes=3/2`, which is how a single
            // gold drift in fight 1 came to look like 13 drifts everywhere.
            planDriftDetected = 0; planSentinelPending = 0; planSentinelMissing = 0;
            searchingRoundsHighWater = 0;
            LastNoAction = NoActionKind.None;
            boundaryKinds.Clear(); unmodeledPotions.Clear();
            combatLabel = ""; maxRoundSeen = 0; partyState = "";
        }
    }
    internal Status Poll(CombatState combat, IReadOnlyList<Player> actors, uint actionVersion, uint? manualFocus,
        bool humansFinished, BotDifficulty difficulty, out TeamCombatPlanner.Decision? decision)
    {
        decision = null;
        ConfirmedPotion = null;
        ConfirmedEndTurn = null;

        if (actors.Count == 0) { Reset(); return Status.Ready; }
        if (TournamentDrives(combat, actors))
        {
            // NEVER LET A TOURNAMENT DECLINE FALL INTO THE OPENING LADDER.
            //
            // This flag is what hands the ordinary path 180 s, then five one-minute extensions.
            // The tournament path never touched it, so a single declined decision — and the
            // tournament declines whenever the board cannot be forked, which is exactly the
            // `pending-choice` state — started the ladder and the bot went silent for minutes.
            // Measured live 2026-09-21: two preview runs fired it 21 and 7 times, with
            // heartbeats at 45 s / 90 s / 135 s. Clearing it makes a decline cost one ordinary
            // search instead of a whole episode, which is what "fall back cheaply" has to mean.
            openingSearch = false;
            var point = $"{combat.GetHashCode()}/{combat.RoundNumber}/{actionVersion}";
            if (point != tournamentDeclined)
            {
                // PENDING IS NOT DECLINED. The tournament is sliced, so it answers across
                // several frames; returning Pending keeps the caller polling and — the whole
                // point — keeps the main thread free to render the card animation in between
                // (this branch used to block for up to a second inside one Poll).
                var outcome = TryTournamentDecision(combat, actors, humansFinished, point, out decision);
                if (outcome == TournamentOutcome.Pending) return Status.Pending;
                if (outcome == TournamentOutcome.Decided) return Status.Ready;
            }
            // Declined (or already declined) for this point: do not ask again until the board
            // moves — and DO NOT RUN THE SEARCH.
            //
            // The search used to be the fallback here, and it made every measurement of this
            // mode a hybrid: measured live 2026-09-21 a run had C at 51 % with the other 49 %
            // coming from ~180-node searches that hit their time budget. That is not a safety
            // net, it is a confound — the entire point of this configuration is to find out what
            // the tournament plays, and a search standing behind it answers a different question.
            //
            // `Status.Fallback` hands the tick to the legacy planner, which returns a legal
            // action immediately. C either decides, or the simplest planner covers the tick, and
            // nothing in between looks like C when it is not.
            //
            // A cached script is only valid while the tournament is the thing driving the fight.
            // The legacy planner is about to submit an action the script never priced, so the
            // line stops describing the board here and must not be replayed on a later tick.
            ClearTournamentScript("the tournament declined; the legacy planner may move the board");
            tournamentDeclined = point;
            return Status.Fallback;
        }
        if (ReferenceEquals(combat, disabledCombat)) return Status.Fallback;
        // `maxRound` has to be the last round the fight actually REACHED, so it is
        // sampled here, on every poll. It used to be sampled only inside ObserveCombat,
        // which runs on the search-start path — and since a solved fight is ONE search
        // replayed to the end, "the round the search started in" is always 1. Every
        // multi-turn fight in the 2026-09-20 live round reported `maxRound=1`, so the
        // summary could not be used to tell a two-turn fight from a twenty-turn one.
        maxRoundSeen = Math.Max(maxRoundSeen, combat.RoundNumber);
        // Scope and a queued action: cheap, unambiguous reasons to abandon a
        // branch. `KernelSession.LiveRevision` deliberately is NOT part of this
        // check — it counts every combat-state notification, including ones a
        // branch does not depend on, and discarding on those threw away in-flight
        // searches that were still valid. A queued action is a real event and
        // still cancels here; anything subtler is caught by the authoritative
        // state-stamp comparison on the completion path before a plan is deployed.
        bool Current() => ReferenceEquals(rootCombat, combat) && round == combat.RoundNumber
            && queueVersion == actionVersion && focus == manualFocus
            && actorIds.SequenceEqual(actors.Select(p => p.NetId));
        try
        {
            if (search is not null && !Current())
            {
                // The live root moved while expanding. Hand this tick to the legacy
                // planner and retry next tick: normal human actions must never
                // accumulate into disabling the kernel, and must never leave the
                // bots without an action either.
                staleCount++;
                fallbackStale++;
                CountStale(combat);
                Reset(); return Status.Fallback;
            }
            // Reuse before re-searching. Within a turn nothing but our own submitted
            // actions changes the board, and the search already computed the rest of the
            // sequence, so the next step is still the plan's and a fresh search would
            // only rediscover it at full cost. Anything TryEmitFromPlan refuses gives
            // the plan up and falls through to the search below, which is exactly the
            // behaviour this had before the plan was kept.
            if (search is null && plan is not null)
            {
                if (TryEmitFromPlan(combat, actors, humansFinished, out var reused, out var waitingForStep))
                {
                    decision = reused;
                    return Status.Ready;
                }
                if (waitingForStep)
                {
                    // The previous step has been submitted but has not landed yet. Upstream
                    // AWAITS the action's completion task here (DeployCurrentTurn awaits
                    // `actionCompletion` per step); our tick model cannot block, so the
                    // equivalent is to come back next frame with the script intact. It must
                    // NOT be a refusal: dropping the script because our own action is still
                    // resolving is how a boundary crossing would kill itself.
                    return Status.Pending;
                }
                // Exhaustion first, and separately. Past the last step there is nothing
                // left to refuse — and reporting it as a refusal both inflated the
                // acceptance metric and starved the STRICT EXECUTION branch below, which
                // is the one written to count ticks the legacy planner covered (noScript).
                // Completion also leaves `planCommitted` set on purpose: that is what
                // routes the following ticks into that branch.
                if (planIndex >= plan!.Actions.Count)
                {
                    CompleteContinuation(plan!);
                }
                else
                {
                    // A refusal means the live board no longer matches the saved script
                    // (card, target, turn boundary, or risk). Invalidate the continuation
                    // and search from the authoritative state on the next tick.
                    //
                    // The three sentinel counters only cover the L1 block; every other guard
                    // here returned false without recording WHY, which is why a fight could
                    // show planRefusals=18 with no way to tell which guard fired.
                    ReportPlanStepRefusal(combat, actors, plan!);
                    planRefusals++;
                    // The drop is explicit and named, the way upstream nulls
                    // `_combat.ContinuationSource` only at a named replan decision. It is
                    // also logged unthrottled: a script that disappears without a line is
                    // indistinguishable from one that ran out, and that ambiguity is what
                    // made three separate silent kill sites take three rounds to find.
                    DropContinuation($"step refused at index {planIndex}/{plan?.Actions.Count}");
                    planCommitted = false;
                    return Status.Fallback;
                }
            }
            // STRICT EXECUTION: once a plan has been deployed for this fight the kernel
            // does not search again. Only reachable when the script has run out of
            // actions or was dropped for a reason of its own; the tick goes to the
            // legacy planner and the counter says how much of the fight that covered.
            if (search is null && planCommitted)
            {
                planTicksWithoutScript++;
                return Status.Fallback;
            }
            if (search is null)
            {
                if (noActionStamp.Length > 0 && noActionStamp == KernelSession.CaptureBotStamp(combat, actors))
                {
                    fallbackNoAction++;
                    Report("no-action verdict reused: the visible board has not changed");
                    return Status.Fallback;
                }
                if (NoAvailableCombatAction(actors))
                {
                    // Nothing the team can do this tick. Capturing and searching
                    // here cannot find anything, so answer immediately: the runtime
                    // skips the poll in this state, and the tests drive the planner
                    // directly, so the cheap guard lives here too. Zero energy is
                    // NOT enough to conclude this — zero-cost and X-cost cards are
                    // playable, and an energy/draw potion can unlock an unaffordable
                    // hand, so both are checked on the live board.
                    fallbackNoAction++;
                    LastNoAction = NoActionKind.Idle;
                    noActionKinds[LastNoAction] = noActionKinds.GetValueOrDefault(LastNoAction) + 1;
                    noActionStamp = "";
                    Report("no available combat action: no live playable card or usable potion");
                    Reset(); return Status.Fallback;
                }
                var capture = Stopwatch.StartNew();
                rootCombat = combat; round = combat.RoundNumber; focus = manualFocus; queueVersion = actionVersion;
                actorIds = actors.Select(p => p.NetId).ToArray();
                stamp = KernelSession.CaptureLiveStamp(combat);
                var root = KernelSession.Capture(combat);
                if (root.ProjectionDifferences.Count > 0)
                {
                    // L0: the search is running from a simulator that does not reproduce
                    // the live board, so nothing it finds can be promised to replay. Say
                    // so at capture time instead of letting it become a silent mid-fight
                    // mismatch.
                    projectionMismatches++;
                    Report($"capture projection differs from live "
                        + $"({root.ProjectionDifferences.Count} seat(s), #{projectionMismatches} this fight): "
                        + string.Join(" | ", root.ProjectionDifferences.Take(3)));
                }
                actorList = actors.ToArray();
                var fresh = combatLabel.Length == 0;
                ObserveCombat(combat, actors);
                if (fresh) Log.Info($"CoopBots combat start: {combatLabel}; {partyState}");
                // One bounded search shape for every phase. The old post-human and
                // solo escalation traded a predictable pause for a seconds-long one;
                // removing it means humans finishing (or dying) no longer changes
                // the budget. Bounded is not shallow: the same multi-card,
                // cross-bot beam runs in every phase and the next action is always
                // re-planned from the real board, never replayed from a stale tail.
                // Rounds is NOT "how many turns one end-turn expansion simulates" — that
                // comment was wrong and the wrong value hid behind it. KernelSession.EndTurn
                // advances at most ONE round per call (`if (RoundsAdvanced + 1 < maxRounds)`
                // in KernelTurns.cs:79 wraps a single StartNextPlayerTurn, not a loop), so this
                // is a CAP ON HOW MANY ROUND ADVANCES ONE BRANCH MAY ACCUMULATE.
                //
                // At 2 it meant a branch could cross exactly one round boundary: the second
                // resolution took the else path, set EnemyPhaseCompleted, and CanAct became
                // false for every seat — no cards, no end-turns, so the frontier emptied.
                // Measured on a real all-bot fight, every plan stopped at 31 actions / 2 rounds
                // while the fight needed 35 actions / 3, so `route` could never be true and 20
                // attempts burned the whole ten-minute cap re-walking the same two rounds.
                // Depth could not help: EnPhaseCompleted is a round wall, not a depth wall.
                //
                // 99 is "no cap in practice": a fight that needs more than 99 rounds has
                // already been lost. Depth and the node/time budget are the real limiters.
                //
                // BOUNDED LOOKAHEAD WAS ATTEMPTED HERE AND REVERTED (2026-09-20). Setting this
                // to 3 made one search plan 3 rounds instead of the whole fight, which is what
                // the 27 GB / 0.5-0.7 MB-per-node allocation calls for. It does not work as a
                // planner constant: with no victory reachable the evaluator ranks a 2-ACTION
                // line above the 3-round one, because the longer line has to eat enemy turns
                // and nothing at the horizon pays for them. Measured live: `best partial:
                // actions=2, nodes=1616, stop=frontier-empty` for a 200k-node budget, then the
                // ladder's cap branch (`HasRoute: false`) disabled the kernel for the whole
                // fight. Doing this properly needs a TERMINAL VALUE AT THE HORIZON in the
                // evaluator, which is a kernel change, not a constant. See
                // work/agent-state/state/live-pipeline-plan.md.
                // USER SPEC 2026-09-20: bounded lookahead. Search at most this many rounds, deploy that
                // script, play it out, then search again from the live board — a run is a SEQUENCE
                // of scripts rather than one script for the whole fight. 99 was "no cap in
                // practice", which is what made a single search allocate 10-27 GB and stall the
                // main thread long enough to kill the round.
                var (depth, width, nodes, budgetMs, wallMs, rounds) = (9, 8, 768, 200, 300, LookaheadRounds);
                // Difficulty is a thinking-time knob, not a different algorithm:
                // scale the node and time budgets, keep the search shape (depth
                // and width) identical so a fast tier cuts the thinking short
                // rather than planning something structurally worse. Pro's x1.5
                // schedule tops the wall budget out at 450ms.
                var thinking = difficulty.ThinkingScale();
                nodes = Math.Max(64, (int)(nodes * thinking));
                budgetMs = Math.Max(20, (int)(budgetMs * thinking));
                wallMs = Math.Max(40, (int)(wallMs * thinking));
                sliceMs = DefaultSliceMs;   // every search starts narrow; only the opening widens it
                // Full-bot tables only: with a human still deciding there is no plan to
                // replay, so a minute of thinking would just be a minute of nothing.
                // EVERY segment gets a real budget, not just a fight's first search.
                // USER SPEC 2026-09-20: a run is a SEQUENCE of 5-round computations, so each one
                // has to BE a computation. Gated on `openingSearch`, only the fight's first script
                // was planned properly and every later segment dropped to the INTERACTIVE per-tier
                // budget (768 nodes / 450ms) — measured live12 as `alloc=21-55MB` for those
                // searches against ~1.5GB for the first. Those are not 5-round computations, they
                // are guesses, and a chain of guesses is not the design.
                //
                // The gate is now "every seat is driven", the same condition that decides whether
                // the kernel is authoritative at all. Note what is deliberately NOT reset here:
                // the EXTENSION ladder's clock and attempt count stay shared across the whole
                // fight (`openingEpisodeStartMs` is stamped once per combat), so a chain of
                // segments shares one 8-minute extension ceiling while each segment still gets its
                // own three-minute base search. A per-segment extension ladder would let a long
                // fight think for 3 minutes per segment with no ceiling at all.
                // AND NOT WHILE THE TOURNAMENT IS DRIVING. The gate below used to be
                // `All(Drives)` alone, and clearing `openingSearch` does NOT touch it — that flag
                // gates `StopOnFirstTerminal` and the escalation, not the budget. Measured live
                // 2026-09-21: a tournament run played one card, declined once, and the fallback
                // search took the FULL three-minute budget (`search heartbeat: elapsed=45000ms
                // opening=False`). The previous "cheap fallback" fix addressed a flag that was
                // never in this path. With the tournament driving, a decline must cost a per-tier
                // search (450 ms), which is what "fall back cheaply" has to mean.
                // BOTH halves matter: `TournamentDrives` is true on a mixed table now, so the
                // deep opening ladder must never leak there through AllSeatsDriven. The gate is
                // still "every seat is driven" for the deep budget; only the tournament itself
                // was widened. (The kernel suite's "Pro's INTERACTIVE wall budget must stay
                // bounded at 450ms when a human is seated" assertion catches a leak.)
                if (!TournamentDrives(combat, actors) && AllSeatsDriven(combat))
                {
                    // Captured before the overrides: the first version of this line divided
                    // the already-overwritten budgetMs and printed "was 60000ms", which is
                    // exactly the kind of log that makes a later reading lie.
                    var wasBudget = budgetMs;
                    var wasNodes = nodes;
                    // Started once per COMBAT, not once per attempt: an attempt that the
                    // live board invalidates must not restart the ten-minute clock, or a
                    // fight could keep resetting its own ceiling by moving.
                    if (openingEpisodeStartMs == 0) openingEpisodeStartMs = Environment.TickCount64;
                    // Attempt 1 is the five-minute search; every later one is a one-minute
                    // extension with twice the depth, so the extra minute goes DEEPER
                    // instead of re-walking a shape whose depth already closed it.
                    var extension = openingAttempts > 0;
                    var attemptBudget = extension ? openingExtensionMs : OpeningSearchBudgetMs;
                    // No depth ladder: upstream has no depth axis, and doubling ours made
                    // the depth the binding constraint instead of the node or time budget.
                    depth = OpeningSearchDepth;
                    width = OpeningSearchWidth;
                    nodes = OpeningSearchNodes;
                    budgetMs = attemptBudget;
                    wallMs = attemptBudget;
                    sliceMs = OpeningSliceMs;
                    Report($"opening search #{openingAttempts + 1}: every seat is driven, "
                        + $"budget={attemptBudget}ms nodes={OpeningSearchNodes} depth={depth} width={OpeningSearchWidth} "
                        + $"episodeElapsed={Environment.TickCount64 - openingEpisodeStartMs}ms "
                        + $"(was {wasBudget}ms {wasNodes} nodes, depth=9 width=8)");
                }
                timeBudgetMs = budgetMs;
                // The CPU budget is spread over frames (4ms each), so it is also
                // capped by wall clock.
                wallBudgetMs = wallMs;
                planningStartMs = Environment.TickCount64;
                gcPauseAtStart = GC.GetTotalPauseDuration();
                gen0AtStart = GC.CollectionCount(0);
                allocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
                // A new search owns the accumulators again; the next search-complete line
                // carries its metrics and every line after that is a plan step.
                searchMetrics.BeginSearch();
                evaluation = new(combat, actors, manualFocus, null, rounds);
                rootMetrics = evaluation.Evaluate(root);
                // S4: measurement only — the same root priced by CombatSolver's evaluator,
                // printed beside ours. Replacing the scorer outright would leave any
                // difference unattributable; this makes the size and the sign of the
                // disagreement the first thing a log shows. One line per search, at the
                // root only, so it cannot flood a per-node path.
                // FULL-BOT ONLY, and the gate is not cosmetic. Both lines below invoke a
                // foreign engine: TeamScore reflects into the vendored engine (0.41.0) and
                // captures a solver per seat, and CombatSolver41.Solve runs the same version
                // from its own assembly. On a table with
                // a human in it that is pure cost on the planning path, and neither engine
                // is being probed for that board anyway. Same condition as the opening
                // budget, so "every seat is driven" stays the single definition of when
                // this mod is allowed to think differently.
                if (combat.Players.All(player => AutoPilot.Drives(player.NetId))
                    && CoopBots.Kernel.CombatSolverEvaluator.TeamScore(
                        root, combat, root.RoundsAdvanced + 1,
                        new HashSet<uint>(), actors.Select(p => p.NetId)) is { } engineScore)
                {
                    Log.Info($"CoopBots score: ours={rootMetrics.Score:F0} combatSolver={engineScore:F0} "
                        + $"seats={actors.Count} round={combat.RoundNumber}");
                }
                // Both probes below are DIAGNOSTICS — they answer a question about the
                // engine, not about this board — so they run once per combat. They used
                // to run once per search, which put an engine-side `CombatRootSnapshot.
                // Capture` (two full state stamps plus a projection and an equality
                // check) and two full state-text captures on the path of every search
                // in the fight, to re-print an answer that had not changed.
                if (!probesDone && combat.Players.All(player => AutoPilot.Drives(player.NetId)))
                {
                    probesDone = true;
                    RunShadowTournament(root, combat, actors);
                    // V2 probe: cross-turn reuse compares the LIVE board's state text against
                    // the text the search recorded for a branch at its turn boundary. That
                    // only works if the two producers agree byte for byte, and that was never
                    // checked on a real board — it was assumed, and the failure mode is silent
                    // (reuse simply never happens, which looks like "no benefit" rather than
                    // like a bug). At the root the branch IS the live capture, so this is the
                    // one position where the two should be identical if the formats match.
                    if (root is not null)
                    {
                        var live = KernelSession.CaptureLivePartyText(combat);
                        var branch = root.PartyStateText();
                        Log.Info($"CoopBots boundary probe: equal={live == branch} "
                            + $"liveLen={live.Length} branchLen={branch.Length}");
                        if (live != branch)
                        {
                            var at = 0;
                            while (at < live.Length && at < branch.Length && live[at] == branch[at]) at++;
                            Log.Info($"CoopBots boundary probe first difference at {at}: "
                                + $"live=[{Live(branch: live, at)}] branch=[{Live(branch: branch, at)}]");
                        }
                    }
                    Log.Info(CoopBots.Kernel.CombatSolver41.Solve(combat) is { } engine41
                        ? $"CoopBots 0.41: reachable, result={engine41.GetType().Name}"
                        : $"CoopBots 0.41: NOT reachable — {CoopBots.Kernel.CombatSolver41.LastFailure}");
                }
                rootSession = root;
                searchDepth = depth; searchRounds = rounds;
                portfolioWidths = PortfolioWidths(width);
                portfolioIndex = 0;
                portfolioNodesLeft = nodes;
                portfolioWinner = null;
                portfolioBestScore = double.NegativeInfinity;
                // V1: the engine's evaluation decides the score, ours is the fallback.
                //
                // The seam is exactly here and nowhere else: the search takes a
                // Func<KernelSession, double>, so swapping the brain is swapping this
                // lambda. Until now the engine was reachable but only logged — the joint
                // expansion saw four hands and then priced them with our reduced
                // evaluator, which is the "limited card sense" this closes.
                //
                // Full-bot only, for the same reason the opening budget is: with a human
                // deciding there is no single team to be optimal for, and the per-seat sum
                // would be pricing a board the engine was never asked about.
                //
                // TeamScore is a SUM OVER SEATS, and the search itself is joint, so a
                // cross-seat line is both generated (by the expansion) and priced (each
                // seat's view sees the same enemy, so the seat that cashes a window pays
                // for it). That is an approximation of a team value, not a derivation of
                // one — see the type's own note.
                var engineDecides = combat.Players.All(player => AutoPilot.Drives(player.NetId));
                superBrain = engineDecides;
                // The opening ladder sets its own depth per attempt, so there is no
                // separate escalation boost to apply here any more.
                // A ladder extension continues the SAME session, so the depth the panel is
                // already showing must carry over into it; only a genuinely new search
                // (a new fight, or a repair mid-fight) starts the round count from zero.
                if (!(openingSearch && openingAttempts > 0)) searchingRoundsHighWater = 0;
                search = new(root, actors, s =>
                {
                    if (engineDecides
                        && CoopBots.Kernel.CombatSolverEvaluator.TeamScore(s, combat, s.RoundsAdvanced + 1,
                            new HashSet<uint>(), actors.Select(p => p.NetId)) is { } engineScore)
                        return engineScore;
                    return evaluation.Evaluate(s).Score;
                },
                    new(Depth: depth, Width: portfolioWidths[0], MaxNodes: portfolioNodesLeft,
                        IncludeEndTurns: humansFinished, MaxRounds: rounds,
                        // Only the ladder's EXTENSION attempts stop on the first ending.
                        // openingSearch is combat-scoped, so this is false for the opening
                        // search itself AND for every later repair search — both of those
                        // still have to rank endings against each other.
                        StopOnFirstTerminal: openingSearch && openingAttempts > 0));
                revision = KernelSession.LiveRevision;
                computeMs = capture.Elapsed.TotalMilliseconds;
                // Capture and JIT are not preemptible; never add an expansion slice
                // to the same frame, and expose slow capture in diagnostics.
                if (computeMs >= 40) Report($"capture={computeMs:F1}ms phase={(humansFinished ? "finished" : "live")}");
                return Status.Pending;
            }
            var elapsed = Stopwatch.StartNew();
            // Search heartbeat. The unattended live loop kills the client when godot.log has
            // not moved for five minutes, and an opening search is legitimately silent for up
            // to TEN (300s base plus up to five 60s continuations) — so without this line the
            // kill rule murders a valid search on every fight. One line every 45s also makes
            // "the search is stuck" distinguishable from "the search is thinking", which no
            // other signal in the log provides.
            if (SearchingElapsedMs - lastSearchHeartbeatMs >= SearchHeartbeatMs)
            {
                lastSearchHeartbeatMs = SearchingElapsedMs;
                ReportSearchLine($"search heartbeat: elapsed={SearchingElapsedMs}ms "
                    + $"rounds={SearchingRoundsReached} opening={openingSearch} attempt={openingAttempts + 1} "
                    + $"slice={sliceMs}ms");
            }
            var done = search.Advance(TimeSpan.FromMilliseconds(sliceMs), Current);
            computeMs += elapsed.Elapsed.TotalMilliseconds;
            // AN OVERRUNNING SLICE HAS TO SAY SO. `sliceMs` is a soft budget checked BETWEEN
            // expansions, so one expansion can overrun it without limit — and when that happens
            // the main thread never returns to `Poll`, which means the 45-second search
            // heartbeat cannot fire either. The only trace is then an unexplained silence, and
            // the watchdog kills a search that was legitimately running.
            //
            // Measured live 2026-09-20 (live10, THE_KIN_BOSS, act-1 boss, run LOST): the
            // opening search ran ~366 s and emitted exactly TWO heartbeats — at elapsed=45000ms
            // and elapsed=90000ms, both reading `rounds=14` — then nothing for the remaining
            // ~276 s. `rounds` never advanced past 14, so this was not "still thinking"; it was
            // one slice (or a run of them) that never came back. The kernel therefore never
            // deployed a plan, the whole boss fight was played by the LEGACY planner, and the
            // run was lost. Nothing in that log named the culprit, because nothing was looking.
            //
            // 20x the slice is deliberately loose: a normal slice may overshoot while finishing
            // the expansion it started, and only a stall worth reporting should print.
            if (elapsed.Elapsed.TotalMilliseconds > sliceMs * 20)
            {
                ReportSearchLine($"search slice overran: took={elapsed.Elapsed.TotalMilliseconds:F0}ms "
                    + $"budget={sliceMs}ms rounds={SearchingRoundsReached} opening={openingSearch} "
                    + $"attempt={openingAttempts + 1} compute={computeMs:F0}ms "
                    + $"nodes={search.ExpandedNodesSoFar}");
            }
            var wallElapsed = Environment.TickCount64 - planningStartMs;
            if (!done && computeMs < timeBudgetMs && wallElapsed < wallBudgetMs) return Status.Pending;
            // Budget exhausted while the tree is still EXPANDING. Continue the same search
            // rather than finalising it and starting another.
            //
            // The ladder used to be implemented by restarting with a doubled DEPTH, and that
            // depth was the only thing an extension ever bought. Removing the depth axis (no
            // such field upstream — SolverSearchProfile is width/nodes/time) left the
            // extensions restarting the SAME search with a SMALLER time budget, so "extend by
            // a minute" bought strictly less than the attempt it replaced: measured live
            // 2026-09-20 as six minutes of the panel sitting on the same deepest round with no
            // result. A continuation is the honest form of the same policy: keep the tree, the
            // node count and the frontier, and move only the clock.
            //
            // Only when `!done`: a search that ran its frontier dry (or hit the node cap) has
            // nothing left to buy, and re-running it would be a deterministic repeat.
            if (!done && superBrain && openingSearch && search is { HasRoute: false })
            {
                var episodeElapsed = Environment.TickCount64 - openingEpisodeStartMs;
                var remaining = OpeningSearchHardCapMs - episodeElapsed;
                if (remaining > 0 && openingAttempts < OpeningSearchMaxExtensions)
                {
                    openingAttempts++;
                    var added = (int)Math.Min(OpeningSearchExtensionMs, remaining);
                    openingExtensionMs = added;
                    wallBudgetMs += added;
                    timeBudgetMs += added;
                    Report($"opening search has no end yet; CONTINUING the same search for another "
                        + $"{added / 1000}s (extension {openingAttempts}/{OpeningSearchMaxExtensions}, "
                        + $"episodeElapsed={episodeElapsed}ms of {OpeningSearchHardCapMs}ms)");
                    return Status.Pending;
                }
            }
            if (!done) search.FinishAtBudget();
            // Portfolio step. A member that ran out of nodes did not finish, so its score
            // is not comparable to a finished member's — skip it rather than let an
            // arbitrarily truncated search win. Everything else compares strictly, so a
            // tie leaves the earlier (baseline) member in place.
            var member = search.CompletedResult!;
            portfolioNodesLeft -= member.ExpandedNodes;
            if (member.StopReason != "node-budget")
            {
                if (portfolioWinner is null || member.Score > portfolioBestScore)
                {
                    if (portfolioWinner is not null && !ReferenceEquals(portfolioWinner, search)) portfolioWinner.Dispose();
                    portfolioWinner = search;
                    portfolioBestScore = member.Score;
                }
            }
            if (portfolioIndex + 1 < portfolioWidths.Length && portfolioNodesLeft > 0)
            {
                portfolioIndex++;
                if (!ReferenceEquals(portfolioWinner, search)) search.Dispose();
                search = new(rootSession!, actors, s => evaluation!.Evaluate(s).Score,
                    new(Depth: searchDepth, Width: portfolioWidths[portfolioIndex],
                        MaxNodes: portfolioNodesLeft, IncludeEndTurns: humansFinished, MaxRounds: searchRounds,
                        StopOnFirstTerminal: openingSearch && openingAttempts > 0));
                return Status.Pending;
            }
            // Out of members or out of budget: deploy the best that actually finished,
            // and fall back to this member when none did — the old single-search
            // behaviour, which is also what happens when the portfolio yields nothing.
            if (portfolioWinner is not null && !ReferenceEquals(portfolioWinner, search)) search.Dispose();
            search = portfolioWinner ?? search;
            var result = search.CompletedResult!;
            // A completed search must always end in Ready or Fallback. Returning
            // Pending here re-captures and re-searches every frame without ever
            // submitting an action, which leaves the bots idle for the fight.
            // The state stamp is the authoritative check: Since Current() no
            // longer treats notification churn as staleness, this is what stops a
            // plan captured before a real board change from being deployed.
            if (!Current() || KernelSession.CaptureLiveStamp(combat) != stamp)
            {
                fallbackStale++;
                CountStale(combat);
                Reset(); return Status.Fallback;
            }
            staleCount = 0;
            // "Every seat is driven" is sampled when the search STARTS, and a search is
            // long enough to hand the seat over — or take it back — while it runs. Every
            // other all-bot decision (the opening budget, the engine scorer, the probes,
            // plan replay) is gated on this same fact, so the DEPLOYMENT of a plan has to
            // be too: otherwise a seat that came back to its owner mid-search still
            // receives one action from a line computed on the assumption that no human
            // could act. The mixed path is the 1.5-second-per-card search and it must not
            // be handed a super-brain plan.
            //
            // It deliberately does NOT bail out here any more. Bailing out gated the
            // no-action path too, which is wrong twice over: that verdict is cached
            // against the BOT-VISIBLE board (CaptureBotStamp covers `actors` only, so
            // finishing a human turn cannot invalidate it — the invariant
            // AuditBotStampIgnoresHumanEndTurn exists for), and refusing it here made
            // every human-to-finished transition pay for a whole search it then threw
            // away. The plan branch below re-checks this before a decision is returned.
            bool everySeatDriven = combat.Players.All(player => AutoPilot.Drives(player.NetId));
            if (!everySeatDriven) superBrain = false;
            // Only a result that passed the stamp check describes the live board,
            // so only its boundaries are useful model-work evidence.
            foreach (var (kind, count) in result.Boundaries)
                boundaryKinds[kind] = boundaryKinds.GetValueOrDefault(kind) + count;
            if (result.Boundaries.Count > 0)
            {
                // An unmodeled card only removes that card from the search; it must
                // not disable the kernel for the whole fight. A10 hands almost
                // always contain something unmodeled, so disabling here meant the
                // kernel effectively never ran. Keep the plan for the modeled cards
                // and let the legacy planner cover the skipped ones.
                Report("partial plan (skipped unmodeled): " + string.Join(";", result.Boundaries.Keys.Take(3)));
                fallbackBoundary++;
            }
            if (result.Actions.Count > 0 && search.CompletedState is { } final)
            {
                // This IS the super-brain path: a concrete plan is about to be handed out
                // as a decision. A seat that came back to its owner while the search ran
                // must not receive one action from a line computed on the assumption that
                // no human could act — see the sampling above. The no-action verdict
                // further down is deliberately NOT gated this way: it is cached against
                // the bot-visible board only, so a finished human turn cannot invalidate
                // it, and refusing it there cost a whole search per human transition.
                if (!everySeatDriven)
                {
                    Reset(); return Status.Fallback;
                }
                var first = result.Actions[0];
                if (first.EndTurn)
                {
                    // `result.Boundaries.Count > 0` used to be a refusal term here, and it
                    // is what produced a live fight where every plan was thrown away: one
                    // unmodeled card (SURVIVOR) anywhere in the line made the whole plan
                    // "partial", and a plan whose best line STARTS by ending the turn was
                    // then declined every single time. The boundary term is wrong on the
                    // merits too: a boundary is an action the model skipped, not evidence
                    // that the enemy phase will differ, and the actions after this
                    // end-turn are re-validated against the live board at the boundary
                    // anyway.
                    //
                    // `result.HasUncertainRisk` was the last term standing here and is now
                    // gone for the same reason. It is NOT a statement about the end turn:
                    // the enemy phase itself sets the flag on every crossing
                    // (KernelTurns:71 runs PredictionCoverage.Collect over the whole
                    // accumulated history and ORs "any uncompensated gap" in), so the term
                    // reduced to "this plan crosses a turn" — precisely the boundary term
                    // that was already rejected above. Measured, not assumed: every refusal
                    // in the 2026-09-20 01:11 log printed `risk=True` with NO `risk_first=`
                    // suffix, and RiskDetail is only ever written by a card play
                    // (KernelSession.Play/UsePotion), so the risk came from the enemy-phase
                    // path and never from a card. `boundaries=0` on the `end-turn plan
                    // declined` line proves the other term was not the one firing. The flag
                    // is kept on Result as a diagnostic — it just no longer gates.
                    if (!humansFinished || !actors.Contains(first.Player))
                    {
                        // Unthrottled: the throttled version was suppressed by the
                        // "partial plan" line printed moments earlier in the same tick,
                        // which is why this refusal was invisible in the live log.
                        ReportSearchLine($"end-turn plan declined: humansFinished={humansFinished}, "
                            + $"actorEligible={actors.Contains(first.Player)}, uncertainRisk={result.HasUncertainRisk}, "
                            + $"boundaries={result.Boundaries.Count}");
                        EndOpeningEpisode();
                        Reset(); return Status.Fallback;
                    }
                    noActionStamp = "";
                    ConfirmedEndTurn = first.Player;
                    CountDeployedPlan();
                    var endTurnBoundaries = search.TurnBoundaries;
                    var endTurnMetrics = evaluation!.Evaluate(final);
                    ReportSearch("end-turn", result, humansFinished);
                    Reset();
                    // Keep the tail. A line that begins by ending the turn was being
                    // played as "end turn" alone: Reset drops the plan and the other N-1
                    // actions of a solved fight went with it.
                    CommitContinuation(result, combat, endTurnBoundaries,
                        result.ActionStates, endTurnMetrics.WeightedDeaths);
                    // The fight has been solved once, so later searches are repairs and go
                    // back to the per-tier budget. The card and potion paths both set this;
                    // the end-turn-first path did not, so a plan whose first action is an
                    // EndTurn left the episode open — and when it was later dropped, the
                    // next search ran the whole five-minute opening ladder again. Measured
                    // live: `opening search #1 ... episodeElapsed=111390ms` immediately
                    // after a `turnPrefix=0` plan was dropped at index 9/116.
                    openingSearch = false;
                    return Status.Ready;
                }
                // A potion-first plan (e.g. buff, then the burst) is deployed as a
                // free action; the cards are replanned against the real state.
                if (first.Potion is { } planned)
                {
                    // The live bottle, not the plan's simulated one. See FindLivePotion.
                    var livePotion = FindLivePotion(first.Player, planned);
                    if (!actors.Contains(first.Player) || livePotion is null
                        || livePotion.IsQueued || livePotion.HasBeenRemovedFromState
                        || !livePotion.PassesCustomUsabilityCheck || !livePotion.IsValidTarget(first.Target))
                    {
                        // Upstream answers this with DEPLOY_REPLAN potion_missing /
                        // potion_mismatch (SolverController.cs:2693-2721): drop the route and
                        // search again from the live board. It is ordinary drift, not the
                        // "the board moved under us" staleness staleCount measures, and it
                        // MUST NOT disable the kernel for the rest of the fight. The old code
                        // pushed it through staleCount — which also collects root movement
                        // from human plays — so in a four-player game one bottle that had
                        // already been drunk could be the second event that silently turned
                        // the kernel off for the whole combat.
                        Report($"potion plan declined: {planned.Id.Entry} is "
                            + $"{(livePotion is null ? "no longer held" : "no longer usable")}; replanning from the live board");
                        EndOpeningEpisode();
                        Reset(); return Status.Fallback;
                    }
                    // The "redundant debuff" veto was REMOVED on 2026-09-20. It declined a
                    // potion the search had priced and chosen, on the reasoning that
                    // re-applying a debuff the target already carries only extends its
                    // duration. Upstream has no such rule (grep "redundant" in
                    // SolverController: zero hits), and a post-hoc veto here is the wrong
                    // home for the complaint even when the complaint is real: if the
                    // marginal Vulnerable stack is worth less than the bottle, the
                    // EVALUATOR should price it that way and the search will not choose it.
                    // Overriding the search instead means the executor silently contradicts
                    // the plan it is supposed to follow — the same shape as the gates that
                    // cost us this whole debugging session.
                    //
                    // Open risk, stated rather than hidden: the observation that produced
                    // this veto (a boss at 15% HP already carrying four Vulnerable, and a
                    // bot spending the bottle into it) may come back. If it does, the fix
                    // belongs in the evaluator's marginal-power pricing, not here.
                    noActionStamp = "";
                    // Hand over the LIVE bottle. Passing the plan's simulated model to
                    // PotionPlan would enqueue a use of an object the player does not hold.
                    ConfirmedPotion = new PotionPlan(livePotion, first.Target,
                        $"kernel-plan:potion:{livePotion.Id.Entry},sequence:{result.Actions.Count},{result.StopReason}");
                    // Keep the tail, exactly as the card path below does. A potion is a FREE
                    // action, so the remaining steps were computed against a board where it
                    // had already resolved — the old `Reset()` here threw them all away and
                    // made every potion-first route pay for a second full search (observed:
                    // a 141 s opening search whose 40 remaining steps were discarded the
                    // moment the bottle was drunk, then re-searched from scratch).
                    var potionKeep = result;
                    var potionBoundaries = search?.TurnBoundaries ?? [];
                    var potionMetrics = evaluation!.Evaluate(final);
                    CountDeployedPlan();
                    LastNoAction = NoActionKind.None;
                    ReportSearch("potion", result, humansFinished);
                    Reset();
                    CommitContinuation(potionKeep, combat, potionBoundaries,
                        potionKeep.ActionStates, potionMetrics.WeightedDeaths);
                    openingSearch = false;
                    return Status.Ready;
                }
                // The search completed against a board that its own stamp says is still
                // live, so every one of these can only fail because the FIRST action is
                // no longer the right one to submit. That is a different failure from
                // staleness, and it used to be counted as stale — the fight summary then
                // read "stale=18(root=0,notification=0)", which says the state did not
                // move while claiming the plan was discarded for moving. It is also the
                // failure that makes "search once, then play the plan" feel false: the
                // search runs, is thrown away, and the legacy planner plays the card.
                var refusal = RefuseFirstAction(first, actors);
                if (refusal.Length > 0)
                {
                    fallbackFirstAction++;
                    firstActionKinds[refusal] = firstActionKinds.GetValueOrDefault(refusal) + 1;
                    ReportSearchLine($"plan refused at deployment: {refusal}; "
                        + $"seat={first.Player.NetId}, actions={result.Actions.Count}");
                    EndOpeningEpisode();
                    Reset(); return Status.Fallback;
                }
                var firstCard = first.Card!;
                var metrics = evaluation!.Evaluate(final);
                // A path that used an unmodeled environmental hook is an estimate,
                // so it must not claim a verified rescue when a potion compares.
                //
                // Keyed on RiskDetail, not on the flag: RiskDetail is written ONLY by a
                // card/potion play that left an uncompensated gap, which is exactly the
                // "hook we did not model" this line is about. The flag is set by the enemy
                // phase on every crossing too (KernelTurns:71), so keying on it marked
                // every cross-turn plan as an estimate even though its enemy phase was
                // simulated by the search — and TeamCoordinator reads the zeroed
                // DeathsPrevented as "this plan saves nobody", which would let a proactive
                // potion pre-empt a perfectly good scripted route.
                var estimated = result.RiskDetail.Length > 0;
                decision = new(first.Player, new(firstCard, first.Target,
                    Math.Max(1, metrics.Score - rootMetrics!.Score),
                    $"kernel-team,sequence:{result.Actions.Count},nodes:{result.ExpandedNodes},{result.StopReason}"
                        + (metrics.SoftFinish > 0 ? $",human-finish:{metrics.SoftFinish:F0}" : "")
                        + (estimated ? ",estimated" : ""), first.Choices),
                    result.Actions.Count,
                    // The real projection, NOT zeroed when the line used an unmodelled hook.
                    // Zeroing it was our invention and it was load-bearing: TeamCoordinator
                    // reads DeathsPrevented to decide whether a proactive potion outbids the
                    // plan, so "estimated" silently meant "this plan saves nobody". The
                    // ",estimated" marker in the reason string already carries the honesty
                    // that the zero was trying to express, and it does it without rewriting
                    // a number the rest of the code arbitrates on.
                    Math.Max(0, rootMetrics!.WeightedDeaths - metrics.WeightedDeaths),
                    rootMetrics.HpLoss - metrics.HpLoss);
                noActionStamp = "";
                // The rest of this plan is kept rather than thrown away. Only the first
                // action is submitted here; the remainder is offered to the next ticks
                // by TryEmitFromPlan, which re-checks the card before every step and
                // gives the whole plan up the moment one no longer holds. A stale
                // assumption therefore still cannot be played out — it ends the plan,
                // and the next tick searches again.
                // X3: a plan that never reached the end of the fight is not a route, and
                // accepting it means playing to wherever the depth happened to run out.
                // Escalate and look again — this is "if it found none, keep looking" in
                // the only form that cannot hang: bounded, and it falls through to the
                // partial plan once the attempts are spent.
                // The ladder: a five-minute opening search, then up to five one-minute
                // extensions, and an error at the end of it. BETWEEN attempts the plan is
                // NOT handed to the legacy planner — this returns Pending so the tick does
                // nothing and the next tick starts the extension. Letting a card be played
                // in between would change the board the search is trying to solve, which is
                // the opposite of "keep thinking about this board". AT THE END of the ladder
                // it is the opposite: the rest of the combat goes to the legacy planner,
                // because a partial line is a plan already known to run out.
                // Both halves of the ladder are OPENING ONLY. Gated on superBrain alone,
                // the error below re-fired on every later repair search — a 286-node
                // mid-fight search printed "the opening search spent 611375ms over 20
                // attempt(s)", which is both false and alarming; and the extension would
                // have kept a repair from ever being deployed.
                if (superBrain && openingSearch && search is { HasRoute: false })
                {
                    var episodeElapsed = Environment.TickCount64 - openingEpisodeStartMs;
                    // The extension is trimmed to what is left of the ten minutes, so the
                    // cap is a real ceiling rather than "the last attempt may overshoot".
                    var remaining = OpeningSearchHardCapMs - episodeElapsed;
                    // Every failure extends — `frontier-empty` included. It used to be
                    // excluded on the theory that re-searching the same board hits the same
                    // wall, but an extension does not re-search the same shape: the depth
                    // doubles (512 -> 1024 -> 2048), which is exactly what reaches past a
                    // frontier that closed too early. The ceiling is the ten-minute clock
                    // AND the five-extension count, whichever binds first.
                    var spent = search!.CompletedResult!.StopReason;
                    if (remaining > 0 && openingAttempts < OpeningSearchMaxExtensions)
                    {
                        openingAttempts++;
                        openingExtensionMs = (int)Math.Min(OpeningSearchExtensionMs, remaining);
                        Report($"no route to the end of the fight yet; extending by "
                            + $"{openingExtensionMs / 1000}s "
                            + $"(attempt {openingAttempts + 1}/{OpeningSearchMaxExtensions + 1}, "
                            + $"episodeElapsed={episodeElapsed}ms of {OpeningSearchHardCapMs}ms, "
                            + $"previousStop={spent})");
                        Reset();
                        return Status.Pending;
                    }
                    // The ceiling is reached and even the horizon was not reached by any line.
                    // Under bounded lookahead this can no longer mean "the search failed to
                    // solve the fight" — it cannot solve the fight BY CONSTRUCTION — so the old
                    // response (disable the kernel for the whole combat) is no longer available:
                    // it was measured on 2026-09-20 to turn every action of the fight into
                    // `via=legacy`. Report the shortfall, then fall through and DEPLOY the best
                    // line found. A shorter script is a worse script, not a reason to stop
                    // thinking; the next script is planned from the live board when this one
                    // runs out.
                    openingNoRouteAtCap++;
                    ReportSearchLine($"opening search hit its ceiling without reaching the horizon "
                        + $"(spent={episodeElapsed}ms over {openingAttempts + 1} attempt(s), "
                        + $"best: actions={result.Actions.Count}, nodes={result.ExpandedNodes}, "
                        + $"stop={spent}); deploying it and planning the next script from the live board");
                    EndOpeningEpisode();
                }
                var keep = result;
                // Read off the search before Reset disposes it. This is the plan's own
                // prediction for the turn that starts after its first EndTurn, and it is
                // what deployment has to match before it may play past that boundary.
                var boundaries = search?.TurnBoundaries ?? [];
                CountDeployedPlan();
                // A plan supersedes the last abstention: leaving it set would put a
                // stale reason on a later legacy move that had nothing to do with it.
                LastNoAction = NoActionKind.None;
                ReportSearch("card", result, humansFinished);
                Reset();
                // Reset above disposes the search; it can no longer reach the script, so
                // there is no ordering to get right here any more.
                CommitContinuation(keep, combat, boundaries, keep.ActionStates, metrics.WeightedDeaths);
                // The fight has been solved once; later searches are repairs, not the
                // opening, and go back to the per-tier budget.
                openingSearch = false;
                return Status.Ready;
            }
            // Search finished but recommends nothing: let the legacy planner act
            // this tick instead of leaving the bots idle. The kernel retries next
            // tick because the combat is not disabled.
            noActionStamp = KernelSession.CaptureBotStamp(combat, actors);
            fallbackNoAction++;
            // Three different problems, counted separately: nothing was playable
            // (harmless — out of energy), cards were simulated and none beat
            // passing (an evaluation gap that quietly hands the bots to the weaker
            // legacy planner), or an unmodeled mechanic cut the search short.
            var rootState = rootSession ?? KernelSession.Capture(combat);
            LastNoAction = ClassifyNoAction(rootState, actors, result);
            noActionKinds[LastNoAction] = noActionKinds.GetValueOrDefault(LastNoAction) + 1;
            ReportSearchLine($"search-complete: action=none/{LastNoAction}, wall={Environment.TickCount64 - planningStartMs}ms, "
                + $"engine={KernelTeamSearch.EngineMarker}, "
                + $"compute={computeMs:F1}ms, mode={(humansFinished ? "finished" : "live")}, nodes={result.ExpandedNodes}, "
                + $"stop={result.StopReason}, boundaries={result.Boundaries.Count}, root={rootMetrics?.Score:F0}, best={result.Score:F0}");
            EndOpeningEpisode();
            Reset(); return Status.Fallback;
        }
        catch (Exception error)
        {
            // Only a real failure disables the kernel for the fight; ordinary root
            // movement must not.
            fallbackException++;
            Log.Warn($"CoopBots kernel fallback for this combat: {error.GetBaseException()}");
            Reset(); disabledCombat = combat; return Status.Fallback;
        }
    }
    // Nothing playable is the benign answer and has to be checked on the live
    // root, not inferred from the node count: the search expands end-turn and
    // potion lines even when no card can be paid for, so a large node count can
    // still mean "the hand was empty". A boundary is worth naming because it is
    // the one kind the model can be extended to fix; everything else is an
    // evaluation gap.
    private static NoActionKind ClassifyNoAction(KernelSession root, IReadOnlyList<Player> actors,
        KernelTeamSearch.Result result)
    {
        var interesting = result.Boundaries.Count > 0 ? NoActionKind.Boundary : NoActionKind.Tie;
        try
        {
            foreach (var player in actors.Where(p => p.Creature.IsAlive))
            {
                var potions = root.UsablePotions(player);
                if (potions.Count > 0 && root.PotionTargets(potions[0]).Count > 0) return interesting;
                foreach (var card in root.Hand(player))
                    if (root.CanPlay(card)) return interesting;
            }
            return NoActionKind.Idle;
        }
        catch
        {
            // A probe that fails here must not change the outcome of the verdict.
            return interesting;
        }
    }

    // The live stamp is the authoritative "did anything the branch depends on
    // move" check. When only the notification counter moved, the discarded work
    // was avoidable; when the stamp differs, the root really changed.
    /// <summary>
    /// Why the first action of a completed search cannot be submitted to the live board,
    /// or "" when it can. One clause per named reason so a live log answers the question
    /// instead of only counting it.
    /// </summary>
    /// <summary>
    /// One structured line naming which guard ended the kept script. Reads only — it
    /// re-checks the same facts the guards use rather than duplicating their control
    /// flow, so it cannot drift out of sync with them.
    /// </summary>
    private void ReportPlanStepRefusal(CombatState combat, IReadOnlyList<Player> actors,
        KernelTeamSearch.Result result)
    {
        try
        {
            if (planIndex >= result.Actions.Count)
            {
                Report($"plan step refused: index={planIndex}/{result.Actions.Count} reason=plan-exhausted");
                return;
            }
            // THE ROUND-DRIFT GUARD, named first because TryEmitFromPlan checks it first.
            // It refuses when the plan has crossed from its own round into a later one without
            // that crossing having been validated. It is NOT one of the three sentinel
            // counters, so a refusal from it fell through to the descriptive labels below and
            // printed the card as `playable` — the card WAS in hand and targetable, so the line
            // read like the diagnostic contradicting itself. Measured live 2026-09-20 (round 8,
            // LOUSE_PROGENITOR_NORMAL):
            //   plan step refused: index=4/20 card=WHIRLWIND playable seat_eligible=True
            //   end_turn=False round=2/plan_round=1 boundary=none sentinel_states=20 risk=True
            // with the summary reading `planRefusals=1(drift=0,pending=0,missing=0)` — every
            // counter saying "nothing went wrong" and no field saying which guard fired. That
            // is the exact ambiguity this reporter exists to remove, so name it.
            if (!AllowsRoundDrift(combat.RoundNumber, planRound,
                    BoundaryTextAt(planIndex) is not null, ContinuationSource?.ValidatedRoundDelta ?? 0))
            {
                ReportSearchLine($"plan step refused: index={planIndex}/{result.Actions.Count} "
                    + $"card={result.Actions[planIndex].Card?.Id.Entry ?? "-"} reason=round-drift "
                    + $"round={combat.RoundNumber}/plan_round={planRound} "
                    + $"validated_delta={ContinuationSource?.ValidatedRoundDelta ?? 0} "
                    + $"boundary_here={(BoundaryTextAt(planIndex) is null ? "no" : "yes")} "
                    // The recorded crossing indices, because "boundary_here=no" on its own cannot
                    // tell "the plan crossed EARLIER than this index and the entry should have
                    // been hit" from "the plan crosses LATER and the live round moved early".
                    // Those two need opposite fixes, and round 8 could not separate them.
                    + $"boundaries=[{string.Join(",", planBoundaries.Select(b => b.Index))}]");
                return;
            }
            var next = result.Actions[planIndex];
            // POTION STEPS. Everything below labels a step by its CARD state, so a potion step
            // could only ever be described as `no-card` — which reads as "the bottle was not in
            // the belt" but is equally the label for a step that has neither card nor potion nor
            // end-turn. Seven different guards produce that one word. Measured live 2026-09-20
            // (round 8, SOUL_FYSH_BOSS):
            //   plan step refused: index=5/30 card=WEAK_POTION no-card seat_eligible=True
            //   end_turn=False round=2/plan_round=1 boundary=none sentinel_states=30 risk=True
            // Nothing in that line says whether the bottle was gone, queued, unusable, or its
            // target had gone invalid — so the fix could not even be scoped.
            if (next.Potion is { } wantedPotion)
            {
                var why = !actors.Contains(next.Player) ? "seat-not-eligible"
                    : next.Player.PlayerCombatState is null ? "no-combat-state"
                    : FindLivePotion(next.Player, wantedPotion) is not { } live ? "potion-not-held"
                    : live.IsQueued ? "potion-queued"
                    : live.HasBeenRemovedFromState ? "potion-removed"
                    : !live.PassesCustomUsabilityCheck ? "potion-not-usable"
                    : !live.IsValidTarget(next.Target) ? "potion-target-invalid"
                    : "";
                if (why.Length > 0)
                {
                    ReportSearchLine($"plan step refused: index={planIndex}/{result.Actions.Count} "
                        + $"potion={wantedPotion.Id.Entry} reason={why} "
                        + $"target={(next.Target is null ? "none" : next.Target.CombatId.ToString())} "
                        + $"round={combat.RoundNumber}/plan_round={planRound} "
                        + $"belt=[{string.Join(",", next.Player.Potions.Select(p => p.Id.Entry))}]");
                    return;
                }
                // The bottle IS usable here, so the refusal came from a check the potion branch
                // reaches after those — fall through and let the existing labels describe it.
            }
            // Identity, not instance — the same resolution the guard itself uses. Testing
            // `hand.Contains(next.Card)` reported "card-left-hand" for every generated card
            // (a Shiv's planned model is a simulation object that is never in the live
            // hand), so the diagnostic disagreed with the guard it exists to explain.
            var resolved = next.Card is not { } plannedCard
                ? null
                : KernelSession.FindLiveCardByKey(next.Player, next.CardStateKey, next.CardStateOccurrence)
                    ?? (next.Player.PlayerCombatState?.Hand.Cards.Contains(plannedCard) == true ? plannedCard : null);
            var cardState = next.Card is null
                ? "no-card"
                : resolved is null
                    ? "card-left-hand"
                    : resolved.CanPlayTargeting(next.Target)
                        ? "playable"
                        : "not-playable:" + PlayRefusalDetail(resolved, next.Target, next.Player);
            // NOT the throttled Report(): a fight can refuse a plan two dozen times, and a
            // 5-second throttle swallowed every one of them — leaving `planRefusals=24`
            // with no way to say which guard fired. One line per refusal is the whole point
            // of this diagnostic.
            ReportSearchLine($"plan step refused: index={planIndex}/{result.Actions.Count} "
                + $"card={next.Card?.Id.Entry ?? next.Potion?.Id.Entry ?? (next.EndTurn ? "end-turn" : "-")} {cardState} "
                + $"seat_eligible={actors.Contains(next.Player)} end_turn={next.EndTurn} "
                + $"round={combat.RoundNumber}/plan_round={planRound} "
                + $"boundary={(BoundaryTextAt(planIndex) is null ? "none" : "expected")} "
                + $"sentinel_states={planActionStates.Count} risk={result.HasUncertainRisk}"
                // Name the FIRST risky step on the path. Without it a refused end-turn only
                // says "risk=True" and there is no way to tell which card or hook to model.
                + (result.RiskDetail.Length == 0 ? "" : $" risk_first={result.RiskDetail}"));
        }
        catch (Exception error)
        {
            ReportSearchLine($"plan step refused: index={planIndex}/{result.Actions.Count} diagnostic-failed={error.GetType().Name}");
        }
    }

    /// <summary>
    /// The live bottle a planned potion names, or null when the player no longer holds
    /// one with that id.
    /// </summary>
    /// <remarks>
    /// Upstream re-resolves the bottle by SLOT and then compares `potion.Id.Entry` to
    /// `action.PotionId` (SolverController.cs:2693-2721). A plan here carries the
    /// simulated PotionModel rather than a slot, so the same check has to match on id —
    /// but the failure it catches is not hypothetical: the planned model is a simulation
    /// object, and asking IT `IsQueued` / `HasBeenRemovedFromState` /
    /// `PassesCustomUsabilityCheck` answers questions about a bottle that is not in the
    /// player's inventory at all.
    /// </remarks>
    private static PotionModel? FindLivePotion(Player player, PotionModel planned)
        => player.Potions.FirstOrDefault(potion =>
            string.Equals(potion.Id.Entry, planned.Id.Entry, StringComparison.Ordinal));

    /// <summary>
    /// Why the game says this card cannot be played at this target, in the field shape
    /// upstream logs on a failed deployment (SolverController.cs:2741-2747).
    /// </summary>
    /// <remarks>
    /// "not playable" alone cannot separate out-of-energy from a target that died from a
    /// card-specific rule such as Normality's play limit, and those need different fixes.
    /// `target_valid` / `can_play` / `reason` / `preventer` come from the game's own
    /// predicates rather than being re-derived here, so the line cannot drift from
    /// whatever actually blocked the play.
    /// </remarks>
    private static string PlayRefusalDetail(CardModel card, Creature? target, Player player)
    {
        var state = player.PlayerCombatState;
        var cost = card.EnergyCost.CostsX
            ? "X"
            : card.EnergyCost.GetWithModifiers(CostModifiers.Local).ToString();
        var targetValid = card.IsValidTarget(target);
        var playable = card.CanPlay(out UnplayableReason reason, out AbstractModel? preventer);
        return $"cost={cost}/energy={state?.Energy ?? -1}"
            + $"/target={(target?.CombatId?.ToString() ?? "none")}"
            + $"/target_valid={targetValid}/can_play={playable}/reason={reason}"
            + $"/preventer={preventer?.Id.Entry ?? "-"}"
            + $"/stars={state?.Stars ?? -1}/star_cost={card.GetStarCostWithModifiers()}";
    }

    private static string RefuseFirstAction(KernelTeamSearch.Action first, IReadOnlyList<Player> actors)
    {
        if (!actors.Contains(first.Player)) return "seat-not-eligible";
        if (first.Card is null) return "no-card";
        var state = first.Player.PlayerCombatState;
        // Identity, not instance: a generated card's planned CardModel is a simulated object
        // and is never present in the live hand (see KernelSession.FindLiveCardByKey).
        var card = KernelSession.FindLiveCardByKey(first.Player, first.CardStateKey, first.CardStateOccurrence);
        if (card is null && first.Card is { } firstInstance
            && state?.Hand.Cards.Contains(firstInstance) == true)
            card = firstInstance;  // no key recorded on this action: fall back to the instance
        if (card is null) return "card-left-hand";
        // The DETAIL matters and was missing: "card-not-playable" fired 19 times in one
        // fight with no way to tell whether it was energy, a target that died, or a
        // card-specific rule. Cost and current energy separate the first two; the target
        // is included so a dead/changed target is visible too.
        if (!card.CanPlayTargeting(first.Target))
        {
            return $"card-not-playable:{card.Id.Entry}/{PlayRefusalDetail(card, first.Target, first.Player)}";
        }
        return "";
    }

    /// <summary>
    /// The opening episode is over: later searches in this fight are repairs and go back
    /// to the per-tier budget.
    ///
    /// Called from every path that CONSUMED a completed search without deploying it. It
    /// used to be set only when a plan was deployed, so a plan that was refused left
    /// `openingSearch` true and the next tick started another five-minute, 200k-node
    /// search — measured live as one full-budget search before every card, about two
    /// minutes apart, each one refused for the same reason as the last.
    /// </summary>
    private void EndOpeningEpisode() => openingSearch = false;

    private void CountStale(CombatState combat)
    {
        try
        {
            if (stamp.Length > 0 && KernelSession.CaptureLiveStamp(combat) == stamp) fallbackStaleNoise++;
            else fallbackStaleRoot++;
        }
        catch { fallbackStaleRoot++; }
    }

    // Concise completion line so a slow turn can be attributed: the search wall
    // is separate from the engine's animation and action-queue waits. Report
    // rate-limits to one line per five seconds, so this cannot spam per frame.
    // Measurement only — nothing here changes behaviour. `turnPrefix` is how much of
    // the returned sequence belongs to the current turn, which is the run a committed
    // deploy could take without playing against a simulated enemy turn: the search
    // crosses a turn boundary by emitting an EndTurn action, so everything before the
    // first one is safe and everything after it is a forecast. Our Action record has no
    // Turn field (CombatSolver's PlanAction does), so the boundary is positional.
    // Read it before deciding whether committing is worth building: a prefix of 1 means
    // the rebuid would save nothing, a prefix of 3-4 means it removes most searches.
    private static string Live(string branch, int at)
        => branch.Substring(Math.Max(0, at - 20), Math.Min(60, Math.Max(0, branch.Length - Math.Max(0, at - 20))));

    /// <summary>
    /// May a step whose script was captured in an earlier round be played?
    /// </summary>
    /// <param name="boundaryHere">A turn-boundary prediction is recorded at this index,
    /// which is the only place a crossing can be validated.</param>
    /// <param name="validatedRoundDelta">How many rounds of drift have already had a
    /// boundary validated against the live board.</param>
    /// <remarks>
    /// Extracted on 2026-09-20 so it can be asserted directly. The live bug was a
    /// PREDICATE, not a state machine, and a predicate is the cheapest thing in this file
    /// to pin: the old form demanded a boundary entry at every index, which passes only on
    /// the crossing index and refuses every step after it — the round stays changed for the
    /// rest of the turn, and a script carries one entry per crossing, not one per step.
    /// </remarks>
    internal static bool AllowsRoundDrift(int liveRound, int planRound, bool boundaryHere, int validatedRoundDelta)
        => boundaryHere || liveRound - planRound <= validatedRoundDelta;

    /// <summary>
    /// Emits the next step of the kept plan without searching. Returns false — which
    /// makes the caller drop the plan and fall through to a fresh search — as soon as
    /// the next action can no longer be taken as written.
    ///
    /// This mirrors CombatSolver's deploy loop, which re-checks each planned card at
    /// submission and treats a refusal as deployment drift rather than trusting the
    /// plan. The check is per action, not a state comparison: the board has already
    /// moved because of our own previous action, so an unchanged fingerprint is neither
    /// available nor the right test. What must hold is that the seat still acts and the
    /// card is still in hand and still playable — which is exactly the condition the
    /// deploy path enforced before the plan was kept.
    ///
    /// Stops at the turn boundary. Actions after an EndTurn were computed against a
    /// simulated enemy turn, and the real enemy turn is played by the game, not by us;
    /// reusing across that boundary needs the predicted state to compare against and is
    /// deliberately not attempted here.
    /// </summary>
    /// <summary>
    /// The plan's predicted state text for the turn starting at this action index, or
    /// null when that index is not a turn boundary. Null and "" mean different things
    /// on purpose: null is "no boundary here, nothing to check", "" is "this is a
    /// boundary the search could not describe" — and the caller refuses the second.
    /// </summary>
    private string? BoundaryTextAt(int index)
    {
        foreach (var (boundaryIndex, text) in planBoundaries)
            if (boundaryIndex == index) return text;
        return null;
    }

    private bool TryEmitFromPlan(CombatState combat, IReadOnlyList<Player> actors, bool humansFinished,
        out TeamCombatPlanner.Decision? decision, out bool waitingForStep)
    {
        decision = null;
        waitingForStep = false;
        if (plan is not { } result) return false;
        // Full-bot tables only. A human playing between two of our cards changes the
        // board in a way this plan never modelled — the same staleness the old
        // re-search-per-card existed to catch — and none of the checks below can see
        // it: the planned card is still in hand and still playable, it has just stopped
        // being the right card. The kernel's own staleness check does not cover a kept
        // plan either, since a human action moves queueVersion rather than the search.
        // With every seat driven there is no such action, which is the only case where
        // replaying a plan across several cards is safe.
        if (!combat.Players.All(player => AutoPilot.Drives(player.NetId))) return false;
        if (!ReferenceEquals(planCombat, combat)) return false;
        // A plan captured in an earlier ROUND may only continue when it recorded a
        // prediction for the action we are about to take — that prediction is what the
        // live board is compared against a few lines below. Requiring one keeps the old
        // safety for plans that never crossed a turn, while letting a cross-turn plan
        // survive the round change it was built for.
        //
        // Refusing on `planRound != combat.RoundNumber` unconditionally is what made
        // "one search, then play the line" impossible: the round number advances the
        // moment the first turn ends, so the plan was dropped at its first boundary and
        // the next turn searched again from scratch. Measured live, reuse ran 2/31…10/31
        // and never past 13/31 — exactly that plan's first turn boundary.
        // A step in a later round is allowed only once that crossing's prediction has been
        // validated — which the boundary check further down does, and marks. See
        // KernelContinuation.ValidatedRoundDelta: demanding a boundary entry at THIS index
        // passed on the crossing index and refused every step after it, because the round
        // stays changed for the rest of the turn while entries exist only at crossings.
        if (!AllowsRoundDrift(combat.RoundNumber, planRound,
                BoundaryTextAt(planIndex) is not null, ContinuationSource?.ValidatedRoundDelta ?? 0)) return false;
        if (planIndex >= result.Actions.Count) return false;
        // L1: the step already played must have landed exactly where the search said it
        // would. The turn-boundary check below only fires when the plan CROSSES a turn,
        // so a step that resolved differently inside a turn used to go unnoticed for the
        // rest of that turn — while every later action in the script had already been
        // chosen for a board that never existed. Refusing here drops the plan and hands
        // the tick to the legacy planner, exactly like any other refusal, and the log
        // names the first differing field instead of only that something differed.
        if (planLastExecuted >= 0 && !planSentinelInvalidated)
        {
            if (planActionStates.Count <= planLastExecuted)
            {
                // Attenuate; do NOT block. The finished path could not be replayed, so
                // this step has no prediction to compare against — but the turn-boundary
                // checks below still guard every crossing. Refusing outright made the
                // whole feature fall back to re-searching when one step failed to replay:
                // a live fight rejected a 51-action route at step 0/51 and paid 18
                // re-searches for it. Unverified is weaker than verified; it is not the
                // same as wrong, and the boundary checks still apply.
                planSentinelMissing++;
                Report($"plan sentinel missing after step {planLastExecuted}/{result.Actions.Count}: "
                    + $"states={planActionStates.Count} [{result.ReplayFailure}] — step check skipped, boundary checks still apply");
            }
            else
            {
                var previousPlayer = result.Actions[planLastExecuted].Player;
                // Both ends are the WHOLE TABLE's text (see KernelSession.PartyStateText),
                // so step planIndex-1 must have left `After` behind and must have started
                // from `Before` — no viewpoint to match, and no seat's numbers compared
                // against another's. That mistake produced the bogus field=hp 55/66/59 and
                // gold 249/99 drifts when the sentinel was per-seat.
                var (before, after) = planActionStates[planLastExecuted];
                var landed = KernelSession.FirstLiveDifference(combat, after);
                if (landed is not null)
                {
                    if (KernelSession.FirstLiveDifference(combat, before) is null)
                    {
                        // The board still sits exactly where that step started, so the step
                        // has not been applied yet — a poll between submit and resolve, not a
                        // drifted plan. Upstream AWAITS the action here; we ask the caller to
                        // come back next frame with the script intact.
                        if (++planSentinelPendingTicks <= MaxSentinelPendingTicks)
                        {
                            planSentinelPending++;
                            waitingForStep = true;
                            return false;
                        }
                        // Something stopped resolving. Say so and carry on rather than
                        // spinning Pending forever with the bots idle.
                        ReportSearchLine($"plan step never resolved after {planSentinelPendingTicks} ticks "
                            + $"(step {planLastExecuted}/{result.Actions.Count}, seat {previousPlayer.NetId}); continuing");
                    }
                    // LOGGED, NOT REFUSED — this is the upstream behaviour, and it is
                    // deliberate. Upstream compares state text at exactly two QUIESCENT
                    // points: the root capture (CombatRootSnapshot) and turn start
                    // (TryCreateContinuation, `validation=exact_state_text`). After every
                    // action it only LOGS the live state (DeployCurrentTurn:2793-2807
                    // DEPLOY_STATE) and never re-plans on a difference; it re-plans on
                    // whether the step can be EXECUTED at all (IsSamePlayableTurn,
                    // potion_missing/mismatch, card_unplayable).
                    //
                    // Comparing after every action instead pulled turn-LOCAL counters into
                    // the verdict — the Y= field (status cards drawn / zero-cost attack
                    // starts / card-play starts / attack-skill starts THIS TURN) is
                    // mid-flight by construction, and a one-tick difference in when either
                    // side increments it refuses a script whose board is perfectly
                    // correct. Measured on SLIMES_WEAK 2026-09-20: `field=Y expected=
                    // {0/1/3/2} actual={0/0/0/0}` at step 13/134, the only drift in the
                    // fight, dropping a 134-action route the search had found a terminal
                    // for.
                    planSentinelPendingTicks = 0;
                    planDriftDetected++;
                    ReportSearchLine($"plan step drift (logged, not refused): after step "
                        + $"{planLastExecuted}/{result.Actions.Count} (seat {previousPlayer.NetId}): {landed}");
                }
                else
                {
                    planSentinelPendingTicks = 0;
                }
            }
        }
        // SKIP steps whose seat cannot act right now.
        //
        // A script is priced for the seats that were ALIVE and waiting when the search ran. In a
        // four-seat fight one of them can die mid-script, and every remaining step of that seat is
        // then unplayable — a dead seat has no hand, so the step reads `card-left-hand`. That used
        // to be a hard refusal, which threw away the WHOLE script including the surviving seats'
        // perfectly playable steps. Measured live 2026-09-20 (CEREMONIAL_BEAST_BOSS, A10 4-bot):
        // three seats died to STOMP_MOVE and the survivor's script was dropped with
        //   `plan step refused: index=19/32 card=EXPECT_A_FIGHT card-left-hand seat_eligible=False`
        // — a seat-eligibility failure, not a plan violation. R4: only EXECUTABILITY may refuse,
        // and the surviving seats' steps are still executable.
        //
        // Skipping invalidates the sentinel: every later prediction was made for a board where the
        // skipped step HAD happened, so comparing it is not evidence of drift. Say so ONCE rather
        // than emitting one drift line per remaining step (the over-reading that turned 7 differing
        // fields into 72 drift lines in that same fight).
        while (planIndex < result.Actions.Count && !actors.Contains(result.Actions[planIndex].Player))
        {
            var skippedStep = result.Actions[planIndex];
            var skippedSeat = skippedStep.Player.NetId;
            var skippedAlive = skippedStep.Player.Creature.IsAlive;
            planIndex++;
            seatStepsSkipped++;
            if (!planSentinelInvalidated)
            {
                planSentinelInvalidated = true;
                ReportSearchLine($"plan step skipped: seat {skippedSeat} cannot act (alive={skippedAlive}); "
                    + $"skipping to step {planIndex + 1}/{result.Actions.Count} and standing the sentinel down "
                    + "— later predictions were made for a board where this step happened");
            }
            else
            {
                Report($"plan step skipped: seat {skippedSeat} cannot act (alive={skippedAlive}); "
                    + $"now at step {planIndex + 1}/{result.Actions.Count}");
            }
        }
        // THE TURN-CROSSING CHECK RUNS FOR EVERY STEP, not only for card steps.
        //
        // It used to sit inside the CARD branch, so a boundary whose index landed on an EndTurn or
        // a potion never marked the crossing as consumed — and `ValidatedRoundDelta` is written
        // here and nowhere else. In a four-seat fight every round ends with FOUR EndTurns (one per
        // seat), so boundary indices land on EndTurn steps constantly and the delta never advanced:
        // measured live 2026-09-20 (A10 4-bot, SHRINKER_BEETLE_WEAK)
        //   plan step refused: index=19/36 card=POMMEL_STRIKE reason=round-drift round=2/plan_round=1
        //   validated_delta=0 boundary_here=no boundaries=[15,16,17,18,29,31,33,34]
        // — the plan had recorded crossings at 15..18 and the runtime walked straight through
        // them without recording a single one. Single-seat plans have one EndTurn per round, so
        // the boundary index almost always landed on a card and this never showed up in 34
        // single-seat fights.
        //
        // It stays BELOW the L1 sentinel on purpose: the sentinel is what waits for the previous
        // step to resolve, and checking the crossing before that would compare against a board
        // that has not moved yet. It also keeps running after a seat skip invalidates the
        // sentinel — the round guard still needs its crossings marked, and a skipped step does
        // not mean the rounds stopped turning.
        var expected = BoundaryTextAt(planIndex);
        if (expected is not null)
        {
            // LOGGED, NOT REFUSED. This was the last place an UNMODELLED effect could stop the
            // script: the crossing's prediction was taken during the search, so anything the
            // simulator approximated makes it differ from the live board, and the comparison
            // then dropped a route the search had found a terminal for. Measured live
            // 2026-09-20 (CUBEX_CONSTRUCT_NORMAL): a structurally priced CONCOCT put a
            // generated card in the live hand that the plan did not predict, and the plan was
            // refused here — eighteen times in a row.
            //
            // Policy (user decision): execution is never interrupted by anything unmodelled.
            // The step-by-step sentinel and this crossing check are EVIDENCE now; the only
            // refusals left are executability — a card that is not in the hand, cannot be
            // played at that target, a potion that is gone, a seat that cannot act.
            string mismatch;
            try
            {
                mismatch = expected.Length == 0
                    ? "the search recorded no prediction for this boundary"
                    : KernelSession.FirstLiveDifference(combat, expected)
                        ?? "";
            }
            catch (Exception error) { mismatch = $"live-capture-failed {error.GetType().Name}"; }
            if (mismatch.Length > 0)
            {
                planBoundaryMismatch++;
                ReportSearchLine($"plan boundary drift (logged, not refused): at step "
                    + $"{planIndex}/{result.Actions.Count}: {mismatch}");
            }
            // Mark the crossing as consumed EITHER WAY. Validating only on a match would have
            // moved the refusal one step later: the round guard refuses every step in a later
            // round until a crossing is recorded as passed.
            if (ContinuationSource is { } crossed)
                crossed.ValidatedRoundDelta =
                    Math.Max(crossed.ValidatedRoundDelta, combat.RoundNumber - planRound);
        }
        var next = result.Actions[planIndex];
        if (next.EndTurn)
        {
            // The plan's own end-turn, submitted from the plan so the cursor can reach
            // the turn boundary — otherwise the cursor stops one step short of the only
            // place a cross-turn check exists, and the reuse would never cross. Same
            // guards the freshly-searched end-turn branch applies, so a kept plan can
            // never end a turn that path would have refused. Submitted with a null
            // decision on purpose: BotRuntime reads ConfirmedEndTurn for this.
            if (!humansFinished || !actors.Contains(next.Player)) return false;
            // Neither `HasUncertainRisk` nor `Boundaries.Count > 0` gates this crossing any
            // more; both were removed for the same reasons spelled out on the end-turn-first
            // branch above. Briefly: the risk flag is set by the enemy phase itself, so it
            // means "this plan crosses a turn" rather than "the next phase is untrustworthy"
            // — and the boundary dictionary is a SEARCH-WIDE count of end-turn attempts that
            // hit a boundary, including ones on branches the plan never took, so it says
            // nothing about the step being emitted. What actually guards this crossing is
            // below: the real enemy phase is run from the live board (LiveEndTurnRisk) and
            // the plan's own recorded state text for the new turn is compared against the
            // live board before the next action is played (BoundaryTextAt + L1 sentinel).
            // Live risk re-check before committing to the rest of the plan, ported from
            // CombatSolver's LiveEndTurnRiskEvaluator. Everything after this end-turn was
            // computed against a SIMULATED enemy phase; run that phase for real from the
            // live board and refuse when it kills more seats than the plan expected. The
            // refusal drops the plan and the next tick searches from the real board, so
            // the worst case is the old re-search-per-turn behaviour — never a plan
            // carried forward on a phase that does not match.
            //
            // Deaths rather than raw HP on purpose: comparing a one-phase live figure
            // against the plan's multi-round projection would be two different units, and
            // a threshold that fires on HP could refuse forever on a fight that genuinely
            // loses HP every turn.
            // MEASURED, NOT ENFORCED. Upstream runs LiveEndTurnRiskEvaluator only under
            // FullAuto and only as an unattended-test kill switch — it compares HP loss
            // (not deaths) and its action is to stop the auto-pilot, NOT to refuse this
            // step (SolverController.cs:2837-2870). Enforcing it here was our addition, and
            // it is a second way for an invented rule to overrule the search's own
            // conclusion about a line the search already priced.
            //
            // The measurement stays: it is the only reading of "does the coming enemy
            // phase actually cost what the plan said", and it is worth a log line.
            var risk = LiveEndTurnRisk.Evaluate(combat, actors, searchRounds);
            if (!risk.Simulated)
            {
                endTurnRiskUnsimulated++;
                ReportSearchLine($"plan end-turn risk not simulated (round {combat.RoundNumber}); "
                    + "the step proceeds — upstream treats an unsimulatable phase as no evidence, not as a refusal");
            }
            else if (risk.Deaths > planProjectedDeaths)
            {
                endTurnRiskDeaths++;
                ReportSearchLine($"plan end-turn measured risk: the live enemy phase kills {risk.Deaths} seat(s), "
                    + $"the plan expected {planProjectedDeaths} (hpLost={risk.HpLost:F0}); logged, not refused");
            }
            ConfirmedEndTurn = next.Player;
            noActionStamp = "";
            planLastExecuted = planIndex;
            planIndex++;
            plans++;
            ReportSearch("end-turn(plan)", result, humansFinished);
            return true;
        }
        // A potion is a normal step of the line, exactly as it is upstream
        // (PlanActionKind.UsePotion, with retention seats of its own). It is a FREE action,
        // so the steps after it were computed against a board where it had already
        // resolved — refusing it here threw the whole tail away and made any route that
        // drank mid-plan unplayable.
        //
        // Delivered through ConfirmedPotion rather than `decision`: TeamCoordinator.Select
        // merges `joint` with a separate potion argument, and it only reaches that argument
        // when joint is null. So a potion step leaves `decision` unset on purpose.
        if (next.Potion is { } nextPotion)
        {
            if (!actors.Contains(next.Player)) return false;
            if (next.Player.PlayerCombatState is null) return false;
            var livePotion = FindLivePotion(next.Player, nextPotion);
            if (livePotion is null
                || livePotion.IsQueued
                || livePotion.HasBeenRemovedFromState
                || !livePotion.PassesCustomUsabilityCheck
                || !livePotion.IsValidTarget(next.Target))
            {
                return false;
            }
            ConfirmedPotion = new PotionPlan(livePotion, next.Target,
                $"kernel-plan:reuse,potion:{livePotion.Id.Entry},step:{planIndex + 1}/{result.Actions.Count}");
            planLastExecuted = planIndex;
            planIndex++;
            noActionStamp = "";
            return true;
        }
        if (next.Card is null) return false;
        if (!actors.Contains(next.Player)) return false;
        // Crossing a turn boundary is the one step whose action was computed against a
        // SIMULATED enemy turn, and the real enemy turn is played by the game. Compare
        // the live board against what the plan predicted for that moment before playing
        // past it; a mismatch drops the plan and the next tick searches from the real
        // board. A plan with no recorded prediction refuses the crossing rather than
        // trusting it — that is the difference between "no reuse" and "wrong reuse".
        // Resolve the planned card by IDENTITY against the live hand, the way upstream's
        // FindCardForDeployment does. Instance matching (`Hand.Cards.Contains`) failed for
        // generated cards: their planned CardModel is a simulated object that is never in
        // the live hand, so every step through a Shiv was refused as card-left-hand.
        var card = KernelSession.FindLiveCardByKey(next.Player, next.CardStateKey, next.CardStateOccurrence);
        if (card is null && next.Card is { } nextInstance
            && next.Player.PlayerCombatState?.Hand.Cards.Contains(nextInstance) == true)
            card = nextInstance;   // no key recorded on this action: fall back to the instance
        if (card is null) return false;
        if (!card.CanPlayTargeting(next.Target)) return false;

        planLastExecuted = planIndex;
        planIndex++;
        // The reason string is the marker: it lands in the same `CoopBots team: ... plays`
        // line as everything else, so a plain grep over a log tells us how much of the
        // fight came from the kept plan versus a fresh search.
        decision = new TeamCombatPlanner.Decision(next.Player,
            new(card, next.Target, 0.0,
                $"kernel-plan:reuse,step:{planIndex}/{result.Actions.Count}", next.Choices),
            result.Actions.Count - planIndex);
        return true;
    }

    /// <summary>One-word ending label for the potion-pass diagnostic.</summary>
    private static string Describe(TerminalRecord? record) =>
        record is null ? "none"
        : record.Victory ? "VICTORY"
        : record.Verified ? "WIPE"
        : record.CutoffReason;

    /// <summary>
    /// Ask the roll-out tournament for this decision. Returns false — leaving the caller to the
    /// existing planner — whenever anything at all is off: a human in the party, no legal
    /// action, a card the live hand cannot resolve, or an exception. A driving path that can
    /// stall a turn is worse than the thing it replaces, so every exit here is a fallback.
    /// </summary>
    /// <summary>
    /// How much COMPUTE the tournament may do per frame before giving the frame back.
    ///
    /// The tournament used to run 500-1200 ms in one call on the main thread, which is longer
    /// than a card-play animation, so the animation froze mid-flight and the mod looked buggy
    /// (measured live 2026-09-22: `Completed execution of action` immediately followed by
    /// `decision-ms=1003.1`). A frame at 60 fps has ~16.7 ms; taking ~6 of them leaves the rest
    /// for the game, so the animation stays smooth.
    ///
    /// WHAT IT COSTS: the same rollouts now span more wall time — a decision whose compute was
    /// 400 ms takes roughly 400/6 frames ≈ 1.1 s. That is the trade the user asked for
    /// ("打一张牌时多思考一会可以接受，动画不能卡"). Raise it for faster decisions, lower it for
    /// an even smoother frame.
    /// </summary>
    private const int TournamentSliceMs = 6;


    /// <summary>Whether a sliced tournament produced an answer this tick.</summary>
    private enum TournamentOutcome { Pending, Decided, Declined }

    /// <summary>Where the sliced tournament is between its (up to) two passes.</summary>
    private enum TournamentPhase { Idle, NoPotions, Potions }

    private TournamentPhase tournamentPhase;
    private TournamentRun? tournamentRun;
    private TournamentResult? tournamentFirstPass;
    private KernelSession? tournamentRoot;
    private IReadOnlyList<Player>? tournamentParty;
    private IReadOnlyList<Player>? tournamentActors;
    private TournamentOptions? tournamentOptions;
    private string? tournamentPoint;
    private Stopwatch? tournamentWatch;

    /// <summary>Drop anything in flight. Called when the board moves and on a new fight.</summary>
    private void ClearTournament()
    {
        tournamentPhase = TournamentPhase.Idle;
        tournamentRun = null;
        tournamentFirstPass = null;
        tournamentRoot = null;
        tournamentParty = null;
        tournamentActors = null;
        tournamentOptions = null;
        tournamentWatch = null;
    }

    /// <summary>
    /// True while the cached tournament line still has a step to play.
    /// </summary>
    private bool HasTournamentScript =>
        tournamentScript is { Count: > 0 } && tournamentScriptIndex < tournamentScript.Count;

    /// <summary>
    /// The boundary the stored-line feature was specified with: a fresh ending only displaces a
    /// stored one when STRICTLY better. Kept as a named, tested predicate so the rule itself
    /// cannot drift. The live planner no longer runs a fresh tournament per step to evaluate it
    /// (that per-step re-search was the 2026-09-23 mixed-table stutter); it follows the stored
    /// line, and only searches fresh once that line can no longer be executed.
    /// </summary>
    internal static bool FreshEndingDoesNotBeat(TerminalRecord? fresh, TerminalRecord? script)
        => script is not null && (fresh is null || TerminalComparer.Compare(fresh, script) <= 0);

    /// <summary>
    /// Adopt the winning roll-out as the running script. Index 1 because the caller is about
    /// to submit index 0; a one-action result has no tail worth keeping, so it clears instead.
    /// </summary>
    private void AdoptTournamentScript(TournamentResult result, CombatState combat)
    {
        if (result.Script is { Count: > 1 } script
            && result.ScriptAliveStates is { Count: > 1 } aliveStates
            && aliveStates.Count == script.Count
            && result.Record is not null)
        {
            tournamentScript = script;
            tournamentScriptRecord = result.Record;
            tournamentScriptIndex = 1;
            tournamentScriptCombat = combat;
            tournamentScriptAliveStates = aliveStates;
            ReportSearchLine($"tournament script cached: steps={script.Count} "
                + $"ending={Describe(result.Record)}");
            return;
        }
        ClearTournamentScript("the winning result has no multi-step tail");
    }

    private void ClearTournamentScript(string reason)
    {
        if (tournamentScript is null) return;
        ReportSearchLine($"tournament script dropped: {reason}");
        tournamentScript = null;
        tournamentScriptRecord = null;
        tournamentScriptIndex = 0;
        tournamentScriptCombat = null;
        tournamentScriptAliveStates = null;
    }

    private static ulong[] LivingSeats(CombatState combat)
        => combat.Players.Where(player => player.Creature.IsAlive)
            .Select(player => player.NetId).OrderBy(id => id).ToArray();

    private static bool SameSeats(ulong[] a, ulong[] b)
        => a.Length == b.Length && a.SequenceEqual(b);

    /// <summary>
    /// Emit the cached script's next step when it is still executable. A refusal drops the
    /// script and returns false, which lets the caller use the fresh tournament answer instead.
    ///
    /// ONE refusal does NOT drop the script: an end-turn step on a mixed table is still valid,
    /// it just cannot be submitted until every human has ended their turn (BotRuntime refuses
    /// it earlier). That leaves the script and cursor untouched and returns false, which the
    /// caller reads as "park until the humans finish" rather than "re-search everything".
    /// </summary>
    private bool TryEmitFromTournamentScript(CombatState combat, IReadOnlyList<Player> actors,
        bool humansFinished, out TeamCombatPlanner.Decision? decision)
    {
        decision = null;
        if (tournamentScript is not { } script) return false;
        if (!ReferenceEquals(tournamentScriptCombat, combat))
        {
            ClearTournamentScript("the combat changed");
            return false;
        }
        // EACH SCRIPT STEP CARRIES ITS OWN LIVING-SEAT PREDICTION. A script is allowed to plan a
        // death: the next step may legitimately expect a seat to be gone. Continue only while the
        // live set matches that step's prediction; a death earlier/later than the script planned,
        // or a different seat, drops the script and lets the fresh tournament re-plan.
        if (tournamentScriptAliveStates is not { } aliveStates
            || tournamentScriptIndex >= aliveStates.Count
            || !SameSeats(aliveStates[tournamentScriptIndex], LivingSeats(combat)))
        {
            ClearTournamentScript("the live living-seat set no longer matches the script step");
            return false;
        }
        if (tournamentScriptIndex >= script.Count)
        {
            ClearTournamentScript("exhausted");
            return false;
        }
        var next = script[tournamentScriptIndex];
        if (!actors.Contains(next.Player) || !next.Player.Creature.IsAlive)
        {
            ClearTournamentScript("the next planned seat can no longer act");
            return false;
        }
        var stepLabel = $"{tournamentScriptIndex + 1}/{script.Count}";
        if (next.EndTurn)
        {
            if (!actors.Contains(next.Player)) { ClearTournamentScript("end-turn seat is inactive"); return false; }
            if (!TournamentCanEndTurn(combat, humansFinished))
            {
                // KEEP THE LINE. Live 2026-09-23 mixed table: this one guard dropped 180
                // valid stored lines, each drop followed by a full fresh tournament (both
                // passes) whose only finding was the same WIPE — that churn is the stutter.
                // The step is valid; only its submission is gated on the humans' end-turn.
                return false;
            }
            ConfirmedEndTurn = next.Player;
            noActionStamp = "";
            tournamentScriptIndex++;
            tournamentDriven++;
            ReportSearchLine($"tournament script: end-turn step {stepLabel}");
            return true;
        }
        if (next.Potion is { } wantedPotion)
        {
            if (!actors.Contains(next.Player) || next.Player.PlayerCombatState is null)
            {
                ClearTournamentScript("potion seat is inactive");
                return false;
            }
            var livePotion = FindLivePotion(next.Player, wantedPotion);
            if (livePotion is null
                || livePotion.IsQueued
                || livePotion.HasBeenRemovedFromState
                || !livePotion.PassesCustomUsabilityCheck
                || !livePotion.IsValidTarget(next.Target))
            {
                ClearTournamentScript("the planned potion is no longer usable");
                return false;
            }
            ConfirmedPotion = new PotionPlan(livePotion, next.Target,
                $"kernel-tournament-script:potion:{livePotion.Id.Entry},step:{stepLabel}");
            noActionStamp = "";
            tournamentScriptIndex++;
            tournamentDriven++;
            ReportSearchLine($"tournament script: potion {livePotion.Id.Entry} step {stepLabel}");
            return true;
        }
        if (next.Card is null)
        {
            ClearTournamentScript("the cached step is neither a card, a potion, nor end-turn");
            return false;
        }
        if (!actors.Contains(next.Player)) { ClearTournamentScript("the cached step's seat is inactive"); return false; }
        var card = KernelSession.FindLiveCardByKey(next.Player, next.CardStateKey, next.CardStateOccurrence);
        if (card is null && next.Card is { } instance
            && next.Player.PlayerCombatState?.Hand.Cards.Contains(instance) == true)
        {
            card = instance;
        }
        if (card is null || !card.CanPlayTargeting(next.Target))
        {
            ClearTournamentScript("the planned card is no longer playable");
            return false;
        }
        tournamentScriptIndex++;
        tournamentDriven++;
        decision = new TeamCombatPlanner.Decision(next.Player,
            new(card, next.Target, 0.0,
                $"kernel-tournament-script:step:{tournamentScriptIndex}/{script.Count}",
                next.Choices),
            script.Count - tournamentScriptIndex);
        ReportSearchLine($"tournament script: play {card.Id.Entry} step {stepLabel}");
        return true;
    }

    private bool BeginTournamentPhase(CombatState combat, IReadOnlyList<Player> actors,
        bool humansFinished)
    {
        if (tournamentPhase == TournamentPhase.Idle)
        {
            // C replans after every live action. rootSession belongs to the segmented
            // search and can describe an already-played hand or a previous combat.
            var root = KernelSession.Capture(combat);
            if (root is null) return false;
            // A `pending-choice` board cannot be forked at all (KernelSession.Fork refuses an
            // incomplete action), so the tournament cannot run on it. Say so and decline —
            // the caller's cheap-fallback path is the answer here, not an exception trace
            // repeated every tick.
            if (!root.CanFork)
            {
                Log.Info("CoopBots tournament: declined — the board has an unresolved choice and "
                    + "cannot be forked, so no roll-out can be started from it");
                return false;
            }
            rootSession = root;
            tournamentRoot = root;
            tournamentActors = actors;
            tournamentParty = root.Party.Count > 0 ? root.Party : actors;
            // ASK TWICE. First without potions: if the line already wins, there is nothing worth
            // spending a cross-fight resource on. Only when it does not win is the question
            // "would a potion change this?" even meaningful — and that is the case where it is
            // worth paying for a second tournament. The potion pass is the same options with the
            // switch flipped, built when that phase starts.
            tournamentOptions = new TournamentOptions(TournamentDriveTopK,
                Rollout: new RolloutOptions(RolloutPolicyKind.Kill, Seed: combat.RoundNumber),
                IncludePotions: false,
                // A mixed table must not offer end-turn while a human is still acting: the
                // runtime will not submit it, and asking again every tick would burn a whole
                // tournament per frame. All-bot tables are always allowed.
                IncludeEndTurns: TournamentCanEndTurn(combat, humansFinished),
                // The second pass is this with the potion switch flipped, so it inherits this too.
                MaxDegreeOfParallelism: TournamentParallelism);
            tournamentPhase = TournamentPhase.NoPotions;
        }
        var options = tournamentPhase == TournamentPhase.NoPotions
            ? tournamentOptions!
            : tournamentOptions! with { IncludePotions = true };
        tournamentRun = new TournamentRun(tournamentRoot!, tournamentParty!, tournamentActors!,
            options, TournamentDriveBudgetMs);
        tournamentPasses++;
        return true;
    }

    private TournamentOutcome TryTournamentDecision(CombatState combat, IReadOnlyList<Player> actors,
        bool humansFinished, string point, out TeamCombatPlanner.Decision? decision)
    {
        decision = null;
        try
        {
            // The board moved, so anything in flight describes a position that no longer
            // exists. `point` is the same key the decline memo uses.
            if (tournamentPoint != point)
            {
                ClearTournament();
                tournamentPoint = point;
                tournamentWatch = Stopwatch.StartNew();
            }

            while (true)
            {
                // FOLLOW THE STORED LINE BEFORE PAYING FOR A FRESH ONE.
                //
                // The first cut re-ran the whole tournament (both passes) on EVERY step just to
                // check whether some fresh ending beat the stored one. Measured live on a mixed
                // table 2026-09-23: 553 stored steps, each carrying `decision-ms=100-880`, i.e.
                // ~6 ms of the main thread every frame for the rest of the fight — the stutter
                // the user reported. The stored line is the plan; replay it, and only re-search
                // when a step is no longer executable (or the line is exhausted). This is the
                // same reuse the segmented path does in TryEmitFromPlan.
                if (HasTournamentScript)
                {
                    var step = tournamentScriptIndex + 1;
                    var total = tournamentScript!.Count;
                    var indexBefore = tournamentScriptIndex;
                    if (TryEmitFromTournamentScript(combat, actors, humansFinished, out decision))
                    {
                        Log.Info($"CoopBots tournament: script step {step}/{total} "
                            + "followed without a fresh search");
                        return FinishTournament(TournamentOutcome.Decided);
                    }
                    if (tournamentScript is not null && tournamentScriptIndex == indexBefore)
                    {
                        // The step is VALID but cannot be submitted yet — an end-turn whose
                        // seat has to wait for the humans. Keep the line and park; a fresh
                        // tournament would only rediscover it after burning both passes.
                        return TournamentOutcome.Pending;
                    }
                    // The step was refused and the script cleared. Fall through and re-search.
                }
                if (tournamentRun is null && !BeginTournamentPhase(combat, actors, humansFinished))
                {
                    return FinishTournament(TournamentOutcome.Declined);
                }
                // ONE FRAME'S WORTH. This is the whole point of the slicing: return to the
                // caller without the animation having missed a frame.
                if (!tournamentRun!.Advance(TimeSpan.FromMilliseconds(TournamentSliceMs)))
                {
                    return TournamentOutcome.Pending;
                }

                var result = tournamentRun.Result!;
                if (tournamentPhase == TournamentPhase.NoPotions)
                {
                    tournamentFirstPass = result;
                    if (result.Record?.Victory != true)
                    {
                        // Not a win without potions, so the second question is worth paying for.
                        tournamentRun = null;
                        tournamentPhase = TournamentPhase.Potions;
                        continue;
                    }
                }
                else if (tournamentPhase == TournamentPhase.Potions)
                {
                    // Keep the potion line only if it is actually BETTER; a second run that
                    // changes nothing must not be allowed to spend a potion on a tie.
                    // CAPTURE THE FIRST PASS BEFORE OVERWRITING IT. `result` is reassigned
                    // below, so reading it in the diagnostic afterwards printed the DRUGGED
                    // ending under the `noPotion=` label — a diagnostic that reports the wrong
                    // value is worse than none, and this one nearly shipped with exactly that bug.
                    var firstPass = tournamentFirstPass!.Record;
                    // A bottle is only worth spending when it turns a NON-WIN first pass
                    // into a real victory. A first-pass null/cutoff must not make any
                    // second-pass record "better": measured live 2026-09-22, `noPotion=none`
                    // plus a SPEED_POTION cutoff at the end of the turn spent the bottle
                    // after the last card, where temporary Dexterity could not do anything.
                    var better = result.Record is { Victory: true };
                    // SAY WHAT THE SECOND PASS DID. Without this the log shows "no potion was
                    // used" and cannot distinguish "the potion pass did not run", "it ran and was
                    // no better", and "it ran, won, and the planner failed to act on it" —
                    // measured 2026-09-21, one run had two WIPE windows and zero potion uses, and
                    // there was no way to tell which of the three had happened.
                    Log.Info($"CoopBots tournament: potion pass — "
                        + $"noPotion={Describe(firstPass)} withPotion={Describe(result.Record)} "
                        + $"took={better} druggedFirst={result.FirstAction?.Potion?.Id.Entry ?? "none"} "
                        + $"hadPotions={(tournamentParty!.Any(p => tournamentRoot!.UsablePotions(p).Count > 0) ? "yes" : "no")}");
                    if (!better) result = tournamentFirstPass!;
                }

                var next = result.FirstAction;
                if (next is null)
                {
                    // A stored line was already tried at the top of the loop, so there is
                    // nothing left to fall back to here.
                    Log.Info($"CoopBots tournament: declined — {result.StopReason}");
                    return FinishTournament(TournamentOutcome.Declined);
                }

                // Defence in depth for a mixed table: BeginTournamentPhase already keeps
                // end-turn out of the candidate list while a human is acting, but a cached
                // first pass or a stale decision must not reach the executor either — BotRuntime
                // would refuse it and the same decision point would be recomputed every tick.
                if (next.EndTurn && !TournamentCanEndTurn(combat, humansFinished))
                {
                    Log.Info("CoopBots tournament: declined — an end-turn decision arrived while "
                        + "a human is still acting");
                    return FinishTournament(TournamentOutcome.Declined);
                }
                AdoptTournamentScript(result, combat);
                if (next.EndTurn)
                {
                    ConfirmedEndTurn = next.Player;
                    tournamentDriven++;
                    return FinishTournament(TournamentOutcome.Decided);   // ConfirmedEndTurn is the decision
                }
                if (next.Potion is { } chosenPotion)
                {
                    // A potion decision. This is the piece the first preview build lacked: the
                    // branch sat ahead of the search-side potion planner, so a tournament-driven
                    // fight never drank. Now the tournament can SEE potions (they are candidates)
                    // and the planner can ACT on one.
                    ConfirmedPotion = new PotionPlan(chosenPotion, next.Target,
                        $"tournament:rollouts={result.Rollouts},predicted="
                        + (result.Record is null ? "?" : result.Record.Victory ? "VICTORY" : "not-a-win"));
                    tournamentDriven++;
                    Log.Info($"CoopBots tournament: uses potion {chosenPotion.Id.Entry} on "
                        + $"{next.Target?.CombatId} rollouts={result.Rollouts}");
                    return FinishTournament(TournamentOutcome.Decided);
                }
                var card = KernelSession.FindLiveCardByKey(next.Player, next.CardStateKey, next.CardStateOccurrence);
                if (card is null && next.Card is { } instance
                    && next.Player.PlayerCombatState?.Hand.Cards.Contains(instance) == true) card = instance;
                if (card is null || !card.CanPlayTargeting(next.Target))
                {
                    Log.Info($"CoopBots tournament: declined — "
                        + $"{(card is null ? "live-card-not-found" : "live-target-unplayable")}");
                    return FinishTournament(TournamentOutcome.Declined);
                }
                var ending = result.Record is null ? "?"
                    : result.Record.Victory ? "VICTORY"
                    : result.Record.Verified ? "WIPE" : result.Record.CutoffReason;
                tournamentDriven++;
                decision = new TeamCombatPlanner.Decision(next.Player,
                    new(card, next.Target, 0.0,
                        $"tournament:rollouts={result.Rollouts},cutoffs={result.Cutoffs},predicted={ending}",
                        next.Choices),
                    // The first card, but now backed by the winning line's tail when one
                    // exists. PlannedCards only feeds the choice-plan sync, so this is the
                    // number of steps downstream code should expect from the cached script.
                    result.Script?.Count ?? 1);
                Log.Info($"CoopBots tournament: plays {CardLabel(card)} on {next.Target?.CombatId} "
                    + $"predicted={ending} hp={string.Join('/', result.Record?.PostCombatHp ?? [])} "
                    + $"rollouts={result.Rollouts} stop={result.StopReason}");
                return FinishTournament(TournamentOutcome.Decided);
            }
        }
        catch (Exception error)
        {
            Log.Info($"CoopBots tournament: falling back for this tick — {error.GetType().Name}: {error.Message}");
            return FinishTournament(TournamentOutcome.Declined);
        }
    }

    /// <summary>
    /// Close out a finished decision: log how long the bot thought, drop the in-flight state,
    /// and hand the outcome back. `decision-ms` is now WALL time across every slice, which is
    /// what the player actually waited — `rollouts` and `stop=` on the play line still describe
    /// the compute, and those are the two that must not change.
    /// </summary>
    private TournamentOutcome FinishTournament(TournamentOutcome outcome)
    {
        Log.Info($"CoopBots tournament: decision-ms={tournamentWatch?.Elapsed.TotalMilliseconds:F1} "
            + $"(sliced {TournamentSliceMs}ms/frame)");
        ClearTournament();
        tournamentPoint = null;
        return outcome;
    }

    /// <summary>
    /// Run the tournament beside the real decision and log the comparison. NEVER submits, NEVER
    /// throws into the caller, and says both of those in its own log line so a reader of the
    /// log cannot mistake it for something that moved the board.
    ///
    /// The exception isolation is not decoration. This runs inside the live planning path; a
    /// simulator change that makes the tournament throw must cost one diagnostic line, not the
    /// bot's turn. That is why the whole body is inside the try.
    /// </summary>
    private void RunShadowTournament(KernelSession root, CombatState combat, IReadOnlyList<Player> actors)
    {
        if (!ShadowTournamentEnabled) return;
        try
        {
            var watch = Stopwatch.StartNew();
            var party = root.Party.Count > 0 ? root.Party : actors;
            var result = KernelTournament.Run(root, party, actors,
                new TournamentOptions(ShadowTournamentTopK,
                    Rollout: new RolloutOptions(RolloutPolicyKind.Kill, Seed: 1)),
                ShadowTournamentBudgetMs);
            watch.Stop();
            var action = result.FirstAction is null ? "none"
                : $"{result.FirstAction.Card?.Id.Entry ?? "end-turn"}"
                  + $"->{result.FirstAction.Target?.CombatId}";
            // A VERIFIED WIPE MUST SAY "WIPE". The first version printed `Victory ? "VICTORY" :
            // CutoffReason`, and a verified terminal has an EMPTY CutoffReason by construction
            // (it is not a cutoff), so the boss fight — the one that ended the run — logged
            // `ending= hp=0/0/0/0`. That is the diagnostic-that-says-nothing this project keeps
            // getting bitten by (R5c): the single most important line of the batch rendered
            // its most important fact as a blank.
            var ending = result.Record is null ? "none"
                : result.Record.Victory ? "VICTORY"
                : result.Record.Verified ? "WIPE"
                : result.Record.CutoffReason;
            var hp = result.Record is null ? "-" : string.Join('/', result.Record.PostCombatHp);
            // Held, not logged. ReportSearch prints it beside the action the search ACTUALLY
            // committed, so the two are on one line and can be read against each other.
            shadowCombat = combat;
            shadowChoice = $"would-play={action} ending={ending} hp={hp} "
                + $"rollouts={result.Rollouts} cutoffs={result.Cutoffs} stop={result.StopReason} "
                + $"wall={watch.ElapsedMilliseconds}ms";
        }
        catch (Exception error)
        {
            shadowChoice = $"failed={error.GetType().Name}: {error.Message}";
        }
    }

    /// <summary>Card name for logs: the entry id when it has one, else the type name.</summary>
    private static string CardLabel(MegaCrit.Sts2.Core.Models.CardModel? card) =>
        card is null ? "?"
        : !string.IsNullOrEmpty(card.Id.Entry) ? card.Id.Entry
        : card.GetType().Name;

    private void ReportSearch(string action, KernelTeamSearch.Result result, bool humansFinished)
    {
        // A SEARCH REPORTS ITS COST ONCE. This method has four callers, and three of them
        // (card / potion / end-turn(plan)) run while the COMMITTED SCRIPT IS BEING PLAYED —
        // long after the search that produced it finished. computeMs, planningStartMs and the
        // three GC baselines are fields, so re-reporting them there prints the SAME search's
        // numbers again, only staler: measured live 2026-09-18 (4-seat A10), 6 logs produced
        // 738 search-complete lines for ~116 real searches, one of them repeating
        // `compute=61121.7ms` byte-identically 21 times and showing `wall=217344ms` beside
        // `compute=88083ms`. The wall/GC/alloc columns of a repeated line are not a slow
        // search — they are the script's runtime being charged to the search, which is why
        // PLAN.md B1's cost figures ("alloc 29GB", "wall p90 174s") were unreadable.
        //
        // The first report is the honest one: it runs immediately after the search returns.
        // Every later one is a plan step and says so, without the search's accumulators.
        if (!searchMetrics.ClaimSearchLine())
        {
            ReportSearchLine($"plan-step: action={action}, step={planIndex}/{result.Actions.Count}, "
                + $"mode={(humansFinished ? "finished" : "live")}");
            return;
        }
        var turnPrefix = result.Actions.Count;
        var crossesTurn = false;
        for (var index = 0; index < result.Actions.Count; index++)
        {
            if (!result.Actions[index].EndTurn) continue;
            turnPrefix = index;
            crossesTurn = true;
            break;
        }
        // How much of this search was the garbage collector, not the search. The kernel
        // forks a whole simulated state per branch, so allocation volume is the number
        // that decides whether a lane-parallel build would scale or would just fight the
        // GC. Measured as a DELTA over the search, because GC.GetTotalPauseDuration is
        // cumulative for the process.
        var gcPause = GC.GetTotalPauseDuration() - gcPauseAtStart;
        var gcShare = computeMs <= 0 ? 0 : 100.0 * gcPause.TotalMilliseconds / computeMs;
        var allocated = GC.GetTotalAllocatedBytes(precise: false) - allocatedAtStart;
        ReportSearchLine($"search-complete: action={action}, wall={Environment.TickCount64 - planningStartMs}ms, "
            + $"compute={computeMs:F1}ms, gcPause={gcPause.TotalMilliseconds:F0}ms ({gcShare:F1}% of compute), "
            + $"gen0={GC.CollectionCount(0) - gen0AtStart}, alloc={allocated / (1024 * 1024)}MB, "
            + $"mode={(humansFinished ? "finished" : "live")}, "
            // engine= proves which upstream solver actually ran: the value is read from
            // the vendored engine, so it cannot be printed by a stale 0.33.9 DLL.
            + $"engine={KernelTeamSearch.EngineMarker}, "
            + $"actions={result.Actions.Count}, turnPrefix={turnPrefix}, crossesTurn={crossesTurn}, "
            // route= is whether a branch that actually ENDED the fight was found. False
            // means this plan stops where depth or nodes ran out — the acceptance reading
            // for "a world-line that ends the combat", and the number to watch when tuning
            // depth against the engine's per-leaf cost.
            + $"route={search?.HasRoute ?? false}, "
            + $"nodes={result.ExpandedNodes}, stop={result.StopReason}");

        // SHADOW vs REAL on ONE line. `chose=` is the action this search actually committed
        // (index 0 of its own plan); `would-play=` is what the rollout tournament would have
        // played from the SAME root. They are comparable because both are the first action of
        // the same decision, and ending/hp price the tournament's line to a real terminal.
        // Emitted only on the deployment report, which is the combat's first decision — the
        // root the tournament was run against.
        // ONLY PAIR WITHIN THE SAME COMBAT. `is not null` alone let a stale choice from a
        // fight that never deployed be printed beside this fight's action.
        if (shadowChoice is not null && ReferenceEquals(shadowCombat, rootCombat))
        {
            // Fall back to the TYPE name. `Id.Entry` comes back empty for some cards, which
            // printed the search's choice as `?->4` and made the whole comparison unreadable —
            // measured 2026-09-21, two of the first three A/B lines. A one-sided log is a
            // comparison nobody can read.
            var chosen = result.Actions.Count == 0 ? "none"
                : result.Actions[0].EndTurn ? "end-turn"
                : $"{CardLabel(result.Actions[0].Card)}->{result.Actions[0].Target?.CombatId}";
            Log.Info($"CoopBots shadow: chose={chosen} {shadowChoice} "
                + "| MEASUREMENT ONLY, the played action was unchanged");
            shadowChoice = null;
            shadowCombat = null;
        }
    }

    // The cheap live-board gate: true only when no eligible bot holds a playable
    // card or a usable combat potion. It runs before the root is captured, so an
    // idle team never pays for a kernel search. Energy is deliberately not the
    // test: zero-cost and X-cost cards are playable at zero energy, and an
    // energy/draw potion can unlock an unaffordable hand, so cards are checked
    // with CanPlay and potions with the same usability rules the game uses.
    internal static bool NoAvailableCombatAction(IReadOnlyList<Player> actors)
    {
        foreach (var player in actors)
        {
            if (!player.Creature.IsAlive) continue;
            try
            {
                foreach (var card in player.PlayerCombatState!.Hand.Cards)
                    if (card.CanPlay()) return false;
                if (player.CanUseOrRemovePotions && player.Potions.Any(potion => !potion.IsQueued
                    && !potion.HasBeenRemovedFromState && potion.PassesCustomUsabilityCheck
                    && potion.Usage is PotionUsage.CombatOnly or PotionUsage.AnyTime)) return false;
            }
            catch { return false; }
        }
        return true;
    }

    // Re-applying a debuff the target already carries only extends its duration,
    // so the board barely changes while a limited potion is spent. A potion that
    // turns the line into a kill this turn is exempt: there the extra Vulnerable
    // does real work. The threshold is >1 rather than >0 so a single expiring
    // stack does not block a legitimate refresh.
    private void Report(string message)
    {
        if (Environment.TickCount64 < nextLog) return;
        nextLog = Environment.TickCount64 + 5000;
        Log.Info("CoopBots kernel: " + message);
    }

    /// <summary>
    /// One line per completed search, NOT rate limited.
    ///
    /// The 5-second gate above exists for the repetitive "still trying" noise, but it
    /// was swallowing the one line that carries `route=`, `wall=`, `compute=`,
    /// `gcPause=` and `stop=` — measured live, a whole session produced zero
    /// search-complete lines and zero `route=True` lines while the fight summary said
    /// `routes=2/4`. The evidence for whether the search works was being dropped by the
    /// throttle that was meant to keep the log readable.
    /// </summary>
    private static void ReportSearchLine(string message)
        => Log.Info("CoopBots kernel: " + message);
}




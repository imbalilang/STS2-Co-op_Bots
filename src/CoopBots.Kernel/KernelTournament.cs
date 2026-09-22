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
    /// WHY THIS IS A SWITCH AND NOT A PRICE: a potion is a CROSS-FIGHT resource, and the
    /// objective prices consumables at zero by default (ConsumableUnitCost = 0), so a tournament
    /// that always saw potions would drink them the moment they improved this fight — which they
    /// almost always do, and which is the wrong trade. The alternative was to invent an HP price
    /// per potion; that is a made-up number. Instead the caller asks twice: once without potions,
    /// and only if that line does not WIN does it ask again with them. A potion then costs
    /// nothing in the common case and is spent exactly when it changes the outcome.
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
    bool IncludeEndTurns = true);

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
        IReadOnlyList<Player> actors, int topK, TournamentOptions o)
    {
        var scored = new List<(KernelTeamSearch.Action Action, double Score, ActionRole Role)>();
        var seenCardStates = new Dictionary<string, int>(StringComparer.Ordinal);
        var party = root.Party.Count > 0 ? root.Party : actors;
        foreach (var player in actors)
        {
            if (!root.CanAct(player)) continue;
            // POTIONS ARE FIRST-CLASS CANDIDATES, exactly as they are for the search
            // (KernelTeamSearch builds potion actions the same way). Leaving them out was why a
            // tournament-driven build never drank: the tournament could not SEE a potion, so no
            // amount of evaluating would ever choose one. A potion is a free action, so it is
            // scored by what it enables rather than by damage — the roll-out decides, since the
            // candidate list only has to contain it.
            foreach (var potion in o.IncludePotions ? root.UsablePotions(player) : [])
            {
                if (!KernelSession.CanModelPotion(potion)) continue;
                if (!PotionHasImmediateValue(root, potion)) continue;
                var potionTarget = BestPotionTarget(root, potion);
                // Above every card: a potion costs no energy, so it must not be crowded out of
                // the top-K by a card that merely deals a little more damage. Whether it is
                // actually worth spending is the ROLL-OUT's question, not this ranking's.
                scored.Add((new KernelTeamSearch.Action(player, null, potionTarget, potion), 1e8,
                    ActionRole.Potion));
            }
            seenCardStates.Clear();
            foreach (var card in root.Hand(player))
            {
                var key = Vendor.CardChoiceSupport.ChoiceCardKey(card);
                var occurrence = seenCardStates.GetValueOrDefault(key);
                seenCardStates[key] = occurrence + 1;
                if (!root.CanPlay(card)) continue;
                // One target per card: the full target space belongs to the search, and a
                // tournament that branched on targets would cost |cards| x |enemies| rollouts.
                Creature? target;
                if (KernelTemporaryBuffs.IsAllyTarget(card)
                    && KernelTemporaryBuffs.Timing(card) is { } buffTiming)
                {
                    target = RolloutRun.BestBuffTarget(root, root.Targets(card), buffTiming);
                }
                else
                {
                    target = null;
                    var lowest = int.MaxValue;
                    foreach (var candidate in root.Targets(card))
                    {
                        if (candidate is not { } t) continue;
                        var hp = root.Hp(t);
                        if (hp >= lowest) continue;
                        lowest = hp; target = t;
                    }
                }
                var damage = card.DynamicVars.Values.OfType<DamageVar>().Sum(v => (double)v.BaseValue);
                var block = card.DynamicVars.Values.OfType<BlockVar>().Sum(v => (double)v.BaseValue);
                var lethal = damage > 0 && target is not null && damage >= root.Hp(target);
                var rider = KernelTemporaryBuffs.Timing(card) is not null
                    ? RolloutRun.RelevantRemaining(root, party, player, card, target) * 1_000_000d
                    : 0;
                var role = lethal ? ActionRole.Lethal
                    : damage > 0 ? ActionRole.Damage
                    : block > 0 ? ActionRole.Block
                    : card.Type == CardType.Power ? ActionRole.Power
                    : ActionRole.Other;
                scored.Add((new KernelTeamSearch.Action(player, card, target,
                    CardStateKey: key, CardStateOccurrence: occurrence),
                    (lethal ? 1e9 : 0) + damage + rider, role));
            }
            // NOT PLAYING IS A CANDIDATE, not an absence of one. Only added when a cheap
            // hold rule actually fires, so a normal turn still offers every card and no
            // pass; Wisp with a wasted energy gain and Piercing Wail into a 2-damage turn
            // are exactly the cases where the tournament must be able to choose “end the
            // turn and keep the card”. The roll-out then decides whether holding wins.
            //
            // A mixed table suppresses this while a human is still acting: see
            // TournamentOptions.IncludeEndTurns.
            if (o.IncludeEndTurns
                && root.Hand(player).Any(card => RolloutRun.ShouldHold(root, party, player, card)))
                scored.Add((new KernelTeamSearch.Action(player, null, null, EndTurn: true), 1e6,
                    ActionRole.Pass));
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
                     ActionRole.Pass, ActionRole.Lethal, ActionRole.Potion, ActionRole.Damage,
                     ActionRole.Block, ActionRole.Power, ActionRole.Other,
                 })
            TakeFirst(entry => entry.Role == role);
        foreach (var player in ranked.Select(entry => entry.Action.Player).Distinct())
            TakeFirst(entry => ReferenceEquals(entry.Action.Player, player));
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
    private enum ActionRole { Pass, Lethal, Damage, Block, Power, Other, Potion }
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
/// between two equal endings picks the same winner the sequential loop would. The budget is
/// still COMPUTE time (the sum of what the workers actually spent), so parallelism buys wall
/// time, not a different amount of work.
/// </summary>
public sealed class TournamentRun
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

    private readonly List<TerminalRecord> considered = new();
    private KernelTeamSearch.Action? bestAction;
    private TerminalRecord? bestRecord;
    // The winning roll-out's whole line, not just its first action. Kept beside bestRecord so
    // the planner can keep playing a line the fresh tournament can no longer reproduce.
    private IReadOnlyList<KernelTeamSearch.Action>? bestScript;
    private IReadOnlyList<ulong[]>? bestScriptAliveStates;
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

    /// <summary>Workers still running. Empty on the sequential path.</summary>
    private readonly List<Lane> inFlight = new();
    /// <summary>A launch attempt hit the deadline or ran out of candidates while lanes were out.</summary>
    private bool pendingFinish;

    /// <summary>Time actually spent computing, across every Advance. Never wall time.</summary>
    private double computeMs;

    public TournamentResult? Result { get; private set; }
    public bool Done => Result is not null;

    /// <summary>Rollouts finished so far — the unit the budget is really spent in.</summary>
    public int Rollouts => rollouts;

    public TournamentRun(KernelSession root, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, TournamentOptions? options, int budgetMs)
    {
        this.root = root;
        this.party = party;
        this.actors = actors;
        o = options ?? new TournamentOptions();
        this.budgetMs = budgetMs;
        rolloutOptions = o.Rollout ?? new RolloutOptions(o.Policy);
        dop = Math.Max(1, o.MaxDegreeOfParallelism);

        if (!root.CanFork)
        {
            Result = new TournamentResult(null, null, [], 0, 0, "unresolved-root");
            candidates = [];
            policies = [];
            return;
        }

        var watch = Stopwatch.StartNew();
        candidates = KernelTournament.FirstActions(root, actors, o.TopK, o);
        computeMs = watch.Elapsed.TotalMilliseconds;
        if (candidates.Count == 0)
        {
            Result = new TournamentResult(null, null, [], 0, 0, "no-legal-action");
            policies = [];
            return;
        }

        // Rollout.Run mutates its argument. Every policy must start from an independent
        // copy of the SAME opened position, never from another policy's terminal state.
        policies = o.UsePolicyPortfolio
            ? new[] { rolloutOptions.Policy, RolloutPolicyKind.Kill, RolloutPolicyKind.Defend, RolloutPolicyKind.Growth }.Distinct().ToArray()
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
        => dop <= 1 ? AdvanceSerial(slice) : AdvanceParallel(slice);

    private bool AdvanceSerial(TimeSpan slice)
    {
        if (Done) return true;
        var watch = Stopwatch.StartNew();
        while (!Done)
        {
            // ONLY OPEN WHEN NOTHING IS IN FLIGHT. TryOpenNextRollout always builds a new
            // rollout, so calling it every iteration would discard the half-stepped one and no
            // rollout would ever reach its end — the loop would burn every candidate and finish
            // with `rollouts=0, no-playable-first-action`. Step the open one to completion first.
            if (rollout is null)
            {
                if (TryOpenNextRollout(watch) != OpenOutcome.Opened) { Finish(); break; }
                continue;
            }
            rollout.Step();
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
                if (lane.Error is { } error) { inFlight.Clear(); throw error; }
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
            if (inFlight.Count > 0 && launch.Elapsed >= launchBudget) break;
            if (TryOpenNextRollout(launch) != OpenOutcome.Opened) { pendingFinish = true; break; }
            Launch(rollout!, currentAction!);
            rollout = null;
        }
        computeMs += launch.Elapsed.TotalMilliseconds;
        if (inFlight.Count == 0) { pendingFinish = false; Finish(); }
        return Done;
    }

    /// <summary>
    /// Hand a private fork to a worker. Dedicated `BelowNormal` background threads rather than
    /// the pool, mirroring the engine's own expansion lanes: a rollout is short and bursty, and
    /// at Normal priority a saturated wave would compete with the game's render thread on a
    /// small machine.
    /// </summary>
    private void Launch(RolloutRun run, KernelTeamSearch.Action action)
    {
        var lane = new Lane { Run = run, Action = action };
        inFlight.Add(lane);
        var thread = new Thread(() =>
        {
            var watch = Stopwatch.StartNew();
            try
            {
                while (!run.Done) run.Step();
                lane.ElapsedMs = watch.Elapsed.TotalMilliseconds;
            }
            catch (Exception error)
            {
                lane.Error = error;
            }
            // Volatile write: publishes ElapsedMs / Error to whoever reads Finished.
            lane.Finished = true;
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "CoopBots tournament rollout",
        };
        lane.Worker = thread;
        thread.Start();
    }

    /// <summary>
    /// A rollout cannot be preempted mid-action, so the budget is only ever checked BETWEEN
    /// them — and never before the first one, so a legal candidate always produces an answer.
    /// Same shape as the one-shot version, in the same two places (before a candidate's branch
    /// is opened, and before each of its rollouts).
    ///
    /// <paramref name="watch"/> HAS to be part of this sum. `computeMs` only folds in an Advance
    /// when that Advance RETURNS, so on its own it is stale for the whole of a call — and the
    /// one-shot façade passes one enormous slice, which would have made the budget never trip at
    /// all and quietly changed what every non-sliced caller decides.
    /// </summary>
    private bool BudgetSpent(Stopwatch watch)
        => rollouts > 0 && computeMs + watch.Elapsed.TotalMilliseconds + o.ReserveMs > budgetMs;

    private enum OpenOutcome { Opened, Exhausted, Deadline }

    /// <summary>
    /// Open the next (candidate, policy) rollout, or say why there is none. Doing nothing means
    /// the caller finishes the run — it is NOT done here because on the parallel path there may
    /// still be lanes in flight whose results have to be collected first.
    /// </summary>
    private OpenOutcome TryOpenNextRollout(Stopwatch watch)
    {
        while (candidateIndex < candidates.Count)
        {
            if (branch is null)
            {
                if (BudgetSpent(watch)) { stop = "deadline"; return OpenOutcome.Deadline; }
                var candidate = candidates[candidateIndex];
                if (candidate.EndTurn)
                {
                    var forked = root.Fork();
                    if (!forked.EndTurn(candidate.Player, rolloutOptions.MaxRounds, out _))
                    {
                        NextCandidate();
                        continue;
                    }
                    branch = forked;
                    currentAction = candidate;
                }
                else if (candidate.Potion is { } potion)
                {
                    var forked = root.Fork();
                    if (!forked.UsePotion(potion, candidate.Target, out _)) { NextCandidate(); continue; }
                    branch = forked;
                    currentAction = candidate;
                }
                else
                {
                    var opened = root.CardBranches(candidate.Card!, candidate.Target, maximumBranches: 1)
                        .FirstOrDefault(b => b.Boundary.Length == 0);
                if (opened is null) { NextCandidate(); continue; }
                branch = opened.State;
                currentAction = candidate with { Choices = opened.Choices, ChoiceOrdinals = opened.ChoiceOrdinals };
                }
            }
            if (policyIndex >= policies.Length) { NextCandidate(); continue; }
            if (BudgetSpent(watch)) { stop = "deadline"; return OpenOutcome.Deadline; }
            // The fork happens HERE, on the caller's thread, one at a time — see the class
            // remarks for why this line may not move into the worker.
            rollout = new RolloutRun(branch.Fork(), party, actors,
                rolloutOptions with { Policy = policies[policyIndex++] });
            return OpenOutcome.Opened;
        }
        return OpenOutcome.Exhausted;
    }

    private void NextCandidate()
    {
        candidateIndex++;
        policyIndex = 0;
        branch = null;
    }

    private void Consume(RolloutRun run, KernelTeamSearch.Action action)
    {
        var outcome = run.Result!;
        rollouts++;
        considered.Add(outcome.Record);
        if (outcome.Cutoff) cutoffs++;
        if (bestRecord is null || TerminalComparer.Compare(outcome.Record, bestRecord) > 0)
        {
            bestRecord = outcome.Record;
            bestAction = action;
            // Copy: the outcome's list belongs to a finished rollout, but copying keeps the
            // cached script immune to any later reuse of that collection.
            bestScript = outcome.Actions.ToArray();
            bestScriptAliveStates = outcome.AliveStates.ToArray();
        }
    }

    private void Finish()
    {
        if (Done) return;
        Result = bestAction is null
            ? new TournamentResult(null, null, considered, rollouts, cutoffs, "no-playable-first-action")
            : new TournamentResult(bestAction, bestRecord, considered, rollouts, cutoffs, stop)
            {
                Script = bestScript,
                ScriptAliveStates = bestScriptAliveStates,
            };
    }

    /// <summary>One rollout plus the worker running it.</summary>
    private sealed class Lane
    {
        public required RolloutRun Run { get; init; }
        public required KernelTeamSearch.Action Action { get; init; }
        public Thread? Worker { get; set; }
        /// <summary>Written by the worker before <see cref="Finished"/>; read by the caller after.</summary>
        public Exception? Error { get; set; }
        /// <summary>Volatile write publishes <see cref="ElapsedMs"/> and <see cref="Error"/>.</summary>
        public volatile bool Finished;
        public double ElapsedMs;
    }
}

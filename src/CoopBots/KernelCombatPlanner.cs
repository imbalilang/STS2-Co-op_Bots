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
    private int staleCount;
    private int timeBudgetMs = 200;
    private int wallBudgetMs = 300;
    private long planningStartMs;
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
    // Proactive potions the search proposed but that were declined as redundant.
    // Kept in the summary so a tuning change can be judged against real runs.
    private int potionDeclined;
    // Post-mortem context: which encounter this was, how far it got, and the
    // party state, so a wipe can be reviewed from the log alone.
    private string combatLabel = "";
    private int maxRoundSeen;
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
    internal void DiscardConfirmation()
    {
        ConfirmedPotion = null;
        ConfirmedEndTurn = null;
    }

    internal void Reset(bool newCombat = false)
    {
        search?.Dispose(); search = null; rootSession = null;
        evaluation = null; rootMetrics = null; rootCombat = null;
        if (newCombat)
        {
            disabledCombat = null; staleCount = 0; noActionStamp = "";
            if (plans + fallbackBoundary + fallbackNoAction + fallbackStale + fallbackException
                + potionDeclined + unmodeledPotions.Count > 0)
            {
                // Coverage is the metric the optimization plan is built around:
                // how often a kernel search actually ends in a deployable plan.
                // Boundary is an annotation on a plan, not a separate outcome, so
                // it is excluded from the denominator.
                var resolved = plans + fallbackNoAction + fallbackStale;
                var coverage = resolved == 0 ? 0 : 100.0 * plans / resolved;
                var topBoundaries = boundaryKinds.Count == 0 ? "none"
                    : string.Join(";", boundaryKinds.OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(3)
                        .Select(pair => $"{pair.Key}x{pair.Value}"));
                var kindSummary = noActionKinds.Count == 0 ? "none"
                    : string.Join(";", noActionKinds.OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key).Select(pair => $"{pair.Key}x{pair.Value}"));
                Log.Info($"CoopBots kernel combat summary: encounter={combatLabel}, "
                    + $"plans={plans} (coverage={coverage:F0}%), boundary={fallbackBoundary}, no-action={fallbackNoAction}, "
                    + $"stale={fallbackStale}(root={fallbackStaleRoot},notification={fallbackStaleNoise}), "
                    + $"potion-declined={potionDeclined}, "
                    + $"exception={fallbackException}, maxRound={maxRoundSeen}, "
                    + $"party={partyState}, boundaries={topBoundaries}, "
                    + $"no-action-kinds={kindSummary}, "
                    + $"unmodeled-potions={(unmodeledPotions.Count == 0 ? "none" : string.Join(";", unmodeledPotions.OrderBy(n => n, StringComparer.Ordinal)))}");
            }
            plans = fallbackBoundary = fallbackNoAction = fallbackStale = fallbackException = 0;
            fallbackStaleRoot = fallbackStaleNoise = 0; potionDeclined = 0; noActionKinds.Clear();
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
        if (ReferenceEquals(combat, disabledCombat)) return Status.Fallback;
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
                // Rounds is how many turns each end-turn expansion simulates.
                var (depth, width, nodes, budgetMs, wallMs, rounds) = (9, 8, 768, 200, 300, 2);
                // Difficulty is a thinking-time knob, not a different algorithm:
                // scale the node and time budgets, keep the search shape (depth
                // and width) identical so a fast tier cuts the thinking short
                // rather than planning something structurally worse. Pro's x1.5
                // schedule tops the wall budget out at 450ms.
                var thinking = difficulty.ThinkingScale();
                nodes = Math.Max(64, (int)(nodes * thinking));
                budgetMs = Math.Max(20, (int)(budgetMs * thinking));
                wallMs = Math.Max(40, (int)(wallMs * thinking));
                timeBudgetMs = budgetMs;
                // The CPU budget is spread over frames (4ms each), so it is also
                // capped by wall clock.
                wallBudgetMs = wallMs;
                planningStartMs = Environment.TickCount64;
                evaluation = new(combat, actors, manualFocus, null, rounds);
                rootMetrics = evaluation.Evaluate(root);
                rootSession = root;
                search = new(root, actors, s => evaluation.Evaluate(s).Score,
                    new(Depth: depth, Width: width, MaxNodes: nodes, IncludeEndTurns: humansFinished,
                        MaxRounds: rounds));
                revision = KernelSession.LiveRevision;
                computeMs = capture.Elapsed.TotalMilliseconds;
                // Capture and JIT are not preemptible; never add an expansion slice
                // to the same frame, and expose slow capture in diagnostics.
                if (computeMs >= 40) Report($"capture={computeMs:F1}ms phase={(humansFinished ? "finished" : "live")}");
                return Status.Pending;
            }
            var elapsed = Stopwatch.StartNew();
            var done = search.Advance(TimeSpan.FromMilliseconds(4), Current);
            computeMs += elapsed.Elapsed.TotalMilliseconds;
            var wallElapsed = Environment.TickCount64 - planningStartMs;
            if (!done && computeMs < timeBudgetMs && wallElapsed < wallBudgetMs) return Status.Pending;
            if (!done) search.FinishAtBudget();
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
                var first = result.Actions[0];
                if (first.EndTurn)
                {
                    if (!humansFinished || !actors.Contains(first.Player) || result.HasUncertainRisk
                        || result.Boundaries.Count > 0) { Reset(); return Status.Fallback; }
                    noActionStamp = "";
                    ConfirmedEndTurn = first.Player;
                    plans++;
                    ReportSearch("end-turn", result, humansFinished);
                    Reset(); return Status.Ready;
                }
                // A potion-first plan (e.g. buff, then the burst) is deployed as a
                // free action; the cards are replanned against the real state.
                if (first.Potion is { } planned)
                {
                    if (!actors.Contains(first.Player) || planned.IsQueued || planned.HasBeenRemovedFromState
                        || !planned.PassesCustomUsabilityCheck || !planned.IsValidTarget(first.Target))
                    {
                        if (++staleCount >= 2) { Reset(); disabledCombat = combat; return Status.Fallback; }
                        Reset(); return Status.Fallback;
                    }
                    // The search prices boards, not inventory, so a debuff the
                    // target already carries looks like a small gain and is worth
                    // "spending" a potion on. The run that exposed this: a boss at
                    // 15% HP already carrying four Vulnerable stacks, and a bot
                    // drank a Vulnerable potion into it instead of keeping the
                    // slot; it died the next turn with nothing left. Re-applying a
                    // debuff only extends its duration, so treat it as no gain
                    // unless the line turns it into a kill this turn.
                    var powerName = planned.GetType().Name switch
                    {
                        "VulnerablePotion" => "VulnerablePower",
                        "WeakPotion" => "WeakPower",
                        _ => null,
                    };
                    var existing = powerName is null || first.Target is null ? 0
                        : first.Target.Powers.Where(power => power.GetType().Name == powerName).Sum(power => power.Amount);
                    if (RedundantDebuff(planned.GetType().Name, first.Target is { IsEnemy: true },
                            first.Target is not null && final.Hp(first.Target) <= 0, existing))
                    {
                        potionDeclined++;
                        Report($"declined redundant potion {planned.Id.Entry}: existing={existing}");
                        Reset(); return Status.Fallback;
                    }
                    noActionStamp = "";
                    ConfirmedPotion = new PotionPlan(planned, first.Target,
                        $"kernel-plan:potion:{planned.Id.Entry},sequence:{result.Actions.Count},{result.StopReason}");
                    plans++;
                    ReportSearch("potion", result, humansFinished);
                    Reset(); return Status.Ready;
                }
                if (first.Card is not { } firstCard || !actors.Contains(first.Player)
                    || !first.Player.PlayerCombatState!.Hand.Cards.Contains(firstCard)
                    || !firstCard.CanPlayTargeting(first.Target))
                {
                    fallbackStale++;
                    Reset(); return Status.Fallback;
                }
                var metrics = evaluation!.Evaluate(final);
                // A path that used an unmodeled environmental hook is an estimate,
                // so it must not claim a verified rescue when a potion compares.
                var estimated = result.HasUncertainRisk || result.Boundaries.Count > 0;
                decision = new(first.Player, new(firstCard, first.Target,
                    Math.Max(1, metrics.Score - rootMetrics!.Score),
                    $"kernel-team,sequence:{result.Actions.Count},nodes:{result.ExpandedNodes},{result.StopReason}"
                        + (metrics.SoftFinish > 0 ? $",human-finish:{metrics.SoftFinish:F0}" : "")
                        + (estimated ? ",estimated" : ""), first.Choices),
                    result.Actions.Count,
                    estimated ? 0 : Math.Max(0, rootMetrics!.WeightedDeaths - metrics.WeightedDeaths),
                    rootMetrics.HpLoss - metrics.HpLoss);
                noActionStamp = "";
                // Only the first action of the plan is submitted. There is no
                // speculative tail: after this action resolves the next one is
                // re-planned against the real board, so a stale assumption can
                // never be played out.
                plans++;
                // A plan supersedes the last abstention: leaving it set would put a
                // stale reason on a later legacy move that had nothing to do with it.
                LastNoAction = NoActionKind.None;
                ReportSearch("card", result, humansFinished);
                Reset(); return Status.Ready;
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
            Report($"search-complete: action=none/{LastNoAction}, wall={Environment.TickCount64 - planningStartMs}ms, "
                + $"compute={computeMs:F1}ms, mode={(humansFinished ? "finished" : "live")}, nodes={result.ExpandedNodes}, "
                + $"stop={result.StopReason}, boundaries={result.Boundaries.Count}, root={rootMetrics?.Score:F0}, best={result.Score:F0}");
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
    private void ReportSearch(string action, KernelTeamSearch.Result result, bool humansFinished)
        => Report($"search-complete: action={action}, wall={Environment.TickCount64 - planningStartMs}ms, "
            + $"compute={computeMs:F1}ms, mode={(humansFinished ? "finished" : "live")}, "
            + $"actions={result.Actions.Count}, nodes={result.ExpandedNodes}, stop={result.StopReason}");

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
    internal static bool RedundantDebuff(string potionName, bool livingEnemy, bool diesThisPlan, int existingPower)
        => potionName is "VulnerablePotion" or "WeakPotion"
            && livingEnemy && !diesThisPlan && existingPower > 1;

    private void Report(string message)
    {
        if (Environment.TickCount64 < nextLog) return;
        nextLog = Environment.TickCount64 + 5000;
        Log.Info("CoopBots kernel: " + message);
    }
}

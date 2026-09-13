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
    internal sealed record PotionPlan(PotionModel Potion, Creature? Target, string Reason);
    internal PotionPlan? ConfirmedPotion { get; private set; }
    // Set when the plan leaves an enemy that only a human's damage can finish
    // this turn: the team asks that player to commit the hit.
    internal sealed record CalloutPlan(Creature Enemy, int NeededHp, string Text);
    internal CalloutPlan? Callout { get; private set; }
    internal Player? ConfirmedEndTurn { get; private set; }
    private Player[] actorList = [];
    private KernelTeamSearch? search;
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
    // notification that changed nothing the branch depended on.
    private int fallbackStaleRoot;
    private int fallbackStaleNoise;
    private int fallbackException;
    // Post-mortem context: which encounter this was, how far it got, and the
    // party state, so a wipe can be reviewed from the log alone.
    private string combatLabel = "";
    private int maxRoundSeen;
    private string partyState = "";
    private void ObserveCombat(CombatState combat, IReadOnlyList<Player> actors)
    {
        combatLabel = $"{combat.Encounter?.Id.Entry ?? "unknown"} act={combat.RunState.CurrentActIndex + 1}";
        maxRoundSeen = Math.Max(maxRoundSeen, combat.RoundNumber);
        partyState = string.Join(", ", combat.Players.Select(p =>
            $"{p.NetId}:{p.Creature.CurrentHp}/{p.Creature.MaxHp} deck={p.Deck.Cards.Count}"));
    }
    internal void Reset(bool newCombat = false)
    {
        search?.Dispose(); search = null; evaluation = null; rootMetrics = null; rootCombat = null;
        if (newCombat)
        {
            disabledCombat = null; staleCount = 0;
            if (plans + fallbackBoundary + fallbackNoAction + fallbackStale + fallbackException > 0)
            {
                Log.Info($"CoopBots kernel combat summary: encounter={combatLabel}, "
                    + $"plans={plans}, boundary={fallbackBoundary}, no-action={fallbackNoAction}, "
                    + $"stale={fallbackStale}(root={fallbackStaleRoot},notification={fallbackStaleNoise}), "
                    + $"exception={fallbackException}, maxRound={maxRoundSeen}, "
                    + $"party={partyState}");
            }
            plans = fallbackBoundary = fallbackNoAction = fallbackStale = fallbackException = 0;
            fallbackStaleRoot = fallbackStaleNoise = 0;
            combatLabel = ""; maxRoundSeen = 0; partyState = "";
        }
    }
    internal Status Poll(CombatState combat, IReadOnlyList<Player> actors, uint actionVersion, uint? manualFocus,
        bool humansFinished, out TeamCombatPlanner.Decision? decision)
    {
        decision = null;
        ConfirmedPotion = null;
        ConfirmedEndTurn = null;
        Callout = null;
        if (actors.Count == 0) { Reset(); return Status.Ready; }
        if (ReferenceEquals(combat, disabledCombat)) return Status.Fallback;
        bool Current() => ReferenceEquals(rootCombat, combat) && round == combat.RoundNumber
            && queueVersion == actionVersion && revision == KernelSession.LiveRevision && focus == manualFocus
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
                var capture = Stopwatch.StartNew();
                rootCombat = combat; round = combat.RoundNumber; focus = manualFocus; queueVersion = actionVersion;
                actorIds = actors.Select(p => p.NetId).ToArray();
                stamp = KernelSession.CaptureLiveStamp(combat);
                var root = KernelSession.Capture(combat);
                actorList = actors.ToArray();
                var fresh = combatLabel.Length == 0;
                ObserveCombat(combat, actors);
                if (fresh) Log.Info($"CoopBots combat start: {combatLabel}; {partyState}");
                // Two scheduling regimes: while humans are still deciding the
                // battlefield keeps changing, so each card gets a short, turn-local
                // search; once everyone has ended their turn we can spend a much
                // larger budget and project further ahead.
                var (depth, width, nodes, budgetMs, wallMs, rounds) = humansFinished
                    ? (16, 12, 3072, 1200, 600, 4)
                    : (9, 8, 768, 200, 300, 2);
                timeBudgetMs = budgetMs;
                // The CPU budget is spread over frames (4ms each), so it must be
                // capped by wall clock too or a deep search could run for seconds.
                wallBudgetMs = wallMs;
                planningStartMs = Environment.TickCount64;
                evaluation = new(combat, actors, manualFocus, null, rounds);
                rootMetrics = evaluation.Evaluate(root);
                search = new(root, actors, s => evaluation.Evaluate(s).Score,
                    new(Depth: depth, Width: width, MaxNodes: nodes, IncludeEndTurns: humansFinished));
                revision = KernelSession.LiveRevision;
                computeMs = capture.Elapsed.TotalMilliseconds;
                // Capture and JIT are not preemptible; never add an expansion slice
                // to the same frame, and expose slow capture in diagnostics.
                if (computeMs >= 40) Report($"capture={computeMs:F1}ms phase={(humansFinished ? "deep" : "live")}");
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
            if (!Current() || KernelSession.CaptureLiveStamp(combat) != stamp)
            {
                fallbackStale++;
                CountStale(combat);
                Reset(); return Status.Fallback;
            }
            staleCount = 0;
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
                    ConfirmedEndTurn = first.Player;
                    plans++;
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
                    ConfirmedPotion = new PotionPlan(planned, first.Target,
                        $"kernel-plan:potion:{planned.Id.Entry},sequence:{result.Actions.Count},{result.StopReason}");
                    Report($"planned potion first: {planned.Id.Entry} ({result.Actions.Count} actions)");
                    plans++;
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
                Callout = BuildCallout(final);
                plans++;
                if (computeMs >= 80)
                    Report($"search={computeMs:F1}ms wall={Environment.TickCount64 - planningStartMs}ms; "
                        + $"nodes={result.ExpandedNodes}; stop={result.StopReason}");
                Reset(); return Status.Ready;
            }
            // Search finished but recommends nothing: let the legacy planner act
            // this tick instead of leaving the bots idle. The kernel retries next
            // tick because the combat is not disabled.
            fallbackNoAction++;
            // Distinguish "nothing was playable" (out of energy: idle, harmless)
            // from "cards were simulated but none scored better than doing
            // nothing" (an evaluation gap that silently hands the bots to the
            // weaker legacy planner). Nodes expanded makes the two obvious.
            if (computeMs >= 80)
                Report($"search={computeMs:F1}ms; no-action; stop={result.StopReason}; nodes={result.ExpandedNodes}; "
                    + $"boundaries={result.Boundaries.Count}; root={rootMetrics?.Score:F0}; best={result.Score:F0}");
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

    // If the plan cannot finish an enemy alone but the humans' combined damage
    // could, ask them to commit it: the kill removes this and future damage.
    private CalloutPlan? BuildCallout(KernelSession final)
    {
        if (evaluation is null || final.HasWon) return null;
        // Same predicate the score credit uses, so a kill that only becomes
        // reachable through Vulnerable still raises the request.
        if (evaluation.HumanFinishTarget(final) is not { } finish) return null;
        var name = finish.Enemy.Monster?.Id.Entry ?? "目标";
        return new CalloutPlan(finish.Enemy, finish.Remaining,
            $"请对 {name} 输出，本回合可斩杀（约需 {finish.Remaining} 点，真人可覆盖 {finish.Cover:0} 点）");
    }

    private void Report(string message)
    {
        if (Environment.TickCount64 < nextLog) return;
        nextLog = Environment.TickCount64 + 5000;
        Log.Info("CoopBots kernel: " + message);
    }
}

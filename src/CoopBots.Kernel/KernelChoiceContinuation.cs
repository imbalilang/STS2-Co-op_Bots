using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel;

// A selector is not a settled transaction. Only completed native choice
// branches enter this comparison; no suspended CLR scope is ever forked.
internal static class KernelChoiceContinuation
{
    internal const int MaximumChoices = 10;
    internal const int MaximumSteps = 384;
    internal sealed record Evaluation(double Value, int Steps, int CompleteHands)
    {
        internal int NativePrefixHits { get; init; }
        internal string StopReason { get; init; } = "";
    }
    [ThreadStatic] private static int depth;
    internal static bool Active => depth > 0;
    internal static IDisposable Enter() { depth++; return new Scope(); }
    private sealed class Scope : IDisposable { public void Dispose() => depth--; }

    // A first menu with ten options must not fill a ten-trial queue before
    // any later source can vary. Rotate menu positions at each ordinal layer;
    // the total replay bound and each menu's original legal ordering remain.
    internal static void EnqueueAlternatives(Queue<int[]> queue, HashSet<string> seen,
        IReadOnlyList<int> vector, IReadOnlyList<int> counts)
    {
        for (var alternative = 0; alternative < counts.DefaultIfEmpty(0).Max(); alternative++)
        for (var position = 0; position < counts.Count; position++)
        {
            if (alternative >= counts[position]
                || alternative == (position < vector.Count ? vector[position] : 0)) continue;
            var next = Enumerable.Range(0, position + 1)
                .Select(j => j < vector.Count ? vector[j] : 0).ToArray();
            next[position] = alternative;
            // Missing ordinals already mean zero in the native cursor. Do
            // not spend a replay on [0] / [1,0] after [] / [1] was visited.
            var length = next.Length;
            while (length > 0 && next[length - 1] == 0) length--;
            if (length != next.Length) next = next.Take(length).ToArray();
            if (seen.Add(string.Join(',', next))) queue.Enqueue(next);
        }
    }

    internal static double Value(KernelSession root, KernelSession selected, Player owner,
        int choiceCount)
        => Evaluate(root, selected, owner, choiceCount).Value;

    internal static Evaluation Evaluate(KernelSession root, KernelSession selected, Player owner,
        int choiceCount)
    {
        var request = KernelSearchBudgetScope.Current;
        bool Stopped() => request?.IsStopped == true;
        if (Stopped())
        {
            // A settled native victory is already a complete witness. Preserve
            // its existing value without starting another rollout policy/Step.
            // Existing valuation projections still settle (and may fork).
            // EnemyPhaseCompleted alone can belong to an earlier round boundary.
            if (selected.CanFork && selected.HasTerminal && selected.HasWon)
            {
                var terminalValue = KernelTacticalPolicy.ActionValue(root, selected, selected.ChoiceParty, [owner])
                    + selected.FutureDebuffValue([owner], elapsedTurns: 0, reference: root);
                return new(terminalValue, 0, 1) { StopReason = request!.StopReason };
            }
            return new(double.NegativeInfinity, 0, 0) { StopReason = request!.StopReason };
        }
        var party = selected.ChoiceParty;
        var best = double.NegativeInfinity;
        var steps = Math.Min(16, Math.Max(1, MaximumSteps / (Math.Max(1, choiceCount) * 3)));
        var used = 0;
        var complete = 0;
        using var guard = Enter();
        var opportunity = root.ExhaustOpportunityCost(selected, owner);
        // With no legal affordable card, all three policies take the same
        // native EndTurn. Resolve that boundary once; this is still a complete
        // witness, including held status, orbs and the real enemy phase.
        // A playable zero-cost card keeps the ordinary three-policy comparison.
        var policies = selected.Hand(owner).Any(selected.CanPlay)
            ? new[] { RolloutPolicyKind.Kill, RolloutPolicyKind.Defend, RolloutPolicyKind.Growth }
            : new[] { RolloutPolicyKind.Kill };
        // Optional continuation work must not start after the same outer
        // request expires. Mandatory native menu/action settlement is elsewhere.
        if (Stopped()) return new(best, used, complete) { StopReason = request!.StopReason };
        var seed = selected.Fork();
        seed.SampleChoiceDrawPool(owner);
        seed.PrepareChoiceHand(owner);
        var prefixCache = policies.Length > 1 ? new KernelChoicePrefixCache(seed) : null;
        foreach (var policy in policies)
        {
            if (Stopped()) break;
            var child = prefixCache?.ForkSeed() ?? seed;
            var run = new RolloutRun(child, party, [owner],
                new(policy, MaxSteps: steps, MaxRounds: child.RoundsAdvanced + 1)
                { InvestmentLookahead = false, SequenceLookahead = true, SequenceHandLookahead = false })
            { ChoicePrefixCache = prefixCache, StopRequested = Stopped };
            while (!run.Done)
            {
                if (Stopped()) { run.FinishAtBudget(); break; }
                // Step completes its real native play/choice/side transaction;
                // the shared stop signal is observed before the next Step.
                run.Step();
            }
            used += run.Result!.Steps;
            if (!run.State.CanFork || !run.State.HasTerminal && !run.State.EnemyPhaseCompleted) continue;
            complete++;
            var value = KernelTacticalPolicy.ActionValue(root, run.State, party, [owner])
                + run.State.FutureDebuffValue([owner], elapsedTurns: 0, reference: root);
            // Only a completed unfinished fight has later draw opportunities.
            // Actual victory or survival remains native; no choice is forbidden.
            if (!run.State.HasWon) value -= opportunity + run.State.ChoicePollutionCost(owner);
            best = Math.Max(best, value);
        }
        return new(best, used, complete) { NativePrefixHits = prefixCache?.Hits ?? 0,
            StopReason = Stopped() ? request!.StopReason : "" };
    }
}

public sealed partial class KernelSession
{
    private Queue<PlanCardChoice>? preparedAfterStartChoices;
    internal IReadOnlyList<Player> ChoiceParty => Combat.Players;

    internal double ExhaustOpportunityCost(KernelSession selected, Player owner)
    {
        // Compare Original card identities before projecting the future lease.
        // An unchanged exhaust pile has no opportunity cost or native valuation.
        var previous = simulator.State.GetPlayerCombatState(owner).ExhaustPile.Cards;
        var chosen = selected.simulator.State.GetPlayerCombatState(owner).ExhaustPile.Cards;
        var added = false;
        for (var index = 0; index < chosen.Count && !added; index++)
        {
            var existed = false;
            for (var prior = 0; prior < previous.Count; prior++)
                if (EqualityComparer<MegaCrit.Sts2.Core.Models.CardModel>.Default.Equals(
                    previous[prior].Original, chosen[index].Original))
                {
                    existed = true;
                    break;
                }
            added = !existed;
        }
        if (!added) return 0;
        var before = simulator.State.GetPlayerCombatState(owner).ExhaustPile.Cards
            .Select(c => c.Original).ToHashSet();
        // Opportunity belongs after the current stat lease. Its native expiry
        // runs only on a settled independent fork; immediate play keeps the
        // original state and its real temporary Strength/Dexterity.
        var future = AfterTemporaryStatExpiry();
        var available = Math.Max(future.Energy(owner), PersistentPowerSupport.GetModifiedMaxEnergy(future.Combat, owner));
        using var isolation = SimulationNotificationIsolation.Enter();
        return selected.simulator.State.GetPlayerCombatState(owner).ExhaustPile.Cards
            .Where(c => !before.Contains(c.Original))
            .Sum(c => KernelRelicChoices.FutureExhaustValue(future.simulator, owner,
                future.simulator.State.FindCard(c.Original) ?? c, available));
    }

    // Analysis only: other seats' already-visible HP/block stay fixed while
    // this owner's affordable hand is played to the real shared enemy phase.
    // These pass markers are not returned as deployable actions.
    internal void PrepareChoiceHand(Player owner)
    {
        ready = Combat.Players.Where(p => p != owner).Select(p => p.NetId).ToHashSet();
        EnemyPhaseCompleted = false;
        AutoResolveEnemyChoices = true;
        AutoResolveTurnStartChoices = true;
    }

    internal double ChoicePollutionCost(Player owner)
        => simulator.State.GetPlayerCombatState(owner).DiscardPile.Cards
            .Concat(simulator.State.GetPlayerCombatState(owner).DrawPile.Cards)
            .Where(c => c.Preview.Type is CardType.Curse or CardType.Status)
            .Sum(c => KernelRelicChoices.IsHarmlessEtherealJunk(c.Preview)
                ? KernelRelicChoices.FutureJunkDrawCost : .1);

    internal void SampleChoiceDrawPool(Player owner)
    {
        // A live selector knows the deck multiset, not its hidden top card.
        // Use one common deterministic multiset order across alternatives;
        // native draw/autoplay/shuffle still applies all hooks and costs.
        var pile = simulator.State.GetPlayerCombatState(owner).DrawPile;
        var cards = pile.Cards.OrderBy(CardChoiceSupport.ChoiceCardKey, StringComparer.Ordinal).ToArray();
        foreach (var card in cards) pile.Remove(card);
        foreach (var card in cards) pile.Add(card);
    }

    internal bool ApplyChoiceForAnalysis(Player owner, CardChoiceSpec spec, PlanCardChoice choice,
        string source)
    {
        var options = spec.Options.Select(c => simulator.State.FindCard(c.Original) ?? c).ToArray();
        var forkSpec = spec with { Options = options, SourceCards = options };
        var request = new TurnStartChoiceRequest(source, spec.Effect, spec.SourcePile,
            spec.MinCount, forkSpec);
        var cursor = new TurnStartChoiceCursor([choice with { SourceId = source }]);
        Combat.BeginActionChoices(cursor);
        try
        {
            if (!TurnStartChoiceSupport.ResolveCapturedChoice(simulator, Combat, owner, cursor, request)) return false;
            // Native ToastyMittens applies Strength AFTER the selected exhaust
            // and its reactive hooks. Runtime capture occurs before this tail.
            if (source == "ToastyMittens" || source == "TOASTY_MITTENS")
            {
                var relic = Combat.IterateHookListeners().OfType<MegaCrit.Sts2.Core.Models.RelicModel>()
                    .FirstOrDefault(r => r.Owner == owner && r.Id.Entry == "TOASTY_MITTENS" && !r.IsMelted);
                if (relic is not null) Combat.Apply<StrengthPower>(owner.Creature,
                    relic.DynamicVars.Strength.IntValue, owner.Creature);
            }
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, Combat);
            return !Combat.HasPendingChoice;
        }
        finally { Combat.EndActionChoices(); }
    }

    // Stable point AFTER the hand draw and BEFORE any after-start hook. The
    // vendor's existing stable-prefix scope suspends only an idle cursor; its
    // ordinary fork guards still reject active card/draw/power transactions.
    private Queue<PlanCardChoice>? PrepareAfterStartChoices(Player owner, bool isExtraTurn, bool sideStarted)
    {
        if (KernelSearchBudgetScope.IsStopped || KernelChoiceContinuation.Active || !AutoResolveTurnStartChoices
            || !Combat.IterateHookListeners().OfType<MegaCrit.Sts2.Core.Models.RelicModel>()
                .Any(r => r.Owner == owner && r.Id.Entry == "TOASTY_MITTENS" && !r.IsMelted)
                && Combat.GetAmount<ToolsOfTheTradePower>(owner.Creature) <= 0) return null;
        KernelSession seed;
        using (Combat.SuspendExecutionChoicesForStablePrefix())
            seed = new(simulator.Fork(), new(processedDeaths), liveRoot, rootTurnNumber)
                { RoundsAdvanced = RoundsAdvanced, extraTurnParticipants = new(extraTurnParticipants),
                    synchronousChoicePlayers = new(synchronousChoicePlayers) };
        var queue = new Queue<int[]>(); queue.Enqueue([]);
        var seen = new HashSet<string> { "" };
        var trials = new List<(KernelSession State, List<PlanCardChoice> Choices)>();
        using var guard = KernelChoiceContinuation.Enter();
        for (var trial = 0; trial < KernelChoiceContinuation.MaximumChoices
            && !KernelSearchBudgetScope.IsStopped && queue.TryDequeue(out var vector); trial++)
        {
            var child = seed.Fork();
            var resolved = new List<PlanCardChoice>();
            var counts = new List<int>();
            var position = 0;
            var cursor = TurnStartChoiceCursor.ForAutomaticPolicy(request =>
            {
                if (request.Spec is not { } spec) return null;
                var choices = spec.SourcePile == PileType.Hand
                    && spec.Effect is PlanChoiceEffect.Exhaust or PlanChoiceEffect.Discard
                    && !spec.IsImplicitAllSelection
                        ? KernelRelicChoices.OrderHandCleanupChoices(child.simulator, spec,
                            KernelRelicChoices.CleanupChoices(spec, 256))
                            .Take(KernelChoiceContinuation.MaximumChoices).ToArray()
                        : [KernelRelicChoices.Choose(child.simulator, owner, spec)];
                counts.Add(choices.Length);
                var index = position < vector.Length ? vector[position] : 0; position++;
                if (index >= choices.Length) return null;
                var choice = choices[index] with { SourceId = request.SourceId,
                    ContextId = request.ContextId, Timing = request.Timing };
                resolved.Add(choice); return choice;
            });
            var started = sideStarted;
            bool StartSide()
            {
                if (started) return true;
                started = true;
                return child.Combat.TriggerSideTurnStart(child.simulator,
                    MegaCrit.Sts2.Core.Combat.CombatSide.Player, [owner.Creature],
                    child.Combat.GetPlayerTurnNumber(owner) != 1, isExtraTurn);
            }
            child.Combat.BeginActionChoices(cursor);
            bool complete;
            try
            {
                child.Combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
                using (cursor.BeforeNextTake(StartSide))
                    complete = !child.Combat.TriggerAfterPlayerTurnStart(child.simulator, owner.Creature, cursor)
                        && (started || StartSide()) && child.SettleDeaths();
                if (complete)
                {
                    EnchantmentLifecycleSupport.TriggerAfterTurnStartOrbs(child.simulator, owner);
                    complete = !child.Combat.TriggerAutoPrePlayEarly(child.simulator, owner,
                        child.Combat.GetPlayerTurnNumber(owner), cursor, child.processedDeaths)
                        && !child.Combat.HasPendingChoice;
                    child.Combat.NormalizeAeonglassWithers(child.simulator);
                    child.Combat.NormalizeCardAfflictions(child.simulator);
                }
            }
            finally { child.Combat.EndActionChoices(); }
            KernelChoiceContinuation.EnqueueAlternatives(queue, seen, vector, counts);
            if (complete) trials.Add((child, resolved));
        }
        var evaluated = trials.Select(t => (t.Choices,
            Value: KernelChoiceContinuation.Value(seed, t.State, owner, trials.Count))).ToArray();
        var best = evaluated.Where(t => !double.IsNegativeInfinity(t.Value))
            .OrderByDescending(t => t.Value).FirstOrDefault();
        if (best.Choices is null)
            LastHandChoiceComparison = $"native-start candidates={trials.Count} fallback=no-complete-native-hand";
        return best.Choices is null ? null : new(best.Choices);
    }
}

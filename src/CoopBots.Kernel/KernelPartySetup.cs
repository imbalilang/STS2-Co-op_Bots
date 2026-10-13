using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    private HashSet<ulong> synchronousChoicePlayers = new();
    /// <summary>Snapshot seats whose patched native selectors return completed tasks.</summary>
    public KernelSession ConfigureSynchronousChoicePlayers(IEnumerable<ulong> netIds)
    {
        synchronousChoicePlayers = netIds.ToHashSet();
        return this;
    }
    private sealed record PreparedPartyMenu(PlanCardChoice Choice, string Signature);
    private Queue<PreparedPartyMenu>? preparedPartyMenus;
    private bool requirePreparedPartyMenu;
    private static string PartyMenuSignature(CardChoiceSpec spec)
        => $"{spec.MinCount}:{spec.MaxCount}:{spec.IsImplicitAllSelection}|"
            + string.Join('|', spec.Options.Select(c => c.Preview.Owner.NetId + ":" + CardChoiceSupport.ChoiceCardKey(c)))
            + ";source=" + string.Join('|', spec.SourceCards.Select(c => c.Preview.Owner.NetId + ":" + CardChoiceSupport.ChoiceCardKey(c)));

    private enum PartySetupStage { Energy, BeforeHand, Draw, CompensateDraw, AfterPlayer, Finish }
    private sealed record PartySetupFrame(Player Player, PartySetupStage Stage, int DrawHistoryStart)
        : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context) => this;
        public bool Resume(CombatPredictionSimulator simulator) => ContinuePartySetup(simulator,
            (SimulatedCombatState)simulator.State.CombatState, Player, Stage, DrawHistoryStart);
    }
    private sealed record PartyAutoPrePlayFrame(Player Player, ISet<uint> Deaths) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context) => SimulatedCombatState.ForkExecutionDeaths(Deaths, context);
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Deaths = context.RequireRemap(Deaths) };
        public bool Resume(CombatPredictionSimulator simulator)
        {
            simulator.AcknowledgeExecutionDispatch();
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            if (!CorePowerSupport.ApplyEnemyDeathPowers(simulator, combat, combat.KnownEnemies, Deaths)) return false;
            if (simulator.State.GetCreature(Player.Creature).IsDead) return true;
            simulator.State.GetPlayerCombatState(Player).Phase = PlayerTurnPhase.AutoPrePlay;
            if (combat.TriggerAutoPrePlayEarly(simulator, Player, combat.GetPlayerTurnNumber(Player),
                    combat.ActiveExecutionChoices, Deaths)) return false;
            simulator.State.GetPlayerCombatState(Player).Phase = PlayerTurnPhase.Play;
            combat.NormalizeAeonglassWithers(simulator);
            combat.NormalizeCardAfflictions(simulator);
            return !combat.HasPendingChoice;
        }
    }

    // Native SetupPlayerTurn ends after AfterPlayerTurnStart. Side hooks, orbs
    // and AutoPrePlay are distinct PARTY stages in CombatManager.StartTurn.
    private static bool ContinuePartySetup(CombatPredictionSimulator simulator,
        SimulatedCombatState combat, Player player, PartySetupStage stage, int drawHistoryStart = 0)
    {
        simulator.AcknowledgeExecutionDispatch();
        var cursor = combat.ActiveExecutionChoices;
        bool Paused(PartySetupStage next)
        {
            if (!combat.HasPendingChoice) return false;
            simulator.AppendExecutionContinuation(new PartySetupFrame(player, next, drawHistoryStart));
            return true;
        }
        if (stage <= PartySetupStage.Energy)
        {
            var state = simulator.State.GetPlayerCombatState(player);
            state.Phase = PlayerTurnPhase.Start;
            if (PersistentRelicSupport.ShouldPlayerResetEnergy(combat, player)) state.LoseEnergy(state.Energy);
            state.GainEnergy(PersistentPowerSupport.GetModifiedMaxEnergy(combat, player)
                + combat.ConsumeEnergyNextTurn(player));
            if (!PersistentPowerSupport.TriggerAfterEnergyReset(simulator, combat, player)) return false;
            TurnStartRelicSupport.TriggerAfterEnergyReset(simulator, combat, player);
            if (combat.HasPendingChoice) return false;
            TurnStartRelicSupport.TriggerAfterEnergyResetLate(simulator, combat, player);
            if (combat.HasPendingChoice) return false;
        }
        if (stage <= PartySetupStage.BeforeHand)
        {
            combat.PrepareBeforeHandDraw(simulator, player, cursor);
            if (Paused(PartySetupStage.Draw)) return false;
        }
        if (stage <= PartySetupStage.Draw)
        {
            drawHistoryStart = simulator.History.Entries.Count;
            var count = PersistentPowerSupport.ConsumeModifiedHandDraw(combat, player, CombatManager.baseHandDrawCount);
            simulator.Draw(player, count, fromHandDraw: true);
            if (Paused(PartySetupStage.CompensateDraw)) return false;
        }
        if (stage <= PartySetupStage.CompensateDraw)
        {
            using (simulator.BeginExecutionDispatch())
                TriggeredPowerSupport.CompensateHistorySince(simulator, combat, drawHistoryStart);
            if (Paused(PartySetupStage.AfterPlayer)) return false;
        }
        if (stage <= PartySetupStage.AfterPlayer)
        {
            combat.TriggerAfterPlayerTurnStart(simulator, player.Creature, cursor);
            if (Paused(PartySetupStage.Finish)) return false;
        }
        return !combat.HasPendingChoice;
    }

    // Same bounded complete-native-hand policy as the previous stable-after-
    // draw helper, now replaying the exact paused setup tail AFTER the shared
    // side/orb barrier. No hook that already ran can be executed twice.
    private Queue<PreparedPartyMenu>? PreparePartyStartChoices(Player owner, Player[] players,
        IReadOnlyDictionary<Player, CombatPredictionSimulator.PartySetupPause> pauses, KernelSession reference,
        bool beforeSide = false, bool isExtraTurn = false)
    {
        if (KernelChoiceContinuation.Active || !AutoResolveTurnStartChoices) return null;
        if (KernelSearchBudgetScope.IsStopped) return null;
        var first = pauses[owner];
        var steps = players.SelectMany(p => beforeSide
            ? pauses.TryGetValue(p, out var pending) ? pending.Continuation.Steps : []
            : (pauses.TryGetValue(p, out var after) ? after.Continuation.Steps : [])
                .Append(new PredictionExecutionStep(null, 0, null, new PartyAutoPrePlayFrame(p, processedDeaths)))).ToArray();
        var pause = first with { Continuation = new(first.Continuation.HistoryStart, steps) };
        var queue = new Queue<int[]>(); queue.Enqueue([]);
        var seen = new HashSet<string> { "" };
        var trials = new List<(KernelSession State, List<PreparedPartyMenu> Choices)>();
        using var guard = KernelChoiceContinuation.Enter();
        var limit = pause.Request.Spec?.Effect == PlanChoiceEffect.DiscardAndDraw
            ? KernelChoiceContinuation.MaximumChoices + 2 : KernelChoiceContinuation.MaximumChoices;
        for (var trial = 0; trial < limit && !KernelSearchBudgetScope.IsStopped
            && queue.TryDequeue(out var vector); trial++)
        {
            CombatPredictionSimulator fork;
            PredictionExecutionContinuation continuation;
            using (Combat.HasActiveActionChoices ? Combat.SuspendExecutionChoicesForStablePrefix() : null)
                fork = simulator.ForkPartySetupPause(pause, out continuation);
            var child = new KernelSession(fork, new(processedDeaths), liveRoot, rootTurnNumber)
                { RoundsAdvanced = RoundsAdvanced, extraTurnParticipants = new(extraTurnParticipants),
                    synchronousChoicePlayers = new(synchronousChoicePlayers) };
            // All candidates see the same known multiset, never the live top
            // card. Returned choices execute on the untouched native branch.
            foreach (var seat in players) child.SampleChoiceDrawPool(seat);
            var selected = new List<PreparedPartyMenu>();
            var counts = new List<int>();
            var position = 0;
            var cursor = TurnStartChoiceCursor.ForAutomaticPolicy(request =>
            {
                if (request.Spec is not { } spec) return null;
                var chooser = spec.Options.FirstOrDefault()?.Preview.Owner ?? owner;
                if (chooser != owner)
                    return KernelRelicChoices.Choose(child.simulator, chooser, spec) with {
                        SourceId = request.SourceId, ContextId = request.ContextId, Timing = request.Timing };
                var options = spec.Effect == PlanChoiceEffect.DiscardAndDraw
                    ? KernelRelicChoices.ReplacementChoices(child.simulator, spec).ToArray()
                    : spec.SourcePile == MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand
                    && spec.Effect is PlanChoiceEffect.Exhaust or PlanChoiceEffect.Discard
                    && !spec.IsImplicitAllSelection
                        ? KernelRelicChoices.OrderHandCleanupChoices(child.simulator, spec,
                            KernelRelicChoices.CleanupChoices(spec, 256))
                            .Take(KernelChoiceContinuation.MaximumChoices).ToArray()
                        : [KernelRelicChoices.Choose(child.simulator, chooser, spec)];
                counts.Add(options.Length);
                var index = position < vector.Length ? vector[position] : 0; position++;
                if (index >= options.Length) return null;
                var choice = options[index] with { SourceId = request.SourceId,
                    ContextId = request.ContextId, Timing = request.Timing };
                selected.Add(new(choice, PartyMenuSignature(spec))); return choice;
            });
            bool complete;
            if (beforeSide)
            {
                // All paused sibling scopes must be qualified in the fork, but
                // only the synchronous owner's task resumes before the side.
                child.simulator.DetachPreparedPartySetupProbe(continuation);
                var mapped = new Dictionary<Player, CombatPredictionSimulator.PartySetupPause>();
                var offset = 0;
                foreach (var p in players.Where(pauses.ContainsKey))
                {
                    var original = pauses[p];
                    var count = original.Continuation.Steps.Count;
                    mapped[p] = original with { Continuation = new(continuation.HistoryStart,
                        continuation.Steps.Skip(offset).Take(count).ToArray()) };
                    offset += count;
                }
                child.Combat.BeginActionChoices(cursor);
                try { complete = child.simulator.ResumePartySetupPause(mapped[owner]); }
                finally { child.Combat.EndActionChoices(); }
                mapped.Remove(owner);
                if (complete) complete = child.CompleteInstantPartyProbe(owner, players, mapped,
                    continuation.HistoryStart, cursor, isExtraTurn);
            }
            else
            {
                child.Combat.BeginActionChoices(cursor);
                try
                {
                    child.Combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
                    complete = child.simulator.ResumeExecutionContinuation(continuation) && child.SettleDeaths();
                }
                finally { child.Combat.EndActionChoices(); }
            }
            KernelChoiceContinuation.EnqueueAlternatives(queue, seen, vector, counts);
            if (complete) trials.Add((child, selected));
        }
        var evaluated = trials.Select(t => (t.Choices,
            Value: KernelChoiceContinuation.Value(reference, t.State, owner, trials.Count))).ToArray();
        var best = evaluated.Where(t => !double.IsNegativeInfinity(t.Value))
            .OrderByDescending(t => t.Value).FirstOrDefault();
        if (best.Choices is null)
            LastHandChoiceComparison = $"native-start candidates={trials.Count} fallback=no-complete-native-hand";
        return best.Choices is null ? null : new(best.Choices);
    }

    // Finish the native task graph on an analysis fork. Instant bot tasks finish
    // now; only genuinely deferred human tasks cross the shared side barrier.
    private bool CompleteInstantPartyProbe(Player owner, Player[] players,
        Dictionary<Player, CombatPredictionSimulator.PartySetupPause> paused,
        int historyStart, TurnStartChoiceCursor cursor, bool isExtraTurn)
    {
        foreach (var player in players.SkipWhile(p => p != owner).Skip(1))
        {
            simulator.BeginPartySetupCapture(historyStart);
            Combat.BeginActionChoices(new TurnStartChoiceCursor(null));
            Combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
            bool complete;
            try { complete = ContinuePartySetup(simulator, Combat, player, PartySetupStage.Energy); }
            finally { Combat.EndActionChoices(); simulator.EndExecutionContinuationCapture(); }
            if (complete) continue;
            var pause = simulator.DetachPartySetupPause(paused.Values.ToArray());
            if (pause is null) return false;
            if (!synchronousChoicePlayers.Contains(player.NetId)) { paused.Add(player, pause); continue; }
            Combat.BeginActionChoices(cursor);
            Combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
            try { if (!simulator.ResumePartySetupPause(pause)) return false; }
            finally { Combat.EndActionChoices(); }
        }
        Combat.BeginActionChoices(cursor);
        Combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
        try
        {
            if (!Combat.TriggerSideTurnStart(simulator, CombatSide.Player, players.Select(p => p.Creature).ToArray(),
                    Combat.GetPlayerTurnNumber(players[0]) != 1, isExtraTurn) || !SettleDeaths()) return false;
            foreach (var player in players) EnchantmentLifecycleSupport.TriggerAfterTurnStartOrbs(simulator, player);
            foreach (var player in players.Where(p => Hp(p.Creature) > 0))
            {
                if (paused.TryGetValue(player, out var pause) && !simulator.ResumePartySetupPause(pause)) return false;
                if (!new PartyAutoPrePlayFrame(player, processedDeaths).Resume(simulator)) return false;
            }
            return SettleDeaths();
        }
        finally { Combat.EndActionChoices(); }
    }

    private bool ResumePreparedPartySetup(Player player, CombatPredictionSimulator.PartySetupPause pause,
        IReadOnlyDictionary<Player, CombatPredictionSimulator.PartySetupPause> siblings,
        TurnStartChoiceCursor cursor, Func<CombatPredictionSimulator.PartySetupPause, Queue<PreparedPartyMenu>?> prepare)
    {
        // A sampled replacement can change a later actual menu. Never deploy
        // its cached future tokens against a different hand. Pause that native
        // frame again and compare the real menu, preserving all sibling scopes.
        for (var menu = 0; ; menu++)
        {
            // Bound expensive comparisons, not native legality/progress. Once
            // spent, the actual menu still receives its legal ordinary policy.
            preparedPartyMenus = menu < 32 ? prepare(pause) : null;
            requirePreparedPartyMenu = preparedPartyMenus is not null;
            var hadPreparedMenu = preparedPartyMenus is { Count: > 0 };
            Combat.BeginActionChoices(cursor);
            Combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
            bool complete;
            try { complete = simulator.ResumePartySetupPause(pause); }
            finally { Combat.EndActionChoices(); requirePreparedPartyMenu = false; }
            if (complete) return true;
            if (!hadPreparedMenu) return false;
            var next = simulator.DetachPartySetupPause(siblings.Values.ToArray());
            if (next is null) return false;
            pause = next;
        }
    }
}

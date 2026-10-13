using CoopBots.Kernel.Vendor;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    private HashSet<ulong> ready = new();
    private HashSet<ulong> extraTurnParticipants = new();
    private string? roundBoundary;
    /// <summary>
    /// When true, a turn-start choice this session meets is answered by the same
    /// automatic-policy shape the rollout's card choices already use, instead of
    /// failing closed. Rollouts need this: `Tools of the Trade` (and its family)
    /// triggers a discard at the start of the next player turn, and a fail-closed
    /// boundary there truncates every line at the same place — measured in the
    /// 2026-09-22 live run, 23 of 56 final-boss decisions stopped exactly there.
    ///
    /// The planning search keeps this false: a committed plan must record the
    /// actual choice, and a default guess is not a plan.
    /// </summary>
    internal bool AutoResolveTurnStartChoices { get; set; }
    /// <summary>
    /// When true, an enemy-turn choice this session meets is answered by a deterministic
    /// default instead of failing the round. The roll-out tournament has no plan vector
    /// for Knowledge Demon's per-player curse choice, so before this every final-boss
    /// roll-out stopped at `round-pending-choice:knowledge-demon/...` or threw when the
    /// per-target counter ran past its table. A default curse is a prediction, not a
    /// committed plan; the planning search keeps this false and branches normally.
    /// </summary>
    internal bool AutoResolveEnemyChoices { get; set; }
    /// <summary>
    /// WHICH failure inside the current step produced the boundary. `round-pending-choice`
    /// alone said a turn boundary failed closed and nothing about where; measured live
    /// 2026-09-21 the site label narrowed a lost run's truncations to `@enemy-phase`, and
    /// `RunEnemyPhase` alone has fourteen `return false` sites, so the label has to reach one
    /// level deeper still.
    /// </summary>
    private string? boundarySite;
    public bool EnemyPhaseCompleted { get; private set; }
    public string? LastRoundFailure { get; private set; }
    private string? lastLoggedRoundFailure;
    public bool IsReady(Player player) => ready.Contains(player.NetId);
    public bool CanAct(Player player) => !EnemyPhaseCompleted && !HasWon && !IsReady(player) && Hp(player.Creature) > 0;

    // Readiness is per player; lifecycle hooks belong to the shared side boundary.
    // Never implicitly end a human's turn to make a speculative bot plan work.
    // Default: settle at the enemy boundary (P1-1 semantics). Multi-round search
    // asks for more rounds explicitly through the searching caller.
    public bool EndTurn(Player player, out string boundary) => EndTurn(player, 1, out boundary);

    public bool EndTurn(Player player, int maxRounds, out string boundary)
        => EndTurn(player, maxRounds, out boundary, out _);

    private bool EndTurnCore(Player player, int maxRounds, out string boundary)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        roundBoundary = null;
        if (failedBoundary is not null) { boundary = failedBoundary; return false; }
        if (!Combat.Players.Contains(player) || !CanAct(player)) { boundary = "already-ended"; return false; }
        ready.Add(player.NetId);
        boundary = "";
        if (Combat.Players.Any(p => Hp(p.Creature) > 0 && !IsReady(p))) return true;
        return ResolveReadySide(player, maxRounds, out boundary);
    }

    // Native CreatureCmd.Kill ends a dead player's turn. If the last unready
    // actor dies to thorns/self-damage, nobody can explicitly EndTurn again.
    // Settle the already-ready side without inventing a deployable extra action.
    internal bool TryAdvanceReadySide(int maxRounds, out string boundary)
        => RunTurnStartTransition(null, maxRounds, pendingImplicitTurnStartReplay, out boundary);

    private bool TryAdvanceReadySideCore(int maxRounds, out string boundary)
    {
        boundary = "";
        if (EnemyPhaseCompleted || HasTerminal) return false;
        var alive = Combat.Players.Where(p => Hp(p.Creature) > 0).ToArray();
        if (alive.Length == 0 || alive.Any(p => !IsReady(p))) return false;
        return ResolveReadySide(alive[0], maxRounds, out boundary);
    }

    private bool ResolveReadySide(Player player, int maxRounds, out string boundary)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        roundBoundary = null;
        if (failedBoundary is not null) { boundary = failedBoundary; return false; }
        failedBoundary = "incomplete-round";
        Player[] players;
        Combat.BeginActionChoices([]);
        try
        {
            players = Combat.Players.Where(p => Hp(p.Creature) > 0
                && (extraTurnParticipants.Count == 0 || extraTurnParticipants.Contains(p.NetId))).ToArray();
            var participants = players.Select(p => p.Creature).ToArray();
            var takingExtraTurn = new List<Player>();
            foreach (var p in Combat.Players.Where(p => Hp(p.Creature) > 0))
            {
                if (!Combat.TryPrepareExtraPlayerTurn(simulator, p, out var extra, out _))
                    return Pending(out boundary, "prepare-extra-player-turn");
                if (extra) takingExtraTurn.Add(p);
            }
            var ethereal = players.ToDictionary(p => p.Creature, p => Combat.CountEtherealCardsInHand(simulator, p));
            var historyStart = simulator.History.Entries.Count;
            Combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnEnd);
            // --- player turn end, phase one. Mirrors PlayerTurnEndLifecycle.RunPhaseOne
            // (Vendor/Prediction/PlayerTurnEndLifecycle.cs:32-58) step for step. We used to
            // hand-roll a subset of that method and had dropped three of its calls; the
            // audit on 2026-09-20 found them by diffing the two sequences, which is the
            // only way they were ever going to surface — none of the three refuses a step,
            // throws, or logs, they just make the simulation quietly drift.
            foreach (var p in players)
                simulator.State.GetPlayerCombatState(p).Phase = PlayerTurnPhase.End;      // RunPhaseOne:39
            EndTurnPowerSupport.TriggerVeryEarly(Combat, participants);                    // :40
            if (Combat.HasPendingChoice) return Pending(out boundary, "phase-one-very-early");                     // :41
            TurnStartRelicSupport.TriggerBeforeSideTurnEnd(simulator, Combat, participants); // :43
            if (Combat.HasPendingChoice) return Pending(out boundary, "phase-one-before-side-turn-end");                     // :44
            if (!simulator.SimulateEndPlayerTurnBeforeOrbPassives(Combat.GetPlayerTurnNumber(player), players))
                return Pending(out boundary, "phase-one-end-turn-before-orbs");                                             // :47  (was missing)
            // :49-50 — a combat that is over or ending short-circuits phase one; upstream
            // returns true here and the caller moves on.
            if (!simulator.IsOverOrEnding)
            {
                if (Combat.HasPendingChoice || !simulator.SimulateEndPlayerTurnAfterOrbPassives(Combat.GetPlayerTurnNumber(player), players,
                        p => OrbLifecycleSupport.TriggerBeforeTurnEnd(simulator, Combat, p))) return Pending(out boundary, "phase-one-end-turn-after-orbs");
                CorePowerSupport.CompletePlayerEarlySideTurnEndEffects(Combat, participants); // :55
                if (Combat.HasPendingChoice) return Pending(out boundary, "phase-one-early-side-turn-end");                 // :56
            }
            Combat.NormalizeAeonglassWithers(simulator);
            Combat.NormalizeCardAfflictions(simulator);
            if (!SettleDeaths()) return Pending(out boundary, "settle-deaths");
            simulator.CheckWinCondition(Combat.GetPlayerTurnNumber(player));
            if (simulator.IsInProgress)
            {
                foreach (var p in players) CorePowerSupport.FlushPlayerHandAtTurnEnd(simulator, Combat, p);
                if (!PlayerTurnEndLifecycle.RunPhaseTwo(simulator, Combat, participants, etherealByOwner: ethereal)
                    || !SettleDeaths()) return Pending(out boundary, "phase-two-settle-deaths");
                simulator.CheckWinCondition(Combat.GetPlayerTurnNumber(player));
            }
            // Native SwitchFromPlayerToEnemySide skips the enemy side when any
            // seat takes an extra turn. Only that subset starts/draws/acts; the
            // other seats remain ready until the normal shared turn resumes.
            takingExtraTurn.RemoveAll(p => Hp(p.Creature) <= 0);
            if (takingExtraTurn.Count == 0)
            {
                var enemyTargets = Combat.Players.Where(p => Hp(p.Creature) > 0).ToArray();
                if (simulator.IsInProgress && !RunEnemyPhase(enemyTargets, historyStart)) return Pending(out boundary, "enemy-phase");
            }
            else
            {
                foreach (var extraPlayer in takingExtraTurn)
                {
                    Combat.RecordRelicRoundDamage(simulator, extraPlayer, historyStart);
                    Combat.ConsumeExtraTurnSources(extraPlayer);
                }
            }
            extraTurnParticipants = takingExtraTurn.Select(p => p.NetId).ToHashSet();
            if (Combat.HasPendingChoice) return Pending(out boundary, "enemy-phase-pending");
            LastActionHadEnvironmentalRisk |= PredictionCoverage.Collect(simulator).Any(g => !g.Compensated);
            failedBoundary = null;
        }
        catch (Exception error)
        {
            LastRoundFailure = error.ToString();
            boundary = failedBoundary = "round-exception:" + error.GetType().Name;
            // NAME THE CAUSE, ONCE PER DISTINCT FAILURE. The boundary alone said only
            // `round-exception:InvalidOperationException`; measured 2026-09-22 that left
            // 69 final-boss decisions with no way to tell a Knowledge Demon counter
            // overrun from a hook null-reference. `lastLoggedRoundFailure` keeps a
            // frame-loop from reprinting the same stack every tick.
            if (!string.Equals(lastLoggedRoundFailure, LastRoundFailure, StringComparison.Ordinal))
            {
                lastLoggedRoundFailure = LastRoundFailure;
                Log.Warn($"CoopBots kernel: round settlement exception: "
                    + $"{error.GetType().Name}: {error.Message}\n{error.StackTrace}");
            }
            return false;
        }
        finally { Combat.EndActionChoices(); }
        return CompleteReadySideBoundary(maxRounds, out boundary);
    }

    private bool CompleteReadySideBoundary(int maxRounds, out string boundary)
    {
        // The enemy phase is a settled boundary. If rounds remain in the search
        // budget, start the next player turn outside the end-turn choice scope
        // and keep searching (P1-2); otherwise stop as a resolved terminal node.
        if (RoundsAdvanced + 1 < maxRounds && !HasWon && simulator.IsInProgress)
        {
            var nextPlayers = extraTurnParticipants.Count == 0 ? Combat.Players.ToArray()
                : Combat.Players.Where(p => extraTurnParticipants.Contains(p.NetId)).ToArray();
            if (!StartNextPlayerTurn(nextPlayers, isExtraTurn: extraTurnParticipants.Count > 0))
                return Pending(out boundary, "start-next-player-turn");
            RoundsAdvanced++;
            EnemyPhaseCompleted = false;
            HorizonClosed = false;
            ready.Clear();
            // Native StartTurn readies dead seats and extra-turn nonparticipants
            // after setup. Use settled branch HP: death prevention may have healed
            // a seat, and its captured live object must not decide future readiness.
            foreach (var seat in Combat.Players.Where(p => Hp(p.Creature) <= 0
                || extraTurnParticipants.Count > 0 && !extraTurnParticipants.Contains(p.NetId)))
                ready.Add(seat.NetId);
        }
        else
        {
            EnemyPhaseCompleted = true;
            // BOUNDED LOOKAHEAD: no rounds remain and the fight is still going, so THIS is the
            // horizon. Only when the fight is genuinely unfinished — a victory or a wipe is a
            // terminal, not a horizon, and the scoring keeps those strictly above this.
            HorizonClosed = !HasWon && simulator.IsInProgress;
        }
        boundary = "";
        return true;
    }

    // Mirrors the player half of the native round advance for the whole party:
    // turn number, turn-start relics/powers, block clear, energy reset, draw and
    // auto-pre-play. Any step that needs a choice we cannot answer fails closed.
    private bool StartNextPlayerTurn(Player[] players, bool isExtraTurn)
    {
        var choices = TurnStartChoiceCursor.ForAutomaticPolicy(request =>
            IsTurnStartReplay || AutoResolveTurnStartChoices ? ResolveRecordedTurnStartChoice(request) : null);
        Combat.BeginActionChoices(choices);
        try
        {
            Combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
            Combat.CurrentSide = CombatSide.Player;
            if (!isExtraTurn) Combat.RoundNumber++;
            // A normal native SwitchSides advances every party member, including
            // dead seats; an extra turn advances only its participating players.
            foreach (var seat in isExtraTurn ? players : Combat.Players)
            {
                Combat.CommitHistoryCourseTurn(seat);
                Combat.AdvancePlayerTurn(seat);
                if (turnStartOwners is not null) turnStartOwners[seat.NetId] = Combat.GetPlayerTurnNumber(seat);
            }
            // The history window is shared by the side; a dead player's last plays
            // must not survive as this turn's counters in the party continuation.
            ResetSideHistory();
            Combat.SetPlayerTurnParticipation(players);
            var activePlayers = players.Where(p => Hp(p.Creature) > 0).ToArray();
            turnStartChoiceOwner = activePlayers.FirstOrDefault();
            var participants = activePlayers.Select(p => p.Creature).ToArray();
            // CombatManager.StartTurn: ALL BeforeTurnStart -> shared BeforeSide;
            // ALL block clears -> ALL AfterBlockCleared. Per-seat interleaving is
            // observable when later-seat relics damage an enemy before its Rampart.
            Combat.SnapshotPowerAmountsAtTurnStart(participants);
            if (!TurnStartRelicSupport.TriggerBeforeSideTurnStart(simulator, Combat, participants)) return false;
            // Native BeforeSideTurnStart awaits each power command before block
            // clearing; do not postpone its notifications until AfterBlockCleared.
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, Combat);
            if (Combat.HasPendingChoice) return false;
            if (TurnStartPowerSupport.TriggerBeforeSideTurnStart(simulator, Combat, participants)) return false;
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, Combat);
            if (Combat.HasPendingChoice) return false;
            foreach (var creature in participants)
            {
                var state = simulator.State.GetCreature(creature);
                if (state.Block <= 0) continue;
                if (Combat.ShouldClearBlock(creature, out var preventer)) state.DamageBlock(state.Block, ValueProp.Move);
                else PersistentRelicSupport.TriggerAfterPreventingBlockClear(simulator, preventer, creature);
            }
            foreach (var creature in participants)
            {
                if (!CorePowerSupport.TriggerAfterBlockCleared(simulator, Combat, creature)) return false;
                // Native AfterBlockCleared awaits PowerCmd.Apply and its amount-change
                // listeners before the next seat and SetupPlayerTurn. The multiplayer
                // choice-reference fork below is earlier than the upstream whole-turn
                // settle, so discharge those real notifications here (Sparkling Rouge
                // grants Strength/Dexterity on turn 3). Do not discard the effects or
                // relax the simulator's pending-power fork qualification.
                PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, Combat);
                if (Combat.HasPendingChoice) return false;
            }
            Combat.EndActionChoices();
            var choiceReference = IsTurnStartReplay ? this : new KernelSession(simulator.Fork(), new(processedDeaths), liveRoot, rootTurnNumber)
                { RoundsAdvanced = RoundsAdvanced, extraTurnParticipants = new(extraTurnParticipants),
                    synchronousChoicePlayers = new(synchronousChoicePlayers) };
            var paused = new Dictionary<Player,
                CoopBots.Kernel.Vendor.Engine.InCombat.Simulation.CombatPredictionSimulator.PartySetupPause>();
            var setupHistoryStart = simulator.PreparePartySetupCapture();
            var preparedInstant = new Dictionary<Player, Queue<PreparedPartyMenu>?>();
            foreach (var player in activePlayers)
            {
                turnStartChoiceOwner = player;
                // Capture the first menu so the complete native tail can be
                // compared. A patched Bot selector completes synchronously;
                // a genuine human selector keeps its task paused until Side.
                simulator.BeginPartySetupCapture(setupHistoryStart);
                Combat.BeginActionChoices(new TurnStartChoiceCursor(null));
                bool complete;
                try
                {
                    Combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
                    complete = ContinuePartySetup(simulator, Combat, player, PartySetupStage.Energy);
                }
                finally { Combat.EndActionChoices(); simulator.EndExecutionContinuationCapture(); }
                if (!complete)
                {
                    var continuation = simulator.DetachPartySetupPause(paused.Values.ToArray());
                    if (continuation is null) return false;
                    if (!synchronousChoicePlayers.Contains(player.NetId)) paused.Add(player, continuation);
                    else
                    {
                        if (!ResumePreparedPartySetup(player, continuation, paused, choices, current => {
                            if (IsTurnStartReplay) return null;
                            var probes = new Dictionary<Player, CoopBots.Kernel.Vendor.Engine.InCombat.Simulation.CombatPredictionSimulator.PartySetupPause>(paused)
                                { [player] = current };
                            return PreparePartyStartChoices(player, activePlayers, probes, choiceReference,
                                beforeSide: true, isExtraTurn: isExtraTurn);
                        })) return false;
                        preparedInstant[player] = preparedPartyMenus;
                    }
                }
            }
            Combat.BeginActionChoices(choices);
            Combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
            // Native AfterSideTurnStart occurs exactly once after every setup
            // reached completion/pause. Rampart runs after every completed
            // MercuryHourglass, while a setup paused BEFORE its relic resumes
            // after Rampart and can correctly have its damage blocked.
            if (!Combat.TriggerSideTurnStart(simulator, CombatSide.Player, participants,
                    Combat.GetPlayerTurnNumber(activePlayers.FirstOrDefault() ?? players[0]) != 1, isExtraTurn)) return false;
            if (!SettleDeaths()) return false;
            foreach (var player in activePlayers)
                EnchantmentLifecycleSupport.TriggerAfterTurnStartOrbs(simulator, player);
            Combat.EndActionChoices();
            foreach (var player in activePlayers.Where(p => Hp(p.Creature) > 0))
            {
                turnStartChoiceOwner = player;
                preparedPartyMenus = preparedInstant.GetValueOrDefault(player);
                if (paused.TryGetValue(player, out var continuation))
                {
                    var remaining = activePlayers.SkipWhile(p => p != player).ToArray();
                    var siblings = paused.Where(p => remaining.Contains(p.Key) && p.Key != player)
                        .ToDictionary(p => p.Key, p => p.Value);
                    if (!ResumePreparedPartySetup(player, continuation, siblings, choices, current => {
                        if (IsTurnStartReplay) return null;
                        var probes = new Dictionary<Player, CoopBots.Kernel.Vendor.Engine.InCombat.Simulation.CombatPredictionSimulator.PartySetupPause>(siblings)
                            { [player] = current };
                        return PreparePartyStartChoices(player, remaining, probes, choiceReference);
                    })) return false;
                }
                Combat.BeginActionChoices(choices);
                Combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
                if (!SettleDeaths()) return false;
                simulator.State.GetPlayerCombatState(player).Phase = PlayerTurnPhase.AutoPrePlay;
                if (Combat.TriggerAutoPrePlayEarly(simulator, player, Combat.GetPlayerTurnNumber(player),
                        choices, processedDeaths)) return false;
                simulator.State.GetPlayerCombatState(player).Phase = PlayerTurnPhase.Play;
                Combat.NormalizeAeonglassWithers(simulator);
                Combat.NormalizeCardAfflictions(simulator);
                preparedAfterStartChoices = null;
                preparedPartyMenus = null;
                Combat.EndActionChoices();
            }
            Combat.SetPredictedEnemyIntents(Combat.CurrentMonsterMoves()
                .Where(move => move.AttackHits.Count > 0).Select(move => move.Owner));
            simulator.CheckWinCondition(Combat.GetPlayerTurnNumber(players[0]));
            return !Combat.HasPendingChoice;
        }
        finally { preparedAfterStartChoices = null; preparedPartyMenus = null; requirePreparedPartyMenu = false;
            if (Combat.HasActiveActionChoices) Combat.EndActionChoices(); }
    }

    /// <summary>
    /// Deterministic default answer for a turn-start choice during a rollout.
    ///
    /// The request always carries the spec its producer built (`ResolvePileDiscard`,
    /// `Resolve`, `ResolveGeneratedToHand` all set it). A request without a spec is left
    /// unanswered, so this can only turn a modeled choice into a modeled default — it
    /// cannot invent an answer for a mechanism the engine did not describe.
    /// </summary>
    private PlanCardChoice? AutomaticTurnStartChoice(TurnStartChoiceRequest request)
    {
        if (request.Spec is not { } spec) return null;
        if (preparedPartyMenus is { Count: > 0 } party)
        {
            var cached = party.Peek();
            var plan = cached.Choice;
            if (plan.SourceId == request.SourceId && plan.ContextId == request.ContextId
                && plan.Effect == request.Effect && plan.SourcePile == request.SourcePile
                && cached.Signature == PartyMenuSignature(spec)) return party.Dequeue().Choice;
            preparedPartyMenus = null;
            // Re-capture the actual native tail and compare this menu afresh.
            // Cached sampled future cards are never resolved against this hand.
            if (requirePreparedPartyMenu) return null;
        }
        else if (requirePreparedPartyMenu) return null;
        if (preparedAfterStartChoices is { Count: > 0 } prepared
            && prepared.Peek() is { } planned && planned.SourceId == request.SourceId
            && planned.ContextId == request.ContextId && planned.Effect == request.Effect
            && planned.SourcePile == request.SourcePile) return prepared.Dequeue();
        var owner = spec.Options.FirstOrDefault()?.Preview.Owner;
        return (owner is null ? CardChoiceSupport.BuildAutomaticPolicyChoice(spec)
            : KernelRelicChoices.Choose(simulator, owner, spec)) with
        {
            SourceId = request.SourceId,
            ContextId = request.ContextId,
            Timing = request.Timing,
        };
    }

    /// <summary>
    /// A turn-boundary settle hit a pending choice and the round fails closed here.
    /// </summary>
    /// <remarks>
    /// THE BOUNDARY NAMES THE CHOICE, and that is a diagnostic fix, not a behavioural one —
    /// <c>Pending</c> still returns false exactly as before, and nothing compares these strings
    /// (checked: the only other producer is <c>enemy-move-unmodeled:...</c> at the enemy-move
    /// site below).
    ///
    /// It used to be the bare literal `round-pending-choice`, which says that SOMETHING at the
    /// turn boundary wants a choice and nothing about which. Measured live 2026-09-21: a lost
    /// run carried four of them, and none could be attributed — which blocks B11, whose standing
    /// protocol is "read the NAMED pending choice from a live log, and only then touch the
    /// settlement". The SteamEruption site has printed its own name since the 2026-09-20
    /// investigation; the other sites never did, and those are the ones that fired.
    ///
    /// SourceId only, never the full description: the description carries ContextId, which is
    /// unbounded and would fragment the `boundaries=` histogram the live triage reads.
    /// </remarks>
    private bool Pending(out string boundary, string site)
    {
        // The SITE goes in the name too. `round-pending-choice` alone says a turn boundary
        // failed closed but not where, and there are TEN of these sites — measured live
        // 2026-09-21: a lost run carried two bare ones, the choice-naming fallback ruled out
        // both a turn-start choice and a knowledge-demon choice, and nothing was left to
        // attribute them to. B11 cannot be fixed from a name that does not narrow anything.
        //
        // The `round-pending-choice` prefix is preserved so every existing grep and every
        // `boundaries=` reading still finds these; the site is appended.
        boundary = failedBoundary = roundBoundary ?? PendingBoundary(Combat.PendingTurnStartChoice?.SourceId,
            Combat.PendingKnowledgeDemonChoice?.GetType().Name, boundarySite ?? site);
        roundBoundary = null;
        boundarySite = null;
        return false;
    }

    /// <summary>
    /// The name a turn-boundary truncation carries: WHAT was pending (if anything) and WHERE it
    /// failed closed. Separated from <see cref="Pending"/> so the contract is assertable without
    /// a board sitting on a pending choice at a round boundary.
    /// </summary>
    internal static string PendingBoundary(string? sourceId, string? knowledgeDemonType, string site) =>
        $"{NamePendingChoice(sourceId, knowledgeDemonType)}@{site}";

    /// <summary>
    /// The naming rule itself, separated so it can be asserted without a board that happens to
    /// be sitting on a pending choice at a round boundary — which the live path reaches only in
    /// some fights (measured: 4 times in one run, 0 times in the next two, always late in a
    /// fight). A naming rule that cannot be tested is a naming rule that quietly stops naming.
    /// </summary>
    internal static string NamePendingChoice(string? sourceId, string? knowledgeDemonType)
    {
        if (sourceId is { Length: > 0 }) return $"round-pending-choice:{sourceId}";
        if (knowledgeDemonType is { Length: > 0 }) return $"round-pending-choice:knowledge-demon/{knowledgeDemonType}";
        // The ORIGINAL name, preserved exactly. Every `boundaries=` histogram taken before this
        // change groups these under the bare literal, and a fallback that renamed itself would
        // silently split that history in two.
        return "round-pending-choice";
    }
    private bool SettleDeaths() => CorePowerSupport.ApplyEnemyDeathPowers(simulator, Combat, Combat.KnownEnemies, processedDeaths);

    // Native HappenedThisTurn expires history for every actor when the side
    // or any party turn number changes. BeginSideTurn resets only these cached
    // counters; actual power/relic hooks below still receive active participants.
    private void ResetSideHistory()
    {
        foreach (var actor in Combat.Creatures.Concat(Combat.Players.Select(p => p.Creature)).Distinct())
            Combat.BeginSideTurn(actor);
    }
    /// <summary>Record which step inside the enemy phase failed, then fail the round closed.</summary>
    private bool EnemyPhaseFail(string site)
    {
        boundarySite = site;
        return false;
    }

    private bool RunEnemyPhase(Player[] players, int historyStart)
    {
        Combat.SetActionChoiceTiming(PlanChoiceTiming.EnemyTurn);
        var acting = Combat.Enemies.ToArray();
        Combat.CurrentSide = CombatSide.Enemy;
        // The side switch also expires player history. Reset pure caches for all
        // actors here; the enemy lifecycle itself still uses only enemy participants.
        ResetSideHistory();
        Combat.SnapshotPowerAmountsAtTurnStart(Combat.Enemies);
        if (!TurnStartRelicSupport.TriggerBeforeSideTurnStart(simulator, Combat, Combat.Enemies)
            || TurnStartPowerSupport.TriggerBeforeSideTurnStart(simulator, Combat, Combat.Enemies)) return EnemyPhaseFail("side-turn-start-triggers");
        foreach (var enemy in Combat.Enemies.ToArray())
        {
            var state = simulator.State.GetCreature(enemy);
            if (state.Block > 0)
            {
                if (Combat.ShouldClearBlock(enemy, out var preventer)) state.DamageBlock(state.Block, ValueProp.Move);
                else PersistentRelicSupport.TriggerAfterPreventingBlockClear(simulator, preventer, enemy);
            }
            if (!CorePowerSupport.TriggerAfterBlockCleared(simulator, Combat, enemy)) return EnemyPhaseFail("block-cleared");
        }
        if (!Combat.TriggerSideTurnStart(simulator, CombatSide.Enemy, Combat.Enemies, Combat.RoundNumber > 1)) return EnemyPhaseFail("side-turn-start");
        var poisonStart = simulator.History.Entries.Count;
        if (!CorePowerSupport.TriggerPoison(simulator, Combat, Combat.Enemies.ToArray())) return EnemyPhaseFail("poison-apply");
        TriggeredPowerSupport.CompensateHistorySince(simulator, Combat, poisonStart);
        if (!SettleDeaths()) return EnemyPhaseFail("settle-deaths");
        bool Over() => simulator.CheckWinCondition(Combat.GetPlayerTurnNumber(players[0]));
        if (Over()) return true;
        var performed = new Dictionary<Creature, MoveState>();
        foreach (var enemy in acting)
        {
            if (!Combat.CanPerformMonsterMove(simulator, enemy)) continue;
            var move = Combat.CurrentMonsterMove(enemy);
            performed[enemy] = move.Move;
            if (!Combat.ConsumeStunNextMove(enemy))
            {
                var targets = players.Select(p => p.Creature).Where(c => Hp(c) > 0).ToArray();
                if (Combat.TryConsumeForcedMonsterMove(enemy, out var forced, out var damage))
                {
                    // THE TWO-PHASE BOSS, and the reason this branch exists at all.
                    //
                    // TryConsumeForcedMonsterMove drives WaterfallGiant's SteamEruption
                    // transition and returns the move id for whichever half it is in. The old
                    // code accepted only "EXPLODE_MOVE" and failed every other forced move
                    // closed, so the WIND-UP half never got through: every line that killed this
                    // boss became a boundary and the search's best usable line was "survive five
                    // more rounds". Measured live 2026-09-21 — three of six runs died at this
                    // boss, and the boundary name was
                    // `round-pending-choice@forced-move-unmodeled`. It fired only when the boss
                    // died WITH SteamEruptionPower on it, which is why it looked intermittent:
                    // that power comes from the boss's own pressurise moves.
                    //
                    // The wind-up is a NO-OP, read off the model rather than guessed:
                    // MonsterMoveEffects' own case for this move is exactly
                    // `combat.PrepareSteamEruption(move.Owner); return true;` — no damage, and
                    // the boss does not die. PrepareSteamEruption has already run inside
                    // TryConsumeForcedMonsterMove by the time control returns here.
                    if (forced == "ABOUT_TO_BLOW_MOVE")
                    {
                        // Wind-up turn: fall through to the pending-choice and Over checks below.
                    }
                    else if (forced == "EXPLODE_MOVE")
                    {
                        foreach (var target in targets) MonsterMoveSemantics.DamagePlayer(simulator, Combat, enemy, target, damage);
                        simulator.Kill(enemy, force: true);
                        if (!SettleDeaths()) return EnemyPhaseFail("settle-deaths-after-forced-move");
                    }
                    else
                    {
                        return EnemyPhaseFail("forced-move-unmodeled");
                    }
                }
                else
                {
                    if (!MonsterMoveEffects.Supports(enemy.Monster!, move.Move.Id) && !IsInert(move.Move))
                    {
                        if (move.Move.Intents.Any(i => i is not AttackIntent))
                        {
                            // Fail this round closed as a boundary instead of
                            // throwing: a single unmodeled monster move must not
                            // disable the kernel for the whole fight.
                            roundBoundary = $"enemy-move-unmodeled:{enemy.Monster!.Id.Entry}/{move.Move.Id}";
                            return EnemyPhaseFail("unmodeled-move-boundary");
                        }
                        LastActionHadEnvironmentalRisk = true;
                    }
                    if (!MonsterMoveSemantics.ApplyPartyMove(simulator, Combat, move, targets, processedDeaths,
                            AutoResolveEnemyChoices)) return EnemyPhaseFail("party-move");
                }
                if (enemy.CombatId is uint id && Hp(enemy) > 0) processedDeaths.Remove(id);
            }
            if (Combat.HasPendingChoice) return EnemyPhaseFail("pending-choice");
            if (Over()) return true;
        }
        if (!CorePowerSupport.TriggerEnemySideTurnEndEffects(simulator, Combat, Combat.Enemies.ToArray()) || !SettleDeaths()) return EnemyPhaseFail("enemy-side-turn-end");
        if (Combat.BattlewornDummyTimedOut) return EnemyPhaseFail("dazed-timed-out");
        poisonStart = simulator.History.Entries.Count;
        if (!CorePowerSupport.TriggerPoison(simulator, Combat, players.Select(p => p.Creature).ToArray())) return EnemyPhaseFail("poison-player-side");
        TriggeredPowerSupport.CompensateHistorySince(simulator, Combat, poisonStart);
        foreach (var p in players) { Combat.ClearNoDraw(p.Creature); Combat.RecordRelicRoundDamage(simulator, p, historyStart); }
        if (!SettleDeaths()) return EnemyPhaseFail("settle-deaths-end");
        if (!Over()) Combat.PrepareMonsterMovesForNextRound(simulator, performed);
        // Stop at a complete enemy boundary. Next-turn draw/search is P1-2.
        return !Combat.HasPendingChoice;
    }

    // A monster move whose only intents are Sleep, Stun or Hidden is the enemy
    // doing nothing on its own turn, which needs no per-monster entry — it is the
    // same rule the intent forecaster already applies. Without this, Rocket's
    // RECHARGE_MOVE (a Sleep-intent breather that changes no combat number) was
    // recorded as `enemy-move-unmodeled:ROCKET/RECHARGE_MOVE` thirty times in one
    // fight and **failed every end-turn branch closed**, so the kernel could not
    // look past the current turn at all while that enemy was in play.
    private static bool IsInert(MoveState move) => move.Intents.Count > 0
        && move.Intents.All(intent => intent.IntentType is IntentType.Sleep or IntentType.Stun or IntentType.Hidden);
}

using CoopBots.Kernel.Vendor;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    private HashSet<ulong> ready = new();
    private bool capturedExtraTurn;
    private string? roundBoundary;
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
    public bool IsReady(Player player) => ready.Contains(player.NetId);
    public bool CanAct(Player player) => !EnemyPhaseCompleted && !HasWon && !IsReady(player) && Hp(player.Creature) > 0;

    // Readiness is per player; lifecycle hooks belong to the shared side boundary.
    // Never implicitly end a human's turn to make a speculative bot plan work.
    // Default: settle at the enemy boundary (P1-1 semantics). Multi-round search
    // asks for more rounds explicitly through the searching caller.
    public bool EndTurn(Player player, out string boundary) => EndTurn(player, 1, out boundary);

    public bool EndTurn(Player player, int maxRounds, out string boundary)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        roundBoundary = null;
        if (failedBoundary is not null) { boundary = failedBoundary; return false; }
        if (!Combat.Players.Contains(player) || !CanAct(player)) { boundary = "already-ended"; return false; }
        ready.Add(player.NetId);
        boundary = "";
        if (Combat.Players.Any(p => Hp(p.Creature) > 0 && !IsReady(p))) return true;
        failedBoundary = "incomplete-round";
        Player[] players;
        Combat.BeginActionChoices([]);
        try
        {
            if (capturedExtraTurn) { boundary = failedBoundary = "extra-player-turn"; return false; }
            players = Combat.Players.Where(p => Hp(p.Creature) > 0).ToArray();
            var participants = players.Select(p => p.Creature).ToArray();
            foreach (var p in players)
            {
                if (!Combat.TryPrepareExtraPlayerTurn(simulator, p, out var extra, out _) || extra)
                { boundary = failedBoundary = "extra-player-turn"; return false; }
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
            if (!simulator.SimulateEndPlayerTurnBeforeOrbPassives(Combat.GetPlayerTurnNumber(player)))
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
            foreach (var p in players) Combat.CommitHistoryCourseTurn(p);
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
            if (simulator.IsInProgress && !RunEnemyPhase(players, historyStart)) return Pending(out boundary, "enemy-phase");
            if (Combat.HasPendingChoice) return Pending(out boundary, "enemy-phase-pending");
            LastActionHadEnvironmentalRisk |= PredictionCoverage.Collect(simulator).Any(g => !g.Compensated);
            failedBoundary = null;
        }
        catch (Exception error) { LastRoundFailure = error.ToString(); boundary = failedBoundary = "round-exception:" + error.GetType().Name; return false; }
        finally { Combat.EndActionChoices(); }
        // The enemy phase is a settled boundary. If rounds remain in the search
        // budget, start the next player turn outside the end-turn choice scope
        // and keep searching (P1-2); otherwise stop as a resolved terminal node.
        if (RoundsAdvanced + 1 < maxRounds && !HasWon && simulator.IsInProgress)
        {
            if (!StartNextPlayerTurn(players)) return Pending(out boundary, "start-next-player-turn");
            RoundsAdvanced++;
            EnemyPhaseCompleted = false;
            ready.Clear();
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
    private bool StartNextPlayerTurn(Player[] players)
    {
        var choices = TurnStartChoiceCursor.ForAutomaticPolicy(_ => null);
        Combat.BeginActionChoices(choices);
        try
        {
            Combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
            Combat.CurrentSide = CombatSide.Player;
            Combat.RoundNumber++;
            foreach (var player in players)
            {
                Combat.AdvancePlayerTurn(player);
                // The per-turn counter reset, and it was MISSING here until 2026-09-20.
                // Upstream's round transition is the same sequence — CurrentSide,
                // RoundNumber, AdvancePlayerTurn, BeginSideTurn, SnapshotPowerAmounts
                // (CombatBeamSolver.RoundTransition.cs:26-32) — and we copied every step
                // except this one, so nothing that "this turn" counts was ever reset on
                // the player side: cards played, attacks, skills, block cards, Shivs,
                // zero-cost attack starts, exhausts, discards, play-series.
                //
                // The visible symptom was a boundary refusal (`field=Y expected={0/1/4/4}
                // actual={0/0/0/0}` — the plan carrying turn one's totals into turn two
                // while the live board correctly showed zero). The real damage was larger:
                // every turn-two-and-later line the search priced was evaluated against
                // turn-one counters that should have been back at zero, which is exactly
                // the "simulator and executor do not share one state" failure this whole
                // feature rests on.
                Combat.BeginSideTurn(player.Creature);
                Combat.SnapshotPowerAmountsAtTurnStart([player.Creature]);
                if (!TurnStartRelicSupport.TriggerBeforeSideTurnStart(simulator, Combat, [player.Creature])) return false;
                if (TurnStartPowerSupport.TriggerBeforeSideTurnStart(simulator, Combat, [player.Creature])) return false;
                var creature = simulator.State.GetCreature(player.Creature);
                if (creature.Block > 0)
                {
                    if (Combat.ShouldClearBlock(player.Creature, out var preventer)) creature.DamageBlock(creature.Block, ValueProp.Move);
                    else PersistentRelicSupport.TriggerAfterPreventingBlockClear(simulator, preventer, player.Creature);
                }
                if (!CorePowerSupport.TriggerAfterBlockCleared(simulator, Combat, player.Creature)) return false;
                var playerState = simulator.State.GetPlayerCombatState(player);
                if (PersistentRelicSupport.ShouldPlayerResetEnergy(Combat, player)) playerState.LoseEnergy(playerState.Energy);
                playerState.GainEnergy(PersistentPowerSupport.GetModifiedMaxEnergy(Combat, player)
                    + Combat.ConsumeEnergyNextTurn(player));
                if (Combat.HasPendingChoice
                    || !PersistentPowerSupport.TriggerAfterEnergyReset(simulator, Combat, player)) return false;
                TurnStartRelicSupport.TriggerAfterEnergyReset(simulator, Combat, player);
                if (Combat.HasPendingChoice) return false;
                TurnStartRelicSupport.TriggerAfterEnergyResetLate(simulator, Combat, player);
                if (Combat.HasPendingChoice) return false;
                if (!Combat.TriggerSideTurnStart(simulator, CombatSide.Player, [player.Creature],
                        Combat.GetPlayerTurnNumber(player) != 1, isExtraTurn: false)) return false;
                if (Combat.PrepareBeforeHandDraw(simulator, player, choices)) return false;
                var historyStart = simulator.History.Entries.Count;
                var drawCount = PersistentPowerSupport.ConsumeModifiedHandDraw(Combat, player, CombatManager.baseHandDrawCount);
                simulator.Draw(player, drawCount, fromHandDraw: true);
                if (Combat.HasPendingChoice) return false;
                TriggeredPowerSupport.CompensateHistorySince(simulator, Combat, historyStart);
                if (Combat.TriggerAfterPlayerTurnStart(simulator, player.Creature, choices)) return false;
                if (!SettleDeaths()) return false;
                EnchantmentLifecycleSupport.TriggerAfterTurnStartOrbs(simulator, player);
                if (Combat.TriggerAutoPrePlayEarly(simulator, player, Combat.GetPlayerTurnNumber(player),
                        choices, processedDeaths)) return false;
                Combat.NormalizeAeonglassWithers(simulator);
                Combat.NormalizeCardAfflictions(simulator);
            }
            Combat.SetPredictedEnemyIntents(Combat.CurrentMonsterMoves()
                .Where(move => move.AttackHits.Count > 0).Select(move => move.Owner));
            simulator.CheckWinCondition(Combat.GetPlayerTurnNumber(players[0]));
            return !Combat.HasPendingChoice;
        }
        finally { Combat.EndActionChoices(); }
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
        // Same omission as the player side of this file, found by the same sequence diff.
        // Upstream resets every enemy's per-turn state right here:
        //
        //     foreach (Creature enemy in simulatedCombat.Enemies)
        //         simulatedCombat.BeginSideTurn(enemy);
        //
        // (CombatBeamSolver.Expansion.cs:3219-3220, immediately before
        // SnapshotPowerAmountsAtTurnStart.) Without it the enemy-side per-turn counters and
        // power-lifecycle state accumulate across rounds inside the simulation, so every
        // round-two-and-later line is priced against enemy bookkeeping the live game has
        // already cleared. Covered by an assertion too, but only after a detour: enemy
        // per-turn counters are invisible in the state text, so the first version of that
        // check had to be rewritten once (it asserted 0 at a point where the enemy had
        // legitimately attacked) and the mutation check caught that it proved nothing. The
        // final form crosses TWO rounds and expects the attack count to be 1, because with
        // one round "it reset and then attacked" and "it never reset" are the same number.
        foreach (var enemy in Combat.Enemies) Combat.BeginSideTurn(enemy);
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
                    if (!MonsterMoveSemantics.ApplyPartyMove(simulator, Combat, move, targets, processedDeaths)) return EnemyPhaseFail("party-move");
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

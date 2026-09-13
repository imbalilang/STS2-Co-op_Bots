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
            EndTurnPowerSupport.TriggerVeryEarly(Combat, participants);
            TurnStartRelicSupport.TriggerBeforeSideTurnEnd(simulator, Combat, participants);
            if (Combat.HasPendingChoice || !simulator.SimulateEndPlayerTurnAfterOrbPassives(Combat.GetPlayerTurnNumber(player), players,
                    p => OrbLifecycleSupport.TriggerBeforeTurnEnd(simulator, Combat, p))) return Pending(out boundary);
            CorePowerSupport.CompletePlayerEarlySideTurnEndEffects(Combat, participants);
            foreach (var p in players) Combat.CommitHistoryCourseTurn(p);
            Combat.NormalizeAeonglassWithers(simulator);
            Combat.NormalizeCardAfflictions(simulator);
            if (!SettleDeaths()) return Pending(out boundary);
            simulator.CheckWinCondition(Combat.GetPlayerTurnNumber(player));
            if (simulator.IsInProgress)
            {
                foreach (var p in players) CorePowerSupport.FlushPlayerHandAtTurnEnd(simulator, Combat, p);
                if (!PlayerTurnEndLifecycle.RunPhaseTwo(simulator, Combat, participants, etherealByOwner: ethereal)
                    || !SettleDeaths()) return Pending(out boundary);
                simulator.CheckWinCondition(Combat.GetPlayerTurnNumber(player));
            }
            if (simulator.IsInProgress && !RunEnemyPhase(players, historyStart)) return Pending(out boundary);
            if (Combat.HasPendingChoice) return Pending(out boundary);
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
            if (!StartNextPlayerTurn(players)) return Pending(out boundary);
            RoundsAdvanced++;
            EnemyPhaseCompleted = false;
            ready.Clear();
        }
        else
        {
            EnemyPhaseCompleted = true;
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

    private bool Pending(out string boundary)
    {
        boundary = failedBoundary = roundBoundary ?? "round-pending-choice";
        roundBoundary = null;
        return false;
    }
    private bool SettleDeaths() => CorePowerSupport.ApplyEnemyDeathPowers(simulator, Combat, Combat.KnownEnemies, processedDeaths);
    private bool RunEnemyPhase(Player[] players, int historyStart)
    {
        Combat.SetActionChoiceTiming(PlanChoiceTiming.EnemyTurn);
        var acting = Combat.Enemies.ToArray();
        Combat.CurrentSide = CombatSide.Enemy;
        Combat.SnapshotPowerAmountsAtTurnStart(Combat.Enemies);
        if (!TurnStartRelicSupport.TriggerBeforeSideTurnStart(simulator, Combat, Combat.Enemies)
            || TurnStartPowerSupport.TriggerBeforeSideTurnStart(simulator, Combat, Combat.Enemies)) return false;
        foreach (var enemy in Combat.Enemies.ToArray())
        {
            var state = simulator.State.GetCreature(enemy);
            if (state.Block > 0)
            {
                if (Combat.ShouldClearBlock(enemy, out var preventer)) state.DamageBlock(state.Block, ValueProp.Move);
                else PersistentRelicSupport.TriggerAfterPreventingBlockClear(simulator, preventer, enemy);
            }
            if (!CorePowerSupport.TriggerAfterBlockCleared(simulator, Combat, enemy)) return false;
        }
        if (!Combat.TriggerSideTurnStart(simulator, CombatSide.Enemy, Combat.Enemies, Combat.RoundNumber > 1)) return false;
        var poisonStart = simulator.History.Entries.Count;
        if (!CorePowerSupport.TriggerPoison(simulator, Combat, Combat.Enemies.ToArray())) return false;
        TriggeredPowerSupport.CompensateHistorySince(simulator, Combat, poisonStart);
        if (!SettleDeaths()) return false;
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
                    if (forced != "EXPLODE_MOVE") return false;
                    foreach (var target in targets) MonsterMoveSemantics.DamagePlayer(simulator, Combat, enemy, target, damage);
                    simulator.Kill(enemy, force: true);
                    if (!SettleDeaths()) return false;
                }
                else
                {
                    if (!MonsterMoveEffects.Supports(enemy.Monster!, move.Move.Id))
                    {
                        if (move.Move.Intents.Any(i => i is not AttackIntent))
                        {
                            // Fail this round closed as a boundary instead of
                            // throwing: a single unmodeled monster move must not
                            // disable the kernel for the whole fight.
                            roundBoundary = $"enemy-move-unmodeled:{enemy.Monster!.Id.Entry}/{move.Move.Id}";
                            return false;
                        }
                        LastActionHadEnvironmentalRisk = true;
                    }
                    if (!MonsterMoveSemantics.ApplyPartyMove(simulator, Combat, move, targets, processedDeaths)) return false;
                }
                if (enemy.CombatId is uint id && Hp(enemy) > 0) processedDeaths.Remove(id);
            }
            if (Combat.HasPendingChoice) return false;
            if (Over()) return true;
        }
        if (!CorePowerSupport.TriggerEnemySideTurnEndEffects(simulator, Combat, Combat.Enemies.ToArray()) || !SettleDeaths()) return false;
        if (Combat.BattlewornDummyTimedOut) return false;
        poisonStart = simulator.History.Entries.Count;
        if (!CorePowerSupport.TriggerPoison(simulator, Combat, players.Select(p => p.Creature).ToArray())) return false;
        TriggeredPowerSupport.CompensateHistorySince(simulator, Combat, poisonStart);
        foreach (var p in players) { Combat.ClearNoDraw(p.Creature); Combat.RecordRelicRoundDamage(simulator, p, historyStart); }
        if (!SettleDeaths()) return false;
        if (!Over()) Combat.PrepareMonsterMovesForNextRound(simulator, performed);
        // Stop at a complete enemy boundary. Next-turn draw/search is P1-2.
        return !Combat.HasPendingChoice;
    }
}

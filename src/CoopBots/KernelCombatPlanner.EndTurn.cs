using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    private KernelEndTurnRun? endTurnComparison;
    private string? endTurnComparisonStamp;
    private int endTurnComparisonIndex = -1;

    // true means a slice or a successfully spliced repair owns this tick.
    private bool CompareLosingEndTurn(CombatState combat, Player player)
    {
        var stamp = KernelSession.CaptureLivePartyText(combat);
        if (endTurnComparison is null || stamp != endTurnComparisonStamp || endTurnComparisonIndex != tournamentScriptIndex)
        {
            var root = KernelCombatCapture.Capture(combat);
            if (!root.CanFork) return false;
            endTurnComparison = new(root, player, request: tournamentRequest);
            endTurnComparisonStamp = stamp; endTurnComparisonIndex = tournamentScriptIndex;
        }
        if (!endTurnComparison.Advance(TimeSpan.FromMilliseconds(TournamentSliceMs))) return true;
        var comparison = endTurnComparison;
        endTurnComparison = null;
        Log.Info($"CoopBots end-turn comparison: player={player.NetId} attempted={comparison.Attempted} compared={comparison.Compared} "
            + $"gain={comparison.Gain:F1} steps={comparison.Actions?.Count ?? 0} compute={comparison.ComputeMs:F1}ms stop={comparison.StopReason}");
        if (tournamentRequest?.IsCancelled == true || comparison.Actions is not { Count: > 0 } repair) return false;
        // Rebuild only this turn's evidence, preserving the other seats' planned
        // actions. Reuse the repair directly instead of launching another 6s
        // tournament just to rediscover these same cards.
        var replay = KernelCombatCapture.Capture(combat).ForkForReplay();
        var actions = new List<KernelTeamSearch.Action>();
        var alive = new List<ulong[]>(); var enemies = new List<Creature[]>();
        var round = replay.RoundsAdvanced;
        foreach (var action in repair.Concat(tournamentScript!.Skip(tournamentScriptIndex)))
        {
            if (tournamentRequest?.IsStopped == true) return false;
            alive.Add(combat.Players.Where(p => replay.Hp(p.Creature) > 0).Select(p => p.NetId).Order().ToArray());
            enemies.Add(replay.Enemies.ToArray());
            if (!replay.ReplayPlanned(action, round + 2, out var reason, out var resolved))
            {
                ClearTournamentScript($"end-turn repair remainder refused: {reason}");
                return true;
            }
            actions.Add(resolved);
            if (replay.HasTerminal || replay.RoundsAdvanced > round || replay.EnemyPhaseCompleted) break;
        }
        ClearRefinement("end-turn repair");
        CaptureMixedContinuation(KernelCombatCapture.Capture(combat), combat, actions, cursor: 0);
        tournamentScript = actions; tournamentScriptIndex = 0;
        tournamentScriptAliveStates = alive; tournamentScriptEnemyStates = enemies;
        tournamentScriptRecord = TerminalRecord.Capture(replay, combat.Players, replay.HasTerminal, "end-turn-repair");
        if (tournamentScriptRecord is { Verified: true, Victory: true }) victoryCheckedRound = combat.RoundNumber;
        tournamentScriptRound = combat.RoundNumber;
        tournamentReplanNextRound = tournamentScriptRecord is not { Verified: true, Victory: true };
        Log.Info($"CoopBots end-turn repair: player={player.NetId} cards={string.Join(',', repair.Select(a => a.Card?.Id.Entry))} gain={comparison.Gain:F1}");
        return true;
    }
}

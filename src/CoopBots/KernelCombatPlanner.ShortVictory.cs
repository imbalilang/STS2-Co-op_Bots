using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    private KernelShortVictoryRun? shortVictory;
    private string? shortVictoryStamp;
    private bool CompareShortVictory(CombatState combat, IReadOnlyList<Player> actors)
    {
        var stamp = KernelSession.CaptureLivePartyText(combat);
        if (stamp != shortVictoryStamp)
        {
            shortVictoryStamp = stamp; shortVictory = null;
            var root = KernelCombatCapture.Capture(combat);
            if (root.CanFork && KernelShortVictoryRun.WorthChecking(root, actors)) shortVictory = new(root, actors, request: tournamentRequest);
        }
        if (shortVictory is null) return false;
        if (!shortVictory.Advance(TimeSpan.FromMilliseconds(TournamentSliceMs))) return true;
        var result = shortVictory.Result; shortVictory = null;
        if (tournamentRequest is { } request && (request.IsCancelled || result?.IsFor(request) != true)
            || result?.Record is null || FreshEndingDoesNotBeat(result.Record, tournamentScriptRecord)) return false;
        ClearRefinement("verified short victory");
        CaptureMixedContinuation(KernelCombatCapture.Capture(combat), combat, result.Script!, cursor: 0);
        tournamentScript = result.Script; tournamentScriptIndex = 0;
        tournamentScriptRecord = result.Record; tournamentScriptAliveStates = result.ScriptAliveStates;
        tournamentScriptEnemyStates = result.ScriptEnemyStates;
        tournamentScriptRound = combat.RoundNumber; tournamentReplanNextRound = false;
        victoryCheckedRound = combat.RoundNumber;
        Log.Info($"CoopBots short victory: adopted steps={result.Script!.Count} ending={Describe(result.Record)}");
        return true;
    }
}

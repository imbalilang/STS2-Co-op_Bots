using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    private void UpdateManualFocus(CombatState combat, uint? manualFocus)
    {
        if (focus == manualFocus) return;
        if (ReferenceEquals(tournamentScriptCombat, combat))
            ClearTournamentScript($"human focus changed: {focus?.ToString() ?? "none"}->{manualFocus?.ToString() ?? "none"}");
        focus = manualFocus;
        mixedProposal = null; mixedProposalReplay = null; mixedProposalClock = null;
        mixedReadyProposal = null;
        // The no-action memo was priced under a different expressed intent.
        noActionStamp = "";
    }

    private TournamentOptions WithMixedFocusOptions(TournamentOptions options)
        => options with { ManualFocusTargetCombatId = focus };
}

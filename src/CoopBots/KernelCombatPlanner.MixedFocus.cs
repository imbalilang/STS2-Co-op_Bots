using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    internal void NotifyManualFocusChanged(CombatState combat, uint? manualFocus)
        => UpdateManualFocus(combat, manualFocus);

    private void UpdateManualFocus(CombatState combat, uint? manualFocus)
    {
        if (focus == manualFocus) return;
        ClearMixedReplan("explicit human focus changed");
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

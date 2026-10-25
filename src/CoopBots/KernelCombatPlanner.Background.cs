using CoopBots.Kernel;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    private KernelBackgroundValue<KernelPotionContinuationRun>? mixedPotionReplayWork;
    private KernelBackgroundValue<KernelPotionPlanAudit>? mixedPotionAuditWork;
    private KernelBackgroundValue<double>? mixedPotionBenefitWork;
    private KernelBackgroundValue<KernelPotionOpportunity.Choice?>? mixedOpportunityWork;
    private bool mixedOpportunityPending;
    // An asynchronously completed tournament result is a single decision.
    // Its audit/opportunity/rescue stages may span frames without reopening
    // the result or launching the same potion comparison again.
    private TournamentResult? tournamentCompletedResult;
    private bool tournamentCompletedResultPrepared, tournamentCompletedOpportunityReviewed;
    private KernelBackgroundValue<(EmergencyLine? Rescue, EmergencyLine? Repair, double Compute)>? mixedEmergencyWork;

    private bool AdvanceMixedPotionReplay()
    {
        if (tournamentPotionReplay is not null) return true;
        var root = tournamentRoot!; var party = tournamentParty!; var actors = tournamentActors!;
        var baseline = tournamentFirstPass!; var request = tournamentRequest!;
        var budget = Math.Max(50, request.BudgetMs / 8);
        mixedPotionReplayWork ??= new(() => {
            var run = new KernelPotionContinuationRun(root, party, actors, baseline, budget, request: request);
            while (!run.Advance(TimeSpan.FromMilliseconds(4))) KernelBackgroundOperation.Yield();
            return run;
        }, request);
        if (!mixedPotionReplayWork.Poll()) return false;
        tournamentPotionReplay = mixedPotionReplayWork.Value;
        return true;
    }

    private bool AdvanceMixedPotionBenefit(TournamentResult result, out double benefit)
    {
        var root = tournamentRoot!; var party = tournamentParty!; var request = tournamentRequest!;
        mixedPotionBenefitWork ??= new(() => KernelPotionPolicy.ImmediateBenefit(root, party,
            result.FirstAction, result.Script, request), request);
        if (!mixedPotionBenefitWork.Poll()) { benefit = 0; return false; }
        benefit = mixedPotionBenefitWork.Value;
        return true;
    }

    private bool AdvanceMixedPotionAudit(TournamentResult result)
    {
        if (tournamentPotionAudit is not null) return true;
        var root = tournamentRoot!; var party = tournamentParty!; var request = tournamentRequest!;
        mixedPotionAuditWork ??= new(() => {
            var audit = new KernelPotionPlanAudit(root, party, result, request);
            while (!audit.Advance(TimeSpan.FromMilliseconds(4))) KernelBackgroundOperation.Yield();
            return audit;
        }, request);
        if (!mixedPotionAuditWork.Poll()) return false;
        tournamentPotionAudit = mixedPotionAuditWork.Value;
        return true;
    }
}

using System.Diagnostics;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    private sealed record MixedBaselineProposal(CombatState Combat, int Round, string Control,
        TournamentResult Result, CardModel[] PhysicalCards);
    private MixedBaselineProposal? mixedAvailableBaseline, mixedInterruptedBaseline;
    private sealed record MixedSearchOrigin(CombatState Combat, int Round, string Control, CardModel[] Cards);
    private MixedSearchOrigin? mixedSearchOrigin;
    private void RememberMixedSearchRoot(CombatState combat)
        => mixedSearchOrigin = new(combat, combat.RoundNumber, ControlKey(combat),
            combat.Players.Where(p => p.PlayerCombatState is not null).SelectMany(p =>
                p.PlayerCombatState!.Hand.Cards.Concat(p.PlayerCombatState.DrawPile.Cards)
                .Concat(p.PlayerCombatState.DiscardPile.Cards).Concat(p.PlayerCombatState.ExhaustPile.Cards))
                .Distinct(ReferenceEqualityComparer.Instance).Cast<CardModel>().ToArray());


    // Freeze only native card identity here; a later human input withdraws the
    // terminal proof, while these physical action proposals may still be legal.
    private void RememberMixedBaseline(TournamentResult result, CombatState combat)
    {
        mixedAvailableBaseline = result.Script is { Count: > 0 } script
            && result.ScriptAliveStates?.Count == script.Count
            && result.ScriptEnemyStates?.Count == script.Count && result.Record is not null
            ? new(combat, combat.RoundNumber, ControlKey(combat), result,
                combat.Players.Where(p => p.PlayerCombatState is not null).SelectMany(p =>
                    p.PlayerCombatState!.Hand.Cards.Concat(p.PlayerCombatState.DrawPile.Cards)
                        .Concat(p.PlayerCombatState.DiscardPile.Cards).Concat(p.PlayerCombatState.ExhaustPile.Cards))
                    .Distinct(ReferenceEqualityComparer.Instance).Cast<CardModel>().ToArray())
            : null;
    }

    private bool RestoreInterruptedMixedBaseline(CombatState combat, IReadOnlyList<Player> actors,
        bool humansFinished, out TournamentOutcome outcome, out TeamCombatPlanner.Decision? decision)
    {
        outcome = TournamentOutcome.Pending; decision = null;
        if (mixedInterruptedBaseline is not { } saved) return false;
        mixedInterruptedBaseline = null;
        if (!ReferenceEquals(saved.Combat, combat) || saved.Round != combat.RoundNumber
            || saved.Control != ControlKey(combat))
        { Log.Info("CoopBots mixed baseline: retired after combat/round/control change"); return false; }
        // Do not retag the request or promote old-root victory. Adoption here
        // installs historical physical proposals, checked by the normal emitter.
        var historical = saved.Result with { Record = saved.Result.Record! with {
            Victory = false, Kind = EvidenceKind.EstimatedCutoff,
            CutoffReason = "mixed-interrupted-baseline-quality-unverified", IrreversibleLoss = 0 } };
        ClearTournament("completed baseline retained after settled human input");
        // The cancelled worker still owns its old frozen parent until its
        // current native transaction settles. Never Fork that parent here.
        tournamentRoot = rootSession = KernelCombatCapture.Capture(combat);
        AdoptTournamentScript(historical, combat);
        RetireMixedChoiceContracts(combat, "interrupted historical root cannot retain future choice contracts");
        mixedPhysicalCards.Clear(); mixedPhysicalCards.UnionWith(saved.PhysicalCards);
        Log.Info("CoopBots mixed baseline: retained completed physical proposals; old-root quality withdrawn");
        if (TryEmitFromTournamentScript(combat, actors, humansFinished, out decision))
        { outcome = TournamentOutcome.Decided; return true; }
        return HasTournamentScript;
    }

    private KernelPlanContinuation? mixedContinuation;
    private long mixedInputRevision;
    private string mixedControl = "";
    private bool mixedCapturedLivingHuman;
    private string mixedLivingDomain = "";
    private IReadOnlyList<KernelTeamSearch.Action>? mixedHistoricalScript;
    private int mixedCheckedCursor = -1;
    private TournamentResult? mixedProposal;
    private KernelScriptReplayRun? mixedProposalReplay;
    private Stopwatch? mixedProposalClock;

    private static ulong[] ControlledSeats(CombatState combat)
        => combat.Players.Where(p => AutoPilot.Drives(p.NetId)).Select(p => p.NetId).Order().ToArray();
    private static string ControlKey(CombatState combat) => string.Join(',', ControlledSeats(combat));
    private static bool MixedTable(CombatState combat)
        => combat.Players.Any(p => p.Creature.IsAlive && !AutoPilot.Drives(p.NetId));

    // A line retained through the last human's death/delegation remains a
    // historical action proposal until an actual-root refinement replaces it.
    // Newly searched all-Bot scripts never enter this execution mode.
    private bool UsesMixedExecution(CombatState combat)
        => MixedTable(combat) || ReferenceEquals(tournamentScriptCombat, combat)
            && tournamentScript is not null && ReferenceEquals(mixedHistoricalScript, tournamentScript);

    private void CaptureMixedContinuation(KernelSession? root, CombatState combat,
        IReadOnlyList<KernelTeamSearch.Action> script, int cursor = 1)
    {
        RememberMixedPhysicalCards(combat);
        mixedContinuation = MixedTable(combat) && root?.CanFork == true
            ? new KernelPlanContinuation(root, script, ControlledSeats(combat)) : null;
        mixedInputRevision = KernelInputObserver.Revision;
        mixedControl = ControlKey(combat);
        mixedCapturedLivingHuman = MixedTable(combat);
        mixedLivingDomain = string.Join(',', LivingSeats(combat));
        mixedHistoricalScript = MixedTable(combat) ? script : null;
        mixedCheckedCursor = -1;
        if (mixedContinuation is not null)
            Log.Info($"CoopBots mixed plan: captured cursor={cursor} inputRevision={mixedInputRevision} stableContinuation=true");
    }

    // Human input retires the ending's quality proof, not every legal card
    // proposal. Full-state replay belongs to the optional background comparison;
    // it is never an execution lease and does not block the next foreground card.
    private bool ReviewMixedInput(CombatState combat)
    {
        if (tournamentScript is null) return false;
        var control = ControlKey(combat);
        var mixedNow = MixedTable(combat);
        // Pure all-Bot lines belong to the existing strict/refinement path.
        // A missing mixed cache is not a human ownership transition.
        if (!mixedNow && !mixedCapturedLivingHuman && mixedContinuation is null
            && !ReferenceEquals(mixedHistoricalScript, tournamentScript)) return false;
        var modeChanged = mixedCapturedLivingHuman != mixedNow;
        var livingDomain = string.Join(',', LivingSeats(combat));
        var livingChanged = (mixedCapturedLivingHuman || mixedNow) && mixedLivingDomain != livingDomain;
        if (!mixedNow && mixedContinuation is null && mixedControl == control && !modeChanged && !livingChanged) return false;
        var controlChanged = mixedControl != control;
        var changed = mixedInputRevision != KernelInputObserver.Revision;
        var domainChanged = controlChanged || modeChanged || livingChanged;
        if (!changed && !domainChanged) return false;
        if (changed && tournamentScript.Skip(tournamentScriptIndex).Any(step => step.TurnStartChoices is not null))
            RetireMixedChoiceContracts(combat, "settled material input retired historical choice dependencies");
        if (domainChanged)
        {
            ClearMixedReplan("control changed");
            ClearRefinement("control changed");
            mixedProposal = null; mixedProposalReplay = null; mixedProposalClock = null; mixedReadyProposal = null;
            // Remove only actions whose living seat is no longer controlled.
            // Other seats retain their same physical-card proposals.
            var retained = Enumerable.Range(tournamentScriptIndex,
                Math.Max(0, tournamentScript.Count - tournamentScriptIndex))
                .Where(i => AutoPilot.Drives(tournamentScript[i].Player.NetId)
                    && tournamentScript[i].Player.Creature.IsAlive).ToArray();
            var old = tournamentScript;
            var retired = old.Count - tournamentScriptIndex - retained.Length;
            if (retired > 0)
            {
                tournamentScript = retained.Select(i => old[i]).ToArray();
                tournamentScriptAliveStates = tournamentScriptAliveStates is { } alive
                    ? retained.Select(i => i < alive.Count ? alive[i] : Array.Empty<ulong>()).ToArray() : null;
                tournamentScriptEnemyStates = tournamentScriptEnemyStates is { } enemies
                    ? retained.Select(i => i < enemies.Count ? enemies[i] : Array.Empty<MegaCrit.Sts2.Core.Entities.Creatures.Creature>()).ToArray() : null;
                tournamentScriptIndex = 0;
            }
            mixedControl = control;
            Log.Info($"CoopBots mixed plan: control renewed retainedSteps={retained.Length} retiredSteps={retired}");
        }
        if (tournamentScriptRecord is { } previous)
            tournamentScriptRecord = previous with { Victory = false, Kind = EvidenceKind.EstimatedCutoff,
                CutoffReason = "mixed-historical-root-quality-unverified", IrreversibleLoss = 0 };
        mixedContinuation = null;
        mixedCapturedLivingHuman = mixedNow;
        mixedLivingDomain = livingDomain;
        mixedInputRevision = KernelInputObserver.Revision;
        mixedCheckedCursor = tournamentScriptIndex;
        tournamentReplanNextRound = false;
        victoryReview = null; victoryReviewRoot = null; victoryReviewStamp = null;
        shortVictory = null; shortVictoryStamp = null;
        endTurnComparison = null; endTurnComparisonStamp = null;
        if (domainChanged && !mixedNow && HasTournamentScript)
        {
            // The last human delegated control. The future optimizer must
            // freeze the actual remaining suffix, never replay its old prefix
            // from this new board. Its identity lease is this same new array.
            var actual = KernelCombatCapture.Capture(combat);
            if (actual.CanFork)
            {
                tournamentScript = tournamentScript!.Skip(tournamentScriptIndex).ToArray();
                tournamentScriptAliveStates = tournamentScriptAliveStates?.Skip(tournamentScriptIndex).ToArray();
                tournamentScriptEnemyStates = tournamentScriptEnemyStates?.Skip(tournamentScriptIndex).ToArray();
                tournamentScriptIndex = 0;
                rootSession = actual;
                StartRefinement(actual, combat, tournamentScript);
            }
        }
        mixedHistoricalScript = tournamentScript;
        if (domainChanged) RebindPreparedSubmission();
        NoteMixedInput();
        Log.Info($"CoopBots mixed plan: historical proposals retained cursor={tournamentScriptIndex} "
            + $"inputRevision={mixedInputRevision} controlChanged={controlChanged} modeChanged={modeChanged} livingChanged={livingChanged}");
        return false;
    }

    private bool ReplayMixedProposal()
    {
        if (mixedProposal is null || tournamentPhase != TournamentPhase.NoPotions) return false;
        mixedProposalReplay ??= new(tournamentRoot!, tournamentParty!, mixedProposal, 80, tournamentRequest);
        mixedProposalClock ??= Stopwatch.StartNew();
        mixedProposalReplay.Advance(TimeSpan.FromMilliseconds(TournamentSliceMs));
        if (!mixedProposalReplay.Done && mixedProposalClock.Elapsed.TotalMilliseconds < 120
            && tournamentRequest?.IsStopped != true) return true;
        if (mixedProposalReplay.Result is { FirstAction: { EndTurn: false }, Record: not null } replayed
            && tournamentRequest is { IsCancelled: false } request && replayed.IsFor(request)
            && tournamentActors!.Contains(replayed.FirstAction.Player)
            && replayed.Script!.All(a => AutoPilot.Drives(a.Player.NetId)))
        {
            // The all-Bot refinement fallback also reaches this actual-root
            // replay. Preserve it as a comparator reference rather than a
            // mixed-only direct-adoption shortcut which skips the portfolio.
            if (replayed.Record.Verified || tournamentInteractive)
                tournamentOptions = tournamentOptions! with { ReferenceResult = replayed };
        }
        Log.Info($"CoopBots mixed proposal: replayed={mixedProposalReplay.Result?.Record?.Verified == true} "
            + $"stop={mixedProposalReplay.Failure} wall={mixedProposalClock.Elapsed.TotalMilliseconds:F1}ms "
            + $"generation={tournamentRequest?.RootGeneration}");
        mixedProposal = null; mixedProposalReplay = null; mixedProposalClock = null;
        return false;
    }

    internal void CancelPendingForExternalInput() => NoteMixedInput();
}

using System.Diagnostics;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    // Upstream withdraws its single-player request on input. We retain legal
    // proposals and improve them on settled human roots (owner V2, 2026-10-24),
    // so optional work never becomes a per-card foreground prerequisite.
    // One current-root comparison and one coalesced latest-input flag. Neither
    // owns the foreground cursor or can publish a card to the native executor.
    private sealed class MixedReplanJob
    {
        internal required CombatState Combat;
        internal required string Control;
        internal required long Revision;
        internal required IReadOnlyList<KernelTeamSearch.Action> Source;
        internal required int Cursor;
        internal required KernelSession Root;
        internal required KernelPlanningState Evidence;
        internal required Player[] Party;
        internal required Player[] Actors;
        internal required KernelDecisionRequest Request;
        internal required TournamentOptions Options;
        internal required TournamentResult Proposal;
        internal KernelBackgroundValue<TournamentResult?>? Work;
        internal TournamentResult? Baseline;
        internal TournamentRun? Search;
        internal double ReplayComputeMs;
    }

    private MixedReplanJob? mixedReplan;
    private bool mixedReplanDirty;
    private long mixedLatestDirtyRevision;
    private long mixedNextAdmissionAt;
    private int mixedReplanStarts, mixedReplanCoalesced, mixedReplanAdoptions, mixedReplanRetired;
    private CombatState? mixedBudgetCombat;
    private int mixedBudgetRound;
    private string mixedBudgetControl = "";
    private double mixedEpisodeSpentMs;
    private readonly HashSet<long> mixedAccountedRequests = new();

    // Called only after a native human action has settled. No capture, search,
    // cursor movement or script destruction occurs in the input callback.
    internal void NoteMixedInput()
    {
        if (mixedReplanDirty) mixedReplanCoalesced++;
        mixedReplanDirty = true;
        mixedLatestDirtyRevision = KernelInputObserver.Revision;
        if (tournamentScriptRecord is { } record)
            tournamentScriptRecord = record with { Victory = false, Kind = EvidenceKind.EstimatedCutoff,
                CutoffReason = "mixed-historical-root-quality-unverified", IrreversibleLoss = 0 };
        // An initial search has no executable continuation to preserve. Its
        // old-root workers must still be retired before a settled recapture.
        if (!HasTournamentScript)
        {
            mixedInterruptedBaseline ??= mixedAvailableBaseline;
            if (mixedInterruptedBaseline is null && mixedSearchOrigin is { } origin
                && tournamentRun?.LatestSettledProposal is { } settled)
                mixedInterruptedBaseline = new(origin.Combat, origin.Round, origin.Control, settled, origin.Cards);
            tournamentRequest?.Cancel("settled external player input");
            tournamentRun?.Cancel("settled external player input");
        }
    }

    private void EnsureMixedBudget(CombatState combat)
    {
        var control = ControlKey(combat);
        if (ReferenceEquals(mixedBudgetCombat, combat) && mixedBudgetRound == combat.RoundNumber
            && mixedBudgetControl == control) return;
        ClearMixedReplan("round or control domain changed");
        mixedBudgetCombat = combat; mixedBudgetRound = combat.RoundNumber; mixedBudgetControl = control;
        mixedEpisodeSpentMs = 0; mixedAccountedRequests.Clear();
        mixedNextAdmissionAt = 0;
    }

    // Necessary initialization/repair owns a complete bounded request. Only
    // optional improvement consumes the round ledger; it cannot starve execution.
    private int MixedRemainingDecisionBudget(CombatState combat)
        => checked(2 * TournamentBudget(combat));

    private int MixedRemainingReplanBudget(CombatState combat)
    {
        EnsureMixedBudget(combat);
        return Math.Max(0, (int)Math.Floor(2 * TournamentBudget(combat) - mixedEpisodeSpentMs));
    }

    // Runtime calls this only at a settled native queue boundary. A prepared
    // card does not stop frozen computation, but excludes candidate adoption.
    internal void AdvanceMixedReplan(CombatState combat, long now, bool humansFinished = false)
    {
        if (!MixedTable(combat)) { ClearMixedReplan("all-Bot table"); return; }
        if (humansFinished) { ClearMixedReplan("human finished; executable suffix has priority"); return; }
        EnsureMixedBudget(combat);
        ReviewMixedInput(combat);
        if (mixedReplan is { } stale && (!ReferenceEquals(stale.Combat, combat)
            || stale.Control != ControlKey(combat) || stale.Revision != KernelInputObserver.Revision
            || !ReferenceEquals(stale.Source, tournamentScript) || !HasTournamentScript || stale.Cursor != tournamentScriptIndex))
        {
            // Never retag a result or trim N actions to pretend it has reached
            // today's root. Discard optional work and let the existing line run.
            // Only a newly settled human revision can readmit optional work.
            // Bot cursor progress is not another external input.
            ClearMixedReplan("foreground advanced or newer settled input");
        }
        // Foreground repair/initial work has priority. A prepared submission is
        // already owned by the executor, so it is safe to compute on its root.
        if (tournamentRequest is { IsStopped: false }) return;
        if (mixedReplan is null)
        {
            if (!mixedReplanDirty || now < mixedNextAdmissionAt || !HasTournamentScript) return;
            var remaining = MixedRemainingReplanBudget(combat);
            // Leave optional credit for subsequent input; necessary repair has
            // its own request. The complete search portfolio remains unchanged.
            var reserve = Math.Min(TournamentBudget(combat) / 2, (remaining + 1) / 2);
            var optional = remaining - reserve;
            if (optional <= 0) { mixedReplanDirty = false; return; }
            var captureClock = Stopwatch.StartNew();
            var root = KernelCombatCapture.Capture(combat);
            if (!root.CanFork) return;
            var suffix = tournamentScript!.Skip(tournamentScriptIndex).ToArray();
            if (suffix.Length == 0 || suffix.Any(a => !AutoPilot.Drives(a.Player.NetId))) return;
            var party = combat.Players.ToArray();
            var actors = party.Where(p => p.Creature.IsAlive && AutoPilot.Drives(p.NetId) && root.CanAct(p)).ToArray();
            if (actors.Length == 0) { mixedReplanDirty = false; return; }
            var request = new KernelDecisionRequest(++tournamentRootGeneration, optional, TournamentParallelism);
            var previous = tournamentScriptRecord ?? TerminalRecord.Capture(root, party, false, "mixed-historical-proposal");
            var proposal = new TournamentResult(suffix[0], previous, [], 0, 0, "mixed-historical-proposal") { Script = suffix };
            var options = WithMixedFocusOptions(new TournamentOptions(TournamentDriveTopK,
                Rollout: new RolloutOptions(RolloutPolicyKind.Kill, Seed: combat.RoundNumber),
                // Computing/retaining an ending is independent of permission
                // to submit it. The foreground still waits for human ready.
                IncludePotions: true, IncludeEndTurns: true,
                MaxDegreeOfParallelism: TournamentParallelism, Objective: KernelPotionPolicy.Objective,
                ContinuationActors: party.Where(p => AutoPilot.Drives(p.NetId)).ToArray(),
                EnableJointSearch: true) { Request = request, MixedIncrementalOpenings = true });
            mixedReplan = new MixedReplanJob { Combat = combat, Control = ControlKey(combat),
                Revision = KernelInputObserver.Revision, Source = tournamentScript!, Cursor = tournamentScriptIndex,
                Root = root, Evidence = root.PlanningState(ControlledSeats(combat)), Party = party,
                Actors = actors, Request = request, Options = options,
                Proposal = proposal };
            mixedReplan.ReplayComputeMs += captureClock.Elapsed.TotalMilliseconds;
            mixedReplanDirty = false;
            mixedNextAdmissionAt = now + 1500;
            mixedReplanStarts++;
            Log.Info($"CoopBots mixed replan: started revision={mixedLatestDirtyRevision} cursor={tournamentScriptIndex} "
                + $"starts={mixedReplanStarts} coalesced={mixedReplanCoalesced} budget={optional}ms remaining={remaining}ms");
        }
        var job = mixedReplan!;
        try
        {
            // Everything after the settled root capture has one background
            // owner, including replay, constructor preparation and nested probes.
            job.Work ??= new(() => ComputeMixedReplan(job), job.Request);
            if (!job.Work.Poll()) return;
            if (job.Work.Value is { } candidate && !HasPreparedSubmission
                && job.Cursor == tournamentScriptIndex)
                TryAdoptMixedCandidate(combat, job, candidate);
            else if (HasPreparedSubmission && job.Work.Value is not null
                && job.Cursor == tournamentScriptIndex) return;
            ClearMixedReplan(job.Cursor == tournamentScriptIndex ? "candidate consumed" : "input consumed; foreground advanced");
        }
        catch (Exception error)
        { Log.Warn($"CoopBots mixed replan: failed {error}"); ClearMixedReplan("exception"); }
    }

    private static TournamentResult? ComputeMixedReplan(MixedReplanJob job)
    {
        var watch = Stopwatch.StartNew();
        var replay = new KernelScriptReplayRun(job.Root, job.Party, job.Proposal, job.Options.Rollout!.MaxRounds, job.Request);
        while (!replay.Done) { replay.Advance(TimeSpan.FromMilliseconds(4)); KernelBackgroundOperation.Yield(); }
        job.ReplayComputeMs += watch.Elapsed.TotalMilliseconds;
        job.Baseline = replay.Result;
        if (job.Request.IsStopped || job.Baseline?.Record is { Verified: true, Victory: true }) return null;
        job.Search = new TournamentRun(job.Root, job.Party, job.Actors,
            job.Options with { ReferenceResult = job.Baseline }, job.Request.RemainingMs);
        while (!job.Search.Advance(TimeSpan.FromMilliseconds(4))) KernelBackgroundOperation.Yield();
        var result = job.Search.Result;
        if (job.Request.IsCancelled || result?.FirstAction is null || result.Record is null
            || FreshEndingDoesNotBeat(result.Record, job.Baseline?.Record)) return null;
        watch.Restart();
        var candidate = new KernelScriptReplayRun(job.Root, job.Party, result, job.Options.Rollout.MaxRounds, job.Request);
        while (!candidate.Done) { candidate.Advance(TimeSpan.FromMilliseconds(4)); KernelBackgroundOperation.Yield(); }
        job.ReplayComputeMs += watch.Elapsed.TotalMilliseconds;
        return candidate.Result;
    }

    private bool TryAdoptMixedCandidate(CombatState combat, MixedReplanJob job, TournamentResult result)
    {
        if (HasPreparedSubmission || job.Request.IsCancelled || !result.IsFor(job.Request)
            || !ReferenceEquals(job.Combat, combat) || job.Control != ControlKey(combat)
            || job.Revision != KernelInputObserver.Revision || !ReferenceEquals(job.Source, tournamentScript)
            || job.Cursor != tournamentScriptIndex || result.Script is not { Count: > 0 } script
            || result.ScriptAliveStates?.Count != script.Count || result.ScriptEnemyStates?.Count != script.Count
            || script.Any(a => !AutoPilot.Drives(a.Player.NetId))
            || FreshEndingDoesNotBeat(result.Record, job.Baseline?.Record)) return false;
        var actual = KernelCombatCapture.Capture(combat);
        if (!actual.CanFork || !job.Evidence.Matches(actual.PlanningState(ControlledSeats(combat)))) return false;
        tournamentScript = script; tournamentScriptRecord = result.Record;
        tournamentScriptAliveStates = result.ScriptAliveStates; tournamentScriptEnemyStates = result.ScriptEnemyStates;
        tournamentScriptIndex = 0; tournamentScriptCombat = combat; tournamentScriptRound = combat.RoundNumber;
        tournamentReplanNextRound = false; victoryCheckedRound = combat.RoundNumber;
        rootSession = actual;
        CaptureMixedContinuation(actual, combat, script, 0);
        endTurnComparison = null; endTurnComparisonStamp = null;
        mixedReplanAdoptions++;
        Log.Info($"CoopBots mixed replan: adopted revision={job.Revision} steps={script.Count} "
            + $"adoptions={mixedReplanAdoptions} old={Describe(job.Baseline?.Record)} new={Describe(result.Record)}");
        return true;
    }

    private void ClearMixedReplan(string reason, bool newCombat = false)
    {
        if (mixedReplan is { } job)
        {
            // Conservative optional wall credit includes queue/cadence waiting.
            // Never sample mutable worker counters while cancelling; necessary
            // initialization/repair has its independent bounded allowance.
            if (mixedAccountedRequests.Add(job.Request.RootGeneration))
                mixedEpisodeSpentMs += Math.Min(job.Request.BudgetMs, job.Request.ElapsedMs);
            job.Request.Cancel(reason);
            mixedReplanRetired++;
            Log.Info($"CoopBots mixed replan: retired reason={reason} revision={job.Revision} "
                + $"wall={job.Request.ElapsedMs:F1}ms spent={mixedEpisodeSpentMs:F1}ms retired={mixedReplanRetired}");
            mixedReplan = null;
        }
        if (!newCombat) return;
        mixedReplanDirty = false; mixedLatestDirtyRevision = 0; mixedNextAdmissionAt = 0;
        mixedBudgetCombat = null; mixedBudgetRound = 0; mixedBudgetControl = "";
        mixedEpisodeSpentMs = 0; mixedAccountedRequests.Clear();
        mixedReplanStarts = mixedReplanCoalesced = mixedReplanAdoptions = mixedReplanRetired = 0;
    }
}

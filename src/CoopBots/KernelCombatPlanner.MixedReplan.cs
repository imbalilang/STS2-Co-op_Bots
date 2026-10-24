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
        internal required KernelScriptReplayRun BaselineReplay;
        internal TournamentResult? Baseline;
        internal TournamentRun? Search;
        internal KernelScriptReplayRun? CandidateReplay;
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

    // Foreground and optional background requests consume one round/control
    // allowance. Inputs/restarts do not grant another full tournament budget.
    private int MixedRemainingDecisionBudget(CombatState combat)
    {
        EnsureMixedBudget(combat);
        return Math.Max(0, (int)Math.Floor(2 * TournamentBudget(combat) - mixedEpisodeSpentMs));
    }

    private void AccountMixedForegroundRequest(KernelDecisionRequest request)
    {
        if (mixedBudgetCombat is null || !MixedTable(mixedBudgetCombat)
            || !mixedAccountedRequests.Add(request.RootGeneration)) return;
        // The round ledger measures actual work. Request deadlines separately
        // bound wall time; cadence, animation and waiting for human input are
        // not new computation and must not erase all later improvement time.
        var unobservedRun = tournamentPhase == TournamentPhase.NoPotions ? tournamentScopes == 0
            : tournamentPhase == TournamentPhase.Potions && tournamentScopes < 2;
        var actual = tournamentComputeMs + (unobservedRun ? tournamentRun?.ComputeMs ?? 0 : 0);
        // ComputeMs includes overlapping rollout worker intervals. Capping at
        // request wall time avoids charging that parallel interval repeatedly;
        // taking the smaller value also excludes pure between-frame waiting.
        mixedEpisodeSpentMs += Math.Max(0, Math.Min(actual, request.ElapsedMs));
    }

    // Runtime calls this only at a settled native queue boundary. A prepared
    // card does not stop frozen computation, but excludes candidate adoption.
    internal void AdvanceMixedReplan(CombatState combat, long now)
    {
        if (!MixedTable(combat)) { ClearMixedReplan("all-Bot table"); return; }
        EnsureMixedBudget(combat);
        ReviewMixedInput(combat);
        if (mixedReplan is { } stale && (!ReferenceEquals(stale.Combat, combat)
            || stale.Control != ControlKey(combat) || stale.Revision != KernelInputObserver.Revision
            || !ReferenceEquals(stale.Source, tournamentScript) || stale.Cursor != tournamentScriptIndex))
        {
            // Never retag a result or trim N actions to pretend it has reached
            // today's root. Discard optional work and let the existing line run.
            mixedReplanDirty |= ReferenceEquals(stale.Combat, combat) && MixedTable(combat);
            ClearMixedReplan("foreground advanced or newer settled input");
        }
        // Foreground repair/initial work has priority. A prepared submission is
        // already owned by the executor, so it is safe to compute on its root.
        if (tournamentRequest is { IsStopped: false }) return;
        if (mixedReplan is null)
        {
            if (!mixedReplanDirty || now < mixedNextAdmissionAt || !HasTournamentScript) return;
            var remaining = MixedRemainingDecisionBudget(combat);
            // Reserve necessary repair work without starving improvement after
            // an initial full no-potion pass. This is a scheduling allowance,
            // shared across input restarts, never a smaller search portfolio.
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
                IncludePotions: true, IncludeEndTurns: TournamentCanEndTurn(combat,
                    party.Where(p => p.Creature.IsAlive && !AutoPilot.Drives(p.NetId)).All(p => !root.CanAct(p))),
                MaxDegreeOfParallelism: TournamentParallelism, Objective: KernelPotionPolicy.Objective,
                ContinuationActors: party.Where(p => AutoPilot.Drives(p.NetId)).ToArray(),
                EnableJointSearch: true) { Request = request, MixedIncrementalOpenings = true });
            mixedReplan = new MixedReplanJob { Combat = combat, Control = ControlKey(combat),
                Revision = KernelInputObserver.Revision, Source = tournamentScript!, Cursor = tournamentScriptIndex,
                Root = root, Evidence = root.PlanningState(ControlledSeats(combat)), Party = party,
                Actors = actors, Request = request, Options = options,
                BaselineReplay = new(root, party, proposal, options.Rollout!.MaxRounds, request) };
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
            if (!job.BaselineReplay.Done)
            {
                var replayClock = Stopwatch.StartNew();
                job.BaselineReplay.Advance(TimeSpan.FromMilliseconds(2));
                job.ReplayComputeMs += replayClock.Elapsed.TotalMilliseconds;
                if (!job.BaselineReplay.Done) return;
                job.Baseline = job.BaselineReplay.Result;
                // A fully replayed current-root victory already satisfies the
                // objective. Merely receiving input need not launch a search.
                if (job.Baseline?.Record is { Verified: true, Victory: true })
                { ClearMixedReplan("current-root baseline remains a victory"); return; }
            }
            if (job.CandidateReplay is not null)
            {
                var replayClock = Stopwatch.StartNew();
                job.CandidateReplay.Advance(TimeSpan.FromMilliseconds(2));
                job.ReplayComputeMs += replayClock.Elapsed.TotalMilliseconds;
                if (!job.CandidateReplay.Done) return;
                if (job.CandidateReplay.Result is { } candidate && !HasPreparedSubmission)
                    TryAdoptMixedCandidate(combat, job, candidate);
                // A ready candidate can wait behind a prepared card, but must
                // be discarded as soon as that card advances the source root.
                if (HasPreparedSubmission && job.CandidateReplay.Result is not null) return;
                ClearMixedReplan("candidate consumed"); return;
            }
            if (job.Request.IsStopped)
            { ClearMixedReplan(job.Request.StopReason); return; }
            job.Search ??= new TournamentRun(job.Root, job.Party, job.Actors,
                job.Options with { ReferenceResult = job.Baseline }, job.Request.RemainingMs);
            if (!job.Search.Advance(TimeSpan.FromMilliseconds(2))) return;
            var result = job.Search.Result;
            if (result?.FirstAction is null || result.Record is null
                || FreshEndingDoesNotBeat(result.Record, job.Baseline?.Record))
            { ClearMixedReplan("current-root baseline kept"); return; }
            job.CandidateReplay = new(job.Root, job.Party, result, job.Options.Rollout!.MaxRounds, job.Request);
        }
        catch (Exception error)
        { Log.Warn($"CoopBots mixed replan: failed {error}"); ClearMixedReplan("exception"); }
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
            if (mixedAccountedRequests.Add(job.Request.RootGeneration))
                mixedEpisodeSpentMs += Math.Min(job.Request.ElapsedMs,
                    job.ReplayComputeMs + (job.Search?.ComputeMs ?? 0));
            job.Request.Cancel(reason); job.Search?.Cancel(reason);
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

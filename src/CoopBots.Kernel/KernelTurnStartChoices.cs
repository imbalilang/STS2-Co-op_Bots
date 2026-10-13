using System.Collections.ObjectModel;
using CoopBots.Kernel.Vendor;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

/// <summary>A settled native side transition, including the legal absence of menus.</summary>
public sealed record KernelTurnStartChoiceContract(int FromRound, int ToRound,
    IReadOnlyDictionary<ulong, int> OwnersTurnNumbers, bool Complete,
    IReadOnlyList<KernelChoice> Choices);

public sealed partial class KernelSession
{
    public KernelTurnStartChoiceContract? LastTurnStartChoices { get; private set; }
    private List<KernelChoice>? turnStartJournal;
    private Dictionary<ulong, int>? turnStartOwners;
    private KernelTurnStartChoiceContract? turnStartReplay;
    private KernelTurnStartChoiceContract? pendingImplicitTurnStartReplay;
    private bool plannedImplicitAction;
    private int turnStartReplayIndex;
    private string? turnStartReplayFailure;
    private Player? turnStartChoiceOwner;
    private bool IsTurnStartReplay => turnStartReplay is not null;

    // Upstream drives an automatic cursor during prediction but does not retain
    // our multiplayer native task/barrier answers on the deployed action. We
    // record only the original branch's adopted answers, and replay that exact
    // menu contract. Owner 2026-10-12: real four-seat Tools chose Defend while a
    // deadline-zero replay chose BladeDance; rescore is not deterministic replay.
    public bool EndTurn(Player player, int maxRounds, out string boundary,
        out KernelTeamSearch.Action resolvedAction)
    {
        if (plannedImplicitAction && pendingImplicitTurnStartReplay is null)
        {
            boundary = failedBoundary = "turn-start-contract:implicit-not-recorded";
            resolvedAction = new(player, null, null, EndTurn: true);
            return false;
        }
        var success = RunTurnStartTransition(player, maxRounds, pendingImplicitTurnStartReplay, out boundary);
        resolvedAction = new(player, null, null, EndTurn: true) { TurnStartChoices = LastTurnStartChoices };
        return success;
    }

    private bool ReplayEndTurn(KernelTeamSearch.Action action, int maxRounds,
        out string boundary, out KernelTeamSearch.Action resolvedAction)
    {
        resolvedAction = action;
        if (action.TurnStartChoices is not { Complete: true } contract)
        { boundary = "turn-start-contract:not-recorded"; return false; }
        var success = RunTurnStartTransition(action.Player, maxRounds, contract, out boundary);
        resolvedAction = action with { TurnStartChoices = LastTurnStartChoices };
        return success;
    }

    private bool RunTurnStartTransition(Player? player, int maxRounds,
        KernelTurnStartChoiceContract? expected, out string boundary, bool continueHorizon = false)
    {
        LastTurnStartChoices = null;
        var from = Combat.RoundNumber;
        if (expected is not null && (!expected.Complete || expected.FromRound != from))
        { boundary = "turn-start-contract:from-round"; return false; }
        // Exact replay ends where the recorded transition ended. A short beam
        // may settle the enemy side without forecasting the next player setup;
        // a caller's larger horizon must not manufacture unrecorded menus.
        // Extra turns keep the same native round but DO advance owner turns.
        if (expected is { OwnersTurnNumbers.Count: 0 } && expected.ToRound == from)
            maxRounds = Math.Min(maxRounds, RoundsAdvanced + 1);
        var journal = new List<KernelChoice>();
        var owners = new Dictionary<ulong, int>();
        turnStartJournal = journal; turnStartOwners = owners; turnStartReplay = expected;
        turnStartReplayIndex = 0; turnStartReplayFailure = null;
        var success = false;
        try
        {
            success = continueHorizon ? CompleteReadySideBoundary(maxRounds, out boundary)
                : player is null ? TryAdvanceReadySideCore(maxRounds, out boundary)
                : EndTurnCore(player, maxRounds, out boundary);
            if (success && expected is not null)
            {
                var sameOwners = expected.OwnersTurnNumbers.Count == owners.Count
                    && expected.OwnersTurnNumbers.All(p => owners.TryGetValue(p.Key, out var turn) && turn == p.Value);
                if (expected.ToRound != Combat.RoundNumber || !sameOwners
                    || turnStartReplayIndex != expected.Choices.Count)
                {
                    success = false;
                    boundary = failedBoundary = "turn-start-contract:unconsumed-or-transition";
                }
            }
            if (turnStartReplayFailure is not null)
            { success = false; boundary = failedBoundary = "turn-start-contract:" + turnStartReplayFailure; }
            return success;
        }
        catch (InvalidPlannedChoiceBranchException error)
        {
            boundary = failedBoundary = "turn-start-contract:" + (turnStartReplayFailure ?? error.Message);
            return false;
        }
        finally
        {
            LastTurnStartChoices = new(from, Combat.RoundNumber,
                new ReadOnlyDictionary<ulong, int>(owners),
                success && !Combat.HasPendingChoice && failedBoundary is null,
                Array.AsReadOnly(journal.ToArray()));
            turnStartJournal = null; turnStartOwners = null; turnStartReplay = null;
            turnStartReplayFailure = null; turnStartChoiceOwner = null;
        }
    }

    // Forecasting beyond a verified short prefix is a NEW prediction. The
    // enemy phase is already settled, so execute only the native player setup
    // and attach its adopted choices to the prefix's final causal action.
    internal bool ContinueRecordedHorizon(int maxRounds, out string boundary,
        out KernelTurnStartChoiceContract? continuation)
    {
        continuation = null;
        if (!EnemyPhaseCompleted || !HorizonClosed || HasTerminal || RoundsAdvanced + 1 >= maxRounds)
        { boundary = "horizon-not-continuable"; return false; }
        using var isolation = SimulationNotificationIsolation.Enter();
        var success = RunTurnStartTransition(null, maxRounds, null, out boundary, continueHorizon: true);
        continuation = LastTurnStartChoices;
        return success;
    }

    private PlanCardChoice? ResolveRecordedTurnStartChoice(TurnStartChoiceRequest request)
    {
        if (request.Spec is not { } spec)
            return IsTurnStartReplay ? TurnStartContractMismatch("menu-spec-missing") : null;
        var owner = spec.Options.FirstOrDefault()?.Preview.Owner ?? turnStartChoiceOwner;
        if (owner is null) return TurnStartContractMismatch("owner-unavailable");
        PlanCardChoice? selected;
        if (turnStartReplay is { } contract)
        {
            if (turnStartReplayIndex >= contract.Choices.Count) return TurnStartContractMismatch("unexpected-menu");
            var choice = contract.Choices[turnStartReplayIndex];
            if (choice.Options is null || choice.Timing != request.Timing.ToString()
                || choice.NativeRound != Combat.RoundNumber || choice.PlayerTurnNumber != Combat.GetPlayerTurnNumber(owner)
                || choice.Owner != owner.NetId || choice.Source != request.SourceId
                || choice.Context != request.ContextId || choice.Effect != request.Effect.ToString()
                || choice.Pile != request.SourcePile || choice.MenuSource != request.MenuSource
                || choice.RequestedMinimum != spec.MinCount || choice.RequestedMaximum != spec.MaxCount
                || choice.ResolveMenu(owner.NetId, spec.Options.Select(c => c.Preview).ToArray(),
                    spec.MinCount, spec.MaxCount, request.MenuSource) is null)
                return TurnStartContractMismatch("menu-identity:" + request.SourceId);
            selected = new(request.Effect, request.SourcePile, choice.Cards.Select(c =>
                new PlanCardToken(c.Id, c.Upgrade, c.StateKey, c.Occurrence, c.Occurrence, c.Id)).ToArray(),
                request.SourceId, request.ContextId, request.Timing);
            turnStartReplayIndex++;
        }
        else selected = AutomaticTurnStartChoice(request);
        if (selected is null) return null;
        // These lists belong to this transition only. Fork constructors do not
        // copy the journal; sampled PreparePartyStartChoices probes cannot enter it.
        turnStartJournal?.Add(new(owner.NetId, request.SourceId, request.Effect.ToString(), request.SourcePile,
            request.ContextId, Math.Min(spec.MinCount, spec.Options.Count), Math.Min(spec.MaxCount, spec.Options.Count),
            selected.Cards.Select(c => new KernelChoiceCard(c.CardId, c.UpgradeLevel, c.StateKey, c.OptionOccurrence)).ToArray())
        {
            Options = MenuTokensForSpec(spec), RequestedMinimum = spec.MinCount, RequestedMaximum = spec.MaxCount,
            MenuSource = request.MenuSource, Timing = request.Timing.ToString(), NativeRound = Combat.RoundNumber,
            PlayerTurnNumber = Combat.GetPlayerTurnNumber(owner),
        });
        return selected;
    }

    private PlanCardChoice? TurnStartContractMismatch(string reason)
    {
        turnStartReplayFailure = reason;
        // Existing ResumePreparedPartySetup retries a paused native menu. A
        // recorded-contract mismatch is not another probe candidate; let the
        // ordinary round failure path unwind it rather than retrying indefinitely.
        throw new InvalidPlannedChoiceBranchException(reason);
    }

    private bool BeginImplicitTurnStartReplay(KernelTeamSearch.Action action, out string boundary)
    {
        boundary = "";
        if (action.TurnStartChoices is { } contract
            && (!contract.Complete || contract.FromRound != Combat.RoundNumber))
        { boundary = "turn-start-contract:implicit-from-round"; return false; }
        pendingImplicitTurnStartReplay = action.TurnStartChoices;
        plannedImplicitAction = true;
        return true;
    }

    // ForecastCausal retains the same native effects and immediate answers.
    // Record only the newly adopted transition; an old future contract cannot
    // describe a counterfactual that kills or rescues a seat at this boundary.
    private bool FinishImplicitForecast(int maxRounds, out string boundary)
    {
        boundary = "";
        if (LastTurnStartChoices is null && !EnemyPhaseCompleted && !HasTerminal)
        {
            var alive = Combat.Players.Where(p => Hp(p.Creature) > 0).ToArray();
            if (alive.Length > 0 && alive.All(IsReady)
                && !TryAdvanceReadySide(maxRounds, out boundary)) return false;
        }
        if (LastTurnStartChoices is not null && !LastTurnStartChoices.Complete)
        { boundary = "turn-start-contract:forecast-incomplete"; return false; }
        return true;
    }

    private bool FinishImplicitTurnStartReplay(KernelTeamSearch.Action action, int maxRounds, out string boundary)
    {
        boundary = "";
        if (action.TurnStartChoices is null)
        {
            // A native Kill can ready the final seat without a card requesting
            // EndTurn. Replay must not silently manufacture its next setup.
            var alive = Combat.Players.Where(p => Hp(p.Creature) > 0).ToArray();
            if (LastTurnStartChoices is not null || !EnemyPhaseCompleted && !HasTerminal
                && alive.Length > 0 && alive.All(IsReady))
            { boundary = failedBoundary = "turn-start-contract:implicit-not-recorded"; return false; }
            return true;
        }
        if (LastTurnStartChoices is null && !TryAdvanceReadySide(maxRounds, out boundary))
        {
            boundary = "turn-start-contract:implicit-transition-missing:" + boundary;
            return false;
        }
        if (LastTurnStartChoices is not { Complete: true })
        { boundary = "turn-start-contract:implicit-incomplete"; return false; }
        return true;
    }
}

using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    // Owner-requested Touch-only choice contract, 2026-10-11. The paid native
    // F28 witness selected DASH+ (filtered menu ordinal 4), retained ThisCombat
    // free cost and consumed its bottle. Its later terminal is not verified.
    // Other eight chooser bottles remain boundaries. All effect/lifecycle
    // stages below reuse native mirrors; this adapter owns recipient and tokens.
    public static bool RequiresPotionChoiceContract(PotionModel potion) => potion is TouchOfInsanity;

    public IReadOnlyList<KernelTeamSearch.Action> PotionActions(PotionModel potion, Creature? target,
        int maximumChoices = 10, IReadOnlyList<Player>? controlledActors = null)
    {
        if (!RequiresPotionChoiceContract(potion))
            return [new(potion.Owner, null, target, potion)];
        return PotionBranches(potion, target, maximumChoices, controlledActors)
            .Where(branch => branch.Boundary.Length == 0)
            .Select(branch => new KernelTeamSearch.Action(potion.Owner, null, target, potion,
                Choices: branch.Choices, ChoiceOrdinals: branch.ChoiceOrdinals)).ToArray();
    }

    public IEnumerable<CardBranch> PotionBranches(PotionModel potion, Creature? target,
        int maximumBranches = 10, IReadOnlyList<Player>? controlledActors = null)
    {
        if (maximumBranches <= 0 || KernelSearchBudgetScope.IsStopped) yield break;
        if (potion is not TouchOfInsanity)
        {
            var ordinary = Fork();
            var ok = ordinary.UsePotion(potion, target, out var failure);
            yield return new(ordinary, [], ok ? "" : failure, []);
            yield break;
        }
        // Kernel has no dependency on Core's AutoPilot. The planning caller
        // supplies the controlled choice seats; a human's menu remains native.
        if (target?.Player is not { } recipient || controlledActors?.Contains(recipient) != true) yield break;
        var prefix = Fork();
        if (!prefix.PrepareTouch(potion, target, out var simulated, out var history, out var boundary))
        {
            yield return new(prefix, [], boundary, []);
            yield break;
        }
        // Prepare is the upstream stable potion-choice prefix: consumed bottle,
        // completed BeforePotionUsed, no pending transaction. It is private and
        // never published as an action before Complete and native settlement.
        var options = prefix.TouchOptions(target);
        var menu = KernelChoice.MenuTokens(options.Select(card => card.Preview).ToArray());
        var count = options.Count == 0 ? 1 : Math.Min(maximumBranches, options.Count);
        // Only schedules this bottle's bounded menu; no terminal score changes.
        // The native cost forgone is an existing energy opportunity, unlike
        // menu order. Preserve physical ordinals for replay after ordering.
        var ordinals = options.Count == 0 ? new[] { 0 } : Enumerable.Range(0, options.Count)
            .OrderByDescending(i => options[i].Preview.EnergyCost.GetWithModifiers(CostModifiers.All))
            .ThenByDescending(i => options[i].Preview.GetStarCostWithModifiers()).Take(count).ToArray();
        for (var i = 0; i < count && (i == 0 || !KernelSearchBudgetScope.IsStopped); i++)
        {
            var ordinal = ordinals[i];
            var chosen = new KernelChoice(target!.Player!.NetId, potion.Id.Entry,
                PlanChoiceEffect.SetFreeThisCombat.ToString(), PileType.Hand, "",
                Math.Min(1, options.Count), Math.Min(1, options.Count),
                options.Count == 0 ? [] : [menu[ordinal]])
            {
                Options = menu, RequestedMinimum = 1, RequestedMaximum = 1,
                MenuSource = potion.Id.Entry,
            };
            var completed = prefix.Fork();
            completed.choiceOrdinals = [ordinal];
            var ok = completed.CompleteTouch(potion, simulated!, target, [chosen], history, out boundary);
            yield return new(completed, [chosen], ok ? "" : boundary, [ordinal]);
        }
    }

    private IReadOnlyList<PredictedCard> TouchOptions(Creature? target)
        => simulator.State.GetPlayerCombatState(target!.Player!).Hand.Cards.Where(card =>
            card.Preview.CostsEnergyOrStars(includeGlobalModifiers: false)
            || card.Preview.CostsEnergyOrStars(includeGlobalModifiers: true)).ToArray();

    private bool UseTouch(PotionModel potion, Creature? target, IReadOnlyList<KernelChoice>? choices,
        out string boundary)
    {
        // A missing contract must leave even the bottle/history/RNG untouched.
        // Actual menu validation follows Prepare, because BeforePotionUsed may
        // change that menu. Failure then belongs to this private action fork.
        if (!ValidTouchEnvelope(potion, target, choices) || choiceOrdinals is not { Length: 1 })
        { boundary = "potion-choice-required"; return false; }
        if (!PrepareTouch(potion, target, out var simulated, out var history, out boundary)) return false;
        return CompleteTouch(potion, simulated!, target, choices!, history, out boundary);
    }

    private static bool ValidTouchEnvelope(PotionModel potion, Creature? target,
        IReadOnlyList<KernelChoice>? choices)
        => target?.Player is { } recipient && choices is { Count: 1 }
            && choices[0] is { Options: not null, RequestedMinimum: 1, RequestedMaximum: 1 } choice
            && choice.Owner == recipient.NetId && choice.Source == potion.Id.Entry
            && choice.MenuSource == potion.Id.Entry && choice.Pile == PileType.Hand
            && choice.Effect == PlanChoiceEffect.SetFreeThisCombat.ToString();

    private bool PrepareTouch(PotionModel potion, Creature? target, out PotionModel? simulated,
        out int historyStart, out string boundary)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        simulated = null; historyStart = 0;
        if (failedBoundary is not null) { boundary = failedBoundary; return false; }
        if (!CanAct(potion.Owner)) { boundary = "owner-ended"; return false; }
        if (target?.Player is null || !Combat.Creatures.Contains(target) || Hp(target) <= 0
            || !potion.IsValidTarget(target)) { boundary = "potion-target-invalid"; return false; }
        simulated = FindCapturedPotion(potion);
        var slot = simulated is null ? -1 : SimulatedPotionSlot(simulated);
        if (slot < 0) { boundary = "potion-not-captured"; return false; }
        historyStart = simulator.History.Entries.Count;
        failedBoundary = "incomplete-action";
        try
        {
            var savedOrdinals = choiceOrdinals; choiceOrdinals = null;
            Combat.BeginActionChoices(ChoiceCursor(potion.Owner));
            try
            {
                if (!PotionExecutionSupport.Prepare(simulator, Combat, simulated!, slot, target)
                    || simulator.HasPendingChoice)
                { boundary = failedBoundary = "pending-choice"; return false; }
            }
            finally { Combat.EndActionChoices(); choiceOrdinals = savedOrdinals; }
            // Fork only the settled upstream continuation prefix. A caller
            // cannot obtain this session through a completed plan/action yet.
            failedBoundary = null;
            boundary = "";
            return true;
        }
        catch (Exception error)
        { boundary = failedBoundary = "potion-exception:" + error.GetType().Name; return false; }
    }

    private bool CompleteTouch(PotionModel source, PotionModel simulated, Creature? target,
        IReadOnlyList<KernelChoice> choices, int historyStart, out string boundary)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        var options = TouchOptions(target);
        var models = options.Select(card => card.Preview).ToArray();
        var selected = ValidTouchEnvelope(source, target, choices)
            ? choices[0].ResolveMenu(target!.Player!.NetId, models, 1, 1, source.Id.Entry) : null;
        var ordinal = choiceOrdinals is { Length: 1 } ? choiceOrdinals[0] : -1;
        var ordinalMatches = ordinal >= 0 && (models.Length == 0
            ? ordinal == 0 && selected?.Count == 0
            : ordinal < models.Length && selected is { Count: 1 } && ReferenceEquals(selected[0], models[ordinal]));
        if (selected is null || !ordinalMatches)
        { boundary = failedBoundary = "potion-choice-changed"; return false; }
        failedBoundary = "incomplete-action";
        try
        {
            foreach (var model in selected)
                options.Single(card => ReferenceEquals(card.Preview, model)).SetToFreeThisCombat();
            var savedOrdinals = choiceOrdinals; choiceOrdinals = null;
            Combat.BeginActionChoices(ChoiceCursor(source.Owner));
            try
            {
                if (!PotionExecutionSupport.Complete(simulator, Combat, simulated, target, null,
                    historyStart, processedDeaths))
                { boundary = failedBoundary = "pending-choice"; return false; }
                if (!CombatBeamSolver.SettleReplayActionBoundary(simulator, Combat))
                { boundary = failedBoundary = "pending-boundary"; return false; }
            }
            finally { Combat.EndActionChoices(); choiceOrdinals = savedOrdinals; }
            simulator.CheckWinCondition(Combat.GetPlayerTurnNumber(source.Owner));
        }
        catch (Exception error)
        { boundary = failedBoundary = "potion-exception:" + error.GetType().Name; return false; }
        return FinishPotionEvidence(source, out boundary);
    }
}

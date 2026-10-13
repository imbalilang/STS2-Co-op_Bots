using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Extensions;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Potions.OnUse;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    public sealed record PotionOffer(string CardId, double Benefit);

    // Upstream exposes deterministic generation on a private RNG clone. Use it
    // to price the free choice at a full belt (owner request, 2026-09-28), while
    // retaining the existing live-choice/recapture boundary for committed plans.
    public IReadOnlyList<PotionOffer> PreviewPotionOffers(PotionModel potion, Creature? target,
        IReadOnlyList<Player> actors)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        var recipient = target?.Player ?? potion.Owner;
        if (!CanFork || !actors.Contains(recipient) || !CanAct(recipient) || Hand(recipient).Count >= 10
            || !PotionChoiceSupport.GeneratesCardChoice(potion)) return [];
        var preview = Fork();
        var generated = CardGenerationPotionMirrors.Generate(potion, recipient,
            preview.simulator.Rng.CombatCardGeneration, preview.Combat.CardMultiplayerConstraint, preview.simulator);
        if (generated is null) return [];
        return generated.Cards.Select(c => new PotionOffer(c.Original.Id.Entry,
            preview.GeneratedCardBenefit(c.Original, actors))).ToArray();
    }

    public double GeneratedCardBenefit(CardModel offered, IReadOnlyList<Player> actors)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        if (!CanFork || !actors.Contains(offered.Owner) || !CanAct(offered.Owner) || Hand(offered.Owner).Count >= 10)
            return 0;
        var branch = Fork();
        var card = new PredictedCard(offered);
        card.SetToFreeThisTurn();
        branch.simulator.AddGeneratedCardsToCombat([card], PileType.Hand, offered.Owner);
        if (!branch.CanFork || !branch.Hand(offered.Owner).Contains(offered) || !branch.CanPlay(offered)) return 0;
        var party = Party;
        var baseline = KernelTacticalPolicy.Value(this, party, actors);
        var best = 0d;
        foreach (var target in branch.Targets(offered))
        foreach (var resolved in branch.CardBranches(offered, target, 3).Where(b => b.Boundary.Length == 0))
        {
            var value = Math.Max(KernelTacticalPolicy.ActionValue(this, resolved.State, party, actors),
                RolloutRun.GeneratedFollowupValue(this, resolved.State, party, actors));
            best = Math.Max(best, value - baseline);
        }
        return best;
    }
}

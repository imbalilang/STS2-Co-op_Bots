using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

// Owner policy, 2026-10-07: a starter-heavy deck lacking hand advantage should
// upgrade a payable, real net-draw gain before ordinary numerical gains.
// This is an upgrade-choice priority, not native combat value, and therefore
// does not alter deck potentials, random-upgrade event means or edit inverses.
internal static class UpgradePreference
{
    internal static BuildValue.Valuation Apply(CardModel card, Player player,
        IReadOnlyList<CardModel> deck, BuildValue.Valuation model)
    {
        var energy=BuildValue.StableEnergy(player);
        if(deck.Count==0 || deck.Count(c=>c.Rarity==CardRarity.Basic
                && (c.Tags.Contains(CardTag.Strike)||c.Tags.Contains(CardTag.Defend)))/(double)deck.Count<.6
            || deck.Sum(c=>CardProfile.NetDraw(c,CardProfile.Of(c,energy)))>=2
            || CardProfile.Upgraded(card,energy) is not { } upgraded) return model;
        var before=CardProfile.Of(card,energy);
        var gain=CardProfile.NetDraw(upgraded.Card,upgraded.Facts)-CardProfile.NetDraw(card,before);
        // A filled draw pile is useful only if this card leaves energy for at
        // least one ordinary paid action (or the deck has a free payoff).
        var remainder=energy-upgraded.Facts.Cost+upgraded.Facts.Energy;
        var payable=deck.Any(c=>!ReferenceEquals(c,card) && CardProfile.Of(c,energy) is { Unplayable:false } f
            && (f.Damage>0||f.Block>0) && f.Cost<=remainder);
        if(gain<=0 || !payable || model.Total<=0) return model;
        foreach(var held in BuildValue.EligibleUpgrades(deck))
        {
            var diff=CardProfile.DiffUpgrade(held,energy);
            if(diff is null)continue;
            // Preserve genuine economy/safety priorities. The draw policy
            // cannot displace a useful cost/HP reduction or wounded defence.
            if(!diff.Before.Unplayable && (diff.CostDelta>0 || diff.HpLossDelta<0
                || player.Creature.CurrentHp*2<=player.Creature.MaxHp && diff.BlockDelta>0)) return model;
        }
        return model with { UpgradePriority=1,
            Reason=model.Reason+$",upgrade-policy:starter-net-hand-advantage,net-draw+{gain:F0}" };
    }
}

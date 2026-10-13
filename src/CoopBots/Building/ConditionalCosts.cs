using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CoopBots.Building;

// Native Midnight discounts every player's exhaust, including generated cards.
// Permanent decisions use public decks and a finite exposure forecast, never
// the live combat's temporary discount or a flat rare-card bonus.
internal static class ConditionalCosts
{
    internal static double ExhaustsBefore(IReadOnlyList<CardModel> deck, int energy, int turn)
    {
        if(turn<=1 || deck.Count==0)return 0;
        var exposure=5d*(turn-1)/Math.Max(5,deck.Count);
        var corruption=deck.Any(c=>c is Corruption);
        double total=0;
        foreach(var c in deck)
        {
            var cost=Math.Max(0,c.EnergyCost.Canonical);
            var playable=cost<=energy ? 1d : 0;
            var once=c.Keywords.Contains(CardKeyword.Exhaust) || c.Keywords.Contains(CardKeyword.Ethereal)
                || corruption && c.Type==CardType.Skill;
            if(once)total+=Math.Min(1,exposure)*(c.Keywords.Contains(CardKeyword.Ethereal)?1:playable);
            var plays=(once?Math.Min(1,exposure):exposure)*playable;
            total+=plays*(DraftCardEffects.Of(c).GeneratedShivs
                +(c is TrueGrit or BurningPact or Cinder or Havoc ? 1 : c is FiendFire or SecondWind ? 2 : 0));
            if(c is InfiniteBlades)
                for(var age=1;age<turn-1;age++)total+=Math.Min(1,5d*age/Math.Max(5,deck.Count));
        }
        return total;
    }

    internal static double Load(CardModel card, IReadOnlyList<CardModel> deck, int energy)
    {
        var owner=card.Owner;
        var baseCost=CardProfile.Of(card,energy).Cost;
        var allies=owner?.RunState.Players.Where(p=>p!=owner && p.Creature.IsAlive).ToArray()??[];
        double total=0;
        for(var turn=1;turn<=6;turn++)
        {
            var exhausts=ExhaustsBefore(deck,energy,turn)
                +allies.Sum(p=>ExhaustsBefore(p.Deck.Cards,BuildValue.StableEnergy(p),turn));
            var cost=Math.Max(0,baseCost-(int)exhausts);
            // An unavailable conditional card occupies a draw slot (already
            // simulated); it cannot commit four turns' energy in one action.
            // Price at most a full available turn until its discount unlocks it.
            total+=DeckStrength.CommitmentLoadForCost(Math.Min(energy,cost),energy)/6;
        }
        return total;
    }
}

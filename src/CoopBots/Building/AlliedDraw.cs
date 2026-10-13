using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CoopBots.Building;

// Native GetTeammatesOf includes Plot's caster; NextTurnDraw already carries
// that owner's draw. Only OTHER living teammates supply this additional
// support. Paired finite payable hands use the existing
// next-turn .7 discount. No live shuffle is read and support cannot recurse.
internal static class AlliedDraw
{
    [ThreadStatic] private static bool evaluating;
    internal static double Value(Player? owner,int amount)
    {
        if(evaluating || amount<=0 || owner?.RunState is not { } run)return 0;
        evaluating=true;
        try
        {
            double total=0;
            foreach(var ally in run.Players.Where(p=>p!=owner && p.Creature.IsAlive))
            {
                var energy=BuildValue.StableEnergy(ally);
                var cards=ally.Deck.Cards.Where(c=>c is not Plot and not BelieveInYou)
                    .OrderBy(c=>c.Id.Entry,StringComparer.Ordinal)
                    .Select(c=>new ConstructionHand.Slot(c,CardProfile.Of(c,energy))).ToArray();
                if(cards.Length==0)continue;
                for(var sample=0;sample<4;sample++)
                {
                    var offset=sample*cards.Length/4;
                    var order=cards.Skip(offset).Concat(cards.Take(offset)).ToArray();
                    var before=ConstructionHand.Solve(order,5,new(energy,16,100));
                    var after=ConstructionHand.Solve(order,Math.Min(ConstructionHand.Window,5+amount),new(energy,16,100));
                    total+=.7*Math.Max(0,after.Utility-before.Utility)/4;
                }
            }
            return total;
        }
        finally { evaluating=false; }
    }
}

using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

// Paired bounded hand samples. Draw, mandatory discard, exhaust and Sly share
// ConstructionHand with the opening model; no live shuffle/RNG is inspected.
internal static class PayableHands
{
    internal const double TemporaryStatShare = .7;
    internal sealed record Card(ConstructionCard Model, CardProfile.Facts Facts, double Remaining, double PlayBlock = 0);
    internal sealed record Outcome(double[] Plays, double[] Exhausted, double[] Targets,
        double Spent, double Refunded, double StatusDamage, double StatusExhausted,
        double WitherExhausted, double Generated, int Counter, double Drawn)
    {
        internal double TriggeredPoison { get; init; }
        internal double TriggeredBlock { get; init; }
        internal double TemporaryDamage { get; init; }
        internal double TemporaryBlock { get; init; }
        internal double[] AutomaticPlays { get; init; } = [];
        internal double CopiedAttackDamage { get; init; }
        internal double FirstShivDamage { get; init; }
        internal double StartDiscardCount { get; init; }
    }

    internal static double[] Plays(Card[] cards, double deckSize, double draw, double energy,
        double incoming, double health, int enemies, int turn, double corruption, int trajectory = -1, int drawOffset = 0)
        => Evaluate(cards, deckSize, draw, energy, incoming, health, enemies, turn, corruption, trajectory, drawOffset).Plays;

    internal static Outcome Evaluate(Card[] cards, double deckSize, double draw, double energy,
        double incoming, double health, int enemies, int turn, double corruption, int trajectory = -1, int drawOffset = 0,
        double statuses = 0, double withers = 0, double witherDamage = 0, int counter = 0, bool chains = false,
        double exhaustDraw = 0, double exhaustBlock = 0, int initialWithers = 0,
        int startDiscard = 0, double juggling = 0, double phantomBlades = 0, double skillEnrage = 0)
    {
        var plays = new double[cards.Length]; var automatic = new double[cards.Length];
        var burned = new double[cards.Length]; var targets = new double[cards.Length];
        double spent=0, refunded=0, damage=0, removed=0, witherRemoved=0, generated=0, drawn=0,
            triggeredPoison=0, triggeredBlock=0, temporaryDamage=0, temporaryBlock=0, copiedAttackDamage=0, firstShivDamage=0, startDiscardCount=0;
        var finalCounter=counter;
        var samples=trajectory>=0?1:8;
        for(var sample=0;sample<samples;sample++)
        {
            var key=trajectory>=0?trajectory:sample+19*turn;
            var copies=new Dictionary<string,int>();
            var pool=new List<(int Index,uint Key)>();
            for(var i=0;i<cards.Length;i++)
            {
                var id=cards[i].Model.Id.Entry;
                var copy=copies.GetValueOrDefault(id); copies[id]=copy+1;
                if(Unit(id,copy,key+331)<cards[i].Remaining)pool.Add((i,Hash(id,copy,key)));
            }
            var occupied=Math.Max(0,deckSize-cards.Sum(c=>c.Remaining));
            int SampleCount(double n,string id) => (int)n+(Unit(id,0,key)<n%1?1:0);
            var slots=SampleCount(occupied,"occupied");
            var bad=Math.Min(slots,SampleCount(statuses,"statuses"));
            var harmful=Math.Min(bad,SampleCount(withers,"withers"));
            for(var j=0;j<slots;j++)pool.Add((j<harmful?-3:j<bad?-2:-1,Hash("unavailable-slot",j,key)));
            var order=pool.OrderByDescending(x=>turn==1 && x.Index>=0 && cards[x.Index].Model.Keywords.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardKeyword.Innate)).ThenBy(x=>x.Key).ToArray();
            var offset=trajectory<0 || order.Length==0?0:drawOffset%order.Length;
            var indices=order.Skip(offset).Concat(order.Take(offset)).Take(ConstructionHand.Window).Select(x=>x.Index).ToArray();
            var hand=indices.Select(i=>i<0 ? new ConstructionHand.Slot(null,null,i<=-2,i==-3?witherDamage:0)
                : new ConstructionHand.Slot(cards[i].Model,cards[i].Facts,cards[i].Facts.Unplayable,PlayBlock:cards[i].PlayBlock)).ToArray();
            var selection=ConstructionHand.Solve(hand,SampleCount(Math.Max(0,draw),"draw"),
                new(energy,incoming,health,enemies,chains,witherDamage,counter,exhaustDraw,exhaustBlock,
                    Unit("corruption",0,key)<corruption,initialWithers,deckSize)
                { StartDiscard=startDiscard, Juggling=juggling, PhantomBlades=phantomBlades, SkillEnrage=skillEnrage });
            for(var j=0;j<indices.Length;j++)
            {
                var index=indices[j]; if(index<0)continue;
                if((selection.Played&(1<<j))!=0)plays[index]+=Math.Max(1,(int)((selection.Executions>>(j*4))&15))
                    / (double)(samples*(1+Math.Max(0,cards[index].Model.GetEnchantedReplayCount())));
                if((selection.AutomaticPlayed&(1<<j))!=0)automatic[index]+=1d/samples;
                if((selection.Exhausted&(1<<j))!=0)burned[index]+=1d/samples;
                targets[index]+=selection.Targets[j]/(double)samples;
            }
            spent+=selection.EnergySpent/samples;
            refunded+=(selection.EnergyLeft-energy+selection.EnergySpent)/samples;
            damage+=selection.StatusDamage/samples; removed+=selection.StatusExhausted/(double)samples;
            witherRemoved+=selection.WitherExhausted/(double)samples; generated+=selection.Generated/(double)samples;
            drawn+=selection.Drawn/(double)samples; finalCounter=selection.Counter;
            triggeredPoison += selection.TriggeredPoison / samples;
            triggeredBlock += selection.TriggeredBlock / samples;
            temporaryDamage += selection.TemporaryDamage / samples;
            temporaryBlock += selection.TemporaryBlock / samples;
            copiedAttackDamage += selection.CopiedAttackDamage / samples;
            firstShivDamage += selection.FirstShivDamage / samples;
            startDiscardCount += selection.StartDiscardCount/(double)samples;
        }
        return new(plays,burned,targets,spent,refunded,damage,removed,witherRemoved,generated,finalCounter,drawn)
            { TriggeredPoison=triggeredPoison, TriggeredBlock=triggeredBlock,
                TemporaryDamage=temporaryDamage, TemporaryBlock=temporaryBlock, AutomaticPlays=automatic,
                CopiedAttackDamage=copiedAttackDamage, FirstShivDamage=firstShivDamage,StartDiscardCount=startDiscardCount };
    }
    private static double Unit(string id,int copy,int sample)=>Hash(id,copy,sample)/(double)uint.MaxValue;
    internal static bool Reached(ConstructionCard card,double probability,int trajectory)
        =>Unit(card.Id.Entry,0,trajectory+771)<probability;
    private static uint Hash(string id,int copy,int sample)
    {
        uint x=2166136261;
        foreach(var c in id)x=unchecked((x^c)*16777619);
        x^=unchecked((uint)copy*0x9E3779B9u^(uint)sample*0x85EBCA6Bu);
        x^=x>>16;x*=0x7FEB352Du;x^=x>>15;x*=0x846CA68Bu;
        return x^(x>>16);
    }
}

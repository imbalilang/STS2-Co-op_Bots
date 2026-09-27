using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CoopBots.Building;

// Paired six-turn expectation, deliberately NOT a native combat simulation or
// win probability. Adds engine startup, finite energy and status pressure to
// construction. Both alternatives see the same ordinary/long-fight scenarios;
// no shuffled live order or live RNG is inspected. Unsupported powers retain
// the existing structural policy instead of silently becoming blank cards.
internal static class SustainedDeckValue
{
    private sealed record Entry(CardModel Card, CardProfile.Facts Facts);
    private static bool KnownPower(CardModel c) => c is Afterimage or Footwork or InfiniteBlades
        or MachineLearning or Inflame or DemonForm or FeelNoPain or DarkEmbrace
        or Accelerant or Accuracy or Cruelty or Unmovable;
    private static bool Cleanup(CardModel c) => c is SecondWind or TrueGrit or BurningPact
        or Cinder or FiendFire or Prepared or CalculatedGamble or Survivor or Purity;
    private static double Amount(CardModel c, string key, double fallback=0)
        => c.DynamicVars.TryGetValue(key,out var v) ? (double)v.BaseValue : fallback;

    // Native Unmovable doubles all block events from the first eligible card
    // play per stack, excluding later card plays and non-move power block.
    // Expected availability is bounded; a Second Wind is ONE card play even
    // though its exhausts can produce multiple native block events.
    internal static double FirstBlockBonus(IEnumerable<double> cards,double probability,double slots)
    {
        double bonus=0;
        foreach(var block in cards.OrderByDescending(x=>x))
        {
            var used=Math.Min(Math.Clamp(probability,0,1),Math.Max(0,slots));
            bonus+=used*block;slots-=used;
        }
        return bonus;
    }

    internal static CounterfactualDraft.Result? Compare(IReadOnlyList<CardModel> deck,
        CardModel? added, CardModel? removed, int energy, bool includePollution = true)
    {
        if(deck.Count<8 || !deck.Append(added).Any(c=>c is not null && (KnownPower(c)||Cleanup(c)))) return null;
        if(deck.Append(added).Any(c=>c is not null && c.Type==CardType.Power && !KnownPower(c))) return null;
        // Missing held mechanisms can reverse removal decisions. Attack replay,
        // temporary attributes and threshold attacks stay on the structural
        // model until this expectation represents their prerequisites too.
        if(deck.Append(added).Where(c=>c is not null).Any(c=>
        {
            var effects=DraftCardEffects.Of(c!);
            return effects.ReplayAttacks>0 || effects.TemporaryStrength>0 || effects.TemporaryDexterity>0
                || effects.ConditionalDamage>0 || effects.FreeExhaustSkills;
        }))return null;
        var before=deck.Select(c=>new Entry(c,CardProfile.Of(c,energy))).ToArray();
        var after=deck.Where(c=>!ReferenceEquals(c,removed)).Concat(added is null ? [] : [added])
            .Select(c=>new Entry(c,CardProfile.Of(c,energy))).ToArray();
        var old=new List<double>(); var next=new List<double>();
        foreach(var threat in new[]{12d,26d})
        foreach(var status in includePollution ? new[]{false,true} : new[]{false})
        { old.Add(Score(before,energy,threat,status)); next.Add(Score(after,energy,threat,status)); }
        var meanOld=old.Average();var meanNew=next.Average();
        return new(.7*(meanNew-meanOld)+.3*(next.Min()-old.Min()),meanOld,meanNew,old.Min(),next.Min(),0,0);
    }

    private static double Score(Entry[] deck,int energy,double pressure,bool pollution)
    {
        if(deck.Length==0)return 0;
        var ordinary=deck.Where(e=>!e.Facts.Unplayable && !e.Facts.IsPower && e.Facts.Cost<=energy).ToArray();
        var powers=deck.Where(e=>e.Facts.IsPower && e.Facts.Cost<=energy).ToArray();
        var active=new Dictionary<CardModel,double>();
        var initialStatus=deck.Count(e=>e.Facts.Unplayable);
        double seen=0,status=initialStatus,poison=0,growthStrength=0,total=0,nextEnergy=0,nextDraw=0;
        for(var turn=1;turn<=6;turn++)
        {
            var atTurnStart=new Dictionary<CardModel,double>(active);
            var size=Math.Max(5,deck.Length-initialStatus+status-active.Values.Sum());
            var draw=5+nextDraw+ordinary.Sum(e=>Math.Max(0,CardProfile.NetDraw(e.Card,e.Facts)))*5/size;
            draw=Math.Min(10,draw);seen+=draw;
            double setupEnergy=0,setupPlays=0;
            foreach(var p in powers)
            {
                // Innate brings the engine online immediately, but still pays
                // its real cost. More copies/crowding cannot create free energy.
                var chance=p.Card.Keywords.Contains(CardKeyword.Innate) ? 1 : Math.Min(1,seen/size);
                var prior=active.GetValueOrDefault(p.Card);
                var gain=Math.Max(0,chance-prior);
                if(p.Facts.Cost>0)gain=Math.Min(gain,Math.Max(0,energy-setupEnergy)/p.Facts.Cost);
                active[p.Card]=prior+gain;
                setupEnergy+=gain*p.Facts.Cost;
                setupPlays+=gain;
            }
            double Power<T>(string key,double fallback=0) where T:CardModel
                => powers.Where(e=>e.Card is T).Sum(e=>active[e.Card]*Amount(e.Card,key,fallback));
            double StartPower<T>(string key,double fallback=0) where T:CardModel
                => powers.Where(e=>e.Card is T).Sum(e=>atTurnStart.GetValueOrDefault(e.Card)*Amount(e.Card,key,fallback));
            var available=Math.Max(0,energy+nextEnergy-setupEnergy);
            var drawn=ordinary.Length*draw/size;
            var free=ordinary.Count(e=>e.Facts.Cost==0)*draw/size;
            var paid=ordinary.Where(e=>e.Facts.Cost>0).ToArray();
            var averageCost=paid.Length==0 ? 1 : paid.Average(e=>e.Facts.Cost);
            var plays=free+Math.Min(Math.Max(0,drawn-free),available/averageCost);
            var fraction=ordinary.Length==0 ? 0 : plays/ordinary.Length;
            nextEnergy=ordinary.Sum(e=>e.Facts.NextTurnEnergy)*fraction;
            nextDraw=ordinary.Sum(e=>e.Facts.NextTurnDraw)*fraction;
            var strength=Power<Inflame>("StrengthPower")+growthStrength;
            growthStrength+=Power<DemonForm>("StrengthPower");
            var dex=Power<Footwork>("DexterityPower");
            var shivs=StartPower<InfiniteBlades>("Cards",1)+ordinary.Sum(e=>e.Facts.Effects.GeneratedShivs)*fraction;
            var damage=ordinary.Sum(e=>e.Facts.Damage+Math.Max(1,e.Facts.Hits)*strength*(e.Card.Type==CardType.Attack?1:0))*fraction;
            damage+=shivs*(4+strength+Power<Accuracy>("AccuracyPower"));
            var block=ordinary.Where(e=>e.Card is not SecondWind).Sum(e=>e.Facts.Block+(e.Facts.Block>0?dex:0))*fraction;
            var cardBlocks=ordinary.Where(e=>e.Facts.Block>0 && e.Card is not SecondWind)
                .Select(e=>e.Facts.Block+dex).ToList();
            var badHand=status*draw/size;
            double cleaned=0,exhausted=ordinary.Count(e=>e.Card.Keywords.Contains(CardKeyword.Exhaust))*fraction+shivs;
            foreach(var e in ordinary.Where(e=>Cleanup(e.Card)))
            {
                var probability=Math.Min(1,fraction);
                var count=e.Card is SecondWind or FiendFire or CalculatedGamble or Purity
                    ? badHand : Math.Min(1,badHand);
                // Cinder and unupgraded True Grit choose randomly, unlike a
                // targeted exhaust. Do not grant them a guaranteed status hit.
                if(e.Card is Cinder || e.Card is TrueGrit && !e.Card.IsUpgraded)count=badHand/Math.Max(1,draw-1);
                cleaned+=count*probability;
                var consumes=e.Card is not (Prepared or CalculatedGamble or Survivor);
                if(consumes)exhausted+=Math.Max(count,e.Card is SecondWind ? (draw-1)*deck.Count(x=>x.Card.Type!=CardType.Attack)/size : 1)*probability;
                if(e.Card is SecondWind)
                {
                    var fromCard=(e.Facts.Block+dex)*Math.Max(count,(draw-1)*deck.Count(x=>x.Card.Type!=CardType.Attack)/size);
                    block+=fromCard*probability;cardBlocks.Add(fromCard);
                }
            }
            cleaned=Math.Min(badHand,cleaned);
            block+=FirstBlockBonus(cardBlocks,Math.Min(1,fraction),Power<Unmovable>("Power",1));
            var triggerPlays=ordinary.Sum(e=>1+e.Card.GetEnchantedReplayCount())*fraction+shivs+setupPlays;
            block+=Power<Afterimage>("AfterimagePower",1)*triggerPlays
                +Power<FeelNoPain>("Power")*exhausted;
            // Exhaust draw is useful future throughput, bounded by the energy
            // this model can pay. It cannot generate an unlimited free loop.
            var extraDraw=Math.Min(3,Power<DarkEmbrace>("Cards",1)*exhausted+StartPower<MachineLearning>("Cards",1));
            damage+=Math.Min(extraDraw,available)*2;
            var weakened=Math.Min(1,ordinary.Sum(e=>e.Facts.Weak)*fraction);
            var incoming=pressure*(weakened>0?1-.25*weakened:1);
            incoming=Math.Max(0,incoming-ordinary.Sum(e=>e.Facts.StrengthDown)*fraction);
            poison+=ordinary.Sum(e=>e.Facts.Poison)*fraction;
            var sourceChance=1-Math.Pow(1-deck.Count(e=>CoopBots.Kernel.CardPoisonEffects.CanSeedPoison(e.Card))/(double)deck.Length,
                Math.Min(deck.Length,seen));
            poison+=ordinary.Sum(e=>e.Facts.Effects.ConditionalPoison)*fraction*sourceChance;
            var ticks=1+Power<Accelerant>("Accelerant");
            var poisonDamage=Math.Max(0,poison*ticks-ticks*(ticks-1)/2);
            poison=Math.Max(0,poison-ticks);
            var vulnerable=Math.Min(1,ordinary.Sum(e=>e.Facts.Vulnerable)*fraction);
            damage*=1+vulnerable*(.5+Power<Cruelty>("CrueltyPower")/100);
            var statusDamage=pollution ? Math.Max(0,badHand-cleaned)*(3+3*((turn-1)/3)) : 0;
            var harm=ordinary.Sum(e=>e.Facts.HpLoss)*fraction+Math.Max(0,incoming+statusDamage-block);
            total+=damage+poisonDamage+Math.Min(incoming+statusDamage,block)-harm*1.3;
            // Discard prevents this hand's damage; only exhaust removes future
            // pollution. The ordinary scenario never fabricates boss statuses.
            var permanent=ordinary.Where(e=>Cleanup(e.Card)&&e.Card is not (Prepared or CalculatedGamble or Survivor)).Sum(_=>fraction);
            status=Math.Max(0,status-Math.Min(cleaned,permanent));
            if(turn==1)status=Math.Max(0,status-deck.Count(e=>e.Facts.Unplayable && e.Card.Keywords.Contains(CardKeyword.Ethereal))*Math.Min(1,draw/size));
            if(pollution)status+=triggerPlays/6+(turn%3==0?2:0);
        }
        return total/6;
    }
}

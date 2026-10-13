using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class RobustnessScenarios
{
    // Additional R2 production faults (r2-extra-results.json, r2-mass-result.json):
    // System.Exception: Robustness: the real A10 run must select its declared robustness weight
    // System.Exception: Robustness: mass exhaust must multiply its own block but count Afterimage only once
    // r2-endurance-result.json: System.Exception: Robustness: endurance must reach Wither's third damage tier instead of ending at six turns
    // R2 2026-10-01: 14 production faults exit 1; restored command exits 0.
    // Commands and full raw logs: work/robustness-optimization/r2-results.json.
    // net-draw: System.Exception: Robustness: gross draw must reveal an attack while the actual Wound pays Prepared's discard | Robustness: mandatory discard must prevent playing both paid targets from the same hand | Robustness: discarded Tactician must auto-play once for free and fund the three-cost attack | Robustness: discarded Reflex must reveal two real cards
    // free-discard: System.Exception: Robustness: gross draw must reveal an attack while the actual Wound pays Prepared's discard | Robustness: mandatory discard must prevent playing both paid targets from the same hand | Robustness: discarded Tactician must auto-play once for free and fund the three-cost attack | Robustness: discarded Reflex must reveal two real cards | Robustness: discard clears this hand's damage without exhausting future pollution | Robustness: a cleaner's sixth-play Wither arrives after its own cleanup
    // paid-sly: System.Exception: Robustness: discarded Tactician must auto-play once for free and fund the three-cost attack | Robustness: discarded Reflex must reveal two real cards
    // missing-energy: System.Exception: Robustness: discarded Tactician must auto-play once for free and fund the three-cost attack
    // phantom-sly: System.Exception: Robustness: discard without Sly must not refund energy
    // no-chains: System.Exception: Robustness: Chains must also reject Sly after a bound card has been played | Robustness: Chains allows only one of the first three drawn cards
    // automatic-chain-bypass: System.Exception: Robustness: Chains must also reject Sly after a bound card has been played
    // discard-is-exhaust: System.Exception: Robustness: discard clears this hand's damage without exhausting future pollution
    // no-sixth-trigger: System.Exception: Robustness: a cleaner's sixth-play Wither arrives after its own cleanup
    // no-permanent-cleanup: System.Exception: Robustness: targeted exhaust removes the harmful card from future stock
    // post-lethal-harm: System.Exception: Robustness: lethal must end incoming attacks and hand-end Wither damage
    // weak-a10: System.Exception: Robustness: A10 must carry strong interference weight before and during act three
    // disconnected-robustness: System.Exception: Robustness: shared strength must expose one interference term and remove the old boss bonus
    // double-robustness: System.Exception: Robustness: robustness must enter the deck potential exactly once
    internal static void Run()
    {
        var p=Player.CreateForNewRun<Deprived>(UnlockState.all,1);
        var run=RunState.CreateForTest([p],ascensionLevel:10,seed:"ROBUSTNESS");p.MaxEnergy=3;
        CardModel C<T>() where T:CardModel =>run.CreateCard<T>(p);
        ConstructionHand.Slot Slot(CardModel c)=>new(c,CardProfile.Of(c,3),c.Type is CardType.Status or CardType.Curse);
        ConstructionHand.Result Solve(CardModel[] cards,int draw,ConstructionHand.Context context)
            =>ConstructionHand.Solve(cards.Select(Slot).ToArray(),draw,context);
        var failures=new List<string>();
        void Check(bool ok,string message) { if(!ok)failures.Add("Robustness: "+message); }
        var prepared=C<Prepared>();var wound=C<Wound>();var strike=C<StrikeIronclad>();var bludgeon=C<Bludgeon>();
        var filter=Solve([prepared,wound,strike],2,new(1,0,100));
        Check(filter.Damage==6 && (filter.Removed&2)!=0,"gross draw must reveal an attack while the actual Wound pays Prepared's discard");
        var payment=Solve([prepared,strike,bludgeon],2,new(4,0,100));
        Check(payment.Damage==32 && (payment.Removed&2)!=0,"mandatory discard must prevent playing both paid targets from the same hand");
        var tactician=C<Tactician>();
        var sly=Solve([prepared,tactician,wound,bludgeon],3,new(2,0,100));
        Check(sly.Damage==32 && sly.EnergySpent==3 && (sly.Played&2)!=0,"discarded Tactician must auto-play once for free and fund the three-cost attack");
        tactician.RemoveKeyword(CardKeyword.Sly);
        var without=Solve([prepared,tactician,wound,bludgeon],3,new(2,0,100));
        Check(without.Damage==0,"discard without Sly must not refund energy");
        tactician.AddKeyword(CardKeyword.Sly);
        var reflex=Solve([prepared,C<Reflex>(),wound,strike,C<StrikeIronclad>()],2,new(2,0,100));
        Check(reflex.Damage==12 && reflex.Drawn==5,"discarded Reflex must reveal two real cards");
        var chained=Solve([prepared,tactician,wound,bludgeon],3,new(2,0,100,Chains:true));
        Check(chained.Damage==0,"Chains must also reject Sly after a bound card has been played");
        var strikes=new[]{C<StrikeIronclad>(),C<StrikeIronclad>(),C<StrikeIronclad>(),C<StrikeIronclad>(),C<StrikeIronclad>()};
        Check(Solve(strikes,5,new(5,0,100,Chains:true)).Damage==18,"Chains allows only one of the first three drawn cards");
        var slot=new ConstructionHand.Slot(null,null,true,9);
        var clean=ConstructionHand.Solve([Slot(prepared),slot,Slot(strike)],2,new(1,0,100,WitherDamage:9));
        Check(clean.StatusDamage==0 && clean.StatusExhausted==0,"discard clears this hand's damage without exhausting future pollution");
        // Afterimage supplies a reason to play the otherwise break-even cleaner.
        var trigger=Slot(prepared) with { Facts=CardProfile.Of(prepared) with { Block=1 } };
        var sixth=ConstructionHand.Solve([trigger,slot,Slot(strike)],2,new(0,0,100,WitherDamage:9,Counter:5));
        Check(sixth.Generated==1 && sixth.StatusDamage==9,"a cleaner's sixth-play Wither arrives after its own cleanup");
        var grit=C<TrueGrit>();grit.UpgradeInternal();
        var exhaust=ConstructionHand.Solve([Slot(grit),slot,Slot(strike)],3,new(1,10,100,WitherDamage:9));
        Check(exhaust.StatusExhausted==1 && exhaust.WitherExhausted==1,"targeted exhaust removes the harmful card from future stock");
        var wind=Slot(C<SecondWind>()) with { PlayBlock=2 };
        var mass=ConstructionHand.Solve([wind,slot,slot],3,new(1,30,100,WitherDamage:9));
        Check(mass.Block==12 && mass.StatusExhausted==2,"mass exhaust must multiply its own block but count Afterimage only once");
        var kill=ConstructionHand.Solve([Slot(bludgeon),slot],2,new(3,20,18,WitherDamage:9,Counter:5));
        Check(kill.Utility==18,"lethal must end incoming attacks and hand-end Wither damage");
        var endurance=SustainedDeckValue.EnduranceTrace([wound],3);
        Check(endurance.Count==9 && endurance[6].StatusDamage>endurance[3].StatusDamage,
            "endurance must reach Wither's third damage tier instead of ending at six turns");
        Check(DeckRobustness.WeightFor(10,2)==1 && DeckRobustness.WeightFor(10,0)>=.8
            && DeckRobustness.WeightFor(0,2)<DeckRobustness.WeightFor(10,2),"A10 must carry strong interference weight before and during act three");
        foreach(var c in new[]{C<StrikeIronclad>(),C<StrikeIronclad>(),C<StrikeIronclad>(),C<DefendIronclad>(),C<DefendIronclad>(),C<DefendIronclad>(),C<Survivor>(),C<Neutralize>()})p.Deck.AddInternal(c);
        using var evaluation=DeckStrength.BeginEvaluation();
        var before=DeckStrength.Evaluate(p.Deck.Cards.ToArray(),p);
        var after=DeckStrength.Evaluate(p.Deck.Cards.Append(prepared).ToArray(),p);
        var add=BuildValue.Add(prepared,p);
        Check(before.RobustnessWeight==.80,"the real A10 run must select its declared robustness weight");
        Check(Math.Abs(before.Robustness)>1e-6 && before.Boss==0 && add.Reason.Contains("robustness:"),"shared strength must expose one interference term and remove the old boss bonus");
        Check(Math.Abs(before.Total-(before.Combat+before.Robustness+before.Growth+before.Prior+before.Unmodeled-before.Size-before.Commitment))<1e-7,
            "robustness must enter the deck potential exactly once");
        Console.WriteLine($"ROBUSTNESS prepared add={add.Total:F4} combat={after.Combat-before.Combat:F4} robustness={after.Robustness-before.Robustness:F4} prior={after.Prior-before.Prior:F4}");
        // Addition/removal/upgrade identities are already checked by
        // UnifiedDeckStrengthScenarios; do not add a redundant identity guard.
        if(failures.Count>0)throw new Exception(string.Join("\n",failures));
        Console.WriteLine("PASS: robust construction covers actual filtering, discard payment, Sly, Chains, Wither timing and one shared potential.");
    }
}

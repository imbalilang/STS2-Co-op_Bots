using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;

namespace CoopBots.Building;

// Upstream has no permanent-deck utility. Owner 2026-10-01 requests A10
// interference preparedness as a survival need. These are stress scenarios,
// not estimated encounter probabilities or a forecast of the hidden boss.
internal static class DeckRobustness
{
    internal enum Interference { None, Wither, Chains, Adaptable }
    internal readonly record struct Estimate(double Clean,double Wither,double Chains,double Stressed,double Weight,double StressClean,double Adaptable=0)
    {
        // All scenarios are per-turn outcomes. Blend absolute outcomes with
        // nonnegative weights: improving the clean endurance baseline must
        // never become a penalty merely because stressed play improved less.
        internal double Adjustment=>Weight*((StressClean+Stressed)*.5-Clean);
        internal double Total=>Clean+Adjustment;
    }
    internal static double WeightFor(int ascension,int act)
        =>ascension>=10?new[]{.80,.90,1.0}[Math.Clamp(act,0,2)]:new[]{.40,.55,.70}[Math.Clamp(act,0,2)];
    internal static Estimate Evaluate(IReadOnlyList<CardModel> deck,Player player,int energy)
    {
        var clean=SustainedDeckValue.ScenarioStrength(deck,energy,Interference.None);
        // A short ordinary encounter can end before Wither escalates. Compare
        // all three endurance alternatives for nine turns at identical 240 HP
        // and pressure, normalized to the same per-turn unit as ordinary play.
        var baseline=SustainedDeckValue.ScenarioStrength(deck,energy,Interference.None,true);
        var wither=SustainedDeckValue.ScenarioStrength(deck,energy,Interference.Wither,true);
        var chains=SustainedDeckValue.ScenarioStrength(deck,energy,Interference.Chains,true);
        var adaptable=SustainedDeckValue.ScenarioStrength(deck,energy,Interference.Adaptable,true);
        // A strong answer to one boss must not conceal a collapse against the
        // other. Retain an average share so further useful improvements count.
        var stressed=.65*Math.Min(Math.Min(wither,chains),adaptable)+.35*(wither+chains+adaptable)/3;
        var run=player.RunState;
        if(run.AscensionLevel<10)
        {
            // At lower ascension a visible single boss gives public context.
            try { if(run.Act.BossEncounter is AeonglassBoss)stressed=wither;
                  else if(run.Act.BossEncounter is QueenBoss)stressed=chains; }
            catch { /* Detached fixtures have no generated boss. */ }
        }
        return new(clean,wither,chains,stressed,WeightFor(run.AscensionLevel,run.CurrentActIndex),baseline,adaptable);
    }
}

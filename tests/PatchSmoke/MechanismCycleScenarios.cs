using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class MechanismCycleScenarios
{
    // R2 evidence is recorded after deliberate production faults, not inferred
    // from a green run. See work/strength-20260928-v20/r2-results.json.
    // Actual failures (each production fault exited 1; sources restored):
    // System.Exception: Fan of Knives and Serpent Form must not disable held-engine evaluation.
    // System.Exception: Serpent Form native upgrade must improve the shared construction/camp estimate.
    // System.Exception: An unknown held engine must preserve a labeled partial comparison.
    // System.Exception: Removing an unknown engine must keep its structural value, not treat it as blank.
    // System.Exception: Corruption cannot replay a consumed skill on later cycles.
    // System.Exception: Corruption setup may play free skills but cannot grant free paid attacks.
    // System.Exception: A self-exhausting draw/energy card cannot provide recurring output after consumption.
    // System.Exception: Fan of Knives produces its four Shivs once, not on every turn.
    // System.Exception: Knife Trap must reuse exhausted Shivs in later turns.
    // System.Exception: Serpent Form cannot trigger its own newly applied damage.
    // System.Exception: Demon Form strength starts on a later turn, not its application turn.
    // System.Exception: Mass exhaust must remove multiple future statuses per played cleaner.
    // System.Exception: Temporary strength must expire instead of accumulating between turns.
    // System.Exception: Attack replay must contribute resolved extra attacks within the same turn.
    // System.Exception: Exhaust-threshold damage must be absent without supply and activate after actual consumption.
    internal static void Run()
    {
        var players=Enumerable.Range(0,2).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro,31,i))).ToArray();
        var run=RunState.CreateForTest(players,seed:"MECHANISM-CYCLE");
        var owner=players[0];owner.MaxEnergy=3;
        T Make<T>() where T:CardModel => run.CreateCard<T>(owner);
        var plain=Enumerable.Range(0,5).SelectMany(_=>new CardModel[]{Make<StrikeSilent>(),Make<DefendSilent>()}).ToArray();
        var fan=Make<FanOfKnives>();var serpent=Make<SerpentForm>();var image=Make<Afterimage>();
        var silent=plain.Concat(new CardModel[]{fan,serpent,image,Make<InfiniteBlades>()}).ToArray();
        var prepared=Make<Prepared>();
        var covered=SustainedDeckValue.Compare(silent,prepared,null,3);
        Check(covered is {ModelShare:1},"Fan of Knives and Serpent Form must not disable held-engine evaluation.");
        foreach(var c in silent)owner.Deck.AddInternal(c);
        Check(BuildValue.Add(prepared,owner).Reason.Contains("cycle:"),
            "Reward/shop entry must expose the active cycle evaluator.");
        Check(BuildValue.UpgradeDelta(serpent,owner).Total>0
            && BuildValue.UpgradeDelta(serpent,owner).Reason.Contains("deck-strength:"),
            "Serpent Form native upgrade must improve the shared construction/camp estimate.");
        Console.WriteLine("PASS: real Silent engines reach reward, shop and native upgrade valuation.");

        var unknown=Make<NoxiousFumes>();
        var partial=silent.Append(unknown).ToArray();
        var estimate=SustainedDeckValue.Compare(partial,prepared,null,3);
        Check(estimate is {ModelShare:>0 and <1} && estimate.Coverage.Contains("NOXIOUS_FUMES"),
            "An unknown held engine must preserve a labeled partial comparison.");
        Check(SustainedDeckValue.Compare(partial,null,unknown,3) is null,
            "Removing an unknown engine must keep its structural value, not treat it as blank.");
        Console.WriteLine("PASS: unknown held powers retain partial estimates and unknown edits retain fallback.");

        var corruption=Make<Corruption>();
        corruption.AddKeyword(MegaCrit.Sts2.Core.Entities.Cards.CardKeyword.Innate);
        var skills=Enumerable.Range(0,8).Select(_=>(CardModel)Make<DefendIronclad>()).ToArray();
        var corruptDeck=skills.Append(corruption).Append(Make<FeelNoPain>()).ToArray();
        var cycle=SustainedDeckValue.Estimate(corruptDeck,3);
        Check(cycle.All(t=>double.IsFinite(t.Damage)) && skills.All(c=>cycle.Sum(t=>t.Plays.GetValueOrDefault(c))<=1.001),
            "Corruption cannot replay a consumed skill on later cycles.");
        var strike=Make<StrikeIronclad>();
        var mixed=SustainedDeckValue.Estimate(skills.Append(corruption).Append(strike).ToArray(),3);
        Check(mixed[0].Plays.GetValueOrDefault(strike)==0 && skills.Sum(c=>mixed[0].Plays.GetValueOrDefault(c))>0,
            "Corruption setup may play free skills but cannot grant free paid attacks.");
        var offering=Make<Offering>();
        var singleUse=SustainedDeckValue.Estimate(plain.Append(offering).ToArray(),3);
        Check(singleUse.Sum(t=>t.Plays.GetValueOrDefault(offering))<=1.001,
            "A self-exhausting draw/energy card cannot provide recurring output after consumption.");
        var fanOnly=SustainedDeckValue.Estimate(plain.Append(fan).ToArray(),3);
        Check(Math.Abs(fanOnly.Sum(t=>t.Shivs)-4)<.001,
            "Fan of Knives produces its four Shivs once, not on every turn.");
        var dance=Make<BladeDance>();var trap=Make<KnifeTrap>();
        var knives=plain.Append(dance).ToArray();
        Check(SustainedDeckValue.Estimate(knives.Append(trap).ToArray(),3).Sum(t=>t.Shivs)
            >SustainedDeckValue.Estimate(knives,3).Sum(t=>t.Shivs),
            "Knife Trap must reuse exhausted Shivs in later turns.");
        var serpentOnly=SustainedDeckValue.Estimate([serpent],3);
        Check(serpentOnly.Sum(t=>t.Damage)==0,"Serpent Form cannot trigger its own newly applied damage.");
        var demon=Make<DemonForm>();
        var growth=SustainedDeckValue.Estimate(plain.Append(demon).ToArray(),3);
        Check(growth[0].Strength==0 && growth[1].Strength>0,
            "Demon Form strength starts on a later turn, not its application turn.");
        Console.WriteLine("PASS: finite card stocks and native power trigger timing survive six-turn evaluation.");

        var wind=Make<SecondWind>();
        var statuses=Enumerable.Range(0,8).Select(_=>(CardModel)Make<Wither>()).ToArray();
        var cleanup=SustainedDeckValue.Estimate(plain.Concat(statuses).Append(wind).ToArray(),3);
        Check(cleanup.Any(t=>t.StatusExhausted>t.Plays.GetValueOrDefault(wind)+.01),
            "Mass exhaust must remove multiple future statuses per played cleaner.");
        var temporary=Make<SetupStrike>();
        var tempDeck=plain.Append(temporary).ToArray();
        var temp=SustainedDeckValue.Estimate(tempDeck,3);
        Check(temp[0].Strength>0 && temp[^1].Strength<=temp[0].Strength+.001,
            "Temporary strength must expire instead of accumulating between turns.");
        var replay=Make<OneTwoPunch>();var conditional=Make<PactsEnd>();
        double Printed(SustainedDeckValue.TurnEstimate turn)=>turn.Plays.Sum(p=>CardProfile.Of(p.Key).Damage*p.Value);
        var replayTurns=SustainedDeckValue.Estimate(plain.Append(replay).ToArray(),3);
        Check(replayTurns.Any(t=>t.Damage>Printed(t)+.01),
            "Attack replay must contribute resolved extra attacks within the same turn.");
        var closed=SustainedDeckValue.Estimate(plain.Append(conditional).ToArray(),3);
        var opened=SustainedDeckValue.Estimate(skills.Append(corruption).Append(conditional).ToArray(),3);
        Check(closed.All(t=>Math.Abs(t.Damage-Printed(t))<.001) && opened.Any(t=>t.Damage>Printed(t)+.01),
            "Exhaust-threshold damage must be absent without supply and activate after actual consumption.");
        Console.WriteLine("PASS: mass cleanup, temporary buffs, replays and threshold attacks retain distinct lifecycles.");
    }

    private static void Check(bool condition,string message)
    {if(!condition)throw new Exception(message);}
}

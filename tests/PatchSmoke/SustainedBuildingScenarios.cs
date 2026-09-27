using System.Reflection;
using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class SustainedBuildingScenarios
{
    // R2 V16: three deliberate faults exited 1, then production restored.
    // System.Exception: The original shop deck with Unmovable must retain its block-engine evaluation.
    // Original floor-28 stock must buy Second Wind with the complete saved deck, including Unmovable.
    // System.Exception: Unmovable must value doubling an eligible card's block, not power-trigger block or every defence.
    // System.Exception: First-card block bonus must spend native stack slots across expected eligible plays.
    // Commands/raw logs: work/strength-20260927-v16/r2-results.json.
    // R2 2026-09-28: 25 deliberate production faults exited 1; source restored.
    // Commands/full raw failures: work/strength-20260927-v14/r2-results.json.
    // System.Exception: A healthy engine owner must upgrade rather than heal thirteen HP or dig.
    // System.Exception: A one-HP engine owner must still select emergency recovery.
    // System.Exception: The seat with a cheap upgrade must act before the engine owner at camp.
    // System.Exception: Six-turn construction must recognize useful status cleanup and repeated block.
    // Feel No Pain must gain value from a real repeatable exhaust source.
    // The actual shop planner must buy seventy-five-gold Second Wind with 112 gold and an exhaust deck.
    // System.Exception: Feel No Pain must gain value from a real repeatable exhaust source.
    // System.Exception: The actual shop planner must buy seventy-five-gold Second Wind with 112 gold and an exhaust deck.
    // System.Exception: Unsupported permanent cost/exhaust changes must retain the structural fallback.
    // System.Exception: Cycle evaluation must be deterministic and leave the real deck unchanged.
    private static readonly List<string> Failures=[];
    private static void Check(bool value,string why) {if(!value)Failures.Add(why);}
    internal static void Run()
    {
        Failures.Clear();
        var ps=Enumerable.Range(0,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro,25,i))).ToArray();
        var run=RunState.CreateForTest(ps,seed:"SUSTAINED");
        foreach(var p in ps) { p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(67);p.MaxEnergy=3; }
        T Add<T>(Player p) where T:CardModel {var c=run.CreateCard<T>(p);p.Deck.AddInternal(c);return c;}
        var host=ps[0];
        for(var i=0;i<5;i++){Add<StrikeSilent>(host);Add<DefendSilent>(host);}
        Add<Neutralize>(host);Add<Survivor>(host);Add<AscendersBane>(host);Add<Flechettes>(host);
        Add<Deflect>(host);Add<SuckerPunch>(host);Add<CalculatedGamble>(host).UpgradeInternal();
        Add<InfiniteBlades>(host);Add<EscapePlan>(host);var image=Add<Afterimage>(host);
        host.AddRelicInternal(ModelDb.Relic<RingOfTheSnake>().ToMutable());
        var upgrade=BuildValue.UpgradeDelta(image,host);
        Console.WriteLine($"PROBE image upgrade {upgrade}");
        Check(upgrade.Total>BuildValue.UpgradeDelta(host.Deck.Cards.OfType<SuckerPunch>().Single(),host).Total,
            "Twenty-card Afterimage startup must outrank the routine Sucker Punch upgrade.");
        var smith=new SmithRestSiteOption(host);
        RestSiteOption[] choices=[smith,new HealRestSiteOption(host),new DigRestSiteOption(host)];
        var choose=typeof(BotRestSitePatch).GetMethod("Choose",BindingFlags.Static|BindingFlags.NonPublic)!;
        int Camp(){var plan=choose.Invoke(null,[host,choices])!;return (int)plan.GetType().GetProperty("Index")!.GetValue(plan)!;}
        Check(Camp()==0,"A healthy engine owner must upgrade rather than heal thirteen HP or dig.");
        host.Creature.SetCurrentHpInternal(1);
        Check(Camp()==1,"A one-HP engine owner must still select emergency recovery.");
        host.Creature.SetCurrentHpInternal(67);
        Add<StrikeIronclad>(ps[1]);
        Check(BotRestSitePatch.InvestmentOrder([host,ps[1]]).First()==ps[1],
            "The seat with a cheap upgrade must act before the engine owner at camp.");
        Check(BotRestSitePatch.DiscountedFights(8,.9)>BotRestSitePatch.DiscountedFights(1,.9)
            && BotRestSitePatch.DiscountedFights(8,.9)>BotRestSitePatch.DiscountedFights(8,.1),
            "Upgrade investment must scale with remaining fights and survival prospects.");
        Console.WriteLine("PASS: camp invests in early engines while preserving emergency healing and coordinated order.");

        var warrior=ps[2];
        for(var i=0;i<5;i++){Add<StrikeIronclad>(warrior);Add<DefendIronclad>(warrior);}
        Add<Bash>(warrior);Add<AscendersBane>(warrior);Add<Cinder>(warrior).UpgradeInternal();
        Add<Anger>(warrior);Add<Anger>(warrior);Add<Cruelty>(warrior);
        var deck=warrior.Deck.Cards.ToArray();
        var upgradesBefore=deck.Select(c=>c.IsUpgraded).ToArray();
        var wind=run.CreateCard<SecondWind>(warrior);var pain=run.CreateCard<FeelNoPain>(warrior);
        var windValue=SustainedDeckValue.Compare(deck,wind,null,3);
        var painValue=SustainedDeckValue.Compare(deck.Append(wind).ToArray(),pain,null,3);
        var plainPain=SustainedDeckValue.Compare(deck,pain,null,3);
        Console.WriteLine($"PROBE cycle wind={windValue?.Delta:F3} painWithWind={painValue?.Delta:F3} painPlain={plainPain?.Delta:F3} add={BuildValue.Add(wind,warrior)}");
        Check(windValue is {Delta:>0},"Six-turn construction must recognize useful status cleanup and repeated block.");
        Check(painValue is {Delta:>0} && plainPain is not null && painValue.Delta>plainPain.Delta,
            "Feel No Pain must gain value from a real repeatable exhaust source.");
        var inventory=new MerchantInventory(warrior);warrior.Gold=112;
        var entry=new MerchantCardEntry(warrior,inventory,[],CardType.Skill);
        typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(entry,new CardCreationResult(wind));
        typeof(MerchantEntry).GetField("_cost",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(entry,75);
        ((List<MerchantCardEntry>)inventory.CharacterCardEntries).Add(entry);
        Check(BotShopPlanner.Choose(inventory)?.Index==0,
            "The actual shop planner must buy seventy-five-gold Second Wind with 112 gold and an exhaust deck.");
        Check(SustainedDeckValue.Compare(deck,wind,null,3)==windValue && deck.SequenceEqual(warrior.Deck.Cards)
            && upgradesBefore.SequenceEqual(deck.Select(c=>c.IsUpgraded)),
            "Cycle evaluation must be deterministic and leave the real deck unchanged.");
        Check(SustainedDeckValue.Compare(deck,run.CreateCard<Corruption>(warrior),null,3) is null,
            "Unsupported permanent cost/exhaust changes must retain the structural fallback.");
        Console.WriteLine("PASS: sustained construction values exhaust synergy and reaches the actual shop purchase decision.");
        // Exact seat-four card list/upgrade levels from the floor-28 shop save.
        var real=ps[3];real.Gold=112;
        for(var i=0;i<3;i++)Add<StrikeIronclad>(real);
        for(var i=0;i<4;i++)Add<DefendIronclad>(real);
        Add<Bash>(real).UpgradeInternal();Add<AscendersBane>(real);Add<Stomp>(real);
        Add<Anger>(real);Add<Anger>(real);Add<MoltenFist>(real).UpgradeInternal();Add<Cinder>(real).UpgradeInternal();
        Add<Spite>(real);Add<Cruelty>(real);Add<ShrugItOff>(real);Add<Unmovable>(real).UpgradeInternal();Add<Exterminate>(real);
        var realWind=run.CreateCard<SecondWind>(real);var realDeck=real.Deck.Cards.ToArray();
        var realValue=SustainedDeckValue.Compare(realDeck,realWind,null,3);
        Console.WriteLine($"PROBE actual shop wind {BuildValue.Add(realWind,real)}");
        Check(realValue is {Delta:>0},"The original shop deck with Unmovable must retain its block-engine evaluation.");
        var stock=new MerchantInventory(real);var offer=new MerchantCardEntry(real,stock,[],CardType.Skill);
        typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(offer,new CardCreationResult(realWind));
        typeof(MerchantEntry).GetField("_cost",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(offer,75);
        ((List<MerchantCardEntry>)stock.CharacterCardEntries).Add(offer);
        Check(BotShopPlanner.Choose(stock)?.Index==0,"Original floor-28 stock must buy Second Wind with the complete saved deck, including Unmovable.");
        var without=realDeck.Where(c=>c is not Unmovable).ToArray();
        Check(SustainedDeckValue.Compare(without,realDeck.OfType<Unmovable>().Single(),null,3) is {Delta:>0},
            "Unmovable must value doubling an eligible card's block, not power-trigger block or every defence.");
        Check(SustainedDeckValue.FirstBlockBonus([8,5],1,1)==8
            && SustainedDeckValue.FirstBlockBonus([8,5],1,2)==13
            && SustainedDeckValue.FirstBlockBonus([8,5],.5,1)==6.5,
            "First-card block bonus must spend native stack slots across expected eligible plays.");
        Console.WriteLine("PASS: actual saved shop deck includes native first-card block doubling in the purchase decision.");
        if(Failures.Count>0)throw new Exception(string.Join("\n",Failures));
    }
}

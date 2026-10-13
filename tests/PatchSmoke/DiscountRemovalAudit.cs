using System.Reflection;
using System.Text.Json;
using CoopBots;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class DiscountRemovalAudit
{
    private static double prior;
    private static bool Override(CardModel card,ref double __result)
    {
        if(card.Rarity!=CardRarity.Basic || !(card.Tags.Contains(CardTag.Strike)||card.Tags.Contains(CardTag.Defend))) return true;
        __result=prior;return false;
    }
    internal static void Run(string path)
    {
        var rows=new List<object>();var harmony=new Harmony("coopbots.discount-removal-audit");
        harmony.Patch(AccessTools.Method(typeof(DeckStrength),nameof(DeckStrength.CommunityPrior)),
            prefix:new HarmonyMethod(typeof(DiscountRemovalAudit),nameof(Override)));
        try
        {
            foreach(var scenario in new[]{
                (Name:"silent-two-blade-dance",Character:"SILENT",Extra:new[]{"BLADE_DANCE","BLADE_DANCE"},Card:"ACCURACY",Price:75),
                (Name:"ironclad-starter",Character:"IRONCLAD",Extra:Array.Empty<string>(),Card:"BULLY",Price:37),
                (Name:"ironclad-starter",Character:"IRONCLAD",Extra:Array.Empty<string>(),Card:"POMMEL_STRIKE",Price:25),
                (Name:"silent-shiv",Character:"SILENT",Extra:new[]{"BLADE_DANCE","BLADE_DANCE","BLADE_DANCE","CLOAK_AND_DAGGER","INFINITE_BLADES"},Card:"ACCURACY",Price:75),
                (Name:"silent-shiv",Character:"SILENT",Extra:new[]{"BLADE_DANCE","BLADE_DANCE","BLADE_DANCE","CLOAK_AND_DAGGER","INFINITE_BLADES"},Card:"AFTERIMAGE",Price:75),
                (Name:"silent-discard",Character:"SILENT",Extra:new[]{"PREPARED","TACTICIAN","REFLEX","ACROBATICS"},Card:"CALCULATED_GAMBLE",Price:38)})
            {
                var ps=Enumerable.Range(0,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,BotRegistry.CreateId(BotDifficulty.Pro,87,i))).ToArray();
                var character=ModelDb.AllCharacters.First(c=>c.Id.Entry==scenario.Character);
                var run=RunState.CreateForTest(ps,ascensionLevel:10,seed:"DISCOUNT-REMOVAL-AUDIT");
                foreach(var q in ps)
                {
                    typeof(Player).GetField("<Character>k__BackingField",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(q,character);
                    q.MaxEnergy=3;q.Creature.SetMaxHpInternal(80);q.Creature.SetCurrentHpInternal(80);
                    foreach(var c in character.StartingDeck)q.Deck.AddInternal(run.CreateCard(c,q));
                    q.Deck.AddInternal(ConstructionParallelScenarios.Card("ASCENDERS_BANE",q));
                }
                var p=ps[0];p.Gold=100;
                foreach(var id in scenario.Extra)p.Deck.AddInternal(ConstructionParallelScenarios.Card(id,p));
                var offered=ConstructionParallelScenarios.Card(scenario.Card,p);
                var inventory=new MerchantInventory(p);ShopRemovalAnchorScenarios.Offer(inventory,offered,scenario.Price);
                var removal=new MerchantCardRemovalEntry(p);ShopRemovalAnchorScenarios.Cost(removal,100);
                typeof(MerchantInventory).GetProperty("CardRemovalEntry")!.SetValue(inventory,removal);
                foreach(var value in new[]{-6d,-18d,-17d,-15d})
                {
                    prior=value;using var scope=DeckStrength.BeginEvaluation();
                    var cardScore=BuildValue.Add(offered,p).Total;var target=BotShopPlanner.BestRemoval(p.Deck.Cards,p);
                    var cardGold=cardScore*BotRestSitePatch.InvestmentHorizon(p)*BotShopPlanner.GoldPerDeckValue;
                    var removeGold=target.value*BotShopPlanner.GoldPerDeckValue;
                    var pick=BotShopPlanner.Choose(inventory);
                    var row=new {scenario=scenario.Name,card=scenario.Card,price=scenario.Price,prior,cardScore,cardGold,cardNet=cardGold-scenario.Price,
                        removal=p.Deck.Cards[target.index].Id.Entry,removeGold,removeNet=removeGold-100,
                        choice=pick is null?"save":BotShopPlanner.Key(inventory.AllEntries.ElementAt(pick.Index))};
                    rows.Add(row);Console.WriteLine(JsonSerializer.Serialize(row));
                    File.WriteAllText(path,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
                }
            }
        }
        finally{harmony.UnpatchAll(harmony.Id);}
    }

    internal static void ShopPriorAudit(double value)
    {
        prior=value;
        var harmony=new Harmony("coopbots.shop-prior-audit");
        harmony.Patch(AccessTools.Method(typeof(DeckStrength),nameof(DeckStrength.CommunityPrior)),
            prefix:new HarmonyMethod(typeof(DiscountRemovalAudit),nameof(Override)));
        try { ShopRemovalAnchorScenarios.Run(); }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
}

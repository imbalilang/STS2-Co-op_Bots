using System.Reflection;
using CoopBots;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class WoodGenericAudit
{
    internal static void Run()
    {
        var player=Player.CreateForNewRun<Deprived>(UnlockState.all,BotRegistry.CreateId(BotDifficulty.Pro,96,0));
        var state=RunState.CreateForTest(new[]{player},ascensionLevel:10,seed:"WOOD-GENERIC-AUDIT");
        player.MaxEnergy=3;
        foreach(var id in new[]{"STRIKE_IRONCLAD","DEFEND_IRONCLAD","BASH"})
        {
            var card=ConstructionParallelScenarios.Card(id,player);
            if(id=="BASH"){card.UpgradeInternal();card.FinalizeUpgradeInternal();}
            player.Deck.AddInternal(card);
        }
        var score=typeof(BotBrain).GetMethod("ScoreDeckCard",BindingFlags.Static|BindingFlags.NonPublic)!;
        foreach(var card in player.Deck.Cards)
            Console.WriteLine($"GENERIC SCORE {card.Id.Entry} upgraded={card.IsUpgraded} score={score.Invoke(null,new object[]{card})}");
        var method=typeof(CardSelectCmd).GetMethods().First(m=>m.Name=="FromDeckGeneric");
        Func<CardModel,bool> filter=c=>c.IsTransformable&&c.Rarity==CardRarity.Basic;
        var handled=BotCardChoiceDispatcher.TrySelect(method,new object[]{player,new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt,1),filter},out var result);
        Console.WriteLine($"GENERIC DISPATCH handled={handled} selected={string.Join(',',((IEnumerable<CardModel>)result!).Select(c=>c.Id.Entry+(c.IsUpgraded?"+":"")))}");
        Console.WriteLine("DLL SHA256="+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(BotBrain).Assembly.Location))));
    }
}

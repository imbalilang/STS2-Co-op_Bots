using System.Reflection;
using System.Runtime.CompilerServices;
using CoopBots;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class TransformEventScenarios
{
    // R2: deliberate production mutations, all exit 1 (2026-10-01).
    // Disable TO_TRANSFORM recognition:
    // System.Exception: Transform events: generic transform prompt must select weak targets
    // Disable WoodCarvings caller capture:
    // System.Exception: Transform events: native async caller must retain fixed target PECK
    // Add 10 to replacement delta:
    // System.Exception: Transform events: fixed transformation must equal the complete replacement delta
    // Raw logs: work/transform-bash-audit/mutant-{prompt,caller,delta}.log.
    private sealed class Captured : Exception;
    private static CardModel? selected;
    private static readonly HashSet<string> usedFixed = new();
    private static void FixedCalled(string replacementId) => usedFixed.Add(replacementId);
    private static bool Capture(CardModel original, ref Task<CardPileAddResult?> __result)
    { selected=original; __result=Task.FromException<CardPileAddResult?>(new Captured()); return false; }
    private static void Check(bool condition,string message) { if(!condition)throw new Exception("Transform events: "+message); }
    internal static void Run()
    {
        TestEnvironment.Ensure();
        using var pump=new ConstructionParallelScenarios.Pump();
        var players=Enumerable.Range(0,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,BotRegistry.CreateId(BotDifficulty.Pro,97,i))).ToArray();
        var run=RunState.CreateForTest(players,ascensionLevel:10,seed:"TRANSFORM-EVENTS");
        foreach(var p in players)
        {
            p.MaxEnergy=3;
            foreach(var id in new[]{"STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","BASH"})
            {
                var c=ConstructionParallelScenarios.Card(id,p);
                if(id=="BASH"){c.UpgradeInternal();c.FinalizeUpgradeInternal();}
                p.Deck.AddInternal(c);
            }
        }
        var owner=players[0];
        var prefs=new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt,1);
        Check(BotCardChoiceDispatcher.PurposeFor("FromDeckGeneric",false,prefs)=="FromDeckForTransformation","generic transform prompt must select weak targets");
        Check(BotCardChoiceDispatcher.PurposeFor("FromDeckGeneric",false,new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt,1))=="FromDeckGeneric","unrelated generic purpose must stay separate");
        var generic=typeof(CardSelectCmd).GetMethods().First(m=>m.Name=="FromDeckGeneric");
        Func<CardModel,bool> filter=c=>c.IsTransformable&&c.Rarity==CardRarity.Basic;
        BotCardChoiceDispatcher.TrySelect(generic,new object[]{owner,prefs,filter},out var answer);
        Check(((IEnumerable<CardModel>)answer!).Single().Id.Entry!="BASH","generic transform must not choose upgraded Bash over basics");

        // These native event methods all use the dedicated transform selector.
        // Inspect the upstream state machine, not our own method-name catalogue.
        foreach(var (type,method) in new[]{(typeof(AromaOfChaos),"LetGo"),(typeof(EndlessConveyor),"JellyLiver"),
            (typeof(MorphicGrove),"Group"),(typeof(Symbiote),"KillWithFire"),(typeof(Trial),"NondescriptInnocent"),(typeof(WhisperingHollow),"Hug")})
        {
            var native=AccessTools.Method(type,method).GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
            Check(PatchProcessor.GetOriginalInstructions(AccessTools.Method(native,"MoveNext")).Any(i=>i.operand is MethodInfo called
                && called.DeclaringType==typeof(CardSelectCmd)&&called.Name=="FromDeckForTransformation"),"native transform entry changed: "+type.Name);
        }
        var random=typeof(CardSelectCmd).GetMethods().First(m=>m.Name=="FromDeckForTransformation");
        BotCardChoiceDispatcher.TrySelect(random,new object[]{owner,new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt,2)},out answer);
        var randomTargets=((IEnumerable<CardModel>)answer!).ToArray();
        Check(randomTargets.Length==2&&randomTargets.Distinct().Count()==2&&randomTargets.All(c=>c.IsTransformable&&c.Id.Entry!="BASH"),"two-card random transform must choose two legal basic targets");

        // Live 2026-10-04: Astrolabe goes through :RNG_NICHE_UPGRADE, so the chooser
        // gets a KNOWN Rng and used to rank each source by its single predicted
        // replacement. It turned BLOODLETTING (the deck's energy engine) into a rare
        // card while unupgraded Strikes/Defends stayed in the deck. A known RNG must
        // still spend the edit on filler first.
        var blood=ConstructionParallelScenarios.Card("BLOODLETTING",owner);owner.Deck.AddInternal(blood);
        var uppercut=ConstructionParallelScenarios.Card("UPPERCUT",owner);owner.Deck.AddInternal(uppercut);
        var fullDeck=owner.Deck.Cards.ToList();
        var preferred=TransformPlan.PreferredTargets(fullDeck,fullDeck);
        Check(preferred.Count>0&&preferred.All(c=>!ReferenceEquals(c,blood)&&!ReferenceEquals(c,uppercut)
            &&c.Rarity==CardRarity.Basic&&!c.IsUpgraded),
            "Astrolabe targets must be the unupgraded basic Strikes/Defends while any remain");
        var knownRng=new Rng((ulong)424242);
        var knownPlan=TransformPlan.Choose(owner,fullDeck,3,knownRng,fullDeck);
        Check(knownPlan.Steps.All(s=>s.Card.Rarity==CardRarity.Basic&&!s.Card.IsUpgraded),
            "known-RNG transform must consume the basic Strikes/Defends first");
        Check(knownPlan.Steps.All(s=>!ReferenceEquals(s.Card,blood)&&!ReferenceEquals(s.Card,uppercut)),
            "known-RNG transform must preserve Bloodletting and Uppercut while basics remain");
        Console.WriteLine($"TRANSFORM known-rng targets={string.Join(',',knownPlan.Steps.Select(s=>s.Card.Id.Entry))}");

        var harmony=new Harmony("coopbots.test.fixed-transform");
        harmony.Patch(AccessTools.Method(typeof(CardCmd),"Transform",new[]{typeof(CardModel),typeof(CardModel),typeof(CardPreviewStyle)}),prefix:new HarmonyMethod(typeof(TransformEventScenarios),nameof(Capture)));
        harmony.Patch(AccessTools.Method(typeof(FixedTransform),nameof(FixedTransform.Rank)),prefix:new HarmonyMethod(typeof(TransformEventScenarios),nameof(FixedCalled)));
        try
        {
            foreach(var (method,id) in new[]{("Bird","PECK"),("Torus","TORIC_TOUGHNESS")})
            {
                var model=(WoodCarvings)RuntimeHelpers.GetUninitializedObject(typeof(WoodCarvings));
                typeof(AbstractModel).GetProperty("IsMutable")!.SetValue(model,true);
                typeof(EventModel).GetProperty("Owner")!.SetValue(model,owner);
                var before=owner.Deck.Cards.ToArray();
                var best=FixedTransform.Rank(owner,before.Where(filter),id).First();
                var replacement=ModelDb.AllCards.First(c=>c.Id.Entry==id).ToMutable();replacement.Owner=owner;
                var expected=BuildValue.Deck(before.Select(c=>ReferenceEquals(c,best.Card)?replacement:c).ToArray(),owner)-BuildValue.Deck(before,owner);
                Check(Math.Abs(best.Value-expected)<1e-8,"fixed transformation must equal the complete replacement delta");
                Check(Math.Abs((id=="PECK"?WoodCarvingsHandler.Price(owner,model).Bird:WoodCarvingsHandler.Price(owner,model).Torus)-expected)<1e-8,"wood option and target scores must agree");
                selected=null;usedFixed.Clear();
                try { pump.Run(async()=>{await (Task)AccessTools.Method(typeof(WoodCarvings),method).Invoke(model,null)!;return true;}); }
                catch(Captured) { }
                Check(usedFixed.Contains(id),"native async caller must retain fixed target "+id);
                Check(ReferenceEquals(selected,best.Card),"native wood choice must use the same replacement ranking");
                Check(selected?.Id.Entry!="BASH","wood transform must preserve upgraded Bash in starter deck");
                Check(owner.Deck.Cards.SequenceEqual(before)&&before.Single(c=>c.Id.Entry=="BASH").IsUpgraded,"preview must preserve native deck and upgrade");
                Console.WriteLine($"TRANSFORM {id} selected={selected!.Id.Entry} delta={best.Value:F3}");
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        Console.WriteLine("PASS: seven native transform events audited; generic prompts, fixed replacements and async target selection preserve the intended deck edit.");
    }
}

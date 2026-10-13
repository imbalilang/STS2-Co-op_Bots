using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using CoopBots;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Card;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Constructed four-seat paid-native mechanism, not the archived F45 hidden root.
// No native damage/power/draw/effect replacement. Actual Facts-block withdrawal
// r2-withering-v1/proof.json: six exits 0/0/1/0/0/0, exact source/DLL/PDB restore.
internal static class HellraiserFactsNativeScenarios
{
    private const BindingFlags Fields = BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    internal static void Run(string? selected=null)
    {
        TestEnvironment.Ensure();
        using var presentation=new Presentation();
        var assembly=typeof(CardModel).Assembly;
        Console.WriteLine($"LOADED Game={assembly.Location} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))}");
        if(selected is null or "paid") Paid();
        if(selected is null or "withering") Withering();
        if(selected is null or "retired") KernelPlanningFactsScenarios.Run("power-origin-hidden");
    }
    private static void Withering()
    {
        var party=Enumerable.Range(1,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,(ulong)(21100+i))).ToArray();
        var combat=new CombatState(runState:RunState.CreateForTest(party,seed:"WITHERING-PAID-A10",ascensionLevel:10)){RoundNumber=1};
        var cards=new List<CardModel>();
        foreach(var player in party)
        {
            AccessTools.Field(typeof(Player),"<Character>k__BackingField").SetValue(player,ModelDb.Character<Ironclad>());
            player.ResetCombatState();combat.AddPlayer(player);player.MaxEnergy=8;
            player.Creature.SetMaxHpInternal(80);player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase=PlayerTurnPhase.Play;player.PlayerCombatState.GainEnergy(8);
            for(var i=0;i<6;i++)
            {var card=combat.CreateCard<DefendIronclad>(player);player.PlayerCombatState.Hand.AddInternal(card);cards.Add(card);}
        }
        var model=ModelDb.Monster<Aeonglass>().ToMutable();var enemy=combat.CreateCreature(model,CombatSide.Enemy,"aeonglass");combat.AddCreature(enemy);
        using var native=Scope("NativeFactsState",combat);using var managers=Scope("NativeReadyManagerScope",combat);
        model.SetUpForCombat();model.RollMove(party.Select(p=>p.Creature));
        model.AfterAddedToRoom().GetAwaiter().GetResult();
        var ids=party.Select(p=>p.NetId).ToArray();var root=KernelCombatCapture.Capture(combat);
        var before=root.PlanningState(ids);var sibling=root.Fork();var branch=root.Fork();
        Console.WriteLine($"WITHERING ROOT reusable={before.Reusable} powerTargets={enemy.Powers.OfType<WitheringPresencePower>().Count()} cardsLeft={string.Join(',',enemy.Powers.OfType<WitheringPresencePower>().Select(p=>p.DynamicVars["CardsLeft"].IntValue))}");
        Check(before.Reusable,"real four-target native Withering root must qualify");
        foreach(var player in party)
        {
            var card=cards.First(c=>c.Owner==player);
            Check(branch.Play(card,null,out var reason),"predicted Withering paid card settles: "+reason);
            NativePaid(card);
        }
        var expected=branch.PlanningState(ids);var actual=KernelCombatCapture.Capture(combat).PlanningState(ids);
        File.WriteAllText("withering-expected.txt",expected.Semantics);File.WriteAllText("withering-actual.txt",actual.Semantics);
        Console.WriteLine($"WITHERING PAID nativeCardsLeft={string.Join(',',enemy.Powers.OfType<WitheringPresencePower>().Select(p=>p.DynamicVars["CardsLeft"].IntValue))} nativeHistory={CombatManager.Instance.History.CardPlaysFinished.Count()} reusable={expected.Reusable}/{actual.Reusable} mismatch={expected.FirstMismatch(actual)??"equal"}");
        Check(root.PlanningState(ids).Matches(before)&&sibling.PlanningState(ids).Matches(before),"four paid native Withering cards cannot mutate root or sibling");
        // Actual R2: System.InvalidOperationException: Hellraiser facts: four-target paid Withering must match complete native Facts and RNG
        Check(expected.Matches(actual),"four-target paid Withering must match complete native Facts and RNG");
        Console.WriteLine("PASS: four-target paid Withering matches complete native Facts and RNG.");
        var invalid=branch.Fork();var invalidState=(SimulatedCombatState)AccessTools.Property(typeof(KernelSession),"Combat").GetValue(invalid)!;
        var invalidPower=invalidState.EffectivePowers().OfType<WitheringPresencePower>().First();
        invalid.Simulator.StateStore.Get(invalidPower,()=>new CounterPredictionState(5)).Value=4;
        Check(invalid.PlanningState(ids).Semantics.Contains("CounterPredictionState")&&!invalid.PlanningState(ids).Matches(expected),
            "inconsistent owned Withering counter must remain serialized and rejected");
        var fractional=branch.Fork();var fractionalState=(SimulatedCombatState)AccessTools.Property(typeof(KernelSession),"Combat").GetValue(fractional)!;
        var fractionalPower=fractionalState.EffectivePowers().OfType<WitheringPresencePower>().First();
        fractionalState.SetPowerDynamicVar(fractional.Simulator,fractionalPower,"CardsLeft",5);
        fractionalPower=fractionalState.EffectivePowers().OfType<WitheringPresencePower>().First();
        fractionalPower.DynamicVars["CardsLeft"].BaseValue=5.5m;
        Check(fractional.PlanningState(ids).Semantics.Contains("CounterPredictionState")&&!fractional.PlanningState(ids).Matches(expected),
            "fractional CardsLeft must not be erased by integer truncation");
        var detached=branch.Fork();var orphan=(WitheringPresencePower)ModelDb.Power<WitheringPresencePower>().ToMutable();
        orphan.Target=party[0].Creature;AccessTools.Property(typeof(PowerModel),"Owner").SetValue(orphan,enemy);
        _=detached.Simulator.StateStore.Get(orphan,()=>new CounterPredictionState(6));
        Check(!detached.PlanningState(ids).Reusable,"detached Withering counter must fail closed");
        Check(branch.PlanningState(ids).Matches(expected),"negative fork controls cannot mutate positive branch");
        Console.WriteLine("PASS: inconsistent, fractional and detached Withering counters remain strict.");
        var owner=party[3];var remaining=cards.Where(c=>c.Owner==owner).Skip(1).ToArray();
        foreach(var card in remaining.Take(4))
        {Check(branch.Play(card,null,out var why),"predicted fifth-card threshold settles: "+why);NativePaid(card);}
        var other=party[2];var otherRemaining=cards.Where(c=>c.Owner==other).Skip(1).ToArray();
        foreach(var card in otherRemaining.Take(3))
        {Check(branch.Play(card,null,out var why),"predicted fourth-card neighbor settles: "+why);NativePaid(card);}
        var fifth=KernelCombatCapture.Capture(combat).PlanningState(ids);
        Check(branch.PlanningState(ids).Matches(fifth),"actual fifth paid card preserves complete native Facts and RNG");
        Check(enemy.Powers.OfType<WitheringPresencePower>().Single(p=>p.Target==owner.Creature).DynamicVars["CardsLeft"].BaseValue==1
            &&enemy.Powers.OfType<WitheringPresencePower>().Single(p=>p.Target==other.Creature).DynamicVars["CardsLeft"].BaseValue==2
            &&!owner.PlayerCombatState!.Hand.Cards.Any(c=>c is Wither),"real targets with one versus two cards left must remain distinct");
        Check(branch.Play(otherRemaining[3],null,out var neighborWhy),"predicted native noncritical neighbor settles: "+neighborWhy);NativePaid(otherRemaining[3]);
        Check(!other.PlayerCombatState!.Hand.Cards.Any(c=>c is Wither)&&!branch.Hand(other).Any(c=>c is Wither),
            "real neighbor fifth card at CardsLeft two must not create Wither");
        var critical=KernelCombatCapture.Capture(combat).PlanningState(ids);
        Check(branch.PlanningState(ids).Matches(critical),"actual neighbor fifth card preserves complete native Facts and RNG");
        var beforeSixth=branch.Fork();var last=remaining[4];
        Check(branch.Play(last,null,out var lastWhy),"predicted sixth paid card settles: "+lastWhy);NativePaid(last);
        var sixth=KernelCombatCapture.Capture(combat).PlanningState(ids);var sixthPredicted=branch.PlanningState(ids);
        File.WriteAllText("sixth-expected.txt",sixthPredicted.Semantics);File.WriteAllText("sixth-actual.txt",sixth.Semantics);
        Console.WriteLine($"WITHERING SIXTH nativeCardsLeft={string.Join(',',enemy.Powers.OfType<WitheringPresencePower>().Select(p=>p.DynamicVars["CardsLeft"].BaseValue))} nativeWither={owner.PlayerCombatState!.Hand.Cards.Count(c=>c is Wither)} predictedWither={branch.Hand(owner).Count(c=>c is Wither)} nativeEnergy={owner.PlayerCombatState.Energy} predictedEnergy={branch.Energy(owner)} nativeHistory={CombatManager.Instance.History.CardPlaysFinished.Count()} reusable={sixthPredicted.Reusable}/{sixth.Reusable} mismatch={sixthPredicted.FirstMismatch(sixth)??"equal"}");
        // Independently generated native/predicted cards have different physical
        // identities. Preserve that existing strict refusal, while comparing all
        // canonical fields/RNG and recapturing a truly reusable actual root.
        Check(sixthPredicted.Semantics==sixth.Semantics&&!sixthPredicted.Matches(sixth)
            &&sixthPredicted.FirstMismatch(sixth)!.StartsWith("cacheMissAxis=card-reference",StringComparison.Ordinal),
            "native sixth play keeps exact canonical Facts/RNG and strict generated-card identity refusal");
        var generatedRoot=KernelCombatCapture.Capture(combat);
        Check(generatedRoot.PlanningState(ids).Matches(generatedRoot.Fork().PlanningState(ids)),
            "real native Wither successor is reusable only under its actual physical root");
        Check(owner.PlayerCombatState.Hand.Cards.Count(c=>c is Wither)==1&&branch.Hand(owner).Count(c=>c is Wither)==1,
            "only sixth true native card must generate one Wither in target seat four");
        Check(party.Take(3).All(p=>!p.PlayerCombatState!.Hand.Cards.Any(c=>c is Wither))
            &&enemy.Powers.OfType<WitheringPresencePower>().Where(p=>p.Target!=owner.Creature&&p.Target!=other.Creature).All(p=>p.DynamicVars["CardsLeft"].BaseValue==5)
            &&enemy.Powers.OfType<WitheringPresencePower>().Single(p=>p.Target==other.Creature).DynamicVars["CardsLeft"].BaseValue==1,
            "four target-specific native countdowns must remain isolated");
        Check(!critical.Matches(sixth)&&beforeSixth.PlanningState(ids).Matches(critical),
            "real critical CardsLeft transition and resulting future Wither must remain distinct");
        Console.WriteLine("PASS: native critical countdown generates target-isolated Wither; canonical Facts/RNG agree and physical refusal remains.");
    }
    private static void NativePaid(CardModel card)
    {
        var paid=card.SpendResources().GetAwaiter().GetResult();
        Check(paid.Item1==1,"real native Defend pays one energy");
        card.OnPlayWrapper(new ThrowingPlayerChoiceContext(),null,false,new ResourceInfo{EnergySpent=paid.Item1,EnergyValue=paid.Item1,StarsSpent=paid.Item2,StarValue=paid.Item2},skipCardPileVisuals:true).GetAwaiter().GetResult();
    }
    private static void Paid()
    {
        var party=Enumerable.Range(1,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,(ulong)(20100+i))).ToArray();
        var combat=new CombatState(runState:RunState.CreateForTest(party,seed:"HELLRAISER-PAID-A10",ascensionLevel:10)){RoundNumber=1};
        foreach(var player in party)
        {
            AccessTools.Field(typeof(Player),"<Character>k__BackingField").SetValue(player,ModelDb.Character<Ironclad>());
            player.ResetCombatState(); combat.AddPlayer(player); player.MaxEnergy=3;
            player.Creature.SetMaxHpInternal(80);player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase=PlayerTurnPhase.Play;player.PlayerCombatState.GainEnergy(3);
            for(var i=0;i<12;i++)player.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(player));
        }
        var owner=party[3];var card=combat.CreateCard<Hellraiser>(owner);owner.PlayerCombatState!.Hand.AddInternal(card);
        var model=ModelDb.Monster<Axebot>().ToMutable();var enemy=combat.CreateCreature(model,CombatSide.Enemy,"axebot");combat.AddCreature(enemy);
        using var native=Scope("NativeFactsState",combat);using var managers=Scope("NativeReadyManagerScope",combat);
        model.SetUpForCombat(); model.RollMove(party.Select(p=>p.Creature));
        model.SetMoveImmediate((MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState)model.MoveStateMachine!.States["BOOT_UP_MOVE"],true);
        var ids=party.Select(p=>p.NetId).ToArray();var root=KernelCombatCapture.Capture(combat);
        var before=root.PlanningState(ids);var sibling=root.Fork();var branch=root.Fork();
        Console.WriteLine($"ROOT reusable={before.Reusable} move={model.NextMove.Id} semanticsLength={before.Semantics.Length}");
        Check(before.Reusable,"actual supported four-seat Axebot root must qualify");
        Check(branch.Play(card,null,out var why),"predicted actual Hellraiser must settle: "+why);
        var paid=card.SpendResources().GetAwaiter().GetResult();
        Check(paid.Item1==2,"real native Hellraiser pays two energy");
        card.OnPlayWrapper(new ThrowingPlayerChoiceContext(),null,false,new ResourceInfo{EnergySpent=paid.Item1,EnergyValue=paid.Item1,StarsSpent=paid.Item2,StarValue=paid.Item2},skipCardPileVisuals:true).GetAwaiter().GetResult();
        var live=owner.Creature.GetPower<HellraiserPower>()!;
        var state=(SimulatedCombatState)AccessTools.Property(typeof(KernelSession),"Combat").GetValue(branch)!;
        var predicted=state.EffectivePowers().OfType<HellraiserPower>().Single();
        var raw=(int)AccessTools.Field(typeof(PowerModel),"_amount").GetValue(predicted)!;
        var expected=branch.PlanningState(ids);var actual=KernelCombatCapture.Capture(combat).PlanningState(ids);
        File.WriteAllText("paid-expected.txt",expected.Semantics);File.WriteAllText("paid-actual.txt",actual.Semantics);
        Console.WriteLine($"PAID amountNative={live.Amount} predictedRaw={raw} predictedEffective={branch.Power<HellraiserPower>(owner.Creature)} energyNative={owner.PlayerCombatState.Energy} energyPredicted={branch.Energy(owner)} counterNative={Counter(live)} expectedHellPayload={expected.Semantics.Contains("HellraiserPredictionState")} actualHellPayload={actual.Semantics.Contains("HellraiserPredictionState")} reusable={expected.Reusable}/{actual.Reusable} mismatch={expected.FirstMismatch(actual)??"equal"}");
        Check(root.PlanningState(ids).Matches(before)&&sibling.PlanningState(ids).Matches(before),"paid native successor cannot mutate captured parent or sibling");
        // Existing direct Play does not materialize the required payload until
        // its first real handler. Record this separate qualification boundary;
        // this is not the target qualified next-turn captured-state mismatch.
        Check(live.Amount==1 && branch.Power<HellraiserPower>(owner.Creature)==1 && paid.Item1==2
            && owner.PlayerCombatState.Energy==1 && branch.Energy(owner)==1,
            "paid native Hellraiser must preserve actual amount and payment");
        var paidRoot=KernelCombatCapture.Capture(combat);var paidFacts=paidRoot.PlanningState(ids);
        Check(paidFacts.Reusable&&paidFacts.Matches(paidRoot.Fork().PlanningState(ids)),
            "real paid successor recapture must own a qualified complete native Hellraiser payload");
        branch.AutoResolveTurnStartChoices=true;branch.AutoResolveEnemyChoices=true;
        foreach(var player in party)Check(branch.EndTurn(player,80,out why),"predicted four-seat next turn settles: "+why);
        Advance(managers);
        var stored=(IDictionary)AccessTools.Field(branch.Simulator.StateStore.GetType(),"_states").GetValue(branch.Simulator.StateStore)!;
        foreach(DictionaryEntry entry in stored)
        {
            var key=((AbstractModel,Type))entry.Key;
            if(key.Item1 is HellraiserPower held)Console.WriteLine($"STORED modelRaw={AccessTools.Field(typeof(PowerModel),"_amount").GetValue(held)} getter={held.Amount} type={key.Item2.Name} effectiveReference={state.EffectivePowers().Any(p=>ReferenceEquals(p,held))}");
        }
        var next=KernelCombatCapture.Capture(combat).PlanningState(ids);var forecast=branch.PlanningState(ids);
        File.WriteAllText("next-expected.txt",forecast.Semantics);File.WriteAllText("next-actual.txt",next.Semantics);
        Console.WriteLine($"NEXT nativeRound={combat.RoundNumber} nativeEnemyHP={enemy.CurrentHp} forecastEnemyHP={branch.Hp(enemy)} counter={Counter(live)} nativeHistory={CombatManager.Instance.History.CardPlaysFinished.Count()} reusable={forecast.Reusable}/{next.Reusable} mismatch={forecast.FirstMismatch(next)??"equal"}");
        Check(Counter(live)==0,"normal HP enemy must reset rather than grow infinite autoplay counter");
        Check(forecast.Matches(next),"actual next-turn Hellraiser Strike autoplay must preserve complete reusable native Facts and RNG");
        Console.WriteLine("PASS: real next-turn Hellraiser autoplay matches native full Facts and RNG.");
    }
    private static int Counter(HellraiserPower power)
    {var data=AccessTools.Field(typeof(PowerModel),"_internalData").GetValue(power)!;return (int)AccessTools.Field(data.GetType(),"infiniteAutoPlaysThisTurn").GetValue(data)!;}
    private static IDisposable Scope(string name,CombatState combat)=> (IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios),name),Fields,null,[combat],null)!;
    private static void Advance(IDisposable managers)
    {var type=managers.GetType();var turn=AccessTools.Property(type,"Turn").GetValue(managers)!;foreach(var name in new[]{"EndPlayerTurnPhaseOneInternal","EndPlayerTurnPhaseTwoInternal","SwitchFromPlayerToEnemySide","StartTurn","StartTurn"})AccessTools.Method(type,"Call").Invoke(managers,[name,name=="StartTurn"?new object?[]{turn,null}:new object?[]{turn}]);}
    private static void Check(bool yes,string message){if(!yes)throw new InvalidOperationException("Hellraiser facts: "+message);}
    private sealed class Presentation:IDisposable
    {private readonly Harmony harmony=new("coopbots.hellraiser-facts.presentation");internal Presentation(){harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText"),prefix:new HarmonyMethod(typeof(Presentation),nameof(Format)));}private static bool Format(ref string __result){__result="";return false;}public void Dispose()=>harmony.UnpatchAll(harmony.Id);}
}

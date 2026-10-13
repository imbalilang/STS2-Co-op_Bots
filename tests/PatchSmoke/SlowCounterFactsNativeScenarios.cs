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

// Constructed four-seat paid-native Slow; no archived hidden-root equivalence.
// Actual effect implementations and identity guards stay active.
internal static class SlowCounterFactsNativeScenarios
{
    private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    internal static void Run()
    {
        TestEnvironment.Ensure();using var presentation=new Presentation();
        var assembly=typeof(CardModel).Assembly;
        Console.WriteLine($"LOADED Game={assembly.Location} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))}");
        var party=Enumerable.Range(1,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,(ulong)(22100+i))).ToArray();
        var combat=new CombatState(runState:RunState.CreateForTest(party,seed:"SLOW-COUNTER-PAID-A10",ascensionLevel:10)){RoundNumber=1};
        var cards=new List<CardModel>();
        foreach(var player in party)
        {
            AccessTools.Field(typeof(Player),"<Character>k__BackingField").SetValue(player,ModelDb.Character<Ironclad>());
            player.ResetCombatState();combat.AddPlayer(player);player.MaxEnergy=3;
            player.Creature.SetMaxHpInternal(80);player.Creature.SetCurrentHpInternal(80);
            player.PlayerCombatState!.Phase=PlayerTurnPhase.Play;player.PlayerCombatState.GainEnergy(3);
            for(var i=0;i<3;i++){var card=combat.CreateCard<StrikeIronclad>(player);player.PlayerCombatState.Hand.AddInternal(card);cards.Add(card);}
            for(var i=0;i<8;i++)player.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(player));
        }
        var model=ModelDb.Monster<Axebot>().ToMutable();var enemy=combat.CreateCreature(model,CombatSide.Enemy,"axebot");combat.AddCreature(enemy);
        using var native=Scope("NativeFactsState",combat);using var managers=Scope("NativeReadyManagerScope",combat);
        model.SetUpForCombat();model.RollMove(party.Select(p=>p.Creature));
        model.SetMoveImmediate((MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState)model.MoveStateMachine!.States["BOOT_UP_MOVE"],true);
        PowerCmd.Apply<SlowPower>(new ThrowingPlayerChoiceContext(),enemy,1m,enemy,null).GetAwaiter().GetResult();
        var ids=party.Select(p=>p.NetId).ToArray();var root=KernelCombatCapture.Capture(combat);var before=root.PlanningState(ids);var sibling=root.Fork();var branch=root.Fork();
        var slow=enemy.GetPower<SlowPower>()!;var historyBefore=CombatManager.Instance.History.CardPlaysFinished.Count();
        Check(before.Reusable,"actual native Slow root must qualify");
        Console.WriteLine($"SLOW ROOT reusable={before.Reusable} amount={slow.Amount} slowAmount={slow.DynamicVars["SlowAmount"].BaseValue} displayAmount={slow.DynamicVars["DisplayAmount"].BaseValue} target={enemy.CombatId}");
        KernelPlanningState? firstExpected=null,firstActual=null;var damages=new List<int>();
        for(var i=0;i<3;i++)
        {
            var card=cards.First(c=>c.Owner==party[i]);var hp=enemy.CurrentHp;
            Check(branch.Play(card,enemy,out var reason),"predicted paid Strike settles: "+reason);
            var paid=card.SpendResources().GetAwaiter().GetResult();Check(paid.Item1==1,"real Strike pays one energy");
            card.OnPlayWrapper(new ThrowingPlayerChoiceContext(),enemy,false,new ResourceInfo{EnergySpent=paid.Item1,EnergyValue=paid.Item1,StarsSpent=paid.Item2,StarValue=paid.Item2},skipCardPileVisuals:true).GetAwaiter().GetResult();
            damages.Add(hp-enemy.CurrentHp);
            var expected=branch.PlanningState(ids);var actual=KernelCombatCapture.Capture(combat).PlanningState(ids);
            if(i==0){firstExpected=expected;firstActual=actual;}
            File.WriteAllText($"slow-{i+1}-expected.txt",expected.Semantics);File.WriteAllText($"slow-{i+1}-actual.txt",actual.Semantics);
            var power=State(branch).EffectivePowers().OfType<SlowPower>().Single();var counter=branch.Simulator.StateStore.Get(power,()=>new CounterPredictionState(-999));
            Console.WriteLine($"SLOW PAID index={i+1} actor={card.Owner.NetId} nativeHP={enemy.CurrentHp} predictedHP={branch.Hp(enemy)} damage={damages[^1]} nativeSlow={slow.DynamicVars["SlowAmount"].BaseValue} nativeDisplay={slow.DynamicVars["DisplayAmount"].BaseValue} predictedSlow={power.DynamicVars["SlowAmount"].BaseValue} predictedDisplay={power.DynamicVars["DisplayAmount"].BaseValue} counter={counter.Value} nativeEnergy={card.Owner.PlayerCombatState!.Energy} predictedEnergy={branch.Energy(card.Owner)} historyDelta={CombatManager.Instance.History.CardPlaysFinished.Count()-historyBefore} reusable={expected.Reusable}/{actual.Reusable} mismatch={expected.FirstMismatch(actual)??"equal"}");
            Check(enemy.CurrentHp==branch.Hp(enemy)&&card.Owner.PlayerCombatState.Energy==branch.Energy(card.Owner),"true native Slow damage and payment must agree");
        }
        Check(damages[2]>damages[0],"real native Slow accumulation must increase later Strike damage");
        Check(root.PlanningState(ids).Matches(before)&&sibling.PlanningState(ids).Matches(before),"native Slow successor cannot mutate parent/sibling");
        // r2-slow-v1 withdrew only the Slow canonical block: actual native exit1,
        // System.InvalidOperationException: Slow facts: paid native Slow must match complete PlanningFacts and RNG
        // Exact source/31 DLL+PDB restore plus forced GREEN retained this full-facts assertion.
        Check(firstExpected!.Matches(firstActual!),"paid native Slow must match complete PlanningFacts and RNG");
        var settled=branch.PlanningState(ids);Check(settled.Matches(KernelCombatCapture.Capture(combat).PlanningState(ids)),"third paid native Slow preserves complete Facts and RNG");
        Console.WriteLine("PASS: paid native Slow accumulation matches complete Facts, RNG, payment and increasing damage.");
        var invalid=branch.Fork();var badPower=State(invalid).EffectivePowers().OfType<SlowPower>().Single();
        invalid.Simulator.StateStore.Get(badPower,()=>new CounterPredictionState(3)).Value=2;
        Check(invalid.PlanningState(ids).Semantics.Contains("CounterPredictionState")&&!invalid.PlanningState(ids).Matches(settled),"inconsistent Slow counter must remain serialized and rejected");
        var fractional=branch.Fork();var fractionalState=State(fractional);var fractionalPower=fractionalState.EffectivePowers().OfType<SlowPower>().Single();
        fractionalState.SetPowerDynamicVar(fractional.Simulator,fractionalPower,"SlowAmount",3);
        fractionalPower=fractionalState.EffectivePowers().OfType<SlowPower>().Single();fractionalPower.DynamicVars["SlowAmount"].BaseValue=3.5m;
        Check(fractional.PlanningState(ids).Semantics.Contains("CounterPredictionState")&&!fractional.PlanningState(ids).Matches(settled),"fractional Slow counter must not be erased by truncation");
        var detached=branch.Fork();var orphan=(SlowPower)ModelDb.Power<SlowPower>().ToMutable();AccessTools.Property(typeof(PowerModel),"Owner").SetValue(orphan,enemy);
        _=detached.Simulator.StateStore.Get(orphan,()=>new CounterPredictionState(0));
        Check(!detached.PlanningState(ids).Reusable,"detached Slow counter must fail closed");
        var display=branch.Fork();var displayPower=State(display).EffectivePowers().OfType<SlowPower>().Single();
        State(display).SetPowerDynamicVar(display.Simulator,displayPower,"DisplayAmount",31);
        Check(!display.PlanningState(ids).Matches(settled),"Slow DisplayAmount must remain part of the complete canonical facts");
        Check(branch.PlanningState(ids).Matches(settled),"negative Slow fork controls must not mutate the positive branch");
        Console.WriteLine("PASS: wrong, fractional, detached Slow counters and DisplayAmount remain strict.");
        branch.AutoResolveTurnStartChoices=true;branch.AutoResolveEnemyChoices=true;
        foreach(var player in party)Check(branch.EndTurn(player,80,out var why),"predicted real next turn settles: "+why);
        Advance(managers);
        var reset=KernelCombatCapture.Capture(combat).PlanningState(ids);var expectedReset=branch.PlanningState(ids);
        File.WriteAllText("slow-reset-expected.txt",expectedReset.Semantics);File.WriteAllText("slow-reset-actual.txt",reset.Semantics);
        Console.WriteLine($"SLOW RESET round={combat.RoundNumber} nativeSlow={slow.DynamicVars["SlowAmount"].BaseValue} nativeDisplay={slow.DynamicVars["DisplayAmount"].BaseValue} reusable={expectedReset.Reusable}/{reset.Reusable} mismatch={expectedReset.FirstMismatch(reset)??"equal"}");
        Check(slow.DynamicVars["SlowAmount"].BaseValue==0&&slow.DynamicVars["DisplayAmount"].BaseValue==0,"native enemy side must truly reset both Slow DynamicVars");
        Check(expectedReset.Matches(reset),"native Slow reset must match complete PlanningFacts and RNG");
        var owner=party[3];var nextCard=owner.PlayerCombatState!.Hand.Cards.First(c=>c is StrikeIronclad);var nextHP=enemy.CurrentHp;var nextBlock=enemy.Block;
        Check(branch.Play(nextCard,enemy,out var nextWhy),"post-reset predicted real Strike settles: "+nextWhy);
        var nextPaid=nextCard.SpendResources().GetAwaiter().GetResult();Check(nextPaid.Item1==1,"native post-reset Strike really pays one energy");
        nextCard.OnPlayWrapper(new ThrowingPlayerChoiceContext(),enemy,false,new ResourceInfo{EnergySpent=nextPaid.Item1,EnergyValue=nextPaid.Item1,StarsSpent=nextPaid.Item2,StarValue=nextPaid.Item2},skipCardPileVisuals:true).GetAwaiter().GetResult();
        var post=KernelCombatCapture.Capture(combat).PlanningState(ids);var expectedPost=branch.PlanningState(ids);
        File.WriteAllText("slow-post-reset-expected.txt",expectedPost.Semantics);File.WriteAllText("slow-post-reset-actual.txt",post.Semantics);
        var actualDamage=nextHP-enemy.CurrentHp+nextBlock-enemy.Block;
        Console.WriteLine($"SLOW POSTRESET actor={owner.NetId} hpDamage={nextHP-enemy.CurrentHp} blockDamage={nextBlock-enemy.Block} totalDamage={actualDamage} nativeHP={enemy.CurrentHp} predictedHP={branch.Hp(enemy)} nativeSlow={slow.DynamicVars["SlowAmount"].BaseValue} nativeDisplay={slow.DynamicVars["DisplayAmount"].BaseValue} reusable={expectedPost.Reusable}/{post.Reusable} mismatch={expectedPost.FirstMismatch(post)??"equal"}");
        // BootUp legitimately adds block. Compare the actual attack absorbed by
        // HP plus block rather than assuming every true Strike must lose HP.
        Check(actualDamage==damages[0]&&slow.DynamicVars["SlowAmount"].BaseValue==1&&slow.DynamicVars["DisplayAmount"].BaseValue==10,"true next Strike after native reset returns to base damage and starts a new countdown");
        Check(expectedPost.Matches(post),"real post-reset native Slow Strike preserves complete PlanningFacts and RNG");
        Console.WriteLine("PASS: real native Slow reset and next paid Strike match complete Facts and RNG.");
    }
    private static SimulatedCombatState State(KernelSession branch)=>(SimulatedCombatState)AccessTools.Property(typeof(KernelSession),"Combat").GetValue(branch)!;
    private static IDisposable Scope(string name,CombatState combat)=>(IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios),name),Fields,null,[combat],null)!;
    private static void Advance(IDisposable managers){var type=managers.GetType();var turn=AccessTools.Property(type,"Turn").GetValue(managers)!;foreach(var name in new[]{"EndPlayerTurnPhaseOneInternal","EndPlayerTurnPhaseTwoInternal","SwitchFromPlayerToEnemySide","StartTurn","StartTurn"})AccessTools.Method(type,"Call").Invoke(managers,[name,name=="StartTurn"?new object?[]{turn,null}:new object?[]{turn}]);}
    private static void Check(bool yes,string message){if(!yes)throw new InvalidOperationException("Slow facts: "+message);}
    private sealed class Presentation:IDisposable{private readonly Harmony harmony=new("coopbots.slow-facts.presentation");internal Presentation(){harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText"),prefix:new HarmonyMethod(typeof(Presentation),nameof(Format)));}private static bool Format(ref string __result){__result="";return false;}public void Dispose()=>harmony.UnpatchAll(harmony.Id);}
}

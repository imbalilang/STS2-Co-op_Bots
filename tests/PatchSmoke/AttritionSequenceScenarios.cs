using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class AttritionSequenceScenarios
{
    // R2 V18: five production faults fail with assertion exceptions, restored code passes.
    // Commands and raw output: work/strength-20260928-v18/r2-results.json.
    // Kill must commit the profitable four-card sequence instead of ending with four energy; played=0.
    // A profitable sequence must retain its opening ahead of Pass even with TopK one.
    // Native Wither damage must be charged without a second estimated future pollution tax; gain=1.694.
    // The selected Second Wind must actually exhaust both Withers and remove their hand damage.
    private static bool Format(ref string __result){__result="";return false;}
    internal static void Run()
    {
        new Harmony("coopbots.attrition.sequence.presentation").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText"),
            prefix:new HarmonyMethod(typeof(AttritionSequenceScenarios),nameof(Format)));
        var p=Player.CreateForNewRun<Deprived>(UnlockState.all,1);
        var run=RunState.CreateForTest([p],seed:"FOUR-ENERGY-WITHER");
        var combat=new CombatState(runState:run);p.ResetCombatState();combat.AddPlayer(p);
        p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);
        p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(4);
        for(var i=0;i<25;i++)p.Deck.AddInternal(run.CreateCard<StrikeSilent>(p));
        for(var i=0;i<4;i++)p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeSilent>(p));
        var foe=combat.CreateCreature(ModelDb.Monster<Aeonglass>().ToMutable(),CombatSide.Enemy,"0");
        combat.AddCreature(foe);foe.Monster!.SetUpForCombat();foe.SetMaxHpInternal(500);foe.SetCurrentHpInternal(500);
        ((Aeonglass)foe.Monster).WitherUpgradeCount=2;
        foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine!.States["EYE_LASERS_MOVE"],true);
        var aura=ModelDb.Power<WitheringPresencePower>().ToMutable();aura.Target=p.Creature;
        aura.ApplyInternal(foe,6,true);aura.DynamicVars["CardsLeft"].BaseValue=1;
        var root=KernelSession.Capture(combat);
        var single=root.CardBranches(root.Hand(p)[0],foe,1).First(b=>b.Boundary.Length==0).State;
        var baseline=KernelTacticalPolicy.Value(root,[p],[p]);
        if(KernelTacticalPolicy.ActionValue(root,single,[p],[p])>=baseline)
            throw new Exception("Sequence fixture must have a locally losing first attack.");
        foreach(var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var line=KernelRollout.Run(root.Fork(),[p],[p],new(policy,MaxSteps:5));
            var plays=line.Actions.TakeWhile(a=>!a.EndTurn).ToArray();
            if(plays.Length!=4)throw new Exception($"{policy} must commit the profitable four-card sequence instead of ending with four energy; played={plays.Length}.");
            var replay=root.Fork();
            foreach(var a in plays)if(!replay.ReplayPlanned(a,1,out var error))throw new Exception("Sequence replay failed: "+error);
            if(replay.Energy(p)!=0 || replay.Hp(foe)!=476 || replay.Threat(p).HandDamage!=9)
                throw new Exception("Sequence must spend four energy, deal twenty-four damage and retain the real nine-damage Wither.");
            if(Math.Abs(replay.WitheringBurden(p)-root.WitheringBurden(p)-6)>1e-8)
                throw new Exception("All four plays must still pay four sixths of future Wither pollution.");
            var gain=KernelTacticalPolicy.ActionValue(root,replay,[p],[p])-baseline;
            if(gain<24*.8-9*1.3)
                throw new Exception($"Native Wither damage must be charged without a second estimated future pollution tax; gain={gain:F3}.");
        }
        if(KernelTournament.FirstActions(root,[p],1,new(IncludePotions:false)).FirstOrDefault()?.Card is not StrikeSilent)
            throw new Exception("A profitable sequence must retain its opening ahead of Pass even with TopK one.");
        var tournament=KernelTournament.Run(root,[p],[p],new(TopK:1,IncludePotions:false,
            Rollout:new(MaxRounds:1),UsePolicyPortfolio:true),budgetMs:10000);
        if(tournament.FirstAction?.Card is not StrikeSilent || tournament.Script?.TakeWhile(a=>!a.EndTurn).Count()!=4)
            throw new Exception("The tournament must deploy the four-card continuation, not merely pass its isolated rollout test.");
        if(p.PlayerCombatState.Energy!=4 || p.PlayerCombatState.Hand.Cards.Count!=4 || foe.CurrentHp!=500)
            throw new Exception("Sequence planning must leave the root and live board unchanged.");
        Console.WriteLine("PASS: four-energy attrition turns deploy profitable sequences with native Wither costs and no additional future pollution tax.");

        // Cleaning is an alternative to racing when the hand itself is lethal.
        p.Creature.SetCurrentHpInternal(15);
        aura.DynamicVars["CardsLeft"].BaseValue=6;
        foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine.States["INCREASING_INTENSITY_MOVE"],true);
        for(var i=0;i<2;i++)
        {
            var wither=combat.CreateCard<Wither>(p);wither.FakeUpgrade();wither.FakeUpgrade();
            p.PlayerCombatState.Hand.AddInternal(wither);
        }
        p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<SecondWind>(p));
        var cleanRoot=KernelSession.Capture(combat);
        var clean=KernelRollout.Run(cleanRoot.Fork(),[p],[p],new(RolloutPolicyKind.Tactical,MaxSteps:1));
        if(clean.Actions.FirstOrDefault()?.Card is not SecondWind)
            throw new Exception("A lethal Wither hand must retain the exhaust rescue alongside damage race candidates.");
        var cleaned=cleanRoot.Fork();
        if(!cleaned.ReplayPlanned(clean.Actions[0],1,out _) || cleaned.Exhaust(p).Count(c=>c is Wither)!=2
            || cleaned.Hand(p).Any(c=>c is Wither) || cleaned.Threat(p).HandDamage!=0)
            throw new Exception("The selected Second Wind must actually exhaust both Withers and remove their hand damage.");
        Console.WriteLine("PASS: native exhaust rescues a lethal Wither hand and removes its cards from future circulation.");
    }
}

using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using System.Reflection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;

internal static class DecisionQualityScenarios
{
    // R2 2026-09-28: 13 independent production faults exited 1; bytes restored.
    // Commands and raw logs: work/decision-feedback-20260928-233427/r2-results.json.
    // Defend must spend two energy on attacks once incoming damage is fully covered.
    // Needed block must remain preferred by the defensive policy.
    // Temporary-buff follow-ups must share the one remaining energy.
    // Uncontrolled Anticipate must still precede useful block.
    // A controlled caster may still buff an unrestricted teammate's block.
    // Barricade must retain the value of block saved for future turns.
    // Kill must apply Strength to area attacks as well as targeted Strikes.
    // Defend must apply Strength to area attacks as well as targeted Strikes.
    // Growth must apply Strength to area attacks as well as targeted Strikes.
    // Cleanup must apply Strength to area attacks as well as targeted Strikes.
    // Tournament pruning must compare twenty-one area damage against ten single-target damage.
    // Kill must not consume its only legal card play on temporary Dexterity with no follow-up.
    // Defend must not consume its only legal card play on temporary Dexterity with no follow-up.
    // Growth must not consume its only legal card play on temporary Dexterity with no follow-up.
    // Tactical must not consume its only legal card play on temporary Dexterity with no follow-up.
    // Cleanup must not consume its only legal card play on temporary Dexterity with no follow-up.
    // One-play Sloth must leave no temporary-buff follow-up.
    // Permanent dual-stat oil must be proactively usable with two payable improvements.
    // Full-belt potion selection must emit Fysh Oil before another attack without requiring a death forecast.
    // The live planner must interrupt its cached card line to use full-belt Fysh Oil.
    // Oil must not invent improvements when no follow-up energy remains.
    // Fortifier must proactively prevent all twelve remaining damage from six existing block.
    // Fortifier must retain the bottle when its extra block prevents no damage.
    // Fortifier must not duplicate the protection of an already planned Whistle.
    // The reported Fysh Oil/Fortifier belt must make room for Ghost in a Jar at five HP.
    // The native reward pipeline must acquire Ghost in a Jar from the reported full belt.
    private static bool Format(ref string __result) { __result = ""; return false; }
    internal static void Run()
    {
        new Harmony("coopbots.test.decision-quality").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(DecisionQualityScenarios), nameof(Format)));
        var failures = new List<string>();
        void Check(bool ok, string why) { if (!ok) failures.Add(why); }
        foreach (var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var (party, combat, enemy) = Board(); var p = party[0];
            p.Creature.GainBlockInternal(20); p.PlayerCombatState!.LoseEnergy(1);
            Add<StrikeSilent>(combat,p); Add<StrikeSilent>(combat,p);
            Add<DefendSilent>(combat,p); Add<DefendSilent>(combat,p);
            var root = KernelSession.Capture(combat);
            var line = KernelRollout.Run(root.Fork(),party,party,new(policy,MaxSteps:2));
            Console.WriteLine($"PROBE overblock {policy}: {string.Join(',',line.Actions.Select(a=>a.Card?.Id.Entry))}");
            Check(line.Actions.Count(a=>a.Card is StrikeSilent)==2,$"{policy} must spend two energy on attacks once incoming damage is fully covered.");

            (party,combat,enemy) = Board(); p=party[0]; p.PlayerCombatState!.LoseEnergy(1);
            Add<StrikeSilent>(combat,p); Add<StrikeSilent>(combat,p); Add<FlickFlack>(combat,p);
            root=KernelSession.Capture(combat);
            line=KernelRollout.Run(root.Fork(),party,party,new(policy,MaxSteps:2));
            Console.WriteLine($"PROBE flick {policy}: {string.Join(',',line.Actions.Select(a=>a.Card?.Id.Entry))}");
            Check(line.Actions.Any(a=>a.Card is FlickFlack),$"{policy} must include seven-damage Flick Flack instead of two six-damage Strikes.");
            ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(p.Creature,3,true);
            line=KernelRollout.Run(KernelSession.Capture(combat),party,party,new(policy,MaxSteps:2));
            Console.WriteLine($"PROBE strengthened flick {policy}: {string.Join(',',line.Actions.Select(a=>a.Card?.Id.Entry))}");
            Check(line.Actions.Any(a=>a.Card is FlickFlack),$"{policy} must apply Strength to area attacks as well as targeted Strikes.");

            (party,combat,enemy)=Board(); p=party[0];
            Add<Anticipate>(combat,p); Add<DefendSilent>(combat,p); Add<DefendSilent>(combat,p); Add<StrikeSilent>(combat,p);
            ModelDb.Power<RingingPower>().ToMutable().ApplyInternal(p.Creature,1,true);
            foreach(var c in p.PlayerCombatState!.Hand.Cards) c.AfflictInternal(ModelDb.Affliction<Ringing>().ToMutable(),1m);
            root=KernelSession.Capture(combat);
            line=KernelRollout.Run(root.Fork(),party,party,new(policy,MaxSteps:1));
            Console.WriteLine($"PROBE ringing {policy}: {line.Actions.FirstOrDefault()?.Card?.Id.Entry}");
            Check(line.Actions.FirstOrDefault()?.Card is not Anticipate,$"{policy} must not consume its only legal card play on temporary Dexterity with no follow-up.");
        }
        var (stock,stockCombat,_) = Board(); var owner=stock[0];
        for(var i=0;i<5;i++)
        {
            owner.Deck.AddInternal(stockCombat.RunState.CreateCard<StrikeSilent>(owner));
            owner.Deck.AddInternal(stockCombat.RunState.CreateCard<DefendSilent>(owner));
        }
        var oil=ModelDb.Potion<FyshOil>().ToMutable(); owner.AddPotionInternal(oil,silent:true);
        var fort=ModelDb.Potion<Fortifier>().ToMutable(); owner.AddPotionInternal(fort,silent:true);
        while(owner.HasOpenPotionSlots) owner.AddPotionInternal(ModelDb.Potion<FairyInABottle>().ToMutable(),silent:true);
        owner.Creature.SetCurrentHpInternal(5);
        Check(BotPotionInventory.Replacement(owner,ModelDb.Potion<GhostInAJar>().ToMutable()) is FyshOil or Fortifier,
            "The reported Fysh Oil/Fortifier belt must make room for Ghost in a Jar at five HP.");
        Add<StrikeSilent>(stockCombat,owner); Add<StrikeSilent>(stockCombat,owner);
        var opportunity=KernelPotionOpportunity.Measure(KernelSession.Capture(stockCombat),oil,owner.Creature,stock);
        Console.WriteLine($"PROBE fysh {opportunity}");
        Check(opportunity.FullValue,"Permanent dual-stat oil must be proactively usable with two payable improvements.");
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(stockCombat),stock,stock)?.Action.Potion==oil,
            "Full-belt potion selection must emit Fysh Oil before another attack without requiring a death forecast.");
        var planner=new KernelCombatPlanner();
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var planned=new KernelTeamSearch.Action(owner,owner.PlayerCombatState!.Hand.Cards.First(),stockCombat.Enemies[0]);
        typeof(KernelCombatPlanner).GetField("tournamentScript",flags)!.SetValue(planner,new[]{planned,planned});
        typeof(KernelCombatPlanner).GetField("tournamentScriptCombat",flags)!.SetValue(planner,stockCombat);
        var arguments=new object?[]{stockCombat,stock,true,"decision-quality",null};
        typeof(KernelCombatPlanner).GetMethod("TryTournamentDecision",flags)!.Invoke(planner,arguments);
        Check(planner.ConfirmedPotion?.Potion==oil && arguments[4] is null,
            "The live planner must interrupt its cached card line to use full-belt Fysh Oil.");
        owner.PlayerCombatState.LoseEnergy(3);
        Check(!KernelPotionOpportunity.Measure(KernelSession.Capture(stockCombat),oil,owner.Creature,stock).FullValue,
            "Oil must not invent improvements when no follow-up energy remains.");
        var run=stockCombat.RunState;
        run.AppendToMapPointHistory(MegaCrit.Sts2.Core.Map.MapPointType.Unknown,MegaCrit.Sts2.Core.Rooms.RoomType.Event,
            ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.DrowningBeacon>().Id);
        var ghost=ModelDb.Potion<GhostInAJar>().ToMutable();
        var net=DispatchProxy.Create<INetHostGameService,ReconnectNetProxy>();
        using(var sync=new RewardsSetSynchronizer(new RunLocationTargetedMessageBuffer(net),net,run,1))
        {
            var reward=new PotionReward(ghost,owner);
            var set=new RewardsSet(owner,sync).WithCustomRewards([reward]);
            typeof(RewardsSet).GetField("_isGenerated",flags)!.SetValue(set,true);
            BotRewardDriver.Offer(set).GetAwaiter().GetResult();
            Check(reward.SuccessfullySelected && owner.Potions.Contains(ghost),
                "The native reward pipeline must acquire Ghost in a Jar from the reported full belt.");
        }
        var (fortParty,fortCombat,fortEnemy)=Board(); var fortOwner=fortParty[0];
        var fortifier=ModelDb.Potion<Fortifier>().ToMutable(); fortOwner.AddPotionInternal(fortifier,silent:true);
        fortEnemy.GetPower<StrengthPower>()!.SetAmount(17, silent:true);
        fortOwner.Creature.GainBlockInternal(6);
        var fortValue=KernelPotionOpportunity.Measure(KernelSession.Capture(fortCombat),fortifier,fortOwner.Creature,fortParty);
        Check(fortValue.FullValue && fortValue.Benefit==12,"Fortifier must proactively prevent all twelve remaining damage from six existing block.");
        while(fortOwner.HasOpenPotionSlots) fortOwner.AddPotionInternal(ModelDb.Potion<FairyInABottle>().ToMutable(),silent:true);
        var whistle=Add<Whistle>(fortCombat,fortOwner);
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(fortCombat),fortParty,fortParty,
            plan:[new(fortOwner,whistle,fortEnemy)]) is null,
            "Fortifier must not duplicate the protection of an already planned Whistle.");
        fortOwner.Creature.GainBlockInternal(20);
        Check(!KernelPotionOpportunity.Measure(KernelSession.Capture(fortCombat),fortifier,fortOwner.Creature,fortParty).FullValue,
            "Fortifier must retain the bottle when its extra block prevents no damage.");

        var (defParty,defCombat,_)=Board(); var defender=defParty[0];
        Add<DefendSilent>(defCombat,defender); Add<StrikeSilent>(defCombat,defender);
        var defense=KernelRollout.Run(KernelSession.Capture(defCombat),defParty,defParty,new(RolloutPolicyKind.Defend,MaxSteps:1));
        Check(defense.Actions.FirstOrDefault()?.Card is DefendSilent,"Needed block must remain preferred by the defensive policy.");
        defender.Creature.GainBlockInternal(20);
        ModelDb.Power<BarricadePower>().ToMutable().ApplyInternal(defender.Creature,1,true);
        defense=KernelRollout.Run(KernelSession.Capture(defCombat),defParty,defParty,new(RolloutPolicyKind.Defend,MaxSteps:1));
        Check(defense.Actions.FirstOrDefault()?.Card is DefendSilent,"Barricade must retain the value of block saved for future turns.");

        foreach(var limit in new[]{false,true})
        {
            var (party,combat,_)=Board(); var p=party[0];
            var anticipate=Add<Anticipate>(combat,p); Add<DefendSilent>(combat,p); Add<DefendSilent>(combat,p); Add<DefendSilent>(combat,p);
            p.PlayerCombatState!.LoseEnergy(2);
            if(limit) ModelDb.Power<SlothPower>().ToMutable().ApplyInternal(p.Creature,1,true);
            var count=RolloutRun.RelevantRemaining(KernelSession.Capture(combat),party,p,anticipate,null);
            Check(count==(limit?0:1),limit?"One-play Sloth must leave no temporary-buff follow-up.":"Temporary-buff follow-ups must share the one remaining energy.");
            if(!limit)
            {
                var line=KernelRollout.Run(KernelSession.Capture(combat),party,party,new(RolloutPolicyKind.Kill,MaxSteps:1));
                Check(line.Actions.FirstOrDefault()?.Card is Anticipate,"Uncontrolled Anticipate must still precede useful block.");
            }
        }
        var (allies,alliedCombat,_)=Board(); var caster=allies[0];
        var fade=Add<Fade>(alliedCombat,caster); Add<DefendSilent>(alliedCombat,allies[1]);
        ModelDb.Power<SlothPower>().ToMutable().ApplyInternal(caster.Creature,1,true);
        Check(RolloutRun.RelevantRemaining(KernelSession.Capture(alliedCombat),allies,caster,fade,allies[1].Creature)==1,
            "A controlled caster may still buff an unrestricted teammate's block.");
        var (areaParty,areaCombat,_)=Board();
        Add<Dash>(areaCombat,areaParty[0]); Add<FlickFlack>(areaCombat,areaParty[0]);
        for(var i=0;i<2;i++)
        {
            var e=areaCombat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,$"extra-{i}");
            areaCombat.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(500); e.SetCurrentHpInternal(500);
            e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);
        }
        Check(KernelTournament.FirstActions(KernelSession.Capture(areaCombat),areaParty,1,
            new(IncludePotions:false,IncludeEndTurns:false)).FirstOrDefault()?.Card is FlickFlack,
            "Tournament pruning must compare twenty-one area damage against ten single-target damage.");
        if(failures.Count>0) throw new Exception(string.Join('\n',failures));
        Console.WriteLine("PASS: decision quality covers surplus block, stronger attacks, one-play control and full potion belts.");
    }
    private static T Add<T>(CombatState combat,Player p) where T:CardModel,new()
    { var c=combat.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(c); return c; }
    private static (Player[],CombatState,Creature) Board()
    {
        var party=Enumerable.Range(0,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro,39,i))).ToArray();
        var run=RunState.CreateForTest(party,seed:"DECISION-QUALITY");
        var combat=new CombatState(runState:run);
        foreach(var p in party)
        { p.ResetCombatState(); combat.AddPlayer(p); p.MaxEnergy=3; p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
          p.PlayerCombatState!.Phase=PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3); }
        var enemy=combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"enemy");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(500); enemy.SetCurrentHpInternal(500);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"],true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(enemy,9,true);
        return(party,combat,enemy);
    }
}

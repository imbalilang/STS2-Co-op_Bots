using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// R2 2026-10-03: production faults each failed with exit 1, then restored.
// Commands, mutations and raw logs: work/combat-quality-20261003/r2.py / r2-*.log.
// phase: System.Exception: Combat quality: reviving boss must retain its future HP and enemy count
// discard: System.Exception: Combat quality: exhaust must realize payable defence before burning the hand
// investment: System.Exception: Combat quality: a mid-turn drawn power must receive a fresh investment comparison
// manual-hand: System.Exception: Combat quality: manual play cannot use cards already removed from hand
// potion-replay: System.Exception: Combat quality: bottle value must be measured by reusing the completed card reference
// emergency-tail: System.Exception: Combat quality: an emergency modeled bottle must preserve its native continuation
// serial-budget: System.Exception: Combat quality: 1 lanes must interrupt an in-flight continuation at budget
// parallel-budget: System.Exception: Combat quality: 2 lanes must interrupt an in-flight continuation at budget
// reference-retention: System.Exception: Combat quality: deadline must retain the completed potion reference
// resource-refill: System.Exception: Combat quality: spent energy followed by a smaller refill must reopen the power comparison
internal static class CombatQualityScenarios
{
    static bool Format(ref string __result) { __result = ""; return false; }
    static void Check(bool ok,string why) { if(!ok)throw new Exception("Combat quality: "+why); }
    internal static void Run()
    {
        TestEnvironment.Ensure();
        new Harmony("coopbots.wipe-review").Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix:new HarmonyMethod(typeof(CombatQualityScenarios),nameof(Format)));
        if(Environment.GetEnvironmentVariable("COOPBOTS_QUALITY_PERF")=="1") { LateFightBudget(); return; }
        PhaseProgress();
        InvestmentRefresh();
        ResourceRefresh();
        PotionReuse();
        DeadlineChecks();
        ExhaustOpportunity();
        var p=Player.CreateForNewRun<Deprived>(UnlockState.all,1);
        var party=new[]{p};var c=new CombatState(runState:RunState.CreateForTest(party,seed:"HIDDEN-COST"));
        p.ResetCombatState();c.AddPlayer(p);p.MaxEnergy=3;p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;
        p.PlayerCombatState.GainEnergy(2);p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(41);
        var e=c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"0");
        c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(1017);e.SetCurrentHpInternal(1017);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e,8,true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(p.Creature,1,true);
        ModelDb.Power<DexterityPower>().ToMutable().ApplyInternal(p.Creature,2,true);
        ModelDb.Power<EnvenomPower>().ToMutable().ApplyInternal(p.Creature,1,true);
        CardModel Add(CardModel card) {var a=c.CreateCard(card,p);p.PlayerCombatState.Hand.AddInternal(a);return a;}
        var hidden=Add(ModelDb.Card<HiddenDaggers>());var defend=Add(ModelDb.Card<DefendSilent>());defend.UpgradeInternal();var dance=Add(ModelDb.Card<BladeDance>());
        var root=KernelSession.Capture(c);
        var discarded=root.CardBranches(hidden,null,1).Single(b=>b.Boundary.Length==0).State;
        Check(!discarded.CanPlay(defend) && !discarded.CanPlay(dance),"manual play cannot use cards already removed from hand");
        foreach(var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var line=KernelRollout.Run(root.Fork(),party,party,new(policy,MaxRounds:1));
            var replay=root.Fork();foreach(var a in line.Actions)if(!replay.ReplayPlanned(a,1,out var why))throw new Exception(why);
            Console.WriteLine($"DISCARD {policy}: {string.Join(',',line.Actions.Select(a=>a.Card?.Id.Entry??"END"))} enemyHP={replay.Hp(e)} ownerHP={replay.Hp(p.Creature)}");
            Check(replay.Hp(e)==987 && replay.Hp(p.Creature)==41, $"{policy} must preserve the paid block and all five Shivs before discarding");
        }
        var tournament=KernelTournament.Run(root.Fork(),party,party,new(IncludePotions:false,ReserveMs:0,Rollout:new(MaxRounds:1)),2000);
        var selected=root.Fork();
        foreach(var action in tournament.Script!) Check(selected.ReplayPlanned(action,1,out _),"selected discard script must replay");
        Check(selected.Hp(e)==987 && selected.Hp(p.Creature)==41,"production tournament must retain all five Shivs and paid block");
        var better=root.Fork();
        void Play(CardModel card) { better=better.CardBranches(card,better.Targets(card).First(),1).First(b=>b.Boundary.Length==0).State; }
        Play(defend);Play(dance);
        foreach(var shiv in better.Hand(p).OfType<Shiv>().ToArray())Play(shiv);
        Play(hidden);foreach(var shiv in better.Hand(p).OfType<Shiv>().ToArray())Play(shiv);
        better.EndTurn(p,1,out _);
        Console.WriteLine($"DISCARD legal alternative: DEFEND+,BLADE_DANCE,SHIVx3,HIDDEN_DAGGERS,SHIVx2,END enemyHP={better.Hp(e)} ownerHP={better.Hp(p.Creature)}");
        LateFightBudget();
        Console.WriteLine("PASS: phase progress, mid-turn powers, discarded opportunities and potion references reach native replayed outcomes.");
    }
    static void PhaseProgress()
    {
        var p=Player.CreateForNewRun<Deprived>(UnlockState.all,31);
        var c=new CombatState(runState:RunState.CreateForTest(new[]{p},seed:"PHASE-PROGRESS"));
        p.ResetCombatState();c.AddPlayer(p);p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);
        var e=c.CreateCreature(ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.TestSubject>().ToMutable(),CombatSide.Enemy,"0");
        c.AddCreature(e);e.Monster!.SetUpForCombat();
        ModelDb.Power<AdaptablePower>().ToMutable().ApplyInternal(e,1,true);
        AccessTools.Field(e.Monster.GetType(),"_respawns").SetValue(e.Monster,1);
        e.SetCurrentHpInternal(3);
        var root=KernelSession.Capture(c);
        var phase2=TerminalRecord.Capture(root,new[]{p},false);
        var strike=c.CreateCard<StrikeIronclad>(p);p.PlayerCombatState.Hand.AddInternal(strike);
        root=KernelSession.Capture(c);
        var killed=root.CardBranches(strike,e,1).Single(b=>b.Boundary.Length==0).State;
        var gap=TerminalRecord.Capture(killed,new[]{p},false);
        Check(!killed.HasWon && gap.RemainingEnemies==1 && gap.RemainingEnemyHp>0,"reviving boss must retain its future HP and enemy count");
        Check(killed.EndTurn(p,80,out _),"phase transition must execute natively");
        var revived=TerminalRecord.Capture(killed,new[]{p},false);
        Check(phase2.RemainingEnemyHp>revived.RemainingEnemyHp && gap.RemainingEnemyHp==revived.RemainingEnemyHp,
            "remaining HP must decrease at a form kill and remain continuous through respawn");
        Check(TerminalComparer.Compare(revived,phase2)>0,"terminal comparison must prefer reaching the later form");
        Console.WriteLine($"PHASE PROGRESS before={phase2.RemainingEnemyHp} gap={gap.RemainingEnemyHp} revived={revived.RemainingEnemyHp}");
    }

    static void InvestmentRefresh()
    {
        foreach(var draw in new CardModel[]{ModelDb.Card<Offering>(),ModelDb.Card<BattleTrance>()})
        {
            var p=Player.CreateForNewRun<Deprived>(UnlockState.all,41);
            var c=new CombatState(runState:RunState.CreateForTest(new[]{p},seed:"INVESTMENT-REFRESH"));
            p.ResetCombatState();c.AddPlayer(p);p.MaxEnergy=3;p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);
            p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);
            var e=c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"0");c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(400);e.SetCurrentHpInternal(400);
            e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);
            foreach(var card in new CardModel[]{ModelDb.Card<Accuracy>(),draw,ModelDb.Card<Shiv>(),ModelDb.Card<Shiv>(),ModelDb.Card<Shiv>(),ModelDb.Card<Shiv>()})
                p.PlayerCombatState.Hand.AddInternal(c.CreateCard(card,p));
            foreach(var card in new CardModel[]{ModelDb.Card<Panache>(),ModelDb.Card<Shiv>(),ModelDb.Card<Shiv>()})
                p.PlayerCombatState.DrawPile.AddInternal(c.CreateCard(card,p));
            var root=KernelSession.Capture(c);
            var run=new RolloutRun(root.Fork(),new[]{p},new[]{p},new(RolloutPolicyKind.Tactical,MaxRounds:1));
            while(!run.Done)run.Step();
            Console.WriteLine($"REFRESH {draw.Id.Entry} probes={run.InvestmentProbes} cards="+string.Join(',',run.Result!.Actions.Select(a=>a.Card?.Id.Entry??"END")));
            Check(run.InvestmentProbes>=2,"a mid-turn drawn power must receive a fresh investment comparison");
            var cards=run.Result.Actions.Select(a=>a.Card).ToArray();var pan=Array.FindIndex(cards,a=>a is Panache);
            Check(pan>=0 && cards.Skip(pan+1).Count(a=>a is Shiv)>=5,"drawn Panache must start before its remaining five-card chain");
        }
    }

    // Cards/upgrades from WMQZBCD4582E, floor 45. Controlled opening hands;
    // no claim to reproduce the unrecorded pile order, relics or RNG of the live save.
    static void LateFightBudget()
    {
        string[] decks = ["STRIKE_IRONCLAD+,STRIKE_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD,BASH+,ASCENDERS_BANE,STRIKE_IRONCLAD,DEFEND_IRONCLAD,SHRUG_IT_OFF,SHRUG_IT_OFF,POMMEL_STRIKE+,WHIRLWIND+,INFLAME+,STOKE,WHIRLWIND+,REGRET,REGRET","STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,BASH+,ASCENDERS_BANE,GREED,THUNDERCLAP+,SETUP_STRIKE+,POMMEL_STRIKE,BULLY,TAUNT+,OFFERING+,BATTLE_TRANCE+,INFLAME+,TWIN_STRIKE,FIEND_FIRE+,CRUELTY,CLUMSY,HEMOKINESIS","STRIKE_SILENT,STRIKE_SILENT,STRIKE_SILENT,DEFEND_SILENT,NEUTRALIZE,ASCENDERS_BANE,GREED,DAGGER_SPRAY+,BLADE_DANCE,EXPOSE,BLADE_DANCE,BLADE_DANCE,ASSASSINATE+,HIDDEN_DAGGERS,DEFEND_SILENT+,PREPARED+,BLADE_DANCE+,PIERCING_WAIL,FINISHER,STRIKE_SILENT+,BACKFLIP,DEFEND_SILENT+,ENVENOM,PREPARED,BACKFLIP,REGRET","STRIKE_SILENT,DEFEND_SILENT+,DEFEND_SILENT,NEUTRALIZE,SURVIVOR+,ASCENDERS_BANE,BACKSTAB+,HIDDEN_DAGGERS,FLICK_FLACK+,SUCKER_PUNCH,BLADE_OF_INK+,ESCAPE_PLAN,PREPARED+,ACCURACY,PIERCING_WAIL+,PREPARED,SNEAKY+,ESCAPE_PLAN,PREPARED,DECAY,CONCOCT"];
        string[] hands = ["INFLAME+,POMMEL_STRIKE+,WHIRLWIND+,SHRUG_IT_OFF,DEFEND_IRONCLAD",
            "OFFERING+,BATTLE_TRANCE+,INFLAME+,FIEND_FIRE+,CRUELTY",
            "HIDDEN_DAGGERS,DEFEND_SILENT+,BLADE_DANCE,PREPARED+,ENVENOM",
            "ACCURACY,PREPARED+,ESCAPE_PLAN,HIDDEN_DAGGERS,SNEAKY+"];
        var party=Enumerable.Range(0,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,(ulong)(71+i))).ToArray();
        var c=new CombatState(encounter:ModelDb.Encounter<MegaCrit.Sts2.Core.Models.Encounters.TestSubjectBoss>().ToMutable(),runState:RunState.CreateForTest(party,ascensionLevel:10,seed:"WMQZBCD4582E-QUALITY"));
        for(var i=0;i<4;i++)
        {
            var p=party[i];p.ResetCombatState();c.AddPlayer(p);p.MaxEnergy=3;
            p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);
            p.Creature.SetMaxHpInternal(110);p.Creature.SetCurrentHpInternal(new[]{85,76,105,105}[i]);
            var remaining=decks[i].Split(',').ToList();
            void Add(string id,bool held)
            {
                var card=c.CreateCard(ModelDb.AllCards.First(m=>m.Id.Entry==id.TrimEnd('+')),p);
                if(id.EndsWith('+'))card.UpgradeInternal();
                (held?p.PlayerCombatState.Hand:p.PlayerCombatState.DrawPile).AddInternal(card);
            }
            foreach(var id in hands[i].Split(',')) { Add(id,true);remaining.Remove(id); }
            foreach(var id in remaining)Add(id,false);
            p.AddPotionInternal(ModelDb.Potion<MegaCrit.Sts2.Core.Models.Potions.FirePotion>().ToMutable(),silent:true);
        }
        var e=c.CreateCreature(ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.TestSubject>().ToMutable(),CombatSide.Enemy,"0");
        c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(500);e.SetCurrentHpInternal(500);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["BITE_MOVE"],true);
        ModelDb.Power<AdaptablePower>().ToMutable().ApplyInternal(e,1,true);
        ModelDb.Power<EnragePower>().ToMutable().ApplyInternal(e,3,true);
        var root=KernelSession.Capture(c);
        var diagnostic=new RolloutRun(root.Fork(),party,party,new(RolloutPolicyKind.Kill));
        while(!diagnostic.Done) diagnostic.Step();
        Console.WriteLine($"LATE NATIVE stop={diagnostic.Result!.StopReason} detail={diagnostic.State.LastRoundFailure}");
        for(var trial=0;trial<2;trial++)
        {
            var started=GC.GetTotalAllocatedBytes(true);var pause=GC.GetTotalPauseDuration();
            var clock=System.Diagnostics.Stopwatch.StartNew();
            var tournament=new TournamentRun(root.Fork(),party,party,
                new(IncludePotions:true,ReserveMs:0,EnableJointSearch:true),3200);
            while(!tournament.Advance(TimeSpan.FromMilliseconds(5))) { }
            var result=tournament.Result!;
            Console.WriteLine($"LATE BUDGET trial={trial} wall={clock.ElapsedMilliseconds} compute={tournament.ComputeMs:F0} alloc={(GC.GetTotalAllocatedBytes(true)-started)/1048576.0:F1} gc={(GC.GetTotalPauseDuration()-pause).TotalMilliseconds:F0} rollouts={result.Rollouts} verified={result.Considered.Count(x=>x.Verified)} {result.JointDiagnostics}");
            Check(result.Record is {Verified:true},"late-game budget must preserve a completed reference");
            Check(!result.JointDiagnostics.Contains("reference=continuation-budget"),"late-game joint reference must complete");
            Check(result.Considered.Count(x=>x.Verified)>=2 && (tournament.EvaluatedOpenings>=2 || result.JointPrefixes>=2),"late-game budget must compare different openings");
            Check(clock.ElapsedMilliseconds<6000,"late-game search must not overrun the production budget by a whole rollout");
            var replay=root.Fork();foreach(var action in result.Script!)Check(replay.ReplayPlanned(action,80,out _),"late-game chosen script must replay");
            Check(replay.HasTerminal && replay.HasWon==result.Record.Victory,"late-game recorded ending must match replay");
        }
    }

    static void PotionReuse()
    {
        var p=Player.CreateForNewRun<Deprived>(UnlockState.all,92);
        var c=new CombatState(runState:RunState.CreateForTest(new[]{p},seed:"POTION-REUSE"));
        p.ResetCombatState();c.AddPlayer(p);p.MaxEnergy=3;p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);
        p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(9);
        var e=c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"0");c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(300);e.SetCurrentHpInternal(300);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e,5,true);
        for(var i=0;i<3;i++)p.PlayerCombatState.Hand.AddInternal(c.CreateCard<StrikeIronclad>(p));
        var strength=ModelDb.Potion<MegaCrit.Sts2.Core.Models.Potions.StrengthPotion>().ToMutable();p.AddPotionInternal(strength,silent:true);
        var root=KernelSession.Capture(c);
        var baseline=KernelTournament.Run(root.Fork(),new[]{p},new[]{p},new(IncludePotions:false,ReserveMs:0),2000);
        Check(baseline.Record is {Verified:true,Victory:false},"potion reference fixture must reach a real loss");
        var trial=new KernelPotionContinuationRun(root,new[]{p},new[]{p},baseline,1000);
        while(!trial.Advance(TimeSpan.FromMilliseconds(2))) { }
        Check(trial.Completed>0 && trial.Result.FirstAction?.Potion==strength,"bottle value must be measured by reusing the completed card reference");
        var replay=root.Fork();foreach(var action in trial.Result.Script!)Check(replay.ReplayPlanned(action,80,out _),"reused potion continuation must replay");
        Check(replay.HasTerminal && trial.Result.Script!.Count==trial.Result.ScriptAliveStates!.Count,"reused potion plan must retain terminal and action-aligned evidence");
        var seeded=KernelTournament.Run(root.Fork(),new[]{p},new[]{p},new(IncludePotions:true,PotionsOnly:true,ReserveMs:0){ReferenceResult=trial.Result},1);
        Check(seeded.Record is {Verified:true} && seeded.Script is {Count:>1},"deadline must retain the completed potion reference");
        var planner=new CoopBots.KernelCombatPlanner();
        AccessTools.Method(typeof(CoopBots.KernelCombatPlanner),"AdoptTournamentScript").Invoke(planner,new object[]{trial.Result,c});
        Check(planner.HasPendingStep,"an emergency modeled bottle must preserve its native continuation");
        var offered=(bool)AccessTools.Method(typeof(CoopBots.KernelCombatPlanner),"TryPotionOpportunity")
            .Invoke(planner,new object[]{c,new[]{p},trial.Result.Record!,trial.Result.Script!.Skip(1).ToArray()})!;
        Check(!offered && planner.HasPendingStep,"live opportunity dispatch must not discard a modeled potion continuation");
        Console.WriteLine($"POTION REUSE completed={trial.Completed} compute={trial.ComputeMs:F1} steps={trial.Result.Script.Count} remaining={baseline.Record!.RemainingEnemyHp}->{trial.Result.Record!.RemainingEnemyHp}");
    }

    static void DeadlineChecks()
    {
        var p=Player.CreateForNewRun<Deprived>(UnlockState.all,99);
        var c=new CombatState(runState:RunState.CreateForTest(new[]{p},seed:"ROLLOUT-DEADLINE"));
        p.ResetCombatState();c.AddPlayer(p);p.MaxEnergy=3;p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);
        p.Creature.SetMaxHpInternal(100000);p.Creature.SetCurrentHpInternal(100000);
        var e=c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"0");c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(100000);e.SetCurrentHpInternal(100000);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);
        for(var i=0;i<5;i++)p.PlayerCombatState.Hand.AddInternal(c.CreateCard<StrikeIronclad>(p));
        var root=KernelSession.Capture(c);
        foreach(var dop in new[]{1,2})
        {
            var result=KernelTournament.Run(root.Fork(),new[]{p},new[]{p},new(IncludePotions:false,ReserveMs:0,MaxDegreeOfParallelism:dop),1);
            Check(result.Record?.CutoffReason=="continuation-budget",$"{dop} lanes must interrupt an in-flight continuation at budget");
        }
        Console.WriteLine("PASS: serial and parallel continuation deadlines interrupt unfinished work.");
    }

    static void ExhaustOpportunity()
    {
        foreach(var harmful in new[]{false,true})
        foreach(var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var p=Player.CreateForNewRun<Deprived>(UnlockState.all,111);
            var c=new CombatState(runState:RunState.CreateForTest(new[]{p},seed:"EXHAUST-OPPORTUNITY"));
            p.ResetCombatState();c.AddPlayer(p);p.MaxEnergy=3;p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);
            p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(41);
            ModelDb.Power<DexterityPower>().ToMutable().ApplyInternal(p.Creature,2,true);
            var e=c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"0");c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(400);e.SetCurrentHpInternal(400);
            e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);
            ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e,8,true);
            p.PlayerCombatState.Hand.AddInternal(c.CreateCard<FiendFire>(p));
            var defend=c.CreateCard<DefendIronclad>(p);defend.UpgradeInternal();p.PlayerCombatState.Hand.AddInternal(defend);
            p.PlayerCombatState.Hand.AddInternal(c.CreateCard(harmful?ModelDb.Card<Regret>():ModelDb.Card<Shiv>(),p));
            var root=KernelSession.Capture(c);var run=new RolloutRun(root.Fork(),new[]{p},new[]{p},new(policy,MaxRounds:1));while(!run.Done)run.Step();
            Console.WriteLine($"EXHAUST {policy} harmful={harmful} hp={run.State.Hp(p.Creature)} enemy={run.State.Hp(e)} cards="+string.Join(',',run.Result!.Actions.Select(a=>a.Card?.Id.Entry??"END")));
            Check(run.Result.Actions.FirstOrDefault()?.Card==defend && run.State.Hp(p.Creature)==41,
                "exhaust must realize payable defence before burning the hand");
            Check(run.State.Hp(e)==393,"exhaust should still consume the lower-value attack or harmful curse for native damage");
        }
        Console.WriteLine("PASS: native exhaust opportunity cost preserves useful defence and still burns suitable fodder.");
    }

    static void ResourceRefresh()
    {
 var p=Player.CreateForNewRun<Deprived>(UnlockState.all,99);
 var c=new CombatState(runState:RunState.CreateForTest(new[]{p},seed:"RESOURCE-REFRESH"));p.ResetCombatState();c.AddPlayer(p);p.MaxEnergy=3;p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);
 var e=c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"0");c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(400);e.SetCurrentHpInternal(400);e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);
 foreach(var card in new CardModel[]{ModelDb.Card<Accuracy>(),ModelDb.Card<Bludgeon>(),ModelDb.Card<Bloodletting>(),ModelDb.Card<StrikeIronclad>()})p.PlayerCombatState.Hand.AddInternal(c.CreateCard(card,p));
 var root=KernelSession.Capture(c);var r=new RolloutRun(root.Fork(),new[]{p},new[]{p},new(RolloutPolicyKind.Tactical,MaxRounds:1));while(!r.Done)r.Step();
 Check(r.InvestmentProbes>=2,"spent energy followed by a smaller refill must reopen the power comparison");
 System.Console.WriteLine($"RESOURCE probes={r.InvestmentProbes} sequence="+string.Join(',',r.Result!.Actions.Select(a=>a.Card?.Id.Entry??"END")));
    }

}

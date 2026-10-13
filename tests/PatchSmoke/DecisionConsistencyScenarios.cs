using CoopBots;
using CoopBots.Building;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class DecisionConsistencyScenarios
{
    // R2 2026-10-04: deliberate source faults each exited 1; restored suite exited 0.
    // Commands and full logs: work/decision-consistency-20261004/r2-results.json.
    // robustness: System.Exception: Decision consistency: improved ordinary and stressed outcomes must not lose to relative robustness subtraction
    // autoplay-cost: System.Exception: Decision consistency: Cascade must spend X once and autoplay paid cards without forced exhaust
    // autoplay-upgrade: System.Exception: Decision consistency: upgraded Cascade must autoplay X plus one even at zero energy
    // autoplay-exhaust: System.Exception: Decision consistency: Havoc must exhaust its automatic target
    // autoplay-power: System.Exception: Decision consistency: automatic power startup must affect subsequent automatic attacks
    // midnight-hand: System.Exception: Decision consistency: same-hand exhaust must unlock Midnight at its discounted cost
    // draw-lock: System.Exception: Decision consistency: BattleTrance must prevent later cantrips from revealing more cards
    // allied-discount: System.Exception: Decision consistency: allied generated exhaust must lower permanent Midnight commitment
    // discount-entry: System.Exception: Decision consistency: draft entry must value allied exhaust payoffs without a fixed twelve-energy penalty
    // team-protection: System.Exception: Decision consistency: Tank must credit living allies' prevented HP loss and retain the owner's extra damage
    // forecast-choice: System.Exception: Decision consistency: live enemy choice and simulated curse must use the same policy
    // live-choice: System.Exception: Decision consistency: live enemy choice and simulated curse must use the same policy
    // curse-life: System.Exception: Decision consistency: curse choice must account for low remaining life
    // future-reference: System.Exception: Decision consistency: realized damage must not destroy the shared future Vulnerable dividend
    // future-entry: System.Exception: Decision consistency: end comparison must retain the fourth action in the free shiv generation chain
    // stale-victory: System.Exception: Decision consistency: stale predicted victory must lose its end-turn exemption at the live round
    // real-victory: System.Exception: Decision consistency: live replay must preserve and execute an actually winning suffix
    private static void Check(bool yes,string reason) { if(!yes)throw new Exception("Decision consistency: "+reason); }
    internal static void Run()
    {
        NightDecisionScenarios.Initialize();
        Building(); DuplicatePrior(); EnemyChoice(); Debuff(); Victory(); RelicExhaust(); Sequence();
    }
    internal static void RunVictory()
    {
        NightDecisionScenarios.Initialize(); Victory();
    }
    private static void DuplicatePrior()
    {
        var(run,ps)=Party();var p=ps[3];
        string[] ids=["ASCENDERS_BANE","DEFEND_SILENT","DEFEND_SILENT","DEFEND_SILENT","DEFEND_SILENT","DEFEND_SILENT",
            "NEUTRALIZE","STRIKE_SILENT","STRIKE_SILENT","STRIKE_SILENT","STRIKE_SILENT","STRIKE_SILENT","SURVIVOR",
            "SUCKER_PUNCH","ACROBATICS","CONCOCT","BLADE_DANCE","PREPARED"];
        foreach(var id in ids){var c=ModelDb.AllCards.First(c=>c.Id.Entry==id).ToMutable();c.Owner=p;p.Deck.AddInternal(c);}
        using var budget=ConstructionBudget.Use(ConstructionBudget.For(KernelCombatPlanner.TournamentPerf.High));
        var before=DeckStrength.Evaluate(p.Deck.Cards,p);
        var next=run.CreateCard<Prepared>(p);
        var after=DeckStrength.Evaluate(p.Deck.Cards.Append(next).ToArray(),p);
        var delta=BuildValue.Add(next,p);
        Console.WriteLine($"CONSISTENCY duplicate Prepared prior={after.Prior-before.Prior:F3} total={delta.Total:F3}");
        // R2: restore harmonic positive priors -> exit 1:
        // System.Exception: Decision consistency: duplicate filtering must not earn the first copy's positive community prior again
        Check(Math.Abs(after.Prior-before.Prior)<1e-7,
            "duplicate filtering must not earn the first copy's positive community prior again");
        // R2: add a ten-point acquisition bonus -> exit 1:
        // System.Exception: Decision consistency: a second Prepared with negative modeled return must not be rescued by its community prior
        // Commands: work/decision-consistency-20261004/duplicate-r2.json; restored exit 0.
        Check(delta.Total<0,"a second Prepared with negative modeled return must not be rescued by its community prior");
        Console.WriteLine("PASS: repeat acquisitions depend on marginal deck outcomes, not repeated positive community recommendations.");
    }
    private static (RunState Run,Player[] Players) Party(int count=4)
    {
        var ps=Enumerable.Range(0,count).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,(ulong)(870+i))).ToArray();
        var run=RunState.CreateForTest(ps,ascensionLevel:10,seed:"DECISION-CONSISTENCY");
        foreach(var p in ps){p.MaxEnergy=3;p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);}
        return(run,ps);
    }
    private static void Building()
    {
        var a=new DeckRobustness.Estimate(4.389820,-11.171813,-1.657988,-9.506894,.8,-.798972);
        var b=new DeckRobustness.Estimate(4.472979,-11.341150,.658321,-9.241242,.8,.490925);
        Check(b.Total>a.Total,"improved ordinary and stressed outcomes must not lose to relative robustness subtraction");
        var(run,ps)=Party(); var p=ps[0];
        CardModel C<T>()where T:CardModel=>run.CreateCard<T>(p);
        ConstructionHand.Slot S(CardModel c)=>new(c,CardProfile.Of(c),c.Type is CardType.Curse or CardType.Status);
        var cascade=C<Cascade>();var big=C<Bludgeon>();var defend=C<DefendIronclad>();
        var auto=ConstructionHand.Solve([S(cascade),S(big),S(defend)],1,new(2,20,100));
        Check(auto.Damage==32 && auto.Block==5 && auto.EnergySpent==2 && auto.Exhausted==0,
            "Cascade must spend X once and autoplay paid cards without forced exhaust");
        cascade.UpgradeInternal();
        var zero=ConstructionHand.Solve([S(cascade),S(big)],1,new(0,0,100));
        Check(zero.Damage==32,"upgraded Cascade must autoplay X plus one even at zero energy");
        var havoc=ConstructionHand.Solve([S(C<Havoc>()),S(big)],1,new(1,0,100));
        Check(havoc.Damage==32 && (havoc.Exhausted&2)!=0,"Havoc must exhaust its automatic target");
        var engine=ConstructionHand.Solve([S(cascade),S(C<Inflame>()),S(C<StrikeIronclad>())],1,new(1,0,100));
        Check(engine.Damage==8,"automatic power startup must affect subsequent automatic attacks");
        var automaticDeck=Enumerable.Range(0,9).Select(_=>C<Bludgeon>()).Prepend(C<Cascade>()).ToArray();
        Check(SustainedDeckValue.OutcomeEstimate(automaticDeck,1).Sum(t=>t.Damage)>0,
            "sustained construction must count automatic cards that cannot be paid normally");
        var midnight=C<Midnight>();
        var ready=ConstructionHand.Solve([S(C<Shiv>()),S(midnight)],2,new(3,0,100,ExhaustBefore:8));
        Check(ready.Damage==64 && ready.EnergySpent==3,"same-hand exhaust must unlock Midnight at its discounted cost");
        var late=ConstructionHand.Solve([S(C<BattleTrance>()),S(C<PommelStrike>()),S(C<Wound>()),S(C<Wound>()),S(big)],1,new(4,0,100));
        Check(late.Drawn==4 && (late.Played&16)==0,"BattleTrance must prevent later cantrips from revealing more cards");
        foreach(var q in ps)
        {
            for(var i=0;i<5;i++)q.Deck.AddInternal(run.CreateCard<StrikeIronclad>(q));
            for(var i=0;i<5;i++)q.Deck.AddInternal(run.CreateCard<DefendIronclad>(q));
        }
        var noSupply=ConditionalCosts.Load(midnight,p.Deck.Cards,3);
        var noSupplyValue=BuildValue.Add(midnight,p).Total;
        foreach(var q in ps.Skip(1))for(var i=0;i<4;i++)q.Deck.AddInternal(run.CreateCard<BladeDance>(q));
        Check(ConditionalCosts.Load(midnight,p.Deck.Cards,3)<noSupply,
            "allied generated exhaust must lower permanent Midnight commitment");
        var suppliedValue=BuildValue.Add(midnight,p).Total;
        Console.WriteLine($"CONSISTENCY MIDNIGHT noSupply={noSupplyValue:F3} supplied={suppliedValue:F3}");
        Check(suppliedValue>0 && suppliedValue>noSupplyValue+10,
            "draft entry must value allied exhaust payoffs without a fixed twelve-energy penalty");
        var tank=C<Tank>();
        using var budget=ConstructionBudget.Use(ConstructionBudget.For(KernelCombatPlanner.TournamentPerf.High));
        var team=BuildValue.Add(tank,p).Total;
        foreach(var q in ps.Skip(1))q.Creature.SetCurrentHpInternal(0);
        var solo=BuildValue.Add(tank,p).Total;
        Console.WriteLine($"CONSISTENCY TANK team={team:F3} solo={solo:F3} midnightLoad={noSupply:F3}");
        Check(team>solo+5,"Tank must credit living allies' prevented HP loss and retain the owner's extra damage");
        Console.WriteLine("PASS: construction blends absolute outcomes and models autoplay, power startup, exhaust discounts and team protection.");
    }
    private static (CombatState C,Player P,Creature E) Board(bool demon=false,int hp=80,int enemyHp=1000)
    {
        var(run,ps)=Party(1);var p=ps[0];var c=new CombatState(runState:run){RoundNumber=1};
        p.ResetCombatState();c.AddPlayer(p);p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);p.Creature.SetCurrentHpInternal(hp);
        MonsterModel model=demon?ModelDb.Monster<KnowledgeDemon>().ToMutable():ModelDb.Monster<MockAttackMonster>().ToMutable();
        var e=c.CreateCreature(model,CombatSide.Enemy,"0");c.AddCreature(e);model.SetUpForCombat();e.SetMaxHpInternal(enemyHp);e.SetCurrentHpInternal(enemyHp);
        model.SetMoveImmediate((MoveState)model.MoveStateMachine!.States[demon?"CURSE_OF_KNOWLEDGE_MOVE":"ATTACK"],true);
        return(c,p,e);
    }
    private static T Hand<T>(CombatState c,Player p)where T:CardModel
    {var card=c.CreateCard<T>(p);p.PlayerCombatState!.Hand.AddInternal(card);return card;}
    private static void EnemyChoice()
    {
        // Explicit owner policy 2026-10-07 replaces the old deck/HP valuation.
        // R2 production faults exited 1; restored runs exited 0:
        // System.Exception: Decision consistency: owner policy must choose Disintegration for every curse, deck and HP
        // System.Exception: Decision consistency: automatic enemy prediction must follow the fixed live Disintegration policy
        // Commands/raw logs: work/decision-repair-20261007/root-r2-results.json.
        foreach(var shivs in new[]{false,true})
        foreach(var hp in new[]{80,2})
        foreach(var counter in new[]{0,1,2})
        {
            var(c,p,e)=Board(true,hp);
            for(var i=0;i<10;i++)p.Deck.AddInternal(shivs?c.RunState.CreateCard<BladeDance>(p):c.RunState.CreateCard<Bludgeon>(p));
            AccessTools.Field(typeof(KnowledgeDemon),"_curseOfKnowledgeCounter").SetValue(e.Monster,counter);
            var dis=c.CreateCard<Disintegration>(p);dis.DynamicVars["DisintegrationPower"].BaseValue=6+counter;
            CardModel alternative=counter switch {0=>c.CreateCard<MindRot>(p),1=>c.CreateCard<Sloth>(p),_=>c.CreateCard<WasteAway>(p)};
            var options=new CardModel[]{alternative,dis};
            var picked=BotBrain.SelectCards(p,options,1,1,"FromChooseACardScreen").Single();
            Check(picked is Disintegration,"owner policy must choose Disintegration for every curse, deck and HP");
            var sim=KernelSession.Capture(c);sim.AutoResolveEnemyChoices=true;sim.AutoResolveTurnStartChoices=true;
            Check(sim.EndTurn(p,1,out var reason),"curse prediction must finish: "+reason);
            Check(sim.Power<DisintegrationPower>(p.Creature)==6+counter
                && sim.Power<MindRotPower>(p.Creature)==0 && sim.Power<SlothPower>(p.Creature)==0
                && sim.Power<WasteAwayPower>(p.Creature)==0,
                "automatic enemy prediction must follow the fixed live Disintegration policy");
        }
        Console.WriteLine("PASS: all three native enemy-stage curse menus choose Disintegration for expensive/shiv decks and low life.");
    }
    private static void Debuff()
    {
        var(c,p,e)=Board();ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(e,30,true);
        var shiv=Hand<Shiv>(c,p);Hand<HiddenDaggers>(c,p);Hand<Prepared>(c,p);p.PlayerCombatState!.LoseEnergy(3);
        var root=KernelSession.Capture(c);var after=root.Fork();Check(after.Play(shiv,e,out _),"shiv fixture must play");
        Check(Math.Abs(root.FutureDebuffValue([p],reference:root)-after.FutureDebuffValue([p],reference:root))<1e-7,
            "realized damage must not destroy the shared future Vulnerable dividend");
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(p.Creature,5,true);
        var stronger=KernelSession.Capture(c);
        // R2: freeze new Strength together with the horizon -> exit 1:
        // System.Exception: Decision consistency: a frozen opportunity window must retain newly earned Strength's future Vulnerable payoff
        // Command: work/decision-consistency-20261004/strength-r2.json; restored exit 0.
        Check(stronger.FutureDebuffValue([p],reference:root)>root.FutureDebuffValue([p],reference:root),
            "a frozen opportunity window must retain newly earned Strength's future Vulnerable payoff");
        var comparison=new KernelEndTurnRun(root,p,5000);while(!comparison.Done)comparison.Advance(TimeSpan.FromMilliseconds(2));
        Check(comparison.Actions?.Count==4 && comparison.Actions.Count(a=>a.Card is Shiv)==3,
            "end comparison must retain the fourth action in the free shiv generation chain");
        Console.WriteLine("PASS: future debuff comparisons share one opportunity window and retain all free shiv damage.");
    }
    private static void RelicExhaust()
    {
        // R2: restore static damage and fixed X cost -> exit 1:
        // System.Exception: Decision consistency: relic exhaust must preserve affordable strength-scaled X burst over ordinary fodder
        // R2: remove branch-local damage hooks -> exit 1:
        // System.Exception: Decision consistency: relic exhaust must count Strength on every hit rather than static printed damage
        // Commands: work/decision-consistency-20261004/relic-old-r2.json and relic-r2-results.json.
        var(c,p,e)=Board(hp:39,enemyHp:955);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(p.Creature,23,true);
        ModelDb.Power<CrueltyPower>().ToMutable().ApplyInternal(p.Creature,50,true);
        ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(e,16,true);
        var whirlwind=Hand<Whirlwind>(c,p);whirlwind.UpgradeInternal();
        Hand<Taunt>(c,p).UpgradeInternal();Hand<Bash>(c,p).UpgradeInternal();Hand<StrikeIronclad>(c,p).UpgradeInternal();Hand<StrikeIronclad>(c,p).UpgradeInternal();
        var selected=KernelSession.Capture(c).SelectRelicCards(p,p.PlayerCombatState!.Hand.Cards,"Exhaust",1,1).Single();
        Check(selected!=whirlwind,"relic exhaust must preserve affordable strength-scaled X burst over ordinary fodder");
        foreach(var strength in new[]{0,23})
        {
            var(tc,tp,te)=Board();
            if(strength>0)ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(tp.Creature,strength,true);
            var twin=Hand<TwinStrike>(tc,tp);var bludgeon=Hand<Bludgeon>(tc,tp);
            var pick=KernelSession.Capture(tc).SelectRelicCards(tp,tp.PlayerCombatState!.Hand.Cards,"Exhaust",1,1).Single();
            Check(strength>0?pick==bludgeon:pick==twin,"relic exhaust must count Strength on every hit rather than static printed damage");
        }
        Console.WriteLine("PASS: relic exhaust prices current Strength, attack repeats and affordable X burst before removing a card.");
    }
    private static void Sequence()
    {
        // R2: omit the exclusive continuation's debuff value -> exit 1:
        // System.Exception: Decision consistency: last-energy continuation must retain Taunt's future debuff over unnecessary plain block
        // R2: omit the AB/BA debuff comparison -> exit 1:
        // System.Exception: Decision consistency: native Vulnerable application must precede native doubling when damage ties
        // Commands and restored exits 0: work/decision-consistency-20261004/sequence-r2.json.
        foreach(var pair in new[]{false,true})
        {
            var(c,p,e)=Board(enemyHp:500);
            ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e,-1,true);
            ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(e,pair?1:4,true);
            if(!pair)p.PlayerCombatState!.LoseEnergy(2);
            CardModel first=pair?Hand<MoltenFist>(c,p):Hand<DefendIronclad>(c,p);
            CardModel second=pair?Hand<Bash>(c,p):Hand<Taunt>(c,p);
            if(pair)second.UpgradeInternal();
            for(var i=0;i<5;i++)p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
            var root=KernelSession.Capture(c);
            KernelTeamSearch.Action Action(CardModel card)=>new(p,card,card.TargetType==TargetType.AnyEnemy?e:null,
                CardStateKey:root.CardStateKey(card));
            var budget=KernelSequenceOrder.PairLimit;
            var best=KernelSequenceOrder.Before(root,[p],[p],Action(first),[Action(first),Action(second)],ref budget);
            Check(best.Card==second,pair?"native Vulnerable application must precede native doubling when damage ties"
                :"last-energy continuation must retain Taunt's future debuff over unnecessary plain block");
        }
        Console.WriteLine("PASS: last-energy block alternatives and native Vulnerable doubling retain their later payoff.");
    }
    private static void Set(object o,string field,object? value)=>AccessTools.Field(o.GetType(),field).SetValue(o,value);
    private static object? Get(object o,string field)=>AccessTools.Field(o.GetType(),field).GetValue(o);
    private static void Victory()
    {
        // R2 2026-10-09: focused native replay reproof; both production faults
        // exited 1, exact source/DLL restoration and focused reruns exited 0.
        // Evidence: work/architecture-refactor-20261009/r2/decision-victory-control-v1/proof/proof.json.
        // Skip ReviewVictoryAtRound -> System.Exception: Decision consistency: stale predicted victory must lose its end-turn exemption at the live round
        // Reject the completed native win -> System.Exception: Decision consistency: live replay must preserve and execute an actually winning suffix
        foreach(var realWin in new[]{false,true})
        {
            var(c,p,e)=Board(hp:20,enemyHp:realWin?4:1000);c.RoundNumber=2;
            // This manually installed suffix tests the all-Bot victory review.
            // A human seat without a captured mixed continuation must re-root
            // before that review; mixed control has separate native fixtures.
            var previous=AutoPilot.IsAutopiloted(p.NetId);
            try
            {
                AutoPilot.Set(p.NetId,true);
                var shiv=Hand<Shiv>(c,p);ModelDb.Power<DisintegrationPower>().ToMutable().ApplyInternal(p.Creature,13,true);
                var root=KernelSession.Capture(c);
                var action=realWin?new KernelTeamSearch.Action(p,shiv,e,CardStateKey:root.CardStateKey(shiv)):new(p,null,null,EndTurn:true);
                var record=TerminalRecord.Capture(root,[p],true,"") with {Victory=true};
                var planner=new KernelCombatPlanner();
                Set(planner,"tournamentScript",new[]{action});Set(planner,"tournamentScriptCombat",c);Set(planner,"tournamentScriptRound",1);
                Set(planner,"tournamentScriptRecord",record);Set(planner,"tournamentScriptAliveStates",new[]{new[]{p.NetId}});
                Set(planner,"tournamentScriptEnemyStates",new[]{new[]{e}});Set(planner,"victoryCheckedRound",1);
                object?[] args=[c,new[]{p},true,null];
                for(var i=0;i<500;i++)
                {
                    AccessTools.Method(typeof(KernelCombatPlanner),"TryEmitFromTournamentScript").Invoke(planner,args);
                    if(args[3] is not null || Get(planner,"tournamentScript") is null || planner.ConfirmedEndTurn is not null)break;
                }
                if(realWin)Check(args[3] is TeamCombatPlanner.Decision d && d.Move.Card==shiv,
                    "live replay must preserve and execute an actually winning suffix");
                else Check(Get(planner,"tournamentScriptRecord") is not TerminalRecord {Verified:true,Victory:true}
                    && planner.ConfirmedEndTurn is null,
                    "stale predicted victory must lose its end-turn exemption at the live round");
            }
            finally {AutoPilot.Set(p.NetId,previous);}
        }
        Console.WriteLine("PASS: live round replay revokes a false terminal guarantee and preserves a real victory without per-card search.");
    }
}

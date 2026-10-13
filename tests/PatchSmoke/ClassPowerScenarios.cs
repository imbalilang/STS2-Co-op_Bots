using CoopBots;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Death;
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

internal static class ClassPowerScenarios
{
    // R2 2026-10-01: disable each mechanism independently, exit 1; restore and
    // rerun this suite. Commands/logs: work/power-coverage-20261001/r2.json.
    // Stack-count fallback: "Pyre must retain more future energy value than its two-energy activation cost."
    //   "RupturePower must value real triggers, zero without them: 2.00/2.00."
    //   "Two Plating must count only the one remaining future block, since current block is already in Threat."
    // Constant energy bonus: "Pyre must not invent energy payoff for a zero-cost deck."
    // Zero projection: "Vicious route must follow real Vulnerable triggers: withTrigger=True, empty=True."
    // Missing OnPlay: "TANK: native OnPlay must be executable without a prediction boundary."
    // Missing ally grant or Tank multiplier: "Tank must take 1.5x while each living ally takes 0.5x powered attack damage."
    // Missing Guarded multiplier: "Two Tanks must independently halve another ally's damage."
    // Missing death cleanup: "Tank death must remove its Guarded, preserve the other caster and leave the sibling branch unchanged."
    // Ignoring death prevention: "Prevented Tank death must preserve every Guarded instance."
    // Free retention bonus: "Well Laid Plans must not invent unspent cards when the entire deck is free."
    // Free Juggling copies: "JugglingPower must value real triggers, zero without them: 28.80/28.80."
    // Native v0.111.0 OnPlay destinations. Enumerate the actual character pools
    // below, so a new power cannot silently bypass this inventory.
    private static readonly Dictionary<string,Type[]> Effects = new()
    {
        ["AGGRESSION"]=[typeof(AggressionPower)], ["BARRICADE"]=[typeof(BarricadePower)],
        ["CORRUPTION"]=[typeof(CorruptionPower)], ["CRIMSON_MANTLE"]=[typeof(CrimsonMantlePower)],
        ["CRUELTY"]=[typeof(CrueltyPower)], ["DARK_EMBRACE"]=[typeof(DarkEmbracePower)],
        ["DEMON_FORM"]=[typeof(DemonFormPower)], ["FEEL_NO_PAIN"]=[typeof(FeelNoPainPower)],
        ["HELLRAISER"]=[typeof(HellraiserPower)], ["INFERNO"]=[typeof(InfernoPower)],
        ["INFLAME"]=[typeof(StrengthPower)], ["JUGGERNAUT"]=[typeof(JuggernautPower)],
        ["JUGGLING"]=[typeof(JugglingPower)], ["PYRE"]=[typeof(PyrePower)],
        ["RUPTURE"]=[typeof(RupturePower)], ["STAMPEDE"]=[typeof(StampedePower)],
        ["STONE_ARMOR"]=[typeof(PlatingPower)], ["TANK"]=[typeof(TankPower)],
        ["UNMOVABLE"]=[typeof(UnmovablePower)], ["VICIOUS"]=[typeof(ViciousPower)],
        ["ABRASIVE"]=[typeof(DexterityPower),typeof(ThornsPower)], ["ACCELERANT"]=[typeof(AccelerantPower)],
        ["ACCURACY"]=[typeof(AccuracyPower)], ["AFTERIMAGE"]=[typeof(AfterimagePower)],
        ["ENVENOM"]=[typeof(EnvenomPower)], ["FAN_OF_KNIVES"]=[typeof(FanOfKnivesPower)],
        ["FOOTWORK"]=[typeof(DexterityPower)], ["INFINITE_BLADES"]=[typeof(InfiniteBladesPower)],
        ["MASTER_PLANNER"]=[typeof(MasterPlannerPower)], ["NOXIOUS_FUMES"]=[typeof(NoxiousFumesPower)],
        ["PHANTOM_BLADES"]=[typeof(PhantomBladesPower)], ["SERPENT_FORM"]=[typeof(SerpentFormPower)],
        ["SNEAKY"]=[typeof(SneakyPower)], ["SPEEDSTER"]=[typeof(SpeedsterPower)],
        ["TOOLS_OF_THE_TRADE"]=[typeof(ToolsOfTheTradePower)], ["TRACKING"]=[typeof(TrackingPower)],
        ["WELL_LAID_PLANS"]=[typeof(WellLaidPlansPower)], ["WRAITH_FORM"]=[typeof(IntangiblePower),typeof(WraithFormPower)],
    };

    internal static void Run()
    {
        new Harmony("coopbots.test.class-powers.format").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText"),
            prefix:new HarmonyMethod(typeof(ClassPowerScenarios),nameof(Format)));
        var failures=new List<string>();
        void Check(bool value,string why){if(!value)failures.Add(why);}
        var count=0;
        foreach(var character in new CharacterModel[]{ModelDb.Character<Ironclad>(),ModelDb.Character<Silent>()})
        foreach(var template in character.CardPool.AllCards.Where(c=>c.Type==CardType.Power).OrderBy(c=>c.Id.Entry))
        foreach(var upgraded in new[]{false,true})
        {
            var name=template.Id.Entry+(upgraded?"+":"");
            try
            {
                var (party,combat,enemy)=Board();var p=party[0];
                var card=combat.CreateCard(template,p);if(upgraded)card.UpgradeInternal();
                p.PlayerCombatState!.Hand.AddInternal(card);
                foreach(var support in new CardModel[]{ModelDb.Card<StrikeIronclad>(),ModelDb.Card<DefendIronclad>(),ModelDb.Card<Shiv>(),ModelDb.Card<Backflip>(),ModelDb.Card<Bloodletting>()})
                    p.PlayerCombatState.Hand.AddInternal(combat.CreateCard(support,p));
                foreach(var support in new CardModel[]{ModelDb.Card<StrikeIronclad>(),ModelDb.Card<DefendIronclad>(),ModelDb.Card<BladeDance>(),ModelDb.Card<Bash>(),ModelDb.Card<Prepared>(),ModelDb.Card<TrueGrit>()})
                    p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard(support,p));
                var root=KernelSession.Capture(combat);
                var branch=root.CardBranches(card,null,1).FirstOrDefault(b=>b.Boundary.Length==0);
                Check(branch is not null,$"{name}: native OnPlay must be executable without a prediction boundary.");
                if(branch is null)continue;
                var after=branch.State;var state=State(after);
                Check(Effects.TryGetValue(template.Id.Entry,out var effects),$"{name}: missing native effect audit.");
                foreach(var effect in effects??[])
                    Check(state.EffectivePowers().Any(q=>q.Owner==p.Creature && q.GetType()==effect && q.Amount>0),$"{name}: missing {effect.Name}.");
                var persistent=after.PersistentValue(p,10,1);
                Check(double.IsFinite(persistent),$"{name}: persistent valuation must be finite.");
                after.AutoResolveTurnStartChoices=true;after.AutoResolveEnemyChoices=true;
                var boundary="";var advances=true;
                foreach(var player in party)if(!after.EndTurn(player,3,out boundary)){advances=false;break;}
                Check(advances,$"{name}: rollout must cross the next turn: {boundary}.");
                if(card is Pyre)Check(after.Energy(p)==(upgraded?5:4),$"{name}: actual next-turn energy must include Pyre.");
                if(card is ToolsOfTheTrade)Check(after.Hand(p).Count==5,$"{name}: six draws followed by one discard must leave five cards.");
                if(card is NoxiousFumes)Check(after.Power<PoisonPower>(enemy)>0,$"{name}: next player start must apply poison.");
                if(card is InfiniteBlades)Check(after.Hand(p).Any(c=>c is Shiv),$"{name}: next player start must generate a Shiv.");
                Console.WriteLine($"CLASS POWER {name}: applied=True next={advances} value={persistent:F2}");count++;
            }
            catch(Exception ex){failures.Add($"{name}: {ex}");}
        }
        Check(count==Effects.Count*2,$"All 38 class powers, normal and upgraded, must complete: {count}/76.");
        PyreDecisions(Check);
        TriggerValues(Check);
        TriggerRoutes(Check);
        TankLifecycle(Check);
        if(failures.Count>0)throw new Exception(string.Join("\n",failures));
        Console.WriteLine("PASS: all Ironclad/Silent powers apply, cross turns, value triggers and preserve Tank ownership.");
    }

    private static void PyreDecisions(Action<bool,string> check)
    {
        foreach(var upgraded in new[]{false,true})
        {
            var (party,combat,_)=Board();var p=party[0];p.PlayerCombatState!.LoseEnergy(3);
            var pyre=combat.CreateCard<Pyre>(p);if(upgraded)pyre.UpgradeInternal();p.PlayerCombatState.Hand.AddInternal(pyre);
            for(var i=0;i<3;i++)p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(p));
            for(var i=0;i<5;i++)p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
            var root=KernelSession.Capture(combat);var after=root.CardBranches(pyre,null,1).Single(b=>b.Boundary.Length==0).State;
            var gain=after.PersistentValue(p,10,1)-root.PersistentValue(p,10,1);
            check(gain>6,"Pyre must retain more future energy value than its two-energy activation cost.");
            var line=KernelRollout.Run(root.Fork(),party,party,new(RolloutPolicyKind.Tactical,MaxSteps:1));
            check(line.Actions.FirstOrDefault()?.Card is Pyre,"Tactical must invest in Pyre before spending all energy on Strikes in a long fight.");
            Console.WriteLine($"PYRE DECISION upgrade={upgraded} gain={gain:F2} first={line.Actions.FirstOrDefault()?.Card?.Id.Entry}");
        }
    }

    private static void TriggerValues(Action<bool,string> check)
    {
        // Counterexamples prevent replacing a missing model by a constant bonus.
        double Value<TPower>(bool trigger) where TPower:PowerModel
        {
            var (party,combat,enemy)=Board();var p=party[0];
            for(var i=0;i<8;i++)p.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
            if(trigger)
            {
                p.Creature.GainBlockInternal(40);
                p.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<Bloodletting>(p));
                p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Bash>(p));
                p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Backflip>(p));
                p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Shiv>(p));
                p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Anger>(p));
                p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendIronclad>(p));
                ModelDb.Power<PoisonPower>().ToMutable().ApplyInternal(enemy,20,true);
                ModelDb.Power<WeakPower>().ToMutable().ApplyInternal(enemy,2,true);
                ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(enemy,2,true);
            }
            var root=KernelSession.Capture(combat);var after=root.Fork();State(after).Apply<TPower>(p.Creature,2,p.Creature);
            return after.PersistentValue(p,10,1)-root.PersistentValue(p,10,1);
        }
        void Needs<T>() where T:PowerModel
        {
            var without=Value<T>(false);var with=Value<T>(true);
            check(Math.Abs(without)<.001 && with>0,$"{typeof(T).Name} must value real triggers, zero without them: {without:F2}/{with:F2}.");
        }
        Needs<RupturePower>();Needs<ViciousPower>();Needs<AccelerantPower>();Needs<PhantomBladesPower>();
        Needs<SpeedsterPower>();Needs<TrackingPower>();Needs<CrueltyPower>();Needs<JuggernautPower>();Needs<UnmovablePower>();
        Needs<BarricadePower>();Needs<InfernoPower>();
        check(Value<AggressionPower>(false)>2,"Aggression must value recurring attack retrieval and upgrades.");
        check(Value<HellraiserPower>(false)>2,"Hellraiser must value recurring Strike autoplays.");
        Needs<JugglingPower>();
        check(Value<StampedePower>(false)>2,"Stampede must value unused attacks played after turn end.");
        check(Value<CrimsonMantlePower>(false)>2,"Crimson Mantle must value recurring block.");
        check(Value<PlatingPower>(false)==1,"Two Plating must count only the one remaining future block, since current block is already in Threat.");
        check(Value<ToolsOfTheTradePower>(false)>2,"Tools of the Trade must value repeated hand filtering.");
        check(Value<WellLaidPlansPower>(false)>2,"Well Laid Plans must value retaining unspent playable cards.");
        check(Value<IntangiblePower>(false)>2,"Intangible must value future protected turns.");
        check(Value<WraithFormPower>(true)<0,"Wraith Form must include the future Dexterity penalty.");
        check(Value<NoxiousFumesPower>(false)>2,"Noxious Fumes must value recurring poison, not just its stack count.");
        check(Value<PyrePower>(false)>6,"Pyre in a paid attack deck must model recurring energy.");
        var (party,combat,_)=Board();var p=party[0];
        for(var i=0;i<8;i++)p.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<Anger>(p));
        var root=KernelSession.Capture(combat);State(root).Apply<PyrePower>(p.Creature,2,p.Creature);
        check(root.PersistentValue(p,10,1)==0,"Pyre must not invent energy payoff for a zero-cost deck.");
        State(root).SetPowerAmount(State(root).EffectivePowers().OfType<PyrePower>().Single(),0);
        State(root).Apply<WellLaidPlansPower>(p.Creature,1,p.Creature);
        check(root.PersistentValue(p,10,1)==0,"Well Laid Plans must not invent unspent cards when the entire deck is free.");
    }

    private static void TankLifecycle(Action<bool,string> check)
    {
        var (party,combat,enemy)=Board();var p=party[0];
        var tank=combat.CreateCard<Tank>(p);p.PlayerCombatState!.Hand.AddInternal(tank);
        var root=KernelSession.Capture(combat);var branch=root.CardBranches(tank,null,1).FirstOrDefault(b=>b.Boundary.Length==0);
        if(branch is null){check(false,"Tank must be executable in multiplayer.");return;}
        var after=branch.State;
        check(after.IntentHit(enemy,p.Creature,10)==15 && party.Skip(1).All(a=>after.IntentHit(enemy,a.Creature,10)==5),
            "Tank must take 1.5x while each living ally takes 0.5x powered attack damage.");
        check(party.Skip(1).All(a=>State(after).EffectivePowers().OfType<GuardedPower>().Any(q=>q.Owner==a.Creature && q.Applier==p.Creature)),
            "Every Guarded instance must retain the Tank owner as its applier.");
        check(root.IntentHit(enemy,party[1].Creature,10)==10,"Applying Tank in a fork must not mutate the parent.");
        var fork=after.Fork();State(fork).Apply<TankPower>(party[1].Creature,1,party[1].Creature);
        check(fork.IntentHit(enemy,party[2].Creature,20)==5,"Two Tanks must independently halve another ally's damage.");
        // Prevented death preserves protection. An actual simulated death must
        // dispatch the hook and remove only this caster's independent instances.
        var guarded=State(fork).EffectivePowers().OfType<GuardedPower>().Where(q=>q.Applier==p.Creature).ToArray();
        foreach(var q in guarded)AfterDeathMirrors.Invoke(q,new AfterDeathMirrorContext
        {Simulator=Simulator(fork),Creature=p.Creature,WasRemovalPrevented=true});
        check(fork.IntentHit(enemy,party[2].Creature,20)==5,"Prevented Tank death must preserve every Guarded instance.");
        check(Simulator(fork).Kill(p.Creature,force:true),"Tank death must settle without a pending choice.");
        check(fork.IntentHit(enemy,party[2].Creature,20)==10 && after.IntentHit(enemy,party[2].Creature,20)==10,
            "Tank death must remove its Guarded, preserve the other caster and leave the sibling branch unchanged.");
    }

    private static void TriggerRoutes(Action<bool,string> check)
    {
        foreach(var withTrigger in new[]{false,true})
        {
            var (party,combat,_)=Board();var p=party[0];
            var vicious=combat.CreateCard<Vicious>(p);p.PlayerCombatState!.Hand.AddInternal(vicious);
            for(var i=0;i<8;i++)p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
            if(withTrigger)p.PlayerCombatState.Hand.AddInternal(combat.CreateCard<Bash>(p));
            var root=KernelSession.Capture(combat);
            var after=root.CardBranches(vicious,null,1).Single(b=>b.Boundary.Length==0).State;
            var ledger=KernelPowerRouter.Advance(KernelPowerLedger.Empty,root,after,new(p,vicious,null),party);
            check(ledger.IsEmpty!=withTrigger,$"Vicious route must follow real Vulnerable triggers: withTrigger={withTrigger}, empty={ledger.IsEmpty}.");
        }
    }

    private static CombatPredictionSimulator Simulator(KernelSession session)
        => (CombatPredictionSimulator)AccessTools.Field(typeof(KernelSession),"simulator").GetValue(session)!;

    internal static SimulatedCombatState State(KernelSession session)
        => (SimulatedCombatState)AccessTools.Property(typeof(KernelSession),"Combat").GetValue(session)!;
    private static (Player[],CombatState,Creature) Board()
    {
        var party=Enumerable.Range(0,4).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,BotRegistry.CreateId(BotDifficulty.Pro,41,i))).ToArray();
        var run=RunState.CreateForTest(party,seed:"CLASS-POWER-AUDIT");var combat=new CombatState(runState:run);
        foreach(var p in party){p.ResetCombatState();combat.AddPlayer(p);p.MaxEnergy=3;p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(6);for(var i=0;i<6;i++)p.Deck.AddInternal(run.CreateCard<StrikeIronclad>(p));}
        var enemy=combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"enemy");combat.AddCreature(enemy);enemy.Monster!.SetUpForCombat();enemy.SetMaxHpInternal(500);enemy.SetCurrentHpInternal(500);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"],true);
        return(party,combat,enemy);
    }
    private static bool Format(ref string __result){__result="";return false;}
}

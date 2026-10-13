using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class SzgBuildRepairScenarios
{
    // R2 2026-10-08: 21 production-mechanism and four performance-control
    // mutations in isolated source mirrors. Each dotnet run exited 1 at its
    // named assertion (not a compiler error), exact source-byte restoration
    // then ran green exit 0. Commands and source hashes:
    // work/szg-repair-20261008T124624/build/{r2,performance-r2}-results.json.
    // The seven native card cases below are independent sts2.dll witnesses;
    // the bounded construction estimates are not exact full native combats.
    // burst: System.InvalidOperationException: SZG build repair: Burst must repeat a funded following skill, never invent energy or a free standalone payoff
    // burst-enchanted-addition: System.InvalidOperationException: SZG build repair: Burst plus Spiral must add one native execution and thread the three-half effect factor through paid hands
    // burst-paid-weight: System.InvalidOperationException: SZG build repair: Burst plus Spiral must add one native execution and thread the three-half effect factor through paid hands
    // ink: System.InvalidOperationException: SZG build repair: BladeOfInk must generate two payable zero-cost attacks and Inky weakness
    // phantom: System.InvalidOperationException: SZG build repair: PhantomBlades adds nine to only the first Shiv of the turn
    // juggling-payment: System.InvalidOperationException: SZG build repair: Juggling third-attack copies must pay their ordinary cost
    // tools-filter: System.InvalidOperationException: SZG build repair: ToolsOfTheTrade must filter the newly drawn hand before spending energy
    // tools-pipeline: System.InvalidOperationException: SZG build repair: ToolsOfTheTrade must reach sustained hands as six native draws followed by one mandatory discard
    // engine-pipeline: System.InvalidOperationException: SZG build repair: ToolsOfTheTrade must reach sustained hands as six native draws followed by one mandatory discard
    // phase-continuation: System.InvalidOperationException: SZG build repair: the actual construction stress path must revive into another finite phase instead of stopping after first lethal
    // reactive-skill: System.InvalidOperationException: SZG build repair: construction skill prevention must pay the same first-form reactive damage it causes
    // boss-pipeline: System.InvalidOperationException: SZG build repair: the actual deck-strength robustness evaluation must include the long-boss continuation
    // parasol-owner: System.InvalidOperationException: SZG build repair: only a virtual parasol owner needs host compensation
    // parasol-wire: System.InvalidOperationException: SZG build repair: production parasol command must replicate its free purchase and full-belt replacement
    // parasol-cap: System.InvalidOperationException: SZG build repair: parasol must take all forced cards, relics, potion and mandatory removal beyond the ordinary eight-purchase cap
    // native-parasol-hook: System.InvalidOperationException: SZG build repair: the installed native LordsParasol hook must observe the real owner's returned purchase task
    // native-parasol-wait: System.InvalidOperationException: SZG build repair: ordinary driver and room completion must wait for a real owner's native free purchase task
    // native-parasol-room: System.InvalidOperationException: SZG build repair: a pending real-owner native task must hold an otherwise completed room
    // native-parasol-fault: System.InvalidOperationException: SZG build repair: failed native purchase task must remain a reported fault, never successful completion
    // upgrade-diagnostics: System.InvalidOperationException: SZG build repair: production upgrade ranking must log every candidate's real value and priority
    // shop-diagnostics: System.InvalidOperationException: SZG build repair: production shop planning must log considered and unaffordable goods with actual net values
    // performance-long-boss: System.Exception: Construction performance: full deck scores or decision-scope invalidation changed
    // performance-independent-score: System.Exception: Construction performance: full deck scores or decision-scope invalidation changed
    // performance-cache-budget: System.Exception: Construction performance: reward plus trace must evaluate four decks once; robustness calls=48, expected=16
    // performance-allocations: System.Exception: Construction performance: repeated hand searches allocated 23141512 bytes, budget=2097152
    // Performance calibration changes only Total/Robustness/Growth in 22
    // deck vectors. Removing only the new long-boss blend reproduces every
    // old field and the same 768-hand hash; performance-deltas.json records
    // every old/new delta. Reward-cache count=16 and allocation budget=2MiB
    // are unchanged, independently faulted above; restored allocations=69632
    // bytes / 64 searches. No assertions or thresholds were relaxed.
    private static void Check(bool value, string why)
    { if (!value) throw new InvalidOperationException("SZG build repair: " + why); }

    internal static void Run(string? only=null)
    {
        TestEnvironment.Ensure();
        if(only is null or "native")NativeMechanisms();
        if(only is null or "engines")Engines();
        if(only is null or "boss")BossStress();
        if(only is null or "shop")FreeShop();
        if(only is null or "diagnostics")Diagnostics();
    }

    // Actual sts2.dll hooks/OnPlay below; no simulation mirrors. Presentation
    // and the Godot timer wrapper are omitted. Card play-count, clone creation,
    // enchantment, history-trigger rules and energy stock remain native.
    private static void NativeMechanisms()
    {
        var presentation = new Harmony("coopbots.test.szg-build-native");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix:new HarmonyMethod(typeof(SzgBuildRepairScenarios), nameof(Format)));
        presentation.Patch(AccessTools.PropertyGetter(typeof(PowerModel), "Icon"),
            prefix:new HarmonyMethod(typeof(SzgBuildRepairScenarios), nameof(NoIcon)));
        try
        {
            foreach (var energy in new[] { 1, 2 })
            {
                var (p,c,e) = NativeBoard("SZG-NATIVE-BURST",energy); using var turn = new NativeTurn(c);
                var burst = c.CreateCard<Burst>(p); p.PlayerCombatState!.Hand.AddInternal(burst);
                var defence = c.CreateCard<DefendSilent>(p); p.PlayerCombatState.Hand.AddInternal(defence);
                if(energy==2)NativePlay(burst,null);
                if (p.PlayerCombatState.Energy >= 1) NativePlay(defence,null);
                Check(p.Creature.Block == (energy == 2 ? 10 : 5), "native Burst oracle must copy paid defence only");
                Console.WriteLine($"SZG NATIVE Burst energy={energy} block={p.Creature.Block} remaining={p.PlayerCombatState.Energy}");
            }
            {
                var (p,c,e) = NativeBoard("SZG-NATIVE-BURST-SPIRAL",2); using var turn = new NativeTurn(c);
                var burst=c.CreateCard<Burst>(p);p.PlayerCombatState!.Hand.AddInternal(burst);
                var defence=c.CreateCard<DefendSilent>(p);p.PlayerCombatState.Hand.AddInternal(defence);
                var spiral=ModelDb.Enchantment<MegaCrit.Sts2.Core.Models.Enchantments.Spiral>().ToMutable();
                defence.EnchantInternal(spiral,1m);spiral.ModifyCard();
                NativePlay(burst,null);NativePlay(defence,null);
                Check(p.Creature.Block==15, "native Burst must add one execution to Spiral instead of doubling it");
                Console.WriteLine($"SZG NATIVE Burst+Spiral block={p.Creature.Block} remaining={p.PlayerCombatState.Energy}");
            }
            {
                var (p,c,e) = NativeBoard("SZG-NATIVE-INK",1); using var turn = new NativeTurn(c);
                var ink = c.CreateCard<BladeOfInk>(p); p.PlayerCombatState!.Hand.AddInternal(ink); NativePlay(ink,null);
                var shivs=p.PlayerCombatState.Hand.Cards.OfType<Shiv>().ToArray();
                Check(shivs.Length==2,"native BladeOfInk must generate two enchanted Shivs");
                foreach(var shiv in shivs)NativePlay(shiv,e);
                Check(e.CurrentHp==992 && e.GetPower<WeakPower>()?.Amount==2,"native Inky Shivs must deal eight and apply two Weak");
                Console.WriteLine($"SZG NATIVE BladeOfInk damage={1000-e.CurrentHp} weak={e.GetPower<WeakPower>()?.Amount}");
            }
            {
                var (p,c,e) = NativeBoard("SZG-NATIVE-PHANTOM",0); using var turn = new NativeTurn(c);
                PowerCmd.Apply<PhantomBladesPower>(new ThrowingPlayerChoiceContext(),p.Creature,9,p.Creature,null).GetAwaiter().GetResult();
                for(var i=0;i<2;i++){var shiv=c.CreateCard<Shiv>(p);p.PlayerCombatState!.Hand.AddInternal(shiv);NativePlay(shiv,e);}
                Check(e.CurrentHp==983,"native PhantomBlades must add nine only to the first Shiv");
                Console.WriteLine($"SZG NATIVE PhantomBlades damage={1000-e.CurrentHp}");
            }
            foreach(var energy in new[]{3,4})
            {
                var (p,c,e) = NativeBoard("SZG-NATIVE-JUGGLING",energy); using var turn = new NativeTurn(c);
                PowerCmd.Apply<JugglingPower>(new ThrowingPlayerChoiceContext(),p.Creature,1,p.Creature,null).GetAwaiter().GetResult();
                for(var i=0;i<3;i++){var attack=c.CreateCard<StrikeIronclad>(p);p.PlayerCombatState!.Hand.AddInternal(attack);NativePlay(attack,e);}
                var copy=p.PlayerCombatState!.Hand.Cards.OfType<StrikeIronclad>().Single();
                Check(copy.EnergyCost.GetWithModifiers(CostModifiers.All)==1,"native Juggling copy must keep the original one-energy cost");
                if(p.PlayerCombatState.Energy>=1)NativePlay(copy,e);
                Check(e.CurrentHp==(energy==4?976:982),"native third-attack copy must require another paid energy");
                Console.WriteLine($"SZG NATIVE Juggling energy={energy} damage={1000-e.CurrentHp} remaining={p.PlayerCombatState.Energy}");
            }
        }
        finally{presentation.UnpatchAll(presentation.Id);}
    }

    private static (Player,CombatState,Creature) NativeBoard(string seed,int energy)
    {
        var p=Player.CreateForNewRun<Deprived>(UnlockState.all,37);var run=RunState.CreateForTest([p],seed:seed);
        var c=new CombatState(runState:run);p.MaxEnergy=energy;p.ResetCombatState();c.AddPlayer(p);
        p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(energy);
        var e=c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"oracle");c.AddCreature(e);
        e.Monster!.SetUpForCombat();e.SetMaxHpInternal(1000);e.SetCurrentHpInternal(1000);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);
        return(p,c,e);
    }
    private static void NativePlay(CardModel card,Creature? target)
    {
        var c=card.Owner.Creature.CombatState!;var ctx=new ThrowingPlayerChoiceContext();
        card.Owner.PlayerCombatState!.LoseEnergy(card.EnergyCost.GetWithModifiers(CostModifiers.All));
        var count=((Task<int>)AccessTools.Method(typeof(CardModel),"GeneratePlayCount").Invoke(card,[c,target])!).GetAwaiter().GetResult();
        for(var i=0;i<count;i++)
        {
            var play=new CardPlay{Card=card,Player=card.Owner,Target=target,ResultPile=PileType.Discard,Resources=default,IsAutoPlay=false,PlayIndex=i,PlayCount=count};
            Hook.BeforeCardPlayed(c,play).GetAwaiter().GetResult();CombatManager.Instance.History.CardPlayStarted(c,play);
            ((Task)AccessTools.Method(card.GetType(),"OnPlay").Invoke(card,[ctx,play])!).GetAwaiter().GetResult();
            if(card.Enchantment is { } enchantment)enchantment.OnPlay(ctx,play).GetAwaiter().GetResult();
            CombatManager.Instance.History.CardPlayFinished(c,play);Hook.AfterCardPlayed(c,ctx,play).GetAwaiter().GetResult();
        }
        card.Owner.PlayerCombatState.Hand.RemoveInternal(card);card.Owner.PlayerCombatState.DiscardPile.AddInternal(card);
    }
    private static bool Format(ref string __result){__result="";return false;}
    private static bool NoIcon(ref Godot.Texture2D? __result){__result=null;return false;}
    private sealed class NativeTurn:IDisposable
    {
        private readonly FieldInfo field=AccessTools.Field(typeof(CombatManager),"_turnState");
        private readonly PropertyInfo run=AccessTools.Property(typeof(RunManager),"State");
        private readonly PropertyInfo ascension=AccessTools.Property(typeof(RunManager),"AscensionManager");
        private readonly object? oldTurn,oldRun,oldAscension;private readonly ulong? oldId=LocalContext.NetId;
        internal NativeTurn(CombatState c)
        {
            oldTurn=field.GetValue(CombatManager.Instance);oldRun=run.GetValue(RunManager.Instance);
            oldAscension=ascension.GetValue(RunManager.Instance);
            run.SetValue(RunManager.Instance,c.RunState);LocalContext.NetId=c.Players[0].NetId;
            ascension.SetValue(RunManager.Instance,new MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager(c.RunState.AscensionLevel));
            var turn=Activator.CreateInstance(field.FieldType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[c],null)!;
            AccessTools.Property(field.FieldType,"IsInProgress").SetValue(turn,true);AccessTools.Property(field.FieldType,"IsStarting").SetValue(turn,false);
            field.SetValue(CombatManager.Instance,turn);
        }
        public void Dispose(){field.SetValue(CombatManager.Instance,oldTurn);run.SetValue(RunManager.Instance,oldRun);ascension.SetValue(RunManager.Instance,oldAscension);LocalContext.NetId=oldId;}
    }

    private static void Engines()
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 37);
        var run = RunState.CreateForTest([p], seed: "SZG-ENGINES");
        p.MaxEnergy = 3; p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
        T Card<T>() where T : CardModel => run.CreateCard<T>(p);
        ConstructionHand.Slot Slot(CardModel c) => new(ConstructionCard.Capture(c), CardProfile.Of(c));
        var burst = Card<Burst>(); var defend = Card<DefendSilent>();
        var funded = ConstructionHand.Solve([Slot(burst), Slot(defend)], 2, new(2, 20, 400));
        var unfunded = ConstructionHand.Solve([Slot(burst), Slot(defend)], 2, new(1, 20, 400));
        Check(funded.Block == 10 && funded.EnergySpent == 2 && unfunded.Block == 5,
            "Burst must repeat a funded following skill, never invent energy or a free standalone payoff");
        Console.WriteLine($"PASS: SZG Burst copies funded defence only ({funded.Block}/{unfunded.Block}, paid={funded.EnergySpent}).");

        var spiral=ModelDb.Enchantment<MegaCrit.Sts2.Core.Models.Enchantments.Spiral>().ToMutable();
        var repeatedDefend=Card<DefendSilent>();repeatedDefend.EnchantInternal(spiral,1m);spiral.ModifyCard();
        var additive=ConstructionHand.Solve([Slot(burst),Slot(repeatedDefend)],2,new(2,40,400){SkillEnrage=2});
        var played=PayableHands.Evaluate([new(ConstructionCard.Capture(burst),CardProfile.Of(burst),1),new(ConstructionCard.Capture(repeatedDefend),CardProfile.Of(repeatedDefend),1)],2,2,2,40,400,1,1,0);
        Console.WriteLine($"SZG CONSTRUCTION Burst+Spiral block={additive.Block} executions={(additive.Executions>>4)&15} incoming={additive.EnemyAttackDamage} weight={played.Plays[1]}");
        Check(additive.Block==15 && ((additive.Executions>>4)&15)==3 && additive.EnemyAttackDamage==48 && played.Plays[1]==1.5,
            "Burst plus Spiral must add one native execution and thread the three-half effect factor through paid hands");
        Console.WriteLine("PASS: SZG Burst+Spiral executes three times and preserves paid-hand weighting.");

        var ink = Card<BladeOfInk>();
        var inkHand = ConstructionHand.Solve([Slot(ink)], 1, new(1, 20, 400));
        Check(inkHand.Damage == 8 && inkHand.EnemyAttackDamage == 15 && DraftCardEffects.Of(ink).GeneratedShivs == 2,
            "BladeOfInk must generate two payable zero-cost attacks and Inky weakness");
        Console.WriteLine($"PASS: SZG Blade of Ink grants native two Shivs and weakness ({inkHand.Damage}/{inkHand.EnemyAttackDamage}).");

        var shivs = new[] { Slot(Card<Shiv>()), Slot(Card<Shiv>()) };
        var phantom = ConstructionHand.Solve(shivs, 2, new(0, 0, 400) { PhantomBlades = 9 });
        Check(phantom.Damage == 17 && phantom.FirstShivDamage == 9,
            "PhantomBlades adds nine to only the first Shiv of the turn");
        Console.WriteLine($"PASS: SZG Phantom Blades spends its first-Shiv bonus once ({phantom.Damage}).");

        var attacks = new[] { Slot(Card<StrikeIronclad>()), Slot(Card<StrikeIronclad>()), Slot(Card<StrikeIronclad>()) };
        var affordable = ConstructionHand.Solve(attacks, 3, new(4, 0, 400) { Juggling = 1 });
        var unpaid = ConstructionHand.Solve(attacks, 3, new(3, 0, 400) { Juggling = 1 });
        Check(affordable.CopiedAttackDamage == 6 && affordable.EnergySpent == 4 && unpaid.CopiedAttackDamage == 0,
            "Juggling third-attack copies must pay their ordinary cost");
        Console.WriteLine($"PASS: SZG Juggling third-attack copy pays energy ({affordable.Damage}/{unpaid.Damage}).");

        var slots = new[] { Slot(Card<StrikeIronclad>()), Slot(Card<StrikeIronclad>()), Slot(Card<StrikeIronclad>()),
            Slot(Card<DefendIronclad>()), Slot(Card<Injury>()), Slot(Card<DefendIronclad>()) };
        var filtered = ConstructionHand.Solve(slots, 6, new(3, 20, 400) { StartDiscard = 1 });
        Check(filtered.Drawn == 6 && (filtered.Removed & (1 << 4)) != 0 && filtered.Block == 10,
            "ToolsOfTheTrade must filter the newly drawn hand before spending energy");
        var models = new CardModel[] { Card<ToolsOfTheTrade>(), Card<Juggling>(), Card<PhantomBlades>(), ink, burst };
        Check(models.All(c => !SustainedDeckValue.Unsupported(c) && BuildCoverage.Of(c) == 1),
            "all five modeled engines must reach construction coverage");
        var deck = Enumerable.Range(0, 5).Select(_ => (CardModel)Card<StrikeSilent>()).Concat(Enumerable.Range(0,4).Select(_ => (CardModel)Card<DefendSilent>())).ToList();
        deck.Add(Card<ToolsOfTheTrade>());
        var trace = SustainedDeckValue.OutcomeEstimate(deck, 3);
        Check(trace.Any(t => t.Turn>=2 && t.StartDiscardCount==1 && t.HandDrawn==6),
            "ToolsOfTheTrade must reach sustained hands as six native draws followed by one mandatory discard");
        Console.WriteLine("PASS: SZG Tools of the Trade filters draws through the construction pipeline.");
    }

    private static void BossStress()
    {
        var p=Player.CreateForNewRun<Deprived>(UnlockState.all,38);
        var run=RunState.CreateForTest([p],ascensionLevel:10,seed:"SZG-ENDURANCE");p.MaxEnergy=3;
        var c=new CombatState(runState:run);p.ResetCombatState();c.AddPlayer(p);using var turn=new NativeTurn(c);
        var native=AdaptableBossStress.For(p);
        var subject=ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.TestSubject>();
        double Amount(string name)=>(double)(int)AccessTools.Property(subject.GetType(),name).GetValue(subject)!;
        Check(native.Health.SequenceEqual(new[]{(double)subject.FirstFormHp,subject.SecondFormHp,subject.ThirdFormHp})
            && native.Bite==Amount("BiteDamage") && native.Bash==Amount("SkullBashDamage") && native.Claw==Amount("MultiClawDamage") && native.Enrage==Amount("EnrageAmount"),
            "long-boss stress must use public A10 native phases and attack parameters");
        var deck=Enumerable.Range(0,8).Select(_=>(CardModel)run.CreateCard<Bludgeon>(p)).ToArray();
        var trace=SustainedDeckValue.AdaptableTrace(deck,3);
        var resistance=DeckRobustness.Evaluate(deck,p,3);
        Check(Math.Abs(resistance.Adaptable-SustainedDeckValue.ScenarioStrength(deck,3,DeckRobustness.Interference.Adaptable,true))<1e-8,
            "the actual deck-strength robustness evaluation must include the long-boss continuation");
        Check(trace.Count==9 && trace.Any(t=>t.BossPhase==1) && trace.Where(t=>t.BossPhase==0).Last().EnemyHealthAfter==0,
            "the actual construction stress path must revive into another finite phase instead of stopping after first lethal");
        var defend=run.CreateCard<DefendSilent>(p);var slot=new ConstructionHand.Slot(ConstructionCard.Capture(defend),CardProfile.Of(defend));
        var ordinary=ConstructionHand.Solve([slot],1,new(1,22,1000));
        var reactive=ConstructionHand.Solve([slot],1,new(1,22,1000){SkillEnrage=3});
        Check(ordinary.Block==5 && reactive.Block==5 && reactive.EnemyAttackDamage==ordinary.EnemyAttackDamage+3
            && reactive.Utility<ordinary.Utility,
            "construction skill prevention must pay the same first-form reactive damage it causes");
        Console.WriteLine($"PASS: SZG long-boss construction crosses native finite phases and charges Skill enrage (phases={string.Join('/',trace.Select(t=>t.BossPhase))}).");
    }

    private static void Diagnostics()
    {
        var p=Player.CreateForNewRun<Deprived>(UnlockState.all,39);var run=RunState.CreateForTest([p],seed:"SZG-DIAGNOSTICS");
        p.MaxEnergy=3;for(var i=0;i<5;i++)p.Deck.AddInternal(run.CreateCard<StrikeIronclad>(p));
        for(var i=0;i<4;i++)p.Deck.AddInternal(run.CreateCard<DefendIronclad>(p));
        p.Deck.AddInternal(run.CreateCard<Bash>(p));p.Deck.AddInternal(run.CreateCard<PommelStrike>(p));p.Gold=77;
        var messages=new List<string>();void Capture(MegaCrit.Sts2.Core.Logging.LogLevel level,string message,int frames)=>messages.Add(message);
        MegaCrit.Sts2.Core.Logging.Log.LogCallback+=Capture;
        try
        {
            var ranked=BuildValue.RankUpgrades(p.Deck.Cards.Where(card=>card.IsUpgradable).Select(card=>(Card:card,Value:BuildValue.UpgradeForDecision(card,p)))).ToArray();
            Check(messages.Any(line=>line.Contains("CoopBots upgrade candidates:") && line.Contains("BASH") && line.Contains("POMMEL_STRIKE") && line.Contains("DEFEND_IRONCLAD") && line.Contains("priority=")),
                "production upgrade ranking must log every candidate's real value and priority");
            var room=new MerchantRoom();run.PushRoom(room);var stock=new MerchantInventory(p);room.Inventories.Add(stock);
            var offer=new MerchantCardEntry(p,stock,[],CardType.Skill);typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(offer,new CardCreationResult(run.CreateCard<ShrugItOff>(p)));
            typeof(MerchantEntry).GetField("_cost",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(offer,77);
            ((List<MerchantCardEntry>)stock.CharacterCardEntries).Add(offer);
            var tooExpensive=new MerchantCardEntry(p,stock,[],CardType.Attack);typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(tooExpensive,new CardCreationResult(run.CreateCard<Bludgeon>(p)));
            typeof(MerchantEntry).GetField("_cost",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(tooExpensive,150);
            ((List<MerchantCardEntry>)stock.CharacterCardEntries).Add(tooExpensive);
            BotShopPlanner.ChooseDetailed(stock);
            Check(messages.Any(line=>line.Contains("CoopBots shop candidates:") && line.Contains("SHRUG_IT_OFF") && line.Contains("BLUDGEON") && line.Contains("net=") && line.Contains("affordability")),
                "production shop planning must log considered and unaffordable goods with actual net values");
            Console.WriteLine("PASS: SZG production upgrade and shop logs expose all candidate comparisons.");
        }
        finally{MegaCrit.Sts2.Core.Logging.Log.LogCallback-=Capture;}
    }

    private static void FreeShop()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 91, 2));
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var run = RunState.CreateForTest([human, bot], seed: "SZG-PARASOL");
        var room = new MerchantRoom(); run.PushRoom(room);
        var stock = new MerchantInventory(bot); room.Inventories.Add(stock);
        bot.Gold = 0; human.Gold = 227;
        bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
        bot.AddRelicInternal(ModelDb.Relic<LordsParasol>().ToMutable());
        human.AddRelicInternal(ModelDb.Relic<LordsParasol>().ToMutable());
        var curse = run.CreateCard<Injury>(bot); bot.Deck.AddInternal(curse);
        for (var i = 0; i < 9; i++)
        {
            var card = run.CreateCard<StrikeIronclad>(bot);
            var entry = new MerchantCardEntry(bot, stock, [], CardType.Attack);
            typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(entry, new CardCreationResult(card));
            typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, 77);
            ((List<MerchantCardEntry>)stock.CharacterCardEntries).Add(entry);
        }
        T Entry<T>(object model) where T : MerchantEntry
        {
            var entry = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            typeof(MerchantEntry).GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, bot);
            typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, 200);
            typeof(T).GetProperty("Model")!.SetValue(entry, model);
            return entry;
        }
        stock.AddRelicEntry(Entry<MerchantRelicEntry>(ModelDb.Relic<BloodVial>().ToMutable()));
        ((List<MerchantPotionEntry>)stock.PotionEntries).Add(Entry<MerchantPotionEntry>(ModelDb.Potion<FruitJuice>().ToMutable()));
        for(var i=0;i<bot.MaxPotionCount;i++)PotionCmd.TryToProcure(ModelDb.Potion<BlockPotion>().ToMutable(),bot).GetAwaiter().GetResult();
        var removal = new MerchantCardRemovalEntry(bot);
        typeof(MerchantInventory).GetProperty("CardRemovalEntry")!.SetValue(stock, removal);
        Check(BotShopDriver.ParasolOwner(bot) && !BotShopDriver.ParasolOwner(human),
            "only a virtual parasol owner needs host compensation");
        var sync = typeof(RunManager).GetProperty("RewardSynchronizer")!;
        var previous = sync.GetValue(RunManager.Instance);
        sync.SetValue(RunManager.Instance, RuntimeHelpers.GetUninitializedObject(typeof(RewardSynchronizer)));
        var execute = typeof(BotShopDriver).GetMethod("Execute", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Execute(BotShopMessage message) => ((Task<bool>)execute.Invoke(null, [message, run])!).GetAwaiter().GetResult();
        var visited = new HashSet<int>(); var sequence = 0;
        string[] managerFields=["State","NetService","RunLocationTargetedBuffer","ActionQueueSet","ActionExecutor"];
        var previousFields=managerFields.ToDictionary(name=>name,name=>AccessTools.Property(typeof(RunManager),name).GetValue(RunManager.Instance));
        var net=(NetSingleplayerGameService)RuntimeHelpers.GetUninitializedObject(typeof(NetSingleplayerGameService));
        var queues=new ActionQueueSet(run.Players);
        AccessTools.Property(typeof(RunManager),"State").SetValue(RunManager.Instance,run);
        typeof(RunManager).GetProperty("NetService")!.SetValue(RunManager.Instance,net);
        typeof(RunManager).GetProperty("RunLocationTargetedBuffer")!.SetValue(RunManager.Instance,new RunLocationTargetedMessageBuffer(net));
        typeof(RunManager).GetProperty("ActionQueueSet")!.SetValue(RunManager.Instance,queues);
        typeof(RunManager).GetProperty("ActionExecutor")!.SetValue(RunManager.Instance,new ActionExecutor(queues));
        BotShopDriver.Initialize(RunManager.Instance);
        var pending=typeof(BotShopDriver).GetField("pending",BindingFlags.NonPublic|BindingFlags.Static)!;
        var replicated=new HashSet<int>();
        using var pump=new GamePump();
        try
        {
            var humanStock=new MerchantInventory(human);room.Inventories.Add(humanStock);
            var humanRelic=human.Relics.OfType<LordsParasol>().Single();
            var actualNative=(Task)AccessTools.Method(typeof(LordsParasol),"PurchaseEverything").Invoke(humanRelic,[humanStock])!;
            actualNative.GetAwaiter().GetResult();
            var observed=(System.Collections.IDictionary)typeof(BotShopDriver).GetField("nativeParasol",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
            Check(observed.Contains(human.NetId),"the installed native LordsParasol hook must observe the real owner's returned purchase task");
            var nativeWait=new TaskCompletionSource();
            BotShopDriver.ObserveNativeParasol(humanRelic,stock,nativeWait.Task);
            Check(!BotShopDriver.NativeParasolPending(room),"native task observation must reject a different owner's inventory");
            BotShopDriver.ObserveNativeParasol(humanRelic,humanStock,nativeWait.Task);
            Check(BotShopDriver.NativeParasolPending(room) && BotShopDriver.Tick(RunManager.Instance,run)
                && !BotShopDriver.Finished(run) && stock.CharacterCardEntries.All(e=>e.IsStocked),
                "ordinary driver and room completion must wait for a real owner's native free purchase task");
            nativeWait.SetResult();
            var deadline=Environment.TickCount64+45000;
            while (!BotShopDriver.Finished(run) && !BotShopDriver.Faulted && Environment.TickCount64<deadline)
            {
                BotShopDriver.Tick(RunManager.Instance,run);
                pump.Drain();
                if(pending.GetValue(null) is BotShopMessage message && replicated.Add(message.Sequence))
                {
                    var writer = new PacketWriter(); message.Serialize(writer);
                    var reader = new PacketReader(); reader.Reset(writer.Buffer); var decoded = new BotShopMessage(); decoded.Deserialize(reader);
                    Check(decoded.Parasol && decoded.Price==0 && decoded.Bot==bot.NetId && decoded.PotionDiscard==message.PotionDiscard,
                        "production parasol command must replicate its free purchase and full-belt replacement");
                    Check(!Execute(decoded), "a consumed or stale parasol command cannot grant twice");
                    visited.Add(message.Entry);sequence++;
                }
                Thread.Sleep(1);
            }
            Check(!BotShopDriver.Faulted && BotShopDriver.Finished(run) && sequence == 12 && bot.Gold == 0 && human.Gold == 227 && bot.Deck.Cards.Count == 9
                && !bot.Deck.Cards.Contains(curse) && removal.Used && bot.Relics.Any(r => r is BloodVial)
                && bot.Potions.Any(p => p is FruitJuice),
                "parasol must take all forced cards, relics, potion and mandatory removal beyond the ordinary eight-purchase cap");
            Check(BotShopDriver.NextParasol(stock, visited) is null, "parasol visit must settle once all stock is processed");
            var lateNative=new TaskCompletionSource();BotShopDriver.ObserveNativeParasol(humanRelic,humanStock,lateNative.Task);
            Check(!BotShopDriver.Finished(run),"a pending real-owner native task must hold an otherwise completed room");lateNative.SetResult();
            Check(BotShopDriver.Finished(run),"successful native completion must release the settled room");
            BotShopDriver.ObserveNativeParasol(humanRelic,humanStock,Task.FromException(new InvalidOperationException("native-purchase-probe")));
            BotShopDriver.Tick(RunManager.Instance,run);
            Check(BotShopDriver.Faulted && !BotShopDriver.Finished(run),"failed native purchase task must remain a reported fault, never successful completion");
            Console.WriteLine($"PASS: SZG virtual Lords Parasol buys all native stock once for zero gold ({sequence} transactions).");
        }
        finally
        {
            sync.SetValue(RunManager.Instance, previous);
            foreach(var pair in previousFields)AccessTools.Property(typeof(RunManager),pair.Key).SetValue(RunManager.Instance,pair.Value);
            typeof(BotShopDriver).GetField("manager",BindingFlags.NonPublic|BindingFlags.Static)!.SetValue(null,null);
            typeof(BotShopDriver).GetField("faulted",BindingFlags.NonPublic|BindingFlags.Static)!.SetValue(null,false);
        }
    }
    private sealed class GamePump:SynchronizationContext,IDisposable
    {
        private readonly SynchronizationContext? previous=Current;
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback,object? State)> queue=new();
        internal GamePump()=>SetSynchronizationContext(this);
        public override void Post(SendOrPostCallback callback,object? state)=>queue.Enqueue((callback,state));
        internal void Drain(){while(queue.TryDequeue(out var entry))entry.Callback(entry.State);}
        public void Dispose()=>SetSynchronizationContext(previous);
    }
}

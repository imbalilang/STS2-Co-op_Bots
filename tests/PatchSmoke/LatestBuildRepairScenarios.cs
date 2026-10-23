using System.Reflection;
using CoopBots;
using CoopBots.Building;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Live evidence: work/live-triage-20261007-latest/godot.log lines 1248/1250,
// 18803, 5186-5206 and 19034/19053. The latest run skipped every Massive
// Scroll card before valuation, omitted temporary draw/attack triggers, used
// flat Tablet upgrade benefits and charged one cheap Soot for repeated shuffles.
// R2 2026-10-07: twenty independent production regressions each exit 1, then
// their restored case exits 0. Raw red text (all System.InvalidOperationException):
// Latest build repair: native optional permanent single choice must evaluate and take a useful offer
// Latest build repair: Corrosive Wave must apply native poison only to three later draws
// Latest build repair: Rage must trigger per later attack card, never per damage hit
// Latest build repair: Tablet must value the uniform current native upgrade pool rather than rarity constants
// Latest build repair: Tablet must keep immediately executable 20 healing at full event value when injured
// Latest build repair: Biiig Hug must keep adding Soot across repeated post-removal shuffles
// Latest build repair: native Biiig Hug option must use the joint removal and ongoing shuffle package
// Latest build repair: Neow Ambergris reserve must share battle preservation value without a guessed extra-turn bonus
// corrosive-target-decay / printed-poison-targets each produced:
// System.InvalidOperationException: Latest build repair: all-target poison must merge on each target and decay once per target each turn
// Follow-up raw System.InvalidOperationException messages:
// Latest build repair: printed poison must keep future credit separate from immediate damage and a nonlethal native first tick
// Latest build repair: a lethal native first poison tick must prevent enemy attacks while preserving earlier held status damage
// Latest build repair: targeted poison must prevent only its own target's attack in a three-enemy encounter
// Latest build repair: future poison credit must stop at target capacity so an immediate kill avoids the earlier held status cost
// Latest build repair: Rage must include every native replay's generated Shiv attack plays
// Latest build repair: replayed generator bodies and their native generated attacks must both advance the Wither counter
// Latest build repair: discard continuations must finish the original source's native body replay count
// Latest build repair: autoplay continuations must finish the original source's native body replay count
// accelerant-primary-cap / accelerant-secondary-cap each produced:
// System.InvalidOperationException: Latest build repair: Accelerant must cap native poison ticks independently by each target's current stock
// Commands, exits and complete raw logs: work/decision-repair-20261007/build-repair-r2-results.json.
internal static class LatestBuildRepairScenarios
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Latest build repair: " + message); }
    private static bool combatMode;
    private static bool CombatMode(ref bool __result) { __result = combatMode; return false; }
    private static Player Fixture(params string[] ids)
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 79));
        typeof(Player).GetField("<Character>k__BackingField", BindingFlags.Instance|BindingFlags.NonPublic)!
            .SetValue(player, ModelDb.Character<Ironclad>());
        RunState.CreateForTest([player], ascensionLevel:10, seed:"LATEST-BUILD-REPAIR");
        player.MaxEnergy=3; player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
        foreach(var c in player.Deck.Cards.ToArray())player.Deck.RemoveInternal(c);
        foreach(var id in ids)player.Deck.AddInternal(Card(id,player));
        return player;
    }
    private static CardModel Card(string id,Player player)=>ConstructionParallelScenarios.Card(id,player);
    private static ConstructionHand.Slot Slot(string id,Player player)
    { var c=Card(id,player);var f=CardProfile.Of(c,3);return new(c,f,f.Unplayable); }
    private static AncientOptionValue.Estimate Price(Player player,RelicModel relic)
    {
        // Drive the SAME private payload scorer used by native option evaluation;
        // presentation/option factories are irrelevant to this price regression.
        var type=typeof(AncientOptionValue).GetNestedType("Context",BindingFlags.NonPublic)!;
        var context=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,[player],null)!;
        return (AncientOptionValue.Estimate)typeof(AncientOptionValue).GetMethod("Price",BindingFlags.Static|BindingFlags.NonPublic)!
            .Invoke(null,[context,relic])!;
    }
    private static double Part(AncientOptionValue.Estimate value,string label)=>double.Parse(
        value.Detail.Split(',').Single(s=>s.StartsWith(label+":",StringComparison.Ordinal))[(label.Length+1)..],
        System.Globalization.CultureInfo.InvariantCulture);

    internal static void Run(string? only=null)
    {
        TestEnvironment.Ensure();
        using var budget=ConstructionBudget.Use(ConstructionBudget.For(KernelCombatPlanner.TournamentPerf.Low));
        if(only is null or "choice")Choice();
        if(only is null or "triggers")Triggers();
        if(only is null or "tablet")Tablet();
        if(only is null or "shuffle")Shuffle();
        if(only is null or "ambergris")AmbergrisPrice();
    }

    internal static void Choice()
    {
        var player=Fixture("STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD",
            "DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","BASH");
        // Ordinary positive offer: the imported scoring no longer guarantees
        // Anger is beneficial here. Keep this off the key-card priority path.
        // Original package and rebuilt DLLs pass the updated whole scenario;
        // frozen v21 still passes the original fixture. Permanent-choice fault
        // RED1, exact source restore GREEN0: same task's r2-single-choice/proof.json.
        // RED: Latest build repair: native optional permanent single choice must evaluate and take a useful offer
        var offer=new[]{Card("POMMEL_STRIKE",player)};
        Check(BuildValue.AddForDecision(offer[0],player).Total>0,"fixture must offer a useful native permanent card");
        var before=ConstructionDecision.Fingerprint(player);
        var getter=AccessTools.PropertyGetter(typeof(CombatManager),"IsInProgress");
        var original=AccessTools.Method(typeof(TestEnvironment),"CombatInProgress");
        var harness=new Harmony("coopbots.test.console");
        var seam=new Harmony("coopbots.test.latest-build-choice");
        harness.Unpatch(getter,original);
        seam.Patch(getter,prefix:new HarmonyMethod(typeof(LatestBuildRepairScenarios),nameof(CombatMode)));
        try
        {
            combatMode=false;
            var method=typeof(CardSelectCmd).GetMethod(nameof(CardSelectCmd.FromChooseACardScreen))!;
            // R2: permanent-single (raw red text above).
            Check(BotCardChoiceDispatcher.TrySelect(method,[null!,offer,player,true],out var selected)
                && ReferenceEquals(selected,offer[0]),"native optional permanent single choice must evaluate and take a useful offer");
            Check(BotCardChoiceDispatcher.TrySelect(method,[null!,new[]{Card("GUILTY",player)},player,true],out var skipped)
                && skipped is null,"native optional permanent single choice must still skip a harmful offer");
            Check(BotBrain.SelectCards(player,[Card("GUILTY",player)],0,1,BotBrain.PermanentAddPurpose,maySkip:false).Count==1,
                "mandatory permanent single choice must still fill one slot");
            using var pump=new ConstructionParallelScenarios.Pump();
            Check(BotCardChoiceDispatcher.TrySelectAsync(method,[null!,offer,player,true],out var task)
                && ReferenceEquals(pump.Run(()=>task!),offer[0]),"async native permanent single choice must use the same valuation");
            combatMode=true;
            Check(BotCardChoiceDispatcher.SingleChoicePurpose(true)==nameof(CardSelectCmd.FromChooseACardScreen),
                "combat generated cards must retain combat choice semantics");
            Check(ConstructionDecision.Fingerprint(player)==before,"card offer preview must preserve native run and deck");
        }
        finally { seam.Unpatch(getter,AccessTools.Method(typeof(LatestBuildRepairScenarios),nameof(CombatMode)));
            harness.Patch(getter,prefix:new HarmonyMethod(original)); }
        Console.WriteLine("PASS: latest native optional permanent single choice evaluates useful/harmful offers and agrees asynchronously.");
    }

    internal static void Triggers()
    {
        var player=Fixture();
        var wave=Slot("CORROSIVE_WAVE",player);
        var draw=new[]{wave,Slot("ACROBATICS",player),Slot("GUILTY",player),Slot("STRIKE_SILENT",player),Slot("STRIKE_SILENT",player),Slot("DEFEND_SILENT",player)};
        var poison=ConstructionHand.Solve(draw,2,new(2,26,240));
        var nativePoison=(double)wave.Model!.Amount("CorrosiveWave");
        // The opening two cards precede the power, the three newly drawn cards
        // follow it. Native discard does not undo the already triggered poison.
        // R2: corrosive-draw / rage-attack (raw red text above).
        Check(Math.Abs(poison.TriggeredPoison-3*nativePoison)<1e-9,
            "Corrosive Wave must apply native poison only to three later draws");
        Check(ConstructionHand.Solve(draw,2,new(0,26,240)).TriggeredPoison==0,
            "unaffordable Corrosive Wave must not supply trigger value");
        Check(ConstructionHand.Solve(draw,draw.Length,new(2,26,240)).TriggeredPoison==0,
            "Corrosive Wave must not poison for cards already in the opening hand");
        var rage=Slot("RAGE",player);
        var attacks=new[]{rage,Slot("TWIN_STRIKE",player),Slot("STRIKE_IRONCLAD",player)};
        var block=ConstructionHand.Solve(attacks,3,new(3,26,240));
        Check(Math.Abs(block.TriggeredBlock-2*rage.Model!.Amount("Power"))<1e-9,
            "Rage must trigger per later attack card, never per damage hit");
        var generated=new[]{rage,Slot("BLADE_DANCE",player)};
        var shivs=ConstructionHand.Solve(generated,2,new(3,26,240));
        Check(Math.Abs(shivs.TriggeredBlock-generated[1].Model!.Amount("Cards")*rage.Model.Amount("Power"))<1e-9,
            "Rage must include the owner's generated Shiv attack plays");
        var replayed=Card("BLADE_DANCE",player);
        replayed.BaseReplayCount=1;
        var replayedFacts=CardProfile.Of(replayed,3);
        var nativeShivs=(double)replayed.DynamicVars["Cards"].BaseValue*(1+replayed.GetEnchantedReplayCount());
        var replayedHand=ConstructionHand.Solve([rage,new(replayed,replayedFacts)],2,new(3,26,240));
        Check(Math.Abs(replayedHand.TriggeredBlock-nativeShivs*rage.Model.Amount("Power"))<1e-9
            && Math.Abs(replayedFacts.Effects.GeneratedShivs-nativeShivs)<1e-9,
            "Rage must include every native replay's generated Shiv attack plays");
        PoisonTiming(player);
        ReplayedWither();
        AcceleratedPoison();
        var cycle=Enumerable.Range(0,4).SelectMany(_=>new[]{Card("CORROSIVE_WAVE",player),Card("ACROBATICS",player),Card("RAGE",player),Card("STRIKE_IRONCLAD",player)}).ToArray();
        var trace=SustainedDeckValue.EnduranceTrace(cycle,3);
        Check(trace.Sum(t=>t.TriggeredPoison)>0 && trace.Sum(t=>t.TriggeredBlock)>0,
            "temporary draw and attack triggers must reach the sustained construction trajectory");
        void PerTargetPoison(params string[] ids)
        {
            var deck=ids.Select(id=>Card(id,player)).ToArray();
            double primary=0,secondary=0;
            foreach(var turn in SustainedDeckValue.Estimate(deck,3,enemies:3))
            {
                primary+=turn.TriggeredPoison;
                secondary+=turn.TriggeredPoison;
                foreach(var card in deck.Where(c=>c.DynamicVars.ContainsKey("PoisonPower")))
                {
                    var added=(double)card.DynamicVars["PoisonPower"].BaseValue*turn.Plays[card];
                    primary+=added;
                    if(card.TargetType==MegaCrit.Sts2.Core.Entities.Cards.TargetType.AllEnemies)secondary+=added;
                }
                Check(Math.Abs(turn.PoisonDamage-(primary+2*secondary))<1e-9,
                    "all-target poison must merge on each target and decay once per target each turn");
                primary=Math.Max(0,primary-1);secondary=Math.Max(0,secondary-1);
            }
        }
        // Native PoisonPower decays ONE on EACH poisoned creature. Include
        // Corrosive Wave, printed all-target Haze, and a targeted source sharing
        // the primary target, to prevent both encounter-wide and double decay.
        // R2: corrosive-target-decay / printed-poison-targets (raw red above).
        PerTargetPoison("CORROSIVE_WAVE","ACROBATICS","STRIKE_SILENT","STRIKE_SILENT","DEFEND_SILENT","DEFEND_SILENT");
        PerTargetPoison("HAZE","DEFEND_SILENT","DEFEND_SILENT","STRIKE_SILENT","STRIKE_SILENT","DEFEND_SILENT");
        PerTargetPoison("HAZE","DEADLY_POISON","DEFEND_SILENT","STRIKE_SILENT","STRIKE_SILENT","DEFEND_SILENT");
        Console.WriteLine("PASS: latest Corrosive Wave/Rage construction uses post-play draws, attack plays, affordability and sustained outcomes.");
    }

    private static void ReplayedWither()
    {
        // CardModel emits AfterCardPlayed for EACH native PlayIndex. Drive the
        // real WitheringPresence hook, and BladeDance's real OnPlay generation,
        // without replacing either mechanic with a fixture counter.
        var player=Fixture();player.ResetCombatState();
        var combat=new CombatState(runState:player.RunState);combat.AddPlayer(player);
        var enemy=combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"wither-oracle");
        combat.AddCreature(enemy);enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(240);enemy.SetCurrentHpInternal(240);
        var presence=(WitheringPresencePower)ModelDb.Power<WitheringPresencePower>().ToMutable();
        presence.ApplyInternal(enemy,1,true);presence.Target=player.Creature;
        var source=combat.CreateCard<BladeDance>(player);source.BaseReplayCount=1;
        player.PlayerCombatState!.Hand.AddInternal(source);
        var choice=new ThrowingPlayerChoiceContext();
        CardPlay Play(CardModel card,int index,int count)=>new()
        { Card=card,Player=player,Target=null,ResultPile=PileType.Discard,Resources=default,
          IsAutoPlay=false,PlayIndex=index,PlayCount=count };
        presence.DynamicVars["CardsLeft"].BaseValue=2;
        var count=1+source.GetEnchantedReplayCount();
        for(var index=0;index<count;index++)
        {
            var play=Play(source,index,count);
            ((Task)typeof(BladeDance).GetMethod("OnPlay",BindingFlags.Instance|BindingFlags.NonPublic)!
                .Invoke(source,[choice,play])!).GetAwaiter().GetResult();
            presence.AfterCardPlayed(choice,play).GetAwaiter().GetResult();
        }
        var shivs=player.PlayerCombatState!.Hand.Cards.OfType<Shiv>().ToArray();
        foreach(var shiv in shivs)presence.AfterCardPlayed(choice,Play(shiv,0,1)).GetAwaiter().GetResult();
        var nativeGenerated=player.PlayerCombatState.Hand.Cards.Count(c=>c is Wither);
        var nativeCounter=6-presence.DynamicVars["CardsLeft"].IntValue;
        var macro=ConstructionHand.Solve([new(source,CardProfile.Of(source,1))],1,new(1,0,240,WitherDamage:3,Counter:4));
        Check(shivs.Length==6 && nativeGenerated==2 && nativeCounter==0
            && macro.Generated==nativeGenerated && macro.Counter==nativeCounter && macro.StatusDamage==nativeGenerated*3,
            "replayed generator bodies and their native generated attacks must both advance the Wither counter");
        // Same physical-play count for a replayed ordinary card: no generated
        // attacks to conceal a missing body replay in the common finish path.
        foreach(var held in player.PlayerCombatState.Hand.Cards.ToArray())player.PlayerCombatState.Hand.RemoveInternal(held);
        presence.DynamicVars["CardsLeft"].BaseValue=2;
        var strike=combat.CreateCard<StrikeSilent>(player);strike.BaseReplayCount=1;
        for(var index=0;index<1+strike.GetEnchantedReplayCount();index++)
            presence.AfterCardPlayed(choice,Play(strike,index,2)).GetAwaiter().GetResult();
        var hit=ConstructionHand.Solve([new(strike,CardProfile.Of(strike,1))],1,new(1,0,240,WitherDamage:3,Counter:4));
        Check(hit.Generated==player.PlayerCombatState.Hand.Cards.Count(c=>c is Wither)
            && hit.Generated==1 && hit.Counter==6-presence.DynamicVars["CardsLeft"].IntValue && hit.Counter==0,
            "ordinary card body replays must advance the same native Wither counter");
        // Finish runs after discard/Sly and autoplay continuations. Keep the
        // original source's physical replay count through those nested returns.
        var cleanup=Card("DAGGER_THROW",player);cleanup.BaseReplayCount=1;
        var cleanupHand=ConstructionHand.Solve([Slot("DAZED",player),new(cleanup,CardProfile.Of(cleanup,1))],2,
            new(1,0,240,WitherDamage:3,Counter:4));
        Check(cleanupHand.Generated==hit.Generated && cleanupHand.Counter==hit.Counter,
            "discard continuations must finish the original source's native body replay count");
        presence.DynamicVars["CardsLeft"].BaseValue=2;
        var havoc=Card("HAVOC",player);havoc.BaseReplayCount=1;
        presence.AfterCardPlayed(choice,Play(strike,0,1)).GetAwaiter().GetResult();
        for(var index=0;index<1+havoc.GetEnchantedReplayCount();index++)
            presence.AfterCardPlayed(choice,Play(havoc,index,2)).GetAwaiter().GetResult();
        var auto=ConstructionHand.Solve([new(havoc,CardProfile.Of(havoc,1)),Slot("STRIKE_SILENT",player)],1,
            new(1,0,240,WitherDamage:3,Counter:4));
        Check(auto.Generated==1 && auto.Counter==6-presence.DynamicVars["CardsLeft"].IntValue && auto.Counter==1,
            "autoplay continuations must finish the original source's native body replay count");
        Console.WriteLine("PASS: native WitheringPresence agrees with replayed generator bodies, generated attacks and ordinary body replays.");
    }

    private static void AcceleratedPoison()
    {
        // Native TriggerCount is min(Amount, 1+Accelerant), independently on
        // each enemy. Interpolate adjacent native integer stocks only where
        // this six-turn macro intentionally carries an expected fractional stock.
        double NativeTick(int amount)
        {
            if(amount==0)return 0;
            var player=Fixture();player.ResetCombatState();
            var combat=new CombatState(runState:player.RunState);combat.AddPlayer(player);
            ModelDb.Power<AccelerantPower>().ToMutable().ApplyInternal(player.Creature,2,true);
            var enemy=combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"accelerant-oracle");
            combat.AddCreature(enemy);enemy.Monster!.SetUpForCombat();enemy.SetMaxHpInternal(240);enemy.SetCurrentHpInternal(240);
            var power=(PoisonPower)ModelDb.Power<PoisonPower>().ToMutable();power.ApplyInternal(enemy,amount,true);
            return power.CalculateTotalDamageNextTurn();
        }
        Check(NativeTick(1)==1 && NativeTick(2)==3 && NativeTick(4)==9,"native low-stock Accelerant oracle must cap trigger count at available poison");
        double Expected(double stock)
        {
            var low=(int)Math.Floor(stock);var fraction=stock-low;
            return (1-fraction)*NativeTick(low)+fraction*NativeTick(low+1);
        }
        var owner=Fixture();
        var engine=Card("ACCELERANT",owner);engine.UpgradeInternal();engine.AddKeyword(CardKeyword.Innate);
        var all=Card("HAZE",owner);all.DynamicVars["PoisonPower"].BaseValue=1;
        var one=Card("DEADLY_POISON",owner);one.DynamicVars["PoisonPower"].BaseValue=2;
        var fillers=Enumerable.Range(0,6).Select(_=>Card("DEFEND_SILENT",owner)).ToArray();
        double primary=0,secondary=0;
        foreach(var turn in SustainedDeckValue.Estimate(fillers.Concat([engine,all,one]).ToArray(),3,enemies:3))
        {
            primary+=turn.Plays.GetValueOrDefault(all)+2*turn.Plays.GetValueOrDefault(one);
            secondary+=turn.Plays.GetValueOrDefault(all);
            Check(Math.Abs(turn.PoisonDamage-(Expected(primary)+2*Expected(secondary)))<1e-9,
                "Accelerant must cap native poison ticks independently by each target's current stock");
            primary=Math.Max(0,primary-3);secondary=Math.Max(0,secondary-3);
        }
        Console.WriteLine("PASS: native low-poison/high-Accelerant damage agrees with independent primary and secondary macro stocks.");
    }

    private static void PoisonTiming(Player player)
    {
        // Native PoisonPower's public next-turn damage is the oracle. Its
        // AfterSideTurnStart hook runs before enemy attack moves, while Burn's
        // native OnTurnEndInHand precedes the enemy side. A future two-tick
        // valuation must never substitute for this first-tick kill threshold.
        double NativeTick(int amount)
        {
            var oracle=Fixture();
            oracle.ResetCombatState();
            var combat=new CombatState(runState:oracle.RunState);
            combat.AddPlayer(oracle);
            var enemy=combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"poison-oracle");
            combat.AddCreature(enemy);enemy.Monster!.SetUpForCombat();
            enemy.SetMaxHpInternal(80);enemy.SetCurrentHpInternal(80);
            var power=(PoisonPower)ModelDb.Power<PoisonPower>().ToMutable();power.ApplyInternal(enemy,amount,true);
            return power.CalculateTotalDamageNextTurn();
        }
        var poison=Slot("DEADLY_POISON",player);
        var amount=(int)poison.Model!.Amount("PoisonPower");
        var tick=NativeTick(amount);
        var burn=Slot("BURN",player);
        burn=burn with { EndDamage=burn.Model!.Amount("Damage") };
        var almost=ConstructionHand.Solve([poison,burn],2,new(1,20,tick+1));
        Check(almost.Damage==0 && Math.Abs(almost.FirstPoisonTickDamage-tick)<1e-9
            && almost.EnemyAttackDamage==20,
            "printed poison must keep future credit separate from immediate damage and a nonlethal native first tick");
        var exact=ConstructionHand.Solve([poison,burn],2,new(1,20,tick));
        Check(exact.Damage==0 && Math.Abs(exact.FirstPoisonTickDamage-tick)<1e-9
            && exact.EnemyAttackDamage==0 && exact.StatusDamage==burn.EndDamage,
            "a lethal native first poison tick must prevent enemy attacks while preserving earlier held status damage");
        var instant=ConstructionHand.Solve([Slot("STRIKE_SILENT",player),burn],2,new(1,20,tick));
        Check(exact.Utility<instant.Utility,
            "future poison credit must stop at target capacity so an immediate kill avoids the earlier held status cost");
        var targeted=ConstructionHand.Solve([poison,burn],2,new(1,30,3*tick,3,
            EnemyHealth:[tick,tick,tick]));
        Check(targeted.EnemyAttackDamage==20 && Math.Abs(targeted.FirstPoisonTickDamage-tick)<1e-9,
            "targeted poison must prevent only its own target's attack in a three-enemy encounter");
        var haze=Slot("HAZE",player);
        var allTick=NativeTick((int)haze.Model!.Amount("PoisonPower"));
        var all=ConstructionHand.Solve([haze,burn],2,new(2,30,3*allTick,3,
            EnemyHealth:[allTick,allTick,allTick]));
        Check(all.Damage==0 && all.EnemyAttackDamage==0 && all.StatusDamage==burn.EndDamage
            && Math.Abs(all.FirstPoisonTickDamage-3*allTick)<1e-9,
            "all-target native first poison ticks must prevent all lethal targets' attacks without suppressing held statuses");
        var partial=ConstructionHand.Solve([haze,burn],2,new(2,30,3*allTick+1,3,
            EnemyHealth:[allTick,allTick,allTick+1]));
        // Haze also applies native Weak: the lone surviving 10-damage attack
        // retains its existing 25% reduction, rather than disappearing.
        Check(partial.EnemyAttackDamage==7.5 && Math.Abs(partial.FirstPoisonTickDamage-3*allTick)<1e-9,
            "all-target poison must preserve the surviving target's attack at its native first-tick boundary");
        var spray=Slot("DAGGER_SPRAY",player);
        var hit=spray.Facts!.Damage;
        var combined=ConstructionHand.Solve([haze,spray,burn],3,new(3,30,3*(allTick+hit)+1,3,
            EnemyHealth:[allTick+hit,allTick+hit,allTick+hit+1]));
        Check(Math.Abs(combined.Damage-3*hit)<1e-9 && combined.EnemyAttackDamage==7.5
            && combined.StatusDamage==burn.EndDamage && Math.Abs(combined.FirstPoisonTickDamage-3*allTick)<1e-9,
            "native poison must consume each target's remaining capacity after immediate AoE damage");
        Console.WriteLine("PASS: printed poison uses native first-tick boundaries, independent target capacities and earlier held statuses.");
    }

    internal static void Tablet()
    {
        var player=Fixture("STRIKE_IRONCLAD","DEFEND_IRONCLAD","BASH","POMMEL_STRIKE");
        using var evaluation=DeckStrength.BeginEvaluation();
        var expected=player.Deck.Cards.Where(c=>c.IsUpgradable).Average(c=>BuildValue.UpgradeForDecision(c,player).Total);
        // R2: tablet-pool (raw red text above).
        Check(Math.Abs(BotEventDriver.TabletRandomUpgradeValue(player)-expected)<1e-9,
            "Tablet must value the uniform current native upgrade pool rather than rarity constants");
        var saturated=Fixture("STRIKE_IRONCLAD");
        saturated.Deck.Cards[0].DynamicVars["Damage"].BaseValue=999;
        Check(!BotEventDriver.ShouldDecipherTablet(saturated,3,0),
            "Tablet must reject permanent MaxHp loss when the real random upgrade adds no encounter value");
        var useful=Fixture("BLUDGEON");
        Check(BotEventDriver.ShouldDecipherTablet(useful,3,0),
            "Tablet must still accept a real upgrade worth the cheap permanent loss");
        var wounded=Fixture("STRIKE_IRONCLAD");
        wounded.Creature.SetCurrentHpInternal(60);
        // R2: tablet-immediate-heal (raw red text above). The original
        // TabletEventScenarios healing guard also stays green without edits.
        Check(!BotEventDriver.ShouldDecipherTablet(wounded,3,20),
            "Tablet must keep immediately executable 20 healing at full event value when injured");
        Check(BotEventDriver.TabletMaxHpCost(player,3)>=3 && BotEventDriver.TabletMaxHpCost(player,6)>=6,
            "Tablet permanent capacity loss must never be priced as less than its native MaxHp reduction");
        Console.WriteLine("PASS: latest Tablet evaluates the real random pool, rejects valueless upgrades and prices permanent MaxHp capacity.");
    }

    internal static void Shuffle()
    {
        var player=Fixture("STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD","STRIKE_IRONCLAD",
            "DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","BASH","POMMEL_STRIKE","IRON_WAVE","SHRUG_IT_OFF");
        var before=ConstructionDecision.Fingerprint(player);
        var deck=player.Deck.Cards.ToArray();
        IReadOnlyList<SustainedDeckValue.TurnEstimate> small, large;
        using(ShuffleStatusContext.Use(player,true))
        { small=SustainedDeckValue.Estimate(deck.Take(8).ToArray(),3);large=SustainedDeckValue.Estimate(deck,3); }
        // R2: repeated-soot / hug-package (raw red text above).
        Check(small.Sum(t=>t.ShuffleStatusesGenerated)>=2,
            "Biiig Hug must keep adding Soot across repeated post-removal shuffles");
        Check(small.Sum(t=>t.ShuffleStatusesGenerated)>large.Sum(t=>t.ShuffleStatusesGenerated),
            "a smaller post-removal deck must reach the next Soot shuffle sooner");
        Check(small.All(t=>t.StatusDamage==0),"Soot must dilute draws without invented hand-end damage");
        using var evaluation=DeckStrength.BeginEvaluation();
        var hug=AncientOptionValue.BiiigHugPackage(player,4);
        var after=deck.Except(hug.Targets).ToArray();
        var clean=DeckStrength.WithShuffleSoot(after,player,false).Total;
        var polluted=DeckStrength.WithShuffleSoot(after,player,true).Total;
        Check(hug.Targets.Count==4 && Math.Abs(hug.Soot-(polluted-clean))<1e-9,
            "Biiig Hug must jointly price all four removals before their ongoing shuffle cost");
        var priced=Price(player,ModelDb.Relic<BiiigHug>().ToMutable());
        Check(Math.Abs(priced.Value-hug.Total)<1e-9
            && Math.Abs(Part(priced,"soot-after-removals-on-every-shuffle")-hug.Soot)<.011,
            "native Biiig Hug option must use the joint removal and ongoing shuffle package");
        IReadOnlyList<CardModel> selected;
        using(ShuffleStatusContext.Use(player,true))
            selected=BotBrain.SelectCards(player,deck.Where(c=>c.IsRemovable),4,4,"FromDeckForRemoval");
        Check(selected.SequenceEqual(hug.Targets),"Biiig Hug quote and native removal screen must choose the same four targets");
        Check(ConstructionDecision.Fingerprint(player)==before,"Hug quote must preserve live deck/relic/HP/RNG state");
        Console.WriteLine($"PASS: latest Biiig Hug jointly removes four cards and prices repeated Soot on the remaining deck ({hug.Soot:F2}).");
    }

    internal static void AmbergrisPrice()
    {
        var player=Fixture("STRIKE_IRONCLAD","DEFEND_IRONCLAD","BASH");
        var potion=ModelDb.Potion<Ambergris>().ToMutable();
        var expected=KernelPotionPolicy.PreservationValue(potion,player.RunState.Players);
        // R2: ancient-ambergris (raw red text above).
        var value=Price(player,ModelDb.Relic<NeowsSacrifice>().ToMutable());
        Check(Math.Abs(Part(value,"ambergris-reserve")-expected)<.011,
            "Neow Ambergris reserve must share battle preservation value without a guessed extra-turn bonus");
        player.Creature.SetMaxHpInternal(120);
        var larger=Price(player,ModelDb.Relic<NeowsSacrifice>().ToMutable());
        Check(Math.Abs(Part(larger,"ambergris-reserve")-KernelPotionPolicy.PreservationValue(potion,player.RunState.Players))<.011,
            "Ambergris reserve must follow native healing percentage and party MaxHp");
        Console.WriteLine("PASS: latest ancient Ambergris uses the same native healing reservation as battle policy.");
    }
}

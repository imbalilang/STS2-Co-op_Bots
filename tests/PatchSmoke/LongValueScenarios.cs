using CoopBots;
using CoopBots.Building;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// R2 2026-10-03: each production regression failed, then was restored.
// System.Exception: Long value: Kill must preserve both legal Vulnerable setups before Dominate
// System.Exception: Long value: Pyre must earn modeled energy output, not unknown-card compensation
// System.Exception: Long value: mixed-deck tactical rollout must invest in Pyre before spending its activation energy
// System.Exception: Long value: full tournament must select a native Pyre investment route
// System.Exception: Long value: sustained hands must not spend energy from undrawn or unplayed producers
// System.Exception: Long value: unsupported complex cards must not receive an invented base reward
internal static class LongValueScenarios
{
    private static bool Format(ref string __result) { __result=""; return false; }
    private static void Check(bool ok,string message) { if(!ok) throw new Exception("Long value: "+message); }
    internal static void Run()
    {
        TestEnvironment.Ensure();
        new Harmony("coopbots.long-value").Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText"),
            prefix:new HarmonyMethod(typeof(LongValueScenarios),nameof(Format)));
        var players=Enumerable.Range(1,2).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,(ulong)i)).ToArray();
        var run=RunState.CreateForTest(players,seed:"LONG-VALUE"); var c=new CombatState(runState:run);
        foreach(var p in players) { p.ResetCombatState();c.AddPlayer(p);p.MaxEnergy=3;
            p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3); }
        var e=c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"0");c.AddCreature(e);
        e.Monster!.SetUpForCombat();e.SetMaxHpInternal(600);e.SetCurrentHpInternal(600);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);
        var owner=players[0];
        CardModel Add(CardModel card,Player p,bool hand=true) {var x=c.CreateCard(card,p);(hand?p.PlayerCombatState!.Hand:p.PlayerCombatState!.DrawPile).AddInternal(x);return x;}
        var pyre=Add(ModelDb.Card<Pyre>(),owner);
        Add(ModelDb.Card<PommelStrike>(),owner);Add(ModelDb.Card<StrikeIronclad>(),owner);
        foreach(var card in new CardModel[]{ModelDb.Card<Cascade>(),ModelDb.Card<Panache>(),ModelDb.Card<CrimsonMantle>(),ModelDb.Card<Taunt>(),ModelDb.Card<Break>(),ModelDb.Card<PommelStrike>(),ModelDb.Card<Bash>(),ModelDb.Card<StrikeIronclad>()}) Add(card,owner,false);
        var root=KernelSession.Capture(c);var after=root.CardBranches(pyre,null,1).First().State;
        var line=KernelRollout.Run(root.Fork(),players,players,new(RolloutPolicyKind.Tactical,MaxSteps:1));
        Check(line.Actions.FirstOrDefault()?.Card is Pyre,"mixed-deck tactical rollout must invest in Pyre before spending its activation energy");
        Check(KernelTournament.FirstActions(root,players,2,new TournamentOptions(IncludePotions:false)).Any(a=>a.Card is Pyre),
            "power opening must survive the actual top-K budget");
        Console.WriteLine($"LONG PYRE value={after.PersistentValue(owner,10,1)-root.PersistentValue(owner,10,1):F2} first={line.Actions.FirstOrDefault()?.Card?.Id.Entry}");
        var tournament=KernelTournament.Run(root.Fork(),players,players,new TournamentOptions(TopK:6,ReserveMs:0,IncludePotions:false,
            Rollout:new RolloutOptions(MaxSteps:100,MaxRounds:12)),budgetMs:5000);
        Console.WriteLine($"STRATEGIC OLD FIXTURE first={tournament.FirstAction?.Card?.Id.Entry} pyres={tournament.Script?.Count(a=>a.Card is Pyre)} victory={tournament.Record?.Victory} rollouts={tournament.Rollouts} stop={tournament.StopReason}");
        // Strike can legally precede Pyre while leaving its two energy available.
        // The invariant is investment in the opening turn, not a fixed first card.
        Check(tournament.Script?.TakeWhile(a=>!a.EndTurn).Any(a=>a.Card is Pyre)==true
            && tournament.Record is { Verified:true, Victory:true },
            "full tournament must select a native Pyre investment route");
        Console.WriteLine($"LONG TOURNAMENT first={tournament.FirstAction?.Card?.Id.Entry} pyre={tournament.Script?.Count(a=>a.Card is Pyre)} rollouts={tournament.Rollouts} ending={tournament.Record?.Victory}");
        foreach(var p in players) foreach(var card in p.PlayerCombatState!.Hand.Cards.ToArray()) p.PlayerCombatState.Hand.RemoveInternal(card);
        var dominate=Add(ModelDb.Card<Dominate>(),owner);Add(ModelDb.Card<StrikeIronclad>(),owner);
        Add(ModelDb.Card<Taunt>(),players[1]);Add(ModelDb.Card<Thunderclap>(),players[1]);
        foreach(var policy in Enum.GetValues<RolloutPolicyKind>()) {
            var path=KernelRollout.Run(KernelSession.Capture(c),players,players,new(policy,MaxSteps:4));
            var sequence=path.Actions.Select(a=>a.Card).ToArray();
            Console.WriteLine($"LONG DOMINATE {policy}: "+string.Join(",",path.Actions.Select(a=>a.Card?.Id.Entry??"END")));
            Check(Array.FindIndex(sequence,a=>a is Dominate)>Array.FindIndex(sequence,a=>a is Taunt)
                && Array.FindIndex(sequence,a=>a is Dominate)>Array.FindIndex(sequence,a=>a is Thunderclap),
                policy+" must preserve both legal Vulnerable setups before Dominate");
        }
        e.SetCurrentHpInternal(6);
        var lethal=KernelRollout.Run(KernelSession.Capture(c),players,players,new(RolloutPolicyKind.Tactical,MaxSteps:1));
        Check(lethal.Actions.FirstOrDefault()?.Card is StrikeIronclad,"immediate lethal must still beat long-term setup");
        e.SetCurrentHpInternal(600);
        var deck=Enumerable.Range(0,5).Select(_=>c.CreateCard<StrikeIronclad>(owner)).Cast<CardModel>().Concat(Enumerable.Range(0,4).Select(_=>c.CreateCard<DefendIronclad>(owner))).ToArray();
        Check(!SustainedDeckValue.Unsupported(pyre),"Pyre must have a sustained construction model");
        var plain=DeckStrength.Evaluate(deck,owner);var invested=DeckStrength.Evaluate(deck.Append(pyre).ToArray(),owner);
        Check(invested.Combat>plain.Combat && invested.Unmodeled==0,"Pyre must earn modeled energy output, not unknown-card compensation");
        Check(DeckStrength.Evaluate(deck.Append(c.CreateCard<Metamorphosis>(owner)).ToArray(),owner).Unmodeled==0,
            "unsupported complex cards must not receive an invented base reward");
        var blood=c.CreateCard<Bloodletting>(owner);var empty=c.CreateCard<Bloodletting>(owner);empty.DynamicVars["Energy"].BaseValue=0;
        Check(DeckStrength.Evaluate(deck.Append(blood).ToArray(),owner).Combat>DeckStrength.Evaluate(deck.Append(empty).ToArray(),owner).Combat,
            "Bloodletting energy must unlock actual paid plays");
        var sample=new[]{new PayableHands.Card(ConstructionCard.Capture(blood),CardProfile.Of(blood),1),
            new PayableHands.Card(ConstructionCard.Capture(deck[0]),CardProfile.Of(deck[0]),1)};
        var use=PayableHands.Evaluate(sample,2,2,0,0,100,1,1,0);
        Check(use.Plays[0]>0 && use.Plays[1]>0 && Math.Abs(use.Refunded-2)<.001,"energy producer must pay HP and refund exactly once");
        var energyDeck=Enumerable.Range(0,8).Select(_=>c.CreateCard<Bash>(owner)).Cast<CardModel>().Append(blood).ToArray();
        foreach(var turn in SustainedDeckValue.OutcomeEstimate(energyDeck,3))
            Check(turn.EnergySpent<=turn.EnergyAvailable+.001,"sustained hands must not spend energy from undrawn or unplayed producers");
        foreach(var player in players)
        foreach(var pile in new[]{player.PlayerCombatState!.Hand,player.PlayerCombatState.DrawPile})
            foreach(var card in pile.Cards.ToArray()) pile.RemoveInternal(card);
        var freePyre=Add(ModelDb.Card<Pyre>(),owner);
        for(var i=0;i<8;i++)Add(ModelDb.Card<Anger>(),owner,false);
        var freeRoot=KernelSession.Capture(c);var freeAfter=freeRoot.CardBranches(freePyre,null,1).First().State;
        Check(Math.Abs(freeAfter.PersistentValue(owner,10,1)-freeRoot.PersistentValue(owner,10,1))<.001,
            "a free-only deck must not invent Pyre energy demand");
        Console.WriteLine("PASS: long-value routes deploy Pyre, order Dominate after allied setup, model energy and remove unknown-card dividends.");
        Console.WriteLine("LONG DRAFT PYRE "+DeckStrength.Change(deck,deck.Append(pyre).ToArray(),owner));
        Console.WriteLine("LONG DRAFT BLOOD "+DeckStrength.Change(deck,deck.Append(c.CreateCard<Bloodletting>(owner)).ToArray(),owner));
    }
}

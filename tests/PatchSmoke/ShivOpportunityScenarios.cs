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
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class ShivOpportunityScenarios
{
    // R2 V17: four independent production reversions failed (exit 1), then were restored.
    // Raw commands/output: work/strength-20260927-v17/r2-results.json.
    // System.Exception: Realized Shiv damage must not pay a second charge for spent hand, Vulnerable or unchanged Afterimage potential: left=2, gain=1.910.
    // System.Exception: Realized Shiv damage must not pay a second charge for spent hand, Vulnerable or unchanged Afterimage potential: left=1, gain=1.910.
    // System.Exception: Realized Shiv damage must not pay a second charge for spent hand, Vulnerable or unchanged Afterimage potential: left=1, gain=1.010.
    // System.Exception: End-turn recovery must recognize the same profitable Shivs: left=2.
    private static bool Format(ref string __result){__result="";return false;}
    internal static void Run()
    {
        new Harmony("coopbots.shiv.opportunity.presentation").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText"),
            prefix:new HarmonyMethod(typeof(ShivOpportunityScenarios),nameof(Format)));
        var errors=new List<string>();
        for(var left=1;left<=6;left++)
        {
            // V16 A10NEW92701 R3: six Shivs, five energy, Afterimage/Fan of
            // Knives active, no enemy block, buff intent. These are the actual
            // host piles; the fixture isolates its payoff from other seats.
            var p=Player.CreateForNewRun<Deprived>(UnlockState.all,1);
            var run=RunState.CreateForTest([p],seed:"SHIV-PAYOFF");var combat=new CombatState(runState:run);
            p.ResetCombatState();combat.AddPlayer(p);p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);
            p.Creature.GainBlockInternal(1);p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(5);
            for(var i=0;i<25;i++)p.Deck.AddInternal(run.CreateCard<StrikeSilent>(p));
            Add(p.PlayerCombatState.Hand,"SHIV DEFEND_SILENT DEFEND_SILENT SHIV SHIV SHIV SHIV SHIV DEFEND_SILENT DEFLECT");
            Add(p.PlayerCombatState.DrawPile,"STRIKE_SILENT EXTERMINATE SUCKER_PUNCH+ DEFEND_SILENT DEFEND_SILENT SURVIVOR CALCULATED_GAMBLE+ BACKFLIP PRECISE_CUT");
            Add(p.PlayerCombatState.DiscardPile,"DEFLECT+ SLICE+ STRIKE_SILENT STRIKE_SILENT NEUTRALIZE OUTRAGE WITHER DASH+ SIDESTEP+ WITHER SUCKER_PUNCH OUTRAGE+");
            var foe=combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"0");
            combat.AddCreature(foe);foe.Monster!.SetUpForCombat();foe.SetMaxHpInternal(2782);foe.SetCurrentHpInternal(2217);
            foe.Monster.SetMoveImmediate(new MoveState("BUFF",_=>Task.CompletedTask,new SingleAttackIntent(0)),true);
            var aura=ModelDb.Power<WitheringPresencePower>().ToMutable();aura.Target=p.Creature;aura.ApplyInternal(foe,6,true);aura.DynamicVars["CardsLeft"].BaseValue=left;
            ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(foe,6,true);
            ModelDb.Power<AfterimagePower>().ToMutable().ApplyInternal(p.Creature,1,true);
            ModelDb.Power<InfiniteBladesPower>().ToMutable().ApplyInternal(p.Creature,1,true);
            ModelDb.Power<FanOfKnivesPower>().ToMutable().ApplyInternal(p.Creature,1,true);
            var root=KernelSession.Capture(combat);var shiv=root.Hand(p).First(c=>c is Shiv);
            var played=root.CardBranches(shiv,null,1).First(b=>b.Boundary.Length==0).State;
            var damage=root.Hp(foe)-played.Hp(foe);
            var cost=played.WitheringBurden(p)-root.WitheringBurden(p);
            var threat=played.Threat(p).HpLoss-root.Threat(p).HpLoss;
            var gain=KernelTacticalPolicy.ActionValue(root,played,[p],[p])-KernelTacticalPolicy.Value(root,[p],[p]);
            if(damage!=6 || Math.Abs(cost-.9)>1e-8)errors.Add("The live-style Shiv fixture must realize six damage and one sixth of future pollution.");
            if(gain+1e-8<4.8-.9-threat*1.3)
                errors.Add($"Realized Shiv damage must not pay a second charge for spent hand, Vulnerable or unchanged Afterimage potential: left={left}, gain={gain:F3}.");
            if(!root.HasBeneficialAttack(p))errors.Add($"End-turn recovery must recognize the same profitable Shivs: left={left}.");
            foreach(var policy in Enum.GetValues<RolloutPolicyKind>())
            {
                // V18 may also spend useful defence against the generated Wither.
                // Observe the actual pass, not an artificial eight-action cutoff.
                var line=KernelRollout.Run(root.Fork(),[p],[p],new(policy,MaxSteps:24));
                var shivs=line.Actions.TakeWhile(a=>!a.EndTurn).Count(a=>a.Card is Shiv);
                if(shivs!=6)errors.Add($"{policy} must play all six profitable Shivs before passing: left={left}, played={shivs}.");
            }
            if(p.PlayerCombatState.Energy!=5 || root.Hand(p).Count(c=>c is Shiv)!=6 || foe.CurrentHp!=2217)
                errors.Add("Shiv evaluation must not consume live energy, cards or enemy HP.");

            void Add(CardPile pile,string names)
            {
                foreach(var name in names.Split(' '))
                {
                    var card=combat.CreateCard(ModelDb.AllCards.First(c=>c.Id.Entry==name.TrimEnd('+')),p);
                    if(name.EndsWith('+'))card.UpgradeInternal();pile.AddInternal(card);
                }
            }
        }
        if(errors.Count>0)throw new Exception(string.Join(Environment.NewLine,errors));
        Console.WriteLine("PASS: all policies spend six profitable Shivs at every Wither counter position without double-charging their realized payoff.");
    }
}

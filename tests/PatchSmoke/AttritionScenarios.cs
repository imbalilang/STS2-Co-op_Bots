using CoopBots;
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
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class AttritionScenarios
{
    // R2 V15: restored V14's unconditional early Pass coverage; exit 1:
    // System.Exception: A profitable play must precede optional passing when only one opening can be tested.
    // Raw command/output: work/strength-20260927-v15/r2-results.json.
    private static bool Format(ref string __result) { __result = ""; return false; }
    // R2 2026-09-28: 25 deliberate production faults exited 1; source restored.
    // Commands/full raw failures: work/strength-20260927-v14/r2-results.json.
    // System.Exception: Three upgraded Withers must consume 36 block before the thirty-point enemy attack.
    // Tactical must discard lethal end-turn status before optional damage.
    // Block potion must count twelve useful points against hand damage even without a large attack.
    // Tactical must stop a low-value sixth card when its generated Wither costs more than its damage.
    // System.Exception: Unplayable status cards must not receive usable-hand credit.
    // System.Exception: Tactical must evaluate team mitigation despite a hand full of higher printed damage.
    // System.Exception: Opening candidate coverage must retain team mitigation at TopK two.
    // System.Exception: The selected cleanup must replay its choice and actually remove Wither from hand.
    // System.Exception: Tactical must stop a low-value sixth card when its generated Wither costs more than its damage.
    // Tactical must decline a one-point block payoff even before the sixth card: left=1.
    // Tactical must decline a one-point block payoff even before the sixth card: left=2.
    // Tactical must decline a one-point block payoff even before the sixth card: left=3.
    // Tactical must decline a one-point block payoff even before the sixth card: left=4.
    // Tactical must decline a one-point block payoff even before the sixth card: left=5.
    // Tactical must decline a one-point block payoff even before the sixth card: left=6.
    // System.Exception: Status pressure must never discourage an immediate killing card.
    // System.Exception: Block potion must count twelve useful points against hand damage even without a large attack.
    // System.Exception: A full-health loss ten turns away must not force an opening choice potion.
    // System.Exception: An imminent verified death must release a useful choice potion.
    // System.Exception: Current lethal damage must release a useful choice potion even in a longer forecast.
    // System.Exception: Twenty-card Afterimage startup must outrank the routine Sucker Punch upgrade.
    // Upgrade investment must scale with remaining fights and survival prospects.
    // System.Exception: Every counted play must accrue one sixth of future Wither burden, including rollover: left=1.
    // Every counted play must accrue one sixth of future Wither burden, including rollover: left=2.
    // Kill must decline a one-point block payoff even before the sixth card: left=2.
    // Defend must decline a one-point block payoff even before the sixth card: left=2.
    // Growth must decline a one-point block payoff even before the sixth card: left=2.
    // Defend must still play damage worth more than its fractional pollution cost.
    // Every counted play must accrue one sixth of future Wither burden, including rollover: left=3.
    // Kill must decline a one-point block payoff even before the sixth card: left=3.
    // Defend must decline a one-point block payoff even before the sixth card: left=3.
    // Growth must decline a one-point block payoff even before the sixth card: left=3.
    // Every counted play must accrue one sixth of future Wither burden, including rollover: left=4.
    // Kill must decline a one-point block payoff even before the sixth card: left=4.
    // Defend must decline a one-point block payoff even before the sixth card: left=4.
    // Growth must decline a one-point block payoff even before the sixth card: left=4.
    // Every counted play must accrue one sixth of future Wither burden, including rollover: left=5.
    // Kill must decline a one-point block payoff even before the sixth card: left=5.
    // Defend must decline a one-point block payoff even before the sixth card: left=5.
    // Growth must decline a one-point block payoff even before the sixth card: left=5.
    // Every counted play must accrue one sixth of future Wither burden, including rollover: left=6.
    // Kill must decline a one-point block payoff even before the sixth card: left=6.
    // Defend must decline a one-point block payoff even before the sixth card: left=6.
    // Growth must decline a one-point block payoff even before the sixth card: left=6.
    // One replay must accrue two sixths of Wither burden rather than one manual-card charge.
    // Kill must decline a one-point block payoff even before the sixth card: left=1.
    // Defend must decline a one-point block payoff even before the sixth card: left=1.
    // Growth must decline a one-point block payoff even before the sixth card: left=1.
    // System.Exception: Kill must decline a one-point block payoff even before the sixth card: left=1.
    // System.Exception: Search must offer passing under fractional status pressure before the hand is empty.
    // Search must offer passing under fractional status pressure before the hand is empty.
    private static readonly List<string> Failures = [];
    private static void Check(bool ok, string why) { if (!ok) Failures.Add(why); }
    internal static void Run()
    {
        Failures.Clear();
        new Harmony("coopbots.test.attrition.presentation").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(AttritionScenarios), nameof(Format)));
        var (one, battle, foe) = Board(1, 30);
        one[0].Creature.SetCurrentHpInternal(25); one[0].Creature.GainBlockInternal(40);
        for (var i=0;i<3;i++) { var w=Hand<Wither>(one[0],battle); for(var j=0;j<3;j++) w.FakeUpgrade(); }
        var root = KernelSession.Capture(battle);
        var threat = root.Threat(one[0]);
        Console.WriteLine($"PROBE wither {threat}");
        Check(threat.HandDamage == 36 && threat.AttackDamage == 30 && threat.HpLoss == 26,
            "Three upgraded Withers must consume 36 block before the thirty-point enemy attack.");
        var end=root.Fork();
        Check(end.EndTurn(one[0], 1, out _) && end.Hp(one[0].Creature)==0,
            "The Wither threat fixture must actually die through forty block in end-turn simulation.");
        Check(root.UsefulHandCount(one[0])==0, "Unplayable status cards must not receive usable-hand credit.");
        Console.WriteLine("PASS: upgraded status damage precedes enemy attacks and unusable hand cards receive no credit.");

        var (team, c, e)=Board(4,30);
        ModelDb.Power<WeakPower>().ToMutable().ApplyInternal(e,2,true);
        var wail=Hand<PiercingWail>(team[1],c); wail.UpgradeInternal();
        // Nine distinct high-damage candidates reproduce the old top-eight blind spot.
        foreach(var p in team) { Hand<Bludgeon>(p,c); Hand<MoltenFist>(p,c); Hand<PommelStrike>(p,c); }
        var before=KernelSession.Capture(c);
        var reduced=before.CardBranches(wail,null,1).First(b=>b.Boundary.Length==0).State;
        Console.WriteLine($"PROBE wail threat {before.Threat(team[0])} -> {reduced.Threat(team[0])}");
        Check(team.Sum(p=>before.Threat(p).HpLoss-reduced.Threat(p).HpLoss)==24,
            "Upgraded Wail against thirty raw damage and Weak must save twenty-four party HP.");
        var line=KernelRollout.Run(before.Fork(),team,team,new(RolloutPolicyKind.Tactical,MaxSteps:1));
        Console.WriteLine($"PROBE tactical first {line.Actions.FirstOrDefault()?.Card?.Id.Entry}");
        Check(line.Actions.FirstOrDefault()?.Card is PiercingWail,
            "Tactical must evaluate team mitigation despite a hand full of higher printed damage.");
        Check(KernelTournament.FirstActions(before,team,2,new()).Any(a=>a.Card is PiercingWail),
            "Opening candidate coverage must retain team mitigation at TopK two.");
        Console.WriteLine("PASS: team mitigation survives tactical and tournament candidate limits.");

        var (clean, cb, ce)=Board(1,10);
        clean[0].Creature.SetCurrentHpInternal(15);
        var survivor=Hand<Survivor>(clean[0],cb);
        var wither=Hand<Wither>(clean[0],cb); for(var j=0;j<3;j++) wither.FakeUpgrade();
        Hand<Bludgeon>(clean[0],cb); Hand<MoltenFist>(clean[0],cb); Hand<StrikeIronclad>(clean[0],cb);
        var cleanRoot=KernelSession.Capture(cb);
        var cleanLine=KernelRollout.Run(cleanRoot.Fork(),clean,clean,new(RolloutPolicyKind.Tactical,MaxSteps:1));
        Console.WriteLine($"PROBE cleanup first {cleanLine.Actions.FirstOrDefault()?.Card?.Id.Entry}");
        Check(cleanLine.Actions.FirstOrDefault()?.Card is Survivor,
            "Tactical must discard lethal end-turn status before optional damage.");
        var replay=cleanRoot.Fork();
        Check(cleanLine.Actions.Count>0 && replay.ReplayPlanned(cleanLine.Actions[0],2,out _)
            && replay.Threat(clean[0]).HandDamage==0,
            "The selected cleanup must replay its choice and actually remove Wither from hand.");
        Console.WriteLine("PASS: cleanup choices replay and reduce the actual harmful hand.");

        var (pots,pb,pe)=Board(1,1);
        var burn=Hand<Wither>(pots[0],pb);for(var j=0;j<3;j++)burn.FakeUpgrade();
        var bottle=ModelDb.Potion<BlockPotion>().ToMutable();pots[0].AddPotionInternal(bottle,silent:true);
        var measure=KernelPotionOpportunity.Measure(KernelSession.Capture(pb),bottle,pots[0].Creature,pots);
        Check(measure.FullValue && measure.Benefit==12,
            "Block potion must count twelve useful points against hand damage even without a large attack.");
        var (future,fb,fe)=Board(1,1);
        var chooser=ModelDb.Potion<AttackPotion>().ToMutable();future[0].AddPotionInternal(chooser,silent:true);
        var forecast=new TerminalRecord(false,EvidenceKind.VerifiedTerminal,"",[0],1,0,RoundsSurvived:10);
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(fb),future,future,forecast) is null,
            "A full-health loss ten turns away must not force an opening choice potion.");
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(fb),future,future,forecast with {RoundsSurvived=1})?.Action.Potion==chooser,
            "An imminent verified death must release a useful choice potion.");
        future[0].Creature.SetCurrentHpInternal(1);
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(fb),future,future,forecast)?.Action.Potion==chooser,
            "Current lethal damage must release a useful choice potion even in a longer forecast.");
        Console.WriteLine("PASS: potion timing counts status damage and separates future loss from the last chance.");

        var (six,sb,se)=Board(1,1);
        var presence=ModelDb.Power<WitheringPresencePower>().ToMutable();
        presence.Target=six[0].Creature;presence.ApplyInternal(se,1,true);
        presence.DynamicVars["CardsLeft"].BaseValue=1;
        ModelDb.Power<WeakPower>().ToMutable().ApplyInternal(six[0].Creature,1,true);
        Hand<StrikeIronclad>(six[0],sb);
        var stop=KernelRollout.Run(KernelSession.Capture(sb),six,six,new(RolloutPolicyKind.Tactical,MaxSteps:1));
        Check(stop.Actions.All(a=>a.Card is null),
            "Tactical must stop a low-value sixth card when its generated Wither costs more than its damage.");
        se.SetCurrentHpInternal(4);
        var kill=KernelRollout.Run(KernelSession.Capture(sb),six,six,new(RolloutPolicyKind.Tactical,MaxSteps:1));
        Check(kill.Actions.FirstOrDefault()?.Card is StrikeIronclad,
            "Status pressure must never discourage an immediate killing card.");
        Console.WriteLine("PASS: sixth-card status cost can justify stopping while lethal still wins.");
        for(var left=1;left<=6;left++)
        {
            var (fair,fc,foe2)=Board(1,0);
            var aura=ModelDb.Power<WitheringPresencePower>().ToMutable();
            aura.Target=fair[0].Creature;aura.ApplyInternal(foe2,1,true);aura.DynamicVars["CardsLeft"].BaseValue=left;
            var defend=Hand<DefendIronclad>(fair[0],fc);
            var start=KernelSession.Capture(fc);
            var played=start.CardBranches(defend,null,1).First(x=>x.Boundary.Length==0).State;
            Check(Math.Abs(played.WitheringBurden(fair[0])-start.WitheringBurden(fair[0])-start.WitheringPlayCost(fair[0]))<1e-8,
                $"Every counted play must accrue one sixth of future Wither burden, including rollover: left={left}.");
            foreach(var policy in Enum.GetValues<RolloutPolicyKind>())
                Check(KernelRollout.Run(start.Fork(),fair,fair,new(policy,MaxSteps:1)).Actions.All(a=>a.Card is null),
                    $"{policy} must decline completely unused block at every counter position: left={left}.");
            Check(KernelTournament.FirstActions(start,fair,20,new()).Any(a=>a.EndTurn),
                "Search must offer passing under fractional status pressure before the hand is empty.");
            Hand<Bludgeon>(fair[0],fc);
            Check(KernelTournament.FirstActions(KernelSession.Capture(fc),fair,1,new()).FirstOrDefault()?.Card is Bludgeon,
                "A profitable play must precede optional passing when only one opening can be tested.");
            foreach(var policy in Enum.GetValues<RolloutPolicyKind>())
                Check(KernelRollout.Run(KernelSession.Capture(fc),fair,fair,new(policy,MaxSteps:1)).Actions.FirstOrDefault()?.Card is Bludgeon,
                    $"{policy} must still play damage worth more than its fractional pollution cost.");
        }
        var (repeated,rc,re)=Board(1,1);
        var rp=ModelDb.Power<WitheringPresencePower>().ToMutable();rp.Target=repeated[0].Creature;rp.ApplyInternal(re,1,true);
        var rs=Hand<StrikeIronclad>(repeated[0],rc);var spiral=ModelDb.Enchantment<Spiral>().ToMutable();
        rs.EnchantInternal(spiral,1);spiral.ModifyCard();
        var rroot=KernelSession.Capture(rc);var rend=rroot.CardBranches(rs,re,1).First(x=>x.Boundary.Length==0).State;
        Check(Math.Abs(rend.WitheringBurden(repeated[0])-rroot.WitheringBurden(repeated[0])-2*rroot.WitheringPlayCost(repeated[0]))<1e-8,
            "One replay must accrue two sixths of Wither burden rather than one manual-card charge.");
        Console.WriteLine("PASS: fractional diagnostic accounting remains consistent; policy skips unused block and retains profitable plays at all six counter positions.");
        if(Failures.Count>0)throw new Exception(string.Join("\n",Failures));
    }
    private static T Hand<T>(Player p, CombatState c) where T:CardModel
    { var card=c.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(card); return card; }
    private static (Player[],CombatState,Creature) Board(int count,int damage)
    {
        var ps=Enumerable.Range(0,count).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro,26,i))).ToArray();
        var c=new CombatState(runState:RunState.CreateForTest(ps,seed:"ATTRITION"));
        foreach(var p in ps) {p.ResetCombatState();c.AddPlayer(p);p.MaxEnergy=3;
            p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);}
        var e=c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"0");
        c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(500);e.SetCurrentHpInternal(500);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e,damage-1,true);
        return(ps,c,e);
    }
}



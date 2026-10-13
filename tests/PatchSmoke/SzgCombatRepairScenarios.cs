using System.Reflection;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// SZG1RJZ6XVL6 controlled mechanism receipts, not a replay of its hidden RNG.
// R2 2026-10-08: eleven isolated production mutants each compiled with exit0,
// named RED1, exact-byte restored, rebuilt exit0 and GREEN0. Native game DLL:
// 0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9.
// Commands, original outputs and source/restoration hashes:
// work/szg-repair-20261008T124624/combat/r2-results.json.
// System.Exception: SZG combat repair: 1 seats: native Dexterity setup must precede both block payoffs
// System.Exception: SZG combat repair: nonprinted enemy setup must precede native Vulnerable-consuming Bully (84 damage)
// System.Exception: SZG combat repair: native draw payoff must precede a draw source which closes further draws
// System.Exception: SZG combat repair: joint reference must order its first native action before committing an irreversible payoff prefix
// System.Exception: SZG combat repair: joint opening and suffix must share the same finite per-side ordering allowance
// System.Exception: SZG combat repair: lasting reaction must price a native further hit against every living seat
// System.Exception: SZG combat repair: temporary enemy Strength loss must not hide the native lasting Skill reaction
// System.Exception: SZG combat repair: cheap defence must preserve a Skill whose reaction hurts the full team more than its block
// System.Exception: SZG combat repair: Tactical must compare a reactive Skill with passing
// System.Exception: SZG combat repair: initial independent policy share must retain its unfinished native state
// System.Exception: SZG combat repair: resumed independent policy must reach and publish its real ending
// Rejected Accuracy-reserve hypothesis: fault remained GREEN because the
// existing SetupPayoff already protects that simple root. No redundant new
// assertion survives; --szg-combat-case=accuracy-control is a native probe.
// Additional same-owner sampled future-menu R2, isolated source mirror:
// work/szg-repair-20261008T124624/combat/future-choice/r2-results.json.
// InvalidPlannedChoiceBranchException: 回合开始选牌时找不到 DEFEND_IRONCLAD+0#0。
// System.Exception: SZG combat repair: sampled replacement must resume a later menu using its actual native hand
// System.Exception: SZG combat repair: actual later menu must compare the native hand and exhaust its cheap replacement instead of a retained core power
// Additional Cascade integration R2: isolated build0/RED1/exact restore/build0/GREEN0.
// work/szg-repair-20261008T124624/combat/cascade-fix/r2-results.json.
// System.Exception: SZG combat repair: asymmetric ordering must compare the proven legal pair and retain its first paid attack
// System.Exception: SZG combat repair: realized attack payoff must not pay again for the energy and useful card it consumes
// System.Exception: SZG combat repair: native incidental energy loss must not receive a card-payment refund
// System.Exception: SZG combat repair: a ready seat awaiting its ally must not reclaim opportunities excluded from its state value
// System.Exception: SZG combat repair: a later turn must not refund a new payment against an earlier opportunity window
// Old PostDecisionChoice and DecisionFix guards remain unchanged.
// Additional native order/conservation R2 (2026-10-08), isolated Rebuild:
// work/szg-repair-20261008T124624/combat/tail-fix-v2/r2-final-results.json
// work/szg-repair-20261008T124624/combat/tail-fix-v2/generated-pipeline-r2.json
// System.Exception: Strategic lookahead: Kill must apply debuffs, multiply their duration, then cash out Strength before attacking
// System.Exception: team-wide baselines must win the two-target setup fixture
// System.Exception: SZG combat repair: native generated-card payoff must realize all five attacks before a destructive hand effect
// System.Exception: SZG combat repair: printed native power variables must receive the finite comparison before a plain damage card
// System.Exception: SZG combat repair: printed native power variables must enter the reserved production ordering and realize their payoff
// Original native full-side controls: model/ordering-oracle.run.log; no Kernel score oracle.
// Reverting only seat priority remained GREEN and is preserved as a rejected
// fault hypothesis. The proven scheduling fault restores the entire old
// rarity/seat ordering; no extra pair quota or card-name policy is introduced.
internal static class SzgCombatRepairScenarios
{
    private static KernelSession? choiceObserver;
    private static readonly List<(ulong Owner, string Source, IReadOnlyList<CardModel> Cards)> startChoices = new();
    private static Queue<(ulong Owner, string Source, IReadOnlyList<CardModel> Cards)>? nativeStartChoices;
    internal static void Run(string? caseName = null)
    {
        TestEnvironment.Ensure();
        var harmony = new Harmony("coopbots.szg-combat-repair");
        harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(SzgCombatRepairScenarios), nameof(Format)));
        try
        {
            if (caseName is null or "ordering" or "footwork" or "molten" or "draw") Ordering(caseName);
            if (caseName is null or "reaction") Reaction();
            if (caseName is null or "generated-options") GeneratedOptions();
            if (caseName is null or "printed-power") PrintedPower();
            if (caseName is null or "asymmetry") AsymmetricPayoff();
            if (caseName is null or "resource-payoff") ResourcePayoff();
            if (caseName is null or "resume") Resume();
            if (caseName is null or "chained-choice") ChainedChoice(harmony);
            if (caseName == "accuracy-control")
                Console.WriteLine($"PROBE existing Accuracy native-control damage={NativeOrdering("accuracy",false)}; existing SetupPayoff already proposes this order, no new reserve assertion");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    private static void Ordering(string? selected)
    {
        if (selected is null or "ordering" or "footwork") foreach (var count in new[] { 1, 2, 4 })
        {
            var (party,c,e) = Board(count, 2000, 3); var p = party[^1];
            Pow<StrengthPower>(e,30);
            var foot = Hand<Footwork>(p,c,true); var d1 = Hand<DefendSilent>(p,c); var d2 = Hand<DefendSilent>(p,c);
            var run = Run(KernelSession.Capture(c),party,RolloutPolicyKind.Defend,3,reservedOnly:true);
            Check(run.Result!.Actions[0].Card == foot && run.State.Block(p.Creature) == 16,
                $"{count} seats: native Dexterity setup must precede both block payoffs");
            Check(run.Result.Actions.Skip(1).Select(a=>a.Card).ToHashSet().SetEquals(new CardModel[]{d1,d2}),
                "block dependency must retain both legal payoffs");
            Console.WriteLine($"PROBE szg-order seats={count} Footwork block={run.State.Block(p.Creature)}");
        }
        if (selected is null or "ordering" or "molten") {
            var (party,c,e) = Board(4,2000,1); var p = party[0];
            Pow<VulnerablePower>(e,5); Pow<StrengthPower>(p.Creature,4);
            var bully = Hand<Bully>(p,c,true); var molten = Hand<MoltenFist>(p,c,true);
            var root = KernelSession.Capture(c); var run = Run(root,party,RolloutPolicyKind.Kill,2,reservedOnly:true);
            Check(run.Result!.Actions.Select(a=>a.Card).SequenceEqual(new CardModel[]{molten,bully})
                && root.Hp(e)-run.State.Hp(e)==84,
                "nonprinted enemy setup must precede native Vulnerable-consuming Bully (84 damage)");
            var bad=root.ForkForReplay(); Check(bad.Play(bully,e,out _) && bad.Play(molten,e,out _),"reverse Molten pair must be legal");
            Check(root.Hp(e)-bad.Hp(e)==61 && root.Hp(e)-run.State.Hp(e)-(root.Hp(e)-bad.Hp(e))==23,
                "native reversed pair must independently expose the 23 damage loss");
            var joint=new KernelJointTurnRun(root,party,party,new(IncludePotions:false),10000);
            joint.Advance(TimeSpan.FromTicks(1));
            var referencePath=(IReadOnlyList<KernelTeamSearch.Action>)AccessTools.Field(typeof(KernelJointTurnRun),"referencePath").GetValue(joint)!;
            Check(referencePath.Single().Card==molten,
                "joint reference must order its first native action before committing an irreversible payoff prefix");
            var first=(RolloutRun)AccessTools.Field(typeof(KernelJointTurnRun),"referenceOpening").GetValue(joint)!;
            var tail=(RolloutRun)AccessTools.Field(typeof(KernelJointTurnRun),"continuation").GetValue(joint)!;
            int Remaining(RolloutRun line)=>((Dictionary<Player,int>)AccessTools.Field(typeof(RolloutRun),"orderPairs").GetValue(line)!).Values.Sum()
                +((Dictionary<Player,int>)AccessTools.Field(typeof(RolloutRun),"orderSetupPairs").GetValue(line)!).Values.Sum();
            Check(Remaining(first)<KernelSequenceOrder.PairLimit && Remaining(tail)<=Remaining(first),
                "joint opening and suffix must share the same finite per-side ordering allowance");
            Console.WriteLine("PROBE szg-order Molten/Bully native=84 reverse=61 delta=23");
        }
        if (selected is null or "ordering" or "draw") {
            var (party,c,e) = Board(4,2000,1); var p = party[2];
            var trance=Hand<BattleTrance>(p,c); var shrug=Hand<ShrugItOff>(p,c);
            for(var i=0;i<4;i++)p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
            var root=KernelSession.Capture(c); var run=Run(root,party,RolloutPolicyKind.Growth,2,reservedOnly:true);
            Check(run.Result!.Actions.Select(a=>a.Card).SequenceEqual(new CardModel[]{shrug,trance})
                && run.State.Hand(p).Count==4,
                "native draw payoff must precede a draw source which closes further draws");
            var bad=root.ForkForReplay(); Check(bad.Play(trance,null,out _) && bad.Play(shrug,null,out _),"reverse draw pair must be legal");
            Check(bad.Hand(p).Count==3,"native NoDraw must distinguish the lost fourth draw");
            Console.WriteLine("PROBE szg-order Shrug/Trance draws=4 reverse=3");
        }
        if (selected is null or "ordering" or "footwork") Check(NativeOrdering("footwork",false)==16,"original sts2.dll Footwork/block effects must independently match 16 block");
        if (selected is null or "ordering" or "molten") Check(NativeOrdering("molten",false)==84 && NativeOrdering("molten",true)==61,
            "original sts2.dll Molten/Bully effects must independently match both damage orders");
        if (selected is null or "ordering" or "draw") Check(NativeOrdering("draw",false)==4 && NativeOrdering("draw",true)==3,
            "original sts2.dll draw/NoDraw effects must independently match both hand counts");
        Console.WriteLine("PASS: SZG native block, enemy setup, draw closure and attack-trigger dependencies enter production rollouts.");
    }

    private static void GeneratedOptions()
    {
        var (party,c,e)=Board(1,1017,2);var p=party[0];p.Creature.SetCurrentHpInternal(41);
        Pow<StrengthPower>(p.Creature,1);Pow<DexterityPower>(p.Creature,2);Pow<StrengthPower>(e,8);Pow<EnvenomPower>(p.Creature,1);
        var defend=Hand<DefendSilent>(p,c,true);var hidden=Hand<HiddenDaggers>(p,c);var dance=Hand<BladeDance>(p,c);
        var root=KernelSession.Capture(c);
        Console.WriteLine($"PROBE native generated Skill hold={KernelExhaustPolicy.ShouldHold(root,party,p,dance)}");
        foreach(var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var line=KernelRollout.Run(root.Fork(),party,party,new(policy,MaxRounds:1));var replayed=root.Fork();
            foreach(var action in line.Actions)Check(replayed.ReplayPlanned(action,1,out _),"generated continuation must replay with native resources");
            Console.WriteLine($"PROBE generated {policy}: damage={root.Hp(e)-replayed.Hp(e)} cards={string.Join(',',line.Actions.Select(a=>a.Card?.Id.Entry??"END"))}");
            Check(root.Hp(e)-replayed.Hp(e)==30 && line.Actions.Count(a=>a.Card is Shiv)==5
                && line.Actions.Any(a=>a.Card==defend),
                "native generated-card payoff must realize all five attacks before a destructive hand effect");
        }
        using(var native=new NativeTurn(c))
        {
            NativePlay(defend,null);NativePlay(dance,null);
            foreach(var shiv in p.PlayerCombatState!.Hand.Cards.OfType<Shiv>().ToArray())NativePlay(shiv,e);
            Check(1017-e.CurrentHp==15 && e.GetPowerAmount<PoisonPower>()==3 && p.Creature.Block==10
                && p.PlayerCombatState.Energy==0,
                "original sts2.dll must independently create and legally pay all three generated attacks after paid block");
        }
        Console.WriteLine("PASS: SZG native generated payable options survive conservation and enter all six production policies.");
    }

    private static void PrintedPower()
    {
        var (party,c,e)=Board(2,2000,3);var p=party[0];var q=party[1];
        Pow<VulnerablePower>(e,5);Pow<StrengthPower>(p.Creature,4);
        var bully=Hand<Bully>(p,c,true);var filler=Hand<Anger>(q,c);var bash=Hand<Bash>(q,c,true);
        Console.WriteLine("PROBE native printed vars="+string.Join(',',bash.DynamicVars.Values.Select(v=>v.GetType().FullName)));
        var root=KernelSession.Capture(c);
        var production=Run(root,party,RolloutPolicyKind.Kill,2,reservedOnly:true);
        // R2 final: disabling the native PowerVar category exited 1 with
        // "printed native power variables must enter the reserved production ordering and realize their payoff".
        // Exact-byte restore + Rebuild then exited 0; see r2-final-results.json.
        Check(production.Result!.Actions.Select(a=>a.Card).SequenceEqual(new CardModel[]{bash,bully})
            && root.Hp(e)-production.State.Hp(e)==63,
            "printed native power variables must enter the reserved production ordering and realize their payoff");
        KernelTeamSearch.Action A(CardModel card)=>new(card.Owner,card,e,CardStateKey:root.CardStateKey(card));
        var proposed=A(bully);var remaining=1;
        var chosen=KernelSequenceOrder.Before(root,party,party,proposed,[A(filler),A(bash)],ref remaining,
            cache:new(root,party,party,handLookahead:false));
        Check(chosen.Card==bash && remaining==0,
            "printed native power variables must receive the finite comparison before a plain damage card");
        using(var native=new NativeTurn(c))
        {
            NativePlay(bash,e);NativePlay(bully,e);
            Console.WriteLine($"PROBE native Bash/Bully dmg={2000-e.CurrentHp} vul={e.GetPowerAmount<VulnerablePower>()}");
            Check(e.GetPowerAmount<VulnerablePower>()==8 && 2000-e.CurrentHp==63,
                "original sts2.dll must independently realize the native Vulnerable setup and payoff");
        }
        Console.WriteLine("PASS: SZG printed native power variables schedule bounded real setup comparisons.");
    }

    // Actual game resources make one direction incomplete while the opposite
    // direction still realizes both cards. No card ID enters the algorithm.
    private static void AsymmetricPayoff()
    {
        var (party,c,e)=Board(1,2000,3);var p=party[0];
        var strike=Hand<StrikeSilent>(p,c,true);var x=Hand<Skewer>(p,c);
        var root=KernelSession.Capture(c);var text=root.StateText(p);
        var a=new KernelTeamSearch.Action(p,strike,e,CardStateKey:root.CardStateKey(strike));
        var b=new KernelTeamSearch.Action(p,x,e,CardStateKey:root.CardStateKey(x));
        var cache=new KernelSequenceOrder.ProbeCache(root,party,party,handLookahead:false);
        var first=cache.Open(a)!.State;var other=cache.Open(b)!.State;
        var legal=cache.Then(first,b);
        Check(cache.Then(other,a) is null && legal is not null && root.Hp(e)-legal.Hp(e)==25,
            "native asymmetric X resource pair must preserve its actually legal 25 damage direction");
        foreach(var proposed in new[]{a,b})
        {
            var remaining=2;var chosen=KernelSequenceOrder.Before(root,party,party,proposed,[a,b],ref remaining,
                cache:new(root,party,party,handLookahead:false));
            Check(chosen.Card==strike && remaining==1,
                "asymmetric ordering must compare the proven legal pair and retain its first paid attack");
        }
        var run=Run(root,party,RolloutPolicyKind.Kill,2);
        Check(run.Result!.Actions.Select(a=>a.Card).SequenceEqual(new CardModel[]{strike,x})
            && root.Hp(e)-run.State.Hp(e)==25 && run.State.Energy(p)==0,
            "production rollout must commit the superior payable pair rather than lose its first attack");
        using(var native=new NativeTurn(c))
        {
            NativePlay(strike,e);NativePlay(x,e);
            Check(2000-e.CurrentHp==25 && p.PlayerCombatState!.Energy==0,
                "original sts2.dll spend and attack hooks must independently match the legal 25 damage pair");
        }
        var (reverseParty,reverseCombat,reverseEnemy)=Board(1,2000,3);var rp=reverseParty[0];
        var rs=Hand<StrikeSilent>(rp,reverseCombat,true);var rx=Hand<Skewer>(rp,reverseCombat);
        using(var native=new NativeTurn(reverseCombat))
        {
            NativePlay(rx,reverseEnemy);
            Check(2000-reverseEnemy.CurrentHp==24 && !rp.PlayerCombatState!.HasEnoughResourcesFor(rs,out _),
                "original sts2.dll reverse X prefix must independently expose the unpayable second card");
        }
        Check(root.StateText(p)==text,"asymmetric native comparisons must preserve the captured parent");
        Console.WriteLine("PROBE szg-asymmetry native Strike+/X=25 X-only=24, both proposed directions and production rollout agree");
        Console.WriteLine("PASS: SZG asymmetric native second-card evidence enters bounded production ordering.");
    }

    private static void ResourcePayoff()
    {
        var (party,c,e)=Board(1,2000,2);var p=party[0];Pow<StrengthPower>(p.Creature,1);
        var a=Hand<StrikeIronclad>(p,c);var b=Hand<StrikeIronclad>(p,c);
        var root=KernelSession.Capture(c);var first=root.CardBranches(a,e,1).Single(x=>x.Boundary.Length==0).State;
        var second=first.CardBranches(b,e,1).Single(x=>x.Boundary.Length==0).State;
        var gain=KernelTacticalPolicy.ActionValue(root,second,party,party)-KernelTacticalPolicy.ActionValue(root,first,party,party);
        Check(gain>5.6 && gain<5.7,
            "realized attack payoff must not pay again for the energy and useful card it consumes");
        var passed=root.ForkForReplay();Check(passed.EndTurn(p,1,out _),"resource control must complete a legal native pass");
        Check(Math.Abs(KernelTacticalPolicy.ActionValue(root,passed,party,party)-KernelTacticalPolicy.Value(passed,party,party))<.001,
            "passing must receive no refund for unused resources");
        using(var native=new NativeTurn(c))
        {
            NativePlay(a,e);NativePlay(b,e);
            Check(2000-e.CurrentHp==14 && p.PlayerCombatState!.Energy==0,
                "original sts2.dll must independently pay two cards and realize fourteen damage");
        }
        var (vp,vc,ve)=Board(1,2000,3);var v=vp[0];var draw=Hand<BattleTrance>(v,vc);
        v.PlayerCombatState!.DrawPile.AddInternal(vc.CreateCard<MegaCrit.Sts2.Core.Models.Cards.Void>(v));
        for(var i=0;i<2;i++)v.PlayerCombatState.DrawPile.AddInternal(vc.CreateCard<StrikeIronclad>(v));
        var vroot=KernelSession.Capture(vc);var voidDraw=vroot.CardBranches(draw,null,1).Single(b=>b.Boundary.Length==0).State;
        Check(voidDraw.Energy(v)==2 && voidDraw.EnergySpentThisTurn(v)==0,
            "native zero-cost draw must lose Void energy without spending it on the played card");
        Check(Math.Abs(KernelTacticalPolicy.ActionValue(vroot,voidDraw,vp,vp)-KernelTacticalPolicy.Value(voidDraw,vp,vp))<.001,
            "native incidental energy loss must not receive a card-payment refund");
        using(var native=new NativeTurn(vc))
        {
            NativePlay(draw,null);
            Check(v.PlayerCombatState.Energy==2 && v.PlayerCombatState.Hand.Cards.Any(c=>c is MegaCrit.Sts2.Core.Models.Cards.Void),
                "original sts2.dll zero-cost draw must independently realize Void's incidental energy loss");
        }
        var (waitingParty,waitingCombat,waitingEnemy)=Board(2,2000,3);var wp=waitingParty[0];
        var paidCard=Hand<StrikeIronclad>(wp,waitingCombat);var waitingRoot=KernelSession.Capture(waitingCombat);
        var waiting=waitingRoot.CardBranches(paidCard,waitingEnemy,1).Single(b=>b.Boundary.Length==0).State;
        Check(waiting.EndTurn(wp,1,out _) && !waiting.CanAct(wp) && waiting.CanAct(waitingParty[1])
            && waiting.RoundsAdvanced==waitingRoot.RoundsAdvanced && waiting.EnergySpentThisTurn(wp)==1,
            "ready-seat control must keep native payment history while awaiting its ally");
        Check(Math.Abs(KernelTacticalPolicy.ActionValue(waitingRoot,waiting,waitingParty,waitingParty)
            -KernelTacticalPolicy.Value(waiting,waitingParty,waitingParty))<.001,
            "a ready seat awaiting its ally must not reclaim opportunities excluded from its state value");
        var (zp,zc,ze)=Board(1,2000,3);var z=zp[0];var zero=Hand<Shiv>(z,zc);
        var zroot=KernelSession.Capture(zc);var played=zroot.CardBranches(zero,ze,1).Single(b=>b.Boundary.Length==0).State;
        Check(played.EnergySpentThisTurn(z)==0 && played.Energy(z)==3 && played.EndTurn(z,1,out _),
            "zero-cost control must play legally and then settle the side");
        Check(Math.Abs(KernelTacticalPolicy.ActionValue(zroot,played,zp,zp)-KernelTacticalPolicy.Value(played,zp,zp))<.001,
            "zero-cost play followed by end-turn must not refund cleared energy or unplayed hand opportunities");
        using(var native=new NativeTurn(zc))
        {
            NativePlay(zero,ze);
            Check(z.PlayerCombatState!.Energy==3 && 2000-ze.CurrentHp==4,
                "original sts2.dll zero-cost card must independently spend no energy");
        }
        var (nextParty,nextCombat,nextEnemy)=Board(1,2000,3);var np=nextParty[0];Hand<StrikeIronclad>(np,nextCombat);
        for(var i=0;i<5;i++)np.PlayerCombatState!.DrawPile.AddInternal(nextCombat.CreateCard<StrikeIronclad>(np));
        var nroot=KernelSession.Capture(nextCombat);var next=nroot.ForkForReplay();
        Check(next.EndTurn(np,2,out _) && next.CanAct(np) && next.TurnNumberFor(np)>nroot.TurnNumberFor(np),
            "resource boundary control must advance a real new player turn");
        var ns=next.Hand(np).First(card=>next.CanPlay(card));
        Check(next.Play(ns,nextEnemy,out _) && next.EnergySpentThisTurn(np)==1,
            "new-turn control must record its own native payment after resetting the turn history");
        Check(Math.Abs(KernelTacticalPolicy.ActionValue(nroot,next,nextParty,nextParty)-KernelTacticalPolicy.Value(next,nextParty,nextParty))<.001,
            "a later turn must not refund a new payment against an earlier opportunity window");
        Console.WriteLine($"PROBE szg-resource native damage=14 energy=0 second-realized-output-value={gain:F6} zero-cost/end/new-turn=no-refund");
        Console.WriteLine("PASS: SZG tactical comparisons cash native output without double charging spent opportunities.");
    }

    private static void Reaction()
    {
        foreach(var count in new[]{1,2,4})
        {
            var (party,c,e)=Board(count,2000,3);var p=party[0];
            Pow<EnragePower>(e,3);var defend=Hand<DefendSilent>(p,c);
            var root=KernelSession.Capture(c);var branch=root.CardBranches(defend,null,1).Single(b=>b.Boundary.Length==0).State;
            Check(branch.Power<StrengthPower>(e)==3,"native Skill reaction must increase enemy Strength");
            var cost=KernelTacticalPolicy.LastingEnemyReactionCost(root,branch,party);
            Check(Math.Abs(cost-3*count*1.3)<.001,"lasting reaction must price a native further hit against every living seat");
            using(var native=new NativeTurn(c))
            {
                NativePlay(defend,null);
                Check(e.GetPowerAmount<StrengthPower>()==3 && p.Creature.Block==5,
                    "original sts2.dll Skill and AfterCardPlayed hooks must independently produce block and enemy Strength");
                e.Monster!.NextMove.PerformMove(party.Select(x=>x.Creature).ToArray()).GetAwaiter().GetResult();
                Check(p.Creature.CurrentHp==80 && party.Skip(1).All(x=>x.Creature.CurrentHp==76),
                    "original enemy attack must independently expose the full-team reactive damage");
                Console.WriteLine($"PROBE szg-native-reaction seats={count} HP={string.Join('/',party.Select(x=>x.Creature.CurrentHp))} strength={e.GetPowerAmount<StrengthPower>()}");
            }
            if(count==4)
            {
                var run=Run(root,party,RolloutPolicyKind.Defend,1);
                Check(run.Result!.Actions.Single().EndTurn && run.State.Hand(p).Contains(defend),
                    "cheap defence must preserve a Skill whose reaction hurts the full team more than its block");
                var tactical=Run(root,party,RolloutPolicyKind.Tactical,1);
                Check(tactical.Result!.Actions.Single().EndTurn,"Tactical must compare a reactive Skill with passing");
            }
            Console.WriteLine($"PROBE szg-reaction seats={count} persistent-native-hit-cost={cost:F1}");
        }
        {
            var (party,c,e)=Board(4,2000,1);var p=party[0];Pow<EnragePower>(e,3);var defend=Hand<DefendSilent>(p,c);
            e.SetCurrentHpInternal(6);var strike=Hand<StrikeSilent>(p,c);
            var root=KernelSession.Capture(c);var run=Run(root,party,RolloutPolicyKind.Defend,1);
            Check(run.Result!.Actions.Single().Card==strike && run.State.HasWon,
                "reaction pricing must preserve a legal immediate victory");
        }
        {
            var (party,c,e)=Board(4,2000,1);var p=party[0];Pow<EnragePower>(e,3);Pow<StrengthPower>(e,10);
            var wail=Hand<PiercingWail>(p,c);var root=KernelSession.Capture(c);
            var branch=root.CardBranches(wail,null,1).Single(b=>b.Boundary.Length==0).State;
            var cost=KernelTacticalPolicy.LastingEnemyReactionCost(root,branch,party);
            Check(branch.Power<StrengthPower>(e)==7 && Math.Abs(cost-15.6)<.001,
                "temporary enemy Strength loss must not hide the native lasting Skill reaction");
            using var native=new NativeTurn(c);NativePlay(wail,null);
            Check(e.GetPowerAmount<StrengthPower>()==7,"original Wail/reaction must agree before expiry");
            e.GetPower<PiercingWailPower>()!.AfterSideTurnEnd(new ThrowingPlayerChoiceContext(),CombatSide.Enemy,[e]).GetAwaiter().GetResult();
            Check(e.GetPowerAmount<StrengthPower>()==13,"original temporary expiry must independently retain the three lasting reaction Strength");
            Console.WriteLine($"PROBE szg-native-reaction Wail before-expiry=7 after-expiry={e.GetPowerAmount<StrengthPower>()} lasting-cost={cost:F1}");
        }
        Console.WriteLine("PASS: SZG native reaction cost covers the whole team and cheap/Tactical continuations compare passing.");
    }

    private static void Resume()
    {
        var (party,c,e)=Board(1,18,3);var p=party[0];
        Hand<StrikeIronclad>(p,c);Hand<StrikeIronclad>(p,c);Hand<StrikeIronclad>(p,c);
        var root=KernelSession.Capture(c);var rootText=root.StateText(p);
        var tournament=new TournamentRun(root,party,party,
            new(IncludePotions:false,ReserveMs:0,EnableJointSearch:true,Rollout:new(MaxSteps:30){InvestmentLookahead=false}),10000);
        // Put the initial fair share just below one action. The real production
        // scheduler must pause its exact native state rather than publish a
        // cutoff and throw it away. No wall-clock sleeps are involved.
        AccessTools.Field(typeof(TournamentRun),"policyReferenceDeadlineMs").SetValue(tournament,100000d);
        Invoke(tournament,"AdvancePolicyReferences",TimeSpan.FromTicks(1));
        AccessTools.Field(typeof(TournamentRun),"policyReferenceCapMs").SetValue(tournament,0d);
        Invoke(tournament,"AdvancePolicyReferences",TimeSpan.FromTicks(1));
        var paused=AccessTools.Field(typeof(TournamentRun),"pausedPolicyReferences").GetValue(tournament)!;
        Check((int)paused.GetType().GetProperty("Count")!.GetValue(paused)! == 1,
            "initial independent policy share must retain its unfinished native state");
        var item=(ValueTuple<RolloutPolicyKind,RolloutRun>)paused.GetType().GetMethod("Peek")!.Invoke(paused,null)!;
        Check(!item.Item2.Done && item.Item2.State.Hand(p).Count==1 && item.Item2.State.Hp(e)==6,
            "pause must preserve its resolved actions, enemy HP and native hand");
        Invoke(tournament,"AdvancePolicyReferences",TimeSpan.FromTicks(1));
        AccessTools.Field(typeof(TournamentRun),"policyReferenceCapMs").SetValue(tournament,0d);
        Invoke(tournament,"AdvancePolicyReferences",TimeSpan.FromTicks(1));
        Check((int)paused.GetType().GetProperty("Count")!.GetValue(paused)! == 2,
            "distinct defence and growth openings must both retain their own unfinished native state");
        AccessTools.Field(typeof(TournamentRun),"referenceFinished").SetValue(tournament,true);
        AccessTools.Field(typeof(TournamentRun),"policyReferencesFinished").SetValue(tournament,true);
        tournament.Advance(TimeSpan.MaxValue);
        Check(item.Item2.Result is {Terminal:true,Victory:true} && tournament.BestCompleted?.Record is {Verified:true,Victory:true},
            "resumed independent policy must reach and publish its real ending");
        var winner=tournament.BestCompleted!;var replay=root.ForkForReplay();
        foreach(var action in winner.Script!)Check(replay.ReplayPlanned(action,80,out _),"resumed native script must replay");
        Check(replay.HasWon && winner.Script!.Count==winner.ScriptAliveStates!.Count
            && winner.Script.Count==winner.ScriptEnemyStates!.Count && root.StateText(p)==rootText,
            "resume must retain action-aligned replay evidence and preserve the root");
        Console.WriteLine("PASS: SZG independent native alternatives retain state across fair budget pauses and publish only actual endings.");
    }

    private static void ChainedChoice(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(KernelSession), "AutomaticTurnStartChoice"),
            postfix: new HarmonyMethod(typeof(SzgCombatRepairScenarios), nameof(ObserveStartChoice)));
        foreach (var method in typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(Task<IEnumerable<CardModel>>)))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(SzgCombatRepairScenarios), nameof(NativeStartSelect)));
        foreach (var count in new[] { 1, 2, 4 })
        {
            var (party, combat, enemy) = Board(count, 2000, 3);
            combat.RoundNumber = 0;
            foreach (var seat in party) AccessTools.Property(typeof(PlayerCombatState), "TurnNumber").SetValue(seat.PlayerCombatState, 0);
            var owner = party[^1]; Pow<StrengthPower>(enemy, 30);
            foreach (var seat in party)
                foreach (var card in seat == owner
                    ? new CardModel[] { ModelDb.Card<Impervious>(), ModelDb.Card<Barricade>(),
                        ModelDb.Card<DemonForm>(), ModelDb.Card<Corruption>(), ModelDb.Card<DarkEmbrace>() }
                    : new CardModel[] { ModelDb.Card<StrikeIronclad>(), ModelDb.Card<DefendIronclad>(),
                        ModelDb.Card<Bash>(), ModelDb.Card<ShrugItOff>(), ModelDb.Card<PommelStrike>() })
                    seat.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard(card, seat));
            // Analysis sorts DEFEND before STRIKE; the untouched native branch
            // has STRIKE on top. Replanning must burn its actual cheap Strike,
            // while the original game DLL verifies the whole resulting state.
            for (var i = 0; i < 5; i++) owner.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
            for (var i = 0; i < 5; i++) owner.PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<DefendIronclad>(owner));
            owner.AddRelicInternal(ModelDb.Relic<GamblingChip>().ToMutable());
            owner.AddRelicInternal(ModelDb.Relic<ToastyMittens>().ToMutable());
            using var native = new NativeTurn(combat);
            var root = KernelSession.Capture(combat); var frozen = root.PartyStateText(); var sim = root.ForkForReplay();
            sim.ConfigureSynchronousChoicePlayers(party.Select(p => p.NetId));
            startChoices.Clear(); choiceObserver = sim;
            try
            {
                Check((bool)AccessTools.Method(typeof(KernelSession), "StartNextPlayerTurn").Invoke(sim, [party, false])!,
                    "sampled replacement must resume a later menu using its actual native hand");
            }
            finally { choiceObserver = null; }
            Console.WriteLine($"PROBE szg-chained seats={count} choices={string.Join('|',startChoices.Select(q=>q.Source+":"+string.Join(',',q.Cards.Select(c=>c.Id.Entry))))}");
            Check(startChoices.Count == 2 && startChoices.All(q => q.Owner == owner.NetId)
                && startChoices[0].Cards.Count > 0 && startChoices[1].Cards.Count == 1,
                "chained fixture must drive a real replacement and a later same-owner exhaust menu");
            Check(startChoices[1].Cards.Single() is StrikeIronclad,
                "actual later menu must compare the native hand and exhaust its cheap replacement instead of a retained core power");
            nativeStartChoices = new(startChoices);
            try { StartNativeSynchronous(native, combat, party); }
            finally { nativeStartChoices = null; }
            Check(Piles(sim, party) == Piles(party) && party.All(p => sim.Energy(p) == p.PlayerCombatState!.Energy)
                && sim.Power<StrengthPower>(owner.Creature) == owner.Creature.GetPowerAmount<StrengthPower>(),
                "actual sts2.dll chained replacement and exhaust must match every complete pile, energy and Strength");
            Check(sim.Simulator.Rng.ShuffleState == ((RunState)combat.RunState).Rng.Shuffle.CaptureState()
                && root.PartyStateText() == frozen,
                "chained menu analysis must preserve the shared native RNG and its frozen parent");
        }
        Console.WriteLine("PASS: SZG sampled replacement cannot commit an unavailable future-menu token; native complete effects match.");
    }
    private static void ObserveStartChoice(KernelSession __instance, TurnStartChoiceRequest request, PlanCardChoice? __result)
    {
        if (!ReferenceEquals(__instance, choiceObserver) || __result is null || request.Spec is not { } spec) return;
        var owner = spec.Options.FirstOrDefault()?.Preview.Owner;
        if (owner is null) return;
        var cards = TurnStartChoiceSupport.ResolveTokens(__result, spec.Options, spec.MinCount, spec.MaxCount);
        startChoices.Add((owner.NetId, request.SourceId, cards.Select(c => c.Original).ToArray()));
    }
    private static bool NativeStartSelect(object[] __args, ref Task<IEnumerable<CardModel>> __result)
    {
        if (nativeStartChoices is null) return true;
        var owner = __args.OfType<Player>().First();
        Check(nativeStartChoices.TryDequeue(out var selection) && selection.Owner == owner.NetId
            && selection.Cards.All(owner.PlayerCombatState!.Hand.Cards.Contains),
            "independent native selector must use the current owner's actual hand and physical cards");
        __result = Task.FromResult<IEnumerable<CardModel>>(selection.Cards); return false;
    }
    private static void StartNativeSynchronous(NativeTurn native, CombatState combat, Player[] party)
    {
        combat.RoundNumber++; combat.CurrentSide = CombatSide.Player;
        foreach (var p in party) { p.PlayerCombatState!.IncrementTurnNumber(); p.Creature.BeforeTurnStart(CombatSide.Player); }
        Hook.BeforeSideTurnStart(combat, CombatSide.Player, party.Select(p => p.Creature).ToArray()).GetAwaiter().GetResult();
        foreach (var p in party) p.Creature.AfterTurnStart(CombatSide.Player).GetAwaiter().GetResult();
        foreach (var p in party) Hook.AfterBlockCleared(combat, p.Creature).GetAwaiter().GetResult();
        foreach (var p in party)
        {
            var context = new HookPlayerChoiceContext(p, party[0].NetId,
                MegaCrit.Sts2.Core.Entities.Multiplayer.GameActionType.CombatPlayPhaseOnly);
            var task = (Task)AccessTools.Method(typeof(CombatManager), "SetupPlayerTurn").Invoke(CombatManager.Instance,
                [native.Turn, p, context])!;
            Check(task.IsCompletedSuccessfully, "synchronous native setup must finish before the shared side hook: " + task.Exception);
        }
        Hook.AfterSideTurnStart(combat, CombatSide.Player, party.Select(p => p.Creature).ToArray()).GetAwaiter().GetResult();
        foreach (var p in party) p.PlayerCombatState!.OrbQueue.AfterTurnStart(new HookPlayerChoiceContext(p,
            party[0].NetId, MegaCrit.Sts2.Core.Entities.Multiplayer.GameActionType.CombatPlayPhaseOnly)).GetAwaiter().GetResult();
        foreach (var p in party)
        {
            p.PlayerCombatState!.Phase = PlayerTurnPhase.AutoPrePlay;
            Hook.AfterAutoPrePlayPhaseEntered(new HookPlayerChoiceContext(p, party[0].NetId,
                MegaCrit.Sts2.Core.Entities.Multiplayer.GameActionType.CombatPlayPhaseOnly), combat, p).GetAwaiter().GetResult();
            p.PlayerCombatState.Phase = PlayerTurnPhase.Play;
        }
        Check(nativeStartChoices!.Count == 0, "native setup must consume every observed current-menu choice exactly once");
    }
    private static string CardNames(IEnumerable<CardModel> cards) => string.Join(',',cards.Select(c=>c.Id.Entry+"+"+c.CurrentUpgradeLevel));
    private static string Piles(KernelSession s,Player[] p) => string.Join('|',p.Select(x=>$"{x.NetId}:H[{CardNames(s.Hand(x))}]D[{CardNames(s.DrawPile(x))}]C[{CardNames(s.Discard(x))}]E[{CardNames(s.Exhaust(x))}]"));
    private static string Piles(Player[] p) => string.Join('|',p.Select(x=>$"{x.NetId}:H[{CardNames(x.PlayerCombatState!.Hand.Cards)}]D[{CardNames(x.PlayerCombatState.DrawPile.Cards)}]C[{CardNames(x.PlayerCombatState.DiscardPile.Cards)}]E[{CardNames(x.PlayerCombatState.ExhaustPile.Cards)}]"));

    private static RolloutRun Run(KernelSession root,Player[] party,RolloutPolicyKind policy,int steps,bool reservedOnly=false)
    {
        var run=new RolloutRun(root.ForkForReplay(),party,party,new(policy,MaxSteps:steps){InvestmentLookahead=false,SequenceHandLookahead=false});
        if(reservedOnly)
        {
            AccessTools.Field(typeof(RolloutRun),"orderRound").SetValue(run,root.RoundsAdvanced);
            var ordinary=(Dictionary<Player,int>)AccessTools.Field(typeof(RolloutRun),"orderPairs").GetValue(run)!;
            var setup=(Dictionary<Player,int>)AccessTools.Field(typeof(RolloutRun),"orderSetupPairs").GetValue(run)!;
            for(var i=0;i<party.Length;i++){ordinary[party[i]]=0;setup[party[i]]=KernelSequenceOrder.SeatAllowance(party.Length,i).Setup;}
        }
        while(!run.Done)run.Step();return run;
    }
    private static void Invoke(object target,string name,TimeSpan value)=>AccessTools.Method(target.GetType(),name).Invoke(target,[value]);
    private static int NativeOrdering(string kind,bool reversed)
    {
        var (party,c,e)=Board(1,2000,3);var p=party[0];using var native=new NativeTurn(c);
        CardModel[] cards;
        switch(kind)
        {
            case "footwork":Pow<StrengthPower>(e,30);cards=[Hand<Footwork>(p,c,true),Hand<DefendSilent>(p,c),Hand<DefendSilent>(p,c)];break;
            case "molten":Pow<VulnerablePower>(e,5);Pow<StrengthPower>(p.Creature,4);cards=[Hand<MoltenFist>(p,c,true),Hand<Bully>(p,c,true)];break;
            case "draw":cards=[Hand<ShrugItOff>(p,c),Hand<BattleTrance>(p,c)];for(var i=0;i<4;i++)p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));break;
            case "accuracy":Pow<VulnerablePower>(e,17);cards=[Hand<Accuracy>(p,c,true),Hand<Shiv>(p,c),Hand<Shiv>(p,c)];break;
            default:throw new ArgumentException(kind);
        }
        foreach(var card in reversed?cards.Reverse():cards)NativePlay(card,card.TargetType==TargetType.AnyEnemy?e:null);
        var result=kind=="footwork"?p.Creature.Block:kind=="draw"?p.PlayerCombatState!.Hand.Cards.Count:2000-e.CurrentHp;
        Console.WriteLine($"PROBE original-sts2 order={kind} reversed={reversed} result={result}");return result;
    }
    private static void NativePlay(CardModel card,Creature? target)
    {
        var context=new ThrowingPlayerChoiceContext();var player=card.Owner;
        Check(player.PlayerCombatState!.HasEnoughResourcesFor(card,out _) && card.IsValidTarget(target),"native comparison must pay a legal card");
        var spent=card.SpendResources().GetAwaiter().GetResult();
        player.PlayerCombatState.Hand.RemoveInternal(card);player.PlayerCombatState.PlayPile.AddInternal(card);
        var pile=card.Type==CardType.Power?PileType.None:card.Keywords.Contains(CardKeyword.Exhaust)?PileType.Exhaust:PileType.Discard;
        var play=new CardPlay{Card=card,Player=player,Target=target,ResultPile=pile,Resources=new ResourceInfo{EnergySpent=spent.Item1,EnergyValue=spent.Item1,StarsSpent=spent.Item2,StarValue=spent.Item2},IsAutoPlay=false,PlayIndex=0,PlayCount=1};
        Hook.BeforeCardPlayed(card.CombatState!,play).GetAwaiter().GetResult();
        ((Task)AccessTools.Method(card.GetType(),"OnPlay").Invoke(card,[context,play])!).GetAwaiter().GetResult();
        Hook.AfterCardPlayed(card.CombatState!,context,play).GetAwaiter().GetResult();
        player.PlayerCombatState.PlayPile.RemoveInternal(card);
        if(pile==PileType.Discard)player.PlayerCombatState.DiscardPile.AddInternal(card);
        else if(pile==PileType.Exhaust)player.PlayerCombatState.ExhaustPile.AddInternal(card);
    }
    private sealed class NativeTurn:IDisposable
    {
        readonly FieldInfo turnField=AccessTools.Field(typeof(CombatManager),"_turnState");
        readonly object? previousTurn,previousRun,previousAscension;
        readonly ulong? previousLocal=MegaCrit.Sts2.Core.Context.LocalContext.NetId;
        internal NativeTurn(CombatState c)
        {
            previousRun=AccessTools.Property(typeof(RunManager),"State").GetValue(RunManager.Instance);
            previousAscension=AccessTools.Property(typeof(RunManager),nameof(RunManager.AscensionManager)).GetValue(RunManager.Instance);
            AccessTools.Property(typeof(RunManager),"State").SetValue(RunManager.Instance,c.RunState);
            AccessTools.Property(typeof(RunManager),nameof(RunManager.AscensionManager)).SetValue(RunManager.Instance,new MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager(0));
            MegaCrit.Sts2.Core.Context.LocalContext.NetId=c.Players[0].NetId;previousTurn=turnField.GetValue(CombatManager.Instance);
            var turn=Activator.CreateInstance(turnField.FieldType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[c],null)!;
            AccessTools.Property(turnField.FieldType,"IsInProgress").SetValue(turn,true);AccessTools.Property(turnField.FieldType,"IsStarting").SetValue(turn,false);turnField.SetValue(CombatManager.Instance,turn);
        }
        internal object Turn => turnField.GetValue(CombatManager.Instance)!;
        public void Dispose(){turnField.SetValue(CombatManager.Instance,previousTurn);MegaCrit.Sts2.Core.Context.LocalContext.NetId=previousLocal;AccessTools.Property(typeof(RunManager),"State").SetValue(RunManager.Instance,previousRun);AccessTools.Property(typeof(RunManager),nameof(RunManager.AscensionManager)).SetValue(RunManager.Instance,previousAscension);}
    }
    private static void Pow<T>(Creature target,int amount)where T:PowerModel=>ModelDb.Power<T>().ToMutable().ApplyInternal(target,amount,true);
    private static T Hand<T>(Player p,CombatState c,bool upgrade=false)where T:CardModel
    {var card=c.CreateCard<T>(p);if(upgrade)card.UpgradeInternal();p.PlayerCombatState!.Hand.AddInternal(card);return card;}
    private static (Player[],CombatState,Creature) Board(int count,int hp,int energy)
    {
        var party=Enumerable.Range(0,count).Select(i=>Player.CreateForNewRun<Deprived>(UnlockState.all,(ulong)(58000+i))).ToArray();
        var c=new CombatState(runState:RunState.CreateForTest(party,seed:"SZG-COMBAT-REPAIR")){RoundNumber=1};
        foreach(var p in party){p.ResetCombatState();c.AddPlayer(p);p.MaxEnergy=3;p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(energy);p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);}
        var e=c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"0");c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(hp);e.SetCurrentHpInternal(hp);e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);return(party,c,e);
    }
    private static bool Format(ref string __result){__result="";return false;}
    private static void Check(bool ok,string why){if(!ok)throw new Exception("SZG combat repair: "+why);}
}

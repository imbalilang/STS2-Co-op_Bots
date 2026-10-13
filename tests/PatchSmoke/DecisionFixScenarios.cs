using System.Text.Json;
using CoopBots;
using CoopBots.Building;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

// Native boss-entry decks/relics come from XZMG9W041DLG floor 31. Controlled
// hands below isolate each mechanism; they are not a full historical replay.
// R2 2026-10-03: production faults returned exit 1; raw logs and commands in
// persistent-upgrade (r2-extra-results.json): System.Exception: Decision fixes: persistent native card upgrades must not be held as empty defence
// ally-lethal (r2-extra-results.json): System.Exception: Decision fixes: hand search must realize generated attacks after paying the producer
// work/decision-fixes-20261003/r2-results.json. Restored full gates recorded separately.
// hold-family: System.Exception: Decision fixes: native no-attack turn must conserve both debuff and block exhausts
// reserve: System.Exception: Decision fixes: beam must distinguish retained and wasted one-shot options
// opening: System.Exception: Decision fixes: budget fallback must attack instead of wasting a defensive exhaust
// prevention: System.Exception: Decision fixes: real attack prevention must still spend exhaust cards
// exhaust-hook: System.Exception: Decision fixes: exhaust-trigger draw must remain a usable payoff
// dispatch: System.Exception: Decision fixes: Gambling Chip must replace junk and retain the affordable free attacks
// relic-minimum: System.Exception: Decision fixes: Gambling Chip must replace junk and retain the affordable free attacks
// relic-exhaust: System.Exception: Decision fixes: mandatory relic exhaust must remove junk before useful cards
// relic-generated: System.Exception: Decision fixes: generated relic choice must value the resulting combat hand
// relic-keep: System.Exception: Decision fixes: optional redraw must keep a good hand when replacements are worse
// human: System.Exception: Decision fixes: relic selector must leave human choices untouched
// team-shiv: System.Exception: Decision fixes: team Shiv generation must model owner cards and allied damage separately
// generated-damage: System.Exception: Decision fixes: hand search must realize generated attacks after paying the producer
// energy: System.Exception: Decision fixes: energy generation must fund actual attacks and pay its HP cost
// draw-upgrade: System.Exception: Decision fixes: extra-draw upgrade must unlock the actual card beyond the old draw horizon
internal static class DecisionFixScenarios
{
    static bool Format(ref string __result) { __result = ""; return false; }
    static void Check(bool ok, string why) { if (!ok) throw new Exception("Decision fixes: " + why); }
    internal static void Run()
    {
        TestEnvironment.Ensure();
        new Harmony("coopbots.decision-fixes").Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(DecisionFixScenarios), nameof(Format)));
        Exhaust(); LastingExhaust(); JointOrder(); ShortVictoryPoison(); Relics(); Upgrades();
    }
    static (Player P, CombatState C, Creature E) Board()
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 103);
        var c = new CombatState(runState: RunState.CreateForTest(new[] { p }, seed: "DECISION-FIXES"));
        p.ResetCombatState(); c.AddPlayer(p); p.MaxEnergy = 3; p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
        p.PlayerCombatState.GainEnergy(3); p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
        var e = c.CreateCreature(ModelDb.Monster<KnowledgeDemon>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(500); e.SetCurrentHpInternal(500);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["CURSE_OF_KNOWLEDGE_MOVE"], true);
        return (p, c, e);
    }
    static CardModel Hand<T>(Player p, CombatState c) where T : CardModel
    { var card = c.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(card); return card; }
    static void Exhaust()
    {
        var (p, c, e) = Board(); var party = new[] { p };
        var wail = Hand<PiercingWail>(p, c); var impervious = Hand<Impervious>(p, c); var strike = Hand<StrikeSilent>(p, c);
        var root = KernelSession.Capture(c);
        Check(RolloutRun.ShouldHold(root, party, p, wail) && RolloutRun.ShouldHold(root, party, p, impervious),
            "native no-attack turn must conserve both debuff and block exhausts");
        var spent = root.CardBranches(wail, null, 1).Single(b => b.Boundary.Length == 0).State;
        Check(KernelExhaustPolicy.Reserve(root, p) > KernelExhaustPolicy.Reserve(spent, p),
            "beam must distinguish retained and wasted one-shot options");
        foreach (var joint in new[] { false, true })
        {
            var chosen = KernelTournament.Run(root.Fork(), party, party,
                new(IncludePotions: false, ReserveMs: 0, EnableJointSearch: joint), 1);
            Check(chosen.FirstAction?.Card is StrikeSilent,
                "budget fallback must attack instead of wasting a defensive exhaust");
        }
        var move = e.Monster!.MoveStateMachine!.States.Values.OfType<MoveState>()
            .First(m => m.Intents.Any(i => i is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent));
        e.Monster.SetMoveImmediate(move, true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e, 15, true);
        var danger = KernelSession.Capture(c);
        Check(!RolloutRun.ShouldHold(danger, party, p, wail) && !RolloutRun.ShouldHold(danger, party, p, impervious),
            "real attack prevention must still spend exhaust cards");
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine.States["CURSE_OF_KNOWLEDGE_MOVE"], true);
        ModelDb.Power<DarkEmbracePower>().ToMutable().ApplyInternal(p.Creature, 1, true);
        p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<Bludgeon>(p));
        var hooks = KernelSession.Capture(c);
        Check(!RolloutRun.ShouldHold(hooks, party, p, wail), "exhaust-trigger draw must remain a usable payoff");
        var (upgradeOwner, upgradeCombat, _) = Board();
        var apotheosis = Hand<Apotheosis>(upgradeOwner, upgradeCombat); Hand<StrikeSilent>(upgradeOwner, upgradeCombat);
        var upgrades = KernelSession.Capture(upgradeCombat);
        Check(!RolloutRun.ShouldHold(upgrades, new[] { upgradeOwner }, upgradeOwner, apotheosis),
            "persistent native card upgrades must not be held as empty defence");
        Console.WriteLine("PASS: exhaust conservation reaches root, beam and cutoff fallback without hiding native payoffs.");
    }
    static void LastingExhaust()
    {
        // R2 2026-10-04, work/exhaust-duration-20261004/r2-results.json:
        // four retained production faults exited 1, then were restored:
        // System.Exception: Decision fixes: multi-turn Vulnerable must not be conserved as empty one-turn defence
        // System.Exception: Decision fixes: end-turn repair must retain the value of unspent Vulnerable duration
        // System.Exception: Decision fixes: Bully damage hint must cash out every added Vulnerable stack
        // System.Exception: Decision fixes: already-Vulnerable targets must retain their stack-scaling setup payoff
        // The original policy also failed: multi-turn Vulnerable must not be conserved as empty one-turn defence.
        foreach (var upgraded in new[] { false, true })
        {
            var (p, c, e) = Board(); var party = new[] { p };
            var tremble = Hand<Tremble>(p, c);
            if (upgraded) tremble.UpgradeInternal();
            p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
            var root = KernelSession.Capture(c);
            var after = root.CardBranches(tremble, e, 1).Single(b => b.Boundary.Length == 0).State;
            Check(after.Power<VulnerablePower>(e) == (upgraded ? 4 : 3), "native Tremble must apply its actual duration");
            Check(!RolloutRun.ShouldHold(root, party, p, tremble),
                "multi-turn Vulnerable must not be conserved as empty one-turn defence");
            var chosen = KernelTournament.Run(root.Fork(), party, party,
                new(IncludePotions: false, ReserveMs: 0), 1);
            Check(chosen.FirstAction?.Card == tremble, "cutoff fallback must play lasting setup without an attack in hand");
            var repair = new KernelEndTurnRun(root, p, 5000);
            while (!repair.Advance(TimeSpan.FromMilliseconds(10))) { }
            Check(repair.Actions?.FirstOrDefault()?.Card == tremble,
                "end-turn repair must retain the value of unspent Vulnerable duration");
        }
        var (owner, combat, enemy) = Board(); var allies = new[] { owner };
        var shockwave = Hand<Shockwave>(owner, combat);
        var state = KernelSession.Capture(combat);
        Check(!RolloutRun.ShouldHold(state, allies, owner, shockwave),
            "multi-turn Weak must remain useful on a non-attacking turn without held attacks");
        foreach (var stacks in new[] { 9, 30 })
        {
            var (p, c, e) = Board(); var party = new[] { p };
            ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(e, stacks, true);
            var tremble = Hand<Tremble>(p, c); var bully = Hand<Bully>(p, c); var dominate = Hand<Dominate>(p, c);
            var root = KernelSession.Capture(c);
            Check(!RolloutRun.ShouldHold(root, party, p, tremble), "existing high Vulnerable must not impose a conservation ceiling");
            var after = root.CardBranches(tremble, e, 1).Single(b => b.Boundary.Length == 0).State;
            Check(after.AttackDamageHint(bully, e) - root.AttackDamageHint(bully, e) == 9,
                "Bully damage hint must cash out every added Vulnerable stack");
            Check(KernelTeamCoordination.SetupPayoff(root, party, tremble, e) >= 9,
                "already-Vulnerable targets must retain their stack-scaling setup payoff");
            var cash = after.CardBranches(dominate, e, 1).Single(b => b.Boundary.Length == 0).State;
            Check(cash.Power<StrengthPower>(p.Creature) == stacks + 4,
                "Dominate must cash out all native Vulnerable stacks without a duration cap");
            var line = KernelRollout.Run(root.Fork(), party, party, new(RolloutPolicyKind.Kill, MaxSteps: 3));
            Check(line.Actions.Select(a => a.Card).SequenceEqual(new[] { tremble, dominate, bully }),
                "stack setup must precede Strength conversion and the scaling attack");
        }
        {
            var (p, c, e) = Board(); var party = new[] { p };
            var tremble = Hand<Tremble>(p, c); Hand<Bully>(p, c);
            ModelDb.Power<ArtifactPower>().ToMutable().ApplyInternal(e, 1, true);
            var root = KernelSession.Capture(c);
            Check(KernelTeamCoordination.SetupPayoff(root, party, tremble, e) == 0,
                "a blocked Vulnerable application must not invent stack payoff");
        }
        Console.WriteLine("PASS: lasting exhaust debuffs remain playable through conservation, cutoff and end-turn repair.");
    }
    static void ShortVictoryPoison()
    {
        var (p, c, e) = Board();
        e.SetMaxHpInternal(8); e.SetCurrentHpInternal(8);
        ModelDb.Power<PoisonPower>().ToMutable().ApplyInternal(e, 4, true);
        var strike = Hand<StrikeIronclad>(p, c);
        Hand<BurningPact>(p, c);
        var root = KernelSession.Capture(c);
        Check(KernelShortVictoryRun.WorthChecking(root, [p]),
            "an already-poisoned 8 HP enemy must trigger the short-victory gate before the attack kills outright");
        var search = new KernelShortVictoryRun(root, [p]);
        while (!search.Done) search.Advance(TimeSpan.FromMilliseconds(2));
        Check(search.Result?.Record is { Victory: true },
            "the poisons kill at the start of the enemy turn must be recovered as a victory");
        Check(search.Result!.Script!.FirstOrDefault()?.Card is StrikeIronclad,
            "a poison-assisted kill must still cash the attack before optional setup");
        Console.WriteLine("PASS: short victory counts poison ticks and keeps the killing Strike in front.");
    }
    static void JointOrder()
    {
        const System.Reflection.BindingFlags Hidden = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic;
        static RolloutRun? Current(KernelJointTurnRun run) =>
            (RolloutRun?)typeof(KernelJointTurnRun).GetField("continuation", Hidden)!.GetValue(run);
        static bool SequenceEnabled(RolloutRun run)
        {
            var options = typeof(RolloutRun).GetField("o", Hidden)!.GetValue(run)!;
            return (bool)options.GetType()
                .GetProperty("SequenceLookahead", Hidden | System.Reflection.BindingFlags.Public)!
                .GetValue(options)!;
        }
        var (p, c, e) = Board();
        e.SetMaxHpInternal(500); e.SetCurrentHpInternal(500);
        var breakCard = Hand<Break>(p, c); breakCard.UpgradeInternal();
        Hand<Bully>(p, c);
        Hand<StrikeIronclad>(p, c);
        ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(e, 50, true);
        var root = KernelSession.Capture(c);
        var joint = new KernelJointTurnRun(root, [p], [p], new TournamentOptions(IncludePotions: false), 5000);
        var ticks = 0;
        while (Current(joint) is null && !joint.Done && ticks++ < 1000)
            joint.Advance(TimeSpan.FromMilliseconds(5));
        var continuation = Current(joint);
        Check(continuation is not null, "joint search must construct a continuation before ordering can run");
        Check(SequenceEnabled(continuation!),
            "the first production joint continuation must keep SequenceLookahead enabled; the live run replayed Bully before Break");
        while (!joint.Advance(TimeSpan.FromMilliseconds(5))) { }
        var first = joint.Result?.Script?.FirstOrDefault()?.Card;
        Check(ReferenceEquals(first, breakCard),
            "production joint search must order Break before Bully when both are in hand");
        Console.WriteLine("PASS: production joint search keeps sequence ordering enabled and plays Break before Bully.");
    }
    static void Relics()
    {
        var (p, c, _) = Board();
        var junk = Hand<Wound>(p, c); Hand<Anger>(p, c); Hand<Anger>(p, c); Hand<Anger>(p, c);
        for (var i = 0; i < 5; i++) p.PlayerCombatState!.DrawPile.AddInternal(c.CreateCard<StrikeIronclad>(p));
        var chip = ModelDb.Relic<GamblingChip>().ToMutable(); chip.Owner = p;
        var method = typeof(CardSelectCmd).GetMethod(nameof(CardSelectCmd.FromHandForDiscard))!;
        object[] args = [null!, p, new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 0, 999999999), null!, chip];
        AutoPilot.Set(p.NetId, true);
        try
        {
            Check(BotCardChoiceDispatcher.TrySelect(method, args, out var result), "native relic selector must answer a driven seat");
            var selected = ((IEnumerable<CardModel>)result!).ToArray();
            Console.WriteLine("GAMBLING pick=" + string.Join(',', selected.Select(x => x.Id.Entry)));
            Check(selected.Length == 1 && selected[0] == junk, "Gambling Chip must replace junk and retain the affordable free attacks");
            var root = KernelSession.Capture(c);
            var exhaust = root.SelectRelicCards(p, p.PlayerCombatState!.Hand.Cards.ToArray(), "Exhaust", 1, 1);
            Check(exhaust.Count == 1 && exhaust[0] == junk, "mandatory relic exhaust must remove junk before useful cards");
            CardModel[] options = [c.CreateCard<Anger>(p), c.CreateCard<Wound>(p)];
            Check(root.SelectRelicCards(p, options, "GenerateToHand", 1, 1).Single() == options[0],
                "generated relic choice must value the resulting combat hand");
            p.PlayerCombatState.Hand.Clear();
            Hand<Anger>(p, c); Hand<Anger>(p, c); Hand<Anger>(p, c);
            p.PlayerCombatState.DrawPile.Clear();
            for (var i = 0; i < 5; i++) p.PlayerCombatState.DrawPile.AddInternal(c.CreateCard<Wound>(p));
            Check(BotCardChoiceDispatcher.TrySelect(method, args, out result) && !((IEnumerable<CardModel>)result!).Any(),
                "optional redraw must keep a good hand when replacements are worse");
        }
        finally { AutoPilot.Clear(); }
        Check(!BotCardChoiceDispatcher.TrySelect(method, args, out _), "relic selector must leave human choices untouched");
        Console.WriteLine("PASS: native relic dispatcher compares zero, partial and full choices under exact min/max.");
    }
    static void Upgrades()
    {
        var save = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "knowledge-demon-20261003.json")),
            JsonSerializationUtility.GetTypeInfo<SerializableRun>())!;
        var run = RunState.FromSerializable(save);
        using var scope = DeckStrength.BeginEvaluation();
        foreach (var p in run.Players)
        foreach (var card in p.Deck.Cards.Where(c => c.IsUpgradable && c is PommelStrike or Acrobatics or Offering or BladeSymphony or BladeDance))
        {
            var value = BuildValue.UpgradeDelta(card, p);
            Console.WriteLine($"UPGRADE {p.NetId} {card.Id.Entry} {value.Total:F2} {value.Reason}");
        }
        var owner = run.Players[2];
        var symphony = owner.Deck.Cards.OfType<BladeSymphony>().First();
        var f = CardProfile.Of(symphony);
        Check(f.Effects.GeneratedShivs == 2 && f.Effects.AlliedShivDamage > 0,
            "team Shiv generation must model owner cards and allied damage separately");
        var slot = new ConstructionHand.Slot(symphony, f);
        var play = ConstructionHand.Solve([slot], 1, new(3, 20, 10));
        Check(play.Damage == 8 && play.EnergySpent == 2 && play.Utility is > -18 and < 10,
            "hand search must realize generated attacks after paying the producer");
        var blood = ModelDb.Card<Bloodletting>().ToMutable(); blood.Owner = owner;
        var strike = ModelDb.Card<StrikeSilent>().ToMutable(); strike.Owner = owner;
        var energy = ConstructionHand.Solve([new(blood, CardProfile.Of(blood)), new(strike, CardProfile.Of(strike)),
            new(strike, CardProfile.Of(strike)), new(strike, CardProfile.Of(strike))], 4, new(1, 0, 100));
        Check(energy.Damage == 18 && energy.SelfLoss > 0, "energy generation must fund actual attacks and pay its HP cost");
        var pommel = ModelDb.Card<PommelStrike>().ToMutable(); pommel.Owner = owner;
        var upgraded = CardProfile.Upgraded(pommel)!.Value;
        var wound = ModelDb.Card<Wound>().ToMutable(); wound.Owner = owner;
        ConstructionHand.Result DrawHand(CardModel card) => ConstructionHand.Solve(
            [new(card, CardProfile.Of(card)), new(wound, CardProfile.Of(wound)), new(strike, CardProfile.Of(strike))],
            1, new(2, 0, 100));
        var oldHand = DrawHand(pommel); var newHand = DrawHand(upgraded.Card);
        Check(newHand.Drawn == 3 && newHand.Damage >= oldHand.Damage + 7,
            "extra-draw upgrade must unlock the actual card beyond the old draw horizon");
        Console.WriteLine("PASS: resource payoff, team generation and native draw upgrades are evaluated together.");
    }
}

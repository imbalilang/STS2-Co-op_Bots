using System.Reflection;
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
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class PoisonPotionScenarios
{
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static (Player P, CombatState C, Creature E) Board(int attack = 0, int enemies = 1, int energy = 3)
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var c = new CombatState(runState: RunState.CreateForTest([p], seed: "POISON-POTION"));
        p.ResetCombatState(); c.AddPlayer(p); p.MaxEnergy = energy;
        p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
        p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(energy);
        Creature? first = null;
        for (var i = 0; i < enemies; i++)
        {
            var e = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, i.ToString());
            c.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(500); e.SetCurrentHpInternal(500);
            e.Monster.SetMoveImmediate(new MoveState(attack > 0 ? "ATTACK" : "BUFF", _ => Task.CompletedTask,
                attack > 0 ? new SingleAttackIntent(attack) : new BuffIntent()), true);
            if (attack > 1) ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e, attack - 1, true);
            first ??= e;
        }
        return (p, c, first!);
    }
    private static T Hand<T>(Player p, CombatState c) where T : CardModel
    { var card = c.CreateCard<T>(p); p.PlayerCombatState!.Hand.AddInternal(card); return card; }
    private static T Bottle<T>(Player p) where T : PotionModel
    { var q = (T)ModelDb.Potion<T>().ToMutable(); p.AddPotionInternal(q, silent: true); return q; }
    private static void Fill(Player p) { while (p.Potions.Count() < p.MaxPotionCount) Bottle<StrengthPotion>(p); }

    // R2 2026-09-27: 18 deliberate production regressions all exited 1.
    // Commands and raw logs: work/strength-20260927-v10/r2-results.json.
    // System.Exception: Kill must seed poison before BubbleBubble (upgrade=False).
    // System.Exception: Tactical must seed poison before BubbleBubble (upgrade=False).
    // System.Exception: Kill must target the already-poisoned enemy.
    // System.Exception: TopK must retain poison setup before cheap printed damage.
    // System.Exception: Draft must not count conditional poison as an independent source.
    // System.Exception: Full belt must spend twelve useful block.
    // System.Exception: Eleven useful block must not pretend to be a full twelve-point dose.
    // System.Exception: Area potion must subtract overkill from its thirty-damage benchmark.
    // System.Exception: A shield must reduce the effective direct damage dose.
    // System.Exception: A partially overflowing heal must not count as a full dose.
    // System.Exception: FLEX_POTION must spend for three affordable useful amplifications.
    // System.Exception: FLEX_POTION must share the energy budget across its three amplifications.
    // System.Exception: Predicted death must spend both useful bottles even if neither alone saves the seat.
    // System.Exception: An unfinished search must not be labeled predicted death.
    // System.Exception: Predicted death must expose a live choice potion rather than silently excluding it.
    // System.Exception: Cached card script must yield to an explicit full-value potion opportunity.
    // System.Exception: Potion-only pass must never select a card-only joint continuation.
    // System.Exception: Emergency must recognize a potion that amplifies the next attack: VULNERABLE_POTION
    internal static void Run()
    {
        new Harmony("coopbots.test.poison-potion.presentation").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(PoisonPotionScenarios), nameof(Format)));
        foreach (var upgrade in new[] { false, true })
        {
            var (p, c, e) = Board();
            var snake = Hand<Snakebite>(p, c); var bubble = Hand<BubbleBubble>(p, c);
            Hand<BubbleBubble>(p, c); var strike = Hand<StrikeSilent>(p, c);
            if (upgrade) { snake.UpgradeInternal(); bubble.UpgradeInternal(); }
            var root = KernelSession.Capture(c);
            foreach (var policy in Enum.GetValues<RolloutPolicyKind>())
            {
                var run = KernelRollout.Run(root.Fork(), [p], [p], new RolloutOptions(policy, MaxSteps: 4, MaxRounds: 1));
                Check(run.Actions[0].Card is Snakebite && run.Actions[1].Card is BubbleBubble,
                    $"{policy} must seed poison before BubbleBubble (upgrade={upgrade}).");
            }
            var opening = KernelTournament.FirstActions(root, [p], 1, new TournamentOptions(IncludePotions: false));
            Check(opening[0].Card is Snakebite, "TopK must retain poison setup before cheap printed damage.");
            var dry = root.Fork(); dry.Play(bubble, e, out _);
            Check(dry.Power<PoisonPower>(e) == 0, "BubbleBubble cannot seed its own poison.");
            Check(CardProfile.Of(bubble).Poison == 0 && !CardPoisonEffects.CanSeedPoison(bubble),
                "Draft must not count conditional poison as an independent source.");
            var noSource = Enumerable.Repeat<CardModel>(strike, 8).ToArray();
            var source = noSource.Append(snake).ToArray();
            Check(DraftCardEffects.Value(DraftCardEffects.Of(bubble), source, 3) > DraftCardEffects.Value(DraftCardEffects.Of(bubble), noSource, 3),
                "Conditional poison draft value must depend on a real source.");
        }
        var (targetOwner, targetCombat, clean) = Board(enemies: 2);
        clean.SetCurrentHpInternal(10);
        var poisoned = targetCombat.Enemies.Last();
        ModelDb.Power<PoisonPower>().ToMutable().ApplyInternal(poisoned, 3, true);
        Hand<BubbleBubble>(targetOwner, targetCombat);
        foreach (var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var line = KernelRollout.Run(KernelSession.Capture(targetCombat), [targetOwner], [targetOwner],
                new RolloutOptions(policy, MaxSteps: 1));
            Check(line.Actions[0].Target == poisoned, $"{policy} must target the already-poisoned enemy.");
        }
        Console.WriteLine("PASS: poison setup order in all policies, TopK and conditional drafting.");

        var (blockOwner, blockCombat, _) = Board(12);
        var block = Bottle<BlockPotion>(blockOwner); Fill(blockOwner);
        var blockRoot = KernelSession.Capture(blockCombat);
        var chosen = KernelPotionOpportunity.Choose(blockRoot, [blockOwner], [blockOwner]);
        Check(chosen?.Action.Potion is BlockPotion, "Full belt must spend twelve useful block.");
        var spent = blockRoot.Fork(); spent.UsePotion(block, blockOwner.Creature, out _);
        Check(spent.HeldPotionCount(blockOwner) == 2 && !spent.UsablePotions(blockOwner).Contains(block)
            && KernelPotionOpportunity.Choose(spent, [blockOwner], [blockOwner]) is null,
            "Spending one bottle must release the full-belt incentive and captured slot.");
        var (partial, partialCombat, _) = Board(11); Bottle<BlockPotion>(partial); Fill(partial);
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(partialCombat), [partial], [partial]) is null,
            "Eleven useful block must not pretend to be a full twelve-point dose.");
        Console.WriteLine("PASS: full-belt block uses actual incoming damage and releases the slot.");

        var (blast, blastCombat, blastEnemy) = Board(enemies: 3); Bottle<ExplosiveAmpoule>(blast); Fill(blast);
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(blastCombat), [blast], [blast])?.Action.Potion is ExplosiveAmpoule,
            "Full belt must spend thirty effective area damage.");
        blastEnemy.SetCurrentHpInternal(1);
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(blastCombat), [blast], [blast]) is null,
            "Area potion must subtract overkill from its thirty-damage benchmark.");
        Console.WriteLine("PASS: full-belt explosive potion measures effective damage across three targets.");

        var (fire, fireCombat, fireEnemy) = Board(); Bottle<FirePotion>(fire); Fill(fire);
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(fireCombat), [fire], [fire]) is not null,
            "Full belt must spend a full single-target damage dose.");
        fireEnemy.GainBlockInternal(1);
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(fireCombat), [fire], [fire]) is null,
            "A shield must reduce the effective direct damage dose.");
        var (healer, healCombat, _) = Board(); Bottle<BloodPotion>(healer); Fill(healer);
        healer.Creature.SetCurrentHpInternal(64);
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(healCombat), [healer], [healer]) is not null,
            "Full belt must use a full heal without waiting for lethal damage.");
        healer.Creature.SetCurrentHpInternal(65);
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(healCombat), [healer], [healer]) is null,
            "A partially overflowing heal must not count as a full dose.");
        Console.WriteLine("PASS: direct damage and healing share the effective-dose model.");

        foreach (var attack in new[] { true, false })
        {
            var (p, c, _) = Board(40);
            PotionModel q = attack ? Bottle<FlexPotion>(p) : Bottle<SpeedPotion>(p); Fill(p);
            if (attack) Hand<Bash>(p, c);
            for (var i = 0; i < 3; i++) { if (attack) Hand<StrikeIronclad>(p, c); else Hand<DefendIronclad>(p, c); }
            var root = KernelSession.Capture(c);
            Check(KernelPotionOpportunity.Choose(root, [p], [p])?.Action.Potion == q,
                $"{q.Id.Entry} must spend for three affordable useful amplifications.");
            p.PlayerCombatState!.LoseEnergy(1);
            Check(KernelPotionOpportunity.Choose(KernelSession.Capture(c), [p], [p]) is null,
                $"{q.Id.Entry} must share the energy budget across its three amplifications.");
        }
        Console.WriteLine("PASS: temporary potion value requires three payable useful plays.");

        var (dying, dyingCombat, _) = Board(100); dying.Creature.SetCurrentHpInternal(1);
        Bottle<FirePotion>(dying); Bottle<BlockPotion>(dying);
        var wipe = new TerminalRecord(false, EvidenceKind.VerifiedTerminal, "", [0], 1, 0);
        var dyingRoot = KernelSession.Capture(dyingCombat);
        Check(KernelPotionOpportunity.Choose(dyingRoot, [dying], [dying], wipe with { Kind = EvidenceKind.EstimatedCutoff }) is null,
            "An unfinished search must not be labeled predicted death.");
        for (var i = 0; i < 2; i++)
        {
            var next = KernelPotionOpportunity.Choose(dyingRoot, [dying], [dying], wipe);
            Check(next is not null && next.Reason.StartsWith("predicted-death"),
                "Predicted death must spend both useful bottles even if neither alone saves the seat.");
            Check(dyingRoot.UsePotion(next!.Action.Potion!, next.Action.Target, out _), "Emergency bottle must execute.");
        }
        Console.WriteLine("PASS: predicted death uses multiple useful bottles even on an unchanged wipe.");

        var (chooser, chooserCombat, _) = Board(); var generated = Bottle<AttackPotion>(chooser);
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(chooserCombat), [chooser], [chooser], wipe)?.Action.Potion == generated,
            "Predicted death must expose a live choice potion rather than silently excluding it.");
        Console.WriteLine("PASS: last-chance card generation is executed outside a cached simulation.");

        var (riders, riderCombat, _) = Board();
        Hand<StrikeIronclad>(riders, riderCombat);
        var vulnerable = Bottle<VulnerablePotion>(riders);
        var duplicate = Bottle<Duplicator>(riders);
        var riderRoot = KernelSession.Capture(riderCombat);
        foreach (var bottle in new PotionModel[] { vulnerable, duplicate })
        {
            var target = riderRoot.PotionTargets(bottle).First();
            var measured = KernelPotionOpportunity.Measure(riderRoot, bottle, target, [riders], emergency: true);
            Console.WriteLine($"Emergency rider {bottle.Id.Entry}: modeled={KernelSession.CanModelPotion(bottle)}, measured={measured}");
            Check(measured.Benefit > 0, "Emergency must recognize a potion that amplifies the next attack: " + bottle.Id.Entry);
        }
        Console.WriteLine("PASS: last-chance vulnerability and duplication value their next attack.");

        // The actual planner entry, with a cached card line, must emit the free action first.
        var planner = new KernelCombatPlanner();
        var card = Hand<StrikeIronclad>(blockOwner, blockCombat);
        var action = new KernelTeamSearch.Action(blockOwner, card, blockCombat.Enemies.First());
        typeof(KernelCombatPlanner).GetField("tournamentScript", Flags)!.SetValue(planner, new[] { action, action });
        typeof(KernelCombatPlanner).GetField("tournamentScriptCombat", Flags)!.SetValue(planner, blockCombat);
        var args = new object?[] { blockCombat, new[] { blockOwner }, true, "full-belt-cached-line", null };
        var result = typeof(KernelCombatPlanner).GetMethod("TryTournamentDecision", Flags)!.Invoke(planner, args);
        Check(result!.ToString() == "Decided" && planner.ConfirmedPotion?.Potion is BlockPotion && args[4] is null,
            "Cached card script must yield to an explicit full-value potion opportunity.");
        Console.WriteLine("PASS: production planner spends a full-value potion before its cached card script.");

        // R2 V11: slot limit reverted to bottle count, exit 1; raw failure:
        // System.Exception: Last occupied slot must remain usable after leading bottles are spent (capacity=3, boundary=potion-not-captured).
        foreach (var capacity in new[] { 3, 4 })
        {
            var (sparse, sparseCombat, sparseEnemy) = Board();
            sparse.AddToMaxPotionCount(capacity - sparse.MaxPotionCount);
            for (var i = 0; i < capacity - 1; i++) Bottle<FirePotion>(sparse);
            var last = Bottle<CureAll>(sparse);
            foreach (var leading in sparse.Potions.Where(q => q != last).ToArray()) sparse.DiscardPotionInternal(leading, silent: true);
            var sparseRoot = KernelSession.Capture(sparseCombat);
            Check(sparse.GetPotionSlotIndex(last) == capacity - 1 && sparse.Potions.Count() == 1,
                "Sparse potion fixture must preserve empty leading slots.");
            var branch = sparseRoot.Fork();
            Check(branch.UsePotion(last, sparse.Creature, out var why) && branch.Energy(sparse) == 4,
                $"Last occupied slot must remain usable after leading bottles are spent (capacity={capacity}, boundary={why}).");
            var lastChance = KernelPotionOpportunity.Choose(sparseRoot, [sparse], [sparse], wipe);
            Check(lastChance?.Action.Potion == last && lastChance.Benefit > 0,
                "Predicted death must see the remaining CureAll beyond empty slots.");
            var sparsePlanner = new KernelCombatPlanner();
            var sparseCard = Hand<StrikeIronclad>(sparse, sparseCombat);
            var sparseAction = new KernelTeamSearch.Action(sparse, sparseCard, sparseEnemy);
            typeof(KernelCombatPlanner).GetField("tournamentScript", Flags)!.SetValue(sparsePlanner, new[] { sparseAction, sparseAction });
            typeof(KernelCombatPlanner).GetField("tournamentScriptCombat", Flags)!.SetValue(sparsePlanner, sparseCombat);
            typeof(KernelCombatPlanner).GetField("tournamentScriptRecord", Flags)!.SetValue(sparsePlanner, wipe);
            var sparseArgs = new object?[] { sparseCombat, new[] { sparse }, true, "sparse-last-chance", null };
            typeof(KernelCombatPlanner).GetMethod("TryTournamentDecision", Flags)!.Invoke(sparsePlanner, sparseArgs);
            Check(sparsePlanner.ConfirmedPotion?.Potion == last && sparseArgs[4] is null,
                "Production planner must drink the last sparse-slot bottle before its losing cached cards.");
        }
        Console.WriteLine("PASS: sparse three- and four-slot belts retain their last bottle through the production planner.");

        var (pass, passCombat, _) = Board(); Bottle<BlockPotion>(pass); Hand<StrikeIronclad>(pass, passCombat);
        var compared = KernelTournament.Run(KernelSession.Capture(passCombat), [pass], [pass],
            new TournamentOptions(IncludePotions: true, PotionsOnly: true, EnableJointSearch: true,
                UsePolicyPortfolio: false, Rollout: new RolloutOptions(MaxSteps: 2)), 5000);
        Check(compared.FirstAction?.Potion is not null && compared.Considered.All(r => r.ResourcesConsumed >= 1),
            "Potion-only pass must never select a card-only joint continuation.");
        Console.WriteLine("PASS: potion-only pass cannot return a zero-potion route.");
    }
}

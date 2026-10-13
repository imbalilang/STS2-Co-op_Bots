using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
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

internal static class FlankingLifetimeScenarios
{
    // LIVE 2026-09-29, A10 4-bot, floor 25 ENTOMANCER_ELITE. The cached script was
    // labelled `ending=VICTORY` (VerifiedTerminal), all 81 of its steps were replayed,
    // and the party still wiped with 218/789 left on the elite. Root cause found here:
    // FLANKING. The live power removes itself in AfterSideTurnEnd
    // (`if (participants.Contains(Owner)) Remove`); the kernel only ever mirrored the
    // damage half, so the "double damage from your allies" multiplier stayed on the
    // enemy for the REST OF THE SIMULATED FIGHT. R2-R6 were replayed action-for-action
    // identically to live (16/14/13/13/6 plays, same order), yet the rollout doubled
    // every later round's output and "killed" an elite live left at 218. KNOCKDOWN is
    // the same file shape (KnockdownPower.AfterSideTurnEnd) and is pinned here too.
    //
    // R2 mutate-to-red (the two TriggerRegular cases set to `when false`, i.e. the
    // pre-fix behaviour, compile-clean) reproduced exactly:
    //   FLANKING must be gone by the next player round: the live power removes itself
    //   at the owner's side turn end, a surviving copy multiplies every later round of
    //   the plan; foe still has 1.
    //   FLANKING: the ally Strike must be back to its unamplified value in round 2, got 12.
    //   KNOCKDOWN must be gone by the next player round: ... foe still has 2.
    //   KNOCKDOWN: the ally Strike must be back to its unamplified value in round 2, got 12.
    // Restored code prints the PASS line below. Registered as D6 in
    // src/CoopBots.Kernel/VENDOR_DEVIATIONS.md.
    private static bool Format(ref string __result) { __result = ""; return false; }

    internal static void Run()
    {
        new Harmony("coopbots.flanking.lifetime.presentation").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(FlankingLifetimeScenarios), nameof(Format)));
        var errors = new List<string>();
        Probe("FLANKING", () => AuditLifetime<Flanking, FlankingPower>("FLANKING", applierDamage: 6, allyDamage: 12, errors), errors);
        Probe("KNOCKDOWN", () => AuditLifetime<Knockdown, KnockdownPower>("KNOCKDOWN", applierDamage: 6, allyDamage: 12, errors), errors);
        Probe("CAPTURED", () => AuditCapturedLivePower(errors), errors);
        if (errors.Count > 0)
            throw new Exception(string.Join(Environment.NewLine, errors));
        Console.WriteLine("PASS: FLANKING/KNOCKDOWN multipliers do not outlive the turn that applied them.");
    }

    /// <summary>
    /// The fixture measures both halves of the live rule: the applier's own attack keeps
    /// full damage, every other seat's attack is multiplied — and neither survives the
    /// owner's side turn end. Damage is read off real card branches, not off the power
    /// number, so a "remove the power but keep the multiplier" fix cannot pass.
    /// </summary>
    private static void AuditLifetime<TCard, TPower>(string label, int applierDamage, int allyDamage,
        List<string> errors)
        where TCard : CardModel
        where TPower : PowerModel
    {
        var (applier, ally, combat, foe) = Board(label);
        var setup = combat.CreateCard<TCard>(applier);
        applier.PlayerCombatState!.Hand.AddInternal(setup);
        var applierStrike = combat.CreateCard<StrikeIronclad>(applier);
        applier.PlayerCombatState.Hand.AddInternal(applierStrike);
        var allyStrike = combat.CreateCard<StrikeIronclad>(ally);
        ally.PlayerCombatState!.Hand.AddInternal(allyStrike);

        var root = KernelSession.Capture(combat).Fork();
        var baseline = Strike(root, allyStrike, foe, "baseline ally Strike");
        Check(errors, baseline == 6, $"{label}: fixture must open on an unamplified Strike, got {baseline}.");

        if (!root.Play(setup, foe, out var playBoundary))
        {
            errors.Add($"{label}: the fixture could not play the setup card ({playBoundary}).");
            return;
        }
        Check(errors, root.Power<TPower>(foe) > 0, $"{label}: the setup card must apply its power.");

        var own = Strike(root, applierStrike, foe, $"{label} applier Strike");
        Check(errors, own == applierDamage,
            $"{label}: the applier's own attack must keep full damage ({applierDamage}), got {own}.");
        var allyHit = Strike(root, allyStrike, foe, $"{label} ally Strike");
        Check(errors, allyHit == allyDamage,
            $"{label}: another seat's attack must be multiplied ({allyDamage}), got {allyHit}.");

        // Cross the round. Ending the last seat's turn resolves the enemy phase, which is
        // exactly where the live power's AfterSideTurnEnd fires.
        if (!root.EndTurn(applier, 2, out var firstBoundary))
        {
            errors.Add($"{label}: could not end the applier's turn ({firstBoundary}).");
            return;
        }
        if (!root.EndTurn(ally, 2, out var secondBoundary))
        {
            errors.Add($"{label}: the enemy phase must resolve ({secondBoundary}).");
            return;
        }
        Check(errors, root.Power<TPower>(foe) == 0,
            $"{label} must be gone by the next player round: the live power removes itself at the owner's "
            + $"side turn end, a surviving copy multiplies every later round of the plan; foe still has "
            + $"{root.Power<TPower>(foe)}.");

        var nextRound = root.Hand(ally).FirstOrDefault(c => c is StrikeIronclad);
        if (nextRound is null)
        {
            errors.Add($"{label}: the next round must still hold a Strike for the second measurement.");
            return;
        }
        var after = Strike(root, nextRound, foe, $"{label} ally Strike in round 2");
        Check(errors, after == 6,
            $"{label}: the ally Strike must be back to its unamplified value in round 2, got {after}.");
    }

    /// <summary>
    /// A board that already carries the LIVE game's FLANKING: the card is always applied as
    /// `PowerCmd.Apply(..., 2m, ...)` and the live power renders `DynamicVars["Applier"]` as
    /// the platform display name. Both are the live game's encodings, and the kernel has to
    /// read them as one stack on the applier's teammates, exactly like a card the kernel
    /// played itself.
    /// </summary>
    private static void AuditCapturedLivePower(List<string> errors)
    {
        var (applier, ally, combat, foe) = Board("CAPTURED-LIVE");
        var applierStrike = combat.CreateCard<StrikeIronclad>(applier);
        applier.PlayerCombatState!.Hand.AddInternal(applierStrike);
        var allyStrike = combat.CreateCard<StrikeIronclad>(ally);
        ally.PlayerCombatState!.Hand.AddInternal(allyStrike);

        var live = ModelDb.Power<FlankingPower>().ToMutable();
        live.Applier = applier.Creature;
        live.ApplyInternal(foe, 2, silent: true);
        ((MegaCrit.Sts2.Core.Localization.DynamicVars.StringVar)live.DynamicVars["Applier"]).StringValue = "S-jie";

        var root = KernelSession.Capture(combat).Fork();
        var mine = Strike(root, applierStrike, foe, "captured-live applier Strike");
        Check(errors, mine == 6,
            $"CAPTURED: a live FLANKING must not amplify the applier's own attack, got {mine}.");
        var theirs = Strike(root, allyStrike, foe, "captured-live ally Strike");
        Check(errors, theirs == 12,
            $"CAPTURED: a live FLANKING ('Applier' is a platform name, an amount of 2 per card) "
            + $"must read as one stack for the teammates, got {theirs}.");

        // Three live applications on one board must still compound to 8x: the live units are
        // converted to stacks, never compounded raw (2^(2+2+2) would be 64x).
        var (applier3, ally3, combat3, foe3) = Board("CAPTURED-LIVE-3");
        var applierStrike3 = combat3.CreateCard<StrikeIronclad>(applier3);
        applier3.PlayerCombatState!.Hand.AddInternal(applierStrike3);
        var allyStrike3 = combat3.CreateCard<StrikeIronclad>(ally3);
        ally3.PlayerCombatState!.Hand.AddInternal(allyStrike3);
        for (var i = 0; i < 3; i++)
        {
            var copy = ModelDb.Power<FlankingPower>().ToMutable();
            copy.Applier = applier3.Creature;
            copy.ApplyInternal(foe3, 2, silent: true);
            ((MegaCrit.Sts2.Core.Localization.DynamicVars.StringVar)copy.DynamicVars["Applier"]).StringValue = "S-jie";
        }

        var root3 = KernelSession.Capture(combat3).Fork();
        var eight = Strike(root3, allyStrike3, foe3, "three captured-live ally Strikes");
        Check(errors, eight == 48,
            $"CAPTURED: three live FLANKING applications must still compound to 8x (48 on a base 6 "
            + $"Strike), got {eight}.");
        var own3 = Strike(root3, applierStrike3, foe3, "three captured-live applier Strikes");
        Check(errors, own3 == 6,
            $"CAPTURED: three live FLANKING applications must not amplify the applier, got {own3}.");
    }

    private static int Strike(KernelSession session, CardModel card, Creature foe, string what)
    {
        var branch = session.CardBranches(card, foe, 1).FirstOrDefault(b => b.Boundary.Length == 0);
        if (branch is null)
        {
            throw new Exception($"The fixture could not resolve {what}.");
        }
        return session.Hp(foe) - branch.State.Hp(foe);
    }

    private static (Player Applier, Player Ally, CombatState Combat, Creature Foe) Board(string seed)
    {
        var applier = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 31, 1));
        var ally = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 32, 2));
        var party = new[] { applier, ally };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: seed));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            // 5, not 3: the applier must still be able to attack after paying for the setup
            // card, otherwise the applier-exclusion half of the rule cannot be measured
            // (KNOCKDOWN costs 3 and is itself an attack).
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(5);
            // A crossing flushes the hand and draws the next one, so the draw pile has to
            // exist or the round settles with an exception instead of a turn boundary.
            for (var i = 0; i < 5; i++) p.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "hive");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat();
        foe.SetMaxHpInternal(400); foe.SetCurrentHpInternal(400);
        // The move has to come FROM the state machine, not be a detached MoveState: a
        // crossing prepares the monster's next round by walking FollowUpState.
        foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (applier, ally, combat, foe);
    }

    private static void Check(List<string> errors, bool ok, string message)
    {
        if (!ok) errors.Add(message);
    }

    /// <summary>One broken fixture must not hide the other label's findings.</summary>
    private static void Probe(string label, Action body, List<string> errors)
    {
        try { body(); }
        catch (Exception error) { errors.Add($"{label}: fixture threw: {error.Message}"); }
    }
}

using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

/// <summary>
/// Diagnostic probe for the 2026-10-05 live run (seed RFTC0WGM4A3P): the round-6
/// refinement adopted `new=VICTORY enemyHp=347->0` while the live game wiped.
/// The live boundary had four SandpitPower instances on the boss (amounts 1/2/1/1)
/// and the refinement's boundary check passed. This probe asks three questions:
///   1. Does the captured branch preserve each seat's own countdown?
///   2. Does playing FranticEscape increment the PLAYING seat's countdown?
///   3. Does the party state text used by refinement distinguish a permutation of
///      which seat holds the "2"?
/// and then rolls the fight forward under the live assignment vs the permutation.
/// </summary>
internal static class SandpitVictoryProbe
{
    private sealed record Setup(CombatState Combat, Player[] Party, Creature Enemy);

    internal static void Run()
    {
        // The console harness has no localization tables; power dynamic variables
        // materialize through LocString and NRE without this stub.
        new Harmony("coopbots.test.sandpit.probe").Patch(
            AccessTools.Method(typeof(LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(SandpitVictoryProbe), nameof(FormatText)));
        // Live round-6 assignment, inferred from the death order and the executed
        // escape: S/B2/B3 held 1, B1 held 2 (S escaped hers, B2/B3 popped, B1 was
        // killed by THRASH).
        var live = Build([1, 2, 1, 1]);
        // Permutation that the boundary text cannot see: B2 holds the 2 instead of B1.
        var permuted = Build([1, 1, 2, 1]);

        Console.WriteLine("== 1) capture preserves per-seat countdown ==");
        Report("live   ", live);
        Report("permuted", permuted);

        Console.WriteLine("== 2) party text (refinement boundary check) vs permutation ==");
        var liveText = KernelSession.CaptureLivePartyText(live.Combat);
        var permutedText = KernelSession.CaptureLivePartyText(permuted.Combat);
        Console.WriteLine("identical party text = " + string.Equals(liveText, permutedText, StringComparison.Ordinal));
        foreach (var line in liveText.Split('\n'))
            if (line.Contains("SANDPIT"))
                Console.WriteLine("  live text: " + line.Trim());

        Console.WriteLine("== 2b) production comparison shape: predicted(B) vs live(A) ==");
        var branchB = KernelSession.Capture(permuted.Combat);
        var predictedB = branchB.PartyStateText();
        var liveA = KernelSession.CaptureLivePartyText(live.Combat);
        Console.WriteLine("predicted(B)==live(A): " + string.Equals(predictedB, liveA, StringComparison.Ordinal));
        Console.WriteLine("FirstLiveDifference(liveA, predictedB): "
            + (KernelSession.FirstLiveDifference(live.Combat, predictedB) ?? "<null>"));
        Console.WriteLine("live pit order (seat:amount)   = " + PitOrder(live.Combat, live.Party));

        Console.WriteLine("== 3) FranticEscape must increment the playing seat's own pit ==");
        var escape = live.Combat.CreateCard<FranticEscape>(live.Party[2]);
        live.Party[2].PlayerCombatState!.Hand.AddInternal(escape);
        var fork = KernelSession.Capture(live.Combat).Fork();
        if (fork.Play(escape, null, out var why))
            Console.WriteLine("B2 played escape; amounts=S,B1,B2,B3 = " + Amounts(fork, live.Party));
        else
            Console.WriteLine("B2 escape refused: " + why);

        Console.WriteLine("== 4) rollout under live assignment ==");
        Roll("live   ", live);
        Console.WriteLine("== 5) rollout under permuted assignment ==");
        Roll("permuted", permuted);

        Console.WriteLine("== 6) replay the LIVE round-6 script on the live assignment ==");
        ReplayLiveRound6(live);

        Console.WriteLine("== 7) production TournamentRun from the same boundary ==");
        Tournament("live   ", live);
        Tournament("permuted", permuted);

        Console.WriteLine("== 8) THRASH damage model vs live numbers (live: S 6, B1 raw 8) ==");
        ThrashDamage();
    }

    private static void ThrashDamage()
    {
        var s = Build([4, 4, 4, 4]); // no pit pops this round
        foreach (var p in s.Party) p.Creature.SetCurrentHpInternal(80);
        var session = KernelSession.Capture(s.Combat).Fork();
        foreach (var p in s.Party) session.EndTurn(p, out _);
        Console.WriteLine("post-THRASH hp=[80,80,80,80] -> [" + Hps(session, s.Party) + "] "
            + $"damage=[{string.Join(",", s.Party.Select(p => 80 - session.Hp(p.Creature)))}] "
            + $"enemyPhase={session.EnemyPhaseCompleted} terminal={session.HasTerminal}");
    }

    private static void Tournament(string tag, Setup s)
    {
        var root = KernelSession.Capture(s.Combat);
        var options = new TournamentOptions(
            TopK: 6,
            Objective: KernelPotionPolicy.Objective,
            Rollout: new RolloutOptions(Seed: 6));
        var run = new TournamentRun(root, s.Party, s.Party, options, 6400);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!run.Done && clock.Elapsed < TimeSpan.FromSeconds(40))
            run.Advance(TimeSpan.FromMilliseconds(2));
        var best = run.BestCompleted;
        Console.WriteLine($"{tag}: done={run.Done} rollouts={run.Rollouts} bestVictory={best?.Record?.Victory} "
            + $"enemyHp={best?.Record?.RemainingEnemyHp} script={best?.Script?.Count ?? 0} stop={best?.StopReason}");
        if (best?.Script is not { Count: > 0 }) return;
        var validation = new KernelScriptReplayRun(root, s.Party, best, maxRounds: 80);
        while (!validation.Done) validation.Advance(TimeSpan.FromMilliseconds(1));
        var rec = validation.Result?.Record;
        Console.WriteLine($"  validated: victory={rec?.Victory} kind={rec?.Kind} enemyHp={rec?.RemainingEnemyHp} "
            + $"postHp=[{string.Join(",", rec?.PostCombatHp ?? [])}] failure={validation.Failure}");
        var aliveStates = validation.Result?.ScriptAliveStates;
        if (aliveStates is not null)
            for (var i = 0; i < aliveStates.Count && i < 28; i++)
                Console.WriteLine($"    step{i}: alive=[{string.Join(",", aliveStates[i])}]");
        var replayScript = validation.Result?.Script ?? best.Script;
        if (replayScript is not null)
            for (var i = 0; i < replayScript.Count && i < 20; i++)
            {
                var a = replayScript[i];
                Console.WriteLine($"    script{i}: player={a.Player.NetId} "
                    + (a.EndTurn ? "END-TURN" : (a.Card?.Id.Entry ?? a.Potion?.Id.Entry ?? "?"))
                    + (a.Card?.CurrentUpgradeLevel > 0 ? "+" : ""));
            }
    }

    private static void ReplayLiveRound6(Setup s)
    {
        var party = s.Party;
        var enemy = s.Enemy;
        CardModel Card<T>(Player p) where T : CardModel
        {
            var card = s.Combat.CreateCard<T>(p);
            p.PlayerCombatState!.Hand.AddInternal(card);
            return card;
        }
        // Cards must exist before the capture so the branch can resolve them.
        var b3Survivor = Card<DefendSilent>(party[3]);      // SURVIVOR+ stand-in (discard choice)
        var b1Bash = Card<Bash>(party[1]);
        var b2Dash = Card<Dash>(party[2]);
        var sStrike = Card<StrikeIronclad>(party[0]);
        var b2Strike = Card<StrikeSilent>(party[2]);
        var sAnger = Card<Anger>(party[0]);
        var sThrash = Card<Thrash>(party[0]);
        var b3Strike = Card<StrikeSilent>(party[3]);
        var b1Defend = Card<DefendIronclad>(party[1]);
        var sEscape = Card<FranticEscape>(party[0]);
        var b3Cut = Card<StrikeSilent>(party[3]);           // PRECISE_CUT stand-in
        var session = KernelSession.Capture(s.Combat).Fork();

        void Play(CardModel card, Player owner, Creature? target)
        {
            var ok = session.Play(card, target, out var why);
            Console.WriteLine($"  play {card.Id.Entry} by {owner.NetId}: ok={ok} why={why} hp=[{Hps(session, party)}] pits=[{Amounts(session, party)}]");
        }
        Play(b3Survivor, party[3], null);
        Play(b1Bash, party[1], enemy);
        Play(b2Dash, party[2], enemy);
        Play(sStrike, party[0], enemy);
        Play(b2Strike, party[2], enemy);
        Play(sAnger, party[0], enemy);
        Play(sThrash, party[0], enemy);
        Play(b3Strike, party[3], enemy);
        Play(b1Defend, party[1], null);
        Play(sEscape, party[0], null);
        Play(b3Cut, party[3], enemy);
        foreach (var p in party)
        {
            var ok = session.EndTurn(p, out var why);
            Console.WriteLine($"  end-turn {p.NetId}: ok={ok} why={why} enemyPhase={session.EnemyPhaseCompleted} hp=[{Hps(session, party)}]");
        }
        Console.WriteLine($"post-round-6: terminal={session.HasTerminal} victory={session.HasWon} "
            + $"enemyHp={session.RemainingOpposition().Hp} hp=[{Hps(session, party)}] pits=[{Amounts(session, party)}]");
    }

    private static string Hps(KernelSession session, Player[] party)
        => string.Join(",", party.Select(p => session.Hp(p.Creature)));

    private static void Report(string tag, Setup s)
        => Console.WriteLine($"{tag}: amounts(S,B1,B2,B3) = " + Amounts(KernelSession.Capture(s.Combat), s.Party));

    private static bool FormatText(LocString __instance, ref string __result)
    { __result = __instance.LocEntryKey; return false; }

    private static string Amounts(KernelSession session, Player[] party)
        => string.Join(",", party.Select(p => session.SandpitTurnsRemaining(p.Creature)?.ToString() ?? "null"));

    private static string PitOrder(CombatState combat, Player[] party)
        => string.Join(",", combat.Enemies
            .SelectMany(e => e.Powers.OfType<SandpitPower>())
            .Select(p => $"{Array.FindIndex(party, x => ReferenceEquals(x.Creature, p.Target))}:{p.Amount}"));

    private static void Roll(string tag, Setup s)
    {
        var root = KernelSession.Capture(s.Combat);
        var outcome = KernelRollout.Run(root, s.Party, s.Party,
            new RolloutOptions(Policy: RolloutPolicyKind.Kill, MaxRounds: 12));
        Console.WriteLine($"{tag}: terminal={outcome.Terminal} victory={outcome.Victory} stop={outcome.StopReason} "
            + $"rounds={outcome.RoundsAdvanced} enemyHp={outcome.Record.RemainingEnemyHp} "
            + $"postHp=[{string.Join(",", outcome.Record.PostCombatHp)}] "
            + $"dead={outcome.Record.IrreversibleLoss}");
    }

    private static Setup Build(int[] pitAmounts)
    {
        var party = new[]
        {
            Player.CreateForNewRun<Deprived>(UnlockState.all, 201343),
            Player.CreateForNewRun<Deprived>(UnlockState.all, 793217),
            Player.CreateForNewRun<Deprived>(UnlockState.all, 793474),
            Player.CreateForNewRun<Deprived>(UnlockState.all, 793731),
        };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "SANDPIT-FALSE-VICTORY"));
        var hp = new[] { 6, 3, 40, 8 };
        var maxHp = new[] { 80, 80, 70, 70 };
        var block = new[] { 0, 0, 0, 11 };
        for (var i = 0; i < party.Length; i++)
        {
            var p = party[i];
            p.ResetCombatState();
            combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(maxHp[i]);
            p.Creature.SetCurrentHpInternal(hp[i]);
            if (block[i] > 0) p.Creature.GainBlockInternal(block[i]);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
            p.PlayerCombatState.GainEnergy(3);
        }
        var enemy = combat.CreateCreature(ModelDb.Monster<TheInsatiable>().ToMutable(), CombatSide.Enemy, "insatiable");
        combat.AddCreature(enemy);
        enemy.Monster!.SetUpForCombat();
        enemy.SetMaxHpInternal(667);
        enemy.SetCurrentHpInternal(667);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["THRASH_MOVE"], true);
        ModelDb.Power<PoisonPower>().ToMutable().ApplyInternal(enemy, 47, true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(enemy, 3, true);
        ModelDb.Power<WeakPower>().ToMutable().ApplyInternal(enemy, 2, true);
        for (var i = 0; i < party.Length; i++)
        {
            var pit = (SandpitPower)ModelDb.Power<SandpitPower>().ToMutable();
            pit.Target = party[i].Creature;
            pit.ApplyInternal(enemy, pitAmounts[i], true);
        }
        // The exact round-6 hands are not needed for the assignment question; give each
        // seat one escape and one cheap card so the rollout can act at all.
        void Hand<T>(Player p) where T : CardModel
        {
            var card = combat.CreateCard<T>(p);
            p.PlayerCombatState!.Hand.AddInternal(card);
        }
        foreach (var p in party)
        {
            Hand<FranticEscape>(p);
            Hand<StrikeIronclad>(p);
            Hand<DefendIronclad>(p);
        }
        return new(combat, party, enemy);
    }
}

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

internal static class TeamOrderingScenarios
{
    // R2 2026-09-27: 13 deliberate production regressions, restored after
    // execution. Raw commands/results: work/strength-20260927-v13/r2*.json.
    // Kill must let seat 3 apply Vulnerable before the front seats attack.
    // Kill must coordinate again after each round boundary.
    // Vulnerable payoff must share one energy across three Strikes.
    // Vulnerable payoff must not invent actions for undelegated humans.
    // Artifact must prevent an invented Vulnerable payoff.
    // Existing Vulnerable must not give a duplicate setup bonus.
    // Vulnerable payoff must cap overkill at one additional HP.
    // Shielded attacks must not invent additional HP damage.
    // Tactical must let seat 3 apply Vulnerable before the front seats attack.
    // TopK must retain the low-damage late-seat team setup.
    // Global ordering must never play a human's cards.
    // Tactical probes must retain another owner's identical Strike when the stronger attacker would die to thorns.
    // Kill must take immediate victory before setup.
    // The first owner-dedup fixture stayed green: cheap ranking already put
    // the stronger owner first. The replacement thorns fixture forces ranking
    // and resolved survival to disagree and catches the lost candidate.
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static readonly List<string> Failures = [];
    private static void Check(bool ok, string message) { if (!ok) Failures.Add(message); }

    internal static void Run()
    {
        Failures.Clear();
        new Harmony("coopbots.test.team-order.presentation").Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(TeamOrderingScenarios), nameof(Format)));
        foreach (var lateSeat in new[] { 2, 3 })
        foreach (var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var (party, combat, enemy) = Board();
            Hand<StrikeSilent>(party[0], combat); Hand<StrikeSilent>(party[1], combat);
            Hand<Bash>(party[lateSeat], combat);
            var root = KernelSession.Capture(combat);
            var line = KernelRollout.Run(root.Fork(), party, party, new RolloutOptions(policy, MaxSteps: 3));
            Check(line.Actions[0].Card is Bash && line.Actions[0].Player == party[lateSeat],
                $"{policy} must let seat {lateSeat + 1} apply Vulnerable before the front seats attack.");
            var replay = root.Fork();
            foreach (var action in line.Actions)
            {
                if (action.Card is StrikeSilent)
                    Check(replay.Power<VulnerablePower>(enemy) > 0, "Front-seat attacks must actually receive Vulnerable in the replay.");
                Check(replay.ReplayPlanned(action, 10, out var why), "Team action must replay: " + why);
            }
            Check(replay.Hp(enemy) == 474, "Two front-seat Strikes must deal eighteen after eight-damage Bash.");
        }
        Console.WriteLine("PASS: every rollout policy coordinates third/fourth-seat Bash and both front-seat attacks.");

        foreach (var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var (party, combat, enemy) = Board();
            for (var i = 0; i < 15; i++)
            {
                party[0].PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<StrikeSilent>(party[0]));
                party[3].PlayerCombatState!.DrawPile.AddInternal(combat.CreateCard<Thunderclap>(party[3]));
            }
            Hand<StrikeSilent>(party[0], combat); Hand<Thunderclap>(party[3], combat);
            var root = KernelSession.Capture(combat);
            var line = KernelRollout.Run(root.Fork(), party, party, new RolloutOptions(policy, MaxRounds: 3));
            var replay = root.Fork(); var rounds = new HashSet<int>();
            foreach (var action in line.Actions)
            {
                if (action.Card is StrikeSilent)
                {
                    Check(replay.Power<VulnerablePower>(enemy) > 0,
                        $"{policy} must coordinate again after each round boundary.");
                    rounds.Add(replay.RoundsAdvanced);
                }
                Check(replay.ReplayPlanned(action, 10, out var why), "Cross-round team action must replay: " + why);
            }
            Check(rounds.Count >= 3, "Ordering fixture must exercise three distinct rounds.");
        }
        Console.WriteLine("PASS: team ordering is preserved through three simulated and replayed rounds.");

        var (team, board, foe) = Board();
        for (var i = 0; i < 3; i++) Hand<StrikeSilent>(team[0], board);
        var bash = Hand<Bash>(team[3], board);
        team[0].PlayerCombatState!.LoseEnergy(2);
        var state = KernelSession.Capture(board);
        Check(KernelTeamCoordination.SetupPayoff(state, team, bash, foe) == 3,
            "Vulnerable payoff must share one energy across three Strikes.");
        Check(KernelTeamCoordination.SetupPayoff(state, [team[3]], bash, foe) == 0,
            "Vulnerable payoff must not invent actions for undelegated humans.");
        team[0].PlayerCombatState!.LoseEnergy(1);
        Check(KernelTeamCoordination.SetupPayoff(KernelSession.Capture(board), team, bash, foe) == 0,
            "Vulnerable payoff must be zero when allied attacks are unaffordable.");
        team[0].PlayerCombatState!.GainEnergy(3);
        ModelDb.Power<ArtifactPower>().ToMutable().ApplyInternal(foe, 1, true);
        Check(KernelTeamCoordination.SetupPayoff(KernelSession.Capture(board), team, bash, foe) == 0,
            "Artifact must prevent an invented Vulnerable payoff.");
        Console.WriteLine("PASS: setup payoff respects shared energy, controlled actors and Artifact.");

        var (dup, dupBoard, dupFoe) = Board();
        Hand<StrikeSilent>(dup[0], dupBoard); var duplicate = Hand<Bash>(dup[3], dupBoard);
        ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(dupFoe, 2, true);
        Check(KernelTeamCoordination.SetupPayoff(KernelSession.Capture(dupBoard), dup, duplicate, dupFoe) == 0,
            "Existing Vulnerable must not give a duplicate setup bonus.");
        var (cap, capBoard, capFoe) = Board(15);
        Hand<StrikeSilent>(cap[0], capBoard); var capBash = Hand<Bash>(cap[3], capBoard);
        Check(KernelTeamCoordination.SetupPayoff(KernelSession.Capture(capBoard), cap, capBash, capFoe) == 1,
            "Vulnerable payoff must cap overkill at one additional HP.");
        capFoe.GainBlockInternal(100);
        Check(KernelTeamCoordination.SetupPayoff(KernelSession.Capture(capBoard), cap, capBash, capFoe) == 0,
            "Shielded attacks must not invent additional HP damage.");
        Console.WriteLine("PASS: redundant Vulnerable, overkill and block cannot inflate setup value.");

        var (opening, openingBoard, _) = Board();
        Hand<StrikeSilent>(opening[0], openingBoard); Hand<StrikeSilent>(opening[1], openingBoard);
        Hand<Thunderclap>(opening[3], openingBoard);
        var openingRoot = KernelSession.Capture(openingBoard);
        Check(KernelTournament.FirstActions(openingRoot, opening, 1,
            new TournamentOptions(IncludePotions: false))[0].Card is Thunderclap,
            "TopK must retain the low-damage late-seat team setup.");
        var humanLine = KernelRollout.Run(openingRoot.Fork(), opening, [opening[0]], new RolloutOptions(MaxSteps: 5));
        Check(humanLine.Actions.All(a => a.Player == opening[0]), "Global ordering must never play a human's cards.");
        Console.WriteLine("PASS: opening pruning retains team setup and mixed tables only drive delegated seats.");

        foreach (var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var (kill, killBoard, _) = Board(6);
            Hand<StrikeSilent>(kill[0], killBoard); Hand<Thunderclap>(kill[3], killBoard);
            var line = KernelRollout.Run(KernelSession.Capture(killBoard), kill, kill, new RolloutOptions(policy, MaxSteps: 1));
            Check(line.Actions[0].Card is StrikeSilent, $"{policy} must take immediate victory before setup.");
        }
        Console.WriteLine("PASS: immediate victory still outranks late-seat setup in all policies.");

        foreach (var policy in Enum.GetValues<RolloutPolicyKind>())
        {
            var (same, sameBoard, _) = Board();
            Hand<StrikeSilent>(same[0], sameBoard); Hand<StrikeSilent>(same[3], sameBoard);
            ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(same[3].Creature, 6, true);
            var line = KernelRollout.Run(KernelSession.Capture(sameBoard), same, same, new RolloutOptions(policy, MaxSteps: 1));
            Check(line.Actions[0].Player == same[3], $"{policy} must compare identical card states from different owners.");
        }
        Console.WriteLine("PASS: team candidates retain identical cards from owners with different damage.");
        var (risk, riskBoard, thorn) = Board();
        Hand<StrikeSilent>(risk[0], riskBoard); Hand<StrikeSilent>(risk[3], riskBoard);
        risk[0].Creature.SetCurrentHpInternal(1);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(risk[0].Creature, 1, true);
        ModelDb.Power<ThornsPower>().ToMutable().ApplyInternal(thorn, 2, true);
        var riskLine = KernelRollout.Run(KernelSession.Capture(riskBoard), risk, risk,
            new RolloutOptions(RolloutPolicyKind.Tactical, MaxSteps: 1));
        Check(riskLine.Actions[0].Player == risk[3],
            "Tactical probes must retain another owner's identical Strike when the stronger attacker would die to thorns.");
        if (Failures.Count > 0) throw new Exception(string.Join("\n", Failures.Distinct()));
    }

    private static T Hand<T>(Player owner, CombatState combat) where T : CardModel
    { var card = combat.CreateCard<T>(owner); owner.PlayerCombatState!.Hand.AddInternal(card); return card; }

    private static (Player[], CombatState, Creature) Board(int hp = 500)
    {
        var party = Enumerable.Range(0, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 25, i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "TEAM-ORDER"));
        foreach (var p in party)
        {
            p.ResetCombatState(); combat.AddPlayer(p); p.MaxEnergy = 3;
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        }
        var foe = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(foe); foe.Monster!.SetUpForCombat(); foe.SetMaxHpInternal(hp); foe.SetCurrentHpInternal(hp);
        foe.Monster.SetMoveImmediate((MoveState)foe.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (party, combat, foe);
    }
}

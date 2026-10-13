using System.Reflection;
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

internal static class PostDecisionSearchScenarios
{
    // R2 2026-10-07: four actual KernelTournament source faults exited 1,
    // exact source restoration exited 0. Isolated source SHA equals production;
    // commands and full logs: work/incombat-repair-20261007/search/r2-results.json.
    // System.Exception: Post-decision search: Defend and Growth must complete real native endings before beam expansion
    // System.Exception: Post-decision search: an explicit single-policy tournament must not add a defensive portfolio
    // System.Exception: Post-decision search: an unfunded defensive comparison must not publish a fabricated completed result
    // System.Exception: Post-decision search: defensive reference ending and per-seat HP must match its replay
    // R2 2026-10-08: three isolated KernelJointTurnRun source faults each
    // compiled successfully, exited RED1 at the named behavior, were restored
    // byte-for-byte, compiled successfully again and exited GREEN0.
    // Commands, exact source hashes and full raw logs:
    // work/incombat-repair-20261007/joint-before-beam-r2/r2-results.json.
    // System.Exception: Post-decision search: joint Tactical must start its real native reference replay before beam expansion
    // System.Exception: Post-decision search: early joint Tactical must start from a fresh exact native opening without consuming the parent
    // System.Exception: Post-decision search: early joint Tactical reference tokens must replay natively: unplayable
    internal static void Run(string? caseName = null)
    {
        if (caseName is not (null or "coverage" or "single" or "cutoff" or "joint-order"))
            throw new ArgumentException("Unknown post-decision search case: " + caseName);
        TestEnvironment.Ensure();
        var h = new Harmony("coopbots.post-decision-search");
        h.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(PostDecisionSearchScenarios), nameof(Format)));
        try
        {
            if (caseName is null or "coverage") Coverage();
            if (caseName is null or "single") ExplicitPolicy();
            if (caseName is null or "cutoff") Cutoff();
            if (caseName is null or "joint-order") JointBeforeBeam();
        }
        finally { h.UnpatchAll(h.Id); }
    }

    private static void Coverage()
    {
        var (party, combat) = Board(4, 60);
        foreach (var p in party)
        {
            Hand<StrikeSilent>(p, combat); Hand<StrikeSilent>(p, combat);
            Hand<DefendSilent>(p, combat); Hand<DefendSilent>(p, combat);
            for (var i = 0; i < 20; i++)
                p.PlayerCombatState!.DrawPile.AddInternal(i % 2 == 0
                    ? combat.CreateCard<StrikeSilent>(p) : combat.CreateCard<DefendSilent>(p));
        }
        var root = KernelSession.Capture(combat); var text = root.StateText(party[0]);
        var run = new TournamentRun(root, party, party, new(IncludePotions: false,
            ReserveMs: 0, EnableJointSearch: true, Rollout: new(MaxSteps: 160, MaxRounds: 8)
                { InvestmentLookahead = false }), 6000);
        // Stop at the actual phase boundary, before the beam is created.
        while (!Field<bool>(run, "policyReferencesFinished") && !run.Done)
            run.Advance(TimeSpan.FromMilliseconds(2));
        var references = Field<List<TournamentResult>>(run, "policyReferenceResults");
        Console.WriteLine("PROBE root-policy " + string.Join(",", Field<List<string>>(run, "policyReferenceStops"))
            + " compute=" + run.ComputeMs);
        Check(references.Count == 2 && references.All(r => r.Record is { Verified: true }),
            "Defend and Growth must complete real native endings before beam expansion");
        Check(AccessTools.Field(typeof(TournamentRun), "joint").GetValue(run) is null,
            "native defensive comparisons must precede the beam rather than follow its deadline");
        foreach (var reference in references)
        {
            var replay = root.Fork();
            Check(reference.Script!.Count == reference.ScriptAliveStates!.Count
                && reference.Script.Count == reference.ScriptEnemyStates!.Count,
                "root policy references must preserve action-aligned evidence");
            for (var i = 0; i < reference.Script.Count; i++)
            {
                Check(reference.ScriptAliveStates![i].SequenceEqual(party.Where(p => replay.Hp(p.Creature) > 0)
                        .Select(p => p.NetId).OrderBy(n => n))
                    && reference.ScriptEnemyStates![i].Select(e => e.CombatId)
                        .SequenceEqual(replay.Enemies.Select(e => e.CombatId)),
                    "policy reference evidence must describe the actual state before every action");
                Check(replay.ReplayPlanned(reference.Script[i], 80, out var why),
                    "policy reference must replay from the original root: " + why);
            }
            Check(replay.HasTerminal && replay.HasWon == reference.Record!.Victory
                && party.Select(p => replay.Hp(p.Creature)).SequenceEqual(reference.Record.PostCombatHp),
                "defensive reference ending and per-seat HP must match its replay");
        }
        Check(root.StateText(party[0]) == text, "root policy comparisons must preserve their captured root");
        while (!run.Advance(TimeSpan.FromMilliseconds(2))) { }
        Check(run.Result!.JointDiagnostics.Contains("policyReferences=[Defend:")
            && run.Result.JointDiagnostics.Contains(",Growth:"),
            "production diagnostics must distinguish completed root policy comparisons");
        Check(run.ComputeMs < 6400, "policy coverage must stay inside the unchanged tournament budget plus one native step");
        Console.WriteLine("PASS: native four-seat defence and growth finish before beam expansion and replay from root.");
    }

    private static void ExplicitPolicy()
    {
        var (party, c) = Board(1, 6); Hand<StrikeSilent>(party[0], c);
        var run = new TournamentRun(KernelSession.Capture(c), party, party,
            new(IncludePotions: false, ReserveMs: 0, EnableJointSearch: true, UsePolicyPortfolio: false), 1200);
        while (!run.Advance(TimeSpan.FromMilliseconds(2))) { }
        Check(Field<List<string>>(run, "policyReferenceStops").Count == 0,
            "an explicit single-policy tournament must not add a defensive portfolio");
        Console.WriteLine("PASS: an explicit single-policy request keeps its existing comparisons.");
    }

    private static void Cutoff()
    {
        var (party, c) = Board(1, 600); Hand<StrikeSilent>(party[0], c); Hand<DefendSilent>(party[0], c);
        var run = new TournamentRun(KernelSession.Capture(c), party, party,
            new(IncludePotions: false, ReserveMs: 0, EnableJointSearch: true), 1200);
        // An already occupied budget is an actual deadline, not a fake ending.
        AccessTools.Field(typeof(TournamentRun), "policyReferenceDeadlineMs").SetValue(run, 0d);
        AccessTools.Method(typeof(TournamentRun), "AdvancePolicyReferences")
            .Invoke(run, [TimeSpan.FromMilliseconds(2)]);
        Check(Field<bool>(run, "policyReferencesFinished")
            && Field<List<TournamentResult>>(run, "policyReferenceResults").Count == 0,
            "an unfunded defensive comparison must not publish a fabricated completed result");
        Console.WriteLine("PASS: unfunded defensive comparisons retain a named cutoff and publish no terminal.");
    }

    private static void JointBeforeBeam()
    {
        var (party, combat) = Board(1, 200);
        var player = party[0];
        Hand<Bash>(player, combat); Hand<StrikeIronclad>(player, combat);
        Hand<DefendIronclad>(player, combat); Hand<Survivor>(player, combat);
        var root = KernelSession.Capture(combat); var before = root.StateText(player);
        var run = new KernelJointTurnRun(root, party, party,
            new(IncludePotions: false, Rollout: new(MaxSteps: 80, MaxRounds: 2)), 5000);
        // A zero slice executes one atomic state-machine step. Stop exactly
        // after the real Tactical constructor, before any Tactical action.
        for (var i = 0; i < 10000 && !run.Done
            && !Field<Dictionary<RolloutPolicyKind, int>>(run, "startedPolicies")
                .ContainsKey(RolloutPolicyKind.Tactical); i++) run.Advance(TimeSpan.Zero);
        var started = Field<Dictionary<RolloutPolicyKind, int>>(run, "startedPolicies");
        Check(started.ContainsKey(RolloutPolicyKind.Tactical)
            && !Field<bool>(run, "searchReady") && Field<object?>(run, "search") is null,
            "joint Tactical must start its real native reference replay before beam expansion");
        var path = Field<IReadOnlyList<KernelTeamSearch.Action>>(run, "referencePath");
        var continuation = Field<RolloutRun>(run, "continuation");
        var replay = root.ForkForReplay();
        foreach (var action in path)
            Check(replay.ReplayPlanned(action, 80, out var why),
                "early joint Tactical reference tokens must replay natively: " + why);
        Check(path.Count == 1 && Field<int>(run, "jobIndex") == 1
            && continuation.State.StateText(player) == replay.StateText(player)
            && root.StateText(player) == before,
            "early joint Tactical must start from a fresh exact native opening without consuming the parent");
        Console.WriteLine("PASS: joint Tactical replays the real native opening before beam expansion.");
    }

    private static (Player[], CombatState) Board(int count, int enemyHp)
    {
        var party = Enumerable.Range(0, count).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
            (ulong)(22300 + i))).ToArray();
        var c = new CombatState(runState: RunState.CreateForTest(party, seed: "POST-DECISION-DEFENCE")) { RoundNumber = 1 };
        foreach (var p in party)
        {
            p.ResetCombatState(); c.AddPlayer(p); p.MaxEnergy = 3;
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(18);
        }
        var e = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(enemyHp); e.SetCurrentHpInternal(enemyHp);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"], true);
        ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(e, 8, true);
        return (party, c);
    }
    private static void Hand<T>(Player p, CombatState c) where T : CardModel
        => p.PlayerCombatState!.Hand.AddInternal(c.CreateCard<T>(p));
    private static T Field<T>(object o, string name) => (T)AccessTools.Field(o.GetType(), name).GetValue(o)!;
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool ok, string why) { if (!ok) throw new Exception("Post-decision search: " + why); }
}

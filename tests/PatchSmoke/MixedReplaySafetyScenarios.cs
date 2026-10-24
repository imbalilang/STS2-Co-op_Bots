using System.Diagnostics;
using System.Reflection;
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
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Production Poll, real native suffix replay, then the actual short tournament.
// A newly proved fatal old line is evidence for comparison, not an immediate
// action when another current-root native opening preserves the living seats.
// R2 actual 2026-10-24: production mutant build0 / RED1 / exact restore / GREEN0.
// Receipt: work/mixed-fixes-20261023T164339Z/r2-valid-proofs.json
// mixed-fatal-replay: System.InvalidOperationException: Mixed replay safety: a newly proved fatal replay must fund a current-root comparison and emit the surviving kill before human EndTurn
internal static class MixedReplaySafetyScenarios
{
    internal static void Run()
    {
        TestEnvironment.Ensure();
        var previous = KernelCombatPlanner.TournamentOverride;
        var presentation = new Harmony("coopbots.mixed-replay-safety.presentation");
        presentation.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(MixedReplaySafetyScenarios), nameof(Format)));
        KernelCombatPlanner.TournamentOverride = null;
        try { KnownFatalReplayMustCompare(); }
        finally { KernelCombatPlanner.TournamentOverride = previous; presentation.UnpatchAll(presentation.Id); }
    }

    private static void KnownFatalReplayMustCompare()
    {
        using var b = new Board();
        var root = KernelCombatCapture.Capture(b.Combat);
        // Resolve and warm native candidate/side settlement outside the short
        // timed request. Every operation below owns a fork of the same root.
        _ = KernelTournament.Run(root, b.Combat.Players.ToArray(), [b.Bot],
            new TournamentOptions(ReserveMs: 0, IncludePotions: false, IncludeEndTurns: false,
                UsePolicyPortfolio: false, Rollout: new(MaxRounds: 1, MaxSteps: 32)), 5000);
        var wrong = new KernelTeamSearch.Action(b.Bot, b.Offering, null,
            CardStateKey: root.CardStateKey(b.Offering));
        var safe = new KernelTeamSearch.Action(b.Bot, b.Strike, b.Enemy,
            CardStateKey: root.CardStateKey(b.Strike));
        KernelTeamSearch.Action[] Script(KernelTeamSearch.Action first)
            => TurnStartFixtureCapture.Record(root, [first], maxRounds: 1);
        var old = new TerminalRecord(false, EvidenceKind.EstimatedCutoff, "old-root-untrusted-proposal", [21, 5], 0, 0);
        var proposal = new TournamentResult(wrong, old, [], 0, 0, "mixed-proposal") { Script = Script(wrong) };
        TournamentResult Replay(TournamentResult candidate)
        {
            var replay = new KernelScriptReplayRun(root, b.Combat.Players.ToArray(), candidate, 1);
            while (!replay.Done) replay.Advance(TimeSpan.FromSeconds(1));
            return replay.Result ?? throw new InvalidOperationException("Mixed replay safety: native fixture replay failed: " + replay.Failure);
        }
        var fatal = Replay(proposal);
        var surviving = Replay(proposal with { FirstAction = safe, Script = Script(safe) });
        Check(fatal.Record is not null && fatal.Record.PostCombatHp[1] == 0 && b.Bot.Creature.CurrentHp == 5
            && surviving.Record is { Verified: true, Victory: true } && surviving.Record.PostCombatHp.All(hp => hp > 0),
            "the legal native Offering must really kill the living 5HP Bot while the alternative native Strike wins with both seats alive");
        Console.WriteLine("NATIVE mixed replay safety wrongHp=" + string.Join('/', fatal.Record!.PostCombatHp)
            + " safeHp=" + string.Join('/', surviving.Record!.PostCombatHp));

        var planner = new KernelCombatPlanner();
        try
        {
            AccessTools.Field(typeof(KernelCombatPlanner), "mixedProposal").SetValue(planner, proposal);
            TeamCombatPlanner.Decision? decision = null;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(8))
            {
                var status = planner.Poll(b.Combat, [b.Bot], 0, null, false, BotDifficulty.Pro, out decision);
                if (status == KernelCombatPlanner.Status.Ready && decision is not null) break;
                Check(status != KernelCombatPlanner.Status.Fallback,
                    "the supported actual root must retain the production tournament after replaying the fatal proposal");
            }
            var passes = (int)AccessTools.Field(typeof(KernelCombatPlanner), "tournamentPasses").GetValue(planner)!;
            Check(decision?.Player == b.Bot && decision.Move.Card == b.Strike && decision.Move.Target == b.Enemy && passes > 0
                && b.Human.PlayerCombatState!.Phase == PlayerTurnPhase.Play,
                "a newly proved fatal replay must fund a current-root comparison and emit the surviving kill before human EndTurn");
            Console.WriteLine("PASS: mixed fatal suffix replay yields to a current-root native comparison and the surviving opening.");
        }
        finally { planner.Reset(newCombat: true); }
    }

    private sealed class Board : IDisposable
    {
        internal readonly Player Human, Bot;
        internal readonly CombatState Combat;
        internal readonly Creature Enemy;
        internal readonly CardModel Strike, Offering;
        private readonly IDisposable native;
        internal Board()
        {
            Human = Player.CreateForNewRun<Deprived>(UnlockState.all, 14501);
            Bot = Player.CreateForNewRun<Deprived>(UnlockState.all,
                BotRegistry.CreateId(BotDifficulty.Pro, 1, 124));
            Combat = new CombatState(runState: RunState.CreateForTest([Human, Bot], seed: "MIXED-FATAL-REPLAY")) { RoundNumber = 1 };
            foreach (var p in new[] { Human, Bot })
            {
                p.MaxEnergy = 3; p.ResetCombatState(); Combat.AddPlayer(p);
                p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(p == Bot ? 5 : 21);
                p.PlayerCombatState!.Phase = PlayerTurnPhase.Play;
                p.PlayerCombatState.GainEnergy(p == Bot ? 1 : 0);
            }
            Strike = Combat.CreateCard<StrikeIronclad>(Bot); Bot.PlayerCombatState!.Hand.AddInternal(Strike);
            Offering = Combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.Offering>(Bot); Bot.PlayerCombatState.Hand.AddInternal(Offering);
            var monster = ModelDb.Monster<MockAttackMonster>().ToMutable();
            Enemy = Combat.CreateCreature(monster, CombatSide.Enemy, "fatal-replay-enemy");
            Combat.AddCreature(Enemy); monster.SetUpForCombat();
            Enemy.SetMaxHpInternal(80); Enemy.SetCurrentHpInternal(6);
            monster.SetMoveImmediate((MoveState)monster.MoveStateMachine!.States["ATTACK"], true);
            native = (IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios), "NativeFactsState"),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [Combat], null)!;
        }
        public void Dispose() => native.Dispose();
    }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool okay, string reason)
    { if (!okay) throw new InvalidOperationException("Mixed replay safety: " + reason); }
}

// R2 2026-10-08: work/szg-repair-20261008T124624/model/owned-card-reference-proposal/r2/proof.json.
// Ten isolated production mutations: forced Rebuild0 / named RED1 /
// exact source restoration / forced Rebuild0 / all six native cases GREEN0.
// Each actual exception below starts "System.InvalidOperationException: Owned card reference: ".
// joint must immediately retain its owner's real completed card ending
// tournament must pass its own completed Kill reference into joint work
// inherited ending must not be counted as a newly realized joint job
// joint must preserve the full original record, native choices and all action-aligned evidence
// external ReferenceResult must never become this tournament's owned Kill reference
// Tactical-only and potion-only joints must select their own native reference
// (Two separate mutations independently removed Tactical and potion-only protection.)
// a real native step-cap reference must not be published as a completed owned ending
// resumed original Kill must publish its actual completed native ending as the owned reference
// a different continuation actor domain must preserve joint's own original native opening
// The completed reference below comes from the actual owning TournamentRun.
// No handcrafted TerminalRecord is used as a positive reference.
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
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class OwnedCardReferenceScenarios
{
    private static readonly FieldInfo ownedField = AccessTools.Field(typeof(TournamentRun), "ownedCompletedCardReference");
    private static readonly FieldInfo jointField = AccessTools.Field(typeof(TournamentRun), "joint");
    private static readonly FieldInfo referenceReadyField = AccessTools.Field(typeof(KernelJointTurnRun), "referenceReady");
    private static readonly FieldInfo reusedField = AccessTools.Field(typeof(KernelJointTurnRun), "reusedOwnedReference");
    private static readonly MethodInfo openingMethod = AccessTools.Method(typeof(KernelJointTurnRun), "NativeReferenceOpening");
    private static bool observing;
    private static int openings;

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var patch = new Harmony("coopbots.private-owned-card-reference");
        var format = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText");
        patch.Patch(format, prefix: new HarmonyMethod(typeof(OwnedCardReferenceScenarios), nameof(Format)));
        patch.Patch(openingMethod, prefix: new HarmonyMethod(typeof(OwnedCardReferenceScenarios), nameof(ObserveOpening)));
        try
        {
            if (selected is null or "reuse") ReuseEnding();
            if (selected is null or "wiring") WiringAndOwnership();
            if (selected is null or "standalone") StandalonePolicyCoverage();
            if (selected is null or "cutoff") CutoffIsNotAnEnding();
            if (selected is null or "paused") PausedNativeKill();
            if (selected is null or "actor-domain") ActorDomainKeepsNativeOpening();
        }
        finally
        {
            observing = false; openings = 0;
            // Owner-qualified removal preserves any already-installed production
            // or earlier test patch using a coincident MethodInfo.
            patch.Unpatch(openingMethod, HarmonyPatchType.All, patch.Id);
            patch.Unpatch(format, HarmonyPatchType.All, patch.Id);
        }
    }

    private static void ReuseEnding()
    {
        var (root, owner) = Board();
        var options = Options();
        var before = root.PartyStateText();
        var (_, owned) = CompleteOwnedReference(root, owner, options);
        Replay(root, owner, owned);
        Check(owned.Script!.Any(a => a.Choices is { Count: > 0 }),
            "the real card reference must contain native menu evidence");
        var joint = new KernelJointTurnRun(root, [owner], [owner], options, 1,
            hasVerifiedReference: true, ownedCompletedCardReference: owned);
        Check((bool)referenceReadyField.GetValue(joint)! && (bool)reusedField.GetValue(joint)!,
            "joint must immediately retain its owner's real completed card ending");
        Check(joint.Prefixes == 0 && joint.Nodes == 0 && joint.Result!.Rollouts == 0
            && joint.Result.Considered.Count == 0
            && !joint.Diagnostics.Contains("policies=[Kill:")
            && !joint.Diagnostics.Contains("started=[Kill="),
            "inherited ending must not be counted as a newly realized joint job");
        AssertSameEvidence(joint.Result!, owned);
        // First prove the large native opening observation is alive on this
        // same board, including a direct reflective invocation not vulnerable
        // to an earlier inlined callsite.
        var standalone = new KernelJointTurnRun(root, [owner], [owner], options, 1);
        openings = 0; observing = true;
        try { Check(openingMethod.Invoke(standalone, null) is not null,
            "independent joint must resolve a real native opening"); }
        finally { observing = false; }
        Check(openings == 1, "native opening observation must see the actual original method");
        openings = 0; observing = true;
        try { Finish(joint); }
        finally { observing = false; }
        Check(openings == 0,
            "joint must not restart its own already-completed Kill reference");
        Check(joint.Result?.Record is { Verified: true },
            "one millisecond of new work must retain a real completed ending");
        Replay(root, owner, joint.Result!);
        Check(root.PartyStateText() == before,
            "inheriting and comparing a completed ending must preserve the physical root");
        Console.WriteLine($"PROBE owned ending tinyMs=1 newPrefixes={joint.Prefixes} diagnostics={joint.Diagnostics}");
        Console.WriteLine("PASS: a completed native menu-bearing reference survives a tiny joint budget without double counting or restarting Kill.");
    }

    private static void WiringAndOwnership()
    {
        var (root, owner) = Board();
        var options = Options();
        var (tournament, owned) = CompleteOwnedReference(root, owner, options);
        var outerRollouts = tournament.Rollouts;
        tournament.Advance(TimeSpan.FromTicks(1));
        var joint = (KernelJointTurnRun?)jointField.GetValue(tournament);
        Check(joint is not null && (bool)reusedField.GetValue(joint)!,
            "tournament must pass its own completed Kill reference into joint work");
        Check(tournament.Rollouts == outerRollouts,
            "passing the already-counted reference must not add an outer rollout");
        AssertSameEvidence(joint!.Result!, owned);

        // Even an externally supplied real result from the same board is not
        // an owned producer: the constructor must not seed this private slot.
        var external = new TournamentRun(root, [owner], [owner], options with { ReferenceResult = owned }, 1);
        Check(ownedField.GetValue(external) is null,
            "external ReferenceResult must never become this tournament's owned Kill reference");
        openings = 0; observing = true;
        try { external.Advance(TimeSpan.FromTicks(1)); }
        finally { observing = false; }
        var externalJoint = (KernelJointTurnRun?)jointField.GetValue(external);
        Check(externalJoint is not null && !(bool)reusedField.GetValue(externalJoint)!
            && openings > 0,
            "an external best record must keep the original joint native reference behavior");
        Console.WriteLine("PASS: only the owning tournament's real Kill outcome is forwarded; external best records preserve the native reference.");
    }

    private static void StandalonePolicyCoverage()
    {
        var (root, owner) = Board();
        var options = Options();
        var (_, owned) = CompleteOwnedReference(root, owner, options);
        foreach (var control in new[]
                 {
                     options with { DiverseJointContinuations = false },
                     options with { PotionsOnly = true },
                 })
        {
            var joint = new KernelJointTurnRun(root, [owner], [owner], control, 1,
                hasVerifiedReference: true, ownedCompletedCardReference: owned);
            Check(!(bool)referenceReadyField.GetValue(joint)! && !(bool)reusedField.GetValue(joint)!,
                "Tactical-only and potion-only joints must select their own native reference");
            openings = 0; observing = true;
            try { joint.Advance(TimeSpan.FromTicks(1)); }
            finally { observing = false; }
            Check(openings > 0,
                "standalone policy constraints must still execute the original native opening method");
        }
        Check(KernelJointTurnRun.Schedule(1, true).Take(4).Select(job => job.Policy)
            .SequenceEqual(new[] { RolloutPolicyKind.Kill, RolloutPolicyKind.Tactical,
                RolloutPolicyKind.Defend, RolloutPolicyKind.Growth }),
            "reusing Kill must leave all three original priority comparison policies scheduled");
        Console.WriteLine("PASS: Tactical-only, potion-only and no-owned joint searches keep the original native reference and priority coverage.");
    }

    private static void CutoffIsNotAnEnding()
    {
        var (root, owner) = Board();
        var options = Options() with { Rollout = new RolloutOptions(MaxSteps: 1) };
        var tournament = new TournamentRun(root, [owner], [owner], options, 10000);
        for (var i = 0; i < 12 && !(bool)AccessTools.Field(typeof(TournamentRun), "referenceFinished").GetValue(tournament)!; i++)
            tournament.Advance(TimeSpan.FromTicks(1));
        Check((bool)AccessTools.Field(typeof(TournamentRun), "referenceFinished").GetValue(tournament)!
            && tournament.BestCompleted?.Record is { Verified: false }
            && ownedField.GetValue(tournament) is null,
            "a real native step-cap reference must not be published as a completed owned ending");
        Console.WriteLine("PASS: actual native cutoffs do not become completed card references.");
    }

    private static void PausedNativeKill()
    {
        var (root, owner) = Board();
        var options = Options();
        var tournament = new TournamentRun(root, [owner], [owner], options, 10000);
        tournament.Advance(TimeSpan.FromTicks(1));
        var inFlightField = AccessTools.Field(typeof(TournamentRun), "referenceRun");
        var actual = (RolloutRun?)inFlightField.GetValue(tournament);
        Check(actual is not null && !actual.Done && ownedField.GetValue(tournament) is null,
            "pause control must start from the real owning Kill's incomplete native frame");
        // Move that actual in-flight state through the production paused lane.
        // This isolates queue resumption from wall-time-dependent share expiry;
        // neither Result, Terminal, Record nor any action evidence is fabricated.
        var queue = (Queue<(RolloutPolicyKind Policy, RolloutRun Run)>)
            AccessTools.Field(typeof(TournamentRun), "pausedPolicyReferences").GetValue(tournament)!;
        queue.Enqueue((RolloutPolicyKind.Kill, actual!));
        inFlightField.SetValue(tournament, null);
        AccessTools.Field(typeof(TournamentRun), "referenceFinished").SetValue(tournament, true);
        var resume = AccessTools.Method(typeof(TournamentRun), "AdvancePausedPolicyReferences");
        for (var i = 0; i < 600 && queue.Count > 0 && !tournament.Done; i++)
            resume.Invoke(tournament, [TimeSpan.FromTicks(1)]);
        var owned = (TournamentResult?)ownedField.GetValue(tournament);
        Check(actual!.Done && actual.Result is { Terminal: true, Record: { Verified: true } }
            && owned is not null && ReferenceEquals(owned.Record, actual.Result.Record),
            "resumed original Kill must publish its actual completed native ending as the owned reference");
        Replay(root, owner, owned!);
        var joint = new KernelJointTurnRun(root, [owner], [owner], options, 1,
            hasVerifiedReference: true, ownedCompletedCardReference: owned);
        Check((bool)reusedField.GetValue(joint)! && joint.Prefixes == 0,
            "paused Kill's native completion must be reusable without a new joint rollout");
        AssertSameEvidence(joint.Result!, owned!);
        Console.WriteLine("PASS: a real paused Kill frame resumes natively to an owned ending; no terminal evidence is fabricated.");
    }

    private static void ActorDomainKeepsNativeOpening()
    {
        var (root, owner) = Board(extraSeat: true);
        var extra = root.Party.Single(p => p != owner);
        var options = Options() with { ContinuationActors = new[] { owner, extra } };
        var (tournament, owned) = CompleteOwnedReference(root, owner, options);
        Check(owned.Script![0].Player == owner,
            "actor-domain control must have a legal shared first actor despite the larger Kill policy domain");
        openings = 0; observing = true;
        try { tournament.Advance(TimeSpan.FromTicks(1)); }
        finally { observing = false; }
        var joint = (KernelJointTurnRun?)jointField.GetValue(tournament);
        Check(joint is not null && !(bool)reusedField.GetValue(joint)! && openings > 0,
            "a different continuation actor domain must preserve joint's own original native opening");
        Console.WriteLine("PASS: a larger completed-Kill actor domain cannot replace the original narrower joint reference.");
    }

    private static (TournamentRun, TournamentResult) CompleteOwnedReference(KernelSession root, Player owner,
        TournamentOptions options)
    {
        var tournament = new TournamentRun(root, root.Party, [owner], options, 10000);
        for (var i = 0; i < 600 && ownedField.GetValue(tournament) is null && !tournament.Done; i++)
            tournament.Advance(TimeSpan.FromTicks(1));
        var owned = (TournamentResult?)ownedField.GetValue(tournament);
        Check(owned is { Record: { Verified: true }, Script: { Count: > 0 } },
            "owning tournament must produce a real completed Kill outcome");
        Check(tournament.Rollouts == 1 && tournament.EvaluatedOpenings == 1,
            "the original completed Kill producer must count its one actual rollout and opening once");
        return (tournament, owned!);
    }

    private static void AssertSameEvidence(TournamentResult actual, TournamentResult expected)
    {
        Check(ReferenceEquals(actual.Record, expected.Record)
            && ReferenceEquals(actual.FirstAction, expected.FirstAction)
            && ReferenceEquals(actual.Script, expected.Script)
            && ReferenceEquals(actual.ScriptAliveStates, expected.ScriptAliveStates)
            && ReferenceEquals(actual.ScriptEnemyStates, expected.ScriptEnemyStates),
            "joint must preserve the full original record, native choices and all action-aligned evidence");
    }

    private static void Replay(KernelSession root, Player owner, TournamentResult result)
    {
        Check(result.Script!.Count == result.ScriptAliveStates!.Count
            && result.Script.Count == result.ScriptEnemyStates!.Count,
            "completed reference evidence must remain index aligned");
        var replay = root.Fork();
        replay.AutoResolveTurnStartChoices = true; replay.AutoResolveEnemyChoices = true;
        for (var i = 0; i < result.Script.Count; i++)
        {
            var alive = root.Party.Where(p => replay.Hp(p.Creature) > 0).Select(p => p.NetId).OrderBy(n => n);
            Check(result.ScriptAliveStates[i].SequenceEqual(alive)
                && result.ScriptEnemyStates![i].SequenceEqual(replay.Enemies),
                "completed reference must carry the real before-action party and enemy state");
            Check(replay.ReplayPlanned(result.Script[i], 80, out var reason),
                "completed reference script must replay through native choices: " + reason);
        }
        var record = result.Record!;
        Check(replay.HasTerminal && replay.HasWon == record.Victory
            && record.PostCombatHp.SequenceEqual(root.Party.Select(p => replay.Hp(p.Creature)))
            && record.RemainingEnemyHp == replay.RemainingOpposition().Hp,
            "replayed native ending must match the inherited record");
    }

    private static TournamentOptions Options() => new(IncludePotions: false, UsePolicyPortfolio: false,
        ReserveMs: 0, EnableJointSearch: true);

    private static (KernelSession, Player) Board(bool extraSeat = false)
    {
        var owner = Player.CreateForNewRun<Deprived>(UnlockState.all, 59137);
        Player[] party = extraSeat
            ? [owner, Player.CreateForNewRun<Deprived>(UnlockState.all, 59138)] : [owner];
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "OWNED-JOINT-REFERENCE"));
        owner.ResetCombatState(); combat.AddPlayer(owner);
        owner.Creature.SetMaxHpInternal(80); owner.Creature.SetCurrentHpInternal(1);
        owner.PlayerCombatState!.Phase = PlayerTurnPhase.Play; owner.PlayerCombatState.GainEnergy(3);
        foreach (var extra in party.Skip(1))
        {
            extra.ResetCombatState(); combat.AddPlayer(extra);
            extra.Creature.SetMaxHpInternal(80); extra.Creature.SetCurrentHpInternal(80);
            extra.PlayerCombatState!.Phase = PlayerTurnPhase.Play; extra.PlayerCombatState.GainEnergy(3);
        }
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DaggerThrow>(owner));
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
        owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendIronclad>(owner));
        var enemy = combat.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "enemy");
        combat.AddCreature(enemy); enemy.Monster!.SetUpForCombat(); enemy.SetMaxHpInternal(18); enemy.SetCurrentHpInternal(18);
        enemy.Monster.SetMoveImmediate((MoveState)enemy.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (KernelSession.Capture(combat), owner);
    }

    private static void Finish(KernelJointTurnRun joint)
    {
        for (var i = 0; i < 1000 && !joint.Done; i++) joint.Advance(TimeSpan.FromTicks(1));
        Check(joint.Done, "tiny joint must respect its existing finite compute budget");
    }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void ObserveOpening() { if (observing) openings++; }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Owned card reference: " + message); }
}

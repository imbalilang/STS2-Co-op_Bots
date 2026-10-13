// R2 physical reproof: work/architecture-refactor-20261009/r2/model-refinement-prior-guards-proof-v2/proof.json
// p0-refinement-no-survivor: baseline 0 -> compiled mutant 1 -> exact restoration GREEN 0.
// Actual RED: System.Exception: P0-SIM: a victory with no surviving seat must not be adopted
// p0-refinement-targeted-seats: baseline 0 -> compiled mutant 1 -> exact restoration GREEN 0.
// Actual RED: System.Exception: P0-SIM: a victory whose boundary targeted powers differ from live must not be adopted
// p0-deployment-card-state: baseline 0 -> compiled mutant 1 -> exact restoration GREEN 0.
// Actual RED: System.Exception: P0-SIM: a card whose state changed since verification must not be played
// p0-deployment-acting-seat: baseline 0 -> compiled mutant 1 -> exact restoration GREEN 0.
// Actual RED: System.Exception: P0-SIM: the planned step must resolve to the acting seat's own card
using System.Diagnostics;
using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

/// <summary>
/// P0-SIM regression suite (2026-10-05 false VICTORY, seed RFTC0WGM4A3P).
///
/// P0-SIM-1: targeted powers must be compared by (owner,target) identity, not by the
/// position of an ordered amount list. Live renders `Creatures.SelectMany(c => c.Powers)`
/// while a branch renders `EffectivePowers()` (hook-listener order), so swapping which
/// seat holds a SandpitPower countdown could render identical text and pass the refinement
/// boundary check -- exactly the per-seat state that decides who dies to the countdown.
///
/// P0-SIM-2: a validated VICTORY may only be adopted when its replay leaves a surviving
/// seat and the boundary's targeted powers match live; deployment re-asserts per step that
/// the resolved live card is still in the state key the plan was verified against.
/// </summary>
internal static class KernelConsistencyScenarios
{
    internal static void Run()
    {
        TestEnvironment.Ensure();
        // A focused console invocation also warms all canonical powers. Only
        // localization presentation is stubbed; native effects/guards stay real.
        var presentation = new Harmony("coopbots.p0-consistency-presentation");
        var format = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText");
        presentation.Patch(format, prefix: new HarmonyMethod(typeof(KernelConsistencyScenarios), nameof(Format)));
        try
        {
            TargetIdentity();
            RefinementVictoryGuard();
            DeploymentIdentity();
        }
        finally { presentation.Unpatch(format, HarmonyPatchType.All, presentation.Id); }
    }

    private static bool Format(ref string __result) { __result = ""; return false; }

    // ------------------------------------------------------------------ P0-SIM-1

    private static void TargetIdentity()
    {
        void Check(bool value, string reason) { if (!value) throw new Exception("P0-SIM: " + reason); }

        // A: seat0=1, seat1=2, seat2=1 (creation order = seat order) -> P="1,2,1".
        var a = Board([(0, 1), (1, 2), (2, 1)]);
        // B: seat0=1, seat2=2, seat1=1 (creation order puts the 2 on seat2) -> P="1,2,1" too.
        var b = Board([(0, 1), (2, 2), (1, 1)]);
        var branchA = KernelSession.Capture(a.Combat);
        var branchB = KernelSession.Capture(b.Combat);
        Check(branchA.SandpitTurnsRemaining(a.Party[1].Creature) == 2
            && branchA.SandpitTurnsRemaining(a.Party[2].Creature) == 1,
            "board A must hold the 2 on seat 1");
        Check(branchB.SandpitTurnsRemaining(b.Party[2].Creature) == 2
            && branchB.SandpitTurnsRemaining(b.Party[1].Creature) == 1,
            "board B must hold the 2 on seat 2");

        var textA = KernelSession.CaptureLivePartyText(a.Combat);
        var textB = KernelSession.CaptureLivePartyText(b.Combat);
        Check(!string.Equals(textA, textB, StringComparison.Ordinal),
            "targeted powers must be compared by (owner,target) identity, not by position");
        Check(KernelSession.FirstLiveDifference(b.Combat, branchA.PartyStateText()) is not null,
            "a branch captured from A must not validate against live board B");

        // Same per-seat targets as A, different creation order: the identity section must
        // be order independent, so a legitimate board never fails just because the power
        // list order changed.
        var c = Board([(1, 2), (2, 1), (0, 1)]);
        Check(PT(KernelSession.CaptureLivePartyText(c.Combat))
                == PT(KernelSession.CaptureLivePartyText(a.Combat)),
            "PT identity section must not depend on enumeration order");

        Console.WriteLine("PASS: P0-SIM-1 targeted powers compare by (owner,target) identity, not position.");
    }

    // ------------------------------------------------------------------ P0-SIM-2

    private static string? boundaryText;
    private static bool LiveText(ref string __result) { __result = boundaryText!; return false; }
    private static KernelSession? boundaryCapture;
    private static bool CaptureBoundary(ref KernelSession __result)
    {
        if (boundaryCapture is null) return true;
        __result = boundaryCapture.Fork(); return false;
    }
    private static readonly List<string> messages = [];
    private static void CaptureLog(LogLevel level, string text, int skipFrames) => messages.Add(text);

    private static void RefinementVictoryGuard()
    {
        void Check(bool value, string reason) { if (!value) throw new Exception("P0-SIM: " + reason); }

        // A one-seat board with a real round crossing: the refinement replays the current
        // turn, freezes the next-round boundary, then a search could publish a suffix.
        var (c, p, _) = RefinementBoard();
        var root = KernelSession.Capture(c);
        var defend = p.PlayerCombatState!.Hand.Cards[0];
        var prefix = new KernelTeamSearch.Action[]
        {
            new(p, defend, null, CardStateKey: root.CardStateKey(defend)),
            new(p, null, null, EndTurn: true),
        };
        var old = TurnStartFixtureCapture.Record(root, prefix.Concat(Enumerable.Range(0, 10).Select(_ => new KernelTeamSearch.Action(p, null, null, EndTurn: true))));
        var job = new KernelScriptRefinementRun(root, [p], [p], old, new(ReserveMs: 0), 5000);
        var ticks = 0;
        while ((job.Baseline is null || job.BoundaryState is null) && ticks++ < 20000)
            job.Advance(TimeSpan.FromMilliseconds(1));
        job.FinishAtBoundary();
        Check(job.BoundaryState is not null && job.Baseline is not null && job.BoundaryIndex > 0,
            "fixture must cross a round boundary and replay an ending");

        var endTurn = new KernelTeamSearch.Action(p, null, null, EndTurn: true);
        TournamentResult Verdict(params int[] postHp)
        {
            var record = new TerminalRecord(true, EvidenceKind.VerifiedTerminal, "", postHp,
                postHp.Count(hp => hp <= 0), 0);
            return new TournamentResult(endTurn, record, [record], 1, 0, "fixture") { Script = [endTurn] };
        }
        void ForceResult(TournamentResult verdict)
            => typeof(KernelScriptRefinementRun).GetProperty(nameof(KernelScriptRefinementRun.Result))!.SetValue(job, verdict);

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Field(object planner, string name) => typeof(KernelCombatPlanner).GetField(name, flags)!.GetValue(planner);
        KernelCombatPlanner Planner()
        {
            var planner = new KernelCombatPlanner();
            // These deliberate verdict injections exercise the existing named
            // survivor/target guards, not native proof of victory. Give each
            // attempt fresh ownership so the new request check reaches them.
            var request = new KernelDecisionRequest(9201, 10000, 1);
            var verdict = job.Result! with { RootGeneration = request.RootGeneration };
            typeof(TournamentResult).GetProperty("RequestProvenance", flags)!.SetValue(verdict, request);
            ForceResult(verdict);
            typeof(KernelCombatPlanner).GetField("refinementRequest", flags)!.SetValue(planner, request);
            typeof(KernelCombatPlanner).GetField("refinement", flags)!.SetValue(planner, job);
            typeof(KernelCombatPlanner).GetField("refinementRound", flags)!.SetValue(planner, c.RoundNumber);
            typeof(KernelCombatPlanner).GetField("refinementScript", flags)!.SetValue(planner, old);
            typeof(KernelCombatPlanner).GetField("tournamentScript", flags)!.SetValue(planner, old);
            typeof(KernelCombatPlanner).GetField("tournamentScriptCombat", flags)!.SetValue(planner, c);
            typeof(KernelCombatPlanner).GetField("tournamentScriptRound", flags)!.SetValue(planner, c.RoundNumber);
            typeof(KernelCombatPlanner).GetField("tournamentScriptIndex", flags)!.SetValue(planner, job.BoundaryIndex);
            return planner;
        }
        void Adopt(KernelCombatPlanner planner) => AccessTools.Method(typeof(KernelCombatPlanner), "TryAdoptRefinement")!.Invoke(planner, [c]);

        c.RoundNumber = 2;
        boundaryText = job.BoundaryText;
        var harmony = new Harmony("coopbots.test.p0sim.victory");
        harmony.Patch(AccessTools.Method(typeof(KernelSession), nameof(KernelSession.CaptureLivePartyText)),
            prefix: new HarmonyMethod(typeof(KernelConsistencyScenarios), nameof(LiveText)));
        // Observe the real capture entry after other scenarios have warmed JIT.
        harmony.Patch(AccessTools.Method(typeof(KernelSession), nameof(KernelSession.Capture)),
            prefix: new HarmonyMethod(typeof(KernelConsistencyScenarios), nameof(CaptureBoundary)));
        boundaryCapture = job.BoundaryState;
        Log.LogCallback += CaptureLog;
        try
        {
            // 1) A "verified victory" whose replay leaves no survivor is impossible; the
            //    guard must refuse it by name instead of adopting the line.
            messages.Clear(); ForceResult(Verdict(0));
            var planner = Planner(); Adopt(planner);
            Check(ReferenceEquals(Field(planner, "tournamentScript"), old)
                && (int)Field(planner, "tournamentScriptIndex")! == job.BoundaryIndex,
                "a victory with no surviving seat must not be adopted");
            Check(messages.Any(m => m.Contains("victory refused: the validated replay leaves no surviving seat")),
                "the no-survivor refusal must name itself");

            // 2) Even when the caller's text matches, a boundary whose targeted powers do
            //    not match live must refuse the victory (second line of defence behind the
            //    P0-SIM-1 text identity section).
            var boundaryTargeted = job.BoundaryState!.TargetedPowerState();
            var pit = (SandpitPower)ModelDb.Power<SandpitPower>().ToMutable();
            pit.Target = p.Creature;
            pit.ApplyInternal(c.Enemies.First(), 2, true);
            Check(KernelSession.TargetedPowerState(c) != boundaryTargeted,
                "fixture must actually make the live targeted powers differ");
            messages.Clear(); ForceResult(Verdict(80));
            planner = Planner(); Adopt(planner);
            Check(ReferenceEquals(Field(planner, "tournamentScript"), old),
                "a victory whose boundary targeted powers differ from live must not be adopted");
            Check(messages.Any(m => m.Contains("victory refused: targeted power seats differ")),
                "the targeted-power refusal must name itself");

            // 3) With matching targeted powers and a predicted survivor the guard passes,
            //    adopts the suffix, and logs the per-seat picture the next triage needs.
            c.Enemies.First().Powers.OfType<SandpitPower>().ToArray()
                .ToList().ForEach(sandpit => sandpit.RemoveInternal());
            // As in the negative guard cases, this fixture supplies a capture
            // seam because it sets the live round manually. It tests the splice,
            // not full native/live round equivalence; separate contract cases
            // exercise re-rooting when the complete actual state differs.
            messages.Clear(); ForceResult(Verdict(80));
            planner = Planner(); Adopt(planner);
            Check(ReferenceEquals(Field(planner, "tournamentScript"), ((TournamentResult)typeof(KernelScriptRefinementRun)
                    .GetProperty(nameof(KernelScriptRefinementRun.Result))!.GetValue(job)!).Script),
                "a legitimate victory must be adopted");
            Check(messages.Any(m => m.Contains("CoopBots victory guard:")
                    && m.Contains("postHp=[80]") && m.Contains("sandpits-boundary=[")
                    && m.Contains("sandpits-live=[")),
                "adopting a victory must log postHp and both sandpit views");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            boundaryText = null;
            boundaryCapture = null;
            Log.LogCallback -= CaptureLog;
            messages.Clear();
        }
        Console.WriteLine("PASS: P0-SIM-2 victory adoption requires a surviving seat and matching targeted powers.");
    }

    private static void DeploymentIdentity()
    {
        void Check(bool value, string reason) { if (!value) throw new Exception("P0-SIM: " + reason); }

        // (a) The instance fallback must not play a card whose mutable state moved since
        //     verification (here: upgraded after the plan recorded its state key).
        var (c, p, e) = CombatBoard();
        var strike = c.CreateCard<StrikeIronclad>(p);
        p.PlayerCombatState!.Hand.AddInternal(strike);
        var key = KernelSession.Capture(c).CardStateKey(strike);
        strike.UpgradeInternal();
        Check(KernelSession.LiveCardStateKey(strike) != key, "fixture must change the live card's state key");
        var planner = new KernelCombatPlanner();
        InstallScript(planner, c, [p], [new KernelTeamSearch.Action(p, strike, e, CardStateKey: key)]);
        messages.Clear(); Log.LogCallback += CaptureLog;
        bool emitted;
        try
        {
            emitted = Emit(planner, c, [p], out _);
            Check(!emitted, "a card whose state changed since verification must not be played");
            Check(Get(planner, "tournamentScript") is null, "the drifted step must drop the cached script");
            Check(messages.Any(m => m.Contains("card-state-changed")),
                "the refusal must name the drifted card state");
        }
        finally { Log.LogCallback -= CaptureLog; messages.Clear(); }

        // (b) Identical cards on two seats must resolve against the planned seat, never the
        //     first hand that happens to hold an equal-looking copy.
        var (group, pA, pB, foe) = TwoSeatBoard();
        var cardA = group.CreateCard<StrikeSilent>(pA);
        var cardB = group.CreateCard<StrikeSilent>(pB);
        pA.PlayerCombatState!.Hand.AddInternal(cardA);
        pB.PlayerCombatState!.Hand.AddInternal(cardB);
        var root = KernelSession.Capture(group);
        Check(root.CardStateKey(cardA) == root.CardStateKey(cardB), "fixture needs identical state keys");
        var second = new KernelCombatPlanner();
        InstallScript(second, group, [pA, pB],
            [new KernelTeamSearch.Action(pB, cardB, foe, CardStateKey: root.CardStateKey(cardB))]);
        Check(Emit(second, group, [pA, pB], out var decision) && ReferenceEquals(decision!.Move.Card, cardB),
            "the planned step must resolve to the acting seat's own card");

        Console.WriteLine("PASS: P0-SIM-2 deployment refuses state-drifted cards and never crosses seats.");
    }

    // ------------------------------------------------------------------ helpers

    private static string PT(string text)
    {
        var start = text.IndexOf(";PT=", StringComparison.Ordinal);
        if (start < 0) return "<missing>";
        var end = text.IndexOf(";R=", start, StringComparison.Ordinal);
        return text[start..(end < 0 ? text.Length : end)];
    }

    private static (CombatState Combat, Player[] Party) Board((int Seat, int Amount)[] pits)
    {
        // SAME NetIds on both boards: the assertion has to be about the state, not labels.
        var party = Enumerable.Range(0, 3)
            .Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)(900 + i))).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "P0-SIM-TARGET"));
        foreach (var p in party)
        {
            p.ResetCombatState();
            combat.AddPlayer(p);
            p.Creature.SetMaxHpInternal(80);
            p.Creature.SetCurrentHpInternal(80);
        }
        Creature enemy = combat.CreateCreature(ModelDb.Monster<TheInsatiable>().ToMutable(), CombatSide.Enemy, "0");
        combat.AddCreature(enemy);
        enemy.Monster!.SetUpForCombat();
        foreach (var (seat, amount) in pits)
        {
            var pit = (SandpitPower)ModelDb.Power<SandpitPower>().ToMutable();
            pit.Target = party[seat].Creature;
            pit.ApplyInternal(enemy, amount, true);
        }
        return (combat, party);
    }

    private static (CombatState Combat, Player Player, Creature Enemy) RefinementBoard()
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 126);
        var c = new CombatState(runState: RunState.CreateForTest([p], seed: "P0-SIM-REFINEMENT"));
        p.ResetCombatState(); c.AddPlayer(p); c.RoundNumber = 1; p.MaxEnergy = 3;
        p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
        // The successful splice must use a supported native round. Mock ATTACK
        // is intentionally uncertain and cannot qualify full-state reuse.
        var e = c.CreateCreature(ModelDb.Monster<BowlbugEgg>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(24); e.SetCurrentHpInternal(24);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["BITE_MOVE"], true);
        var defend = c.CreateCard<DefendIronclad>(p);
        p.PlayerCombatState.Hand.AddInternal(defend);
        return (c, p, e);
    }

    private static (CombatState Combat, Player Player, Creature Enemy) CombatBoard()
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 127);
        var c = new CombatState(runState: RunState.CreateForTest([p], seed: "P0-SIM-DEPLOY"));
        p.ResetCombatState(); c.AddPlayer(p);
        p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
        p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
        var e = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(200); e.SetCurrentHpInternal(200);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (c, p, e);
    }

    private static (CombatState Combat, Player A, Player B, Creature Enemy) TwoSeatBoard()
    {
        var a = Player.CreateForNewRun<Deprived>(UnlockState.all, 128);
        var b = Player.CreateForNewRun<Deprived>(UnlockState.all, 129);
        var c = new CombatState(runState: RunState.CreateForTest([a, b], seed: "P0-SIM-CROSS-SEAT"));
        foreach (var p in new[] { a, b })
        {
            p.ResetCombatState(); c.AddPlayer(p);
            p.PlayerCombatState!.Phase = PlayerTurnPhase.Play; p.PlayerCombatState.GainEnergy(3);
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
        }
        var e = c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(e); e.Monster!.SetUpForCombat(); e.SetMaxHpInternal(200); e.SetCurrentHpInternal(200);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"], true);
        return (c, a, b, e);
    }

    private static void InstallScript(KernelCombatPlanner planner, CombatState c, Player[] party,
        KernelTeamSearch.Action[] script)
    {
        object? Set(string name, object? value)
        {
            AccessTools.Field(typeof(KernelCombatPlanner), name)!.SetValue(planner, value);
            return value;
        }
        Set("tournamentScript", script);
        Set("tournamentScriptCombat", c);
        Set("tournamentScriptRound", c.RoundNumber);
        var record = new TerminalRecord(false, EvidenceKind.EstimatedCutoff, "fixture",
            party.Select(_ => 80).ToArray(), 0, 0);
        Set("tournamentScriptRecord", record);
        Set("tournamentScriptAliveStates",
            script.Select(_ => party.Select(p => p.NetId).OrderBy(id => id).ToArray()).ToArray());
        Set("tournamentScriptEnemyStates", script.Select(_ => c.Enemies.ToArray()).ToArray());
        Set("tournamentScriptIndex", 0);
    }

    private static bool Emit(KernelCombatPlanner planner, CombatState c, Player[] actors,
        out TeamCombatPlanner.Decision? decision)
    {
        // These injected scripts isolate the bot-owned card identity guard.
        // Mixed-input qualification has its own native continuation fixtures;
        // treating these actors as humans would re-root before card resolution.
        var previous = actors.Select(p => (p.NetId, Driven: AutoPilot.IsAutopiloted(p.NetId))).ToArray();
        try
        {
            foreach (var p in actors) AutoPilot.Set(p.NetId, true);
            object?[] args = [c, actors, true, null];
            var ok = (bool)AccessTools.Method(typeof(KernelCombatPlanner), "TryEmitFromTournamentScript")
                .Invoke(planner, args)!;
            decision = args[3] as TeamCombatPlanner.Decision;
            return ok;
        }
        finally { foreach (var p in previous) AutoPilot.Set(p.NetId, p.Driven); }
    }

    private static object? Get(object planner, string name)
        => AccessTools.Field(typeof(KernelCombatPlanner), name)!.GetValue(planner);
}

using System.Reflection;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.Common.Mirrors;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// R2 2026-10-08: isolated physical source faults each Rebuild0/RED1/exact restore Rebuild0/GREEN0.
// Raw commands, physical original/mutant/restored bytes, runtime exits and DLLs:
// work/szg-repair-20261008T124624/combat/risk-prefix-coverage-proposal/r2-runtime/proof.json.
// lose-typed-coverage-connection: Typed risk coverage: production coverage must populate the immutable prefix risk reference index
// cached-risk-order-reversed: Typed risk coverage: cached immutable risk references must still receive fresh live classification
// eager-tail-before-prefix: Typed risk coverage: prefix fork must retain its first-MoveNext snapshot and read the replacement tail late
// mutable-tail-risk-snapshot: Typed risk coverage: mutable tail must retain original version failure after an ordinary entry append
// Every restoration executes all three native groups. These are structural controls;
// no historical gameplay defect, completed full-gate result or deployment is claimed.
// Direct MoveNext witnesses avoid relying on late patches to tiny iterator factories.
internal static class TypedRiskCoverageScenarios
{
    private static bool observing;
    private static readonly List<CombatPredictionRiskEntry> described = new();
    private static readonly MethodInfo describe = AccessTools.Method(typeof(PredictionCoverage), "DescribeGap");

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var patch = new Harmony("coopbots.private.typed-risk-coverage");
        patch.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(TypedRiskCoverageScenarios), nameof(Format)));
        patch.Patch(describe, prefix: new HarmonyMethod(typeof(TypedRiskCoverageScenarios), nameof(ObserveDescription)));
        try
        {
            if (selected is null or "classify") Classifications();
            if (selected is null or "prefix") PrefixLifetime();
            if (selected is null or "tail") TailLifetime();
        }
        finally { observing = false; described.Clear(); patch.UnpatchAll(patch.Id); }
    }

    private static void Classifications()
    {
        var session = NativeBoard();
        var history = session.Simulator.History;
        Publish(session, PredictionRiskReason.MethodNotMirrored);
        history.CardsSelected([]);
        Publish(session, PredictionRiskReason.MethodNotMirrored);
        history.Fork(new PredictionTrace());
        history.RecordRisk(PredictionRiskReason.MethodMirrorIncomplete);
        Publish(session, PredictionRiskReason.MethodMirrorIncomplete);
        var risks = history.OfType<CombatPredictionRiskEntry>().ToArray();
        Check(risks.Length == 4, "actual risk publications must include four original occurrences");
        var expected = Observe(() => Baseline(session.Simulator));
        Check(described.SequenceEqual(risks), "old live classification observer must see every original occurrence");
        Check(expected.Any(x => x.Compensated) && expected.Any(x => !x.Compensated)
            && expected.Count == 3, "native trace fixture must exercise old compensation and result dedup");
        var actual = Observe(() => PredictionCoverage.Collect(session.Simulator));
        Check(expected.SequenceEqual(actual) && described.SequenceEqual(risks),
            "typed production coverage must preserve every live classification and exact sorted output");
        var again = Observe(() => PredictionCoverage.Collect(session.Simulator));
        // R2 cached-risk-order-reversed: RED at this guard, all native groups restored GREEN.
        Check(expected.SequenceEqual(again) && described.SequenceEqual(risks),
            "cached immutable risk references must still receive fresh live classification");
        var prefix = AccessTools.Field(typeof(CombatPredictionHistory), "_prefix").GetValue(history)!;
        var riskField = AccessTools.Field(prefix.GetType(), "_riskEntries");
        var cached = riskField.GetValue(prefix);
        // R2 lose-typed-coverage-connection: original full-timeline loop leaves this index absent.
        Check(cached is CombatPredictionRiskEntry[] entries && entries.Length == 2,
            "production coverage must populate the immutable prefix risk reference index");
        var fork = session.Fork();
        Observe(() => PredictionCoverage.Collect(fork.Simulator));
        Check(ReferenceEquals(cached, riskField.GetValue(prefix)),
            "immutable prefix risk reference index must survive independent owned forks");
        Console.WriteLine("PASS: typed production coverage preserves four live classifications, three sorted gaps, and reused immutable references across forks.");
    }

    private static void PrefixLifetime()
    {
        foreach (var typed in new[] {false, true})
        {
            var session = NativeBoard(); var history = session.Simulator.History;
            Publish(session, PredictionRiskReason.MethodNotMirrored);
            history.Fork(new PredictionTrace());
            var a = history.OfType<CombatPredictionRiskEntry>().Single();
            Publish(session, PredictionRiskReason.MethodMirrorIncomplete);
            var b = history.OfType<CombatPredictionRiskEntry>().Last();
            using var iterator = Enumerate(history, typed).GetEnumerator();
            Check(iterator.MoveNext() && ReferenceEquals(iterator.Current, a), "prefix must yield its first original reference");
            // After the same risk yield at which DescribeGap runs: seal the old tail,
            // then publish a new tail. The captured ordered prefix must not chase b.
            history.Fork(new PredictionTrace());
            history.RecordRisk(PredictionRiskReason.MethodNotMirrored);
            var c = history.OfType<CombatPredictionRiskEntry>().Last();
            var rest = Drain(iterator);
            // R2 eager-tail-before-prefix: yielding old b instead of replacement c is named RED.
            Check(rest.SequenceEqual(new[] {c}) && !rest.Contains(b),
                "prefix fork must retain its first-MoveNext snapshot and read the replacement tail late");

            var second = NativeBoard(); var h = second.Simulator.History;
            Publish(second, PredictionRiskReason.MethodNotMirrored); h.Fork(new PredictionTrace());
            using var delayed = Enumerate(h, typed).GetEnumerator();
            Publish(second, PredictionRiskReason.MethodMirrorIncomplete); h.Fork(new PredictionTrace());
            h.RecordRisk(PredictionRiskReason.MethodNotMirrored);
            var all = h.OfType<CombatPredictionRiskEntry>().ToArray();
            Check(Drain(delayed).SequenceEqual(all),
                "risk prefix snapshot must occur on first MoveNext rather than iterator factory entry");
        }
        Console.WriteLine("PASS: typed risk iteration preserves deferred prefix capture and prefix-fork/replacement-tail visibility.");
    }

    private static void TailLifetime()
    {
        foreach (var typed in new[] {false, true})
        {
            var session = NativeBoard(); var history = session.Simulator.History;
            Publish(session, PredictionRiskReason.MethodNotMirrored);
            using var iterator = Enumerate(history, typed).GetEnumerator();
            Check(iterator.MoveNext(), "mutable tail must yield its original risk");
            history.CardsSelected([]); // ordinary entry must still invalidate the original List enumerator
            var failed = false;
            try { iterator.MoveNext(); } catch (InvalidOperationException) { failed = true; }
            // R2 mutable-tail-risk-snapshot: ToArray loses the original List version failure.
            Check(failed, "mutable tail must retain original version failure after an ordinary entry append");

            var second = NativeBoard(); var h = second.Simulator.History;
            Publish(second, PredictionRiskReason.MethodNotMirrored);
            Publish(second, PredictionRiskReason.MethodMirrorIncomplete);
            var old = h.OfType<CombatPredictionRiskEntry>().ToArray();
            using var sealedTail = Enumerate(h, typed).GetEnumerator();
            Check(sealedTail.MoveNext() && ReferenceEquals(sealedTail.Current, old[0]), "old tail first risk must be live");
            h.Fork(new PredictionTrace()); // ToArray and field replacement do not mutate the old List
            h.RecordRisk(PredictionRiskReason.MethodNotMirrored);
            Check(Drain(sealedTail).SequenceEqual(old.Skip(1)),
                "tail seal must continue its original List and must not chase newly published tail risk");
        }
        Console.WriteLine("PASS: typed mutable tail keeps ordinary-append version failure and old-list continuation after sealing.");
    }

    private static IEnumerable<CombatPredictionRiskEntry> Enumerate(CombatPredictionHistory history, bool typed)
        => typed ? history.RiskEntries() : history.OfType<CombatPredictionRiskEntry>();
    private static List<CombatPredictionRiskEntry> Drain(IEnumerator<CombatPredictionRiskEntry> iterator)
    { var result = new List<CombatPredictionRiskEntry>(); while(iterator.MoveNext())result.Add(iterator.Current); return result; }
    private static IReadOnlyList<PredictionGap> Observe(Func<IReadOnlyList<PredictionGap>> query)
    { described.Clear(); observing = true; try { return query(); } finally { observing = false; } }
    private static void ObserveDescription(CombatPredictionRiskEntry entry)
    { if(observing) described.Add(entry); }
    private static IReadOnlyList<PredictionGap> Baseline(CombatPredictionSimulator simulator)
    {
        var seen = new HashSet<(string SourceId, string Method, string Reason, bool Compensated)>();
        var result = new List<PredictionGap>();
        foreach (CombatPredictionHistoryEntry entry in simulator.History)
        {
            if (entry is not CombatPredictionRiskEntry risk) continue;
            var gap = ((string SourceId, string Method, string Reason, bool Compensated))describe.Invoke(null, [risk])!;
            if (seen.Add(gap)) result.Add(new(gap.SourceId,gap.Method,gap.Reason,gap.Compensated));
        }
        return result.OrderBy(x=>x.SourceId,StringComparer.Ordinal).ThenBy(x=>x.Method,StringComparer.Ordinal).ToList();
    }
    private static void Publish(KernelSession session, PredictionRiskReason reason)
    {
        var onPlay = new MirrorMethodSpec(typeof(CardModel), "OnPlay", BindingFlags.Instance|BindingFlags.NonPublic,
            [typeof(PlayerChoiceContext),typeof(CardPlay)]);
        using(session.Simulator.PushMethodSource(ModelDb.Card<Armaments>(),onPlay))
            session.Simulator.History.RecordRisk(reason);
    }
    private static KernelSession NativeBoard()
    {
        var p=Player.CreateForNewRun<Deprived>(UnlockState.all,58999);
        var c=new CombatState(runState:RunState.CreateForTest([p],seed:"TYPED-RISK-COVERAGE")){RoundNumber=1};
        p.ResetCombatState();c.AddPlayer(p);p.MaxEnergy=3;p.PlayerCombatState!.Phase=PlayerTurnPhase.Play;p.PlayerCombatState.GainEnergy(3);
        p.Creature.SetMaxHpInternal(80);p.Creature.SetCurrentHpInternal(80);
        var e=c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"0");c.AddCreature(e);e.Monster!.SetUpForCombat();e.SetMaxHpInternal(500);e.SetCurrentHpInternal(500);
        e.Monster.SetMoveImmediate((MoveState)e.Monster.MoveStateMachine!.States["ATTACK"],true);
        ModelDb.Power<EnragePower>().ToMutable().ApplyInternal(e,3,true);
        var strike=c.CreateCard<StrikeIronclad>(p);p.PlayerCombatState.Hand.AddInternal(strike);
        var session=KernelSession.Capture(c);
        Check(session.Play(strike,e,out var reason),"actual native paid card must complete: "+reason);
        Check(session.Hp(e)==494 && session.Energy(p)==2,"boundary fixture must start after real paid native card effects");
        return session;
    }
    private static bool Format(ref string __result) { __result="";return false; }
    private static void Check(bool value,string why) { if(!value)throw new Exception("Typed risk coverage: "+why); }
}

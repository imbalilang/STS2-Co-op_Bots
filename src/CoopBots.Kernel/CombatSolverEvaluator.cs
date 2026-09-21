using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;

namespace CoopBots.Kernel;

/// <summary>
/// The score the team search ranks on: the vendored engine's own state evaluation,
/// summed over the living seats.
///
/// WHY THIS IS THE VENDORED ENGINE AND NOT THE 0.41 ASSEMBLY — the finding that settled
/// it, and it is a type-identity fact rather than a preference:
///
///   src/CoopBots.Kernel/Vendor/...     CombatPredictionSimulator
///                                      namespace CoopBots.Kernel.Vendor.Engine.InCombat.Simulation
///   work/combatsolver-sync-latest/...  CombatPredictionSimulator
///                                      namespace CombatSolver.Engine.InCombat.Simulation
///
/// Two distinct types of the same name in two assemblies. <c>CombatBeamSolver.Snapshot</c>
/// takes the simulator as a PARAMETER, which is what made this seam look usable — but the
/// engine can only price a simulator of ITS OWN type. Handing it our joint state throws,
/// the reflection invoke fails, and the score silently falls back to our reduced
/// evaluator: a worse outcome than not trying.
///
/// So "joint four-hand expansion" and "0.41's brain" are MUTUALLY EXCLUSIVE, because the
/// evaluator and the state are one thing, not two. What 0.41 CAN do is run end to end on
/// its own state — see CombatSolver41, which is shipped and probed for exactly that
/// reason — but its expansion walks one player's hand, so it cannot see four.
///
/// This was tried the other way round and reverted. Reverting matters: a scorer that
/// always fails is not a no-op, it is a downgrade, because the caller falls back.
///
/// The seam still works for the vendored engine, and the reasoning that made it work is
/// unchanged: our joint multi-actor expansion produces the state — where a Vulnerable →
/// Knockdown → Flanking → burst line actually exists — and this prices the leaves.
///
/// Team value is a SUM over seats, an approximation and not a derivation: each view sees
/// the same enemies, so the seat that cashes a window pays for it, but how shared state
/// is attributed across views is a modelling choice rather than a result.
/// </summary>
public static class CombatSolverEvaluator
{
    private static MethodInfo? _snapshot;
    private static readonly Dictionary<ulong, object> Solvers = new();
    private static CombatState? _forCombat;
    private static bool _failed;

    public static double? TeamScore(KernelSession session, CombatState combat, int turn,
        IReadOnlySet<uint> processedEnemyDeaths, IEnumerable<ulong> seats)
    {
        var simulator = session.Simulator;
        double total = 0;
        var scored = 0;
        foreach (var seat in seats)
        {
            if (Score(simulator, combat, turn, processedEnemyDeaths, seat) is not { } value) continue;
            total += value;
            scored++;
        }
        return scored == 0 ? null : total;
    }

    private static double? Score(CombatPredictionSimulator simulator, CombatState combat, int turn,
        IReadOnlySet<uint> processedEnemyDeaths, ulong seat)
    {
        try
        {
            if (_failed) return null;
            if (!ReferenceEquals(_forCombat, combat))
            {
                Solvers.Clear();
                _forCombat = combat;
                _snapshot = null;
            }
            if (!Solvers.TryGetValue(seat, out var solver))
            {
                var player = combat.Players.FirstOrDefault(candidate => candidate.NetId == seat);
                if (player is null) return null;
                // Capture is per seat: the constructor's playerIdentity IS the perspective,
                // so there is no shared instance to reuse here.
                solver = new CombatBeamSolver(
                    CombatRootSnapshot.Capture(combat, player),
                    SolverDisplayNames.Capture(combat),
                    BattleDamageTracker.Observe(combat),
                    NeutralSearchPolicy.Create());
                Solvers[seat] = solver;
            }

            _snapshot ??= typeof(CombatBeamSolver).GetMethod(
                "Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(CombatBeamSolver).FullName, "Snapshot");

            var snapshot = _snapshot.Invoke(solver, new object?[]
            {
                simulator, turn, 0, 0, SearchBoundaryReason.None, processedEnemyDeaths,
            });
            if (snapshot is null) return null;
            var score = snapshot.GetType().GetProperty("Score")?.GetValue(snapshot);
            return score is double value ? value : null;
        }
        catch (Exception)
        {
            // Mark it dead so a broken bridge costs one attempt per combat, not one per
            // leaf. The caller falls back to our own evaluator, which is the pre-existing
            // behaviour and therefore a safe place to land.
            _failed = true;
            return null;
        }
    }
}

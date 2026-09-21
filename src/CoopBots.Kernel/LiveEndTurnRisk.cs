using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

/// <summary>
/// What the coming enemy phase would actually cost, measured by running it in the
/// simulator from the LIVE board instead of trusting the plan's own projection.
///
/// Ported from CombatSolver's LiveEndTurnRiskEvaluator, which clones the live state,
/// runs the turn-end and enemy-turn phases, and compares the projected HP loss against
/// what the plan predicted. The point is the same here: a kept plan's actions after a
/// turn boundary were computed against a SIMULATED enemy turn, and the enemy turn is
/// really played by the game — so before committing to the rest of a plan, run that
/// phase for real and see whether it disagrees.
///
/// <see cref="Result.Simulated"/> matters as much as the numbers: a phase that could
/// not be simulated is not evidence of safety, and callers must not read a failure as
/// "no risk".
/// </summary>
public static class LiveEndTurnRisk
{
    public readonly record struct Result(double HpLost, int Deaths, bool Simulated);

    public static Result Evaluate(CombatState combat, IReadOnlyList<Player> actors, int rounds)
    {
        try
        {
            if (actors.Count == 0) return new(0, 0, false);
            var session = KernelSession.Capture(combat);
            // Ending every driven seat's turn in sequence is what advances the enemy
            // phase in the simulator; one seat's end alone leaves it half-run, which is
            // why the search does the same thing node by node.
            foreach (var actor in actors)
            {
                if (!actor.Creature.IsAlive) continue;
                if (!session.EndTurn(actor, Math.Max(1, rounds), out _)) return new(0, 0, false);
            }
            var lost = 0.0;
            var deaths = 0;
            foreach (var actor in actors)
            {
                var hp = session.Hp(actor.Creature);
                if (hp <= 0) deaths++;
                var live = actor.Creature.CurrentHp;
                if (hp < live) lost += live - hp;
            }
            return new(lost, deaths, true);
        }
        catch
        {
            return new(0, 0, false);
        }
    }
}

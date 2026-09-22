using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor;

internal static partial class MonsterMoveSemantics
{
    // A native monster move receives the whole target list. Effects on the
    // monster (growth, summons, stance) are committed once, not once per target.
    internal static bool ApplyPartyMove(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        ForecastMove move, IReadOnlyList<Creature> targets, ISet<uint> deaths,
        bool autoResolveKnowledgeChoices = false)
    {
        if (targets.Count == 0) return true;
        if (move.Owner.Monster is ThievingHopper && move.Move.Id == "THIEVERY_MOVE")
            foreach (var target in targets) MonsterMoveEffects.ApplyBeforeAttack(simulator, combat, move, target);
        else MonsterMoveEffects.ApplyBeforeAttack(simulator, combat, move, targets[0]);
        if (simulator.HasPendingChoice) return false;
        var fullyBlocked = false;
        var context = move.AttackHits.Count > 0
            ? simulator.BeginAttackContext(new AttackCommand(0m).FromMonster(move.Owner.Monster!).WithHitCount(0)) : null;
        var complete = false;
        try
        {
            foreach (var hit in move.AttackHits)
            {
                if (simulator.State.GetCreature(move.Owner).IsDead) break;
                var results = new List<DamageResult>();
                foreach (var target in targets)
                {
                    if (simulator.State.GetCreature(target).IsDead) continue;
                    results.AddRange(DamagePlayer(simulator, combat, move.Owner, target,
                        combat.AdjustMonsterMoveDamage(move.Owner, move.Move.Id, hit.BaseDamage)));
                    if (simulator.HasPendingChoice) return false;
                }
                simulator.AddAttackContextHit(context!, results);
                fullyBlocked |= results.Any(r => targets.Contains(r.Receiver) && r.WasFullyBlocked);
                if (!CorePowerSupport.ApplyEnemyDeathPowers(simulator, combat, combat.KnownEnemies, deaths)) return false;
            }
            complete = true;
        }
        finally { if (context is not null) simulator.EndAttackContext(context, complete); }
        if (simulator.HasPendingChoice) return false;
        if (fullyBlocked && combat.GetAmount<ImbalancedPower>(move.Owner) > 0)
        {
            if (move.Owner.Monster is BowlbugRock) combat.ForceStunnedMove(move.Owner, "HEADBUTT_MOVE");
            combat.StunNextMove(move.Owner);
        }
        MonsterMoveEffects.Apply(simulator, combat, move, targets[0], out _,
            partyTargets: targets, autoResolveKnowledgeChoices: autoResolveKnowledgeChoices);
        if (!CorePowerSupport.ApplyEnemyDeathPowers(simulator, combat, combat.KnownEnemies, deaths)) return false;
        simulator.SynchronizePowerAmountPredictionStates();
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        combat.NormalizeAeonglassWithers(simulator);
        combat.NormalizeCardAfflictions(simulator);
        return !simulator.HasPendingChoice;
    }
}

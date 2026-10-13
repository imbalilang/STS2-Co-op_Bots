using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
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
                // Native AttackCommand sends the whole live target list to
                // CreatureCmd.Damage for each hit. All targets lose HP before
                // death hooks run; a Tank dying on this hit must not remove
                // the other targets' Guarded before their same-hit damage.
                var results = DamagePartyHit(simulator, combat, move.Owner, targets,
                    combat.AdjustMonsterMoveDamage(move.Owner, move.Move.Id, hit.BaseDamage));
                if (simulator.HasPendingChoice) return false;
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

    private static IReadOnlyList<DamageResult> DamagePartyHit(CombatPredictionSimulator simulator,
        SimulatedCombatState combat, Creature attacker, IReadOnlyList<Creature> targets, int baseDamage)
    {
        // Keep DamagePlayer's dead-Osty suppression while using the native
        // batch transaction. The temporary suppression is local to this hit.
        List<(Creature Osty, int Amount)>? suppressed = null;
        foreach (var target in targets)
        {
            Creature? osty = target.Player is { } owner ? simulator.State.GetOsty(owner) : null;
            if (osty is not null && simulator.State.GetCreature(osty).IsDead
                && combat.GetAmount<DieForYouPower>(osty) is > 0 and var amount)
            {
                (suppressed ??= []).Add((osty, amount));
                combat.SetAmount<DieForYouPower>(osty, 0);
            }
        }
        try
        {
            using (simulator.PushDamageSource(
                CombatDamageSource.For(CombatDamageSourceKind.MonsterMove, attacker.Monster?.Id.Entry)))
                return simulator.Damage(targets, baseDamage, ValueProp.Move, attacker);
        }
        finally
        {
            if (suppressed is not null)
                foreach (var (osty, amount) in suppressed) combat.SetAmount<DieForYouPower>(osty, amount);
        }
    }
}

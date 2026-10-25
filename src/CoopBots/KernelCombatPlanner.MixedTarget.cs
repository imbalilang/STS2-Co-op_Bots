using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    // Upstream has no concurrent human actor. Our historical mixed proposals
    // retain their physical card when that actor removes the old enemy; only
    // live native targeting may rebind it. Owner's 2026-10-25 stall repair keeps
    // stable execution without transferring old-root quality to the new target.
    // A historical physical-card proposal can outlive its enemy. Repair only
    // that target, using native legality; changing cost, card identity or seat
    // eligibility is still handled by the normal execution guards.
    private bool TryRepairMixedDeadTarget(CombatState combat, KernelTeamSearch.Action next,
        CardModel card, ref Creature? target)
    {
        if (card.TargetType != TargetType.AnyEnemy || next.Target is null
            || target is { IsAlive: true } || card.CanPlayTargeting(target)) return false;
        var replacement = combat.HittableEnemies.Where(enemy => card.CanPlayTargeting(enemy))
            .OrderByDescending(enemy => focus == enemy.CombatId)
            .ThenBy(enemy => enemy.CurrentHp + enemy.Block).ThenBy(enemy => enemy.CombatId)
            .FirstOrDefault();
        if (replacement is null) return false;
        RetireMixedChoiceContracts(combat, "historical enemy target died");
        var updated = tournamentScript!.ToArray();
        updated[tournamentScriptIndex] = next with { Target = replacement,
            Choices = null, ChoiceOrdinals = null, TurnStartChoices = null };
        tournamentScript = updated;
        mixedHistoricalScript = updated;
        mixedContinuation = null;
        if (tournamentScriptRecord is { } record)
            tournamentScriptRecord = record with { Victory = false, Kind = EvidenceKind.EstimatedCutoff,
                CutoffReason = "mixed-dead-target-repaired-quality-unverified", IrreversibleLoss = 0 };
        target = replacement;
        ReportSearchLine($"mixed target repaired: card={card.Id.Entry} old={next.Target.CombatId} "
            + $"new={replacement.CombatId}; physical suffix retained, old-root quality and dependent choices withdrawn");
        return true;
    }
}

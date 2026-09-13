using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel;

/// <summary>
/// Priced-from-data fallback for cards the snapshot has no OnPlay mirror for.
/// The upstream solver answers "not mirrored" by refusing the action, which
/// removes the card from the search entirely. That is wrong for a whole family
/// this port runs into constantly: multiplayer-only support cards (Blaze,
/// Coordinate, and anything shaped like them) exist only in co-op and were
/// therefore never played by the bots at all.
///
/// Only the one shape we can describe exactly is accepted: a Skill that does
/// nothing but grant a single declared power to itself or one ally. Damage,
/// block, draw, energy, HP costs, X costs and multi-var cards are all rejected,
/// because guessing those wrong would be worse than not playing the card. The
/// action stays an estimate (<see cref="KernelSession.LastActionHadEnvironmentalRisk"/>),
/// so it can never be reported as a confirmed kill or rescue.
/// </summary>
internal static class StructuralCardMirror
{
    /// <summary>
    /// Adds the card's declared power to the simulation when its OnPlay had no
    /// mirror. Returns false when the card is outside the describable shape, so
    /// the caller keeps treating it as a boundary.
    /// </summary>
    internal static bool ApplyIfUnmodeled(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CardModel card,
        Creature? target)
    {
        if (!IsUnmodeled(simulator, card)) return false;
        if (!TryDescribe(card, target, out var powerType, out var amount, out var recipients)) return false;
        foreach (var recipient in recipients)
        {
            var before = AmountOf(combat, powerType, recipient);
            combat.ApplyPower(powerType, recipient, amount, card.Owner.Creature);
            var applied = AmountOf(combat, powerType, recipient) - before;
            if (applied <= 0) continue;
            // A temporary Strength power carries its real Strength change through
            // the engine's paired helper in every mirrored card. Applying only the
            // declared power would leave Strength untouched while the end-of-turn
            // restore still subtracts it, turning a buff into a debuff.
            if (typeof(TemporaryStrengthPower).IsAssignableFrom(powerType))
            {
                var instance = combat.EffectivePowers().FirstOrDefault(power =>
                    power.GetType() == powerType && ReferenceEquals(power.Owner, recipient));
                var sign = instance?.Type == PowerType.Buff ? 1 : -1;
                combat.ApplyPower(typeof(StrengthPower), recipient, sign * applied, card.Owner.Creature);
            }
        }
        // Applying a power leaves a pending amount change; resolve it here or the
        // branch cannot be forked and searched afterwards.
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        return true;
    }

    // The power a card-specific temporary effect declares as its origin. Matching
    // on the id (not the instance) keeps this working for the live mutable card.
    private static Type? OriginPowerFor(CardModel card)
    {
        try
        {
            foreach (var power in ModelDb.AllPowers.OfType<TemporaryStrengthPower>())
                if (power.OriginModel?.Id.Entry == card.Id.Entry) return power.GetType();
        }
        catch
        {
            // A power whose origin cannot be read simply leaves the declared
            // variable in place.
        }
        return null;
    }

    private static int AmountOf(SimulatedCombatState combat, Type powerType, Creature target)
        => combat.EffectivePowers()
            .Where(power => power.GetType() == powerType && ReferenceEquals(power.Owner, target))
            .Sum(power => power.Amount);

    // The OnPlay of this card recorded an uncompensated MethodNotMirrored risk,
    // i.e. the registry fell through to Unsupported for it.
    private static bool IsUnmodeled(CombatPredictionSimulator simulator, CardModel card)
        => PredictionCoverage.Collect(simulator).Any(gap =>
            !gap.Compensated && gap.Method == "OnPlay" && gap.SourceId == card.Id.Entry);

    // Exactly one PowerVar<T>, applied to the caster or a single chosen ally.
    private static bool TryDescribe(
        CardModel card,
        Creature? target,
        out Type powerType,
        out int amount,
        out IReadOnlyList<Creature> recipients)
    {
        powerType = typeof(PowerModel);
        amount = 0;
        recipients = [];

        if (card.Type != CardType.Skill) return false;
        if (card.EnergyCost.CostsX || card.HasStarCostX) return false;
        if (card.Owner?.Creature is not { } owner) return false;

        Type? declared = null;
        var declaredAmount = 0;
        foreach (var variable in card.DynamicVars.Values)
        {
            // Anything else (damage, block, draw, energy, HP loss, repeat, ...)
            // means the card does more than grant this power.
            if (variable.GetType().IsGenericType
                && variable.GetType().GetGenericTypeDefinition() == typeof(PowerVar<>))
            {
                if (declared is not null) return false;
                declared = variable.GetType().GetGenericArguments()[0];
                declaredAmount = (int)variable.BaseValue;
                continue;
            }
            return false;
        }
        if (declared is null || declaredAmount <= 0) return false;
        if (!typeof(PowerModel).IsAssignableFrom(declared)) return false;
        // A card may declare its power only for display while applying a
        // card-specific variant: Coordinate shows Strength but applies
        // CoordinatePower, a temporary Strength power. Such powers name their
        // origin card, which identifies them exactly.
        declared = OriginPowerFor(card) ?? declared;

        switch (card.TargetType)
        {
            case TargetType.Self:
                recipients = [owner];
                break;
            case TargetType.AnyAlly or TargetType.AnyPlayer:
                if (target is null || target == owner) return false;
                recipients = [target];
                break;
            default:
                // Multi-target shapes need per-ally amounts and ordering we cannot
                // verify from the model alone.
                return false;
        }

        // Never guess whether a control effect belongs on a teammate.
        if (recipients.Any(recipient => !recipient.IsPlayer)) return false;

        powerType = declared;
        amount = declaredAmount;
        return true;
    }
}

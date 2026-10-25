using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel;

/// <summary>
/// Our cross-fight tradeoff: a bottle may prevent a loss or save meaningful HP.
/// The former win-only gate wasted bottles through costly victories. Six HP is
/// an explicit tuning prior, not a measured causal value or a legality rule.
/// </summary>
public static class KernelPotionPolicy
{
    public static readonly ObjectiveConfig Objective = new(ConsumableUnitCost: 6);

    // Future healing is uncertain, so reserve three quarters of its native
    // capacity. An extra turn earns value only when the actual continuation
    // takes it; an unused AmbergrisPower at victory earns no speculative bonus.
    public const double HealingReservationDiscount = .75;

    public static double PreservationValue(PotionModel potion, IReadOnlyList<Player> party)
        => potion is Ambergris && party.Count > 0
            ? party.Max(p => p.Creature.MaxHp) * (double)potion.DynamicVars["HealPercent"].BaseValue / 100
                * HealingReservationDiscount
            : Objective.ConsumableUnitCost;

    public static double PreservationValue(KernelSession session, PotionModel potion, IReadOnlyList<Player> party)
        => potion is Ambergris && party.Count > 0
            ? party.Max(p => session.MaxHp(p.Creature)) * (double)session.PotionValue(potion, "HealPercent") / 100
                * HealingReservationDiscount
            : Objective.ConsumableUnitCost;

    internal static double ConsumedPreservationPremium(KernelSession session, IReadOnlyList<Player> party)
        => party.SelectMany(session.CapturedPotions).Where(p => p is Ambergris && !session.HoldsPotion(p))
            .Sum(p => Math.Max(0, PreservationValue(session, p, party) - Objective.ConsumableUnitCost));

    public static bool WorthComparing(KernelSession root, IReadOnlyList<Player> actors,
        IReadOnlyList<Player> party, TerminalRecord? baseline)
        => actors.Any(p => root.UsablePotions(p).Any(KernelSession.CanModelPotion))
           && (baseline?.Victory != true || baseline.IrreversibleLoss > 0
               || party.Sum(p => root.MaxHp(p.Creature)) - baseline.PostCombatHp.Sum()
                   > Objective.ConsumableUnitCost);

    public static bool Improves(TerminalRecord? baseline, TerminalRecord? candidate,
        double immediateBenefit = 0)
    {
        if (candidate is null) return false;
        if (candidate is { Victory: true, Verified: true })
            return baseline is null || TerminalComparer.Compare(candidate, baseline, Objective) > 0;
        if (baseline is not { Verified: true, Victory: false }) return false;
        var comparison = TerminalComparer.Compare(candidate, baseline, Objective);
        // Fixed-policy losing continuations can exchange damage for survival.
        // V5 live refused Dexterity at 8->9 rounds solely for 306->327 enemy HP.
        // If both lines wipe, try an actually useful effect unless that fallback
        // makes the wipe earlier; replan after resolving it. This does not
        // override a winning baseline or label an uncertain cutoff as a win.
        var usefulEmergency = candidate is { Verified: true, Victory: false }
            && immediateBenefit > 0.01
            && candidate.RoundsSurvived >= baseline.RoundsSurvived;
        return comparison > 0 || usefulEmergency;
    }

    public static double ImmediateBenefit(KernelSession root, IReadOnlyList<Player> party,
        KernelTeamSearch.Action? opening, IReadOnlyList<KernelTeamSearch.Action>? plan = null,
        KernelDecisionRequest? request = null)
    {
        if (request?.IsStopped == true) return 0;
        if (opening?.Potion is not { } potion) return 0;
        // A damage bottle spent into IntangiblePower is a 1-damage bottle. Do not
        // let the "both lines wipe, prefer a useful effect" fallback count it.
        var potionName = potion.GetType().Name;
        if (potionName is "FirePotion" or "PotionShapedRock" or "ExplosiveAmpoule")
        {
            if (opening.Target is Creature target && root.Power<IntangiblePower>(target) > 0)
                return 0;
            if (potionName == "ExplosiveAmpoule"
                && root.Enemies.Count > 0
                && root.Enemies.All(e => root.Power<IntangiblePower>(e) > 0))
                return 0;
        }
        var branch = root.Fork();
        var resolved = KernelSession.RequiresPotionChoiceContract(potion)
            ? branch.ReplayPlanned(opening, 80, out _) : branch.UsePotion(potion, opening.Target, out _);
        if (!resolved || request?.IsStopped == true) return 0;
        var raw = KernelTacticalPolicy.Value(branch, party) - KernelTacticalPolicy.Value(root, party);
        if (plan is null || potion.GetType().Name is not ("ShacklingPotion" or "BeetleJuice" or "WeakPotion" or "BlockPotion" or "Fortifier")) return raw;
        if (request?.IsStopped == true) return 0;
        var baseline = root.Fork();
        foreach (var action in plan.Take(64))
        {
            if (request?.IsStopped == true) return 0;
            if (action.EndTurn) break;
            if (ReferenceEquals(action, opening) || action.Potion == potion) continue;
            if (!baseline.ReplayPlanned(action, 80, out _)) return raw;
            if (request?.IsStopped == true) return 0;
            if (!branch.ReplayPlanned(action, 80, out _)) return raw;
            if (request?.IsStopped == true) return 0;
            if (baseline.HasTerminal || branch.HasTerminal) break;
        }
        // In particular: a subsequent Whistle cancels this enemy action on both
        // branches. Shackles then saves zero, and Shrink's future turns must earn
        // their value in the full continuation rather than this emergency fallback.
        return KernelTacticalPolicy.Value(branch, party) - KernelTacticalPolicy.Value(baseline, party);
    }
}

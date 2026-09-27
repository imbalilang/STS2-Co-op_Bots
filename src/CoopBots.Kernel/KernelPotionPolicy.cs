using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

/// <summary>
/// Our cross-fight tradeoff: a bottle may prevent a loss or save meaningful HP.
/// The former win-only gate wasted bottles through costly victories. Six HP is
/// an explicit tuning prior, not a measured causal value or a legality rule.
/// </summary>
public static class KernelPotionPolicy
{
    public static readonly ObjectiveConfig Objective = new(ConsumableUnitCost: 6);

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
        KernelTeamSearch.Action? opening, IReadOnlyList<KernelTeamSearch.Action>? plan = null)
    {
        if (opening?.Potion is not { } potion) return 0;
        var branch = root.Fork();
        if (!branch.UsePotion(potion, opening.Target, out _)) return 0;
        var raw = KernelTacticalPolicy.Value(branch, party) - KernelTacticalPolicy.Value(root, party);
        if (plan is null || potion.GetType().Name is not ("ShacklingPotion" or "BeetleJuice" or "WeakPotion" or "BlockPotion")) return raw;
        var baseline = root.Fork();
        foreach (var action in plan.Take(64))
        {
            if (action.EndTurn) break;
            if (ReferenceEquals(action, opening) || action.Potion == potion) continue;
            if (!baseline.ReplayPlanned(action, 80, out _) || !branch.ReplayPlanned(action, 80, out _)) return raw;
            if (baseline.HasTerminal || branch.HasTerminal) break;
        }
        // In particular: a subsequent Whistle cancels this enemy action on both
        // branches. Shackles then saves zero, and Shrink's future turns must earn
        // their value in the full continuation rather than this emergency fallback.
        return KernelTacticalPolicy.Value(branch, party) - KernelTacticalPolicy.Value(baseline, party);
    }
}

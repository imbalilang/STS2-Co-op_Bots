using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel;

// OUR continuation/beam heuristic; upstream has no conservation policy. Native
// branches supply payoff, including exhaust/HP-loss hooks. Do not name individual
// defensive cards or prohibit their use in the search (owner report 2026-10-03).
internal static class KernelExhaustPolicy
{
    private static bool HasOption(CardModel card) => card.Type == CardType.Skill
        && card.Keywords.Contains(CardKeyword.Exhaust) && !card.Keywords.Contains(CardKeyword.Ethereal);

    internal static double Reserve(KernelSession state, Player player)
    {
        var piles = state.Simulator.State.GetPlayerCombatState(player);
        return 1.5 * (piles.Hand.Cards.Count(c => HasOption(c.Preview))
            + piles.DrawPile.Cards.Count(c => HasOption(c.Preview))
            + piles.DiscardPile.Cards.Count(c => HasOption(c.Preview)));
    }

    internal static bool ShouldHold(KernelSession state, IReadOnlyList<Player> party, Player player, CardModel card)
    {
        if (!HasOption(card) || !state.CanPlay(card)) return false;
        var resources = CardResourceTiming.Of(card, state.Energy(player));
        if (resources.EnergyNow > 0 || resources.EnergyNextTurn > 0 || resources.DrawNextTurn > 0) return false;
        var before = KernelTacticalPolicy.Value(state, party, party);
        var exposure = party.Where(p => state.Hp(p.Creature) > 0)
            .Select(p => (Player: p, Loss: state.Threat(p).HpLoss)).ToArray();
        // Preserve the existing low-threat conservation preference for all
        // one-shot skills. Minor prevention alone does not repay the option;
        // lethal pressure and other native payoffs still do.
        var minorPressure = exposure.Sum(e => e.Loss) <= 4
            && exposure.All(e => e.Loss < state.Hp(e.Player.Creature));
        foreach (var target in state.Targets(card))
        {
            var branch = state.CardBranches(card, target, 1).FirstOrDefault(b => b.Boundary.Length == 0);
            if (branch is null) return false; // unknown effects remain available to the policy
            var after = branch.State;
            if (after.HasWon || party.Any(p => after.DrawPile(p).Count < state.DrawPile(p).Count)) return false;
            // Native generated payable cards create immediate options, just as drawing does.
            if (party.Any(p => after.Hand(p).Any(c => !state.Hand(p).Contains(c) && after.CanPlay(c)))) return false;
            // The owner's 2026-10-04 report distinguishes expiring defence from
            // lasting setup. Compare native effects, not card names or printed
            // variables: blocked debuffs do not qualify. Vulnerable stacks are
            // also a spendable scaling resource (not merely buff duration), so
            // no existing-stack ceiling may turn this into a conservation veto.
            if (after.Enemies.Any(e => after.Power<VulnerablePower>(e) > state.Power<VulnerablePower>(e)
                || after.Power<WeakPower>(e) > Math.Max(1, state.Power<WeakPower>(e)))) return false;
            // Upgrading/discounting held or future cards is persistent output
            // even when this small state score has no printed damage to count.
            // Compare native card states, so Apotheosis/Madness and future
            // effects of the same shape are not mistaken for unused defence.
            foreach (var member in party)
            foreach (var remaining in after.Hand(member).Concat(after.DrawPile(member)).Concat(after.Discard(member)))
                if (state.CardStateKey(remaining) != after.CardStateKey(remaining)) return false;
            var value = KernelTacticalPolicy.ActionValue(state, after, party, party);
            // ActionValue already neutralizes actual current card expenditure.
            if (minorPressure)
                value -= exposure.Sum(e => Math.Max(0, e.Loss - after.Threat(e.Player).HpLoss)) * 1.3;
            if (value > before + .001) return false;
        }
        return true;
    }
}

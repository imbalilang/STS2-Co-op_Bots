using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

/// <summary>
/// Our bounded one-action policy, alongside the upstream-style cheap rollouts.
/// Printed damage/block cannot distinguish shielding, over-block or lethal enemy
/// pressure. Compare resolved states instead; the tournament still judges the
/// complete ending, not this local heuristic. Coefficients are HP-scale estimates.
/// </summary>
internal static class KernelTacticalPolicy
{
    internal const int Width = 8;

    internal static double ActionValue(KernelSession before,KernelSession after,
        IReadOnlyList<Player> party,IReadOnlyList<Player> actors)
    {
        if(after.HasWon || !party.Any(before.HasWitheringPresence))return Value(after,party,actors);
        var value=ValueCore(after,party,actors,before);
        // The state heuristic credits unused resources and future opportunities.
        // These are not extra costs of realizing a card's actual payoff. Refund
        // only resources consumed by card plays, never a seat simply passing.
        foreach(var player in party)
        {
            var plays=Math.Max(0,after.CardsPlayedThisTurn(player)-before.CardsPlayedThisTurn(player));
            if(plays==0)continue;
            value+=Math.Max(0,before.Energy(player)-after.Energy(player))*3;
            value+=Math.Min(plays,Math.Max(0,before.UsefulHandCount(player)-after.UsefulHandCount(player)))*.7;
        }
        // Existing Vulnerable was already credited on held attacks. Its damage
        // is now realized; keep that conversion neutral. Newly applied Vulnerable
        // still earns its remaining-hand setup benefit, and expired/removed
        // Vulnerable is not refunded.
        var continuing=before.Enemies.Where(e=>before.Power<MegaCrit.Sts2.Core.Models.Powers.VulnerablePower>(e)>0
            && after.Enemies.Contains(e) && after.Power<MegaCrit.Sts2.Core.Models.Powers.VulnerablePower>(e)>0).ToHashSet();
        value+=(KernelTeamCoordination.VulnerableValue(before,actors,continuing)
            -KernelTeamCoordination.VulnerableValue(after,actors,continuing))*.8;
        return value;
    }

    internal static double Value(KernelSession state, IReadOnlyList<Player> party,
        IReadOnlyList<Player>? actors = null)
        => ValueCore(state,party,actors,null);

    private static double ValueCore(KernelSession state, IReadOnlyList<Player> party,
        IReadOnlyList<Player>? actors, KernelSession? futureReference)
    {
        if (state.HasWon) return 1_000_000 + party.Sum(p => state.Hp(p.Creature));
        var attacks = state.CurrentAttacks();
        var futureHits = futureReference?.CurrentAttacks().Sum(a=>a.Repeats) ?? attacks.Sum(a=>a.Repeats);
        // Team focus: linear total HP made equal damage to separate enemies tie.
        // A living enemy has future turns even while buffing now. The concave
        // HP term makes damage on a wounded enemy more valuable until it dies;
        // resolved block/thorns and player survival remain in the same score.
        var value = -state.Enemies.Sum(e => state.Hp(e) * 0.8 + state.Block(e) * 0.15
            + 12 + 8 * Math.Sqrt((double)state.Hp(e) / Math.Max(1, state.MaxHp(e))));
        // Poison used to be invisible to both one-action probes and the joint
        // frontier. They pruned its setup before the real future ticks occurred.
        value += state.Enemies.Sum(e => Math.Min(state.Hp(e),
            CardPoisonEffects.TwoTicks(state.Power<MegaCrit.Sts2.Core.Models.Powers.PoisonPower>(e))) * .8);
        value += KernelTeamCoordination.VulnerableValue(state, actors ?? party) * .8;
        foreach (var player in party)
        {
            var hp = state.Hp(player.Creature);
            if (hp <= 0) { value -= 10_000; continue; }
            var threat = state.Threat(player);
            var incoming = threat.AttackDamage;
            var exposed = threat.HpLoss;
            value += hp - exposed * 1.3;
            // Future Wither draws are resolved by native continuations. Do not
            // charge an additional estimated pollution tax on top of those losses.
            // Threat still includes this hand's actual end-turn status damage.
            // Protect a vulnerable seat instead of exchanging its life for chip damage.
            if (exposed >= hp) value -= 100 + exposed - hp;
            value += futureReference is null ? state.PersistentValue(player, (int)incoming, futureHits)
                : state.PersistentValue(player,(int)futureReference.Threat(player).AttackDamage,futureHits,futureReference);
            if (!state.CanAct(player)) continue;
            value += state.Energy(player) * 3 + state.UsefulHandCount(player) * 0.7;
        }
        return value;
    }
}

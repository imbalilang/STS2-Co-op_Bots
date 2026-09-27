using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel;

/// <summary>
/// Upstream evaluates a single player's continuation. Our multiplayer rollouts
/// choose from every delegated seat, as requested by the user on 2026-09-27:
/// late-seat Vulnerable must enable early-seat attacks in every round.
/// These bounded hints rank actions; complete simulated endings still select
/// the script. No class/seat priority and no additional deployment refusal.
/// </summary>
internal static class KernelTeamCoordination
{
    internal static double OrderingBonus(KernelSession state, IReadOnlyList<Player> actors,
        CardModel card, Creature? target, RolloutPolicyKind policy)
    {
        var payoff = SetupPayoff(state, actors, card, target);
        // Keep each baseline's units and defensive priority. Setup contributes
        // the damage it enables, not a blanket mandate to spend all energy on it.
        return payoff <= 0 ? 0 : payoff * (policy == RolloutPolicyKind.Defend ? 1 : 1e3) + 1;
    }

    internal static double SetupPayoff(KernelSession before, IReadOnlyList<Player> actors,
        CardModel card, Creature? target)
    {
        if (!card.DynamicVars.Values.OfType<PowerVar<VulnerablePower>>().Any()
            && card.GetType().Name is not ("Uppercut" or "Putrefy" or "Shockwave")) return 0;
        if (target is not null && before.Power<VulnerablePower>(target) > 0) return 0;
        if (target is null && before.Enemies.All(e => before.Power<VulnerablePower>(e) > 0)) return 0;
        // Resolve the setup itself: its energy is already spent, dead targets
        // have disappeared, and Artifact/blocked debuffs confer no payoff.
        var after = before.CardBranches(card, target, maximumBranches: 1)
            .FirstOrDefault(b => b.Boundary.Length == 0)?.State;
        if (after is null) return 0;
        var newlyVulnerable = after.Enemies.Where(e => before.Power<VulnerablePower>(e) == 0
            && after.Power<VulnerablePower>(e) > 0).ToHashSet();
        return newlyVulnerable.Count == 0 ? 0 : VulnerableValue(after, actors, newlyVulnerable);
    }

    internal static double VulnerableValue(KernelSession state, IReadOnlyList<Player> actors,
        IReadOnlySet<Creature>? selectedEnemies = null)
    {
        var enemies = state.Enemies.Where(e => state.Power<VulnerablePower>(e) > 0
            && (selectedEnemies is null || selectedEnemies.Contains(e))).ToArray();
        if (enemies.Length == 0) return 0;
        var players = actors.Where(state.CanAct).ToArray();
        var attacks = players.SelectMany(state.Hand)
            .Where(c => c.Type == CardType.Attack && state.CanPlay(c)).ToList();
        if (attacks.Count == 0) return 0;
        var plain = state.WithoutEnemyVulnerable();
        var energy = players.ToDictionary(p => p, state.Energy);
        var stars = players.ToDictionary(p => p, state.Stars);
        var hp = enemies.ToDictionary(e => e, state.Hp);
        var plainHp = new Dictionary<Creature, int>(hp);
        var block = enemies.ToDictionary(e => e, state.Block);
        var plainBlock = new Dictionary<Creature, int>(block);
        // This projection spends only its local budgets, not simulator state.
        // Cache native cost/damage hooks once instead of re-running them for
        // every greedy comparison of the same hand.
        var costs = attacks.ToDictionary(c => c, state.AttackCosts);
        var targetsByCard = attacks.ToDictionary(c => c, c => state.Targets(c).Select(target => target is null
            ? state.CardTargetType(c) == TargetType.AllEnemies ? enemies : []
            : enemies.Contains(target) ? new[] { target } : []).Where(ts => ts.Length > 0).ToArray());
        var hits = attacks.SelectMany(c => enemies.Select(e => (Card: c, Enemy: e)))
            .ToDictionary(pair => pair, pair => (Boosted: state.AttackDamageHint(pair.Card, pair.Enemy),
                Plain: plain.AttackDamageHint(pair.Card, pair.Enemy)));
        // Allocate each card once, sharing the owner's energy/stars across the
        // whole hand. Greedy marginal damage is bounded by the visible hand;
        // it does not invent draws, recursive plays or future human actions.
        while (attacks.Count > 0)
        {
            CardModel? best = null;
            Creature[] bestTargets = [];
            var bestRank = 0d;
            foreach (var card in attacks)
            {
                var cost = costs[card];
                if (cost.Energy > energy[card.Owner] || cost.Stars > stars[card.Owner]) continue;
                foreach (var targets in targetsByCard[card])
                {
                    var gain = targets.Sum(e => Extra(card, e));
                    var rank = gain / Math.Max(1, cost.Energy + cost.Stars);
                    if (rank <= bestRank) continue;
                    best = card; bestTargets = targets; bestRank = rank;
                }
            }
            if (best is null) break;
            var spent = costs[best];
            energy[best.Owner] -= spent.Energy;
            stars[best.Owner] -= spent.Stars;
            foreach (var e in bestTargets)
            {
                Apply(hits[(best, e)].Boosted, e, hp, block);
                Apply(hits[(best, e)].Plain, e, plainHp, plainBlock);
            }
            attacks.Remove(best);
        }
        return enemies.Sum(e => Math.Max(0, plainHp[e] - hp[e]));

        double Extra(CardModel c, Creature e)
        {
            var boostedLoss = Math.Min(hp[e], Math.Max(0, hits[(c, e)].Boosted - block[e]));
            var plainLoss = Math.Min(plainHp[e], Math.Max(0, hits[(c, e)].Plain - plainBlock[e]));
            return Math.Max(0, boostedLoss - plainLoss);
        }
    }

    private static void Apply(int damage, Creature e, Dictionary<Creature, int> hp, Dictionary<Creature, int> block)
    {
        hp[e] = Math.Max(0, hp[e] - Math.Max(0, damage - block[e]));
        block[e] = Math.Max(0, block[e] - damage);
    }
}

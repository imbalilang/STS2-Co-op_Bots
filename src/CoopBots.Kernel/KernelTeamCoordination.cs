using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
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
        if (card is Flanking or Knockdown or Debilitate)
        {
            var amplified = before.CardBranches(card, target, maximumBranches: 1)
                .FirstOrDefault(b => b.Boundary.Length == 0)?.State;
            return amplified is null ? 0 : AttackGainValue(amplified,
                amplified.WithTeamAmplifiersFrom(before), actors,
                amplified.Enemies.Where(amplified.IsPrimaryEnemy).ToArray());
        }
        // Offensive buffs and Expose's shield/Artifact removal have the same
        // ordering dependency as Vulnerable. Compare native damage, after paying
        // its energy, instead of letting cheap rollouts spend the triggers first.
        if (card is Accuracy or Inflame or Expose)
        {
            var boosted = before.CardBranches(card, target, maximumBranches: 1)
                .FirstOrDefault(b => b.Boundary.Length == 0)?.State;
            return boosted is null ? 0 : AttackGainValue(boosted, before, actors, boosted.Enemies.ToArray());
        }
        if (!card.DynamicVars.Values.OfType<PowerVar<VulnerablePower>>().Any()
            && card.GetType().Name is not ("Uppercut" or "Putrefy" or "Shockwave")) return 0;
        // Resolve the setup itself: its energy is already spent, dead targets
        // have disappeared, and Artifact/blocked debuffs confer no payoff.
        var after = before.CardBranches(card, target, maximumBranches: 1)
            .FirstOrDefault(b => b.Boundary.Length == 0)?.State;
        if (after is null) return 0;
        var increased = after.Enemies.Where(e => after.IsPrimaryEnemy(e) && after.Power<VulnerablePower>(e)
            > before.Power<VulnerablePower>(e)).ToArray();
        // Native damage includes stack-scaled attacks such as Bully, even when
        // the target was already Vulnerable. Ordinary attacks gain no extra
        // multiplier from redundant stacks; their future duration is separate.
        // Hold HP, block, energy and the remaining hand equal: Bash's own hit
        // is already counted as damage, not an additional setup payoff.
        return increased.Length == 0 ? 0 : AttackGainValue(after, after.WithEnemyVulnerableFrom(before), actors, increased);
    }

    internal static double VulnerableValue(KernelSession state, IReadOnlyList<Player> actors,
        IReadOnlySet<Creature>? selectedEnemies = null)
    {
        var enemies = state.Enemies.Where(e => state.IsPrimaryEnemy(e) && state.Power<VulnerablePower>(e) > 0
            && (selectedEnemies is null || selectedEnemies.Contains(e))).ToArray();
        if (enemies.Length == 0) return 0;
        if (!actors.Any(p => state.CanAct(p)
            && state.Hand(p).Any(c => c.Type == CardType.Attack && state.CanPlay(c)))) return 0;
        return AttackGainValue(state, state.WithoutEnemyVulnerable(), actors, enemies);
    }

    internal static double SharedSetupValue(KernelSession state, IReadOnlyList<Player> actors,
        IReadOnlySet<Creature>? selectedEnemies = null)
    {
        if (!state.Enemies.Any(state.HasTeamAmplifier)) return VulnerableValue(state, actors, selectedEnemies);
        var enemies = state.Enemies.Where(e => state.IsPrimaryEnemy(e)
            && (state.Power<VulnerablePower>(e) > 0 || state.HasTeamAmplifier(e))
            && (selectedEnemies is null || selectedEnemies.Contains(e))).ToArray();
        if (enemies.Length == 0 || !actors.Any(p => state.CanAct(p)
            && state.Hand(p).Any(c => c.Type == CardType.Attack && state.CanPlay(c)))) return 0;
        var plain = state.WithTeamAmplifiersFrom(null).WithoutEnemyVulnerable();
        return AttackGainValue(state, plain, actors, enemies);
    }

    private static double AttackGainValue(KernelSession state, KernelSession plain,
        IReadOnlyList<Player> actors, Creature[] enemies)
    {
        if (enemies.Length == 0) return 0;
        var players = actors.Where(state.CanAct).ToArray();
        var attacks = players.SelectMany(state.Hand)
            .Where(c => c.Type == CardType.Attack && state.CanPlay(c)).ToList();
        if (attacks.Count == 0) return 0;

        // Keep every native sampling pass in its original order. The three
        // index dictionaries also keep ToDictionary's duplicate-key failure
        // point: evaluate the repeated selector first, then throw from Add.
        var ownerIndex = new Dictionary<Player, int>(players.Length);
        var energy = new int[players.Length];
        for (var i = 0; i < players.Length; i++)
        {
            energy[i] = state.Energy(players[i]);
            ownerIndex.Add(players[i], i);
        }
        var stars = new int[players.Length];
        for (var i = 0; i < players.Length; i++) stars[i] = state.Stars(players[i]);

        var enemyIndex = new Dictionary<Creature, int>(enemies.Length);
        var hp = new int[enemies.Length];
        for (var i = 0; i < enemies.Length; i++)
        {
            hp[i] = state.Hp(enemies[i]);
            enemyIndex.Add(enemies[i], i);
        }
        var plainHp = new int[enemies.Length];
        for (var i = 0; i < enemies.Length; i++) plainHp[i] = plain.Hp(enemies[i]);
        var block = new int[enemies.Length];
        for (var i = 0; i < enemies.Length; i++) block[i] = state.Block(enemies[i]);
        // Expose's plain and boosted shields remain independent snapshots.
        var plainBlock = new int[enemies.Length];
        for (var i = 0; i < enemies.Length; i++) plainBlock[i] = plain.Block(enemies[i]);

        var cardIndex = new Dictionary<CardModel, int>(attacks.Count);
        var costs = new (int Energy, int Stars)[attacks.Count];
        for (var i = 0; i < attacks.Count; i++)
        {
            costs[i] = state.AttackCosts(attacks[i]);
            cardIndex.Add(attacks[i], i);
        }
        // -1 is the same full enemy-array target group as the old AllEnemies
        // entry. Retain every group, including repeated null/all groups, in
        // native Targets order; do not merge or reorder target alternatives.
        var targetsByCard = new int[attacks.Count][];
        var targetCounts = new int[attacks.Count];
        for (var i = 0; i < attacks.Count; i++)
        {
            var card = attacks[i];
            var nativeTargets = state.Targets(card);
            var options = new int[nativeTargets.Count];
            var count = 0;
            foreach (var target in nativeTargets)
            {
                if (target is null)
                {
                    if (state.CardTargetType(card) == TargetType.AllEnemies)
                        options[count++] = -1;
                }
                else if (enemies.Contains(target))
                    options[count++] = enemyIndex[target];
            }
            targetsByCard[i] = options;
            targetCounts[i] = count;
        }

        var hits = new (int Boosted, int Plain)[checked(attacks.Count * enemies.Length)];
        for (var i = 0; i < attacks.Count; i++)
            for (var e = 0; e < enemies.Length; e++)
                hits[i * enemies.Length + e] = (
                    state.AttackDamageHint(attacks[i], enemies[e]),
                    plain.AttackDamageHint(attacks[i], enemies[e]));

        // Only these local budgets change. Native effects, getter sampling,
        // card removal order and the strict first-best tie break stay intact.
        while (attacks.Count > 0)
        {
            CardModel? best = null;
            var bestTargets = 0;
            var bestRank = 0d;
            foreach (var card in attacks)
            {
                var index = cardIndex[card];
                var cost = costs[index];
                if (cost.Energy > energy[ownerIndex[state.CardOwner(card)]]
                    || cost.Stars > stars[ownerIndex[state.CardOwner(card)]]) continue;
                var options = targetsByCard[index];
                for (var t = 0; t < targetCounts[index]; t++)
                {
                    var target = options[t];
                    double gain = 0;
                    if (target < 0)
                        for (var e = 0; e < enemies.Length; e++) gain += Extra(index, e);
                    else gain += Extra(index, target);
                    var rank = gain / Math.Max(1, cost.Energy + cost.Stars);
                    if (rank <= bestRank) continue;
                    best = card; bestTargets = target; bestRank = rank;
                }
            }
            if (best is null) break;
            var bestIndex = cardIndex[best];
            var spent = costs[bestIndex];
            energy[ownerIndex[state.CardOwner(best)]] -= spent.Energy;
            stars[ownerIndex[state.CardOwner(best)]] -= spent.Stars;
            if (bestTargets < 0)
            {
                for (var e = 0; e < enemies.Length; e++) ApplyBoth(bestIndex, e);
            }
            else ApplyBoth(bestIndex, bestTargets);
            attacks.Remove(best);
        }
        // Enumerable.Sum(int selector) is checked and accumulates in enemy
        // order. Preserve both that overflow behavior and integer arithmetic.
        var total = 0;
        for (var e = 0; e < enemies.Length; e++)
            total = checked(total + Math.Max(0, plainHp[e] - hp[e]));
        return total;

        double Extra(int c, int e)
        {
            var hit = hits[c * enemies.Length + e];
            var boostedLoss = Math.Min(hp[e], Math.Max(0, hit.Boosted - block[e]));
            var plainLoss = Math.Min(plainHp[e], Math.Max(0, hit.Plain - plainBlock[e]));
            return Math.Max(0, boostedLoss - plainLoss);
        }

        void ApplyBoth(int c, int e)
        {
            var hit = hits[c * enemies.Length + e];
            Apply(hit.Boosted, e, hp, block);
            Apply(hit.Plain, e, plainHp, plainBlock);
        }
    }

    private static void Apply(int damage, int e, int[] hp, int[] block)
    {
        hp[e] = Math.Max(0, hp[e] - Math.Max(0, damage - block[e]));
        block[e] = Math.Max(0, block[e] - damage);
    }
}

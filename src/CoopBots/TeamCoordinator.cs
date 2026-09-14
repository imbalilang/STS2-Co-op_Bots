using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots;

// Reactive selector tree: rescue -> joint plan -> unsupported-card fallback -> wait.
// The selected branch owns the action. Individual scores cannot override a joint plan.
internal static class TeamCoordinator
{
    internal sealed record Decision(Player? Player, BotBrain.CombatMove? Move, BotPotionPlanner.Choice? Potion, string Branch);

    internal static Decision Select(TeamCombatPlanner.Decision? team, BotPotionPlanner.Choice? potion,
        IReadOnlyList<(Player Player, BotBrain.CombatMove Move)> fallback, bool planningFailed = false)
    {
        // A preemptive bottle is one the cards cannot replace (a kill nothing else
        // on the board can make), so a card plan must not bury it: throwing it
        // first is worth more than any single play.
        if (potion is not null && (team is null || potion.Preemptive || potion.SavedLives > team.DeathsPrevented
            || (team.DeathsPrevented <= 0 && potion.SavedLives == 0)))
            return new(null, null, potion, potion.Preemptive ? "主动用药" : "救援药水");
        if (team is not null) return new(team.Player, team.Move, null, "团队规划");
        var best = fallback.Where(c => c.Move.Score > 0 && (planningFailed || TeamCombatPlanner.NeedsEffectFallback(c.Move.Card)))
            .OrderByDescending(c => c.Move.Score).ThenBy(c => c.Player.NetId).FirstOrDefault();
        return best.Player is null ? new(null, null, null, "等待配合") : new(best.Player, best.Move, null, "特殊牌回退");
    }

    internal static BotBrain.CombatMove? Advise(Player human, IReadOnlyList<Player> party)
    {
        var participants = new[] { human }.Concat(party.Where(p => BotRegistry.IsBot(p.NetId)
            && p.Creature.IsAlive && !CombatManager.Instance.IsPlayerReadyToEndTurn(p))).ToList();
        return TeamCombatPlanner.ChooseCore(participants, party, BotCooperation.FocusTarget, human)?.Move;
    }

    internal static (double Score, string Reason) CardValue(CardModel card, Player player)
    {
        if (card.Type is CardType.Curse or CardType.Status) return (-1000, "避免主动拿入诅咒或状态牌");
        var baseline = HumanCoopAdvisor.CardValue(card, player);
        var bonus = TeamBonus(card, player);
        if (bonus <= 0) return baseline;
        var why = card.TargetType == TargetType.AnyAlly
            ? (player.Deck.Cards.Any(c => c.Id == card.Id) ? "已有同类援护牌，降低重复投入" : "按团队分工补充援护；保留个人防御与输出")
            : "为全队补充易伤／虚弱铺垫";
        return (baseline.Score + bonus, why);
    }

    /// <summary>
    /// What a card is worth to the party beyond its own line: an ally-targeted
    /// support card, or a team-wide debuff setup. Shared with the unified build
    /// valuation so a reward and a shop purchase agree on the same card.
    /// </summary>
    internal static double TeamBonus(CardModel card, Player player)
    {
        var party = player.RunState.Players;
        if (party.Count < 2) return 0;
        // Rewards can resolve concurrently on peers. Use immutable roster roles and
        // the recipient's own deck, not another bot's potentially in-flight reward.
        var teamCopies = player.Deck.Cards.Count(c => c.Id == card.Id);
        if (card.TargetType == TargetType.AnyAlly)
        {
            var supportId = party.Where(p => BotRegistry.IsBot(p.NetId)).OrderBy(p => p.NetId).FirstOrDefault()?.NetId;
            var supportRole = !BotRegistry.IsBot(player.NetId) || player.NetId == supportId;
            var ownSupport = player.Deck.Cards.Count(c => c.TargetType == TargetType.AnyAlly);
            return (supportRole ? 26.0 : 10.0) / (1 + teamCopies) - ownSupport * 5;
        }
        if (card.GetType().Name is "Shockwave" or "Uppercut" or "LegSweep" or "Bash")
            return 15.0 / (1 + teamCopies);
        return 0;
    }

    // Exhaustive small assignment (at most four seats), locking every existing vote.
    // Skip is legal; no two newly assigned bots compete for the same relic.
    internal static Dictionary<ulong, int?> AssignRelics(IReadOnlyList<Player> bots, IReadOnlyList<RelicModel> relics,
        IReadOnlySet<int> reserved)
    {
        var ordered = bots.OrderBy(p => p.NetId).ToList();
        var current = new Dictionary<ulong, int?>();
        var best = new Dictionary<ulong, int?>();
        var used = new HashSet<int>(reserved);
        double bestScore = double.NegativeInfinity;
        void Search(int seat, double score)
        {
            if (seat == ordered.Count)
            {
                if (score > bestScore) { bestScore = score; best = new(current); }
                return;
            }
            var player = ordered[seat];
            for (var i = 0; i < relics.Count; i++)
            {
                if (!used.Add(i)) continue;
                current[player.NetId] = i;
                // Unknown relics get neutral utility rather than a fabricated synergy.
                Search(seat + 1, score + 1 + HumanCoopAdvisor.RelicValue(relics[i], player).Score);
                used.Remove(i);
            }
            current[player.NetId] = null; Search(seat + 1, score);
        }
        Search(0, 0);
        return best;
    }
}

using CoopBots.Building;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

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

    // How much bigger than the card's own contribution its team bonus may be.
    // Stated as a share rather than an absolute so it self-normalises: a strong
    // card can carry a large bonus, a card the deck has no use for carries none.
    private const double TeamBonusShare = 1.5;

    internal static (double Score, string Reason) CardValue(CardModel card, Player player)
    {
        if (card.Type is CardType.Curse or CardType.Status) return (-1000, "避免主动拿入诅咒或状态牌");
        var baseline = HumanCoopAdvisor.CardValue(card, player);
        var bonus = TeamBonus(card, player);
        // A team bonus may not carry a card on its own. The reviewed run took two
        // CONCOCT on `team-fit` alone into a deck that could not support them,
        // while multiplayer-only-card decks lose more often than they win. The
        // bonus still counts — it just cannot outvote the card's own merit by more
        // than this share, and a card the deck has no use for gets nothing.
        var ceiling = Math.Max(0, baseline.Score) * TeamBonusShare;
        if (bonus > ceiling) bonus = ceiling;
        if (bonus <= 0) return baseline;
        var why = card.TargetType == TargetType.AnyAlly
            ? (player.Deck.Cards.Any(c => c.Id == card.Id) ? "已有同类援护牌，降低重复投入" : "按团队分工补充援护；保留个人防御与输出")
            : "为全队补充易伤／虚弱铺垫";
        return (baseline.Score + bonus, why);
    }

    // A team bonus is a bounded heuristic, not a support-strength model. This is
    // only the absolute ceiling; it is not sufficient on its own to stop the
    // bonus carrying a card the deck has no use for. CardValue additionally caps
    // it as a share of the card's own baseline, and BuildValue applies its own
    // modest share of the card's pre-team contribution (a different scale than
    // HumanCoopAdvisor, so its 1.5x is deliberately not copied there).
    private const double MaxTeamBonus = 18.0;
    // A modeled ally support that answers real demand, versus none at all.
    private const double SupportWithDemand = 12.0;
    private const double SupportNoDemand = 4.0;
    // A missing party-wide debuff setup versus one the party already holds.
    private const double DebuffWhenMissing = 14.0;
    private const double DebuffWhenCovered = 3.0;
    // An AnyAlly card whose effect the profile cannot read is neutral: there is
    // no modelled benefit to price. The reviewed run drafted two Concoct on
    // `team-fit` alone into a deck that could not support them; an unmodeled
    // ally power is not evidence of a team payoff, so it earns nothing.
    private const double UnknownAllySupport = 0.0;

    /// <summary>
    /// What a card is worth to the party beyond its own line: an ally-targeted
    /// support card, or a debuff setup the whole party can use. Shared with the
    /// unified build valuation so a reward and a shop purchase agree on the same
    /// card.
    ///
    /// Rewards resolve concurrently on every peer with no host authority, so the
    /// teammate side of this comes only from the combat-start snapshot in
    /// <see cref="TeamBuildingContext"/>; a missing or stale snapshot degrades to
    /// an own-deck/roster-only conservative bonus and never rereads a teammate's
    /// mutable deck.
    /// </summary>
    internal static double TeamBonus(CardModel card, Player player) => TeamBonus(card, player, null, null);

    internal static double TeamBonus(CardModel card, Player player, IReadOnlyList<CardModel>? ownDeckOverride,
        DeckStructure.Summary? ownSummaryOverride)
    {
        IRunState run;
        IReadOnlyList<Player> party;
        try
        {
            run = player.RunState;
            party = run.Players;
        }
        catch { return 0; }
        if (party.Count < 2) return 0;

        CardProfile.Facts facts;
        try { facts = CardProfile.Of(card); }
        catch { return 0; }
        if (facts.Unplayable || card.Type is CardType.Curse or CardType.Status) return 0;

        IReadOnlyList<CardModel> ownDeck;
        try { ownDeck = ownDeckOverride ?? player.Deck.Cards.ToList(); }
        catch { return 0; }
        var own = ownSummaryOverride ?? DeckStructure.Build(ownDeck, BuildValue.StableEnergy(player));
        var ownSupport = ownDeck.Count(c => c.TargetType == TargetType.AnyAlly);
        var ownCopies = ownDeck.Count(c => c.Id == card.Id);

        var bonus = card.TargetType == TargetType.AnyAlly
            ? AllySupportBonus(card, facts, run, player, party, own, ownSupport, ownCopies)
            : DebuffBonus(card, facts, run, player, party, own, ownCopies);
        return Math.Clamp(bonus, 0, MaxTeamBonus);
    }

    private static double AllySupportBonus(CardModel card, CardProfile.Facts facts, IRunState run,
        Player player, IReadOnlyList<Player> party, DeckStructure.Summary own,
        int ownSupport, int ownCopies)
    {
        // Which ally benefits depends on what the support actually does, from
        // reliable card facts only. An AnyAlly card with no modeled effect is
        // neutral. No AllEnemies requirement: a single-target debuff still helps
        // the party.
        var guards = card.GainsBlock || facts.Block > 0;
        var accelerates = facts.Draw > 0 || facts.Energy > 0;
        var buffs = facts.Scaling > 0 || facts.Poison > 0 || facts.Doom > 0
            || facts.Vulnerable > 0 || facts.Weak > 0 || facts.StrengthDown > 0;
        if (!guards && !accelerates && !buffs) return UnknownAllySupport;

        var defenseDemand = 0;
        var offenseDemand = 0;
        var engineDemand = 0;
        var teammateSupport = 0;
        var unknownAllies = 0;
        var partyAttacks = own.Attacks;
        foreach (var mate in party)
        {
            if (mate.NetId == player.NetId) continue;
            // A missing snapshot is "an ally exists", never a live deck read.
            var snap = TeamBuildingContext.TryGet(run, mate.NetId);
            if (snap is null)
            {
                unknownAllies++;
                partyAttacks += 1;
                continue;
            }
            teammateSupport += snap.Supports;
            partyAttacks += snap.Attacks;
            if (snap.Blocks < Math.Max(3, snap.Attacks / 2)) defenseDemand++;
            if (snap.Attacks < 5) offenseDemand++;
            if (snap.Draws == 0 && snap.Energies == 0) engineDemand++;
        }

        var need = guards
            ? defenseDemand > 0 || unknownAllies > 0
            : accelerates
                ? engineDemand > 0 || unknownAllies > 0
                : offenseDemand > 0 || unknownAllies > 0 || own.Attacks >= 5;
        var baseBonus = need ? SupportWithDemand : SupportNoDemand;
        // Duplicate copies and existing party support both pay less: a weak deck
        // must not stack identical support it cannot cash.
        var redundancy = ownSupport + teammateSupport;
        var bonus = baseBonus / (1 + 0.5 * redundancy + ownCopies);
        // Preserve a minimum of the player's own offence and defence: a deck
        // that has not reached those floors drafts its own line before pure
        // support even when an ally could use the help. A useful offensive
        // player may still accept support when the demand is real.
        if (facts.Damage <= 0 && own.Attacks < 5) bonus *= 0.4;
        if (!guards && own.Blocks < Math.Max(3, own.Attacks / 2)) bonus *= 0.7;
        return bonus;
    }

    // Debuff channels are scored independently. Weak coverage is not Vulnerable
    // coverage, and a saturated channel must not suppress a missing one. Each
    // present channel uses the party's count of *that* role, and only
    // enemy-targeted debuffs are credited: a self/ally Weak or Vulnerable
    // drawback is not a shared benefit.
    private static double DebuffBonus(CardModel card, CardProfile.Facts facts, IRunState run, Player player,
        IReadOnlyList<Player> party, DeckStructure.Summary own, int ownCopies)
    {
        if (facts.Vulnerable <= 0 && facts.Weak <= 0 && facts.StrengthDown <= 0) return 0;
        if (!TargetsEnemies(card)) return 0;

        var attacks = own.Attacks;
        foreach (var mate in party)
        {
            if (mate.NetId == player.NetId) continue;
            var snap = TeamBuildingContext.TryGet(run, mate.NetId);
            if (snap is null) { attacks += 1; continue; }
            attacks += snap.Attacks;
        }

        var bonus = 0.0;
        bonus += DebuffChannel("vulnerable", facts.Vulnerable, run, player, party, own, ownCopies, attacks);
        bonus += DebuffChannel("weak", facts.Weak, run, player, party, own, ownCopies, attacks);
        bonus += DebuffChannel("strengthdown", facts.StrengthDown, run, player, party, own, ownCopies, attacks);
        return bonus;
    }

    // One debuff channel: missing coverage is worth more than one more copy of a
    // saturated setup, and the channel's own count drives the falloff. Missing
    // snapshot evidence adds no coverage (unknown is not "covered").
    private static double DebuffChannel(string role, double stacks, IRunState run, Player player,
        IReadOnlyList<Player> party, DeckStructure.Summary own, int ownCopies, int partyAttacks)
    {
        if (stacks <= 0) return 0;
        var coverage = own.RoleCounts.GetValueOrDefault(role);
        foreach (var mate in party)
        {
            if (mate.NetId == player.NetId) continue;
            var snap = TeamBuildingContext.TryGet(run, mate.NetId);
            if (snap is null) continue;
            coverage += snap.RoleCounts.GetValueOrDefault(role);
        }
        var baseBonus = coverage == 0 ? DebuffWhenMissing : DebuffWhenCovered;
        var bonus = baseBonus / (1 + 0.4 * coverage + ownCopies);
        // Vulnerable only pays when the party has attacks to exploit it.
        if (role == "vulnerable" && partyAttacks <= 0) bonus *= 0.5;
        return bonus;
    }

    // Debuffs that land on an enemy are shared setup; a debuff applied to self or
    // an ally is a downside and gets no team credit. Unknown/missing target data
    // is conservative (no credit).
    private static bool TargetsEnemies(CardModel card)
    {
        try
        {
            return card.TargetType is TargetType.AnyEnemy or TargetType.AllEnemies or TargetType.RandomEnemy;
        }
        catch
        {
            return false;
        }
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

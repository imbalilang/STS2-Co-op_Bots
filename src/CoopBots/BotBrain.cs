using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots;

public static class BotBrain
{
    public readonly record struct CombatMove(CardModel Card, Creature? Target, double Score, string Reason = "basic",
        IReadOnlyList<CoopBots.Kernel.KernelChoice>? Choices = null);

    public static CombatMove? ChooseCombatMove(Player player, int actionNumber)
    {
        var difficulty = BotRegistry.Difficulty(player.NetId);
        var moves = LegalCombatMoves(player, difficulty);

        if (moves.Count == 0)
            return null;

        if (difficulty == BotDifficulty.Dumb)
        {
            var combat = player.PlayerCombatState!;
            var key = $"combat:{player.NetId}:{combat.TurnNumber}:{actionNumber}:{moves.Count}";
            return moves[StableIndex(key, moves.Count)];
        }

        if (difficulty == BotDifficulty.Genius)
            return GeniusCombatStrategy.Choose(player, moves, actionNumber);

        return moves
            .OrderByDescending(move => move.Score)
            .ThenBy(move => move.Card.Id.Entry, StringComparer.Ordinal)
            .ThenBy(move => move.Target?.CombatId ?? uint.MaxValue)
            .First();
    }

    // One-action fallback for effects outside the team projection.
    internal static CombatMove? ChooseEffectFallback(Player player, int actionNumber)
    {
        var legal = LegalCombatMoves(player).Where(move => TeamCombatPlanner.NeedsEffectFallback(move.Card)).ToList();
        // A fallback is a one-action heuristic, not a rescue certificate.
        // Avoid speculative spending when any teammate already faces lethal damage.
        if (legal.Count == 0 || player.RunState.Players.Any(p => p.Creature.IsAlive && CombatAssessment.InDanger(p.Creature)))
            return null;
        var scored = GeniusCombatStrategy.ScoreLegalMoves(player, legal, actionNumber);
        return scored.Where(move => move.Score > 0
                && GeniusCombatStrategy.Analyze(move.Card, move.Target, player.PlayerCombatState!.Energy,
                    player.Creature.CombatState!.HittableEnemies.Count()).HpLoss < player.Creature.CurrentHp)
            .Select(move => (CombatMove?)move).FirstOrDefault();
    }

    // The live policy requires full legality. Search may also retain cards blocked
    // only by resources; its root action still requires full live legality.
    internal static List<CombatMove> LegalCombatMoves(Player player, BotDifficulty? difficultyOverride = null)
        => PlanningCombatMoves(player, difficultyOverride);

    internal static List<CombatMove> PlanningCombatMoves(Player player, BotDifficulty? difficultyOverride = null,
        bool allowFutureResources = false)
    {
        var state = player.Creature.CombatState;
        var combat = player.PlayerCombatState;
        if (state is null || combat is null)
            return new List<CombatMove>();

        var difficulty = difficultyOverride ?? BotRegistry.Difficulty(player.NetId);
        var moves = new List<CombatMove>();
        foreach (var card in combat.Hand.Cards.Where(card =>
        {
            if (card.CanPlay(out var reason, out _)) return true;
            return allowFutureResources && (reason & ~(UnplayableReason.EnergyCostTooHigh
                | UnplayableReason.StarCostTooHigh)) == UnplayableReason.None;
        }))
        {
            var candidates = state.Creatures.Where(card.IsValidTarget).ToList();
            if (card.IsValidTarget(null))
                candidates.Add(null!);

            foreach (var target in candidates.Cast<Creature?>())
            {
                if (allowFutureResources || card.CanPlayTargeting(target))
                    moves.Add(new CombatMove(card, target, ScoreCombatCard(card, target, difficulty)));
            }
        }

        return moves;
    }

    public static bool ShouldEndTurnEarly(Player player, int actionNumber)
    {
        if (BotRegistry.Difficulty(player.NetId) != BotDifficulty.Dumb)
            return false;

        var turn = player.PlayerCombatState?.TurnNumber ?? 0;
        var roll = StableIndex($"end:{player.NetId}:{turn}:{actionNumber}", 100);
        return roll < (actionNumber == 0 ? 18 : 38);
    }

    // The native entry points that permanently edit the deck. `FromDeckForUpgrade`
    // and `FromHandForUpgrade` are different things: the hand one upgrades a card
    // for the current combat only.
    private static IReadOnlyList<CardModel>? Permanent(
        Player player, List<CardModel> list, int count, string purpose)
    {
        if (!purpose.Contains("FromDeck", StringComparison.OrdinalIgnoreCase)) return null;
        Func<CardModel, double> score;
        var worst = false;
        if (purpose.Contains("ForUpgrade", StringComparison.OrdinalIgnoreCase))
            score = card => Building.BuildValue.UpgradeDelta(card, player).Total;
        else if (purpose.Contains("ForRemoval", StringComparison.OrdinalIgnoreCase))
            score = card => Building.BuildValue.Remove(card, player).Total;
        else if (purpose.Contains("ForTransformation", StringComparison.OrdinalIgnoreCase))
        {
            // A transform should target the card contributing the least.
            score = card => Building.BuildValue.Marginal(card, player, list).Total;
            worst = true;
        }
        else return null;
        var ordered = worst
            ? list.OrderBy(score).ThenBy(card => card.Id.Entry, StringComparer.Ordinal)
            : list.OrderByDescending(score).ThenBy(card => card.Id.Entry, StringComparer.Ordinal);
        return ordered.Take(count).ToList();
    }

    public static IReadOnlyList<CardModel> SelectCards(
        Player player,
        IEnumerable<CardModel> cards,
        int min,
        int max,
        string purpose)
    {
        var list = cards.Distinct().ToList();
        if (list.Count == 0)
            return Array.Empty<CardModel>();

        var count = Math.Clamp(Math.Max(0, min), 0, Math.Min(Math.Max(min, max), list.Count));
        if (count == 0 && purpose.Contains("ChooseA", StringComparison.OrdinalIgnoreCase))
            count = 1;

        var difficulty = BotRegistry.Difficulty(player.NetId);
        if (difficulty == BotDifficulty.Dumb)
        {
            return list
                .OrderBy(card => StableHash($"choice:{player.NetId}:{purpose}:{card.Id.Entry}"))
                .Take(count)
                .ToList();
        }

        // Permanent deck edits are decided by the same valuation the reward screen
        // and the shop use, so "is this worth doing" and "which card" cannot
        // disagree. Hand-based variants are combat effects and keep their own
        // scoring; the combat plan already has first refusal through
        // BotChoicePlanSync.
        if (Permanent(player, list, count, purpose) is { } permanent) return permanent;

        var preferWeak = purpose.Contains("Discard", StringComparison.OrdinalIgnoreCase)
                         || purpose.Contains("Removal", StringComparison.OrdinalIgnoreCase)
                         || purpose.Contains("Transform", StringComparison.OrdinalIgnoreCase);
        return (preferWeak
                ? list.OrderBy(card => ScoreDeckCard(card, difficulty))
                : list.OrderByDescending(card => ScoreDeckCard(card, difficulty)))
            .ThenBy(card => card.Id.Entry, StringComparer.Ordinal)
            .Take(count)
            .ToList();
    }

    public static int ChooseCardReward(Player player, IReadOnlyList<CardModel> cards)
    {
        if (cards.Count == 0)
            return -1;
        var difficulty = BotRegistry.Difficulty(player.NetId);
        if (difficulty == BotDifficulty.Dumb)
            return StableIndex($"reward:{player.NetId}:{player.Deck.Cards.Count}:{cards.Count}", cards.Count + 1) - 1;

        // Every candidate is compared against the deck as it stands, so skipping
        // is a real candidate rather than a fallback: a card that takes more
        // draws from the deck than it contributes is worth less than nothing.
        // Only Smart and above act on that; the lower difficulties still rank the
        // candidates and take the best one.
        return Building.BuildValue.BestReward(player, cards, allowSkip: difficulty >= BotDifficulty.Smart);
    }

    public static int StableIndex(string key, int count)
        => count <= 0 ? 0 : (int)(StableHash(key) % (uint)count);

    private static uint StableHash(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }
        return hash;
    }

    private static double ScoreCombatCard(CardModel card, Creature? target, BotDifficulty difficulty)
    {
        var damage = card.DynamicVars.Values.OfType<DamageVar>().Sum(variable => (double)variable.BaseValue);
        var block = card.DynamicVars.Values.OfType<BlockVar>().Sum(variable => (double)variable.BaseValue);
        var cost = Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All));
        var score = card.Type switch
        {
            CardType.Attack => 7.0 + damage,
            CardType.Skill => 5.0 + block * 0.9,
            CardType.Power => 11.0,
            CardType.Curse => -100.0,
            CardType.Status => -40.0,
            _ => 2.0,
        };

        if (difficulty >= BotDifficulty.Smart)
        {
            score -= cost * 2.2;
            if (cost == 0)
                score += 2.5;
            if (card.GainsBlock && card.Owner.Creature.Block < 12)
                score += block * 0.5;
            if (target?.IsEnemy == true)
                score += 10.0 / Math.Max(1, target.CurrentHp + target.Block);
        }

        if (difficulty == BotDifficulty.Genius)
        {
            score += card.Type == CardType.Power ? 5.0 : 0.0;
            if (target?.IsEnemy == true && damage >= target.CurrentHp + target.Block)
                score += 80.0;
            if (card.TargetType == TargetType.AllEnemies)
                score += damage * Math.Max(0, card.Owner.Creature.CombatState!.Enemies.Count - 1);
        }

        return score;
    }

    private static double ScoreDeckCard(CardModel card, BotDifficulty difficulty)
    {
        var damage = card.DynamicVars.Values.OfType<DamageVar>().Sum(variable => (double)variable.BaseValue);
        var block = card.DynamicVars.Values.OfType<BlockVar>().Sum(variable => (double)variable.BaseValue);
        var rarity = card.Rarity switch
        {
            CardRarity.Rare => 9.0,
            CardRarity.Uncommon => 4.0,
            CardRarity.Curse => -50.0,
            CardRarity.Status => -30.0,
            _ => 0.0,
        };
        var type = card.Type switch
        {
            CardType.Attack => 3.0,
            CardType.Skill => 2.5,
            CardType.Power => 5.0,
            CardType.Curse => -30.0,
            CardType.Status => -20.0,
            _ => 0.0,
        };
        var value = rarity + type + damage * 0.25 + block * 0.25;
        if (difficulty >= BotDifficulty.Smart)
        {
            var cost = Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.Local));
            value -= cost * 1.25;
            if (cost == 0)
                value += 1.5;
        }
        if (card.IsUpgraded)
            value += 3.0;
        return value;
    }
}

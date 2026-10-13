using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

// Upstream has no permanent-deck forecast. Owner 2026-09-30: removal also
// changes the draw quality of later rewards/upgrades. Use the same public
// character pool in every counterfactual, never future map rewards or live RNG.
internal static class DeckGrowth
{
    internal const double ForecastWeight = .25;
    internal static double Weight(Player player)
    {
        // No speculative growth at the end of the run. Imminent survival keeps
        // its weight when the team is wounded, just as in InvestmentHorizon.
        var fights = RunDepth.RemainingCombats(player);
        var hp = player.RunState.Players.Where(p => p.Creature.IsAlive)
            .Select(p => (double)p.Creature.CurrentHp / Math.Max(1, p.Creature.MaxHp)).DefaultIfEmpty(1).Min();
        return WeightFor(fights, hp);
    }
    internal static double WeightFor(double fights, double hp)
        => ForecastWeight * Math.Clamp((fights - 2) / 10, 0, 1) * Math.Clamp(hp, 0, 1);

    internal static IReadOnlyList<CardModel[]> Forecasts(IReadOnlyList<CardModel> deck, Player player, int energy)
    {
        var character = player.Character;
        // Deprived is used by our native test harness; infer its fixture pool
        // from the live reference deck, never the hypothetical edited deck.
        if (character.Id.Entry == "DEPRIVED")
            character = ModelDb.AllCharacters.Where(c => c.Id.Entry != "DEPRIVED")
                .OrderByDescending(c => c.StartingDeck.Count(s => player.Deck.Cards.Any(d => d.Id == s.Id))).FirstOrDefault() ?? character;
        var pool = character.CardPool.AllCards.Where(c => c.Rarity is CardRarity.Common or CardRarity.Uncommon)
            .Where(c => c.Type != CardType.Power && BuildCoverage.Of(c) > 0)
            .Select(c => { var clone = c.ToMutable(); clone.Owner = player; return clone; })
            .Select(c => (Card: c, F: CardProfile.Of(c, energy)))
            .Where(c => !c.F.Unplayable && c.F.Cost <= energy && !c.Card.EnergyCost.CostsX)
            .OrderByDescending(c => BakedCardElo.TryGet(c.Card.Id.Entry, out var elo) ? elo : BakedCardElo.SkipElo)
            .ThenBy(c => c.Card.Id.Entry, StringComparer.Ordinal).ToArray();
        if (pool.Length == 0) return [];
        var attack = pool.Where(c => c.F.Damage > 0).Take(3).Select(c => c.Card).ToArray();
        var defend = pool.Where(c => c.F.Block > 0).Take(3).Select(c => c.Card).ToArray();
        var draw = pool.Where(c => c.F.Draw > 0 || c.F.Energy > 0).Take(3).Select(c => c.Card).ToArray();
        var forecasts = new List<CardModel[]>();
        for (var i = 0; i < 3; i++)
        {
            // Four later ordinary rewards, covering offense, defence and draw.
            // Pool preference is a disclosed community prior, not a prediction
            // that the run will receive these exact cards.
            var additions = new List<CardModel>();
            foreach (var category in new[] { attack, defend, draw, pool.Take(3).Select(c => c.Card).ToArray() })
                if (category.Length > 0)
                {
                    var clone = ModelDb.AllCards.First(c => c.Id == category[i % category.Length].Id).ToMutable(); clone.Owner = player;
                    additions.Add(clone);
                }
            var grown = deck.Concat(additions).ToArray();
            // One available later upgrade; both alternatives use the same
            // public selection policy and native upgrade operation.
            var target = grown.Where(c => c.IsUpgradable && c.Rarity != CardRarity.Basic)
                .OrderByDescending(c => BakedCardElo.TryGet(c.Id.Entry, out var elo) ? elo : BakedCardElo.SkipElo)
                .ThenBy(c => c.Id.Entry, StringComparer.Ordinal).FirstOrDefault();
            if (target is not null && CardProfile.Upgraded(target, energy) is { } upgraded)
                grown[Array.IndexOf(grown, target)] = upgraded.Card;
            forecasts.Add(grown.OrderBy(c => c.Id.Entry, StringComparer.Ordinal).ThenBy(c => c.CurrentUpgradeLevel).ToArray());
        }
        return forecasts;
    }
}

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Encounters;

namespace CoopBots.Building;

// Construction only. This expectation estimates the extra survival supplied by
// cleanup against the VISIBLE boss; it never taxes a combat action or reads the
// hidden second boss / shuffled draw order. It is not a native win forecast.
internal static class BossPreparation
{
    internal static double Strength(Player player, IReadOnlyList<CardModel> deck, int energy)
        => !AgainstWither(player) || deck.Count == 0 ? 0
            : -6 * new[] { 6, 9 }.Average(turns => Burden(deck, energy, Plays(deck, energy), turns) / turns);

    internal static bool AgainstWither(Player player)
    {
        try { return player.RunState.Act.BossEncounter is AeonglassBoss; }
        catch { return false; } // No known boss in a detached card/test context.
    }

    internal static bool Cleans(CardModel c) => c is Prepared or Survivor or Acrobatics or DaggerThrow
        or CalculatedGamble or ToolsOfTheTrade or TrueGrit or Cinder or BurningPact or SecondWind or FiendFire or Purity;
    private static bool Burns(CardModel c) => c is TrueGrit or Cinder or BurningPact or SecondWind or FiendFire or Purity;
    private static double Amount(CardModel c, string key, double fallback = 1)
        => c.DynamicVars.TryGetValue(key, out var v) ? (double)v.BaseValue : fallback;

    internal static double Change(Player player, IReadOnlyList<CardModel> deck,
        CardModel? added, CardModel? removed, int energy)
    {
        if (!AgainstWither(player) || deck.Count < 8 || !(added is not null && Cleans(added)
            || removed is not null && Cleans(removed))) return 0;
        var after = deck.Where(c => !ReferenceEquals(c, removed)).Concat(added is null ? [] : [added]).ToArray();
        // Paired alternatives face the SAME production rate. Adding a damage
        // card cannot incur a new pollution penalty or discourage a kill race.
        var plays = Plays(deck, energy);
        var delta = new[] { 6, 9 }.Average(turns =>
            (Burden(deck, energy, plays, turns) - Burden(after, energy, plays, turns)) / turns);
        return delta * Math.Max(1, after.Length / 5d) * 6;
    }

    private static double Plays(IReadOnlyList<CardModel> deck, int energy)
    {
        var f = deck.Select(c => CardProfile.Of(c, energy)).Where(c => !c.Unplayable).ToArray();
        var paid = f.Where(c => c.Cost > 0).ToArray();
        var draw = Math.Min(10, 5 + f.Sum(c => c.Draw) * 5 / deck.Count);
        var free = f.Count(c => c.Cost == 0) * draw / deck.Count;
        var ordinary = free + Math.Min(Math.Max(0, draw - free), energy / Math.Max(1, paid.Select(c => (double)c.Cost).DefaultIfEmpty(1).Average()));
        return Math.Clamp(ordinary + f.Sum(c => c.Effects.GeneratedShivs) * 5 / deck.Count
            + deck.Count(c => c is InfiniteBlades) * .75, 1, 12);
    }

    // Native rules: every sixth play puts a Wither IN HAND; every third enemy
    // turn adds two to discard at A10 and raises all Wither damage by three.
    // Discard only clears this hand; exhaust also reduces the future stock.
    // Fractions are expected availability, not a claim about a concrete draw.
    internal static double Burden(IReadOnlyList<CardModel> deck, int energy, double plays, int turns)
    {
        var cleaners = deck.Where(Cleans).Select(c => (Card: c, Facts: CardProfile.Of(c, energy)))
            .OrderByDescending(e => Burns(e.Card)).ThenBy(e => e.Facts.Cost).ToArray();
        var used = new Dictionary<CardModel, double>();
        var netDraw = deck.Sum(c => CardProfile.NetDraw(c, CardProfile.Of(c, energy)));
        double stock = 0, damage = 0;
        for (var turn = 1; turn <= turns; turn++)
        {
            var size = Math.Max(5, deck.Count + stock);
            var draw = Math.Min(10, 5 + netDraw * 5 / size);
            var arrival = plays / 6;
            var drawnBad = stock * draw / size;
            var inHand = drawnBad + arrival;
            double burned = 0, cleanupEnergy = 0, recoveredDraw = 0;
            foreach (var (card, facts) in cleaners)
            {
                if (facts.Cost > energy) continue;
                var chance = Math.Min(1, draw / size);
                if (card is ToolsOfTheTrade)
                {
                    // Its discard happens at turn start, before this turn's
                    // played-card Withers exist, and after the setup turn.
                    chance = Math.Min(1, (turn - 1) * draw / size);
                    var clear = Math.Min(Math.Min(drawnBad, inHand), chance);
                    inHand -= clear; drawnBad = Math.Max(0, drawnBad - clear);
                    continue;
                }
                if (card.Keywords.Contains(CardKeyword.Exhaust)) chance *= 1 - used.GetValueOrDefault(card);
                if (facts.Cost > 0) chance = Math.Min(chance, Math.Max(0, energy - cleanupEnergy) / facts.Cost);
                cleanupEnergy += chance * facts.Cost;
                used[card] = Math.Min(1, used.GetValueOrDefault(card) + chance);
                // Cleanup's own sixth-play trigger fires AFTER its OnPlay.
                var available = Math.Max(0, inHand - 1d / 6);
                var count = card switch
                {
                    SecondWind or FiendFire or CalculatedGamble => available,
                    Prepared or Purity => Math.Min(available, Amount(card, "Cards")),
                    Cinder => available / Math.Max(1, draw - 1),
                    TrueGrit when !card.IsUpgraded => available / Math.Max(1, draw - 1),
                    _ => Math.Min(1, available),
                };
                var cleaned = Math.Min(available, count * chance);
                inHand -= cleaned;
                if (Burns(card)) burned += cleaned;
                // The ordinary hand model charges a useful card for mandatory
                // discard/exhaust. Paying that cost with Wither preserves a
                // useful draw instead. Use the same four-point draw unit as
                // BuildValue, limited by remaining energy to use the card.
                if (card is Prepared or Acrobatics or DaggerThrow or BurningPact)
                    recoveredDraw += cleaned;
            }
            damage += inHand * (3 + 3 * ((turn - 1) / 3))
                - 4 * Math.Min(recoveredDraw, Math.Max(0, energy - cleanupEnergy));
            stock = Math.Max(0, stock + arrival - burned) + (turn % 3 == 0 ? 2 : 0);
        }
        return damage;
    }
}

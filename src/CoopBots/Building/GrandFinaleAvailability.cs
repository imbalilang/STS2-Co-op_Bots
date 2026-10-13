using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CoopBots.Building;

// Construction evidence only: sample public deck permutations, never the live
// draw order or RNG. Require a playable window in the FIRST deck cycle: an
// expensive purchase cannot rely on a coincidental later reshuffle alignment.
// Grand Finale requires an EMPTY draw pile, not merely a small deck. Drawing
// past zero reshuffles before the card becomes playable and misses the window.
internal static class GrandFinaleAvailability
{
    internal static double Estimate(IReadOnlyList<CardModel> deck, int energy)
    {
        if (!deck.Any(c => c is GrandFinale)) return 1;
        var facts = deck.ToDictionary(c => c, c => CardProfile.Of(c, energy));
        var openingDraw = deck.FirstOrDefault()?.Owner?.Relics
            .Where(r => r.GetType().Name is "RingOfTheSnake" or "BagOfPreparation")
            .Sum(r => r.DynamicVars.TryGetValue("Cards", out var count) ? (int)count.BaseValue : 0) ?? 0;
        double seen = 0, windows = 0;
        for (var sample = 0; sample < 24; sample++)
        {
            var random = new Random(7927 + sample * 104729);
            // An upgrade replaces a model instance and may move it to the end
            // of a comparison deck. Stable card identity keeps that bookkeeping
            // change from changing the sampled draw orders and the result.
            var draw = deck.OrderBy(c => c.Id.Entry, StringComparer.Ordinal).ToList();
            var hand = new List<CardModel>(); var discard = new List<CardModel>();
            var cycleEnded = false;
            void Shuffle() { for (var i = draw.Count - 1; i > 0; i--) { var j = random.Next(i + 1); (draw[i], draw[j]) = (draw[j], draw[i]); } }
            Shuffle();
            void Draw(int count)
            {
                for (var i = 0; i < count && hand.Count < 10; i++)
                {
                    if (draw.Count == 0) { cycleEnded = discard.Count > 0; return; }
                    var card = draw[^1]; draw.RemoveAt(draw.Count - 1); hand.Add(card);
                    if (card is GrandFinale) seen++;
                }
            }
            var nextDraw = 0;
            for (var turn = 0; turn < 6; turn++)
            {
                Draw(5 + nextDraw + (turn == 0 ? openingDraw : 0)); nextDraw = 0;
                if (cycleEnded) break;
                var budget = energy;
                // A bounded draw plan gives the condition a fair opportunity:
                // exact draws first; do not spend a draw that overshoots zero.
                for (var step = 0; step < 30; step++)
                {
                    var finale = hand.FirstOrDefault(c => c is GrandFinale);
                    if (finale is not null && draw.Count == 0)
                    { windows++; hand.Remove(finale); discard.Add(finale); continue; }
                    var card = hand.Where(c => c is not GrandFinale && !facts[c].Unplayable && facts[c].Cost <= budget)
                        .OrderByDescending(c => facts[c].Draw > 0 && facts[c].Draw == draw.Count)
                        .ThenByDescending(c => facts[c].Draw > 0 && facts[c].Draw <= draw.Count)
                        .ThenByDescending(c => facts[c].NextTurnDraw)
                        .ThenByDescending(c => c.Type == CardType.Power)
                        .FirstOrDefault();
                    if (card is null) break;
                    hand.Remove(card); budget -= facts[card].Cost;
                    // The resolving card is not in discard during its draw.
                    Draw((int)facts[card].Draw);
                    if (cycleEnded) break;
                    nextDraw += (int)facts[card].NextTurnDraw;
                    var payment = card is Prepared ? (int)card.DynamicVars["Cards"].BaseValue
                        : card is Acrobatics or DaggerThrow or Survivor ? 1 : 0;
                    for (var i = 0; i < payment && hand.Count > 0; i++)
                    {
                        var paid = hand.OrderBy(c => c is GrandFinale).ThenBy(c => facts[c].Draw > 0).First();
                        hand.Remove(paid); discard.Add(paid);
                    }
                    if (card.Type != CardType.Power && !card.Keywords.Contains(CardKeyword.Exhaust)) discard.Add(card);
                }
                discard.AddRange(hand); hand.Clear();
                if (cycleEnded) break;
            }
        }
        return seen == 0 ? 0 : Math.Clamp(windows / seen, 0, 1);
    }
}

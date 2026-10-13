using CoopBots.Building;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CoopBots;

internal sealed class RoomFullOfCheeseHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds => ["GORGE", "SEARCH"];

    internal static (double Cheese, double Cards, double Fights, double Cost) Values(Player player, EventModel model)
    {
        var cost = (double)model.DynamicVars.Damage.BaseValue;
        var hp = (double)player.Creature.CurrentHp;
        var maxHp = Math.Max(1, player.Creature.MaxHp);
        // Paying HP early can buy permanent growth, but the immediate wound
        // must still be survived. Penalise the remaining wound continuously.
        var lifeCost = cost * (1 + 2 * (1 - Math.Clamp((hp - cost) / maxHp, 0, 1)));
        var cheese = hp <= cost ? double.NegativeInfinity
            : HumanCoopAdvisor.RelicValue(ModelDb.Relic<ChosenCheese>(), player).Score - lifeCost;
        var gorge = model.CurrentOptions.FirstOrDefault(o => o.TextKey.EndsWith("GORGE", StringComparison.Ordinal));
        var offered = gorge is null ? [] : OutOfCombatPredictions.EventOption(model, gorge).Cards;
        var deck = player.Deck.Cards.ToList();
        // The current game grants TWO of eight common cards, not eight cards
        // and not upgrades. Price the second against the first addition too.
        var candidates = offered.ToList();
        var cards = 0.0;
        for (var pick = 0; pick < 2 && candidates.Count > 0; pick++)
        {
            var best = candidates.Select(c => (Card: c, Value: BuildValue.AddForDecision(c, player, deck).Total))
                .OrderByDescending(c => c.Value).First();
            cards += best.Value; deck.Add(best.Card); candidates.Remove(best.Card);
        }
        // Unknown predicted rewards still have an opportunity cost.
        if (offered.Count < 2) cards = 12;
        return (cheese, cards, RunDepth.RemainingCombats(player), cost);
    }

    public int? Choose(Player player, EventModel eventModel)
    {
        if (eventModel is not RoomFullOfCheese) return null;
        var cheese = EventHandlers.IndexOf(eventModel, "SEARCH");
        var cards = EventHandlers.IndexOf(eventModel, "GORGE");
        if (cheese < 0 || cards < 0) return null;
        var values = Values(player, eventModel);
        return values.Cheese > values.Cards ? cheese : cards;
    }

    internal static string Explain(Player player, EventModel model)
    {
        var v = Values(player, model);
        return $"cheese={v.Cheese:F1} cards={v.Cards:F1} estimatedFights={v.Fights:F1} hpCost={v.Cost:F1}";
    }
}

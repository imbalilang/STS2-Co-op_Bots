using CoopBots.Building;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;

namespace CoopBots;

/// <summary>
/// Doors of Light and Dark. The light door upgrades <c>{Cards}</c> cards the game
/// picks AT RANDOM; the dark door removes one card the player CHOOSES.
///
/// The option ids carry no wording ("...options.LIGHT" / "...options.DARK"), so the
/// generic text-key scorer rated both at its neutral base 5.0 and the page order
/// decided. Measured live 2026-09-29 (seed 9F4Q3R2BV8HQ): all four seats took the
/// light door for two random upgrades — a Strike and a Defend — while the dark
/// door's chosen removal is worth an order of magnitude more in the bot's own
/// units (a starter removal carries a 30 x up to 2.5 baseline; a basic upgrade is
/// ~3.6-6.5). The event was never modelled; it fell through to the scorer.
///
/// The upgrade is random, but it is not unknown. The same RandomForeseer port the
/// event's hover tips already use replays the game's own generator on a CLONE of
/// the event RNG (RandomForeseer 0.13.14, MIT — see THIRD_PARTY_NOTICES.md), so
/// the two cards can be read off BEFORE choosing. Price THOSE two, never the
/// deck's best two: the whole point is what this option will actually do.
/// </summary>
internal sealed class DoorsOfLightAndDarkHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds => ["DARK", "LIGHT"];

    /// <summary>Both sides and the evidence behind them, so the log can be audited.</summary>
    internal readonly record struct Valuation(double Light, double Dark,
        IReadOnlyList<string> Upgraded, string Removal, bool PredictionAvailable);

    internal static Valuation Price(Player player, EventModel model)
    {
        var light = EventHandlers.IndexOf(model, "LIGHT");
        // What the light door will actually upgrade. The prediction clones the RNG,
        // so asking does not consume the roll it is predicting.
        IReadOnlyList<CardModel> predicted = light < 0
            ? []
            : OutOfCombatPredictions.EventOption(model, model.CurrentOptions[light]).Cards;
        return PriceWith(player, predicted);
    }

    /// <summary>
    /// Both sides priced from an EXPLICIT prediction. Split out of <see cref="Price"/>
    /// because the live prediction needs the event's own RNG, which a headless
    /// harness cannot replay — but the policy itself must still be pinned by a test.
    /// </summary>
    internal static Valuation PriceWith(Player player, IReadOnlyList<CardModel> predicted)
    {
        var deck = player.Deck.Cards.ToList();
        // Match each shown (already upgraded) card back to the deck copy it came
        // from: the prediction returns upgraded COPIES, so the entry id is the link.
        var pool = deck.Where(card => card.IsUpgradable).ToList();
        var upgraded = new List<string>(predicted.Count);
        var after = deck.ToArray();
        foreach (var shown in predicted)
        {
            var match = pool.FirstOrDefault(card => card.Id.Entry == shown.Id.Entry);
            if (match is null) continue;
            pool.Remove(match);
            upgraded.Add(match.Id.Entry);
            if (CardProfile.Upgraded(match, BuildValue.StableEnergy(player)) is { } native)
                after[deck.IndexOf(match)] = native.Card;
        }

        // The dark door is a CHOSEN removal, so its value is the best target the
        // deck actually has — the same unit the shop, camp and removal screens use.
        var removal = deck.Count == 0
            ? (Card: (CardModel?)null, Value: 0.0)
            : deck.Select(card => (Card: (CardModel?)card, Value: BuildValue.RemoveForDecision(card, player, deck).Total))
                .OrderByDescending(entry => entry.Value).First();

        return new Valuation(DeckStrength.Change(deck, after, player).Total, removal.Value, upgraded, removal.Card?.Id.Entry ?? "",
            predicted.Count > 0);
    }

    public int? Choose(Player player, EventModel eventModel)
    {
        if (eventModel is not DoorsOfLightAndDark) return null;
        var light = EventHandlers.IndexOf(eventModel, "LIGHT");
        var dark = EventHandlers.IndexOf(eventModel, "DARK");
        if (light < 0 || dark < 0) return null;
        var values = Price(player, eventModel);
        // Nothing was predicted while the deck still has something to upgrade means
        // the read failed. Defer to the generic scorer rather than guess; a deck
        // with nothing upgradable needs no such guard, because the light door then
        // does nothing and its zero already says so.
        if (!values.PredictionAvailable && player.Deck.Cards.Any(card => card.IsUpgradable)) return null;
        return Decide(light, dark, values);
    }

    /// <summary>The decision on its own, so a test can pin it without a live event.</summary>
    internal static int Decide(int light, int dark, Valuation values)
        => values.Dark > values.Light ? dark : light;

    internal static string Explain(Player player, EventModel model)
    {
        var v = Price(player, model);
        return $"light={v.Light:F1}[{string.Join(",", v.Upgraded)}] dark={v.Dark:F1}({v.Removal}) "
            + $"predicted={v.PredictionAvailable}";
    }
}

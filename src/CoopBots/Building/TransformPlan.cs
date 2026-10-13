using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots.Building;

// Read-only replacement transactions. Native CardFactory owns pool filtering
// and pool order; using CreateRandomCardForTransform would register live cards.
internal static class TransformPlan
{
    internal sealed record Step(CardModel Card, string Result, double Value);
    internal sealed record Plan(IReadOnlyList<Step> Steps, IReadOnlyList<CardModel> Deck, double Value, bool Exact);

    internal static bool UsesEventRng(EventModel model) => model is AromaOfChaos or EndlessConveyor
        or MorphicGrove or Symbiote or Trial or WhisperingHollow;

    internal static Rng? CurrentRng(Player player)
    {
        try
        {
            var model = RunManager.Instance.EventSynchronizer.GetEventForPlayer(player);
            return model is not null && UsesEventRng(model) && !model.IsFinished && model.Rng is not null
                ? new Rng(model.Rng.ToSerializable()) : null;
        }
        catch { return null; }
    }

    internal static CardModel Copy(Player player, CardModel card)
    { var copy = card.IsMutable ? (CardModel)card.MutableClone() : card.ToMutable(); if (copy.Owner is null) copy.Owner = player; return copy; }

    internal static CardModel[] Add(Player player, IEnumerable<CardModel> deck, CardModel card)
    {
        var result = deck.Append(card);
        // BingBong.AfterCardChangedPiles: new deck entries have clonedBy=null;
        // the extra copy is marked by the relic and does not recurse.
        if (player.Relics.Any(r => r is BingBong)) result = result.Append(Copy(player, card));
        return result.ToArray();
    }

    internal static CardModel[] Replace(Player player, IReadOnlyList<CardModel> deck, CardModel original, CardModel replacement)
    {
        var result = deck.ToList();
        var index = result.IndexOf(original);
        if (index < 0) throw new ArgumentException("Transform target is absent from preview deck.");
        result[index] = replacement;
        if (player.Relics.Any(r => r is BingBong)) result.Add(Copy(player, replacement));
        return result.ToArray();
    }

    internal static CardModel Predict(Player player, CardModel original, Rng rng, bool upgrade = false)
    {
        var pool = CardFactory.GetDefaultTransformationOptions(original, isInCombat: false).ToArray();
        var card = Copy(player, rng.NextItem(pool) ?? throw new InvalidOperationException("Empty native transformation pool."));
        if (upgrade && card.IsUpgradable) { card.UpgradeInternal(); card.FinalizeUpgradeInternal(); }
        return card;
    }

    internal static double EventValue(Player player, EventModel model, EventOption option)
    {
        var before = player.Deck.Cards.ToArray();
        var deck = before;
        var count = model is Trial or MorphicGrove ? 2 : model is Symbiote
            ? model.DynamicVars.Cards.IntValue : 1;
        if (model is Trial && option.TextKey.Contains("NONDESCRIPT", StringComparison.Ordinal))
            deck = Add(player, deck, Copy(player, ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.Doubt>()));
        var plan = Choose(player, deck, count, UsesEventRng(model) ? model.Rng : null, deck);
        var value = plan.Value + BuildValue.Deck(deck, player) - BuildValue.Deck(before, player);
        if (model is MorphicGrove) value -= EventTrade.Gold(player.Gold);
        if (model is EndlessConveyor) value -= EventTrade.Gold((double)model.DynamicVars.Gold.BaseValue);
        if (model is WhisperingHollow) value -= EventTrade.HealthLost((double)model.DynamicVars.HpLoss.BaseValue);
        return value;
    }

    internal static double TrialRewards(Player player, EventModel model, EventOption option)
    {
        var before = player.Deck.Cards.ToArray();
        var deck = Add(player, before, Copy(player, ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.Doubt>()));
        // Both verdicts pay the same curse transaction. Each of the guilty
        // verdict's two reward screens can be skipped independently; the
        // second screen sees the first selection and BingBong's extra copy.
        foreach (var bundle in CoopBots.Kernel.OutOfCombatPredictions.EventOption(model, option).Bundles)
        {
            var current = BuildValue.Deck(deck, player);
            var choices = bundle.Cards.Select(card => Add(player, deck, Copy(player, card)))
                .Select(after => (Deck: after, Value: BuildValue.Deck(after, player))).ToArray();
            if (ConstructionDecision.AwaitingValues) break;
            var best = choices.OrderByDescending(c => c.Value).FirstOrDefault();
            if (best.Deck is not null && best.Value > current) deck = best.Deck;
        }
        return BuildValue.Deck(deck, player) - BuildValue.Deck(before, player);
    }

    // Paired samples when the source cannot be identified. Selected cards are
    // removed AND replaced in every sample, including curse/off-class pools.
    // Each subsequent choice sees the prior replacement and advanced RNG.
    internal static Plan Choose(Player player, IEnumerable<CardModel> candidates, int count,
        Rng? rng = null, IReadOnlyList<CardModel>? before = null, bool upgrade = false,
        bool forced = false)
    {
        before ??= player.Deck.Cards.ToArray();
        var baseline = BuildValue.Deck(before, player);
        var remaining = candidates.Where(c => c.IsTransformable && before.Contains(c)).Distinct().ToList();
        var randoms = rng is not null ? new[] { new Rng(rng.ToSerializable()) }
            : Enumerable.Range(0, 8).Select(i => new Rng((ulong)(1729 + i * 7919))).ToArray();
        var decks = randoms.Select(_ => before.ToArray()).ToArray();
        var steps = new List<Step>();
        var current = baseline;
        while (steps.Count < count && remaining.Count > 0)
        {
            // A transformation is a removal with a random replacement attached. The
            // source the edit is spent on must first be a card the deck can afford to
            // lose — a curse or an unupgraded basic Strike/Defend, and never a card the
            // deck would break without (the removal guard) — while any such filler is
            // still on the table. Only within that preferred pool do we let the
            // predicted replacement pick the best outcome. Without this the chooser
            // spent the edit on whichever source's single RNG draw happened to be
            // lucky: live 2026-10-04 it turned BLOODLETTING (the deck's energy engine)
            // into MIDNIGHT while basic Strikes/Defends were still in the deck.
            var pool = forced
                ? remaining.Take(1)
                : PreferredTargets(remaining, decks[0]);
            var distinct = pool.DistinctBy(c => ConstructionDecision.Cards([c]).Split(':', 2)[1]);
            var trials = distinct.Select(card => {
                var outputs = randoms.Select((r, i) => Predict(player, card, new Rng(r.ToSerializable()), upgrade)).ToArray();
                var after = decks.Select((deck, i) => Replace(player, deck, card, outputs[i])).ToArray();
                return (Card: card, Outputs: outputs, Decks: after, Value: after.Average(d => BuildValue.Deck(d, player)));
            }).ToArray();
            if (ConstructionDecision.AwaitingValues) return new(steps, decks[0], current - baseline, rng is not null);
            var best = trials.OrderByDescending(t => t.Value).ThenBy(t => t.Card.Id.Entry, StringComparer.Ordinal).First();
            steps.Add(new(best.Card, rng is not null ? best.Outputs[0].Id.Entry : "expected-pool", best.Value - current));
            for (var i = 0; i < randoms.Length; i++) Predict(player, best.Card, randoms[i], upgrade);
            decks = best.Decks; current = best.Value; remaining.Remove(best.Card);
        }
        return new(steps, decks[0], current - baseline, rng is not null);
    }

    /// <summary>
    /// The targets a random transformation should be spent on: unprotected filler
    /// (a curse, or an unupgraded basic Strike/Defend) while any exists, else the
    /// unprotected remainder, else every candidate when the whole screen is
    /// protected and the count still has to be filled. Only the ordering INSIDE
    /// the returned pool is left to the predicted replacement.
    /// </summary>
    /// <remarks>
    /// The removal guard is shared with <see cref="RemovalPlan"/> on purpose: a
    /// transform that removed the deck's only energy source would break the deck
    /// exactly like a removal. Live 2026-10-04 the old ranking transformed
    /// BLOODLETTING under an Astrolabe while basic Strikes/Defends remained,
    /// because one RNG sample mapped that source to a rare card.
    /// </remarks>
    internal static IReadOnlyList<CardModel> PreferredTargets(
        IReadOnlyList<CardModel> candidates, IReadOnlyList<CardModel> deck)
    {
        var unprotected = candidates
            .Where(card => !BuildValue.RemovalProtection(card, deck).Protected)
            .ToList();
        var pool = unprotected.Count > 0 ? unprotected : candidates.ToList();
        var filler = pool.Where(IsTransformFiller).ToList();
        return filler.Count > 0 ? filler : pool;
    }

    // The same filler the removal priority holds back for: a curse, or an
    // unupgraded basic Strike/Defend. Other Basic-rarity cards (Neutralize,
    // Survivor) are real cards and stay out of the filler tier.
    private static bool IsTransformFiller(CardModel card)
        => card.Type == CardType.Curse
           || (card.Rarity == CardRarity.Basic && !card.IsUpgraded && card.IsRemovable
               && (card.Tags.Contains(CardTag.Strike) || card.Tags.Contains(CardTag.Defend)));
}

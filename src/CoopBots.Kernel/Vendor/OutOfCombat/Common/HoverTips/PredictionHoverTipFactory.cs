using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using CoopBots.Kernel.Vendor.RandomForeseer.Localization;

namespace CoopBots.Kernel.Vendor.RandomForeseer.Common.HoverTips;

/// <summary>
/// Creates individual prediction HoverTips without advancing RNG or changing game model state.
/// </summary>
/// <remarks>
/// Model-based methods accept either canonical or prediction/mutable models as supplied by the caller. The factory
/// does not clone or convert the model; callers must choose the model source appropriate for the prediction semantics.
/// </remarks>
internal static class PredictionHoverTipFactory
{
    /// <summary>
    /// Prefix used to identify prediction-owned text and model HoverTips.
    /// </summary>
    public const string HoverTipIdPrefix = $"{Entry.ModId}:Prediction";

    // Local addition for the kernel facade. Upstream nulls CanonicalModel on
    // relic and potion tips so a prediction cannot mark them as discovered,
    // which also throws the identity away. Consumers that need to *reason*
    // about what was predicted rather than display it read it back from here.
    private static readonly ConditionalWeakTable<IHoverTip, AbstractModel> PredictedModels = [];

    public static bool TryGetModel(IHoverTip tip, out AbstractModel model)
        => PredictedModels.TryGetValue(tip, out model!);

    /// <summary>
    /// Creates a non-dimmed prediction tip for one card.
    /// </summary>
    public static IHoverTip Card(CardModel card)
    {
        return Card(card, isDimmed: false);
    }

    /// <summary>
    /// Creates a prediction tip for one card, optionally dimmed for transform-result presentation.
    /// </summary>
    public static IHoverTip Card(CardModel card, bool isDimmed)
    {
        return new PredictionCardHoverTip(card, isDimmed);
    }

    /// <summary>
    /// Creates one logical prediction bundle tip from a non-empty card list.
    /// </summary>
    /// <remarks>
    /// <paramref name="cards"/> must be in prediction/semantic order and must contain at least one card. Do not
    /// reverse the list for visual stacking; <paramref name="kind"/> is consumed by the presentation layer.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="cards"/> is empty.</exception>
    public static IHoverTip CardBundle(
        IReadOnlyList<CardModel> cards,
        PredictionCardBundleKind kind = PredictionCardBundleKind.Regular)
    {
        ArgumentOutOfRangeException.ThrowIfZero(cards.Count);
        return new PredictionCardBundleHoverTip(cards, kind);
    }

    /// <summary>
    /// Creates a prediction tip from a potion's HoverTip.
    /// </summary>
    /// <remarks>
    /// The returned HoverTip is marked instanced and detached from its canonical model so prediction display cannot
    /// mark the potion as discovered.
    /// </remarks>
    public static IHoverTip Potion(PotionModel potion)
    {
        return FromModelHoverTip(potion.HoverTip, potion);
    }

    /// <summary>
    /// Creates a prediction tip from a relic's HoverTip.
    /// </summary>
    /// <remarks>
    /// The returned HoverTip is marked instanced and detached from its canonical model so prediction display cannot
    /// mark the relic as discovered.
    /// </remarks>
    public static IHoverTip Relic(RelicModel relic)
    {
        return FromModelHoverTip(relic.HoverTip, relic);
    }

    /// <summary>
    /// Creates a prediction tip from an orb's dumb HoverTip.
    /// </summary>
    /// <remarks>
    /// The returned HoverTip is marked instanced and detached from its canonical model.
    /// </remarks>
    public static IHoverTip Orb(OrbModel orb)
    {
        return FromModelHoverTip(orb.DumbHoverTip);
    }

    /// <summary>
    /// Creates a localized, instanced prediction text tip.
    /// </summary>
    /// <param name="key">Localization key stem with <c>.title</c> and <c>.description</c> entries.</param>
    /// <param name="configureDescription">Optional callback to add formatter variables before display.</param>
    public static HoverTip Text(string key, Action<LocString>? configureDescription = null)
    {
        var title = ModLocalization.Text($"{key}.title");
        var description = ModLocalization.Text($"{key}.description");
        configureDescription?.Invoke(description);

        var tip = new HoverTip(title, description)
        {
            Id = $"{HoverTipIdPrefix}:{key}",
            IsInstanced = true
        };
        return tip;
    }

    private static HoverTip FromModelHoverTip(HoverTip tip, AbstractModel? model = null)
    {
        tip.Id = $"{Entry.ModId}:Prediction:{tip.Id}";
        tip.IsInstanced = true;
        // Vanilla records hover tips with canonical models as discovered progress.
        // Prediction tips are informational only, so they must not reveal cards/relics/potions in the save.
        tip.CanonicalModel = null;
        // Keep the identity for consumers that reason about the prediction instead
        // of displaying it. See PredictedModels above.
        if (model is not null) PredictedModels.Add(tip, model);
        return tip;
    }
}

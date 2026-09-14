// Ported from Random Foreseer 0.13.14 (MIT, copyright (c) 2026 hotwords123).
// See THIRD_PARTY_NOTICES.md.
//
// Upstream is a UI mod: every prediction is gated by a settings page, and the
// fairness setting decides whether an outcome a save/reload could not reach may
// be shown. The kernel has no settings page and no player, so the settings are
// constants and every prediction is on. A bot that could not see the outcome
// would simply be wrong about the board it is choosing for.
//
// The one deliberate behavioural difference from upstream: `Allows` always
// answers yes. See outputs/randomforeseer-out-of-combat-plan.md.

namespace CoopBots.Kernel.Vendor.RandomForeseer.Data;

/// <summary>Outcome classes upstream lets the player gate behind a fairness setting.</summary>
internal enum PredictionFairness
{
    Fair,
    UnfairInSingleplayer,
    UnfairInAllModes,
}

/// <summary>
/// Only the members the ported code actually reads are kept. Upstream's schema
/// version, persistence attributes and in-combat display options are dropped
/// with the settings UI.
/// </summary>
internal sealed class ModSettings
{
    public static ModSettings Default { get; } = new();

    public bool ShowDriftWarnings { get; set; }
    public bool PredictBaseGameCardsOnly { get; set; }
    public bool InvokeBaseGameHookListenersOnly { get; set; }

    public int SlipperyBridgeRerollPreviewCount { get; set; } = 5;

    public bool IsPredictionEnabled => true;

    public bool DeckTransformPredictionEnabled => true;
    public bool EventOptionPredictionEnabled => true;
    public bool RestSitePredictionEnabled => true;
    public bool RelicPickupPredictionEnabled => true;
    public bool AncientRelicPickupPredictionEnabled => true;
    public bool MerchantRestockPredictionEnabled => true;
    public bool CardPlayPredictionEnabled => true;
    public bool PotionPredictionEnabled => true;
    public bool CombatTransformPredictionEnabled => true;
    public bool CombatCardGenerationPredictionEnabled => true;
    public bool PotionCardGenerationPredictionEnabled => true;
    public bool PotionGenerationPredictionEnabled => true;

    // Upstream consults the player's fairness setting here. A bot has no such
    // setting and no save to reload; it needs the real outcome.
    public bool Allows(PredictionFairness fairness) => true;
}

internal static class ModData
{
    public static ModSettings Settings => ModSettings.Default;
}

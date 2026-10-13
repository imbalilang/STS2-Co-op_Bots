using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Building;

// Upstream models battle effects, not a permanent-deck calibration corpus.
// We freeze audited native bindings and their source separately from proxy scores.
// Owner's staged architecture plan, 2026-10-09: collect evidence first; no new weights.
internal static class ConstructionMechanismFacts
{
    internal enum Lifetime { Unknown, OwnerSideEnd, Persistent }
    internal enum Stat { Strength, Dexterity, Focus }
    internal enum FlowOrder { Unknown, Draw, DrawThenDiscard, ExhaustThenDraw }
    internal readonly record struct Source(string NativeType, string Member, string GameMvid);
    internal readonly record struct StatGrant(Stat Stat, double Amount, Lifetime Lifetime,
        string AmountVariable, Source Source, bool AfterOwnDamage = false);
    internal sealed record Facts(StatGrant? StatGrant, double? GrossDraw, double RequiredDiscard,
        double RequiredExhaust, bool ExhaustsSelf, FlowOrder Flow, double NextTurnDraw,
        bool DrawForTeam, Source Source)
    {
        // Null means unreviewed, not a proven zero. Contextual hooks and target
        // availability are deliberately not folded into these printed quantities.
        internal bool DrawReviewed => GrossDraw.HasValue;
        internal bool HandSelectionReviewed { get; init; }
    }

    internal static Lifetime PowerLifetime(Type nativePowerType)
        => typeof(TemporaryStrengthPower).IsAssignableFrom(nativePowerType)
            || typeof(TemporaryDexterityPower).IsAssignableFrom(nativePowerType)
            || typeof(TemporaryFocusPower).IsAssignableFrom(nativePowerType)
                ? Lifetime.OwnerSideEnd
                : nativePowerType == typeof(StrengthPower) || nativePowerType == typeof(DexterityPower)
                    || nativePowerType == typeof(FocusPower) ? Lifetime.Persistent : Lifetime.Unknown;

    internal static Facts Read(CardModel card, int energy = 3)
    {
        var plays = 1 + Math.Max(0, card.GetEnchantedReplayCount());
        var source = NativeSource(card.GetType(), "OnPlay");
        if (card.GetType().Assembly != typeof(CardModel).Assembly)
            return new(null, null, 0, 0, card.Keywords.Contains(CardKeyword.Exhaust),
                FlowOrder.Unknown, 0, false, source);
        var resources = CardResourceTiming.Of(card, energy);
        StatGrant? grant = card switch
        {
            SetupStrike => Grant<SetupStrikePower>(card, Stat.Strength, "StrengthPower", afterDamage: true),
            Coordinate => Grant<CoordinatePower>(card, Stat.Strength, "StrengthPower"),
            FeedingFrenzy => Grant<FeedingFrenzyPower>(card, Stat.Strength, "StrengthPower"),
            Fade => Grant<FadePower>(card, Stat.Dexterity, "DexterityPower"),
            Anticipate => Grant<AnticipatePower>(card, Stat.Dexterity, "DexterityPower"),
            Hotfix => Grant<HotfixPower>(card, Stat.Focus, "FocusPower"),
            FocusedStrike => Grant<FocusedStrikePower>(card, Stat.Focus, "FocusPower", afterDamage: true),
            Hyperbeam => Grant<HyperbeamFocusDownPower>(card, Stat.Focus, "FocusPower", -1, true),
            Inflame => Grant<StrengthPower>(card, Stat.Strength, "StrengthPower"),
            Footwork => Grant<DexterityPower>(card, Stat.Dexterity, "DexterityPower"),
            _ => null,
        };
        double? draw = card switch
        {
            Finesse or FlashOfSteel => 1,
            PommelStrike or ShrugItOff or Offering or BattleTrance or Adrenaline or Backflip
                or Acrobatics or DaggerThrow or BurningPact or Prepared => NullableAmount(card, "Cards"),
            _ => null,
        };
        var discard = card is Prepared ? draw ?? 0 : card is Acrobatics or DaggerThrow ? 1 : 0;
        var exhaust = card is BurningPact or TrueGrit ? 1
            : card is PactsEnd ? Amount(card, "Cards") : 0;
        var flow = card is BurningPact ? FlowOrder.ExhaustThenDraw
            : card is Prepared or Acrobatics or DaggerThrow ? FlowOrder.DrawThenDiscard
            : draw.HasValue ? FlowOrder.Draw : FlowOrder.Unknown;
        return new(grant is { } stat ? stat with { Amount = stat.Amount * plays } : null,
            draw * plays, discard * plays, exhaust * plays,
            card.Keywords.Contains(CardKeyword.Exhaust), flow,
            resources.DrawNextTurn * plays, resources.DrawForTeam, source)
        {
            HandSelectionReviewed = card is Prepared or Acrobatics or DaggerThrow or BurningPact or TrueGrit or PactsEnd,
        };
    }

    // Preserve the established NetDraw accounting exactly: Prepared pays its
    // realized gross draw; each other audited selection pays once per replay.
    // The original card's hand slot is charged by CardProfile, not here.
    internal static double HandPayment(CardModel card, double realizedGrossDraw)
        => card.GetType().Name switch
        {
            "Prepared" => realizedGrossDraw,
            "Acrobatics" or "DaggerThrow" or "BurningPact" => 1 + Math.Max(0, card.GetEnchantedReplayCount()),
            _ => 0,
        };

    private static StatGrant? Grant<TPower>(CardModel card, Stat stat, string variable,
        int sign = 1, bool afterDamage = false) where TPower : PowerModel
        => NullableAmount(card, variable) is { } amount
            ? new StatGrant(stat, sign * amount, PowerLifetime(typeof(TPower)), variable,
                NativeSource(typeof(TPower), PowerLifetime(typeof(TPower)) == Lifetime.OwnerSideEnd
                    ? "AfterSideTurnEnd" : "AfterApplied"), afterDamage) : null;
    private static double? NullableAmount(CardModel card, string variable)
        => card.DynamicVars.TryGetValue(variable, out var value) ? (double)value.BaseValue : null;
    private static double Amount(CardModel card, string variable)
        => card.DynamicVars.TryGetValue(variable, out var value) ? (double)value.BaseValue : 0;
    private static Source NativeSource(Type type, string member)
        => new(type.FullName ?? type.Name, member, type.Module.ModuleVersionId.ToString("D"));
}

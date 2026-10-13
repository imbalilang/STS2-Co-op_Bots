using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots;

internal static class EnchantmentEventPolicy
{
    // Only native options that select existing deck cards. Random/automatic
    // enchants (Glam/Goopy/etc.) must not be rejected as if targets were chosen.
    private static (string? Name, CardType? Type, int Count) Choice(EventModel model, EventOption option)
    {
        if (option.IsProceed) return default;
        if (option.Relic is { } relic)
            return relic.GetType().Name switch
            {
                "ElectricShrymp" => ("Imbued", null, 1),
                "PaelsGrowth" => ("Clone", null, 1),
                "TriBoomerang" => ("Instinct", null, relic.DynamicVars.Cards.IntValue),
                _ => default,
            };
        var suffix = option.TextKey.Split('.').Last();
        return (model.GetType().Name, suffix) switch
        {
            ("GraveOfTheForgotten", "CONFRONT") => ("SoulsPower", null, 1),
            ("FieldOfManSizedHoles", "ENTER_YOUR_HOLE") => ("PerfectFit", null, 1),
            ("SelfHelpBook", "READ_THE_BACK") => ("Sharp", CardType.Attack, 1),
            ("SelfHelpBook", "READ_PASSAGE") => ("Nimble", CardType.Skill, 1),
            ("SelfHelpBook", "READ_ENTIRE_BOOK") => ("Swift", CardType.Power, 1),
            ("SapphireSeed", "PLANT") => ("Sown", null, 1),
            ("Symbiote", "APPROACH") => ("Corrupted", null, 1),
            ("SpiralingWhirlpool", "OBSERVE") => ("Spiral", null, 1),
            ("StoneOfAllTime", "PUSH") => ("Vigorous", null, 1),
            ("WoodCarvings", "SNAKE") => ("Slither", null, 1),
            ("WaterloggedScriptorium", "TENTACLE_QUILL") => ("Steady", null, 1),
            ("WaterloggedScriptorium", "PRICKLY_SPONGE") => ("Steady", null, model.DynamicVars.Cards.IntValue),
            _ => default,
        };
    }

    internal static bool NeedsRejectedTarget(Player player, EventModel model, EventOption option)
    {
        var choice = Choice(model, option);
        if (choice.Name is null || EnchantmentPriorities.Resolve(choice.Name) is not { } enchantment) return false;
        var cards = player.Deck.Cards.Where(c => enchantment.CanEnchant(c) && (choice.Type is null || c.Type == choice.Type)).ToArray();
        return cards.Length == 0 || cards.Count(c => EnchantmentPriorities.Classify(c, enchantment).Tier >= 0) < Math.Min(choice.Count, cards.Length);
    }

    internal static bool ShouldAvoid(Player player, EventModel model, EventOption option)
        => NeedsRejectedTarget(player, model, option) && model.CurrentOptions.Any(other =>
            !other.IsLocked && !other.WasChosen && !NeedsRejectedTarget(player, model, other));

    internal static string Explain(Player player, EventModel model)
    {
        var avoided = model.CurrentOptions.Where(o => !o.IsLocked && !o.WasChosen && ShouldAvoid(player, model, o))
            .Select(o => o.TextKey.Split('.').Last()).ToArray();
        return avoided.Length == 0 ? "" : " enchant-veto-options=[" + string.Join(',', avoided) + "]";
    }
}

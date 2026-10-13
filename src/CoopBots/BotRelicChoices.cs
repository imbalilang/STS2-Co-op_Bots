using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots;

internal static class BotRelicChoices
{
    internal static IReadOnlyList<CardModel>? Select(Player player, IReadOnlyList<CardModel> cards,
        int minimum, int maximum, string purpose)
    {
        var source = purpose.Split(':').Last();
        var effect = source switch {
            "GamblingChip" => "DiscardAndDraw", "ToastyMittens" => "Exhaust",
            "Toolbox" or "ChoicesParadox" => "GenerateToHand",
            "ToolsOfTheTradePower" => "Discard", _ => null };
        if (effect is null || player.Creature.CombatState is not CombatState combat) return null;
        var root = KernelCombatCapture.Capture(combat);
        var selected = root.SelectRelicCards(player, cards, effect, minimum, maximum, source);
        MegaCrit.Sts2.Core.Logging.Log.Info($"CoopBots turn-start choice: bot={player.NetId} source={source} effect={effect} "
            + $"selected={selected.Count}/{cards.Count} cards=[{string.Join(',', selected.Select(c => c.Id.Entry))}] "
            + $"comparison=[{root.LastHandChoiceComparison}]");
        return selected;
    }
}

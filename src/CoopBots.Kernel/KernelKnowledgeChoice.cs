using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CoopBots.Kernel;

// Owner 2026-10-07: always choose Disintegration. Live selection, automatic
// prediction and enumerated search choices share this policy; no deck/HP
// valuation may substitute another Knowledge Demon curse.
public static class KernelKnowledgeChoice
{
    public static CardModel? Select(Player player, IReadOnlyList<CardModel> options)
    {
        if (options.Count != 2 || options.SingleOrDefault(c => c is Disintegration) is not { } damage
            || !options.Any(c => c is MindRot or Sloth or WasteAway)) return null;
        return damage;
    }

    internal static string Choose(IReadOnlyList<string> options)
        => options.Single(id => id == "DISINTEGRATION");
}

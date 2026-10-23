using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;

namespace CoopBots.Building;

/// <summary>The contribution package's first-copy acquisition policy.</summary>
internal static class KeyCardAcquisition
{
    // 上游怎么做：永久获取由普通构筑价值与跳过选项比较。
    // 我们怎么做：战士首张 JUGGERNAUT（势不可当），多人首张夹击/击倒优先。
    // 为什么：用户 2026-10-23 要求合入贡献包的全部逻辑；保留包内 ID，
    // JUGGERNAUT 不是 DOMINATE（主宰），不因包内中文误称而替换牌。
    internal static bool Preferred(CardModel card, Player player, IReadOnlyList<CardModel>? deck = null)
    {
        var id = card.Id.Entry;
        var key = id == "JUGGERNAUT" && player.Character is Ironclad
            || (id is "FLANKING" or "KNOCKDOWN") && player.RunState.Players.Count > 1;
        return key && !CardProfile.Of(card).Unplayable
            && !(deck ?? player.Deck.Cards).Any(held => held.Id.Entry == id);
    }
}

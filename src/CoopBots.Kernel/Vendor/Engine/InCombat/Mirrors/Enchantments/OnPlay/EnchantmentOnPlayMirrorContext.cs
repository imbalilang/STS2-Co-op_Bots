using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Enchantments.OnPlay;

internal sealed class EnchantmentOnPlayMirrorContext : CombatCardMirrorContext<EnchantmentModel>
{
    public required CardPlay CardPlay { get; init; }

    protected override AbstractModel GetDispatchSource(EnchantmentModel receiver)
    {
        return OriginalCard.Enchantment is { } original && original.GetType() == receiver.GetType()
            ? original
            : receiver;
    }
}

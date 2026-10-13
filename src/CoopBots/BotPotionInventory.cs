using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CoopBots.Building;

namespace CoopBots;

// Out-of-combat inventory value. These are reserve estimates, not damage
// predictions. Unknown bottles are retained until their effect is modeled.
internal static class BotPotionInventory
{
    internal static double? ReserveValue(Player player, PotionModel potion)
    {
        var deck = player.Deck.Cards;
        var attacks = deck.Count(c => c.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack);
        var blocks = deck.Count(c => c.GainsBlock);
        var handAttacks = Math.Min(3, 5d * attacks / Math.Max(1, deck.Count));
        var handBlocks = Math.Min(3, 5d * blocks / Math.Max(1, deck.Count));
        return potion.GetType().Name switch
        {
            "FlexPotion" => 5 * handAttacks,
            "SpeedPotion" => 5 * handBlocks,
            "StrengthPotion" => 3 * handAttacks * 3,
            "DexterityPotion" => 2 * handBlocks * 3,
            "FyshOil" => (handAttacks + handBlocks) * 3,
            // Two extra copies of the block provided by an expected hand.
            "Fortifier" => 2 * 5 * handBlocks,
            "BlessingOfTheForge" => deck.Select(c => CardProfile.DiffUpgrade(c)).Where(d => d is not null)
                .Sum(d => Math.Max(0, d!.DamageDelta + d.BlockDelta + 4*d.DrawDelta + 6*d.CostDelta))
                * Math.Min(1, 5d / Math.Max(1, deck.Count)),
            "BlockPotion" => 12,
            "FirePotion" => 20,
            "ExplosiveAmpoule" => 20,
            "BloodPotion" => (double)player.Creature.MaxHp * .2,
            "EnergyPotion" => 16,
            "SwiftPotion" => 18,
            "AttackPotion" or "SkillPotion" or "ColorlessPotion" => 18,
            "PowerPotion" => 25,
            // Exhausting the old hand + drawing ten is a reset and cleanup
            // resource. Keep a substantial reserve over one temporary buff.
            "GlowwaterPotion" => 30,
            "FairyInABottle" or "GhostInAJar" => 60,
            "FruitJuice" => 40,
            _ => null,
        };
    }

    internal static PotionModel? Replacement(Player player, PotionModel incoming)
    {
        if (player.HasOpenPotionSlots || !player.CanUseOrRemovePotions
            || !MegaCrit.Sts2.Core.Hooks.Hook.ShouldProcurePotion(player.RunState, player.Creature.CombatState, incoming, player)
            || ReserveValue(player, incoming) is not { } incomingValue) return null;
        return player.Potions.Where(p => p.Id != incoming.Id && ReserveValue(player, p) is { } value
                && incomingValue > value + 2)
            .OrderBy(p => ReserveValue(player, p)).FirstOrDefault();
    }

    internal static string Describe(Player player, PotionModel old, PotionModel incoming)
        => $"CoopBots potion replacement: bot={player.NetId} discard={old.Id.Entry}:{ReserveValue(player, old):F1} "
            + $"take={incoming.Id.Entry}:{ReserveValue(player, incoming):F1} reason=full-belt-better-reward";
}

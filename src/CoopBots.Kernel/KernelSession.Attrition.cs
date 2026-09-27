using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    public sealed record TurnThreat(double HandDamage, double DirectHpLoss, double AttackDamage, double HpLoss);

    // Bounded ranking estimate, not a substitute for EndTurn's actual lifecycle.
    // Card amounts/props mirror CardOnTurnEndInHandMirrors.HandleDamage/HpLoss;
    // branch previews retain Wither's fake upgrades. Hand damage consumes block
    // BEFORE regular end-turn plating and the enemy phase (Aeonglass live R11).
    public TurnThreat Threat(Player player)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        var creature = player.Creature;
        if (Hp(creature) <= 0) return new(0, 0, 0, 0);
        var hand = simulator.State.GetPlayerCombatState(player).Hand.Cards;
        double damage = 0, direct = 0;
        foreach (var card in hand)
        {
            var preview = card.Preview;
            if (!preview.HasTurnEndInHandEffect) continue;
            decimal amount;
            ValueProp props;
            if (preview is Burn or Decay or Infection or Toxic or Wither)
            { amount = preview.DynamicVars.Damage.BaseValue; props = preview.DynamicVars.Damage.Props; }
            else if (preview is BadLuck or Beckon)
            { amount = preview.DynamicVars.HpLoss.BaseValue; props = ValueProp.Unpowered | ValueProp.Unblockable; }
            else if (preview is Regret)
            { amount = hand.Count; props = ValueProp.Unpowered | ValueProp.Unblockable; }
            else continue;
            var hit = Math.Max(0, (int)HookMirrors.ModifyDamage(simulator, creature, creature,
                amount, props, card, null));
            if ((props & ValueProp.Unblockable) != 0) direct += hit;
            else damage += hit;
        }
        var incoming = CurrentAttacks().Sum(a => IntentHit(a.Enemy, creature, a.Raw) * a.Repeats);
        var block = Block(creature);
        var loss = direct + Math.Max(0, damage - block)
            + Math.Max(0, incoming - Math.Max(0, block - damage) - EndPlating(creature));
        return new(damage, direct, incoming, loss);
    }

    internal int UsefulHandCount(Player player)
        => simulator.State.GetPlayerCombatState(player).Hand.Cards.Count(c =>
            !c.Preview.Keywords.Contains(CardKeyword.Unplayable));

    internal bool HasAttritionPressure => Combat.EffectivePowers().Any(p => p is WitheringPresencePower)
        || Party.Any(p => Hp(p.Creature) > 0 && simulator.State.GetPlayerCombatState(p).Hand.Cards
            .Any(c => c.Preview.HasTurnEndInHandEffect));

    internal bool HasWitheringPresence(Player player) => Combat.EffectivePowers()
        .OfType<WitheringPresencePower>().Any(p => p.Amount > 0 && p.Target?.Player == player && Hp(p.Owner) > 0);

    // Historical diagnostic of the V14-V17 fractional estimate. V18 deliberately
    // removes it from decision scores: native continuations already simulate the
    // real shuffled Withers, their damage, and discard/exhaust. Keep the diagnostic
    // for comparisons; no production ranking calls these two methods.
    // Native mirrors charge the actual current-hand damage when it materializes.
    // This separate, bounded estimate prices future redraw damage and the useful
    // draw slot it replaces. It never reads the shuffled order or live RNG.
    internal double WitheringPlayCost(Player player)
    {
        var sources=Combat.EffectivePowers().OfType<WitheringPresencePower>()
            .Where(p=>p.Amount>0 && p.Target?.Player==player && Hp(p.Owner)>0).ToArray();
        if(sources.Length==0)return 0;
        var pcs=simulator.State.GetPlayerCombatState(player);
        var cards=pcs.Hand.Cards.Concat(pcs.DrawPile.Cards).Concat(pcs.DiscardPile.Cards).ToArray();
        var upgrades=Enemies.Where(e=>e.Monster is Aeonglass).Sum(Combat.GetAeonglassWitherUpgradeCount);
        var damage=Math.Max(3+3*upgrades,cards.Where(c=>c.Preview is Wither)
            .Select(c=>(double)c.Preview.DynamicVars.Damage.BaseValue).DefaultIfEmpty(3).Max());
        // Three future turns at five draws, capped at two redraws. +3 damage
        // allows one further strengthening; +3 values the lost useful draw.
        var redraws=Math.Clamp(15d/Math.Max(5,player.Deck.Cards.Count),.25,2);
        return sources.Length*redraws*(damage+3+3)/6;
    }

    internal double WitheringBurden(Player player)
    {
        var sources=Combat.EffectivePowers().OfType<WitheringPresencePower>()
            .Where(p=>p.Amount>0 && p.Target?.Player==player && Hp(p.Owner)>0).ToArray();
        if(sources.Length==0)return 0;
        var pcs=simulator.State.GetPlayerCombatState(player);
        var cards=pcs.Hand.Cards.Concat(pcs.DrawPile.Cards).Concat(pcs.DiscardPile.Cards);
        var redraws=Math.Clamp(15d/Math.Max(5,player.Deck.Cards.Count),.25,2);
        var existing=cards.Where(c=>c.Preview is Wither)
            .Sum(c=>redraws*((double)c.Preview.DynamicVars.Damage.BaseValue+3+3));
        var perPlay=WitheringPlayCost(player)/sources.Length;
        var accrued=sources.Sum(p=>(6-Math.Clamp(p.DynamicVars["CardsLeft"].IntValue,0,6))*perPlay);
        // The sixth play converts five sixths of debt into an actual card and
        // clears the counter: exactly one further sixth, without charging the
        // full future card again. Exhaust removes the corresponding future debt.
        // Use permanent deck size: adding junk must not lower the existing debt.
        return existing+accrued;
    }
}


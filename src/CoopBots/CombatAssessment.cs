using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopBots;

internal static class CombatAssessment
{
    internal static double BlockFor(CardModel card, Creature recipient, Creature? calculationTarget = null)
    {
        return card.DynamicVars.Values.Sum(v => v switch
        {
            BlockVar block => (double)Hook.ModifyBlock(card.CombatState!, recipient, block.BaseValue,
                block.Props, card, null, out _),
            CalculatedBlockVar calculated => (double)Hook.ModifyBlock(card.CombatState!, recipient,
                  calculated.Calculate(calculationTarget ?? recipient), calculated.Props, card, null, out _),
            _ => 0.0
        });
    }
    // Intent UI uses LocalContext.GetMe; calculate modifiers for the actual recipient instead.
    internal static double Incoming(Creature recipient) => recipient.CombatState?.Enemies
        .Where(e => e.IsAlive).Sum(e => FromEnemy(e, recipient)) ?? 0;

    internal static double FromEnemy(Creature enemy, Creature recipient)
        => ProjectIncoming(enemy, recipient, false);

    internal static double AfterWeakUpperBound(Creature enemy, Creature recipient)
        => ProjectIncoming(enemy, recipient, true);

    private static double ProjectIncoming(Creature enemy, Creature recipient, bool addedWeak)
    {
        if (!enemy.IsAlive || enemy.Monster is null) return 0;
        try
        {
            return enemy.Monster.NextMove.Intents.OfType<AttackIntent>().Sum(intent =>
            {
                var damage = Hook.ModifyDamage(recipient.Player!.RunState, recipient.CombatState,
                    recipient, enemy, intent.DamageCalc!(), ValueProp.Move, null, null,
                    ModifyDamageHookType.All, CardPreviewMode.None, out _);
                var perHit = Math.Max(0, (int)damage);
                return (addedWeak ? Math.Ceiling(perHit * .75) : perHit) * intent.Repeats;
            });
        }
        catch { return enemy.Monster.IntendsToAttack ? 8 : 0; }
    }
    internal static double Uncovered(Creature target) => Math.Max(0, Incoming(target) - target.Block
        - (target.Player is { } player ? TurnBoundaryProjection.CurrentEndBlockFor(player) : 0));
    internal static bool InDanger(Creature target) => target.IsAlive
        && (MonsterHazards.Imminent(target) || Uncovered(target) >= target.CurrentHp);
    internal static double HumanWeight(Creature target) => target.Player is { } p && !BotRegistry.IsBot(p.NetId) ? 1.5 : 1;

    // A buff handed to someone who can no longer act is wasted. A bot always
    // spends what it is given; a human only while still deciding, and even then
    // with a discount, because we cannot make them use it. This is what keeps a
    // multiplayer card pointed at the team rather than at a spectator.
    internal static double RecipientConfidence(Creature target)
    {
        if (target.Player is not { } player) return 0;
        if (BotRegistry.IsBot(player.NetId)) return 1.0;
        return CanStillAct(player) ? 0.7 : 0.15;
    }

    internal static bool CanStillAct(Player player)
    {
        try
        {
            return player.Creature.IsAlive && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && !CombatManager.Instance.IsPlayerReadyToEndTurn(player);
        }
        catch { return false; }
    }

    // Cards whose buff expires with the current turn. Two sources, because
    // neither alone is complete: a declared Temporary* power var is readable
    // directly, but the multiplayer timing cards do not declare one — Coordinate
    // declares a plain PowerVar<StrengthPower> and only applies its one-turn
    // power at play time. This list is about coordination timing, not effect
    // modelling, so it is curated and short by design; extend it when a new
    // one-turn buff appears.
    private static readonly HashSet<string> ThisTurnBuffCards = new(StringComparer.Ordinal)
    {
        "COORDINATE", "FADE",
    };

    internal static bool TemporaryBuff(CardModel card)
    {
        try
        {
            if (ThisTurnBuffCards.Contains(card.Id.Entry)) return true;
            foreach (var variable in card.DynamicVars.Values)
            {
                var type = variable.GetType();
                if (!type.IsGenericType) continue;
                foreach (var argument in type.GetGenericArguments())
                    if (typeof(TemporaryStrengthPower).IsAssignableFrom(argument)
                        || argument.Name.StartsWith("Temporary", StringComparison.Ordinal)) return true;
            }
        }
        catch { /* an unreadable card counts as permanent */ }
        return false;
    }
}

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
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
}

using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots;

// A combat-local preference, not an action script. Pure searches (including
// potion probes) must never change the committed target.
internal static class TeamFocus
{
    private sealed class Memory { internal Creature? Target; }
    private static readonly ConditionalWeakTable<ICombatState, Memory> Memories = new();

    internal static Creature? Resolve(ICombatState combat, IReadOnlyList<Creature> enemies, uint? manual)
    {
        if (manual.HasValue)
        {
            var chosen = enemies.FirstOrDefault(e => e.CombatId == manual);
            if (chosen is not null) return chosen;
        }
        // Intent-independent estimate of remaining work. Stable ordering handles
        // ties; after reload, existing damage reconstructs the likely focus.
        var easiest = enemies.OrderBy(Work).ThenBy(e => e.CombatId).FirstOrDefault();
        if (easiest is null) return null;
        var held = Memories.TryGetValue(combat, out var memory) ? memory.Target : null;
        if (held is null || !enemies.Contains(held)) return easiest;
        // Avoid a hard lock when healing, block or a newly exposed enemy makes
        // another target substantially easier. Current attack intent is irrelevant.
        return Work(easiest) < Work(held) * 0.55 ? easiest : held;
    }

    internal static void ObserveSubmitted(ICombatState combat, BotBrain.CombatMove move, uint? manual)
    {
        if (move.Card.Type != CardType.Attack || move.Target is not { IsEnemy: true } target) return;
        var enemies = combat.HittableEnemies.Where(e => e.IsAlive).ToList();
        if (!enemies.Contains(target)) return;
        var preferred = Resolve(combat, enemies, manual);
        var memory = Memories.GetValue(combat, _ => new Memory());
        // A one-off emergency hit on another enemy must not erase the old plan.
        if (memory.Target is null || !enemies.Contains(memory.Target) || ReferenceEquals(preferred, target))
            memory.Target = target;
    }

    // Effective remaining work. A Ritual-like enemy gains Strength every turn,
    // so every turn we leave it alive makes the rest of the fight harder: it
    // must outrank a lower-HP enemy that will stay harmless.
    private static double Work(Creature creature)
    {
        var remaining = Math.Max(0, creature.CurrentHp + creature.Block);
        var growth = creature.Powers.OfType<RitualPower>().Sum(power => (double)power.Amount);
        return remaining / (1 + growth * 0.8);
    }
}

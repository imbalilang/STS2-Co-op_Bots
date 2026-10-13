using System.Runtime.CompilerServices;
using CoopBots.Kernel;
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
        var easiest = enemies.OrderBy(e => Work(combat, e)).ThenBy(e => e.CombatId).FirstOrDefault();
        if (easiest is null) return null;
        var held = Memories.TryGetValue(combat, out var memory) ? memory.Target : null;
        if (held is null || !enemies.Contains(held)) return easiest;
        // Avoid a hard lock when healing, block or a newly exposed enemy makes
        // another target substantially easier. Current attack intent is irrelevant.
        return Work(combat, easiest) < Work(combat, held) * 0.55 ? easiest : held;
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
    private static double Work(ICombatState combat, Creature creature)
    {
        var remaining = Math.Max(0, creature.CurrentHp + creature.Block);
        var growth = creature.Powers.OfType<RitualPower>().Sum(power => (double)power.Amount);
        // 2026-10-05 女王爪牙标注：火炬头聚合体的成长来自女王每回合的
        // BURN_BRIGHT_FOR_ME（+1 力量），它自己不带 RitualPower——改前它被当成
        // 「低血无害」的目标排到最后。女王还活着时按 +1/回合计入。
        if (KernelPriorityAdds.ScalesEachRound(combat.HittableEnemies, creature)) growth += 1;
        // 会复活的次要敌人（幻影/重接/寄生/自适应）：打死不算进度，还会回来。
        // 排到最后，除非场上没有别的目标（×100+1000 确保低血也压不过主怪）。
        // 2026-10-05 owner：胧光怪房间里 Bot 常把火力倒在会复活的爪牙上，
        // 主怪击杀过慢、战损过大。
        if (RevivesWithoutProgress(creature)) return remaining * 100 + 1000;
        return remaining / (1 + growth * 0.8);
    }

    /// <summary>
    /// 次要敌人（非主要）身上带复活/再生成类能力的，击杀不产生胜利进度。
    /// 女王聚合体有 KernelPriorityAdds 的例外标注（owner 点名允许先打），不在此列。
    /// </summary>
    private static bool RevivesWithoutProgress(Creature creature)
        => !creature.IsPrimaryEnemy
           && !KernelPriorityAdds.IsPriorityAdd(creature)
           && creature.Powers.Any(power => power is IllusionPower or AdaptablePower
               or ReattachPower or InfestedPower);
}

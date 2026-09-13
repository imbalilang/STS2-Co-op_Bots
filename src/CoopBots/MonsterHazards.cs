using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots;

// Death outside damage resolution must never be reduced by block, Weak or healing.
internal static class MonsterHazards
{
    internal readonly record struct Sandpit(Creature Source, Creature Target, int Remaining, bool EscapeApplies);

    internal static Sandpit[] Capture(Creature participant)
    {
        var enemies = participant.CombatState?.Enemies.ToArray() ?? Array.Empty<Creature>();
        // FranticEscape uses the first enemy carrying any Sandpit, then the first
        // instance targeting its owner. Preserve that rule even in unusual encounters.
        var escapeSource = enemies.FirstOrDefault(e => e.HasPower<SandpitPower>());
        return enemies.Where(e => e.IsAlive).SelectMany(e => e.Powers.OfType<SandpitPower>()
            .Where(p => p.Target is { IsAlive: true }).Select(p => new Sandpit(e, p.Target!, p.Amount,
                e == escapeSource && ReferenceEquals(p, e.Powers.OfType<SandpitPower>().First(s => s.Target == p.Target)))))
            .ToArray();
    }

    internal static bool Imminent(Creature target, IReadOnlyCollection<Creature>? removed = null)
    {
        if (target.CombatState is not { } combat) return false;
        foreach (var enemy in combat.Enemies)
        {
            if (!enemy.IsAlive || removed is not null && removed.Contains(enemy)) continue;
            foreach (var power in enemy.Powers)
                if (power is SandpitPower pit && pit.Target == target && pit.Amount <= 1) return true;
        }
        return false;
    }

    internal static double EscapeScore(Creature target)
    {
        var pit = Capture(target).Where(p => p.Target == target && p.EscapeApplies).ToArray();
        if (pit.Length == 0) return -500;
        return pit[0].Remaining switch { <= 1 => 1800, 2 => 12, 3 => 3, _ => -1 };
    }

    internal static double ReserveCost(int remaining) => remaining switch { <= 1 => 30, 2 => 12, 3 => 3, _ => 0 };
}

using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopBots;

// Known player-side boundary effects only. Enemy damage is supplied by the
// current intent projection; this is not an enemy-AI or next-draw simulator.
internal static class TurnBoundaryProjection
{
    internal sealed record Snapshot(double[] PlatingBlock, double[] Plating, double[] Ritual,
        double[] Strength, double[] DemonForm, bool[] Barricade, int[] AttackCapacity);
    internal readonly record struct Result(double Hp, double Block, double Strength, double Plating);

    internal static Snapshot Capture(IReadOnlyList<Player> party)
    {
        double[] Amount(string name) => party.Select(p => (double)p.Creature.Powers
            .Where(power => power.GetType().Name == name).Sum(power => power.Amount)).ToArray();
        var plating = Amount("PlatingPower");
        var block = party.Select((p, i) => plating[i] <= 0 ? 0 : Math.Max(0, (double)Hook.ModifyBlock(
            p.Creature.CombatState!, p.Creature, (decimal)plating[i], ValueProp.Unpowered, null, null, out _))).ToArray();
        return new(block, plating, Amount("RitualPower"), Amount("StrengthPower"), Amount("DemonFormPower"),
            party.Select(p => p.Creature.Powers.Any(power => power.GetType().Name == "BarricadePower")).ToArray(),
            party.Select(p => Math.Min(3, p.Deck.Cards.Count(card => card.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack))).ToArray());
    }

    internal static Result Advance(double hp, double blockBeforeEnemy, double incoming, double strength,
        double ritual, double demonForm, double plating, bool barricade)
    {
        var remainingHp = Math.Max(0, hp - Math.Max(0, incoming - blockBeforeEnemy));
        if (remainingHp <= 0) return new(0, 0, 0, 0);
        return new(remainingHp, barricade ? Math.Max(0, blockBeforeEnemy - incoming) : 0,
            strength + ritual + demonForm, Math.Max(0, plating - 1));
    }

    internal static double CurrentEndBlockFor(Player recipient)
    {
        var party = recipient.RunState.Players;
        if (!party.Any(p => p.Creature.IsAlive && p.Creature.Powers.Any(power => power.GetType().Name == "PlatingPower"))) return 0;
        var alive = party.Where(p => p.Creature.IsAlive).ToArray();
        var index = Array.IndexOf(alive, recipient);
        if (index < 0) return 0;
        var block = new double[alive.Length];
        var beacon = alive.Select(p => p.Creature.Powers.Any(power => power.GetType().Name == "BeaconOfHopePower")).ToArray();
        for (var i = 0; i < alive.Length; i++)
        {
            var creature = alive[i].Creature;
            var amount = creature.Powers.Where(power => power.GetType().Name == "PlatingPower").Sum(power => power.Amount);
            if (amount <= 0) continue;
            var granted = (double)Hook.ModifyBlock(creature.CombatState!, creature, amount, ValueProp.Unpowered, null, null, out _);
            ProjectedBlockSharing.Add(block, beacon, alive, i, granted);
        }
        return block[index];
    }
}

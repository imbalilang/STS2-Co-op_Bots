using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopBots;

internal static class ProjectedBlockSharing
{
    internal static void Add(double[] block, bool[] beacon, IReadOnlyList<Player> party, int recipient, double amount)
    {
        if (amount <= 0) return;
        if (!beacon[recipient] || amount < 2) { block[recipient] += Math.Floor(amount); return; }
        var active = new bool[party.Count]; var remaining = 256;
        void Grant(int index, double supplied)
        {
            if (remaining-- <= 0 || supplied <= 0) return;
            block[index] += Math.Floor(supplied);
            if (!beacon[index] || active[index] || supplied < 2) return;
            active[index] = true;
            for (var other = 0; other < party.Count; other++)
            {
                if (other == index) continue;
                var creature = party[other].Creature;
                var shared = (double)Hook.ModifyBlock(creature.CombatState!, creature,
                    (decimal)(supplied * 0.5), ValueProp.Unpowered, null, null, out _);
                Grant(other, Math.Max(0, shared));
            }
            active[index] = false;
        }
        Grant(recipient, amount);
    }
}

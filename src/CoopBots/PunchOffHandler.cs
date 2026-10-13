using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots;

// Both routes give a relic. The optional two-enemy fight buys freedom from an
// Injury and extra combat rewards at the cost of team HP and consumables.
// This is a health reserve policy, not a claimed combat win prediction.
internal sealed class PunchOffHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds => ["NAB", "I_CAN_TAKE_THEM", "FIGHT"];

    internal static double WeakestHealth(Player player) =>
        (player.RunState?.Players ?? [player]).Min(p => (double)p.Creature.CurrentHp / Math.Max(1, (double)p.Creature.MaxHp));

    public int? Choose(Player player, EventModel eventModel)
    {
        var safe = EventHandlers.IndexOf(eventModel, ".NAB");
        var challenge = EventHandlers.IndexOf(eventModel, ".I_CAN_TAKE_THEM");
        if (safe >= 0 && challenge >= 0)
            return WeakestHealth(player) >= .8 ? challenge : safe;
        // FIGHT is the only continuation AFTER the irreversible challenge.
        // Never invent an exit or leave a loaded save waiting forever.
        var committed = EventHandlers.IndexOf(eventModel, ".FIGHT");
        return committed >= 0 ? committed : null;
    }
}

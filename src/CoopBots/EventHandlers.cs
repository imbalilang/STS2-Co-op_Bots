using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;

namespace CoopBots;

/// <summary>
/// Per-event overrides, consulted before the generic scorer.
///
/// Most events are now decided from the game's own data (an option's relic and
/// the models behind its hover tips), which is language independent and carries
/// magnitude. A few still need card-specific reasoning that no option data
/// expresses: Reflections can duplicate the entire deck, and nothing in the
/// option says so. Those get a handler here. Returning null defers to the
/// generic scorer, so an uncovered or misbehaving event can never stall a run.
/// </summary>
internal interface IEventSpecialHandler
{
    /// <summary>
    /// The option ids this handler matches. Checked against the baked event table
    /// so a patch that renames an id fails a test instead of silently leaving the
    /// handler dead.
    /// </summary>
    IReadOnlyList<string> OptionIds { get; }

    /// <summary>Index into <see cref="EventModel.CurrentOptions"/>, or null to defer.</summary>
    int? Choose(Player player, EventModel eventModel);
}

internal static class EventHandlers
{
    private static readonly Dictionary<string, IEventSpecialHandler> Handlers = new(StringComparer.Ordinal)
    {
        ["REFLECTIONS"] = new ReflectionsHandler(),
        ["TABLET_OF_TRUTH"] = new TabletOfTruthHandler(),
        ["CRYSTAL_SPHERE"] = new CrystalSphereHandler(),
        ["POTION_COURIER"] = new PotionCourierHandler(),
        ["MORPHIC_GROVE"] = new MorphicGroveHandler(),
        ["ABYSSAL_BATHS"] = new AbyssalBathsHandler(),
        ["COLOSSAL_FLOWER"] = new ColossalFlowerHandler(),
    };

    /// <summary>Registered handlers, for the drift check against the baked ids.</summary>
    internal static IReadOnlyDictionary<string, IEventSpecialHandler> Registered => Handlers;

    internal static bool Handles(string eventId) => Handlers.ContainsKey(eventId);

    /// <summary>
    /// A handler's index, -1 when it deliberately waits for a later page, or null
    /// to defer to the generic scorer.
    /// </summary>
    internal static int? TryChoose(Player player, EventModel eventModel)
    {
        if (!Handlers.TryGetValue(eventModel.Id.Entry, out var handler)) return null;
        try
        {
            var choice = handler.Choose(player, eventModel);
            if (choice is not { } index) return null;
            if (index < 0) return -1;
            if (index >= eventModel.CurrentOptions.Count) return null;
            var option = eventModel.CurrentOptions[index];
            // A handler may never commit a locked or already-resolved option.
            return option.IsLocked || option.WasChosen ? null : index;
        }
        catch
        {
            return null;
        }
    }

    // Only selectable options: a locked exit must read as "no such option", not
    // as a choice the handler can make.
    internal static int IndexOf(EventModel eventModel, string textSuffix)
    {
        for (var index = 0; index < eventModel.CurrentOptions.Count; index++)
        {
            var option = eventModel.CurrentOptions[index];
            if (!option.IsLocked && !option.WasChosen
                && option.TextKey.EndsWith(textSuffix, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }
}

/// <summary>
/// Reflections offers one page: touch the mirror (downgrade two random cards,
/// upgrade four) or shatter it (clone the whole deck, then add a curse).
/// </summary>
internal sealed class ReflectionsHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds => ["TOUCH_A_MIRROR", "SHATTER"];

    public int? Choose(Player player, EventModel eventModel)
    {
        // Cloning every card doubles the junk along with the payoff, adds a
        // permanent curse and halves the odds of drawing anything the deck was
        // built around. That is not a trade the option data can express, and a
        // real run was lost to it: the bots duplicated 28/31/30-card decks into
        // 57/63/61 right before the act-3 elite and both bosses. Always take the
        // guaranteed mirror instead.
        var mirror = EventHandlers.IndexOf(eventModel, "TOUCH_A_MIRROR");
        return mirror >= 0 ? mirror : null;
    }
}

/// <summary>
/// The divination board opens its screen only for the local player, so a bot
/// never plays it: the event would finish with nothing collected. The bot's
/// board is driven separately (<see cref="CrystalSphereSync"/>), which leaves the
/// entry choice as "spend gold" versus "take a permanent Debt curse". The gold
/// option is taken, because a lasting curse costs the deck every remaining
/// fight while the extra reveals only buy a few more grid items.
/// </summary>
internal sealed class CrystalSphereHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds => ["UNCOVER_FUTURE", "PAYMENT_PLAN"];

    public int? Choose(Player player, EventModel eventModel)
    {
        var gold = EventHandlers.IndexOf(eventModel, "UNCOVER_FUTURE");
        return gold >= 0 ? gold : null;
    }
}

/// <summary>
/// Tablet of Truth's price doubles each page and can reduce Max HP. Verifying
/// the accumulated spend is spread across pages, so it stays a handler.
/// </summary>
internal sealed class TabletOfTruthHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds => ["DECIPHER", "DECIPHER_1", "SMASH", "GIVE_UP"];

    public int? Choose(Player player, EventModel eventModel)
    {
        if (eventModel is not TabletOfTruth tablet) return null;
        var decipher = EventHandlers.IndexOf(eventModel, "DECIPHER");
        var decipherFirst = EventHandlers.IndexOf(eventModel, "DECIPHER_1");
        var smash = EventHandlers.IndexOf(eventModel, "SMASH");
        var giveUp = EventHandlers.IndexOf(eventModel, "GIVE_UP");
        var decipherIndex = decipher >= 0 ? decipher : decipherFirst;
        var stopIndex = smash >= 0 ? smash : giveUp;
        if (decipherIndex < 0) return stopIndex >= 0 ? stopIndex : null;

        var cost = tablet.DynamicVars["DecipherMaxHpLoss"].IntValue;
        var healing = smash >= 0 ? tablet.DynamicVars["SmashHPGain"].IntValue : 0;
        if (eventModel.CurrentOptions[decipherIndex].WillKillPlayer?.Invoke(player) != true
            && BotEventDriver.ShouldDecipherTablet(player, cost, healing)) return decipherIndex;
        // A temporarily unavailable exit is a reason to wait, never to sacrifice
        // HP. Waiting is -1, not "defer": the generic scorer would happily take
        // the bad deal this handler exists to refuse.
        return stopIndex >= 0 ? stopIndex : -1;
    }
}

/// <summary>
/// Potion Courier offers three Foul Potions or one real potion. A Foul Potion
/// damages every non-pet creature including its own drinker, so "grab potions"
/// is a trap: it fills slots with something that hurts the team. Always ransack.
/// </summary>
internal sealed class PotionCourierHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds => ["GRAB_POTIONS", "RANSACK"];

    public int? Choose(Player player, EventModel eventModel)
    {
        var ransack = EventHandlers.IndexOf(eventModel, "RANSACK");
        return ransack >= 0 ? ransack : null;
    }
}

/// <summary>
/// Morphic Grove asks for free +5 Max HP or for every coin the player owns in
/// exchange for two random transforms. Gold buys removals and relics, which is
/// worth more than two random cards, so the free option is taken unless the
/// player has almost nothing to lose.
/// </summary>
internal sealed class MorphicGroveHandler : IEventSpecialHandler
{
    // Below this the gold is not buying anything anyway, so the transforms are
    // the better half of the trade.
    private const int SpareGold = 120;

    public IReadOnlyList<string> OptionIds => ["GROUP", "LONER"];

    public int? Choose(Player player, EventModel eventModel)
    {
        var loner = EventHandlers.IndexOf(eventModel, "LONER");
        var group = EventHandlers.IndexOf(eventModel, "GROUP");
        if (player.Gold <= SpareGold) return group >= 0 ? group : loner >= 0 ? loner : null;
        return loner >= 0 ? loner : null;
    }
}

/// <summary>
/// Abyssal Baths trades health for permanent Max HP, and the damage grows with
/// every soak. Soaking is right while the hit is comfortably survivable and
/// wrong once it would leave the player in danger, so the exit is taken on a
/// health floor rather than on the option's wording.
/// </summary>
internal sealed class AbyssalBathsHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds => ["IMMERSE", "LINGER", "ABSTAIN", "EXIT_BATHS"];

    public int? Choose(Player player, EventModel eventModel)
    {
        var soak = -1;
        foreach (var suffix in new[] { "IMMERSE", "LINGER" })
        {
            soak = EventHandlers.IndexOf(eventModel, suffix);
            if (soak >= 0) break;
        }
        var exit = -1;
        foreach (var suffix in new[] { "ABSTAIN", "EXIT_BATHS" })
        {
            exit = EventHandlers.IndexOf(eventModel, suffix);
            if (exit >= 0) break;
        }
        if (soak < 0) return null;
        if (eventModel.CurrentOptions[soak].WillKillPlayer?.Invoke(player) == true) return exit >= 0 ? exit : null;
        var maxHp = Math.Max(1, player.Creature.MaxHp);
        var damage = Damage(eventModel);
        // A soak only ever costs a few hit points and grants permanent Max HP, so
        // when the damage cannot be read the healthy keep going and the hurt stop.
        var survivable = damage > 0
            ? player.Creature.CurrentHp - damage >= maxHp * 0.35
            : player.Creature.CurrentHp >= maxHp * 0.5;
        return survivable ? soak : exit >= 0 ? exit : null;
    }

    // The event publishes the current soak damage; zero means it could not be
    // read, which the caller handles conservatively rather than by refusing.
    private static int Damage(EventModel eventModel)
    {
        try { return eventModel.DynamicVars["Damage"].IntValue; }
        catch { return 0; }
    }
}

/// <summary>
/// Colossal Flower pays 35, 75 or 135 gold (or a relic) for reaching deeper, and
/// each reach costs a small amount of unblockable damage. Taking the prize
/// immediately is the safe line the generic scorer falls into, because the
/// option ids carry no wording to score. Going deeper is worth it while the
/// player can absorb the next hit and still have health left to fight with.
/// </summary>
internal sealed class ColossalFlowerHandler : IEventSpecialHandler
{
    public IReadOnlyList<string> OptionIds =>
        ["EXTRACT_CURRENT_PRIZE_1", "EXTRACT_CURRENT_PRIZE_2", "EXTRACT_INSTEAD", "POLLINOUS_CORE",
         "REACH_DEEPER_1", "REACH_DEEPER_2"];

    public int? Choose(Player player, EventModel eventModel)
    {
        // The relic at the end of the dig beats the last gold step, so it is
        // taken whenever the player can still afford the hit.
        var relic = EventHandlers.IndexOf(eventModel, "POLLINOUS_CORE");
        var deeper = -1;
        for (var step = 1; step <= 3 && deeper < 0; step++)
            deeper = EventHandlers.IndexOf(eventModel, "REACH_DEEPER_" + step);
        var prize = -1;
        for (var step = 1; step <= 3 && prize < 0; step++)
            prize = EventHandlers.IndexOf(eventModel, "EXTRACT_CURRENT_PRIZE_" + step);
        if (prize < 0) prize = EventHandlers.IndexOf(eventModel, "EXTRACT_INSTEAD");

        var dive = relic >= 0 ? relic : deeper;
        if (dive < 0) return null;
        var floor = Math.Max(1, player.Creature.MaxHp * 0.4);
        return player.Creature.CurrentHp >= floor ? dive : prize >= 0 ? prize : null;
    }
}

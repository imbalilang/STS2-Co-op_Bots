using System.Collections.Generic;
using System.Linq;

namespace CoopBots;

/// <summary>
/// Seats the bot logic makes decisions for, beyond the synthetic bots.
///
/// A real player can hand their own seat over from the room panel: from then on
/// the bot answers for that seat and the player watches. The seat keeps its real
/// net id, its real <c>Player</c> and its real character, so nothing in the run
/// schema changes and every peer keeps seeing an ordinary player — only the
/// client that owns the seat stops making the choices itself.
///
/// Two different questions live in this codebase and must not be conflated:
///
///   <see cref="BotRegistry.IsBot"/>  — is this a synthetic seat? Drives identity,
///                                      display names, tiers and the lobby roster.
///   <see cref="Drives"/>             — does the bot logic answer for this seat?
///                                      Drives control: who plans, who submits,
///                                      who answers a reward or an event.
///
/// Widening a <c>IsBot</c> call site to <see cref="Drives"/> is only correct where
/// the question is "who decides". It is wrong where the question is a property of
/// the character rather than of control — a handed-over seat is still a human
/// character, and the party weighting in <c>TeamCombatPlanner</c> and the advice
/// plumbing in <c>HumanCoopAdvisor</c> should keep treating it as one.
///
/// Only the host submits actions (<c>BotRuntime</c> returns early for clients, and
/// <c>ActionQueueSynchronizer.EnqueueAction</c> only broadcasts from the host), so
/// only the host may hand a seat over. A client that wants its own seat handed
/// over would need a request message to the host; that is not implemented, and
/// <see cref="CanToggle"/> is what keeps the panel from offering it.
/// </summary>
public static class AutoPilot
{
    private static readonly HashSet<ulong> Seats = new();

    /// <summary>Does the bot logic answer for this seat?</summary>
    public static bool Drives(ulong netId) => BotRegistry.IsBot(netId) || Seats.Contains(netId);

    /// <summary>A real player's seat that has been handed over, as opposed to a
    /// synthetic bot. The two are distinguished anywhere the difference is visible
    /// — display names, the lobby roster, and "is there a human here at all".</summary>
    public static bool IsAutopiloted(ulong netId) => !BotRegistry.IsBot(netId) && Seats.Contains(netId);

    public static IReadOnlyCollection<ulong> HandedOver => Seats;

    public static bool Any => Seats.Count > 0;

    /// <summary>Only the host can submit for a seat it does not own, so the panel
    /// only offers the toggle to the host. Bots are never toggled here: they are
    /// added and removed through the roster, not handed over.</summary>
    public static bool CanToggle(ulong netId, bool isHost) => isHost && !BotRegistry.IsBot(netId);

    public static void Set(ulong netId, bool handedOver)
    {
        if (BotRegistry.IsBot(netId)) return;
        if (handedOver) Seats.Add(netId);
        else Seats.Remove(netId);
    }

    public static void Clear() => Seats.Clear();

    /// <summary>A seat label for logs and callouts. Bots keep their tier name; a
    /// handed-over seat names the player instead, so a log never shows a human's
    /// steam id rendered through the bot tier bits as a phantom "Bot 0".</summary>
    public static string Label(ulong netId, string? playerName = null)
    {
        if (BotRegistry.IsBot(netId)) return BotRegistry.DisplayName(netId);
        var who = string.IsNullOrWhiteSpace(playerName) ? netId.ToString() : playerName!;
        return IsAutopiloted(netId) ? $"{who}（Bot 接管）" : who;
    }

    /// <summary>The handed-over seats that are actually present in a run, so a
    /// stale entry from a previous run can never make the bot act for a seat that
    /// is no longer there.</summary>
    public static IEnumerable<ulong> HandedOverIn(IEnumerable<ulong> presentNetIds)
    {
        var present = presentNetIds as ICollection<ulong> ?? presentNetIds.ToList();
        return Seats.Where(present.Contains);
    }
}

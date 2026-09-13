using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using CoopBots.Building;

namespace CoopBots;

/// <summary>
/// Deck-building instrumentation. The in-combat side has had coverage and
/// decision logging for a while; construction had none, so every review of
/// "the bots still draft badly" had to guess. These lines are the evidence base
/// for that workstream: what the deck actually looked like at each act, and why
/// every permanent change was made.
///
/// Kept deliberately compact — one line per event, sorted and deduplicated — so
/// a full run stays readable in the log.
/// </summary>
internal static class BuildTrace
{
    internal const string LogPrefix = "CoopBots build";

    /// <summary>Compact, stable deck listing: "BASH+ STRIKE_IRONCLADx4 DEFEND...".</summary>
    internal static string Describe(Player player)
    {
        try
        {
            return string.Join(" ", player.Deck.Cards
                .GroupBy(card => card.Id.Entry + (card.IsUpgraded ? "+" : ""))
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Count() > 1 ? $"{group.Key}x{group.Count()}" : group.Key));
        }
        catch (Exception error)
        {
            return "<unreadable:" + error.GetType().Name + ">";
        }
    }

    internal static void LogDeck(string tag, Player player)
    {
        try
        {
            Log.Info($"{LogPrefix}[{tag}]: {player.NetId} size={player.Deck.Cards.Count} "
                + $"route={Archetypes.Describe(player.Deck.Cards.ToList(), player)} :: {Describe(player)}");
        }
        catch { /* instrumentation must never break a run */ }
    }

    internal static void LogAllDecks(string tag, IEnumerable<Player> players)
    {
        foreach (var player in players.Where(p => BotRegistry.IsBot(p.NetId)))
            LogDeck(tag, player);
    }

    /// <summary>
    /// A card-reward decision: every candidate with its computed value and the
    /// reasons behind it, and whether the team took one or skipped the screen.
    /// </summary>
    internal static void LogReward(Player player, IReadOnlyList<CardModel> candidates, int chosen, bool skip)
    {
        try
        {
            double skipValue = 0;
            var parts = new List<string>(candidates.Count);
            for (var index = 0; index < candidates.Count; index++)
            {
                double value;
                string reason;
                try
                {
                    var valuation = BuildValue.Add(candidates[index], player);
                    value = valuation.Total;
                    reason = valuation.Reason;
                }
                catch (Exception error) { value = double.NaN; reason = "error:" + error.GetType().Name; }
                if (value > skipValue) skipValue = value;
                parts.Add($"{(index == chosen && !skip ? ">" : string.Empty)}{candidates[index].Id.Entry}={value:F1}({reason})");
            }
            var pick = skip || chosen < 0 ? "SKIP" : candidates[chosen].Id.Entry;
            Log.Info($"{LogPrefix}: reward bot={player.NetId} pick={pick} size={player.Deck.Cards.Count} | {string.Join(" ", parts)}");
        }
        catch { /* instrumentation must never break a run */ }
    }

    /// <summary>
    /// A permanent deck edit (removal, upgrade, transform, smith, event choice):
    /// what was chosen and the score each candidate received.
    /// </summary>
    internal static void LogDeckEdit(Player player, string purpose,
        IReadOnlyList<CardModel> chosen, IReadOnlyList<(CardModel Card, double Value, string Reason)> ranked)
    {
        try
        {
            var picks = chosen.Count == 0 ? "-" : string.Join(",", chosen.Select(card => card.Id.Entry));
            var detail = string.Join(" ", ranked.Take(8).Select(entry =>
                $"{entry.Card.Id.Entry}{(entry.Card.IsUpgraded ? "+" : string.Empty)}={entry.Value:F1}({entry.Reason})"));
            Log.Info($"{LogPrefix}: edit purpose={purpose} bot={player.NetId} chose=[{picks}] size={player.Deck.Cards.Count} | {detail}");
        }
        catch { /* instrumentation must never break a run */ }
    }

    /// <summary>
    /// A shop or event purchase in build terms: what was bought and what the
    /// deck looked like immediately afterwards.
    /// </summary>
    internal static void LogAcquisition(Player player, string kind, string item, int gold, int cost)
    {
        try
        {
            Log.Info($"{LogPrefix}: buy kind={kind} bot={player.NetId} item={item} cost={cost} gold={gold} size={player.Deck.Cards.Count}");
        }
        catch { /* instrumentation must never break a run */ }
    }
}

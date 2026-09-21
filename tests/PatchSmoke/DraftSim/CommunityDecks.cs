using System.Text.Json;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace DeckSim;

/// <summary>One real Ascension 10 winning deck, as the community API reports it.</summary>
internal sealed record CommunityDeck(
    string RunHash, string Character, int Players, bool Win, string? KilledBy,
    IReadOnlyList<CommunityCard> Deck, IReadOnlyList<string> Picks, int Skipped);

internal sealed record CommunityCard(string Id, int Upgrades);

/// <summary>The community's own per-card judgement: 0-100 score, Elo, pick and win counts.</summary>
internal sealed record CommunityCardScore(double Score, double Elo, int Picks, int Wins, double WinRate);

/// <summary>
/// Loads the Spire Codex cache written by scripts/fetch-spire-codex-decks.py.
///
/// These are decks that won Ascension 10 for real, plus the community's own
/// per-card score. The point of loading them is that everything else the
/// pipeline measures is self-referential: the bot drafts with BuildValue and the
/// scorer scores the result, so the two can agree with each other and both be
/// wrong. These files are the outside opinion.
/// </summary>
internal static class CommunityDecks
{
    // The per-run cache is the source of truth rather than the combined file: the
    // fetch writes one file per run as it goes, so a validation can run on a
    // partial crawl instead of waiting for the whole thing.
    private const string DeckDirectory = "work/spire-codex/decks/runs";
    private const string ScoreFile = "work/spire-codex/card-scores.json";

    internal static string DeckPath => Path.Combine(DraftSimHarness.RepoRoot(), DeckDirectory);
    internal static string ScorePath => Path.Combine(DraftSimHarness.RepoRoot(), ScoreFile);

    internal static bool Available => Directory.Exists(DeckPath) && Directory.GetFiles(DeckPath, "*.json").Length > 0
        && File.Exists(ScorePath);

    /// <summary>
    /// What the cache held and what was usable. The dropped counts are returned
    /// rather than swallowed: a record skipped for a missing party size is a
    /// smaller sample than the cache suggests, and the validation has to be able
    /// to say so out loud.
    /// </summary>
    internal sealed record Sample(
        IReadOnlyList<CommunityDeck> Decks, int SkippedNoParty, int SkippedNoCards, int Files);

    internal static Sample Load()
    {
        var decks = new List<CommunityDeck>();
        var noParty = 0;
        var noCards = 0;
        var files = 0;
        var known = ModelDb.AllCards.Select(card => card.Id.Entry).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(DeckPath, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            files++;
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var element = document.RootElement;
            // Only ascension 10 runs: the whole point of the sample is the pressure
            // the drafting was under, and a mixed sample would average that away.
            if (element.GetProperty("ascension").GetInt32() != 10) continue;
            var cards = new List<CommunityCard>();
            var skipped = 0;
            foreach (var card in element.GetProperty("deck").EnumerateArray())
            {
                var id = card.GetProperty("id").GetString() ?? string.Empty;
                // A cached run can come from an older build id; a card this build
                // does not know is dropped and counted, never silently guessed at.
                if (!known.Contains(id))
                {
                    skipped++;
                    continue;
                }
                cards.Add(new CommunityCard(id, card.GetProperty("upgrades").GetInt32()));
            }
            if (cards.Count == 0) { noCards++; continue; }
            // A record written before the party size was captured is skipped rather
            // than defaulted to 1: the boss scales with party size and so does the
            // damage each player takes, so an unknown party size is not a value that
            // can be assumed. It is counted, because a cache full of these is a
            // sample far smaller than it looks.
            if (!element.TryGetProperty("players", out var party) || party.GetInt32() <= 0) { noParty++; continue; }
            decks.Add(new CommunityDeck(
                element.GetProperty("run_hash").GetString() ?? string.Empty,
                element.GetProperty("character").GetString() ?? string.Empty,
                party.GetInt32(),
                element.GetProperty("win").GetBoolean(),
                element.TryGetProperty("killed_by", out var killed) ? killed.GetString() : null,
                cards,
                element.TryGetProperty("picks", out var picks)
                    ? picks.EnumerateArray().Select(entry => entry.GetString() ?? string.Empty).ToList()
                    : [],
                skipped));
        }
        return new Sample(decks, noParty, noCards, files);
    }

    internal static IReadOnlyDictionary<string, CommunityCardScore> LoadScores()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ScorePath));
        var scores = new Dictionary<string, CommunityCardScore>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = property.Value;
            scores[property.Name] = new CommunityCardScore(
                value.GetProperty("score").GetDouble(),
                value.TryGetProperty("elo", out var elo) && elo.ValueKind == JsonValueKind.Number ? elo.GetDouble() : 0,
                value.GetProperty("picks").GetInt32(),
                value.GetProperty("wins").GetInt32(),
                value.GetProperty("win_rate").GetDouble());
        }
        return scores;
    }

    /// <summary>
    /// Rebuilds a real deck as live card models. Upgrade levels are reapplied, so
    /// a smithed winning deck is scored as the smithed deck it was.
    /// </summary>
    internal static List<CardModel> Build(Player player, CommunityDeck deck, out int unknown)
    {
        unknown = deck.Skipped;
        var cards = new List<CardModel>();
        foreach (var entry in deck.Deck)
        {
            var canonical = ModelDb.AllCards.FirstOrDefault(card =>
                string.Equals(card.Id.Entry, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (canonical is null) { unknown++; continue; }
            var card = player.RunState.CreateCard(canonical, player);
            for (var level = 0; level < entry.Upgrades && card.IsUpgradable; level++)
            {
                try { card.UpgradeInternal(); card.FinalizeUpgradeInternal(); }
                catch { break; }
            }
            cards.Add(card);
        }
        return cards;
    }

    /// <summary>
    /// What the drafting logic thinks of a card, measured against a fixed
    /// reference deck.
    ///
    /// Deliberately the production <see cref="BuildValue.Marginal"/> and not a
    /// local approximation of it: a validation that restated the valuation in its
    /// own words would only ever confirm its own restatement. Pinning the deck is
    /// what makes it comparable at all — the community score is deck-independent,
    /// so an unpinned comparison would just measure whatever the bot had drafted.
    /// </summary>
    internal static double DraftValue(CardModel card, Player player, IReadOnlyList<CardModel> referenceDeck)
        => BuildValue.Marginal(card, player, referenceDeck).Total;
}

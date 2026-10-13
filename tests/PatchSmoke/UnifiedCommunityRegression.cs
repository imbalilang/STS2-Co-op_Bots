using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CoopBots.Building;
using DeckSim;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Outside labels are reward decisions from recorded runs, not the Elo values
// used by the policy. Use the first ten floors, before persistent energy relics
// dominate; unknown/lossy histories are counted and excluded, not guessed.
internal static class UnifiedCommunityRegression
{
    internal static void Run(string[] args)
    {
        var root = DraftSimHarness.RepoRoot();
        var rows = new List<object>();
        int excluded = 0, files = 0, duplicates = 0, modifiedRuns = 0;
        var games = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(Path.Combine(root, "work/spire-codex/runs-full"), "*.json").Order())
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var run = json.RootElement;
            if (run.GetProperty("ascension").GetInt32() != 10) continue;
            files++;
            var gameKey = run.GetProperty("seed").GetString() + "/" + run.GetProperty("start_time").GetRawText()
                + "/" + string.Join(',', run.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("id").GetRawText()).Order());
            if (!games.Add(gameKey)) { duplicates++; continue; }
            if (run.GetProperty("game_mode").GetString() != "standard"
                || run.GetProperty("modifiers").GetArrayLength() != 0) { modifiedRuns++; continue; }
            var group = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gameKey))).ToLowerInvariant();
            var seats = run.GetProperty("players").GetArrayLength();
            var characters = run.GetProperty("players").EnumerateArray()
                .Select(i => i.GetProperty("character").GetString()!.Replace("CHARACTER.", "")).ToArray();
            if (characters.Any(c => c is not ("IRONCLAD" or "SILENT" or "DEFECT" or "NECROBINDER" or "REGENT")))
                { excluded++; continue; }
            // The standard starter reference preserves actual character cards.
            var players = new Dictionary<ulong, Player>();
            foreach (var info in run.GetProperty("players").EnumerateArray())
            {
                var id = info.GetProperty("id").GetUInt64();
                var character = info.GetProperty("character").GetString()!.Replace("CHARACTER.", "");
                players[id] = Player.CreateForNewRun<Deprived>(UnlockState.all, id);
                typeof(Player).GetField("<Character>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .SetValue(players[id], ModelDb.AllCharacters.First(c => c.Id.Entry == character));
                players[id].MaxEnergy = 3;
            }
            var state = RunState.CreateForTest(players.Values.ToArray(), ascensionLevel: 10, seed: "COMMUNITY-REPLAY");
            foreach (var p in players.Values)
            {
                foreach (var card in p.Character.StartingDeck) p.Deck.AddInternal(state.CreateCard(card, p));
                p.Deck.AddInternal(state.CreateCard<MegaCrit.Sts2.Core.Models.Cards.AscendersBane>(p));
            }
            var invalid = new HashSet<ulong>();
            int floor = 0;
            foreach (var act in run.GetProperty("map_point_history").EnumerateArray())
            foreach (var node in act.EnumerateArray())
            {
                floor++;
                if (floor > 10) continue;
                foreach (var stats in node.GetProperty("player_stats").EnumerateArray())
                {
                    var id = stats.GetProperty("player_id").GetUInt64();
                    if (!players.TryGetValue(id, out var p)) continue;
                    if (stats.TryGetProperty("card_choices", out var choices) && choices.GetArrayLength() is >= 2 and <= 4)
                    {
                        if (invalid.Contains(id)) { excluded++; continue; }
                        var offers = new List<(CardModel Card, bool Picked)>();
                        foreach (var entry in choices.EnumerateArray())
                        {
                            var card = Create(p, entry.GetProperty("card"));
                            if (card is null) { invalid.Add(id); break; }
                            offers.Add((card, entry.GetProperty("was_picked").GetBoolean()));
                        }
                        if (invalid.Contains(id) || offers.Count(c => c.Picked) > 1) { excluded++; continue; }
                        using var evaluation = DeckStrength.BeginEvaluation();
                        var before = DeckStrength.Evaluate(p.Deck.Cards.ToArray(), p);
                        var estimates = offers.Select(c => DeckStrength.Evaluate(p.Deck.Cards.Append(c.Card).ToArray(), p)).ToArray();
                        var values = offers.Select(c => BuildValue.Add(c.Card, p).Total).ToArray();
                        var legacy = offers.Select(c => HistoricalBuildValue.AddDetailed(c.Card, p).Total).ToArray();
                        int Best(double[] v) => v.Max() > 0 ? Array.IndexOf(v, v.Max()) : -1;
                        var best = Best(values); var old = Best(legacy);
                        rows.Add(new { run = Path.GetFileNameWithoutExtension(path), group, seats, floor,
                            alliedDecksKnown = !invalid.Any(other => other != id),
                            version = run.GetProperty("build_id").GetString(), character = p.Character.Id.Entry,
                            cards = offers.Select(c => c.Card.Id.Entry).ToArray(),
                            picked = offers.FindIndex(c => c.Picked), predicted = best, legacy = old, values,
                            guards = offers.Select(c => BuildValue.AddDetailed(c.Card, p).GuardRefusal).ToArray(),
                            commitmentLoad = offers.Select(c => DeckStrength.CommitmentLoad(c.Card, BuildValue.StableEnergy(p))).ToArray(),
                            coverage = before.Coverage, size = p.Deck.Cards.Count,
                            features = estimates.Select(e => new { cycle = e.Cycle-before.Cycle, hand = e.Hands-before.Hands,
                                prior = e.Prior-before.Prior, unknown = e.Unmodeled-before.Unmodeled, risk = e.Commitment-before.Commitment,
                                growth = e.Growth-before.Growth, robustness=e.Robustness-before.Robustness,
                                wither=e.Wither-before.Wither, chains=e.Chains-before.Chains }),
                            split = group[0] < 'c' ? "development" : "holdout" });
                    }
                }
                // Simultaneous rewards must all see pre-reward allied decks.
                // Applying seat 1's pick before scoring seat 2 leaks that label
                // into Bully/Sneaky synergy and makes JSON seat order matter.
                foreach (var stats in node.GetProperty("player_stats").EnumerateArray())
                {
                    var id = stats.GetProperty("player_id").GetUInt64();
                    if (!players.TryGetValue(id, out var p)) continue;
                    if (invalid.Contains(id)) continue;
                    foreach (var key in new[] { "cards_enchanted", "cards_transformed", "cards_removed", "bought_relics" })
                        if (stats.TryGetProperty(key, out var changes) && changes.GetArrayLength() > 0) invalid.Add(id);
                    // Acquisitions recorded separately from reward offers must be
                    // applied exactly once. Unknown history stops that seat.
                    foreach (var key in new[] { "cards_gained", "bought_cards", "bought_colorless" })
                    {
                        if (!stats.TryGetProperty(key, out var gained)) continue;
                        foreach (var entry in gained.EnumerateArray())
                        {
                            var card = Create(p, entry);
                            if (card is null) invalid.Add(id); else p.Deck.AddInternal(card);
                        }
                    }
                    if (stats.TryGetProperty("upgraded_cards", out var upgrades))
                        foreach (var entry in upgrades.EnumerateArray())
                        {
                            var cid = entry.GetString()!.Replace("CARD.", "");
                            var card = p.Deck.Cards.FirstOrDefault(c => c.Id.Entry == cid && c.IsUpgradable);
                            if (card is null) { invalid.Add(id); break; }
                            card.UpgradeInternal(); card.FinalizeUpgradeInternal();
                        }
                }
            }
            if (files % 40 == 0) Console.WriteLine($"COMMUNITY files={files} screens={rows.Count} excluded={excluded}");
        }
        var output = args.FirstOrDefault(a => a.StartsWith("--community-output="))?.Split('=', 2)[1]
            ?? Path.Combine(root, "work/unified-deck-strength/community-decisions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { files, uniqueGames = games.Count, duplicates, modifiedRuns, excluded,
            combatScale = DeckStrength.CombatScale, forecastWeight = DeckGrowth.ForecastWeight, riskWeight = DeckStrength.CommitmentWeight, rows }));
        Console.WriteLine($"COMMUNITY saved {rows.Count} decisions from {files} runs: {output}");
    }

    private static CardModel? Create(Player p, JsonElement entry)
    {
        var id = entry.ValueKind == JsonValueKind.String ? entry.GetString()
            : entry.TryGetProperty("id", out var v) ? v.GetString() : null;
        if (id is null) return null;
        var canonical = ModelDb.AllCards.FirstOrDefault(c => c.Id.Entry == id.Replace("CARD.", ""));
        if (canonical is null) return null;
        var card = p.RunState.CreateCard(canonical, p);
        var level = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("current_upgrade_level", out var u) ? u.GetInt32() : 0;
        for (var i = 0; i < level && card.IsUpgradable; i++) { card.UpgradeInternal(); card.FinalizeUpgradeInternal(); }
        return card;
    }
}

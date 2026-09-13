using System.Text.Json;
using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Cross-checks our structural card parser against an externally extracted
// card dump (Spire Codex `cards.json`). This is a validation aid, not a source
// of truth: the external dump is itself derived from the game, so a mismatch
// means one of the two extractions is wrong and has to be looked at. Nothing
// here ships the external data; the file is read from disk only when present.
internal static class CodexCrossCheckScenarios
{
    internal static void Run()
    {
        if (LocateJson() is not { } path)
        {
            Console.WriteLine("SKIP: no Spire Codex card dump found "
                + "(set COOPBOTS_CODEX_CARDS to cards.json or export-en.zip).");
            return;
        }

        var entries = Read(path);
        if (entries.Count == 0) { Console.WriteLine("SKIP: card dump was empty."); return; }

        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var party = new[] { bot };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "CODEX-CHECK"));
        bot.ResetCombatState(); combat.AddPlayer(bot);

        var byId = new Dictionary<string, CardModel>(StringComparer.Ordinal);
        foreach (var canonical in ModelDb.AllCards)
        {
            try { byId[canonical.Id.Entry] = canonical; }
            catch { /* a card whose id cannot be read cannot be matched */ }
        }

        // Publish the ids this build actually has, so the bake scripts can drop
        // references the installed game does not know about (the feed runs ahead
        // of it: it lists SCARE, which is not in this build). Best effort only.
        try
        {
            var idsPath = Path.Combine("work", "spire-codex", "local-card-ids.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(idsPath)!);
            File.WriteAllLines(idsPath, byId.Keys.OrderBy(id => id, StringComparer.Ordinal));
        }
        catch { /* a read-only checkout must not fail the suite */ }

        var missing = new List<string>();
        var costMismatch = new List<string>();
        var typeMismatch = new List<string>();
        var rarityMismatch = new List<string>();
        var hitsMismatch = new List<string>();
        var damageMismatch = new List<string>();
        var blockMismatch = new List<string>();
        var powersMismatch = new List<string>();
        var unparsed = new List<string>();
        int matched = 0, compared = 0, hitsCompared = 0, damageCompared = 0, blockCompared = 0, powersCompared = 0;
        int unplayableCost = 0;

        foreach (var entry in entries)
        {
            compared++;
            if (!byId.TryGetValue(entry.Id, out var canonical)) { missing.Add(entry.Id); continue; }
            CardModel card;
            CardProfile.Facts facts;
            try
            {
                card = combat.CreateCard(canonical, bot);
                facts = CardProfile.Of(card);
            }
            catch (Exception error) { unparsed.Add($"{entry.Id}({error.GetType().Name})"); continue; }
            matched++;

            if (entry.Cost is { } cost && cost != facts.Cost)
            {
                // The dump writes -1 for "cannot be played"; our facts clamp to 0.
                // That is a representation difference, not a disagreement.
                if (cost < 0) unplayableCost++;
                else costMismatch.Add($"{entry.Id}: codex={cost} ours={facts.Cost}");
            }
            if (entry.Type is { } type && !string.Equals(type, card.Type.ToString(), StringComparison.OrdinalIgnoreCase))
                typeMismatch.Add($"{entry.Id}: codex={type} ours={card.Type}");
            if (entry.Rarity is { } rarity && !string.Equals(rarity, card.Rarity.ToString(), StringComparison.OrdinalIgnoreCase))
                rarityMismatch.Add($"{entry.Id}: codex={rarity} ours={card.Rarity}");

            if (entry.HitCount is { } hits && hits > 0)
            {
                hitsCompared++;
                if (hits != facts.Hits) hitsMismatch.Add($"{entry.Id}: codex={hits} ours={facts.Hits}");
            }

            // Our damage figure multiplies out the hit count, so compare the
            // totals rather than the raw per-hit number.
            if (entry.Damage is { } damage && damage > 0)
            {
                damageCompared++;
                var expected = damage * Math.Max(1, entry.HitCount ?? 1);
                if (Math.Abs(expected - facts.Damage) > 0.001)
                    damageMismatch.Add($"{entry.Id}: codex={expected:F0} ours={facts.Damage:F0}");
            }
            if (entry.Block is { } block && block > 0)
            {
                blockCompared++;
                if (Math.Abs(block - facts.Block) > 0.001)
                    blockMismatch.Add($"{entry.Id}: codex={block:F0} ours={facts.Block:F0}");
            }
            if (entry.Powers.Count > 0)
            {
                powersCompared++;
                var ours = PowerNames(facts);
                var unknown = entry.Powers.Where(power => !ours.Contains(power)).ToList();
                // Only powers our analyzer models are comparable; an unmodelled
                // power means "we do not read this", not "we read it wrong".
                if (unknown.Count == entry.Powers.Count && entry.Powers.Count > 0)
                    powersMismatch.Add($"{entry.Id}: codex={string.Join('/', entry.Powers)} ours=none");
            }
        }

        Console.WriteLine($"CODEX cross-check: {matched}/{compared} cards matched by id"
            + $" ({missing.Count} missing, {unparsed.Count} unparsable).");
        Console.WriteLine($"  cost    : {matched - costMismatch.Count - unplayableCost} agree, "
            + $"{unplayableCost} unplayable (-1 vs clamped 0), {costMismatch.Count} differ");
        Console.WriteLine($"  type    : {matched - typeMismatch.Count - unparsed.Count} agree, {typeMismatch.Count} differ");
        Console.WriteLine($"  rarity  : {matched - rarityMismatch.Count - unparsed.Count} agree, {rarityMismatch.Count} differ");
        Console.WriteLine($"  hitcount: {hitsCompared - hitsMismatch.Count}/{hitsCompared} agree (multihit detection)");
        Console.WriteLine($"  damage  : {damageCompared - damageMismatch.Count}/{damageCompared} agree");
        Console.WriteLine($"  block   : {blockCompared - blockMismatch.Count}/{blockCompared} agree");
        Console.WriteLine($"  powers  : {powersCompared - powersMismatch.Count}/{powersCompared} read by our subset");
        Report("missing ids", missing);
        Report("unparsable", unparsed);
        Report("cost differs", costMismatch);
        Report("type differs", typeMismatch);
        Report("rarity differs", rarityMismatch);
        Report("hit count differs", hitsMismatch);
        Report("damage differs", damageMismatch);
        Report("block differs", blockMismatch);
        Report("powers not read", powersMismatch);
        Console.WriteLine("PASS: structural card parser cross-checked against the external dump (report above).");
    }

    private static IReadOnlySet<string> PowerNames(CardProfile.Facts facts)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The powers our analyzer actually reads. Strength/Dexterity/Focus are
        // read too, through PowerAmount, and they drive the archetype tags.
        if (facts.Vulnerable > 0) names.Add("Vulnerable");
        if (facts.Weak > 0) names.Add("Weak");
        if (facts.StrengthDown > 0) names.Add("StrengthLoss");
        if (facts.Poison > 0) names.Add("Poison");
        if (facts.Doom > 0) names.Add("Doom");
        if (facts.Scaling > 0)
        {
            names.Add("Strength");
            names.Add("Dexterity");
            names.Add("Focus");
        }
        return names;
    }

    private static void Report(string label, List<string> items)
    {
        if (items.Count == 0) return;
        Console.WriteLine($"  {label} ({items.Count}): " + string.Join(" | ", items.Take(12)));
    }

    // The dump is a local development input, never shipped: look for it via an
    // explicit override, then next to the build output, then upwards.
    private static string? LocateJson()
    {
        var explicitPath = Environment.GetEnvironmentVariable("COOPBOTS_CODEX_CARDS");
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) return explicitPath;
        foreach (var root in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(root);
            for (var depth = 0; depth < 6 && directory is not null; depth++)
            {
                foreach (var name in new[] { "cards.json", "export-en.zip" })
                {
                    var candidate = Path.Combine(directory.FullName, "work", "spire-codex", name);
                    if (File.Exists(candidate)) return candidate;
                    candidate = Path.Combine(directory.FullName, "work", "spire-codex", "export", name);
                    if (File.Exists(candidate)) return candidate;
                }
                directory = directory.Parent;
            }
        }
        return null;
    }

    private sealed record Entry(
        string Id, int? Cost, string? Type, string? Rarity, double? Damage, double? Block,
        int? HitCount, IReadOnlyList<string> Powers);

    private static List<Entry> Read(string path)
    {
        using var stream = OpenCards(path);
        using var document = JsonDocument.Parse(stream);
        var result = new List<Entry>();
        foreach (var card in document.RootElement.EnumerateArray())
        {
            var powers = new List<string>();
            if (card.TryGetProperty("powers_applied", out var applied) && applied.ValueKind == JsonValueKind.Array)
                foreach (var power in applied.EnumerateArray())
                    if (power.TryGetProperty("power", out var name) && name.GetString() is { } text)
                        powers.Add(text);
            result.Add(new Entry(
                Id: Text(card, "id") ?? "",
                Cost: Number(card, "cost") is { } cost ? (int)cost : null,
                Type: Text(card, "type"),
                Rarity: Text(card, "rarity"),
                Damage: Number(card, "damage"),
                Block: Number(card, "block"),
                HitCount: Number(card, "hit_count") is { } hits ? (int)hits : null,
                Powers: powers));
        }
        return result;
    }

    private static Stream OpenCards(string path)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return File.OpenRead(path);
        var archive = System.IO.Compression.ZipFile.OpenRead(path);
        var entry = archive.GetEntry("cards.json")
            ?? throw new InvalidOperationException("cards.json is not in the archive");
        // Copy out so closing the archive cannot invalidate the parse.
        var buffer = new MemoryStream();
        using (var source = entry.Open()) source.CopyTo(buffer);
        buffer.Position = 0;
        archive.Dispose();
        return buffer;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static double? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : null;
}

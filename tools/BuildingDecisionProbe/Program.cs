using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoopBots;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

namespace BuildingDecisionProbe;

/// <summary>
/// Offline adapter that exports the current production BuildValue.Add /
/// BestReward(allowSkip:true) decisions for two explicitly labelled PROXY
/// SILENT fixtures.
///
/// It is not a battle instrument and it starts no combat. It builds the real
/// game models headlessly (the same four stubs tests/PatchSmoke installs, plus a
/// relic-free configured fixture), constructs the deck from ModelDb, and calls
/// the internal CoopBots.Building.BuildValue by reflection. Nothing scores a
/// card here; every score comes from the same production method the reward path
/// calls, so the fixture output cannot drift from the mod's own arithmetic.
///
/// The run carries the serialized ascension (RunState.CreateForTest's
/// ascensionLevel), and the player's Character stays the real SILENT. Because
/// the headless process has no SaveManager, the one line that cannot run is the
/// starting-relic acquisition; it is skipped by prefix and the fixture is
/// deliberately relic-free, which is recorded, not hidden.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions InputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int Main(string[] args)
    {
        try
        {
            string outPath = GetArg(args, "--out")
                ?? throw new ArgumentException("--out <path> is required");
            string fixturesPath = Path.GetFullPath(GetArg(args, "--fixtures")
                ?? throw new ArgumentException("--fixtures <path> is required"));
            string? repoRoot = GetArg(args, "--repo-root");

            if (!File.Exists(fixturesPath))
                throw new FileNotFoundException("fixture spec not found", fixturesPath);

            ProbeSpec spec = JsonSerializer.Deserialize<ProbeSpec>(
                File.ReadAllText(fixturesPath, Encoding.UTF8), InputOptions)
                ?? throw new InvalidDataException("fixture spec is empty");
            ValidateSpec(spec);

            EnsureHeadless();
            BuildValueReflection buildValue = BuildValueReflection.Bind();

            var fixtureResults = new List<object?>();
            ulong netId = 100;
            foreach (FixtureSpec fixture in spec.Fixtures)
            {
                fixtureResults.Add(EvaluateFixture(fixture, spec, buildValue, netId, repoRoot));
                netId++;
                Console.WriteLine($"probe {fixture.Id}: evaluated {spec.Candidates.Count} candidates plus skip");
            }

            var output = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["tool"] = "BuildingDecisionProbe",
                ["assemblyName"] = typeof(Program).Assembly.GetName().Name,
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture),
                ["runtime"] = CollectRuntimeProvenance(fixturesPath, spec),
                ["buildValue"] = new Dictionary<string, object?>
                {
                    ["type"] = "CoopBots.Building.BuildValue",
                    ["assembly"] = typeof(ModEntry).Assembly.GetName().Name,
                    ["access"] = "reflection",
                    ["internal"] = true,
                    ["addSignature"] = "Valuation Add(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)",
                    ["bestRewardSignature"] = "int BestReward(Player player, IReadOnlyList<CardModel> candidates, bool allowSkip = true)",
                    ["skipBaseline"] = "BuildValue.Add compares every candidate against a do-nothing value of 0; a candidate must exceed 0 to beat skip",
                },
                ["fixtures"] = fixtureResults,
                ["limitations"] = Limitations(),
            };

            WriteExclusive(outPath, output);
            Console.WriteLine($"wrote {Path.GetFullPath(outPath)}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void ValidateSpec(ProbeSpec spec)
    {
        if (spec.SchemaVersion != 1)
            throw new InvalidDataException($"unsupported fixture schema version {spec.SchemaVersion}");
        if (spec.Fixtures.Count != 2)
            throw new InvalidDataException($"expected exactly two proxy fixtures, got {spec.Fixtures.Count}");
        if (spec.Candidates.Count == 0)
            throw new InvalidDataException("the spec declares no candidates");
        if (spec.Candidates.Distinct(StringComparer.Ordinal).Count() != spec.Candidates.Count)
            throw new InvalidDataException("the spec declares duplicate candidate ids");
        foreach (FixtureSpec fixture in spec.Fixtures)
        {
            if (!fixture.Proxy || !string.Equals(fixture.Label, "PROXY", StringComparison.Ordinal))
                throw new InvalidDataException($"fixture {fixture.Id} must be explicitly labelled PROXY");
            if (!string.Equals(fixture.CharacterId, "SILENT", StringComparison.Ordinal))
                throw new InvalidDataException($"fixture {fixture.Id} must be SILENT, got {fixture.CharacterId}");
            if (fixture.Ascension != 10)
                throw new InvalidDataException($"fixture {fixture.Id} must be ascension 10");
            if (fixture.MaxHp != 70 || fixture.CurrentHp != 70)
                throw new InvalidDataException($"fixture {fixture.Id} must be 70/70 HP");
            if (fixture.CharacterCards.Count != fixture.CharacterCardUpgrades.Count)
                throw new InvalidDataException($"fixture {fixture.Id} characterCard ids and upgrade levels differ in length");
            if (fixture.ColorlessCards.Count != fixture.ColorlessCardUpgrades.Count)
                throw new InvalidDataException($"fixture {fixture.Id} colorlessCard ids and upgrade levels differ in length");
            if (fixture.IncludeStartingRelics)
                throw new InvalidDataException(
                    $"fixture {fixture.Id} requests starting relics, but the headless relic-free ablation cannot populate them; "
                    + "failing instead of silently dropping them");
            if (fixture.Relics.Count != 0 || fixture.Potions.Count != 0)
                throw new InvalidDataException($"fixture {fixture.Id} must have empty relics and potions (an explicit ablation)");
            if (!fixture.IncludeAscendersBane)
                throw new InvalidDataException($"fixture {fixture.Id} must request exactly one Ascender's Bane");
            if (fixture.SourceInput is not null && fixture.IncludeStartingDeck)
                throw new InvalidDataException(
                    $"fixture {fixture.Id} combines a historical source input with includeStartingDeck; "
                    + "the source deck is a full deck, so starting-deck expansion would double count it");
        }
    }

    private static Dictionary<string, object?> EvaluateFixture(
        FixtureSpec spec, ProbeSpec probe, BuildValueReflection buildValue, ulong netId, string? repoRoot)
    {
        CharacterModel character = ModelDb.Character<Silent>();
        if (!string.Equals(character.Id.Entry, "SILENT", StringComparison.Ordinal))
            throw new InvalidDataException($"ModelDb character identity is {character.Id.Entry}, expected SILENT");
        if (character.StartingHp != spec.MaxHp)
            throw new InvalidDataException(
                $"fixture {spec.Id}: SILENT's character starting HP is {character.StartingHp}, but the spec says {spec.MaxHp}; "
                + "the scenario HP no longer matches the harness default and would change real-run meaning");

        Player player = Player.CreateForNewRun<Silent>(UnlockState.all, netId);
        string verifiedCharacter = player.Character.Id.Entry;
        if (!string.Equals(verifiedCharacter, "SILENT", StringComparison.Ordinal))
            throw new InvalidDataException($"created player identity is {verifiedCharacter}, expected SILENT");

        // The player factory populates the real SILENT starting deck before the
        // player joins a run. Capture it now: the late fixture clears it and
        // rebuilds, the starter fixture keeps it. Either way the actual deck is
        // verified against ModelDb below.
        List<CardModel> factoryStarter = player.Deck.Cards.ToList();

        var party = new[] { player };
        RunState runState = RunState.CreateForTest(party, ascensionLevel: spec.Ascension, seed: spec.Seed);
        if (runState.AscensionLevel != spec.Ascension)
            throw new InvalidDataException(
                $"fixture {spec.Id}: actual run ascension is {runState.AscensionLevel}, expected {spec.Ascension}");
        if (spec.ActIndex < 0 || spec.ActIndex >= runState.Acts.Count)
            throw new InvalidDataException($"fixture {spec.Id} actIndex {spec.ActIndex} is outside the run's act list");
        runState.CurrentActIndex = spec.ActIndex;
        if (runState.CurrentActIndex != spec.ActIndex)
            throw new InvalidDataException(
                $"fixture {spec.Id}: actual run act is {runState.CurrentActIndex}, expected {spec.ActIndex}");

        var combat = new CombatState(runState: runState);
        player.ResetCombatState();
        combat.AddPlayer(player);
        player.Creature.SetMaxHpInternal(spec.MaxHp);
        player.Creature.SetCurrentHpInternal(spec.CurrentHp);
        if (player.Creature.MaxHp != spec.MaxHp || player.Creature.CurrentHp != spec.CurrentHp)
            throw new InvalidDataException(
                $"fixture {spec.Id}: actual HP is {player.Creature.CurrentHp}/{player.Creature.MaxHp}, expected {spec.CurrentHp}/{spec.MaxHp}");

        List<CardModel> resolvedStartingDeck;
        if (spec.IncludeStartingDeck)
        {
            // The factory normally populates the real SILENT starting deck. If it
            // ever stops, populate it explicitly from ModelDb rather than scoring
            // whatever happens to be there.
            if (!ExpectedStarterMatches(character, factoryStarter))
            {
                ClearDeck(player, runState);
                AddStartingDeck(character, runState, player);
                resolvedStartingDeck = player.Deck.Cards.ToList();
                if (!ExpectedStarterMatches(character, resolvedStartingDeck))
                    throw new InvalidDataException(
                        $"fixture {spec.Id}: the deck does not equal ModelDb.Silent.StartingDeck even after explicit population");
            }
            else
            {
                resolvedStartingDeck = factoryStarter;
            }
        }
        else
        {
            ClearDeck(player, runState);
            resolvedStartingDeck = new List<CardModel>();
        }

        AddCards(spec, runState, player, spec.CharacterCards, spec.CharacterCardUpgrades, "character");
        AddCards(spec, runState, player, spec.ColorlessCards, spec.ColorlessCardUpgrades, "colorless");
        EnsureAscendersBane(runState, player);

        List<CardModel> deck = player.Deck.Cards.ToList();
        int baneCount = deck.Count(card => card.Id.Entry == "ASCENDERS_BANE");
        if (baneCount != 1)
            throw new InvalidDataException($"fixture {spec.Id} deck holds {baneCount} ASCENDERS_BANE, expected exactly 1");
        if (player.Relics.Count != 0)
            throw new InvalidDataException($"fixture {spec.Id} unexpectedly holds {player.Relics.Count} relics");
        if (player.Potions.Any())
            throw new InvalidDataException($"fixture {spec.Id} unexpectedly holds potions");

        var expectedDeck = new List<(string Id, int Upgrade)>();
        expectedDeck.AddRange(Resolved(resolvedStartingDeck));
        expectedDeck.AddRange(SpecCards(spec.CharacterCards, spec.CharacterCardUpgrades));
        expectedDeck.AddRange(SpecCards(spec.ColorlessCards, spec.ColorlessCardUpgrades));
        expectedDeck.Add(("ASCENDERS_BANE", 0));
        var actualDeck = Resolved(deck);
        if (!SameCards(expectedDeck, actualDeck))
            throw new InvalidDataException(
                $"fixture {spec.Id}: actual deck does not equal the declared starting deck + cards + one Ascender's Bane");

        Dictionary<string, object?>? sourceValidation = spec.SourceInput is null
            ? null
            : ValidateSourceInput(spec, repoRoot, deck);

        List<CardModel> rewardPool = character.CardPool.AllCards
            .Where(card => card.Rarity is CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare)
            .Where(card => card.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)
            .ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidateCards = new List<CardModel>();
        var candidateScores = new List<double>();
        var candidateResults = new List<object?>();
        foreach (string id in probe.Candidates)
        {
            if (!seen.Add(id))
                throw new InvalidDataException($"duplicate candidate id {id}");
            CardModel canonical = rewardPool.FirstOrDefault(card => string.Equals(card.Id.Entry, id, StringComparison.Ordinal))
                ?? throw new InvalidDataException($"candidate {id} is not in the current SILENT reward pool resolved from ModelDb; refusing to substitute");
            CardModel card = runState.CreateCard(canonical, player);
            if (card.IsUpgraded)
                throw new InvalidDataException($"candidate {id} was created upgraded; offers must be unupgraded");
            (double total, string reason) = buildValue.InvokeAdd(card, player);
            candidateCards.Add(card);
            candidateScores.Add(total);
            candidateResults.Add(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["canonicalId"] = card.Id.Entry,
                ["cardPool"] = "character",
                ["cardType"] = card.Type.ToString(),
                ["rarity"] = card.Rarity.ToString(),
                ["upgrade"] = card.CurrentUpgradeLevel,
                ["score"] = total,
                ["finite"] = double.IsFinite(total),
                ["reason"] = reason,
            });
        }

        int argmax = -1;
        double argmaxValue = 0.0;
        for (int index = 0; index < candidateScores.Count; index++)
        {
            if (candidateScores[index] > argmaxValue)
            {
                argmaxValue = candidateScores[index];
                argmax = index;
            }
        }
        int bestRewardAllowSkip = buildValue.InvokeBestReward(player, candidateCards, allowSkip: true);
        int bestRewardNoSkip = buildValue.InvokeBestReward(player, candidateCards, allowSkip: false);
        if (argmax != bestRewardAllowSkip)
            throw new InvalidDataException(
                $"fixture {spec.Id}: argmax including skip is {argmax}, but BuildValue.BestReward(allowSkip:true) returned {bestRewardAllowSkip}");

        var selectionCharacterIds = spec.CharacterCards.ToList();
        var selectionColorlessIds = spec.ColorlessCards.ToList();

        return new Dictionary<string, object?>
        {
            ["id"] = spec.Id,
            ["label"] = spec.Label,
            ["stateKind"] = spec.StateKind,
            ["proxy"] = true,
            ["proxyReason"] = spec.ProxyReason,
            ["seed"] = spec.Seed,
            ["characterId"] = spec.CharacterId,
            ["verifiedCharacterId"] = verifiedCharacter,
            ["ascension"] = spec.Ascension,
            ["verifiedAscension"] = runState.AscensionLevel,
            ["actIndex"] = spec.ActIndex,
            ["verifiedActIndex"] = runState.CurrentActIndex,
            ["encounterId"] = spec.EncounterId,
            ["encounterSuitability"] = spec.EncounterSuitability,
            ["maxHp"] = spec.MaxHp,
            ["currentHp"] = spec.CurrentHp,
            ["characterStartingHp"] = character.StartingHp,
            ["verifiedMaxHp"] = player.Creature.MaxHp,
            ["verifiedCurrentHp"] = player.Creature.CurrentHp,
            ["relics"] = spec.Relics,
            ["potions"] = spec.Potions,
            ["relicsEmptyByDesign"] = true,
            ["potionsEmptyByDesign"] = true,
            ["ascendersBaneRequested"] = spec.IncludeAscendersBane,
            ["ascendersBaneCount"] = baneCount,
            ["startingDeck"] = spec.IncludeStartingDeck ? "character-starting-deck" : "cleared",
            ["resolvedStartingDeck"] = resolvedStartingDeck.Select(Entry).ToList(),
            ["deckSize"] = deck.Count,
            ["deck"] = deck.Select(Entry).ToList(),
            ["context"] = new Dictionary<string, object?>
            {
                ["actIndex"] = runState.CurrentActIndex,
                ["ascension"] = runState.AscensionLevel,
                ["seed"] = spec.Seed,
                ["encounterId"] = spec.EncounterId,
                ["mapContext"] = "none; RunDepth.ShopAhead falls back to true and RunDepth.Act reads CurrentActIndex only",
                ["sourceInput"] = ResolveSourceInput(spec, repoRoot),
                ["sourceInputSha256"] = ResolveSourceInputHash(spec, repoRoot),
                ["sourceInputValidation"] = sourceValidation,
            },
            ["selection"] = new Dictionary<string, object?>
            {
                ["includeStartingDeck"] = spec.IncludeStartingDeck,
                ["includeStartingRelics"] = spec.IncludeStartingRelics,
                ["includeAscendersBane"] = spec.IncludeAscendersBane,
                ["characterCards"] = new Dictionary<string, object?>
                {
                    ["ids"] = selectionCharacterIds,
                    ["upgradeLevelsPerCard"] = spec.CharacterCardUpgrades,
                },
                ["colorlessCards"] = new Dictionary<string, object?>
                {
                    ["ids"] = selectionColorlessIds,
                    ["upgradeLevelsPerCard"] = spec.ColorlessCardUpgrades,
                },
            },
            ["sourceInputValidation"] = sourceValidation,
            ["missingContext"] = spec.MissingContext,
            ["candidates"] = candidateResults,
            ["skip"] = new Dictionary<string, object?>
            {
                ["kind"] = "skip",
                ["index"] = -1,
                ["value"] = 0.0,
                ["reason"] = "skip baseline: BuildValue.Add must exceed 0 to beat doing nothing",
            },
            ["bestRewardAllowSkip"] = bestRewardAllowSkip,
            ["bestRewardNoSkip"] = bestRewardNoSkip,
            ["argmaxIncludingSkip"] = argmax,
            ["argmaxValue"] = argmaxValue,
            ["argmaxMatchesBestReward"] = true,
        };
    }

    private static Dictionary<string, object?> Entry(CardModel card)
        => new() { ["id"] = card.Id.Entry, ["upgrade"] = card.CurrentUpgradeLevel };

    private static List<(string Id, int Upgrade)> Resolved(IEnumerable<CardModel> cards)
        => cards.Select(card => (card.Id.Entry, card.CurrentUpgradeLevel)).ToList();

    private static List<(string Id, int Upgrade)> SpecCards(IReadOnlyList<string> ids, IReadOnlyList<int> upgrades)
        => ids.Select((id, index) => (id, upgrades[index])).ToList();

    private static bool ExpectedStarterMatches(CharacterModel character, IReadOnlyList<CardModel> actualStarter)
    {
        var expected = character.StartingDeck.Select(card => (card.Id.Entry, card.CurrentUpgradeLevel)).ToList();
        return SameCards(expected, Resolved(actualStarter));
    }

    private static bool SameCards(
        IEnumerable<(string Id, int Upgrade)> expected,
        IEnumerable<(string Id, int Upgrade)> actual)
        => expected.OrderBy(entry => entry.Id, StringComparer.Ordinal).ThenBy(entry => entry.Upgrade)
            .SequenceEqual(actual.OrderBy(entry => entry.Id, StringComparer.Ordinal).ThenBy(entry => entry.Upgrade));

    private static void ClearDeck(Player player, RunState runState)
    {
        foreach (CardModel card in player.Deck.Cards.ToArray())
        {
            player.Deck.RemoveInternal(card);
            runState.RemoveCard(card);
        }
    }

    private static void AddStartingDeck(CharacterModel character, RunState runState, Player player)
    {
        foreach (CardModel canonical in character.StartingDeck)
            player.Deck.AddInternal(runState.CreateCard(canonical, player));
    }

    private static void AddCards(
        FixtureSpec spec, RunState runState, Player player,
        IReadOnlyList<string> ids, IReadOnlyList<int> upgrades, string block)
    {
        for (int index = 0; index < ids.Count; index++)
        {
            string id = ids[index];
            int level = upgrades[index];
            CardModel canonical = FindCard(id)
                ?? throw new InvalidDataException($"fixture {spec.Id}: {block} card {id} is not in the current ModelDb; refusing to drop it silently");
            CardModel card = runState.CreateCard(canonical, player);
            if (level < 0 || level > card.MaxUpgradeLevel)
                throw new InvalidDataException(
                    $"fixture {spec.Id}: card {id} requested upgrade level {level}, but MaxUpgradeLevel is {card.MaxUpgradeLevel}");
            for (int upgrade = 0; upgrade < level; upgrade++)
            {
                card.UpgradeInternal();
                card.FinalizeUpgradeInternal();
            }
            player.Deck.AddInternal(card);
        }
    }

    private static void EnsureAscendersBane(RunState runState, Player player)
    {
        int existing = player.Deck.Cards.Count(card => card.Id.Entry == "ASCENDERS_BANE");
        if (existing == 1) return;
        if (existing > 1)
            throw new InvalidDataException($"deck already holds {existing} copies of Ascender's Bane");
        CardModel bane = runState.CreateCard(ModelDb.Card<AscendersBane>(), player);
        player.Deck.AddInternal(bane);
    }

    private static CardModel? FindCard(string id)
        => ModelDb.AllCards.FirstOrDefault(card => string.Equals(card.Id.Entry, id, StringComparison.Ordinal));

    private static Dictionary<string, object?> ValidateSourceInput(FixtureSpec spec, string? repoRoot, IReadOnlyList<CardModel> actualDeck)
    {
        string path = ResolveSourceInput(spec, repoRoot)
            ?? throw new InvalidDataException($"fixture {spec.Id}: source input is set but could not be resolved");
        if (!File.Exists(path))
            throw new FileNotFoundException($"fixture {spec.Id}: source input not found", path);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        JsonElement root = document.RootElement;
        (List<string> sourceCharacterIds, List<int> sourceCharacterUpgrades) = ReadCardBlock(root, "characterCards");
        (List<string> sourceColorlessIds, List<int> sourceColorlessUpgrades) = ReadCardBlock(root, "colorlessCards");

        if (!sourceCharacterIds.SequenceEqual(spec.CharacterCards)
            || !sourceCharacterUpgrades.SequenceEqual(spec.CharacterCardUpgrades))
            throw new InvalidDataException(
                $"fixture {spec.Id}: source input characterCards/upgrades do not match the spec declaration");
        if (!sourceColorlessIds.SequenceEqual(spec.ColorlessCards)
            || !sourceColorlessUpgrades.SequenceEqual(spec.ColorlessCardUpgrades))
            throw new InvalidDataException(
                $"fixture {spec.Id}: source input colorlessCards/upgrades do not match the spec declaration");

        var sourceDeck = sourceCharacterIds.Select((id, index) => (id, sourceCharacterUpgrades[index]))
            .Concat(sourceColorlessIds.Select((id, index) => (id, sourceColorlessUpgrades[index])))
            .ToList();
        var actualWithoutBane = actualDeck
            .Where(card => card.Id.Entry != "ASCENDERS_BANE")
            .Select(card => (card.Id.Entry, card.CurrentUpgradeLevel))
            .ToList();
        if (!SameCards(sourceDeck, actualWithoutBane))
            throw new InvalidDataException(
                $"fixture {spec.Id}: the actual constructed deck does not match the historical source input deck/upgrades");

        return new Dictionary<string, object?>
        {
            ["path"] = path,
            ["sha256"] = Sha256File(path),
            ["characterCards"] = sourceCharacterIds.Count,
            ["colorlessCards"] = sourceColorlessIds.Count,
            ["matchesSpec"] = true,
            ["matchesActualDeck"] = true,
        };
    }

    private static (List<string> Ids, List<int> Upgrades) ReadCardBlock(JsonElement root, string block)
    {
        if (!root.TryGetProperty(block, out JsonElement element) || element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"source input is missing the {block} block");
        if (!element.TryGetProperty("ids", out JsonElement idsElement) || idsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"source input {block} has no ids array");
        if (!element.TryGetProperty("upgradeLevelsPerCard", out JsonElement upgradesElement) || upgradesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"source input {block} has no upgradeLevelsPerCard array");

        var ids = idsElement.EnumerateArray().Select(item => item.GetString()
            ?? throw new InvalidDataException($"source input {block} has a null card id")).ToList();
        var upgrades = upgradesElement.EnumerateArray().Select(item => item.GetInt32()).ToList();
        if (ids.Count != upgrades.Count)
            throw new InvalidDataException($"source input {block} ids and upgradeLevelsPerCard differ in length");
        return (ids, upgrades);
    }

    private static string? ResolveSourceInput(FixtureSpec spec, string? repoRoot)
    {
        if (string.IsNullOrWhiteSpace(spec.SourceInput)) return null;
        return Path.IsPathRooted(spec.SourceInput) || string.IsNullOrEmpty(repoRoot)
            ? spec.SourceInput
            : Path.Combine(repoRoot, spec.SourceInput);
    }

    private static string? ResolveSourceInputHash(FixtureSpec spec, string? repoRoot)
    {
        string? path = ResolveSourceInput(spec, repoRoot);
        return path is null ? null : Sha256File(path);
    }

    private static Dictionary<string, object?> CollectRuntimeProvenance(string fixturesPath, ProbeSpec spec)
    {
        Assembly probe = typeof(Program).Assembly;
        Assembly coopBots = typeof(ModEntry).Assembly;
        Assembly sts2 = typeof(Player).Assembly;
        Assembly godot = typeof(Godot.Color).Assembly;
        Assembly harmony = typeof(Harmony).Assembly;
        string[] internalsVisibleTo = coopBots.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        return new Dictionary<string, object?>
        {
            ["targetFramework"] = "net9.0",
            ["gameDataDir"] = Path.GetDirectoryName(sts2.Location),
            ["fixturesSpec"] = fixturesPath,
            ["fixturesSpecSha256"] = Sha256File(fixturesPath),
            ["probeAssembly"] = DescribeAssembly(probe),
            ["coopBotsAssembly"] = DescribeAssembly(coopBots),
            ["sts2"] = DescribeFile(sts2.Location),
            ["godotSharp"] = DescribeFile(godot.Location),
            ["harmony"] = DescribeFile(harmony.Location),
            ["coopBotsInternalsVisibleTo"] = internalsVisibleTo,
            ["sourceInputs"] = spec.Fixtures
                .Select(fixture => fixture.SourceInput)
                .Where(path => path is not null)
                .ToArray(),
        };
    }

    private static Dictionary<string, object?> DescribeAssembly(Assembly assembly)
    {
        var info = new Dictionary<string, object?>
        {
            ["name"] = assembly.GetName().Name,
            ["version"] = assembly.GetName().Version?.ToString(),
            ["path"] = assembly.Location,
        };
        info["sha256"] = Sha256File(assembly.Location);
        return info;
    }

    private static Dictionary<string, object?> DescribeFile(string path) => new()
    {
        ["path"] = path,
        ["sha256"] = Sha256File(path),
    };

    private static string? Sha256File(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string[] Limitations() =>
    [
        "Both states are PROXY fixtures, not observed reward screens; no result here is an observed pick.",
        "Relics and potions are empty by design (an ablation); removing them changes what a real run means.",
        "No map/floor context is present, so RunDepth.ShopAhead falls back to true and RunDepth.Act reads actIndex only.",
        "Scores are the current production BuildValue values for these constructed states; they are not strength labels and are not compared across budget settings here.",
        "The adapter starts no combat and launches no game; it only constructs models and evaluates BuildValue.",
    ];

    private static string? GetArg(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        return null;
    }

    private static void WriteExclusive(string path, object payload)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string json = JsonSerializer.Serialize(payload, OutputOptions);
        using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(json);
    }

    // The console host the whole probe runs inside, copied from tests/PatchSmoke:
    // there is no Godot native host, and the game models reach for save, logging
    // and combat-state entry points on their way through. Stubbing exactly those,
    // plus the relic acquisition that needs a SaveManager, is what lets the real
    // Silent model run headlessly with an explicitly relic-free fixture.
    private static void EnsureHeadless()
    {
        TestMode.IsOn = true;
        typeof(ModManager).GetProperty("State")!.SetValue(null, ModManagerState.Skipped);
        var harness = new Harmony("coopbots.building.probe");
        harness.Patch(AccessTools.PropertyGetter(typeof(SaveManager), "Instance"),
            prefix: new HarmonyMethod(typeof(Program), nameof(NoSaveAccess)));
        harness.Patch(AccessTools.PropertyGetter(typeof(CombatManager), "IsInProgress"),
            prefix: new HarmonyMethod(typeof(Program), nameof(CombatInProgress)));
        harness.Patch(AccessTools.Method(typeof(ConsoleLogPrinter), "Print"),
            prefix: new HarmonyMethod(typeof(Program), nameof(SuppressNativePrint)));
        harness.Patch(AccessTools.Method(typeof(Logger), "GetIsRunningFromGodotEditor"),
            prefix: new HarmonyMethod(typeof(Program), nameof(ConsoleHost)));
        // Starting relics go through SaveManager.Instance.MarkRelicAsSeen, which the
        // null save host cannot serve. The fixtures are relic-free by design, so the
        // acquisition is the explicit ablation rather than a silent drop; the player
        // character stays SILENT and is verified against ModelDb above.
        harness.Patch(AccessTools.Method(typeof(Player), "PopulateStartingRelics"),
            prefix: new HarmonyMethod(typeof(Program), nameof(SkipStartingRelics)));
        MegaCrit.Sts2.Core.Modding.AssemblyInfo.Init();
        ModelDb.Init(typeof(AbstractModel).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(AbstractModel))
                && type.GetConstructor(Type.EmptyTypes) is not null)
            .ToArray());
        ModelIdSerializationCache.Init();
        ModelDb.InitIds();
    }

    private static bool NoSaveAccess(ref SaveManager? __result) { __result = null; return false; }
    private static bool CombatInProgress(ref bool __result) { __result = true; return false; }
    private static bool SuppressNativePrint() => false;
    private static bool ConsoleHost(ref bool __result) { __result = false; return false; }
    private static bool SkipStartingRelics() => false;
}

/// <summary>Reflection binding to the internal production BuildValue methods.</summary>
internal sealed class BuildValueReflection
{
    private readonly MethodInfo _add;
    private readonly MethodInfo _bestReward;

    private BuildValueReflection(Type type, MethodInfo add, MethodInfo bestReward)
    {
        Type = type;
        _add = add;
        _bestReward = bestReward;
    }

    public Type Type { get; }

    public static BuildValueReflection Bind()
    {
        Assembly assembly = typeof(ModEntry).Assembly;
        Type type = assembly.GetType("CoopBots.Building.BuildValue", throwOnError: true)
            ?? throw new InvalidOperationException("CoopBots.Building.BuildValue was not found in the CoopBots assembly");
        MethodInfo add = type.GetMethod("Add", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildValue.Add was not found by reflection");
        MethodInfo bestReward = type.GetMethod("BestReward", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildValue.BestReward was not found by reflection");
        return new BuildValueReflection(type, add, bestReward);
    }

    public (double Total, string Reason) InvokeAdd(CardModel card, Player player)
    {
        object? valuation;
        try
        {
            valuation = _add.Invoke(null, new object?[] { card, player, null });
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
        if (valuation is null)
            throw new InvalidOperationException("BuildValue.Add returned null");
        Type valuationType = valuation.GetType();
        object? total = valuationType.GetProperty("Total")?.GetValue(valuation)
            ?? throw new InvalidOperationException("BuildValue.Add result has no Total");
        string? reason = valuationType.GetProperty("Reason")?.GetValue(valuation) as string
            ?? throw new InvalidOperationException("BuildValue.Add result has no Reason");
        double value = Convert.ToDouble(total, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value))
            throw new InvalidDataException($"BuildValue.Add returned a non-finite score {value}");
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidDataException("BuildValue.Add returned an empty reason");
        return (value, reason);
    }

    public int InvokeBestReward(Player player, IReadOnlyList<CardModel> candidates, bool allowSkip)
    {
        try
        {
            return (int)_bestReward.Invoke(null, new object?[] { player, candidates, allowSkip })!;
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
}

internal sealed class ProbeSpec
{
    public int SchemaVersion { get; set; }
    public string? Note { get; set; }
    public List<string> Candidates { get; set; } = new();
    public List<FixtureSpec> Fixtures { get; set; } = new();
}

internal sealed class FixtureSpec
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string StateKind { get; set; } = "";
    public bool Proxy { get; set; }
    public string ProxyReason { get; set; } = "";
    public string Seed { get; set; } = "";
    public string CharacterId { get; set; } = "";
    public int Ascension { get; set; }
    public int ActIndex { get; set; }
    public string EncounterId { get; set; } = "";
    public string EncounterSuitability { get; set; } = "";
    public int MaxHp { get; set; }
    public int CurrentHp { get; set; }
    public bool IncludeStartingDeck { get; set; }
    public bool IncludeStartingRelics { get; set; }
    public bool IncludeAscendersBane { get; set; }
    public List<string> CharacterCards { get; set; } = new();
    public List<int> CharacterCardUpgrades { get; set; } = new();
    public List<string> ColorlessCards { get; set; } = new();
    public List<int> ColorlessCardUpgrades { get; set; } = new();
    public List<string> Relics { get; set; } = new();
    public List<string> Potions { get; set; } = new();
    public string? SourceInput { get; set; }
    public string? SourceInputSha256 { get; set; }
    public List<string> MissingContext { get; set; } = new();
}

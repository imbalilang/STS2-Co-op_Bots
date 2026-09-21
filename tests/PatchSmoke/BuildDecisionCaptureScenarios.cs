using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoopBots;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;

/// <summary>
/// Synthetic, local tests for <see cref="BuildDecisionCapture"/>. These drive
/// the real game models headlessly through the game's own serializer, but they
/// are NOT a live-game capture: no reward screen, no run, no deployment. They
/// assert default-off behavior, the JSONL writer bounds, the pre-state snapshot
/// and the selection linkage.
/// </summary>
internal static class BuildDecisionCaptureScenarios
{
    private static int _passes;

    internal static void Run(string[] args)
    {
        TestEnvironment.Ensure();

        var outIndex = Array.IndexOf(args, "--out");
        var outDir = outIndex >= 0 && outIndex + 1 < args.Length
            ? Path.GetFullPath(args[outIndex + 1])
            : Path.Combine(AppContext.BaseDirectory, "capture-out");
        Directory.CreateDirectory(outDir);

        try
        {
            DisabledReturnsBeforePlayer(outDir);
            BlankDirectoryRejected(outDir);
            EnabledCapturesSchemaAndRoundtrip(outDir);
            DistinctDecisionIds(outDir);
            RunIdentityIsStableAndDistinct(outDir);
            PreStateIsImmutableAfterMutation(outDir);
            OutcomeIsConsumedOnce(outDir);
            ValidatorRejectsMalformed(outDir);
            ItemStateSurvivesCapture(outDir);
            CardEnchantmentSurvivesCapture(outDir);
            SilentCharacterSmoke(outDir);
            RecordAndByteCaps(outDir);
            UnwritableTargetFailsSafely(outDir);
            EnabledAndDisabledAgree(outDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COOPBOTS_CAPTURE_DECISIONS", null);
            Environment.SetEnvironmentVariable("COOPBOTS_CAPTURE_DIR", null);
            BuildDecisionCapture.ResetForTest();
        }

        Console.WriteLine($"PASS: {_passes} building decision capture checks.");
    }

    private static void DisabledReturnsBeforePlayer(string outDir)
    {
        var disabledDir = Path.Combine(outDir, "disabled");
        Directory.CreateDirectory(disabledDir);
        // A directory alone must not enable: the explicit flag is required.
        Environment.SetEnvironmentVariable("COOPBOTS_CAPTURE_DECISIONS", null);
        Environment.SetEnvironmentVariable("COOPBOTS_CAPTURE_DIR", disabledDir);
        BuildDecisionCapture.ResetForTest();

        // No Player dereference: a null player must be harmless while disabled.
        var pre = BuildDecisionCapture.CapturePreState(null!, Array.Empty<CardModel>());
        Check(pre is null, "disabled capture must return before touching Player");

        var fixture = Make("CAPTURE-DISABLED");
        fixture.Clear();
        fixture.Give<StrikeIronclad>(5);
        fixture.Give<DefendIronclad>(4);
        var candidates = new[] { fixture.Candidate<Bash>(), fixture.Candidate<Breakthrough>() };
        var expected = BuildValue.BestReward(fixture.Bot, candidates, allowSkip: true);
        var actual = BotBrain.ChooseCardReward(fixture.Bot, candidates);
        Check(expected == actual, "default-off capture changed the reward choice");
        Check(!Directory.EnumerateFiles(disabledDir).Any(), "disabled capture wrote output");

        // A flag other than exactly "1" must not enable either.
        Environment.SetEnvironmentVariable("COOPBOTS_CAPTURE_DECISIONS", "0");
        BuildDecisionCapture.ResetForTest();
        Check(BuildDecisionCapture.CapturePreState(null!, Array.Empty<CardModel>()) is null,
            "a non-1 flag must not enable capture");
        Pass("disabled capture returns before Player and writes nothing");
    }

    private static void BlankDirectoryRejected(string outDir)
    {
        // A blank directory is "enabled" only by the flag; it must be rejected
        // without touching the Player and without writing.
        Environment.SetEnvironmentVariable("COOPBOTS_CAPTURE_DECISIONS", "1");
        Environment.SetEnvironmentVariable("COOPBOTS_CAPTURE_DIR", "   ");
        BuildDecisionCapture.ResetForTest();
        Check(BuildDecisionCapture.CapturePreState(null!, Array.Empty<CardModel>()) is null,
            "a blank capture directory must not enable capture");
        Check(BuildDecisionCapture.DisabledReasonForTest == "directory-missing-or-relative",
            "a blank capture directory must be recorded as a directory rejection");
        Pass("a blank enabled directory is rejected without touching the Player");
    }

    private static void EnabledCapturesSchemaAndRoundtrip(string outDir)
    {
        var enabledDir = Path.Combine(outDir, "enabled");
        Directory.CreateDirectory(enabledDir);
        SetEnabled(enabledDir);

        var fixture = Make("CAPTURE-ENABLED");
        fixture.Clear();
        fixture.Give<StrikeIronclad>(5);
        fixture.Give<DefendIronclad>(4);
        var upgraded = fixture.Bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        upgraded.UpgradeInternal();
        upgraded.FinalizeUpgradeInternal();
        var strikeCount = fixture.Bot.Deck.Cards.Count(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var deckCount = fixture.Bot.Deck.Cards.Count;
        var strikeToken = upgraded.Id.ToString();

        var first = fixture.Candidate<Bash>();
        var second = fixture.Candidate<Breakthrough>();
        var candidates = new[] { first, second };

        // Real game serializer roundtrip: serialize a real card, read it back.
        var serialized = JsonSerializer.Serialize(first.ToSerializable(), JsonSerializationUtility.GetTypeInfo<SerializableCard>());
        var restored = JsonSerializer.Deserialize(serialized, JsonSerializationUtility.GetTypeInfo<SerializableCard>());
        Check(restored is not null && restored.Id!.Entry == first.Id.Entry, "serialized card roundtrip lost its id");
        var upgradeJson = JsonSerializer.Serialize(upgraded.ToSerializable(), JsonSerializationUtility.GetTypeInfo<SerializableCard>());
        Check(JsonNode.Parse(upgradeJson)!.AsObject()["current_upgrade_level"]?.GetValue<int>() == 1,
            "serialized upgraded card lost its upgrade level");

        var pending = BuildDecisionCapture.CapturePreState(fixture.Bot, candidates);
        Check(pending is not null, "enabled capture did not start");
        var chosen = BuildValue.BestReward(fixture.Bot, candidates, allowSkip: true);
        BuildDecisionCapture.CaptureOutcome(pending, chosen);

        var path = BuildDecisionCapture.SessionPathForTest;
        Check(path is not null && File.Exists(path), "no session file was created");
        var lines = ReadLines(path!);
        Check(lines.Length == 1, $"expected 1 record, got {lines.Length}");
        var record = JsonNode.Parse(lines[0])!.AsObject();

        Check(record["schema"]!.GetValue<string>() == BuildDecisionCapture.SchemaVersion, "schema id mismatch");
        Check(record["source"]!.GetValue<string>() == "bot_card_reward", "source mismatch");
        Check(record["decision"] is not null && record["session"] is not null, "opaque ids missing");
        Check(record["policy"]!.AsObject()["mvid"]!.GetValue<string>().Length > 0, "policy MVID missing");
        Check(record["game"]!.AsObject()["mvid"]!.GetValue<string>().Length > 0, "game MVID missing");
        Check(record["schema_valid"]!.GetValue<bool>(), "schema_valid should be true for emitted v1");
        Check(!record["solver_eligible"]!.GetValue<bool>(), "solver_eligible must be false");
        Check(!record["ready_for_solver"]!.GetValue<bool>(), "ready_for_solver must be false");
        Check(!record["complete"]!.GetValue<bool>(), "complete must be false");
        Check(record["missing"] is JsonArray missing && missing.Count >= 4, "missing coverage list too short");
        Check(record["coverage_limits"] is JsonArray limits && limits.Count >= 4, "coverage_limits list too short");
        Check(record["rng_state"] is null, "rng_state must be null, not defaulted");
        Check(record["hp"] is not null && record["max_hp"] is not null && record["gold"] is not null, "vitals missing");
        Check(record["potion_slot_capacity"] is not null, "potion slot capacity missing");
        Check(record["relics"] is JsonArray, "relics missing");

        var deck = (JsonArray)record["deck"]!;
        Check(deck.Count == deckCount, $"deck snapshot size {deck.Count} != {deckCount}");
        Check(deck.Count(node => Obj(node)["id"]!.GetValue<string>() == strikeToken) == strikeCount,
            "duplicate cards were deduplicated in the snapshot");
        Check(deck.Any(node => Obj(node)["id"]!.GetValue<string>() == strikeToken
            && Obj(node)["current_upgrade_level"]?.GetValue<int>() == 1),
            "upgraded card lost its upgrade level in the snapshot");

        var candidateArray = (JsonArray)record["candidates"]!;
        Check(candidateArray.Count == 2, "candidate count mismatch");
        Check(Obj(candidateArray[0])["index"]!.GetValue<int>() == 0
            && Obj(candidateArray[0])["card"]!.AsObject()["id"]!.GetValue<string>() == first.Id.ToString(),
            "candidate 0 ordering mismatch");
        Check(Obj(candidateArray[1])["index"]!.GetValue<int>() == 1
            && Obj(candidateArray[1])["card"]!.AsObject()["id"]!.GetValue<string>() == second.Id.ToString(),
            "candidate 1 ordering mismatch");

        var selection = record["selection"]!.AsObject();
        Check(selection["index"]!.GetValue<int>() == chosen, "selection index does not match the scorer result");
        Check(selection["skip"]!.GetValue<bool>() == (chosen < 0), "selection skip flag mismatch");
        Check(selection["applied"] is null, "applied must remain null");
        Check(!selection["applied_observed"]!.GetValue<bool>(), "applied_observed must be false");
        Check(selection["legal_alternatives"]!.GetValue<string>() == "unknown", "alternatives must be recorded as unknown");
        if (chosen >= 0)
        {
            var canonical = Obj(candidateArray[chosen])["card"]!.AsObject()["id"]!.GetValue<string>();
            Check(selection["selected_card_id"]!.GetValue<string>() == canonical,
                "selected_card_id must use the same canonical full ModelId string as the candidate");
        }
        Check(BuildDecisionCapture.ValidateRecord(record, out _), "normal output must pass the structural validator");

        Pass("enabled capture serializes real models, preserves copies/upgrades and links the selection");
    }

    private static void DistinctDecisionIds(string outDir)
    {
        var dir = Path.Combine(outDir, "distinct");
        Directory.CreateDirectory(dir);
        SetEnabled(dir);

        var fixture = Make("CAPTURE-DISTINCT");
        fixture.Clear();
        fixture.Give<StrikeIronclad>(4);
        var candidates = new[] { fixture.Candidate<Bash>(), fixture.Candidate<PommelStrike>() };

        for (var i = 0; i < 2; i++)
        {
            var pending = BuildDecisionCapture.CapturePreState(fixture.Bot, candidates);
            var chosen = BuildValue.BestReward(fixture.Bot, candidates, allowSkip: true);
            BuildDecisionCapture.CaptureOutcome(pending, chosen);
        }

        var path = BuildDecisionCapture.SessionPathForTest;
        Check(path is not null, "no session file");
        var lines = ReadLines(path!);
        Check(lines.Length == 2, $"expected 2 records, got {lines.Length}");
        var first = JsonNode.Parse(lines[0])!.AsObject();
        var second = JsonNode.Parse(lines[1])!.AsObject();
        Check(first["decision"]!.GetValue<string>() != second["decision"]!.GetValue<string>(),
            "each invocation must get a fresh decision id");
        Check(first["session"]!.GetValue<string>() == second["session"]!.GetValue<string>(),
            "one session must share its session id");
        Pass("each invocation gets a distinct decision id within one session");
    }

    private static void RunIdentityIsStableAndDistinct(string outDir)
    {
        var dir = Path.Combine(outDir, "run-identity");
        Directory.CreateDirectory(dir);
        SetEnabled(dir);

        var fixture = Make("CAPTURE-RUNID");
        fixture.Clear();
        fixture.Give<StrikeIronclad>(3);
        var candidates = new[] { fixture.Candidate<Bash>() };
        for (var i = 0; i < 2; i++)
        {
            var pending = BuildDecisionCapture.CapturePreState(fixture.Bot, candidates);
            BuildDecisionCapture.CaptureOutcome(pending, BuildValue.BestReward(fixture.Bot, candidates, allowSkip: true));
        }
        var other = Make("CAPTURE-RUNID");
        other.Clear();
        other.Give<StrikeIronclad>(3);
        var otherCandidates = new[] { other.Candidate<Bash>() };
        var otherPending = BuildDecisionCapture.CapturePreState(other.Bot, otherCandidates);
        BuildDecisionCapture.CaptureOutcome(otherPending, BuildValue.BestReward(other.Bot, otherCandidates, allowSkip: true));

        var path = BuildDecisionCapture.SessionPathForTest;
        Check(path is not null, "no session file for run identity");
        var lines = ReadLines(path!);
        Check(lines.Length == 3, $"expected 3 records, got {lines.Length}");
        var first = JsonNode.Parse(lines[0])!.AsObject();
        var second = JsonNode.Parse(lines[1])!.AsObject();
        var third = JsonNode.Parse(lines[2])!.AsObject();
        var runId = first["run"]!.GetValue<string>();
        Check(runId.Length > 0, "run id must be present");
        Check(runId == second["run"]!.GetValue<string>(), "the same run object must keep one opaque id");
        Check(runId != third["run"]!.GetValue<string>(), "distinct run objects must not collide on a hash code");
        Check(first["session"]!.GetValue<string>() == third["session"]!.GetValue<string>(),
            "one session must share its session id");
        Pass("run identity is stable per run object and distinct between run objects");
    }

    private static void PreStateIsImmutableAfterMutation(string outDir)
    {
        var dir = Path.Combine(outDir, "immutable");
        Directory.CreateDirectory(dir);
        SetEnabled(dir);

        var fixture = Make("CAPTURE-IMMUTABLE");
        fixture.Clear();
        fixture.Give<StrikeIronclad>(5);
        var candidates = new[] { fixture.Candidate<Bash>() };

        var breakthroughToken = fixture.Candidate<Breakthrough>().Id.ToString();
        var pending = BuildDecisionCapture.CapturePreState(fixture.Bot, candidates);
        var deckBefore = fixture.Bot.Deck.Cards.Count;
        fixture.Give<Breakthrough>(3);
        fixture.Clear();

        BuildDecisionCapture.CaptureOutcome(pending, 0);

        var record = ReadOnlyRecord();
        var deck = (JsonArray)record["deck"]!;
        Check(deck.Count == deckBefore, "pre-state changed after the model was mutated");
        Check(!deck.Any(node => Obj(node)["id"]!.GetValue<string>() == breakthroughToken),
            "a card added after capture leaked into the pre-state");
        Pass("the pre-state snapshot is immutable after model mutation");
    }

    private static void OutcomeIsConsumedOnce(string outDir)
    {
        var dir = Path.Combine(outDir, "consume-once");
        Directory.CreateDirectory(dir);
        SetEnabled(dir);

        var fixture = Make("CAPTURE-ONCE");
        fixture.Clear();
        fixture.Give<StrikeIronclad>(3);
        var candidates = new[] { fixture.Candidate<Bash>() };
        var pending = BuildDecisionCapture.CapturePreState(fixture.Bot, candidates);
        var chosen = BuildValue.BestReward(fixture.Bot, candidates, allowSkip: true);
        BuildDecisionCapture.CaptureOutcome(pending, chosen);
        BuildDecisionCapture.CaptureOutcome(pending, chosen);

        var path = BuildDecisionCapture.SessionPathForTest;
        Check(path is not null && ReadLines(path).Length == 1,
            "a pre-state passed to CaptureOutcome twice must append exactly one record");
        Pass("a pre-state is consumed once; a duplicate outcome is ignored");
    }

    private static void ValidatorRejectsMalformed(string outDir)
    {
        var dir = Path.Combine(outDir, "validator");
        Directory.CreateDirectory(dir);
        SetEnabled(dir);

        var fixture = Make("CAPTURE-VALIDATE");
        fixture.Clear();
        fixture.Give<StrikeIronclad>(3);
        var candidates = new[] { fixture.Candidate<Bash>(), fixture.Candidate<Breakthrough>() };
        var pending = BuildDecisionCapture.CapturePreState(fixture.Bot, candidates);
        BuildDecisionCapture.CaptureOutcome(pending, BuildValue.BestReward(fixture.Bot, candidates, allowSkip: true));

        var valid = ReadOnlyRecord();
        Check(BuildDecisionCapture.ValidateRecord(valid, out _), "a normal record must pass the structural validator");
        foreach (var key in new[] { "character", "ascension", "party_size", "rng_state", "room_id" })
        {
            var missing = Clone(valid);
            missing.Remove(key);
            Check(!BuildDecisionCapture.ValidateRecord(missing, out _), "missing field accepted: " + key);
        }
        var missingApplied = Clone(valid);
        ((JsonObject)missingApplied["selection"]!).Remove("applied");
        Check(!BuildDecisionCapture.ValidateRecord(missingApplied, out _), "absent applied must not mean explicit unknown");
        var wrongSource = Clone(valid);
        wrongSource["source"] = "human_event";
        Check(!BuildDecisionCapture.ValidateRecord(wrongSource, out _), "unsupported source accepted");
        var badPotion = Clone(valid);
        badPotion["potion_slot_capacity"] = 3;
        badPotion["potions"] = new JsonArray(new JsonObject
        {
            ["slot_index"] = 2,
            ["potion"] = new JsonObject { ["id"] = "POTION.TEST", ["slot_index"] = 0 },
        });
        Check(!BuildDecisionCapture.ValidateRecord(badPotion, out var slotReason)
            && slotReason == "potion-slot-mismatch", "contradictory potion slot accepted");

        var missingRun = Clone(valid);
        missingRun.Remove("run");
        Check(!BuildDecisionCapture.ValidateRecord(missingRun, out var missingReason) && missingReason == "run",
            "a missing run field must be rejected");

        var badIndex = Clone(valid);
        ((JsonObject)badIndex["selection"]!)["index"] = 999;
        Check(!BuildDecisionCapture.ValidateRecord(badIndex, out var indexReason) && indexReason == "selection-index-range",
            "an out-of-range selection index must be rejected");

        var contradictory = Clone(valid);
        var contradiction = (JsonObject)contradictory["selection"]!;
        contradiction["index"] = 0;
        contradiction["skip"] = false;
        contradiction["selected_card_id"] = "CARD.NOT_A_CANDIDATE";
        Check(!BuildDecisionCapture.ValidateRecord(contradictory, out var contradictionReason)
            && contradictionReason == "selection-selected-id",
            "a selected_card_id that does not match the candidate must be rejected");

        // A malformed outcome is dropped, not written, and stops capture.
        var badDir = Path.Combine(outDir, "validator-outcome");
        Directory.CreateDirectory(badDir);
        SetEnabled(badDir);
        var badFixture = Make("CAPTURE-VALIDATE-OUTCOME");
        badFixture.Clear();
        badFixture.Give<StrikeIronclad>(3);
        var badCandidates = new[] { badFixture.Candidate<Bash>() };
        var badPending = BuildDecisionCapture.CapturePreState(badFixture.Bot, badCandidates);
        var badPath = BuildDecisionCapture.SessionPathForTest;
        BuildDecisionCapture.CaptureOutcome(badPending, 999);
        Check(badPath is not null && new FileInfo(badPath).Length == 0,
            "an invalid chosen index must not append a malformed record");
        Check(BuildDecisionCapture.DisabledReasonForTest?.StartsWith("validation-failed", StringComparison.Ordinal) == true,
            "an invalid chosen index must disable capture with a validation reason");

        Pass("the structural validator passes normal records and rejects missing, out-of-range and contradictory ones");
    }

    private static void ItemStateSurvivesCapture(string outDir)
    {
        var dir = Path.Combine(outDir, "items");
        Directory.CreateDirectory(dir);
        SetEnabled(dir);

        var fixture = Make("CAPTURE-ITEMS");
        fixture.Clear();
        fixture.Give<StrikeIronclad>(3);

        // Real relic with a saved counter: PenNib.AttacksPlayed is a SavedProperty.
        var penNib = (PenNib)ModelDb.Relic<PenNib>().ToMutable();
        fixture.Bot.AddRelicInternal(penNib);
        var attacksField = typeof(PenNib).GetField("_attacksPlayed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PenNib._attacksPlayed field not found");
        attacksField.SetValue(penNib, 7);
        Check(penNib.AttacksPlayed == 7, "could not set the PenNib saved counter");

        // Real potion in a nonzero slot with slots 0 and 1 left empty.
        if (fixture.Bot.MaxPotionCount < 3)
            fixture.Bot.AddToMaxPotionCount(3 - fixture.Bot.MaxPotionCount);
        var potion = (PotionModel)ModelDb.Potion<BlockPotion>().ToMutable();
        fixture.Bot.AddPotionInternal(potion, 2, silent: true);
        Check(ReferenceEquals(fixture.Bot.PotionSlots[2], potion)
            && fixture.Bot.PotionSlots[0] is null && fixture.Bot.PotionSlots[1] is null,
            "the potion did not land in slot 2 with slots 0 and 1 empty");

        var candidates = new[] { fixture.Candidate<Bash>() };
        var pending = BuildDecisionCapture.CapturePreState(fixture.Bot, candidates);

        // Mutate both models after the snapshot: the record must not follow.
        attacksField.SetValue(penNib, 3);
        fixture.Bot.DiscardPotionInternal(potion, silent: true);
        Check(penNib.AttacksPlayed == 3, "the relic counter mutation did not take effect");
        Check(fixture.Bot.PotionSlots[2] is null, "the potion was not removed before the outcome");

        BuildDecisionCapture.CaptureOutcome(pending, BuildValue.BestReward(fixture.Bot, candidates, allowSkip: true));

        var record = ReadOnlyRecord();
        var relics = (JsonArray)record["relics"]!;
        var relicJson = relics.Select(Obj).FirstOrDefault(node => node["id"]!.GetValue<string>() == penNib.Id.ToString());
        Check(relicJson is not null, "the PenNib relic is missing from the snapshot");
        var ints = relicJson!["props"]?.AsObject()?["ints"]?.AsArray();
        Check(ints is not null, "the serialized relic props.ints is missing");
        var counter = ints!.Select(Obj).FirstOrDefault(node => node["name"]!.GetValue<string>() == "AttacksPlayed");
        Check(counter is not null, "the saved relic counter AttacksPlayed is missing from the snapshot");
        Check(counter!["value"]!.GetValue<int>() == 7, "the saved relic counter changed after the model was mutated");

        var potions = (JsonArray)record["potions"]!;
        Check(potions.Count == 1, $"expected exactly one captured potion, got {potions.Count}");
        var potionJson = Obj(potions[0]);
        Check(potionJson["slot_index"]!.GetValue<int>() == 2, "the captured potion slot index is wrong");
        Check(potionJson["card"] is null, "the potion wrapper must be named 'potion', not 'card'");
        var capturedPotion = potionJson["potion"]?.AsObject();
        Check(capturedPotion is not null, "the captured potion wrapper 'potion' is missing");
        Check(capturedPotion!["id"]!.GetValue<string>().Length > 0, "the captured potion id is missing");
        Pass("real relic saved props and a holed nonzero potion slot survive capture and stay immutable");
    }

    private static void CardEnchantmentSurvivesCapture(string outDir)
    {
        var dir = Path.Combine(outDir, "enchantment");
        Directory.CreateDirectory(dir);
        SetEnabled(dir);

        var fixture = Make("CAPTURE-ENCHANT");
        fixture.Clear();
        fixture.Give<StrikeIronclad>(1);
        var card = fixture.Bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var enchantment = (EnchantmentModel)ModelDb.Enchantment<Sharp>().ToMutable();
        if (!enchantment.CanEnchant(card))
        {
            Console.WriteLine("GAP: card enchantment not exercised; Sharp cannot enchant this card in this model state.");
            return;
        }
        CardCmd.Enchant(enchantment, card, 2);
        Check(card.Enchantment is not null, "CardCmd.Enchant did not attach an enchantment to the real card");

        var candidates = new[] { fixture.Candidate<Bash>() };
        var pending = BuildDecisionCapture.CapturePreState(fixture.Bot, candidates);
        BuildDecisionCapture.CaptureOutcome(pending, BuildValue.BestReward(fixture.Bot, candidates, allowSkip: true));

        var record = ReadOnlyRecord();
        var deck = (JsonArray)record["deck"]!;
        var entry = deck.Select(Obj).FirstOrDefault(node => node["id"]!.GetValue<string>() == card.Id.ToString());
        Check(entry is not null, "the enchanted card is missing from the snapshot");
        var captured = entry!["enchantment"]?.AsObject();
        Check(captured is not null, "the enchantment is missing from the snapshot");
        Check(captured!["id"]!.GetValue<string>() == enchantment.Id.ToString(), "the captured enchantment id is wrong");
        Check(captured["amount"]!.GetValue<int>() == 2, "the captured enchantment amount is wrong");
        Pass("card enchantment survives capture through the real game serializer");
    }

    private static void SilentCharacterSmoke(string outDir)
    {
        // The real Silent factory populates starting relics through a SaveManager
        // the test host does not have; skipping exactly that is the same adapter
        // the BuildingDecisionProbe uses, and it keeps the real Silent model.
        EnsureStartingRelicAdapter();

        var dir = Path.Combine(outDir, "silent");
        Directory.CreateDirectory(dir);
        SetEnabled(dir);

        var player = Player.CreateForNewRun<Silent>(UnlockState.all, 1);
        Check(player.Character?.Id.Entry == "SILENT", "the created player is not the real Silent model");
        var combat = new CombatState(runState: RunState.CreateForTest(new[] { player }, seed: "CAPTURE-SILENT"));
        player.ResetCombatState();
        combat.AddPlayer(player);
        Check(player.Deck.Cards.Count > 0, "the real Silent starting deck was not populated");

        CardModel[] candidates = [combat.CreateCard<StrikeSilent>(player), combat.CreateCard<DefendSilent>(player)];
        var pending = BuildDecisionCapture.CapturePreState(player, candidates);
        BuildDecisionCapture.CaptureOutcome(pending, BuildValue.BestReward(player, candidates, allowSkip: true));

        var record = ReadOnlyRecord();
        Check(record["character"]!.GetValue<string>() == "SILENT", "the snapshot did not record the Silent character");
        Check(record["deck"] is JsonArray deck && deck.Count > 0, "the Silent deck snapshot is empty");
        Pass("a real Silent player model is captured headlessly with its character and deck");
    }

    private static bool _startingRelicAdapterInstalled;

    private static void EnsureStartingRelicAdapter()
    {
        if (_startingRelicAdapterInstalled)
            return;
        _startingRelicAdapterInstalled = true;
        var harness = new Harmony("coopbots.test.capture.silent");
        harness.Patch(AccessTools.Method(typeof(Player), "PopulateStartingRelics"),
            prefix: new HarmonyMethod(typeof(BuildDecisionCaptureScenarios).GetMethod(
                nameof(SkipStartingRelics), BindingFlags.Static | BindingFlags.NonPublic)));
    }

    private static bool SkipStartingRelics() => false;

    private static JsonObject Clone(JsonObject source) => (JsonObject)JsonNode.Parse(source.ToJsonString())!;

    private static void RecordAndByteCaps(string outDir)
    {
        var recordDir = Path.Combine(outDir, "record-cap");
        Directory.CreateDirectory(recordDir);
        SetEnabled(recordDir);
        BuildDecisionCapture.ConfigureLimitsForTest(2, 10L * 1024 * 1024);

        var fixture = Make("CAPTURE-RECORDCAP");
        fixture.Clear();
        fixture.Give<StrikeIronclad>(3);
        var candidates = new[] { fixture.Candidate<Bash>() };
        var firstPending = BuildDecisionCapture.CapturePreState(fixture.Bot, candidates);
        var recordPath = BuildDecisionCapture.SessionPathForTest;
        BuildDecisionCapture.CaptureOutcome(firstPending,
            BuildValue.BestReward(fixture.Bot, candidates, allowSkip: true));
        for (var i = 1; i < 3; i++)
        {
            var pending = BuildDecisionCapture.CapturePreState(fixture.Bot, candidates);
            var chosen = BuildValue.BestReward(fixture.Bot, candidates, allowSkip: true);
            BuildDecisionCapture.CaptureOutcome(pending, chosen);
        }
        Check(recordPath is not null && ReadLines(recordPath).Length == 2,
            "the record cap must stop after the configured number of records");
        Check(BuildDecisionCapture.DisabledReasonForTest == "session-limit", "record cap did not disable capture");
        Pass("the per-session record cap stops and disables capture");

        var byteDir = Path.Combine(outDir, "byte-cap");
        Directory.CreateDirectory(byteDir);
        SetEnabled(byteDir);
        BuildDecisionCapture.ConfigureLimitsForTest(1000, 8);

        var byteFixture = Make("CAPTURE-BYTECAP");
        byteFixture.Clear();
        byteFixture.Give<StrikeIronclad>(2);
        var byteCandidates = new[] { byteFixture.Candidate<Bash>() };
        var bytePending = BuildDecisionCapture.CapturePreState(byteFixture.Bot, byteCandidates);
        var bytePath = BuildDecisionCapture.SessionPathForTest;
        var byteChosen = BuildValue.BestReward(byteFixture.Bot, byteCandidates, allowSkip: true);
        BuildDecisionCapture.CaptureOutcome(bytePending, byteChosen);
        Check(bytePath is not null, "byte cap session file missing");
        Check(new FileInfo(bytePath!).Length == 0, "the byte cap must not write an oversized record");
        Check(BuildDecisionCapture.DisabledReasonForTest == "session-limit", "byte cap did not disable capture");
        Pass("the per-session byte cap stops without writing a partial record");
    }

    private static void UnwritableTargetFailsSafely(string outDir)
    {
        var blocked = Path.Combine(outDir, "blocked-target");
        File.WriteAllText(blocked, "this is a file, not a directory");
        Environment.SetEnvironmentVariable("COOPBOTS_CAPTURE_DECISIONS", "1");
        Environment.SetEnvironmentVariable("COOPBOTS_CAPTURE_DIR", blocked);
        BuildDecisionCapture.ResetForTest();

        var fixture = Make("CAPTURE-BLOCKED");
        fixture.Clear();
        fixture.Give<StrikeIronclad>(3);
        var candidates = new[] { fixture.Candidate<Bash>() };
        var expected = BuildValue.BestReward(fixture.Bot, candidates, allowSkip: true);
        var actual = BotBrain.ChooseCardReward(fixture.Bot, candidates);
        Check(expected == actual, "a blocked capture directory changed the reward choice");
        Check(BuildDecisionCapture.SessionPathForTest is null, "a blocked capture directory produced a session");

        Environment.SetEnvironmentVariable("COOPBOTS_CAPTURE_DIR", "relative-not-absolute");
        BuildDecisionCapture.ResetForTest();
        var relativeFixture = Make("CAPTURE-RELATIVE");
        relativeFixture.Clear();
        relativeFixture.Give<StrikeIronclad>(3);
        Check(BuildDecisionCapture.CapturePreState(relativeFixture.Bot, new[] { relativeFixture.Candidate<Bash>() }) is null,
            "a relative capture directory must not enable capture");
        Pass("an unwritable target fails safely and does not affect the choice");
    }

    private static void EnabledAndDisabledAgree(string outDir)
    {
        SetDisabled();
        var off = Make("CAPTURE-COMPARE");
        off.Clear();
        off.Give<StrikeIronclad>(5);
        off.Give<DefendIronclad>(4);
        var offCandidates = new[] { off.Candidate<Bash>(), off.Candidate<Breakthrough>(), off.Candidate<PommelStrike>() };
        var offChoice = BotBrain.ChooseCardReward(off.Bot, offCandidates);
        var offDeck = BuildTrace.Describe(off.Bot);

        var dir = Path.Combine(outDir, "compare");
        Directory.CreateDirectory(dir);
        SetEnabled(dir);
        var on = Make("CAPTURE-COMPARE");
        on.Clear();
        on.Give<StrikeIronclad>(5);
        on.Give<DefendIronclad>(4);
        var onCandidates = new[] { on.Candidate<Bash>(), on.Candidate<Breakthrough>(), on.Candidate<PommelStrike>() };
        var onChoice = BotBrain.ChooseCardReward(on.Bot, onCandidates);
        var onDeck = BuildTrace.Describe(on.Bot);

        Check(offChoice == onChoice, "capture changed the reward choice");
        Check(offDeck == onDeck, "capture changed the deck");
        Pass("enabled and disabled capture return the same choice and leave the same deck");
    }

    private static void SetDisabled()
    {
        Environment.SetEnvironmentVariable("COOPBOTS_CAPTURE_DECISIONS", null);
        Environment.SetEnvironmentVariable("COOPBOTS_CAPTURE_DIR", null);
        BuildDecisionCapture.ResetForTest();
    }

    private static void SetEnabled(string directory)
    {
        Environment.SetEnvironmentVariable("COOPBOTS_CAPTURE_DECISIONS", "1");
        Environment.SetEnvironmentVariable("COOPBOTS_CAPTURE_DIR", directory);
        BuildDecisionCapture.ResetForTest();
        Directory.CreateDirectory(directory);
    }

    private static JsonObject ReadOnlyRecord()
    {
        var path = BuildDecisionCapture.SessionPathForTest;
        Check(path is not null && File.Exists(path), "expected a session file");
        var lines = ReadLines(path!);
        Check(lines.Length == 1, $"expected 1 record, got {lines.Length}");
        return JsonNode.Parse(lines[0])!.AsObject();
    }

    // The writer keeps its session file open; read it with a share mode that
    // permits the existing write handle rather than File.ReadAllLines' default.
    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

    private static JsonObject Obj(JsonNode? node)
        => node as JsonObject ?? throw new InvalidOperationException("expected a JSON object");

    private static void Pass(string message)
    {
        _passes++;
        Console.WriteLine("PASS: " + message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static Fixture Make(string seed)
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var combat = new CombatState(runState: RunState.CreateForTest(new[] { bot }, seed: seed));
        bot.ResetCombatState();
        combat.AddPlayer(bot);
        return new Fixture(bot, combat);
    }

    private sealed class Fixture
    {
        internal Fixture(Player bot, CombatState combat)
        {
            Bot = bot;
            Combat = combat;
        }

        internal Player Bot { get; }
        internal CombatState Combat { get; }

        internal void Clear()
        {
            foreach (var card in Bot.Deck.Cards.ToArray())
                Bot.Deck.RemoveInternal(card);
        }

        internal void Give<T>(int count = 1) where T : CardModel
        {
            for (var i = 0; i < count; i++)
                Bot.Deck.AddInternal(Combat.CreateCard<T>(Bot));
        }

        internal CardModel Candidate<T>() where T : CardModel
            => Combat.CreateCard<T>(Bot);
    }
}

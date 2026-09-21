using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CoopBots;

/// <summary>
/// Default-off local research capture for <see cref="BotBrain.ChooseCardReward"/>.
///
/// Disabled (the default) it returns before it touches the <see cref="Player"/>
/// and never serializes anything. Enabled it snapshots the acting player's state
/// before the scorer runs, then appends one JSONL record per completed decision
/// with the observed selection. It never calls the scorer itself and never
/// changes the returned choice.
///
/// The snapshot is deliberately narrow: the acting player's deck, relics,
/// potions and vitals plus the reward candidates. It does not copy the whole
/// Player/Run graph, and it does not retain any game object after capture. The
/// selection is what this hook observes; whether the game applied it is left
/// null/unknown, and legal alternatives are recorded as unknown.
/// </summary>
internal static class BuildDecisionCapture
{
    internal const string SchemaVersion = "coopbots.build_decision_capture.v1";
    internal const string Source = "bot_card_reward";
    internal const string PolicyId = "CoopBots.BotBrain.ChooseCardReward";

    private const int DefaultMaxRecords = 1000;
    private const long DefaultMaxBytes = 10L * 1024 * 1024;
    private const string EnableVariable = "COOPBOTS_CAPTURE_DECISIONS";
    private const string DirectoryVariable = "COOPBOTS_CAPTURE_DIR";

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions OuterOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Per-run object identity. A RunState object keeps the same opaque id for
    // the life of the capture session; distinct run objects never collide the
    // way a hash code can. Cleared with the session so ids cannot be correlated
    // across sessions.
    private static ConditionalWeakTable<object, RunIdentity> _runIds = new();

    private sealed class RunIdentity
    {
        internal Guid Id { get; } = Guid.NewGuid();
    }

    private static bool _initialized;
    private static FileStream? _stream;
    private static string? _path;
    private static string? _sessionId;
    private static string? _disabledReason;
    private static int _records;
    private static long _bytes;
    private static int _maxRecords = DefaultMaxRecords;
    private static long _maxBytes = DefaultMaxBytes;

    /// <summary>
    /// The immutable pre-scoring snapshot for one invocation. Holds only
    /// serialized JSON plus the decision id and candidate id strings, never a
    /// mutable game reference.
    /// </summary>
    internal sealed class PendingDecision
    {
        internal PendingDecision(string decisionId, JsonObject preState, string[] candidateIds)
        {
            DecisionId = decisionId;
            PreState = preState;
            CandidateIds = candidateIds;
        }

        internal string DecisionId { get; }
        internal JsonObject PreState { get; }
        internal string[] CandidateIds { get; }

        private int _consumed;

        /// <summary>True exactly once per pending decision, so a repeated outcome cannot append twice.</summary>
        internal bool TryConsume() => Interlocked.Exchange(ref _consumed, 1) == 0;
    }

    /// <summary>
    /// Captures the pre-scoring snapshot. Returns null when capture is disabled
    /// or the snapshot cannot be serialized; on failure capture stays disabled
    /// for the rest of the session and the choice is unaffected.
    /// </summary>
    internal static PendingDecision? CapturePreState(Player player, IReadOnlyList<CardModel> candidates)
    {
        try
        {
            if (!EnsureSession())
                return null;
            var decisionId = Guid.NewGuid().ToString("N");
            var preState = BuildPreState(player, candidates, decisionId);
            var ids = new string[candidates.Count];
            for (var i = 0; i < candidates.Count; i++)
                ids[i] = candidates[i].Id.ToString();
            return new PendingDecision(decisionId, preState, ids);
        }
        catch (Exception error)
        {
            // Every instrumentation step, including session setup, stays inside
            // instrumentation: it can never escape into the scorer's call path.
            Disable("pre-state-failed:" + error.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Completes the record with the selection the scorer returned and appends
    /// it. No-op when the snapshot is null. The selected index/skip is the only
    /// outcome this hook can observe; application stays null/unknown.
    /// </summary>
    internal static void CaptureOutcome(PendingDecision? pending, int chosen)
    {
        if (pending is null || !pending.TryConsume())
            return;
        try
        {
            // same canonical full ModelId string the candidates were serialized with
            var selectedId = chosen >= 0 && chosen < pending.CandidateIds.Length
                ? pending.CandidateIds[chosen]
                : null;
            pending.PreState["selection"] = new JsonObject
            {
                ["index"] = chosen,
                ["skip"] = chosen < 0,
                ["selected_card_id"] = selectedId,
                ["applied"] = null,
                ["applied_observed"] = false,
                ["legal_alternatives"] = "unknown",
                ["note"] = "hook observes the selection return only; application is not observed",
            };
            pending.PreState["selection_recorded_utc"] = UtcNow();
            if (!ValidateRecord(pending.PreState, out var reason))
            {
                // Malformed capture is dropped rather than written; capture stops
                // for the session so it cannot keep producing bad rows.
                Disable("validation-failed:" + reason);
                return;
            }
            Append(pending.PreState.ToJsonString(OuterOptions));
        }
        catch (Exception error)
        {
            Disable("outcome-failed:" + error.GetType().Name);
        }
    }

    /// <summary>
    /// Compact structural validator for one completed record. Returns false with
    /// a short reason when the mandatory shape is broken. It validates the JSON
    /// shape only: it makes no claim about gameplay legality or solver coverage.
    /// Internally callable so tests can feed deliberately malformed records.
    /// </summary>
    internal static bool ValidateRecord(JsonObject record, out string? reason)
    {
        reason = null;

        if (!TryString(record, "schema", out var schema) || schema != SchemaVersion) { reason = "schema"; return false; }
        if (!TryString(record, "source", out var source) || source != Source) { reason = "source"; return false; }
        if (!TryNonEmptyString(record, "character", out _)) { reason = "character"; return false; }
        if (!TryInt(record, "ascension", out var ascension) || ascension < 0) { reason = "ascension"; return false; }
        if (!TryInt(record, "party_size", out var partySize) || partySize < 1) { reason = "party_size"; return false; }
        if (!TryInt(record, "player_slot", out var playerSlot) || playerSlot < 0 || playerSlot >= partySize)
        { reason = "player_slot"; return false; }
        foreach (var key in new[] { "act", "floor", "room", "room_id", "root_seed", "rng_state" })
            if (!record.ContainsKey(key)) { reason = "missing-" + key; return false; }
        if (!TryNonEmptyString(record, "decision", out _)) { reason = "decision"; return false; }
        if (!TryNonEmptyString(record, "session", out _)) { reason = "session"; return false; }
        if (!TryNonEmptyString(record, "run", out _)) { reason = "run"; return false; }
        if (!TryNonEmptyString(record, "captured_utc", out _)) { reason = "captured_utc"; return false; }
        if (!TryNonEmptyString(record, "selection_recorded_utc", out _)) { reason = "selection_recorded_utc"; return false; }

        if (record["policy"] is not JsonObject policy
            || !TryNonEmptyString(policy, "id", out _)
            || !TryNonEmptyString(policy, "assembly", out _)
            || !TryNonEmptyString(policy, "version", out _)
            || !TryNonEmptyString(policy, "mvid", out _)) { reason = "policy"; return false; }

        if (record["game"] is not JsonObject game
            || !TryNonEmptyString(game, "assembly", out _)
            || !TryNonEmptyString(game, "version", out _)
            || !TryNonEmptyString(game, "mvid", out _)) { reason = "game"; return false; }

        if (!TryInt(record, "hp", out var hp) || hp < 0) { reason = "hp"; return false; }
        if (!TryInt(record, "max_hp", out var maxHp) || maxHp < 0) { reason = "max_hp"; return false; }
        if (!TryInt(record, "gold", out var gold) || gold < 0) { reason = "gold"; return false; }
        if (!TryInt(record, "potion_slot_capacity", out var capacity) || capacity < 0) { reason = "potion_slot_capacity"; return false; }

        if (record["missing"] is not JsonArray) { reason = "missing"; return false; }
        if (record["coverage_limits"] is not JsonArray) { reason = "coverage_limits"; return false; }

        if (record["deck"] is not JsonArray deck) { reason = "deck"; return false; }
        foreach (var node in deck)
            if (node is not JsonObject card || !TryNonEmptyString(card, "id", out _)) { reason = "deck-card"; return false; }

        if (record["relics"] is not JsonArray relics) { reason = "relics"; return false; }
        foreach (var node in relics)
            if (node is not JsonObject relic || !TryNonEmptyString(relic, "id", out _)) { reason = "relic"; return false; }

        if (record["candidates"] is not JsonArray candidates) { reason = "candidates"; return false; }
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i] is not JsonObject candidate) { reason = "candidate"; return false; }
            if (!TryInt(candidate, "index", out var index) || index != i) { reason = "candidate-index"; return false; }
            if (candidate["card"] is not JsonObject candidateCard || !TryNonEmptyString(candidateCard, "id", out _))
            { reason = "candidate-card"; return false; }
        }

        if (record["potions"] is not JsonArray potions) { reason = "potions"; return false; }
        var slots = new HashSet<int>();
        foreach (var node in potions)
        {
            if (node is not JsonObject potion) { reason = "potion"; return false; }
            if (!TryInt(potion, "slot_index", out var slot) || slot < 0 || slot >= capacity) { reason = "potion-slot"; return false; }
            if (!slots.Add(slot)) { reason = "potion-slot-duplicate"; return false; }
            if (potion["potion"] is not JsonObject potionBody || !TryNonEmptyString(potionBody, "id", out _))
            { reason = "potion-body"; return false; }
            if (!TryInt(potionBody, "slot_index", out var savedSlot) || savedSlot != slot)
            { reason = "potion-slot-mismatch"; return false; }
        }

        if (record["selection"] is not JsonObject selection) { reason = "selection"; return false; }
        if (!TryInt(selection, "index", out var chosen)) { reason = "selection-index"; return false; }
        if (chosen < -1 || chosen >= candidates.Count) { reason = "selection-index-range"; return false; }
        if (!TryBool(selection, "skip", out var skip)) { reason = "selection-skip"; return false; }
        if (!selection.ContainsKey("applied") || selection["applied"] is not null) { reason = "selection-applied"; return false; }
        if (!TryBool(selection, "applied_observed", out var appliedObserved) || appliedObserved) { reason = "selection-applied-observed"; return false; }
        if (!TryString(selection, "legal_alternatives", out var alternatives) || alternatives != "unknown") { reason = "selection-alternatives"; return false; }
        if (chosen < 0)
        {
            if (!skip) { reason = "selection-skip"; return false; }
            if (!selection.ContainsKey("selected_card_id") || selection["selected_card_id"] is not null) { reason = "selection-selected-id"; return false; }
        }
        else
        {
            if (skip) { reason = "selection-skip"; return false; }
            if (!TryNonEmptyString(selection, "selected_card_id", out var selectedId)) { reason = "selection-selected-id"; return false; }
            var expected = ((JsonObject)candidates[chosen]!)["card"]!["id"]!.GetValue<string>();
            if (!string.Equals(selectedId, expected, StringComparison.Ordinal)) { reason = "selection-selected-id"; return false; }
        }

        if (!TryBool(record, "schema_valid", out var schemaValid) || !schemaValid) { reason = "schema_valid"; return false; }
        if (!TryBool(record, "solver_eligible", out var solverEligible) || solverEligible) { reason = "solver_eligible"; return false; }
        if (!TryBool(record, "ready_for_solver", out var ready) || ready) { reason = "ready_for_solver"; return false; }
        if (!TryBool(record, "complete", out var complete) || complete) { reason = "complete"; return false; }

        return true;
    }

    private static bool TryInt(JsonObject obj, string key, out int value)
    {
        value = 0;
        return obj[key] is JsonValue node && node.TryGetValue(out value);
    }

    private static bool TryBool(JsonObject obj, string key, out bool value)
    {
        value = false;
        return obj[key] is JsonValue node && node.TryGetValue(out value);
    }

    private static bool TryString(JsonObject obj, string key, out string? value)
    {
        value = null;
        return obj[key] is JsonValue node && node.TryGetValue(out value);
    }

    private static bool TryNonEmptyString(JsonObject obj, string key, out string? value)
        => TryString(obj, key, out value) && !string.IsNullOrWhiteSpace(value);

    private static bool EnsureSession()
    {
        lock (Gate)
        {
            if (_initialized)
                return _stream is not null;
            _initialized = true;

            if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal))
            {
                _disabledReason = "flag-off";
                return false;
            }

            var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            {
                _disabledReason = "directory-missing-or-relative";
                return false;
            }

            try
            {
                Directory.CreateDirectory(directory);
                var sessionId = Guid.NewGuid().ToString("N")[..12];
                var name = $"coopbots-decisions-{DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)}"
                    + $"-{sessionId}-p{Environment.ProcessId}.jsonl";
                var path = Path.Combine(directory, name);
                _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _path = path;
                _sessionId = sessionId;
                return true;
            }
            catch (Exception error)
            {
                _stream = null;
                _path = null;
                _sessionId = null;
                _disabledReason = "open-failed:" + error.GetType().Name;
                return false;
            }
        }
    }

    private static JsonObject BuildPreState(Player player, IReadOnlyList<CardModel> candidates, string decisionId)
    {
        var run = player.RunState;
        var room = run.CurrentRoom;

        var deck = new JsonArray();
        foreach (var card in player.Deck.Cards)
            deck.Add(SerializeCard(card));

        var candidateArray = new JsonArray();
        for (var i = 0; i < candidates.Count; i++)
        {
            candidateArray.Add(new JsonObject
            {
                ["index"] = i,
                ["card"] = SerializeCard(candidates[i]),
            });
        }

        var relics = new JsonArray();
        foreach (var relic in player.Relics)
            relics.Add(SerializeRelic(relic));

        var potions = new JsonArray();
        for (var i = 0; i < player.PotionSlots.Count; i++)
        {
            var potion = player.PotionSlots[i];
            if (potion is null)
                continue;
            potions.Add(new JsonObject
            {
                ["slot_index"] = i,
                ["potion"] = SerializePotion(potion, i),
            });
        }

        var policyAssembly = typeof(BuildDecisionCapture).Assembly;
        var gameAssembly = typeof(Player).Assembly;
        JsonNode? roomId = room?.Id is int id ? JsonValue.Create(id) : null;
        var runId = _runIds.GetValue(run, _ => new RunIdentity()).Id.ToString("N");

        return new JsonObject
        {
            ["schema"] = SchemaVersion,
            ["source"] = Source,
            ["captured_utc"] = UtcNow(),
            ["session"] = _sessionId,
            ["run"] = "run-" + _sessionId + "-" + runId,
            ["decision"] = decisionId,
            ["player_slot"] = run.GetPlayerSlotIndex(player),
            ["policy"] = new JsonObject
            {
                ["id"] = PolicyId,
                ["assembly"] = policyAssembly.GetName().Name,
                ["version"] = policyAssembly.GetName().Version?.ToString(),
                ["mvid"] = policyAssembly.ManifestModule.ModuleVersionId.ToString("D"),
                ["mod_version"] = ModEntry.Version,
            },
            ["game"] = new JsonObject
            {
                ["assembly"] = gameAssembly.GetName().Name,
                ["version"] = gameAssembly.GetName().Version?.ToString(),
                ["mvid"] = gameAssembly.ManifestModule.ModuleVersionId.ToString("D"),
                ["informational_version"] = gameAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            },
            ["character"] = player.Character?.Id.Entry,
            ["ascension"] = run.AscensionLevel,
            ["party_size"] = run.Players.Count,
            ["act"] = run.CurrentActIndex,
            ["floor"] = run.ActFloor,
            ["room"] = room?.RoomType.ToString(),
            ["room_id"] = roomId,
            ["hp"] = player.Creature.CurrentHp,
            ["max_hp"] = player.Creature.MaxHp,
            ["gold"] = player.Gold,
            ["deck"] = deck,
            ["candidates"] = candidateArray,
            ["relics"] = relics,
            ["potion_slot_capacity"] = player.MaxPotionCount,
            ["potions"] = potions,
            ["root_seed"] = null,
            ["rng_state"] = null,
            ["teammates_captured"] = false,
            ["missing"] = new JsonArray(
                "rng_state",
                "root_seed",
                "teammate_state",
                "applied_state",
                "legal_alternatives"),
            ["coverage_limits"] = new JsonArray(
                "SerializablePotion records only potion id and slot index; charges and runtime state are not captured.",
                "Relics use the game's SerializableRelic (id, props, floor); counters not represented there are omitted.",
                "The selection is observed; application of the selected card is not observed by this hook and stays null.",
                "Legal reward alternatives are assumed from allowSkip=true and are not independently validated.",
                "RNG state and root seed are intentionally omitted, not defaulted.",
                "Teammate inventories are not captured; only the acting player is recorded."),
            ["schema_valid"] = true,
            ["solver_eligible"] = false,
            ["ready_for_solver"] = false,
            ["complete"] = false,
        };
    }

    private static JsonNode? SerializeCard(CardModel card)
        => JsonSerializer.SerializeToNode(card.ToSerializable(), JsonSerializationUtility.GetTypeInfo<SerializableCard>());

    private static JsonNode? SerializeRelic(RelicModel relic)
        => JsonSerializer.SerializeToNode(relic.ToSerializable(), JsonSerializationUtility.GetTypeInfo<SerializableRelic>());

    private static JsonNode? SerializePotion(PotionModel potion, int slotIndex)
        => JsonSerializer.SerializeToNode(potion.ToSerializable(slotIndex), JsonSerializationUtility.GetTypeInfo<SerializablePotion>());

    private static void Append(string json)
    {
        lock (Gate)
        {
            if (_stream is null)
                return;
            try
            {
                var payload = Encoding.UTF8.GetBytes(json);
                var total = (long)payload.Length + 1;
                if (_records >= _maxRecords || _bytes + total > _maxBytes)
                {
                    DisableNoLock("session-limit");
                    return;
                }
                _stream.Write(payload, 0, payload.Length);
                _stream.WriteByte((byte)'\n');
                _stream.Flush();
                _records++;
                _bytes += total;
            }
            catch (Exception error)
            {
                DisableNoLock("append-failed:" + error.GetType().Name);
            }
        }
    }

    private static void Disable(string reason)
    {
        lock (Gate)
            DisableNoLock(reason);
    }

    private static void DisableNoLock(string reason)
    {
        try { _stream?.Dispose(); } catch { /* instrumentation must never break a run */ }
        _stream = null;
        _path = null;
        _disabledReason = reason;
    }

    private static string UtcNow() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Closes the session and clears all cached state. Test harness only.</summary>
    internal static void ResetForTest()
    {
        lock (Gate)
        {
            try { _stream?.Dispose(); } catch { /* test cleanup */ }
            _stream = null;
            _path = null;
            _sessionId = null;
            _disabledReason = null;
            _records = 0;
            _bytes = 0;
            _maxRecords = DefaultMaxRecords;
            _maxBytes = DefaultMaxBytes;
            _initialized = false;
            _runIds = new ConditionalWeakTable<object, RunIdentity>();
        }
    }

    /// <summary>Overrides the per-session bounds. Test harness only.</summary>
    internal static void ConfigureLimitsForTest(int maxRecords, long maxBytes)
    {
        lock (Gate)
        {
            _maxRecords = maxRecords;
            _maxBytes = maxBytes;
        }
    }

    internal static string? SessionPathForTest
    {
        get { lock (Gate) return _path; }
    }

    internal static int RecordCountForTest
    {
        get { lock (Gate) return _records; }
    }

    internal static string? DisabledReasonForTest
    {
        get { lock (Gate) return _disabledReason; }
    }

    internal static long ByteCountForTest
    {
        get { lock (Gate) return _bytes; }
    }
}

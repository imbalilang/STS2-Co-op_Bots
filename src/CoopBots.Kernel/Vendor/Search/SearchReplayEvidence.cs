using System.Text.Json;
using System.Text.Json.Serialization;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel.Vendor;

internal readonly record struct ReplayStepValues(int Hp, int Block, int Energy, int Stars, int HandCount);
internal sealed record ReplayStepEvidence(int ActionIndex, PlanAction Action, ReplayStepValues? Expected,
    ReplayStepValues Actual);

// Only instantiated for final-route materialization. Parent-chain scalars already exist;
// this never retains a simulator or adds a state dump to an expanded search node.
internal sealed class SearchReplayEvidence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly ReplayStepValues?[] _expected;
    private readonly IReadOnlyList<PlanAction> _actions;
    private readonly List<ReplayStepEvidence> _steps = [];
    public static void PublishCandidateFailure(SearchDiagnosticsSink sink, SearchNode node,
        string stage, PlanAction? attemptedAction = null)
        => sink.Info("[CombatSolver/Evidence] FAILED_CANDIDATE " + JsonSerializer.Serialize(new
        {
            schemaVersion = 1, stage, node.Turn, node.ActionCount, attemptedAction,
            prefix = node.Actions, turnSetupChoices = node.GetTurnSetupChoices(),
            node.Snapshot.PlayerHp, node.Snapshot.PlayerBlock, node.Snapshot.Energy, node.Snapshot.Stars,
            node.Snapshot.HandCount, node.Snapshot.EnemyHp,
            hasPendingCycleExitObservation = node.PendingCycleExitObservation != null,
        }, JsonOptions));
    public int? FirstScalarDifference { get; private set; }
    public string? FirstActualState { get; set; }
    public SearchReplayEvidence(SearchNode selected)
    {
        _actions = selected.Actions;
        _expected = new ReplayStepValues?[selected.ActionCount];
        for (SearchNode? node = selected; node?.Action != null; node = node.Parent)
        {
            SimulationSnapshot value = node.Snapshot;
            _expected[node.ActionCount - 1] = new(value.PlayerHp, value.PlayerBlock,
                value.Energy, value.Stars, value.HandCount);
        }
    }
    public bool Observe(CombatPredictionSimulator simulator, Player player, PlanAction action, int index)
    {
        SimPlayerCombatState combat = simulator.State.GetPlayerCombatState(player);
        SimCreatureState creature = simulator.State.GetCreature(player.Creature);
        ReplayStepValues actual = new(creature.CurrentHp, creature.Block, combat.Energy, combat.Stars, combat.Hand.Cards.Count);
        ReplayStepValues? expected = _expected[index];
        _steps.Add(new(index, action, expected, actual));
        if (FirstScalarDifference == null && expected.HasValue && expected.Value != actual)
        {
            FirstScalarDifference = index;
            return true;
        }
        return false;
    }
    public void Publish(SearchDiagnosticsSink sink, string reason, ActionRelicTriggerRecorder recorder,
        string? expectedFinalState = null, string? actualFinalState = null)
    {
        string traceId = Guid.NewGuid().ToString("N");
        sink.Info("[CombatSolver/Evidence] ROUTE_REPLAY " + JsonSerializer.Serialize(new
        {
            schemaVersion = 1, traceId, reason, firstScalarDifference = FirstScalarDifference,
            firstActualState = FirstActualState, expectedFinalState, actualFinalState,
            comparisonScope = "hp_block_energy_stars_hand_count_per_action; full_state_at_final_failure",
            actionCount = _actions.Count, completedSteps = _steps.Count,
        }, JsonOptions));
        for (int index = 0; index < _actions.Count; index++)
            sink.Info("[CombatSolver/Evidence] ROUTE_ACTION " + JsonSerializer.Serialize(new
            {
                traceId, index, action = _actions[index], expected = _expected[index],
                actual = index < _steps.Count ? _steps[index].Actual : (ReplayStepValues?)null,
            }, JsonOptions));
        foreach (RecordedHealthChange change in recorder.HealthChanges)
            sink.Info("[CombatSolver/Evidence] ROUTE_HEALTH " + JsonSerializer.Serialize(new { traceId, change }, JsonOptions));
    }
}

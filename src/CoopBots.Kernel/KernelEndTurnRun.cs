using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

// Upstream executes its stored line. Owner request 2026-10-04: rescue a losing
// EndTurn using actual round outcomes, not the value of energy about to expire.
// This bounded, sliced comparison supplies a short repair, never a new full fight.
public sealed class KernelEndTurnRun
{
    private sealed record Node(KernelSession State, IReadOnlyList<KernelTeamSearch.Action> Actions, double Value);
    private readonly KernelSession root;
    private readonly Player player;
    private readonly IReadOnlyList<Player> party;
    private readonly int budgetMs;
    private readonly KernelDecisionRequest? request;
    private readonly Stopwatch? phaseClock;
    private bool Stopped => request?.IsStopped == true
        || phaseClock is not null && phaseClock.Elapsed.TotalMilliseconds >= budgetMs;
    private string RequestStop => request is { IsStopped: true } stopped ? stopped.StopReason : "phase-deadline";
    private IEnumerator<Node>? trials;
    private readonly Dictionary<string, (string Card, double Value, int Steps, int Compared)> openingScores = new(StringComparer.Ordinal);
    public bool Done { get; private set; }
    public IReadOnlyList<KernelTeamSearch.Action>? Actions { get; private set; }
    public double Gain { get; private set; }
    public double ComputeMs { get; private set; }
    public int Compared { get; private set; }
    public int Attempted { get; private set; }
    public string StopReason { get; private set; } = "pending";
    public double BaselineValue { get; private set; } = double.NaN;
    public string OpeningComparison => string.Join(';', openingScores.Values.Select(r =>
        $"{r.Card}:gain={r.Value - BaselineValue:F1}:steps={r.Steps}:compared={r.Compared}"));

    public KernelEndTurnRun(KernelSession root, Player player, int budgetMs = 80,
        KernelDecisionRequest? request = null)
    {
        this.root = root; this.player = player; party = root.Party; this.budgetMs = budgetMs;
        this.request = request; phaseClock = request is null ? null : Stopwatch.StartNew();
        if (Stopped) { StopReason = RequestStop; Done = true; }
    }

    public bool Advance(TimeSpan slice)
    {
        using var budgetScope = KernelSearchBudgetScope.Enter(request);
        if (Done) return true;
        var clock = Stopwatch.StartNew();
        try
        {
            trials ??= Compare().GetEnumerator();
            do
            {
                if (Stopped) { StopReason = RequestStop; Done = true; break; }
                // Initial JIT/baseline settlement may consume the soft budget.
                // Finish at least one actual comparison before using its expiry
                // as a reason to pass; each failed trial still yields the frame.
                if (Compared > 0 && ComputeMs + clock.Elapsed.TotalMilliseconds >= budgetMs)
                { StopReason = "budget"; Done = true; break; }
                if (!trials.MoveNext()) { StopReason = "complete"; Done = true; break; }
                if (Stopped) { StopReason = RequestStop; Done = true; break; }
            } while (clock.Elapsed < slice);
        }
        finally { ComputeMs += clock.Elapsed.TotalMilliseconds; if (Done) trials?.Dispose(); }
        return Done;
    }

    private IEnumerable<Node> Compare()
    {
        if (Stopped || !root.CanFork || !root.CanAct(player) || !TryValue(root, out var baseline)) yield break;
        BaselineValue = baseline;
        var best = baseline;
        var level = new List<Node> { new(root, Array.Empty<KernelTeamSearch.Action>(), baseline) };
        yield return level[0]; // baseline settlement counts against the slice
        for (var depth = 0; depth < 4 && Attempted < 64 && level.Count > 0; depth++)
        {
            var children = new List<Node>();
            foreach (var node in level)
            {
                if (Stopped) yield break;
                if (node.State.HasTerminal || !node.State.CanAct(player)) continue;
                foreach (var card in node.State.Hand(player).DistinctBy(node.State.CardStateKey))
                {
                    if (Stopped) yield break;
                    if (!node.State.CanPlay(card)) continue;
                    foreach (var target in node.State.Targets(card))
                    {
                        if (Stopped) yield break;
                        using var branches = node.State.CardBranches(card, target, 2).GetEnumerator();
                        while (!Stopped && branches.MoveNext())
                        {
                            var branch = branches.Current;
                            if (Attempted >= 64) yield break;
                            Attempted++;
                            if (branch.Boundary.Length != 0 || !TryValue(branch.State, out var value))
                            { yield return node; continue; }
                            Compared++;
                            var action = new KernelTeamSearch.Action(player, card, target, Choices: branch.Choices,
                                ChoiceOrdinals: branch.ChoiceOrdinals, CardStateKey: node.State.CardStateKey(card))
                                { TurnStartChoices = branch.TurnStartChoices };
                            var child = new Node(branch.State, node.Actions.Append(action).ToArray(), value);
                            var opening = child.Actions[0];
                            var openingCard = opening.Card!;
                            var key = opening.CardStateKey ?? openingCard.Id.Entry;
                            var found = openingScores.TryGetValue(key, out var previous);
                            openingScores[key] = (openingCard.Id.Entry + (openingCard.IsUpgraded ? "+" : ""),
                                found ? Math.Max(previous.Value, value) : value,
                                !found || value > previous.Value ? child.Actions.Count : previous.Steps,
                                found ? previous.Compared + 1 : 1);
                            if (value > best + .01)
                            { best = value; Gain = value - baseline; Actions = child.Actions; }
                            children.Add(child);
                            yield return child;
                        }
                    }
                }
            }
            level = children.OrderByDescending(n => n.Value).Take(4).ToList();
        }
    }

    private bool TryValue(KernelSession beforeEnd, out double value)
    {
        value = 0;
        if (Stopped) return false;
        var end = beforeEnd.Fork(); end.AutoResolveEnemyChoices = true; end.AutoResolveTurnStartChoices = true;
        foreach (var member in party)
        {
            if (Stopped) return false;
            if (end.CanAct(member) && !end.EndTurn(member, end.RoundsAdvanced + 1, out _)) return false;
        }
        if (!end.HasTerminal && !end.EnemyPhaseCompleted) return false;
        // No next-turn energy, draw, or repeated attack-intent charge. Both
        // branches have finished exactly the same enemy phase.
        value = (end.HasWon ? 1_000_000 : 0)
            - end.RemainingOpposition().Hp * .8 + end.FutureDebuffValue(party, elapsedTurns: 0, reference: root);
        foreach (var member in party)
        {
            var hp = end.Hp(member.Creature);
            if (hp <= 0) { value -= 10_000; continue; }
            value += hp + end.Block(member.Creature) + end.PersistentValue(member, 0, 0, root) + KernelExhaustPolicy.Reserve(end, member);
        }
        return true;
    }
}

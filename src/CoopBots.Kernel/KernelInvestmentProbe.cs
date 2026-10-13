using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

// Existing tactical pruning prices an investment before its triggers happen.
// Compare equal, native two-round continuations, including ordinary
// continuation baselines, for ANY held power. One simulated action per Step keeps the
// existing frame/deadline scheduler in control. No nested lookahead.
internal sealed class KernelInvestmentProbe
{
    private readonly KernelSession root;
    private readonly IReadOnlyList<Player> party, actors;
    private readonly RolloutOptions options;
    private readonly List<(KernelTeamSearch.Action? Opening, RolloutPolicyKind Policy)> jobs = new();
    private int index;
    private RolloutRun? run;
    private KernelTeamSearch.Action? opening;
    private double bestValue = double.NegativeInfinity;
    internal KernelTeamSearch.Action? Choice { get; private set; }
    internal bool Done => index >= jobs.Count && run is null;
    internal int Completed { get; private set; }
    internal int CompletedPowerTrials { get; private set; }

    internal KernelInvestmentProbe(KernelSession root, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, RolloutOptions options)
    {
        this.root = root; this.party = party; this.actors = actors; this.options = options;
        jobs.Add((null, RolloutPolicyKind.Kill));
        jobs.Add((null, RolloutPolicyKind.Defend));
        // A cheap continuation is not an adequate control for Tactical. Keep
        // the same resolved team-defence policy in the comparison, without
        // recursively starting another investment probe.
        jobs.Add((null, RolloutPolicyKind.Tactical));
        // All legal powers, independent of their model's current heuristic
        // score. Duplicate instances share one trial, different owners do not.
        foreach (var policy in new[] { RolloutPolicyKind.Growth, RolloutPolicyKind.Defend })
        foreach (var p in actors.Where(root.CanAct))
        foreach (var card in root.Hand(p).Where(c => c.Type == CardType.Power && root.CanPlay(c))
            .DistinctBy(root.CardStateKey))
        foreach (var target in root.Targets(card).Take(1))
        {
            var action = new KernelTeamSearch.Action(p, card, target, CardStateKey: root.CardStateKey(card));
            jobs.Add((action, policy));
        }
    }

    internal void Step()
    {
        if (Done) return;
        if (run is null)
        {
            var job = jobs[index]; opening = job.Opening;
            KernelSession branch;
            if (opening is not null)
            {
                var resolved = root.CardBranches(opening.Card!, opening.Target, 1)
                    .FirstOrDefault(b => b.Boundary.Length == 0);
                if (resolved is null) { index++; return; }
                branch = resolved.State;
                opening = opening with { Choices = resolved.Choices, ChoiceOrdinals = resolved.ChoiceOrdinals,
                    TurnStartChoices = resolved.TurnStartChoices };
            }
            else branch = root.Fork();
            run = new RolloutRun(branch, party, actors, options with
            {
                Policy = job.Policy, MaxRounds = root.RoundsAdvanced + 2,
                MaxSteps = 80, InvestmentLookahead = false, SequenceLookahead = false,
            });
            return;
        }
        // Future suffixes supply only a score; Choice retains the separately
        // resolved native opening above. Bound nested menu scoring here without
        // changing or exporting the suffix's default choice tokens.
        using (KernelChoiceContinuation.Enter()) run.Step();
        if (!run.Done) return;
        // A boundary/step cutoff did not reach the same horizon as its peers.
        // Keep the regular tactical fallback; never label this a win or loss.
        if (run.State.CanFork && (run.Result!.Terminal || run.State.RoundsAdvanced >= root.RoundsAdvanced + 2
            || run.State.EnemyPhaseCompleted && run.State.RoundsAdvanced >= root.RoundsAdvanced + 1))
        {
            Completed++;
            if (opening?.Card?.Type == CardType.Power) CompletedPowerTrials++;
            var value = KernelTacticalPolicy.ActionValue(root, run.State, party, actors);
            if (options.Policy == RolloutPolicyKind.Defend && !run.State.HasWon)
                value += party.Sum(p => run.State.Hp(p.Creature) > 0
                    ? 3 * run.State.Hp(p.Creature) + run.State.Block(p.Creature) : -10000);
            if (value > bestValue + .01)
            {
                bestValue = value;
                // A control that beats investing means "do not force a power".
                // Let the caller's policy choose its ordinary action; importing
                // Kill's first card here silently replaces Tactical with Kill.
                Choice = opening;
            }
        }
        run = null; index++;
    }
}

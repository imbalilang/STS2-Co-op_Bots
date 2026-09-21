using CoopBots.Kernel.Vendor;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel;

public sealed record KernelChoiceCard(string Id, int Upgrade, string StateKey, int Occurrence);
public sealed record KernelChoice(ulong Owner, string Source, string Effect, PileType Pile,
    string Context, int Minimum, int Maximum, IReadOnlyList<KernelChoiceCard> Cards)
{
    public IReadOnlyList<CardModel>? Resolve(ulong owner, IReadOnlyList<CardModel> options, int minimum, int maximum)
    {
        if (Owner != owner || Cards.Count < minimum || Cards.Count > maximum
            || Cards.Count < Minimum || Cards.Count > Maximum) return null;
        var selected = new List<CardModel>();
        foreach (var token in Cards)
        {
            if (token.Occurrence < 0) return null;
            var model = options.Where(c => CardChoiceSupport.MatchesToken(c,
                new PlanCardToken(token.Id, token.Upgrade, token.StateKey, token.Occurrence, token.Occurrence, token.Id)))
                .Skip(token.Occurrence).FirstOrDefault();
            if (model is null || selected.Contains(model) || CardChoiceSupport.ChoiceCardKey(model) != token.StateKey) return null;
            selected.Add(model);
        }
        return selected;
    }
}

public sealed partial class KernelSession
{
    /// <summary>
    /// <paramref name="ChoiceOrdinals"/> is the branch's bounded choice vector. It is
    /// carried alongside the resolved <paramref name="Choices"/> so a finished plan can
    /// be replayed deterministically without re-enumerating the choice space.
    /// </summary>
    public sealed record CardBranch(KernelSession State, IReadOnlyList<KernelChoice> Choices, string Boundary,
        IReadOnlyList<int> ChoiceOrdinals);
    private int[]? choiceOrdinals;
    private int choicePosition;
    private readonly List<int> choiceCounts = new();
    private readonly List<KernelChoice> resolvedChoices = new();

    // Replays from a settled parent for each bounded choice vector. Pending
    // transactions are never forked. Nested choices are enumerated as well.
    public IEnumerable<CardBranch> CardBranches(CardModel card, MegaCrit.Sts2.Core.Entities.Creatures.Creature? target,
        int maximumBranches = 16)
    {
        var queue = new Queue<int[]>(); queue.Enqueue([]);
        var seen = new HashSet<string> { "" };
        for (var tries = 0; tries < maximumBranches && queue.TryDequeue(out var vector); tries++)
        {
            var child = Fork(); child.choiceOrdinals = vector;
            var success = child.Play(card, target, out var boundary);
            var used = Enumerable.Range(0, child.choiceCounts.Count).Select(i => i < vector.Length ? vector[i] : 0).ToArray();
            for (var i = 0; i < used.Length; i++)
            for (var alternative = 0; alternative < child.choiceCounts[i]; alternative++)
            {
                if (alternative == used[i]) continue;
                var next = used.Take(i + 1).ToArray(); next[i] = alternative;
                if (seen.Add(string.Join(",", next))) queue.Enqueue(next);
            }
            yield return new(child, child.resolvedChoices.ToArray(), success ? "" : boundary, vector);
        }
    }

    /// <summary>
    /// Replays one recorded plan action on this branch, driving card choices from the
    /// recorded bounded vector instead of re-enumerating the choice space.
    /// </summary>
    /// <remarks>
    /// This exists so the per-action predicted states of a finished plan can be
    /// recovered AFTER the search, by replaying only the chosen path from the captured
    /// root. Recording them per search node instead would pay a full state render across
    /// the whole tree rather than once per action that is actually deployed.
    ///
    /// A false return means the path no longer replays — the caller must treat the plan
    /// as unverified rather than as verified-and-matching.
    /// </remarks>
    public bool ReplayPlanned(KernelTeamSearch.Action action, int maxRounds, out string boundary)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        // Reset the choice gate per action: the vector is positional and starts at zero
        // for every play. An action with no recorded vector has no bounded choice to
        // drive, which is the same "no plan" state the search itself ran with.
        // An EMPTY vector is NOT the same as no vector. CardBranches seeds its queue with
        // the empty vector (queue.Enqueue([])), so a card whose first branch simply used
        // the DEFAULT choice index carries `[]`, not null — the search ran that branch
        // with choiceOrdinals = [] and let the cursor fall back to index 0. Treating `[]`
        // as null here disabled the cursor entirely, ManualPlay returned false, and the
        // step failed with `pending-choice`. One SURVIVOR in the path was therefore
        // enough to empty ActionStates for a whole 104-action route.
        choiceOrdinals = action.ChoiceOrdinals is { } ordinals ? ordinals.ToArray() : null;
        choicePosition = 0;
        if (action.EndTurn) return EndTurn(action.Player, maxRounds, out boundary);
        if (action.Potion is { } potion) return UsePotion(potion, action.Target, out boundary);
        if (action.Card is { } plannedCard)
        {
            // Resolve by IDENTITY first, never by the stored instance. For a generated card
            // (Shiv, Mirage…) that instance is a fresh simulated object whose Original is a
            // new CardModel, and Play's FindCard matches by plain reference equality — so
            // handing it straight through failed every such step with `unplayable`, which
            // is how one Shiv in a path emptied the sentinel for the whole route.
            //
            // The instance fallback covers actions that carry no key at all; upstream's
            // FindCardForDeployment has the same two-step shape.
            var resolved = FindSimulatedCardByKey(
                    action.Player, action.CardStateKey, action.CardStateOccurrence)
                ?? simulator.State.FindCard(plannedCard);
            if (resolved is null)
            {
                boundary = "planned-card-not-found";
                return false;
            }
            return Play(resolved.Original, action.Target, out boundary);
        }
        boundary = "empty-action";
        return false;
    }

    private TurnStartChoiceCursor ChoiceCursor(MegaCrit.Sts2.Core.Entities.Players.Player owner)
        => TurnStartChoiceCursor.ForAutomaticPolicy(request =>
        {
            if (choiceOrdinals is null) return null;
            var spec = TurnStartChoiceSupport.BuildSpec(simulator, owner, request);
            var options = CardChoiceSupport.BuildChoices(spec, null, 8, 8).Take(8).ToArray();
            choiceCounts.Add(options.Length);
            var index = choicePosition < choiceOrdinals.Length ? choiceOrdinals[choicePosition] : 0;
            choicePosition++;
            if (index >= options.Length) throw new InvalidPlannedChoiceBranchException("Choice vector no longer matches this branch.");
            var chosen = options[index] with { SourceId = request.SourceId, ContextId = request.ContextId, Timing = request.Timing };
            var actualOwner = spec.Options.FirstOrDefault()?.Preview.Owner ?? owner;
            resolvedChoices.Add(new(actualOwner.NetId, request.SourceId, request.Effect.ToString(), request.SourcePile,
                request.ContextId, spec.MinCount, spec.MaxCount, chosen.Cards.Select(c =>
                    new KernelChoiceCard(c.CardId, c.UpgradeLevel, c.StateKey, c.OptionOccurrence)).ToArray()));
            return chosen;
        });
}

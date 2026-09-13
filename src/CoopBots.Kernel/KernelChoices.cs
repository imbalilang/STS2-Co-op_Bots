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
    public sealed record CardBranch(KernelSession State, IReadOnlyList<KernelChoice> Choices, string Boundary);
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
            yield return new(child, child.resolvedChoices.ToArray(), success ? "" : boundary);
        }
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

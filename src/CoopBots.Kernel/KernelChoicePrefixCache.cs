using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

// One Evaluate owns this exact native prefix tree. Every policy starts from a
// private clone of the same prepared seed, follows native action/token edges,
// and receives owned forks. No state hash, unrelated root, or later menu is shared.
internal sealed class KernelChoicePrefixCache(KernelSession seed)
{
    private readonly Node root = new();
    private int stored;
    internal int Hits { get; private set; }
    internal KernelSession ForkSeed() => seed.Fork();
    internal Cursor CreateCursor() => new(this);
    private sealed class Node
    {
        internal readonly Dictionary<Key, Edge> Edges = new();
    }
    private sealed record Edge(KernelSession Template, IReadOnlyList<KernelChoice> Choices,
        IReadOnlyList<int> Ordinals, string Boundary, bool Success, Key Completed, Node Next)
    {
        internal KernelTurnStartChoiceContract? TurnStartChoices { get; init; }
    }
    private readonly record struct Key(Player Owner, MegaCrit.Sts2.Core.Models.CardModel? Card,
        MegaCrit.Sts2.Core.Models.PotionModel? Potion, MegaCrit.Sts2.Core.Entities.Creatures.Creature? Target, string StateKey, int Occurrence,
        bool EndTurn, int MaxRounds, bool HasChoices, bool HasOrdinals, string Tokens, string Ordinals,
        string TurnStartContract);
    private static Key Identity(KernelTeamSearch.Action action, int maxRounds = 0)
        => new(action.Player, action.Card, action.Potion,
            action.Target,
            action.CardStateKey, action.CardStateOccurrence, action.EndTurn, maxRounds,
            action.Choices is not null, action.ChoiceOrdinals is not null,
            action.Choices is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(action.Choices) : "",
            action.ChoiceOrdinals is { Count: > 0 } ? string.Join(',', action.ChoiceOrdinals) : "",
            action.TurnStartChoices is null ? "not-recorded" : System.Text.Json.JsonSerializer.Serialize(action.TurnStartChoices));

    internal sealed class Cursor
    {
        private readonly KernelChoicePrefixCache cache;
        private Node node;
        private Edge? pending;
        private int committed;
        private bool enabled = true;
        internal Cursor(KernelChoicePrefixCache cache) { this.cache = cache; node = cache.root; }
        private bool Matches(int count)
        {
            if (count != committed) enabled = false; // an uncached native transition
            return enabled;
        }
        internal KernelSession.CardBranch? Card(int count, KernelTeamSearch.Action action,
            Func<KernelSession.CardBranch?> native)
        {
            pending = null;
            if (!Matches(count)) return native();
            var key = Identity(action);
            if (node.Edges.TryGetValue(key, out var hit))
            {
                cache.Hits++; pending = hit;
                return new(hit.Template.Fork(), hit.Choices, hit.Boundary, hit.Ordinals)
                    { TurnStartChoices = hit.TurnStartChoices };
            }
            var branch = native();
            if (branch is null || branch.Boundary.Length != 0 || !branch.State.CanFork
                || cache.stored >= KernelChoiceContinuation.MaximumSteps) return branch;
            var completed = action with { Choices = branch.Choices, ChoiceOrdinals = branch.ChoiceOrdinals,
                TurnStartChoices = branch.TurnStartChoices };
            var edge = new Edge(branch.State.Fork(), branch.Choices.ToArray(), branch.ChoiceOrdinals.ToArray(),
                branch.Boundary, true, Identity(completed), new()) { TurnStartChoices = branch.TurnStartChoices };
            node.Edges[key] = edge; cache.stored++; pending = edge;
            return branch;
        }
        internal void Commit(KernelTeamSearch.Action action)
        {
            if (pending is null || pending.Completed != Identity(action)) { enabled = false; return; }
            node = pending.Next; committed++; pending = null;
        }
        internal bool End(int count, KernelSession before, Player player, int maxRounds,
            out KernelSession after, out string boundary, out KernelTeamSearch.Action resolvedAction)
        {
            pending = null;
            if (!Matches(count)) { after = before; return after.EndTurn(player, maxRounds, out boundary, out resolvedAction); }
            var action = new KernelTeamSearch.Action(player, null, null, EndTurn: true);
            var key = Identity(action, maxRounds);
            if (node.Edges.TryGetValue(key, out var hit))
            {
                cache.Hits++; after = hit.Template.Fork(); boundary = hit.Boundary;
                resolvedAction = action with { TurnStartChoices = hit.TurnStartChoices };
                node = hit.Next; committed++;
                return hit.Success;
            }
            // before is owned by this run: ForkSeed, a native CardBranch, or
            // a cache-hit Fork. Neither the shared seed nor an edge template
            // is ever handed to a run; settle the owned receiver once.
            after = before;
            var success = after.EndTurn(player, maxRounds, out boundary, out resolvedAction);
            if (success && after.CanFork && cache.stored < KernelChoiceContinuation.MaximumSteps)
            {
                var edge = new Edge(after.Fork(), [], [], boundary, true, key, new())
                    { TurnStartChoices = resolvedAction.TurnStartChoices };
                node.Edges[key] = edge; cache.stored++; node = edge.Next; committed++;
            }
            else enabled = false;
            return success;
        }
    }
}

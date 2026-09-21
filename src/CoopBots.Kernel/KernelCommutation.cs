using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

/// <summary>
/// Whether two actions may be treated as order-independent.
///
/// <see cref="Commutes"/> true means the two orders were SIMULATED and left identical state —
/// not that they look independent. The plan is explicit about which way to be wrong here
/// (§P3-04): "共享触发、RNG 消费、目标死亡和条件效果默认视为可能相关", and "未知规则不进入白名单".
/// A false negative costs a larger search space; a false positive silently changes what a plan
/// does, which is the failure this project keeps paying for.
/// </summary>
public sealed record CommutationVerdict(bool Commutes, string Reason, string Difference)
{
    internal static CommutationVerdict No(string reason, string difference = "") =>
        new(false, reason, difference);
}

/// <summary>
/// Decides whether a pair of actions commutes, and holds the conservatively-built white-list.
///
/// WHY THIS IS THE LEVER FOR A JOINT SEARCH SPECIFICALLY: a four-seat round is roughly sixteen
/// actions, and the joint search expands orderings of them. Any pair proven independent halves
/// the orderings that have to be explored separately, and in a fight where several seats act on
/// different enemies most pairs are independent. The plan lists this last (§P3-04) and default
/// OFF, which is the right default — but for a multi-seat search it is the lever that targets
/// the multi-seat explosion, which node-count reductions do not.
///
/// The check is a SIMULATION, not a rule: two forks of the same root, the two orders, then a
/// full-state comparison. Nothing here reasons about card text.
/// </summary>
public static class KernelCommutation
{
    /// <summary>
    /// <paramref name="root"/> is forked twice and never mutated.
    /// <paramref name="related"/> lets a caller declare a pair suspect up front — the white-list
    /// path uses it so that a pair which WAS proven once is still re-checked when the board
    /// stops matching the conditions it was proven under.
    /// </summary>
    public static CommutationVerdict Check(KernelSession root,
        KernelTeamSearch.Action first, KernelTeamSearch.Action second)
    {
        // Cheap structural refusals first. Each is a case the plan names explicitly, and each
        // costs one comparison instead of two simulations.
        if (first.EndTurn || second.EndTurn)
            return CommutationVerdict.No("end-turn-is-a-boundary");
        // NO "same seat shares energy" GUARD. It was here, and it was wrong: it refused every
        // same-seat pair before the simulation ran, which is exactly the case where conditional
        // effects (Vulnerable, Strength, an enemy that dies) decide the answer. Affordability
        // needs no guard either — an order whose second card is unaffordable simply fails to
        // play, and that comes back as an explicit `ab-unplayable`/`ba-unplayable` verdict.
        // Measured: with the guard in place the Bash/Strike pair was refused as
        // `same-seat-shares-energy` and the divergence was never simulated.
        if (first.Potion is not null || second.Potion is not null
            || first.Choices is not null || second.Choices is not null)
            return CommutationVerdict.No("choice-or-potion-consumes-shared-resource");

        string forward, reverse;
        try
        {
            forward = RunOrder(root, first, second, out var forwardStop);
            if (forwardStop.Length > 0) return CommutationVerdict.No("ab-unplayable:" + forwardStop);
            reverse = RunOrder(root, second, first, out var reverseStop);
            if (reverseStop.Length > 0) return CommutationVerdict.No("ba-unplayable:" + reverseStop);
        }
        catch (Exception error)
        {
            // A mirror that throws means the pair was not proven, not that it commutes.
            return CommutationVerdict.No("ab-ba-exception:" + error.GetType().Name);
        }
        if (forward == reverse) return new CommutationVerdict(true, "simulated-equal", "");
        return CommutationVerdict.No("state-diverged", FirstDifference(forward, reverse));
    }

    /// <summary>
    /// Play both actions on a fresh fork and render the resulting state. Both
    /// <c>CompactStateKey</c> (HP, block, energy, readiness, hand identity, enemy HP, powers,
    /// round) and <c>PartyStateText</c> (the per-seat rendering deployment already trusts) go in,
    /// because neither alone is the "complete state" the plan asks for.
    /// </summary>
    private static string RunOrder(KernelSession root, KernelTeamSearch.Action first,
        KernelTeamSearch.Action second, out string stop)
    {
        var branch = root.Fork();
        stop = "";
        foreach (var action in new[] { first, second })
        {
            if (action.Card is null) { stop = "not-a-card"; return ""; }
            if (!branch.Play(action.Card, action.Target, out var boundary)) { stop = boundary; return ""; }
        }
        return branch.CompactStateKey() + "\n--\n" + branch.PartyStateText();
    }

    private static string FirstDifference(string a, string b)
    {
        var left = a.Split('\n');
        var right = b.Split('\n');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
            if (left[i] != right[i]) return $"line {i}: '{left[i]}' vs '{right[i]}'";
        return $"length {left.Length} vs {right.Length}";
    }
}

/// <summary>
/// Pairs that have been PROVEN to commute, with the conditions they were proven under.
///
/// Empty on purpose. The plan's config default is `ActionCommutation: Off`, and a white-list
/// entry is a claim that must be re-established whenever the rules or the board change — so
/// entries are added by a caller that has just run <see cref="KernelCommutation.Check"/>, not
/// seeded here from intuition. An entry with no evidence behind it is worse than no entry:
/// it turns a search-space optimisation into an unverified assumption about the game.
/// </summary>
public sealed class CommutationWhiteList
{
    private readonly Dictionary<(string, string), string> proven = new();

    public int Count => proven.Count;

    /// <summary>
    /// Records a pair only if the check actually proved it. Passing an unproven pair is a
    /// programming error and throws rather than silently widening the white-list.
    /// </summary>
    public void Add(KernelSession root, KernelTeamSearch.Action first, KernelTeamSearch.Action second)
    {
        var verdict = KernelCommutation.Check(root, first, second);
        if (!verdict.Commutes)
            throw new InvalidOperationException(
                $"refusing to white-list an unproven pair: {verdict.Reason} {verdict.Difference}");
        proven[Key(first, second)] = verdict.Reason;
    }

    public bool Contains(KernelTeamSearch.Action first, KernelTeamSearch.Action second) =>
        proven.ContainsKey(Key(first, second));

    private static (string, string) Key(KernelTeamSearch.Action a, KernelTeamSearch.Action b)
    {
        var left = Tag(a);
        var right = Tag(b);
        // Order-insensitive: commuting is symmetric, so looking it up must be too.
        return string.CompareOrdinal(left, right) <= 0 ? (left, right) : (right, left);
    }

    private static string Tag(KernelTeamSearch.Action action) =>
        $"{action.Player.NetId}:{action.CardStateKey}#{action.CardStateOccurrence}->{action.Target?.CombatId}";
}

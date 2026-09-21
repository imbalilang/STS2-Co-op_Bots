using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel;

public sealed record TournamentOptions(
    // How many first actions to try. Each one costs a fork of the root plus a full rollout, so
    // this is the knob that converts directly into latency: measured on the dev machine a
    // complete rollout is ~7 MB and ~50-130 ms, and the plan's budget is 1500 ms ideal /
    // 5000 ms ceiling.
    int TopK = 6,
    RolloutPolicyKind Policy = RolloutPolicyKind.Kill,
    RolloutOptions? Rollout = null,
    // Never start a rollout with less than this much of the budget left. A rollout cannot be
    // preempted, so starting one on a nearly-spent budget is how a deadline gets missed.
    int ReserveMs = 300,
    /// <summary>
    /// Whether potions are candidates at all.
    ///
    /// WHY THIS IS A SWITCH AND NOT A PRICE: a potion is a CROSS-FIGHT resource, and the
    /// objective prices consumables at zero by default (ConsumableUnitCost = 0), so a tournament
    /// that always saw potions would drink them the moment they improved this fight — which they
    /// almost always do, and which is the wrong trade. The alternative was to invent an HP price
    /// per potion; that is a made-up number. Instead the caller asks twice: once without potions,
    /// and only if that line does not WIN does it ask again with them. A potion then costs
    /// nothing in the common case and is spent exactly when it changes the outcome.
    /// </summary>
    bool IncludePotions = true,
    // False evaluates only Rollout.Policy (or Policy if Rollout is absent), for A/B probes.
    bool UsePolicyPortfolio = true);

/// <summary>
/// What the tournament decided. <see cref="FirstAction"/> is the deployable answer and is
/// non-null whenever any candidate was legal — this is the plan's "始终保留可执行方案"
/// (§06) made structural rather than promised.
/// </summary>
public sealed record TournamentResult(
    KernelTeamSearch.Action? FirstAction,
    TerminalRecord? Record,
    IReadOnlyList<TerminalRecord> Considered,
    int Rollouts,
    int Cutoffs,
    string StopReason);

/// <summary>
/// Rolls the fight out from each of the best few first actions and picks the one whose ending
/// is best under the shared objective. This is the plan's P1-02 + P2-01 in their smallest
/// useful form (§06 "以分级估值筛选，并对领先候选续演到终局").
///
/// Why this rather than a deeper search: a bounded search cannot reach a terminal by
/// construction, so it ranks lines by an estimate of an ending it never saw. A rollout reaches
/// a REAL ending, and measured on this machine one costs ~27 ms against ~0.40 ms for a single
/// expanded search node — so for the price of a few hundred search nodes you get an outcome
/// instead of a guess.
///
/// Enabled by the live planner only when the experimental tournament switch is selected.
/// </summary>
public static class KernelTournament
{
    /// <summary>
    /// <paramref name="root"/> is FORKED per candidate and never mutated, so the caller's
    /// session — including a live one — is left exactly as it was.
    /// </summary>
    public static TournamentResult Run(KernelSession root, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, TournamentOptions? options, int budgetMs)
    {
        var o = options ?? new TournamentOptions();
        if (!root.CanFork)
            return new TournamentResult(null, null, [], 0, 0, "unresolved-root");
        var rolloutOptions = o.Rollout ?? new RolloutOptions(o.Policy);
        var watch = Stopwatch.StartNew();

        var candidates = FirstActions(root, actors, o.TopK, o);
        if (candidates.Count == 0)
            return new TournamentResult(null, null, [], 0, 0, "no-legal-action");

        var considered = new List<TerminalRecord>();
        KernelTeamSearch.Action? bestAction = null;
        TerminalRecord? bestRecord = null;
        var rollouts = 0;
        var cutoffs = 0;
        var stop = "top-k-exhausted";

        // Rollout.Run mutates its argument. Every policy must start from an independent
        // copy of the SAME opened position, never from another policy's terminal state.
        var policies = o.UsePolicyPortfolio
            ? new[] { rolloutOptions.Policy, RolloutPolicyKind.Kill, RolloutPolicyKind.Defend, RolloutPolicyKind.Growth }.Distinct().ToArray()
            : new[] { rolloutOptions.Policy };
        foreach (var candidate in candidates)
        {
            if (watch.ElapsedMilliseconds + o.ReserveMs > budgetMs && rollouts > 0)
            {
                stop = "deadline";
                break;
            }
            KernelSession branch;
            var action = candidate;
            if (candidate.Potion is { } potion)
            {
                branch = root.Fork();
                if (!branch.UsePotion(potion, candidate.Target, out _)) continue;
            }
            else
            {
                var opened = root.CardBranches(candidate.Card!, candidate.Target, maximumBranches: 1)
                    .FirstOrDefault(b => b.Boundary.Length == 0);
                if (opened is null) continue;
                branch = opened.State;
                action = candidate with { Choices = opened.Choices, ChoiceOrdinals = opened.ChoiceOrdinals };
            }
            foreach (var policy in policies)
            {
                if (watch.ElapsedMilliseconds + o.ReserveMs > budgetMs && rollouts > 0)
                {
                    stop = "deadline";
                    break;
                }
                var outcome = KernelRollout.Run(branch.Fork(), party, actors,
                    rolloutOptions with { Policy = policy });
                rollouts++;
                considered.Add(outcome.Record);
                if (outcome.Cutoff) cutoffs++;
                if (bestRecord is null || TerminalComparer.Compare(outcome.Record, bestRecord) > 0)
                {
                    bestRecord = outcome.Record;
                    bestAction = action;
                }
            }
            if (stop == "deadline") break;
        }

        if (bestAction is null)
            return new TournamentResult(null, null, considered, rollouts, cutoffs, "no-playable-first-action");
        return new TournamentResult(bestAction, bestRecord, considered, rollouts, cutoffs, stop);
    }

    /// <summary>
    /// The first actions worth rolling out, best-looking first. Deliberately the SAME cheap
    /// scoring the rollout policy uses: the tournament's job is to compare ENDINGS, so spending
    /// the evaluator on ranking the first move would pay twice for the same guess.
    /// </summary>
    private static List<KernelTeamSearch.Action> FirstActions(KernelSession root,
        IReadOnlyList<Player> actors, int topK, TournamentOptions o)
    {
        var scored = new List<(KernelTeamSearch.Action Action, double Score)>();
        var seenCardStates = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var player in actors)
        {
            if (!root.CanAct(player)) continue;
            // POTIONS ARE FIRST-CLASS CANDIDATES, exactly as they are for the search
            // (KernelTeamSearch builds potion actions the same way). Leaving them out was why a
            // tournament-driven build never drank: the tournament could not SEE a potion, so no
            // amount of evaluating would ever choose one. A potion is a free action, so it is
            // scored by what it enables rather than by damage — the roll-out decides, since the
            // candidate list only has to contain it.
            foreach (var potion in o.IncludePotions ? root.UsablePotions(player) : [])
            {
                if (!KernelSession.CanModelPotion(potion)) continue;
                Creature? potionTarget = null;
                var lowestPotionTarget = int.MaxValue;
                foreach (var candidate in root.PotionTargets(potion))
                {
                    if (candidate is not { } t) continue;
                    var hp = root.Hp(t);
                    if (hp >= lowestPotionTarget) continue;
                    lowestPotionTarget = hp; potionTarget = t;
                }
                // Above every card: a potion costs no energy, so it must not be crowded out of
                // the top-K by a card that merely deals a little more damage. Whether it is
                // actually worth spending is the ROLL-OUT's question, not this ranking's.
                scored.Add((new KernelTeamSearch.Action(player, null, potionTarget, potion), 1e8));
            }
            seenCardStates.Clear();
            foreach (var card in root.Hand(player))
            {
                var key = Vendor.CardChoiceSupport.ChoiceCardKey(card);
                var occurrence = seenCardStates.GetValueOrDefault(key);
                seenCardStates[key] = occurrence + 1;
                if (!root.CanPlay(card)) continue;
                // One target per card: the full target space belongs to the search, and a
                // tournament that branched on targets would cost |cards| x |enemies| rollouts.
                Creature? target = null;
                var lowest = int.MaxValue;
                foreach (var candidate in root.Targets(card))
                {
                    if (candidate is not { } t) continue;
                    var hp = root.Hp(t);
                    if (hp >= lowest) continue;
                    lowest = hp; target = t;
                }
                var damage = card.DynamicVars.Values.OfType<DamageVar>().Sum(v => (double)v.BaseValue);
                var lethal = damage > 0 && target is not null && damage >= root.Hp(target);
                scored.Add((new KernelTeamSearch.Action(player, card, target,
                    CardStateKey: key, CardStateOccurrence: occurrence), (lethal ? 1e9 : 0) + damage));
            }
        }
        return scored
            .OrderByDescending(entry => entry.Score)
            .Take(Math.Max(1, topK))
            .Select(entry => entry.Action)
            .ToList();
    }
}

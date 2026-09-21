using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel;

/// <summary>
/// The three cheap, deterministic baseline policies the plan asks for
/// (STS2_Bot_Solver_Implementation_Plan_v1.0 §06 "基线策略组合", §P1-02).
///
/// They are deliberately NOT an attempt at good play. The plan's own words: "它们只需稳定
/// 推进，不承担最优性证明". Their job is to turn "is this position good?" into "play the
/// rest of the fight out cheaply and look at the real ending", which is the only leaf
/// evaluation that fits a 1.5 s decision budget — measured on this machine, one complete
/// rollout to victory costs ~27 ms and ~2 MB, against ~0.40 ms and ~0.094 MB for a single
/// expanded search node.
/// </summary>
public enum RolloutPolicyKind
{
    /// <summary>Finishing first: prefer the hit that kills, then the biggest hit.</summary>
    Kill,
    /// <summary>Survival first: prefer the most block, then the biggest hit.</summary>
    Defend,
    /// <summary>Setup first: prefer powers and other non-damage, non-block cards, then damage.</summary>
    Growth,
}

public sealed record RolloutOptions(
    RolloutPolicyKind Policy = RolloutPolicyKind.Kill,
    // A rollout that cannot finish inside these bounds is a CUTOFF, never a fabricated
    // defeat. The plan is explicit that a truncated line must not be reported as a loss.
    int MaxSteps = 600,
    int MaxRounds = 80,
    // Ties are broken from THIS stream. Rolling a random tie out of the combat RNG would
    // make the simulated fight disagree with the real one and poison the live battle.
    int Seed = 0);

/// <summary>
/// What one rollout found. <see cref="Terminal"/> is true only for a real ending the
/// simulation produced — a victory or a party wipe. Everything else is a cutoff and says
/// why in <see cref="StopReason"/>.
/// </summary>
public sealed record RolloutOutcome(
    bool Terminal,
    bool Victory,
    string StopReason,
    IReadOnlyList<KernelTeamSearch.Action> Actions,
    int Steps,
    int RoundsAdvanced,
    TerminalRecord Record)
{
    public bool Cutoff => !Terminal;
}

/// <summary>
/// Plays one fight forward to its end with a fixed cheap policy.
///
/// This is the piece the search has never had. <c>KernelTeamSearch</c> explores a tree and
/// stops at a horizon; it can only report a terminal if the fight happens to end inside
/// that horizon, which is why 5-round segments cannot answer "does this win?". A rollout
/// simulates ONE line all the way down, so the answer is the real ending rather than an
/// estimate of it.
///
/// COST: one settled fork per attempted card, one choice branch, no search tree.
/// Failed card transactions are discarded; EndTurn can advance the supplied session.
///
/// The caller owns <paramref name="session"/>. It is mutated in place, so it must be a
/// fork or a private capture, never the live board's session.
/// </summary>
public static class KernelRollout
{
    public static RolloutOutcome Run(KernelSession session, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, RolloutOptions? options = null)
    {
        var o = options ?? new RolloutOptions();
        var driven = actors.ToHashSet();
        var plannerRng = new Random(o.Seed);
        var actions = new List<KernelTeamSearch.Action>();
        var candidates = new List<(CardModel Card, Creature? Target, string Key, int Occurrence, double Score)>();
        var recentStates = new Queue<string>();
        var steps = 0;

        while (true)
        {
            if (session.HasTerminal)
                return new RolloutOutcome(true, session.HasWon, session.HasWon ? "victory" : "wipe",
                    actions, steps, session.RoundsAdvanced, Record(session, party, true, ""));
            if (steps >= o.MaxSteps) return Cutoff("step-cap", actions, steps, session, party, o);
            if (session.RoundsAdvanced >= o.MaxRounds) return Cutoff("round-cap", actions, steps, session, party, o);

            var acted = false;
            var stuck = "no-actor";
            foreach (var p in party)
            {
                if (!session.CanAct(p)) continue;
                if (!driven.Contains(p))
                {
                    // A seat this policy does not drive — a human, in mixed play. The plan's
                    // "player does nothing" stress scenario (§08): end the seat's turn
                    // rather than inventing an action on a human's behalf. A real causal
                    // player model replaces this, and that is P4, not P1.
                    if (session.EndTurn(p, o.MaxRounds, out _)) { steps++; acted = true; }
                    else stuck = "undelegated-seat-cannot-end";
                    break;
                }
                if (TryPlay(ref session, p, o, plannerRng, candidates, actions, ref steps))
                {
                    acted = true;
                    break;
                }
                if (session.EndTurn(p, o.MaxRounds, out var endBoundary))
                {
                    actions.Add(new KernelTeamSearch.Action(p, null, null, EndTurn: true));
                    steps++;
                    acted = true;
                    break;
                }
                // Neither playable nor endable: a pending choice or an unsupported rule.
                // NAME it — a cutoff that cannot say why is the failure mode this project
                // has already been burned by twice (R5 / R5c).
                stuck = "stuck:" + endBoundary;
                break;
            }
            if (!acted) return Cutoff(stuck, actions, steps, session, party, o);

            // Cycle guard. A repeated position is NOT a game-rule defeat (the plan says so
            // explicitly) and the policy's own tie-break stream is part of what makes it
            // repeat, so the guard stays a cutoff. The window is small on purpose: a
            // long fight legitimately revisits shapes and only a tight loop is a loop.
            var key = session.CompactStateKey();
            recentStates.Enqueue(key);
            if (recentStates.Count > 12) recentStates.Dequeue();
            if (recentStates.Count(r => r == key) >= 4)
                return Cutoff("cycle", actions, steps, session, party, o);
        }
    }

    private static RolloutOutcome Cutoff(string reason, List<KernelTeamSearch.Action> actions, int steps,
        KernelSession session, IReadOnlyList<Player> party, RolloutOptions o) =>
        new(false, false, reason, actions, steps, session.RoundsAdvanced,
            Record(session, party, false, reason));

    /// <summary>
    /// The plan's unified end record (§02), read off the session the rollout just finished.
    ///
    /// IrreversibleLoss counts seats still dead at the END, not seats that touched zero on the
    /// way: the engine models death saves, so a seat that was revived is alive here and is not
    /// charged. A party wipe is every seat, which is what makes it lose to any surviving line
    /// without needing a 100,000,000 constant to say so.
    ///
    /// ResourcesConsumed is a potion count and is 0 for every baseline policy today, because
    /// none of the three drinks. It is read from the party's own state rather than hardcoded so
    /// that it becomes live the moment a policy (or a caller-supplied suffix) does.
    /// </summary>
    private static TerminalRecord Record(KernelSession session, IReadOnlyList<Player> party,
        bool terminal, string cutoffReason)
    {
        var hp = new int[party.Count];
        var dead = 0;
        for (var i = 0; i < party.Count; i++)
        {
            hp[i] = Math.Max(0, session.Hp(party[i].Creature));
            if (hp[i] <= 0) dead++;
        }
        return new TerminalRecord(
            Victory: terminal && session.HasWon,
            Kind: terminal ? EvidenceKind.VerifiedTerminal : EvidenceKind.EstimatedCutoff,
            CutoffReason: cutoffReason,
            PostCombatHp: hp,
            IrreversibleLoss: dead,
            ResourcesConsumed: 0);
    }

    /// <summary>
    /// Play the policy's best card for one seat. Tries candidates in descending score order
    /// rather than only the top one: a card can be playable by the cheap predicate and still
    /// be refused by the simulator (a pending choice, a targeting rule), and falling through
    /// to the next-best card is both cheaper and truer than ending the turn on it.
    /// </summary>
    private static bool TryPlay(ref KernelSession session, Player player, RolloutOptions o, Random rng,
        List<(CardModel Card, Creature? Target, string Key, int Occurrence, double Score)> candidates,
        List<KernelTeamSearch.Action> actions, ref int steps)
    {
        // One Hand() call, not three. Hand() materialises a fresh array per call, and this
        // method runs once per action of a ~220-action fight; the first version measured
        // 0.053 MB/step against 0.009 for a loop that skipped the bookkeeping, and this is
        // where the difference was. A rollout only earns its keep if it stays cheap.
        var seenCardStates = new Dictionary<string, int>(StringComparer.Ordinal);
        candidates.Clear();
        foreach (var card in session.Hand(player))
        {
            var key = Vendor.CardChoiceSupport.ChoiceCardKey(card);
            var occurrence = seenCardStates.GetValueOrDefault(key);
            seenCardStates[key] = occurrence + 1;
            if (!session.CanPlay(card)) continue;
            var targets = session.Targets(card);
            // One target per card is enough for a baseline policy. The full target space is
            // the search's job; a rollout that branched on targets would stop being cheap.
            // Nulls are skipped rather than ordered around: Targets may be non-empty and
            // entirely null for a non-targeted card, and First() on that throws.
            Creature? target = null;
            var lowest = int.MaxValue;
            for (var i = 0; i < targets.Count; i++)
            {
                if (targets[i] is not { } t) continue;
                var hp = session.Hp(t);
                if (hp >= lowest) continue;
                lowest = hp; target = t;
            }
            candidates.Add((card, target, key, occurrence, Score(session, o.Policy, card, target, rng)));
        }
        if (candidates.Count == 0) return false;

        // Try cards best-first without sorting: the first candidate almost always succeeds,
        // so an OrderByDescending per step is pure allocation for a result nobody reads.
        // Retrying a card that the simulator refuses means "clear the best, take the next".
        while (candidates.Count > 0)
        {
            var pick = 0;
            for (var i = 1; i < candidates.Count; i++)
                if (candidates[i].Score > candidates[pick].Score) pick = i;
            var (card, target, key, occurrence, _) = candidates[pick];
            // Resolve on a settled fork BEFORE attempting Play. A failed Play poisons its
            // session, so trying CardBranches afterwards can never recover a pending choice.
            // Keeping the parent also lets us skip an unsupported card without losing the line.
            var resolved = session.CardBranches(card, target, maximumBranches: 1)
                .FirstOrDefault(branch => branch.Boundary.Length == 0);
            if (resolved is not null)
            {
                session = resolved.State;
                actions.Add(new KernelTeamSearch.Action(player, card, target,
                    Choices: resolved.Choices,
                    ChoiceOrdinals: resolved.ChoiceOrdinals,
                    CardStateKey: key, CardStateOccurrence: occurrence));
                steps++;
                return true;
            }
            candidates.RemoveAt(pick);
        }
        return false;
    }

    /// <summary>
    /// Cheap and evaluation-free on purpose: the moment a rollout calls the real evaluator it
    /// inherits the evaluator's cost, and the measured advantage over the search disappears.
    /// </summary>
    private static double Score(KernelSession session, RolloutPolicyKind kind, CardModel card,
        Creature? target, Random rng)
    {
        var damage = Variable<DamageVar>(card);
        var block = card.DynamicVars.Values.OfType<BlockVar>().Sum(v => (double)v.BaseValue);
        // An attack that kills the target it is aimed at is worth more than any amount of
        // damage that does not, in every policy: leaving an enemy alive costs a whole enemy
        // phase, and no baseline policy should need to be told that twice.
        var lethal = damage > 0 && target is not null && damage >= session.Hp(target);
        var setup = damage <= 0 && block <= 0 ? 1 : 0;
        var tie = rng.NextDouble();
        return kind switch
        {
            RolloutPolicyKind.Kill => (lethal ? 1e9 : 0) + damage * 1e3 + block + tie,
            RolloutPolicyKind.Defend => (lethal ? 1e9 : 0) + block * 1e3 + damage + tie,
            RolloutPolicyKind.Growth => (lethal ? 1e9 : 0) + setup * 1e6 + damage * 1e3 + block + tie,
            _ => damage + tie,
        };
    }

    private static double Variable<T>(CardModel card) where T : DynamicVar =>
        card.DynamicVars.Values.OfType<T>().Sum(v => (double)v.BaseValue);
}

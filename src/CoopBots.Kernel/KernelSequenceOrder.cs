using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;

namespace CoopBots.Kernel;

// The baseline policies score one card. Compare AB with BA to discover an
// ordering dependency from native effects instead of a list of card names.
// Requested by the owner, 2026-10-03: investment/trigger and debuff/payoff
// ordering are families of problems. This proposes actions, never vetoes play.
internal static class KernelSequenceOrder
{
    internal const int PairLimit = 16;

    // One physical root, one Order invocation. Retain the COMPLETE native
    // branch (including actual choices) for reserve/broad comparisons and
    // the eventual commit. Action keys include owner, instance/state identity,
    // occurrence, target and choice metadata; no compact-state reuse occurs.
    // Cache hits do not replenish or bypass the caller's finite pair quota.
    internal sealed class ProbeCache(KernelSession root, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, bool handLookahead = true)
    {
        private readonly Dictionary<KernelTeamSearch.Action, KernelSession.CardBranch?> opened = new();
        private readonly Dictionary<(KernelSession, KernelTeamSearch.Action), KernelSession.CardBranch?> next = new();
        private readonly Dictionary<KernelSession, double> continuations = new();
        internal bool HasRoot(KernelSession state) => ReferenceEquals(root, state);
        internal void Seed(KernelTeamSearch.Action action, KernelSession.CardBranch branch)
            => opened[action] = branch;
        internal KernelSession.CardBranch? Open(KernelTeamSearch.Action action)
        {
            if (!opened.TryGetValue(action, out var branch))
                opened[action] = branch = root.CardBranches(action.Card!, action.Target, 1)
                    .FirstOrDefault(b => b.Boundary.Length == 0);
            return branch;
        }
        internal KernelSession? Then(KernelSession first, KernelTeamSearch.Action action)
        {
            if (!next.TryGetValue((first, action), out var branch))
            {
                // AB/BA/ABC suffixes supply only a numeric ordering estimate.
                // Before returns the FIRST action; formal commit gets Open's
                // fully compared branch, then re-evaluates on the new root.
                using var scoringOnly = KernelChoiceContinuation.Enter();
                next[(first, action)] = branch = first.CanAct(action.Player)
                    && first.Hand(action.Player).Contains(action.Card!) && first.CanPlay(action.Card!)
                        ? first.CardBranches(action.Card!, action.Target, 1)
                            .FirstOrDefault(b => b.Boundary.Length == 0) : null;
            }
            return branch?.State;
        }
        internal double Continue(KernelSession first)
        {
            if (!continuations.TryGetValue(first, out var value))
                continuations[first] = value = handLookahead
                    ? HandValue(root, first, party, actors)
                    : KernelTacticalPolicy.ActionValue(root, first, party, actors);
            return value;
        }
    }

    // Payoffs can be attacks OR non-attack engines that consume an enemy's
    // existing debuff stacks. Reserve bounded probes for their native power
    // dependencies, including another seat's enemy-targeted setup. The native
    // AB/BA comparison decides whether any effect is actually amplified; these
    // categories schedule work and never force or veto a legal card.
    internal static bool HeldSetup(KernelTeamSearch.Action proposed, KernelTeamSearch.Action other)
    {
        if (proposed.Card is not { } payoff || other.Card is not { } card) return false;
        bool Resource(MegaCrit.Sts2.Core.Models.CardModel c)
            => c.DynamicVars.Values.Any(v => v is CardsVar or EnergyVar);
        // Immediate energy is a dependency of an X payoff, and also deserves
        // its native AB/BA comparison before a discard/exhaust can consume it.
        // These categories schedule comparisons; native states decide play.
        if (other.Player == proposed.Player && CardResourceTiming.Of(card).EnergyNow > 0
            && (payoff.EnergyCost.CostsX || payoff.Keywords.Contains(CardKeyword.Exhaust))) return true;
        // Block and draw are payoffs too: Dexterity must precede block, and
        // held draw must precede an effect that prevents further draws. The
        // native pair decides the consequence, not a printed attack-only gate.
        if (payoff.Type is not (CardType.Attack or CardType.Skill)) return false;
        if (other.Player == proposed.Player && (card.Type == CardType.Power
            || card.Type == CardType.Skill && (!card.GainsBlock || Resource(card)))) return true;
        return card.TargetType is TargetType.AnyEnemy or TargetType.AllEnemies
            && (card.DynamicVars.Values.Any(NativePowerVar) || card.Rarity != CardRarity.Basic);
    }

    private static bool NativePowerVar(DynamicVar value)
    {
        for (var type = value.GetType(); type is not null; type = type.BaseType)
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PowerVar<>)) return true;
        return false;
    }

    private static bool EnemySetup(KernelTeamSearch.Action action)
        => action.Card is { } card && card.TargetType is TargetType.AnyEnemy or TargetType.AllEnemies
            && card.DynamicVars.Values.Any(NativePowerVar);

    internal static (int Ordinary, int Setup) SeatAllowance(int seats, int index)
    {
        var quota = PairLimit / Math.Max(1, seats) + (index < PairLimit % Math.Max(1, seats) ? 1 : 0);
        var ordinary = (quota + 1) / 2;
        return (ordinary, quota - ordinary);
    }

    internal static KernelTeamSearch.Action Before(KernelSession root, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, KernelTeamSearch.Action proposed,
        IEnumerable<KernelTeamSearch.Action> alternatives, ref int remaining,
        KernelSession? proposedFirst = null, ProbeCache? cache = null)
    {
        // Other living enemies still need the team's resources after a target kill.
        // The native first.HasTerminal check below retains actual whole-fight victory.
        if (proposed.Card is null) return proposed;
        var choices = alternatives.Where(a => a.Card is not null)
            .DistinctBy(a => (a.Player, a.CardStateKey, a.Target))
            // Printed enemy setup must get its bounded comparison before a
            // same-seat engine consumes those stacks. Printed power categories and
            // cross-seat options share the existing finite quota; plain nonbasic
            // damage is not promoted to a printed enemy setup. AB/BA still proves the
            // dependency; card kinds and class/seat order do not decide play.
            .OrderBy(a => a.Player == proposed.Player && proposed.Card.GainsBlock && a.Card!.Type == CardType.Power ? 0 : 1)
            .ThenBy(a => a.Card!.DynamicVars.Values.Any(NativePowerVar) ? 0 : 1)
            .ThenBy(a => EnemySetup(a) ? 0 : 1)
            .ThenBy(a => a.Player == proposed.Player ? 1 : 0)
            .ThenBy(a => HeldSetup(proposed, a) ? 0 : 1).ToArray();
        cache ??= new(root, party, actors);
        double Continue(KernelSession state) => cache.Continue(state);
        KernelSession? Open(KernelTeamSearch.Action a) => cache.Open(a)?.State;
        KernelSession? Then(KernelSession first, KernelTeamSearch.Action a) => cache.Then(first, a);

        bool PlainBody(KernelTeamSearch.Action a) => a.Card!.Rarity == CardRarity.Basic
            && !a.Card.Keywords.Any() && a.Card.DynamicVars.Values.All(v => v is DamageVar or BlockVar);
        if (remaining <= 0) return proposed;
        var first = proposedFirst ?? Open(proposed);
        if (first is null || first.HasTerminal) return proposed;
        var gain = .01;
        var best = proposed;
        foreach (var other in choices)
        {
            if (other.Card == proposed.Card) continue;
            // Two plain starter bodies commute unless the first native
            // play changed powers or lost friendly HP (e.g. thorns).
            // This avoids spending pair probes on duplicate Strike/Defend
            // permutations, while conditional/non-basic effects still enter.
            if (PlainBody(proposed) && PlainBody(other) && root.SamePowerAmounts(first)
                && party.All(p => root.Hp(p.Creature) == first.Hp(p.Creature))) continue;
            if (--remaining < 0) break;
            var otherFirst = Open(other);
            if (otherFirst is null) continue;
            var ab = Then(otherFirst, proposed);
            var ba = Then(first, other);
            // Upstream single-action scoring does not price a hand that
            // disappears. Owner request (2026-10-03): compare complete legal
            // hand continuations when discard/exhaust consumes the other
            // action. Never invent the now-illegal second play.
            if (ab is null || ba is null)
            {
                // Spending the last energy is also a mutually exclusive
                // choice. Compare legal remaining hands instead of assuming
                // that both actions can eventually be played.
                // A failed second branch may coexist with an actually legal pair
                // in the opposite direction. Keep that native witness instead
                // of comparing only raw prefixes. Retain each prefix's optional
                // continuation as well: a legal second card is never forced.
                // These states share this invocation's finite probe/hand cache.
                var otherValue = Math.Max(Continue(otherFirst),
                    ab is null ? double.NegativeInfinity : Continue(ab));
                var proposedValue = Math.Max(Continue(first),
                    ba is null ? double.NegativeInfinity : Continue(ba));
                var continuationGain = otherValue - proposedValue;
                // Consuming/discarding the hand makes these alternatives
                // exclusive. Keep their best complete legal continuation from
                // this one scan; the first improvement can be weaker fodder.
                if (continuationGain > gain) { gain = continuationGain; best = other; }
                continue;
            }
            var delta = KernelTacticalPolicy.ActionValue(root, ab, party, actors)
                - KernelTacticalPolicy.ActionValue(root, ba, party, actors);
            var differentPowers = !ab.SamePowerAmounts(ba);
            if (differentPowers) delta += ab.FutureDebuffValue(actors, reference: root) - ba.FutureDebuffValue(actors, reference: root);
            // Extra duration can be cashed out by a THIRD action even when
            // both two-card states exceed the ordinary duration horizon.
            // Probe remaining held payoffs only when native powers differ.
            if (Math.Abs(delta) < .01 && differentPowers)
            {
                var abValue = KernelTacticalPolicy.ActionValue(root, ab, party, actors);
                var baValue = KernelTacticalPolicy.ActionValue(root, ba, party, actors);
                foreach (var next in choices.Where(a => a.Card != other.Card && a.Card != proposed.Card).Take(4))
                {
                    var abc = Then(ab, next); var bac = Then(ba, next);
                    if (abc is null || bac is null) continue;
                    abValue = Math.Max(abValue, KernelTacticalPolicy.ActionValue(root, abc, party, actors));
                    baValue = Math.Max(baValue, KernelTacticalPolicy.ActionValue(root, bac, party, actors));
                }
                delta = abValue - baValue;
            }
            if (delta <= gain) continue;
            // Commit one proven predecessor, then re-evaluate after its
            // native effects really happen. Re-scanning a transitive chain
            // at this unchanged root consumed the finite late-payoff budget.
            return other;
        }
        return best;
    }

    private static double HandValue(KernelSession root, KernelSession first,
        IReadOnlyList<Player> party, IReadOnlyList<Player> actors)
    {
        // These two suffixes return only a numeric estimate, never actions or
        // native choice tokens. Use the existing nested-choice bound here so
        // each future mandatory menu does not start another three-policy hand
        // search. Open/Then and the committed branch still compare native choices.
        using var scoringOnly = KernelChoiceContinuation.Enter();
        var value = double.NegativeInfinity;
        foreach (var policy in new[] { RolloutPolicyKind.Kill, RolloutPolicyKind.Defend })
        {
            var run = new RolloutRun(first.Fork(), party, actors,
                new(policy, MaxSteps: 24, MaxRounds: root.RoundsAdvanced + 1)
                { InvestmentLookahead = false, SequenceLookahead = false });
            while (!run.Done) run.Step();
            if (!run.State.CanFork) continue;
            // The one-turn continuation has already consumed one debuff tick.
            // Keep the surviving duration/resource value when the last energy
            // makes alternatives exclusive (e.g. Taunt versus plain Defend).
            value = Math.Max(value, KernelTacticalPolicy.ActionValue(root, run.State, party, actors)
                + run.State.FutureDebuffValue(actors, elapsedTurns: 0, reference: root));
        }
        return value;
    }
}

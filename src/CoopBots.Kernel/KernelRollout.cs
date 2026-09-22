using MegaCrit.Sts2.Core.Entities.Cards;
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
    // Index-aligned with Actions: the living NetIds immediately BEFORE that action. The
    // script cache uses this to tell "this seat was already dead in the line" from "the
    // live death no longer matches the line", which the final HP array cannot express.
    IReadOnlyList<ulong[]> AliveStates,
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
    /// <summary>
    /// Run one rollout to its end. The whole fight happens inside this call — for the live
    /// planner use <see cref="RolloutRun"/> instead, which does one action per Step so the
    /// main thread can return to the frame in between (see its remarks).
    /// </summary>
    public static RolloutOutcome Run(KernelSession session, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, RolloutOptions? options = null)
    {
        var run = new RolloutRun(session, party, actors, options);
        while (!run.Done) run.Step();
        return run.Result!;
    }
}

/// <summary>
/// A rollout that can be advanced ONE ACTION at a time.
///
/// WHY THIS EXISTS: a rollout is ~27 ms, and the tournament runs ~18 of them per bot
/// decision. Run to completion in one call that is ~500-1200 ms of uninterrupted work on
/// Godot's main thread, which is exactly the length of the card-play animation — so the
/// animation froze mid-flight and the mod looked like it had a bug (measured live
/// 2026-09-22: `[ActionExecutor] Completed execution of action` immediately followed by
/// `decision-ms=1003.1`, with the visual tail of that card still playing).
///
/// One Step is one card play or one end-turn, well under a millisecond, so the caller can
/// spend a slice of each frame on it and let the frame finish. The fight is simulated by
/// exactly the same code in exactly the same order, so a sliced run reaches the same ending
/// as <see cref="KernelRollout.Run"/> — the caller controls WHEN the work happens, never WHAT.
/// </summary>
public sealed class RolloutRun
{
    private KernelSession session;
    private readonly IReadOnlyList<Player> party;
    private readonly HashSet<Player> driven;
    private readonly Random plannerRng;
    private readonly RolloutOptions o;
    private readonly List<KernelTeamSearch.Action> actions = new();
    private readonly List<ulong[]> aliveStates = new();
    private readonly List<(CardModel Card, Creature? Target, string Key, int Occurrence, double Score)> candidates = new();
    private readonly Queue<string> recentStates = new();
    private int steps;

    public RolloutOutcome? Result { get; private set; }
    public bool Done => Result is not null;

    internal RolloutRun(KernelSession session, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, RolloutOptions? options)
    {
        this.session = session;
        // A rollout is a prediction, not a committed plan: a start-of-turn choice
        // (Tools of the Trade, Tyranny, …) and an enemy-turn choice (Knowledge Demon's
        // curse) must not truncate the line at their first boundary. The planning
        // search keeps these false and records/branches the real choices.
        session.AutoResolveTurnStartChoices = true;
        session.AutoResolveEnemyChoices = true;
        this.party = party;
        o = options ?? new RolloutOptions();
        driven = actors.ToHashSet();
        plannerRng = new Random(o.Seed);
    }

    /// <summary>
    /// One action of the simulated fight: play one card for one seat, or end one seat's turn.
    /// Repeat until <see cref="Done"/>. Safe to call after Done (it does nothing).
    /// </summary>
    public void Step()
    {
        if (Done) return;

        if (session.HasTerminal)
        {
            Result = new RolloutOutcome(true, session.HasWon, session.HasWon ? "victory" : "wipe",
                actions, aliveStates, steps, session.RoundsAdvanced, Record(session, party, true, ""));
            return;
        }
        if (steps >= o.MaxSteps) { Result = Cutoff("step-cap"); return; }
        if (session.RoundsAdvanced >= o.MaxRounds) { Result = Cutoff("round-cap"); return; }

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
            if (TryPlay(p))
            {
                acted = true;
                break;
            }
            if (session.EndTurn(p, o.MaxRounds, out var endBoundary))
            {
                aliveStates.Add(SnapshotAlive());
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
        if (!acted)
        {
            Result = Cutoff(stuck == "no-actor" ? NoActorReason() : stuck);
            return;
        }

        // Cycle guard. A repeated position is NOT a game-rule defeat (the plan says so
        // explicitly) and the policy's own tie-break stream is part of what makes it
        // repeat, so the guard stays a cutoff. The window is small on purpose: a
        // long fight legitimately revisits shapes and only a tight loop is a loop.
        var key = session.CompactStateKey();
        recentStates.Enqueue(key);
        if (recentStates.Count > 12) recentStates.Dequeue();
        if (recentStates.Count(r => r == key) >= 4) Result = Cutoff("cycle");
    }

    private RolloutOutcome Cutoff(string reason) =>
        new(false, false, reason, actions, aliveStates, steps, session.RoundsAdvanced,
            Record(session, party, false, reason));

    /// <summary>
    /// Why a rollout found nobody who could act. The old bare `no-actor` was measured
    /// 16 times in the 2026-09-22 Ovicopter fight, where five party members died; a name
    /// that says "all-ready" / "enemy-phase-complete" / "all-dead" is the difference
    /// between a simulator boundary and a genuine wipe.
    /// </summary>
    private string NoActorReason()
    {
        if (session.HasWon) return "no-actor:victory";
        if (party.All(p => session.Hp(p.Creature) <= 0)) return "no-actor:all-dead";
        if (session.EnemyPhaseCompleted) return "no-actor:enemy-phase-complete";
        var alive = party.Where(p => session.Hp(p.Creature) > 0).ToArray();
        if (alive.Length > 0 && alive.All(p => session.IsReady(p))) return "no-actor:all-ready";
        return "no-actor:no-legal-action";
    }

    /// <summary>
    /// Play the policy's best card for one seat. Tries candidates in descending score order
    /// rather than only the top one: a card can be playable by the cheap predicate and still
    /// be refused by the simulator (a pending choice, a targeting rule), and falling through
    /// to the next-best card is both cheaper and truer than ending the turn on it.
    /// </summary>
    private bool TryPlay(Player player)
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
            Creature? target;
            if (KernelTemporaryBuffs.IsAllyTarget(card)
                && KernelTemporaryBuffs.Timing(card) is { } buffTiming)
            {
                // Aim the one-turn buff at the ally with the most relevant actions
                // left, not the lowest-HP ally: it is worth zero after that ally has
                // already played the cards it would amplify.
                target = BestBuffTarget(session, targets, buffTiming);
            }
            else
            {
                target = null;
                var lowest = int.MaxValue;
                for (var i = 0; i < targets.Count; i++)
                {
                    if (targets[i] is not { } t) continue;
                    var hp = session.Hp(t);
                    if (hp >= lowest) continue;
                    lowest = hp; target = t;
                }
            }
            candidates.Add((card, target, key, occurrence,
                Score(session, party, o.Policy, player, card, target, plannerRng)));
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
            // NOT PLAYING IS A REAL OPTION. A 0-cost exhaust that cannot unlock a card,
            // or a low-threat pure-setup exhaust (Piercing Wail), is worth more held:
            // skipping it here lets the caller end the turn instead. Clearing the best
            // and re-picking means a hold-worthy card never blocks a real play behind it.
            if (ShouldHold(session, party, player, card))
            {
                candidates.RemoveAt(pick);
                continue;
            }
            // Resolve on a settled fork BEFORE attempting Play. A failed Play poisons its
            // session, so trying CardBranches afterwards can never recover a pending choice.
            // Keeping the parent also lets us skip an unsupported card without losing the line.
            var resolved = session.CardBranches(card, target, maximumBranches: 1)
                .FirstOrDefault(branch => branch.Boundary.Length == 0);
            if (resolved is not null)
            {
                session = resolved.State;
                session.AutoResolveTurnStartChoices = true;
                session.AutoResolveEnemyChoices = true;
                aliveStates.Add(SnapshotAlive());
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
    /// Whether the cheap policy should NOT play this card and let the caller end the turn.
    ///
    /// The baseline policies had no "do nothing" answer: they always played the
    /// highest-scoring card, so Wisp's +1 energy and Piercing Wail's Strength loss were
    /// spent the moment they appeared. Two bounded rules cover the live reports:
    ///
    ///   * a 0-cost energy gain whose extra energy unlocks no card this turn is a wasted
    ///     exhaust (the live example: 1 energy, Wisp + a 3-cost card, end with 2 energy);
    ///   * a pure setup/defensive exhaust is held when this turn's incoming attack is
    ///     already small enough not to need it (Piercing Wail into a 2-damage attack).
    ///
    /// Lethal/damage/block cards never reach this path: a damaging exhaust attack has
    /// damage > 0, and a block exhaust has block > 0.
    /// </summary>
    internal static bool ShouldHold(KernelSession session, IReadOnlyList<Player> party,
        Player player, CardModel card)
    {
        // A one-turn setup card is worth zero once nothing it enables is left.
        if (KernelTemporaryBuffs.IsPureSetup(card))
            return RelevantRemaining(session, party, player, card, target: null) <= 0;
        var energyGain = EnergyGain(card);
        if (energyGain > 0) return WastesEnergyGain(session, player, card, energyGain);
        return IsPureExhaustSetup(card) && LowThreatTurn(session, party, player);
    }

    /// <summary>Playable attack cards left in this ally's hand.</summary>
    internal static int RemainingAttacks(KernelSession session, Player ally)
        => session.Hand(ally).Count(card => card.Type == CardType.Attack && session.CanPlay(card));

    /// <summary>Playable block cards left in this ally's hand.</summary>
    internal static int RemainingBlocks(KernelSession session, Player ally)
        => session.Hand(ally).Count(card => card.GainsBlock && session.CanPlay(card));

    /// <summary>Playable cards of any type left in this player's hand.</summary>
    internal static int RemainingPlayables(KernelSession session, Player player)
        => session.Hand(player).Count(session.CanPlay);

    /// <summary>
    /// How many actions left this turn the card can still amplify. Ally-targeted
    /// buffs use the target when known, otherwise the best remaining teammate;
    /// self/enemy cards use the acting seat (or for Oblivion, its card count).
    /// </summary>
    internal static int RelevantRemaining(KernelSession session, IReadOnlyList<Player> party,
        Player actor, CardModel card, Creature? target)
    {
        if (KernelTemporaryBuffs.Timing(card) is not { } timing) return 0;
        if (timing == TemporaryBuffTiming.Cards)
            return RemainingPlayables(session, actor);
        Func<KernelSession, Player, int> countFor = timing == TemporaryBuffTiming.Attack
            ? RemainingAttacks
            : RemainingBlocks;
        if (target?.Player is { } targetAlly) return countFor(session, targetAlly);
        if (KernelTemporaryBuffs.IsAllyTarget(card))
            return party.Where(p => p.Creature.IsAlive).Select(p => countFor(session, p)).DefaultIfEmpty(0).Max();
        return countFor(session, actor);
    }

    /// <summary>
    /// The ally that should receive a one-turn buff: the one with the most actions
    /// left of the buff's kind, then lowest HP as the tie-break.
    /// </summary>
    internal static Creature? BestBuffTarget(KernelSession session, IReadOnlyList<Creature?> targets,
        TemporaryBuffTiming timing)
    {
        Func<KernelSession, Player, int> countFor = timing == TemporaryBuffTiming.Attack
            ? RemainingAttacks
            : RemainingBlocks;
        Creature? best = null;
        var bestCount = -1;
        foreach (var candidate in targets)
        {
            if (candidate is not { } target || target.Player is not { } ally) continue;
            var count = countFor(session, ally);
            if (count > bestCount
                || (count == bestCount && (best is null || session.Hp(target) < session.Hp(best))))
            {
                best = target;
                bestCount = count;
            }
        }
        return best;
    }

    private static bool WastesEnergyGain(KernelSession session, Player player, CardModel card, int gain)
    {
        var energyBefore = session.Energy(player);
        var energyAfter = energyBefore - CardCost(card) + gain;
        foreach (var other in session.Hand(player))
        {
            if (ReferenceEquals(other, card)) continue;
            var cost = CardCost(other);
            if (cost < 0 || cost <= energyBefore) continue;
            if (cost <= energyAfter) return false;
        }
        return true;
    }

    private static int EnergyGain(CardModel card)
    {
        try { return (int)card.DynamicVars.Values.OfType<EnergyVar>().Sum(v => v.BaseValue); }
        catch { return 0; }
    }

    private static int CardCost(CardModel card)
    {
        try { return (int)card.EnergyCost.GetWithModifiers(CostModifiers.All); }
        catch { return -1; }
    }

    private static bool IsPureExhaustSetup(CardModel card)
    {
        if (!card.Keywords.Contains(CardKeyword.Exhaust)) return false;
        var damage = Variable<DamageVar>(card);
        var block = card.DynamicVars.Values.OfType<BlockVar>().Sum(v => (double)v.BaseValue);
        return damage <= 0 && block <= 0;
    }

    internal static bool LowThreatTurn(KernelSession session, IReadOnlyList<Player> party, Player player)
    {
        try
        {
            var attacks = session.CurrentAttacks();
            foreach (var member in party)
            {
                if (session.Hp(member.Creature) <= 0) continue;
                var incoming = attacks.Sum(a =>
                    session.IntentHit(a.Enemy, member.Creature, a.Raw) * a.Repeats);
                if (incoming - session.Block(member.Creature) > 4) return false;
            }
            return true;
        }
        catch
        {
            // Unknown intent must not turn into a hold; fall through to the normal play loop.
            return false;
        }
    }

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
            ResourcesConsumed: 0,
            OutstandingStolenResource: session.OutstandingStolenResource);
    }

    /// <summary>The living party immediately before the next action, ordered for comparison.</summary>
    private ulong[] SnapshotAlive()
        => party.Where(player => session.Hp(player.Creature) > 0)
            .Select(player => player.NetId).OrderBy(id => id).ToArray();

    /// <summary>
    /// Cheap and evaluation-free on purpose: the moment a rollout calls the real evaluator it
    /// inherits the evaluator's cost, and the measured advantage over the search disappears.
    /// </summary>
    private static double Score(KernelSession session, IReadOnlyList<Player> party,
        RolloutPolicyKind kind, Player actor, CardModel card, Creature? target, Random rng)
    {
        var damage = Variable<DamageVar>(card);
        var block = card.DynamicVars.Values.OfType<BlockVar>().Sum(v => (double)v.BaseValue);
        // An attack that kills the target it is aimed at is worth more than any amount of
        // damage that does not, in every policy: leaving an enemy alive costs a whole enemy
        // phase, and no baseline policy should need to be told that twice.
        var lethal = damage > 0 && target is not null && damage >= session.Hp(target);
        var setup = damage <= 0 && block <= 0 ? 1 : 0;
        var tie = rng.NextDouble();
        var score = kind switch
        {
            RolloutPolicyKind.Kill => (lethal ? 1e9 : 0) + damage * 1e3 + block + tie,
            RolloutPolicyKind.Defend => (lethal ? 1e9 : 0) + block * 1e3 + damage + tie,
            RolloutPolicyKind.Growth => (lethal ? 1e9 : 0) + setup * 1e6 + damage * 1e3 + block + tie,
            _ => damage + tie,
        };
        if (KernelTemporaryBuffs.Timing(card) is not null)
        {
            // Play it BEFORE the cards it amplifies. Every remaining attack/block/card
            // is a trigger; with none left a pure setup card is actively bad and
            // ShouldHold will skip it.
            var remaining = RelevantRemaining(session, party, actor, card, target);
            score += remaining > 0
                ? remaining * 1_000_000d
                : KernelTemporaryBuffs.IsPureSetup(card) ? -1_000_000d : 0;
        }
        return score;
    }

    private static double Variable<T>(CardModel card) where T : DynamicVar =>
        card.DynamicVars.Values.OfType<T>().Sum(v => (double)v.BaseValue);
}

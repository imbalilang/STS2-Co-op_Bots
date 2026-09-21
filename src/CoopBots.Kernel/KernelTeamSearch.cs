using System.Diagnostics;
using CoopBots.Kernel.Vendor.PowerSync;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel;

/// <summary>
/// Team policy supplies a score; the imported engine owns all card effects.
/// Advance must run on the capturing game thread while the live root is stable.
/// A slice ends between actions: one native effect cannot be preempted.
/// </summary>
public sealed class KernelTeamSearch : IDisposable
{
    /// <summary>
    /// Which upstream engine this build actually linked, so a live log can be
    /// checked instead of trusted.
    ///
    /// The numbers are read from the vendored <c>SolverWeights</c> rather than
    /// written here, and both members only exist in 0.41.0 — 0.33.9 split the
    /// profile pair differently and had no escalation caps. A stale engine
    /// therefore fails the build, and a stale DLL cannot print the field at all,
    /// which is what makes the marker falsifiable instead of a claim.
    /// </summary>
    public static string EngineMarker { get; } =
        $"0.41.0(nve={Vendor.SolverWeights.MaximumNoVictoryEscalations},"
        + $"dsp={Vendor.SolverWeights.DeathSavePremiumPercent})";

    // Exactly one of Card/Potion/EndTurn is set. Potions are free actions, so the search
    // can discover "use a buff potion, then play the burst" as one plan.
    //
    // ChoiceOrdinals is the bounded choice vector this branch resolved through, kept so
    // the action can be replayed exactly once the search is over (see KernelSession
    // .ReplayPlanned). Choices alone is not enough: it records WHAT was picked, not the
    // positional vector the simulator's choice gate needs to pick it again.
    // CardStateKey/CardStateOccurrence identify the planned card the way upstream's
    // PlanAction does (CardId + CardOccurrence / CardStateKey + CardStateOccurrence), and
    // for the same reason: a plan must never depend on holding a CardModel INSTANCE.
    // Generated cards (Shiv, Mirage…) are fresh simulated objects whose Original is a new
    // CardModel, and References() is plain reference equality — so an instance-held plan
    // matched nothing on the live board and nothing on replay.
    public sealed record Action(Player Player, CardModel? Card, Creature? Target, PotionModel? Potion = null,
        IReadOnlyList<KernelChoice>? Choices = null, bool EndTurn = false,
        IReadOnlyList<int>? ChoiceOrdinals = null,
        string CardStateKey = "", int CardStateOccurrence = 0);
    // StopOnFirstTerminal: end the search the moment ANY line that ends the fight (a
    // victory OR a party wipe) is in hand, instead of spending the rest of the budget
    // ranking terminal lines against each other. The five-minute OPENING search leaves
    // this false — its job is to pick the best of several endings. The one-minute
    // EXTENSION attempts set it true: they exist to find one ending at all, and once one
    // is found there is nothing left for the remaining seconds to buy.
    public sealed record Options(int Depth = 9, int Width = 12, int MaxNodes = 512, bool IncludeEndTurns = false,
        int MaxRounds = 2, bool StopOnFirstTerminal = false)
    {
        // Focused-regression old/new switch. Production keeps the backported
        // power-route protection on; tests flip it off on the same fixture.
        internal bool EnablePowerRoutes { get; init; } = true;
    }
    // ActionStates is the L1 sentinel: the branch's visible-state text AFTER each action
    // on the returned path, index-aligned with Actions. Deployment compares the live
    // board against entry i once action i has been submitted, so a step that silently
    // resolved differently than predicted is caught before the NEXT step is played
    // instead of at the next turn boundary. Empty means the path could not be replayed
    // and the plan must be treated as unverified — "no reuse" beats "wrong reuse".
    public sealed record Result(IReadOnlyList<Action> Actions, double Score, int ExpandedNodes,
        IReadOnlyDictionary<string, int> Boundaries, string StopReason, bool HasUncertainRisk,
        IReadOnlyList<(string Before, string After)> ActionStates, string ReplayFailure,
        string RiskDetail);
    // TurnBoundaries records the plan's own prediction for every turn it crosses: for
    // each EndTurn on the path, the action index the next turn starts at and the
    // branch's visible-state text at that moment. Deployment compares the live board
    // against each one before it will carry the plan past that boundary — the actions
    // after it were computed against a SIMULATED enemy turn, and the real one is played
    // by the game. Same check CombatSolver calls `validation=exact_state_text`.
    //
    // Every boundary, not just the first: validating one and then trusting the rest
    // would carry the whole tail of a long plan on a single check.
    private sealed record Node(KernelSession State, Action[] Path, double Score, bool Risky, KernelPowerLedger Power,
        IReadOnlyList<(int Index, string Text)> Boundaries, string RiskDetail);
    private readonly int thread = Environment.CurrentManagedThreadId;
    // Set by Expand when the horizon closes; see the note there.
    private string horizonStop = "depth-cap";
    private readonly Player[] actors;
    private readonly Func<KernelSession, double> evaluate;
    private readonly Options options;
    // The captured root, kept untouched so a finished path can be replayed from it to
    // recover per-action predicted states. Deliberately NOT a parent chain on Node:
    // that would keep every ancestor's simulator alive for as long as the frontier,
    // which is the memory the release path exists to reclaim.
    private readonly KernelSession replayRoot;
    private readonly Dictionary<string, int> boundaries = new(StringComparer.Ordinal);
    private readonly IEnumerator<bool> steps;
    private Node best;
    private Node? bestResolved;
    // The best line that REACHED THE HORIZON while the fight continued. Kept apart from
    // bestTerminal (victory/wipe) because the two are different successes, and apart from
    // bestResolved because that only says an enemy phase resolved.
    private Node? bestHorizon;
    // A node whose branch actually ENDED the fight — victory or a wipe. Kept apart from
    // bestResolved because that one only says an enemy phase resolved, which is a round
    // boundary rather than a route. Preferring "crossed a round" over "finished" is how
    // a search returns a plan that stops where the depth ran out.
    private Node? bestTerminal;
    private int expanded;
    private int maxRoundsReached;
    private bool disposed;
    public bool IsComplete { get; private set; }
    public Result? CompletedResult { get; private set; }
    public KernelSession? CompletedState { get; private set; }

    /// <summary>
    /// The plan's own predictions for every turn boundary it crosses, in order: the
    /// action index each turn starts at and the branch's visible-state text there. Empty
    /// when the plan never crosses a turn, so there is nothing to validate before
    /// deployment continues into it.
    /// </summary>
    public IReadOnlyList<(int Index, string Text)> TurnBoundaries { get; private set; } = [];

    /// <summary>
    /// L1 sentinel: the branch's visible-state text after each action on the returned
    /// path, index-aligned with <c>CompletedResult.Actions</c>. Empty means the path
    /// could not be replayed and the plan is unverified. See <see cref="Result"/>.
    /// </summary>
    public IReadOnlyList<(string Before, string After)> ActionStates { get; private set; } = [];

    /// <summary>
    /// Why <see cref="ActionStates"/> is empty, when it is: which step of the path
    /// stopped replaying and the boundary it returned. Empty when the sentinel was
    /// built or when the search was cancelled.
    /// </summary>
    public string ReplayFailure { get; private set; } = "";

    /// <summary>
    /// How deep into the fight this search has actually reached, in completed player/enemy
    /// rounds: the maximum <see cref="KernelSession.RoundsAdvanced"/> over every admitted
    /// node. Used by the in-combat panel to show "how far it has got" while the opening
    /// ladder is still running — a node count alone cannot tell a player whether the tree
    /// is exploring the first round or the fourth.
    /// </summary>
    public int MaxRoundsReached => maxRoundsReached;
    /// <summary>
    /// Nodes expanded so far. Read-only, for diagnostics: an overrunning slice has to be able
    /// to say how far it got before it stopped returning, otherwise "the search went quiet for
    /// five minutes" has no number attached to it.
    /// </summary>
    public int ExpandedNodesSoFar => expanded;

    /// <summary>
    /// Whether this search found at least one branch that ENDED the fight. False means
    /// every returned action came from a plan that ran out of depth or nodes instead of
    /// reaching an end — the caller should treat it as "no route found yet", not as a
    /// route. Cleared and re-derived by every <c>Complete</c>.
    /// </summary>
    public bool HasRoute { get; private set; }

    public KernelTeamSearch(KernelSession root, IEnumerable<Player> actors,
        Func<KernelSession, double> evaluate, Options? options = null)
    {
        this.options = options ?? new();
        if (this.options.Depth < 1 || this.options.Width < 1 || this.options.MaxNodes < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.actors = actors.Distinct().ToArray();
        this.evaluate = evaluate;
        // Own the root: callers cannot mutate a running search through their session.
        replayRoot = root.Fork();
        best = new(replayRoot, [], Score(replayRoot), false, KernelPowerLedger.Empty, [], "");
        steps = Expand(best).GetEnumerator();
    }

    public bool Advance(TimeSpan slice, Func<bool> rootStillCurrent, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != thread)
            throw new InvalidOperationException("Kernel search must stay on its capturing thread.");
        if (IsComplete) return true;
        if (slice <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(slice));
        if (cancellation.IsCancellationRequested || !rootStillCurrent())
        { Cancel(cancellation.IsCancellationRequested ? "cancelled" : "stale-root"); return true; }
        var watch = Stopwatch.StartNew();
        do
        {
            if (cancellation.IsCancellationRequested) { Cancel("cancelled"); return true; }
            if (!steps.MoveNext()) { Complete(expanded >= options.MaxNodes ? "node-budget" : horizonStop); return true; }
            // An extension attempt stops the instant it holds a line that ends the fight.
            // Without this the attempt always ran to `time-budget`, so every extension
            // reported `previousStop=time-budget` and a ladder that HAD found an ending
            // early still burned its whole minute before using it.
            if (options.StopOnFirstTerminal && (bestTerminal is not null || bestHorizon is not null))
            {
                Complete("terminal-found");
                return true;
            }
        } while (watch.Elapsed < slice);
        return false;
    }

    /// <summary>
    /// The FIRST risky step on a path is the one that matters: it is what made every later
    /// action an estimate, and the one whose card or hook has to be modelled before the plan
    /// is allowed to cross a turn. Later risky steps are consequences, not causes.
    /// </summary>
    private static string RiskDetail(KernelSession child, Node parent)
        => parent.RiskDetail.Length > 0 ? parent.RiskDetail : child.LastActionRiskDetail;

    private IEnumerable<bool> Expand(Node root)
    {
        var frontier = new List<Node> { root };
        var protect = options.EnablePowerRoutes;
        // Panel progress: the deepest round any admitted branch has reached. Tracked as
        // nodes are admitted rather than derived after the fact, so a still-running search
        // can report it live. RoundsAdvanced counts completed player/enemy rounds.
        void TrackRounds(KernelSession state)
            => maxRoundsReached = Math.Max(maxRoundsReached, state.RoundsAdvanced);
        TrackRounds(root.State);
        for (var depth = 0; depth < options.Depth && frontier.Count > 0; depth++)
        {
            var next = new List<Node>();
            foreach (var parent in frontier)
            foreach (var actor in actors)
            {
                if (!parent.State.CanAct(actor) || parent.State.Enemies.Count == 0) continue;
                // Enumerate each branch hand, including cards drawn/generated by earlier bots.
                // Identity for every planned card, resolved once per hand position. Counts
                // every EARLIER card with the same state key, playable or not — upstream's
                // CardOccurrence counts the same way, so the two agree on which physical
                // card an occurrence names.
                var seenCardStates = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var card in parent.State.Hand(actor))
                {
                    var cardStateKey = Vendor.CardChoiceSupport.ChoiceCardKey(card);
                    var cardStateOccurrence = seenCardStates.GetValueOrDefault(cardStateKey);
                    seenCardStates[cardStateKey] = cardStateOccurrence + 1;
                    if (!parent.State.CanPlay(card)) continue;
                    foreach (var target in parent.State.Targets(card))
                    foreach (var branch in parent.State.CardBranches(card, target, Math.Min(8, options.MaxNodes - expanded)))
                    {
                        if (expanded >= options.MaxNodes) yield break;
                        expanded++;
                        var child = branch.State;
                        TrackRounds(child);
                        var boundary = branch.Boundary;
                        if (boundary.Length == 0 && branch.Choices.Any(c => !actors.Any(p => p.NetId == c.Owner)))
                            boundary = "external-player-choice";
                        if (boundary.Length > 0)
                        {
                            var key = card.Id.Entry + ":" + boundary;
                            boundaries[key] = boundaries.GetValueOrDefault(key) + 1;
                            yield return true;
                            continue;
                        }
                        var action = new Action(actor, card, target, Choices: branch.Choices,
                            ChoiceOrdinals: branch.ChoiceOrdinals,
                            CardStateKey: cardStateKey, CardStateOccurrence: cardStateOccurrence);
                        var power = protect
                            ? KernelPowerRouter.Advance(parent.Power, parent.State, child, action, actors)
                            : KernelPowerLedger.Empty;
                        var node = new Node(child, [.. parent.Path, action], Score(child),
                            parent.Risky || child.LastActionHadEnvironmentalRisk, power,
                            parent.Boundaries, RiskDetail(child, parent));
                        if (node.Score > best.Score) best = node;
                        if (child.HasWon || child.EnemyPhaseCompleted)
                        {
                            if (bestResolved is null || node.Score > bestResolved.Score) bestResolved = node;
                            // TERMINALS ARE RANKED BY THE OBJECTIVE, NOT BY THE HEURISTIC.
                            // The plan keeps two pools apart (§06): a verified ending goes in
                            // the plan pool and is compared with the terminal objective; an
                            // unfinished line only ever informs where to search next, and the
                            // tactical Score is the better estimate for that. Ranking
                            // terminals by Score instead is what let a line that ends the
                            // fight lose to a line that merely crossed a round.
                            if (child.HasTerminal && (bestTerminal is null
                                || TerminalComparer.Compare(TerminalOf(node), TerminalOf(bestTerminal)) > 0))
                                bestTerminal = node;
                        }
                        next.Add(node);
                        // Bound retained memory throughout expansion, not just at end of depth.
                        if (next.Count > options.Width * 4)
                            next = TrimInterim(next, options.Width);
                        yield return true;
                    }
                }
                // Free potion actions. A potion that cannot be simulated in this
                // branch is simply skipped (potions are optional, unlike cards).
                foreach (var potion in parent.State.UsablePotions(actor))
                foreach (var target in parent.State.PotionTargets(potion))
                {
                    if (expanded >= options.MaxNodes) yield break;
                    expanded++;
                    var child = parent.State.Fork();
                    if (!child.UsePotion(potion, target, out _))
                    {
                        yield return true;
                        continue;
                    }
                    TrackRounds(child);
                    var potionAction = new Action(actor, null, target, potion);
                    var potionPower = protect
                        ? KernelPowerRouter.Advance(parent.Power, parent.State, child, potionAction, actors)
                        : KernelPowerLedger.Empty;
                    var node = new Node(child, [.. parent.Path, potionAction], Score(child),
                        parent.Risky || child.LastActionHadEnvironmentalRisk, potionPower,
                        parent.Boundaries, RiskDetail(child, parent));
                    if (node.Score > best.Score) best = node;
                    if (child.HasWon && (bestResolved is null || node.Score > bestResolved.Score)) bestResolved = node;
                    next.Add(node);
                    if (next.Count > options.Width * 4)
                        next = TrimInterim(next, options.Width);
                    yield return true;
                }
                if (options.IncludeEndTurns)
                {
                    if (expanded >= options.MaxNodes) yield break;
                    expanded++;
                    var child = parent.State.Fork();
                    if (child.EndTurn(actor, options.MaxRounds, out var boundary))
                    {
                        TrackRounds(child);
                        var endAction = new Action(actor, null, null, EndTurn: true);
                        var endPower = protect
                            ? KernelPowerRouter.Advance(parent.Power, parent.State, child, endAction, actors)
                            : KernelPowerLedger.Empty;
                        // The boundary is the action AFTER the end turn, and `child` is
                        // the branch as the next turn begins — which is exactly what
                        // deployment will have to match before it may play that action.
                        // Appended rather than replacing: a plan that crosses several
                        // turns carries one entry per crossing, and deployment checks
                        // them in order.
                        var node = new Node(child, [.. parent.Path, endAction], Score(child),
                            parent.Risky || child.LastActionHadEnvironmentalRisk, endPower,
                            [.. parent.Boundaries, (parent.Path.Length + 1, BoundaryText(child))],
                            RiskDetail(child, parent));
                        if (node.Score > best.Score) best = node;
                        if (child.HorizonClosed
                            && (bestHorizon is null || node.Score > bestHorizon.Score)) bestHorizon = node;
                        if (child.EnemyPhaseCompleted)
                        {
                            if (bestResolved is null || node.Score > bestResolved.Score) bestResolved = node;
                        }
                        else
                        {
                            next.Add(node);
                        }
                    }
                    else boundaries["end-turn:" + boundary] = boundaries.GetValueOrDefault("end-turn:" + boundary) + 1;
                    yield return true;
                }
            }
            // Dedup equivalent states, then keep a diverse frontier: one attack,
            // skill/block, potion and end-turn line each before filling by score.
            // A pure top-score cut can otherwise exhaust the budget on variations
            // of one idea and never compare a defense or a settling line.
            frontier = SelectFrontier(next, options.Width);
        }
        // WHY the horizon ended, which the caller needs and the stop reason used to
        // conflate: the loop exits either at the depth cap (more depth would explore
        // further) or with an empty frontier (no board position was reachable from the
        // kept nodes — more depth would explore exactly nothing). Both used to report
        // "depth-or-exhausted", which made a livelock look like a budget problem.
        horizonStop = frontier.Count == 0 ? "frontier-empty" : "depth-cap";
    }
    // Intermediate trims must apply the same route protection as the final
    // frontier; a pure top-score cut in the middle of a depth kills a delayed
    // power line before it can pay off. When nothing carries a commitment the
    // original score-only trim is kept so no-power behaviour is unchanged.
    private static List<Node> TrimInterim(List<Node> candidates, int width)
        => candidates.Any(n => !n.Power.IsEmpty)
            ? SelectFrontier(candidates, width)
            : candidates.OrderByDescending(n => n.Score).Take(width).ToList();

    // Drop equivalent states, then keep one line per action category before
    // filling by score. Dedup includes commitment owner+family history so route
    // lineage is not silently merged; with commitments present the bounded
    // upstream seat quota reserves representatives inside the same width.
    private static List<Node> SelectFrontier(List<Node> candidates, int width)
    {
        var best = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var node in candidates.OrderByDescending(n => n.Score))
        {
            var key = node.State.CompactStateKey() + "|" + node.Power.Signature();
            if (!best.TryGetValue(key, out var kept) || node.Score > kept.Score)
                best[key] = node;
        }
        var deduped = best.Values.OrderByDescending(n => n.Score).ToList();
        return deduped.Any(n => !n.Power.IsEmpty)
            ? SelectProtectedFrontier(deduped, width)
            : SelectOrdinaryFrontier(deduped, width);
    }

    // The original no-commitment selection: one line per category, then score.
    private static List<Node> SelectOrdinaryFrontier(List<Node> deduped, int width)
    {
        var chosen = new List<Node>();
        var used = new HashSet<Node>();
        foreach (var category in new[] { "attack", "skill", "power", "potion", "endturn", "other" })
        {
            if (chosen.Count >= width) break;
            var pick = deduped.FirstOrDefault(n => Category(n) == category && !used.Contains(n));
            if (pick is not null && used.Add(pick)) chosen.Add(pick);
        }
        foreach (var node in deduped)
        {
            if (chosen.Count >= width) break;
            if (used.Add(node)) chosen.Add(node);
        }
        return chosen.OrderByDescending(n => n.Score).Take(width).ToList();
    }

    private static List<Node> SelectProtectedFrontier(List<Node> deduped, int width)
    {
        var ordinary = deduped.Where(n => n.Power.IsEmpty).ToList();
        var committed = deduped.Where(n => !n.Power.IsEmpty).ToList();
        // Upstream normal seat quota; its formula already leaves at least half
        // of the width to ordinary lines.
        var quota = PowerCommitmentSeatPolicy.SeatQuota(width, aggressive: false);
        var ordinaryReserve = Math.Max(1, width - quota);
        var chosen = new List<Node>();
        var used = new HashSet<Node>();
        // The best ordinary score line is protected before any commitment: an
        // immediate kill or finisher is never displaced by a delayed route.
        foreach (var node in ordinary)
        {
            used.Add(node);
            chosen.Add(node);
            break;
        }
        foreach (var category in new[] { "attack", "skill", "power", "potion", "endturn", "other" })
        {
            if (chosen.Count >= ordinaryReserve) break;
            var pick = ordinary.FirstOrDefault(n => Category(n) == category && !used.Contains(n));
            if (pick is not null && used.Add(pick)) chosen.Add(pick);
        }
        foreach (var node in ordinary)
        {
            if (chosen.Count >= ordinaryReserve) break;
            if (used.Add(node)) chosen.Add(node);
        }
        var commitmentBudget = Math.Min(quota, width - chosen.Count);
        if (commitmentBudget > 0)
        {
            // One best node per (owner,family) key, ordered by the upstream
            // retention rank.
            var bestByKey = new Dictionary<KernelPowerKey, Node>();
            foreach (var node in committed)
                foreach (var key in node.Power.Keys)
                {
                    if (!bestByKey.TryGetValue(key, out var existing)
                        || IsBetterRepresentative(node, key.OwnerId, existing))
                        bestByKey[key] = node;
                }
            var ordered = bestByKey
                .OrderByDescending(pair => RankOf(pair.Value, pair.Key.OwnerId))
                .ToList();

            var reps = new List<Node>();
            var selected = new HashSet<Node>();
            var representedOwners = new HashSet<ulong>();
            void Select(Node node)
            {
                if (!selected.Add(node)) return;
                reps.Add(node);
                // A shared node represents every owner carrying a commitment on
                // it. Counting it once stops quota from being spent twice on the
                // same node and frees a seat for another owner's eligible route.
                foreach (var ownerId in node.Power.OwnerIds) representedOwners.Add(ownerId);
            }
            // First pass: give every distinct eligible owner a seat; one node may
            // cover several owners at once. Second pass spends any seats left.
            foreach (var pair in ordered)
            {
                if (reps.Count >= commitmentBudget) break;
                if (representedOwners.Contains(pair.Key.OwnerId)) continue;
                Select(pair.Value);
            }
            foreach (var pair in ordered)
            {
                if (reps.Count >= commitmentBudget) break;
                Select(pair.Value);
            }
            foreach (var rep in reps)
                if (used.Add(rep)) chosen.Add(rep);
        }
        foreach (var node in deduped)
        {
            if (chosen.Count >= width) break;
            if (used.Add(node)) chosen.Add(node);
        }
        return chosen.OrderByDescending(n => n.Score).ToList();
    }

    // Representative ranking mirrors the upstream retention order: realized
    // evidence, priority, progress, net unrealized value, then score.
    private static (int Realized, int Priority, int Progress, int Net, double Score) RankOf(Node node, ulong ownerId)
    {
        if (!node.Power.TryGet(ownerId, out var commitment)) return (0, 0, 0, 0, node.Score);
        return (commitment.RealizedEvidence, (int)commitment.Priority, commitment.ProgressEvidence,
            commitment.NetUnrealizedValue, node.Score);
    }

    // Deterministic element-wise comparison of the retention rank tuple.
    private static bool IsBetterRepresentative(Node candidate, ulong ownerId, Node existing)
        => RankOf(candidate, ownerId).CompareTo(RankOf(existing, ownerId)) > 0;

    private static string Category(Node node)
    {
        if (node.Path.Length == 0) return "other";
        var action = node.Path[^1];
        if (action.EndTurn) return "endturn";
        if (action.Potion is not null) return "potion";
        return action.Card?.Type switch
        {
            CardType.Attack => "attack",
            CardType.Skill => "skill",
            CardType.Power => "power",
            _ => "other",
        };
    }

    /// <summary>
    /// What reaching the bounded-lookahead horizon is worth.
    /// </summary>
    /// <remarks>
    /// Sized from the evaluator's own tiers (<c>KernelCombatEvaluation.Evaluate</c>):
    /// victory is <c>+10_000_000</c>, a party wipe <c>-100_000_000</c>, and the tactical term is
    /// clamped to <c>+-1_000_000</c>. So this sits ABOVE the clamp — no positional difference can
    /// cancel it, a full-horizon line always beats a stub — and well BELOW victory, so a line that
    /// really ends the fight still wins.
    /// <para>
    /// It has to exist. A bounded search cannot reach a terminal by construction, and the
    /// evaluator charges a deeper line for the enemy turns it eats, so with nothing paying for
    /// the horizon the deepest line is worth nothing: measured live 2026-09-20 with MaxRounds=3,
    /// `best partial: actions=2, nodes=1616` — two cards played and stop, beating a real
    /// five-round line. That is what made the first attempt at bounded lookahead plan a stub and
    /// then hand the fight to the legacy planner.
    /// </para>
    /// </remarks>
    private const double HorizonBonus = 2_000_000;

    private double Score(KernelSession state)
    {
        var score = evaluate(state);
        if (!double.IsFinite(score)) throw new InvalidOperationException("Team score must be finite.");
        return state.HorizonClosed ? score + HorizonBonus : score;
    }
    /// <summary>
    /// The branch's visible-state text at a turn boundary. Best-effort: a branch whose
    /// player has no combat state (a mock, or a state the simulator did not track) must
    /// not kill a search that is otherwise fine — an empty text simply means "no
    /// boundary to validate", which makes deployment refuse the cross-turn reuse rather
    /// than trust it.
    /// </summary>
    private static string BoundaryText(KernelSession child)
    {
        // The TABLE's text, not the ending seat's. Deployment compares this against the
        // live board when the NEXT action is about to be played, and that action can
        // belong to any seat; a per-seat render made the two sides different seats'
        // views on a multi-bot table. See KernelSession.PartyStateText.
        try { return child.PartyStateText(); }
        catch { return ""; }
    }

    /// <summary>
    /// The unified end record for a node that ENDED the fight (§02). Only ever called on
    /// terminal nodes — building one per expanded node would put a per-seat scan on the hot
    /// path for a number nothing reads.
    /// </summary>
    private TerminalRecord TerminalOf(Node node) =>
        TerminalRecord.Capture(node.State, node.State.Party.Count > 0 ? node.State.Party : actors, terminal: true);

    private void Complete(string reason)
    {
        // Prefer a settled plan, but never adopt one that scores worse than an
        // unsettled alternative: settled and projected scores are not identical.
        // A route to the end of the fight wins over anything that merely crossed a round,
        // and over any unfinished frontier node. Only when the search found no terminal
        // branch at all does it fall back to the old preference — and HasRoute records
        // that, so the caller can tell a plan that reaches the end from one that ran out
        // of depth.
        // Read by THREE places — deployment, the opening ladder, and the ladder's cap branch —
        // so what it means has to match what a bounded search can actually achieve. It used to
        // mean "found a line that ends the fight", which under bounded lookahead is unreachable
        // by construction: the ladder then fired on every fight and its cap branch DISABLED THE
        // KERNEL for the rest of combat (measured live 2026-09-20, live8: `never found a line
        // that ends the fight … the rest of this combat goes to the legacy planner`, after which
        // every action was `via=legacy`). A line that reaches the horizon is the deployable
        // success now; one that ends the fight is still strictly better and still preferred.
        HasRoute = bestTerminal is not null || bestHorizon is not null;
        if (reason is not ("cancelled" or "stale-root") && bestTerminal is not null) best = bestTerminal;
        else if (reason is not ("cancelled" or "stale-root") && bestHorizon is not null && bestHorizon.Score >= best.Score) best = bestHorizon;
        else if (reason is not ("cancelled" or "stale-root") && bestResolved is not null && bestResolved.Score >= best.Score)
            best = bestResolved;
        IsComplete = true;
        CompletedState = reason is "cancelled" or "stale-root" ? null : best.State;
        TurnBoundaries = best.Boundaries;
        if (reason is "cancelled" or "stale-root")
        {
            ActionStates = Array.Empty<(string Before, string After)>();
            ReplayFailure = "";
        }
        else
        {
            ActionStates = CaptureActionStates(replayRoot, best.Path, out var replayFailure);
            ReplayFailure = replayFailure;
        }
        CompletedResult = new(Array.AsReadOnly(best.Path), best.Score, expanded,
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(new Dictionary<string, int>(boundaries)), reason,
            best.Risky, ActionStates, ReplayFailure, best.RiskDetail);
        steps.Dispose();
    }

    /// <summary>
    /// Recomputes the branch's visible-state text after each planned action by replaying
    /// the finished path from the captured root.
    /// </summary>
    /// <remarks>
    /// Cost is one state render per action on the FINAL path. Recording the same thing
    /// per search node instead would pay that render across the whole tree, and the tree
    /// is orders of magnitude larger than the path that actually gets deployed.
    ///
    /// Returns empty when the path does not replay. That is deliberate: an unverified
    /// plan must refuse cross-step reuse rather than pass a check it never took.
    /// </remarks>
    private IReadOnlyList<(string Before, string After)> CaptureActionStates(
        KernelSession root, IReadOnlyList<Action> path, out string failure)
    {
        failure = "";
        if (path.Count == 0) return [];
        // One (Before, After) pair per step, both the WHOLE TABLE's text (see
        // KernelSession.PartyStateText). Deployment needs both ends of a step — to tell
        // "this step has not resolved yet" apart from "this step resolved somewhere else"
        // — and it checks them against the same table-wide live text.
        //
        // This used to render the PLAYING seat's view, which was correct for the sentinel
        // itself (both ends and the live side named the same seat) but left every OTHER
        // consumer of a state text to re-derive whose view it was holding. The boundary
        // text did not, and compared two different seats' views on every crossing.
        // Making the text table-wide means no caller has a viewpoint to get wrong.
        var states = new (string Before, string After)[path.Count];
        try
        {
            var session = root.Fork();
            for (var index = 0; index < path.Count; index++)
            {
                var action = path[index];
                var before = session.PartyStateText();
                if (!session.ReplayPlanned(action, options.MaxRounds, out var replayBoundary))
                {
                    // Say WHICH step and WHY. Without this the sentinel reports
                    // "states=0" and every possible cause looks identical.
                    failure = $"step {index}/{path.Count} "
                        + $"({action.Card?.Id.Entry ?? action.Potion?.Id.Entry ?? "end-turn"}): {replayBoundary}";
                    // Return the VERIFIED PREFIX, not nothing. Deployment already handles a
                    // short sentinel: it checks the states it has and skips the rest with a
                    // counter. Discarding all of them because the tail failed erased the
                    // evidence for every step that HAD replayed — measured live 2026-09-20 as
                    // `missing=56` on a 57-step route whose step 39 failed, i.e. zero
                    // verification for the 38 steps before it that were fine.
                    return states[..index];
                }
                states[index] = (before, session.PartyStateText());
            }
        }
        catch (Exception error)
        {
            // A path that throws part-way through is unverified, not verified-and-good.
            failure = $"threw {error.GetType().Name}: {error.Message}";
            return [];
        }
        return states;
    }
    private void Cancel(string reason)
    {
        // A stale/cancelled plan must not retain even its first deployment action.
        best = best with { Path = [] };
        Complete(reason);
    }
    public void FinishAtBudget()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Wrong search thread.");
        if (!IsComplete) Complete("time-budget");
    }
    public void Dispose() { if (disposed) return; steps.Dispose(); disposed = true; }
}

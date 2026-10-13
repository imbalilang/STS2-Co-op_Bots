using System.Runtime.CompilerServices;
using CoopBots.Kernel.Vendor;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel;

public sealed record KernelChoiceCard(string Id, int Upgrade, string StateKey, int Occurrence);
public sealed record KernelChoice(ulong Owner, string Source, string Effect, PileType Pile,
    string Context, int Minimum, int Maximum, IReadOnlyList<KernelChoiceCard> Cards)
{
    // Full menu identity is separate from the selected tokens. A selected card
    // can remain present even though another candidate/source has changed.
    // Null retains compatibility with already serialized plans; new captures
    // always carry the complete ordered menu, including a legal empty menu.
    public IReadOnlyList<KernelChoiceCard>? Options { get; init; }
    public int? RequestedMinimum { get; init; }
    public int? RequestedMaximum { get; init; }
    public string? MenuSource { get; init; }
    public string? Timing { get; init; }
    public int? NativeRound { get; init; }
    public int? PlayerTurnNumber { get; init; }

    public IReadOnlyList<CardModel>? Resolve(ulong owner, IReadOnlyList<CardModel> options, int minimum, int maximum)
        => ResolveMenu(owner, options, minimum, maximum, null);

    public IReadOnlyList<CardModel>? ResolveMenu(ulong owner, IReadOnlyList<CardModel> options,
        int minimum, int maximum, string? menuSource)
    {
        // Native FromHand returns all available cards when fewer than MinSelect
        // remain (Brand played as the last card is a valid empty exhaust choice).
        minimum = Math.Min(minimum, options.Count);
        maximum = Math.Min(maximum, options.Count);
        if (Options is not null && (!Options.SequenceEqual(MenuTokens(options))
            || minimum != Minimum || maximum != Maximum
            || menuSource is not null && MenuSource is not null && menuSource != MenuSource)) return null;
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

    public static IReadOnlyList<KernelChoiceCard> MenuTokens(IReadOnlyList<CardModel> options)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        return options.Select(card => {
            var key = CardChoiceSupport.ChoiceCardKey(card);
            var occurrence = occurrences.GetValueOrDefault(key);
            occurrences[key] = occurrence + 1;
            return new KernelChoiceCard(card.Id.Entry, card.CurrentUpgradeLevel, key, occurrence);
        }).ToArray();
    }

    public string MenuDifference(ulong owner, IReadOnlyList<CardModel> options, int minimum, int maximum,
        string? menuSource)
        => $"plannedOwner={Owner} actualOwner={owner} plannedSource={MenuSource ?? Source} actualSource={menuSource ?? "unknown"} "
            + $"effect={Effect} pile={Pile} context={Context} "
            + $"plannedBounds={Minimum}..{Maximum} requestedBounds={RequestedMinimum}..{RequestedMaximum} "
            + $"actualBounds={minimum}..{maximum} effectiveBounds={Math.Min(minimum, options.Count)}..{Math.Min(maximum, options.Count)} "
            + $"plannedOptions=[{Describe(Options)}] actualOptions=[{Describe(MenuTokens(options))}] selected=[{Describe(Cards)}]";

    private static string Describe(IReadOnlyList<KernelChoiceCard>? cards)
        => cards is null ? "not-captured" : string.Join(',', cards.Select(c => $"{c.Id}+{c.Upgrade}#{c.Occurrence}:{c.StateKey}"));
}

public sealed partial class KernelSession
{
    public KernelSession ForkForReplay()
    {
        var branch = Fork();
        branch.AutoResolveEnemyChoices = true; branch.AutoResolveTurnStartChoices = true;
        return branch;
    }
    /// <summary>
    /// <paramref name="ChoiceOrdinals"/> is the branch's bounded choice vector. It is
    /// carried alongside the resolved <paramref name="Choices"/> so a finished plan can
    /// be replayed deterministically without re-enumerating the choice space.
    /// </summary>
    public sealed record CardBranch(KernelSession State, IReadOnlyList<KernelChoice> Choices, string Boundary,
        IReadOnlyList<int> ChoiceOrdinals)
    {
        public KernelTurnStartChoiceContract? TurnStartChoices { get; init; }
    }
    private int[]? choiceOrdinals;
    private int choicePosition;
    private readonly List<int> choiceCounts = new();
    private readonly List<KernelChoice> resolvedChoices = new();

    // Replays from a settled parent for each bounded choice vector. Pending
    // transactions are never forked. Nested choices are enumerated as well.
    public IEnumerable<CardBranch> CardBranches(CardModel card, MegaCrit.Sts2.Core.Entities.Creatures.Creature? target,
        int maximumBranches = 16, [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
    {
        var core = CoreCardBranches(card, target, maximumBranches);
        // Disabled returns the original iterator, with no probe iterator layer.
        return AllocationProbe.Enabled ? ProbeCardBranches(core, callerFile, callerMember) : core;
    }

    private static IEnumerable<CardBranch> ProbeCardBranches(IEnumerable<CardBranch> core,
        string callerFile, string callerMember)
    {
        var phase = AllocationProbe.CardBranchesPhase(callerFile, callerMember, KernelChoiceContinuation.Active);
        AllocationProbe.ObserveCardBranchesEnumeration(phase);
        using var iterator = core.GetEnumerator();
        for (;;)
        {
            bool hasNext;
            using (AllocationProbe.Measure(phase)) hasNext = iterator.MoveNext();
            // Dispose before yielding: consumer work and suspension are excluded.
            if (!hasNext) yield break;
            yield return iterator.Current;
        }
    }

    private IEnumerable<CardBranch> CoreCardBranches(CardModel card, MegaCrit.Sts2.Core.Entities.Creatures.Creature? target,
        int maximumBranches)
    {
        using var branches = RawCardBranches(card, target,
            KernelChoiceContinuation.Active ? maximumBranches : Math.Max(maximumBranches, KernelChoiceContinuation.MaximumChoices))
            .GetEnumerator();
        if (!branches.MoveNext()) yield break;
        var first = branches.Current;
        if (KernelSearchBudgetScope.IsStopped || KernelChoiceContinuation.Active || !first.Choices.Any(c => c.Pile == PileType.Hand
            && c.Effect is "Discard" or "Exhaust" or "DiscardAndDraw"))
        {
            yield return first;
            for (var i = 1; i < maximumBranches && !KernelSearchBudgetScope.IsStopped
                && branches.MoveNext(); i++) yield return branches.Current;
            yield break;
        }
        // Finish the native card transaction before comparing choices. The
        // cursor below still enumerates exactly the same legal ordinals; its
        // selected token vector is published with the winning COMPLETE branch.
        var candidates = new List<CardBranch> { first };
        while (candidates.Count < Math.Max(maximumBranches, KernelChoiceContinuation.MaximumChoices)
            && !KernelSearchBudgetScope.IsStopped && branches.MoveNext()) candidates.Add(branches.Current);
        var owner = CardOwner(card);
        CardBranch[] ranked;
        using (KernelChoiceContinuation.Enter())
            ranked = candidates.Select((b, i) => (Branch: b, Value: b.Boundary.Length == 0
                && i < KernelChoiceContinuation.MaximumChoices
                    ? KernelChoiceContinuation.Value(this, b.State, owner,
                        Math.Min(candidates.Count, KernelChoiceContinuation.MaximumChoices)) : double.NegativeInfinity))
                .OrderByDescending(v => v.Value).Take(maximumBranches).Select(v => v.Branch).ToArray();
        foreach (var branch in ranked) yield return branch;
    }

    private IEnumerable<CardBranch> RawCardBranches(CardModel card, MegaCrit.Sts2.Core.Entities.Creatures.Creature? target,
        int maximumBranches)
    {
        var queue = new Queue<int[]>(); queue.Enqueue([]);
        var seen = new HashSet<string> { "" };
        // The first native card/choice transaction is the legal fallback and
        // always settles. Only additional analysis branches are optional.
        for (var tries = 0; tries < maximumBranches && (tries == 0 || !KernelSearchBudgetScope.IsStopped)
            && queue.TryDequeue(out var vector); tries++)
        {
            var child = Fork(); child.choiceOrdinals = vector;
            var success = child.Play(card, target, out var boundary);
            var used = Enumerable.Range(0, child.choiceCounts.Count).Select(i => i < vector.Length ? vector[i] : 0).ToArray();
            KernelChoiceContinuation.EnqueueAlternatives(queue, seen, used, child.choiceCounts);
            yield return new(child, child.resolvedChoices.ToArray(), success ? "" : boundary, vector)
                { TurnStartChoices = child.LastTurnStartChoices };
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
        => ReplayPlanned(action, maxRounds, out boundary, out _);

    // Store the resolved identity/target along with replay evidence. A power can
    // change a targeted attack into a group attack between recorded steps.
    public bool ReplayPlanned(KernelTeamSearch.Action action, int maxRounds, out string boundary,
        out KernelTeamSearch.Action resolvedAction)
        => ApplyCausal(action, maxRounds, true, out boundary, out resolvedAction);

    // Existing native apply resolves card identity, targets and immediate menu
    // tokens. Upstream does not distinguish our deployed turn-start contract.
    // A current-side counterfactual is a new prediction with its own horizon;
    // exact replay below still requires every recorded future menu unchanged.
    // Owner 2026-10-12: cached Dex rescue was rejected before potion comparison
    // because a full next-turn contract was applied to the one-side witness.
    public bool ForecastCausal(KernelTeamSearch.Action action, int maxRounds, out string boundary,
        out KernelTeamSearch.Action resolvedAction)
        => ApplyCausal(action, maxRounds, false, out boundary, out resolvedAction);

    private bool ApplyCausal(KernelTeamSearch.Action action, int maxRounds, bool exactTurnStartContract,
        out string boundary, out KernelTeamSearch.Action resolvedAction)
    {
        resolvedAction = action;
        LastTurnStartChoices = null;
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
        if (action.EndTurn) return exactTurnStartContract
            ? ReplayEndTurn(action, maxRounds, out boundary, out resolvedAction)
            : EndTurn(action.Player, maxRounds, out boundary, out resolvedAction);
        if (action.Potion is { } potion)
        {
            if (exactTurnStartContract && !BeginImplicitTurnStartReplay(action, out boundary)) return false;
            try
            {
                var used = UsePotion(potion, action.Target, action.Choices, out boundary);
                if (used) used = exactTurnStartContract
                    ? FinishImplicitTurnStartReplay(action, maxRounds, out boundary)
                    : FinishImplicitForecast(maxRounds, out boundary);
                if (used) resolvedAction = action with { TurnStartChoices = LastTurnStartChoices };
                return used;
            }
            finally { pendingImplicitTurnStartReplay = null; plannedImplicitAction = false; }
        }
        if (action.Card is { } plannedCard)
        {
            // Resolve by IDENTITY first, never by the stored instance. For a generated card
            // (Shiv, Mirage…) that instance is a fresh simulated object whose Original is a
            // new CardModel, and Play's FindCard matches by plain reference equality — so
            // handing it straight through failed every such step with `unplayable`, which
            // is how one Shiv in a path emptied the sentinel for the whole route.
            //
            // Replay can also retain the original instance when a native boundary
            // effect changes its key (Ringing). Resolve that still-present instance
            // and publish its current identity below; live deployment keeps its
            // strict published-key guard.
            var resolved = FindSimulatedCardByKey(
                    action.Player, action.CardStateKey, action.CardStateOccurrence)
                ?? simulator.State.FindCard(plannedCard);
            if (resolved is null)
            {
                boundary = "planned-card-not-found";
                return false;
            }
            if (!ReferenceEquals(resolved.Preview.Owner, action.Player))
            {
                boundary = "planned-card-owner-changed";
                return false;
            }
            var target = NormalizePlannedTarget(simulator.GetTargetType(resolved), ResolveCreature(action.Target, Combat.Creatures));
            // The replay may resolve a stored instance after its mutable key
            // changed at the boundary (for example native Ringing). Publish this
            // branch's identity BEFORE Play removes or further mutates the card.
            var resolvedKey = CardChoiceSupport.ChoiceCardKey(resolved);
            var occurrence = simulator.State.GetPlayerCombatState(action.Player).Hand.Cards
                .TakeWhile(card => !ReferenceEquals(card, resolved))
                .Count(card => CardChoiceSupport.ChoiceCardKey(card) == resolvedKey);
            resolvedAction = action with { Card = resolved.Original, Target = target,
                CardStateKey = resolvedKey, CardStateOccurrence = occurrence };
            var choiceStart = resolvedChoices.Count;
            if (exactTurnStartContract && !BeginImplicitTurnStartReplay(action, out boundary)) return false;
            bool played;
            try
            {
                played = Play(resolved.Original, target, out boundary);
                if (played) played = exactTurnStartContract
                    ? FinishImplicitTurnStartReplay(action, maxRounds, out boundary)
                    : FinishImplicitForecast(maxRounds, out boundary);
            }
            finally { pendingImplicitTurnStartReplay = null; plannedImplicitAction = false; }
            if (played)
            {
                // Tokens are created by this play's actual choice cursor, after
                // any new affliction/cost state. The cursor list belongs to the
                // replay session, so previous actions must not enter this action.
                var actualChoices = resolvedChoices.Skip(choiceStart).ToArray();
                resolvedAction = resolvedAction with { Choices = actualChoices.Length > 0
                    || action.Choices is not null ? actualChoices : null,
                    TurnStartChoices = LastTurnStartChoices };
            }
            return played;
        }
        boundary = "empty-action";
        return false;
    }

    // Only implicit targets are canonicalized. Never invent a replacement for
    // a missing/dead explicit enemy, ally or Osty target.
    public static MegaCrit.Sts2.Core.Entities.Creatures.Creature? NormalizePlannedTarget(
        TargetType type, MegaCrit.Sts2.Core.Entities.Creatures.Creature? target)
        => type is TargetType.None or TargetType.Self or TargetType.AllEnemies
            or TargetType.AllAllies or TargetType.RandomEnemy ? null : target;

    public static bool SameCreature(MegaCrit.Sts2.Core.Entities.Creatures.Creature a,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature b)
        => ReferenceEquals(a, b) || a.CombatId is not null && a.CombatId == b.CombatId
            && a.Side == b.Side && a.Monster is not null && b.Monster?.Id == a.Monster.Id;

    public static MegaCrit.Sts2.Core.Entities.Creatures.Creature? ResolveCreature(
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? target,
        IEnumerable<MegaCrit.Sts2.Core.Entities.Creatures.Creature> roster)
    {
        if (target is null) return null;
        var matches = roster.Where(c => SameCreature(c, target)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : target;
    }

    internal string PlannedActionDetail(KernelTeamSearch.Action action)
    {
        var card = FindSimulatedCardByKey(action.Player, action.CardStateKey, action.CardStateOccurrence)
            ?? (action.Card is null ? null : simulator.State.FindCard(action.Card));
        return $"player={action.Player.NetId} card={action.Card?.Id.Entry} key={action.CardStateKey} "
            + $"occurrence={action.CardStateOccurrence} found={card is not null} energy={Energy(action.Player)} "
            + $"type={ (card is null ? "missing" : simulator.GetTargetType(card).ToString())} "
            + $"target={action.Target?.CombatId} hand=[{string.Join(',', Hand(action.Player).Select(c => c.Id.Entry))}]";
    }

    private TurnStartChoiceCursor ChoiceCursor(MegaCrit.Sts2.Core.Entities.Players.Player owner)
        => TurnStartChoiceCursor.ForAutomaticPolicy(request =>
        {
            if (choiceOrdinals is null) return null;
            var spec = TurnStartChoiceSupport.BuildSpec(simulator, owner, request);
            // Every physical occurrence in a native ten-card SINGLE cleanup
            // menu must survive enumeration. Multiselect keeps its old bound.
            var limit = spec.SourcePile == PileType.Hand && spec.MinCount == 1 && spec.MaxCount == 1
                && spec.Effect is PlanChoiceEffect.Discard or PlanChoiceEffect.Exhaust
                    ? KernelChoiceContinuation.MaximumChoices : 8;
            var options = KernelRelicChoices.CleanupChoices(spec, limit).Take(limit).ToArray();
            // 上游怎么做：BuildSpec/BuildChoices 只给有界候选的"代表序"，弃牌时不区分牌型，
            //            默认取代表序里的第一张（它会把 Wither 印的回合末自伤当攻击价值）。
            // 我们怎么做：手牌弃牌（Discard / DiscardAndDraw）按下面这张表从大到小排：
            //            2+伤害  不虚无的状态/诅咒，以及带回合末伤害的（贪婪、WITHER）
            //            1       本回合确定打不出去的牌（能量不够 / 没有合法目标）
            //            0       还能打的牌
            //           -1       虚无且没有别的负面效果的（进阶之灾、眩晕）——留给回合末烧掉
            // 为什么：用户 2026-09-29「这类虚无状态/诅咒本身没有额外负面效果，主动丢掉不如丢掉那些
            //        已经确定本回合不会打的牌，让这些牌烧掉以避免再次抽到它们」。WITHER 不是虚无，
            //        仍是"先扔掉伤手牌"（V14–V18 定的政策），所以不动。
            // 消耗的普通牌保留上游移除价值顺序，不因当前费用不足提高消耗优先级；
            // 它会永久失去本场后续使用机会。状态/诅咒和无害虚无的既有排序保持。
            // 记录下来的 ordinals 走同一张确定的顺序，所以实盘与仿真取到同一张牌。
            if (spec.SourcePile == PileType.Hand && spec.Effect is PlanChoiceEffect.Discard
                or PlanChoiceEffect.DiscardAndDraw or PlanChoiceEffect.Exhaust)
            {
                options = KernelRelicChoices.OrderHandCleanupChoices(simulator, spec, options).ToArray();
            }
            choiceCounts.Add(options.Length);
            var index = choicePosition < choiceOrdinals.Length ? choiceOrdinals[choicePosition] : 0;
            choicePosition++;
            if (index >= options.Length) throw new InvalidPlannedChoiceBranchException("Choice vector no longer matches this branch.");
            var chosen = options[index] with { SourceId = request.SourceId, ContextId = request.ContextId, Timing = request.Timing };
            var actualOwner = spec.Options.FirstOrDefault()?.Preview.Owner ?? owner;
            resolvedChoices.Add(new(actualOwner.NetId, request.SourceId, request.Effect.ToString(), request.SourcePile,
                request.ContextId, Math.Min(spec.MinCount, spec.Options.Count), Math.Min(spec.MaxCount, spec.Options.Count), chosen.Cards.Select(c =>
                    new KernelChoiceCard(c.CardId, c.UpgradeLevel, c.StateKey, c.OptionOccurrence)).ToArray()) {
                Options = MenuTokensForSpec(spec), RequestedMinimum = spec.MinCount, RequestedMaximum = spec.MaxCount,
                MenuSource = request.MenuSource, Timing = request.Timing.ToString(),
                NativeRound = Combat.RoundNumber, PlayerTurnNumber = Combat.GetPlayerTurnNumber(actualOwner),
            });
            return chosen;
        });

    private static IReadOnlyList<KernelChoiceCard> MenuTokensForSpec(CardChoiceSpec spec)
        => KernelChoice.MenuTokens(spec.Options.Select(card => card.Preview).ToArray());
}

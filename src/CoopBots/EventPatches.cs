using System.Reflection;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

public static class BotEventDriver
{
    private static readonly MethodInfo VoteShared = AccessTools.Method(
        typeof(EventSynchronizer),
        "PlayerVotedForSharedOptionIndex",
        new[] { typeof(Player), typeof(uint), typeof(uint) });
    private static readonly MethodInfo ChooseSolo = AccessTools.Method(
        typeof(EventSynchronizer),
        "ChooseOptionForEvent",
        new[] { typeof(Player), typeof(int) });
    private static readonly FieldInfo PageIndex = AccessTools.Field(typeof(EventSynchronizer), "_pageIndex");
    private static readonly HashSet<EventOption> ChosenOptions = new(ReferenceEqualityComparer.Instance);

    private static readonly Dictionary<ulong, ConstructionPoll<(int Index, string Explanation)>> planning = new();
    public static void Reset()
    {
        ChosenOptions.Clear();
        foreach (var poll in planning.Values) poll.Clear();
        planning.Clear();
    }
    private static bool TryPlan(Player player, EventModel model, out int index, out string explanation)
    {
        if (!planning.TryGetValue(player.NetId, out var poll)) planning[player.NetId] = poll = new();
        string Context() {
            if (model.IsFinished || !AutoPilot.Drives(player.NetId)) throw new OperationCanceledException();
            return $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(model)}:"
                + string.Join(';', model.CurrentOptions.Select(o => $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o)}:{o.TextKey}:{o.IsLocked}:{o.WasChosen}"))
                + string.Join(';', model.DynamicVars.Select(v => $"{v.Key}:{v.Value.BaseValue}"));
        }
        index = -1; explanation = "";
        if (!poll.TryGet(ConstructionDecision.Fingerprint(player) + Context(), token =>
            ConstructionDecision.RunAsync(player, () => (ChooseEventOption(player, model), EventHandlers.Explain(player, model)), Context, token), out var result)) return false;
        index = result.Index; explanation = result.Explanation; return true;
    }

    public static void Tick(RunManager manager, RunState state)
    {
        if (manager.ActionExecutor.CurrentlyRunningAction is not null || !manager.ActionQueueSet.IsEmpty)
            return;
        TickEvents(manager.EventSynchronizer, state);
    }

    internal static void TickEvents(EventSynchronizer synchronizer, RunState state)
    {
        if (synchronizer.Events.Count == 0)
            return;

        bool isShared;
        try
        {
            isShared = synchronizer.IsShared;
        }
        catch
        {
            return;
        }

        foreach (var player in state.Players.Where(player => AutoPilot.Drives(player.NetId)))
        {
            var eventModel = synchronizer.GetEventForPlayer(player);
            if (eventModel.IsFinished || eventModel.CurrentOptions.Count == 0)
                continue;
            // Shared votes are cleared and the synchronizer's page counter is
            // advanced BEFORE the old option's animation/body finishes. A new
            // vote is not a new page: wait for actual fresh option objects just
            // as in solo events. BattlewornDummy otherwise chooses all three
            // encounters while its local animation is still pending.
            if (eventModel.CurrentOptions.Any(option => option.WasChosen || ChosenOptions.Contains(option)))
                continue;

            // The card select inside an event (upgrade / remove / transform) goes
            // through CardSelectCmd, which is already widened.
            var bots = state.Players.Where(p => AutoPilot.Drives(p.NetId)).ToList();
            var humanVotes = state.Players.Where(p => !AutoPilot.Drives(p.NetId))
                .Select(p => synchronizer.GetPlayerVote(p)).ToList();
            var humanVote = isShared ? MultiHumanCooperation.Vote(humanVotes, bots.Count, bots.IndexOf(player)) : null;
            int optionIndex;
            var explanation = "";
            if (!isShared) { if (!TryPlan(player, eventModel, out optionIndex, out explanation)) return; }
            else if (humanVote.HasValue) optionIndex = (int)humanVote.Value;
            else if (humanVotes.Count == 0)
                // Every seat is handed over, so there is no human distribution to copy
                // and Vote() answers null. Without this the shared vote abstains
                // forever and the event never resolves — the same shape as the map
                // vote in TryVoteOnMap. Fall back to the scorer the per-player events
                // already use, so each seat answers for its own deck.
                { if (!TryPlan(player, eventModel, out optionIndex, out explanation)) return; }
            else optionIndex = -1;
            if (optionIndex < 0 || optionIndex >= eventModel.CurrentOptions.Count)
                continue;

            if (isShared)
            {
                if (state.Players.Where(p => !AutoPilot.Drives(p.NetId)).Any(p => !synchronizer.GetPlayerVote(p).HasValue))
                    continue;
                if (synchronizer.GetPlayerVote(player) == (uint)optionIndex)
                    continue;
                var page = (uint)PageIndex.GetValue(synchronizer)!;
                LogChoice(player, eventModel, optionIndex, explanation);
                VoteShared.Invoke(synchronizer, new object[] { player, (uint)optionIndex, page });
            }
            else
            {
                var option = eventModel.CurrentOptions[optionIndex];
                if (!ShouldAnswer(eventModel, option, ChosenOptions))
                    continue;
                ChosenOptions.Add(option);
                LogChoice(player, eventModel, optionIndex, explanation);
                ChooseSolo.Invoke(synchronizer, new object[] { player, optionIndex });
            }
        }
    }

    // Choosing is fire-and-forget: the page stays visible while the chosen
    // option's animation and awaits run, and the game marks it chosen before
    // its body executes. Answering again in that window would take a second,
    // mutually exclusive option — Reflections.Shatter duplicated a whole deck
    // (plus a curse) on every bot this way. A page with any chosen option is
    // still mid-resolution, so wait for it to advance (new pages build fresh
    // options) or for the event to finish.
    internal static bool ShouldAnswer(EventModel eventModel, EventOption option, ICollection<EventOption> chosen)
        => !option.WasChosen && !chosen.Contains(option)
            && !eventModel.CurrentOptions.Any(other => other.WasChosen);

    private static int ChooseEventOption(Player player, EventModel eventModel)
    {
        var usable = eventModel.CurrentOptions
            .Select((option, index) => (option, index))
            .Where(pair => !pair.option.IsLocked && !pair.option.WasChosen
                && !EnchantmentEventPolicy.ShouldAvoid(player, eventModel, pair.option))
            .ToList();
        if (usable.Count == 0)
            return -1;

        if (eventModel is AncientEventModel ancient)
            return AncientOptionValue.Rank(player, ancient).First().Index;

        // Events that need card-specific reasoning the option data cannot
        // express get a handler; everything else falls through to the scorer.
        if (EventHandlers.TryChoose(player, eventModel) is { } special)
        {
            // A negative index is the handler asking to wait for a later page.
            if (special < 0) return -1;
            if (usable.Any(pair => pair.index == special)) return special;
        }

        double Score(EventOption option)
        {
            if (option.IsProceed)
                return 1;
            if (option.WillKillPlayer?.Invoke(player) == true)
                return -1000;
            var key = option.TextKey.ToLowerInvariant();
            var score = 5.0;
            if (key.Contains("relic") || key.Contains("gain") || key.Contains("obtain"))
                score += 8;
            if (key.Contains("heal") || key.Contains("max_hp") || key.Contains("upgrade"))
                score += 6;
            if (key.Contains("gold"))
                score += 4;
            if (key.Contains("damage") || key.Contains("lose"))
                score -= 9;
            // A curse accepted now is one the deck may never be rid of, so its
            // cost rises with the run exactly as a filler card's does — more so
            // once no shop is left to buy a removal at.
            if (key.Contains("curse"))
                score -= 9 * RunDepth.BloatFactor(player);
            // The key only says what the option is called; the game states what it
            // actually gives. T1+T2: price everything the option will REALLY grant —
            // the models the game attached, plus the ones the RandomForeseer port
            // predicts by replaying the generator on a cloned RNG — through one
            // implementation shared with the trade handlers. Deduped, so an option
            // that names its grant is not counted twice.
            var grants = EventTrade.Grants(player, eventModel, option).Value;
            // Grants evaluates the complete transform transaction, including
            // replacement pools, compulsory additions and native duplication.
            score += grants * ScorePerDeckPoint;
            return score;
        }
        var ranked = usable
            .Select(pair => (pair.option, pair.index, Value: Score(pair.option)))
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.index)
            .ToList();
        // Pool-based events (NEOW / OROBAS / PAEL) draw their option ids from a relic pool,
        // so no handler's Explain() covers them and the decision used to leave NO trace at
        // all — a three-way tie broken by page order was indistinguishable from a considered
        // pick. Measured 2026-09-30: OROBAS offered a seat Sea Glass / Driftwood /
        // Archaic Tooth and the log said only which one won. Priced 58.24 / 0.00 / 0.00 at
        // the time, so the two zeroes were tied and the order decided it.
        if (ConstructionDecision.Current is null) LogRanking(player, eventModel, ranked);
        return ranked[0].index;
    }

    /// <summary>
    /// The generic scorer's per-option ranking, so a pool-based event can be read back
    /// afterwards. Option ids are printed as their last segment (<c>SEA_GLASS</c>), which is
    /// the relic id for these lives pools.
    /// </summary>
    private static void LogRanking(Player player, EventModel eventModel,
        IReadOnlyList<(EventOption Option, int Index, double Value)> ranked)
    {
        try
        {
            var detail = string.Join(" ", ranked.Select(entry =>
            {
                var key = entry.Option.TextKey;
                var dot = key.LastIndexOf('.');
                return $"{(dot >= 0 ? key[(dot + 1)..] : key)}#{entry.Index}={entry.Value:F1}";
            }));
            MegaCrit.Sts2.Core.Logging.Log.Info($"CoopBots event ranking: bot={player.NetId} event={eventModel.Id.Entry} {detail}");
        }
        catch { /* instrumentation must never break a run */ }
    }

    internal static IEnumerable<RelicModel> GrantedRelics(EventOption option)
        => option.HoverTips.Select(t => t.CanonicalModel).OfType<RelicModel>()
            .Concat(option.Relic is { } direct ? [direct] : [])
            .DistinctBy(r => r.Id);

    private static void LogChoice(Player player, EventModel model, int index, string explanation)
        => MegaCrit.Sts2.Core.Logging.Log.Info($"CoopBots event: bot={player.NetId} event={model.Id.Entry} "
            + $"chose={model.CurrentOptions[index].TextKey} "
            + explanation + EnchantmentEventPolicy.Explain(player, model));

    // A relic we cannot evaluate must not swing an event decision.
    private static double RelicValueSafe(RelicModel relic, Player player)
    {
        try { return HumanCoopAdvisor.RelicValue(relic, player).Score; }
        catch { return 0; }
    }

    // A card we cannot evaluate is likewise neutral, never a crash.
    private static double CardValueSafe(CardModel card, Player player)
    {
        try { return BuildValue.AddForDecision(card, player).Total; }
        catch { return 0; }
    }

    // The scorer's scale is a handful of points per option; deck values run in tens.
    // 0.25 is the ratio the relic branch has always used, so a card and a relic of
    // equal deck value are worth equally much to this decision.
    private const double ScorePerDeckPoint = 0.25;

    internal static bool ShouldDecipherTablet(Player player, int cost, int availableHealing)
    {
        // Recover cumulative spending from the game's price schedule. No transient
        // visit counter is needed, so reloads/remote peers make the same decision.
        var spent = cost switch { 3 => 0, 6 => 3, 12 => 9, 24 => 21, _ => -1 };
        if (spent < 0 || cost > 6) return false; // Never pay 24, or reduce MaxHp to one.
        var maxHp = player.Creature.MaxHp;
        var hp = player.Creature.CurrentHp;
        var entryMaxHp = maxHp + spent;
        var remainingMaxHp = maxHp - cost;
        var floor = Math.Max(40, (int)Math.Ceiling(player.Character.StartingHp * 0.70));
        if (remainingMaxHp < floor || cost + spent > Math.Min(9, entryMaxHp * 0.15)
            || hp <= maxHp * 0.5) return false;

        var upgrades = player.Deck.Cards.Where(card => card.IsUpgradable).ToList();
        if (upgrades.Count == 0) return false;
        // Native selects uniformly from the CURRENT upgradable cards, without
        // replacement after each purchase. Basic rarity supplies no fabricated
        // upgrade reward: evaluate every actual upgraded clone and average it.
        using var evaluation = DeckStrength.BeginEvaluation();
        var upgradeValue = TabletRandomUpgradeValue(player);
        var clippedHp = Math.Max(0, hp - remainingMaxHp);
        var lowHealthPenalty = hp < maxHp * 0.75 ? cost * 0.5 : 0;
        // MaxHp is a permanent capacity loss; full-health purchases also lose
        // the clipped CURRENT Hp now. Reducing the cap costs real healing at
        // subsequent rest sites (the native rest fraction), rather than pricing
        // this as a reversible three-point wound.
        var permanentCost = TabletMaxHpCost(player, cost) + clippedHp + lowHealthPenalty;
        // This heal is executable NOW. Use the shared event HP unit without
        // the discount reserved for uncertain future potion opportunities.
        var healingValue = EventTrade.Healed(player, availableHealing);
        return upgradeValue > permanentCost + healingValue;
    }

    internal static double TabletRandomUpgradeValue(Player player)
    {
        var pool = player.Deck.Cards.Where(c => c.IsUpgradable).ToArray();
        return pool.Length == 0 ? 0 : pool.Average(c => BuildValue.UpgradeForDecision(c, player).Total);
    }

    internal static double TabletMaxHpCost(Player player, int cost)
    {
        double camps = 0;
        try { camps = AncientOptionValue.RemainingRooms(player, MapPointType.RestSite, 2); }
        catch { /* Detached fixtures have no generated route. */ }
        return cost * (1 + .3 * Math.Min(3, camps));
    }
}


using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Entities.Cards;
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

    public static void Reset() => ChosenOptions.Clear();

    public static void Tick(RunManager manager, RunState state)
    {
        if (manager.ActionExecutor.CurrentlyRunningAction is not null || !manager.ActionQueueSet.IsEmpty)
            return;
        var synchronizer = manager.EventSynchronizer;
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

        foreach (var player in state.Players.Where(player => BotRegistry.IsBot(player.NetId)))
        {
            var eventModel = synchronizer.GetEventForPlayer(player);
            if (eventModel.IsFinished || eventModel.CurrentOptions.Count == 0)
                continue;

            var bots = state.Players.Where(p => BotRegistry.IsBot(p.NetId)).ToList();
            var humanVote = isShared ? MultiHumanCooperation.Vote(state.Players.Where(p => !BotRegistry.IsBot(p.NetId))
                .Select(p => synchronizer.GetPlayerVote(p)).ToList(), bots.Count, bots.IndexOf(player)) : null;
            var optionIndex = isShared ? (humanVote.HasValue ? (int)humanVote.Value : -1)
                : ChooseEventOption(player, eventModel);
            if (optionIndex < 0 || optionIndex >= eventModel.CurrentOptions.Count)
                continue;

            if (isShared)
            {
                if (state.Players.Where(p => !BotRegistry.IsBot(p.NetId)).Any(p => !synchronizer.GetPlayerVote(p).HasValue))
                    continue;
                if (synchronizer.GetPlayerVote(player) == (uint)optionIndex)
                    continue;
                var page = (uint)PageIndex.GetValue(synchronizer)!;
                VoteShared.Invoke(synchronizer, new object[] { player, (uint)optionIndex, page });
            }
            else
            {
                var option = eventModel.CurrentOptions[optionIndex];
                if (!ShouldAnswer(eventModel, option, ChosenOptions))
                    continue;
                ChosenOptions.Add(option);
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
            .Where(pair => !pair.option.IsLocked && !pair.option.WasChosen)
            .ToList();
        if (usable.Count == 0)
            return -1;

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
            if (key.Contains("damage") || key.Contains("lose") || key.Contains("curse"))
                score -= 9;
            // The key only says what the option is called; the game states what
            // it actually gives. Read that instead of inferring magnitude from
            // text: a key like LOST_WISP...CLAIM never mentions the Decay curse
            // the option adds, and "gain" keys look identical whether they hand
            // over a relic or a single card.
            if (option.Relic is { } relic)
                score += RelicValueSafe(relic, player) * 0.25;
            foreach (var tip in option.HoverTips)
            {
                if (tip.CanonicalModel is not CardModel granted) continue;
                score += granted.Type switch
                {
                    // A curse or status is a permanent deck cost, not a reward.
                    CardType.Curse or CardType.Status => -14,
                    CardType.Power => 6,
                    _ => 0,
                };
            }
            return score;
        }
        return usable.OrderByDescending(pair => Score(pair.option)).ThenBy(pair => pair.index).First().index;
    }

    // A relic we cannot evaluate must not swing an event decision.
    private static double RelicValueSafe(RelicModel relic, Player player)
    {
        try { return HumanCoopAdvisor.RelicValue(relic, player).Score; }
        catch { return 0; }
    }

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
        // The upgrade is random: estimate the pool's average, not its best card.
        // These are conservative utility units, not a claim of exact card simulation.
        var upgradeValue = upgrades.Average(card => card.Type is CardType.Curse or CardType.Status ? 0.0
            : card.Rarity == CardRarity.Basic ? 8.0 : 12.0);
        var clippedHp = Math.Max(0, hp - remainingMaxHp);
        var lowHealthPenalty = hp < maxHp * 0.75 ? cost * 0.5 : 0;
        var permanentCost = cost + clippedHp * 0.25 + lowHealthPenalty;
        var healingValue = Math.Min(Math.Max(0, availableHealing), Math.Max(0, maxHp - hp)) * 0.75;
        return upgradeValue > permanentCost + healingValue;
    }
}


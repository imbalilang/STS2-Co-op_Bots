using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;

namespace CoopBots;

internal static class BotRewardDriver
{
    private static readonly MethodInfo SelectReward = AccessTools.Method(
        typeof(RewardsSetSynchronizer),
        "SelectRewardForPlayer",
        new[] { typeof(Player), typeof(int) });
    private static readonly MethodInfo SkipRewards = AccessTools.Method(
        typeof(RewardsSetSynchronizer),
        "SkipRewardsSetOnStackTopForPlayer",
        new[] { typeof(Player) });
    private static readonly FieldInfo SynchronizerField = AccessTools.Field(typeof(RewardsSet), "_synchronizer");
    private static readonly Dictionary<ulong, CardReward> ActiveCardRewards = new();

    public static void TrackCardReward(CardReward reward)
    {
        if (AutoPilot.Drives(reward.Player.NetId))
            ActiveCardRewards[reward.Player.NetId] = reward;
    }

    public static async Task Offer(RewardsSet set)
    {
        if (set.Player.Creature.IsDead)
            return;

        await set.GenerateWithoutOffering();
        var synchronizer = (RewardsSetSynchronizer)SynchronizerField.GetValue(set)!;
        var completion = synchronizer.BeginRewardsSet(set);

        // Work from the original indices: a successful reward stays in the list.
        for (var index = 0; index < set.Rewards.Count; index++)
        {
            if (set.Rewards[index].SuccessfullySelected)
                continue;
            var reward = set.Rewards[index];
            try
            {
                var task = (Task)SelectReward.Invoke(synchronizer, new object[] { set.Player, index })!;
                await task;
            }
            catch (Exception exception)
            {
                // NAME IT, ALL OF IT. This line used to print only `GetBaseException().Message`,
                // so the live occurrences could not be traced to a throw site: measured
                // 2026-09-21/22, `could not take reward 1 or 2 … Index was out of range …` fired
                // 21 times across four batches (godot-mp-a10-01/02/03, c-run-1) and the message we
                // did get is NOT the game's own out-of-bounds guard ("Tried to select reward index
                // … out of bounds"), which means it was thrown from somewhere below it. Without the
                // type, the frames and which reward it was about, there is nothing to fix.
                Log.Warn($"CoopBots could not take reward {index} [{Describe(reward)}] for bot "
                    + $"{set.Player.NetId}; it will be skipped. {Describe(exception)}");
            }

            // A reward that ends without being taken used to be COMPLETELY silent: the driver
            // throws the task's result away, so a potion the game refused (belt full is the
            // ordinary case) simply vanished from the log. That is the "the bot never took the
            // potion" report, and there was no line to read it from.
            if (!reward.SuccessfullySelected)
                Log.Info($"CoopBots reward not taken: {Describe(reward)} for bot {set.Player.NetId} "
                    + $"(set has {set.Rewards.Count}, free potion slot="
                    + $"{(set.Player.HasOpenPotionSlots ? "yes" : "no")}, taken={reward.SuccessfullySelected}).");
        }

        if (!completion.IsCompleted)
            SkipRewards.Invoke(synchronizer, new object[] { set.Player });
        await completion;
        ActiveCardRewards.Remove(set.Player.NetId);
    }

    /// <summary>What a reward is, in one token, for a log that has to name what was lost.</summary>
    private static string Describe(Reward reward)
    {
        try
        {
            var detail = reward switch
            {
                PotionReward potion => potion.Potion?.Id.Entry ?? "?",
                CardReward => $"{reward.GetType().Name}",
                _ => reward.GetType().Name,
            };
            return $"{reward.GetType().Name}:{detail}";
        }
        catch (Exception error) { return $"{reward.GetType().Name} (undescribable: {error.GetType().Name})"; }
    }

    /// <summary>The exception with its type and the first frames — the part that names the throw site.</summary>
    private static string Describe(Exception exception)
    {
        var inner = exception.GetBaseException();
        var frames = new StackTrace(inner, fNeedFileInfo: false).GetFrames();
        var where = frames is null
            ? "no stack"
            : string.Join(" <- ", frames.Take(3).Select(frame => frame.GetMethod()?.DeclaringType?.Name
                + "." + frame.GetMethod()?.Name));
        return $"{inner.GetType().Name}: {inner.Message} @ {where}";
    }

    public static PlayerChoiceResult ChoiceForWait(Player player)
    {
        var declaringNames = new StackTrace().GetFrames()
            .Select(frame => frame.GetMethod()?.DeclaringType?.FullName ?? string.Empty)
            .ToList();

        if (declaringNames.Any(name => name.Contains(nameof(CardReward), StringComparison.Ordinal))
            && ActiveCardRewards.TryGetValue(player.NetId, out var reward))
        {
            var cards = reward.Cards.ToList();
            return PlayerChoiceResult.FromIndex(BotBrain.ChooseCardReward(player, cards));
        }

        if (declaringNames.Any(name => name.Contains("MendRestSiteOption", StringComparison.Ordinal)))
        {
            var target = player.RunState.Players
                .Where(candidate => candidate.NetId != player.NetId && candidate.Creature.IsAlive)
                .OrderBy(candidate => (double)candidate.Creature.CurrentHp / Math.Max(1, candidate.Creature.MaxHp))
                .ThenBy(candidate => candidate.NetId)
                .FirstOrDefault();
            return PlayerChoiceResult.FromPlayerId(target?.NetId);
        }

        Log.Warn($"CoopBots encountered an unknown remote choice for bot {player.NetId}; selecting index 0.");
        return PlayerChoiceResult.FromIndex(0);
    }
}

[HarmonyPatch(typeof(RewardsSet), nameof(RewardsSet.Offer))]
internal static class BotRewardsSetPatch
{
    private static bool Prefix(RewardsSet __instance, ref Task __result)
    {
        // IsBot, deliberately NOT Drives. This prefix replaces Offer wholesale, and for
        // a synthetic bot that is right: the original only generates rewards on the
        // backend for a non-local player. A handed-over seat IS the local player, and
        // the original's local branch is the only thing that ever calls
        // NRewardsScreen.ShowScreen — replace it and the rewards screen is never
        // created, so the room has nothing to advance through. The seat instead rides
        // the normal flow: the screen appears, BotCardSelector answers the card,
        // BotRewardsScreenDriver clicks the rest, BotRoomProceedDriver presses continue.
        if (!BotRegistry.IsBot(__instance.Player.NetId))
            return true;
        __result = BotRewardDriver.Offer(__instance);
        return false;
    }
}

[HarmonyPatch]
internal static class CardRewardTrackingPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(CardReward), "OnSelect");
    private static void Prefix(CardReward __instance) => BotRewardDriver.TrackCardReward(__instance);
}

[HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.WaitForRemoteChoice))]
internal static class BotRemoteChoicePatch
{
    private static bool Prefix(Player player, ref Task<PlayerChoiceResult> __result)
    {
        if (!AutoPilot.Drives(player.NetId))
            return true;
        __result = Task.FromResult(BotRewardDriver.ChoiceForWait(player));
        return false;
    }
}

// Cheated bots are meant to feel like a teammate who is quietly cheating: every
// gold payout is tripled. Patching the single command that pays gold keeps the
// cheat deterministic for every peer (it derives from the bot's NetId) instead
// of persisting an extra field the fixed run schema has no room for. Gold
// stolen back was already the player's, so it is not multiplied again.
[HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.GainGold))]
internal static class BotGoldCheatPatch
{
    private static void Prefix(Player player, ref decimal amount, bool wasStolenBack)
    {
        if (wasStolenBack || !BotRegistry.IsCheated(player.NetId))
            return;
        amount *= 3m;
    }
}

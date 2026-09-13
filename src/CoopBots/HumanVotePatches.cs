using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

// Invalidate derived bot votes before the original method checks "all voted".
// A Tick-only correction is too late if the last human vote resolves the choice immediately.
[HarmonyPatch(typeof(MapSelectionSynchronizer), nameof(MapSelectionSynchronizer.PlayerVotedForMapCoord))]
internal static class HumanMapVotePatch
{
    private static void Prefix(MapSelectionSynchronizer __instance, Player player, MapLocation source,
        MapVote? destination, RunState ____runState, MapLocation ____acceptingVotesFromSource)
    {
        if (BotRegistry.IsBot(player.NetId) || source != ____acceptingVotesFromSource
            || destination?.mapGenerationCount < __instance.MapGenerationCount) return;
        foreach (var bot in ____runState.Players.Where(p => BotRegistry.IsBot(p.NetId)))
            if (__instance.GetVote(bot).HasValue) __instance.PlayerVotedForMapCoord(bot, source, null);
    }
}

[HarmonyPatch]
internal static class HumanEventVotePatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(EventSynchronizer), "PlayerVotedForSharedOptionIndex");
    private static void Prefix(Player player, uint pageIndex, uint ____pageIndex, List<uint?> ____playerVotes)
    {
        if (BotRegistry.IsBot(player.NetId) || pageIndex != ____pageIndex) return;
        var players = player.RunState.Players;
        for (var i = 0; i < players.Count && i < ____playerVotes.Count; i++)
            if (BotRegistry.IsBot(players[i].NetId)) ____playerVotes[i] = null;
    }
}

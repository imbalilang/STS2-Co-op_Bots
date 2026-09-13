using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CoopBots;

/// <summary>
/// Virtual players do not own a network connection. Seed the combat sync barrier
/// with each bot's deterministic local state, while real peers still exchange and
/// validate their normal synchronization messages.
/// </summary>
[HarmonyPatch]
internal static class CombatSyncBotPatch
{
    private static readonly FieldInfo SyncData = AccessTools.Field(typeof(CombatStateSynchronizer), "_syncData");
    private static readonly FieldInfo RunStateField = AccessTools.Field(typeof(CombatStateSynchronizer), "_runState");

    private static MethodBase TargetMethod() => AccessTools.Method(typeof(CombatStateSynchronizer), "CheckSyncCompleted");

    private static void Prefix(CombatStateSynchronizer __instance)
    {
        var state = (RunState)RunStateField.GetValue(__instance)!;
        var syncData = (Dictionary<ulong, SerializablePlayer>)SyncData.GetValue(__instance)!;
        foreach (var bot in state.Players.Where(player => BotRegistry.IsBot(player.NetId)))
            syncData.TryAdd(bot.NetId, bot.ToSerializable());
    }
}

/// <summary>
/// After end-of-turn hooks finish, every real peer automatically queues one
/// ReadyToBeginEnemyTurnAction. Virtual players have no peer to do that. Seed
/// their phase-two readiness before processing each real player's ready action,
/// preserving the requirement that every real player must still become ready.
/// </summary>
[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetReadyToBeginEnemyTurn))]
internal static class EnemyTurnReadyBotPatch
{
    [ThreadStatic]
    private static bool _seedingBots;

    private static void Prefix(CombatManager __instance, Player player)
    {
        if (_seedingBots || BotRegistry.IsBot(player.NetId) || player.Creature.CombatState is not { } combat)
            return;

        var bots = combat.Players.Where(candidate => BotRegistry.IsBot(candidate.NetId)).ToList();
        if (bots.Count == 0)
            return;

        _seedingBots = true;
        try
        {
            foreach (var bot in bots)
                __instance.SetReadyToBeginEnemyTurn(bot);
        }
        finally
        {
            _seedingBots = false;
        }

        Log.Info($"CoopBots: marked {bots.Count} bot(s) ready for the enemy turn.");
    }
}

/// <summary>
/// The normal act transition waits for a ready action from every Player. Mark bot
/// slots ready whenever a real ready action is processed; real players still all
/// need to confirm normally.
/// </summary>
[HarmonyPatch(typeof(ActChangeSynchronizer), nameof(ActChangeSynchronizer.OnPlayerReady))]
internal static class ActChangeBotPatch
{
    private static readonly FieldInfo ReadyPlayers = AccessTools.Field(typeof(ActChangeSynchronizer), "_readyPlayers");
    private static readonly FieldInfo RunStateField = AccessTools.Field(typeof(ActChangeSynchronizer), "_runState");

    private static void Prefix(ActChangeSynchronizer __instance)
    {
        var state = (RunState)RunStateField.GetValue(__instance)!;
        var ready = (List<bool>)ReadyPlayers.GetValue(__instance)!;
        for (var index = 0; index < state.Players.Count; index++)
        {
            if (BotRegistry.IsBot(state.Players[index].NetId))
                ready[index] = true;
        }
    }
}

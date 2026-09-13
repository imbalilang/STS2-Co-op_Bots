using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

internal static class BotReconnect
{
    internal static void Restore(LoadRunLobby lobby)
    {
        if (lobby.NetService.Type != NetGameType.Host) return;
        foreach (var saved in lobby.Run.Players.Where(p => BotRegistry.IsBot(p.NetId)))
        {
            var index = lobby.Players.FindIndex(p => p.id == saved.NetId);
            if (index >= 0 && lobby.Players[index].isReady) continue;
            var bot = new LoadRunLobbyPlayer { id = saved.NetId, isReady = true, isModded = true };
            if (index < 0)
            {
                lobby.Players.Add(bot);
                lobby.LobbyListener.PlayerConnected(bot);
                // Late real joiners receive the same list in ClientLoadJoinResponseMessage.
                lobby.NetService.SendMessage(new PlayerReconnectedMessage { player = bot });
            }
            else
            {
                lobby.Players[index] = bot;
                lobby.LobbyListener.PlayerReadyChanged(bot.id);
            }
        }
    }

    internal static IEnumerable<RunLobbyPlayer> RestoreRunPlayers(IPlayerCollection state, IEnumerable<RunLobbyPlayer> connected)
    {
        var result = connected.ToList();
        foreach (var bot in state.Players.Where(p => BotRegistry.IsBot(p.NetId)))
            if (!result.Any(p => p.id == bot.NetId))
                result.Add(new RunLobbyPlayer { id = bot.NetId, isModded = true });
        return result;
    }

    // These notifications are addressed to lobby entries, which include virtual players.
    // A virtual entry must never be passed to a physical network transport.
    internal static void SendReconnect(INetGameService service, PlayerReconnectedMessage message, ulong id)
    { if (!BotRegistry.IsBot(id)) service.SendMessage(message, id); }
    internal static void SendRejoin(INetGameService service, PlayerRejoinedMessage message, ulong id)
    { if (!BotRegistry.IsBot(id)) service.SendMessage(message, id); }
}

[HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.AddLocalHostPlayer))]
internal static class RestoreLoadLobbyBotsPatch
{
    private static void Postfix(LoadRunLobby __instance) => BotReconnect.Restore(__instance);
}

[HarmonyPatch]
internal static class RestoreRunLobbyBotsPatch
{
    private static MethodBase TargetMethod() => typeof(RunLobby).GetConstructors().Single();
    private static void Prefix(IPlayerCollection playerCollection, ref IEnumerable<RunLobbyPlayer> players)
        => players = BotReconnect.RestoreRunPlayers(playerCollection, players);
}

[HarmonyPatch]
internal static class ReconnectNotificationBotsPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(LoadRunLobby), "HandleClientLoadJoinRequestMessage");
        yield return AccessTools.Method(typeof(RunLobby), "HandleClientRejoinRequestMessage");
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.Name == "SendMessage" && method.IsGenericMethod
                && method.GetParameters().Length == 2)
            {
                var type = method.GetGenericArguments()[0];
                var helper = type == typeof(PlayerReconnectedMessage) ? nameof(BotReconnect.SendReconnect)
                    : type == typeof(PlayerRejoinedMessage) ? nameof(BotReconnect.SendRejoin) : null;
                if (helper is not null)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(BotReconnect), helper);
                    replaced++;
                }
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("Unsupported reconnect notification layout; expected exactly one targeted send.");
    }
}

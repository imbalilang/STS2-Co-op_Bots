using System.Collections;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;

namespace CoopBots;

/// <summary>
/// Adds synthetic players without statically referencing the lobby-player
/// struct. The public branch calls it LobbyPlayer while public-beta calls it
/// StartRunLobbyPlayer; all fields used by this mod are otherwise compatible.
/// </summary>
public static class LobbyBotService
{
    private static readonly PropertyInfo PlayersProperty = typeof(StartRunLobby)
        .GetProperty("Players", BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMemberException(typeof(StartRunLobby).FullName, "Players");
    private static readonly Type LobbyPlayerType = PlayersProperty.PropertyType.GetGenericArguments().Single();
    private static readonly FieldInfo? PlayerConnectedEvent = typeof(StartRunLobby)
        .GetField("PlayerConnected", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? PlayerDisconnectedEvent = typeof(StartRunLobby)
        .GetField("PlayerDisconnected", BindingFlags.Instance | BindingFlags.NonPublic);

    public static int PlayerCount(StartRunLobby lobby) => Players(lobby).Count;

    public static IReadOnlyList<ulong> BotIds(StartRunLobby lobby)
        => Players(lobby).Cast<object>().Select(Id).Where(BotRegistry.IsBot).ToList();

    public static void ValidateRuntimeSchema()
    {
        if (!typeof(IList).IsAssignableFrom(PlayersProperty.PropertyType))
            throw new NotSupportedException($"Unsupported lobby list type: {PlayersProperty.PropertyType.FullName}");
        foreach (var fieldName in new[]
                 {
                     "id", "slotId", "character", "unlockState", "maxMultiplayerAscensionUnlocked", "isReady",
                 })
        {
            if (LobbyPlayerType.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public) is null)
                throw new MissingFieldException(LobbyPlayerType.FullName, fieldName);
        }
        var joinedPlayerField = typeof(PlayerJoinedMessage)
            .GetField("lobbyPlayer", BindingFlags.Instance | BindingFlags.Public);
        if (joinedPlayerField?.FieldType != LobbyPlayerType)
            throw new NotSupportedException("Lobby player and PlayerJoinedMessage schemas do not match.");
    }

    public static bool Add(StartRunLobby lobby, CharacterModel character, BotDifficulty difficulty, out string error)
    {
        error = string.Empty;
        if (lobby.NetService.Type != NetGameType.Host)
        {
            error = "只有房主能添加机器人";
            return false;
        }

        var players = Players(lobby);
        if (players.Count >= 4)
        {
            error = "原版大厅已满（最多 4 人）";
            return false;
        }

        var entries = players.Cast<object>().ToList();
        var freeSlot = Enumerable.Range(0, 4).First(slot => entries.All(player => Field<int>(player, "slotId") != slot));
        var serial = Enumerable.Range(1, 99).First(candidate => entries
            .Where(player => BotRegistry.IsBot(Id(player)))
            .All(player => BotRegistry.Serial(Id(player)) != candidate));
        var host = entries.FirstOrDefault(player => Id(player) == lobby.NetService.NetId);
        if (host is null)
        {
            error = "无法读取房主大厅数据";
            return false;
        }

        var bot = Activator.CreateInstance(LobbyPlayerType)
            ?? throw new InvalidOperationException($"Cannot create {LobbyPlayerType.FullName}.");
        SetField(bot, "id", BotRegistry.CreateId(difficulty, serial, freeSlot));
        SetField(bot, "slotId", freeSlot);
        SetField(bot, "character", character);
        SetField(bot, "unlockState", Field<object>(host, "unlockState"));
        SetField(bot, "maxMultiplayerAscensionUnlocked", Field<int>(host, "maxMultiplayerAscensionUnlocked"));
        SetFieldIfPresent(bot, "isModded", true);
        SetField(bot, "isReady", true);

        players.Add(bot);
        lobby.NetService.SendMessage(CreateJoinedMessage(bot));
        InvokeListener(lobby, "PlayerConnected", bot);
        (PlayerConnectedEvent?.GetValue(lobby) as Delegate)?.DynamicInvoke(bot);
        return true;
    }

    public static bool RemoveLast(StartRunLobby lobby, out string error)
    {
        error = string.Empty;
        if (lobby.NetService.Type != NetGameType.Host)
        {
            error = "只有房主能移除机器人";
            return false;
        }

        var players = Players(lobby);
        var index = -1;
        for (var i = players.Count - 1; i >= 0; i--)
        {
            if (BotRegistry.IsBot(Id(players[i]!)))
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            error = "大厅里没有机器人";
            return false;
        }

        var bot = players[index]!;
        var botId = Id(bot);
        players.RemoveAt(index);
        lobby.InputSynchronizer.OnPlayerDisconnected(botId);
        lobby.NetService.SendMessage(new PlayerLeftMessage(botId));
        InvokeListener(lobby, "RemotePlayerDisconnected", bot);
        (PlayerDisconnectedEvent?.GetValue(lobby) as Delegate)?.DynamicInvoke(bot);
        return true;
    }

    public static void SubscribeToPlayerChanges(StartRunLobby lobby, Action callback)
    {
        Subscribe(lobby, "PlayerConnected", callback);
        Subscribe(lobby, "PlayerDisconnected", callback);
    }

    private static IList Players(StartRunLobby lobby)
        => (IList)(PlayersProperty.GetValue(lobby)
            ?? throw new InvalidOperationException("Lobby player list is unavailable."));

    private static ulong Id(object player) => Field<ulong>(player, "id");

    private static T Field<T>(object value, string name)
    {
        var result = value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value)
            ?? throw new MissingFieldException(value.GetType().FullName, name);
        return (T)result;
    }

    private static void SetField(object value, string name, object fieldValue)
    {
        var field = value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingFieldException(value.GetType().FullName, name);
        field.SetValue(value, fieldValue);
    }

    private static void SetFieldIfPresent(object value, string name, object fieldValue)
        => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)?.SetValue(value, fieldValue);

    private static PlayerJoinedMessage CreateJoinedMessage(object bot)
    {
        object boxed = default(PlayerJoinedMessage);
        var field = typeof(PlayerJoinedMessage).GetField("lobbyPlayer", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingFieldException(typeof(PlayerJoinedMessage).FullName, "lobbyPlayer");
        field.SetValue(boxed, bot);
        return (PlayerJoinedMessage)boxed;
    }

    private static void InvokeListener(StartRunLobby lobby, string methodName, object bot)
    {
        var method = lobby.LobbyListener.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.Name.EndsWith(methodName, StringComparison.Ordinal) && candidate.GetParameters().Length == 1)
            ?? throw new MissingMethodException(lobby.LobbyListener.GetType().FullName, methodName);
        method.Invoke(lobby.LobbyListener, new[] { bot });
    }

    private static void Subscribe(StartRunLobby lobby, string eventName, Action callback)
    {
        var eventInfo = typeof(StartRunLobby).GetEvent(eventName, BindingFlags.Instance | BindingFlags.Public);
        var handlerType = eventInfo?.EventHandlerType;
        var playerType = handlerType?.GenericTypeArguments.SingleOrDefault();
        if (eventInfo is null || handlerType is null || playerType is null)
            return;
        var factory = typeof(LobbyBotService).GetMethod(nameof(CreateCallback), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(playerType);
        var handler = (Delegate)factory.Invoke(null, new object[] { callback })!;
        eventInfo.AddEventHandler(lobby, handler);
    }

    private static Delegate CreateCallback<T>(Action callback) => new Action<T>(_ => callback());
}

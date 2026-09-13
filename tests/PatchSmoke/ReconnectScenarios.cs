using System.Reflection;
using CoopBots;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

internal static class ReconnectScenarios
{
    internal static void Run(RunState state)
    {
        var save = new SerializableRun { Players = state.Players.Select(p => p.ToSerializable()).ToList() };
        var originalIds = save.Players.Select(p => p.NetId).ToArray();
        var originalHp = save.Players.Select(p => p.CurrentHp).ToArray();
        var host = DispatchProxy.Create<INetHostGameService, ReconnectNetProxy>();
        var transport = (ReconnectNetProxy)host;
        var listener = new Listener();
        var lobby = new LoadRunLobby(host, listener, save);
        lobby.AddLocalHostPlayer(); // Exercise the actual patched entry point.
        Check(lobby.Players.Count == state.Players.Count, "Saved bots must reappear when the host opens the load lobby.");
        Check(lobby.Players.Where(p => BotRegistry.IsBot(p.id)).All(p => p.isReady && p.isModded), "Restored bots must be ready and modded.");
        Check(!lobby.IsPlayerReady(1) && !lobby.IsAboutToBeginGame(), "Restoring bots must not ready the human or auto-start.");
        Check(!lobby.IsPlayerReady(999), "An absent human must remain absent, not become a bot.");
        var restore = typeof(BotBrain).Assembly.GetType("CoopBots.BotReconnect")!.GetMethod("Restore", BindingFlags.Static | BindingFlags.NonPublic)!;
        var connectedCount = listener.Connected.Count;
        var sentCount = transport.Sent.Count;
        restore.Invoke(null, new object[] { lobby });
        Check(listener.Connected.Count == connectedCount && transport.Sent.Count == sentCount && lobby.Players.Select(p => p.id).Distinct().Count() == lobby.Players.Count,
            "Repeated restoration must not duplicate slots, UI notifications or broadcasts.");
        Check(save.Players.Select(p => p.NetId).SequenceEqual(originalIds) && save.Players.Select(p => p.CurrentHp).SequenceEqual(originalHp),
            "Reconnection must not replace saved player identities or health.");
        Check(lobby.Players.Where(p => BotRegistry.IsBot(p.id)).All(p => originalIds.Contains(p.id)), "Reconnection must preserve encoded bot difficulty and serial IDs.");

        var client = DispatchProxy.Create<INetHostGameService, ReconnectNetProxy>();
        ((ReconnectNetProxy)client).Kind = NetGameType.Client;
        var clientLobby = new LoadRunLobby(client, new Listener(), new ClientLoadJoinResponseMessage
            { serializableRun = save, playersAlreadyConnected = lobby.Players.ToList() });
        Check(clientLobby.Players.Count == lobby.Players.Count && clientLobby.Players.Where(p => BotRegistry.IsBot(p.id)).All(p => p.isReady),
            "A joining client must receive the restored bot roster through the native join response.");
        restore.Invoke(null, new object[] { clientLobby });
        Check(((ReconnectNetProxy)client).Sent.Count == 0, "Clients must not independently announce synthetic peers.");

        var runLobby = new RunLobby(default, host, listener, state, new[] { new RunLobbyPlayer { id = 1 } });
        Check(runLobby.Players.Count == state.Players.Count, "Run lobby reconstruction must include saved virtual teammates.");
        runLobby.Dispose();
        runLobby = new RunLobby(default, host, listener, state,
            lobby.Players.Select(p => new RunLobbyPlayer { id = p.id, isModded = p.isModded }));
        Check(runLobby.Players.Count == state.Players.Count, "Run reconstruction must not duplicate already restored bots.");
        runLobby.Dispose();

        // A real second human joins: execute the patched game's handshake loop with bots in Players.
        var secondHuman = state.Players[0].ToSerializable();
        secondHuman.NetId = 99;
        save.Players.Add(secondHuman);
        var join = typeof(LoadRunLobby).GetMethod("HandleClientLoadJoinRequestMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        join.Invoke(lobby, new object[] { new ClientLoadJoinRequestMessage(), 99UL });
        Check(lobby.Players.Any(p => p.id == 99 && !p.isReady), "A real joining human retains their own ready choice.");
        Check(transport.Sent.Where(s => s.Id.HasValue).All(s => !BotRegistry.IsBot(s.Id!.Value)), "Handshake must not send physical packets to bots.");
        var joinResponse = transport.Sent.Select(s => s.Message).OfType<ClientLoadJoinResponseMessage>().Last();
        Check(joinResponse.playersAlreadyConnected.Count(p => BotRegistry.IsBot(p.id)) == originalIds.Count(BotRegistry.IsBot),
            "Actual handshake response must include every saved bot exactly once.");
        lobby.Players.RemoveAll(p => p.id == 99);
        save.Players.Remove(secondHuman);
        lobby.SetReady(true);
        Check(listener.Begun == 1, "Host ready must resume a one-human-plus-bots save without waiting for synthetic connections.");
        lobby.CleanUp(false); clientLobby.CleanUp(false);
        var reopened = new LoadRunLobby(host, new Listener(), save);
        reopened.AddLocalHostPlayer();
        Check(reopened.Players.Count == state.Players.Count && !reopened.IsPlayerReady(1),
            "Opening the same save in a fresh lobby must restore bots without retaining the old human ready flag.");
        reopened.CleanUp(false);
        var vanilla = new LoadRunLobby(host, new Listener(), new SerializableRun { Players = new() { save.Players[0] } });
        vanilla.AddLocalHostPlayer();
        Check(vanilla.Players.Count == 1 && !vanilla.IsPlayerReady(1), "A save without bots must not gain any synthetic players.");
        vanilla.CleanUp(false);
        Console.WriteLine("PASS: saved bots auto-ready, identity/HP preservation, idempotent restore, human ready control, client handshake, no synthetic network sends, run roster recovery and host resume.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Listener : ILoadRunLobbyListener, IRunLobbyListener
    {
        internal readonly List<ulong> Connected = new();
        internal int Begun;
        public void PlayerConnected(LoadRunLobbyPlayer player) => Connected.Add(player.id);
        public void RemotePlayerDisconnected(ulong id) { }
        public Task<bool> ShouldAllowRunToBegin() => Task.FromResult(true);
        public void BeginRun() => Begun++;
        public void PlayerReadyChanged(ulong id) { }
        public void LocalPlayerDisconnected(NetErrorInfo info) { }
        public ClientRejoinResponseMessage GetRejoinMessage() => default;
        public void RunAbandoned() { }
    }
}

public class ReconnectNetProxy : DispatchProxy
{
    public NetGameType Kind = NetGameType.Host;
    public List<(object Message, ulong? Id)> Sent = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_Type": return Kind;
            case "get_NetId": return 1UL;
            case "get_IsConnected": return true;
            case "get_LocalVersion": return new PeerVersionInfo();
            case "GetVersionInfoForPeer": return (PeerVersionInfo?)new PeerVersionInfo();
            case "SendMessage":
                var id = args!.Length == 2 ? (ulong?)args[1] : null;
                if (id.HasValue && BotRegistry.IsBot(id.Value)) throw new Exception("Physical send to a bot!");
                Sent.Add((args[0]!, id)); return null;
            case "DisconnectClient": throw new Exception("Handshake unexpectedly disconnected a real client.");
        }
        return method.ReturnType == typeof(void) || !method.ReturnType.IsValueType ? null : Activator.CreateInstance(method.ReturnType);
    }
}

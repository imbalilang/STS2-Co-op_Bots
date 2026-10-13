using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace CoopBots;

// Sentinel-only recovery of our original four-seat native snapshots. Mirrors
// RunManager.SetUpSavedMultiplayer + NGame.LoadRun, with saving disabled so an
// unattended diagnostic cannot overwrite the player's normal save slot.
internal static class LiveTestSnapshotResume
{
    internal static void Start(string path) => TaskHelper.RunSafely(Load(path));

    private static async Task Load(string path)
    {
        if (!LiveTestAutoStart.Enabled) throw new InvalidOperationException("Snapshot recovery requires the live-test sentinel.");
        var manager = RunManager.Instance;
        var game = NGame.Instance ?? throw new InvalidOperationException("Game scene is unavailable.");
        if (manager.DebugOnlyGetState() is not null) throw new InvalidOperationException("Cannot restore over an active run.");
        var save = JsonSerializer.Deserialize(File.ReadAllText(path), JsonSerializationUtility.GetTypeInfo<SerializableRun>())
            ?? throw new InvalidOperationException("Empty native run snapshot.");
        var state = RunState.FromSerializable(save);
        var net = new NetHostGameService(PeerVersionInfo.LocalDefault());
        if (net.StartENetHost(33771, 4) is { } error) throw new InvalidOperationException($"Snapshot host failed: {error}");
        if (state.Players.Count != 4 || state.Players[0].NetId != net.NetId
            || state.Players.Skip(1).Any(p => !BotRegistry.IsBot(p.NetId)))
        {
            net.Disconnect(MegaCrit.Sts2.Core.Entities.Multiplayer.NetError.Quit);
            throw new InvalidOperationException("Snapshot recovery only supports our local host plus three saved bots.");
        }
        var input = new PeerInputSynchronizer(net);
        AccessTools.Property(typeof(RunManager), "State").SetValue(manager, state);
        // Same order as SetUpSavedMultiplayer, excluding the profile-writing
        // IncrementNumReloads and using shouldSave=false for diagnostic runs.
        AccessTools.Method(typeof(RunManager), "InitializeShared").Invoke(manager,
            new object?[] { net, input, false, save.DailyTime, save.StartTime, save.RunTime, save.WinTime, save.NumReloads });
        var players = state.Players.Select(p => new RunLobbyPlayer { id = p.NetId, isModded = net.LocalVersion.IsModded() });
        AccessTools.Method(typeof(RunManager), "InitializeRunLobby").Invoke(manager, new object[] { net, state, players });
        AccessTools.Method(typeof(RunManager), "InitializeSavedRun").Invoke(manager, new object[] { save });
        foreach (var player in state.Players) AutoPilot.Set(player.NetId, true);
        await game.LoadRun(state, save.PreFinishedRoom);
        LiveTestMultiplayerRun.MarkResumed(state);
        Log.Info($"CoopBots live test: snapshot resumed — path={path} ascension={save.Ascension} "
            + $"seats={state.Players.Count} floor={state.TotalFloor} "
            + $"hp=[{string.Join(",", state.Players.Select(p => p.Creature.CurrentHp))}] "
            + $"decks=[{string.Join(",", state.Players.Select(p => p.Deck.Cards.Count))}]");
    }
}

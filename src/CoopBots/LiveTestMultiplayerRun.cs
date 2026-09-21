using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace CoopBots;

/// <summary>
/// 实机测试的**多人**开局：开一个 ENet 房主，塞满 4 个席位并全部交给 AutoPilot，按 A10 开始爬塔。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不走界面：`NMultiplayerHostSubmenu` 要人点"开房"再点"准备"。这里的每一步都是游戏自己公开的 API，
/// 顺序照抄 `NMultiplayerTest.StartHost`（`debug/multiplayer_test`，那个类就是给无人值守用的）：
/// <c>NetHostGameService + StartENetHost(port, max) → StartRunLobby → AddLocalHostPlayer → LobbyBotService.Add × N</c>。
/// </para>
/// <para>
/// **为什么用 ENet 而不是 Steam**：`StartENetHost` 是纯 UDP，不需要 Steam 大厅、不需要第二个账号、
/// 也不会留下 Steam 会话残留。`--fastmp` 那句游戏注释说的就是这件事
/// （`PlatformType = (SteamInitializer.Initialized &amp;&amp; !HasArg("fastmp")) ? Steam : None`）。
/// 单人席位的实机管线跑得再熟，也测不到"Host 才有的那些门"：地图投票的多人分支、商店的 ack 流程、
/// 药水的按席归属、多人牌的团队目标——它们全在 `NetGameType.Host` 下才亮起来。
/// </para>
/// <para>
/// **A10 的诚实做法**：`MaxAscension` 是各席位"已解锁上限"的最小值，而 bot 席位继承房主的。
/// 所以 A10 能不能开取决于档案里 `MaxMultiplayerAscension`，这里**钳制并如实打出来**，
/// 不去伪造一个没解锁的难度（伪造出来的 A10 是假 A10，量出来的难度曲线也是假的）。
/// </para>
/// </remarks>
internal static class LiveTestMultiplayerRun
{
    internal const ushort Port = 33771;
    /// <summary>原版大厅上限就是 4（<c>LobbyBotService.Add</c> 里也照着 4 判满）。</summary>
    internal const int Seats = 4;
    /// <summary>1 个房主席位 + 3 个 bot 席位；房主席位同样交给 AutoPilot，所以是"4 BOT"。</summary>
    internal const int BotSeats = Seats - 1;
    internal const int WantedAscension = 10;

    /// <summary>本机这次是否已经成功开进局 —— 供看护/诊断用。</summary>
    internal static bool Started { get; private set; }

    /// <summary>
    /// <paramref name="room"/> and <paramref name="encounter"/> come from the sentinel and, when
    /// set, drop the 4-seat party straight into that fight instead of the map.
    ///
    /// WHY THIS EXISTS: a two-phase boss defect could only be reproduced by playing whole runs —
    /// the act-1 boss is seed-chosen AND its transition only fires if the party reaches 0 HP on
    /// it, and the defect itself makes that hard. Ten live batches hit the path four times. The
    /// encounter is resolved BY ID against the model database, never by a guessed type name.
    /// </summary>
    internal static bool Start(string seed, string room = "", string encounter = "")
    {
        var netService = new NetHostGameService(PeerVersionInfo.LocalDefault());
        if (netService.StartENetHost(Port, Seats) is { } hostError)
        {
            Log.Error($"CoopBots live test: could not open the ENet host on port {Port}: {hostError}");
            return false;
        }

        var listener = new Listener();
        var lobby = new StartRunLobby(GameMode.Standard, netService, listener, Seats);
        listener.Lobby = lobby;

        var character = ModelDb.Character<Ironclad>();
        // The host seat inherits the profile's unlock state; the bots copy it (LobbyBotService.Add
        // reads these same two fields off the host entry).
        if (lobby.AddLocalHostPlayer(new UnlockState(SaveManager.Instance.Progress),
                SaveManager.Instance.Progress.MaxMultiplayerAscension) is null)
        {
            Log.Error("CoopBots live test: the host seat could not be added to the lobby.");
            return false;
        }

        for (var i = 0; i < BotSeats; i++)
        {
            if (LobbyBotService.Add(lobby, character, BotDifficulty.Pro, out var botError)) continue;
            Log.Error($"CoopBots live test: bot seat {i + 1}/{BotSeats} was refused: {botError}");
            return false;
        }

        var ascension = Math.Min(WantedAscension, lobby.MaxAscension);
        lobby.SyncAscensionChange(ascension);
        // Every seat, the host's included: "4 BOT" means nobody is at the keyboard, and the mod's
        // per-seat gates (`AutoPilot.Drives`) are what make the kernel authoritative for a seat.
        foreach (var player in lobby.Players) AutoPilot.Set(player.id, true);

        Log.Info($"CoopBots live test: multiplayer lobby ready — seats={lobby.Players.Count} "
            + $"ascension={ascension} (wanted {WantedAscension}, profile max={SaveManager.Instance.Progress.MaxMultiplayerAscension}, "
            + $"lobby cap={lobby.MaxAscension}, difficulty={BotDifficulty.Pro}); every seat is auto-piloted.");

        // Hand the target to the listener BEFORE the lobby can fire BeginRun — SetReady below is
        // what triggers it, on this same thread.
        listener.Room = room;
        listener.Encounter = encounter;
        // Ready on every seat → the lobby calls Listener.BeginRun on this same thread.
        lobby.SetReady(true);
        return true;
    }

    /// <summary>
    /// 只实现 `BeginRun`，其余是空实现 —— 那 8 个回调是给大厅界面刷新用的。
    /// </summary>
    private sealed class Listener : IStartRunLobbyListener
    {
        internal StartRunLobby? Lobby;
        /// <summary>Sentinel-requested target, empty for the normal map start.</summary>
        internal string Room = "";
        internal string Encounter = "";

        public void BeginRun(string seed, List<ActModel> acts, IReadOnlyList<ModifierModel> modifiers)
        {
            if (Lobby is not { } lobby) return;
            Log.Info($"CoopBots live test: every seat is ready — beginning the multiplayer run (seed={seed}).");
            // Called from the main thread (SetReady ← our Godot timer), and GodotSharp ships a
            // GodotSynchronizationContext, so the continuation comes back to the main thread —
            // the same contract NSceneBootstrapper relies on.
            _ = StartRun(lobby, seed, acts, modifiers, Room, Encounter);
        }

        private static async Task StartRun(StartRunLobby lobby, string seed, List<ActModel> acts,
            IReadOnlyList<ModifierModel> modifiers, string room, string encounter)
        {
            try
            {
                // The canonical multiplayer start, and deliberately NOT SetUpNewSingleplayer +
                // EnterRoomDebug: a multiplayer run has to go through the LOBBY (that is what
                // builds the run's Player list from the seats) and then enters the MAP ROOM,
                // so the bot climbs by voting instead of being dropped into a fight.
                if (NGame.Instance is not { } game)
                {
                    Log.Error("CoopBots live test: NGame.Instance is gone; cannot start the run.");
                    return;
                }
                var runState = await game.StartNewMultiplayerRun(
                    lobby, shouldSave: false, acts, modifiers, seed, lobby.Ascension);
                Started = true;
                Log.Info($"CoopBots live test: multiplayer run started — seed={seed} "
                    + $"ascension={lobby.Ascension} seats={runState.Players.Count} "
                    + $"decks=[{string.Join(",", runState.Players.Select(p => p.Deck.Cards.Count))}]");
                // TARGETED ROOM, only when the sentinel asks for one. The default stays the map
                // (see the note above); this is the debug entry the single-seat path has always
                // used, applied after the lobby has built the seat list.
                if (encounter.Length == 0) return;
                var target = ModelDb.AllEncounters.FirstOrDefault(e =>
                    string.Equals(e.Id.Entry, encounter, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"live-test.flag names encounter='{encounter}', which this build does not know.");
                var roomType = Enum.TryParse<RoomType>(room.Length == 0 ? "Monster" : room,
                    ignoreCase: true, out var parsed) ? parsed : RoomType.Monster;
                await RunManager.Instance.EnterRoomDebug(roomType, MapPointType.Unassigned, target.ToMutable());
                Log.Info($"CoopBots live test: entered {target.Id} directly as room={roomType}.");
            }
            catch (Exception error)
            {
                Log.Error($"CoopBots live test: could not start the multiplayer run: {error}");
            }
        }

        public void PlayerConnected(StartRunLobbyPlayer player) { }
        public void PlayerChanged(StartRunLobbyPlayer player, bool isRandomCharacterResolution) { }
        public void AscensionChanged() { }
        public void SeedChanged() { }
        public void ModifiersChanged() { }
        public void MaxAscensionChanged() { }
        public void RemotePlayerDisconnected(StartRunLobbyPlayer player) { }
        public void LocalPlayerDisconnected(NetErrorInfo info) { }
    }
}

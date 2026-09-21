using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

/// <summary>
/// Carries one bot's divination result to the other peers, so every peer builds
/// the same reward set for that bot. The game's own
/// <see cref="CrystalSphereRewardsMessage"/> cannot be reused: its handler
/// resolves the rewarded player from the *sender*, so it can only ever grant the
/// sender's own rewards.
/// </summary>
public struct BotCrystalSphereMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    public ulong Bot;
    public SerializableCrystalSphereItem[] Items;
    public RunLocation Location { get; set; }
    public bool ShouldBroadcast => true;
    public bool ShouldBuffer => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(Bot);
        writer.WriteInt(Items?.Length ?? 0);
        foreach (var item in Items ?? []) writer.Write(item);
        writer.Write(Location);
    }
    public void Deserialize(PacketReader reader)
    {
        Bot = reader.ReadULong();
        var count = reader.ReadInt();
        Items = new SerializableCrystalSphereItem[count];
        for (var i = 0; i < count; i++) Items[i] = reader.Read<SerializableCrystalSphereItem>();
        Location = reader.Read<RunLocation>();
    }
}

/// <summary>
/// A bot cannot play the divination board: the minigame opens its screen only for
/// the local player, so for a bot the whole interaction is skipped and the event
/// finishes without awarding anything. The bot still paid for it — 51-99 gold, or
/// a permanent Debt curse on the other option — which made this event a straight
/// loss.
///
/// The board is seeded from the run's RNG, so every player's grid holds the same
/// items in the same places. That makes the host's own play a usable script: the
/// host reveals cells, we replay the same clicks on the bot's grid, and the bot
/// collects the matching items. The host's script is recorded from their screen;
/// if it does not arrive in time we fall back to a fixed sweep so the bot always
/// completes and is never worse off than before.
/// </summary>
internal static class CrystalSphereSync
{
    private static readonly MethodInfo OfferRewards = AccessTools.Method(
        typeof(OneOffSynchronizer), "OfferCrystalSphereRewards");
    private static readonly FieldInfo OwnerField = AccessTools.Field(typeof(CrystalSphereMinigame), "_owner");
    private static readonly FieldInfo RevealedField = AccessTools.Field(typeof(CrystalSphereMinigame), "_revealed");

    // The host's own clicks, in order. Replayed onto each bot's board.
    private static readonly List<(CrystalSphereMinigame.CrystalSphereToolType Tool, int X, int Y)> Script = new();
    // The host must finish their own board before a bot can mirror it. Bounded so
    // an abandoned board cannot stall the run.
    private const int WaitMs = 60_000;
    private static bool _hostFinished;
    private static RunManager? _manager;
    private static long _nextLog;

    internal static void Initialize(RunManager instance)
    {
        _manager = instance;
        Script.Clear();
        _hostFinished = false;
        instance.RunLocationTargetedBuffer.RegisterMessageHandler<BotCrystalSphereMessage>(Receive);
    }

    internal static void Reset()
    {
        Script.Clear();
        _hostFinished = false;
    }

    /// <summary>True when this minigame belongs to a seat the host answers for.
    /// A synthetic bot and a handed-over player's seat both qualify: for either one
    /// there is nobody at the keyboard to play the board, and the board is seeded
    /// from the run's RNG so the sweep fallback is always a legal script.</summary>
    internal static bool ShouldDrive(CrystalSphereMinigame game)
    {
        try
        {
            if (_manager is null || !RunAuthority.IsSubmittingPeer()) return false;
            if (OwnerField.GetValue(game) is not Player owner) return false;
            return AutoPilot.Drives(owner.NetId);
        }
        catch { return false; }
    }

    /// <summary>
    /// Replays the host's clicks on the bot's board, then hands the revealed items
    /// to the game's own reward path. Every step fails soft: a bot that ends up
    /// with nothing is exactly the behaviour this replaces.
    /// </summary>
    internal static async Task PlayForBot(CrystalSphereMinigame game)
    {
        try
        {
            await WaitForHost();
            game.SetTool(CrystalSphereMinigame.CrystalSphereToolType.Big);
            foreach (var (tool, x, y) in EffectiveScript(game))
            {
                if (game.IsFinished) break;
                game.SetTool(tool);
                if (x < 0 || x >= game.GridSize.X || y < 0 || y >= game.GridSize.Y) continue;
                await game.CellClicked(game.cells[x, y]);
            }
            await Publish(game);
        }
        catch (Exception error)
        {
            Log.Warn($"CoopBots divination failed for a bot; the event completes with nothing: {error.GetBaseException()}");
        }
    }

    // Wait for the host to finish their own board before mirroring it, using the
    // same 100 ms cadence the rest of the bot code uses. The wait is bounded: the
    // sweep fallback below is always available.
    private static async Task WaitForHost()
    {
        // With the local seat handed over there is no local board being played, so
        // _hostFinished can never be set and the wait would burn its full 60s before
        // falling back to the sweep on every divination. Go straight to the sweep.
        if (MegaCrit.Sts2.Core.Context.LocalContext.NetId is { } me && AutoPilot.Drives(me)) return;
        var started = Environment.TickCount64;
        while (!_hostFinished && Environment.TickCount64 - started < WaitMs)
            await Task.Delay(100);
    }

    // The host's clicks when they exist, otherwise a sweep that still covers the
    // board: the big tool on a 3-cell stride reaches every item.
    private static IReadOnlyList<(CrystalSphereMinigame.CrystalSphereToolType Tool, int X, int Y)> EffectiveScript(
        CrystalSphereMinigame game)
        // A copy, not the live list: PlayForBot drives game.CellClicked while walking
        // this sequence, so anything that appends to Script mid-play would otherwise
        // mutate the collection being enumerated.
        => Script.Count > 0 ? Script.ToList() : Sweep(game.GridSize.X, game.GridSize.Y, game.DivinationCount);

    /// <summary>
    /// Cell centres for the fallback sweep: one reveal per cell of a 3-cell
    /// stride, never more than the divinations available and always inside the
    /// grid. Deterministic, so every peer would produce the same script.
    /// </summary>
    internal static List<(CrystalSphereMinigame.CrystalSphereToolType Tool, int X, int Y)> Sweep(
        int width, int height, int divinations)
    {
        var sweep = new List<(CrystalSphereMinigame.CrystalSphereToolType, int, int)>();
        for (var x = 1; x < width && sweep.Count < divinations; x += 3)
            for (var y = 1; y < height && sweep.Count < divinations; y += 3)
                sweep.Add((CrystalSphereMinigame.CrystalSphereToolType.Big, x, y));
        return sweep;
    }

    // Grant through the game's own path: it is what the local player's own board
    // calls, so the rewards, rarities and any hooks behave identically.
    private static async Task Publish(CrystalSphereMinigame game)
    {
        if (OfferRewards is null || RevealedField?.GetValue(game) is not List<CrystalSphereItem> revealed || revealed.Count == 0)
            return;
        if (OwnerField.GetValue(game) is not Player owner || _manager is null) return;
        var serialized = revealed.Select(item => item.ToSerializable()).ToArray();
        _manager.NetService.SendMessage(new BotCrystalSphereMessage
        {
            Bot = owner.NetId,
            Items = serialized,
            Location = _manager.RunLocationTargetedBuffer.CurrentLocation,
        });
        await OfferFor(owner, revealed);
        if (Environment.TickCount64 >= _nextLog)
        {
            _nextLog = Environment.TickCount64 + 5000;
            Log.Info($"CoopBots divination: {AutoPilot.Label(owner.NetId)} revealed "
                + $"{revealed.Count} item(s) from {game.GridSize.X}x{game.GridSize.Y}.");
        }
    }

    // Rewards come from the event's own RNG, the same source the local player's
    // board uses, so every peer builds the identical reward set.
    private static async Task OfferFor(Player owner, List<CrystalSphereItem> revealed)
    {
        if (OfferRewards is null || _manager is null) return;
        if (_manager.EventSynchronizer.GetEventForPlayer(owner) is not EventModel model) return;
        if (OfferRewards.Invoke(_manager.OneOffSynchronizer, new object[] { owner, revealed, model.Rng })
            is Task task) await task;
    }

    private static void Receive(BotCrystalSphereMessage message, ulong sender)
    {
        try
        {
            if (_manager is null || _manager.NetService.Type == NetGameType.Host) return;
            var state = _manager.DebugOnlyGetState();
            var owner = state?.Players.FirstOrDefault(player => player.NetId == message.Bot);
            // No bot test here: a handed-over seat keeps its real net id, and the
            // host — the only sender of this message — decides which seats it answers
            // for. Filtering on IsBot would make a client silently drop the rewards
            // for the handed-over seat while the host granted them, i.e. desync.
            if (owner is null) return;
            if (_manager.EventSynchronizer.GetEventForPlayer(owner) is not { } model) return;
            var revealed = (message.Items ?? [])
                .Select(item => CrystalSphereItem.FromSerializable(item, owner)).ToList();
            if (revealed.Count == 0) return;
            _ = OfferFor(owner, revealed);
        }
        catch (Exception error)
        {
            Log.Warn($"CoopBots divination rewards could not be mirrored: {error.GetBaseException()}");
        }
    }

    /// <summary>Records the local player's own clicks so bots can replay them.</summary>
    internal static void Record(CrystalSphereMinigame game, CrystalSphereCell cell)
    {
        try
        {
            if (OwnerField?.GetValue(game) is not Player owner) return;
            // Skip every seat we drive, not only synthetic bots. PlayForBot replays
            // the script through game.CellClicked, which lands here as a Postfix —
            // and EffectiveScript hands back the Script list itself, so recording our
            // own replay would append to the collection being enumerated.
            if (AutoPilot.Drives(owner.NetId)) return;
            Script.Add((game.CrystalSphereTool, cell.X, cell.Y));
            // The last click is what ends the host's board; marking it here keeps
            // the wait below to a plain state read instead of a hook on an async
            // private method.
            if (game.IsFinished) _hostFinished = true;
        }
        catch { /* recording is best effort; the sweep fallback covers a miss */ }
    }

}

[HarmonyPatch(typeof(CrystalSphereMinigame), nameof(CrystalSphereMinigame.PlayMinigame))]
internal static class CrystalSpherePlayPatch
{
    private static bool Prefix(CrystalSphereMinigame __instance, ref Task __result)
    {
        if (!CrystalSphereSync.ShouldDrive(__instance)) return true;
        __result = CrystalSphereSync.PlayForBot(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(CrystalSphereMinigame), nameof(CrystalSphereMinigame.CellClicked))]
internal static class CrystalSphereClickPatch
{
    private static void Postfix(CrystalSphereMinigame __instance, CrystalSphereCell clickedCell)
        => CrystalSphereSync.Record(__instance, clickedCell);
}

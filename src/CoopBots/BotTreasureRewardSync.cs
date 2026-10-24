using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

public sealed class BotTreasureRewardMessage : INetMessage, IRunLocationTargetedMessage
{
    public int Sequence;
    public ulong Bot;
    public string Room = "", Before = "", After = "";
    public bool Ack, Success;
    public RunLocation Location { get; set; }
    public bool ShouldBroadcast => false;
    public bool ShouldBuffer => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Sequence); writer.WriteULong(Bot); writer.WriteString(Room);
        writer.WriteString(Before); writer.WriteString(After);
        writer.WriteBool(Ack); writer.WriteBool(Success); writer.Write(Location);
    }
    public void Deserialize(PacketReader reader)
    {
        Sequence = reader.ReadInt(); Bot = reader.ReadULong(); Room = reader.ReadString();
        Before = reader.ReadString(); After = reader.ReadString();
        Ack = reader.ReadBool(); Success = reader.ReadBool(); Location = reader.Read<RunLocation>();
    }
}

// One instance per physical peer. Retrying a command only resends its receipt;
// it must never consume reward RNG or complete SPOILS_MAP a second time.
internal sealed class BotTreasureRewardSession(
    Func<RunState?> state, Func<RunLocation> location, Func<bool> authority,
    Func<ulong?> hostId, Func<IReadOnlyCollection<ulong>> peers,
    Action<BotTreasureRewardMessage> send, Func<Player, Task<int>> grant,
    Func<long>? clock = null) : IDisposable
{
    private readonly Dictionary<(string Room, ulong Bot), BotTreasureRewardMessage> completed = new();
    private readonly HashSet<ulong> waiting = new();
    private RunState? run;
    private BotTreasureRewardMessage? pending;
    private Task<bool>? running;
    private IDisposable? lease;
    private int sequence;
    private int generation;
    private long sent;
    private bool faulted;
    private bool announced, disposed;
    private long Now => clock?.Invoke() ?? Environment.TickCount64;

    internal static string RoomIdentity(RunState current)
        => $"act={current.CurrentActIndex};floor={current.TotalFloor};coord={current.CurrentMapCoord?.ToString() ?? "none"}";

    // Rejoin imports the host's gold/deck/RNG but not this in-memory receipt
    // ledger. Bind a command to both native states so a retry can acknowledge an
    // imported completed reward without paying it again.
    internal static string RewardState(Player player)
        => $"gold={player.Gold};spoils={player.Deck.Cards.Count(c => c.Id.Entry == "SPOILS_MAP")};rng="
            + JsonSerializer.Serialize(player.PlayerRng.ToSerializable(), new JsonSerializerOptions { IncludeFields = true });

    private void BindRun(RunState current)
    {
        if (ReferenceEquals(run, current)) return;
        ClearPending(); run = current; sequence = 0; completed.Clear(); faulted = false;
    }

    private static bool Same(BotTreasureRewardMessage left, BotTreasureRewardMessage right)
        => left.Sequence == right.Sequence && left.Bot == right.Bot && left.Room == right.Room
            && left.Location.Equals(right.Location) && left.Before == right.Before && left.After == right.After;

    private bool IsHere(BotTreasureRewardMessage message, RunState current)
        => current.CurrentRoom?.RoomType == RoomType.Treasure
            && message.Room == RoomIdentity(current) && message.Location.Equals(location())
            && message.Location.Equals(current.RunLocation);

    internal void Receive(BotTreasureRewardMessage message, ulong sender)
    {
        if (disposed) return;
        var current = state();
        if (current is null) return;
        BindRun(current);
        if (authority())
        {
            if (!message.Ack || pending is null || !Same(message, pending) || !waiting.Contains(sender)) return;
            if (!message.Success) Fail($"peer={sender} rejected chest reward bot={message.Bot}");
            else waiting.Remove(sender);
            return;
        }
        if (message.Ack || hostId() != sender || !IsHere(message, current)) return;
        if (!BotRegistry.IsBot(message.Bot) || current.Players.All(p => p.NetId != message.Bot)) return;
        if (completed.TryGetValue((message.Room, message.Bot), out var receipt))
        {
            if (Same(message, receipt)) send(receipt);
            return;
        }
        if (pending is not null) return;
        var player = current.Players.First(p => p.NetId == message.Bot);
        var actual = RewardState(player);
        if (message.After.Length > 0 && actual == message.After)
        {
            var loadedReceipt = Receipt(message, true);
            completed[(message.Room, message.Bot)] = loadedReceipt;
            send(loadedReceipt);
            return;
        }
        if (message.Before.Length == 0 || message.After.Length == 0 || actual != message.Before)
        {
            Install(message, current);
            running = Task.FromResult(false);
            Fail($"bot={message.Bot} chest state matches neither command state");
            send(Receipt(message, false));
            return;
        }
        Start(message, current);
    }

    internal bool Tick()
    {
        if (disposed) return false;
        var current = state();
        if (current is null) { Dispose(); return false; }
        BindRun(current);
        if (pending is not null)
        {
            if (!IsHere(pending, current)) { ClearPending(); return false; }
            if (running is not { IsCompleted: true }) return true;
            if (faulted || !running.Result) return true;
            if (authority())
            {
                Announce();
                waiting.IntersectWith(peers());
                if (waiting.Count > 0)
                {
                    if (Now - sent >= 2000) { sent = Now; send(pending); }
                    return true;
                }
            }
            var receipt = Receipt(pending, true);
            completed[(pending.Room, pending.Bot)] = receipt;
            if (!authority()) send(receipt);
            ClearPending();
        }
        if (!authority() || current.CurrentRoom?.RoomType != RoomType.Treasure) return false;
        // Native pushes the room before awaiting entry hooks/assets, but only
        // advances its message buffer after Enter completes. Starting in that
        // gap would pay under the previous location and then pay again when the
        // old transaction is invalidated by the buffer catching up.
        if (!location().Equals(current.RunLocation)) return true;
        var room = RoomIdentity(current);
        var bot = BotTreasureRewardDriver.SyntheticBots(current)
            .FirstOrDefault(p => !completed.ContainsKey((room, p.NetId)));
        if (bot is null) return false;
        var command = new BotTreasureRewardMessage { Sequence = ++sequence, Bot = bot.NetId,
            Room = room, Location = location(), Before = RewardState(bot) };
        // Unlike native DoLocalTreasureRoomRewards, compute the host body first
        // under the departure lease, then publish both states. A reconnect
        // snapshot can therefore recognize a completed reward without a ledger.
        Install(command, current);
        running = Execute(command, bot, generation);
        if (running.IsCompletedSuccessfully && running.Result) Announce();
        return true;
    }

    private void Start(BotTreasureRewardMessage message, RunState current)
    {
        Install(message, current);
        running = Execute(message, current.Players.First(p => p.NetId == message.Bot), generation);
    }

    private void Install(BotTreasureRewardMessage message, RunState current)
    {
        generation++; faulted = false; announced = false;
        pending = message;
        lease = RoomWork.Begin(current.CurrentRoom!);
    }

    private bool Active(BotTreasureRewardMessage message, int epoch)
        => !disposed && epoch == generation && ReferenceEquals(message, pending);

    private static BotTreasureRewardMessage Receipt(BotTreasureRewardMessage message, bool success)
        => new() { Sequence = message.Sequence, Bot = message.Bot, Room = message.Room,
            Before = message.Before, After = message.After, Location = message.Location, Ack = true, Success = success };

    private void Announce()
    {
        if (announced || pending is null) return;
        announced = true; waiting.UnionWith(peers()); sent = Now;
        if (waiting.Count > 0) send(pending);
    }

    private async Task<bool> Execute(BotTreasureRewardMessage message, Player player, int epoch)
    {
        try
        {
            var hadSpoilsMap = player.Deck.Cards.Any(card => card.Id.Entry == "SPOILS_MAP");
            var gold = await grant(player);
            if (!Active(message, epoch)) return false;
            var after = RewardState(player);
            if (authority()) message.After = after;
            else if (after != message.After)
            {
                Fail($"bot={message.Bot} native chest result differs from host state");
                send(Receipt(message, false));
                return false;
            }
            Log.Info($"CoopBots treasure #{message.Sequence}: bot={player.NetId} room={message.Room} +{gold} gold"
                + (hadSpoilsMap ? " (SPOILS_MAP converted)" : ""));
            return true;
        }
        catch (Exception failure)
        {
            if (!Active(message, epoch)) return false;
            Fail($"bot={player.NetId} reward failed: {failure.GetBaseException()}");
            if (!authority()) send(Receipt(message, false));
            return false;
        }
    }

    private void Fail(string reason)
    {
        faulted = true;
        Log.Error($"CoopBots treasure: {reason}; departure held to prevent divergent chest rewards.");
    }

    private void ClearPending()
    {
        generation++;
        pending = null; running = null; waiting.Clear(); lease?.Dispose(); lease = null;
        announced = false;
    }

    public void Dispose()
    {
        disposed = true;
        ClearPending();
        completed.Clear(); run = null;
    }
}

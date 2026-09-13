using System.Text.Json;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

public sealed class BotChoicePlanMessage : INetMessage, IRunLocationTargetedMessage
{
    public int Sequence;
    public uint Action;
    public ulong Bot;
    public string Card = "", Payload = "";
    public bool Ack, Clear;
    public RunLocation Location { get; set; }
    public bool ShouldBroadcast => false;
    public bool ShouldBuffer => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public void Serialize(PacketWriter w)
    {
        w.WriteInt(Sequence); w.WriteUInt(Action); w.WriteULong(Bot); w.WriteString(Card); w.WriteString(Payload);
        w.WriteBool(Ack); w.WriteBool(Clear); w.Write(Location);
    }
    public void Deserialize(PacketReader r)
    {
        Sequence = r.ReadInt(); Action = r.ReadUInt(); Bot = r.ReadULong(); Card = r.ReadString(); Payload = r.ReadString();
        Ack = r.ReadBool(); Clear = r.ReadBool(); Location = r.Read<RunLocation>();
    }
}

internal static class BotChoicePlanSync
{
    private static RunManager? manager;
    private static BotChoicePlanMessage? installed;
    private static KernelChoice[] choices = [];
    private static int index, sequence;
    private static TeamCombatPlanner.Decision? pending;
    private static CombatState? combat;
    private static long revision, sent;
    private static string stamp = "";
    private static uint? focus;
    private static readonly HashSet<ulong> waiting = new();

    internal static void Initialize(RunManager instance)
    {
        manager = instance; installed = null; pending = null; choices = []; index = sequence = 0; waiting.Clear();
        instance.RunLocationTargetedBuffer.RegisterMessageHandler<BotChoicePlanMessage>(Receive);
    }
    private static void Receive(BotChoicePlanMessage message, ulong sender)
    {
        if (manager is null) return;
        if (manager.NetService.Type == NetGameType.Host)
        {
            if (message.Ack && installed?.Sequence == message.Sequence) waiting.Remove(sender);
            return;
        }
        if (message.Ack || manager.NetService is not NetClientGameService client || sender != client.HostNetId) return;
        if (message.Clear)
        {
            if (installed?.Sequence == message.Sequence) { installed = null; choices = []; }
            return;
        }
        if (installed is not null && installed.Sequence > message.Sequence) return;
        if (installed?.Sequence != message.Sequence)
        {
            choices = JsonSerializer.Deserialize<KernelChoice[]>(message.Payload) ?? [];
            installed = message; index = 0;
        }
        manager.NetService.SendMessage(new BotChoicePlanMessage { Ack = true, Sequence = message.Sequence, Location = message.Location });
    }
    internal static bool Prepare(TeamCombatPlanner.Decision decision, CombatState state)
    {
        if (decision.Move.Choices is not { Count: > 0 } plan || manager is null) return true;
        if (pending == decision && installed?.Action == manager.ActionQueueSet.NextActionId && waiting.Count == 0)
        { pending = null; return true; }
        Cancel();
        pending = decision; combat = state; revision = KernelSession.LiveRevision;
        stamp = KernelSession.CaptureLiveStamp(state); focus = BotCooperation.FocusTarget;
        installed = new() { Sequence = ++sequence, Action = manager.ActionQueueSet.NextActionId,
            Bot = decision.Player.NetId, Card = decision.Move.Card.Id.Entry, Payload = JsonSerializer.Serialize(plan),
            Location = manager.RunLocationTargetedBuffer.CurrentLocation };
        choices = plan.ToArray(); index = 0; waiting.Clear();
        if (manager.NetService is NetHostGameService host)
            foreach (var peer in host.ConnectedPeers.Where(p => p.readyForBroadcasting)) waiting.Add(peer.peerId);
        manager.NetService.SendMessage(installed); sent = Environment.TickCount64;
        if (waiting.Count == 0) { pending = null; return true; }
        return false;
    }
    internal static bool Resume(CombatState state, out TeamCombatPlanner.Decision? decision)
    {
        decision = null;
        if (pending is null || manager is null) return false;
        if (!ReferenceEquals(state, combat) || revision != KernelSession.LiveRevision
            || installed?.Action != manager.ActionQueueSet.NextActionId || focus != BotCooperation.FocusTarget)
        { Cancel(); return false; }
        if (manager.NetService is NetHostGameService host)
            waiting.IntersectWith(host.ConnectedPeers.Where(p => p.readyForBroadcasting).Select(p => p.peerId));
        if (waiting.Count == 0)
        {
            if (stamp != KernelSession.CaptureLiveStamp(state)) { Cancel(); return false; }
            decision = pending; return true;
        }
        if (Environment.TickCount64 - sent > 2000) { manager.NetService.SendMessage(installed!); sent = Environment.TickCount64; }
        return true;
    }
    internal static void Cancel()
    {
        if (pending is not null && installed is not null && manager is not null)
            manager.NetService.SendMessage(new BotChoicePlanMessage { Sequence = installed.Sequence, Clear = true, Location = installed.Location });
        pending = null; installed = null; choices = []; waiting.Clear(); index = 0;
    }
    internal static IReadOnlyList<CardModel>? Select(Player player, IReadOnlyList<CardModel> options, int min, int max)
    {
        if (installed is null || index >= choices.Length
            || manager?.ActionExecutor.CurrentlyRunningAction is not PlayCardAction action
            || action.Id != installed.Action || action.OwnerId != installed.Bot || action.CardModelId.Entry != installed.Card) return null;
        var result = choices[index].Resolve(player.NetId, options, min, max);
        if (result is null)
        {
            // Invalidate the remaining transaction; the identical deterministic
            // fallback remains available on every peer when options changed.
            index = choices.Length;
            Log.Warn("CoopBots planned choice no longer matches live options; using deterministic fallback.");
            return null;
        }
        index++;
        return result;
    }
}

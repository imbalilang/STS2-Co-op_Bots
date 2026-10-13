using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

public sealed class BotConstructionPerfMessage : INetMessage, IRunLocationTargetedMessage
{
    public bool Request;
    public int Tier;
    public RunLocation Location { get; set; }
    public bool ShouldBroadcast => false;
    public bool ShouldBuffer => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public void Serialize(PacketWriter writer) { writer.WriteBool(Request); writer.WriteInt(Tier); writer.Write(Location); }
    public void Deserialize(PacketReader reader) { Request = reader.ReadBool(); Tier = reader.ReadInt(); Location = reader.Read<RunLocation>(); }
}

// Synthetic reward/card choices are replayed on every peer. Precision must be
// the host's tier, independent of machine speed and task completion order.
internal static class ConstructionPerfSync
{
    private static RunManager? manager;
    private static TaskCompletionSource? ready;
    private static bool requested;
    private static KernelCombatPlanner.TournamentPerf hostTier;
    internal static KernelCombatPlanner.TournamentPerf Tier => ready is { Task.IsCompletedSuccessfully: true }
        ? hostTier : KernelCombatPlanner.Perf;
    internal static void Initialize(RunManager instance)
    {
        ready?.TrySetCanceled(); manager = instance; requested = false;
        ready = instance.NetService.Type == NetGameType.Client
            ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        instance.RunLocationTargetedBuffer.RegisterMessageHandler<BotConstructionPerfMessage>(Receive);
    }
    internal static async Task EnsureAsync(CancellationToken cancellation)
    {
        if (ready is null || ready.Task.IsCompletedSuccessfully) return;
        if (!requested)
        {
            requested = true;
            manager!.NetService.SendMessage(new BotConstructionPerfMessage {
                Request = true, Location = manager.RunLocationTargetedBuffer.CurrentLocation });
        }
        await ready.Task.WaitAsync(cancellation);
    }
    private static void Receive(BotConstructionPerfMessage message, ulong sender)
    {
        if (manager is null) return;
        if (manager.NetService.Type == NetGameType.Host && message.Request)
        {
            manager.NetService.SendMessage(new BotConstructionPerfMessage {
                Tier = (int)KernelCombatPlanner.Perf, Location = message.Location });
            return;
        }
        if (manager.NetService is not NetClientGameService client
            || !AcceptHost(sender, client.HostNetId, message.Request, message.Tier)) return;
        hostTier = (KernelCombatPlanner.TournamentPerf)message.Tier;
        ready?.TrySetResult();
        Log.Info($"CoopBots construction precision synchronized: {hostTier}");
    }
    internal static bool AcceptHost(ulong sender, ulong host, bool request, int tier)
        => sender == host && !request && Enum.IsDefined(typeof(KernelCombatPlanner.TournamentPerf), tier);
}

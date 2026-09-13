using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

// The native reaction wheel only carries eight emotes, which cannot express
// "hit this target so we can kill it". This is a mod-level callout so every
// client that runs CoopBots sees the same request from the team planner.
public struct BotCalloutMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    public string Text;
    public uint Target;
    public RunLocation Location { get; set; }
    public bool ShouldBroadcast => true;
    public bool ShouldBuffer => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(Text ?? "");
        writer.WriteUInt(Target);
        writer.Write(Location);
    }
    public void Deserialize(PacketReader reader)
    {
        Text = reader.ReadString();
        Target = reader.ReadUInt();
        Location = reader.Read<RunLocation>();
    }
}

internal static class BotCalloutSync
{
    private static RunManager? manager;
    private static string lastText = "";
    private static long lastSentMs;

    internal static void Initialize(RunManager instance)
    {
        manager = instance;
        lastText = "";
        instance.RunLocationTargetedBuffer.RegisterMessageHandler<BotCalloutMessage>(Receive);
    }

    // Host publishes; every client (including the host) shows it locally.
    internal static void Publish(string text, uint target)
    {
        if (manager is null) return;
        if (text == lastText && Environment.TickCount64 - lastSentMs < 4000) return;
        lastText = text;
        lastSentMs = Environment.TickCount64;
        Log.Info($"CoopBots callout: {text}");
        BotCooperation.SetCallout(text);
        if (manager.NetService.Type == NetGameType.Host)
            manager.NetService.SendMessage(new BotCalloutMessage
            {
                Text = text,
                Target = target,
                Location = manager.RunLocationTargetedBuffer.CurrentLocation,
            });
    }

    private static void Receive(BotCalloutMessage message, ulong sender)
    {
        if (manager is null || sender == manager.NetService.NetId) return;
        BotCooperation.SetCallout(message.Text);
    }

    internal static void Reset() => lastText = "";

    internal static void Clear() => Publish("", 0);
}

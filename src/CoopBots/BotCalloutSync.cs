using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

// The native reaction wheel only carries eight emotes, which cannot express
// "hit this target so we can kill it". This is a mod-level callout so every
// client that runs CoopBots sees the same request from the team planner. The
// cards travel with it so the client that owns the hand can point at them:
// only that peer holds the card nodes the arrow is drawn from.
public struct BotCalloutMessage : INetMessage, IPacketSerializable, IRunLocationTargetedMessage
{
    public string Text;
    public ulong Actor;
    public uint Target;
    public string[] Cards;
    public RunLocation Location { get; set; }
    public bool ShouldBroadcast => true;
    public bool ShouldBuffer => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(Text ?? "");
        writer.WriteULong(Actor);
        writer.WriteUInt(Target);
        var cards = Cards ?? Array.Empty<string>();
        writer.WriteInt(cards.Length);
        foreach (var card in cards) writer.WriteString(card);
        writer.Write(Location);
    }
    public void Deserialize(PacketReader reader)
    {
        Text = reader.ReadString();
        Actor = reader.ReadULong();
        Target = reader.ReadUInt();
        var count = Math.Clamp(reader.ReadInt(), 0, 4);
        Cards = new string[count];
        for (var i = 0; i < count; i++) Cards[i] = reader.ReadString();
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

    /// <summary>Host publishes; every client (including the host) shows it locally.</summary>
    internal static void Publish(string text, Player? human = null, Creature? enemy = null,
        IReadOnlyList<CardModel>? cards = null)
    {
        if (manager is null) return;
        if (text == lastText && Environment.TickCount64 - lastSentMs < 4000) return;
        lastText = text;
        lastSentMs = Environment.TickCount64;
        var keys = cards is null
            ? Array.Empty<string>()
            : cards.Take(2).Select(card => card.Id.Entry).ToArray();
        var actor = human?.NetId ?? 0;
        var target = enemy?.CombatId ?? 0;
        Log.Info($"CoopBots callout: {text}");
        BotCooperation.SetCallout(text, actor, target, keys);
        if (manager.NetService.Type == NetGameType.Host)
            manager.NetService.SendMessage(new BotCalloutMessage
            {
                Text = text,
                Actor = actor,
                Target = target,
                Cards = keys,
                Location = manager.RunLocationTargetedBuffer.CurrentLocation,
            });
    }

    private static void Receive(BotCalloutMessage message, ulong sender)
    {
        if (manager is null || sender == manager.NetService.NetId) return;
        BotCooperation.SetCallout(message.Text, message.Actor, message.Target, message.Cards);
    }

    internal static void Reset() => lastText = "";

    internal static void Clear() => Publish("");
}

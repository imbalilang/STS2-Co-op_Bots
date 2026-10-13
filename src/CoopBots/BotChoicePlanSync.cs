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
    public bool Potion;
    public uint PotionIndex;
    public uint? Target;
    public bool Ack, Clear;
    public RunLocation Location { get; set; }
    public bool ShouldBroadcast => false;
    public bool ShouldBuffer => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public void Serialize(PacketWriter w)
    {
        w.WriteInt(Sequence); w.WriteUInt(Action); w.WriteULong(Bot); w.WriteString(Card); w.WriteString(Payload);
        w.WriteBool(Potion); w.WriteUInt(PotionIndex); w.WriteBool(Target.HasValue);
        if (Target.HasValue) w.WriteUInt(Target.Value);
        w.WriteBool(Ack); w.WriteBool(Clear); w.Write(Location);
    }
    public void Deserialize(PacketReader r)
    {
        Sequence = r.ReadInt(); Action = r.ReadUInt(); Bot = r.ReadULong(); Card = r.ReadString(); Payload = r.ReadString();
        Potion = r.ReadBool(); PotionIndex = r.ReadUInt(); Target = r.ReadBool() ? r.ReadUInt() : null;
        Ack = r.ReadBool(); Clear = r.ReadBool(); Location = r.Read<RunLocation>();
    }
}

internal static class BotChoicePlanSync
{
    private static RunManager? manager;
    private static BotChoicePlanMessage? installed;
    private static KernelChoice[] choices = [];
    private static int index, sequence;
    private static bool choiceDiverged;
    private static TeamCombatPlanner.Decision? pending;
    private static KernelCombatPlanner.PotionPlan? pendingPotion;
    private static CombatState? combat;
    private static long revision, sent;
    private static string stamp = "";
    private static uint? focus;
    private static readonly HashSet<ulong> waiting = new();

    internal static void Initialize(RunManager instance)
    {
        manager = instance; installed = null; pending = null; pendingPotion = null; choices = []; index = sequence = 0; choiceDiverged = false; waiting.Clear();
        instance.RunLocationTargetedBuffer.RegisterMessageHandler<BotChoicePlanMessage>(Receive);
        BotTurnStartChoiceSync.Initialize(instance);
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
            installed = message; index = 0; choiceDiverged = false;
        }
        manager.NetService.SendMessage(new BotChoicePlanMessage { Ack = true, Sequence = message.Sequence, Location = message.Location });
    }
    internal static bool Prepare(TeamCombatPlanner.Decision decision, CombatState state)
    {
        if (decision.Move.Choices is not { Count: > 0 } plan || manager is null) return true;
        if (pending == decision && installed?.Action == manager.ActionQueueSet.NextActionId && waiting.Count == 0)
        { pending = null; return true; }
        Cancel();
        pending = decision;
        return Install(state, plan, new() { Bot = decision.Player.NetId, Card = decision.Move.Card.Id.Entry });
    }
    internal static bool Prepare(KernelCombatPlanner.PotionPlan decision, CombatState state)
    {
        if (decision.Choices is not { Count: > 0 } plan || manager is null) return true;
        if (pendingPotion == decision && installed?.Action == manager.ActionQueueSet.NextActionId && waiting.Count == 0)
        { pendingPotion = null; return true; }
        Cancel();
        if (decision.Potion.Owner is not { } owner) return true;
        var slot = owner.GetPotionSlotIndex(decision.Potion);
        if (slot < 0) return true; // The runtime's live-belt check rejects this before enqueue.
        var target = decision.Target;
        if (target is null && decision.Potion.IsValidTarget(owner.Creature)) target = owner.Creature;
        pendingPotion = decision;
        // v0.111.0 PotionModel.OnUseWrapper removes the bottle before its menu.
        // Capture its slot/source now; UsePotionAction keeps slot/owner/target,
        // while KernelChoice.Owner is the independently choosing recipient.
        return Install(state, plan, new() { Bot = owner.NetId, Card = decision.Potion.Id.Entry,
            Potion = true, PotionIndex = (uint)slot, Target = target?.CombatId });
    }
    private static bool Install(CombatState state, IReadOnlyList<KernelChoice> plan, BotChoicePlanMessage message)
    {
        combat = state; revision = KernelSession.LiveRevision;
        stamp = KernelSession.CaptureLiveStamp(state); focus = BotCooperation.FocusTarget;
        installed = message; installed.Sequence = ++sequence; installed.Action = manager!.ActionQueueSet.NextActionId;
        installed.Payload = JsonSerializer.Serialize(plan); installed.Location = manager.RunLocationTargetedBuffer.CurrentLocation;
        choices = plan.ToArray(); index = 0; choiceDiverged = false; waiting.Clear();
        if (manager.NetService is NetHostGameService host)
            foreach (var peer in host.ConnectedPeers.Where(p => p.readyForBroadcasting)) waiting.Add(peer.peerId);
        manager.NetService.SendMessage(installed); sent = Environment.TickCount64;
        if (waiting.Count == 0) { pending = null; pendingPotion = null; return true; }
        return false;
    }
    internal static bool Resume(CombatState state, out TeamCombatPlanner.Decision? decision)
        => Resume(state, out decision, out _);
    internal static bool Resume(CombatState state, out TeamCombatPlanner.Decision? decision,
        out KernelCombatPlanner.PotionPlan? potion)
    {
        decision = null; potion = null;
        if (pending is null && pendingPotion is null || manager is null) return false;
        if (!ReferenceEquals(state, combat) || revision != KernelSession.LiveRevision
            || installed?.Action != manager.ActionQueueSet.NextActionId || focus != BotCooperation.FocusTarget)
        { return AbandonPending(state, "planned potion changed before enqueue"); }
        if (manager.NetService is NetHostGameService host)
            waiting.IntersectWith(host.ConnectedPeers.Where(p => p.readyForBroadcasting).Select(p => p.peerId));
        if (waiting.Count == 0)
        {
            if (stamp != KernelSession.CaptureLiveStamp(state)) return AbandonPending(state, "planned potion live stamp changed before enqueue");
            decision = pending; potion = pendingPotion; return true;
        }
        if (Environment.TickCount64 - sent > 2000) { manager.NetService.SendMessage(installed!); sent = Environment.TickCount64; }
        return true;
    }
    private static bool AbandonPending(CombatState state, string detail)
    {
        var hadPotion = pendingPotion is not null;
        Cancel();
        if (hadPotion) BotRuntime.NotifyPlannedChoiceDivergence(state, detail);
        return false;
    }
    internal static void Cancel()
    {
        if (installed is not null && manager is not null)
            manager.NetService.SendMessage(new BotChoicePlanMessage { Sequence = installed.Sequence, Clear = true, Location = installed.Location });
        pending = null; pendingPotion = null; installed = null; choices = []; waiting.Clear(); index = 0; choiceDiverged = false;
    }
    internal static IReadOnlyList<CardModel>? Select(Player player, IReadOnlyList<CardModel> options, int min, int max)
        => SelectMenu(player, options, min, max, null);

    internal static IReadOnlyList<CardModel>? SelectMenu(Player player, IReadOnlyList<CardModel> options,
        int min, int max, string? source)
    {
        if (installed is null || manager?.ActionExecutor.CurrentlyRunningAction is not { } action) return null;
        if (installed.Potion)
        {
            if (action is not UsePotionAction potion || potion.Id != installed.Action || potion.OwnerId != installed.Bot) return null;
            if (potion.PotionIndex != installed.PotionIndex || potion.TargetId != installed.Target)
            {
                ChoiceDiverged(action, "changed-potion-action-binding", $"plannedSlot={installed.PotionIndex} actualSlot={potion.PotionIndex} "
                    + $"plannedTarget={installed.Target} actualTarget={potion.TargetId}");
                return null;
            }
        }
        else if (action is not PlayCardAction card || card.Id != installed.Action || card.OwnerId != installed.Bot
            || card.CardModelId.Entry != installed.Card) return null;
        if (index >= choices.Length)
        {
            ChoiceDiverged(action, "unexpected-choice", $"actualSource={source ?? "unknown"} actualBounds={min}..{max} "
                + $"actualOptions={JsonSerializer.Serialize(KernelChoice.MenuTokens(options))}");
            return null;
        }
        var choice = choices[index];
        if (installed.Potion && (choice.Options is null || choice.MenuSource is null
            || source != installed.Card || choice.Source != installed.Card || source != choice.MenuSource))
        {
            ChoiceDiverged(action, "changed-potion-choice-source", choice.MenuDifference(player.NetId, options, min, max, source));
            return null;
        }
        var result = choice.ResolveMenu(player.NetId, options, min, max, source);
        if (result is null)
        {
            // Invalidate the remaining transaction; the identical deterministic
            // fallback remains available on every peer when options changed.
            ChoiceDiverged(action, "changed-choice-options", choice.MenuDifference(player.NetId, options, min, max, source));
            return null;
        }
        index++;
        return result;
    }

    private static void ChoiceDiverged(GameAction action, string reason, string detail)
    {
        index = choices.Length;
        if (choiceDiverged) return;
        choiceDiverged = true;
        Log.Warn($"CoopBots planned choice diverged: {reason}; using deterministic fallback. {detail}");
        var player = action is PlayCardAction card ? card.Player : (action as UsePotionAction)?.Player;
        BotRuntime.NotifyPlannedChoiceDivergence(player?.Creature.CombatState as CombatState,
            $"reason={reason} {(installed!.Potion ? "potion" : "card")}={installed.Card} action={installed.Action} {detail}");
    }
}

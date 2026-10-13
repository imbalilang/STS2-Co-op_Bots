using System.Text.Json;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

public sealed class BotTurnStartChoiceMessage : INetMessage, IRunLocationTargetedMessage
{
    public int Sequence;
    public uint Action;
    public ulong Owner;
    public string Payload = "";
    public string ActionBinding = "", Failure = "";
    public bool External;
    public bool Ack, Clear;
    public RunLocation Location { get; set; }
    public bool ShouldBroadcast => false;
    public bool ShouldBuffer => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Sequence); writer.WriteUInt(Action); writer.WriteULong(Owner);
        writer.WriteString(Payload); writer.WriteString(ActionBinding); writer.WriteString(Failure);
        writer.WriteBool(External); writer.WriteBool(Ack); writer.WriteBool(Clear); writer.Write(Location);
    }
    public void Deserialize(PacketReader reader)
    {
        Sequence = reader.ReadInt(); Action = reader.ReadUInt(); Owner = reader.ReadULong();
        Payload = reader.ReadString(); ActionBinding = reader.ReadString(); Failure = reader.ReadString();
        External = reader.ReadBool(); Ack = reader.ReadBool(); Clear = reader.ReadBool(); Location = reader.Read<RunLocation>();
    }
}

// Upstream answers a local turn-start menu again. Our multiplayer plans carry
// the answer actually used by the bounded search, including its complete menu.
// An expired search must not predict one discard while the native selector
// independently computes another (F31 A10 diagnostic, owner request 2026-10-12).
// This lease is separate from a card/potion's immediate choice transaction:
// native setup can run after the action executor has moved on to the enemy side.
internal static class BotTurnStartChoiceSync
{
    private static RunManager? manager;
    private static BotTurnStartChoiceMessage? installed;
    private static KernelTurnStartChoiceContract? contract;
    private static CombatState? combat;
    private static GameAction? pending;
    private static Action? pendingEnqueue;
    private static long revision, inputRevision, sent;
    private static long preparedAt;
    private static string stamp = "";
    private static int sequence, index;
    private static bool armed, active;
    private static GameAction? boundAction;
    private static int receivedSequence;
    private sealed record RetiredLease(BotTurnStartChoiceMessage Message, CombatState Combat);
    private static readonly Queue<RetiredLease> retired = new();
    private static readonly HashSet<ulong> waiting = new();

    internal static void Initialize(RunManager instance)
    {
        if (manager is not null)
        {
            manager.ActionQueueSet.ActionEnqueued -= ActionEnqueued;
            manager.RunLocationTargetedBuffer.UnregisterMessageHandler<BotTurnStartChoiceMessage>(Receive);
        }
        CombatManager.Instance.TurnStarted -= CompletePlayerTurn;
        Reset(); manager = instance; receivedSequence = 0;
        instance.RunLocationTargetedBuffer.RegisterMessageHandler<BotTurnStartChoiceMessage>(Receive);
        instance.ActionQueueSet.ActionEnqueued += ActionEnqueued;
        CombatManager.Instance.TurnStarted += CompletePlayerTurn;
    }

    private static void Receive(BotTurnStartChoiceMessage message, ulong sender)
    {
        if (manager is null || !message.Location.Equals(manager.RunLocationTargetedBuffer.CurrentLocation)) return;
        if (manager.NetService.Type == NetGameType.Host)
        {
            if (message.Ack && installed?.Sequence == message.Sequence && installed.Action == message.Action
                && installed.Owner == message.Owner && installed.ActionBinding == message.ActionBinding) waiting.Remove(sender);
            if (message.Clear && message.Failure.Length > 0 && manager.NetService is NetHostGameService host
                && host.ConnectedPeers.Any(peer => peer.peerId == sender))
            {
                var prior = installed is not null && combat is not null ? new RetiredLease(installed, combat) : null;
                prior = new[] { prior }.Concat(retired).FirstOrDefault(lease => lease is not null
                    && lease.Message.Sequence == message.Sequence && lease.Message.Action == message.Action
                    && lease.Message.Owner == message.Owner && lease.Message.ActionBinding == message.ActionBinding);
                if (prior is not null)
                {
                    Cancel();
                    if (message.External) BotRuntime.NotifyFutureChoiceExternalInput(prior.Combat, message.Failure);
                    else
                    {
                        Log.Warn($"CoopBots planned choice diverged: turn-start/peer-rejection peer={sender} {message.Failure}");
                        BotRuntime.NotifyPlannedChoiceDivergence(prior.Combat, message.Failure);
                    }
                }
            }
            return;
        }
        if (message.Ack || manager.NetService is not NetClientGameService client || sender != client.HostNetId) return;
        if (message.Clear)
        {
            if (installed?.Sequence == message.Sequence) ClearLocal();
            return;
        }
        if (message.Sequence < receivedSequence) return;
        if (message.Sequence == receivedSequence && installed?.Sequence != message.Sequence)
        {
            manager.NetService.SendMessage(Acknowledge(message));
            return;
        }
        if (installed is not null && installed.Sequence > message.Sequence) return;
        if (installed?.Sequence != message.Sequence)
        {
            var state = manager.DebugOnlyGetState()?.Players.FirstOrDefault()?.Creature.CombatState as CombatState;
            KernelTurnStartChoiceContract? received;
            try
            {
                received = JsonSerializer.Deserialize<KernelTurnStartChoiceContract>(message.Payload);
                if (state is null || received is null) throw new InvalidOperationException("native combat is unavailable");
                InstallLocal(state, received, message.Owner, message.Action);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
            {
                manager.NetService.SendMessage(new BotTurnStartChoiceMessage {
                    Sequence = message.Sequence, Action = message.Action, Owner = message.Owner,
                    ActionBinding = message.ActionBinding, Location = message.Location,
                    Clear = true, Failure = "turn-start contract rejected before enqueue: " + error.Message });
                return;
            }
            installed = message;
            receivedSequence = message.Sequence;
        }
        manager.NetService.SendMessage(Acknowledge(message));
    }

    private static BotTurnStartChoiceMessage Acknowledge(BotTurnStartChoiceMessage message) => new() {
        Ack = true, Sequence = message.Sequence, Action = message.Action, Owner = message.Owner,
        ActionBinding = message.ActionBinding, Location = message.Location };

    internal static bool Prepare(CombatState state, GameAction action, KernelTurnStartChoiceContract? choices,
        Action? nativeEnqueue = null)
    {
        if (choices is null || choices.OwnersTurnNumbers.Count == 0) return true;
        if (manager is null) throw new InvalidOperationException("Turn-start choice synchronizer is not initialized.");
        Cancel();
        InstallLocal(state, choices, action.OwnerId, manager.ActionQueueSet.NextActionId);
        installed!.Sequence = ++sequence;
        installed.ActionBinding = ActionKey(action);
        installed.Location = manager.RunLocationTargetedBuffer.CurrentLocation;
        installed.Payload = JsonSerializer.Serialize(choices);
        revision = KernelSession.LiveRevision; inputRevision = KernelInputObserver.Revision;
        stamp = KernelSession.CaptureLiveStamp(state); pending = action; pendingEnqueue = nativeEnqueue;
        if (manager.NetService is NetHostGameService host)
            foreach (var peer in host.ConnectedPeers.Where(p => p.readyForBroadcasting)) waiting.Add(peer.peerId);
        manager.NetService.SendMessage(installed); sent = preparedAt = Environment.TickCount64;
        if (waiting.Count != 0) return false;
        pending = null; pendingEnqueue = null;
        return true;
    }

    // Shared host/receiver installation, also driven by the real-menu regression.
    internal static void InstallLocal(CombatState state, KernelTurnStartChoiceContract choices,
        ulong endingOwner, uint actionId)
    {
        if (!choices.Complete || state.RoundNumber != choices.FromRound)
            throw new InvalidOperationException("Incomplete or stale turn-start choice contract.");
        ClearLocal();
        combat = state; contract = choices;
        installed = new() { Owner = endingOwner, Action = actionId };
        inputRevision = KernelInputObserver.Revision;
    }

    internal static bool Resume(CombatState state, out GameAction? action, out Action? nativeEnqueue)
    {
        action = null; nativeEnqueue = null;
        if (pending is null || manager is null) return false;
        if (!ReferenceEquals(state, combat) || revision != KernelSession.LiveRevision
            || inputRevision != KernelInputObserver.Revision || installed?.Action != manager.ActionQueueSet.NextActionId
            || stamp != KernelSession.CaptureLiveStamp(state))
        {
            ExternalInput("board changed before turn-start contract enqueue");
            return false;
        }
        if (manager.NetService is NetHostGameService host)
            waiting.IntersectWith(host.ConnectedPeers.Where(p => p.readyForBroadcasting).Select(p => p.peerId));
        if (waiting.Count == 0)
        { action = pending; nativeEnqueue = pendingEnqueue; pending = null; pendingEnqueue = null; return true; }
        if (Environment.TickCount64 - preparedAt > 10000)
        {
            // The native action can still settle using its ordinary policy,
            // but no later step may rely on the unacknowledged future hand.
            action = pending; nativeEnqueue = pendingEnqueue;
            Diverged("turn-start-peer-ack-timeout", $"waiting=[{string.Join(',', waiting)}]");
            return true;
        }
        if (Environment.TickCount64 - sent > 2000)
        { manager.NetService.SendMessage(installed!); sent = Environment.TickCount64; }
        return true;
    }

    private static void ActionEnqueued(GameAction action)
    {
        if (installed is null) return;
        if (action.Id == installed.Action && action.OwnerId == installed.Owner)
        {
            boundAction = action;
            action.BeforeExecuted += BeforeExecuted; action.BeforeCancelled += BeforeCancelled;
            action.AfterFinished += AfterFinished;
            return;
        }
        if (!AutoPilot.Drives(action.OwnerId)) ExternalInput("human action enqueued before planned turn-start choices");
    }

    private static string ActionKey(GameAction action) => action switch {
        PlayCardAction card => $"card:{card.CardModelId.Entry}",
        UsePotionAction potion => $"potion:{potion.PotionIndex}:{potion.TargetId}",
        EndPlayerTurnAction ending => $"end:{((NetEndPlayerTurnAction)ending.ToNetAction()).turnNumber}",
        _ => action.GetType().FullName ?? action.GetType().Name,
    };
    private static void BeforeExecuted(GameAction action)
    {
        if (installed is null || contract is null || combat is null) return;
        if (!ReferenceEquals(action, boundAction) || action.Id != installed.Action || action.OwnerId != installed.Owner
            || installed.ActionBinding != ActionKey(action) || combat.RoundNumber != contract.FromRound
            || action is EndPlayerTurnAction && contract.OwnersTurnNumbers.TryGetValue(action.OwnerId, out var nextTurn)
                && combat.Players.First(p => p.NetId == action.OwnerId).PlayerCombatState?.TurnNumber != nextTurn - 1)
        { Diverged("changed-causal-action", $"action={action.Id}/{ActionKey(action)} round={combat.RoundNumber}"); return; }
        armed = true;
    }
    private static void BeforeCancelled(GameAction action)
    {
        if (ReferenceEquals(action, boundAction)) ExternalInput("causal action cancelled before native turn-start");
    }
    private static void AfterFinished(GameAction action)
    {
        action.BeforeExecuted -= BeforeExecuted; action.BeforeCancelled -= BeforeCancelled; action.AfterFinished -= AfterFinished;
        if (ReferenceEquals(action, boundAction)) boundAction = null;
    }

    internal static void MarkEnqueued(Player endingPlayer, uint actionId)
    {
        if (installed?.Owner == endingPlayer.NetId && installed.Action == actionId) armed = true;
    }

    internal static void BeginPlayerTurn(ICombatState state, CombatSide side, IReadOnlyList<Creature> participants)
    {
        if (side != CombatSide.Player || !armed || contract is null || !ReferenceEquals(state, combat)) return;
        if (combat!.RoundNumber != contract.ToRound || inputRevision != KernelInputObserver.Revision
            || contract.OwnersTurnNumbers.Any(pair => combat.Players.FirstOrDefault(p => p.NetId == pair.Key)
                is not { } player || player.PlayerCombatState?.TurnNumber != pair.Value)
            || !contract.OwnersTurnNumbers.Keys.Order().SequenceEqual(
                participants.Where(c => c.Player is not null).Select(c => c.Player!.NetId).Order()))
        { Diverged("changed-turn-start-boundary", $"actualRound={combat.RoundNumber} plannedRound={contract.ToRound}"); return; }
        if (contract.Choices.Any(choice => !AutoPilot.Drives(choice.Owner)))
        { ExternalInput("turn-start plan includes an undelegated human menu"); return; }
        active = true;
        Log.Info($"CoopBots turn-start plan: activated round={contract.ToRound} choices={contract.Choices.Count}");
    }

    internal static IReadOnlyList<CardModel>? SelectMenu(Player player, IReadOnlyList<CardModel> options,
        int min, int max, string? source)
    {
        var activeCombat = combat;
        if (!active || contract is null || activeCombat is null
            || !ReferenceEquals(player.Creature.CombatState, activeCombat)) return null;
        if (index >= contract.Choices.Count)
        { Diverged("unexpected-turn-start-choice", $"owner={player.NetId} source={source} options={options.Count}"); return null; }
        var choice = contract.Choices[index];
        // Kernel specs cap counts to the available menu. Native Tools still
        // requests its power amount and GamblingChip requests 999999999; both
        // have the same effective cardinality. Requested bounds are diagnostic.
        var result = choice.Options is not null && choice.Minimum == Math.Min(min, options.Count)
            && choice.Maximum == Math.Min(max, options.Count)
            && choice.NativeRound == activeCombat.RoundNumber
            && choice.PlayerTurnNumber == player.PlayerCombatState?.TurnNumber
            && choice.Timing == "PlayerTurnStart" && source == (choice.MenuSource ?? choice.Source)
                ? choice.ResolveMenu(player.NetId, options, min, max, source) : null;
        if (result is null)
        { Diverged("changed-turn-start-choice-menu", choice.MenuDifference(player.NetId, options, min, max, source)); return null; }
        index++;
        Log.Info($"CoopBots turn-start choice: bot={player.NetId} source={source} effect={choice.Effect} "
            + $"selected={result.Count}/{options.Count} cards=[{string.Join(',', result.Select(card => card.Id.Entry))}] "
            + $"comparison=[planned-native-menu round={activeCombat.RoundNumber} choice={index}/{contract.Choices.Count}]");
        return result;
    }

    internal static void CompletePlayerTurn(CombatState state)
    {
        if (!active || contract is null || !ReferenceEquals(state, combat)) return;
        if (index != contract.Choices.Count)
        { Diverged("unconsumed-turn-start-choice", $"consumed={index} expected={contract.Choices.Count}"); return; }
        Log.Info($"CoopBots turn-start plan: consumed round={contract.ToRound} choices={index}");
        if (installed is not null)
        {
            retired.Enqueue(new(installed, state));
            while (retired.Count > 8) retired.Dequeue();
        }
        // Each peer retires after its own native setup completes. A host Clear
        // packet here could arrive before a slower peer has opened its menu.
        ClearLocal();
    }

    internal static void ExternalChoice(Player player)
    {
        if (active && ReferenceEquals(player.Creature.CombatState, combat))
            ExternalInput("undelegated human opened a turn-start menu");
    }

    private static void ExternalInput(string detail)
    {
        var state = combat;
        ReportToHost(detail, external: true);
        Cancel();
        BotRuntime.NotifyFutureChoiceExternalInput(state, detail);
    }

    private static void Diverged(string reason, string detail)
    {
        var state = combat;
        Log.Warn($"CoopBots planned choice diverged: turn-start/{reason}; using deterministic fallback. {detail}");
        ReportToHost($"turn-start/{reason} {detail}", external: false);
        Cancel();
        BotRuntime.NotifyPlannedChoiceDivergence(state, $"turn-start/{reason} {detail}");
    }

    private static void ReportToHost(string detail, bool external)
    {
        if (installed is not null && manager?.NetService is NetClientGameService)
            manager.NetService.SendMessage(new BotTurnStartChoiceMessage {
                Sequence = installed.Sequence, Action = installed.Action, Owner = installed.Owner,
                ActionBinding = installed.ActionBinding, Location = installed.Location,
                Clear = true, Failure = detail, External = external });
    }

    internal static void Cancel()
    {
        if (installed is not null && manager is not null)
            manager.NetService.SendMessage(new BotTurnStartChoiceMessage {
                Sequence = installed.Sequence, Clear = true, Location = installed.Location });
        ClearLocal();
    }
    internal static void Reset() { Cancel(); retired.Clear(); }
    private static void ClearLocal()
    {
        if (boundAction is not null)
        {
            boundAction.BeforeExecuted -= BeforeExecuted; boundAction.BeforeCancelled -= BeforeCancelled;
            boundAction.AfterFinished -= AfterFinished; boundAction = null;
        }
        installed = null; contract = null; combat = null; pending = null; pendingEnqueue = null;
        waiting.Clear(); index = 0; armed = active = false;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeSideTurnStart))]
internal static class BotTurnStartChoiceBoundaryPatch
{
    private static void Prefix(ICombatState __0, CombatSide __1, IReadOnlyList<Creature> __2)
        => BotTurnStartChoiceSync.BeginPlayerTurn(__0, __1, __2);
}

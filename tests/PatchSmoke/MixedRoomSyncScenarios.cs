using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CoopBots;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// R2 actual 2026-10-24: production mutant build0 / RED1 / exact restore / GREEN0.
// Receipt: work/mixed-fixes-20261023T164339Z/r2-valid-proofs.json
// treasure-broadcast: System.Exception: Mixed room sync: settled treasure entry must grant once and publish its native location
// treasure-authority: System.Exception: Mixed room sync: client accepted a synthetic chest command from a non-host
// treasure-ack: System.Exception: Mixed room sync: host released departure before chest ACK
// treasure-act: System.Exception: Mixed room sync: same-coordinate treasure in a new act was skipped
// local-event-broadcast: System.Exception: Mixed room sync: local autopiloted human event must broadcast the native choice message
// treasure-reconnect: System.Exception: Mixed room sync: rejoined post-reward snapshot must ACK without paying or consuming RNG again
// treasure-before-binding: System.Exception: Mixed room sync: unmatched native reward state must reject the command and hold departure
// treasure-after-binding: System.Exception: Mixed room sync: native reward result must match the host after state before ACK
// treasure-cleanup: System.Exception: Mixed room sync: native run cleanup must release the treasure transaction immediately
// treasure-room-entry: System.Exception: Mixed room sync: treasure entry must wait for native room and message-buffer locations to agree
// treasure-native-location: System.Exception: Mixed room sync: old-buffer treasure command must not run in the new native room
// treasure-complete-rng: System.Exception: Mixed room sync: reward binding must include non-reward player RNG streams
// treasure-spoils-state: System.Exception: Mixed room sync: reward binding must include Spoils Map quest count
// treasure-late-ack: System.Exception: Mixed room sync: disposed native reward must not send a late ACK into the next run
internal static class MixedRoomSyncScenarios
{
        // Each fault must change production code; the native rewards/event bodies
    // and packet serialization below remain real, including reward RNG.
    private static void Check(bool ok, string why)
    { if (!ok) throw new Exception("Mixed room sync: " + why); }

    internal static void Run()
    {
        TestEnvironment.Ensure();
        TreasureRoomEntry();
        TreasurePeers();
        ReconnectAndCleanup();
        LocalHumanEvent();
    }

    private static BotTreasureRewardMessage Packet(BotTreasureRewardMessage source)
    {
        var writer = new PacketWriter(); source.Serialize(writer);
        var reader = new PacketReader(); reader.Reset(writer.Buffer);
        var result = new BotTreasureRewardMessage(); result.Deserialize(reader); return result;
    }

    private sealed class TreasurePeer : IDisposable
    {
        internal readonly RunState State;
        internal readonly Player Human, Bot;
        internal readonly OneOffSynchronizer Native;
        internal RunLocation Location;
        internal readonly List<BotTreasureRewardMessage> Sent = new();
        internal readonly BotTreasureRewardSession Session;
        internal long Time;
        internal int NativeCalls;
        internal TreasurePeer(bool host, Player? loaded = null)
        {
            var snapshot = loaded?.ToSerializable();
            Human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
            Bot = snapshot is null
                ? Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 96, 0))
                : Player.FromSerializable(snapshot);
            State = RunState.CreateForTest(new[] { Human, Bot }, seed: "MIXED-CHEST-PEERS");
            // CreateForTest initializes fresh RNG; a native reconnect restores
            // the serialized player RNG, so put that imported state back here.
            if (snapshot is not null) Bot.PlayerRng.LoadFromSerializable(snapshot.Rng);
            State.PushRoom(new TreasureRoom(0));
            Location = State.RunLocation;
            var service = DispatchProxy.Create<INetHostGameService, ReconnectNetProxy>();
            ((ReconnectNetProxy)(object)service).Kind = host ? NetGameType.Host : NetGameType.Client;
            Native = new OneOffSynchronizer(new RunLocationTargetedMessageBuffer(service), service, State, Human.NetId);
            Session = new BotTreasureRewardSession(() => State, () => Location, () => host,
                () => host ? null : 1UL, () => host ? new[] { 2UL } : Array.Empty<ulong>(),
                message => Sent.Add(Packet(message)), async player =>
                {
                    NativeCalls++;
                    return await BotTreasureRewardDriver.GrantNative(Native, player);
                }, () => Time);
        }
        internal string Rng() => JsonSerializer.Serialize(Bot.PlayerRng.ToSerializable(), new JsonSerializerOptions { IncludeFields = true });
        internal void NextAct()
        {
            State.PopCurrentRoom(); State.CurrentActIndex = 1; State.PushRoom(new TreasureRoom(1));
            Location = State.RunLocation;
        }
        public void Dispose() { Session.Dispose(); Native.Dispose(); }
    }

    private static void TreasureRoomEntry()
    {
        using var host = new TreasurePeer(true);
        using var client = new TreasurePeer(false);
        var nativeLocation = host.State.RunLocation;
        var staleLocation = new RunLocation(0, null, 99);
        host.Location = staleLocation;
        Check(host.Session.Tick() && host.NativeCalls == 0 && host.Sent.Count == 0,
            "treasure entry must wait for native room and message-buffer locations to agree");
        host.Location = nativeLocation;
        Check(host.Session.Tick() && host.NativeCalls == 1 && host.Sent.Count == 1,
            "settled treasure entry must grant once and publish its native location");
        var command = host.Sent.Single();
        client.Location = staleLocation;
        var staleCommand = Packet(command); staleCommand.Location = staleLocation;
        client.Session.Receive(staleCommand, 1);
        Check(client.NativeCalls == 0 && client.Sent.Count == 0,
            "old-buffer treasure command must not run in the new native room");
        client.Session.Receive(command, 1);
        Check(client.NativeCalls == 0, "new-room treasure command must wait for its native buffer location");
        client.Location = client.State.RunLocation;
        client.Session.Receive(command, 1); client.Session.Tick();
        host.Session.Receive(client.Sent.Single(), 2);
        Check(!host.Session.Tick() && host.NativeCalls == 1 && client.NativeCalls == 1
            && host.Bot.Gold == client.Bot.Gold && host.Rng() == client.Rng(),
            "buffer catch-up must finish one synchronized treasure reward without repaying the host");
        Console.WriteLine("PASS: treasure entry waits for native and buffer room locations, rejects stale-room commands and pays each peer once after entry settles.");
    }

    private static void TreasurePeers()
    {
        using var host = new TreasurePeer(true);
        using var client = new TreasurePeer(false);
        var hostHumanGold = host.Human.Gold; var clientHumanGold = client.Human.Gold;
        var before = client.Rng(); var initialGold = client.Bot.Gold;
        Check(!client.Session.Tick(), "client generated its own chest command");
        Check(host.Session.Tick() && host.Sent.Count == 1,
            "host must send a room-bound synthetic chest command");
        var command = host.Sent.Single();
        Check(command.Bot == host.Bot.NetId && !command.Ack && command.Room == BotTreasureRewardSession.RoomIdentity(host.State)
            && command.Location.Equals(host.Location), "chest packet lost its player or room binding");
        Check(RoomWork.Pending(host.State.CurrentRoom), "host must hold departure until remote chest ACK");

        client.Session.Receive(command, 77);
        client.Session.Tick();
        Check(client.NativeCalls == 0 && client.Bot.Gold == initialGold && client.Rng() == before,
            "client accepted a synthetic chest command from a non-host");
        var wrongRoom = Packet(command); wrongRoom.Location = new RunLocation(0, null, 9);
        client.Session.Receive(wrongRoom, 1); client.Session.Tick();
        Check(client.NativeCalls == 0, "client accepted a chest command for another room location");

        client.Session.Receive(command, 1);
        Check(RoomWork.Pending(client.State.CurrentRoom), "client must hold departure while applying chest reward");
        client.Session.Tick();
        Check(client.NativeCalls == 1 && host.NativeCalls == 1 && client.Bot.Gold > initialGold
            && host.Bot.Gold == client.Bot.Gold && host.Rng() == client.Rng(),
            "both peers must execute the native chest reward exactly once with identical gold and RNG");
        Check(client.Sent.Count == 1 && client.Sent[0].Ack && client.Sent[0].Success,
            "client must ACK its completed native chest reward");
        Check(host.Session.Tick(), "host released departure before chest ACK");

        var receipt = client.Sent[0];
        host.Session.Receive(receipt, 77);
        Check(host.Session.Tick(), "unknown peer ACK released chest departure");
        var wrongBot = Packet(receipt); wrongBot.Bot = host.Human.NetId;
        host.Session.Receive(wrongBot, 2);
        Check(host.Session.Tick(), "ACK for another player released chest departure");

        // Drop the first ACK and force the actual production retry path.
        var paid = client.Bot.Gold; var consumed = client.Rng();
        host.Time = 2500; host.Session.Tick();
        Check(host.Sent.Count == 2, "missing ACK must resend the chest command");
        client.Session.Receive(host.Sent[1], 1); client.Session.Tick();
        Check(client.Sent.Count == 2 && client.NativeCalls == 1 && client.Bot.Gold == paid && client.Rng() == consumed,
            "retried chest command must resend ACK without paying or consuming RNG twice");
        host.Session.Receive(client.Sent[1], 2);
        Check(!host.Session.Tick() && !RoomWork.Pending(host.State.CurrentRoom),
            "matching chest ACK must release host departure");
        Check(!host.Session.Tick() && host.NativeCalls == 1, "repeated host ticks repaid the same chest");
        Check(host.Human.Gold == hostHumanGold && client.Human.Gold == clientHumanGold,
            "synthetic chest driver paid a human seat");

        // The map coordinate is deliberately the same (null in this detached
        // native fixture), so only the new act/location identifies a fresh room.
        host.NextAct(); client.NextAct();
        client.Session.Receive(command, 1); client.Session.Tick();
        Check(client.NativeCalls == 1, "old-act chest command was applied in a new act");
        Check(host.Session.Tick() && host.Sent.Count == 3, "same-coordinate treasure in a new act was skipped");
        client.Session.Receive(host.Sent[2], 1); client.Session.Tick();
        host.Session.Receive(client.Sent[^1], 2);
        Check(!host.Session.Tick() && host.NativeCalls == 2 && client.NativeCalls == 2
            && host.Bot.Gold == client.Bot.Gold && host.Rng() == client.Rng(),
            "new-act chest must pay once per peer and keep reward RNG synchronized");
        Console.WriteLine("PASS: synthetic treasure executes native rewards on two peers, authenticates host/ACK/player/room, retries idempotently and pays repeated coordinates across acts.");
    }

    private static void ReconnectAndCleanup()
    {
        using var pump = new ConstructionParallelScenarios.Pump();
        using var host = new TreasurePeer(true);
        host.Session.Tick();
        var command = host.Sent.Single();
        Check(command.Before.Length > 0 && command.After == BotTreasureRewardSession.RewardState(host.Bot)
            && command.Before != command.After, "chest command must carry its native before and after states");
        using (var joined = new TreasurePeer(false, host.Bot))
        {
            var imported = BotTreasureRewardSession.RewardState(joined.Bot);
            joined.Session.Receive(command, 1);
            Check(!joined.Session.Tick() && joined.NativeCalls == 0 && joined.Sent.Count == 1
                && joined.Sent[0].Ack && joined.Sent[0].Success
                && BotTreasureRewardSession.RewardState(joined.Bot) == imported,
                "rejoined post-reward snapshot must ACK without paying or consuming RNG again");
            joined.Session.Receive(command, 1);
            Check(joined.NativeCalls == 0 && joined.Sent.Count == 2 && joined.Sent[^1].Success,
                "rejoined snapshot retry must retain its completed receipt");
        }
        using (var mismatch = new TreasurePeer(false))
        {
            mismatch.Bot.Gold++;
            mismatch.Session.Receive(command, 1);
            Check(mismatch.NativeCalls == 0 && mismatch.Session.Tick()
                && mismatch.Sent.Count == 1 && !mismatch.Sent[0].Success
                && RoomWork.Pending(mismatch.State.CurrentRoom),
                "unmatched native reward state must reject the command and hold departure");
        }
        using (var badResult = new TreasurePeer(false))
        {
            var forged = Packet(command); forged.After += ";wrong-result";
            badResult.Session.Receive(forged, 1);
            Check(badResult.NativeCalls == 1 && badResult.Session.Tick()
                && badResult.Sent.Count == 1 && !badResult.Sent[0].Success,
                "native reward result must match the host after state before ACK");
        }
        using (var semantic = new TreasurePeer(false))
        {
            var initial = BotTreasureRewardSession.RewardState(semantic.Bot);
            semantic.Bot.PlayerRng.Transformations.NextInt(99);
            Check(BotTreasureRewardSession.RewardState(semantic.Bot) != initial,
                "reward binding must include non-reward player RNG streams");
            initial = BotTreasureRewardSession.RewardState(semantic.Bot);
            semantic.Bot.Deck.AddInternal(ConstructionParallelScenarios.Card("SPOILS_MAP", semantic.Bot));
            Check(BotTreasureRewardSession.RewardState(semantic.Bot) != initial,
                "reward binding must include Spoils Map quest count");
        }

        using var fresh = new TreasurePeer(false);
        var delayed = new TaskCompletionSource<int>();
        var lateMessages = new List<BotTreasureRewardMessage>();
        using var late = new BotTreasureRewardSession(() => fresh.State, () => fresh.Location,
            () => false, () => 1UL, () => Array.Empty<ulong>(), lateMessages.Add, _ => delayed.Task);
        late.Receive(command, 1);
        var running = (Task<bool>)AccessTools.Field(typeof(BotTreasureRewardSession), "running").GetValue(late)!;
        Check(RoomWork.Pending(fresh.State.CurrentRoom), "pending native reward must own its departure lease");
        var patch = typeof(BotTreasureRewardCleanUpPatch);
        Check(patch.GetCustomAttributes<HarmonyPatch>().Any(a => a.info.declaringType == typeof(RunManager)
            && a.info.methodName == nameof(RunManager.CleanUp)), "treasure cleanup must target native RunManager.CleanUp");
        var managerField = AccessTools.Field(typeof(BotTreasureRewardDriver), "manager");
        var sessionField = AccessTools.Field(typeof(BotTreasureRewardDriver), "session");
        var bufferField = AccessTools.Field(typeof(BotTreasureRewardDriver), "buffer");
        var previousManager = managerField.GetValue(null);
        var previousSession = sessionField.GetValue(null);
        var previousBuffer = bufferField.GetValue(null);
        try
        {
            var manager = (RunManager)RuntimeHelpers.GetUninitializedObject(typeof(RunManager));
            managerField.SetValue(null, manager); sessionField.SetValue(null, late); bufferField.SetValue(null, null);
            AccessTools.Method(patch, "Prefix").Invoke(null, new object[] { manager });
            Check(!RoomWork.Pending(fresh.State.CurrentRoom) && sessionField.GetValue(null) is null,
                "native run cleanup must release the treasure transaction immediately");
        }
        finally
        {
            managerField.SetValue(null, previousManager); sessionField.SetValue(null, previousSession);
            bufferField.SetValue(null, previousBuffer);
        }
        delayed.SetException(new InvalidOperationException("deliberate late native failure after run cleanup"));
        pump.Run(async () => { await running; return true; });
        Check(lateMessages.Count == 0 && !late.Tick() && !RoomWork.Pending(fresh.State.CurrentRoom),
            "disposed native reward must not send a late ACK into the next run");
        Console.WriteLine("PASS: reconnect imports native post-reward state without reapplying, complete RNG/quest state binds rewards, mismatches hold departure and native cleanup suppresses late ACKs.");
    }

    private static void LocalHumanEvent()
    {
        using var pump = new ConstructionParallelScenarios.Pump();
        var players = new[] {
            Player.CreateForNewRun<Deprived>(UnlockState.all, 1),
            Player.CreateForNewRun<Deprived>(UnlockState.all, 2),
            Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 96, 1)) };
        var hostRun = RunState.CreateForTest(players, seed: "MIXED-EVENT-PEERS");
        var clones = players.Select(p => Player.CreateForNewRun<Deprived>(UnlockState.all, p.NetId)).ToArray();
        var clientRun = RunState.CreateForTest(clones, seed: "MIXED-EVENT-PEERS");
        var hostNet = DispatchProxy.Create<INetHostGameService, ReconnectNetProxy>();
        var clientNet = DispatchProxy.Create<INetHostGameService, ReconnectNetProxy>();
        ((ReconnectNetProxy)(object)clientNet).Kind = NetGameType.Client;
        using var host = new EventSynchronizer(new RunLocationTargetedMessageBuffer(hostNet), hostNet, hostRun, hostRun, 1, 4);
        using var client = new EventSynchronizer(new RunLocationTargetedMessageBuffer(clientNet), clientNet, clientRun, clientRun, 2, 4);
        var hostEffects = new int[3]; var clientEffects = new int[3];
        FillEvents(host, hostEffects); FillEvents(client, clientEffects);
        AutoPilot.Set(1, true);
        try
        {
            var tick = AccessTools.Method(typeof(BotEventDriver), "TickEvents");
            Check(PatchProcessor.GetOriginalInstructions(tick).Any(i => i.operand is MethodInfo called
                && called.DeclaringType == typeof(BotEventDriver) && called.Name == "ChooseNonSharedOption"),
                "event tick must use the local-human broadcast path");
            BotEventDriver.ChooseNonSharedOption(host, players[0], 0);
            var sent = ((ReconnectNetProxy)(object)hostNet).Sent;
            Check(sent.Count == 1 && sent[0].Message is OptionIndexChosenMessage,
                "local autopiloted human event must broadcast the native choice message");
            var packet = (OptionIndexChosenMessage)sent[0].Message;
            AccessTools.Method(typeof(EventSynchronizer), "HandleEventOptionChosenMessage")
                .Invoke(client, new object[] { packet, 1UL });
            pump.Run(async () => { await host.AwaitPendingOptionTasks(); await client.AwaitPendingOptionTasks(); return true; });
            Check(hostEffects[0] == 1 && clientEffects[0] == 1 && hostEffects[1] == 0 && clientEffects[1] == 0,
                "native local-human choice must execute for the same human on both peers");
            BotEventDriver.ChooseNonSharedOption(host, players[2], 0);
            BotEventDriver.ChooseNonSharedOption(client, clones[2], 0);
            pump.Run(async () => { await host.AwaitPendingOptionTasks(); await client.AwaitPendingOptionTasks(); return true; });
            Check(hostEffects[2] == 1 && clientEffects[2] == 1 && sent.Count == 1
                && ((ReconnectNetProxy)(object)clientNet).Sent.Count == 0,
                "synthetic event replicas must execute once on each peer without impersonating the host");
            Console.WriteLine("PASS: local handed-over human event uses native broadcast and remote execution; synthetic event replicas remain independent.");
        }
        finally { AutoPilot.Set(1, false); BotEventDriver.Reset(); }
    }

    private static void FillEvents(EventSynchronizer synchronizer, int[] effects)
    {
        var canonical = ModelDb.Event<Reflections>();
        Check(!canonical.IsShared, "native event fixture must be non-shared");
        AccessTools.Field(typeof(EventSynchronizer), "_canonicalEvent").SetValue(synchronizer, canonical);
        var events = (List<EventModel>)AccessTools.Field(typeof(EventSynchronizer), "_events").GetValue(synchronizer)!;
        for (var seat = 0; seat < effects.Length; seat++)
        {
            var captured = seat;
            var model = canonical.ToMutable();
            var option = new EventOption(model, () => { effects[captured]++; return Task.CompletedTask; },
                new LocString("events", "test.title"), new LocString("events", "test.description"),
                "REFLECTIONS.pages.TEST.options.CONTINUE", Array.Empty<IHoverTip>());
            AccessTools.Field(typeof(EventModel), "_currentOptions").SetValue(model, new List<EventOption> { option });
            events.Add(model);
        }
    }
}

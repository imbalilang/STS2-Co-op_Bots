using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

// Registered by the game's mod message discovery. Host instructions, never client-broadcast commands.
public sealed class BotShopMessage : INetMessage, IRunLocationTargetedMessage
{
    public int Sequence, Entry, Removal, Price, Gold;
    public ulong Bot;
    public bool Ack, Success;
    public string Key = "";
    public string State = "";
    public RunLocation Location { get; set; }
    public bool ShouldBroadcast => false;
    public bool ShouldBuffer => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public void Serialize(PacketWriter w)
    {
        w.WriteInt(Sequence); w.WriteInt(Entry); w.WriteInt(Removal); w.WriteInt(Price); w.WriteInt(Gold);
        w.WriteULong(Bot); w.WriteBool(Ack); w.WriteBool(Success); w.WriteString(Key); w.WriteString(State); w.Write(Location);
    }
    public void Deserialize(PacketReader r)
    {
        Sequence = r.ReadInt(); Entry = r.ReadInt(); Removal = r.ReadInt(); Price = r.ReadInt(); Gold = r.ReadInt();
        Bot = r.ReadULong(); Ack = r.ReadBool(); Success = r.ReadBool(); Key = r.ReadString(); State = r.ReadString(); Location = r.Read<RunLocation>();
    }
}

internal static class BotShopDriver
{
    internal sealed record PurchaseScope(MerchantEntry Entry, int Price);
    internal static readonly AsyncLocal<PurchaseScope?> Scope = new();
    private static RunManager? manager;
    private static MerchantRoom? room;
    private static int nextSequence;
    private static Task<bool>? running;
    private static BotShopMessage? pending;
    private static readonly Queue<BotShopMessage> incoming = new();
    private static readonly Dictionary<int, BotShopMessage> completed = new();
    private static readonly Dictionary<ulong, BotShopMessage> acknowledgments = new();
    private static readonly HashSet<ulong> expected = new();
    private static readonly HashSet<ulong> done = new();
    private static readonly Dictionary<ulong, int> purchases = new();
    private static bool faulted;
    private static long started;
    private static long nextIdleLogAt;

    internal static void Initialize(RunManager instance)
    {
        manager = instance; room = null; pending = null; running = null; nextSequence = 0; faulted = false;
        incoming.Clear(); completed.Clear(); acknowledgments.Clear(); expected.Clear(); done.Clear(); purchases.Clear();
        instance.RunLocationTargetedBuffer.RegisterMessageHandler<BotShopMessage>(Receive);
    }

    private static void Receive(BotShopMessage message, ulong sender)
    {
        if (manager is null) return;
        if (manager.NetService.Type == NetGameType.Host)
        {
            if (message.Ack && pending is not null && message.Sequence == pending.Sequence && expected.Contains(sender))
                acknowledgments[sender] = message;
            return;
        }
        if (message.Ack || manager.NetService is not NetClientGameService client || sender != client.HostNetId) return;
        if (completed.TryGetValue(message.Sequence, out var result)) { manager.NetService.SendMessage(result); return; }
        if (pending?.Sequence == message.Sequence || incoming.Any(m => m.Sequence == message.Sequence)) return;
        incoming.Enqueue(message);
    }

    /// <summary>
    /// True once every seat the host shops for in this shop has been settled, so the
    /// room's continue is safe to press. Deliberately requires having entered *this*
    /// room instance: `done` is cleared on a room change, but between the change and
    /// the next Tick it still holds the previous shop's entries, and reading those as
    /// "finished" would press continue before a single purchase was made.
    /// </summary>
    internal static bool Finished(RunState state)
    {
        try
        {
            if (state.CurrentRoom is not MerchantRoom shop || !ReferenceEquals(room, shop)) return false;
            var driven = shop.Inventories.Where(i => AutoPilot.Drives(i.Player.NetId)).ToList();
            return driven.Count > 0
                && driven.All(i => done.Contains(i.Player.NetId) || !i.Player.Creature.IsAlive);
        }
        catch { return false; }
    }

    // Called on every peer. True keeps host bot map votes back until transactions are acknowledged.
    internal static bool Tick(RunManager instance, RunState state)
    {
        if (manager != instance || state.CurrentRoom is not MerchantRoom current)
        {
            // This early exit used to be silent, and silence here is the worst possible
            // shape: BotRoomProceedDriver refuses to press the shop's continue until
            // Finished() is true, and Finished() needs THIS method to have remembered
            // `room` first. So one silent return freezes the shop forever, with nothing
            // in the log but the periodic `where` line — which is exactly how the
            // 2026-09-20 live round died on floor 4. Speak instead — but only where it can
            // mean something: this method is called every frame in every room, so logging
            // the ordinary "we are not in a shop" case costs a line every five seconds for
            // a whole climb. Only the two deadlock shapes are worth a line: the run says we
            // ARE in a shop and this driver cannot bind it, or we are still holding a shop
            // that has gone away.
            // Drop the remembered shop once we are out of it. Without this, every room after
            // the first shop keeps `room` set, so this diagnostic fires once per five seconds
            // for the rest of the climb — and `Finished()` must not read a stale shop anyway.
            room = null;
            var suspicious = state.CurrentRoom?.RoomType == RoomType.Shop;
            if (suspicious && Environment.TickCount64 >= nextIdleLogAt)
            {
                nextIdleLogAt = Environment.TickCount64 + 5000;
                Log.Info($"CoopBots shop: idle room={(state.CurrentRoom is { } r ? r.RoomType.ToString() : "null")} "
                    + $"isMerchant={(state.CurrentRoom is MerchantRoom)} managerBound={(manager == instance)} "
                    + $"rememberedRoom={(room is null ? "null" : "set")} done={done.Count}");
            }
            return false;
        }
        if (!ReferenceEquals(room, current))
        {
            room = current; done.Clear(); purchases.Clear();
            foreach (var inventory in current.Inventories)
                Log.Info($"CoopBots shop: entered for seat {inventory.Player.NetId} "
                    + $"(driven={AutoPilot.Drives(inventory.Player.NetId)}, gold={inventory.Player.Gold}, "
                    + $"alive={inventory.Player.Creature.IsAlive}) stock=["
                    + string.Join(", ", inventory.AllEntries.Select(e => $"{BotShopPlanner.Key(e)}/{(e.IsStocked ? e.Cost.ToString() : "out")}"))
                    + "]");
        }
        if (faulted) return true;
        if (pending is not null)
        {
            if (running is null || !running.IsCompleted) return true;
            if (running.IsFaulted || !running.Result) { Fail("购买失败或状态不一致；停止购物及Bot离店投票，请重新读档检查日志。"); return true; }
            var buyer = state.Players.First(p => p.NetId == pending.Bot);
            if (RunAuthority.IsSubmittingPeer(instance))
            {
                if (expected.Any(id => !acknowledgments.ContainsKey(id)))
                {
                    if (Environment.TickCount64 - started > 10000)
                    {
                        instance.NetService.SendMessage(pending); started = Environment.TickCount64;
                        BotCooperation.LastAction = "商店：等待其他玩家确认Bot交易";
                    }
                    return true;
                }
                var actual = Fingerprint(current.Inventories.First(i => i.Player == buyer));
                if (acknowledgments.Values.Any(a => !a.Success || a.Bot != buyer.NetId || a.Gold != buyer.Gold || a.State != actual))
                { Fail("各端Bot购买结果不一致；停止继续购物，请重新读档检查日志。"); return true; }
                purchases[buyer.NetId] = purchases.GetValueOrDefault(buyer.NetId) + 1;
                if (purchases[buyer.NetId] >= 8) done.Add(buyer.NetId);
            }
            else
            {
                var ack = new BotShopMessage { Sequence = pending.Sequence, Bot = buyer.NetId, Ack = true, Success = true,
                    Gold = buyer.Gold, State = Fingerprint(current.Inventories.First(i => i.Player == buyer)), Location = pending.Location };
                completed[pending.Sequence] = ack; instance.NetService.SendMessage(ack);
            }
            pending = null; running = null;
            return true;
        }
        // RunAuthority, not `== Host`: singleplayer IS a submitting peer (`LoadRunLobby.cs:368`).
        // Reading Host-only here sent the singleplayer live run down this *client* branch,
        // where there is never an incoming command and `pending` is always null — so the
        // shopping loop below was never reached, no seat ever reached `done`, and the shop
        // could never be left. That is the floor-4 deadlock, exactly.
        if (!RunAuthority.IsSubmittingPeer(instance))
        {
            if (incoming.TryDequeue(out var command)) Start(command, state);
            return pending is not null;
        }
        if (instance.ActionExecutor.CurrentlyRunningAction is not null || !instance.ActionQueueSet.IsEmpty) return true;
        // Drives, not IsBot: the host has to shop for a handed-over seat too, or the
        // shop waits forever on a purchase nobody is going to make.
        foreach (var inventory in current.Inventories.Where(i => AutoPilot.Drives(i.Player.NetId)).OrderBy(i => i.Player.NetId))
        {
            if (done.Contains(inventory.Player.NetId) || !inventory.Player.Creature.IsAlive) continue;
            var choice = BotShopPlanner.Choose(inventory);
            if (choice is null)
            {
                // Also used to be silent. "the bot decided not to buy" and "the bot never
                // wired up" look identical in the log otherwise — and the shop's continue
                // is gated on this seat reaching `done`, so this line is the difference
                // between "settled, about to leave" and "deadlocked forever".
                done.Add(inventory.Player.NetId);
                Log.Info($"CoopBots shop: seat {inventory.Player.NetId} settled without a purchase "
                    + $"(gold={inventory.Player.Gold}, done={done.Count})");
                continue;
            }
            var command = new BotShopMessage { Sequence = ++nextSequence, Bot = inventory.Player.NetId, Entry = choice.Index,
                Removal = choice.RemovalIndex, Price = choice.Cost, Gold = inventory.Player.Gold,
                Key = BotShopPlanner.Key(inventory.AllEntries.ElementAt(choice.Index)), State = Fingerprint(inventory), Location = instance.RunLocationTargetedBuffer.CurrentLocation };
            expected.Clear(); acknowledgments.Clear();
            if (instance.NetService is NetHostGameService host)
                foreach (var peer in host.ConnectedPeers.Where(p => p.readyForBroadcasting)) expected.Add(peer.peerId);
            else
                foreach (var human in state.Players.Where(p => !BotRegistry.IsBot(p.NetId) && p.NetId != instance.NetService.NetId)) expected.Add(human.NetId);
            instance.NetService.SendMessage(command); Start(command, state); return true;
        }
        return false;
    }

    private static void Start(BotShopMessage message, RunState state)
    {
        pending = message; started = Environment.TickCount64;
        running = Execute(message, state);
    }

    private static async Task<bool> Execute(BotShopMessage message, RunState state)
    {
        if (state.CurrentRoom is not MerchantRoom shop || !AutoPilot.Drives(message.Bot)) return false;
        var inventory = shop.Inventories.FirstOrDefault(i => i.Player.NetId == message.Bot);
        if (inventory is null) return false;
        var player = inventory.Player;
        var entry = inventory.AllEntries.ElementAtOrDefault(message.Entry);
        if (entry is null || !entry.IsStocked || !player.Creature.IsAlive || player.Gold != message.Gold || Fingerprint(inventory) != message.State
            || entry.Cost != message.Price || message.Price < 0 || player.Gold < message.Price || BotShopPlanner.Key(entry) != message.Key) return false;
        Scope.Value = new(entry, message.Price);
        try
        {
            if (entry is MerchantCardRemovalEntry removal)
            {
                var card = player.Deck.Cards.ElementAtOrDefault(message.Removal);
                if (card is null || !card.IsRemovable) return false;
                await PlayerCmd.LoseGold(message.Price, player, GoldLossType.Spent);
                await CardPileCmd.RemoveFromDeck(card);
                player.ExtraFields.CardShopRemovalsUsed++;
                removal.SetUsed();
                await Hook.AfterItemPurchased(state, player, entry, message.Price);
                entry.InvokePurchaseCompleted(entry);
            }
            else if (!await entry.OnTryPurchaseWrapper(inventory)) return false;
            BotCooperation.LastAction = $"商店：Bot购买 {message.Key}，花费 {message.Price}，余额 {player.Gold}";
            Log.Info($"CoopBots shop #{message.Sequence}: bot={player.NetId} item={message.Key} cost={message.Price} gold={player.Gold}");
            return true;
        }
        finally { Scope.Value = null; }
    }

    internal static string Fingerprint(MerchantInventory inventory)
    {
        var p = inventory.Player;
        return $"{p.Gold}:{p.Creature.CurrentHp}:{p.Creature.MaxHp}:{p.ExtraFields.CardShopRemovalsUsed}:"
            + string.Join(',', p.Deck.Cards.Select(c => $"{c.Id.Entry}+{c.CurrentUpgradeLevel}")) + ":"
            + string.Join(',', p.Relics.Select(r => $"{r.Id.Entry}/{r.IsUsedUp}")) + ":"
            + string.Join(',', p.PotionSlots.Select(potion => potion?.Id.Entry ?? "-")) + ":"
            + string.Join(',', inventory.AllEntries.Select(e => $"{BotShopPlanner.Key(e)}/{e.IsStocked}/{(e.IsStocked ? e.Cost : -1)}"));
    }

    private static void Fail(string reason)
    {
        faulted = true; BotCooperation.LastAction = "商店：" + reason;
        Log.Error("CoopBots shop: " + reason + " " + running?.Exception);
        // Only a CLIENT reports back to a host. Reading this as "!= Host" made a
        // singleplayer run the sender of its own failure ack.
        if (!RunAuthority.IsSubmittingPeer(manager) && pending is not null)
            manager?.NetService.SendMessage(new BotShopMessage { Sequence = pending.Sequence, Bot = pending.Bot, Ack = true,
                Success = false, Location = pending.Location });
    }
}

[HarmonyPatch(typeof(RunManager), "InitializeShared")]
internal static class BotShopInitializePatch
{
    private static void Postfix(RunManager __instance)
    {
        BotShopDriver.Initialize(__instance);
        BotChoicePlanSync.Initialize(__instance);
        BotCalloutSync.Initialize(__instance);
        CrystalSphereSync.Initialize(__instance);
    }
}

[HarmonyPatch]
internal static class BotShopLocalRewardPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => typeof(RewardSynchronizer).GetMethods()
        .Where(m => m.Name.StartsWith("SyncLocal", StringComparison.Ordinal));
    private static bool Prefix() => BotShopDriver.Scope.Value is null;
}

[HarmonyPatch(typeof(MerchantEntry), "get_Cost")]
internal static class BotShopFixedPricePatch
{
    private static bool Prefix(MerchantEntry __instance, ref int __result)
    {
        var scope = BotShopDriver.Scope.Value;
        if (scope is null || scope.Entry != __instance) return true;
        __result = scope.Price; return false;
    }
}

[HarmonyPatch(typeof(MembershipCard), nameof(MembershipCard.ModifyMerchantPrice))]
internal static class BotMembershipPatch
{
    private static bool Prefix(MembershipCard __instance, Player player, decimal originalPrice, ref decimal __result)
    {
        // DIFFED AGAINST NATIVE 2026-09-20, because every wholesale replacement of a method
        // body is a candidate for "the body did something we skipped". Result: the formula is
        // identical (MembershipCard.cs:28), and the only difference is DELIBERATE —
        // native also requires `LocalContext.IsMe(Owner)` and returns the full price
        // otherwise, which is a single-player assumption (the local player is the owner).
        // A driven seat here is not necessarily the local one, and the discount belongs to
        // whoever owns the relic, so dropping that guard is the multiplayer correction, not
        // an oversight. Do not "fix" it back to the native form without re-checking that.
        if (!AutoPilot.Drives(player.NetId) || __instance.Owner != player) return true;
        __result = originalPrice * __instance.DynamicVars["Discount"].BaseValue / 100;
        return false;
    }
}

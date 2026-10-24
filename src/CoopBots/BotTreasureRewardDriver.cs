using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

// Native DoLocalTreasureRoomRewards broadcasts first, then executes the same
// private reward body on every peer. Its message takes the player from sender
// ID, so a synthetic seat needs an explicit host-authorized player ID instead.
internal static class BotTreasureRewardDriver
{
    private static readonly MethodInfo RewardBody = AccessTools.Method(
        typeof(OneOffSynchronizer), "DoTreasureRoomRewards", new[] { typeof(Player) });
    private static RunManager? manager;
    private static RunLocationTargetedMessageBuffer? buffer;
    private static BotTreasureRewardSession? session;

    internal static IReadOnlyList<Player> SyntheticBots(RunState state)
        => state.Players.Where(p => BotRegistry.IsBot(p.NetId)
            && AutoPilot.Drives(p.NetId) && p.Creature.IsAlive).ToArray();

    internal static void Initialize(RunManager instance)
    {
        buffer?.UnregisterMessageHandler<BotTreasureRewardMessage>(Receive);
        session?.Dispose();
        manager = instance;
        session = new BotTreasureRewardSession(
            () => instance.DebugOnlyGetState(),
            () => instance.RunLocationTargetedBuffer.CurrentLocation,
            () => RunAuthority.IsSubmittingPeer(instance),
            () => (instance.NetService as NetClientGameService)?.HostNetId,
            () => instance.NetService is NetHostGameService host
                ? host.ConnectedPeers.Where(p => p.readyForBroadcasting).Select(p => p.peerId).ToArray() : [],
            message => instance.NetService.SendMessage(message),
            player => GrantNative(instance.OneOffSynchronizer, player));
        buffer = instance.RunLocationTargetedBuffer;
        buffer.RegisterMessageHandler<BotTreasureRewardMessage>(Receive);
    }

    internal static void CleanUp(RunManager instance)
    {
        if (manager != instance) return;
        buffer?.UnregisterMessageHandler<BotTreasureRewardMessage>(Receive);
        buffer = null; session?.Dispose(); session = null; manager = null;
    }

    private static void Receive(BotTreasureRewardMessage message, ulong sender) => session?.Receive(message, sender);

    internal static Task<int> GrantNative(OneOffSynchronizer synchronizer, Player player)
        => (Task<int>)RewardBody.Invoke(synchronizer, new object[] { player })!;

    internal static bool TryGrant(RunManager instance, RunState state)
    {
        if (manager != instance) Initialize(instance);
        return session!.Tick();
    }
}

[HarmonyPatch(typeof(RunManager), "InitializeShared")]
internal static class BotTreasureRewardInitializePatch
{
    private static void Postfix(RunManager __instance) => BotTreasureRewardDriver.Initialize(__instance);
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
internal static class BotTreasureRewardCleanUpPatch
{
    private static void Prefix(RunManager __instance) => BotTreasureRewardDriver.CleanUp(__instance);
}

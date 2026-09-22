using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

/// <summary>
/// Gives synthetic bot seats the treasure room's NORMAL rewards.
///
/// The game's treasure room is per-player and click-driven:
///
///   NTreasureRoom.OnChestButtonReleased -> OpenChest()
///     -> TreasureRoom.DoNormalRewards()
///       -> OneOffSynchronizer.DoLocalTreasureRoomRewards()
///            -> DoTreasureRoomRewards(LocalPlayer)
///
/// <see cref="BotTreasureChestDriver"/> handles the handed-over LOCAL seat by
/// clicking its chest. Synthetic bots have no client and no chest node, so unless
/// the host calls the reward body for them they get nothing from the chest — while
/// the RELIC vote still succeeds for every seat. The live 2026-09-22 run showed the
/// asymmetry: only the handed-over seat converted SPOILS_MAP, and the three
/// synthetic bots still carried the unplayable Quest card in build[final].
///
/// This driver runs only on the submitting peer (the one that owns the run) and
/// grants each synthetic bot the same body the game runs for a remote human opener.
/// It is latched per treasure room so a frame loop can never pay twice.
/// </summary>
internal static class BotTreasureRewardDriver
{
    private static readonly MethodInfo? DoTreasureRoomRewards = AccessTools.Method(
        typeof(OneOffSynchronizer), "DoTreasureRoomRewards", new[] { typeof(Player) });
    private static readonly HashSet<(RunState Run, string Key)> Granted = new();
    private static Task? pending;

    /// <summary>
    /// The synthetic seats this driver owes a chest to. Deliberately IsBot, not
    /// Drives: a handed-over HUMAN seat already has a local chest and already gets
    /// its reward through <see cref="BotTreasureChestDriver"/>.
    /// </summary>
    internal static IReadOnlyList<Player> SyntheticBots(RunState state)
        => state.Players
            .Where(player => BotRegistry.IsBot(player.NetId)
                && AutoPilot.Drives(player.NetId)
                && player.Creature.IsAlive)
            .ToArray();

    /// <summary>
    /// Start (or finish) this room's synthetic-bot chest rewards.
    /// Returns true while work is still in flight; the caller must not proceed the
    /// room until it returns false, or the gold/Quest effects can race the exit.
    /// </summary>
    internal static bool TryGrant(RunManager manager, RunState state)
    {
        if (state.CurrentRoom is not { RoomType: RoomType.Treasure }) return false;
        if (pending is { } running)
        {
            if (!running.IsCompleted) return true;
            pending = null;
        }
        if (!RunAuthority.IsSubmittingPeer(manager)) return false;
        var bots = SyntheticBots(state);
        if (bots.Count == 0) return false;
        if (DoTreasureRoomRewards is null)
        {
            Log.Warn("CoopBots treasure: OneOffSynchronizer.DoTreasureRoomRewards "
                + "was not found; synthetic bots will not receive the room's chest rewards.");
            return false;
        }

        var key = state.CurrentMapCoord?.ToString()
            ?? state.CurrentRoom.GetHashCode().ToString();
        if (!Granted.Add((state, key))) return false;

        pending = GrantAll(manager, bots);
        return true;
    }

    private static async Task GrantAll(RunManager manager, IReadOnlyList<Player> bots)
    {
        foreach (var player in bots)
        {
            // Read this before the reward removes it, so the log can distinguish
            // "no SPOILS_MAP" from "map converted silently".
            var hadSpoilsMap = player.Deck.Cards.Any(card => card.Id.Entry == "SPOILS_MAP");
            await GrantOne(manager, player, hadSpoilsMap);
        }
    }

    private static async Task GrantOne(RunManager manager, Player player, bool hadSpoilsMap)
    {
        try
        {
            var task = (Task<int>)DoTreasureRoomRewards!.Invoke(
                manager.OneOffSynchronizer, new object[] { player })!;
            var gold = await task;
            Log.Info($"CoopBots treasure: cashed the chest for bot {player.NetId}"
                + (hadSpoilsMap ? " (SPOILS_MAP converted)" : "")
                + $" +{gold} gold");
        }
        catch (Exception error)
        {
            var inner = error.GetBaseException();
            Log.Warn($"CoopBots treasure: bot {player.NetId} chest reward failed: "
                + $"{inner.GetType().Name}: {inner.Message}");
        }
    }
}

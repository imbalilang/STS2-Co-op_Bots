using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.TreasureRooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using GodotObject = Godot.GodotObject;

namespace CoopBots;

/// <summary>
/// Opens the treasure room's chest for a handed-over seat.
///
/// The room has three steps and this mod only had the middle one. The relic is a
/// synchronized vote (<c>BotRuntime.TryPickTreasureRelic</c>) and the continue is a
/// button (<see cref="BotRoomProceedDriver"/>), but the chest itself is a plain local
/// click — <c>NTreasureRoom</c> wires <c>OnChestButtonReleased</c> to <c>OpenChest()</c>
/// and nothing else ever calls it. So a table with nobody at the keyboard sits in front
/// of a closed chest.
///
/// Only the opening is done here. The relic assignment already arrives through the vote
/// path, and clicking a relic holder as well would submit a second choice for the same
/// seat.
/// </summary>
internal static class BotTreasureChestDriver
{
    private static readonly List<NTreasureRoom> Live = new();
    private static long _nextWarnAt;

    internal static void Register(NTreasureRoom room)
    {
        if (!Live.Contains(room)) Live.Add(room);
    }

    internal static void Unregister(NTreasureRoom room) => Live.Remove(room);

    internal static bool TryOpenChest(RunManager manager, RunState state)
    {
        if (LocalContext.NetId is not { } me || !AutoPilot.IsAutopiloted(me)) return false;
        if (state.CurrentRoom?.RoomType != RoomType.Treasure) return false;
        if (Live.Count == 0) return false;
        try
        {
            foreach (var room in Live.ToList())
            {
                if (room is null || !GodotObject.IsInstanceValid(room)) { Live.Remove(room); continue; }
                if (room.GetNodeOrNull<NTreasureButton>("%Chest") is not { } chest) continue;
                // The room disables the chest once OpenChest starts, so an enabled chest
                // is exactly "not opened yet" — no separate latch needed.
                if (!chest.IsEnabled || !chest.IsVisibleInTree()) continue;

                chest.ForceClick();
                Log.Info("CoopBots treasure: opened the chest for the handed-over seat");
                return true;
            }
        }
        catch (Exception error)
        {
            if (Environment.TickCount64 >= _nextWarnAt)
            {
                _nextWarnAt = Environment.TickCount64 + 1000;
                Log.Warn($"CoopBots could not open the treasure chest: {error.GetBaseException().Message}");
            }
        }
        return false;
    }
}

// Registered on the room rather than on the chest: the chest is a private field reached
// by node path, and the room node is the thing that lives for exactly one treasure room.
[HarmonyPatch(typeof(NTreasureRoom), nameof(NTreasureRoom._Ready))]
internal static class TreasureRoomReadyPatch
{
    private static void Postfix(NTreasureRoom __instance) => BotTreasureChestDriver.Register(__instance);
}

[HarmonyPatch(typeof(NTreasureRoom), nameof(NTreasureRoom._ExitTree))]
internal static class TreasureRoomExitPatch
{
    private static void Postfix(NTreasureRoom __instance) => BotTreasureChestDriver.Unregister(__instance);
}

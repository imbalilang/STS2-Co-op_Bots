using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots.Building;

/// <summary>
/// A combat-start snapshot of every player's *permanent* deck, reduced to plain
/// counts and card ids.
///
/// Rewards are resolved concurrently on every peer (see
/// <c>RewardPatches.BotRemoteChoicePatch</c>, which computes locally with no
/// host authority), so scoring one player's reward must never read a teammate's
/// mutable <c>Deck</c> while that teammate may be editing its own. This context
/// is captured once, at the shared gameplay boundary
/// <c>CombatManager.SetUpCombat</c>, before the engine calls
/// <c>PopulateCombatState</c> and before any temporary combat piles exist. Every
/// candidate in one offer reads the same immutable summary, so teammate reward
/// order cannot change the current offer's result.
///
/// It is explicitly a combat-start snapshot for combat-reward decisions, not a
/// live view of the deck after the battle. Storage is weakly keyed by the
/// <see cref="IRunState"/> and versioned by act/floor/room; a read that does not
/// match the current location (a new room, a resumed or reconnected run, a shop,
/// an unknown map) is treated as unknown and the caller <b>never</b> falls back
/// to rereading a teammate deck. Capture is bounded, synchronous, IO/rng/network
/// free, and any failure clears the stale entry and fails harmlessly.
/// </summary>
internal static class TeamBuildingContext
{
    /// <summary>Plain deck facts for one player; no live card references.</summary>
    internal sealed record PlayerSummary(
        int Size, int Attacks, int Blocks, int Draws, int Energies,
        int Debuffs, int Supports,
        IReadOnlyDictionary<string, int> IdCounts,
        IReadOnlyDictionary<string, int> RoleCounts);

    private sealed record RunCapture(
        int ActIndex, int TotalFloor, int RoomCount,
        IReadOnlyDictionary<ulong, PlayerSummary> Players)
    {
        internal bool Matches(IRunState run)
            => run.CurrentActIndex == ActIndex
               && run.TotalFloor == TotalFloor
               && run.CurrentRoomCount == RoomCount;
    }

    // A location that lifecycle events (rejoin / load) have poisoned. The
    // snapshot is not trustworthy at this act/floor on *any* peer: a rejoining
    // client restores a saved run that never saw the shared combat start, so the
    // host and the existing clients must also drop their capture or the peers
    // would compute different reward choices. The poison lasts until the act or
    // floor moves on, so a saved combat's own SetUpCombat cannot immediately
    // recapture the current (post-reward) deck.
    private sealed record Suppression(int ActIndex, int TotalFloor)
    {
        internal bool Matches(IRunState run)
            => run.CurrentActIndex == ActIndex && run.TotalFloor == TotalFloor;
    }

    // The run is the owning lifetime: a finished run's capture dies with it.
    private static readonly ConditionalWeakTable<IRunState, RunCapture> Store = new();
    private static readonly ConditionalWeakTable<IRunState, Suppression> Poisoned = new();

    /// <summary>
    /// Capture every player's permanent deck at combat start. A prefix on
    /// <c>CombatManager.SetUpCombat</c> would otherwise be able to see the
    /// engine already populating combat piles; this runs before that.
    /// </summary>
    internal static void Capture(CombatState state)
    {
        try
        {
            var run = state.RunState;
            if (run is null) return;
            Capture(run, state.Players);
        }
        catch
        {
            // A capture we cannot complete must not leave a mismatched snapshot
            // behind that a later reward could read. The lifecycle poison, if any,
            // is left in place.
            try { ClearCapture(state.RunState); } catch { }
        }
    }

    /// <summary>Capture from a player list; the test-friendly entry point.</summary>
    internal static void Capture(IRunState run, IReadOnlyList<Player> players)
    {
        if (run is null) return;
        try
        {
            // A poisoned location must not recapture: a saved combat's SetUpCombat
            // runs at the very floor the rejoin poisoned, and the deck it would
            // see is not the shared combat-start deck.
            if (IsPoisoned(run)) { ClearCapture(run); return; }
            var summaries = new Dictionary<ulong, PlayerSummary>();
            foreach (var player in players)
                summaries[player.NetId] = Summarize(player);
            // AddOrUpdate rather than Remove+Add: a concurrent capture for the
            // same run must not race on the key. The run is weakly held.
            Store.AddOrUpdate(run, new RunCapture(run.CurrentActIndex, run.TotalFloor, run.CurrentRoomCount, summaries));
        }
        catch
        {
            try { ClearCapture(run); } catch { }
        }
    }

    /// <summary>
    /// The teammate summary for this run at the current location, or null when
    /// it is unknown/stale. Callers must treat null as "no teammate evidence"
    /// and never reread the teammate's live deck.
    /// </summary>
    internal static PlayerSummary? TryGet(IRunState run, ulong netId)
    {
        if (run is null) return null;
        try
        {
            if (IsPoisoned(run)) { ClearCapture(run); return null; }
            if (!Store.TryGetValue(run, out var capture)) return null;
            if (!capture.Matches(run))
            {
                ClearCapture(run);
                return null;
            }
            return capture.Players.TryGetValue(netId, out var summary) ? summary : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Poison the current act/floor after a rejoin or a save load, on every peer.
    /// The existing snapshot is dropped and no capture is accepted again until
    /// the run moves to a later act/floor, at which point the shared combat start
    /// is real again on all peers. There is no new network message or save field;
    /// this only withholds local evidence that is not provably shared.
    /// </summary>
    internal static void Invalidate(IRunState? run)
    {
        if (run is null) return;
        try
        {
            Store.Remove(run);
            Poisoned.AddOrUpdate(run, new Suppression(run.CurrentActIndex, run.TotalFloor));
        }
        catch { }
    }

    /// <summary>Whether the current act/floor is poisoned; clears a stale poison.</summary>
    internal static bool IsPoisoned(IRunState run)
    {
        if (run is null) return false;
        try
        {
            if (!Poisoned.TryGetValue(run, out var suppression)) return false;
            if (suppression.Matches(run)) return true;
            Poisoned.Remove(run);
            return false;
        }
        catch
        {
            return false;
        }
    }

    internal static void Clear(IRunState run)
    {
        if (run is null) return;
        try { Store.Remove(run); } catch { }
        try { Poisoned.Remove(run); } catch { }
    }

    private static void ClearCapture(IRunState? run)
    {
        if (run is null) return;
        try { Store.Remove(run); } catch { }
    }

    private static PlayerSummary Summarize(Player player)
    {
        var deck = player.Deck.Cards;
        var summary = DeckStructure.Build(deck, BuildValue.StableEnergy(player));
        var debuffs = summary.RoleCounts.GetValueOrDefault("vulnerable")
            + summary.RoleCounts.GetValueOrDefault("weak")
            + summary.RoleCounts.GetValueOrDefault("strengthdown");
        var supports = 0;
        foreach (var card in deck)
        {
            try { if (card.TargetType == TargetType.AnyAlly) supports++; }
            catch { }
        }
        return new PlayerSummary(summary.Size, summary.Attacks, summary.Blocks, summary.Draws,
            summary.Energies, debuffs, supports, summary.IdCounts, summary.RoleCounts);
    }
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
internal static class TeamBuildingContextPatch
{
    // A prefix, not a postfix: the engine reads each player's permanent Deck in
    // PopulateCombatState, so the snapshot must be taken before that mutation.
    private static void Prefix(CombatState state) => TeamBuildingContext.Capture(state);
}

// A run is loaded or a client rejoins, and the shared combat start those peers
// are at is no longer available to everyone. Each of these three verified native
// entry points poisons the current act/floor on its peer; because every peer runs
// one of them, the fallback is consistent across the session. RunManager owns the
// host path and the loading-client path; RunLobby owns the existing clients'
// PlayerRejoinedMessage handler. None of them sends anything.
[HarmonyPatch(typeof(RunManager), "GetRejoinMessage")]
internal static class TeamBuildingHostRejoinPatch
{
    private static void Postfix()
        => TeamBuildingContext.Invalidate(RunManager.Instance.DebugOnlyGetState());
}

[HarmonyPatch(typeof(RunManager), "InitializeSavedRun")]
internal static class TeamBuildingSavedRunPatch
{
    // Runs after State is assigned and before the saved room is set up, so the
    // poison is already in place when a saved combat's SetUpCombat fires.
    private static void Postfix()
        => TeamBuildingContext.Invalidate(RunManager.Instance.DebugOnlyGetState());
}

[HarmonyPatch(typeof(RunLobby), "HandlePlayerRejoinedMessage")]
internal static class TeamBuildingClientRejoinPatch
{
    // A prefix, not a postfix: the native handler invokes PlayerRejoined
    // listeners, which can resume actions, so the poison must be in place before
    // any of them run. The host and saved-run paths still invalidate afterwards.
    private static void Prefix()
        => TeamBuildingContext.Invalidate(RunManager.Instance.DebugOnlyGetState());
}

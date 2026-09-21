using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

/// <summary>
/// "Is THIS machine the peer that owns and submits for the run?"
///
/// <para>
/// This is one predicate, in one place, on purpose. It was previously spelled inline as
/// `NetService.Type == NetGameType.Host` in seven different files, and every one of them
/// silently excluded singleplayer — which is exactly the mode the unattended live run
/// uses. The shop was where it finally cost a whole round: the driver took its *client*
/// branch, never shopped for the seat, so <c>Finished</c> never became true, so the
/// shop's continue was never pressed, and the run sat on floor 4 with nothing in the log
/// but the periodic `where` line.
/// </para>
///
/// <para>
/// The shape is <b>deliberately copied from the game</b>, not invented: the authoritative
/// peer is decided the same way in <c>LoadRunLobby.cs:368</c> —
/// <code>(NetService.Type == NetGameType.Host || NetService.Type == NetGameType.Singleplayer)</code>
/// — and the game's own <c>--autoslay</c> unattended mode runs a SINGLEPLAYER run. So a
/// gate that only accepts <c>Host</c> does nothing in the mode the game itself uses for
/// unattended play.
/// </para>
///
/// <para>
/// <c>None</c>, <c>Client</c> and <c>Replay</c> are all correctly excluded: a client must
/// not submit, and a replay is driven by the game itself. Note that this is NOT the same
/// question as "should I broadcast to remote peers" — a site that means that asks
/// <c>Type == NetGameType.Host</c> directly and keeps doing so, because singleplayer has
/// no peers to broadcast to.
/// </para>
/// </summary>
internal static class RunAuthority
{
    /// <summary>
    /// True when the given manager (or the current run's) belongs to the peer that owns
    /// the run and submits its actions. Prefer this over writing the comparison out again.
    /// Pass the manager when one is already in hand: reaching for the singleton under a
    /// method that was handed a manager hides which run is being asked about.
    /// </summary>
    internal static bool IsSubmittingPeer(RunManager? manager = null) =>
        Accepts((manager ?? RunManager.Instance)?.NetService?.Type);

    /// <summary>
    /// The decision itself, split out from the singleton so the shop's regression can
    /// assert the table directly — this is the exact predicate that was wrong, and it is
    /// what a reverted edit would have to change.
    /// </summary>
    internal static bool Accepts(NetGameType? type) =>
        type is NetGameType.Host or NetGameType.Singleplayer;
}

using System;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace CoopBots;

/// <summary>
/// Restores <c>FastMode</c> for anyone the removed "极速 / Instant mode" switch left
/// stuck at <see cref="FastModeType.Instant"/>.
///
/// <para>
/// Removing the panel switch is not enough on its own: it wrote the preference to the
/// local save, so a player who had it on keeps hanging after the button is gone. This
/// runs once per run, before any combat, and only touches a value it is safe to assume
/// came from us — the dev console's <c>instant</c> command sets the same preference, but
/// that is a developer toggle and Instant is a known hard-freeze on a real event.
/// </para>
///
/// <para>
/// Why Instant is dangerous: <c>Cmd.Wait</c> returns a completed task when
/// <c>PrefsSave.FastMode == Instant</c> (Cmd.cs:36-43), and PunchOff's ambient animation
/// loop is a <c>while</c> over <c>await Cmd.Wait(...)</c> that allocates an
/// <c>NHitSparkVfx</c> node per iteration. With no wait it never yields, exhausts Godot's
/// element limit, then logs a full backtrace per iteration — 1.3 GB of log and a freeze
/// inside a single frame, cancelled only by an event option that needs a frame to arrive.
/// </para>
///
/// <para>
/// We cannot know what the player's speed was before the switch overwrote it (the old
/// switch remembered it in memory only, so a restart loses it), so this restores
/// <see cref="FastModeType.Fast"/> — a step faster than the default that still waits.
/// </para>
/// </summary>
internal static class InstantModeGuard
{
    private static bool repaired;

    internal static void RepairIfStuck()
    {
        if (repaired) return;
        repaired = true;
        try
        {
            if (SaveManager.Instance?.PrefsSave is not { } prefs) return;
            if (prefs.FastMode != FastModeType.Instant) return;
            prefs.FastMode = FastModeType.Fast;
            Log.Warn("CoopBots: FastMode was left at Instant by the removed instant-mode switch; "
                + "restored to Fast. Instant makes the game's Cmd.Wait return immediately, which turns "
                + "event animation loops into unbounded VFX allocation (Godot element limit, hard freeze).");
        }
        catch (Exception error)
        {
            Log.Warn($"CoopBots could not repair the fast-mode preference: {error.GetBaseException().Message}");
        }
    }
}

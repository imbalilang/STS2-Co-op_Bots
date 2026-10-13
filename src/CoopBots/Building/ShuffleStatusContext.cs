using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CoopBots.Building;

// Price an upcoming Biiig Hug against the exact deck its removals leave. The
// scope is decision-local, affects only this player, and never grants a relic
// or changes the live deck. Worker jobs capture this fact before leaving the
// game thread; replay restores it along with their native read cache.
internal static class ShuffleStatusContext
{
    [ThreadStatic] private static (Player Player, bool Soot)? preview;
    internal static bool Active(Player? player)
        => player is not null && (preview is { } value && ReferenceEquals(value.Player, player)
            ? value.Soot : player.Relics.Any(r => r is BiiigHug));
    internal static IDisposable Use(Player player, bool soot)
    {
        var previous = preview;
        preview = (player, soot);
        return new Scope(previous);
    }
    private sealed class Scope((Player Player, bool Soot)? previous) : IDisposable
    { public void Dispose() => preview = previous; }
}

using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;

namespace CoopBots;

// Native Bot selectors return completed tasks; human selectors can pause.
// Snapshot that execution contract with every combat root, including replay,
// emergency and runtime selector roots. Future forks retain the captured set.
internal static class KernelCombatCapture
{
    internal static KernelSession Capture(CombatState state)
        => KernelSession.Capture(state).ConfigureSynchronousChoicePlayers(SynchronousSeats(state));

    internal static ulong[] SynchronousSeats(CombatState state)
        => state.Players.Where(p => AutoPilot.Drives(p.NetId)).Select(p => p.NetId).ToArray();
}

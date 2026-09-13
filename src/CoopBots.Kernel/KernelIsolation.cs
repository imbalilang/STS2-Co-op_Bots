using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using CoopBots.Kernel.Vendor;

namespace CoopBots.Kernel;

// Separate from the original mod's startup. This patch only suppresses UI
// notifications inside our thread-local simulation scope.
internal static class KernelIsolation
{
    private static bool initialized;
    private static long revision;
    internal static long LiveRevision => Interlocked.Read(ref revision);
    internal static void Initialize()
    {
        if (initialized) return;
        new Harmony("cn.xiwa.sts2.coopbots.kernel.isolation").Patch(
            AccessTools.Method(typeof(CombatStateTracker), "NotifyCombatStateChanged", [typeof(string)]),
            prefix: new HarmonyMethod(typeof(KernelIsolation), nameof(NotifyPrefix)) { priority = Priority.First });
        initialized = true;
    }
    private static bool NotifyPrefix()
    {
        if (SimulationNotificationIsolation.IsActive) return false;
        Interlocked.Increment(ref revision);
        return true;
    }
}

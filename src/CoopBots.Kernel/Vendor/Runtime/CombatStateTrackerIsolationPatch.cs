using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using STS2RitsuLib.Patching.Models;

namespace CoopBots.Kernel.Vendor;

internal sealed class CombatStateTrackerIsolationPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_simulation_notification_isolation";
    public static string Description => "阻止预测变量变化把状态通知泄漏到真实战斗 UI";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CombatStateTracker), "NotifyCombatStateChanged", [typeof(string)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(string caller)
    {
        if (!SimulationNotificationIsolation.IsActive)
            return true;
        SimulationNotificationIsolation.LogSuppression(caller);
        return false;
    }
}

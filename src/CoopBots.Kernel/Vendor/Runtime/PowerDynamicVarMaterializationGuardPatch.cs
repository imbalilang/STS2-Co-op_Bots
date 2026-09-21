using HarmonyLib;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Patching.Models;

namespace CoopBots.Kernel.Vendor;

internal sealed class PowerDynamicVarMaterializationGuardPatch : IPatchMethod
{
    private static readonly AccessTools.FieldRef<PowerModel, DynamicVarSet?> DynamicVarsField =
        AccessTools.FieldRefAccess<PowerModel, DynamicVarSet?>("_dynamicVars");

    public static string PatchId => "combat_solver_power_dynamic_var_materialization_guard";
    public static string Description => "禁止后台模拟惰性创建 Power 显示变量";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(PowerModel), "get_DynamicVars", Type.EmptyTypes),
    ];

    [HarmonyPriority(Priority.First)]
    public static void Prefix(PowerModel __instance)
    {
        if (SimulationNotificationIsolation.IsActive && DynamicVarsField(__instance) == null)
        {
            throw new InvalidOperationException(
                $"后台模拟尝试惰性创建 Power 显示变量：power={__instance.Id.Entry}；" +
                "该实例必须在主线程根捕获阶段完成物化。");
        }
    }
}

using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Patching.Models;
using CoopBots.Kernel.Vendor.Engine.Common;

namespace CoopBots.Kernel.Vendor;

internal sealed class BaseLibCloneConcurrencyPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_baselib_clone_concurrency";
    public static string Description => "串行保护 BaseLib 的模型克隆扩展";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(AbstractModel), nameof(AbstractModel.MutableClone), Type.EmptyTypes),
    ];

    [HarmonyPriority(Priority.First)]
    public static void Prefix(out bool __state)
        => __state = BaseLibCloneConcurrency.Enter();

    [HarmonyPriority(Priority.Last)]
    public static Exception? Finalizer(Exception? __exception, bool __state)
    {
        BaseLibCloneConcurrency.Exit(__state);
        return __exception;
    }
}

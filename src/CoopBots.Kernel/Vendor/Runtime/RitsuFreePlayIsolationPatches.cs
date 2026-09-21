using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Cards.FreePlay;
using STS2RitsuLib.Patching.Models;

namespace CoopBots.Kernel.Vendor;

internal sealed class RitsuFreePlayVoidIsolationPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_ritsu_free_play_void_isolation";
    public static string Description => "求解模拟不写入 RitsuLib 卡牌免费状态";

    public static ModPatchTarget[] GetTargets() =>
    [
        Target(nameof(FreePlayBindingRegistry.MarkCardFreeNextPlay), typeof(CardModel)),
        Target(nameof(FreePlayBindingRegistry.MarkCardFreeThisTurn), typeof(CardModel)),
        Target("MarkCardBaseCostsFreeThisTurn", typeof(CardModel)),
        Target("MarkCardBaseCostsFreeForRestOfTurn", typeof(CardModel)),
        Target("MarkCardBaseCostsFreeThisCombat", typeof(CardModel)),
        Target(nameof(FreePlayBindingRegistry.MarkCardFreeThisCombat), typeof(CardModel)),
        Target(nameof(FreePlayBindingRegistry.MarkCurrentPlayFree), typeof(CardPlay)),
    ];

    public static bool Prefix()
    {
        if (!SimulationNotificationIsolation.IsActive)
            return true;
        SimulationNotificationIsolation.LogSuppression("RitsuFreePlayState");
        return false;
    }

    private static ModPatchTarget Target(string name, params Type[] parameters)
        => new(typeof(FreePlayBindingRegistry), name, parameters);
}

internal sealed class RitsuFreePlayBoolIsolationPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_ritsu_free_play_bool_isolation";
    public static string Description => "求解模拟直接使用内置费用状态";

    public static ModPatchTarget[] GetTargets() =>
    [
        Target(nameof(FreePlayBindingRegistry.IsFreeForPlay), typeof(CardPlay)),
        Target(nameof(FreePlayBindingRegistry.IsCardFreeForUpcomingPlay), typeof(CardModel)),
        Target(nameof(FreePlayBindingRegistry.ClearCardFreeThisTurn), typeof(CardModel)),
        Target(nameof(FreePlayBindingRegistry.ClearCardFreeAfterPlayed), typeof(CardModel)),
    ];

    public static bool Prefix(ref bool __result)
    {
        if (!SimulationNotificationIsolation.IsActive)
            return true;
        __result = false;
        SimulationNotificationIsolation.LogSuppression("RitsuFreePlayQuery");
        return false;
    }

    private static ModPatchTarget Target(string name, params Type[] parameters)
        => new(typeof(FreePlayBindingRegistry), name, parameters);
}

internal sealed class RitsuFreePlayResolveIsolationPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_ritsu_free_play_resolve_isolation";
    public static string Description => "求解模拟不缓存 RitsuLib 出牌费用解析";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(
            typeof(FreePlayBindingRegistry),
            nameof(FreePlayBindingRegistry.Resolve),
            [typeof(CardPlay)]),
    ];

    public static bool Prefix(CardPlay play, ref FreePlayResolution __result)
    {
        if (!SimulationNotificationIsolation.IsActive)
            return true;
        __result = new FreePlayResolution(play.IsAutoPlay, false, false);
        SimulationNotificationIsolation.LogSuppression("RitsuFreePlayResolve");
        return false;
    }
}

// Backport source: CombatSolver 0.41.0
//   upstream path: src/Search/PowerCardValuation/Cards/Silent/SilentPowerRoutePolicy.cs
//   upstream commit: 0e6cc2df342ed300c854013a7ec667eae4f58861
// Namespace adapted from CombatSolver to CoopBots.Kernel.Vendor.PowerSync; body unchanged.
namespace CoopBots.Kernel.Vendor.PowerSync;

/// <summary>
/// 静默猎手逐卡路线政策。只登记数据，不承载搜索状态；公共准入顺序由
/// <see cref="PowerRouteAdmission" /> 决定。卡牌身份使用稳定运行时 CardId。
/// </summary>
internal static class SilentPowerRoutePolicy
{
    internal static PowerCommitmentFamily FamilyFor(string cardId)
        => cardId switch
        {
            "ABRASIVE" or "AFTERIMAGE" or "FOOTWORK" or "WRAITH_FORM"
                => PowerCommitmentFamily.DefenseEfficiency,
            "ACCURACY" or "FAN_OF_KNIVES" or "INFINITE_BLADES" or "PHANTOM_BLADES"
                => PowerCommitmentFamily.ShivEngine,
            "ACCELERANT" or "ENVENOM" or "NOXIOUS_FUMES"
                => PowerCommitmentFamily.PoisonEngine,
            "MASTER_PLANNER" or "SPEEDSTER" or "TOOLS_OF_THE_TRADE" or "WELL_LAID_PLANS"
                => PowerCommitmentFamily.HandEngine,
            "SERPENT_FORM" or "TRACKING"
                => PowerCommitmentFamily.DamageEngine,
            _ => PowerCommitmentFamily.None,
        };

    internal static PowerRouteAdmissionPolicy For(string cardId)
        => cardId switch
        {
            "ABRASIVE" => new(
                PowerRoutePriority.Low,
                PreferFreeActivation: true),
            "ACCELERANT" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "ACCURACY" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "AFTERIMAGE" => new(
                PowerRoutePriority.Strong,
                MinimumProjection: 5),
            "ENVENOM" => new(
                PowerRoutePriority.Low,
                RequireFreeOrSpareActivation: true),
            "FAN_OF_KNIVES" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "FOOTWORK" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "INFINITE_BLADES" => new(
                PowerRoutePriority.Low,
                AllowTriggerBackedProjectionFloor: true,
                RequireFreeOrSpareActivation: true),
            "MASTER_PLANNER" => new(
                PowerRoutePriority.Normal,
                RequirePositiveProjection: true),
            "NOXIOUS_FUMES" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "PHANTOM_BLADES" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "SERPENT_FORM" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "SPEEDSTER" => new(
                PowerRoutePriority.Normal,
                AllowTriggerBackedProjectionFloor: true),
            "TOOLS_OF_THE_TRADE" => new(
                PowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            "TRACKING" => new(
                PowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            "WELL_LAID_PLANS" => new(
                PowerRoutePriority.Dedicated,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            "WRAITH_FORM" => new(
                PowerRoutePriority.Strong,
                RequireImmediateDefenseGain: true),
            _ => default,
        };
}

using HarmonyLib;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots.Building;

[HarmonyPatch(typeof(RestSiteRoom), "EnterInternal")]
internal static class TeamBuildingRestEntryPatch
{
    private static void Prefix(IRunState? runState)
    {
        if (runState is not null) TeamBuildingContext.Capture(runState, runState.Players);
    }
}

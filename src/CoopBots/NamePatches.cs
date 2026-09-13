using HarmonyLib;
using MegaCrit.Sts2.Core.Platform;

namespace CoopBots;

[HarmonyPatch(typeof(PlatformUtil), nameof(PlatformUtil.GetPlayerNameRaw))]
internal static class BotNamePatch
{
    private static bool Prefix(ulong playerId, ref string __result)
    {
        if (!BotRegistry.IsBot(playerId))
            return true;

        __result = BotRegistry.DisplayName(playerId);
        return false;
    }
}

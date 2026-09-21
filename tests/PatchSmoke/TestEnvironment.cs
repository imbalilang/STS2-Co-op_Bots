using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

/// <summary>
/// The console host the whole harness runs inside.
///
/// There is no Godot native host in this process, and the game models reach for
/// one on their way through saving, logging and combat state. Stubbing exactly
/// those four entry points — and nothing about cards, hooks or combat rules —
/// is what lets the real models run headlessly.
///
/// Extracted from CooperativeScenarios so the draft sweep can also run on its
/// own (--deck-sim-only). That path exists because the regression suite pins
/// several BuildValue calibrations: an experiment that moves one of them would
/// otherwise abort the suite before the sweep it was meant to measure ever ran.
/// </summary>
internal static class TestEnvironment
{
    private static bool _installed;

    internal static void Ensure()
    {
        if (_installed) return;
        _installed = true;

        TestMode.IsOn = true;
        // PIN THE DECISION POLICY TO THE SEGMENTED SEARCH. Since 0.38.0 the shipped default is
        // the roll-out tournament on an all-bot table, and nearly every planner fixture here
        // drives one bot in its own combat — which IS an all-bot table. Those fixtures guard the
        // segmented search (its deploy/replay/drift/sentinel behaviour), so they must keep
        // running it; the tournament has its own suite in KernelRolloutScenarios, and
        // ChoicePolicyScenarios asserts which one the default picks.
        CoopBots.KernelCombatPlanner.TournamentOverride = false;
        typeof(MegaCrit.Sts2.Core.Modding.ModManager).GetProperty("State")!.SetValue(null,
            MegaCrit.Sts2.Core.Modding.ModManagerState.Skipped);
        var harness = new Harmony("coopbots.test.console");
        harness.Patch(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Saves.SaveManager), "Instance"),
            prefix: new HarmonyMethod(typeof(TestEnvironment), nameof(NoSaveAccess)));
        harness.Patch(AccessTools.PropertyGetter(typeof(CombatManager), "IsInProgress"),
            prefix: new HarmonyMethod(typeof(TestEnvironment), nameof(CombatInProgress)));
        harness.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Logging.ConsoleLogPrinter), "Print"),
            prefix: new HarmonyMethod(typeof(TestEnvironment), nameof(SuppressNativePrint)));
        harness.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Logging.Logger), "GetIsRunningFromGodotEditor"),
            prefix: new HarmonyMethod(typeof(TestEnvironment), nameof(ConsoleHost)));
        MegaCrit.Sts2.Core.Modding.AssemblyInfo.Init();
        ModelDb.Init(typeof(AbstractModel).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(AbstractModel))
                && type.GetConstructor(Type.EmptyTypes) is not null)
            .ToArray());
        MegaCrit.Sts2.Core.Multiplayer.Serialization.ModelIdSerializationCache.Init();
        ModelDb.InitIds();
    }

    private static bool ConsoleHost(ref bool __result) { __result = false; return false; }
    private static bool SuppressNativePrint() => false;
    private static bool NoSaveAccess(ref MegaCrit.Sts2.Core.Saves.SaveManager? __result) { __result = null; return false; }
    private static bool CombatInProgress(ref bool __result) { __result = true; return false; }
}

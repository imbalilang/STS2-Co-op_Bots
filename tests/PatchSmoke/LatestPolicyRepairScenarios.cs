using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class LatestPolicyRepairScenarios
{
    // R2 2026-10-07: production mutations each exited 1, restored runs exited 0.
    // System.Exception: Latest policy repairs: pending enemy search must expose only Disintegration
    // System.Exception: Latest policy repairs: last-card Brand must accept the native empty exhaust selection
    // Commands/raw logs: work/decision-repair-20261007/root-r2-results.json.
    static bool Format(ref string __result) { __result = ""; return false; }
    static void Check(bool ok, string why) { if (!ok) throw new Exception("Latest policy repairs: " + why); }
    internal static void Run()
    {
        TestEnvironment.Ensure();
        var harmony = new Harmony("coopbots.latest-policy-repairs");
        harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(LatestPolicyRepairScenarios), nameof(Format)));
        try
        {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 128);
        var c = new CombatState(runState: RunState.CreateForTest([p], seed: "LATEST-POLICY-REPAIRS"));
        p.ResetCombatState(); c.AddPlayer(p);
        var demon = c.CreateCreature(ModelDb.Monster<KnowledgeDemon>().ToMutable(), CombatSide.Enemy, "0");
        c.AddCreature(demon); demon.Monster!.SetUpForCombat();
        // This headless fixture uses identity labels; no UI localization runtime.
        var names = (SolverDisplayNames)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SolverDisplayNames));
        AccessTools.Field(typeof(SolverDisplayNames), "_cards").SetValue(names, new Dictionary<(string, int), string>());
        foreach (var alternative in new[] { "MIND_ROT", "SLOTH", "WASTE_AWAY" })
        {
            var request = new KnowledgeDemonChoiceRequest(demon, 0, "KNOWLEDGE_DEMON:0:0", [alternative, "DISINTEGRATION"]);
            var branches = KnowledgeDemonChoiceSupport.BuildChoices(request, names);
            // R2: restoring all-option enumeration must fail this assertion.
            // Raw failure/commands and restored GREEN: work/decision-repair-20261007/root-r2-results.json.
            Check(branches.Count == 1 && branches[0].Cards.Single().CardId == "DISINTEGRATION",
                "pending enemy search must expose only Disintegration");
        }
        Check(KernelKnowledgeChoice.Select(p, [c.CreateCard<Disintegration>(p), c.CreateCard<StrikeIronclad>(p)]) is null,
            "fixed curse policy must leave unrelated screens alone");
        var empty = new KernelChoice(p.NetId, "BRAND", "Exhaust", PileType.Hand, "", 0, 0, []);
        // R2: removing native count clamping must fail the last-card Brand assertion.
        Check(empty.Resolve(p.NetId, [], 1, 1) is { Count: 0 },
            "last-card Brand must accept the native empty exhaust selection");
        var strike = c.CreateCard<StrikeIronclad>(p);
        Check(empty.Resolve(p.NetId, [strike], 1, 1) is null,
            "empty transaction must not erase a required available choice");
        Check(empty.Resolve(p.NetId + 1, [], 1, 1) is null,
            "empty choice must retain owner validation");
        var keyed = new KernelChoice(p.NetId, "SURVIVOR", "Discard", PileType.Hand, "", 1, 1,
            [new KernelChoiceCard(strike.Id.Entry, strike.CurrentUpgradeLevel, CardChoiceSupport.ChoiceCardKey(strike), 0)]);
        Check(keyed.Resolve(p.NetId, [strike], 1, 1)?.Single() == strike
            && keyed.Resolve(p.NetId, [c.CreateCard<DefendIronclad>(p)], 1, 1) is null,
            "normal planned selections must retain complete card identity validation");
        Console.WriteLine("PASS: fixed Knowledge Demon search policy and native empty-choice transaction validation.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
}

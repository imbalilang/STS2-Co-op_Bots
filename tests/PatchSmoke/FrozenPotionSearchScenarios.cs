using System.Reflection;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;

// Actual production R2, 2026-10-25: five controls each built 0 -> named
// runtime RED 1 -> byte-exact source restoration -> build 0 -> GREEN 0.
// Commands, hashes and raw logs: work/stutter-frozen-potions-r2/receipts.json.
// inventory: System.InvalidOperationException: Frozen potion search: old background root must retain its original bottle order after live removal and replacement
// owner-permission / bottle-usability: System.InvalidOperationException: Frozen potion search: frozen worker must use captured owner permission and bottle usability without native hooks
// target-rules: System.InvalidOperationException: Frozen potion search: boss opportunity must use frozen target HP after live target death
// dynamic-dose: System.InvalidOperationException: Frozen potion search: old root must preserve captured bottle dose while a new root observes changed native dynamic values
// Full-suite isolation repair: a prewarmed Capture had inlined the native
// owner getter before late Harmony instrumentation. Short warm-usability
// reproduced RED: owner=True usable=0 hooks=1 nativeOwner=False nativeBottle=False.
// Actual owner setter/queued state plus FoulPotion's unchanged native custom
// rule now pass both cold and warm paths. Follow-up physical production R2:
// work/stutter-frozen-potions-r2-isolation/receipts.json (owner-permission,
// bottle-usability, capture-custom-usability), all build0/RED1/restore/build0/GREEN0.
// capture-custom-usability: System.InvalidOperationException: Frozen potion search: new capture must observe changed native owner and bottle usability
internal static class FrozenPotionSearchScenarios
{
    private static bool? foulInCombat;
    private static MegaCrit.Sts2.Core.Entities.Players.Player? observedOwner;
    private static PotionModel? observedBottle;
    private static int nativeReads;

    internal static void Run(string? selected = null)
    {
        TestEnvironment.Ensure();
        var harmony = new Harmony("coopbots.frozen-potion-search");
        harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(FrozenPotionSearchScenarios), nameof(Format)));
        if (selected == "warm-usability")
        {
            // Reproduce the complete-suite order: Capture's Release lambdas
            // have already been JIT compiled before getter instrumentation.
            var (warmCombat, warmOwner, _) = KernelPlanningStateScenarios.Board();
            warmOwner.AddPotionInternal(ModelDb.Potion<StrengthPotion>().ToMutable(), silent: true);
            for (var i = 0; i < 400; i++)
            {
                var warm = KernelSession.Capture(warmCombat);
                _ = warm.CanUsePotions(warmOwner); _ = warm.UsablePotions(warmOwner);
            }
            Thread.Sleep(200);
            for (var i = 0; i < 40; i++)
            {
                var warm = KernelSession.Capture(warmCombat);
                _ = warm.CanUsePotions(warmOwner); _ = warm.UsablePotions(warmOwner);
            }
            selected = "usability";
        }
        harmony.Patch(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Players.Player), "CanUseOrRemovePotions"),
            prefix: new HarmonyMethod(typeof(FrozenPotionSearchScenarios), nameof(OwnerUsability)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(FoulPotion), "PassesCustomUsabilityCheck"),
            prefix: new HarmonyMethod(typeof(FrozenPotionSearchScenarios), nameof(BottleUsability)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Combat.CombatManager), "IsInProgress"),
            prefix: new HarmonyMethod(typeof(FrozenPotionSearchScenarios), nameof(FoulCombat)) { priority = Priority.First });
        try
        {
            foreach (var test in new (string Name, Action Body)[] {
                ("inventory", Inventory), ("usability", Usability), ("targets", Targets) })
                if (selected is null || selected == test.Name)
                { test.Body(); Console.WriteLine("PASS: frozen potion search " + test.Name); }
        }
        finally { foulInCombat = null; observedOwner = null; observedBottle = null; harmony.UnpatchAll(harmony.Id); }
    }

    private static bool Format(ref string __result) { __result = "fixture"; return false; }
    private static void OwnerUsability(MegaCrit.Sts2.Core.Entities.Players.Player __instance)
    { if (ReferenceEquals(__instance, observedOwner)) Interlocked.Increment(ref nativeReads); }
    private static void BottleUsability(PotionModel __instance)
    { if (ReferenceEquals(__instance, observedBottle)) Interlocked.Increment(ref nativeReads); }
    private static bool FoulCombat(ref bool __result)
    { if (foulInCombat is not { } value) return true; __result = value; return false; }
    private static IDisposable Native(object combat)
        => (IDisposable)Activator.CreateInstance(AccessTools.Inner(typeof(KernelPlanningFactsScenarios), "NativeFactsState"),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [combat], null)!;
    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException("Frozen potion search: " + message); }

    private static void Inventory()
    {
        var (combat, owner, enemy) = KernelPlanningStateScenarios.Board();
        using var native = Native(combat);
        var fire = ModelDb.Potion<FirePotion>().ToMutable();
        var strength = ModelDb.Potion<StrengthPotion>().ToMutable();
        owner.AddPotionInternal(fire, 0, silent: true); owner.AddPotionInternal(strength, 2, silent: true);
        var root = KernelSession.Capture(combat);
        Check(root.UsablePotions(owner).SequenceEqual(new[] { fire, strength }), "capture preserves physical slot order");
        var dose = KernelPotionOpportunity.Measure(root, fire, enemy, [owner]);
        fire.DynamicVars.Damage.BaseValue += 100;
        Check(dose.FullValue && KernelPotionOpportunity.Measure(root, fire, enemy, [owner]) == dose
            && !KernelPotionOpportunity.Measure(KernelSession.Capture(combat), fire, enemy, [owner]).FullValue,
            "old root must preserve captured bottle dose while a new root observes changed native dynamic values");
        owner.DiscardPotionInternal(fire, silent: true);
        var replacement = ModelDb.Potion<EnergyPotion>().ToMutable(); owner.AddPotionInternal(replacement, 0, silent: true);
        var fresh = KernelSession.Capture(combat);
        Check(Task.Run(() => root.UsablePotions(owner)).GetAwaiter().GetResult().SequenceEqual(new[] { fire, strength }),
            "old background root must retain its original bottle order after live removal and replacement");
        Check(fresh.UsablePotions(owner).SequenceEqual(new[] { replacement, strength }), "new root sees replacement inventory");
        var branch = root.Fork(); var hp = branch.Hp(enemy);
        Check(branch.UsePotion(fire, enemy, out var reason), "old physical potion replays on its captured slot: " + reason);
        Check(branch.Hp(enemy) < hp && branch.UsablePotions(owner).SequenceEqual(new[] { strength })
            && root.UsablePotions(owner).Count == 2 && fresh.UsablePotions(owner).Contains(replacement)
            && ReferenceEquals(owner.GetPotionAtSlotIndex(0), replacement),
            "simulated consumption removes only the captured bottle without consuming its live replacement");
    }

    private static void Usability()
    {
        var (combat, owner, _) = KernelPlanningStateScenarios.Board();
        var potion = ModelDb.Potion<StrengthPotion>().ToMutable(); owner.AddPotionInternal(potion, silent: true);
        var foul = ModelDb.Potion<FoulPotion>().ToMutable(); owner.AddPotionInternal(foul, silent: true);
        observedOwner = owner; observedBottle = foul; foulInCombat = true;
        Check(foul.PassesCustomUsabilityCheck, "native Foul custom rule accepts its in-combat condition");
        var root = KernelSession.Capture(combat);
        var originalPermission = owner.CanUseOrRemovePotions;
        var queueSetter = AccessTools.PropertySetter(typeof(PotionModel), nameof(PotionModel.IsQueued));
        // Change actual native state, rather than relying on a late Harmony
        // getter replacing a field read already inlined into Capture by Tier 1.
        owner.CanUseOrRemovePotions = false;
        queueSetter.Invoke(potion, [true]);
        // FoulPotion's actual custom rule depends on the same already-hooked
        // CombatManager predicate used by the headless harness. Change that
        // dependency, not the bottle getter or production JIT configuration.
        foulInCombat = false;
        Check(!foul.PassesCustomUsabilityCheck, "native Foul custom rule rejects the changed combat condition");
        try
        {
            nativeReads = 0;
            var old = Task.Run(() => root.CanUsePotions(owner)
                && root.UsablePotions(owner).SequenceEqual(new[] { potion, foul })).GetAwaiter().GetResult();
            Check(old && nativeReads == 0, "frozen worker must use captured owner permission and bottle usability without native hooks");
            var fresh = KernelSession.Capture(combat);
            Console.WriteLine($"FROZEN-USABILITY: owner={fresh.CanUsePotions(owner)} usable={fresh.UsablePotions(owner).Count} captureHooks={nativeReads} nativeQueued={potion.IsQueued} foulCustom={foul.PassesCustomUsabilityCheck}");
            Check(!fresh.CanUsePotions(owner) && fresh.UsablePotions(owner).Count == 0 && potion.IsQueued,
                "new capture must observe changed native owner and bottle usability");
        }
        finally
        {
            foulInCombat = null; observedOwner = null; observedBottle = null;
            queueSetter.Invoke(potion, [false]);
            owner.CanUseOrRemovePotions = originalPermission;
        }
    }

    private static void Targets()
    {
        var (combat, owner, enemy) = KernelPlanningStateScenarios.Board();
        var fire = ModelDb.Potion<FirePotion>().ToMutable(); owner.AddPotionInternal(fire, silent: true);
        var root = KernelSession.Capture(combat);
        enemy.SetCurrentHpInternal(0);
        var choice = Task.Run(() => KernelPotionOpportunity.Choose(root, [owner], [owner], bossOpening: true)).GetAwaiter().GetResult();
        Check(choice?.Action.Potion == fire && choice.Action.Target == enemy,
            "boss opportunity must use frozen target HP after live target death");
        Check(KernelPotionOpportunity.Choose(KernelSession.Capture(combat), [owner], [owner], bossOpening: true) is null,
            "new root must withdraw a dead target");
    }
}

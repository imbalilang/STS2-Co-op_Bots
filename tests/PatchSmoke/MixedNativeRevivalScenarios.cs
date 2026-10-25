using System.Diagnostics;
using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;

// The actual sts2.dll Adaptable death and RESPAWN_MOVE are driven below.
// Only localization/animation clocks use the shared NativeFactsState fixture.
// A historical card proposal is supplied at the planner boundary; the repair
// request and complete tournament are unmodified production code, not an
// injected EndTurn answer or a timer which invents a replacement enemy.
// R2 RED evidence is appended by the serialized validation owner.
internal static class MixedNativeRevivalScenarios
{
    internal static void Run(string? selected = null)
    {
        if (selected is not null && selected != "native-empty-target-close") return;
        TestEnvironment.Ensure();
        var localization = new Harmony("coopbots.mixed-native-revival.localization");
        localization.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(MixedNativeRevivalScenarios), nameof(Format)));
        var previous = KernelCombatPlanner.TournamentOverride;
        KernelCombatPlanner.TournamentOverride = null;
        try
        {
            NativeEmptyTargetClose();
            Console.WriteLine("PASS: mixed native revival native-empty-target-close");
        }
        finally
        {
            KernelCombatPlanner.TournamentOverride = previous;
            localization.UnpatchAll(localization.Id);
        }
    }

    private static void NativeEmptyTargetClose()
    {
        using var b = new MixedBackgroundReplanScenarios.Board();
        foreach (var old in b.Combat.Enemies.ToArray()) b.Combat.RemoveCreature(old);
        var model = (TestSubject)ModelDb.Monster<TestSubject>().ToMutable();
        var enemy = b.Combat.CreateCreature(model, CombatSide.Enemy, "native-reviving-subject");
        b.Combat.AddCreature(enemy); model.SetUpForCombat();
        ModelDb.Power<AdaptablePower>().ToMutable().ApplyInternal(enemy, 1, true);
        enemy.SetMaxHpInternal(6); enemy.SetCurrentHpInternal(6);
        model.RollMove(b.Combat.Players.Select(player => player.Creature).ToArray());
        var physical = b.Bot.PlayerCombatState!.Hand.Cards[0];
        var root = KernelCombatCapture.Capture(b.Combat);
        var oldAttack = new KernelTeamSearch.Action(b.Bot, physical, enemy,
            CardStateKey: root.CardStateKey(physical));
        var oldScript = new[] { oldAttack };
        var proposal = new TournamentResult(oldAttack,
            TerminalRecord.Capture(root, b.Combat.Players, false, "historical-native-target-proposal"),
            [], 0, 0, "historical-native-target-proposal") {
                Script = oldScript,
                ScriptAliveStates = [b.Combat.Players.Select(player => player.NetId).Order().ToArray()],
                ScriptEnemyStates = [new[] { enemy }]
            };
        Set(b.Planner, "tournamentRoot", root);
        Invoke(b.Planner, "AdoptTournamentScript", proposal, b.Combat);

        // Actual native death installs an unhittable reviving phase. It does
        // not create a replacement during a later frame: only the monster's
        // enemy-side RESPAWN_MOVE brings this same creature back.
        CreatureCmd.Kill(enemy).GetAwaiter().GetResult();
        Check(b.Combat.ContainsCreature(enemy) && enemy.IsDead && !enemy.IsHittable
            && !b.Combat.HittableEnemies.Any(creature => creature.IsAlive)
            && model.NextMove.Id == "RESPAWN_MOVE",
            "the real native Adaptable death must leave no living target until the enemy respawn move");
        Check(b.Bot.PlayerCombatState.Hand.Cards.Contains(physical),
            "the old physical attack must still be in hand before the native revive boundary");

        var firstStatus = b.Planner.Poll(b.Combat, [b.Bot], b.Queue.NextActionId, null,
            true, BotDifficulty.Pro, out var firstDecision);
        Check(!ReferenceEquals(Get(b.Planner, "tournamentScript"), oldScript)
            && firstDecision is null && firstStatus != KernelCombatPlanner.Status.Fallback,
            "a native reviving enemy with no living replacement must retire the impossible old attack instead of parking forever for a target");

        var watch = Stopwatch.StartNew();
        var status = firstStatus;
        while (b.Planner.ConfirmedEndTurn is null && watch.ElapsedMilliseconds < 15000)
        {
            status = b.Planner.Poll(b.Combat, [b.Bot], b.Queue.NextActionId, null,
                true, BotDifficulty.Pro, out var decision);
            Check(status != KernelCombatPlanner.Status.Fallback && decision is null,
                "the real empty-target repair must use the full tournament and cannot play an attack against a not-yet-revived enemy");
            if (b.Planner.ConfirmedEndTurn is null) Thread.Sleep(1);
        }
        var repaired = (IReadOnlyList<KernelTeamSearch.Action>?)Get(b.Planner, "tournamentScript");
        var cursor = (int)Get(b.Planner, "tournamentScriptIndex")!;
        Check(status == KernelCombatPlanner.Status.Ready && ReferenceEquals(b.Planner.ConfirmedEndTurn, b.Bot)
            && repaired is not null && cursor < repaired.Count && repaired[cursor].EndTurn
            && (int)Get(b.Planner, "tournamentPasses")! > 0
            && b.Bot.PlayerCombatState.Hand.Cards.Contains(physical)
            && enemy.IsDead && model.NextMove.Id == "RESPAWN_MOVE",
            "the complete native mixed repair must return EndTurn before revival rather than waiting or moving the old attack across the side boundary");

        // Prove the native causal boundary, independently of planner scheduling:
        // the same native creature returns only when its actual enemy move is
        // executed. No AddEnemy, SetCurrentHp or fake 'next frame' callback.
        b.Planner.Reset(newCombat: true);
        var drained = Stopwatch.StartNew();
        while (!KernelBackgroundOperation.IsIdle && drained.ElapsedMilliseconds < 5000) Thread.Sleep(1);
        Check(KernelBackgroundOperation.IsIdle, "the full search worker must retire before the native respawn control");
        b.Combat.CurrentSide = CombatSide.Enemy;
        model.NextMove.PerformMove(b.Combat.Players.Select(player => player.Creature).ToArray()).GetAwaiter().GetResult();
        Check(enemy.IsAlive && enemy.IsHittable && b.Combat.ContainsCreature(enemy)
            && b.Combat.HittableEnemies.Contains(enemy),
            "the actual enemy-side native respawn move must revive the original creature without a fabricated replacement target");
        Console.WriteLine($"MIXED NATIVE REVIVE: repairMs={watch.Elapsed.TotalMilliseconds:F1} "
            + $"revivedHp={enemy.CurrentHp} cardStillInHand={b.Bot.PlayerCombatState.Hand.Cards.Contains(physical)}");
    }

    private static object? Get(object obj, string name) => AccessTools.Field(obj.GetType(), name).GetValue(obj);
    private static void Set(object obj, string name, object? value) => AccessTools.Field(obj.GetType(), name).SetValue(obj, value);
    private static object? Invoke(object obj, string name, params object?[] args) => AccessTools.Method(obj.GetType(), name).Invoke(obj, args);
    private static bool Format(ref string __result) { __result = "fixture"; return false; }
    private static void Check(bool condition, string text)
    { if (!condition) throw new InvalidOperationException("Mixed native revival: " + text); }
}

// R2 actual 2026-10-25: native-empty-target-close build0 / named RED1 / exact restore / build0 / GREEN0.
// Private receipt: work/mixed-quality-fix-20261025T083140Z/target-r2/receipts.json
// System.InvalidOperationException: Mixed native revival: a native reviving enemy with no living replacement must retire the impossible old attack instead of parking forever for a target

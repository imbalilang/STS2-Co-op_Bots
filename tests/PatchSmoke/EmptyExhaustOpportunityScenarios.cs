// Two physical source faults, private force-Rebuild0 / named runtimeRED1 / exact byte restore
// / force-Rebuild0 / native16 GREEN0. Full actual DLLs and raw logs:
// work/szg-repair-20261008T124624/perf-audit/empty-exhaust-opportunity/r2-results.json
// Compiled fixture SHA256 before these comment-only annotations: B7437DE1BE34EDCD85B3952EE21577604A4740AFF2F7644843DE421CB8D67D9A
// empty-nonzero: System.InvalidOperationException: Empty exhaust opportunity: complete IEEE opportunity must equal the original independent path: fork/temporary=False/existing=False expected=0000000000000000 actual=3FF0000000000000
// ignore-new-exhaust: System.InvalidOperationException: Empty exhaust opportunity: complete IEEE opportunity must equal the original independent path: FiendFire/temporary=False/existing=False expected=4031000000000000 actual=0000000000000000
using System.Diagnostics;
using System.Text.Json;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

internal static class EmptyExhaustOpportunityScenarios
{
    private static bool watching;
    private static readonly List<string> calls = [];
    internal static void Run(bool baseline = false, bool benchmark = false)
    {
        TestEnvironment.Ensure();
        var h = new Harmony("coopbots.empty-exhaust-proof");
        h.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(EmptyExhaustOpportunityScenarios), nameof(Format)));
        h.Patch(AccessTools.Method(typeof(KernelSession), "AfterTemporaryStatExpiry"),
            prefix: new HarmonyMethod(typeof(EmptyExhaustOpportunityScenarios), nameof(ExpiryCall)));
        h.Patch(AccessTools.Method(typeof(PersistentPowerSupport), "GetModifiedMaxEnergy"),
            prefix: new HarmonyMethod(typeof(EmptyExhaustOpportunityScenarios), nameof(EnergyCall)));
        h.Patch(AccessTools.Method(typeof(KernelRelicChoices), "FutureExhaustValue"),
            prefix: new HarmonyMethod(typeof(EmptyExhaustOpportunityScenarios), nameof(ExhaustCall)));
        var benches = new List<(string Name, KernelSession Root, KernelSession Selected, Player Owner)>();
        try
        {
            foreach (var temporary in new[] { false, true })
            foreach (var existing in new[] { false, true })
            foreach (var kind in new[] { "fork", "Prepared", "Survivor", "FiendFire" })
            {
                var (party, combat, enemy) = (ValueTuple<Player[], MegaCrit.Sts2.Core.Combat.CombatState, Creature>)
                    AccessTools.Method(typeof(ExpiryValueScenarios), "Board").Invoke(null, null)!;
                var owner = party[0];
                CardModel? source = kind switch {
                    "Prepared" => combat.CreateCard<Prepared>(owner),
                    "Survivor" => combat.CreateCard<Survivor>(owner),
                    "FiendFire" => combat.CreateCard<FiendFire>(owner),
                    _ => null
                };
                if (source is not null) owner.PlayerCombatState!.Hand.AddInternal(source);
                owner.PlayerCombatState!.Hand.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
                owner.PlayerCombatState.Hand.AddInternal(combat.CreateCard<DefendIronclad>(owner));
                owner.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(owner));
                owner.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<DefendIronclad>(owner));
                if (existing) owner.PlayerCombatState.ExhaustPile.AddInternal(combat.CreateCard<Bash>(owner));
                if (temporary) ModelDb.Power<ArtifactPower>().ToMutable().ApplyInternal(owner.Creature, 1, true);
                var root = KernelSession.Capture(combat);
                if (temporary)
                {
                    root = root.Fork();
                    var projected = (SimulatedCombatState)root.Simulator.State.CombatState;
                    projected.ApplyTemporaryStrengthGain<SetupStrikePower>(owner.Creature, 3, owner.Creature);
                    projected.ApplyTemporaryDexterity<AnticipatePower>(owner.Creature, 2, owner.Creature);
                    PowerLifecycleSupport.ResolvePowerAmountChanges(root.Simulator, projected);
                }
                KernelSession selected;
                IReadOnlyList<KernelChoice> menus = [];
                using (KernelChoiceContinuation.Enter())
                {
                    if (source is null) selected = root.Fork();
                    else
                    {
                        var branch = root.CardBranches(source, source is FiendFire ? enemy : null, 1).Single();
                        Check(branch.Boundary.Length == 0, "formal card must finish its native transaction: " + kind + "/" + branch.Boundary);
                        selected = branch.State; menus = branch.Choices;
                    }
                }
                var name = $"{kind}/temporary={temporary}/existing={existing}";
                var oldState = Full(root, party, enemy); var selectedState = Full(selected, party, enemy);
                var menuState = JsonSerializer.Serialize(menus);
                var originals = root.Simulator.State.GetPlayerCombatState(owner).ExhaustPile.Cards.Select(c => c.Original).ToHashSet();
                var newly = selected.Simulator.State.GetPlayerCombatState(owner).ExhaustPile.Cards.Where(c => !originals.Contains(c.Original)).ToArray();
                calls.Clear(); watching = true;
                var expected = OriginalOpportunity(root, selected, owner);
                watching = false; var expectedCalls = calls.ToArray();
                calls.Clear(); watching = true;
                var actual = root.ExhaustOpportunityCost(selected, owner);
                watching = false; var actualCalls = calls.ToArray();
                Check(Bits(actual) == Bits(expected), "complete IEEE opportunity must equal the original independent path: " + name + " expected=" + Bits(expected) + " actual=" + Bits(actual));
                Check(Full(root, party, enemy) == oldState && Full(selected, party, enemy) == selectedState
                    && JsonSerializer.Serialize(menus) == menuState,
                    "opportunity must preserve parent/selected native states, powers, RNG and resolved menu attributes: " + name);
                if (newly.Length == 0)
                {
                    Check(Bits(actual) == "0000000000000000", "no newly exhausted Original card must return exact positive zero: " + name);
                    if (!baseline) Check(actualCalls.Length == 0, "empty exhaust difference must skip expiry and max-energy/native valuation calls: " + name + "/" + string.Join(',', actualCalls));
                    Check(expectedCalls.SequenceEqual(new[] { "expiry", "energy/710" }), "before oracle observation must remain active for the empty path: " + name);
                    if (kind == "fork") benches.Add((name, root, selected, owner));
                }
                else
                {
                    Check(kind == "FiendFire" && actual > 0 && newly.Length == 3,
                        "actual exhaust must retain all three new native exhaust opportunities: " + name);
                    Check(expectedCalls.SequenceEqual(actualCalls) && actualCalls.Length == 5
                        && actualCalls.Skip(2).SequenceEqual(newly.Select(c => "exhaust/" + c.Preview.Id.Entry)),
                        "actual exhaust must keep original expiry, energy and selected-pile native valuation order: " + name + "/" + string.Join(',', actualCalls));
                }
                if (kind is "Prepared" or "Survivor") Check(menus.Count == 1 && menus[0].Effect == "Discard",
                    "formal discard must retain the actual native menu: " + name);
                Console.WriteLine($"PASS: empty-exhaust {name} added={newly.Length} IEEE={Bits(actual)} oldCalls={string.Join(',', expectedCalls)} actualCalls={string.Join(',', actualCalls)}");
            }
        }
        finally { watching = false; h.UnpatchAll(h.Id); }
        if (benchmark)
            foreach (var board in benches)
            {
                for (var i = 0; i < 100; i++) board.Root.ExhaustOpportunityCost(board.Selected, board.Owner);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var allocated = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew(); double sum = 0;
                for (var i = 0; i < 10000; i++) sum += board.Root.ExhaustOpportunityCost(board.Selected, board.Owner);
                clock.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Check(sum == 0, "fixed-work no-exhaust sum must remain zero");
                Console.WriteLine($"BENCH {board.Name} calls=10000 ms={clock.Elapsed.TotalMilliseconds:F3} allocated={allocated} sum={Bits(sum)}");
            }
        Console.WriteLine("PASS: empty-exhaust complete native16 and original IEEE/state/menu/valuation-order controls");
    }

    // Independent unchanged pre-optimization implementation; this is an oracle,
    // not a copy of the early-return predicate under test.
    private static double OriginalOpportunity(KernelSession root, KernelSession selected, Player owner)
    {
        var before = root.Simulator.State.GetPlayerCombatState(owner).ExhaustPile.Cards.Select(c => c.Original).ToHashSet();
        var future = root.AfterTemporaryStatExpiry();
        var available = Math.Max(future.Energy(owner), PersistentPowerSupport.GetModifiedMaxEnergy((SimulatedCombatState)future.Simulator.State.CombatState, owner));
        using var isolation = SimulationNotificationIsolation.Enter();
        return selected.Simulator.State.GetPlayerCombatState(owner).ExhaustPile.Cards.Where(c => !before.Contains(c.Original))
            .Sum(c => KernelRelicChoices.FutureExhaustValue(future.Simulator, owner, future.Simulator.State.FindCard(c.Original) ?? c, available));
    }
    private static string Full(KernelSession state, Player[] players, Creature enemy)
        => state.PartyStateText() + "/" + state.RoundsAdvanced + "/" + state.EnemyPhaseCompleted + "/"
            + string.Join('|', players.Select(p => p.Creature).Append(enemy).Select(owner => string.Join(';',
                ((SimulatedCombatState)state.Simulator.State.CombatState).EffectivePowers().Where(p => p.Owner == owner)
                    .OrderBy(p => p.Id.Entry).Select(p => $"{p.Id.Entry}:{p.Amount}:{p.TypeForCurrentAmount}:{p.SkipNextDurationTick}:{p.AmountOnTurnStart}:{p.Applier?.CombatId}"))));
    private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("X16");
    private static void ExpiryCall() { if (watching) calls.Add("expiry"); }
    private static void EnergyCall(Player player) { if (watching) calls.Add("energy/" + player.NetId); }
    private static void ExhaustCall(PredictedCard card) { if (watching) calls.Add("exhaust/" + card.Preview.Id.Entry); }
    private static bool Format(ref string __result) { __result = ""; return false; }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException("Empty exhaust opportunity: " + message); }
}

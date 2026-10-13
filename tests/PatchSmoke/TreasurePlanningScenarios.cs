using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CoopBots;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class TreasurePlanningScenarios
{
    // R2 fault injections (restored):
    // Treasure planning: four-by-four assignment repeated valuation: 224
    // Treasure planning: a completed vote recomputed the score table
    // Treasure planning: changed player state reused a stale score table
    // Treasure planning: changed chest reused a stale score table
    // Treasure planning: clear retained a completed chest table
    private static int reads;
    private static void Count() => reads++;
    private static void Check(bool ok, string why) { if (!ok) throw new Exception("Treasure planning: " + why); }
    internal static void Run()
    {
        var players = Enumerable.Range(1, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)i)).ToArray();
        RunState.CreateForTest(players, ascensionLevel: 10, seed: "CHEST-DELAY");
        foreach (var p in players)
        {
            typeof(Player).GetField("<Character>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(p, ModelDb.Character<Ironclad>());
            p.MaxEnergy = 3; p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(40 + (int)p.NetId * 5);
            foreach (var c in p.Character.StartingDeck) p.Deck.AddInternal(ConstructionParallelScenarios.Card(c.Id.Entry, p));
        }
        RelicModel[] relics = [ModelDb.Relic<PenNib>().ToMutable(), ModelDb.Relic<Akabeko>().ToMutable(),
            ModelDb.Relic<Whetstone>().ToMutable(), ModelDb.Relic<RegalPillow>().ToMutable()];
        var json = new JsonSerializerOptions { IncludeFields = true };
        string State() => ConstructionDecision.Fingerprint(players[0]) + JsonSerializer.Serialize(players[0].RunState.Rng.ToSerializable(), json)
            + string.Join('|', players.Select(p => JsonSerializer.Serialize(p.PlayerRng.ToSerializable(), json)));
        var before = State();
        var harmony = new Harmony("coopbots.treasure-planning-test");
        harmony.Patch(AccessTools.Method(typeof(HumanCoopAdvisor), nameof(HumanCoopAdvisor.RelicValue)),
            prefix: new HarmonyMethod(typeof(TreasurePlanningScenarios), nameof(Count)));
        var planner = new TreasureRelicPlanner();
        try
        {
            reads = 0;
            var expected = TeamCoordinator.AssignRelics(players, relics, new HashSet<int>());
            Check(reads == 16, "four-by-four assignment repeated valuation: " + reads);
            using var pump = new ConstructionParallelScenarios.Pump();
            reads = 0;
            var timer = Stopwatch.StartNew();
            Check(!planner.TryAssign(players, relics, players, new HashSet<int>(), out _), "initial chest calculation blocked until complete");
            var initialCallMs = timer.Elapsed.TotalMilliseconds;
            Dictionary<ulong, int?> Poll(IReadOnlyList<Player> waiting, HashSet<int> reserved) => pump.Run(async () =>
            {
                while (true)
                {
                    if (planner.TryAssign(players, relics, waiting, reserved, out var result)) return result;
                    await Task.Yield();
                }
            });
            var actual = Poll(players, new());
            var firstReadyMs = timer.Elapsed.TotalMilliseconds;
            Check(expected.OrderBy(x => x.Key).SequenceEqual(actual.OrderBy(x => x.Key)), "async allocation differs from synchronous optimum");
            Check(pump.Ticks > 0, "chest calculation never yielded");
            var cachedReads = reads;
            var reserved = new HashSet<int> { actual[players[0].NetId]!.Value };
            var remaining = Poll(players.Skip(1).ToArray(), reserved);
            Check(reads == cachedReads, "a completed vote recomputed the score table");
            Check(remaining.Values.All(i => !i.HasValue || !reserved.Contains(i.Value)), "reserved human/previous vote was contested");
            Check(remaining.Values.Where(i => i.HasValue).Distinct().Count() == remaining.Values.Count(i => i.HasValue), "bots contested a relic");
            Check(State() == before, "chest preview mutated live state or RNG");
            players[0].Creature.SetCurrentHpInternal(10);
            _ = Poll(players, new());
            Check(reads > cachedReads, "changed player state reused a stale score table");
            var changedReads = reads;
            relics[0] = ModelDb.Relic<Anchor>().ToMutable();
            _ = Poll(players, new());
            Check(reads > changedReads, "changed chest reused a stale score table");
            planner.Clear();
            Check(!planner.TryAssign(players, relics, players, new HashSet<int>(), out _), "clear retained a completed chest table");
            planner.Clear();
            _ = Poll(players, new());
            Console.WriteLine($"PASS: treasure scores 16 pairs once per pass; async polling preserves allocation/reservations/RNG, reuses votes, invalidates state/chest and cancels cleanly; initialCallMs={initialCallMs:F1} firstReadyMs={firstReadyMs:F1} totalMs={timer.Elapsed.TotalMilliseconds:F1} maxCallbackMs={pump.MaxCallbackMs:F1}.");
        }
        finally { planner.Clear(); harmony.UnpatchAll(harmony.Id); }
    }
}

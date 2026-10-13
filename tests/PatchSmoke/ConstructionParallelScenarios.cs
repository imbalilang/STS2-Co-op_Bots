using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CoopBots;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class ConstructionParallelScenarios
{
    // R2 2026-10-01, production mutants, each exits 1; restored suite green.
    // Evidence: work/reward-parallel/r2-{score,stale,budget,host,affinity,restore,native,cancel,packet,power}.log.
    // Raw failures: "Construction parallel: async complete score vector changed";
    // "Construction parallel: stale deck result was submitted";
    // "Construction parallel: worker count exceeded selected tier";
    // "Construction parallel: invalid host precision message accepted";
    // "Construction parallel: workers overlap the reserved main core";
    // "Construction parallel: main affinity was not restored";
    // "Construction parallel: native card profiling escaped the game thread";
    // "Construction parallel: cancellation was ignored";
    // "Construction parallel: precision message round trip changed";
    // "Construction parallel: snapshot lost Footwork's upgraded Dexterity".
    private static int mainThread;
    private static void NativeRead()
    {
        if (Environment.CurrentManagedThreadId != mainThread)
            throw new Exception("Construction parallel: native card profiling escaped the game thread");
    }
    internal sealed class Pump : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? previous = Current;
        private readonly ConcurrentQueue<(SendOrPostCallback, object?)> messages = new();
        internal int Ticks;
        internal double MaxCallbackMs;
        internal Pump() => SetSynchronizationContext(this);
        public override void Post(SendOrPostCallback callback, object? state) => messages.Enqueue((callback, state));
        internal T Run<T>(Func<Task<T>> start, Action? whilePending = null)
        {
            var watch = Stopwatch.StartNew();
            var first = Stopwatch.GetTimestamp();
            var task = start();
            MaxCallbackMs = Math.Max(MaxCallbackMs, Stopwatch.GetElapsedTime(first).TotalMilliseconds);
            whilePending?.Invoke();
            while (!task.IsCompleted)
            {
                if (watch.Elapsed.TotalSeconds > 60) throw new Exception("Construction parallel: calculation timed out");
                if (messages.TryDequeue(out var message))
                {
                    var begin = Stopwatch.GetTimestamp(); message.Item1(message.Item2);
                    MaxCallbackMs = Math.Max(MaxCallbackMs, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                }
                Ticks++; Thread.Sleep(1);
            }
            return task.GetAwaiter().GetResult();
        }
        public void Dispose() => SetSynchronizationContext(previous);
    }
    internal static Player Fixture()
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 19));
        typeof(Player).GetField("<Character>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(player, ModelDb.AllCharacters.First(c => c.Id.Entry == "SILENT"));
        RunState.CreateForTest([player], ascensionLevel: 10, seed: "CONSTRUCTION-PARALLEL");
        player.MaxEnergy = 3; player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
        foreach (var c in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(c);
        foreach (var c in player.Character.StartingDeck) player.Deck.AddInternal(Card(c.Id.Entry, player));
        player.Deck.AddInternal(Card("ASCENDERS_BANE", player));
        return player;
    }
    internal static CardModel Card(string id, Player player)
    { var card = ModelDb.AllCards.First(c => c.Id.Entry == id).ToMutable(); card.Owner = player; return card; }
    private static void Equal<T>(T a, T b, string message)
    { if (JsonSerializer.Serialize(a) != JsonSerializer.Serialize(b)) throw new Exception("Construction parallel: " + message); }

    internal static void CanonicalEventReward()
    {
        using var pump = new Pump();
        var player = Fixture();
        var before = ConstructionDecision.Fingerprint(player);
        var canonical = ModelDb.AllCards.First(c => c.Id.Entry == "ABUNDANCE");
        var expected = EventTrade.Card(player, Card("ABUNDANCE", player));
        // R2: removing preview normalization must fail through RunAsync, even
        // though EventTrade.Card catches the original snapshot exception.
        // Observed red: "Construction snapshot failed" ->
        // "Canonical model of type MegaCrit.Sts2.Core.Models.Cards.Abundance used in incorrect place."
        // Evidence: work/neow-stall-20261001/red.log (exit 1).
        var actual = pump.Run(() => ConstructionDecision.RunAsync(player,
            () => EventTrade.Card(player, canonical), () => "neow-dowsing-rod"));
        Equal(actual, expected, "canonical event reward changed score");
        Equal(ConstructionDecision.Fingerprint(player), before, "event preview changed live state");
        if (canonical.IsMutable) throw new Exception("Construction parallel: event template mutated");
        Console.WriteLine("PASS: canonical Neow reward completes async event valuation without changing live state.");
    }

    internal static void Run()
    {
        CanonicalEventReward();
        var previous = KernelCombatPlanner.Perf;
        KernelCombatPlanner.Perf = KernelCombatPlanner.TournamentPerf.High;
        mainThread = Environment.CurrentManagedThreadId;
        var harmony = new Harmony("coopbots.construction.parallel.native-guard");
        harmony.Patch(AccessTools.Method(typeof(CardProfile), nameof(CardProfile.Of)),
            prefix: new HarmonyMethod(typeof(ConstructionParallelScenarios), nameof(NativeRead)));
        try
        {
            using var pump = new Pump();
            var player = Fixture();
            var footwork = Card("FOOTWORK", player);
            var upgradedFootwork = CardProfile.Upgraded(footwork,3)!.Value.Card;
            var defense = Enumerable.Range(0,8).Select(i => Card(i < 2 ? "DEFEND_SILENT" : "STRIKE_SILENT",player)).ToArray();
            if (SustainedDeckValue.ScenarioStrength(defense.Append(upgradedFootwork).ToArray(),3,DeckRobustness.Interference.None)
                <= SustainedDeckValue.ScenarioStrength(defense.Append(footwork).ToArray(),3,DeckRobustness.Interference.None))
                throw new Exception("Construction parallel: snapshot lost Footwork's upgraded Dexterity");
            var offers = new[] { Card("PREPARED", player), Card("ANTICIPATE", player), Card("DAGGER_THROW", player) };
            var expected = BotBrain.ChooseCardReward(player, offers);
            var initialAffinity = Mask();
            var actual = pump.Run(() => BotBrain.ChooseCardRewardAsync(player, offers));
            if (Mask() != initialAffinity) throw new Exception("Construction parallel: main affinity was not restored");
            Equal(actual, expected, "async reward choice changed");
            var deck = player.Deck.Cards.Append(offers[0]).ToArray();
            var score = pump.Run(() => ConstructionDecision.RunAsync(player, () => DeckStrength.Evaluate(deck, player), () => "score"));
            Equal(score, DeckStrength.Evaluate(deck, player), "async complete score vector changed");
            if (pump.Ticks == 0) throw new Exception("Construction parallel: game thread never yielded");
            Console.WriteLine($"PASS: async construction preserves reward and full score; native reads stay on game thread; pumpTicks={pump.Ticks} maxCallbackMs={pump.MaxCallbackMs:F1}.");

            var changed = pump.Run(() => ConstructionDecision.RunAsync(player,
                () => DeckStrength.Evaluate(player.Deck.Cards.ToArray(), player), () => "stale"), () => player.MaxEnergy = 4);
            Equal(changed, DeckStrength.Evaluate(player.Deck.Cards.ToArray(), player), "stale deck result was submitted");
            using var cancel = new CancellationTokenSource();
            try
            {
                pump.Run(() => ConstructionDecision.RunAsync(player, () => DeckStrength.Evaluate(deck, player), () => "cancel", cancel.Token), cancel.Cancel);
                throw new Exception("Construction parallel: cancellation was ignored");
            }
            catch (OperationCanceledException) { }
            var afterCancel = pump.Run(() => ConstructionDecision.RunAsync(player, () => DeckStrength.Evaluate(deck, player), () => "fresh"));
            Equal(afterCancel, DeckStrength.Evaluate(deck, player), "cancelled scope contaminated a later decision");
            Console.WriteLine("PASS: changed state recomputes; cancellation leaves no reusable stale score.");

            var configurations = Enum.GetValues<KernelCombatPlanner.TournamentPerf>().Select(ConstructionBudget.For).ToArray();
            if (!configurations.Select(c => c.Samples).SequenceEqual(new[] {4,8,16,24})
                || !configurations.Select(c => c.Beam).SequenceEqual(new[] {8,16,32,48}))
                throw new Exception("Construction parallel: performance tiers do not scale search precision");
            foreach (var tier in Enum.GetValues<KernelCombatPlanner.TournamentPerf>())
            {
                KernelCombatPlanner.Perf = tier;
                var computed = pump.Run(() => ConstructionDecision.RunAsync(player, () => DeckStrength.Evaluate(deck, player), () => "tier"));
                Equal(computed, DeckStrength.Evaluate(deck, player), "worker ignored selected performance tier");
                if (ConstructionWorkers.WorkerCount > ConstructionBudget.For(tier).Workers)
                    throw new Exception("Construction parallel: worker count exceeded selected tier");
            }
            Console.WriteLine("PASS: low/medium/high/ultra use matching worker and search budgets.");

            if (!ConstructionPerfSync.AcceptHost(7,7,false,0) || ConstructionPerfSync.AcceptHost(8,7,false,0)
                || ConstructionPerfSync.AcceptHost(7,7,true,0) || ConstructionPerfSync.AcceptHost(7,7,false,99))
                throw new Exception("Construction parallel: invalid host precision message accepted");
            var message = new BotConstructionPerfMessage {Request=false, Tier=3, Location=new RunLocation(0,null,2)};
            var writer = new MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter(); message.Serialize(writer);
            var reader = new MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketReader(); reader.Reset(writer.Buffer);
            var decoded = new BotConstructionPerfMessage(); decoded.Deserialize(reader);
            if (decoded.Tier != message.Tier || decoded.Request != message.Request || decoded.Location != message.Location)
                throw new Exception("Construction parallel: precision message round trip changed");
            Console.WriteLine("PASS: construction precision messages preserve tier and location and require the host.");

            KernelCombatPlanner.Perf = KernelCombatPlanner.TournamentPerf.High;
            var originalMask = Mask();
            var masks = pump.Run(async () => {
                using var lease = ConstructionWorkers.ReserveGameCore();
                var mainMask = Mask();
                using var release = new ManualResetEventSlim(false);
                var started = new CountdownEvent(ConstructionWorkers.WorkerCount);
                var tasks = Enumerable.Range(0, ConstructionWorkers.WorkerCount).Select(_ => ConstructionWorkers.Run(() => {
                    var mask = Mask(); started.Signal();
                    if (!release.Wait(10000)) throw new Exception("Construction parallel: worker rendezvous timed out");
                    return (Environment.CurrentManagedThreadId, mask);
                })).ToArray();
                // Test-only timer releases workers; the game thread stays awaitable.
                var releaseTask = Task.Run(() => { if (!started.Wait(10000)) throw new Exception("Construction parallel: workers did not run concurrently"); release.Set(); });
                var workers = await Task.WhenAll(tasks); await releaseTask;
                return (mainMask, workers);
            });
            if (masks.workers.Any(w => w.Item1 == mainThread) || masks.workers.Select(w => w.Item1).Distinct().Count() != ConstructionWorkers.WorkerCount)
                throw new Exception("Construction parallel: workers did not use distinct background threads");
            if (ConstructionWorkers.Policy.Contains("mainPhysicalMask") && masks.workers.Any(w => (w.mask & masks.mainMask) != 0))
                throw new Exception("Construction parallel: workers overlap the reserved main core");
            if (Mask() != originalMask) throw new Exception("Construction parallel: main affinity was not restored");
            Console.WriteLine($"PASS: background workers are concurrent and disjoint from reserved main core; affinity restored; {ConstructionWorkers.Policy}.");
        }
        finally { harmony.UnpatchAll(harmony.Id); KernelCombatPlanner.Perf = previous; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct GroupAffinity
    { public nuint Mask; public ushort Group, Reserved0, Reserved1, Reserved2; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);
    private static nuint Mask() => OperatingSystem.IsWindows() && GetThreadGroupAffinity(GetCurrentThread(), out var affinity) ? affinity.Mask : 0;

    internal static void SurvivorAudit(string path)
    {
        var save = JsonSerializer.Deserialize(File.ReadAllText(path),
            MegaCrit.Sts2.Core.Saves.JsonSerializationUtility.GetTypeInfo<MegaCrit.Sts2.Core.Saves.SerializableRun>())!;
        var run = RunState.FromSerializable(save);
        var player = run.Players[0];
        var survivor = Card("SURVIVOR", player); player.Deck.AddInternal(survivor);
        // Saved immediately after the same shop: restore the removed card. This
        // is a near replay; allied purchases and HP after removal are disclosed.
        Console.WriteLine($"SURVIVOR-AUDIT act={run.CurrentActIndex} size={player.Deck.Cards.Count} energy={BuildValue.StableEnergy(player)}");
        foreach (var tier in Enum.GetValues<KernelCombatPlanner.TournamentPerf>())
        {
            KernelCombatPlanner.Perf = tier;
            using var scope = DeckStrength.BeginEvaluation();
            foreach (var card in new[]{survivor, player.Deck.Cards.First(c=>c.Id.Entry=="DEFEND_SILENT"),player.Deck.Cards.First(c=>c.Id.Entry=="STRIKE_SILENT")})
                Console.WriteLine($"{tier} remove {card.Id.Entry}: {JsonSerializer.Serialize(BuildValue.Remove(card,player))}");
            var selected = BotShopPlanner.BestRemoval(player.Deck.Cards,player);
            Console.WriteLine($"{tier} shop-filter selected={player.Deck.Cards[selected.index].Id.Entry} candidates={BotShopPlanner.RemovalCandidates(player.Deck.Cards).Count}");
        }
        foreach (var samples in new[] {64,128})
        {
            using var budget = ConstructionBudget.Use(new(samples,32,2,4,4));
            using var scope = DeckStrength.BeginEvaluation();
            foreach (var card in new[]{survivor,player.Deck.Cards.First(c=>c.Id.Entry=="DEFEND_SILENT")})
                Console.WriteLine($"samples={samples} remove {card.Id.Entry}: {JsonSerializer.Serialize(BuildValue.Remove(card,player))}");
        }
        KernelCombatPlanner.Perf = KernelCombatPlanner.TournamentPerf.High;
    }

    internal static void Benchmark(string path)
    {
        var player = Fixture();
        foreach (var id in new[] {"PREPARED","ANTICIPATE","TACTICIAN","REFLEX","BLADE_DANCE","DAGGER_THROW",
            "ACROBATICS","CALCULATED_GAMBLE","FOOTWORK","AFTERIMAGE","INFINITE_BLADES","DEADLY_POISON"})
            player.Deck.AddInternal(Card(id, player));
        var rows = new List<object>();
        var plans = new Dictionary<string, Func<string>> {
            ["camp-upgrade"] = () => string.Join(',', BotBrain.SelectCards(player, player.Deck.Cards.Where(c=>c.IsUpgradable).ToArray(),1,1,"FromDeckForUpgrade").Select(c=>c.Id.Entry)),
            ["event-upgrade-three"] = () => EventTrade.BestUpgrades(player,3).ToString("R"),
            ["event-remove-two"] = () => EventTrade.BestRemoval(player,2).ToString("R"),
        };
        foreach (var tier in Enum.GetValues<KernelCombatPlanner.TournamentPerf>())
        {
            KernelCombatPlanner.Perf = tier;
            foreach (var (name, plan) in plans)
            {
                var watch = Stopwatch.StartNew(); string expected; double synchronous;
                if (File.Exists(path + ".before"))
                {
                    using var baseline = JsonDocument.Parse(File.ReadAllText(path + ".before"));
                    var baselineRow = baseline.RootElement.EnumerateArray().First(r => r.GetProperty("tier").GetString() == tier.ToString() && r.GetProperty("name").GetString() == name);
                    expected = baselineRow.GetProperty("result").GetString()!; synchronous = baselineRow.GetProperty("synchronousMs").GetDouble();
                }
                else { expected = plan(); synchronous = watch.Elapsed.TotalMilliseconds; }
                using var pump = new Pump();
                watch.Restart(); var actual = pump.Run(() => ConstructionDecision.RunAsync(player, plan, () => name));
                Equal(actual, expected, $"{tier} {name} changed result");
                var row = new { tier=tier.ToString(), name, size=player.Deck.Cards.Count, synchronousMs=synchronous,
                    asyncMs=watch.Elapsed.TotalMilliseconds, maxMainCallbackMs=pump.MaxCallbackMs, pumpTicks=pump.Ticks, result=actual };
                rows.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
                File.WriteAllText(path, JsonSerializer.Serialize(rows,new JsonSerializerOptions {WriteIndented=true}));
            }
        }
        KernelCombatPlanner.Perf = KernelCombatPlanner.TournamentPerf.High;
    }
}

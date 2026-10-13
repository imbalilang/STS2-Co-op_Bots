using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Logging;

namespace CoopBots.Building;

// The planner stays on the game thread. A provisional pass discovers missing
// deck evaluations; it is never submitted to the game. At most two snapshots
// are prepared per pass, then await releases the game thread. Later passes use
// the completed values, including any additional two-purchase branches.
internal sealed class ConstructionDecision
{
    [ThreadStatic] internal static ConstructionDecision? Current;
    internal static bool AwaitingValues => Current?.unresolved == true;
    private readonly Dictionary<(Player, string), DeckStrength.Estimate> cache = new();
    private readonly ConstructionReadCache reads = new();
    private readonly List<(Player Player, string Key, CardModel[] Deck, ConstructionJobs Jobs, bool ShuffleSoot)> pending = new();
    private bool unresolved;
    private Exception? failure;
    private long sliceStart;
    internal DeckStrength.Estimate Request(CardModel[] deck, Player player, string key)
    {
        unresolved = true;
        var budget = ConstructionBudget.Current;
        if (pending.Count >= budget.Snapshots || pending.Count > 0 && Stopwatch.GetElapsedTime(sliceStart).TotalMilliseconds >= budget.PrepareMs
            || pending.Any(p => p.Player == player && p.Key == key)) return default;
        var jobs = new ConstructionJobs();
        var previous = Current;
        Current = null;
        try
        {
            using var isolated = DeckStrength.UseCache(new());
            jobs.Capture(() => DeckStrength.Evaluate(deck, player));
            pending.Add((player, key, deck, jobs, ShuffleStatusContext.Active(player)));
        }
        catch (Exception error) { failure = error; throw; }
        finally { Current = previous; }
        return default;
    }

    internal static async Task<T> RunAsync<T>(Player player, Func<T> plan, Func<string> context,
        CancellationToken cancellation = default)
    {
        // Native getters and the eventual purchase/choice must resume on the
        // caller's game synchronization context. Never fall back to Task.Run(plan).
        if (SynchronizationContext.Current is null)
            throw new InvalidOperationException("Async construction requires the game synchronization context");
        var room = player.RunState.CurrentRoom;
        using var roomWork = RoomWork.Begin((object?)room ?? player.RunState);
        await ConstructionPerfSync.EnsureAsync(cancellation);
        using var cores = ConstructionWorkers.ReserveGameCore();
        var total = Stopwatch.GetTimestamp();
        double mainMs = 0, maxSliceMs = 0;
        var batches = 0;
        var stamp = Fingerprint(player) + context();
        var decision = new ConstructionDecision();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!ReferenceEquals(room, player.RunState.CurrentRoom)) throw new OperationCanceledException("Construction room changed");
            var start = Stopwatch.GetTimestamp();
            decision.sliceStart = start;
            decision.pending.Clear(); decision.unresolved = false; decision.failure = null;
            T result;
            using (DeckStrength.UseCache(decision.cache))
            using (decision.reads.Use())
            {
                Current = decision;
                try { result = plan(); }
                finally { Current = null; }
            }
            if (decision.failure is not null) throw new InvalidOperationException("Construction snapshot failed", decision.failure);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            mainMs += elapsed; maxSliceMs = Math.Max(maxSliceMs, elapsed);
            if (!decision.unresolved)
            {
                Log.Info($"CoopBots construction async: bot={player.NetId} batches={batches} decks={decision.cache.Count} "
                    + $"tier={ConstructionPerfSync.Tier} samples={ConstructionBudget.Current.Samples} beam={ConstructionBudget.Current.Beam} workers={ConstructionWorkers.WorkerCount} "
                    + $"elapsedMs={Stopwatch.GetElapsedTime(total).TotalMilliseconds:F1} mainMs={mainMs:F1} maxSliceMs={maxSliceMs:F1}");
                // Emit diagnostics only from a fully evaluated pass. The plan
                // delegate must be read-only: submission happens after return.
                using var completed = DeckStrength.UseCache(decision.cache);
                using var native = decision.reads.Use();
                return plan();
            }
            if (decision.pending.Count == 0) throw new InvalidOperationException("Construction made no progress");
            batches++;
            await Task.WhenAll(decision.pending.Select(p => p.Jobs.ComputeAsync(cancellation)));
            cancellation.ThrowIfCancellationRequested();
            if (!ReferenceEquals(room, player.RunState.CurrentRoom)) throw new OperationCanceledException("Construction room changed");
            var nextStamp = Fingerprint(player) + context();
            if (nextStamp != stamp)
            {
                Log.Info($"CoopBots construction snapshot changed: bot={player.NetId}; recomputing");
                stamp = nextStamp; decision = new();
            }
            else
            {
                start = Stopwatch.GetTimestamp();
                foreach (var item in decision.pending)
                {
                    using var isolated = DeckStrength.UseCache(new());
                    using var native = decision.reads.Use();
                    using var shuffle = ShuffleStatusContext.Use(item.Player, item.ShuffleSoot);
                    decision.cache[(item.Player, item.Key)] = item.Jobs.Replay(() => DeckStrength.Evaluate(item.Deck, item.Player));
                }
                elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                mainMs += elapsed; maxSliceMs = Math.Max(maxSliceMs, elapsed);
            }
            // Even an already completed worker batch must return control before
            // the next native snapshot/replay pass.
            await Task.Yield();
        }
    }

    internal static string Cards(IEnumerable<CardModel> cards) => string.Join(";", cards.Select(c =>
        $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c)}:{c.Id.Entry}:{c.CurrentUpgradeLevel}:{c.Type}:{c.Enchantment?.Id}:{c.Enchantment?.Amount}:"
        + $"{c.BaseStarCost}:{c.HasStarCostX}:{c.GetEnchantedReplayCount()}:{c.Affliction?.Id}:{CardProfile.Of(c).Cost}:"
        + string.Join(',', c.Keywords.Order()) + ":" + string.Join(',', c.DynamicVars.OrderBy(v => v.Key).Select(v => $"{v.Key}={v.Value.BaseValue}/{v.Value.IntValue}"))));

    internal static string Fingerprint(Player player) =>
        $"{ConstructionPerfSync.Tier}:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(player.RunState)}:{player.RunState.CurrentActIndex}:{player.RunState.AscensionLevel}:{RunDepth.RemainingCombats(player)}:{player.RunState.Rng.Niche.ToSerializable().counter}|"
        + string.Join("|", player.RunState.Players.Select(p =>
            $"{p.NetId}:{p.Creature.CurrentHp}:{p.Creature.MaxHp}:{p.Gold}:{BuildValue.StableEnergy(p)}:{p.MaxEnergy}:"
            + Cards(p.Deck.Cards) + ":" + string.Join(';', p.Relics.Select(r => $"{r.Id}:{r.IsUsedUp}:" + string.Join(',', r.DynamicVars.Select(v => $"{v.Key}={v.Value.BaseValue}"))))));
}

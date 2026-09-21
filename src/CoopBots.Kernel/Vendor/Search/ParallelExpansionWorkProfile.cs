using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace CoopBots.Kernel.Vendor;

/// <summary>Coordinator-owned elapsed-time distributions, never CPU-time estimates.</summary>
internal sealed class ParallelExpansionWorkProfile
{
    internal enum Kind
    {
        Parent, Prepare, Action, Choice, PrimaryReplay, Tail, Potion, EndTurn, Wave, Wait, Commit,
        StandPat, StandPatWave, RoutingSignature, RoutingSummary, RoutingPareto,
        ContinuationPacket, RetentionWave,
    }

    private sealed class Distribution
    {
        public readonly long[] Buckets = new long[32];
        public long Count;
        public long Ticks;
        public long Maximum;
        public int MaximumConcurrency;
    }

    private readonly Distribution[] _distributions =
        Enum.GetValues<Kind>().Select(_ => new Distribution()).ToArray();

    public void Record(Kind kind, long elapsedTicks, int activeKindConcurrency = 0)
    {
        Distribution distribution = _distributions[(int)kind];
        distribution.Count++;
        distribution.Ticks += elapsedTicks;
        distribution.Maximum = Math.Max(distribution.Maximum, elapsedTicks);
        distribution.MaximumConcurrency = Math.Max(distribution.MaximumConcurrency, activeKindConcurrency);
        ulong microseconds = (ulong)Math.Max(1, Math.Ceiling(
            elapsedTicks * 1_000_000d / Stopwatch.Frequency));
        int bucket = Math.Min(31, BitOperations.Log2(microseconds - 1) + 1);
        if (microseconds == 1)
            bucket = 0;
        distribution.Buckets[bucket]++;
    }

    public IEnumerable<string> Describe()
    {
        foreach (Kind kind in Enum.GetValues<Kind>())
        {
            Distribution distribution = _distributions[(int)kind];
            if (distribution.Count == 0)
                continue;
            double PercentileUpper(double fraction)
            {
                long target = (long)Math.Ceiling(distribution.Count * fraction);
                long cumulative = 0;
                for (int index = 0; index < distribution.Buckets.Length; index++)
                {
                    cumulative += distribution.Buckets[index];
                    if (cumulative >= target)
                        return (1L << index) / 1000d;
                }
                throw new InvalidOperationException("并行作业分布计数不一致。");
            }
            yield return string.Create(CultureInfo.InvariantCulture,
                $"kind={kind} count={distribution.Count} elapsed_sum_ms={distribution.Ticks * 1000d / Stopwatch.Frequency:F3} p50_upper_ms={PercentileUpper(.5):F3} p95_upper_ms={PercentileUpper(.95):F3} max_ms={distribution.Maximum * 1000d / Stopwatch.Frequency:F3}")
                + (distribution.MaximumConcurrency > 0
                    ? $" max_concurrency={distribution.MaximumConcurrency}" : "");
        }
    }
}

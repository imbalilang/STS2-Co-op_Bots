using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace CoopBots.Kernel.Vendor;

internal sealed partial class CombatBeamSolver
{
    internal long VerifyRetentionJobsForTesting(Action<Action<int, Action<int>>> verify)
    {
        using ParallelExpansionExecutor executor = new(this, 2);
        verify((count, evaluate) => executor.EvaluateRetentionIndices(
            count, ParallelExpansionWorkProfile.Kind.RoutingSignature, evaluate));
        return _run.OffThreadAllocatedBytes;
    }

    private sealed partial class ParallelExpansionExecutor
    {
        // The coordinator waits for the whole batch. Callbacks may only read frozen retention
        // inputs and write their own output slots; no simulation, admission or shared counters.
        public void EvaluateRetentionIndices(
            int count,
            ParallelExpansionWorkProfile.Kind kind,
            Action<int> evaluate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count == 0)
                return;
            ExpansionLane[] lanes = EnsureBackgroundLanes();
            using RetentionJobWave wave = new(
                count, Math.Clamp(count / (DegreeOfParallelism * 4), 1, 16));
            RetentionIndexJob[] jobs = new RetentionIndexJob[Math.Min(count, DegreeOfParallelism)];
            long startedAt = Stopwatch.GetTimestamp();
            try
            {
                for (int lane = 0; lane < jobs.Length; lane++)
                {
                    _coordinator.SearchCancellationToken.ThrowIfCancellationRequested();
                    jobs[lane] = new(wave, evaluate);
                    wave.Completed.AddCount();
                    try { lanes[lane].Dispatch(jobs[lane]); }
                    catch
                    {
                        wave.Completed.Signal();
                        throw;
                    }
                }
            }
            finally
            {
                wave.Completed.Signal();
                wave.Completed.Wait();
                // Count every completed callback, including partial work on cancellation or
                // failure. Worker run contexts are unused; these are metadata-only jobs.
                foreach (RetentionIndexJob? job in jobs)
                {
                    if (job == null)
                        continue;
                    _coordinator._run.OffThreadAllocatedBytes += job.AllocatedBytes;
                    _workProfile.Record(kind, job.ElapsedTicks, job.Concurrency);
                }
                _workProfile.Record(ParallelExpansionWorkProfile.Kind.RetentionWave,
                    Stopwatch.GetTimestamp() - startedAt);
            }
            wave.Error?.Throw();
        }

        private sealed class RetentionJobWave(int count, int chunkSize) : IDisposable
        {
            private int _next;
            public int Count { get; } = count;
            public int ChunkSize { get; } = chunkSize;
            public int Active;
            public ExceptionDispatchInfo? Error;
            public CountdownEvent Completed { get; } = new(1);
            public int TakeChunk() => Interlocked.Add(ref _next, ChunkSize) - ChunkSize;
            public void Dispose() => Completed.Dispose();
        }

        private sealed class RetentionIndexJob(RetentionJobWave wave, Action<int> evaluate)
            : IExpansionLaneWorkItem
        {
            public long AllocatedBytes;
            public long ElapsedTicks;
            public int Concurrency;

            public void Execute(ParallelExpansionExecutor owner, CombatBeamSolver worker)
            {
                long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
                long startedAt = Stopwatch.GetTimestamp();
                Concurrency = Interlocked.Increment(ref wave.Active);
                try
                {
                    while (Volatile.Read(ref wave.Error) == null)
                    {
                        owner._coordinator.SearchCancellationToken.ThrowIfCancellationRequested();
                        int start = wave.TakeChunk();
                        if (start >= wave.Count)
                            break;
                        int end = Math.Min(wave.Count, start + wave.ChunkSize);
                        for (int index = start; index < end; index++)
                        {
                            owner._coordinator.SearchCancellationToken.ThrowIfCancellationRequested();
                            evaluate(index);
                        }
                    }
                }
                catch (System.Exception error)
                {
                    Interlocked.CompareExchange(
                        ref wave.Error, ExceptionDispatchInfo.Capture(error), null);
                }
                finally
                {
                    Interlocked.Decrement(ref wave.Active);
                    AllocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart);
                    ElapsedTicks = Stopwatch.GetTimestamp() - startedAt;
                }
            }

            public void Signal() => wave.Completed.Signal();
        }
    }
}

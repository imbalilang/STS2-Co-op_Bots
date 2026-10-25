using System.Diagnostics;
using MegaCrit.Sts2.Core.Logging;

namespace CoopBots;

// Frame intervals include engine/other-mod work. Tick and capture costs measure
// only this caller's elapsed interval; neither is advertised as pure CPU time.
internal static class BotFrameTelemetry
{
    private static readonly double[] limits = [1, 2, 4, 8, 16, 33, 50, 100, 250, 1000, double.PositiveInfinity];
    private static readonly long[] frames = new long[limits.Length];
    private static long count, nextReport;
    private static double maxFrame, maxTick, lastPause;
    private static long allocated;

    internal static void Frame(double delta, double tickMs, long tickAllocated)
    {
        var milliseconds = delta * 1000;
        var bucket = 0;
        while (bucket < limits.Length - 1 && milliseconds > limits[bucket]) bucket++;
        frames[bucket]++; count++;
        maxFrame = Math.Max(maxFrame, milliseconds); maxTick = Math.Max(maxTick, tickMs);
        allocated += Math.Max(0, tickAllocated);
        var now = Environment.TickCount64;
        if (nextReport == 0) { nextReport = now + 10000; lastPause = GC.GetTotalPauseDuration().TotalMilliseconds; }
        if (now < nextReport) return;
        var pause = GC.GetTotalPauseDuration().TotalMilliseconds;
        Log.Info($"CoopBots frame cost: frames={count} frameP95UpperMs={Percentile(.95):F1} "
            + $"frameP99UpperMs={Percentile(.99):F1} frameMaxMs={maxFrame:F1} botTickMaxMs={maxTick:F1} "
            + $"botTickAllocMB={allocated / 1048576d:F2} processGcPauseMs={pause - lastPause:F1}");
        Array.Clear(frames); count = allocated = 0; maxFrame = maxTick = 0;
        lastPause = pause; nextReport = now + 10000;
    }

    private static double Percentile(double fraction)
    {
        long cumulative = 0;
        for (var i = 0; i < frames.Length; i++)
        { cumulative += frames[i]; if (cumulative >= Math.Ceiling(count * fraction)) return Math.Min(limits[i], maxFrame); }
        return maxFrame;
    }

    internal readonly struct CaptureScope : IDisposable
    {
        private readonly long started, allocation;
        public CaptureScope() { started = Stopwatch.GetTimestamp(); allocation = GC.GetAllocatedBytesForCurrentThread(); }
        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (elapsed > 8)
                Log.Info($"CoopBots capture cost: wallMs={elapsed:F1} callerAllocKB={(GC.GetAllocatedBytesForCurrentThread() - allocation) / 1024d:F1} thread={Environment.CurrentManagedThreadId}");
        }
    }
}

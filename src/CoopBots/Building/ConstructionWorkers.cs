using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MegaCrit.Sts2.Core.Logging;

namespace CoopBots.Building;

// Upstream has no deck-search workers. Owner 2026-10-01 asks for multicore
// construction without animation stalls: use a bounded low-priority pool and
// reserve the caller's physical core (including SMT siblings) while it runs.
// Never change process-wide affinity or the rendering/audio threads.
internal static class ConstructionWorkers
{
    private static readonly Queue<Action> queue = new();
    private static int hardwareWorkers, limit;
    private static readonly object gate = new();
    private static bool started;
    private static int leases;
    private static uint callerThread;
    private static nuint originalAffinity, mainMask, workerMask;
    [ThreadStatic] private static bool isolated;
    internal static int WorkerCount { get; private set; }
    internal static string Policy { get; private set; } = "not-started";

    internal static IDisposable ReserveGameCore()
    {
        lock (gate)
        {
            var tid = OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0;
            if (leases > 0 && tid != callerThread)
                throw new InvalidOperationException("Construction core lease must belong to the game thread");
            if (!started)
            {
                Configure();
                hardwareWorkers = WorkerCount;
                started = true;
                for (var i = 0; i < WorkerCount; i++)
                {
                    var lane = i;
                    var thread = new Thread(() => Work(lane)) { IsBackground = true, Name = $"CoopBots construction {i}", Priority = ThreadPriority.BelowNormal };
                    thread.Start();
                }
                Log.Info($"CoopBots construction workers: {Policy}");
            }
            limit = Math.Min(hardwareWorkers, ConstructionBudget.Current.Workers);
            WorkerCount = limit;
            Monitor.PulseAll(gate);
            if (leases == 0)
            {
                callerThread = tid;
                if (mainMask != 0)
                {
                    originalAffinity = SetThreadAffinityMask(GetCurrentThread(), mainMask);
                    if (originalAffinity == 0) throw new InvalidOperationException("Could not reserve the game thread's core");
                }
            }
            leases++;
            return new Lease();
        }
    }
    private sealed class Lease : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                if (OperatingSystem.IsWindows() && GetCurrentThreadId() != callerThread)
                    throw new InvalidOperationException("Construction core lease returned off the game thread");
                disposed = true;
                if (--leases == 0 && originalAffinity != 0)
                {
                    if (SetThreadAffinityMask(GetCurrentThread(), originalAffinity) == 0)
                        Log.Warn("CoopBots could not restore game thread affinity");
                    originalAffinity = 0;
                }
            }
        }
    }
    internal static Task<T> Run<T>(Func<T> action, CancellationToken cancellation = default)
    {
        if (!started) throw new InvalidOperationException("Reserve construction workers on the game thread first");
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) { queue.Enqueue(() => {
            if (cancellation.IsCancellationRequested) { completion.TrySetCanceled(cancellation); return; }
            try {
                if (!isolated) throw new InvalidOperationException("Could not isolate construction worker affinity");
                completion.TrySetResult(action());
            }
            catch (Exception error) { completion.TrySetException(error); }
        });
        Monitor.PulseAll(gate); }
        return completion.Task;
    }
    private static void Work(int lane)
    {
        isolated = workerMask == 0 || SetThreadAffinityMask(GetCurrentThread(), workerMask) != 0;
        while (true)
        {
            Action action;
            lock (gate)
            {
                while (queue.Count == 0 || lane >= limit) Monitor.Wait(gate);
                action = queue.Dequeue();
            }
            action();
        }
    }
    private static void Configure()
    {
        WorkerCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        Policy = $"workers={WorkerCount},priority=BelowNormal,affinity=OS-managed";
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8 || Environment.ProcessorCount > 64) return;
        uint length = 0;
        GetLogicalProcessorInformation(IntPtr.Zero, ref length);
        if (length == 0) return;
        var memory = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformation(memory, ref length)) return;
            var allowed = (nuint)Process.GetCurrentProcess().ProcessorAffinity;
            var current = (nuint)1 << (int)GetCurrentProcessorNumber();
            var cores = new List<nuint>();
            for (var offset = 0; offset + 32 <= length; offset += 32)
            {
                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION, x64: mask[8],
                // relationship[4], padding[4], union[16]. RelationProcessorCore=0.
                if (Marshal.ReadInt32(memory, offset + 8) != 0) continue;
                var mask = unchecked((nuint)Marshal.ReadInt64(memory, offset)) & allowed;
                if (mask != 0) cores.Add(mask);
            }
            var reserved = cores.FirstOrDefault(mask => (mask & current) != 0);
            var others = cores.Where(mask => mask != reserved).ToArray();
            if (reserved == 0 || others.Length == 0) return;
            mainMask = reserved;
            workerMask = others.Aggregate((nuint)0, (mask, core) => mask | core);
            WorkerCount = Math.Clamp(others.Length / 2, 1, 4);
            Policy = $"workers={WorkerCount},priority=BelowNormal,mainPhysicalMask=0x{mainMask:X},workerMask=0x{workerMask:X},restoreAfterDecision=true";
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessorNumber();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint SetThreadAffinityMask(IntPtr thread, nuint mask);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint length);
}

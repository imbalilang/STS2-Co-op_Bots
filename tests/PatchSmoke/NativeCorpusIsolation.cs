internal static class NativeCorpusIsolation
{
    internal static void Run(string flag, string failurePrefix)
    {
        // The child resolves this exact test assembly and its adjacent verified
        // Core/Kernel DLLs. Keep observer/JIT effects outside later budget tests.
        // Parent failure propagation was proved with a physical production fault:
        // perf-audit/attack-gain-arrays/r2-isolation/proof.json (child 1 / parent 1).
        // This generic helper was revalidated with the empty-nonzero production
        // fault: perf-audit/empty-exhaust-opportunity/parent-witness/witness.json.
        // Both native corpora passed in that physical assembly; child 1 reached
        // the parent, and exact restoration rebuilt all three DLLs identically.
        var host = Environment.ProcessPath
            ?? throw new InvalidOperationException("Missing test process host");
        var start = new System.Diagnostics.ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(NativeCorpusIsolation).Assembly.Location);
        start.ArgumentList.Add(flag);
        using var child = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Cannot start native sampling corpus");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        child.WaitForExit();
        Console.Write(stdout.GetAwaiter().GetResult());
        Console.Error.Write(stderr.GetAwaiter().GetResult());
        if (child.ExitCode != 0)
            throw new InvalidOperationException(failurePrefix + " failed: exit=" + child.ExitCode);
    }
}

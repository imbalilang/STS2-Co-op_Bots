namespace CoopBots.Building;

// Capture native inputs on the game thread, compute pure scenario values on
// workers, then replay the same formula on the game thread. Ordinal replay is
// decision-local and strict: a changed job shape is an error, never a cache hit.
internal sealed class ConstructionJobs
{
    [ThreadStatic] private static ConstructionJobs? current;
    private readonly List<Func<double>> jobs = new();
    private double[]? results;
    private int position;
    private readonly ConstructionBudget.Settings budget = ConstructionBudget.Current;
    internal int Count => jobs.Count;
    internal static double Value(Func<Func<double>> prepare)
    {
        if (current is not { } scope) return prepare()();
        if (scope.results is { } values)
        {
            if (scope.position >= values.Length) throw new InvalidOperationException("Construction replay added a job");
            return values[scope.position++];
        }
        scope.jobs.Add(prepare());
        return 0;
    }
    internal T Capture<T>(Func<T> action)
    {
        if (current is not null) throw new InvalidOperationException("Nested construction capture");
        current = this;
        try { return action(); }
        finally { current = null; }
    }
    internal T Replay<T>(Func<T> action)
    {
        if (results is null) throw new InvalidOperationException("Construction jobs are unfinished");
        position = 0;
        var value = Capture(action);
        if (position != results.Length) throw new InvalidOperationException("Construction replay omitted a job");
        return value;
    }
    internal async Task ComputeAsync(CancellationToken cancellation = default)
    {
        results = await Task.WhenAll(jobs.Select(job => ConstructionWorkers.Run(() => {
            using var scope = ConstructionBudget.Use(budget);
            return job();
        }, cancellation)));
    }
}

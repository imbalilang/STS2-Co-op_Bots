namespace CoopBots.Building;

internal static class ConstructionBudget
{
    internal sealed record Settings(int Samples, int Beam, int Snapshots, double PrepareMs, int Workers);
    private static readonly Settings low = new(4, 8, 1, 2, 1), mid = new(8, 16, 1, 3, 2),
        high = new(16, 32, 2, 4, 4), ultra = new(24, 48, 2, 6, 4);
    internal static Settings For(KernelCombatPlanner.TournamentPerf tier) => tier switch
    {
        KernelCombatPlanner.TournamentPerf.Low => low,
        KernelCombatPlanner.TournamentPerf.Mid => mid,
        KernelCombatPlanner.TournamentPerf.Ultra => ultra,
        _ => high,
    };
    [ThreadStatic] private static Settings? active;
    internal static Settings Current => active ?? For(ConstructionPerfSync.Tier);
    internal static IDisposable Use(Settings value)
    {
        var previous = active; active = value; return new Scope(previous);
    }
    private sealed class Scope(Settings? previous) : IDisposable { public void Dispose() => active = previous; }
}

using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

// Only attached during native read-only passes. The owning decision discards
// this cache whenever its live-state fingerprint changes; no scope crosses await.
internal sealed class ConstructionReadCache
{
    [ThreadStatic] internal static ConstructionReadCache? Current;
    internal readonly Dictionary<(CardModel, int, int), CardProfile.Facts> Profiles = new();
    internal readonly Dictionary<CardModel, ConstructionCard> Cards = new();
    internal readonly Dictionary<(CardModel, int, int), (CardProfile.Facts Facts, CardModel Card)?> Upgrades = new();
    internal IDisposable Use()
    {
        var previous = Current; Current = this; return new Scope(previous);
    }
    private sealed class Scope(ConstructionReadCache? previous) : IDisposable
    { public void Dispose() => Current = previous; }
}

using System.Collections.Frozen;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

// Copy native properties on the caller/game thread. Search workers receive only
// immutable values; no native getters, Godot objects, hooks or RNG are accessed.
internal sealed class ConstructionCard
{
    internal readonly record struct Identifier(string Entry);
    internal Identifier Id { get; }
    internal string Kind { get; }
    internal CardType Type { get; }
    internal bool IsUpgraded { get; }
    internal bool HasAffliction { get; }
    internal FrozenSet<CardKeyword> Keywords { get; }
    private readonly FrozenDictionary<string, double> amounts;
    private readonly FrozenDictionary<string, int> integers;
    private readonly int replay;
    private readonly Type nativeType;
    internal ConstructionCard(CardModel card)
    {
        Id = new(card.Id.Entry); nativeType = card.GetType(); Kind = nativeType.Name;
        Type = card.Type; IsUpgraded = card.IsUpgraded; HasAffliction = card.Affliction is not null;
        Keywords = card.Keywords.ToFrozenSet(); replay = card.GetEnchantedReplayCount();
        amounts = card.DynamicVars.ToFrozenDictionary(v => v.Key, v => (double)v.Value.BaseValue);
        integers = card.DynamicVars.ToFrozenDictionary(v => v.Key, v => v.Value.IntValue);
    }
    internal bool Is<T>() where T : CardModel => typeof(T).IsAssignableFrom(nativeType);
    internal double Amount(string key, double fallback = 0) => amounts.GetValueOrDefault(key, fallback);
    internal int IntAmount(string key, int fallback = 1) => Math.Max(0, integers.GetValueOrDefault(key, fallback));
    internal int GetEnchantedReplayCount() => replay;
    internal static ConstructionCard Capture(CardModel card)
    {
        var cache = ConstructionReadCache.Current?.Cards;
        if (cache is not null && cache.TryGetValue(card, out var snapshot)) return snapshot;
        var result = new ConstructionCard(card);
        if (cache is not null) cache[card] = result;
        return result;
    }
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(card))]
    public static implicit operator ConstructionCard?(CardModel? card) => card is null ? null : Capture(card);
}

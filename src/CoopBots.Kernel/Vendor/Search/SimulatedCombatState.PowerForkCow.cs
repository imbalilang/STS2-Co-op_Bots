using CoopBots.Kernel.Vendor.Engine.Common;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel.Vendor;

internal sealed partial class SimulatedCombatState
{
    // Powers inherited from a parent fork. They are shared until the first
    // mutation, at which point EnsureOwnedPower clones just that one instance
    // and re-points every container that references it. This is the P2a fix
    // for the measured 94% share of CombatStateFork allocation.
    private System.Collections.Concurrent.ConcurrentDictionary<PowerModel, byte>? _sharedPowers;
    private PredictionStateStore? _stateStore;

    internal void AttachStateStore(PredictionStateStore stateStore) => _stateStore = stateStore;

    private void MarkSharedPower(PowerModel power)
    {
        // A frozen source is forked concurrently by tournament workers. Sharing
        // registration mutates only this synchronized ownership marker, never the
        // models or the source's simulation state.
        var shared = Volatile.Read(ref _sharedPowers);
        if (shared is null)
        {
            // One stripe still synchronizes simultaneous read-only Fork calls;
            // CPU-count stripes/default capacity would tax every tiny branch.
            var capacity = Math.Max(4, (_powers?.Count ?? 0) + (_addedPowerInstances?.Count ?? 0));
            var fresh = new System.Collections.Concurrent.ConcurrentDictionary<PowerModel, byte>(
                concurrencyLevel: 1, capacity, ReferenceEqualityComparer.Instance);
            shared = Interlocked.CompareExchange(ref _sharedPowers, fresh, null) ?? fresh;
        }
        shared.TryAdd(power, 0);
    }

    private void SharePowerWith(SimulatedCombatState fork, PowerModel power)
    {
        // Upstream eagerly clones; our existing allocation optimization shares
        // until write. The 2026-10-07 native-boundary repair requires marking
        // both sides: refinement continues writing its saved boundary's parent.
        MarkSharedPower(power);
        fork.MarkSharedPower(power);
    }

    private PowerModel EnsureOwnedPower(PowerModel power)
    {
        if (_sharedPowers is null || !_sharedPowers.TryRemove(power, out _))
            return power;
        PowerModel clone = PredictionUtils.CloneModelForSimulation(power);
        clone._owner = power.Owner;
        clone._applier = power.Applier;
        clone._target = power.Target;
        clone._amount = power.Amount;
        ReplacePowerInstance(power, clone);
        _stateStore?.RemapModel(power, clone);
        InvalidateHookListeners();
        return clone;
    }

    private void ReplacePowerInstance(PowerModel old, PowerModel replacement)
    {
        if (_powers is not null)
        {
            foreach (var key in _powers
                .Where(pair => ReferenceEquals(pair.Value, old))
                .Select(pair => pair.Key).ToArray())
                _powers[key] = replacement;
        }
        ReplaceInList(_addedPowerInstances, old, replacement);
        ReplaceInList(_powerListenerOrder, old, replacement);
        if (_rootMultiInstancePowerClones is not null)
        {
            foreach (var key in _rootMultiInstancePowerClones
                .Where(pair => ReferenceEquals(pair.Value, old))
                .Select(pair => pair.Key).ToArray())
                _rootMultiInstancePowerClones[key] = replacement;
            if (_rootMultiInstancePowerClones.TryGetValue(old, out PowerModel? mapped))
            {
                _rootMultiInstancePowerClones.Remove(old);
                _rootMultiInstancePowerClones[replacement] = mapped;
            }
        }
        if (_nightmareSelections is not null)
        {
            foreach (var pair in _nightmareSelections
                .Where(pair => ReferenceEquals(pair.Key, old)).ToArray())
            {
                _nightmareSelections.Remove(pair.Key);
                if (replacement is NightmarePower nightmare)
                    _nightmareSelections[nightmare] = pair.Value;
            }
        }
        if (_orbitEnergyRemainders is not null
            && old is OrbitPower oldOrbit && replacement is OrbitPower newOrbit
            && _orbitEnergyRemainders.Remove(oldOrbit, out int orbitRemainder))
            _orbitEnergyRemainders[newOrbit] = orbitRemainder;
        if (_paleBlueDotActivated is not null
            && old is PaleBlueDotPower oldPale && replacement is PaleBlueDotPower newPale
            && _paleBlueDotActivated.Remove(oldPale, out bool activated))
            _paleBlueDotActivated[newPale] = activated;
        if (_pendingPowerAmountChanges is { Count: > 0 })
        {
            for (var index = 0; index < _pendingPowerAmountChanges.Count; index++)
            {
                SimulatedPowerAmountChange change = _pendingPowerAmountChanges[index];
                if (ReferenceEquals(change.Power, old))
                    _pendingPowerAmountChanges[index] = change with { Power = replacement };
            }
        }
    }

    private static void ReplaceInList<T>(List<T>? list, T old, T replacement)
        where T : class
    {
        if (list is null) return;
        for (var index = 0; index < list.Count; index++)
            if (ReferenceEquals(list[index], old))
                list[index] = replacement;
    }
}

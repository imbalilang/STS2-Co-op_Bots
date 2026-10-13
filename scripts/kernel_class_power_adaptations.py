"""Preserve the native Tank lifecycle after either kernel import's positional patches."""


def apply_class_power_adaptations(relative, text):
    additions = {
        'Prediction/CardEffectSpecRegistry.cs': (
            '        [typeof(Colossus)] = [Owner<ColossusPower>("Colossus")],\n',
            '        // Native Tank.OnPlay applies one TankPower; its AfterApplied grants\n'
            '        // separate Guarded instances to living allies (SimulatedCombatState).\n'
            '        [typeof(Tank)] = [Owner<TankPower>(_ => 1)],\n',
        ),
        'Search/SimulatedCombatState.cs': (
            '        afterAmountChanged?.Invoke(amount, simulated);\n',
            '        // Native TankPower.AfterApplied: one Guarded instance per living ally,\n'
            '        // retaining the Tank owner as applier so death removes only its instances.\n'
            '        if (previousAmount == 0 && simulated._amount > 0 && simulated is TankPower)\n'
            '            foreach (var ally in Players.Where(p => p.Creature != target && p.Creature.Side == target.Side\n'
            '                && CanReceivePredictedPowers(p.Creature)))\n'
            '                AddPowerInstance<GuardedPower>(ally.Creature, simulated._amount, target);\n',
        ),
        'Engine/InCombat/Mirrors/Hooks/Death/AfterDeathMirrors.cs': (
            '        registry.Register<DampenPower>(HandleDampen);\n',
            '        registry.Register<GuardedPower>((power, context) =>\n'
            '        {\n'
            '            if (!context.WasRemovalPrevented && context.Creature == power.Applier)\n'
            '            {\n'
            '                if (context.CombatState is not ICombatPredictionEffectSink effects)\n'
            '                    throw new InvalidOperationException("Guarded death requires writable branch state.");\n'
            '                effects.SetPowerAmount(power, 0);\n'
            '            }\n'
            '        });\n',
        ),
    }
    if relative not in additions:
        return text
    anchor, addition = additions[relative]
    if addition in text:
        return text
    if text.count(anchor) != 1:
        raise RuntimeError(f'Class-power adaptation anchor changed: {relative}')
    return text.replace(anchor, anchor + addition)

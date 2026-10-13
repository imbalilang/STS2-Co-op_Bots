using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Attack;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Card;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Damage;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor;

internal static class PowerPredictionStateSupport
{
    public static SurroundedPower.Direction SurroundedFacing(CombatPredictionSimulator simulator, SurroundedPower power)
        => simulator.StateStore.Peek(power, () => new SurroundedPredictionState(power)).Facing;

    public static void CaptureRootState(
        CombatPredictionSimulator simulator,
        PowerModel target,
        PowerModel source)
    {
        switch (target, source)
        {
            // Upstream mirrors Afterimage's Before/After pair, but omits root
            // capture. Only an actually empty original native pair can become
            // owned empty CardPlay state; clone InitInternalData is no evidence.
            // Native mutable power cloning initializes Data; only its real
            // empty dictionary is evidence. Nonempty or absent data stays unknown.
            case (AfterimagePower value, AfterimagePower original):
                if (original.GetInternalData<AfterimagePower.Data>() is { } data
                    && data.amountsForPlayedCards.Count == 0)
                    _ = simulator.StateStore.GetReadOnly(value, static () => new CardPlayPairPredictionState());
                break;
            case (DarkEmbracePower value, DarkEmbracePower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new DarkEmbracePredictionState(original));
                break;
            case (SkittishPower value, SkittishPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new SkittishPredictionState(original));
                break;
            case (PanachePower value, PanachePower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new PanachePredictionState(original));
                break;
            case (SoulboundPower value, SoulboundPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new SoulboundPredictionState(original));
                break;
            case (HellraiserPower value, HellraiserPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new HellraiserPredictionState(original));
                break;
            case (CacophonyPower value, CacophonyPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new CacophonyPredictionState(original));
                break;
            case (AutomationPower value, AutomationPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new AutomationPredictionState(original));
                break;
            case (JugglingPower value, JugglingPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new JugglingPredictionState(original));
                break;
            case (ChainsOfBindingPower value, ChainsOfBindingPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => ChainsOfBindingPredictionState.CaptureRoot(original));
                break;
            case (SurroundedPower value, SurroundedPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new SurroundedPredictionState(original));
                break;
            case (VoidFormPower value, VoidFormPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new VoidFormPredictionState(original));
                break;
            case (FeralPower value, FeralPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new FeralPredictionState(original));
                break;
            case (HardenedShellPower value, HardenedShellPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new HardenedShellPredictionState(original));
                break;
        }
        // 上面这个 switch 按原版类型写死，第三方登记不进去。克隆会把 _internalData 重置成
        // InitInternalData()，所以靠它保存状态的第三方 Power 同样必须在这里把实机实例的值搬进
        // StateStore，否则模拟一开始读到的就是初值。
        PowerHiddenStateMirrors.CaptureRootState(simulator, target, source);
    }
}

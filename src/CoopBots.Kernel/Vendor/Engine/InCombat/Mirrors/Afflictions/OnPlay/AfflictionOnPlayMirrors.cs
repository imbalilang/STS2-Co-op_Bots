using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.Common.Mirrors;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Afflictions.OnPlay;

using Registry = MethodMirrorRegistry<AfflictionModel, AfflictionOnPlayMirrorContext>;

// Vanilla afflictions currently inherit the empty base OnPlay implementation. Keep an empty
// registry so future vanilla or gameplay-mod overrides are detected by the normal mirror policy.
internal static class AfflictionOnPlayMirrors
{
    private static readonly MirrorMethodSpec OnPlay = new(
        typeof(AfflictionModel),
        nameof(AfflictionModel.OnPlay),
        BindingFlags.Instance | BindingFlags.Public,
        [typeof(PlayerChoiceContext), typeof(Creature)]);

    private static readonly Registry Registry = new(OnPlay);

    public static MirrorDispatchResult Invoke(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        Creature? target,
        AfflictionModel affliction)
    {
        return Registry.Invoke(affliction, new AfflictionOnPlayMirrorContext
        {
            Simulator = simulator,
            Card = card,
            Target = target
        });
    }
}

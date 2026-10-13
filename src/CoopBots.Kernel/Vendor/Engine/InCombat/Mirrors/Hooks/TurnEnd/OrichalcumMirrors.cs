using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using CoopBots.Kernel.Vendor.Engine.Common;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.TurnEnd;

internal static class OrichalcumMirrors
{
    public static void BeforeSideTurnEndVeryEarly(RelicModel relic, BeforeSideTurnEndMirrorContext context)
    {
        if (context.Participants.Contains(relic.Owner.Creature) &&
            context.State.GetCreature(relic.Owner.Creature).Block <= 0)
        {
            GetState(relic, context).ShouldTrigger = true;
        }
    }

    public static void BeforeSideTurnEnd(RelicModel relic, BeforeSideTurnEndMirrorContext context)
    {
        var state = GetState(relic, context);
        if (state.ShouldTrigger)
        {
            state.ShouldTrigger = false;
            context.Simulator.GainBlock(relic.Owner.Creature, relic.DynamicVars.Block);
        }
    }

    private static OrichalcumPredictionState GetState(RelicModel relic, BeforeSideTurnEndMirrorContext context)
        => context.StateStore.Get(relic, static model => new OrichalcumPredictionState(model));
}

// One owned payload at capture and during every mirror hook. False is real
// state too; allocating it on first use must not change planning evidence.
internal sealed class OrichalcumPredictionState(RelicModel relic) : IPredictionStateForkable
{
    public bool ShouldTrigger { get; set; } = relic switch
    {
        Orichalcum value => value._shouldTrigger,
        FakeOrichalcum value => value._shouldTrigger,
        _ => throw new InvalidOperationException("Orichalcum state requires its native relic type.")
    };

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

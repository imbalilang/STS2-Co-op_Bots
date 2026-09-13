using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel.Vendor;

internal enum SolverTheftPolicy
{
    PreserveResources,
    LetEscape,
}

internal static class TheftEncounterStrategy
{
    public static bool IsApplicable(CombatState state)
        => state.Encounter?.Id.Entry is "GREMLIN_MERC_NORMAL" or "THIEVING_HOPPER_WEAK"
            || state.Creatures.Any(creature => creature.Monster is GremlinMerc or ThievingHopper or FatGremlin);

    public static int OutstandingStolenResource(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
        => combat.OutstandingStolenResource(simulator);
}

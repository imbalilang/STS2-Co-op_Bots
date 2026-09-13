using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Orbs;

internal class OrbMirrorContext : CombatMirrorContext
{
    public ISet<uint>? ProcessedEnemyDeaths { get; init; }
}

internal sealed class OrbPassiveMirrorContext : OrbMirrorContext
{
    public Creature? Target { get; init; }
}

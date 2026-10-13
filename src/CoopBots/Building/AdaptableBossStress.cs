using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;

namespace CoopBots.Building;

// A public mechanism stress case, never a prediction of the hidden boss.
// Audited alongside v0.111.0 TestSubject.cs: First/Second/ThirdFormHp,
// BiteDamage, SkullBashDamage, MultiClawDamage and BaseMultiClawCount.
// Native multiplayer HP scaling is called directly; Ascension thresholds are
// the public run's level, so parallel construction workers need no global
// RunManager/AscensionManager mutation. Each deck receives its equal share of
// finite party HP, but still needs to survive the full native per-seat attack.
internal sealed record AdaptableBossStress(double[] Health, double Bite, double Bash, double Claw, double Enrage)
{
    internal static AdaptableBossStress For(Player? player)
    {
        var asc = player?.RunState.AscensionLevel ?? 10;
        var tough = asc >= (int)AscensionLevel.ToughEnemies;
        var deadly = asc >= (int)AscensionLevel.DeadlyEnemies;
        var count = Math.Max(1, player?.RunState.Players.Count ?? 1);
        var act = Math.Max(0, player?.RunState.CurrentActIndex ?? 2);
        var encounter = ModelDb.Encounter<TestSubjectBoss>();
        double Share(int hp) => (double)Creature.ScaleHpForMultiplayer(hp, encounter, count, act) / count;
        return new([Share(tough ? 111 : 100),Share(tough ? 212 : 200),Share(tough ? 313 : 300)],
            deadly ? 22 : 20, deadly ? 16 : 14, deadly ? 11 : 10, deadly ? 3 : 2);
    }
}

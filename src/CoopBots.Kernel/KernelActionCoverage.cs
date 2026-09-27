using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

// Candidate coverage only. Actual branches and complete outcomes still decide
// whether to spend the energy. A zero-damage support must survive the damage
// shortlist before that comparison is possible (live R5 PiercingWail).
internal static class KernelActionCoverage
{
    internal static bool TeamDefence(CardModel card) => card is PiercingWail or DarkShackles
        || card.TargetType == TargetType.AllEnemies && card.DynamicVars.Values.Any(v =>
            v.Name is "Weak" or "WeakPower" or "StrengthLoss");

    internal static bool Cleanup(CardModel card) => card is SecondWind or TrueGrit or BurningPact
        or FiendFire or Cinder or Prepared or CalculatedGamble or Survivor or Purity;

    internal static bool HasHarmfulHand(KernelSession state, IReadOnlyList<Player> party)
        => party.Any(p => state.Hp(p.Creature) > 0 && state.Hand(p)
            .Any(c => c.Type is CardType.Status or CardType.Curse));
}

using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel.Vendor;

internal sealed partial class SimulatedCombatState
{
    private ForkableDictionary<(Creature Possessor, Creature Target), int>? _stolenStrength;
    private ForkableDictionary<(Creature Possessor, Creature Target), int>? _stolenDexterity;

    private void RecordPossessedStatChange(PowerModel power, int applied, Creature? applier)
    {
        if (applied >= 0 || applier == null || power.Owner.Player == null)
            return;
        ForkableDictionary<(Creature, Creature), int>? ledger = power switch
        {
            StrengthPower when GetAmount<PossessStrengthPower>(applier) > 0
                => _stolenStrength ??= [],
            DexterityPower when GetAmount<PossessSpeedPower>(applier) > 0
                => _stolenDexterity ??= [],
            _ => null,
        };
        if (ledger == null)
            return;
        (Creature, Creature) key = (applier, power.Owner);
        ledger[key] = ledger.GetValueOrDefault(key) + applied;
    }

    public void RefundPossessedStats(Creature possessor)
    {
        Refund<StrengthPower>(_stolenStrength, possessor);
        Refund<DexterityPower>(_stolenDexterity, possessor);
    }

    private void Refund<T>(
        ForkableDictionary<(Creature Possessor, Creature Target), int>? ledger,
        Creature possessor)
        where T : PowerModel
    {
        if (ledger == null)
            return;
        foreach (((Creature owner, Creature target), int stolen) in ledger
                     .Where(entry => entry.Key.Possessor == possessor)
                     .ToArray())
        {
            Apply<T>(target, -stolen, null);
            ledger.Remove((owner, target));
        }
    }

    private void AppendPossessFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        AppendPossessLedger(ref fingerprint, 's', _stolenStrength);
        AppendPossessLedger(ref fingerprint, 'y', _stolenDexterity);
    }

    private static void AppendPossessLedger(
        ref StateFingerprintBuilder fingerprint,
        char marker,
        IReadOnlyDictionary<(Creature Possessor, Creature Target), int>? ledger)
    {
        if (ledger == null)
            return;
        foreach (((Creature possessor, Creature target), int amount) in ledger
                     .OrderBy(entry => entry.Key.Possessor.CombatId)
                     .ThenBy(entry => entry.Key.Target.CombatId))
        {
            fingerprint.Add(marker);
            fingerprint.Add(possessor.CombatId ?? uint.MaxValue);
            fingerprint.Add(target.CombatId ?? uint.MaxValue);
            fingerprint.Add(amount);
        }
    }
}

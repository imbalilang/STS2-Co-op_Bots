// Backport source: CombatSolver 0.41.0
//   upstream path: src/Search/PowerCardValuation/Commitments/PowerCommitment.cs
//   upstream commit: 0e6cc2df342ed300c854013a7ec667eae4f58861
// Namespace adapted from CombatSolver to CoopBots.Kernel.Vendor.PowerSync; body unchanged.
namespace CoopBots.Kernel.Vendor.PowerSync;

internal sealed record PowerCommitment(
    PowerCommitmentFamily Family,
    PowerRoutePriority Priority,
    IReadOnlyList<string> Cards,
    int OpenedTurn,
    int OpenedActionCount,
    int OpenedHistoryEntryCount,
    int RoundTransitions,
    int LastEvidenceTurn,
    int Investment,
    int ProvisionalPotential,
    int ProgressEvidence,
    int RealizedEvidence,
    int PowerCardsPlayed)
{
    public int NetUnrealizedValue => (int)Math.Clamp(
        (long)ProvisionalPotential - Investment,
        int.MinValue,
        int.MaxValue);

    public bool HasCard(string cardId)
    {
        for (int index = 0; index < Cards.Count; index++)
        {
            if (string.Equals(Cards[index], cardId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

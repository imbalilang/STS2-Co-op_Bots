// Backport source: CombatSolver 0.41.0
//   upstream path: src/Search/PowerCardValuation/Commitments/PowerCommitmentSeatPolicy.cs
//   upstream commit: 0e6cc2df342ed300c854013a7ec667eae4f58861
// Namespace adapted from CombatSolver to CoopBots.Kernel.Vendor.PowerSync; body unchanged.
namespace CoopBots.Kernel.Vendor.PowerSync;

internal static class PowerCommitmentSeatPolicy
{
    internal static int SeatQuota(int beamWidth, bool aggressive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(beamWidth);
        int ordinaryFloor = (beamWidth + 1) / 2;
        int maximumPowerSeats = beamWidth - ordinaryFloor;
        int requested = aggressive
            ? Math.Min(beamWidth / 2, Math.Max(4, (beamWidth + 2) / 3))
            : Math.Clamp(beamWidth / 12, 2, 12);
        return Math.Min(maximumPowerSeats, requested);
    }
}

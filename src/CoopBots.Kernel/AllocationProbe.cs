using System.Text;

namespace CoopBots.Kernel;

/// <summary>
/// Measurement-only allocation probe. Disabled unless
/// <c>mods/CoopBots/alloc-probe.flag</c> exists (or the test harness sets
/// <c>COOPBOTS_ALLOC_PROBE=1</c>). When disabled every call is a stack-only
/// struct plus a branch; when enabled it adds per-phase
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> deltas with
/// <see cref="Interlocked"/>, so it is safe on the parallel tournament workers.
///
/// It does not alter combat or policy rules. Enabled instrumentation adds cost
/// and can affect a wall-clock cutoff. The counters are reset at the end of a
/// tournament decision by <c>KernelCombatPlanner.ReportTournamentCost</c>.
/// </summary>
public static class AllocationProbe
{
    public enum Phase
    {
        Fork,
        StateFork,
        CombatStateFork,
        PileFork,
        CardFork,
        StoreFork,
        HistoryFork,
        Coverage,
        Play,
        RolloutStep,
        CompactKey,
        // Fine-grained splits inside SimulatedCombatState.Fork.
        SimLists,
        SimPowers,
        SimHookCaches,
        SimCards,
        SimMisc,
        // Inclusive caller totals: each Core MoveNext ends before the yield.
        // Scope calls count segments, BranchEnums counts actual enumerations.
        BranchRolloutPlay,
        BranchTacticalScoringMode,
        BranchTacticalFullChoiceMode,
        BranchGeneratedAttack,
        BranchSequenceOpen,
        BranchSequenceSuffix,
        BranchInvestment,
        BranchJointOpening,
        BranchBeam,
        BranchTournamentOpening,
        BranchHoldProbe,
        BranchSetupProbe,
        BranchDefenceScore,
        BranchAuxiliary,
        BranchOther,
        PreviewInitialDetach,
        PreviewSharedDetach,
    }

    private const string FlagName = "alloc-probe.flag";
    private static readonly long[] Bytes = new long[Enum.GetValues<Phase>().Length];
    private static readonly long[] Calls = new long[Bytes.Length];
    private static readonly long[] BranchEnumerations = new long[Bytes.Length];
    private static volatile bool enabled;

    public static bool Enabled => enabled;

    /// <summary>Path next to this assembly (the mod directory for a live install).</summary>
    public static string FlagPath =>
        Path.Combine(Path.GetDirectoryName(typeof(AllocationProbe).Assembly.Location) ?? ".", FlagName);

    /// <summary>
    /// Cheap once-per-decision refresh. The live flag is a file because Steam
    /// launches the game out-of-process, so an environment variable cannot reach it.
    /// </summary>
    public static void RefreshEnabled()
    {
        try
        {
            enabled = File.Exists(FlagPath)
                || string.Equals(Environment.GetEnvironmentVariable("COOPBOTS_ALLOC_PROBE"), "1", StringComparison.Ordinal);
        }
        catch { enabled = false; }
    }

    public readonly struct Scope : IDisposable
    {
        private readonly Phase phase;
        private readonly long start;
        private readonly bool active;

        internal Scope(Phase phase)
        {
            this.phase = phase;
            active = enabled;
            start = active ? GC.GetAllocatedBytesForCurrentThread() : 0;
        }

        public void Dispose()
        {
            if (!active) return;
            var delta = GC.GetAllocatedBytesForCurrentThread() - start;
            Interlocked.Add(ref Bytes[(int)phase], delta);
            Interlocked.Increment(ref Calls[(int)phase]);
        }
    }

    public static Scope Measure(Phase phase) => new(phase);

    // Compile-time metadata only, finite categories, no path output or root
    // fingerprint. Entry choice mode is fixed at the first actual MoveNext.
    internal static Phase CardBranchesPhase(string file, string member, bool scoring)
    {
        var name = file.AsSpan();
        name = name[(name.LastIndexOfAny('/', (char)92) + 1)..];
        if (name.Equals("KernelRollout.cs", StringComparison.Ordinal))
            return member switch
            {
                "TryPlay" or "NativeBranch" => Phase.BranchRolloutPlay,
                "TryTacticalPlay" => scoring ? Phase.BranchTacticalScoringMode : Phase.BranchTacticalFullChoiceMode,
                "GeneratedFollowupValue" => Phase.BranchGeneratedAttack,
                "ShouldHold" => Phase.BranchHoldProbe,
                "RelevantRemaining" or "FollowupsAfterSetup" or "WastesEnergyGain" => Phase.BranchSetupProbe,
                "Score" => Phase.BranchDefenceScore,
                _ => Phase.BranchOther,
            };
        if (name.Equals("KernelSequenceOrder.cs", StringComparison.Ordinal))
            return member switch { "Open" => Phase.BranchSequenceOpen, "Then" => Phase.BranchSequenceSuffix, _ => Phase.BranchOther };
        if (name.Equals("KernelInvestmentProbe.cs", StringComparison.Ordinal) && member == "Step") return Phase.BranchInvestment;
        if (name.Equals("KernelJointTurnRun.cs", StringComparison.Ordinal) && member == "NativeReferenceOpening") return Phase.BranchJointOpening;
        if (name.Equals("KernelTeamSearch.cs", StringComparison.Ordinal) && member == "Expand") return Phase.BranchBeam;
        if (name.Equals("KernelTournament.cs", StringComparison.Ordinal) && member is "FirstActions" or "TryOpenNextRollout") return Phase.BranchTournamentOpening;
        if (name.Equals("KernelExhaustPolicy.cs", StringComparison.Ordinal) && member == "ShouldHold") return Phase.BranchHoldProbe;
        if (name.Equals("KernelEndTurnRun.cs", StringComparison.Ordinal) && member == "Compare"
            || name.Equals("KernelSession.cs", StringComparison.Ordinal) && member == "HasBeneficialAttack"
            || name.Equals("KernelSession.PotionOffers.cs", StringComparison.Ordinal) && member == "GeneratedCardBenefit"
            || name.Equals("KernelTeamCoordination.cs", StringComparison.Ordinal) && member == "SetupPayoff") return Phase.BranchAuxiliary;
        return Phase.BranchOther;
    }

    internal static void ObserveCardBranchesEnumeration(Phase phase)
    { if (enabled) Interlocked.Increment(ref BranchEnumerations[(int)phase]); }


    /// <summary>One compact line per tournament decision; zeroes the counters for the next one.</summary>
    public static string ReportAndReset()
    {
        if (!enabled) return "";
        var text = new StringBuilder(192);
        for (var i = 0; i < Bytes.Length; i++)
        {
            var bytes = Interlocked.Exchange(ref Bytes[i], 0);
            var calls = Interlocked.Exchange(ref Calls[i], 0);
            if (calls == 0 && bytes == 0) continue;
            if (text.Length > 0) text.Append(", ");
            text.Append((Phase)i).Append('=')
                .Append(bytes / 1048576.0).Append("MB/")
                .Append(calls);
        }
        var hasEnums = false;
        for (var i = 0; i < BranchEnumerations.Length; i++)
        {
            var count = Interlocked.Exchange(ref BranchEnumerations[i], 0);
            if (count == 0) continue;
            if (!hasEnums) { if (text.Length > 0) text.Append(", "); text.Append("branchEnums=["); }
            else text.Append(',');
            text.Append((Phase)i).Append(':').Append(count);
            hasEnums = true;
        }
        if (hasEnums) text.Append(']');
        return text.Length == 0 ? "none" : text.ToString();
    }

    /// <summary>Test-only: force the probe on without a flag file.</summary>
    internal static void ForceEnabled(bool value) => enabled = value;
}

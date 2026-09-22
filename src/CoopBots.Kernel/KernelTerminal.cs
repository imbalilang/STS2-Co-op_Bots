using MegaCrit.Sts2.Core.Entities.Players;

namespace CoopBots.Kernel;

/// <summary>
/// Whether a line's ending was actually produced by the simulation, or is the place a
/// budget/limit stopped it. The plan keeps these apart on purpose (§02: "必须与游戏胜负分开";
/// §04: "截断不算团灭") because collapsing them makes a truncated search indistinguishable
/// from a real result — the failure is silent and moves every number downstream.
/// </summary>
public enum EvidenceKind
{
    /// <summary>
    /// The fight ended, and the simulation produced the ending.
    ///
    /// WHY THIS IS NOT BACKED BY AN EXPLICIT "no truncation on this path" FLAG, which the plan
    /// asks for (§02 EvidenceKind, §P1-04): in this codebase a recorded step is clean BY
    /// CONSTRUCTION, so such a flag could never fail and would be a green check that proves
    /// nothing (R5b). <c>KernelSession.Play</c> returns <c>boundary.Length == 0</c> as success
    /// (KernelSession.cs:613), so a successful step has an empty boundary; and
    /// <c>KernelTeamSearch</c>'s expansion skips any branch with a non-empty boundary
    /// (`if (boundary.Length > 0) { boundaries[key]++; continue; }`) instead of creating a
    /// node. Both producers therefore only ever record clean steps, and the refusal contract is
    /// separately guarded by KernelEngineScenarios.cs:1375.
    ///
    /// The failure mode actually worth fearing here is the OPPOSITE one: refusing too much.
    /// B11 is the worked example — a fail-closed <c>return false</c> on a two-phase boss made
    /// every killing line unreachable, so the best available line was "survive five more
    /// rounds" and the fight ran 25 rounds without ending.
    /// </summary>
    VerifiedTerminal,
    /// <summary>The line stopped early. <see cref="TerminalRecord.CutoffReason"/> says why.</summary>
    EstimatedCutoff,
}

/// <summary>
/// The single end-of-battle record the plan asks for (§02 "统一终局记录"). Everything the
/// objective is allowed to look at lives here, so "what does good mean" is one readable
/// structure instead of constants scattered through the search.
/// </summary>
/// <param name="Victory">True only for a victory the simulation actually produced.</param>
/// <param name="Kind">Verified terminal vs cutoff. A cutoff is never a defeat.</param>
/// <param name="CutoffReason">Empty for a verified terminal; names the limit otherwise.</param>
/// <param name="PostCombatHp">Per seat, indexed like the party the rollout was given.</param>
/// <param name="IrreversibleLoss">
/// Seats that ENDED the fight dead — not seats that were reduced to zero at some point.
/// The engine models death saves (relic/potion revives) and charges them a measured premium
/// (ActEndingBossPolicy.DeathSavePremium), so a seat that was saved is not a seat that died.
/// This is the plan's "临时倒地不能自动等同永久损失" made concrete, and it is measured in
/// seats rather than in HP because that is the quantity the game actually makes irreversible.
/// </param>
/// <param name="ResourcesConsumed">
/// Cross-battle resources spent by this line. The baseline policies never drink, so a
/// rollout of today reports 0 here; the field is populated and compared anyway so the third
/// key of the comparator is live the moment a policy uses a potion. See the note on
/// <see cref="ObjectiveConfig.ConsumableUnitCost"/>.
/// </param>
public sealed record TerminalRecord(
    bool Victory,
    EvidenceKind Kind,
    string CutoffReason,
    IReadOnlyList<int> PostCombatHp,
    int IrreversibleLoss,
    double ResourcesConsumed,
    // Cards/gold still held by a thief at the end of the line. This is not HP: a line that
    // let a Thieving Hopper escape at full health has permanently thinned the deck, and the
    // 2026-09-22 live report was exactly that — the bot blocked the steal, survived, and lost
    // a key card. The simulator already counts this in SimulatedCombatState.Theft; the
    // terminal record just has to carry it so the tournament can compare it.
    int OutstandingStolenResource = 0)
{
    public bool Verified => Kind == EvidenceKind.VerifiedTerminal;

    /// <summary>
    /// Read the record off a session. This is the ONLY place a record is built, so the search
    /// and the rollout cannot drift apart on what "the party ended the fight like this" means.
    ///
    /// IrreversibleLoss counts seats still dead at the END, not seats that touched zero along
    /// the way: the engine models death saves, so a seat that was revived is alive here and is
    /// not charged. That is the plan's "临时倒地不能自动等同永久损失", and it is why a party
    /// wipe loses to any surviving line without needing a 100,000,000 constant to say so.
    /// </summary>
    /// <param name="terminal">
    /// True only when the simulation really ended the fight. Anything else is a cutoff, and the
    /// loss count is then reported as ZERO rather than inferred from current HP: an unfinished
    /// fight has unknown losses, and writing a number there would be a fabricated defeat —
    /// the plan's "截断不算团灭".
    /// </param>
    public static TerminalRecord Capture(KernelSession session, IReadOnlyList<Player> party,
        bool terminal, string cutoffReason = "")
    {
        var hp = new int[party.Count];
        var dead = 0;
        for (var i = 0; i < party.Count; i++)
        {
            hp[i] = Math.Max(0, session.Hp(party[i].Creature));
            if (hp[i] <= 0) dead++;
        }
        return new TerminalRecord(
            Victory: terminal && session.HasWon,
            Kind: terminal ? EvidenceKind.VerifiedTerminal : EvidenceKind.EstimatedCutoff,
            CutoffReason: cutoffReason,
            PostCombatHp: hp,
            IrreversibleLoss: terminal ? dead : 0,
            ResourcesConsumed: 0,
            OutstandingStolenResource: session.OutstandingStolenResource);
    }
}

/// <summary>
/// The product parameters of the objective, versioned rather than hidden in code (§02: "都应
/// 作为版本化配置，而不是隐藏在搜索代码里").
///
/// The defaults reproduce the objective the project already has — team net HP after the fight
/// — so switching to this comparator does not silently change what the bot is optimising for.
/// The reserve curve is off by default because it IS a different objective, not a refinement.
/// </summary>
/// <param name="ReserveFloor">tau_i: the post-fight HP each seat should stay above.</param>
/// <param name="ReservePenalty">b_i: how hard a seat below its floor is punished.</param>
/// <param name="ConsumableUnitCost">What one consumable is worth in HP terms.</param>
public sealed record ObjectiveConfig(
    IReadOnlyList<int>? ReserveFloor = null,
    double ReservePenalty = 0,
    double ConsumableUnitCost = 0)
{
    public static readonly ObjectiveConfig NetPostCombatHp = new();
}

/// <summary>
/// The plan's default comparator (§02):
/// <code>Score = lexicographic(Victory, -IrreversibleLoss, ResourceUtility)</code>
///
/// Compared field by field, in order — NOT summed into one number with large constants.
/// The scalar that this replaces reads
/// <c>-(deaths == party.Length ? 100_000_000 : 0) + (victory ? 10_000_000 : 0) - tactical</c>
/// plus a <c>HorizonBonus = 2_000_000</c> that exists only to sit above the tactical clamp and
/// below victory. A lexicographic order makes those constants unnecessary: a cutoff is not a
/// slightly-worse terminal, it is a different category, so no magnitude has to be chosen to
/// express that.
///
/// ORDER OF THE TWO NON-WIN TIERS: a verified defeat is a FACT (the rollout saw the party
/// die) while a cutoff is an UNKNOWN (budget, step cap, or an unresolved boundary). In a
/// coverage-complete simulator "known fact" is the conservative rank; in this project it is
/// not, because `Tools of the Trade` and the two-stage boss boundaries can truncate every
/// line at the same point. Under that coverage gap, ranking `VerifiedDefeat` above `Cutoff`
/// actively selects a known losing line over an unmodeled one — measured in the 2026-09-22
/// final boss, which finished 32 WIPE / 23 pending-choice decisions. An unknown line is not
/// a defeat (R4/R5b), so it must not lose to one; it still loses to a real victory.
/// </summary>
public static class TerminalComparer
{
    /// <summary>Positive when <paramref name="a"/> is better than <paramref name="b"/>.</summary>
    public static int Compare(TerminalRecord a, TerminalRecord b, ObjectiveConfig? config = null)
    {
        // 1. A real victory beats everything that is not one. Between the two non-win
        //    categories, an unmodeled/cut-off line outranks a verified wipe: the cutoff is
        //    unknown, not a loss, and preferring the wipe turns a simulator hole into a
        //    deliberate losing move.
        var victory = Rank(a).CompareTo(Rank(b));
        if (victory != 0) return victory;

        // 2. Fewer irreversibly lost seats is better.
        var loss = b.IrreversibleLoss.CompareTo(a.IrreversibleLoss);
        if (loss != 0) return loss;

        // 3. Fewer unrecovered stolen cards/gold is better. A thief that escapes is a
        //    permanent deck/gold loss, not a hit point; the live report was a bot that
        //    out-blocked the theft and lost a key card while ending at high HP.
        var theft = b.OutstandingStolenResource.CompareTo(a.OutstandingStolenResource);
        if (theft != 0) return theft;

        // 4. Then the resources the plan costs the party.
        var utility = ResourceUtility(a, config).CompareTo(ResourceUtility(b, config));
        if (utility != 0) return utility;
        return 0;
    }

    private enum Tier { VerifiedDefeat = 0, Cutoff = 1, VerifiedVictory = 2 }

    private static Tier Rank(TerminalRecord r) =>
        !r.Verified ? Tier.Cutoff
        : r.Victory ? Tier.VerifiedVictory
        : Tier.VerifiedDefeat;

    /// <summary>
    /// The plan's R (§03): <c>sum_i u_i(HP_i) - ConsumableCost</c> with
    /// <c>u_i(h) = a_i*h - b_i*max(0, tau_i - h)^2 / max(1, tau_i)</c>, and <c>a_i = 1</c>.
    ///
    /// The reserve term is a SQUARED shortfall normalised by the floor, so dipping just under
    /// the line costs little and being nearly dead costs a lot. With b = 0 (the default) this
    /// is exactly "team net HP", which is what the project already optimises for.
    /// </summary>
    public static double ResourceUtility(TerminalRecord record, ObjectiveConfig? config = null)
    {
        var c = config ?? ObjectiveConfig.NetPostCombatHp;
        double total = 0;
        for (var i = 0; i < record.PostCombatHp.Count; i++)
        {
            var hp = record.PostCombatHp[i];
            total += hp;
            if (c.ReservePenalty <= 0) continue;
            var floor = c.ReserveFloor is not null && i < c.ReserveFloor.Count ? c.ReserveFloor[i] : 0;
            if (floor <= 0 || hp >= floor) continue;
            var shortfall = floor - hp;
            total -= c.ReservePenalty * shortfall * shortfall / Math.Max(1, floor);
        }
        return total - record.ResourcesConsumed * c.ConsumableUnitCost;
    }
}

using System.Runtime.CompilerServices;
using CoopBots.Kernel.Vendor.PowerSync;
using MegaCrit.Sts2.Core.Entities.Players;

[assembly: InternalsVisibleTo("PatchSmoke")]

namespace CoopBots.Kernel;

/// <summary>
/// Per-owner immutable commitment history carried by one search node. The
/// entry set only ever controls which power-route lines survive the bounded
/// frontier; it never enters team evaluation, so <see cref="KernelTeamSearch" />
/// score ordering is unchanged by construction.
/// </summary>
internal sealed class KernelPowerLedger
{
    public static readonly KernelPowerLedger Empty = new(new SortedDictionary<ulong, PowerCommitment>());

    private readonly SortedDictionary<ulong, PowerCommitment> commitments;

    private KernelPowerLedger(SortedDictionary<ulong, PowerCommitment> commitments)
        => this.commitments = commitments;

    public bool IsEmpty => commitments.Count == 0;
    public int Count => commitments.Count;
    public IEnumerable<ulong> OwnerIds => commitments.Keys;

    public bool TryGet(ulong ownerId, out PowerCommitment commitment)
        => commitments.TryGetValue(ownerId, out commitment!);

    public IEnumerable<KernelPowerKey> Keys
    {
        get
        {
            foreach (var pair in commitments)
            {
                if (pair.Value.Family != PowerCommitmentFamily.None)
                    yield return new KernelPowerKey(pair.Key, pair.Value.Family);
            }
        }
    }

    public KernelPowerLedger With(ulong ownerId, PowerCommitment commitment)
    {
        var copy = new SortedDictionary<ulong, PowerCommitment>(commitments);
        copy[ownerId] = commitment;
        return new KernelPowerLedger(copy);
    }

    public KernelPowerLedger Without(ulong ownerId)
    {
        if (!commitments.ContainsKey(ownerId)) return this;
        var copy = new SortedDictionary<ulong, PowerCommitment>(commitments);
        copy.Remove(ownerId);
        return new KernelPowerLedger(copy);
    }

    /// <summary>
    /// Deterministic full decision-relevant commitment state so dedup cannot
    /// silently merge routes that share owner+family but differ in expiry,
    /// remaining potential, cards, progress/realized evidence, investment or any
    /// other field the retention/rank logic reads. Owners iterate in id order
    /// (the backing store is sorted), so identical states always yield the same
    /// string and different states always differ.
    /// </summary>
    public string Signature()
    {
        if (IsEmpty) return "";
        var text = new System.Text.StringBuilder(160);
        foreach (var pair in commitments)
        {
            var commitment = pair.Value;
            text.Append(pair.Key).Append(':')
                .Append((int)commitment.Family).Append(':')
                .Append((int)commitment.Priority).Append(':')
                .Append(commitment.OpenedTurn).Append(':')
                .Append(commitment.OpenedActionCount).Append(':')
                .Append(commitment.OpenedHistoryEntryCount).Append(':')
                .Append(commitment.RoundTransitions).Append(':')
                .Append(commitment.LastEvidenceTurn).Append(':')
                .Append(commitment.Investment).Append(':')
                .Append(commitment.ProvisionalPotential).Append(':')
                .Append(commitment.ProgressEvidence).Append(':')
                .Append(commitment.RealizedEvidence).Append(':')
                .Append(commitment.PowerCardsPlayed).Append(':');
            foreach (var card in commitment.Cards)
                text.Append(card).Append(',');
            text.Append(';');
        }
        return text.ToString();
    }
}

internal readonly record struct KernelPowerKey(ulong OwnerId, PowerCommitmentFamily Family);

/// <summary>
/// Data-only catalog over the six upstream 0.41.0 pool route policies. An
/// unknown card id, or a card whose upstream policy disables in-combat
/// commitments, produces no descriptor so unknown/modelled-elsewhere powers do
/// not create progress-protected seats.
/// </summary>
internal static class KernelPowerCatalog
{
    internal static bool TryDescribe(string cardId, out PowerCommitmentDescriptor descriptor)
    {
        if (string.IsNullOrEmpty(cardId))
        {
            descriptor = default;
            return false;
        }
        var family = FamilyFor(cardId);
        if (family == PowerCommitmentFamily.None)
        {
            descriptor = default;
            return false;
        }
        var policy = PolicyFor(cardId);
        if (policy.NoInCombatCommitment)
        {
            descriptor = default;
            return false;
        }
        descriptor = new PowerCommitmentDescriptor(PoolFor(cardId), cardId, family, policy);
        return true;
    }

    internal static PowerCommitmentFamily FamilyFor(string cardId)
    {
        var family = IroncladPowerRoutePolicy.FamilyFor(cardId);
        if (family != PowerCommitmentFamily.None) return family;
        family = SilentPowerRoutePolicy.FamilyFor(cardId);
        if (family != PowerCommitmentFamily.None) return family;
        family = DefectPowerRoutePolicy.FamilyFor(cardId);
        if (family != PowerCommitmentFamily.None) return family;
        family = RegentPowerRoutePolicy.FamilyFor(cardId);
        if (family != PowerCommitmentFamily.None) return family;
        family = NecrobinderPowerRoutePolicy.FamilyFor(cardId);
        if (family != PowerCommitmentFamily.None) return family;
        return ColorlessPowerRoutePolicy.FamilyFor(cardId);
    }

    internal static PowerRouteAdmissionPolicy PolicyFor(string cardId)
    {
        if (IroncladPowerRoutePolicy.FamilyFor(cardId) != PowerCommitmentFamily.None)
            return IroncladPowerRoutePolicy.For(cardId);
        if (SilentPowerRoutePolicy.FamilyFor(cardId) != PowerCommitmentFamily.None)
            return SilentPowerRoutePolicy.For(cardId);
        if (DefectPowerRoutePolicy.FamilyFor(cardId) != PowerCommitmentFamily.None)
            return DefectPowerRoutePolicy.For(cardId);
        if (RegentPowerRoutePolicy.FamilyFor(cardId) != PowerCommitmentFamily.None)
            return RegentPowerRoutePolicy.For(cardId);
        if (NecrobinderPowerRoutePolicy.FamilyFor(cardId) != PowerCommitmentFamily.None)
            return NecrobinderPowerRoutePolicy.For(cardId);
        return ColorlessPowerRoutePolicy.For(cardId);
    }

    private static PowerCardPool PoolFor(string cardId)
    {
        if (IroncladPowerRoutePolicy.FamilyFor(cardId) != PowerCommitmentFamily.None)
            return PowerCardPool.Ironclad;
        if (SilentPowerRoutePolicy.FamilyFor(cardId) != PowerCommitmentFamily.None)
            return PowerCardPool.Silent;
        if (DefectPowerRoutePolicy.FamilyFor(cardId) != PowerCommitmentFamily.None)
            return PowerCardPool.Defect;
        if (RegentPowerRoutePolicy.FamilyFor(cardId) != PowerCommitmentFamily.None)
            return PowerCardPool.Regent;
        if (NecrobinderPowerRoutePolicy.FamilyFor(cardId) != PowerCommitmentFamily.None)
            return PowerCardPool.Necrobinder;
        return PowerCardPool.Colorless;
    }
}

/// <summary>
/// Adapter from the upstream pure commitment lifecycle to one production
/// <see cref="KernelSession" /> branch step. Ownership comes from the played
/// action's <see cref="Player" />; all trigger evidence is computed from the
/// owner's own branch piles through <see cref="KernelSession.PersistentValue" />,
/// so a teammate's shivs or block never justify another owner's route.
/// </summary>
internal static class KernelPowerRouter
{
    private const int MaximumTransitions = 2;

    internal static KernelPowerLedger Advance(
        KernelPowerLedger parent,
        KernelSession before,
        KernelSession after,
        KernelTeamSearch.Action action,
        IReadOnlyList<Player> actors)
    {
        var owner = action.Player;
        var played = action.Card;
        // A won battle is a terminal branch: drop every owner's protection before
        // any creation/advance so an immediate win can never keep or create a
        // commitment (and a power-caused death can never do so either).
        if (after.HasWon) return KernelPowerLedger.Empty;
        var descriptor = default(PowerCommitmentDescriptor);
        bool registered = false;
        if (played is not null
            && KernelPowerCatalog.TryDescribe(played.Id.Entry, out descriptor))
        {
            registered = true;
        }
        // Fast path: no lineage to preserve and this action is not a registered
        // power, so nothing is scanned and no PersistentValue is evaluated.
        if (parent.IsEmpty && !registered) return parent;

        var next = parent;
        bool ownerHandledByPower = false;
        if (registered && after.Hp(owner.Creature) > 0)
        {
            if (TryBuildPotential(before, after, owner, descriptor, out int potential, out int investment))
            {
                var (progress, realized) = Evidence(before, after, owner);
                if (next.TryGet(owner.NetId, out var existing))
                {
                    next = next.With(owner.NetId, PowerCommitmentLifecycle.AddPower(
                        existing, descriptor, investment, potential, progress, realized, Turn(after)));
                }
                else
                {
                    next = next.With(owner.NetId, PowerCommitmentLifecycle.Create(
                        descriptor, Turn(after), 0, 0, investment, potential));
                }
                ownerHandledByPower = true;
            }
        }

        if (next.IsEmpty) return next;
        foreach (var ownerId in next.OwnerIds.ToArray())
        {
            if (ownerHandledByPower && ownerId == owner.NetId) continue;
            next.TryGet(ownerId, out var commitment);
            var actor = actors.FirstOrDefault(p => p.NetId == ownerId);
            if (actor is null || after.Hp(actor.Creature) <= 0 || after.HasWon)
            {
                next = next.Without(ownerId);
                continue;
            }
            var (progress, realized) = Evidence(before, after, actor);
            var advanced = PowerCommitmentLifecycle.Advance(
                commitment, Turn(before), Turn(after), MaximumTransitions,
                progress, realized, terminal: false);
            next = advanced.Commitment is null
                ? next.Without(ownerId)
                : next.With(ownerId, advanced.Commitment);
        }
        return next;
    }

    /// <summary>
    /// Conservative trigger/admission: a route only opens when the owner's own
    /// branch shows an observable strategic delta (<see cref="KernelSession.PersistentValue" />)
    /// or an immediate defense gain. No per-card projection exists in this
    /// vendor, so <c>ProjectedPotential</c> stays zero and upstream policies that
    /// require positive projection keep their old behaviour (no commitment).
    /// </summary>
    private static bool TryBuildPotential(
        KernelSession before,
        KernelSession after,
        Player owner,
        in PowerCommitmentDescriptor descriptor,
        out int potential,
        out int investment)
    {
        int spent = Math.Max(0, before.Energy(owner) - after.Energy(owner));
        int setupGain = Math.Max(0, Persistent(after, owner) - Persistent(before, owner));
        int immediateDefense = Math.Max(0, after.Block(owner.Creature) - before.Block(owner.Creature));
        bool hasTriggerEvidence = setupGain > 0 || immediateDefense > 0;
        investment = PowerActivationInvestmentPolicy.EnergyInvestment(
            spent, totalFloor: 0, combatTurnOffset: Math.Max(0, after.RoundsAdvanced));
        var admission = PowerRouteAdmission.Evaluate(
            new PowerRouteAdmissionInput(
                descriptor.CardId,
                IsAutoPlay: false,
                spent,
                after.Energy(owner),
                hasTriggerEvidence,
                immediateDefense,
                SetupGain: setupGain,
                ProjectedPotential: 0,
                TriggerProjectionFloor: 0,
                investment),
            descriptor.Admission);
        potential = admission.Potential;
        return admission.Admitted;
    }

    private static int Persistent(KernelSession state, Player owner)
    {
        var value = state.PersistentValue(owner, 0, 1);
        if (double.IsNaN(value) || value <= 0) return 0;
        return (int)Math.Min(int.MaxValue, value);
    }

    /// <summary>
    /// Noncausal progress/release facts. Realized damage and owner-side defensive
    /// progress only release a protected seat; they are never added to score.
    /// </summary>
    private static (int Progress, int Realized) Evidence(
        KernelSession before,
        KernelSession after,
        Player owner)
    {
        int enemyBefore = before.Enemies.Sum(e => before.Hp(e));
        int enemyAfter = after.Enemies.Sum(e => after.Hp(e));
        int realized = Math.Max(0, enemyBefore - enemyAfter);
        int progress = Math.Max(0, after.Block(owner.Creature) - before.Block(owner.Creature))
            + Math.Max(0, after.Hp(owner.Creature) - before.Hp(owner.Creature));
        return (progress, realized);
    }

    private static int Turn(KernelSession state) => Math.Max(0, state.RoundsAdvanced);
}

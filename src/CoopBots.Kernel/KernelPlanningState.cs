using MegaCrit.Sts2.Core.Models;
using System.Text;

namespace CoopBots.Kernel;

/// <summary>
/// Exact equality evidence for supported, settled states of one battle. This
/// is deliberately not a transposition hash or a proof for unknown mechanics.
/// </summary>
public sealed class KernelPlanningState
{
    private readonly object battle;
    private readonly CardModel[] physicalCards;
    private readonly string? reuseFailure;
    internal KernelPlanningState(object battle, string semantics, CardModel[] physicalCards, bool reusable, string? reuseFailure = null)
    {
        this.battle = battle;
        Semantics = semantics;
        this.physicalCards = physicalCards;
        Reusable = reusable;
        this.reuseFailure = reuseFailure;
    }

    public string Semantics { get; }
    /// <summary>
    /// Exact branch scoring values relative to their captured search root.
    /// These remain in the simulator's evaluation/fork machinery; they do not
    /// authorize native reuse and are deliberately separate from future state.
    /// </summary>
    public string SearchMetadata { get; internal init; } = "";
    public bool Reusable { get; }
    public bool Matches(KernelPlanningState other)
    {
        if (!Reusable || !other.Reusable || !ReferenceEquals(battle, other.battle)
            || !string.Equals(Semantics, other.Semantics, StringComparison.Ordinal)
            || physicalCards.Length != other.physicalCards.Length) return false;
        for (var i = 0; i < physicalCards.Length; i++)
            if (!ReferenceEquals(physicalCards[i], other.physicalCards[i])) return false;
        return true;
    }

    /// <summary>
    /// Observes the same first failed axis as Matches. Qualification and object
    /// identity failures are cache misses, not evidence of simulation drift.
    /// Only stored evidence is read; no native state is recaptured here.
    /// </summary>
    public string? FirstMismatch(KernelPlanningState other)
    {
        if (!Reusable || !other.Reusable)
            return $"cacheMissAxis=qualification field=reusable expected={Reusable} actual={other.Reusable} "
                + $"expectedReason={ReuseReason()} actualReason={other.ReuseReason()}";
        if (!ReferenceEquals(battle, other.battle))
            return "cacheMissAxis=battle-reference field=battle.same-reference expected=True actual=False";
        if (!string.Equals(Semantics, other.Semantics, StringComparison.Ordinal))
            return FirstSemanticMismatch(Semantics, other.Semantics);
        if (physicalCards.Length != other.physicalCards.Length)
            return $"cacheMissAxis=card-count field=physical-card-count expected={physicalCards.Length} actual={other.physicalCards.Length}";
        for (var i = 0; i < physicalCards.Length; i++)
            if (!ReferenceEquals(physicalCards[i], other.physicalCards[i]))
                return $"cacheMissAxis=card-reference field=card[{i}].physical-reference "
                    + $"expected={Bounded(physicalCards[i].Id.Entry.AsSpan(), 48)} "
                    + $"actual={Bounded(other.physicalCards[i].Id.Entry.AsSpan(), 48)} sameReference=False";
        return null;
    }

    private string ReuseReason()
    {
        if (Reusable) return "available";
        if (reuseFailure != "facts-unavailable") return reuseFailure ?? "unqualified";
        // Mechanism facts already contain a qualified failure marker. Report
        // only its short reason, never the huge captured payload around it.
        const string marker = "!unavailable[";
        var start = Semantics.IndexOf(marker, StringComparison.Ordinal);
        if (start >= 0)
        {
            start += marker.Length;
            var end = Semantics.IndexOf(']', start);
            return "facts-unavailable:" + Bounded(Semantics.AsSpan(start,
                end < 0 ? Semantics.Length - start : end - start), 80);
        }
        return "facts-unavailable";
    }

    private static string FirstSemanticMismatch(string expected, string actual)
    {
        var offset = 0;
        var shared = Math.Min(expected.Length, actual.Length);
        while (offset < shared && expected[offset] == actual[offset]) offset++;
        var e = Segment(expected, offset);
        var a = Segment(actual, offset);
        if (!string.Equals(e.Name, a.Name, StringComparison.Ordinal))
            return $"cacheMissAxis=semantics field=field-order expected={e.Name} actual={a.Name} offset={offset}";
        return $"cacheMissAxis=semantics field={e.Name} expected={e.Value} actual={a.Value} offset={offset}";
    }

    private static (string Name, string Value) Segment(string text, int offset)
    {
        if (offset >= text.Length) return ("<end>", "<end>");
        var begin = offset;
        while (begin > 0 && text[begin - 1] is not (';' or '\n')) begin--;
        var end = offset;
        while (end < text.Length && text[end] is not (';' or '\n')) end++;
        var equals = text.IndexOf('=', begin, end - begin);
        var nameEnd = equals < 0 ? end : equals;
        var nameBegin = begin;
        var lineBegin = begin;
        while (lineBegin > 0 && text[lineBegin - 1] != '\n') lineBegin--;
        var scope = "";
        if (text[lineBegin] == 'p')
        {
            var brace = lineBegin + 1;
            while (brace < text.Length && char.IsAsciiDigit(text[brace])) brace++;
            if (brace < text.Length && text[brace] == '{')
            {
                scope = Bounded(text.AsSpan(lineBegin, brace - lineBegin), 24) + ".";
                if (nameBegin == lineBegin) nameBegin = brace + 1;
            }
        }
        var name = scope + Bounded(text.AsSpan(nameBegin, Math.Max(0, nameEnd - nameBegin)), 80);
        var valueBegin = equals < 0 ? begin : equals + 1;
        var window = Math.Max(valueBegin, offset - 24);
        var length = Math.Min(80, end - window);
        var value = (window > valueBegin ? "..." : "") + Bounded(text.AsSpan(window, length), 80)
            + (window + length < end ? "..." : "");
        return (name, value.Length == 0 ? "<empty>" : value);
    }

    private static string Bounded(ReadOnlySpan<char> value, int maximum)
    {
        var text = new StringBuilder(Math.Min(value.Length, maximum) + 3);
        foreach (var c in value[..Math.Min(value.Length, maximum)])
            text.Append(char.IsControl(c) ? ' ' : c);
        if (value.Length > maximum) text.Append("...");
        return text.ToString();
    }
}

public sealed partial class KernelSession
{
    // Upstream compares full continuation state, not the cheap beam key.
    // Multiplayer also binds seat control and original physical cards. Owner
    // 2026-10-09: exact supported-state equality permits bounded continuation
    // reuse after human input; equal hand/HP alone must never authorize reuse.
    public KernelPlanningState PlanningState(IEnumerable<ulong> controlledPlayers)
    {
        if (liveRoot is null) throw new InvalidOperationException("Planning evidence needs a battle identity.");
        var text = new StringBuilder(PartyStateText());
        text.Append("\nplanning-round=").Append(Combat.RoundNumber)
            .Append(";enemy-phase=").Append(EnemyPhaseCompleted)
            .Append(";horizon-closed=").Append(HorizonClosed)
            .Append(";terminal=").Append(HasTerminal).Append('/').Append(HasWon)
            .Append(";controlled=").AppendJoin(',', controlledPlayers.Order())
            .Append(";synchronous=").AppendJoin(',', synchronousChoicePlayers.Order())
            .Append(";extra-turn=").AppendJoin(',', extraTurnParticipants.Order());
        var cards = new List<CardModel>();
        foreach (var player in liveRoot.Players)
        {
            text.Append(";seat=").Append(player.NetId).Append('/')
                .Append(IsReady(player)).Append('/').Append(CanAct(player))
                .Append('/').Append(Combat.GetPlayerTurnNumber(player))
                .Append('/').Append(CardsPlayedThisTurn(player))
                .Append('/').Append(Combat.GetEnergySpentThisTurn(player));
            cards.AddRange(Hand(player)); cards.AddRange(DrawPile(player));
            cards.AddRange(Discard(player)); cards.AddRange(Exhaust(player));
        }
        foreach (var creature in Combat.Creatures)
            text.Append(";attacks=").Append(creature.CombatId).Append('/')
                .Append(Combat.GetCreatureAttacksThisTurn(creature));
        foreach (var (_, current, previous) in PlanningHistoryCourseAttacks())
        {
            if (current is not null) cards.Add(current.Original);
            if (previous is not null) cards.Add(previous.Original);
        }
        foreach (var (_, pending) in PlanningHistoryCoursePendingAttacks())
            if (pending is not null) cards.Add(pending.Original);
        var mechanismsAvailable = AppendPlanningMechanismFacts(text);
        // A pending native selector or prepared answer is part of an action,
        // not a settled root. This qualifies reuse evidence only; it does not
        // refuse a legal game action or change native choice resolution.
        string? reuseFailure = null;
        return new(liveRoot, text.ToString(), cards.ToArray(),
            Qualify(mechanismsAvailable, "facts-unavailable", ref reuseFailure)
            && Qualify(CanFork, "cannot-fork", ref reuseFailure)
            && Qualify(ProjectionDifferences.Count == 0, "projection-differences", ref reuseFailure)
            && Qualify(!LastActionHadEnvironmentalRisk, "environmental-risk", ref reuseFailure)
            && Qualify(!Combat.HasPendingChoice, "pending-choice", ref reuseFailure)
            && Qualify(!requirePreparedPartyMenu, "prepared-party-selector", ref reuseFailure)
            && Qualify(preparedPartyMenus is not { Count: > 0 }, "prepared-party-answers", ref reuseFailure)
            && Qualify(preparedAfterStartChoices is not { Count: > 0 }, "prepared-after-start-answers", ref reuseFailure),
            reuseFailure) { SearchMetadata = PlanningSearchMetadata() };

        static bool Qualify(bool condition, string reason, ref string? failure)
        {
            if (!condition) failure = reason;
            return condition;
        }
    }
}

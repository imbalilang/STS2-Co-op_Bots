using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots;

// First bounded resource layer. It owns branch data, never changes live piles,
// and deliberately stops at shuffle/unknown drawn-card semantics.
internal static class CombatResourceProjection
{
    internal const int DrawWindow = 6;
    internal static bool ModelsDraw(CardModel card) => card.GetType().Name is
        "BattleTrance" or "Offering" or "PommelStrike" or "ShrugItOff" or "Backflip" or "Finesse" or "FlashOfSteel";
    internal static bool CanEnterFromDraw(CardModel card) => card.GetType().Name is
        "StrikeIronclad" or "DefendIronclad" or "Bash" or "TwinStrike" or "Bludgeon" or "Uppercut"
        or "BattleTrance" or "Offering" or "PommelStrike" or "ShrugItOff" or "Bloodletting" or "Corruption" or "Inflame"
        or "DemonForm" or "Barricade" or "FranticEscape" or "Entrench" or "BodySlam" or "FeelNoPain" or "DarkEmbrace"
        or "Impervious" or "Backflip" or "Finesse" or "FlashOfSteel";

    internal static bool CanExpandDraw(Player player) => !player.Creature.CombatState!.IterateHookListeners().Any(model =>
        model.GetType().Name != "NoDrawPower" && new[] { "ShouldDraw", "AfterCardDrawn", "AfterHandEmptied", "AfterPreventingDraw" }
            .Any(name => model.GetType().GetMethod(name) is { } method && method.DeclaringType != typeof(AbstractModel)));

    internal static bool NeedsDrawFallback(CardModel card)
    {
        if (!ModelsDraw(card) || card.Owner.Creature.Powers.Any(p => p.GetType().Name == "NoDrawPower")) return false;
        var state = card.Owner.PlayerCombatState!;
        return !CanExpandDraw(card.Owner) || state.DrawPile.Cards.Count == 0 && state.DiscardPile.Cards.Count > 0
            || state.DrawPile.Cards.Take(DrawWindow).Any(c => !CanEnterFromDraw(c));
    }

    internal sealed record CardSpec(int Owner, bool Skill, bool Exhaust, bool Power, bool Trance, bool Corruption);
    internal sealed record Snapshot(CardSpec[] Cards, int[][] DrawOrder, State Root);
    internal sealed class State
    {
        internal ulong Available;
        internal ulong Discarded;
        internal ulong Exhausted;
        internal ulong PowersPlayed;
        internal required int[] HandCount;
        internal required int[] DrawCursor;
        internal required bool[] NoDraw;
        internal required bool[] Corruption;
        internal required bool[] Boundary;
        internal State Fork() => new()
        {
            Available = Available, Discarded = Discarded, Exhausted = Exhausted, PowersPlayed = PowersPlayed,
            HandCount = (int[])HandCount.Clone(), DrawCursor = (int[])DrawCursor.Clone(),
            NoDraw = (bool[])NoDraw.Clone(), Corruption = (bool[])Corruption.Clone(), Boundary = (bool[])Boundary.Clone(),
        };
    }

    internal static Snapshot Capture(IReadOnlyList<Player> players, IReadOnlyList<CardModel> cards)
    {
        var indexes = cards.Select((card, index) => (card, index)).ToDictionary(x => x.card, x => x.index);
        var root = new State
        {
            HandCount = players.Select(p => p.PlayerCombatState!.Hand.Cards.Count).ToArray(),
            DrawCursor = new int[players.Count], Boundary = new bool[players.Count],
            NoDraw = players.Select(p => p.Creature.Powers.Any(power => power.GetType().Name == "NoDrawPower")).ToArray(),
            Corruption = players.Select(p => p.Creature.Powers.Any(power => power.GetType().Name == "CorruptionPower")).ToArray(),
        };
        var specs = new CardSpec[cards.Count];
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i]; var owner = players.ToList().IndexOf(card.Owner);
            specs[i] = new(owner, card.Type == CardType.Skill, card.Keywords.Contains(CardKeyword.Exhaust),
                card.Type == CardType.Power, card.GetType().Name == "BattleTrance", card.GetType().Name == "Corruption");
            if (card.Owner.PlayerCombatState!.Hand.Cards.Contains(card)) root.Available |= 1UL << i;
        }
        var draw = players.Select(p => !CanExpandDraw(p) ? Array.Empty<int>() : p.PlayerCombatState!.DrawPile.Cards.Take(DrawWindow)
            .Select(card => CanEnterFromDraw(card) && indexes.TryGetValue(card, out var index) ? index : -1).ToArray()).ToArray();
        return new(specs, draw, root);
    }

    internal static int Cost(State state, CardSpec card, int normalCost) => state.Corruption[card.Owner] && card.Skill ? 0 : normalCost;
    internal static void BeginPlay(State state, int index, CardSpec card)
    {
        state.Available &= ~(1UL << index);
        state.HandCount[card.Owner]--;
        if (card.Power) state.PowersPlayed |= 1UL << index;
        else if (card.Exhaust || state.Corruption[card.Owner] && card.Skill) state.Exhausted |= 1UL << index;
        else state.Discarded |= 1UL << index;
    }
    internal static int Draw(State state, Snapshot snapshot, int player, int requested)
    {
        if (state.NoDraw[player] || state.Boundary[player]) return 0;
        var count = 0;
        while (count < requested && state.HandCount[player] < CardPile.MaxCardsInHand)
        {
            if (state.DrawCursor[player] >= snapshot.DrawOrder[player].Length)
            { state.Boundary[player] = true; break; }
            var card = snapshot.DrawOrder[player][state.DrawCursor[player]];
            if (card < 0) { state.Boundary[player] = true; break; }
            state.DrawCursor[player]++; state.HandCount[player]++; count++;
            state.Available |= 1UL << card;
        }
        return count;
    }
    internal static bool CanDrawAfterPlay(State state, Snapshot snapshot, int player)
        => !state.NoDraw[player] && !state.Boundary[player] && state.HandCount[player] <= CardPile.MaxCardsInHand
            && state.DrawCursor[player] < snapshot.DrawOrder[player].Length
            && snapshot.DrawOrder[player][state.DrawCursor[player]] >= 0;
}

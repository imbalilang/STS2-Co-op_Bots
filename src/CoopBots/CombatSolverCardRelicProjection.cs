// Adapted from CombatSolver 0.33.9 (Torch/contributors; Random Foreseer-derived
// portions hotwords123). See THIRD_PARTY_NOTICES.md and outputs/port-preview20.md.
// The user confirmed permission for this port on 2026-09-12.
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CoopBots;

internal static class CombatSolverCardRelicProjection
{
    internal enum TriggerKind { Strength, Dexterity, Block, Energy }
    internal sealed record RelicTrigger(int Owner, TriggerKind Kind, int Period, int Amount, int Initial);
    internal sealed record Snapshot(RelicTrigger[] Relics, State Root);
    internal sealed class State
    {
        internal required int[] Counters;
        internal required int[] FeelNoPain;
        internal required int[] DarkEmbrace;
        internal bool ImmutableEmpty;
        internal State Fork() => ImmutableEmpty ? this : new() { Counters = (int[])Counters.Clone(), FeelNoPain = (int[])FeelNoPain.Clone(), DarkEmbrace = (int[])DarkEmbrace.Clone() };
        internal bool Same(State other) => Counters.AsSpan().SequenceEqual(other.Counters)
            && FeelNoPain.AsSpan().SequenceEqual(other.FeelNoPain) && DarkEmbrace.AsSpan().SequenceEqual(other.DarkEmbrace);
        internal void AddHash(ref HashCode hash)
        { foreach (var n in Counters) hash.Add(n); foreach (var n in FeelNoPain) hash.Add(n); foreach (var n in DarkEmbrace) hash.Add(n); }
    }

    // Read each relic's own counter, rather than assuming that all relics have
    // observed the same number of cards or that a mid-turn root starts at zero.
    internal static Snapshot Capture(IReadOnlyList<Player> party)
    {
        var relics = new List<RelicTrigger>();
        for (var owner = 0; owner < party.Count; owner++)
        foreach (var relic in party[owner].Relics)
        {
            var kind = relic switch { Shuriken => TriggerKind.Strength, Kunai => TriggerKind.Dexterity,
                OrnamentalFan => TriggerKind.Block, Nunchaku => TriggerKind.Energy, _ => (TriggerKind?)null };
            if (kind is null) continue;
            var field = relic.GetType().GetField(relic is Nunchaku ? "_attacksPlayed" : "_attacksPlayedThisTurn",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(relic.GetType().FullName, "attack counter");
            var amount = kind switch { TriggerKind.Strength => relic.DynamicVars.Strength.IntValue,
                TriggerKind.Dexterity => relic.DynamicVars.Dexterity.IntValue,
                TriggerKind.Block => relic.DynamicVars.Block.IntValue, _ => relic.DynamicVars.Energy.IntValue };
            relics.Add(new(owner, kind.Value, relic.DynamicVars.Cards.IntValue, amount, (int)field.GetValue(relic)!));
        }
        var root = new State {
            Counters = relics.Select(r => r.Initial).ToArray(),
            FeelNoPain = party.Select(p => p.Creature.Powers.OfType<FeelNoPainPower>().Sum(power => power.Amount)).ToArray(),
            DarkEmbrace = party.Select(p => p.Creature.Powers.OfType<DarkEmbracePower>().Sum(power => power.Amount)).ToArray(),
        };
        root.ImmutableEmpty = relics.Count == 0 && root.FeelNoPain.All(n => n == 0) && root.DarkEmbrace.All(n => n == 0)
            && !party.Any(p => p.PlayerCombatState!.Hand.Cards.Concat(p.PlayerCombatState.DrawPile.Cards.Take(CombatResourceProjection.DrawWindow))
                .Any(card => card is FeelNoPain or DarkEmbrace));
        return new(relics.ToArray(), root);
    }

    internal static void PlayPower(State state, CardModel card, int owner)
    {
        if (state.ImmutableEmpty && card is FeelNoPain or DarkEmbrace)
            throw new InvalidOperationException("Imported power absent from captured branch state.");
        if (card is FeelNoPain) state.FeelNoPain[owner] += card.DynamicVars["Power"].IntValue;
        if (card is DarkEmbrace) state.DarkEmbrace[owner]++;
    }

    // CombatSolver AfterCardPlayedMirrors.IncrementCounter: increment once per
    // matching card PLAY, not per damage hit; replay is a separate play.
    internal static bool IncrementCounter(State state, Snapshot snapshot, int relicIndex, int owner, CardType type)
    {
        var relic = snapshot.Relics[relicIndex];
        if (owner != relic.Owner || type != CardType.Attack) return false;
        state.Counters[relicIndex]++;
        return state.Counters[relicIndex] % relic.Period == 0;
    }
}

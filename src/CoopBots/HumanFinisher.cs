using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace CoopBots;

// At most two original hand cards from ONE human, with a gross two-energy budget.
// Enumerating yields after each simulation; the runtime may spread it over frames.
internal static class HumanFinisher
{
    internal const int MaxCards = 12, MaxBranches = 64;
    internal sealed record Plan(Player Human, Creature Enemy, IReadOnlyList<CardModel> Cards, int Energy);

    // A single-card solution kept for the two-card pass. This is deliberately a
    // reference type: an iterator hoists its locals into a compiler-generated
    // state machine, and a *value* type that contains a kernel type (such as a
    // ValueTuple<CardModel, KernelSession, int>) forces the runtime to resolve
    // CoopBots.Kernel while the mod loader is still enumerating our types — long
    // before Initialize() loads the kernel, which makes the whole mod fail with
    // ReflectionTypeLoadException. A reference type carries no such layout
    // requirement.
    private sealed record FirstStep(CardModel Card, KernelSession State, int Cost);

    internal static bool Threat(Creature enemy) => enemy.IsAlive
        && enemy.Monster?.NextMove.Intents.OfType<AttackIntent>().Any() == true;

    internal static IEnumerable<Plan?> Search(KernelSession root, CombatState live)
    {
        var expanded = 0;
        foreach (var human in live.Players.Where(p => !BotRegistry.IsBot(p.NetId)
                     && p.PlayerCombatState?.Phase == PlayerTurnPhase.Play && root.CanAct(p)))
        {
            var original = root.Hand(human).ToHashSet();
            var cards = original.Where(c => c.Type is CardType.Attack or CardType.Skill)
                .Where(c => root.FinisherEnergyCost(c) <= 2)
                .OrderBy(c => root.FinisherEnergyCost(c)).ThenBy(c => c.Id.Entry, StringComparer.Ordinal)
                .Take(MaxCards).ToArray();
            foreach (var enemy in live.HittableEnemies.Where(Threat).OrderBy(e => e.CurrentHp + e.Block))
            {
                // Check all single-card solutions before any two-card sequence.
                var firsts = new List<FirstStep>();
                foreach (var card in cards)
                {
                    if (expanded >= MaxBranches) yield break;
                    if (!root.CanPlay(card) || !TryTarget(root, card, enemy, out var target)) continue;
                    expanded++;
                    var cost = root.FinisherEnergyCost(card);
                    var child = root.Fork();
                    if (!SafePlay(root, child, card, target, original, live)) { yield return null; continue; }
                    if (child.Hp(enemy) <= 0)
                    { yield return new(human, enemy, new[] { card }, cost); yield break; }
                    firsts.Add(new(card, child, cost));
                    yield return null;
                }
                foreach (var first in firsts)
                foreach (var second in cards)
                {
                    if (expanded >= MaxBranches) yield break;
                    if (ReferenceEquals(first.Card, second) || !first.State.CanPlay(second)) continue;
                    var cost = first.State.FinisherEnergyCost(second);
                    if (cost > 2 - first.Cost || !TryTarget(first.State, second, enemy, out var target)) continue;
                    expanded++;
                    var child = first.State.Fork();
                    if (SafePlay(first.State, child, second, target, original, live) && child.Hp(enemy) <= 0)
                    { yield return new(human, enemy, new[] { first.Card, second }, first.Cost + cost); yield break; }
                    yield return null;
                }
            }
        }
    }

    private static bool TryTarget(KernelSession state, CardModel card, Creature enemy, out Creature? target)
    {
        target = card.TargetType switch
        {
            TargetType.AnyEnemy => enemy,
            TargetType.Self => card.Owner.Creature,
            TargetType.AllEnemies or TargetType.None => null,
            _ => enemy,
        };
        return card.TargetType is TargetType.AnyEnemy or TargetType.Self or TargetType.AllEnemies or TargetType.None
            && state.Targets(card).Contains(target);
    }

    private static bool SafePlay(KernelSession parent, KernelSession child, CardModel card, Creature? target,
        HashSet<CardModel> original, CombatState live)
    {
        var rng = parent.FinisherRandomStamp();
        return child.Play(card, target, out _) && !child.LastActionHadEnvironmentalRisk
            && child.RoundsAdvanced == parent.RoundsAdvanced && !child.EnemyPhaseCompleted
            && child.FinisherRandomStamp() == rng
            && child.Hand(card.Owner).All(original.Contains)
            // Never recommend paying HP or taking thorns damage to abandon defence.
            && live.Players.All(p => child.Hp(p.Creature) >= parent.Hp(p.Creature));
    }
}

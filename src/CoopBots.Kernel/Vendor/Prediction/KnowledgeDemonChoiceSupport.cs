using CoopBots.Kernel.Vendor.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel.Vendor;

internal sealed record KnowledgeDemonChoiceRequest(
    Creature Source,
    int Counter,
    string SourceId,
    IReadOnlyList<string> OptionIds);

internal static class KnowledgeDemonChoiceSupport
{
    private static readonly string[][] OptionsByCounter =
    [
        [CanonicalModels.Card<Disintegration>().Id.Entry, CanonicalModels.Card<MindRot>().Id.Entry],
        [CanonicalModels.Card<Disintegration>().Id.Entry, CanonicalModels.Card<Sloth>().Id.Entry],
        [CanonicalModels.Card<Disintegration>().Id.Entry, CanonicalModels.Card<WasteAway>().Id.Entry],
    ];

    public static bool Resolve(
        SimulatedCombatState combat,
        Creature source,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        bool autoResolve = false)
    {
        int counter = combat.GetKnowledgeDemonCurseCounter(source);
        if ((uint)counter >= (uint)OptionsByCounter.Length)
            throw new InvalidOperationException($"知识恶魔诅咒计数超出范围：{counter}。");

        string sourceId = SourceId(source, counter);
        IReadOnlyList<string> optionIds = OptionsByCounter[counter];
        PlanCardChoice? choice = plannedChoices?.FirstOrDefault(candidate =>
            candidate.Effect == PlanChoiceEffect.ApplyKnowledgeCurse
            && string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal));
        if (choice == null)
        {
            if (!autoResolve)
            {
                combat.SetPendingKnowledgeDemonChoice(new KnowledgeDemonChoiceRequest(
                    source,
                    counter,
                    sourceId,
                    optionIds));
                return false;
            }
            // Roll-out prediction, not a committed plan: every target still has to
            // choose, and the tournament has no vector for those choices. Prefer the
            // non-Disintegration curse (Mind Rot / Sloth / Waste Away) because
            // Disintegration is direct unblockable damage and scales with the counter.
            var disintegrationId = CanonicalModels.Card<Disintegration>().Id.Entry;
            var defaultId = optionIds.FirstOrDefault(id =>
                !string.Equals(id, disintegrationId, StringComparison.Ordinal)) ?? optionIds[0];
            choice = new PlanCardChoice(
                PlanChoiceEffect.ApplyKnowledgeCurse,
                PileType.None,
                [new PlanCardToken(defaultId, 0, string.Empty, 0, 0, defaultId)],
                sourceId,
                Timing: PlanChoiceTiming.EnemyTurn);
        }
        if (choice.SourcePile != PileType.None || choice.Cards.Count != 1)
            throw new InvalidOperationException($"知识恶魔计划选牌格式无效：{sourceId}。");

        string selectedId = choice.Cards[0].CardId;
        if (!optionIds.Contains(selectedId, StringComparer.Ordinal))
            throw new InvalidOperationException($"知识恶魔当前不能选择 {selectedId}。");

        if (selectedId == CanonicalModels.Card<Disintegration>().Id.Entry)
            combat.Apply<DisintegrationPower>(player, 6 + counter, player);
        else if (selectedId == CanonicalModels.Card<MindRot>().Id.Entry)
            combat.Apply<MindRotPower>(player, 1, player);
        else if (selectedId == CanonicalModels.Card<Sloth>().Id.Entry)
            combat.Apply<SlothPower>(player, 3, player);
        else if (selectedId == CanonicalModels.Card<WasteAway>().Id.Entry)
            combat.Apply<WasteAwayPower>(player, 1, player);
        else
            throw new InvalidOperationException($"知识恶魔诅咒 {selectedId} 没有模拟效果。");

        // The move-level caller advances the counter exactly once after every target
        // has resolved. Doing it here advanced once per party member in 4-player play.
        combat.ClearPendingKnowledgeDemonChoice();
        return true;
    }

    public static IReadOnlyList<PlanCardChoice> BuildChoices(
        KnowledgeDemonChoiceRequest request,
        SolverDisplayNames displayNames)
    {
        List<PlanCardChoice> choices = new(request.OptionIds.Count);
        for (int optionIndex = 0; optionIndex < request.OptionIds.Count; optionIndex++)
        {
            string cardId = request.OptionIds[optionIndex];
            choices.Add(new PlanCardChoice(
                PlanChoiceEffect.ApplyKnowledgeCurse,
                PileType.None,
                [new PlanCardToken(cardId, 0, string.Empty, 0, 0, displayNames.Card(cardId))],
                request.SourceId,
                Timing: PlanChoiceTiming.EnemyTurn));
        }
        return choices;
    }

    private static string SourceId(Creature source, int counter)
        => $"KNOWLEDGE_DEMON:{source.CombatId ?? uint.MaxValue}:{counter}";
}

"""Preserve audited party enemy-choice semantics after the upstream/P1 adapters.

Includes the previous multiplayer continuation/counter fixes absent on reimport,
plus the shared live policy and branch HP. Changed upstream input fails loudly.
"""
from pathlib import Path
import hashlib
import json

_patches = json.loads(Path(__file__).with_name('kernel-decision-consistency-adaptations.json').read_text(encoding='utf-8'))


def apply_decision_consistency_adaptations(relative, text):
    patch = _patches.get(relative)
    if patch is None:
        return text
    if hashlib.sha256(text.encode()).hexdigest() != patch['base_sha256']:
        raise RuntimeError(f'Enemy choice adapter base changed: {relative}; review before importing.')
    for change in reversed(patch['changes']):
        start, end = change['start'], change['end']
        if text[start:end] != change['old']:
            raise RuntimeError(f'Enemy choice adapter context changed: {relative}')
        text = text[:start] + change['new'] + text[end:]
    if relative == 'Prediction/KnowledgeDemonChoiceSupport.cs':
        text = _fixed_disintegration_policy(text)
    return text


def _fixed_disintegration_policy(text):
    """Owner 2026-10-07: bot policies always choose Disintegration; native effects stay intact."""
    for old, new in _fixed_policy_changes:
        if new in text:
            continue
        if text.count(old) != 1:
            raise RuntimeError('Fixed Disintegration adapter context changed; review before importing.')
        text = text.replace(old, new, 1)
    return text

_fixed_policy_changes = [('            // Shared with the live screen; native Disintegration is blockable.\n            var defaultId = KernelKnowledgeChoice.Choose(player.Player!, optionIds, 6 + counter,\n                currentHp ?? player.CurrentHp, combat.GetAmount<DisintegrationPower>(player));\n', '            var defaultId = KernelKnowledgeChoice.Choose(optionIds);\n'), ('    public static IReadOnlyList<PlanCardChoice> BuildChoices(\n        KnowledgeDemonChoiceRequest request,\n        SolverDisplayNames displayNames)\n    {\n        List<PlanCardChoice> choices = new(request.OptionIds.Count);\n        for (int optionIndex = 0; optionIndex < request.OptionIds.Count; optionIndex++)\n        {\n            string cardId = request.OptionIds[optionIndex];\n            choices.Add(new PlanCardChoice(\n                PlanChoiceEffect.ApplyKnowledgeCurse,\n                PileType.None,\n                [new PlanCardToken(cardId, 0, string.Empty, 0, 0, displayNames.Card(cardId))],\n                request.SourceId,\n                Timing: PlanChoiceTiming.EnemyTurn));\n        }\n        return choices;\n    }\n\n', '    public static IReadOnlyList<PlanCardChoice> BuildChoices(\n        KnowledgeDemonChoiceRequest request,\n        SolverDisplayNames displayNames)\n    {\n        // Search must not publish a suffix that live policy cannot execute.\n        string cardId = KernelKnowledgeChoice.Choose(request.OptionIds);\n        return [new PlanCardChoice(\n                PlanChoiceEffect.ApplyKnowledgeCurse,\n                PileType.None,\n                [new PlanCardToken(cardId, 0, string.Empty, 0, 0, displayNames.Card(cardId))],\n                request.SourceId,\n                Timing: PlanChoiceTiming.EnemyTurn)];\n    }\n\n')]

"""Retain BeforeAttack paired listeners through actor death (2026-10-25).

Native attack ordering is unchanged. Only Vigor/Gigantification transaction
cleanup retains departed start listeners. Exact predecessor, slices and target
prevent a later import from silently dropping or misapplying this repair.
"""
import hashlib
import json
from pathlib import Path


def apply_attack_pair_adaptations(relative, text):
    patches = json.loads(Path(__file__).with_name('kernel-attack-pair-adaptations-41.json').read_text(encoding='utf-8'))
    patch = patches.get(relative)
    if patch is None:
        return text
    if hashlib.sha256(text.encode()).hexdigest() != patch['base_sha256']:
        raise RuntimeError('Attack pair predecessor changed: ' + relative)
    for change in reversed(patch['changes']):
        start, end = change['start'], change['end']
        if text[start:end] != change['old']:
            raise RuntimeError('Attack pair seam changed: ' + relative)
        text = text[:start] + change['new'] + text[end:]
    if hashlib.sha256(text.encode()).hexdigest() != patch['target_sha256']:
        raise RuntimeError('Attack pair target differs: ' + relative)
    return text

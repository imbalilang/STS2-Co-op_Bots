"""Preserve native temporary-stat lifecycle and manual ally legality on reimport.

Only named audited seams are changed. The post-adapter source hash and exact
context must match before the patch is applied; an upstream change fails closed.
"""
import hashlib
import json
from pathlib import Path

_patches = json.loads(Path(__file__).with_name(
    'kernel-temporary-stat-adaptations-41.json').read_text(encoding='utf-8'))

# R2 isolated byte-identical adapter mirror (production unchanged): disable
# each guard, observe exit 1, restore exact bytes, observe exit 0.
# AssertionError: changed temporary-stat upstream must fail closed
# AssertionError: changed temporary-stat adaptation seam must fail closed
# Raw commands/logs/SHA256: temporary-stats/combat/temporary-combat-import-r2-results.json


def apply_temporary_stat_adaptations(relative, text):
    patch = _patches.get(relative)
    if patch is None:
        return text
    if hashlib.sha256(text.encode('utf-8')).hexdigest() != patch['base_sha256']:
        raise RuntimeError(f'Temporary stat adapter source changed: {relative}; review native lifecycle before importing.')
    for change in patch['changes']:
        if text.count(change['old']) != 1:
            raise RuntimeError(f'Temporary stat adapter seam changed: {relative}; review before importing.')
        text = text.replace(change['old'], change['new'], 1)
    return text

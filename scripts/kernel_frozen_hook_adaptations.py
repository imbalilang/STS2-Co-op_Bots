"""Persist frozen turn/stateful draw and energy hooks without rereading live owners."""
from pathlib import Path
import hashlib
import json


def apply_frozen_hook_adaptations(relative, text):
    if relative != 'Prediction/PersistentPowerSupport.cs':
        return text
    patch = json.loads((Path(__file__).resolve().parent /
                        'kernel-frozen-hook-adaptations-41.json').read_text(encoding='utf-8'))
    if hashlib.sha256(text.encode('utf-8')).hexdigest() != patch['base_sha256']:
        raise RuntimeError('Frozen hook source hash changed: PersistentPowerSupport; review native listener order and captured relic semantics.')
    for change in patch['changes']:
        if text.count(change['old']) != 1:
            raise RuntimeError('Frozen draw/energy hook seam changed; review before importing.')
        text = text.replace(change['old'], change['new'])
    return text

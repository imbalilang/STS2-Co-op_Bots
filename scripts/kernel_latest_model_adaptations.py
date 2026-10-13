"""Preserve latest native multiplayer Power identity/application repairs.

Runs after all existing 0.41 adaptations. Whole post-adapter source hashes and
unique exact seams fail closed when upstream or a predecessor changes.
"""
import hashlib
import json
from pathlib import Path

_patches = json.loads(Path(__file__).with_name(
    'kernel-latest-model-adaptations-41.json').read_text(encoding='utf-8'))


def apply_latest_model_adaptations(relative, text):
    patch = _patches.get(relative)
    if patch is None:
        return text
    if hashlib.sha256(text.encode('utf-8')).hexdigest() != patch['base_sha256']:
        raise RuntimeError(f'Latest model adapter source changed: {relative}; review native multiplayer rules before importing.')
    for change in patch['changes']:
        if text.count(change['old']) != 1:
            raise RuntimeError(f'Latest model adapter seam changed: {relative}; review before importing.')
        text = text.replace(change['old'], change['new'], 1)
    return text

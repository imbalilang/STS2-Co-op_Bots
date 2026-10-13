"""PRIVATE static proposal: retain every live classification, index CLR types only.

No regeneration/build/runtime/performance/R2 result is claimed by this module.
The exact baseline and target hashes deliberately refuse changed predecessors.
"""
import hashlib
import json
from pathlib import Path


def apply_risk_prefix_coverage_adaptations(relative, text):
    path = Path(__file__).with_name('kernel-risk-prefix-coverage-adaptations.json')
    if not path.exists():
        path = Path(__file__).with_name('adaptations.json')
    matches = [p for p in json.loads(path.read_text(encoding='utf-8'))['files']
               if p['path'] == relative]
    if not matches:
        return text
    patch, = matches
    if hashlib.sha256(text.encode()).hexdigest().upper() != patch['base_normalized_sha256']:
        raise RuntimeError('Risk-prefix coverage predecessor changed: ' + relative)
    for change in reversed(patch['changes']):
        start, end = change['start'], change['end']
        if text[start:end] != change['old']:
            raise RuntimeError('Risk-prefix coverage seam changed: ' + relative)
        text = text[:start] + change['new'] + text[end:]
    if hashlib.sha256(text.encode()).hexdigest().upper() != patch['candidate_normalized_sha256']:
        raise RuntimeError('Risk-prefix coverage target differs: ' + relative)
    return text

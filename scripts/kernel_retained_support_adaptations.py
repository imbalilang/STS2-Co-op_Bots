"""Reapply unchanged host support that earlier focused import proofs omitted.

The 2026-10-08 whole imported-source audit found allocation instrumentation,
gold-cheat parity, card preview support and named boundary diagnostics missing
from a clean import. Retain the captured pre-task implementations verbatim.
Exact whole-input hashes plus exact spans fail closed; this changes importing,
not production behavior.

R2 2026-10-08: eight isolated predecessor mutations exited 1; the original
inputs each exited 0 and reproduced the production text exactly. Raw commands
and logs: work/latest-run-repair-20261008/retained-import-r2.json.
RuntimeError: Retained host support predecessor changed: Engine/Common/PredictedCard.cs; review before importing.
"""
import hashlib
import json
from pathlib import Path


def apply_retained_support_adaptations(relative, text):
    patches = json.loads(Path(__file__).with_name('kernel-retained-support-adaptations-41.json').read_text(encoding='utf-8'))
    patch = patches.get(relative)
    if patch is None: return text
    if hashlib.sha256(text.encode('utf-8')).hexdigest() != patch['base_sha256']:
        raise RuntimeError('Retained host support predecessor changed: '+relative+'; review before importing.')
    for change in reversed(patch['changes']):
        start, end = change['start'], change['end']
        if text[start:end] != change['old']:
            raise RuntimeError('Retained host support seam changed: '+relative)
        text = text[:start]+change['new']+text[end:]
    if hashlib.sha256(text.encode('utf-8')).hexdigest() != patch['target_sha256']:
        raise RuntimeError('Retained host support target differs: '+relative)
    return text

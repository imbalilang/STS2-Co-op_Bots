"""Freeze native HistoryCourse, VitalSpark and played-skill root facts.

Reviewed predecessor, unique seams and exact output hashes fail closed. This
does not rebase relative solver scores or remove future-driving state.


Actual pure-Python R2: eight new predecessor/seam/target guard withdrawals
yielded baseline0 / named AssertionError RED1 / exact restore GREEN0. Prior
Orichalcum guards remain unchanged. Raw commands/source receipts:
state-diagnostics/p0-route-ledger-v1/adapter-proof/proof.json.
AssertionError: native capture adapter must fail at target; actual accepted
"""
import hashlib,json
from pathlib import Path

def apply_history_capture_adaptations(relative,text):
    patches=json.loads(Path(__file__).with_name('kernel-history-capture-adaptations-41.json').read_text(encoding='utf-8'))
    patch=patches.get(relative)
    if patch is None:return text
    if hashlib.sha256(text.encode()).hexdigest()!=patch['base_sha256']:
        raise RuntimeError('Native history capture predecessor changed: '+relative)
    for change in patch['changes']:
        if text.count(change['old'])!=1:
            raise RuntimeError('Native history capture seam changed: '+relative)
        text=text.replace(change['old'],change['new'],1)
    if hashlib.sha256(text.encode()).hexdigest()!=patch['target_sha256']:
        raise RuntimeError('Native history capture target differs: '+relative)
    return text

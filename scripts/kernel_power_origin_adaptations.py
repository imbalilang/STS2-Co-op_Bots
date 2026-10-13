"""Audited native move/roster intent-cache updates. Exact predecessor, seam,
and output guards; only named native writes are adapted. Existing COW
retirement, hidden payloads, scores and qualification stay intact."""
# Actual six predecessor/seam/target withdrawals: 0 / named RED1 /
# exact restored GREEN0; p0-power-origin-v1/adapter-proof/proof.json.
# AssertionError: native power-origin adapter must reject at target; actual accepted
import hashlib,json
from pathlib import Path

def apply_power_origin_adaptations(relative,text):
    patches=json.loads(Path(__file__).with_name('kernel-power-origin-adaptations-41.json').read_text(encoding='utf-8'))
    patch=patches.get(relative)
    if patch is None:return text
    if hashlib.sha256(text.encode()).hexdigest()!=patch['base_sha256']:
        raise RuntimeError('Native power-origin predecessor changed: '+relative)
    for change in patch['changes']:
        if text.count(change['old'])!=1:
            raise RuntimeError('Native power-origin seam changed: '+relative)
        text=text.replace(change['old'],change['new'],1)
    if hashlib.sha256(text.encode()).hexdigest()!=patch['target_sha256']:
        raise RuntimeError('Native power-origin target differs: '+relative)
    return text

"""Capture Orichalcum's native flag instead of allocating a lazy false state.

Exact reviewed predecessor, unique literal seams and final output hashes are
checked before importing. The real ordered hook list and flag remain semantic.

Actual Python R2: 12 predecessor/seam/target guard withdrawals plus the added
fingerprint seam each yielded baseline0 / AssertionError RED1 / exact GREEN0.
Raw commands/exits and private bytes: state-diagnostics/p0-native-orichalcum-v1/
adapter-proof and adapter-addition-proof.
AssertionError: adapter must fail at reviewed seam guard; actual Orichalcum capture target differs: Prediction/RelicPredictionStateSupport.cs
"""
import hashlib
import json
from pathlib import Path

def apply_orichalcum_capture_adaptations(relative, text):
    patches = json.loads(Path(__file__).with_name(
        'kernel-orichalcum-capture-adaptations-41.json').read_text(encoding='utf-8'))
    patch = patches.get(relative)
    if patch is None: return text
    if hashlib.sha256(text.encode()).hexdigest() != patch['base_sha256']:
        raise RuntimeError('Orichalcum capture predecessor changed: '+relative)
    for change in patch['changes']:
        if text.count(change['old']) != 1:
            raise RuntimeError('Orichalcum capture seam changed: '+relative)
        text = text.replace(change['old'], change['new'], 1)
    if hashlib.sha256(text.encode()).hexdigest() != patch['target_sha256']:
        raise RuntimeError('Orichalcum capture target differs: '+relative)
    return text

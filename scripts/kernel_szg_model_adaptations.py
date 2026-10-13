"""Reproduce the SZG native multi-seat RNG and party-start lifecycle repairs.

Only exact reviewed predecessors may be adapted. Generated support is frozen
in the same manifest as the seams, so importing never loses host continuations.

R2 isolated source guards: work/szg-repair-20261008T124624/import-final-proof-v2.json.
Disabling predecessor or seam checks yields RED1:
  SZG importer must fail at the reviewed guard: SZG native model predecessor changed
  SZG importer must fail at the reviewed guard: SZG native model seam changed
Disabling the support target check yields RED1:
  SZG importer must reject SZG party support target differs
Restoring the source yields GREEN0 for all nine probes; no shared mutation.
"""
import hashlib
import json
from pathlib import Path

def _patches():
    return json.loads(Path(__file__).with_name('kernel-szg-model-adaptations-41.json').read_text(encoding='utf-8'))

def apply_szg_model_adaptations(relative, text):
    patch = _patches()['patches'].get(relative)
    if patch is None: return text
    if hashlib.sha256(text.encode()).hexdigest() != patch['base_sha256']:
        raise RuntimeError('SZG native model predecessor changed: '+relative)
    for change in reversed(patch['changes']):
        start,end=change['start'],change['end']
        if text[start:end] != change['old']:
            raise RuntimeError('SZG native model seam changed: '+relative)
        text=text[:start]+change['new']+text[end:]
    if hashlib.sha256(text.encode()).hexdigest() != patch['target_sha256']:
        raise RuntimeError('SZG native model target differs: '+relative)
    return text

def write_szg_party_support(dest):
    manifest=[]
    for relative,patch in _patches()['support'].items():
        text=patch['text']
        if hashlib.sha256(text.encode()).hexdigest() != patch['target_sha256']:
            raise RuntimeError('SZG party support target differs: '+relative)
        target=dest/relative;target.parent.mkdir(parents=True,exist_ok=True)
        target.write_text(text,encoding='utf-8')
        manifest.append({'path':relative,'upstream':'CoopBots native multi-seat setup support',
            'upstream_sha256':patch['target_sha256']})
    return manifest

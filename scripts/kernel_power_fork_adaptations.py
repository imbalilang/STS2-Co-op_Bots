"""Replay the complete COW ownership adaptation, including its support file."""
from pathlib import Path
import hashlib
import json

_scripts = Path(__file__).resolve().parent


def apply_power_fork_adaptations(relative, text):
    patches = json.loads((_scripts / 'kernel-power-fork-cow-adaptations-41.json').read_text(encoding='utf-8'))
    if relative not in patches:
        return text
    patch = patches[relative]
    actual = hashlib.sha256(text.encode('utf-8')).hexdigest()
    if actual != patch['base_sha256']:
        raise RuntimeError(f'COW adapted source hash changed: {relative}; review fork ownership before importing.')
    for change in patch['changes']:
        if text.count(change['old']) != 1:
            raise RuntimeError(f'COW ownership seam changed: {relative}.')
        text = text.replace(change['old'], change['new'])
    return text


def write_power_fork_support(dest):
    source = _scripts / 'templates/kernel-power-fork-cow-support.cs'
    text = source.read_text(encoding='utf-8')
    target = Path(dest) / 'Search/SimulatedCombatState.PowerForkCow.cs'
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text, encoding='utf-8')
    return [{'path': 'Search/SimulatedCombatState.PowerForkCow.cs',
             'authoritative_template': str(source.relative_to(_scripts.parent)),
             'template_sha256': hashlib.sha256(text.encode('utf-8')).hexdigest()}]

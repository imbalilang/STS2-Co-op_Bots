"""Re-anchor the P1 multiplayer adaptation table onto a new upstream snapshot.

The table is positional: each change carries start/end offsets plus the exact old
text expected at those offsets. Upstream edits move the offsets and change the
base hash, so the importer refuses to run. This tool re-derives the offsets from
the 0.33.9 table (`kernel-p1-adaptations.json`) against the new snapshot, which
keeps ONE authored copy of every multiplayer change instead of a second table
that can silently drift from it.

Two things make the re-anchoring trustworthy:

* Changes are matched in ASCENDING order and each search starts where the
  previous change's original text ended. Order, not the stale offsets, is the
  real anchor, and a short fragment like "        };\n" cannot match a later
  occurrence by accident.
* Offsets are written in ORIGINAL-file coordinates. The importer applies changes
  in reverse offset order to the unpatched text, so every offset must stay valid
  on that text; computing them against a mutated copy silently moves every later
  change (that bug shipped once and put a local variable declaration in the
  middle of a parameter list).

An insertion (old == "") has no text to search for, so it is anchored on the
line immediately before its original position.

Usage:
    python scripts/reanchor-p1-adaptations-41.py [--write]

Without --write it only reports.
"""
from pathlib import Path
import argparse, hashlib, json

root = Path(__file__).resolve().parents[1]
source = root / 'work/combatsolver-sync-latest/src'
authored_path = root / 'scripts/kernel-p1-adaptations.json'
target_path = root / 'scripts/kernel-p1-adaptations-41.json'

# Changes that no longer describe anything on this snapshot, verified by reading
# the new upstream rather than by the anchor failing. Addressed BY INDEX because
# two call sites in PlayerTurnEndLifecycle.cs are byte-identical, so an
# old-text-keyed drop cannot tell them apart.
DROP = {
    'Search/SimulatedCombatState.cs': {
        0: '0.41 ApplyTargeted branches on PowerInstanceType itself and keeps '
           'instanced powers off the (owner,type) merge cache, which is what this '
           'insertion did by hand. Re-applying it would fight upstream.',
    },
    # 0.41 moved the JossPaper ethereal-count logic out of EndTurnPowerSupport and
    # into SimulatedCombatState.ReactiveRelics (where this same patch already
    # threads etherealByOwner), so TriggerRegular no longer reads the count at all
    # — the parameter is dead there and the CorePowerSupport leg that forwards it
    # has nothing to forward to. Both files stay upstream-clean.
    'Prediction/EndTurnPowerSupport.cs': {
        0: 'signature gains a dictionary the 0.41 body never reads',
        1: 'the local it declares is unused on 0.41',
        2: 'the use moved to ReactiveRelics.cs',
        3: 'the use moved to ReactiveRelics.cs',
    },
    'Prediction/CorePowerSupport.cs': {
        0: 'only existed to forward the dictionary into EndTurnPowerSupport',
        1: 'only existed to forward the dictionary into EndTurnPowerSupport',
    },
    'Prediction/PlayerTurnEndLifecycle.cs': {
        1: 'forwards the dictionary into CorePowerSupport, which no longer takes it',
    },
    # The switch expression it closed no longer exists after the retarget below.
    'Engine/InCombat/Simulation/CombatPredictionSimulator.EndTurn.cs': {
        2: 'closed the CombatManager switch expression that 0.41 replaced with '
           'State.CombatState.Players',
    },
}

# Changes the upstream edit invalidated, rewritten against the new text. Keyed by
# index and addressed by 0.41 text so the re-anchor still proves the anchor moved
# for a reason we read, not for a reason we guessed.
RETARGET = {
    'Engine/InCombat/Simulation/CombatPredictionSimulator.EndTurn.cs': {
        # 0.33.9 offered the "players taking an extra turn" override through the
        # CombatManager singleton; 0.41 reads the combat state directly. The
        # multiplayer patch still has to be able to name the ending party.
        1: ('        var playersEndingTurn = State.CombatState.Players;\n',
            '        var playersEndingTurn = participants ?? State.CombatState.Players;\n'),
    },
}


def rewrite(text):
    return text.replace('CombatSolver.', 'CoopBots.Kernel.Vendor.').replace(
        'namespace CombatSolver;', 'namespace CoopBots.Kernel.Vendor;')


def anchor_of(change, original, cursor):
    """Return (search_text, insert_after) for one change."""
    if change['old']:
        return change['old'], False
    # Insertion: use the line that precedes its original position as context.
    head = original[:change['start']]
    line_start = head.rfind('\n', 0, len(head) - 1) + 1
    return head[line_start:], True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--write', action='store_true')
    args = parser.parse_args()

    authored = json.loads(authored_path.read_text(encoding='utf-8'))
    table = {}
    for relative, patch in authored.items():
        original = rewrite((source / relative).read_text(encoding='utf-8-sig'))
        drops = DROP.get(relative, {})
        retargets = RETARGET.get(relative, {})
        cursor = 0
        moved = []
        failures = []
        for index, change in enumerate(patch['changes']):
            if index in retargets:
                change = {'old': retargets[index][0], 'new': retargets[index][1]}
            needle, insert_after = anchor_of(change, original, cursor)
            start = original.find(needle, cursor)
            if start < 0:
                # A dropped change may have no successor text at all. That is
                # still an expected outcome, but only for a dropped change, and
                # it must not leave the cursor behind: two call sites in
                # PlayerTurnEndLifecycle.cs are byte-identical, so a skipped
                # search makes the NEXT change patch the wrong one.
                if index not in drops:
                    failures.append(change['old'][:60] or '<insertion>')
                continue
            if insert_after:
                start += len(needle)
                end = start
            else:
                end = start + len(needle)
            cursor = end
            if index in drops:
                continue
            moved.append({'start': start, 'end': end,
                          'old': change['old'], 'new': change['new']})
        if failures:
            print(f'{relative}: FAILED {len(failures)}/{len(patch["changes"])}: {failures}')
            continue
        table[relative] = {
            'base_sha256': hashlib.sha256(original.encode()).hexdigest(),
            'changes': moved,
        }
        note = f' (dropped {len(patch["changes"]) - len(moved)})' if len(moved) != len(patch['changes']) else ''
        print(f'{relative}: re-anchored {len(moved)}/{len(patch["changes"])}{note}')
    for relative, rules in DROP.items():
        for index, why in rules.items():
            print(f'{relative}[{index}]: DROPPED — {why}')

    if args.write and len(table) == len(authored):
        target_path.write_text(json.dumps(table, ensure_ascii=False, indent=2), encoding='utf-8')
        print(f'wrote {target_path}')
    elif args.write:
        print('refusing to write: at least one file did not re-anchor cleanly')


if __name__ == '__main__':
    main()

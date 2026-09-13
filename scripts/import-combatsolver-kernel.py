"""Import authorized upstream source, excluding mod startup, UI and automation."""
from pathlib import Path
import hashlib, json, argparse
root=Path(__file__).resolve().parents[1]
source=root/'CombatSolver-0.33.9/src'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, default=root/'src/CoopBots.Kernel/Vendor')
parser.add_argument('--skip-patches', action='store_true')
args = parser.parse_args()
dest = args.output
patch_path = root/'scripts/kernel-p1-adaptations.json'
patches = {} if args.skip_patches or not patch_path.exists() else json.loads(patch_path.read_text(encoding='utf-8'))

def add_multiplayer_card_support(text):
    """Re-apply the CoopBots multiplayer card completion after namespace rewrite.

    Only adds cards whose team semantics are already carried by hooks present in
    this snapshot; upstream changes to any anchor abort the import for review.
    """
    anchors=[
        ('        [typeof(Salvo)] = [Owner<RetainHandPower>(_ => 1)],\n',
         '        // CoopBots multiplayer port: power hooks already exist in this snapshot\n'
         '        // (AfterCardPlayed/AfterBlockGained), only the OnPlay application was missing.\n'
         '        [typeof(Sneaky)] = [Owner<SneakyPower>("SneakyPower")],\n'
         '        [typeof(BeaconOfHope)] = [Owner<BeaconOfHopePower>(_ => 1)],\n',
         False),
        ('        typeof(Adrenaline), typeof(BigBang), typeof(BloodWall), typeof(Breakthrough), typeof(BrightestFlame), typeof(GatherLight),\n',
         '        // CoopBots multiplayer port: instant team/target energy, no power lifecycle needed.\n'
         '        typeof(EnergySurge), typeof(BelieveInYou),\n',
         False),
        ('            case Adrenaline:\n',
         '            case EnergySurge:\n'
         '                // Every living teammate, matching the native GetTeammatesOf loop.\n'
         '                foreach (Creature ally in combat.GetTeammatesOf(ownerCreature))\n'
         '                {\n'
         '                    if (!ally.IsPlayer || !ally.IsAlive || ally.Player is not { } allyPlayer)\n'
         '                        continue;\n'
         '                    simulator.GainEnergy(allyPlayer, card.DynamicVars.Energy.IntValue);\n'
         '                    if (simulator.HasPendingChoice)\n'
         '                        return true;\n'
         '                }\n'
         '                applied = true;\n'
         '                break;\n'
         '            case BelieveInYou when target?.Player is { } believeInYouTarget:\n'
         '                simulator.GainEnergy(believeInYouTarget, card.DynamicVars.Energy.IntValue);\n'
         '                applied = true;\n'
         '                break;\n',
         True),
    ]
    for anchor, addition, before in anchors:
        if text.count(anchor) != 1:
            raise RuntimeError(f'CardEffectSpecRegistry multiplayer anchor changed: {anchor.strip()!r}')
        text=text.replace(anchor, addition+anchor if before else anchor+addition)
    return text

files=[p for folder in ['Engine','Prediction','Search'] for p in (source/folder).rglob('*.cs')]
runtime='CombatRootSnapshot ContinuationStamp LiveCombatStamp BattleDamageTracker CardDynamicVarWarmup PowerDynamicVarWarmup SimulationNotificationIsolation SolverDisplayNames SolverProgress SolverSettings SolverDiagnostics CombatReplayOutcome PhysicalMemoryUsage SearchGcLifecycleMetrics SearchMemoryPressureSignal'.split()
files += [source/'Runtime'/f'{name}.cs' for name in runtime]
manifest=[]
for path in sorted(files):
    relative=path.relative_to(source)
    text=path.read_text(encoding='utf-8-sig')
    adapted=text.replace('CombatSolver.', 'CoopBots.Kernel.Vendor.').replace('namespace CombatSolver;', 'namespace CoopBots.Kernel.Vendor;')
    if relative.as_posix() == 'Search/SimulatedCombatState.cs':
        original='PlatformUtil.GetPlayerName(RunManager.Instance.NetService.Platform, player.NetId)'
        if adapted.count(original) != 1:
            raise RuntimeError('Upstream player-name capture changed; review the kernel adaptation.')
        adapted=adapted.replace(original, 'player.NetId.ToString(System.Globalization.CultureInfo.InvariantCulture)')
    if relative.as_posix() == 'Runtime/ContinuationStamp.cs':
        original='public static ContinuationStamp CaptureLive(CombatState state)'
        if adapted.count(original) != 1 or adapted.count('Player player = LocalContext.GetMe(state)') != 1:
            raise RuntimeError('Upstream live stamp changed; review multiplayer owner selection.')
        adapted=adapted.replace(original, 'public static ContinuationStamp CaptureLive(CombatState state, Player? selectedPlayer = null)')
        adapted=adapted.replace('Player player = LocalContext.GetMe(state)', 'Player player = selectedPlayer ?? LocalContext.GetMe(state)')
    if relative.as_posix() == 'Prediction/CardEffectSpecRegistry.cs':
        adapted=add_multiplayer_card_support(adapted)
    if relative.as_posix() == 'Engine/InCombat/Mirrors/Hooks/Block/ModifyBlockMultiplicativeMirrors.cs':
        anchor=('        int playerCount = context.State.CombatState.Players.Count;\n'
                '        if (playerCount != 1)\n'
                '            throw new NotSupportedException($"CombatSolver only supports single-player combat, found {playerCount} players.");\n'
                '        return 1m;\n')
        addition=('        // CoopBots multiplayer port: the upstream guard refused playerCount != 1,\n'
                  '        // which made every block card throw in real multiplayer and forced the\n'
                  '        // kernel to skip all defense. Mirror the native scaling instead: player\n'
                  '        // block is unscaled, enemy block scales with the party size.\n'
                  '        var target = context.Target;\n'
                  '        if (target != null && !target.IsPrimaryEnemy && !target.IsSecondaryEnemy) return 1m;\n'
                  '        if (!context.Props.IsPoweredCardOrMonsterMoveBlock()) return 1m;\n'
                  '        int playerCount = context.State.CombatState.Players.Count;\n'
                  '        if (playerCount <= 2) return playerCount;\n'
                  '        return playerCount * MultiplayerScalingModel.GetMultiplayerScaling(\n'
                  '            context.State.CombatState.Encounter, context.State.CombatState.RunState.CurrentActIndex);\n')
        if adapted.count(anchor) != 1:
            raise RuntimeError('ModifyBlockMultiplicative multiplayer scaling anchor changed; review the adaptation.')
        adapted=adapted.replace(anchor, addition)
    if relative.as_posix() == 'Engine/InCombat/Mirrors/Potions/OnUse/PotionOnUseMirrors.cs':
        anchor='        registry.Register<EssenceOfDarkness>(OrbPotionMirrors.EssenceOfDarknessOnUse);\n'
        addition=('\n        // CoopBots multiplayer port: proactive buff/energy potions.\n'
                  '        CoopBots.Kernel.TeamPotionMirrors.Register(registry);\n')
        if adapted.count(anchor) != 1:
            raise RuntimeError('PotionOnUseMirrors anchor changed; review the multiplayer potion registration.')
        adapted=adapted.replace(anchor, anchor+addition)
    if relative.as_posix() == 'Search/SimulatedCombatState.Potions.cs':
        anchor='    public bool IsPotionAvailable(Player player, int slot)\n'
        addition=('    // CoopBots multiplayer port: map a live potion to the cloned instance the\n'
                  '    // simulation owns, so a proactive potion can be used inside a branch.\n'
                  '    public PotionModel? FindPotion(PotionModel live)\n'
                  '    {\n'
                  '        for (int slot = 0; slot < PotionSlotCount(live.Owner); slot++)\n'
                  '        {\n'
                  '            PotionModel? candidate = GetPotionAtSlot(live.Owner, slot);\n'
                  '            if (candidate is not null && candidate.Id == live.Id)\n'
                  '                return candidate;\n'
                  '        }\n'
                  '        return null;\n'
                  '    }\n\n')
        if adapted.count(anchor) != 1:
            raise RuntimeError('SimulatedCombatState.Potions anchor changed; review FindPotion adaptation.')
        adapted=adapted.replace(anchor, addition+anchor)
    if relative.as_posix() in patches:
        patch = patches[relative.as_posix()]
        if hashlib.sha256(adapted.encode()).hexdigest() != patch['base_sha256']:
            raise RuntimeError(f'P1 adaptation base changed: {relative}; review before importing.')
        for change in reversed(patch['changes']):
            start, end = change['start'], change['end']
            if adapted[start:end] != change['old']:
                raise RuntimeError(f'P1 adaptation context mismatch: {relative}')
            adapted = adapted[:start] + change['new'] + adapted[end:]
    target=dest/relative; target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(adapted,encoding='utf-8')
    manifest.append({'path':relative.as_posix(),'upstream_sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
sessions=source/'Runtime/SolverControllerSessions.cs'
session_text=sessions.read_text(encoding='utf-8-sig')
progress=session_text[session_text.index('internal sealed class SearchProgressDisplayState'):session_text.index('internal sealed class SolverCombatSession')]
(dest/'Runtime/SearchProgressDisplayState.cs').write_text('namespace CoopBots.Kernel.Vendor;\n'+progress,encoding='utf-8')
manifest.append({'path':'Runtime/SearchProgressDisplayState.cs','upstream':'Runtime/SolverControllerSessions.cs:SearchProgressDisplayState','upstream_sha256':hashlib.sha256(sessions.read_bytes()).hexdigest()})
(dest.parent/'UPSTREAM.json').write_text(json.dumps({'source':'CombatSolver 0.33.9','namespace':'CoopBots.Kernel.Vendor','files':manifest},indent=2),encoding='utf-8')
print(f'Imported {len(files)} source files; no upstream startup/UI/test runner.')

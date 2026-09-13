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

def add_team_card_support(text):
    """CoopBots multiplayer cards the snapshot cannot infer, applied after
    add_multiplayer_card_support. Each block is anchored on upstream text and
    aborts the import if the anchor moved, so a silent loss is impossible."""
    replacements=[
        # 1. Power hooks that already exist; only the OnPlay application was missing.
        ('        [typeof(Unrelenting)] = [Owner<FreeAttackPower>(_ => 1)],\n'
         '        [typeof(Veilpiercer)] = [Owner<VeilpiercerPower>(_ => 1)],\n'
         '    };\n',
         '        [typeof(Unrelenting)] = [Owner<FreeAttackPower>(_ => 1)],\n'
         '        [typeof(Veilpiercer)] = [Owner<VeilpiercerPower>(_ => 1)],\n'
         '        // CoopBots multiplayer port: the power hooks already exist in this\n'
         '        // snapshot (AfterCardDrawn / FrostOrb / AfterCardGeneratedForCombat /\n'
         '        // AfterDamageGiven), so only the OnPlay application was missing.\n'
         '        [typeof(Cacophony)] = [Owner<CacophonyPower>(card => card.DynamicVars.Cards.IntValue)],\n'
         '        [typeof(Hibernate)] = [Owner<HibernatePower>(_ => 1)],\n'
         '        [typeof(Soulbound)] = [Target<SoulboundPower>(_ => 1)],\n'
         '        [typeof(Underworld)] = [Owner<UnderworldPower>(_ => 1)],\n'
         '    };\n'),
        # 2. Resource effects: team-wide next-turn draw, HP-for-ally-block.
        ('        typeof(EnergySurge), typeof(BelieveInYou),\n',
         '        typeof(EnergySurge), typeof(BelieveInYou),\n'
         '        // CoopBots multiplayer port: team-wide next-turn draw and the\n'
         '        // HP-for-ally-block trade; both are handled in the switch.\n'
         '        typeof(Plot), typeof(DemonicShield),\n'),
        # 3. Generation effects: copies / gifts to every player.
        ('        typeof(Severance), typeof(Undeath),\n'
         '    ];\n',
         '        typeof(Severance), typeof(Undeath),\n'
         '        // CoopBots multiplayer port: copies and gifts that reach EVERY player.\n'
         '        typeof(Outrage), typeof(BladeSymphony), typeof(GlimpseBeyond), typeof(LegionOfBone),\n'
         '    ];\n'),
        # 4. The per-player loops themselves.
        ('                        && candidate.Preview.Type is CardType.Attack or CardType.Skill or CardType.Power)\n'
         '                    .ToArray();\n'
         '                simulator.AddToPile(cards, PileType.Hand);\n'
         '                applied = true;\n'
         '                break;\n'
         '            }\n',
         '                        && candidate.Preview.Type is CardType.Attack or CardType.Skill or CardType.Power)\n'
         '                    .ToArray();\n'
         '                simulator.AddToPile(cards, PileType.Hand);\n'
         '                applied = true;\n'
         '                break;\n'
         '            }\n'
         '            case Outrage:\n'
         '                // One copy per player, the caster included.\n'
         '                foreach (MegaCrit.Sts2.Core.Entities.Players.Player recipient in combat.Players)\n'
         '                {\n'
         '                    simulator.CreateAndAddGeneratedCardsToCombat<Outrage>(\n'
         '                        recipient, PileType.Discard, 1, card.Owner);\n'
         '                    if (simulator.HasPendingChoice)\n'
         '                        return true;\n'
         '                }\n'
         '                applied = true;\n'
         '                break;\n'
         '            case BladeSymphony:\n'
         '                foreach (MegaCrit.Sts2.Core.Entities.Players.Player recipient in combat.Players)\n'
         '                {\n'
         '                    simulator.CreateAndAddGeneratedCardsToCombat<Shiv>(\n'
         '                        recipient, PileType.Hand, card.DynamicVars.Cards.IntValue, card.Owner);\n'
         '                    if (simulator.HasPendingChoice)\n'
         '                        return true;\n'
         '                }\n'
         '                applied = true;\n'
         '                break;\n'
         '            case GlimpseBeyond:\n'
         '                foreach (MegaCrit.Sts2.Core.Entities.Players.Player recipient in combat.Players)\n'
         '                {\n'
         '                    simulator.CreateAndAddGeneratedCardsToCombat<Soul>(\n'
         '                        recipient, PileType.Draw, card.DynamicVars.Cards.IntValue, card.Owner);\n'
         '                    if (simulator.HasPendingChoice)\n'
         '                        return true;\n'
         '                }\n'
         '                applied = true;\n'
         '                break;\n'
         '            case LegionOfBone when combat is ICombatPredictionEffectSink summonSink:\n'
         '                foreach (MegaCrit.Sts2.Core.Entities.Players.Player recipient in combat.Players)\n'
         '                {\n'
         '                    summonSink.SummonOsty(simulator, recipient, card.DynamicVars["Summon"].IntValue);\n'
         '                    if (simulator.HasPendingChoice)\n'
         '                        return true;\n'
         '                }\n'
         '                applied = true;\n'
         '                break;\n'
         '            case Plot:\n'
         '                foreach (MegaCrit.Sts2.Core.Entities.Players.Player recipient in combat.Players)\n'
         '                    ApplyPower(combat, typeof(DrawCardsNextTurnPower), recipient.Creature,\n'
         '                        card.DynamicVars.Cards.IntValue, ownerCreature);\n'
         '                applied = true;\n'
         '                break;\n'
         '            case DemonicShield when target is { IsPlayer: true }:\n'
         '                simulator.Damage(card.Owner.Creature, card.DynamicVars.HpLoss.IntValue,\n'
         '                    ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move, card.Owner.Creature);\n'
         '                if (simulator.HasPendingChoice)\n'
         '                    return true;\n'
         '                simulator.GainBlock(target, card.DynamicVars["CalculatedBlock"].IntValue, ValueProp.Unpowered);\n'
         '                applied = true;\n'
         '                break;\n'),
    ]
    for anchor, replacement in replacements:
        if text.count(anchor) != 1:
            raise RuntimeError(f'CardEffectSpecRegistry team-card anchor changed: {anchor.strip()[:60]!r}')
        text=text.replace(anchor, replacement)
    # FLANKING joins KNOCKDOWN as a teammate-only damage amplifier.
    flanking_anchor='        [typeof(Knockdown)] = [Target<KnockdownPower>("KnockdownPower")],\n'
    flanking_replacement=(flanking_anchor +
        '        // CoopBots multiplayer port: FLANKING declares no variable; each\n'
        '        // application is one stack and the multiplier lives in the power hook.\n'
        '        [typeof(Flanking)] = [Target<FlankingPower>(_ => 1)],\n')
    if text.count(flanking_anchor) != 1:
        raise RuntimeError('CardEffectSpecRegistry Knockdown spec anchor changed; review Flanking.')
    text=text.replace(flanking_anchor, flanking_replacement)
    # The Applier name is recorded by SimulatedCombatState from its own table;
    # overwriting it with the live platform lookup threw outside a Steam host.
    nre_anchor=('                        if (effect.PowerType == typeof(KnockdownPower)\n'
                '                            && combat.GetPower<KnockdownPower>(effectTarget) is { } knockdown)\n'
                '                        {\n'
                '                            ((StringVar)knockdown.DynamicVars["Applier"]).StringValue = PlatformUtil.GetPlayerName(\n'
                '                                RunManager.Instance.NetService.Platform,\n'
                '                                playedCard.Preview.Owner.NetId);\n'
                '                        }\n')
    if text.count(nre_anchor) != 1:
        raise RuntimeError('CardEffectSpecRegistry Knockdown applier anchor changed; review the NRE fix.')
    text=text.replace(nre_anchor,
        '                        // CoopBots multiplayer port: SimulatedCombatState.ApplyPower\n'
        '                        // records the Applier from its own player-name table. The\n'
        '                        // live platform lookup that used to run here threw outside a\n'
        '                        // Steam host and disagreed with the simulated state.\n')
    return text

def add_team_damage_support(text):
    """FLANKING / KNOCKDOWN ride the same ModifyDamageMultiplicative pass as
    Vulnerable; the handlers live in the non-vendored TeamDamageMirrors."""
    anchor=('        registry.Register<PenNib>(HandlePenNib);\n'
            '        registry.Register<UndyingSigil>(HandleUndyingSigil);\n'
            '\n'
            '        return registry;\n'
            '    }\n')
    replacement=('        registry.Register<PenNib>(HandlePenNib);\n'
                 '        registry.Register<UndyingSigil>(HandleUndyingSigil);\n'
                 '\n'
                 '        // CoopBots multiplayer port: FLANKING / KNOCKDOWN amplify another\n'
                 '        // player\'s attack damage for the rest of the turn. They are a special\n'
                 '        // Vulnerable, so they belong on this same multiplicative pass.\n'
                 '        CoopBots.Kernel.TeamDamageMirrors.Register(registry);\n'
                 '\n'
                 '        return registry;\n'
                 '    }\n')
    if text.count(anchor) != 1:
        raise RuntimeError('ModifyDamageMirrors multiplicative registry anchor changed; review Flanking/Knockdown.')
    return text.replace(anchor, replacement)

def add_flanking_applier(text):
    """FLANKING needs the applier recorded, because its bonus excludes the
    applier's own attack; KNOCKDOWN already does this."""
    anchor=('        if (simulated is KnockdownPower knockdown && applier != null)\n'
            '        {\n'
            '            Player? applyingPlayer = applier.Player\n'
            '                ?? Players.FirstOrDefault(player => player.Creature.CombatId == applier.CombatId);\n'
            '            if (applyingPlayer == null)\n'
            '                throw new InvalidOperationException("击倒 Power 的施加者不是战斗中的玩家。");\n'
            '            ((StringVar)knockdown.DynamicVars["Applier"]).StringValue = _playerNames[applyingPlayer];\n'
            '        }\n')
    addition=('        // CoopBots multiplayer port: FLANKING needs the same applier record as\n'
              '        // KNOCKDOWN, because its damage bonus excludes the applier\'s own attack.\n'
              '        if (simulated is FlankingPower flanking && applier != null)\n'
              '        {\n'
              '            Player? applyingPlayer = applier.Player\n'
              '                ?? Players.FirstOrDefault(player => player.Creature.CombatId == applier.CombatId);\n'
              '            if (applyingPlayer == null)\n'
              '                throw new InvalidOperationException("夹击 Power 的施加者不是战斗中的玩家。");\n'
              '            ((StringVar)flanking.DynamicVars["Applier"]).StringValue = _playerNames[applyingPlayer];\n'
              '        }\n')
    if text.count(anchor) != 1:
        raise RuntimeError('SimulatedCombatState Knockdown applier anchor changed; review Flanking.')
    return text.replace(anchor, anchor + addition)

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
        adapted=add_team_card_support(adapted)
    if relative.as_posix() == 'Engine/InCombat/Mirrors/Hooks/Damage/ModifyDamageMirrors.cs':
        adapted=add_team_damage_support(adapted)
    if relative.as_posix() == 'Search/SimulatedCombatState.cs':
        adapted=add_flanking_applier(adapted)
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

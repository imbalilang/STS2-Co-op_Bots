"""Import authorized upstream source, excluding mod startup, UI and automation."""
from pathlib import Path
from kernel_attack_pair_adaptations import apply_attack_pair_adaptations
from kernel_class_power_adaptations import apply_class_power_adaptations
from kernel_relic_choice_adaptations import apply_relic_choice_adaptations
from kernel_power_fork_adaptations import write_power_fork_support
from kernel_decision_consistency_adaptations import apply_decision_consistency_adaptations
from kernel_frozen_hook_adaptations import apply_frozen_hook_adaptations
from kernel_temporary_stat_adaptations import apply_temporary_stat_adaptations
from kernel_latest_model_adaptations import apply_latest_model_adaptations
from kernel_latest_choice_adaptations import apply_latest_choice_adaptations
from kernel_retained_support_adaptations import apply_retained_support_adaptations
from kernel_szg_model_adaptations import apply_szg_model_adaptations, write_szg_party_support
from kernel_risk_prefix_coverage_adaptations import apply_risk_prefix_coverage_adaptations
from kernel_orichalcum_capture_adaptations import apply_orichalcum_capture_adaptations
from kernel_history_capture_adaptations import apply_history_capture_adaptations
from kernel_power_origin_adaptations import apply_power_origin_adaptations
import hashlib, json, argparse
root=Path(__file__).resolve().parents[1]
source=root/'work/combatsolver-sync-latest/src'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, default=root/'src/CoopBots.Kernel/Vendor')
parser.add_argument('--skip-patches', action='store_true')
args = parser.parse_args()
dest = args.output
patch_path = root/'scripts/kernel-p1-adaptations-41.json'
patches = {} if args.skip_patches or not patch_path.exists() else json.loads(patch_path.read_text(encoding='utf-8'))

def apply_the_ball_growth(relative, text):
    """Current-DLL paid TheBall growth. Exact 0.41 adapted base/seam/target."""
    # Actual guard R2 RED: TheBall import guard: corrupted growth bookkeeping must fail the exact target checksum
    # Withdrawing only the target checksum admitted the deliberately bad +1m
    # receipt. Exact adapter restoration rejected it again (0 -> 1 -> 0).
    if relative != 'Prediction/CardEffectSpecRegistry.cs': return text
    if hashlib.sha256(text.encode()).hexdigest() != '2ababa62d7b87a86d13c81bfb7a0b226d11ca3a709cdc366ac650f2d7724dcf1':
        raise RuntimeError('TheBall growth predecessor changed: ' + relative)
    anchor = '            case Rampage rampage:\n'
    addition = '            // Upstream 0.41 completes Rampage growth but omits TheBall\'s\n            // multiplayer growth despite the existing result-location mirror.\n            // Mirror native TheBall after its generic damage transaction: keep\n            // both current damage and the private downgrade-restoration total.\n            // User 2026-10-09 A10 native correctness request; paid current-DLL\n            // witness found 20/10 native damage versus 10/0 branch growth.\n            case TheBall theBall:\n            {\n                decimal increase = theBall.DynamicVars["Increase"].BaseValue;\n                TheBall mutableBall = (TheBall)playedCard.MutablePreview;\n                mutableBall.DynamicVars.Damage.BaseValue += increase;\n                mutableBall.ExtraDamageFromPlays += increase;\n                applied = true;\n                break;\n            }\n'
    if text.count(anchor) != 1:
        raise RuntimeError('TheBall growth seam changed: ' + relative)
    text = text.replace(anchor, addition + anchor)
    if hashlib.sha256(text.encode()).hexdigest() != '06960eaabe0177191c7f7dfbd2c6e2c35e62516a9618d40049c9205b57bb6881':
        raise RuntimeError('TheBall growth target differs: ' + relative)
    return text

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
        ('        typeof(BigBang), typeof(BloodWall), typeof(Breakthrough), typeof(BrightestFlame), typeof(GatherLight),\n',
         '        // CoopBots multiplayer port: instant team/target energy, no power lifecycle needed.\n'
         '        typeof(EnergySurge), typeof(BelieveInYou),\n',
         False),
        # 0.41 removed ADRENALINE from this dispatch (it now has its own OnPlay mirror
        # in CardDrawCardMirrors.AdrenalineOnPlay), which is why the anchor is the
        # switch's first case rather than the case this patch used to sit above.
        ('        switch (card)\n'
         '        {\n'
         '            case AllForOne:\n',
         '            // CoopBots multiplayer port: ENERGY_SURGE pays every living teammate and\n'
         '            // BELIEVE_IN_YOU pays the chosen ally. Upstream models only the caster,\n'
         '            // which is the same card in single player.\n'
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
         False),
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
         '                // Native Outrage clones the played instance for each living teammate.\n'
         '                // Preserve upgrades/modifiers; generating canonical base cards breaks\n'
         '                // both damage predictions and later deployment identity.\n'
         '                foreach (MegaCrit.Sts2.Core.Entities.Players.Player recipient in combat.Players)\n'
         '                {\n'
         '                    if (!simulator.State.GetCreature(recipient.Creature).IsAlive)\n'
         '                        continue;\n'
         '                    simulator.AddGeneratedCardToCombat(\n'
         '                        playedCard.CreateCloneForPlayer(recipient), PileType.Discard,\n'
         '                        card.Owner, CardPilePosition.Bottom, CardGenerationResultKind.Fixed);\n'
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

def add_team_multiplier_lifetime(text):
    """FLANKING / KNOCKDOWN remove themselves when their owner's side ends its turn.

    Only their damage half used to be mirrored, so a single application multiplied
    every later round of a cached plan. LIVE 2026-09-29 (A10 4-bot, floor 25
    ENTOMANCER_ELITE): the script was labelled ending=VICTORY (VerifiedTerminal), all
    81 steps were replayed action-for-action, and the party still wiped with 218/789
    left on the elite — the rollout had doubled its R3-R7 output because the FLANKING
    applied in R2 never came off. FlankingLifetimeScenarios pins the lifetime.
    """
    flanking=('                case GravityPower when ownerParticipates:\n'
              '                    combat.SetAmount<GravityPower>(owner, 0);\n'
              '                    break;\n')
    flankingBody=('                // LIVE 2026-09-29 (A10 4-bot, ENTOMANCER_ELITE floor 25): only the damage half\n'
                  '                // of FLANKING was mirrored, so a single application rode every later round of\n'
                  '                // the cached rollout. The script was replayed action-for-action and the party\n'
                  '                // still wiped with 218/789 left on the elite, while the rollout had "killed" it\n'
                  '                // during R7 — the verified VICTORY was this multiplier outliving its turn.\n'
                  '                // FlankingPower.AfterSideTurnEnd removes the power when its owner\'s side ends\n'
                  '                // its turn; FlankingLifetimeScenarios pins that lifetime.\n'
                  '                case FlankingPower when ownerParticipates:\n'
                  '                    combat.SetPowerAmount(power, 0);\n'
                  '                    break;\n')
    knockdown=('                case TenderPower when ownerParticipates:\n')
    knockdownBody=('                // Same shape as FLANKING above: KnockdownPower.AfterSideTurnEnd removes the\n'
                   '                // teammate multiplier when its owner\'s side ends its turn.\n'
                   '                case KnockdownPower when ownerParticipates:\n'
                   '                    combat.SetPowerAmount(power, 0);\n'
                   '                    break;\n')
    if text.count(flanking) != 1:
        raise RuntimeError('EndTurnPowerSupport Gravity anchor changed; review Flanking lifetime.')
    if text.count(knockdown) != 1:
        raise RuntimeError('EndTurnPowerSupport Tender anchor changed; review Knockdown lifetime.')
    return text.replace(flanking, flankingBody + flanking).replace(knockdown, knockdownBody + knockdown)

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


def add_rampart_once_per_round(relative, text):
    """RampartPower is a SIDE-turn-start hook; a per-seat driver must not multiply it.

    Native: RampartPower.AfterSideTurnStart(side, participants, state) performs
    CreatureCmd.GainBlock (coverage/state-mutations.json, runtimeVerified) -- an ordinary
    side-turn-start hook, so it grants once per player SIDE turn with the whole
    participant list. Upstream's solver drives that sequence for a single player, so its
    ungated loop below grants once. This port drives the same sequence once per SEAT
    (KernelTurns.StartNextPlayerTurn), so the grant landed seats times.

    LIVE 2026-10-06 (seed 8W0NNPPSPFTX, act 3, TURRET_OPERATOR_WEAK): the refinement
    boundary check reported `field=E1.block expected={480} actual={120}` three times in
    one fight, and 480 = 4 seats x RAMPART_POWER(120). The value was CONSTANT across the
    reported rounds, which pins it to "once per seat inside one round" rather than a
    per-round accumulation -- the sim clears enemy block at the enemy side-turn start, so
    accumulation would have read 240/360/480.

    The claim is keyed by the power's owner and compared against RoundNumber, so a stale
    entry is ignored without needing a reset hook, and the FORKABLE dictionary keeps a
    branch that grants from leaking into a sibling. Regression:
    KernelEngineScenarios.RampartGrantsOncePerPlayerTurnNotOncePerSeat
    (R2 red: "a 4-seat table got 480 (= 4x)").
    """
    if relative == 'Prediction/PersistentPowerSupport.cs':
        anchor = ('            if (rampart.Amount <= 0)\n'
                  '                continue;\n')
        addition = ('            // CoopBots multiplayer port: RampartPower is a native SIDE-turn-start hook\n'
                    '            // (AfterSideTurnStart -> CreatureCmd.GainBlock), so it grants once per player\n'
                    '            // SIDE turn. This port drives that once per SEAT, which multiplied the grant by\n'
                    '            // the table size; live 2026-10-06 read `E1.block expected={480} actual={120}`\n'
                    '            // three times in TURRET_OPERATOR_WEAK (480 = 4 x RAMPART_POWER 120).\n'
                    '            if (!combat.TryClaimRampartGrant(rampart.Owner, combat.RoundNumber))\n'
                    '                continue;\n')
        if text.count(anchor) != 1 or text.count('            foreach (Creature enemy in combat.Enemies)\n') != 1:
            raise RuntimeError('PersistentPowerSupport Rampart anchor changed; review the once-per-round claim.')
        return text.replace(anchor, anchor + addition)
    if relative == 'Search/SimulatedCombatState.cs':
        field_anchor = '    private ForkableDictionary<Creature, bool>? _nemesisShouldApplyIntangible;\n'
        field_addition = ('    // CoopBots multiplayer port: RampartPower grants once per player SIDE turn, but this\n'
                          '    // port drives that turn start once per SEAT; the claim keeps a four-seat table at one\n'
                          '    // grant. Keyed by owner and compared against RoundNumber, so no reset hook is needed;\n'
                          '    // FORKABLE so a branch that grants cannot leak into a sibling.\n'
                          '    private ForkableDictionary<Creature, int>? _rampartGrantedRound;\n')
        method_anchor = ('    public bool ShouldClearBlock(Creature owner)\n'
                         '        => ShouldClearBlock(owner, out _);\n')
        method_addition = ('    /// <summary>\n'
                           '    /// CoopBots multiplayer port: true at most once per (Rampart power owner, round), so\n'
                           '    /// the per-seat turn-start driver cannot multiply the native once-per-side grant.\n'
                           '    /// </summary>\n'
                           '    public bool TryClaimRampartGrant(Creature owner, int round)\n'
                           '    {\n'
                           '        _rampartGrantedRound ??= new ForkableDictionary<Creature, int>();\n'
                           '        if (_rampartGrantedRound.TryGetValue(owner, out var claimed) && claimed == round)\n'
                           '            return false;\n'
                           '        _rampartGrantedRound[owner] = round;\n'
                           '        return true;\n'
                           '    }\n'
                           '\n')
        if text.count(field_anchor) != 1 or text.count(method_anchor) != 1:
            raise RuntimeError('SimulatedCombatState Rampart anchors changed; review the once-per-round claim.')
        return text.replace(field_anchor, field_anchor + field_addition).replace(method_anchor, method_addition + method_anchor)
    raise RuntimeError(f'add_rampart_once_per_round called for an unregistered file: {relative}')


def make_stamp_fields_positionally_stable(text):
    """The party stamp is compared as ONE ';'-separated list, so its field COUNT must not
    depend on the state, and P= must be enumerated the same way on both sides.

    LIVE 2026-10-06 (seed 8W0NNPPSPFTX): `field_order[35] expected={PT=0:GUARDED_POWER=1@0,…}
    actual={R=3008…}` in FABRICATOR_NORMAL -- the prediction had a targeted power and the live
    board did not, so PT was emitted on one side only, every later field was compared one slot
    off, and the real difference (a missing GUARDED) was never named. `onplay_configuration`
    had the same shape. Separately, DescribeDifferences compares the `P=` list by INDEX while
    the two renderers enumerate powers differently (live: Creatures.SelectMany(c => c.Powers);
    a branch: EffectivePowers(), the hook-listener order), so `P[n]` could be a pure order
    artefact. Both fixes make the SAME text on both sides, so index n means the same thing.

    This hook also carries the pre-existing P0-SIM-1 targeted-power ledger (`active` / `targeted`
    / `PT=`), which was live in production but had never been added to the pipeline -- the
    whole block would have been lost on the next re-import. Regression:
    KernelEngineScenarios.StampFieldsArePositionallyStable.
    """
    live_old = '        if (AdaptedCardOnPlayMirrors.CaptureLiveStamp() is { } onPlayStamp)\n            text.Append(";onplay_configuration=").Append(onPlayStamp);\n'
    live_new = '        // CoopBots multiplayer port: emit this field even when empty. DescribeDifferences\n        // compares the whole party text as ONE \';\'-separated list, so a field present on\n        // one side and absent on the other shifts every later field and reports\n        // `field_order[i]` instead of the field that actually differs.\n        text.Append(";onplay_configuration=");\n        if (AdaptedCardOnPlayMirrors.CaptureLiveStamp() is { } onPlayStamp)\n            text.Append(onPlayStamp);\n'
    pred_old = '        if (combat.AdaptedOnPlay is { } adaptedOnPlay)\n            text.Append(";onplay_configuration=").Append(adaptedOnPlay.Stamp);\n'
    pred_new = '        // CoopBots multiplayer port: always emit, same reason as the live branch above.\n        text.Append(";onplay_configuration=");\n        if (combat.AdaptedOnPlay is { } adaptedOnPlay)\n            text.Append(adaptedOnPlay.Stamp);\n'
    method_old = '    private static void AppendPowers(StringBuilder text, IEnumerable<PowerModel> powers, CombatPredictionSimulator? simulator = null)\n    {\n        text.Append(";P=");\n        foreach (PowerModel power in powers.Where(power => power.Amount != 0))\n        {\n            text.Append(power.Owner.CombatId).Append(\':\').Append(power.Id.Entry).Append(\'=\')\n                .Append(power.Amount).Append(\'/\')\n                .Append(PowerLifecycleSupport.SemanticallyRelevantAmountOnTurnStart(power));\n            if (PowerLifecycleSupport.SemanticallyRelevantSkipNextDurationTick(power)) text.Append("/skip");\n            text.Append(\'[\');\n            foreach (var dynamicVar in power.DynamicVars.OrderBy(item => item.Key, StringComparer.Ordinal))\n            {\n                if (!SemanticStateFieldPolicy.IsSemantic(power, dynamicVar.Key, dynamicVar.Value))\n                    continue;\n                text.Append(dynamicVar.Key).Append(\'=\').Append(dynamicVar.Value.BaseValue);\n                if (dynamicVar.Value is StringVar stringVar)\n                    text.Append(\':\').Append(stringVar.StringValue);\n                text.Append(\',\');\n            }\n            if (power is SurroundedPower surrounded)\n                text.Append("Facing=").Append(simulator == null ? surrounded.Facing\n                    : PowerPredictionStateSupport.SurroundedFacing(simulator, surrounded)).Append(\',\');\n            if (power is NightmarePower nightmare)\n            {\n                CardModel selected = simulator == null\n                    ? nightmare.GetInternalData<NightmarePower.Data>().selectedCard\n                        ?? throw new InvalidOperationException("Native Nightmare has no selected card at continuation capture.")\n                    : ((SimulatedCombatState)simulator.State.CombatState).GetNightmareSelection(nightmare).Preview;\n                text.Append("Selected=");\n                AppendCard(text, selected, discoverUnregisteredBaseLibModifiers: simulator == null);\n            }\n            if (power is OrbitPower orbit)\n                text.Append("EnergyRemainder=").Append(simulator == null ? (4 - orbit.DisplayAmount) % 4\n                    : ((SimulatedCombatState)simulator.State.CombatState).GetOrbitEnergyRemainder(orbit)).Append(\',\');\n            text.Append("],");\n        }\n    }\n\n'
    method_new = '    private static void AppendPowers(StringBuilder text, IEnumerable<PowerModel> powers, CombatPredictionSimulator? simulator = null)\n    {\n        // CoopBots multiplayer port: canonical order, because DescribeDifferences compares\n        // the P= list by INDEX. The two renderers enumerate powers differently (live uses\n        // `state.Creatures.SelectMany(c => c.Powers)`, a branch uses `EffectivePowers()` --\n        // the hook-listener order), so the same board could render the same amounts onto\n        // different seats and compare equal, or report a `P[n]` diff that is only an\n        // enumeration-order difference. Sorting by (owner, type) makes index n mean the\n        // same power on both sides.\n        PowerModel[] active = powers.Where(power => power.Amount != 0)\n            .OrderBy(power => power.Owner.CombatId)\n            .ThenBy(power => power.Id.Entry, StringComparer.Ordinal)\n            .ToArray();\n        text.Append(";P=");\n        foreach (PowerModel power in active)\n        {\n            text.Append(power.Owner.CombatId).Append(\':\').Append(power.Id.Entry).Append(\'=\')\n                .Append(power.Amount).Append(\'/\')\n                .Append(PowerLifecycleSupport.SemanticallyRelevantAmountOnTurnStart(power));\n            if (PowerLifecycleSupport.SemanticallyRelevantSkipNextDurationTick(power)) text.Append("/skip");\n            text.Append(\'[\');\n            foreach (var dynamicVar in power.DynamicVars.OrderBy(item => item.Key, StringComparer.Ordinal))\n            {\n                if (!SemanticStateFieldPolicy.IsSemantic(power, dynamicVar.Key, dynamicVar.Value))\n                    continue;\n                text.Append(dynamicVar.Key).Append(\'=\').Append(dynamicVar.Value.BaseValue);\n                if (dynamicVar.Value is StringVar stringVar)\n                    text.Append(\':\').Append(stringVar.StringValue);\n                text.Append(\',\');\n            }\n            if (power is SurroundedPower surrounded)\n                text.Append("Facing=").Append(simulator == null ? surrounded.Facing\n                    : PowerPredictionStateSupport.SurroundedFacing(simulator, surrounded)).Append(\',\');\n            if (power is NightmarePower nightmare)\n            {\n                CardModel selected = simulator == null\n                    ? nightmare.GetInternalData<NightmarePower.Data>().selectedCard\n                        ?? throw new InvalidOperationException("Native Nightmare has no selected card at continuation capture.")\n                    : ((SimulatedCombatState)simulator.State.CombatState).GetNightmareSelection(nightmare).Preview;\n                text.Append("Selected=");\n                AppendCard(text, selected, discoverUnregisteredBaseLibModifiers: simulator == null);\n            }\n            if (power is OrbitPower orbit)\n                text.Append("EnergyRemainder=").Append(simulator == null ? (4 - orbit.DisplayAmount) % 4\n                    : ((SimulatedCombatState)simulator.State.CombatState).GetOrbitEnergyRemainder(orbit)).Append(\',\');\n            text.Append("],");\n        }\n        // P0-SIM-1（2026-10-05 伪 VICTORY）：上面的 P= 是有序表。实机渲染\n        // `Creatures.SelectMany(c => c.Powers)`，分支渲染 `EffectivePowers()`（hook listener\n        // 顺序），两侧口径不同时，两个"把同一个 amount 序列挂到不同席位"的盘面可以渲染成\n        // 同一段文本。SandpitPower 的全部语义就在 Target 上，逐席交换会翻转"谁被倒计时处死"。\n        // 这里按 (owner,type,target) 排序单独渲染一遍，让交换无法再被枚举顺序掩盖。\n        PowerModel[] targeted = active.Where(power => power.Target is not null)\n            .OrderBy(power => power.Owner.CombatId)\n            .ThenBy(power => power.Id.Entry, StringComparer.Ordinal)\n            .ThenBy(power => power.Target!.CombatId)\n            .ToArray();\n        // CoopBots multiplayer port: always emit PT=, even with no target. Live 2026-10-06\n        // (FABRICATOR_NORMAL) reported `field_order[35] expected={PT=0:GUARDED_POWER=1@0,…}\n        // actual={R=3008…}` -- the prediction had a targeted power and the live board did\n        // not, so the field COUNT differed and every later field was compared one slot off.\n        // An empty PT= keeps the layout fixed and turns that case into a readable\n        // `PT expected={…} actual={}`.\n        text.Append(";PT=");\n        foreach (PowerModel power in targeted)\n            text.Append(power.Owner.CombatId).Append(\':\').Append(power.Id.Entry).Append(\'=\')\n                .Append(power.Amount).Append(\'@\').Append(power.Target!.CombatId).Append(\',\');\n    }\n\n'
    if text.count(live_old) != 1 or text.count(pred_old) != 1 or text.count(method_old) != 1:
        raise RuntimeError('ContinuationStamp anchors changed; review the stamp field stability port.')
    if text.count(live_new) or text.count(pred_new) or text.count(method_new):
        raise RuntimeError('ContinuationStamp already carries the stamp field stability port; drop the hook.')
    return text.replace(live_old, live_new).replace(pred_old, pred_new).replace(method_old, method_new)


def make_music_box_forkable(text):
    """Music Box must not take the whole planner down with it (see VENDOR_DEVIATIONS D7).

    Upstream refuses to fork this prediction state while a card play is in flight. In a
    live 4-seat run that refusal escaped `KernelCombatPlanner.Choose`, so the tournament
    declined the tick and the weakest planner took over — six consecutive decisions,
    exactly for the seats holding the relic. A shallow copy carries the live CardModel
    that both branches share; the worst case is one missed Ethereal copy, not a lost search.
    """
    anchor = (
        '    public object Fork(PredictionForkContext context)\n'
        '    {\n'
        '        AssertForkable();\n'
        '        return MemberwiseClone();\n'
        '    }\n'
        '\n'
        '    public void AssertForkable()\n'
        '    {\n'
        '        if (CardBeingPlayed is not null)\n'
        '            throw new InvalidOperationException("Cannot fork Music Box during card-play resolution.");\n'
        '    }\n')
    replacement = (
        '    // CoopBots: a fork CARRIES this state instead of refusing to fork at all.\n'
        '    // Upstream throws here; the live cost was measured 2026-09-30 (A10 four seats,\n'
        '    // AEONGLASS_BOSS): the two Music Box holders threw out of the tournament on six\n'
        '    // consecutive round-1 decisions and fell back to the legacy planner\n'
        '    // ("falling back for this tick — InvalidOperationException: Cannot fork Music Box\n'
        '    // during card-play resolution") — the strongest planner switching itself off in\n'
        '    // the fight that wiped the run.\n'
        '    //\n'
        '    // CardBeingPlayed is only a marker spanning one card play\'s before/after hooks and\n'
        '    // holds a live CardModel both branches share, so a shallow copy is a valid state for\n'
        '    // a branch. If the branch does not continue that same card play the loss is bounded:\n'
        '    // the box hands out no Ethereal copy for the rest of the turn and WasUsedThisTurn\n'
        '    // resets at the turn boundary. Losing one copy beats losing the search.\n'
        '    public object Fork(PredictionForkContext context) => MemberwiseClone();\n'
        '\n'
        '    // Deliberately still an IPredictionForkBoundary: that interface ALSO keeps this\n'
        '    // state out of the manual card-choice continuation path\n'
        '    // (PredictionStateStore.SupportsManualCardChoiceContinuation), which is a separate\n'
        '    // and still-valid restriction. Only the fork refusal is lifted.\n'
        '    public void AssertForkable()\n'
        '    {\n'
        '    }\n')
    if text.count(anchor) != 1:
        raise RuntimeError('MusicBox fork boundary anchor changed; review the D7 adaptation.')
    return text.replace(anchor, replacement)


files=[p for folder in ['Engine','Prediction','Search'] for p in (source/folder).rglob('*.cs')]
runtime='CombatRootSnapshot ContinuationStamp LiveCombatStamp BattleDamageTracker CardDynamicVarWarmup PowerDynamicVarWarmup SimulationNotificationIsolation SolverDisplayNames SolverProgress SolverSettings SolverDiagnostics CombatReplayOutcome PhysicalMemoryUsage SearchGcLifecycleMetrics SearchMemoryPressureSignal'.split()
files += [source/'Runtime'/f'{name}.cs' for name in runtime]

def preserve_verified_registry_fixes(text):
    """Retain production fixes established after the 0.41 import.

    Apply after positional patches so these additions cannot shift their offsets.
    Keep AllForOne's body adjacent to its case; restore ally powers and lifecycle
    dispatch. Each exact anchor must occur once, otherwise review the new source.
    R2 reimport evidence: work/strength-20260927-v3/import-check*.log.
    """
    replacements = [
        ((            '            // CoopBots multiplayer port: ENERGY_SURGE pays every living teammate and\n'
            '            // BELIEVE_IN_YOU pays the chosen ally. Upstream models only the caster,\n'
            '            // which is the same card in single player.\n'
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
            '                break;\n'), ''),
        ((            '            case Outrage:\n'), (            '            // CoopBots multiplayer port: ENERGY_SURGE pays every living teammate and\n'
            '            // BELIEVE_IN_YOU pays the chosen ally. Upstream models only the caster,\n'
            '            // which is the same card in single player.\n'
            '            //\n'
            '            // This case label used to be MISSING. Without it AllForOne fell through into\n'
            "            // EnergySurge's body while AllForOne's own body sat below as an unlabelled block —\n"
            '            // unreachable, and flagged by `warning CS0162: unreachable code`.\n'
            '            //\n'
            '            // The verified consequence is that ALL_FOR_ONE was UNUSABLE, not mispriced:\n'
            "            // AllForOne carries only a DamageVar, so EnergySurge's body evaluated\n"
            '            // `card.DynamicVars.Energy` and threw KeyNotFoundException out of this method. The\n'
            '            // search records that as a prediction exception, so the card could never appear in\n'
            '            // a plan. The evidence had been printed on EVERY kernel-suite run, all session:\n'
            '            //   COVERAGE BROKEN: ALL_FOR_ONE:prediction-exception:KeyNotFoundException:\n'
            "            //   The given key 'Energy' was not present in the dictionary.\n"
            '            //   @ …CardEffectSpecRegistry.Apply(…) in …/CardEffectSpecRegistry.cs:line 221\n'
            '            // It named this file and this exact call, and it was read as background noise\n'
            "            // because it appeared on both sides of a pass/fail run. That is R5b's lesson in a\n"
            '            // second costume: a diagnostic that is always present is a diagnostic nobody reads.\n'
            '            // Before trusting "the tests are green", read the lines that are not PASS.\n'
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
            '                break;\n'
            '            case Outrage:\n')),
        ((            '        [typeof(CrushUnder)]'), (            '        // CoopBots multiplayer port. CONCOCT is multiplayer-only (AnyAlly): it gives an ally\n'
            '        // "your powered attacks apply N Poison". ConcoctPower\'s own TRIGGER was already\n'
            "        // mirrored (AfterDamageGivenMirrors), but the card's OnPlay was not, so the power was\n"
            '        // never applied in the simulation and everything downstream diverged. Measured live\n'
            '        // 2026-09-20 (CUBEX_CONSTRUCT_NORMAL): `risk_first=CONCOCT.OnPlay:MethodNotMirrored`,\n'
            "        // a logged hand drift (live hand 6 cards vs the plan's 5, the extra being a generated\n"
            '        // AFTERIMAGE), and the turn-boundary check then correctly refused a 90-action route.\n'
            '        [typeof(Concoct)] = [Target<ConcoctPower>("ConcoctPower")],\n'
            '        // The other one-turn AnyAlly cards were left on the structural fallback, which the\n'
            '        // kernel still records as an uncompensated OnPlay boundary. That made them\n'
            '        // unplayable in a plan and hid their timing from the tournament: Fade is temporary\n'
            "        // Dexterity (before the ally's block cards) and Coordinate is temporary Strength\n"
            "        // (before the ally's attacks). Both powers already have lifecycle support.\n"
            '        [typeof(Fade)] = [Target<FadePower>(card => card.DynamicVars.Dexterity.IntValue)],\n'
            '        [typeof(Coordinate)] = [Target<CoordinatePower>(card => card.DynamicVars.Strength.IntValue)],\n'
            '        // CoopBots multiplayer port. BLAZE is the same shape as CONCOCT — Skill, AnyAlly, one\n'
            '        // declared PowerVar — so it gets the same treatment: grant the declared power to the\n'
            '        // chosen ally. It used to be SKIPPED as an unmirrored ally buff, and the live A10 4-bot\n'
            '        // round measured what that costs (2026-09-20, CEREMONIAL_BEAST_BOSS):\n'
            '        //   boundaries=BLAZE:prediction-risk:{SourceId=BLAZE, Method=OnPlay, Reason=MethodNotMirrored}x1272\n'
            '        // — the single most frequent boundary in the fight, i.e. a card the bot tried to play over\n'
            '        // and over and could not. With Blaze modelled it is a real +5 Strength to a teammate.\n'
            '        [typeof(Blaze)] = [Target<StrengthPower>("StrengthPower")],\n'
            '        [typeof(CrushUnder)]')),
        ((            '        else if (powerType == typeof(SetupStrikePower))'), (            '        else if (powerType == typeof(CoordinatePower))\n'
            '            combat.ApplyTemporaryStrengthGain<CoordinatePower>(target, amount, applier);\n'
            '        else if (powerType == typeof(FadePower))\n'
            '            combat.ApplyTemporaryDexterity<FadePower>(target, amount, applier);\n'
            '        else if (powerType == typeof(SetupStrikePower))')),
    ]
    for anchor, replacement in replacements:
        if text.count(anchor) != 1:
            raise RuntimeError('Verified registry fix anchor changed: ' + anchor[:80])
        text = text.replace(anchor, replacement)
    return text

manifest=[]
for path in sorted(files):
    relative=path.relative_to(source)
    text=path.read_text(encoding='utf-8-sig')
    adapted=text.replace('CombatSolver.', 'CoopBots.Kernel.Vendor.').replace('namespace CombatSolver;', 'namespace CoopBots.Kernel.Vendor;')
    # The snapshot fingerprint belongs to the UPSTREAM text, so it is taken here,
    # before the CoopBots hooks below touch it. Hashing `adapted` further down would
    # make the recorded value depend on our own patches, so the re-anchor tool that
    # regenerates this table could never reproduce it.
    snapshot_sha256=hashlib.sha256(adapted.encode()).hexdigest()
    if relative.as_posix() == 'Search/SimulatedCombatState.cs':
        original='PlatformUtil.GetPlayerName(RunManager.Instance.NetService.Platform, player.NetId)'
        if adapted.count(original) != 1:
            raise RuntimeError('Upstream player-name capture changed; review the kernel adaptation.')
        adapted=adapted.replace(original, 'player.NetId.ToString(System.Globalization.CultureInfo.InvariantCulture)')
    if relative.as_posix() == 'Runtime/ContinuationStamp.cs':
        adapted=make_stamp_fields_positionally_stable(adapted)
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
    if relative.as_posix() in ('Prediction/PersistentPowerSupport.cs', 'Search/SimulatedCombatState.cs'):
        adapted=add_rampart_once_per_round(relative.as_posix(), adapted)
    if relative.as_posix() == 'Prediction/EndTurnPowerSupport.cs':
        adapted=add_team_multiplier_lifetime(adapted)
    if relative.as_posix() == 'Engine/InCombat/Mirrors/Hooks/Card/BeforeCardPlayedMirrors.cs':
        adapted=make_music_box_forkable(adapted)
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
        if snapshot_sha256 != patch['base_sha256']:
            raise RuntimeError(f'P1 adaptation base changed: {relative}; review before importing.')
        for change in reversed(patch['changes']):
            start, end = change['start'], change['end']
            if adapted[start:end] != change['old']:
                raise RuntimeError(f'P1 adaptation context mismatch: {relative}')
            adapted = adapted[:start] + change['new'] + adapted[end:]
    if relative.as_posix() == 'Prediction/CardEffectSpecRegistry.cs':
        adapted = preserve_verified_registry_fixes(adapted)
    adapted = apply_class_power_adaptations(relative.as_posix(), adapted)
    adapted = apply_relic_choice_adaptations(relative.as_posix(), adapted)
    adapted = apply_decision_consistency_adaptations(relative.as_posix(), adapted)
    adapted = apply_frozen_hook_adaptations(relative.as_posix(), adapted)
    adapted = apply_temporary_stat_adaptations(relative.as_posix(), adapted)
    adapted = apply_latest_model_adaptations(relative.as_posix(), adapted)
    adapted = apply_latest_choice_adaptations(relative.as_posix(), adapted)
    adapted = apply_retained_support_adaptations(relative.as_posix(), adapted)
    adapted = apply_szg_model_adaptations(relative.as_posix(), adapted)
    adapted = apply_risk_prefix_coverage_adaptations(relative.as_posix(), adapted)
    adapted = apply_orichalcum_capture_adaptations(relative.as_posix(), adapted)
    adapted = apply_history_capture_adaptations(relative.as_posix(), adapted)
    adapted = apply_power_origin_adaptations(relative.as_posix(), adapted)
    adapted = apply_the_ball_growth(relative.as_posix(), adapted)
    adapted = apply_attack_pair_adaptations(relative.as_posix(), adapted)
    target=dest/relative; target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(adapted,encoding='utf-8')
    manifest.append({'path':relative.as_posix(),'upstream_sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
manifest.extend(write_power_fork_support(dest))
manifest.extend(write_szg_party_support(dest))
sessions=source/'Runtime/SolverControllerSessions.cs'
session_text=sessions.read_text(encoding='utf-8-sig')
progress=session_text[session_text.index('internal sealed class SearchProgressDisplayState'):session_text.index('internal sealed class SolverCombatSession')]
(dest/'Runtime/SearchProgressDisplayState.cs').write_text('namespace CoopBots.Kernel.Vendor;\n'+progress,encoding='utf-8')
manifest.append({'path':'Runtime/SearchProgressDisplayState.cs','upstream':'Runtime/SolverControllerSessions.cs:SearchProgressDisplayState','upstream_sha256':hashlib.sha256(sessions.read_bytes()).hexdigest()})
(dest.parent/'UPSTREAM.json').write_text(json.dumps({'source':'CombatSolver 0.33.9','namespace':'CoopBots.Kernel.Vendor','files':manifest},indent=2),encoding='utf-8')
print(f'Imported {len(files)} source files; no upstream startup/UI/test runner.')

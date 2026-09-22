using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using CoopBots.Kernel.Vendor.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors;

namespace CoopBots.Kernel.Vendor;

internal enum CardEffectTarget
{
    Owner,
    Target,
    AllEnemies,
}

internal sealed record CardPowerEffect(
    Type PowerType,
    CardEffectTarget Target,
    Func<CardModel, int> Amount);

/// <summary>
/// Parameterized completion for common deterministic OnPlay effects. Identity is still explicit,
/// while execution, targeting, artifact handling and Power lifecycle use one shared implementation.
/// </summary>
internal static class CardEffectSpecRegistry
{
    private static readonly Dictionary<Type, CardPowerEffect[]> PowerEffects = new()
    {
        [typeof(Blur)] = [Owner<BlurPower>("Blur")],
        [typeof(ChargeBattery)] = [Owner<EnergyNextTurnPower>(card => card.DynamicVars.Energy.IntValue)],
        [typeof(Colossus)] = [Owner<ColossusPower>("Colossus")],
        // CoopBots multiplayer port. CONCOCT is multiplayer-only (AnyAlly): it gives an ally
        // "your powered attacks apply N Poison". ConcoctPower's own TRIGGER was already
        // mirrored (AfterDamageGivenMirrors), but the card's OnPlay was not, so the power was
        // never applied in the simulation and everything downstream diverged. Measured live
        // 2026-09-20 (CUBEX_CONSTRUCT_NORMAL): `risk_first=CONCOCT.OnPlay:MethodNotMirrored`,
        // a logged hand drift (live hand 6 cards vs the plan's 5, the extra being a generated
        // AFTERIMAGE), and the turn-boundary check then correctly refused a 90-action route.
        [typeof(Concoct)] = [Target<ConcoctPower>("ConcoctPower")],
        // The other one-turn AnyAlly cards were left on the structural fallback, which the
        // kernel still records as an uncompensated OnPlay boundary. That made them
        // unplayable in a plan and hid their timing from the tournament: Fade is temporary
        // Dexterity (before the ally's block cards) and Coordinate is temporary Strength
        // (before the ally's attacks). Both powers already have lifecycle support.
        [typeof(Fade)] = [Target<FadePower>(card => card.DynamicVars.Dexterity.IntValue)],
        [typeof(Coordinate)] = [Target<CoordinatePower>(card => card.DynamicVars.Strength.IntValue)],
        // CoopBots multiplayer port. BLAZE is the same shape as CONCOCT — Skill, AnyAlly, one
        // declared PowerVar — so it gets the same treatment: grant the declared power to the
        // chosen ally. It used to be SKIPPED as an unmirrored ally buff, and the live A10 4-bot
        // round measured what that costs (2026-09-20, CEREMONIAL_BEAST_BOSS):
        //   boundaries=BLAZE:prediction-risk:{SourceId=BLAZE, Method=OnPlay, Reason=MethodNotMirrored}x1272
        // — the single most frequent boundary in the fight, i.e. a card the bot tried to play over
        // and over and could not. With Blaze modelled it is a real +5 Strength to a teammate.
        [typeof(Blaze)] = [Target<StrengthPower>("StrengthPower")],
        [typeof(CrushUnder)] = [AllEnemies<CrushUnderPower>("StrengthLoss")],
        [typeof(Debilitate)] = [Target<DebilitatePower>("DebilitatePower")],
        [typeof(Defy)] = [Target<WeakPower>(card => card.DynamicVars.Weak.IntValue)],
        [typeof(Delay)] = [Owner<EnergyNextTurnPower>(card => card.DynamicVars.Energy.IntValue)],
        [typeof(DyingStar)] = [AllEnemies<DyingStarPower>("StrengthLoss")],
        [typeof(Equilibrium)] = [Owner<RetainHandPower>("Equilibrium")],
        [typeof(FlameBarrier)] = [Owner<FlameBarrierPower>("DamageBack")],
        [typeof(FocusedStrike)] = [Owner<FocusedStrikePower>("FocusPower")],
        [typeof(Glow)] = [Owner<DrawCardsNextTurnPower>(card => card.DynamicVars.Cards.IntValue)],
        [typeof(GuidingStar)] = [Owner<DrawCardsNextTurnPower>(card => card.DynamicVars.Cards.IntValue)],
        [typeof(Hegemony)] = [Owner<EnergyNextTurnPower>(card => card.DynamicVars.Energy.IntValue)],
        [typeof(Hyperbeam)] = [Owner<HyperbeamFocusDownPower>("FocusPower")],
        [typeof(Knockdown)] = [Target<KnockdownPower>("KnockdownPower")],
        // CoopBots multiplayer port: FLANKING declares no variable; each
        // application is one stack and the multiplier lives in the power hook.
        [typeof(Flanking)] = [Target<FlankingPower>(_ => 1)],
        [typeof(LightningRod)] = [Owner<LightningRodPower>("LightningRodPower")],
        [typeof(Mangle)] = [Target<ManglePower>("StrengthLoss")],
        [typeof(NegativePulse)] = [AllEnemies<DoomPower>(card => card.DynamicVars.Doom.IntValue)],
        [typeof(PanicButton)] = [Owner<NoBlockPower>("Turns")],
        [typeof(Patter)] = [Owner<VigorPower>("VigorPower")],
        [typeof(Pounce)] = [Owner<FreeSkillPower>(_ => 1)],
        [typeof(Predator)] = [Owner<DrawCardsNextTurnPower>(_ => 2)],
        [typeof(Rebound)] = [Owner<ReboundPower>(_ => 1)],
        [typeof(Reflect)] = [Owner<ReflectPower>(_ => 1)],
        [typeof(Relax)] =
        [
            Owner<DrawCardsNextTurnPower>(card => card.DynamicVars.Cards.IntValue),
            Owner<EnergyNextTurnPower>(card => card.DynamicVars.Energy.IntValue),
        ],
        [typeof(Salvo)] = [Owner<RetainHandPower>(_ => 1)],
        // CoopBots multiplayer port: power hooks already exist in this snapshot
        // (AfterCardPlayed/AfterBlockGained), only the OnPlay application was missing.
        [typeof(Sneaky)] = [Owner<SneakyPower>("SneakyPower")],
        [typeof(BeaconOfHope)] = [Owner<BeaconOfHopePower>(_ => 1)],
        [typeof(Scourge)] = [Target<DoomPower>(card => card.DynamicVars.Doom.IntValue)],
        [typeof(SetupStrike)] = [Owner<SetupStrikePower>(card => card.DynamicVars.Strength.IntValue)],
        [typeof(SicEm)] = [Target<SicEmPower>("SicEmPower")],
        [typeof(Strangle)] = [Target<StranglePower>("StranglePower")],
        [typeof(Synthesis)] = [Owner<FreePowerPower>(_ => 1)],
        [typeof(TagTeam)] = [Target<TagTeamPower>(_ => 1)],
        [typeof(TheGambit)] = [Owner<TheGambitPower>(_ => 1)],
        [typeof(Unrelenting)] = [Owner<FreeAttackPower>(_ => 1)],
        [typeof(Veilpiercer)] = [Owner<VeilpiercerPower>(_ => 1)],
        // CoopBots multiplayer port: the power hooks already exist in this
        // snapshot (AfterCardDrawn / FrostOrb / AfterCardGeneratedForCombat /
        // AfterDamageGiven), so only the OnPlay application was missing.
        [typeof(Cacophony)] = [Owner<CacophonyPower>(card => card.DynamicVars.Cards.IntValue)],
        [typeof(Hibernate)] = [Owner<HibernatePower>(_ => 1)],
        [typeof(Soulbound)] = [Target<SoulboundPower>(_ => 1)],
        [typeof(Underworld)] = [Owner<UnderworldPower>(_ => 1)],
    };

    private static readonly HashSet<Type> ResourceEffects =
    [
        typeof(BigBang), typeof(BloodWall), typeof(Breakthrough), typeof(BrightestFlame), typeof(GatherLight),
        // CoopBots multiplayer port: instant team/target energy, no power lifecycle needed.
        typeof(EnergySurge), typeof(BelieveInYou),
        // CoopBots multiplayer port: team-wide next-turn draw and the
        // HP-for-ally-block trade; both are handled in the switch.
        typeof(Plot), typeof(DemonicShield),
        typeof(Glow), typeof(Hemokinesis), typeof(ShiningStrike), typeof(SolarStrike),
        typeof(AllForOne), typeof(BoneShards), typeof(Bulwark), typeof(Claw), typeof(Compact),
        typeof(DeathsDoor), typeof(EvilEye), typeof(GeneticAlgorithm), typeof(Glitterstream), typeof(GoForTheEyes),
        typeof(Misery), typeof(Modded), typeof(MoltenFist), typeof(MomentumStrike), typeof(PullAggro),
        typeof(Rampage), typeof(Whistle), typeof(WroughtInWar),
    ];

    private static readonly HashSet<Type> GenerationEffects =
    [
        typeof(AdaptiveStrike), typeof(BoostAway), typeof(CollisionCourse), typeof(CrashLanding),
        typeof(FightThrough), typeof(GraveWarden), typeof(GunkUp), typeof(Overclock), typeof(Reave),
        typeof(Severance), typeof(Undeath),
        // CoopBots multiplayer port: copies and gifts that reach EVERY player.
        typeof(Outrage), typeof(BladeSymphony), typeof(GlimpseBeyond), typeof(LegionOfBone),
    ];

    public static IReadOnlyCollection<Type> SupportedTypes
        => PowerEffects.Keys.Concat(ResourceEffects).Concat(GenerationEffects).Distinct().ToArray();

    public static IReadOnlyDictionary<Type, string> EvidenceByType
    {
        get
        {
            Dictionary<Type, string> result = SupportedTypes.ToDictionary(
                type => type,
                _ => "CARD-EFFECT-SPEC-BATCH-137");
            foreach (Type type in GenerationEffects)
                result[type] = "CARD-GENERATION-SPEC-BATCH-138";
            Type[] completionTypes =
            [
                typeof(AllForOne), typeof(BoneShards), typeof(Bulwark), typeof(Claw), typeof(Compact),
                typeof(DeathsDoor), typeof(EvilEye), typeof(GeneticAlgorithm), typeof(Glitterstream),
                typeof(GoForTheEyes), typeof(Misery), typeof(Modded), typeof(MoltenFist),
                typeof(MomentumStrike), typeof(PullAggro), typeof(Rampage), typeof(SicEm),
                typeof(Whistle), typeof(WroughtInWar),
            ];
            foreach (Type type in completionTypes)
                result[type] = "CARD-COMPLETION-BATCH-123";
            return result;
        }
    }

    public static bool Contains(CardModel card)
        => PowerEffects.ContainsKey(card.GetType())
            || ResourceEffects.Contains(card.GetType())
            || GenerationEffects.Contains(card.GetType());

    public static bool Apply(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard,
        Creature? target)
    {
        CardModel card = playedCard.Preview;
        Creature ownerCreature = playedCard.Preview.Owner.Creature;
        bool applied = false;
        if (PowerEffects.TryGetValue(card.GetType(), out CardPowerEffect[]? effects))
        {
            applied = true;
            foreach (CardPowerEffect effect in effects)
            {
                int amount = effect.Amount(card);
                Creature owner = ownerCreature;
                switch (effect.Target)
                {
                    case CardEffectTarget.Owner:
                        ApplyPower(combat, effect.PowerType, owner, amount, owner);
                        break;
                    case CardEffectTarget.Target:
                    {
                        Creature effectTarget = target
                            ?? throw new InvalidOperationException($"{card.Id} requires a target.");
                        ApplyPower(
                            combat,
                            effect.PowerType,
                            effectTarget,
                            amount,
                            owner);
                        // CoopBots multiplayer port: SimulatedCombatState.ApplyPower
                        // records the Applier from its own player-name table. The
                        // live platform lookup that used to run here threw outside a
                        // Steam host and disagreed with the simulated state.
                        break;
                    }
                    case CardEffectTarget.AllEnemies:
                    {
                        foreach (Creature enemy in combat.HittableEnemies)
                        {
                            ApplyPower(combat, effect.PowerType, enemy, amount, owner);
                            if (simulator.HasPendingChoice)
                                return true;
                        }
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(effect.Target), effect.Target, null);
                }
                if (simulator.HasPendingChoice)
                    return true;
            }
        }

        switch (card)
        {
            case AllForOne:
            {
                SimPlayerCombatState ownerState = simulator.State.GetPlayerCombatState(card.Owner);
                PredictedCard[] cards = ownerState.DiscardPile.Cards
                    .Where(candidate => !candidate.Preview.EnergyCost.CostsX
                        && candidate.GetEnergyCostWithModifiers(simulator, ownerState) == 0
                        && candidate.Preview.Type is CardType.Attack or CardType.Skill or CardType.Power)
                    .ToArray();
                simulator.AddToPile(cards, PileType.Hand);
                applied = true;
                break;
            }
            // CoopBots multiplayer port: ENERGY_SURGE pays every living teammate and
            // BELIEVE_IN_YOU pays the chosen ally. Upstream models only the caster,
            // which is the same card in single player.
            //
            // This case label used to be MISSING. Without it AllForOne fell through into
            // EnergySurge's body while AllForOne's own body sat below as an unlabelled block —
            // unreachable, and flagged by `warning CS0162: unreachable code`.
            //
            // The verified consequence is that ALL_FOR_ONE was UNUSABLE, not mispriced:
            // AllForOne carries only a DamageVar, so EnergySurge's body evaluated
            // `card.DynamicVars.Energy` and threw KeyNotFoundException out of this method. The
            // search records that as a prediction exception, so the card could never appear in
            // a plan. The evidence had been printed on EVERY kernel-suite run, all session:
            //   COVERAGE BROKEN: ALL_FOR_ONE:prediction-exception:KeyNotFoundException:
            //   The given key 'Energy' was not present in the dictionary.
            //   @ …CardEffectSpecRegistry.Apply(…) in …/CardEffectSpecRegistry.cs:line 221
            // It named this file and this exact call, and it was read as background noise
            // because it appeared on both sides of a pass/fail run. That is R5b's lesson in a
            // second costume: a diagnostic that is always present is a diagnostic nobody reads.
            // Before trusting "the tests are green", read the lines that are not PASS.
            case EnergySurge:
                // Every living teammate, matching the native GetTeammatesOf loop.
                foreach (Creature ally in combat.GetTeammatesOf(ownerCreature))
                {
                    if (!ally.IsPlayer || !ally.IsAlive || ally.Player is not { } allyPlayer)
                        continue;
                    simulator.GainEnergy(allyPlayer, card.DynamicVars.Energy.IntValue);
                    if (simulator.HasPendingChoice)
                        return true;
                }
                applied = true;
                break;
            case BelieveInYou when target?.Player is { } believeInYouTarget:
                simulator.GainEnergy(believeInYouTarget, card.DynamicVars.Energy.IntValue);
                applied = true;
                break;
            case Outrage:
                // One copy per player, the caster included.
                foreach (MegaCrit.Sts2.Core.Entities.Players.Player recipient in combat.Players)
                {
                    simulator.CreateAndAddGeneratedCardsToCombat<Outrage>(
                        recipient, PileType.Discard, 1, card.Owner);
                    if (simulator.HasPendingChoice)
                        return true;
                }
                applied = true;
                break;
            case BladeSymphony:
                foreach (MegaCrit.Sts2.Core.Entities.Players.Player recipient in combat.Players)
                {
                    simulator.CreateAndAddGeneratedCardsToCombat<Shiv>(
                        recipient, PileType.Hand, card.DynamicVars.Cards.IntValue, card.Owner);
                    if (simulator.HasPendingChoice)
                        return true;
                }
                applied = true;
                break;
            case GlimpseBeyond:
                foreach (MegaCrit.Sts2.Core.Entities.Players.Player recipient in combat.Players)
                {
                    simulator.CreateAndAddGeneratedCardsToCombat<Soul>(
                        recipient, PileType.Draw, card.DynamicVars.Cards.IntValue, card.Owner);
                    if (simulator.HasPendingChoice)
                        return true;
                }
                applied = true;
                break;
            case LegionOfBone when combat is ICombatPredictionEffectSink summonSink:
                foreach (MegaCrit.Sts2.Core.Entities.Players.Player recipient in combat.Players)
                {
                    summonSink.SummonOsty(simulator, recipient, card.DynamicVars["Summon"].IntValue);
                    if (simulator.HasPendingChoice)
                        return true;
                }
                applied = true;
                break;
            case Plot:
                foreach (MegaCrit.Sts2.Core.Entities.Players.Player recipient in combat.Players)
                    ApplyPower(combat, typeof(DrawCardsNextTurnPower), recipient.Creature,
                        card.DynamicVars.Cards.IntValue, ownerCreature);
                applied = true;
                break;
            case DemonicShield when target is { IsPlayer: true }:
                simulator.Damage(card.Owner.Creature, card.DynamicVars.HpLoss.IntValue,
                    ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move, card.Owner.Creature);
                if (simulator.HasPendingChoice)
                    return true;
                simulator.GainBlock(target, card.DynamicVars["CalculatedBlock"].IntValue, ValueProp.Unpowered);
                applied = true;
                break;
            case BigBang:
                simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue);
                if (!simulator.GainStars(card.Owner, card.DynamicVars.Stars.IntValue))
                    return true;
                PersistentPowerSupport.Forge(simulator, card.Owner, card.DynamicVars.Forge.IntValue);
                applied = true;
                break;
            case BloodWall or Breakthrough or Hemokinesis:
                simulator.Damage(
                    card.Owner.Creature,
                    card.DynamicVars.HpLoss.IntValue,
                    ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move,
                    card.Owner.Creature);
                applied = true;
                break;
            case BrightestFlame:
            {
                combat.RecordBrightestFlameMaxHpLoss(card.DynamicVars.MaxHp.IntValue);
                simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue);
                SimCreatureState ownerState = simulator.State.GetCreature(card.Owner.Creature);
                int newMaxHp = Math.Max(1, ownerState.MaxHp - card.DynamicVars.MaxHp.IntValue);
                if (ownerState.CurrentHp > newMaxHp)
                {
                    simulator.Damage(
                        card.Owner.Creature,
                        ownerState.CurrentHp - newMaxHp,
                        ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move,
                        null);
                    if (simulator.HasPendingChoice)
                        return true;
                }
                ownerState.SetMaxHp(newMaxHp);
                applied = true;
                break;
            }
            case GatherLight:
                simulator.GainStars(card.Owner, card.DynamicVars.Stars.IntValue);
                applied = true;
                break;
            case Glow:
                simulator.GainStars(card.Owner, card.DynamicVars.Stars.IntValue);
                applied = true;
                break;
            case ShiningStrike or SolarStrike:
                simulator.GainStars(card.Owner, card.DynamicVars.Stars.IntValue);
                applied = true;
                break;
            case BoneShards:
                if (simulator.State.GetOsty(card.Owner) is { } osty
                    && simulator.State.GetCreature(osty).IsAlive)
                {
                    if (!simulator.Kill(osty, force: true))
                        return true;
                }
                applied = true;
                break;
            case Bulwark:
                PersistentPowerSupport.Forge(simulator, card.Owner, card.DynamicVars.Forge.IntValue);
                applied = true;
                break;
            case Compact:
            {
                PredictedCard[] statuses = simulator.State.GetPlayerCombatState(card.Owner).Hand.Cards
                    .Where(candidate => candidate.Preview.IsTransformable && candidate.Preview.Type == CardType.Status)
                    .ToArray();
                CardChoiceSupport.TransformCards(
                    simulator,
                    statuses,
                    CanonicalModels.Card<Fuel>(),
                    card.IsUpgraded);
                applied = true;
                break;
            }
            case Claw claw:
            {
                decimal increase = claw.DynamicVars["Increase"].BaseValue;
                foreach (PredictedCard candidate in simulator.State.GetPlayerCombatState(card.Owner).AllCards)
                {
                    if (candidate.Preview is Claw)
                        ((Claw)candidate.MutablePreview).BuffFromClawPlay(increase);
                }
                applied = true;
                break;
            }
            case DeathsDoor when combat.WasDoomAppliedThisTurn(ownerCreature):
                for (int index = 0; index < card.DynamicVars.Repeat.IntValue; index++)
                {
                    simulator.GainBlock(ownerCreature, card.DynamicVars.Block, playedCard, null);
                    if (simulator.HasPendingChoice)
                        return true;
                }
                applied = true;
                break;
            case EvilEye when combat.WasCardExhaustedThisTurn(ownerCreature):
                simulator.GainBlock(ownerCreature, card.DynamicVars.Block, playedCard, null);
                applied = true;
                break;
            case GeneticAlgorithm geneticAlgorithm:
            {
                int increase = geneticAlgorithm.DynamicVars["Increase"].IntValue;
                ((GeneticAlgorithm)playedCard.MutablePreview).BuffFromPlay(increase);
                if (playedCard.MutablePreview.DeckVersion != null)
                {
                    combat.RecordLongTermResource(increase);
                    combat.RecordGrowthReward(GrowthSource.GeneticAlgorithm);
                }
                applied = true;
                break;
            }
            case Glitterstream:
            {
                BlockVar nextTurn = (BlockVar)card.DynamicVars["BlockNextTurn"];
                decimal amount = HookMirrors.ModifyBlock(
                    simulator,
                    ownerCreature,
                    nextTurn.BaseValue,
                    nextTurn.Props,
                    playedCard,
                    null,
                    out _);
                combat.Apply<BlockNextTurnPower>(ownerCreature, (int)amount, ownerCreature);
                applied = true;
                break;
            }
            case GoForTheEyes when target != null && combat.IsEnemyIntendingToAttack(target):
                combat.Apply<WeakPower>(target, card.DynamicVars.Weak.IntValue, ownerCreature);
                applied = true;
                break;
            case Misery when target != null:
                SpreadDebuffs(combat, target);
                applied = true;
                break;
            case Modded:
                simulator.AddOrbSlots(card.Owner, card.DynamicVars.Repeat.IntValue);
                playedCard.MutablePreview.EnergyCost.AddThisCombat(1);
                applied = true;
                break;
            case MoltenFist when target != null && simulator.State.GetCreature(target).IsAlive:
            {
                int vulnerable = combat.GetAmount<VulnerablePower>(target);
                if (vulnerable > 0)
                    combat.Apply<VulnerablePower>(target, vulnerable, ownerCreature);
                applied = true;
                break;
            }
            case MomentumStrike:
                playedCard.MutablePreview.EnergyCost.SetThisCombat(0);
                applied = true;
                break;
            case PullAggro:
                combat.SummonOsty(simulator, card.Owner, card.DynamicVars.Summon.IntValue);
                applied = true;
                break;
            case Rampage rampage:
            {
                decimal increase = rampage.DynamicVars["Increase"].BaseValue;
                Rampage mutableRampage = (Rampage)playedCard.MutablePreview;
                mutableRampage.DynamicVars.Damage.BaseValue += increase;
                mutableRampage.ExtraDamageFromPlays += increase;
                applied = true;
                break;
            }
            case WroughtInWar:
                PersistentPowerSupport.Forge(simulator, card.Owner, card.DynamicVars.Forge.IntValue);
                applied = true;
                break;
            case Whistle:
                combat.ForceStunnedMove(target
                    ?? throw new InvalidOperationException($"{card.Id} requires a target."));
                applied = true;
                break;
        }
        if (simulator.HasPendingChoice)
            return true;
        switch (card)
        {
            case AdaptiveStrike:
            {
                PredictedCard copy = playedCard.CreateClone();
                copy.MutablePreview.EnergyCost.SetThisCombat(0);
                simulator.AddGeneratedCardToCombat(
                    copy,
                    PileType.Discard,
                    card.Owner,
                    resultKind: CardGenerationResultKind.Fixed);
                applied = true;
                break;
            }
            case BoostAway:
                AddFixed<Dazed>(simulator, card, PileType.Discard, 1);
                applied = true;
                break;
            case CollisionCourse:
                AddFixed<Debris>(simulator, card, PileType.Hand, 1);
                applied = true;
                break;
            case CrashLanding:
            {
                int count = simulator.GetMaxHandSize(card.Owner)
                    - simulator.State.GetPlayerCombatState(card.Owner).Hand.Cards.Count;
                AddFixed<Debris>(simulator, card, PileType.Hand, count);
                applied = true;
                break;
            }
            case FightThrough:
                AddFixed<Wound>(simulator, card, PileType.Discard, 2);
                applied = true;
                break;
            case GraveWarden:
                AddFixed<Soul>(
                    simulator,
                    card,
                    PileType.Draw,
                    card.DynamicVars.Cards.IntValue,
                    CardPilePosition.Random);
                applied = true;
                break;
            case GunkUp:
                AddFixed<Slimed>(simulator, card, PileType.Discard, 1);
                applied = true;
                break;
            case Overclock:
                AddFixed<Burn>(simulator, card, PileType.Discard, 1);
                applied = true;
                break;
            case Reave:
            {
                int count = card.DynamicVars.Cards.IntValue;
                List<PredictedCard> souls = new(count);
                for (int index = 0; index < count; index++)
                {
                    PredictedCard soul = PredictedCard.Create(CanonicalModels.Card<Soul>(), card.Owner);
                    if (card.IsUpgraded)
                        soul.Upgrade();
                    souls.Add(soul);
                }
                simulator.AddGeneratedCardsToCombat(
                    souls,
                    PileType.Draw,
                    card.Owner,
                    CardPilePosition.Random,
                    CardGenerationResultKind.Fixed);
                applied = true;
                break;
            }
            case Severance:
            {
                AddFixed<Soul>(simulator, card, PileType.Draw, 1, CardPilePosition.Random);
                if (simulator.HasPendingChoice)
                    return true;
                AddFixed<Soul>(simulator, card, PileType.Discard, 1);
                if (simulator.HasPendingChoice)
                    return true;
                AddFixed<Soul>(simulator, card, PileType.Hand, 1);
                applied = true;
                break;
            }
            case Undeath:
                simulator.AddGeneratedCardToCombat(
                    playedCard.CreateClone(),
                    PileType.Discard,
                    card.Owner,
                    resultKind: CardGenerationResultKind.Fixed);
                applied = true;
                break;
        }
        return applied;
    }

    private static void SpreadDebuffs(SimulatedCombatState combat, Creature source)
    {
        Dictionary<Type, (int Amount, Creature? Applier)> debuffs = combat.EffectivePowers()
            .Where(power => power.Owner == source
                && power.TypeForCurrentAmount == PowerType.Debuff)
            .GroupBy(power => power.GetType())
            .ToDictionary(
                group => group.Key,
                group => (group.Sum(power => power.Amount), group.First().Applier));
        foreach (PowerModel power in combat.EffectivePowers().Where(power => power.Owner == source))
        {
            if (power is not ITemporaryPower temporary
                || !debuffs.TryGetValue(temporary.InternallyAppliedPower.GetType(), out var internalEffect))
            {
                continue;
            }
            debuffs[temporary.InternallyAppliedPower.GetType()] =
                (internalEffect.Amount + power.Amount, internalEffect.Applier);
        }
        foreach (Creature enemy in combat.HittableEnemies.Where(enemy => enemy != source))
        {
            foreach ((Type type, (int amount, Creature? applier)) in debuffs)
                combat.ApplyPower(type, enemy, amount, applier);
        }
    }

    private static void AddFixed<TCard>(
        CombatPredictionSimulator simulator,
        CardModel source,
        PileType pile,
        int count,
        CardPilePosition position = CardPilePosition.Bottom)
        where TCard : CardModel
    {
        if (count <= 0)
            return;
        simulator.CreateAndAddGeneratedCardsToCombat<TCard>(
            source.Owner,
            pile,
            count,
            source.Owner,
            position);
    }

    private static void ApplyPower(
        SimulatedCombatState combat,
        Type powerType,
        Creature target,
        int amount,
        Creature applier)
    {
        if (powerType == typeof(CrushUnderPower))
            combat.ApplyTemporaryStrengthLoss<CrushUnderPower>(target, amount, applier);
        else if (powerType == typeof(DyingStarPower))
            combat.ApplyTemporaryStrengthLoss<DyingStarPower>(target, amount, applier);
        else if (powerType == typeof(ManglePower))
            combat.ApplyTemporaryStrengthLoss<ManglePower>(target, amount, applier);
        else if (powerType == typeof(CoordinatePower))
            combat.ApplyTemporaryStrengthGain<CoordinatePower>(target, amount, applier);
        else if (powerType == typeof(FadePower))
            combat.ApplyTemporaryDexterity<FadePower>(target, amount, applier);
        else if (powerType == typeof(SetupStrikePower))
            combat.ApplyTemporaryStrengthGain<SetupStrikePower>(target, amount, applier);
        else if (powerType == typeof(FocusedStrikePower))
            combat.ApplyTemporaryFocus<FocusedStrikePower>(target, amount, applier);
        else if (powerType == typeof(HyperbeamFocusDownPower))
            combat.ApplyTemporaryFocusLoss<HyperbeamFocusDownPower>(target, amount, applier);
        else
            combat.ApplyPower(powerType, target, amount, applier);
    }

    private static CardPowerEffect Owner<TPower>(string dynamicVar)
        where TPower : PowerModel
        => Owner<TPower>(card => card.DynamicVars[dynamicVar].IntValue);

    private static CardPowerEffect Owner<TPower>(Func<CardModel, int> amount)
        where TPower : PowerModel
        => new(typeof(TPower), CardEffectTarget.Owner, amount);

    private static CardPowerEffect Target<TPower>(string dynamicVar)
        where TPower : PowerModel
        => Target<TPower>(card => card.DynamicVars[dynamicVar].IntValue);

    private static CardPowerEffect Target<TPower>(Func<CardModel, int> amount)
        where TPower : PowerModel
        => new(typeof(TPower), CardEffectTarget.Target, amount);

    private static CardPowerEffect AllEnemies<TPower>(string dynamicVar)
        where TPower : PowerModel
        => AllEnemies<TPower>(card => card.DynamicVars[dynamicVar].IntValue);

    private static CardPowerEffect AllEnemies<TPower>(Func<CardModel, int> amount)
        where TPower : PowerModel
        => new(typeof(TPower), CardEffectTarget.AllEnemies, amount);
}

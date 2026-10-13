using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Attack;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Card;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Damage;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    // Upstream's continuation text covers visible fields and selected private
    // fields, but cost-expiration sequences and all StateStore payloads are not
    // in that text. Multiplayer cache reuse records those captured facts too.
    // Owner's 2026-10-09 architecture plan: unavailable facts prevent reuse only;
    // they never refuse a native action or substitute a numeric hash for evidence.
    internal bool AppendPlanningMechanismFacts(StringBuilder text)
    {
        try { return new PlanningMechanismWriter(this, text).Append(); }
        catch (Exception error) when (error is InvalidOperationException or MissingMemberException
            or ArgumentException or TargetInvocationException or InvalidCastException)
        {
            text.Append(";planning-facts-unavailable=").Append(error.GetType().Name);
            return false;
        }
    }

    // These nine receipts count damage/recovery and resources since this
    // search root. Native CombatState owns none, and a paid settled action
    // recaptured into a new root starts them empty. Preserve their exact values
    // for search/diagnostics without calling them native future-driving state.
    internal string PlanningSearchMetadata()
    {
        var text = new StringBuilder("search-metadata-v=1;origin=route-relative");
        new PlanningMechanismWriter(this, text).AppendSearchMetadata();
        return text.ToString();
    }

    // MaterializeRoot has already captured both slots. These getters read
    // only owned branch data; detached historical cards retain native identity
    // and frozen preview rather than borrowing later live card fields.
    internal (Player Player, PredictedCard? Current, PredictedCard? Previous)[] PlanningHistoryCourseAttacks()
        => Combat.Players.Select(player => (player,
            CapturedAttack("GetCurrentTurnAttack", player), CapturedAttack("GetPreviousTurnAttack", player))).ToArray();

    internal (Player Player, PredictedCard? Pending)[] PlanningHistoryCoursePendingAttacks()
        => Combat.Players.Select(player => (player, CapturedAttack("GetPendingTurnAttack", player))).ToArray();

    private PredictedCard? CapturedAttack(string name, Player player)
        => (PredictedCard?)(typeof(SimulatedCombatState).GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(name))
            .Invoke(Combat, [simulator, player]);

    private sealed class PlanningMechanismWriter(KernelSession session, StringBuilder text)
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<Type, Type> RequiredPowerStates = new()
        {
            [typeof(AfterimagePower)] = typeof(CardPlayPairPredictionState),
            [typeof(DarkEmbracePower)] = typeof(DarkEmbracePredictionState),
            [typeof(SkittishPower)] = typeof(SkittishPredictionState),
            [typeof(PanachePower)] = typeof(PanachePredictionState),
            [typeof(SoulboundPower)] = typeof(SoulboundPredictionState),
            [typeof(HellraiserPower)] = typeof(HellraiserPredictionState),
            [typeof(CacophonyPower)] = typeof(CacophonyPredictionState),
            [typeof(AutomationPower)] = typeof(AutomationPredictionState),
            [typeof(JugglingPower)] = typeof(JugglingPredictionState),
            [typeof(ChainsOfBindingPower)] = typeof(ChainsOfBindingPredictionState),
            [typeof(SurroundedPower)] = typeof(SurroundedPredictionState),
            [typeof(VoidFormPower)] = typeof(VoidFormPredictionState),
            [typeof(HardenedShellPower)] = typeof(HardenedShellPredictionState),
            [typeof(FeralPower)] = typeof(FeralPredictionState),
            [typeof(ImitationLearningPower)] = typeof(ImitationLearningPredictionState),
        };
        private readonly List<(PredictedCard Card, string Address)> cards = [];
        private readonly Dictionary<AbstractModel, string> models = new(ReferenceEqualityComparer.Instance);
        private bool available = true;

        internal bool Append()
        {
            text.Append(";planning-facts-v=4;game=").Append(typeof(CardModel).Module.ModuleVersionId.ToString("D"));
            foreach (var player in session.Combat.Players)
            {
                var state = session.simulator.State.GetPlayerCombatState(player);
                AddPile(state.Hand.Cards, player, "H"); AddPile(state.DrawPile.Cards, player, "D");
                AddPile(state.DiscardPile.Cards, player, "C"); AddPile(state.ExhaustPile.Cards, player, "X");
                // An occupied PlayPile is a transaction rather than a settled
                // planning boundary, even if a permissive fork happens to exist.
                if (state.PlayPile.Cards.Count > 0) Unavailable("occupied-play-pile");
            }
            foreach (var (player, current, previous) in session.PlanningHistoryCourseAttacks())
            {
                AddHistoryCard(current, player, "current");
                AddHistoryCard(previous, player, "previous");
            }
            foreach (var (player, pending) in session.PlanningHistoryCoursePendingAttacks())
                AddHistoryCard(pending, player, "pending");
            foreach (var (card, address) in cards)
            {
                text.Append(";card-facts=").Append(address).Append('{');
                if (address.StartsWith("history/", StringComparison.Ordinal))
                    AppendDetachedHistoryState(card.Preview);
                AppendCard(card.Preview);
                text.Append(";fetch=").Append(card.Preview is Fetch && session.Combat.WasFetchPlayedThisTurn(card)).Append('}');
            }
            var powers = session.Combat.EffectivePowers();
            for (var i = 0; i < powers.Count; i++)
            {
                var power = powers[i];
                var address = "power/" + i + "/" + CreatureAddress(power.Owner) + "/" + power.GetType().FullName;
                models[power] = address;
                text.Append(';').Append(address).Append("/applier=").Append(CreatureAddress(power.Applier));
            }
            foreach (var player in session.Combat.Players)
            {
                var relics = session.Combat.RelicsOf(player);
                var index = 0;
                foreach (var relic in relics)
                {
                    var address = "relic/" + player.NetId + "/" + index++ + "/" + relic.GetType().FullName;
                    models[relic] = address;
                    text.Append(';').Append(address).Append("/melted=").Append(relic.IsMelted).Append("/vars=");
                    if (relic.GetType().Assembly != typeof(RelicModel).Assembly)
                    { Unavailable("third-party-relic"); continue; }
                    foreach (var variable in relic.DynamicVars.OrderBy(v => v.Key, StringComparer.Ordinal))
                        text.Append(variable.Key.Length).Append(':').Append(variable.Key).Append('/')
                            .Append(variable.Value.BaseValue.ToString(CultureInfo.InvariantCulture)).Append(',');
                    // Tracked/stateful relic counters already have explicit
                    // live/predicted renderers. Their stale native clone fields
                    // must not override the captured StateStore values below.
                    if (!RelicPredictionStateSupport.IsTracked(relic) && !IsStatefulRelic(relic))
                        AppendDerivedFields(relic, typeof(RelicModel));
                }
            }
            var stored = ReadStates();
            foreach (var power in powers)
            {
                if (power.GetType().Assembly != typeof(PowerModel).Assembly || PowerHiddenStateMirrors.Slots(power).Length > 0)
                { Unavailable("unqualified-power-adapter:" + power.GetType().FullName); continue; }
                if (RequiredPowerStates.TryGetValue(power.GetType(), out var required))
                {
                    // CaptureRootState materializes the audited native payload.
                    // Never create one from clone.GetInternalData: DeepCloneFields
                    // resets that data and it is not the captured live value.
                    if (!stored.Any(entry => ReferenceEquals(entry.Model, power) && entry.StateType == required))
                        Unavailable("missing-captured-power-state:" + power.GetType().Name);
                }
                else if (power is OrbitPower orbit)
                    text.Append(";orbit=").Append(session.Combat.GetOrbitEnergyRemainder(orbit));
                else if (power is PaleBlueDotPower pale)
                    text.Append(";pale=").Append(session.Combat.IsPaleBlueDotActivated(pale));
                else if (power is NightmarePower nightmare)
                {
                    var selection = session.Combat.GetNightmareSelection(nightmare);
                    text.Append(";nightmare-facts="); AppendCard(selection.Preview);
                    AppendCardAddress(selection.Original);
                }
                else if (power is NemesisPower)
                    text.Append(";nemesis=").Append(session.Combat.GetNemesisShouldApplyIntangible(power.Owner));
                else if (power is TenderPower)
                    text.Append(";tender=").Append(session.Combat.GetTenderCardsPlayed(power.Owner));
                else
                {
                    var init = power.GetType().GetMethod("InitInternalData", Fields);
                    if (init?.DeclaringType != typeof(PowerModel))
                        Unavailable("unqualified-internal-power-state:" + power.GetType().Name);
                    else AppendDerivedFields(power, typeof(PowerModel));
                }
            }
            foreach (var entry in stored.OrderBy(e => ModelAddress(e.Model), StringComparer.Ordinal)
                .ThenBy(e => e.StateType.FullName, StringComparer.Ordinal))
            {
                // Amount is already the canonical effective amount in P=.
                // Lazy creation of its redundant wrapper is not semantic state.
                if (entry.StateType.Name == "PowerAmountPredictionState") continue;
                // Upstream lazily mirrors Withering's integer CardsLeft in a
                // CounterPredictionState; native roots retain only DynamicVars.
                // Our full canonical facts keep P=/PT= and skip only an exact
                // duplicate on the same active power, including decimal equality.
                // Owner R9 / 2026-10-12 F45: paid native four-target reproduction
                // found identical future effects plus this redundant lazy payload.
                // Actual withdrawal R2 (r2-withering-v1/proof.json):
                // System.InvalidOperationException: Hellraiser facts: four-target paid Withering must match complete native Facts and RNG
                // Six exits 0/0/1/0/0/0; source and DLL/PDB restored exactly.
                if (entry.Model is WitheringPresencePower withering
                    && entry.StateType == typeof(CounterPredictionState)
                    && entry.Value is CounterPredictionState counter
                    && powers.Any(power => ReferenceEquals(power, withering))
                    && withering.DynamicVars[WitheringPresencePower._cardsLeftKey].BaseValue == counter.Value)
                    continue;
                // Upstream Slow mirrors a lazy integer counter and our card
                // completion tail also maintains native SlowAmount/DisplayAmount.
                // Canonicalize only the same active exact counter when its full
                // decimal SlowAmount agrees; all displayed/other fields remain.
                // Owner R9 / 2026-10-12 same-pattern native paid damage 6,6,7:
                // complete facts differed solely by the redundant Counter state.
                // R2 r2-slow-v1: withdrawing only this block produced actual exit1:
                // System.InvalidOperationException: Slow facts: paid native Slow must match complete PlanningFacts and RNG
                // Exact source/artifact restore and forced native GREEN: proof.json, 0/0/1/0/0/0.
                if (entry.Model is SlowPower slow
                    && entry.StateType == typeof(CounterPredictionState)
                    && entry.Value is CounterPredictionState slowCounter
                    && powers.Any(power => ReferenceEquals(power, slow))
                    && slow.DynamicVars["SlowAmount"].BaseValue == slowCounter.Value)
                    continue;
                // An audited, amount-zero instance has been removed. Its old
                // StateStore payload remains owned for fork diagnostics, while
                // any replacement's required payload is serialized separately.
                // Unknown/detached nonzero models still fail closed.
                if (entry.Model is PowerModel retired && retired.Amount == 0
                    && RequiredPowerStates.TryGetValue(retired.GetType(), out var retiredState)
                    && entry.StateType == retiredState) continue;
                var address = ModelAddress(entry.Model);
                if (address is null) { Unavailable("detached-captured-state"); continue; }
                text.Append(";captured-state=").Append(address).Append('/').Append(entry.StateType.FullName).Append('{');
                if (entry.StateType.Assembly != typeof(KernelSession).Assembly
                    || !typeof(IPredictionStateForkable).IsAssignableFrom(entry.StateType))
                    Unavailable("unqualified-captured-state");
                else
                {
                    if (entry.Value is MusicBoxPredictionState { CardBeingPlayed: not null })
                        Unavailable("pending-music-box-card");
                    AppendObjectFields(entry.Value, entry.StateType, 0);
                }
                text.Append('}');
            }
            AppendCounters();
            AppendCapturedLifecycle();
            return available;
        }

        // Root COW power slots encode their acquisition origin. Their future
        // meaning is the effective ordered power list with captured payloads
        // above; Vendor retirement/Fork/search-key data is never mutated here.
        private string CapturedDeathPhase(Creature creature)
        {
            var container = ReadField(session.Combat, "_deathPhases");
            if (container is null) return "None";
            if (container is not IEnumerable phases)
            {
                Unavailable("unreadable-death-phase-container");
                return "Unavailable";
            }
            foreach (var pair in phases)
                if (pair is not null && ReferenceEquals(CapturedProperty(pair, "Key"), creature))
                    return CapturedProperty(pair, "Value")?.ToString()
                        ?? throw new InvalidOperationException("Captured death phase has no value.");
            return "None";
        }
        private bool IsCompletedDeparted(Creature creature)
            => !session.Combat.ContainsCreature(creature) && session.Hp(creature) == 0
                && CapturedDeathPhase(creature) == "PermanentlyDead"
                && !session.Combat.EffectivePowers().Any(power => ReferenceEquals(power.Owner, creature));

        private void AddPile(IReadOnlyList<PredictedCard> pile, Player player, string pileName)
        {
            for (var i = 0; i < pile.Count; i++) cards.Add((pile[i], player.NetId + "/" + pileName + "/" + i));
        }
        private void AppendCard(CardModel card)
        {
            if (card.GetType().Assembly != typeof(CardModel).Assembly)
            { Unavailable("third-party-card"); return; }
            text.Append(card.GetType().FullName).Append("/type=").Append(card.Type).Append("/target=").Append(card.TargetType);
            var cost = card.EnergyCost;
            text.Append("/energy-base="); AppendValue(ReadField(cost, "_base"), 0);
            text.Append("/captured-x="); AppendValue(ReadField(cost, "_capturedXValue"), 0);
            text.Append("/modifiers=[");
            foreach (var modifier in (IEnumerable<LocalCostModifier>)ReadField(cost, "_localModifiers")!)
                text.Append(modifier.Amount).Append('/').Append(modifier.Type).Append('/').Append(modifier.Expiration)
                    .Append('/').Append(modifier.IsReduceOnly).Append(',');
            text.Append("]/star-base=").Append(card.BaseStarCost).Append("/star-costs=[");
            foreach (var star in (IEnumerable<TemporaryCardCost>)ReadField(card, "_temporaryStarCosts")!)
                text.Append(star.Cost).Append('/').Append(star.ClearsWhenTurnEnds).Append('/').Append(star.ClearsWhenCardIsPlayed).Append(',');
            text.Append("]/last-stars="); AppendValue(ReadField(card, "_lastStarsSpent"), 0);
            text.Append("/single-retain="); AppendValue(ReadField(card, "_hasSingleTurnRetain"), 0);
            text.Append("/single-sly="); AppendValue(ReadField(card, "_hasSingleTurnSly"), 0);
            text.Append("/dupe="); AppendValue(ReadField(card, "_isDupe"), 0);
            text.Append("/clone-of="); AppendCardAddress((CardModel?)ReadField(card, "_cloneOf"));
            AppendDerivedFields(card, typeof(CardModel));
            AppendAttachedModel(card.Enchantment, typeof(EnchantmentModel), "enchantment");
            AppendAttachedModel(card.Affliction, typeof(AfflictionModel), "affliction");
        }
        private void AppendAttachedModel(AbstractModel? model, Type baseType, string kind)
        {
            if (model is null) return;
            var card = kind == "enchantment" ? ((EnchantmentModel)model).Card : ((AfflictionModel)model).Card;
            var cardAddress = FindCardAddress(card);
            if (cardAddress is null) { Unavailable("detached-attached-model"); return; }
            models[model] = cardAddress + "/" + kind;
            text.Append('/').Append(kind).Append('=').Append(model.GetType().FullName);
            if (model.GetType().Assembly != typeof(CardModel).Assembly)
            { Unavailable("third-party-attached-model"); return; }
            AppendDerivedFields(model, baseType);
        }
        private void AppendCounters()
        {
            foreach (var creature in session.Combat.Creatures)
            {
                text.Append(";history-facts=").Append(CreatureAddress(creature)).Append('/')
                    .Append(session.Combat.HasLostHpThisTurn(creature)).Append('/')
                    .Append(session.Combat.WasDoomAppliedThisTurn(creature)).Append('/')
                    .Append(session.Combat.GetCardsDiscardedThisTurn(creature)).Append('/')
                    .Append(CapturedExhaustCount(creature)).Append('/')
                    .Append(session.Combat.GetSkillCardsPlayedThisTurn(creature)).Append('/')
                    .Append(session.Combat.GetManualCardsPlayedThisTurn(creature)).Append('/')
                    .Append(session.Combat.GetCardPlaySeriesStartedThisTurn(creature)).Append('/')
                    .Append(session.Combat.GetShivsPlayedThisTurn(creature)).Append('/')
                    .Append(session.Combat.GetBlockCardsPlayedThisTurn(creature));
                foreach (var receiver in session.Combat.Creatures)
                    text.Append('/').Append(CreatureAddress(receiver)).Append(':')
                        .Append(session.Combat.GetPoweredAttackHitsThisTurn(creature, receiver));
            }
            foreach (var player in session.Combat.Players)
                text.Append(";player-history-facts=").Append(player.NetId).Append('/')
                    .Append(session.Combat.GetStarsGainedThisTurn(player)).Append('/')
                    .Append(session.Combat.GetNonHandDrawsThisTurn(player));
        }
        private List<(AbstractModel Model, Type StateType, object Value)> ReadStates()
        {
            var result = new List<(AbstractModel, Type, object)>();
            var container = ReadField(session.simulator.StateStore, "_states");
            // StateStore uses null for its supported, lazily allocated empty table.
            if (container is null) return result;
            if (container is not IDictionary states)
            { Unavailable("unreadable-captured-state-container"); return result; }
            foreach (DictionaryEntry row in states)
            {
                if (row.Key is not ValueTuple<AbstractModel, Type> key || row.Value is null)
                { Unavailable("unreadable-captured-state-entry"); continue; }
                result.Add((key.Item1, key.Item2, row.Value));
            }
            return result;
        }
        internal void AppendSearchMetadata()
        {
            foreach (var name in new[] { "_cumulativeHpLost", "_recoveredHp", "_potionUses",
                "_longTermResourceValue", "_angerCopiesGenerated", "_growthRewards",
                "_deathSaveRelicHpRestored", "_deathSavePotionHpRestored", "_deathSaveUseCount" })
            {
                text.Append(';').Append(name).Append('=');
                AppendValue(ReadField(session.Combat, name), 0);
            }
        }
        private void AppendCapturedLifecycle()
        {
            // Preserve future lifecycle values, not root-relative archive shape.
            // Retired power slots retain their Vendor/Fork/key representation;
            // the effective ordered powers and required captured payloads above
            // are the native future contract. Only completely removed, HP-zero,
            // permanently dead owners with no effective powers may leave known,
            // death and AI projections; pending/reviving owners remain exact.
            foreach (var name in new[] {
                "_drawNextTurn", "_skipNextDurationTick", "_skipNextMove",
                "_pressureGunBonus", "_steamEruptionDamage", "_steamEruptionPhases",
                "_aeonglassAdditionalStrength", "_aeonglassWitherUpgradeCount",
                "_rampartGrantedRound",
                "_returnToHandNextTurn",
                "_swordSageReplayBonuses",
                "_dampenCasters", "_dampenOriginalUpgrades", "_deathPhases",
                "_stolenStrength", "_stolenDexterity", "_statefulRelicStates",
                "_simulatedOsties", "_simulatedOstyMaxHp", "_monsterIntStates",
                "_knowledgeDemonCurseCounters", "_enemiesIntendingAttack",
                "_hasPredictedEnemyIntents", "_nextCreatureId", "_currentSide",
                "_escapedCreatures", "_knownEnemies", "_allies", "_enemies",
                "_outstandingStolenGold", "_outstandingStolenCards",
                "_brightestFlameMaxHpSpent", "_swordSageCardsInitialized",
                "_lastNormalizedVitalSparkAmount", "_playerTurnEndRequested" })
            {
                text.Append(";lifecycle=").Append(name).Append('=');
                if (name == "_knownEnemies")
                    AppendValue(session.Combat.KnownEnemies.Where(creature => !IsCompletedDeparted(creature)).ToArray(), 0);
                else if (name == "_deathPhases")
                {
                    text.Append('[');
                    foreach (var creature in session.Combat.KnownEnemies.Concat(session.Combat.Allies)
                        .Where(creature => !IsCompletedDeparted(creature)).Distinct())
                        text.Append(CreatureAddress(creature)).Append('/').Append(CapturedDeathPhase(creature)).Append(',');
                    text.Append(']');
                }
                else if (name == "_monsterIntStates"
                    && ReadField(session.Combat, name) is IReadOnlyDictionary<(Creature Creature, string Name), int> monsterInts)
                {
                    // Native paid Rat death removes the old owner from a fresh
                    // capture; the branch retains its COW root overrides. Apply
                    // the same strict departure eligibility as known/death/AI
                    // projections, retaining every active or reviving value.
                    // Vendor storage, Fork and search fingerprint stay intact.
                    AppendValue(monsterInts.Where(row => !IsCompletedDeparted(row.Key.Creature))
                        .ToDictionary(row => row.Key, row => row.Value), 0);
                }
                else AppendValue(ReadField(session.Combat, name), 0);
            }
            foreach (var (player, current, previous) in session.PlanningHistoryCourseAttacks())
            {
                // null and an empty backing dictionary have the same effective
                // attack slot. Preserve the actual card, fields and identity.
                text.Append(";history-current-attack=").Append(player.NetId).Append('/');
                AppendCardAddress(current?.Original);
                text.Append(";history-previous-attack=").Append(player.NetId).Append('/');
                AppendCardAddress(previous?.Original);
            }
            foreach (var (player, pending) in session.PlanningHistoryCoursePendingAttacks())
            {
                text.Append(";history-pending-attack=").Append(player.NetId).Append('/');
                AppendCardAddress(pending?.Original);
                text.Append(";history-pending-window=").Append(player.NetId).Append('/');
                var window = typeof(SimulatedCombatState).GetMethod("GetPendingTurnAttackWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(session.Combat, [player]);
                AppendValue(window, 0);
            }
            // The set's null/empty representation is allocation history;
            // effective membership governs future Smog normalization.
            foreach (var creature in session.Combat.Creatures)
                text.Append(";played-skill-this-turn=").Append(CreatureAddress(creature)).Append('/')
                    .Append(session.Combat.HasPlayedSkillThisTurn(creature));
            if ((int)ReadField(session.Combat, "_cardExecutionScopeDepth")! != 0)
                Unavailable("active-card-execution-scope");
            if (ReadField(session.Combat, "_pendingPowerAmountChanges") is ICollection { Count: > 0 })
                Unavailable("pending-power-amount-change");
            if (ReadField(session.Combat, "_powerCardSources") is ICollection { Count: > 0 })
                Unavailable("active-power-card-source");
            // AI continuation text already includes StateLog. Initial-roll and
            // captured static parameters also govern subsequent native moves.
            if (ReadField(session.Combat, "_monsterAiStates") is IEnumerable ais)
                foreach (var pair in ais)
                {
                    if (pair is null) throw new InvalidOperationException("Captured AI map contains a null entry.");
                    var key = CapturedProperty(pair, "Key") as Creature
                        ?? throw new InvalidOperationException("Captured AI map has no creature identity.");
                    var ai = CapturedProperty(pair, "Value")
                        ?? throw new InvalidOperationException("Captured AI map has no state.");
                    if (IsCompletedDeparted(key)) continue;
                    text.Append(";ai-facts=").Append(CreatureAddress(key));
                    foreach (var property in new[] { "KnowledgeDemonCurseCounter", "NeedsInitialRoll", "Static" })
                    {
                        text.Append('/').Append(property).Append('=');
                        var value = CapturedProperty(ai, property);
                        if (property == "Static")
                        {
                            if (value is null) throw new InvalidOperationException("Captured AI state has no static snapshot.");
                            if (ai is BranchMonsterAiState capturedAi)
                                value = EffectiveAiStatic(capturedAi);
                            AppendObjectFields(value, value.GetType(), 0);
                        }
                        else AppendValue(value, 0);
                    }
                }
        }
        private BranchMonsterStaticSnapshot EffectiveAiStatic(BranchMonsterAiState ai)
        {
            // Upstream: the existing ResolveConditional consumes captured owned
            // state; upstream has no local complete planning-facts contract.
            // Local: canonicalize only its actual future conditional input,
            // retaining strict unknown fallback, native-validated Rat weights
            // and all other static fields. No native lambda or RNG is evaluated.
            // Reason: user R9 / P0-SIM26, 2026-10-11 F18 native reproduction:
            // STUNNED really clears off balance and advances to HEADBUTT, while
            // the old root still holds an unused POST_HEADBUTT=DIZZY selection.
            var effective = ai.Monster is MegaCrit.Sts2.Core.Models.Monsters.TwoTailedRat
                ? EffectiveRatStatic(ai) : ai.Static;
            if (effective.ConditionalSelections.Count == 0) return effective;
            var conditionals = effective.ConditionalSelections.ToDictionary(row => row.Key, row => row.Value);
            var resolver = typeof(BranchMonsterAi).GetMethod("ResolveConditional", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("BranchMonsterAi.ResolveConditional");
            foreach (var row in effective.ConditionalSelections)
            {
                var conditional = ai.Machine.States[row.Key] as
                    MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.ConditionalBranchState
                    ?? throw new InvalidOperationException("Captured AI conditional branch is unavailable: " + row.Key);
                conditionals[row.Key] = (string)resolver.Invoke(null, [conditional, ai, session.Combat, session.Simulator])!;
            }
            return effective with { ConditionalSelections = conditionals };
        }
        private BranchMonsterStaticSnapshot EffectiveRatStatic(BranchMonsterAiState ai)
        {
            // Native Rat weight lambdas vary with current summon eligibility.
            // The existing branch resolver overrides these four RAND weights
            // from captured state. Compare that same effective future input,
            // retaining every other static field/weight and the original root.
            var weights = ai.Static.RandomBaseWeights.ToDictionary(row => row.Key, row => row.Value);
            var resolver = typeof(BranchMonsterAi).GetMethod("GetBranchWeight", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("BranchMonsterAi.GetBranchWeight");
            foreach (var row in ai.Static.RandomBaseWeights)
            {
                if (row.Key.BranchId != "RAND" || row.Key.StateId is not
                    ("SCRATCH_MOVE" or "DISEASE_BITE_MOVE" or "SCREECH_MOVE" or "CALL_FOR_BACKUP_MOVE")) continue;
                var random = ai.Machine.States[row.Key.BranchId] as
                    MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.RandomBranchState
                    ?? throw new InvalidOperationException("Captured Rat RAND branch is unavailable.");
                var state = random.States.Single(weight => weight.stateId == row.Key.StateId);
                weights[row.Key] = (float)resolver.Invoke(null, [state, row.Key.BranchId, ai, session.Combat])!;
            }
            return ai.Static with { RandomBaseWeights = weights };
        }
        private int CapturedExhaustCount(Creature creature)
            // MaterializeRoot eagerly fills this captured counter before a fork
            // exists. This is the vendor's private branch getter, not a read of
            // the live CombatManager history or a native Power getter.
            => (int)(typeof(SimulatedCombatState).GetMethod("GetCardsExhaustedThisTurn", Fields)
                ?? throw new MissingMethodException(typeof(SimulatedCombatState).FullName, "GetCardsExhaustedThisTurn"))
                .Invoke(session.Combat, [creature])!;
        private string? ModelAddress(AbstractModel model)
            => models.GetValueOrDefault(model) ?? (model is CardModel card ? FindCardAddress(card) : null);
        private string? FindCardAddress(CardModel card)
            => cards.FirstOrDefault(c => ReferenceEquals(c.Card.Original, card) || ReferenceEquals(c.Card.Preview, card)).Address;
        private void AddHistoryCard(PredictedCard? card, Player player, string slot)
        {
            if (card is null || cards.Any(row => ReferenceEquals(row.Card.Original, card.Original))) return;
            cards.Add((card, "history/" + player.NetId + "/" + slot));
        }
        private void AppendDetachedHistoryState(CardModel card)
        {
            // Piled cards already receive the upstream continuation rendering.
            // A removed native attack still drives HistoryCourse, so record
            // the same upgrade/vars/keywords/attached state from its frozen
            // preview too. false forbids discovery of live BaseLib modifiers.
            var renderer = typeof(ContinuationStamp).GetMethod(
                "AppendCard", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("ContinuationStamp.AppendCard");
            renderer.Invoke(null, [text, card, false]);
        }
        private void AppendCardAddress(CardModel? card)
        {
            if (card is null) { text.Append("null"); return; }
            var address = FindCardAddress(card);
            if (address is null) Unavailable("detached-card-identity");
            else text.Append(address);
        }
        private static string CreatureAddress(Creature? creature)
            => creature is null ? "null" : creature.Side + "/" + creature.CombatId;
        private void AppendDerivedFields(object model, Type stop)
        {
            for (var type = model.GetType(); type != stop && type is not null; type = type.BaseType)
                AppendObjectFields(model, type, 0);
        }
        private void AppendObjectFields(object value, Type type, int depth)
        {
            if (depth > 32) { Unavailable("cyclic-captured-state"); return; }
            foreach (var field in type.GetFields(Fields | BindingFlags.DeclaredOnly).Where(f => !f.IsStatic)
                .OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                text.Append('/').Append(type.Name).Append('.').Append(field.Name).Append('=');
                AppendValue(field.GetValue(value), depth + 1);
            }
        }
        private void AppendValue(object? value, int depth)
        {
            if (depth > 32) { Unavailable("cyclic-captured-state"); return; }
            if (value is null) { text.Append("null"); return; }
            if (value is string str) { text.Append(str.Length).Append(':').Append(str); return; }
            if (value is double d) { text.Append(BitConverter.DoubleToInt64Bits(d)); return; }
            if (value is float f) { text.Append(BitConverter.SingleToInt32Bits(f)); return; }
            if (value is decimal dec) { foreach (var bit in decimal.GetBits(dec)) text.Append(bit).Append(':'); return; }
            var type = value.GetType();
            if (value is Type modelType)
            { text.Append(modelType.AssemblyQualifiedName); return; }
            if (type.IsPrimitive || type.IsEnum) { text.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (value is CardModel card) { AppendCardAddress(card); return; }
            if (value is PredictedCard predicted) { AppendCardAddress(predicted.Original); return; }
            if (value is Creature creature) { text.Append(CreatureAddress(creature)); return; }
            if (value is Player player) { text.Append(player.NetId); return; }
            if (value is AbstractModel model)
            {
                var address = ModelAddress(model);
                if (address is null) Unavailable("detached-model-identity"); else text.Append(address);
                return;
            }
            if (value is IDictionary dictionary)
            {
                // Dictionaries are semantic maps. Canonicalize only their entry
                // text; ordered lists (especially cost modifiers) remain ordered.
                var rows = new List<string>();
                foreach (DictionaryEntry row in dictionary)
                {
                    var mark = text.Length; AppendValue(row.Key, depth + 1); text.Append('='); AppendValue(row.Value, depth + 1);
                    rows.Add(text.ToString(mark, text.Length - mark)); text.Length = mark;
                }
                text.Append('[').AppendJoin(',', rows.Order(StringComparer.Ordinal)).Append(']'); return;
            }
            if (value is IEnumerable sequence && (type.IsArray || type.Namespace == "System.Collections.Generic"
                || type.Assembly == typeof(KernelSession).Assembly && type.Name.StartsWith("Forkable", StringComparison.Ordinal)))
            {
                var unordered = type.GetInterfaces().Any(i => i.IsGenericType
                    && (i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
                        || i.GetGenericTypeDefinition() == typeof(IReadOnlySet<>)));
                if (unordered)
                {
                    var rows = new List<string>();
                    foreach (var item in sequence)
                    {
                        var mark = text.Length; AppendValue(item, depth + 1);
                        rows.Add(text.ToString(mark, text.Length - mark)); text.Length = mark;
                    }
                    text.Append('[').AppendJoin(',', rows.Order(StringComparer.Ordinal)).Append(']');
                }
                else { text.Append('['); foreach (var item in sequence) { AppendValue(item, depth + 1); text.Append(','); } text.Append(']'); }
                return;
            }
            if (type.IsValueType || type.Assembly == typeof(KernelSession).Assembly && typeof(IPredictionStateForkable).IsAssignableFrom(type))
            { AppendObjectFields(value, type, depth + 1); return; }
            Unavailable("unqualified-field:" + type.FullName);
        }
        private static object? ReadField(object value, string name)
        {
            for (var type = value.GetType(); type is not null; type = type.BaseType)
                if (type.GetField(name, Fields | BindingFlags.DeclaredOnly) is { } field) return field.GetValue(value);
            throw new MissingFieldException(value.GetType().FullName, name);
        }
        private static object? CapturedProperty(object value, string name)
            => (value.GetType().GetProperty(name, Fields)
                ?? throw new MissingMemberException(value.GetType().FullName, name)).GetValue(value);
        private static bool IsStatefulRelic(RelicModel relic)
            => (bool)(typeof(SimulatedCombatState).GetMethod("IsStatefulRelic", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(SimulatedCombatState).FullName, "IsStatefulRelic"))
                .Invoke(null, [relic])!;
        private void Unavailable(string reason)
        { available = false; text.Append("!unavailable[").Append(reason).Append(']'); }
    }
}

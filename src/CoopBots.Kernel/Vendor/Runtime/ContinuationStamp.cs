using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Orbs;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor;

/// <summary>
/// 跨回合续用路线的逐项状态文本。它不是哈希；只有实际状态与预测状态文本完全相等才允许复用。
/// </summary>
internal sealed record ContinuationStamp(string StateText)
{
    public string DescribeFirstDifference(ContinuationStamp actual)
        => DescribeDifferences(actual, maximumDifferences: 1).FirstOrDefault() ?? "none";

    public IReadOnlyList<string> DescribeDifferences(
        ContinuationStamp actual,
        int maximumDifferences = int.MaxValue)
    {
        if (maximumDifferences <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDifferences));
        List<string> differences = [];
        string[] expectedFields = StateText.Split(';');
        string[] actualFields = actual.StateText.Split(';');
        int sharedCount = Math.Min(expectedFields.Length, actualFields.Length);
        for (int index = 0; index < sharedCount; index++)
        {
            string expectedField = expectedFields[index];
            string actualField = actualFields[index];
            if (string.Equals(expectedField, actualField, StringComparison.Ordinal))
                continue;

            (string expectedName, string expectedValue) = SplitField(expectedField);
            (string actualName, string actualValue) = SplitField(actualField);
            if (!string.Equals(expectedName, actualName, StringComparison.Ordinal))
            {
                differences.Add($"field_order[{index}] expected={LogValue(expectedField)} " +
                                $"actual={LogValue(actualField)}");
            }
            else
            {
                differences.Add(DescribeValueDifference(expectedName, expectedValue, actualValue));
            }
            if (differences.Count >= maximumDifferences)
                return differences;
        }

        if (expectedFields.Length != actualFields.Length)
        {
            string expected = sharedCount < expectedFields.Length ? expectedFields[sharedCount] : "<end>";
            string current = sharedCount < actualFields.Length ? actualFields[sharedCount] : "<end>";
            differences.Add($"field_count expected={expectedFields.Length} actual={actualFields.Length} " +
                            $"first_extra_expected={LogValue(expected)} first_extra_actual={LogValue(current)}");
        }
        return differences;
    }

    public static ContinuationStamp CaptureLive(CombatState state, Player? selectedPlayer = null)
    {
        Player player = selectedPlayer ?? LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        PlayerCombatState pcs = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有战斗状态。");
        StringBuilder text = Begin(
            pcs.TurnNumber,
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.Creature.Block,
            pcs.Energy,
            pcs.Stars,
            player.Gold);
        AppendOsty(text, player.Osty, player.Osty?.CurrentHp ?? 0, player.Osty?.MaxHp ?? 0);
        AppendEnemies(text, state.Enemies,
            enemy => enemy.CurrentHp,
            enemy => enemy.MaxHp,
            enemy => enemy.Block,
            enemy => enemy.Monster?.NextMove?.Id ?? "null");
        SimulatedCombatState.AppendLiveMonsterAiContinuation(text, state.Enemies);
        SimulatedCombatState.AppendLiveMonsterStateContinuation(text, state.Enemies);
        AppendLivePile(text, pcs.Hand, 'H');
        AppendLivePile(text, pcs.DrawPile, 'D');
        AppendLivePile(text, pcs.DiscardPile, 'C');
        AppendLivePile(text, pcs.ExhaustPile, 'X');
        SimulatedCombatState.AppendLiveTurnCardHistory(text, state, player);
        AppendOrbs(text, pcs.OrbQueue.Capacity, pcs.OrbQueue.Orbs);
        AppendPotions(text, player, player.GetPotionAtSlotIndex);
        SimulatedCombatState.AppendLiveStatefulRelics(text, player);
        RelicPredictionStateSupport.AppendLiveContinuation(text, player);
        ModelPredictionStateMirrors.AppendLiveContinuation(text, state);
        // CoopBots multiplayer port: emit this field even when empty. DescribeDifferences
        // compares the whole party text as ONE ';'-separated list, so a field present on
        // one side and absent on the other shifts every later field and reports
        // `field_order[i]` instead of the field that actually differs.
        text.Append(";onplay_configuration=");
        if (AdaptedCardOnPlayMirrors.CaptureLiveStamp() is { } onPlayStamp)
            text.Append(onPlayStamp);
        AppendPowers(text, state.Creatures.SelectMany(creature => creature.Powers));
        AppendRng(text,
            state.RunState.Rng.Shuffle.CaptureState(),
            state.RunState.Rng.CombatCardGeneration.CaptureState(),
            state.RunState.Rng.CombatPotionGeneration.CaptureState(),
            state.RunState.Rng.CombatCardSelection.CaptureState(),
            state.RunState.Rng.CombatEnergyCosts.CaptureState(),
            state.RunState.Rng.CombatTargets.CaptureState(),
            state.RunState.Rng.CombatOrbGeneration.CaptureState(),
            state.RunState.Rng.MonsterAi.CaptureState(),
            state.RunState.Rng.Niche.CaptureState());
        return new ContinuationStamp(text.ToString());
    }

    public static ContinuationStamp CapturePredicted(
        Player player,
        CombatPredictionSimulator simulator,
        int turn,
        IntentForecast forecast,
        int startTurnNumber)
    {
        SimPlayerCombatState pcs = simulator.State.GetPlayerCombatState(player);
        SimCreatureState simulatedPlayer = simulator.State.GetCreature(player.Creature);
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        StringBuilder text = Begin(
            turn,
            simulatedPlayer.CurrentHp,
            simulatedPlayer.MaxHp,
            simulatedPlayer.Block,
            pcs.Energy,
            pcs.Stars,
            combat.GetPlayerGold(player));
        AppendOsty(
            text,
            combat.GetOsty(player),
            combat.GetOsty(player) is { } osty ? simulator.State.GetCreature(osty).CurrentHp : 0,
            combat.GetOstyMaxHp(simulator, player));
        IReadOnlyList<Creature> predictedEnemies = combat.Enemies;
        AppendEnemies(text, predictedEnemies,
            enemy => simulator.State.GetCreature(enemy).CurrentHp,
            enemy => simulator.State.GetCreature(enemy).MaxHp,
            enemy => simulator.State.GetCreature(enemy).Block,
            enemy => combat.TryGetForcedMoveId(enemy, out string forcedMove)
                ? forcedMove
                : combat.GetPredictedMoveId(enemy));
        combat.AppendPredictedMonsterAiContinuation(text);
        combat.AppendPredictedMonsterStateContinuation(text);
        AppendPredictedPile(text, pcs.Hand, 'H');
        AppendPredictedPile(text, pcs.DrawPile, 'D');
        AppendPredictedPile(text, pcs.DiscardPile, 'C');
        AppendPredictedPile(text, pcs.ExhaustPile, 'X');
        combat.AppendPredictedTurnCardHistory(text, player);
        AppendPredictedOrbs(text, simulator, pcs.OrbQueue.Capacity, pcs.OrbQueue.Orbs);
        AppendPotions(text, player, slot => combat.GetPotionAtSlot(player, slot));
        combat.AppendPredictedStatefulRelics(text, player);
        RelicPredictionStateSupport.AppendPredictedContinuation(
            text,
            simulator,
            combat.RelicsOf(player));
        StateFingerprintBuilder adapterFingerprint = new();
        ModelPredictionStateMirrors.AppendPredicted(ref adapterFingerprint, text, simulator, combat);
        // CoopBots multiplayer port: always emit, same reason as the live branch above.
        text.Append(";onplay_configuration=");
        if (combat.AdaptedOnPlay is { } adaptedOnPlay)
            text.Append(adaptedOnPlay.Stamp);
        AppendPowers(text, combat.EffectivePowers(), simulator);
        AppendRng(text,
            simulator.Rng.ShuffleState,
            simulator.Rng.CombatCardGenerationState,
            simulator.Rng.CombatPotionGenerationState,
            simulator.Rng.CombatCardSelectionState,
            simulator.Rng.CombatEnergyCostsState,
            simulator.Rng.CombatTargetsState,
            simulator.Rng.CombatOrbGenerationState,
            simulator.Rng.MonsterAiState,
            simulator.Rng.NicheState);
        return new ContinuationStamp(text.ToString());
    }

    private static StringBuilder Begin(int turn, int hp, int maxHp, int block, int energy, int stars, int gold)
        => new StringBuilder().Append("turn=").Append(turn)
            .Append(";hp=").Append(hp).Append(";max_hp=").Append(maxHp).Append(";block=").Append(block)
            .Append(";energy=").Append(energy).Append(";stars=").Append(stars)
            .Append(";gold=").Append(gold);

    private static (string Name, string Value) SplitField(string field)
    {
        int separator = field.IndexOf('=');
        return separator < 0
            ? (field, string.Empty)
            : (field[..separator], field[(separator + 1)..]);
    }

    private static string DescribeValueDifference(string name, string expected, string actual)
    {
        string[]? partNames = name switch
        {
            "osty" => ["combat_id", "hp", "max_hp"],
            "O" => ["capacity", "orbs"],
            "R" => ["shuffle", "card_generation", "potion_generation", "card_selection", "energy_costs", "targets", "orbs", "monster_ai", "niche"],
            _ when name.StartsWith('E') => ["combat_id", "monster", "slot", "hp", "max_hp", "block", "move"],
            _ => null,
        };
        if (partNames != null)
        {
            char separator = name == "O" ? ':' : '/';
            string[] expectedParts = expected.Split(separator);
            string[] actualParts = actual.Split(separator);
            int count = Math.Max(expectedParts.Length, actualParts.Length);
            for (int index = 0; index < count; index++)
            {
                string expectedPart = index < expectedParts.Length ? expectedParts[index] : "<missing>";
                string actualPart = index < actualParts.Length ? actualParts[index] : "<missing>";
                if (string.Equals(expectedPart, actualPart, StringComparison.Ordinal))
                    continue;
                string partName = index < partNames.Length ? partNames[index] : $"part_{index}";
                return $"field={name}.{partName} expected={LogValue(expectedPart)} actual={LogValue(actualPart)}";
            }
        }

        if (name is "H" or "D" or "C" or "X" or "P")
        {
            IReadOnlyList<string> expectedItems = SplitTopLevelItems(expected);
            IReadOnlyList<string> actualItems = SplitTopLevelItems(actual);
            int count = Math.Max(expectedItems.Count, actualItems.Count);
            for (int index = 0; index < count; index++)
            {
                string expectedItem = index < expectedItems.Count ? expectedItems[index] : "<missing>";
                string actualItem = index < actualItems.Count ? actualItems[index] : "<missing>";
                if (string.Equals(expectedItem, actualItem, StringComparison.Ordinal))
                    continue;
                return $"field={name}[{index}] expected={LogValue(expectedItem)} actual={LogValue(actualItem)} " +
                       $"expected_count={expectedItems.Count} actual_count={actualItems.Count}";
            }
        }

        return $"field={name} expected={LogValue(expected)} actual={LogValue(actual)}";
    }

    private static IReadOnlyList<string> SplitTopLevelItems(string value)
    {
        List<string> items = [];
        int start = 0;
        int bracketDepth = 0;
        for (int index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    break;
                case ',' when bracketDepth == 0:
                    if (index > start)
                        items.Add(value[start..index]);
                    start = index + 1;
                    break;
            }
        }
        if (start < value.Length)
            items.Add(value[start..]);
        return items;
    }

    private static string LogValue(string value)
    {
        const int maxLength = 180;
        string normalized = value.Replace('\r', ' ').Replace('\n', ' ');
        return normalized.Length <= maxLength
            ? $"{{{normalized}}}"
            : $"{{{normalized[..maxLength]}…}}";
    }

    private static void AppendOsty(StringBuilder text, Creature? osty, int hp, int maxHp)
        => text.Append(";osty=")
            .Append(osty?.CombatId ?? uint.MaxValue)
            .Append('/').Append(hp)
            .Append('/').Append(maxHp);

    private static void AppendPotions(StringBuilder text, Player player, Func<int, PotionModel?> getPotion)
    {
        text.Append(";potions=");
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            if (slot > 0)
                text.Append(',');
            text.Append(getPotion(slot)?.Id.Entry ?? "-");
        }
    }

    private static void AppendEnemies(
        StringBuilder text,
        IReadOnlyList<Creature> enemies,
        Func<Creature, int> hp,
        Func<Creature, int> maxHp,
        Func<Creature, int> block,
        Func<Creature, string> move)
    {
        for (int index = 0; index < enemies.Count; index++)
        {
            Creature enemy = enemies[index];
            text.Append(";E").Append(index).Append('=')
                .Append(enemy.CombatId ?? uint.MaxValue).Append('/')
                .Append(enemy.Monster?.Id.Entry ?? "null").Append('/')
                .Append(enemy.SlotName ?? "-").Append('/')
                .Append(hp(enemy)).Append('/').Append(maxHp(enemy)).Append('/')
                .Append(block(enemy)).Append('/').Append(move(enemy));
        }
    }

    private static void AppendLivePile(StringBuilder text, CardPile pile, char marker)
    {
        text.Append(';').Append(marker).Append('=');
        foreach (CardModel card in pile.Cards)
            AppendCard(text, card, discoverUnregisteredBaseLibModifiers: true);
    }

    private static void AppendPredictedPile(StringBuilder text, SimCardPile pile, char marker)
    {
        text.Append(';').Append(marker).Append('=');
        foreach (PredictedCard card in pile.Cards)
            AppendCard(text, card.Preview, discoverUnregisteredBaseLibModifiers: false);
    }

    private static void AppendCard(
        StringBuilder text,
        CardModel card,
        bool discoverUnregisteredBaseLibModifiers)
    {
        text.Append(card.Id.Entry).Append('+').Append(card.CurrentUpgradeLevel)
            .Append('/').Append(card.EnergyCost.CostsX).Append(':')
            .Append(card.EnergyCost.GetWithModifiers(CostModifiers.Local))
            .Append('/').Append(card.HasStarCostX).Append(':').Append(card.CurrentStarCost)
            .Append('/').Append(card.BaseReplayCount)
            .Append('/').Append(card.ExhaustOnNextPlay)
            .Append('/').Append(card.IsSlyThisTurn)
            .Append('/').Append(card.ShouldRetainThisTurn)
            .Append('/').Append(card.DeckVersion != null)
            .Append('/').Append(card.HasBeenRemovedFromState)
            .Append('/');
        EnchantmentStateSupport.Append(text, card.Enchantment);
        text.Append('/').Append(card.Affliction?.Id.Entry ?? "-").Append(':').Append(card.Affliction?.Amount ?? 0)
            .Append('[');
        foreach (var dynamicVar in card.DynamicVars.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!SemanticStateFieldPolicy.IsSemantic(card, dynamicVar.Key, dynamicVar.Value))
                continue;
            text.Append(dynamicVar.Key).Append('=').Append(dynamicVar.Value.BaseValue);
            if (dynamicVar.Value is StringVar stringVar)
                text.Append(':').Append(stringVar.StringValue);
            text.Append(',');
        }
        text.Append("]/private=");
        switch (card)
        {
            case Claw claw:
                text.Append(claw.ExtraDamageFromClawPlays);
                break;
            case GeneticAlgorithm geneticAlgorithm:
                text.Append(geneticAlgorithm.IncreasedBlock);
                break;
            case Maul maul:
                text.Append(maul._extraDamageFromMaulPlays);
                break;
            case MadScience madScience:
                text.Append(madScience.TinkerTimeType).Append('/').Append(madScience.TinkerTimeRider);
                break;
            case Rampage rampage:
                text.Append(rampage.ExtraDamageFromPlays);
                break;
            case TheScythe scythe:
                text.Append(scythe.IncreasedDamage);
                break;
            default:
                text.Append('-');
                break;
        }
        text.Append("/keywords=[");
        text.AppendJoin(',', card.GetKeywordsWithSources(KeywordSources.Local).Order());
        text.Append("]/baselib=");
        if (!PredictionModModelSupport.AppendBaseLibCardModifierState(
                text,
                card,
                discoverUnregisteredBaseLibModifiers))
            text.Append('-');
        text.Append(',');
    }

    private static void AppendOrbs(
        StringBuilder text,
        int capacity,
        IReadOnlyList<OrbModel> orbs)
    {
        text.Append(";O=").Append(capacity).Append(':');
        foreach (OrbModel orb in orbs)
            text.Append(orb.Id.Entry).Append('[')
                .Append(orb.PassiveVal).Append('/')
                .Append(orb.EvokeVal).Append("],");
    }

    private static void AppendPredictedOrbs(
        StringBuilder text,
        CombatPredictionSimulator simulator,
        int capacity,
        IReadOnlyList<OrbModel> orbs)
    {
        text.Append(";O=").Append(capacity).Append(':');
        foreach (OrbModel orb in orbs)
            text.Append(orb.Id.Entry).Append('[')
                .Append(OrbMirrors.GetPassiveValue(simulator, orb)).Append('/')
                .Append(OrbMirrors.GetEvokeValue(simulator, orb)).Append("],");
    }

    private static void AppendPowers(StringBuilder text, IEnumerable<PowerModel> powers, CombatPredictionSimulator? simulator = null)
    {
        // CoopBots multiplayer port: canonical order, because DescribeDifferences compares
        // the P= list by INDEX. The two renderers enumerate powers differently (live uses
        // `state.Creatures.SelectMany(c => c.Powers)`, a branch uses `EffectivePowers()` --
        // the hook-listener order), so the same board could render the same amounts onto
        // different seats and compare equal, or report a `P[n]` diff that is only an
        // enumeration-order difference. Sorting by (owner, type) makes index n mean the
        // same power on both sides.
        PowerModel[] active = powers.Where(power => power.Amount != 0)
            .OrderBy(power => power.Owner.CombatId)
            .ThenBy(power => power.Id.Entry, StringComparer.Ordinal)
            .ToArray();
        text.Append(";P=");
        foreach (PowerModel power in active)
        {
            text.Append(power.Owner.CombatId).Append(':').Append(power.Id.Entry).Append('=')
                .Append(power.Amount).Append('/')
                .Append(PowerLifecycleSupport.SemanticallyRelevantAmountOnTurnStart(power));
            if (PowerLifecycleSupport.SemanticallyRelevantSkipNextDurationTick(power)) text.Append("/skip");
            text.Append('[');
            foreach (var dynamicVar in power.DynamicVars.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (!SemanticStateFieldPolicy.IsSemantic(power, dynamicVar.Key, dynamicVar.Value))
                    continue;
                text.Append(dynamicVar.Key).Append('=').Append(dynamicVar.Value.BaseValue);
                if (dynamicVar.Value is StringVar stringVar)
                    text.Append(':').Append(stringVar.StringValue);
                text.Append(',');
            }
            if (power is SurroundedPower surrounded)
                text.Append("Facing=").Append(simulator == null ? surrounded.Facing
                    : PowerPredictionStateSupport.SurroundedFacing(simulator, surrounded)).Append(',');
            if (power is NightmarePower nightmare)
            {
                CardModel selected = simulator == null
                    ? nightmare.GetInternalData<NightmarePower.Data>().selectedCard
                        ?? throw new InvalidOperationException("Native Nightmare has no selected card at continuation capture.")
                    : ((SimulatedCombatState)simulator.State.CombatState).GetNightmareSelection(nightmare).Preview;
                text.Append("Selected=");
                AppendCard(text, selected, discoverUnregisteredBaseLibModifiers: simulator == null);
            }
            if (power is OrbitPower orbit)
                text.Append("EnergyRemainder=").Append(simulator == null ? (4 - orbit.DisplayAmount) % 4
                    : ((SimulatedCombatState)simulator.State.CombatState).GetOrbitEnergyRemainder(orbit)).Append(',');
            text.Append("],");
        }
        // P0-SIM-1（2026-10-05 伪 VICTORY）：上面的 P= 是有序表。实机渲染
        // `Creatures.SelectMany(c => c.Powers)`，分支渲染 `EffectivePowers()`（hook listener
        // 顺序），两侧口径不同时，两个"把同一个 amount 序列挂到不同席位"的盘面可以渲染成
        // 同一段文本。SandpitPower 的全部语义就在 Target 上，逐席交换会翻转"谁被倒计时处死"。
        // 这里按 (owner,type,target) 排序单独渲染一遍，让交换无法再被枚举顺序掩盖。
        PowerModel[] targeted = active.Where(power => power.Target is not null)
            .OrderBy(power => power.Owner.CombatId)
            .ThenBy(power => power.Id.Entry, StringComparer.Ordinal)
            .ThenBy(power => power.Target!.CombatId)
            .ToArray();
        // CoopBots multiplayer port: always emit PT=, even with no target. Live 2026-10-06
        // (FABRICATOR_NORMAL) reported `field_order[35] expected={PT=0:GUARDED_POWER=1@0,…}
        // actual={R=3008…}` -- the prediction had a targeted power and the live board did
        // not, so the field COUNT differed and every later field was compared one slot off.
        // An empty PT= keeps the layout fixed and turns that case into a readable
        // `PT expected={…} actual={}`.
        text.Append(";PT=");
        foreach (PowerModel power in targeted)
            text.Append(power.Owner.CombatId).Append(':').Append(power.Id.Entry).Append('=')
                .Append(power.Amount).Append('@').Append(power.Target!.CombatId).Append(',');
    }

    private static void AppendRng(
        StringBuilder text,
        PredictionRngState shuffle,
        PredictionRngState generation,
        PredictionRngState potionGeneration,
        PredictionRngState selection,
        PredictionRngState energy,
        PredictionRngState targets,
        PredictionRngState orbs,
        PredictionRngState monsterAi,
        PredictionRngState niche)
    {
        text.Append(";R=");
        AppendRngState(text, shuffle).Append('/');
        AppendRngState(text, generation).Append('/');
        AppendRngState(text, potionGeneration).Append('/');
        AppendRngState(text, selection).Append('/');
        AppendRngState(text, energy).Append('/');
        AppendRngState(text, targets).Append('/');
        AppendRngState(text, orbs).Append('/');
        AppendRngState(text, monsterAi).Append('/');
        AppendRngState(text, niche);
    }

    private static StringBuilder AppendRngState(StringBuilder text, PredictionRngState state)
        => text.Append(state.Counter).Append(':')
            .Append(state.State0).Append(':').Append(state.State1).Append(':')
            .Append(state.State2).Append(':').Append(state.State3);
}

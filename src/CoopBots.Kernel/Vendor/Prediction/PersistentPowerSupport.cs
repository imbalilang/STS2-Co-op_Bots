using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Card;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Resources;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor;

internal static class PersistentPowerSupport
{
    public static int ConsumeModifiedHandDraw(
        SimulatedCombatState combat,
        Player player,
        int baseDraw)
    {
        DrawCardsNextTurnPower? delayedDraw = combat.GetPower<DrawCardsNextTurnPower>(player.Creature);
        int drawNotYetSnapshotted = delayedDraw is { Amount: > 0, AmountOnTurnStart: 0 }
            ? delayedDraw.Amount
            : 0;
        int result = GetModifiedHandDraw(
            combat,
            player,
            baseDraw + combat.ConsumeDrawNextTurn(player) + drawNotYetSnapshotted);
        combat.CompleteRelicHandDraw(player);
        return result;
    }

    public static int GetModifiedHandDraw(
        SimulatedCombatState combat,
        Player player,
        int baseDraw)
    {
        // Upstream calls native Hook then subtracts the captured-turn relic bonus.
        // We substitute the modeled relic at its native position in the hook order.
        // User 2026-10-07: a frozen turn-1 root must not read the live owner's later
        // turn (Ring of the Snake changed the same future draw from five to three).
        // Do not adjust the final sum: that also moves a bonus past late modifiers.
        decimal result = baseDraw;
        if (CombatManager.Instance.IsOverOrEnding && !CombatManager.Instance.IsStarting)
            return Math.Max(0, (int)result);
        foreach (AbstractModel listener in combat.IterateHookListeners())
        {
            if (listener is RelicModel relic && IsProjectedHandDrawRelic(relic))
            {
                if (ReferenceEquals(relic.Owner, player))
                {
                    int turn = combat.GetPlayerTurnNumber(player);
                    result += GetTurnBasedHandDrawContribution(relic, combat, turn)
                        + combat.GetStatefulRelicHandDrawContribution(relic, player, turn);
                }
            }
            else
                result = listener.ModifyHandDraw(player, result);
        }
        // Native Hook dispatches all ordinary modifiers before the late pass.
        foreach (AbstractModel listener in combat.IterateHookListeners())
            result = listener.ModifyHandDrawLate(player, result);
        return Math.Max(0, (int)result);
    }

    public static int GetModifiedMaxEnergy(SimulatedCombatState combat, Player player)
    {
        // The same native owner/turn dependency exists in Bread and Pael's Flesh.
        // Evaluate those captured listeners in order, rather than correcting a
        // value obtained from live PlayerCombatState after capture.
        decimal result = player.MaxEnergy;
        if (CombatManager.Instance.IsOverOrEnding && !CombatManager.Instance.IsStarting)
            return Math.Max(0, (int)result);
        foreach (AbstractModel listener in combat.IterateHookListeners())
        {
            if (listener is Bread or PaelsFlesh)
            {
                var relic = (RelicModel)listener;
                if (ReferenceEquals(relic.Owner, player))
                    result += GetTurnBasedMaxEnergyContribution(relic, combat.GetPlayerTurnNumber(player));
            }
            else
                result = listener.ModifyMaxEnergy(player, result);
        }
        return Math.Max(0, (int)result);
    }

    private static bool IsProjectedHandDrawRelic(RelicModel relic)
        => relic is BagOfPreparation or BigMushroom or BoomingConch
            or RingOfTheDrake or RingOfTheSnake or Pendulum or Pocketwatch or PollinousCore;

    private static decimal GetTurnBasedHandDrawContribution(
        RelicModel relic,
        SimulatedCombatState combat,
        int turn)
        => relic switch
        {
            BagOfPreparation when turn <= 1 => relic.DynamicVars.Cards.BaseValue,
            BigMushroom when turn == 1 => -relic.DynamicVars.Cards.BaseValue,
            BoomingConch when turn <= 1
                && combat.CurrentRoomType == RoomType.Elite
                => relic.DynamicVars.Cards.BaseValue,
            RingOfTheDrake when turn <= relic.DynamicVars["Turns"].IntValue
                => relic.DynamicVars.Cards.BaseValue,
            RingOfTheSnake when turn <= 1 => relic.DynamicVars.Cards.BaseValue,
            _ => 0m,
        };

    private static decimal GetTurnBasedMaxEnergyContribution(RelicModel relic, int turn)
        => relic switch
        {
            Bread when turn != 1 => relic.DynamicVars["GainEnergy"].BaseValue,
            PaelsFlesh when turn >= 3 => relic.DynamicVars.Energy.BaseValue,
            _ => 0m,
        };

    public static bool TriggerAfterEnergyReset(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player)
    {
        Creature owner = player.Creature;
        var context = new AfterEnergyResetMirrorContext { Simulator = simulator, Player = player };

        // Channel order affects the queue and the effects evoked when it is full.
        foreach (PowerModel power in combat.EffectivePowers())
        {
            // 层数为零的能力在原版里每一个重写都是空转：加零点星、抽零张、扣零点能量。
            // 这道门保持原来的判据，顺带让没登记的第三方能力不会为了一次空转记风险。
            if (power.Owner != owner || power.Amount <= 0)
                continue;
            AfterEnergyResetMirrors.Invoke(power, context);
            if (simulator.HasPendingChoice)
                return false;
        }
        return !simulator.HasPendingChoice;
    }

    public static bool TriggerAfterSideTurnStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CombatSide side,
        IReadOnlyList<Creature> participants,
        bool isExtraTurn = false)
    {
        foreach (Creature owner in participants)
        {
            if (!TriggerOwnerAfterSideTurnStart(simulator, combat, owner))
                return false;
        }

        if (side == CombatSide.Player && !isExtraTurn)
            return TriggerRampart(simulator, combat);
        return !simulator.HasPendingChoice;
    }

    public static void TriggerRitual(SimulatedCombatState combat, Creature owner)
    {
        int amount = combat.GetAmount<RitualPower>(owner);
        if (amount <= 0 || combat.ConsumeRitualApplicationDelay(owner))
            return;
        combat.Apply<StrengthPower>(owner, amount, owner);
    }

    private static bool TriggerOwnerAfterSideTurnStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature owner)
    {
        int biasedCognition = combat.GetAmount<BiasedCognitionPower>(owner);
        if (biasedCognition > 0)
            combat.Apply<FocusPower>(owner, -biasedCognition, owner);

        int coolant = combat.GetAmount<CoolantPower>(owner);
        if (coolant > 0 && owner.Player is { } coolantPlayer)
        {
            int distinctOrbs = simulator.State.GetPlayerCombatState(coolantPlayer).OrbQueue.Orbs
                .Select(static orb => orb.Id)
                .Distinct()
                .Count();
            simulator.GainBlock(owner, distinctOrbs * coolant, ValueProp.Unpowered);
            if (simulator.HasPendingChoice)
                return false;
        }

        int demonForm = combat.GetAmount<DemonFormPower>(owner);
        if (demonForm > 0)
            combat.Apply<StrengthPower>(owner, demonForm, owner);

        FeralPower? feral = combat.GetPower<FeralPower>(owner);
        if (feral is { Amount: > 0 })
            simulator.StateStore.Get(feral, () => new FeralPredictionState(feral)).ZeroCostAttacksPlayed = 0;

        int furnace = combat.GetAmount<FurnacePower>(owner);
        if (furnace > 0 && owner.Player is { } furnacePlayer)
        {
            Forge(simulator, furnacePlayer, furnace);
            if (simulator.HasPendingChoice)
                return false;
        }

        int neurosurge = combat.GetAmount<NeurosurgePower>(owner);
        if (neurosurge > 0)
            combat.Apply<DoomPower>(owner, neurosurge, owner);

        int noxiousFumes = combat.GetAmount<NoxiousFumesPower>(owner);
        if (noxiousFumes > 0)
        {
            foreach (Creature opponent in combat.GetOpponentsOf(owner))
            {
                if (simulator.State.IsHittable(opponent))
                    combat.Apply<PoisonPower>(opponent, noxiousFumes, owner);
            }
        }

        int prepTime = combat.GetAmount<PrepTimePower>(owner);
        if (prepTime > 0)
            combat.Apply<VigorPower>(owner, prepTime, owner);

        int reflect = combat.GetAmount<ReflectPower>(owner);
        if (reflect > 0)
            combat.SetAmount<ReflectPower>(owner, reflect - 1);

        int shadowStep = combat.GetAmount<ShadowStepPower>(owner);
        if (shadowStep > 0)
        {
            combat.Apply<DoubleDamagePower>(owner, shadowStep, owner);
            combat.SetAmount<ShadowStepPower>(owner, 0);
        }

        int wraithForm = combat.GetAmount<WraithFormPower>(owner);
        if (wraithForm > 0)
            combat.Apply<DexterityPower>(owner, -wraithForm, owner);

        int clarity = combat.GetAmount<ClarityPower>(owner);
        if (clarity > 0)
            combat.SetAmount<ClarityPower>(owner, clarity - 1);
        return !simulator.HasPendingChoice;
    }

    private static bool TriggerRampart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
    {
        foreach (RampartPower rampart in combat.EffectivePowers().OfType<RampartPower>().ToArray())
        {
            if (rampart.Amount <= 0)
                continue;
            // CoopBots multiplayer port: RampartPower is a native SIDE-turn-start hook
            // (AfterSideTurnStart -> CreatureCmd.GainBlock), so it grants once per player
            // SIDE turn. This port drives that once per SEAT, which multiplied the grant by
            // the table size; live 2026-10-06 read `E1.block expected={480} actual={120}`
            // three times in TURRET_OPERATOR_WEAK (480 = 4 x RAMPART_POWER 120).
            if (!combat.TryClaimRampartGrant(rampart.Owner, combat.RoundNumber))
                continue;
            foreach (Creature enemy in combat.Enemies)
            {
                if (enemy.Monster is TurretOperator && simulator.State.GetCreature(enemy).IsAlive)
                {
                    simulator.GainBlock(enemy, rampart.Amount, ValueProp.Unpowered);
                    if (simulator.HasPendingChoice)
                        return false;
                }
            }
        }
        return true;
    }

    public static void Forge(
        CombatPredictionSimulator simulator,
        Player player,
        int amount)
    {
        if (simulator.HasPendingChoice)
            return;

        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        bool hasUnexhaustedBlade = state.AllCards.Any(card =>
            card.Preview is SovereignBlade
            && !card.Preview.IsDupe
            && !state.ExhaustPile.Cards.Contains(card));
        if (!hasUnexhaustedBlade)
        {
            PredictedCard created = PredictedCard.Create(CanonicalModels.Card<SovereignBlade>(), player);
            ((SovereignBlade)created.MutablePreview).CreatedThroughForge = true;
            simulator.AddGeneratedCardToCombat(
                created,
                PileType.Hand,
                player,
                CardPilePosition.Bottom,
                CardGenerationResultKind.Fixed);
            if (simulator.HasPendingChoice)
                return;
        }
        foreach (PredictedCard card in state.AllCards)
        {
            if (card.Preview is not SovereignBlade preview || preview.IsDupe)
                continue;
            ((SovereignBlade)card.MutablePreview).AddDamage(amount);
        }
    }
}

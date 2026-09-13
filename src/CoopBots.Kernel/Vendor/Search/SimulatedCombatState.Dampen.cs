using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor;

internal sealed partial class SimulatedCombatState
{
    private ForkableSet<(Creature Target, Creature Caster)>? _dampenCasters;
    private Dictionary<PredictedCard, int>? _dampenOriginalUpgrades;

    public void ApplyDampen(
        CombatPredictionSimulator simulator,
        Creature target,
        Creature caster)
    {
        if ((_dampenCasters ??= []).Contains((target, caster)))
            return;
        bool firstCaster = !_dampenCasters.Any(entry => entry.Target == target);
        _dampenCasters.Add((target, caster));
        if (!firstCaster)
            return;

        Apply<DampenPower>(target, 1, caster);
        if (target.Player is not { } player)
            return;
        _dampenOriginalUpgrades ??= new Dictionary<PredictedCard, int>();
        foreach (PredictedCard card in simulator.State.GetPlayerCombatState(player).AllCards)
        {
            if (!card.Preview.IsUpgraded)
                continue;
            _dampenOriginalUpgrades.Add(card, card.Preview.CurrentUpgradeLevel);
            card.MutablePreview.DowngradeInternal();
        }
    }

    public void RemoveDampenCaster(Creature caster)
    {
        if (_dampenCasters == null)
            return;
        Creature[] affectedTargets = _dampenCasters
            .Where(entry => entry.Caster == caster)
            .Select(entry => entry.Target)
            .Distinct()
            .ToArray();
        foreach ((Creature target, Creature existingCaster) in _dampenCasters
                     .Where(entry => entry.Caster == caster)
                     .ToArray())
        {
            _dampenCasters.Remove((target, existingCaster));
        }
        foreach (Creature target in affectedTargets)
        {
            if (_dampenCasters.Any(entry => entry.Target == target))
                continue;
            SetAmount<DampenPower>(target, 0);
            RestoreDampenedCards(target);
        }
    }

    private void RestoreDampenedCards(Creature target)
    {
        if (_dampenOriginalUpgrades == null)
            return;
        foreach ((PredictedCard card, int level) in _dampenOriginalUpgrades
                     .Where(entry => entry.Key.Preview.Owner.Creature == target)
                     .ToArray())
        {
            while (card.Preview.CurrentUpgradeLevel < level)
            {
                card.MutablePreview.UpgradeInternal();
                card.MutablePreview.FinalizeUpgradeInternal();
            }
            _dampenOriginalUpgrades.Remove(card);
        }
    }

    private void CaptureDampenRootState(
        CombatPredictionSimulator simulator,
        DampenPower livePower)
    {
        object data = PowerInternalDataField.GetValue(livePower)
            ?? throw new InvalidOperationException("压制缺少内部状态。");
        Type dataType = data.GetType();
        var casters = (HashSet<Creature>)(dataType.GetField("casters")?.GetValue(data)
            ?? throw new MissingFieldException(dataType.FullName, "casters"));
        var originalUpgrades = (Dictionary<CardModel, int>)(dataType
            .GetField("downgradedCardsToOldUpgradeLevels")?.GetValue(data)
            ?? throw new MissingFieldException(dataType.FullName, "downgradedCardsToOldUpgradeLevels"));
        IEnumerable<Creature> capturedCasters = casters.Count > 0
            ? casters
            : livePower.Applier is { } applier && applier.CurrentHp > 0
                ? [applier]
                : throw new InvalidOperationException("压制存在但没有存活的施法者。");
        foreach (Creature caster in capturedCasters)
            (_dampenCasters ??= []).Add((livePower.Owner, caster));

        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(
            livePower.Owner.Player
            ?? throw new InvalidOperationException("压制目标不是玩家。"));
        HashSet<CardModel> liveCards = livePower.Owner.Player.PlayerCombatState?.AllCards.ToHashSet()
            ?? throw new InvalidOperationException("压制目标没有实机战斗牌堆。");
        foreach ((CardModel liveCard, int level) in originalUpgrades)
        {
            if (!liveCards.Contains(liveCard))
                continue;
            PredictedCard card = playerState.FindCard(liveCard)
                ?? throw new InvalidOperationException($"压制根状态找不到卡牌 {liveCard.Id.Entry}。");
            (_dampenOriginalUpgrades ??= []).Add(card, level);
        }
    }

    private void AppendDampenFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        if (_dampenCasters != null)
        {
            foreach ((Creature target, Creature caster) in _dampenCasters
                         .OrderBy(entry => entry.Target.CombatId)
                         .ThenBy(entry => entry.Caster.CombatId))
            {
                fingerprint.Add('c');
                fingerprint.Add(target.CombatId ?? uint.MaxValue);
                fingerprint.Add(caster.CombatId ?? uint.MaxValue);
            }
        }
        if (_dampenOriginalUpgrades != null)
        {
            foreach ((PredictedCard card, int level) in _dampenOriginalUpgrades
                         .OrderBy(entry => entry.Key.Preview.Id.Entry, StringComparer.Ordinal))
            {
                fingerprint.Add('g');
                fingerprint.Add(card.Preview.Id.Entry);
                fingerprint.Add(level);
            }
        }
    }

    private Dictionary<PredictedCard, int>? ForkDampenCards(PredictionForkContext context)
    {
        if (_dampenOriginalUpgrades == null)
            return null;
        Dictionary<PredictedCard, int> result = new(_dampenOriginalUpgrades.Count);
        foreach ((PredictedCard card, int level) in _dampenOriginalUpgrades)
            result.Add(ForkCard(card, context), level);
        return result;
    }
}

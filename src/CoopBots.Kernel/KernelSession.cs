using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Potions.OnUse;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    public static long LiveRevision => KernelIsolation.LiveRevision;
    public static string CaptureLiveStamp(CombatState state) => $"{state.RoundNumber}/{state.CurrentSide}\n" +
        string.Join("\n", state.Players.Select(p => $"{p.NetId}/{p.PlayerCombatState?.Phase}:" +
            ContinuationStamp.CaptureLive(state, p).StateText));

    /// <summary>
    /// The same fingerprint, but only over the players that will act. A human
    /// merely ending their turn changes their own phase and nothing the team can
    /// see — same hands, same energy, same intents — so a verdict reached before
    /// that still holds afterwards; the full stamp would hide that.
    /// </summary>
    public static string CaptureBotStamp(CombatState state, IEnumerable<Player> actors) =>
        $"{state.RoundNumber}/{state.CurrentSide}\n" +
        string.Join("\n", actors.Select(p => $"{p.NetId}:" + ContinuationStamp.CaptureLive(state, p).StateText));
    /// <summary>
    /// True when the last action resolved with at least one uncompensated
    /// environmental risk (an enemy power or relic hook we do not model). The
    /// action stays usable, but its result is an estimate: callers must not treat
    /// it as a verified kill, rescue or death.
    /// </summary>
    public bool LastActionHadEnvironmentalRisk { get; private set; }

    /// <summary>Completed player/enemy rounds advanced inside this branch.</summary>
    public int RoundsAdvanced { get; private set; }

    private string? failedBoundary;
    private readonly CombatPredictionSimulator simulator;
    private readonly HashSet<uint> processedDeaths;
    // The live state this branch descends from. Only the diagnostic state-text
    // fingerprint needs it: the intent forecast is built once from the root, the
    // same way the vendored solver builds it for every branch.
    private readonly CombatState? liveRoot;
    private SimulatedCombatState Combat => (SimulatedCombatState)simulator.State.CombatState;
    private KernelSession(CombatPredictionSimulator simulator, HashSet<uint> deaths, CombatState? liveRoot = null)
    { this.simulator = simulator; processedDeaths = deaths; this.liveRoot = liveRoot; }

    // Capture is invoked by the host on the game thread; every branch owns the
    // complete party, shared monsters, card piles, relics, powers and RNG streams.
    public static KernelSession Capture(CombatState live)
    {
        KernelIsolation.Initialize();
        using var isolation = SimulationNotificationIsolation.Enter();
        PowerDynamicVarWarmup.EnsureMaterialized(live);
        CardDynamicVarWarmup.EnsureMaterialized(live);
        return new(new CombatPredictionSimulator(new SimulatedCombatState(live, live.IterateHookListeners().ToArray())), new(), live)
        {
            ready = live.Players.Where(CombatManager.Instance.IsPlayerReadyToEndTurn).Select(p => p.NetId).ToHashSet(),
            capturedExtraTurn = CombatManager.Instance.PlayersTakingExtraTurn.Count > 0
        };
    }
    public KernelSession Fork()
    {
        if (failedBoundary is not null) throw new InvalidOperationException("Cannot fork incomplete action: " + failedBoundary);
        using var isolation = SimulationNotificationIsolation.Enter();
        return new(simulator.Fork(), new(processedDeaths), liveRoot) { ready = new(ready), capturedExtraTurn = capturedExtraTurn,
            EnemyPhaseCompleted = EnemyPhaseCompleted, LastActionHadEnvironmentalRisk = LastActionHadEnvironmentalRisk,
            RoundsAdvanced = RoundsAdvanced };
    }
    public IReadOnlyList<Creature> Enemies => Combat.HittableEnemies.Where(c => Hp(c) > 0).ToArray();

    /// <summary>
    /// Compact, conservative state key for search dedup: HP, block, energy, hand
    /// identities, enemy health, powers and readiness. Two nodes with the same
    /// key are equivalent for planning; anything not covered here keeps them
    /// distinct, so dedup can only drop true duplicates.
    /// </summary>
    public string CompactStateKey()
    {
        var text = new System.Text.StringBuilder(256);
        foreach (var player in Combat.Players)
        {
            text.Append(player.NetId).Append(':').Append(Hp(player.Creature)).Append('/')
                .Append(Block(player.Creature)).Append('/').Append(Energy(player)).Append('/')
                .Append(IsReady(player) ? '1' : '0').Append('[');
            foreach (var card in Hand(player).Select(c => c.Id.Entry + "+" + c.CurrentUpgradeLevel)
                .OrderBy(name => name, StringComparer.Ordinal))
                text.Append(card).Append(',');
            text.Append(']').Append(';');
        }
        foreach (var enemy in Enemies)
            text.Append(enemy.CombatId).Append(':').Append(Hp(enemy)).Append('/').Append(Block(enemy)).Append(';');
        foreach (var power in Combat.EffectivePowers()
            .OrderBy(p => p.Owner.CombatId).ThenBy(p => p.Id.Entry, StringComparer.Ordinal))
            text.Append(power.Owner.CombatId).Append(':').Append(power.Id.Entry).Append('=').Append(power.Amount).Append(';');
        text.Append('R').Append(Combat.RoundNumber).Append('E').Append(EnemyPhaseCompleted ? '1' : '0');
        return text.ToString();
    }
    public bool HasWon => simulator.TerminalStamp is { Outcome: CombatTerminalOutcome.Victory };
    public IReadOnlyList<Creature?> Targets(CardModel source)
    {
        var card = simulator.State.FindCard(source);
        if (card is null) return [];
        return card.Preview.TargetType switch
        {
            TargetType.AnyEnemy => Enemies.Cast<Creature?>().ToArray(),
            TargetType.AnyPlayer => Combat.Players.Where(p => Hp(p.Creature) > 0).Select(p => (Creature?)p.Creature).ToArray(),
            TargetType.AnyAlly => Combat.Players.Where(p => p != source.Owner && Hp(p.Creature) > 0).Select(p => (Creature?)p.Creature).ToArray(),
            TargetType.Self => [source.Owner.Creature],
            TargetType.Osty => Combat.GetOsty(source.Owner) is { } osty && Hp(osty) > 0 ? [osty] : [],
            TargetType.TargetedNoCreature => [],
            _ => [null]
        };
    }
    public int Hp(Creature creature) => simulator.State.GetCreature(creature).CurrentHp;
    public int MaxHp(Creature creature) => simulator.State.GetCreature(creature).MaxHp;
    public int Block(Creature creature) => simulator.State.GetCreature(creature).Block;
    public int Energy(Player player) => simulator.State.GetPlayerCombatState(player).Energy;
    public IReadOnlyList<CardModel> Hand(Player player) => simulator.State.GetPlayerCombatState(player).Hand.Cards.Select(c => c.Original).ToArray();
    public IReadOnlyList<CardModel> Exhaust(Player player) => simulator.State.GetPlayerCombatState(player).ExhaustPile.Cards.Select(c => c.Original).ToArray();
    public IReadOnlyList<CardModel> Discard(Player player) => simulator.State.GetPlayerCombatState(player).DiscardPile.Cards.Select(c => c.Original).ToArray();
    public IReadOnlyList<CardModel> DrawPile(Player player) => simulator.State.GetPlayerCombatState(player).DrawPile.Cards.Select(c => c.Original).ToArray();
    public int OrbCount(Player player) => simulator.State.GetPlayerCombatState(player).OrbQueue.Orbs.Count;
    public bool HasOsty(Player player) => Combat.GetOsty(player) is not null;
    public IReadOnlyList<PotionModel> UsablePotions(Player player) => player.Potions
        .Where(p => !p.IsQueued && !p.HasBeenRemovedFromState && p.PassesCustomUsabilityCheck
            && p.Usage is PotionUsage.CombatOnly or PotionUsage.AnyTime)
        .ToArray();
    /// <summary>
    /// Whether the kernel can simulate this potion at all. A potion with no
    /// mirrored OnUse is rejected by every branch, so the search can never pick
    /// it; the planner reports these by name instead of leaving a full slot
    /// unexplained in the log.
    /// </summary>
    public static bool CanModelPotion(PotionModel potion) => PotionOnUseMirrors.CanMirror(potion);

    /// <summary>
    /// The status card the player's next play would hand them, or null when no
    /// enemy counts their plays or the counter is not yet at its last step. Only
    /// Aeonglass's Withering Presence does this today, and the legacy scorer
    /// prices one card at a time, so it could not see that playing one more card
    /// was about to cost end-of-turn damage.
    /// </summary>
    public static CardModel? PendingHeldStatus(Player player)
    {
        try
        {
            foreach (var enemy in player.Creature.CombatState?.Enemies ?? [])
                foreach (var power in enemy.Powers)
                {
                    if (power is not WitheringPresencePower presence || power.Amount <= 0) continue;
                    if (presence.Target?.Player != player) continue;
                    if (!presence.DynamicVars.TryGetValue(WitheringPresencePower._cardsLeftKey, out var left)) continue;
                    if (left.IntValue == 1) return ModelDb.Card<Wither>().ToMutable();
                }
        }
        catch
        {
            // A mechanic we cannot read must not invent a cost.
        }
        return null;
    }
    public IReadOnlyList<Creature?> PotionTargets(PotionModel potion)
    {
        switch (potion.TargetType)
        {
            case TargetType.AllEnemies:
                return [null];
            case TargetType.AnyEnemy:
                return Enemies.Where(e => potion.IsValidTarget(e)).Cast<Creature?>().ToArray();
            case TargetType.AnyPlayer:
                return Combat.Players.Where(p => Hp(p.Creature) > 0 && potion.IsValidTarget(p.Creature))
                    .Select(p => (Creature?)p.Creature).ToArray();
            case TargetType.Self:
                return Hp(potion.Owner.Creature) > 0 ? [potion.Owner.Creature] : [];
            default:
                return [];
        }
    }
    public bool CanPlay(CardModel source)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        var card = simulator.State.FindCard(source);
        return failedBoundary is null && card is not null && CanAct(source.Owner) && Combat.CanPlayCard(simulator, card);
    }
    public bool Play(CardModel source, Creature? target, out string boundary)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        if (failedBoundary is not null) { boundary = failedBoundary; return false; }
        var card = simulator.State.FindCard(source);
        if (card is null || !CanPlay(source)) { boundary = "unplayable"; return false; }
        if (!Targets(source).Contains(target)) { boundary = "invalid-target"; return false; }
        // Any exception/choice/risk after this point leaves an unusable branch.
        // Never allow a partially resolved action to enter a subsequent fork.
        failedBoundary = "incomplete-action";
        var suppression = Combat.SuppressHistorySensitiveCardModifiers(card);
        Combat.BeginActionChoices(ChoiceCursor(source.Owner));
        var structural = false;
        try
        {
            using (Combat.BeginCardExecutionScope(processedDeaths))
            {
                if (!simulator.ManualPlay(card, target, out _)) { boundary = failedBoundary = "pending-choice"; return false; }
                // A card the snapshot cannot mirror would otherwise be dropped from
                // the search entirely. Price the shapes we can describe exactly
                // from their own declared variables instead.
                structural = StructuralCardMirror.ApplyIfUnmodeled(simulator, Combat, source, target);
                if (!CorePowerSupport.ApplyEnemyDeathPowers(simulator, Combat, Combat.KnownEnemies, processedDeaths)
                    || !CombatBeamSolver.SettleReplayActionBoundary(simulator, Combat))
                { boundary = failedBoundary = "pending-boundary"; return false; }
            }
        }
        catch (Exception error)
        {
            // A mirror that throws (e.g. an upstream assumption our multiplayer
            // capture does not satisfy) must fail closed like any other boundary.
            // Aborting here would poison the whole search instead of falling back.
            boundary = failedBoundary = "prediction-exception:" + error.GetType().Name;
            return false;
        }
        finally { Combat.RestoreHistorySensitiveCardModifiers(suppression); Combat.EndActionChoices(); }
        var requestedEnd = Combat.ConsumePlayerTurnEndRequest();
        simulator.CheckWinCondition(Combat.GetPlayerTurnNumber(source.Owner));
        // Only the played card's own semantics must be fully modeled to trust the
        // action. An unknown enemy power/relic hook is an approximation, not a
        // reason to remove the card from the plan: rejecting those made the bots
        // refuse to attack enemies whose passive hooks the snapshot lacks.
        var gaps = PredictionCoverage.Collect(simulator).Where(gap => !gap.Compensated).ToArray();
        var blocking = structural ? [] : gaps
            .Where(gap => gap.Method == "OnPlay" && gap.SourceId == source.Id.Entry)
            .ToArray();
        LastActionHadEnvironmentalRisk = gaps.Length > blocking.Length;
        boundary = blocking.Length > 0 ? "prediction-risk:" + string.Join(";", blocking.Select(gap => gap.ToString())) : "";
        failedBoundary = boundary.Length > 0 ? boundary : null;
        if (boundary.Length == 0 && requestedEnd && !HasWon) return EndTurn(source.Owner, 1, out boundary);
        return boundary.Length == 0;
    }

    /// <summary>
    /// Uses a potion inside this branch. Potions are free actions, so a potion
    /// that turns a non-lethal turn into a confirmed kill can be evaluated and
    /// confirmed by the same engine that plans the cards.
    /// </summary>
    public bool UsePotion(PotionModel potion, Creature? target, out string boundary)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        if (failedBoundary is not null) { boundary = failedBoundary; return false; }
        if (!CanAct(potion.Owner)) { boundary = "owner-ended"; return false; }
        failedBoundary = "incomplete-action";
        try
        {
            // The simulation owns cloned potions; map the live one to its clone.
            var simulated = Combat.FindPotion(potion);
            if (simulated is null)
            { boundary = failedBoundary = "potion-not-captured"; return false; }
            if (!simulator.ManualUse(simulated, target, out _))
            { boundary = failedBoundary = "invalid-potion"; return false; }
            if (simulator.HasPendingChoice)
            { boundary = failedBoundary = "pending-choice"; return false; }
            if (!CorePowerSupport.ApplyEnemyDeathPowers(simulator, Combat, Combat.KnownEnemies, processedDeaths)
                || !CombatBeamSolver.SettleReplayActionBoundary(simulator, Combat))
            { boundary = failedBoundary = "pending-boundary"; return false; }
            simulator.CheckWinCondition(Combat.GetPlayerTurnNumber(potion.Owner));
        }
        catch (Exception error)
        {
            boundary = failedBoundary = "potion-exception:" + error.GetType().Name;
            return false;
        }
        // Same rule as Play: only the potion's own OnUse must be modeled; other
        // unknowns are estimates and are surfaced through the risk flag.
        var gaps = PredictionCoverage.Collect(simulator).Where(gap => !gap.Compensated).ToArray();
        var blocking = gaps
            .Where(gap => gap.Method == "OnUse" && gap.SourceId == potion.Id.Entry)
            .ToArray();
        LastActionHadEnvironmentalRisk = gaps.Length > blocking.Length;
        boundary = blocking.Length > 0 ? "prediction-risk:" + string.Join(";", blocking.Select(gap => gap.ToString())) : "";
        failedBoundary = boundary.Length > 0 ? boundary : null;
        return boundary.Length == 0;
    }
}

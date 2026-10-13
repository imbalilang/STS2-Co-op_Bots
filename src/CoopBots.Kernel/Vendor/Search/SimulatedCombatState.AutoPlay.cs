using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Relics;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor;

internal sealed partial class SimulatedCombatState
{
    private Dictionary<Player, PredictedCard>? _lastAttackThisTurn;
    private Dictionary<Player, PredictedCard>? _lastAttackPreviousTurn;
    // Own-turn pending survives a teammate's extra turn. Effective current
    // instead follows native round/side and the entire party turn vector.
    private Dictionary<Player, HistoryCourseAttackWindow>? _historyCourseAttackWindows;
    private readonly record struct HistoryCourseAttackWindow(int Round, CombatSide Side,
        IReadOnlyDictionary<ulong, int> PlayerTurns);
    private static readonly FieldInfo HistoryPlayerTurns = typeof(MegaCrit.Sts2.Core.Combat.History.CombatHistoryEntry)
        .GetField("_playerTurnNumbers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingFieldException("CombatHistoryEntry", "_playerTurnNumbers");
    private static readonly PropertyInfo HistoryRound = typeof(MegaCrit.Sts2.Core.Combat.History.CombatHistoryEntry)
        .GetProperty("RoundNumber", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMemberException("CombatHistoryEntry.RoundNumber");
    private static readonly PropertyInfo HistorySide = typeof(MegaCrit.Sts2.Core.Combat.History.CombatHistoryEntry)
        .GetProperty("CurrentSide", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMemberException("CombatHistoryEntry.CurrentSide");

    private bool IsHistoryCurrentWindow(HistoryCourseAttackWindow window)
        => window.Round == RoundNumber && window.Side == CurrentSide
            && window.PlayerTurns.All(row => Players.FirstOrDefault(p => p.NetId == row.Key) is { } player
                && GetPlayerTurnNumber(player) == row.Value);
    private bool IsHistoryOwnerTurn(HistoryCourseAttackWindow window, Player player)
        => window.PlayerTurns.TryGetValue(player.NetId, out var turn) && turn == GetPlayerTurnNumber(player);
    private static HistoryCourseAttackWindow CaptureHistoryWindow(CardPlayFinishedEntry entry)
        => new((int)HistoryRound.GetValue(entry)!, (CombatSide)HistorySide.GetValue(entry)!,
            new Dictionary<ulong, int>((Dictionary<ulong, int>)HistoryPlayerTurns.GetValue(entry)!));

    private void RecordHistoryCourseAttack(PredictedCard card)
    {
        if (card.Preview.Type != CardType.Attack || card.Preview.IsDupe)
            return;
        (_lastAttackThisTurn ??= [])[card.Preview.Owner] = card;
        (_historyCourseAttackWindows ??= [])[card.Preview.Owner] = new(RoundNumber, CurrentSide,
            Players.ToDictionary(player => player.NetId, GetPlayerTurnNumber));
    }

    public void CommitHistoryCourseTurn(Player player)
    {
        _lastAttackPreviousTurn ??= [];
        if (_lastAttackThisTurn?.Remove(player, out PredictedCard? card) == true
            && _historyCourseAttackWindows?.TryGetValue(player, out var window) == true
            && IsHistoryOwnerTurn(window, player))
            _lastAttackPreviousTurn[player] = card;
        else
            _lastAttackPreviousTurn.Remove(player);
        _historyCourseAttackWindows?.Remove(player);
    }

    public bool TriggerScheduledAutoPlays(
        CombatPredictionSimulator simulator,
        Player player,
        int turnNumber,
        TurnStartChoiceCursor choices,
        ISet<uint> processedEnemyDeaths)
    {
        int mayhem = GetAmount<MegaCrit.Sts2.Core.Models.Powers.MayhemPower>(player.Creature);
        IReadOnlyList<PredictedCard> mayhemCards = simulator.MoveCardsForAutoPlay(
            player,
            mayhem,
            CardPilePosition.Top);
        if (HasPendingChoice)
        {
            simulator.AppendExecutionContinuation(new ScheduledAutoPlayFrame(player, turnNumber,
                processedEnemyDeaths, mayhemCards, 0));
            return true;
        }
        return ContinueScheduledAutoPlays(simulator, player, turnNumber, processedEnemyDeaths, mayhemCards, 0);
    }

    public bool TriggerWhisperingEarring(
        CombatPredictionSimulator simulator,
        Player player,
        int turnNumber,
        ISet<uint> processedEnemyDeaths)
    {
        if (turnNumber > 1)
            return true;

        foreach (WhisperingEarring relic in RelicsOf(player)
                     .OfType<WhisperingEarring>()
                     .Where(static relic => !relic.IsMelted))
        {
            TurnStartChoiceCursor vakuuChoices = TurnStartChoiceCursor.ForAutomaticPolicy(request =>
                request.Spec == null ? null : CardChoiceSupport.BuildVakuuChoice(request.Spec));
            TurnStartChoiceCursor previous = OverrideActionChoices(vakuuChoices);
            try
            {
                for (int cardsPlayed = 0; cardsPlayed < WhisperingEarring.maxCardsToPlay; cardsPlayed++)
                {
                    if (simulator.IsOverOrEnding)
                        break;
                    SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
                    PredictedCard? card = playerState.Hand.Cards
                        .FirstOrDefault(candidate => CanPlayCard(simulator, candidate));
                    if (card == null)
                        break;
                    Creature? target = simulator.GetTargetType(card) switch
                    {
                        TargetType.AnyEnemy => HittableEnemies.FirstOrDefault(),
                        TargetType.AnyAlly => simulator.Rng.CombatTargets.NextItem(
                            Allies.Where(candidate => candidate.IsPlayer
                                && !ReferenceEquals(candidate, player.Creature)
                                && simulator.State.GetCreature(candidate).IsAlive)),
                        TargetType.AnyPlayer => player.Creature,
                        _ => null,
                    };
                    // The overridden cursor answers every request with the fixed Vakuu policy, so the
                    // card's own selection resolves inside the auto-play like any other nested choice.
                    bool played = CardExecutionSupport.AutoPlay(
                            simulator,
                            this,
                            card,
                            target,
                            processedEnemyDeaths,
                            payResources: true,
                            nestedChoiceSourceId: card.Preview.Id.Entry);
                    if (HasPendingChoice)
                        return false;
                    if (!played)
                    {
                        break;
                    }

                    if (!CorePowerSupport.ApplyEnemyDeathPowers(
                            simulator,
                            this,
                            KnownEnemies,
                            processedEnemyDeaths))
                    {
                        return false;
                    }
                }
            }
            finally
            {
                RestoreActionChoices(vakuuChoices, previous);
            }
        }
        return true;
    }

    public bool AutoPlayWithChoice(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        string sourceId,
        string contextId,
        TurnStartChoiceCursor choices,
        ISet<uint> processedEnemyDeaths)
    {
        _ = choices;
        _ = CardExecutionSupport.AutoPlay(
            simulator,
            this,
            card,
            target: null,
            processedEnemyDeaths,
            nestedChoiceSourceId: sourceId,
            nestedChoiceContextId: contextId);
        return !HasPendingChoice;
    }

    // Native HistoryCourse chooses a finished, non-dupe attack. Capture the
    // current turn too: CommitHistoryCourseTurn must carry attacks already
    // paid before this root into next turn, not only attacks played by a fork.
    private PredictedCard? GetCurrentTurnAttack(CombatPredictionSimulator simulator, Player player)
    {
        var card = GetPendingTurnAttack(simulator, player);
        return card is not null && _historyCourseAttackWindows?.TryGetValue(player, out var window) == true
            && IsHistoryCurrentWindow(window) ? card : null;
    }

    private PredictedCard? GetPendingTurnAttack(CombatPredictionSimulator simulator, Player player)
    {
        if (_lastAttackThisTurn?.TryGetValue(player, out PredictedCard? predicted) == true
            && _historyCourseAttackWindows?.TryGetValue(player, out var window) == true)
            return IsHistoryOwnerTurn(window, player) ? predicted : null;
        if (_rootMaterialized) return null;
        CardPlayFinishedEntry? captured = _rootHistory.CardPlaysFinished.LastOrDefault(entry =>
            entry.CardPlay.Player == player && entry.CardPlay.Card.Type == CardType.Attack && !entry.CardPlay.Card.IsDupe
            && IsHistoryOwnerTurn(CaptureHistoryWindow(entry), player));
        if (captured is null) return null;
        predicted = CaptureHistoryCourseCard(simulator, captured.CardPlay.Card);
        (_lastAttackThisTurn ??= [])[player] = predicted;
        (_historyCourseAttackWindows ??= [])[player] = CaptureHistoryWindow(captured);
        return predicted;
    }

    private object? GetPendingTurnAttackWindow(Player player)
        => _historyCourseAttackWindows?.TryGetValue(player, out var window) == true
            && IsHistoryOwnerTurn(window, player) ? window : null;

    private static PredictedCard CaptureHistoryCourseCard(CombatPredictionSimulator simulator,
        MegaCrit.Sts2.Core.Models.CardModel original)
        => simulator.State.FindCard(original)
            ?? new PredictedCard(original, PredictionUtils.CloneCardStateForSimulation(original));

    private PredictedCard? GetPreviousTurnAttack(CombatPredictionSimulator simulator, Player player)
    {
        if (_rootMaterialized)
            return _lastAttackPreviousTurn?.GetValueOrDefault(player);
        if (_lastAttackPreviousTurn?.TryGetValue(player, out PredictedCard? predicted) == true)
            return predicted;
        CardPlayFinishedEntry? live = _rootHistory.CardPlaysFinished.LastOrDefault(entry =>
            entry.CardPlay.Player == player
            && entry.HappenedLastPlayerTurn(player)
            && entry.CardPlay.Card.Type == CardType.Attack
            && !entry.CardPlay.Card.IsDupe);
        if (live == null)
            return null;
        predicted = CaptureHistoryCourseCard(simulator, live.CardPlay.Card);
        (_lastAttackPreviousTurn ??= [])[player] = predicted;
        return predicted;
    }

    private Dictionary<Player, PredictedCard>? ForkHistoryCourseCards(
        Dictionary<Player, PredictedCard>? source,
        PredictionForkContext context)
    {
        if (source == null)
            return null;
        Dictionary<Player, PredictedCard> result = new(source.Count);
        foreach ((Player player, PredictedCard card) in source)
            result.Add(player, ForkCard(card, context));
        return result;
    }

    private void AppendAutoPlayFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        AppendTrackedAttack(ref fingerprint, 't', _lastAttackThisTurn);
        AppendTrackedAttack(ref fingerprint, 'p', _lastAttackPreviousTurn);
        if (_historyCourseAttackWindows is not null)
            foreach (var (player, window) in _historyCourseAttackWindows.OrderBy(row => row.Key.NetId))
            {
                fingerprint.Add('w'); fingerprint.Add((long)player.NetId);
                fingerprint.Add(IsHistoryCurrentWindow(window));
                fingerprint.Add(window.Round); fingerprint.Add((int)window.Side);
                foreach (var (netId, turn) in window.PlayerTurns.OrderBy(row => row.Key))
                { fingerprint.Add((long)netId); fingerprint.Add(turn); }
            }
    }

    private static void AppendTrackedAttack(
        ref StateFingerprintBuilder fingerprint,
        char marker,
        IReadOnlyDictionary<Player, PredictedCard>? cards)
    {
        if (cards == null)
            return;
        foreach ((Player player, PredictedCard card) in cards.OrderBy(entry => entry.Key.NetId))
        {
            fingerprint.Add(marker);
            fingerprint.Add((long)player.NetId);
            fingerprint.Add(card.Preview.Id.Entry);
            fingerprint.Add(card.Preview.CurrentUpgradeLevel);
        }
    }
}

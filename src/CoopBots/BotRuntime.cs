using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

public static class BotRuntime
{
    private static readonly MethodInfo EnqueueAs = AccessTools.Method(
        typeof(ActionQueueSynchronizer),
        "EnqueueAction",
        new[] { typeof(GameAction), typeof(ulong) });

    private static readonly Dictionary<ulong, (int Turn, int Actions)> TurnState = new();
    private static DateTime _nextActionAt = DateTime.MinValue;
    private static DateTime _nextErrorAt = DateTime.MinValue;
    private static int _badPlans;
    private static object? _runIdentity;
    private static object? _combatIdentity;
    private static readonly CombatPacing Pacing = new();
    private static readonly KernelCombatPlanner KernelPlanner = new();
    private static long ClockMs => Environment.TickCount64;

    internal static void ResumeAfterPause()
    {
        foreach (var id in TurnState.Keys.ToList())
            if (TurnState[id].Actions >= 80) TurnState[id] = (TurnState[id].Turn, 0);
    }

    public static void Tick()
    {
        try
        {
            var manager = RunManager.Instance;
            if (!manager.IsInProgress)
            {
                // The run just ended: the final decks are the only durable record
                // of how construction turned out, so write them before clearing.
                if (_lastState is { } finished) BuildTrace.LogAllDecks("final", finished.Players);
                Reset(); return;
            }
            var state = manager.DebugOnlyGetState();
            if (state is null) return;
            // One snapshot per act: a compact deck listing per bot, so a review
            // can compare the intended build against what was actually drafted.
            if (state.CurrentActIndex != _lastAct)
            {
                _lastAct = state.CurrentActIndex;
                BuildTrace.LogAllDecks($"act{_lastAct + 1}", state.Players);
            }
            _lastState = state;
            // Local route recommendation overlay; no-ops unless the map is open.
            MapRouteOverlay.Update(state);
            if (!ReferenceEquals(_runIdentity, state)) { Reset(); _runIdentity = state; }
            var combatIdentity = state.Players.FirstOrDefault()?.Creature.CombatState;
            if (!ReferenceEquals(_combatIdentity, combatIdentity))
            {
                _combatIdentity = combatIdentity;
                TurnState.Clear();
                _nextActionAt = DateTime.MinValue;
                _lastProgressAt = ClockMs;
                BotCooperation.Reset();
                HumanFinisherHints.Reset();
                Pacing.Reset();
                KernelPlanner.Reset(newCombat: true);
                BotChoicePlanSync.Cancel();
            }
            // Choice handlers resolve deterministically on peers; only the host submits combat/map actions.
            BotEventDriver.Tick(manager, state);
            if (BotShopDriver.Tick(manager, state)) return;
            if (!manager.NetService.IsConnected) { KernelPlanner.Reset(); _planStartedAt = 0; return; }
            BotCooperation.Refresh(state);
            if (manager.NetService.Type != NetGameType.Host) { KernelPlanner.Reset(); _planStartedAt = 0; return; }
            HumanFinisherHints.Invalidate();
            if (BotCooperation.Gate.Paused) HumanFinisherHints.Reset();
            if (DateTime.UtcNow < _nextActionAt) { KernelPlanner.Reset(); _planStartedAt = 0; return; }
            // A transient busy queue or running action must NOT discard an
            // in-flight kernel search. The search is expanded across frames, so
            // resetting it on every busy frame means it never completes: Poll
            // returns Pending forever and the bots never submit a card. Poll
            // revalidates the root itself and falls back if it truly went stale.
            if (manager.ActionExecutor.CurrentlyRunningAction is not null
                || !manager.ActionQueueSet.IsEmpty)
            {
                HumanFinisherHints.Reset();
                _planStartedAt = 0; return;
            }
            if (TryVoteOnMap(manager, state) || TryPickTreasureRelic(manager, state)) return;
            if (!CombatManager.Instance.IsInProgress || CombatManager.Instance.IsPaused || CombatManager.Instance.IsEnding
                || manager.ActionQueueSynchronizer.CombatState != MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.PlayPhase
                || BotCooperation.Gate.Paused) { KernelPlanner.Reset(); _planStartedAt = 0; return; }
            var humansFinished = BotCooperation.HumansFinished(state);
            var allHumansDown = BotCooperation.AllHumansDown(state);
            // The panel shows this while the team thinks a fight through alone.
            BotCooperation.SoloThinking = allHumansDown && KernelPlanner.IsSearching;
            BotCooperation.SoloExecuting = allHumansDown && KernelPlanner.HasPendingPlan;
            var teamDifficulty = TeamDifficulty(state);
            if (combatIdentity is not MegaCrit.Sts2.Core.Combat.CombatState combat) return;
            Pacing.Observe(combat, combat.RoundNumber, ClockMs);

            var eligible = state.Players.Where(p => BotRegistry.IsBot(p.NetId) && p.Creature.IsAlive
                && p.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && !CombatManager.Instance.IsPlayerReadyToEndTurn(p)
                && !manager.ActionQueueSet.ActionQueueIsPaused(p.NetId)).ToList();
            if (eligible.Count == 0) { KernelPlanner.Reset(); _planStartedAt = 0; return; }
            var candidates = new List<(MegaCrit.Sts2.Core.Entities.Players.Player Player, BotBrain.CombatMove Move)>();
            var botsIdle = NoPlayableCard(eligible);
            // Play out the plan the team already computed, one step per tick, with
            // no search in between. Solo takeover is the case this exists for: a
            // single very long think resolves the whole fight and the rest of it
            // is executed — end-turn steps included — without thinking again.
            // The interval must not delay a queued script, so this runs first.
            if (KernelPlanner.HasPendingPlan && KernelPlanner.TryTakeNext(humansFinished, out var step))
            {
                if (step.EndTurn)
                {
                    EnqueueAs.Invoke(manager.ActionQueueSynchronizer,
                        new object[] { new EndPlayerTurnAction(step.Player, step.Player.PlayerCombatState!.TurnNumber), step.Player.NetId });
                    BotCooperation.LastAction = $"{BotRegistry.DisplayName(step.Player.NetId)}：按计划结束回合";
                    Log.Info($"CoopBots team: {step.Player.NetId} ends the turn; plan-step");
                }
                else
                {
                    var stepCard = step.Card!;
                    if (step.Choices is { Count: > 0 }
                        && !BotChoicePlanSync.Prepare(new(step.Player, new(stepCard, step.Target, 0, "plan-step", step.Choices), 1), combat)) return;
                    EnqueueAs.Invoke(manager.ActionQueueSynchronizer,
                        new object[] { new PlayCardAction(stepCard, step.Target), step.Player.NetId });
                    TeamFocus.ObserveSubmitted(combat, new(stepCard, step.Target, 0), BotCooperation.FocusTarget);
                    BotCooperation.LastAction = $"按计划：{BotRegistry.DisplayName(step.Player.NetId)} — {stepCard.Title}";
                    Log.Info($"CoopBots team: {step.Player.NetId} plays {stepCard.Id.Entry} on {step.Target?.LogName}; plan-step");
                    var stepTurn = step.Player.PlayerCombatState!.TurnNumber;
                    if (!TurnState.TryGetValue(step.Player.NetId, out var stepProgress) || stepProgress.Turn != stepTurn)
                        TurnState[step.Player.NetId] = stepProgress = (stepTurn, 0);
                    TurnState[step.Player.NetId] = (stepTurn, stepProgress.Actions + 1);
                }
                _lastProgressAt = ClockMs;
                _idleLast = false;
                Pacing.MarkAction(ClockMs, 0);
                return;
            }
            // The interval throttles only the idle re-check, never the action
            // itself: an in-flight search keeps its frames, and a team with a
            // playable card acts as soon as the plan is ready. Waiting the whole
            // interval after thinking had already finished is what made the bots
            // feel slow in real play.
            if ((botsIdle || _idleLast) && !KernelPlanner.IsSearching
                && !Pacing.IsDue(ClockMs, humansFinished, BotCooperation.Gate.Paused,
                    teamDifficulty.CardIntervalMs())) { _planStartedAt = 0; return; }
            // Stamp the start of this plan once: the search spans several frames,
            // and the next wait is shortened by how long it actually took.
            if (_planStartedAt == 0) _planStartedAt = ClockMs;
            // The request describes the REAL post-bot board, never a future search leaf.
            // Do not ask a human to abandon defence while bots still have actions pending.
            HumanFinisherHints.Tick(combat, NoPlayableCards(eligible) && !humansFinished, ClockMs);
            var faulty = new HashSet<ulong>();
            foreach (var player in eligible)
            {
                try
                {
                    var turn = player.PlayerCombatState!.TurnNumber;
                    if (!TurnState.TryGetValue(player.NetId, out var progress) || progress.Turn != turn)
                        TurnState[player.NetId] = progress = (turn, 0);
                    if (progress.Actions >= 80)
                    {
                        BotCooperation.Gate.TogglePause();
                        BotCooperation.LastAction = "行动过多，已暂停，请检查局面";
                        Log.Warn("CoopBots action limit reached; paused instead of silently ending the turn.");
                        return;
                    }
                }
                catch (Exception error) { faulty.Add(player.NetId); Report(error); }
            }
            // A kernel/JIT failure must never abort the whole tick: it would skip
            // the legacy planner below and leave the bots with no action at all.
            KernelCombatPlanner.Status kernelStatus;
            TeamCombatPlanner.Decision? joint;
            try
            {
                // The team spends most of a long human turn with nothing playable:
                // every bot has already spent its energy and is waiting for the
                // human to finish. The last run answered "no action" in 43.9% of
                // all searches, which is exactly that state. Searching (and
                // capturing) here cannot find anything, so skip the poll and let
                // the potion and end-turn paths below run instead.
                if (botsIdle)
                {
                    if (++_idleSkips % 40 == 0) ReportIdleIdle();
                    KernelPlanner.Reset();
                    kernelStatus = KernelCombatPlanner.Status.Fallback;
                    joint = null;
                }
                else if (BotChoicePlanSync.Resume(combat, out joint))
                    kernelStatus = joint is null ? KernelCombatPlanner.Status.Pending : KernelCombatPlanner.Status.Ready;
                else kernelStatus = KernelPlanner.Poll(combat, eligible, manager.ActionQueueSet.NextActionId,
                    BotCooperation.FocusTarget, humansFinished, teamDifficulty, out joint, allHumansDown);
            }
            catch (Exception error)
            {
                Report(error);
                KernelPlanner.Reset();
                kernelStatus = KernelCombatPlanner.Status.Fallback;
                joint = null;
            }
            if (kernelStatus == KernelCombatPlanner.Status.Pending) return;
            // HumanFinisherHints alone publishes verified, current-board requests.
            // Free potion actions are taken before card planning: a rescue potion
            // wins; otherwise a kernel-confirmed proactive potion (e.g. buff then
            // burst) is used now and the cards are replanned against real state.
            // Defensive bottles are judged last: until every bot is out of cards a
            // card may still cover the hit, which would make the "full dose" partly
            // wasted. Lethal ones are judged now, so the throw lands before the
            // team commits its plays to an enemy the potion removes outright.
            var earlyPotion = BotPotionPlanner.Choose(eligible, state.Players, NoPlayableCards(eligible));
            if (earlyPotion is null && joint is null && KernelPlanner.ConfirmedPotion is { } proactivePotion)
            {
                proactivePotion.Potion.EnqueueManualUse(proactivePotion.Target);
                BotCooperation.LastAction = "主动用药：" + proactivePotion.Potion.Id.Entry;
                Log.Info($"CoopBots proactive potion: {proactivePotion.Potion.Id.Entry}; {proactivePotion.Reason}");
                _lastProgressAt = ClockMs;
                _idleLast = false;
                Pacing.MarkAction(ClockMs, _planStartedAt);
                return;
            }
            if (earlyPotion is null && joint is null && KernelPlanner.ConfirmedEndTurn is { } endingPlan
                && humansFinished && eligible.Contains(endingPlan))
            {
                EnqueueAs.Invoke(manager.ActionQueueSynchronizer,
                    new object[] { new EndPlayerTurnAction(endingPlan, endingPlan.PlayerCombatState!.TurnNumber), endingPlan.NetId });
                _lastProgressAt = ClockMs;
                _idleLast = false;
                Pacing.MarkAction(ClockMs, _planStartedAt);
                return;
            }
            // The kernel owns the action only when it actually produced a plan.
            // Any other outcome (fallback, or a completed search that recommends
            // nothing) must still let the legacy planner propose moves, otherwise
            // the bots can sit idle for the whole fight.
            var legacyPlan = kernelStatus != KernelCombatPlanner.Status.Ready || joint is null;
            if (legacyPlan)
                foreach (var player in eligible)
                {
                    try
                    {
                        var progress = TurnState[player.NetId];
                        var move = BotBrain.ChooseCombatMove(player, progress.Actions);
                        if (move.HasValue) candidates.Add((player, move.Value));
                    }
                    catch (Exception error) { faulty.Add(player.NetId); Report(error); }
                }
            // Potions are evaluated before ending any bot turn, and after every resolved action.
            var potion = earlyPotion;
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (BotCooperation.FocusTarget.HasValue && candidate.Move.Target?.CombatId == BotCooperation.FocusTarget)
                    candidates[i] = (candidate.Player, candidate.Move with { Score = candidate.Move.Score + 45 });
            }
            var planningFailed = false;
            try
            {
                if (legacyPlan)
                    joint = TeamCombatPlanner.Choose(eligible, state.Players, BotCooperation.FocusTarget);
            }
            catch (Exception error) { Report(error); planningFailed = true; }
            if (joint is null && !planningFailed)
            {
                // Add the partial-effect scan on top of the normal candidates. It
                // used to clear them, so when no team plan existed the only moves
                // left were unmodeled cards and the bots could sit idle.
                foreach (var player in eligible)
                {
                    try
                    {
                        var move = BotBrain.ChooseEffectFallback(player, TurnState[player.NetId].Actions);
                        if (move.HasValue
                            && !candidates.Any(c => c.Player == player && ReferenceEquals(c.Move.Card, move.Value.Card)))
                            candidates.Add((player, move.Value));
                    }
                    catch (Exception error) { faulty.Add(player.NetId); Report(error); }
                }
            }
            var decision = TeamCoordinator.Select(joint, potion, candidates, planningFailed);
            if (decision.Player is null && decision.Potion is null && ClockMs - _lastProgressAt >= StallMs)
            {
                // Deliberate team waits are respected, but never forever: after a
                // grace period take the best positive individual move so a combat
                // where the team planner abstains cannot stall the whole fight.
                var salvage = candidates.Where(c => c.Move.Score > 0 && c.Move.Card is not null)
                    .OrderByDescending(c => c.Move.Score).ThenBy(c => c.Player.NetId).FirstOrDefault();
                if (salvage.Player is not null)
                {
                    Log.Warn($"CoopBots team wait exceeded {StallMs}ms; playing {salvage.Move.Card.Id.Entry} to keep the fight moving.");
                    decision = new TeamCoordinator.Decision(salvage.Player, salvage.Move, null, "超时接管");
                }
            }
            var best = (Player: decision.Player, Move: decision.Move.GetValueOrDefault());
            potion = decision.Potion;
            if (potion is not null)
            {
                potion.Potion.EnqueueManualUse(potion.Target);
                BotCooperation.LastAction = $"{decision.Branch}：{potion.Potion.Id.Entry}";
                Log.Info($"CoopBots potion: {potion.Potion.Id.Entry}; {potion.Reason}");
                _lastProgressAt = ClockMs;
                Pacing.MarkAction(ClockMs, _planStartedAt);
                return;
            }
            if (best.Player is not null && best.Move.Card is { } chosen)
            {
                // Do not queue a multi-step script. Recompute after this action and any human intervention.
                // A plan can still go stale between planning and submission; never
                // return silently or the bots idle for the whole fight.
                if (!chosen.CanPlayTargeting(best.Move.Target)
                    || !best.Player.PlayerCombatState!.Hand.Cards.Contains(chosen))
                {
                    Log.Warn($"CoopBots discarded an unplayable plan ({chosen.Id.Entry}); replanning.");
                    Pacing.MarkAction(ClockMs, _planStartedAt);
                    if (++_badPlans >= 3 && humansFinished && eligible.FirstOrDefault() is { } stuck)
                    {
                        _badPlans = 0;
                        Log.Warn("CoopBots repeated an unplayable plan; ending the bot turn to keep the fight moving.");
                        EnqueueAs.Invoke(manager.ActionQueueSynchronizer,
                            new object[] { new EndPlayerTurnAction(stuck, stuck.PlayerCombatState!.TurnNumber), stuck.NetId });
                    }
                    return;
                }
                _badPlans = 0;
                if (best.Move.Choices is { Count: > 0 }
                    && !BotChoicePlanSync.Prepare(new(best.Player, best.Move, joint?.PlannedCards ?? 1), combat)) return;
                EnqueueAs.Invoke(manager.ActionQueueSynchronizer,
                    new object[] { new PlayCardAction(chosen, best.Move.Target), best.Player.NetId });
                TeamFocus.ObserveSubmitted(combat, best.Move, BotCooperation.FocusTarget);
                var progress = TurnState[best.Player.NetId];
                TurnState[best.Player.NetId] = (progress.Turn, progress.Actions + 1);
                BotCooperation.LastAction = $"{decision.Branch}：{BotRegistry.DisplayName(best.Player.NetId)} — {chosen.Title}";
                if (joint is not null && joint.HpSaved > 0)
                    BotCooperation.LastAction += $"\n计划预计减少全队战损 {joint.HpSaved:F0} 点";
                // The review could count "40 of 61 plays came from the legacy
                // planner" but not say why the kernel abstained. Naming the
                // fallback kind on the move that followed is what makes the
                // difference between "out of cards" and "found nothing better".
                var viaKernel = kernelStatus == KernelCombatPlanner.Status.Ready && joint is not null;
                var attribution = viaKernel ? "kernel" : KernelPlanner.LastNoAction switch
                {
                    KernelCombatPlanner.NoActionKind.Idle => "legacy:kernel-idle",
                    KernelCombatPlanner.NoActionKind.Tie => "legacy:kernel-tie",
                    KernelCombatPlanner.NoActionKind.Boundary => "legacy:kernel-boundary",
                    _ => "legacy",
                };
                Log.Info($"CoopBots team: {best.Player.NetId} plays {chosen.Id.Entry} on {best.Move.Target?.LogName}; "
                    + $"score={best.Move.Score:F1}; via={attribution}; {best.Move.Reason}");
                _lastProgressAt = ClockMs;
                _idleLast = false;
                Pacing.MarkAction(ClockMs, _planStartedAt);
                return;
            }
            if (faulty.Count > 0)
            {
                BotCooperation.Gate.TogglePause();
                Log.Warn("CoopBots paused after strategy failure; other bots were allowed to finish useful actions first.");
                return;
            }
            if (!humansFinished)
            {
                // Keep turns open for later human energy/draw effects; don't repeatedly plan at frame rate.
                ReportIdle(kernelStatus, legacyPlan, humansFinished);
                _idleLast = true;
                Pacing.MarkAction(ClockMs, _planStartedAt);
                return;
            }
            // Nothing left to play and every human is done: end the whole team's
            // turn in one tick. Ending one bot per interval made the team trickle
            // out over several seconds while the player waited.
            if (eligible.Count > 0)
            {
                foreach (var ending in eligible)
                {
                    EnqueueAs.Invoke(manager.ActionQueueSynchronizer,
                        new object[] { new EndPlayerTurnAction(ending, ending.PlayerCombatState!.TurnNumber), ending.NetId });
                    BotCooperation.LastAction = $"{BotRegistry.DisplayName(ending.NetId)}：结束回合";
                }
                _lastProgressAt = ClockMs;
                _idleLast = false;
                Pacing.MarkAction(ClockMs, _planStartedAt);
            }
        }
        catch (Exception error) { Report(error); _nextActionAt = DateTime.UtcNow.AddSeconds(2); }
    }

    // The team plans as one unit, so the whole search runs at the strongest tier
    // present. No bots at all (or a human-only lobby) falls back to Pro pacing.
    private static BotDifficulty TeamDifficulty(RunState state)
        => state.Players.Where(p => BotRegistry.IsBot(p.NetId))
            .Select(p => BotRegistry.Difficulty(p.NetId))
            .DefaultIfEmpty(BotDifficulty.Pro)
            .Strongest();

    private static long _lastProgressAt;
    private static long _planStartedAt;
    // Ticks skipped because no eligible bot held a playable card. Non-zero and
    // large means the idle guard is doing its job; it should never grow while a
    // bot still has a card to play.
    private static long _idleSkips;
    // The previous tick reached its end with nothing to do. The pacing interval
    // then applies to the re-check, so an idle team is not re-planned per frame.
    private static bool _idleLast;
    // Last run state seen while in progress, so the run-end branch can still dump
    // the final decks after RunManager has already stopped reporting them.
    private static RunState? _lastState;
    private static int _lastAct = -1;
    private static DateTime _nextIdleSkipLogAt = DateTime.MinValue;
    private static void ReportIdleIdle()
    {
        if (DateTime.UtcNow < _nextIdleSkipLogAt) return;
        _nextIdleSkipLogAt = DateTime.UtcNow.AddSeconds(10);
        Log.Info($"CoopBots idle-skip: no playable card; skipped={_idleSkips} kernel polls this combat.");
    }

    // Only true when no eligible bot can play a card AND none holds a usable
    // combat potion: a potion can still be worth using (a rescue, or the energy
    // that unlocks a hand), so those cases keep the full search.
    private static bool NoPlayableCards(IReadOnlyList<MegaCrit.Sts2.Core.Entities.Players.Player> eligible)
    {
        try { return eligible.All(p => !p.PlayerCombatState!.Hand.Cards.Any(c => c.CanPlay())); }
        catch (Exception error) { Report(error); return false; }
    }

    private static bool NoPlayableCard(IReadOnlyList<MegaCrit.Sts2.Core.Entities.Players.Player> eligible)
    {
        foreach (var player in eligible)
        {
            try
            {
                foreach (var card in player.PlayerCombatState!.Hand.Cards)
                    if (card.CanPlay()) return false;
                if (player.CanUseOrRemovePotions && player.Potions.Any(potion => !potion.IsQueued
                    && !potion.HasBeenRemovedFromState && potion.PassesCustomUsabilityCheck
                    && potion.Usage is MegaCrit.Sts2.Core.Entities.Potions.PotionUsage.CombatOnly
                        or MegaCrit.Sts2.Core.Entities.Potions.PotionUsage.AnyTime)) return false;
            }
            catch (Exception error) { Report(error); return false; }
        }
        return true;
    }
    private const long StallMs = 6000;
    private static DateTime _nextIdleLogAt = DateTime.MinValue;
    private static void ReportIdle(KernelCombatPlanner.Status status, bool legacy, bool humansFinished)
    {
        if (DateTime.UtcNow < _nextIdleLogAt) return;
        _nextIdleLogAt = DateTime.UtcNow.AddSeconds(10);
        Log.Info($"CoopBots idle: kernel={status}, legacy={legacy}, humansFinished={humansFinished}, "
            + $"paused={BotCooperation.Gate.Paused}");
    }

    private static void Report(Exception error)
    {
        if (DateTime.UtcNow < _nextErrorAt) return;
        _nextErrorAt = DateTime.UtcNow.AddSeconds(10);
        Log.Error($"CoopBots runtime error (rate limited, not permanently suppressed): {error.GetBaseException()}");
    }

    private static void Reset()
    {
        if (_runIdentity is null && _combatIdentity is null) return;
        _runIdentity = null; _combatIdentity = null;
        TurnState.Clear(); _nextActionAt = _nextErrorAt = DateTime.MinValue; _badPlans = 0; _lastProgressAt = ClockMs;
        _planStartedAt = 0;
        _idleSkips = 0;
        _idleLast = false;
        _lastState = null;
        _lastAct = -1;
        BotCooperation.Reset(); BotEventDriver.Reset();
        HumanFinisherHints.Reset();
        Pacing.Reset();
        KernelPlanner.Reset(newCombat: true);
        BotChoicePlanSync.Cancel();
        CrystalSphereSync.Reset();
    }
    private static bool TryPickTreasureRelic(RunManager manager, RunState state)
    {
        var synchronizer = manager.TreasureRoomRelicSynchronizer;
        var relics = synchronizer.CurrentRelics;
        if (relics is null)
            return false;

        // Let humans inspect the advice and register their preferences before bots vote.
        if (state.Players.Where(p => !BotRegistry.IsBot(p.NetId))
            .Any(p => !synchronizer.GetPlayerVote(p).voteReceived)) return false;

        var waitingBots = state.Players.Where(p => BotRegistry.IsBot(p.NetId) && !synchronizer.GetPlayerVote(p).voteReceived).ToList();
        var reserved = state.Players.Select(p => synchronizer.GetPlayerVote(p))
            .Where(v => v.voteReceived && v.index.HasValue).Select(v => v.index!.Value).ToHashSet();
        var assignment = TeamCoordinator.AssignRelics(waitingBots, relics, reserved);
        foreach (var player in waitingBots)
        {
            if (synchronizer.GetPlayerVote(player).voteReceived)
                continue;
            var index = assignment[player.NetId];
            var action = new PickRelicAction(player, index < 0 ? null : index);
            EnqueueAs.Invoke(manager.ActionQueueSynchronizer, new object[] { action, player.NetId });
            _nextActionAt = DateTime.UtcNow.AddMilliseconds(220);
            return true;
        }

        return false;
    }

    private static bool TryVoteOnMap(RunManager manager, RunState state)
    {
        // Most rooms open the shared map as an overlay when they finish. In that
        // state CurrentRoom is still CombatRoom/EventRoom/etc., not MapRoom. The
        // map screen itself is the authoritative signal that votes are accepted.
        var mapScreen = NMapScreen.Instance;
        if (mapScreen is null || !mapScreen.IsOpen || !mapScreen.IsTravelEnabled || mapScreen.IsTraveling)
            return false;

        foreach (var player in state.Players.Where(player => BotRegistry.IsBot(player.NetId)))
        {
            if (manager.MapSelectionSynchronizer.GetVote(player).HasValue)
                continue;

            var humanVotes = state.Players.Where(p => !BotRegistry.IsBot(p.NetId))
                .Select(p => manager.MapSelectionSynchronizer.GetVote(p)).ToList();
            if (humanVotes.Any(v => v?.mapGenerationCount != manager.MapSelectionSynchronizer.MapGenerationCount)) return false;
            var bots = state.Players.Where(p => BotRegistry.IsBot(p.NetId)).ToList();
            var humanVote = MultiHumanCooperation.Vote(humanVotes, bots.Count, bots.IndexOf(player));
            if (!humanVote.HasValue || humanVote.Value.mapGenerationCount != manager.MapSelectionSynchronizer.MapGenerationCount)
                return false;
            var vote = humanVote.Value;
            var action = new VoteForMapCoordAction(player, state.MapLocation, vote);
            Log.Info($"CoopBots: queued map vote for {BotRegistry.DisplayName(player.NetId)} " +
                $"from {state.MapLocation} to {vote.coord} (generation {vote.mapGenerationCount}).");
            EnqueueAs.Invoke(manager.ActionQueueSynchronizer, new object[] { action, player.NetId });
            _nextActionAt = DateTime.UtcNow.AddMilliseconds(220);
            return true;
        }

        return false;
    }

}

[HarmonyPatch(typeof(NRun), nameof(NRun._Process))]
internal static class RunProcessPatch
{
    private static void Postfix() => BotRuntime.Tick();
}

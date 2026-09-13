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
            if (!manager.IsInProgress) { Reset(); return; }
            var state = manager.DebugOnlyGetState();
            if (state is null) return;
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
            if (DateTime.UtcNow < _nextActionAt) { KernelPlanner.Reset(); _planStartedAt = 0; return; }
            // A transient busy queue or running action must NOT discard an
            // in-flight kernel search. The search is expanded across frames, so
            // resetting it on every busy frame means it never completes: Poll
            // returns Pending forever and the bots never submit a card. Poll
            // revalidates the root itself and falls back if it truly went stale.
            if (manager.ActionExecutor.CurrentlyRunningAction is not null
                || !manager.ActionQueueSet.IsEmpty) { _planStartedAt = 0; return; }
            if (TryVoteOnMap(manager, state) || TryPickTreasureRelic(manager, state)) return;
            if (!CombatManager.Instance.IsInProgress || CombatManager.Instance.IsPaused || CombatManager.Instance.IsEnding
                || manager.ActionQueueSynchronizer.CombatState != MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.PlayPhase
                || BotCooperation.Gate.Paused) { KernelPlanner.Reset(); _planStartedAt = 0; return; }
            var humansFinished = BotCooperation.HumansFinished(state);
            if (combatIdentity is not MegaCrit.Sts2.Core.Combat.CombatState combat) return;
            Pacing.Observe(combat, combat.RoundNumber, ClockMs);
            if (!Pacing.IsDue(ClockMs, humansFinished, BotCooperation.Gate.Paused)) { _planStartedAt = 0; return; }
            // Stamp the start of this plan once: the search spans several frames,
            // and the next wait is shortened by how long it actually took.
            if (_planStartedAt == 0) _planStartedAt = ClockMs;

            var eligible = state.Players.Where(p => BotRegistry.IsBot(p.NetId) && p.Creature.IsAlive
                && p.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && !CombatManager.Instance.IsPlayerReadyToEndTurn(p)
                && !manager.ActionQueueSet.ActionQueueIsPaused(p.NetId)).ToList();
            var candidates = new List<(MegaCrit.Sts2.Core.Entities.Players.Player Player, BotBrain.CombatMove Move)>();
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
                if (NoPlayableCard(eligible))
                {
                    if (++_idleSkips % 40 == 0) ReportIdleIdle();
                    KernelPlanner.Reset();
                    kernelStatus = KernelCombatPlanner.Status.Fallback;
                    joint = null;
                }
                else if (BotChoicePlanSync.Resume(combat, out joint))
                    kernelStatus = joint is null ? KernelCombatPlanner.Status.Pending : KernelCombatPlanner.Status.Ready;
                else kernelStatus = KernelPlanner.Poll(combat, eligible, manager.ActionQueueSet.NextActionId,
                    BotCooperation.FocusTarget, humansFinished, out joint);
            }
            catch (Exception error)
            {
                Report(error);
                KernelPlanner.Reset();
                kernelStatus = KernelCombatPlanner.Status.Fallback;
                joint = null;
            }
            if (kernelStatus == KernelCombatPlanner.Status.Pending) return;
            // Ask the humans for the finishing damage when the plan needs it.
            if (KernelPlanner.Callout is { } callout)
                BotCalloutSync.Publish(callout.Text, callout.Enemy.CombatId ?? 0);
            // Free potion actions are taken before card planning: a rescue potion
            // wins; otherwise a kernel-confirmed proactive potion (e.g. buff then
            // burst) is used now and the cards are replanned against real state.
            var earlyPotion = BotPotionPlanner.Choose(eligible, state.Players);
            if (earlyPotion is null && joint is null && KernelPlanner.ConfirmedPotion is { } proactivePotion)
            {
                proactivePotion.Potion.EnqueueManualUse(proactivePotion.Target);
                BotCooperation.LastAction = "主动用药：" + proactivePotion.Potion.Id.Entry;
                Log.Info($"CoopBots proactive potion: {proactivePotion.Potion.Id.Entry}; {proactivePotion.Reason}");
                _lastProgressAt = ClockMs;
                Pacing.MarkAction(ClockMs, _planStartedAt);
                return;
            }
            if (earlyPotion is null && joint is null && KernelPlanner.ConfirmedEndTurn is { } endingPlan
                && humansFinished && eligible.Contains(endingPlan))
            {
                EnqueueAs.Invoke(manager.ActionQueueSynchronizer,
                    new object[] { new EndPlayerTurnAction(endingPlan, endingPlan.PlayerCombatState!.TurnNumber), endingPlan.NetId });
                _lastProgressAt = ClockMs;
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
                        var move = BotBrain.ShouldEndTurnEarly(player, progress.Actions)
                            ? null : BotBrain.ChooseCombatMove(player, progress.Actions);
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
                BotCooperation.LastAction = "使用救援药水：" + potion.Potion.Id.Entry;
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
                Log.Info($"CoopBots team: {best.Player.NetId} plays {chosen.Id.Entry} on {best.Move.Target?.LogName}; score={best.Move.Score:F1}; {best.Move.Reason}");
                _lastProgressAt = ClockMs;
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
                Pacing.MarkAction(ClockMs, _planStartedAt);
                return;
            }
            // Waiting is distinct from ready-to-end. No bot ends while another has a useful action.
            var ending = eligible.FirstOrDefault();
            if (ending is not null)
            {
                EnqueueAs.Invoke(manager.ActionQueueSynchronizer,
                    new object[] { new EndPlayerTurnAction(ending, ending.PlayerCombatState!.TurnNumber), ending.NetId });
                _lastProgressAt = ClockMs;
                Pacing.MarkAction(ClockMs, _planStartedAt);
            }
        }
        catch (Exception error) { Report(error); _nextActionAt = DateTime.UtcNow.AddSeconds(2); }
    }

    private static long _lastProgressAt;
    private static long _planStartedAt;
    // Ticks skipped because no eligible bot held a playable card. Non-zero and
    // large means the idle guard is doing its job; it should never grow while a
    // bot still has a card to play.
    private static long _idleSkips;
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
        BotCooperation.Reset(); BotEventDriver.Reset();
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

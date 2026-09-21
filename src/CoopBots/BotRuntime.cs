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
        // 无人值守实机测试的开局。必须在下面那些早退之前调用 —— 主菜单正是本方法
        // 一路早退的状态，而开局恰恰要在那时候发生。没设 COOPBOTS_LIVE_TEST=1 时是一次
        // 环境变量读取，正式包里不产生任何行为。
        LiveTestAutoStart.Tick();
        if (LiveTestMenuTicker.Armed) LiveTestAutoStart.BotTicks++;
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
            // A new run re-opens the fight panel collapsed. This is the run
            // transition, not the combat one below: an expand the player made
            // during a run has to survive the rest of that run's fights.
            if (!ReferenceEquals(_runIdentity, state))
            {
                Reset();
                _runIdentity = state;
                BotCooperation.OnRunStart();
                // The instant-mode switch is gone; this only repairs a preference it left
                // stuck at Instant, which is a hard freeze on the PunchOff event.
                InstantModeGuard.RepairIfStuck();
            }
            // A handed-over seat resolves its card rewards through the game's own
            // selector hatch, which has to be in place before the first reward opens.
            // Idempotent, so the per-frame cost is one null check.
            if (AutoPilot.Any) BotCardSelector.EnsureInstalled();
            var combatIdentity = state.Players.FirstOrDefault()?.Creature.CombatState;
            if (!ReferenceEquals(_combatIdentity, combatIdentity))
            {
                _combatIdentity = combatIdentity;
                TurnState.Clear();
                // SETTLE BEFORE ACTING ON A NEW SCENE.
                //
                // This used to be `DateTime.MinValue`, i.e. "act on the very first frame the
                // new scene exists". Measured live 2026-09-21: after the act-1 boss the run went
                // boss → act 2 combat with the boss's own rewards screen never handled — no
                // CardReward/GoldReward lines between the boss and the next fight, and the party
                // entered act 2 at 7/80 and 12/80. The bot is racing the UI: it submits as soon
                // as a state exists, before the game has finished putting screens up, and the
                // room-proceed driver sits ahead of the rewards driver in Tick's order.
                //
                // Two seconds is the user's number. It is applied AT TRANSITIONS rather than
                // before every choice: a blanket 2 s on each action would add two seconds to
                // every card on top of the 1.5 s pacing, while the race this fixes only exists
                // in the frames right after a scene changes.
                _nextActionAt = DateTime.UtcNow.AddMilliseconds(SceneSettleMs);
                _lastProgressAt = ClockMs;
                BotCooperation.Reset();
                HumanFinisherHints.Reset();
                Pacing.Reset();
                KernelPlanner.Reset(newCombat: true);
                BotChoicePlanSync.Cancel();
            }
            // ALL NON-COMBAT DECISIONS ARE THROTTLED — the user's 2 s, applied here because the
            // act-transition bug lives in this block.
            //
            // The transition is driven by the TERMINAL REWARD SCREEN'S CONTINUE BUTTON, which
            // BotRoomProceedDriver presses (see the note further down about MoveToNextAct). Press
            // it too early and the transition — including the NPC event that restores 80 % of
            // lost HP — is gone: measured 2026-09-21, the run went boss -> rewards -> act 2
            // combat with NO event room between, the party entering act 2 at 7/87. The block
            // below the combat gate is a wall of "act on whatever exists this frame", and this
            // gate is what stops the bot from being faster than the screens it depends on.
            var nonCombatDue = DateTime.UtcNow >= _nextNonCombatAt;
            if (nonCombatDue)
            {
                _nextNonCombatAt = DateTime.UtcNow.AddMilliseconds(NonCombatSettleMs);
                BotEventDriver.Tick(manager, state);
                // Room-level proceeds are local UI, not synchronizer choices, so they sit
                // outside BotEventDriver and everything else that talks to a synchronizer.
                BotEventProceedDriver.TryProceed(manager, state);
                BotRoomProceedDriver.TryProceed(manager, state);
                BotRewardsScreenDriver.TryDrive(manager, state);
                BotTreasureChestDriver.TryOpenChest(manager, state);
                if (BotShopDriver.Tick(manager, state)) return;
            }
            if (!manager.NetService.IsConnected) { KernelPlanner.Reset(); _planStartedAt = 0; return; }
            // Publish the planner's searching state before the panel renders, so the
            // "searching for the optimal line" hint is on screen for exactly the frames
            // the search is in flight.
            BotCooperation.Searching = KernelPlanner.IsSearching;
            BotCooperation.SearchingElapsedMs = KernelPlanner.SearchingElapsedMs;
            BotCooperation.SearchingBudgetMs = KernelPlanner.SearchingWallBudgetMs;
            BotCooperation.SearchingRounds = KernelPlanner.SearchingRoundsReached;
            BotCooperation.Refresh(state);
            // Singleplayer counts as "this machine submits" — see RunAuthority, which is the
            // one place this rule now lives. This gate used to accept only Host, so the bots
            // did nothing for an entire automatically-played run; the same misreading then
            // cost a whole live round in the shop (see RunAuthority's remarks).
            if (!RunAuthority.IsSubmittingPeer(manager))
            { KernelPlanner.Reset(); _planStartedAt = 0; return; }
            HumanFinisherHints.Invalidate();
            if (BotCooperation.Gate.Paused) HumanFinisherHints.Reset();
            if (DateTime.UtcNow < _nextActionAt)
            {
                // Pacing throttles submission, not planning. Preserve an in-flight
                // search and its deployed continuation across the short cooldown;
                // resetting here caused one full search per card in a scripted fight.
                _planStartedAt = 0; return;
            }
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
            // BotActChangeDriver is deliberately NOT called. Its gate was "the current
            // room is a Boss room", which is true for the whole boss fight, and
            // ActChangeSynchronizer.OnPlayerReady marks a seat ready as soon as its vote
            // is accepted — so four driven seats could have marked the whole team ready
            // and fired MoveToNextAct() mid-fight. It was also redundant: the act
            // transition is triggered by the terminal reward screen's continue button,
            // which BotRoomProceedDriver already presses, and NRewardsScreen's own
            // handler calls SetLocalPlayerReady from there.
            if (nonCombatDue
                && (TryVoteOnMap(manager, state) || TryPickTreasureRelic(manager, state))) return;
            if (!CombatManager.Instance.IsInProgress || CombatManager.Instance.IsPaused || CombatManager.Instance.IsEnding
                || manager.ActionQueueSynchronizer.CombatState != MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.PlayPhase
                || BotCooperation.Gate.Paused) { KernelPlanner.Reset(); _planStartedAt = 0; return; }
            var humansFinished = BotCooperation.HumansFinished(state);
            var teamDifficulty = TeamDifficulty(state);
            if (combatIdentity is not MegaCrit.Sts2.Core.Combat.CombatState combat) return;
            Pacing.Observe(combat, combat.RoundNumber, ClockMs);

            // Drives, not IsBot: a seat handed over from the room panel is planned
            // and submitted exactly like a synthetic bot. Only the host reaches
            // this line at all, so "who is allowed to drive whom" is already
            // settled above and needs no second check here.
            var eligible = state.Players.Where(p => AutoPilot.Drives(p.NetId) && p.Creature.IsAlive
                && p.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && !CombatManager.Instance.IsPlayerReadyToEndTurn(p)
                && !manager.ActionQueueSet.ActionQueueIsPaused(p.NetId)).ToList();
            if (eligible.Count == 0) { KernelPlanner.Reset(); _planStartedAt = 0; return; }
            var candidates = new List<(MegaCrit.Sts2.Core.Entities.Players.Player Player, BotBrain.CombatMove Move)>();
            // These candidates are the LEGACY planner's input and are recomputed from the
            // real board every tick. The kernel above is not the same: on an all-bot table
            // it keeps a searched plan and replays it across cards and turns
            // (KernelCombatPlanner.TryEmitFromPlan), re-searching only when the board stops
            // matching the plan's own turn boundaries. That is safe precisely because no
            // human can act between two of our cards — see the gate in TryEmitFromPlan.
            // A live script step is an action even when no card is playable. The next step
            // is usually the plan's own EndTurn precisely BECAUSE the team is out of
            // cards, so `NoAvailableCombatAction` alone reads "time to end the turn" as
            // "nothing to do" and the idle shortcut below skips the poll — the plan's
            // EndTurn then never gets emitted, the cursor never advances past it, and the
            // turn ends without the plan's own boundary bookkeeping ever being consumed.
            // This is not a workaround for Reset() any more (Reset cannot reach the script
            // — see KernelContinuation); it is the condition for the script's own next
            // step to be considered an action at all.
            var botsIdle = NoAvailableCombatAction(eligible) && !KernelPlanner.HasPendingStep;
            // The interval throttles only the idle re-check, never the action
            // itself: an in-flight search keeps its frames, and a team with a
            // playable card acts as soon as the plan is ready. Waiting the whole
            // interval after thinking had already finished is what made the bots
            // feel slow in real play.
            if ((botsIdle || _idleLast) && !KernelPlanner.IsSearching
                && !Pacing.IsDue(ClockMs, humansFinished, BotCooperation.Gate.Paused,
                    Math.Max(ActionSettleMs, teamDifficulty.CardIntervalMs()))) { _planStartedAt = 0; return; }
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
                // Never take the idle shortcut while a search is in flight. The guard at
                // the top of this method already refuses to early-return in that case, so
                // without this the flow fell straight through to Reset() — which threw
                // the search away, reported Fallback, and let the potion path drink a
                // bottle for a board the search had never finished thinking about. The
                // panel had already published `Searching` for this tick, so the player
                // watched "searching for the optimal line" while the bottle was used.
                if (botsIdle && !KernelPlanner.IsSearching)
                {
                    if (++_idleSkips % 40 == 0) ReportIdleIdle();
                    KernelPlanner.Reset();
                    // No poll this tick, so nothing else will clear the last
                    // decision, and the paths below would read it as if it were
                    // still current. The reviewed boss fight hung on exactly that:
                    // the confirmed bottle had already been drunk, and re-enqueuing
                    // it threw once every two seconds instead of ending the turn.
                    KernelPlanner.DiscardConfirmation();
                    kernelStatus = KernelCombatPlanner.Status.Fallback;
                    joint = null;
                }
                else if (BotChoicePlanSync.Resume(combat, out joint))
                    kernelStatus = joint is null ? KernelCombatPlanner.Status.Pending : KernelCombatPlanner.Status.Ready;
                else kernelStatus = KernelPlanner.Poll(combat, eligible, manager.ActionQueueSet.NextActionId,
                    BotCooperation.FocusTarget, humansFinished, teamDifficulty, out joint);
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
            // The kernel owns the combat whenever it actually produced a plan; only when it
            // did NOT (fallback, or a completed search that recommends nothing) may the
            // standalone planners propose anything. `candidates` below already uses this
            // gate — the rescue heuristic did not, so it ran on every tick and could outbid
            // a step the search had already priced and whose index it had already consumed.
            // A SCRIPT IN FLIGHT OWNS THE ACTION — "the kernel answered Ready this tick" is not
            // the same condition, and the difference leaks whole bottles and cards into the middle
            // of a script.
            //
            // `kernelStatus != Ready` is true on EVERY tick where the previous step is still
            // resolving (TryEmitFromPlan returns the waiting-for-step Pending there), so this gate
            // let the standalone planners speak mid-script:
            //   * `BotPotionPlanner` drank a bottle the script never priced;
            //   * `BotBrain.ChooseCombatMove` proposed cards the script had not chosen;
            //   * `TeamCombatPlanner.Choose` built a `joint` that TeamCoordinator.Select could pick
            //     over the script's own step.
            // Any of the three changes the hand the script's LATER steps were priced against.
            //
            // Measured live 2026-09-20 (A10 4-bot, BYGONE_EFFIGY_ELITE): the log reads
            //   CoopBots potion: POWER_POTION; card-generation-tempo     ← not a kernel-plan step
            //   plan step drift: field=R.card_generation …               ← the draw RNG diverged
            //   plan step refused: index=17/42 card=INFLAME card-left-hand
            // INFLAME is a POWER card, which is exactly what a Power Potion puts in hand: the bottle
            // was drunk while the script sat at step 10/42, and seven steps later the script could
            // not be executed. The plan was dropped and the rest of the fight went to the legacy
            // planner — the failure the script existed to prevent.
            //
            // Refusals still fall back to the legacy planner as designed: a refusal CALLS
            // DropContinuation, so HasPendingStep goes false and the planners speak on the next tick.
            var scriptInFlight = KernelPlanner.HasPendingStep;
            var legacyPlan = !scriptInFlight && (kernelStatus != KernelCombatPlanner.Status.Ready || joint is null);
            var earlyPotion = legacyPlan
                ? BotPotionPlanner.Choose(eligible, state.Players, NoPlayableCards(eligible))
                : null;
            // The PLAN's own potion step wins over the standalone rescue heuristic. Both
            // are bottles, but the plan's was priced by the search AND has already consumed
            // a plan index — taking the heuristic instead left that step unexecuted with
            // its index advanced, so the very next tick failed the L1 check and dropped the
            // plan. That is the "the plan resets right after drinking" symptom: not a failed
            // check, but a step that was skipped by a second decision source.
            if (joint is null && KernelPlanner.ConfirmedPotion is { } proactivePotion)
            {
                if (TryUsePotion(proactivePotion.Potion, proactivePotion.Target))
                {
                    BotCooperation.LastAction = "按剧本用药：" + proactivePotion.Potion.Id.Entry;
                    Log.Info($"CoopBots scripted potion: {proactivePotion.Potion.Id.Entry}; {proactivePotion.Reason}");
                    _lastProgressAt = ClockMs;
                    _idleLast = false;
                    Pacing.MarkAction(ClockMs, _planStartedAt);
                    return;
                }
                // The game refused the bottle, so the confirmation is spent. Drop it
                // and fall through: the rest of the tick still plays a card or ends
                // the turn instead of retrying a plan that cannot be carried out.
                KernelPlanner.DiscardConfirmation();
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
            // A refusal here falls through to the end-turn path rather than
            // returning: the planner picked this bottle from the live belt, so the
            // only way it fails is the game rejecting the action, and letting that
            // cost the team its turn is the failure this guards against.
            if (potion is not null && TryUsePotion(potion.Potion, potion.Target))
            {
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
                BotCooperation.LastAction = $"{decision.Branch}："
                    + $"{AutoPilot.Label(best.Player.NetId, best.Player.NetId == MegaCrit.Sts2.Core.Context.LocalContext.NetId ? "你" : null)}"
                    + $" — {chosen.Title}";
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
            //
            // Iterated over the driven seats rather than `eligible`. `eligible` excludes
            // anyone already marked ready to end their turn, so on a team that was
            // entirely ready this block did nothing AND said nothing — which is the
            // same thing the log shows for a genuine stall. The empty case is now
            // reported, because a turn that quietly never ends is the most expensive
            // thing this planner can do.
            // Phase is kept from `eligible` on purpose: without it this would also end
            // the turn for a seat the phase gate excludes, which is a live behaviour
            // change for mixed tables that have nothing to do with seat handover.
            var ending = state.Players.Where(p => AutoPilot.Drives(p.NetId) && p.Creature.IsAlive
                && p.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && !CombatManager.Instance.IsPlayerReadyToEndTurn(p)).ToList();
            if (ending.Count == 0)
            {
                if (Environment.TickCount64 >= _nextIdleEndLogAt)
                {
                    _nextIdleEndLogAt = Environment.TickCount64 + 2000;
                    Log.Info($"CoopBots end turn declined: nothing to play and no driven seat left to end "
                        + $"(driven={state.Players.Count(p => AutoPilot.Drives(p.NetId))}, "
                        + $"eligible={eligible.Count}, humansFinished={humansFinished}, "
                        + $"kernel={kernelStatus}, noAction={KernelPlanner.LastNoAction})");
                }
                ReportIdle(kernelStatus, legacyPlan, humansFinished);
                _idleLast = true;
                Pacing.MarkAction(ClockMs, _planStartedAt);
                return;
            }
            foreach (var seat in ending)
            {
                EnqueueAs.Invoke(manager.ActionQueueSynchronizer,
                    new object[] { new EndPlayerTurnAction(seat, seat.PlayerCombatState!.TurnNumber), seat.NetId });
                BotCooperation.LastAction = $"{AutoPilot.Label(seat.NetId)}：结束回合";
            }
            Log.Info($"CoopBots end turn: nothing to play; ended the turn for {ending.Count} seat(s)");
            _lastProgressAt = ClockMs;
            _idleLast = false;
            Pacing.MarkAction(ClockMs, _planStartedAt);
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
    private static long _nextIdleEndLogAt;
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

    // Cards only. Callers that also care about potions use
    // NoAvailableCombatAction: a usable potion keeps the planner searching.
    private static bool NoPlayableCards(IReadOnlyList<MegaCrit.Sts2.Core.Entities.Players.Player> eligible)
    {
        try { return eligible.All(p => !p.PlayerCombatState!.Hand.Cards.Any(c => c.CanPlay())); }
        catch (Exception error) { Report(error); return false; }
    }

    // Named for the action, not the card: this is also false when a bot holds a
    // usable combat potion, because a potion can rescue or unlock a hand and must
    // keep the planner running. Energy alone never makes this true (zero-cost and
    // X-cost cards are playable at zero energy).
    private static bool NoAvailableCombatAction(IReadOnlyList<MegaCrit.Sts2.Core.Entities.Players.Player> eligible)
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
    /// <summary>
    /// MINIMUM WAIT BEFORE EVERY CHOICE. The user's number, and their instruction was "before
    /// every choice", not only at transitions — so it is applied as a FLOOR on the per-card
    /// pacing interval rather than only in the scene-change branch below.
    ///
    /// WHAT IT COSTS: difficulty paces cards at 1500 ms (Pro). A 2000 ms floor therefore adds
    /// 0.5 s to every Pro action, and it overrides CombatPacing's speed-up entirely — that
    /// speed-up multiplies the interval by 0.0 once every human has finished, i.e. "the bots
    /// are the only ones left, let them play as fast as they can think", and a floor cancels it.
    /// Bot-only fights get measurably slower; that is the trade the user asked for, and it is
    /// one constant to undo.
    /// </summary>
    private const int ActionSettleMs = 2000;
    /// <summary>How long the bot lets a freshly-entered scene settle before it acts.</summary>
    private const int SceneSettleMs = 2000;
    /// <summary>
    /// The same 2 s for everything the bot decides OUTSIDE combat: map votes, room proceeds,
    /// reward screens, chests, shops, events. The combat pacing below is separate — a fight has
    /// its own 1.5 s card cadence and its own gate.
    /// </summary>
    private const int NonCombatSettleMs = 2000;
    private static DateTime _nextNonCombatAt = DateTime.MinValue;
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

    // EnqueueManualUse validates the bottle against its owner's live potion slots
    // and throws when it is not there, so the ownership test below is the same one
    // the game makes — checked first so a stale reference costs a skipped action
    // instead of an exception. A search can confirm a bottle that the belt no
    // longer holds: the plan outlives the state it was computed from.
    private static bool TryUsePotion(MegaCrit.Sts2.Core.Models.PotionModel potion,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? target)
    {
        try
        {
            if (potion.Owner is not { } owner || !owner.Potions.Contains(potion))
            {
                Log.Warn($"CoopBots dropped a potion plan it no longer holds: {potion.Id.Entry}.");
                return false;
            }
            potion.EnqueueManualUse(target);
            return true;
        }
        catch (Exception error)
        {
            Report(error);
            return false;
        }
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
        // A handed-over seat is not a human here: waiting for it would wait forever.
        if (state.Players.Where(p => !AutoPilot.Drives(p.NetId))
            .Any(p => !synchronizer.GetPlayerVote(p).voteReceived)) return false;

        var waitingBots = state.Players.Where(p => AutoPilot.Drives(p.NetId) && !synchronizer.GetPlayerVote(p).voteReceived).ToList();
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

    /// <summary>
    /// Says WHY the map vote has not been cast, throttled. The map is the one decision point
    /// where every failure mode is a bare `return false`, so a run that stalls there produces
    /// no log at all beyond the periodic `where` line — which is exactly how round 8 spent four
    /// minutes on an open map with `mapTravel=True` and no diagnosis. `where` already says the
    /// screen is ready; this says which of the six waits is holding the vote up.
    /// </summary>
    private static void ReportMapWait(string why)
    {
        try
        {
            if (Environment.TickCount64 < _nextMapWaitLogAt) return;
            _nextMapWaitLogAt = Environment.TickCount64 + 10_000;
            Log.Info($"CoopBots map: waiting — {why}; "
                + $"generation={RunManager.Instance?.MapSelectionSynchronizer?.MapGenerationCount.ToString() ?? "?"}");
        }
        catch (Exception error) { Report(error); }
    }

    private static long _nextMapWaitLogAt;

    private static bool TryVoteOnMap(RunManager manager, RunState state)
    {
        // Most rooms open the shared map as an overlay when they finish. In that
        // state CurrentRoom is still CombatRoom/EventRoom/etc., not MapRoom. The
        // map screen itself is the authoritative signal that votes are accepted.
        var mapScreen = NMapScreen.Instance;
        if (mapScreen is null || !mapScreen.IsOpen || !mapScreen.IsTravelEnabled || mapScreen.IsTraveling)
            return false;

        foreach (var player in state.Players.Where(player => AutoPilot.Drives(player.NetId)))
        {
            if (manager.MapSelectionSynchronizer.GetVote(player) is { } alreadyVoted)
            {
                // The skip must be "already voted for THIS generation", not "has ever voted".
                // A stale vote from the previous generation makes this true forever, so the
                // seat is never re-voted and the map never advances. Measured live 2026-09-20
                // (round 8): after the act-3 boss the map stood open with travel enabled for
                // four minutes, `generation 4` appeared ZERO times in the whole log, and the
                // last vote was generation 3. Behaviour is unchanged here — this only says
                // which case it is when the generations disagree.
                if (alreadyVoted.mapGenerationCount == manager.MapSelectionSynchronizer.MapGenerationCount)
                    continue;
                ReportMapWait($"stale-own-vote(gen={alreadyVoted.mapGenerationCount})");
                continue;
            }

            var humanVotes = state.Players.Where(p => !AutoPilot.Drives(p.NetId))
                .Select(p => manager.MapSelectionSynchronizer.GetVote(p)).ToList();
            if (humanVotes.Any(v => v?.mapGenerationCount != manager.MapSelectionSynchronizer.MapGenerationCount))
            { ReportMapWait("human-vote-not-this-generation"); return false; }
            var bots = state.Players.Where(p => AutoPilot.Drives(p.NetId)).ToList();
            var humanVote = MultiHumanCooperation.Vote(humanVotes, bots.Count, bots.IndexOf(player));
            if (!humanVote.HasValue)
            {
                // Nobody is left to follow. Vote() copies the humans' distribution
                // and answers null when there is none, so a table where every seat
                // has been handed over would abstain forever and the map would never
                // advance — the one shape this feature exists to create. Fall back to
                // the same route planner the human overlay draws, so each seat votes
                // for the node its own deck actually wants.
                if (humanVotes.Count > 0) { ReportMapWait("following-a-human-vote"); return false; }
                // Say out loud which nodes the router is refusing to consider, and why.
                // A silent skip is indistinguishable from "the router did not want it",
                // and this one is not a preference — it is a room the game cannot build
                // (see RoutePlanner.IsBuildable). Logged here rather than in the planner so
                // it lands once per seat per map generation instead of once per frame.
                foreach (var (skipped, why) in RoutePlanner.UnbuildableChildren(state))
                    Log.Info($"CoopBots route: refusing to travel to {skipped} — {why}; "
                        + "entering it throws and strands the run (vanilla, not route preference).");
                if (RoutePlanner.Plan(state, player) is not { Path.Count: > 1 } route)
                {
                    // Name EVERY refused node, not just the start's children: the blocking one is
                    // usually deeper, and a silent children-log nearly cleared the exclusion that
                    // actually caused this stall (see RoutePlanner.UnbuildableNodes).
                    var refused = RoutePlanner.UnbuildableNodes(state).ToList();
                    ReportMapWait(refused.Count == 0
                        ? "no-route(route-planner-returned-null, no-unbuildable-node-found)"
                        : "no-route(blocked-by: " + string.Join(" ",
                            refused.Select(r => $"{r.Coord}={r.Reason}")) + ")");
                    return false;
                }
                humanVote = new MapVote
                {
                    mapGenerationCount = manager.MapSelectionSynchronizer.MapGenerationCount,
                    coord = route.Path[1].coord,
                };
            }
            if (humanVote.Value.mapGenerationCount != manager.MapSelectionSynchronizer.MapGenerationCount)
            { ReportMapWait("vote-generation-race"); return false; }
            var vote = humanVote.Value;
            var action = new VoteForMapCoordAction(player, state.MapLocation, vote);
            Log.Info($"CoopBots: queued map vote for {AutoPilot.Label(player.NetId)} " +
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





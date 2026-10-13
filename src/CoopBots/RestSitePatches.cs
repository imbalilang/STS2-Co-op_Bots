using System.Reflection;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;

namespace CoopBots;

[HarmonyPatch(typeof(RestSiteRoom), nameof(RestSiteRoom.EnterInternal))]
internal static partial class BotRestSitePatch
{
    private static readonly MethodInfo ChooseOption = AccessTools.Method(
        typeof(RestSiteSynchronizer),
        "ChooseOption",
        new[] { typeof(Player), typeof(int) });
    private static readonly MethodInfo FinishLocalOption = AccessTools.Method(
        typeof(NRestSiteRoom), "AfterSelectingOptionAsync", new[] { typeof(RestSiteOption) });

    private static void Postfix(Task __result, IRunState? runState)
    {
        if (runState is null) return;
        _ = CompleteTracked(runState, __result, async () =>
        {
            if (!ReferenceEquals(runState, RunManager.Instance.DebugOnlyGetState())) return;
            ResetPlanning();
            var players = runState.Players.Where(player => AutoPilot.Drives(player.NetId)).ToArray();
            if (players.Length == 0) return;
            var ordered = await ConstructionDecision.RunAsync(players[0], () => InvestmentOrder(players).ToArray(),
                () => { if (!ReferenceEquals(runState, RunManager.Instance.DebugOnlyGetState())) throw new OperationCanceledException(); return "camp-order"; });
            await CompleteInOrder(ordered,
                player => CompleteForBot(RunManager.Instance.RestSiteSynchronizer, player));
        });
    }

    internal static async Task CompleteTracked(IRunState state, Task entry, Func<Task> decide)
    {
        using var work = RoomWork.Begin((object?)state.CurrentRoom ?? state);
        await CompleteAfterEntry(entry, decide);
    }

    // BeginRestSite only creates options. EnterInternal then builds the room
    // and awaits AfterRoomEntered (including EternalFeather's free healing).
    // Merely using an async-method postfix is insufficient: await its Task.
    internal static async Task CompleteAfterEntry(Task entry, Func<Task> decide)
    {
        try { await entry; await decide(); }
        catch (Exception failure) { Log.Error($"CoopBots rest-site entry failed: {failure.GetBaseException()}"); }
    }

    // Each choice can await healing/target selection. The next seat must see
    // its result, not plan against the same pre-heal snapshot. No reservations
    // remain to double-count healing already reflected in CurrentHp.
    internal static async Task CompleteInOrder(IEnumerable<Player> players, Func<Player, Task> complete)
    {
        foreach (var player in players) await complete(player);
    }

    // Spend the least valuable upgrade opportunity first. Its MEND can let an
    // engine owner upgrade later, instead of that owner healing itself before
    // any teammate gets a chance to help. Choices still await native effects.
    internal static IEnumerable<Player> InvestmentOrder(IEnumerable<Player> players)
        => players.OrderBy(p => p.Deck.Cards.Select(c => HumanCoopAdvisor.UpgradeValue(c, p))
            .DefaultIfEmpty(0).Max()).ThenBy(p => p.NetId).ToArray();

    // Mirror NRestSiteButton.SelectOption: disable, broadcast/choose, finish the
    // local room UI on success, restore choices on failure. Choosing through the
    // synchronizer alone does not enable map travel. Await the native UI task
    // too so MiniatureTent's next choice cannot race its previous animation.
    internal static async Task<bool> CompleteChoice(bool local, Func<Task<bool>> chooseLocal,
        Func<Task<bool>> chooseRemote, Action disable, Func<Task> finish, Action restore)
    {
        if (!local) return await chooseRemote();
        disable();
        var success = false;
        try
        {
            success = await chooseLocal();
            if (success) await finish();
            return success;
        }
        finally { if (!success) restore(); }
    }

    private static async Task CompleteForBot(RestSiteSynchronizer synchronizer, Player player)
    {
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var options = synchronizer.GetOptionsForPlayer(player);
                if (options.Count == 0)
                    return;

                // Walk EVERY usable option, not just the preferred one plus a single
                // fallback taken only when the call returns false. MendRestSiteOption
                // .OnSelect() THROWS when it has no target, and a throw escaped straight
                // to the catch below — leaving this seat with its options unspent. One
                // unchosen seat blocks the whole camp: RestSiteSynchronizer completes a
                // rest site only once every player has no options left, so the run parked
                // there permanently (observed as an elite camp that never advanced).
                var plan = await ConstructionDecision.RunAsync(player,
                    () => ChooseTeam(player, options, synchronizer.GetOptionsForPlayer), () => {
                    if (!AutoPilot.Drives(player.NetId) || !ReferenceEquals(options, synchronizer.GetOptionsForPlayer(player)))
                        throw new OperationCanceledException();
                    return string.Join(',', options.Select(o => o.OptionId));
                });
                var attempts = options
                    .Select((option, index) => (option, index))
                    .Where(pair => IsUsable(pair.option))
                    .Where(pair => pair.option.OptionId != "MEND" || MendRunnable(player))
                    .OrderByDescending(pair => pair.index == plan.Index)
                    .ThenBy(pair => pair.index)
                    .ToList();
                var advanced = false;
                foreach (var (option, index) in attempts)
                {
                    try
                    {
                        if (index == plan.Index && option.OptionId == "MEND")
                            PlannedPatients[player.NetId] = plan.Patient;
                        var local = LocalContext.IsMe(player);
                        var room = local ? NRestSiteRoom.Instance : null;
                        if (local && room is null)
                            throw new InvalidOperationException("Local rest-site room is not available.");
                        if (await CompleteChoice(local,
                            () => synchronizer.ChooseLocalOption(index),
                            () => (Task<bool>)ChooseOption.Invoke(synchronizer, new object[] { player, index })!,
                            () => room!.DisableOptions(),
                            () => (Task)FinishLocalOption.Invoke(room, new object[] { option })!,
                            () => { if (Godot.GodotObject.IsInstanceValid(room)) room!.EnableOptions(); }))
                        {
                            if (local) Log.Info($"CoopBots camp: local completion finished option={option.OptionId}.");
                            advanced = true;
                            break;
                        }
                    }
                    catch (Exception optionFailure)
                    {
                        Log.Warn($"CoopBots rest-site option {option.OptionId} refused for "
                            + $"{player.NetId}: {optionFailure.GetBaseException().Message}");
                    }
                    finally { PlannedPatients.Remove(player.NetId); }
                }
                if (!advanced)
                    return;
            }
        }
        catch (Exception exception)
        {
            Log.Error($"CoopBots rest-site choice failed for bot {player.NetId}: {exception.GetBaseException()}");
        }
    }

    private const double NeedAtFull = 1;
    private const double NeedAtDeath = 3;
    private const double HealPerHp = 0.4;
    // 极低血量：线性 need 曲线把"从 0 回到第 1 点"定在 3x，但它仍认为血条上剩下的
    // 那几点和已经涨回来的部分同价。低于这个比例时，真正的问题不是"血条没满"而是
    // "下一个房间第一下就死"，所以再加一档乘数（见 SurvivalPressure）。
    private const double CriticalHpRatio = 0.35;
    private const double CriticalHpBonus = 2.0;
    // 最后一个篝火：见 CampHealWeight。
    private const double LastCampHealBonus = 1.5;
    // The last-camp bonus pays for a seat that still NEEDS the healing, so it is
    // gated on the HEAL TARGET's own HP ratio, not the deciding seat's. Measured
    // live 2026-10-01: at 87.5% HP a seat chose MEND for a teammate missing only
    // 10 of a 24-point heal, and that mostly-overflowing heal (26.7) outscored a
    // real 19.4-point smith on the strength of this bonus.
    internal const double LastCampHealTargetRatio = 0.75;
    private const double WorthwhileUpgrade = 2.5;
    private const double HumanHealWeight = 1.2;

    /// <summary>
    /// OWNER DECISION 2026-10-02: a self-preference bias. Healing a TEAMMATE is
    /// worth 75% of the same recovery on the deciding seat, because the seat that
    /// dies is the seat that is gone and the deciding seat is the one that has to
    /// live with the choice. Measured live 2026-10-02: a handed-over seat at 37/70
    /// (52.9%) chose MEND for a teammate at 39/87 (44.8%) by 50.8 to 49.3 — the
    /// teammate's ratio is lower so the need curve paid more per point, and a 87-max
    /// seat's 30% heal is simply bigger than a 70-max one's. Nothing in the model
    /// said the deciding seat's own survival matters more. It does.
    ///
    /// Applied inside <see cref="MendValue"/> so the target picker and the option
    /// score use the same discounted number.
    /// </summary>
    private const double AllyHealDiscount = 0.75;
    internal static void ResetPlanning()
    {
        PlannedPatients.Clear();
    }

    /// <summary>
    /// Whether this seat can execute MEND at all.
    ///
    /// <para>
    /// <c>MendRestSiteOption.OnSelect</c> has two native branches. For
    /// <c>LocalContext.IsMe(owner)</c> it drives the TARGETING UI — <c>NRestSiteRoom.Instance</c>,
    /// <c>NTargetManager.StartTargeting</c> and an <c>await SelectionFinished()</c> that only a
    /// click resolves. For every other seat it asks
    /// <c>PlayerChoiceSynchronizer.WaitForRemoteChoice</c>, which this mod already answers for a
    /// driven seat (<c>BotRemoteChoicePatch</c> → <c>BotRewardDriver.ChoiceForWait</c>).
    /// </para>
    /// <para>
    /// A driven LOCAL seat used to have neither: no human to click a target and no UI to click it
    /// in. <see cref="BotMendLocalSeatPatch"/> now intercepts that exact branch, picks the same
    /// target <see cref="BestMendTarget"/> already uses for arm valuation, calls
    /// <c>PlayerChoiceSynchronizer.SyncLocalChoice</c> so every peer heals the same player, and
    /// runs the native heal/hook body. MEND is therefore executable by every seat.
    /// </para>
    /// </summary>
    internal static bool MendRunnable(Player player)
    {
        _ = player;
        return true;
    }

    /// <summary>
    /// The heal + post-heal hook half of <c>MendRestSiteOption.OnSelect</c>, shared so the
    /// local-targeting replacement cannot drift from the native effect body. Target resolution
    /// and choice syncing stay in the caller.
    /// </summary>
    internal static async Task<bool> ApplyMend(Player owner, Player? target)
    {
        if (target is null) return false;
        await CreatureCmd.Heal(target.Creature, MendRestSiteOption.GetHealAmount(target), true);
        await Hook.AfterRestSiteHeal(owner.RunState, target, false);
        return true;
    }

    // The same target policy is used by valuation and the actual remote choice.
    // A lower HP percentage is not necessarily a better heal: RegalPillow,
    // max HP, overflow, and a human teammate's weight all change its payoff.
    internal static Player? BestMendTarget(Player owner)
        => PlannedPatients.TryGetValue(owner.NetId, out var patient)
            ? owner.RunState.Players.FirstOrDefault(p => p.NetId == patient && p.Creature.IsAlive)
            : owner.RunState.Players
            .Where(candidate => candidate.NetId != owner.NetId && candidate.Creature.IsAlive)
            .OrderByDescending(candidate => MendValue(owner, candidate))
            .ThenBy(candidate => candidate.NetId)
            .FirstOrDefault();

    /// <summary>
    /// 极低血量的额外一档，只乘在**这个席位自己的** HEAL 选项上。
    ///
    /// <para>
    /// 为什么不用 <see cref="HealValue"/> 自带的 need 曲线：那条曲线把"从 0 回到第 1 点"
    /// 定在 3x，但它对"再挨一下必死"的血量没有额外区分度。实机 2026-09-30（四人 A10，
    /// Boss 前最后一个篝火）：B3 席位 `hp=7/62`（11%）仍然给 `SMITH` 打了 47.3、
    /// 只给 `HEAL` 打 33.1，于是带着 9 血进终局 Boss，第一回合末被打死。
    /// </para>
    /// <para>
    /// 只作用于 HEAL、不作用于 MEND 的**病人选择**：那是另一个决策（"这一口治疗放谁身上
    /// 收益最大"），已有自己的夹具与 R2 变异证据；在没有新证据前不该被顺带改掉。
    /// </para>
    /// </summary>
    internal static double SurvivalPressure(decimal currentHp, decimal maxHp)
    {
        if (maxHp <= 0) return 1;
        var current = Math.Clamp((double)(currentHp / maxHp), 0, 1);
        return 1 + (CriticalHpBonus - 1) * Math.Clamp((CriticalHpRatio - current) / CriticalHpRatio, 0, 1);
    }

    /// <summary>
    /// The per-HP weight both recovery options are priced with.
    ///
    /// 上游怎么做：CombatSolver 没有营地模型（单机没有"多席位共享一个篝火"的决策）。
    /// 我们怎么做：<see cref="UpcomingRisk"/> 已经为 Boss 抬过一次档，这里在
    /// <see cref="RunDepth.LastCampBeforeBoss"/> 时再乘一个 <see cref="LastCampHealBonus"/>；
    /// 极低血量的那档加在 <see cref="HealValue"/> 里，所以 HEAL 和 MEND 同时受益。
    /// 为什么：实机 2026-09-30 四人 A10 团灭在第三层 Boss AEONGLASS_BOSS。Boss 前最后
    /// 一个篝火（B3，铁甲以外的静默席位）：`hp=7/62`、`lastCamp=True`、
    /// `scores=[HEAL:33.1,SMITH:47.3,MEND:42.2]` —— 它在 11% 血、下一层就是终局 Boss 时
    /// 去升级了一张牌，进 Boss 时 9 血，第一回合末就被打死；全队也是 80/36/42/9 进的本。
    /// 旧曲线里"再挨一下必死"的血量并不比"掉了三分之一的血"更值钱。
    /// </summary>
    /// <param name="lastCampBeforeBoss">
    /// Test seam: the live value comes from <see cref="RunDepth.LastCampBeforeBoss"/>,
    /// which needs a real map. Passing it explicitly lets the regression fixture pin the
    /// multiplier itself instead of the map reader.
    /// </param>
    internal static double CampHealWeight(Player owner, bool? lastCampBeforeBoss = null,
        double targetHpRatio = 1)
        => HealPerHp * UpcomingRisk(owner)
            * ((lastCampBeforeBoss ?? RunDepth.LastCampBeforeBoss(owner))
                && targetHpRatio < LastCampHealTargetRatio ? LastCampHealBonus : 1);

    private static double MendValue(Player owner, Player patient)
        => (HealValue(HealAmount(patient, self: false), patient.Creature.CurrentHp, patient.Creature.MaxHp,
                CampHealWeight(owner, null, patient.Creature.CurrentHp / (double)Math.Max(1, patient.Creature.MaxHp))
                    * (AutoPilot.Drives(patient.NetId) ? 1 : HumanHealWeight))
            + SharedHealHookValue(patient)) * AllyHealDiscount;

    // StoneHumidifier runs after either self-healing or receiving MEND. Its
    // native MaxHpVar grants max HP and matching current HP (2 points per HP,
    // the same growth unit used by ChosenCheese and camp Cook).
    private static double SharedHealHookValue(Player player)
        => player.Relics.Where(r => r.GetType().Name == "StoneHumidifier")
            .Sum(r => (double)r.DynamicVars.MaxHp.BaseValue * 2);

    // Only ExecuteRestSiteHeal offers these rewards. MEND calls the heal hook
    // but does not offer heal rewards. Both Tea Sets trigger on room entry,
    // so they add no marginal value to choosing HEAL over another camp action.
    internal static double SelfHealRewardValue(Player player)
        => (player.Relics.Any(r => r.GetType().Name == "DreamCatcher") ? 12 : 0)
            + (player.Relics.Any(r => r.GetType().Name == "TinyMailbox") ? 12 : 0)
            + SharedHealHookValue(player);

    /// <summary>
    /// Selected action and patient, retained together for diagnostics. Choices
    /// execute sequentially and always read actual HP; no heal is reserved.
    /// </summary>
    internal readonly record struct CampPlan(int Index, ulong Patient, decimal Heal);

    private static CampPlan Choose(Player player, IReadOnlyList<RestSiteOption> options)
    {
        using var evaluation = DeckStrength.BeginEvaluation();
        decimal EffectiveHp(Player candidate) => candidate.Creature.CurrentHp;
        // Mend must not even be OFFERED when it has no target: MendRestSiteOption.OnSelect()
        // dereferences one, and offering an option that cannot resolve is how a seat ended up
        // unchosen and stalled the whole camp. Resolved before `usable` so the filter sees it.
        var mendTarget = BestMendTarget(player);
        var usable = options
            .Select((option, index) => (option, index))
            .Where(pair => IsUsable(pair.option))
            .Where(pair => pair.option.OptionId != "MEND" || (mendTarget is not null && MendRunnable(player)))
            .ToList();
        if (usable.Count == 0)
            return new CampPlan(0, 0, 0);

        var act = player.RunState.CurrentActIndex;
        // The camp exists to get the team through whatever comes next, so the same
        // HP is worth more before an elite or the act boss than before a shop —
        // and most of all at the last camp before the act boss (CampHealWeight).
        var healWeight = CampHealWeight(player, null,
            (double)EffectiveHp(player) / Math.Max(1, player.Creature.MaxHp));
        // An upgrade is only worth skipping recovery for when a card actually
        // improves the deck; otherwise resting/healing keeps the run alive.
        // The upgrade value is now the real difference an upgrade makes (a cost
        // drop is worth ~14, +3 damage ~3.6), so the gate asks "does some card
        // actually improve", not "is some card good on its own".
        // Score each card's upgrade once: the worthwhile gate and the best
        // upgrade are two reads of the same value, and rescoring clones twice per
        // camp was pure waste.
        var upgrades = BuildValue.RankUpgrades(BuildValue.EligibleUpgrades(player.Deck.Cards).Select(card =>
            (Card: card, Value: BuildValue.UpgradeForDecision(card, player)))).ToList();
        var bestUpgrade = upgrades.Select(entry => entry.Value.Total).FirstOrDefault();
        var upgradeWorthwhile = bestUpgrade >= WorthwhileUpgrade;
        var selfHeal = HealAmount(player, self: true);
        // Resting also pays out through these relics, so the heal option carries
        // their value too.
        var restReward = SelfHealRewardValue(player);
        // Mend heals one other player: compare its best actual recovery after
        // earlier choices settle, including modifiers and the human HP weight.
        // mendTarget was resolved above; MEND is already filtered out of `usable` when there
        // is nobody it could heal, so this reads only the "how much would it heal" part.
        var mendHeal = mendTarget is null ? 0 : HealAmount(mendTarget, self: false);
        // The upgrade's opportunity cost is already in Score. A separate
        // patient-HP cutoff hid useful MEND choices after an earlier teammate
        // healed this seat, making it overfill itself instead of helping back.
        // Compare every executable aid by the actual HP it would restore.
        // The best upgrade the deck can actually buy. Not a flat number: the smith
        // option has to be able to lose to a relic or to a real wound, and it has
        // to prefer a deck that holds a core card over one that only holds basics.
        var horizon = InvestmentHorizon(player);

        // Compare effective healing with the actual upgrade on one scale.
        // HP thresholds previously suppressed every upgrade to 22%, even when
        // most of the rest would overflow. Risk already prices the next room.
        double Score(RestSiteOption option) => option.OptionId switch
        {
            "HEAL" => HealValue(selfHeal, EffectiveHp(player), player.Creature.MaxHp, healWeight)
                * SurvivalPressure(EffectiveHp(player), player.Creature.MaxHp) + restReward,
            "MEND" => mendTarget is null ? 0
                : MendValue(player, mendTarget),
            "SMITH" => !upgradeWorthwhile ? 2 : bestUpgrade * horizon,
            // Dig: one relic now, worth less the fewer acts remain.
            "DIG" => 5 + 3 * horizon,
            // Lift: permanent Strength that scales with the acts still ahead.
            "LIFT" => Math.Max(18 + Math.Max(0, 2 - act) * 9, 8 * horizon),
            "HATCH" => 22,
            "COOK" => CookValue(player),
            "KINDLE" => 15,
            "CLONE" => (player.Deck.Cards.Any(card => card.Enchantment is not null) ? 18 : 2),
            _ => 6,
        };
        var choice = usable.OrderByDescending(pair => Score(pair.option)).ThenBy(pair => pair.index).First();
        // ALWAYS LOG THE SCORES. This used to be gated on `ConstructionDecision.Current is null`,
        // and in the live 2026-10-05 A10 run the async deck rebuild was in flight at every camp,
        // so the one line that shows HEAL-vs-SMITH and the overflow breakdown never appeared —
        // the user could only see the final `chose=HEAL`. Camps are a handful of nodes per act,
        // so the volume is negligible next to losing the ability to audit the decision.
        Log.Info($"CoopBots camp: bot={player.NetId} hp={player.Creature.CurrentHp}/{player.Creature.MaxHp} "
            + $"lastCamp={RunDepth.LastCampBeforeBoss(player)} chose={choice.option.OptionId} "
            + $"heal={Math.Min(selfHeal, Math.Max(0, player.Creature.MaxHp - EffectiveHp(player))):F1}/{selfHeal:F1} "
            + $"overflow={Math.Max(0, selfHeal - Math.Max(0, player.Creature.MaxHp - EffectiveHp(player))):F1} "
            + $"restReward={restReward:F1} "
            + $"investmentHorizon={horizon:F2} "
            + (upgrades.Count == 0 ? "" : $"bestUpgrade={upgrades[0].Card.Id.Entry}:{bestUpgrade:F1}:{upgrades[0].Value.Reason} ")
            + (mendTarget is null ? "" : $"mendTarget={mendTarget.NetId} "
                + $"mendHp={mendTarget.Creature.CurrentHp}/{mendTarget.Creature.MaxHp} "
                + $"mendHeal={Math.Min(mendHeal, Math.Max(0, mendTarget.Creature.MaxHp - EffectiveHp(mendTarget))):F1}/{mendHeal:F1} "
                + $"mendOverflow={Math.Max(0, mendHeal - Math.Max(0, mendTarget.Creature.MaxHp - EffectiveHp(mendTarget))):F1} ")
            + $"scores=[{string.Join(',', usable.Select(pair => $"{pair.option.OptionId}:{Score(pair.option):F1}"))}]");
        return choice.option.OptionId switch
        {
            "MEND" when mendTarget is not null => new CampPlan(choice.index, mendTarget.NetId, mendHeal),
            "HEAL" => new CampPlan(choice.index, player.NetId, selfHeal),
            _ => new CampPlan(choice.index, 0, 0),
        };
    }

    // How dangerous the room this camp is preparing for is. The heal value used
    // to ask "how hurt are you" in a vacuum, so a pot of HP bought the same
    // whether the next node was the act boss or a shop; the reviewed party
    // reached the boss at 43-61% because an upgrade always won. The next node is
    // the only part of "expected fight length" the map can actually tell us.
    private static double UpcomingRisk(Player player)
    {
        try
        {
            var state = player.RunState;
            // The actual next choice is unknown, so price the camp for the most
            // dangerous room it could reach rather than the lexicographically
            // first child: child order is presentation order, not travel order.
            var types = state.CurrentMapPoint?.Children.Select(child => child.PointType);
            return MaxReachableRisk(state.CurrentActIndex, types, RunDepth.LastCampBeforeBoss(player));
        }
        catch
        {
            // A map we cannot read must not change the camp decision.
            return 1;
        }
    }

    // The conservative maximum reachable risk, kept free of the map so its order
    // invariance can be pinned directly. An unknown set is neutral, and the last
    // camp before a boss keeps its own act's boss floor.
    internal static double MaxReachableRisk(int act, IEnumerable<MapPointType>? nextTypes, bool lastCampBeforeBoss)
    {
        var risk = 1.0;
        if (nextTypes is not null)
            foreach (var type in nextTypes) risk = Math.Max(risk, RiskFor(type, act));
        return lastCampBeforeBoss ? Math.Max(risk, RiskFor(MapPointType.Boss, act)) : risk;
    }

    // Kept free of the map so it can be pinned directly, the way RiskFor is.
    // The last camp before a boss is the last chance to heal in the act whatever
    // the next room happens to be, so it is treated as the boss camp it
    // effectively is — and through the act's own boss risk, not a flat floor. A
    // flat floor flattened the early acts up to the last act's urgency, which is
    // the opposite of what a young deck needs.
    internal static double RiskForCamp(int act, MapPointType? next, bool lastCampBeforeBoss)
    {
        var risk = next is null ? 1 : RiskFor(next.Value, act);
        return lastCampBeforeBoss ? Math.Max(risk, RiskFor(MapPointType.Boss, act)) : risk;
    }

    // An upgrade's own value, in the same unit as the heals it competes with. A
    // starter deck's best upgrade is a Strike at 3.6 and a Defend at 3.0, where a
    // real card runs 5-8, so the same scale that lets a core card take the camp
    // also lets it lose to a relic or to a real wound.
    internal static double InvestmentHorizon(Player player)
    {
        double remaining;
        try { remaining = RunDepth.RemainingCombats(player); }
        catch { return 2.5; }
        var weakest = player.RunState.Players.Where(p => p.Creature.IsAlive)
            .Select(p => (double)p.Creature.CurrentHp / Math.Max(1, p.Creature.MaxHp)).DefaultIfEmpty(1).Min();
        return DiscountedFights(remaining, weakest);
    }

    // Permanent improvements pay in multiple fights; healing pays once. Future
    // fights are discounted more heavily while any teammate is close to death,
    // then repriced after the team's first heal has actually landed.
    internal static double DiscountedFights(double remaining, double weakestHpRatio)
    {
        var continuation = .45 + .4 * Math.Clamp(weakestHpRatio, 0, 1);
        var count = Math.Clamp(remaining, 1, 12);
        var whole = (int)count;
        return Enumerable.Range(0, whole).Sum(i => Math.Pow(continuation, i))
            + (count - whole) * Math.Pow(continuation, whole);
    }

    // Kept free of the map so it can be pinned directly.
    // Later acts hit harder, so the same elite is worse in act 3 than in act 1.
    internal static double RiskFor(MapPointType type, int act) => type switch
    {
        // An act-3 boss is the run's biggest single hit, so the same wound is
        // worth more there than before an earlier one.
        MapPointType.Boss => act >= BotShopPlanner.FinalAct ? 1.8 : 1.5,
        MapPointType.Elite => act <= 0 ? 1.35 : 1.5,
        MapPointType.Monster => 1.15,
        MapPointType.Unknown => 1.1,
        // Another camp, a shop or a treasure means the party can still top up
        // before anything tests it: this heal is worth less right now.
        MapPointType.RestSite or MapPointType.Shop or MapPointType.Treasure => 0.85,
        _ => 1,
    };

    // Healing buys survival in the fight ahead, so every point it actually adds
    // counts, scaled by how close the patient is to dying. Only HP that fits
    // under the bar is credited, which is what stops a second mend stacking onto
    // the first: once the target is nearly topped up there is nothing left to buy.
    internal static double HealValue(decimal healed, decimal currentHp, decimal maxHp, double weight)
    {
        if (healed <= 0 || maxHp <= 0) return 0;
        var credited = Math.Min(healed, Math.Max(0, maxHp - currentHp));
        if (credited <= 0) return 0;
        // Integrate need over the HP actually restored. The last healed point
        // is less urgent than the first; overflow contributes exactly zero.
        var ratio = Math.Clamp((double)((currentHp + credited / 2) / maxHp), 0, 1);
        var need = NeedAtDeath - (NeedAtDeath - NeedAtFull) * ratio;
        return (double)credited * need * weight;
    }

    private static double CookValue(Player player)
    {
        // Cook removes two cards AND grants +5 max HP. The ordered plan is the
        // same one the permanent deck-edit path makes, so the cards Cook is
        // valued for removing are the cards it removes. Two corrections:
        //  - A protected step is the planner refusing to strip a role, not a
        //    free removal. If either required step is protected the option is
        //    declined outright rather than valued as if the cost were zero.
        //  - The removal term is signed. Removing strong cards is a real cost,
        //    so it may pull the whole choice below the +5 max HP baseline (worth
        //    10); only the upper bound is capped, and the choice floors at zero.
        var removable = player.Deck.Cards.Where(card => card.IsRemovable).ToList();
        if (removable.Count < 2) return 0;
        var plan = RemovalPlan.Choose(player, removable, 2);
        if (plan.Steps.Count < 2) return 0;
        if (plan.Steps.Any(step => step.Protected)) return 0;
        var removal = plan.Steps.Sum(step => step.Value) * InvestmentHorizon(player);
        return Math.Max(0, 10 + removal);
    }

    private static decimal HealAmount(Player player, bool self)
    {
        try
        {
            return self
                ? HealRestSiteOption.GetHealAmount(player)
                : MendRestSiteOption.GetHealAmount(player);
        }
        catch
        {
            // A hook we cannot evaluate must not break the rest-site choice.
            return HealRestSiteOption.GetBaseHealAmount(player.Creature);
        }
    }

    private static bool IsUsable(RestSiteOption option)
    {
        var property = option.GetType().GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Instance);
        return property?.GetValue(option) as bool? ?? true;
    }
}

/// <summary>
/// MEND's native local branch cannot run on a handed-over LOCAL seat: it drives
/// <c>NTargetManager</c> and waits for a click nobody can make. Replace only that target
/// acquisition. The remote branch's policy (<see cref="BotRestSitePatch.BestMendTarget"/>)
/// is reused, and <c>SyncLocalChoice</c> still broadcasts the chosen target so every peer
/// heals the same player. The heal/hook body is the same code the remote branch already runs.
/// </summary>
[HarmonyPatch(typeof(MendRestSiteOption), nameof(MendRestSiteOption.OnSelect))]
internal static class BotMendLocalSeatPatch
{
    private static readonly MethodInfo OwnerGetter = AccessTools.PropertyGetter(typeof(RestSiteOption), "Owner");

    private static bool Prefix(MendRestSiteOption __instance, ref Task<bool> __result)
    {
        var owner = (Player?)OwnerGetter.Invoke(__instance, null);
        if (owner is null || !AutoPilot.Drives(owner.NetId) || !LocalContext.IsMe(owner))
            return true;
        __result = ChooseWithoutTargetingUi(owner);
        return false;
    }

    private static async Task<bool> ChooseWithoutTargetingUi(Player owner)
    {
        var synchronizer = RunManager.Instance.PlayerChoiceSynchronizer;
        var choiceId = synchronizer.ReserveChoiceId(owner);
        var target = BotRestSitePatch.BestMendTarget(owner);
        Log.Info($"CoopBots rest-site: local MEND seat={owner.NetId} "
            + $"target={target?.NetId.ToString() ?? "none"}");
        synchronizer.SyncLocalChoice(owner, choiceId, PlayerChoiceResult.FromPlayerId(target?.NetId));
        return await BotRestSitePatch.ApplyMend(owner, target);
    }
}

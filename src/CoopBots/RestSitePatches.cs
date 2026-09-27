using System.Reflection;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;

namespace CoopBots;

[HarmonyPatch(typeof(RestSiteRoom), nameof(RestSiteRoom.EnterInternal))]
internal static class BotRestSitePatch
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
        _ = CompleteAfterEntry(__result, async () =>
        {
            if (!ReferenceEquals(runState, RunManager.Instance.DebugOnlyGetState())) return;
            ResetPlanning();
            await CompleteInOrder(InvestmentOrder(runState.Players.Where(player => AutoPilot.Drives(player.NetId))),
                player => CompleteForBot(RunManager.Instance.RestSiteSynchronizer, player));
        });
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
                if (!MendRunnable(player) && !_mendRefusalLogged
                    && options.Any(option => option.OptionId == "MEND"))
                {
                    _mendRefusalLogged = true;
                    Log.Warn($"CoopBots rest-site: MEND is not offered to driven local seat "
                        + $"{player.NetId} — its OnSelect drives the targeting UI, which a "
                        + "handed-over seat has no way to answer.");
                }
                var plan = Choose(player, options);
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
    private const double WorthwhileUpgrade = 2.5;
    private const double HumanHealWeight = 1.2;
    private static bool _mendRefusalLogged;

    internal static void ResetPlanning()
    {
        _mendRefusalLogged = false;
    }

    /// <summary>
    /// Whether this seat can execute MEND at all.
    ///
    /// <para>
    /// <c>MendRestSiteOption.OnSelect</c> has two branches. For <c>LocalContext.IsMe(owner)</c>
    /// it drives the TARGETING UI — <c>NRestSiteRoom.Instance</c>, <c>NTargetManager.StartTargeting</c>
    /// and an <c>await SelectionFinished()</c> that only a click resolves. For every other seat
    /// it asks <c>PlayerChoiceSynchronizer.WaitForRemoteChoice</c>, and this mod already answers
    /// that for a driven seat (<c>BotRemoteChoicePatch</c> → <c>BotRewardDriver.ChoiceForWait</c>,
    /// which carries an explicit <c>MendRestSiteOption</c> case).
    /// </para>
    /// <para>
    /// A driven LOCAL seat has neither: no human to click a target and no UI to click it in.
    /// That is the live <c>rest-site option MEND refused for 1: Object reference not set</c>
    /// (3 times over five A5 rounds, and the seat that wanted it was the 9/80 one before the
    /// act-1 boss). Offering an option that cannot resolve is also how a camp parks forever —
    /// the synchronizer only completes once every seat has no options left. Refusing it is an
    /// executability refusal (R4), and it is logged by name.
    /// </para>
    /// </summary>
    internal static bool MendRunnable(Player player)
        => !(AutoPilot.Drives(player.NetId) && LocalContext.NetId == player.NetId);

    // The same target policy is used by valuation and the actual remote choice.
    // A lower HP percentage is not necessarily a better heal: RegalPillow,
    // max HP, overflow, and a human teammate's weight all change its payoff.
    internal static Player? BestMendTarget(Player owner)
        => owner.RunState.Players
            .Where(candidate => candidate.NetId != owner.NetId && candidate.Creature.IsAlive)
            .OrderByDescending(candidate => MendValue(owner, candidate))
            .ThenBy(candidate => candidate.NetId)
            .FirstOrDefault();

    private static double MendValue(Player owner, Player patient)
        => HealValue(HealAmount(patient, self: false), patient.Creature.CurrentHp, patient.Creature.MaxHp,
            HealPerHp * UpcomingRisk(owner) * (AutoPilot.Drives(patient.NetId) ? 1 : HumanHealWeight))
            + SharedHealHookValue(patient);

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
    private readonly record struct CampPlan(int Index, ulong Patient, decimal Heal);

    private static CampPlan Choose(Player player, IReadOnlyList<RestSiteOption> options)
    {
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
        // HP is worth more before an elite or the act boss than before a shop.
        var risk = UpcomingRisk(player);
        // An upgrade is only worth skipping recovery for when a card actually
        // improves the deck; otherwise resting/healing keeps the run alive.
        // The upgrade value is now the real difference an upgrade makes (a cost
        // drop is worth ~14, +3 damage ~3.6), so the gate asks "does some card
        // actually improve", not "is some card good on its own".
        // Score each card's upgrade once: the worthwhile gate and the best
        // upgrade are two reads of the same value, and rescoring clones twice per
        // camp was pure waste.
        var upgrades = player.Deck.Cards.Select(card => (Card: card, Value: BuildValue.UpgradeDelta(card, player)))
            .OrderByDescending(entry => entry.Value.Total).ToList();
        var upgradeWorthwhile = upgrades.Any(entry => entry.Value.Total >= WorthwhileUpgrade);
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
        var bestUpgrade = upgrades.Select(entry => entry.Value.Total).DefaultIfEmpty(0).Max();
        var horizon = InvestmentHorizon(player);

        // Compare effective healing with the actual upgrade on one scale.
        // HP thresholds previously suppressed every upgrade to 22%, even when
        // most of the rest would overflow. Risk already prices the next room.
        double Score(RestSiteOption option) => option.OptionId switch
        {
            "HEAL" => HealValue(selfHeal, EffectiveHp(player), player.Creature.MaxHp, HealPerHp * risk) + restReward,
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
        var removal = Math.Min(plan.Steps.Sum(step => step.Value) * 0.3, 35);
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

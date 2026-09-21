using System.Reflection;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.BeginRestSite))]
internal static class BotRestSitePatch
{
    private static readonly MethodInfo ChooseOption = AccessTools.Method(
        typeof(RestSiteSynchronizer),
        "ChooseOption",
        new[] { typeof(Player), typeof(int) });

    private static void Postfix(RestSiteSynchronizer __instance)
    {
        var state = RunManager.Instance.DebugOnlyGetState();
        if (state is null)
            return;
        // BeginRestSite fires once per camp; forget the previous camp's plans.
        ResetPlanning();
        foreach (var player in state.Players.Where(player => AutoPilot.Drives(player.NetId)))
            _ = CompleteForBot(__instance, player);
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
                var preferred = Choose(player, options);
                var attempts = options
                    .Select((option, index) => (option, index))
                    .Where(pair => IsUsable(pair.option))
                    .OrderByDescending(pair => pair.index == preferred)
                    .ThenBy(pair => pair.index)
                    .ToList();
                var advanced = false;
                foreach (var (option, index) in attempts)
                {
                    try
                    {
                        var task = (Task<bool>)ChooseOption.Invoke(synchronizer, new object[] { player, index })!;
                        if (await task) { advanced = true; break; }
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

    // HP is not worth nothing above half. The old rule credited healing only up
    // to 50% max HP, which made a wound that still left a bot above half score
    // zero — and at the camp before the act boss that is exactly the case that
    // matters. Three bots entered that fight at 43%, 60% and 61% and upgraded
    // instead, because the formula could only ever say "0".
    //
    // The rate now runs from NeedAtFull at a full bar to NeedAtDeath at an empty
    // one, so the same heal is worth three times as much on a dying bot as on a
    // lightly wounded one, and the crossover against an upgrade lands near 65%
    // max HP — the same "low HP" line the route planner already uses.
    private const double NeedAtFull = 1;
    private const double NeedAtDeath = 3;
    // Deck-building is still greedy: an upgrade outranks ordinary HP, so an
    // ordinary wound is left alone and the bot smiths instead.
    private const double HealPerHp = 0.4;
    // An upgrade is worth spending a camp on once it measurably improves a
    // card. Below this the card gains nothing the smith can realise.
    private const double WorthwhileUpgrade = 2.5;
    // A human is a little more valuable to keep alive than a bot.
    private const double HumanHealWeight = 1.2;
    // Each bot plans independently on the same thread. Remember the healing a
    // previous bot already committed at this rest site so the party does not
    // stack several mends into one target and overheal it to full.
    private static readonly Dictionary<ulong, decimal> PlannedHeal = new();
    internal static void ResetPlanning() => PlannedHeal.Clear();

    private static int Choose(Player player, IReadOnlyList<RestSiteOption> options)
    {
        decimal EffectiveHp(Player candidate) => candidate.Creature.CurrentHp + PlannedHeal.GetValueOrDefault(candidate.NetId);
        // Mend must not even be OFFERED when it has no target: MendRestSiteOption.OnSelect()
        // dereferences one, and offering an option that cannot resolve is how a seat ended up
        // unchosen and stalled the whole camp. Resolved before `usable` so the filter sees it.
        var mendTarget = player.RunState.Players
            .Where(candidate => candidate.NetId != player.NetId && candidate.Creature.IsAlive)
            .OrderBy(candidate => (double)EffectiveHp(candidate) / Math.Max(1, candidate.Creature.MaxHp))
            .ThenBy(candidate => candidate.NetId)
            .FirstOrDefault();
        var usable = options
            .Select((option, index) => (option, index))
            .Where(pair => IsUsable(pair.option))
            .Where(pair => pair.option.OptionId != "MEND" || mendTarget is not null)
            .ToList();
        if (usable.Count == 0)
            return 0;

        var hp = (double)player.Creature.CurrentHp / Math.Max(1, player.Creature.MaxHp);
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
        var upgrades = player.Deck.Cards.Select(card => HumanCoopAdvisor.UpgradeValue(card, player)).ToList();
        var upgradeWorthwhile = upgrades.Any(value => value >= WorthwhileUpgrade);
        var selfHeal = HealAmount(player, self: true);
        // Resting also pays out through these relics, so the heal option carries
        // their value too.
        var restReward = player.Relics.Any(relic => relic.GetType().Name is "DreamCatcher" or "TinyMailbox") ? 12
            : player.Relics.Any(relic => relic.GetType().Name is "VenerableTeaSet" or "FakeVenerableTeaSet") ? 8
            : 0;
        // Mend heals one other player: value it by the most injured teammate (after what
        // earlier bots already committed), and weight a human's HP above a bot's.
        // mendTarget was resolved above; MEND is already filtered out of `usable` when there
        // is nobody it could heal, so this reads only the "how much would it heal" part.
        var mendHeal = mendTarget is null ? 0 : HealAmount(mendTarget, self: false);
        var mendWeight = mendTarget is not null && !AutoPilot.Drives(mendTarget.NetId)
            ? HealPerHp * HumanHealWeight : HealPerHp;
        // Mending hands the mender's own camp to someone else: one pick, and its
        // deck does not grow. The one way to make it free is a MiniatureTent,
        // whose owner keeps every option at the camp and can therefore smith
        // *and* mend. Otherwise only a teammate who would not survive without it
        // is worth the trade — and that rescue is a survival move, so it is not
        // subject to the mender's own line.
        var mendEmergency = mendTarget is not null
            && (double)EffectiveHp(mendTarget) / Math.Max(1, mendTarget.Creature.MaxHp) <= MendEmergencyHp;
        var mendAllowed = mendEmergency
            || player.Relics.Any(relic => relic.GetType().Name == "MiniatureTent");
        // The best upgrade the deck can actually buy. Not a flat number: the smith
        // option has to be able to lose to a relic or to a real wound, and it has
        // to prefer a deck that holds a core card over one that only holds basics.
        var bestUpgrade = upgrades.DefaultIfEmpty(0).Max();

        // The line this camp uses: above it the camp goes to the deck, below it to
        // survival. Acts 1-2 are the greedy ones — a weakened member can be carried
        // through a small fight, so the party buys deck strength instead of HP. The
        // last act is the reverse, and its final camp is the one camp nobody can be
        // carried out of a boss fight on. The reviewed act-1 run took heal or mend
        // at every one of its fifteen bot camp slots and then could not out-damage
        // the act-1 boss; the human, who smithed every camp, was the only member
        // whose deck grew.
        var floor = SmithHpFloor(act, RunDepth.LastCampBeforeBoss(player));
        var toDeck = hp >= floor;
        // The losing side of the line is scaled down rather than cut off. A cliff
        // would make the whole choice jump on a single point of HP, and tuning the
        // two sides against each other could not hold the line at all: how much a
        // heal is worth moves with the wound and how much an upgrade is worth moves
        // with the deck, so any fixed pair of numbers lets one side drift across.
        double Weight(bool survival) => survival == !toDeck ? 1.0 : OffSide;

        double Score(RestSiteOption option) => option.OptionId switch
        {
            "HEAL" => (HealValue(selfHeal, EffectiveHp(player), player.Creature.MaxHp, HealPerHp * risk) + restReward)
                * Weight(survival: true),
            "MEND" => !mendAllowed || mendTarget is null ? 0
                : HealValue(mendHeal, EffectiveHp(mendTarget), mendTarget.Creature.MaxHp, mendWeight * risk)
                    * (mendEmergency ? 1.0 : Weight(survival: true)),
            "SMITH" => !upgradeWorthwhile ? 2 : bestUpgrade * UpgradeScale * Weight(survival: false),
            // Dig: one relic now, worth less the fewer acts remain.
            "DIG" => Math.Max(8, 30 - act * 5) * Weight(survival: false),
            // Lift: permanent Strength that scales with the acts still ahead.
            "LIFT" => (18 + Math.Max(0, 2 - act) * 9) * Weight(survival: false),
            "HATCH" => 22 * Weight(survival: false),
            "COOK" => CookValue(player) * Weight(survival: false),
            "KINDLE" => 15 * Weight(survival: false),
            "CLONE" => (player.Deck.Cards.Any(card => card.Enchantment is not null) ? 18 : 2) * Weight(survival: false),
            _ => 6 * Weight(survival: false),
        };
        var choice = usable.OrderByDescending(pair => Score(pair.option)).ThenBy(pair => pair.index).First();
        // Reserve what this bot is about to heal so later bots at the same rest
        // site see the target as already covered.
        if (choice.option.OptionId == "MEND" && mendTarget is not null)
            PlannedHeal[mendTarget.NetId] = PlannedHeal.GetValueOrDefault(mendTarget.NetId) + mendHeal;
        else if (choice.option.OptionId == "HEAL")
            PlannedHeal[player.NetId] = PlannedHeal.GetValueOrDefault(player.NetId) + selfHeal;
        return choice.index;
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

    // The HP a bot gives up on its own deck to keep. Above this line the camp
    // goes to the deck, below it to survival. Acts 1-2 hold it low because a
    // weakened member can be carried through a small fight and the deck that
    // never grows is the one that cannot out-damage the act boss. The last act is
    // the reverse, and its final camp is the one camp nobody can be carried out
    // of a boss fight on.
    internal static double SmithHpFloor(int act, bool lastCampBeforeBoss)
        => act >= BotShopPlanner.FinalAct ? (lastCampBeforeBoss ? 0.85 : 0.55) : 0.25;

    // The losing side of the line, scaled down rather than cut off.
    private const double OffSide = 0.22;
    // An upgrade's own value, in the same unit as the heals it competes with. A
    // starter deck's best upgrade is a Strike at 3.6 and a Defend at 3.0, where a
    // real card runs 5-8, so the same scale that lets a core card take the camp
    // also lets it lose to a relic or to a real wound.
    private const double UpgradeScale = 2.5;
    // A teammate this close to death is worth the mender's camp even without a
    // tent; anything healthier can be carried to the next camp.
    private const double MendEmergencyHp = 0.25;

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
        var ratio = Math.Clamp((double)(currentHp / maxHp), 0, 1);
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

using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Logging;
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
        foreach (var player in state.Players.Where(player => BotRegistry.IsBot(player.NetId)))
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

                var optionIndex = Choose(player, options);
                var task = (Task<bool>)ChooseOption.Invoke(synchronizer, new object[] { player, optionIndex })!;
                if (!await task)
                {
                    var next = options
                        .Select((option, index) => (option, index))
                        .FirstOrDefault(pair => pair.index != optionIndex && IsUsable(pair.option));
                    if (next.option is null)
                        return;
                    task = (Task<bool>)ChooseOption.Invoke(synchronizer, new object[] { player, next.index })!;
                    await task;
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error($"CoopBots rest-site choice failed for bot {player.NetId}: {exception.GetBaseException()}");
        }
    }

    // A player above half HP can clear another fight, so HP added there buys
    // nothing and is not worth a permanent upgrade. Only the heal that pulls a
    // teammate back toward half counts, and it counts more the closer they are
    // to dying.
    private const double MendSafeFraction = 0.5;
    // Deck-building is greedy: one upgrade outranks ordinary HP. With the
    // urgency curve below, a bot only spends its camp on healing once a
    // teammate is near a third HP; above that it upgrades instead.
    private const double HealPerHp = 0.8;
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
        var usable = options
            .Select((option, index) => (option, index))
            .Where(pair => IsUsable(pair.option))
            .ToList();
        if (usable.Count == 0)
            return 0;

        var difficulty = BotRegistry.Difficulty(player.NetId);
        if (difficulty == BotDifficulty.Dumb)
            return usable[BotBrain.StableIndex($"rest:{player.NetId}:{player.RunState.TotalFloor}", usable.Count)].index;

        decimal EffectiveHp(Player candidate) => candidate.Creature.CurrentHp + PlannedHeal.GetValueOrDefault(candidate.NetId);

        var hp = (double)player.Creature.CurrentHp / Math.Max(1, player.Creature.MaxHp);
        var act = player.RunState.CurrentActIndex;
        // An upgrade is only worth skipping recovery for when a card actually
        // improves the deck; otherwise resting/healing keeps the run alive.
        // The upgrade value is now the real difference an upgrade makes (a cost
        // drop is worth ~14, +3 damage ~3.6), so the gate asks "does some card
        // actually improve", not "is some card good on its own".
        var upgradeWorthwhile = player.Deck.Cards.Any(card =>
            HumanCoopAdvisor.UpgradeValue(card, player) >= WorthwhileUpgrade);
        var selfHeal = HealAmount(player, self: true);
        // Resting also pays out through these relics, so the heal option carries
        // their value too.
        var restReward = player.Relics.Any(relic => relic.GetType().Name is "DreamCatcher" or "TinyMailbox") ? 12
            : player.Relics.Any(relic => relic.GetType().Name is "VenerableTeaSet" or "FakeVenerableTeaSet") ? 8
            : 0;
        // Mend heals one other player: value it by the most injured teammate
        // (after what earlier bots already committed), and weight a human's HP
        // above a bot's.
        var mendTarget = player.RunState.Players
            .Where(candidate => candidate.NetId != player.NetId && candidate.Creature.IsAlive)
            .OrderBy(candidate => (double)EffectiveHp(candidate) / Math.Max(1, candidate.Creature.MaxHp))
            .ThenBy(candidate => candidate.NetId)
            .FirstOrDefault();
        var mendHeal = mendTarget is null ? 0 : HealAmount(mendTarget, self: false);
        var mendWeight = mendTarget is not null && !BotRegistry.IsBot(mendTarget.NetId)
            ? HealPerHp * HumanHealWeight : HealPerHp;

        double Score(RestSiteOption option) => option.OptionId switch
        {
            "HEAL" => HealValue(selfHeal, EffectiveHp(player), player.Creature.MaxHp, HealPerHp) + restReward,
            "MEND" => mendTarget is null ? 0
                : HealValue(mendHeal, EffectiveHp(mendTarget), mendTarget.Creature.MaxHp, mendWeight),
            "SMITH" => !upgradeWorthwhile ? 2 : hp > 0.6 ? 16 : hp > 0.4 ? 10 : 6,
            // Dig: one relic now, worth less the fewer acts remain.
            "DIG" => Math.Max(8, 30 - act * 5),
            // Lift: permanent Strength that scales with the acts still ahead.
            "LIFT" => 18 + Math.Max(0, 2 - act) * 9,
            "HATCH" => 22,
            "COOK" => CookValue(player),
            "KINDLE" => 15,
            "CLONE" => player.Deck.Cards.Any(card => card.Enchantment is not null) ? 18 : 2,
            _ => 6,
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

    // Healing is only worth a permanent upgrade when it buys survival: credit at
    // most the HP that lifts someone back to half, scaled up as they approach
    // death. A near-full player scores zero, so the bot smiths instead.
    internal static double HealValue(decimal healed, decimal currentHp, decimal maxHp, double weight)
    {
        if (healed <= 0 || maxHp <= 0) return 0;
        var credited = Math.Min(healed, Math.Max(0, maxHp * (decimal)MendSafeFraction - currentHp));
        if (credited <= 0) return 0;
        var ratio = Math.Clamp((double)(currentHp / maxHp), 0, 1);
        var urgency = Math.Clamp((MendSafeFraction - ratio) / MendSafeFraction, 0, 1);
        return (double)credited * (1 + 2 * urgency * urgency) * weight;
    }

    private static double CookValue(Player player)
    {
        // Cook removes two cards AND grants +5 max HP, so it is only wrongly
        // attractive when the two worst cards are not actually bad: the removal
        // term stays zero then and only the durability gain remains.
        var removable = player.Deck.Cards.Where(card => card.IsRemovable)
            .OrderBy(card => HumanCoopAdvisor.CardValue(card, player).Score).Take(2).ToList();
        if (removable.Count < 2) return 0;
        var removal = Math.Min(35, removable.Sum(card => BotShopPlanner.RemovalValue(card, player)) * 0.3);
        return 10 + removal;
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

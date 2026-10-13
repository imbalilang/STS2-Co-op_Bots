using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CoopBots;

internal static partial class BotRestSitePatch
{
    internal static readonly Dictionary<ulong, ulong> PlannedPatients = new();
    private sealed record CampAction(Player Owner, CampPlan Plan, string Id, double Bonus);

    internal static bool NextFightIsBoss(Player player)
    {
        var seen = new HashSet<MapPoint>();
        bool Walk(MapPoint point)
        {
            if (point.PointType == MapPointType.Boss) return true;
            if (point.PointType is MapPointType.Monster or MapPointType.Elite or MapPointType.Unknown
                || !seen.Add(point) || seen.Count > 256 || point.Children.Count == 0) return false;
            var result = point.Children.All(Walk);
            seen.Remove(point);
            return result;
        }
        var next = player.RunState.CurrentMapPoint?.Children;
        return next is { Count: > 0 } && next.All(Walk);
    }

    internal static decimal EntryHeal(Player player, bool bossNext) => bossNext
        ? player.Relics.OfType<Pantograph>().Sum(r => decimal.Floor(r.DynamicVars.Heal.BaseValue)) : 0;

    internal static CampPlan ChooseTeam(Player player, IReadOnlyList<RestSiteOption> ownOptions,
        Func<Player, IReadOnlyList<RestSiteOption>> optionsFor, bool? bossNext = null)
    {
        using var evaluation = DeckStrength.BeginEvaluation();
        var party = player.RunState.Players.Where(p => p.Creature.IsAlive).OrderBy(p => p.NetId).ToArray();
        var actors = party.Where(p => p == player || AutoPilot.Drives(p.NetId)).ToArray();
        var choices = new List<CampAction[]>();
        foreach (var owner in actors)
        {
            var options = owner == player ? ownOptions : optionsFor(owner);
            if (options.Count == 0) continue;
            var horizon = InvestmentHorizon(owner);
            var bestUpgrade = BuildValue.RankUpgrades(BuildValue.EligibleUpgrades(owner.Deck.Cards).Select(c =>
                (Card: c, Value: BuildValue.UpgradeForDecision(c, owner))))
                .Select(entry => entry.Value.Total).FirstOrDefault();
            double Investment(string id) => id switch {
                "SMITH" => bestUpgrade < WorthwhileUpgrade ? 2 : bestUpgrade * horizon,
                "DIG" => 5 + 3 * horizon,
                "LIFT" => Math.Max(18 + Math.Max(0, 2 - owner.RunState.CurrentActIndex) * 9, 8 * horizon),
                "HATCH" => 22, "COOK" => CookValue(owner), "KINDLE" => 15,
                "CLONE" => owner.Deck.Cards.Any(c => c.Enchantment is not null) ? 18 : 2, _ => 6,
            };
            var actions = new List<CampAction>();
            foreach (var (option, index) in options.Select((o, i) => (o, i)).Where(x => IsUsable(x.o)))
            {
                if (option.OptionId == "HEAL")
                    actions.Add(new(owner, new(index, owner.NetId, decimal.Floor(HealAmount(owner, true))), "HEAL", SelfHealRewardValue(owner)));
                else if (option.OptionId == "MEND" && MendRunnable(owner))
                    actions.AddRange(party.Where(p => p != owner).Select(p => new CampAction(owner,
                        new(index, p.NetId, decimal.Floor(HealAmount(p, false))), "MEND", SharedHealHookValue(p) * AllyHealDiscount)));
                else if (option.OptionId != "MEND")
                    actions.Add(new(owner, new(index, 0, 0), option.OptionId, Investment(option.OptionId)));
            }
            // Investment alternatives do not change this camp's HP. Keep their
            // best value; retain every heal/target combination for comparison.
            var bestInvestment = actions.Where(a => a.Plan.Patient == 0).OrderByDescending(a => a.Bonus).FirstOrDefault();
            var useful = actions.Where(a => a.Plan.Patient != 0 || a == bestInvestment).ToArray();
            if (useful.Length > 0) choices.Add(useful);
        }
        if (choices.Count == 0) return Choose(player, ownOptions);
        var future = party.ToDictionary(p => p, p => EntryHeal(p, bossNext ?? NextFightIsBoss(p)));
        double Score(IReadOnlyList<CampAction> actions)
        {
            var value = actions.Sum(a => a.Bonus);
            foreach (var patient in party)
            {
                var max = patient.Creature.MaxHp;
                var hp = Math.Min(max, patient.Creature.CurrentHp + future[patient]);
                var self = actions.Where(a => a.Owner == patient && a.Id == "HEAL").Sum(a => a.Plan.Heal);
                var mend = actions.Where(a => a.Id == "MEND" && a.Plan.Patient == patient.NetId).Sum(a => a.Plan.Heal);
                var weight = CampHealWeight(patient, null, (double)hp / Math.Max(1, max));
                value += HealValue(self, hp, max, weight) * SurvivalPressure(hp, max);
                // Credit self recovery first. A helper buys only what will
                // still be missing after that and the guaranteed entry heal.
                var afterSelf = Math.Min(max, hp + self);
                value += HealValue(mend, afterSelf, max, weight) * AllyHealDiscount
                    * (AutoPilot.Drives(patient.NetId) ? 1 : HumanHealWeight);
            }
            return value;
        }
        // Four native seats have at most a few thousand combinations. The beam
        // bounds modded larger parties; no native healing or selection runs here.
        var plans = new List<CampAction[]> { Array.Empty<CampAction>() };
        foreach (var arms in choices)
            plans = plans.SelectMany(p => arms.Select(a => p.Append(a).ToArray()))
                .OrderByDescending(Score).Take(4096).ToList();
        var winner = plans.OrderByDescending(Score).First();
        var selected = winner.FirstOrDefault(a => a.Owner == player);
        if (selected is null) return Choose(player, ownOptions);
        if (ConstructionDecision.Current is null)
            Log.Info($"CoopBots camp team: actor={player.NetId} chose={selected.Id} patient={selected.Plan.Patient} "
                + $"value={Score(winner):F1} plan=[{string.Join(',', winner.Select(a => $"{a.Owner.NetId}:{a.Id}->{a.Plan.Patient}"))}] "
                + $"entryHeal=[{string.Join(',', future.Select(p => $"{p.Key.NetId}:{p.Value}"))}]");
        return selected.Plan;
    }
}

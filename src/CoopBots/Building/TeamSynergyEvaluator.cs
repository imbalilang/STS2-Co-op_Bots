using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CoopBots.Building;

/// <summary>Signed party-wide residual beyond the owner's ordinary deck estimate.</summary>
internal static class TeamSynergyEvaluator
{
    internal sealed record Change(double Total, double Damage, double Prevention);

    // 上游怎么做：局外构筑使用本人卡组差值及有上限的团队启发式。
    // 我们怎么做：从共享边界快照估算联合输出/减伤，加入有正负号的边际残值；
    // 具有联合机制的牌不再重复获取旧的共享减益加分。
    // 为什么：用户 2026-10-23 要求合入贡献包，保留其六回合压力及三回合投资近似。
    internal static TeamSynergyMath.Seat Summarize(Player player, IReadOnlyList<CardModel> deck)
    {
        var baseEnergy = BuildValue.StableEnergy(player);
        double paidAttack = 0, freeAttack = 0, otherDamage = 0;
        double paidBlock = 0, freeBlock = 0, draw = 0, energy = 0;
        double ordinaryCost = 0, componentCost = 0, nonPowerStrength = 0;
        double paidHits = 0, freeHits = 0;
        var components = new List<TeamSynergyMath.Component>();
        var powers = new List<PowerInvestmentMath.Power>();
        var supply = new ResourceStartup.Supply(deck, player);

        foreach (var card in deck.OrderBy(c => c.Id.Entry, StringComparer.Ordinal)
                     .ThenBy(c => c.CurrentUpgradeLevel))
        {
            var facts = CardProfile.Of(card, baseEnergy);
            if (facts.Unplayable || supply.Missing(card) is not null) continue;
            var component = TeamSynergyNative.Component(card, facts.Cost);
            var hasComponent = TeamSynergyNative.HasComponent(component);
            if (card.Type == CardType.Attack && !card.Tags.Contains(CardTag.OstyAttack))
            {
                if (facts.Cost == 0)
                {
                    freeAttack += facts.Damage;
                    freeHits += facts.Hits;
                }
                else
                {
                    paidAttack += facts.Damage;
                    paidHits += facts.Hits;
                }
            }
            else
            {
                otherDamage += facts.Damage + facts.Poison + facts.Doom;
            }

            if (facts.Effects.GeneratedShivs > 0)
            {
                var shivDamage = facts.Effects.GeneratedShivs * (double)ModelDb.Card<Shiv>().DynamicVars.Damage.BaseValue;
                if (facts.Cost == 0)
                {
                    freeAttack += shivDamage;
                    freeHits += facts.Effects.GeneratedShivs;
                }
                else
                {
                    paidAttack += shivDamage;
                    paidHits += facts.Effects.GeneratedShivs;
                }
            }
            if (!hasComponent)
            {
                if (facts.Cost == 0) freeBlock += facts.Block;
                else paidBlock += facts.Block;
            }
            if (!facts.IsPower) ordinaryCost += facts.Cost;
            draw += CardProfile.NetDraw(card, facts) + .7 * facts.NextTurnDraw;
            energy += facts.Energy + .7 * facts.NextTurnEnergy;
            var strength = facts.Mechanisms?.StatGrant is
                { Stat: ConstructionMechanismFacts.Stat.Strength, Lifetime: ConstructionMechanismFacts.Lifetime.Persistent } grant
                ? grant.Amount : 0;
            if (facts.IsPower)
            {
                powers.Add(new PowerInvestmentMath.Power(facts.Cost, strength, facts.Effects.StrengthPerTurn,
                    0, 0, card.Keywords.Contains(CardKeyword.Retain) || card.Keywords.Contains(CardKeyword.Sly)));
            }
            else
            {
                nonPowerStrength += strength + 2 * facts.Effects.StrengthPerTurn;
            }
            if (hasComponent)
            {
                components.Add(component with { Block = facts.Block });
                if (!facts.IsPower) componentCost += facts.Cost;
            }
        }

        var size = Math.Max(1, deck.Count);
        var hand = Math.Min(size, 5 + Math.Min(3, draw * 5 / size));
        var availableEnergy = baseEnergy + Math.Min(2, energy * hand / size);
        var demand = PowerInvestmentMath.OrdinaryDemand(ordinaryCost, componentCost) * hand / size;
        var investment = PowerInvestmentMath.Evaluate(size, hand, availableEnergy, demand, powers);
        var ordinaryStrength = nonPowerStrength * Math.Min(1, 3 * hand / size);
        var attack = freeAttack + paidAttack * investment.PaidShare
            + freeHits * (investment.Strength + ordinaryStrength)
            + paidHits * (investment.PaidStrength + ordinaryStrength * investment.PaidShare);
        var vulnerableFactor = TeamSynergyNative.VulnerableFactorFor(player);
        var weakFactor = TeamSynergyNative.WeakFactorFor(player);
        return new TeamSynergyMath.Seat(deck.Count, hand, availableEnergy, attack * hand / size,
            otherDamage * hand / size, (freeBlock + paidBlock * investment.PaidShare) * hand / size,
            player.Creature.CurrentHp, player.Creature.MaxHp, components, vulnerableFactor, weakFactor,
            (freeAttack + freeHits * (investment.Strength + ordinaryStrength)) * hand / size,
            paidBlock * investment.PaidShare * hand / size);
    }

    internal static Change Marginal(Player owner, IReadOnlyList<CardModel> before, IReadOnlyList<CardModel> after)
    {
        var run = owner.RunState;
        if (run is null || run.Players.Count < 2) return new Change(0, 0, 0);
        var ownBefore = Summarize(owner, before);
        var ownAfter = Summarize(owner, after);
        var party = new List<TeamSynergyMath.Seat> { ownBefore };
        foreach (var player in run.Players.OrderBy(p => p.NetId))
        {
            if (player.NetId == owner.NetId) continue;
            if (TeamBuildingContext.TryGet(run, player.NetId)?.Synergy is { } snapshot)
                party.Add(snapshot);
        }
        if (party.Count < 2 || (!party.Any(s => s.Components.Count > 0) && ownAfter.Components.Count == 0))
            return new Change(0, 0, 0);

        var health = Math.Max(1, party.Sum(s => s.Attack + s.OtherDamage) * 6);
        var pressure = new TeamSynergyMath.Pressure(health, 8, 2, 0, 3);
        var fullBefore = TeamSynergyMath.Evaluate(party, pressure, 0);
        var soloBefore = TeamSynergyMath.Evaluate(new[] { ownBefore }, pressure);
        var damageBefore = TeamSynergyMath.ConstructionDamageResidual(party, pressure, fullBefore.Damage, soloBefore.Damage);
        party[0] = ownAfter;
        var fullAfter = TeamSynergyMath.Evaluate(party, pressure, 0);
        var soloAfter = TeamSynergyMath.Evaluate(new[] { ownAfter }, pressure);
        var damageAfter = TeamSynergyMath.ConstructionDamageResidual(party, pressure, fullAfter.Damage, soloAfter.Damage);
        var damage = Math.Clamp(6 * (damageAfter - damageBefore), -health, health);
        var prevention = 3 * (fullAfter.Prevention - fullBefore.Prevention);
        return new Change(TeamSynergyMath.BoundedMarginal(damage + prevention), damage, prevention);
    }
}

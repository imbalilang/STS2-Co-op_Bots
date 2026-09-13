using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

internal static class HumanCoopAdvisor
{
    // Only verified, uncomplicated debuff cards may bypass the normal output permit.
    private static readonly HashSet<string> Openers = new(StringComparer.Ordinal)
        { "Bash", "Uppercut", "Shockwave", "Neutralize", "LegSweep", "SuckerPunch", "GoForTheEyes",
          "PiercingWail", "DarkShackles", "EnfeeblingTouch", "DyingStar", "Mangle", "CrushUnder", "Malaise" };

    internal static BotBrain.CombatMove? Opening(Player player, IReadOnlyList<Player> party)
    {
        return BotBrain.LegalCombatMoves(player).Where(m => Openers.Contains(m.Card.GetType().Name))
            .Select(m => m with { Score = SetupValue(m, party), Reason = "cooperative-setup" })
            .Where(m => m.Score > 0).OrderByDescending(m => m.Score)
            .ThenBy(m => m.Card.Id.Entry, StringComparer.Ordinal).Cast<BotBrain.CombatMove?>().FirstOrDefault();
    }

    private static double Power(Creature c, string name) =>
        c.Powers.Where(p => p.GetType().Name == name).Sum(p => (double)p.Amount);

    internal static double SetupValue(BotBrain.CombatMove move, IReadOnlyList<Player> party)
    {
        var card = move.Card;
        var owner = card.Owner;
        var combat = owner.Creature.CombatState;
        if (combat is null || owner.PlayerCombatState is null) return 0;
        var facts = GeniusCombatStrategy.Analyze(card, move.Target, owner.PlayerCombatState.Energy, combat.HittableEnemies.Count());
        if (facts.HpLoss > 0) return 0;
        if (facts.DamagePerEnemy > 0 && move.Target is { } attacked
            && Power(attacked, "ThornsPower") >= owner.Creature.CurrentHp) return 0;
        var weak = facts.Weak;
        var vulnerable = facts.Vulnerable;
        var strengthDown = facts.StrengthDown;
        // Shockwave uses a generic Power variable, not typed Weak/Vulnerable vars.
        if (card.GetType().Name == "Shockwave") weak = vulnerable = card.DynamicVars["Power"].IntValue;
        double value = 0;
        foreach (var enemy in combat.HittableEnemies.Where(e => e.IsAlive && (move.Target is null || move.Target == e)))
        {
            if (Power(enemy, "ArtifactPower") > 0) continue;
            // Do not finish the encounter or steal a human's planned kill through the setup exception.
            if (facts.DamagePerEnemy >= enemy.CurrentHp + enemy.Block && facts.DamagePerEnemy > 0) continue;
            // Cutting the enemy's Strength removes damage from every remaining
            // hit this turn, for every teammate it targets, so it is worth more
            // than an equivalent amount of block and should be suggested first.
            if (strengthDown > 0)
            {
                var repeats = enemy.Monster?.NextMove.Intents.OfType<AttackIntent>().Sum(intent => intent.Repeats) ?? 0;
                if (repeats > 0)
                {
                    var living = party.Count(p => p.Creature.IsAlive);
                    var threat = party.Where(p => p.Creature.IsAlive).Sum(p => CombatAssessment.FromEnemy(enemy, p.Creature));
                    value += Math.Min(threat, strengthDown * repeats * living) * 3.0;
                }
            }
            if (weak > 0 && Power(enemy, "WeakPower") <= 0)
                value += party.Where(p => p.Creature.IsAlive).Sum(p =>
                    Math.Min(CombatAssessment.Uncovered(p.Creature), CombatAssessment.FromEnemy(enemy, p.Creature) * .25)
                    * CombatAssessment.HumanWeight(p.Creature)) * 4;
            if (vulnerable > 0 && Power(enemy, "VulnerablePower") <= 0)
            {
                // A currently affordable attack is an opportunity, never a committed human action.
                foreach (var ally in party.Where(p => p != owner && p.Creature.IsAlive && p.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                    && !CombatManager.Instance.IsPlayerReadyToEndTurn(p)))
                {
                    var damage = BotBrain.LegalCombatMoves(ally).Where(m => m.Target == enemy && m.Card.Type == CardType.Attack)
                        .Select(m => GeniusCombatStrategy.Analyze(m.Card, enemy, ally.PlayerCombatState!.Energy, 1).TotalDamage)
                        .DefaultIfEmpty(0).Max();
                    value += Math.Min(damage, enemy.CurrentHp + enemy.Block) * .5;
                }
            }
        }
        // Reserve enough energy for known survival cards when an offensive setup would otherwise prevent them.
        if (facts.Block <= 0 && CombatAssessment.InDanger(owner.Creature) && weak <= 0 && strengthDown <= 0) return 0;
        return value > 0 ? value + 1 : 0;
    }

    internal static BotBrain.CombatMove? CombatAdvice(Player human, IReadOnlyList<Player> party)
    {
        var teamMove = TeamCoordinator.Advise(human, party);
        if (teamMove.HasValue) return teamMove;
        var legal = BotBrain.LegalCombatMoves(human, BotDifficulty.Genius);
        var scored = GeniusCombatStrategy.ScoreLegalMoves(human, legal, 0)
            .Select(m => m with { Score = m.Score + SetupValue(m, party) })
            .OrderByDescending(m => m.Score).ToList();
        return scored.Count > 0 && scored[0].Score > 0 ? scored[0] : null;
    }

    // Deck-aware card valuation shared by rewards, the shop, upgrades and
    // removals. Uses the same modeled numbers the combat planner trusts, plus
    // deck-need and dilution signals, so a mature deck stops taking filler.
    internal static (double Score, string Reason) CardValue(CardModel card, Player player)
    {
        if (card.Type is CardType.Curse or CardType.Status) return (-1000, "避免主动拿入诅咒或状态牌");
        var deck = player.Deck.Cards;
        var attacks = deck.Count(c => c.Type == CardType.Attack);
        var blocks = deck.Count(c => c.GainsBlock);
        var powers = deck.Count(c => c.Type == CardType.Power);
        var aoe = deck.Count(c => c.Type == CardType.Attack && c.TargetType == TargetType.AllEnemies);
        var duplicates = deck.Count(c => c.Id == card.Id);

        double damage = 0, block = 0, draw = 0, energy = 0, vuln = 0, weak = 0;
        double scaling = 0, poison = 0, doom = 0;
        try
        {
            var facts = GeniusCombatStrategy.Analyze(card, null, 3, 1);
            damage = facts.DamagePerEnemy;
            block = facts.Block;
            draw = facts.Draw;
            energy = facts.ImmediateEnergy;
            vuln = facts.Vulnerable;
            weak = facts.Weak;
            scaling = facts.Strength + facts.Dexterity + facts.Focus;
            poison = facts.Poison;
            doom = facts.Doom;
        }
        catch
        {
            // A card whose preview cannot be built is judged on type/rarity alone.
        }

        var cost = card.EnergyCost.CostsX ? 3 : Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All));
        var score = 8.0;
        var reason = "补充可用牌；仍需结合构筑判断";
        score += card.Rarity switch { CardRarity.Rare => 10, CardRarity.Uncommon => 4, _ => 0 };
        score += Math.Min(55, damage * 1.2 + block);
        score += draw * 4 + energy * 8;
        score += (vuln + weak) * 3;
        score += scaling * 7;
        score += (poison + doom) * 3;
        score += (3 - Math.Min(3, cost)) * 2;
        if (card.IsUpgraded) score += 3;
        if (card.Keywords.Contains(CardKeyword.Exhaust) && damage <= 0 && block <= 0) score -= 6;

        if (card.GainsBlock && blocks < Math.Max(3, attacks / 2)) { score += 14; reason = "当前牌组防御偏少"; }
        if (card.Type == CardType.Attack && attacks < 5) { score += 10; reason = "补足前期输出"; }
        if (card.Type == CardType.Power && powers < 3) { score += 12; reason = "补充长线成长能力"; }
        if (card.TargetType == TargetType.AllEnemies && aoe == 0) { score += 8; reason = "补充群体伤害，应对多敌"; }
        if (draw > 0 && deck.Count < 22) { score += 4; reason = "增加过牌，提升稳定性"; }
        if (scaling > 0 && powers >= 3) { score -= 6; }
        score -= duplicates * 5;

        // Adding cards dilutes a large deck: only premium cards are worth it.
        if (deck.Count >= 25 && score < 30)
        {
            score -= (deck.Count - 24) * 2;
            reason = "牌组已偏大，普通牌会稀释关键抽牌";
        }
        return (score, reason);
    }

    // Whether a card already in the deck is worth upgrading. This is the real
    // difference the upgrade makes to the card itself (cost, damage, block,
    // draw, roles unlocked), not the card's value as an addition — a cost drop
    // can make a whole line playable and must outrank extra raw numbers.
    internal static double UpgradeValue(CardModel card, Player player)
        => Building.BuildValue.UpgradeDelta(card, player).Total;

    internal static (double Score, string Reason) RelicValue(RelicModel relic, Player player)
    {
        var party = player.RunState.Players;
        var attacks = party.Sum(p => p.Deck.Cards.Count(c => c.Type == CardType.Attack));
        var sameRelics = party.Sum(p => p.Relics.Count(r => r.Id == relic.Id));
        var sharedDiscount = 1.0 / (1 + sameRelics);
        // Enemy-buff relics are the mirror of team support: the upside belongs to
        // the holder only, while every point of enemy Strength is paid by every
        // member. Value them by net team effect, not as a personal buff.
        if (relic.GetType().Name == "Brimstone")
        {
            var self = relic.DynamicVars.TryGetValue("SelfStrength", out var selfVar) ? selfVar.IntValue : 2;
            var enemy = relic.DynamicVars.TryGetValue("EnemyStrength", out var enemyVar) ? enemyVar.IntValue : 1;
            var ownerAttacks = player.Deck.Cards.Count(c => c.Type == CardType.Attack);
            var upside = Math.Min(42, self * ownerAttacks * 1.5);
            var downside = enemy * party.Count * 12;
            return (upside - downside,
                $"仅持有者每回合+{self}力量；敌人每回合+{enemy}力量，全队{party.Count}人都要多承受，人数越多越亏");
        }
        if (relic.GetType().Name == "PhilosophersStone")
        {
            var enemy = relic.DynamicVars.TryGetValue("StrengthPower", out var stoneVar) ? stoneVar.IntValue : 1;
            var downside = enemy * party.Count * 12;
            return (28 - downside,
                $"仅持有者+1能量；敌人+{enemy}力量由全队{party.Count}人承担，多人下负面扩散");
        }
        if (relic.GetType().Name == "BagOfMarbles")
            return ((20 + Math.Min(30, attacks * 1.2)) * sharedDiscount
                / (1 + .15 * party.Sum(p => p.Deck.Cards.Count(c => c.GetType().Name is "Bash" or "Shockwave" or "Uppercut"))),
                "首回合易伤供全队攻击利用；按攻击储备估值，已有同类效果降低重复投入");
        if (relic.GetType().Name == "RedMask")
            return ((24 + Math.Min(24, party.Count * 6)) * sharedDiscount
                / (1 + .15 * party.Sum(p => p.Deck.Cards.Count(c => c.GetType().Name is "LegSweep" or "Shockwave" or "Uppercut"))),
                "首回合虚弱减轻团队战损；重复持有收益递减");
        if (relic.GetType().Name == "MassiveScroll") return (25, "提供多人专属卡选择，收益仍取决于实际候选卡");
        if (relic.GetType().Name == "MembershipCard") return (Math.Min(45, player.Gold * .08), "折扣价值受持有者金币限制；商店另计算购买回本");
        if (relic.GetType().Name == "TheCourier") return (Math.Min(40, 12 + player.Gold * .05), "持有者购物折扣及补货机会，不作为全队共享折扣");
        if (relic.GetType().Name == "MawBank") return (relic.IsMutable && relic.IsUsedUp ? 0 : 20, "后续房间收入；付费购物会终止，需要比较机会成本");
        if (relic.GetType().Name == "OldCoin") return (40, "金币可用于购买和删牌，不等同于即时战斗能力");
        var shareBlock = player.Deck.Cards.Any(c => c.GetType().Name is "BeaconOfHope" or "DemonicShield")
            || party.Any(p => p != player && p.Deck.Cards.Any(c => c.GetType().Name == "Mimic"));
        return relic.GetType().Name switch
        {
            "Anchor" => (player.Creature.CurrentHp < player.Creature.MaxHp * .6 ? 35 : 22, "首回合格挡，降低启动时的生存压力"),
            "BagOfPreparation" => (25, "首回合额外手牌，增加团队铺垫选择"),
            "Vajra" => (10 + player.Deck.Cards.Where(c => c.Type == CardType.Attack).Sum(c => c.GetType().Name is "TwinStrike" or "DaggerSpray" ? 2 : 1), "常驻力量，已识别多段攻击获得额外适配收益"),
            "OddlySmoothStone" => (12 + player.Deck.Cards.Count(c => c.GainsBlock) * 2 + (shareBlock ? 8 : 0), "常驻敏捷，兼顾格挡牌与团队分享／复制机会"),
            "Lantern" => (23, "首回合额外能量，减少铺垫和输出争抢费用"),
            "BloodVial" => (player.Creature.CurrentHp < player.Creature.MaxHp * .6 ? 26 : 12, "每场战斗首回合恢复生命，减轻持续战损"),
            "HornCleat" => (24, "第二回合获得格挡，缓解启动压力"),
            _ => (0, "尚无可靠的专属估值，请比较遗物描述与构筑")
        };
    }

    // Kept as the shared, context-free view of a room's worth; the full-act
    // planner adds act/deck context on top of the same numbers.
    internal static double RouteValue(MapPointType type, double health, int gold)
        => RoutePlanner.NodeValue(type, health, gold, act: 0, deckSize: 20);

    internal static string RouteAdvice(RunState state, Player human)
    {
        if (state.CurrentMapPoint is null) return "路线建议：先选择起点；队友会跟随你的投票。";
        var health = state.Players.Where(p => p.Creature.IsAlive).Select(p => (double)p.Creature.CurrentHp / Math.Max(1, p.Creature.MaxHp)).DefaultIfEmpty(1).Min();
        var shoppers = state.Players.Count(p => p.Creature.IsAlive && p.Gold >= 100);
        var names = new Dictionary<MapPointType, string> { [MapPointType.RestSite] = "休息点", [MapPointType.Shop] = "商店", [MapPointType.Elite] = "精英", [MapPointType.Monster] = "普通战斗", [MapPointType.Unknown] = "事件", [MapPointType.Treasure] = "宝箱", [MapPointType.Boss] = "Boss", [MapPointType.Ancient] = "先古" };
        var plan = RoutePlanner.Plan(state, human);
        var next = plan?.Path.Count > 1 ? plan.Path[1] : null;
        if (next is null) return "暂无可选路线";
        var route = plan!.Path.Skip(1).Take(3)
            .Select(p => names.GetValueOrDefault(p.PointType, p.PointType.ToString()));
        var warning = "";
        if (next.PointType == MapPointType.Elite)
        {
            var maturity = RoutePlanner.DeckMaturity(human);
            if (state.CurrentActIndex >= 1 || health < .7 || maturity < .45)
                warning = $"\n注意：{state.CurrentActIndex + 1} 层精英强度远高于本层普通战，当前血量 {health:P0}、构筑成熟度 {maturity:P0}"
                    + (health < .7 ? "，血量偏低" : maturity < .45 ? "，构筑尚未成型" : "")
                    + "；建议谨慎或改走其他分支。";
        }
        return $"路线建议：{string.Join(" → ", route)}（已在地图上画线）。\n按队伍最低血量 {health:P0}、你的金币 {human.Gold}、有购物预算的队员 {shoppers} 人做整幕路径搜索；由你决定。{warning}";
    }
}

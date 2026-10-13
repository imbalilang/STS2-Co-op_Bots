using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Logging;

namespace CoopBots.Building;

/// <summary>
/// One valuation for permanent deck changes — reward picks, removals, upgrades
/// and shop investments — so the same card is judged the same way everywhere.
///
/// Every decision is a difference against the deck as it stands, which makes
/// skipping a real candidate instead of an afterthought: a card is only worth
/// taking when its own contribution exceeds the draws it takes away from the
/// rest of the deck. The shared potential combines native card effects and
/// documented community priors; missing effects carry an explicit estimate.
/// </summary>
internal static class BuildValue
{
    // A card's own contribution, before it is compared against the deck.
    private const double Base = 8;
    private const double DamageWeight = 1.2;
    private const double BlockWeight = 1.0;
    private const double DamageCap = 55;
    private const double DrawWeight = 4;
    private const double EnergyWeight = 8;
    private const double DebuffWeight = 3;
    private const double ScalingWeight = 7;
    private const double DoomWeight = 3;
    private const double DuplicatePenalty = 5;
    // A recognised payoff whose trigger is missing keeps this share of its
    // scaling: enough that a route can still be started, not enough to read as
    // an assembled plan.
    private const double UnsupportedScalingFactor = 0.4;
    // A mechanism-supported card is worth this much of the size pressure waived.
    // Continuous and bounded; it can never switch the pressure off entirely the
    // way the old binary "on plan" test did.
    private const double MechanismRelief = 0.5;
    // For an amplifier whose own numbers are not scaling-based (Accuracy buffs
    // Shivs, not itself) the payoff still needs triggers, but it is a small
    // bounded term rather than a share of a scaling number that is zero.
    private const double MechanismPayoffPerTrigger = 2.0;
    private const double MechanismPayoffCap = 6.0;
    private const double MechanismUnsupportedPenalty = 4.0;
    // A producer whose resource has a separate multiplier/replayer that the deck does not
    // hold yet. Bounded and small: it makes the incomplete package visible, but a good
    // standalone producer (Hidden Daggers) can still be worth taking on its own numbers.
    private const double ProducerWithoutPayoffPenalty = 5.0;
    // The most of a card's own pre-team contribution the team bonus may add.
    // Modest and absolute-free so it self-normalises across cards; it is what
    // stops a large flat team term from carrying a card the deck cannot use.
    private const double TeamBonusBuildShare = 0.5;
    /// <summary>
    /// Cards the bot must never acquire, whatever any prior says about them.
    ///
    /// 上游怎么做：CombatSolver 没有牌名黑名单，获取只看 Elo / 结构先验 —— 任何牌都可能被拿走。
    /// 我们怎么做：命中即返回固定的 <see cref="AcquisitionBlacklistTotal"/>，
    /// 在 <see cref="AddDetailed"/> 的第一行短路，压在全部先验与情境加成之前，
    /// 因此它低于 skip 的 0，也低于任何真实候选。
    /// 为什么：THE_GAMBIT 打出后挂 THE_GAMBIT_POWER —— 吃到任意一次未被格挡的
    /// powered attack 伤害就直接处决（TheGambitPower.AfterDamageReceived →
    /// CreatureCmd.Kill），**无视剩余血量**，是"构筑里带一颗地雷"而不是"少一点数值"。
    /// 实机 2026-09-29 二层第一战 THIEVING_HOPPER_WEAK（floor 18）：B2 自己的剧本在
    /// 第 25/26 步连打 THE_GAMBIT + PANIC_BUTTON（后者 NoBlockPower 让格挡全变 0），
    /// turn3 被 HAT_TRICK 触发处决，52/80 血满着死；而模拟器因漏掉敌人 Power 的
    /// 多人缩放（Flutter 实机 24 层 vs 内核 5 层）预测 4 人全活 VICTORY、一步未拒。
    /// 也就是说估值模型在这里**读不出**这张牌的代价，只能人工拉黑。
    /// 用户 2026-09-29：「直接把 THE_GAMBIT 拉入黑名单……-100 获取意向分数（赌狗不得好死）」。
    /// </summary>
    private static readonly HashSet<string> AcquisitionBlacklist = new(StringComparer.Ordinal)
    {
        // 用户 2026-09-29：赌狗不得好死。
        "THE_GAMBIT",
        // 用户 2026-10-02：梦魇。内核**能**模拟它——--nightmare-probe 显示选择走
        // CardChoiceSupport，NightmarePower 落地，下回合真的多 3 张复制；缺 OnPlay
        // 镜像只让 PredictionCoverage 记一个 compensated 风险，不是空操作。
        // 问题在锦标赛的终局目标（剩余敌人/HP/回合）不评价"延迟复制"这个 setup turn，
        // 实机 12 场 0 次出牌。修搜索目标的成本远高于收益，按用户"太难就压死"的授权，
        // 获取阶段直接 -100；等终局目标能评价 setup 再撤。
        "NIGHTMARE",
        // 用户 2026-10-02：应急按钮。本地卡面是"获得 X 格挡；接下来 N 回合无法从卡牌获得格挡"。
        // 内核确实应用了 NoBlockPower（CardEffectSpecRegistry.cs:85 Owner<NoBlockPower>("Turns")），
        // 所以不是模拟缺口；但锦标赛目标只看本回合剩余敌人/HP，不评价这条跨回合的格挡封锁。
        // 实机日志里 PANIC_BUTTON 被打了 22 次，多次是在不需要防御的回合先拿快照格挡，
        // 随后 Boss 输出时无法再叠甲。按"内核不会打"处理，获取阶段压死。
        "PANIC_BUTTON",
    };

    private const double AcquisitionBlacklistTotal = -100;

    // Starter composition is retained as a diagnostic, not a removal-price floor.
    private static bool IsPlainStarter(CardModel card)
        => card.Rarity == CardRarity.Basic && !card.IsUpgraded
           && (card.Tags.Contains(CardTag.Strike) || card.Tags.Contains(CardTag.Defend))
           && card.Enchantment is null && card.GetEnchantedReplayCount() <= 0;
    internal readonly record struct Valuation(double Total, string Reason)
    {
        internal double RepeatablePerFight { get; init; }
        // Owner upgrade preference; never a fabricated deck-potential gain.
        internal int UpgradePriority { get; init; }
    }

    internal static IOrderedEnumerable<(CardModel Card, Valuation Value)> RankUpgrades(
        IEnumerable<(CardModel Card, Valuation Value)> upgrades)
    {
        var all=upgrades.ToArray();
        if(all.Length>0 && !ConstructionDecision.AwaitingValues)
            Log.Info($"CoopBots upgrade candidates: bot={all[0].Card.Owner.NetId} all=["
                +string.Join(';',all.Select(e=>$"{e.Card.Id.Entry}+{e.Card.CurrentUpgradeLevel}:priority={e.Value.UpgradePriority}:value={e.Value.Total:F2}:reason={e.Value.Reason}"))+"]");
        return all.OrderByDescending(e=>e.Value.UpgradePriority)
            .ThenByDescending(e=>e.Value.Total).ThenBy(e=>e.Card.Id.Entry,StringComparer.Ordinal);
    }

    internal static IReadOnlyList<CardModel> EligibleUpgrades(IEnumerable<CardModel> cards)
    {
        var all=cards.Where(c=>c.IsUpgradable).ToArray();
        var allowed=all.Where(c=>!BotBrain.IsForbiddenUpgrade(c)).ToArray();
        return allowed.Length>0?allowed:all;
    }

    /// <summary>
    /// Whether a removal is refused because the card is the deck's last copy of
    /// a role it depends on. <see cref="Role"/> names that role, so a caller can
    /// rank around the refusal without parsing the "protected:" reason string.
    /// </summary>
    internal readonly record struct RemovalGuard(bool Protected, string? Role);

    internal sealed record DeckCard(CardModel Card, int Upgrades) { }

    private static Valuation? UnplayableForMissingResource(CardModel card, IReadOnlyList<CardModel> deck, Player player)
        => ResourceStartup.Missing(card, deck, player) is { } resource
            ? new Valuation(-ResourceStartup.Penalty, "unplayable:no-" + resource + ",missing-startup:" + resource)
            : null;

    /// <summary>
    /// What this card is worth to this deck right now: its own output, weighted
    /// down for roles the deck already covers, with a role-aware nudge for what
    /// the deck is missing. Negative when the card would only dilute.
    /// </summary>
    internal static Valuation Marginal(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var deck = deckOverride ?? player.Deck.Cards.ToList();
        return MarginalCore(card, player, deck, DeckStructure.Build(deck, StableEnergy(player)));
    }

    // The character's persistent per-combat energy, restored from the save, is
    // the stable supply a permanent building decision should use. It is not the
    // remaining energy of the current fight. Any positive value is accepted as
    // the character's real curve — a high-energy character must not be capped to
    // satisfy a mock fixture. Only a missing or nonpositive value falls back to
    // the documented conservative baseline. No precise solvability is claimed.
    internal static int StableEnergy(Player player)
    {
        try
        {
            var energy = player.MaxEnergy;
            return energy >= 1 ? energy : DeckStructure.DefaultMaxEnergy;
        }
        catch { return DeckStructure.DefaultMaxEnergy; }
    }

    private static Valuation MarginalCore(CardModel card, Player player, IReadOnlyList<CardModel> deck,
        DeckStructure.Summary summary)
    {
        var facts = CardProfile.Of(card);
        if (card is MegaCrit.Sts2.Core.Models.Cards.GrandFinale)
        {
            var candidateDeck = deck.Any(c => ReferenceEquals(c, card)) ? deck : deck.Append(card).ToArray();
            facts = facts with { Damage = facts.Damage * GrandFinaleAvailability.Estimate(candidateDeck, StableEnergy(player)) };
        }
        // A curse or status is never a card the deck wants: it occupies a draw
        // without contributing. This has to come first, because the cost and
        // rarity terms below would otherwise make one look roughly neutral.
        if (facts.Unplayable || card.Type is CardType.Curse or CardType.Status)
            return new Valuation(-1000, card.Type == CardType.Curse ? "curse" : "status");
        // Spending a resource the deck cannot produce is not a weak card, it is an
        // unplayable one: Seven Stars is seven hits of seven for seven Stars and
        // never leaves the hand without a producer. The scorer priced cards on
        // their printed numbers regardless, so a Regent whose producers had been
        // stolen still picked it up, and so did a cross-class bot that can make no
        // Stars at all. Both are the same fact, so they get the same refusal.
        if (UnplayableForMissingResource(card, deck, player) is { } unpayable) return unpayable;
        // Membership is instance identity, not id count: a new reward card is a
        // different instance from any copy already held, and an upgraded instance
        // can fill a different role. Only the actual candidate may be treated as
        // its own producer or as its own saturation.
        var candidateInDeck = deck.Any(existing => ReferenceEquals(existing, card));
        var mechanism = DeckMechanisms.Assess(card, facts, summary, candidateInDeck);
        var saturation = RoleSaturation(facts, card, summary, candidateInDeck);
        var score = Base + RarityBonus(card);
        var reasons = new List<string>();

        // A producer whose payoff is a separate multiplier that the deck does not hold.
        // This is the shiv/poison half of the 2026-09-22 Silent loss: the deck drafted
        // Blade Dance / Hidden Daggers, never saw the payoff, and the route ended as a pile
        // of enablers. Only producers that Assess left Unknown take this; a card that is
        // itself an amplifier goes through the existing supported/unsupported branch.
        if (mechanism.Support == DeckMechanisms.Support.Unknown
            && DeckMechanisms.ProducerWithoutPayoff(card, summary) is { } missingPayoff)
        {
            score -= ProducerWithoutPayoffPenalty;
            reasons.Add("producer-no-payoff:" + missingPayoff);
        }

        var offense = Math.Min(DamageCap, facts.Damage * DamageWeight + facts.Block * BlockWeight);
        if (offense > 0)
        {
            var weighted = offense * RoleWeight(saturation, facts, "damage", "block");
            score += weighted;
            reasons.Add(facts.IsAoe ? "aoe" : facts.Damage > 0 ? "damage" : "block");
        }

        // Cycling is useful, but drawing then paying another hand card is not
        // the same engine as actually increasing the playable hand.
        var effectiveDraw = Math.Min(1, facts.Draw) + CardProfile.NetDraw(card, facts);
        var engine = effectiveDraw * DrawWeight + facts.Energy * EnergyWeight
            + .7 * (facts.NextTurnDraw * DrawWeight + facts.NextTurnEnergy * EnergyWeight);
        if (engine > 0)
        {
            score += engine * RoleWeight(saturation, facts, "draw", "energy");
            reasons.Add("engine");
        }

        var debuff = (facts.Vulnerable + facts.Weak + facts.StrengthDown) * DebuffWeight;
        if (debuff > 0)
        {
            score += debuff * RoleWeight(saturation, facts, "vulnerable", "weak", "strengthdown");
            reasons.Add("setup");
        }

        var scaling = facts.Scaling * ScalingWeight + (facts.Poison + facts.Doom) * DoomWeight;
        if (scaling > 0)
        {
            var weighted = scaling * RoleWeight(saturation, facts, "scaling", "poison", "doom");
            if (mechanism.Support == DeckMechanisms.Support.Unsupported)
            {
                // The mechanism is recognised and its trigger is not in the deck:
                // the scaling is a promise, not a plan. Keep a share so a route
                // can still be started, but stop it outbidding real route cards.
                score += weighted * UnsupportedScalingFactor;
                reasons.Add("scaling-unsupported:" + mechanism.Resource);
            }
            else
            {
                score += weighted;
                reasons.Add(mechanism.Support == DeckMechanisms.Support.Supported
                    ? "scaling-supported:" + mechanism.Resource
                    : "scaling");
            }
        }
        else if (mechanism.Payoff && mechanism.Support != DeckMechanisms.Support.Unknown)
        {
            // An amplifier whose own printed numbers are not scaling (Accuracy
            // raises Shiv damage, not its own) still only pays when its triggers
            // exist. Bounded modestly so it is a heuristic term, not a claim
            // about exact strength or win rate.
            if (mechanism.Support == DeckMechanisms.Support.Unsupported)
            {
                score -= MechanismUnsupportedPenalty;
                reasons.Add("scaling-unsupported:" + mechanism.Resource);
            }
            else
            {
                score += Math.Min(MechanismPayoffCap, mechanism.Triggers * MechanismPayoffPerTrigger);
                reasons.Add("scaling-supported:" + mechanism.Resource);
            }
        }

        var lifecycle = DraftCardEffects.Value(facts.Effects, deck, StableEnergy(player));
        if (lifecycle > 0) { score += lifecycle; reasons.Add($"lifecycle:{lifecycle:F1}"); }
        if (facts.Effects.ExhaustRequired > 0)
            reasons.Add($"exhaust-condition:{DraftCardEffects.ConditionalAvailability(facts.Effects, deck):F2}");
        score += (3 - Math.Min(3, facts.Cost)) * 2;
        if (facts.HpLoss > 0) score -= facts.HpLoss * 2.5;
        if (card.IsUpgraded) score += 3;
        if (facts.Roles.Contains("exhaust") && facts.Damage <= 0 && facts.Block <= 0) score -= 6;

        score += GapBonus(facts, summary, reasons);
        // A support card or a team-wide debuff setup is worth more here than it is
        // alone. The same helper backs the team coordinator, so a reward and a
        // shop purchase cannot disagree about the card. The absolute cap in
        // TeamCoordinator is not enough on its own: on this scale a flat +18 can
        // carry a card the deck has no use for, so the bonus is additionally
        // bounded to a modest share of the card's own pre-team useful
        // contribution. HumanCoopAdvisor uses a 1.5x share on its own scale; that
        // number is deliberately not copied here. No recursive valuation.
        var team = CoopBots.TeamCoordinator.TeamBonus(card, player, deck, summary);
        if (team > 0)
        {
            var allowed = Math.Max(0, score) * TeamBonusBuildShare;
            var applied = Math.Min(team, allowed);
            if (applied > 0.01)
            {
                score += applied;
                reasons.Add("team-fit");
            }
        }
        // Cards drafted alongside this one far more often than chance. A nudge
        // only: it is a prior from average play, bounded by AffinityCap, and the
        // direction is the one the feed actually has — the held card's document
        // recommends the offered candidate.
        var affinity = Affinity(card, deck);
        if (affinity > 0.01)
        {
            score += affinity;
            reasons.Add("drafted-together");
        }
        // Feeds the route the deck is already on. Zero when no route is
        // recognised, so a deck nobody understands behaves exactly as before.
        var archetype = Archetypes.Bonus(facts, card, deck, player, out var route);
        if (Math.Abs(archetype) > 0.01)
        {
            score += archetype;
            // Naming the route makes the log auditable: "fits-route" alone cannot
            // tell whether the deck is converging on a build or just collecting.
            var label = archetype > 0 ? "fits-route" : "off-route";
            reasons.Add(route.Length == 0 ? label : $"{label}:{route}");
        }
        // Curve and balance bottlenecks. Bounded and contextual; never a ban.
        score += DeckStructure.Adjust(card, facts, summary, reasons);
        score -= DuplicateCost(card, mechanism, deck, reasons);
        return new Valuation(score, reasons.Count == 0 ? "no role" : string.Join(',', reasons));
    }

    /// <summary>Value of the whole deck as it stands.</summary>
    internal static double Deck(IReadOnlyList<CardModel> deck, Player player)
        => DeckStrength.Evaluate(deck, player).Total;

    // Structural traits are diagnostics and the imputation input for unseen effects.
    private static readonly string[] FitTokens =
    {
        "needs-block", "needs-damage", "needs-aoe", "needs-draw", "needs-energy",
        "fits-route", "scaling-supported:", "team-fit",
        "bottleneck:defence-quality", "bottleneck:needs-hand-advantage",
    };

    private static bool HasDeckFit(string reason) =>
        FitTokens.Any(token => reason.Contains(token, StringComparison.Ordinal));
    internal readonly record struct AddBreakdown(double Total, string Reason, double Coverage,
        bool DeckFit, bool UsedFallback, bool GuardRefusal, bool Contextual = true, double RepeatablePerFight = 0);

    /// <summary>Addition is the common deck strength difference, subject to acquisition vetoes.</summary>
    internal static Valuation Add(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var terms = AddDetailed(card, player, deckOverride);
        return new Valuation(terms.Total, terms.Reason) { RepeatablePerFight = terms.RepeatablePerFight };
    }

    /// <summary>
    /// Greedy marginal picks for a screen that lets you take ANY NUMBER of an offer
    /// (OROBAS / Sea Glass), plus the per-candidate scores in the order they were judged.
    ///
    /// Each pick is scored against the deck as it would stand WITH the picks already made —
    /// through the same <c>deckOverride</c> seam <see cref="Add"/> already exposes — so the
    /// fifth good card is not priced as if it were the first. Stops at the first candidate
    /// that no longer pays for the draws it costs, which is the "skipping is a real
    /// candidate" rule the reward screen uses; a mandatory floor (<paramref name="min"/>)
    /// is still honoured when nothing scores.
    ///
    /// Two callers share this so the price and the behaviour cannot disagree: the selector
    /// that answers the grid, and the relic valuation that prices the option which opens it.
    /// </summary>
    internal static (List<CardModel> Picked, List<(CardModel Card, double Value, string Reason)> Ranked)
        GreedyPick(Player player, IReadOnlyList<CardModel> offer, int min, int max)
    {
        using var evaluation = DeckStrength.BeginEvaluation();
        var working = player.Deck.Cards.ToList();
        var ranked = new List<(CardModel Card, double Value, string Reason)>();
        var picked = new List<CardModel>();
        var remaining = offer.Distinct()
            .OrderBy(card => card.Id.Entry, StringComparer.Ordinal).ToList();
        while (picked.Count < max && remaining.Count > 0)
        {
            var best = remaining
                .Select(card => (Card: card, Value: AddForDecision(card, player, working)))
                .OrderByDescending(entry => entry.Value.Total)
                .ThenBy(entry => entry.Card.Id.Entry, StringComparer.Ordinal)
                .First();
            ranked.Add((best.Card, best.Value.Total, best.Value.Reason));
            if (ConstructionDecision.AwaitingValues) break;
            if (best.Value.Total <= 0 && picked.Count >= min) break;
            picked.Add(best.Card);
            remaining.Remove(best.Card);
            working.Add(best.Card);
        }
        return (picked, ranked);
    }

    internal static AddBreakdown AddDetailed(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var deck = deckOverride ?? player.Deck.Cards.ToList();
        // Acquisition policy remains outside the strength model. These are
        // owner-requested/legality vetoes, not a second scoring algorithm.
        if (AcquisitionBlacklist.Contains(card.Id.Entry))
            return Guard($"blacklist:{card.Id.Entry.ToLowerInvariant()}", AcquisitionBlacklistTotal);
        var facts = CardProfile.Of(card);
        if (facts.Unplayable || card.Type is CardType.Curse or CardType.Status)
            return Guard(card.Type == CardType.Curse ? "curse" : "status", -1000);
        var added = deck.Any(c => ReferenceEquals(c, card)) ? (CardModel)card.MutableClone() : card;
        var change = DeckStrength.Change(deck, deck.Append(added).ToArray(), player);
        var traits = Marginal(card, player, deck);
        var known = BakedCardElo.TryGet(card.Id.Entry, out var elo) && CommunityDraft.IsFinite(elo);
        return new AddBreakdown(change.Total, change.Reason + $",card-coverage:{BuildCoverage.Of(card):F2},traits:" + traits.Reason,
            BuildCoverage.Of(card), HasDeckFit(traits.Reason), UsedFallback: !known,
            GuardRefusal: false, RepeatablePerFight: change.Total);
    }

    private static AddBreakdown Guard(string reason, double total) =>
        new(total, reason, 0, false, UsedFallback: true, GuardRefusal: true, Contextual: false);

    // Legacy composition diagnostics; neither changes the strength potential.
    private const double MaturityStart = 0.6;
    private const int HardSize = 32;
    private const int HardSizeRamp = 8;
    private static double BasicShare(IReadOnlyList<CardModel> deck)
        => deck.Count == 0 ? 0 : deck.Count(IsPlainStarter) / (double)deck.Count;

    /// <summary>Legacy diagnostic: 0 for plain starters, 1 for no plain starter.</summary>
    internal static double Maturity(Player player, IReadOnlyList<CardModel> deck)
        => Math.Clamp(1 - BasicShare(deck), 0, 1);

    /// <summary>Legacy composition diagnostic; not a pricing input.</summary>
    internal static double MaturityGate(Player player, IReadOnlyList<CardModel> deck)
    {
        var formed = Math.Clamp((Maturity(player, deck) - MaturityStart) / (1 - MaturityStart), 0, 1);
        var oversized = Math.Clamp((deck.Count - HardSize) / (double)HardSizeRamp, 0, 1);
        return Math.Max(formed, oversized);
    }

    /// <summary>
    /// Signed difference between the deck without this instance and the current deck.
    /// </summary>
    internal static Valuation Remove(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var deck = deckOverride ?? player.Deck.Cards.ToList();
        var index = deck.ToList().FindIndex(c => ReferenceEquals(c, card));
        if (index < 0) return new Valuation(0, "not in deck");
        var after = deck.Where((_, i) => i != index).ToArray();
        return DeckStrength.Change(deck, after, player);
    }

    /// <summary>
    /// The decision-layer value of an edit: the deck-strength difference plus the
    /// bounded team value the card carries for the party (a shared debuff the
    /// party is missing, or a payoff that reads the party's Vulnerable supply).
    ///
    /// Kept outside <see cref="Add"/>/<see cref="Remove"/>/<see cref="UpgradeDelta"/>
    /// on purpose: those three must stay exact transforms of the deck model
    /// (UnifiedDeckStrengthScenarios pins `add == deck difference` and
    /// `add + remove == 0`). Every decision path that picks, prices, upgrades or
    /// strips a card goes through these wrappers, so one card is worth the same
    /// on a reward, in a shop, at a camp and on an event screen.
    /// </summary>
    internal static Valuation AddForDecision(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var deck = deckOverride ?? player.Deck.Cards.ToList();
        var value = Add(card, player, deck);
        var team = AppliedTeamBonus(card, player, deck, value.Total);
        return team <= 0.01
            ? value
            : value with { Total = value.Total + team, Reason = value.Reason + $",team:+{team:F2}" };
    }

    internal static Valuation RemoveForDecision(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var deck = deckOverride ?? player.Deck.Cards.ToList();
        var value = Remove(card, player, deckOverride);
        if (value.Reason == "not in deck") return value;
        // Losing a card loses exactly what acquiring it would have gained: the
        // same card, the same before-deck, the same bound. That keeps the two
        // decision edits inverse as well, so a removal plan cannot strip the
        // party's only applier for less than a reward would have paid for it.
        var before = deck.Where(c => !ReferenceEquals(c, card)).ToArray();
        var team = AppliedTeamBonus(card, player, before, Add(card, player, before).Total);
        return team <= 0.01
            ? value
            : value with { Total = value.Total - team, Reason = value.Reason + $",team:-{team:F2}" };
    }

    internal static Valuation UpgradeForDecision(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var value = UpgradeDelta(card, player, deckOverride);
        if (CardProfile.Upgraded(card, StableEnergy(player)) is not { } upgraded) return value;
        var deck = deckOverride ?? player.Deck.Cards.ToList();
        var before = CoopBots.TeamCoordinator.TeamBonus(card, player, deck, null);
        var after = CoopBots.TeamCoordinator.TeamBonus(upgraded.Card, player, deck, null);
        var team = after - before;
        var combined=Math.Abs(team) <= 0.01
            ? value
            : value with { Total = value.Total + team, Reason = value.Reason + $",team:{team:+0.00;-0.00;0.00}" };
        return UpgradePreference.Apply(card,player,deck,combined);
    }

    // Decision-layer bound: a team term may add at most this share of the edit's
    // own deck-strength gain, so it can break a near-tie but never carry an edit
    // the deck itself does not want. Same share MarginalCore uses, measured
    // against the number the reward and shop paths actually rank by.
    private static double AppliedTeamBonus(CardModel card, Player player, IReadOnlyList<CardModel> deckBefore, double ownTotal)
    {
        var team = CoopBots.TeamCoordinator.TeamBonus(card, player, deckBefore, null);
        if (team <= 0) return 0;
        return Math.Min(team, Math.Max(0, ownTotal) * TeamBonusBuildShare);
    }

    /// <summary>Replace the native instance with an isolated native upgraded clone.</summary>
    internal static Valuation UpgradeDelta(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        if (CardProfile.Upgraded(card, StableEnergy(player)) is not { } upgraded)
            return new Valuation(0, "not upgradable");
        var deck = deckOverride ?? player.Deck.Cards.ToList();
        // Detached candidates occur on event previews; include that card in both
        // alternatives, so upgrade can never accidentally be valued as an addition.
        if (!deck.Any(c => ReferenceEquals(c, card))) deck = deck.Append(card).ToArray();
        var after = deck.Select(c => ReferenceEquals(c, card) ? upgraded.Card : c).ToArray();
        var change = DeckStrength.Change(deck, after, player);
        var diff = CardProfile.DiffUpgrade(card);
        var details = diff is null ? "" : $",cost-{diff.CostDelta},damage+{diff.DamageDelta:F0},block+{diff.BlockDelta:F0},"
            + $"draw+{diff.DrawDelta:F0},energy+{diff.EnergyDelta:F0},"
            + $"next-energy+{diff.After.NextTurnEnergy-diff.Before.NextTurnEnergy:F0},next-draw+{diff.After.NextTurnDraw-diff.Before.NextTurnDraw:F0},"
            + $"team-next-draw+{diff.After.NextTurnDrawForTeam-diff.Before.NextTurnDrawForTeam:F0}";
        return change with { Reason = change.Reason + details };
    }

    /// <summary>
    /// Signed value of an HP-cost change. Negative delta (the upgrade lowers the
    /// life paid) is worth the same 2.5 per point the pick valuation charges,
    /// with the sign flipped because the upgrade removes the cost.
    /// </summary>
    internal static double HpLossUpgradeValue(double hpLossDelta) => -hpLossDelta * 2.5;

    /// <summary>
    /// Value of adding or removing one keyword. Beneficial and harmful keyword
    /// transitions are the only ones that move the score; anything the enum
    /// names that this policy does not classify — and any keyword a future
    /// patch adds — stays neutral rather than being guessed at. Exhaust is
    /// scored contextually elsewhere, so it is neutral here.
    /// </summary>
    internal static double KeywordTransitionValue(CardKeyword keyword, bool added)
    {
        var name = keyword.ToString();
        if (string.Equals(name, "Exhaust", StringComparison.Ordinal)) return 0;
        var beneficial = name is "Innate" or "Retain";
        var harmful = name is "Ethereal" or "Unplayable";
        if (!beneficial && !harmful) return 0;
        var weight = beneficial ? 4.0 : -4.0;
        return added ? weight : -weight;
    }

    /// <summary>
    /// Best permanent change among the candidates, compared against skipping.
    /// With <paramref name="allowSkip"/> the result is -1 when doing nothing is
    /// at least as good as every candidate; without it the best candidate is
    /// returned even when all of them are worse than skipping, which is how the
    /// lower difficulties still pick something.
    /// </summary>
    /// <param name="takeBar">
    /// What "doing nothing" scores. Zero everywhere except the early floors, where
    /// <see cref="RewardTakeBar"/> lowers it. A card must BEAT this bar, so moving
    /// it can only change WHETHER the screen is taken — never which candidate wins,
    /// because every candidate still beats the others by the same margin.
    /// </param>
    internal static int BestReward(Player player, IReadOnlyList<CardModel> candidates,
        bool allowSkip = true, double takeBar = 0.0)
    {
        using var evaluation = DeckStrength.BeginEvaluation();
        var best = -1;
        var bestValue = takeBar;
        var bestAny = candidates.Count > 0 ? 0 : -1;
        var bestAnyValue = double.NegativeInfinity;
        for (var index = 0; index < candidates.Count; index++)
        {
            double value;
            try { value = AddForDecision(candidates[index], player).Total; }
            catch { continue; }
            if (value > bestValue) { bestValue = value; best = index; }
            if (value > bestAnyValue) { bestAnyValue = value; bestAny = index; }
        }
        return allowSkip ? best : bestAny;
    }

    /// <summary>
    /// The widest the reward screen's bar gets, at the first floor of act 1. The
    /// bar then decays to zero over <see cref="EarlyTakeRampFloors"/>.
    /// </summary>
    internal const double EarlyTakeBonus = 4.5;

    /// <summary>Act-1 floors over which <see cref="EarlyTakeBonus"/> decays to zero.</summary>
    internal const int EarlyTakeRampFloors = 16;

    /// <summary>
    /// What "skip this reward" scores, while the deck is still being formed.
    ///
    /// WHY THIS EXISTS. The community prior is a whole-run statistic: a BakedCardElo
    /// value is measured over the decks that carried the card to the end, so it prices
    /// end-game worth and says nothing about what a deck wants on floor 3. Measured
    /// live 2026-10-02 (A10, four seats): the screen's bar was a flat 0, so the draft
    /// skipped 9 of its act-1 rewards and every final deck was still 36% basic
    /// Strike/Defend, producing 5.4-13.0 damage per card at act 2 — the two lowest
    /// seats were the two whose drafts had been starved. The clearest case is a menu
    /// whose best candidate scored -2.8 (Dagger Throw, whose measured `combat` was the
    /// highest in that menu at +7.2) and was rejected outright.
    ///
    /// WHY THE BAR AND NOT THE PRIOR'S WEIGHT. De-weighting the community prior early
    /// was the first proposal and it fails the same live menu: with the prior scaled
    /// down, Sucker Punch (-12.0 prior, +13.5 measured combat) overtakes Backflip
    /// (+2.0 prior) on the run's FIRST reward. That re-orders a menu. Moving this bar
    /// cannot: it is the same constant in every candidate's comparison, so it decides
    /// only whether the screen is taken at all.
    /// </summary>
    internal static double RewardTakeBar(int act, int floor)
    {
        if (act > 0) return 0;
        var remaining = EarlyTakeRampFloors - Math.Clamp(floor, 0, EarlyTakeRampFloors);
        return -EarlyTakeBonus * remaining / EarlyTakeRampFloors;
    }

    // The reward screen also gets a size bar once the deck is already large. Without
    // it, every marginal positive card kept winning the size-0 comparison: live
    // 2026-10-04 Bot 2 drafted 22 -> 31 cards and its top damage card was still a
    // starting Strike.
    //
    // The count is now EffectiveDeckSize, shared with DeckStrength's total-side penalty
    // so the two cannot disagree. That was the 2026-10-06 FAN_OF_KNIVES refusal: raw
    // count 25 against an onset of 24, while 11 of the deck's 28 cards were
    // self-removing -- an EFFECTIVE 22.5, below the new onset, so the card is no longer
    // blocked for reasons that were really about cards that leave the deck after one play.

    /// <summary>
    /// Extra "skip" value while the seat holds Pael's Wing. The Wing turns a
    /// skipped card reward into a sacrifice toward a free relic, so a marginal
    /// card is worth less than the sacrifice; owner request 2026-10-04. Like
    /// the act-1 bar it only moves WHETHER a reward is taken, never which card
    /// wins the menu.
    /// </summary>
    internal const double PaelsWingTakeBarBonus = 4.5;

    /// <summary>
    /// <see cref="RewardTakeBar(int, int)"/> from live run state, plus the bounded
    /// large-deck bar. "No opinion" when the state cannot be read, so a fixture
    /// without a map keeps the historical bar of 0 rather than silently taking
    /// every slightly-negative card.
    /// </summary>
    internal static double RewardTakeBar(Player player)
    {
        try
        {
            var act = player.RunState.CurrentActIndex;
            var floor = player.RunState.ActFloor;
            var sizeBar = act >= 1
                ? EffectiveDeckSize.TakeBarPressure(player.Deck.Cards, player)
                : 0;
            // Pael's Wing: a skipped reward becomes a sacrifice instead of nothing.
            var wingBar = player.Relics.Any(relic => relic is MegaCrit.Sts2.Core.Models.Relics.PaelsWing)
                ? PaelsWingTakeBarBonus
                : 0;
            return RewardTakeBar(act, floor) + sizeBar + wingBar;
        }
        catch { return 0; }
    }

    // How much of the deck already fills the roles this card would fill, from
    // the single summary pass. Only the candidate *instance* is excluded when it
    // is already in the deck, so a card is never its own saturation — but a new
    // offered copy of an id the deck holds cannot subtract an existing copy, and
    // an upgraded instance may fill a different role than the copy it is compared
    // against. The summary's role counts are reused unchanged.
    private static Dictionary<string, double> RoleSaturation(CardProfile.Facts facts, CardModel card,
        DeckStructure.Summary deck, bool candidateInDeck)
    {
        var inDeck = candidateInDeck ? 1 : 0;
        var saturation = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (role, count) in deck.RoleCounts)
        {
            var effective = facts.Roles.Contains(role) ? Math.Max(0, count - inDeck) : count;
            saturation[role] = Decay(effective);
        }
        return saturation;
    }

    private static IReadOnlySet<string> RolesOf(CardModel card)
    {
        try { return CardProfile.Of(card).Roles; }
        catch { return new HashSet<string>(StringComparer.Ordinal); }
    }

    private static double Decay(int existing) => existing switch { < 4 => 1.0, < 7 => 0.7, _ => 0.4 };

    // Applies the worst decay among the roles a term is driven by.
    private static double RoleWeight(IReadOnlyDictionary<string, double> saturation, CardProfile.Facts facts, params string[] roles)
    {
        var weight = 1.0;
        foreach (var role in roles)
        {
            if (!facts.Roles.Contains(role)) continue;
            weight = Math.Min(weight, saturation.GetValueOrDefault(role, 1.0));
        }
        return weight;
    }

    private static double RarityBonus(CardModel card) => card.Rarity switch
    {
        CardRarity.Rare => 10,
        CardRarity.Uncommon => 4,
        _ => 0,
    };

    // Counts *other* copies. Including the card itself depressed every card
    // already in the deck by one copy's penalty while the candidate never paid
    // it, which biased every comparison toward adding more cards.
    private static int Duplicates(CardModel card, IReadOnlyList<CardModel> deck)
        => deck.Count(existing => !ReferenceEquals(existing, card) && existing.Id == card.Id);

    // A duplicate mechanism payoff is not universally redundant: how much the
    // extra copy is worth depends on how many triggers the deck already holds
    // against how many copies of the payoff there are. More triggers than
    // copies keeps the second copy live; a copy past its triggers is dead
    // weight, and pays a larger penalty.
    private static double DuplicateCost(CardModel card, DeckMechanisms.Assessment mechanism,
        IReadOnlyList<CardModel> deck, List<string> reasons)
    {
        var copies = Duplicates(card, deck);
        if (copies <= 0) return 0;
        var penalty = DuplicatePenalty;
        if (mechanism.Payoff)
        {
            var triggers = Math.Max(0, mechanism.Triggers);
            var factor = Math.Clamp(0.5 + 0.35 * copies / (triggers + 1.0), 0.5, 2.0);
            penalty *= factor;
            reasons.Add($"dup-payoff:{factor:F2}");
        }
        return copies * penalty;
    }

    // Value per point of lift for a partner already in the deck, the most
    // partners that may pay at once, how much lift counts in full, and the
    // ceiling on the whole term. Deliberately small: a strong role fit is worth
    // ~8-14, so affinity is a prior that can move a close ranking but makes no
    // claim to be decisive, and it never touches the size pressure.
    private const double AffinityPerLift = 1.2;
    private const int AffinityPartners = 2;
    private const double AffinityLiftCap = 2.5;
    private const double AffinityCap = 6.0;

    // The feed is directional: the document for a held card B lists the cards A
    // that were offered alongside it, so "held B recommends A" is the edge the
    // data actually contains. The old code looked the candidate's own document
    // up and searched the deck for its partners, reading the arrow backwards.
    // Here the deck is the context: each held card's partners are checked for
    // the candidate, and only the strongest two positive lifts pay.
    private static double Affinity(CardModel card, IReadOnlyList<CardModel> deck)
    {
        if (deck.Count == 0) return 0;
        var candidate = card.Id.Entry;
        var lifts = new List<double>();
        foreach (var held in deck)
        {
            if (ReferenceEquals(held, card)) continue;
            if (!BakedCardAffinity.Pairs.TryGetValue(held.Id.Entry, out var partners) || partners.Length == 0) continue;
            foreach (var (partner, lift) in partners)
            {
                if (!string.Equals(partner, candidate, StringComparison.Ordinal)) continue;
                if (lift > 1.0) lifts.Add(lift);
                break;
            }
        }
        if (lifts.Count == 0) return 0;
        lifts.Sort((left, right) => right.CompareTo(left));
        var bonus = 0.0;
        for (var index = 0; index < Math.Min(AffinityPartners, lifts.Count); index++)
            bonus += AffinityPerLift * Math.Min(lifts[index], AffinityLiftCap);
        return Math.Min(bonus, AffinityCap);
    }

    // Filling a role the deck has none of is worth more than a second copy of a
    // role it already covers. The functional deficit that drives the size relief
    // uses the same thresholds, so a card cannot be told it fills a gap and then
    // be charged the full penalty for being another card.
    private static double GapBonus(CardProfile.Facts facts, DeckStructure.Summary deck, List<string>? reasons)
    {
        var bonus = 0.0;
        void Add(double worth, string label, bool needed)
        {
            if (!needed) return;
            bonus += worth;
            reasons?.Add(label);
        }
        Add(14, "needs-block", facts.Roles.Contains("block") && deck.Blocks < Math.Max(3, deck.Attacks / 2));
        Add(10, "needs-damage", facts.Roles.Contains("damage") && deck.Attacks < 5);
        // No generic Power quota: "fewer than three Powers" is not a deficit in
        // every build, and it was promoting support-free payoffs. A Power with a
        // real mechanism is still valued through DeckMechanisms and its roles.
        Add(8, "needs-aoe", facts.Roles.Contains("aoe") && deck.Aoes == 0);
        Add(6, "needs-draw", facts.Roles.Contains("draw") && deck.Draws == 0);
        Add(6, "needs-energy", facts.Roles.Contains("energy") && deck.Energies == 0);
        return bonus;
    }

    // Roles the deck would break without: the only copy of a defensive card, of
    // area damage, of an energy source or of a draw engine. Never removed by a
    // static low score.
    /// <summary>
    /// Structured form of the removal refusal: the role this card is the last
    /// copy of, or an unprotected guard when it can be removed. Every removal
    /// path (shop purchase, event, rest-site Cook, Neow) narrows candidates
    /// through <see cref="RemovalPlan.PriorityPool"/>, which uses this guard;
    /// the shop's final valuation keeps its protected-zero semantics in
    /// <see cref="CoopBots.BotShopPlanner.RemovalValue"/>.
    /// </summary>
    internal static RemovalGuard RemovalProtection(CardModel card, IReadOnlyList<CardModel> deck)
    {
        var roles = RolesOf(card);
        foreach (var role in new[] { "block", "aoe", "energy", "draw" })
        {
            if (!roles.Contains(role)) continue;
            if (deck.Count(existing => RolesOf(existing).Contains(role)) <= 1)
                return new RemovalGuard(true, role);
        }
        return new RemovalGuard(false, null);
    }
}

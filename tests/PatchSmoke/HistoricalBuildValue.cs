// Frozen pre-unification baseline for offline community comparisons only. Never ships in the MOD.
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

/// <summary>
/// One valuation for permanent deck changes — reward picks, removals, upgrades
/// and shop investments — so the same card is judged the same way everywhere.
///
/// Every decision is a difference against the deck as it stands, which makes
/// skipping a real candidate instead of an afterthought: a card is only worth
/// taking when its own contribution exceeds the draws it takes away from the
/// rest of the deck. Nothing here reads an authored tier list; the inputs come
/// from the card's own model, so modded cards are judged on what they do.
/// </summary>
internal static class HistoricalBuildValue
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
    // Thinning a starter card out of a real deck is worth this much before any
    // comparison against the rest of the deck. Lowered from 40 to 30 on 2026-09-23:
    // a first starter removal was worth 100 deck points (300 gold), which outbid every
    // build-defining card even after the route-core premium; the same 2.5x dead-draw
    // pressure still makes a 150-gold removal payable (75 x 3.0 = 225 gold).
    private const double StarterRemovalBaseline = 30;
    // Thinning is worth more the more dead draws the deck still carries: the
    // first removal takes one Strike out of fifteen, the eighth takes one out of
    // eight that are left. A flat baseline could never outbid the shop's growing
    // price — 40 x 3.0 = 120 gold, and the 2.5x dead-draw pressure carries it past
    // a 150-gold removal — so the decks that most needed thinning can still buy it.
    // The shop also discounts repeated removals once the starter pile has shrunk;
    // see BotShopPlanner.RemovalValue.
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
    };

    private const double AcquisitionBlacklistTotal = -100;

    private static double StarterPressure(IReadOnlyList<CardModel> deck)
    {
        var starters = deck.Count(IsPlainStarter);
        return Math.Min(2.5, 1 + 0.35 * Math.Max(0, starters - 1));
    }
    /// <summary>
    /// A Basic card with nothing else on it: the dead draw the starter removal
    /// floor and the dead-draw pressure are priced against. An ENCHANTED Basic is
    /// not one — the enchantment is a printed upgrade, and Replay in particular is
    /// only visible through GetEnchantedReplayCount. Shared by <see cref="Remove"/>
    /// and <see cref="StarterPressure"/> so the floor and the pressure cannot
    /// disagree about which cards are junk (R3: one shape, one rule).
    /// </summary>
    private static bool IsPlainStarter(CardModel card)
        => card.Rarity == CardRarity.Basic && !card.IsUpgraded
           && (card.Tags.Contains(CardTag.Strike) || card.Tags.Contains(CardTag.Defend))
           && card.Enchantment is null && card.GetEnchantedReplayCount() <= 0;
    // Draws per turn a hand represents, used to price the dilution a new card
    // imposes on the cards already in the deck.
    private const double HandSize = 5;

    internal readonly record struct Valuation(double Total, string Reason)
    {
        internal double RepeatablePerFight { get; init; }
    }

    /// <summary>
    /// Whether a removal is refused because the card is the deck's last copy of
    /// a role it depends on. <see cref="Role"/> names that role, so a caller can
    /// rank around the refusal without parsing the "protected:" reason string.
    /// </summary>
    internal readonly record struct RemovalGuard(bool Protected, string? Role);

    internal sealed record DeckCard(CardModel Card, int Upgrades) { }

    // A card whose cost the deck cannot pay, refusing it outright. Only Star has a
    // structured cost field (`star_cost`, and the X variant), so it is the only
    // resource this can be decided for today; the others would need the same kind
    // of baked consumer table their producer side already has.
    //
    // "No producer yet, but one may still turn up" is deliberately NOT softened
    // here. That is the continuous half of the design — how likely the enabler is
    // still findable, how much of the card depends on it, how thin the deck's
    // production is — and it belongs with parameters that can be validated
    // against the community anchor rather than guessed at.
    private static readonly BakedResources.Resource? StarResource =
        BakedResources.All.FirstOrDefault(resource => resource.Name == "star");

    private static Valuation? UnplayableForMissingResource(CardModel card, IReadOnlyList<CardModel> deck)
    {
        // An empty deck is a probe, not a draft: everything looks unpayable there.
        if (deck.Count == 0 || StarResource is null || !SpendsStars(card)) return null;
        return deck.Any(existing => StarResource.Producers.Contains(existing.Id.Entry, StringComparer.Ordinal))
            ? null
            : new Valuation(0, "unplayable:no-star");
    }

    private static bool SpendsStars(CardModel card)
    {
        try { return card.HasStarCostX || card.CanonicalStarCost > 0; }
        catch { return false; }
    }

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
        if (UnplayableForMissingResource(card, deck) is { } unpayable) return unpayable;
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
    {
        var summary = DeckStructure.Build(deck, StableEnergy(player));
        var total = 0.0;
        foreach (var card in deck) total += MarginalCore(card, player, deck, summary).Total;
        return total;
    }

    // The Elo-first policy: the community all-runs Elo baseline leads, and the
    // legacy heuristic survives only as a bounded, clearly logged correction.
    // These constants are tunable policy, not calibrated victory probabilities.
    private const double EloPerPoint = 10.0;
    private const double RulesPivot = 20.0;
    private const double RulesScale = 0.25;
    // A card the deck itself has a use for is not an average-play question, so a
    // structural fit is priced against a lower break-even: it reaches the same
    // ceiling from a lower legacy score, and the ceiling itself never moves.
    //
    // This used to be a steeper *slope* on the same pivot (0.5 against 0.25).
    // Read on a live A10 four-player log (2026-09-19), a fitted card's legacy
    // total clusters below RulesPivot, so a steeper slope did not lift it toward
    // the ceiling - it doubled its penalty. Concretely the Silent bot scored
    // BLUR at -7.8 and skipped it (rules -15.88 against -7.94 on the flat scale),
    // while the community's four-player winners run Blur. The pivot is the lever
    // this comment always described; the slope was the wrong knob.
    private const double FitPivot = 10.0;
    private const double RulesMin = -20.0;
    private const double RulesMax = 8.0;
    // THE POPULATION ELO MAY OPINE, NOT VETO FOREVER. The Elo term is unbounded below while
    // every term that can answer it is capped (rules <= RulesMax, development <= 2), so a card
    // the community rated below ~1460 could never be taken however well it fitted the deck.
    //
    // Measured on the 2026-09-25/26 live A5 rounds, that is precisely the damage those runs
    // were missing: FIGHT_ME was skipped at rules=+8.00 (its own ceiling) with eloBase=-14.20,
    // and SETUP_STRIKE five times at rules=+7.22 with eloBase=-12.56 — both `damage,
    // scaling-supported:strength` cards the model itself flagged as fitting the route — while
    // the party needed ~15 turns to kill a 972-hp four-player act-1 boss and survived six to
    // ten. What got taken instead was TAUNT / SHRUG_IT_OFF / COLOSSUS.
    //
    // The bound is RulesMax rather than a new number: Elo may not veto by more than the largest
    // structural credit the model can award, so a strong structural case always has a way to
    // win, and a mediocre one still does not (below the break-even it fails anyway).
    private const double EloVetoCap = -RulesMax;

    // Tokens the legacy heuristic only ever emits when the deck has a
    // demonstrable use for the card: a functional gap it fills, a mechanism whose
    // trigger is already present, a route the deck is already on, or a teammate's
    // plan. Everything else in the legacy total ("generically decent") stays on
    // the generic scale, which is why the whole apparatus used to be
    // indistinguishable from filler once it was clamped into one scalar.
    private static readonly string[] FitTokens =
    {
        "needs-block", "needs-damage", "needs-aoe", "needs-draw", "needs-energy",
        "fits-route", "scaling-supported:", "team-fit",
        "bottleneck:defence-quality", "bottleneck:needs-hand-advantage",
    };

    private static bool HasDeckFit(string reason) =>
        FitTokens.Any(token => reason.Contains(token, StringComparison.Ordinal));
    // A small early-deck bonus, NOT a target to fill. The previous +8 at deck size 12
    // carried cards with a negative community baseline over the skip line — measured live
    // 2026-09-22: HAND_TRICK (elo-base -8.00, rules +0.22) scored +0.2 and was taken, and
    // act 1 took 19/20 rewards before act 3 skipped 13/20. The bonus stays bounded and
    // small enough that a below-skip card cannot ride it alone; deciding to add a card must
    // still come from the card's own Elo/rules/affinity.
    private const double DevelopmentDeckTarget = 22.0;
    private const double DevelopmentPerCard = 0.25;
    private const double DevelopmentMax = 2.0;

    // A compiled table with a finite SKIP row is the only state in which the
    // Elo-first branch may run. The empty placeholder keeps the exact legacy
    // behavior instead of inventing an Elo.
    private static bool HasBakedBaseline =>
        BakedCardElo.CardCount > 0
        && BakedCardElo.SkipElo > double.NegativeInfinity
        && BakedCardElo.SkipElo < double.PositiveInfinity;

    /// <summary>
    /// Numeric terms behind one Elo-first addition, for logging and tests. The
    /// legacy value is the old heuristic total with its own affinity removed;
    /// its bounded contribution is <see cref="Rules"/>. A guard refusal or a
    /// missing-Elo fallback sets <see cref="UsedFallback"/> so the exact reason
    /// is not folded into the numeric line.
    /// </summary>
    internal readonly record struct AddBreakdown(
        double Elo,
        double SkipElo,
        double EloBase,
        double Rules,
        bool DeckFit,
        double Development,
        double Affinity,
        double Multiplayer,
        double LegacyTotal,
        double LegacyAffinity,
        string LegacyReason,
        double Total,
        bool UsedFallback,
        bool GuardRefusal,
        CounterfactualDraft.Result? HandExperiment = null,
        double ContextualPrior = 0,
        bool Contextual = false,
        // How much of this card's printed effect the building estimate can see,
        // and how much of the size tax this deck has earned. Both are logged so a
        // reviewer can tell "the model judged it weak" from "the model cannot read
        // it", which used to be the same -11.6 in the log (2026-09-29).
        double Coverage = 1.0,
        double Gate = 1.0);

    private readonly record struct LegacyAdd(double Total, double Affinity, string Reason);

    /// <summary>
    /// Adding a card under the community-Elo policy. The hard eligibility guards
    /// still run first and unchanged; a card the snapshot does not cover falls
    /// back to the legacy heuristic with the reason labeled, never to an invented
    /// zero. The result is a real quantity compared against skipping.
    ///
    /// Scope note: only <c>Add</c> (reward picks and the card shop) takes this
    /// path. Marginal, Deck, Remove and UpgradeDelta keep their algorithms.
    /// </summary>
    internal static Valuation Add(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var terms = AddDetailed(card, player, deckOverride);
        if (terms.GuardRefusal) return new Valuation(terms.Total, terms.LegacyReason);
        if (terms.Contextual)
        {
            var hand = terms.HandExperiment;
            return new Valuation(terms.Total, hand is null
                ? $"hand-model:unsupported,structural-fallback,{FormatTerms(terms)}"
                : $"hand-delta:{hand.Delta:F2},mean:{hand.MeanBefore:F1}->{hand.MeanAfter:F1},"
                    + $"tail:{hand.TailBefore:F1}->{hand.TailAfter:F1},dead-hand:{hand.DeadBefore:P0}->{hand.DeadAfter:P0},"
                    + $"prior:{terms.ContextualPrior:F2},legacy:{terms.LegacyReason}");
        }
        if (terms.UsedFallback)
            return new Valuation(terms.Total, "elo-missing:legacy-fallback," + terms.LegacyReason);
        return new Valuation(terms.Total, FormatTerms(terms));
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
        var working = player.Deck.Cards.ToList();
        var ranked = new List<(CardModel Card, double Value, string Reason)>();
        var picked = new List<CardModel>();
        var remaining = offer.Distinct()
            .OrderBy(card => card.Id.Entry, StringComparer.Ordinal).ToList();
        while (picked.Count < max && remaining.Count > 0)
        {
            var best = remaining
                .Select(card => (Card: card, Value: Add(card, player, working)))
                .OrderByDescending(entry => entry.Value.Total)
                .ThenBy(entry => entry.Card.Id.Entry, StringComparer.Ordinal)
                .First();
            ranked.Add((best.Card, best.Value.Total, best.Value.Reason));
            if (best.Value.Total <= 0 && picked.Count >= min) break;
            picked.Add(best.Card);
            remaining.Remove(best.Card);
            working.Add(best.Card);
        }
        return (picked, ranked);
    }

    private static string FormatTerms(AddBreakdown terms) =>
        $"coverage:{terms.Coverage:F2},maturity:{terms.Gate:F2},"
        + $"elo:{terms.Elo:F1},skip:{terms.SkipElo:F1},elo-base:{terms.EloBase:F2},"
        + $"rules:{terms.Rules:F2},fit:{terms.DeckFit},development:{terms.Development:F2},affinity:{terms.Affinity:F2},"
        + $"multiplayer:{terms.Multiplayer:F2},legacy:{terms.LegacyReason}";

    internal static AddBreakdown AddDetailed(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var deck = deckOverride ?? player.Deck.Cards.ToList();
        // Hard acquisition veto, ahead of every prior (see AcquisitionBlacklist).
        // This prices the card; it does not refuse an action at execution time.
        if (AcquisitionBlacklist.Contains(card.Id.Entry))
            return Guard("blacklist:the-gambit", AcquisitionBlacklistTotal);
        // Curse, status and unplayable-resource refuse before any Elo lookup: no
        // community prior can make a curse takeable or pay a resource the deck
        // cannot produce. These are the same guards the marginal valuation uses,
        // and they stay byte-for-byte in effect on this path.
        var facts = CardProfile.Of(card);
        if (facts.Unplayable || card.Type is CardType.Curse or CardType.Status)
            return Guard(card.Type == CardType.Curse ? "curse" : "status", -1000);
        if (UnplayableForMissingResource(card, deck) is { } unpayable)
            return Guard(unpayable.Reason, 0);
        // Card eligibility precedes the lookup: a card without a finite Elo is
        // not rejected and is not assigned a zero, it is evaluated the old way
        // and the reason says so.
        if (!HasBakedBaseline || !BakedCardElo.TryGet(card.Id.Entry, out var candidateElo)
            || !CommunityDraft.IsFinite(candidateElo))
            return ContextualAddition(Fallback(card, player, deck, "legacy", null), card, player, deck);
        var legacy = AddLegacy(card, player, deck);
        var oldWithoutAffinity = legacy.Total - legacy.Affinity;
        var coverage = BuildCoverage.Of(card);
        var gate = MaturityGate(player, deck);
        // Capped: for these cards the breakdown does NOT satisfy elo-base == (elo - skip) / 10.
        // The raw Elo is still printed as `elo:` in the log, and this field is the veto AFTER
        // the bound. Kept as its own term rather than folded elsewhere so the log stays checkable.
        var eloBase = Math.Max(EloVetoCap, (candidateElo - BakedCardElo.SkipElo) / EloPerPoint);
        var deckFit = HasDeckFit(legacy.Reason);
        // The off-route break-even is NOT part of the size tax and is deliberately
        // left alone (2026-09-29). Collapsing it on an unformed deck let a role gap
        // carry a below-skip community card over the line, which is exactly what the
        // Elo-first policy exists to prevent; the live fix does not need it either,
        // because an unreadable card now bypasses the structural residual entirely.
        var rules = Math.Clamp((oldWithoutAffinity - (deckFit ? FitPivot : RulesPivot)) * RulesScale,
            RulesMin, RulesMax);
        var development = Math.Clamp((DevelopmentDeckTarget - deck.Count) * DevelopmentPerCard, 0, DevelopmentMax);
        var affinity = CommunityDraft.Affinity(card, deck);
        // The four-player correction rides beside the single-player Elo: the baked table is
        // the same one, only its four-player bias is added. Zero on a one-seat run by
        // construction (MultiplayerPickLift.MinimumSeats), so no single-player number moves.
        var multiplayer = MultiplayerPickLift.Term(card.Id.Entry, player.RunState?.Players.Count ?? 1);
        // ABSENCE OF EVIDENCE IS NOT EVIDENCE OF ABSENCE. Only the term derived from
        // the card's own (mis)read text is silenced by coverage: the STRUCTURAL RULES
        // heuristic, which is what charged a blank-looking card a deck-size tax and a
        // role penalty. Community co-pick affinity and the four-player correction are
        // priors about cards in general, not claims about this card's text, so they
        // always apply. Measured live 2026-09-29: Burst (community elo +12.9, the best
        // card on the screen) was skipped at -11.6 because rules from a blind cycle
        // estimate outvoted the prior.
        var total = eloBase + coverage * rules + development + affinity + multiplayer;
        return ContextualAddition(new AddBreakdown(candidateElo, BakedCardElo.SkipElo, eloBase, rules, deckFit, development, affinity,
            multiplayer, legacy.Total, legacy.Affinity, legacy.Reason, total, UsedFallback: false, GuardRefusal: false,
            Coverage: coverage, Gate: gate),
            card, player, deck, coverage);
    }

    // Multiplayer construction is decided by the change to affordable hands.
    // Population Elo and route rules remain a small prior for effects the hand
    // model cannot describe; they no longer have an unbounded veto or bonus.
    private static AddBreakdown ContextualAddition(AddBreakdown terms, CardModel card,
        Player player, IReadOnlyList<CardModel> deck, double coverage = 1.0)
    {
        if ((player.RunState?.Players.Count ?? 1) < 2 || deck.Count < 8 || terms.GuardRefusal) return terms;
        var boss = BossPreparation.Change(player, deck, card, null, StableEnergy(player));
        terms = terms with { LegacyReason = terms.LegacyReason + (BossPreparation.AgainstWither(player)
            ? $",boss:AEONGLASS,cleanup:{boss:+0.00;-0.00;0.00}" : "") };
        // A card the estimate cannot read gets NO cycle verdict at all. Running the
        // experiment anyway is exactly what turned Burst into a blank play worth
        // -11.9 and let it veto a +12.9 community prior (2026-09-29).
        if (coverage <= 0)
            return terms with { Total = terms.Total + boss, Contextual = true, ContextualPrior = terms.Total,
                LegacyReason = terms.LegacyReason + ",estimate:no-coverage,"
                    + SustainedDeckValue.Coverage(deck, card) };
        var cycle = SustainedDeckValue.Compare(deck, card, null, StableEnergy(player), !BossPreparation.AgainstWither(player));
        var experiment = cycle ?? CounterfactualDraft.Compare(deck, card, StableEnergy(player));
        // Unsupported is absence of evidence, not a zero-valued experiment.
        // Preserve the complete structural/community policy instead of squeezing
        // every unmodeled engine into the same [-6,6] residual prior.
        if (experiment is null) return terms with { Total = terms.Total + boss, Contextual = true, ContextualPrior = terms.Total,
            LegacyReason = terms.LegacyReason + ",estimate:structural," + SustainedDeckValue.Coverage(deck, card) };
        var prior = Math.Clamp(terms.Total / 10, -2, 2);
        // Never more than the card's own coverage: a partial read may not speak with
        // the confidence of a complete one (2026-09-29).
        var share = Math.Min(experiment.ModelShare, coverage);
        return terms with { Total = share * (CounterfactualDraft.DeckPoints(experiment, deck.Count + 1) + prior)
                + (1 - share) * terms.Total + boss,
            HandExperiment = experiment, ContextualPrior = prior, Contextual = true,
            LegacyReason = terms.LegacyReason + $",estimate:{experiment.Coverage},model-share:{share:F2}"
                // print the per-turn delta under its own name; DeckPoints multiplies by
                // the six turns. Labelled `six-turn:` it understated the penalty 6x.
                + (cycle is null ? "" : $",per-turn:{cycle.Delta:F2}") };
    }

    private static AddBreakdown Guard(string reason, double total) =>
        new(0, 0, 0, 0, false, 0, 0, 0, total, 0, reason, total, UsedFallback: true, GuardRefusal: true);

    // The fallback path keeps the legacy total exactly and labels the reason.
    private static AddBreakdown Fallback(CardModel card, Player player, IReadOnlyList<CardModel> deck,
        string reason, double? forcedTotal)
    {
        if (forcedTotal is { } total)
            return new AddBreakdown(0, 0, 0, 0, false, 0, 0, 0, total, 0, reason, total, UsedFallback: true, GuardRefusal: false);
        var legacy = AddLegacy(card, player, deck);
        return new AddBreakdown(0, 0, 0, 0, false, 0, 0, 0, legacy.Total, legacy.Affinity, legacy.Reason,
            legacy.Total, UsedFallback: true, GuardRefusal: false);
    }

    /// <summary>
    /// The pre-Elo heuristic, kept verbatim as a bounded auxiliary and the
    /// missing-Elo fallback. Its own affinity is returned separately so the
    /// Elo-first path can subtract it and never double count.
    /// </summary>
    private static LegacyAdd AddLegacy(CardModel card, Player player, IReadOnlyList<CardModel> deck)
    {
        var summary = DeckStructure.Build(deck, StableEnergy(player));
        var own = MarginalCore(card, player, deck, summary);
        var average = deck.Count == 0
            ? 0
            : deck.Average(existing => MarginalCore(existing, player, deck, summary).Total);
        var dilution = Math.Max(0, average - own.Total) * (HandSize / (deck.Count + 1.0));
        // Past its forming stage a deck is not improved by "another fine card": it
        // is improved by thinning and by the cards the plan actually asked for.
        // This pressure is what lets a reward be skipped — or a shop card be
        // declined — because it is not closer to the target build, rather than
        // because it is weak.
        //
        // The relief is continuous and bounded. Two independent signals feed it:
        // a real functional deficit (the deck cannot block, clear groups, draw or
        // pay), and a mechanism-supported fit whose trigger is already present.
        // Community affinity no longer touches the size penalty at all — it was
        // only ever a small additive prior, and letting a +2 lift switch off a
        // 30-point dilution cost was a discontinuity no data supports. A generic
        // mined route likewise grants no relief on its own.
        var facts = CardProfile.Of(card);
        var candidateInDeck = deck.Any(existing => ReferenceEquals(existing, card));
        var mechanism = DeckMechanisms.Assess(card, facts, summary, candidateInDeck);
        var relief = Math.Clamp(
            FunctionalDeficit(card, facts, summary) + (mechanism.Support == DeckMechanisms.Support.Supported ? MechanismRelief : 0),
            0, 1);
        var bloat = RunDepth.BloatFactor(player);
        var factor = bloat - relief * (bloat - OnPlanRelief);
        // The size tax is what an unformed deck has not earned yet: measured live
        // 2026-09-29, a 21-card Act-1 deck of nine basics plus a curse paid the full
        // 40.5 and skipped a boss rare the community rates ABOVE the cards it took.
        // The gate leaves the dilution term alone — a card below the deck's own
        // average is still charged for that at any size, which is the comparison
        // that actually means "this card lowers my average".
        var pressure = Oversize(deck.Count) * factor * MaturityGate(player, deck);
        var total = own.Total - dilution - pressure;
        var reason = own.Reason
            + (dilution > 0.5 ? $",dilutes:{dilution:F1}" : "")
            + (pressure > 0.5 ? $",size:{pressure:F1}" : "")
            + (relief > 0.01 ? $",relief:{relief:F2}" : "");
        // The legacy affinity is scored separately so the Elo-first branch can
        // remove it before adding the directional community affinity, which
        // would otherwise count the same relation twice.
        var legacyAffinity = Affinity(card, deck);
        if (legacyAffinity > 0.01)
        {
            total += legacyAffinity;
            reason += ",drafted-together";
        }
        return new LegacyAdd(total, legacyAffinity > 0.01 ? legacyAffinity : 0, reason);
    }

    // How much of a role the deck is genuinely short of, as a 0..1 share. This
    // is the functional half of the size relief, and it deliberately excludes
    // Powers: "fewer than three Powers" is not a deficit in every build.
    private static double FunctionalDeficit(CardModel card, CardProfile.Facts facts, DeckStructure.Summary deck)
    {
        var deficit = 0.0;
        if (facts.Roles.Contains("block"))
        {
            var desired = Math.Max(3, deck.Attacks / 2);
            if (deck.Blocks < desired)
                deficit = Math.Max(deficit, (desired - deck.Blocks) / (double)desired);
        }
        // OUR policy: the same quality gaps priced in MarginalCore must also reach
        // the addition/skip decision. Counts alone call four weak Defends sufficient
        // and let Prepared satisfy the draw gap. Keep relief bounded and candidate-specific.
        deficit = Math.Max(deficit, DeckStructure.DefenceQualityGain(facts, deck) / 4.0);
        if (CardProfile.NetDraw(card, facts) > 0 && deck.NetDraw < 2)
            deficit = Math.Max(deficit, (2 - deck.NetDraw) / 2.0);
        if (facts.Roles.Contains("damage") && deck.Attacks < 5)
            deficit = Math.Max(deficit, (5 - deck.Attacks) / 5.0);
        if (facts.Roles.Contains("aoe") && deck.Aoes == 0) deficit = Math.Max(deficit, 1.0);
        if (facts.Roles.Contains("draw") && deck.Draws == 0) deficit = Math.Max(deficit, 1.0);
        if (facts.Roles.Contains("energy") && deck.Energies == 0) deficit = Math.Max(deficit, 1.0);
        return Math.Clamp(deficit, 0, 1);
    }

    // DECK MATURITY (2026-09-29). "One more card dilutes me" is only true of a
    // deck that has actually taken shape. The old trigger was a bare count
    // (size > 15), which charged a 21-card Act-1 deck of nine basics plus a curse
    // exactly what it charged a finished engine — measured live, a deck the model
    // itself labelled `route=none` paid the maximum 40.5 and skipped a boss rare.
    // Maturity is the share of the deck that is no longer a plain starter; the size
    // tax scales with it. Depth is deliberately NOT folded in — it already enters
    // through RunDepth.BloatFactor, and including it here made a card the deck needs
    // dearer just for arriving late (guarded by the filler-vs-needed fixture).
    // Calibration
    // note kept from the count-based version: at 19 cards a ~18-20 point candidate
    // was worth skipping, a 46-point card and a 28-point route card were not —
    // those two stay upgrades under this rule because they carry their own value.
    private const int SizeFloor = 15;
    private const double SizePressure = 5.0;
    // The pressure saturates. Unbounded, it reached 40-45 points in the 23-24
    // card decks the reviewed run actually had, which is more than any card's own
    // contribution: the log shows Blood Wall declined at -37.8 from a deck whose
    // own reason line said `needs-block`. Past a point, "this deck is big" is the
    // dilution term's job, and dilution is already charged separately.
    private const double SizePressureCap = 30;
    // A card the plan actually asked for justifies most of the extra size.
    private const double OnPlanRelief = 0.4;
    // Below this maturity the size tax is not charged at all; above it the tax
    // ramps in over the remaining range. This is the rewritten form of the old
    // "a filler in a 20-card deck must score below skipping" rule: a filler must
    // be skipped once the deck is FORMED, not once it merely has cards in it.
    private const double MaturityStart = 0.6;
    // A deck this large pays the tax whatever its maturity: at some point size is
    // diluting on its own, and no composition argument excuses it. This is the
    // flood guard — it is what stops "unformed decks do not pay" from turning
    // every reward into a take.
    private const int HardSize = 32;
    private const int HardSizeRamp = 8;
    private static double BasicShare(IReadOnlyList<CardModel> deck)
        => deck.Count == 0 ? 0 : deck.Count(IsPlainStarter) / (double)deck.Count;

    /// <summary>
    /// 0 = still a starter pile, 1 = a deck with no plain starter left. A policy
    /// estimate, not a win rate; it decides how much of a size tax a deck has
    /// earned, nothing else. Deliberately NOT act-based: depth already enters
    /// through <see cref="RunDepth.BloatFactor"/>, and folding it in twice made a
    /// card the deck needs dearer just for arriving late (guarded by the
    /// filler-vs-needed test).
    /// </summary>
    internal static double Maturity(Player player, IReadOnlyList<CardModel> deck)
        => Math.Clamp(1 - BasicShare(deck), 0, 1);

    /// <summary>How much of the size tax this deck has earned: 0 unformed, 1 formed.</summary>
    internal static double MaturityGate(Player player, IReadOnlyList<CardModel> deck)
    {
        var formed = Math.Clamp((Maturity(player, deck) - MaturityStart) / (1 - MaturityStart), 0, 1);
        var oversized = Math.Clamp((deck.Count - HardSize) / (double)HardSizeRamp, 0, 1);
        return Math.Max(formed, oversized);
    }

    private static double Oversize(int size)
        => Math.Min(SizePressureCap, Math.Max(0, size - SizeFloor) * SizePressure);

    /// <summary>
    /// Removing a card is worth the value it was contributing, plus the draws it
    /// gives back to the rest of the deck.
    /// </summary>
    internal static Valuation Remove(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var deck = deckOverride ?? player.Deck.Cards.ToList();
        var stableEnergy = StableEnergy(player);
        var value = ComputeRemovalValuation(card, player, deck, stableEnergy);
        // 用户 2026-09-30：**没建模的牌，估值不得低于基础牌（打击 / 防御）**。
        // 上游怎么做：上游没有这套删牌定价；我们自己的 Add 里早就有"读不懂的牌只由社区先验说话"
        //            的 coverage 规则（BuildCoverage 的注释："absence of evidence is not evidence
        //            of absence"），但 Remove 这一路拿着实验的 model-share:1.00 直接采信。
        // 我们怎么做：coverage==None 的牌，其删除价值封顶到**本副牌组里普通打击/防御的删除价值
        //            （取更小的那个）**——即"我们顶多跟想删一张基础牌一样想删它"。
        // 为什么：2026-09-29 实机，BULLET_TIME 靠社区 Elo 被买入、营地锻造、事件附魔，下一个商店
        //         却被删：抽手实验对读不懂的牌只会把它当占位符（coverage:0.00 ⇒ 永远用不上），
        //         于是它的删除价值 +11.15 反而高过普通打击 +2.59。没有建模 ≠ 有害。
        var floor = UnmodeledRemovalFloor(card, player, deck, stableEnergy);
        if (floor is { } capped && value.Total > capped)
            return new Valuation(capped, value.Reason + $",unmodeled-floor:{capped:F2}");
        return value;
    }

    /// <summary>
    /// 没建模的牌的删除价值上限：本副牌组里普通打击/防御的删除价值里**更小**的那个。
    /// 没有基础牌可比时返回 null（不封顶，保持旧行为）——没有参照物就谈不上"不低于基础牌"。
    /// </summary>
    private static double? UnmodeledRemovalFloor(CardModel card, Player player,
        IReadOnlyList<CardModel> deck, int stableEnergy)
    {
        if (BuildCoverage.Of(card) > BuildCoverage.None) return null;
        var basics = deck.Where(existing => !ReferenceEquals(existing, card) && IsPlainStarter(existing)).ToList();
        if (basics.Count == 0) return null;
        return basics.Min(basic => ComputeRemovalValuation(basic, player, deck, stableEnergy).Total);
    }

    private static Valuation ComputeRemovalValuation(CardModel card, Player player,
        IReadOnlyList<CardModel> deck, int stableEnergy)
    {
        var own = MarginalCore(card, player, deck, DeckStructure.Build(deck, stableEnergy)).Total;
        var others = deck.Where(existing => !ReferenceEquals(existing, card)).ToList();
        var otherSummary = DeckStructure.Build(others, stableEnergy);
        var average = others.Count == 0
            ? 0
            : others.Average(existing => MarginalCore(existing, player, others, otherSummary).Total);
        var relief = Math.Max(0, average - own) * (HandSize / Math.Max(1, deck.Count));
        // A starter card sits below the curve by construction — the deck a run is
        // measured against does not contain one — so thinning it is worth a
        // baseline even when the rest of the deck is no better. Without this a
        // uniformly poor deck looks like it has nothing worth removing.
        //
        // AN ENCHANTED BASIC IS NOT STARTER JUNK. The baseline is a FLOOR applied with
        // `max`, so it used to erase the enchantment entirely: a Glam/Spiral Strike and a
        // plain Strike both scored `StarterRemovalBaseline * StarterPressure` and the
        // tie-break picked whichever came first in the deck — community report 2026-09-23,
        // the bot deleted the Replay Strike while plain Strikes still sat in the deck.
        // Any enchantment disqualifies the card (Sharp/Corrupted raise the numbers too);
        // intrinsic replay (BaseReplayCount) gets the same treatment even without one.
        var starter = IsPlainStarter(card) && deck.Count >= 12
            ? StarterRemovalBaseline * StarterPressure(deck)
            : double.NegativeInfinity;
        // The last copy of a role the deck depends on is not a preference that a
        // score can outweigh: removing it would leave the deck unable to block,
        // clear groups, draw or pay. That is a refusal, not a penalty, because a
        // deduction large enough to win the comparison can always be found.
        if (RemovalProtection(card, deck) is { Protected: true } guard) return new Valuation(0, "protected:" + guard.Role);
        var boss = BossPreparation.Change(player, deck, null, card, stableEnergy);
        if ((player.RunState?.Players.Count ?? 1) >= 2 && card.Type is not (CardType.Curse or CardType.Status)
            && (SustainedDeckValue.Compare(deck, null, card, stableEnergy, !BossPreparation.AgainstWither(player))
                ?? CounterfactualDraft.Remove(deck, card, stableEnergy)) is { } experiment)
        {
            // 用户 2026-09-30（统一第 1 步）：与 Add 同一口径 —— 实验份额先被这张牌的文本
            // 覆盖率封顶。coverage==None 的牌（效果完全读不懂）拿不到实验的判决权，
            // 直接回落 `max(-own+relief, starter)`，再由 Remove 外层的 unmodeled-floor 兜底。
            var removalShare = Math.Min(experiment.ModelShare, BuildCoverage.Of(card));
            return new Valuation(removalShare * CounterfactualDraft.DeckPoints(experiment, deck.Count)
                + (1-removalShare) * Math.Max(-own + relief, starter) + boss,
                $"hand-removal:{experiment.Delta:F2},mean:{experiment.MeanBefore:F1}->{experiment.MeanAfter:F1},"
                + $"tail:{experiment.TailBefore:F1}->{experiment.TailAfter:F1},estimate:{experiment.Coverage},"
                + $"model-share:{removalShare:F2},boss-cleanup:{boss:F2}")
                { RepeatablePerFight = experiment.Coverage.StartsWith("cycle:", StringComparison.Ordinal)
                    ? removalShare * CounterfactualDraft.DeckPoints(experiment, deck.Count) : 0 };
        }
        // Removing the card takes away what it was contributing, so the sign
        // flips: a card dragging the deck down (negative marginal) is worth the
        // most to remove, and a strong card costs the deck by leaving.
        var total = Math.Max(-own + relief, starter);
        var reason = own < 0 ? "diluting card" : "low contribution";
        if (starter > double.NegativeInfinity && starter >= -own + relief) reason += ",starter";
        if (relief > 0.5) reason += $",frees:{relief:F1}";
        return new Valuation(total + boss, reason + $",boss-cleanup:{boss:F2}");
    }

    /// <summary>
    /// Upgrading a card: the real difference the upgrade makes, priced by what
    /// it changes. A cost drop can turn a dead turn live, but how much it is
    /// worth depends on whether the deck is actually short of energy: the same
    /// reduction is worth more in a heavy, unaccelerated curve than in one that
    /// already has energy to spare. The floor stays positive so a genuine
    /// improvement is never downgraded just because the deck is well rounded.
    /// </summary>
    internal static Valuation UpgradeDelta(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        if (CardProfile.DiffUpgrade(card) is not { } diff) return new Valuation(0, "not upgradable");
        var deck = deckOverride ?? player.Deck.Cards.ToList();
        var pressure = DeckStructure.EnergyPressure(DeckStructure.Build(deck, StableEnergy(player)));
        var debuff = diff.VulnerableDelta + diff.WeakDelta + diff.StrengthDownDelta;
        var nextEnergy = diff.After.NextTurnEnergy - diff.Before.NextTurnEnergy;
        var nextDraw = diff.After.NextTurnDraw - diff.Before.NextTurnDraw;
        var lifecycleDelta = DraftCardEffects.Value(diff.After.Effects, deck, StableEnergy(player))
            - DraftCardEffects.Value(diff.Before.Effects, deck, StableEnergy(player));
        var value = diff.CostDelta * CostUpgradeValuePerPoint(pressure)
            + Math.Min(DamageCap, Math.Max(0, diff.DamageDelta) * DamageWeight
                * (card is MegaCrit.Sts2.Core.Models.Cards.GrandFinale ? GrandFinaleAvailability.Estimate(deck, StableEnergy(player)) : 1))
            + Math.Max(0, diff.BlockDelta) * BlockWeight
            + Math.Max(0, diff.DrawDelta) * DrawWeight
            + Math.Max(0, diff.EnergyDelta) * EnergyWeight
            + .7 * (nextEnergy * EnergyWeight + nextDraw * DrawWeight)
            + Math.Max(0, debuff) * DebuffWeight
            + Math.Max(0, diff.ScalingDelta) * ScalingWeight
            + Math.Max(0, diff.PoisonDelta + diff.DoomDelta) * DoomWeight
            // A lower HP cost is a real gain; a higher one is a real cost.
            + HpLossUpgradeValue(diff.HpLossDelta) + lifecycleDelta;
        // Roles and keywords are separate axes: a role the upgrade unlocks is a
        // new capability, while a keyword change is a rules transition.
        foreach (var role in diff.AddedRoles)
            if (role != "exhaust") value += 6;
        foreach (var role in diff.LostRoles)
            if (role != "exhaust") value -= 6;
        value += ExhaustTransitionValue(diff);
        var innateValue = diff.AddedKeywords.Contains(CardKeyword.Innate)
            ? OpeningUpgradeValue.Innate(card, player, deck, StableEnergy(player)) : null;
        foreach (var keyword in diff.AddedKeywords)
            value += keyword == CardKeyword.Innate && innateValue is { } timing
                ? timing : KeywordTransitionValue(keyword, added: true);
        foreach (var keyword in diff.RemovedKeywords) value += KeywordTransitionValue(keyword, added: false);

        var reasons = new List<string>();
        // The same paired mechanism model used by rewards and shops evaluates
        // the actual native upgraded clone. Replace the covered contribution;
        // adding it to all printed deltas would count the upgrade twice.
        if ((player.RunState?.Players.Count ?? 1) >= 2
            && CardProfile.Upgraded(card, StableEnergy(player)) is { } cycleUpgrade
            && SustainedDeckValue.Compare(deck, cycleUpgrade.Card, card, StableEnergy(player),
                !BossPreparation.AgainstWither(player)) is { } upgradeCycle)
        {
            // 用户 2026-09-30（统一第 1+2 步）：和 Add/Remove 用同一条接线。
            // ① share = min(实验份额, 这张牌的文本覆盖率) —— Add 早就这么做了，升级这条路漏了，
            //    于是一张效果读不懂的牌（BuildCoverage==None）照样被实验按"占位符"判死。
            // ② 单位改走 CounterfactualDraft.DeckPoints：cycle 覆盖时它与 Δ*6 完全等价
            //    （cycle 分支就是 ×6），换过来只是让三处"一个点"由同一个函数定义。
            // Camp already multiplies by future fights. A six-turn per-fight
            // delta needs six turns here, not another deck-size multiplier.
            var upgradeShare = Math.Min(upgradeCycle.ModelShare, BuildCoverage.Of(card));
            value = upgradeShare * CounterfactualDraft.DeckPoints(upgradeCycle, deck.Count)
                + (1-upgradeShare) * value;
            reasons.Add($"upgrade-cycle:{upgradeCycle.Delta:F2},estimate:{upgradeCycle.Coverage}"
                + $",model-share:{upgradeShare:F2}");
        }
        if (BossPreparation.Cleans(card) && CardProfile.Upgraded(card, StableEnergy(player)) is { } upgraded)
        {
            var cleanup = BossPreparation.Change(player, deck, upgraded.Card, card, StableEnergy(player));
            value += cleanup;
            if (Math.Abs(cleanup) > .01) reasons.Add($"boss-cleanup:{cleanup:+0.00;-0.00}");
        }
        if (innateValue is { } openingValue) reasons.Add($"opening-engine+{openingValue:F1}");
        if (diff.CostDelta != 0) reasons.Add(diff.CostDelta > 0 ? $"cost-{diff.CostDelta}" : $"cost+{-diff.CostDelta}");
        if (lifecycleDelta != 0) reasons.Add($"lifecycle{lifecycleDelta:+0.0;-0.0}");
        if (diff.DamageDelta > 0) reasons.Add($"damage+{diff.DamageDelta:F0}");
        if (diff.BlockDelta > 0) reasons.Add($"block+{diff.BlockDelta:F0}");
        if (diff.DrawDelta > 0) reasons.Add($"draw+{diff.DrawDelta:F0}");
        if (nextEnergy != 0) reasons.Add($"next-energy{nextEnergy:+0;-0}");
        if (nextDraw != 0) reasons.Add($"next-draw{nextDraw:+0;-0}");
        if (diff.ScalingDelta > 0) reasons.Add($"scaling+{diff.ScalingDelta:F0}");
        if (diff.HpLossDelta < 0) reasons.Add($"hp-cost{-diff.HpLossDelta:F0}");
        if (diff.HpLossDelta > 0) reasons.Add($"hp-cost+{diff.HpLossDelta:F0}");
        if (debuff > 0) reasons.Add($"debuff+{debuff:F0}");
        var unlocked = diff.AddedRoles.Where(role => role != "exhaust").ToList();
        if (unlocked.Count > 0) reasons.Add("unlocks:" + string.Join('/', unlocked));
        if (diff.LostRoles.Contains("exhaust") || diff.RemovedKeywords.Contains(CardKeyword.Exhaust)) reasons.Add("exhaust-");
        if (diff.AddedRoles.Contains("exhaust") || diff.AddedKeywords.Contains(CardKeyword.Exhaust)) reasons.Add("exhaust+");
        return new Valuation(value, reasons.Count == 0 ? "marginal" : string.Join(',', reasons));
    }

    // Cost is the strongest lever an upgrade has, but it is contextual. A tight,
    // slow curve turns a one-cost reduction into a whole extra play; an
    // energy-rich deck cashes the same reduction for less. Bounded to 8..16 so
    // it stays in the same band the flat 14 always was.
    private static double CostUpgradeValuePerPoint(double pressure) => 8.0 + Math.Clamp(pressure, 0, 1) * 8.0;

    /// <summary>
    /// Signed value of an HP-cost change. Negative delta (the upgrade lowers the
    /// life paid) is worth the same 2.5 per point the pick valuation charges,
    /// with the sign flipped because the upgrade removes the cost.
    /// </summary>
    internal static double HpLossUpgradeValue(double hpLossDelta) => -hpLossDelta * 2.5;

    /// <summary>
    /// Whether the card is worth replaying at all. Only a card with an actual
    /// effect pays for losing Exhaust; a junk card that loses it is unchanged.
    /// </summary>
    private static bool HasReplayValue(CardProfile.Facts facts)
        => facts.Damage > 0 || facts.Block > 0 || facts.Draw > 0 || facts.Energy > 0
           || facts.NextTurnEnergy > 0 || facts.NextTurnDraw > 0
           || facts.Vulnerable > 0 || facts.Weak > 0 || facts.StrengthDown > 0
           || facts.Scaling > 0 || facts.Poison > 0 || facts.Doom > 0;

    // Exhaust is contextual: losing it makes a card with an effect reusable, but
    // it says nothing about a card that had no effect to repeat. Gaining it is
    // the mirror. The old blanket +7 reward for losing Exhaust on any card was
    // right for Hologram and wrong for junk.
    private static double ExhaustTransitionValue(CardProfile.UpgradeDiff diff)
    {
        var gained = diff.AddedRoles.Contains("exhaust") || diff.AddedKeywords.Contains(CardKeyword.Exhaust);
        var lost = diff.LostRoles.Contains("exhaust") || diff.RemovedKeywords.Contains(CardKeyword.Exhaust);
        if (!gained && !lost) return 0;
        if (!HasReplayValue(diff.Before)) return 0;
        return lost ? 7.0 : -7.0;
    }

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
    internal static int BestReward(Player player, IReadOnlyList<CardModel> candidates, bool allowSkip = true)
    {
        var best = -1;
        var bestValue = 0.0;
        var bestAny = candidates.Count > 0 ? 0 : -1;
        var bestAnyValue = double.NegativeInfinity;
        for (var index = 0; index < candidates.Count; index++)
        {
            double value;
            try { value = Add(candidates[index], player).Total; }
            catch { continue; }
            if (value > bestValue) { bestValue = value; best = index; }
            if (value > bestAnyValue) { bestAnyValue = value; bestAny = index; }
        }
        return allowSkip ? best : bestAny;
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
    /// copy of, or an unprotected guard when it can be removed. This is the
    /// query callers outside the shop must use; the shop keeps calling
    /// <see cref="Remove"/>, whose protected zero semantics are unchanged.
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

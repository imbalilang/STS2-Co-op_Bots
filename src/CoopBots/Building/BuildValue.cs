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
    // The most of a card's own pre-team contribution the team bonus may add.
    // Modest and absolute-free so it self-normalises across cards; it is what
    // stops a large flat team term from carrying a card the deck cannot use.
    private const double TeamBonusBuildShare = 0.5;
    // Thinning a starter card out of a real deck is worth this much before any
    // comparison against the rest of the deck.
    private const double StarterRemovalBaseline = 55;
    // Thinning is worth more the more dead draws the deck still carries: the
    // first removal takes one Strike out of fifteen, the eighth takes one out of
    // eight that are left. A flat baseline could never outbid the shop's growing
    // price — 55 x 2.2 = 121 gold against a 150-gold second removal — so the
    // decks that most needed thinning were exactly the ones that could not buy
    // it, and every run ended with ten starters still in the deck.
    private static double StarterPressure(IReadOnlyList<CardModel> deck)
    {
        var starters = deck.Count(card => card.Rarity == CardRarity.Basic && !card.IsUpgraded);
        return Math.Min(2.5, 1 + 0.35 * Math.Max(0, starters - 1));
    }
    // Draws per turn a hand represents, used to price the dilution a new card
    // imposes on the cards already in the deck.
    private const double HandSize = 5;

    internal readonly record struct Valuation(double Total, string Reason);

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

        var offense = Math.Min(DamageCap, facts.Damage * DamageWeight + facts.Block * BlockWeight);
        if (offense > 0)
        {
            var weighted = offense * RoleWeight(saturation, facts, "damage", "block");
            score += weighted;
            reasons.Add(facts.IsAoe ? "aoe" : facts.Damage > 0 ? "damage" : "block");
        }

        var engine = facts.Draw * DrawWeight + facts.Energy * EnergyWeight;
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

    /// <summary>
    /// Adding a card: its own contribution minus the draws it takes from the
    /// cards already in the deck. A below-average card in a small deck is worth
    /// less than nothing, which is exactly when skipping is correct.
    /// </summary>
    internal static Valuation Add(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var deck = deckOverride ?? player.Deck.Cards.ToList();
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
            FunctionalDeficit(facts, summary) + (mechanism.Support == DeckMechanisms.Support.Supported ? MechanismRelief : 0),
            0, 1);
        var bloat = RunDepth.BloatFactor(player);
        var factor = bloat - relief * (bloat - OnPlanRelief);
        var pressure = Oversize(deck.Count) * factor;
        var total = own.Total - dilution - pressure;
        var reason = own.Reason
            + (dilution > 0.5 ? $",dilutes:{dilution:F1}" : "")
            + (pressure > 0.5 ? $",size:{pressure:F1}" : "")
            + (relief > 0.01 ? $",relief:{relief:F2}" : "");
        return new Valuation(total, reason);
    }

    // How much of a role the deck is genuinely short of, as a 0..1 share. This
    // is the functional half of the size relief, and it deliberately excludes
    // Powers: "fewer than three Powers" is not a deficit in every build.
    private static double FunctionalDeficit(CardProfile.Facts facts, DeckStructure.Summary deck)
    {
        var deficit = 0.0;
        if (facts.Roles.Contains("block"))
        {
            var desired = Math.Max(3, deck.Attacks / 2);
            if (deck.Blocks < desired)
                deficit = Math.Max(deficit, (desired - deck.Blocks) / (double)desired);
        }
        if (facts.Roles.Contains("damage") && deck.Attacks < 5)
            deficit = Math.Max(deficit, (5 - deck.Attacks) / 5.0);
        if (facts.Roles.Contains("aoe") && deck.Aoes == 0) deficit = Math.Max(deficit, 1.0);
        if (facts.Roles.Contains("draw") && deck.Draws == 0) deficit = Math.Max(deficit, 1.0);
        if (facts.Roles.Contains("energy") && deck.Energies == 0) deficit = Math.Max(deficit, 1.0);
        return Math.Clamp(deficit, 0, 1);
    }

    // Deck size at which "one more card" stops being free. Calibrated against
    // real reward screens: at 19 cards the best candidate was worth ~18-20 and
    // should have been skipped, while a 20-card screen offering a 46-point card
    // and a 21-card screen offering a 28-point route card are real upgrades that
    // must still be taken.
    private const int FormingSize = 15;
    private const double SizePressure = 5.0;
    // The pressure saturates. Unbounded, it reached 40-45 points in the 23-24
    // card decks the reviewed run actually had, which is more than any card's own
    // contribution: the log shows Blood Wall declined at -37.8 from a deck whose
    // own reason line said `needs-block`. Past a point, "this deck is big" is the
    // dilution term's job, and dilution is already charged separately.
    private const double SizePressureCap = 30;
    // A card the plan actually asked for justifies most of the extra size.
    private const double OnPlanRelief = 0.4;
    private static double Oversize(int size)
        => Math.Min(SizePressureCap, Math.Max(0, size - FormingSize) * SizePressure);

    /// <summary>
    /// Removing a card is worth the value it was contributing, plus the draws it
    /// gives back to the rest of the deck.
    /// </summary>
    internal static Valuation Remove(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var deck = deckOverride ?? player.Deck.Cards.ToList();
        var stableEnergy = StableEnergy(player);
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
        var starter = card.Rarity == CardRarity.Basic && !card.IsUpgraded && deck.Count >= 12
            ? StarterRemovalBaseline * StarterPressure(deck)
            : double.NegativeInfinity;
        // The last copy of a role the deck depends on is not a preference that a
        // score can outweigh: removing it would leave the deck unable to block,
        // clear groups, draw or pay. That is a refusal, not a penalty, because a
        // deduction large enough to win the comparison can always be found.
        if (RemovalProtection(card, deck) is { Protected: true } guard) return new Valuation(0, "protected:" + guard.Role);
        // Removing the card takes away what it was contributing, so the sign
        // flips: a card dragging the deck down (negative marginal) is worth the
        // most to remove, and a strong card costs the deck by leaving.
        var total = Math.Max(-own + relief, starter);
        var reason = own < 0 ? "diluting card" : "low contribution";
        if (starter > double.NegativeInfinity && starter >= -own + relief) reason += ",starter";
        if (relief > 0.5) reason += $",frees:{relief:F1}";
        return new Valuation(total, reason);
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
        var value = diff.CostDelta * CostUpgradeValuePerPoint(pressure)
            + Math.Min(DamageCap, Math.Max(0, diff.DamageDelta) * DamageWeight)
            + Math.Max(0, diff.BlockDelta) * BlockWeight
            + Math.Max(0, diff.DrawDelta) * DrawWeight
            + Math.Max(0, diff.EnergyDelta) * EnergyWeight
            + Math.Max(0, debuff) * DebuffWeight
            + Math.Max(0, diff.ScalingDelta) * ScalingWeight
            + Math.Max(0, diff.PoisonDelta + diff.DoomDelta) * DoomWeight
            // A lower HP cost is a real gain; a higher one is a real cost.
            + HpLossUpgradeValue(diff.HpLossDelta);
        // Roles and keywords are separate axes: a role the upgrade unlocks is a
        // new capability, while a keyword change is a rules transition.
        foreach (var role in diff.AddedRoles)
            if (role != "exhaust") value += 6;
        foreach (var role in diff.LostRoles)
            if (role != "exhaust") value -= 6;
        value += ExhaustTransitionValue(diff);
        foreach (var keyword in diff.AddedKeywords) value += KeywordTransitionValue(keyword, added: true);
        foreach (var keyword in diff.RemovedKeywords) value += KeywordTransitionValue(keyword, added: false);

        var reasons = new List<string>();
        if (diff.CostDelta != 0) reasons.Add(diff.CostDelta > 0 ? $"cost-{diff.CostDelta}" : $"cost+{-diff.CostDelta}");
        if (diff.DamageDelta > 0) reasons.Add($"damage+{diff.DamageDelta:F0}");
        if (diff.BlockDelta > 0) reasons.Add($"block+{diff.BlockDelta:F0}");
        if (diff.DrawDelta > 0) reasons.Add($"draw+{diff.DrawDelta:F0}");
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

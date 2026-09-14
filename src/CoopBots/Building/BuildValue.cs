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

    internal sealed record DeckCard(CardModel Card, int Upgrades) { }

    /// <summary>
    /// What this card is worth to this deck right now: its own output, weighted
    /// down for roles the deck already covers, with a role-aware nudge for what
    /// the deck is missing. Negative when the card would only dilute.
    /// </summary>
    internal static Valuation Marginal(CardModel card, Player player, IReadOnlyList<CardModel>? deckOverride = null)
    {
        var deck = deckOverride ?? player.Deck.Cards.ToList();
        var facts = CardProfile.Of(card);
        // A curse or status is never a card the deck wants: it occupies a draw
        // without contributing. This has to come first, because the cost and
        // rarity terms below would otherwise make one look roughly neutral.
        if (facts.Unplayable || card.Type is CardType.Curse or CardType.Status)
            return new Valuation(-1000, card.Type == CardType.Curse ? "curse" : "status");
        var saturation = RoleSaturation(deck, card);
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
            if (UnsupportedScaling(card, deck) && !DefenceAlreadySupported(facts, deck))
            {
                // The card's mined partners exist and none are in the deck: it is
                // a payoff whose enabler is missing. Rupture (Strength when you
                // lose HP) wants Hemokinesis/Inferno/Breakthrough; without one its
                // scaling is not a plan, it is a promise. Keep a share so a route
                // can still be started, but stop it outbidding real route cards.
                score += scaling * RoleWeight(saturation, facts, "scaling", "poison", "doom") * 0.4;
                reasons.Add("scaling-unsupported");
            }
            else
            {
                score += scaling * RoleWeight(saturation, facts, "scaling", "poison", "doom");
                reasons.Add("scaling");
            }
        }

        score += (3 - Math.Min(3, facts.Cost)) * 2;
        if (facts.HpLoss > 0) score -= facts.HpLoss * 2.5;
        if (card.IsUpgraded) score += 3;
        if (facts.Roles.Contains("exhaust") && facts.Damage <= 0 && facts.Block <= 0) score -= 6;

        score += NeedBonus(card, facts, deck, reasons);
        // A support card or a team-wide debuff setup is worth more here than it is
        // alone. The same helper backs the team coordinator, so a reward and a
        // shop purchase cannot disagree about the card.
        var team = CoopBots.TeamCoordinator.TeamBonus(card, player);
        if (team > 0)
        {
            score += team;
            reasons.Add("team-fit");
        }
        // Cards drafted alongside this one far more often than chance. A nudge
        // only: it is a prior from average play, the surviving pairs are the
        // ones the sample floors support, and it must never outrank the card's
        // own contribution.
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
        score -= Duplicates(card, deck) * DuplicatePenalty;
        return new Valuation(score, reasons.Count == 0 ? "no role" : string.Join(',', reasons));
    }

    /// <summary>Value of the whole deck as it stands.</summary>
    internal static double Deck(IReadOnlyList<CardModel> deck, Player player)
    {
        var total = 0.0;
        foreach (var card in deck) total += Marginal(card, player, deck).Total;
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
        var own = Marginal(card, player, deck);
        var average = deck.Count == 0 ? 0 : deck.Average(existing => Marginal(existing, player, deck).Total);
        var dilution = Math.Max(0, average - own.Total) * (HandSize / (deck.Count + 1.0));
        // Past its forming stage a deck is not improved by "another fine card": it
        // is improved by thinning and by the cards the plan actually asked for.
        // This pressure is what lets a reward be skipped — or a shop card be
        // declined — because it is not closer to the target build, rather than
        // because it is weak. A card that advances the recognised route pays much
        // less of it, which is how a plan-advancing card still gets in.
        //
        // "On plan" has two sources and needs both. The recognised route is one;
        // the mined card-to-card affinity is the other, and leaving it out skipped
        // cards the mining says belong with this deck — a Deadly Poison was
        // declined from a poison deck because the route happened to be detected
        // from a different cluster that does not list it as a signature.
        var facts = CardProfile.Of(card);
        var route = Archetypes.Bonus(facts, card, deck, player);
        // Filling a function the deck is short of also earns the relief. Without
        // it a bloated deck could be shown `needs-block` in the same breath as a
        // 40-point penalty for being big, which is how a deck that could not
        // block stayed that way.
        var onPlan = route > 0.01 || Affinity(card, deck) > 0.01 || GapBonus(facts, deck, null) > 0.01;
        // A card nothing asked for is worse later in the run: the shops that
        // could have removed it are behind the party, and once no shop is left
        // the dilution is permanent. Plan cards keep their relief, so a route
        // can still be finished in the last act.
        var pressure = Oversize(deck.Count) * (onPlan ? OnPlanRelief : RunDepth.BloatFactor(player));
        var total = own.Total - dilution - pressure;
        var reason = own.Reason
            + (dilution > 0.5 ? $",dilutes:{dilution:F1}" : "")
            + (pressure > 0.5 ? $",size:{pressure:F1}" : "");
        return new Valuation(total, reason);
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
    // A card the recognised route asked for justifies most of the extra size.
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
        var own = Marginal(card, player, deck).Total;
        var others = deck.Where(existing => !ReferenceEquals(existing, card)).ToList();
        var average = others.Count == 0 ? 0 : others.Average(existing => Marginal(existing, player, others).Total);
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
        if (Blocking(card, deck) is { } blocking) return new Valuation(0, "protected:" + blocking);
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
    /// it changes. A cost drop can turn a dead turn live, so it is worth more
    /// than a few points of damage.
    /// </summary>
    internal static Valuation UpgradeDelta(CardModel card, Player player)
    {
        if (CardProfile.DiffUpgrade(card) is not { } diff) return new Valuation(0, "not upgradable");
        var debuff = diff.VulnerableDelta + diff.WeakDelta + diff.StrengthDownDelta;
        var value = diff.CostDelta * 14.0
            + Math.Min(DamageCap, Math.Max(0, diff.DamageDelta) * DamageWeight)
            + Math.Max(0, diff.BlockDelta) * BlockWeight
            + Math.Max(0, diff.DrawDelta) * DrawWeight
            + Math.Max(0, diff.EnergyDelta) * EnergyWeight
            + Math.Max(0, debuff) * DebuffWeight
            + Math.Max(0, diff.ScalingDelta) * ScalingWeight
            + Math.Max(0, diff.PoisonDelta + diff.DoomDelta) * DoomWeight
            + diff.AddedRoles.Count * 6
            // Losing a role is usually a loss, but losing Exhaust makes the card
            // reusable, which the previous blanket penalty scored backwards.
            + diff.LostRoles.Sum(role => role == "exhaust" ? 7.0 : -6.0)
            // Exhaust is priced through the role above, so the keyword lists skip
            // it to avoid counting the same change twice.
            + diff.AddedKeywords.Count(keyword => keyword != CardKeyword.Exhaust) * 4
            - diff.RemovedKeywords.Count(keyword => keyword != CardKeyword.Exhaust) * 4;
        var reasons = new List<string>();
        if (diff.CostDelta > 0) reasons.Add($"cost-{diff.CostDelta}");
        if (diff.DamageDelta > 0) reasons.Add($"damage+{diff.DamageDelta:F0}");
        if (diff.BlockDelta > 0) reasons.Add($"block+{diff.BlockDelta:F0}");
        if (diff.DrawDelta > 0) reasons.Add($"draw+{diff.DrawDelta:F0}");
        if (diff.ScalingDelta > 0) reasons.Add($"scaling+{diff.ScalingDelta:F0}");
        if (debuff > 0) reasons.Add($"debuff+{debuff:F0}");
        if (diff.AddedRoles.Count > 0) reasons.Add("unlocks:" + string.Join('/', diff.AddedRoles));
        return new Valuation(value, reasons.Count == 0 ? "marginal" : string.Join(',', reasons));
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

    // How much of the deck already fills the roles this card would fill. Roles
    // past their comfortable count pay progressively less.
    private static Dictionary<string, double> RoleSaturation(IReadOnlyList<CardModel> deck, CardModel card)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var existing in deck)
        {
            if (ReferenceEquals(existing, card)) continue;
            foreach (var role in RolesOf(existing))
                counts[role] = counts.GetValueOrDefault(role) + 1;
        }
        var saturation = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (role, count) in counts) saturation[role] = Decay(count);
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

    // Value per point of lift for a partner already in the deck, the most
    // partners that may pay at once, how much lift counts in full, and the
    // ceiling on the whole term. Deliberately small: a strong role fit is worth
    // ~8-14, so affinity can tip a close call but never drive the pick.
    private const double AffinityPerLift = 1.2;
    private const int AffinityPartners = 2;
    private const double AffinityLiftCap = 2.5;
    private const double AffinityCap = 6.0;

    // A payoff with no enabler. The mined affinity table is the evidence: every
    // partner Rupture has (Hemokinesis, Inferno, Breakthrough, Spite) is a card
    // that costs HP. When none of them is in the deck its trigger never fires, so
    // the scaling term must not read as if the plan were assembled. Cards with no
    // mining record keep their previous valuation exactly.
    private static bool UnsupportedScaling(CardModel card, IReadOnlyList<CardModel> deck)
    {
        if (!BakedCardAffinity.Pairs.TryGetValue(card.Id.Entry, out var partners) || partners.Length == 0) return false;
        return !deck.Any(existing => !ReferenceEquals(existing, card)
            && partners.Any(partner => string.Equals(partner.Card, existing.Id.Entry, StringComparison.Ordinal)));
    }

    // Dexterity is not a promise that needs a partner card: it multiplies every
    // block card the deck already plays. The mined table only records which cards
    // were drafted together, so a block deck without Footwork's mined partners
    // read as an unsupported payoff and the card lost 60% of its scaling — the
    // reviewed Silent had Footwork flagged `scaling-unsupported` in a deck whose
    // existing block cards were exactly what the Dexterity was for.
    //
    // Only Dexterity is exempt, and the coverage test is what makes that safe.
    // Strength looks identical from the outside but is not: Rupture reads as a
    // Strength payoff too, and its Strength only arrives when the deck loses HP.
    // Nothing the profile exposes separates "multiplies the attacks you have"
    // from "waits for a trigger", so the deck's attack count would clear the
    // warning on a card with no enabler at all. Focus is the same problem for a
    // different reason: no role describes an orb card, so there is no coverage to
    // measure. Both keep the mined prior until a conditional-Strength signal
    // exists.
    private static bool DefenceAlreadySupported(CardProfile.Facts facts, IReadOnlyList<CardModel> deck)
        => facts.Roles.Contains("dexterity")
            && deck.Count(existing => RolesOf(existing).Contains("block")) >= 3;

    private static double Affinity(CardModel card, IReadOnlyList<CardModel> deck)
    {
        if (deck.Count == 0) return 0;
        if (!BakedCardAffinity.Pairs.TryGetValue(card.Id.Entry, out var partners) || partners.Length == 0) return 0;
        var bonus = 0.0;
        var matched = 0;
        foreach (var (partner, lift) in partners)
        {
            if (!deck.Any(existing => existing.Id.Entry == partner)) continue;
            bonus += AffinityPerLift * Math.Min(lift, AffinityLiftCap);
            if (++matched >= AffinityPartners) break;
        }
        return Math.Min(bonus, AffinityCap);
    }

    // Filling a role the deck has none of is worth more than a second copy of a
    // role it already covers. The same test decides the size relief, so a card
    // cannot be told it fills a gap and then charged the full penalty for being
    // another card.
    private static double NeedBonus(CardModel card, CardProfile.Facts facts, IReadOnlyList<CardModel> deck, List<string> reasons)
        => GapBonus(facts, deck, reasons);

    private static double GapBonus(CardProfile.Facts facts, IReadOnlyList<CardModel> deck, List<string>? reasons)
    {
        var bonus = 0.0;
        void Add(double worth, string label, bool needed)
        {
            if (!needed) return;
            bonus += worth;
            reasons?.Add(label);
        }
        var attacks = deck.Count(existing => RolesOf(existing).Contains("damage"));
        var blocks = deck.Count(existing => RolesOf(existing).Contains("block"));
        Add(14, "needs-block", facts.Roles.Contains("block") && blocks < Math.Max(3, attacks / 2));
        Add(10, "needs-damage", facts.Roles.Contains("damage") && attacks < 5);
        Add(12, "needs-power",
            facts.Roles.Contains("power") && deck.Count(existing => RolesOf(existing).Contains("power")) < 3);
        Add(8, "needs-aoe",
            facts.Roles.Contains("aoe") && !deck.Any(existing => RolesOf(existing).Contains("aoe")));
        Add(6, "needs-draw",
            facts.Roles.Contains("draw") && !deck.Any(existing => RolesOf(existing).Contains("draw")));
        Add(6, "needs-energy",
            facts.Roles.Contains("energy") && !deck.Any(existing => RolesOf(existing).Contains("energy")));
        return bonus;
    }

    // Roles the deck would break without: the only copy of a defensive card, of
    // area damage, of an energy source or of a draw engine. Never removed by a
    // static low score.
    /// <summary>The role this card is the last of, or null when it can be removed.</summary>
    private static string? Blocking(CardModel card, IReadOnlyList<CardModel> deck)
    {
        var roles = RolesOf(card);
        foreach (var role in new[] { "block", "aoe", "energy", "draw" })
        {
            if (!roles.Contains(role)) continue;
            if (deck.Count(existing => RolesOf(existing).Contains(role)) <= 1) return role;
        }
        return null;
    }
}

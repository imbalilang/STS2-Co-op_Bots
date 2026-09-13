using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

/// <summary>
/// Structural, data-derived view of one card: what it actually does and which
/// deck roles it fills. Everything here comes from the card's own model (its
/// dynamic variables and keywords) rather than from an authored tier table, so
/// modded and rebalanced cards classify the same way as shipped ones.
///
/// Upgrade decisions use an isolated clone run through the game's own upgrade
/// path, so cost reductions and target/keyword changes are real differences
/// instead of a fixed bonus.
/// </summary>
internal static class CardProfile
{
    // Roles a card can fill. Saturation is tracked per role, which is what lets
    // the same card be worth less once its role is already covered.
    internal static readonly string[] Roles =
    [
        "damage", "aoe", "block", "draw", "energy", "vulnerable", "weak", "strengthdown",
        "poison", "doom", "scaling", "power", "exhaust",
        // Finer tags exist so an engine can be recognised rather than guessed:
        // "scaling" alone cannot tell a Strength deck from a poison one.
        "strength", "dexterity", "focus", "multihit", "retain", "zerocost", "shiv", "minion", "osty",
    ];

    internal sealed record Facts(
        double Damage, double Block, double Draw, double Energy,
        double Vulnerable, double Weak, double StrengthDown, double Poison, double Doom,
        double Scaling, double HpLoss, int Cost, int Hits, bool IsAoe, bool IsPower, bool Unplayable,
        IReadOnlySet<string> Roles);

    internal static Facts Of(CardModel card, int energy = 3, int enemies = 1)
    {
        var analyzed = GeniusCombatStrategy.Analyze(card, null, energy, enemies);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        if (analyzed.TotalDamage > 0)
        {
            roles.Add("damage");
            if (card.TargetType == TargetType.AllEnemies) roles.Add("aoe");
        }
        if (analyzed.Block > 0) roles.Add("block");
        if (analyzed.Draw > 0) roles.Add("draw");
        if (analyzed.ImmediateEnergy > 0) roles.Add("energy");
        if (analyzed.Vulnerable > 0) roles.Add("vulnerable");
        if (analyzed.Weak > 0) roles.Add("weak");
        if (analyzed.StrengthDown > 0) roles.Add("strengthdown");
        if (analyzed.Poison > 0) roles.Add("poison");
        if (analyzed.Doom > 0) roles.Add("doom");
        if (analyzed.Strength + analyzed.Dexterity + analyzed.Focus > 0) roles.Add("scaling");
        if (analyzed.Strength > 0) roles.Add("strength");
        if (analyzed.Dexterity > 0) roles.Add("dexterity");
        if (analyzed.Focus > 0) roles.Add("focus");
        if (card.Type == CardType.Power) roles.Add("power");
        if (card.Keywords.Contains(CardKeyword.Exhaust)) roles.Add("exhaust");
        if (card.Keywords.Contains(CardKeyword.Retain)) roles.Add("retain");
        // The analyzer clamps an unplayable card's cost to 0, so the keyword —
        // not the number — decides whether this really is a free card.
        var unplayable = card.Keywords.Contains(CardKeyword.Unplayable)
            || card.Type is CardType.Curse or CardType.Status;
        if (analyzed.EnergyCost == 0 && !unplayable) roles.Add("zerocost");
        // Damage split across several hits multiplies any Strength the deck has,
        // which is what makes multi-hit cards the payoff of a Strength engine.
        if (analyzed.TotalDamage > 0 && analyzed.Hits >= 2) roles.Add("multihit");
        if (card.Tags.Contains(CardTag.Shiv)) roles.Add("shiv");
        if (card.Tags.Contains(CardTag.Minion)) roles.Add("minion");
        if (card.Tags.Contains(CardTag.OstyAttack)) roles.Add("osty");

        return new Facts(
            analyzed.TotalDamage, analyzed.Block, analyzed.Draw, analyzed.ImmediateEnergy,
            analyzed.Vulnerable, analyzed.Weak, analyzed.StrengthDown, analyzed.Poison, analyzed.Doom,
            analyzed.Strength + analyzed.Dexterity + analyzed.Focus, analyzed.HpLoss,
            analyzed.EnergyCost, analyzed.Hits,
            card.TargetType == TargetType.AllEnemies, card.Type == CardType.Power, unplayable,
            roles);
    }

    /// <summary>
    /// The same card after its own upgrade, or null when it cannot be upgraded.
    /// The clone is never published, so the live card is untouched.
    /// </summary>
    internal static (Facts Facts, CardModel Card)? Upgraded(CardModel card, int energy = 3, int enemies = 1)
    {
        if (!card.IsUpgradable) return null;
        try
        {
            var clone = (CardModel)card.MutableClone();
            clone.UpgradeInternal();
            clone.FinalizeUpgradeInternal();
            return (Of(clone, energy, enemies), clone);
        }
        catch
        {
            // A card whose upgrade cannot be realised is treated as unupgradable
            // rather than crashing a reward or rest decision.
            return null;
        }
    }

    /// <summary>
    /// What an upgrade actually changes: cost, damage, block, draw, energy,
    /// roles and keywords. A cost drop matters far more than raw numbers,
    /// because it is what makes a whole line playable in one turn.
    /// </summary>
    internal static UpgradeDiff? DiffUpgrade(CardModel card, int energy = 3, int enemies = 1)
    {
        var before = Of(card, energy, enemies);
        if (Upgraded(card, energy, enemies) is not var (after, clone) || after is null) return null;
        var addedRoles = after.Roles.Where(role => !before.Roles.Contains(role)).ToList();
        var lostRoles = before.Roles.Where(role => !after.Roles.Contains(role)).ToList();
        var addedKeywords = clone.Keywords.Where(keyword => !card.Keywords.Contains(keyword)).ToList();
        var removedKeywords = card.Keywords.Where(keyword => !clone.Keywords.Contains(keyword)).ToList();
        return new UpgradeDiff(
            CostDelta: before.Cost - after.Cost,
            DamageDelta: after.Damage - before.Damage,
            BlockDelta: after.Block - before.Block,
            DrawDelta: after.Draw - before.Draw,
            EnergyDelta: after.Energy - before.Energy,
            // Most upgrades only raise an amount (Strength 2 -> 3, Vulnerable
            // 2 -> 3). Without these the diff reads zero for them.
            VulnerableDelta: after.Vulnerable - before.Vulnerable,
            WeakDelta: after.Weak - before.Weak,
            StrengthDownDelta: after.StrengthDown - before.StrengthDown,
            PoisonDelta: after.Poison - before.Poison,
            DoomDelta: after.Doom - before.Doom,
            ScalingDelta: after.Scaling - before.Scaling,
            AddedRoles: addedRoles,
            LostRoles: lostRoles,
            AddedKeywords: addedKeywords,
            RemovedKeywords: removedKeywords,
            Before: before,
            After: after);
    }

    internal sealed record UpgradeDiff(
        int CostDelta, double DamageDelta, double BlockDelta, double DrawDelta, double EnergyDelta,
        double VulnerableDelta, double WeakDelta, double StrengthDownDelta,
        double PoisonDelta, double DoomDelta, double ScalingDelta,
        IReadOnlyList<string> AddedRoles, IReadOnlyList<string> LostRoles,
        IReadOnlyList<CardKeyword> AddedKeywords, IReadOnlyList<CardKeyword> RemovedKeywords,
        Facts Before, Facts After);
}

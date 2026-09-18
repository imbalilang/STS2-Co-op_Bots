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
        "strength", "dexterity", "focus", "multihit", "retain", "zerocost",
        // 小刀拆成三个语义角色,不再用一个 `shiv` 标签兼任。见 Of() 里的口径注释。
        "is-shiv", "produces-shiv", "buffs-shiv",
        // 运行期才定型(Type 由 TinkerTime 事件赋值)的卡:角色取三变体并集,
        // 并打这个标记,让消费方看得见这次并集是被迫的。
        "variant-type",
        "minion", "osty",
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
        // `shiv` 一个标签曾同时压着四种含义,这是加法编码乘法最干净的实例:
        // 产者(BLADE_DANCE)、乘数器(ACCURACY)、重放器(KNIFE_TRAP)、以及资源本身(SHIV 白卡)
        // 全都带 `CardTag.Shiv`。旧 Route 拿这个标签当 payoff 并要求 >=2 张,于是
        // "两张产者、没有乘数器"(实测 OR 0.45,负资产)与"产者+ACCURACY+KNIFE_TRAP"(OR 3.11)
        // 评分完全相同。拆成三个,谁都不能再兼任:
        //   is-shiv       这张牌【就是】小刀 —— 它是资源,既不是 payoff 也不是 enabler
        //   produces-shiv 把资源造出来   —— enabler
        //   buffs-shiv    让每张小刀更值钱 —— payoff(乘数器)
        // 判据来自 BakedResources 的手工校白表(正则在这里必然出错,见烘焙脚本的注释),
        // 不再从卡面文本或 CardTag 就地推导 —— 这份表是唯一事实来源。
        var entry = card.Id.Entry;
        if (card.Tags.Contains(CardTag.Shiv)) roles.Add("is-shiv");
        foreach (var resource in BakedResources.All)
        {
            if (resource.Name != "shiv") continue;
            if (resource.Producers.Contains(entry, StringComparer.Ordinal)) roles.Add("produces-shiv");
            if (resource.Multipliers.Contains(entry, StringComparer.Ordinal)) roles.Add("buffs-shiv");
        }
        // Mad Science 一张卡就出现在 36% 的牌组里,而它的 Type 由 TinkerTime 事件运行期赋值,
        // 事件外读作 CardType.None。静态数据无从知道是哪个变体,所以取三变体角色的并集,
        // 并打 variant-type 标记。并集是被迫的过度声明 —— 标记就是让消费方能看见并自行打折,
        // 而不是让一条"猜一个变体"的规则在两边各猜各的(那正是 51% 分歧的来源)。
        if (card.GetType().Name == "MadScience") roles.Add("variant-type");
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
            // Signed: negative means the upgrade lowers the HP cost, which is a
            // real improvement the previous diff simply did not carry.
            HpLossDelta: after.HpLoss - before.HpLoss,
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
        double PoisonDelta, double DoomDelta, double ScalingDelta, double HpLossDelta,
        IReadOnlyList<string> AddedRoles, IReadOnlyList<string> LostRoles,
        IReadOnlyList<CardKeyword> AddedKeywords, IReadOnlyList<CardKeyword> RemovedKeywords,
        Facts Before, Facts After);
}

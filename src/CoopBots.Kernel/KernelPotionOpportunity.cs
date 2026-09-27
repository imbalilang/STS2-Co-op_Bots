using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel;

/// <summary>
/// Upstream compares combat endings. Our user requested (2026-09-27) a full-belt
/// spending rule and a last-chance rule even when every continuation still loses.
/// These are spending preferences, never claims that a heuristic is a victory.
/// </summary>
public static class KernelPotionOpportunity
{
    public sealed record Choice(KernelTeamSearch.Action Action, string Reason, double Benefit);

    public static bool HasFullBelt(Player p) => p.MaxPotionCount > 0
        && p.Potions.Count(q => !q.HasBeenRemovedFromState) >= p.MaxPotionCount;

    public static bool PredictedDeath(Player p, IReadOnlyList<Player> party, TerminalRecord? forecast)
    {
        if (forecast is not { Verified: true }) return false;
        for (var i = 0; i < party.Count && i < forecast.PostCombatHp.Count; i++)
            if (party[i] == p) return forecast.PostCombatHp[i] <= 0;
        return false;
    }

    public static bool LastChance(KernelSession root, Player owner, IReadOnlyList<Player> party, TerminalRecord? forecast)
        => PredictedDeath(owner, party, forecast)
            && (forecast!.RoundsSurvived <= 1 || party.Any(p => root.Hp(p.Creature) > 0 && PredictedDeath(p, party, forecast)
                && (root.Threat(p).HpLoss >= root.Hp(p.Creature) || root.Hp(p.Creature) <= root.MaxHp(p.Creature) * .3)));

    public static Choice? Choose(KernelSession root, IReadOnlyList<Player> actors,
        IReadOnlyList<Player> party, TerminalRecord? forecast = null,
        IReadOnlyList<KernelTeamSearch.Action>? plan = null)
    {
        Choice? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var owner in actors.Where(p => root.CanAct(p) && p.CanUseOrRemovePotions))
        {
            // A verified loss ten rounds away is not the last opportunity to
            // drink a chooser potion. Preserve that resource for a bad hand;
            // imminent death and a critically wounded forecast victim still
            // release every useful bottle, including repeated free actions.
            var emergency = LastChance(root, owner, party, forecast);
            var full = root.HeldPotionCount(owner) >= owner.MaxPotionCount && owner.MaxPotionCount > 0;
            if (!emergency && !full) continue;
            foreach (var potion in root.UsablePotions(owner))
            foreach (var target in root.PotionTargets(potion))
            {
                var value = Measure(root, potion, target, party, emergency);
                if (plan is not null && potion.GetType().Name is "ShacklingPotion" or "BeetleJuice" or "WeakPotion" or "BlockPotion")
                {
                    var marginal = KernelPotionPolicy.ImmediateBenefit(root, party, new(owner, null, target, potion), plan);
                    value = (Math.Min(value.Benefit, Math.Max(0, marginal)), value.FullValue && marginal >= value.Benefit);
                }
                if (value.Benefit <= .01 || !emergency && !value.FullValue) continue;
                var score = value.Benefit + (emergency ? 10000 : 0);
                if (score <= bestScore) continue;
                bestScore = score;
                best = new(new(owner, null, target, potion),
                    $"{(emergency ? "predicted-death" : "full-belt")}:benefit={value.Benefit:F1}", value.Benefit);
            }
        }
        return best;
    }

    public static (double Benefit, bool FullValue) Measure(KernelSession root,
        PotionModel potion, Creature? target, IReadOnlyList<Player> party, bool emergency = false)
    {
        var name = potion.GetType().Name;
        if (!KernelSession.CanModelPotion(potion))
        {
            // Choice bottles are executed live, followed by a fresh capture. Never
            // pretend their unresolved pick is part of a replayable combat script.
            var recipient = target?.Player ?? potion.Owner;
            var usableChoice = name switch
            {
                "AttackPotion" or "SkillPotion" or "PowerPotion" or "ColorlessPotion" => true,
                "Ashwater" => root.Hand(recipient).Any(c => c.Type is MegaCrit.Sts2.Core.Entities.Cards.CardType.Curse or MegaCrit.Sts2.Core.Entities.Cards.CardType.Status),
                "DropletOfPrecognition" => root.DrawPile(recipient).Count > 0,
                "GamblersBrew" or "TouchOfInsanity" => root.Hand(recipient).Count > 0,
                "LiquidMemories" => root.Discard(recipient).Count > 0,
                _ => false,
            };
            return (emergency && usableChoice ? 1 : 0, false);
        }
        var after = root.Fork();
        if (!after.UsePotion(potion, target, out _)) return (0, false);
        var damage = root.Enemies.Sum(e => Math.Max(0, root.Hp(e) - after.Hp(e)));
        double Exposed(KernelSession s, Creature c) => c.Player is { } p ? s.Threat(p).HpLoss : 0;
        var saved = party.Sum(p => Math.Max(0, Exposed(root, p.Creature) - Exposed(after, p.Creature))
            + Math.Max(0, after.Hp(p.Creature) - root.Hp(p.Creature)));
        if (name == "BlockPotion" && target is not null)
        {
            var dose = after.Block(target) - root.Block(target);
            var useful = Math.Min(dose, Exposed(root, target));
            return (useful, dose > 0 && useful >= dose);
        }
        if (name == "ExplosiveAmpoule") return (damage + saved, damage >= 30);
        if (name is "FirePotion" or "PotionShapedRock")
        {
            var nominal = potion.DynamicVars.Values.Where(v => v.Name == "Damage").Sum(v => (double)v.BaseValue);
            return (damage + saved, nominal > 0 && damage >= nominal);
        }
        if (name == "BloodPotion" && target is not null)
        {
            var dose = Math.Floor(root.MaxHp(target) * (double)potion.DynamicVars["HealPercent"].BaseValue / 100);
            var useful = after.Hp(target) - root.Hp(target);
            return (useful, dose > 0 && useful >= dose);
        }
        if (name is "FlexPotion" or "SpeedPotion")
        {
            if (target?.Player is not { } recipient || !root.CanAct(recipient)) return (0, false);
            var (benefit, triggers) = Amplification(root, after, recipient, name == "FlexPotion");
            // Actual extra damage / useful block, and three payable amplifications.
            return (benefit, triggers >= 3 && benefit >= 15);
        }
        var tactical = KernelTacticalPolicy.Value(after, party) - KernelTacticalPolicy.Value(root, party);
        var benefitNow = Math.Max(damage + saved, tactical);
        if (emergency && name is "VulnerablePotion" or "Duplicator" or "StrengthPotion" or "GigantificationPotion")
        {
            // A debuff/replay bottle changes the next attack, not HP at drink
            // time. Both were present in V9's final fight; a paired attack probe
            // exposed their zero immediate valuation. Resolve payable attacks
            // before calling these zero value.
            var beneficiaries = name == "VulnerablePotion" ? party
                : party.Where(p => p.Creature == target).ToArray();
            benefitNow = Math.Max(benefitNow, beneficiaries.Where(root.CanAct)
                .Sum(p => Amplification(root, after, p, attack: true).Benefit));
        }
        // Persistent buffs and debuffs can be useful even though no card or enemy
        // turn has resolved yet. The state value owns their known future benefit.
        return (Math.Max(0, benefitNow), false);
    }

    private static (double Benefit, int Triggers) Amplification(KernelSession baseline,
        KernelSession boosted, Player player, bool attack)
    {
        // Paired real branches enforce shared energy, modified costs, exhaust and
        // target survival. Counting three individually payable cards overvalued
        // hands such as three two-cost attacks with only three energy.
        var before = baseline.Fork();
        double benefit = 0;
        var triggers = 0;
        for (var step = 0; step < 3; step++)
        {
            KernelSession? nextBase = null, nextBoost = null;
            double best = 0, bestRank = 0;
            foreach (var card in before.Hand(player).Where(c => attack ? c.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack : c.GainsBlock)
                         .GroupBy(before.CardStateKey).Select(g => g.First()))
            {
                if (!before.CanPlay(card) || !boosted.CanPlay(card)) continue;
                foreach (var target in before.Targets(card))
                {
                    if (!boosted.Targets(card).Contains(target)) continue;
                    var a = before.Fork(); var b = boosted.Fork();
                    if (!a.Play(card, target, out _) || !b.Play(card, target, out _)) continue;
                    var extra = attack
                        ? before.Enemies.Sum(e => Math.Max(0, a.Hp(e) - b.Hp(e)))
                        : Math.Max(0, b.Block(player.Creature) - a.Block(player.Creature));
                    // Subtract previous accumulated divergence, not count it again.
                    var existing = attack
                        ? before.Enemies.Sum(e => Math.Max(0, before.Hp(e) - boosted.Hp(e)))
                        : Math.Max(0, boosted.Block(player.Creature) - before.Block(player.Creature));
                    var gain = extra - existing;
                    var spent = Math.Max(.25, before.Energy(player) - a.Energy(player));
                    var rank = gain / spent;
                    if (gain <= 0 || rank <= bestRank) continue;
                    best = gain; bestRank = rank; nextBase = a; nextBoost = b;
                }
            }
            if (nextBase is null || nextBoost is null) break;
            before = nextBase; boosted = nextBoost;
            benefit += best; triggers++;
        }
        if (!attack)
        {
            var incoming = boosted.CurrentAttacks().Sum(a => boosted.IntentHit(a.Enemy, player.Creature, a.Raw) * a.Repeats);
            benefit = Math.Min(benefit, Math.Max(0, incoming - before.Block(player.Creature) - before.EndPlating(player.Creature)));
        }
        return (benefit, triggers);
    }
}

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Building;

// Upstream executes resource costs/conditional effects but has no draft penalty.
// We subtract 1000 per stranded card from the common deck potential. Owner request
// 2026-10-01: a huge negative correction when exclusive resources cannot start.
// This is construction policy, not a restriction on native combat actions.
internal static class ResourceStartup
{
    internal const double Penalty = 1000;
    // Native v0.111.0 OnPlay/power hooks, not the text-derived BakedResources table:
    // BlackHole consumes triggers; RoyalGamble and TheSealedThrone need seed stars.
    private static readonly HashSet<string> StarSources = new(StringComparer.Ordinal)
    { "BIG_BANG", "GATHER_LIGHT", "GENESIS", "GLOW", "HIDDEN_CACHE", "KNOCKOUT_BLOW",
      "SHINING_STRIKE", "SOLAR_STRIKE", "VENERATE", "CONVERGENCE", "ROYAL_GAMBLE", "THE_SEALED_THRONE" };
    private static readonly HashSet<string> Summoners = new(StringComparer.Ordinal)
    { "AFTERLIFE", "BODYGUARD", "CLEANSE", "DIRGE", "INVOKE", "LEGION_OF_BONE", "NECRO_MASTERY",
      "PULL_AGGRO", "REANIMATE", "SPUR" };
    private static readonly HashSet<string> OrbSources = new(StringComparer.Ordinal)
    { "BALL_LIGHTNING", "CHAOS", "CHILL", "COLD_SNAP", "CONSUMING_SHADOW", "COOLHEADED",
      "DARKNESS", "FUSION", "GLACIER", "GLASSWORK", "ICE_LANCE", "LIGHTNING_ROD", "METEOR_STRIKE",
      "NULL", "RAINBOW", "REFRACT", "SHADOW_SHIELD", "SPINNER", "ZAP", "TEMPEST", "IGNITION" };
    // Only dedicated payoffs: CompileDriver/Shatter/FocusedStrike/BulkUp have
    // independent damage/stats and must not be treated as blank without orbs.
    private static readonly HashSet<string> OrbPayoffs = new(StringComparer.Ordinal)
    { "BARRAGE", "CAPACITOR", "COOLANT", "DUALCAST", "LOOP", "MULTI_CAST", "QUADCAST",
      "SYNCHRONIZE", "THUNDER", "VOLTAIC", "BIASED_COGNITION", "DEFRAGMENT", "HOTFIX" };

    internal sealed class Supply
    {
        private readonly double stars;
        private readonly bool osty, orbs, freeStarCost, starSpending, starXBonus;
        internal Supply(IReadOnlyList<CardModel> deck, Player player)
        {
            var relics = player.Relics.Select(r => r.Id.Entry).ToHashSet(StringComparer.Ordinal);
            var ids = deck.Select(c => c.Id.Entry).ToHashSet(StringComparer.Ordinal);
            freeStarCost = relics.Contains("BRILLIANT_SCARF");
            starXBonus = relics.Contains("CHEMICAL_X");
            stars = relics.Contains("LUNAR_PASTRY") ? double.PositiveInfinity
                : player.Relics.Where(r => r.Id.Entry is "DIVINE_RIGHT" or "DIVINE_DESTINY")
                    .Sum(r => r.DynamicVars.TryGetValue("Stars", out var amount) ? (double)amount.BaseValue : 0);
            var sources = deck.Where(c => StarSources.Contains(c.Id.Entry)).ToList();
            // Reachability, not a turn-by-turn budget. Finite exhaust producers
            // contribute once; repeatable producers can accumulate between turns.
            bool changed;
            do
            {
                changed = false;
                foreach (var c in sources.ToArray())
                {
                    var cost = Math.Max(0, c.BaseStarCost);
                    if (cost > stars && !freeStarCost) continue;
                    sources.Remove(c); changed = true;
                    var gain = c.DynamicVars.TryGetValue("Stars", out var amount) ? (double)amount.BaseValue : 0;
                    if (c.Id.Entry is "GENESIS" or "THE_SEALED_THRONE") stars = double.PositiveInfinity;
                    else if (gain > cost && !c.Keywords.Contains(CardKeyword.Exhaust)) stars = double.PositiveInfinity;
                    else stars += Math.Max(0, gain - (freeStarCost ? 0 : cost));
                }
            } while (changed && sources.Count > 0);
            starSpending = deck.Any(c => (c.HasStarCostX || c.BaseStarCost > 0) && stars > 0
                && (c.HasStarCostX || c.BaseStarCost <= stars));
            var souls = ids.Contains("SOUL") || BakedResources.All.Single(r => r.Name == "soul").Producers.Any(ids.Contains);
            osty = relics.Overlaps(new[] { "BOUND_PHYLACTERY", "PHYLACTERY_UNBOUND" })
                || ids.Overlaps(Summoners) || ids.Contains("DEVOUR_LIFE") && souls;
            orbs = relics.Overlaps(new[] { "CRACKED_CORE", "INFUSED_CORE", "SYMBIOTIC_VIRUS" })
                || ids.Overlaps(OrbSources)
                // Storm needs another power played after itself.
                || ids.Contains("STORM") && deck.Count(c => c.Type == CardType.Power) > 1
                || ids.Contains("TRASH_TO_TREASURE") && ids.Overlaps(new[]
                    { "TURBO", "OVERCLOCK", "RECKLESS_CHARGE", "WILD_STRIKE", "IMMOLATE", "POWER_THROUGH" });
        }
        internal string? Missing(CardModel card)
        {
            var id = card.Id.Entry;
            if (card.HasStarCostX && stars <= 0 && !starXBonus || card.BaseStarCost > stars && !freeStarCost)
                return "star";
            if (id is "BLACK_HOLE" or "RADIATE" && stars <= 0) return "star";
            if (id == "CHILD_OF_THE_STARS" && !starSpending) return "star-spend";
            if (card.Tags.Contains(CardTag.OstyAttack) || id is "HIGH_FIVE" or "PROTECTOR" or "SACRIFICE")
                if (!osty) return "osty";
            if (OrbPayoffs.Contains(id) && !orbs) return "orb";
            return null;
        }
    }

    internal static string? Missing(CardModel card, IReadOnlyList<CardModel> deck, Player player)
        => new Supply(deck.Any(c => ReferenceEquals(c, card)) ? deck : deck.Append(card).ToArray(), player).Missing(card);
}

using CoopBots.Building;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CoopBots;

internal static partial class AncientOptionValue
{
    private static double V(RelicModel r, string key, double fallback)
        => r.DynamicVars.TryGetValue(key, out var value) ? (double)value.BaseValue : fallback;

    private static Estimate Price(Context c, RelicModel r)
    {
        var p = new Parts(); var name = r.GetType().Name;
        var cards = (int)V(r, "Cards", 1); var energy = V(r, "Energy", 1);
        switch (name)
        {
            // NEOW: include compulsory dilution/curses and distinguish choices
            // from forced grants. Common ancient arrival healing is NOT an option.
            case "ArcaneScroll": p.Add("forced-rare", c.ForcedRare(r)); break;
            case "BoomingConch": p.Add("elite-opening-only", (c.EnergyGain(energy, 1) + c.Draw(cards, 1)) * Math.Min(1, c.Elites / Math.Max(1, c.Fights))); break;
            case "CursedPearl": p.Add("gold", c.Money(V(r,"Gold",333))).Add("greed-curse", c.Curse()); break;
            case "DowsingRod": p.Add("five-unknown-room-quest", Math.Min(1, c.Rooms(MapPointType.Unknown, 4) / 5) * .75 * c.Add("Abundance")).Add("quest-draw-delay", -EventTrade.DeadCard(c.Player)); break;
            case "FishingRod": p.Add("one-random-upgrade-per-three-normal-fights", c.RandomUpgrades(c.Fights * .65 / V(r,"Combats",3)) * .7); break;
            case "GoldenPearl": p.Add("gold", c.Money(V(r,"Gold",150))); break;
            case "HeftyTablet": p.Add("choose-one-rare", c.Offer(r, cards, CardRarity.Rare)).Add("injury-even-if-skip", c.Curse()); break;
            case "Kaleidoscope": p.Add("two-other-class-rewards", c.Offer(r, choices: cards, otherCharacters: true)); break;
            case "LargeCapsule":
                p.Add("two-relics", c.Relics((int)V(r,"Relics",2), r));
                var basics = new[] { CardTag.Strike, CardTag.Defend }.Select(tag => c.Player.Character.CardPool.AllCards
                    .FirstOrDefault(card => card.Rarity == CardRarity.Basic && card.Tags.Contains(tag))
                    ?? ModelDb.AllCards.First(card => card.GetType().Name == (tag == CardTag.Strike ? "StrikeIronclad" : "DefendIronclad"))).ToArray();
                p.Add("mandatory-extra-strike-and-defend", c.Added(basics)); break;
            case "LavaRock": p.Add("act-one-boss-delayed-relics", c.Player.RunState.CurrentActIndex == 0 ? c.Relics((int)V(r,"Relics",2)) * .65 : 0); break;
            case "LeadPaperweight": p.Add("choose-one-colorless", c.Offer(r, 2, colorless: true)); break;
            case "LeafyPoultice":
                var forced = new[] { CardTag.Strike, CardTag.Defend }.Select(tag => c.Deck.FirstOrDefault(card => card.Rarity == CardRarity.Basic && card.Tags.Contains(tag))).OfType<CardModel>();
                p.Add("transform-first-basic-pair", c.Transform(2, forced: forced, rng: c.Player.PlayerRng.Transformations)).Add("max-hp-and-clipping", c.Hp(-V(r,"MaxHp",12))); break;
            case "LostCoffer": p.Add("one-card-reward", c.Offer(r)).Add("one-potion", c.Potions(1)); break;
            case "MassiveScroll": p.Add("choose-one-multiplayer-card", c.Offer(r, multiplayer: true)); break;
            case "NeowsBones":
                var rolls = OutOfCombatPredictions.RelicPickup(c.Player, r).Relics.Where(relic => relic is not NeowsBones).ToArray();
                var expected = rolls.Length > 0 ? rolls.Sum(relic => Price(c, relic).Value)
                    : ModelDb.AllRelics.Where(relic => Catalog["Neow"].Contains(relic.GetType().Name)
                        && relic is not NeowsBones && relic.IsAllowedAtNeow(c.Player))
                        .Select(relic => Price(c, relic).Value).DefaultIfEmpty(0).Average() * V(r,"Relics",2);
                p.Add("two-neow-effects-including-their-costs", expected).Add("additional-random-curse", c.Curse((int)V(r,"Curses",1))); break;
            case "NeowsSacrifice": p.Add("ambergris-reserve", c.Has("Sozu") || !c.Player.HasOpenPotionSlots ? 0
                    : KernelPotionPolicy.PreservationValue(ModelDb.Potion<Ambergris>().ToMutable(), c.Player.RunState.Players))
                .Add("guilty-curse", c.Curse()); break;
            case "NeowsTalisman":
                p.Add("upgrade-last-basic-pair", new[] { CardTag.Strike, CardTag.Defend }.Select(tag => c.Deck.LastOrDefault(card => card.Rarity == CardRarity.Basic && card.Tags.Contains(tag)))
                    .OfType<CardModel>().Where(card => card.IsUpgradable).Sum(card => BuildValue.UpgradeForDecision(card, c.Player).Total)); break;
            case "NeowsTorment": p.Add("neows-fury", c.Add("NeowsFury")); break;
            case "NewLeaf": p.Add("one-replacement", c.Transform(cards)); break;
            case "NutritiousOyster": case "LoomingFruit": p.Add("max-hp-and-heal", c.Hp(V(r,"MaxHp",11))); break;
            case "PhialHolster": p.Add("potion-capacity", c.PotionSlots((int)V(r,"PotionSlots",1))).Add("two-potions-with-capacity", c.Potions((int)V(r,"Potions",2), (int)V(r,"PotionSlots",1))); break;
            case "Pomander": case "YummyCookie": p.Add("chosen-upgrades", c.UpgradePlan(cards).Value); break;
            case "PreciseScissors": case "EmptyCage": p.Add("forced-removal-set", c.Removal(cards)); break;
            case "PrecariousShears": p.Add("two-removals", c.Removal(cards)).Add("immediate-damage", c.Damage(V(r,"Damage",16))); break;
            case "ScrollBoxes": p.Add("choose-entire-three-card-bundle", ScrollBundle(c, r)); break;
            case "SilkenTress": p.Add("first-reward-replay-only", FutureRewardBonus(c, glam: true, 1)).Add("lose-all-current-gold", c.Money(-c.Player.Gold)); break;
            case "SilverCrucible": p.Add("first-three-upgraded-rewards", FutureRewardBonus(c, glam: false, cards)).Add("forfeit-first-chest", -c.Relics(1)); break;
            case "SmallCapsule": p.Add("one-relic-reward", c.Relics(1, r)); break;
            case "StoneHumidifier": p.Add("max-hp-only-when-resting", c.Camps * (.25 + c.Wound * .5) * c.Hp(V(r,"MaxHp",5))); break;
            case "WingedBoots": p.Add("three-route-changes-estimate", c.Player.RunState.Players.Count == 1 ? Math.Min(V(r,"Rooms",3), c.Fights) * (4 + 4 * c.Wound) : 0); break;

            // DARV: no hover-tip summation. Energy downsides are paid by the
            // actual owner/team; transforms replace cards rather than add them.
            case "Astrolabe": p.Add("three-upgraded-replacements", c.Transform(cards, upgrade: true)); break;
            case "BlackStar": p.Add("future-elite-extra-relics", c.Elites * c.Relics(1) * .7); break;
            case "CallingBell": p.Add("three-rarity-relics", c.Relics((int)V(r,"Relics",3), r)).Add("unremovable-bell-curse", c.Curse() * 1.25); break;
            case "DustyTome":
                var dusty = (DustyTome)r;
                p.Add("configured-upgraded-ancient-card", dusty.AncientCard is { } id ? c.Added(Upgrade(c, ModelDb.GetById<CardModel>(id)))
                    : c.Pool(CardRarity.Ancient).Where(card => !ArchaicTooth.TranscendenceCards.Any(t => t.Id == card.Id)).Select(card => c.Added(Upgrade(c, card))).DefaultIfEmpty(0).Average()); break;
            case "Ectoplasm": p.Add("recurring-energy", c.EnergyGain(energy)).Add("lost-future-gold", c.Has("Ectoplasm") ? 0 : -EventTrade.Gold(Math.Min(c.Fights * 17, Math.Max(0, c.Shops * 350 - c.Player.Gold)))); break;
            case "PandorasBox": p.Add("replace-all-basic-strikes-defends", c.Transform(c.Deck.Length, forced: c.Deck.Where(card => card.IsBasicStrikeOrDefend && card.IsRemovable))); break;
            case "PhilosophersStone": p.Add("owner-energy", c.EnergyGain(energy)).Add("enemy-strength-team-cost", -c.Combat(V(r,"StrengthPower",1) * 12 * c.Player.RunState.Players.Count)); break;
            case "RunicPyramid": p.Add("retain-selection", c.Draw(1.5)).Add("hand-clog", -c.Combat((c.Facts.Count(f => f.Unplayable) + Math.Max(0, 5 - c.Plays - 1)) * 5)); break;
            case "SneckoEye": p.Add("draw-two-random-cost-payability-and-tail-risk", Snecko(c)); break;
            case "Sozu": p.Add("recurring-energy", c.EnergyGain(energy)).Add("future-potions-lost-not-held-bottles", c.Has("Sozu") ? 0 : -c.Fights * 4 * (c.Has("DelicateFrond") ? 3 : 1)); break;
            case "VelvetChoker": p.Add("recurring-energy", c.EnergyGain(energy)).Add("six-play-cap-including-generated-cards", -c.Combat(Math.Max(0, c.Plays + 1 - cards) * 24)); break;

            // NONUPEIPE
            case "BeautifulBracelet": p.Add("four-random-eligible-swift-enchants", c.Enchant<Swift>(cards, (decimal)V(r,"Swift",2), random: true)); break;
            case "BlessedAntler": p.Add("recurring-energy", c.EnergyGain(energy)).Add("three-dazed-in-initial-draw-pile", -c.Draw(5 * cards / (c.Count + cards), 2)); break;
            case "BrilliantScarf": p.Add("fifth-manual-card-free", c.EnergyGain(Math.Min(2, c.Facts.Select(f => (double)f.Cost).DefaultIfEmpty(0).Max())) * Math.Clamp((c.Plays - 3) / 2, 0, 1)); break;
            case "DelicateFrond": p.Add("refill-empty-potion-slots-each-combat", c.Has("Sozu") ? 0 : Math.Min(2, c.Player.MaxPotionCount) * 8 * Math.Min(c.Fights, 8)); break;
            case "DiamondDiadem": p.Add("opening-block-with-one-turn-retention", c.Combat(V(r,"Block",20) * (1 + c.Wound))); break;
            case "FurCoat": p.Add("random-marked-combats-shared-team-benefit", Math.Min(V(r,"Combats",8), c.Fights * .3) * 10 * c.Player.RunState.Players.Count); break;
            case "Glitter": p.Add("future-reward-first-play-replay", FutureRewardBonus(c, glam: true, Math.Min(5, c.Fights * .5))); break;
            case "JewelryBox": p.Add("apotheosis", c.Add("Apotheosis")); break;
            case "SignetRing": p.Add("gold-with-spending-horizon", c.Money(V(r,"Gold",888))); break;

            // OROBAS
            case "AlchemicalCoffer": p.Add("four-new-slots", c.PotionSlots((int)V(r,"PotionSlots",4))).Add("fill-new-slots", c.Potions((int)V(r,"PotionSlots",4), (int)V(r,"PotionSlots",4))); break;
            case "ArchaicTooth": p.Add("starter-replacement-preserve-upgrade-enchantment", c.Transcend()); break;
            case "Driftwood": p.Add("reroll-card-rewards-selection-option", c.Draw(1, 1) * Math.Min(5, c.Fights * .3)); break;
            case "ElectricShrymp": p.Add("chosen-skill-auto-play-at-opening", c.Enchant<Imbued>(1)); break;
            case "GlassEye": p.Add("five-separate-rewards", c.Offer(r, choices: 5)); break;
            case "PrismaticGem": p.Add("recurring-energy", c.EnergyGain(energy)).Add("diluted-class-synergy-in-future-rewards", -c.Combat(c.Facts.Count(f => f.Roles.Contains("buffs-shiv") || f.Roles.Contains("focus")) * 3)); break;
            case "RadiantPearl": p.Add("one-retained-luminesce-two-energy-once", c.EnergyGain(2 * cards, 1)); break;
            case "SandCastle": p.Add("six-random-upgrades-not-six-best", c.RandomUpgrades(cards)); break;
            case "SeaGlass": p.Add("optional-any-number-of-fifteen-cards", SeaGlassValue(c, r)); break;
            case "TouchOfOrobas": p.Add("upgraded-minus-removed-starter-relic", c.StarterUpgrade()); break;

            // PAEL
            case "PaelsBlood": p.Add("extra-draw-each-turn", c.Draw(cards)); break;
            case "PaelsClaw": p.Add("all-eligible-defends-exhaust-and-persistent-growth", c.Enchant<Goopy>(c.Deck.Length)); break;
            case "PaelsEye": p.Add("once-per-combat-bad-hand-redraw-exhaust-cost", c.Combat(Math.Max(0, 10 + c.Facts.Count(f => f.Unplayable) * 2 - c.Facts.Count(f => f.IsPower) * 1.5))); break;
            case "PaelsFlesh": p.Add("energy-only-from-turn-three", c.EnergyGain(energy, 2)); break;
            case "PaelsGrowth": p.Add("owner-clone-option-score", EnchantmentPriorities.Table["Clone"].OptionScore!.Value); break;
            case "PaelsHorn": p.Add("two-relax-cards-with-dilution", c.Add("Relax", 2)); break;
            case "PaelsLegion": p.Add("first-block-card-doubled-every-two-turns", c.Combat(c.Facts.Where(f => f.Block > 0).Select(f => f.Block).DefaultIfEmpty(0).Average() * 2)); break;
            case "PaelsTears": p.Add("next-turn-energy-after-leaving-one-unused", c.EnergyGain(V(r,"Energy",2), 2) * Math.Clamp((5 - c.Plays) / 3, .1, 1)); break;
            case "PaelsTooth":
                var upgrade = c.UpgradePlan(cards, includePlainBasics: true);
                p.Add("delayed-return-upgrades", upgrade.Value * Math.Min(1, c.Fights / Math.Max(1, cards)) * .75)
                    .Add("temporary-removal-of-selected-cards", c.Delta(c.Deck.Except(upgrade.Targets)) * .5); break;
            case "PaelsWing": p.Add("two-skipped-rewards-per-relic", c.Fights * .25 / V(r,"Sacrifices",2) * c.Relics(1) * .7); break;

            // TANX
            case "Claws": p.Add("up-to-six-maul-replacements-preserve-upgrades", ClawPlan(c.Player, c.Deck.Where(card => card.IsTransformable), cards).Value); break;
            case "Crossbow": p.Add("free-random-attack-each-turn", c.Combat(7 * 4)); break;
            case "IronClub": p.Add("draw-per-four-plays", c.Draw(c.Plays / cards)); break;
            case "MeatCleaver": p.Add("cook-option-removal-and-hp-minus-camp-opportunity", Math.Max(0, c.Removal(2) + c.Hp(5) - Math.Max(c.UpgradePlan(1).Value, EventTrade.Healed(c.Player, c.Player.Creature.MaxHp * .3))) * Math.Min(c.Camps, 2)); break;
            case "Sai": p.Add("block-each-turn", c.Combat(V(r,"Block",7) * 4 * .75)); break;
            case "SpikedGauntlets": p.Add("recurring-energy", c.EnergyGain(energy)).Add("power-cost-increase", -c.Combat(c.Facts.Count(f => f.IsPower) * 6)); break;
            case "TanxsWhistle": p.Add("whistle-card", c.Add("Whistle")); break;
            case "ThrowingAxe": p.Add("first-card-replay-not-attacks-only", c.Combat(c.Facts.Where(f => !f.Unplayable).Select(UseValue).OrderDescending().Take(3).DefaultIfEmpty(0).Average() * .8)); break;
            case "TriBoomerang": p.Add("three-chosen-double-damage-enchants", c.Enchant<Instinct>(cards)); break;
            case "WarHammer": p.Add("four-random-upgrades-after-elites", c.RandomUpgrades(cards * c.Elites) * .65); break;

            // TEZCATARA
            case "BiiigHug":
                var hug = BiiigHugPackage(c.Player, cards);
                p.Add("joint-removals", hug.Removals).Add("soot-after-removals-on-every-shuffle", hug.Soot); break;
            case "GoldenCompass": p.Add("shared-map-rewrite-and-event-only-unknowns-estimate", c.Player.RunState.Players.Sum(ally => 12 + 12 * (1 - (double)ally.Creature.CurrentHp / Math.Max(1, ally.Creature.MaxHp)))); break;
            case "NutritiousSoup": p.Add("all-basic-strikes-free-eternal-plus-three-damage", c.Enchant<TezcatarasEmber>(c.Deck.Length, filter: card => card.Rarity == CardRarity.Basic && card.Tags.Contains(CardTag.Strike))); break;
            case "PumpkinCandle": p.Add("five-combats-energy", c.EnergyGain(energy) * Math.Min(1, V(r,"CombatCount",5) / Math.Max(1,c.Fights))).Add("rekindle-minus-camp-opportunity", Math.Max(0, c.EnergyGain(energy) * .5 - c.UpgradePlan(1).Value) * Math.Min(1, c.Camps)); break;
            case "SealOfGold": p.Add("energy-while-affordable", c.EnergyGain(energy) * Math.Min(1, (c.Player.Gold + (c.Has("Ectoplasm") ? 0 : c.Fights * 17)) / Math.Max(1, c.Fights * 4 * V(r,"Gold",3))))
                .Add("per-turn-gold-payment", -EventTrade.Gold(Math.Min(c.Player.Gold + (c.Has("Ectoplasm") ? 0 : c.Fights * 17), c.Fights * 4 * V(r,"Gold",3)))); break;
            case "Storybook": p.Add("brightest-flame-card", c.Add("BrightestFlame")); break;
            case "ToastyMittens": p.Add("strength-growing-each-turn", c.Combat(V(r,"StrengthPower",1) * c.AttackPlays * 10)).Add("mandatory-hand-exhaust", -c.Draw(Math.Clamp(1 - c.Facts.Count(f => f.Unplayable) / c.Count * 5, 0, 1), 3)); break;
            case "ToyBox": p.Add("five-wax-relics-one-melts-every-three-fights", c.Relics((int)V(r,"Relics",5), r, wax: true)); break;
            case "VeryHotCocoa": p.Add("four-energy-opening-only", c.EnergyGain(energy, 1)); break;

            // VAKUU
            case "BloodSoakedRose": p.Add("recurring-energy", c.EnergyGain(energy)).Add("enthralled-curse", c.Curse()); break;
            case "ChoicesParadox": p.Add("one-of-five-retained-opening-options", c.Draw(1, 1) + c.Combat(6)); break;
            case "DistinguishedCape": p.Add("three-apparitions-with-dilution", c.Add("Apparition", cards)).Add("two-distinct-curses", c.Curse((int)V(r,"Curses",2))); break;
            case "Fiddle": p.Add("two-extra-turn-start-draws", c.Draw(cards)).Add("blocked-card-draw-engines", -c.Draw(c.DrawCards)); break;
            case "JeweledMask": p.Add("random-power-tutored-and-free-opening", c.Facts.Where(f => f.IsPower).Select(f => c.Combat(5 + 6 * f.Cost)).DefaultIfEmpty(0).Average()); break;
            case "LordsParasol": p.Add("shops-free-relics-potions-removal", c.Shops * (c.Relics(3) + c.Potions(3) + c.Removal(1)))
                .Add("forced-seven-shop-cards-dilution", -c.Shops * c.Count * .35); break;
            case "MusicBox": p.Add("paid-ethereal-copy-of-first-attack", c.Combat(c.AttackPlays > 0 ? Math.Min(10, c.Facts.Where(f => f.Damage > 0).Select(f => f.Damage / Math.Max(1,f.Cost)).DefaultIfEmpty(0).Max()) * Math.Clamp((c.Energy - 1) / 3d, 0, 1) * 4 : 0)); break;
            case "PreservedFog": p.Add("three-removals", c.Removal(cards)).Add("folly-curse", c.Curse()); break;
            case "SereTalon": p.Add("three-wishes-with-dilution", c.Add("Wish", (int)V(r,"Wishes",3))).Add("nine-max-hp-and-clipping", c.Hp(-V(r,"HpLoss",9))); break;
            case "WhisperingEarring": p.Add("recurring-energy", c.EnergyGain(energy)).Add("forced-opening-order-and-target-risk", -c.Combat(6 + c.Facts.Count(f => f.IsPower || f.HpLoss > 0) * 2)); break;
            default: return new(-1000, "missing-native-effect:" + name, false);
        }
        return p.Finish();
    }

    private static CardModel Upgrade(Context c, CardModel card)
    { var clone = c.Copy(card); if (clone.IsUpgradable) { clone.UpgradeInternal(); clone.FinalizeUpgradeInternal(); } return clone; }
    private static double UseValue(CardProfile.Facts f) => Math.Max(.5, f.Damage + .7 * f.Block + 3 * f.Draw + 6 * f.Energy + 4 * f.Scaling);

    // Compare payable hands on paired deterministic samples, including both
    // +2 draw and 0..3 randomized costs; averaging costs alone misses free cards.
    // The lower tail prices the opening risk when the party is already wounded.
    private static double Snecko(Context c)
    {
        if (c.Has("SneckoEye") || c.Deck.Length == 0) return 0;
        var rng = new Random(1729); var improvements = new List<double>();
        for (var sample = 0; sample < 64; sample++)
        {
            var order = Enumerable.Range(0, c.Deck.Length).OrderBy(_ => rng.Next()).ToArray();
            double Hand(int draws, bool confused)
            {
                var dp = new double[Math.Max(0, c.Energy) + 1];
                foreach (var index in order.Take(draws))
                {
                    var f = c.Facts[index]; if (f.Unplayable) continue;
                    var cost = confused && c.Deck[index].EnergyCost.Canonical >= 0 ? rng.Next(4) : Math.Max(0, f.Cost);
                    var value = UseValue(f);
                    for (var budget = c.Energy; budget >= cost; budget--) dp[budget] = Math.Max(dp[budget], dp[budget-cost] + value);
                }
                return dp.Max();
            }
            improvements.Add(Hand(7, true) - Hand(5, false));
        }
        return c.Combat(4 * (improvements.Average() + (.25 + c.Wound) * Math.Min(0, improvements.Order().Take(8).Average())));
    }
    private static double ScrollBundle(Context c, RelicModel r)
    {
        var bundles = OutOfCombatPredictions.RelicPickup(c.Player, r).Bundles;
        if (bundles.Count > 0) return bundles.Max(b => c.Added(b.Cards.ToArray()));
        var commons = c.Pool(CardRarity.Common); var uncommons = c.Pool(CardRarity.Uncommon);
        if (commons.Length < 2 || uncommons.Length == 0) return 0;
        return Enumerable.Range(0, 8).Select(i => c.Added(commons[i % commons.Length], commons[(i+1) % commons.Length], uncommons[i % uncommons.Length])).Average();
    }
    private static double SeaGlassValue(Context c, RelicModel r)
    {
        var offered = OutOfCombatPredictions.RelicPickup(c.Player, r).Bundles.SelectMany(b => b.Cards).ToArray();
        if (offered.Length == 0)
        {
            // Multiplayer cannot use the private reward RNG preview. Preserve the
            // advertised character and five cards of EACH rarity in the estimate.
            var character = ((SeaGlass)r).CharacterId is { } id ? ModelDb.GetById<CharacterModel>(id) : c.Player.Character;
            offered = new[] { CardRarity.Common, CardRarity.Uncommon, CardRarity.Rare }.SelectMany(rarity => {
                var pool = character.CardPool.GetUnlockedCards(c.Player.UnlockState, c.Player.RunState.CardMultiplayerConstraint)
                    .Where(card => card.Rarity == rarity).OrderBy(card => card.Id.Entry, StringComparer.Ordinal).ToArray();
                return Enumerable.Range(0, Math.Min(5, pool.Length)).Select(i => c.Copy(pool[i * pool.Length / Math.Min(5, pool.Length)]));
            }).ToArray();
        }
        var (selected, _) = BuildValue.GreedyPick(c.Player, offered, 0, offered.Length);
        return c.Added(selected.ToArray());
    }
    private static double FutureRewardBonus(Context c, bool glam, double count)
    {
        var pool = c.Pool().Where(card => glam ? ModelDb.Enchantment<Glam>().CanEnchant(card) : card.IsUpgradable).ToArray();
        if (pool.Length == 0) return 0;
        var samples = Enumerable.Range(0, Math.Min(8,pool.Length)).Select(i => pool[i*pool.Length/Math.Min(8,pool.Length)]);
        var mean = samples.Select(card => {
            var after = glam ? c.Copy(card) : Upgrade(c, card);
            if (glam) { var e = ModelDb.Enchantment<Glam>().ToMutable(); after.EnchantInternal(e, 1); e.ModifyCard(); }
            return Math.Max(0, c.Added(after) - c.Added(card));
        }).Average();
        return mean * Math.Min(count, c.Fights) * .65;
    }
    internal static (IReadOnlyList<CardModel> Cards, double Value) ClawPlan(MegaCrit.Sts2.Core.Entities.Players.Player player,
        IEnumerable<CardModel> candidates, int max)
    {
        var c = new Context(player);
        var after = c.Deck.ToList(); var remaining = candidates.ToList(); var selected = new List<CardModel>(); var total = 0d;
        CardModel Maul(CardModel card)
        {
            var maul = c.Card("Maul", card.IsUpgraded);
            if (card.Enchantment is { } e && e.CanEnchant(maul)) { var copy = (EnchantmentModel)e.MutableClone(); maul.EnchantInternal(copy, copy.Amount); copy.ModifyCard(); }
            return maul;
        }
        while (selected.Count < max && remaining.Count > 0)
        {
            var baseline = BuildValue.Deck(after, player);
            var best = remaining.Select(card => (Card: card, Replacement: Maul(card)))
                .Select(x => (x.Card, x.Replacement, Value: BuildValue.Deck(after.Select(old => ReferenceEquals(old, x.Card) ? x.Replacement : old).ToArray(), player) - baseline))
                .OrderByDescending(x => x.Value).ThenBy(x => x.Card.Id.Entry, StringComparer.Ordinal).First();
            if (ConstructionDecision.AwaitingValues || best.Value <= 0) break;
            selected.Add(best.Card); remaining.Remove(best.Card);
            after[after.IndexOf(best.Card)] = best.Replacement; total += best.Value;
        }
        return (selected, total);
    }
}

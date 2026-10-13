using System.Reflection;
using System.Text.Json;
using CoopBots;
using CoopBots.Building;
using DeckSim;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Native mechanism checks and a same-price shop fixture. Community popularity
// is not a requirement that Bully wins every deck.
internal static class BullyPriceAnchorScenarios
{
    // R2 outcome calibration: set CommitmentWeight=0 -> exit 1:
    // System.Exception: Bully price anchor: 3-energy 1p starter must prefer Bully at the same 75 gold price; starter shop must buy Bully, then prefer 100-gold basic removal over 75-gold Bludgeon
    // work/deck-outcome-optimization/r2-results.json; restored source is tested by the acceptance run.
    // R2 2026-09-30: six production mutations each exit 1, restored gates exit 0.
    // Commands/raw logs: work/energy-commitment-risk/r2-results.json.
    // bully-native-upgrade: System.Exception: Bully mechanism: Bully payoff must use native ExtraDamage before and after upgrade; cycle valuation must realize native Bully upgrade with vulnerable; hand valuation must realize native Bully upgrade with vulnerable; living ally vulnerable must increase Bully acquisition and upgrade values
    // bully-cycle: System.Exception: Bully mechanism: cycle valuation must realize native Bully upgrade with vulnerable; living ally vulnerable must increase Bully acquisition and upgrade values
    // bully-hand: System.Exception: Bully mechanism: hand valuation must realize native Bully upgrade with vulnerable
    // bully-phantom: System.Exception: Bully mechanism: Bully upgrade needs an actual vulnerable source
    // bully-dead-ally: System.Exception: Bully mechanism: dead ally must not supply vulnerable
    // bully-price-ranking: System.Exception: Bully price anchor: 3-energy 1p starter must prefer Bully at the same 75 gold price; 3-energy 1p own-vulnerable must prefer Bully at the same 75 gold price; 3-energy 4p starter must prefer Bully at the same 75 gold price; 3-energy 4p own-vulnerable must prefer Bully at the same 75 gold price; 3-energy 4p ally-vulnerable must prefer Bully at the same 75 gold price
    internal static void Run(bool starterOnly = false, string? outputPath = null)
    {
        CheckMechanism();
        var errors = new List<string>();
        var rows = new List<object>();
        foreach (var ascension in new[] { 0, 10 })
        foreach (var seats in new[] { 1, 4 })
        foreach (var energy in new[] { 3, 4 })
        foreach (var support in new[] { "starter", "own-vulnerable", "ally-vulnerable" })
        {
            if (starterOnly && (energy != 3 || support != "starter")) continue;
            if (ascension == 10 && (energy != 3 || support != "starter")) continue;
            if (seats == 1 && support == "ally-vulnerable") continue;
            var ps = Enumerable.Range(0, seats).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
                BotRegistry.CreateId(BotDifficulty.Pro, 84, i))).ToArray();
            var run = RunState.CreateForTest(ps, ascensionLevel: ascension, seed: "BULLY-PRICE-ANCHOR");
            foreach (var q in ps) { q.MaxEnergy = energy; q.Creature.SetMaxHpInternal(80); q.Creature.SetCurrentHpInternal(80); }
            var p = ps[0]; p.Gold = 100;
            for (var i = 0; i < 5; i++) p.Deck.AddInternal(run.CreateCard<StrikeIronclad>(p));
            for (var i = 0; i < 4; i++) p.Deck.AddInternal(run.CreateCard<DefendIronclad>(p));
            p.Deck.AddInternal(run.CreateCard<Bash>(p));
            foreach(var ally in ps.Skip(1))
            {
                for(var i=0;i<5;i++)ally.Deck.AddInternal(run.CreateCard<StrikeIronclad>(ally));
                for(var i=0;i<4;i++)ally.Deck.AddInternal(run.CreateCard<DefendIronclad>(ally));
                ally.Deck.AddInternal(run.CreateCard<Bash>(ally));
            }
            if(ascension>=10)foreach(var q in ps)q.Deck.AddInternal(run.CreateCard<AscendersBane>(q));
            if (support != "starter")
            {
                var q = support == "own-vulnerable" ? p : ps[1];
                if (q != p)
                {
                    for (var i = 0; i < 4; i++) q.Deck.AddInternal(run.CreateCard<StrikeIronclad>(q));
                    for (var i = 0; i < 4; i++) q.Deck.AddInternal(run.CreateCard<DefendIronclad>(q));
                }
                q.Deck.AddInternal(run.CreateCard<Shockwave>(q));
                q.Deck.AddInternal(run.CreateCard<Tremble>(q));
            }
            using var evaluation = DeckStrength.BeginEvaluation();
            var bully = run.CreateCard<Bully>(p); var bludgeon = run.CreateCard<Bludgeon>(p);
            if (bully.Rarity != bludgeon.Rarity) throw new Exception("Bully price anchor needs equal native rarity");
            var b = BuildValue.Add(bully, p); var d = BuildValue.Add(bludgeon, p);
            var inventory = new MerchantInventory(p);
            foreach (var card in new CardModel[] { bully, bludgeon })
            {
                var entry = new MerchantCardEntry(p, inventory, [], CardType.Attack);
                typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(entry, new CardCreationResult(card));
                typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, 75);
                ((List<MerchantCardEntry>)inventory.CharacterCardEntries).Add(entry);
            }
            var choice = BotShopPlanner.Choose(inventory);
            var selected = choice is null ? "save" : BotShopPlanner.Key(inventory.AllEntries.ElementAt(choice.Index));
            if (energy == 3 && (b.Total <= d.Total || selected != "BULLY"))
                errors.Add($"3-energy {seats}p {support} must prefer Bully at the same 75 gold price");
            var up = BuildValue.UpgradeDelta(bully, p).Total;
            var removalValues = p.Deck.Cards.Select(c => new { card = c.Id.Entry,
                delta = BuildValue.Remove(c, p).Total, reason = BuildValue.Remove(c, p).Reason,
                shopGold = BotShopPlanner.GoldPerDeckValue * BotShopPlanner.RemovalValue(c, p) }).ToArray();
            var bestRemoval = removalValues.OrderByDescending(c => c.shopGold).First();
            var removalEntry = new MerchantCardRemovalEntry(p);
            typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(removalEntry, 100);
            typeof(MerchantInventory).GetProperty("CardRemovalEntry")!.SetValue(inventory, removalEntry);
            string ShopChoice()
            {
                var pick = BotShopPlanner.Choose(inventory);
                return pick is null ? "save" : BotShopPlanner.Key(inventory.AllEntries.ElementAt(pick.Index));
            }
            var withRemoval = ShopChoice();
            var bullyEntry = inventory.CharacterCardEntries.First(c => c.CreationResult?.Card == bully);
            ((List<MerchantCardEntry>)inventory.CharacterCardEntries).Remove(bullyEntry);
            var withoutBully = ShopChoice();
            // Owner removed the mandatory Bully purchase anchor. Retain the
            // separate removal-vs-Bludgeon check and the same-price card ranking.
            if (energy == 3 && support == "starter" && withoutBully != "remove")
                errors.Add("starter shop must prefer 100-gold basic removal over 75-gold Bludgeon");
            var horizon = BotRestSitePatch.InvestmentHorizon(p);
            if (seats == 1 && energy == 3 && support == "starter")
            {
                Console.WriteLine($"VALUE PARTS BULLY {b.Reason}");
                Console.WriteLine($"VALUE PARTS BLUDGEON {d.Reason}");
                Console.WriteLine($"VALUE PARTS REMOVE {bestRemoval.card} {bestRemoval.reason}");
            }
            Console.WriteLine($"REMOVAL PRICE {seats}p energy={energy} {support} horizon={horizon:F3} remove={bestRemoval.card} delta={bestRemoval.delta:F3} goldValue={bestRemoval.shopGold:F3} net={bestRemoval.shopGold-100:F3} bullyGold={BotShopPlanner.GoldPerDeckValue*horizon*b.Total:F3} bludgeonGold={BotShopPlanner.GoldPerDeckValue*horizon*d.Total:F3} all={withRemoval} noBully={withoutBully}");
            Console.WriteLine($"BULLY ANCHOR {seats}p energy={energy} {support} bully={b.Total:F3} bludgeon={d.Total:F3} bullyUpgrade={up:F3} samePrice75={selected}");
            rows.Add(new { ascension, seats, energy, support, bully = b.Total, bludgeon = d.Total, bullyUpgrade = up,
                bullyWithoutRisk = b.Total + DeckStrength.CommitmentWeight * DeckStrength.CommitmentLoad(bully, energy),
                bludgeonWithoutRisk = d.Total + DeckStrength.CommitmentWeight * DeckStrength.CommitmentLoad(bludgeon, energy), selected,
                removalValues, bestRemoval, horizon, withRemoval, withoutBully });
        }
        var output = outputPath ?? Path.Combine(DraftSimHarness.RepoRoot(), "work/energy-commitment-risk/bully-anchor.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(rows));
        if (errors.Count > 0) throw new Exception("Bully price anchor: " + string.Join("; ", errors));
        Console.WriteLine("PASS: Bully native vulnerable payoff, upgrade and same-price shop anchor.");
    }

    private static void CheckMechanism()
    {
        var errors = new List<string>();
        void Check(bool ok, string message) { if (!ok) errors.Add(message); }
        var ps = Enumerable.Range(1, 2).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)i)).ToArray();
        var run = RunState.CreateForTest(ps, seed: "BULLY-PAYOFF");
        var p = ps[0]; var ally = ps[1];
        foreach (var q in ps) { q.MaxEnergy = 3; q.Creature.SetMaxHpInternal(80); q.Creature.SetCurrentHpInternal(80); }
        for (var i = 0; i < 5; i++) p.Deck.AddInternal(run.CreateCard<StrikeIronclad>(p));
        for (var i = 0; i < 4; i++) p.Deck.AddInternal(run.CreateCard<DefendIronclad>(p));
        var original = p.Deck.Cards.ToArray();
        var bully = run.CreateCard<Bully>(p);
        var upgrade = CardProfile.Upgraded(bully, 3)!.Value.Card;
        Check(SustainedDeckValue.DamagePerVulnerable(bully) == 2 && SustainedDeckValue.DamagePerVulnerable(upgrade) == 3,
            "Bully payoff must use native ExtraDamage before and after upgrade");
        var plain = original.Append(bully).ToArray();
        var plainUp = original.Append(upgrade).ToArray();
        // Growth now has explicit future reward sources; its value can differ.
        // The CURRENT fight still cannot invent vulnerable for an upgrade.
        Check(Math.Abs(SustainedDeckValue.Strength(plain, 3, true) - SustainedDeckValue.Strength(plainUp, 3, true)) < 1e-8
            && Math.Abs(CounterfactualDraft.Strength(plain, 3) - CounterfactualDraft.Strength(plainUp, 3)) < 1e-8,
            "Bully upgrade needs an actual vulnerable source");
        var support = run.CreateCard<Shockwave>(p);
        var deck = plain.Append(support).ToArray();
        var improved = original.Append(upgrade).Append(support).ToArray();
        Check(SustainedDeckValue.Strength(improved, 3, true) > SustainedDeckValue.Strength(deck, 3, true),
            "cycle valuation must realize native Bully upgrade with vulnerable");
        Check(CounterfactualDraft.Strength(improved, 3) > CounterfactualDraft.Strength(deck, 3),
            "hand valuation must realize native Bully upgrade with vulnerable");
        var withoutAlly = BuildValue.Add(bully, p).Total;
        ally.Deck.AddInternal(run.CreateCard<Shockwave>(ally));
        ally.Deck.AddInternal(run.CreateCard<Tremble>(ally));
        var aliveAlly = BuildValue.Add(bully, p).Total;
        Check(aliveAlly > withoutAlly && BuildValue.UpgradeDelta(bully, p).Total > 0,
            "living ally vulnerable must increase Bully acquisition and upgrade values");
        ally.Creature.SetCurrentHpInternal(0);
        Check(Math.Abs(BuildValue.Add(bully, p).Total - withoutAlly) < 1e-8,
            "dead ally must not supply vulnerable");
        Check(p.Deck.Cards.SequenceEqual(original) && !bully.IsUpgraded,
            "Bully counterfactuals must preserve the native deck and card");
        if (errors.Count > 0) throw new Exception("Bully mechanism: " + string.Join("; ", errors));
    }
}

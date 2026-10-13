using System.Reflection;
using System.Text.Json;
using CoopBots;
using CoopBots.Building;
using DeckSim;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class ShopRemovalAnchorScenarios
{
    // R2 outcome calibration: GoldPerDeckValue=.01 -> exit 1:
    // System.Exception: Shop removal anchor: six-shop cohort needs mostly 2-3 removals, mean 2-3 and no zero-removal seat: 0,0,0,0
    // work/deck-outcome-optimization/r2-results.json; restored acceptance run is green.
    // R2: deleting the starter prior exited 1; restoring it exited 0.
    // System.Exception: Shop removal anchor: six-shop fixture needs 2-3 removals, got 0
    // Command/raw logs: work/unified-deck-strength/r2-shop-anchor.json.
    internal static void Run()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DraftSimHarness.RepoRoot(), "tests/PatchSmoke/Fixtures/unified-shops.json")));
        var report = new List<object>();
        var failures = new List<string>();
        var frequencies = new List<int>();
        foreach(var cohort in doc.RootElement.GetProperty("shops").EnumerateArray().ToArray().Chunk(4))
        foreach (var row in cohort)
        {
            var p = PlayerFor(row,cohort);
            using var evaluation = DeckStrength.BeginEvaluation();
            p.Gold = row.GetProperty("gold").GetInt32();
            var inventory = new MerchantInventory(p);
            foreach (var item in row.GetProperty("stock").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var cost = item.GetProperty("cost").GetInt32();
                if (id == "remove")
                {
                    var remove = new MerchantCardRemovalEntry(p); Cost(remove, cost);
                    typeof(MerchantInventory).GetProperty("CardRemovalEntry")!.SetValue(inventory, remove);
                }
                else if (ModelDb.AllCards.FirstOrDefault(c => c.Id.Entry == id) is { } card)
                    Offer(inventory, p.RunState.CreateCard(card, p), cost);
            }
            var target = BotShopPlanner.BestRemoval(p.Deck.Cards,p);
            var best = (Card:p.Deck.Cards[target.index],Value:target.value);
            var choice = BotShopPlanner.Choose(inventory);
            var items = inventory.CharacterCardEntries.Cast<MerchantEntry>().Append(inventory.CardRemovalEntry!).ToArray();
            var chosen = choice is null ? "save" : BotShopPlanner.Key(items[choice.Index]);
            Console.WriteLine($"SHOP ANCHOR seat={row.GetProperty("seat").GetString()} gold={p.Gold} size={p.Deck.Cards.Count} removal={best.Card.Id.Entry}:{best.Value*BotShopPlanner.GoldPerDeckValue:F1}g chosen={chosen}");
            report.Add(new { seat = row.GetProperty("seat").GetString(), gold = p.Gold, size = p.Deck.Cards.Count,
                removalCard = best.Card.Id.Entry, removalGold = best.Value*BotShopPlanner.GoldPerDeckValue, chosen });
        }
        var snapshots=doc.RootElement.GetProperty("snapshots").EnumerateArray().Where(r=>r.GetProperty("stage").GetString()=="act2").ToArray();
        foreach (var row in snapshots)
        {
            var p = PlayerFor(row,snapshots); p.Gold = 0;
            int removals = 0, bought = 0;
            // Sensitivity probe, not a replay or a victory claim: six shops,
            // 150 fresh gold per shop, native escalating removal price, same
            // damage/draw card at 60 gold. No target removal count enters policy.
            for (var visit = 0; visit < 6; visit++)
            {
                using var evaluation = DeckStrength.BeginEvaluation();
                p.Gold += 150;
                var inventory = new MerchantInventory(p);
                var candidate = p.RunState.CreateCard(ModelDb.AllCards.First(c => c.Id.Entry == (p.Deck.Cards.Any(c => c.Id.Entry == "BASH") ? "POMMEL_STRIKE" : "BACKFLIP")), p);
                Offer(inventory, candidate, 60);
                var remove = new MerchantCardRemovalEntry(p);
                // A10 Inflation: first removal 100, then +50 (native MerchantCardRemovalEntry).
                Cost(remove, 100 + 50 * p.ExtraFields.CardShopRemovalsUsed);
                typeof(MerchantInventory).GetProperty("CardRemovalEntry")!.SetValue(inventory, remove);
                var target = BotShopPlanner.BestRemoval(p.Deck.Cards,p);
                var best = (Card:p.Deck.Cards[target.index],Value:target.value);
                Console.WriteLine($"SHOP VISIT seat={row.GetProperty("seat").GetString()} visit={visit+1} gold={p.Gold} removal={best.Card.Id.Entry}:{best.Value*BotShopPlanner.GoldPerDeckValue:F2} price={remove.Cost}");
                var choice = BotShopPlanner.Choose(inventory);
                if (choice is null) continue;
                p.Gold -= choice.Cost;
                if (choice.RemovalIndex >= 0)
                { p.Deck.RemoveInternal(p.Deck.Cards[choice.RemovalIndex]); p.ExtraFields.CardShopRemovalsUsed++; removals++; }
                else { p.Deck.AddInternal(candidate); bought++; }
            }
            Console.WriteLine($"SHOP FREQUENCY seat={row.GetProperty("seat").GetString()} removals={removals} cards={bought} goldLeft={p.Gold}");
            frequencies.Add(removals);
            report.Add(new { seat = row.GetProperty("seat").GetString(), probe = "six-shop-sensitivity", removals, bought, gold = p.Gold });
        }
        var output = Path.Combine(DraftSimHarness.RepoRoot(), "work/unified-deck-strength");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "shop-anchor.json"), JsonSerializer.Serialize(report));
        // 2026-10-03 coverage decision: BladeSymphony was a blank effect in
        // the first deck. Modeling its generated attacks changes this cohort
        // from 3,4,3,3 to 4,4,3,3. Preserve the owner's existing mean/range and
        // zero-removal guard; do not require 3 of these 4 historical hands to
        // retain their old count after correcting a card's missing payoff.
        // This small synthetic cohort cannot estimate the population frequency.
        // Owner 2026-10-04 requires spare cash to become strength earlier.
        // This fixed cohort changes 4,4,3,3 -> 4,4,3,4 (mean 3.75). Accept a
        // mean up to four, retaining the no-zero and excessive-removal guards.
        // R2 (GoldPerDeckValue=.01), exit 1; policy-red-removal-anchor.log:
        // System.Exception: Shop removal anchor: six-shop cohort needs mean 2-4 and no zero-removal seat: 0,0,0,0
        if (frequencies.Any(n => n == 0) || frequencies.Average() is < 2 or > 4)
            failures.Add("six-shop cohort needs mean 2-4 and no zero-removal seat: " + string.Join(',',frequencies));
        if (failures.Count > 0) throw new Exception("Shop removal anchor: " + string.Join("; ",failures));
        Console.WriteLine("PASS: fixed six-shop cohort averages 2-4 removals without a runtime removal quota.");
    }

    private static Player PlayerFor(JsonElement owner,IReadOnlyList<JsonElement> cohort)
    {
        var ps = Enumerable.Range(1, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 81, i))).ToArray();
        var run = RunState.CreateForTest(ps, ascensionLevel: 10, seed: "SHOP-ANCHOR");
        foreach (var q in ps) { q.ResetCombatState(); q.MaxEnergy = 3; q.Creature.SetMaxHpInternal(80); q.Creature.SetCurrentHpInternal(64); }
        // Shared status needs the public allied decks from the SAME snapshot.
        // The old fixture silently left all three teammates' decks empty.
        for(var i=0;i<cohort.Count;i++)
        foreach (var token in cohort[i].GetProperty("deck").EnumerateArray())
        {
            var id = token.GetString()!;
            var card = run.CreateCard(ModelDb.AllCards.First(c => c.Id.Entry == id.TrimEnd('+')), ps[i]);
            if (id.EndsWith('+')) { card.UpgradeInternal(); card.FinalizeUpgradeInternal(); }
            ps[i].Deck.AddInternal(card);
        }
        return ps[Enumerable.Range(0,cohort.Count).Single(i=>cohort[i].GetProperty("seat").GetString()==owner.GetProperty("seat").GetString())];
    }

    // Diagnostic entry: intentionally uses only APIs also present in the
    // installed baseline DLL so both binaries can replay the same fixture.
    internal static void Audit()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DraftSimHarness.RepoRoot(),"tests/PatchSmoke/Fixtures/unified-shops.json")));
        var snapshots=doc.RootElement.GetProperty("snapshots").EnumerateArray().Where(r=>r.GetProperty("stage").GetString()=="act2").ToArray();
        foreach (var row in snapshots)
        {
            var p=PlayerFor(row,snapshots); p.Gold=0;
            for(var visit=1;visit<=6;visit++)
            {
                using var evaluation=DeckStrength.BeginEvaluation(); p.Gold+=150;
                var inventory=new MerchantInventory(p);
                var card=p.RunState.CreateCard(ModelDb.AllCards.First(c=>c.Id.Entry==(p.Deck.Cards.Any(c=>c.Id.Entry=="BASH")?"POMMEL_STRIKE":"BACKFLIP")),p);
                Offer(inventory,card,60);
                var removal=new MerchantCardRemovalEntry(p); Cost(removal,100+50*p.ExtraFields.CardShopRemovalsUsed);
                typeof(MerchantInventory).GetProperty("CardRemovalEntry")!.SetValue(inventory,removal);
                var choice=BotShopPlanner.Choose(inventory);
                if(choice is null) continue;
                if(choice.RemovalIndex>=0)
                {
                    var target=p.Deck.Cards[choice.RemovalIndex];
                    var alternatives=p.Deck.Cards.Where(c=>c.IsRemovable && c.Rarity==CardRarity.Basic && !c.IsUpgraded
                        && (c.Tags.Contains(CardTag.Strike)||c.Tags.Contains(CardTag.Defend)))
                        .GroupBy(c=>c.Id.Entry).Select(g=>g.First()).Select(c=>new {id=c.Id.Entry,score=BuildValue.Remove(c,p),gold=BotShopPlanner.RemovalValue(c,p)*BotShopPlanner.GoldPerDeckValue}).ToArray();
                    Console.WriteLine(JsonSerializer.Serialize(new {seat=row.GetProperty("seat").GetString(),visit,cost=choice.Cost,
                        removed=target.Id.Entry,score=BuildValue.Remove(target,p),gold=choice.Value,basics=alternatives,
                        deck=p.Deck.Cards.Select(c=>c.Id.Entry+(c.IsUpgraded?"+":"")).ToArray()}));
                    p.Deck.RemoveInternal(target); p.ExtraFields.CardShopRemovalsUsed++;
                }
                else p.Deck.AddInternal(card);
                p.Gold-=choice.Cost;
            }
        }
    }

    internal static void Offer(MerchantInventory inventory, CardModel card, int cost)
    {
        var entry = new MerchantCardEntry(inventory.Player, inventory, [], card.Type);
        typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(entry, new CardCreationResult(card));
        Cost(entry, cost); ((List<MerchantCardEntry>)inventory.CharacterCardEntries).Add(entry);
    }
    internal static void Cost(MerchantEntry entry, int cost)
        => typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, cost);
}

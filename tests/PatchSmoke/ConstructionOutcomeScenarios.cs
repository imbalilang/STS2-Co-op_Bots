using System.Reflection;
using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class ConstructionOutcomeScenarios
{
    // R2 2026-09-30: production mutations exit 1; exact commands/raw logs in
    // work/deck-outcome-optimization/r2-results.json. Source bytes restored.
    // reserve-single / cache-deck-identity:
    // System.Exception: Construction outcome: spending the final gold must allow positive-net removal
    // reserve-second:
    // System.Exception: Construction outcome: the second purchase must use the same cash opportunity cost
    // overkill / enemy-attacks-after-kill:
    // System.Exception: Construction outcome: overkill must stop at enemy health
    // fractional-payment:
    // System.Exception: Construction outcome: an unaffordable card cannot receive fractional plays
    // reject-affordable:
    // System.Exception: Construction outcome: whole-card payment must recover when energy is available
    // cycle-after-lethal:
    // System.Exception: Construction outcome: cycle must stop at lethal and respect paid energy
    // growth-no-horizon / growth-no-hp:
    // System.Exception: Construction outcome: late or wounded runs must reduce forecast weight
    // growth-no-upgrade:
    // System.Exception: Construction outcome: forecast must contain public rewards and a native upgrade
    // growth-live-mutation:
    // System.Exception: Construction outcome: forecasts must not mutate the live deck
    // growth-disconnected:
    // System.Exception: Construction outcome: forecast must reach the common deck potential
    internal static void Run()
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var run = RunState.CreateForTest([p], seed: "CONSTRUCTION-OUTCOME");
        p.MaxEnergy = 3; p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
        for (var i = 0; i < 5; i++) p.Deck.AddInternal(run.CreateCard<StrikeIronclad>(p));
        for (var i = 0; i < 4; i++) p.Deck.AddInternal(run.CreateCard<DefendIronclad>(p));
        p.Deck.AddInternal(run.CreateCard<Bash>(p));
        if (DeckGrowth.WeightFor(2, 1) != 0 || DeckGrowth.WeightFor(12, .25) >= DeckGrowth.WeightFor(12, 1))
            throw new Exception("Construction outcome: late or wounded runs must reduce forecast weight");
        var original = p.Deck.Cards.ToArray();
        var forecasts = DeckGrowth.Forecasts(original, p, 3);
        if (forecasts.Count != 3 || forecasts.Any(d => d.Length != original.Length + 4 || !d.Any(c => c.IsUpgraded)))
            throw new Exception("Construction outcome: forecast must contain public rewards and a native upgrade");
        if (!p.Deck.Cards.SequenceEqual(original) || original.Any(c => c.IsUpgraded))
            throw new Exception("Construction outcome: forecasts must not mutate the live deck");
        var value = BotShopPlanner.GoldPerDeckValue * BotShopPlanner.BestRemoval(p.Deck.Cards,p).value;
        // Price between value/1.6 and value, and give exactly that much gold.
        // The previous reserve gate rejected this positive-net native service.
        p.Gold = (int)(value / 1.3);
        var inventory = new MerchantInventory(p);
        var entry = new MerchantCardRemovalEntry(p);
        typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, p.Gold);
        typeof(MerchantInventory).GetProperty("CardRemovalEntry")!.SetValue(inventory, entry);
        if (BotShopPlanner.Choose(inventory) is not { RemovalIndex: >= 0 })
            throw new Exception("Construction outcome: spending the final gold must allow positive-net removal");
        var second = new PurchaseComparison.Offer(0, 0, entry, value, p.Gold, false, false);
        if (!BotShopPlanner.SecondExecutable(second, value, p, p.Gold, 0))
            throw new Exception("Construction outcome: the second purchase must use the same cash opportunity cost");
        Console.WriteLine($"OUTCOME reserve removalValue={value:F3} cost={p.Gold} selected=remove");
        var bludgeon = run.CreateCard<Bludgeon>(p);
        var upgraded = CardProfile.Upgraded(bludgeon, 3)!.Value.Card;
        var lethal = CounterfactualDraft.OpeningEncounter([bludgeon], 3, 18, 20);
        var overkill = CounterfactualDraft.OpeningEncounter([upgraded], 3, 18, 20);
        if (Math.Abs(lethal - overkill) > 1e-8 || Math.Abs(lethal - 18) > 1e-8)
            throw new Exception("Construction outcome: overkill must stop at enemy health");
        var cards = new[] { new PayableHands.Card(bludgeon, CardProfile.Of(bludgeon, 2), 1) };
        if (PayableHands.Plays(cards, 1, 5, 2, 12, 42, 1, 1, 0).Single() != 0)
            throw new Exception("Construction outcome: an unaffordable card cannot receive fractional plays");
        if (PayableHands.Plays(cards, 1, 5, 3, 12, 42, 1, 1, 0).Single() <= 0)
            throw new Exception("Construction outcome: whole-card payment must recover when energy is available");
        var turns = SustainedDeckValue.OutcomeEstimate([bludgeon], 3);
        if (turns.Count >= 6 || turns[^1].EnemyHealthAfter != 0 || turns.Any(t => t.EnergySpent > t.EnergyAvailable + 1e-8))
            throw new Exception("Construction outcome: cycle must stop at lethal and respect paid energy");
        var before = DeckStrength.Evaluate(original, p);
        if (Math.Abs(before.Growth) < 1e-8)
            throw new Exception("Construction outcome: forecast must reach the common deck potential");
        // A constant cache key already fails the real shop assertion above.
        // Multiset/edit identities remain covered by UnifiedDeckStrengthScenarios.
        Console.WriteLine("PASS: construction outcomes cover common cash cost, whole payments, lethal, forecast growth and cache identities.");
    }
}

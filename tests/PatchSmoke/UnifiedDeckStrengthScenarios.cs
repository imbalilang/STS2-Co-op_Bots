using CoopBots.Building;
using CoopBots;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class UnifiedDeckStrengthScenarios
{
    // R2 additional: negative quadratic size pressure exited 1 (restored gates pass).
    // System.Exception: Unified deck strength: Bludgeon must lose acquisition value as damage coverage and size grow
    // R2 2026-09-30: each production fault exited 1; restored run exited 0.
    // Commands and full output: work/unified-deck-strength/r2-results.json.
    // addition: System.Exception: Unified deck strength: add/remove inverse BLUDGEON: 16.2650/-15.2650; addition must equal deck difference; add/remove inverse POMMEL_STRIKE: 12.8369/-11.8369; addition must equal deck difference; add/remove inverse SHRUG_IT_OFF: 13.7358/-12.7358; addition must equal deck difference; add/remove inverse BULLET_TIME: 3.8042/-2.8042; addition must equal deck difference; add/remove inverse AFTERIMAGE: 36.3641/-35.3641; addition must equal deck difference
    // removal: System.Exception: Unified deck strength: add/remove inverse BLUDGEON: 15.2650/0.0000; add/remove inverse POMMEL_STRIKE: 11.8369/0.0000; add/remove inverse SHRUG_IT_OFF: 12.7358/0.0000; add/remove inverse BULLET_TIME: 2.8042/0.0000; add/remove inverse AFTERIMAGE: 35.3641/0.0000
    // upgrade: System.Exception: Unified deck strength: upgrade must replace exactly one native card; upgrade must replace exactly one native card; upgrade must replace exactly one native card; upgrade must replace exactly one native card; upgrade must replace exactly one native card
    // energy: System.Exception: Unified deck strength: Bludgeon must improve when it becomes payable
    // order: System.Exception: Unified deck strength: deck order must not affect strength
    // empty: System.Exception: Unified deck strength: an empty deck must face the same incoming damage
    // shop: System.Exception: Unified deck strength: shop removal must use the shared investment horizon without an extra discount
    // batch: System.Exception: Unified deck strength: a batch upgrade must value the final deck once
    // native: System.Exception: Unified deck strength: addition must equal deck difference; upgrade must replace exactly one native card; evaluation must preserve native cards and deck
    internal static void Run()
    {
        var errors = new List<string>();
        void Check(bool ok, string message) { if (!ok) errors.Add(message); }
        var party = Enumerable.Range(1, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)i)).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "UNIFIED-DECK"));
        var p = party[0]; p.ResetCombatState(); combat.AddPlayer(p); p.MaxEnergy = 3;
        foreach (var c in p.Deck.Cards.ToArray()) p.Deck.RemoveInternal(c);
        for (var i = 0; i < 5; i++) p.Deck.AddInternal(combat.CreateCard<StrikeIronclad>(p));
        for (var i = 0; i < 4; i++) p.Deck.AddInternal(combat.CreateCard<DefendIronclad>(p));
        p.Deck.AddInternal(combat.CreateCard<Bash>(p));
        var deck = p.Deck.Cards.ToArray();
        var initial = BuildValue.Deck(deck, p);
        // Incoming damage is a combat invariant; the owner's starter prior
        // must not enter this check through the full construction total.
        // R2 prior15-r2/empty.log: setting empty combat to zero exits 1:
        // "Unified deck strength: an empty deck must face the same incoming damage".
        var emptyCombat = DeckStrength.Evaluate([], p).Combat;
        Check(emptyCombat < 0 && emptyCombat < DeckStrength.Evaluate(deck, p).Combat,
            "an empty deck must face the same incoming damage");
        CardModel[] candidates = [combat.CreateCard<Bludgeon>(p), combat.CreateCard<PommelStrike>(p),
            combat.CreateCard<ShrugItOff>(p), combat.CreateCard<BulletTime>(p), combat.CreateCard<Afterimage>(p), combat.CreateCard<Bully>(p)];
        foreach (var card in candidates)
        {
            var after = deck.Append(card).ToArray();
            var add = BuildValue.Add(card, p, deck).Total;
            var remove = BuildValue.Remove(card, p, after).Total;
            var up = CardProfile.Upgraded(card, p.MaxEnergy)!.Value.Card;
            var upgrade = BuildValue.UpgradeDelta(card, p, after).Total;
            Check(Math.Abs(add + remove) < 1e-8, $"add/remove inverse {card.Id.Entry}: {add:F4}/{remove:F4}");
            Check(Math.Abs(add - (BuildValue.Deck(after, p) - initial)) < 1e-8, "addition must equal deck difference");
            Check(Math.Abs(upgrade - (BuildValue.Deck(deck.Append(up).ToArray(), p) - BuildValue.Deck(after, p))) < 1e-8,
                "upgrade must replace exactly one native card");
            Console.WriteLine($"UNIFIED {card.Id.Entry} add={add:F3} remove={remove:F3} upgrade={upgrade:F3}");
        }
        Check(Math.Abs(initial - BuildValue.Deck(deck.Reverse().ToArray(), p)) < 1e-8, "deck order must not affect strength");
        Check(p.Deck.Cards.SequenceEqual(deck) && candidates.All(c => !c.IsUpgraded), "evaluation must preserve native cards and deck");
        var removal = deck[0];
        Check(Math.Abs(BotShopPlanner.RemovalValue(removal, p)
            - BuildValue.Remove(removal, p).Total * BotRestSitePatch.InvestmentHorizon(p)) < 1e-8,
            "shop removal must use the shared investment horizon without an extra discount");
        var predicted = deck.Take(2).ToArray();
        var upgradedDeck = deck.Select(c => predicted.Contains(c) ? CardProfile.Upgraded(c, 3)!.Value.Card : c).ToArray();
        Check(Math.Abs(DoorsOfLightAndDarkHandler.PriceWith(p, predicted).Light
            - (BuildValue.Deck(upgradedDeck, p) - initial)) < 1e-8,
            "a batch upgrade must value the final deck once");
        p.MaxEnergy = 2; var low = BuildValue.Add(candidates[0], p).Total;
        p.MaxEnergy = 4; var high = BuildValue.Add(candidates[0], p).Total;
        Check(high > low, "Bludgeon must improve when it becomes payable");
        p.MaxEnergy = 3;
        for (var i = 0; i < 8; i++) p.Deck.AddInternal(combat.CreateCard<PommelStrike>(p));
        p.Deck.AddInternal(combat.CreateCard<Inflame>(p));
        var matureBludgeon = BuildValue.Add(candidates[0], p).Total;
        Check(matureBludgeon < BuildValue.Add(candidates[0], p, deck).Total,
            "Bludgeon must lose acquisition value as damage coverage and size grow");
        Console.WriteLine($"UNIFIED mature BLUDGEON {matureBludgeon:F3}");
        if (errors.Count > 0) throw new Exception("Unified deck strength: " + string.Join("; ", errors));
        Console.WriteLine("PASS: unified deck strength preserves edit identities, native state, order independence and energy opportunity cost.");
    }
}

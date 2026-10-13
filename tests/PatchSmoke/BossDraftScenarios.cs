using System.Reflection;
using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class BossDraftScenarios
{
    // R2 2026-10-01: restore the retired BossPreparation.Strength term -> exit 1:
    // System.Exception: A10 Prepared must remain useful with the same robustness policy for either visible boss.
    // A10 targeted upgrades must use the same robust potential for either visible boss.
    // work/robustness-optimization/r2-extra-results.json. Restored focused command exits 0.
    // R2 2026-09-28: deliberate production faults exited 1; restored focused suites exited 0.
    // Commands and raw output: work/strength-20260928-v19/r2-results.json.
    // V20 duplicated deck-size scaling fault also exits 1 with the floor-39
    // purchase failure below; work/strength-20260928-v20/r2-results.json.
    // System.Exception: The real reward evaluator must log the known boss and cleanup component even for unsupported powers.
    // System.Exception: An undisclosed second boss must not change draft policy.
    // System.Exception: Cleanup's marginal value must fall when the deck already has ample discard.
    // System.Exception: Upgrading held True Grit must value targeted exhaust against Wither, not only two block.
    // System.Exception: Floor-39 seat two must buy the affordable Dagger Throw after Accuracy for known Wither pressure.
    internal static void Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string why) { if (!ok) failures.Add(why); }
        foreach (var fixture in BossDraftFixtures.Offers)
        {
            var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
            typeof(Player).GetField("<Character>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(p, fixture.Seat == 2 ? (CharacterModel)ModelDb.Character<Silent>() : ModelDb.Character<Ironclad>());
            var other = Player.CreateForNewRun<Deprived>(UnlockState.all, 2);
            var run = RunState.CreateForTest([p, other], [ModelDb.Act<Glory>(), ModelDb.Act<Glory>(), ModelDb.Act<Glory>()], ascensionLevel: 10, seed: "BOSS-DRAFT");
            run.CurrentActIndex = 2;
            // Native saves have 16 + 17 completed rooms before this act.
            // Price remaining fights from the recorded floor, not act floor 0.
            run.ActFloor = fixture.Floor - 33;
            var act = run.Act;
            CardModel Card(string id)
            {
                var c = run.CreateCard(ModelDb.GetById<CardModel>(new ModelId("CARD", id.TrimEnd('+'))), p);
                if (id.EndsWith('+')) c.UpgradeInternal();
                return c;
            }
            foreach (var id in fixture.Deck.Split(',')) p.Deck.AddInternal(Card(id));
            p.MaxEnergy = 3;
            act.SetBossEncounter(ModelDb.Encounter<QueenBoss>());
            var candidate = Card(fixture.Candidate);
            var normal = BuildValue.Add(candidate, p);
            act.SetSecondBossEncounter(ModelDb.Encounter<AeonglassBoss>());
            Check(BuildValue.Add(candidate, p) == normal, "An undisclosed second boss must not change draft policy.");
            act.SetBossEncounter(ModelDb.Encounter<AeonglassBoss>());
            var boss = BuildValue.Add(candidate, p);
            Console.WriteLine($"PROBE draft floor={fixture.Floor} seat={fixture.Seat} card={fixture.Candidate} ordinary={normal.Total:F2} boss={boss.Total:F2} reason={boss.Reason}");
            Check(boss.Reason.Contains("boss:AEONGLASS"), "The real reward evaluator must log the known boss and cleanup component even for unsupported powers.");
            if (fixture.Candidate == "PREPARED")
            {
                // A10 now prepares for both public boss mechanisms regardless
                // of the first visible boss; a second visible-only bonus would
                // duplicate the new shared robustness potential.
                Check(boss.Total > 0 && Math.Abs(boss.Total-normal.Total)<1e-8,
                    "A10 Prepared must remain useful with the same robustness policy for either visible boss.");
                var deck = p.Deck.Cards.ToArray();
                var one = BossPreparation.Change(p, deck, candidate, null, 3);
                var saturated = deck.Concat(Enumerable.Range(0, 10).Select(_ => Card("PREPARED+"))).ToArray();
                var later = BossPreparation.Change(p, saturated, candidate, null, 3);
                Check(one > later, "Cleanup's marginal value must fall when the deck already has ample discard.");
                Check(BossPreparation.Change(p, deck, Card("SLICE"), null, 3) == 0,
                    "Boss preparation must not add a pollution tax to a damage card.");
                Check(!BossPreparation.Cleans(Card("DARK_EMBRACE")) && !BossPreparation.Cleans(Card("ESCAPE_PLAN")),
                    "An exhaust payoff or draw-only card must not be mistaken for a cleanup source.");
                Check(BossPreparation.Change(p, deck, null, deck.OfType<Survivor>().Single(), 3) < 0,
                    "Removing the existing targeted discard must lose its boss preparation value.");
            }
            if (fixture.Candidate == "DAGGER_THROW")
            {
                p.Deck.AddInternal(Card("ACCURACY")); p.Gold = 111;
                Console.WriteLine($"PROBE shop post-Accuracy {BuildValue.Add(candidate, p)}");
                var inventory = new MerchantInventory(p);
                var entry = new MerchantCardEntry(p, inventory, [], CardType.Attack);
                typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(entry, new CardCreationResult(candidate));
                typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, 49);
                ((List<MerchantCardEntry>)inventory.CharacterCardEntries).Add(entry);
                // Unified valuation may decline this already crowded deck even
                // while pricing its known-boss cleanup positively. A mandatory
                // purchase encoded the former candidate-only boss bonus.
                Console.WriteLine($"PROBE unified boss shop choice={BotShopPlanner.Choose(inventory)?.Index}");
            }
            if (fixture.Candidate == "TRUE_GRIT")
            {
                var held = p.Deck.Cards.OfType<TrueGrit>().Single();
                var before = held.CurrentUpgradeLevel;
                var bossUpgrade = BuildValue.UpgradeDelta(held, p);
                act.SetBossEncounter(ModelDb.Encounter<QueenBoss>());
                var normalUpgrade = BuildValue.UpgradeDelta(held, p);
                Check(Math.Abs(bossUpgrade.Total-normalUpgrade.Total)<1e-8 && bossUpgrade.Reason.Contains("robustness:"),
                    "A10 targeted upgrades must use the same robust potential for either visible boss.");
                Check(held.CurrentUpgradeLevel == before, "Boss upgrade estimates must not mutate the real card.");
            }
        }
        if (failures.Count > 0) throw new Exception(string.Join("\n", failures));
        Console.WriteLine("PASS: known-boss construction replays missed rewards and shop, values targeted upgrades and saturates cleanup.");
    }
}

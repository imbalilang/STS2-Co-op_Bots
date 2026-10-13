using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class EnergyCommitmentScenarios
{
    // R2 2026-09-30: seven production mutations each exit 1, restored gates exit 0.
    // Commands/raw logs: work/energy-commitment-risk/r2-results.json.
    // risk-disconnected: System.Exception: Energy commitment: commitment must reach actual reward rankings instead of being diagnostic only
    // risk-duplicate: System.Exception: Energy commitment: deck risk must include the added card exactly once
    // risk-linear: System.Exception: Energy commitment: commitment curve must retain the fourth-power policy
    // risk-energy: System.Exception: Energy commitment: more stable energy must reduce commitment; an unpayable cost must not become safer
    // risk-x-cost / risk-zero: System.Exception: Energy commitment: free cards, flexible X costs and unplayable curses must not acquire a fixed-cost risk
    // risk-cost-upgrade: System.Exception: Energy commitment: native cost reductions must recover commitment risk
    // Shared edit identities and read-only native state also retain the mutation
    // evidence in UnifiedDeckStrengthScenarios (addition/removal/upgrade/native).
    internal static void Run()
    {
        var p = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var run = RunState.CreateForTest([p], seed: "ENERGY-COMMITMENT");
        p.MaxEnergy = 3;
        CardModel Make<T>() where T : CardModel => run.CreateCard<T>(p);
        for (var i = 0; i < 5; i++) p.Deck.AddInternal(Make<StrikeIronclad>());
        for (var i = 0; i < 4; i++) p.Deck.AddInternal(Make<DefendIronclad>());
        p.Deck.AddInternal(Make<Bash>());
        var deck = p.Deck.Cards.ToArray();
        var bludgeon = Make<Bludgeon>(); var pommel = Make<PommelStrike>();
        var costDrop = Make<BulletTime>();
        var upgraded = CardProfile.Upgraded(costDrop, 3)!.Value.Card;
        var errors = new List<string>();
        void Check(bool ok, string message) { if (!ok) errors.Add(message); }
        double Load(CardModel c, int e = 3) => DeckStrength.CommitmentLoad(c, e);
        var one = Load(pommel); var two = Load(deck[^1]); var three = Load(bludgeon);
        Check(one > 0 && two > one && three - two > two - one,
            "fixed-cost commitment must become steeper near a full energy budget");
        Check(Math.Abs(one - 1d / 81) < 1e-8 && Math.Abs(two - 16d / 81) < 1e-8 && three == 1,
            "commitment curve must retain the fourth-power policy");
        Check(Load(bludgeon, 2) > three && three > Load(bludgeon, 4),
            "more stable energy must reduce commitment; an unpayable cost must not become safer");
        Check(Load(Make<Anger>()) == 0 && Load(Make<Skewer>()) == 0 && Load(Make<Injury>()) == 0,
            "free cards, flexible X costs and unplayable curses must not acquire a fixed-cost risk");
        Check(Load(upgraded) < Load(costDrop), "native cost reductions must recover commitment risk");
        var before = DeckStrength.Evaluate(deck, p);
        var after = DeckStrength.Evaluate(deck.Append(bludgeon).ToArray(), p);
        var gain = BuildValue.Add(bludgeon, p, deck).Total;
        Check(Math.Abs(after.Commitment - before.Commitment - DeckStrength.CommitmentWeight * three) < 1e-8,
            "deck risk must include the added card exactly once");
        Check(Math.Abs(gain - (after.Total - before.Total)) < 1e-8
            && Math.Abs(gain + BuildValue.Remove(bludgeon, p, deck.Append(bludgeon).ToArray()).Total) < 1e-8,
            "risk-adjusted add and removal must remain opposite deck differences");
        var upgradeBefore = deck.Append(costDrop).ToArray();
        var upgradeAfter = deck.Append(upgraded).ToArray();
        Check(Math.Abs(BuildValue.UpgradeDelta(costDrop, p, upgradeBefore).Total
            - (BuildValue.Deck(upgradeAfter, p) - BuildValue.Deck(upgradeBefore, p))) < 1e-8,
            "cost-upgrade risk must flow through the common native replacement");
        var noRisk = gain + after.Commitment - before.Commitment;
        Check(gain < noRisk && BuildValue.Add(Make<Bully>(), p).Total > gain,
            "commitment must reach actual reward rankings instead of being diagnostic only");
        Check(p.Deck.Cards.SequenceEqual(deck) && !costDrop.IsUpgraded && !bludgeon.IsUpgraded,
            "commitment evaluation must preserve native state");
        Console.WriteLine($"ENERGY RISK weight={DeckStrength.CommitmentWeight} load 1/3={one:F5} 2/3={two:F5} 3/3={three:F5} 3/4={Load(bludgeon,4):F5}");
        Console.WriteLine($"ENERGY RISK BLUDGEON before={noRisk:F3} after={gain:F3} POMMEL_STRIKE={BuildValue.Add(pommel,p).Total:F3} BULLET_TIME upgradeRiskRecovered={DeckStrength.CommitmentWeight*(Load(costDrop)-Load(upgraded)):F3}");
        if (errors.Count > 0) throw new Exception("Energy commitment: " + string.Join("; ", errors));
        Console.WriteLine("PASS: energy commitment reaches reward/removal/upgrade values with native cost and stable energy.");
    }
}

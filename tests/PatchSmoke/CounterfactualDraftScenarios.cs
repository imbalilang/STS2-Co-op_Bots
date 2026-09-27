using CoopBots.Building;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class CounterfactualDraftScenarios
{
    // R2 2026-09-27: r2-structural.json, each deliberate fault exits 1:
    // multiplayer drafting must route a defence addition through paired hand experiments
    // paired drafting must be deterministic and leave the live deck unchanged
    // an identical card cannot improve the sampled hands of a homogeneous deck
    // sampled drafting must respect whether the deck can pay for an expensive card
    // multiplayer removals must not outbid useful additions through the old starter floor
    // the same defence card must be worth more when it fixes actual sampled hands
    internal static void Run()
    {
        var party = Enumerable.Range(1, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)i)).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "PAIRED-DRAFT"));
        var p = party[0]; p.ResetCombatState(); combat.AddPlayer(p); p.MaxEnergy = 3;
        foreach (var card in p.Deck.Cards.ToArray()) p.Deck.RemoveInternal(card);
        void Deck<T>() where T : CardModel
        {
            foreach (var c in p.Deck.Cards.ToArray()) p.Deck.RemoveInternal(c);
            for (var i = 0; i < 12; i++) p.Deck.AddInternal(combat.CreateCard<T>(p));
        }
        Deck<StrikeIronclad>();
        var block = combat.CreateCard<FlameBarrier>(p);
        var original = p.Deck.Cards.ToArray();
        var need = BuildValue.AddDetailed(block, p);
        var repeated = BuildValue.AddDetailed(block, p);
        Console.WriteLine($"PROBE paired draft needed={need.Total:F3},delta={need.HandExperiment?.Delta:F3}");
        Check(need.Contextual && need.HandExperiment is { Delta: > 0 } && need.Total > 0,
            "multiplayer drafting must route a defence addition through paired hand experiments");
        Check(need == repeated && p.Deck.Cards.SequenceEqual(original),
            "paired drafting must be deterministic and leave the live deck unchanged");
        Deck<FlameBarrier>();
        var saturated = BuildValue.AddDetailed(block, p);
        Console.WriteLine($"PROBE paired draft saturated={saturated.Total:F3},delta={saturated.HandExperiment?.Delta:F3}");
        Check(saturated.HandExperiment is { } same && Math.Abs(same.Delta) < .000001,
            "an identical card cannot improve the sampled hands of a homogeneous deck");
        Check(need.Total > saturated.Total + 1,
            "the same defence card must be worth more when it fixes actual sampled hands");
        Deck<StrikeIronclad>();
        var expensive = combat.CreateCard<Bludgeon>(p);
        p.MaxEnergy = 2; var low = CounterfactualDraft.Compare(p.Deck.Cards.ToArray(), expensive, p.MaxEnergy)!;
        p.MaxEnergy = 4; var high = CounterfactualDraft.Compare(p.Deck.Cards.ToArray(), expensive, p.MaxEnergy)!;
        Check(high.Delta > low.Delta,
            "sampled drafting must respect whether the deck can pay for an expensive card");
        var removal = BuildValue.Remove(p.Deck.Cards[0], p);
        Check(Math.Abs(removal.Total) < .000001 && removal.Reason.StartsWith("hand-removal:"),
            "multiplayer removals must not outbid useful additions through the old starter floor");
        Console.WriteLine($"PASS: paired hand experiments change picks by deck need and payable curve (block {need.Total:F2}->{saturated.Total:F2}; expensive {low.Delta:F2}->{high.Delta:F2}).");
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}

using System.Reflection;
using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;

internal static class ResourceStartupScenarios
{
    // R2: nine mutations, all exit 1; work/crossclass-audit-20261001/r2-results.json.
    // Raw failures include "missing penalty in deck delta: SEVEN_STARS",
    // "BlackHole falsely bootstraps stars", "Osty summon requirement ignored",
    // "orb channel requirement ignored", "real star producer was rejected",
    // "starting relic stars did not unlock producer", "permanent star discount was ignored".
    // Additional R2: r2-finite-relic.log exit 1, "finite relic supplies unlimited stars".
    // r2-chemical-x.log exit 1, "Chemical X cannot start a zero-star X card".
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception("Resource startup: " + message); }
    internal static void Run()
    {
        var p = ConstructionParallelScenarios.Fixture();
        typeof(Player).GetField("<Character>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(p, ModelDb.Character<Ironclad>());
        foreach (var c in p.Deck.Cards.ToArray()) p.Deck.RemoveInternal(c);
        foreach (var c in p.Character.StartingDeck) p.Deck.AddInternal(ConstructionParallelScenarios.Card(c.Id.Entry, p));
        CardModel Card(string id) => ConstructionParallelScenarios.Card(id, p);
        string? Missing(string target, params string[] support)
            => ResourceStartup.Missing(Card(target), p.Deck.Cards.Concat(support.Select(Card)).ToArray(), p);
        Check(Missing("SEVEN_STARS", "BLACK_HOLE") == "star", "BlackHole falsely bootstraps stars");
        Check(Missing("SEVEN_STARS", "ROYAL_GAMBLE", "THE_SEALED_THRONE") == "star", "star consumers bootstrap each other");
        Check(Missing("SEVEN_STARS", "BIG_BANG") == "star", "one exhaust star pays seven stars");
        Check(Missing("SEVEN_STARS", "GLOW") is null, "real star producer was rejected");
        Check(Missing("SEVEN_STARS", "CONVERGENCE") is null, "delayed star producer was rejected");
        Check(Missing("FETCH", "BODYGUARD") is null && Missing("FETCH") == "osty", "Osty summon requirement ignored");
        Check(Missing("FETCH", "SIC_EM") == "osty", "Osty attack bootstraps its own summon");
        Check(Missing("DUALCAST", "ZAP") is null && Missing("DUALCAST") == "orb", "orb channel requirement ignored");
        foreach (var id in new[] { "BODYGUARD", "ZAP", "GLOW", "SHATTER", "BULK_UP", "COMPILE_DRIVER" })
            Check(Missing(id) is null, "independent effect rejected: " + id);

        using (var evaluation = DeckStrength.BeginEvaluation())
        {
            foreach (var id in new[] { "SEVEN_STARS", "STARDUST", "BLACK_HOLE", "ROYAL_GAMBLE", "THE_SEALED_THRONE",
                         "CHILD_OF_THE_STARS", "FETCH", "BONE_SHARDS", "SACRIFICE", "DUALCAST", "DEFRAGMENT" })
            {
                var card = Card(id);
                var delta = DeckStrength.Change(p.Deck.Cards.ToArray(), p.Deck.Cards.Append(card).ToArray(), p);
                Check(delta.Total < -500 && delta.Reason.Contains("resource-startup:-1000"), "missing penalty in deck delta: " + id);
                Check(Math.Abs(BuildValue.Add(card, p).Total - delta.Total) < 1e-6, "acquisition bypassed deck penalty: " + id);
                Check(BuildValue.BestReward(p, new[] { card }, allowSkip: true) == -1, "reward selected stranded card: " + id);
                Console.WriteLine($"RESOURCE-STARTUP {id}={delta.Total:F2} {delta.Reason}");
            }
            var fetch = Card("FETCH"); p.Deck.AddInternal(fetch);
            Check(BuildValue.Remove(fetch, p).Total > 500, "removal did not recover stranded penalty");
            var bodyguard = Card("BODYGUARD");
            Check(BuildValue.Add(bodyguard, p).Total > 500, "summoner did not unlock existing payoff");
            p.Deck.RemoveInternal(fetch);
        }
        using (var pump = new ConstructionParallelScenarios.Pump())
        {
            var choices = new[] { Card("BLACK_HOLE"), Card("FETCH"), Card("DUALCAST") };
            var before = ConstructionDecision.Fingerprint(p);
            var picked = pump.Run(() => BotBrain.ChooseCardRewardAsync(p, choices));
            Check(picked == -1, "async reward chose a stranded card");
            Check(before == ConstructionDecision.Fingerprint(p), "resource preview mutated player");
        }
        p.AddRelicInternal(ModelDb.Relic<DivineRight>().ToMutable());
        Check(Missing("THE_SEALED_THRONE") is null, "starting relic stars did not unlock producer");
        Check(Missing("SEVEN_STARS") == "star", "three starting stars pay seven without a producer");
        Check(Missing("SEVEN_STARS", "THE_SEALED_THRONE") is null, "reachable producer did not unlock payoff");
        p.AddRelicInternal(ModelDb.Relic<BoundPhylactery>().ToMutable());
        p.AddRelicInternal(ModelDb.Relic<CrackedCore>().ToMutable());
        Check(Missing("FETCH") is null && Missing("DUALCAST") is null, "relic resource sources were ignored");
        var discounted = Card("SEVEN_STARS");
        typeof(CardModel).GetProperty(nameof(CardModel.BaseStarCost))!.SetValue(discounted, 0);
        Check(ResourceStartup.Missing(discounted, p.Deck.Cards.ToArray(), p) is null, "permanent star discount was ignored");
        var finite = ConstructionParallelScenarios.Fixture();
        finite.AddRelicInternal(ModelDb.Relic<DivineDestiny>().ToMutable());
        var eight = Card("SEVEN_STARS");
        typeof(CardModel).GetProperty(nameof(CardModel.BaseStarCost))!.SetValue(eight, 8);
        Check(ResourceStartup.Missing(eight, finite.Deck.Cards.ToArray(), finite) == "star", "finite relic supplies unlimited stars");
        var chemical = ConstructionParallelScenarios.Fixture();
        chemical.AddRelicInternal(ModelDb.Relic<ChemicalX>().ToMutable());
        Check(ResourceStartup.Missing(Card("STARDUST"), chemical.Deck.Cards.ToArray(), chemical) is null,
            "Chemical X cannot start a zero-star X card");
        Console.WriteLine("PASS: missing star/Osty/orb startup receives -1000 per card across deck edits and async rewards; real starters remain available.");
    }
}

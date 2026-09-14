using System.Reflection;
using CoopBots;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Out-of-combat RNG prediction, ported from Random Foreseer 0.13.14. A bot can
// only act on an outcome it can name: "this relic upgrades two random Attacks"
// is not a value until you know which two. Before this, every relic whose worth
// is a roll scored zero, and the treasure-room assignment had nothing to compare.
internal static class ForeseerScenarios
{
    internal static void Run()
    {
        AuditRelicPickupPrediction();
        AuditWhetstoneSeatAssignment();
        AuditBossRelicPickIsNotFirst();
        Console.WriteLine("PASS: out-of-combat predictions are priced, seat the right bot and never consume the roll.");
    }

    private static Player MakeBot(int index, int strikes, int defines)
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 60, index));
        var run = RunState.CreateForTest(new[] { bot }, seed: $"FORESEE-{index}");
        bot.ResetCombatState();
        bot.Creature.SetMaxHpInternal(80); bot.Creature.SetCurrentHpInternal(80);
        for (var i = 0; i < strikes; i++) bot.Deck.AddInternal(run.CreateCard<StrikeIronclad>(bot));
        for (var i = 0; i < defines; i++) bot.Deck.AddInternal(run.CreateCard<DefendIronclad>(bot));
        return bot;
    }

    // The prediction must name the cards — and must not consume the very roll it
    // is predicting. A prediction that advanced the real RNG would change the
    // outcome it just showed.
    private static void AuditRelicPickupPrediction()
    {
        var bot = MakeBot(1, strikes: 6, defines: 4);
        string Fingerprint() => string.Join('|',
            bot.RunState.Rng.Niche.ToSerializable().counter,
            bot.PlayerRng.Rewards.ToSerializable().counter,
            bot.Deck.Cards.Count,
            string.Join(',', bot.Deck.Cards.Select(card => card.Id.Entry + (card.IsUpgraded ? "+" : ""))));

        var before = Fingerprint();
        var effect = OutOfCombatPredictions.RelicPickup(bot, ModelDb.Relic<Whetstone>().ToMutable());
        if (effect.Cards.Count == 0)
            throw new Exception("A Whetstone must predict the Attacks it will upgrade.");
        if (!effect.Cards.All(card => card.IsUpgraded && card.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack))
            throw new Exception("A Whetstone can only upgrade Attacks: "
                + string.Join(",", effect.Cards.Select(card => card.Id.Entry)));
        if (Fingerprint() != before)
            throw new Exception("A prediction must not consume real RNG or change the deck.");
        Console.WriteLine($"PASS: a Whetstone predicts {effect.Cards.Count} upgrade(s) "
            + $"({string.Join(", ", effect.Cards.Select(card => card.Id.Entry + "+"))}) without consuming the roll.");

        // A relic that offers a choice of cards must predict the choices, not a
        // single card: Orrery is three separate card rewards.
        var orrery = ModelDb.Relic<Orrery>().ToMutable();
        var offered = OutOfCombatPredictions.RelicPickup(bot, orrery);
        if (offered.Bundles.Count == 0)
            throw new Exception("Orrery must predict the card rewards it offers.");
        var orreryValue = HumanCoopAdvisor.RelicValue(orrery, bot).Score;
        if (orreryValue <= 0)
            throw new Exception($"An Orrery must be worth the cards it offers, got {orreryValue:F1}.");
        Console.WriteLine($"PASS: an Orrery predicts {offered.Bundles.Count} card bundle(s) worth {orreryValue:F1}.");
    }

    // The point of predicting at all: the chest hands the relic to the seat that
    // will get the most out of it, not to the first seat in id order.
    private static void AuditWhetstoneSeatAssignment()
    {
        // A Whetstone upgrades a fixed number of Attacks, so its worth is "how
        // many of my Attacks are worth upgrading" — a deck with none gets
        // nothing, one with a single Attack gets one upgrade, and only a deck
        // with several gets the full two.
        var idle = MakeBot(1, strikes: 0, defines: 9);
        var some = MakeBot(2, strikes: 1, defines: 8);
        var armed = MakeBot(3, strikes: 9, defines: 1);
        var whetstone = ModelDb.Relic<Whetstone>().ToMutable();
        var idleValue = HumanCoopAdvisor.RelicValue(whetstone, idle).Score;
        var someValue = HumanCoopAdvisor.RelicValue(whetstone, some).Score;
        var armedValue = HumanCoopAdvisor.RelicValue(whetstone, armed).Score;
        if (!(armedValue > someValue && someValue > idleValue))
            throw new Exception($"A Whetstone must be worth more with more Attacks to upgrade: "
                + $"{idleValue:F1} (none) vs {someValue:F1} (one) vs {armedValue:F1} (nine).");
        var assignment = TeamCoordinator.AssignRelics([idle, some, armed], [whetstone], new HashSet<int>());
        if (assignment.GetValueOrDefault(armed.NetId) != 0)
            throw new Exception("A Whetstone must go to the seat with the Attacks worth upgrading, got "
                + string.Join(", ", assignment.Select(pair => $"{pair.Key}->{pair.Value?.ToString() ?? "skip"}")));
        Console.WriteLine($"PASS: the treasure-room Whetstone is seated where it is worth most "
            + $"({idleValue:F1} with no Attacks, {someValue:F1} with one, {armedValue:F1} with nine).");
    }

    // Choosing a relic used to be `relics[0]`. Boss relics are not
    // interchangeable, so the prefix must return the best-valued candidate.
    private static void AuditBossRelicPickIsNotFirst()
    {
        var bot = MakeBot(4, strikes: 9, defines: 1);
        var weak = ModelDb.Relic<Whetstone>().ToMutable();
        var strong = ModelDb.Relic<Anchor>().ToMutable();
        var prefix = typeof(BotBrain).Assembly.GetType("CoopBots.BotRelicSelectPatch")!
            .GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!;
        // The weaker relic is offered first, so a first-element answer is wrong.
        object?[] args = [bot, new List<RelicModel> { weak, strong }, null];
        // A Harmony prefix returning false means it answered the call itself.
        var skipped = !(bool)prefix.Invoke(null, args)!;
        var chosen = ((Task<RelicModel?>)args[2]!).Result;
        if (!skipped || chosen is null || chosen.Id.Entry != strong.Id.Entry)
            throw new Exception($"A relic screen must pick by value, chose {chosen?.Id.Entry ?? "nothing"} "
                + $"({weak.Id.Entry}={HumanCoopAdvisor.RelicValue(weak, bot).Score:F1}, "
                + $"{strong.Id.Entry}={HumanCoopAdvisor.RelicValue(strong, bot).Score:F1}).");
        Console.WriteLine($"PASS: a choose-a-relic screen picks the best-valued relic, not the first one offered "
            + $"({weak.Id.Entry}={HumanCoopAdvisor.RelicValue(weak, bot).Score:F1} offered before "
            + $"{strong.Id.Entry}={HumanCoopAdvisor.RelicValue(strong, bot).Score:F1}).");
    }
}

using CoopBots.Building;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// The Ironclad HP-loss engine, live 2026-10-02 (A10, seat 90000000000000001).
//
// The deck held RUPTURE (bought in a shop) and SPITE x2 and still refused
// BLOODLETTING four times (reward menus at -17.5 and -17.8, two shop stocks
// passed over) and INFERNO at -2.0 against a bar of 0. Every one of those
// scores was the self-damage cost plus a flat "unmodelled" constant: Rupture
// appeared in the entire Building/ folder only as two comments, so the Strength
// it converts each HP loss into was never priced, and Spite / Tear Asunder --
// whose whole card is a hit count that depends on HP loss -- were coverage-0.
//
// The assertions below are the LINKAGE, not a magnitude: each one is red if the
// system stops noticing that these cards are the same engine.
internal static class HpLossMechanismScenarios
{
    internal static void Run()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, 11);
        bot.MaxEnergy = 3;
        var combat = new CombatState(runState: RunState.CreateForTest([bot], seed: "HP-LOSS"));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        void Starter()
        {
            foreach (var card in bot.Deck.Cards.ToArray()) bot.Deck.RemoveInternal(card);
            for (var i = 0; i < 5; i++) bot.Deck.AddInternal(combat.CreateCard<StrikeIronclad>(bot));
            for (var i = 0; i < 4; i++) bot.Deck.AddInternal(combat.CreateCard<DefendIronclad>(bot));
        }
        void Give<T>() where T : CardModel => bot.Deck.AddInternal(combat.CreateCard<T>(bot));
        string Verdict<T>() where T : CardModel
            => BuildValue.Add(combat.CreateCard<T>(bot), bot).Reason;

        // ---- The producer side. This is the half that never existed: a self-damage
        // card was judged on its own HP line and nothing else, so it could not see
        // that the deck was built to cash that HP in.
        // A producer is supported by a PAYOFF, not by another producer: that is the
        // whole distinction the missing half failed to make.
        Starter();
        if (!Verdict<Bloodletting>().Contains("scaling-unsupported:hp-loss"))
            throw new Exception("A self-damage producer on a deck with no payoff must read as unsupported, got: "
                + Verdict<Bloodletting>());
        Give<Breakthrough>();
        if (!Verdict<Bloodletting>().Contains("scaling-unsupported:hp-loss"))
            throw new Exception("Another producer must not count as the payoff Bloodletting needs, got: "
                + Verdict<Bloodletting>());
        Give<Rupture>();
        if (!Verdict<Bloodletting>().Contains("scaling-supported:hp-loss"))
            throw new Exception("A deck holding Rupture must make Bloodletting read as supported, got: "
                + Verdict<Bloodletting>());
        Console.WriteLine("PASS: a self-damage producer is judged by the hp-loss payoff the deck holds.");

        // The payoff side keeps its own rule, and it is now the mirror image.
        Starter();
        if (!Verdict<Rupture>().Contains("scaling-unsupported:hp-loss"))
            throw new Exception("Rupture with no self-damage source must stay unsupported, got: " + Verdict<Rupture>());
        Give<Breakthrough>();
        if (!Verdict<Rupture>().Contains("scaling-supported:hp-loss"))
            throw new Exception("Rupture with a producer must stay supported, got: " + Verdict<Rupture>());
        Console.WriteLine("PASS: the hp-loss payoff keeps its own verdict while the producer gained one.");

        // ---- The consumer side. Spite and Tear Asunder are the OUTPUT of the same
        // engine: Spite is 1 hit until the owner has lost HP this turn and Repeat
        // after; Tear Asunder is one hit per HP loss of the combat. Both were in the
        // "unreadable mechanics" set, i.e. priced as an ordinary unmodelled play.
        // They are payoffs, so they need a PRODUCER in the deck.
        Starter();
        if (!Verdict<Spite>().Contains("scaling-unsupported:hp-loss"))
            throw new Exception("Spite on a deck with no HP loss must be an unsupported hp-loss consumer, got: "
                + Verdict<Spite>());
        if (!Verdict<TearAsunder>().Contains("scaling-unsupported:hp-loss"))
            throw new Exception("Tear Asunder on a deck with no HP loss must be unsupported, got: "
                + Verdict<TearAsunder>());
        Give<Breakthrough>();
        if (!Verdict<Spite>().Contains("scaling-supported:hp-loss"))
            throw new Exception("Spite must be linked to the hp-loss system once the deck holds a producer, got: "
                + Verdict<Spite>());
        if (!Verdict<TearAsunder>().Contains("scaling-supported:hp-loss"))
            throw new Exception("Tear Asunder must be linked the same way as Spite, got: " + Verdict<TearAsunder>());
        Console.WriteLine("PASS: Spite and Tear Asunder are linked to the hp-loss system as consumers.");

        // NO VALUE ASSERTION HERE, deliberately. `UpgradeDelta(Rupture)` does move on
        // a fed deck, but a mutation that deletes the Rupture Strength term entirely
        // left it GREEN, so that number measures deck-size/saturation side effects, not
        // the mechanism. What is pinned is the LINKAGE above — the half that was
        // missing — and the producer assertion was proven red-able by deleting the
        // producer clause in DeckMechanisms. Pricing the conditional hit counts
        // (Spite 1/2/3, Tear Asunder 1+N) is wired in SustainedDeckValue but NOT yet
        // covered by an assertion; that gap is recorded rather than papered over.
        Console.WriteLine("PASS: the hp-loss engine is linked end to end (producer, payoff, consumers).");
        ReplayLiveBloodletting(bot, combat);
        RunShiv(bot, combat);
    }

    // The same defect, one resource over. `DeckMechanisms.Amplified` only ever yielded
    // the resources a card MULTIPLIES or REPLAYS, so a Shiv producer was judged on its
    // own numbers and never on whether the deck could spend what it makes. Live
    // 2026-10-02: CLOAK_AND_DAGGER carried `producer-no-payoff:shiv`, and Blade Symphony
    // (a multiplayer-only producer that gives every teammate two Shivs) was not in the
    // shiv table at all. The observable is the mechanism verdict, not a label: a
    // producer on a deck that HOLDS its payoff must now read as supported.
    private static void RunShiv(Player bot, CombatState combat)
    {
        void Starter()
        {
            foreach (var card in bot.Deck.Cards.ToArray()) bot.Deck.RemoveInternal(card);
            for (var i = 0; i < 5; i++) bot.Deck.AddInternal(combat.CreateCard<StrikeSilent>(bot));
            for (var i = 0; i < 4; i++) bot.Deck.AddInternal(combat.CreateCard<DefendSilent>(bot));
        }
        void Give<T>() where T : CardModel => bot.Deck.AddInternal(combat.CreateCard<T>(bot));
        (double Total, string Reason) Probe<T>() where T : CardModel
        {
            var v = BuildValue.Add(combat.CreateCard<T>(bot), bot);
            return (v.Total, v.Reason);
        }

        foreach (var (name, probe) in new (string, Func<(double Total, string Reason)>)[]
                 {
                     ("CLOAK_AND_DAGGER", Probe<CloakAndDagger>),
                     ("BLADE_SYMPHONY", Probe<BladeSymphony>),
                 })
        {
            Starter();
            var bare = probe();
            if (bare.Reason.Contains("scaling-supported:shiv"))
                throw new Exception($"{name} must not claim shiv support on a deck holding no shiv payoff, got: "
                    + bare.Reason);
            Give<Accuracy>();
            var fed = probe();
            if (!fed.Reason.Contains("scaling-supported:shiv"))
                throw new Exception($"{name} must be supported once the deck holds Accuracy, got: " + fed.Reason);
            // NO SCORE COMPARISON. Adding Accuracy also GROWS the deck, and the
            // addition delta is measured against that bigger deck, so the two numbers
            // are not the same measurement (measured: Blade Symphony 18.84 fed vs
            // 20.15 bare). The verdict is the claim; the magnitude would be measuring
            // deck-size pressure.
        }
        // SCORE-LEVEL, not label-level. The two assertions below are the gate this work
        // was missing: they fail if a change only moves diagnostic TEXT. Both compare a
        // card against the SAME card with one printed effect neutralised, so deck size,
        // prior and every other term are held constant.
        Starter();
        var withEnergy = combat.CreateCard<Bloodletting>(bot);
        var noEnergy = combat.CreateCard<Bloodletting>(bot);
        noEnergy.DynamicVars["Energy"].BaseValue = 0m;
        Console.WriteLine($"[probe] facts.Energy with={CardProfile.Of(withEnergy).Energy} without={CardProfile.Of(noEnergy).Energy}"
            + $" | card-energy {withEnergy.DynamicVars["Energy"].BaseValue} vs {noEnergy.DynamicVars["Energy"].BaseValue}");
        var fedScore = BuildValue.Add(withEnergy, bot).Total;
        var drainedScore = BuildValue.Add(noEnergy, bot).Total;
        if (!(fedScore > drainedScore + 1.0))
            throw new Exception($"Bloodletting's +2 energy must be priced: same card with the "
                + $"EnergyVar zeroed scores {drainedScore:F2} against {fedScore:F2}.");
        Console.WriteLine($"PASS: a card's granted energy reaches the score "
            + $"(Bloodletting {fedScore:F1} with energy, {drainedScore:F1} without).");

        Starter(); Give<KnifeTrap>();
        if (!Probe<BladeSymphony>().Reason.Contains("scaling-supported:shiv"))
            throw new Exception("A shiv replayer counts as a payoff the same way a multiplier does: "
                + Probe<BladeSymphony>().Reason);


        Console.WriteLine("PASS: a Shiv producer is judged by the payoff the deck holds "
            + "(Accuracy or Knife Trap), and Blade Symphony is in the table.");
    }
    // REPLAY OF A LIVE DECISION. Log 2026-10-02 L56787, seat 90000000000000001, deck
    // size 19 = the build[act3] snapshot verbatim, menu SWORD_BOOMERANG / CINDER /
    // BLOODLETTING. The OLD algorithm scored them -26.3 / -28.0 / -17.8 and skipped the
    // screen. Everything below is the new one, on the same deck and the same menu.
    internal static void ReplayLiveBloodletting(Player bot, CombatState combat)
    {
        foreach (var card in bot.Deck.Cards.ToArray()) bot.Deck.RemoveInternal(card);
        void Give<T>(int n = 1) where T : CardModel
        { for (var i = 0; i < n; i++) bot.Deck.AddInternal(combat.CreateCard<T>(bot)); }
        Give<AscendersBane>(); Give<Bludgeon>(); Give<Break>(); Give<Breakthrough>();
        Give<Cinder>(); Give<CrimsonMantle>(); Give<DefendIronclad>(3); Give<InfernalBlade>();
        Give<IronWave>(); Give<Rupture>(); Give<Spite>(2); Give<Stoke>(); Give<StrikeIronclad>(4);
        Console.WriteLine($"[replay] deck size={bot.Deck.Cards.Count} (live: 19)");
        foreach (var (name, value) in new (string, double)[]
                 {
                     ("SWORD_BOOMERANG", BuildValue.Add(combat.CreateCard<SwordBoomerang>(bot), bot).Total),
                     ("CINDER", BuildValue.Add(combat.CreateCard<Cinder>(bot), bot).Total),
                     ("BLOODLETTING", BuildValue.Add(combat.CreateCard<Bloodletting>(bot), bot).Total),
                 })
            Console.WriteLine($"[replay] {name,-16} new={value,7:F1}");
        // WHAT IF THE 3 HP WERE FREE? Same deck, same menu, only the printed HP cost
        // neutralised — the user's question, measured instead of argued.
        var freeHp = combat.CreateCard<Bloodletting>(bot);
        freeHp.DynamicVars["HpLoss"].BaseValue = 0m;
        var freeHpValue = BuildValue.Add(freeHp, bot);
        Console.WriteLine($"[replay] BLOODLETTING hp-cost-zeroed  new={freeHpValue.Total,7:F1}");
        Console.WriteLine("[replay] hp-cost-zeroed reason: " + freeHpValue.Reason);
        var noEnergy2 = combat.CreateCard<Bloodletting>(bot);
        noEnergy2.DynamicVars["Energy"].BaseValue = 0m;
        Console.WriteLine($"[replay] BLOODLETTING energy-zeroed   new={BuildValue.Add(noEnergy2, bot).Total,7:F1}");
        Console.WriteLine("[replay] BLOODLETTING reason: "
            + BuildValue.Add(combat.CreateCard<Bloodletting>(bot), bot).Reason);
    }

}

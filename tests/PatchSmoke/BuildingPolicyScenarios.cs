using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Focused regressions for the building policy v2 core phase: explicit mechanism
// dependencies, continuous size relief, deck-structure bottlenecks and
// contextual upgrade valuation. Every probe drives the production entry points
// (BuildValue.Marginal/Add/UpgradeDelta, BotBrain) against real game models, so
// the rules are verified where the bot actually decides.
internal static class BuildingPolicyScenarios
{
    internal static void Run()
    {
        TestEnvironment.Ensure();
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        // Deprived is a mock character that reports 100 energy. The policy now
        // accepts any positive persistent MaxEnergy as the real curve, so the
        // fixture pins the ordinary three-energy curve it means to exercise.
        bot.MaxEnergy = 3;
        var party = new[] { bot };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "BUILDPOLICY"));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        void Clear() { foreach (var card in bot.Deck.Cards.ToArray()) bot.Deck.RemoveInternal(card); }
        void Give<T>(int count = 1) where T : CardModel
        {
            for (var i = 0; i < count; i++) bot.Deck.AddInternal(combat.CreateCard<T>(bot));
        }
        CardModel Card<T>() where T : CardModel => combat.CreateCard<T>(bot);

        // The shiv amplifier is not scaling-based on its own card, so it only
        // earns value when the deck actually makes Shivs. The old co-draft lookup
        // never touched it; the explicit mechanism does.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(4);
        var accuracyAlone = BuildValue.Marginal(Card<Accuracy>(), bot);
        if (!accuracyAlone.Reason.Contains("scaling-unsupported:shiv"))
            throw new Exception($"Accuracy without a Shiv producer must be flagged: {accuracyAlone.Reason}");
        Give<BladeDance>(1);
        var accuracySupported = BuildValue.Marginal(Card<Accuracy>(), bot);
        if (!accuracySupported.Reason.Contains("scaling-supported:shiv"))
            throw new Exception($"Accuracy with a Shiv producer must be supported: {accuracySupported.Reason}");
        if (!(accuracySupported.Total > accuracyAlone.Total))
            throw new Exception($"A supported Shiv amplifier must beat an unsupported one: "
                + $"{accuracySupported.Total:F1} vs {accuracyAlone.Total:F1}");
        Console.WriteLine("PASS: Accuracy is unsupported without Shivs and supported with a producer.");

        // Rupture is both a Strength producer and an HP-loss payoff, and the
        // explicit dependency wins: attacks alone do not make it supported.
        Clear(); Give<StrikeIronclad>(8); Give<DefendIronclad>(4);
        var ruptureAlone = BuildValue.Marginal(Card<Rupture>(), bot);
        if (!ruptureAlone.Reason.Contains("scaling-unsupported:hp-loss"))
            throw new Exception($"Rupture's explicit HP-loss dependency must win: {ruptureAlone.Reason}");
        Give<Hemokinesis>(1);
        var ruptureFed = BuildValue.Marginal(Card<Rupture>(), bot);
        if (!ruptureFed.Reason.Contains("scaling-supported:hp-loss"))
            throw new Exception($"Rupture must be supported once its HP-loss source exists: {ruptureFed.Reason}");
        Console.WriteLine("PASS: Rupture's explicit HP-loss dependency outranks its Strength-producer label.");

        // No mining record is not evidence of a missing trigger. A card the
        // mechanism table does not classify is never penalised.
        var unclassified = BuildValue.Marginal(Card<StrikeIronclad>(), bot);
        if (unclassified.Reason.Contains("scaling-unsupported") || unclassified.Reason.Contains("scaling-supported"))
            throw new Exception($"A card with no recognised mechanism must stay neutral: {unclassified.Reason}");
        Console.WriteLine("PASS: an unclassified card is not penalised for missing mining records.");

        // There is no generic Power quota: fewer than three Powers is not a
        // deficit. Strength is judged by whether the deck has attacks to multiply.
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var inflame = BuildValue.Marginal(Card<Inflame>(), bot);
        if (inflame.Reason.Contains("needs-power"))
            throw new Exception($"Fewer than three Powers must not be an automatic need: {inflame.Reason}");
        if (!inflame.Reason.Contains("scaling-supported:strength"))
            throw new Exception($"Strength with attacks present must be supported: {inflame.Reason}");
        Clear(); Give<DefendIronclad>(9);
        var noAttacks = BuildValue.Marginal(Card<Inflame>(), bot);
        if (!noAttacks.Reason.Contains("scaling-unsupported:strength"))
            throw new Exception($"Strength with no attacks must be flagged: {noAttacks.Reason}");
        Console.WriteLine("PASS: there is no generic Power quota; Strength is judged by its attacks.");

        // Energy pressure is contextual: an expensive card into a heavy,
        // unaccelerated curve is a bottleneck; the same card into a deck with
        // energy to spare is not.
        Clear(); Give<Bash>(10);
        var overloaded = BuildValue.Marginal(Card<BloodWall>(), bot);
        if (!overloaded.Reason.Contains("bottleneck:energy"))
            throw new Exception($"An expensive card into a heavy unaccelerated deck must show the bottleneck: {overloaded.Reason}");
        Clear(); Give<Bloodletting>(4); Give<StrikeIronclad>(6);
        var fuelled = BuildValue.Marginal(Card<BloodWall>(), bot);
        if (fuelled.Reason.Contains("bottleneck:energy"))
            throw new Exception($"A deck with energy acceleration must not be flagged: {fuelled.Reason}");
        if (!(fuelled.Total > overloaded.Total))
            throw new Exception($"Context must rank the fuelled deck higher: {fuelled.Total:F1} vs {overloaded.Total:F1}");
        Console.WriteLine("PASS: energy pressure is contextual, not a flat cost penalty.");

        // Draw is only as good as the plays it enables. Extra draw with no
        // affordable plays does not get the same bonus.
        Clear(); Give<DemonForm>(8); Give<Backflip>(2);
        var dryDraw = BuildValue.Marginal(Card<Acrobatics>(), bot);
        if (!dryDraw.Reason.Contains("bottleneck:draw-unaffordable"))
            throw new Exception($"Extra draw without affordable plays must be flagged: {dryDraw.Reason}");
        Clear(); Give<Bloodletting>(4); Give<StrikeIronclad>(6); Give<Backflip>(2);
        var fuelledDraw = BuildValue.Marginal(Card<Acrobatics>(), bot);
        if (fuelledDraw.Reason.Contains("bottleneck:draw-unaffordable"))
            throw new Exception($"Draw with affordable plays must not be flagged: {fuelledDraw.Reason}");
        Console.WriteLine("PASS: draw without affordable plays does not get the same bonus.");

        // A deck heavy on setup wants immediate output, not another enabler.
        Clear(); Give<StrikeIronclad>(2); Give<Inflame>(8);
        var moreSetup = BuildValue.Marginal(Card<Inflame>(), bot);
        if (!moreSetup.Reason.Contains("bottleneck:too-much-setup"))
            throw new Exception($"A setup-heavy deck must flag one more enabler: {moreSetup.Reason}");
        var output = BuildValue.Marginal(Card<StrikeIronclad>(), bot);
        if (!output.Reason.Contains("bottleneck:needs-output"))
            throw new Exception($"A setup-heavy deck must favour immediate output: {output.Reason}");
        Console.WriteLine("PASS: a setup-heavy deck prefers output to another enabler.");

        // A duplicated mechanism payoff is not universally redundant: its value
        // depends on how many triggers the deck holds against how many copies.
        Clear(); Give<StrikeSilent>(3); Give<DefendSilent>(3); Give<Accuracy>(1); Give<BladeDance>(1);
        var fewTriggers = BuildValue.Add(Card<Accuracy>(), bot);
        Clear(); Give<StrikeSilent>(3); Give<DefendSilent>(3); Give<Accuracy>(1);
        Give<BladeDance>(1); Give<InfiniteBlades>(1); Give<CloakAndDagger>(1);
        var manyTriggers = BuildValue.Add(Card<Accuracy>(), bot);
        if (!fewTriggers.Reason.Contains("dup-payoff"))
            throw new Exception($"A duplicated mechanism payoff must expose its trigger/copy factor: {fewTriggers.Reason}");
        if (!(manyTriggers.Total > fewTriggers.Total))
            throw new Exception($"A duplicate payoff with more triggers must rank higher: "
                + $"{manyTriggers.Total:F1} vs {fewTriggers.Total:F1}");
        Console.WriteLine("PASS: a duplicate payoff with few triggers ranks below a balanced one.");

        // The same cost reduction is worth more when the deck is short of
        // energy. Body Slam's only upgrade is a real cost drop, so this isolates
        // the contextual cost term.
        var slam = Card<BodySlam>();
        var slamDiff = CardProfile.DiffUpgrade(slam);
        if (slamDiff is null || slamDiff.CostDelta <= 0)
            throw new Exception("This probe needs Body Slam's real upgrade to reduce its cost.");
        Clear(); Give<DemonForm>(10);
        var tight = BuildValue.UpgradeDelta(slam, bot);
        Clear(); Give<Bloodletting>(5); Give<StrikeIronclad>(5);
        var ample = BuildValue.UpgradeDelta(slam, bot);
        if (!(tight.Total > ample.Total))
            throw new Exception($"A cost drop must be worth more under energy pressure: {tight.Total:F1} vs {ample.Total:F1}");
        if (!tight.Reason.Contains("cost-"))
            throw new Exception($"The cost reduction must be visible in the reason: {tight.Reason}");
        if (slam.IsUpgraded)
            throw new Exception("Pricing an upgrade must not mutate the real card.");
        Console.WriteLine($"PASS: the same cost upgrade is worth more under pressure ({ample.Total:F1} -> {tight.Total:F1}).");

        // The HP-cost difference must carry the right sign. The term is checked
        // by sign directly, and a real card whose upgrade changes its HP cost is
        // named so the path is exercised against actual game data.
        CardModel? hpCard = null;
        CardProfile.UpgradeDiff? hpDiff = null;
        foreach (var canonical in ModelDb.AllCards)
        {
            CardModel candidate;
            try { candidate = combat.CreateCard(canonical, bot); } catch { continue; }
            if (CardProfile.Of(candidate).HpLoss <= 0) continue;
            var diff = CardProfile.DiffUpgrade(candidate);
            if (diff is { } real && real.HpLossDelta != 0) { hpCard = candidate; hpDiff = real; break; }
        }
        if (hpDiff is { } hit)
        {
            var expected = BuildValue.HpLossUpgradeValue(hit.HpLossDelta);
            if (Math.Sign(expected) != -Math.Sign(hit.HpLossDelta))
                throw new Exception($"The HP-cost upgrade term must invert the sign: "
                    + $"delta={hit.HpLossDelta:F0} value={expected:F1}.");
            Console.WriteLine($"PASS: the HP-cost upgrade term follows a real delta ({hpCard!.Id.Entry}: {hit.HpLossDelta:F0}).");
        }
        else
        {
            Console.WriteLine("SKIP: no card in this build changes its HP cost on upgrade.");
        }
        if (!(BuildValue.HpLossUpgradeValue(-2) > 0 && BuildValue.HpLossUpgradeValue(2) < 0 && BuildValue.HpLossUpgradeValue(0) == 0))
            throw new Exception("Lowering the HP cost must be a gain, raising it a cost, and zero neutral.");

        // Keyword transitions must separate beneficial from harmful and leave
        // anything the enum names but this policy does not classify neutral.
        if (!(BuildValue.KeywordTransitionValue(CardKeyword.Ethereal, added: true) < 0
            && BuildValue.KeywordTransitionValue(CardKeyword.Ethereal, added: false) > 0
            && BuildValue.KeywordTransitionValue(CardKeyword.Retain, added: true) > 0
            && BuildValue.KeywordTransitionValue(CardKeyword.Retain, added: false) < 0
            && BuildValue.KeywordTransitionValue(CardKeyword.Exhaust, added: true) == 0
            && BuildValue.KeywordTransitionValue(CardKeyword.Exhaust, added: false) == 0
            && BuildValue.KeywordTransitionValue(CardKeyword.None, added: true) == 0))
            throw new Exception("Keyword transitions must separate beneficial, harmful and unknown cases.");
        Console.WriteLine("PASS: keyword transitions are signed and unknown keywords stay neutral.");

        // A real functional deficit earns bounded continuous relief.
        Clear(); Give<StrikeIronclad>(18);
        var needed = BuildValue.Add(Card<BloodWall>(), bot);
        if (!needed.Reason.Contains("needs-block"))
            throw new Exception($"A blockless deck must show the block deficit: {needed.Reason}");
        if (!needed.Reason.Contains("relief:"))
            throw new Exception($"A real functional deficit must earn bounded relief: {needed.Reason}");
        Console.WriteLine($"PASS: a functional deficit earns continuous relief ({needed.Reason}).");

        // A hypothetical own deck is the whole context: a Star payoff is refused
        // against the live deck and payable against a copy that includes a
        // producer, and the live deck is never touched.
        Clear(); Give<StrikeIronclad>(5);
        var hypothetical = bot.Deck.Cards.ToList();
        hypothetical.Add(combat.CreateCard<Glow>(bot));
        var liveBefore = bot.Deck.Cards.Count;
        var liveRefused = BuildValue.Add(Card<SevenStars>(), bot);
        var hypotheticalPayable = BuildValue.Add(Card<SevenStars>(), bot, hypothetical);
        if (liveRefused.Total > 0)
            throw new Exception($"Without a producer the live deck must refuse the Star payoff: {liveRefused.Total:F1}.");
        if (hypotheticalPayable.Total <= 0)
            throw new Exception($"A hypothetical deck with a producer must price the payoff: {hypotheticalPayable.Total:F1}.");
        if (bot.Deck.Cards.Count != liveBefore)
            throw new Exception("A hypothetical deck must not mutate the live deck.");
        Console.WriteLine("PASS: hypothetical decks are respected and the live deck stays unchanged.");

        // A payoff that is not itself a producer must never discount a real
        // producer. Two copies of Accuracy plus one Blade Dance is one trigger,
        // not negative one: the old code subtracted the candidate's own id count
        // from the producer total.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(4); Give<BladeDance>(1); Give<Accuracy>(2);
        var dupAccuracy = BuildValue.Marginal(Card<Accuracy>(), bot);
        if (!dupAccuracy.Reason.Contains("scaling-supported:shiv"))
            throw new Exception($"Two Accuracy with one Blade Dance must be supported: {dupAccuracy.Reason}");
        var shivSummary = DeckStructure.Build(bot.Deck.Cards.ToList(), bot.MaxEnergy);
        var accuracyProbe = Card<Accuracy>();
        var shivAssessment = DeckMechanisms.Assess(accuracyProbe, CardProfile.Of(accuracyProbe), shivSummary);
        if (shivAssessment.Support != DeckMechanisms.Support.Supported || shivAssessment.Triggers != 1)
            throw new Exception($"Exactly one real Shiv producer must be counted, got "
                + $"{shivAssessment.Support} triggers={shivAssessment.Triggers}.");
        Console.WriteLine("PASS: a duplicated non-producer payoff counts one real Shiv producer.");

        // A new, external self-contained producer+payoff must count itself as
        // one trigger rather than subtracting an existing producer. Fan of
        // Knives both makes Shivs and amplifies them; with one Blade Dance
        // already held the total is two triggers whether the Fan is new or held.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(4); Give<BladeDance>(1);
        var fan = Card<FanOfKnives>();
        var fanFacts = CardProfile.Of(fan);
        var fanSummary = DeckStructure.Build(bot.Deck.Cards.ToList(), bot.MaxEnergy);
        var fanExternal = DeckMechanisms.Assess(fan, fanFacts, fanSummary, candidateInDeck: false);
        bot.Deck.AddInternal(fan);
        var fanHeldSummary = DeckStructure.Build(bot.Deck.Cards.ToList(), bot.MaxEnergy);
        var fanHeld = DeckMechanisms.Assess(fan, fanFacts, fanHeldSummary, candidateInDeck: true);
        if (fanExternal.Support != DeckMechanisms.Support.Supported || fanExternal.Triggers != 2
            || fanHeld.Triggers != 2)
            throw new Exception($"A self-contained producer/payoff must count itself exactly once "
                + $"(external triggers={fanExternal.Triggers}, held triggers={fanHeld.Triggers}).");
        Console.WriteLine("PASS: a new self-contained Shiv producer/payoff counts itself as one trigger.");

        // Removing the last producer is what makes the payoff unsupported: the
        // deck is judged on the hypothetical deck, not on the candidate's own id.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(4); Give<BladeDance>(1);
        var lastBladeDance = bot.Deck.Cards.First(card => card.Id.Entry == "BLADE_DANCE");
        bot.Deck.RemoveInternal(lastBladeDance);
        var removedProducer = BuildValue.Marginal(Card<Accuracy>(), bot);
        if (!removedProducer.Reason.Contains("scaling-unsupported:shiv"))
            throw new Exception($"Removing the last Shiv producer must make Accuracy unsupported: {removedProducer.Reason}");
        Console.WriteLine("PASS: removing the last producer makes the payoff unsupported.");

        // Unplayable cards are not useful supply: a curse in the deck must not
        // count toward the producer total or the average cost.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(4); Give<BladeDance>(1); Give<Decay>(2);
        var curseSummary = DeckStructure.Build(bot.Deck.Cards.ToList(), bot.MaxEnergy);
        if (curseSummary.Unplayable != 2)
            throw new Exception($"Two curses must count as unplayable supply, got {curseSummary.Unplayable}.");
        if (curseSummary.IdCounts.ContainsKey("DECAY"))
            throw new Exception("An unplayable card must not be counted as useful supply.");
        var withCurses = BuildValue.Marginal(Card<Accuracy>(), bot);
        if (!withCurses.Reason.Contains("scaling-supported:shiv"))
            throw new Exception($"Curses must not consume the Shiv producer total: {withCurses.Reason}");
        Console.WriteLine("PASS: unplayable cards are excluded from useful supply.");

        // A card whose profile cannot be read is neutral: excluded from the
        // useful supply and the average, never charged as a defect. If this
        // build contains one, exercise it; otherwise the unplayable case above
        // covers the supply exclusion.
        CardModel? unreadable = null;
        foreach (var canonical in ModelDb.AllCards)
        {
            CardModel candidate;
            try { candidate = combat.CreateCard(canonical, bot); } catch { continue; }
            try { CardProfile.Of(candidate); } catch { unreadable = candidate; break; }
        }
        if (unreadable is not null)
        {
            Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(4); Give<BladeDance>(1);
            bot.Deck.AddInternal(unreadable);
            var unreadableSummary = DeckStructure.Build(bot.Deck.Cards.ToList(), bot.MaxEnergy);
            if (unreadableSummary.Unknown < 1 || unreadableSummary.IdCounts.ContainsKey(unreadable.Id.Entry))
                throw new Exception("An unreadable card must be excluded from known supply, not counted in it.");
            var neutral = BuildValue.Marginal(Card<Accuracy>(), bot);
            if (!neutral.Reason.Contains("scaling-supported:shiv"))
                throw new Exception($"An unreadable card must not disturb the known supply: {neutral.Reason}");
            Console.WriteLine($"PASS: an unreadable card is excluded, not charged as a defect ({unreadable.Id.Entry}).");
        }
        else
        {
            Console.WriteLine("SKIP: no card in this build fails profiling; unknown-supply exclusion covered by the summary test.");
        }

        // Energy pressure is monotone in supply: adding generators or raising the
        // stable per-combat MaxEnergy can only lower it, never the reverse, and a
        // cheap deck with a generator must not read as more pressed than one
        // without (the old flat 0.3 did exactly that).
        Clear(); Give<StrikeIronclad>(12);
        var noGeneratorDeck = bot.Deck.Cards.ToList();
        var noGenerator = DeckStructure.EnergyPressure(DeckStructure.Build(noGeneratorDeck, 3));
        Clear(); Give<StrikeIronclad>(12); Give<Bloodletting>(9);
        var manyGeneratorsDeck = bot.Deck.Cards.ToList();
        var manyGenerators = DeckStructure.EnergyPressure(DeckStructure.Build(manyGeneratorsDeck, 3));
        var highEnergy = DeckStructure.EnergyPressure(DeckStructure.Build(noGeneratorDeck, 6));
        var extremeEnergy = DeckStructure.EnergyPressure(DeckStructure.Build(noGeneratorDeck, 100));
        if (!(manyGenerators <= noGenerator + 0.001 && highEnergy <= noGenerator + 0.001
            && extremeEnergy <= noGenerator + 0.001))
            throw new Exception($"More energy supply must never raise pressure "
                + $"(none={noGenerator:F2}, generators={manyGenerators:F2}, high-max={highEnergy:F2}, extreme={extremeEnergy:F2}).");
        // A positive persistent MaxEnergy is the character's real curve; it is
        // never special-cased down to a fixture range, and a high value can only
        // lower energy pressure, never create it.
        bot.MaxEnergy = 100;
        if (BuildValue.StableEnergy(bot) != 100)
            throw new Exception("A positive persistent MaxEnergy must be accepted as-is, not capped.");
        var highStablePressure = DeckStructure.EnergyPressure(DeckStructure.Build(noGeneratorDeck, BuildValue.StableEnergy(bot)));
        bot.MaxEnergy = 3;
        if (highStablePressure > noGenerator + 0.001)
            throw new Exception($"A high MaxEnergy must not create pressure ({noGenerator:F2} -> {highStablePressure:F2}).");
        Clear(); Give<StrikeIronclad>(12); Give<Bloodletting>(1);
        var cheapPlusGenerator = DeckStructure.EnergyPressure(DeckStructure.Build(bot.Deck.Cards.ToList(), 3));
        if (cheapPlusGenerator > noGenerator + 0.001)
            throw new Exception($"A generator in a cheap deck must not increase pressure "
                + $"({noGenerator:F2} -> {cheapPlusGenerator:F2}).");
        Console.WriteLine($"PASS: energy pressure is monotone in supply "
            + $"({noGenerator:F2} -> {manyGenerators:F2} with generators, {highEnergy:F2} at MaxEnergy 6).");

        // The stable MaxEnergy must reach each entry point. A heavy deck is more
        // pressed at three energy than at six, and the same cost-reduction
        // upgrade is worth more there, without reading transient combat energy.
        Clear(); Give<Bash>(12);
        var heavyDeck = bot.Deck.Cards.ToList();
        bot.MaxEnergy = 3;
        var tightCard = BuildValue.Add(Card<BloodWall>(), bot, heavyDeck);
        var energySlam = Card<BodySlam>();
        var tightUpgrade = BuildValue.UpgradeDelta(energySlam, bot, heavyDeck);
        bot.MaxEnergy = 6;
        var ampleCard = BuildValue.Add(Card<BloodWall>(), bot, heavyDeck);
        var ampleUpgrade = BuildValue.UpgradeDelta(energySlam, bot, heavyDeck);
        bot.MaxEnergy = 100;
        var hugeCard = BuildValue.Add(Card<BloodWall>(), bot, heavyDeck);
        bot.MaxEnergy = 3;
        if (!(ampleCard.Total >= tightCard.Total) || !(hugeCard.Total >= tightCard.Total))
            throw new Exception($"A high MaxEnergy can only lower pressure, never create it "
                + $"(tight={tightCard.Total:F1}, ample={ampleCard.Total:F1}, huge={hugeCard.Total:F1}).");
        if (hugeCard.Reason.Contains("bottleneck:energy"))
            throw new Exception($"A 100-energy character must not read as pressed: {hugeCard.Reason}");
        if (!(tightUpgrade.Total >= ampleUpgrade.Total))
            throw new Exception($"A cost reduction must be worth at least as much under tight energy "
                + $"(tight={tightUpgrade.Total:F1}, ample={ampleUpgrade.Total:F1}).");
        if (!tightCard.Reason.Contains("bottleneck:energy") || ampleCard.Reason.Contains("bottleneck:energy"))
            throw new Exception($"The energy bottleneck must follow stable MaxEnergy "
                + $"(tight={tightCard.Reason}; ample={ampleCard.Reason}).");
        if (slam.IsUpgraded)
            throw new Exception("Pricing an upgrade must not mutate the real card.");
        Console.WriteLine($"PASS: Add and UpgradeDelta propagate stable MaxEnergy "
            + $"(card {tightCard.Total:F1} -> {ampleCard.Total:F1}, upgrade {tightUpgrade.Total:F1} -> {ampleUpgrade.Total:F1}).");
    }
}

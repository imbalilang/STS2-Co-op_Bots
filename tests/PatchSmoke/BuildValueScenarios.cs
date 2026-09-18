using System.Reflection;
using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Build valuation: reward picks, removals and upgrades all answer to one
// objective measured against the deck as it stands, so skipping a reward is a
// real candidate rather than a fallback.
internal static class BuildValueScenarios
{
    internal static void Run()
    {
        // Idempotent: the full suite has already installed this by the time it
        // reaches here, but --building-edits-only runs this scenario first.
        TestEnvironment.Ensure();
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var party = new[] { bot };
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "BUILDVALUE"));
        bot.ResetCombatState(); combat.AddPlayer(bot);
        void Clear() { foreach (var card in bot.Deck.Cards.ToArray()) bot.Deck.RemoveInternal(card); }
        void Give<T>(int count = 1) where T : CardModel
        {
            for (var i = 0; i < count; i++) bot.Deck.AddInternal(combat.CreateCard<T>(bot));
        }

        // A starter deck has no area damage, so the first one is worth taking.
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var cleave = combat.CreateCard<Breakthrough>(bot);
        var cleaveValue = BuildValue.Add(cleave, bot);
        if (cleaveValue.Total <= 0)
            throw new Exception($"The deck's first area attack must be worth taking: {cleaveValue.Total:F1} ({cleaveValue.Reason}).");
        if (!cleaveValue.Reason.Contains("needs-aoe"))
            throw new Exception($"Filling a missing role must be visible in the reason: {cleaveValue.Reason}");
        Console.WriteLine("PASS: a card that fills a missing role is worth taking.");

        // The same card stops being worth it once the role is saturated and the
        // deck is already large: this is what makes Skip a real candidate.
        Clear();
        for (var i = 0; i < 5; i++) Give<Breakthrough>();
        Give<StrikeIronclad>(20);
        var extraBreakthrough = BuildValue.Add(combat.CreateCard<Breakthrough>(bot), bot);
        if (extraBreakthrough.Total > 0)
            throw new Exception($"A saturated role in a bloated deck must not score positive: {extraBreakthrough.Total:F1} ({extraBreakthrough.Reason}).");
        Console.WriteLine("PASS: a saturated role in a bloated deck scores below skipping.");

        // Skipping must beat every candidate when nothing improves the deck.
        var bloated = new List<CardModel> { combat.CreateCard<Breakthrough>(bot), combat.CreateCard<Breakthrough>(bot) };
        if (BuildValue.BestReward(bot, bloated) != -1)
            throw new Exception("BestReward must return Skip when no candidate improves the deck.");
        Console.WriteLine("PASS: skipping is chosen when no candidate improves the deck.");

        // An upgrade that raises two things must beat one that only raises
        // damage, and neither may be priced as if the card were added again.
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var broad = combat.CreateCard<Bash>(bot);          // +2 damage and +1 Vulnerable
        var plain = combat.CreateCard<StrikeIronclad>(bot); // +3 damage only
        var broadGain = BuildValue.UpgradeDelta(broad, bot);
        var plainGain = BuildValue.UpgradeDelta(plain, bot);
        if (plainGain.Total <= 0)
            throw new Exception($"A damage upgrade must be worth something: {plainGain.Total:F1} ({plainGain.Reason}).");
        if (broadGain.Total <= plainGain.Total)
            throw new Exception($"An upgrade that also raises Vulnerable must beat a plain damage upgrade "
                + $"(broad={broadGain.Total:F1} {broadGain.Reason}, plain={plainGain.Total:F1}).");
        var addSame = BuildValue.Add(plain, bot).Total;
        if (addSame > plainGain.Total * 10)
            throw new Exception($"Upgrade and addition must be separate quantities (add={addSame:F1}, upgrade={plainGain.Total:F1}).");
        Console.WriteLine("PASS: upgrades are priced by the real difference they make.");

        // Removal: a diluting non-basic card is a target, but the only copy of a
        // role the deck depends on is protected.
        Clear(); Give<StrikeIronclad>(12);
        var filler = bot.Deck.Cards.First();
        var removal = BuildValue.Remove(filler, bot);
        if (removal.Total <= 0)
            throw new Exception($"A diluting basic card must be worth removing: {removal.Total:F1}.");
        // The only copy of a role the deck depends on must be *refused*, not
        // merely penalised: a deduction can always be outweighed.
        Clear(); Give<StrikeIronclad>(6); Give<DefendIronclad>();
        var lastDefend = bot.Deck.Cards.First(card => card.Type == CardType.Skill);
        var protectedRemoval = BuildValue.Remove(lastDefend, bot);
        if (protectedRemoval.Total != 0 || !protectedRemoval.Reason.Contains("protected"))
            throw new Exception($"Removing the only defensive card must be refused, got "
                + $"{protectedRemoval.Total:F1} ({protectedRemoval.Reason}).");
        if (BotShopPlanner.RemovalValue(lastDefend, bot) != 0)
            throw new Exception("A refused removal must also not be worth gold.");
        Console.WriteLine("PASS: removal targets diluting cards and refuses to remove the deck's only defence.");

        // An upgrade that removes Exhaust must score above zero: the card becomes
        // reusable, which the previous blanket lost-role penalty inverted.
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var hologram = combat.CreateCard<Hologram>(bot);
        if (BuildValue.UpgradeDelta(hologram, bot).Total <= 0)
            throw new Exception($"An upgrade that removes Exhaust must be worth taking, got "
                + $"{BuildValue.UpgradeDelta(hologram, bot).Total:F1} ({BuildValue.UpgradeDelta(hologram, bot).Reason}).");
        Console.WriteLine("PASS: an upgrade that drops Exhaust is valued as reusability, not as a lost tag.");


        // A route the deck is already on must make its payoff cards worth more:
        // the same card means different things in a Strength deck and elsewhere.
        var outsider = Player.CreateForNewRun<Deprived>(UnlockState.all, 2);
        var routeParty = new[] { outsider };
        var routeCombat = new CombatState(runState: RunState.CreateForTest(routeParty, seed: "BUILDVALUE-ROUTE"));
        outsider.ResetCombatState(); routeCombat.AddPlayer(outsider);
        void ClearOutsider() { foreach (var card in outsider.Deck.Cards.ToArray()) outsider.Deck.RemoveInternal(card); }
        void GiveOutsider<T>(int count) where T : CardModel
        {
            for (var i = 0; i < count; i++) outsider.Deck.AddInternal(routeCombat.CreateCard<T>(outsider));
        }

        ClearOutsider(); GiveOutsider<TwinStrike>(4);
        if (Archetypes.Detect(outsider.Deck.Cards.ToList()).Count != 0)
            throw new Exception("A deck with no scaling must not claim a route.");
        var twinInPlainDeck = BuildValue.Add(routeCombat.CreateCard<TwinStrike>(outsider), outsider).Total;

        ClearOutsider(); GiveOutsider<TwinStrike>(4); GiveOutsider<Inflame>(2);
        var routes = Archetypes.Detect(outsider.Deck.Cards.ToList());
        // Deprived is not a character the mined feed covers, so this exercises
        // the role routes: the multi-hit payoff must be what is recognised.
        if (routes.Count == 0 || !routes[0].Roles.Contains("multihit"))
            throw new Exception($"A multi-hit deck with Strength must be recognised: "
                + $"{string.Join(",", routes.Select(match => match.Name))}");
        var twinInStrengthDeck = BuildValue.Add(routeCombat.CreateCard<TwinStrike>(outsider), outsider).Total;
        if (twinInStrengthDeck <= twinInPlainDeck)
            throw new Exception($"A multi-hit payoff must be worth more once the deck scales Strength "
                + $"({twinInStrengthDeck:F1} vs {twinInPlainDeck:F1}).");
        Console.WriteLine("PASS: a card that feeds the deck's route is worth more than the same card elsewhere.");

        // Size pressure: past the forming stage the same card must be worth less,
        // and a mediocre one must go below zero. This is the lever that makes Skip
        // a real answer to "the card is fine but not what the deck needs".
        // Calibrated on real reward screens: at 19 cards the best candidate was
        // ~18-20 and should have been skipped.
        ClearOutsider(); GiveOutsider<StrikeIronclad>(5); GiveOutsider<DefendIronclad>(4);
        var smallDeck = outsider.Deck.Cards.ToList();
        var smallValue = BuildValue.Add(routeCombat.CreateCard<TwinStrike>(outsider), outsider, smallDeck).Total;
        ClearOutsider(); GiveOutsider<StrikeIronclad>(11); GiveOutsider<DefendIronclad>(9);
        var bigDeck = outsider.Deck.Cards.ToList();
        var bigValuation = BuildValue.Add(routeCombat.CreateCard<TwinStrike>(outsider), outsider, bigDeck);
        if (!(bigValuation.Total < smallValue))
            throw new Exception($"The same card must be worth less in a 20-card deck ({bigValuation.Total:F1}) "
                + $"than in a 9-card one ({smallValue:F1}).");
        if (bigValuation.Total >= 0)
            throw new Exception($"A filler card in a 20-card starter deck must score below skipping, "
                + $"got {bigValuation.Total:F1} ({bigValuation.Reason}).");
        if (!bigValuation.Reason.Contains("size:"))
            throw new Exception($"The size pressure must be visible in the reason: {bigValuation.Reason}");
        Console.WriteLine("PASS: deck size pressure makes a filler card in a large deck score below skipping.");

        // A payoff whose enabler is missing must not read as a plan. Rupture gains
        // Strength when you lose HP, and every card the mining pairs it with is a
        // self-damage card; without one its scaling credit drops and the reason
        // says why. This is the act-2 Ironclad that drafted Rupture with no way to
        // trigger it.
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var ruptureAlone = BuildValue.Marginal(combat.CreateCard<Rupture>(bot), bot);
        if (!ruptureAlone.Reason.Contains("scaling-unsupported"))
            throw new Exception($"Rupture without a self-damage source must be flagged, got: {ruptureAlone.Reason}");
        Give<Hemokinesis>(1);
        var ruptureWithEnabler = BuildValue.Marginal(combat.CreateCard<Rupture>(bot), bot);
        if (!(ruptureWithEnabler.Total > ruptureAlone.Total))
            throw new Exception($"Rupture must be worth more once its mined enabler is in the deck: "
                + $"{ruptureWithEnabler.Total:F1} vs {ruptureAlone.Total:F1}");
        Console.WriteLine("PASS: a scaling payoff without its mined enabler is flagged and valued lower.");

        // Community affinity is a bounded additive prior and must never control
        // the size penalty. The old code let any positive lift switch the
        // 30-point dilution cost down to 12, so a +2 nudge waived 18 points. The
        // feed is directional: the held card's document recommends the offered
        // candidate, so a held Deadly Poison activates Outbreak's prior. The
        // prior still adds value, but both decks pay the full size:30.0.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(15); Give<DeadlyPoison>(1);
        var withPrior = BuildValue.Add(combat.CreateCard<Outbreak>(bot), bot);
        if (!withPrior.Reason.Contains("drafted-together"))
            throw new Exception($"A held partner's recommendation must earn the affinity prior: {withPrior.Reason}");
        if (!withPrior.Reason.Contains("size:30.0"))
            throw new Exception($"Affinity must not waive the size penalty: {withPrior.Reason}");
        if (withPrior.Reason.Contains("relief:"))
            throw new Exception($"Affinity must not grant size relief: {withPrior.Reason}");
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(16);
        var withoutPrior = BuildValue.Add(combat.CreateCard<Outbreak>(bot), bot);
        if (withoutPrior.Reason.Contains("drafted-together"))
            throw new Exception($"Affinity must not fire without its partner: {withoutPrior.Reason}");
        if (!withoutPrior.Reason.Contains("size:30.0"))
            throw new Exception($"A card with no partner pays the full size pressure: {withoutPrior.Reason}");
        Console.WriteLine("PASS: affinity is a bounded prior and no longer controls the size penalty.");

        // Thinning must get more valuable as dead draws accumulate, or the shop's
        // growing price (a second removal costs 150) can never be paid: a flat
        // 55 x 2.2 = 121 left every run with ten starters still in the deck.
        const double GoldPerDeckValue = 2.2;  // mirrors BotShopPlanner.GoldPerDeckValue
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(5);
        for (var i = 0; i < 13; i++) Give<PommelStrike>(1);
        var heavyTarget = bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var heavyRemoval = BotShopPlanner.RemovalValue(heavyTarget, bot);
        if (!(heavyRemoval > 150 / GoldPerDeckValue))
            throw new Exception($"Ten starters must make a 150-gold removal affordable, got {heavyRemoval:F1}.");
        Clear(); Give<StrikeIronclad>(2); Give<DefendIronclad>(2);
        for (var i = 0; i < 19; i++) Give<PommelStrike>(1);
        var leanTarget = bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var leanRemoval = BotShopPlanner.RemovalValue(leanTarget, bot);
        if (!(heavyRemoval > leanRemoval))
            throw new Exception($"Removal value must rise with the number of dead draws ({heavyRemoval:F1} vs {leanRemoval:F1}).");
        Console.WriteLine($"PASS: starter removal scales with dead draws ({leanRemoval:F1} -> {heavyRemoval:F1}), so a 150-gold removal is payable.");

        // The reviewed Ironclad declined Blood Wall at -37.8 from a 23-card deck
        // whose own reason line said `needs-block`: the size penalty alone was
        // worth more than the card. A card that fills a function the deck is short
        // of must survive the size of the deck it is joining.
        ClearOutsider();
        GiveOutsider<StrikeIronclad>(9); GiveOutsider<DefendIronclad>(2); GiveOutsider<PommelStrike>(12);
        var bloodWall = BuildValue.Add(routeCombat.CreateCard<BloodWall>(outsider), outsider);
        if (!bloodWall.Reason.Contains("needs-block"))
            throw new Exception($"A deck with two block cards must be shown as short of block: {bloodWall.Reason}");
        if (bloodWall.Total <= 0)
            throw new Exception($"A block card the deck needs must beat skipping even at 23 cards: "
                + $"{bloodWall.Total:F1} ({bloodWall.Reason}).");
        Console.WriteLine($"PASS: a needed block card still gets in past the size pressure ({bloodWall.Total:F1}, {bloodWall.Reason}).");

        // A card nothing asked for costs more the later it is taken, and most
        // once no shop is left to remove it at — that dilution can never be
        // undone. Plan cards keep their relief, so a route can still be finished
        // in the last act rather than being taxed for arriving late.
        if (!(RunDepth.BloatFactor(2, shopAhead: false) > RunDepth.BloatFactor(2, shopAhead: true)
            && RunDepth.BloatFactor(2, shopAhead: true) > RunDepth.BloatFactor(0, shopAhead: true)))
            throw new Exception("The bloat factor must rise with the act and again once no shop is left ahead.");
        if (RunDepth.BloatFactor(0, shopAhead: true) != 1.0)
            throw new Exception("An act-1 deck with shops ahead must be priced exactly as before.");
        void SetAct(int act) => typeof(RunState)
            .GetField("_currentActIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(bot.RunState, act);
        Clear(); Give<StrikeIronclad>(9); Give<DefendIronclad>(9);
        SetAct(0); var earlyFiller = BuildValue.Add(combat.CreateCard<TwinStrike>(bot), bot);
        SetAct(2); var lateFiller = BuildValue.Add(combat.CreateCard<TwinStrike>(bot), bot);
        if (!(lateFiller.Total < earlyFiller.Total))
            throw new Exception($"The same filler must be worth less in the last act: "
                + $"{lateFiller.Total:F1} vs {earlyFiller.Total:F1}.");
        if (!lateFiller.Reason.Contains("size:22.5"))
            throw new Exception($"An act-3 filler must pay the depth-weighted size pressure: {lateFiller.Reason}");
        Clear(); Give<StrikeIronclad>(18);
        SetAct(0); var earlyNeeded = BuildValue.Add(combat.CreateCard<BloodWall>(bot), bot);
        SetAct(2); var lateNeeded = BuildValue.Add(combat.CreateCard<BloodWall>(bot), bot);
        SetAct(0);
        if (Math.Abs(lateNeeded.Total - earlyNeeded.Total) > 0.01)
            throw new Exception($"A card the deck needs must not get dearer with depth: "
                + $"{lateNeeded.Total:F1} vs {earlyNeeded.Total:F1}.");
        if (!lateNeeded.Reason.Contains("size:6.0"))
            throw new Exception($"A needed card keeps its relief in the last act: {lateNeeded.Reason}");
        Console.WriteLine($"PASS: filler gets dearer with depth ({earlyFiller.Total:F1} -> {lateFiller.Total:F1}, "
            + $"{lateFiller.Reason}) while a card the deck needs does not.");

        // The reviewed Silent's Footwork was flagged `scaling-unsupported` in a
        // deck with plenty of block. Dexterity multiplies the block already
        // there, so it is not waiting on a mined partner.
        Clear(); Give<StrikeSilent>(5); Give<DefendSilent>(8);
        var footwork = BuildValue.Marginal(combat.CreateCard<Footwork>(bot), bot);
        if (footwork.Reason.Contains("scaling-unsupported"))
            throw new Exception($"Dexterity in a deck with eight block cards is supported, not a missing enabler: {footwork.Reason}");
        Clear(); Give<StrikeSilent>(8); Give<DefendSilent>(1);
        var footworkAlone = BuildValue.Marginal(combat.CreateCard<Footwork>(bot), bot);
        if (!(footwork.Total > footworkAlone.Total))
            throw new Exception($"Dexterity must be worth more with block to multiply: {footwork.Total:F1} vs {footworkAlone.Total:F1}");
        Console.WriteLine($"PASS: Dexterity scaling is valued against the block the deck already has ({footworkAlone.Total:F1} -> {footwork.Total:F1}).");

        // No card in the pool both retains and blocks, so a retained skill must
        // not read as a block build. The route is the Dexterity one.
        Clear(); Give<DefendSilent>(4); Give<StrikeSilent>(2); Give<Snakebite>(1);
        var retainNames = Archetypes.Detect(bot.Deck.Cards.ToList()).Select(match => match.Name).ToArray();
        if (retainNames.Contains("block-retain"))
            throw new Exception($"A deck whose only Retain card is an attack must not read as block-retain: {string.Join(",", retainNames)}");
        Clear(); Give<DefendSilent>(4); Give<StrikeSilent>(2); Give<Fade>(1);
        var dexterityNames = Archetypes.Detect(bot.Deck.Cards.ToList()).Select(match => match.Name).ToArray();
        if (!dexterityNames.Contains("block-dexterity"))
            throw new Exception($"A block deck with a Dexterity card must read as block-dexterity: {string.Join(",", dexterityNames)}");
        Console.WriteLine("PASS: the block route is driven by Dexterity, not by the Retain keyword.");

        // No recognised route must leave the valuation exactly as it was: this is
        // the guarantee that an unknown or modded deck cannot be made worse.
        ClearOutsider(); GiveOutsider<StrikeIronclad>(5); GiveOutsider<DefendIronclad>(4);
        var plainDeck = outsider.Deck.Cards.ToList();
        foreach (var card in plainDeck)
            if (Math.Abs(Archetypes.Bonus(CardProfile.Of(card), card, plainDeck, outsider)) > 0.01)
                throw new Exception("An unrecognised deck must receive no archetype bonus at all.");
        Console.WriteLine("PASS: an unrecognised deck receives no archetype bonus.");

        // A curse or status must never score as a card worth taking, and must
        // always be a removal target. The old card valuation had this guard;
        // the unified valuation has to keep it (caught by the Codex cross-check,
        // where unplayable cards read as cost 0 and thus "free and cheap").
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var curse = routeCombat.CreateCard<Decay>(outsider);
        outsider.Deck.AddInternal(curse);
        if (BuildValue.Marginal(curse, outsider).Total > -100)
            throw new Exception($"A curse must never be worth adding: {BuildValue.Marginal(curse, outsider).Total:F1}.");
        if (BuildValue.Add(curse, outsider).Total > -100)
            throw new Exception("A curse must never win a reward comparison.");
        var curseRemoval = BuildValue.Remove(curse, outsider);
        if (curseRemoval.Total < 100)
            throw new Exception($"A curse must always be worth removing: {curseRemoval.Total:F1}.");
        if (CardProfile.Of(curse).Roles.Contains("zerocost"))
            throw new Exception("An unplayable card must not be tagged as a free card.");
        // The shop compares everything in gold. A removal used to be compared in
        // raw deck-value units and so could never beat a real removal price.
        Clear(); Give<StrikeIronclad>(11); Give<DefendIronclad>();
        var strikeToRemove = bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var removalGold = BotShopPlanner.RemovalValue(strikeToRemove, bot) * 2.2;
        if (removalGold <= 75)
            throw new Exception($"Thinning a starter must be worth more than the first removal price (75), got {removalGold:F0}.");
        Console.WriteLine("PASS: a removal is priced in the same gold unit as everything else the shop sells.");

        // A support card must keep its team value on the reward path, where it
        // used to be scored as if the deck were alone.
        var ally = Player.CreateForNewRun<Deprived>(UnlockState.all, 4);
        var mate = Player.CreateForNewRun<Deprived>(UnlockState.all, 5);
        var duo = new[] { ally, mate };
        var duoCombat = new CombatState(runState: RunState.CreateForTest(duo, seed: "BUILDVALUE-TEAM"));
        foreach (var member in duo) { member.ResetCombatState(); duoCombat.AddPlayer(member); }
        foreach (var card in ally.Deck.Cards.ToArray()) ally.Deck.RemoveInternal(card);
        for (var i = 0; i < 5; i++) ally.Deck.AddInternal(duoCombat.CreateCard<StrikeIronclad>(ally));
        var supportCard = (CardModel)duoCombat.CreateCard<Lift>(ally);
        if (TeamCoordinator.TeamBonus(supportCard, ally) <= 0)
            throw new Exception("An ally-targeted support card must carry a team bonus.");
        var withTeam = BuildValue.Marginal(supportCard, ally).Reason.Contains("team-fit");
        var alone = Player.CreateForNewRun<Deprived>(UnlockState.all, 6);
        var soloCombat = new CombatState(runState: RunState.CreateForTest(new[] { alone }, seed: "BUILDVALUE-SOLO"));
        alone.ResetCombatState(); soloCombat.AddPlayer(alone);
        if (BuildValue.Marginal(soloCombat.CreateCard<Lift>(alone), alone).Reason.Contains("team-fit"))
            throw new Exception("A solo deck must not get a team bonus.");
        if (!withTeam) throw new Exception("The reward path must carry the team bonus.");
        Console.WriteLine("PASS: the reward path carries the team bonus for support cards.");

        // The card the smith actually upgrades must be the one the upgrade
        // valuation ranks highest, not whatever the older deck-scoring picks. The
        // audit found Bash (5.4) losing to True Grit (2) because the two used
        // different algorithms.
        Clear(); Give<StrikeIronclad>(5); Give<DefendIronclad>(4);
        var bash = combat.CreateCard<Bash>(bot);
        var trueGrit = combat.CreateCard<TrueGrit>(bot);
        if (BuildValue.UpgradeDelta(bash, bot).Total <= BuildValue.UpgradeDelta(trueGrit, bot).Total)
            throw new Exception("This probe needs Bash to outrank True Grit on the upgrade valuation.");
        var upgraded = BotBrain.SelectCards(bot, new CardModel[] { trueGrit, bash }, 1, 1, "FromDeckForUpgrade");
        if (upgraded.Count != 1 || upgraded[0].Id.Entry != bash.Id.Entry)
            throw new Exception($"The smith must upgrade the card the valuation ranks highest, got "
                + $"{upgraded.FirstOrDefault()?.Id.Entry ?? "(none)"}.");
        Console.WriteLine("PASS: the smith upgrades the card the build valuation ranks highest.");

        // A screen the game marks declinable must be declinable. The stall guard
        // below it keys off the method name, and FromChooseACardScreen contains
        // "ChooseA", so every such screen was forced to a pick: an offer of
        // another character's cards had to be taken from even when none of them
        // could be played. Passing the screen's own canSkip through is what lets
        // the bot decline; the guard still holds when the screen demands a card.
        var skipProbe = new CardModel[] { trueGrit, bash };
        if (BotBrain.SelectCards(bot, skipProbe, 0, 1, "FromChooseACardScreen", maySkip: true).Count != 0
            && BuildValue.Add(trueGrit, bot).Total <= 0 && BuildValue.Add(bash, bot).Total <= 0)
            throw new Exception("A declinable screen must be able to come back empty.");
        if (BotBrain.SelectCards(bot, skipProbe, 0, 1, "FromChooseACardScreen", maySkip: false).Count != 1)
            throw new Exception("A screen that demands a card must still return one.");
        if (BotBrain.SelectCards(bot, skipProbe, 1, 1, "FromChooseACardScreen", maySkip: true).Count != 1)
            throw new Exception("maySkip must not override an explicit minimum.");
        Console.WriteLine("PASS: a declinable card screen can be declined, a mandatory one cannot.");

        // The baked clusters come from real runs, so every card they name must
        // exist in this game build. A patch that removes one is a silent
        // invalidation of a signature: fail loudly instead.
        var localIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var canonical in ModelDb.AllCards)
        {
            try { localIds.Add(canonical.Id.Entry); } catch { }
        }
        var unknown = BakedArchetypes.All
            .SelectMany(cluster => cluster.Cards)
            .Distinct(StringComparer.Ordinal)
            .Where(id => !localIds.Contains(id))
            .ToList();
        if (unknown.Count > 0)
            throw new Exception($"Baked archetype cards missing from this build: {string.Join(",", unknown)}");
        if (BakedArchetypes.All.Length < 40)
            throw new Exception($"The baked archetype table looks truncated: {BakedArchetypes.All.Length} clusters.");
        Console.WriteLine($"PASS: all {BakedArchetypes.All.Length} mined archetypes name cards that exist in this build.");

        // The resource table is scraped from card text and a cost field, so it is
        // exactly as drift-prone as the archetypes — more so, because a rename
        // would silently drop a producer and make a payable payoff look unpaid.
        var unknownResource = BakedResources.All
            .SelectMany(resource => resource.Producers.Concat(resource.Spends))
            .Distinct(StringComparer.Ordinal)
            .Where(id => !localIds.Contains(id))
            .ToList();
        if (unknownResource.Count > 0)
            throw new Exception($"Baked resource cards missing from this build: {string.Join(",", unknownResource)}");
        var stars = BakedResources.All.Single(resource => resource.Name == "star");
        if (stars.Producers.Length == 0 || stars.Spends.Length == 0)
            throw new Exception("The star row must record both sides; it is the one resource where the "
                + "scarcity argument rests on data rather than on a guess.");
        // The asymmetry that makes Star the breaking case: far more cards spend it
        // than can ever make it, and every producer is one character's.
        // Generators whose text does not read "Gain [star:1]" exactly: a number can
        // sit between the verb and the resource, and the verb can be lower case.
        // The first extraction matched the exact shape only, silently dropped both
        // of these, and the gate then refused Star payoffs for a deck whose only
        // Star source was one of them. Named so the miss fails loudly instead.
        foreach (var generator in new[] { "ROYAL_GAMBLE", "THE_SEALED_THRONE" })
            if (!stars.Producers.Contains(generator))
                throw new Exception($"{generator} makes Stars, so it must be recorded as a producer; "
                    + "without it the gate refuses payoffs for a deck that can pay for them.");
        var shivResource = BakedResources.All.Single(resource => resource.Name == "shiv");
        foreach (var generator in new[] { "BLADE_DANCE", "INFINITE_BLADES" })
            if (!shivResource.Producers.Contains(generator))
                throw new Exception($"{generator} makes Shivs, so it must be recorded as a producer.");
        if (stars.Spends.Length <= stars.Producers.Length)
            throw new Exception($"Star spends ({stars.Spends.Length}) must outnumber its producers "
                + $"({stars.Producers.Length}); if that changed, revisit the scarcity assumption.");
        Console.WriteLine($"PASS: the baked resource table names live cards; stars are {stars.Producers.Length} "
            + $"producers against {stars.Spends.Length} payoffs.");

        // The gate. Both reviewed cases — a Regent whose producers had been stolen
        // and a cross-class bot offered another character's Star card — reduce to
        // one fact: the deck makes no Stars, so the card never leaves the hand.
        // That is why the gate needs no character lookup at all.
        Clear(); Give<StrikeIronclad>(5);
        var sevenStars = combat.CreateCard<SevenStars>(bot);
        var unpayable = BuildValue.Marginal(sevenStars, bot);
        if (unpayable.Total > 0)
            throw new Exception($"A Star payoff with no producer in the deck must be refused, got {unpayable.Total:F1}.");
        if (!unpayable.Reason.Contains("no-star"))
            throw new Exception($"The refusal must name the missing resource, got '{unpayable.Reason}'.");
        Clear(); Give<StrikeIronclad>(5); Give<Glow>(1);
        if (BuildValue.Marginal(sevenStars, bot).Total <= 0)
            throw new Exception("With a producer in the deck the same card must be priced normally.");
        Console.WriteLine("PASS: a Star payoff is refused without a producer and priced with one.");

        // The shiv package is the one concrete case where a single tag conflates
        // three different roles. All of Blade Dance, Accuracy and Knife Trap carry
        // the same `shiv` tag, so the old additive route — which counts that tag and
        // needs two of it — scored "two producers, no multiplier" (measured odds
        // ratio 0.45, a negative asset) identically to "producers + Accuracy +
        // Knife Trap" (3.11). Splitting them is what makes the package expressible.
        var shivs = BakedResources.All.Single(resource => resource.Name == "shiv");
        foreach (var producer in new[] { "BLADE_DANCE", "INFINITE_BLADES", "CLOAK_AND_DAGGER" })
            if (!shivs.Producers.Contains(producer))
                throw new Exception($"{producer} makes Shivs and must be a producer.");
        if (!shivs.Multipliers.Contains("ACCURACY"))
            throw new Exception("Accuracy makes each Shiv worth more, so it is a multiplier, not a producer.");
        if (!shivs.Replayers.Contains("KNIFE_TRAP"))
            throw new Exception("Knife Trap cashes in the accumulated Shivs, so it is a replayer.");
        // The discriminator the old tag could not make: the Shiv token IS the
        // resource. Counting it as a source is what let two tokens satisfy a
        // "complete" package.
        foreach (var role in new[] { shivs.Producers, shivs.Multipliers, shivs.Replayers })
            if (role.Contains("SHIV"))
                throw new Exception("The Shiv token is the resource itself; it must not count as a source of it.");
        // And the overlap the single tag also could not express: this card does both.
        if (!(shivs.Producers.Contains("FAN_OF_KNIVES") && shivs.Multipliers.Contains("FAN_OF_KNIVES")))
            throw new Exception("Fan of Knives both makes Shivs and makes them hit everything.");
        Console.WriteLine($"PASS: the shiv package splits into {shivs.Producers.Length} producers, "
            + $"{shivs.Multipliers.Length} multipliers and {shivs.Replayers.Length} replayers.");

        // Poison, same three-way split. Its finding is an absence: it has a
        // multiplier (Accelerant triggers each stack once more) and two ways to
        // cash in gradually — Mirage converts the stacks to Block, Outbreak fires
        // every third application — but nothing that dumps the accumulated poison
        // at once. Shiv's top tier (odds ratio 3.11) comes precisely from having
        // that burst in Knife Trap, so poison's measured -18pt against shiv's +31pt
        // has a concrete structural candidate. Recorded, not acted on: whether the
        // replayer tier earns its own dimension is for the AUC table to say.
        var poison = BakedResources.All.Single(resource => resource.Name == "poison");
        foreach (var producer in new[] { "DEADLY_POISON", "SNAKEBITE", "NOXIOUS_FUMES" })
            if (!poison.Producers.Contains(producer))
                throw new Exception($"{producer} applies Poison and must be a producer.");
        if (!poison.Multipliers.Contains("ACCELERANT"))
            throw new Exception("Accelerant makes each Poison stack trigger again, so it is a multiplier.");
        if (poison.Replayers.Length != 0)
            throw new Exception("Poison has no burst replayer; if one is added, re-check the ramp/burst "
                + $"split rather than assuming it belongs here (got {string.Join(",", poison.Replayers)}).");
        // Re-bucketing: three cards the tester identified as exponential engines were
        // sitting in the loose Payoffs set and absent from M/R. Each judgement cites
        // the card-text clause it rests on, so it can be independently reviewed.
        var doom = BakedResources.All.Single(resource => resource.Name == "doom");
        if (!doom.Multipliers.Contains("NO_ESCAPE"))
            throw new Exception("No Escape adds Doom per existing Doom, so its output grows with the "
                + "stock: that is a multiplier, not a payoff.");
        var orb = BakedResources.All.Single(resource => resource.Name == "orb");
        // Tester's independent reading (high confidence), adopted over my first call:
        // it reads what is already channelled and produces that much again in one go,
        // which is the cash-in shape, not a standing amplifier.
        if (!orb.Replayers.Contains("VOLTAIC"))
            throw new Exception("Voltaic channels Lightning equal to what is already channelled this combat: replayer.");
        var soul = BakedResources.All.Single(resource => resource.Name == "soul");
        if (!soul.Replayers.Contains("SOUL_STORM"))
            throw new Exception("Soul Storm converts the Soul stock in the exhaust pile into damage in one "
                + "go: a cash-in, so it belongs with replayers, not multipliers.");
        // The one that must NOT be bucketed: its multiplier is over a resource this
        // table has no axis for, so filing it under any resource would fake a stock.
        foreach (var resource in BakedResources.All)
            if (resource.Multipliers.Contains("SUPERMASSIVE") || resource.Replayers.Contains("SUPERMASSIVE"))
                throw new Exception("Supermassive scales on cards created this combat, which is not a "
                    + "resource this table tracks; it must stay unbucketed until that axis exists.");
        // The fourth bucket. Without it the residue of the loose Payoff set has
        // nowhere to go, and clearing that set would delete Mirage from the table
        // entirely — a card the tester's full sweep found is the only Poison reader
        // Silent has. Data classification only: `complete` does not consult it.
        var poisonConverters = BakedResources.All.Single(r => r.Name == "poison").Converters;
        if (!poisonConverters.Contains("MIRAGE"))
            throw new Exception("Mirage is the only card that reads the Poison stack; it must survive in "
                + "the converter bucket now that the loose payoff set is cleared.");
        var doomConverters = doom.Converters;
        if (!(doomConverters.Contains("TIME'S UP") && doomConverters.Contains("SHROUD")))
            throw new Exception("Time's Up and Shroud read the Doom stack: converters, not payoffs.");
        Console.WriteLine($"PASS: the converter bucket keeps Mirage and {doomConverters.Length - 1} Doom readers.");
        // Ironclad Strength. Two things are worth pinning.
        var strength = BakedResources.All.Single(resource => resource.Name == "strength");
        // 1. Colorless cards are in every class's pool. The generator scanned only the
        //    five class colours, so Prowess ("Gain 1 Strength") was invisible to the
        //    strength row. Fixing that is what put it back.
        if (!strength.Producers.Contains("PROWESS"))
            throw new Exception("Prowess is Colorless but gains Strength; the table must scan the "
                + "colorless pool or every row silently loses those cards.");
        // 2. Strength has no doubler in this build, and its real multiplier is not a card
        //    but a card property: hit count. A multi-hit attack applies the whole
        //    accumulated Strength once per hit, so it is the cash-in (replayer), not a
        //    standing amplifier. Twin Strike "Deal 5 damage twice" is the canonical one.
        if (strength.Multipliers.Length != 0)
            throw new Exception("No card in this build doubles Strength; if one is added, re-decide "
                + $"whether it is a multiplier or a replayer (got {string.Join(",", strength.Multipliers)}).");
        if (!strength.Replayers.Contains("TWIN_STRIKE"))
            throw new Exception("A multi-hit attack is how accumulated Strength is spent in one play.");
        // 3. The interaction the user pointed at runs on the Vulnerable axis, not Strength:
        //    Molten Fist doubles Vulnerable, Dominate converts Vulnerable into Strength.
        //    Molten Fist never mentions Strength, so filing it under that row would fake
        //    the relationship. It waits for a Vulnerable axis.
        foreach (var resource in BakedResources.All)
            foreach (var bucket in new[] { resource.Producers, resource.Multipliers, resource.Replayers, resource.Converters })
                if (bucket.Contains("MOLTEN_FIST"))
                    throw new Exception("Molten Fist doubles Vulnerable; it is not a Strength card and "
                        + "must wait for the Vulnerable axis rather than be filed under Strength.");
        Console.WriteLine($"PASS: strength is {strength.Producers.Length} producers / no doubler / "
            + $"{strength.Replayers.Length} multi-hit cash-ins; Molten Fist stays off the Strength axis.");
        Console.WriteLine("PASS: No Escape / Voltaic / Soul Storm are re-bucketed with cited card text, "
            + "and Supermassive stays out.");
        Console.WriteLine($"PASS: poison splits into {poison.Producers.Length} producers, "
            + $"{poison.Multipliers.Length} multiplier and no replayer.");

        // A team bonus may not carry a card on its own: the reviewed run took two
        // CONCOCT on `team-fit` alone into a deck that could not support them.
        // The cap is a share of the card's own contribution rather than an
        // absolute, so it self-normalises and holds at every party size — which is
        // also why it needs no tuning and is safe to pin here.
        var teamCappedScore = TeamCoordinator.CardValue(bash, bot).Score;
        var ownOnlyScore = HumanCoopAdvisor.CardValue(bash, bot).Score;
        if (teamCappedScore > ownOnlyScore + Math.Max(0, ownOnlyScore) * 1.5 + 0.001)
            throw new Exception($"A team bonus must not exceed 1.5x the card's own value: "
                + $"team={teamCappedScore:F1} own={ownOnlyScore:F1}.");
        Console.WriteLine("PASS: a team bonus cannot outvote the card's own contribution.");


        // A deck holding a mined signature must be recognised by it, and the
        // cards that complete that signature must be worth more than they would
        // be in a deck going nowhere. Uses a real character, since clusters are
        // keyed by character.
        // The headless fixture cannot populate starting relics, so the character
        // is swapped in after creation (the same workaround the event tests use).
        var knight = Player.CreateForNewRun<Deprived>(UnlockState.all, 3);
        typeof(Player).GetField("<Character>k__BackingField", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(knight, ModelDb.Character<Ironclad>());
        var knightParty = new[] { knight };
        var knightCombat = new CombatState(runState: RunState.CreateForTest(knightParty, seed: "BUILDVALUE-CLUSTER"));
        knight.ResetCombatState(); knightCombat.AddPlayer(knight);
        foreach (var card in knight.Deck.Cards.ToArray()) knight.Deck.RemoveInternal(card);
        void GiveKnight<T>(int count) where T : CardModel
        {
            for (var i = 0; i < count; i++) knight.Deck.AddInternal(knightCombat.CreateCard<T>(knight));
        }

        GiveKnight<Bloodletting>(1); GiveKnight<Breakthrough>(1); GiveKnight<StrikeIronclad>(8);
        var clusterMatches = Archetypes.Detect(knight.Deck.Cards.ToList(), knight);
        if (clusterMatches.Count == 0 || !clusterMatches[0].Name.Contains("Bloodletting"))
            throw new Exception($"A deck holding a mined signature must be recognised: "
                + $"{string.Join(",", clusterMatches.Select(match => match.Name))}");
        if (clusterMatches[0].Signature.Count < 2)
            throw new Exception("A mined match must carry its signature cards.");
        var infernoInCluster = BuildValue.Add(knightCombat.CreateCard<Inferno>(knight), knight).Total;

        // The same card in a deck that is on no route.
        foreach (var card in knight.Deck.Cards.ToArray()) knight.Deck.RemoveInternal(card);
        GiveKnight<StrikeIronclad>(8); GiveKnight<DefendIronclad>(4);
        if (Archetypes.Detect(knight.Deck.Cards.ToList(), knight).Count != 0)
            throw new Exception("A starter deck must not match a mined cluster.");
        var infernoAlone = BuildValue.Add(knightCombat.CreateCard<Inferno>(knight), knight).Total;
        if (infernoInCluster <= infernoAlone)
            throw new Exception($"A card completing a mined signature must be worth more "
                + $"({infernoInCluster:F1} vs {infernoAlone:F1}).");
        Console.WriteLine("PASS: a mined signature is recognised and its completing cards are worth more.");

        // Card-to-card affinity comes from real picks, so every id it names must
        // exist in this build, and the floors the generator applied must still be
        // recorded (they are what makes the numbers worth trusting).
        CardModel? Local(string id)
        {
            var canonical = ModelDb.AllCards.FirstOrDefault(c => c.Id.Entry == id);
            return canonical is null ? null : knightCombat.CreateCard(canonical, knight);
        }
        var affinityIds = BakedCardAffinity.Pairs.Keys
            .Concat(BakedCardAffinity.Pairs.Values.SelectMany(pairs => pairs.Select(pair => pair.Card)))
            .Distinct(StringComparer.Ordinal).ToList();
        var affinityUnknown = affinityIds.Where(id => Local(id) is null).ToList();
        if (affinityUnknown.Count > 0)
            throw new Exception($"Baked affinity references cards this build does not have "
                + $"({string.Join(",", affinityUnknown.Take(10))}); the feed runs ahead of the game, so "
                + "re-run scripts/fetch-spire-codex-draft-recs.py then scripts/import-spire-codex-affinity.py.");
        if (BakedCardAffinity.MinOffers < 100 || BakedCardAffinity.MinPicks < 10)
            throw new Exception("Affinity sample floors are too low to trust the mined pairs.");
        Console.WriteLine($"PASS: all {affinityIds.Count} cards named by the {BakedCardAffinity.Pairs.Count} affinity entries exist in this build.");

        // Direction matters: the feed's document for a held card B lists the
        // offered cards A it recommends. The old code read the candidate A's
        // document and searched the deck for a partner B — the reverse arrow.
        // Here a held Deadly Poison's document recommends Outbreak, so adding
        // the former must activate the latter's prior.
        if (Local("DEADLY_POISON") is { } held && Local("OUTBREAK") is { } offered)
        {
            foreach (var card in knight.Deck.Cards.ToArray()) knight.Deck.RemoveInternal(card);
            GiveKnight<StrikeIronclad>(5); GiveKnight<DefendIronclad>(4);
            if (BuildValue.Marginal(offered, knight).Reason.Contains("drafted-together"))
                throw new Exception("Affinity must not fire on a card whose recommending context is absent.");
            knight.Deck.AddInternal(held);
            if (!BuildValue.Marginal(offered, knight).Reason.Contains("drafted-together"))
                throw new Exception("Affinity must fire when DEADLY_POISON, the context card, is already in the deck.");
            Console.WriteLine("PASS: affinity reads held=>offered, so DEADLY_POISON activates OUTBREAK.");
        }

        // Removal entry point: a protected card is a refusal, not a slightly
        // worse score. The old ranking compared the refusal's 0 against an
        // unprotected candidate's negative removal value and removed the
        // protected card, so the deck lost the last copy of a role.
        Clear();
        Give<DefendIronclad>(1);
        Give<StrikeIronclad>(15);
        var lastBlock = bot.Deck.Cards.First(card => card.Id.Entry == "DEFEND_IRONCLAD");
        var strongTarget = combat.CreateCard<Inflame>(bot);
        bot.Deck.AddInternal(strongTarget);
        var guardRemoval = BuildValue.Remove(lastBlock, bot);
        var negativeRemoval = BuildValue.Remove(strongTarget, bot);
        if (guardRemoval.Total != 0 || !guardRemoval.Reason.Contains("protected"))
            throw new Exception($"The last block card must be a refused removal: {guardRemoval.Total:F1} ({guardRemoval.Reason}).");
        if (negativeRemoval.Total >= 0)
            throw new Exception($"This fixture needs an unprotected negative removal target, got {negativeRemoval.Total:F1} ({negativeRemoval.Reason}).");
        var preferUnprotected = BotBrain.SelectCards(bot, new CardModel[] { lastBlock, strongTarget }, 1, 1, "FromDeckForRemoval");
        if (preferUnprotected.Count != 1 || !ReferenceEquals(preferUnprotected[0], strongTarget))
            throw new Exception($"An unprotected candidate must win over a protected card even when its score is negative, got "
                + $"{preferUnprotected.FirstOrDefault()?.Id.Entry ?? "(none)"}.");
        Console.WriteLine("PASS: removal prefers an unprotected negative target over a protected card.");

        // Two removals must re-read the deck after each pick. With two Defends
        // the first may go, but the second becomes the last block card and must
        // be left alone while unprotected alternatives remain. The old code
        // scores the whole list once and takes the top two, which removes both.
        Clear();
        Give<DefendIronclad>(2);
        Give<StrikeIronclad>(12);
        var twoCandidates = bot.Deck.Cards.ToList();
        var twoRemovals = BotBrain.SelectCards(bot, twoCandidates, 2, 2, "FromDeckForRemoval");
        if (twoRemovals.Count != 2)
            throw new Exception($"Two removals must return two cards, got {twoRemovals.Count}.");
        if (twoRemovals.Count(card => card.Id.Entry == "DEFEND_IRONCLAD") > 1)
            throw new Exception("Two removals must not take the deck's last block card while other options exist.");
        if (ReferenceEquals(twoRemovals[0], twoRemovals[1]))
            throw new Exception("Two removals must not return the same card twice.");
        Console.WriteLine($"PASS: two removals keep a block card back ({string.Join(",", twoRemovals.Select(card => card.Id.Entry))}).");

        // When every legal candidate is protected the screen still demands its
        // count, so the pick fills deterministically from the protected pool and
        // logs the fallback rather than short-picking or stalling.
        Clear();
        Give<DefendIronclad>(1);
        Give<Breakthrough>(1);
        Give<PommelStrike>(1);
        Give<StrikeIronclad>(10);
        var forcedBlock = bot.Deck.Cards.First(card => card.Id.Entry == "DEFEND_IRONCLAD");
        var forcedAoe = bot.Deck.Cards.First(card => card.Id.Entry == "BREAKTHROUGH");
        var forcedDraw = bot.Deck.Cards.First(card => card.Id.Entry == "POMMEL_STRIKE");
        foreach (var protectedCard in new[] { forcedBlock, forcedAoe, forcedDraw })
            if (BuildValue.Remove(protectedCard, bot).Total != 0)
                throw new Exception($"This fixture needs {protectedCard.Id.Entry} to be a refused removal, got {BuildValue.Remove(protectedCard, bot).Total:F1}.");
        var forced = BotBrain.SelectCards(bot, new CardModel[] { forcedBlock, forcedAoe, forcedDraw }, 2, 2, "FromDeckForRemoval");
        if (forced.Count != 2)
            throw new Exception($"A forced removal screen must still return its required count, got {forced.Count}.");
        if (ReferenceEquals(forced[0], forced[1]))
            throw new Exception("A forced removal screen must not return the same instance twice.");
        Console.WriteLine($"PASS: an all-protected forced removal still returns {forced.Count} distinct cards "
            + $"({string.Join(",", forced.Select(card => card.Id.Entry))}).");

        // Transformation must judge each candidate against the full actual deck,
        // not the offered subset. Seven Stars is refused (0) when its whole
        // context is the subset, but is a payable Regent payoff once the deck's
        // Star producer is visible, so the subset ranking and the full-deck
        // ranking disagree. The old code scored the subset and transformed the
        // wrong card.
        Clear();
        Give<StrikeIronclad>(5);
        Give<Glow>(1);
        var transformStrike = bot.Deck.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        var transformStar = combat.CreateCard<SevenStars>(bot);
        bot.Deck.AddInternal(transformStar);
        var fullDeckContext = bot.Deck.Cards.ToList();
        var transformCandidates = new CardModel[] { transformStrike, transformStar };
        var starFull = BuildValue.Marginal(transformStar, bot, fullDeckContext).Total;
        var strikeFull = BuildValue.Marginal(transformStrike, bot, fullDeckContext).Total;
        var starSubset = BuildValue.Marginal(transformStar, bot, transformCandidates).Total;
        var strikeSubset = BuildValue.Marginal(transformStrike, bot, transformCandidates).Total;
        if (!(starFull > strikeFull))
            throw new Exception($"This fixture needs the full deck to rank the Star payoff above a Strike "
                + $"(star={starFull:F1}, strike={strikeFull:F1}).");
        if (!(starSubset < strikeSubset))
            throw new Exception($"This fixture needs the subset to rank the unpayable Star payoff below a Strike "
                + $"(star={starSubset:F1}, strike={strikeSubset:F1}).");
        var transformed = BotBrain.SelectCards(bot, transformCandidates, 1, 1, "FromDeckForTransformation");
        if (transformed.Count != 1 || !ReferenceEquals(transformed[0], transformStrike))
            throw new Exception($"Transformation must use the full-deck ranking and target the weakest real card, got "
                + $"{transformed.FirstOrDefault()?.Id.Entry ?? "(none)"}.");
        Console.WriteLine("PASS: transformation scores candidates against the full deck, so the subset counterexample is avoided.");

        // The valuation reads a copy of the deck and never edits the real one,
        // and the same call twice must return the same cards in the same order.
        Clear();
        Give<DefendIronclad>(2);
        Give<StrikeIronclad>(12);
        var liveBefore = bot.Deck.Cards.ToList();
        var liveFirst = BotBrain.SelectCards(bot, liveBefore, 2, 2, "FromDeckForRemoval");
        var liveSecond = BotBrain.SelectCards(bot, liveBefore, 2, 2, "FromDeckForRemoval");
        if (bot.Deck.Cards.Count != liveBefore.Count)
            throw new Exception($"The live deck must keep its size during a removal choice "
                + $"({liveBefore.Count} -> {bot.Deck.Cards.Count}).");
        for (var index = 0; index < liveBefore.Count; index++)
            if (!ReferenceEquals(bot.Deck.Cards[index], liveBefore[index]))
                throw new Exception($"The live deck must not be mutated or reordered during a removal choice (index {index}).");
        if (liveFirst.Count != liveSecond.Count)
            throw new Exception($"Repeated selection must return the same count ({liveFirst.Count} vs {liveSecond.Count}).");
        for (var index = 0; index < liveFirst.Count; index++)
            if (!ReferenceEquals(liveFirst[index], liveSecond[index]))
                throw new Exception($"Repeated selection must be deterministic (index {index}).");
        Console.WriteLine("PASS: removal never touches the live deck and repeats deterministically.");

        NewLeafScenarios.Run();
    }
}
